using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net.Http;
using System.Text.Json;

namespace ScreenTime;

/// <summary>
/// Guides the user through adding the browser extension. Every step has to actually be done before
/// "Next" unlocks (open the page; confirm Developer mode after a short pause; the last step only
/// finishes once the extension really connects to the app), so it can't be clicked through blindly.
/// If the extension is published on a store (see <see cref="ExtensionStore"/>), it's added
/// automatically instead and the user only has to switch it on.
/// </summary>
internal sealed class ExtensionWizard : Form
{
    private enum Page { Choose, Auto, Open, DevMode, Load, Done }

    private static ExtensionWizard _open;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private static readonly Color Accent = Color.FromArgb(42, 120, 214);
    private static readonly Color Ink = Color.FromArgb(11, 11, 11);
    private static readonly Color Ink2 = Color.FromArgb(82, 81, 78);
    private static readonly Color Muted = Color.FromArgb(137, 135, 129);
    private static readonly Color SuccessText = Color.FromArgb(0, 99, 0);
    private static readonly Color Line = Color.FromArgb(225, 224, 217);
    private const int DevModeReadSeconds = 4;

    private readonly List<BrowserInfo> _browsers;
    private readonly string _extensionFolder;
    private BrowserInfo _browser;
    private Page _page;
    private bool _opened, _devModeOn, _skipConfirmed, _polling;
    private DateTime _pageShownAt;

    // connection state from /api/now
    private readonly HashSet<string> _baseline = new();
    private bool _baselineLoaded, _appReachable = true;
    private List<(string Id, string Browser, long SecondsAgo)> _extensions = new();

    private readonly Label _title, _subtitle;
    private readonly Panel _content;
    private readonly Button _back, _next;
    private readonly LinkLabel _skip;
    private readonly System.Windows.Forms.Timer _timer;
    private Label _status, _countdownHint, _trouble;

    /// <summary>Shows the wizard (or brings the open one to the front).</summary>
    public static void ShowWizard()
    {
        if (_open != null && !_open.IsDisposed) { _open.Activate(); return; }
        _open = new ExtensionWizard();
        _open.FormClosed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    /// <summary>Development aid (--wizard-screens=DIR): saves every page as a PNG, for the detected browser and for Edge.</summary>
    public static void RenderScreens(string dir)
    {
        Directory.CreateDirectory(dir);
        var edge = new BrowserInfo("Microsoft Edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", "edge://extensions", ExtensionPageLayout.EdgeStyle, false);
        int n = 0;
        foreach (var variant in new[] { null, edge })
        {
            using var w = new ExtensionWizard { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false };
            if (variant != null) w._browser = variant;
            w.Show();
            foreach (var p in new[] { Page.Choose, Page.Open, Page.DevMode, Page.Load, Page.Done })
            {
                if (variant != null && p is Page.Choose or Page.Done) continue;
                w.ShowPage(p);
                Application.DoEvents();
                using var bmp = new Bitmap(w.Width, w.Height);
                w.DrawToBitmap(bmp, new Rectangle(Point.Empty, w.Size));
                bmp.Save(Path.Combine(dir, $"{++n:00}-{(variant?.Name ?? w._browser?.Name ?? "none").Replace(' ', '_')}-{p}.png"));
            }
            w._skipConfirmed = true;
            w.Close();
        }
    }

    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public ExtensionWizard()
    {
        _browsers = Browsers.Detect();
        _extensionFolder = Path.Combine(AppContext.BaseDirectory, "extension");

        Text = "Screen Time – Set up website tracking";
        Font = new Font("Segoe UI", 9.75f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;
        ClientSize = new Size(S(680), S(600));
        try
        {
            using var s = typeof(ExtensionWizard).Assembly.GetManifestResourceStream("app.ico");
            Icon = new Icon(s);
        }
        catch { }

        // header
        var header = new Panel { Dock = DockStyle.Top, Height = S(78), BackColor = Color.FromArgb(249, 249, 247) };
        var logo = new PictureBox { Size = new Size(S(40), S(40)), Location = new Point(S(24), S(19)), SizeMode = PictureBoxSizeMode.Zoom };
        try { logo.Image = new Icon(Icon, S(40), S(40)).ToBitmap(); } catch { }
        _title = new Label { Text = "Set up website tracking", Font = new Font("Segoe UI Semibold", 14f), ForeColor = Ink, AutoSize = true, Location = new Point(S(76), S(14)) };
        _subtitle = new Label { Text = "", ForeColor = Ink2, AutoSize = true, Location = new Point(S(78), S(46)) };
        header.Controls.AddRange([logo, _title, _subtitle]);
        header.Paint += (_, e) => e.Graphics.DrawLine(new Pen(Line), 0, header.Height - 1, header.Width, header.Height - 1);

        // footer
        var footer = new Panel { Dock = DockStyle.Bottom, Height = S(62), BackColor = Color.FromArgb(249, 249, 247) };
        footer.Paint += (_, e) => e.Graphics.DrawLine(new Pen(Line), 0, 0, footer.Width, 0);
        _next = MakeButton("Next", primary: true);
        _back = MakeButton("Back", primary: false);
        _next.Size = _back.Size = new Size(S(110), S(34));
        _next.Location = new Point(ClientSize.Width - S(24) - _next.Width, S(14));
        _back.Location = new Point(_next.Left - S(10) - _back.Width, S(14));
        _skip = new LinkLabel { Text = "I'll do this later", AutoSize = true, Location = new Point(S(24), S(22)), LinkColor = Ink2, ActiveLinkColor = Accent };
        _skip.LinkClicked += (_, _) => { if (ConfirmSkip()) Close(); };
        _next.Click += (_, _) => GoNext();
        _back.Click += (_, _) => GoBack();
        footer.Controls.AddRange([_skip, _back, _next]);

        _content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(28), S(20), S(28), S(10)), AutoScroll = true };
        Controls.Add(_content);
        Controls.Add(header);
        Controls.Add(footer);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => { Poll(); UpdateButtons(); };
        _timer.Start();
        Poll();

        _browser = _browsers.FirstOrDefault(b => b.Supported);
        ShowPage(Page.Choose);
    }

    private Button MakeButton(string text, bool primary)
    {
        var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = new Font("Segoe UI Semibold", 9.75f) };
        b.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(200, 199, 192);
        b.BackColor = primary ? Accent : Color.White;
        b.ForeColor = primary ? Color.White : Ink;
        b.EnabledChanged += (_, _) =>
        {
            b.BackColor = !b.Enabled ? Color.FromArgb(235, 234, 229) : primary ? Accent : Color.White;
            b.FlatAppearance.BorderColor = !b.Enabled ? Color.FromArgb(225, 224, 217) : primary ? Accent : Color.FromArgb(200, 199, 192);
        };
        return b;
    }

    // ---------- page building ----------

    private int _y;
    private int ContentWidth => _content.ClientSize.Width - _content.Padding.Horizontal - S(4);

    private T Add<T>(T c, int gapAfter = 10) where T : Control
    {
        c.Location = new Point(_content.Padding.Left, _y + _content.AutoScrollPosition.Y);
        _content.Controls.Add(c);
        _y += c.Height + S(gapAfter);
        return c;
    }

    private Label Para(string text, Color? color = null, float size = 9.75f, bool bold = false, int gap = 10)
    {
        var l = new Label
        {
            Text = text,
            ForeColor = color ?? Ink,
            Font = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size),
            AutoSize = false,
            UseMnemonic = false,
        };
        l.Size = l.GetPreferredSize(new Size(ContentWidth, 0));
        l.Width = ContentWidth;
        return Add(l, gap);
    }

    private (TextBox Box, Button Copy) CopyRow(string value, Action onCopy, string extraLabel = null, Action extra = null)
    {
        var row = new Panel { Width = ContentWidth, Height = S(34) };
        var copy = MakeButton("Copy", primary: false);
        copy.Size = new Size(S(80), S(32));
        Button extraBtn = null;
        if (extraLabel != null)
        {
            extraBtn = MakeButton(extraLabel, primary: false);
            extraBtn.Size = new Size(S(120), S(32));
            extraBtn.Location = new Point(row.Width - extraBtn.Width, 0);
            extraBtn.Click += (_, _) => extra();
        }
        copy.Location = new Point((extraBtn?.Left ?? row.Width) - copy.Width - (extraBtn != null ? S(8) : 0), 0);
        var box = new TextBox { Text = value, ReadOnly = true, BackColor = Color.FromArgb(249, 249, 247), Font = new Font("Consolas", 10f),
                                Location = new Point(0, S(4)), Width = copy.Left - S(8) };
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(value); } catch { }
            copy.Text = "Copied ✓";
            onCopy?.Invoke();
        };
        row.Controls.Add(box);
        row.Controls.Add(copy);
        if (extraBtn != null) row.Controls.Add(extraBtn);
        Add(row, 12);
        return (box, copy);
    }

    private void ShowPage(Page p)
    {
        _page = p;
        _pageShownAt = DateTime.Now;
        _content.SuspendLayout();
        foreach (Control c in _content.Controls.Cast<Control>().ToList()) { _content.Controls.Remove(c); c.Dispose(); }
        _content.AutoScrollPosition = Point.Empty;
        _y = _content.Padding.Top;
        _status = _countdownHint = _trouble = null;
        string name = _browser?.Name ?? "your browser";

        switch (p)
        {
            case Page.Choose:
                _subtitle.Text = "Which browser do you use?";
                Para("Screen Time can also show which websites you spend time on. For that it needs a small extension in your browser. " +
                     "It only tells Screen Time the website you're on (for example youtube.com), and everything stays on this PC.", Ink2, gap: 16);
                Para("Pick the browser you use most:", bold: true, gap: 8);
                foreach (var b in _browsers.Append(Browsers.Other()))
                {
                    var rb = new RadioButton
                    {
                        Text = "  " + b.Name + (b.IsDefault ? "   (your default browser)" : "") + (b.Supported ? "" : "   – not supported yet"),
                        Enabled = b.Supported,
                        Checked = _browser != null && b.Name == _browser.Name && b.ExePath == _browser.ExePath,
                        AutoSize = false, Width = ContentWidth, Height = S(36),
                        Font = new Font(b.IsDefault ? "Segoe UI Semibold" : "Segoe UI", 10f),
                        TextImageRelation = TextImageRelation.ImageBeforeText, ImageAlign = ContentAlignment.MiddleLeft, TextAlign = ContentAlignment.MiddleLeft,
                    };
                    if (b.ExePath != null) try { rb.Image = new Icon(Icon.ExtractAssociatedIcon(b.ExePath), S(24), S(24)).ToBitmap(); } catch { }
                    var captured = b;
                    rb.CheckedChanged += (_, _) => { if (rb.Checked) { _browser = captured; UpdateButtons(); } };
                    Add(rb, 2);
                }
                _y += S(8);
                if (_browsers.Any(b => !b.Supported))
                    Para("Website tracking works in Chrome, Edge, Brave, Vivaldi, Opera and other Chromium-based browsers. Firefox isn't supported yet; your apps are still tracked either way.", Muted, 9f);
                _status = Para("", SuccessText, bold: true);
                break;

            case Page.Auto:
                _subtitle.Text = $"Turn on the extension in {name}";
                Para($"Screen Time has added its extension to {name}. Browsers always ask before a new extension can run, so there's one thing left for you to do:", Ink2, gap: 14);
                Para($"1.  Open {name}. If it's already open, close it completely and open it again.", gap: 6);
                Para("2.  You'll see a message that a new extension was added. Click “Enable extension” (or “Turn on”).", gap: 16);
                var openBrowser = MakeButton($"Open {name}", primary: true);
                openBrowser.Size = new Size(S(220), S(38));
                openBrowser.Click += (_, _) => LaunchBrowser(null);
                Add(openBrowser, 18);
                _status = Para("Waiting for the extension to connect…", Ink2, bold: true);
                var manual = new LinkLabel { Text = "No message showed up? Add it by hand instead", AutoSize = true, LinkColor = Accent };
                manual.LinkClicked += (_, _) => ShowPage(Page.Open);
                Add(manual);
                break;

            case Page.Open:
                _subtitle.Text = "Step 1 of 3 · Open the extensions page";
                Para($"Click the button below. {name} opens its extensions page in a new tab.", gap: 14);
                if (_browser?.ExePath != null)
                {
                    var open = MakeButton($"Open {name} extensions page", primary: true);
                    open.Size = new Size(S(300), S(40));
                    open.Click += (_, _) => { LaunchBrowser(_browser.ExtensionsUrl); _opened = true; ShowOpenedNote(); UpdateButtons(); };
                    Add(open, 16);
                }
                _status = Para(_opened ? $"✓ Opened. If {name} didn't come to the front, click it in the taskbar." : "", SuccessText, bold: true, gap: 16);
                Para(_browser?.ExePath != null
                        ? $"Didn't open? Click {name}'s address bar at the top, paste this address and press Enter:"
                        : "Click your browser's address bar at the top, paste this address and press Enter:", Ink2, gap: 6);
                CopyRow(_browser?.ExtensionsUrl ?? "chrome://extensions", () => { _opened = true; UpdateButtons(); });
                break;

            case Page.DevMode:
                _subtitle.Text = "Step 2 of 3 · Turn on Developer mode";
                Add(new BrowserSketch(_browser, BrowserSketch.Highlight.DevMode) { Width = ContentWidth, Height = S(200) }, 16);
                Para($"On the extensions page, find the Developer mode switch {_browser.DevModeWhere} and turn it on.", gap: 6);
                Para("This only allows adding an extension from a folder on your PC. It doesn't change anything else.", Muted, 9f, gap: 14);
                var check = new CheckBox { Text = "  I turned on Developer mode", Checked = _devModeOn, AutoSize = true, Font = new Font("Segoe UI Semibold", 10f) };
                check.CheckedChanged += (_, _) => { _devModeOn = check.Checked; UpdateButtons(); };
                Add(check, 6);
                _countdownHint = Para("", Muted, 9f);
                break;

            case Page.Load:
                _subtitle.Text = "Step 3 of 3 · Add the Screen Time extension";
                try { Clipboard.SetText(_extensionFolder); } catch { }
                Add(new BrowserSketch(_browser, BrowserSketch.Highlight.LoadUnpacked) { Width = ContentWidth, Height = S(170) }, 14);
                Para($"1.  Click “Load unpacked” {_browser.LoadUnpackedWhere}.", gap: 6);
                Para("2.  A folder window opens. Click the address bar at its top, press Ctrl+V to paste (the folder below is already copied for you) and press Enter.", gap: 6);
                Para("3.  Click “Select Folder”.", gap: 12);
                CopyRow(_extensionFolder, null, "Show folder", () =>
                {
                    try { Process.Start("explorer.exe", $"/select,\"{_extensionFolder}\""); } catch { }
                });
                Para("Tip: you can also drag the “extension” folder from File Explorer onto the extensions page.", Muted, 9f, gap: 14);
                _status = Para("", Ink2, 10.5f, bold: true, gap: 8);
                _trouble = Para("Having trouble?\n" +
                                "•  Make sure you picked the folder named “extension” (it has a file called manifest.json inside).\n" +
                                "•  On the extensions page, check that “Screen Time Connector” is switched on and shows no errors.\n" +
                                "•  Click once into a web page in the browser, then wait a few seconds.", Ink2, 9f);
                _trouble.Visible = false;
                if (!Directory.Exists(_extensionFolder))
                    _status.Text = "The extension folder is missing. Reinstall Screen Time to restore it.";
                break;

            case Page.Done:
                _subtitle.Text = "All set";
                Para("✓  Website tracking is on.", SuccessText, 13f, bold: true, gap: 12);
                Para($"Websites you visit in {name} will now show up in your Screen Time dashboard, next to your apps.", gap: 10);
                Para("Use another browser too? Run this again any time: right-click the Screen Time icon in the taskbar tray → “Set up browser extension…”.", Ink2, gap: 18);
                var dash = MakeButton("Open dashboard", primary: false);
                dash.Size = new Size(S(160), S(36));
                dash.Click += (_, _) => Program.OpenDashboard();
                Add(dash);
                break;
        }
        _content.ResumeLayout();
        UpdateButtons();
    }

    private void ShowOpenedNote()
    {
        if (_status != null && _page == Page.Open)
            _status.Text = $"✓ Opened. If {_browser?.Name ?? "the browser"} didn't come to the front, click it in the taskbar.";
    }

    private void LaunchBrowser(string url)
    {
        if (_browser?.ExePath == null) return;
        try { Process.Start(new ProcessStartInfo(_browser.ExePath) { Arguments = url ?? "", UseShellExecute = false }); }
        catch { }
    }

    // ---------- navigation ----------

    private void GoNext()
    {
        switch (_page)
        {
            case Page.Choose:
                if (_browser == null || !_browser.Supported) return;
                if (IsConnected(onlyChosenBrowser: true)) { ShowPage(Page.Done); return; }
                if (ExtensionStore.CanAutoInstall(_browser) && ExtensionStore.TryAutoInstall(_browser)) { ShowPage(Page.Auto); return; }
                ShowPage(Page.Open);
                break;
            case Page.Open: if (_opened) ShowPage(Page.DevMode); break;
            case Page.DevMode: if (DevModeReady()) ShowPage(Page.Load); break;
            case Page.Load:
            case Page.Auto: if (IsConnected(false)) ShowPage(Page.Done); break;
            case Page.Done: _skipConfirmed = true; Close(); break;
        }
    }

    private void GoBack()
    {
        switch (_page)
        {
            case Page.Open:
            case Page.Auto: ShowPage(Page.Choose); break;
            case Page.DevMode: ShowPage(Page.Open); break;
            case Page.Load: ShowPage(Page.DevMode); break;
        }
    }

    private bool DevModeReady() => _devModeOn && (DateTime.Now - _pageShownAt).TotalSeconds >= DevModeReadSeconds;

    private void UpdateButtons()
    {
        if (IsDisposed) return;
        _back.Visible = _page is Page.Open or Page.DevMode or Page.Load or Page.Auto;
        _skip.Visible = _page != Page.Done;
        _next.Text = _page switch { Page.Load or Page.Auto => "Finish", Page.Done => "Close", _ => "Next" };
        bool connected = IsConnected(false);

        switch (_page)
        {
            case Page.Choose:
                _next.Enabled = _browser != null && _browser.Supported;
                if (_status != null)
                    _status.Text = _browser != null && IsConnected(onlyChosenBrowser: true) ? $"✓ The extension is already working in {_browser.Name}." : "";
                break;
            case Page.Open:
                _next.Enabled = _opened;
                break;
            case Page.DevMode:
                int left = DevModeReadSeconds - (int)(DateTime.Now - _pageShownAt).TotalSeconds;
                _next.Enabled = DevModeReady();
                if (_devModeOn && left > 0) _next.Text = $"Next ({left})";
                if (_countdownHint != null)
                    _countdownHint.Text = _devModeOn ? "" : "Tick the box once the switch is on (it turns blue).";
                break;
            case Page.Load:
            case Page.Auto:
                _next.Enabled = connected;
                if (_status != null)
                {
                    if (!_appReachable) { _status.Text = "Screen Time isn't running, so the connection can't be checked. Start Screen Time from the Start menu."; _status.ForeColor = Ink2; }
                    else if (connected) { _status.Text = "✓ Connected! Screen Time can now see the websites you visit. Click Finish."; _status.ForeColor = SuccessText; }
                    else
                    {
                        int dots = (int)(DateTime.Now - _pageShownAt).TotalSeconds % 4;
                        _status.Text = "⏳ Waiting for the extension to connect" + new string('.', dots);
                        _status.ForeColor = Ink2;
                    }
                }
                if (_trouble != null) _trouble.Visible = !connected && (DateTime.Now - _pageShownAt).TotalSeconds > 75;
                break;
            case Page.Done:
                _next.Enabled = true;
                break;
        }
        AcceptButton = _next.Enabled ? _next : null;
    }

    private bool ConfirmSkip()
    {
        if (_skipConfirmed || _page == Page.Done) return true;
        var skip = new TaskDialogButton("Skip for now");
        var stay = new TaskDialogButton("Keep setting up");
        var page = new TaskDialogPage
        {
            Caption = "Screen Time",
            Heading = "Skip website tracking?",
            Text = "Screen Time will still track your apps, but not which websites you visit.\n\n" +
                   "You can set it up later: right-click the Screen Time icon in the taskbar tray → “Set up browser extension…”.",
            Buttons = { stay, skip },
            DefaultButton = stay,
            Icon = TaskDialogIcon.Information,
        };
        bool result = TaskDialog.ShowDialog(this, page) == skip;
        _skipConfirmed = result;
        return result;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !ConfirmSkip()) { e.Cancel = true; return; }
        _timer.Stop();
        base.OnFormClosing(e);
    }

    // ---------- connection check ----------

    private bool IsConnected(bool onlyChosenBrowser)
    {
        string exe = _browser?.Exe;
        foreach (var e in _extensions)
        {
            bool sameBrowser = !string.IsNullOrEmpty(exe) && string.Equals(e.Browser, exe, StringComparison.OrdinalIgnoreCase);
            if (sameBrowser && e.SecondsAgo <= 90) return true;
            // A brand-new install counts right away, even before the app has worked out which browser it's in.
            if (!onlyChosenBrowser && _baselineLoaded && !_baseline.Contains(e.Id) && e.SecondsAgo <= 120) return true;
        }
        return false;
    }

    private async void Poll()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"http://127.0.0.1:{Program.Port}/api/now"));
            var list = new List<(string, string, long)>();
            if (doc.RootElement.TryGetProperty("extensions", out var exts))
                foreach (var e in exts.EnumerateArray())
                    list.Add((e.GetProperty("id").GetString(), e.GetProperty("browser").GetString(), e.GetProperty("secondsAgo").GetInt64()));
            _extensions = list;
            if (!_baselineLoaded) { foreach (var e in list) _baseline.Add(e.Item1); _baselineLoaded = true; }
            _appReachable = true;
        }
        catch { _appReachable = false; }
        finally { _polling = false; }
        if (!IsDisposed) UpdateButtons();
    }
}

/// <summary>A simple drawing of the browser's extensions page, highlighting where to click.</summary>
internal sealed class BrowserSketch : Control
{
    public enum Highlight { DevMode, LoadUnpacked }

    private readonly BrowserInfo _browser;
    private readonly Highlight _focus;
    private static readonly Color Accent = Color.FromArgb(42, 120, 214);

    public BrowserSketch(BrowserInfo browser, Highlight focus)
    {
        _browser = browser;
        _focus = focus;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.White;
    }

    private float S(float v) => v * DeviceDpi / 96f;

    private static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var small = new Font("Segoe UI", 8.25f);
        using var smallBold = new Font("Segoe UI Semibold", 8.25f);
        using var title = new Font("Segoe UI Semibold", 10f);
        using var borderPen = new Pen(Color.FromArgb(200, 199, 192), 1);
        using var inkBrush = new SolidBrush(Color.FromArgb(11, 11, 11));
        using var mutedBrush = new SolidBrush(Color.FromArgb(137, 135, 129));
        using var cardBrush = new SolidBrush(Color.FromArgb(239, 238, 233));
        using var accentBrush = new SolidBrush(Accent);
        using var ringPen = new Pen(Color.FromArgb(230, 70, 60), S(2.5f));

        var win = new RectangleF(S(1), S(1), Width - S(3), Height - S(3));
        using (var path = Round(win, S(8)))
        {
            g.FillPath(Brushes.White, path);
            g.DrawPath(borderPen, path);
        }
        // tab strip + address bar
        var bar = new RectangleF(win.X, win.Y, win.Width, S(30));
        using (var top = Round(bar, S(8))) g.FillPath(cardBrush, top);
        g.FillRectangle(cardBrush, bar.X, bar.Bottom - S(8), bar.Width, S(8));
        var address = new RectangleF(win.X + S(54), win.Y + S(6), win.Width - S(108), S(18));
        using (var a = Round(address, S(9))) g.FillPath(Brushes.White, a);
        g.DrawString(_browser?.ExtensionsUrl ?? "chrome://extensions", small, inkBrush, address.X + S(10), address.Y + S(2));
        for (int i = 0; i < 3; i++) g.FillEllipse(mutedBrush, win.X + S(12 + i * 12), win.Y + S(11), S(7), S(7));

        float top0 = bar.Bottom + S(10);
        bool devOn = _focus == Highlight.LoadUnpacked;
        RectangleF toggle, loadBtn = RectangleF.Empty;

        if (_browser?.Layout == ExtensionPageLayout.EdgeStyle)
        {
            // left menu with the Developer mode switch near its bottom
            var side = new RectangleF(win.X + S(10), top0, S(170), win.Bottom - top0 - S(10));
            g.DrawString("Extensions", title, inkBrush, side.X + S(4), side.Y);
            string[] items = ["Installed extensions", "Keyboard shortcuts", "Get extensions"];
            for (int i = 0; i < items.Length; i++) g.DrawString(items[i], small, mutedBrush, side.X + S(4), side.Y + S(28 + i * 18));
            float devY = side.Bottom - S(24);
            g.DrawString("Developer mode", smallBold, inkBrush, side.X + S(4), devY);
            toggle = new RectangleF(side.X + S(110), devY, S(30), S(16));
            float mainX = side.Right + S(16);
            g.DrawString("Installed extensions", title, inkBrush, mainX, top0);
            float cardsTop = top0 + S(30);
            if (devOn)
            {
                string[] buttons = ["Load unpacked", "Pack extension", "Update"];
                float x = mainX;
                foreach (var b in buttons)
                {
                    var size = g.MeasureString(b, small);
                    var r = new RectangleF(x, top0 + S(26), size.Width + S(14), S(20));
                    using (var p = Round(r, S(4))) g.DrawPath(borderPen, p);
                    g.DrawString(b, small, inkBrush, r.X + S(7), r.Y + S(3));
                    if (b == "Load unpacked") loadBtn = r;
                    x = r.Right + S(8);
                }
                cardsTop += S(28);
            }
            DrawCards(g, cardBrush, mainX, cardsTop, win.Right - mainX - S(12), win.Bottom - cardsTop - S(10));
        }
        else
        {
            // Chrome style: header with the Developer mode switch at the right
            g.DrawString("Extensions", title, inkBrush, win.X + S(14), top0);
            var search = new RectangleF(win.X + win.Width / 2 - S(90), top0, S(180), S(20));
            using (var p = Round(search, S(10))) g.FillPath(cardBrush, p);
            g.DrawString("Search extensions", small, mutedBrush, search.X + S(10), search.Y + S(3));
            var devSize = g.MeasureString("Developer mode", smallBold);
            toggle = new RectangleF(win.Right - S(14) - S(30), top0 + S(2), S(30), S(16));
            g.DrawString("Developer mode", smallBold, inkBrush, toggle.X - devSize.Width - S(6), top0 + S(2));
            float cardsTop = top0 + S(32);
            if (devOn)
            {
                string[] buttons = ["Load unpacked", "Pack extension", "Update"];
                float x = win.X + S(14);
                foreach (var b in buttons)
                {
                    var size = g.MeasureString(b, small);
                    var r = new RectangleF(x, top0 + S(30), size.Width + S(14), S(20));
                    using (var p = Round(r, S(4))) g.DrawPath(borderPen, p);
                    g.DrawString(b, small, inkBrush, r.X + S(7), r.Y + S(3));
                    if (b == "Load unpacked") loadBtn = r;
                    x = r.Right + S(8);
                }
                cardsTop += S(30);
            }
            DrawCards(g, cardBrush, win.X + S(14), cardsTop, win.Width - S(28), win.Bottom - cardsTop - S(10));
        }

        // the switch
        using (var t = Round(toggle, toggle.Height / 2)) g.FillPath(devOn ? accentBrush : new SolidBrush(Color.FromArgb(190, 189, 182)), t);
        float knob = toggle.Height - S(4);
        g.FillEllipse(Brushes.White, devOn ? toggle.Right - knob - S(2) : toggle.X + S(2), toggle.Y + S(2), knob, knob);

        // highlight what to click, with a short label
        var target = _focus == Highlight.DevMode ? toggle : loadBtn;
        if (target != RectangleF.Empty)
        {
            var ring = RectangleF.Inflate(target, S(6), S(6));
            using (var p = Round(ring, S(8))) g.DrawPath(ringPen, p);
            string label = _focus == Highlight.DevMode ? "Turn this on" : "Click this";
            var ls = g.MeasureString(label, smallBold);
            float lx = Math.Min(Math.Max(ring.X + ring.Width / 2 - ls.Width / 2, win.X + S(6)), win.Right - ls.Width - S(6));
            float ly = ring.Bottom + S(4);
            if (ly + ls.Height > win.Bottom - S(2)) ly = ring.Y - ls.Height - S(4);
            var tag = new RectangleF(lx - S(4), ly, ls.Width + S(8), ls.Height);
            using (var p = Round(tag, S(4))) g.FillPath(new SolidBrush(Color.FromArgb(230, 70, 60)), p);
            g.DrawString(label, smallBold, Brushes.White, lx, ly);
        }
    }

    private void DrawCards(Graphics g, Brush brush, float x, float y, float w, float h)
    {
        if (h < S(30)) return;
        float cw = (w - S(12)) / 2, ch = Math.Min(S(56), h);
        for (int i = 0; i < 2; i++)
        {
            var r = new RectangleF(x + i * (cw + S(12)), y, cw, ch);
            using var p = Round(r, S(6));
            g.FillPath(brush, p);
        }
    }
}
