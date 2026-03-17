using System.Collections.Concurrent;
using Photino.NET;
using VRCNext.Services;

namespace VRCNext;

public class CoreLibrary
{
    public VRChatApiService VrcApi { get; }
    public VRChatLogWatcher LogWatcher { get; }
    public TimelineService Timeline { get; }
    public ImageCacheService? ImgCache { get; set; }
    public AppSettings Settings { get; }
    public CacheHandler Cache { get; }
    public UserTimeTracker TimeTracker { get; }
    public WorldTimeTracker WorldTimeTracker { get; }
    public PhotoPlayersStore PhotoPlayersStore { get; }
    public WebhookService Webhook { get; }
    public FileWatcherService FileWatcher { get; }
    public Action<string, object?> SendToJS { get; }
    public Action<string>? AvtrdbSubmit { get; set; }

    public ConcurrentDictionary<string, string> PlayerImageCache { get; } = new();
    public ConcurrentDictionary<string, bool> PlayerAgeVerifiedCache { get; } = new();
    public ConcurrentDictionary<string, Newtonsoft.Json.Linq.JObject> PlayerProfileCache { get; } = new();
    public ConcurrentDictionary<string, string> WorldThumbCache { get; } = new();

    public Dictionary<string, (string name, string thumb)> VrWorldCache { get; } = new();

    public string CurrentVrcUserId { get; set; } = "";
    public string MyVrcStatus { get; set; } = "active";
    public DateTime DiscordJoinedAt { get; set; } = DateTime.MinValue;
    public int HttpPort { get; set; }

    public MemoryTrimService MemTrim { get; }
    public UpdateService UpdateService { get; }

    public PhotinoWindow? Window { get; set; }

    public Action? PushDiscordPresence { get; set; }
    public Func<bool>? IsVrcRunning { get; set; }
    public Func<bool>? IsSteamVrRunning { get; set; }
    public Func<string, string>? GetVirtualMediaUrl { get; set; }
    public Action<string>? LoadPage { get; set; }
    public Func<string, Task>? DispatchMessage { get; set; }

#if WINDOWS
    public VROverlayService? VrOverlay { get; set; }
#endif

    public CoreLibrary(
        VRChatApiService vrcApi,
        VRChatLogWatcher logWatcher,
        TimelineService timeline,
        AppSettings settings,
        CacheHandler cache,
        UserTimeTracker timeTracker,
        WorldTimeTracker worldTimeTracker,
        PhotoPlayersStore photoPlayersStore,
        WebhookService webhook,
        FileWatcherService fileWatcher,
        MemoryTrimService memTrim,
        UpdateService updateService,
        Action<string, object?> sendToJS)
    {
        VrcApi = vrcApi;
        LogWatcher = logWatcher;
        Timeline = timeline;
        Settings = settings;
        Cache = cache;
        TimeTracker = timeTracker;
        WorldTimeTracker = worldTimeTracker;
        PhotoPlayersStore = photoPlayersStore;
        Webhook = webhook;
        FileWatcher = fileWatcher;
        MemTrim = memTrim;
        UpdateService = updateService;
        SendToJS = sendToJS;
    }

    public string FixLocalUrl(string url)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("http://localhost:")) return url;
        var slash = url.IndexOf('/', "http://localhost:".Length);
        return slash < 0 ? url : $"http://localhost:{HttpPort}{url[slash..]}";
    }

    public string ResolveAndCache(string url, bool longTtl = false)
    {
        if (string.IsNullOrEmpty(url)) return url;
        if (url.StartsWith("http://localhost:")) return FixLocalUrl(url);
        if (ImgCache == null) return url;
        return longTtl ? ImgCache.GetWorld(url) : ImgCache.Get(url);
    }
}
