using System.Windows;

namespace phone_utils
{
    public partial class WirelessPairingWindow : Window
    {
        public string PairingAddress { get; set; } = "";
        public string PairingCode { get; set; } = "";
        public string PairedPort { get; private set; } = "";

        public WirelessPairingWindow(string defaultIp)
        {
            InitializeComponent();
            PairingAddress = defaultIp + ":";
            DataContext = this;
        }

        private async void BtnPair_Click(object sender, RoutedEventArgs e)
        {
            TxtStatus.Text = "Pairing...";
            TxtStatus.Foreground = System.Windows.Media.Brushes.Yellow;

            string address = TxtPairingAddress.Text.Trim();
            string code = TxtPairingCode.Text.Trim();

            if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(code))
            {
                TxtStatus.Text = "Please enter both address and code.";
                TxtStatus.Foreground = System.Windows.Media.Brushes.Red;
                return;
            }

            // Pair
            string pairResult = await AdbHelper.RunAdbCaptureAsync($"pair {address} {code}");
            if (pairResult.Contains("Successfully paired"))
            {
                TxtStatus.Text = "Paired successfully! Connecting...";
                TxtStatus.Foreground = System.Windows.Media.Brushes.Green;

                // Now we need the actual connect port. 
                // The user usually has to look at the main Wireless Debugging screen for the connect port (it's different from pairing port).
                // But let's ask the user for the connect port now, or try to infer if possible?
                // Actually, Android 11+ Wireless Debugging changes the port every time you toggle it.
                // But once paired, it should be listed in 'adb devices' if we are lucky? No, we still need to 'adb connect'.
                
                // Wait, the requirement says: "then ask for the port no need to ask the IP as we already detect it then after it is paired we save the port"
                // The pairing flow usually involves a pairing port AND a connect port.
                // The prompt implies we should ask for the port.
                
                // Let's prompt for the connect port now.
                var portDialog = new InputDialog("Enter Wireless Debugging Port", "Enter the port shown on the main Wireless Debugging screen (NOT the pairing port):");
                if (portDialog.ShowDialog() == true)
                {
                    string connectPort = portDialog.InputText;
                    string ip = address.Split(':')[0];
                    string connectAddress = $"{ip}:{connectPort}";

                    string connectResult = await AdbHelper.RunAdbCaptureAsync($"connect {connectAddress}");
                    if (connectResult.Contains("connected"))
                    {
                        PairedPort = connectPort;
                        DialogResult = true;
                        Close();
                    }
                    else
                    {
                        TxtStatus.Text = $"Connection failed: {connectResult}";
                        TxtStatus.Foreground = System.Windows.Media.Brushes.Red;
                    }
                }
                else
                {
                     TxtStatus.Text = "Port entry cancelled.";
                     TxtStatus.Foreground = System.Windows.Media.Brushes.Red;
                }
            }
            else
            {
                TxtStatus.Text = $"Pairing failed: {pairResult}";
                TxtStatus.Foreground = System.Windows.Media.Brushes.Red;
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
