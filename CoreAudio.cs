using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Trio
{
    internal enum VirtualChannel { None, Cable, Trio }

    internal sealed class Endpoint
    {
        public readonly string Id;
        public readonly string Name;
        public readonly VirtualChannel Channel;
        public readonly bool IsAvailable;

        public Endpoint(string id, string name, VirtualChannel channel = VirtualChannel.None, bool isAvailable = true)
        {
            Id = id;
            Name = name;
            Channel = channel;
            IsAvailable = isAvailable;
        }

        public Endpoint Unavailable() { return new Endpoint(Id, Name, Channel, false); }

        public override string ToString()
        {
            return IsAvailable ? Name : Name + " (недоступно)";
        }
    }

    internal sealed class AudioDevices
    {
        public readonly List<Endpoint> Outputs;
        public readonly string DefaultOutputId;

        public AudioDevices(List<Endpoint> outputs, string defaultOutputId)
        {
            Outputs = outputs;
            DefaultOutputId = defaultOutputId;
        }

        public Endpoint FindTrioChannel()
        {
            Endpoint first = null;
            foreach (var output in Outputs)
            {
                if (output.Channel != VirtualChannel.Trio) continue;
                if (String.Equals(output.Id, DefaultOutputId, StringComparison.OrdinalIgnoreCase)) return output;
                if (first == null) first = output;
            }
            return first;
        }

        public Endpoint FindSetupCable()
        {
            return Outputs.Find(output => output.Channel == VirtualChannel.Cable);
        }
    }

    internal static class CoreAudio
    {
        private static readonly Guid EnumeratorClass = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly PropertyKey FriendlyName = new PropertyKey("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);
        private static readonly PropertyKey Description = new PropertyKey("A45C254E-DF1C-4EFD-8020-67D146A850E0", 2);
        private static readonly PropertyKey InterfaceName = new PropertyKey("026E516E-B814-414B-83CD-856D6FEF4822", 2);

        public static AudioDevices GetDevices()
        {
            var result = new List<Endpoint>();
            var enumerator = CreateEnumerator();
            IMMDeviceCollection collection = null;
            string defaultOutputId;
            try
            {
                Check(enumerator.EnumAudioEndpoints(0, 1, out collection)); // eRender, DEVICE_STATE_ACTIVE
                uint count;
                Check(collection.GetCount(out count));
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice device;
                    Check(collection.Item(i, out device));
                    try
                    {
                        string id;
                        Check(device.GetId(out id));
                        IPropertyStore properties;
                        Check(device.OpenPropertyStore(0, out properties));
                        try
                        {
                            string name = ReadString(properties, FriendlyName) ?? id;
                            string description = ReadString(properties, Description) ?? "";
                            string driver = ReadString(properties, InterfaceName) ?? "";
                            result.Add(new Endpoint(id, name, IdentifyChannel(name, description, driver)));
                        }
                        finally { Release(properties); }
                    }
                    catch (COMException) { } // An endpoint can disappear during enumeration.
                    finally { Release(device); }
                }
                defaultOutputId = ReadDefaultOutputId(enumerator);
            }
            finally
            {
                Release(collection);
                Release(enumerator);
            }
            result.Sort((a, b) =>
            {
                int nameOrder = StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
                return nameOrder != 0 ? nameOrder : StringComparer.OrdinalIgnoreCase.Compare(a.Id, b.Id);
            });
            return new AudioDevices(result, defaultOutputId);
        }

        private static VirtualChannel IdentifyChannel(string name, string description, string driver)
        {
            const string cableDriver = "VB-Audio Virtual Cable";
            if (!driver.EndsWith(cableDriver, StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(cableDriver + ")", StringComparison.OrdinalIgnoreCase))
                return VirtualChannel.None;

            return HasEndpointName(name, "Trio") || HasEndpointName(name, "Трио") ||
                HasEndpointName(description, "Trio") || HasEndpointName(description, "Трио")
                ? VirtualChannel.Trio : VirtualChannel.Cable;
        }

        private static bool HasEndpointName(string label, string name)
        {
            return String.Equals(label, name, StringComparison.OrdinalIgnoreCase) ||
                label.StartsWith(name + " (", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadDefaultOutputId(IMMDeviceEnumerator enumerator)
        {
            IMMDevice device;
            int hr = enumerator.GetDefaultAudioEndpoint(0, 1, out device); // eRender, eMultimedia
            if (hr == unchecked((int)0x80070490)) return null; // E_NOTFOUND
            Check(hr);
            try
            {
                string id;
                Check(device.GetId(out id));
                return id;
            }
            finally { Release(device); }
        }

        public static IAudioClient Activate(string id)
        {
            var enumerator = CreateEnumerator();
            IMMDevice device = null;
            try
            {
                Check(enumerator.GetDevice(id, out device));
                Guid iid = typeof(IAudioClient).GUID;
                object client;
                Check(device.Activate(ref iid, 23, IntPtr.Zero, out client)); // CLSCTX_ALL
                return (IAudioClient)client;
            }
            finally
            {
                Release(device);
                Release(enumerator);
            }
        }

        public static void Check(int hr)
        {
            if (hr < 0) Marshal.ThrowExceptionForHR(hr, new IntPtr(-1));
        }

        public static void Release(object instance)
        {
            if (instance != null) Marshal.ReleaseComObject(instance);
        }

        private static IMMDeviceEnumerator CreateEnumerator()
        {
            return (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClass, true));
        }

        private static string ReadString(IPropertyStore store, PropertyKey key)
        {
            PropVariant value;
            Check(store.GetValue(ref key, out value));
            try
            {
                return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null; // VT_LPWSTR
            }
            finally { PropVariantClear(ref value); }
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant value);
    }

    internal sealed class AudioStream : IDisposable
    {
        private const uint EventCallback = 0x00040000;
        private const uint Loopback = 0x00020000;
        private const uint NoPersist = 0x00080000;
        private const uint ConvertPcm = 0x80000000;
        private const uint HighQualityConversion = 0x08000000;
        public const uint Silent = 0x2;

        private readonly IAudioClient client;
        private bool started;
        public readonly AutoResetEvent Ready = new AutoResetEvent(false);
        public readonly CaptureFormat InputFormat;
        public readonly int BufferFrames;

        private unsafe AudioStream(string id, int renderRate)
        {
            try
            {
                client = CoreAudio.Activate(id);
                if (renderRate == 0)
                {
                    IntPtr mix;
                    CoreAudio.Check(client.GetMixFormat(out mix));
                    try
                    {
                        InputFormat = new CaptureFormat(mix);
                        CoreAudio.Check(client.Initialize(0, EventCallback | Loopback, 0, 0, mix, IntPtr.Zero));
                    }
                    finally { Marshal.FreeCoTaskMem(mix); }
                }
                else
                {
                    var format = new WaveFormat
                    {
                        Tag = 3, // IEEE float
                        Channels = 2,
                        Rate = renderRate,
                        BytesPerSecond = renderRate * 8,
                        BlockAlign = 8,
                        Bits = 32
                    };
                    CoreAudio.Check(client.Initialize(0, EventCallback | NoPersist | ConvertPcm | HighQualityConversion,
                        0, 0, (IntPtr)(&format), IntPtr.Zero));
                }
                CoreAudio.Check(client.SetEventHandle(Ready.SafeWaitHandle.DangerousGetHandle()));
                uint size;
                CoreAudio.Check(client.GetBufferSize(out size));
                BufferFrames = checked((int)size);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public static AudioStream Capture(string id) { return new AudioStream(id, 0); }
        public static AudioStream Render(string id, int sampleRate) { return new AudioStream(id, sampleRate); }

        public T Service<T>() where T : class
        {
            Guid iid = typeof(T).GUID;
            object service;
            CoreAudio.Check(client.GetService(ref iid, out service));
            return (T)service;
        }

        public void Start()
        {
            CoreAudio.Check(client.Start());
            started = true;
        }

        public int AvailableFrames()
        {
            uint padding;
            CoreAudio.Check(client.GetCurrentPadding(out padding));
            return BufferFrames - checked((int)padding);
        }

        public void Dispose()
        {
            if (started) client.Stop();
            CoreAudio.Release(client);
            Ready.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormat
    {
        public short Tag, Channels;
        public int Rate, BytesPerSecond;
        public short BlockAlign, Bits, ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid Format;
        public uint Id;
        public PropertyKey(string format, uint id) { Format = new Guid(format); Id = id; }
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
        [FieldOffset(8)] private PropVariantArray array;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropVariantArray
    {
        public uint Count;
        public IntPtr Pointer;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object client);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint flags, long duration, long period, IntPtr format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, IntPtr position, IntPtr qpc);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }
}
