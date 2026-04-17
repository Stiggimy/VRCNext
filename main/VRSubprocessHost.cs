#if WINDOWS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace VRCNext;

/// <summary>
/// Manages the VR subprocess (VRCNext.exe --vr-subprocess).
/// VROverlayService + SteamVRService run inside that subprocess, isolated from VRCNext.exe.
/// If SteamVR hard-crashes (native AV), only the subprocess dies; VRCNext.exe survives.
/// </summary>
public sealed class VRSubprocessHost : IDisposable
{
    private readonly Action<string> _log;
    private Process? _process;
    private StreamWriter? _stdin;
    private readonly object _stdinLock = new();
    private CancellationTokenSource? _readCts;
    private bool _disposed;

    public bool VroConnected { get; private set; }
    public bool SfConnected  { get; private set; }

    // Events fired when the subprocess sends a message over stdout.
    public event Action<JObject>? OnVroState;
    public event Action<List<uint>, List<string>, int, int>? OnVroKeybindRecorded;
    public event Action<string, string>? OnVroJoinRequest;
    public event Action<string>? OnVroInviteFriend;
    public event Action<string, string, string, string>? OnVroNotifAccept;
    public event Action<int>? OnVroToolToggle;
    public event Action? OnVroToastSound;
    public event Action? OnVroWaterAlarm;
    public event Action? OnVroWaterDismissed;
    public event Action? OnVroQuit;
    public event Action<JObject>? OnSfUpdate;
    public event Action? OnSfQuit;

    public VRSubprocessHost(Action<string> log) => _log = log;

    /// <summary>Starts the subprocess if it isn't already running, then sends the init message.</summary>
    public void EnsureRunning(string cacheDir, int httpPort, string? authCookie, string? tfaCookie)
    {
        if (_process is { HasExited: false }) return;

        _readCts?.Cancel();
        _readCts = new CancellationTokenSource();

        var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
        var psi = new ProcessStartInfo(exe!, "--vr-subprocess")
        {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = false,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        _process = Process.Start(psi)!;
        _stdin   = _process.StandardInput;
        _stdin.AutoFlush = true;

        _process.EnableRaisingEvents = true;
        _process.Exited += OnProcessExited;

        _ = ReadLoopAsync(_process.StandardOutput, _readCts.Token);

        SendRaw(new JObject
        {
            ["t"]          = "init",
            ["cacheDir"]   = cacheDir,
            ["httpPort"]   = httpPort,
            ["authCookie"] = authCookie,
            ["tfaCookie"]  = tfaCookie,
        });

        _log("[VRSub] Subprocess started");
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        _log("[VRSub] Subprocess exited");
        bool wasVro = VroConnected;
        bool wasSf  = SfConnected;
        VroConnected = false;
        SfConnected  = false;
        lock (_stdinLock) _stdin = null;
        if (wasVro) OnVroQuit?.Invoke();
        if (wasSf)  OnSfQuit?.Invoke();
    }

    private void Kill()
    {
        if (_process != null)
        {
            _process.Exited -= OnProcessExited; // prevent spurious Exited event
            try { _process.Kill(); } catch { }
            _process = null;
        }
        lock (_stdinLock) _stdin = null;
    }

    private void SendRaw(JObject obj)
    {
        try
        {
            lock (_stdinLock)
                _stdin?.WriteLine(obj.ToString(Newtonsoft.Json.Formatting.None));
        }
        catch (Exception ex) { _log($"[VRSub] Send failed: {ex.Message}"); }
    }

    private void Send(string t, object? payload = null)
    {
        var obj = payload != null ? JObject.FromObject(payload) : new JObject();
        obj["t"] = t;
        SendRaw(obj);
    }

    private async Task ReadLoopAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break; // EOF
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { Dispatch(JObject.Parse(line)); }
                catch (Exception ex) { _log($"[VRSub] Parse error: {ex.Message}"); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"[VRSub] Reader crashed: {ex.Message}"); }
    }

    private void Dispatch(JObject msg)
    {
        var t = msg["t"]?.Value<string>() ?? "";
        msg.Remove("t");

        switch (t)
        {
            case "log":
                _log(msg["text"]?.Value<string>() ?? "");
                break;
            case "vro_state":
                OnVroState?.Invoke(msg);
                break;
            case "vro_keybind_recorded":
                OnVroKeybindRecorded?.Invoke(
                    msg["ids"]?.ToObject<List<uint>>()   ?? new(),
                    msg["names"]?.ToObject<List<string>>() ?? new(),
                    msg["hand"]?.Value<int>()  ?? 0,
                    msg["mode"]?.Value<int>()  ?? 0);
                break;
            case "vro_join_request":
                OnVroJoinRequest?.Invoke(
                    msg["friendId"]?.Value<string>()  ?? "",
                    msg["location"]?.Value<string>()  ?? "");
                break;
            case "vro_invite_friend":
                OnVroInviteFriend?.Invoke(msg["friendId"]?.Value<string>() ?? "");
                break;
            case "vro_notif_accept":
                OnVroNotifAccept?.Invoke(
                    msg["notifId"]?.Value<string>()   ?? "",
                    msg["notifType"]?.Value<string>() ?? "",
                    msg["senderId"]?.Value<string>()  ?? "",
                    msg["notifData"]?.Value<string>() ?? "");
                break;
            case "vro_tool_toggle":
                OnVroToolToggle?.Invoke(msg["index"]?.Value<int>() ?? 0);
                break;
            case "vro_toast_sound":
                OnVroToastSound?.Invoke();
                break;
            case "vro_water_alarm":
                OnVroWaterAlarm?.Invoke();
                break;
            case "vro_water_dismissed":
                OnVroWaterDismissed?.Invoke();
                break;
            case "sf_update":
                OnSfUpdate?.Invoke(msg);
                break;
        }
    }

    public void VroConnect()
    {
        VroConnected = true;
        Send("vro_connect");
    }

    public void VroDisconnect()
    {
        VroConnected = false;
        Send("vro_disconnect");
        if (!SfConnected) Kill();
    }

    public void VroShow()            => Send("vro_show");
    public void VroHide()            => Send("vro_hide");
    public void VroToggle()          => Send("vro_toggle");
    public void VroSetTab(int tab)   => Send("vro_set_tab",   new { tab });
    public void VroRecordKeybind()   => Send("vro_record_keybind");
    public void VroCancelRecording() => Send("vro_cancel_recording");

    public void VroConfig(bool attachLeft, bool attachHand,
        float px, float py, float pz, float rx, float ry, float rz, float width,
        List<uint> keybind, int keybindHand, int keybindMode,
        List<uint> keybindDt, int keybindDtHand, float controlRadius)
        => Send("vro_config", new { attachLeft, attachHand, px, py, pz, rx, ry, rz, width,
            keybind, keybindHand, keybindMode, keybindDt, keybindDtHand, controlRadius });

    public void VroApplyToastConfig(bool enabled, bool favOnly, int size, float offX, float offY,
        bool online, bool offline, bool gps, bool status, bool statusDesc, bool bio,
        int durationSec, int stackSize, bool friendReq, bool invite, bool groupInv)
        => Send("vro_toast_config", new { enabled, favOnly, size, offX, offY,
            online, offline, gps, status, statusDesc, bio, durationSec, stackSize,
            friendReq, invite, groupInv });

    public void VroThemeColors(Dictionary<string, string> colors)
        => Send("vro_theme_colors", new { colors });

    public void VroWaterConfig(bool enabled, int intervalSec)
        => Send("vro_water_config", new { enabled, intervalSec });

    public void VroSetLanguage(string lang)
        => Send("vro_set_language", new { lang });

    // These match VROverlayService's public API so callers on _core.VrOverlay compile unchanged.
    public void AddNotification(string evType, string friendName, string evText, string time,
        string imageUrl = "", string friendId = "", string location = "", string notifId = "", string notifData = "")
        => Send("vro_add_notif", new { evType, friendName, evText, time, imageUrl, friendId, location, notifId, notifData });

    public void UpdateNotification(string notifId, string? newText = null, string? newImageUrl = null, string? newFriendName = null)
        => Send("vro_update_notif", new { notifId, newText, newImageUrl, newFriendName });

    public void EnqueueToast(string evType, string friendName, string evText, string time,
        string imageUrl, bool isFavorited)
        => Send("vro_enqueue_toast", new { evType, friendName, evText, time, imageUrl, isFavorited });

    public void SetFriendLocations(IReadOnlyList<(string worldId, string instanceId, string worldName,
        string worldImageUrl, string friendId, string friendName, string friendImageUrl, string location)> entries)
    {
        var list = entries.Select(e => new {
            e.worldId, e.instanceId, e.worldName, e.worldImageUrl,
            e.friendId, e.friendName, e.friendImageUrl, e.location
        }).ToList();
        Send("vro_set_locations", new { entries = list });
    }

    public void SetOnlineFriends(IReadOnlyList<(string friendId, string friendName,
        string friendImageUrl, string status, string statusDescription, string location, string worldName)> entries)
    {
        var list = entries.Select(e => new {
            e.friendId, e.friendName, e.friendImageUrl,
            e.status, e.statusDescription, e.location, e.worldName
        }).ToList();
        Send("vro_set_online_friends", new { entries = list });
    }

    public void UpdateMediaInfo(string title, string artist, double position, double duration, bool playing)
        => Send("vro_update_media", new { title, artist, position, duration, playing });

    public void SetToolStates(bool discord, bool voice, bool ytFix, bool space, bool relay, bool chatbox)
        => Send("vro_tool_states", new { discord, voice, ytFix, space, relay, chatbox });

    public void SfConnect(float multiplier, bool lockX, bool lockY, bool lockZ,
        bool leftHand, bool rightHand, bool useGrip)
    {
        SfConnected = true;
        Send("sf_connect", new { multiplier, lockX, lockY, lockZ, leftHand, rightHand, useGrip });
    }

    public void SfDisconnect()
    {
        SfConnected = false;
        Send("sf_disconnect");
        if (!VroConnected) Kill();
    }

    public void SfConfig(float multiplier, bool lockX, bool lockY, bool lockZ,
        bool leftHand, bool rightHand, bool useGrip)
        => Send("sf_config", new { multiplier, lockX, lockY, lockZ, leftHand, rightHand, useGrip });

    public void SfReset() => Send("sf_reset");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _readCts?.Cancel();
        Kill();
        _readCts?.Dispose();
    }
}
#else
namespace VRCNext;

public sealed class VRSubprocessHost : IDisposable
{
    public bool VroConnected { get; private set; }
    public bool SfConnected  { get; private set; }

    public VRSubprocessHost(Action<string> log) { }
    public void EnsureRunning(string c, int p, string? a, string? t) { }
    public void VroConnect()    { }
    public void VroDisconnect() { }
    public void VroShow()       { }
    public void VroHide()       { }
    public void VroToggle()     { }
    public void VroSetTab(int tab) { }
    public void VroRecordKeybind()   { }
    public void VroCancelRecording() { }
    public void VroConfig(bool a, bool b, float c, float d, float e, float f, float g, float h, float i,
        System.Collections.Generic.List<uint> j, int k, int l,
        System.Collections.Generic.List<uint> m, int n, float o) { }
    public void VroApplyToastConfig(bool a, bool b, int c, float d, float e,
        bool f, bool g, bool h, bool i, bool j, bool k, int l, int m, bool n, bool o, bool p) { }
    public void VroThemeColors(System.Collections.Generic.Dictionary<string, string> colors) { }
    public void AddNotification(string a, string b, string c, string d,
        string e = "", string f = "", string g = "", string h = "", string i = "") { }
    public void UpdateNotification(string a, string? b = null, string? c = null, string? d = null) { }
    public void EnqueueToast(string a, string b, string c, string d, string e, bool f) { }
    public void SetFriendLocations(System.Collections.Generic.IReadOnlyList<(string, string, string, string, string, string, string, string)> entries) { }
    public void SetOnlineFriends(System.Collections.Generic.IReadOnlyList<(string, string, string, string, string, string, string)> entries) { }
    public void UpdateMediaInfo(string a, string b, double c, double d, bool e) { }
    public void SetToolStates(bool a, bool b, bool c, bool d, bool e, bool f) { }
    public void SfConnect(float a, bool b, bool c, bool d, bool e, bool f, bool g) { }
    public void SfDisconnect() { }
    public void SfConfig(float a, bool b, bool c, bool d, bool e, bool f, bool g) { }
    public void SfReset() { }
    public void VroWaterConfig(bool enabled, int intervalSec) { }
    public void VroSetLanguage(string lang) { }
    public event System.Action? OnVroWaterAlarm;
    public event System.Action? OnVroWaterDismissed;
    public void Dispose() { }
}
#endif
