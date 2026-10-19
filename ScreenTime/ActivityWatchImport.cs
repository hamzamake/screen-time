using System.Globalization;
using System.Runtime;
using System.Text;
using System.Text.Json;

namespace ScreenTime;

/// <summary>
/// Imports ActivityWatch history (bucket exports as JSON, or CSV) on top of the existing data.
///
/// - App time = window events, clipped to the periods ActivityWatch's AFK watcher says you were active
///   (the same rule ActivityWatch itself uses).
/// - Website time = web-watcher events, clipped to the periods a browser was the active window.
/// - Anything inside a range that's already covered (Screen Time was running, or that range was imported
///   before) is skipped. Everything else is added to the hourly totals, never replacing them. So importing
///   the same files twice, or a newer export that overlaps an older one, never counts anything twice.
/// </summary>
internal static class ActivityWatchImport
{
    public sealed record Result(long AppMs, long SiteMs, long SkippedMs, long FromMs, long ToMs, int Apps, int Sites, string Warning);

    private enum Kind { None, Window, Afk, Web }

    private readonly record struct Ev(long Start, long End, int Label);

    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "brave.exe", "opera.exe", "vivaldi.exe", "firefox.exe", "chromium.exe", "arc.exe",
        "librewolf.exe", "waterfox.exe", "zen.exe", "floorp.exe", "thorium.exe", "browser.exe", "iexplore.exe", "comet.exe",
    };

    private static readonly HashSet<string> SkipApps = new(StringComparer.OrdinalIgnoreCase) { "", "unknown", "lockapp.exe" };

    private static int _running;

    // ---------- file discovery ----------

    public static List<FileInfo> FindFiles(string folder)
    {
        if (!Directory.Exists(folder)) return [];
        return new DirectoryInfo(folder).EnumerateFiles()
            .Where(f => (f.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".csv", StringComparison.OrdinalIgnoreCase))
                        && (f.Name.StartsWith("aw-", StringComparison.OrdinalIgnoreCase) || f.Name.Contains("activitywatch", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();
    }

    public static string GuessKind(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("watcher-window")) return "Window activity (apps)";
        if (n.Contains("watcher-afk")) return "AFK status";
        if (n.Contains("watcher-web")) return "Browser tabs (websites)";
        if (n.Contains("buckets")) return "All buckets";
        return "ActivityWatch export";
    }

    // ---------- import ----------

    public static Result Run(Db db, IEnumerable<string> paths)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) throw new InvalidOperationException("An import is already running.");
        try
        {
            var labels = new List<string>();
            var labelIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int Label(string s)
            {
                if (!labelIds.TryGetValue(s, out int i)) { i = labels.Count; labels.Add(s); labelIds[s] = i; }
                return i;
            }
            var window = new List<Ev>();
            var notAfk = new List<Ev>();
            var web = new List<Ev>();
            bool sawAfk = false;

            foreach (var path in paths)
            {
                var sink = new Action<Kind, long, long, string>((kind, start, end, value) =>
                {
                    if (end <= start || value == null) return;
                    switch (kind)
                    {
                        case Kind.Window:
                            if (!SkipApps.Contains(value)) window.Add(new Ev(start, end, Label(value)));
                            break;
                        case Kind.Afk:
                            sawAfk = true;
                            if (value == "not-afk") notAfk.Add(new Ev(start, end, 0));
                            break;
                        case Kind.Web:
                            string domain = DomainOf(value);
                            if (domain != null) web.Add(new Ev(start, end, Label(domain)));
                            break;
                    }
                });
                if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) ReadCsv(path, sink);
                else ReadJson(path, sink);
            }

            if (window.Count == 0)
                throw new InvalidOperationException("No window activity found. Include the aw-watcher-window export (that's the one with your app usage).");

            long fromMs = window.Min(e => e.Start), toMs = window.Max(e => e.End);
            window = Flatten(window);
            web = Flatten(web);
            var active = sawAfk ? Union(notAfk) : null;
            var covered = db.Coverage();

            // App time: window events while not AFK, minus anything already covered.
            var appIntervals = active != null ? Intersect(window, active) : window;
            long before = Sum(appIntervals);
            appIntervals = Subtract(appIntervals, covered);
            long skipped = before - Sum(appIntervals);

            // Website time: web events while a browser was the active (non-AFK, not already covered) window.
            var browserTime = Union(appIntervals.Where(e => Browsers.Contains(labels[e.Label])).ToList());
            var siteIntervals = web.Count > 0 ? Intersect(web, browserTime) : [];

            // Resolve names to ids and add everything up per local hour.
            var running = RunningPaths();
            var appIds = new Dictionary<int, int>();
            var siteIds = new Dictionary<int, int>();
            var buckets = new Dictionary<Key, long>();
            long appMs = 0, siteMs = 0;
            int legacyBrowser = -1;   // imported browsing has no profile info: it goes to the "earlier browsing" tracker entry
            foreach (var e in appIntervals)
            {
                if (!appIds.TryGetValue(e.Label, out int id))
                    appIds[e.Label] = id = db.ImportAppId(labels[e.Label], exe => running.TryGetValue(exe, out var p) ? p : null);
                AddSplit(buckets, e.Start, e.End, Bucket.App, id);
                if (Browsers.Contains(labels[e.Label]))
                {
                    if (legacyBrowser < 0) legacyBrowser = db.LegacyBrowserId();
                    AddSplit(buckets, e.Start, e.End, Bucket.Browser, legacyBrowser);
                }
                appMs += e.End - e.Start;
            }
            foreach (var e in siteIntervals)
            {
                if (!siteIds.TryGetValue(e.Label, out int id)) siteIds[e.Label] = id = db.SiteId(labels[e.Label], null);
                AddSplit(buckets, e.Start, e.End, Bucket.Site, id);
                if (legacyBrowser < 0) legacyBrowser = db.LegacyBrowserId();
                AddSplit(buckets, e.Start, e.End, Bucket.BrowserSite, legacyBrowser, id);
                siteMs += e.End - e.Start;
            }

            db.WriteImport(buckets, "activitywatch", fromMs, toMs);

            string warning = !sawAfk ? "No AFK export was included, so time you were away from the PC may be counted. Include the aw-watcher-afk export next time."
                           : web.Count == 0 ? "No browser export was included, so no website time was imported."
                           : null;
            return new Result(appMs, siteMs, skipped, fromMs, toMs, appIds.Count, siteIds.Count, warning);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            // Give the memory used for parsing big exports back to Windows; the tracker itself needs very little.
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, true, true);
        }
    }

    // ---------- interval math (all lists sorted by start) ----------

    /// <summary>Sorts and removes overlaps inside one bucket (later events start where earlier ones end).</summary>
    private static List<Ev> Flatten(List<Ev> evs)
    {
        evs.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
        var result = new List<Ev>(evs.Count);
        long cursor = long.MinValue;
        foreach (var e in evs)
        {
            long s = Math.Max(e.Start, cursor);
            if (e.End > s) { result.Add(new Ev(s, e.End, e.Label)); cursor = e.End; }
        }
        return result;
    }

    private static List<Ev> Union(List<Ev> evs)
    {
        evs.Sort((a, b) => a.Start.CompareTo(b.Start));
        var result = new List<Ev>();
        foreach (var e in evs)
        {
            if (result.Count > 0 && e.Start <= result[^1].End)
            {
                if (e.End > result[^1].End) result[^1] = result[^1] with { End = e.End };
            }
            else result.Add(e);
        }
        return result;
    }

    /// <summary>Parts of <paramref name="a"/> inside <paramref name="b"/> (b must not overlap itself).</summary>
    private static List<Ev> Intersect(List<Ev> a, List<Ev> b)
    {
        var result = new List<Ev>(a.Count);
        int i = 0, j = 0;
        while (i < a.Count && j < b.Count)
        {
            long s = Math.Max(a[i].Start, b[j].Start), e = Math.Min(a[i].End, b[j].End);
            if (s < e) result.Add(new Ev(s, e, a[i].Label));
            if (a[i].End < b[j].End) i++; else j++;
        }
        return result;
    }

    private static List<Ev> Subtract(List<Ev> a, List<(long Start, long End)> cut)
    {
        if (cut.Count == 0) return a;
        var result = new List<Ev>(a.Count);
        int j = 0;
        foreach (var e in a)
        {
            while (j < cut.Count && cut[j].End <= e.Start) j++;
            long cur = e.Start;
            for (int k = j; k < cut.Count && cut[k].Start < e.End; k++)
            {
                if (cut[k].Start > cur) result.Add(new Ev(cur, cut[k].Start, e.Label));
                cur = Math.Max(cur, cut[k].End);
                if (cur >= e.End) break;
            }
            if (cur < e.End) result.Add(new Ev(cur, e.End, e.Label));
        }
        return result;
    }

    private static long Sum(List<Ev> evs) { long t = 0; foreach (var e in evs) t += e.End - e.Start; return t; }

    /// <summary>Adds an interval to the per-local-hour buckets, splitting it at hour boundaries.</summary>
    private static void AddSplit(Dictionary<Key, long> buckets, long start, long end, Bucket kind, int id, int sub = 0)
    {
        while (start < end)
        {
            var local = DateTimeOffset.FromUnixTimeMilliseconds(start).ToLocalTime();
            long intoHour = (local.Minute * 60L + local.Second) * 1000 + local.Millisecond;
            long stop = Math.Min(end, start + 3_600_000 - intoHour);
            var k = new Key(Db.DayOf(local.DateTime), local.Hour, kind, id, sub);
            buckets.TryGetValue(k, out long v);
            buckets[k] = v + (stop - start);
            start = stop;
        }
    }

    private static Dictionary<string, string> RunningPaths()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            using (p)
            {
                string path = Native.ProcessPath((uint)p.Id);
                if (path == null) continue;
                string exe = Path.GetFileName(path);
                if (map.TryGetValue(exe, out var existing) && !string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)) ambiguous.Add(exe);
                else map[exe] = path;
            }
        }
        foreach (var exe in ambiguous) map.Remove(exe);   // two different programs with this name: don't guess
        return map;
    }

    private static string DomainOf(string url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        if (u.Scheme == "http" || u.Scheme == "https")
        {
            string host = u.Host.ToLowerInvariant();
            if (host.StartsWith("www.")) host = host[4..];
            return host.Length > 0 && host.Length <= 253 ? host : null;
        }
        if (u.Scheme == "file") return "local files";
        if (u.Scheme is "chrome" or "edge" or "brave" or "opera" or "vivaldi" && u.Host.Length > 0) return u.Scheme + "://" + u.Host;
        return null;
    }

    // ---------- readers ----------

    private static Kind KindOf(string bucketId, string type)
    {
        string id = (bucketId ?? "").ToLowerInvariant(), t = (type ?? "").ToLowerInvariant();
        if (t == "currentwindow" || id.Contains("aw-watcher-window")) return Kind.Window;
        if (t == "afkstatus" || id.Contains("aw-watcher-afk")) return Kind.Afk;
        if (t.StartsWith("web.tab") || id.Contains("aw-watcher-web")) return Kind.Web;
        return Kind.None;
    }

    /// <summary>
    /// ActivityWatch JSON export: {"buckets": {"&lt;id&gt;": {"type": ..., "events": [{"timestamp", "duration", "data"}]}}}.
    /// Streamed with Utf8JsonReader so a big export only costs its file size in memory, briefly.
    /// </summary>
    private static void ReadJson(string path, Action<Kind, long, long, string> sink)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var r = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 });
        string Bad() => $"{Path.GetFileName(path)} doesn't look like an ActivityWatch export.";

        if (!r.Read() || r.TokenType != JsonTokenType.StartObject) throw new InvalidOperationException(Bad());
        bool sawBuckets = false;
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (!r.ValueTextEquals("buckets")) { r.Read(); r.Skip(); continue; }
            sawBuckets = true;
            r.Read();
            if (r.TokenType != JsonTokenType.StartObject) throw new InvalidOperationException(Bad());
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                string bucketId = r.GetString();
                r.Read();
                if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); continue; }
                string type = null;
                while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                {
                    if (r.ValueTextEquals("type"))
                    {
                        r.Read();
                        type = r.TokenType == JsonTokenType.String ? r.GetString() : null;
                    }
                    else if (r.ValueTextEquals("events"))
                    {
                        r.Read();
                        var kind = KindOf(bucketId, type);
                        if (kind == Kind.None || r.TokenType != JsonTokenType.StartArray) { r.Skip(); continue; }
                        ReadEvents(ref r, kind, sink);
                    }
                    else { r.Read(); r.Skip(); }
                }
            }
        }
        if (!sawBuckets) throw new InvalidOperationException(Bad());
    }

    private static void ReadEvents(ref Utf8JsonReader r, Kind kind, Action<Kind, long, long, string> sink)
    {
        string field = kind switch { Kind.Window => "app", Kind.Afk => "status", _ => "url" };
        while (r.Read() && r.TokenType == JsonTokenType.StartObject)
        {
            long start = long.MinValue;
            double duration = 0;
            string value = null;
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                if (r.ValueTextEquals("timestamp"))
                {
                    r.Read();
                    if (r.TokenType == JsonTokenType.String
                        && DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts))
                        start = ts.ToUnixTimeMilliseconds();
                }
                else if (r.ValueTextEquals("duration"))
                {
                    r.Read();
                    if (r.TokenType == JsonTokenType.Number) duration = r.GetDouble();
                }
                else if (r.ValueTextEquals("data"))
                {
                    r.Read();
                    if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); continue; }
                    while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                    {
                        bool wanted = r.ValueTextEquals(field);
                        r.Read();
                        if (wanted && r.TokenType == JsonTokenType.String) value = r.GetString();
                        else r.Skip();
                    }
                }
                else { r.Read(); r.Skip(); }
            }
            if (start != long.MinValue) sink(kind, start, start + (long)(duration * 1000), value);
        }
    }

    /// <summary>CSV export: a header row with timestamp, duration and the data field (app / status / url).</summary>
    private static void ReadCsv(string path, Action<Kind, long, long, string> sink)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        var header = ParseCsvLine(reader);
        if (header == null) return;
        int Col(params string[] names)
        {
            for (int i = 0; i < header.Count; i++)
            {
                string h = header[i].Trim().ToLowerInvariant();
                if (h.StartsWith("data.")) h = h[5..];
                if (names.Contains(h)) return i;
            }
            return -1;
        }
        int tsCol = Col("timestamp", "time", "start"), durCol = Col("duration"), appCol = Col("app"), statusCol = Col("status"), urlCol = Col("url");
        if (tsCol < 0 || durCol < 0) throw new InvalidOperationException($"{Path.GetFileName(path)}: expected 'timestamp' and 'duration' columns.");

        var kind = KindOf(Path.GetFileName(path), null);
        if (kind == Kind.None) kind = appCol >= 0 ? Kind.Window : statusCol >= 0 ? Kind.Afk : urlCol >= 0 ? Kind.Web : Kind.None;
        int valCol = kind switch { Kind.Window => appCol, Kind.Afk => statusCol, Kind.Web => urlCol, _ => -1 };
        if (valCol < 0) return;

        List<string> row;
        while ((row = ParseCsvLine(reader)) != null)
        {
            if (row.Count <= Math.Max(tsCol, Math.Max(durCol, valCol))) continue;
            if (!DateTimeOffset.TryParse(row[tsCol], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start)) continue;
            if (!double.TryParse(row[durCol], NumberStyles.Float, CultureInfo.InvariantCulture, out double dur)) continue;
            long s = start.ToUnixTimeMilliseconds();
            sink(kind, s, s + (long)(dur * 1000), row[valCol]);
        }
    }

    private static List<string> ParseCsvLine(StreamReader r)
    {
        if (r.EndOfStream) return null;
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        while (true)
        {
            int c = r.Read();
            if (c < 0) { fields.Add(sb.ToString()); return fields; }
            if (quoted)
            {
                if (c == '"') { if (r.Peek() == '"') { r.Read(); sb.Append('"'); } else quoted = false; }
                else sb.Append((char)c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else if (c == '\n') { fields.Add(sb.ToString().TrimEnd('\r')); return fields; }
            else sb.Append((char)c);
        }
    }
}
