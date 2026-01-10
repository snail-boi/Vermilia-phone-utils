using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.IO;

namespace phone_utils
{
    public partial class AppManagerControl : UserControl
    {
        private string _device; // Set this from MainWindow or your logic
        private AppConfig config; // class-level

        public AppManagerControl(string device)
        {
            InitializeComponent();
            // Set background color from config
            try
            {
                string configPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Snail",
                    "config.json"
                );
                var config = ConfigManager.Load(configPath);
                Application.Current.Resources["AppBackgroundColor"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(config.ButtonStyle.BackgroundColor ?? "#111111"));
            }
            catch { Application.Current.Resources["AppBackgroundColor"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#111111")); }
            RefreshInstalledApps();
            _device = device;
        }

        #region Installed Apps

        private async void RefreshInstalledApps()
        {
            if (string.IsNullOrEmpty(_device)) return;

            try
            {
                TxtStatus.Text += "Refreshing installed apps...\n";
                var output = await AdbHelper.RunAdbCaptureAsync($"-s {_device} shell pm list packages");
                var packages = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                                     .Where(l => l.StartsWith("package:"))
                                     .Select(l => l.Substring(8))
                                     .OrderBy(l => l)
                                     .ToList();

                CmbAndroidApps.ItemsSource = packages;
                TxtStatus.Text += $"Loaded {packages.Count} apps.\n";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load installed apps: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Button Handlers

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshInstalledApps();
        }

        private async void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog ofd = new OpenFileDialog
            {
                Filter = "APK Files (*.apk)|*.apk",
                Title = "Select APK to Install"
            };
            if (ofd.ShowDialog() == true)
            {
                try
                {
                    TxtStatus.Text += $"Installing {ofd.FileName}...\n";
                    var output = await AdbHelper.RunAdbCaptureAsync($"-s {_device} install \"{ofd.FileName}\"");
                    TxtStatus.Text += output + "\n";
                    RefreshInstalledApps();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to install app: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (CmbAndroidApps.SelectedItem == null) return;

            var package = CmbAndroidApps.SelectedItem.ToString();
            if (MessageBox.Show($"Are you sure you want to uninstall {package}?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                try
                {
                    TxtStatus.Text += $"Uninstalling {package}...\n";
                    var output = await AdbHelper.RunAdbCaptureAsync($"-s {_device} uninstall {package}");
                    TxtStatus.Text += output + "\n";
                    RefreshInstalledApps();
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to uninstall app: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            if (CmbAndroidApps.SelectedItem == null) return;

            var package = CmbAndroidApps.SelectedItem.ToString();
            try
            {
                TxtStatus.Text += $"Opening {package}...\n";
                var output = await AdbHelper.RunAdbCaptureAsync($"-s {_device} shell monkey -p {package} -c android.intent.category.LAUNCHER 1");
                TxtStatus.Text += output + "\n";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open app: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion


        #region Drag and Drop APK Installation

        private void UserControl_DragOver(object sender, DragEventArgs e)
        {
            // Only allow files with .apk
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files.All(f => f.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)))
                {
                    e.Effects = DragDropEffects.Copy;
                }
                else
                {
                    e.Effects = DragDropEffects.None;
                }
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private async void UserControl_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            foreach (var file in files)
            {
                if (file.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        TxtStatus.Text += $"Installing {file} via drag-and-drop...\n";
                        var output = await AdbHelper.RunAdbCaptureAsync($"-s {_device} install \"{file}\"");
                        TxtStatus.Text += output + "\n";
                        RefreshInstalledApps();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to install app: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                else
                {
                    TxtStatus.Text += $"Skipped non-APK file: {file}\n";
                }
            }
        }

        #endregion
        private void LoadConfiguration()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Snail",
                "config.json"
            );

            config = ConfigManager.Load(configPath);


            // Load button colors from config
            try
            {
                Application.Current.Resources["ButtonBackground"] =
                    (SolidColorBrush)new BrushConverter().ConvertFromString(config.ButtonStyle.Background);
                Application.Current.Resources["ButtonForeground"] =
                    (SolidColorBrush)new BrushConverter().ConvertFromString(config.ButtonStyle.Foreground);
                Application.Current.Resources["ButtonHover"] =
                    (SolidColorBrush)new BrushConverter().ConvertFromString(config.ButtonStyle.Hover);
            }
            catch
            {
                // fallback to defaults if invalid color strings
                Application.Current.Resources["ButtonBackground"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#5539cc"));
                Application.Current.Resources["ButtonForeground"] = new SolidColorBrush(Colors.White);
                Application.Current.Resources["ButtonHover"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#553fff"));
            }
        }
        public void SetDevice(string device)
        {
            _device = device;
            RefreshInstalledApps();
        }
    }
}
