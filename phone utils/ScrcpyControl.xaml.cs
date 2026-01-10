using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace phone_utils
{
    public partial class ScrcpyControl : UserControl, IDisposable
    {
        private readonly MainWindow _main;
        private GlobalHotkeyManager _hotkeyManager;
        private bool _hotkeysEnabled = true;
        private bool _suppressEvents = false;

        public ScrcpyControl(MainWindow main)
        {
            InitializeComponent();
            _main = main;

            // Set background color from config
            try
            {
                Application.Current.Resources["AppBackgroundColor"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_main.Config.ButtonStyle.BackgroundColor ?? "#111111"));
            }
            catch { Application.Current.Resources["AppBackgroundColor"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#111111")); }

            BtnStartScrcpy.Click += BtnStartScrcpy_Click;
            this.Loaded += ScrcpyControl_Loaded;


            Debugger.show("ScrcpyControl initialized with device: " + main); // Added to trace initialization
            LoadSavedSettings();

        }

        private async void ScrcpyControl_Loaded(object sender, RoutedEventArgs e)
        {
            Debugger.show("ScrcpyControl loaded, hotkeysEnabled: " + _hotkeysEnabled); // Trace control load
            if (_hotkeysEnabled) InitializeGlobalHotkeys();
            try
            {
                await LoadInstalledApps();
            }
            catch (Exception ex)
            {
                Debugger.show("LoadInstalledApps failed: " + ex.Message);
            }
        }

        #region Hotkeys

        private void InitializeGlobalHotkeys()
        {
            try
            {
                var parentWindow = Window.GetWindow(this);
                if (parentWindow == null) 
                {
                    Debugger.show("Parent window is null, skipping hotkey initialization."); // Trace null parent
                    return;
                }

                _hotkeyManager?.Dispose();
                _hotkeyManager = new GlobalHotkeyManager(parentWindow);

                // Use fire-and-forget wrappers so hotkey manager can keep Action signatures but we avoid async void
                _hotkeyManager.MediaPlayPause += () => _ = RunAdbKeyEvent(85);
                _hotkeyManager.MediaNext += () => _ = RunAdbKeyEvent(87);
                _hotkeyManager.MediaPrev += () => _ = RunAdbKeyEvent(88);
                _hotkeyManager.ShiftVolumeUp += () => _ = RunAdbKeyEvent(24);
                _hotkeyManager.ShiftVolumeDown += () => _ = RunAdbKeyEvent(25);
                _hotkeyManager.InsertPressed += () => _ = OnInsertPressedAsync();
                _hotkeyManager.PageUpPressed += () => _ = LaunchApp("anddea.youtube");
                _hotkeyManager.PageDownPressed += () => _ = LaunchApp("in.krosbits.musicolet");
                _hotkeyManager.EndPressed += () => _ = LaunchApp("com.anilab.android");

                Debugger.show("Global hotkeys initialized."); // Confirm hotkey setup
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize hotkeys: {ex.Message}", "Hotkey Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                Debugger.show("Hotkey initialization error: " + ex.Message); // Trace hotkey exception
            }
        }

        private void UnregisterGlobalHotkeys()
        {
            Debugger.show("Unregistering global hotkeys."); // Trace hotkey cleanup
            _hotkeyManager?.Dispose();
            _hotkeyManager = null;
        }

        private async Task RunAdbKeyEvent(int keyCode)
        {
            Debugger.show("RunAdbKeyEvent called with keyCode: " + keyCode); // Trace key events
            if (!_hotkeysEnabled || string.IsNullOrEmpty(_main.currentDevice)) return;
            try
            {
                await AdbHelper.RunAdbAsync($"-s {_main.currentDevice} shell input keyevent {keyCode}").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debugger.show("RunAdbKeyEvent failed: " + ex.Message);
            }
        }

        private async Task OnInsertPressedAsync()
        {
            Debugger.show("unlocking phone."); // Trace Insert pressed
            if (!_hotkeysEnabled || string.IsNullOrEmpty(_main.currentDevice) || _main.currentDevice.Contains(":")) return;
            try
            {
                await AdbHelper.RunAdbAsync($"-s {_main.currentDevice} shell input text {_main.Config.SelectedDevicePincode}").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debugger.show("OnInsertPressed failed: " + ex.Message);
            }
        }

        private async Task LaunchApp(string packageName)
        {
            Debugger.show("Launching app: " + packageName); // Trace which app is being launched
            if (!_hotkeysEnabled || string.IsNullOrEmpty(_main.currentDevice)) return;
            try
            {
                await AdbHelper.RunAdbAsync($"-s {_main.currentDevice} shell monkey -p {packageName} -c android.intent.category.LAUNCHER 1").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debugger.show("LaunchApp failed: " + ex.Message);
            }
        }

        private void ChkEnableHotkeys_Checked(object sender, RoutedEventArgs e) => ToggleHotkeys(true);
        private void ChkEnableHotkeys_Unchecked(object sender, RoutedEventArgs e) => ToggleHotkeys(false);

        private void ToggleHotkeys(bool enabled)
        {
            Debugger.show("ToggleHotkeys called. Enabled: " + enabled); // Trace toggle action
            _hotkeysEnabled = enabled;
            if (enabled) InitializeGlobalHotkeys();
            else UnregisterGlobalHotkeys();
        }

        #endregion

        #region fuckit random checkbox go
        private void ChkAutoEnable_Checked(object sender, RoutedEventArgs e)
        {
            //this shit should run whenever you open the usercontrol or check the checkbox, so this things should usually save correctly
            var args = BuildScrcpyArgs();
            // previously saved to ScrcpyAutoStart; now we save to AutorunStart if enabled
            if (_main.Config.AutorunStart != null)
            {
                _main.Config.AutorunStart.Arguments = string.Join(" ", args);
            }
            SaveCurrentSettings();
            Debugger.show("ChkAutoEnable_Checked: Auto-start enabled."); // Trace auto-start enable
        }
        #endregion

        #region AudioCheckboxes

        private void ChkAudio_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;

            _suppressEvents = true;
            Debugger.show("ChkAudio_Checked fired for: " + (sender as CheckBox)?.Name); // Trace which checkbox triggered

            switch ((sender as CheckBox)?.Name)
            {
                case "ChkNoAudio":
                    ChkPlaybackAudio.IsChecked = false;
                    ChkAudioOnly.IsChecked = false;
                    Chkaudiobuffer.IsChecked = false;
                    Chkaudiobuffer.IsEnabled = false;
                    break;
                case "ChkPlaybackAudio":
                    ChkNoAudio.IsChecked = false;
                    ChkAudioOnly.IsChecked = false;
                    Chkaudiobuffer.IsEnabled = true;
                    break;
                case "ChkAudioOnly":
                    ChkNoAudio.IsChecked = false;
                    ChkPlaybackAudio.IsChecked = false;
                    Chkaudiobuffer.IsChecked = false;
                    Chkaudiobuffer.IsEnabled = false;
                    break;
            }

            _suppressEvents = false;
        }

        private void ChkAudio_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_suppressEvents) return;

            _suppressEvents = true;
            Debugger.show("ChkAudio_Unchecked fired for: " + (sender as CheckBox)?.Name); // Trace checkbox uncheck

            if (ChkNoAudio.IsChecked == false && ChkPlaybackAudio.IsChecked == false && ChkAudioOnly.IsChecked == false)
            {
                var chk = sender as CheckBox;
                chk.IsChecked = true;
                Debugger.show("Reverting unchecked to true for: " + chk.Name); // Trace auto-reset
            }

            _suppressEvents = false;
        }

        #endregion

        #region Settings

        private void LoadSavedSettings()
        {
            Debugger.show("Loading saved settings..."); // Trace loading settings
            var settings = _main.Config.ScrcpySettings;

            ChkAudioOnly.IsChecked = settings.AudioOnly;
            ChkNoAudio.IsChecked = settings.NoAudio;
            ChkPlaybackAudio.IsChecked = settings.PlaybackAudio;
            ChkMaxSize.IsChecked = settings.LimitMaxSize;
            TxtMaxSize.Text = settings.MaxSize.ToString();
            ChkStayAwake.IsChecked = settings.StayAwake;
            ChkTop.IsChecked = settings.Top;
            ChkTurnScreenOff.IsChecked = settings.TurnScreenOff;
            ChkLockAfterExit.IsChecked = settings.LockPhone;
            ChkEnableHotkeys.IsChecked = settings.EnableHotkeys;
            Chkaudiobuffer.IsChecked = settings.audiobuffer;
            Chkvideobuffer.IsChecked = settings.videobuffer;
            TxtAudioBuffer.Text = settings.AudioBufferSize.ToString();
            TxtVideoBuffer.Text = settings.VideoBufferSize.ToString();
            CmbAndroidApps.Text = settings.VirtualDisplayApp ?? "";
            CmbCameraList.SelectedIndex = settings.CameraType;
            // Auto-start checkbox removed; autorun is managed in SettingsControl now

            _hotkeysEnabled = settings.EnableHotkeys;

            // Apply button colors
            Application.Current.Resources["ButtonBackground"] =
                (SolidColorBrush)new BrushConverter().ConvertFromString(_main.Config.ButtonStyle.Background);
            Application.Current.Resources["ButtonForeground"] =
                (SolidColorBrush)new BrushConverter().ConvertFromString(_main.Config.ButtonStyle.Foreground);
            Application.Current.Resources["ButtonHover"] =
                (SolidColorBrush)new BrushConverter().ConvertFromString(_main.Config.ButtonStyle.Hover);

            Debugger.show("Saved settings applied."); // Confirm settings applied
        }

        private void SaveCurrentSettings()
        {
            Debugger.show("Saving current settings..."); // Trace saving settings
            var settings = _main.Config.ScrcpySettings;

            settings.AudioOnly = ChkAudioOnly.IsChecked == true;
            settings.NoAudio = ChkNoAudio.IsChecked == true;
            settings.PlaybackAudio = ChkPlaybackAudio.IsChecked == true;
            settings.LimitMaxSize = ChkMaxSize.IsChecked == true;
            settings.MaxSize = int.TryParse(TxtMaxSize.Text, out int maxSize) ? maxSize : 2440;
            settings.StayAwake = ChkStayAwake.IsChecked == true;
            settings.TurnScreenOff = ChkTurnScreenOff.IsChecked == true;
            settings.Top = ChkTop.IsChecked == true;
            settings.LockPhone = ChkLockAfterExit.IsChecked == true;
            settings.EnableHotkeys = ChkEnableHotkeys.IsChecked == true;
            settings.audiobuffer = Chkaudiobuffer.IsChecked == true;
            settings.videobuffer = Chkvideobuffer.IsChecked == true;
            settings.AudioBufferSize = int.TryParse(TxtAudioBuffer.Text, out int abuffer) ? abuffer : 50;
            settings.VideoBufferSize = int.TryParse(TxtVideoBuffer.Text, out int vbuffer) ? vbuffer : 50;
            settings.VirtualDisplayApp = CmbAndroidApps.Text;
            settings.CameraType = CmbCameraList.SelectedIndex;
            // Auto-start checkbox removed from this control; settings saved in SettingsControl now

            string configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snail", "config.json");
            if (!Directory.Exists(Path.GetDirectoryName(configPath))) Directory.CreateDirectory(Path.GetDirectoryName(configPath));

            ConfigManager.Save(configPath, _main.Config);
            Debugger.show("Settings saved to " + configPath); // Confirm where settings were saved
        }

        #endregion

        #region Scrcpy Launch

        private async Task LaunchScrcpyAsync(List<string> args)
        {
            Debugger.show("Launching scrcpy with args: " + string.Join(" ", args)); // Trace scrcpy launch args
            SaveCurrentSettings();
            string finalArgs = string.Join(" ", args);
            await RunScrcpyAsync(finalArgs);
        }

        private async void BtnDisplay_click(object sender, RoutedEventArgs e)
        {
            var args = BuildScrcpyArgs(display: true);
            await LaunchScrcpyAsync(args);
        }

        private async void BtnCamera_click(object sender, RoutedEventArgs e)
        {
            var args = BuildScrcpyArgs(camera: true);
            await LaunchScrcpyAsync(args);
        }

        private async void BtnStartScrcpy_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_main.currentDevice))
            {
                MessageBox.Show("No device connected!", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                Debugger.show("BtnStartScrcpy_Click aborted: no device."); // Trace missing device
                return;
            }

            var args = BuildScrcpyArgs();
            await LaunchScrcpyAsync(args);
        }

        private List<string> BuildScrcpyArgs(bool display = false, bool camera = false)
        {
            var args = new List<string>();
            args.Add($"--window-title=\"{_main.Config.SelectedDeviceName}\"");

            Debugger.show("Building scrcpy args. display=" + display + ", camera=" + camera); // Trace arg build

            if (ChkAudioOnly.IsChecked == true && !camera)
            {
                args.Add("--no-video");
                args.Add("--no-window");
                args.Add("--audio-source=playback");
            }

            if (ChkNoAudio.IsChecked == true) args.Add("--no-audio");
            if (ChkPlaybackAudio.IsChecked == true) args.Add("--audio-source=playback");
            if (Chkaudiobuffer.IsChecked == true && int.TryParse(TxtAudioBuffer.Text, out int audioBuffer) && audioBuffer > 0)
                args.Add($"--audio-buffer={audioBuffer}");
            else if (Chkaudiobuffer.IsChecked == true)
                MessageBox.Show("Invalid Audio Buffer value.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (Chkvideobuffer.IsChecked == true && int.TryParse(TxtVideoBuffer.Text, out int videoBuffer) && videoBuffer > 0)
                args.Add($"--video-buffer={videoBuffer}");
            else if (Chkvideobuffer.IsChecked == true)
                MessageBox.Show("Invalid Audio Buffer value.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (ChkStayAwake.IsChecked == true && !camera) args.Add("--stay-awake");
            if (ChkTurnScreenOff.IsChecked == true && !camera) args.Add("--turn-screen-off");
            if (ChkLockAfterExit.IsChecked == true && !camera && !display) args.Add("--power-off-on-close");
            if (ChkTop.IsChecked == true) args.Add("--always-on-top");
            if (ChkMaxSize.IsChecked == true && int.TryParse(TxtMaxSize.Text, out int maxSize) && maxSize > 0)
                args.Add($"--max-size={maxSize}");
            else if (ChkMaxSize.IsChecked == true)
                MessageBox.Show("Invalid Max Size value.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);

            if (display && int.TryParse(TxtMaxSize.Text, out int width))
            {
                args.Add($"--new-display={width}x{width * 9 / 16}");
                if (!string.IsNullOrWhiteSpace(CmbAndroidApps.Text))
                    args.Add($"--start-app={CmbAndroidApps.Text}");
            }


            if (camera)
            {
                args.Add("--video-source=camera");
                switch (CmbCameraList.Text)
                {
                    case "Front": args.Add("--camera-facing=front"); break;
                    case "Back": args.Add("--camera-facing=back"); break;
                    case "External": args.Add("--camera-facing=external"); break;
                }
            }

            Debugger.show("Final scrcpy args: " + string.Join(" ", args)); // Trace final args li
            // Save args to AutorunStart if autorun is enabled
            if (_main.Config.AutorunStart != null && _main.Config.AutorunStart.Enabled)
            {
                _main.Config.AutorunStart.Arguments = string.Join(" ", args); // Save for auto-start settings
            }
            return args;
        }
        public async Task RunScrcpyAsync(string args)
        {
            Debugger.show("RunScrcpyAsync called with args: " + args); // Trace scrcpy async launch
            _main.DeviceStatusText.Text = args.Contains("--no-audio")
                ? "Device Status: Casting without audio"
                : "Device Status: Casting with audio";

            if (!File.Exists(_main.Config.Paths.Scrcpy))
            {
                Debugger.show("scrcpy.exe not found at path: " + _main.Config.Paths.Scrcpy); // Trace missing executable
                MessageBox.Show("scrcpy.exe not found!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _main.Config.Paths.Scrcpy,
                    Arguments = $"-s {_main.currentDevice} {args}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                Debugger.show("Starting scrcpy process with command: " + psi.FileName + " " + psi.Arguments); // Trace process start

                using var scrcpyProcess = Process.Start(psi);
                if (scrcpyProcess == null)
                {
                    Debugger.show("Failed to start scrcpy process.");
                    return;
                }

                await Task.Delay(1000).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() => _main.DeviceStatusText.Text += " - Ready");
                Debugger.show("scrcpy process started successfully."); // Confirm process start

                // Wait for exit asynchronously
                await scrcpyProcess.WaitForExitAsync().ConfigureAwait(false);

                Debugger.show("scrcpy process exited."); // Trace exit
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => MessageBox.Show($"scrcpy launch failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error));
                Debugger.show("scrcpy launch exception: " + ex.Message); // Trace exception
            }
        }
        #endregion

        #region Installed Apps

        private async Task LoadInstalledApps()
        {
            Debugger.show("Loading installed apps..."); // Trace app loading start
            if (string.IsNullOrEmpty(_main.currentDevice))
            {
                Debugger.show("LoadInstalledApps aborted: no device."); // Trace missing device
                return;
            }


            try
            {
                var output = await AdbHelper.RunAdbCaptureAsync($"-s {_main.currentDevice} shell pm list packages");
                var packages = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                                     .Where(l => l.StartsWith("package:"))
                                     .Select(l => l.Substring(8))
                                     .OrderBy(l => l)
                                     .ToList();

                await Dispatcher.InvokeAsync(() => CmbAndroidApps.ItemsSource = packages);
                Debugger.show("Installed apps loaded: " + packages.Count + " apps."); // Confirm loaded apps
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => MessageBox.Show($"Failed to load installed apps: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error));
                Debugger.show("LoadInstalledApps exception: " + ex.Message); // Trace exception
            }
        }

        #endregion

        public void Dispose()
        {
            Debugger.show("Disposing ScrcpyControl, unregistering hotkeys."); // Trace dispose
            UnregisterGlobalHotkeys();
        }
    }
}
