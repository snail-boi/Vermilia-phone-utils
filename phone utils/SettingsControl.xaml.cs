using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using YourApp;


namespace phone_utils
{
    public partial class SettingsControl : UserControl
    {
        private readonly MainWindow _main;
        private AppConfig _config;
        private string _currentDevice;
        private readonly string configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Phone Utils",
            "config.json"
        );

        private bool _isInitializing = true;
        private CoverCacheManager _coverCacheManager;

        public SettingsControl(MainWindow main, string currentdevice)
        {
            InitializeComponent();
            _main = main;
            _currentDevice = currentdevice;

            Debugger.show("SettingsControl initialized. Loading configuration from: " + configPath);
            _config = ConfigManager.Load(configPath);
            _coverCacheManager = new CoverCacheManager(_config.Paths.FfmpegPath, _config.Paths.CoverCachePath);
            ApplyConfigToUI();
            InitializeUpdateIntervalUI();
            _isInitializing = false;
            LoadThemesIntoComboBox();

            // wire up buttons
            BtnAutorunStartSettings.Click += BtnAutorunStartSettings_Click;
            BtnAutoUsbStartSettings.Click += BtnAutoUsbStartSettings_Click;
            BtnClearCoverCache.Click += BtnClearCoverCache_Click;

            // Use checkboxes for enabling autorun/autoUSB
            if (this.FindName("ChkAutorunStart") is CheckBox chkAutorun)
            {
                chkAutorun.Checked += ChkAutorunStart_Checked;
                chkAutorun.Unchecked += ChkAutorunStart_Unchecked;
            }
            if (this.FindName("ChkAutoUsbStart") is CheckBox chkAutoUsb)
            {
                chkAutoUsb.Checked += ChkAutoUsbStart_Checked;
                chkAutoUsb.Unchecked += ChkAutoUsbStart_Unchecked;
            }
        }

        private void BtnSetRemoteRoot_Click(object sender, RoutedEventArgs e)
        {
            var picker = new RemoteFolderPicker(_currentDevice);
            picker.Owner = Window.GetWindow(this);
            if (picker.ShowDialog() == true)
            {
                TxtMusicRemoteRoot.Text = picker.SelectedFolder;
                _config.SpecialOptions.MusicRemoteRoot = picker.SelectedFolder;
            }
            SaveConfig(true);
        }

        private void BtnClearCoverCache_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _coverCacheManager?.ClearCache();
                MessageBox.Show("Cover cache cleared.", "Cache Cleared", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Debugger.show("BtnClearCoverCache_Click failed: " + ex.Message);
                MessageBox.Show($"Failed to clear cover cache: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void InitializeUpdateIntervalUI()
        {
            // Ensure UI element exists in XAML: we'll look for CmbUpdateInterval
            try
            {
                if (this.FindName("CmbUpdateInterval") is ComboBox cmb)
                {
                    cmb.ItemsSource = new[] { "Extreme (1s)", "Fast (5s)", "Medium (15s)", "Slow (30s)", "No automatic update" };
                    int mode = (int)_config.UpdateIntervalMode;
                    if (mode < 1 || mode > 5) mode = 3;
                    cmb.SelectedIndex = mode - 1;
                    cmb.SelectionChanged += CmbUpdateInterval_SelectionChanged;
                }
            }
            catch { }
        }

        private void CmbUpdateInterval_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            if (!(sender is ComboBox cmb)) return;
            int sel = cmb.SelectedIndex;
            if (sel < 0) return;
            _config.UpdateIntervalMode = (UpdateIntervalMode)(sel + 1);
            SaveConfig();
        }

        private void LoadThemesIntoComboBox()
        {
            if (_config?.Themes == null || _config.Themes.Count == 0)
            {
                CmbThemes.ItemsSource = null;
                return;
            }

            CmbThemes.ItemsSource = _config.Themes;
            CmbThemes.DisplayMemberPath = "Name";

            // Select the theme that matches the current ButtonStyle
            var currentTheme = _config.Themes.FirstOrDefault(t =>
                t.Background == _config.ButtonStyle.Background &&
                t.Foreground == _config.ButtonStyle.Foreground &&
                t.Hover == _config.ButtonStyle.Hover &&
                t.BackgroundColor == _config.ButtonStyle.BackgroundColor);

            if (currentTheme != null)
                CmbThemes.SelectedItem = currentTheme;
            else
                CmbThemes.SelectedIndex = 0; // fallback if no match
        }

        private void ApplyConfigToUI()
        {
            Debugger.show("Applying configuration to UI in SettingsControl.");

            // Apply button colors
            ApplyButtonColors(_config.ButtonStyle);

            // Set background color swatch
            BtnPickBackgroundColor.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_config.ButtonStyle.BackgroundColor ?? "#111111"));

            // Set checkboxes
            ChkDevmode.IsChecked = _config.SpecialOptions.DevMode;
            ChkMusicPresence.IsChecked = _config.SpecialOptions.MusicPresence;
            ChkDebugMode.IsChecked = _config.SpecialOptions.DebugMode;

            // Battery warning settings
            ShowBatteryWarnings.IsChecked = _config.BatteryWarningSettings.ShowWarning;
            ShowBatteryChargeWarnings.IsChecked = _config.BatteryWarningSettings.chargingwarningenabled;
            WattThreshholdEnabled.IsChecked = _config.BatteryWarningSettings.wattthresholdenabled;
            WattThreshhold.Text = _config.BatteryWarningSettings.wattthreshold.ToString();
            FirstWarningEnabled.IsChecked = _config.BatteryWarningSettings.firstwarningenabled;
            FirstWarning.Text = _config.BatteryWarningSettings.firstwarning.ToString();
            SecondWarningEnabled.IsChecked = _config.BatteryWarningSettings.secondwarningenabled;
            SecondWarning.Text = _config.BatteryWarningSettings.secondwarning.ToString();
            ThirdWarningEnabled.IsChecked = _config.BatteryWarningSettings.thirdwarningenabled;
            ThirdWarning.Text = _config.BatteryWarningSettings.thirdwarning.ToString();
            ShutdownWarningEnabled.IsChecked = _config.BatteryWarningSettings.shutdownwarningenabled;
            FinalWarning.Text = _config.BatteryWarningSettings.shutdownwarning.ToString();
            EmergencyShutdown.IsChecked = _config.BatteryWarningSettings.emergencydisconnectenabled;
            TxtMusicRemoteRoot.Text = _config.SpecialOptions.MusicRemoteRoot;

            // Update autorun checkbox state
            if (this.FindName("ChkAutorunStart") is CheckBox chkA) chkA.IsChecked = _config.AutorunStart != null && _config.AutorunStart.Enabled;
            if (this.FindName("ChkAutoUsbStart") is CheckBox chkU) chkU.IsChecked = _config.AutoUsbStart != null && _config.AutoUsbStart.Enabled;

            // Apply button colors
            Application.Current.Resources["ButtonBackground"] = (SolidColorBrush)new BrushConverter().ConvertFromString(_config.ButtonStyle.Background);
            Application.Current.Resources["ButtonForeground"] = (SolidColorBrush)new BrushConverter().ConvertFromString(_config.ButtonStyle.Foreground);
            Application.Current.Resources["ButtonHover"] = (SolidColorBrush)new BrushConverter().ConvertFromString(_config.ButtonStyle.Hover);

            Debugger.show("Saved settings applied."); // Confirm settings applied
        }

        private void ApplyButtonColors(ButtonStyleConfig style)
        {
            Debugger.show("Applying button colors.");
            Application.Current.Resources["ButtonBackground"] = (SolidColorBrush)new BrushConverter().ConvertFromString(style.Background);
            Application.Current.Resources["ButtonForeground"] = (SolidColorBrush)new BrushConverter().ConvertFromString(style.Foreground);
            Application.Current.Resources["ButtonHover"] = (SolidColorBrush)new BrushConverter().ConvertFromString(style.Hover);
            Application.Current.Resources["MainBackgroundColor"] = (SolidColorBrush)new BrushConverter().ConvertFromString(style.BackgroundColor ?? "#111111");

            // Set the color swatches for the pickers
            BtnPickButtonBackground.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(style.Background));
            BtnPickButtonText.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(style.Foreground));
            BtnPickButtonHover.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(style.Hover));
            BtnPickBackgroundColor.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(style.BackgroundColor ?? "#111111"));
        }

        private void BtnSaveTheme_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ThemeNameDialog
            {
                Owner = Window.GetWindow(this)
            };

            if (dialog.ShowDialog() == true)
            {
                var newTheme = new ThemesConfig
                {
                    Name = dialog.ThemeName,
                    Foreground = _config.ButtonStyle.Foreground,
                    Background = _config.ButtonStyle.Background,
                    Hover = _config.ButtonStyle.Hover,
                    BackgroundColor = _config.ButtonStyle.BackgroundColor
                };

                // ✅ Add the new theme to your theme list
                _config.Themes ??= new List<ThemesConfig>();
                _config.Themes.Add(newTheme);

                MessageBox.Show($"Theme '{newTheme.Name}' saved successfully!", "Success",
                                MessageBoxButton.OK, MessageBoxImage.Information);
                SaveConfig();
            }
        }

        private void CmbThemes_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbThemes.SelectedItem is ThemesConfig selectedTheme)
            {
                _config.ButtonStyle.Foreground = selectedTheme.Foreground;
                _config.ButtonStyle.Background = selectedTheme.Background;
                _config.ButtonStyle.Hover = selectedTheme.Hover;
                _config.ButtonStyle.BackgroundColor = selectedTheme.BackgroundColor;
                SaveConfig();
                ApplyTheme(selectedTheme);
                _main.ReloadConfiguration();
                LoadThemesIntoComboBox();
            }
        }

        private void ApplyTheme(ThemesConfig theme)
        {
            if (theme == null) return;

            // Just copy the values to ButtonStyle
            _config.ButtonStyle.Foreground = theme.Foreground;
            _config.ButtonStyle.Background = theme.Background;
            _config.ButtonStyle.Hover = theme.Hover;
            _config.ButtonStyle.BackgroundColor = theme.BackgroundColor;
        }


        private void BtnDeleteTheme_Click(object sender, RoutedEventArgs e)
        {
            if (CmbThemes.SelectedItem is ThemesConfig selectedTheme)
            {
                var result = MessageBox.Show(
                    $"Are you sure you want to delete the theme '{selectedTheme.Name}'?",
                    "Confirm Delete",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning
                );

                if (result == MessageBoxResult.Yes)
                {
                    // Remove the selected theme
                    _config.Themes.Remove(selectedTheme);

                    // Save config
                    SaveConfig();

                    // Reload the themes in ComboBox
                    LoadThemesIntoComboBox();

                    // Optionally, reset button style to default if the deleted theme was selected
                    if (_config.ButtonStyle.Background == selectedTheme.Background &&
                        _config.ButtonStyle.Foreground == selectedTheme.Foreground &&
                        _config.ButtonStyle.Hover == selectedTheme.Hover &&
                        _config.ButtonStyle.BackgroundColor == selectedTheme.BackgroundColor)

                    {
                        // Reset to first theme or defaults
                        if (_config.Themes.Count > 0)
                        {
                            ApplyTheme(_config.Themes[0]);
                        }
                        else
                        {
                            _config.ButtonStyle = new ButtonStyleConfig
                            {
                                Background = "#FFFFFF",
                                Foreground = "#000000",
                                Hover = "#CCCCCC",
                                BackgroundColor = "#111111"
                            };
                            ApplyButtonColors(_config.ButtonStyle);
                        }
                    }

                    MessageBox.Show($"Theme '{selectedTheme.Name}' deleted successfully.", "Deleted", MessageBoxButton.OK, MessageBoxImage.Information);
                    LoadThemesIntoComboBox();
                }
            }
            else
            {
                MessageBox.Show("Please select a theme to delete.", "No Theme Selected", MessageBoxButton.OK, MessageBoxImage.Exclamation);
            }
        }


        #region Color Pickers
        // Opens a color picker dialog and applies the selected color using the provided action
        private void PickColor(SolidColorBrush currentBrush, Action<Color> apply)
        {
            Debugger.show("Opening color picker.");
            var picker = new ColorPickerWindow(currentBrush.Color);
            if (picker.ShowDialog() == true)
            {
                apply(picker.SelectedColor);
                SaveConfig();
            }
        }

        // Button background color picker
        private void BtnPickButtonBackground_Click(object sender, RoutedEventArgs e) =>
            PickColor(BtnPickButtonBackground.Background as SolidColorBrush, c =>
            {
                Application.Current.Resources["ButtonBackground"] = new SolidColorBrush(c);
                BtnPickButtonBackground.Background = new SolidColorBrush(c);
                _config.ButtonStyle.Background = c.ToString();
            });

        // Button text color picker
        private void BtnPickButtonText_Click(object sender, RoutedEventArgs e) =>
            PickColor(BtnPickButtonText.Background as SolidColorBrush, c =>
            {
                Application.Current.Resources["ButtonForeground"] = new SolidColorBrush(c);
                BtnPickButtonText.Background = new SolidColorBrush(c);
                _config.ButtonStyle.Foreground = c.ToString();
            });

        // Button hover color picker
        private void BtnPickButtonHover_Click(object sender, RoutedEventArgs e) =>
            PickColor(BtnPickButtonHover.Background as SolidColorBrush, c =>
            {
                Application.Current.Resources["ButtonHover"] = new SolidColorBrush(c);
                BtnPickButtonHover.Background = new SolidColorBrush(c);
                _config.ButtonStyle.Hover = c.ToString();
            });

        // Background color picker
        private void BtnPickBackgroundColor_Click(object sender, RoutedEventArgs e) =>
            PickColor(BtnPickBackgroundColor.Background as SolidColorBrush, c =>
            {
                BtnPickBackgroundColor.Background = new SolidColorBrush(c);
                _config.ButtonStyle.BackgroundColor = c.ToString();
                SaveConfig();
                // Also update main window background immediately
                var mainWindow = Window.GetWindow(this) as MainWindow;
                if (mainWindow != null)
                {
                    var bgRect = mainWindow.FindName("MainBackgroundRect") as System.Windows.Shapes.Rectangle;
                    if (bgRect != null)
                        bgRect.Fill = new SolidColorBrush(c);
                }
            });
        #endregion

        #region DevMode
        private void ChkDevmode_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            _config.SpecialOptions.DevMode = true;
            var result = MessageBox.Show(
                "Are you sure you want to enable devmode\n this has extra features, but they only work when specific apps are installed",
                "",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            );
            Debugger.show("DevMode checked. User confirmation: " + result);

            if (result == MessageBoxResult.Yes)
            {
                SaveConfig();
            }
            else
            {
                ChkDevmode.IsChecked = false;
                _config.SpecialOptions.DevMode = false;
            }
        }
        private void ChkDevmode_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            _config.SpecialOptions.DevMode = false;
            Debugger.show("DevMode unchecked.");
            SaveConfig();
        }
        #endregion
        #region music presence
        private void ChkMusicPresence_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _config.SpecialOptions.MusicPresence = true;
            Debugger.show("MusicPresence checked.");
            SaveConfig();
        }

        private void ChkMusicPresence_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _config.SpecialOptions.MusicPresence = false;
            Debugger.show("MusicPresence unchecked.");
            SaveConfig();
        }
        #endregion
        #region debug mode
        private void ChkDebugMode_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _config.SpecialOptions.DebugMode = true;
            Debugger.show("DebugMode checked.");
            SaveConfig();
        }

        private void ChkDebugMode_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _config.SpecialOptions.DebugMode = false;
            Debugger.show("DebugMode unchecked.");
            SaveConfig();
        }
        #endregion

        #region Battery Settings Handlers
        private void ShowBatteryWarnings_Checked(object sender, RoutedEventArgs e)
        {
            _config.BatteryWarningSettings.ShowWarning = true;
            SaveConfig();
        }
        private void ShowBatteryWarnings_Unchecked(object sender, RoutedEventArgs e)
        {
            _config.BatteryWarningSettings.ShowWarning = false;
            SaveConfig();
        }
        private void ShowBatteryChargeWarnings_Checked(object sender, RoutedEventArgs e)
        {
            _config.BatteryWarningSettings.chargingwarningenabled = true;
            SaveConfig();
        }
        private void ShowBatteryChargeWarnings_Unchecked(object sender, RoutedEventArgs e)
        {
            _config.BatteryWarningSettings.chargingwarningenabled = false;
            SaveConfig();
        }
        private void WattThreshholdEnabled_Checked(object sender, RoutedEventArgs e)
        {
            _config.BatteryWarningSettings.wattthresholdenabled = true;
            SaveConfig();
        }
        private void WattThreshholdEnabled_Unchecked(object sender, RoutedEventArgs e)
        {
            _config.BatteryWarningSettings.wattthresholdenabled = false;
            SaveConfig();
        }
        private void WattThreshhold_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(WattThreshhold.Text, out double value))
                _config.BatteryWarningSettings.wattthreshold = value;
            SaveConfig();
        }
        private void FirstWarningEnabled_Checked(object sender, RoutedEventArgs e)
        {
            _config.BatteryWarningSettings.firstwarningenabled = true;
            SaveConfig();
        }
        private void FirstWarningEnabled_Unchecked(object sender, RoutedEventArgs e)
        {
            _config.BatteryWarningSettings.firstwarningenabled = false;
            SaveConfig();
        }
        private void FirstWarning_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(FirstWarning.Text, out double value))
                _config.BatteryWarningSettings.firstwarning = value;
            SaveConfig();
        }
        private void SecondWarningEnabled_Checked(object sender, RoutedEventArgs e)
        {
            _config.BatteryWarningSettings.secondwarningenabled = true;
            SaveConfig();
        }
        private void SecondWarningEnabled_Unchecked(object sender, RoutedEventArgs e)
        {
            _config.BatteryWarningSettings.secondwarningenabled = false;
            SaveConfig();
        }
        private void SecondWarning_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(SecondWarning.Text, out double value))
                _config.BatteryWarningSettings.secondwarning = value;
            SaveConfig();
        }
        private void ThirdWarningEnabled_Checked(object sender, RoutedEventArgs e)
        {
            _config.BatteryWarningSettings.thirdwarningenabled = true;
            SaveConfig();
        }
        private void ThirdWarningEnabled_Unchecked(object sender, RoutedEventArgs e)
        {
            _config.BatteryWarningSettings.thirdwarningenabled = false;
            SaveConfig();
        }
        private void ThirdWarning_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(ThirdWarning.Text, out double value))
                _config.BatteryWarningSettings.thirdwarning = value;
            SaveConfig();
        }
        private void ShutdownWarningEnabled_Checked(object sender, RoutedEventArgs e)
        {
            _config.BatteryWarningSettings.shutdownwarningenabled = true;
            SaveConfig();
        }
        private void ShutdownWarningEnabled_Unchecked(object sender, RoutedEventArgs e)
        {
            _config.BatteryWarningSettings.shutdownwarningenabled = false;
            SaveConfig();
        }
        private void FinalWarning_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (double.TryParse(FinalWarning.Text, out double value))
                _config.BatteryWarningSettings.shutdownwarning = value;
            SaveConfig();
        }
        private void EmergencyShutdown_Checked(object sender, RoutedEventArgs e)
        {
            _config.BatteryWarningSettings.emergencydisconnectenabled = true;
            SaveConfig();
        }
        private void EmergencyShutdown_Unchecked(object sender, RoutedEventArgs e)
        {
            _config.BatteryWarningSettings.emergencydisconnectenabled = false;
            SaveConfig();
        }
        #endregion

        #region Autorun/AutoUSB handlers
        private void BtnAutorunStartSettings_Click(object sender, RoutedEventArgs e)
        {
            // open scrcpy settings window for Autorun
            var win = new ScrcpySettingsWindow(_main, _config, false)
            {
                Owner = Window.GetWindow(this)
            };
            if (win.ShowDialog() == true)
            {
                // reload config and UI
                _config = ConfigManager.Load(configPath);
                ApplyConfigToUI();
            }
        }

        private void BtnAutoUsbStartSettings_Click(object sender, RoutedEventArgs e)
        {
            // open scrcpy settings window for Auto USB
            var win = new ScrcpySettingsWindow(_main, _config, true)
            {
                Owner = Window.GetWindow(this)
            };
            if (win.ShowDialog() == true)
            {
                _config = ConfigManager.Load(configPath);
                ApplyConfigToUI();
            }
        }

        // Checkbox handlers for autorun/autousb toggles
        private void ChkAutorunStart_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _config.AutorunStart.Enabled = true;
            SaveConfig();
        }

        private void ChkAutorunStart_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _config.AutorunStart.Enabled = false;
            SaveConfig();
        }

        private void ChkAutoUsbStart_Checked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _config.AutoUsbStart.Enabled = true;
            SaveConfig();
        }

        private void ChkAutoUsbStart_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            _config.AutoUsbStart.Enabled = false;
            SaveConfig();
        }
        #endregion

        #region Config Save
        private void SaveConfig(bool showmessage = false)
        {
            try
            {
                string folder = Path.GetDirectoryName(configPath);
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

                ConfigManager.Save(configPath, _config);
                Debugger.show("Configuration saved to: " + configPath);
                _main.ReloadConfiguration();

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
    }
}