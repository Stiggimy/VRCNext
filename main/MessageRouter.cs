using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NativeFileDialogSharp;
using VRCNext.Services;
using VRCNext.Services.Helpers;
using System.Diagnostics;

namespace VRCNext;

public partial class AppShell
{
    private readonly HashSet<string> _checkedAvatarIds = new();
    private readonly HashSet<string> _deletedAvatarIds = new();
    private readonly HashSet<string> _reportedToAvtrdb = new();
    private readonly List<string> _avtrdbReportQueue = new();
    private Timer? _avtrdbReportTimer;
    private readonly List<string> _avtrdbSubmitQueue = new();
    private readonly HashSet<string> _avtrdbSubmittedIds = new();
    private Timer? _avtrdbSubmitTimer;

    private static readonly string _deletedAvatarsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCNext", "deleted_avatars.json");

    private void LoadDeletedAvatarsCache()
    {
        try
        {
            if (File.Exists(_deletedAvatarsPath))
            {
                var json = File.ReadAllText(_deletedAvatarsPath);
                var ids = JsonConvert.DeserializeObject<List<string>>(json);
                if (ids != null)
                {
                    foreach (var id in ids)
                    {
                        _deletedAvatarIds.Add(id);
                        _checkedAvatarIds.Add(id);
                    }
                }
            }
        }
        catch { }
    }

    private void SaveDeletedAvatarsCache()
    {
        try
        {
            var dir = Path.GetDirectoryName(_deletedAvatarsPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_deletedAvatarsPath, JsonConvert.SerializeObject(_deletedAvatarIds.ToList()));
        }
        catch { }
    }

    private void QueueAvtrdbReport(List<string> ids)
    {
        int added = 0;
        lock (_avtrdbReportQueue)
        {
            foreach (var id in ids)
                if (_reportedToAvtrdb.Add(id)) { _avtrdbReportQueue.Add(id); added++; }
        }
        if (added > 0)
            Invoke(() => SendToJS("avtrdbCollecting", new { count = added }));
        // Debounce: wait 60s for more IDs to accumulate, then send in one batch
        _avtrdbReportTimer?.Dispose();
        _avtrdbReportTimer = new Timer(_ => _ = Task.Run(FlushAvtrdbReportQueue), null, 60_000, Timeout.Infinite);
    }

    private async Task FlushAvtrdbReportQueue()
    {
        List<string> batch;
        lock (_avtrdbReportQueue)
        {
            if (_avtrdbReportQueue.Count == 0) return;
            batch = new List<string>(_avtrdbReportQueue);
            _avtrdbReportQueue.Clear();
        }
        await SendToAvtrdb(batch, "deletion");
    }

    private void QueueAvtrdbSubmit(string avatarId)
    {
        if (!_settings.AvtrdbSubmitAvatars) return;
        lock (_avtrdbSubmitQueue)
        {
            if (!_avtrdbSubmittedIds.Add(avatarId)) return;
            _avtrdbSubmitQueue.Add(avatarId);
        }
        // Check if avatar already exists in avtrdb before submitting
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _vrcApi.SearchAvatarsAsync(avatarId, 1);
                bool exists = result.Count > 0 && result.Any(a =>
                    (a["vrc_id"]?.ToString() ?? a["id"]?.ToString() ?? "") == avatarId);
                if (exists)
                {
                    lock (_avtrdbSubmitQueue) _avtrdbSubmitQueue.Remove(avatarId);
                    return;
                }
                // Avatar not in avtrdb — keep in queue, debounce submit
                Invoke(() => SendToJS("avtrdbCollecting", new { count = 0, submit = 1 }));
                _avtrdbSubmitTimer?.Dispose();
                _avtrdbSubmitTimer = new Timer(_ => _ = Task.Run(FlushAvtrdbSubmitQueue), null, 60_000, Timeout.Infinite);
            }
            catch { }
        });
    }

    private async Task FlushAvtrdbSubmitQueue()
    {
        List<string> batch;
        lock (_avtrdbSubmitQueue)
        {
            if (_avtrdbSubmitQueue.Count == 0) return;
            batch = new List<string>(_avtrdbSubmitQueue);
            _avtrdbSubmitQueue.Clear();
        }
        await SendToAvtrdb(batch, "submit");
    }

    private async Task SendToAvtrdb(List<string> avatarIds, string reportType = "deletion")
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AppInfo.UserAgent);
            var payload = new { avatar_ids = avatarIds, attribution = _vrcApi.CurrentUserId ?? "" };
            var json = JsonConvert.SerializeObject(payload);
            var resp = await client.PostAsync("https://api.avtrdb.com/v3/avatar/ingest",
                new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadAsStringAsync();

            if (resp.IsSuccessStatusCode)
            {
                var r = JObject.Parse(body);
                var enqueued = r["avatars_enqueued"]?.Value<int>() ?? 0;
                var invalid = r["invalid_ids"]?.Value<int>() ?? 0;
                var ticket = r["ticket"]?.ToString() ?? "";
                Invoke(() =>
                {
                    var label = reportType == "submit" ? "Submitted" : "Reported";
                    SendToJS("log", new { msg = $"[avtrdb] {label} {avatarIds.Count} avatar(s) — {enqueued} enqueued, {invalid} invalid", color = "ok" });
                    SendToJS("avtrdbReport", new { count = avatarIds.Count, enqueued, invalid, ticket, type = reportType });
                });
            }
            else
                Invoke(() => SendToJS("log", new { msg = $"[avtrdb] Failed to report: {(int)resp.StatusCode} {body[..Math.Min(200, body.Length)]}", color = "err" }));
        }
        catch (Exception ex)
        {
            Invoke(() => SendToJS("log", new { msg = $"[avtrdb] Error: {ex.Message}", color = "err" }));
        }
    }

    // JS to C# message handler
    private async Task OnWebMessage(string rawMessage)
    {
        try
        {
            JObject msg;
            using (var _jr = new Newtonsoft.Json.JsonTextReader(new System.IO.StringReader(rawMessage)) { DateParseHandling = Newtonsoft.Json.DateParseHandling.None })
                msg = JObject.Load(_jr);
            var action = msg["action"]?.ToString() ?? "";

            switch (action)
            {
                case "ready":
                    // Signal platform to JS (hides Windows-only tabs on Linux)
                    SendToJS("setPlatform", new { isLinux = !OperatingSystem.IsWindows() });
                    _windowCtrl.InstallChrome();
                    // Debug: show what Load() did
                    if (AppSettings.LastLoadError != null)
                        SendToJS("log", new { msg = $"[LOAD ERROR] {AppSettings.LastLoadError}", color = "err" });
                    SendToJS("log", new { msg = $"[LOAD] {AppSettings.LoadDebugInfo}", color = "sec" });
                    SendToJS("log", new { msg = $"[STARTUP] Webhooks: {string.Join(", ", _settings.Webhooks.Select((w,i) => $"#{i+1} \"{w.Name}\" url={w.Url?.Length ?? 0}ch {(w.Enabled?"ON":"off")}"))}", color = "sec" });
                    _authCtrl.HandleReady();
                    break;

                // Setup / Auth / Settings — delegated to AuthController
                case "setupReady":
                case "setupDone":
                case "forceTrim":
                case "resetSetup":
                case "clearImgCache":
                case "clearFfcCache":
                case "forceFfcAll":
                case "setupSaveStartWithWindows":
                case "setupSaveVrcPath":
                case "setupSavePhotoDir":
                case "setupBrowsePhotoDir":
                    await _authCtrl.HandleMessage(action, msg);
                    break;

                // Window chrome (borderless)
                case "windowMinimize":
                case "windowMaximize":
                case "windowClose":
                case "windowDragStart":
                case "windowResizeStart":
                    _windowCtrl.HandleMessage(action, msg);
                    break;

                case "startRelay":
                case "stopRelay":
                    _relayCtrl.HandleMessage(action, msg);
                    break;

                case "getCursorFiles":
                    _windowCtrl.HandleMessage(action, msg);
                    break;

                case "saveSettings":
                    await _authCtrl.HandleMessage(action, msg);
                    break;

                case "saveCustomColors":
                    var themesArr = msg["themes"] as JArray;
                    if (themesArr != null)
                        _cache.Save(CacheHandler.KeyCustomColors, new { themes = themesArr });
                    break;

                case "addFolder":
                    {
                        var r = Dialog.FolderPicker();
                        if (r.IsOk) SendToJS("folderAdded", r.Path);
                    }
                    break;

                case "importVrcxSelect":
                case "importVrcxStart":
                    await _timelineCtrl.HandleMessage(action, msg);
                    break;

                // Photo/Library actions delegated to PhotosController
                case "deletePost":
                case "manualPost":
                case "dropFiles":
                case "scanLibrary":
                case "scanLibraryForce":
                case "loadLibraryPage":
                case "deleteLibraryFile":
                case "copyImageToClipboard":
                case "addFavorite":
                case "removeFavorite":
                    await _photos.HandleMessage(action, msg);
                    break;

                case "browseExe":
                case "browseDashBg":
                case "vrcLoadDashBg":
                case "vrcRandomDashBg":
                    await _authCtrl.HandleMessage(action, msg);
                    break;

                // Resolve world IDs to names/thumbnails for dashboard
                case "vrcResolveWorlds":
                    await _instance.HandleMessage(action, msg);
                    break;

                case "vrcGetRecentWorlds":
                    _ = Task.Run(async () =>
                    {
                        var worlds = await _vrcApi.GetRecentWorldsAsync();
                        Invoke(() => SendToJS("recentWorlds", new { worlds }));
                    });
                    break;

                case "vrcGetPopularWorlds":
                    _ = Task.Run(async () =>
                    {
                        var worlds = await _vrcApi.GetPopularWorldsAsync();
                        Invoke(() => SendToJS("popularWorlds", new { worlds }));
                    });
                    break;

                case "vrcGetActiveWorlds":
                    _ = Task.Run(async () =>
                    {
                        var worlds = await _vrcApi.GetActiveWorldsAsync();
                        Invoke(() => SendToJS("activeWorlds", new { worlds }));
                    });
                    break;

                case "fetchDiscoveryFeed":
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var url = msg["url"]?.ToString() ?? "";
                            using var http = new System.Net.Http.HttpClient();
                            http.DefaultRequestHeaders.Add("User-Agent", AppInfo.UserAgent);
                            var resp = await http.GetStringAsync(url);
                            Invoke(() => SendToJS("discoveryFeed", new { json = resp }));
                        }
                        catch (Exception ex)
                        {
                            Invoke(() => SendToJS("log", new { msg = $"Discovery fetch error: {ex.Message}", color = "err" }));
                        }
                    });
                    break;

                case "playVRChat":
                    _relayCtrl.HandleMessage(action, msg);
                    break;

                case "vrcLogin":
                case "vrc2FA":
                case "vrcLogout":
                    await _authCtrl.HandleMessage(action, msg);
                    break;

                case "vrcRefreshFriends":
                    await _friends.RefreshFriendsAsync();
                    break;

                // Update own status
                case "vrcUpdateStatus":
                    var newStatus = msg["status"]?.ToString() ?? "active";
                    var newDesc = msg["statusDescription"]?.ToString() ?? "";
                    await _friends.UpdateStatusAsync(newStatus, newDesc);
                    break;

                // Update own profile (bio, pronouns, links, languages, icon, banner)
                case "vrcUpdateProfile":
                    var upBio = msg["bio"] != null ? msg["bio"]!.ToString() : (string?)null;
                    var upPronouns = msg["pronouns"] != null ? msg["pronouns"]!.ToString() : (string?)null;
                    var upBioLinks = msg["bioLinks"]?.ToObject<List<string>>();
                    var upTags = msg["tags"]?.ToObject<List<string>>();
                    var upUserIcon = msg["userIcon"]           != null ? _imgCache?.GetOriginalUrl(msg["userIcon"]!.ToString())           ?? msg["userIcon"]!.ToString()           : (string?)null;
                    var upBanner   = msg["profilePicOverride"] != null ? _imgCache?.GetOriginalUrl(msg["profilePicOverride"]!.ToString()) ?? msg["profilePicOverride"]!.ToString() : (string?)null;
                    _ = Task.Run(async () =>
                    {
                        var updUser = await _vrcApi.UpdateProfileAsync(upBio, upPronouns, upBioLinks, upTags, upUserIcon, upBanner);
                        Invoke(() =>
                        {
                            if (updUser != null)
                            {
                                _authCtrl.SendVrcUserData(updUser);
                                SendToJS("vrcProfileUpdated", new { success = true });
                                SendToJS("log", new { msg = "VRChat: Profile updated", color = "ok" });
                            }
                            else
                            {
                                SendToJS("vrcProfileUpdated", new { success = false, error = "Update failed" });
                                SendToJS("log", new { msg = "VRChat: Profile update failed", color = "err" });
                            }
                        });
                    });
                    break;

                // Multi-Invite delegated to FriendsController
                case "vrcBatchInvite":
                    await _friends.HandleMessage(action, msg);
                    break;

                case "vrcGetMyInstances":
                    await _instance.HandleMessage(action, msg);
                    break;

                case "vrcRemoveMyInstance":
                    await _instance.HandleMessage(action, msg);
                    break;

                case "openLogFile":
                    if (!string.IsNullOrEmpty(_activityLogPath) && File.Exists(_activityLogPath))
                        Process.Start(new ProcessStartInfo(_activityLogPath) { UseShellExecute = true });
                    break;

                case "openLogFolder":
                    if (!string.IsNullOrEmpty(_activityLogDir) && Directory.Exists(_activityLogDir))
                        Process.Start(new ProcessStartInfo(_activityLogDir) { UseShellExecute = true });
                    break;

                // Get friend detail
                case "vrcGetFriendDetail":
                    var friendId = msg["userId"]?.ToString();
                    if (!string.IsNullOrEmpty(friendId))
                        await _friends.GetFriendDetailAsync(friendId);
                    break;

                // Friend actions delegated to FriendsController
                case "vrcJoinFriend":
                case "vrcInviteFriend":
                case "vrcInviteFriendWithPhoto":
                case "vrcGetInviteMessages":
                case "vrcUpdateInviteMessage":
                case "vrcRequestInvite":
                case "vrcGetUserAvatars":
                    await _friends.HandleMessage(action, msg);
                    break;

                case "vrcCreateInstance":
                    await _instance.HandleMessage(action, msg);
                    break;

                // User Notes
                case "vrcUpdateNote":
                    await _friends.HandleMessage(action, msg);
                    break;

                // Avatars - list and switch
                case "vrcGetAvatars":
                    var avatarFilterType = msg["filter"]?.ToString() ?? "own";
                    if (avatarFilterType == "own")
                    {
                        if (_settings.FfcEnabled)
                        {
                            var cachedAvt = _cache.LoadRaw(CacheHandler.KeyAvatars);
                            if (cachedAvt != null) Invoke(() => SendToJS("vrcAvatars", cachedAvt));
                        }
                        _ = Task.Run(_authCtrl.FetchAndCacheAvatarsAsync);
                    }
                    else
                    {
                        _ = Task.Run(_authCtrl.FetchAndCacheFavAvatarsAsync);
                    }
                    break;

                case "vrcSelectAvatar":
                    var selAvatarId = msg["avatarId"]?.ToString();
                    if (!string.IsNullOrEmpty(selAvatarId))
                    {
                        _ = Task.Run(async () =>
                        {
                            var ok5 = await _vrcApi.SelectAvatarAsync(selAvatarId);
                            Invoke(() =>
                            {
                                SendToJS("vrcAvatarSelected", new { avatarId = ok5 ? selAvatarId : "" });
                                SendToJS("log", new
                                {
                                    msg = ok5 ? "Avatar changed!" : "Failed to change avatar",
                                    color = ok5 ? "ok" : "err"
                                });
                            });
                        });
                    }
                    break;

                case "vrcSearchAvatars":
                    var avSearchQuery = msg["query"]?.ToString() ?? "";
                    var avSearchPage  = msg["page"]?.Value<int>() ?? 0;
                    if (!string.IsNullOrWhiteSpace(avSearchQuery))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                const int avLimit = 20;
                                var raw = await _vrcApi.SearchAvatarsAsync(avSearchQuery, avLimit, avSearchPage);
                                var list = raw.Cast<JObject>().Select(a => new
                                {
                                    id               = a["vrc_id"]?.ToString() ?? a["id"]?.ToString() ?? "",
                                    name             = a["name"]?.ToString() ?? "",
                                    thumbnailImageUrl = a["image_url"]?.ToString() ?? a["thumbnailImageUrl"]?.ToString() ?? "",
                                    imageUrl         = a["image_url"]?.ToString() ?? a["imageUrl"]?.ToString() ?? "",
                                    authorName       = a["author"]?["name"]?.ToString() ?? a["authorName"]?.ToString() ?? "",
                                    releaseStatus    = "public",
                                    description      = a["description"]?.ToString() ?? "",
                                    unityPackages    = (a["unityPackages"] as JArray ?? new JArray())
                                        .Select(p => new { platform = p["platform"]?.ToString() ?? "", variant = p["variant"]?.ToString() ?? "" })
                                        .ToArray(),
                                    compatibility    = a["compatibility"] as JArray ?? new JArray(),
                                }).ToList();
                                Invoke(() => SendToJS("vrcAvatarSearchResults", new
                                {
                                    results = list,
                                    page    = avSearchPage,
                                    hasMore = list.Count >= avLimit,
                                }));
                            }
                            catch (Exception ex)
                            {
                                Invoke(() => SendToJS("log", new { msg = $"Avatar search error: {ex.Message}", color = "err" }));
                            }
                        });
                    }
                    break;

                case "vrcCheckAvatars":
                {
                    var ids = msg["ids"]?.ToObject<string[]>();
                    if (ids is { Length: > 0 })
                    {
                        // Return cached deleted IDs immediately
                        List<string> cachedDeleted;
                        lock (_deletedAvatarIds) cachedDeleted = ids.Where(id => _deletedAvatarIds.Contains(id)).ToList();
                        if (cachedDeleted.Count > 0)
                        {
                            Invoke(() => SendToJS("vrcAvatarsDeleted", new { ids = cachedDeleted }));

                            // Queue cached deleted IDs for batched report to avtrdb
                            if (_settings.AvtrdbReportDeleted)
                                QueueAvtrdbReport(cachedDeleted);
                        }

                        // Mark IDs as checked IMMEDIATELY to prevent duplicate concurrent checks
                        string[] toCheck;
                        lock (_checkedAvatarIds)
                        {
                            toCheck = ids.Where(id => _checkedAvatarIds.Add(id)).ToArray();
                        }

                        if (toCheck.Length > 0)
                        {
                            _ = Task.Run(async () =>
                            {
                                var deleted = new List<string>();
                                foreach (var id in toCheck)
                                {
                                    try
                                    {
                                        var av = await _vrcApi.GetAvatarAsync(id);
                                        if (av == null) { deleted.Add(id); lock (_deletedAvatarIds) _deletedAvatarIds.Add(id); }
                                    }
                                    catch { deleted.Add(id); lock (_deletedAvatarIds) _deletedAvatarIds.Add(id); }
                                    await Task.Delay(250);
                                }
                                if (deleted.Count > 0)
                                {
                                    SaveDeletedAvatarsCache();
                                    Invoke(() => SendToJS("vrcAvatarsDeleted", new { ids = deleted }));

                                    if (_settings.AvtrdbReportDeleted)
                                        QueueAvtrdbReport(deleted);
                                }
                            });
                        }
                    }
                    break;
                }

                // Search - users, worlds, groups
                case "vrcSearchUsers":
                    var uQ = msg["query"]?.ToString() ?? "";
                    var uOff = msg["offset"]?.Value<int>() ?? 0;
                    _ = Task.Run(async () =>
                    {
                        var res = await _vrcApi.SearchUsersAsync(uQ, 20, uOff);
                        var list = res.Cast<JObject>().Select(u => new {
                            id = u["id"]?.ToString() ?? "", displayName = u["displayName"]?.ToString() ?? "",
                            image = VRChatApiService.GetUserImage(u), status = u["status"]?.ToString() ?? "offline",
                            statusDescription = u["statusDescription"]?.ToString() ?? "", bio = u["bio"]?.ToString() ?? "",
                            isFriend = u["isFriend"]?.Value<bool>() ?? false,
                            location = u["location"]?.ToString() ?? "",
                        }).ToList();
                        Invoke(() => SendToJS("vrcSearchResults", new { type = "users", results = list, offset = uOff, hasMore = list.Count >= 20 }));
                    });
                    break;

                case "vrcSearchWorlds":
                    var wQ = msg["query"]?.ToString() ?? "";
                    var wOff = msg["offset"]?.Value<int>() ?? 0;
                    _ = Task.Run(async () =>
                    {
                        var res = await _vrcApi.SearchWorldsAsync(wQ, 20, wOff);
                        var list = res.Cast<JObject>().Select(w => new {
                            id = w["id"]?.ToString() ?? "", name = w["name"]?.ToString() ?? "",
                            imageUrl = w["imageUrl"]?.ToString() ?? "", thumbnailImageUrl = w["thumbnailImageUrl"]?.ToString() ?? "",
                            authorName = w["authorName"]?.ToString() ?? "", occupants = w["occupants"]?.Value<int>() ?? 0,
                            capacity = w["capacity"]?.Value<int>() ?? 0, favorites = w["favorites"]?.Value<int>() ?? 0,
                            visits = w["visits"]?.Value<int>() ?? 0, description = w["description"]?.ToString() ?? "",
                            tags = w["tags"]?.ToObject<List<string>>() ?? new(),
                            worldTimeSeconds = _worldTimeTracker.GetWorldStats(w["id"]?.ToString() ?? "").totalSeconds,
                        }).ToList();
                        Invoke(() => SendToJS("vrcSearchResults", new { type = "worlds", results = list, offset = wOff, hasMore = list.Count >= 20 }));
                    });
                    break;

                case "vrcSearchGroups":
                    await _groups.HandleMessage(action, msg);
                    break;

                case "vrcGetWorldDetail":
                    var wdId = msg["worldId"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(wdId))
                    {
                        _ = Task.Run(async () =>
                        {
                            static string StripNonce(string l) =>
                                System.Text.RegularExpressions.Regex.Replace(l ?? "", @"~nonce\([^)]*\)", "");

                            var world = await _vrcApi.GetWorldAsync(wdId);
                            if (world == null)
                            {
                                Invoke(() => SendToJS("vrcWorldDetailError", new { error = "Could not load world" }));
                                return;
                            }
                            // Helper: parse owner ID (usr_xxx or grp_xxx) from instance ID string
                            static string ParseOwnerId(string instId) {
                                var m = System.Text.RegularExpressions.Regex.Match(instId, @"~(?:friends|hidden|private|group)\(([^)]+)\)");
                                return m.Success ? m.Groups[1].Value : "";
                            }

                            // Phase 1 — build raw list with ownerIds
                            var rawInstances = new List<(string instanceId, int users, string type, string region, string location, string ownerId)>();
                            var knownLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            var instArr = world["instances"] as JArray;
                            if (instArr != null)
                            {
                                foreach (var inst in instArr)
                                {
                                    if (inst is JArray pair && pair.Count >= 2)
                                    {
                                        var instId = pair[0]?.ToString() ?? "";
                                        var users = pair[1]?.Value<int>() ?? 0;
                                        var (_, _, instType) = VRChatApiService.ParseLocation($"{wdId}:{instId}");
                                        // ~canRequestInvite in raw instance IDs (from world's instances array) is the instance type flag
                                        if (instType == "private" && instId.Contains("~canRequestInvite")) instType = "invite_plus";
                                        var regionMatch = System.Text.RegularExpressions.Regex.Match(instId, @"region\(([^)]+)\)");
                                        var region = regionMatch.Success ? regionMatch.Groups[1].Value : "us";
                                        var loc = $"{wdId}:{instId}";
                                        rawInstances.Add((instId, users, instType, region, loc, ParseOwnerId(instId)));
                                        knownLocations.Add(loc);
                                    }
                                }
                            }
                            // Find friend locations in this world not covered by the world API instances
                            var storeSnapshot = _friends.GetStoreSnapshot();
                            var friendLocs = storeSnapshot
                                .Select(f => f["location"]?.ToString() ?? "")
                                .Where(loc => loc.StartsWith(wdId + ":"))
                                .Distinct()
                                .Where(loc => !knownLocations.Contains(StripNonce(loc)))
                                .ToList();
                            // Fetch real user counts for friend-inferred instances in parallel
                            if (friendLocs.Count > 0)
                            {
                                var instTasks = friendLocs.Select(loc => _vrcApi.GetInstanceAsync(loc)).ToArray();
                                var instResults = await Task.WhenAll(instTasks);
                                for (int i = 0; i < friendLocs.Count; i++)
                                {
                                    var loc = friendLocs[i];
                                    var instData = instResults[i];
                                    var nUsers = instData?["n_users"]?.Value<int>() ?? instData?["userCount"]?.Value<int>() ?? 0;
                                    var (_, instId2, instType2) = VRChatApiService.ParseLocation(loc);
                                    // Use instance API canRequestInvite to distinguish Invite from Invite+
                                    var instType2Final = instType2 == "private" && instData?["canRequestInvite"]?.Value<bool>() == true ? "invite_plus" : instType2;
                                    var regionMatch2 = System.Text.RegularExpressions.Regex.Match(instId2, @"region\(([^)]+)\)");
                                    var region2 = regionMatch2.Success ? regionMatch2.Groups[1].Value : "us";
                                    rawInstances.Add((instId2, nUsers, instType2Final, region2, loc, ParseOwnerId(instId2)));
                                }
                            }

                            // Phase 2 — resolve owner names
                            // Batch-fetch group names for any grp_ owners
                            var uniqueGroupIds = rawInstances
                                .Where(r => r.ownerId.StartsWith("grp_"))
                                .Select(r => r.ownerId).Distinct().ToList();
                            var groupInfoMap = new Dictionary<string, (string name, string shortCode)>();
                            if (uniqueGroupIds.Count > 0)
                            {
                                var gTasks = uniqueGroupIds.ToDictionary(id => id, id => _vrcApi.GetGroupAsync(id));
                                try { await Task.WhenAll(gTasks.Values); } catch { }
                                foreach (var kv in gTasks)
                                    if (!kv.Value.IsFaulted && kv.Value.Result != null)
                                        groupInfoMap[kv.Key] = (
                                            kv.Value.Result["name"]?.ToString() ?? "",
                                            kv.Value.Result["shortCode"]?.ToString() ?? "");
                            }
                            var instances = rawInstances.Select(r => {
                                var ownerName = "";
                                var ownerGroup = "";
                                if (r.ownerId.StartsWith("usr_"))
                                    { var f = _friends.GetStoreValue(r.ownerId); ownerName = f?["displayName"]?.ToString() ?? ""; }
                                else if (r.ownerId.StartsWith("grp_") && groupInfoMap.TryGetValue(r.ownerId, out var info))
                                    (ownerName, ownerGroup) = info;
                                return new { instanceId = r.instanceId, users = r.users, type = r.type, region = r.region, location = r.location, ownerName, ownerGroup, ownerId = r.ownerId };
                            }).ToList<object>();
                            var tags = world["tags"]?.ToObject<List<string>>() ?? new();
                            var (wTimeSeconds, wVisitCount, wLastVisited) = _worldTimeTracker.GetWorldStats(world["id"]?.ToString() ?? "");
                            Invoke(() => SendToJS("vrcWorldDetail", new
                            {
                                id = world["id"]?.ToString() ?? "",
                                name = world["name"]?.ToString() ?? "",
                                description = world["description"]?.ToString() ?? "",
                                imageUrl = world["imageUrl"]?.ToString() ?? "",
                                thumbnailImageUrl = world["thumbnailImageUrl"]?.ToString() ?? "",
                                authorName = world["authorName"]?.ToString() ?? "",
                                authorId = world["authorId"]?.ToString() ?? "",
                                occupants = world["occupants"]?.Value<int>() ?? 0,
                                publicOccupants = world["publicOccupants"]?.Value<int>() ?? 0,
                                privateOccupants = world["privateOccupants"]?.Value<int>() ?? 0,
                                capacity = world["capacity"]?.Value<int>() ?? 0,
                                recommendedCapacity = world["recommendedCapacity"]?.Value<int>() ?? 0,
                                favorites = world["favorites"]?.Value<int>() ?? 0,
                                visits = world["visits"]?.Value<int>() ?? 0,
                                tags,
                                instances,
                                worldTimeSeconds = wTimeSeconds,
                                worldVisitCount = wVisitCount,
                            }));
                        });
                    }
                    break;

                case "vrcGetOnlineCount":
                    await _instance.HandleMessage(action, msg);
                    break;

                case "vrcUpdateAvatar":
                {
                    var avId     = msg["avatarId"]?.ToString()             ?? "";
                    var avName   = msg["name"]?.ToString()                 ?? "";
                    var avDesc   = msg["description"]?.ToString()          ?? "";
                    var avStatus = msg["releaseStatus"]?.ToString()        ?? "private";
                    var avTags   = msg["tags"]?.ToObject<List<string>>()   ?? new();
                    if (!string.IsNullOrEmpty(avId))
                        _ = Task.Run(async () =>
                        {
                            var (ok, error) = await _vrcApi.UpdateAvatarAsync(avId, avName, avDesc, avStatus, avTags);
                            Invoke(() => SendToJS("vrcAvatarUpdateResult", new
                            {
                                ok,
                                error,
                                name          = ok ? avName   : (string?)null,
                                description   = ok ? avDesc   : (string?)null,
                                releaseStatus = ok ? avStatus : (string?)null,
                                tags          = ok ? avTags   : (List<string>?)null,
                            }));
                        });
                    break;
                }

                case "vrcGetAvatarDetail":
                {
                    var avdId = msg["avatarId"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(avdId))
                    {
                        _ = Task.Run(async () =>
                        {
                            var avatar = await _vrcApi.GetAvatarAsync(avdId);
                            if (avatar == null)
                            {
                                Invoke(() => SendToJS("vrcAvatarDetailError", new { error = "Could not load avatar" }));
                                return;
                            }
                            // Parse unityPackages for platform + performance rating
                            var packages = avatar["unityPackages"] as JArray ?? new JArray();
                            var realPkgs = packages.Where(p => p["variant"]?.ToString() != "impostor").ToList();
                            var hasPC    = realPkgs.Any(p => p["platform"]?.ToString() == "standalonewindows");
                            var hasQuest = realPkgs.Any(p => p["platform"]?.ToString() == "android");
                            var hasImpostor = packages.Any(p => p["variant"]?.ToString() == "impostor");
                            var pcPerf    = realPkgs.FirstOrDefault(p => p["platform"]?.ToString() == "standalonewindows")?["performanceRating"]?.ToString() ?? "";
                            var questPerf = realPkgs.FirstOrDefault(p => p["platform"]?.ToString() == "android")?["performanceRating"]?.ToString() ?? "";
                            // Fallback: newer performance object
                            var perf = avatar["performance"] as JObject;
                            if (string.IsNullOrEmpty(pcPerf))    pcPerf    = perf?["standalonewindows"]?.ToString() ?? "";
                            if (string.IsNullOrEmpty(questPerf)) questPerf = perf?["android"]?.ToString() ?? "";
                            Invoke(() => SendToJS("vrcAvatarDetail", new
                            {
                                id               = avatar["id"]?.ToString()                  ?? "",
                                name             = avatar["name"]?.ToString()                ?? "",
                                authorName       = avatar["authorName"]?.ToString()          ?? "",
                                authorId         = avatar["authorId"]?.ToString()            ?? "",
                                thumbnailImageUrl = avatar["thumbnailImageUrl"]?.ToString()  ?? "",
                                imageUrl         = avatar["imageUrl"]?.ToString()            ?? "",
                                releaseStatus    = avatar["releaseStatus"]?.ToString()       ?? "",
                                version          = avatar["version"]?.Value<int>()           ?? 0,
                                created_at       = avatar["created_at"]?.ToString()          ?? "",
                                updated_at       = avatar["updated_at"]?.ToString()          ?? "",
                                description      = avatar["description"]?.ToString()         ?? "",
                                tags             = avatar["tags"]?.ToObject<List<string>>()  ?? new(),
                                hasPC,
                                hasQuest,
                                hasImpostor,
                                pcPerf,
                                questPerf,
                            }));
                        });
                    }
                    break;
                }

                // Favorite Friends
                case "vrcGetFavoriteFriends":
                case "vrcAddFavoriteFriend":
                case "vrcRemoveFavoriteFriend":
                    await _friends.HandleMessage(action, msg);
                    break;

                case "vrcGetMyWorlds":
                    _ = Task.Run(async () =>
                    {
                        var worlds = await _vrcApi.GetMyWorldsAsync();
                        Invoke(() => SendToJS("vrcMyWorlds", worlds));
                    });
                    break;

                case "getWorldInsights":
                    _ = Task.Run(() =>
                    {
                        var worldId = msg["worldId"]?.ToString() ?? "";
                        var from    = msg["from"]?.ToString() ?? "";
                        var to      = msg["to"]?.ToString() ?? "";
                        if (string.IsNullOrEmpty(worldId) || string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) return;
                        var stats = _timeline.GetWorldStats(worldId, from, to);
                        Invoke(() => SendToJS("worldInsights", new { worldId, from, to, stats }));
                    });
                    break;

                case "refreshWorldInsights":
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var worldId = msg["worldId"]?.ToString() ?? "";
                            var from    = msg["from"]?.ToString() ?? "";
                            var to      = msg["to"]?.ToString() ?? "";

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

                            if (!string.IsNullOrEmpty(worldId) && !string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(to))
                            {
                                var stats = _timeline.GetWorldStats(worldId, from, to);
                                Invoke(() => SendToJS("worldInsights", new { worldId, from, to, stats }));
                            }
                        }
                        catch { }
                    });
                    break;

                // Groups - my groups, join, leave
                case "vrcGetFavoriteWorlds":
                    _ = Task.Run(async () =>
                    {
                        if (_settings.FfcEnabled)
                        {
                            var cachedFavWorlds = _cache.LoadRaw(CacheHandler.KeyFavWorlds);
                            if (cachedFavWorlds != null) Invoke(() => SendToJS("vrcFavoriteWorlds", cachedFavWorlds));
                        }
                        await _authCtrl.FetchAndCacheFavWorldsAsync();
                    });
                    break;

                case "vrcUpdateFavoriteGroup":
                    _ = Task.Run(async () =>
                    {
                        var groupType = msg["groupType"]?.ToString() ?? "world";
                        var groupName = msg["groupName"]?.ToString() ?? "";
                        var displayName = msg["displayName"]?.ToString() ?? "";
                        var ok = await _vrcApi.UpdateFavoriteGroupAsync(groupType, groupName, displayName);
                        Invoke(() => SendToJS("vrcFavoriteGroupUpdated", new { ok, groupName, displayName }));
                    });
                    break;

                case "vrcGetWorldFavGroups":
                    _ = Task.Run(async () =>
                    {
                        var groups = await _vrcApi.GetFavoriteGroupsAsync();
                        var worldTypes = new HashSet<string> { "world", "vrcPlusWorld" };
                        var groupList = groups
                            .Where(g => worldTypes.Contains(g["type"]?.ToString() ?? ""))
                            .Select(g => new AuthController.WFavGroup {
                                name        = g["name"]?.ToString() ?? "",
                                displayName = g["displayName"]?.ToString() ?? "",
                                type        = g["type"]?.ToString() ?? "world"
                            })
                            .Where(g => !string.IsNullOrEmpty(g.name))
                            .ToList();
                        groupList = AuthController.FillMissingWorldSlots(groupList);
                        Invoke(() => SendToJS("vrcWorldFavGroups", groupList));
                    });
                    break;

                case "vrcSetHomeWorld":
                    var homeWid = msg["worldId"]?.ToString();
                    if (!string.IsNullOrEmpty(homeWid))
                    {
                        _ = Task.Run(async () =>
                        {
                            var ok = await _vrcApi.SetHomeWorldAsync(homeWid);
                            Invoke(() => SendToJS("vrcActionResult", new { action = "setHomeWorld", success = ok,
                                message = ok ? "Home world updated!" : "Failed to set home world" }));
                        });
                    }
                    break;

                case "vrcAddWorldFavorite":
                    _ = Task.Run(async () =>
                    {
                        var worldId   = msg["worldId"]?.ToString() ?? "";
                        var groupName = msg["groupName"]?.ToString() ?? "";
                        var groupType = msg["groupType"]?.ToString() ?? "world";
                        var oldFvrtId = msg["oldFvrtId"]?.ToString();
                        var (ok, resultData) = await _vrcApi.AddWorldFavoriteAsync(worldId, groupName, groupType, oldFvrtId);
                        // resultData = new fvrt ID on success, error message on failure
                        Invoke(() => SendToJS("vrcWorldFavoriteResult", new { ok, worldId, groupName, newFvrtId = ok ? resultData : "", error = ok ? "" : resultData }));
                    });
                    break;

                case "vrcRemoveWorldFavorite":
                {
                    var worldId = msg["worldId"]?.ToString() ?? "";
                    var fvrtId  = msg["fvrtId"]?.ToString() ?? "";
                    _ = Task.Run(async () =>
                    {
                        var ok = await _vrcApi.RemoveFavoriteFriendAsync(fvrtId);
                        Invoke(() => SendToJS("vrcWorldUnfavoriteResult", new { ok, worldId }));
                    });
                    break;
                }

                case "vrcGetAvatarFavGroups":
                    _ = Task.Run(async () =>
                    {
                        var groups = await _vrcApi.GetFavoriteGroupsAsync();
                        var avatarTypes = new HashSet<string> { "avatar" };
                        var groupList = groups
                            .Where(g => avatarTypes.Contains(g["type"]?.ToString() ?? ""))
                            .Select(g => new AuthController.WFavGroup {
                                name        = g["name"]?.ToString() ?? "",
                                displayName = g["displayName"]?.ToString() ?? "",
                                type        = g["type"]?.ToString() ?? "avatar"
                            })
                            .Where(g => !string.IsNullOrEmpty(g.name))
                            .ToList();
                        groupList = AuthController.FillMissingAvatarSlots(groupList);
                        int avCap = _vrcApi.HasVrcPlus ? 50 : 25;
                        foreach (var g in groupList) g.capacity = avCap;
                        Invoke(() => SendToJS("vrcAvatarFavGroups", groupList));
                    });
                    break;

                case "vrcAddAvatarFavorite":
                    _ = Task.Run(async () =>
                    {
                        var avId      = msg["avatarId"]?.ToString() ?? "";
                        var avGroup   = msg["groupName"]?.ToString() ?? "";
                        var avType    = msg["groupType"]?.ToString() ?? "avatar";
                        var avOldFvrt = msg["oldFvrtId"]?.ToString();
                        var (avOk, avResult) = await _vrcApi.AddAvatarFavoriteAsync(avId, avGroup, avType, avOldFvrt);
                        Invoke(() => SendToJS("vrcAvatarFavoriteResult", new { ok = avOk, avatarId = avId, groupName = avGroup, newFvrtId = avOk ? avResult : "", error = avOk ? "" : avResult }));
                    });
                    break;

                case "vrcRemoveAvatarFavorite":
                {
                    var avRmId   = msg["avatarId"]?.ToString() ?? "";
                    var avFvrtId = msg["fvrtId"]?.ToString() ?? "";
                    _ = Task.Run(async () =>
                    {
                        var ok = await _vrcApi.RemoveFavoriteFriendAsync(avFvrtId);
                        Invoke(() => SendToJS("vrcAvatarUnfavoriteResult", new { ok, avatarId = avRmId }));
                    });
                    break;
                }

                case "vrcGetMyGroups":
                    await _groups.HandleMessage(action, msg);
                    break;

                case "vrcGetGroup":
                    await _groups.HandleMessage(action, msg);
                    break;

                case "vrcJoinGroup":
                case "vrcGetGroupMembers":
                case "vrcSearchGroupMembers":
                case "vrcGetGroupRoleMembers":
                case "vrcLeaveGroup":
                    await _groups.HandleMessage(action, msg);
                    break;

                case "vrcCreateGroupPost":
                case "vrcDeleteGroupPost":
                case "vrcDeleteGroupEvent":
                    await _groups.HandleMessage(action, msg);
                    break;

                case "vrcUpdateGroup":
                case "vrcKickGroupMember":
                case "vrcBanGroupMember":
                case "vrcGetGroupBans":
                case "vrcUnbanGroupMember":
                case "vrcCreateGroupRole":
                case "vrcUpdateGroupRole":
                case "vrcDeleteGroupRole":
                case "vrcAddGroupMemberRole":
                case "vrcRemoveGroupMemberRole":
                case "vrcCreateGroupEvent":
                case "vrcGetMutualsForNetwork":
                case "vrcSaveMutualCache":
                case "vrcLoadMutualCache":
                case "vrcClearMutualCache":
                    await _groups.HandleMessage(action, msg);
                    break;

                case "vrcGetTimeSpent":
                    await _instance.HandleMessage(action, msg);
                    break;

                case "vrcCreateGroupInstance":
                    await _groups.HandleMessage(action, msg);
                    break;

                // Custom Chatbox OSC
                case "chatboxConfig":
                case "chatboxStop":
                    _chatboxCtrl.HandleMessage(action, msg);
                    break;

                // Space Flight
                case "sfConnect":
                case "sfDisconnect":
                case "sfReset":
                case "sfConfig":
                    _sfCtrl.HandleMessage(action, msg);
                    break;

                // Voice Fight
                case "vfGetDevices":
                case "vfGetItems":
                case "vfStart":
                case "vfStop":
                case "vfAddSound":
                case "vfAddSoundToItem":
                case "vfDeleteItem":
                case "vfDeleteSound":
                case "vfPlaySound":
                case "vfSetStopWord":
                case "vfStopSound":
                case "vfGetBlockList":
                case "vfSetBlockList":
                case "vfSetWord":
                case "vfSetVolume":
                case "vfSetMuteTalk":
                case "vfSetInputDevice":
                case "vfSetOutputDevice":
                    _vfCtrl.HandleMessage(action, msg);
                    break;

                // OSC Tool
                case "oscConnect":
                case "oscDisconnect":
                case "oscSend":
                case "oscEnableOutputs":
                    _chatboxCtrl.HandleMessage(action, msg);
                    break;

                // VRCVideoCacher
                case "vcCheck":
                case "vcInstall":
                case "vcStart":
                case "vcStop":
                case "vcSend":
                    _relayCtrl.HandleMessage(action, msg);
                    break;

                // Friend actions delegated to FriendsController
                case "vrcSendFriendRequest":
                case "vrcUnfriend":
                case "vrcGetBlocked":
                case "vrcGetMuted":
                case "vrcBlock":
                case "vrcMute":
                case "vrcUnblock":
                case "vrcUnmute":
                case "vrcBoop":
                case "vrcSendChatMessage":
                case "vrcGetChatHistory":
                    await _friends.HandleMessage(action, msg);
                    break;

                // Calendar
                case "vrcGetCalendarEvents":
                    var calFilter = msg["filter"]?.ToString() ?? "all";
                    var calYear   = msg["year"]?.Value<int>()  ?? 0;
                    var calMonth  = msg["month"]?.Value<int>() ?? 0;
                    _ = Task.Run(async () => {
                        var evts = await _vrcApi.GetCalendarEventsAsync(calFilter, calYear, calMonth);
                        Invoke(() => SendToJS("vrcCalendarEvents", new { events = evts, filter = calFilter }));
                    });
                    break;

                case "vrcGetCalendarEvent":
                    var calGrpId = msg["groupId"]?.ToString();
                    var calEvtId = msg["calendarId"]?.ToString();
                    if (!string.IsNullOrEmpty(calGrpId) && !string.IsNullOrEmpty(calEvtId))
                    {
                        _ = Task.Run(async () => {
                            var ev = await _vrcApi.GetCalendarEventAsync(calGrpId, calEvtId);
                            Invoke(() => SendToJS("vrcCalendarEvent", ev ?? new JObject()));
                        });
                    }
                    break;

                case "vrcFollowEvent":
                    var fevGrpId = msg["groupId"]?.ToString();
                    var fevEvtId = msg["calendarId"]?.ToString();
                    var doFollow = msg["follow"]?.Value<bool>() ?? true;
                    if (!string.IsNullOrEmpty(fevGrpId) && !string.IsNullOrEmpty(fevEvtId))
                    {
                        _ = Task.Run(async () => {
                            var ok = await _vrcApi.FollowEventAsync(fevGrpId, fevEvtId, doFollow);
                            Invoke(() => SendToJS("vrcActionResult", new { action = doFollow ? "followEvent" : "unfollowEvent",
                                success = ok, message = ok ? (doFollow ? "Following event" : "Unfollowed") : "Failed" }));
                        });
                    }
                    break;

                // Notifications delegated to NotificationsController
                case "vrcGetNotifications":
                case "vrcAcceptNotification":
                case "vrcMarkNotifRead":
                case "vrcHideNotification":
                    await _notifications.HandleMessage(action, msg);
                    break;

                // App updates
                case "checkUpdate":
                case "installUpdate":
                    await _authCtrl.HandleMessage(action, msg);
                    break;

                case "vrcLaunchAndJoin":
                    _relayCtrl.HandleMessage(action, msg);
                    break;

                // Current instance
                case "vrcGetCurrentInstance":
                    await _instance.HandleMessage(action, msg);
                    break;

                // User detail (for non-friend profile viewing)
                case "vrcGetUser":
                    var guId = msg["userId"]?.ToString();
                    if (!string.IsNullOrEmpty(guId))
                    {
                        _ = Task.Run(async () => {
                            var u = await _vrcApi.GetUserAsync(guId);
                            if (u != null) Invoke(() => SendToJS("vrcUserDetail", new {
                                id = u["id"]?.ToString() ?? "", displayName = u["displayName"]?.ToString() ?? "",
                                image = VRChatApiService.GetUserImage(u), status = u["status"]?.ToString() ?? "offline",
                                statusDescription = u["statusDescription"]?.ToString() ?? "",
                                bio = u["bio"]?.ToString() ?? "", location = u["location"]?.ToString() ?? "",
                                isFriend = u["isFriend"]?.Value<bool>() ?? false,
                                currentAvatarImageUrl = u["currentAvatarImageUrl"]?.ToString() ?? "",
                            }));
                        });
                    }
                    break;

                // Timeline — all timeline + import message cases delegated to TimelineController
                case "getTimeline":
                case "getTimelinePage":
                case "searchTimeline":
                case "searchFriendTimeline":
                case "getFriendTimeline":
                case "getFriendTimelinePage":
                case "getFtAlsoWasHere":
                case "getTimelineByDate":
                case "getFriendTimelineByDate":
                    await _timelineCtrl.HandleMessage(action, msg);
                    break;

                // Inventory

                case "invGetFiles":
                {
                    var invTag = msg["tag"]?.ToString() ?? "gallery";
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var files = await _vrcApi.GetInventoryFilesAsync(invTag);
                            // Also fetch emojianimated when tag=emoji
                            if (invTag == "emoji")
                            {
                                var animated = await _vrcApi.GetInventoryFilesAsync("emojianimated");
                                foreach (var a in animated)
                                    files.Add(a);
                            }
                            var list = files.OfType<Newtonsoft.Json.Linq.JObject>().Select(f =>
                            {
                                var versions = (f["versions"] as Newtonsoft.Json.Linq.JArray) ?? new Newtonsoft.Json.Linq.JArray();
                                var latest = versions.OfType<Newtonsoft.Json.Linq.JObject>()
                                    .LastOrDefault(v => v["status"]?.ToString() == "complete")
                                    ?? versions.OfType<Newtonsoft.Json.Linq.JObject>().LastOrDefault();
                                var fileUrl = latest?["file"]?["url"]?.ToString() ?? "";
                                var versionId = latest?["version"]?.Value<int>() ?? 1;
                                var sizeBytes = latest?["file"]?["sizeInBytes"]?.Value<long>() ?? 0;
                                var createdAt = IsoDate(latest?["created_at"] ?? f["created_at"]);
                                return new
                                {
                                    id = f["id"]?.ToString() ?? "",
                                    name = f["name"]?.ToString() ?? "",
                                    tags = (f["tags"] as Newtonsoft.Json.Linq.JArray)?.ToObject<List<string>>() ?? new List<string>(),
                                    animationStyle = f["animationStyle"]?.ToString() ?? "",
                                    maskTag = f["maskTag"]?.ToString() ?? "",
                                    fileUrl,
                                    versionId,
                                    sizeBytes,
                                    createdAt,
                                };
                            }).OrderByDescending(f => f.createdAt).ToList();
                            Invoke(() => SendToJS("invFiles", new { tag = invTag, files = list }));
                        }
                        catch (Exception ex)
                        {
                            Invoke(() => SendToJS("log", new { msg = $"Inventory load error: {ex.Message}", color = "err" }));
                            Invoke(() => SendToJS("invFiles", new { tag = invTag, files = new object[0], error = ex.Message }));
                        }
                    });
                    break;
                }

                case "invBrowseUpload":
                {
                    var uploadTag = msg["tag"]?.ToString() ?? "gallery";
                    var r = Dialog.FileOpen("png");
                    if (r.IsOk)
                    {
                        var path = r.Path;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var bytes = System.IO.File.ReadAllBytes(path);
                                var (ok, file, error) = await _vrcApi.UploadInventoryImageAsync(bytes, uploadTag);
                                if (ok && file != null)
                                {
                                    var versions = (file["versions"] as Newtonsoft.Json.Linq.JArray) ?? new Newtonsoft.Json.Linq.JArray();
                                    var latest = versions.OfType<Newtonsoft.Json.Linq.JObject>()
                                        .LastOrDefault(v => v["status"]?.ToString() == "complete")
                                        ?? versions.OfType<Newtonsoft.Json.Linq.JObject>().LastOrDefault();
                                    var fileUrl = latest?["file"]?["url"]?.ToString() ?? "";
                                    var versionId = latest?["version"]?.Value<int>() ?? 1;
                                    var newFile = new
                                    {
                                        id = file["id"]?.ToString() ?? "",
                                        name = file["name"]?.ToString() ?? "",
                                        tags = (file["tags"] as Newtonsoft.Json.Linq.JArray)?.ToObject<List<string>>() ?? new List<string>(),
                                        animationStyle = file["animationStyle"]?.ToString() ?? "",
                                        maskTag = file["maskTag"]?.ToString() ?? "",
                                        fileUrl,
                                        versionId,
                                        sizeBytes = latest?["file"]?["sizeInBytes"]?.Value<long>() ?? (long)bytes.Length,
                                        createdAt = DateTime.UtcNow.ToString("o"),
                                    };
                                    SendToJS("invUploadResult", new { success = true, tag = uploadTag, file = newFile });
                                }
                                else
                                {
                                    SendToJS("invUploadResult", new { success = false, tag = uploadTag, error });
                                }
                            }
                            catch (Exception ex)
                            {
                                SendToJS("invUploadResult", new { success = false, tag = uploadTag, error = ex.Message });
                            }
                        });
                    }
                    break;
                }

                case "invUploadFromData":
                {
                    var uploadTag2  = msg["tag"]?.ToString() ?? "gallery";
                    var dataB64     = msg["data"]?.ToString() ?? "";
                    var animStyle   = msg["animationStyle"]?.ToString() ?? "";
                    var maskTagVal  = msg["maskTag"]?.ToString() ?? "";

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Strip data-URL prefix (data:image/png;base64,...)
                            var raw = dataB64.Contains(",") ? dataB64.Split(',')[1] : dataB64;
                            var bytes2 = Convert.FromBase64String(raw);

                            var (ok2, file2, error2) = await _vrcApi.UploadInventoryImageAsync(bytes2, uploadTag2, animStyle, maskTagVal);
                            if (ok2 && file2 != null)
                            {
                                var versions2 = (file2["versions"] as Newtonsoft.Json.Linq.JArray) ?? new Newtonsoft.Json.Linq.JArray();
                                var latest2   = versions2.OfType<Newtonsoft.Json.Linq.JObject>()
                                    .LastOrDefault(v => v["status"]?.ToString() == "complete")
                                    ?? versions2.OfType<Newtonsoft.Json.Linq.JObject>().LastOrDefault();
                                var fileUrl2    = latest2?["file"]?["url"]?.ToString() ?? "";
                                var versionId2  = latest2?["version"]?.Value<int>() ?? 1;
                                var newFile2 = new
                                {
                                    id            = file2["id"]?.ToString() ?? "",
                                    name          = file2["name"]?.ToString() ?? "",
                                    tags          = (file2["tags"] as Newtonsoft.Json.Linq.JArray)?.ToObject<List<string>>() ?? new List<string>(),
                                    animationStyle = file2["animationStyle"]?.ToString() ?? "",
                                    maskTag       = file2["maskTag"]?.ToString() ?? "",
                                    fileUrl       = fileUrl2,
                                    versionId     = versionId2,
                                    sizeBytes     = latest2?["file"]?["sizeInBytes"]?.Value<long>() ?? (long)bytes2.Length,
                                    createdAt     = DateTime.UtcNow.ToString("o"),
                                };
                                Invoke(() => SendToJS("invUploadResult", new { success = true, tag = uploadTag2, file = newFile2 }));
                            }
                            else
                            {
                                Invoke(() => SendToJS("invUploadResult", new { success = false, tag = uploadTag2, error = error2 }));
                            }
                        }
                        catch (Exception ex)
                        {
                            Invoke(() => SendToJS("invUploadResult", new { success = false, tag = uploadTag2, error = ex.Message }));
                        }
                    });
                    break;
                }

                case "invDeleteFile":
                {
                    var delFileId = msg["fileId"]?.ToString();
                    if (!string.IsNullOrEmpty(delFileId))
                    {
                        _ = Task.Run(async () =>
                        {
                            var ok = await _vrcApi.DeleteInventoryFileAsync(delFileId);
                            Invoke(() => SendToJS("invDeleteResult", new { success = ok, fileId = delFileId }));
                        });
                    }
                    break;
                }

                case "invGetPrints":
                {
                    var printUserId = _vrcApi.CurrentUserId;
                    if (!string.IsNullOrEmpty(printUserId))
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var prints = await _vrcApi.GetUserPrintsAsync(printUserId);
                                var list = prints.OfType<Newtonsoft.Json.Linq.JObject>().Select(p =>
                                {
                                    // Try to get image URL from files object
                                    var filesObj = p["files"] as Newtonsoft.Json.Linq.JObject;
                                    var imageUrl = filesObj?["image"]?.ToString()
                                        ?? p["imageUrl"]?.ToString()
                                        ?? p["thumbnailImageUrl"]?.ToString()
                                        ?? "";
                                    return new
                                    {
                                        id = p["id"]?.ToString() ?? "",
                                        authorId = p["authorId"]?.ToString() ?? "",
                                        authorName = p["authorName"]?.ToString() ?? "",
                                        worldId = p["worldId"]?.ToString() ?? "",
                                        worldName = p["worldName"]?.ToString() ?? "",
                                        note = p["note"]?.ToString() ?? "",
                                        createdAt = IsoDate(p["createdAt"] ?? p["timestamp"]),
                                        imageUrl,
                                    };
                                }).OrderByDescending(p => p.createdAt).ToList();
                                Invoke(() => SendToJS("invPrints", new { prints = list }));
                            }
                            catch (Exception ex)
                            {
                                Invoke(() => SendToJS("log", new { msg = $"Prints load error: {ex.Message}", color = "err" }));
                                Invoke(() => SendToJS("invPrints", new { prints = new object[0], error = ex.Message }));
                            }
                        });
                    }
                    else
                    {
                        Invoke(() => SendToJS("invPrints", new { prints = new object[0] }));
                    }
                    break;
                }

                case "invGetInventory":
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var (items, total) = await _vrcApi.GetInventoryItemsAsync();
                            var list = items.OfType<Newtonsoft.Json.Linq.JObject>().Select(item => new
                            {
                                id          = item["id"]?.ToString() ?? "",
                                name        = item["name"]?.ToString() ?? "Item",
                                description = item["description"]?.ToString() ?? "",
                                itemType    = item["itemType"]?.ToString() ?? "",
                                imageUrl    = item["imageUrl"]?.ToString()
                                              ?? item["metadata"]?["imageUrl"]?.ToString() ?? "",
                                isArchived  = item["isArchived"]?.Value<bool>() ?? false,
                                createdAt   = IsoDate(item["created_at"]),
                            }).ToList();
                            Invoke(() => SendToJS("invInventory", new { items = list, totalCount = total }));
                        }
                        catch (Exception ex)
                        {
                            Invoke(() => SendToJS("invInventory", new { items = new object[0], error = ex.Message }));
                        }
                    });
                    break;
                }

                case "invDeletePrint":
                {
                    var delPrintId = msg["printId"]?.ToString();
                    if (!string.IsNullOrEmpty(delPrintId))
                    {
                        _ = Task.Run(async () =>
                        {
                            var ok = await _vrcApi.DeletePrintAsync(delPrintId);
                            Invoke(() => SendToJS("invPrintDeleteResult", new { success = ok, printId = delPrintId }));
                        });
                    }
                    break;
                }

                case "invDownload":
                {
                    var dlUrl = msg["url"]?.ToString();
                    var dlFileName = msg["fileName"]?.ToString() ?? "download.png";
                    if (!string.IsNullOrEmpty(dlUrl))
                    {
                        var rs = Dialog.FileSave("png");
                        if (rs.IsOk)
                        {
                            var savePath = rs.Path;
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var resp = await _vrcApi.GetHttpClient().GetAsync(dlUrl);
                                    if (resp.IsSuccessStatusCode)
                                    {
                                        var bytes = await resp.Content.ReadAsByteArrayAsync();
                                        System.IO.File.WriteAllBytes(savePath, bytes);
                                        SendToJS("log", new { msg = $"Saved: {savePath}", color = "ok" });
                                    }
                                    else
                                    {
                                        SendToJS("log", new { msg = $"Download failed: HTTP {(int)resp.StatusCode}", color = "err" });
                                    }
                                }
                                catch (Exception ex)
                                {
                                    SendToJS("log", new { msg = $"Download error: {ex.Message}", color = "err" });
                                }
                            });
                        }
                    }
                    break;
                }

                case "openUrl":
                    await _authCtrl.HandleMessage(action, msg);
                    break;

                // Discord Rich Presence
                case "dpStart":
                case "dpStop":
                case "dpRefresh":
                    _discordCtrl.HandleMessage(action, msg);
                    break;

                // VR Wrist Overlay
                case "vroConnect":
                case "overlayThemeColors":
                case "vroDisconnect":
                case "vroShow":
                case "vroHide":
                case "vroToggle":
                case "vroConfig":
                case "vroAutoSave":
                case "vroToastConfig":
                case "vroRecordKeybind":
                case "vroCancelRecording":
                case "vroSetTab":
                    await _vroCtrl.HandleMessage(action, msg);
                    break;
            }
        }
        catch (Exception ex)
        {
            SendToJS("log", new { msg = $"Error: {ex.Message}", color = "err" });
        }
    }

}
