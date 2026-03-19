#if WINDOWS
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace VRCNext;

/// <summary>
/// Native Windows system tray icon with a custom GDI+ rendered popup menu.
/// Runs on its own STA thread with a dedicated Windows message pump.
/// </summary>
public class SystemTrayService : IDisposable
{
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);

    private Thread? _trayThread;
    private NotifyIcon? _trayIcon;
    private TrayPopupForm? _popup;
    private SynchronizationContext? _syncCtx;

    // Cached user data (written from main thread, read from tray thread)
    private readonly object _dataLock = new();
    private string _displayName = "";
    private string _status = "offline";
    private string _statusDescription = "";
    private Image? _avatarImage;
    private string _avatarUrl = "";

    // Theme colors (updated from JS via overlayThemeColors)
    private TrayTheme _theme = TrayTheme.Default;

    // Status tray icons (loaded once on tray thread from PNG assets)
    private Icon? _defaultIcon;
    private readonly Dictionary<string, Icon> _statusIcons = new();

    // Localization
    private Dictionary<string, string> _strings = new();
    private string _language = "en";

    // Callbacks (invoked on the CALLING thread — callers must marshal if needed)
    public Action? OnShowWindow;
    public Action<string>? OnStatusChange;   // VRC status key
    public Action? OnClose;

    /// <summary>
    /// Optional authenticated image downloader. When set, used instead of plain HttpClient.
    /// </summary>
    public Func<string, Task<byte[]>>? ImageDownloader;

    private bool _pendingVisible;

    // ── Lifecycle ──────────────────────────────────────────────────────────

    public void Initialize()
    {
        _trayThread = new Thread(TrayThreadProc)
        {
            Name = "SystemTray",
            IsBackground = true,
        };
        _trayThread.SetApartmentState(ApartmentState.STA);
        _trayThread.Start();
    }

    private void TrayThreadProc()
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.SystemAware);

        _syncCtx = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(_syncCtx);

        // Load default icon
        var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logo.ico");
        _defaultIcon = File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application;

        // Load status icons from PNG assets (tray/ folder in output)
        var trayDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tray");
        foreach (var (status, file) in new[] {
            ("join me",  "join_me.png"),
            ("active",   "online.png"),
            ("ask me",   "ask_me.png"),
            ("busy",     "busy.png"),
        })
        {
            var icon = LoadPngAsIcon(Path.Combine(trayDir, file));
            if (icon != null) _statusIcons[status] = icon;
        }

        _trayIcon = new NotifyIcon
        {
            Icon = _defaultIcon,
            Text = "VRCNext",
            Visible = _pendingVisible,
        };
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OnShowWindow?.Invoke();
            else if (e.Button == MouseButtons.Right)
                ShowPopup();
        };

        System.Windows.Forms.Application.Run();
    }

    /// <summary>Loads a PNG file and converts it to a 32x32 Icon for the system tray.</summary>
    private static Icon? LoadPngAsIcon(string pngPath)
    {
        if (!File.Exists(pngPath)) return null;
        try
        {
            using var bmp = new Bitmap(pngPath);
            using var resized = new Bitmap(bmp, new Size(32, 32));
            var hIcon = resized.GetHicon();
            var icon = Icon.FromHandle(hIcon);
            var clone = (Icon)icon.Clone(); // clone owns a copy — safe after DestroyIcon
            DestroyIcon(hIcon);
            return clone;
        }
        catch { return null; }
    }

    // ── Public API (thread-safe) ──────────────────────────────────────────

    public void SetVisible(bool visible)
    {
        _pendingVisible = visible;
        _syncCtx?.Post(_ =>
        {
            if (_trayIcon != null) _trayIcon.Visible = visible;
        }, null);
    }

    public void UpdateUserInfo(string name, string status, string statusDesc, string imageUrl)
    {
        bool statusChanged;
        lock (_dataLock)
        {
            statusChanged = _status != status;
            _displayName = name;
            _status = status;
            _statusDescription = statusDesc;
            if (imageUrl != _avatarUrl)
            {
                _avatarUrl = imageUrl;
                if (!string.IsNullOrEmpty(imageUrl))
                    _ = DownloadAvatarAsync(imageUrl);
            }
        }
        _syncCtx?.Post(_ =>
        {
            // Switch tray icon to match status
            if (statusChanged && _trayIcon != null)
            {
                _trayIcon.Icon = _statusIcons.TryGetValue(status, out var sIcon) ? sIcon : _defaultIcon;
                _trayIcon.Text = $"VRCNext · {name}";
            }
            // Update popup if open
            _popup?.UpdateUserData(name, status, statusDesc);
        }, null);
    }

    public void UpdateLanguage(string lang)
    {
        _language = lang;
        LoadStrings();
    }

    public void UpdateTheme(Dictionary<string, string> colors)
    {
        lock (_dataLock)
        {
            _theme = TrayTheme.FromCssColors(colors);
        }
    }

    // ── Internal helpers ──────────────────────────────────────────────────

    private async Task DownloadAvatarAsync(string url)
    {
        try
        {
            byte[]? bytes = null;

            if (ImageDownloader != null)
            {
                bytes = await ImageDownloader(url);
            }
            else
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                bytes = await http.GetByteArrayAsync(url);
            }

            if (bytes == null || bytes.Length == 0) return;

            lock (_dataLock)
            {
                if (url != _avatarUrl) return; // stale
                var old = _avatarImage;
                // MemoryStream must stay alive for the lifetime of the Image
                var ms = new MemoryStream(bytes);
                _avatarImage = Image.FromStream(ms);
                old?.Dispose();
            }
            // Repaint popup if open
            _syncCtx?.Post(_ => _popup?.Invalidate(), null);
        }
        catch { /* non-critical */ }
    }

    private void LoadStrings()
    {
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "i18n", $"{_language}.json");
            if (!File.Exists(path))
                path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "i18n", "en.json");
            var json = File.ReadAllText(path);
            var obj = JObject.Parse(json);
            _strings = obj.Properties().ToDictionary(p => p.Name, p => p.Value.ToString());
        }
        catch { _strings = new(); }
    }

    internal string T(string key, string fallback) =>
        _strings.TryGetValue(key, out var v) ? v : fallback;

    private void ShowPopup()
    {
        _syncCtx?.Post(_ =>
        {
            _popup?.Close();
            _popup?.Dispose();

            string name, status, statusDesc;
            Image? avatar;
            TrayTheme theme;
            lock (_dataLock)
            {
                name = _displayName;
                status = _status;
                statusDesc = _statusDescription;
                avatar = _avatarImage != null ? (Image)_avatarImage.Clone() : null;
                theme = _theme;
            }

            _popup = new TrayPopupForm(name, status, statusDesc, avatar, theme, this);

            // Position above the system tray (bottom-right of the working area)
            var screen = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            var x = screen.Right - _popup.Width - 12;
            var y = screen.Bottom - _popup.Height - 12;
            _popup.Location = new Point(x, y);
            _popup.Show();
            _popup.Activate();
        }, null);
    }

    internal void RequestStatusChange(string newStatus)
    {
        // Update popup + tray icon immediately (optimistic), keep popup open
        _syncCtx?.Post(_ =>
        {
            _popup?.SetStatus(newStatus);
            if (_trayIcon != null)
                _trayIcon.Icon = _statusIcons.TryGetValue(newStatus, out var sIcon) ? sIcon : _defaultIcon;
        }, null);
        OnStatusChange?.Invoke(newStatus);
    }

    internal void RequestClose()
    {
        _syncCtx?.Post(_ => _popup?.Close(), null);
        OnClose?.Invoke();
    }

    internal void RequestShowWindow()
    {
        _syncCtx?.Post(_ => _popup?.Close(), null);
        OnShowWindow?.Invoke();
    }

    // ── Dispose ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _syncCtx?.Post(_ =>
        {
            _popup?.Close();
            _popup?.Dispose();
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
            foreach (var icon in _statusIcons.Values) icon.Dispose();
            _statusIcons.Clear();
            System.Windows.Forms.Application.ExitThread();
        }, null);
        _trayThread?.Join(3000);
        lock (_dataLock)
        {
            _avatarImage?.Dispose();
            _avatarImage = null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  TrayTheme — parsed theme colors for native rendering
    // ═══════════════════════════════════════════════════════════════════════

    internal struct TrayTheme
    {
        public Color BgBase;
        public Color BgCard;
        public Color BgHover;
        public Color Tx1;
        public Color Tx2;
        public Color Brd;
        public Color Accent;
        public Color Err;

        // Status colours (fixed across all themes, matching CSS --status-* vars)
        public static readonly Color StatusJoin    = Color.FromArgb(255, 66, 165, 245);   // #42A5F5
        public static readonly Color StatusOnline  = Color.FromArgb(255, 45, 212, 140);   // #2DD48C
        public static readonly Color StatusAsk     = Color.FromArgb(255, 255, 167, 38);   // #FFA726
        public static readonly Color StatusBusy    = Color.FromArgb(255, 239, 83, 80);    // #EF5350
        public static readonly Color StatusOffline = Color.FromArgb(255, 116, 127, 141);  // #747F8D

        /// <summary>Default "midnight" theme</summary>
        public static readonly TrayTheme Default = new()
        {
            BgBase  = ParseHex("#080C15"),
            BgCard  = ParseHex("#0F1628"),
            BgHover = ParseHex("#141E37"),
            Tx1     = ParseHex("#DCE4F5"),
            Tx2     = ParseHex("#788CAF"),
            Brd     = ParseHex("#1C2841"),
            Accent  = ParseHex("#3884FF"),
            Err     = ParseHex("#FF4B55"),
        };

        public static TrayTheme FromCssColors(Dictionary<string, string> c)
        {
            var t = Default;
            if (c.TryGetValue("bg-base",  out var v)) t.BgBase  = ParseHex(v);
            if (c.TryGetValue("bg-card",  out v))     t.BgCard  = ParseHex(v);
            if (c.TryGetValue("bg-hover", out v))     t.BgHover = ParseHex(v);
            if (c.TryGetValue("tx1",      out v))     t.Tx1     = ParseHex(v);
            if (c.TryGetValue("tx2",      out v))     t.Tx2     = ParseHex(v);
            if (c.TryGetValue("brd",      out v))     t.Brd     = ParseHex(v);
            if (c.TryGetValue("accent",   out v))     t.Accent  = ParseHex(v);
            if (c.TryGetValue("err",      out v))     t.Err     = ParseHex(v);
            return t;
        }

        private static Color ParseHex(string hex)
        {
            hex = hex.TrimStart('#');
            if (hex.Length >= 6)
                return Color.FromArgb(255,
                    Convert.ToInt32(hex[0..2], 16),
                    Convert.ToInt32(hex[2..4], 16),
                    Convert.ToInt32(hex[4..6], 16));
            return Color.FromArgb(255, 20, 20, 36);
        }

        public Color CloseHover => Color.FromArgb(255,
            Math.Min(255, Err.R / 4 + BgBase.R),
            Math.Min(255, Err.G / 8 + BgBase.G),
            Math.Min(255, Err.B / 8 + BgBase.B));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  TrayPopupForm — custom borderless GDI+ rendered popup
    // ═══════════════════════════════════════════════════════════════════════

    private class TrayPopupForm : Form
    {
        private string _name;
        private string _status;
        private string _statusDesc;
        private Image? _avatar;
        private readonly SystemTrayService _owner;
        private readonly TrayTheme _theme;

        // Layout constants
        private const int FormWidth = 280;
        private const int Pad = 14;
        private const int AvatarSize = 44;
        private const int BtnHeight = 34;
        private const int BtnGap = 2;
        private const int SepGap = 8;
        private const int Corner = 12;

        private readonly (string key, string label, Color color)[] _statusOpts;
        private readonly Rectangle[] _btnRects; // 0-3 = status, 4 = close
        private int _hoverIdx = -1;
        private int _profileSectionBottom;

        public TrayPopupForm(string name, string status, string statusDesc, Image? avatar, TrayTheme theme, SystemTrayService owner)
        {
            _name = name;
            _status = status;
            _statusDesc = statusDesc;
            _avatar = avatar;
            _owner = owner;
            _theme = theme;

            _statusOpts = new[]
            {
                ("join me",  owner.T("tray.status.join_me",        "Join Me"),         TrayTheme.StatusJoin),
                ("active",   owner.T("tray.status.online",         "Online"),          TrayTheme.StatusOnline),
                ("ask me",   owner.T("tray.status.ask_me",         "Ask Me"),          TrayTheme.StatusAsk),
                ("busy",     owner.T("tray.status.do_not_disturb", "Do Not Disturb"),  TrayTheme.StatusBusy),
            };
            _btnRects = new Rectangle[5];

            // Form setup
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = _theme.BgBase;
            DoubleBuffered = true;

            // Calc height
            int profileH = Pad + Math.Max(AvatarSize, 38) + Pad;
            int btnsH = SepGap + _statusOpts.Length * (BtnHeight + BtnGap) + SepGap;
            int closeH = SepGap + BtnHeight + Pad;
            int totalH = profileH + 1 + btnsH + 1 + closeH;

            Size = new Size(FormWidth, totalH);
            Region = RoundedRegion(Width, Height, Corner);
        }

        /// <summary>Update displayed status without closing the popup.</summary>
        public void SetStatus(string newStatus)
        {
            _status = newStatus;
            Invalidate();
        }

        /// <summary>Update user data (called when tray service receives new info).</summary>
        public void UpdateUserData(string name, string status, string statusDesc)
        {
            _name = name;
            _status = status;
            _statusDesc = statusDesc;
            Invalidate();
        }

        // ── Rounded region ────────────────────────────────────────────────

        private static Region RoundedRegion(int w, int h, int r)
        {
            var p = new GraphicsPath();
            p.AddArc(0, 0, r * 2, r * 2, 180, 90);
            p.AddArc(w - r * 2, 0, r * 2, r * 2, 270, 90);
            p.AddArc(w - r * 2, h - r * 2, r * 2, r * 2, 0, 90);
            p.AddArc(0, h - r * 2, r * 2, r * 2, 90, 90);
            p.CloseFigure();
            return new Region(p);
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ── Status helpers ────────────────────────────────────────────────

        private static Color StatusColor(string s) => s switch
        {
            "join me" => TrayTheme.StatusJoin,
            "active" or "online" => TrayTheme.StatusOnline,
            "ask me" or "look me" => TrayTheme.StatusAsk,
            "busy" or "do not disturb" => TrayTheme.StatusBusy,
            _ => TrayTheme.StatusOffline,
        };

        private static string StatusLabel(string s) => s switch
        {
            "join me" => "Join Me",
            "active" or "online" => "Online",
            "ask me" or "look me" => "Ask Me",
            "busy" or "do not disturb" => "Do Not Disturb",
            _ => "Offline",
        };

        // ── Paint ─────────────────────────────────────────────────────────

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // Fill background (needed for rounded corners)
            using (var bgBrush = new SolidBrush(_theme.BgBase))
                g.FillRectangle(bgBrush, ClientRectangle);

            int y = Pad;

            // ── Profile section ──────────────────────────────────────────
            var avRect = new Rectangle(Pad, y, AvatarSize, AvatarSize);

            // Try to get latest avatar from service
            Image? liveAvatar = null;
            lock (_owner._dataLock)
            {
                if (_owner._avatarImage != null)
                    liveAvatar = (Image)_owner._avatarImage.Clone();
            }
            var drawAvatar = liveAvatar ?? _avatar;

            if (drawAvatar != null)
            {
                using var clip = new GraphicsPath();
                clip.AddEllipse(avRect);
                var saved = g.Clip;
                g.SetClip(clip);
                g.DrawImage(drawAvatar, avRect);
                g.Clip = saved;
                using var pen = new Pen(_theme.Brd, 2);
                g.DrawEllipse(pen, avRect);
                liveAvatar?.Dispose();
            }
            else
            {
                using var bg = new SolidBrush(_theme.BgCard);
                g.FillEllipse(bg, avRect);
                if (!string.IsNullOrEmpty(_name))
                {
                    using var f = new Font("Segoe UI", 15, FontStyle.Bold);
                    using var b = new SolidBrush(_theme.Tx2);
                    var ch = _name[0].ToString().ToUpper();
                    var sz = g.MeasureString(ch, f);
                    g.DrawString(ch, f, b,
                        avRect.X + (avRect.Width - sz.Width) / 2,
                        avRect.Y + (avRect.Height - sz.Height) / 2);
                }
            }

            int tx = Pad + AvatarSize + 10;
            int tw = FormWidth - tx - Pad;

            // Display name
            using (var nf = new Font("Segoe UI", 11, FontStyle.Bold))
            using (var nb = new SolidBrush(_theme.Tx1))
            {
                var nameStr = string.IsNullOrEmpty(_name) ? "VRCNext" : _name;
                g.DrawString(nameStr, nf, nb,
                    new RectangleF(tx, y + 2, tw, 20),
                    new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
            }

            // Status dot + text
            var stColor = StatusColor(_status);
            int dotY = y + 26;
            using (var db = new SolidBrush(stColor))
                g.FillEllipse(db, tx, dotY + 2, 8, 8);

            using (var sf = new Font("Segoe UI", 9))
            using (var sb = new SolidBrush(_theme.Tx2))
            {
                var stText = !string.IsNullOrEmpty(_statusDesc) ? _statusDesc : StatusLabel(_status);
                g.DrawString(stText, sf, sb,
                    new RectangleF(tx + 12, dotY, tw - 12, 16),
                    new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
            }

            y += Math.Max(AvatarSize, 38) + Pad;
            _profileSectionBottom = y;

            // ── Separator ────────────────────────────────────────────────
            using (var sp = new Pen(_theme.Brd))
                g.DrawLine(sp, Pad, y, FormWidth - Pad, y);
            y += 1 + SepGap;

            // ── Status buttons ───────────────────────────────────────────
            for (int i = 0; i < _statusOpts.Length; i++)
            {
                var (key, label, color) = _statusOpts[i];
                var br = new Rectangle(Pad / 2, y, FormWidth - Pad, BtnHeight);
                _btnRects[i] = br;

                bool hovered = _hoverIdx == i;
                bool current = _status == key;

                if (hovered || current)
                {
                    using var hb = new SolidBrush(hovered ? _theme.BgHover : _theme.BgCard);
                    using var hp = RoundedRect(br, 6);
                    g.FillPath(hb, hp);
                }

                // Dot
                using (var db = new SolidBrush(color))
                    g.FillEllipse(db, br.X + 12, br.Y + (BtnHeight - 10) / 2, 10, 10);

                // Label
                using var lf = new Font("Segoe UI", 10, current ? FontStyle.Bold : FontStyle.Regular);
                using var lb = new SolidBrush(_theme.Tx1);
                g.DrawString(label, lf, lb, br.X + 30, br.Y + (BtnHeight - 18) / 2);

                // Checkmark for current status
                if (current)
                {
                    using var cf = new Font("Segoe UI", 10);
                    using var cb = new SolidBrush(color);
                    g.DrawString("\u2713", cf, cb, br.Right - 26, br.Y + (BtnHeight - 18) / 2);
                }

                y += BtnHeight + BtnGap;
            }
            y += SepGap;

            // ── Separator ────────────────────────────────────────────────
            y -= BtnGap;
            using (var sp = new Pen(_theme.Brd))
                g.DrawLine(sp, Pad, y, FormWidth - Pad, y);
            y += 1 + SepGap;

            // ── Close button ─────────────────────────────────────────────
            var cr = new Rectangle(Pad / 2, y, FormWidth - Pad, BtnHeight);
            _btnRects[4] = cr;

            if (_hoverIdx == 4)
            {
                using var hb = new SolidBrush(_theme.CloseHover);
                using var hp = RoundedRect(cr, 6);
                g.FillPath(hb, hp);
            }

            var closeCol = _hoverIdx == 4 ? _theme.Err : _theme.Tx1;
            // X icon
            using (var cp = new Pen(closeCol, 1.8f))
            {
                int cx = cr.X + 15, cy = cr.Y + BtnHeight / 2;
                g.DrawLine(cp, cx - 4, cy - 4, cx + 4, cy + 4);
                g.DrawLine(cp, cx + 4, cy - 4, cx - 4, cy + 4);
            }

            using (var cf = new Font("Segoe UI", 10))
            using (var cb = new SolidBrush(closeCol))
                g.DrawString(_owner.T("tray.close_vrcn", "Close VRCN"), cf, cb, cr.X + 30, cr.Y + (BtnHeight - 18) / 2);

            // ── Outer border ─────────────────────────────────────────────
            using var borderPen = new Pen(_theme.Brd, 1);
            using var borderPath = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), Corner);
            g.DrawPath(borderPen, borderPath);
        }

        // ── Mouse tracking ────────────────────────────────────────────────

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int old = _hoverIdx;
            _hoverIdx = HitTest(e.Location);
            if (_hoverIdx != old)
            {
                Cursor = _hoverIdx >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverIdx != -1) { _hoverIdx = -1; Cursor = Cursors.Default; Invalidate(); }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            int idx = HitTest(e.Location);
            if (idx >= 0 && idx <= 3)
                _owner.RequestStatusChange(_statusOpts[idx].key);
            else if (idx == 4)
                _owner.RequestClose();
        }

        private int HitTest(Point p)
        {
            for (int i = 0; i < _btnRects.Length; i++)
                if (_btnRects[i].Contains(p)) return i;
            // Profile section click → show window
            if (p.Y < _profileSectionBottom)
                return -2; // special: profile area
            return -1;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                int idx = HitTest(e.Location);
                if (idx == -2)
                    _owner.RequestShowWindow();
            }
        }

        // Close popup when it loses focus
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _avatar?.Dispose();
            base.Dispose(disposing);
        }

        // Prevent shadow/flicker — paint the full background ourselves
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }
    }
}
#endif
