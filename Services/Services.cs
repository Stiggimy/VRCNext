using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Security.Cryptography;

namespace VRCNext.Services;

// Webhook Service - posts files to Discord, deletes messages
public class WebhookService : IDisposable
{
    private readonly HttpClient _http = new();

    public class PostResult
    {
        public bool Success { get; set; }
        public string? MessageId { get; set; }
        public string? Error { get; set; }
    }

    public class PostRecord
    {
        public string MessageId { get; set; } = "";
        public string WebhookUrl { get; set; } = "";
        public string WebhookName { get; set; } = "";
        public string FileName { get; set; } = "";
        public double SizeMB { get; set; }
        public DateTime PostedAt { get; set; } = DateTime.Now;
    }

    public async Task<PostResult> PostFileAsync(string url, string path, string? name = null, string? avatar = null)
    {
        try
        {
            if (!File.Exists(path)) return new() { Error = "File not found" };
            using var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(await File.ReadAllBytesAsync(path)), "file", Path.GetFileName(path));
            if (!string.IsNullOrEmpty(name)) content.Add(new StringContent(name), "username");
            if (!string.IsNullOrEmpty(avatar)) content.Add(new StringContent(avatar), "avatar_url");
            var resp = await _http.PostAsync(url.TrimEnd('/') + "?wait=true", content);
            if (resp.IsSuccessStatusCode)
            {
                var data = JObject.Parse(await resp.Content.ReadAsStringAsync());
                return new() { Success = true, MessageId = data["id"]?.ToString() };
            }
            return new() { Error = $"HTTP {(int)resp.StatusCode}" };
        }
        catch (Exception ex) { return new() { Error = ex.Message }; }
    }

    public async Task<bool> DeleteAsync(string url, string msgId)
    {
        try
        {
            var resp = await _http.DeleteAsync($"{url.TrimEnd('/')}/messages/{msgId}");
            return resp.StatusCode == System.Net.HttpStatusCode.NoContent;
        }
        catch { return false; }
    }

    public void Dispose() => _http.Dispose();
}

// File Watcher - monitors folders for new media files
public class FileWatcherService : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly HashSet<string> _recent = new();
    private readonly object _lock = new();

    public static readonly HashSet<string> ImgExt = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };
    public static readonly HashSet<string> VidExt = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".avi", ".mkv", ".webm", ".flv", ".wmv" };

    public event EventHandler<FileArg>? NewFile;

    public class FileArg : EventArgs
    {
        public string FilePath { get; set; } = "";
        public string FileName { get; set; } = "";
        public string FileType { get; set; } = "";
        public double SizeMB { get; set; }
    }

    public void Start(IEnumerable<string> folders)
    {
        Stop();
        foreach (var folder in folders.Where(Directory.Exists))
        {
            var w = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime
            };
            w.Created += (s, e) => Handle(e.FullPath);
            w.Renamed += (s, e) => Handle(e.FullPath);
            _watchers.Add(w);
        }
    }

    public void Stop()
    {
        foreach (var w in _watchers) { w.EnableRaisingEvents = false; w.Dispose(); }
        _watchers.Clear();
        lock (_lock) _recent.Clear();
    }

    private void Handle(string p)
    {
        var ext = Path.GetExtension(p);
        bool img = ImgExt.Contains(ext), vid = VidExt.Contains(ext);
        if (!img && !vid) return;

        lock (_lock)
        {
            if (_recent.Contains(p)) return;
            _recent.Add(p);
            if (_recent.Count > 200) { _recent.Clear(); _recent.Add(p); }
        }

        Task.Run(async () =>
        {
            await Task.Delay(1500);
            if (!await WaitReady(p, vid ? 120 : 10)) return;
            try
            {
                var info = new FileInfo(p);
                var mb = info.Length / 1048576.0;
                if (mb > 25) return;
                NewFile?.Invoke(this, new()
                {
                    FilePath = p,
                    FileName = info.Name,
                    FileType = img ? "image" : "video",
                    SizeMB = mb
                });
            }
            catch { }
        });
    }

    private static async Task<bool> WaitReady(string path, int seconds)
    {
        long lastSize = -1;
        int stable = 0;
        for (int i = 0; i < seconds; i++)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return false;
                if (fi.Length == lastSize && fi.Length > 0)
                {
                    stable++;
                    if (stable >= 3)
                    {
                        try { using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read); return true; }
                        catch { stable = 0; }
                    }
                }
                else stable = 0;
                lastSize = fi.Length;
            }
            catch { return false; }
            await Task.Delay(1000);
        }
        return false;
    }

    public void Dispose() => Stop();
}

// App Settings - persisted to JSON in %AppData%
public class AppSettings
{
    public string BotName { get; set; } = "VRCNext";
    public string BotAvatarUrl { get; set; } = "";
    public List<WebhookSlot> Webhooks { get; set; } = new()
    {
        new() { Name = "Channel 1" },
        new() { Name = "Channel 2" },
        new() { Name = "Channel 3" },
        new() { Name = "Channel 4" },
    };
    public int LocalHttpPort { get; set; } = 0;
    public List<string> WatchFolders { get; set; } = new();
    public List<string> MyInstances { get; set; } = new();
    public List<string> Favorites { get; set; } = new();
    public string VrcPath { get; set; } = "";
    public List<string> ExtraExe { get; set; } = new(); // legacy — kept for JSON compat / migration
    public List<string> ExtraExeDesktop { get; set; } = new();
    public List<string> ExtraExeVR { get; set; } = new();
    public bool AutoStart { get; set; }
    public bool StartWithWindows { get; set; }
    public bool PostAll { get; set; }
    public int SelectedChannel { get; set; }
    public bool Notifications { get; set; } = true;
    public bool NotifySound { get; set; } // legacy — kept for JSON compat
    public bool NotifySoundEnabled { get; set; }
    public bool MessageSoundEnabled { get; set; }
    public bool MediaRelaySoundEnabled { get; set; }
    public bool SteamOverlaySoundEnabled { get; set; } = true;
    public bool MinimizeToTray { get; set; }
    public string Language { get; set; } = "en";
    public string Theme { get; set; } = "midnight";
    public string SpecialTheme { get; set; } = "";
    public int AutoColorAccuracy { get; set; } = 50;
    public string PlayBtnTheme { get; set; } = "";
    public string CursorTheme { get; set; } = "";
    public int GuiZoom { get; set; } = 100;
    public string DashBgPath { get; set; } = "";
    public int DashOpacity { get; set; } = 40;
    public bool RandomDashBg { get; set; } = false;
    public bool ClockEnabled { get; set; } = true;
    public bool ClockAmPm { get; set; } = false;
    public string VrcUsername { get; set; } = "";

    // Encrypted on disk via DPAPI — use VrcPassword/VrcAuthCookie/VrcTwoFactorCookie at runtime
    public string VrcPasswordEnc { get; set; } = "";
    public string VrcAuthCookieEnc { get; set; } = "";
    public string VrcTwoFactorCookieEnc { get; set; } = "";

    [JsonIgnore] public string VrcPassword { get; set; } = "";
    [JsonIgnore] public string VrcAuthCookie { get; set; } = "";
    [JsonIgnore] public string VrcTwoFactorCookie { get; set; } = "";

    private static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        try
        {
#if WINDOWS
            var enc = ProtectedData.Protect(
                System.Text.Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(enc);
#else
            // Linux: no DPAPI — store as Base64 (file is protected by OS file permissions)
            return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plain));
#endif
        }
        catch { return ""; }
    }

    private static string Unprotect(string cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        try
        {
#if WINDOWS
            var dec = ProtectedData.Unprotect(
                Convert.FromBase64String(cipher), null, DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(dec);
#else
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cipher));
#endif
        }
        catch { return ""; }
    }

    // Custom Chatbox settings
    public bool CbShowTime { get; set; } = true;
    public bool CbShowMedia { get; set; } = true;
    public bool CbShowPlaytime { get; set; } = true;
    public bool CbShowCustomText { get; set; } = true;
    public bool CbShowSystemStats { get; set; }
    public bool CbShowAfk { get; set; }
    public string CbAfkMessage { get; set; } = "Currently AFK";
    public bool CbSuppressSound { get; set; } = true;
    public string CbTimeFormat { get; set; } = "hh:mm tt";
    public string CbSeparator { get; set; } = " | ";
    public int CbIntervalMs { get; set; } = 5000;
    public List<string> CbCustomLines { get; set; } = new();

    // Space Flight settings
    public float SfMultiplier { get; set; } = 1f;
    public bool SfLockX { get; set; }
    public bool SfLockY { get; set; }
    public bool SfLockZ { get; set; }
    public bool SfLeftHand { get; set; }
    public bool SfRightHand { get; set; } = true;
    public bool SfUseGrip { get; set; } = true;

    // Auto-start flags (legacy — kept for JSON compat, no longer acted on)
    public bool ChatboxAutoStart { get; set; }
    public bool SfAutoStart { get; set; }
    public bool DiscordPresenceAutoStart { get; set; }
    public bool VroAutoStart { get; set; }

    // Auto-start split: VR vs Desktop (triggered when VRChat is launched from VRCNext)
    public bool ChatboxAutoStartVR       { get; set; }
    public bool ChatboxAutoStartDesktop  { get; set; }
    public bool SfAutoStartVR            { get; set; }
    public bool RelayAutoStartVR         { get; set; }
    public bool RelayAutoStartDesktop    { get; set; }
    public bool YtAutoStartVR            { get; set; }
    public bool YtAutoStartDesktop       { get; set; }
    public bool VfAutoStartVR            { get; set; }
    public bool VfAutoStartDesktop       { get; set; }
    public bool DpAutoStartVR            { get; set; }
    public bool DpAutoStartDesktop       { get; set; }
    public bool VroAutoStartVR           { get; set; }

    // VR Wrist Overlay settings
    public bool    VroAttachLeft  { get; set; } = true;
    public bool    VroAttachHand  { get; set; } = true;
    public float   VroPosX        { get; set; } = -0.10f;
    public float   VroPosY        { get; set; } = -0.03f;
    public float   VroPosZ        { get; set; } = 0.11f;
    public float   VroRotX        { get; set; } = -180f;
    public float   VroRotY        { get; set; } = 46f;
    public float   VroRotZ        { get; set; } = 85f;
    public float   VroWidth       { get; set; } = 0.16f;
    public List<uint> VroKeybind       { get; set; } = new();
    public int        VroKeybindHand   { get; set; } = 0; // 0=any, 1=left, 2=right
    public int        VroKeybindMode   { get; set; } = 0; // 0=combo(hold), 1=doubletap
    public List<uint> VroKeybindDt     { get; set; } = new();
    public int        VroKeybindDtHand { get; set; } = 0; // 0=any, 1=left, 2=right for doubletap slot
    public int        VroControlRadius { get; set; } = 16; // cm, 3–28; 16 = default

    // VR Toast Notifications (HMD-attached)
    public bool       VroToastEnabled      { get; set; } = true;
    public bool       VroToastFavOnly      { get; set; }
    public int        VroToastSize         { get; set; } = 50; // 0–100, default 50%
    public float      VroToastOffsetX      { get; set; } = 0f;
    public float      VroToastOffsetY      { get; set; } = -0.12f;
    public bool       VroToastOnline       { get; set; } = true;
    public bool       VroToastOffline      { get; set; } = true;
    public bool       VroToastWebOnline    { get; set; } = true;
    public bool       VroToastWebOffline   { get; set; } = true;
    public bool       VroToastGps          { get; set; } = true;
    public bool       VroToastStatus       { get; set; } = true;
    public bool       VroToastStatusDesc   { get; set; } = true;
    public bool       VroToastBio          { get; set; } = true;
    public int        VroToastDuration     { get; set; } = 8;   // seconds, 2–10
    public int        VroToastStack        { get; set; } = 2;   // 1–4, max simultaneous toasts
    public bool       VroToastFriendReq    { get; set; } = true;
    public bool       VroToastInvite       { get; set; } = true;
    public bool       VroToastGroupInv     { get; set; } = true;

    // Avtrdb Support — report deleted avatars to help clean the database
    public bool AvtrdbReportDeleted { get; set; } = true;
    public bool AvtrdbSubmitAvatars { get; set; }

    // Discord Rich Presence — privacy per status
    public bool DpHideInstIdJoinMe  { get; set; }
    public bool DpHideInstIdOnline  { get; set; }
    public bool DpHideInstIdAskMe   { get; set; } = true;
    public bool DpHideInstIdBusy    { get; set; } = true;
    public bool DpHideLocJoinMe     { get; set; }
    public bool DpHideLocOnline     { get; set; }
    public bool DpHideLocAskMe      { get; set; } = true;
    public bool DpHideLocBusy       { get; set; } = true;
    public bool DpHidePlayersJoinMe { get; set; }
    public bool DpHidePlayersOnline { get; set; }
    public bool DpHidePlayersAskMe  { get; set; } = true;
    public bool DpHidePlayersBusy   { get; set; } = true;
    public bool DpHideJoinBtnJoinMe { get; set; }
    public bool DpHideJoinBtnOnline { get; set; }
    public bool DpHideJoinBtnAskMe  { get; set; } = true;
    public bool DpHideJoinBtnBusy   { get; set; } = true;

    // Image cache settings
    public bool ImgCacheEnabled         { get; set; } = true;
    public int  ImgCacheLimitGb         { get; set; } = 5;
    public bool ImgCacheOptimizeEnabled { get; set; } = true;

    // Fast Fetch Cache
    public bool FfcEnabled { get; set; } = true;

    // Memory Trim
    public bool MemoryTrimEnabled { get; set; } = false;

    // Crash Reporting, send anonymous stack traces to the developer via Discord webhook
    public bool SendCrashData { get; set; } = true;
    // Restart after crash. We do ignore task manager kills here!
    public bool RestartAfterCrash { get; set; } = true;

    // Legacy Window Manager (requires restart, disables chromeless + custom chrome)
    public bool LegacyWindow { get; set; } = false;

    // Dashboard layout customization
    public List<string> DashSectionOrder  { get; set; } = new();
    public List<string> DashSectionHidden { get; set; } = new();

    public bool SetupComplete { get; set; }

    public List<string> InviteMessages { get; set; } = new()
    {
        "Come join us!",
        "We're here, join!",
        "You should check this out!",
        "Join me?"
    };

    public class WebhookSlot
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public bool Enabled { get; set; }
    }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCNext", "settings.json");

    [JsonIgnore] public static string? LastLoadError { get; private set; }
    [JsonIgnore] public static string? LoadDebugInfo { get; private set; }
    [JsonIgnore] public string? LastSaveError { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonConvert.DeserializeObject<AppSettings>(json,
                    new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace }) ?? new();
                // Ensure exactly 4 webhook slots
                if (s.Webhooks == null) s.Webhooks = new();
                if (s.Webhooks.Count > 4) s.Webhooks = s.Webhooks.Take(4).ToList();
                while (s.Webhooks.Count < 4) s.Webhooks.Add(new() { Name = $"Channel {s.Webhooks.Count + 1}" });
                // Decrypt credentials
                s.VrcPassword        = Unprotect(s.VrcPasswordEnc);
                s.VrcAuthCookie      = Unprotect(s.VrcAuthCookieEnc);
                s.VrcTwoFactorCookie = Unprotect(s.VrcTwoFactorCookieEnc);
                return s;
            }
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            // Encrypt credentials before writing to disk
            VrcPasswordEnc        = Protect(VrcPassword);
            VrcAuthCookieEnc      = Protect(VrcAuthCookie);
            VrcTwoFactorCookieEnc = Protect(VrcTwoFactorCookie);
            var dir = Path.GetDirectoryName(FilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
        catch { }
    }
}

// Voice Fight settings - persisted separately from main settings
public class VoiceFightSettings
{
    public int InputDeviceIndex { get; set; }
    public int OutputDeviceIndex { get; set; } = -1;
    public string StopWord { get; set; } = "";
    public bool MuteTalk { get; set; } = false;
    public List<VfSoundItem> Items { get; set; } = new();

    public class VfSoundItem
    {
        public string Word { get; set; } = "";
        public List<VfSoundFile> Files { get; set; } = new();

        // Legacy single-file fields from pre-v2 saves; migrated to Files on Load.
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string? FilePath { get; set; }
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public float? VolumePercent { get; set; }

        public class VfSoundFile
        {
            public string FilePath { get; set; } = "";
            public float VolumePercent { get; set; } = 100f;
        }
    }

    private static string SavePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCNext", "voicefight_settings.json");

    public static VoiceFightSettings Load()
    {
        try
        {
            if (File.Exists(SavePath))
            {
                var json = File.ReadAllText(SavePath);
                var settings = JsonConvert.DeserializeObject<VoiceFightSettings>(json) ?? new();

                // Migrate legacy single-file items
                bool migrated = false;
                foreach (var item in settings.Items)
                {
                    if (item.Files.Count == 0 && !string.IsNullOrWhiteSpace(item.FilePath))
                    {
                        item.Files.Add(new VfSoundItem.VfSoundFile
                        {
                            FilePath = item.FilePath,
                            VolumePercent = item.VolumePercent ?? 100f
                        });
                        item.FilePath = null;
                        item.VolumePercent = null;
                        migrated = true;
                    }
                }
                if (migrated) settings.Save();
                return settings;
            }
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SavePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(SavePath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
        catch { }
    }
}

// Unified Time Engine — single source of truth for World Time, Instance Time, and Time Spent Together.
// All three timer outputs are driven by timestamp-delta calculation from this one engine.
// DB tables (user_tracking, world_tracking, active_session) remain fully compatible.
//
// ARCHITECTURE:
//   TotalSeconds in DB = accumulated from COMPLETED sessions only (player left, world changed, VRC closed).
//   Active session time is NEVER added to TotalSeconds until the session ends.
//   Display value = TotalSeconds + (now - session_start_utc) for active sessions.
//   active_session DB row stores per-player session_start_utc as JSON.
//   On crash recovery: resume sessions with ORIGINAL timestamps → 0 seconds lost.
//
// SOURCE OF TRUTH for "VRChat is running":
//   _isVrcRunning callback checks for VRChat.exe process (not logs).
//   PRIMARY: Process.Exited event fires within milliseconds of VRC closing.
//   FALLBACK: WatchdogTick every 2s polls process state (covers edge cases).
//   End timestamp uses midpoint between last-confirmed-alive and detection → ~1s avg error.
//   No log-based detection, no delayed cleanup, no blind counting.
//
// DISPOSE BEHAVIOR:
//   If VRChat is still running when VRCNext shuts down → active_session is PRESERVED (not cleared).
//   RestoreActiveSession resumes with original timestamps on next VRCNext launch.
//   If VRChat is NOT running → sessions finalize, active_session cleared.
//
public class UnifiedTimeEngine : IDisposable
{
    // ── Record types (unchanged DB schema) ──

    public class UserRecord
    {
        public long TotalSeconds { get; set; }
        public string LastSeen { get; set; } = "";
        public string LastSeenLocation { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Image { get; set; } = "";
    }

    public class WorldRecord
    {
        public long TotalSeconds { get; set; }
        public string LastVisited { get; set; } = "";
        public int VisitCount { get; set; }
        public string WorldName  { get; set; } = "";
        public string WorldThumb { get; set; } = "";
    }

    // ── Public state (in-memory caches, same access pattern) ──

    public Dictionary<string, UserRecord> Users { get; } = new();
    public Dictionary<string, WorldRecord> Worlds { get; } = new();

    // ── Active session state (timestamp-based) ──
    // Per-player session start times. Key = userId, value = UTC timestamp when session began.
    // These timestamps are persisted to active_session as JSON. They are NEVER reset during a session.
    // TotalSeconds is only updated when a session ENDS (player leave, world change, VRC close, dispose).
    private readonly Dictionary<string, DateTime> _playerSessions = new();

    // World session start — set once per world join, never reset until session ends.
    private DateTime? _worldSessionStart;
    private string _currentWorldId = "";
    private string _currentLocation = "";

    // ── Infrastructure ──
    private readonly SqliteConnection _db;
    private readonly object _lock = new();
    private System.Threading.Timer? _watchdogTimer;  // 5s fallback process check
    private Func<bool>? _isVrcRunning;
    private bool _disposed;
    private bool _vrcWasRunning; // tracks previous VRC state for edge detection
    private Process? _monitoredVrcProcess; // Process.Exited event → near-instant VRC close detection
    private DateTime _lastVrcAliveUtc; // last time we confirmed VRC was running → precise end timestamp
    private DateTime _lastFlushUtc = DateTime.MinValue; // last 30s flush timestamp
    private Action<string>? _logger; // log callback → sends to UI log panel

    private static readonly string UserLegacyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCNext", "user_tracking.json");
    private static readonly string WorldLegacyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCNext", "world_tracking.json");

    private UnifiedTimeEngine(SqliteConnection db) { _db = db; }

    // ── Factory ──

    public static UnifiedTimeEngine Load(Func<bool>? isVrcRunning = null, Action<string>? logger = null)
    {
        var conn = Database.OpenConnection();
        var engine = new UnifiedTimeEngine(conn);
        engine._isVrcRunning = isVrcRunning;
        engine._logger = logger;
        engine.InitSchema();
        engine.MigrateUsersFromJson();
        engine.MigrateWorldsFromJson();
        engine.LoadUsersFromDb();
        engine.LoadWorldsFromDb();
        // Watchdog timer: every 2 seconds, checks VRChat.exe process state.
        // Primary detection is Process.Exited event (near-instant).
        // Watchdog is the fallback in case the event is missed or process handle becomes stale.
        engine._watchdogTimer = new System.Threading.Timer(
            engine.WatchdogTick, null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        return engine;
    }

    // ══════════════════════════════════════════════════════════════════
    //  CORE EVENT METHODS — called from LogWatcher event handlers
    // ══════════════════════════════════════════════════════════════════

    /// <summary>User joined a new world/instance. Ends all prior sessions, starts fresh world session.</summary>
    public void OnWorldJoined(string worldId, string location)
    {
        lock (_lock)
        {
            if (_disposed) return;
            var now = DateTime.UtcNow;

            // End all active player sessions from previous instance (finalizes their TotalSeconds)
            EndAllPlayerSessionsLocked(now);
            // End previous world session
            EndWorldSessionLocked(now);

            _currentWorldId = worldId ?? "";
            _currentLocation = location ?? "";

            // Start new world session
            if (!string.IsNullOrEmpty(_currentWorldId) && _currentWorldId.StartsWith("wrld_"))
            {
                _worldSessionStart = now;
                if (!Worlds.TryGetValue(_currentWorldId, out var rec))
                {
                    rec = new WorldRecord();
                    Worlds[_currentWorldId] = rec;
                }
                rec.VisitCount++;
                rec.LastVisited = now.ToString("o");
                UpsertWorldLocked(_currentWorldId, rec);
            }

            PersistActiveSessionLocked();
        }
    }

    /// <summary>Resume world tracking after VRCNext restart. Session start set by RestoreActiveSession.</summary>
    public void OnWorldResumed(string worldId, string location)
    {
        lock (_lock)
        {
            _currentWorldId = worldId ?? "";
            _currentLocation = location ?? "";
            // World session start will be set by RestoreActiveSession with the persisted timestamp.
            // If RestoreActiveSession is not called, start from now as fallback.
            if (!_worldSessionStart.HasValue)
                _worldSessionStart = DateTime.UtcNow;
            PersistActiveSessionLocked();
        }
    }

    /// <summary>A player joined the current instance. Starts their time session using the LogWatcher timestamp.</summary>
    public void OnPlayerJoined(string userId, DateTime joinedAtUtc)
    {
        lock (_lock)
        {
            if (_disposed || string.IsNullOrEmpty(userId)) return;
            // Use the log timestamp as session start — this is the SAME timestamp used by Instance Info,
            // ensuring zero drift between Instance Info and Time Spent Together for new players.
            _playerSessions[userId] = joinedAtUtc;
            // Ensure user record exists
            if (!Users.TryGetValue(userId, out _))
                Users[userId] = new UserRecord();
            PersistActiveSessionLocked();
        }
    }

    /// <summary>A player left the current instance. Ends their session, adds delta to TotalSeconds.</summary>
    public void OnPlayerLeft(string userId)
    {
        lock (_lock)
        {
            if (_disposed || string.IsNullOrEmpty(userId)) return;
            if (!_playerSessions.TryGetValue(userId, out var sessionStart)) return;

            var now = DateTime.UtcNow;
            var delta = (long)(now - sessionStart).TotalSeconds;
            if (delta > 0 && delta <= 86400) // cap at 24h sanity check
            {
                if (Users.TryGetValue(userId, out var rec))
                {
                    rec.TotalSeconds += delta;
                    rec.LastSeen = now.ToString("o");
                    if (!string.IsNullOrEmpty(_currentLocation))
                        rec.LastSeenLocation = _currentLocation;
                }
            }
            _playerSessions.Remove(userId);
            PersistUserLocked(userId, now);
            PersistActiveSessionLocked();
        }
    }


    // ══════════════════════════════════════════════════════════════════
    //  QUERY METHODS — used by UI to get current time values
    //  All three displays (World Time, Instance Time, Time Spent Together)
    //  derive from the same session_start timestamps.
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get total time spent with a user.
    /// = TotalSeconds (completed sessions) + live session delta (if active).
    /// The live delta uses the SAME session_start as Instance Info → guaranteed consistency.
    /// </summary>
    public (long totalSeconds, string lastSeen) GetUserStats(string userId, bool isCoPresent = false)
    {
        lock (_lock)
        {
            if (!Users.TryGetValue(userId, out var rec))
                return (0, "");

            var total = rec.TotalSeconds;

            // Add live session time. Double-check VRC is actually running via process check.
            if (isCoPresent && _isVrcRunning?.Invoke() == true
                && _playerSessions.TryGetValue(userId, out var sessionStart))
            {
                var live = (long)(DateTime.UtcNow - sessionStart).TotalSeconds;
                if (live > 0 && live <= 86400)
                    total += live;
            }

            return (total, rec.LastSeen);
        }
    }

    /// <summary>
    /// Get total time spent in a world.
    /// = TotalSeconds (completed sessions) + live session delta (if this is the current world).
    /// </summary>
    public (long totalSeconds, int visitCount, string lastVisited) GetWorldStats(string worldId)
    {
        lock (_lock)
        {
            if (!Worlds.TryGetValue(worldId, out var rec))
                return (0, 0, "");

            var total = rec.TotalSeconds;

            if (worldId == _currentWorldId && _worldSessionStart.HasValue
                && _isVrcRunning?.Invoke() == true)
            {
                var live = (long)(DateTime.UtcNow - _worldSessionStart.Value).TotalSeconds;
                if (live > 0 && live <= 86400)
                    total += live;
            }

            return (total, rec.VisitCount, rec.LastVisited);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  USER INFO & FRIEND TRACKING (non-time-counting operations)
    // ══════════════════════════════════════════════════════════════════

    public void UpdateUserInfo(string userId, string displayName, string image)
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(displayName)) return;
            if (!Users.TryGetValue(userId, out var rec))
            {
                rec = new UserRecord();
                Users[userId] = rec;
            }
            if (rec.DisplayName == displayName && rec.Image == image) return;
            rec.DisplayName = displayName;
            if (!string.IsNullOrEmpty(image)) rec.Image = image;
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = @"INSERT INTO user_tracking(user_id,total_seconds,last_seen,last_seen_location,display_name,image)
                    VALUES($uid,0,'','', $dn,$img)
                    ON CONFLICT(user_id) DO UPDATE SET
                        display_name=CASE WHEN excluded.display_name!='' THEN excluded.display_name ELSE user_tracking.display_name END,
                        image=CASE WHEN excluded.image!='' THEN excluded.image ELSE user_tracking.image END";
                cmd.Parameters.AddWithValue("$uid", userId);
                cmd.Parameters.AddWithValue("$dn",  rec.DisplayName);
                cmd.Parameters.AddWithValue("$img", rec.Image);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }

    /// <summary>Updates LastSeen/LastSeenLocation for online friends. No time accumulation.</summary>
    public void UpdateFriendTracking(IEnumerable<(string userId, string location, string presence)> onlineFriends)
    {
        lock (_lock)
        {
            if (_disposed) return;
            var now = DateTime.UtcNow;
            var changed = new List<(string userId, UserRecord rec)>();

            foreach (var (userId, location, presence) in onlineFriends)
            {
                if (string.IsNullOrEmpty(userId)) continue;
                if (!Users.TryGetValue(userId, out var rec))
                {
                    rec = new UserRecord();
                    Users[userId] = rec;
                }
                if (presence != "offline")
                {
                    rec.LastSeen = now.ToString("o");
                    if (!string.IsNullOrEmpty(location) && location != "offline" && location != "private")
                        rec.LastSeenLocation = location;
                }
                changed.Add((userId, rec));
            }

            if (changed.Count == 0) return;
            try
            {
                using var tx = _db.BeginTransaction();
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO user_tracking(user_id,total_seconds,last_seen,last_seen_location,display_name,image)
                    VALUES($uid,$ts,$ls,$lsl,$dn,$img)
                    ON CONFLICT(user_id) DO UPDATE SET
                        total_seconds=excluded.total_seconds,
                        last_seen=excluded.last_seen,
                        last_seen_location=excluded.last_seen_location,
                        display_name=CASE WHEN excluded.display_name!='' THEN excluded.display_name ELSE user_tracking.display_name END,
                        image=CASE WHEN excluded.image!='' THEN excluded.image ELSE user_tracking.image END";
                var pUid = cmd.Parameters.Add("$uid", SqliteType.Text);
                var pTs  = cmd.Parameters.Add("$ts",  SqliteType.Integer);
                var pLs  = cmd.Parameters.Add("$ls",  SqliteType.Text);
                var pLsl = cmd.Parameters.Add("$lsl", SqliteType.Text);
                var pDn  = cmd.Parameters.Add("$dn",  SqliteType.Text);
                var pImg = cmd.Parameters.Add("$img", SqliteType.Text);
                foreach (var (userId, rec) in changed)
                {
                    pUid.Value = userId; pTs.Value = rec.TotalSeconds;
                    pLs.Value = rec.LastSeen; pLsl.Value = rec.LastSeenLocation;
                    pDn.Value = rec.DisplayName; pImg.Value = rec.Image;
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            catch { }
        }
    }

    public void UpdateWorldInfo(string worldId, string name, string thumb)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(worldId) || string.IsNullOrEmpty(name)) return;
            if (!Worlds.TryGetValue(worldId, out var rec)) return;
            if (rec.WorldName == name && rec.WorldThumb == thumb) return;
            rec.WorldName  = name;
            rec.WorldThumb = thumb;
            UpsertWorldLocked(worldId, rec);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  CRASH RECOVERY — 0 seconds data loss
    //
    //  active_session stores per-player session_start_utc as JSON.
    //  On recovery: sessions resume with their ORIGINAL start timestamps.
    //  Display = TotalSeconds + (now - original_session_start) → exact, no gap.
    //
    //  Validation:
    //  1. VRChat.exe must be running (process check, not log-based)
    //  2. Location must match (same instance)
    //  3. Only players confirmed present by LogWatcher get sessions restored
    //  4. Max session age 24h (sanity cap)
    // ══════════════════════════════════════════════════════════════════

    public void RestoreActiveSession(string currentLocation, HashSet<string> currentPlayerIds)
    {
        lock (_lock)
        {
            if (_disposed) return;
            try
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT location,co_present_ids,last_flush_utc FROM active_session WHERE id=1";
                using var r = cmd.ExecuteReader();
                if (!r.Read())
                {
                    // No active_session row — clear any stale sessions pre-populated from catch-up
                    _playerSessions.Clear();
                    _worldSessionStart = null;
                    return;
                }

                var location = r.GetString(0);
                var sessionsJson = r.GetString(1);
                var worldStartStr = r.GetString(2);
                r.Close();

                // Validation 1: Location must match
                if (string.IsNullOrEmpty(location) || location != currentLocation)
                {
                    _playerSessions.Clear();
                    _worldSessionStart = null;
                    ClearActiveSessionLocked();
                    return;
                }

                // Validation 2: VRChat.exe must be running (process-level check)
                if (_isVrcRunning?.Invoke() != true)
                {
                    _playerSessions.Clear();
                    _worldSessionStart = null;
                    ClearActiveSessionLocked();
                    return;
                }

                var now = DateTime.UtcNow;

                // Parse per-player session starts from JSON
                Dictionary<string, string>? savedSessions = null;
                try { savedSessions = JsonConvert.DeserializeObject<Dictionary<string, string>>(sessionsJson); }
                catch { }

                // Fallback: old comma-separated format → treat as stale (can't recover exact starts)
                if (savedSessions == null)
                {
                    ClearActiveSessionLocked();
                    return;
                }

                // Validation 3: Only restore players confirmed present by LogWatcher right now.
                // Session start is set to NOW — TotalSeconds already contains all time up to the
                // last 30s flush. We only need to track from this restart forward to avoid double-counting.
                foreach (var (userId, startStr) in savedSessions)
                {
                    if (!currentPlayerIds.Contains(userId)) continue;
                    // Validate the stored timestamp is parseable and not stale (sanity check only)
                    if (!DateTime.TryParse(startStr, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var sessionStart))
                        continue;
                    var age = (now - sessionStart).TotalSeconds;
                    if (age < 0 || age > 86400) continue;

                    _playerSessions[userId] = now; // start from NOW — DB already has flushed time
                    if (!Users.ContainsKey(userId))
                        Users[userId] = new UserRecord();
                }

                // Restore world session — also start from NOW for the same reason
                if (DateTime.TryParse(worldStartStr, null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var wStart))
                {
                    var wAge = (now - wStart).TotalSeconds;
                    if (wAge >= 0 && wAge <= 86400)
                    {
                        var colon = currentLocation.IndexOf(':');
                        var worldId = colon >= 0 ? currentLocation.Substring(0, colon) : currentLocation;
                        if (!string.IsNullOrEmpty(worldId) && worldId.StartsWith("wrld_"))
                        {
                            _currentWorldId = worldId;
                            _currentLocation = currentLocation;
                            _worldSessionStart = now; // start from NOW — DB already has flushed time
                        }
                    }
                }

                PersistActiveSessionLocked();
            }
            catch
            {
                _playerSessions.Clear();
                _worldSessionStart = null;
                ClearActiveSessionLocked();
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════
    //  BULK IMPORT (VRCX migration)
    // ══════════════════════════════════════════════════════════════════

    public void BulkMergeUsers(IEnumerable<(string userId, string displayName, long seconds, string lastSeen)> entries)
    {
        lock (_lock)
        {
            if (_disposed) return;
            try
            {
                using var tx  = _db.BeginTransaction();
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO user_tracking(user_id,total_seconds,last_seen,last_seen_location,display_name,image)
                    VALUES($uid,$ts,$ls,'',$dn,'')
                    ON CONFLICT(user_id) DO UPDATE SET
                        total_seconds = user_tracking.total_seconds + excluded.total_seconds,
                        last_seen = CASE WHEN excluded.last_seen > user_tracking.last_seen THEN excluded.last_seen ELSE user_tracking.last_seen END,
                        display_name = CASE WHEN excluded.display_name != '' AND user_tracking.display_name = '' THEN excluded.display_name ELSE user_tracking.display_name END";
                var pUid = cmd.Parameters.Add("$uid", SqliteType.Text);
                var pTs  = cmd.Parameters.Add("$ts",  SqliteType.Integer);
                var pLs  = cmd.Parameters.Add("$ls",  SqliteType.Text);
                var pDn  = cmd.Parameters.Add("$dn",  SqliteType.Text);
                foreach (var (userId, displayName, seconds, lastSeen) in entries)
                {
                    pUid.Value = userId; pTs.Value = seconds;
                    pLs.Value  = lastSeen; pDn.Value = displayName;
                    cmd.ExecuteNonQuery();
                    if (!Users.TryGetValue(userId, out var rec)) { rec = new UserRecord(); Users[userId] = rec; }
                    rec.TotalSeconds += seconds;
                    if (string.IsNullOrEmpty(rec.DisplayName) && !string.IsNullOrEmpty(displayName)) rec.DisplayName = displayName;
                    if (string.Compare(lastSeen, rec.LastSeen, StringComparison.Ordinal) > 0) rec.LastSeen = lastSeen;
                }
                tx.Commit();
            }
            catch { }
        }
    }

    public void BulkMergeWorlds(IEnumerable<(string worldId, string worldName, long seconds, int visitCount, string lastVisited)> entries)
    {
        lock (_lock)
        {
            if (_disposed) return;
            try
            {
                using var tx  = _db.BeginTransaction();
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO world_tracking(world_id,total_seconds,visit_count,last_visited,world_name,world_thumb)
                    VALUES($wid,$ts,$vc,$lv,$wn,'')
                    ON CONFLICT(world_id) DO UPDATE SET
                        total_seconds = world_tracking.total_seconds + excluded.total_seconds,
                        visit_count   = world_tracking.visit_count   + excluded.visit_count,
                        last_visited  = CASE WHEN excluded.last_visited > world_tracking.last_visited THEN excluded.last_visited ELSE world_tracking.last_visited END,
                        world_name    = CASE WHEN excluded.world_name != '' AND world_tracking.world_name = '' THEN excluded.world_name ELSE world_tracking.world_name END";
                var pWid = cmd.Parameters.Add("$wid", SqliteType.Text);
                var pTs  = cmd.Parameters.Add("$ts",  SqliteType.Integer);
                var pVc  = cmd.Parameters.Add("$vc",  SqliteType.Integer);
                var pLv  = cmd.Parameters.Add("$lv",  SqliteType.Text);
                var pWn  = cmd.Parameters.Add("$wn",  SqliteType.Text);
                foreach (var (worldId, worldName, seconds, visitCount, lastVisited) in entries)
                {
                    pWid.Value = worldId; pTs.Value = seconds;
                    pVc.Value  = visitCount; pLv.Value = lastVisited; pWn.Value = worldName;
                    cmd.ExecuteNonQuery();
                    if (!Worlds.TryGetValue(worldId, out var rec)) { rec = new WorldRecord(); Worlds[worldId] = rec; }
                    rec.TotalSeconds += seconds; rec.VisitCount += visitCount;
                    if (string.IsNullOrEmpty(rec.WorldName) && !string.IsNullOrEmpty(worldName)) rec.WorldName = worldName;
                    if (string.Compare(lastVisited, rec.LastVisited, StringComparison.Ordinal) > 0) rec.LastVisited = lastVisited;
                }
                tx.Commit();
            }
            catch { }
        }
    }

    public void Save() { } // persistence handled by event methods and watchdog

    // ══════════════════════════════════════════════════════════════════
    //  WATCHDOG — VRChat process monitor (5-second interval)
    //  This is the HARD SOURCE OF TRUTH for whether VRChat is running.
    //  Not log-based. Not delayed. Process check via _isVrcRunning callback.
    // ══════════════════════════════════════════════════════════════════

    private void WatchdogTick(object? state)
    {
        lock (_lock)
        {
            if (_disposed) return;

            var vrcRunning = _isVrcRunning?.Invoke() ?? false;

            // Edge detection: VRC was running, now it's not → end all sessions
            if (_vrcWasRunning && !vrcRunning)
            {
                HandleVrcClosedLocked();
            }

            if (vrcRunning)
            {
                _lastVrcAliveUtc = DateTime.UtcNow;
                // Attach Process.Exited event for near-instant detection when VRC closes.
                // The watchdog (2s) is the fallback; Process.Exited fires within milliseconds.
                AttachProcessExitedLocked();
            }

            _vrcWasRunning = vrcRunning;

            // Every 30 seconds: flush accumulated time into TotalSeconds in the DB.
            // This guarantees max 30s data loss on any crash, hard kill, or power failure.
            // After flush, session starts are reset to now so the next flush starts clean.
            if (vrcRunning && (_playerSessions.Count > 0 || _worldSessionStart.HasValue))
            {
                var now = DateTime.UtcNow;
                if ((now - _lastFlushUtc).TotalSeconds >= 30)
                {
                    FlushSessionsToDbLocked(now);
                    _lastFlushUtc = now;
                }
                PersistActiveSessionLocked();
            }
        }
    }

    /// <summary>
    /// Attaches a Process.Exited handler to the VRChat process for near-instant close detection.
    /// This fires within milliseconds of process exit, unlike the 2s watchdog poll.
    /// </summary>
    private void AttachProcessExitedLocked()
    {
        if (_monitoredVrcProcess != null)
        {
            try { if (!_monitoredVrcProcess.HasExited) return; } // already monitoring a live process
            catch { }
            // Previous monitored process is gone or stale — detach and re-attach
            try { _monitoredVrcProcess.Dispose(); } catch { }
            _monitoredVrcProcess = null;
        }
        try
        {
            var procs = Process.GetProcessesByName("VRChat");
            foreach (var p in procs)
            {
                try
                {
                    if (p.HasExited) { p.Dispose(); continue; }
                    p.EnableRaisingEvents = true;
                    p.Exited += OnVrcProcessExited;
                    _monitoredVrcProcess = p;
                    return; // attached to first live process
                }
                catch { p.Dispose(); }
            }
        }
        catch { }
    }

    /// <summary>
    /// Flushes elapsed session time into TotalSeconds in the DB, then resets session starts to now.
    /// Called every 30 seconds. Guarantees max 30s data loss on crash/power failure.
    /// </summary>
    private void FlushSessionsToDbLocked(DateTime now)
    {
        // Flush player sessions
        var userIds = _playerSessions.Keys.ToList();
        foreach (var userId in userIds)
        {
            var delta = (long)(now - _playerSessions[userId]).TotalSeconds;
            if (delta <= 0 || delta > 86400) continue;
            if (Users.TryGetValue(userId, out var rec))
            {
                rec.TotalSeconds += delta;
                rec.LastSeen = now.ToString("o");
                _logger?.Invoke($"[TIMER] Spend Time saved: {rec.DisplayName} +{delta}s — overall time: {FormatDuration(rec.TotalSeconds)}");
            }
            _playerSessions[userId] = now;
        }
        if (userIds.Count > 0)
            PersistAllUsersLocked(userIds, now);

        // Flush world session
        if (_worldSessionStart.HasValue && !string.IsNullOrEmpty(_currentWorldId) && _currentWorldId.StartsWith("wrld_"))
        {
            var delta = (long)(now - _worldSessionStart.Value).TotalSeconds;
            if (delta > 0 && delta <= 86400)
            {
                if (!Worlds.TryGetValue(_currentWorldId, out var rec))
                {
                    rec = new WorldRecord();
                    Worlds[_currentWorldId] = rec;
                }
                rec.TotalSeconds += delta;
                rec.LastVisited = now.ToString("o");
                UpsertWorldLocked(_currentWorldId, rec);
                var wName = rec.WorldName.Length > 0 ? rec.WorldName : _currentWorldId;
                _logger?.Invoke($"[TIMER] World Time saved: +{delta}s — overall time in \"{wName}\": {FormatDuration(rec.TotalSeconds)}");
            }
            _worldSessionStart = now;
        }
    }

    private static string FormatDuration(long totalSeconds)
    {
        var d = totalSeconds / 86400;
        var h = (totalSeconds % 86400) / 3600;
        var m = (totalSeconds % 3600) / 60;
        var s = totalSeconds % 60;
        if (d > 0) return $"{d}d {h}h {m}m {s}s";
        if (h > 0) return $"{h}h {m}m {s}s";
        if (m > 0) return $"{m}m {s}s";
        return $"{s}s";
    }

    /// <summary>
    /// Process.Exited event handler — fires within milliseconds of VRChat.exe closing.
    /// Uses _lastVrcAliveUtc (last confirmed alive from watchdog) to bound the end timestamp.
    /// Worst-case overcount = 1 watchdog interval (2s), not 5s.
    /// </summary>
    private void OnVrcProcessExited(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            if (_disposed) return;
            // Double-check VRC is really gone (could be multiple instances)
            var stillRunning = _isVrcRunning?.Invoke() ?? false;
            if (stillRunning) return;
            HandleVrcClosedLocked();
            _vrcWasRunning = false;
        }
    }

    /// <summary>
    /// Central handler for VRC close. Uses the midpoint between last confirmed alive and now
    /// to minimize overcount. Called from both watchdog edge detection and Process.Exited.
    /// </summary>
    private void HandleVrcClosedLocked()
    {
        if (_playerSessions.Count == 0 && !_worldSessionStart.HasValue) return;

        // Best estimate of actual VRC close time:
        // We know VRC was alive at _lastVrcAliveUtc and is dead now.
        // Use the midpoint to minimize average error.
        var now = DateTime.UtcNow;
        var endTime = _lastVrcAliveUtc > DateTime.MinValue
            ? _lastVrcAliveUtc + TimeSpan.FromTicks((now - _lastVrcAliveUtc).Ticks / 2)
            : now;
        // Sanity: endTime must not be in the future or more than 10s in the past
        if (endTime > now) endTime = now;
        if ((now - endTime).TotalSeconds > 10) endTime = now;

        EndAllPlayerSessionsLocked(endTime);
        EndWorldSessionLocked(endTime);
        _currentWorldId = "";
        _currentLocation = "";
        ClearActiveSessionLocked();

        // Clean up monitored process
        try { _monitoredVrcProcess?.Dispose(); } catch { }
        _monitoredVrcProcess = null;
    }

    // ══════════════════════════════════════════════════════════════════
    //  INTERNAL — Session end helpers
    //  "End" = compute delta, add to TotalSeconds, persist to DB, remove session.
    //  TotalSeconds is ONLY modified here and in OnPlayerLeft.
    // ══════════════════════════════════════════════════════════════════

    private void EndAllPlayerSessionsLocked(DateTime now)
    {
        if (_playerSessions.Count == 0) return;
        var userIds = _playerSessions.Keys.ToList();
        foreach (var userId in userIds)
        {
            var sessionStart = _playerSessions[userId];
            var delta = (long)(now - sessionStart).TotalSeconds;
            if (delta > 0 && delta <= 86400)
            {
                if (Users.TryGetValue(userId, out var rec))
                {
                    rec.TotalSeconds += delta;
                    rec.LastSeen = now.ToString("o");
                    if (!string.IsNullOrEmpty(_currentLocation))
                        rec.LastSeenLocation = _currentLocation;
                }
            }
        }
        PersistAllUsersLocked(userIds, now);
        _playerSessions.Clear();
    }

    private void EndWorldSessionLocked(DateTime now)
    {
        if (!_worldSessionStart.HasValue) return;
        if (!string.IsNullOrEmpty(_currentWorldId) && _currentWorldId.StartsWith("wrld_"))
        {
            var delta = (long)(now - _worldSessionStart.Value).TotalSeconds;
            if (delta > 0 && delta <= 86400)
            {
                if (!Worlds.TryGetValue(_currentWorldId, out var rec))
                {
                    rec = new WorldRecord();
                    Worlds[_currentWorldId] = rec;
                }
                rec.TotalSeconds += delta;
                rec.LastVisited = now.ToString("o");
                UpsertWorldLocked(_currentWorldId, rec);
            }
        }
        _worldSessionStart = null;
    }

    // ══════════════════════════════════════════════════════════════════
    //  INTERNAL — DB persistence
    // ══════════════════════════════════════════════════════════════════

    private void InitSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = @"CREATE TABLE IF NOT EXISTS user_tracking (
            user_id            TEXT    PRIMARY KEY,
            total_seconds      INTEGER NOT NULL DEFAULT 0,
            last_seen          TEXT    NOT NULL DEFAULT '',
            last_seen_location TEXT    NOT NULL DEFAULT '',
            display_name       TEXT    NOT NULL DEFAULT '',
            image              TEXT    NOT NULL DEFAULT ''
        )";
        cmd.ExecuteNonQuery();

        using var idx = _db.CreateCommand();
        idx.CommandText = "CREATE INDEX IF NOT EXISTS idx_ut_lastseen ON user_tracking(last_seen DESC)";
        try { idx.ExecuteNonQuery(); } catch { }

        foreach (var col in new[] { "display_name TEXT NOT NULL DEFAULT ''", "image TEXT NOT NULL DEFAULT ''" })
        {
            try
            {
                using var ac = _db.CreateCommand();
                ac.CommandText = $"ALTER TABLE user_tracking ADD COLUMN {col}";
                ac.ExecuteNonQuery();
            }
            catch { }
        }

        using var wcmd = _db.CreateCommand();
        wcmd.CommandText = @"CREATE TABLE IF NOT EXISTS world_tracking (
            world_id      TEXT    PRIMARY KEY,
            total_seconds INTEGER NOT NULL DEFAULT 0,
            visit_count   INTEGER NOT NULL DEFAULT 0,
            last_visited  TEXT    NOT NULL DEFAULT '',
            world_name    TEXT    NOT NULL DEFAULT '',
            world_thumb   TEXT    NOT NULL DEFAULT ''
        )";
        wcmd.ExecuteNonQuery();

        foreach (var col in new[] { "world_name TEXT NOT NULL DEFAULT ''", "world_thumb TEXT NOT NULL DEFAULT ''" })
        {
            try
            {
                using var ac = _db.CreateCommand();
                ac.CommandText = $"ALTER TABLE world_tracking ADD COLUMN {col}";
                ac.ExecuteNonQuery();
            }
            catch { }
        }

        using var as_cmd = _db.CreateCommand();
        as_cmd.CommandText = @"CREATE TABLE IF NOT EXISTS active_session (
            id             INTEGER PRIMARY KEY CHECK(id = 1),
            location       TEXT    NOT NULL DEFAULT '',
            co_present_ids TEXT    NOT NULL DEFAULT '',
            last_flush_utc TEXT    NOT NULL DEFAULT ''
        )";
        as_cmd.ExecuteNonQuery();
    }

    private void MigrateUsersFromJson()
    {
        if (!File.Exists(UserLegacyPath)) return;
        try
        {
            var json = File.ReadAllText(UserLegacyPath);
            var legacy = JsonConvert.DeserializeObject<UserLegacy>(json);
            if (legacy?.Users == null) { File.Delete(UserLegacyPath); return; }
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT OR IGNORE INTO user_tracking(user_id,total_seconds,last_seen,last_seen_location)
                VALUES($uid,$ts,$ls,$lsl)";
            var pUid = cmd.Parameters.Add("$uid", SqliteType.Text);
            var pTs  = cmd.Parameters.Add("$ts",  SqliteType.Integer);
            var pLs  = cmd.Parameters.Add("$ls",  SqliteType.Text);
            var pLsl = cmd.Parameters.Add("$lsl", SqliteType.Text);
            foreach (var (userId, rec) in legacy.Users)
            {
                pUid.Value = userId; pTs.Value = rec.TotalSeconds;
                pLs.Value = rec.LastSeen ?? ""; pLsl.Value = rec.LastSeenLocation ?? "";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            File.Delete(UserLegacyPath);
        }
        catch { }
    }

    private void MigrateWorldsFromJson()
    {
        if (!File.Exists(WorldLegacyPath)) return;
        try
        {
            var json = File.ReadAllText(WorldLegacyPath);
            var legacy = JsonConvert.DeserializeObject<WorldLegacy>(json);
            if (legacy?.Worlds == null) { File.Delete(WorldLegacyPath); return; }
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT OR IGNORE INTO world_tracking(world_id,total_seconds,visit_count,last_visited)
                VALUES($wid,$ts,$vc,$lv)";
            var pWid = cmd.Parameters.Add("$wid", SqliteType.Text);
            var pTs  = cmd.Parameters.Add("$ts",  SqliteType.Integer);
            var pVc  = cmd.Parameters.Add("$vc",  SqliteType.Integer);
            var pLv  = cmd.Parameters.Add("$lv",  SqliteType.Text);
            foreach (var (worldId, rec) in legacy.Worlds)
            {
                pWid.Value = worldId; pTs.Value = rec.TotalSeconds;
                pVc.Value = rec.VisitCount; pLv.Value = rec.LastVisited ?? "";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            File.Delete(WorldLegacyPath);
        }
        catch { }
    }

    private void LoadUsersFromDb()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT user_id,total_seconds,last_seen,last_seen_location,display_name,image FROM user_tracking";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            Users[r.GetString(0)] = new UserRecord
            {
                TotalSeconds     = r.GetInt64(1),
                LastSeen         = r.GetString(2),
                LastSeenLocation = r.GetString(3),
                DisplayName      = r.GetString(4),
                Image            = r.GetString(5),
            };
    }

    private void LoadWorldsFromDb()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT world_id,total_seconds,visit_count,last_visited,world_name,world_thumb FROM world_tracking";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            Worlds[r.GetString(0)] = new WorldRecord
            {
                TotalSeconds = r.GetInt64(1),
                VisitCount   = r.GetInt32(2),
                LastVisited  = r.GetString(3),
                WorldName    = r.GetString(4),
                WorldThumb   = r.GetString(5),
            };
    }

    private void PersistUserLocked(string userId, DateTime now)
    {
        if (!Users.TryGetValue(userId, out var rec)) return;
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"INSERT INTO user_tracking(user_id,total_seconds,last_seen,last_seen_location,display_name,image)
                VALUES($uid,$ts,$ls,$lsl,$dn,$img)
                ON CONFLICT(user_id) DO UPDATE SET
                    total_seconds=excluded.total_seconds, last_seen=excluded.last_seen,
                    last_seen_location=excluded.last_seen_location,
                    display_name=CASE WHEN excluded.display_name!='' THEN excluded.display_name ELSE user_tracking.display_name END,
                    image=CASE WHEN excluded.image!='' THEN excluded.image ELSE user_tracking.image END";
            cmd.Parameters.AddWithValue("$uid", userId);
            cmd.Parameters.AddWithValue("$ts",  rec.TotalSeconds);
            cmd.Parameters.AddWithValue("$ls",  now.ToString("o"));
            cmd.Parameters.AddWithValue("$lsl", rec.LastSeenLocation);
            cmd.Parameters.AddWithValue("$dn",  rec.DisplayName);
            cmd.Parameters.AddWithValue("$img", rec.Image);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    private void PersistAllUsersLocked(IEnumerable<string> userIds, DateTime now)
    {
        try
        {
            using var tx = _db.BeginTransaction();
            using var cmd = _db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO user_tracking(user_id,total_seconds,last_seen,last_seen_location,display_name,image)
                VALUES($uid,$ts,$ls,$lsl,$dn,$img)
                ON CONFLICT(user_id) DO UPDATE SET
                    total_seconds=excluded.total_seconds, last_seen=excluded.last_seen,
                    last_seen_location=excluded.last_seen_location,
                    display_name=CASE WHEN excluded.display_name!='' THEN excluded.display_name ELSE user_tracking.display_name END,
                    image=CASE WHEN excluded.image!='' THEN excluded.image ELSE user_tracking.image END";
            var pUid = cmd.Parameters.Add("$uid", SqliteType.Text);
            var pTs  = cmd.Parameters.Add("$ts",  SqliteType.Integer);
            var pLs  = cmd.Parameters.Add("$ls",  SqliteType.Text);
            var pLsl = cmd.Parameters.Add("$lsl", SqliteType.Text);
            var pDn  = cmd.Parameters.Add("$dn",  SqliteType.Text);
            var pImg = cmd.Parameters.Add("$img", SqliteType.Text);
            var nowStr = now.ToString("o");
            foreach (var userId in userIds)
            {
                if (!Users.TryGetValue(userId, out var rec)) continue;
                pUid.Value = userId; pTs.Value = rec.TotalSeconds;
                pLs.Value = nowStr;  pLsl.Value = rec.LastSeenLocation;
                pDn.Value = rec.DisplayName; pImg.Value = rec.Image;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch { }
    }

    private void UpsertWorldLocked(string worldId, WorldRecord rec)
    {
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"INSERT INTO world_tracking(world_id,total_seconds,visit_count,last_visited,world_name,world_thumb)
                VALUES($wid,$ts,$vc,$lv,$wn,$wt)
                ON CONFLICT(world_id) DO UPDATE SET
                    total_seconds=excluded.total_seconds, visit_count=excluded.visit_count,
                    last_visited=excluded.last_visited,
                    world_name=CASE WHEN excluded.world_name!='' THEN excluded.world_name ELSE world_tracking.world_name END,
                    world_thumb=CASE WHEN excluded.world_thumb!='' THEN excluded.world_thumb ELSE world_tracking.world_thumb END";
            cmd.Parameters.AddWithValue("$wid", worldId);
            cmd.Parameters.AddWithValue("$ts",  rec.TotalSeconds);
            cmd.Parameters.AddWithValue("$vc",  rec.VisitCount);
            cmd.Parameters.AddWithValue("$lv",  rec.LastVisited);
            cmd.Parameters.AddWithValue("$wn",  rec.WorldName);
            cmd.Parameters.AddWithValue("$wt",  rec.WorldThumb);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    /// <summary>
    /// Persists active session state to DB for crash recovery.
    /// co_present_ids = JSON { "userId": "session_start_utc_iso", ... }
    /// last_flush_utc = world session start UTC ISO (for world time recovery)
    /// </summary>
    private void PersistActiveSessionLocked()
    {
        if (_playerSessions.Count == 0 && !_worldSessionStart.HasValue)
        {
            ClearActiveSessionLocked();
            return;
        }
        try
        {
            // Serialize per-player session starts as JSON
            var sessionsDict = _playerSessions.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.ToString("o"));
            var sessionsJson = JsonConvert.SerializeObject(sessionsDict);

            using var cmd = _db.CreateCommand();
            cmd.CommandText = @"INSERT INTO active_session(id,location,co_present_ids,last_flush_utc)
                VALUES(1,$loc,$ids,$ts)
                ON CONFLICT(id) DO UPDATE SET
                    location=excluded.location,
                    co_present_ids=excluded.co_present_ids,
                    last_flush_utc=excluded.last_flush_utc";
            cmd.Parameters.AddWithValue("$loc", _currentLocation);
            cmd.Parameters.AddWithValue("$ids", sessionsJson);
            cmd.Parameters.AddWithValue("$ts", _worldSessionStart?.ToString("o") ?? "");
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    private void ClearActiveSessionLocked()
    {
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM active_session WHERE id=1";
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    // ══════════════════════════════════════════════════════════════════
    //  DISPOSE
    // ══════════════════════════════════════════════════════════════════

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watchdogTimer?.Dispose();
        _watchdogTimer = null;
        lock (_lock)
        {
            var vrcRunning = _isVrcRunning?.Invoke() ?? false;
            if (vrcRunning && (_playerSessions.Count > 0 || _worldSessionStart.HasValue))
            {
                // VRChat is still running — VRCNext is being restarted.
                // Do NOT end sessions or clear active_session.
                // Just persist the current state so RestoreActiveSession can resume with original timestamps.
                PersistActiveSessionLocked();
            }
            else
            {
                // VRChat is not running — true shutdown. Finalize all sessions.
                var now = DateTime.UtcNow;
                EndAllPlayerSessionsLocked(now);
                EndWorldSessionLocked(now);
                ClearActiveSessionLocked();
            }
        }
        // Clean up process monitor
        try { _monitoredVrcProcess?.Dispose(); } catch { }
        _monitoredVrcProcess = null;
        try { _db.Close(); } catch { }
        _db.Dispose();
    }

    // ── Legacy migration types ──

    private class UserLegacy { public Dictionary<string, UserRecord>? Users { get; set; } }
    private class WorldLegacy { public Dictionary<string, WorldRecord>? Worlds { get; set; } }

    // ── Static utility (kept from WorldTimeTracker for photo world detection) ──

    public static string? ExtractWorldIdFromPng(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var sig = new byte[8];
            if (fs.Read(sig, 0, 8) != 8) return null;
            if (sig[0] != 137 || sig[1] != 80 || sig[2] != 78 || sig[3] != 71) return null;

            while (fs.Position < fs.Length - 8)
            {
                var lenBuf = new byte[4];
                if (fs.Read(lenBuf, 0, 4) != 4) break;
                int chunkLen = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];

                var typeBuf = new byte[4];
                if (fs.Read(typeBuf, 0, 4) != 4) break;
                var chunkType = System.Text.Encoding.ASCII.GetString(typeBuf);

                if (chunkType == "IEND") break;

                if ((chunkType == "tEXt" || chunkType == "iTXt" || chunkType == "zTXt") && chunkLen > 0 && chunkLen < 131072)
                {
                    var data = new byte[chunkLen];
                    if (fs.Read(data, 0, chunkLen) != chunkLen) break;

                    var text = System.Text.Encoding.UTF8.GetString(data);

                    if (text.Contains("wrld_"))
                    {
                        var idx = text.IndexOf("wrld_");
                        var end = idx;
                        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_' || text[end] == '-'))
                            end++;
                        var worldId = text.Substring(idx, end - idx);
                        if (worldId.Length > 10) return worldId;
                    }

                    fs.Seek(4, SeekOrigin.Current);
                    continue;
                }

                fs.Seek(chunkLen + 4, SeekOrigin.Current);
            }
        }
        catch { }
        return null;
    }
}

// stores which players were in the instance when a photo was taken. persisted in SQLite.
public class PhotoPlayersStore : IDisposable
{
    public class PhotoPlayerInfo
    {
        public string UserId      { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Image       { get; set; } = "";
    }

    public class PhotoRecord
    {
        public List<PhotoPlayerInfo> Players { get; set; } = new();
        public string WorldId { get; set; } = "";
    }

    // In-memory cache, same access pattern as before
    public Dictionary<string, PhotoRecord> Photos { get; } = new();

    private readonly SqliteConnection _db;
    private bool _disposed;

    private static readonly string LegacyFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCNext", "photo_players.json");

    private PhotoPlayersStore(SqliteConnection db) { _db = db; }

    public static PhotoPlayersStore Load()
    {
        var conn = Database.OpenConnection();
        var store = new PhotoPlayersStore(conn);
        store.InitSchema();
        store.MigrateFromJson();
        store.LoadFromDb();
        return store;
    }

    private void InitSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS photo_records (
                file_name TEXT PRIMARY KEY,
                world_id  TEXT DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS photo_record_players (
                file_name    TEXT NOT NULL,
                user_id      TEXT DEFAULT '',
                display_name TEXT DEFAULT '',
                image        TEXT DEFAULT '',
                PRIMARY KEY (file_name, user_id)
            );
        ";
        cmd.ExecuteNonQuery();
    }

    private void MigrateFromJson()
    {
        if (!File.Exists(LegacyFilePath)) return;
        try
        {
            var json = File.ReadAllText(LegacyFilePath);
            // Legacy format: { "Photos": { "fileName": { "WorldId": "", "Players": [...] } } }
            var legacy = JsonConvert.DeserializeObject<PhotoPlayersStore_Legacy>(json);
            if (legacy?.Photos == null) { File.Delete(LegacyFilePath); return; }

            using var tx = _db.BeginTransaction();
            using var recCmd = _db.CreateCommand();
            recCmd.Transaction = tx;
            recCmd.CommandText = "INSERT OR IGNORE INTO photo_records(file_name,world_id) VALUES($fn,$wid)";
            var pfn  = recCmd.Parameters.Add("$fn",  SqliteType.Text);
            var pwid = recCmd.Parameters.Add("$wid", SqliteType.Text);

            using var plCmd = _db.CreateCommand();
            plCmd.Transaction = tx;
            plCmd.CommandText = @"INSERT OR IGNORE INTO photo_record_players
                (file_name,user_id,display_name,image) VALUES($fn,$uid,$dn,$img)";
            var ppfn  = plCmd.Parameters.Add("$fn",  SqliteType.Text);
            var ppuid = plCmd.Parameters.Add("$uid", SqliteType.Text);
            var ppdn  = plCmd.Parameters.Add("$dn",  SqliteType.Text);
            var ppimg = plCmd.Parameters.Add("$img", SqliteType.Text);

            foreach (var (fileName, rec) in legacy.Photos)
            {
                pfn.Value  = fileName;
                pwid.Value = rec.WorldId ?? "";
                recCmd.ExecuteNonQuery();

                ppfn.Value = fileName;
                foreach (var p in rec.Players ?? new())
                {
                    ppuid.Value = p.UserId ?? "";
                    ppdn.Value  = p.DisplayName ?? "";
                    ppimg.Value = p.Image ?? "";
                    plCmd.ExecuteNonQuery();
                }
            }
            tx.Commit();
            File.Delete(LegacyFilePath);
        }
        catch { }
    }

    private void LoadFromDb()
    {
        var playerMap = new Dictionary<string, List<PhotoPlayerInfo>>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT file_name,user_id,display_name,image FROM photo_record_players";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var fn = r.GetString(0);
                if (!playerMap.TryGetValue(fn, out var list))
                    playerMap[fn] = list = new();
                list.Add(new PhotoPlayerInfo { UserId = r.GetString(1), DisplayName = r.GetString(2), Image = r.GetString(3) });
            }
        }
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT file_name,world_id FROM photo_records";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var fn = r.GetString(0);
                Photos[fn] = new PhotoRecord
                {
                    WorldId = r.GetString(1),
                    Players = playerMap.TryGetValue(fn, out var pl) ? pl : new(),
                };
            }
        }
    }

    // Public API

    public void RecordPhoto(string fileName, IEnumerable<(string userId, string displayName, string image)> players, string worldId)
    {
        var rec = new PhotoRecord
        {
            WorldId = worldId,
            Players = players.Select(p => new PhotoPlayerInfo { UserId = p.userId, DisplayName = p.displayName, Image = p.image }).ToList()
        };
        Photos[fileName] = rec;

        try
        {
            using var tx = _db.BeginTransaction();

            using var recCmd = _db.CreateCommand();
            recCmd.Transaction = tx;
            recCmd.CommandText = "INSERT OR REPLACE INTO photo_records(file_name,world_id) VALUES($fn,$wid)";
            recCmd.Parameters.AddWithValue("$fn",  fileName);
            recCmd.Parameters.AddWithValue("$wid", worldId);
            recCmd.ExecuteNonQuery();

            using var delCmd = _db.CreateCommand();
            delCmd.Transaction = tx;
            delCmd.CommandText = "DELETE FROM photo_record_players WHERE file_name=$fn";
            delCmd.Parameters.AddWithValue("$fn", fileName);
            delCmd.ExecuteNonQuery();

            using var plCmd = _db.CreateCommand();
            plCmd.Transaction = tx;
            plCmd.CommandText = @"INSERT INTO photo_record_players
                (file_name,user_id,display_name,image) VALUES($fn,$uid,$dn,$img)";
            var pfn  = plCmd.Parameters.Add("$fn",  SqliteType.Text);
            var puid = plCmd.Parameters.Add("$uid", SqliteType.Text);
            var pdn  = plCmd.Parameters.Add("$dn",  SqliteType.Text);
            var pimg = plCmd.Parameters.Add("$img", SqliteType.Text);
            pfn.Value = fileName;
            foreach (var p in rec.Players)
            {
                puid.Value = p.UserId;
                pdn.Value  = p.DisplayName;
                pimg.Value = p.Image;
                plCmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch { }
    }

    public PhotoRecord? GetPhotoRecord(string fileName)
        => Photos.TryGetValue(fileName, out var rec) ? rec : null;

    public void Save() { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _db.Close(); } catch { }
        _db.Dispose();
    }

    // Used only during JSON migration
    private class PhotoPlayersStore_Legacy
    {
        public Dictionary<string, PhotoRecord>? Photos { get; set; }
    }
}
