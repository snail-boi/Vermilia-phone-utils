using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace phone_utils
{
    internal class CoverCacheManager
    {
        private readonly string cachePath;
        private readonly string tempPath;
        private readonly string ffmpegPath;
        private readonly long maxCacheBytes = 200L * 1024L * 1024L; // 200 MB

        private readonly string indexFile;
        private readonly string folderIndexFile;
        private readonly string noCoverFile;

        private Dictionary<string, CacheEntry> index = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, string> folderIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, DateTime> nocover = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public CoverCacheManager(string ffmpegPath, string cachePath)
        {
            this.ffmpegPath = ffmpegPath;
            this.cachePath = cachePath;
            this.tempPath = Path.Combine(cachePath, "temp");

            this.indexFile = Path.Combine(cachePath, "index.json");
            this.folderIndexFile = Path.Combine(cachePath, "folder_index.json");
            this.noCoverFile = Path.Combine(cachePath, "nocover.json");

            EnsureCacheInitialized();
        }

        private void EnsureCacheInitialized()
        {
            try
            {
                if (!Directory.Exists(cachePath)) Directory.CreateDirectory(cachePath);
                if (!Directory.Exists(tempPath)) Directory.CreateDirectory(tempPath);

                if (File.Exists(indexFile))
                {
                    try { index = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(indexFile)) ?? new Dictionary<string, CacheEntry>(); } catch { index = new Dictionary<string, CacheEntry>(); }
                }

                if (File.Exists(folderIndexFile))
                {
                    try { folderIndex = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(folderIndexFile)) ?? new Dictionary<string, string>(); } catch { folderIndex = new Dictionary<string, string>(); }
                }

                if (File.Exists(noCoverFile))
                {
                    try { nocover = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(noCoverFile)) ?? new Dictionary<string, DateTime>(); } catch { nocover = new Dictionary<string, DateTime>(); }
                }
            }
            catch (Exception ex)
            {
                Debugger.show("EnsureCacheInitialized failed: " + ex.Message);
            }
        }

        private void SaveIndex()
        {
            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(indexFile, JsonSerializer.Serialize(index, opts));
            }
            catch (Exception ex)
            {
                Debugger.show("SaveIndex failed: " + ex.Message);
            }
        }

        private void SaveFolderIndex()
        {
            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(folderIndexFile, JsonSerializer.Serialize(folderIndex, opts));
            }
            catch (Exception ex)
            {
                Debugger.show("SaveFolderIndex failed: " + ex.Message);
            }
        }

        private void SaveNoCover()
        {
            try
            {
                var opts = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(noCoverFile, JsonSerializer.Serialize(nocover, opts));
            }
            catch (Exception ex)
            {
                Debugger.show("SaveNoCover failed: " + ex.Message);
            }
        }

        private static string ComputeKey(string deviceId, string remotePath)
        {
            using var sha = SHA256.Create();
            var input = Encoding.UTF8.GetBytes(deviceId + "|" + remotePath);
            var hash = sha.ComputeHash(input);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        private static string ComputeFolderKey(string deviceId, string folderPath)
        {
            using var sha = SHA256.Create();
            var input = Encoding.UTF8.GetBytes(deviceId + "|" + folderPath);
            var hash = sha.ComputeHash(input);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        private class CacheEntry
        {
            public string FileName { get; set; }
            public long Size { get; set; }
            public DateTime LastAccessUtc { get; set; }
            public string FolderKey { get; set; }
        }

        public void ClearCache()
        {
            try
            {
                if (Directory.Exists(cachePath))
                {
                    foreach (var f in Directory.GetFiles(cachePath))
                    {
                        try { File.Delete(f); } catch { }
                    }

                    var temp = Path.Combine(cachePath, "temp");
                    if (Directory.Exists(temp))
                    {
                        try { Directory.Delete(temp, true); } catch { }
                    }
                }

                index.Clear();
                folderIndex.Clear();
                nocover.Clear();
                SaveIndex();
                SaveFolderIndex();
                SaveNoCover();
                Directory.CreateDirectory(Path.Combine(cachePath, "temp"));

                Debugger.show("Cleared cover cache");
            }
            catch (Exception ex)
            {
                Debugger.show("ClearCache failed: " + ex.Message);
            }
        }

        public async Task<string> GetImagePathForNowPlayingAsync(string deviceId, string remoteFilePath)
        {
            try
            {
                if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(remoteFilePath)) return null;

                string folderPath = Path.GetDirectoryName(remoteFilePath).Replace("\\", "/");
                string key = ComputeKey(deviceId, remoteFilePath);
                string folderKey = ComputeFolderKey(deviceId, folderPath);

                // 1. If folder mapping exists, use that
                if (folderIndex.TryGetValue(folderKey, out var mappedImageKey))
                {
                    if (index.TryGetValue(mappedImageKey, out var entry) && File.Exists(Path.Combine(cachePath, entry.FileName)))
                    {
                        entry.LastAccessUtc = DateTime.UtcNow;
                        SaveIndex();
                        Debugger.show("Using folder-mapped cover for " + remoteFilePath);
                        return Path.Combine(cachePath, entry.FileName);
                    }
                }

                // 2. If an entry exists for this exact file, return it
                if (index.TryGetValue(key, out var existing) && File.Exists(Path.Combine(cachePath, existing.FileName)))
                {
                    existing.LastAccessUtc = DateTime.UtcNow;
                    SaveIndex();
                    Debugger.show("Found cached cover for " + remoteFilePath);
                    return Path.Combine(cachePath, existing.FileName);
                }

                // 3. If marked nocover, skip
                if (nocover.ContainsKey(key))
                {
                    Debugger.show("Key marked nocover: " + key);
                    return null;
                }

                // 4. Attempt to extract embedded cover art from the file itself first (preferred)
                try
                {
                    string remoteExt_emb = Path.GetExtension(remoteFilePath);
                    string tempPull_emb = Path.Combine(tempPath, key + remoteExt_emb);
                    Debugger.show("Pulling remote file to temp for embedded cover extraction: " + remoteFilePath + " -> " + tempPull_emb);
                    await AdbHelper.RunAdbAsync($"-s {deviceId} pull \"{remoteFilePath}\" \"{tempPull_emb}\"");

                    if (File.Exists(tempPull_emb))
                    {
                        string cachedFilename_emb = key + ".jpg";
                        string cachedFull_emb = Path.Combine(cachePath, cachedFilename_emb);

                        var extractedEmbedded = await RunFfmpegExtractAsync(tempPull_emb, cachedFull_emb);
                        try { File.Delete(tempPull_emb); } catch { }

                        if (extractedEmbedded && File.Exists(cachedFull_emb))
                        {
                            var fi = new FileInfo(cachedFull_emb);
                            var entry = new CacheEntry { FileName = cachedFilename_emb, Size = fi.Length, LastAccessUtc = DateTime.UtcNow, FolderKey = null };
                            index[key] = entry;
                            SaveIndex();
                            EnforceCacheSizeLimit();
                            Debugger.show("Extracted and cached embedded cover for " + remoteFilePath);
                            return cachedFull_emb;
                        }
                        else
                        {
                            Debugger.show("No embedded cover extracted for " + remoteFilePath);
                        }
                    }
                    else
                    {
                        Debugger.show("Failed to pull remote file for embedded extraction: " + remoteFilePath);
                    }
                }
                catch (Exception ex)
                {
                    Debugger.show("Embedded extraction attempt failed: " + ex.Message);
                }

                // 3. Check for cover.* in the remote folder (pull it). Prioritize subfolder cover.jpg/png.
                var possibleNames = new[] { "cover.jpg", "cover.png", "folder.jpg" };

                foreach (var name in possibleNames)
                {
                    // First: check one level deeper subfolders for cover (prefer these)
                    try
                    {
                        // use shell glob to match any one-level subfolder under folderPath
                        var subCheck = await AdbHelper.RunAdbCaptureAsync($"-s {deviceId} shell ls \"{folderPath}/*/{name}\"");
                        if (!string.IsNullOrWhiteSpace(subCheck) && !subCheck.Contains("No such file"))
                        {
                            // ls may list multiple matches; take first non-empty line
                            var firstLine = subCheck.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                            if (!string.IsNullOrWhiteSpace(firstLine))
                            {
                                string cand = firstLine.Trim();
                                Debugger.show("Found subfolder image on device: " + cand);
                                string tempImg = Path.Combine(tempPath, Guid.NewGuid().ToString() + Path.GetExtension(name));
                                await AdbHelper.RunAdbAsync($"-s {deviceId} pull \"{cand}\" \"{tempImg}\"");
                                if (File.Exists(tempImg))
                                {
                                    string cachedFile = key + ".jpg";
                                    string cachedPath = Path.Combine(cachePath, cachedFile);
                                    var ffOut = await RunFfmpegExtractAsync(tempImg, cachedPath);
                                    try { File.Delete(tempImg); } catch { }

                                    if (ffOut && File.Exists(cachedPath))
                                    {
                                        var fi = new FileInfo(cachedPath);
                                        var entry = new CacheEntry { FileName = cachedFile, Size = fi.Length, LastAccessUtc = DateTime.UtcNow, FolderKey = null };
                                        index[key] = entry;
                                        SaveIndex();
                                        EnforceCacheSizeLimit();
                                        Debugger.show("Cached subfolder image for file: " + remoteFilePath);
                                        return cachedPath;
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debugger.show("Subfolder image check failed: " + ex.Message);
                    }

                    // Next: check in the same folder
                    try
                    {
                        string remoteCandidate = CombineRemotePath(folderPath, name);
                        var lsResult = await AdbHelper.RunAdbCaptureAsync($"-s {deviceId} shell ls \"{remoteCandidate}\"");
                        if (!string.IsNullOrWhiteSpace(lsResult) && !lsResult.Contains("No such file"))
                        {
                            Debugger.show("Found folder-level image on device: " + remoteCandidate);
                            string tempImg = Path.Combine(tempPath, Guid.NewGuid().ToString() + Path.GetExtension(name));
                            await AdbHelper.RunAdbAsync($"-s {deviceId} pull \"{remoteCandidate}\" \"{tempImg}\"");
                            if (File.Exists(tempImg))
                            {
                                string cachedFile = key + ".jpg";
                                string cachedPath = Path.Combine(cachePath, cachedFile);
                                var ffOut = await RunFfmpegExtractAsync(tempImg, cachedPath);
                                try { File.Delete(tempImg); } catch { }

                                if (ffOut && File.Exists(cachedPath))
                                {
                                    var fi = new FileInfo(cachedPath);
                                    var entry = new CacheEntry { FileName = cachedFile, Size = fi.Length, LastAccessUtc = DateTime.UtcNow, FolderKey = null };
                                    index[key] = entry;
                                    SaveIndex();
                                    EnforceCacheSizeLimit();
                                    Debugger.show("Cached folder image for file: " + remoteFilePath);
                                    return cachedPath;
                                }
                                else
                                {
                                    Debugger.show("ffmpeg failed to extract folder image for " + remoteCandidate);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debugger.show("Folder-level image check failed: " + ex.Message);
                    }
                }

                // Fallback: search for any image files in the same folder, then one level deeper.
                try
                {
                    var imgExts = new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" };

                    // list entries in folder with indicator for directories
                    var listing = await AdbHelper.RunAdbCaptureAsync($"-s {deviceId} shell ls -1p \"{folderPath}\"");
                    if (!string.IsNullOrWhiteSpace(listing))
                    {
                        var entries = listing.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();

                        // first, look for image files in same folder and prefer those with common names
                        var sameFolderImages = entries
                            .Where(e => imgExts.Contains(Path.GetExtension(e).ToLowerInvariant()))
                            .ToList();

                        string PickBestImageFromList(List<string> list)
                        {
                            if (list == null || list.Count == 0) return null;
                            // prefer names containing cover/front/folder/album/art
                            var preferred = list.FirstOrDefault(n => Regex.IsMatch(n, "(?i)^(cover|front|folder|album|art)\\b") || Regex.IsMatch(n, "(?i)(cover|front|folder|album|art)"));
                            if (preferred != null) return preferred;
                            return list.First();
                        }

                        var pick = PickBestImageFromList(sameFolderImages);
                        if (!string.IsNullOrEmpty(pick))
                        {
                            string remoteCandidate = CombineRemotePath(folderPath, pick);
                            Debugger.show("Found image in same folder on device: " + remoteCandidate);
                            string tempImg = Path.Combine(tempPath, Guid.NewGuid().ToString() + Path.GetExtension(pick));
                            await AdbHelper.RunAdbAsync($"-s {deviceId} pull \"{remoteCandidate}\" \"{tempImg}\"");
                            if (File.Exists(tempImg))
                            {
                                string cachedFile = key + ".jpg";
                                string cachedPath = Path.Combine(cachePath, cachedFile);
                                var ffOut = await RunFfmpegExtractAsync(tempImg, cachedPath);
                                try { File.Delete(tempImg); } catch { }

                                if (ffOut && File.Exists(cachedPath))
                                {
                                    var fi = new FileInfo(cachedPath);
                                    var entry = new CacheEntry { FileName = cachedFile, Size = fi.Length, LastAccessUtc = DateTime.UtcNow, FolderKey = null };
                                    index[key] = entry;
                                    SaveIndex();
                                    EnforceCacheSizeLimit();
                                    Debugger.show("Cached discovered folder image for file: " + remoteFilePath);
                                    return cachedPath;
                                }
                            }
                        }

                        // If none in same folder, check one-level subdirectories
                        var dirs = entries.Where(e => e.EndsWith("/")).Select(d => d.TrimEnd('/')).ToList();
                        foreach (var d in dirs)
                        {
                            try
                            {
                                // Only consider this subfolder if the remote file actually lives inside it.
                                // This prevents using sibling-album covers for files that are in the parent folder.
                                var subfolderPath = folderPath + "/" + d;
                                if (!remoteFilePath.StartsWith(subfolderPath, StringComparison.OrdinalIgnoreCase))
                                    continue;

                                var subList = await AdbHelper.RunAdbCaptureAsync($"-s {deviceId} shell ls -1p \"{subfolderPath}\"");
                                if (string.IsNullOrWhiteSpace(subList)) continue;
                                var subEntries = subList.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
                                var imgs = subEntries.Where(e => imgExts.Contains(Path.GetExtension(e).ToLowerInvariant())).ToList();
                                var pickSub = PickBestImageFromList(imgs);
                                if (!string.IsNullOrEmpty(pickSub))
                                {
                                    string cand = CombineRemotePath(subfolderPath, pickSub);
                                    Debugger.show("Found image in subfolder on device: " + cand);
                                    string tempImg = Path.Combine(tempPath, Guid.NewGuid().ToString() + Path.GetExtension(pickSub));
                                    await AdbHelper.RunAdbAsync($"-s {deviceId} pull \"{cand}\" \"{tempImg}\"");
                                    if (File.Exists(tempImg))
                                    {
                                        string cachedFile = key + ".jpg";
                                        string cachedPath = Path.Combine(cachePath, cachedFile);
                                        var ffOut = await RunFfmpegExtractAsync(tempImg, cachedPath);
                                        try { File.Delete(tempImg); } catch { }

                                        if (ffOut && File.Exists(cachedPath))
                                        {
                                            var fi = new FileInfo(cachedPath);
                                            var entry = new CacheEntry { FileName = cachedFile, Size = fi.Length, LastAccessUtc = DateTime.UtcNow, FolderKey = null };
                                            index[key] = entry;
                                            SaveIndex();
                                            EnforceCacheSizeLimit();
                                            Debugger.show("Cached discovered subfolder image for file: " + remoteFilePath);
                                            return cachedPath;
                                        }
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debugger.show("Fallback folder image search failed: " + ex.Message);
                }

                // 5. Pull the file itself to temp and run ffmpeg to extract
                string remoteExt = Path.GetExtension(remoteFilePath);
                string tempPull = Path.Combine(tempPath, key + remoteExt);
                Debugger.show("Pulling remote file to temp: " + remoteFilePath + " -> " + tempPull);
                await AdbHelper.RunAdbAsync($"-s {deviceId} pull \"{remoteFilePath}\" \"{tempPull}\"");

                if (!File.Exists(tempPull))
                {
                    Debugger.show("Failed to pull remote file: " + remoteFilePath);
                    // mark nocover to avoid repeated attempts
                    nocover[key] = DateTime.UtcNow;
                    SaveNoCover();
                    return null;
                }

                string cachedFilename = key + ".jpg";
                string cachedFull = Path.Combine(cachePath, cachedFilename);

                var extracted = await RunFfmpegExtractAsync(tempPull, cachedFull);

                try { File.Delete(tempPull); } catch { }

                if (extracted && File.Exists(cachedFull))
                {
                    var fi = new FileInfo(cachedFull);
                    var entry = new CacheEntry { FileName = cachedFilename, Size = fi.Length, LastAccessUtc = DateTime.UtcNow, FolderKey = folderKey };
                    index[key] = entry;
                    SaveIndex();
                    EnforceCacheSizeLimit();
                    Debugger.show("Extracted and cached cover for " + remoteFilePath);
                    return cachedFull;
                }
                else
                {
                    Debugger.show("No cover extracted for " + remoteFilePath + "; marking as nocover");
                    nocover[key] = DateTime.UtcNow;
                    SaveNoCover();
                    return null;
                }
            }
            catch (Exception ex)
            {
                Debugger.show("GetImagePathForNowPlayingAsync failed: " + ex.Message);
                return null;
            }
        }

        private static string CombineRemotePath(string folder, string name)
        {
            if (folder.EndsWith("/")) return folder + name;
            return folder + "/" + name;
        }

        private async Task<bool> RunFfmpegExtractAsync(string inputPath, string outputJpgPath)
        {
            try
            {
                if (!File.Exists(ffmpegPath))
                {
                    Debugger.show("ffmpeg not found at: " + ffmpegPath);
                    return false;
                }

                // Ensure output directory
                var outDir = Path.GetDirectoryName(outputJpgPath);
                if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

                // Build ffmpeg command: ffmpeg -i "input" -an -vcodec copy -y "output.jpg"
                var args = $"-i \"{inputPath}\" -an -vcodec copy -y \"{outputJpgPath}\"";
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                Debugger.show("Running ffmpeg: " + psi.FileName + " " + psi.Arguments);

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null)
                {
                    Debugger.show("Failed to start ffmpeg process");
                    return false;
                }

                // read output for debug
                string stderr = await proc.StandardError.ReadToEndAsync();
                string stdout = await proc.StandardOutput.ReadToEndAsync();
                proc.WaitForExit();

                Debugger.show("ffmpeg exit code: " + proc.ExitCode);
                if (!string.IsNullOrWhiteSpace(stderr)) Debugger.show("ffmpeg stderr: " + stderr);

                return File.Exists(outputJpgPath) && new FileInfo(outputJpgPath).Length > 0;
            }
            catch (Exception ex)
            {
                Debugger.show("RunFfmpegExtractAsync exception: " + ex.Message);
                return false;
            }
        }

        private void EnforceCacheSizeLimit()
        {
            try
            {
                long total = index.Values.Sum(e => e.Size);
                if (total <= maxCacheBytes) return;

                Debugger.show($"Cache size {total} bytes exceeds limit {maxCacheBytes}. Evicting...");

                var ordered = index.OrderBy(kv => kv.Value.LastAccessUtc).ToList();
                foreach (var kv in ordered)
                {
                    try
                    {
                        var path = Path.Combine(cachePath, kv.Value.FileName);
                        if (File.Exists(path)) File.Delete(path);
                    }
                    catch { }

                    index.Remove(kv.Key);
                    total = index.Values.Sum(e => e.Size);
                    if (total <= maxCacheBytes) break;
                }

                SaveIndex();
            }
            catch (Exception ex)
            {
                Debugger.show("EnforceCacheSizeLimit failed: " + ex.Message);
            }
        }
    }
}
