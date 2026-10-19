using System.Text.Json;

namespace ScreenTime;

/// <summary>
/// Browser trackers: every browser installation/profile running the extension is a "browser"
/// (identified by the random id its extension keeps), and browsers can be put into user-defined
/// groups such as "Work" and "Personal".
///
/// Time per browser is recorded by the tracker (each second goes to exactly one browser: the one in
/// front), so group totals never overlap. "All Time" is the sum over the browsers that are included:
/// a browser counts if it's included itself and its group (or "Unassigned") is included. Excluding only
/// affects that combined total; excluded browsers and groups keep tracking their own time.
/// </summary>
internal sealed partial class Db
{
    public const string LegacyInstance = "legacy";

    private sealed class BrowserItem
    {
        public int Id, GroupId;
        public string Instance, Name, Exe;
        public bool Include = true, CustomName;
        public long FirstSeen, LastSeen, LastSeenSaved;
    }
    private sealed class GroupItem { public int Id; public string Name; public bool Include = true; }

    private readonly Dictionary<string, BrowserItem> _browsersByInstance = new(StringComparer.Ordinal);
    private readonly Dictionary<int, BrowserItem> _browsersById = new();
    private readonly Dictionary<int, GroupItem> _groups = new();
    private bool _unassignedInclude = true;
    private Stmt _upBrowser, _upBrowserSite;

    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ---------- schema ----------

    private void MigrateTo4()
    {
        _s.Exec("BEGIN");
        try
        {
            _s.Exec("CREATE TABLE IF NOT EXISTS tracker_groups(id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE, include_total INTEGER NOT NULL DEFAULT 1)");
            _s.Exec("CREATE TABLE IF NOT EXISTS browsers(id INTEGER PRIMARY KEY, instance TEXT NOT NULL UNIQUE, name TEXT NOT NULL, custom_name INTEGER NOT NULL DEFAULT 0, " +
                    "exe TEXT, group_id INTEGER, include_total INTEGER NOT NULL DEFAULT 1, first_seen INTEGER, last_seen INTEGER)");
            _s.Exec("CREATE TABLE IF NOT EXISTS browser_usage(day INTEGER NOT NULL, hour INTEGER NOT NULL, browser_id INTEGER NOT NULL, ms INTEGER NOT NULL, PRIMARY KEY(day, hour, browser_id)) WITHOUT ROWID");
            _s.Exec("CREATE TABLE IF NOT EXISTS browser_site_usage(day INTEGER NOT NULL, hour INTEGER NOT NULL, browser_id INTEGER NOT NULL, site_id INTEGER NOT NULL, ms INTEGER NOT NULL, " +
                    "PRIMARY KEY(day, hour, browser_id, site_id)) WITHOUT ROWID");

            // Existing history can't be split by browser profile after the fact, so it goes to one "earlier
            // browsing" entry (browser apps' time, and website time). The user can group or exclude it like any browser.
            var browserApps = new List<long>();
            using (var q = _s.Prepare("SELECT id, exe FROM apps"))
                while (q.Step()) if (Browsers.IsKnownBrowser(q.Text(1))) browserApps.Add(q.Long(0));
            bool hasSites;
            using (var q = _s.Prepare("SELECT EXISTS(SELECT 1 FROM site_usage)")) { q.Step(); hasSites = q.Long(0) != 0; }
            bool hasApps = false;
            if (browserApps.Count > 0)
                using (var q = _s.Prepare($"SELECT EXISTS(SELECT 1 FROM app_usage WHERE app_id IN ({string.Join(",", browserApps)}))")) { q.Step(); hasApps = q.Long(0) != 0; }
            if (hasApps || hasSites)
            {
                using (var ins = _s.Prepare("INSERT OR IGNORE INTO browsers(instance,name,custom_name,first_seen,last_seen) VALUES(?1,?2,1,?3,?3)"))
                    ins.Bind(1, LegacyInstance).Bind(2, "Earlier browsing (before trackers)").Bind(3, NowMs).Run();
                long legacy;
                using (var q = _s.Prepare("SELECT id FROM browsers WHERE instance=?1")) { q.Bind(1, LegacyInstance); q.Step(); legacy = q.Long(0); }
                if (hasApps)
                    using (var q = _s.Prepare($"INSERT INTO browser_usage(day,hour,browser_id,ms) SELECT day, hour, ?1, SUM(ms) FROM app_usage WHERE app_id IN ({string.Join(",", browserApps)}) GROUP BY day, hour"))
                        q.Bind(1, legacy).Run();
                if (hasSites)
                    using (var q = _s.Prepare("INSERT INTO browser_site_usage(day,hour,browser_id,site_id,ms) SELECT day, hour, ?1, site_id, ms FROM site_usage"))
                        q.Bind(1, legacy).Run();
            }
            _s.Exec("PRAGMA user_version=4");
            _s.Exec("COMMIT");
        }
        catch
        {
            try { _s.Exec("ROLLBACK"); } catch { }
            throw;
        }
    }

    private void LoadTrackers()
    {
        _upBrowser = _s.Prepare("INSERT INTO browser_usage(day,hour,browser_id,ms) VALUES(?1,?2,?3,?4) ON CONFLICT(day,hour,browser_id) DO UPDATE SET ms = ms + excluded.ms");
        _upBrowserSite = _s.Prepare("INSERT INTO browser_site_usage(day,hour,browser_id,site_id,ms) VALUES(?1,?2,?3,?5,?4) ON CONFLICT(day,hour,browser_id,site_id) DO UPDATE SET ms = ms + excluded.ms");
        using (var q = _s.Prepare("SELECT id, name, include_total FROM tracker_groups"))
            while (q.Step()) _groups[(int)q.Long(0)] = new GroupItem { Id = (int)q.Long(0), Name = q.Text(1), Include = q.Long(2) != 0 };
        using (var q = _s.Prepare("SELECT id, instance, name, custom_name, exe, group_id, include_total, first_seen, last_seen FROM browsers"))
            while (q.Step())
            {
                var b = new BrowserItem
                {
                    Id = (int)q.Long(0), Instance = q.Text(1), Name = q.Text(2), CustomName = q.Long(3) != 0, Exe = q.Text(4),
                    GroupId = (int)q.Long(5), Include = q.Long(6) != 0, FirstSeen = q.Long(7), LastSeen = q.Long(8),
                };
                if (b.GroupId > 0 && !_groups.ContainsKey(b.GroupId)) b.GroupId = 0;
                b.LastSeenSaved = b.LastSeen;
                _browsersByInstance[b.Instance] = b;
                _browsersById[b.Id] = b;
            }
        _unassignedInclude = GetMetaLocked("unassigned_include") != "0";
    }

    private string GetMetaLocked(string k)
    {
        using var q = _s.Prepare("SELECT v FROM meta WHERE k=?1");
        q.Bind(1, k);
        return q.Step() ? q.Text(0) : null;
    }

    private Stmt TrackerStatement(Bucket kind) => kind == Bucket.Browser ? _upBrowser : _upBrowserSite;

    // ---------- browsers ----------

    private static string BrowserDisplayName(string exe) => exe switch
    {
        null or "" => "Browser",
        _ => Browsers.Detect().FirstOrDefault(b => string.Equals(b.Exe, exe, StringComparison.OrdinalIgnoreCase))?.Name
             ?? Path.GetFileNameWithoutExtension(exe) switch { var n when n.Length > 0 => char.ToUpper(n[0]) + n[1..], _ => "Browser" },
    };

    private string NextAutoName(string exe, int skipId)
    {
        string baseName = BrowserDisplayName(exe);
        int n = 1;
        var taken = new HashSet<string>(_browsersById.Values.Where(b => b.Id != skipId).Select(b => b.Name), StringComparer.OrdinalIgnoreCase);
        while (taken.Contains($"{baseName} #{n}")) n++;
        return $"{baseName} #{n}";
    }

    /// <summary>The browser row for an extension install (created the first time it reports). New browsers start unassigned.</summary>
    public int BrowserId(string instance, string exe)
    {
        lock (_lock)
        {
            long now = NowMs;
            if (_browsersByInstance.TryGetValue(instance, out var b))
            {
                bool exeChanged = exe != null && !string.Equals(b.Exe, exe, StringComparison.OrdinalIgnoreCase);
                if (exeChanged)
                {
                    b.Exe = exe;
                    if (!b.CustomName) b.Name = NextAutoName(exe, b.Id);   // e.g. first guessed as Chrome, then recognised as Vivaldi
                }
                b.LastSeen = now;
                if (exeChanged || now - b.LastSeenSaved > 60_000)
                {
                    using var u = _s.Prepare("UPDATE browsers SET exe=?2, name=?3, last_seen=?4 WHERE id=?1");
                    u.Bind(1, b.Id).Bind(2, b.Exe).Bind(3, b.Name).Bind(4, now).Run();
                    b.LastSeenSaved = now;
                }
                return b.Id;
            }
            b = new BrowserItem { Instance = instance, Exe = exe, FirstSeen = now, LastSeen = now, LastSeenSaved = now };
            b.Name = NextAutoName(exe, -1);
            using (var ins = _s.Prepare("INSERT INTO browsers(instance,name,exe,first_seen,last_seen) VALUES(?1,?2,?3,?4,?4)"))
                ins.Bind(1, instance).Bind(2, b.Name).Bind(3, exe).Bind(4, now).Run();
            b.Id = (int)_s.LastInsertId;
            _browsersByInstance[instance] = b;
            _browsersById[b.Id] = b;
            return b.Id;
        }
    }

    /// <summary>The "earlier browsing" entry that imported (and pre-tracker) browser time belongs to.</summary>
    public int LegacyBrowserId()
    {
        lock (_lock)
        {
            if (_browsersByInstance.TryGetValue(LegacyInstance, out var b)) return b.Id;
            long now = NowMs;
            b = new BrowserItem { Instance = LegacyInstance, Name = "Earlier browsing (before trackers)", CustomName = true, FirstSeen = now, LastSeen = now };
            using (var ins = _s.Prepare("INSERT INTO browsers(instance,name,custom_name,first_seen,last_seen) VALUES(?1,?2,1,?3,?3)"))
                ins.Bind(1, LegacyInstance).Bind(2, b.Name).Bind(3, now).Run();
            b.Id = (int)_s.LastInsertId;
            _browsersByInstance[LegacyInstance] = b;
            _browsersById[b.Id] = b;
            return b.Id;
        }
    }

    private bool CountsInAllTime(BrowserItem b) =>
        b.Include && (b.GroupId > 0 && _groups.TryGetValue(b.GroupId, out var g) ? g.Include : _unassignedInclude);

    // ---------- dashboard data ----------

    /// <summary>Trackers for [from, to]: every group, Unassigned, every browser, and the combined "All Time" total.</summary>
    public void WriteTrackers(Utf8JsonWriter w, int from, int to, int activeBrowserId)
    {
        lock (_lock)
        {
            var period = new Dictionary<int, long>();
            var lifetime = new Dictionary<int, long>();
            using (var q = _s.Prepare("SELECT browser_id, SUM(ms) FROM browser_usage WHERE day BETWEEN ?1 AND ?2 GROUP BY browser_id"))
            {
                q.Bind(1, from).Bind(2, to);
                while (q.Step()) period[(int)q.Long(0)] = q.Long(1);
            }
            using (var q = _s.Prepare("SELECT browser_id, SUM(ms) FROM browser_usage GROUP BY browser_id"))
                while (q.Step()) lifetime[(int)q.Long(0)] = q.Long(1);
            long P(BrowserItem b) => period.GetValueOrDefault(b.Id);
            long L(BrowserItem b) => lifetime.GetValueOrDefault(b.Id);

            var browsers = _browsersById.Values.OrderBy(b => b.Instance == LegacyInstance).ThenBy(b => b.FirstSeen).ToList();
            long now = NowMs;

            w.WriteStartObject();
            w.WriteNumber("from", from);
            w.WriteNumber("to", to);

            var included = browsers.Where(CountsInAllTime).ToList();
            w.WriteStartObject("allTime");
            w.WriteNumber("ms", included.Sum(P));
            w.WriteNumber("lifetimeMs", included.Sum(L));
            WriteTopSites(w, from, to, included.Select(b => b.Id).ToList());
            w.WriteEndObject();

            w.WriteStartArray("groups");
            foreach (var g in _groups.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            {
                var members = browsers.Where(b => b.GroupId == g.Id).ToList();
                w.WriteStartObject();
                w.WriteNumber("id", g.Id);
                w.WriteString("name", g.Name);
                w.WriteBoolean("include", g.Include);
                w.WriteNumber("ms", members.Sum(P));
                w.WriteNumber("lifetimeMs", members.Sum(L));
                WriteTopSites(w, from, to, members.Select(b => b.Id).ToList());
                w.WriteEndObject();
            }
            w.WriteEndArray();

            var unassigned = browsers.Where(b => b.GroupId <= 0).ToList();
            w.WriteStartObject("unassigned");
            w.WriteBoolean("include", _unassignedInclude);
            w.WriteNumber("ms", unassigned.Sum(P));
            w.WriteNumber("lifetimeMs", unassigned.Sum(L));
            WriteTopSites(w, from, to, unassigned.Select(b => b.Id).ToList());
            w.WriteEndObject();

            w.WriteStartArray("browsers");
            foreach (var b in browsers)
            {
                w.WriteStartObject();
                w.WriteNumber("id", b.Id);
                w.WriteString("name", b.Name);
                if (b.Exe != null) w.WriteString("exe", b.Exe);
                w.WriteNumber("groupId", b.GroupId);
                w.WriteBoolean("include", b.Include);
                w.WriteBoolean("countsInAllTime", CountsInAllTime(b));
                w.WriteBoolean("legacy", b.Instance == LegacyInstance);
                w.WriteBoolean("active", b.Id == activeBrowserId);
                w.WriteNumber("lastSeenSecondsAgo", Math.Max(0, (now - b.LastSeen) / 1000));
                w.WriteNumber("ms", P(b));
                w.WriteNumber("lifetimeMs", L(b));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
    }

    private void WriteTopSites(Utf8JsonWriter w, int from, int to, List<int> browserIds)
    {
        w.WriteStartArray("topSites");
        if (browserIds.Count > 0)
        {
            using var q = _s.Prepare($"SELECT site_id, SUM(ms) s FROM browser_site_usage WHERE day BETWEEN ?1 AND ?2 AND browser_id IN ({string.Join(",", browserIds)}) GROUP BY site_id ORDER BY s DESC LIMIT 12");
            q.Bind(1, from).Bind(2, to);
            var folded = new Dictionary<int, long>();
            while (q.Step())
            {
                int c = Canonical(_sites, (int)q.Long(0));
                if (_sites.TryGetValue(c, out var s) && s.Hidden) continue;
                folded[c] = folded.GetValueOrDefault(c) + q.Long(1);
            }
            foreach (var (id, ms) in folded.OrderByDescending(kv => kv.Value).Take(3))
            {
                w.WriteStartObject();
                w.WriteString("domain", _sites.TryGetValue(id, out var s) ? s.Display : "?");
                w.WriteNumber("ms", ms);
                w.WriteEndObject();
            }
        }
        w.WriteEndArray();
    }

    // ---------- changes ----------

    /// <summary>Group and browser changes from the dashboard. Returns an error message or null.</summary>
    public string TrackerAction(JsonElement r)
    {
        string action = r.GetProperty("action").GetString();
        string Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;
        int Int(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        bool Bool(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

        lock (_lock)
        {
            switch (action)
            {
                case "createGroup":
                {
                    string name = Str("name");
                    if (string.IsNullOrEmpty(name)) return "Give the group a name.";
                    if (name.Length > 40) name = name[..40];
                    if (_groups.Values.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } dup) return $"There's already a group called “{dup.Name}”.";
                    using (var q = _s.Prepare("INSERT INTO tracker_groups(name) VALUES(?1)")) q.Bind(1, name).Run();
                    int id = (int)_s.LastInsertId;
                    _groups[id] = new GroupItem { Id = id, Name = name };
                    return null;
                }
                case "renameGroup":
                {
                    if (!_groups.TryGetValue(Int("id"), out var g)) return "That group doesn't exist anymore.";
                    string name = Str("name");
                    if (string.IsNullOrEmpty(name)) return "Give the group a name.";
                    if (name.Length > 40) name = name[..40];
                    if (_groups.Values.Any(o => o.Id != g.Id && o.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return $"There's already a group called “{name}”.";
                    using (var q = _s.Prepare("UPDATE tracker_groups SET name=?2 WHERE id=?1")) q.Bind(1, g.Id).Bind(2, name).Run();
                    g.Name = name;
                    return null;
                }
                case "deleteGroup":
                {
                    if (!_groups.TryGetValue(Int("id"), out var g)) return "That group doesn't exist anymore.";
                    int moveTo = Int("moveTo");
                    if (moveTo == g.Id || moveTo > 0 && !_groups.ContainsKey(moveTo)) moveTo = 0;   // 0 = Unassigned
                    _s.Exec("BEGIN");
                    try
                    {
                        using (var q = _s.Prepare("UPDATE browsers SET group_id=?2 WHERE group_id=?1")) q.Bind(1, g.Id).Bind(2, moveTo > 0 ? moveTo : 0).Run();
                        using (var q = _s.Prepare("DELETE FROM tracker_groups WHERE id=?1")) q.Bind(1, g.Id).Run();
                        _s.Exec("COMMIT");
                    }
                    catch { try { _s.Exec("ROLLBACK"); } catch { } throw; }
                    foreach (var b in _browsersById.Values) if (b.GroupId == g.Id) b.GroupId = moveTo;
                    _groups.Remove(g.Id);
                    return null;
                }
                case "setGroupInclude":
                {
                    if (!_groups.TryGetValue(Int("id"), out var g)) return "That group doesn't exist anymore.";
                    g.Include = Bool("include");
                    using (var q = _s.Prepare("UPDATE tracker_groups SET include_total=?2 WHERE id=?1")) q.Bind(1, g.Id).Bind(2, g.Include ? 1 : 0).Run();
                    return null;
                }
                case "setUnassignedInclude":
                {
                    _unassignedInclude = Bool("include");
                    using (var q = _s.Prepare("INSERT INTO meta(k,v) VALUES('unassigned_include',?1) ON CONFLICT(k) DO UPDATE SET v=excluded.v"))
                        q.Bind(1, _unassignedInclude ? "1" : "0").Run();
                    return null;
                }
                case "assign":
                {
                    if (!_browsersById.TryGetValue(Int("browserId"), out var b)) return "That browser doesn't exist.";
                    return Assign(b, Int("groupId"));
                }
                case "setBrowserInclude":
                {
                    if (!_browsersById.TryGetValue(Int("browserId"), out var b)) return "That browser doesn't exist.";
                    b.Include = Bool("include");
                    using (var q = _s.Prepare("UPDATE browsers SET include_total=?2 WHERE id=?1")) q.Bind(1, b.Id).Bind(2, b.Include ? 1 : 0).Run();
                    return null;
                }
                case "renameBrowser":
                {
                    if (!_browsersById.TryGetValue(Int("browserId"), out var b)) return "That browser doesn't exist.";
                    return Rename(b, Str("name"));
                }
            }
            return "Unknown action";
        }
    }

    private string Assign(BrowserItem b, int groupId)
    {
        if (groupId > 0 && !_groups.ContainsKey(groupId)) return "That group doesn't exist anymore.";
        b.GroupId = Math.Max(0, groupId);
        using var q = _s.Prepare("UPDATE browsers SET group_id=?2 WHERE id=?1");
        q.Bind(1, b.Id).Bind(2, b.GroupId).Run();
        return null;
    }

    private string Rename(BrowserItem b, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            b.CustomName = false;
            b.Name = b.Instance == LegacyInstance ? "Earlier browsing (before trackers)" : NextAutoName(b.Exe, b.Id);
        }
        else
        {
            b.CustomName = true;
            b.Name = name.Length > 40 ? name[..40] : name;
        }
        using var q = _s.Prepare("UPDATE browsers SET name=?2, custom_name=?3 WHERE id=?1");
        q.Bind(1, b.Id).Bind(2, b.Name).Bind(3, b.CustomName ? 1 : 0).Run();
        return null;
    }

    // ---------- the extension's own view (popup) ----------

    /// <summary>What the extension popup shows about the browser it runs in.</summary>
    public bool WriteBrowserForInstance(Utf8JsonWriter w, string instance, int today)
    {
        lock (_lock)
        {
            if (!_browsersByInstance.TryGetValue(instance, out var b)) return false;
            long Sum(IEnumerable<int> ids)
            {
                var list = ids.ToList();
                if (list.Count == 0) return 0;
                using var q = _s.Prepare($"SELECT COALESCE(SUM(ms),0) FROM browser_usage WHERE day=?1 AND browser_id IN ({string.Join(",", list)})");
                q.Bind(1, today);
                q.Step();
                return q.Long(0);
            }
            w.WriteStartObject();
            w.WriteNumber("id", b.Id);
            w.WriteString("name", b.Name);
            w.WriteNumber("groupId", b.GroupId);
            w.WriteString("groupName", b.GroupId > 0 && _groups.TryGetValue(b.GroupId, out var g) ? g.Name : "Unassigned");
            w.WriteNumber("todayMs", Sum([b.Id]));
            w.WriteNumber("groupTodayMs", Sum(_browsersById.Values.Where(o => o.GroupId == b.GroupId).Select(o => o.Id)));
            w.WriteStartArray("groups");
            foreach (var grp in _groups.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                w.WriteStartObject();
                w.WriteNumber("id", grp.Id);
                w.WriteString("name", grp.Name);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            return true;
        }
    }

    /// <summary>Changes made from the extension popup (assign this browser to a group / rename it).</summary>
    public string BrowserSelfAction(string instance, int? groupId, string name)
    {
        lock (_lock)
        {
            if (!_browsersByInstance.TryGetValue(instance, out var b)) return "This browser hasn't reported to Screen Time yet.";
            if (groupId.HasValue) { string err = Assign(b, groupId.Value); if (err != null) return err; }
            if (name != null) return Rename(b, name);
            return null;
        }
    }
}
