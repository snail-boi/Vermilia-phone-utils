using Microsoft.Win32;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Threading.Tasks;
using System.Linq;

namespace phone_utils
{
    public partial class SetupControl : UserControl
    {
        private readonly MainWindow _main;
        private AppConfig _config;
        private readonly string configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Snail",
            "config.json"
        );

        public SetupControl(MainWindow main)
        {
            InitializeComponent();
            _main = main;

            // Set background color from config
            try
            {
                var config = ConfigManager.Load(configPath);
                Application.Current.Resources["AppBackgroundColor"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(config.ButtonStyle.BackgroundColor ?? "#111111"));
            }
            catch { Application.Current.Resources["AppBackgroundColor"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#111111")); }

            Debugger.show("SetupControl initialized. Loading configuration from: " + configPath);
            _config = ConfigManager.Load(configPath);
            ApplyConfigToUI();
        }

        #region UI Initialization
        private void ApplyConfigToUI()
        {
            Debugger.show("Applying configuration to UI.");
            string resourcesDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Snail",
                "Resources"
            );

            // Default paths
            _config.Paths.Adb = string.IsNullOrEmpty(_config.Paths.Adb) || _config.Paths.Adb.Contains("PhoneUtils")
                ? Path.Combine(resourcesDir, "adb.exe")
                : _config.Paths.Adb;
            Debugger.show("ADB Path set to: " + _config.Paths.Adb);

            _config.Paths.Scrcpy = string.IsNullOrEmpty(_config.Paths.Scrcpy) || _config.Paths.Scrcpy.Contains("PhoneUtils")
                ? Path.Combine(resourcesDir, "scrcpy.exe")
                : _config.Paths.Scrcpy;
            Debugger.show("Scrcpy Path set to: " + _config.Paths.Scrcpy);

            TxtAdbPath.Text = _config.Paths.Adb;
            TxtScrcpyPath.Text = _config.Paths.Scrcpy;

            DeviceSelector.ItemsSource = _config.SavedDevices;
            if (!string.IsNullOrEmpty(_config.SelectedDeviceUSB))
            {
                DeviceSelector.SelectedValue = _config.SelectedDeviceUSB;
                TxtPincode.Password = _config.SavedDevices
                    .FirstOrDefault(d => d.UsbSerial == _config.SelectedDeviceUSB)?.Pincode ?? string.Empty;
                Debugger.show("Selected device set: " + _config.SelectedDeviceUSB);
            }

            ToggleDevMode(_config.SpecialOptions.DevMode);
        }

        private void ToggleDevMode(bool enabled)
        {
            Debugger.show("ToggleDevMode called. Enabled: " + enabled);
            Visibility devVisibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            AdbPathPanel.Visibility = devVisibility;
            ScrcpyPathPanel.Visibility = devVisibility;
            SaveButton.Visibility = devVisibility;
        }
        #endregion

        #region File Browsing
        private string BrowseFile(string filter)
        {
            var dlg = new OpenFileDialog { Filter = filter };
            var result = dlg.ShowDialog() == true ? dlg.FileName : string.Empty;
            Debugger.show("BrowseFile selected: " + result);
            return result;
        }

        private void BrowseAdb(object sender, RoutedEventArgs e) => TxtAdbPath.Text = BrowseFile("ADB Executable|adb.exe");
        private void BrowseScrcpy(object sender, RoutedEventArgs e) => TxtScrcpyPath.Text = BrowseFile("Scrcpy Executable|scrcpy.exe");
        #endregion

        #region Config Save / Reload
        private async void SaveConfiguration(object sender, RoutedEventArgs e)
        {
            Debugger.show("SaveConfiguration triggered.");
            UpdateConfigFromUI();
            await SaveConfig(true);
        }

        private void UpdateConfigFromUI()
        {
            Debugger.show("Updating config from UI.");
            _config.Paths.Adb = TxtAdbPath.Text;
            _config.Paths.Scrcpy = TxtScrcpyPath.Text;

            if (DeviceSelector.SelectedItem is DeviceConfig selDev)
            {
                _config.SelectedDeviceUSB = selDev.UsbSerial;
                _config.SelectedDeviceName = selDev.Name;
                _config.SelectedDeviceWiFi = selDev.TcpIp;
                _config.SelectedDevicePincode = selDev.Pincode;
                Debugger.show("Device selection updated: " + selDev.UsbSerial);
            }
        }

        private async Task SaveConfig(bool showmessage)
        {
            try
            {
                string folder = Path.GetDirectoryName(configPath);
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

                ConfigManager.Save(configPath, _config);
                Debugger.show("Configuration saved to: " + configPath);

                // Ensure the main window reloads configuration and wait for completion
                await _main.ReloadConfiguration();

                if (showmessage)
                    MessageBox.Show("Configuration saved successfully!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save configuration: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Debugger.show("SaveConfig exception: " + ex.Message);
            }
        }
        #endregion

        #region Device Management
        private async Task<string> GetDeviceIpAsync(string serial)
        {
            Debugger.show("Getting IP for device: " + serial);
            string output = await AdbHelper.RunAdbCaptureAsync($"-s {serial} shell ip addr show wlan0").ConfigureAwait(false);

            var match = Regex.Match(output, @"inet (\d+\.\d+\.\d+\.\d+)/");
            Debugger.show("IP result: " + (match.Success ? match.Groups[1].Value : "null"));
            return match.Success ? match.Groups[1].Value : null;
        }

        private async Task<string> GetFirstUsbSerialAsync()
        {
            string output = await AdbHelper.RunAdbCaptureAsync("devices").ConfigureAwait(false);
            Debugger.show("ADB devices output:\n" + output);

            foreach (var line in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                // Expected format: <serial>\tdevice
                if (trimmed.EndsWith("\tdevice"))
                {
                    var parts = trimmed.Split('\t');
                    if (parts.Length > 0)
                    {
                        Debugger.show("First USB device found: " + parts[0]);
                        return parts[0];
                    }
                }
            }

            Debugger.show("No USB device found.");
            return null;
        }

        // Read MAC info (active MAC, factory MAC) from device via adb
        private async Task<(string ActiveMac, string FactoryMac)> GetDeviceMacInfoAsync(string serial)
        {
            try
            {
                var macRegex = new Regex("([0-9A-Fa-f]{2}[:\\-]){5}[0-9A-Fa-f]{2}", RegexOptions.Compiled);

                // 1) Try reading the standard sysfs address
                string out1 = await AdbHelper.RunAdbCaptureAsync($"-s {serial} shell cat /sys/class/net/wlan0/address");
                if (!string.IsNullOrWhiteSpace(out1))
                {
                    var m = macRegex.Match(out1);
                    if (m.Success)
                    {
                        string mac = m.Value.Trim().ToLower().Replace('-', ':');
                        Debugger.show("Found mac via sysfs: " + mac);

                        // also try dumpsys for factory
                        string dumpsys = await AdbHelper.RunAdbCaptureAsync($"-s {serial} shell dumpsys wifi");
                        string factory = null;
                        var factoryMatch = Regex.Match(dumpsys, @"wifi_sta_factory_mac_address\s*[:=]\s*([0-9A-Fa-f:\-]+)");
                        if (factoryMatch.Success) factory = factoryMatch.Groups[1].Value.Trim().ToLower().Replace('-', ':');

                        return (mac, factory ?? string.Empty);
                    }
                }

                // 2) Fallback: use dumpsys wifi to extract active MAC and factory
                string dump = await AdbHelper.RunAdbCaptureAsync($"-s {serial} shell dumpsys wifi");
                if (!string.IsNullOrWhiteSpace(dump))
                {
                    // Look for line with "MAC: aa:bb:cc:dd:ee:ff" (mWifiInfo)
                    var macMatch = Regex.Match(dump, @"\bMAC:\s*([0-9A-Fa-f:\-]{17})");
                    string active = null;
                    if (macMatch.Success)
                    {
                        active = macMatch.Groups[1].Value.ToLower().Replace('-', ':');
                        Debugger.show("Found mac via dumpsys (MAC:): " + active);
                    }

                    var factoryMatch2 = Regex.Match(dump, @"wifi_sta_factory_mac_address\s*[:=]\s*([0-9A-Fa-f:\-]+)");
                    string factory2 = factoryMatch2.Success ? factoryMatch2.Groups[1].Value.ToLower().Replace('-', ':') : string.Empty;

                    if (!string.IsNullOrEmpty(active)) return (active, factory2);

                    // 3) As a last resort, search for any MAC in output
                    var anyMac = macRegex.Match(dump);
                    if (anyMac.Success)
                    {
                        string any = anyMac.Value.ToLower().Replace('-', ':');
                        Debugger.show("Found mac via dumpsys any-match: " + any);
                        return (any, factory2);
                    }
                }

                return (string.Empty, string.Empty);
            }
            catch (Exception ex)
            {
                Debugger.show("GetDeviceMacInfoAsync exception: " + ex.Message);
                return (string.Empty, string.Empty);
            }
        }

        private async void SaveCurrentDevice(object sender, RoutedEventArgs e)
        {
            try
            {
                string adbPath = TxtAdbPath.Text;
                if (string.IsNullOrWhiteSpace(adbPath) || !File.Exists(adbPath))
                {
                    MessageBox.Show("Please select a valid adb.exe path first.", "ADB Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
                    Debugger.show("Invalid ADB path: " + adbPath);
                    return;
                }

                string serial = await GetFirstUsbSerialAsync();
                if (serial == null)
                {
                    MessageBox.Show(
                        "No USB device was detected.\n\n" +
                        "Please ensure the following:\n" +
                        "1. USB debugging is enabled on your phone.\n" +
                        "2. Your phone is connected via a USB cable.\n" +
                        "3. You’ve allowed the computer’s RSA prompt on your device (if shown).",
                        "Device Not Found",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning
                    );
                    return;
                }

                // Try to get IP (Wi-Fi)
                string ip = await GetDeviceIpAsync(serial);
                string tcpIpWithPort;
                bool saveWithWifi = false;

                if (string.IsNullOrEmpty(ip))
                {
                    // Ask user if they want to save as USB-only
                    var result = MessageBox.Show(
                        "No Wi-Fi IP detected for this device. Do you want to save it as USB only?",
                        "IP Not Found",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question
                    );
                    if (result != MessageBoxResult.Yes)
                    {
                        return;
                    }
                    tcpIpWithPort = "None";
                    Debugger.show($"Device IP: {ip}, TCP: {tcpIpWithPort}");
                }
                else
                {
                    // Ask user if they want to save with Wi-Fi or USB only, with security warning
                    var result = MessageBox.Show(
                        $"A Wi-Fi IP was detected for this device: {ip}\n\nDo you want to enable Wireless Debugging (Android 11+)?\n\nYes: Setup Wireless Debugging (Pairing required)\nNo: Save as USB only\nCancel: Abort",
                        "Wireless Debugging Setup",
                        MessageBoxButton.YesNoCancel,
                        MessageBoxImage.Question
                    );

                    if (result == MessageBoxResult.Yes)
                    {
                        tcpIpWithPort = ip + ":5555"; // Default port
                        Debugger.show($"Wireless Debugging enabled. IP: {ip}");
                        // Wireless Debugging Flow
                    }
                    else if (result == MessageBoxResult.No)
                    {
                        // User chose No: save as USB only
                        tcpIpWithPort = "None";
                        Debugger.show("User chose not to configure wireless debugging. Saving as USB only.");
                    }
                    else // Cancel -> abort operation
                    {
                        return;
                    }
                }

                string name = TxtDeviceName.Text.Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    MessageBox.Show("Please enter a device name.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Retrieve MAC info from device (while still USB connected)
                var (activeMac, factoryMac) = await GetDeviceMacInfoAsync(serial);

                var newDevice = new DeviceConfig
                {
                    Name = name,
                    UsbSerial = serial,
                    TcpIp = tcpIpWithPort,
                    LastConnected = DateTime.Now,
                    Pincode = TxtPincode.Password,

                    // Save MAC info if available
                    MacAddress = string.IsNullOrWhiteSpace(activeMac) ? string.Empty : activeMac,
                    FactoryMac = string.IsNullOrWhiteSpace(factoryMac) ? string.Empty : factoryMac,
                };
                Debugger.show("New device created: " + newDevice.UsbSerial + ", Name: " + newDevice.Name + ", MAC: " + newDevice.MacAddress);

                // Remove existing device with same serial
                var existing = _config.SavedDevices.FirstOrDefault(d => d.UsbSerial == serial);
                if (existing != null)
                {
                    _config.SavedDevices.Remove(existing);
                    Debugger.show("Removed existing device with same serial: " + serial);
                }

                _config.SavedDevices.Add(newDevice);
                UpdateSelectedDevice(newDevice);

                // Refresh ComboBox
                DeviceSelector.SelectionChanged -= DeviceSelector_SelectionChanged;
                DeviceSelector.ItemsSource = null;
                DeviceSelector.ItemsSource = _config.SavedDevices;
                DeviceSelector.SelectedValue = serial;
                DeviceSelector.SelectionChanged += DeviceSelector_SelectionChanged;

                await SaveConfig(false);

                string msg;
                if (tcpIpWithPort == "None")
                {
                    msg = "Device saved successfully (USB connection only).";
                }
                else
                {
                    msg = $"Device saved successfully.\n\nDetected IP: {tcpIpWithPort}\nYou can now disconnect the USB cable.\n\nWarning: Using IP on an unsecured network can be dangerous.";
                }
                Debugger.show(msg);
                MessageBox.Show(msg, "Success", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save device: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Debugger.show("SaveCurrentDevice exception: " + ex.Message);
            }
        }

        private void UpdateSelectedDevice(DeviceConfig device)
        {
            Debugger.show("Updating selected device: " + device.UsbSerial);
            _config.SelectedDeviceUSB = device.UsbSerial;
            _config.SelectedDeviceName = device.Name;
            _config.SelectedDeviceWiFi = device.TcpIp;
            _config.SelectedDevicePincode = device.Pincode;
            TxtPincode.Password = device.Pincode;
        }

        private async void DeleteSelectedDevice(object sender, RoutedEventArgs e)
        {
            if (DeviceSelector.SelectedItem is not DeviceConfig selectedDevice)
                return;

            var result = MessageBox.Show(
                $"Are you sure you want to delete the device '{selectedDevice.Name}'?",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning
            );

            if (result != MessageBoxResult.Yes) return;

            _config.SavedDevices.Remove(selectedDevice);
            Debugger.show("Deleted device: " + selectedDevice.UsbSerial);

            if (_config.SelectedDeviceUSB == selectedDevice.UsbSerial)
            {
                TxtPincode.Password = string.Empty;
                _config.SelectedDeviceUSB = string.Empty;
                _config.SelectedDeviceName = string.Empty;
                _config.SelectedDeviceWiFi = string.Empty;
                _config.SelectedDevicePincode = string.Empty;
                Debugger.show("Cleared current selected device as it was deleted.");
            }

            DeviceSelector.SelectionChanged -= DeviceSelector_SelectionChanged;
            DeviceSelector.ItemsSource = null;
            DeviceSelector.ItemsSource = _config.SavedDevices;
            DeviceSelector.SelectionChanged += DeviceSelector_SelectionChanged;

            await SaveConfig(false);
        }

        private async void DeviceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DeviceSelector.SelectedItem is DeviceConfig selectedDevice)
            {
                Debugger.show("DeviceSelector changed to: " + selectedDevice.UsbSerial);
                UpdateSelectedDevice(selectedDevice);
                UpdateConfigFromUI();
                await SaveConfig(false);
            }
        }
        #endregion

        public enum BatteryStatus
        {
            Unknown = 0,
            Charging = 2,
            Discharging = 3,
            NotCharging = 4,
            Full = 5
        }
    }
}