using System.IO;
using System.Windows.Threading;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;
using CoverArt = TagLib;

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

        public MediaController(Dispatcher dispatcher, Func<string> getCurrentDevice, Func<Task> updateCurrentSongCallback)
        {
            this.dispatcher = dispatcher;
            this.getCurrentDevice = getCurrentDevice;
            this.updateCurrentSongCallback = updateCurrentSongCallback;
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

            string folderPath = @"C:\Users\wille\Desktop\Audio";
            string[] audioExtensions = { ".mp3", ".flac", ".wav", ".m4a", ".ogg", ".opus" };
            string[] imageExtensions = { ".webp", ".png", ".jpg", ".jpeg" };

            try
            {
                Debugger.show($"Starting cover art search for: '{fileNameWithoutExtension}' by '{artist}'");

                // Offload file-system scanning and TagLib extraction to a background thread to avoid UI stalls
                var matchingFiles = await Task.Run(() =>
                {
                    var list = new List<string>();
                    foreach (var ext in audioExtensions)
                    {
                        try
                        {
                            Debugger.show($"Searching for *{ext} files...");
                            var files = Directory.GetFiles(folderPath, "*" + ext, SearchOption.AllDirectories)
                                .Where(f => Path.GetFileNameWithoutExtension(f)
                                .IndexOf(fileNameWithoutExtension, StringComparison.OrdinalIgnoreCase) >= 0)
                                .ToList();

                            Debugger.show($"Found {files.Count} files with extension {ext} that match the title token.");

                            list.AddRange(files);
                        }
                        catch (Exception ex)
                        {
                            Debugger.show($"Directory scan error for ext {ext}: {ex.Message}");
                        }
                    }

                    Debugger.show($"Total partial matches found: {list.Count}");
                    return list;
                }).ConfigureAwait(false);

                if (matchingFiles.Count == 0)
                {
                    Debugger.show("No audio files found (partial match failed)");
                    await SetDefaultImage().ConfigureAwait(false);
                    return null;
                }

                Debugger.show($"Filtering by artist: '{artist}' if available...");

                List<string> artistMatches = new List<string>();

                if (!string.IsNullOrEmpty(artist) && matchingFiles.Count > 1)
                {
                    artistMatches = matchingFiles
                        .Where(f => f.IndexOf(artist, StringComparison.OrdinalIgnoreCase) >= 0)
                        .ToList();

                    Debugger.show($"Artist-based matches: {artistMatches.Count}");
                }

                List<string> filesToProcess = artistMatches.Count > 0 ? artistMatches : matchingFiles;

                Debugger.show($"Files to process for cover art lookup: {filesToProcess.Count}");

                string filePathForDuration = filesToProcess.FirstOrDefault(f => audioExtensions.Contains(Path.GetExtension(f).ToLower()));

                TimeSpan? duration = null;
                if (filePathForDuration != null)
                {
                    Debugger.show($"Attempting to read duration from: {filePathForDuration}");
                    try
                    {
                        // TagLib reads can be blocking; do on background thread
                        duration = await Task.Run(() =>
                        {
                            try
                            {
                                var tagFile = CoverArt.File.Create(filePathForDuration);
                                Debugger.show($"Read duration: {tagFile.Properties.Duration} from {filePathForDuration}");
                                return tagFile.Properties.Duration;
                            }
                            catch (Exception ex)
                            {
                                Debugger.show($"TagLib failed to read duration for {filePathForDuration}: {ex.Message}");
                                return (TimeSpan?)null;
                            }
                        }).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Debugger.show($"Failed to read audio file duration: {ex.Message}");
                    }
                }

                var sortedFiles = filesToProcess.OrderByDescending(f =>
                {
                    string fileDir = Path.GetDirectoryName(f);
                    return !fileDir.Equals(folderPath, StringComparison.OrdinalIgnoreCase);
                }).ToList();

                Debugger.show($"Sorted files count: {sortedFiles.Count}");

                foreach (var filePath in sortedFiles)
                {
                    Debugger.show($"Processing file for cover art: {filePath}");
                    StorageFile imageFile = await TryGetCoverArtForFile(filePath, folderPath, imageExtensions).ConfigureAwait(false);

                    if (imageFile != null)
                    {
                        Debugger.show($"Found image for {filePath}, assigning thumbnail.");
                        // Thumbnail/DisplayUpdater likely needs to be used on a thread that supports COM/WinRT; do the assignment on the dispatcher
                        await dispatcher.InvokeAsync(() =>
                        {
                            try
                            {
                                smtcDisplayUpdater.Thumbnail = RandomAccessStreamReference.CreateFromFile(imageFile);
                                smtcDisplayUpdater.Update();
                                Debugger.show($"Thumbnail set from image file: {imageFile.Path}");
                            }
                            catch (Exception ex)
                            {
                                Debugger.show($"Failed to set thumbnail on dispatcher: {ex.Message}");
                            }
                        }).Task.ConfigureAwait(false);

                        Debugger.show("SMTC image updated successfully");
                        return duration;
                    }
                    else
                    {
                        Debugger.show($"No image found for {filePath}, continuing to next candidate.");
                    }
                }

                Debugger.show("No embedded or folder images found for any candidate files. Falling back to default image.");
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

        private async Task<StorageFile> TryGetCoverArtForFile(string filePath, string folderPath, string[] imageExtensions)
        {
            string fileDirectory = Path.GetDirectoryName(filePath);
            string fileName = Path.GetFileNameWithoutExtension(filePath);
            bool isInSubfolder = !fileDirectory.Equals(folderPath, StringComparison.OrdinalIgnoreCase);

            Debugger.show($"TryGetCoverArtForFile: file={filePath}, isInSubfolder={isInSubfolder}");

            if (isInSubfolder)
            {
                string coverJpg = Path.Combine(fileDirectory, "cover.jpg");
                string coverPng = Path.Combine(fileDirectory, "cover.png");

                Debugger.show($"Checking for cover.jpg at: {coverJpg}");
                if (File.Exists(coverJpg))
                {
                    Debugger.show("cover.jpg found, returning StorageFile");
                    return await StorageFile.GetFileFromPathAsync(coverJpg).AsTask().ConfigureAwait(false);
                }

                Debugger.show($"Checking for cover.png at: {coverPng}");
                if (File.Exists(coverPng))
                {
                    Debugger.show("cover.png found, returning StorageFile");
                    return await StorageFile.GetFileFromPathAsync(coverPng).AsTask().ConfigureAwait(false);
                }

                Debugger.show("No direct cover.jpg/png in subfolder. Attempting TagLib extraction.");
                var tagLibImage = await TryExtractCoverFromTagLib(filePath).ConfigureAwait(false);
                if (tagLibImage != null)
                {
                    Debugger.show("TagLib provided embedded image for file.");
                    return tagLibImage;
                }
                else
                {
                    Debugger.show("TagLib extraction returned no image for file.");
                }
            }
            else
            {
                foreach (var imgExt in imageExtensions)
                {
                    string imagePath = Path.Combine(fileDirectory + "\\images", fileName + imgExt);
                    Debugger.show($"Checking for image in images folder: {imagePath}");
                    if (File.Exists(imagePath))
                    {
                        Debugger.show($"Found image at {imagePath}");
                        return await StorageFile.GetFileFromPathAsync(imagePath).AsTask().ConfigureAwait(false);
                    }
                }

                Debugger.show("No images in images subfolder. Attempting TagLib extraction for file.");
                var tagLibImage = await TryExtractCoverFromTagLib(filePath).ConfigureAwait(false);
                if (tagLibImage != null)
                {
                    Debugger.show("TagLib provided embedded image for file.");
                    return tagLibImage;
                }
                else
                {
                    Debugger.show("TagLib extraction returned no image for file.");
                }
            }

            Debugger.show("TryGetCoverArtForFile finished with no image.");
            return null;
        }

        private async Task<StorageFile> TryExtractCoverFromTagLib(string filePath)
        {
            Debugger.show($"TryExtractCoverFromTagLib: attempting to extract from {filePath}");
            try
            {
                // TagLib operations are blocking; run on a background thread
                return await Task.Run(async () =>
                {
                    try
                    {
                        var tagFile = CoverArt.File.Create(filePath);
                        if (tagFile.Tag.Pictures != null && tagFile.Tag.Pictures.Length > 0)
                        {
                            Debugger.show($"Embedded pictures count: {tagFile.Tag.Pictures.Length} for {filePath}");
                            var pictureData = tagFile.Tag.Pictures[0].Data.Data;
                            string tempPath = Path.Combine(Path.GetTempPath(), "smtc_cover.jpg");
                            Debugger.show($"Writing embedded picture to temp: {tempPath}");
                            await File.WriteAllBytesAsync(tempPath, pictureData).ConfigureAwait(false);
                            Debugger.show($"Temp image written, retrieving StorageFile from path: {tempPath}");
                            return await StorageFile.GetFileFromPathAsync(tempPath).AsTask().ConfigureAwait(false);
                        }
                        else
                        {
                            Debugger.show("No embedded pictures in tag for this file.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debugger.show($"TagLib extraction failed: {ex.Message}");
                    }

                    return null;
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debugger.show($"TagLib wrapper failed: {ex.Message}");
            }

            return null;
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
