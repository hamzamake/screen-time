using System.Diagnostics;
using Microsoft.Win32;

namespace ScreenTime;

internal static class Program
{
    public const int Port = 47321;
    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenTime");

    [STAThread]
    private static void Main(string[] args)
    {
        bool Has(string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

        if (Has("--uninstall"))
        {
            ApplicationConfiguration.Initialize();
            Installer.Uninstall();
            return;
        }
        if (args.FirstOrDefault(a => a.StartsWith("--wizard-screens=", StringComparison.OrdinalIgnoreCase)) is string screens)
        {
            ApplicationConfiguration.Initialize();
            ExtensionWizard.RenderScreens(screens["--wizard-screens=".Length..]);
            return;
        }

        bool setupExtension = Has("--setup-extension");
        using var mutex = new Mutex(true, @"Local\ScreenTimeTracker.SingleInstance", out bool firstInstance);
        if (!firstInstance)
        {
            if (setupExtension)
            {
                // Screen Time is already running: show just the wizard (it talks to the running app).
                ApplicationConfiguration.Initialize();
                Application.Run(new ExtensionWizard());
            }
            else OpenDashboard();   // already running: launching again just opens the dashboard
            return;
        }

        ApplicationConfiguration.Initialize();
        Db db;
        try
        {
            Directory.CreateDirectory(DataDir);
            db = new Db(Path.Combine(DataDir, "screentime.db"));
        }
        catch (Exception ex)
        {
            MessageBox.Show("Screen Time could not open its database:\n\n" + ex.Message, "Screen Time",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Application.Run(new TrayApp(db, installedBySetup: Has("--installed"), showExtensionWizard: setupExtension));
    }

    public static void OpenDashboard()
    {
        try { Process.Start(new ProcessStartInfo($"http://127.0.0.1:{Port}/") { UseShellExecute = true }); } catch { }
    }
}

internal sealed class TrayApp : ApplicationContext
{
    private readonly Db _db;
    private readonly Tracker _tracker;
    private readonly WebServer _server;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolStripMenuItem _pauseMenu, _resumeItem, _mediaItem, _openItem, _startupItem, _idleMenu, _updateItem;
    private readonly Settings _settings;
    private readonly Updater _updater;
    private bool _manualUpdateCheck;
    private string _updateNotified;
    private readonly HashSet<string> _limitWarned = new();
    private readonly List<ToolStripItem> _todayItems = new();
    private DateTime _pausedUntil = DateTime.MaxValue;
    private int _lastBackupDay;
    private bool _shutDown;
    private bool _handoff;   // closing because the installer asked (an update): the next version carries on
    private readonly EventWaitHandle _quitEvent;
    private readonly RegisteredWaitHandle _quitWait;

    public TrayApp(Db db, bool installedBySetup, bool showExtensionWizard)
    {
        _db = db;
        _db.StartSession();
        _tracker = new Tracker(db);
        _settings = new Settings(db, _tracker);
        _updater = new Updater(_settings);

        // ---- tray menu ----
        _menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Open dashboard", null, (_, _) => Program.OpenDashboard())
            { Font = new Font(_menu.Font, FontStyle.Bold) };

        _pauseMenu = new ToolStripMenuItem("Pause tracking");
        _pauseMenu.DropDownItems.AddRange([
            new ToolStripMenuItem("For 15 minutes", null, (_, _) => Pause(DateTime.Now.AddMinutes(15))),
            new ToolStripMenuItem("For 1 hour", null, (_, _) => Pause(DateTime.Now.AddHours(1))),
            new ToolStripMenuItem("For 3 hours", null, (_, _) => Pause(DateTime.Now.AddHours(3))),
            new ToolStripMenuItem("Until tomorrow", null, (_, _) => Pause(DateTime.Today.AddDays(1))),
            new ToolStripMenuItem("Until I resume", null, (_, _) => Pause(DateTime.MaxValue)),
        ]);
        _resumeItem = new ToolStripMenuItem("Resume tracking", null, (_, _) => Resume()) { Visible = false };

        _idleMenu = new ToolStripMenuItem("Idle timeout");
        foreach (int min in new[] { 1, 2, 3, 5, 10, 15, 30 })
        {
            int captured = min;
            _idleMenu.DropDownItems.Add(new ToolStripMenuItem($"{min} minute{(min == 1 ? "" : "s")}", null, (_, _) => _settings.IdleMinutes = captured)
                { Tag = min, Checked = _tracker.IdleTimeoutMs == min * 60_000 });
        }
        _mediaItem = new ToolStripMenuItem("Keep counting while app plays sound", null, (_, _) => _settings.CountMedia = !_settings.CountMedia)
            { Checked = _tracker.CountMedia, ToolTipText = "When you're not touching the mouse/keyboard but the app in front is playing sound (a video, a call), keep counting." };
        _openItem = new ToolStripMenuItem("Track open time", null, (_, _) => _settings.TrackOpenTime = !_settings.TrackOpenTime)
            { Checked = _tracker.TrackOpenTime, ToolTipText = "Also record how long apps and websites are open, not just how long you use them." };
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => _settings.AutoStart = !_settings.AutoStart);
        _updateItem = new ToolStripMenuItem("Check for updates", null, (_, _) => OnUpdateItem())
            { ShortcutKeyDisplayString = "v" + Updater.CurrentText };
        _menu.Items.AddRange([
            open, new ToolStripSeparator(), _resumeItem, _pauseMenu, _idleMenu, _mediaItem, _openItem, _startupItem, new ToolStripSeparator(),
            new ToolStripMenuItem("Set up browser extension…", null, (_, _) => ExtensionWizard.ShowWizard()),
            new ToolStripMenuItem("Open data folder", null, (_, _) => OpenFolder()),
            _updateItem,
            new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()),
        ]);
        _menu.Opening += (_, _) => { RefreshSettingsChecks(); BuildTodaySection(); };

        // The installer (when updating) and the uninstaller ask a running copy to save and exit through this event.
        _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Installer.QuitEventName);
        var ui = SynchronizationContext.Current;
        _settings.Changed += () => ui?.Post(_ => RefreshSettingsChecks(), null);   // may come from the dashboard (server thread)
        _updater.Changed += () => ui?.Post(_ => OnUpdaterChanged(), null);
        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitEvent, (_, _) => ui?.Post(_ => { _handoff = true; ExitThread(); }, null), null, Timeout.Infinite, true);

        _tray = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Screen Time",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Program.OpenDashboard(); };
        _tray.BalloonTipClicked += (_, _) => Program.OpenDashboard();

        // First run: register for autostart (unless the installer already asked the user). Later runs: keep the
        // registered path current if the exe moved.
        if (_db.GetMeta("autostart_init") == null) { if (!installedBySetup) Settings.SetAutoStart(true); _db.SetMeta("autostart_init", "1"); }
        else if (Settings.IsAutoStartRegistered()) Settings.SetAutoStart(true);

        // A pause survives restarts (e.g. "until I resume").
        string pausedUntil = _db.GetMeta("paused_until");
        if (pausedUntil == "forever") Pause(DateTime.MaxValue);
        else if (long.TryParse(pausedUntil, out long ticks) && new DateTime(ticks) > DateTime.Now) Pause(new DateTime(ticks));

        // Restarted by an update: count the seconds the restart took, and know the browser tabs right away.
        if (_db.GetMeta("handoff") is { Length: > 0 } handoff)
        {
            _db.SetMeta("handoff", "");
            _tracker.RestoreHandoff(handoff);
        }

        _server = new WebServer(db, _tracker, _settings, _updater, Program.Port)
        {
            PausedUntil = () => _tracker.Paused ? _pausedUntil : null,
            SetPause = until => ui?.Post(_ => { if (until == null) Resume(); else Pause(until.Value); }, null),
        };
        try { _server.Start(); }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(8000, "Screen Time", $"Dashboard port {Program.Port} is busy ({ex.Message}). Tracking still works.", ToolTipIcon.Warning);
        }

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerMode;
        SystemEvents.SessionEnding += OnSessionEnding;

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) =>
        {
            try
            {
                if (_tracker.Paused && _pausedUntil != DateTime.MaxValue && DateTime.Now >= _pausedUntil) Resume();
                if (_tracker.Tick()) EveryMinute();
            }
            catch { }
        };
        _timer.Start();
        UpdateTooltip();
        AnnounceNewVersion();
        _updater.Start();
        if (showExtensionWizard) ExtensionWizard.ShowWizard();
    }

    /// <summary>First start after an update: say so (and the reason, if an automatic update failed).</summary>
    private void AnnounceNewVersion()
    {
        string last = _db.GetMeta("app_version");
        if (last != Updater.CurrentText)
        {
            _db.SetMeta("app_version", Updater.CurrentText);
            if (last != null)
                _tray.ShowBalloonTip(8000, "Screen Time was updated", $"You're now on version {Updater.CurrentText}. Your history is untouched.", ToolTipIcon.Info);
        }
        else if (_updater.Status == Updater.State.Failed)
            _tray.ShowBalloonTip(10_000, "Screen Time update failed", _updater.Error, ToolTipIcon.Warning);
    }

    private void OnUpdateItem()
    {
        if (_updater.Status is Updater.State.Checking or Updater.State.Downloading or Updater.State.Installing) return;
        _manualUpdateCheck = true;
        if (_updater.Status == Updater.State.Available) _ = _updater.InstallAsync();
        else _ = _updater.CheckAsync(install: _settings.AutoUpdate);
    }

    private void OnUpdaterChanged()
    {
        var latest = _updater.Latest;
        switch (_updater.Status)
        {
            case Updater.State.Checking: _updateItem.Text = "Checking for updates…"; break;
            case Updater.State.Downloading: _updateItem.Text = $"Downloading update… {_updater.DownloadPercent}%"; break;
            case Updater.State.Installing: _updateItem.Text = "Installing update…"; break;
            case Updater.State.Available:
                _updateItem.Text = $"Install update {latest.VersionText}";
                if (_updateNotified != latest.VersionText && !_settings.AutoUpdate)
                {
                    _updateNotified = latest.VersionText;
                    _tray.ShowBalloonTip(10_000, "Screen Time update available",
                        $"Version {latest.VersionText} is ready. Click to open the dashboard and install it.", ToolTipIcon.Info);
                }
                break;
            default:
                _updateItem.Text = "Check for updates";
                if (_manualUpdateCheck && _updater.Status == Updater.State.UpToDate)
                    _tray.ShowBalloonTip(5000, "Screen Time is up to date", $"You have the latest version ({Updater.CurrentText}).", ToolTipIcon.Info);
                else if (_manualUpdateCheck && _updater.Status == Updater.State.Failed)
                    _tray.ShowBalloonTip(8000, "Couldn't update Screen Time", _updater.Error, ToolTipIcon.Warning);
                break;
        }
        if (_updater.Status is not (Updater.State.Checking or Updater.State.Downloading or Updater.State.Available)) _manualUpdateCheck = false;
    }

    private static Icon LoadIcon()
    {
        using var s = typeof(TrayApp).Assembly.GetManifestResourceStream("app.ico");
        return new Icon(s, SystemInformation.SmallIconSize);
    }

    /// <summary>Runs right after each once-a-minute save.</summary>
    private void EveryMinute()
    {
        UpdateTooltip();
        CheckLimits();
        int today = Db.DayOf(DateTime.Now);
        if (_lastBackupDay != today)
        {
            _lastBackupDay = today;   // once per day (and per app start); a failure just waits for the next one
            try { _db.Backup(Path.Combine(Program.DataDir, "backups"), DateTime.Now); } catch { }
        }
    }

    private void CheckLimits()
    {
        int today = Db.DayOf(DateTime.Now);
        var limits = _db.Limits(today);
        foreach (var l in limits)
        {
            if (l.NotifiedDay == today || l.UsedMs < l.Minutes * 60_000L) continue;
            _tray.ShowBalloonTip(10_000, "Daily limit reached",
                $"{l.Name}: {Format(l.UsedMs)} today (your limit is {Format(l.Minutes * 60_000L)}).", ToolTipIcon.Info);
            _db.MarkLimitNotified(l.Kind, l.Target, today);
            return;   // one notification at a time; any others show up next minute
        }
        // A heads-up 5 minutes before a limit (of 15 minutes or more) is reached, once a day.
        foreach (var l in limits)
        {
            long left = l.Minutes * 60_000L - l.UsedMs;
            if (l.NotifiedDay == today || l.Minutes < 15 || left <= 0 || left > 5 * 60_000L) continue;
            if (!_limitWarned.Add($"{today}:{l.Kind}:{l.Target}")) continue;
            _tray.ShowBalloonTip(8000, "5 minutes left",
                $"{l.Name}: {Format(l.UsedMs)} of your {Format(l.Minutes * 60_000L)} daily limit used.", ToolTipIcon.Info);
            return;
        }
    }

    /// <summary>Top of the tray menu: today's total and most used apps, refreshed each time the menu opens.</summary>
    private void BuildTodaySection()
    {
        foreach (var item in _todayItems) { _menu.Items.Remove(item); item.Dispose(); }
        _todayItems.Clear();
        try
        {
            _tracker.Flush();
            int today = Db.DayOf(DateTime.Now);
            var items = new List<ToolStripItem>
            {
                new ToolStripMenuItem("Today", null, (_, _) => Program.OpenDashboard())
                    { ShortcutKeyDisplayString = Format(_db.DayTotal(today)), Font = new Font(_menu.Font, FontStyle.Bold) },
            };
            foreach (var (name, ms) in _db.TopApps(today, 5))
                items.Add(new ToolStripMenuItem("    " + (name.Length > 36 ? name[..35] + "…" : name), null, (_, _) => Program.OpenDashboard())
                    { ShortcutKeyDisplayString = Format(ms) });
            items.Add(new ToolStripSeparator());
            for (int i = 0; i < items.Count; i++) _menu.Items.Insert(i, items[i]);
            _todayItems.AddRange(items);
        }
        catch { }
    }

    private void UpdateTooltip()
    {
        long ms = _db.DayTotal(Db.DayOf(DateTime.Now));
        string state = !_tracker.Paused ? ""
            : _pausedUntil == DateTime.MaxValue ? " (paused)"
            : $" (paused until {_pausedUntil:t})";
        string text = $"Screen Time{state}\nToday: {Format(ms)}";
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    private static string Format(long ms)
    {
        long min = ms / 60_000;
        return min < 60 ? $"{min}m" : $"{min / 60}h {min % 60}m";
    }

    private void Pause(DateTime until)
    {
        _pausedUntil = until;
        _tracker.Paused = true;
        _db.SetMeta("paused_until", until == DateTime.MaxValue ? "forever" : until.Ticks.ToString());
        _resumeItem.Text = until == DateTime.MaxValue ? "Resume tracking (paused)" : $"Resume tracking (paused until {until:t})";
        _resumeItem.Visible = true;
        _pauseMenu.Visible = false;
        UpdateTooltip();
    }

    private void Resume()
    {
        _pausedUntil = DateTime.MaxValue;
        _tracker.Paused = false;
        _db.SetMeta("paused_until", "");
        _resumeItem.Visible = false;
        _pauseMenu.Visible = true;
        UpdateTooltip();
    }

    private void RefreshSettingsChecks()
    {
        int idle = _settings.IdleMinutes;
        foreach (ToolStripMenuItem item in _idleMenu.DropDownItems) item.Checked = (int)item.Tag == idle;
        _mediaItem.Checked = _settings.CountMedia;
        _openItem.Checked = _settings.TrackOpenTime;
        _startupItem.Checked = _settings.AutoStart;
    }

    private static void OpenFolder()
    {
        try { Process.Start(new ProcessStartInfo(Program.DataDir) { UseShellExecute = true }); } catch { }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                _tracker.Locked = true;
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                _tracker.Locked = false;
                break;
        }
    }

    private void OnPowerMode(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) _tracker.Shutdown();
    }

    private void OnSessionEnding(object sender, SessionEndingEventArgs e) => _tracker.Shutdown();

    protected override void ExitThreadCore()
    {
        if (!_shutDown)
        {
            _shutDown = true;
            _timer.Stop();
            _updater.Stop();
            _quitWait.Unregister(null);
            _quitEvent.Dispose();
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerMode;
            SystemEvents.SessionEnding -= OnSessionEnding;
            _tracker.Shutdown();
            if (_handoff) try { _db.SetMeta("handoff", _tracker.SaveHandoff()); } catch { }
            _server.Stop();
            _tray.Visible = false;
            _tray.Dispose();
            _db.Dispose();
        }
        base.ExitThreadCore();
    }
}
