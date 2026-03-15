using Newtonsoft.Json.Linq;
using VRCNext.Services;

namespace VRCNext;

// Owns all Chatbox + OSC state, logic, and message handling.

public class ChatboxController : IDisposable
{
    private readonly CoreLibrary _core;
    private readonly VROverlayController _vroCtrl;

    // Fields (moved from MainForm.Fields.cs)
    private ChatboxService? _chatbox;
    private OscService? _osc;

    // Public Accessors (for other domains)
    public bool IsEnabled => _chatbox?.Enabled ?? false;
    public OscService? Osc => _osc;

    public ChatboxController(CoreLibrary core, VROverlayController vroCtrl)
    {
        _core = core;
        _vroCtrl = vroCtrl;
    }

    // Message Handler

    public void HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "chatboxConfig":
                {
                    _chatbox ??= new ChatboxService(s => Invoke(() => _core.SendToJS("log", new { msg = s, color = "sec" })));
                    _chatbox.SetUpdateCallback(data => {
                        try { Invoke(() => _core.SendToJS("chatboxUpdate", data)); } catch { }
#if WINDOWS
                        try
                        {
                            var d = JObject.FromObject(data);
                            _core.VrOverlay?.UpdateMediaInfo(
                                d["currentTitle"]?.ToString() ?? "",
                                d["currentArtist"]?.ToString() ?? "",
                                d["currentPosition"]?.Value<double>() ?? 0,
                                d["currentDuration"]?.Value<double>() ?? 0,
                                d["isPlaying"]?.Value<bool>() ?? false);
                        }
                        catch { }
#endif
                    });

                    var enabled = msg["enabled"]?.Value<bool>() ?? false;
                    var showTime = msg["showTime"]?.Value<bool>() ?? true;
                    var showMedia = msg["showMedia"]?.Value<bool>() ?? true;
                    var showPlaytime = msg["showPlaytime"]?.Value<bool>() ?? true;
                    var showCustomText = msg["showCustomText"]?.Value<bool>() ?? true;
                    var showSystemStats = msg["showSystemStats"]?.Value<bool>() ?? false;
                    var showAfk = msg["showAfk"]?.Value<bool>() ?? false;
                    var afkMessage = msg["afkMessage"]?.ToString() ?? "Currently AFK";
                    var suppressSound = msg["suppressSound"]?.Value<bool>() ?? true;
                    var timeFormat = msg["timeFormat"]?.ToString() ?? "hh:mm tt";
                    var separator = msg["separator"]?.ToString() ?? " | ";
                    var intervalMs = msg["intervalMs"]?.Value<int>() ?? 5000;
                    var customLines = msg["customLines"]?.ToObject<List<string>>() ?? new();

                    _chatbox.ApplyConfig(enabled, showTime, showMedia, showPlaytime,
                        showCustomText, showSystemStats, showAfk, afkMessage,
                        suppressSound, timeFormat, separator, intervalMs, customLines);
                    _vroCtrl.UpdateToolStates();

                    // Persist chatbox settings
                    _core.Settings.CbShowTime = showTime;
                    _core.Settings.CbShowMedia = showMedia;
                    _core.Settings.CbShowPlaytime = showPlaytime;
                    _core.Settings.CbShowCustomText = showCustomText;
                    _core.Settings.CbShowSystemStats = showSystemStats;
                    _core.Settings.CbShowAfk = showAfk;
                    _core.Settings.CbAfkMessage = afkMessage;
                    _core.Settings.CbSuppressSound = suppressSound;
                    _core.Settings.CbTimeFormat = timeFormat;
                    _core.Settings.CbSeparator = separator;
                    _core.Settings.CbIntervalMs = intervalMs;
                    _core.Settings.CbCustomLines = customLines;
                    _core.Settings.Save();
                }
                break;

            case "chatboxStop":
                _chatbox?.Stop();
                _chatbox = null;
                _vroCtrl.UpdateToolStates();
                break;

            case "oscConnect":
                {
                    _osc ??= new OscService(s => Invoke(() => _core.SendToJS("log", new { msg = s, color = "sec" })));
                    _osc.SetParamCallback((name, val, type) => {
                        try { Invoke(() => _core.SendToJS("oscParam", new { name, value = val, type })); } catch { }
                    });
                    _osc.SetAvatarChangeCallback((avatarId, paramDefs) => {
                        try
                        {
                            var paramList = paramDefs.Select(p => new { p.Name, p.Type, p.HasInput, p.HasOutput }).ToList();
                            Invoke(() => _core.SendToJS("oscAvatarParams", new { avatarId, paramList }));
                        }
                        catch { }
                    });
                    bool oscOk = _osc.Start();
                    _core.SendToJS("oscState", new { connected = oscOk });
                    if (oscOk)
                    {
                        _ = Task.Run(async () =>
                        {
                            // Try OSCQuery first; gets all live values instantly (VRChat v2023.3.1+)
                            bool gotLive = await _osc.TryOscQueryAsync((name, val, type) =>
                            {
                                try { Invoke(() => _core.SendToJS("oscParam", new { name, value = val, type })); } catch { }
                            });
                            // Fallback: load config file as pending params so the full list is visible
                            if (!gotLive)
                            {
                                var (avatarId, paramDefs) = _osc.LoadMostRecentAvatarConfig();
                                if (paramDefs.Count > 0)
                                {
                                    var paramList = paramDefs.Select(p => new { p.Name, p.Type, p.HasInput, p.HasOutput }).ToList();
                                    Invoke(() => _core.SendToJS("oscAvatarParams", new { avatarId, paramList }));
                                }
                            }
                        });
                    }
                }
                break;

            case "oscDisconnect":
                _osc?.Stop();
                _core.SendToJS("oscState", new { connected = false });
                break;

            case "oscSend":
                {
                    var pName = msg["name"]?.ToString() ?? "";
                    var pType = msg["type"]?.ToString() ?? "";
                    if (_osc?.IsConnected != true)
                    {
                        _core.SendToJS("log", new { msg = $"[OSC] Send skipped — not connected (osc={_osc != null}, running={_osc?.IsConnected})", color = "err" });
                    }
                    else if (!string.IsNullOrEmpty(pName))
                    {
                        if (pType == "bool") _osc.SendBool(pName, msg["value"]?.Value<bool>() ?? false);
                        else if (pType == "float") _osc.SendFloat(pName, msg["value"]?.Value<float>() ?? 0f);
                        else if (pType == "int") _osc.SendInt(pName, msg["value"]?.Value<int>() ?? 0);
                    }
                }
                break;

            case "oscEnableOutputs":
                {
                    int filesUpdated = _osc != null ? _osc.EnableAllOutputs()
                        : new OscService(s => { }).EnableAllOutputs();
                    _core.SendToJS("oscOutputsEnabled", new { filesUpdated });
                }
                break;
        }
    }

    // Toggle (called from VR overlay)

    public void Toggle()
    {
        if (_chatbox != null)
        {
            _chatbox.Stop();
            _chatbox = null;
            _core.SendToJS("chatboxUpdate", new { enabled = false });
        }
        else
        {
            _chatbox = new ChatboxService(s => Invoke(() => _core.SendToJS("log", new { msg = s, color = "sec" })));
            _chatbox.SetUpdateCallback(data => { try { Invoke(() => _core.SendToJS("chatboxUpdate", data)); } catch { } });
            _chatbox.ApplyConfig(true, _core.Settings.CbShowTime, _core.Settings.CbShowMedia, _core.Settings.CbShowPlaytime,
                _core.Settings.CbShowCustomText, _core.Settings.CbShowSystemStats, _core.Settings.CbShowAfk, _core.Settings.CbAfkMessage,
                _core.Settings.CbSuppressSound, _core.Settings.CbTimeFormat, _core.Settings.CbSeparator, _core.Settings.CbIntervalMs, _core.Settings.CbCustomLines);
            _core.SendToJS("chatboxUpdate", new { enabled = true });
        }
    }

    // Disposal

    public void Dispose()
    {
        _chatbox?.Dispose();
        _chatbox = null;
        _osc?.Dispose();
        _osc = null;
    }

    // Photino compatibility shim
    private static void Invoke(Action action) => action();
}
