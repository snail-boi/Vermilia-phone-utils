using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Media;


namespace phone_utils
{
    public partial class FileSync : UserControl
    {
        public string CurrentDevice { get; set; } // Device ID
        private CancellationTokenSource _cts;

        public FileSync()
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
            LoadDirectoryPaths();
        }

        private void BrowseRemoteDir_Click(object sender, RoutedEventArgs e)
        {
            var picker = new RemoteFolderPicker(CurrentDevice);
            picker.Owner = Window.GetWindow(this);
            if (picker.ShowDialog() == true)
            {
                RemoteDirTextBox.Text = picker.SelectedFolder;
                SaveDirectoryPaths();
            }
        }

        private void BrowseLocalDir_Click(object sender, RoutedEventArgs e)
        {
            string selectedPath = BrowseFolder();
            if (!string.IsNullOrEmpty(selectedPath))
            {
                LocalDirTextBox.Text = selectedPath;
                SaveDirectoryPaths();
            }
        }

        private string BrowseFolder()
        {
            var dlg = new OpenFileDialog
            {
                CheckFileExists = false,
                CheckPathExists = true,
                FileName = "Select folder",
                ValidateNames = false,
            };

            if (dlg.ShowDialog() == true)
            {
                return Path.GetDirectoryName(dlg.FileName);
            }

            return string.Empty;
        }

        private void LoadDirectoryPaths()
        {
            // Load config from user-writable folder
            string configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Snail",
                "config.json"
            );

            var config = ConfigManager.Load(configPath);

            // Load file sync paths
            LocalDirTextBox.Text = config.FileSync.LocalDir;
            RemoteDirTextBox.Text = config.FileSync.RemoteDir;
            FolderRecursionCheckBox.IsChecked = config.FileSync.recursion;

            // Load button colors
            Application.Current.Resources["ButtonBackground"] =
                (SolidColorBrush)new BrushConverter().ConvertFromString(config.ButtonStyle.Background);
            Application.Current.Resources["ButtonForeground"] =
                (SolidColorBrush)new BrushConverter().ConvertFromString(config.ButtonStyle.Foreground);
            Application.Current.Resources["ButtonHover"] =
                (SolidColorBrush)new BrushConverter().ConvertFromString(config.ButtonStyle.Hover);
        }



        private void SaveDirectoryPaths()
        {
            // Use a user-writable folder
            string configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Snail",
                "config.json"
            );

            // Ensure the folder exists before saving
            string folder = Path.GetDirectoryName(configPath);
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            var config = ConfigManager.Load(configPath);

            config.FileSync.LocalDir = LocalDirTextBox.Text;
            config.FileSync.RemoteDir = RemoteDirTextBox.Text;
            config.FileSync.recursion = FolderRecursionCheckBox.IsChecked == true;

            ConfigManager.Save(configPath, config);
        }



        private void LocalDirTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (IsLoaded)
            {
                SaveDirectoryPaths();
            }
        }

        private void RemoteDirTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (IsLoaded)
            {
                SaveDirectoryPaths();
            }
        }

        private async void StartSyncButton_Click(object sender, RoutedEventArgs e)
        {
            StartSyncButton.IsEnabled = false;
            StartReverseSyncButton.IsEnabled = false;
            StopSyncButton.IsEnabled = true;

            _cts = new CancellationTokenSource();
            try
            {
                await SyncMusicToPcAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                StatusLog.Items.Add("Sync canceled by user.");
                StatusLog.ScrollIntoView(StatusLog.Items[StatusLog.Items.Count - 1]);
            }
            finally
            {
                StartSyncButton.IsEnabled = true;
                StartReverseSyncButton.IsEnabled = true;
                StopSyncButton.IsEnabled = false;
            }
        }
        private async void StartReverseSyncButton_Click(object sender, RoutedEventArgs e)
        {
            StartSyncButton.IsEnabled = false;
            StartReverseSyncButton.IsEnabled = false;
            StopSyncButton.IsEnabled = true;

            _cts = new CancellationTokenSource();
            try
            {
                await SyncMusicToPhoneAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                StatusLog.Items.Add("Sync canceled by user.");
                StatusLog.ScrollIntoView(StatusLog.Items[StatusLog.Items.Count - 1]);
            }
            finally
            {
                StartSyncButton.IsEnabled = true;
                StartReverseSyncButton.IsEnabled = true;
                StopSyncButton.IsEnabled = false;
            }
        }

        private void StopSyncButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
        }



        private static Task<string> RunAdbCaptureAsync(string args)
        {
            return AdbHelper.RunAdbCaptureAsync(args);
        }


        public async Task SyncMusicToPcAsync(CancellationToken token)
        {
            Debugger.show("Entered SyncMusicToPcAsync");

            if (string.IsNullOrEmpty(CurrentDevice))
            {
                Debugger.show("No device selected - exiting SyncMusicToPcAsync");
                MessageBox.Show("No device selected.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string remoteDir = RemoteDirTextBox.Text.Trim();
            string localDir = LocalDirTextBox.Text.Trim();
            Debugger.show($"RemoteDir='{remoteDir}', LocalDir='{localDir}'");

            if (string.IsNullOrEmpty(remoteDir) || string.IsNullOrEmpty(localDir))
            {
                Debugger.show("Remote or local directory missing");
                MessageBox.Show("Please select both local and remote directories.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SaveDirectoryPaths();
            Directory.CreateDirectory(localDir);

            Debugger.show("Fetching remote file list from device...");
            StatusLog.Items.Clear();
            SuccessList.Items.Clear();
            FailedList.Items.Clear();

            StatusLog.Items.Add("Getting file list...");
            StatusLog.ScrollIntoView(StatusLog.Items[StatusLog.Items.Count - 1]);

            string lsCommand = FolderRecursionCheckBox.IsChecked == true ? "ls -lR" : "ls -l";
            string fileListOutput = await RunAdbCaptureAsync($"-s {CurrentDevice} shell {lsCommand} \"{remoteDir}\"");

            Debugger.show("Parsing adb ls output...");
            var lines = fileListOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var lsRegex = new Regex(@"^(?<type>[-d])([rwx-]{9})\s+\d+\s+\S+\s+\S+\s+\d+\s+(?<date>\d{4}-\d{2}-\d{2})\s+(?<time>\d{2}:\d{2})\s+(?<name>.+)$", RegexOptions.Compiled);

            var folderDates = new Dictionary<string, DateTime>();
            var items = new List<(string Path, string Type, DateTime Date)>();
            string currentFolder = remoteDir;

            foreach (var line in lines)
            {
                if (line.EndsWith(":"))
                {
                    currentFolder = line.TrimEnd(':').Trim();
                    continue;
                }

                var match = lsRegex.Match(line);
                if (!match.Success) continue;

                string type = match.Groups["type"].Value;
                string name = match.Groups["name"].Value.Trim();
                if (!DateTime.TryParse($"{match.Groups["date"].Value} {match.Groups["time"].Value}", out DateTime fileDate))
                    fileDate = DateTime.Now;

                string fullRemotePath = $"{currentFolder}/{name}";
                items.Add((fullRemotePath, type, fileDate));

                if (type == "d")
                    folderDates[fullRemotePath] = fileDate;
            }
            Debugger.show($"Total items found: {items.Count}");
            int totalItems = items.Count;
            int currentIndex = 0;

            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                currentIndex++;

                string relativePath = Path.GetRelativePath(remoteDir, item.Path);
                string localPath = Path.Combine(localDir, relativePath);

                if (FolderRecursionCheckBox.IsChecked != true && Path.GetDirectoryName(relativePath) != "")
                {
                    Debugger.show($"Skipping subfolder file {relativePath}");
                    StatusLog.Items.Add($"Skipping subfolder file {currentIndex}/{totalItems}: {relativePath}");
                    StatusLog.ScrollIntoView(StatusLog.Items[StatusLog.Items.Count - 1]);
                    continue;
                }

                if (item.Type == "d")
                {
                    if (FolderRecursionCheckBox.IsChecked == true)
                    {
                        Directory.CreateDirectory(localPath);
                        File.SetLastWriteTime(localPath, item.Date);
                        Debugger.show($"Created directory: {relativePath}");
                        StatusLog.Items.Add($"Created folder {currentIndex}/{totalItems}: {relativePath}");
                        StatusLog.ScrollIntoView(StatusLog.Items[StatusLog.Items.Count - 1]);
                    }
                    continue;
                }

                if (File.Exists(localPath))
                {
                    Debugger.show($"Skipping existing file {relativePath}");
                    StatusLog.Items.Add($"Skipping existing file {currentIndex}/{totalItems}: {relativePath}");
                    StatusLog.ScrollIntoView(StatusLog.Items[StatusLog.Items.Count - 1]);
                    await Task.Delay(50, token);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                Debugger.show($"Pulling file: {relativePath}");
                StatusLog.Items.Add($"Pulling {currentIndex}/{totalItems}: {relativePath}...");
                StatusLog.ScrollIntoView(StatusLog.Items[StatusLog.Items.Count - 1]);
                await Task.Delay(50, token);

                string pullOutput = await RunAdbCaptureAsync($"-s {CurrentDevice} pull \"{item.Path}\" \"{localPath}\"");

                if (!File.Exists(localPath))
                {
                    string failureReason = "Unknown error";

                    if (token.IsCancellationRequested)
                        failureReason = "Canceled by user";
                    else if (string.IsNullOrWhiteSpace(pullOutput))
                        failureReason = "ADB returned no output";
                    else if (pullOutput.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0)
                        failureReason = "ADB pull failed";
                    else
                        failureReason = "File not found after pull";

                    Debugger.show($"Failed to pull {relativePath}: {failureReason}");
                    FailedList.Items.Add($"{relativePath} - {failureReason}");
                    FailedList.ScrollIntoView(FailedList.Items[FailedList.Items.Count - 1]);

                    StatusLog.Items.Add($"Failed to pull {currentIndex}/{totalItems}: {relativePath} ({failureReason})");
                    StatusLog.ScrollIntoView(StatusLog.Items[StatusLog.Items.Count - 1]);
                }
                else
                {
                    Debugger.show($"Successfully pulled {relativePath}");
                    SuccessList.Items.Add(relativePath);
                    SuccessList.ScrollIntoView(SuccessList.Items[SuccessList.Items.Count - 1]);

                    File.SetLastWriteTime(localPath, item.Date);

                    StatusLog.Items.Add($"Successfully pulled {currentIndex}/{totalItems}: {relativePath}");
                    StatusLog.ScrollIntoView(StatusLog.Items[StatusLog.Items.Count - 1]);
                }

                await Task.Delay(50, token);
            }

            Debugger.show("Completed SyncMusicToPcAsync successfully");
            StatusLog.Items.Add("Done syncing music.");
            StatusLog.ScrollIntoView(StatusLog.Items[StatusLog.Items.Count - 1]);
        }



        public async Task SyncMusicToPhoneAsync(CancellationToken token)
        {
            Debugger.show("Entered SyncMusicToPhoneAsync");
            if (string.IsNullOrEmpty(CurrentDevice))
            {
                MessageBox.Show("No device selected.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string remoteDir = RemoteDirTextBox.Text.Trim();
            string localDir = LocalDirTextBox.Text.Trim();
            Debugger.show($"RemoteDir='{remoteDir}', LocalDir='{localDir}'");

            if (string.IsNullOrEmpty(remoteDir) || string.IsNullOrEmpty(localDir))
            {
                Debugger.show("Missing directory selection");
                MessageBox.Show("Please select both local and remote directories.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SaveDirectoryPaths();

            if (!Directory.Exists(localDir))
            {
                Debugger.show($"Local directory does not exist: {localDir}");
                MessageBox.Show("Local directory does not exist.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Debugger.show("Starting local file enumeration...");
            StatusLog.Items.Clear();
            SuccessList.Items.Clear();
            FailedList.Items.Clear();

            StatusLog.Items.Add("Getting local file list...");
            StatusLog.ScrollIntoView(StatusLog.Items[StatusLog.Items.Count - 1]);

            var searchOption = FolderRecursionCheckBox.IsChecked == true ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var localFiles = Directory.GetFiles(localDir, "*.*", searchOption);
            Debugger.show($"Total local files found: {localFiles.Length}");
            int totalFiles = localFiles.Length;
            int currentIndex = 0;

            string EscapeForShell(string path) => path.Replace(" ", "\\ ");

            await RunAdbCaptureAsync($"-s {CurrentDevice} shell mkdir -p \"{EscapeForShell(remoteDir)}\"");

            foreach (var localFile in localFiles)
            {
                token.ThrowIfCancellationRequested();
                currentIndex++;

                string relativePath = Path.GetRelativePath(localDir, localFile).Replace("\\", "/");
                string remoteFile = $"{remoteDir}/{relativePath}";
                string remoteFolder = Path.GetDirectoryName(remoteFile).Replace("\\", "/");

                Debugger.show($"Pushing file {currentIndex}/{totalFiles}: {relativePath}");

                await RunAdbCaptureAsync($"-s {CurrentDevice} shell mkdir -p \"{EscapeForShell(remoteFolder)}\"");
                string checkOutput = await RunAdbCaptureAsync($"-s {CurrentDevice} shell ls \"{EscapeForShell(remoteFile)}\"");
                if (!string.IsNullOrWhiteSpace(checkOutput) && !checkOutput.Contains("No such file"))
                {
                    Debugger.show($"Skipping existing file on device: {relativePath}");
                    StatusLog.Items.Add($"Skipping existing file {currentIndex}/{totalFiles}: {relativePath}");
                    StatusLog.ScrollIntoView(StatusLog.Items[StatusLog.Items.Count - 1]);
                    await Task.Delay(50, token);
                    continue;
                }

                StatusLog.Items.Add($"Pushing {currentIndex}/{totalFiles}: {relativePath}...");
                StatusLog.ScrollIntoView(StatusLog.Items[StatusLog.Items.Count - 1]);
                await Task.Delay(50, token);

                string pushOutput = await RunAdbCaptureAsync($"-s {CurrentDevice} push \"{localFile}\" \"{remoteFile}\"");
                string verifyOutput = await RunAdbCaptureAsync($"-s {CurrentDevice} shell ls \"{EscapeForShell(remoteFile)}\"");

                if (string.IsNullOrWhiteSpace(verifyOutput) || verifyOutput.Contains("No such file"))
                {
                    string failureReason = string.IsNullOrWhiteSpace(pushOutput) ? "ADB returned no output" : "ADB push failed";
                    Debugger.show($"Failed to push {relativePath}: {failureReason}");
                    FailedList.Items.Add($"{relativePath} - {failureReason}");
                    FailedList.ScrollIntoView(FailedList.Items[FailedList.Items.Count - 1]);
                    StatusLog.Items.Add($"Failed to push {currentIndex}/{totalFiles}: {relativePath} ({failureReason})");
                    StatusLog.ScrollIntoView(StatusLog.Items[StatusLog.Items.Count - 1]);
                }
                else
                {
                    Debugger.show($"Successfully pushed {relativePath}");
                    DateTime lastWriteLocal = File.GetLastWriteTime(localFile);
                    DateTime lastWriteUtc = lastWriteLocal.ToUniversalTime();
                    TimeSpan deviceOffset = TimeZoneInfo.Local.GetUtcOffset(lastWriteLocal);
                    DateTime adbTime = lastWriteUtc + deviceOffset;
                    string adbTimeStr = adbTime.ToString("yyyyMMddHHmm.ss");

                    await RunAdbCaptureAsync($"-s {CurrentDevice} shell touch -t {adbTimeStr} \"{EscapeForShell(remoteFile)}\"");

                    SuccessList.Items.Add(relativePath);
                    SuccessList.ScrollIntoView(SuccessList.Items[SuccessList.Items.Count - 1]);
                    StatusLog.Items.Add($"Successfully pushed {currentIndex}/{totalFiles}: {relativePath}");
                    StatusLog.ScrollIntoView(StatusLog.Items[StatusLog.Items.Count - 1]);
                }

                await Task.Delay(50, token);
            }

            if (FolderRecursionCheckBox.IsChecked == true)
            {
                foreach (var folder in Directory.GetDirectories(localDir, "*", SearchOption.AllDirectories).OrderBy(f => f.Length))
                {
                    token.ThrowIfCancellationRequested();
                    string relativeFolder = Path.GetRelativePath(localDir, folder).Replace("\\", "/");
                    string remoteFolder = $"{remoteDir}/{relativeFolder}";

                    DateTime lastWriteLocal = Directory.GetLastWriteTime(folder);
                    DateTime lastWriteUtc = lastWriteLocal.ToUniversalTime();
                    TimeSpan deviceOffset = TimeZoneInfo.Local.GetUtcOffset(lastWriteLocal);
                    DateTime adbTime = lastWriteUtc + deviceOffset;
                    string adbTimeStr = adbTime.ToString("yyyyMMddHHmm.ss");

                    await RunAdbCaptureAsync($"-s {CurrentDevice} shell mkdir -p \"{EscapeForShell(remoteFolder)}\"");
                    await RunAdbCaptureAsync($"-s {CurrentDevice} shell touch -t {adbTimeStr} \"{EscapeForShell(remoteFolder)}\"");
                }
            }
            Debugger.show("Completed SyncMusicToPhoneAsync successfully");
            StatusLog.Items.Add("Done syncing music to phone.");
            StatusLog.ScrollIntoView(StatusLog.Items[StatusLog.Items.Count - 1]);
        }

        private void FileDropBox_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void FileDropBox_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        private async void FileDropBox_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            if (string.IsNullOrEmpty(CurrentDevice))
            {
                MessageBox.Show("No device selected.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string remoteDir = RemoteDirTextBox.Text.Trim();
            if (string.IsNullOrEmpty(remoteDir))
            {
                MessageBox.Show("Please select a remote directory.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var droppedFiles = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (droppedFiles.Length == 0) return;

            StatusLog.Items.Clear();
            SuccessList.Items.Clear();
            FailedList.Items.Clear();

            string EscapeForShell(string path) => path.Replace(" ", "\\ ");

            await RunAdbCaptureAsync($"-s {CurrentDevice} shell mkdir -p \"{EscapeForShell(remoteDir)}\"");

            int totalFiles = droppedFiles.Length;
            int currentIndex = 0;

            foreach (var localPath in droppedFiles)
            {
                currentIndex++;

                if (Directory.Exists(localPath))
                {
                    // Handle directory recursively
                    var allFiles = Directory.GetFiles(localPath, "*.*", FolderRecursionCheckBox.IsChecked == true ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
                    foreach (var file in allFiles)
                    {
                        await PushFileToDevice(file, localPath, remoteDir, currentIndex, totalFiles);
                    }
                }
                else if (File.Exists(localPath))
                {
                    await PushFileToDevice(localPath, Path.GetDirectoryName(localPath), remoteDir, currentIndex, totalFiles);
                }
            }

            StatusLog.Items.Add("Done pushing dropped files.");
            StatusLog.ScrollIntoView(StatusLog.Items[StatusLog.Items.Count - 1]);
        }

        private async Task PushFileToDevice(string localFile, string baseLocalDir, string remoteDir, int currentIndex, int totalFiles)
        {
            string EscapeForShell(string path) => path.Replace(" ", "\\ ");

            string relativePath = Path.GetRelativePath(baseLocalDir, localFile).Replace("\\", "/");
            string remoteFile = $"{remoteDir}/{relativePath}";
            string remoteFolder = Path.GetDirectoryName(remoteFile).Replace("\\", "/");

            await RunAdbCaptureAsync($"-s {CurrentDevice} shell mkdir -p \"{EscapeForShell(remoteFolder)}\"");

            string checkOutput = await RunAdbCaptureAsync($"-s {CurrentDevice} shell ls \"{EscapeForShell(remoteFile)}\"");
            if (!string.IsNullOrWhiteSpace(checkOutput) && !checkOutput.Contains("No such file"))
            {
                StatusLog.Items.Add($"Skipping existing file {currentIndex}/{totalFiles}: {relativePath}");
                StatusLog.ScrollIntoView(StatusLog.Items[StatusLog.Items.Count - 1]);
                await Task.Delay(50);
                return;
            }

            StatusLog.Items.Add($"Pushing {currentIndex}/{totalFiles}: {relativePath}...");
            StatusLog.ScrollIntoView(StatusLog.Items[StatusLog.Items.Count - 1]);
            await Task.Delay(50);

            string pushOutput = await RunAdbCaptureAsync($"-s {CurrentDevice} push \"{localFile}\" \"{remoteFile}\"");

            // Carefully handle file timestamps
            DateTime lastWriteLocal = File.GetLastWriteTime(localFile);
            DateTime lastWriteUtc = lastWriteLocal.ToUniversalTime();
            TimeSpan deviceOffset = TimeZoneInfo.Local.GetUtcOffset(lastWriteLocal);
            DateTime adbTime = lastWriteUtc + deviceOffset;
            string adbTimeStr = adbTime.ToString("yyyyMMddHHmm.ss");

            await RunAdbCaptureAsync($"-s {CurrentDevice} shell touch -t {adbTimeStr} \"{EscapeForShell(remoteFile)}\"");

            string verifyOutput = await RunAdbCaptureAsync($"-s {CurrentDevice} shell ls \"{EscapeForShell(remoteFile)}\"");
            if (string.IsNullOrWhiteSpace(verifyOutput) || verifyOutput.Contains("No such file"))
            {
                string failureReason = string.IsNullOrWhiteSpace(pushOutput) ? "ADB returned no output" : "ADB push failed";
                FailedList.Items.Add($"{relativePath} - {failureReason}");
                FailedList.ScrollIntoView(FailedList.Items[FailedList.Items.Count - 1]);
                StatusLog.Items.Add($"Failed to push {currentIndex}/{totalFiles}: {relativePath} ({failureReason})");
            }
            else
            {
                SuccessList.Items.Add(relativePath);
                SuccessList.ScrollIntoView(SuccessList.Items[SuccessList.Items.Count - 1]);
                StatusLog.Items.Add($"Successfully pushed {currentIndex}/{totalFiles}: {relativePath}");
            }

            StatusLog.ScrollIntoView(StatusLog.Items[StatusLog.Items.Count - 1]);
            await Task.Delay(50);
        }





        private void SuccessList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (SuccessList.SelectedItem is string fileName)
            {
                string localDir = LocalDirTextBox.Text.Trim();
                string filePath = Path.Combine(localDir, fileName);

                if (File.Exists(filePath))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"/select,\"{filePath}\"",
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to open Explorer: {ex.Message}",
                                        "Error",
                                        MessageBoxButton.OK,
                                        MessageBoxImage.Error);
                    }
                }
                else
                {
                    MessageBox.Show("The file no longer exists on disk.",
                                    "File Missing",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Warning);
                }
            }
        }

    }
}