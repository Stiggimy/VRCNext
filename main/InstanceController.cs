using Newtonsoft.Json.Linq;
using VRCNext.Services;

namespace VRCNext;

// Owns all instance-related state, logic, and message handling.

public class InstanceController
{
    private readonly CoreLibrary _core;
    private readonly FriendsController _friends;

    // Instance State
    private string _cachedInstLocation  = "";
    private string _cachedInstWorldName = "";
    private string _cachedInstWorldThumb = "";
    private int    _cachedInstCapacity  = 0;
    private string _cachedInstType      = "";

    private readonly Dictionary<string, (string displayName, string image)> _cumulativeInstancePlayers = new();
    private readonly HashSet<string> _meetAgainThisInstance = new();
    private string? _pendingInstanceEventId;
    private System.Threading.Timer? _instanceSnapshotTimer;
    private bool _logWatcherBootstrapped;
    private string _lastTrackedWorldId = "";
    private readonly HashSet<string> _recentlyClosedLocs = new();

    // Public Accessors (for other domains)
    public string CachedInstLocation   => _cachedInstLocation;
    public string CachedInstWorldName  => _cachedInstWorldName;
    public string CachedInstWorldThumb => _cachedInstWorldThumb;
    public int    CachedInstCapacity   => _cachedInstCapacity;
    public string CachedInstType       => _cachedInstType;
    public Dictionary<string, (string displayName, string image)> CumulativeInstancePlayers => _cumulativeInstancePlayers;
    public string? PendingInstanceEventId { get => _pendingInstanceEventId; set => _pendingInstanceEventId = value; }
    public bool LogWatcherBootstrapped { get => _logWatcherBootstrapped; set => _logWatcherBootstrapped = value; }
    public string LastTrackedWorldId { get => _lastTrackedWorldId; set => _lastTrackedWorldId = value; }
    public HashSet<string> RecentlyClosedLocs => _recentlyClosedLocs;

    // Constructor

    public InstanceController(CoreLibrary core, FriendsController friends)
    {
        _core = core;
        _friends = friends;
    }

    // Static Helpers

    public static string ParseInstanceTypeFromLoc(string loc)
    {
        if (loc.Contains("~private(")) return loc.Contains("~canRequestInvite") ? "invite_plus" : "private";
        if (loc.Contains("~friends(")) return "friends";
        if (loc.Contains("~hidden("))  return "hidden";
        if (loc.Contains("~group("))
        {
            var gat = System.Text.RegularExpressions.Regex
                .Match(loc, @"groupAccessType\(([^)]+)\)").Groups[1].Value.ToLowerInvariant();
            if (gat == "public")  return "group-public";
            if (gat == "plus")    return "group-plus";
            return "group-members"; // covers "members" and unknown
        }
        return "public";
    }

    public static string ParseRegionFromLoc(string loc)
    {
        var m = System.Text.RegularExpressions.Regex.Match(loc, @"~region\(([^)]+)\)");
        return m.Success ? m.Groups[1].Value : "eu";
    }

    // Message Handler

    public async Task HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "vrcGetCurrentInstance":
                _ = GetCurrentInstanceAsync();
                break;

            case "vrcGetMyInstances":
                _ = Task.Run(async () =>
                {
                    var myId = _core.VrcApi.CurrentUserId ?? "";

                    // 1. Inject current location as candidate (LogWatcher is instant — same source as friends panel)
                    //    Ownership is validated by the loop below via API ownerId check.
                    //    This handles startup (already in-game) AND joining a new instance while VRCNext is running.
                    var logLoc = (_core.IsVrcRunning?.Invoke() == true) ? (_core.LogWatcher.CurrentLocation ?? "") : "";
                    if (string.IsNullOrEmpty(logLoc) || !logLoc.Contains(':')) logLoc = _cachedInstLocation;
                    if (!string.IsNullOrEmpty(logLoc) && logLoc.Contains(':')
                        && !_recentlyClosedLocs.Contains(logLoc)
                        && !_core.Settings.MyInstances.Contains(logLoc))
                    {
                        _core.Settings.MyInstances.Insert(0, logLoc);
                        // No save yet — loop validates ownership; removed via miDead if not owner
                    }

                    // 2. Verify all stored instances via API — remove dead ones, keep active
                    var miResults = new List<object>();
                    var miDead = new List<string>();
                    foreach (var instLoc in _core.Settings.MyInstances.ToList())
                    {
                        var inst = await _core.VrcApi.GetInstanceAsync(instLoc);
                        // Dead if API returns null OR if user is no longer listed as owner
                        if (inst == null) { miDead.Add(instLoc); continue; }
                        var apiOwnerId = inst["ownerId"]?.ToString() ?? "";
                        // Group instances are owned by the group (grp_...) — check creatorId instead
                        var effectiveOwner = apiOwnerId.StartsWith("grp_")
                            ? (inst["creatorId"]?.ToString() ?? apiOwnerId)
                            : apiOwnerId;
                        if (!string.IsNullOrEmpty(myId) && effectiveOwner != myId) { miDead.Add(instLoc); continue; }
                        var iType = ParseInstanceTypeFromLoc(instLoc);
                        if (iType == "private" && inst["canRequestInvite"]?.Value<bool>() == true) iType = "invite_plus";
                        miResults.Add(new
                        {
                            location   = instLoc,
                            worldId    = inst["worldId"]?.ToString() ?? "",
                            worldName  = inst["world"]?["name"]?.ToString() ?? "",
                            worldThumb = inst["world"]?["imageUrl"]?.ToString() ?? inst["world"]?["thumbnailImageUrl"]?.ToString() ?? "",
                            instanceType = iType,
                            userCount  = inst["userCount"]?.Value<int>() ?? 0,
                            capacity   = inst["capacity"]?.Value<int>() ?? 0,
                            region     = inst["region"]?.ToString() ?? ParseRegionFromLoc(instLoc),
                        });
                    }
                    foreach (var d in miDead) _core.Settings.MyInstances.Remove(d);
                    if (miDead.Count > 0) _core.Settings.Save();
                    _core.SendToJS("myInstances", miResults);
                });
                break;

            case "vrcRemoveMyInstance":
                _ = Task.Run(async () =>
                {
                    var rmInstLoc = msg["location"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(rmInstLoc)) return;
                    // Close instance via VRChat API (DELETE)
                    await _core.VrcApi.CloseInstanceAsync(rmInstLoc);
                    _core.Settings.MyInstances.Remove(rmInstLoc);
                    _core.Settings.Save();
                });
                break;

            case "vrcCreateInstance":
                var ciWorldId = msg["worldId"]?.ToString() ?? "";
                var ciType = msg["type"]?.ToString() ?? "public";
                var ciRegion = msg["region"]?.ToString() ?? "eu";
                var ciAndJoin = msg["andJoin"]?.ToObject<bool>() ?? true;
                if (!string.IsNullOrEmpty(ciWorldId))
                {
                    _ = Task.Run(async () =>
                    {
                        var location = _core.VrcApi.BuildInstanceLocation(ciWorldId, ciType, ciRegion);
                        bool ok;
                        string message;
                        if (ciAndJoin)
                        {
                            ok = await _core.VrcApi.InviteSelfAsync(location);
                            message = ok ? "Instance created! Self-invite sent." : "Failed to create instance.";
                        }
                        else
                        {
                            ok = true;
                            message = "Instance created.";
                        }
                        if (ok)
                        {
                            _core.Settings.MyInstances.Remove(location);
                            _core.Settings.MyInstances.Insert(0, location);
                            _core.Settings.Save();
                        }
                        Invoke(() =>
                        {
                            _core.SendToJS("vrcActionResult", new
                            {
                                action = "createInstance",
                                success = ok,
                                message,
                                location
                            });
                        });
                    });
                }
                break;

            case "vrcResolveWorlds":
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var worldIds = msg["worldIds"]?.ToObject<List<string>>() ?? new();
                        var tasks = worldIds.Select(async wid =>
                        {
                            try
                            {
                                var world = await _core.VrcApi.GetWorldAsync(wid);
                                if (world == null) return (wid, null as object);
                                return (wid, (object)new
                                {
                                    name             = world["name"]?.ToString() ?? "",
                                    thumbnailImageUrl = world["thumbnailImageUrl"]?.ToString() ?? "",
                                    imageUrl         = world["imageUrl"]?.ToString() ?? ""
                                });
                            }
                            catch { return (wid, null as object); }
                        });
                        var results = await Task.WhenAll(tasks);
                        var dict = results
                            .Where(r => r.Item2 != null)
                            .ToDictionary(r => r.wid, r => r.Item2!);
                        if (dict.Count > 0)
                            _core.SendToJS("vrcWorldsResolved", dict);
                    }
                    catch (Exception ex)
                    {
                        _core.SendToJS("log", new { msg = $"World resolve error: {ex.Message}", color = "err" });
                    }
                });
                break;

            case "vrcGetOnlineCount":
                _ = Task.Run(async () =>
                {
                    var count = await _core.VrcApi.GetOnlineCountAsync();
                    if (count > 0)
                        Invoke(() => _core.SendToJS("vrcOnlineCount", new { count }));
                });
                break;

            case "vrcGetTimeSpent":
            {
                var tsMyId = _core.VrcApi.CurrentUserId ?? "";
                _ = Task.Run(() =>
                {
                    var stats = _core.Timeline.GetTimeSpentStats(tsMyId);

                    // Build name/image/meets lookup from timeline (covers historical data)
                    var tlPersons = stats.Persons.ToDictionary(p => p.UserId);
                    var tlWorlds  = stats.Worlds.ToDictionary(w => w.WorldId);

                    // PERSONS: start from ALL engine users so nobody is missed.
                    var vrcRunning = _core.IsVrcRunning?.Invoke() ?? false;
                    var logPlayerIds = vrcRunning
                        ? new HashSet<string>(_core.LogWatcher.GetCurrentPlayers()
                            .Where(p => !string.IsNullOrEmpty(p.UserId)).Select(p => p.UserId))
                        : new HashSet<string>();

                    var personList = _core.TimeEngine.Users
                        .Where(kv => kv.Key != tsMyId)
                        .Select(kv =>
                        {
                            var isCoPresent = logPlayerIds.Contains(kv.Key);
                            // GetUserStats includes live session delta when co-present
                            var (effectiveSec, _) = _core.TimeEngine.GetUserStats(kv.Key, isCoPresent);
                            if (effectiveSec <= 0) return default; // skip zero-time entries

                            tlPersons.TryGetValue(kv.Key, out var tl);
                            // Priority: live caches (correct) -> UserRecord -> timeline -> friendStore
                            var name  = !string.IsNullOrEmpty(kv.Value.DisplayName) ? kv.Value.DisplayName
                                      : tl?.DisplayName ?? "";
                            var image = _friends.ResolvePlayerImage(kv.Key, null);
                            if (string.IsNullOrEmpty(image))
                            {
                                image = !string.IsNullOrEmpty(kv.Value.Image) ? kv.Value.Image
                                      : tl?.Image ?? "";
                            }
                            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(image))
                            {
                                var fj = _friends.GetStoreValue(kv.Key);
                                if (fj != null)
                                {
                                    if (string.IsNullOrEmpty(name))  name  = fj["displayName"]?.ToString() ?? "";
                                    if (string.IsNullOrEmpty(image)) image = VRChatApiService.GetUserImage(fj);
                                }
                            }
                            if (string.IsNullOrEmpty(name)) return default; // truly unknown, skip
                            return (UserId: kv.Key, DisplayName: name, Image: image,
                                    Seconds: effectiveSec, Meets: tl?.Meets ?? 0);
                        })
                        .Where(p => p.UserId != null)
                        .OrderByDescending(p => p.Seconds)
                        .Take(200)
                        .ToList();

                    // WORLDS: GetWorldStats includes live session delta automatically.
                    var worldList = _core.TimeEngine.Worlds
                        .Select(kv =>
                        {
                            tlWorlds.TryGetValue(kv.Key, out var tl);
                            var (wSec, wVisits, _) = _core.TimeEngine.GetWorldStats(kv.Key);
                            var name  = !string.IsNullOrEmpty(kv.Value.WorldName)  ? kv.Value.WorldName  : (tl?.WorldName  ?? "");
                            var thumb = !string.IsNullOrEmpty(kv.Value.WorldThumb) ? kv.Value.WorldThumb : (tl?.WorldThumb ?? "");
                            var visits = wVisits > 0 ? wVisits : (tl?.Visits ?? 0);
                            return (WorldId: kv.Key, WorldName: name, WorldThumb: thumb,
                                    Seconds: wSec, Visits: visits);
                        })
                        .Where(w => !string.IsNullOrEmpty(w.WorldName)) // skip worlds with no name yet
                        .OrderByDescending(w => w.Seconds)
                        .Take(200)
                        .ToList();

                    Invoke(() => _core.SendToJS("vrcTimeSpentData", new
                    {
                        totalSeconds = stats.TotalSeconds,
                        worlds = worldList.Select(w => new
                        {
                            worldId    = w.WorldId,
                            worldName  = w.WorldName,
                            worldThumb = w.WorldThumb,
                            seconds    = w.Seconds,
                            visits     = w.Visits,
                        }),
                        persons = personList.Select(p => new
                        {
                            userId      = p.UserId,
                            displayName = p.DisplayName,
                            image       = p.Image,
                            seconds     = p.Seconds,
                            meets       = p.Meets,
                        }),
                    }));
                });
                break;
            }
        }
    }

    // Instance Methods

    public Task GetCurrentInstanceAsync() => Task.Run(async () =>
    {
        try
        {
            // Step 1: Location from log watcher — no API call. If VRChat not running, treat as offline.
            var loc = (_core.IsVrcRunning?.Invoke() == true) ? _core.LogWatcher.CurrentLocation : null;
            if (string.IsNullOrEmpty(loc) || loc == "offline" || loc == "private" || loc == "traveling")
            {
                _cachedInstLocation   = "";
                _cachedInstWorldName  = "";
                _cachedInstWorldThumb = "";
                _cachedInstCapacity   = 0;
                _cachedInstType       = "";
                Invoke(() =>
                {
                    _core.PushDiscordPresence?.Invoke();
                    _core.SendToJS("vrcCurrentInstance", new { empty = true });
                });
                return;
            }

            var parsed = VRChatApiService.ParseLocation(loc);

            // Only fetch world info from API once per instance (when location changes or cache is empty).
            // Player count comes from LogWatcher — no need to poll the instance endpoint repeatedly.
            string worldName, worldThumb;
            int worldCapacity;

            bool locationChanged = _cachedInstLocation != loc || string.IsNullOrEmpty(_cachedInstWorldName);
            if (locationChanged)
            {
                var inst = await _core.VrcApi.GetInstanceAsync(loc);
                worldName     = inst?["world"]?["name"]?.ToString() ?? "";
                worldThumb    = inst?["world"]?["imageUrl"]?.ToString() ?? inst?["world"]?["thumbnailImageUrl"]?.ToString() ?? "";
                worldCapacity = inst?["world"]?["capacity"]?.Value<int>() ?? inst?["capacity"]?.Value<int>() ?? 0;

                if (string.IsNullOrEmpty(worldName) && !string.IsNullOrEmpty(parsed.worldId))
                {
                    var world = await _core.VrcApi.GetWorldAsync(parsed.worldId);
                    if (world != null)
                    {
                        worldName     = world["name"]?.ToString() ?? "";
                        worldThumb    = world["imageUrl"]?.ToString() ?? world["thumbnailImageUrl"]?.ToString() ?? "";
                        worldCapacity = world["capacity"]?.Value<int>() ?? 0;
                    }
                }
                if (string.IsNullOrEmpty(worldName)) worldName = parsed.worldId;
            }
            else
            {
                worldName     = _cachedInstWorldName;
                worldThumb    = _cachedInstWorldThumb;
                worldCapacity = _cachedInstCapacity;
            }

            // Step 4: Build player list. Prefer LogWatcher (reads VRChat logs),
            // fall back to API users array
            var users = new List<object>();
            string playerSource = "none";

            Invoke(() => _core.SendToJS("log", new { msg = $"[LOG] {_core.LogWatcher.GetDiagnostics()}", color = "sec" }));

            // Source A: VRChat log file (most complete, shows ALL players)
            var logPlayers = _core.LogWatcher.GetCurrentPlayers();
            if (logPlayers.Count > 0)
            {
                playerSource = "logfile";

                // Only players with a real usr_ ID can be looked up via the VRChat API.
                // Old-format IDs (e.g. "GGQdjFCSD4") are kept for display but not fetched.
                var playersWithId = logPlayers.Where(p => !string.IsNullOrEmpty(p.UserId) && p.UserId.StartsWith("usr_")).ToList();
                var userProfiles  = new Dictionary<string, JObject>();

                // Skip only if we have a previously cached full profile (tags, platform, ageVerified etc.)
                var needFetch = playersWithId.Where(p =>
                    !_core.PlayerProfileCache.ContainsKey(p.UserId)
                ).ToList();

                if (needFetch.Count > 0)
                {
                    var semaphore = new SemaphoreSlim(5);
                    var tasks = needFetch.Select(async p =>
                    {
                        await semaphore.WaitAsync();
                        try
                        {
                            var profile = await _core.VrcApi.GetUserAsync(p.UserId);
                            if (profile != null)
                            {
                                var img = VRChatApiService.GetUserImage(profile);
                                if (!string.IsNullOrEmpty(img))
                                {
                                    _core.PlayerImageCache[p.UserId] = img;
                                    _core.Timeline.SetUserImage(p.UserId, img); // persist across restarts
                                }
                                _core.PlayerAgeVerifiedCache[p.UserId] = profile["ageVerified"]?.Value<bool>() ?? false;
                                _core.PlayerProfileCache[p.UserId] = profile;
                                lock (userProfiles)
                                    userProfiles[p.UserId] = profile;
                            }
                        }
                        finally { semaphore.Release(); }
                    });
                    await Task.WhenAll(tasks);

                }

                // Load previously cached profiles for players that were skipped
                foreach (var p in playersWithId)
                {
                    if (!userProfiles.ContainsKey(p.UserId) && _core.PlayerProfileCache.TryGetValue(p.UserId, out var cached))
                        userProfiles[p.UserId] = cached;
                }

                Invoke(() => _core.SendToJS("log", new { msg = $"[LOG] Profiles: {needFetch.Count} fetched, {playersWithId.Count - needFetch.Count} cached", color = "sec" }));

                foreach (var p in logPlayers)
                {
                    var img               = "";
                    var status            = "";
                    var statusDescription = "";
                    JObject? profObj = null;
                    if (!string.IsNullOrEmpty(p.UserId))
                    {
                        if (userProfiles.TryGetValue(p.UserId, out var prof))
                        {
                            profObj           = prof;
                            img               = VRChatApiService.GetUserImage(prof);
                            status            = prof["status"]?.ToString() ?? "";
                            statusDescription = prof["statusDescription"]?.ToString() ?? "";
                        }
                        else if (_friends.TryGetNameImage(p.UserId, out var fi) && !string.IsNullOrEmpty(fi.image))
                        {
                            img = fi.image;
                        }
                        else if (_core.PlayerImageCache.TryGetValue(p.UserId, out var ci) && !string.IsNullOrEmpty(ci))
                        {
                            img = ci;
                        }
                    }
                    users.Add(new {
                        id                = p.UserId,
                        displayName       = p.DisplayName,
                        image             = img,
                        status,
                        statusDescription,
                        joinedAt          = new DateTimeOffset(p.JoinedAt).ToUnixTimeMilliseconds(),
                        tags              = profObj?["tags"]?.ToObject<List<string>>() ?? new List<string>(),
                        ageVerified       = profObj?["ageVerified"]?.Value<bool>() ?? false,
                        platform          = profObj?["last_platform"]?.ToString() ?? "",
                    });
                }
            }

            var nUsers = users.Count;

            _cachedInstLocation   = loc;
            _cachedInstWorldName  = worldName;
            _cachedInstWorldThumb = worldThumb;
            _cachedInstCapacity   = worldCapacity;
            _cachedInstType       = parsed.instanceType;

            Invoke(() =>
            {
                _core.PushDiscordPresence?.Invoke();
                _core.SendToJS("log", new { msg = $"Instance: {worldName} — {nUsers} total, {users.Count} tracked ({playerSource})", color = "ok" });
                _core.SendToJS("vrcCurrentInstance", new {
                    location = loc, worldId = parsed.worldId,
                    worldName, worldThumb,
                    instanceType = parsed.instanceType,
                    nUsers, capacity = worldCapacity, users, playerSource,
                });
            });
        }
        catch (Exception ex)
        {
            Invoke(() =>
            {
                _core.SendToJS("log", new { msg = $"\u274c Instance error: {ex.Message}", color = "err" });
                _core.SendToJS("vrcCurrentInstance", new { error = ex.Message });
            });
        }
    });

    // Push cached instance data + live LogWatcher players to JS (no REST)
    public void PushCurrentInstanceFromCache()
    {
        if (string.IsNullOrEmpty(_cachedInstLocation)) return;
        var parsed = VRChatApiService.ParseLocation(_cachedInstLocation);
        var logPlayers = _core.LogWatcher.GetCurrentPlayers();

        // Player leave time tracking is handled by OnPlayerLeft in AuthController event wiring
        var users = logPlayers.Select(p =>
        {
            string img = "";
            if (_friends.TryGetNameImage(p.UserId ?? "", out var fi) && !string.IsNullOrEmpty(fi.image))
                img = fi.image;
            else if (_core.PlayerImageCache.TryGetValue(p.UserId ?? "", out var ci) && !string.IsNullOrEmpty(ci))
                img = ci;

            var av = !string.IsNullOrEmpty(p.UserId) && _core.PlayerAgeVerifiedCache.TryGetValue(p.UserId, out var cached) && cached;

            string status = "", statusDescription = "", platform = "";
            var tags = new List<string>();
            if (!string.IsNullOrEmpty(p.UserId) && _core.PlayerProfileCache.TryGetValue(p.UserId, out var prof))
            {
                status            = prof["status"]?.ToString() ?? "";
                statusDescription = prof["statusDescription"]?.ToString() ?? "";
                platform          = prof["last_platform"]?.ToString() ?? "";
                tags              = prof["tags"]?.ToObject<List<string>>() ?? new List<string>();
                if (string.IsNullOrEmpty(img))
                    img = VRChatApiService.GetUserImage(prof);
            }

            return (object)new {
                id                = p.UserId,
                displayName       = p.DisplayName,
                image             = img,
                status,
                statusDescription,
                joinedAt          = new DateTimeOffset(p.JoinedAt).ToUnixTimeMilliseconds(),
                tags,
                ageVerified       = av,
                platform,
            };
        }).ToList();

        _core.SendToJS("vrcCurrentInstance", new {
            location     = _cachedInstLocation,
            worldId      = parsed.worldId,
            worldName    = _cachedInstWorldName,
            worldThumb   = _cachedInstWorldThumb,
            instanceType = _cachedInstType,
            nUsers       = logPlayers.Count,
            capacity     = _cachedInstCapacity,
            users,
            playerSource = "logfile",
        });
    }

    // Timeline - LogWatcher event handlers (run on UI thread)

    public void HandleWorldChangedOnUiThread(string worldId, string location)
    {
        // Finalise previous instance event
        if (_pendingInstanceEventId != null)
        {
            var finalPlayers = _cumulativeInstancePlayers.Select(kv => new TimelineService.PlayerSnap
            {
                UserId      = kv.Key,
                DisplayName = kv.Value.displayName,
                Image       = _friends.ResolvePlayerImage(kv.Key, kv.Value.image)
            }).ToList();
            var prevId = _pendingInstanceEventId;
            _core.Timeline.UpdateEvent(prevId, ev => ev.Players = finalPlayers);
            var finalEv = _core.Timeline.GetEvents().FirstOrDefault(e => e.Id == prevId);
            if (finalEv != null) _core.SendToJS("timelineEvent", BuildTimelinePayload(finalEv));
        }

        _cumulativeInstancePlayers.Clear();
        _meetAgainThisInstance.Clear();
        _instanceSnapshotTimer?.Dispose();
        _instanceSnapshotTimer = null;

        // Create new instance_join timeline event (world name resolved asynchronously)
        var evId  = Guid.NewGuid().ToString("N")[..8];
        _pendingInstanceEventId = evId;

        var instEv = new TimelineService.TimelineEvent
        {
            Id        = evId,
            Type      = "instance_join",
            Timestamp = DateTime.UtcNow.ToString("o"),
            WorldId   = worldId,
            Location  = location
        };
        _core.Timeline.AddEvent(instEv);
        _core.SendToJS("timelineEvent", BuildTimelinePayload(instEv));
        _core.SendToJS("log", new { msg = $"[TIMELINE] Instance join: {worldId}", color = "sec" });

        // Reset Discord join timer for the new instance
        _core.DiscordJoinedAt = DateTime.Now;

        // Unified time engine: start world + player tracking for new instance
        _core.TimeEngine.OnWorldJoined(worldId, location);
        _lastTrackedWorldId = worldId;

        // Immediately refresh instance panel so sidebar doesn't wait for the 60s poll
        _core.SendToJS("vrcWorldJoined", new { worldId });

        // Auto-detect owned instances — add as candidate immediately (no API, just string check),
        // then validate via ownerId API inside HandleMessage so the dashboard updates without any manual action.
        var miCandId = _core.VrcApi.CurrentUserId ?? "";
        if (!string.IsNullOrEmpty(miCandId) && !string.IsNullOrEmpty(location)
            && location.Contains(miCandId)
            && !_recentlyClosedLocs.Contains(location)
            && !_core.Settings.MyInstances.Contains(location))
        {
            _core.Settings.MyInstances.Insert(0, location);
            while (_core.Settings.MyInstances.Count > 4)
                _core.Settings.MyInstances.RemoveAt(_core.Settings.MyInstances.Count - 1);
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500); // let VRChat register the instance before we query it
            await HandleMessage("vrcGetMyInstances", new JObject());
        });

        // After 15 s: snapshot players + resolve world name
        _instanceSnapshotTimer = new System.Threading.Timer(_ =>
        {
            try
            {
                Invoke(async () =>
                {
                    try
                    {
                        // Refresh any images that have since been fetched (e.g. via requestInstanceInfo)
                        var snap = _cumulativeInstancePlayers.Select(kv => new TimelineService.PlayerSnap
                        {
                            UserId      = kv.Key,
                            DisplayName = kv.Value.displayName,
                            Image       = _friends.ResolvePlayerImage(kv.Key, kv.Value.image)
                        }).ToList();

                        // Resolve world name: DB cache first, API fallback
                        var (wName, wThumb) = ResolveWorldInfoFromCache(worldId);
                        if (string.IsNullOrEmpty(wName) && worldId.StartsWith("wrld_") && _core.VrcApi.IsLoggedIn)
                        {
                            var world = await _core.VrcApi.GetWorldAsync(worldId);
                            if (world != null)
                            {
                                wName  = world["name"]?.ToString() ?? "";
                                wThumb = world["thumbnailImageUrl"]?.ToString() ?? "";
                            }
                        }

                        _core.Timeline.UpdateEvent(evId, ev =>
                        {
                            ev.WorldName  = wName;
                            ev.WorldThumb = wThumb;
                            ev.Players    = snap;
                        });

                        // Store name/thumb in TimeEngine so future lookups skip the API
                        if (!string.IsNullOrEmpty(wName))
                        {
                            _core.TimeEngine.UpdateWorldInfo(worldId, wName, wThumb);
                            // Backfill ALL events (entire DB) with same WorldId that are missing WorldName
                            BackfillWorldName(worldId, wName, wThumb);
                        }

                        var updated = _core.Timeline.GetEvents().FirstOrDefault(e => e.Id == evId);
                        if (updated != null) _core.SendToJS("timelineEvent", BuildTimelinePayload(updated));
                    }
                    catch { }
                });
            }
            catch { }
        }, null, 15_000, System.Threading.Timeout.Infinite);
    }

    public void HandlePlayerJoinedOnUiThread(string userId, string displayName)
    {
        // Skip events for the local player; VRChat logs OnPlayerJoined for self too
        if (!string.IsNullOrEmpty(_core.CurrentVrcUserId) && userId == _core.CurrentVrcUserId) return;

        // Accumulate into instance player history
        if (!string.IsNullOrEmpty(userId) && !_cumulativeInstancePlayers.ContainsKey(userId))
        {
            var img = _friends.TryGetNameImage(userId, out var fi) ? fi.image : "";
            _cumulativeInstancePlayers[userId] = (displayName, img);
            // Store name so this player appears in Time Spent list even when not a friend
            _core.TimeEngine.UpdateUserInfo(userId, displayName, img);

            // Start time session for this player using the LogWatcher join timestamp.
            // This ensures Time Together uses the exact same start time as Instance Info.
            var logPlayer = _core.LogWatcher.GetCurrentPlayers().FirstOrDefault(p => p.UserId == userId);
            var joinedAtUtc = logPlayer != null ? logPlayer.JoinedAt.ToUniversalTime() : DateTime.UtcNow;
            _core.TimeEngine.OnPlayerJoined(userId, joinedAtUtc);

            // Live-update the instance_join timeline event so the UI shows players immediately
            if (_pendingInstanceEventId != null)
            {
                var evId = _pendingInstanceEventId;
                var snap = _cumulativeInstancePlayers.Select(kv => new TimelineService.PlayerSnap
                {
                    UserId      = kv.Key,
                    DisplayName = kv.Value.displayName,
                    Image       = _friends.ResolvePlayerImage(kv.Key, kv.Value.image)
                }).ToList();
                _core.Timeline.UpdateEvent(evId, ev => ev.Players = snap);
                var updated = _core.Timeline.GetEvents().FirstOrDefault(e => e.Id == evId);
                if (updated != null) _core.SendToJS("timelineEvent", BuildTimelinePayload(updated));
            }
        }

        // First-meet detection, only after known-users set is seeded
        if (!string.IsNullOrEmpty(userId) && _core.Timeline.KnownUsersSeeded && !_core.Timeline.IsKnownUser(userId))
        {
            _core.Timeline.AddKnownUser(userId);
            var img = _friends.TryGetNameImage(userId, out var fi) ? fi.image : "";
            var fmWorldId = _core.LogWatcher.CurrentWorldId ?? "";
            var (fmWorldName, fmWorldThumb) = ResolveWorldInfoFromCache(fmWorldId);
            var meetEv = new TimelineService.TimelineEvent
            {
                Type       = "first_meet",
                Timestamp  = DateTime.UtcNow.ToString("o"),
                UserId     = userId,
                UserName   = displayName,
                UserImage  = img,
                WorldId    = fmWorldId,
                WorldName  = fmWorldName,
                WorldThumb = fmWorldThumb,
                Location   = _core.LogWatcher.CurrentLocation ?? ""
            };
            _core.Timeline.AddEvent(meetEv);
            _core.SendToJS("timelineEvent", BuildTimelinePayload(meetEv));
            _core.SendToJS("log", new { msg = $"[TIMELINE] First meet: {displayName}", color = "sec" });

            // If world name still unknown, async-fetch and backfill ALL events with same WorldId
            if (string.IsNullOrEmpty(fmWorldName) && fmWorldId.StartsWith("wrld_") && _core.VrcApi.IsLoggedIn)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var world = await _core.VrcApi.GetWorldAsync(fmWorldId);
                        if (world == null) return;
                        var fn = world["name"]?.ToString() ?? "";
                        var ft = world["thumbnailImageUrl"]?.ToString() ?? "";
                        if (string.IsNullOrEmpty(fn)) return;
                        _core.TimeEngine.UpdateWorldInfo(fmWorldId, fn, ft);
                        Invoke(() => BackfillWorldName(fmWorldId, fn, ft));
                    }
                    catch { }
                });
            }

            // If no image yet, fetch async and update the event
            if (string.IsNullOrEmpty(img) && _core.VrcApi.IsLoggedIn)
            {
                var evId = meetEv.Id;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var profile = await _core.VrcApi.GetUserAsync(userId);
                        if (profile == null) return;
                        var fetchedImg = VRChatApiService.GetUserImage(profile);
                        if (string.IsNullOrEmpty(fetchedImg)) return;
                        _core.PlayerImageCache[userId]   = fetchedImg;
                        _core.Timeline.SetUserImage(userId, fetchedImg); // persist across restarts
                        _core.PlayerProfileCache[userId]   = profile;
                        _core.PlayerAgeVerifiedCache[userId] = profile["ageVerified"]?.Value<bool>() ?? false;
                        if (_cumulativeInstancePlayers.TryGetValue(userId, out var ex2) && string.IsNullOrEmpty(ex2.image))
                            _cumulativeInstancePlayers[userId] = (ex2.displayName, fetchedImg);
                        _core.Timeline.UpdateEvent(evId, ev => ev.UserImage = fetchedImg);
                        var updated = _core.Timeline.GetEvents().FirstOrDefault(e => e.Id == evId);
                        if (updated != null) Invoke(() => _core.SendToJS("timelineEvent", BuildTimelinePayload(updated)));
                    }
                    catch { }
                });
            }
        }
        else if (!string.IsNullOrEmpty(userId))
        {
            _core.Timeline.AddKnownUser(userId);

            // Meet Again: known user, not yet seen in this instance
            if (_core.Timeline.KnownUsersSeeded && !_meetAgainThisInstance.Contains(userId))
            {
                _meetAgainThisInstance.Add(userId);
                var img = _friends.TryGetNameImage(userId, out var fi2) ? fi2.image : "";
                var maWorldId = _core.LogWatcher.CurrentWorldId ?? "";
                var (maWorldName, maWorldThumb) = ResolveWorldInfoFromCache(maWorldId);
                var meetAgainEv = new TimelineService.TimelineEvent
                {
                    Type       = "meet_again",
                    Timestamp  = DateTime.UtcNow.ToString("o"),
                    UserId     = userId,
                    UserName   = displayName,
                    UserImage  = img,
                    WorldId    = maWorldId,
                    WorldName  = maWorldName,
                    WorldThumb = maWorldThumb,
                    Location   = _core.LogWatcher.CurrentLocation ?? ""
                };
                _core.Timeline.AddEvent(meetAgainEv);
                _core.SendToJS("timelineEvent", BuildTimelinePayload(meetAgainEv));

                // If world name still unknown, async-fetch and backfill ALL events with same WorldId
                if (string.IsNullOrEmpty(maWorldName) && maWorldId.StartsWith("wrld_") && _core.VrcApi.IsLoggedIn)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var world = await _core.VrcApi.GetWorldAsync(maWorldId);
                            if (world == null) return;
                            var mn = world["name"]?.ToString() ?? "";
                            var mt = world["thumbnailImageUrl"]?.ToString() ?? "";
                            if (string.IsNullOrEmpty(mn)) return;
                            _core.TimeEngine.UpdateWorldInfo(maWorldId, mn, mt);
                            Invoke(() => BackfillWorldName(maWorldId, mn, mt));
                        }
                        catch { }
                    });
                }

                // Async-fetch image if missing
                if (string.IsNullOrEmpty(img) && _core.VrcApi.IsLoggedIn)
                {
                    var maEvId = meetAgainEv.Id;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var profile = await _core.VrcApi.GetUserAsync(userId);
                            if (profile == null) return;
                            var fetchedImg = VRChatApiService.GetUserImage(profile);
                            if (string.IsNullOrEmpty(fetchedImg)) return;
                            _core.PlayerImageCache[userId]   = fetchedImg;
                            _core.Timeline.SetUserImage(userId, fetchedImg); // persist across restarts
                            _core.PlayerProfileCache[userId]   = profile;
                            _core.PlayerAgeVerifiedCache[userId] = profile["ageVerified"]?.Value<bool>() ?? false;
                            if (_cumulativeInstancePlayers.TryGetValue(userId, out var ex3) && string.IsNullOrEmpty(ex3.image))
                                _cumulativeInstancePlayers[userId] = (ex3.displayName, fetchedImg);
                            _core.Timeline.UpdateEvent(maEvId, ev => ev.UserImage = fetchedImg);
                            var updated = _core.Timeline.GetEvents().FirstOrDefault(e => e.Id == maEvId);
                            if (updated != null) Invoke(() => _core.SendToJS("timelineEvent", BuildTimelinePayload(updated)));
                        }
                        catch { }
                    });
                }
            }
        }

        // Instantly push updated player list to JS (no REST call needed)
        PushCurrentInstanceFromCache();

        // If we don't have a cached profile for this player yet, fetch it async so the
        // instance panel and modal get enriched data (image, status, platform, etc.)
        if (!string.IsNullOrEmpty(userId) && userId.StartsWith("usr_")
            && !_core.PlayerProfileCache.ContainsKey(userId) && _core.VrcApi.IsLoggedIn)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var profile = await _core.VrcApi.GetUserAsync(userId);
                    if (profile == null) return;
                    var img = VRChatApiService.GetUserImage(profile);
                    if (!string.IsNullOrEmpty(img))
                    {
                        _core.PlayerImageCache[userId] = img;
                        _core.Timeline.SetUserImage(userId, img); // persist across restarts
                    }
                    _core.PlayerAgeVerifiedCache[userId] = profile["ageVerified"]?.Value<bool>() ?? false;
                    _core.PlayerProfileCache[userId] = profile;

                    // Also enrich the cumulative instance player record with the resolved image
                    if (_cumulativeInstancePlayers.TryGetValue(userId, out var existing) && string.IsNullOrEmpty(existing.image))
                        _cumulativeInstancePlayers[userId] = (existing.displayName, img);

                    Invoke(() =>
                    {
                        PushCurrentInstanceFromCache();

                        // Immediately persist the updated image to the pending instance_join
                        // timeline event — don't wait for the 15 s snapshot, so images are
                        // written to DB even if the snapshot timer fires before all fetches finish.
                        if (_pendingInstanceEventId != null)
                        {
                            var evId = _pendingInstanceEventId;
                            var snap = _cumulativeInstancePlayers.Select(kv => new TimelineService.PlayerSnap
                            {
                                UserId      = kv.Key,
                                DisplayName = kv.Value.displayName,
                                Image       = _friends.ResolvePlayerImage(kv.Key, kv.Value.image)
                            }).ToList();
                            _core.Timeline.UpdateEvent(evId, ev => ev.Players = snap);
                        }
                    });
                }
                catch { }
            });
        }
    }

    // Timeline - helpers

    /// Resolve world name + thumb from in-memory caches (no API call).
    /// Priority: instance cache (only if worldId matches) → TimeEngine DB cache.
    private (string name, string thumb) ResolveWorldInfoFromCache(string worldId)
    {
        if (string.IsNullOrEmpty(worldId)) return ("", "");
        if (!string.IsNullOrEmpty(_cachedInstWorldName) && _cachedInstLocation.StartsWith(worldId))
            return (_cachedInstWorldName, _cachedInstWorldThumb);
        if (_core.TimeEngine.Worlds.TryGetValue(worldId, out var rec) && !string.IsNullOrEmpty(rec.WorldName))
            return (rec.WorldName, rec.WorldThumb);
        return ("", "");
    }

    /// Backfill all timeline events with matching worldId that have empty WorldName.
    /// Pushes updated events to JS automatically.
    private void BackfillWorldName(string worldId, string wName, string wThumb)
    {
        if (string.IsNullOrEmpty(worldId) || string.IsNullOrEmpty(wName)) return;
        var toFix = _core.Timeline.GetEvents()
            .Where(e => e.WorldId == worldId && string.IsNullOrEmpty(e.WorldName))
            .ToList();
        foreach (var ev in toFix)
        {
            _core.Timeline.UpdateEvent(ev.Id, e => { e.WorldName = wName; e.WorldThumb = wThumb; });
            Invoke(() => _core.SendToJS("timelineEvent", BuildTimelinePayload(
                _core.Timeline.GetEvents().FirstOrDefault(e => e.Id == ev.Id) ?? ev)));
        }
    }

    public object BuildTimelinePayload(TimelineService.TimelineEvent ev) => new
    {
        id          = ev.Id,
        type        = ev.Type,
        timestamp   = ev.Timestamp,
        worldId     = ev.WorldId,
        worldName   = ev.WorldName,
        worldThumb  = _core.ResolveAndCache(ev.WorldThumb, longTtl: true),
        location    = ev.Location,
        players     = ev.Players.Select(p => new { userId = p.UserId, displayName = p.DisplayName, image = _core.ResolveAndCache(_friends.ResolvePlayerImage(p.UserId, p.Image)) }).ToList(),
        photoPath   = ev.PhotoPath,
        photoUrl    = !string.IsNullOrEmpty(ev.PhotoPath) ? (_core.GetVirtualMediaUrl?.Invoke(ev.PhotoPath) ?? "") : _core.FixLocalUrl(ev.PhotoUrl),
        userId      = ev.UserId,
        userName    = ev.UserName,
        userImage   = _core.ResolveAndCache(_friends.ResolvePlayerImage(ev.UserId, ev.UserImage)),
        meetCount   = ev.Type == "meet_again" ? _core.Timeline.GetMeetAgainCount(ev.UserId) : 0,
        notifId     = ev.NotifId,
        notifType   = ev.NotifType,
        notifTitle  = ev.NotifTitle,
        senderName  = ev.SenderName,
        senderId    = ev.SenderId,
        senderImage = _core.ResolveAndCache(_friends.ResolvePlayerImage(ev.SenderId, ev.SenderImage)),
        message     = ev.Message,
    };

    // Photino compatibility shim
    private static void Invoke(Action action) => action();
    private static T Invoke<T>(Func<T> func) => func();
}
