using Microsoft.Data.Sqlite;
using NativeFileDialogSharp;
using Newtonsoft.Json.Linq;
using VRCNext.Services;

namespace VRCNext;

public class TimelineController
{
    private readonly CoreLibrary _core;
    private readonly FriendsController _friends;
    private readonly InstanceController _instance;
    private readonly PhotosController _photos;

    // VRCX import — path retained between preview and start
    private string _vrcxImportPath = "";

    // Cancellation tokens for in-flight enrichment
    private CancellationTokenSource _tlFetchCts  = new();
    private CancellationTokenSource _ftlFetchCts = new();

    public TimelineController(
        CoreLibrary core,
        FriendsController friends,
        InstanceController instance,
        PhotosController photos)
    {
        _core     = core;
        _friends  = friends;
        _instance = instance;
        _photos   = photos;
    }

    // Message Dispatch

    public async Task HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "importVrcxSelect":
                _ = Task.Run(() => SelectAndPreview());
                break;

            case "importVrcxStart":
                if (!string.IsNullOrEmpty(_vrcxImportPath))
                    _ = Task.Run(() => ImportAsync(_vrcxImportPath));
                break;

            case "getTimeline":
                HandleGetTimeline(msg);
                break;

            case "getTimelinePage":
                HandleGetTimelinePage(msg);
                break;

            case "searchTimeline":
                HandleSearchTimeline(msg);
                break;

            case "searchFriendTimeline":
                HandleSearchFriendTimeline(msg);
                break;

            case "getFriendTimeline":
                HandleGetFriendTimeline(msg);
                break;

            case "getFriendTimelinePage":
                HandleGetFriendTimelinePage(msg);
                break;

            case "getFtAlsoWasHere":
                HandleGetFtAlsoWasHere(msg);
                break;

            case "getTimelineByDate":
                HandleGetTimelineByDate(msg);
                break;

            case "getFriendTimelineByDate":
                HandleGetFriendTimelineByDate(msg);
                break;
        }
    }

    // getTimeline

    private void HandleGetTimeline(JObject msg)
    {
        _tlFetchCts.Cancel();
        _tlFetchCts = new CancellationTokenSource();
        var tlCt = _tlFetchCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                // Import any existing photos from PhotoPlayersStore not yet in timeline
                await _photos.BootstrapPhotoTimeline();
                if (tlCt.IsCancellationRequested) return;

                var tlTypeFilter = msg["type"]?.ToString() ?? "";
                var (events, hasMore) = _core.Timeline.GetEventsPaged(100, 0, tlTypeFilter);
                var total   = _core.Timeline.GetEventCount(tlTypeFilter);
                var payload = events.Select(e => _instance.BuildTimelinePayload(e)).ToList();
                _core.SendToJS("timelineData", new { events = payload, hasMore, offset = 0, total, type = tlTypeFilter });

                if (!_core.VrcApi.IsLoggedIn) return;
                if (tlCt.IsCancellationRequested) return;
                bool anyResolved = false;

                // 1) Resolve missing world thumbs (session-cached: skip known 404s and already-resolved worlds)
                var unknownWorlds = events
                    .Where(e => !string.IsNullOrEmpty(e.WorldId) && string.IsNullOrEmpty(e.WorldThumb)
                        && !_core.WorldThumbCache.ContainsKey(e.WorldId))
                    .Select(e => e.WorldId).Distinct().Take(20).ToList();

                foreach (var wid in unknownWorlds)
                {
                    if (tlCt.IsCancellationRequested) return;
                    try
                    {
                        var w = await _core.VrcApi.GetWorldAsync(wid);
                        if (w != null)
                        {
                            var wName  = w["name"]?.ToString()              ?? "";
                            var wThumb = w["thumbnailImageUrl"]?.ToString() ?? "";
                            _core.WorldThumbCache[wid] = wThumb;
                            foreach (var ev in events
                                .Where(e => e.WorldId == wid && string.IsNullOrEmpty(e.WorldThumb)))
                            {
                                _core.Timeline.UpdateEvent(ev.Id, e => { e.WorldName = wName; e.WorldThumb = wThumb; });
                                ev.WorldName  = wName;
                                ev.WorldThumb = wThumb;
                                anyResolved = true;
                            }
                        }
                        else _core.WorldThumbCache[wid] = ""; // cache 404 so we don't retry
                    }
                    catch { _core.WorldThumbCache[wid] = ""; }
                }
                // Apply session-cached world thumbs to any events that still have empty thumbs
                foreach (var ev in events.Where(e => !string.IsNullOrEmpty(e.WorldId) && string.IsNullOrEmpty(e.WorldThumb)
                    && _core.WorldThumbCache.TryGetValue(e.WorldId, out var ct) && !string.IsNullOrEmpty(ct)))
                {
                    if (_core.WorldThumbCache.TryGetValue(ev.WorldId, out var cachedThumb) && !string.IsNullOrEmpty(cachedThumb))
                    { ev.WorldThumb = cachedThumb; anyResolved = true; }
                }

                // 2) Resolve missing user / player images (first page only)
                var fetchedImgs   = new Dictionary<string, string>(); // userId -> imageUrl
                var playerRefs    = new List<(string evId, string userId)>();
                var userEventRefs = new List<(string evId, string userId)>();

                foreach (var ev in events)
                {
                    if (ev.Type == "instance_join")
                    {
                        foreach (var p in ev.Players.Where(p =>
                            string.IsNullOrEmpty(p.Image) && !string.IsNullOrEmpty(p.UserId)))
                        {
                            if (!fetchedImgs.ContainsKey(p.UserId)) fetchedImgs[p.UserId] = "";
                            playerRefs.Add((ev.Id, p.UserId));
                        }
                    }
                    else if (ev.Type is "first_meet" or "meet_again")
                    {
                        if (string.IsNullOrEmpty(ev.UserImage) && !string.IsNullOrEmpty(ev.UserId))
                        {
                            if (!fetchedImgs.ContainsKey(ev.UserId)) fetchedImgs[ev.UserId] = "";
                            userEventRefs.Add((ev.Id, ev.UserId));
                        }
                    }
                }

                if (fetchedImgs.Count > 0)
                {
                    // Batch-fetch with rate limiting (max 60 unique users, 3 concurrent)
                    var toFetch  = fetchedImgs.Keys.Take(60).ToList();
                    var sem      = new SemaphoreSlim(3);
                    var imgTasks = toFetch.Select(async uid =>
                    {
                        await sem.WaitAsync();
                        try
                        {
                            if (tlCt.IsCancellationRequested) return;
                            // Session cache first — avoids repeated API calls for the same user
                            if (_core.PlayerImageCache.TryGetValue(uid, out var ci) && !string.IsNullOrEmpty(ci))
                            { fetchedImgs[uid] = ci; return; }
                            if (_friends.TryGetNameImage(uid, out var fi) && !string.IsNullOrEmpty(fi.image))
                            { fetchedImgs[uid] = fi.image; _core.PlayerImageCache[uid] = fi.image; return; }
                            var profile = await _core.VrcApi.GetUserAsync(uid);
                            if (profile != null)
                            {
                                var img = VRChatApiService.GetUserImage(profile);
                                if (!string.IsNullOrEmpty(img))
                                {
                                    fetchedImgs[uid] = img;
                                    _core.PlayerImageCache[uid] = img;
                                    _core.Timeline.SetUserImage(uid, img); // persist across restarts
                                }
                            }
                            await Task.Delay(250);
                        }
                        finally { sem.Release(); }
                    });
                    await Task.WhenAll(imgTasks);

                    foreach (var (evId, uid) in playerRefs)
                    {
                        if (!fetchedImgs.TryGetValue(uid, out var img) || string.IsNullOrEmpty(img)) continue;
                        var localImg = img; var localUid = uid;
                        _core.Timeline.UpdateEvent(evId, ev =>
                        {
                            var p = ev.Players.FirstOrDefault(x => x.UserId == localUid);
                            if (p != null && string.IsNullOrEmpty(p.Image)) p.Image = localImg;
                        });
                        var localEv = events.FirstOrDefault(e => e.Id == evId);
                        if (localEv != null)
                        {
                            var p = localEv.Players.FirstOrDefault(x => x.UserId == uid);
                            if (p != null && string.IsNullOrEmpty(p.Image)) p.Image = img;
                        }
                        anyResolved = true;
                    }
                    foreach (var (evId, uid) in userEventRefs)
                    {
                        if (!fetchedImgs.TryGetValue(uid, out var img) || string.IsNullOrEmpty(img)) continue;
                        var localImg = img;
                        _core.Timeline.UpdateEvent(evId, ev =>
                        {
                            if (string.IsNullOrEmpty(ev.UserImage)) ev.UserImage = localImg;
                        });
                        var localEv = events.FirstOrDefault(e => e.Id == evId);
                        if (localEv != null && string.IsNullOrEmpty(localEv.UserImage)) localEv.UserImage = img;
                        anyResolved = true;
                    }
                }

                if (anyResolved)
                {
                    var updated = events.Select(e => _instance.BuildTimelinePayload(e)).ToList();
                    _core.SendToJS("timelineData", new { events = updated, hasMore, offset = 0, total, type = tlTypeFilter });
                }
            }
            catch (Exception ex)
            {
                _core.SendToJS("log", new { msg = $"[TIMELINE] Load error: {ex.Message}", color = "err" });
            }
        });
    }

    // getTimelinePage

    private void HandleGetTimelinePage(JObject msg)
    {
        // Single DB fetch — no async enrichment so page content never changes after load
        _ = Task.Run(() =>
        {
            try
            {
                var pageOffset   = msg["offset"]?.Value<int>() ?? 0;
                var tlTypeFilter = msg["type"]?.ToString() ?? "";
                var (events, hasMore) = _core.Timeline.GetEventsPaged(100, pageOffset, tlTypeFilter);
                var total   = _core.Timeline.GetEventCount(tlTypeFilter);
                var payload = events.Select(e => _instance.BuildTimelinePayload(e)).ToList();
                _core.SendToJS("timelineData", new { events = payload, hasMore, offset = pageOffset, total, type = tlTypeFilter });
            }
            catch { }
        });
    }

    // searchTimeline

    private void HandleSearchTimeline(JObject msg)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var srchQuery  = msg["query"]?.ToString() ?? "";
                var srchDate   = msg["date"]?.ToString() ?? "";
                var srchOffset = msg["offset"]?.Value<int>() ?? 0;
                var srchType   = msg["type"]?.ToString() ?? "";
                var (events, _) = _core.Timeline.SearchEvents(srchQuery, srchType, srchDate, srchOffset);
                var total   = _core.Timeline.SearchEventsCount(srchQuery, srchType, srchDate);
                var payload = events.Select(e => _instance.BuildTimelinePayload(e)).ToList();
                _core.SendToJS("timelineSearchResults", new { events = payload, query = srchQuery, date = srchDate, total, offset = srchOffset });
            }
            catch { }
        });
    }

    // searchFriendTimeline

    private void HandleSearchFriendTimeline(JObject msg)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var srchQuery  = msg["query"]?.ToString() ?? "";
                var srchDate   = msg["date"]?.ToString() ?? "";
                var srchOffset = msg["offset"]?.Value<int>() ?? 0;
                var srchType   = msg["type"]?.ToString() ?? "";
                var (events, _) = _core.Timeline.SearchFriendEvents(srchQuery, srchDate, srchOffset, srchType);
                var total   = _core.Timeline.SearchFriendEventsCount(srchQuery, srchDate, srchType);
                var payload = events.Select(e => _friends.BuildFriendTimelinePayload(e)).ToList();
                _core.SendToJS("friendTimelineSearchResults", new { events = payload, query = srchQuery, date = srchDate, total, offset = srchOffset });
            }
            catch { }
        });
    }

    // getFriendTimeline

    private void HandleGetFriendTimeline(JObject msg)
    {
        _ftlFetchCts.Cancel();
        _ftlFetchCts = new CancellationTokenSource();
        var ftlCt = _ftlFetchCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var typeFilter = msg["type"]?.ToString() ?? "";
                var (fevents, hasMore) = _core.Timeline.GetFriendEventsPaged(100, 0, typeFilter);
                var ftTotal  = _core.Timeline.GetFriendEventCount(typeFilter);
                var fpayload = fevents.Select(e => _friends.BuildFriendTimelinePayload(e)).ToList();
                _core.SendToJS("friendTimelineData", new { events = fpayload, hasMore, offset = 0, total = ftTotal, type = typeFilter });

                if (!_core.VrcApi.IsLoggedIn) return;
                if (ftlCt.IsCancellationRequested) return;

                // Resolve world thumbs for GPS events (session-cached)
                var unknownGpsWorlds = fevents
                    .Where(e => e.Type == "friend_gps" && !string.IsNullOrEmpty(e.WorldId) && string.IsNullOrEmpty(e.WorldThumb)
                        && !_core.WorldThumbCache.ContainsKey(e.WorldId))
                    .Select(e => e.WorldId).Distinct().Take(20).ToList();

                bool anyFevResolved = false;
                foreach (var wid in unknownGpsWorlds)
                {
                    if (ftlCt.IsCancellationRequested) return;
                    try
                    {
                        var w = await _core.VrcApi.GetWorldAsync(wid);
                        if (w == null) { _core.WorldThumbCache[wid] = ""; continue; }
                        var wName  = w["name"]?.ToString()              ?? "";
                        var wThumb = w["thumbnailImageUrl"]?.ToString() ?? "";
                        _core.WorldThumbCache[wid] = wThumb;
                        foreach (var ev in fevents.Where(e => e.WorldId == wid && string.IsNullOrEmpty(e.WorldThumb)))
                        {
                            _core.Timeline.UpdateFriendEventWorld(ev.Id, wName, wThumb);
                            ev.WorldName  = wName;
                            ev.WorldThumb = wThumb;
                            anyFevResolved = true;
                        }
                    }
                    catch { _core.WorldThumbCache[wid] = ""; }
                }

                // Resolve missing friend images from cache, then API
                var missingFriendIds = fevents
                    .Where(e => !string.IsNullOrEmpty(e.FriendId) && string.IsNullOrEmpty(e.FriendImage))
                    .Select(e => e.FriendId).Distinct().ToList();

                var fetchedFriendImgs = new Dictionary<string, string>();
                foreach (var fid in missingFriendIds)
                {
                    if (_core.PlayerImageCache.TryGetValue(fid, out var ci) && !string.IsNullOrEmpty(ci))
                        fetchedFriendImgs[fid] = ci;
                    else if (_friends.TryGetNameImage(fid, out var fi) && !string.IsNullOrEmpty(fi.image))
                    { fetchedFriendImgs[fid] = fi.image; _core.PlayerImageCache[fid] = fi.image; }
                }

                var needApiImg = missingFriendIds.Where(fid => !fetchedFriendImgs.ContainsKey(fid)).Take(20).ToList();
                if (needApiImg.Count > 0)
                {
                    var semFi = new SemaphoreSlim(3);
                    var fiTasks = needApiImg.Select(async fid =>
                    {
                        await semFi.WaitAsync();
                        try
                        {
                            if (ftlCt.IsCancellationRequested) return;
                            var profile = await _core.VrcApi.GetUserAsync(fid);
                            if (profile != null)
                            {
                                var img = VRChatApiService.GetUserImage(profile);
                                if (!string.IsNullOrEmpty(img))
                                { fetchedFriendImgs[fid] = img; _core.PlayerImageCache[fid] = img; }
                            }
                            await Task.Delay(250);
                        }
                        finally { semFi.Release(); }
                    });
                    await Task.WhenAll(fiTasks);
                }

                foreach (var (fid, img) in fetchedFriendImgs)
                    foreach (var ev in fevents.Where(e => e.FriendId == fid && string.IsNullOrEmpty(e.FriendImage)))
                    {
                        _core.Timeline.UpdateFriendEventImage(ev.Id, img);
                        ev.FriendImage = img;
                        anyFevResolved = true;
                    }

                if (anyFevResolved)
                {
                    var updated = fevents.Select(e => _friends.BuildFriendTimelinePayload(e)).ToList();
                    _core.SendToJS("friendTimelineData", new { events = updated, hasMore, offset = 0, total = ftTotal, type = typeFilter });
                }
            }
            catch (Exception ex)
            {
                _core.SendToJS("log", new { msg = $"[FRIEND TIMELINE] Load error: {ex.Message}", color = "err" });
            }
        });
    }

    // getFriendTimelinePage

    private void HandleGetFriendTimelinePage(JObject msg)
    {
        // Single DB fetch — no async enrichment so page content never changes after load
        _ = Task.Run(() =>
        {
            try
            {
                var pageOffset = msg["offset"]?.Value<int>() ?? 0;
                var typeFilter = msg["type"]?.ToString() ?? "";
                var (fevents, hasMore) = _core.Timeline.GetFriendEventsPaged(100, pageOffset, typeFilter);
                var ftTotal  = _core.Timeline.GetFriendEventCount(typeFilter);
                var fpayload = fevents.Select(e => _friends.BuildFriendTimelinePayload(e)).ToList();
                _core.SendToJS("friendTimelineData", new { events = fpayload, hasMore, offset = pageOffset, total = ftTotal, type = typeFilter });
            }
            catch { }
        });
    }

    // getFtAlsoWasHere

    private void HandleGetFtAlsoWasHere(JObject msg)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var location  = msg["location"]?.ToString() ?? "";
                var excludeId = msg["excludeId"]?.ToString() ?? "";
                var colocated = _core.Timeline.GetFriendGpsColocated(location, excludeId);
                var payload   = colocated.Select(e => new
                {
                    friendId    = e.FriendId,
                    friendName  = e.FriendName,
                    friendImage = _friends.ResolvePlayerImage(e.FriendId, e.FriendImage),
                }).ToList();
                _core.SendToJS("ftAlsoWasHere", new { excludeId, friends = payload });
            }
            catch { }
        });
    }

    // getTimelineByDate

    private void HandleGetTimelineByDate(JObject msg)
    {
        // Cancel any in-flight getTimeline enrichment so its stale timelineData
        // response doesn't overwrite the date-filtered results.
        _tlFetchCts.Cancel();
        _ = Task.Run(() =>
        {
            try
            {
                var dateStr    = msg["date"]?.ToString() ?? "";
                var typeFilter = msg["type"]?.ToString() ?? "";
                if (!DateTime.TryParse(dateStr, out var localDate)) return;
                localDate = DateTime.SpecifyKind(localDate, DateTimeKind.Local);
                var events  = _core.Timeline.GetEventsByDate(localDate);
                // Apply type filter in-memory (date views have limited event counts)
                if (!string.IsNullOrEmpty(typeFilter))
                    events = events.Where(e => e.Type == typeFilter).ToList();
                var total   = events.Count;
                var payload = events.Select(e => _instance.BuildTimelinePayload(e)).ToList();
                _core.SendToJS("timelineData", new { events = payload, hasMore = false, offset = 0, total, type = typeFilter, date = dateStr });
            }
            catch { }
        });
    }

    // getFriendTimelineByDate

    private void HandleGetFriendTimelineByDate(JObject msg)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var dateStr    = msg["date"]?.ToString() ?? "";
                var typeFilter = msg["type"]?.ToString() ?? "";
                if (!DateTime.TryParse(dateStr, out var localDate)) return;
                localDate = DateTime.SpecifyKind(localDate, DateTimeKind.Local);
                var fevents  = _core.Timeline.GetFriendEventsByDate(localDate, typeFilter);
                var fpayload = fevents.Select(e => _friends.BuildFriendTimelinePayload(e)).ToList();
                _core.SendToJS("friendTimelineData", new { events = fpayload, hasMore = false, offset = 0, type = typeFilter, date = dateStr });
            }
            catch { }
        });
    }

    // VRCX Import

    private void SelectAndPreview()
    {
        var r = Dialog.FileOpen("sqlite3,db");
        if (!r.IsOk) return;
        _vrcxImportPath = r.Path;

        try
        {
            using var vrcx = new SqliteConnection($"Data Source={_vrcxImportPath};Mode=ReadOnly");
            vrcx.Open();
            using var cmd = vrcx.CreateCommand();

            long Count(string sql) { cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L); }

            var worlds      = Count("SELECT COUNT(DISTINCT world_id) FROM gamelog_location WHERE world_id != ''");
            var locations   = Count("SELECT COUNT(*) FROM gamelog_location WHERE world_id != ''");
            var friendTimes = Count("SELECT COUNT(DISTINCT user_id) FROM gamelog_join_leave WHERE type='OnPlayerLeft' AND user_id != '' AND time > 0");

            long feedCount(string suffix)
            {
                long total = 0;
                cmd.CommandText = $"SELECT name FROM sqlite_master WHERE name LIKE '%{suffix}' AND type='table'";
                var tables = new List<string>();
                using (var tr = cmd.ExecuteReader()) while (tr.Read()) tables.Add(tr.GetString(0));
                foreach (var t in tables)
                {
                    cmd.CommandText = $"SELECT COUNT(*) FROM \"{t}\"";
                    total += Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
                }
                return total;
            }

            var gps         = feedCount("_feed_gps");
            var onlineOf    = feedCount("_feed_online_offline");
            var statuses    = feedCount("_feed_status");
            var bios        = feedCount("_feed_bio");

            _core.SendToJS("vrcxPreview", new
            {
                path        = Path.GetFileName(_vrcxImportPath),
                worlds,
                locations,
                friendTimes,
                gps,
                onlineOffline = onlineOf,
                statuses,
                bios,
            });
        }
        catch (Exception ex)
        {
            _core.SendToJS("vrcxImportError", new { error = ex.Message });
        }
    }

    // Merges VRCX world/friend time, timeline joins, and friend events into VRCNext
    private void ImportAsync(string vrcxPath)
    {
        try
        {
            _core.SendToJS("vrcxImportProgress", new { status = "Reading database...", percent = 10 });

            var worldMerge   = new List<(string worldId, string worldName, long seconds, int visits, string lastVisited)>();
            var friendMerge  = new List<(string userId, string displayName, long seconds, string lastSeen)>();
            var tlEvents     = new List<TimelineService.TimelineEvent>();
            var friendEvents = new List<TimelineService.FriendTimelineEvent>();

            using var vrcx = new SqliteConnection($"Data Source={vrcxPath};Mode=ReadOnly");
            vrcx.Open();

            using var cmd = vrcx.CreateCommand();

            // 1. World time
            cmd.CommandText = @"
                SELECT world_id, world_name, SUM(time)/1000, COUNT(*), MAX(created_at)
                FROM gamelog_location
                WHERE world_id != '' AND time > 0
                GROUP BY world_id";
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    worldMerge.Add((r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt32(3), r.GetString(4)));

            _core.SendToJS("vrcxImportProgress", new { status = "Reading friend data...", percent = 25 });

            // 2. Friend time
            cmd.CommandText = @"
                SELECT user_id, display_name, SUM(time)/1000, MAX(created_at)
                FROM gamelog_join_leave
                WHERE type='OnPlayerLeft' AND user_id != '' AND time > 0
                GROUP BY user_id";
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    friendMerge.Add((r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3)));

            _core.SendToJS("vrcxImportProgress", new { status = "Reading timeline events...", percent = 40 });

            // 3a. Build location -> players map from gamelog_join_leave
            var locationPlayers = new Dictionary<string, List<TimelineService.PlayerSnap>>();
            cmd.CommandText = "SELECT DISTINCT user_id, display_name, location FROM gamelog_join_leave WHERE type='OnPlayerJoined' AND user_id != ''";
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var uid = r.GetString(0);
                    var dn  = r.GetString(1);
                    var loc = r.GetString(2);
                    if (!locationPlayers.TryGetValue(loc, out var list))
                        locationPlayers[loc] = list = new List<TimelineService.PlayerSnap>();
                    list.Add(new TimelineService.PlayerSnap { UserId = uid, DisplayName = dn });
                }

            // 3b. Timeline: instance_join from gamelog_location
            cmd.CommandText = "SELECT created_at, world_id, world_name, location FROM gamelog_location WHERE world_id != ''";
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var ts  = r.GetString(0);
                    var wid = r.GetString(1);
                    var wn  = r.GetString(2);
                    var loc = r.GetString(3);
                    tlEvents.Add(new TimelineService.TimelineEvent
                    {
                        Id        = "vrcx_loc_" + VrcxHash(ts + wid),
                        Type      = "instance_join",
                        Timestamp = ts,
                        WorldId   = wid,
                        WorldName = wn,
                        Location  = loc,
                        Players   = locationPlayers.TryGetValue(loc, out var pl) ? pl : new(),
                    });
                }

            _core.SendToJS("vrcxImportProgress", new { status = "Reading friend events...", percent = 55 });

            // 4. Friend events from all {userId}_feed_* tables
            var userPrefixes = new List<string>();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE name LIKE '%_feed_gps' AND type='table'";
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var tbl = r.GetString(0);
                    userPrefixes.Add(tbl[..tbl.IndexOf("_feed_gps", StringComparison.Ordinal)]);
                }

            foreach (var prefix in userPrefixes)
            {
                // GPS
                TryImportFeed(vrcx, $"{prefix}_feed_gps", r =>
                    new TimelineService.FriendTimelineEvent
                    {
                        Id         = "vrcx_gps_" + VrcxHash(prefix + r.GetInt64(0)),
                        Type       = "friend_gps",
                        Timestamp  = r.GetString(1),
                        FriendId   = r.GetString(2),
                        FriendName = r.GetString(3),
                        Location   = r.GetString(4),
                        WorldName  = r.GetString(5),
                        WorldId    = ExtractWorldId(r.GetString(4)),
                        OldValue   = r.GetString(6), // previous_location
                        NewValue   = r.GetString(4), // new location
                    }, friendEvents);

                // Online / Offline
                TryImportFeed(vrcx, $"{prefix}_feed_online_offline", r =>
                    new TimelineService.FriendTimelineEvent
                    {
                        Id         = "vrcx_oo_" + VrcxHash(prefix + r.GetInt64(0)),
                        Type       = r.GetString(4) == "Online" ? "friend_online" : "friend_offline",
                        Timestamp  = r.GetString(1),
                        FriendId   = r.GetString(2),
                        FriendName = r.GetString(3),
                        Location   = r.GetString(5),
                        WorldName  = r.GetString(6),
                    }, friendEvents);

                // Status — category change (friend_status) + text change (friend_statusdesc)
                try
                {
                    using var stCmd = vrcx.CreateCommand();
                    stCmd.CommandText = $"SELECT * FROM \"{prefix}_feed_status\"";
                    using var stR = stCmd.ExecuteReader();
                    while (stR.Read())
                    {
                        var rowId  = stR.GetInt64(0);
                        var ts     = stR.GetString(1);
                        var uid    = stR.GetString(2);
                        var dn     = stR.GetString(3);
                        var newSt  = stR.IsDBNull(4) ? "" : stR.GetString(4); // status category
                        var newTxt = stR.IsDBNull(5) ? "" : stR.GetString(5); // status_description
                        var oldSt  = stR.IsDBNull(6) ? "" : stR.GetString(6); // previous_status
                        var oldTxt = stR.IsDBNull(7) ? "" : stR.GetString(7); // previous_status_description

                        friendEvents.Add(new TimelineService.FriendTimelineEvent
                        {
                            Id         = "vrcx_st_"  + VrcxHash(prefix + rowId),
                            Type       = "friend_status",
                            Timestamp  = ts,
                            FriendId   = uid,
                            FriendName = dn,
                            OldValue   = oldSt,
                            NewValue   = newSt,
                        });

                        if (newTxt != oldTxt)
                            friendEvents.Add(new TimelineService.FriendTimelineEvent
                            {
                                Id         = "vrcx_sd_" + VrcxHash(prefix + rowId),
                                Type       = "friend_statusdesc",
                                Timestamp  = ts,
                                FriendId   = uid,
                                FriendName = dn,
                                OldValue   = oldTxt,
                                NewValue   = newTxt,
                            });
                    }
                }
                catch { /* table may not exist */ }

                // Bio
                TryImportFeed(vrcx, $"{prefix}_feed_bio", r =>
                    new TimelineService.FriendTimelineEvent
                    {
                        Id         = "vrcx_bio_" + VrcxHash(prefix + r.GetInt64(0)),
                        Type       = "friend_bio",
                        Timestamp  = r.GetString(1),
                        FriendId   = r.GetString(2),
                        FriendName = r.GetString(3),
                        NewValue   = r.GetString(4), // bio
                        OldValue   = r.GetString(5), // previous_bio
                    }, friendEvents);
            }

            _core.SendToJS("vrcxImportProgress", new { status = "Generating meet events...", percent = 65 });

            // 5. First meet / Meet again from gamelog_join_leave
            var meetEvents   = new List<TimelineService.TimelineEvent>();
            var knownIds     = _core.Timeline.GetKnownUserIds();
            var importSeen   = new HashSet<string>();   // new users discovered during import
            var instanceSeen = new HashSet<string>();   // uid|loc pairs for meet_again dedup

            // location -> (worldId, worldName) built from gamelog_location rows already in worldMerge
            var locWorldInfo = new Dictionary<string, (string wid, string wn)>();
            cmd.CommandText = "SELECT location, world_id, world_name FROM gamelog_location WHERE world_id != ''";
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                    locWorldInfo[r.GetString(0)] = (r.GetString(1), r.GetString(2));

            cmd.CommandText = @"
                SELECT user_id, display_name, location, created_at
                FROM gamelog_join_leave
                WHERE type='OnPlayerJoined' AND user_id != ''
                ORDER BY created_at";
            using (var r = cmd.ExecuteReader())
                while (r.Read())
                {
                    var uid = r.GetString(0);
                    var dn  = r.GetString(1);
                    var loc = r.GetString(2);
                    var ts  = r.GetString(3);
                    var (wid, wn) = locWorldInfo.TryGetValue(loc, out var wi) ? wi : (ExtractWorldId(loc), "");

                    var isKnown = knownIds.Contains(uid) || importSeen.Contains(uid);
                    if (!isKnown)
                    {
                        meetEvents.Add(new TimelineService.TimelineEvent
                        {
                            Id        = "vrcx_fm_" + VrcxHash(uid),
                            Type      = "first_meet",
                            Timestamp = ts,
                            UserId    = uid,
                            UserName  = dn,
                            WorldId   = wid,
                            WorldName = wn,
                            Location  = loc,
                        });
                        importSeen.Add(uid);
                        knownIds.Add(uid);
                    }
                    else
                    {
                        var key = uid + "|" + loc;
                        if (!instanceSeen.Contains(key))
                        {
                            instanceSeen.Add(key);
                            meetEvents.Add(new TimelineService.TimelineEvent
                            {
                                Id        = "vrcx_ma_" + VrcxHash(uid + loc),
                                Type      = "meet_again",
                                Timestamp = ts,
                                UserId    = uid,
                                UserName  = dn,
                                WorldId   = wid,
                                WorldName = wn,
                                Location  = loc,
                            });
                        }
                    }
                }

            _core.SendToJS("vrcxImportProgress", new { status = "Merging into VRCNext...", percent = 75 });

            // 6. Merge into VRCNext
            _core.WorldTimeTracker.BulkMerge(worldMerge);
            _core.TimeTracker.BulkMerge(friendMerge);
            _core.SendToJS("vrcxImportProgress", new { status = "Saving timeline...", percent = 88 });
            _core.Timeline.BulkImportEvents(tlEvents);
            _core.Timeline.BulkImportEvents(meetEvents);
            _core.Timeline.BulkImportFriendEvents(friendEvents);
            if (importSeen.Count > 0) _core.Timeline.SeedKnownUsers(importSeen);

            _core.SendToJS("vrcxImportDone", new
            {
                worlds        = worldMerge.Count,
                friends       = friendMerge.Count,
                timelineJoins = tlEvents.Count,
                friendEvents  = friendEvents.Count,
                meetEvents    = meetEvents.Count,
            });
        }
        catch (Exception ex)
        {
            _core.SendToJS("vrcxImportError", new { error = ex.Message });
        }
    }

    // Import Helpers

    private static void TryImportFeed(
        SqliteConnection vrcx,
        string tableName,
        Func<SqliteDataReader, TimelineService.FriendTimelineEvent> map,
        List<TimelineService.FriendTimelineEvent> target)
    {
        try
        {
            using var cmd = vrcx.CreateCommand();
            cmd.CommandText = $"SELECT * FROM \"{tableName}\"";
            using var r = cmd.ExecuteReader();
            while (r.Read()) target.Add(map(r));
        }
        catch { /* table may not exist */ }
    }

    private static string ExtractWorldId(string location)
    {
        if (string.IsNullOrEmpty(location)) return "";
        var colon = location.IndexOf(':');
        var id = colon > 0 ? location[..colon] : location;
        return id.StartsWith("wrld_") ? id : "";
    }

    private static string VrcxHash(object key)
        => Math.Abs(key?.GetHashCode() ?? 0).ToString("x8");
}
