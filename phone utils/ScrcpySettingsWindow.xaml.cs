using System.Windows;

namespace phone_utils
{
    public partial class ScrcpySettingsWindow : Window
    {
        private readonly MainWindow _main;
        private readonly AppConfig _config;
        private readonly bool _isUsb; // indicate whether this window is editing autorun or auto usb

        public ScrcpySettingsWindow(MainWindow main, AppConfig config, bool isUsb)
        {
            InitializeComponent();
            _main = main;
            _config = config;
            _isUsb = isUsb;

            BtnSave.Click += BtnSave_Click;
            BtnCancel.Click += (s, e) => this.DialogResult = false;
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            // Build arguments like ScrcpyControl.BuildScrcpyArgs but using local controls
            var args = new List<string>();
            args.Add($"--window-title=\"{_config.SelectedDeviceName}\"");

            if (ChkAudioOnly.IsChecked == true)
            {
                args.Add("--no-video");
                args.Add("--no-window");
                args.Add("--audio-source=playback");
            }

            if (ChkNoAudio.IsChecked == true) args.Add("--no-audio");
            if (ChkPlaybackAudio.IsChecked == true) args.Add("--audio-source=playback");
            if (Chkaudiobuffer.IsChecked == true && int.TryParse(TxtAudioBuffer.Text, out int audioBuffer) && audioBuffer > 0)
                args.Add($"--audio-buffer={audioBuffer}");
            if (Chkvideobuffer.IsChecked == true && int.TryParse(TxtVideoBuffer.Text, out int videoBuffer) && videoBuffer > 0)
                args.Add($"--video-buffer={videoBuffer}");
            if (ChkStayAwake.IsChecked == true) args.Add("--stay-awake");
            if (ChkTurnScreenOff.IsChecked == true) args.Add("--turn-screen-off");
            if (ChkLockAfterExit.IsChecked == true) args.Add("--power-off-on-close");
            if (ChkTop.IsChecked == true) args.Add("--always-on-top");
            if (ChkMaxSize.IsChecked == true && int.TryParse(TxtMaxSize.Text, out int maxSize) && maxSize > 0)
                args.Add($"--max-size={maxSize}");

            var final = string.Join(" ", args);

            if (_isUsb)
            {
                _config.AutoUsbStart.Arguments = final;
                _config.AutoUsbStart.Enabled = true; // enabling when user saves
            }
            else
            {
                _config.AutorunStart.Arguments = final;
                _config.AutorunStart.Enabled = true;
            }

            // persist
            string path = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Snail", "config.json");
            ConfigManager.Save(path, _config);

            // close
            this.DialogResult = true;
        }

        private void ChkPlaybackAudio_Checked(object sender, RoutedEventArgs e)
        {

        }
    }
}
