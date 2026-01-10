using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace phone_utils
{
    public partial class NotificationControl : UserControl
    {
        private MainWindow _main;
        private string _currentDevice;
        private DispatcherTimer refreshTimer;
        private CancellationTokenSource _cts;

        public NotificationControl(MainWindow main, string currentDevice)
        {
            InitializeComponent();
            _main = main;
            _currentDevice = currentDevice;

            // Set background color from config
            try
            {
                string configPath = System.IO.Path.Combine(
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                    "Snail",
                    "config.json"
                );
                var config = ConfigManager.Load(configPath);
                Application.Current.Resources["AppBackgroundColor"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(config.ButtonStyle.BackgroundColor ?? "#111111"));
            }
            catch { Application.Current.Resources["AppBackgroundColor"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#111111")); }

            _cts = new CancellationTokenSource();

            // Stop the timer when the control is unloaded
            this.Unloaded += NotificationControl_Unloaded;

            // Auto-refresh every 60 seconds
            refreshTimer = new DispatcherTimer();
            refreshTimer.Interval = TimeSpan.FromSeconds(60);
            // Use non-async event handler and fire-and-forget the Task so exceptions are observed inside the Task
            refreshTimer.Tick += (s, e) => _ = LoadNotificationsAsync(_cts.Token);
            refreshTimer.Start();

            // Initial load (fire-and-forget)
            _ = LoadNotificationsAsync(_cts.Token);
        }

        private void NotificationControl_Unloaded(object sender, RoutedEventArgs e)
        {
            refreshTimer?.Stop();
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
            }
            catch { }
        }


        private async void BtnRefreshNotifications_Click(object sender, RoutedEventArgs e)
        {
            await LoadNotificationsAsync(_cts?.Token ?? CancellationToken.None);
        }

        private async Task LoadNotificationsAsync(CancellationToken ct = default)
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                StatusText.Text = "Loading notifications...";

                var notifications = await GetNotificationsFromDevice(ct).ConfigureAwait(false);

                ct.ThrowIfCancellationRequested();

                // Ensure UI update happens on dispatcher
                await Dispatcher.InvokeAsync(() =>
                {
                    DisplayNotifications(notifications);
                    StatusText.Text = $"Found {notifications.Count} notifications";
                });
            }
            catch (OperationCanceledException)
            {
                // Quietly ignore cancellations
                await Dispatcher.InvokeAsync(() => StatusText.Text = "Notification load cancelled");
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(() => StatusText.Text = $"Error loading notifications: {ex.Message}");
            }
        }


        private async Task<List<NotificationInfo>> GetNotificationsFromDevice(CancellationToken ct = default)
        {
            var notifications = new List<NotificationInfo>();

            try
            {
                ct.ThrowIfCancellationRequested();

                // Get notification dump from Android
                string output = await AdbHelper.RunAdbCaptureAsync($"-s {_currentDevice} shell dumpsys notification --noredact");

                ct.ThrowIfCancellationRequested();

                // Parse the notification dump off the UI thread
                notifications = await Task.Run(() => ParseNotificationDump(output), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debugger.show($"GetNotificationsFromDevice failed: {ex.Message}");
                throw new Exception("Unable to retrieve notifications from device", ex);
            }

            return notifications;
        }

        private List<NotificationInfo> ParseNotificationDump(string dump)
        {
            var notifications = new List<NotificationInfo>();

            var blocks = dump.Split(new[] { "NotificationRecord(" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var block in blocks)
            {
                string packageName = "(No package)";
                string title = "(No title)";
                string text = "(No content)";
                DateTime timestamp = DateTime.Now;

                var lines = block.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

                // Header line: extract package
                if (lines.Length > 0)
                {
                    var headerParts = lines[0].Trim().Split(':');
                    if (headerParts.Length > 1)
                        packageName = CleanPackageName(headerParts[1].Trim());
                }

                bool insideExtras = false;

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();

                    if (trimmed.StartsWith("android.title=") || trimmed.StartsWith("title="))
                    {
                        title = CleanNotificationTextBruteForce(trimmed.Substring(trimmed.IndexOf('=') + 1));
                    }
                    else if (trimmed.StartsWith("android.text=") || trimmed.StartsWith("text="))
                    {
                        text = CleanNotificationTextBruteForce(trimmed.Substring(trimmed.IndexOf('=') + 1));
                    }
                    else if (trimmed.StartsWith("when="))
                    {
                        if (long.TryParse(trimmed.Substring(5), out long t))
                            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(t).DateTime;
                    }
                    else if (trimmed.StartsWith("extras=Bundle"))
                    {
                        insideExtras = true;
                        continue;
                    }

                    // Inside extras bundle
                    if (insideExtras)
                    {
                        if (trimmed == "}") // End of extras
                        {
                            insideExtras = false;
                            continue;
                        }

                        int eq = trimmed.IndexOf('=');
                        if (eq > 0)
                        {
                            var key = trimmed.Substring(0, eq).Trim();
                            var val = trimmed.Substring(eq + 1).Trim();

                            if (key == "android.title") title = CleanNotificationTextBruteForce(val);
                            else if (key == "android.text") text = CleanNotificationTextBruteForce(val);
                            else if (key == "pkg") packageName = CleanPackageName(val);
                        }
                    }
                }

                // Ensure system notifications have proper title
                if (string.Equals(packageName, "android", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(title))
                {
                    title = "android";
                }

                // Skip notifications with no title and no text
                if (title == "(No title)" && text == "(No content)")
                    continue;

                notifications.Add(new NotificationInfo
                {
                    PackageName = packageName,
                    Title = title,
                    Text = text,
                    Timestamp = timestamp
                });
            }

            return notifications
                .OrderByDescending(n => n.Timestamp)
                .Take(20)
                .ToList();
        }

        /// <summary>
        /// Super-aggressive brute-force cleaner for titles and texts.
        /// Removes any word containing "string" or "spannablestring" (case-insensitive),
        /// object references, braces, parentheses, quotes, leaving only visible text.
        /// </summary>
        private string CleanNotificationTextBruteForce(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return input;

            input = input.Trim();

            var words = input.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                             .Where(w => !w.ToLower().Contains("string") && !w.ToLower().Contains("spannablestring"))
                             .Select(w =>
                             {
                                 // Remove any leftover braces, parentheses, quotes, or @hashcodes
                                 w = w.Replace("{", "").Replace("}", "")
                                      .Replace("(", "").Replace(")", "")
                                      .Replace("\"", "");
                                 int atIdx = w.IndexOf('@');
                                 if (atIdx >= 0) w = w.Substring(0, atIdx);
                                 return w;
                             })
                             .Where(w => !string.IsNullOrWhiteSpace(w));

            return string.Join(" ", words).Trim();
        }

        // Cleans package names by removing extra metadata, object references, trailing slashes, and spaces.
        private string CleanPackageName(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "(No package)";

            // Remove object references like @abcdef
            int atIdx = input.IndexOf('@');
            if (atIdx >= 0) input = input.Substring(0, atIdx);

            // Split by space and take first segment
            input = input.Split(' ')[0];

            // Trim leftover slashes or colons
            input = input.Trim().TrimEnd('/', ':');

            return input;
        }

        private void DisplayNotifications(List<NotificationInfo> notifications)
        {
            NotificationPanel.Children.Clear();

            if (!notifications.Any())
            {
                var noNotifLabel = new TextBlock
                {
                    Text = "No notifications found",
                    Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                    FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 50, 0, 0)
                };
                NotificationPanel.Children.Add(noNotifLabel);
                return;
            }

            foreach (var notification in notifications)
            {
                var notifCard = CreateNotificationCard(notification);
                NotificationPanel.Children.Add(notifCard);
            }
        }

        private Border CreateNotificationCard(NotificationInfo notification)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(31, 31, 31)),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(12)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // App icon placeholder
            var iconBorder = new Border
            {
                Width = 32,
                Height = 32,
                Background = GetAppIconColor(notification.PackageName),
                CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            };


            var iconText = new TextBlock
            {
                Text = GetAppIconText(notification.PackageName),
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            iconBorder.Child = iconText;
            Grid.SetColumn(iconBorder, 0);
            Grid.SetRow(iconBorder, 0);

            // App name and time
            var headerStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(8, 0, 0, 4)
            };

            var appNameText = new TextBlock
            {
                Text = GetAppDisplayName(notification.PackageName),
                Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                FontSize = 12,
                FontWeight = FontWeights.Medium
            };

            var timeText = new TextBlock
            {
                Text = FormatTimestamp(notification.Timestamp),
                Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0)
            };

            headerStack.Children.Add(appNameText);
            headerStack.Children.Add(timeText);
            Grid.SetColumn(headerStack, 1);
            Grid.SetRow(headerStack, 0);

            // Title
            var titleText = new TextBlock
            {
                Text = notification.Title,
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeights.Medium,
                Margin = new Thickness(8, 0, 0, 2),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(titleText, 1);
            Grid.SetRow(titleText, 1);

            // Content
            var contentText = new TextBlock
            {
                Text = notification.Text,
                Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                FontSize = 13,
                Margin = new Thickness(8, 0, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(contentText, 1);
            Grid.SetRow(contentText, 2);

            grid.Children.Add(iconBorder);
            grid.Children.Add(headerStack);
            grid.Children.Add(titleText);
            grid.Children.Add(contentText);

            card.Child = grid;
            return card;
        }

        private SolidColorBrush GetAppIconColor(string packageName)
        {
            var colors = new[]
            {
                Color.FromRgb(76, 175, 80),   // Green
                Color.FromRgb(33, 150, 243),  // Blue
                Color.FromRgb(255, 152, 0),   // Orange
                Color.FromRgb(156, 39, 176),  // Purple
                Color.FromRgb(244, 67, 54),   // Red
                Color.FromRgb(0, 150, 136)    // Teal
            };

            int hash = packageName.GetHashCode();
            return new SolidColorBrush(colors[Math.Abs(hash) % colors.Length]);
        }

        private string GetAppIconText(string packageName)
        {
            var parts = packageName.Split('.');
            if (parts.Length > 0)
            {
                var lastPart = parts.Last();
                return lastPart.Substring(0, Math.Min(2, lastPart.Length)).ToUpper();
            }
            return "AP";
        }

        private string GetAppDisplayName(string packageName)
        {
            var appNames = new Dictionary<string, string>
            {
                {"com.whatsapp", "WhatsApp"},
                {"com.instagram.android", "Instagram"},
                {"com.facebook.katana", "Facebook"},
                {"com.twitter.android", "Twitter"},
                {"com.snapchat.android", "Snapchat"},
                {"com.google.android.gm", "Gmail"},
                {"com.spotify.music", "Spotify"},
                {"com.discord", "Discord"},
                {"com.telegram.messenger", "Telegram"},
                {"com.microsoft.teams", "Teams"}
            };

            if (appNames.TryGetValue(packageName, out string displayName))
                return displayName;

            // Extract app name from package
            var parts = packageName.Split('.');
            return parts.Length > 0 ? char.ToUpper(parts.Last()[0]) + parts.Last().Substring(1) : packageName;
        }

        private string FormatTimestamp(DateTime timestamp)
        {
            var now = DateTime.Now;
            var diff = now - timestamp;

            if (diff.TotalMinutes < 1)
                return "now";
            else if (diff.TotalMinutes < 60)
                return $"{(int)diff.TotalMinutes}m ago";
            else if (diff.TotalHours < 24)
                return $"{(int)diff.TotalHours}h ago";
            else
                return timestamp.ToString("MMM d");
        }
    }

    public class NotificationInfo
    {
        public string PackageName { get; set; }
        public string Title { get; set; }
        public string Text { get; set; }
        public DateTime Timestamp { get; set; }
    }
}