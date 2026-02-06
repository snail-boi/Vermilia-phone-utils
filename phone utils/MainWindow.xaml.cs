using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.DirectoryServices.ActiveDirectory;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Threading.Tasks;
using System.Net.NetworkInformation;
using System.Net;
using System.Threading;
using System.Collections.Generic;
using Windows.ApplicationModel.Calls;

namespace phone_utils
{
    public partial class MainWindow : Window
    {
        #region Fields
        public AppConfig Config;
        public static string ADB_PATH;
        private string wifiDevice;
        public string currentDevice;
        private DispatcherTimer connectionCheckTimer;
        private HashSet<int> shownBatteryWarnings = new HashSet<int>();
        private bool wasCharging = false;
        public bool devmode;
        public bool MusicPresence;
        public static bool debugmode;
        private bool portlost = false;

        private MediaController mediaController;

        private int lastBatteryLevel = 100;
        private bool _isBatteryWarningShown = false;

        private readonly HashSet<string> _scrcpyStartedForDevice = new HashSet<string>();
        private bool _wasUsbConnectedForSelectedDevice = false;

        // Track consecutive failed wifi connect attempts per configured Wi‑Fi target
        private readonly Dictionary<string, int> _wifiConnectFailures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private const int WifiFailThreshold = 3;
        private bool stopSpammingARP = false;

        // Tray icon manager (separate class in its own file)
        private TrayIconManager _trayIconManager;
        #endregion

        #region Initialization
        public MainWindow()
        {
            InitializeComponent();

            // Start updater (fire-and-forget)
            _ = Updater.CheckForUpdateAsync(App.CurrentVersion);

            // Initialize media controller (handles MediaPlayer/SMTC)
            mediaController = new MediaController(Dispatcher, () => currentDevice, async () => await UpdateCurrentSongAsync());
            mediaController.Initialize();

            LoadConfiguration();

            // Show info popup if no devices are saved
            if (Config.SavedDevices == null || Config.SavedDevices.Count == 0)
            {
                string message =
                    "No devices are saved in your Configuration yet.\n\n" +
                    "Please add a device in the settings to use Phone Utils.\n\n" +
                    "If this is your first time using this app, ensure that USB debugging is enabled on your phone:\n" +
                    "1. Open your phone's Settings app.\n" +
                    "2. Enable Developer Mode (usually found under 'About Phone' → tap 'Build Number' several times).\n" +
                    "3. Go to Developer Options and turn on 'USB Debugging'.\n" +
                    "4. (Optional) Enable 'Wireless Debugging' to use this app over Wi-Fi.";

                string title = "No Saved Devices Found";

                MessageBox.Show(
                    message,
                    title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
            }

            // Move device detection to the Loaded event so we can await it properly
            this.Loaded += MainWindow_Loaded;

            connectionCheckTimer = new DispatcherTimer
            {
                // default will be overridden by ApplyUpdateIntervalMode
                Interval = TimeSpan.FromSeconds(10)
            };
            connectionCheckTimer.Tick += ConnectionCheckTimer_Tick;
            ApplyUpdateIntervalMode();
            if (connectionCheckTimer.Interval.TotalSeconds > 0)
                connectionCheckTimer.Start();

            // Note: Autorun start logic moved to TryAutorunStartAsync, which runs after initial device detection

            // Initialize tray icon manager
            try
            {
                _trayIconManager = new TrayIconManager(this);
            }
            catch { }
        }

        private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
        {
            // Await initial device detection to ensure any exceptions are observed
            try
            {
                await DetectDeviceAsync();

                // After detection, attempt autorun start — prefer USB first, then Wi‑Fi
                await TryAutorunStartAsync();
            }
            catch (Exception ex)
            {
                Debugger.show($"Error during initial device detection: {ex.Message}");
            }
        }
        #endregion

        // Public helper used by the tray icon to start scrcpy for the current device using config arguments
        public void StartScrcpyFromTray()
        {
            try
            {
                if (string.IsNullOrEmpty(currentDevice))
                {
                    MessageBox.Show("No device connected!", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!File.Exists(Config.Paths.Scrcpy))
                {
                    MessageBox.Show("scrcpy.exe not found!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Prefer explicit autorun arguments if present, fall back to AutoUsbStart, otherwise no extra args
                string cfgArgs = Config?.AutorunStart?.Arguments ?? Config?.AutoUsbStart?.Arguments ?? string.Empty;
                var args = $"-s {currentDevice} {cfgArgs}".Trim();

                StartScrcpyProcessForDevice(currentDevice, args);
            }
            catch (Exception ex)
            {
                Debugger.show("StartScrcpyFromTray exception: " + ex.Message);
            }
        }

        #region Autorun
        // Attempt to autorun scrcpy: prefer USB then Wi‑Fi
        private async Task TryAutorunStartAsync()
        {
            try
            {
                if (Config?.AutorunStart == null || !Config.AutorunStart.Enabled)
                {
                    Debugger.show("AutorunStart not enabled");
                    return;
                }

                if (!File.Exists(Config.Paths.Scrcpy))
                {
                    Debugger.show("AutorunStart: scrcpy.exe not found at path: " + Config.Paths.Scrcpy);
                    return;
                }

                // Get current adb device list
                var devices = await AdbHelper.RunAdbCaptureAsync("devices");
                Debugger.show($"AutorunStart - adb devices output:\n{devices}");
                var deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                // Try USB first if configured
                if (!string.IsNullOrEmpty(Config.SelectedDeviceUSB))
                {
                    bool usbConnected = deviceList.Any(l => l.StartsWith(Config.SelectedDeviceUSB) && l.EndsWith("device"));
                    if (usbConnected)
                    {
                        var args = $"-s {Config.SelectedDeviceUSB} {Config.AutorunStart.Arguments}".Trim();
                        StartScrcpyProcessForDevice(Config.SelectedDeviceUSB, args);
                        Debugger.show("AutorunStart started scrcpy for USB device: " + Config.SelectedDeviceUSB);
                        return;
                    }
                    else
                    {
                        Debugger.show("AutorunStart: USB device not connected: " + Config.SelectedDeviceUSB);
                    }
                }

                // If USB did not work, try Wi‑Fi (if configured)
                if (!string.IsNullOrEmpty(Config.SelectedDeviceWiFi) && Config.SelectedDeviceWiFi != "None")
                {
                    // Attempt to connect to the Wi‑Fi device (may be a no‑op if already connected)
                    Debugger.show("AutorunStart: attempting Wi‑Fi connect to " + Config.SelectedDeviceWiFi);
                    var connectResult = await AdbHelper.RunAdbCaptureAsync($"connect {Config.SelectedDeviceWiFi}");
                    Debugger.show("AutorunStart - adb connect result: " + connectResult);

                    // Re-query devices to see if Wi‑Fi device is present
                    devices = await AdbHelper.RunAdbCaptureAsync("devices");
                    deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    bool wifiConnected = deviceList.Any(l => l.StartsWith(Config.SelectedDeviceWiFi) && l.EndsWith("device"));

                    if (wifiConnected)
                    {
                        var args = $"-s {Config.SelectedDeviceWiFi} {Config.AutorunStart.Arguments}".Trim();
                        StartScrcpyProcessForDevice(Config.SelectedDeviceWiFi, args);
                        Debugger.show("AutorunStart started scrcpy for Wi‑Fi device: " + Config.SelectedDeviceWiFi);
                        return;
                    }
                    else
                    {
                        Debugger.show("AutorunStart: Wi‑Fi device not connected: " + Config.SelectedDeviceWiFi);
                    }
                }

                Debugger.show("AutorunStart: no available device to start scrcpy");
            }
            catch (Exception ex)
            {
                Debugger.show("AutorunStart exception: " + ex.Message);
            }
        }
        #endregion

        #region Configuration
        private void LoadConfiguration()
        {
            Debugger.show("Loading Configuration...");

            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string ConfigPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Snail",
                "Config.json"
            );

            Config = ConfigManager.Load(ConfigPath);

            Debugger.show($"Config loaded from {ConfigPath}");

            ADB_PATH = Config.Paths.Adb;

            Debugger.show($"Scrcpy Path: {Config.Paths.Scrcpy}");

            wifiDevice = Config.SelectedDeviceWiFi;
            devmode = Config.SpecialOptions != null && Config.SpecialOptions.DevMode;
            debugmode = Config.SpecialOptions != null && Config.SpecialOptions.DebugMode;
            MusicPresence = Config.SpecialOptions != null && Config.SpecialOptions.MusicPresence;

            // Update media controller config at runtime so it picks up MusicRemoteRoot changes
            try
            {
                mediaController?.UpdateConfig(Config);
            }
            catch { }

            Debugger.show($"Selected Wi-Fi device: {wifiDevice}");
            Debugger.show($"Dev mode: {devmode}, Debug mode: {debugmode}");

            // Load button colors from Config
            try
            {
                Application.Current.Resources["ButtonBackground"] =
                    (SolidColorBrush)new BrushConverter().ConvertFromString(Config.ButtonStyle.Background);
                Application.Current.Resources["ButtonForeground"] =
                    (SolidColorBrush)new BrushConverter().ConvertFromString(Config.ButtonStyle.Foreground);
                Application.Current.Resources["ButtonHover"] =
                    (SolidColorBrush)new BrushConverter().ConvertFromString(Config.ButtonStyle.Hover);

                Debugger.show("Button colors loaded successfully");
            }
            catch
            {
                Debugger.show("Failed to load button colors, using defaults");
                Application.Current.Resources["ButtonBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#5539cc"));
                Application.Current.Resources["ButtonForeground"] = new SolidColorBrush(Colors.White);
                Application.Current.Resources["ButtonHover"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#553fff"));
            }

            // Set main window background color
            try
            {
                var bgRect = this.FindName("MainBackgroundRect") as System.Windows.Shapes.Rectangle;
                if (bgRect != null)
                {
                    var color = Config.ButtonStyle.BackgroundColor ?? "#111111";
                    bgRect.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
                }
            }
            catch { }
        }

        public async Task ReloadConfiguration()
        {
            LoadConfiguration();
            // Apply interval mode after reloading Config
            ApplyUpdateIntervalMode();
            try
            {
                if (connectionCheckTimer != null)
                {
                    if (connectionCheckTimer.Interval.TotalSeconds > 0)
                    {
                        if (!connectionCheckTimer.IsEnabled) connectionCheckTimer.Start();
                    }
                    else
                    {
                        // disable automatic updates
                        if (connectionCheckTimer.IsEnabled) connectionCheckTimer.Stop();
                    }
                }
            }
            catch { }
            EnableButtons(true);
            StatusText.Foreground = new SolidColorBrush(Colors.Red);
            await DetectDeviceAsync();
        }

        public string GetPincode() => Config.SelectedDevicePincode;
        #endregion

        #region Device Detection
        private async void ConnectionCheckTimer_Tick(object sender, EventArgs e) => await DetectDeviceAsync();

        private async Task DetectDeviceAsync()
        {
            Debugger.show("Starting device detection...");

            if (!File.Exists(Config.Paths.Adb))
            {
                SetStatus("Please add device under device settings.", Colors.Red);
                Debugger.show("ADB executable not found");
                return;
            }

            var devices = await AdbHelper.RunAdbCaptureAsync("devices");
            Debugger.show($"ADB devices output:\n{devices}");

            var deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            // First check USB device
            if (await CheckUsbDeviceAsync(deviceList)) return;

            // Wi‑Fi handling: only if configured
            if (!string.IsNullOrEmpty(Config.SelectedDeviceWiFi) && Config.SelectedDeviceWiFi != "None")
            {
                bool wifiPresent = deviceList.Any(l => l.StartsWith(Config.SelectedDeviceWiFi));
                if (!wifiPresent)
                {
                    Debugger.show("Configured Wi‑Fi device not present in 'adb devices' — attempting adb connect...");

                    // Try adb connect once
                    string connectResult = await AdbHelper.RunAdbCaptureAsync($"connect {Config.SelectedDeviceWiFi}");
                    Debugger.show("adb connect result: " + connectResult);

                    bool connectSucceeded = !string.IsNullOrWhiteSpace(connectResult) && (
                        connectResult.IndexOf("connected", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        connectResult.IndexOf("already", StringComparison.OrdinalIgnoreCase) >= 0);

                    if (connectSucceeded)
                    {
                        Debugger.show("adb connect appears to have succeeded; clearing failure counter.");
                        // clear failure counter and re-query devices
                        _wifiConnectFailures.Remove(Config.SelectedDeviceWiFi);

                        devices = await AdbHelper.RunAdbCaptureAsync("devices");
                        deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    }
                    else if(!portlost)
                    {
                        // increment failure counter
                        int count = 0;
                        if (stopSpammingARP == false)
                        {
                            _wifiConnectFailures.TryGetValue(Config.SelectedDeviceWiFi, out count);
                            count++;
                            _wifiConnectFailures[Config.SelectedDeviceWiFi] = count;
                            Debugger.show($"adb connect failed for {Config.SelectedDeviceWiFi}. Failure count: {count}");
                        }
                        else
                        {
                            Debugger.show("Stop fucking spamming ARP pls");
                            count = 0;
                        }

                        // only attempt ARP discovery after threshold failures
                        if (count >= WifiFailThreshold)
                        {
                            Debugger.show($"Failure threshold reached ({WifiFailThreshold}) for {Config.SelectedDeviceWiFi}. Attempting IP discovery by MAC...");
                            bool resolved = await TryResolveDeviceIpAndReconnectAsync();

                            // reset counter either way to avoid repeated ARP spam
                            if (resolved)
                            {
                                // Clear failure counter only when discovery succeeded to avoid losing the failure history
                                _wifiConnectFailures[Config.SelectedDeviceWiFi] = 0;

                                // re-query devices after resolving
                                devices = await AdbHelper.RunAdbCaptureAsync("devices");
                                deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            }
                            else
                            {
                                Debugger.show("ARP discovery returned nothing; keeping failure counter so discovery can be retried later.");
                                // keep the failure counter as-is so we won't silently forget repeated failures
                            }
                        }
                        else
                        {
                            Debugger.show("Not yet reached failure threshold; will retry later.");
                        }
                    }
                }
                else
                {
                    // wifiPresent — ensure counter reset
                    if (_wifiConnectFailures.ContainsKey(Config.SelectedDeviceWiFi))
                        _wifiConnectFailures[Config.SelectedDeviceWiFi] = 0;

                    Debugger.show("Wi‑Fi device present in adb devices — skipping discovery");
                }

                // Now check if Wi‑Fi device is connected
                if (await CheckWifiDeviceAsync(deviceList)) return;
            }
            if (!portlost && !stopSpammingARP)
            {
                SetStatus("No selected device found!", Colors.Red);
                EnableButtons(false);
                Debugger.show("No device detected");
            }
            else if (portlost)
            {
                SetStatus("Port lost", Colors.Orange);
            }
        }

        private async Task<bool> TryResolveDeviceIpAndReconnectAsync()
        {
            if (stopSpammingARP == true)
            {
                return false;
            }
            else
            {
                stopSpammingARP = true;
            }
                try
                {
                    // Find the saved device matching the selected device
                    DeviceConfig saved = null;
                    if (!string.IsNullOrEmpty(Config.SelectedDeviceUSB))
                        saved = Config.SavedDevices.FirstOrDefault(d => d.UsbSerial == Config.SelectedDeviceUSB);
                    if (saved == null && !string.IsNullOrEmpty(Config.SelectedDeviceName))
                        saved = Config.SavedDevices.FirstOrDefault(d => d.Name == Config.SelectedDeviceName);
                    if (saved == null && !string.IsNullOrEmpty(Config.SelectedDeviceWiFi))
                        saved = Config.SavedDevices.FirstOrDefault(d => d.TcpIp == Config.SelectedDeviceWiFi);

                    if (saved == null)
                    {
                        Debugger.show("No matching saved device found for IP resolution");
                        return false;
                    }

                    // If device is explicitly saved as USB-only, do not attempt discovery
                    if (!string.IsNullOrEmpty(saved.TcpIp) && saved.TcpIp.Equals("None", StringComparison.OrdinalIgnoreCase))
                    {
                        Debugger.show("Device is marked USB-only (TcpIp == 'None'); skipping IP discovery.");
                        return false;
                    }

                    // Determine MACs to search for
                    var candidates = new List<string>();
                    if (!string.IsNullOrWhiteSpace(saved.MacAddress)) candidates.Add(saved.MacAddress.ToLower());
                    if (!string.IsNullOrWhiteSpace(saved.FactoryMac)) candidates.Add(saved.FactoryMac.ToLower());
                    if (candidates.Count == 0)
                    {
                        Debugger.show("No MAC info stored for device; cannot perform IP discovery by MAC.");
                        return false;
                    }

                    // Try each candidate MAC
                    foreach (var mac in candidates)
                    {
                        string foundIp = "";
                        foundIp = await DiscoverIpByMacAsync(mac);

                        if (!string.IsNullOrEmpty(foundIp))
                        {
                            // If a debug port was saved (from wireless pairing), append it.
                            string newTcp = $"{foundIp}:5555";

                            Debugger.show($"Discovered device IP {foundIp} for MAC {mac}, updating config to {newTcp}");

                            // Update config entries
                            saved.TcpIp = newTcp;
                            Config.SelectedDeviceWiFi = newTcp;

                            // Save config to disk
                            try
                            {
                                string configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snail", "Config.json");
                                ConfigManager.Save(configPath, Config);
                            }
                            catch (Exception ex)
                            {
                                Debugger.show("Failed to save config after IP discovery: " + ex.Message);
                            }

                            // Attempt to connect via ADB
                            var connectResult = await AdbHelper.RunAdbCaptureAsync($"connect {Config.SelectedDeviceWiFi}");
                            Debugger.show("ADB connect result after discovery: " + connectResult);
                            Debugger.show("IP discovery found the device and set the IP correctly");
                            portlost = true;
                            return true;

                        }
                    }

                    Debugger.show("IP discovery by MAC did not find the device on the local subnet");
                    DeviceStatusText.Text = "please reconnect the device via USB to set up the port again";
                    //hate that this is the only seamingly stable way to detect port loss
                    portlost = true;
                    return false;
                }
                catch (Exception ex)
                {
                    Debugger.show("TryResolveDeviceIpAndReconnectAsync exception: " + ex.Message);
                    return false;
                }
        }

        private async Task<string> DiscoverIpByMacAsync(string mac)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(mac)) return null;

                // Normalize mac to lower-case colon-separated
                string target = mac.ToLower().Replace('-', ':');

                Debugger.show("Starting subnet ping sweep to discover MAC: " + target);

                // Determine local IPv4 and prefix
                string localIp = null;
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    var props = ni.GetIPProperties();
                    foreach (var ua in props.UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            var ip = ua.Address.ToString();
                            if (!ip.StartsWith("169."))
                            {
                                localIp = ip;
                                break;
                            }
                        }
                    }
                    if (!string.IsNullOrEmpty(localIp)) break;
                }

                if (string.IsNullOrEmpty(localIp))
                {
                    Debugger.show("Failed to determine local IPv4 address for ARP discovery");
                    return null;
                }

                var parts = localIp.Split('.');
                if (parts.Length < 3) return null;
                string prefix = $"{parts[0]}.{parts[1]}.{parts[2]}";

                // Ping sweep 1..254 in parallel with limited concurrency to populate ARP cache
                var semaphore = new SemaphoreSlim(100);
                var pingTasks = new List<Task>();
                for (int i = 1; i <= 254; i++)
                {
                    string ip = $"{prefix}.{i}";
                    if (ip == localIp) continue;

                    await semaphore.WaitAsync();
                    pingTasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            using (var p = new Ping())
                            {
                                await p.SendPingAsync(ip, 80);
                            }
                        }
                        catch { }
                        finally { semaphore.Release(); }
                    }));
                }

                // Wait for pings to finish (or timeout)
                await Task.WhenAll(pingTasks);

                // Small delay to let ARP table populate
                await Task.Delay(300);

                // Read ARP table
                var psi = new ProcessStartInfo
                {
                    FileName = "arp",
                    Arguments = "-a",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                string arpOutput = null;
                try
                {
                    using var proc = Process.Start(psi);
                    arpOutput = await proc.StandardOutput.ReadToEndAsync();
                    proc.WaitForExit();
                }
                catch (Exception ex)
                {
                    Debugger.show("Failed to run arp -a: " + ex.Message);
                    return null;
                }

                if (string.IsNullOrWhiteSpace(arpOutput)) return null;

                // Parse lines like:  192.168.0.127         26-be-9f-31-4d-b0     dynamic
                var lines = arpOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var arpRegex = new Regex(@"(\d+\.\d+\.\d+\.\d+)\s+([0-9A-Fa-f\-:]{17})");

                foreach (var line in lines)
                {
                    var m = arpRegex.Match(line);
                    if (!m.Success) continue;
                    var ip = m.Groups[1].Value.Trim();
                    var macRaw = m.Groups[2].Value.Trim().ToLower();
                    var macNorm = macRaw.Replace('-', ':');
                    if (macNorm == target)
                    {
                        Debugger.show($"ARP match: {ip} -> {macNorm}");
                        return ip;
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                Debugger.show("DiscoverIpByMacAsync exception: " + ex.Message);
                return null;
            }
        }

        private async Task<bool> CheckUsbDeviceAsync(string[] deviceList)
        {
            if (string.IsNullOrEmpty(Config.SelectedDeviceUSB)) return false;

            bool usbConnected = deviceList.Any(l => l.StartsWith(Config.SelectedDeviceUSB) && l.EndsWith("device"));

            // If device is not connected but was previously connected, treat as disconnect
            if (!usbConnected)
            {
                if (_wasUsbConnectedForSelectedDevice)
                {
                    Debugger.show($"USB device {Config.SelectedDeviceUSB} disconnected — clearing autostart tracking");
                    // Clear started-tracking so future reconnect will autostart
                    _scrcpyStartedForDevice.Remove(Config.SelectedDeviceUSB);
                    _wasUsbConnectedForSelectedDevice = false;
                }

                return false;
            }

            // usbConnected == true here
            bool isFreshConnect = !_wasUsbConnectedForSelectedDevice;
            _wasUsbConnectedForSelectedDevice = true;

            SetStatus($"USB device connected: {Config.SelectedDeviceName}", Colors.Green);
            currentDevice = Config.SelectedDeviceUSB;

            Debugger.show($"USB device {currentDevice} connected");
            //hate
            if (portlost == true) await SetupWifiOverUsbAsync(deviceList);

            // Auto USB start: only start on a fresh connect event
            try
            {
                if (Config.AutoUsbStart != null && Config.AutoUsbStart.Enabled && isFreshConnect)
                {
                    if (!_scrcpyStartedForDevice.Contains(currentDevice))
                    {
                        if (File.Exists(Config.Paths.Scrcpy))
                        {
                            var args = $"-s {currentDevice} {Config.AutoUsbStart.Arguments}".Trim();
                            StartScrcpyProcessForDevice(currentDevice, args);
                            Debugger.show("AutoUsbStart started scrcpy for device: " + currentDevice);
                        }
                        else
                        {
                            Debugger.show("AutoUsbStart: scrcpy.exe not found at path: " + Config.Paths.Scrcpy);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debugger.show("AutoUsbStart exception: " + ex.Message);
            }

            EnableButtons(true);
            await UpdateBatteryStatusAsync();
            await UpdateForegroundAppAsync();

            if (ContentHost.Content == null) ShowNotificationsAsDefault();

            return true;
        }

        private async Task SetupWifiOverUsbAsync(string[] deviceList)
        {
            //fuck this stupid ass shit fuck this stupid variable FUCK
            portlost = false;
            if (string.IsNullOrEmpty(Config.SelectedDeviceWiFi)) return;

            bool wifiAlreadyConnected = deviceList.Any(l => l.StartsWith(Config.SelectedDeviceWiFi));
            if (wifiAlreadyConnected)
            {
                Debugger.show("Wi-Fi device already connected via USB setup");
                return;
            }

            Debugger.show("Enabling TCP/IP mode on USB device");
            await AdbHelper.RunAdbAsync($"-s {Config.SelectedDeviceUSB} tcpip 5555");
            var connectResult = await AdbHelper.RunAdbCaptureAsync($"connect {Config.SelectedDeviceWiFi}");
            Debugger.show($"Wi-Fi connection result: {connectResult}");
            if (connectResult.Contains("connected"))
            {
                Debugger.show("found the IP adress using mac and ARP");
            }
            else
            {
                Debugger.show("Getting IP for device: " + Config.SelectedDeviceName);
                string output = await AdbHelper.RunAdbCaptureAsync($"-s {Config.SelectedDeviceUSB} shell ip addr show wlan0").ConfigureAwait(false);
                var match = Regex.Match(output, @"inet (\d+\.\d+\.\d+\.\d+)/");
                Debugger.show("IP result: " + (match.Success ? match.Groups[1].Value : "null"));
                string IP = match.Success ? match.Groups[1].Value : null;
                if (!string.IsNullOrEmpty(IP))
                {
                    Config.SelectedDeviceWiFi = IP + ":5555";
                    Debugger.show("found the IP adress using USB ADB");
                    DeviceConfig saved = null;
                    if (!string.IsNullOrEmpty(Config.SelectedDeviceUSB))
                        saved = Config.SavedDevices.FirstOrDefault(d => d.UsbSerial == Config.SelectedDeviceUSB);
                    if (saved == null && !string.IsNullOrEmpty(Config.SelectedDeviceName))
                        saved = Config.SavedDevices.FirstOrDefault(d => d.Name == Config.SelectedDeviceName);
                    if (saved == null && !string.IsNullOrEmpty(Config.SelectedDeviceWiFi))
                        saved = Config.SavedDevices.FirstOrDefault(d => d.TcpIp == Config.SelectedDeviceWiFi);

                    if (saved == null)
                    {
                        Debugger.show("No matching saved device found for IP resolution");
                    }
                    saved.TcpIp = IP + ":5555";
                    string configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snail", "Config.json");
                    ConfigManager.Save(configPath, Config);

                }

            }
        }

        private async Task<bool> CheckWifiDeviceAsync(string[] deviceList)
        {
            if (string.IsNullOrEmpty(Config.SelectedDeviceWiFi)) return false;

            bool wifiConnected = deviceList.Any(l => l.StartsWith(Config.SelectedDeviceWiFi));
            if (!wifiConnected) return false;

            SetStatus($"Wi-Fi device connected: {Config.SelectedDeviceName}", Colors.CornflowerBlue);
            currentDevice = Config.SelectedDeviceWiFi;
            EnableButtons(true);
            await UpdateBatteryStatusAsync();
            await UpdateForegroundAppAsync();
            if (ContentHost.Content == null) ShowNotificationsAsDefault();
            return true;
        }
        #endregion

        #region Battery & Foreground App
        private void SetStatus(string message, Color color)
        {
            StatusText.Text = message;
            StatusText.Foreground = new SolidColorBrush(color);
        }

        private async Task UpdateBatteryStatusAsync()
        {
            if (string.IsNullOrEmpty(currentDevice))
            {
                SetBatteryStatus("N/A", Colors.Gray);
                Debugger.show("No current device to check battery");
                return;
            }

            try
            {
                Debugger.show("Fetching battery info...");
                var output = await AdbHelper.RunAdbCaptureAsync($"-s {currentDevice} shell dumpsys battery");
                var batteryInfo = ParseBatteryInfo(output);

                Debugger.show($"Battery parsed: Level={batteryInfo.Level}, Charging={batteryInfo.IsCharging}, Wattage={batteryInfo.Wattage}");

                if (batteryInfo.Level < 0)
                {
                    SetBatteryStatus("N/A", Colors.Gray);
                    return;
                }

                string displayText = batteryInfo.IsCharging && batteryInfo.Wattage > 0
                    ? $"Battery: {batteryInfo.Level}% ({batteryInfo.ChargingStatus}) - {batteryInfo.Wattage:F1} W"
                    : $"Battery: {batteryInfo.Level}% ({batteryInfo.ChargingStatus})";

                SetBatteryStatus(displayText, GetBatteryColor(batteryInfo.Level));
                CheckBatteryWarnings(batteryInfo.Level, batteryInfo.IsCharging, batteryInfo.Wattage);
            }
            catch (Exception ex)
            {
                SetBatteryStatus("Error", Colors.Gray);
                Debugger.show($"Failed to update battery status: {ex.Message}");
            }
        }

        private (int Level, SetupControl.BatteryStatus Status, string ChargingStatus, bool IsCharging, double Wattage) ParseBatteryInfo(string output)
        {
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            int level = ParseIntFromLine(lines, "level");
            int statusInt = ParseIntFromLine(lines, "status");
            var status = Enum.IsDefined(typeof(SetupControl.BatteryStatus), statusInt)
                ? (SetupControl.BatteryStatus)statusInt
                : SetupControl.BatteryStatus.Unknown;
            long currentMicroA = ParseLongFromLine(lines, "Max charging current");
            long voltageMicroV = ParseLongFromLine(lines, "Max charging voltage");

            string chargingStatus = status switch
            {
                SetupControl.BatteryStatus.Charging => "Charging",
                SetupControl.BatteryStatus.Discharging => "Discharging",
                SetupControl.BatteryStatus.NotCharging => "Not charging",
                SetupControl.BatteryStatus.Full => "Full",
                _ => "Unknown"
            };

            bool isCharging = status == SetupControl.BatteryStatus.Charging;
            double amps = currentMicroA / 1_000_000.0;
            double volts = voltageMicroV / 1_000_000.0;
            double wattage = amps * volts;

            return (level, status, chargingStatus, isCharging, wattage);
        }

        private int ParseIntFromLine(string[] lines, string prefix)
        {
            var line = lines.FirstOrDefault(l => l.Trim().StartsWith(prefix));
            return line != null && int.TryParse(new string(line.Where(char.IsDigit).ToArray()), out int value) ? value : -1;
        }

        private long ParseLongFromLine(string[] lines, string prefix)
        {
            var line = lines.FirstOrDefault(l => l.Trim().StartsWith(prefix));
            return line != null && long.TryParse(new string(line.Where(char.IsDigit).ToArray()), out long value) ? value : 0;
        }

        private void SetBatteryStatus(string text, Color color)
        {
            BatteryText.Text = $"{text}";
            BatteryText.Foreground = new SolidColorBrush(color);
        }

        private Color GetBatteryColor(int level)
        {
            return level switch
            {
                >= 90 => Colors.Green,
                >= 40 => Colors.CornflowerBlue,
                >= 20 => Colors.Orange,
                > 0 => Colors.Red,
                _ => Colors.Gray
            };
        }

        private void CheckBatteryWarnings(int level, bool isCharging, double wattage)
        {
            // Reset warnings if battery level rises above 30% (from 30 or below)
            if (level > Config.BatteryWarningSettings.firstwarning+10 && lastBatteryLevel <= Config.BatteryWarningSettings.firstwarning + 10)
            {
                shownBatteryWarnings.Clear();
                Debugger.show($"Battery warnings reset: level rose above 30% (was {lastBatteryLevel}%, now {level}%)");
            }

            // Only trigger at specific thresholds
            if (
                Config.BatteryWarningSettings.ShowWarning
                && (
                    !Config.BatteryWarningSettings.wattthresholdenabled
                    || wattage <= Config.BatteryWarningSettings.wattthreshold
                )
                && (
                    (Config.BatteryWarningSettings.firstwarningenabled && level == Config.BatteryWarningSettings.firstwarning && !shownBatteryWarnings.Contains(level)) ||
                    (Config.BatteryWarningSettings.secondwarningenabled && level == Config.BatteryWarningSettings.secondwarning && !shownBatteryWarnings.Contains(level)) ||
                    (Config.BatteryWarningSettings.thirdwarningenabled && level == Config.BatteryWarningSettings.thirdwarning && !shownBatteryWarnings.Contains(level)) ||
                    (Config.BatteryWarningSettings.shutdownwarningenabled && level <= Config.BatteryWarningSettings.shutdownwarning && !shownBatteryWarnings.Contains(level))
                )
            )
            {
                if (_isBatteryWarningShown) return; // Prevent multiple dialogs
                _isBatteryWarningShown = true;
                try
                {
                    if (level == Config.BatteryWarningSettings.shutdownwarning)
                    {
                        if (Config.BatteryWarningSettings.emergencydisconnectenabled)
                        {
                            Task.Run(() =>
                            {
                                MessageBox.Show(
                                    "Shutting down Phone Utils in 5 seconds.\nConnect your charger immediately!",
                                    "Critical Battery",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Error
                                );
                            });
                            // Shutdown after 1 second, regardless of MessageBox interaction
                            Dispatcher.InvokeAsync(async () =>
                            {
                                await Task.Delay(5000);
                                Application.Current.Shutdown();
                            });
                        }
                        else
                        {
                            MessageBox.Show(
                                $"Battery critically low at {level}%! Connect your charger immediately.",
                                "Critical Battery",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error
                            );
                        }
                    }
                    else
                    {
                        MessageBox.Show(
                            $"Battery is at {level}%. Please charge your device.",
                            "Low Battery",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning
                        );
                    }
                    shownBatteryWarnings.Add(level);
                }
                finally
                {
                    _isBatteryWarningShown = false;
                }
            }

            // Update last battery level
            lastBatteryLevel = level;
            // Update last charging state
            wasCharging = isCharging;
        }

        private async Task UpdateForegroundAppAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(currentDevice))
                {
                    DeviceStatusText.Text = "No device selected.";
                    mediaController?.Clear();
                    return;
                }

                if (Config.SpecialOptions != null && Config.SpecialOptions.MusicPresence)
                {
                    // DevMode: detect currently playing song in Musicolet
                    await UpdateCurrentSongAsync();
                }
                else
                {
                    // Normal mode: detect active foreground app
                    await DisplayAppActivity();
                }
            }
            catch (Exception ex)
            {
                DeviceStatusText.Text = $"Error retrieving info: {ex.Message}";
                mediaController?.Clear();
            }
        }

        private async Task UpdateCurrentSongAsync()
        {
            Debugger.show("Updating current song from Musicolet...");
            string output = await AdbHelper.RunAdbCaptureAsync($"-s {currentDevice} shell dumpsys media_session");

            bool foundActiveSong = false;

            if (!string.IsNullOrWhiteSpace(output))
            {
                var sessionBlocks = output.Split(new[] { "queueTitle=" }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var block in sessionBlocks)
                {
                    if (!block.Contains("package=in.krosbits.musicolet") || !block.Contains("active=true"))
                        continue;
                    // Extract metadata: title, artist, album
                    var metaMatch = Regex.Match(block,
                        @"metadata:\s+size=\d+,\s+description=(.+?),\s+(.+?),\s+(.+)",
                        RegexOptions.Singleline);

                    if (!metaMatch.Success)
                        continue;

                    string title = metaMatch.Groups[1].Value.Trim();
                    string artist = metaMatch.Groups[2].Value.Trim();
                    string album = metaMatch.Groups[3].Value.Trim();

                    // Extract playback state and position
                    var stateMatch = Regex.Match(block, @"state=PlaybackState\s*\{[^}]*state=(\w+)\((\d+)\),\s*position=(\d+)", RegexOptions.Singleline);

                    bool isPlaying = false;
                    long position = 0;

                    if (stateMatch.Success)
                    {
                        string stateText = stateMatch.Groups[1].Value.Trim().ToUpper(); // PLAYING, PAUSED, etc.
                        isPlaying = stateText == "PLAYING";
                        long.TryParse(stateMatch.Groups[3].Value.Trim(), out position);
                    }


                    if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(artist))
                    {
                        DeviceStatusText.Text = $"Song: {title} by {artist}";
                        Debugger.show($"Now playing: {title} by {artist} ({album}) at {position} ms — Playing: {isPlaying}");

                        await mediaController.UpdateMediaControlsAsync(title, artist, album, isPlaying);
                        foundActiveSong = true;
                        break;
                    }
                }
            }
        }

        private async Task DisplayAppActivity()
        {
            var sleepState = await AdbHelper.RunAdbCaptureAsync($"-s {currentDevice} shell dumpsys power");
            var match = Regex.Match(sleepState, @"mWakefulness\s*=\s*(\w+)", RegexOptions.IgnoreCase);

            bool isAwake = match.Success && match.Groups[1].Value.Equals("Awake", StringComparison.OrdinalIgnoreCase);

            if (isAwake)
            {
                // Capture full dumpsys window output
                var input = await AdbHelper.RunAdbCaptureAsync($"-s {currentDevice} shell dumpsys window");

                // Find the line containing "mCurrentFocus"
                var currentFocusLine = input
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(line => line.Contains("mCurrentFocus"));

                if (currentFocusLine != null)
                {
                    // Apply your regex exactly as before
                    var match2 = Regex.Match(currentFocusLine, @"\su0\s([^\s/]+)");
                    if (match2.Success)
                    {
                        string packageName = match2.Groups[1].Value;
                        DeviceStatusText.Text = $"Current App: {packageName}";
                      }
                    else
                    {
                        DeviceStatusText.Text = $"Current app not found";
                    }
                }
                else
                {
                    DeviceStatusText.Text = $"Current app not found";
                }
            }
            else
            {
                DeviceStatusText.Text = $"Currently asleep";
            }
        }
        #endregion

        #region UI Handlers
        private void EnableButtons(bool enable)
        {
            bool adbAvailable = File.Exists(Config.Paths.Adb);
            bool scrcpyAvailable = File.Exists(Config.Paths.Scrcpy);

            BtnScrcpyOptions.IsEnabled = enable && scrcpyAvailable && adbAvailable;

            BtnSyncMusic.IsEnabled = enable && adbAvailable;
            Intent.IsEnabled = enable && adbAvailable;
            if(devmode == true)
            {
                Intent.Content = "Intent sender";
            }
            else
            {
                Intent.Content = "App Manager";
            }
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            // Trigger the spin animation
            var animation = (Storyboard)FindResource("SpinAnimation");
            animation.Begin();

            if (ContentHost.Content is SettingsControl)
            {
                ContentHost.Content = null;
                ShowNotificationsAsDefault();
            }
            else
            {
                ContentHost.Content = new SettingsControl(this, currentDevice);
            }
        }

        private void BtnScrcpyOptions_Click(object sender, RoutedEventArgs e)
        {
            if (ContentHost.Content is ScrcpyControl)
            {
                ContentHost.Content = null;
                ShowNotificationsAsDefault();
            }
            else
            {
                ContentHost.Content = new ScrcpyControl(this);
            }
        }

        private void Intent_click(object sender, RoutedEventArgs e)
        {
            if(devmode == true)
            {
                if (ContentHost.Content is Intent_Sender)
                {
                    ContentHost.Content = null;
                    ShowNotificationsAsDefault();
                }
                else
                {
                    ContentHost.Content = new Intent_Sender(this, currentDevice);
                }
            }
            else
            {
                if (ContentHost.Content is AppManagerControl)
                {
                    ContentHost.Content = null;
                    ShowNotificationsAsDefault();
                }
                else
                {
                    ContentHost.Content = new AppManagerControl(currentDevice);
                }
            }
        }

        private void BtnSyncMusic_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(currentDevice)) return;

            if (ContentHost.Content is FileSync)
            {
                ContentHost.Content = null;
                ShowNotificationsAsDefault();
            }
            else
            {
                ContentHost.Content = new FileSync { CurrentDevice = currentDevice };
            }
        }

        private void BtnSetup_Click(object sender, RoutedEventArgs e)
        {
            if (ContentHost.Content is SetupControl)
            {
                ContentHost.Content = null;
                ShowNotificationsAsDefault();
            }
            else
            {
                ContentHost.Content = new SetupControl(this);
            }
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await DetectDeviceAsync();
        #endregion

        #region Scrcpy Management
        private void ShowNotificationsAsDefault() => ContentHost.Content = new NotificationControl(this, currentDevice);

        private void CloseAllAdbProcesses()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = "/F /IM adb.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                Process.Start(psi).WaitForExit();
            }
            catch (Exception ex)
            {
                if(Config.SpecialOptions != null && Config.SpecialOptions.DebugMode)
                    MessageBox.Show($"Failed to close ADB processes: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Helper to start scrcpy for a given device and track it so we don't restart repeatedly
        private void StartScrcpyProcessForDevice(string deviceSerial, string arguments)
        {
            if (string.IsNullOrWhiteSpace(deviceSerial)) return;
            if (_scrcpyStartedForDevice.Contains(deviceSerial)) return;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Config.Paths.Scrcpy,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                var proc = Process.Start(psi);
                if (proc == null)
                {
                    Debugger.show("Failed to start scrcpy for device: " + deviceSerial);
                    return;
                }

                // track that we've started scrcpy for this connection — do NOT remove on process exit.
                // Clearing happens only when device disconnects.
                _scrcpyStartedForDevice.Add(deviceSerial);

                proc.EnableRaisingEvents = true;
                proc.Exited += (s, e) =>
                {
                    try
                    {
                        // Log exit but do NOT clear the started flag here; this prevents automatic restart while still connected.
                        Debugger.show("scrcpy process exited for device: " + deviceSerial);
                    }
                    catch { }
                };

                Debugger.show("Started scrcpy process: " + psi.FileName + " " + psi.Arguments);
            }
            catch (Exception ex)
            {
                Debugger.show("StartScrcpyProcessForDevice exception: " + ex.Message);
            }
        }
        #endregion

        #region Utilities
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            try
            {
                CloseAllAdbProcesses();

                mediaController?.Clear();
            }
            catch { }
        }
        #endregion

        #region Update Interval Mode
        private void ApplyUpdateIntervalMode()
        {
            try
            {
                if (Config == null) return;
                var mode = Config.UpdateIntervalMode;
                switch (mode)
                {
                    case UpdateIntervalMode.Extreme:
                        connectionCheckTimer.Interval = TimeSpan.FromSeconds(1);
                        break;
                    case UpdateIntervalMode.Fast:
                        connectionCheckTimer.Interval = TimeSpan.FromSeconds(5);
                        break;
                    case UpdateIntervalMode.Medium:
                        connectionCheckTimer.Interval = TimeSpan.FromSeconds(15);
                        break;
                    case UpdateIntervalMode.Slow:
                        connectionCheckTimer.Interval = TimeSpan.FromSeconds(30);
                        break;
                    case UpdateIntervalMode.None:
                        connectionCheckTimer.Interval = TimeSpan.FromSeconds(0);
                        break;
                    default:
                        connectionCheckTimer.Interval = TimeSpan.FromSeconds(15);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debugger.show("ApplyUpdateIntervalMode failed: " + ex.Message);
            }
        }
        #endregion
    }
}
