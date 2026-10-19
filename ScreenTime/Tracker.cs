using System.Buffers;
using System.Text.Json;

namespace ScreenTime;

/// <summary>
/// Samples the foreground window once per second and attributes the elapsed time to the
/// foreground app (and to the active website when the app is a browser with the extension).
///
/// Idle handling: time since the last keyboard/mouse input is held as "pending" and only
/// committed when input happens again. If the idle timeout is reached first, the pending time
/// is thrown away, so walking away from the PC doesn't count at all (not even the first
/// minutes). While the foreground app is playing sound (a video, a call) time is committed
/// anyway, if that option is on.
///
/// Open time (optional): every 5 seconds the open windows are listed, and every app with an open
/// window gets those seconds as "open time"; websites get it while a tab of theirs is open in a
/// running browser (the extension reports all open tabs). Open time ignores idle: an app you left
/// open while away is still open. It never counts while paused, locked or asleep.
///
/// Browser trackers: each browser installation/profile reports through its own extension install
/// (with whether its window has focus). While a browser is in front, each second goes to exactly one
/// of them: the focused profile of that browser. That's what makes per-browser and per-group totals
/// add up without double counting.
///
/// Committed time sits in memory and is written to SQLite once a minute in one transaction.
///
/// Updates: before the app closes for an update it saves a "handoff" (what was in front, which tabs were
/// open); the new version restores it on start and counts the few seconds the restart took.
/// </summary>
internal sealed class Tracker
{
    public const int FlushMs = 60_000;
    private const long BrowserStaleMs = 150_000;   // extension heartbeats every 30s
    private const long MediaGraceMs = 30_000;      // quiet scenes in a video don't count as "stopped playing"
    private const long OpenSampleMs = 5000;        // open time is sampled every 5 s (cheap, and plenty precise)

    private readonly object _sync = new();
    private readonly Db _db;
    private readonly Dictionary<Key, long> _committed = new();
    private readonly Dictionary<Key, long> _pending = new();
    private readonly Dictionary<uint, string> _pidPaths = new();
    private readonly Dictionary<string, BrowserState> _browsers = new(StringComparer.OrdinalIgnoreCase);

    private sealed class BrowserState { public string Domain; public int SiteId = -1; public long At; }

    /// <summary>One extension install = one browser installation/profile.</summary>
    private sealed class Instance
    {
        public string Exe, Domain;
        public int BrowserId, SiteId = -1;
        public int[] OpenSiteIds = [];
        public bool? Focused;          // null: old extension that doesn't say
        public long At, FocusAt, ClientTs;
    }
    private readonly Dictionary<string, Instance> _instances = new(StringComparer.Ordinal);
    private int _activeBrowserId = -1;
    private readonly Dictionary<uint, (int Id, string Exe)> _pidApps = new();
    private readonly List<(IntPtr Hwnd, uint Pid)> _windows = new();
    private readonly HashSet<int> _openSeen = new();
    private readonly HashSet<string> _openExes = new(StringComparer.OrdinalIgnoreCase);
    private long _lastOpenSample;
    private readonly List<(Bucket Kind, int Id, int Sub)> _front = new();   // what the last tick counted usage for
    private int[] _openApps = [], _openSites = [];                         // what the last open-time sample saw

    private IntPtr _hwnd;
    private uint _pid;
    private bool _retry;
    private int _appId = -1;
    private string _exe, _appName;

    private long _lastTick, _lastFlush, _lastPidClear, _mediaCheckedAt;
    private long _lastSoundAt = -MediaGraceMs;
    private uint _lastInput;

    public volatile int IdleTimeoutMs = 5 * 60_000;
    public volatile bool CountMedia = true;
    public volatile bool Paused;
    public volatile bool Locked;
    public volatile bool TrackOpenTime = true;

    /// <summary>Where the foreground window comes from (tests point this at a known window).</summary>
    internal Func<IntPtr> ForegroundWindow = Native.GetForegroundWindow;

    public Tracker(Db db)
    {
        _db = db;
        _lastTick = _lastFlush = _lastPidClear = _lastOpenSample = Environment.TickCount64;
        _lastInput = Native.LastInputTick();
    }

    /// <summary>Called once per second on the UI thread. Returns true when data was flushed to disk.</summary>
    public bool Tick()
    {
        long now = Environment.TickCount64;
        long delta = now - _lastTick;
        _lastTick = now;
        if (delta < 0 || delta > 5000) delta = 0;   // resumed from sleep or clock weirdness: count nothing

        uint lastInput = Native.LastInputTick();
        uint idle = unchecked((uint)Environment.TickCount - lastInput);
        bool hadInput = lastInput != _lastInput;
        _lastInput = lastInput;

        lock (_sync)
        {
            if (Paused || Locked)
            {
                _pending.Clear();
            }
            else
            {
                bool media = false;
                if (CountMedia && idle >= 30_000)
                {
                    // Sound only decides anything once the idle timeout is reached (until then, the next input
                    // commits the time anyway), so check every 30s before that and every 10s after it (a check costs a few ms).
                    if (now - _mediaCheckedAt >= (idle >= (uint)IdleTimeoutMs ? 10_000 : 30_000))
                    {
                        _mediaCheckedAt = now;
                        if (Audio.IsPlaying(_exe, PathOf)) _lastSoundAt = now;
                    }
                    media = now - _lastSoundAt < MediaGraceMs;
                }

                if (idle >= (uint)IdleTimeoutMs && !media)
                {
                    _pending.Clear();   // user is away: discard everything since their last input
                }
                else
                {
                    IntPtr hwnd = ForegroundWindow();
                    if (hwnd == IntPtr.Zero) _appId = -1;
                    else Resolve(hwnd);
                    _activeBrowserId = -1;
                    _front.Clear();

                    if (_appId >= 0 && delta > 0)
                    {
                        _front.Add((Bucket.App, _appId, 0));
                        var inst = FrontInstance(now);
                        _activeBrowserId = inst?.BrowserId ?? -1;
                        if (inst != null)
                        {
                            _front.Add((Bucket.Browser, inst.BrowserId, 0));
                            if (inst.SiteId >= 0)
                            {
                                _front.Add((Bucket.Site, inst.SiteId, 0));
                                _front.Add((Bucket.BrowserSite, inst.BrowserId, inst.SiteId));
                            }
                        }
                        else if (_browsers.TryGetValue(_exe, out var b) && b.SiteId >= 0 && now - b.At < BrowserStaleMs)
                            _front.Add((Bucket.Site, b.SiteId, 0));   // extension older than 1.3
                        AddFront(DateTime.Now, delta);
                    }

                    if (hadInput || media) Commit();
                }
            }

            if (now - _lastOpenSample >= OpenSampleMs)
            {
                long openDelta = now - _lastOpenSample;
                _lastOpenSample = now;
                // A long gap means the PC was asleep: count nothing for it.
                if (openDelta <= 3 * OpenSampleMs && TrackOpenTime && !Paused && !Locked) SampleOpen(now, openDelta);
            }

            if (now - _lastPidClear > 600_000) { _pidPaths.Clear(); _pidApps.Clear(); _lastPidClear = now; }
            if (now - _lastFlush >= FlushMs) { FlushLocked(); return true; }
        }
        return false;
    }

    private void Resolve(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (hwnd == _hwnd && pid == _pid && !_retry) return;
        _hwnd = hwnd; _pid = pid; _retry = false;

        string path = PathOf(pid);
        string exe = path != null ? Path.GetFileName(path) : null;

        if (exe != null && exe.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            uint child = Native.FindUwpChildPid(hwnd, pid);
            string childPath = child != 0 ? PathOf(child) : null;
            if (childPath != null) { path = childPath; exe = Path.GetFileName(childPath); }
            else _retry = true;   // app still starting up; look again next tick
        }

        if (exe == null)
        {
            try { exe = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName + ".exe"; } catch { }
        }

        if (exe == null || exe.Equals("LockApp.exe", StringComparison.OrdinalIgnoreCase))
        {
            _appId = -1; _exe = null; _appName = null;
            return;
        }

        _appId = _db.AppId(exe, path);
        _exe = exe;
        _appName = _db.AppName(_appId);
    }

    /// <summary>Adds <paramref name="delta"/> of open time to every app with an open window and every site with an open tab.</summary>
    private void SampleOpen(long now, long delta)
    {
        var t = DateTime.Now;
        int day = Db.DayOf(t), hour = t.Hour;

        Native.OpenWindows(_windows);
        _openSeen.Clear();
        _openExes.Clear();
        foreach (var (hwnd, pid) in _windows)
        {
            var (id, exe) = AppForWindow(hwnd, pid);
            if (exe != null) _openExes.Add(exe);
            if (id >= 0 && _openSeen.Add(id)) Add(_committed, new Key(day, hour, Bucket.AppOpen, id), delta);
        }
        _openApps = [.. _openSeen];

        // Websites: tabs reported by the extension, while that browser still has a window open.
        _openSeen.Clear();
        foreach (var inst in _instances.Values)
        {
            if (now - inst.At >= BrowserStaleMs || !_openExes.Contains(inst.Exe)) continue;
            foreach (int site in inst.OpenSiteIds)
                if (_openSeen.Add(site)) Add(_committed, new Key(day, hour, Bucket.SiteOpen, site), delta);
        }
        _openSites = [.. _openSeen];
    }

    /// <summary>
    /// Which browser profile is in front: of the recently-heard-from installs of the foreground browser,
    /// the one that says it has focus (latest focus wins); with no focus info, the one focused last.
    /// </summary>
    private Instance FrontInstance(long now)
    {
        if (_exe == null) return null;
        Instance best = null;
        foreach (var i in _instances.Values)
        {
            if (now - i.At >= BrowserStaleMs || !string.Equals(i.Exe, _exe, StringComparison.OrdinalIgnoreCase)) continue;
            if (best == null) { best = i; continue; }
            bool iF = i.Focused == true, bF = best.Focused == true;
            if (iF != bF) { if (iF) best = i; continue; }
            if (i.FocusAt > best.FocusAt || i.FocusAt == best.FocusAt && i.At > best.At) best = i;
        }
        return best;
    }

    /// <summary>Adds usage time to everything in <see cref="_front"/> (pending until the next input).</summary>
    private void AddFront(DateTime t, long ms)
    {
        int day = Db.DayOf(t), hour = t.Hour;
        foreach (var (kind, id, sub) in _front) Add(_pending, new Key(day, hour, kind, id, sub), ms);
    }

    private (int Id, string Exe) AppForWindow(IntPtr hwnd, uint pid)
    {
        if (_pidApps.TryGetValue(pid, out var known)) return known;
        string path = PathOf(pid);
        string exe = path != null ? Path.GetFileName(path) : null;
        if (exe != null && exe.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            // UWP frame: the app is the child window's process (not cached: one host serves many apps).
            uint child = Native.FindUwpChildPid(hwnd, pid);
            if (child == 0) return (-1, null);
            return AppForWindow(hwnd, child);
        }
        var result = exe == null || exe.Equals("LockApp.exe", StringComparison.OrdinalIgnoreCase) ? (-1, exe) : (_db.AppId(exe, path), exe);
        if (path != null) _pidApps[pid] = result;
        return result;
    }

    private string PathOf(uint pid)
    {
        if (_pidPaths.TryGetValue(pid, out var p)) return p;
        p = Native.ProcessPath(pid);
        if (p != null) _pidPaths[pid] = p;
        return p;
    }

    private static void Add(Dictionary<Key, long> d, Key k, long ms)
    {
        d.TryGetValue(k, out long v);
        d[k] = v + ms;
    }

    private void Commit()
    {
        if (_pending.Count == 0) return;
        foreach (var (k, v) in _pending) Add(_committed, k, v);
        _pending.Clear();
    }

    private void FlushLocked()
    {
        _lastFlush = Environment.TickCount64;
        if (_committed.Count == 0) return;
        try
        {
            _db.WriteUsage(_committed);
            _committed.Clear();
        }
        catch
        {
            // Disk busy/full: keep the data in memory and try again next minute.
        }
    }

    /// <summary>Writes committed time to disk now (before answering dashboard queries).</summary>
    public void Flush() { lock (_sync) FlushLocked(); }

    /// <summary>App is closing / PC shutting down: keep recent pending time and save everything.</summary>
    public void Shutdown()
    {
        lock (_sync)
        {
            uint idle = unchecked((uint)Environment.TickCount - Native.LastInputTick());
            if (idle < (uint)IdleTimeoutMs && !Paused && !Locked) Commit();
            else _pending.Clear();
            FlushLocked();
        }
    }

    // ---- handoff across an update restart ----

    /// <summary>The longest restart that's still counted (a normal one takes a few seconds).</summary>
    private const long MaxHandoffGapMs = 120_000;

    /// <summary>
    /// Call after <see cref="Shutdown"/> when closing for an update: what was being counted right now, and
    /// the browser profiles' state, for <see cref="RestoreHandoff"/> in the new version.
    /// </summary>
    public string SaveHandoff()
    {
        lock (_sync)
        {
            long now = Environment.TickCount64;
            uint idle = unchecked((uint)Environment.TickCount - Native.LastInputTick());
            bool active = !Paused && !Locked && (idle < (uint)IdleTimeoutMs || CountMedia && now - _lastSoundAt < MediaGraceMs);
            bool open = TrackOpenTime && !Paused && !Locked && now - _lastOpenSample <= 3 * OpenSampleMs;
            var buf = new ArrayBufferWriter<byte>(1024);
            using (var w = new Utf8JsonWriter(buf))
            {
                w.WriteStartObject();
                w.WriteNumber("t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                w.WriteBoolean("active", active && _front.Count > 0);
                w.WriteStartArray("front");
                foreach (var (kind, id, sub) in _front)
                {
                    w.WriteStartArray();
                    w.WriteNumberValue((int)kind); w.WriteNumberValue(id); w.WriteNumberValue(sub);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                WriteInts(w, "openApps", open ? _openApps : []);
                WriteInts(w, "openSites", open ? _openSites : []);
                w.WriteStartArray("instances");
                foreach (var (key, i) in _instances)
                {
                    if (now - i.At >= BrowserStaleMs) continue;
                    w.WriteStartObject();
                    w.WriteString("key", key);
                    w.WriteString("exe", i.Exe);
                    w.WriteNumber("browserId", i.BrowserId);
                    if (i.Domain != null) w.WriteString("domain", i.Domain);
                    w.WriteNumber("siteId", i.SiteId);
                    if (i.Focused is bool f) w.WriteBoolean("focused", f);
                    w.WriteNumber("ts", i.ClientTs);
                    WriteInts(w, "open", i.OpenSiteIds);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(buf.WrittenSpan);
        }
    }

    /// <summary>
    /// Picks up where the previous version left off before an update: the browser profiles' tabs are known
    /// right away (no waiting for the extension's next heartbeat), and the seconds the restart took are
    /// counted for what was in front, under the usual idle rule (they stay pending until the next mouse or
    /// keyboard input, and are dropped if you're away past the idle timeout). Call after restoring a pause.
    /// </summary>
    public void RestoreHandoff(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            long gap = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - r.GetProperty("t").GetInt64();
            if (gap < 0 || gap > MaxHandoffGapMs) return;
            lock (_sync)
            {
                long now = Environment.TickCount64;
                foreach (var e in r.GetProperty("instances").EnumerateArray())
                {
                    string key = e.GetProperty("key").GetString(), exe = e.GetProperty("exe").GetString();
                    if (_instances.ContainsKey(key)) continue;   // the extension already reported to this version
                    bool? focused = e.TryGetProperty("focused", out var f) ? f.GetBoolean() : null;
                    var i = _instances[key] = new Instance
                    {
                        Exe = exe,
                        BrowserId = e.GetProperty("browserId").GetInt32(),
                        Domain = e.TryGetProperty("domain", out var d) ? d.GetString() : null,
                        SiteId = e.GetProperty("siteId").GetInt32(),
                        Focused = focused,
                        ClientTs = e.GetProperty("ts").GetInt64(),
                        OpenSiteIds = Ints(e.GetProperty("open")),
                        At = now,
                        FocusAt = focused == true ? now : 0,
                    };
                    if (!_browsers.ContainsKey(exe)) _browsers[exe] = new BrowserState { Domain = i.Domain, SiteId = i.SiteId, At = now };
                }

                if (Paused || Locked) return;
                var then = DateTime.Now.AddMilliseconds(-gap);
                if (r.GetProperty("active").GetBoolean())
                {
                    _front.Clear();
                    foreach (var e in r.GetProperty("front").EnumerateArray())
                        _front.Add(((Bucket)e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32()));
                    AddFront(then, gap);
                }
                if (TrackOpenTime)
                {
                    int day = Db.DayOf(then), hour = then.Hour;
                    foreach (int id in Ints(r.GetProperty("openApps"))) Add(_committed, new Key(day, hour, Bucket.AppOpen, id), gap);
                    foreach (int id in Ints(r.GetProperty("openSites"))) Add(_committed, new Key(day, hour, Bucket.SiteOpen, id), gap);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException
                                       or FormatException or IndexOutOfRangeException) { }
    }

    private static void WriteInts(Utf8JsonWriter w, string name, int[] values)
    {
        w.WriteStartArray(name);
        foreach (int v in values) w.WriteNumberValue(v);
        w.WriteEndArray();
    }

    private static int[] Ints(JsonElement e) => e.EnumerateArray().Select(x => x.GetInt32()).ToArray();

    /// <summary>Called by the web server when the browser extension reports the active tab.</summary>
    public void SetBrowserTab(string browserExe, string domain, int siteId)
    {
        lock (_sync)
        {
            if (!_browsers.TryGetValue(browserExe, out var b)) _browsers[browserExe] = b = new BrowserState();
            b.Domain = domain;
            b.SiteId = siteId;
            b.At = Environment.TickCount64;
        }
    }

    /// <summary>
    /// A report from one extension install. <paramref name="clientTs"/> (the browser's clock) only orders
    /// reports from that same install, so a late-arriving older report can't undo a newer one.
    /// </summary>
    public void ReportInstance(string instance, string browserExe, int browserId, string domain, int siteId,
                               bool? focused, long clientTs, int[] openSiteIds)
    {
        lock (_sync)
        {
            long now = Environment.TickCount64;
            if (!_instances.TryGetValue(instance, out var i)) _instances[instance] = i = new Instance();
            if (clientTs > 0 && clientTs < i.ClientTs) return;
            i.ClientTs = clientTs;
            i.Exe = browserExe;
            i.BrowserId = browserId;
            i.Domain = domain;
            i.SiteId = siteId;
            i.At = now;
            if (focused == true && i.Focused != true) i.FocusAt = now;
            i.Focused = focused;
            if (openSiteIds != null) i.OpenSiteIds = openSiteIds;
        }
    }

    /// <summary>The browser (profile) currently in front, or -1.</summary>
    public int ActiveBrowserId { get { lock (_sync) return _activeBrowserId; } }

    public readonly record struct Status(string State, string App, string Site, int ExtensionSecondsAgo);

    public Status Now()
    {
        lock (_sync)
        {
            long now = Environment.TickCount64;
            int extAgo = -1;
            foreach (var b in _browsers.Values)
            {
                int ago = (int)((now - b.At) / 1000);
                if (extAgo < 0 || ago < extAgo) extAgo = ago;
            }
            if (Paused) return new Status("paused", null, null, extAgo);
            if (Locked) return new Status("locked", null, null, extAgo);

            uint idle = unchecked((uint)Environment.TickCount - _lastInput);
            bool away = idle >= (uint)IdleTimeoutMs;
            if (away && !(CountMedia && now - _lastSoundAt < MediaGraceMs)) return new Status("idle", null, null, extAgo);

            string site = FrontInstance(now) is { SiteId: >= 0 } front ? front.Domain : null;
            if (site == null && _exe != null && _browsers.TryGetValue(_exe, out var cur) && cur.SiteId >= 0 && now - cur.At < BrowserStaleMs)
                site = cur.Domain;
            return new Status(away ? "media" : "active", _appId >= 0 ? _appName : null, site, extAgo);
        }
    }
}
