#if WINDOWS
using Newtonsoft.Json.Linq;
using VRCNext.Services;

namespace VRCNext;

// Owns all VR wrist-overlay state, message handling, and lifecycle.

public class VROverlayController : IDisposable
{
    private readonly CoreLibrary _core;
    private readonly FriendsController _friends;

    // Field (moved from MainForm.Fields.cs)
    private VROverlayService? _vrOverlay;

    // Callbacks (set by MainForm after creation)
    public Action<int>? OnToolToggle { get; set; }
    public Func<(bool discord, bool voice, bool ytFix, bool space, bool relay, bool chatbox)>? GetToolStates { get; set; }

    public VROverlayController(CoreLibrary core, FriendsController friends)
    {
        _core = core;
        _friends = friends;
    }

    // Message Handler

    public async Task HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "vroConnect":
            {
                _vrOverlay ??= new VROverlayService(
                    s => Invoke(() => _core.SendToJS("log", new { msg = s, color = "sec" })));
                _vrOverlay.OnStateUpdate    += d => Invoke(() => _core.SendToJS("vroState", d));
                _vrOverlay.OnKeybindRecorded += (ids, names, hand, mode) =>
                    Invoke(() => _core.SendToJS("vroKeybindRecorded", new { ids, names, hand, mode }));
                _vrOverlay.OnToolToggle    += idx => Invoke(() => OnToolToggle?.Invoke(idx));
                _vrOverlay.OnJoinRequest   += (fid, loc) => Invoke(async () =>
                {
                    bool ok = await _core.VrcApi.InviteSelfAsync(loc);
                    _core.SendToJS("log", new { msg = ok ? "Self-invite sent — check VRChat notifications!" : "Failed to send self-invite.", color = ok ? "ok" : "err" });
                });
                _vrOverlay.OnToastSound += () => Invoke(() => _core.SendToJS("vroPlayToastSound", new { }));

                // JS sends the resolved theme colors inline with the connect
                // message so we can seed the overlay immediately — no round-trip.
                if (msg["themeColors"] is JObject tc)
                {
                    var dict = tc.Properties()
                        .ToDictionary(p => p.Name, p => p.Value.ToString());
                    _vrOverlay.SetThemeColors(dict);
                }
                else
                {
                    // Fallback: use the hardcoded palette (built-in themes only)
                    _vrOverlay.SetTheme(_core.Settings.Theme);
                }
                _vrOverlay.ApplyConfig(
                    _core.Settings.VroAttachLeft, _core.Settings.VroAttachHand,
                    _core.Settings.VroPosX, _core.Settings.VroPosY, _core.Settings.VroPosZ,
                    _core.Settings.VroRotX, _core.Settings.VroRotY, _core.Settings.VroRotZ,
                    _core.Settings.VroWidth, _core.Settings.VroKeybind, _core.Settings.VroKeybindHand,
                    _core.Settings.VroKeybindMode, _core.Settings.VroKeybindDt, _core.Settings.VroKeybindDtHand,
                    _core.Settings.VroControlRadius);

                _vrOverlay.ApplyToastConfig(
                    _core.Settings.VroToastEnabled, _core.Settings.VroToastFavOnly,
                    _core.Settings.VroToastSize, _core.Settings.VroToastOffsetX, _core.Settings.VroToastOffsetY,
                    _core.Settings.VroToastOnline, _core.Settings.VroToastOffline,
                    _core.Settings.VroToastGps, _core.Settings.VroToastStatus,
                    _core.Settings.VroToastStatusDesc, _core.Settings.VroToastBio);

                bool ok = _vrOverlay.Connect();
                _core.VrOverlay = _vrOverlay;
                if (ok) { _vrOverlay.StartPolling(); _friends.PushVroLocations(); }
                UpdateToolStates();
                _core.SendToJS("vroState", new
                {
                    connected    = ok,
                    visible      = false,
                    recording    = false,
                    keybind       = _core.Settings.VroKeybind,
                    keybindNames  = new List<string>(),
                    keybindHand   = _core.Settings.VroKeybindHand,
                    keybindMode   = _core.Settings.VroKeybindMode,
                    keybindDt     = _core.Settings.VroKeybindDt,
                    keybindDtHand = _core.Settings.VroKeybindDtHand,
                    error         = ok ? null : _vrOverlay.LastError
                });
                break;
            }

            case "overlayThemeColors":
            {
                if (_vrOverlay != null && msg["colors"] is JObject colors)
                {
                    var dict = colors.Properties()
                        .ToDictionary(p => p.Name, p => p.Value.ToString());
                    _vrOverlay.SetThemeColors(dict);
                }
                break;
            }

            case "vroDisconnect":
                _vrOverlay?.Disconnect();
                _vrOverlay?.Dispose();
                _vrOverlay = null;
                _core.VrOverlay = null;
                _core.SendToJS("vroState", new { connected = false, visible = false, recording = false });
                break;

            case "vroShow":
                _vrOverlay?.Show();
                break;

            case "vroHide":
                _vrOverlay?.Hide();
                break;

            case "vroToggle":
                _vrOverlay?.Toggle();
                break;

            case "vroConfig":
            {
                bool left   = msg["attachLeft"]?.Value<bool>() ?? true;
                bool hand   = msg["attachHand"]?.Value<bool>() ?? true;
                float px    = msg["posX"]?.Value<float>() ?? 0f;
                float py    = msg["posY"]?.Value<float>() ?? 0.07f;
                float pz    = msg["posZ"]?.Value<float>() ?? -0.05f;
                float rx    = msg["rotX"]?.Value<float>() ?? -80f;
                float ry    = msg["rotY"]?.Value<float>() ?? 0f;
                float rz    = msg["rotZ"]?.Value<float>() ?? 0f;
                float width = msg["width"]?.Value<float>() ?? 0.22f;
                var kb        = msg["keybind"]?.ToObject<List<uint>>() ?? new();
                int kbHand    = msg["keybindHand"]?.Value<int>() ?? 0;
                int kbMode    = msg["keybindMode"]?.Value<int>() ?? 0;
                var kbDt      = msg["keybindDt"]?.ToObject<List<uint>>() ?? new();
                int kbDtHand  = msg["keybindDtHand"]?.Value<int>() ?? 0;
                int ctrlR     = msg["controlRadius"]?.Value<int>() ?? 28;

                _core.Settings.VroAttachLeft    = left;
                _core.Settings.VroAttachHand    = hand;
                _core.Settings.VroPosX = px; _core.Settings.VroPosY = py; _core.Settings.VroPosZ = pz;
                _core.Settings.VroRotX = rx; _core.Settings.VroRotY = ry; _core.Settings.VroRotZ = rz;
                _core.Settings.VroWidth         = width;
                _core.Settings.VroKeybind       = kb;
                _core.Settings.VroKeybindHand   = kbHand;
                _core.Settings.VroKeybindMode   = kbMode;
                _core.Settings.VroKeybindDt     = kbDt;
                _core.Settings.VroKeybindDtHand = kbDtHand;
                _core.Settings.VroControlRadius = ctrlR;
                _core.Settings.Save();

                _vrOverlay?.ApplyConfig(left, hand, px, py, pz, rx, ry, rz, width, kb, kbHand, kbMode, kbDt, kbDtHand, ctrlR);
                break;
            }

            case "vroAutoSave":
            {
                _core.Settings.VroAutoStart   = msg["autoStart"]?.Value<bool>()   ?? false; // legacy
                _core.Settings.VroAutoStartVR = msg["autoStartVR"]?.Value<bool>() ?? false;
                _core.Settings.Save();
                break;
            }

            case "vroRecordKeybind":
                _vrOverlay?.StartKeybindRecording();
                break;

            case "vroCancelRecording":
                _vrOverlay?.StopKeybindRecording();
                break;

            case "vroSetTab":
                _vrOverlay?.SetActiveTab(msg["tab"]?.Value<int>() ?? 0);
                break;

            case "vroToastConfig":
            {
                bool enabled    = msg["enabled"]?.Value<bool>()    ?? true;
                bool favOnly    = msg["favOnly"]?.Value<bool>()    ?? false;
                int  size       = msg["size"]?.Value<int>()        ?? 50;
                float offX      = msg["offsetX"]?.Value<float>()   ?? 0f;
                float offY      = msg["offsetY"]?.Value<float>()   ?? -0.12f;
                bool online     = msg["online"]?.Value<bool>()     ?? true;
                bool offline    = msg["offline"]?.Value<bool>()    ?? true;
                bool gps        = msg["gps"]?.Value<bool>()        ?? true;
                bool status     = msg["status"]?.Value<bool>()     ?? true;
                bool statusDesc = msg["statusDesc"]?.Value<bool>() ?? true;
                bool bio        = msg["bio"]?.Value<bool>()        ?? true;

                _core.Settings.VroToastEnabled    = enabled;
                _core.Settings.VroToastFavOnly    = favOnly;
                _core.Settings.VroToastSize       = size;
                _core.Settings.VroToastOffsetX    = offX;
                _core.Settings.VroToastOffsetY    = offY;
                _core.Settings.VroToastOnline     = online;
                _core.Settings.VroToastOffline    = offline;
                _core.Settings.VroToastGps        = gps;
                _core.Settings.VroToastStatus     = status;
                _core.Settings.VroToastStatusDesc = statusDesc;
                _core.Settings.VroToastBio        = bio;
                _core.Settings.Save();

                _vrOverlay?.ApplyToastConfig(enabled, favOnly, size, offX, offY,
                    online, offline,
                    gps, status, statusDesc, bio);
                break;
            }
        }
    }

    // VR Overlay tool-state sync

    public void UpdateToolStates()
    {
        if (_vrOverlay == null) return;
        var states = GetToolStates?.Invoke() ?? default;
        _vrOverlay.SetToolStates(states.discord, states.voice, states.ytFix, states.space, states.relay, states.chatbox);
    }

    // Dispose

    public void Dispose()
    {
        _vrOverlay?.Dispose();
        _vrOverlay = null;
        _core.VrOverlay = null;
    }

    // Photino compatibility shim
    private static void Invoke(Action action) => action();
}
#else
namespace VRCNext;

// Stub for non-Windows platforms — all methods are no-ops.
public class VROverlayController : IDisposable
{
    public Action<int>? OnToolToggle { get; set; }
    public Func<(bool discord, bool voice, bool ytFix, bool space, bool relay, bool chatbox)>? GetToolStates { get; set; }

    public VROverlayController(CoreLibrary core, FriendsController friends) { }
    public Task HandleMessage(string action, Newtonsoft.Json.Linq.JObject msg) => Task.CompletedTask;
    public void UpdateToolStates() { }
    public void Dispose() { }
}
#endif
