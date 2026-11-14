using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Trio
{
    internal enum StreamState { Connecting, Ready, Retrying }

    internal sealed class StreamStatus
    {
        public readonly StreamState State;
        public readonly string Detail;

        public StreamStatus(StreamState state, string detail = null)
        {
            State = state;
            Detail = detail;
        }
    }

    internal sealed class OutputRoute
    {
        public readonly string Id;
        public readonly StereoBuffer Buffer = new StereoBuffer();
        public volatile StreamStatus Status = new StreamStatus(StreamState.Connecting);
        public volatile float Gain;

        public OutputRoute(string id, float gain)
        {
            Id = id;
            Gain = gain;
        }
    }

    internal sealed class AudioRouter
    {
        public const int MaxOutputs = 5;

        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly string sourceId;
        private readonly OutputRoute[] routes;
        private readonly List<Thread> workers = new List<Thread>();
        private volatile int sampleRate;
        private volatile float peak;
        private long lastPacket;

        public volatile StreamStatus InputStatus = new StreamStatus(StreamState.Connecting);

        public float InputLevel
        {
            get
            {
                return Stopwatch.GetTimestamp() - Interlocked.Read(ref lastPacket) < Stopwatch.Frequency / 4 ? peak : 0;
            }
        }

        public AudioRouter(string sourceId, OutputRoute[] routes)
        {
            if (String.IsNullOrEmpty(sourceId) || routes.Length < 1 || routes.Length > MaxOutputs ||
                routes.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != routes.Length ||
                routes.Any(r => String.Equals(r.Id, sourceId, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Выберите источник и до " + MaxOutputs + " разных выходов.");

            this.sourceId = sourceId;
            this.routes = routes;
            StartWorker(CaptureLoop, "Trio capture");
            foreach (var route in routes)
                StartWorker(() => RenderLoop(route), "Trio output");
        }

        public void RequestStop() { stop.Set(); }

        public void WaitForStop()
        {
            foreach (var worker in workers) worker.Join();
            stop.Dispose();
        }

        private void StartWorker(ThreadStart action, string name)
        {
            var worker = new Thread(action) { Name = name, IsBackground = true };
            worker.SetApartmentState(ApartmentState.MTA);
            workers.Add(worker);
            worker.Start();
        }

        private void CaptureLoop()
        {
            while (!stop.WaitOne(0))
            {
                try { Capture(); }
                catch (Exception error)
                {
                    foreach (var route in routes) route.Buffer.Clear();
                    peak = 0;
                    InputStatus = RetryStatus(error);
                    stop.WaitOne(1500);
                }
            }
        }

        private unsafe void Capture()
        {
            using (var stream = AudioStream.Capture(sourceId))
            {
                var capture = stream.Service<IAudioCaptureClient>();
                try
                {
                    var format = stream.InputFormat;
                    foreach (var route in routes) route.Buffer.Configure(format.Rate);
                    sampleRate = format.Rate;
                    var stereo = new float[stream.BufferFrames * 2];
                    stream.Start();
                    InputStatus = new StreamStatus(StreamState.Ready);
                    WaitHandle[] events = { stop, stream.Ready };
                    while (WaitHandle.WaitAny(events, 200) != 0)
                    {
                        uint pending;
                        CoreAudio.Check(capture.GetNextPacketSize(out pending));
                        while (pending > 0 && !stop.WaitOne(0))
                        {
                            IntPtr data;
                            uint frames, flags;
                            CoreAudio.Check(capture.GetBuffer(out data, out frames, out flags, IntPtr.Zero, IntPtr.Zero));
                            if (frames == 0) break;
                            int count = checked((int)frames);
                            float level;
                            try
                            {
                                if (stereo.Length < count * 2) stereo = new float[count * 2];
                                if ((flags & AudioStream.Silent) != 0)
                                {
                                    Array.Clear(stereo, 0, count * 2);
                                    level = 0;
                                }
                                else
                                {
                                    level = format.Decode((byte*)data, count, stereo);
                                }
                            }
                            finally { CoreAudio.Check(capture.ReleaseBuffer(frames)); }

                            foreach (var route in routes) route.Buffer.Write(stereo, count);
                            peak = Math.Max(level, peak * 0.9f);
                            Interlocked.Exchange(ref lastPacket, Stopwatch.GetTimestamp());
                            CoreAudio.Check(capture.GetNextPacketSize(out pending));
                        }
                    }
                }
                finally { CoreAudio.Release(capture); }
            }
        }

        private void RenderLoop(OutputRoute route)
        {
            while (!stop.WaitOne(0))
            {
                int rate = sampleRate;
                if (rate == 0)
                {
                    stop.WaitOne(30);
                    continue;
                }
                try { Render(route, rate); }
                catch (Exception error)
                {
                    route.Status = RetryStatus(error);
                    stop.WaitOne(1500);
                }
            }
        }

        private unsafe void Render(OutputRoute route, int rate)
        {
            using (var stream = AudioStream.Render(route.Id, rate))
            {
                var render = stream.Service<IAudioRenderClient>();
                try
                {
                    route.Buffer.Clear();
                    IntPtr data;
                    CoreAudio.Check(render.GetBuffer((uint)stream.BufferFrames, out data));
                    CoreAudio.Check(render.ReleaseBuffer((uint)stream.BufferFrames, AudioStream.Silent));
                    stream.Start();
                    route.Status = new StreamStatus(StreamState.Ready);
                    WaitHandle[] events = { stop, stream.Ready };
                    float gain = 0;
                    while (WaitHandle.WaitAny(events, 200) != 0 && sampleRate == rate)
                    {
                        int count = stream.AvailableFrames();
                        if (count <= 0) continue;
                        CoreAudio.Check(render.GetBuffer((uint)count, out data));
                        uint flags = AudioStream.Silent;
                        try
                        {
                            if (route.Buffer.Read((float*)data, count, rate, route.Gain, ref gain))
                                flags = 0;
                        }
                        finally { CoreAudio.Check(render.ReleaseBuffer((uint)count, flags)); }
                    }
                }
                finally { CoreAudio.Release(render); }
            }
        }

        private static StreamStatus RetryStatus(Exception error)
        {
            return new StreamStatus(StreamState.Retrying,
                String.Format("0x{0:X8} — {1}", error.HResult, error.Message));
        }
    }

    internal sealed class StereoBuffer
    {
        private readonly object gate = new object();
        private float[] samples = new float[0];
        private int sampleRate, capacity, readAt, available;
        private bool buffered;
        private double fraction, correction;

        public void Configure(int rate)
        {
            lock (gate)
            {
                if (sampleRate != rate)
                {
                    sampleRate = rate;
                    capacity = rate / 4;
                    samples = new float[capacity * 2];
                }
                Reset();
            }
        }

        public void Clear()
        {
            lock (gate) Reset();
        }

        private void Reset()
        {
            readAt = 0;
            available = 0;
            buffered = false;
            fraction = 0;
            correction = 0;
        }

        public void Write(float[] source, int frames)
        {
            lock (gate)
            {
                int offset = Math.Max(0, frames - capacity);
                frames -= offset;
                int overflow = Math.Max(0, available + frames - capacity);
                Advance(overflow);
                int writeAt = (readAt + available) % capacity;
                int first = Math.Min(frames, capacity - writeAt);
                Array.Copy(source, offset * 2, samples, writeAt * 2, first * 2);
                Array.Copy(source, (offset + first) * 2, samples, 0, (frames - first) * 2);
                available += frames;
            }
        }

        public unsafe bool Read(float* destination, int frames, int rate, float targetGain, ref float gain)
        {
            lock (gate)
            {
                if (rate != sampleRate) return false;
                int target = Math.Min(capacity - 2, Math.Max(rate * 40 / 1000, frames + 2));
                if (!buffered)
                {
                    if (available < target) return false;
                    buffered = true;
                }
                if (available > Math.Max(rate * 140 / 1000, target * 2))
                {
                    Advance(available - target);
                    fraction = 0;
                }

                // Each endpoint has its own clock. Adjust slowly to keep its queue near 40 ms.
                double requested = (available - target - frames / 2.0) / (rate * 1.5);
                correction += (Math.Max(-0.004, Math.Min(0.004, requested)) - correction) * 0.035;
                float gainStep = 1f / Math.Max(1, rate / 180);
                int written = 0;
                while (written < frames && available > 2)
                {
                    int current = readAt * 2;
                    int next = ((readAt + 1) % capacity) * 2;
                    float blend = (float)fraction;
                    gain += Math.Max(-gainStep, Math.Min(gainStep, targetGain - gain));
                    destination[written * 2] = (samples[current] + (samples[next] - samples[current]) * blend) * gain;
                    destination[written * 2 + 1] = (samples[current + 1] + (samples[next + 1] - samples[current + 1]) * blend) * gain;
                    written++;
                    fraction += 1 + correction;
                    int consumed = (int)fraction;
                    fraction -= consumed;
                    Advance(consumed);
                }
                if (written < frames)
                {
                    for (int i = written * 2; i < frames * 2; i++) destination[i] = 0;
                    Reset();
                    gain = 0;
                }
                return written > 0;
            }
        }

        private void Advance(int frames)
        {
            readAt = (readAt + frames) % capacity;
            available -= frames;
        }
    }

    internal sealed class CaptureFormat
    {
        private static readonly Guid FloatFormat = new Guid("00000003-0000-0010-8000-00AA00389B71");
        private static readonly Guid PcmFormat = new Guid("00000001-0000-0010-8000-00AA00389B71");
        private readonly bool floating;
        private readonly int channels, bytes, block;
        private readonly float[] left, right;
        public readonly int Rate;

        public CaptureFormat(IntPtr address)
        {
            var format = (WaveFormat)Marshal.PtrToStructure(address, typeof(WaveFormat));
            Rate = format.Rate;
            channels = format.Channels;
            bytes = format.Bits / 8;
            block = format.BlockAlign;
            bool extensible = (ushort)format.Tag == 0xfffe && format.ExtraSize >= 22;
            Guid subtype = extensible ? (Guid)Marshal.PtrToStructure(IntPtr.Add(address, 24), typeof(Guid)) : Guid.Empty;
            floating = format.Tag == 3 || subtype == FloatFormat;
            bool pcm = format.Tag == 1 || subtype == PcmFormat;
            if (!(floating && bytes == 4 || pcm && bytes >= 1 && bytes <= 4) ||
                channels < 1 || block < bytes * channels || Rate < 8000 || Rate > 384000)
                throw new NotSupportedException("Формат входного устройства не поддерживается.");

            left = new float[channels];
            right = new float[channels];
            if (channels == 1)
            {
                left[0] = right[0] = 1;
                return;
            }
            uint mask = extensible ? (uint)Marshal.ReadInt32(address, 20) : 0;
            for (int channel = 0; channel < channels; channel++)
            {
                uint speaker = mask & (0u - mask);
                mask &= ~speaker;
                switch (speaker)
                {
                    case 0x1: left[channel] = 1; break;
                    case 0x2: right[channel] = 1; break;
                    case 0x8: left[channel] = right[channel] = 0.35f; break; // LFE
                    case 0x10: case 0x40: case 0x200: case 0x1000: case 0x8000: left[channel] = 0.707f; break;
                    case 0x20: case 0x80: case 0x400: case 0x4000: case 0x20000: right[channel] = 0.707f; break;
                    case 0:
                        if (channel == 0) left[channel] = 1;
                        else if (channel == 1) right[channel] = 1;
                        else left[channel] = right[channel] = 0.5f;
                        break;
                    default: left[channel] = right[channel] = 0.707f; break;
                }
            }
        }

        public unsafe float Decode(byte* data, int frames, float[] stereo)
        {
            float peak = 0;
            for (int frame = 0; frame < frames; frame++, data += block)
            {
                float l = 0, r = 0;
                for (int channel = 0; channel < channels; channel++)
                {
                    float value = ReadSample(data + channel * bytes);
                    l += value * left[channel];
                    r += value * right[channel];
                }
                stereo[frame * 2] = Math.Max(-1f, Math.Min(1f, l));
                stereo[frame * 2 + 1] = Math.Max(-1f, Math.Min(1f, r));
                peak = Math.Max(peak, Math.Max(Math.Abs(l), Math.Abs(r)));
            }
            return Math.Min(1f, peak);
        }

        private unsafe float ReadSample(byte* data)
        {
            if (floating)
            {
                float value = *(float*)data;
                return Single.IsNaN(value) || Single.IsInfinity(value) ? 0f : value;
            }
            switch (bytes)
            {
                case 1: return (data[0] - 128) / 128f;
                case 2: return *(short*)data / 32768f;
                case 3: return (data[0] | data[1] << 8 | (sbyte)data[2] << 16) / 8388608f;
                default: return *(int*)data / 2147483648f;
            }
        }
    }
}
