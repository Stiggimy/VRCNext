using Newtonsoft.Json.Linq;
using VRCNext.Services;
using VRCNext.Services.Helpers;

namespace VRCNext;

// Owns all photo/library related state, logic, and message handling.

public class PhotosController
{
    private readonly CoreLibrary _core;
    private readonly FriendsController _friends;
    private readonly InstanceController _instance;

    // Photo State
    private List<string> _favorites;
    private string _vrcPhotoDir = "";
    private FileSystemWatcher? _vrcPhotoWatcher;
    private List<LibFileEntry> _libFileCache = new();
    private int _libFileCacheTotal = 0;
    private bool _libCacheReady = false;
    private readonly List<WebhookService.PostRecord> _postHistory = new();
    private int _fileCount;
    private double _totalSizeMB;

    // Library file cache entry
    public record LibFileEntry(FileInfo Fi, int FolderIndex, string Folder);

    private static readonly HashSet<string> _imgExts =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

    // Public Accessors (for other domains)
    public List<string> Favorites => _favorites;
    public string VrcPhotoDir => _vrcPhotoDir;
    public FileSystemWatcher? VrcPhotoWatcher => _vrcPhotoWatcher;

    // Constructor

    public PhotosController(CoreLibrary core, FriendsController friends, InstanceController instance)
    {
        _core = core;
        _friends = friends;
        _instance = instance;
        _favorites = FavoritedImagesStore.Load();
    }

    // Public Methods

    public string GetVirtualMediaUrl(string filePath)
    {
        // Check watch-folder routes first
        for (int i = 0; i < _core.Settings.WatchFolders.Count; i++)
        {
            var folder = _core.Settings.WatchFolders[i];
            if (!Directory.Exists(folder)) continue;
            if (filePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            {
                var rel = filePath.Substring(folder.Length).TrimStart('\\', '/').Replace('\\', '/');
                return $"http://localhost:{_core.HttpPort}/media{i}/{Uri.EscapeDataString(rel)}";
            }
        }
        // Fallback: VRChat screenshot folder
        var vrcPhotoDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VRChat");
        if (filePath.StartsWith(vrcPhotoDir, StringComparison.OrdinalIgnoreCase))
        {
            var rel = filePath.Substring(vrcPhotoDir.Length).TrimStart('\\', '/').Replace('\\', '/');
            return $"http://localhost:{_core.HttpPort}/vrcphotos/{Uri.EscapeDataString(rel)}";
        }
        return "";
    }

    // Timeline - photo bootstrap (import existing photos)

    // Imports existing photo_players.json entries not yet in timeline
    public async Task BootstrapPhotoTimeline()
    {
        try
        {
            // Build set of filenames already in timeline
            var existingFiles = new HashSet<string>(
                _core.Timeline.GetEvents()
                    .Where(e => e.Type == "photo" && !string.IsNullOrEmpty(e.PhotoPath))
                    .Select(e => Path.GetFileName(e.PhotoPath)),
                StringComparer.OrdinalIgnoreCase);

            if (_core.PhotoPlayersStore.Photos.Count == 0) return;

            // Build list of search roots (VRChat photo dir + watch folders)
            var searchRoots = new List<string>();
            var vrcPhotoDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VRChat");
            if (Directory.Exists(vrcPhotoDir)) searchRoots.Add(vrcPhotoDir);
            foreach (var folder in _core.Settings.WatchFolders.Where(Directory.Exists))
            {
                if (!searchRoots.Any(r => r.Equals(folder, StringComparison.OrdinalIgnoreCase)))
                    searchRoots.Add(folder);
            }

            int added = 0;
            foreach (var (fileName, rec) in _core.PhotoPlayersStore.Photos)
            {
                if (existingFiles.Contains(fileName)) continue;

                // Find the actual file on disk
                string? filePath = null;
                foreach (var root in searchRoots)
                {
                    try
                    {
                        var found = Directory.GetFiles(root, fileName, SearchOption.AllDirectories)
                                             .FirstOrDefault();
                        if (found != null) { filePath = found; break; }
                    }
                    catch { }
                }
                if (filePath == null) continue;

                var photoUrl = GetVirtualMediaUrl(filePath);
                if (string.IsNullOrEmpty(photoUrl)) continue;

                // Parse timestamp from VRChat filename (VRChat_YYYY-MM-DD_HH-mm-ss.fff_...)
                DateTime ts;
                try
                {
                    var m = System.Text.RegularExpressions.Regex.Match(fileName,
                        @"VRChat_(\d{4}-\d{2}-\d{2})_(\d{2}-\d{2}-\d{2})");
                    ts = m.Success
                        ? DateTime.ParseExact($"{m.Groups[1].Value} {m.Groups[2].Value}",
                            "yyyy-MM-dd HH-mm-ss",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeLocal).ToUniversalTime()
                        : new FileInfo(filePath).LastWriteTimeUtc;
                }
                catch { ts = new FileInfo(filePath).LastWriteTimeUtc; }

                var ev = new TimelineService.TimelineEvent
                {
                    Type      = "photo",
                    Timestamp = ts.ToString("o"),
                    WorldId   = rec.WorldId,
                    PhotoPath = filePath,
                    PhotoUrl  = photoUrl,
                    Players   = rec.Players.Select(p => new TimelineService.PlayerSnap
                    {
                        UserId      = p.UserId,
                        DisplayName = p.DisplayName,
                        Image       = _friends.ResolvePlayerImage(p.UserId, p.Image)
                    }).ToList()
                };
                _core.Timeline.AddEvent(ev);
                existingFiles.Add(fileName);
                added++;
            }

            if (added > 0)
                _core.SendToJS("log", new { msg = $"[TIMELINE] Imported {added} existing photo(s)", color = "sec" });
        }
        catch (Exception ex)
        {
            try { _core.SendToJS("log", new { msg = $"[TIMELINE] Bootstrap error: {ex.Message}", color = "err" }); } catch { }
        }
    }

    // File Watcher - Post to Discord
    public async void OnNewFile(object? sender, FileWatcherService.FileArg e)
    {
        // Snapshot players for VRChat screenshots
        SnapshotPhotoPlayers(e.FilePath);

        // Inject into library without rescanning
        AddFileToLibrary(e.FilePath);

        await PostFile(e.FilePath, false, e.SizeMB);
    }

    public void StartVrcPhotoWatcher()
    {
        if (_vrcPhotoWatcher != null) return; // already running

        var vrcPhotoDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "VRChat");
        if (!Directory.Exists(vrcPhotoDir))
        {
            try { Directory.CreateDirectory(vrcPhotoDir); }
            catch { return; }
        }

        // Store for HttpListener /vrcphotos/ route
        _vrcPhotoDir = vrcPhotoDir;

        _vrcPhotoWatcher = new FileSystemWatcher(vrcPhotoDir)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
            Filter = "VRChat_*.png"
        };
        _vrcPhotoWatcher.Created += (s, e) =>
        {
            // Small delay so file is fully written
            Task.Run(async () =>
            {
                await Task.Delay(2000);
                try { SnapshotPhotoPlayers(e.FullPath); AddFileToLibrary(e.FullPath); }
                catch { }
            });
        };

        // Also snapshot any VRChat photos from watch folders
        foreach (var folder in _core.Settings.WatchFolders.Where(Directory.Exists))
        {
            if (folder.Equals(vrcPhotoDir, StringComparison.OrdinalIgnoreCase)) continue;
            // The relay FileWatcher already handles these via OnNewFile
        }
    }

    public async Task PostFile(string filePath, bool manual, double sizeMB = 0)
    {
        var fileName = Path.GetFileName(filePath);
        if (sizeMB == 0)
        {
            try { sizeMB = new FileInfo(filePath).Length / 1048576.0; } catch { return; }
        }

        var typeStr = FileWatcherService.ImgExt.Contains(Path.GetExtension(filePath)) ? "image" : "video";
        var prefix = manual ? "Manual post" : "New file";
        _core.SendToJS("log", new { msg = $"{prefix}: {fileName} ({sizeMB:F1} MB)", color = "default" });

        var whs = _core.Settings.Webhooks.Where(w => w.Enabled && !string.IsNullOrWhiteSpace(w.Url)).ToList();

        foreach (var wh in whs)
        {
            var result = await _core.Webhook.PostFileAsync(wh.Url, filePath, _core.Settings.BotName, _core.Settings.BotAvatarUrl);
            if (result.Success)
            {
                _core.SendToJS("log", new { msg = $"  Posted to '{wh.Name}'", color = "ok" });
                _fileCount++;
                _totalSizeMB += sizeMB;

                var record = new WebhookService.PostRecord
                {
                    MessageId = result.MessageId ?? "",
                    WebhookUrl = wh.Url,
                    WebhookName = wh.Name,
                    FileName = fileName,
                    SizeMB = sizeMB,
                };
                _postHistory.Add(record);

                _core.SendToJS("stats", new { files = _fileCount, size = $"{_totalSizeMB:F1} MB" });
                _core.SendToJS("filePosted", new
                {
                    name = fileName,
                    channel = wh.Name,
                    size = $"{sizeMB:F1} MB",
                    time = record.PostedAt.ToString("HH:mm:ss"),
                    messageId = record.MessageId,
                    webhookUrl = wh.Url,
                });
            }
            else
            {
                _core.SendToJS("log", new { msg = $"  Error '{wh.Name}': {result.Error}", color = "err" });
            }
        }
    }

    // Message Handler

    public async Task HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "scanLibrary":
                ScanLibraryFolders(false);
                break;

            case "scanLibraryForce":
                _libCacheReady = false;
                ScanLibraryFolders(true);
                break;

            case "loadLibraryPage":
                var libOffset = msg["offset"]?.Value<int>() ?? 0;
                _ = Task.Run(() =>
                {
                    var items = BuildLibraryItems(libOffset, 100);
                    _core.SendToJS("libraryPageData", new
                    {
                        files = items,
                        total = _libFileCacheTotal,
                        offset = libOffset,
                        hasMore = libOffset + items.Count < _libFileCacheTotal,
                    });
                });
                break;

            case "deleteLibraryFile":
                var delPath = msg["path"]?.ToString();
                if (!string.IsNullOrEmpty(delPath))
                {
                    try
                    {
                        var fullDelPath = Path.GetFullPath(delPath);
                        bool inAllowedFolder = _core.Settings.WatchFolders.Any(f =>
                            !string.IsNullOrEmpty(f) &&
                            fullDelPath.StartsWith(
                                Path.GetFullPath(f).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                                StringComparison.OrdinalIgnoreCase));
                        if (!inAllowedFolder)
                        {
                            _core.SendToJS("log", new { msg = "Delete blocked: path outside watch folders.", color = "err" });
                            break;
                        }
                        if (File.Exists(fullDelPath))
                        {
                            File.Delete(fullDelPath);
                            _favorites.Remove(delPath);
                            FavoritedImagesStore.Save(_favorites);
                            _core.SendToJS("log", new { msg = $"Deleted: {Path.GetFileName(fullDelPath)}", color = "ok" });
                            _core.SendToJS("libraryFileDeleted", new { path = delPath });
                        }
                        else
                        {
                            _core.SendToJS("log", new { msg = "File not found", color = "err" });
                        }
                    }
                    catch (Exception ex)
                    {
                        _core.SendToJS("log", new { msg = $"Delete error: {ex.Message}", color = "err" });
                    }
                }
                break;

            case "copyImageToClipboard":
                {
                    var clipPath = msg["path"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(clipPath) && File.Exists(clipPath))
                    {
                        try
                        {
                            // Use PowerShell to copy image to clipboard natively
                            var escaped = clipPath.Replace("'", "''");
                            var psi = new System.Diagnostics.ProcessStartInfo("powershell",
                                $"-NonInteractive -WindowStyle Hidden -Command \"Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.Clipboard]::SetImage([System.Drawing.Image]::FromFile('{escaped}'))\"")
                            { CreateNoWindow = true, UseShellExecute = false };
                            System.Diagnostics.Process.Start(psi);
                            _core.SendToJS("toast", new { ok = true, msg = "Image copied to clipboard" });
                        }
                        catch (Exception ex)
                        {
                            _core.SendToJS("toast", new { ok = false, msg = $"Clipboard failed: {ex.Message}" });
                        }
                    }
                }
                break;

            case "addFavorite":
                var favPath = msg["path"]?.ToString();
                if (!string.IsNullOrEmpty(favPath) && !_favorites.Contains(favPath))
                {
                    _favorites.Add(favPath);
                    FavoritedImagesStore.Save(_favorites);
                }
                break;

            case "removeFavorite":
                var unfavPath = msg["path"]?.ToString();
                if (!string.IsNullOrEmpty(unfavPath))
                {
                    _favorites.Remove(unfavPath);
                    FavoritedImagesStore.Save(_favorites);
                }
                break;

            case "manualPost":
                var filePath = msg["filePath"]?.ToString();
                if (filePath != null) await PostFile(filePath, true);
                break;

            case "dropFiles":
                var files = msg["files"]?.ToObject<string[]>();
                if (files != null)
                {
                    foreach (var f in files)
                    {
                        var ext = Path.GetExtension(f).ToLower();
                        if (FileWatcherService.ImgExt.Contains(ext) || FileWatcherService.VidExt.Contains(ext))
                            await PostFile(f, true);
                    }
                }
                break;

            case "deletePost":
                var msgId = msg["messageId"]?.ToString();
                var whUrl = msg["webhookUrl"]?.ToString();
                if (msgId != null && whUrl != null)
                {
                    var ok = await _core.Webhook.DeleteAsync(whUrl, msgId);
                    _core.SendToJS("deleteResult", new { messageId = msgId, success = ok });
                    if (ok) _postHistory.RemoveAll(p => p.MessageId == msgId);
                }
                break;
        }
    }

    // Private Methods

    // Add a single new file to the library cache and push it to JS immediately.
    // No-op if the cache isn't ready yet (the next scan will pick it up).
    private void AddFileToLibrary(string filePath)
    {
        if (!_libCacheReady) return;
        try
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists) return;

            // Find which watch folder contains this file
            int folderIdx = -1;
            string folder = "";
            for (int i = 0; i < _core.Settings.WatchFolders.Count; i++)
            {
                if (filePath.StartsWith(_core.Settings.WatchFolders[i], StringComparison.OrdinalIgnoreCase))
                { folderIdx = i; folder = _core.Settings.WatchFolders[i]; break; }
            }
            if (folderIdx < 0) return;

            // Deduplicate
            if (_libFileCache.Any(e => e.Fi.FullName.Equals(filePath, StringComparison.OrdinalIgnoreCase))) return;

            var entry = new LibFileEntry(fi, folderIdx, folder);
            _libFileCache.Insert(0, entry);
            _libFileCacheTotal = _libFileCache.Count;

            var isImg  = FileWatcherService.ImgExt.Contains(fi.Extension);
            var sizeMB = fi.Length / 1048576.0;
            var rel    = Path.GetRelativePath(folder, filePath).Replace('\\', '/');
            var url    = $"http://localhost:{_core.HttpPort}/media{folderIdx}/{Uri.EscapeDataString(rel).Replace("%2F", "/")}";

            string? worldId = null;
            List<object>? players = null;
            if (isImg)
            {
                var rec = _core.PhotoPlayersStore.GetPhotoRecord(fi.Name);
                if (rec != null)
                {
                    worldId = rec.WorldId;
                    players = rec.Players.Select(p => (object)new
                    {
                        userId = p.UserId, displayName = p.DisplayName,
                        image  = _friends.ResolvePlayerImage(p.UserId, p.Image)
                    }).ToList();
                }
            }

            _core.SendToJS("libraryNewFile", new
            {
                name     = fi.Name,
                path     = fi.FullName,
                folder,
                type     = isImg ? "image" : "video",
                size     = sizeMB < 1 ? $"{fi.Length / 1024.0:F0} KB" : $"{sizeMB:F1} MB",
                modified = fi.LastWriteTime.ToString("o"),
                time     = fi.LastWriteTime.ToString("HH:mm"),
                url,
                worldId  = worldId ?? "",
                players  = players ?? new List<object>(),
            });
        }
        catch { }
    }

    private void SnapshotPhotoPlayers(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (!_imgExts.Contains(Path.GetExtension(filePath)))
            return;
        if (_core.PhotoPlayersStore.GetPhotoRecord(fileName) != null) return; // already recorded

        try
        {
            var logPlayers = _core.LogWatcher.GetCurrentPlayers();
            var wid = _core.LogWatcher.CurrentWorldId ?? "";

            var players = new List<(string userId, string displayName, string image)>();

            foreach (var p in logPlayers)
            {
                var img = "";
                if (!string.IsNullOrEmpty(p.UserId))
                {
                    if (_friends.TryGetNameImage(p.UserId, out var fi) && !string.IsNullOrEmpty(fi.image))
                        img = fi.image;
                }
                players.Add((p.UserId, p.DisplayName, img));
            }

            // Don't create an empty record; it would poison the cache and prevent
            // re-snapshot on subsequent library loads when VRChat data becomes available.
            if (string.IsNullOrEmpty(wid) && players.Count == 0) return;

            _core.PhotoPlayersStore.RecordPhoto(fileName, players, wid);
            _core.PhotoPlayersStore.Save();

            // Async: fetch missing images and update record
            _ = Task.Run(async () =>
            {
                var needFetch = players.Where(p => string.IsNullOrEmpty(p.image) && !string.IsNullOrEmpty(p.userId)).ToList();
                if (needFetch.Count == 0) return;

                var semaphore = new SemaphoreSlim(5);
                var updated = false;
                var tasks = needFetch.Select(async p =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        var profile = await _core.VrcApi.GetUserAsync(p.userId);
                        if (profile != null)
                        {
                            var img = VRChatApiService.GetUserImage(profile);
                            var rec = _core.PhotoPlayersStore.GetPhotoRecord(fileName);
                            if (rec != null)
                            {
                                var pi = rec.Players.FirstOrDefault(x => x.UserId == p.userId);
                                if (pi != null) { pi.Image = img; updated = true; }
                            }
                        }
                    }
                    finally { semaphore.Release(); }
                });
                await Task.WhenAll(tasks);
                if (updated) _core.PhotoPlayersStore.Save();
            });

            _core.SendToJS("log", new { msg = $"\U0001f4f8 Captured {players.Count} players for {fileName}", color = "sec" });

            // Timeline: log photo event
            var photoUrl = GetVirtualMediaUrl(filePath);
            var photoEv = new TimelineService.TimelineEvent
            {
                Type      = "photo",
                Timestamp = DateTime.UtcNow.ToString("o"),
                WorldId   = wid,
                PhotoPath = filePath,
                PhotoUrl  = photoUrl,
                Players   = players.Select(p => new TimelineService.PlayerSnap
                {
                    UserId      = p.userId,
                    DisplayName = p.displayName,
                    Image       = p.image
                }).ToList()
            };
            _core.Timeline.AddEvent(photoEv);
            _core.SendToJS("timelineEvent", _instance.BuildTimelinePayload(photoEv));
        }
        catch { }
    }

    // Media Library -- enumerate files and send all metadata to JS in one shot.
    // force=false: serve from in-memory cache instantly if already scanned (tab re-open).
    // force=true : rescan filesystem (Refresh button).
    private void ScanLibraryFolders(bool force = false)
    {
        // Cache hit -- serve instantly without touching disk, then enrich in background
        if (!force && _libCacheReady && _libFileCache.Count > 0)
        {
            var all = BuildLibraryItemsFast();
            _core.SendToJS("libraryData", new { files = all, total = all.Count, hasMore = false });
            _ = Task.Run(() => EnrichLibraryWorldIds());
            return;
        }

        _libCacheReady = false;
        Task.Run(() =>
        {
            try
            {
                var allExts = FileWatcherService.ImgExt.Concat(FileWatcherService.VidExt)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var entries = new List<LibFileEntry>();

                for (int fi = 0; fi < _core.Settings.WatchFolders.Count; fi++)
                {
                    var folder = _core.Settings.WatchFolders[fi];
                    if (!Directory.Exists(folder)) continue;
                    try
                    {
                        new DirectoryInfo(folder)
                            .EnumerateFiles("*.*", SearchOption.AllDirectories)
                            .Where(f => allExts.Contains(f.Extension))
                            .ToList()
                            .ForEach(f => entries.Add(new LibFileEntry(f, fi, folder)));
                    }
                    catch { }
                }

                _libFileCache      = entries.OrderByDescending(e => e.Fi.LastWriteTime).ToList();
                _libFileCacheTotal = _libFileCache.Count;
                _libCacheReady     = true;

                var all = BuildLibraryItemsFast();
                _core.SendToJS("libraryData", new { files = all, total = all.Count, hasMore = false });

                // Background pass: read PNG world IDs without blocking the UI
                EnrichLibraryWorldIds();
            }
            catch (Exception ex)
            {
                _core.SendToJS("log", new { msg = $"Library scan error: {ex.Message}", color = "err" });
            }
        });
    }

    // Builds the full item list using only in-memory data -- zero file reads.
    // WorldId comes from the player-record store (already in RAM).
    // ExtractWorldIdFromPng is intentionally skipped here to keep this fast;
    // it is still called live by SnapshotPhotoPlayers when a photo is first taken.
    private List<object> BuildLibraryItemsFast()
    {
        var result = new List<object>(_libFileCache.Count);
        foreach (var e in _libFileCache)
        {
            var f      = e.Fi;
            var isImg  = FileWatcherService.ImgExt.Contains(f.Extension);
            var sizeMB = f.Length / 1048576.0;
            var rel    = Path.GetRelativePath(e.Folder, f.FullName).Replace('\\', '/');
            var url    = $"http://localhost:{_core.HttpPort}/media{e.FolderIndex}/{Uri.EscapeDataString(rel).Replace("%2F", "/")}";

            string? worldId = null;
            List<object>? players = null;
            if (isImg)
            {
                var rec = _core.PhotoPlayersStore.GetPhotoRecord(f.Name); // O(1) dict lookup
                if (rec != null)
                {
                    worldId = rec.WorldId;
                    players = rec.Players.Select(p => (object)new
                    {
                        userId = p.UserId, displayName = p.DisplayName,
                        image  = _friends.ResolvePlayerImage(p.UserId, p.Image)
                    }).ToList();
                }
            }

            result.Add(new
            {
                name     = f.Name,
                path     = f.FullName,
                folder   = e.Folder,
                type     = isImg ? "image" : "video",
                size     = sizeMB < 1 ? $"{f.Length / 1024.0:F0} KB" : $"{sizeMB:F1} MB",
                modified = f.LastWriteTime.ToString("o"),
                time     = f.LastWriteTime.ToString("HH:mm"),
                url,
                worldId  = worldId ?? "",
                players  = players ?? new List<object>(),
            });
        }
        return result;
    }

    // Silently reads PNG world-ID metadata in the background after the fast scan.
    // Sends batches of { path -> worldId } to JS so it can patch items and cards.
    // Only processes PNGs that don't already have a worldId from the player store.
    private void EnrichLibraryWorldIds()
    {
        var batch = new Dictionary<string, string>();
        foreach (var e in _libFileCache)
        {
            var f = e.Fi;
            if (!f.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase)) continue;
            // Skip if player store already provided a worldId
            var rec = _core.PhotoPlayersStore.GetPhotoRecord(f.Name);
            if (rec != null && !string.IsNullOrEmpty(rec.WorldId)) continue;

            string? worldId = null;
            try { worldId = WorldTimeTracker.ExtractWorldIdFromPng(f.FullName); } catch { }
            if (string.IsNullOrEmpty(worldId)) continue;

            batch[f.FullName] = worldId;
            if (batch.Count >= 50)
            {
                var toSend = new Dictionary<string, string>(batch);
                _core.SendToJS("libraryWorldIds", toSend);
                batch.Clear();
                Thread.Sleep(20); // yield -- keep enrichment low-priority
            }
        }
        if (batch.Count > 0)
            _core.SendToJS("libraryWorldIds", batch);
    }

    // Keep old paginated builder for loadLibraryPage compatibility
    private List<object> BuildLibraryItems(int offset, int count)
        => BuildLibraryItemsFast().Skip(offset).Take(count).ToList();
}
