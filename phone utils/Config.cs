using Newtonsoft.Json;
using System.IO;
using System.Windows;
using System.Security.Cryptography;
using System.Text;

namespace phone_utils
{
    public class Scrcpyautostart
    {
        public bool Enabled { get; set; } = false;
        public string Arguments { get; set; } = "";
    }

    // New: renamed autorun start config (replaces/extends old Scrcpyautostart)
    public class AutorunStartConfig
    {
        public bool Enabled { get; set; } = false;
        public string Arguments { get; set; } = string.Empty;
    }

    // New: auto USB start config
    public class AutoUsbStartConfig
    {
        public bool Enabled { get; set; } = false;
        public string Arguments { get; set; } = string.Empty;
    }

    public class DeviceConfig
    {
        public string Name { get; set; } = string.Empty;
        public string UsbSerial { get; set; } = string.Empty;
        public string TcpIp { get; set; } = string.Empty;

        // Keep JSON field name `Pincode` for compatibility but store encrypted value here.
        [JsonProperty("Pincode")]
        public string PincodeEncrypted { get; set; } = string.Empty;

        // Runtime property that returns the decrypted pincode. Not serialized.
        [JsonIgnore]
        public string Pincode
        {
            get
            {
                if (string.IsNullOrEmpty(PincodeEncrypted))
                    return string.Empty;

                try
                {
                    return ConfigManager.DecryptString(PincodeEncrypted);
                }
                catch
                {
                    // If decryption fails, assume the stored value was plaintext (legacy).
                    return PincodeEncrypted;
                }
            }
            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    PincodeEncrypted = string.Empty;
                }
                else
                {
                    PincodeEncrypted = ConfigManager.EncryptString(value);
                }
            }
        }

        public DateTime LastConnected { get; set; } = DateTime.Now;

        // New: store the device's current Wi-Fi MAC (colon-separated, lower-case)
        public string MacAddress { get; set; } = string.Empty;

        // New: store factory MAC if available
        public string FactoryMac { get; set; } = string.Empty;

        public override string ToString() => string.IsNullOrWhiteSpace(Name) ? base.ToString() : Name;
    }

    public class ThemesConfig
    {
        public string Name { get; set; } = string.Empty;
        public string Foreground { get; set; } = "White";
        public string Background { get; set; } = "#5539cc";
        public string Hover { get; set; } = "#553fff";
        public string BackgroundColor { get; set; } = "#111111"; // New: background color for main window
    }

    public class ButtonStyleConfig
    {
        public string Foreground { get; set; } = "White";
        public string Background { get; set; } = "#5539cc";
        public string Hover { get; set; } = "#553fff";
        public string BackgroundColor { get; set; } = "#111111"; // New: background color for main window
    }

    public class SpecialOptionsConfig
    {
        public bool DebugMode { get; set; } = false;
        public bool DevMode { get; set; } = false;
    }

    public class BatteryWarningSettingConfig
    {
        public bool ShowWarning { get; set; } = true;
        public bool chargingwarningenabled { get; set; } = true;
        public bool wattthresholdenabled { get; set; } = true;
        public double wattthreshold { get; set; } = 2.5f;
        public bool firstwarningenabled { get; set; } = true;
        public double firstwarning { get; set; } = 20.0f;
        public bool secondwarningenabled { get; set; } = true;
        public double secondwarning { get; set; } = 10.0f;
        public bool thirdwarningenabled { get; set; } = true;
        public double thirdwarning { get; set; } = 5.0f;
        public bool shutdownwarningenabled { get; set; } = true;
        public double shutdownwarning { get; set; } = 2.0f;
        public bool emergencydisconnectenabled { get; set; } = true;
    }

    public enum UpdateIntervalMode
    {
        Extreme = 1,
        Fast = 2,
        Medium = 3,
        Slow = 4,
        None = 5
    }

    public class AppConfig
    {
        public PathsConfig Paths { get; set; } = new PathsConfig();
        public FileSyncConfig FileSync { get; set; } = new FileSyncConfig();
        public ScrcpyConfig ScrcpySettings { get; set; } = new ScrcpyConfig();

        // Keep old property for backward compatibility during load; new code should use AutorunStart and AutoUsbStart
        public Scrcpyautostart ScrcpyAutoStart { get; set; } = new Scrcpyautostart();

        // New: renamed autorun config
        public AutorunStartConfig AutorunStart { get; set; } = new AutorunStartConfig();

        // New: auto USB start config
        public AutoUsbStartConfig AutoUsbStart { get; set; } = new AutoUsbStartConfig();

        public YTDLConfig YTDL { get; set; } = new YTDLConfig();
        public BatteryWarningSettingConfig BatteryWarningSettings { get; set; } = new BatteryWarningSettingConfig();
        public List<ThemesConfig> Themes { get; set; } = new List<ThemesConfig>();
        public ButtonStyleConfig ButtonStyle { get; set; } = new ButtonStyleConfig();
        public List<DeviceConfig> SavedDevices { get; set; } = new List<DeviceConfig>();
        public SpecialOptionsConfig SpecialOptions { get; set; } = new SpecialOptionsConfig();
        public UpdateIntervalMode UpdateIntervalMode { get; set; } = UpdateIntervalMode.Medium;
        public string SelectedDeviceUSB { get; set; } = string.Empty;
        public string SelectedDeviceName { get; set; } = string.Empty;
        public string SelectedDeviceWiFi { get; set; } = string.Empty;

        // Keep the JSON field name for backward compatibility but store encrypted value
        [JsonProperty("SelectedDevicePincode")]
        public string SelectedDevicePincodeEncrypted { get; set; } = string.Empty;

        [JsonIgnore]
        public string SelectedDevicePincode
        {
            get
            {
                if (string.IsNullOrEmpty(SelectedDevicePincodeEncrypted))
                    return string.Empty;

                try
                {
                    return ConfigManager.DecryptString(SelectedDevicePincodeEncrypted);
                }
                catch
                {
                    // If decryption fails, assume stored value was plaintext
                    return SelectedDevicePincodeEncrypted;
                }
            }
            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    SelectedDevicePincodeEncrypted = string.Empty;
                }
                else
                {
                    SelectedDevicePincodeEncrypted = ConfigManager.EncryptString(value);
                }
            }
        }
    }

    public class PathsConfig
    {
        public string Adb { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Snail",
            "Resources",
            "adb.exe");
        public string Scrcpy { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Snail",
            "Resources",
            "scrcpy.exe");
        // Path where installer will place ffmpeg
        public string FfmpegPath { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Snail",
            "Resources",
            "ffmpeg.exe");

        // Path to cover cache
        public string CoverCachePath { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Snail",
            "CoverCache");
    }

    public class FileSyncConfig
    {
        public string LocalDir { get; set; } = "";
        public string RemoteDir { get; set; } = "";
        public bool recursion { get; set; } = true;
    }

    public class ScrcpyConfig
    {
        public bool AudioOnly { get; set; } = false;
        public bool NoAudio { get; set; } = true;
        public bool PlaybackAudio { get; set; } = false;
        public bool LimitMaxSize { get; set; } = true;
        public int MaxSize { get; set; } = 2440;
        public bool StayAwake { get; set; } = true;
        public bool TurnScreenOff { get; set; } = true;
        public bool LockPhone { get; set; } = true;
        public bool Top { get; set; } = false;
        public bool EnableHotkeys { get; set; } = true;
        public bool audiobuffer { get; set; } = false;
        public bool videobuffer { get; set; } = false;
        public int AudioBufferSize { get; set; } = 50;
        public int VideoBufferSize { get; set; } = 50;
        public int CameraType { get; set; } = 0;
        public string VirtualDisplayApp { get; set; } = "";
    }

    public class YTDLConfig
    {
        public int DownloadType { get; set; } = 1;
        public bool BackgroundCheck { get; set; } = false;
    }

    public class BlockedGateway
    {
        public string Gateway { get; set; }
        public DateTime BlockedAtUtc { get; set; }
    }

    public static class ConfigManager
    {
        // Encrypts a string using DPAPI (CurrentUser) and returns base64
        public static string EncryptString(string plain)
        {
            if (string.IsNullOrEmpty(plain))
                return string.Empty;

            byte[] plainBytes = Encoding.UTF8.GetBytes(plain);
            byte[] encrypted = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }

        // Decrypts a base64 string produced by EncryptString
        public static string DecryptString(string encryptedBase64)
        {
            if (string.IsNullOrEmpty(encryptedBase64))
                return string.Empty;

            byte[] encrypted = Convert.FromBase64String(encryptedBase64);
            byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }

        public static AppConfig Load(string path)
        {
            try
            {
                AppConfig config;
                if (!File.Exists(path))
                {
                    config = new AppConfig();
                }
                else
                {
                    string json = File.ReadAllText(path);
                    config = JsonConvert.DeserializeObject<AppConfig>(json) ?? new AppConfig();
                }

                // Migration: if new AutorunStart wasn't present but ScrcpyAutoStart has values, copy them over
                if ((config.AutorunStart == null || string.IsNullOrEmpty(config.AutorunStart.Arguments)) && config.ScrcpyAutoStart != null)
                {
                    config.AutorunStart = new AutorunStartConfig
                    {
                        Enabled = config.ScrcpyAutoStart.Enabled,
                        Arguments = config.ScrcpyAutoStart.Arguments
                    };
                }




                // Ensure AutoUsbStart exists
                if (config.AutoUsbStart == null)
                {
                    config.AutoUsbStart = new AutoUsbStartConfig();
                }

                // Ensure default themes exist if none are defined
                if (config.Themes == null || config.Themes.Count == 0)
                {
                    config.Themes = new List<ThemesConfig>
                    {
                        new ThemesConfig { Name = "Default Blurple", Foreground = "White", Background = "#5539cc", Hover = "#553fff", BackgroundColor = "#111111"},
                        new ThemesConfig { Name = "SnailDev Red", Foreground = "#FF000000", Background = "#FFC30000", Hover = "#FF8D0000", BackgroundColor = "#111111"},
                        new ThemesConfig { Name = "Grayscale", Foreground = "#FFFFFFFF", Background = "#FF323232", Hover = "#FF282828", BackgroundColor = "#111111"},
                        new ThemesConfig { Name = "White", Foreground = "#FF000000", Background = "#FFFFFFFF", Hover = "#FFC8C8C8", BackgroundColor = "#111111"},
                        new ThemesConfig { Name = "Rissoe", Foreground = "#FF82FF5E", Background = "#FF0743A0", Hover = "#FF003282" , BackgroundColor = "#111111"}
                    };
                }

                // Migrate any legacy plaintext pincode values to encrypted form
                if (config.SavedDevices != null)
                {
                    foreach (var dev in config.SavedDevices)
                    {
                        if (string.IsNullOrEmpty(dev.PincodeEncrypted))
                            continue;

                        try
                        {
                            // Try to decrypt; if this succeeds it's already encrypted and fine
                            var _ = DecryptString(dev.PincodeEncrypted);
                        }
                        catch
                        {
                            // Decryption failed -> assume value is plaintext; encrypt it and store back
                            try
                            {
                                dev.Pincode = dev.PincodeEncrypted; // setter will encrypt
                            }
                            catch
                            {
                                // ignore any further failures
                            }
                        }
                    }
                }

                // Migrate SelectedDevicePincode (legacy plaintext) to encrypted form
                if (!string.IsNullOrEmpty(config.SelectedDevicePincodeEncrypted))
                {
                    try
                    {
                        // If this decrypts successfully, it's already encrypted
                        var _ = DecryptString(config.SelectedDevicePincodeEncrypted);
                    }
                    catch
                    {
                        // Decryption failed -> assume plaintext, encrypt via setter
                        try
                        {
                            config.SelectedDevicePincode = config.SelectedDevicePincodeEncrypted;
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                }

                return config;
            }
            catch
            {
                return new AppConfig();
            }
        }

        public static void Save(string path, AppConfig config)
        {
            try
            {
                File.WriteAllText(path, JsonConvert.SerializeObject(config, Formatting.Indented));
                Debugger.show("ConfigManager.Save completed for: " + path);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save configuration: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Debugger.show("ConfigManager.Save exception: " + ex.Message);
            }
        }
    }
}