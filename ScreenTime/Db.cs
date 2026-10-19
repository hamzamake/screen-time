using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenTime;

internal enum Bucket : byte { App, Site, AppOpen, SiteOpen, Browser, BrowserSite }

/// <summary>
/// One aggregation bucket for one local hour of one local day: usage or open time of an app or a site,
/// or usage of one browser installation/profile (BrowserSite: <see cref="Sub"/> is the site id).
/// </summary>
internal readonly record struct Key(int Day, int Hour, Bucket Kind, int Id, int Sub = 0);

internal readonly record struct LimitStatus(string Kind, int Target, string Name, int Minutes, long UsedMs, int NotifiedDay);

/// <summary>
/// Storage. Usage is kept as per-hour totals (milliseconds) per app and per website, so the
/// database stays tiny forever (a year of heavy use is a few MB) while still supporting
/// hourly charts for any day. Nothing is ever deleted: renames, merges and hiding are just
/// labels on top of the raw data and can always be undone.
///
/// App identity: an app is identified by its install location with version numbers taken out
/// (see <see cref="AppKey"/>), not by its exe name. So two different programs that both happen
/// to be called "launcher.exe" stay separate, while Discord (which moves to a new
/// "app-1.0.xxxx" folder on every update) stays one app.
/// </summary>
internal sealed partial class Db : IDisposable
{
    private const int SchemaVersion = 4;

    private readonly object _lock = new();
    private readonly SqliteDb _s;
    private readonly Stmt _upApp, _upSite, _upAppOpen, _upSiteOpen, _insApp, _insSite, _setIcon, _setPath;

    private sealed class Item
    {
        public int Id, MergedInto;
        public string Key, Exe, Name, Path, Alias, Icon;
        public bool Hidden;
        public string Display => Alias ?? Name;
    }

    private readonly Dictionary<string, Item> _appsByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Item> _apps = new();
    private readonly Dictionary<string, Item> _sitesByDomain = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Item> _sites = new();

    public Db(string path)
    {
        _s = new SqliteDb(path);
        _s.Exec("PRAGMA journal_mode=WAL");
        _s.Exec("PRAGMA synchronous=NORMAL");
        _s.Exec("PRAGMA temp_store=MEMORY");
        Migrate();

        _upApp = _s.Prepare("INSERT INTO app_usage(day,hour,app_id,ms) VALUES(?1,?2,?3,?4) ON CONFLICT(day,hour,app_id) DO UPDATE SET ms = ms + excluded.ms");
        _upSite = _s.Prepare("INSERT INTO site_usage(day,hour,site_id,ms) VALUES(?1,?2,?3,?4) ON CONFLICT(day,hour,site_id) DO UPDATE SET ms = ms + excluded.ms");
        _upAppOpen = _s.Prepare("INSERT INTO app_open(day,hour,app_id,ms) VALUES(?1,?2,?3,?4) ON CONFLICT(day,hour,app_id) DO UPDATE SET ms = ms + excluded.ms");
        _upSiteOpen = _s.Prepare("INSERT INTO site_open(day,hour,site_id,ms) VALUES(?1,?2,?3,?4) ON CONFLICT(day,hour,site_id) DO UPDATE SET ms = ms + excluded.ms");
        _insApp = _s.Prepare("INSERT INTO apps(key,exe,name,path) VALUES(?1,?2,?3,?4)");
        _insSite = _s.Prepare("INSERT INTO sites(domain,icon) VALUES(?1,?2)");
        _setIcon = _s.Prepare("UPDATE sites SET icon=?2 WHERE id=?1");
        _setPath = _s.Prepare("UPDATE apps SET path=?2 WHERE id=?1");

        using (var q = _s.Prepare("SELECT id, key, exe, name, path, alias, merged_into, hidden FROM apps"))
            while (q.Step())
            {
                var a = new Item { Id = (int)q.Long(0), Key = q.Text(1), Exe = q.Text(2), Name = q.Text(3), Path = q.Text(4),
                                   Alias = q.Text(5), MergedInto = (int)q.Long(6), Hidden = q.Long(7) != 0 };
                _apps[a.Id] = a; _appsByKey[a.Key] = a;
            }
        foreach (var a in _apps.Values.Where(a => IsGenericName(a.Name)).ToList())
        {
            a.Name = FriendlyName(a.Exe, null);
            using var u = _s.Prepare("UPDATE apps SET name=?2 WHERE id=?1");
            u.Bind(1, a.Id).Bind(2, a.Name).Run();
        }
        using (var q = _s.Prepare("SELECT id, domain, icon, alias, merged_into, hidden FROM sites"))
            while (q.Step())
            {
                var s = new Item { Id = (int)q.Long(0), Key = q.Text(1), Name = q.Text(1), Icon = q.Text(2),
                                   Alias = q.Text(3), MergedInto = (int)q.Long(4), Hidden = q.Long(5) != 0 };
                _sites[s.Id] = s; _sitesByDomain[s.Key] = s;
            }

        LoadTrackers();
        if (GetMeta("created") == null) SetMeta("created", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
    }

    private void Migrate()
    {
        long version;
        using (var q = _s.Prepare("PRAGMA user_version")) { q.Step(); version = q.Long(0); }
        if (version >= SchemaVersion) return;
        if (version < 2) MigrateTo2();
        // v3: open time (how long apps / sites were open, next to how long they were used)
        _s.Exec("CREATE TABLE IF NOT EXISTS app_open(day INTEGER NOT NULL, hour INTEGER NOT NULL, app_id INTEGER NOT NULL, ms INTEGER NOT NULL, PRIMARY KEY(day, hour, app_id)) WITHOUT ROWID");
        _s.Exec("CREATE TABLE IF NOT EXISTS site_open(day INTEGER NOT NULL, hour INTEGER NOT NULL, site_id INTEGER NOT NULL, ms INTEGER NOT NULL, PRIMARY KEY(day, hour, site_id)) WITHOUT ROWID");
        _s.Exec("PRAGMA user_version=3");
        // v4: browser installations/profiles and tracker groups (see DbTrackers.cs)
        if (version < 4) MigrateTo4();
    }

    private void MigrateTo2()
    {
        bool hadApps;
        using (var q = _s.Prepare("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='apps'")) { q.Step(); hadApps = q.Long(0) > 0; }

        _s.Exec("BEGIN");
        try
        {
            const string appsTable = "(id INTEGER PRIMARY KEY, key TEXT NOT NULL UNIQUE, exe TEXT NOT NULL, name TEXT NOT NULL, path TEXT, alias TEXT, merged_into INTEGER, hidden INTEGER NOT NULL DEFAULT 0)";
            if (hadApps)
            {
                // v1 identified apps by exe name only. Rebuild with path-based keys, keeping every id so usage rows stay attached.
                _s.Exec("CREATE TABLE apps_v2" + appsTable);
                var seen = new Dictionary<string, long>(StringComparer.Ordinal);
                using (var old = _s.Prepare("SELECT id, exe, name, path FROM apps ORDER BY id"))
                using (var ins = _s.Prepare("INSERT INTO apps_v2(id,key,exe,name,path,merged_into) VALUES(?1,?2,?3,?4,?5,?6)"))
                    while (old.Step())
                    {
                        long id = old.Long(0);
                        string exe = old.Text(1), key = AppKey(old.Text(3), exe);
                        long mergeInto = 0;
                        if (seen.TryGetValue(key, out long first)) { mergeInto = first; key += "#" + id; }
                        else seen[key] = id;
                        ins.Bind(1, id).Bind(2, key).Bind(3, exe).Bind(4, old.Text(2)).Bind(5, old.Text(3));
                        if (mergeInto > 0) ins.Bind(6, mergeInto); else ins.Bind(6, (string)null);
                        ins.Run();
                    }
                _s.Exec("DROP TABLE apps");
                _s.Exec("ALTER TABLE apps_v2 RENAME TO apps");
                _s.Exec("ALTER TABLE sites ADD COLUMN alias TEXT");
                _s.Exec("ALTER TABLE sites ADD COLUMN merged_into INTEGER");
                _s.Exec("ALTER TABLE sites ADD COLUMN hidden INTEGER NOT NULL DEFAULT 0");
            }
            else
            {
                _s.Exec("CREATE TABLE apps" + appsTable);
                _s.Exec("CREATE TABLE sites(id INTEGER PRIMARY KEY, domain TEXT NOT NULL UNIQUE, icon TEXT, alias TEXT, merged_into INTEGER, hidden INTEGER NOT NULL DEFAULT 0)");
            }
            _s.Exec("CREATE TABLE IF NOT EXISTS app_usage(day INTEGER NOT NULL, hour INTEGER NOT NULL, app_id INTEGER NOT NULL, ms INTEGER NOT NULL, PRIMARY KEY(day, hour, app_id)) WITHOUT ROWID");
            _s.Exec("CREATE TABLE IF NOT EXISTS site_usage(day INTEGER NOT NULL, hour INTEGER NOT NULL, site_id INTEGER NOT NULL, ms INTEGER NOT NULL, PRIMARY KEY(day, hour, site_id)) WITHOUT ROWID");
            _s.Exec("CREATE TABLE IF NOT EXISTS meta(k TEXT PRIMARY KEY, v TEXT) WITHOUT ROWID");
            _s.Exec("CREATE TABLE IF NOT EXISTS limits(kind TEXT NOT NULL, target INTEGER NOT NULL, minutes INTEGER NOT NULL, notified_day INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(kind, target)) WITHOUT ROWID");
            // Time ranges whose usage is already in the database (Screen Time running, or an import).
            // Imports skip these ranges so nothing is ever counted twice.
            _s.Exec("CREATE TABLE IF NOT EXISTS coverage(source TEXT NOT NULL, start INTEGER NOT NULL, end INTEGER NOT NULL)");
            if (hadApps)
            {
                // Screen Time v1 didn't log its running time; assume it covered everything since it was first started.
                string created = null;
                using (var q = _s.Prepare("SELECT v FROM meta WHERE k='created'")) if (q.Step()) created = q.Text(0);
                if (DateTime.TryParse(created, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var c))
                    using (var q = _s.Prepare("INSERT INTO coverage(source,start,end) VALUES('screentime',?1,?2)"))
                        q.Bind(1, new DateTimeOffset(c).ToUnixTimeMilliseconds()).Bind(2, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Run();
            }
            _s.Exec("PRAGMA user_version=2");
            _s.Exec("COMMIT");
        }
        catch
        {
            try { _s.Exec("ROLLBACK"); } catch { }
            throw;
        }
    }

    public static int DayOf(DateTime t) => t.Year * 10000 + t.Month * 100 + t.Day;
    private static DateTime DateOf(int day) => new(day / 10000, day / 100 % 100, day % 100);

    // ---------- app identity ----------

    [GeneratedRegex(@"\{?[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}?")]
    private static partial Regex GuidRx();

    [GeneratedRegex(@"\d+(?:\.\d+)+")]
    private static partial Regex VersionRx();

    /// <summary>
    /// Stable identity for an executable: its full path, lower-cased, with version numbers and GUIDs
    /// replaced by "*". "...\Discord\app-1.0.9156\Discord.exe" becomes "...\discord\app-*\discord.exe".
    /// Programs run from the temp folder (installers) are grouped by file name.
    /// </summary>
    public static string AppKey(string path, string exe)
    {
        if (string.IsNullOrEmpty(path)) return "exe:" + exe.ToLowerInvariant();
        string p = path.ToLowerInvariant();
        int temp = p.IndexOf(@"\appdata\local\temp\", StringComparison.Ordinal);
        if (temp >= 0 || p.StartsWith(@"c:\windows\temp\", StringComparison.Ordinal))
            return "temp:" + VersionRx().Replace(System.IO.Path.GetFileName(p), "*");
        return VersionRx().Replace(GuidRx().Replace(p, "*"), "*");
    }

    private static readonly HashSet<string> GenericFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "bin64", "bin32", "x64", "x86", "win64", "win32", "amd64", "binaries", "application", "app", "current",
        "release", "build", "dist", "program files", "program files (x86)", "programs", "local", "roaming", "appdata",
        "windows", "system32", "syswow64", "users", "common", "client", "launcher", "game", "*",
    };

    /// <summary>A short "where is it from" label for telling same-named apps apart, e.g. "Epic Games".</summary>
    private static string FolderHint(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var dirs = System.IO.Path.GetDirectoryName(path)?.Split('\\') ?? [];
        for (int i = dirs.Length - 1; i > 0; i--)
        {
            string d = VersionRx().Replace(dirs[i], "*").Trim();
            if (d.Length == 0 || GenericFolders.Contains(d) || d.StartsWith("app-", StringComparison.OrdinalIgnoreCase)) continue;
            return dirs[i];
        }
        return dirs.Length > 0 ? dirs[0] : null;
    }

    public int AppId(string exe, string path)
    {
        lock (_lock)
        {
            string key = AppKey(path, exe);
            if (_appsByKey.TryGetValue(key, out var a))
            {
                if (path != null && !string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    a.Path = path;   // e.g. Discord updated into a new folder: keep the icon pointing at the live exe
                    _setPath.Bind(1, a.Id).Bind(2, path).Run();
                }
                return a.Id;
            }
            a = new Item { Key = key, Exe = exe, Name = FriendlyName(exe, path), Path = path };
            _insApp.Bind(1, key).Bind(2, exe).Bind(3, a.Name).Bind(4, path).Run();
            a.Id = (int)_s.LastInsertId;

            // An exe-only entry (from an import, or when the path couldn't be read) is this same app,
            // unless another app with this exe name already exists: then it's ambiguous and left alone.
            if (path != null && _appsByKey.TryGetValue("exe:" + exe.ToLowerInvariant(), out var orphan) && orphan.MergedInto <= 0
                && !_apps.Values.Any(o => o.Path != null && string.Equals(o.Exe, exe, StringComparison.OrdinalIgnoreCase)))
            {
                using var q = _s.Prepare("UPDATE apps SET merged_into=?2 WHERE id=?1");
                q.Bind(1, orphan.Id).Bind(2, a.Id).Run();
                orphan.MergedInto = a.Id;
            }

            _apps[a.Id] = a; _appsByKey[key] = a;
            return a.Id;
        }
    }

    /// <summary>Display name of the app this id is shown as (follows merges and renames).</summary>
    public string AppName(int id) { lock (_lock) return _apps.TryGetValue(Canonical(_apps, id), out var a) ? a.Display : "?"; }

    public string AppPath(int id) { lock (_lock) return _apps.TryGetValue(id, out var a) ? a.Path : null; }

    public int SiteId(string domain, string icon)
    {
        lock (_lock)
        {
            if (_sitesByDomain.TryGetValue(domain, out var s))
            {
                if (icon != null && icon != s.Icon)
                {
                    s.Icon = icon;
                    _setIcon.Bind(1, s.Id).Bind(2, icon).Run();
                }
                return s.Id;
            }
            s = new Item { Key = domain, Name = domain, Icon = icon };
            _insSite.Bind(1, domain).Bind(2, icon).Run();
            s.Id = (int)_s.LastInsertId;
            _sites[s.Id] = s; _sitesByDomain[domain] = s;
            return s.Id;
        }
    }

    private static string FriendlyName(string exe, string path)
    {
        string name = null;
        if (path != null)
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                name = vi.FileDescription?.Trim();
                if (string.IsNullOrEmpty(name) || name.Length > 64) name = vi.ProductName?.Trim();
            }
            catch { }
        }
        if (IsGenericName(name)) name = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;
        return name;
    }

    private static bool IsGenericName(string name) =>
        string.IsNullOrEmpty(name) || name.Length > 64 || name.Contains("Operating System", StringComparison.OrdinalIgnoreCase);

    // ---------- merge / hide helpers (all in memory; callers hold _lock) ----------

    private static int Canonical(Dictionary<int, Item> items, int id)
    {
        for (int guard = 0; guard < 16 && items.TryGetValue(id, out var it) && it.MergedInto > 0 && items.ContainsKey(it.MergedInto); guard++)
            id = it.MergedInto;
        return id;
    }

    private static List<int> Group(Dictionary<int, Item> items, int canonical)
    {
        var ids = new List<int>();
        foreach (var id in items.Keys) if (Canonical(items, id) == canonical) ids.Add(id);
        if (ids.Count == 0) ids.Add(canonical);
        return ids;
    }

    private static string NotHidden(Dictionary<int, Item> items, string col)
    {
        var ids = new List<int>();
        foreach (var id in items.Keys) if (items.TryGetValue(Canonical(items, id), out var c) && c.Hidden) ids.Add(id);
        return ids.Count == 0 ? "" : $" AND {col} NOT IN ({string.Join(",", ids)})";
    }

    private static string InGroup(Dictionary<int, Item> items, string col, int id) =>
        $" AND {col} IN ({string.Join(",", Group(items, Canonical(items, id)))})";

    /// <summary>Usage (and open time) per displayed item, merged groups folded together, most used first.</summary>
    private List<(Item Item, long Ms, long OpenMs)> Folded(bool site, int from, int to, bool showHidden, bool withOpen = false)
    {
        var items = site ? _sites : _apps;
        var sums = new Dictionary<int, (long Use, long Open)>();
        void Sum(string table, string col, bool open)
        {
            using var q = _s.Prepare($"SELECT {col}, SUM(ms) FROM {table} WHERE day BETWEEN ?1 AND ?2 GROUP BY {col}");
            q.Bind(1, from).Bind(2, to);
            while (q.Step())
            {
                int c = Canonical(items, (int)q.Long(0));
                sums.TryGetValue(c, out var v);
                sums[c] = open ? (v.Use, v.Open + q.Long(1)) : (v.Use + q.Long(1), v.Open);
            }
        }
        Sum(site ? "site_usage" : "app_usage", site ? "site_id" : "app_id", false);
        if (withOpen) Sum(site ? "site_open" : "app_open", site ? "site_id" : "app_id", true);

        var list = new List<(Item, long, long)>(sums.Count);
        foreach (var (id, v) in sums)
        {
            if (!items.TryGetValue(id, out var it)) it = new Item { Id = id, Name = "?", Key = "?" };
            if (it.Hidden && !showHidden) continue;
            list.Add((it, v.Use, v.Open));
        }
        list.Sort((a, b) => a.Item2 != b.Item2 ? b.Item2.CompareTo(a.Item2) : b.Item3.CompareTo(a.Item3));
        return list;
    }

    // ---------- usage ----------

    private long _sessionRow;

    /// <summary>Marks the start of a Screen Time run; its end moves forward with every save.</summary>
    public void StartSession()
    {
        lock (_lock)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var q = _s.Prepare("INSERT INTO coverage(source,start,end) VALUES('screentime',?1,?1)");
            q.Bind(1, now).Run();
            _sessionRow = _s.LastInsertId;
        }
    }

    public void WriteUsage(Dictionary<Key, long> buckets) => WriteUsage(buckets, true);

    private void WriteUsage(Dictionary<Key, long> buckets, bool extendSession)
    {
        lock (_lock)
        {
            _s.Exec("BEGIN");
            try
            {
                foreach (var (k, ms) in buckets)
                {
                    if (ms <= 0) continue;
                    var stmt = k.Kind switch
                    {
                        Bucket.App => _upApp, Bucket.Site => _upSite, Bucket.AppOpen => _upAppOpen, Bucket.SiteOpen => _upSiteOpen,
                        _ => TrackerStatement(k.Kind),
                    };
                    stmt.Bind(1, k.Day).Bind(2, k.Hour).Bind(3, k.Id).Bind(4, ms);
                    if (k.Kind == Bucket.BrowserSite) stmt.Bind(5, k.Sub);
                    stmt.Run();
                }
                if (extendSession && _sessionRow > 0)
                    using (var q = _s.Prepare("UPDATE coverage SET end=?2 WHERE rowid=?1"))
                        q.Bind(1, _sessionRow).Bind(2, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Run();
                _s.Exec("COMMIT");
            }
            catch
            {
                try { _s.Exec("ROLLBACK"); } catch { }
                throw;
            }
        }
    }

    // ---------- import support ----------

    /// <summary>All covered ranges (unix ms), merged and sorted. The current run counts as covered up to now.</summary>
    public List<(long Start, long End)> Coverage()
    {
        var list = new List<(long, long)>();
        lock (_lock)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var q = _s.Prepare("SELECT rowid, start, end FROM coverage ORDER BY start");
            while (q.Step()) list.Add((q.Long(1), q.Long(0) == _sessionRow ? now : q.Long(2)));
        }
        list.Sort();
        var merged = new List<(long Start, long End)>();
        foreach (var (s, e) in list)
        {
            if (merged.Count > 0 && s <= merged[^1].End) merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, e));
            else merged.Add((s, e));
        }
        return merged;
    }

    /// <summary>Adds imported usage (on top of what's there) and records the imported range, in one transaction.</summary>
    public void WriteImport(Dictionary<Key, long> buckets, string source, long start, long end)
    {
        lock (_lock)
        {
            WriteUsage(buckets, false);
            using var q = _s.Prepare("INSERT INTO coverage(source,start,end) VALUES(?1,?2,?3)");
            q.Bind(1, source).Bind(2, start).Bind(3, end).Run();
        }
    }

    /// <summary>
    /// App id for an exe name from another tracker (which doesn't record full paths). Uses the app
    /// Screen Time already knows by that exe name if there's exactly one, else the path of a running
    /// process with that name, else an exe-only entry (merged automatically once the real app is seen).
    /// </summary>
    public int ImportAppId(string exe, Func<string, string> runningPath)
    {
        lock (_lock)
        {
            var known = _apps.Values.Where(a => a.Path != null && string.Equals(a.Exe, exe, StringComparison.OrdinalIgnoreCase))
                .Select(a => Canonical(_apps, a.Id)).Distinct().ToList();
            if (known.Count == 1) return known[0];
            if (known.Count > 1) return AppId(exe, null);
            return AppId(exe, runningPath(exe));
        }
    }

    private long Total(int from, int to)
    {
        using var q = _s.Prepare("SELECT COALESCE(SUM(ms),0) FROM app_usage WHERE day BETWEEN ?1 AND ?2" + NotHidden(_apps, "app_id"));
        q.Bind(1, from).Bind(2, to);
        return q.Step() ? q.Long(0) : 0;
    }

    public long DayTotal(int day) { lock (_lock) return Total(day, day); }

    public List<(string Name, long Ms)> TopApps(int day, int n)
    {
        lock (_lock)
        {
            var list = Folded(false, day, day, false);
            return list.Take(n).Select(x => (x.Item.Display, x.Ms)).ToList();
        }
    }

    public int FirstDay()
    {
        lock (_lock)
        {
            using var q = _s.Prepare("SELECT MIN(day) FROM app_usage");
            return q.Step() && q.Text(0) != null ? (int)q.Long(0) : DayOf(DateTime.Now);
        }
    }

    /// <summary>Writes the dashboard summary for [from, to] (yyyymmdd, inclusive) as JSON.</summary>
    public void WriteSummary(Utf8JsonWriter w, int from, int to, int appFilter, int siteFilter, bool showHidden,
                             bool withOpen, Func<string, bool> isBrowser)
    {
        lock (_lock)
        {
            string hiddenApps = showHidden ? "" : NotHidden(_apps, "app_id");
            int span = (int)(DateOf(to) - DateOf(from)).TotalDays + 1;

            w.WriteStartObject();
            w.WriteNumber("from", from);
            w.WriteNumber("to", to);

            using (var q = _s.Prepare("SELECT COALESCE(SUM(ms),0), COUNT(DISTINCT day) FROM app_usage WHERE day BETWEEN ?1 AND ?2" + hiddenApps))
            {
                q.Bind(1, from).Bind(2, to);
                q.Step();
                w.WriteNumber("total", q.Long(0));
                w.WriteNumber("activeDays", q.Long(1));
            }
            using (var q = _s.Prepare("SELECT COALESCE(SUM(ms),0) FROM app_usage WHERE day BETWEEN ?1 AND ?2" + hiddenApps))
            {
                q.Bind(1, DayOf(DateOf(from).AddDays(-span))).Bind(2, DayOf(DateOf(from).AddDays(-1)));
                q.Step();
                w.WriteNumber("prevTotal", q.Long(0));
            }

            int hiddenCount = _apps.Values.Count(a => a.Hidden && a.MergedInto == 0) + _sites.Values.Count(s => s.Hidden && s.MergedInto == 0);
            w.WriteNumber("hiddenCount", hiddenCount);
            w.WriteBoolean("trackOpen", withOpen);

            WriteItems(w, "apps", false, Folded(false, from, to, showHidden, withOpen), isBrowser);
            WriteItems(w, "sites", true, Folded(true, from, to, showHidden, withOpen), isBrowser);

            string table = siteFilter >= 0 ? "site_usage" : "app_usage";
            string clause = siteFilter >= 0 ? InGroup(_sites, "site_id", siteFilter)
                          : appFilter >= 0 ? InGroup(_apps, "app_id", appFilter)
                          : hiddenApps;

            bool hourly = from == to;
            w.WriteString("unit", hourly ? "hour" : "day");
            w.WriteStartArray("series");
            using (var q = _s.Prepare($"SELECT {(hourly ? "hour" : "day")}, SUM(ms) FROM {table} WHERE day BETWEEN ?1 AND ?2{clause} GROUP BY 1"))
            {
                q.Bind(1, from).Bind(2, to);
                while (q.Step())
                {
                    w.WriteStartArray();
                    w.WriteNumberValue(q.Long(0));
                    w.WriteNumberValue(q.Long(1));
                    w.WriteEndArray();
                }
            }
            w.WriteEndArray();

            // Open time of the selected app / site, drawn behind its usage in the chart.
            if (withOpen && (appFilter >= 0 || siteFilter >= 0))
            {
                string openTable = siteFilter >= 0 ? "site_open" : "app_open";
                w.WriteStartArray("openSeries");
                using (var q = _s.Prepare($"SELECT {(hourly ? "hour" : "day")}, SUM(ms) FROM {openTable} WHERE day BETWEEN ?1 AND ?2{clause} GROUP BY 1"))
                {
                    q.Bind(1, from).Bind(2, to);
                    while (q.Step())
                    {
                        w.WriteStartArray();
                        w.WriteNumberValue(q.Long(0));
                        w.WriteNumberValue(q.Long(1));
                        w.WriteEndArray();
                    }
                }
                w.WriteEndArray();
            }

            // Weekly pattern: average time per weekday x hour over the tracked part of the range.
            int today = DayOf(DateTime.Now), first = FirstDayLocked();
            int pFrom = Math.Max(from, first), pTo = Math.Min(to, today);
            if (span >= 7 && pFrom <= pTo)
            {
                var sum = new double[7, 24];
                var dayCount = new int[7];
                for (var d = DateOf(pFrom); d <= DateOf(pTo); d = d.AddDays(1)) dayCount[((int)d.DayOfWeek + 6) % 7]++;
                using (var q = _s.Prepare($"SELECT day, hour, SUM(ms) FROM {table} WHERE day BETWEEN ?1 AND ?2{clause} GROUP BY day, hour"))
                {
                    q.Bind(1, pFrom).Bind(2, pTo);
                    while (q.Step())
                    {
                        int wd = ((int)DateOf((int)q.Long(0)).DayOfWeek + 6) % 7;
                        sum[wd, (int)q.Long(1)] += q.Long(2);
                    }
                }
                w.WriteStartArray("pattern");   // [Mon..Sun][0..23] average ms
                for (int d = 0; d < 7; d++)
                {
                    w.WriteStartArray();
                    for (int h = 0; h < 24; h++) w.WriteNumberValue(dayCount[d] == 0 ? 0 : Math.Round(sum[d, h] / dayCount[d]));
                    w.WriteEndArray();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
    }

    private void WriteItems(Utf8JsonWriter w, string name, bool site, List<(Item Item, long Ms, long OpenMs)> list, Func<string, bool> isBrowser)
    {
        var items = site ? _sites : _apps;
        var dupNames = new HashSet<string>(list.GroupBy(x => x.Item.Display, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key), StringComparer.OrdinalIgnoreCase);
        var children = new Dictionary<int, List<Item>>();
        foreach (var it in items.Values)
        {
            if (it.MergedInto <= 0) continue;
            int c = Canonical(items, it.Id);
            if (!children.TryGetValue(c, out var l)) children[c] = l = new List<Item>();
            l.Add(it);
        }

        w.WriteStartArray(name);
        foreach (var (it, ms, openMs) in list)
        {
            w.WriteStartObject();
            w.WriteNumber("id", it.Id);
            w.WriteNumber("ms", ms);
            if (openMs > 0) w.WriteNumber("openMs", openMs);
            if (site)
            {
                w.WriteString("domain", it.Display);
                if (it.Alias != null) w.WriteString("original", it.Name);
                if (it.Icon != null) w.WriteString("icon", it.Icon);
            }
            else
            {
                w.WriteString("name", it.Display);
                if (it.Alias != null) w.WriteString("original", it.Name);
                w.WriteString("exe", it.Exe ?? "");
                if (it.Path != null) w.WriteString("path", it.Path);
                if (dupNames.Contains(it.Display)) w.WriteString("hint", FolderHint(it.Path) ?? it.Exe);
                if (it.Exe != null && isBrowser(it.Exe)) w.WriteBoolean("browser", true);
            }
            if (it.Hidden) w.WriteBoolean("hidden", true);
            if (children.TryGetValue(it.Id, out var kids))
            {
                w.WriteStartArray("merged");
                foreach (var k in kids)
                {
                    w.WriteStartObject();
                    w.WriteNumber("id", k.Id);
                    w.WriteString("name", site ? k.Name : k.Display + (k.Path != null ? " (" + (FolderHint(k.Path) ?? k.Exe) + ")" : ""));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private int FirstDayLocked()
    {
        using var q = _s.Prepare("SELECT MIN(day) FROM app_usage");
        return q.Step() && q.Text(0) != null ? (int)q.Long(0) : DayOf(DateTime.Now);
    }

    /// <summary>Every app or site that can be shown (not merged into something else), for pickers.</summary>
    public void WriteItemList(Utf8JsonWriter w, bool site)
    {
        lock (_lock)
        {
            var items = (site ? _sites : _apps).Values.Where(i => i.MergedInto <= 0 || !(site ? _sites : _apps).ContainsKey(i.MergedInto))
                .OrderBy(i => i.Display, StringComparer.OrdinalIgnoreCase).ToList();
            var dup = new HashSet<string>(items.GroupBy(i => i.Display, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key),
                StringComparer.OrdinalIgnoreCase);
            w.WriteStartArray();
            foreach (var i in items)
            {
                w.WriteStartObject();
                w.WriteNumber("id", i.Id);
                w.WriteString("name", i.Display);
                if (!site && dup.Contains(i.Display)) w.WriteString("hint", FolderHint(i.Path) ?? i.Exe);
                if (i.Hidden) w.WriteBoolean("hidden", true);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
    }

    /// <summary>Rename / hide / unhide / merge / unmerge an app or site. Returns an error message or null.</summary>
    public string ItemAction(bool site, int id, string action, string value)
    {
        lock (_lock)
        {
            var items = site ? _sites : _apps;
            string table = site ? "sites" : "apps";
            if (!items.TryGetValue(id, out var it)) return "Unknown item";
            switch (action)
            {
                case "rename":
                    string alias = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                    if (alias != null && alias.Length > 80) alias = alias[..80];
                    if (alias == it.Name) alias = null;
                    using (var q = _s.Prepare($"UPDATE {table} SET alias=?2 WHERE id=?1")) q.Bind(1, id).Bind(2, alias).Run();
                    it.Alias = alias;
                    return null;
                case "hide":
                case "unhide":
                    bool hide = action == "hide";
                    using (var q = _s.Prepare($"UPDATE {table} SET hidden=?2 WHERE id=?1")) q.Bind(1, id).Bind(2, hide ? 1 : 0).Run();
                    it.Hidden = hide;
                    return null;
                case "merge":
                    if (!int.TryParse(value, out int target) || !items.ContainsKey(target)) return "Unknown merge target";
                    target = Canonical(items, target);
                    if (target == id) return "Can't merge an item into itself";
                    _s.Exec("BEGIN");
                    try
                    {
                        // Anything already merged into this item moves along with it, so groups stay one level deep.
                        using (var q = _s.Prepare($"UPDATE {table} SET merged_into=?2 WHERE merged_into=?1")) q.Bind(1, id).Bind(2, target).Run();
                        using (var q = _s.Prepare($"UPDATE {table} SET merged_into=?2 WHERE id=?1")) q.Bind(1, id).Bind(2, target).Run();
                        _s.Exec("COMMIT");
                    }
                    catch { try { _s.Exec("ROLLBACK"); } catch { } throw; }
                    foreach (var other in items.Values) if (other.MergedInto == id) other.MergedInto = target;
                    it.MergedInto = target;
                    return null;
                case "unmerge":
                    using (var q = _s.Prepare($"UPDATE {table} SET merged_into=NULL WHERE id=?1")) q.Bind(1, id).Run();
                    it.MergedInto = 0;
                    return null;
            }
            return "Unknown action";
        }
    }

    // ---------- daily limits ----------

    public void SetLimit(string kind, int target, int minutes)
    {
        lock (_lock)
        {
            if (minutes <= 0)
            {
                using var d = _s.Prepare("DELETE FROM limits WHERE kind=?1 AND target=?2");
                d.Bind(1, kind).Bind(2, target).Run();
                return;
            }
            using var q = _s.Prepare("INSERT INTO limits(kind,target,minutes,notified_day) VALUES(?1,?2,?3,0) " +
                                     "ON CONFLICT(kind,target) DO UPDATE SET minutes=excluded.minutes, notified_day=0");
            q.Bind(1, kind).Bind(2, target).Bind(3, minutes).Run();
        }
    }

    public List<LimitStatus> Limits(int day)
    {
        lock (_lock)
        {
            var rows = new List<(string Kind, int Target, int Minutes, int Notified)>();
            using (var q = _s.Prepare("SELECT kind, target, minutes, notified_day FROM limits"))
                while (q.Step()) rows.Add((q.Text(0), (int)q.Long(1), (int)q.Long(2), (int)q.Long(3)));

            var result = new List<LimitStatus>(rows.Count);
            foreach (var r in rows)
            {
                string name, sql;
                if (r.Kind == "total") { name = "Total screen time"; sql = "SELECT COALESCE(SUM(ms),0) FROM app_usage WHERE day=?1" + NotHidden(_apps, "app_id"); }
                else
                {
                    bool site = r.Kind == "site";
                    var items = site ? _sites : _apps;
                    int c = Canonical(items, r.Target);
                    name = items.TryGetValue(c, out var it) ? it.Display : "?";
                    sql = site ? "SELECT COALESCE(SUM(ms),0) FROM site_usage WHERE day=?1" + InGroup(_sites, "site_id", c)
                               : "SELECT COALESCE(SUM(ms),0) FROM app_usage WHERE day=?1" + InGroup(_apps, "app_id", c);
                }
                using var q = _s.Prepare(sql);
                q.Bind(1, day);
                q.Step();
                result.Add(new LimitStatus(r.Kind, r.Target, name, r.Minutes, q.Long(0), r.Notified));
            }
            result.Sort((a, b) => (b.UsedMs / (double)b.Minutes).CompareTo(a.UsedMs / (double)a.Minutes));
            return result;
        }
    }

    public void MarkLimitNotified(string kind, int target, int day)
    {
        lock (_lock)
        {
            using var q = _s.Prepare("UPDATE limits SET notified_day=?3 WHERE kind=?1 AND target=?2");
            q.Bind(1, kind).Bind(2, target).Bind(3, day).Run();
        }
    }

    // ---------- backups ----------

    /// <summary>
    /// Daily snapshot (the newest 7 are kept) plus one snapshot per month that is kept forever.
    /// Uses VACUUM INTO, which writes a consistent, compact copy while the app keeps running.
    /// </summary>
    public void Backup(string dir, DateTime now)
    {
        string dailyDir = Path.Combine(dir, "daily"), monthlyDir = Path.Combine(dir, "monthly");
        Directory.CreateDirectory(dailyDir);
        Directory.CreateDirectory(monthlyDir);
        string daily = Path.Combine(dailyDir, $"screentime-{now:yyyy-MM-dd}.db");
        if (!File.Exists(daily))
        {
            string tmp = daily + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            lock (_lock) _s.Exec($"VACUUM INTO '{tmp.Replace("'", "''")}'");
            File.Move(tmp, daily);
        }
        string monthly = Path.Combine(monthlyDir, $"screentime-{now:yyyy-MM}.db");
        if (!File.Exists(monthly)) File.Copy(daily, monthly);

        foreach (var old in Directory.GetFiles(dailyDir, "screentime-*.db").OrderByDescending(f => f, StringComparer.Ordinal).Skip(7))
            try { File.Delete(old); } catch { }
    }

    /// <summary>Full history as CSV (one row per app/site per hour), using current names.</summary>
    public string ExportCsv()
    {
        var sb = new StringBuilder(1 << 16);
        sb.Append("date,hour,type,name,id,seconds\r\n");
        lock (_lock)
        {
            using (var q = _s.Prepare("SELECT day, hour, app_id, ms FROM app_usage ORDER BY day, hour"))
                while (q.Step())
                {
                    _apps.TryGetValue((int)q.Long(2), out var raw);
                    _apps.TryGetValue(Canonical(_apps, (int)q.Long(2)), out var a);
                    Row(sb, q.Long(0), q.Long(1), "app", a?.Display, raw?.Path ?? raw?.Exe, q.Long(3));
                }
            using (var q = _s.Prepare("SELECT day, hour, site_id, ms FROM site_usage ORDER BY day, hour"))
                while (q.Step())
                {
                    _sites.TryGetValue((int)q.Long(2), out var raw);
                    _sites.TryGetValue(Canonical(_sites, (int)q.Long(2)), out var s);
                    Row(sb, q.Long(0), q.Long(1), "website", s?.Display, raw?.Name, q.Long(3));
                }
            using (var q = _s.Prepare("SELECT day, hour, app_id, ms FROM app_open ORDER BY day, hour"))
                while (q.Step())
                {
                    _apps.TryGetValue((int)q.Long(2), out var raw);
                    _apps.TryGetValue(Canonical(_apps, (int)q.Long(2)), out var a);
                    Row(sb, q.Long(0), q.Long(1), "app open", a?.Display, raw?.Path ?? raw?.Exe, q.Long(3));
                }
            using (var q = _s.Prepare("SELECT day, hour, site_id, ms FROM site_open ORDER BY day, hour"))
                while (q.Step())
                {
                    _sites.TryGetValue((int)q.Long(2), out var raw);
                    _sites.TryGetValue(Canonical(_sites, (int)q.Long(2)), out var s);
                    Row(sb, q.Long(0), q.Long(1), "website open", s?.Display, raw?.Name, q.Long(3));
                }
        }
        return sb.ToString();

        static void Row(StringBuilder sb, long day, long hour, string type, string name, string id, long ms)
        {
            sb.Append(day / 10000).Append('-').Append((day / 100 % 100).ToString("00")).Append('-').Append((day % 100).ToString("00"))
              .Append(',').Append(hour).Append(',').Append(type).Append(',');
            Csv(sb, name ?? "?"); sb.Append(','); Csv(sb, id ?? "");
            sb.Append(',').Append(Math.Round(ms / 1000.0, 1).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n");
        }

        static void Csv(StringBuilder sb, string v)
        {
            if (v.IndexOfAny([',', '"', '\n', '\r']) < 0) { sb.Append(v); return; }
            sb.Append('"').Append(v.Replace("\"", "\"\"")).Append('"');
        }
    }

    // ---------- settings ----------

    public string GetMeta(string k)
    {
        lock (_lock)
        {
            using var q = _s.Prepare("SELECT v FROM meta WHERE k=?1");
            q.Bind(1, k);
            return q.Step() ? q.Text(0) : null;
        }
    }

    public void SetMeta(string k, string v)
    {
        lock (_lock)
        {
            using var q = _s.Prepare("INSERT INTO meta(k,v) VALUES(?1,?2) ON CONFLICT(k) DO UPDATE SET v=excluded.v");
            q.Bind(1, k).Bind(2, v).Run();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _upApp.Dispose(); _upSite.Dispose(); _upAppOpen.Dispose(); _upSiteOpen.Dispose(); _upBrowser?.Dispose(); _upBrowserSite?.Dispose(); _insApp.Dispose(); _insSite.Dispose(); _setIcon.Dispose(); _setPath.Dispose();
            try { _s.Exec("PRAGMA wal_checkpoint(TRUNCATE)"); } catch { }
            _s.Dispose();
        }
    }
}
