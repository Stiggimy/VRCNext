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
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);
    [DllImport("user32.dll")] private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint Msg, nint wParam, nint lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int leftWidth, rightWidth, topHeight, bottomHeight; }
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);
    private static WndProcDelegate? _subclassProc; // must stay rooted — prevents GC collection
    private static nint _origWndProc;

    private static nint SubclassWndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        const uint WM_NCCALCSIZE = 0x0083;
        const uint WM_NCHITTEST  = 0x0084;

        if (msg == WM_NCCALCSIZE && wParam == 1)
            return 0; // client area = full window rect; removes visible NC border/black strip; snap still works

        if (msg == WM_NCHITTEST)
        {
            var hit = CallWindowProc(_origWndProc, hWnd, msg, wParam, lParam);
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

        return CallWindowProc(_origWndProc, hWnd, msg, wParam, lParam);
    }

    private void InstallWndProcSubclass(nint hWnd)
    {
        if (_origWndProc != 0) return; // already installed — don't re-hook or _origWndProc points to itself
        _subclassProc = SubclassWndProc;
        _origWndProc  = SetWindowLongPtr(hWnd, -4 /*GWLP_WNDPROC*/,
            Marshal.GetFunctionPointerForDelegate(_subclassProc));
    }
#endif

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
                window.SetMinimized(true);
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
