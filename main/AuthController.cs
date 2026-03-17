using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NativeFileDialogSharp;
using VRCNext.Services;
using System.Diagnostics;

namespace VRCNext;

// Owns all auth, login, settings, setup, cache-send, and startup orchestration.

public class AuthController
{
    private readonly CoreLibrary _core;
    private readonly FriendsController _friends;
    private readonly InstanceController _instance;
    private readonly PhotosController _photos;
    private readonly RelayController _relayCtrl;
    private readonly GroupsController _groups;
    private readonly DiscordController _discordCtrl;

    // Auth State
    private string _pending2faType = "totp";
    private bool _vrcDebugSetup;
    private string _lastAvatarName = "";
    private string _lastVideoUrl = "";
    private DateTime _lastVideoUrlTime = DateTime.MinValue;

    // Constructor

    public AuthController(
        CoreLibrary core,
        FriendsController friends,
        InstanceController instance,
        PhotosController photos,
        RelayController relayCtrl,
        GroupsController groups,
        DiscordController discordCtrl)
    {
        _core = core;
        _friends = friends;
        _instance = instance;
        _photos = photos;
        _relayCtrl = relayCtrl;
        _groups = groups;
        _discordCtrl = discordCtrl;
    }

    // Invoke shim (Photino is thread-safe)
    private static void Invoke(Action action) => action();

    // Message Handler

    public async Task HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "vrcLogin":
                var vrcUser = msg["username"]?.ToString() ?? "";
                var vrcPass = msg["password"]?.ToString() ?? "";
                await VrcLoginAsync(vrcUser, vrcPass);
                break;

            case "vrc2FA":
                var code2fa = msg["code"]?.ToString() ?? "";
                var type2fa = msg["type"]?.ToString() ?? "totp";
                await VrcVerify2FAAsync(code2fa, type2fa);
                break;

            case "vrcLogout":
                _relayCtrl.StopWebSocket();
                await _core.VrcApi.LogoutAsync();
                _core.Settings.VrcAuthCookie = "";
                _core.Settings.VrcTwoFactorCookie = "";
                _core.Settings.Save();
                _core.SendToJS("vrcLoggedOut", null);
                _core.SendToJS("log", new { msg = "VRChat: Logged out", color = "sec" });
                break;

            case "saveSettings":
                var data = msg["data"];
                if (data != null) ApplySettings(data);
                break;

            case "setupReady":
                _core.SendToJS("setPlatform", new { isLinux = !OperatingSystem.IsWindows() });
                var detectedPath = _core.Settings.VrcPath;
                if (string.IsNullOrWhiteSpace(detectedPath) || !File.Exists(detectedPath))
                    detectedPath = DetectVrcLaunchExe();
                if (!string.IsNullOrEmpty(detectedPath) && detectedPath != _core.Settings.VrcPath)
                {
                    _core.Settings.VrcPath = detectedPath;
                    _core.Settings.Save();
                }
                var photoDir = _core.Settings.WatchFolders.FirstOrDefault() ?? "";
                if (string.IsNullOrEmpty(photoDir))
                    photoDir = DetectVrcPhotoDir();
                _core.SendToJS("setupState", new
                {
                    vrcPath = detectedPath ?? "",
                    photoDir,
                    loggedIn = _core.VrcApi.IsLoggedIn,
                    displayName = _core.VrcApi.IsLoggedIn ? (_core.VrcApi.CurrentUserRaw?["displayName"]?.ToString() ?? "") : "",
                    platform = OperatingSystem.IsWindows() ? "windows" : "linux",
                });
                _ = VrcTryResumeAsync();
                break;

            case "setupDone":
                _core.Settings.SetupComplete = true;
                _core.Settings.Save();
                _core.LoadPage?.Invoke(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "index.html"));
                break;

            case "resetSetup":
                _core.Settings.SetupComplete = false;
                _core.Settings.Save();
                var setupHtml = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "setup", "setup.html");
                if (File.Exists(setupHtml)) _core.LoadPage?.Invoke(setupHtml);
                break;

            case "forceTrim":
                _core.MemTrim.TrimNow();
                break;

            case "clearImgCache":
                _ = Task.Run(() =>
                {
                    _core.ImgCache?.ClearAll();
                    Invoke(() => _core.SendToJS("log", new { msg = "\ud83d\uddd1 Image cache cleared.", color = "sec" }));
                });
                break;

            case "clearFfcCache":
                _ = Task.Run(() =>
                {
                    _core.Cache.ClearAll();
                    Invoke(() => _core.SendToJS("log", new { msg = "\ud83d\uddd1 FFC cache cleared.", color = "sec" }));
                });
                break;

            case "forceFfcAll":
                _ = Task.Run(ForceFfcAllAsync);
                break;

            case "setupSaveStartWithWindows":
                _core.Settings.StartWithWindows = msg["enabled"]?.Value<bool>() ?? false;
                ApplyStartWithWindows(_core.Settings.StartWithWindows);
                _core.Settings.Save();
                break;

            case "setupSaveVrcPath":
                _core.Settings.VrcPath = msg["path"]?.ToString() ?? "";
                _core.Settings.Save();
                break;

            case "setupSavePhotoDir":
                var setupPhotoDir = msg["path"]?.ToString() ?? "";
                if (!string.IsNullOrEmpty(setupPhotoDir)
                    && Directory.Exists(setupPhotoDir)
                    && !_core.Settings.WatchFolders.Contains(setupPhotoDir, StringComparer.OrdinalIgnoreCase))
                {
                    _core.Settings.WatchFolders.Add(setupPhotoDir);
                    _core.Settings.Save();
                }
                break;

            case "setupBrowsePhotoDir":
                {
                    var r = Dialog.FolderPicker(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VRChat"));
                    if (r.IsOk) _core.SendToJS("setupPhotoDirResult", r.Path);
                }
                break;

            case "checkUpdate":
                _ = Task.Run(async () =>
                {
                    var version = await _core.UpdateService.CheckAsync();
                    if (version != null)
                        Invoke(() => _core.SendToJS("updateAvailable", new { version }));
                });
                break;

            case "installUpdate":
                _ = Task.Run(async () =>
                {
                    await _core.UpdateService.DownloadAsync(p =>
                        Invoke(() => _core.SendToJS("updateProgress", p)));
                    Invoke(() => _core.SendToJS("updateReady", null));
                    await Task.Delay(800);
                    Invoke(() => _core.UpdateService.ApplyAndRestart());
                });
                break;

            case "openUrl":
                var openUrlTarget = msg["url"]?.ToString();
                if (!string.IsNullOrEmpty(openUrlTarget) &&
                    (openUrlTarget.StartsWith("https://") || openUrlTarget.StartsWith("http://")))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = openUrlTarget,
                        UseShellExecute = true
                    });
                }
                break;

            case "browseExe":
                {
                    var target = msg["target"]?.ToString() ?? "extra";
                    var r = Dialog.FileOpen("exe");
                    if (r.IsOk)
                    {
                        _core.SendToJS("exeAdded", new { target, path = r.Path });
                        if (target == "vrchat")
                        {
                            _core.Settings.VrcPath = r.Path;
                            _core.Settings.Save();
                        }
                    }
                }
                break;

            case "browseDashBg":
                {
                    var r = Dialog.FileOpen("png,jpg,jpeg,bmp,webp,gif");
                    if (r.IsOk)
                    {
                        try
                        {
                            var bytes = File.ReadAllBytes(r.Path);
                            var ext2 = Path.GetExtension(r.Path).ToLower();
                            var mime = ext2 switch
                            {
                                ".png" => "image/png",
                                ".jpg" or ".jpeg" => "image/jpeg",
                                ".gif" => "image/gif",
                                ".bmp" => "image/bmp",
                                ".webp" => "image/webp",
                                _ => "image/png"
                            };
                            var dataUri = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
                            _core.SendToJS("dashBgSelected", new { path = r.Path, dataUri });
                        }
                        catch (Exception ex)
                        {
                            _core.SendToJS("log", new { msg = $"Background image error: {ex.Message}", color = "err" });
                        }
                    }
                }
                break;

            case "vrcLoadDashBg":
                _ = Task.Run(() =>
                {
                    try
                    {
                        var bgPath = msg["path"]?.ToString();
                        if (!string.IsNullOrEmpty(bgPath) && File.Exists(bgPath))
                        {
                            var bytes = File.ReadAllBytes(bgPath);
                            var ext3 = Path.GetExtension(bgPath).ToLower();
                            var mime = ext3 switch
                            {
                                ".png" => "image/png",
                                ".jpg" or ".jpeg" => "image/jpeg",
                                ".gif" => "image/gif",
                                ".bmp" => "image/bmp",
                                ".webp" => "image/webp",
                                _ => "image/png"
                            };
                            var dataUri = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
                            Invoke(() => _core.SendToJS("dashBgSelected", new { path = bgPath, dataUri }));
                        }
                    }
                    catch (Exception ex)
                    {
                        Invoke(() => _core.SendToJS("log", new { msg = $"Load background error: {ex.Message}", color = "err" }));
                    }
                });
                break;

            case "vrcRandomDashBg":
                _ = Task.Run(() =>
                {
                    try
                    {
                        var imgExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };
                        var allImages = new List<string>();

                        foreach (var folder in _core.Settings.WatchFolders.Where(Directory.Exists))
                        {
                            try
                            {
                                allImages.AddRange(
                                    Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                                        .Where(f => imgExts.Contains(Path.GetExtension(f)))
                                );
                            }
                            catch { }
                        }

                        if (allImages.Count == 0)
                        {
                            Invoke(() => _core.SendToJS("log", new { msg = "Random background: no images found in watch folders", color = "warn" }));
                            return;
                        }

                        var rng = new Random();
                        var picked = allImages[rng.Next(allImages.Count)];
                        var bytes = File.ReadAllBytes(picked);
                        var imgExt = Path.GetExtension(picked).ToLower();
                        var mime = imgExt switch
                        {
                            ".png" => "image/png",
                            ".jpg" or ".jpeg" => "image/jpeg",
                            ".gif" => "image/gif",
                            ".bmp" => "image/bmp",
                            ".webp" => "image/webp",
                            _ => "image/png"
                        };
                        var dataUri = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
                        Invoke(() =>
                        {
                            _core.SendToJS("dashBgSelected", new { path = picked, dataUri });
                            _core.SendToJS("log", new { msg = $"Random background: {Path.GetFileName(picked)}", color = "ok" });
                        });
                    }
                    catch (Exception ex)
                    {
                        Invoke(() => _core.SendToJS("log", new { msg = $"Random background error: {ex.Message}", color = "err" }));
                    }
                });
                break;
        }
    }

    // Ready handler (called from MainForm "ready" case)

    public void HandleReady()
    {
        _core.SendToJS("loadSettings", _core.Settings);
        _core.SendToJS("favoritesLoaded", _photos.Favorites);
        var customColors = _core.Cache.LoadRaw(CacheHandler.KeyCustomColors);
        if (customColors != null) _core.SendToJS("customColors", customColors);
        _ = VrcTryResumeAsync();
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);
            var version = await _core.UpdateService.CheckAsync();
            if (version != null)
                Invoke(() => _core.SendToJS("updateAvailable", new { version }));
        });
    }

    // VRC Debug Log Setup

    private void SetupVrcDebugLog()
    {
        if (_vrcDebugSetup) return;
        _vrcDebugSetup = true;
        _core.VrcApi.DebugLog += msg =>
        {
            try { _core.SendToJS("log", new { msg = $"[VRC] {msg}", color = "sec" }); } catch { }
        };
        _core.LogWatcher.DebugLog += msg =>
        {
            try { _core.SendToJS("log", new { msg = $"[LOG] {msg}", color = "sec" }); } catch { }
        };
        _core.LogWatcher.WorldChanged += (wId, loc) =>
        {
            try { _instance.HandleWorldChangedOnUiThread(wId, loc); } catch { }
        };
        _core.LogWatcher.PlayerJoined += (uid, name) =>
        {
            try { _instance.HandlePlayerJoinedOnUiThread(uid, name); } catch { }
        };
        _core.LogWatcher.PlayerLeft += (uid, name) =>
        {
            try { _instance.PushCurrentInstanceFromCache(); } catch { }
        };
        _core.LogWatcher.InstanceClosed += loc =>
        {
            try
            {
                _instance.RecentlyClosedLocs.Add(loc);
                if (_core.Settings.MyInstances.Remove(loc))
                {
                    _core.Settings.Save();
                    _ = Task.Run(() => _core.DispatchMessage?.Invoke("""{"type":"vrcGetMyInstances"}"""));
                }
            }
            catch { }
        };
        _core.LogWatcher.AvatarChanged += (displayName, avatarName) =>
        {
            try
            {
                var myName = _core.VrcApi.CurrentUserRaw?["displayName"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(myName) || displayName != myName) return;
                if (avatarName == _lastAvatarName) return;
                _lastAvatarName = avatarName;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(2000);
                        await _core.VrcApi.GetCurrentUserLocationAsync();
                        var avatarId = _core.VrcApi.CurrentAvatarId ?? "";
                        string avatarThumb = "";
                        JObject? av = null;
                        if (!string.IsNullOrEmpty(avatarId))
                        {
                            av = await _core.VrcApi.GetAvatarAsync(avatarId);
                            avatarThumb = av?["thumbnailImageUrl"]?.ToString() ?? av?["imageUrl"]?.ToString() ?? "";
                        }
                        var ev = new TimelineService.TimelineEvent
                        {
                            Type      = "avatar_switch",
                            Timestamp = DateTime.UtcNow.ToString("o"),
                            UserId    = avatarId,
                            UserName  = avatarName,
                            UserImage = avatarThumb,
                        };
                        _core.Timeline.AddEvent(ev);
                        _core.SendToJS("timelineEvent", _instance.BuildTimelinePayload(ev));

                        // Submit public avatar to avtrdb if enabled
                        if (!string.IsNullOrEmpty(avatarId) && av?["releaseStatus"]?.ToString() == "public")
                            _core.AvtrdbSubmit?.Invoke(avatarId);
                    }
                    catch { }
                });
            }
            catch { }
        };
        _core.LogWatcher.VideoUrl += url =>
        {
            try
            {
                var now = DateTime.UtcNow;
                if (_lastVideoUrl == url && (now - _lastVideoUrlTime).TotalSeconds < 30) return;
                _lastVideoUrl     = url;
                _lastVideoUrlTime = now;

                var ev = new TimelineService.TimelineEvent
                {
                    Type      = "video_url",
                    Timestamp = now.ToString("o"),
                    WorldId   = _core.LogWatcher.CurrentWorldId ?? "",
                    WorldName = _instance.CachedInstWorldName,
                    Message   = url,
                };
                _core.Timeline.AddEvent(ev);
                _core.SendToJS("timelineEvent", _instance.BuildTimelinePayload(ev));
            }
            catch { }
        };
    }

    // Session Resume

    public async Task VrcTryResumeAsync()
    {
        SetupVrcDebugLog();

        if (!string.IsNullOrEmpty(_core.Settings.VrcAuthCookie))
        {
            _core.SendToJS("log", new { msg = "VRChat: Resuming session...", color = "sec" });
            _core.VrcApi.RestoreCookies(_core.Settings.VrcAuthCookie, _core.Settings.VrcTwoFactorCookie);

            var result = await _core.VrcApi.TryResumeSessionAsync();
            if (result.Success && result.User != null)
            {
                SendVrcUserData(result.User, loginFlow: true);
                _core.SendToJS("log", new { msg = $"VRChat: Reconnected as {result.User["displayName"]}", color = "ok" });
                SendAllCachedData();
                await _friends.RefreshFriendsAsync();
                _relayCtrl.StartWebSocket();
                _ = TriggerStartupBackgroundRefreshAsync();
                return;
            }

            _core.Settings.VrcAuthCookie = "";
            _core.Settings.VrcTwoFactorCookie = "";
            _core.Settings.Save();
            _core.SendToJS("log", new { msg = "VRChat: Session expired, please log in again", color = "warn" });
        }

        if (!string.IsNullOrEmpty(_core.Settings.VrcUsername))
        {
            _core.SendToJS("vrcPrefillLogin", new
            {
                username = _core.Settings.VrcUsername,
                password = _core.Settings.VrcPassword
            });
        }
    }

    // Login

    private async Task VrcLoginAsync(string username, string password)
    {
        SetupVrcDebugLog();
        _core.SendToJS("log", new { msg = "VRChat: Logging in...", color = "sec" });
        var result = await _core.VrcApi.LoginAsync(username, password);
        if (result.Requires2FA)
        {
            _pending2faType = result.TwoFactorType;
            _core.SendToJS("vrcNeeds2FA", new { type = result.TwoFactorType });
            _core.SendToJS("log", new { msg = $"VRChat: 2FA required ({result.TwoFactorType})", color = "warn" });
        }
        else if (result.Success && result.User != null)
        {
            _core.Settings.VrcUsername = username;
            _core.Settings.VrcPassword = password;
            SaveVrcCookies();
            _core.Settings.Save();

            SendVrcUserData(result.User, loginFlow: true);
            _core.SendToJS("log", new { msg = $"VRChat: Logged in as {result.User["displayName"]}", color = "ok" });
            await _friends.RefreshFriendsAsync();
            _relayCtrl.StartWebSocket();
            _ = TriggerStartupBackgroundRefreshAsync();
        }
        else
        {
            _core.SendToJS("vrcLoginError", new { error = result.Error ?? "Login failed" });
            _core.SendToJS("log", new { msg = $"VRChat: {result.Error}", color = "err" });
        }
    }

    // 2FA

    private async Task VrcVerify2FAAsync(string code, string type)
    {
        var result = await _core.VrcApi.Verify2FAAsync(code, type);
        if (result.Success && result.User != null)
        {
            SaveVrcCookies();
            _core.Settings.Save();

            SendVrcUserData(result.User, loginFlow: true);
            _core.SendToJS("log", new { msg = $"VRChat: Logged in as {result.User["displayName"]}", color = "ok" });
            await _friends.RefreshFriendsAsync();
            _relayCtrl.StartWebSocket();
            _ = TriggerStartupBackgroundRefreshAsync();
        }
        else
        {
            _core.SendToJS("vrcLoginError", new { error = result.Error ?? "2FA failed" });
            _core.SendToJS("log", new { msg = $"VRChat: 2FA error \u2014 {result.Error}", color = "err" });
        }
    }

    // Cookie Persistence

    private void SaveVrcCookies()
    {
        var (auth, tfa) = _core.VrcApi.GetCookies();
        _core.Settings.VrcAuthCookie = auth ?? "";
        _core.Settings.VrcTwoFactorCookie = tfa ?? "";
    }

    // SendVrcUserData (cross-cutting login orchestration)

    public void SendVrcUserData(JObject user, bool loginFlow = false)
    {
        _core.CurrentVrcUserId = user["id"]?.ToString() ?? "";

        if (loginFlow)
        {
            _core.LogWatcher.Start();
            _photos.StartVrcPhotoWatcher();
            _ = _friends.LoadFavoriteFriendsAsync();
        }

        if (!_instance.LogWatcherBootstrapped)
        {
            _instance.LogWatcherBootstrapped = true;
            if (!string.IsNullOrEmpty(_core.LogWatcher.CurrentWorldId) && _instance.PendingInstanceEventId == null)
            {
                var loc = _core.LogWatcher.CurrentLocation ?? _core.LogWatcher.CurrentWorldId;
                var lastJoin = _core.Timeline.GetEvents().FirstOrDefault(e => e.Type == "instance_join");
                if (lastJoin != null && lastJoin.Location == loc)
                {
                    _instance.PendingInstanceEventId = lastJoin.Id;
                    if (lastJoin.Players != null)
                    {
                        foreach (var p in lastJoin.Players)
                        {
                            if (!string.IsNullOrEmpty(p.UserId))
                                _instance.CumulativeInstancePlayers[p.UserId] = (p.DisplayName, p.Image ?? "");
                        }
                    }
                    _core.WorldTimeTracker.ResumeWorld(_core.LogWatcher.CurrentWorldId);
                    _instance.LastTrackedWorldId = _core.LogWatcher.CurrentWorldId;
                }
                else
                {
                    _instance.HandleWorldChangedOnUiThread(_core.LogWatcher.CurrentWorldId, loc);
                }
                foreach (var p in _core.LogWatcher.GetCurrentPlayers())
                {
                    if (!string.IsNullOrEmpty(p.UserId) && !_instance.CumulativeInstancePlayers.ContainsKey(p.UserId))
                        _instance.CumulativeInstancePlayers[p.UserId] = (p.DisplayName, "");
                    if (!string.IsNullOrEmpty(p.UserId) && !string.IsNullOrEmpty(p.DisplayName))
                        _core.TimeTracker.UpdateUserInfo(p.UserId, p.DisplayName, "");
                }
            }
        }

        var rawStatus = user["status"]?.ToString() ?? "";
        if (!string.IsNullOrEmpty(rawStatus)) _core.MyVrcStatus = rawStatus;
        _discordCtrl.PushPresence();

        _core.SendToJS("vrcUser", new
        {
            id = user["id"]?.ToString() ?? "",
            displayName = user["displayName"]?.ToString() ?? "",
            image = VRChatApiService.GetUserImage(user),
            status = user["status"]?.ToString() ?? "offline",
            statusDescription = user["statusDescription"]?.ToString() ?? "",
            currentAvatar = user["currentAvatar"]?.ToString() ?? "",
            bio = user["bio"]?.ToString() ?? "",
            pronouns = user["pronouns"]?.ToString() ?? "",
            bioLinks = user["bioLinks"]?.ToObject<List<string>>() ?? new List<string>(),
            tags = user["tags"]?.ToObject<List<string>>() ?? new List<string>(),
            profilePicOverride    = _core.ImgCache?.Get(user["profilePicOverride"]?.ToString() ?? "") ?? user["profilePicOverride"]?.ToString() ?? "",
            currentAvatarImageUrl = _core.ImgCache?.Get(user["currentAvatarImageUrl"]?.ToString() ?? "") ?? user["currentAvatarImageUrl"]?.ToString() ?? "",
        });

        if (loginFlow)
        {
            _ = Task.Run(async () =>
            {
                var balance = await _core.VrcApi.GetBalanceAsync();
                if (balance >= 0)
                    Invoke(() => _core.SendToJS("vrcCredits", new { balance }));
            });
        }
    }

    // Settings

    private void ApplySettings(JToken data)
    {
        try
        {
            _core.Settings.BotName = data["botName"]?.ToString() ?? "VRCNext";
            _core.Settings.BotAvatarUrl = data["botAvatar"]?.ToString() ?? "";
            _core.Settings.VrcPath = data["vrcPath"]?.ToString() ?? "";
            _core.Settings.AutoStart = data["autoStart"]?.Value<bool>() ?? false;
            _core.Settings.StartWithWindows = data["startWithWindows"]?.Value<bool>() ?? false;
            ApplyStartWithWindows(_core.Settings.StartWithWindows);
            _core.Settings.PostAll = data["postAll"]?.Value<bool>() ?? false;
            _core.Settings.Notifications = data["notifications"]?.Value<bool>() ?? true;
            _core.Settings.NotifySound = data["notifySound"]?.Value<bool>() ?? false; // legacy
            _core.Settings.NotifySoundEnabled = data["notifySoundEnabled"]?.Value<bool>() ?? false;
            _core.Settings.MessageSoundEnabled = data["messageSoundEnabled"]?.Value<bool>() ?? false;
            _core.Settings.MediaRelaySoundEnabled = data["mediaRelaySoundEnabled"]?.Value<bool>() ?? false;
            _core.Settings.MinimizeToTray = data["minimizeToTray"]?.Value<bool>() ?? false;
            _core.Settings.Theme = data["theme"]?.ToString() ?? "midnight";
            _core.Settings.SpecialTheme = data["specialTheme"]?.ToString() ?? "";
#if WINDOWS
            // Theme colors are always pushed from JS via overlayThemeColors
            // (triggered by applyColors in core.js). Do NOT call SetTheme()
            // here — the C# hardcoded palettes may be out of sync with the
            // JS THEMES and would overwrite the correct colors that JS just sent.
#endif
            _core.Settings.AutoColorAccuracy = data["autoColorAccuracy"]?.Value<int>() ?? 50;
            _core.Settings.PlayBtnTheme = data["playBtnTheme"]?.ToString() ?? "";
            _core.Settings.CursorTheme = data["cursorTheme"]?.ToString() ?? "";

            var dashBg = data["dashBgPath"]?.ToString();
            if (dashBg != null) _core.Settings.DashBgPath = dashBg;
            _core.Settings.DashOpacity = data["dashOpacity"]?.Value<int>() ?? 40;
            _core.Settings.RandomDashBg = data["randomDashBg"]?.Value<bool>() ?? false;

            // Webhooks: explicit parsing to handle any casing
            if (data["webhooks"] is JArray whArr && whArr.Count > 0)
            {
                _core.Settings.Webhooks.Clear();
                for (int i = 0; i < Math.Min(whArr.Count, 4); i++)
                {
                    var item = whArr[i];
                    _core.Settings.Webhooks.Add(new AppSettings.WebhookSlot {
                        Name = (item["Name"] ?? item["name"])?.ToString() ?? "",
                        Url = (item["Url"] ?? item["url"])?.ToString() ?? "",
                        Enabled = (item["Enabled"] ?? item["enabled"])?.Value<bool>() ?? false,
                    });
                }
                while (_core.Settings.Webhooks.Count < 4)
                    _core.Settings.Webhooks.Add(new AppSettings.WebhookSlot { Name = $"Channel {_core.Settings.Webhooks.Count + 1}" });
            }

            var folders = data["folders"]?.ToObject<List<string>>();
            if (folders != null) _core.Settings.WatchFolders = folders;

            var extraExe = data["extraExe"]?.ToObject<List<string>>();
            if (extraExe != null) _core.Settings.ExtraExe = extraExe;

            var vrcU = data["vrcUsername"]?.ToString();
            var vrcP = data["vrcPassword"]?.ToString();
            if (vrcU != null) _core.Settings.VrcUsername = vrcU;
            if (vrcP != null) _core.Settings.VrcPassword = vrcP;

            // Space Flight settings
            _core.Settings.SfMultiplier = data["sfMultiplier"]?.Value<float>() ?? 1f;
            _core.Settings.SfLockX = data["sfLockX"]?.Value<bool>() ?? false;
            _core.Settings.SfLockY = data["sfLockY"]?.Value<bool>() ?? false;
            _core.Settings.SfLockZ = data["sfLockZ"]?.Value<bool>() ?? false;
            _core.Settings.SfLeftHand = data["sfLeftHand"]?.Value<bool>() ?? false;
            _core.Settings.SfRightHand = data["sfRightHand"]?.Value<bool>() ?? true;
            _core.Settings.SfUseGrip = data["sfUseGrip"]?.Value<bool>() ?? true;
            _core.Settings.ChatboxAutoStart = data["chatboxAutoStart"]?.Value<bool>() ?? false;
            _core.Settings.SfAutoStart = data["sfAutoStart"]?.Value<bool>() ?? false;
            _core.Settings.DiscordPresenceAutoStart = data["discordPresenceAutoStart"]?.Value<bool>() ?? false;
            // VR / Desktop split auto-starts
            _core.Settings.ChatboxAutoStartVR      = data["chatboxAutoStartVR"]?.Value<bool>()      ?? false;
            _core.Settings.ChatboxAutoStartDesktop = data["chatboxAutoStartDesktop"]?.Value<bool>() ?? false;
            _core.Settings.SfAutoStartVR           = data["sfAutoStartVR"]?.Value<bool>()           ?? false;
            _core.Settings.RelayAutoStartVR        = data["relayAutoStartVR"]?.Value<bool>()        ?? false;
            _core.Settings.RelayAutoStartDesktop   = data["relayAutoStartDesktop"]?.Value<bool>()   ?? false;
            _core.Settings.YtAutoStartVR           = data["ytAutoStartVR"]?.Value<bool>()           ?? false;
            _core.Settings.YtAutoStartDesktop      = data["ytAutoStartDesktop"]?.Value<bool>()      ?? false;
            _core.Settings.VfAutoStartVR           = data["vfAutoStartVR"]?.Value<bool>()           ?? false;
            _core.Settings.VfAutoStartDesktop      = data["vfAutoStartDesktop"]?.Value<bool>()      ?? false;
            _core.Settings.DpAutoStartVR           = data["dpAutoStartVR"]?.Value<bool>()           ?? false;
            _core.Settings.DpAutoStartDesktop      = data["dpAutoStartDesktop"]?.Value<bool>()      ?? false;
            _core.Settings.VroAutoStartVR          = data["vroAutoStartVR"]?.Value<bool>()          ?? false;
            _core.Settings.DpHideInstIdJoinMe  = data["dpHideInstIdJoinMe"]?.Value<bool>()  ?? false;
            _core.Settings.DpHideInstIdOnline  = data["dpHideInstIdOnline"]?.Value<bool>()  ?? false;
            _core.Settings.DpHideInstIdAskMe   = data["dpHideInstIdAskMe"]?.Value<bool>()   ?? true;
            _core.Settings.DpHideInstIdBusy    = data["dpHideInstIdBusy"]?.Value<bool>()    ?? true;
            _core.Settings.DpHideLocJoinMe     = data["dpHideLocJoinMe"]?.Value<bool>()     ?? false;
            _core.Settings.DpHideLocOnline     = data["dpHideLocOnline"]?.Value<bool>()     ?? false;
            _core.Settings.DpHideLocAskMe      = data["dpHideLocAskMe"]?.Value<bool>()      ?? true;
            _core.Settings.DpHideLocBusy       = data["dpHideLocBusy"]?.Value<bool>()       ?? true;
            _core.Settings.DpHidePlayersJoinMe = data["dpHidePlayersJoinMe"]?.Value<bool>() ?? false;
            _core.Settings.DpHidePlayersOnline = data["dpHidePlayersOnline"]?.Value<bool>() ?? false;
            _core.Settings.DpHidePlayersAskMe  = data["dpHidePlayersAskMe"]?.Value<bool>()  ?? true;
            _core.Settings.DpHidePlayersBusy   = data["dpHidePlayersBusy"]?.Value<bool>()   ?? true;
            _core.Settings.DpHideJoinBtnJoinMe = data["dpHideJoinBtnJoinMe"]?.Value<bool>() ?? false;
            _core.Settings.DpHideJoinBtnOnline = data["dpHideJoinBtnOnline"]?.Value<bool>() ?? false;
            _core.Settings.DpHideJoinBtnAskMe  = data["dpHideJoinBtnAskMe"]?.Value<bool>()  ?? true;
            _core.Settings.DpHideJoinBtnBusy   = data["dpHideJoinBtnBusy"]?.Value<bool>()   ?? true;

            // Image cache settings
            _core.Settings.ImgCacheEnabled  = data["imgCacheEnabled"]?.Value<bool>() ?? true;
            _core.Settings.ImgCacheLimitGb  = Math.Clamp(data["imgCacheLimitGb"]?.Value<int>() ?? 5, 5, 30);
            if (_core.ImgCache != null)
            {
                _core.ImgCache.Enabled    = _core.Settings.ImgCacheEnabled;
                _core.ImgCache.LimitBytes = (long)_core.Settings.ImgCacheLimitGb * 1024 * 1024 * 1024;
                if (_core.Settings.ImgCacheEnabled && _core.ImgCache.LimitBytes > 0)
                    _ = Task.Run(() => _core.ImgCache.TrimIfNeeded(_core.ImgCache.LimitBytes));
            }

            // Fast Fetch Cache
            _core.Settings.FfcEnabled = data["ffcEnabled"]?.Value<bool>() ?? true;

            // Avtrdb Support
            _core.Settings.AvtrdbReportDeleted = data["avtrdbReportDeleted"]?.Value<bool>() ?? true;
            _core.Settings.AvtrdbSubmitAvatars = data["avtrdbSubmitAvatars"]?.Value<bool>() ?? false;

            // Memory Trim
            _core.Settings.MemoryTrimEnabled = data["memoryTrimEnabled"]?.Value<bool>() ?? false;
            _core.MemTrim.SetEnabled(_core.Settings.MemoryTrimEnabled);

            _core.Settings.Save();
            if (_core.Settings.LastSaveError != null)
                _core.SendToJS("log", new { msg = $"\u274c Save failed: {_core.Settings.LastSaveError}", color = "err" });

            // No-op with Photino — watch folders served via /media{i}/ routes
        }
        catch (Exception ex)
        {
            _core.SendToJS("log", new { msg = $"Save error: {ex.Message}", color = "err" });
        }
    }

    // Start With Windows

    internal static void ApplyStartWithWindows(bool enable)
    {
#if WINDOWS
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
        if (key == null) return;
        var exe = Environment.ProcessPath ?? "";
        if (enable)
            key.SetValue("VRCNext", $"\"{exe}\" --minimized");
        else
            key.DeleteValue("VRCNext", throwOnMissingValue: false);
#else
        var dir  = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "autostart");
        var file = Path.Combine(dir, "VRCNext.desktop");
        if (enable)
        {
            Directory.CreateDirectory(dir);
            var exe = Environment.ProcessPath ?? "VRCNext";
            File.WriteAllText(file,
                "[Desktop Entry]\n" +
                "Type=Application\n" +
                "Name=VRCNext\n" +
                $"Exec=\"{exe}\" --minimized\n" +
                "Hidden=false\n" +
                "NoDisplay=false\n" +
                "X-GNOME-Autostart-enabled=true\n" +
                "StartupNotify=false\n");
        }
        else if (File.Exists(file)) File.Delete(file);
#endif
    }

    // VRC Path Detection

    internal static string? DetectVrcLaunchExe()
    {
        var candidates = new List<string>();

#if WINDOWS
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed) continue;
            var root = drive.RootDirectory.FullName;
            candidates.Add(Path.Combine(root, "Program Files (x86)", "Steam", "steamapps", "common", "VRChat", "launch.exe"));
            candidates.Add(Path.Combine(root, "Program Files", "Steam", "steamapps", "common", "VRChat", "launch.exe"));
            candidates.Add(Path.Combine(root, "Steam", "steamapps", "common", "VRChat", "launch.exe"));
            candidates.Add(Path.Combine(root, "SteamLibrary", "steamapps", "common", "VRChat", "launch.exe"));
            candidates.Add(Path.Combine(root, "Games", "Steam", "steamapps", "common", "VRChat", "launch.exe"));
            candidates.Add(Path.Combine(root, "Games", "SteamLibrary", "steamapps", "common", "VRChat", "launch.exe"));
        }

        var steamVdfWin = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam", "steamapps", "libraryfolders.vdf");
        AddVdfLibraries(steamVdfWin, candidates, "launch.exe");
#else
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var linuxSteamRoots = new[]
        {
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".steam", "root"),
        };
        foreach (var sr in linuxSteamRoots)
        {
            candidates.Add(Path.Combine(sr, "steamapps", "common", "VRChat", "VRChat.exe"));
            candidates.Add(Path.Combine(sr, "steamapps", "common", "VRChat", "launch.exe"));
            AddVdfLibraries(Path.Combine(sr, "steamapps", "libraryfolders.vdf"), candidates, "VRChat.exe");
        }
#endif
        return candidates.FirstOrDefault(File.Exists);
    }

    private static void AddVdfLibraries(string vdfPath, List<string> candidates, string exe)
    {
        try
        {
            if (!File.Exists(vdfPath)) return;
            var vdf = File.ReadAllText(vdfPath);
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(vdf, "\"path\"\\s+\"([^\"]+)\""))
            {
                var libPath = m.Groups[1].Value.Replace("\\\\", "\\");
                candidates.Add(Path.Combine(libPath, "steamapps", "common", "VRChat", exe));
            }
        }
        catch { }
    }

    internal static string DetectVrcPhotoDir()
    {
#if WINDOWS
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VRChat");
        return Directory.Exists(path) ? path : "";
#else
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var steamRoots = new[]
        {
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".steam", "steam"),
        };
        foreach (var sr in steamRoots)
        {
            var protonPath = Path.Combine(sr, "steamapps", "compatdata", "438100",
                "pfx", "drive_c", "users", "steamuser", "My Pictures", "VRChat");
            if (Directory.Exists(protonPath)) return protonPath;

            var protonPath2 = Path.Combine(sr, "steamapps", "compatdata", "438100",
                "pfx", "drive_c", "users", "steamuser", "Pictures", "VRChat");
            if (Directory.Exists(protonPath2)) return protonPath2;
        }
        var native = Path.Combine(home, "Pictures", "VRChat");
        return Directory.Exists(native) ? native : "";
#endif
    }

    // Cache Send / Startup Refresh

    internal class WFavGroup
    {
        public string name        { get; set; } = "";
        public string displayName { get; set; } = "";
        public string type        { get; set; } = "";
        public int    capacity    { get; set; } = 25;
    }

    internal static List<WFavGroup> FillMissingWorldSlots(List<WFavGroup> groupList)
    {
        var existing = new HashSet<string>(groupList.Select(g => g.name));

        var regularSlots = new[] {
            ("worlds1", "Worlds 1", "world"), ("worlds2", "Worlds 2", "world"),
            ("worlds3", "Worlds 3", "world"), ("worlds4", "Worlds 4", "world")
        };
        foreach (var (sName, sDisplay, sType) in regularSlots)
            if (!existing.Contains(sName))
                groupList.Add(new WFavGroup { name = sName, displayName = sDisplay, type = sType });

        bool hasVrcPlus = groupList.Any(g => g.type == "vrcPlusWorld");
        if (hasVrcPlus)
        {
            var vrcPlusSlots = new[] {
                ("vrcPlusWorlds1", "VRC+ Worlds 1", "vrcPlusWorld"), ("vrcPlusWorlds2", "VRC+ Worlds 2", "vrcPlusWorld"),
                ("vrcPlusWorlds3", "VRC+ Worlds 3", "vrcPlusWorld"), ("vrcPlusWorlds4", "VRC+ Worlds 4", "vrcPlusWorld")
            };
            foreach (var (sName, sDisplay, sType) in vrcPlusSlots)
                if (!existing.Contains(sName))
                    groupList.Add(new WFavGroup { name = sName, displayName = sDisplay, type = sType });
        }

        return groupList
            .OrderBy(g => g.type == "vrcPlusWorld" ? 1 : 0)
            .ThenBy(g => g.name)
            .ToList();
    }

    internal static List<WFavGroup> FillMissingAvatarSlots(List<WFavGroup> groupList)
    {
        var existing = new HashSet<string>(groupList.Select(g => g.name));

        var slots = new[] {
            ("avatars1", "Avatars 1", "avatar"),
            ("avatars2", "Avatars 2", "avatar"),
            ("avatars3", "Avatars 3", "avatar"),
            ("avatars4", "Avatars 4", "avatar"),
            ("avatars5", "Avatars 5", "avatar"),
            ("avatars6", "Avatars 6", "avatar"),
        };
        foreach (var (sName, sDisplay, sType) in slots)
            if (!existing.Contains(sName))
                groupList.Add(new WFavGroup { name = sName, displayName = sDisplay, type = sType });

        return groupList
            .OrderBy(g => g.name)
            .ToList();
    }

    public async Task FetchAndCacheFavWorldsAsync()
    {
        try
        {
            var groups = await _core.VrcApi.GetFavoriteGroupsAsync();
            var worldTypes = new HashSet<string> { "world", "vrcPlusWorld" };
            var groupList = groups
                .Where(g => worldTypes.Contains(g["type"]?.ToString() ?? ""))
                .Select(g => new WFavGroup {
                    name        = g["name"]?.ToString() ?? "",
                    displayName = g["displayName"]?.ToString() ?? "",
                    type        = g["type"]?.ToString() ?? "world"
                })
                .Where(g => !string.IsNullOrEmpty(g.name))
                .ToList();
            groupList = FillMissingWorldSlots(groupList);

            var sem = new SemaphoreSlim(4, 4);
            var perGroup = new System.Collections.Concurrent.ConcurrentDictionary<string, List<JObject>>();
            await Task.WhenAll(groupList.Select(async g =>
            {
                await sem.WaitAsync();
                try { perGroup[g.name] = await _core.VrcApi.GetFavoriteWorldsByGroupAsync(g.name, 100); }
                finally { sem.Release(); }
            }));

            var allWorlds = new List<object>();
            foreach (var g in groupList)
            {
                if (!perGroup.TryGetValue(g.name, out var groupWorlds)) continue;
                foreach (var w in groupWorlds)
                {
                    var wid = w["id"]?.ToString() ?? "";
                    var stats = _core.WorldTimeTracker.GetWorldStats(wid);
                    allWorlds.Add(new
                    {
                        id                = wid,
                        name              = w["name"]?.ToString() ?? "",
                        imageUrl          = w["imageUrl"]?.ToString() ?? "",
                        thumbnailImageUrl = w["thumbnailImageUrl"]?.ToString() ?? "",
                        authorName        = w["authorName"]?.ToString() ?? "",
                        occupants         = w["occupants"]?.Value<int>()  ?? 0,
                        capacity          = w["capacity"]?.Value<int>()   ?? 0,
                        favorites         = w["favorites"]?.Value<int>()  ?? 0,
                        visits            = w["visits"]?.Value<int>()     ?? 0,
                        tags              = w["tags"]?.ToObject<List<string>>() ?? new List<string>(),
                        favoriteGroup     = g.name,
                        favoriteId        = w["favoriteId"]?.ToString() ?? "",
                        worldTimeSeconds  = stats.totalSeconds,
                        worldVisitCount   = stats.visitCount,
                    });
                }
            }

            var payload = new { worlds = allWorlds, groups = groupList };
            if (_core.Settings.FfcEnabled) _core.Cache.Save(CacheHandler.KeyFavWorlds, payload);
            Invoke(() => _core.SendToJS("vrcFavoriteWorlds", payload));
        }
        catch (Exception ex)
        {
            Invoke(() => _core.SendToJS("log", new { msg = $"Favorite worlds error: {ex.Message}", color = "err" }));
        }
    }

    public async Task FetchAndCacheFavAvatarsAsync()
    {
        try
        {
            var groups = await _core.VrcApi.GetFavoriteGroupsAsync();
            var avatarTypes = new HashSet<string> { "avatar" };
            var groupList = groups
                .Where(g => avatarTypes.Contains(g["type"]?.ToString() ?? ""))
                .Select(g => new WFavGroup {
                    name        = g["name"]?.ToString() ?? "",
                    displayName = g["displayName"]?.ToString() ?? "",
                    type        = g["type"]?.ToString() ?? "avatar"
                })
                .Where(g => !string.IsNullOrEmpty(g.name))
                .ToList();
            groupList = FillMissingAvatarSlots(groupList);
            int avCap = _core.VrcApi.HasVrcPlus ? 50 : 25;
            foreach (var g in groupList) g.capacity = avCap;

            var sem = new SemaphoreSlim(4, 4);
            var perGroup = new System.Collections.Concurrent.ConcurrentDictionary<string, List<JObject>>();
            await Task.WhenAll(groupList.Select(async g =>
            {
                await sem.WaitAsync();
                try { perGroup[g.name] = await _core.VrcApi.GetFavoriteAvatarsByGroupAsync(g.name, 100); }
                finally { sem.Release(); }
            }));

            var allAvatars = new List<object>();
            foreach (var g in groupList)
            {
                if (!perGroup.TryGetValue(g.name, out var groupAvatars)) continue;
                foreach (var a in groupAvatars)
                {
                    allAvatars.Add(new
                    {
                        id                = a["id"]?.ToString() ?? "",
                        name              = a["name"]?.ToString() ?? "",
                        imageUrl          = a["imageUrl"]?.ToString() ?? "",
                        thumbnailImageUrl = a["thumbnailImageUrl"]?.ToString() ?? "",
                        authorName        = a["authorName"]?.ToString() ?? "",
                        releaseStatus     = a["releaseStatus"]?.ToString() ?? "private",
                        favoriteGroup     = g.name,
                        favoriteId        = a["favoriteId"]?.ToString() ?? "",
                        unityPackages     = (a["unityPackages"] as JArray ?? new JArray())
                            .Select(p => new { platform = p["platform"]?.ToString() ?? "", variant = p["variant"]?.ToString() ?? "" })
                            .ToArray(),
                    });
                }
            }

            var payload = new { avatars = allAvatars, groups = groupList };
            Invoke(() => _core.SendToJS("vrcFavoriteAvatars", payload));
        }
        catch (Exception ex)
        {
            Invoke(() => _core.SendToJS("log", new { msg = $"Favorite avatars error: {ex.Message}", color = "err" }));
        }
    }

    public async Task FetchAndCacheAvatarsAsync()
    {
        try
        {
            var avatars = await _core.VrcApi.GetOwnAvatarsAsync();
            var list = avatars.Select(a => new
            {
                id                = a["id"]?.ToString() ?? "",
                name              = a["name"]?.ToString() ?? "",
                imageUrl          = a["imageUrl"]?.ToString() ?? "",
                thumbnailImageUrl = a["thumbnailImageUrl"]?.ToString() ?? "",
                authorName        = a["authorName"]?.ToString() ?? "",
                releaseStatus     = a["releaseStatus"]?.ToString() ?? "private",
                description       = a["description"]?.ToString() ?? "",
                unityPackages     = (a["unityPackages"] as JArray ?? new JArray())
                    .Select(p => new { platform = p["platform"]?.ToString() ?? "", variant = p["variant"]?.ToString() ?? "" })
                    .ToArray(),
            }).ToList();
            var payload = new { filter = "own", avatars = list, currentAvatarId = _core.VrcApi.CurrentAvatarId ?? "" };
            if (_core.Settings.FfcEnabled) _core.Cache.Save(CacheHandler.KeyAvatars, payload);
            Invoke(() => _core.SendToJS("vrcAvatars", payload));
        }
        catch (Exception ex)
        {
            Invoke(() => _core.SendToJS("log", new { msg = $"Avatar load error: {ex.Message}", color = "err" }));
        }
    }

    private void SendAllCachedData()
    {
        var customColors = _core.Cache.LoadRaw(CacheHandler.KeyCustomColors);
        if (customColors != null) _core.SendToJS("customColors", customColors);

        if (!_core.Settings.FfcEnabled) return;

        var avatars = _core.Cache.LoadRaw(CacheHandler.KeyAvatars);
        if (avatars != null) _core.SendToJS("vrcAvatars", avatars);

        var groups = _core.Cache.LoadRaw(CacheHandler.KeyGroups);
        if (groups != null) _core.SendToJS("vrcMyGroups", groups);

        var favWorlds = _core.Cache.LoadRaw(CacheHandler.KeyFavWorlds);
        if (favWorlds != null) _core.SendToJS("vrcFavoriteWorlds", favWorlds);
    }

    private async Task TriggerStartupBackgroundRefreshAsync()
    {
        if (!_core.VrcApi.IsLoggedIn) return;
        _ = Task.Run(FetchAndCacheAvatarsAsync);
        _ = Task.Run(_groups.FetchAndCacheAsync);
        _ = Task.Run(FetchAndCacheFavWorldsAsync);
        _ = Task.Run(BackfillMissingPlayerImagesAsync);
        _ = Task.Run(CollectWorldStatsIfMissingAsync);
        await Task.CompletedTask;
    }

    private async Task CollectWorldStatsIfMissingAsync()
    {
        try
        {
            if (!_core.VrcApi.IsLoggedIn) return;
            if (_core.Timeline.HasWorldStatsForCurrentHour()) return;
            var worlds = await _core.VrcApi.GetMyWorldsAsync();
            foreach (var w in worlds)
            {
                var id = w["id"]?.ToString();
                if (string.IsNullOrEmpty(id)) continue;
                var full = await _core.VrcApi.GetWorldFreshAsync(id);
                var active    = full?["occupants"]?.Value<int>() ?? w["occupants"]?.Value<int>() ?? 0;
                var favorites = full?["favorites"]?.Value<int>() ?? w["favorites"]?.Value<int>() ?? 0;
                var visits    = full?["visits"]?.Value<int>() ?? 0;
                _core.Timeline.InsertWorldStats(id, active, favorites, visits);
            }
        }
        catch { }
    }

    private async Task BackfillMissingPlayerImagesAsync()
    {
        if (!_core.VrcApi.IsLoggedIn) return;
        var missing = _core.Timeline.GetUsersWithMissingImages();
        if (missing.Count == 0) return;

        Invoke(() => _core.SendToJS("log", new { msg = $"[IMG] Backfilling images for {missing.Count} players\u2026", color = "sec" }));

        var sem = new SemaphoreSlim(3);
        var tasks = missing.Select(async m =>
        {
            await sem.WaitAsync();
            try
            {
                if (!_core.VrcApi.IsLoggedIn) return;
                var profile = await _core.VrcApi.GetUserAsync(m.UserId);
                if (profile == null) return;
                var img = VRChatApiService.GetUserImage(profile);
                if (string.IsNullOrEmpty(img)) return;

                _core.PlayerImageCache[m.UserId] = img;
                _core.PlayerProfileCache[m.UserId] = profile;
                _core.PlayerAgeVerifiedCache[m.UserId] = profile["ageVerified"]?.Value<bool>() ?? false;
                _core.Timeline.SetUserImage(m.UserId, img);
                await Task.Delay(300);
            }
            catch { }
            finally { sem.Release(); }
        });
        await Task.WhenAll(tasks);

        Invoke(() => _core.SendToJS("log", new { msg = "[IMG] Backfill complete", color = "ok" }));
    }

    public async Task ForceFfcAllAsync()
    {
        if (!_core.VrcApi.IsLoggedIn) return;

        void Progress(int current, int total, string label) =>
            Invoke(() => _core.SendToJS("ffcProgress", new {
                progress = total > 0 ? (int)((double)current / total * 100) : 0,
                label,
                done = false
            }));

        try
        {
            var friendIds = _friends.GetTrackedUserIds();
            int total = friendIds.Count + 3;
            int completed = 0;

            Progress(completed, total, "Caching avatars...");
            await FetchAndCacheAvatarsAsync();
            Progress(++completed, total, "Caching groups...");
            await _groups.FetchAndCacheAsync();
            Progress(++completed, total, "Caching worlds...");
            await FetchAndCacheFavWorldsAsync();

            var semaphore = new SemaphoreSlim(4, 4);
            var tasks = friendIds.Select(async uid =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var payload = await _friends.BuildUserDetailPayloadAsync(uid);
                    if (payload != null)
                    {
                        _core.Cache.Save(CacheHandler.KeyUserProfile(uid), payload);
                    }
                    await Task.Delay(250);
                }
                catch { }
                finally
                {
                    semaphore.Release();
                    int c = Interlocked.Increment(ref completed);
                    Progress(c, total, $"Caching profiles... ({c - 3}/{friendIds.Count})");
                }
            });

            await Task.WhenAll(tasks);

            Invoke(() =>
            {
                _core.SendToJS("ffcProgress", new { progress = 100, label = $"Done! {friendIds.Count} profiles cached.", done = true });
                _core.SendToJS("log", new { msg = $"FFC: {friendIds.Count} profiles + avatars + groups + worlds cached.", color = "ok" });
            });
        }
        catch (Exception ex)
        {
            Invoke(() => _core.SendToJS("ffcProgress", new { progress = 0, label = "Error: " + ex.Message, done = true }));
        }
    }
}
