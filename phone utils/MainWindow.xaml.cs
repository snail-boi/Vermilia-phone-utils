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

        private MediaController mediaController;

        private int lastBatteryLevel = 100;
        private bool _isBatteryWarningShown = false;

        private readonly HashSet<string> _scrcpyStartedForDevice = new HashSet<string>();
        private bool _wasUsbConnectedForSelectedDevice = false;
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
                "Phone Utils",
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

            if (Config.SelectedDeviceWiFi != "None")
            {
                Debugger.show($"Connecting to Wi-Fi device: {wifiDevice}");
                await AdbHelper.RunAdbAsync($"connect {wifiDevice}");
            }

            var devices = await AdbHelper.RunAdbCaptureAsync("devices");
            Debugger.show($"ADB devices output:\n{devices}");

            var deviceList = devices.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            if (await CheckUsbDeviceAsync(deviceList)) return;
            if (await CheckWifiDeviceAsync(deviceList) && Config.SelectedDeviceWiFi != "None") return;

            SetStatus("No selected device found!", Colors.Red);
            EnableButtons(false);
            Debugger.show("No device detected");
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

            if (Config.SelectedDeviceWiFi != "None")
            {
                Debugger.show("Setting up Wi-Fi over USB...");
                await SetupWifiOverUsbAsync(deviceList);
            }

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

            StatusText.Text += connectResult.Contains("connected")
                ? " | Wi-Fi port has been set up."
                : " | Failed to connect Wi-Fi device.";
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
                ContentHost.Content = new SettingsControl(this);
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
