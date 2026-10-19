using Microsoft.Win32;

namespace ScreenTime;

/// <summary>
/// User settings, shared by the tray menu and the dashboard's settings page. Values live in the
/// database (except "start with Windows", which is the registry Run entry itself). <see cref="Changed"/>
/// fires after any change, possibly on a server thread.
/// </summary>
internal sealed class Settings
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "ScreenTime";

    private readonly Db _db;
    private readonly Tracker _tracker;

    public event Action Changed;

    public Settings(Db db, Tracker tracker)
    {
        _db = db;
        _tracker = tracker;
        _tracker.IdleTimeoutMs = (int.TryParse(db.GetMeta("idle_minutes"), out int m) && m > 0 ? m : 5) * 60_000;
        _tracker.CountMedia = db.GetMeta("count_media") != "0";
        _tracker.TrackOpenTime = db.GetMeta("track_open") != "0";
    }

    public int IdleMinutes
    {
        get => _tracker.IdleTimeoutMs / 60_000;
        set
        {
            value = Math.Clamp(value, 1, 120);
            _tracker.IdleTimeoutMs = value * 60_000;
            _db.SetMeta("idle_minutes", value.ToString());
            Changed?.Invoke();
        }
    }

    public bool CountMedia
    {
        get => _tracker.CountMedia;
        set { _tracker.CountMedia = value; _db.SetMeta("count_media", value ? "1" : "0"); Changed?.Invoke(); }
    }

    /// <summary>Also record how long apps and websites are open (not just focused).</summary>
    public bool TrackOpenTime
    {
        get => _tracker.TrackOpenTime;
        set { _tracker.TrackOpenTime = value; _db.SetMeta("track_open", value ? "1" : "0"); Changed?.Invoke(); }
    }

    // ---- updates (see Updater) ----

    /// <summary>Look for new versions on a schedule (checking by hand always works).</summary>
    public bool UpdateCheck
    {
        get => _db.GetMeta("update_check") != "0";
        set { _db.SetMeta("update_check", value ? "1" : "0"); Changed?.Invoke(); }
    }

    public static readonly int[] UpdateHourChoices = [1, 6, 12, 24, 168];

    /// <summary>Hours between scheduled checks: one of <see cref="UpdateHourChoices"/>.</summary>
    public int UpdateHours
    {
        get => int.TryParse(_db.GetMeta("update_hours"), out int h) && UpdateHourChoices.Contains(h) ? h : 6;
        set
        {
            if (!UpdateHourChoices.Contains(value)) value = 6;
            _db.SetMeta("update_hours", value.ToString());
            Changed?.Invoke();
        }
    }

    /// <summary>Install new versions by itself (otherwise it only offers them).</summary>
    public bool AutoUpdate
    {
        get => _db.GetMeta("auto_update") != "0";
        set { _db.SetMeta("auto_update", value ? "1" : "0"); Changed?.Invoke(); }
    }

    /// <summary>When an automatic update installs: "now", or "away" (only while idle or the PC is locked).</summary>
    public string UpdateWhen
    {
        get => _db.GetMeta("update_when") == "away" ? "away" : "now";
        set { _db.SetMeta("update_when", value == "away" ? "away" : "now"); Changed?.Invoke(); }
    }

    /// <summary>Also offer pre-releases (betas) marked as such on GitHub.</summary>
    public bool UpdatePrerelease
    {
        get => _db.GetMeta("update_prerelease") == "1";
        set { _db.SetMeta("update_prerelease", value ? "1" : "0"); Changed?.Invoke(); }
    }

    /// <summary>Windows notifications about updates (available, installed, failed). Checking by hand always answers.</summary>
    public bool UpdateNotify
    {
        get => _db.GetMeta("update_notify") != "0";
        set { _db.SetMeta("update_notify", value ? "1" : "0"); Changed?.Invoke(); }
    }

    /// <summary>A version the user chose to skip: never installed or announced automatically. Empty for none.</summary>
    public string UpdateSkip
    {
        get => _db.GetMeta("update_skip") ?? "";
        set { _db.SetMeta("update_skip", value ?? ""); Changed?.Invoke(); }
    }

    /// <summary>When updates were last looked for (kept across restarts so the schedule holds).</summary>
    public DateTime UpdateLastCheck
    {
        get => long.TryParse(_db.GetMeta("update_last_check"), out long t) && t > 0 && t <= DateTime.MaxValue.Ticks ? new DateTime(t) : default;
        set => _db.SetMeta("update_last_check", value.Ticks.ToString());
    }

    public bool AutoStart
    {
        get => IsAutoStartRegistered();
        set { SetAutoStart(value); Changed?.Invoke(); }
    }

    public static bool IsAutoStartRegistered()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunName) is string;
    }

    public static void SetAutoStart(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue(RunName, $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue(RunName, false);
        }
        catch { }
    }
}
