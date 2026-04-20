#if WINDOWS
using System.Runtime.InteropServices;
#endif
using Newtonsoft.Json.Linq;

namespace VRCNext;

// Owns all window chrome (borderless frame), P/Invoke subclassing, and window action messages.

public class WindowController
{
    private readonly CoreLibrary _core;

    public WindowController(CoreLibrary core)
    {
        _core = core;
    }

#if WINDOWS
    // P/Invoke declarations
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern int SendMessage(nint hWnd, int Msg, int wParam, int lParam);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
    // comctl32 proper subclassing API — safe under nested message loops and window teardown
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint hWnd, SUBCLASSPROC pfn, nuint uId, nuint dwRefData);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint hWnd, SUBCLASSPROC pfn, nuint uId);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hWnd);

    private const int SW_HIDE    = 0;
    private const int SW_SHOW    = 5;
    private const int SW_RESTORE = 9;

    /// <summary>
    /// When true, minimize actions hide the window to the system tray instead.
    /// No window style changes — animations and taskbar behaviour stay native.
    /// </summary>
    private static volatile bool _minimizeToTray;

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int leftWidth, rightWidth, topHeight, bottomHeight; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate nint SUBCLASSPROC(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);
    private static SUBCLASSPROC? _subclassProc; // must stay rooted — prevents GC collection of the delegate
    private static volatile bool _ncDestroyed;  // set on WM_NCDESTROY; guards re-entrant calls and post-destroy messages
    private const nuint SubclassId = 1;
    internal static volatile Action? OnMinimized; // set from AppShell; called on any minimize/hide-to-tray

    private static nint SubclassWndProc(nint hWnd, uint msg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData)
    {
        const uint WM_DESTROY    = 0x0002;
        const uint WM_SIZE       = 0x0005;
        const uint WM_NCDESTROY  = 0x0082;
        const uint WM_NCCALCSIZE = 0x0083;
        const uint WM_NCHITTEST  = 0x0084;
        const uint WM_SYSCOMMAND = 0x0112;
        const int  SC_MINIMIZE   = 0xF020;

        if (msg == WM_NCDESTROY)
        {
            _subclassProc = null;
            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        if (_ncDestroyed)
            return 0;

        if (msg == WM_DESTROY)
        {
            _ncDestroyed = true;
            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        if (msg == WM_SIZE && wParam == 1 /*SIZE_MINIMIZED*/)
        {
            OnMinimized?.Invoke();
            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        if (msg == WM_NCCALCSIZE && wParam == 1)
            return 0;

        if (msg == WM_SYSCOMMAND && (wParam.ToInt32() & 0xFFF0) == SC_MINIMIZE && _minimizeToTray)
        {
            ShowWindow(hWnd, SW_HIDE);
            OnMinimized?.Invoke();
            return 0;
        }

        if (msg == WM_NCHITTEST)
        {
            var hit = DefSubclassProc(hWnd, msg, wParam, lParam);
            if (hit == 1 /*HTCLIENT*/)
            {
                const int border = 8;
                int x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
                int y = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
                GetWindowRect(hWnd, out var rc);
                bool l = x < rc.Left   + border, r = x > rc.Right  - border;
                bool t = y < rc.Top    + border, b = y > rc.Bottom - border;
                if (t && l) return 13; if (t && r) return 14;
                if (b && l) return 16; if (b && r) return 17;
                if (t) return 12; if (b) return 15;
                if (l) return 10; if (r) return 11;
            }
            return hit;
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    private void InstallWndProcSubclass(nint hWnd)
    {
        if (_subclassProc != null) return; // already installed
        _ncDestroyed = false;
        _subclassProc = SubclassWndProc;
        SetWindowSubclass(hWnd, _subclassProc, SubclassId, 0);
    }

    /// <summary>
    /// Enables or disables "minimize to tray" mode.
    /// Does NOT change any window styles — just sets a flag.
    /// When active, minimize actions (JS button, Win+D, etc.) hide the window via SW_HIDE.
    /// </summary>
    public void SetHideFromTaskbar(bool hide)
    {
        _minimizeToTray = hide;
    }

    public bool IsWindowHidden()
    {
        var window = _core.Window;
        return window == null || !IsWindowVisible(window.WindowHandle);
    }

    /// <summary>
    /// Unconditionally hides the window (SW_HIDE). Used on startup to auto-hide to tray.
    /// </summary>
    public void HideWindow()
    {
        var window = _core.Window;
        if (window == null) return;
        ShowWindow(window.WindowHandle, SW_HIDE);
    }

    /// <summary>
    /// Toggles the Photino window visibility (used by tray icon left-click).
    /// </summary>
    public void ToggleWindowVisibility()
    {
        var window = _core.Window;
        if (window == null) return;
        var hWnd = window.WindowHandle;
        if (IsIconic(hWnd))
        {
            ShowWindow(hWnd, SW_RESTORE);
            SetForegroundWindow(hWnd);
        }
        else if (!IsWindowVisible(hWnd))
        {
            ShowWindow(hWnd, SW_SHOW);
            SetForegroundWindow(hWnd);
        }
        else
        {
            ShowWindow(hWnd, SW_HIDE);
        }
    }
#endif

    /// <summary>
    /// Sets the native title bar background color via DWM (Windows 11+).
    /// hex must be in #RRGGBB format. No-op on unsupported OS versions.
    /// </summary>
    public void ApplyDwmCaptionColor(string hex)
    {
#if WINDOWS
        var window = _core.Window;
        if (window == null) return;
        hex = hex.TrimStart('#');
        if (hex.Length != 6) return;
        try
        {
            int r = Convert.ToInt32(hex[..2], 16);
            int g = Convert.ToInt32(hex[2..4], 16);
            int b = Convert.ToInt32(hex[4..6], 16);
            int colorRef = r | (g << 8) | (b << 16); // COLORREF = 0x00BBGGRR
            _ = DwmSetWindowAttribute(window.WindowHandle, 35 /*DWMWA_CAPTION_COLOR*/, ref colorRef, 4);
        }
        catch { }
#endif
    }

    // Install Chrome (called from "ready" message)

    public void InstallChrome()
    {
#if WINDOWS
        var window = _core.Window;
        if (window == null) return;
        var hWnd = window.WindowHandle;
        // Subclass FIRST — WM_NCCALCSIZE handler must be active before SWP_FRAMECHANGED
        // so the title bar is never rendered even for a single frame
        InstallWndProcSubclass(hWnd);
        // Full OVERLAPPEDWINDOW styles → snap/tile/Win11 snap layouts all work
        const int GWL_STYLE      = -16;
        const int WS_THICKFRAME  = 0x00040000;
        const int WS_CAPTION     = 0x00C00000;
        const int WS_SYSMENU     = 0x00080000;
        const int WS_MINIMIZEBOX = 0x00020000;
        const int WS_MAXIMIZEBOX = 0x00010000;
        SetWindowLong(hWnd, GWL_STYLE,
            GetWindowLong(hWnd, GWL_STYLE) | WS_THICKFRAME | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        // SWP_FRAMECHANGED triggers WM_NCCALCSIZE — subclass returns 0 → no NC area drawn
        SetWindowPos(hWnd, 0, 0, 0, 0, 0, 0x0020 | 0x0001 | 0x0002 | 0x0004);
        // Win11 rounded corners
        int cornerPref = 2;
        _ = DwmSetWindowAttribute(hWnd, 33, ref cornerPref, 4);
#endif
    }

    // GetCenteredLocation (used by Run())

    public static (int x, int y) GetCenteredLocation(int w, int h)
    {
#if WINDOWS
        return (Math.Max(0, (GetSystemMetrics(0) - w) / 2), Math.Max(0, (GetSystemMetrics(1) - h) / 2));
#else
        return (100, 50);
#endif
    }

    // Message Handler

    public void HandleMessage(string action, JObject msg)
    {
        var window = _core.Window;
        if (window == null) return;

        switch (action)
        {
            case "windowMinimize":
#if WINDOWS
                if (_minimizeToTray)
                {
                    ShowWindow(window.WindowHandle, SW_HIDE);
                    OnMinimized?.Invoke(); // SW_HIDE does not send WM_SIZE
                }
                else
#endif
                    window.SetMinimized(true); // → WM_SIZE SIZE_MINIMIZED → OnMinimized via subclass
                break;
            case "windowMaximize":
                var nowMax = window.Maximized;
                window.SetMaximized(!nowMax);
                _core.SendToJS("windowMaxState", !nowMax);
                break;
            case "windowClose":
                window.Close();
                break;
            case "windowDragStart":
#if WINDOWS
                // SC_MOVE on a maximized window: Windows natively restores+repositions on drag.
                // Do NOT manually restore here — that would break double-click restore.
                ReleaseCapture();
                SendMessage(window.WindowHandle, 0x0112, 0xF012, 0); // WM_SYSCOMMAND SC_MOVE
#endif
                break;
            case "windowResizeStart":
#if WINDOWS
                if (!window.Maximized)
                {
                    var htCode = msg["direction"]?.ToString() switch {
                        "w"  => 10, // HTLEFT
                        "e"  => 11, // HTRIGHT
                        "n"  => 12, // HTTOP
                        "nw" => 13, // HTTOPLEFT
                        "ne" => 14, // HTTOPRIGHT
                        "s"  => 15, // HTBOTTOM
                        "sw" => 16, // HTBOTTOMLEFT
                        "se" => 17, // HTBOTTOMRIGHT
                        _    => 0
                    };
                    if (htCode != 0) { ReleaseCapture(); SendMessage(window.WindowHandle, 0x00A1, htCode, 0); }
                }
#endif
                break;
            case "getCursorFiles":
            {
                var cursorDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "cursor");
                var cursorFiles = Directory.Exists(cursorDir)
                    ? Directory.GetFiles(cursorDir, "*", SearchOption.TopDirectoryOnly)
                        .Where(f => { var ext = Path.GetExtension(f).ToLower(); return ext == ".cur" || ext == ".png"; })
                        .Select(Path.GetFileName)
                        .OrderBy(f => f)
                        .ToList()
                    : new List<string?>();
                _core.SendToJS("cursorFiles", new { files = cursorFiles, port = _core.HttpPort });
                break;
            }
        }
    }
}
