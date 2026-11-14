using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Xml.Linq;
using Path = System.IO.Path;

[assembly: System.Reflection.AssemblyTitle("Трио — аудиовывод")]
[assembly: System.Reflection.AssemblyProduct("Трио")]
[assembly: System.Reflection.AssemblyVersion("1.3.0.0")]

namespace Trio
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            var app = new Application();
            try { app.Run(new MixerWindow().Window); }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, "Трио — ошибка запуска", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    internal sealed class MixerWindow
    {
        private static readonly Endpoint NoOutput = new Endpoint(null, "Устройство не выбрано");
        private static readonly StringComparer IdComparer = StringComparer.OrdinalIgnoreCase;
        private static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(248, 194, 111));
        private static readonly Brush MeterOff = new SolidColorBrush(Color.FromRgb(49, 64, 71));

        public readonly Window Window;
        private readonly OutputSlot[] slots = new OutputSlot[AudioRouter.MaxOutputs];
        private readonly Rectangle[] meter = new Rectangle[18];
        private readonly Button toggle, refresh, openSound;
        private readonly TextBlock runState, help, channelName, channelHint, installCable;
        private readonly Ellipse runDot;
        private readonly Brush accent, muted;
        private readonly DispatcherTimer statusTimer, deviceTimer;
        private AudioDevices devices = new AudioDevices(new List<Endpoint>(), null);
        private Endpoint channel;
        private Endpoint setupCable;
        private AudioRouter router;
        private string deviceError;
        private bool applying, loading, enumerated, stopping, dirty, deviceChoicesPending;
        private int previousMeter;

        public MixerWindow()
        {
            using (var xaml = typeof(MixerWindow).Assembly.GetManifestResourceStream("MainWindow.xaml"))
                Window = (Window)XamlReader.Load(xaml);

            var saved = MixerSettings.Read();
            accent = (Brush)Window.Resources["Accent"];
            muted = (Brush)Window.Resources["Muted"];
            toggle = Find<Button>("Toggle");
            refresh = Find<Button>("Refresh");
            openSound = Find<Button>("OpenSound");
            help = Find<TextBlock>("Help");
            runState = Find<TextBlock>("RunState");
            runDot = Find<Ellipse>("RunDot");
            channelName = Find<TextBlock>("ChannelName");
            channelHint = Find<TextBlock>("ChannelHint");
            installCable = Find<TextBlock>("InstallCable");
            var outputPanel = Find<StackPanel>("Outputs");
            var template = (DataTemplate)Window.Resources["OutputTemplate"];
            for (int i = 0; i < slots.Length; i++)
            {
                var slot = new OutputSlot((Border)template.LoadContent(), i + 1, saved.Outputs[i], saved.Volumes[i]);
                slots[i] = slot;
                outputPanel.Children.Add(slot.Root);
                slot.Select.SelectionChanged += (sender, args) =>
                {
                    if (applying) return;
                    var selected = slot.Select.SelectedItem as Endpoint;
                    slot.Remembered = selected != null && selected.Id != null ? selected : null;
                    dirty = true;
                    UpdateStatus();
                };
                slot.Volume.ValueChanged += (sender, args) =>
                {
                    slot.UpdateVolume();
                    dirty = true;
                };
            }
            BuildMeter(Find<Grid>("SignalMeter"));
            ApplyDeviceChoices();
            refresh.Click += async (sender, args) => await RefreshDevices();
            toggle.Click += async (sender, args) => await ToggleRouting();
            openSound.Click += (sender, args) => OpenSoundSettings();
            Find<Hyperlink>("CableLink").RequestNavigate += (sender, args) =>
            {
                OpenLink(args.Uri.AbsoluteUri);
                args.Handled = true;
            };
            statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            statusTimer.Tick += (sender, args) => UpdateStatus();
            deviceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            deviceTimer.Tick += async (sender, args) =>
            {
                if (!IsChoosingDevice()) await RefreshDevices();
            };
            Window.SourceInitialized += (sender, args) => ApplyDarkTitleBar();
            Window.Loaded += async (sender, args) =>
            {
                statusTimer.Start();
                deviceTimer.Start();
                await RefreshDevices();
            };
            Window.Closed += (sender, args) =>
            {
                statusTimer.Stop();
                deviceTimer.Stop();
                if (router != null) router.RequestStop();
                if (dirty) MixerSettings.Save(slots.Select(slot => slot.Remembered).ToArray(),
                    slots.Select(slot => (int)slot.Volume.Value).ToArray());
            };
            UpdateStatus();
        }

        private T Find<T>(string name) where T : class { return (T)Window.FindName(name); }

        private void OpenSoundSettings()
        {
            if (channel == null && setupCable != null)
                OpenLink("ms-settings:sound-properties?endpointId=" + Uri.EscapeDataString(setupCable.Id));
            else
                OpenLink("ms-settings:sound");
        }

        private void OpenLink(string uri)
        {
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
            catch (System.ComponentModel.Win32Exception error) { MessageBox.Show(Window, error.Message, "Трио"); }
        }

        private void BuildMeter(Grid grid)
        {
            AutomationProperties.SetName(grid, "Уровень входного сигнала");
            for (int i = 0; i < meter.Length; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                meter[i] = new Rectangle { Fill = MeterOff, RadiusX = 2, RadiusY = 2, Margin = new Thickness(0, 0, 3, 0) };
                Grid.SetColumn(meter[i], i);
                grid.Children.Add(meter[i]);
            }
        }

        private async Task RefreshDevices()
        {
            if (loading) return;
            loading = true;
            refresh.IsEnabled = false;
            try
            {
                var found = await Task.Run(() => CoreAudio.GetDevices());
                deviceError = null;
                if (devices.Outputs.Count != found.Outputs.Count || devices.Outputs.Where((item, i) =>
                    item.Id != found.Outputs[i].Id || item.Name != found.Outputs[i].Name ||
                    item.Channel != found.Outputs[i].Channel).Any())
                    deviceChoicesPending = true;
                devices = found;
                if (deviceChoicesPending && !IsChoosingDevice()) ApplyDeviceChoices();
            }
            catch (Exception error) { deviceError = error.Message; }
            finally
            {
                loading = false;
                enumerated = true;
                refresh.IsEnabled = true;
                UpdateStatus();
            }
        }

        private bool IsChoosingDevice()
        {
            return slots.Any(slot => slot.Select.IsDropDownOpen);
        }

        private void ApplyDeviceChoices()
        {
            applying = true;
            deviceChoicesPending = false;
            try
            {
                if (router != null && channel != null)
                    channel = devices.Outputs.Find(device => IdComparer.Equals(device.Id, channel.Id)) ?? channel.Unavailable();
                else
                    channel = devices.FindTrioChannel();
                setupCable = channel == null ? devices.FindSetupCable() : null;
                var choices = new List<Endpoint> { NoOutput };
                var reserved = channel ?? setupCable;
                choices.AddRange(devices.Outputs.Where(device => reserved == null || !IdComparer.Equals(device.Id, reserved.Id)));
                foreach (var slot in slots)
                    SetChoices(slot, choices, reserved);
            }
            finally { applying = false; }
        }

        private void SetChoices(OutputSlot slot, List<Endpoint> choices, Endpoint reserved)
        {
            var remembered = slot.Remembered;
            if (remembered != null && reserved != null && IdComparer.Equals(remembered.Id, reserved.Id))
            {
                remembered = null;
                dirty = true;
            }
            var selected = remembered == null ? NoOutput : choices.Find(item => IdComparer.Equals(item.Id, remembered.Id));
            if (selected == null)
            {
                selected = remembered.Unavailable();
                choices = new List<Endpoint>(choices) { selected };
            }
            slot.Select.ItemsSource = choices;
            slot.Select.SelectedItem = selected;
            slot.Remembered = selected.Id == null ? null : selected;
        }

        private string StartProblem()
        {
            if (!enumerated) return "Поиск аудиоустройств Windows…";
            if (deviceError != null) return "Не удалось получить список аудиоустройств Windows.";
            if (channel == null) return setupCable == null ? "Для запуска нужен драйвер VB-CABLE." : "Переименуйте виртуальный выход в Trio.";
            if (!channel.IsAvailable) return "Виртуальный канал недоступен.";
            var selected = slots.Where(slot => slot.Remembered != null).Select(slot => slot.Remembered).ToArray();
            if (!selected.Any(device => device.IsAvailable)) return "Выберите хотя бы одно подключённое устройство вывода.";
            if (selected.Select(device => device.Id).Distinct(IdComparer).Count() != selected.Length)
                return "Выберите разные устройства для каждого выхода.";
            return null;
        }

        private async Task ToggleRouting()
        {
            if (stopping) return;
            if (router != null)
            {
                var previous = router;
                previous.RequestStop();
                router = null;
                stopping = true;
                UpdateStatus();
                await Task.Run(() => previous.WaitForStop());
                foreach (var slot in slots) slot.Route = null;
                stopping = false;
                ApplyDeviceChoices();
                UpdateStatus();
                return;
            }
            if (StartProblem() != null) return;

            var routes = new List<OutputRoute>();
            foreach (var slot in slots.Where(slot => slot.Remembered != null))
            {
                slot.Route = new OutputRoute(slot.Remembered.Id, (float)slot.Volume.Value / 100);
                routes.Add(slot.Route);
            }
            router = new AudioRouter(channel.Id, routes.ToArray());
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            bool running = router != null;
            int ready = 0, total = 0;
            foreach (var slot in slots)
            {
                slot.Select.IsEnabled = !running && !stopping;
                slot.Volume.IsEnabled = slot.Remembered != null;
                var state = running && slot.Route != null ? slot.Route.Status : null;
                string label = "";
                if (state != null)
                {
                    total++;
                    if (state.State == StreamState.Ready)
                    {
                        label = "Подключён";
                        ready++;
                    }
                    else
                    {
                        label = state.State == StreamState.Retrying ? "Повторное подключение…" : "Подключение…";
                    }
                }
                else if (slot.Remembered != null)
                    label = slot.Remembered.IsAvailable ? "Выбран" : "Недоступен";
                slot.State.Text = label;
                slot.State.Foreground = state != null && state.State == StreamState.Ready ? accent : muted;
                slot.State.ToolTip = state == null ? null : state.Detail;
            }

            var input = running ? router.InputStatus : null;
            bool streaming = input != null && input.State == StreamState.Ready && ready > 0;
            runDot.Fill = streaming ? accent : running || stopping ? Amber : muted;
            runState.Foreground = streaming ? accent : muted;
            runState.Text = streaming ? "Вывод активен" : stopping ? "Остановка…" : running ? "Подключение…" : "Остановлено";
            bool isDefault = channel != null && channel.IsAvailable && IdComparer.Equals(channel.Id, devices.DefaultOutputId);
            UpdateChannelStatus(isDefault);
            string problem = !running && !stopping ? StartProblem() : null;
            toggle.IsEnabled = !stopping && (running || problem == null);
            toggle.Content = running ? "Остановить вывод" : "Запустить вывод";
            if (input != null && input.State == StreamState.Retrying)
                help.Text = "Канал недоступен. Повторное подключение…";
            else if (running)
                help.Text = String.Format("Подключено выходов {0} из {1}", ready, total);
            else
                help.Text = stopping ? "Закрытие аудиопотоков…" : problem ??
                    (isDefault ? "Чтобы услышать системный звук, запустите вывод." : "");
            help.ToolTip = input != null && input.Detail != null ? input.Detail : deviceError;

            double level = running ? router.InputLevel : 0;
            int bars = level > 0.001 ? (int)Math.Ceiling(Math.Sqrt(level) * meter.Length) : 0;
            for (int i = Math.Min(bars, previousMeter); i < Math.Max(bars, previousMeter); i++)
                meter[i].Fill = i < bars ? accent : MeterOff;
            previousMeter = bars;
        }

        private void UpdateChannelStatus(bool isDefault)
        {
            bool missing = enumerated && deviceError == null && channel == null && setupCable == null;
            installCable.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
            openSound.Visibility = channel != null || setupCable != null ? Visibility.Visible : Visibility.Collapsed;
            openSound.Content = setupCable != null ? "Переименовать" : "Звук Windows";
            var device = channel ?? setupCable;
            channelName.Text = device == null ? "Trio" : device.Name;
            channelName.ToolTip = device == null ? null : device.Name;
            if (channel != null && channel.IsAvailable)
                channelHint.Text = isDefault ? "Выход Windows по умолчанию." : "Для всего звука выберите этот выход в Windows.";
            else if (setupCable != null)
                channelHint.Text = "Откройте свойства устройства и задайте имя Trio.";
            else
                channelHint.Text = missing ? "Установите VB-CABLE и перезагрузите ПК." :
                    enumerated ? "Канал сейчас недоступен." : "Поиск виртуального канала…";
        }

        private void ApplyDarkTitleBar()
        {
            int enabled = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(Window).Handle, 20, ref enabled, sizeof(int));
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    }

    internal sealed class OutputSlot
    {
        public readonly Border Root;
        public readonly ComboBox Select;
        public readonly Slider Volume;
        public readonly TextBlock State;
        private readonly TextBlock percent;
        public Endpoint Remembered;
        public OutputRoute Route;

        public OutputSlot(Border root, int number, Endpoint remembered, int gain)
        {
            Root = root;
            Remembered = remembered;
            string name = "Выход " + number;
            ((TextBlock)root.FindName("OutputTitle")).Text = name;
            Select = (ComboBox)root.FindName("OutputSelect");
            Volume = (Slider)root.FindName("Volume");
            State = (TextBlock)root.FindName("OutputState");
            percent = (TextBlock)root.FindName("Percent");
            AutomationProperties.SetName(Select, name);
            AutomationProperties.SetName(Volume, "Громкость, выход " + number);
            Volume.Value = gain;
            UpdateVolume();
        }

        public void UpdateVolume()
        {
            percent.Text = ((int)Volume.Value).ToString(CultureInfo.InvariantCulture) + "%";
            if (Route != null) Route.Gain = (float)Volume.Value / 100;
        }
    }

    internal sealed class MixerSettings
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Trio", "settings.xml");

        public readonly Endpoint[] Outputs = new Endpoint[AudioRouter.MaxOutputs];
        public readonly int[] Volumes = Enumerable.Repeat(100, AudioRouter.MaxOutputs).ToArray();

        public static MixerSettings Read()
        {
            var result = new MixerSettings();
            try
            {
                if (!File.Exists(SettingsPath)) return result;
                var root = XElement.Load(SettingsPath);
                int i = 0;
                foreach (var output in root.Elements("output").Take(result.Outputs.Length))
                {
                    result.Outputs[i] = ReadEndpoint(output);
                    int gain;
                    if (Int32.TryParse((string)output.Attribute("volume"), out gain))
                        result.Volumes[i] = Math.Max(0, Math.Min(100, gain));
                    i++;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Xml.XmlException) { }
            return result;
        }

        public static void Save(Endpoint[] outputs, int[] volumes)
        {
            string temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath));
                var root = new XElement("trio");
                for (int i = 0; i < outputs.Length; i++)
                    root.Add(WriteEndpoint("output", outputs[i], new XAttribute("volume", volumes[i])));
                root.Save(temporary);
                if (File.Exists(SettingsPath)) File.Replace(temporary, SettingsPath, null);
                else File.Move(temporary, SettingsPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static XElement WriteEndpoint(string tag, Endpoint endpoint, XAttribute extra)
        {
            var element = new XElement(tag, extra);
            if (endpoint != null)
                element.Add(new XAttribute("id", endpoint.Id), new XAttribute("name", endpoint.Name));
            return element;
        }

        private static Endpoint ReadEndpoint(XElement element)
        {
            if (element == null) return null;
            var id = (string)element.Attribute("id");
            if (String.IsNullOrEmpty(id)) return null;
            var name = (string)element.Attribute("name");
            return new Endpoint(id, name ?? id, isAvailable: false);
        }
    }
}
