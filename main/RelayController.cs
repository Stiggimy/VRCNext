using System.Diagnostics;
using Newtonsoft.Json.Linq;
using VRCNext.Services;

namespace VRCNext;

// Owns all Relay, VRCVideoCacher, VRChat launch, and WebSocket lifecycle state + logic.

public class RelayController : IDisposable
{
    private readonly CoreLibrary _core;
    private readonly FriendsController _friends;
    private readonly InstanceController _instance;
    private readonly NotificationsController _notifications;
    private readonly VROverlayController _vroCtrl;

    // Fields (moved from MainForm.Fields.cs)
    private bool _relayRunning;
    private DateTime _relayStart;
    private Process? _vcProcess;
    private VRChatWebSocketService? _wsService;

    // Public Accessors
    public bool IsRunning => _relayRunning;
    public DateTime RelayStart => _relayStart;
    public bool IsVcRunning => _vcProcess != null && !_vcProcess.HasExited;

    // Cross-domain callback (set by MainForm)
    public Action<JObject>? OnOwnUserUpdated { get; set; }

    public RelayController(
        CoreLibrary core,
        FriendsController friends,
        InstanceController instance,
        NotificationsController notifications,
        VROverlayController vroCtrl)
    {
        _core = core;
        _friends = friends;
        _instance = instance;
        _notifications = notifications;
        _vroCtrl = vroCtrl;
    }

    // Message Handler

    public void HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "startRelay":
                StartRelay();
                break;
            case "stopRelay":
                StopRelay();
                break;
            case "playVRChat":
                if (IsVrcRunning())
                    _core.SendToJS("log", new { msg = "VRChat is already running.", color = "ok" });
                else
                    _core.SendToJS("vrcLaunchNeeded", new { location = "", steamVr = IsSteamVrRunning() });
                break;
            case "vcCheck":
                _core.SendToJS("vcState", GetVcState());
                break;
            case "vcInstall":
                _ = InstallVcAsync();
                break;
            case "vcStart":
                StartVcProcess();
                break;
            case "vcStop":
                StopVcProcess();
                break;
            case "vcSend":
                break;
            case "vrcLaunchAndJoin":
                {
                    var llLoc = msg["location"]?.ToString() ?? "";
                    var llVr  = msg["vr"]?.Value<bool>() ?? false;
#if WINDOWS
                    var vrcExe = _core.Settings.VrcPath;
                    if (!string.IsNullOrWhiteSpace(vrcExe) && File.Exists(vrcExe))
                    {
                        string llArgs;
                        if (!string.IsNullOrEmpty(llLoc))
                        {
                            var joinUri = VRChatApiService.BuildLaunchUri(llLoc);
                            llArgs = llVr ? $"\"{joinUri}\"" : $"--no-vr \"{joinUri}\"";
                        }
                        else
                        {
                            llArgs = llVr ? "" : "--no-vr";
                        }
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = vrcExe, Arguments = llArgs,
                            WorkingDirectory = Path.GetDirectoryName(vrcExe) ?? "",
                            UseShellExecute = false
                        });
                    }
                    else if (!string.IsNullOrEmpty(llLoc))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = VRChatApiService.BuildLaunchUri(llLoc), UseShellExecute = true
                        });
                    }
                    else
                    {
                        _core.SendToJS("log", new { msg = "VRChat path not configured. Set it in Settings.", color = "err" });
                        break;
                    }
                    foreach (var exe in _core.Settings.ExtraExe)
                    {
                        try
                        {
                            if (File.Exists(exe))
                                Process.Start(new ProcessStartInfo
                                {
                                    FileName = exe,
                                    WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                                    UseShellExecute = true
                                });
                        }
                        catch { }
                    }
#else
                    // On Linux, launch via Steam so Proton is applied automatically
                    string steamArgs;
                    if (!string.IsNullOrEmpty(llLoc))
                    {
                        var joinUri = VRChatApiService.BuildLaunchUri(llLoc);
                        steamArgs = $"steam://rungameid/438100//{Uri.EscapeDataString(joinUri)}";
                    }
                    else
                    {
                        steamArgs = "steam://rungameid/438100";
                    }
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "steam",
                        Arguments = steamArgs,
                        UseShellExecute = false
                    });
#endif
                    var modeLabel = llVr ? "VR" : "Desktop";
                    var locLabel  = !string.IsNullOrEmpty(llLoc) ? $" → {llLoc}" : "";
                    _core.SendToJS("vrcActionResult", new { action = "join", success = true, message = $"Launching VRChat ({modeLabel})..." });
                    _core.SendToJS("log", new { msg = $"Launched VRChat [{modeLabel}]{locLabel}", color = "ok" });
                    _core.SendToJS("vrcLaunched", new { vr = llVr });
                }
                break;
        }
    }

    // Relay Control

    private void StartRelay()
    {
        var folders = _core.Settings.WatchFolders.Where(Directory.Exists).ToList();
        if (folders.Count == 0)
        {
            _core.SendToJS("log", new { msg = "No valid watch folders configured!", color = "err" });
            return;
        }
        var whs = _core.Settings.Webhooks.Where(w => w.Enabled && !string.IsNullOrWhiteSpace(w.Url)).ToList();
        if (whs.Count == 0)
        {
            _core.SendToJS("log", new { msg = "No webhooks active!", color = "err" });
            return;
        }

        _core.FileWatcher.Start(folders);
        _relayRunning = true;
        _relayStart = DateTime.Now;
        _vroCtrl.UpdateToolStates();

        _core.SendToJS("relayState", new { running = true, streams = whs.Count });
        _core.SendToJS("log", new { msg = "Relay started successfully", color = "ok" });
        foreach (var f in folders)
            _core.SendToJS("log", new { msg = $"  Watching: {f}", color = "sec" });
        foreach (var w in whs)
            _core.SendToJS("log", new { msg = $"  Webhook: {w.Name}", color = "accent" });
    }

    private void StopRelay()
    {
        _core.FileWatcher.Stop();
        _relayRunning = false;
        _vroCtrl.UpdateToolStates();

        _core.SendToJS("relayState", new { running = false, streams = 0 });
        _core.SendToJS("log", new { msg = "Relay stopped", color = "warn" });
    }

    // Toggle (called from VR overlay)

    public void ToggleRelay()
    {
        if (_relayRunning) StopRelay();
        else               StartRelay();
    }

    // VRCVideoCacher

    public static readonly string VcExePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VRCNext", "Tools", "VRCVideoCacher", "VRCVideoCacher.exe");

    private object GetVcState()
    {
        bool installed = File.Exists(VcExePath);
        bool running   = _vcProcess != null && !_vcProcess.HasExited;
        return new { installed, running };
    }

    private void StartVcProcess()
    {
        if (!File.Exists(VcExePath)) return;
        if (_vcProcess != null && !_vcProcess.HasExited) return;

        try
        {
            _vcProcess = Process.Start(new ProcessStartInfo
            {
                FileName         = VcExePath,
                WorkingDirectory = Path.GetDirectoryName(VcExePath)!,
                UseShellExecute  = false,
                CreateNoWindow   = false,
            })!;
            _vcProcess.EnableRaisingEvents = true;
            _vcProcess.Exited += (_, _) =>
            {
                _vcProcess = null;
                try { Invoke(() => { _core.SendToJS("vcState", GetVcState()); _vroCtrl.UpdateToolStates(); }); } catch { }
            };
            _core.SendToJS("vcState", GetVcState());
            _vroCtrl.UpdateToolStates();
        }
        catch { }
    }

    private void StopVcProcess()
    {
        try { _vcProcess?.Kill(entireProcessTree: true); } catch { }
        _vcProcess = null;
        _core.SendToJS("vcState", GetVcState());
        _vroCtrl.UpdateToolStates();
    }

    public void ToggleVc()
    {
        if (_vcProcess != null && !_vcProcess.HasExited)
            StopVcProcess();
        else
            StartVcProcess();
    }

    private async Task InstallVcAsync()
    {
        try
        {
            Invoke(() => _core.SendToJS("vcState", new { installed = false, running = false, downloading = true, progress = 0 }));

            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", AppInfo.UserAgent);

            var apiResp = await http.GetAsync("https://api.github.com/repos/EllyVR/VRCVideoCacher/releases/latest");
            if (!apiResp.IsSuccessStatusCode)
            {
                Invoke(() => _core.SendToJS("vcState", new { installed = false, running = false, error = $"GitHub API: HTTP {(int)apiResp.StatusCode}" }));
                return;
            }

            var json     = JObject.Parse(await apiResp.Content.ReadAsStringAsync());
            var version  = json["tag_name"]?.ToString() ?? "?";
            var assets   = json["assets"] as JArray;
            var exeAsset = assets?.FirstOrDefault(a => a["name"]?.ToString().EndsWith(".exe") == true);
            var dlUrl    = exeAsset?["browser_download_url"]?.ToString();

            if (string.IsNullOrEmpty(dlUrl))
            {
                Invoke(() => _core.SendToJS("vcState", new { installed = false, running = false, error = "No .exe asset in latest release" }));
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(VcExePath)!);

            using var dlResp = await http.GetAsync(dlUrl, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
            var total = dlResp.Content.Headers.ContentLength ?? -1L;
            await using var stream = await dlResp.Content.ReadAsStreamAsync();
            await using var fs     = File.Create(VcExePath);

            var buf        = new byte[65536];
            long downloaded = 0;
            int  read;
            while ((read = await stream.ReadAsync(buf)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, read));
                downloaded += read;
                if (total > 0)
                {
                    int pct = (int)(downloaded * 100 / total);
                    try { Invoke(() => _core.SendToJS("vcState", new { installed = false, running = false, downloading = true, progress = pct })); } catch { }
                }
            }

            Invoke(() =>
            {
                _core.SendToJS("vcLog",   new { msg = $"VRCVideoCacher {version} installed", color = "ok" });
                _core.SendToJS("vcState", GetVcState());
            });
        }
        catch (Exception ex)
        {
            try { Invoke(() => _core.SendToJS("vcState", new { installed = false, running = false, error = ex.Message })); } catch { }
        }
    }

    // Launch VRChat + extra apps

    private void LaunchVRChat()
    {
        try
        {
#if WINDOWS
            var vrcPath = _core.Settings.VrcPath;
            if (string.IsNullOrWhiteSpace(vrcPath) || !File.Exists(vrcPath))
            {
                _core.SendToJS("log", new { msg = "VRChat path not set or invalid. Configure in Settings.", color = "err" });
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = vrcPath,
                WorkingDirectory = Path.GetDirectoryName(vrcPath) ?? "",
                UseShellExecute = true
            });
#else
            // On Linux, launch via Steam so Proton is applied automatically
            Process.Start(new ProcessStartInfo
            {
                FileName = "steam",
                Arguments = "steam://rungameid/438100",
                UseShellExecute = false
            });
#endif
            _core.SendToJS("log", new { msg = "Launched VRChat", color = "ok" });

            foreach (var exe in _core.Settings.ExtraExe)
            {
                try
                {
                    if (!File.Exists(exe))
                    {
                        _core.SendToJS("log", new { msg = $"Not found: {Path.GetFileName(exe)}", color = "warn" });
                        continue;
                    }
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                        UseShellExecute = true
                    });
                    _core.SendToJS("log", new { msg = $"Launched: {Path.GetFileName(exe)}", color = "ok" });
                }
                catch (Exception ex)
                {
                    _core.SendToJS("log", new { msg = $"Failed to launch {Path.GetFileName(exe)}: {ex.Message}", color = "err" });
                }
            }
        }
        catch (Exception ex)
        {
            _core.SendToJS("log", new { msg = $"Launch error: {ex.Message}", color = "err" });
        }
    }

    // Static process checks

    public static bool IsVrcRunning() =>
        Process.GetProcessesByName("VRChat").Any(p => { try { return !p.HasExited; } catch { return false; } });

    public static bool IsSteamVrRunning() =>
        Process.GetProcessesByName("vrserver").Any(p => { try { return !p.HasExited; } catch { return false; } });

    // WebSocket lifecycle

    public void StopWebSocket()
    {
        _wsService?.Stop();
    }

    public void StartWebSocket()
    {
        var (auth, tfa) = _core.VrcApi.GetCookies();
        if (string.IsNullOrEmpty(auth)) return;

        _wsService?.Dispose();
        _wsService = new VRChatWebSocketService();

        // Wire all friend-related WebSocket events to FriendsController
        _friends.WireWebSocket(_wsService);

        // Wire all notification-related WebSocket events to NotificationsController
        _notifications.WireWebSocket(_wsService);

        // Small delay so the VRC API reflects the new location before we query it
        _wsService.OwnLocationChanged += (_, _) =>
        {
            if (_core.VrcApi.IsLoggedIn)
                _ = Task.Delay(3000).ContinueWith(_ => _instance.GetCurrentInstanceAsync());
        };

        // user-update: own profile changed in-game (status, bio, statusDescription, tags, icon…)
        // The WS payload is partial (no pronouns/bioLinks), so fetch the full user from REST
        // then push everything to JS in one shot — sidebar, modal, Discord presence all update.
        _wsService.OwnUserUpdated += (_, _) =>
        {
            if (!_core.VrcApi.IsLoggedIn) return;
            _ = Task.Run(async () =>
            {
                var full = await _core.VrcApi.RefreshCurrentUserAsync();
                if (full != null)
                    Invoke(() => OnOwnUserUpdated?.Invoke(full));
            });
        };

        // All log calls must use Invoke(); these fire on the WebSocket background thread
        _wsService.Connected += (_, _) =>
        {
            Invoke(() =>
            {
                _core.SendToJS("wsStatus", new { connected = true });
                _core.SendToJS("log", new { msg = "[WS] Connected to pipeline.vrchat.cloud", color = "ok" });
            });
        };

        _wsService.Disconnected += (_, _) =>
        {
            Invoke(() =>
            {
                _core.SendToJS("wsStatus", new { connected = false });
                _core.SendToJS("log", new { msg = "[WS] Disconnected — reconnecting...", color = "warn" });
            });
        };

        // Real connection failure (not watchdog idle-timeout "No data for Xs") → one REST fallback.
        // Watchdog aborts the socket after 75s of silence and reconnects automatically — no REST needed.
        _wsService.ConnectError += (_, err) =>
        {
            Invoke(() => _core.SendToJS("log", new { msg = $"[WS] Error: {err}", color = "err" }));
            if (!err.StartsWith("No data for") && _core.VrcApi.IsLoggedIn)
            {
                _ = _friends.RefreshFriendsAsync(true);
                _ = _notifications.GetNotificationsAsync();
            }
        };

        // Pass a delegate so the service fetches fresh cookies on every internal reconnect
        _wsService.Start(auth, tfa ?? "", () =>
        {
            var (a, t) = _core.VrcApi.GetCookies();
            return (a ?? "", t ?? "");
        });
    }

    // Disposal

    public void Dispose()
    {
        try { _vcProcess?.Kill(entireProcessTree: true); } catch { }
        _wsService?.Dispose();
    }

    // Photino compatibility shim
    private static void Invoke(Action action) => action();
}
