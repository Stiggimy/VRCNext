using Newtonsoft.Json.Linq;
using VRCNext.Services;

namespace VRCNext;

// Owns all Space Flight (SteamVR playspace drag) state, logic, and message handling.

public class SpaceFlightController : IDisposable
{
    private readonly CoreLibrary _core;
    private readonly VROverlayController _vroCtrl;

    // Field (moved from MainForm.Fields.cs)
    private SteamVRService? _steamVR;

    // Public Accessors (for other domains)
    public SteamVRService? SteamVR => _steamVR;
    public bool IsConnected => _steamVR != null;

    public SpaceFlightController(CoreLibrary core, VROverlayController vroCtrl)
    {
        _core = core;
        _vroCtrl = vroCtrl;
    }

    // Message Handler

    public void HandleMessage(string action, JObject msg)
    {
        switch (action)
        {
            case "sfConnect":
                {
                    _steamVR ??= new SteamVRService(s => Invoke(() => _core.SendToJS("log", new { msg = s, color = "sec" })));
                    _steamVR.SetUpdateCallback(data => {
                        try { Invoke(() => _core.SendToJS("sfUpdate", data)); } catch { }
                    });
                    if (_steamVR.Connect())
                    {
                        _steamVR.ApplyConfig(_core.Settings.SfMultiplier, _core.Settings.SfLockX, _core.Settings.SfLockY, _core.Settings.SfLockZ,
                            _core.Settings.SfLeftHand, _core.Settings.SfRightHand, _core.Settings.SfUseGrip);
                        _steamVR.StartPolling();
                    }
                    _vroCtrl.UpdateToolStates();
                }
                break;
            case "sfDisconnect":
                _steamVR?.Disconnect();
                _steamVR = null;
                _core.SendToJS("sfUpdate", new { connected = false, dragging = false, offsetX = 0, offsetY = 0, offsetZ = 0,
                    leftController = false, rightController = false, error = (string?)null });
                _vroCtrl.UpdateToolStates();
                break;
            case "sfReset":
                _steamVR?.ResetOffset();
                break;
            case "sfConfig":
                {
                    var mult = msg["dragMultiplier"]?.Value<float>() ?? 1f;
                    var lx = msg["lockX"]?.Value<bool>() ?? false;
                    var ly = msg["lockY"]?.Value<bool>() ?? false;
                    var lz = msg["lockZ"]?.Value<bool>() ?? false;
                    var lh = msg["leftHand"]?.Value<bool>() ?? false;
                    var rh = msg["rightHand"]?.Value<bool>() ?? true;
                    var grip = msg["useGrip"]?.Value<bool>() ?? true;
                    _steamVR?.ApplyConfig(mult, lx, ly, lz, lh, rh, grip);
                }
                break;
        }
    }

    // Toggle (called from VR overlay)

    public void Toggle()
    {
        if (_steamVR != null)
        {
            _steamVR.Disconnect();
            _steamVR = null;
            _core.SendToJS("sfUpdate", new { connected = false, dragging = false,
                offsetX = 0, offsetY = 0, offsetZ = 0,
                leftController = false, rightController = false, error = (string?)null });
        }
        else
        {
            _steamVR ??= new SteamVRService(s => Invoke(() => _core.SendToJS("log", new { msg = s, color = "sec" })));
            _steamVR.SetUpdateCallback(data => { try { Invoke(() => _core.SendToJS("sfUpdate", data)); } catch { } });
            bool sfOk = _steamVR.Connect();
            if (sfOk)
            {
                _steamVR.ApplyConfig(_core.Settings.SfMultiplier, _core.Settings.SfLockX, _core.Settings.SfLockY, _core.Settings.SfLockZ,
                    _core.Settings.SfLeftHand, _core.Settings.SfRightHand, _core.Settings.SfUseGrip);
                _steamVR.StartPolling();
            }
            _core.SendToJS("sfUpdate", new { connected = sfOk, dragging = false,
                offsetX = 0, offsetY = 0, offsetZ = 0,
                leftController = false, rightController = false, error = sfOk ? (string?)null : _steamVR.LastError });
        }
    }

    // Disposal

    public void Dispose()
    {
        _steamVR?.Dispose();
        _steamVR = null;
    }

    // Photino compatibility shim
    private static void Invoke(Action action) => action();
}
