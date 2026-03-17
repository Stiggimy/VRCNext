using Photino.NET;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using VRCNext.Services;
using VRCNext.Services.Helpers;

namespace VRCNext;

public partial class AppShell
{
    // Fields

    private PhotinoWindow _window = null!;
    private string _imgCacheDir = "";
    private string _thumbCacheDir = "";
    private int _httpPort;
    private System.Net.HttpListener? _httpListener;
    private System.Threading.Timer? _uptimeTimer2;
    private System.Threading.Timer? _worldStatsTimer;
    private int _worldStatsOffsetMin; // random 0-10 min offset per session
    private bool _minimized;
    private StreamWriter? _activityLogWriter;
    private string _activityLogPath = "";
    private string _activityLogDir  = "";

    private readonly AppSettings _settings;
    private readonly WebhookService _webhook = new();
    private readonly FileWatcherService _fileWatcher = new();
    private readonly VRChatApiService _vrcApi = new();
    private readonly VRChatLogWatcher _logWatcher = new();
    // Domain controllers
    private CoreLibrary _core = null!;
    private AuthController _authCtrl = null!;
    private FriendsController _friends = null!;
    private InstanceController _instance = null!;
    private NotificationsController _notifications = null!;
    private GroupsController _groups = null!;
    private PhotosController _photos = null!;
    private TimelineController _timelineCtrl = null!;
    private VROverlayController _vroCtrl = null!;
    private SpaceFlightController _sfCtrl = null!;
    private DiscordController _discordCtrl = null!;
    private ChatboxController _chatboxCtrl = null!;
    private VoiceFightController _vfCtrl = null!;
    private RelayController _relayCtrl = null!;
    private WindowController _windowCtrl = null!;
    private ImageCacheService? _imgCache;
    private readonly CacheHandler _cache = new();
    private static readonly System.Text.RegularExpressions.Regex _vrcImgUrlRegex = new(
        @"""(https://(?:api\.vrchat\.cloud|assets\.vrchat\.com|files\.vrchat\.cloud)[^""]+)""",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    private readonly UserTimeTracker _timeTracker;
    private readonly WorldTimeTracker _worldTimeTracker;
    private readonly PhotoPlayersStore _photoPlayersStore;
    private readonly TimelineService _timeline;
    private readonly UpdateService _updateService = new();
    private readonly MemoryTrimService _memTrim = new();

    // VR overlay world name+thumb cache (worldId → name, localUrl)
    private readonly Dictionary<string, (string name, string thumb)> _vrWorldCache = new();

    // Helpers

    // Photino compatibility shim: SendWebMessage is thread-safe, so Invoke is a direct call
    private static void Invoke(Action action) => action();
    private static T Invoke<T>(Func<T> func) => func();


    // Helper: always returns ISO 8601 date string from a JToken
    private static string IsoDate(JToken? t)
    {
        if (t == null) return "";
        if (t.Type == JTokenType.Date)
            return t.Value<DateTime>().ToUniversalTime().ToString("o");
        return t.ToString();
    }

    // Constructor

    public AppShell(string[] args)
    {
        _settings = AppSettings.Load();
        MigrationHelper.MigrateFavorites(_settings); // silently moves Favorites → favorited_images.json
        if (_settings.MemoryTrimEnabled) _memTrim.SetEnabled(true);
        _timeTracker = UserTimeTracker.Load();
        _worldTimeTracker = WorldTimeTracker.Load();
        _photoPlayersStore = PhotoPlayersStore.Load();
        _timeline = TimelineService.Load();
        _minimized = args.Contains("--minimized");
        LoadDeletedAvatarsCache();

        // Create shared service container and domain controllers
        _core = new CoreLibrary(
            _vrcApi, _logWatcher, _timeline, _settings, _cache,
            _timeTracker, _worldTimeTracker, _photoPlayersStore,
            _webhook, _fileWatcher, _memTrim, _updateService,
            (type, payload) => SendToJS(type, payload));
        _core.IsVrcRunning = RelayController.IsVrcRunning;
        _core.IsSteamVrRunning = RelayController.IsSteamVrRunning;
        _core.DispatchMessage = rawMsg => OnWebMessage(rawMsg);
        _core.AvtrdbSubmit = id => QueueAvtrdbSubmit(id);
        _core.LoadPage = path => _window.Load(path);
        _friends = new FriendsController(_core);
        _instance = new InstanceController(_core, _friends);
        _notifications = new NotificationsController(_core, _friends, _instance);
        _groups = new GroupsController(_core);
        _photos = new PhotosController(_core, _friends, _instance);
        _timelineCtrl = new TimelineController(_core, _friends, _instance, _photos);
        _vroCtrl = new VROverlayController(_core, _friends);
        _sfCtrl = new SpaceFlightController(_core, _vroCtrl);
        _discordCtrl = new DiscordController(_core, _instance, _vroCtrl);
        _chatboxCtrl = new ChatboxController(_core, _vroCtrl);
        _vfCtrl = new VoiceFightController(_core, _vroCtrl);
        _relayCtrl = new RelayController(_core, _friends, _instance, _notifications, _vroCtrl);
        _windowCtrl = new WindowController(_core);
        _authCtrl = new AuthController(_core, _friends, _instance, _photos, _relayCtrl, _groups, _discordCtrl);
        _relayCtrl.OnOwnUserUpdated = user => _authCtrl.SendVrcUserData(user);
        _core.PushDiscordPresence = () => _discordCtrl.PushPresence();
        _vroCtrl.OnToolToggle = ToggleToolFromOverlay;
        _vroCtrl.GetToolStates = () => (
            _discordCtrl.IsConnected,
            _vfCtrl.IsRunning,
            _relayCtrl.IsVcRunning,
            _sfCtrl.IsConnected,
            _relayCtrl.IsRunning,
            _chatboxCtrl.IsEnabled);
        _fileWatcher.NewFile += _photos.OnNewFile;
    }

    // Run

    public void Run()
    {
        StartHttpListener();
        _core.HttpPort = _httpPort;

        _imgCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VRCNext", "ImageCache");
        Directory.CreateDirectory(_imgCacheDir);
        _thumbCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VRCNext", "ThumbCache");
        Directory.CreateDirectory(_thumbCacheDir);
        _activityLogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VRCNext", "Logs");
        Directory.CreateDirectory(_activityLogDir);
        var logFileName = $"vrcn-log-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt";
        _activityLogPath = Path.Combine(_activityLogDir, logFileName);
        try { _activityLogWriter = new StreamWriter(_activityLogPath, append: false, System.Text.Encoding.UTF8) { AutoFlush = true }; } catch { }

        _imgCache = new ImageCacheService(_imgCacheDir, _vrcApi.GetHttpClient())
        {
            Enabled    = _settings.ImgCacheEnabled,
            LimitBytes = (long)_settings.ImgCacheLimitGb * 1024 * 1024 * 1024,
            Port       = _httpPort,
        };
        _core.ImgCache = _imgCache;
        _core.GetVirtualMediaUrl = _photos.GetVirtualMediaUrl;

        var wwwroot   = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
        var startPage = _settings.SetupComplete
            ? Path.Combine(wwwroot, "index.html")
            : Path.Combine(wwwroot, "setup", "setup.html");
        if (!File.Exists(startPage)) startPage = Path.Combine(wwwroot, "index.html");

        int uptimeTick = 0;
        _uptimeTimer2 = new System.Threading.Timer(_ =>
        {
            if (_vfCtrl.IsRunning)
                SendToJS("vfMeter", new { level = _vfCtrl.MeterLevel });
            if (Interlocked.Increment(ref uptimeTick) % 10 == 0 && _relayCtrl.IsRunning)
                SendToJS("uptimeTick", (DateTime.Now - _relayCtrl.RelayStart).ToString(@"hh\:mm\:ss"));
        }, null, 100, 100);

        // Hourly world stats collection for World Insights
        // Fires at each UTC hour + random 0-10 min offset (to spread API load across users)
        _worldStatsOffsetMin = new Random().Next(0, 11);
        _worldStatsTimer = new System.Threading.Timer(async _ =>
        {
            await CollectWorldStatsAsync();
            ScheduleNextWorldStats();
        }, null, Timeout.Infinite, Timeout.Infinite);
        ScheduleNextWorldStats();

        // Chromeless on Windows requires explicit location (Center() sets a flag, not coordinates)
        var (startX, startY) = WindowController.GetCenteredLocation(1100, 700);

#if !WINDOWS
        // Auto-install missing GStreamer plugins required by WebKit2GTK (blank window without them)
        EnsureLinuxGstreamer();

        // WebKit2GTK on systems without proper GPU (VMs, Hyper-V, missing Vulkan):
        // Force Mesa software rendering so EGL/OpenGL never fails in the WebKit child process.
        // These must be set before PhotinoWindow so the child process inherits them.
        void SetIfUnset(string key, string val)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                Environment.SetEnvironmentVariable(key, val);
        }
        SetIfUnset("WEBKIT_DISABLE_DMABUF_RENDERER",    "1");
        SetIfUnset("WEBKIT_DISABLE_COMPOSITING_MODE",   "1");
        SetIfUnset("LIBGL_ALWAYS_SOFTWARE",             "1");
        SetIfUnset("GALLIUM_DRIVER",                    "llvmpipe");
#endif

        var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "logo.png");
        var windowBuilder = new PhotinoWindow()
            .SetTitle("VRCNext")
            .SetUseOsDefaultSize(false)
            .SetSize(1100, 700)
            .SetMinSize(900, 540)
            .SetChromeless(OperatingSystem.IsWindows())
            .SetResizable(true)
            .SetUseOsDefaultLocation(false)
            .SetLeft(startX)
            .SetTop(startY)
            .RegisterWebMessageReceivedHandler((_, message) => { _ = OnWebMessage(message); });
        if (File.Exists(iconPath)) windowBuilder.SetIconFile(iconPath);
        _window = windowBuilder.Load(startPage);
        _core.Window = _window;

        if (_minimized) _window.SetMinimized(true);
        _window.WaitForClose();
        OnClose();
    }

#if !WINDOWS
    private static void EnsureLinuxGstreamer()
    {
        // Check if autoaudiosink is available — its absence crashes WebKitWebProcess (blank window)
        try
        {
            var check = Process.Start(new ProcessStartInfo("gst-inspect-1.0")
            {
                Arguments = "autoaudiosink",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            });
            check?.WaitForExit(3000);
            if (check?.ExitCode == 0) return; // already installed
        }
        catch { return; } // gst-inspect-1.0 not on PATH — nothing we can do

        // Determine install command for the running distro
        string pkgs = "";
        if (File.Exists("/usr/bin/pacman") || File.Exists("/bin/pacman"))
            pkgs = "gst-plugins-base gst-plugins-good gst-libav";
        else if (File.Exists("/usr/bin/apt-get"))
            pkgs = "gstreamer1.0-plugins-base gstreamer1.0-plugins-good gstreamer1.0-libav";
        else if (File.Exists("/usr/bin/dnf"))
            pkgs = "gstreamer1-plugins-base gstreamer1-plugins-good";
        if (string.IsNullOrEmpty(pkgs)) return;

        string pm  = File.Exists("/usr/bin/pacman") || File.Exists("/bin/pacman") ? "pacman -S --noconfirm"
                   : File.Exists("/usr/bin/apt-get")                               ? "apt-get install -y"
                                                                                   : "dnf install -y";
        string cmd = $"{pm} {pkgs}";

        // Use pkexec — opens a GUI polkit dialog (no terminal needed)
        try
        {
            var proc = Process.Start(new ProcessStartInfo("pkexec")
            {
                Arguments      = $"/bin/bash -c \"{cmd}\"",
                UseShellExecute = false,
            });
            proc?.WaitForExit();
        }
        catch { }
    }
#endif

    // ── World Stats collection (aligned to UTC hour + random offset) ────────

    private void ScheduleNextWorldStats()
    {
        var now = DateTime.UtcNow;
        // Target: next UTC hour + offset
        var nextHour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);
        var target = nextHour.AddMinutes(_worldStatsOffsetMin);
        // If target is in the past (e.g. offset=5 and it's :03), fire for *this* hour
        if (target <= now) target = target.AddHours(1);
        var delay = target - now;
        _worldStatsTimer?.Change(delay, Timeout.InfiniteTimeSpan);
    }

    internal async Task CollectWorldStatsAsync()
    {
        try
        {
            if (!_vrcApi.IsLoggedIn) return;
            var worlds = await _vrcApi.GetMyWorldsAsync();
            foreach (var w in worlds)
            {
                var id = w["id"]?.ToString();
                if (string.IsNullOrEmpty(id)) continue;
                var full = await _vrcApi.GetWorldFreshAsync(id);
                var active    = full?["occupants"]?.Value<int>() ?? w["occupants"]?.Value<int>() ?? 0;
                var favorites = full?["favorites"]?.Value<int>() ?? w["favorites"]?.Value<int>() ?? 0;
                var visits    = full?["visits"]?.Value<int>() ?? 0;
                _timeline.InsertWorldStats(id, active, favorites, visits);
            }
        }
        catch { }
    }

    // OnClose

    private void OnClose()
    {
        _relayCtrl?.Dispose();
        _fileWatcher.Dispose();
        _uptimeTimer2?.Dispose();
        _worldStatsTimer?.Dispose();
        _sfCtrl?.Dispose();
        _vfCtrl?.Dispose();
        _discordCtrl?.Dispose();
        _chatboxCtrl?.Dispose();
        _vroCtrl?.Dispose();
        _timeline.Dispose();
        _photoPlayersStore.Dispose();
        _timeTracker.Dispose();
        _worldTimeTracker.Dispose();
        _photos.VrcPhotoWatcher?.Dispose();
        _webhook.Dispose();
        _logWatcher.Dispose();
        _memTrim.Dispose();
        _httpListener?.Stop();
        _activityLogWriter?.Dispose();
    }

    // SendToJS

    private void SendToJS(string type, object? payload = null)
    {
        if (type == "log" && payload != null && _activityLogWriter != null)
        {
            try
            {
                var p = Newtonsoft.Json.Linq.JObject.FromObject(payload);
                var logMsg = p["msg"]?.ToString() ?? "";
                _activityLogWriter.WriteLine($"{DateTime.Now:HH:mm:ss}  {logMsg}");
            }
            catch { }
        }
        var msg = JsonConvert.SerializeObject(new { type, payload });
        if (_imgCache != null)
            msg = _vrcImgUrlRegex.Replace(msg, m => $"\"{_imgCache.Get(m.Groups[1].Value)}\"");
        try { _window.SendWebMessage(msg); } catch { }

#if WINDOWS
        // Forward friend timeline events to the VR wrist overlay
        if (type == "friendTimelineEvent" && payload != null && _core.VrOverlay != null)
        {
            try
            {
                var p           = Newtonsoft.Json.Linq.JObject.FromObject(payload);
                var evType      = p["type"]?.ToString() ?? "";
                var name        = p["friendName"]?.ToString() ?? "Friend";
                var worldName   = p["worldName"]?.ToString() ?? "";
                var oldValue    = p["oldValue"]?.ToString() ?? "";
                var newValue    = p["newValue"]?.ToString() ?? "";
                var friendImage = p["friendImage"]?.ToString() ?? "";
                var friendId    = p["friendId"]?.ToString() ?? "";
                var location    = p["location"]?.ToString() ?? "";

                // Build event text — null means "skip this event"
                string? evText = evType switch
                {
                    "friend_online"     => "Online (Game)",
                    "friend_offline"    => "Offline (Game)",
                    "friend_gps"        => string.IsNullOrWhiteSpace(worldName) ? null : $"→ {worldName}",
                    "friend_status"     => !string.IsNullOrEmpty(oldValue) && !string.IsNullOrEmpty(newValue)
                                           ? $"{oldValue} → {newValue}"
                                           : "Changed status",
                    "friend_statusdesc" => !string.IsNullOrEmpty(newValue) ? newValue : "Changed status text",
                    "friend_bio"        => !string.IsNullOrEmpty(newValue) ? newValue : "Updated bio",
                    "friend_added"      => "Friend added",
                    "friend_removed"    => "Removed you",
                    _                   => null   // ignore unknown events (e.g. "friend_updated")
                };
                if (evText == null) return;

                var time = DateTime.Now.ToString("HH:mm");

                // Main overlay (wrist alerts tab) — every event, no filtering
                _core.VrOverlay.AddNotification(evType, name, evText, time, friendImage, friendId, location);

                // HMD toast — every event, cooldown inside EnqueueToast handles rapid-fire dedup
                try
                {
                    bool isFav = !string.IsNullOrEmpty(friendId) && _friends.IsFavorited(friendId);
                    _core.VrOverlay.EnqueueToast(evType, name, evText, time, friendImage, isFav);
                }
                catch { }
            }
            catch { }
        }
#endif
    }

#if WINDOWS
    // VR Overlay tool toggle (fired by overlay card click)

    private void ToggleToolFromOverlay(int idx)
    {
        switch (idx)
        {
            case 0: // Discord Presence
                _discordCtrl.Toggle();
                break;

            case 1: // Voice Fight
                _vfCtrl.Toggle();
                break;

            case 2: // YouTube Fix
                _relayCtrl.ToggleVc();
                break;

            case 3: // Space Flight
                _sfCtrl.Toggle();
                break;

            case 4: // Media Relay
                _relayCtrl.ToggleRelay();
                break;

            case 5: // Custom Chatbox
                _chatboxCtrl.Toggle();
                break;
        }
        _vroCtrl.UpdateToolStates();
    }
#endif

    // HttpListener (replaces WebView2 virtual hosts)

    private readonly List<string> _mappedHosts = new();

    private void StartHttpListener()
    {
        // Try the saved port first, then scan for a free one
        var candidates = new List<int>();
        if (_settings.LocalHttpPort > 0) candidates.Add(_settings.LocalHttpPort);
        var rng = new Random();
        for (int i = 0; i < 200; i++) candidates.Add(rng.Next(49152, 65534));

        foreach (var port in candidates)
        {
            _httpListener = new System.Net.HttpListener();
            _httpListener.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                _httpListener.Start();
                _httpPort = port;
                if (_settings.LocalHttpPort != port)
                {
                    _settings.LocalHttpPort = port;
                    _settings.Save();
                }
                break;
            }
            catch (System.Net.HttpListenerException) { _httpListener.Close(); }
        }
        _ = Task.Run(ServeHttpAsync);
    }

    private void UpdateVirtualHostMappings()
    {
        // No-op with Photino — watch folders served via /media{i}/ routes in HttpListener
    }

    private async Task ServeHttpAsync()
    {
        while (_httpListener?.IsListening == true)
        {
            try
            {
                var ctx = await _httpListener.GetContextAsync();
                _ = HandleHttpAsync(ctx);
            }
            catch (ObjectDisposedException) { break; }   // intentional shutdown
            catch { /* transient error (connection reset, OS glitch) — keep accepting */ }
        }
    }

    private async Task HandleHttpAsync(System.Net.HttpListenerContext ctx)
    {
        var path    = ctx.Request.Url?.AbsolutePath ?? "/";
        var isThumb = ctx.Request.Url?.Query?.Contains("thumb=1") == true;
        try
        {
            if (path.StartsWith("/imgcache/"))
                await ServeFileAsync(ctx, Path.Combine(_imgCacheDir, Uri.UnescapeDataString(path["/imgcache/".Length..])));
            else if (path.StartsWith("/vrcphotos/"))
            {
                if (!string.IsNullOrEmpty(_photos.VrcPhotoDir))
                {
                    var file = Path.Combine(_photos.VrcPhotoDir, Uri.UnescapeDataString(path["/vrcphotos/".Length..]));
                    if (isThumb) await ServeThumbAsync(ctx, file); else await ServeFileAsync(ctx, file);
                }
                else ctx.Response.StatusCode = 404;
            }
            else if (path.StartsWith("/media"))
            {
                var rest  = path["/media".Length..];
                var slash = rest.IndexOf('/');
                if (slash > 0 && int.TryParse(rest[..slash], out var idx)
                    && idx < _settings.WatchFolders.Count)
                {
                    var file = Path.Combine(_settings.WatchFolders[idx], Uri.UnescapeDataString(rest[(slash + 1)..]));
                    if (isThumb) await ServeThumbAsync(ctx, file); else await ServeFileAsync(ctx, file);
                }
                else ctx.Response.StatusCode = 404;
            }
            else if (path.StartsWith("/cursor/"))
            {
                var cursorDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "cursor");
                var file = Path.Combine(cursorDir, Uri.UnescapeDataString(path["/cursor/".Length..]));
                await ServeFileAsync(ctx, file);
            }
            else ctx.Response.StatusCode = 404;
        }
        catch { ctx.Response.StatusCode = 500; }
        finally { try { ctx.Response.Close(); } catch { } }
    }

    private static async Task ServeFileAsync(System.Net.HttpListenerContext ctx, string file)
    {
        if (!File.Exists(file)) { ctx.Response.StatusCode = 404; return; }
        ctx.Response.ContentType = Path.GetExtension(file).ToLower() switch {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png"  => "image/png",
            ".gif"  => "image/gif",
            ".webp" => "image/webp",
            ".mp4"  => "video/mp4",
            ".webm" => "video/webm",
            _       => "application/octet-stream"
        };
        ctx.Response.StatusCode = 200;
        // Stream directly — NEVER load full file into RAM (videos can be multiple gigabytes)
        ctx.Response.ContentLength64 = new FileInfo(file).Length;
        using var fs = File.OpenRead(file);
        await fs.CopyToAsync(ctx.Response.OutputStream);
    }

    private static readonly SemaphoreSlim _thumbSem = new(2, 2);
    private static int _thumbGenCount = 0;

    private async Task ServeThumbAsync(System.Net.HttpListenerContext ctx, string file)
    {
        if (!File.Exists(file)) { ctx.Response.StatusCode = 404; return; }
        var ext = Path.GetExtension(file).ToLower();
        // Only thumbnail images; serve other types (video etc.) as-is
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp"))
        {
            await ServeFileAsync(ctx, file);
            return;
        }

        // Cache key: MD5 of path, invalidate if source is newer
        var keyBytes  = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(file));
        var thumbPath = Path.Combine(_thumbCacheDir, Convert.ToHexString(keyBytes) + ".jpg");

        if (!File.Exists(thumbPath) || File.GetLastWriteTimeUtc(thumbPath) < File.GetLastWriteTimeUtc(file))
        {
            await _thumbSem.WaitAsync();
            try
            {
                // Double-check after acquiring semaphore (another thread may have generated it)
                if (!File.Exists(thumbPath) || File.GetLastWriteTimeUtc(thumbPath) < File.GetLastWriteTimeUtc(file))
                {
                    // Image resize is CPU-bound — offload to thread pool
                    await Task.Run(() =>
                    {
                        var tmpPath = thumbPath + ".tmp";
                        // FromStream instead of FromFile — releases file handle immediately after read
                        var rawBytes = File.ReadAllBytes(file);
                        using var ms  = new MemoryStream(rawBytes);
                        using var src = System.Drawing.Image.FromStream(ms, false, false);
                        const int maxSize = 400;
                        var scale = Math.Min(1.0, Math.Min(maxSize / (double)src.Width, maxSize / (double)src.Height));
                        var w = Math.Max(1, (int)(src.Width  * scale));
                        var h = Math.Max(1, (int)(src.Height * scale));
                        using var bmp = new System.Drawing.Bitmap(w, h);
                        using (var g = System.Drawing.Graphics.FromImage(bmp))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
                            g.DrawImage(src, 0, 0, w, h);
                        }
                        var jpegCodec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
                            .First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
                        var encParams = new System.Drawing.Imaging.EncoderParameters(1);
                        encParams.Param[0] = new System.Drawing.Imaging.EncoderParameter(
                            System.Drawing.Imaging.Encoder.Quality, 72L);
                        // Atomic write: temp file then rename to prevent serving half-written files
                        bmp.Save(tmpPath, jpegCodec, encParams);
                        File.Move(tmpPath, thumbPath, overwrite: true);

                        // Periodic GC to prompt libgdiplus to release native memory
                        if (Interlocked.Increment(ref _thumbGenCount) % 10 == 0)
                            GC.Collect(1, GCCollectionMode.Optimized, false);
                    });
                }
            }
            finally { _thumbSem.Release(); }
        }

        ctx.Response.ContentType = "image/jpeg";
        ctx.Response.StatusCode  = 200;
        var thumbBytes = await File.ReadAllBytesAsync(thumbPath);
        ctx.Response.ContentLength64 = thumbBytes.Length;
        await ctx.Response.OutputStream.WriteAsync(thumbBytes);
    }
}
