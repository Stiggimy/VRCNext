using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VRCNext.Services;

namespace VRCNext;

// owns all friend related state, logic, message handling, and WebSocket events.

public class FriendsController
{
    private readonly CoreLibrary _core;

    // Friend State
    private readonly Dictionary<string, JObject> _friendStore = new();
    private readonly Dictionary<string, string> _friendLastLoc = new();
    private readonly Dictionary<string, string> _friendLastStatus = new();
    private readonly Dictionary<string, string> _friendLastStatusDesc = new();
    private readonly Dictionary<string, string> _friendLastBio = new();
    private readonly Dictionary<string, (string name, string image)> _friendNameImg = new();
    private readonly Dictionary<string, string> _favoriteFriends = new();
    private bool _friendStateSeeded;
    private readonly SemaphoreSlim _friendsRefreshLock = new(1, 1);
    private readonly HashSet<string> _profileRefreshInFlight = new();

    // Chat Storage
    private static readonly string _chatDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VRCNext", "chat");
    public record ChatEntry(string id, string from, string text, string time, string? type = null);

    // Public Accessors (for other domains)
    public bool FriendStateSeeded => _friendStateSeeded;

    public (string name, string image) GetNameImage(string userId)
        => _friendNameImg.GetValueOrDefault(userId, ("", ""));

    public bool TryGetNameImage(string userId, out (string name, string image) result)
        => _friendNameImg.TryGetValue(userId, out result);

    public bool IsInStore(string userId)
    {
        lock (_friendStore) return _friendStore.ContainsKey(userId);
    }

    public bool IsFavorited(string userId) => _favoriteFriends.ContainsKey(userId);
    public string GetFavoriteFriendId(string userId) => _favoriteFriends.GetValueOrDefault(userId, "");

    public List<JObject> GetStoreSnapshot()
    {
        lock (_friendStore) return _friendStore.Values.ToList();
    }

    public JObject? GetStoreValue(string userId)
    {
        lock (_friendStore) return _friendStore.TryGetValue(userId, out var v) ? v : null;
    }

    public List<string> GetTrackedUserIds() => _friendNameImg.Keys.ToList();

    // Prefers live caches over DB-stored images
    public string ResolvePlayerImage(string? userId, string? storedImage)
    {
        if (!string.IsNullOrEmpty(userId))
        {
            if (_friendNameImg.TryGetValue(userId, out var fi) && !string.IsNullOrEmpty(fi.image))
                return fi.image;
            if (_core.PlayerImageCache.TryGetValue(userId, out var ci) && !string.IsNullOrEmpty(ci))
                return ci;
            var dbImg = _core.Timeline.GetCachedUserImage(userId);
            if (!string.IsNullOrEmpty(dbImg))
            {
                _core.PlayerImageCache[userId] = dbImg;
                return dbImg;
            }
        }
        return storedImage ?? "";
    }

    // Constructor

    public FriendsController(CoreLibrary core) => _core = core;

    // WebSocket Wiring

    public void WireWebSocket(VRChatWebSocketService ws)
    {
        ws.FriendsChanged += (_, _) =>
        {
            if (_core.VrcApi.IsLoggedIn && _friendStateSeeded)
                PushFriendsFromStore();
        };

        ws.FriendListChanged += (_, _) =>
        {
            if (_core.VrcApi.IsLoggedIn)
                _ = RefreshFriendsAsync(true);
        };

        ws.FriendLocationChanged += OnWsFriendLocation;
        ws.FriendWentOffline     += OnWsFriendOffline;
        ws.FriendWentOnline      += OnWsFriendOnline;
        ws.FriendUpdated         += OnWsFriendUpdated;
        ws.FriendBecameActive    += OnWsFriendActive;
        ws.FriendAdded           += OnWsFriendAdded;
        ws.FriendRemoved         += OnWsFriendRemoved;
    }

    // Message Handler

    public async Task HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "vrcRefreshFriends":
                await RefreshFriendsAsync();
                break;

            case "vrcUpdateStatus":
                await UpdateStatusAsync(
                    msg["status"]?.ToString() ?? "active",
                    msg["statusDescription"]?.ToString() ?? "");
                break;

            case "vrcGetFriendDetail":
                var fdId = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(fdId))
                    await GetFriendDetailAsync(fdId);
                break;

            case "vrcGetUserAvatars":
            {
                var uid = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(uid))
                {
                    try
                    {
                        var raw = await _core.VrcApi.SearchAvatarsByAuthorAsync(uid);
                        var avatars = raw.Cast<JObject>().Select(a => new
                        {
                            id                = a["vrc_id"]?.ToString() ?? a["id"]?.ToString() ?? "",
                            name              = a["name"]?.ToString() ?? "",
                            thumbnailImageUrl = a["image_url"]?.ToString() ?? a["thumbnailImageUrl"]?.ToString() ?? "",
                            imageUrl          = a["image_url"]?.ToString() ?? a["imageUrl"]?.ToString() ?? "",
                            authorName        = a["author"]?["name"]?.ToString() ?? a["authorName"]?.ToString() ?? "",
                            releaseStatus     = "public",
                            compatibility     = a["compatibility"] as JArray ?? new JArray(),
                        }).ToList();
                        _core.SendToJS("vrcUserAvatars", new { userId = uid, avatars });
                    }
                    catch
                    {
                        _core.SendToJS("vrcUserAvatars", new { userId = uid, avatars = new JArray() });
                    }
                }
                break;
            }

            case "vrcJoinFriend":
                var joinLoc = msg["location"]?.ToString();
                if (!string.IsNullOrEmpty(joinLoc))
                    await HandleJoinFriendAsync(joinLoc);
                break;

            case "vrcInviteFriend":
            {
                var uid = msg["userId"]?.ToString();
                var slot = msg["messageSlot"]?.Value<int?>();
                if (!string.IsNullOrEmpty(uid))
                {
                    var ok = await _core.VrcApi.InviteFriendAsync(uid, _core.LogWatcher.CurrentLocation ?? "", slot);
                    _core.SendToJS("vrcActionResult", new
                    {
                        action = "invite", success = ok,
                        message = ok ? "Invite sent!" : "Failed to send invite. Make sure you are in a valid instance."
                    });
                }
                break;
            }

            case "vrcInviteFriendWithPhoto":
            {
                var uid = msg["userId"]?.ToString();
                var fileUrl = msg["fileUrl"]?.ToString();
                var slot = msg["messageSlot"]?.Value<int?>();
                if (!string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(fileUrl))
                {
                    var ok = await _core.VrcApi.InviteFriendWithPhotoAsync(uid, _core.LogWatcher.CurrentLocation ?? "", fileUrl, slot);
                    _core.SendToJS("vrcActionResult", new
                    {
                        action = "invite", success = ok,
                        message = ok ? "Invite sent!" : "Failed to send invite. Make sure you are in a valid instance."
                    });
                }
                break;
            }

            case "vrcGetInviteMessages":
            {
                var uid = msg["userId"]?.ToString() ?? _core.VrcApi.CurrentUserId;
                if (!string.IsNullOrEmpty(uid))
                {
                    var msgs = await _core.VrcApi.GetInviteMessagesAsync(uid);
                    _core.SendToJS("vrcInviteMessages", msgs ?? new JArray());
                }
                break;
            }

            case "vrcUpdateInviteMessage":
            {
                var uid = msg["userId"]?.ToString() ?? _core.VrcApi.CurrentUserId;
                var slot = msg["slot"]?.Value<int>() ?? -1;
                var text = msg["message"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(uid) && slot >= 0 && !string.IsNullOrEmpty(text))
                {
                    var (ok, arr, cooldown) = await _core.VrcApi.UpdateInviteMessageAsync(uid, slot, text);
                    if (ok && arr != null)
                        _core.SendToJS("vrcInviteMessages", arr);
                    else
                        _core.SendToJS("vrcInviteMessageUpdateFailed", new { slot, cooldown });
                }
                break;
            }

            case "vrcRequestInvite":
            {
                var uid = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(uid))
                {
                    var ok = await _core.VrcApi.RequestInviteAsync(uid);
                    _core.SendToJS("vrcActionResult", new
                    {
                        action = "requestInvite", success = ok,
                        message = ok ? "Invite request sent!" : "Failed to request invite."
                    });
                }
                break;
            }

            case "vrcUpdateNote":
            {
                var uid = msg["userId"]?.ToString() ?? "";
                var note = msg["note"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(uid))
                {
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.VrcApi.UpdateUserNoteAsync(uid, note);
                        _core.SendToJS("vrcNoteUpdated", new { success = ok, userId = uid, note });
                    });
                }
                break;
            }

            case "vrcBatchInvite":
            {
                var ids = msg["userIds"]?.ToObject<List<string>>() ?? new();
                var locOverride = msg["location"]?.ToString();
                if (ids.Count > 0)
                {
                    _ = Task.Run(async () =>
                    {
                        int done = 0, success = 0, fail = 0;
                        int total = ids.Count;
                        var loc = !string.IsNullOrEmpty(locOverride) ? locOverride : (_core.LogWatcher.CurrentLocation ?? "");
                        foreach (var uid in ids)
                        {
                            var ok = await _core.VrcApi.InviteFriendAsync(uid, loc);
                            done++;
                            if (ok) success++; else fail++;
                            _core.SendToJS("vrcBatchInviteProgress", new { done, total, success, fail });
                            if (done < total) await Task.Delay(1500);
                        }
                    });
                }
                break;
            }

            case "vrcGetFavoriteFriends":
                _ = LoadFavoriteFriendsAsync();
                break;

            case "vrcAddFavoriteFriend":
            {
                var uid = msg["userId"]?.ToString() ?? "";
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var result = await _core.VrcApi.AddFavoriteFriendAsync(uid);
                        if (result == null) return;
                        var fvrtId = result["id"]?.ToString() ?? "";
                        if (string.IsNullOrEmpty(fvrtId)) return;
                        lock (_favoriteFriends) _favoriteFriends[uid] = fvrtId;
                        _core.SendToJS("vrcFavoriteFriendToggled", new { userId = uid, fvrtId, isFavorited = true });
                    }
                    catch { }
                });
                break;
            }

            case "vrcRemoveFavoriteFriend":
            {
                var uid = msg["userId"]?.ToString() ?? "";
                var fvrtId = msg["fvrtId"]?.ToString() ?? "";
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var ok = await _core.VrcApi.RemoveFavoriteFriendAsync(fvrtId);
                        if (!ok) return;
                        lock (_favoriteFriends) _favoriteFriends.Remove(uid);
                        _core.SendToJS("vrcFavoriteFriendToggled", new { userId = uid, fvrtId = "", isFavorited = false });
                    }
                    catch { }
                });
                break;
            }

            case "vrcSendFriendRequest":
            {
                var uid = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(uid))
                {
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.VrcApi.SendFriendRequestAsync(uid);
                        _core.SendToJS("vrcActionResult", new { action = "friendRequest", success = ok,
                            message = ok ? "Friend request sent!" : "Failed to send request" });
                    });
                }
                break;
            }

            case "vrcUnfriend":
            {
                var uid = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(uid))
                {
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.VrcApi.UnfriendAsync(uid);
                        _core.SendToJS("vrcActionResult", new { action = "unfriend", success = ok,
                            message = ok ? "Unfriended" : "Failed to unfriend" });
                        if (ok) _core.SendToJS("vrcUnfriendDone", new { userId = uid });
                    });
                }
                break;
            }

            case "vrcGetBlocked":
                _ = Task.Run(async () =>
                {
                    var arr = await _core.VrcApi.GetPlayerModerationsAsync("block");
                    await EnrichModerationsWithImagesAsync(arr);
                    _core.SendToJS("vrcBlockedList", arr);
                });
                break;

            case "vrcGetMuted":
                _ = Task.Run(async () =>
                {
                    var arr = await _core.VrcApi.GetPlayerModerationsAsync("mute");
                    await EnrichModerationsWithImagesAsync(arr);
                    _core.SendToJS("vrcMutedList", arr);
                });
                break;

            case "vrcBlock":
            {
                var uid = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(uid))
                {
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.VrcApi.ModerateUserAsync(uid, "block");
                        _core.SendToJS("vrcActionResult", new { action = "block", success = ok,
                            message = ok ? "Blocked" : "Failed to block" });
                        if (ok) _core.SendToJS("vrcModDone", new { userId = uid, type = "block", active = true });
                    });
                }
                break;
            }

            case "vrcMute":
            {
                var uid = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(uid))
                {
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.VrcApi.ModerateUserAsync(uid, "mute");
                        _core.SendToJS("vrcActionResult", new { action = "mute", success = ok,
                            message = ok ? "Muted" : "Failed to mute" });
                        if (ok) _core.SendToJS("vrcModDone", new { userId = uid, type = "mute", active = true });
                    });
                }
                break;
            }

            case "vrcUnblock":
            {
                var uid = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(uid))
                {
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.VrcApi.UnmoderateUserAsync(uid, "block");
                        _core.SendToJS("vrcActionResult", new { action = "unblock", success = ok,
                            message = ok ? "Unblocked" : "Failed to unblock" });
                        if (ok) _core.SendToJS("vrcModDone", new { userId = uid, type = "block", active = false });
                    });
                }
                break;
            }

            case "vrcUnmute":
            {
                var uid = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(uid))
                {
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.VrcApi.UnmoderateUserAsync(uid, "mute");
                        _core.SendToJS("vrcActionResult", new { action = "unmute", success = ok,
                            message = ok ? "Unmuted" : "Failed to unmute" });
                        if (ok) _core.SendToJS("vrcModDone", new { userId = uid, type = "mute", active = false });
                    });
                }
                break;
            }

            case "vrcBoop":
            {
                var uid = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(uid))
                {
                    _ = Task.Run(async () =>
                    {
                        var ok = await _core.VrcApi.SendBoopAsync(uid);
                        if (ok)
                        {
                            var entry = StoreChatMessage(uid, "me", "💕 Boop!", "boop");
                            _core.SendToJS("vrcChatMessage", entry);
                        }
                        _core.SendToJS("vrcActionResult", new { action = "boop", success = ok,
                            message = ok ? "Booped!" : "Failed to boop" });
                    });
                }
                break;
            }

            case "vrcSendChatMessage":
            {
                var uid = msg["userId"]?.ToString();
                var text = msg["text"]?.ToString();
                if (!string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(text))
                {
                    _ = Task.Run(async () =>
                    {
                        var (ok, err, slotsUsed) = await _core.VrcApi.SendChatMessageAsync(uid, text);
                        if (ok)
                        {
                            var entry = StoreChatMessage(uid, "me", text);
                            _core.SendToJS("vrcChatMessage", entry);
                        }
                        _core.SendToJS("vrcChatSlotInfo", new { used = slotsUsed, total = 24 });
                        _core.SendToJS("vrcActionResult", new { action = "sendChatMessage", success = ok, message = ok ? "Sent!" : err });
                    });
                }
                break;
            }

            case "vrcGetChatHistory":
            {
                var uid = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(uid))
                {
                    _core.SendToJS("vrcChatHistory", new { userId = uid, messages = GetChatHistory(uid) });
                    _ = Task.Run(async () =>
                    {
                        var (used, total) = await _core.VrcApi.LoadChatSlotStatusAsync();
                        _core.SendToJS("vrcChatSlotInfo", new { used, total });
                    });
                }
                break;
            }

            case "vrcGetUser":
            {
                var uid = msg["userId"]?.ToString();
                if (!string.IsNullOrEmpty(uid))
                {
                    _ = Task.Run(async () =>
                    {
                        var u = await _core.VrcApi.GetUserAsync(uid);
                        if (u != null) _core.SendToJS("vrcUserDetail", new
                        {
                            id = u["id"]?.ToString() ?? "", displayName = u["displayName"]?.ToString() ?? "",
                            image = VRChatApiService.GetUserImage(u), status = u["status"]?.ToString() ?? "offline",
                            statusDescription = u["statusDescription"]?.ToString() ?? "",
                            bio = u["bio"]?.ToString() ?? "", location = u["location"]?.ToString() ?? "",
                            isFriend = u["isFriend"]?.Value<bool>() ?? false,
                            currentAvatarImageUrl = u["currentAvatarImageUrl"]?.ToString() ?? "",
                        });
                    });
                }
                break;
            }
        }
    }

    // Set of actions this controller handles
    private static readonly HashSet<string> _handledActions = new()
    {
        "vrcRefreshFriends", "vrcUpdateStatus", "vrcGetFriendDetail", "vrcJoinFriend",
        "vrcInviteFriend", "vrcInviteFriendWithPhoto", "vrcGetInviteMessages",
        "vrcUpdateInviteMessage", "vrcRequestInvite", "vrcUpdateNote", "vrcBatchInvite",
        "vrcGetFavoriteFriends", "vrcAddFavoriteFriend", "vrcRemoveFavoriteFriend",
        "vrcSendFriendRequest", "vrcUnfriend", "vrcGetBlocked", "vrcGetMuted",
        "vrcBlock", "vrcMute", "vrcUnblock", "vrcUnmute", "vrcBoop",
        "vrcSendChatMessage", "vrcGetChatHistory", "vrcGetUser",
        "vrcGetUserAvatars",
    };

    public static bool HandlesAction(string action) => _handledActions.Contains(action);

    // Core Friend Methods

    public async Task LoadFavoriteFriendsAsync()
    {
        try
        {
            var favs = await _core.VrcApi.GetFavoriteFriendsAsync();
            lock (_favoriteFriends)
            {
                _favoriteFriends.Clear();
                foreach (var fav in favs)
                {
                    var uid = fav["favoriteId"]?.ToString() ?? "";
                    var fvrtId = fav["id"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(fvrtId))
                        _favoriteFriends[uid] = fvrtId;
                }
            }
            var list = favs.Select(f => new
            {
                fvrtId = f["id"]?.ToString() ?? "",
                favoriteId = f["favoriteId"]?.ToString() ?? "",
            }).Where(f => !string.IsNullOrEmpty(f.favoriteId)).ToList();
            _core.SendToJS("vrcFavoriteFriends", list);
        }
        catch { }
    }

    public async Task RefreshFriendsAsync(bool silent = false)
    {
        if (!_core.VrcApi.IsLoggedIn) return;
        if (!await _friendsRefreshLock.WaitAsync(0)) return;
        try
        {
            var online = await _core.VrcApi.GetOnlineFriendsAsync();
            var offline = await _core.VrcApi.GetOfflineFriendsAsync();

            lock (_friendStore)
            {
                var onlineIds = new HashSet<string>(
                    online.Select(f => f["id"]?.ToString() ?? "").Where(id => !string.IsNullOrEmpty(id)));
                foreach (var f in online)
                {
                    var uid = f["id"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(uid)) _friendStore[uid] = f;
                }
                foreach (var f in offline)
                {
                    var uid = f["id"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(uid) || onlineIds.Contains(uid)) continue;
                    var copy = (JObject)f.DeepClone();
                    copy["location"] = "offline";
                    copy["status"] = "offline";
                    _friendStore[uid] = copy;
                }
            }

            var seenIds = new HashSet<string>();
            var onlineList = online.Select(f =>
            {
                var id = f["id"]?.ToString() ?? "";
                seenIds.Add(id);
                var location = f["location"]?.ToString() ?? "";
                var platform = f["platform"]?.ToString() ?? f["last_platform"]?.ToString() ?? "";
                bool isWebPlatform = platform.Equals("web", StringComparison.OrdinalIgnoreCase);
                bool isInGame = !string.IsNullOrEmpty(location) && location != "offline" && location != "" && !isWebPlatform;
                return new
                {
                    id, displayName = f["displayName"]?.ToString() ?? "",
                    image = VRChatApiService.GetUserImage(f),
                    status = f["status"]?.ToString() ?? "offline",
                    statusDescription = f["statusDescription"]?.ToString() ?? "",
                    location, platform,
                    presence = isInGame ? "game" : "web",
                    tags = f["tags"]?.ToObject<List<string>>() ?? new(),
                };
            }).ToList();

            var offlineList = offline
                .Where(f => !seenIds.Contains(f["id"]?.ToString() ?? ""))
                .Select(f => new
                {
                    id = f["id"]?.ToString() ?? "",
                    displayName = f["displayName"]?.ToString() ?? "",
                    image = VRChatApiService.GetUserImage(f),
                    status = "offline",
                    statusDescription = f["statusDescription"]?.ToString() ?? "",
                    location = "offline",
                    platform = f["last_platform"]?.ToString() ?? "",
                    presence = "offline",
                    tags = f["tags"]?.ToObject<List<string>>() ?? new(),
                }).ToList();

            var friendList = onlineList
                .OrderBy(f => f.presence == "game" ? 0 : 1)
                .ThenBy(f => f.status switch { "join me" => 0, "active" => 1, "ask me" => 2, "busy" => 3, _ => 4 })
                .Cast<object>()
                .Concat(offlineList.OrderBy(f => f.displayName).Cast<object>())
                .ToList();

            var counts = new
            {
                game = onlineList.Count(f => f.presence == "game"),
                web = onlineList.Count(f => f.presence == "web"),
                offline = offlineList.Count
            };

            if (!_core.Timeline.KnownUsersSeeded)
            {
                var allIds = online.Select(f => f["id"]?.ToString())
                    .Concat(offline.Select(f => f["id"]?.ToString()))
                    .Where(id => !string.IsNullOrEmpty(id)).Cast<string>().ToList();
                _core.Timeline.SeedKnownUsers(allIds);
            }

            if (!_friendStateSeeded)
            {
                foreach (var f in online)
                {
                    var uid = f["id"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(uid)) continue;
                    _friendLastLoc[uid] = f["location"]?.ToString() ?? "";
                    _friendLastStatus[uid] = f["status"]?.ToString() ?? "";
                    _friendLastStatusDesc[uid] = (f["statusDescription"]?.ToString() ?? "").Trim();
                    _friendLastBio[uid] = (f["bio"]?.ToString() ?? "").Trim();
                    var img0 = VRChatApiService.GetUserImage(f);
                    _friendNameImg[uid] = (f["displayName"]?.ToString() ?? "", img0);
                    if (!string.IsNullOrEmpty(img0)) _core.Timeline.SetUserImage(uid, img0);
                }
                foreach (var f in offline)
                {
                    var uid = f["id"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(uid)) continue;
                    _friendLastLoc[uid] = "offline";
                    _friendLastStatus[uid] = f["status"]?.ToString() ?? "";
                    _friendLastStatusDesc[uid] = (f["statusDescription"]?.ToString() ?? "").Trim();
                    _friendLastBio[uid] = (f["bio"]?.ToString() ?? "").Trim();
                    var img0 = VRChatApiService.GetUserImage(f);
                    _friendNameImg[uid] = (f["displayName"]?.ToString() ?? "", img0);
                    if (!string.IsNullOrEmpty(img0)) _core.Timeline.SetUserImage(uid, img0);
                }
                _friendStateSeeded = true;
            }
            else
            {
                foreach (var f in online.Concat(offline))
                {
                    var uid = f["id"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(uid)) continue;
                    var img = VRChatApiService.GetUserImage(f);
                    if (img.Length > 0)
                    {
                        _friendNameImg[uid] = (f["displayName"]?.ToString() ?? _friendNameImg.GetValueOrDefault(uid).name ?? "", img);
                        _core.Timeline.SetUserImage(uid, img);
                    }
                }
            }

            if (_core.Settings.FfcEnabled) _core.Cache.Save(CacheHandler.KeyFriends, new { friends = friendList, counts });
            _core.SendToJS("vrcFriends", new { friends = friendList, counts });
            if (!silent)
                _core.SendToJS("log", new { msg = $"VRChat: {counts.game} in-game, {counts.web} web, {counts.offline} offline", color = "ok" });

            // Proactively resolve world info for in-game friends
            var inGameWorldIds = online
                .Select(f => f["location"]?.ToString() ?? "")
                .Where(l => l.Contains(':'))
                .Select(l => l.Split(':')[0])
                .Where(id => id.StartsWith("wrld_"))
                .Distinct().ToList();
            if (inGameWorldIds.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var tasks = inGameWorldIds.Select(async wid =>
                        {
                            try
                            {
                                var world = await _core.VrcApi.GetWorldAsync(wid);
                                if (world == null) return (wid, null as object);
                                return (wid, (object)new
                                {
                                    name = world["name"]?.ToString() ?? "",
                                    thumbnailImageUrl = world["thumbnailImageUrl"]?.ToString() ?? "",
                                    imageUrl = world["imageUrl"]?.ToString() ?? ""
                                });
                            }
                            catch { return (wid, null as object); }
                        });
                        var results = await Task.WhenAll(tasks);
                        var dict = results.Where(r => r.Item2 != null).ToDictionary(r => r.wid, r => r.Item2!);
                        if (dict.Count > 0)
                        {
                            _core.SendToJS("vrcWorldsResolved", dict);
#if WINDOWS
                            foreach (var (wid, wobj) in dict)
                            {
                                var jo = JObject.FromObject(wobj);
                                var wname = jo["name"]?.ToString() ?? "";
                                var wthumb = _core.ImgCache?.GetWorld(jo["thumbnailImageUrl"]?.ToString()) ?? jo["thumbnailImageUrl"]?.ToString() ?? "";
                                lock (_core.VrWorldCache) _core.VrWorldCache[wid] = (wname, wthumb);
                            }
                            PushVroLocations();
#endif
                        }
                    }
                    catch { }
                });
            }

            // Time tracking
            try
            {
                var myLoc = (_core.IsVrcRunning?.Invoke() ?? false) ? _core.LogWatcher.CurrentLocation : null;
                _core.TimeTracker.SetMyLocation(myLoc ?? "");
                var trackData = onlineList.Select(f => (userId: f.id, location: f.location, presence: f.presence)).ToList();

                if (!string.IsNullOrEmpty(myLoc) && myLoc != "offline" && myLoc != "private" && myLoc != "traveling")
                {
                    var logPlayers = _core.LogWatcher.GetCurrentPlayers();
                    var logPlayerIds = new HashSet<string>(
                        logPlayers.Where(p => !string.IsNullOrEmpty(p.UserId)).Select(p => p.UserId));
                    for (int i = 0; i < trackData.Count; i++)
                    {
                        var t = trackData[i];
                        if (t.location == "private" && logPlayerIds.Contains(t.userId))
                            trackData[i] = (t.userId, myLoc, t.presence);
                    }
                    var trackedIds = new HashSet<string>(trackData.Select(t => t.userId));
                    foreach (var p in logPlayers)
                    {
                        if (!string.IsNullOrEmpty(p.UserId) && !trackedIds.Contains(p.UserId))
                            trackData.Add((userId: p.UserId, location: myLoc, presence: "game"));
                    }
                }

                _core.TimeTracker.Tick(trackData);
                _core.TimeTracker.Save();

                var (myWorldId, _, _) = VRChatApiService.ParseLocation(myLoc);
                if (!string.IsNullOrEmpty(myWorldId) && myWorldId.StartsWith("wrld_"))
                {
                    _core.WorldTimeTracker.Tick();
                    _core.WorldTimeTracker.Save();
                }
            }
            catch { }
        }
        catch (Exception ex)
        {
            if (!silent)
                _core.SendToJS("log", new { msg = $"VRChat: Friends error — {ex.Message}", color = "err" });
        }
        finally
        {
            _friendsRefreshLock.Release();
        }
    }

    public async Task UpdateStatusAsync(string status, string statusDescription)
    {
        if (!_core.VrcApi.IsLoggedIn) return;
        var user = await _core.VrcApi.UpdateStatusAsync(status, statusDescription);
        if (user != null)
        {
            _core.SendToJS("log", new { msg = $"VRChat: Status updated to {status}", color = "ok" });
        }
        else
        {
            _core.SendToJS("log", new { msg = "VRChat: Failed to update status", color = "err" });
        }
    }

    public async Task EnrichModerationsWithImagesAsync(JArray entries)
    {
        var tasks = entries.OfType<JObject>().Select(async entry =>
        {
            var uid = entry["targetUserId"]?.ToString();
            if (string.IsNullOrEmpty(uid)) return;
            var user = await _core.VrcApi.GetUserAsync(uid);
            if (user != null) entry["image"] = VRChatApiService.GetUserImage(user);
        });
        await Task.WhenAll(tasks);
    }

    // Live Friend Store

    public void MergeFriendStore(string userId, JObject? userObj,
        string? location = null, string? platform = null, bool wentOffline = false)
    {
        if (string.IsNullOrEmpty(userId)) return;
        lock (_friendStore)
        {
            if (!_friendStore.TryGetValue(userId, out var entry))
            { entry = new JObject(); _friendStore[userId] = entry; }
            if (userObj != null)
            {
                foreach (var prop in userObj.Properties()) entry[prop.Name] = prop.Value;
                var img = VRChatApiService.GetUserImage(userObj);
                if (!string.IsNullOrEmpty(img))
                    _friendNameImg[userId] = (userObj["displayName"]?.ToString() ?? _friendNameImg.GetValueOrDefault(userId).name ?? "", img);
            }
            if (location != null) entry["location"] = location;
            if (platform != null) entry["last_platform"] = platform;
            if (wentOffline)
            {
                entry["location"] = "offline";
                entry["status"] = "offline";
            }
        }
    }

    public void PushFriendsFromStore()
    {
        List<JObject> snapshot;
        lock (_friendStore) snapshot = _friendStore.Values.ToList();

        var list = snapshot.Select(f =>
        {
            var location = f["location"]?.ToString() ?? "";
            var platform = f["last_platform"]?.ToString() ?? f["platform"]?.ToString() ?? "";
            bool isWebPlatform = platform.Equals("web", StringComparison.OrdinalIgnoreCase);
            bool isInGame = !string.IsNullOrEmpty(location) && location != "offline" && location != "" && !isWebPlatform;
            var status = f["status"]?.ToString() ?? "offline";
            var presence = (location == "offline" && status == "offline") ? "offline" : isInGame ? "game" : "web";
            return new
            {
                id = f["id"]?.ToString() ?? "",
                displayName = f["displayName"]?.ToString() ?? "",
                image = VRChatApiService.GetUserImage(f),
                status, statusDescription = f["statusDescription"]?.ToString() ?? "",
                location, platform, presence,
                tags = f["tags"]?.ToObject<List<string>>() ?? new List<string>(),
                ageVerified = f["ageVerified"]?.Value<bool>() ?? false,
            };
        })
        .OrderBy(f => f.presence switch { "game" => 0, "web" => 1, _ => 2 })
        .ThenBy(f => f.status switch { "join me" => 0, "active" => 1, "ask me" => 2, "busy" => 3, _ => 4 })
        .ThenBy(f => f.displayName)
        .ToList();

        var counts = new
        {
            game = list.Count(f => f.presence == "game"),
            web = list.Count(f => f.presence == "web"),
            offline = list.Count(f => f.presence == "offline"),
        };

        _core.SendToJS("vrcFriends", new { friends = list, counts });

#if WINDOWS
        if (_core.VrOverlay != null) PushVroLocations();
#endif
    }

#if WINDOWS
    public void PushVroLocations()
    {
        if (_core.VrOverlay == null) return;
        List<JObject> snapshot;
        lock (_friendStore) snapshot = _friendStore.Values.ToList();

        var entries = snapshot
            .Where(f =>
            {
                var loc = f["location"]?.ToString() ?? "";
                return loc.Contains(':') && loc.Split(':')[0].StartsWith("wrld_");
            })
            .Select(f =>
            {
                var loc = f["location"]?.ToString() ?? "";
                var wid = loc.Split(':')[0];
                var iid = loc.Contains(':') ? loc.Split(':', 2)[1].Split('~')[0] : "";
                (string name, string thumb) world = ("", "");
                lock (_core.VrWorldCache) _core.VrWorldCache.TryGetValue(wid, out world);
                var rawFriendImg = VRChatApiService.GetUserImage(f);
                return (
                    worldId: wid, instanceId: iid,
                    worldName: world.name,
                    worldImageUrl: _core.ImgCache?.GetWorld(world.thumb) ?? world.thumb,
                    friendId: f["id"]?.ToString() ?? "",
                    friendName: f["displayName"]?.ToString() ?? "",
                    friendImageUrl: _core.ImgCache?.Get(rawFriendImg) ?? rawFriendImg,
                    location: loc
                );
            })
            .ToList();

        _core.VrOverlay.SetFriendLocations(entries);
    }
#endif

    // Friend Detail

    public async Task GetFriendDetailAsync(string userId)
    {
        if (!_core.VrcApi.IsLoggedIn) return;

        bool isFriend;
        lock (_friendStore) isFriend = _friendStore.ContainsKey(userId);

        var diskCached = (_core.Settings.FfcEnabled && isFriend) ? _core.Cache.LoadRaw(CacheHandler.KeyUserProfile(userId)) : null;
        if (diskCached is JObject diskProfile)
        {
            JObject? live;
            lock (_friendStore) _friendStore.TryGetValue(userId, out live);
            diskProfile["status"] = live?["status"]?.ToString() ?? "offline";
            diskProfile["statusDescription"] = live?["statusDescription"]?.ToString() ?? "";
            diskProfile["location"] = live?["location"]?.ToString() ?? "";
            diskProfile["worldName"] = "";
            diskProfile["worldThumb"] = "";
            diskProfile["instanceType"] = "";
            diskProfile["userCount"] = 0;
            diskProfile["worldCapacity"] = 0;
            diskProfile["canJoin"] = false;
            diskProfile["canRequestInvite"] = false;
            diskProfile["inSameInstance"] = false;
            diskProfile["travelingToLocation"] = "";
            var _liveStatus = live?["status"]?.ToString() ?? "offline";
            var _liveLoc = live?["location"]?.ToString() ?? "";
            bool _liveInGame = !string.IsNullOrEmpty(_liveLoc) && _liveLoc != "offline";
            diskProfile["state"] = (_liveStatus != "offline" && !_liveInGame) ? "active" : "";
            _core.SendToJS("vrcFriendDetail", diskProfile);

            bool startRefresh;
            lock (_profileRefreshInFlight) startRefresh = _profileRefreshInFlight.Add(userId);
            if (startRefresh)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var fresh = await BuildUserDetailPayloadAsync(userId);
                        if (fresh == null) return;
                        if (_core.Settings.FfcEnabled) _core.Cache.Save(CacheHandler.KeyUserProfile(userId), fresh);
                        _core.SendToJS("vrcFriendDetail", fresh);
                    }
                    catch { }
                    finally { lock (_profileRefreshInFlight) _profileRefreshInFlight.Remove(userId); }
                });
            }
            return;
        }

        try
        {
            var payload = await BuildUserDetailPayloadAsync(userId);
            if (payload == null)
            {
                _core.SendToJS("vrcFriendDetailError", new { error = "Could not load user profile" });
                return;
            }
            if (_core.Settings.FfcEnabled && isFriend) _core.Cache.Save(CacheHandler.KeyUserProfile(userId), payload);
            _core.SendToJS("vrcFriendDetail", payload);
        }
        catch (Exception ex)
        {
            _core.SendToJS("vrcFriendDetailError", new { error = ex.Message });
            _core.SendToJS("log", new { msg = $"VRChat: Error loading profile — {ex.Message}", color = "err" });
        }
    }

    public async Task<object?> BuildUserDetailPayloadAsync(string userId)
    {
        JObject? user;
        JObject? storeSnapshot;
        lock (_friendStore) _friendStore.TryGetValue(userId, out storeSnapshot);

        user = storeSnapshot;
        if (user == null || user["badges"] == null)
        {
            var fresh = await _core.VrcApi.GetUserAsync(userId);
            if (fresh != null) user = fresh;
            else if (user == null) return null;
        }
        var _freshImg = VRChatApiService.GetUserImage(user);
        if (!string.IsNullOrEmpty(_freshImg)) _core.Timeline.SetUserImage(userId, _freshImg);

        if (storeSnapshot != null)
        {
            var liveStatus = storeSnapshot["status"]?.ToString();
            var liveLoc = storeSnapshot["location"]?.ToString();
            if (!string.IsNullOrEmpty(liveStatus)) user["status"] = liveStatus;
            if (liveLoc != null) user["location"] = liveLoc;
        }

        var location = user["location"]?.ToString() ?? "private";
        var (worldId, instanceId, instanceType) = VRChatApiService.ParseLocation(location);
        bool hasWorld = !string.IsNullOrEmpty(worldId) && worldId.StartsWith("wrld_");

        var instTask = hasWorld ? _core.VrcApi.GetInstanceAsync(location) : Task.FromResult<JObject?>(null);
        var grpsTask = _core.VrcApi.GetUserGroupsByIdAsync(userId);
        var worldsTask = _core.VrcApi.GetUserWorldsAsync(userId);
        var mutualsTask = _core.VrcApi.GetUserMutualsAsync(userId);

        await Task.WhenAll(new Task[] { instTask, grpsTask, worldsTask, mutualsTask }
            .Select(t => t.ContinueWith(_ => { })));

        var inst = instTask.IsCompletedSuccessfully ? instTask.Result : null;
        var groups = grpsTask.IsCompletedSuccessfully ? grpsTask.Result : new JArray();
        var worlds = worldsTask.IsCompletedSuccessfully ? worldsTask.Result : new JArray();
        var (mutualsArr, mutualsOptedOut) = mutualsTask.IsCompletedSuccessfully
            ? mutualsTask.Result : (new JArray(), false);
        var badgesArr = user["badges"] as JArray ?? new JArray();

        if (instanceType == "private" && inst?["canRequestInvite"]?.Value<bool>() == true)
            instanceType = "invite_plus";

        var instWorld = inst?["world"] as JObject;
        string worldName = instWorld?["name"]?.ToString() ?? "";
        string worldThumb = _core.ImgCache?.GetWorld(instWorld?["thumbnailImageUrl"]?.ToString()) ?? instWorld?["thumbnailImageUrl"]?.ToString() ?? "";
        int worldCapacity = instWorld?["capacity"]?.Value<int>() ?? inst?["capacity"]?.Value<int>() ?? 0;
        int userCount = inst?["n_users"]?.Value<int>() ?? inst?["userCount"]?.Value<int>() ?? 0;
        string userNote = user["note"]?.ToString() ?? "";

        bool canJoin = instanceType is "public" or "friends" or "friends+" or "hidden"
            or "group-public" or "group-plus" or "group-members" or "group";
        bool canRequestInvite = instanceType is "private" or "invite_plus";
        bool isInWorld = !string.IsNullOrEmpty(worldId) && location != "private" && location != "offline" && location != "traveling";

        object? representedGroup = null;
        var repGroup = groups.OfType<JObject>().FirstOrDefault(g => g["isRepresenting"]?.Value<bool>() == true);
        if (repGroup != null && !string.IsNullOrEmpty(repGroup["groupId"]?.ToString() ?? repGroup["id"]?.ToString()))
        {
            representedGroup = new
            {
                id = repGroup["groupId"]?.ToString() ?? repGroup["id"]?.ToString() ?? "",
                name = repGroup["name"]?.ToString() ?? "",
                shortCode = repGroup["shortCode"]?.ToString() ?? "",
                discriminator = repGroup["discriminator"]?.ToString() ?? "",
                iconUrl = repGroup["iconUrl"]?.ToString() ?? "",
                bannerUrl = repGroup["bannerUrl"]?.ToString() ?? "",
                memberCount = repGroup["memberCount"]?.Value<int>() ?? 0,
            };
        }

        List<object> userGroups = new();
        foreach (var g in groups)
        {
            var gid = g["groupId"]?.ToString() ?? g["id"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(gid)) continue;
            userGroups.Add(new
            {
                id = gid, name = g["name"]?.ToString() ?? "",
                shortCode = g["shortCode"]?.ToString() ?? "",
                discriminator = g["discriminator"]?.ToString() ?? "",
                iconUrl = g["iconUrl"]?.ToString() ?? g["iconId"]?.ToString() ?? "",
                bannerUrl = g["bannerUrl"]?.ToString() ?? "",
                memberCount = g["memberCount"]?.Value<int>() ?? 0,
                isRepresenting = g["isRepresenting"]?.Value<bool>() ?? false,
            });
        }

        List<object> userWorlds = new();
        foreach (var w in worlds)
        {
            if (w is not JObject wObj) continue;
            userWorlds.Add(new
            {
                id = wObj["id"]?.ToString() ?? "", name = wObj["name"]?.ToString() ?? "",
                thumbnailImageUrl = wObj["thumbnailImageUrl"]?.ToString() ?? "",
                occupants = wObj["occupants"]?.Value<int>() ?? 0,
                favorites = wObj["favorites"]?.Value<int>() ?? 0,
                visits = wObj["visits"]?.Value<int>() ?? 0,
            });
        }

        List<object> mutualsList = new();
        foreach (var mu in mutualsArr)
        {
            if (mu is not JObject muObj) continue;
            var muId = muObj["id"]?.ToString() ?? "";
            var muImage = (_friendNameImg.TryGetValue(muId, out var muFi) && !string.IsNullOrEmpty(muFi.image))
                ? muFi.image : VRChatApiService.GetUserImage(muObj);
            var muLocation = muObj["location"]?.ToString() ?? "";
            var muStatus = muObj["status"]?.ToString() ?? "offline";
            bool muIsInGame = !string.IsNullOrEmpty(muLocation) && muLocation != "offline" && muLocation != "private" && muLocation != "traveling";
            bool muIsOffline = muStatus == "offline" || muLocation == "offline";
            mutualsList.Add(new
            {
                id = muObj["id"]?.ToString() ?? "",
                displayName = muObj["displayName"]?.ToString() ?? "",
                image = muImage, status = muStatus,
                statusDescription = muObj["statusDescription"]?.ToString() ?? "",
                presence = muIsOffline ? "offline" : muIsInGame ? "game" : "web",
            });
        }

        List<object> badges = new();
        foreach (var b in badgesArr)
        {
            if (b is not JObject bObj) continue;
            var imageUrl = bObj["badgeImageUrl"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(imageUrl)) continue;
            badges.Add(new
            {
                id = bObj["badgeId"]?.ToString() ?? "",
                name = bObj["badgeName"]?.ToString() ?? "",
                description = bObj["badgeDescription"]?.ToString() ?? "",
                imageUrl, showcased = bObj["showcased"]?.Value<bool>() ?? false,
            });
        }

        var isCoPresent = _core.LogWatcher.GetCurrentPlayers().Any(p => p.UserId == userId);
        var (totalSeconds, lastSeenLocal) = _core.TimeTracker.GetUserStats(userId, isCoPresent);

        return new
        {
            id = user["id"]?.ToString() ?? "",
            displayName = user["displayName"]?.ToString() ?? "",
            image = VRChatApiService.GetUserImage(user),
            status = user["status"]?.ToString() ?? "offline",
            statusDescription = user["statusDescription"]?.ToString() ?? "",
            bio = user["bio"]?.ToString() ?? "",
            lastLogin = user["last_login"]?.ToString() ?? "",
            dateJoined = user["date_joined"]?.ToString() ?? "",
            location, worldName, worldThumb, instanceType, userCount, worldCapacity,
            isFriend = user["isFriend"]?.Value<bool>() ?? !string.IsNullOrEmpty(user["friendKey"]?.ToString()),
            canJoin = isInWorld && canJoin, canRequestInvite, canInvite = true,
            currentAvatarImageUrl = _core.ImgCache?.Get(user["currentAvatarImageUrl"]?.ToString() ?? "") ?? user["currentAvatarImageUrl"]?.ToString() ?? "",
            profilePicOverride = _core.ImgCache?.Get(user["profilePicOverride"]?.ToString() ?? "") ?? user["profilePicOverride"]?.ToString() ?? "",
            tags = user["tags"]?.ToObject<List<string>>() ?? new(),
            note = user["note"]?.ToString() ?? "",
            friendKey = user["friendKey"]?.ToString() ?? "",
            travelingToLocation = user["travelingToLocation"]?.ToString() ?? "",
            state = user["state"]?.ToString() ?? "",
            lastPlatform = user["last_platform"]?.ToString() ?? "",
            platform = user["platform"]?.ToString() ?? "",
            userNote, totalTimeSeconds = totalSeconds,
            inSameInstance = _core.LogWatcher.GetCurrentPlayers().Any(p => p.UserId == userId),
            lastSeenTracked = lastSeenLocal,
            pronouns = user["pronouns"]?.ToString() ?? "",
            ageVerificationStatus = user["ageVerificationStatus"]?.ToString() ?? "",
            ageVerified = user["ageVerified"]?.Value<bool>() ?? false,
            representedGroup, userGroups, mutuals = mutualsList, mutualsOptedOut, userWorlds,
            bioLinks = user["bioLinks"]?.ToObject<List<string>>() ?? new List<string>(),
            isFavorited = _favoriteFriends.ContainsKey(userId),
            favFriendId = _favoriteFriends.GetValueOrDefault(userId, ""),
            badges,
        };
    }

    // Join Friend

    private async Task HandleJoinFriendAsync(string joinLoc)
    {
        if (_core.IsVrcRunning?.Invoke() ?? false)
        {
            var ok = await _core.VrcApi.InviteSelfAsync(joinLoc);
            if (ok)
            {
                _core.SendToJS("vrcActionResult", new { action = "join", success = true,
                    message = "Self-invite sent! Check VRChat." });
            }
            else
            {
                try
                {
                    var launchUri = VRChatApiService.BuildLaunchUri(joinLoc);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = launchUri, UseShellExecute = true
                    });
                    _core.SendToJS("vrcActionResult", new { action = "join", success = true,
                        message = "Launching VRChat to join world..." });
                    _core.SendToJS("log", new { msg = "Launched via vrchat:// protocol", color = "ok" });
                }
                catch (Exception ex)
                {
                    _core.SendToJS("vrcActionResult", new { action = "join", success = false,
                        message = "Failed to join. Is VRChat running?" });
                    _core.SendToJS("log", new { msg = $"Launch fallback failed: {ex.Message}", color = "err" });
                }
            }
        }
        else
        {
            _core.SendToJS("vrcLaunchNeeded", new { location = joinLoc, steamVr = _core.IsSteamVrRunning?.Invoke() ?? false });
        }
    }

    // WebSocket Event Handlers

    private void OnWsFriendLocation(object? sender, FriendEventArgs e)
    {
        if (string.IsNullOrEmpty(e.UserId) || !_friendStateSeeded) return;

        var loc = e.Location ?? "";

        // friend-location can fire with location="offline" or location="" (pseudo-null).
        // Do NOT overwrite the store with these non-location values.
        if (loc == "offline" || loc == "") return;

        MergeFriendStore(e.UserId, e.User, location: loc,
            platform: string.IsNullOrEmpty(e.Platform) ? null : e.Platform);
        PushFriendsFromStore();

        if (e.User != null)
            _friendNameImg[e.UserId] = (
                e.User["displayName"]?.ToString() ?? _friendNameImg.GetValueOrDefault(e.UserId).name ?? "",
                VRChatApiService.GetUserImage(e.User).Length > 0
                    ? VRChatApiService.GetUserImage(e.User)
                    : _friendNameImg.GetValueOrDefault(e.UserId).image ?? ""
            );

        var newLoc = e.Location;
        var worldId = newLoc.Contains(':') ? newLoc.Split(':')[0] : newLoc;
        if (!worldId.StartsWith("wrld_")) { _friendLastLoc[e.UserId] = newLoc; return; }

        var oldLoc = _friendLastLoc.GetValueOrDefault(e.UserId, "");
        var oldWorldId = oldLoc.Contains(':') ? oldLoc.Split(':')[0] : oldLoc;
        if (oldLoc == newLoc || oldWorldId == worldId) { _friendLastLoc[e.UserId] = newLoc; return; }

        _friendLastLoc[e.UserId] = newLoc;

        var (fname, fimg) = _friendNameImg.GetValueOrDefault(e.UserId, ("", ""));
        var fev = new TimelineService.FriendTimelineEvent
        {
            Type = "friend_gps", FriendId = e.UserId, FriendName = fname,
            FriendImage = fimg, WorldId = worldId, Location = newLoc,
        };
        _core.Timeline.AddFriendEvent(fev);
        _core.SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev));

        var evId = fev.Id;
        _ = Task.Run(async () =>
        {
            try
            {
                var world = await _core.VrcApi.GetWorldAsync(worldId);
                if (world == null) return;
                var wname = world["name"]?.ToString() ?? "";
                var wthumb = _core.ImgCache?.GetWorld(world["thumbnailImageUrl"]?.ToString()) ?? world["thumbnailImageUrl"]?.ToString() ?? "";
                _core.Timeline.UpdateFriendEventWorld(evId, wname, wthumb);
                var updated = _core.Timeline.GetFriendEvents().FirstOrDefault(x => x.Id == evId);
                if (updated != null)
                    _core.SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(updated));
#if WINDOWS
                lock (_core.VrWorldCache) _core.VrWorldCache[worldId] = (wname, wthumb);
                PushVroLocations();
#endif
            }
            catch { }
        });
    }

    private void OnWsFriendActive(object? sender, FriendEventArgs e)
    {
        // friend-active = website/app activity, NOT in-game.
        if (string.IsNullOrEmpty(e.UserId) || !_friendStateSeeded) return;

        // Detect "left the game": if friend was previously in a world, they just exited
        var prevLoc = _friendLastLoc.GetValueOrDefault(e.UserId, "");
        bool wasInGame = !string.IsNullOrEmpty(prevLoc) && prevLoc != "offline" && prevLoc != "";

        MergeFriendStore(e.UserId, e.User,
            location: string.IsNullOrEmpty(e.Location) ? "" : e.Location,
            platform: string.IsNullOrEmpty(e.Platform) ? null : e.Platform);
        PushFriendsFromStore();

        var (fname, fimg) = _friendNameImg.GetValueOrDefault(e.UserId, ("", ""));
        if (e.User != null)
        {
            fname = e.User["displayName"]?.ToString() ?? fname;
            var img = VRChatApiService.GetUserImage(e.User);
            if (img.Length > 0) fimg = img;
            _friendNameImg[e.UserId] = (fname, fimg);
        }

        // Left the game → Offline (Game)
        if (wasInGame)
        {
            _friendLastLoc[e.UserId] = "";
            var fev = new TimelineService.FriendTimelineEvent
            {
                Type = "friend_offline", FriendId = e.UserId, FriendName = fname, FriendImage = fimg,
            };
            _core.Timeline.AddFriendEvent(fev);
            _core.SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev));
        }

        // friend-active only updates friendslist (dot→circle), no timeline event for web.
    }

    private void OnWsFriendOffline(object? sender, FriendEventArgs e)
    {
        if (string.IsNullOrEmpty(e.UserId) || !_friendStateSeeded) return;

        MergeFriendStore(e.UserId, null, wentOffline: true);
        PushFriendsFromStore();

        var (fname, fimg) = _friendNameImg.GetValueOrDefault(e.UserId, ("", ""));
        if (e.User != null)
        {
            fname = e.User["displayName"]?.ToString() ?? fname;
            var img = VRChatApiService.GetUserImage(e.User);
            if (img.Length > 0) fimg = img;
            _friendNameImg[e.UserId] = (fname, fimg);
        }

        var prevLoc = _friendLastLoc.GetValueOrDefault(e.UserId, "");
        bool wasInGame = !string.IsNullOrEmpty(prevLoc) && prevLoc != "offline" && prevLoc != "";
        _friendLastLoc[e.UserId] = "offline";

        // Only log game offline, not web offline
        if (!wasInGame) return;

        var fev = new TimelineService.FriendTimelineEvent
        {
            Type = "friend_offline", FriendId = e.UserId, FriendName = fname, FriendImage = fimg,
        };
        _core.Timeline.AddFriendEvent(fev);
        _core.SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev));
    }

    private void OnWsFriendOnline(object? sender, FriendEventArgs e)
    {
        if (string.IsNullOrEmpty(e.UserId) || !_friendStateSeeded) return;

        MergeFriendStore(e.UserId, e.User,
            location: string.IsNullOrEmpty(e.Location) ? "" : e.Location,
            platform: string.IsNullOrEmpty(e.Platform) ? null : e.Platform);
        PushFriendsFromStore();

        var fname = "";
        var fimg = "";
        if (e.User != null)
        {
            fname = e.User["displayName"]?.ToString() ?? "";
            fimg = VRChatApiService.GetUserImage(e.User);
            _friendNameImg[e.UserId] = (fname, fimg);
        }
        else
        {
            (fname, fimg) = _friendNameImg.GetValueOrDefault(e.UserId, ("", ""));
        }

        // Skip duplicate "Came Online" if the friend is already known as in-game.
        // This happens after WebSocket reconnects (re-sends friend-online for all online friends).
        var prevLoc = _friendLastLoc.GetValueOrDefault(e.UserId, "");
        bool alreadyInGame = !string.IsNullOrEmpty(prevLoc) && prevLoc != "offline" && prevLoc != "";

        var onlineLoc = e.Location ?? "";
        _friendLastLoc[e.UserId] = (string.IsNullOrEmpty(onlineLoc) && prevLoc.StartsWith("wrld_")) ? prevLoc : onlineLoc;

        if (alreadyInGame) return; // already online, don't spam timeline

        var fev = new TimelineService.FriendTimelineEvent
        {
            Type = "friend_online", FriendId = e.UserId, FriendName = fname, FriendImage = fimg,
        };
        _core.Timeline.AddFriendEvent(fev);
        _core.SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev));
    }

    private void OnWsFriendUpdated(object? sender, FriendEventArgs e)
    {
        if (e.User == null || string.IsNullOrEmpty(e.UserId) || !_friendStateSeeded) return;

        MergeFriendStore(e.UserId, e.User);
        PushFriendsFromStore();

        var fname = e.User["displayName"]?.ToString() ?? _friendNameImg.GetValueOrDefault(e.UserId).name ?? "";
        var fimg = VRChatApiService.GetUserImage(e.User);
        if (fimg.Length == 0) fimg = _friendNameImg.GetValueOrDefault(e.UserId).image ?? "";
        _friendNameImg[e.UserId] = (fname, fimg);

        var newStatus = e.User["status"]?.ToString() ?? "";
        var newStatusDesc = (e.User["statusDescription"]?.ToString() ?? "").Trim();
        var newBio = (e.User["bio"]?.ToString() ?? "").Trim();

        if (!string.IsNullOrEmpty(newStatus))
        {
            var oldStatus = _friendLastStatus.GetValueOrDefault(e.UserId, "");
            if (oldStatus != newStatus && !string.IsNullOrEmpty(oldStatus))
            {
                var fev = new TimelineService.FriendTimelineEvent
                {
                    Type = "friend_status", FriendId = e.UserId, FriendName = fname,
                    FriendImage = fimg, OldValue = oldStatus, NewValue = newStatus,
                };
                _core.Timeline.AddFriendEvent(fev);
                _core.SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev));
            }
            _friendLastStatus[e.UserId] = newStatus;
        }

        var oldStatusDesc = _friendLastStatusDesc.GetValueOrDefault(e.UserId, "");
        if (oldStatusDesc != newStatusDesc && !string.IsNullOrEmpty(oldStatusDesc))
        {
            var fev = new TimelineService.FriendTimelineEvent
            {
                Type = "friend_statusdesc", FriendId = e.UserId, FriendName = fname,
                FriendImage = fimg, OldValue = oldStatusDesc, NewValue = newStatusDesc,
            };
            _core.Timeline.AddFriendEvent(fev);
            _core.SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev));
        }
        _friendLastStatusDesc[e.UserId] = newStatusDesc;

        var oldBio = _friendLastBio.GetValueOrDefault(e.UserId, "");
        if (!string.IsNullOrEmpty(newBio) && oldBio != newBio && !string.IsNullOrEmpty(oldBio))
        {
            var fev = new TimelineService.FriendTimelineEvent
            {
                Type = "friend_bio", FriendId = e.UserId, FriendName = fname,
                FriendImage = fimg,
                OldValue = oldBio.Length > 500 ? oldBio[..500] : oldBio,
                NewValue = newBio.Length > 500 ? newBio[..500] : newBio,
            };
            _core.Timeline.AddFriendEvent(fev);
            _core.SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev));
        }
        if (!string.IsNullOrEmpty(newBio))
            _friendLastBio[e.UserId] = newBio;
    }

    private void OnWsFriendAdded(object? sender, FriendEventArgs e)
    {
        if (string.IsNullOrEmpty(e.UserId) || !_friendStateSeeded) return;

        var fname = "";
        var fimg = "";
        if (e.User != null)
        {
            fname = e.User["displayName"]?.ToString() ?? "";
            fimg = VRChatApiService.GetUserImage(e.User);
            _friendNameImg[e.UserId] = (fname, fimg);
        }

        var fev = new TimelineService.FriendTimelineEvent
        {
            Type = "friend_added", FriendId = e.UserId, FriendName = fname, FriendImage = fimg,
        };
        _core.Timeline.AddFriendEvent(fev);
        _core.SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev));
    }

    private void OnWsFriendRemoved(object? sender, FriendEventArgs e)
    {
        if (string.IsNullOrEmpty(e.UserId) || !_friendStateSeeded) return;

        // Grab name/image before they're removed from the store
        var (fname, fimg) = _friendNameImg.GetValueOrDefault(e.UserId, ("", ""));

        // If we don't have a name, try the friend store
        if (string.IsNullOrEmpty(fname) && _friendStore.TryGetValue(e.UserId, out var stored))
            fname = stored["displayName"]?.ToString() ?? e.UserId;

        // Clean up tracking dictionaries
        _friendStore.Remove(e.UserId);
        _friendLastLoc.Remove(e.UserId);
        _friendLastStatus.Remove(e.UserId);
        _friendLastStatusDesc.Remove(e.UserId);
        _friendLastBio.Remove(e.UserId);
        PushFriendsFromStore();

        var fev = new TimelineService.FriendTimelineEvent
        {
            Type = "friend_removed", FriendId = e.UserId, FriendName = fname, FriendImage = fimg,
        };
        _core.Timeline.AddFriendEvent(fev);
        _core.SendToJS("friendTimelineEvent", BuildFriendTimelinePayload(fev));
    }

    // Friend Timeline Payload

    public object BuildFriendTimelinePayload(TimelineService.FriendTimelineEvent ev) => new
    {
        id = ev.Id, type = ev.Type, timestamp = ev.Timestamp,
        friendId = ev.FriendId, friendName = ev.FriendName,
        friendImage = _core.ResolveAndCache(ResolvePlayerImage(ev.FriendId, ev.FriendImage)),
        worldId = ev.WorldId, worldName = ev.WorldName,
        worldThumb = _core.ResolveAndCache(ev.WorldThumb, longTtl: true),
        location = ev.Location, oldValue = ev.OldValue, newValue = ev.NewValue,
    };

    // Chat Storage

    private static string ChatFile(string userId) =>
        Path.Combine(_chatDir, $"chat_{userId}.json");

    public List<ChatEntry> GetChatHistory(string userId)
    {
        try
        {
            var file = ChatFile(userId);
            if (!File.Exists(file)) return [];
            var json = File.ReadAllText(file);
            return JsonConvert.DeserializeObject<List<ChatEntry>>(json) ?? [];
        }
        catch { return []; }
    }

    public ChatEntry StoreChatMessage(string userId, string from, string text, string? type = null)
    {
        var entry = new ChatEntry(Guid.NewGuid().ToString(), from, text, DateTime.UtcNow.ToString("o"), type);
        try
        {
            Directory.CreateDirectory(_chatDir);
            var history = GetChatHistory(userId);
            history.Add(entry);
            if (history.Count > 500) history = history[^500..];
            File.WriteAllText(ChatFile(userId), JsonConvert.SerializeObject(history));
        }
        catch { }
        return entry;
    }
}
