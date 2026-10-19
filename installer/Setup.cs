using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ScreenTimeSetup
{
    internal sealed class Options
    {
        public string Dir = Install.DefaultDir;
        public bool Silent, NoStart, NoRegister, Update;
        public bool StartMenu = true, Desktop = true, AutoStart = true;
        public string ScreensDir;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            foreach (var a in args)
            {
                string arg = a.Trim();
                if (arg.Equals("/S", StringComparison.OrdinalIgnoreCase) || arg.Equals("--silent", StringComparison.OrdinalIgnoreCase)) o.Silent = true;
                else if (arg.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase)) o.Dir = Path.GetFullPath(arg.Substring(6).Trim('"'));
                else if (arg.Equals("--update", StringComparison.OrdinalIgnoreCase)) o.Silent = o.Update = true;
                else if (arg.Equals("--no-start", StringComparison.OrdinalIgnoreCase)) o.NoStart = true;
                else if (arg.Equals("--no-shortcuts", StringComparison.OrdinalIgnoreCase)) o.StartMenu = o.Desktop = false;
                else if (arg.Equals("--no-autostart", StringComparison.OrdinalIgnoreCase)) o.AutoStart = false;
                else if (arg.Equals("--no-register", StringComparison.OrdinalIgnoreCase)) o.NoRegister = true;
                else if (arg.StartsWith("--screens=", StringComparison.OrdinalIgnoreCase)) o.ScreensDir = arg.Substring(10).Trim('"');
            }
            return o;
        }

        /// <summary>
        /// When Screen Time is already installed (and always for --update, run by its updater): install over the
        /// existing copy and keep the choices made back then (folder, desktop shortcut, start with Windows)
        /// instead of the defaults.
        /// </summary>
        public void KeepInstalledChoices()
        {
            if (NoRegister) return;   // a test install leaves everything of the real one alone
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Install.UninstallKey))
                    if (Dir == Install.DefaultDir && k?.GetValue("InstallLocation") is string dir && File.Exists(Path.Combine(dir, "ScreenTime.exe"))) Dir = dir;
                using (var run = Registry.CurrentUser.OpenSubKey(Install.RunKey))
                    AutoStart = run?.GetValue("ScreenTime") is string;
            }
            catch { }
            Desktop = File.Exists(Install.DesktopShortcut);
            StartMenu = true;
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            var o = Options.Parse(args);
            if (o.Silent)
            {
                if (o.Update) o.KeepInstalledChoices();
                try
                {
                    Install.Run(o, null);
                    if (!o.NoStart) Install.LaunchApp(o.Dir, o.Update ? "" : "--installed");
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    if (o.Update)
                    {
                        // The running app was already closed: start the copy that's there again, and let it
                        // tell the user why the update failed.
                        try
                        {
                            string dir = Path.Combine(Path.GetTempPath(), "ScreenTime-Update");
                            Directory.CreateDirectory(dir);
                            File.WriteAllText(Path.Combine(dir, "last-error.txt"), ex.Message);
                        }
                        catch { }
                        if (!o.NoStart) Install.LaunchApp(o.Dir, "");
                    }
                    return 1;
                }
            }
            if (Install.InstalledVersion() != null) o.KeepInstalledChoices();   // updating: start from the current choices
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm(o));
            return 0;
        }
    }

    /// <summary>The actual install steps (no UI).</summary>
    internal static class Install
    {
        public static readonly string DefaultDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ScreenTime");
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ScreenTime";
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public static readonly string DesktopShortcut =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Screen Time.lnk");
        private const string QuitEventName = @"Local\ScreenTimeTracker.Quit";

        public static string Version
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version;
                return v.Major + "." + v.Minor + "." + v.Build;
            }
        }

        public static string InstalledVersion()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                    return k?.GetValue("DisplayVersion") as string;
            }
            catch { return null; }
        }

        public static void Run(Options o, Action<int, string> progress)
        {
            if (progress == null) progress = (p, s) => { };

            progress(3, "Closing Screen Time if it's running…");
            if (!o.NoStart) StopRunningApp();   // the new version is about to start; a test install leaves the running one alone

            progress(8, "Copying files…");
            Directory.CreateDirectory(o.Dir);
            string root = Path.GetFullPath(o.Dir).TrimEnd('\\') + "\\";
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                long total = Math.Max(1, zip.Entries.Sum(e => e.Length)), done = 0;
                foreach (var entry in zip.Entries)
                {
                    string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // never write outside the install folder
                    if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\")) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    Retry(() => entry.ExtractToFile(target, true));
                    done += entry.Length;
                    progress(8 + (int)(80 * done / total), "Copying files…");
                }
            }

            string exe = Path.Combine(o.Dir, "ScreenTime.exe");
            progress(90, "Creating shortcuts…");
            string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Screen Time.lnk");
            string desktop = DesktopShortcut;
            if (o.StartMenu) Shortcut(startMenu, exe); else if (!o.NoRegister) TryDelete(startMenu);
            if (o.Desktop) Shortcut(desktop, exe); else if (!o.NoRegister) TryDelete(desktop);

            if (!o.NoRegister)
            {
                progress(95, "Registering…");
                using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (o.AutoStart) run.SetValue("ScreenTime", "\"" + exe + "\"");
                    else run.DeleteValue("ScreenTime", false);
                }
                using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    k.SetValue("DisplayName", "Screen Time");
                    k.SetValue("DisplayVersion", Version);
                    k.SetValue("Publisher", "Screen Time");
                    k.SetValue("DisplayIcon", exe);
                    k.SetValue("InstallLocation", o.Dir);
                    k.SetValue("UninstallString", "\"" + exe + "\" --uninstall");
                    k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    k.SetValue("EstimatedSize", (int)(DirSize(o.Dir) / 1024), RegistryValueKind.DWord);
                }
            }
            progress(100, "Done");
        }

        /// <summary>Lets a running Screen Time save its data and exit (older versions: save via the API, then stop).</summary>
        public static void StopRunningApp()
        {
            var running = Process.GetProcessesByName("ScreenTime");
            if (running.Length == 0) return;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:47321/api/summary?from=0&to=0");   // makes it write pending data
                req.Timeout = 3000;
                using (req.GetResponse()) { }
            }
            catch { }
            try
            {
                EventWaitHandle ev;
                if (EventWaitHandle.TryOpenExisting(QuitEventName, out ev)) using (ev) ev.Set();
            }
            catch { }
            foreach (var p in running)
            {
                try { if (!p.WaitForExit(8000)) { p.Kill(); p.WaitForExit(3000); } } catch { }
                p.Dispose();
            }
        }

        public static void LaunchApp(string dir, string args)
        {
            try { Process.Start(new ProcessStartInfo(Path.Combine(dir, "ScreenTime.exe"), args) { UseShellExecute = false, WorkingDirectory = dir }); }
            catch { }
        }

        private static void Retry(Action a)
        {
            for (int i = 0; ; i++)
            {
                try { a(); return; }
                catch (IOException) when (i < 10) { Thread.Sleep(500); }
                catch (UnauthorizedAccessException) when (i < 10) { Thread.Sleep(500); }
            }
        }

        private static void Shortcut(string lnkPath, string target)
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(shellType);
                object link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                var t = link.GetType();
                t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { target });
                t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, new object[] { Path.GetDirectoryName(target) });
                t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, new object[] { target + ",0" });
                t.InvokeMember("Description", BindingFlags.SetProperty, null, link, new object[] { "See how you spend time on your PC" });
                t.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            catch { }
        }

        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

        private static long DirSize(string dir)
        {
            try { return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
            catch { return 0; }
        }
    }

    /// <summary>The setup window: Welcome → Installing → Done.</summary>
    internal sealed class SetupForm : Form
    {
        private enum Page { Welcome, Installing, Done, Failed }

        private static readonly Color Accent = Color.FromArgb(42, 120, 214);
        private static readonly Color Ink = Color.FromArgb(11, 11, 11);
        private static readonly Color Ink2 = Color.FromArgb(82, 81, 78);
        private static readonly Color Muted = Color.FromArgb(137, 135, 129);
        private static readonly Color SuccessText = Color.FromArgb(0, 99, 0);
        private static readonly Color Line = Color.FromArgb(225, 224, 217);
        private static readonly Color Plane = Color.FromArgb(249, 249, 247);

        private readonly Options _o;
        private readonly Label _title, _subtitle;
        private readonly Panel _content;
        private readonly Button _primary, _secondary;
        private Page _page;
        private int _y;
        private ProgressBar _bar;
        private Label _status;
        private string _error;
        private bool _installing;

        private int S(int v) { return (int)Math.Round(v * DeviceDpi / 96.0); }
        private int ContentWidth { get { return _content.ClientSize.Width - _content.Padding.Horizontal; } }

        public SetupForm(Options o)
        {
            _o = o;
            Text = "Screen Time Setup";
            Font = new Font("Segoe UI", 9.75f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            ClientSize = new Size(S(600), S(500));
            Icon appIcon = null;
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico")) appIcon = new Icon(s);
                Icon = appIcon;
            }
            catch { }

            var header = new Panel { Dock = DockStyle.Top, Height = S(78), BackColor = Plane };
            var logo = new PictureBox { Size = new Size(S(40), S(40)), Location = new Point(S(24), S(19)), SizeMode = PictureBoxSizeMode.Zoom };
            try { if (appIcon != null) logo.Image = new Icon(appIcon, S(48), S(48)).ToBitmap(); } catch { }
            _title = new Label { Text = "Install Screen Time", Font = new Font("Segoe UI Semibold", 14f), ForeColor = Ink, AutoSize = true, Location = new Point(S(76), S(14)) };
            _subtitle = new Label { Text = "Version " + Install.Version, ForeColor = Ink2, AutoSize = true, Location = new Point(S(78), S(46)) };
            header.Controls.AddRange(new Control[] { logo, _title, _subtitle });
            header.Paint += (s, e) => { using (var p = new Pen(Line)) e.Graphics.DrawLine(p, 0, header.Height - 1, header.Width, header.Height - 1); };

            var footer = new Panel { Dock = DockStyle.Bottom, Height = S(62), BackColor = Plane };
            footer.Paint += (s, e) => { using (var p = new Pen(Line)) e.Graphics.DrawLine(p, 0, 0, footer.Width, 0); };
            _primary = MakeButton("Install", true);
            _secondary = MakeButton("Cancel", false);
            _primary.Size = new Size(S(130), S(34));
            _secondary.Size = new Size(S(110), S(34));
            _primary.Location = new Point(ClientSize.Width - S(24) - _primary.Width, S(14));
            _secondary.Location = new Point(_primary.Left - S(10) - _secondary.Width, S(14));
            _primary.Click += (s, e) => OnPrimary();
            _secondary.Click += (s, e) => Close();
            footer.Controls.AddRange(new Control[] { _secondary, _primary });

            _content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(28), S(22), S(28), S(10)) };
            Controls.Add(_content);
            Controls.Add(header);
            Controls.Add(footer);
            AcceptButton = _primary;

            ShowPage(Page.Welcome);
            if (_o.ScreensDir != null) Shown += (s, e) => RenderScreens();
        }

        private Button MakeButton(string text, bool primary)
        {
            var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = new Font("Segoe UI Semibold", 9.75f) };
            b.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(200, 199, 192);
            b.BackColor = primary ? Accent : Color.White;
            b.ForeColor = primary ? Color.White : Ink;
            b.EnabledChanged += (s, e) =>
            {
                b.BackColor = !b.Enabled ? Color.FromArgb(235, 234, 229) : primary ? Accent : Color.White;
                b.FlatAppearance.BorderColor = !b.Enabled ? Line : primary ? Accent : Color.FromArgb(200, 199, 192);
            };
            return b;
        }

        private T Add<T>(T c, int gapAfter) where T : Control
        {
            c.Location = new Point(_content.Padding.Left, _y);
            _content.Controls.Add(c);
            _y += c.Height + S(gapAfter);
            return c;
        }

        private Label Para(string text, Color color, float size = 9.75f, bool bold = false, int gap = 10)
        {
            var l = new Label { Text = text, ForeColor = color, Font = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size), AutoSize = false, UseMnemonic = false };
            l.Size = l.GetPreferredSize(new Size(ContentWidth, 0));
            l.Width = ContentWidth;
            return Add(l, gap);
        }

        private CheckBox Check(string text, bool value, Action<bool> set)
        {
            var c = new CheckBox { Text = "  " + text, Checked = value, AutoSize = true };
            c.CheckedChanged += (s, e) => set(c.Checked);
            return Add(c, 6);
        }

        private void ShowPage(Page p)
        {
            _page = p;
            _content.SuspendLayout();
            foreach (var c in _content.Controls.Cast<Control>().ToList()) { _content.Controls.Remove(c); c.Dispose(); }
            _y = _content.Padding.Top;
            string installed = Install.InstalledVersion();

            switch (p)
            {
                case Page.Welcome:
                    _title.Text = "Install Screen Time";
                    _subtitle.Text = "Version " + Install.Version;
                    Para("Screen Time shows how much time you spend in each app and on each website: today, this week and all time.", Ink, 10.5f, gap: 14);
                    Para("•  Runs quietly in the taskbar tray and uses almost no CPU or memory\n" +
                         "•  Charts, daily limits with reminders, and your full history in one dashboard\n" +
                         "•  Everything stays on this PC. Nothing is uploaded anywhere.", Ink2, gap: 16);
                    if (installed != null)
                        Para("Screen Time " + installed + " is already installed. Installing updates it; your history is kept.", SuccessText, bold: true, gap: 14);
                    Check("Start Screen Time automatically when I sign in to Windows (recommended)", _o.AutoStart, v => _o.AutoStart = v);
                    Check("Add a shortcut to the desktop", _o.Desktop, v => _o.Desktop = v);
                    _y += S(14);
                    Para("Installs for your Windows account only, so no administrator rights are needed:\n" + _o.Dir, Muted, 9f);
                    _primary.Text = installed != null ? "Update" : "Install";
                    _primary.Enabled = true;
                    _secondary.Text = "Cancel";
                    _secondary.Visible = true;
                    break;

                case Page.Installing:
                    _title.Text = installed != null ? "Updating Screen Time" : "Installing Screen Time";
                    _subtitle.Text = "This only takes a moment";
                    _status = Para("Preparing…", Ink2, gap: 10);
                    _bar = Add(new ProgressBar { Width = ContentWidth, Height = S(18), Minimum = 0, Maximum = 100, Style = ProgressBarStyle.Continuous }, 10);
                    _primary.Enabled = false;
                    _secondary.Visible = false;
                    break;

                case Page.Done:
                    _title.Text = "Screen Time is installed";
                    _subtitle.Text = "It's running now";
                    Para("✓  Installed and running.", SuccessText, 12f, bold: true, gap: 12);
                    Para("Find it in the taskbar tray at the bottom-right, next to the clock (it may be hidden under the ^ arrow). " +
                         "Click the clock icon any time to open your dashboard.", Ink, gap: 18);
                    Para("One more thing: website tracking", Ink, 10.5f, bold: true, gap: 6);
                    Para("To see which websites you spend time on, Screen Time needs a small extension in your browser. " +
                         "The next window walks you through adding it, step by step, for the browser you use. It takes about a minute.", Ink2, gap: 10);
                    _primary.Text = "Continue";
                    _primary.Enabled = true;
                    _secondary.Visible = false;
                    break;

                case Page.Failed:
                    _title.Text = "Something went wrong";
                    _subtitle.Text = "Screen Time could not be installed";
                    Para(_error ?? "Unknown error.", Ink, gap: 12);
                    Para("Close any open Screen Time windows and try again. If it keeps failing, restart the PC and run this setup again.", Ink2);
                    _primary.Text = "Try again";
                    _primary.Enabled = true;
                    _secondary.Text = "Close";
                    _secondary.Visible = true;
                    break;
            }
            _content.ResumeLayout();
        }

        private async void OnPrimary()
        {
            switch (_page)
            {
                case Page.Welcome:
                case Page.Failed:
                    ShowPage(Page.Installing);
                    _installing = true;
                    try
                    {
                        await Task.Run(() => Install.Run(_o, (pct, text) => BeginInvoke((Action)(() =>
                        {
                            if (_bar != null && !_bar.IsDisposed) _bar.Value = Math.Min(100, pct);
                            if (_status != null && !_status.IsDisposed) _status.Text = text;
                        }))));
                        if (!_o.NoStart) Install.LaunchApp(_o.Dir, "--installed");
                        _installing = false;
                        ShowPage(Page.Done);
                    }
                    catch (Exception ex)
                    {
                        _installing = false;
                        _error = ex.Message;
                        ShowPage(Page.Failed);
                    }
                    break;

                case Page.Done:
                    // The app is already running; this opens its browser-extension wizard as the next step.
                    _continued = true;
                    if (!_o.NoStart) Install.LaunchApp(_o.Dir, "--setup-extension");
                    Close();
                    break;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_installing && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
            if (_page == Page.Done && e.CloseReason == CloseReason.UserClosing && !_o.NoStart && _o.ScreensDir == null && !_continued)
            {
                // Closed with the X on the last page: still offer the extension step.
                _continued = true;
                Install.LaunchApp(_o.Dir, "--setup-extension");
            }
            base.OnFormClosing(e);
        }
        private bool _continued;

        /// <summary>Development aid (--screens=DIR): saves each page as a PNG and exits.</summary>
        private void RenderScreens()
        {
            Directory.CreateDirectory(_o.ScreensDir);
            int n = 0;
            foreach (var p in new[] { Page.Welcome, Page.Installing, Page.Done, Page.Failed })
            {
                if (p == Page.Failed) _error = "Example error message.";
                ShowPage(p);
                if (p == Page.Installing) { _bar.Value = 45; _status.Text = "Copying files…"; }
                Application.DoEvents();
                using (var bmp = new Bitmap(Width, Height))
                {
                    DrawToBitmap(bmp, new Rectangle(Point.Empty, Size));
                    bmp.Save(Path.Combine(_o.ScreensDir, string.Format("setup-{0:00}-{1}.png", ++n, p)));
                }
            }
            _continued = true;
            Close();
        }
    }
}
