using System.IO;
using System.Windows.Threading;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;
using System.Linq;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System;

namespace phone_utils
{
    /// <summary>
    ///     i have no clue how this works, this was completly written by chatgpt
    ///     so don't touch this shit ever unless absolutely needed
    /// </summary>


    /// this will be rewritten to use ffmpeg and pull the files for cover art
    /// then create a cache to store it and a button to clear cache
    /// how we will do it consists of a couple of things things
    /// add a button in setup to cache a full folder + recursion // this allows for all songs to be auto cached if the user wants it / not prefered
    /// the main way should be by detecting the song that is currently playing and pulling that into a temp folder within cache (cache sould be stored in appdata)
    /// then we use ffmpeg to extract the cover art we then make sure it gets prioritized over repulling the song
    /// we also make a small file that contains data about what songs don't have coverart to make sure that we don't pull many times

    ///so exact plan is
    /// 1 check if the songs coverart exists in the cache folder
    /// 2 check if remote folder contains a cover.png/jpg in a folder a layer down from the main folder
    /// if it does then make sure to save that all songs in that folder get associated with that cover art
    /// 3 if not found then pull the song via adb to a temp folder and use ffmpeg to extract the cover art
    /// 4 if no cover art is found then we make a note of that in a text file to avoid repulling
    /// 5 we also need to add a button to clear cache in settings
    /// 6 we also need to make sure that the cache folder is created on first run
    /// 7 we also need to make sure that the cache folder is cleaned up on uninstall
    /// 8 we also need to make sure that the cache folder is limited in size and old files are deleted when the limit is reached

    // MediaController encapsulates all SMTC / MediaPlayer logic
    internal class MediaController
    {
        private MediaPlayer mediaPlayer;
        private SystemMediaTransportControls smtcControls;
        private SystemMediaTransportControlsDisplayUpdater smtcDisplayUpdater;
        private readonly Dispatcher dispatcher;
        private readonly Func<string> getCurrentDevice; // callback to get device id
        private readonly Func<Task> updateCurrentSongCallback; // optional callback to refresh song
        private string lastSMTCTitle;

        private readonly CoverCacheManager cacheManager;

        // Make remoteRoot configurable via config (FileSync.RemoteDir). Fallback to previous hardcoded path.
        private readonly string remoteRoot;

        public MediaController(Dispatcher dispatcher, Func<string> getCurrentDevice, Func<Task> updateCurrentSongCallback)
        {
            this.dispatcher = dispatcher;
            this.getCurrentDevice = getCurrentDevice;
            this.updateCurrentSongCallback = updateCurrentSongCallback;

            // Initialize cache manager using config values from expected location
            try
            {
                var ConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Phone Utils", "config.json");
                var Config = ConfigManager.Load(ConfigPath);
                cacheManager = new CoverCacheManager(Config.Paths.FfmpegPath, Config.Paths.CoverCachePath);

                // Read remote root from config FileSync.RemoteDir if present
                if (Config != null && Config.FileSync != null && !string.IsNullOrWhiteSpace(Config.FileSync.RemoteDir))
                {
                    remoteRoot = Config.FileSync.RemoteDir;
                }
                else
                {
                    // previous hardcoded default
                    remoteRoot = "";
                }
            }
            catch (Exception ex)
            {
                Debugger.show("Failed to initialize CoverCacheManager: " + ex.Message);
                // ensure remoteRoot has a value even if config load failed
                remoteRoot = "";
            }
        }

        public void Initialize()
        {
            try
            {
                mediaPlayer = new MediaPlayer();
                smtcControls = mediaPlayer.SystemMediaTransportControls;
                smtcDisplayUpdater = smtcControls.DisplayUpdater;

                smtcControls.IsEnabled = true;
                smtcControls.IsPlayEnabled = true;
                smtcControls.IsPauseEnabled = true;
                smtcControls.IsNextEnabled = true;
                smtcControls.IsPreviousEnabled = true;

                smtcControls.ButtonPressed += SmTc_ButtonPressed;
                smtcDisplayUpdater.Type = MediaPlaybackType.Music;
            }
            catch (Exception ex)
            {
                Debugger.show($"MediaPlayer initialization failed: {ex.Message}");
            }
        }

        // Avoid long-running work on the SMTC event handler. Dispatch the work to an async Task.
        private void SmTc_ButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            Debugger.show($"SMTC Button Pressed: {args.Button}");
            // Fire-and-forget the handler on the dispatcher to keep the event synchronous
            _ = dispatcher.InvokeAsync(() => HandleSmTcButtonAsync(args.Button)).Task;
        }

        private async Task HandleSmTcButtonAsync(SystemMediaTransportControlsButton button)
        {
            try
            {
                switch (button)
                {
                    case SystemMediaTransportControlsButton.Play:
                        await PlayTrack().ConfigureAwait(false);
                        break;
                    case SystemMediaTransportControlsButton.Pause:
                        await PauseTrack().ConfigureAwait(false);
                        break;
                    case SystemMediaTransportControlsButton.Next:
                        await NextTrack().ConfigureAwait(false);
                        break;
                    case SystemMediaTransportControlsButton.Previous:
                        await PreviousTrack().ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                Debugger.show($"SMTC handler error: {ex.Message}");
            }
        }

        private async Task PlayTrack()
        {
            try
            {
                var device = getCurrentDevice();
                if (string.IsNullOrEmpty(device)) return;
                await AdbHelper.RunAdbAsync($"-s {device} shell input keyevent 85").ConfigureAwait(false);
                if (smtcControls != null)
                    smtcControls.PlaybackStatus = MediaPlaybackStatus.Playing;
                Debugger.show("Play requested");
            }
            catch (Exception ex)
            {
                Debugger.show($"PlayTrack failed: {ex.Message}");
            }
        }

        private async Task PauseTrack()
        {
            try
            {
                var device = getCurrentDevice();
                if (string.IsNullOrEmpty(device)) return;
                await AdbHelper.RunAdbAsync($"-s {device} shell input keyevent 85").ConfigureAwait(false);
                if (smtcControls != null)
                    smtcControls.PlaybackStatus = MediaPlaybackStatus.Paused;
                Debugger.show("Pause requested.");
            }
            catch (Exception ex)
            {
                Debugger.show($"PauseTrack failed: {ex.Message}");
            }
        }

        private async Task NextTrack()
        {
            try
            {
                var device = getCurrentDevice();
                if (string.IsNullOrEmpty(device)) return;
                await AdbHelper.RunAdbAsync($"-s {device} shell input keyevent 87").ConfigureAwait(false);
                await Task.Delay(500).ConfigureAwait(false);
                if (updateCurrentSongCallback != null)
                {
                    try { await updateCurrentSongCallback().ConfigureAwait(false); } catch (Exception ex) { Debugger.show($"updateCurrentSongCallback failed: {ex.Message}"); }
                }
                Debugger.show("Next track requested.");
            }
            catch (Exception ex)
            {
                Debugger.show($"NextTrack failed: {ex.Message}");
            }
        }

        private async Task PreviousTrack()
        {
            try
            {
                var device = getCurrentDevice();
                if (string.IsNullOrEmpty(device)) return;
                await AdbHelper.RunAdbAsync($"-s {device} shell input keyevent 88").ConfigureAwait(false);
                await Task.Delay(500).ConfigureAwait(false);
                if (updateCurrentSongCallback != null)
                {
                    try { await updateCurrentSongCallback().ConfigureAwait(false); } catch (Exception ex) { Debugger.show($"updateCurrentSongCallback failed: {ex.Message}"); }
                }
                Debugger.show("Previous track requested.");
            }
            catch (Exception ex)
            {
                Debugger.show($"PreviousTrack failed: {ex.Message}");
            }
        }

        public bool IsPaused { get; private set; }

        public async Task UpdateMediaControlsAsync(string title, string artist, string album, bool isPlaying)
        {
            try
            {
                // Always update paused state and SMTC playback status, even if title is unchanged
                IsPaused = !isPlaying;
                if (smtcControls != null)
                    smtcControls.PlaybackStatus = isPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;

                if (string.Equals(lastSMTCTitle, title, StringComparison.OrdinalIgnoreCase))
                {
                    Debugger.show($"SMTC title '{title}' is same as last. Skipping update.");
                    return;
                }

                lastSMTCTitle = title;

                // Offload image/duration retrieval to avoid UI blocking inside SetSMTCImageAsync which may do file IO.
                TimeSpan? duration = await SetSMTCImageAsync(title, artist).ConfigureAwait(false);

                // UI-affecting updates must run on dispatcher
                if (mediaPlayer == null || smtcDisplayUpdater == null)
                    return;

                await dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        var musicProperties = smtcDisplayUpdater.MusicProperties;
                        musicProperties.Title = title;
                        musicProperties.Artist = artist;
                        musicProperties.AlbumTitle = album;

                        smtcDisplayUpdater.Update();

                        if (duration.HasValue && smtcControls != null)
                        {
                            var timelineProps = new SystemMediaTransportControlsTimelineProperties
                            {
                                StartTime = TimeSpan.Zero,
                                EndTime = duration.Value,
                            };
                            smtcControls.UpdateTimelineProperties(timelineProps);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debugger.show($"Failed updating SMTC metadata on UI thread: {ex.Message}");
                    }
                }).Task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debugger.show($"UpdateMediaControlsAsync failed: {ex.Message}");
            }
        }

        public void Clear()
        {
            try
            {
                if (smtcDisplayUpdater != null)
                {
                    smtcDisplayUpdater.ClearAll();
                    smtcDisplayUpdater.Update();
                }

                if (mediaPlayer != null)
                {
                    mediaPlayer.Pause();
                    mediaPlayer.Dispose();
                    mediaPlayer = null;
                }

                smtcControls = null;
                smtcDisplayUpdater = null;
            }
            catch (Exception ex)
            {
                Debugger.show($"Failed to clear media controls: {ex.Message}");
            }
        }

        private IEnumerable<string> TokenizeForMatch(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return Array.Empty<string>();
            var matches = Regex.Matches(s.ToLowerInvariant(), @"[\p{L}\p{N}]{3,}");
            return matches.Select(m => m.Value).Distinct();
        }

        private bool TokensMatchEnough(IEnumerable<string> titleTokens, IEnumerable<string> fileTokens)
        {
            var t = titleTokens.ToList();
            var f = new HashSet<string>(fileTokens);
            if (t.Count == 0) return false;
            int match = t.Count(tok => f.Contains(tok));
            // require at least half of tokens to match (tunable)
            return match >= Math.Max(1, t.Count / 2);
        }

        private async Task<TimeSpan?> SetSMTCImageAsync(string fileNameWithoutExtension, string artist)
        {
            if (mediaPlayer == null || smtcDisplayUpdater == null)
            {
                Initialize();
                if (mediaPlayer == null || smtcDisplayUpdater == null)
                {
                    Debugger.show("Failed to initialize media player");
                    return null;
                }
            }

            // Use configurable remote root (populated from config at construction time)
            string localRemoteRoot = remoteRoot;
            string[] audioExtensions = { ".mp3", ".flac", ".wav", ".m4a", ".ogg", ".opus" };
            string[] imageExtensions = { ".webp", ".png", ".jpg", ".jpeg" };

            try
            {
                Debugger.show($"Starting cover art search for: '{fileNameWithoutExtension}' by '{artist}'");

                var device = getCurrentDevice();
                if (string.IsNullOrEmpty(device))
                {
                    Debugger.show("No device selected for cover lookup");
                    await SetDefaultImage().ConfigureAwait(false);
                    return null;
                }

                // Use find to get full file paths under remoteRoot
                var findOutput = await AdbHelper.RunAdbCaptureAsync($"-s {device} shell find \"{localRemoteRoot}\" -type f");
                if (string.IsNullOrWhiteSpace(findOutput))
                {
                    Debugger.show("No output from remote find");
                    await SetDefaultImage().ConfigureAwait(false);
                    return null;
                }

                var lines = findOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                var allFiles = new List<string>();
                foreach (var raw in lines)
                {
                    var path = raw.Trim();
                    if (string.IsNullOrEmpty(path)) continue;
                    allFiles.Add(path);
                }

                // Filter audio files and perform tokenized matching
                var titleTokens = TokenizeForMatch(fileNameWithoutExtension).ToList();
                var candidates = allFiles.Where(p => audioExtensions.Contains(Path.GetExtension(p).ToLowerInvariant())).ToList();

                var matched = new List<string>();
                foreach (var candidate in candidates)
                {
                    var fn = Path.GetFileName(candidate);
                    var nameNoExt = Path.GetFileNameWithoutExtension(fn);
                    var fileTokens = TokenizeForMatch(nameNoExt);

                    bool match = false;
                    if (titleTokens.Any())
                    {
                        match = TokensMatchEnough(titleTokens, fileTokens);
                    }
                    else
                    {
                        match = nameNoExt.IndexOf(fileNameWithoutExtension, StringComparison.OrdinalIgnoreCase) >= 0;
                    }

                    if (match) matched.Add(candidate);
                }

                if (matched.Count == 0)
                {
                    Debugger.show("No candidates found on device for title token");
                    await SetDefaultImage().ConfigureAwait(false);
                    return null;
                }

                // Prefer artist matches if multiple
                var artistMatches = new List<string>();
                if (!string.IsNullOrEmpty(artist) && matched.Count > 1)
                {
                    artistMatches = matched.Where(c => c.IndexOf(artist, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                }

                var filesToProcess = artistMatches.Count > 0 ? artistMatches : matched;

                // Rank candidates by a match score (exact title substring, token intersection, artist match),
                // then by folder depth. This reduces incorrect picks where an unrelated file has embedded art.
                var titleStr = fileNameWithoutExtension ?? string.Empty;
                var ranked = filesToProcess
                    .Select(p =>
                    {
                        var nameNoExt = Path.GetFileNameWithoutExtension(p);
                        int score = 0;
                        // exact substring match (strong)
                        if (!string.IsNullOrEmpty(titleStr) && nameNoExt.IndexOf(titleStr, StringComparison.OrdinalIgnoreCase) >= 0)
                            score += 100;

                        // token intersection
                        var fileTokens = TokenizeForMatch(nameNoExt).ToList();
                        var tCount = titleTokens.Count;
                        if (tCount > 0)
                        {
                            int inter = fileTokens.Count(ft => titleTokens.Contains(ft));
                            score += inter * 10;
                        }

                        // artist match (moderate)
                        if (!string.IsNullOrEmpty(artist) && p.IndexOf(artist, StringComparison.OrdinalIgnoreCase) >= 0)
                            score += 50;

                        // small boost for deeper paths
                        int depth = p.Count(ch => ch == '/');
                        score += Math.Min(depth, 10);

                        return (path: p, score);
                    })
                    .OrderByDescending(x => x.score)
                    .ThenByDescending(x => x.path.Count(ch => ch == '/'))
                    .ToList();

                filesToProcess = ranked.Select(r => r.path).Take(20).ToList(); // limit to top 20

                Debugger.show($"Files to process for cover art lookup (ranked): {filesToProcess.Count}");

                TimeSpan? duration = null; // we won't determine duration via TagLib anymore

                foreach (var remotePath in filesToProcess)
                {
                    Debugger.show($"Processing remote file for cover art: {remotePath}");

                    string imagePath = await cacheManager.GetImagePathForNowPlayingAsync(device, remotePath).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
                    {
                        Debugger.show($"Found cached image at {imagePath}, setting SMTC thumbnail");
                        var imageFile = await StorageFile.GetFileFromPathAsync(imagePath).AsTask().ConfigureAwait(false);

                        await dispatcher.InvokeAsync(() =>
                        {
                            try
                            {
                                smtcDisplayUpdater.Thumbnail = RandomAccessStreamReference.CreateFromFile(imageFile);
                                smtcDisplayUpdater.Update();
                                Debugger.show($"Thumbnail set from cached file: {imagePath}");
                            }
                            catch (Exception ex)
                            {
                                Debugger.show($"Failed to set thumbnail on dispatcher: {ex.Message}");
                            }
                        }).Task.ConfigureAwait(false);

                        return duration;
                    }
                    else
                    {
                        Debugger.show($"No image returned for {remotePath}, continuing to next candidate");
                    }
                }

                Debugger.show("No cover art found for any candidates; using default image");
                await SetDefaultImage().ConfigureAwait(false);
                return duration;
            }
            catch (Exception ex)
            {
                Debugger.show($"Critical error in SetSMTCImageAsync: {ex.Message}");
                await SetDefaultImage().ConfigureAwait(false);
                return null;
            }
        }

        private async Task SetDefaultImage()
        {
            try
            {
                string defaultImagePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Phone Utils", "Resources", "logo.png"
                );

                Debugger.show($"Setting default image from: {defaultImagePath}");

                var imageFile = await StorageFile.GetFileFromPathAsync(defaultImagePath).AsTask().ConfigureAwait(false);
                await dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        smtcDisplayUpdater.Thumbnail = RandomAccessStreamReference.CreateFromFile(imageFile);
                        smtcDisplayUpdater.Update();
                        Debugger.show("Default thumbnail set successfully");
                    }
                    catch (Exception ex)
                    {
                        Debugger.show($"Failed to set default thumbnail on dispatcher: {ex.Message}");
                    }
                }).Task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debugger.show($"Failed to set default image: {ex.Message}");
            }
        }
    }
}
