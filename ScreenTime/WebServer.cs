using System.Buffers;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ScreenTime;

/// <summary>
/// Tiny loopback-only HTTP server: serves the dashboard and its JSON API, and receives the
/// active tab from the browser extension. Idle cost is one pending accept on a socket.
/// </summary>
internal sealed class WebServer
{
    private readonly Db _db;
    private readonly Tracker _tracker;
    private readonly Settings _settings;
    private readonly Updater _updater;
    private readonly int _port;
    private readonly byte[] _index, _favicon;
    private readonly ConcurrentDictionary<int, byte[]> _icons = new();
    private readonly CancellationTokenSource _cts = new();
    private TcpListener _listener;

    /// <summary>When tracking is paused until (DateTime.MaxValue: until resumed), or null when it isn't.</summary>
    public Func<DateTime?> PausedUntil { get; init; }
    /// <summary>Pauses until the given time (DateTime.MaxValue: until resumed), or resumes for null.</summary>
    public Action<DateTime?> SetPause { get; init; }

    public WebServer(Db db, Tracker tracker, Settings settings, Updater updater, int port)
    {
        _db = db; _tracker = tracker; _settings = settings; _updater = updater; _port = port;
        _index = Resource("index.html");
        _favicon = Resource("app.ico");
    }

    private static byte[] Resource(string name)
    {
        using var s = typeof(WebServer).Assembly.GetManifestResourceStream(name);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start(16);
        _ = AcceptLoop();
    }

    public void Stop()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
            catch { if (_cts.IsCancellationRequested) return; continue; }
            _ = Task.Run(() => Handle(client));
        }
    }

    private readonly record struct Request(string Method, string Path, Dictionary<string, string> Query,
        Dictionary<string, string> Headers, byte[] Body);

    private readonly record struct Response(int Code, string Type, byte[] Body, string Cache = "no-store", string Extra = null);

    private async Task Handle(TcpClient client)
    {
        using (client)
        using (var timeout = new CancellationTokenSource(15_000))
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var req = await ReadRequest(stream, timeout.Token).ConfigureAwait(false);
                if (req == null) return;
                Response res;
                try { res = Route(req.Value); }
                catch (Exception ex) { res = Text(500, ex.Message); }
                using var writeTimeout = new CancellationTokenSource(15_000);   // routing (e.g. a big import) may take a while
                await WriteResponse(stream, res, req.Value.Method == "HEAD", writeTimeout.Token).ConfigureAwait(false);
            }
            catch { /* client went away */ }
        }
    }

    private static async Task<Request?> ReadRequest(NetworkStream s, CancellationToken ct)
    {
        byte[] buf = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int len = 0, headerEnd = -1;
            while (headerEnd < 0)
            {
                if (len == buf.Length) return null;   // headers too large
                int n = await s.ReadAsync(buf.AsMemory(len), ct).ConfigureAwait(false);
                if (n == 0) return null;
                len += n;
                headerEnd = buf.AsSpan(0, len).IndexOf("\r\n\r\n"u8);
            }

            string head = Encoding.ASCII.GetString(buf, 0, headerEnd);
            string[] lines = head.Split("\r\n");
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) return null;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c > 0) headers[lines[i][..c].Trim()] = lines[i][(c + 1)..].Trim();
            }

            byte[] body = [];
            if (headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out int bodyLen) && bodyLen > 0)
            {
                if (bodyLen > 64 * 1024) return null;
                body = new byte[bodyLen];
                int have = Math.Min(len - (headerEnd + 4), bodyLen);
                Array.Copy(buf, headerEnd + 4, body, 0, have);
                while (have < bodyLen)
                {
                    int n = await s.ReadAsync(body.AsMemory(have), ct).ConfigureAwait(false);
                    if (n == 0) return null;
                    have += n;
                }
            }

            string target = first[1];
            int qm = target.IndexOf('?');
            string path = qm < 0 ? target : target[..qm];
            var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (qm >= 0)
                foreach (var part in target[(qm + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = part.IndexOf('=');
                    if (eq > 0) query[Uri.UnescapeDataString(part[..eq])] = Uri.UnescapeDataString(part[(eq + 1)..]);
                }

            return new Request(first[0].ToUpperInvariant(), path, query, headers, body);
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    private static async Task WriteResponse(NetworkStream s, Response r, bool headOnly, CancellationToken ct)
    {
        string reason = r.Code switch { 200 => "OK", 204 => "No Content", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Error" };
        var sb = new StringBuilder(256);
        sb.Append("HTTP/1.1 ").Append(r.Code).Append(' ').Append(reason).Append("\r\n")
          .Append("Content-Type: ").Append(r.Type).Append("\r\n")
          .Append("Content-Length: ").Append(r.Body.Length).Append("\r\n")
          .Append("Cache-Control: ").Append(r.Cache).Append("\r\n")
          .Append("X-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n");
        if (r.Extra != null) sb.Append(r.Extra);
        sb.Append("\r\n");
        await s.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct).ConfigureAwait(false);
        if (!headOnly && r.Body.Length > 0) await s.WriteAsync(r.Body, ct).ConfigureAwait(false);
    }

    private static Response Text(int code, string msg) => new(code, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(msg));

    private static Response Json(Action<Utf8JsonWriter> write)
    {
        var buf = new ArrayBufferWriter<byte>(4096);
        using (var w = new Utf8JsonWriter(buf)) write(w);
        return new Response(200, "application/json", buf.WrittenSpan.ToArray());
    }

    private Response Route(Request req)
    {
        // Only answer requests addressed to us by name (blocks DNS-rebinding from web pages).
        req.Headers.TryGetValue("Host", out var host);
        if (host != $"127.0.0.1:{_port}" && host != $"localhost:{_port}") return Text(403, "Forbidden");

        if (req.Method == "POST")
        {
            if (req.Path == "/api/tab") return PostTab(req);
            if (req.Path == "/api/browser") return PostBrowser(req);   // the extension popup (or the dashboard)
            if (!FromDashboard(req)) return Text(403, "Forbidden");
            if (req.Path == "/api/trackers") return PostTrackers(req);
            if (req.Path == "/api/item") return PostItem(req);
            if (req.Path == "/api/limit") return PostLimit(req);
            if (req.Path == "/api/import") return PostImport(req);
            if (req.Path == "/api/settings") return PostSettings(req);
            if (req.Path == "/api/pause") return PostPause(req);
            if (req.Path == "/api/update") return PostUpdate(req);
            return Text(404, "Not found");
        }
        if (req.Method != "GET" && req.Method != "HEAD") return Text(405, "Method not allowed");

        switch (req.Path)
        {
            case "/":
            case "/index.html":
                return new Response(200, "text/html; charset=utf-8", _index, "no-cache",
                    "Content-Security-Policy: default-src 'self'; img-src 'self' data: https: http:; style-src 'unsafe-inline'; script-src 'unsafe-inline'; frame-ancestors 'none'\r\n");
            case "/favicon.ico":
                return new Response(200, "image/x-icon", _favicon, "max-age=86400");
            case "/api/summary":
                return Summary(req);
            case "/api/now":
                return Now();
            case "/api/items":
                return Json(w => _db.WriteItemList(w, req.Query.TryGetValue("kind", out var k) && k == "site"));
            case "/api/limits":
                return Limits();
            case "/api/settings":
                return Json(WriteSettings);
            case "/api/trackers":
            {
                int today = Db.DayOf(DateTime.Now);
                int from = Int(req.Query, "from", today), to = Int(req.Query, "to", today);
                if (to < from) (from, to) = (to, from);
                _tracker.Flush();
                return Json(w => _db.WriteTrackers(w, from, to, _tracker.ActiveBrowserId));
            }
            case "/api/browser":
            {
                if (!req.Query.TryGetValue("instance", out var inst) || inst.Length is 0 or > 64) return Text(400, "Missing instance");
                _tracker.Flush();
                bool found = false;
                var r = Json(w => found = _db.WriteBrowserForInstance(w, inst, Db.DayOf(DateTime.Now)));
                return found ? r : Text(404, "Not registered yet");
            }
            case "/api/import/scan":
                return ImportScan();
            case "/api/export.csv":
                _tracker.Flush();
                return new Response(200, "text/csv; charset=utf-8", Encoding.UTF8.GetBytes(_db.ExportCsv()), "no-store",
                    $"Content-Disposition: attachment; filename=\"screen-time-{DateTime.Now:yyyy-MM-dd}.csv\"\r\n");
        }

        if (req.Path.StartsWith("/icon/app/") && int.TryParse(req.Path.AsSpan(10), out int appId))
        {
            byte[] png = _icons.GetOrAdd(appId, AppIcon);
            return png.Length == 0 ? Text(404, "No icon") : new Response(200, "image/png", png, "max-age=86400");
        }
        return Text(404, "Not found");
    }

    private Response Summary(Request req)
    {
        int today = Db.DayOf(DateTime.Now);
        int from = Int(req.Query, "from", today), to = Int(req.Query, "to", today);
        if (to < from) (from, to) = (to, from);
        int app = Int(req.Query, "app", -1), site = Int(req.Query, "site", -1);
        bool hidden = Int(req.Query, "hidden", 0) == 1;
        _tracker.Flush();
        return Json(w => _db.WriteSummary(w, from, to, app, site, hidden, _settings.TrackOpenTime, IsBrowser));
    }

    private Response Now()
    {
        var st = _tracker.Now();
        int firstDay = _db.FirstDay();
        return Json(w =>
        {
            w.WriteStartObject();
            w.WriteString("state", st.State);
            if (st.App != null) w.WriteString("app", st.App);
            if (st.Site != null) w.WriteString("site", st.Site);
            w.WriteNumber("extensionSecondsAgo", st.ExtensionSecondsAgo);
            w.WriteNumber("firstDay", firstDay);
            w.WriteNumber("today", Db.DayOf(DateTime.Now));
            w.WriteNumber("idleMinutes", _tracker.IdleTimeoutMs / 60_000);
            w.WriteString("dataDir", Program.DataDir);
            w.WriteBoolean("paused", _tracker.Paused);
            if (PausedUntil?.Invoke() is DateTime until)
            {
                if (until == DateTime.MaxValue) w.WriteNull("pausedUntil");
                else w.WriteNumber("pausedUntil", new DateTimeOffset(until).ToUnixTimeMilliseconds());
            }
            w.WriteString("version", Updater.CurrentText);
            w.WritePropertyName("update");
            WriteUpdate(w);
            w.WriteStartArray("extensions");
            lock (_instances)
            {
                long now = Environment.TickCount64;
                foreach (var (id, i) in _instances)
                {
                    w.WriteStartObject();
                    w.WriteString("id", id);
                    w.WriteString("browser", i.Browser);
                    w.WriteNumber("secondsAgo", (now - i.Last) / 1000);
                    w.WriteEndObject();
                }
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    /// <summary>
    /// Changes may only come from the dashboard itself: same origin, plus a custom header that a
    /// foreign web page can't send without a CORS preflight (which we never approve).
    /// </summary>
    /// <summary>Requests from a browser extension page (its popup): custom header + an extension origin.</summary>
    private static bool FromExtension(Request req) =>
        req.Headers.ContainsKey("X-ScreenTime")
        && req.Headers.TryGetValue("Origin", out var o)
        && (o.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) || o.StartsWith("extension://", StringComparison.OrdinalIgnoreCase));

    private Response PostTrackers(Request req)
    {
        try
        {
            using var doc = JsonDocument.Parse(req.Body);
            _tracker.Flush();
            string err = _db.TrackerAction(doc.RootElement);
            return err == null ? new Response(204, "text/plain", []) : Text(400, err);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return Text(400, "Bad request");
        }
    }

    private Response PostBrowser(Request req)
    {
        if (!FromExtension(req) && !FromDashboard(req)) return Text(403, "Forbidden");
        try
        {
            using var doc = JsonDocument.Parse(req.Body);
            var r = doc.RootElement;
            string instance = r.GetProperty("instance").GetString();
            int? groupId = r.TryGetProperty("groupId", out var g) && g.ValueKind == JsonValueKind.Number ? g.GetInt32() : null;
            string name = r.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            string err = _db.BrowserSelfAction(instance, groupId, name);
            return err == null ? new Response(204, "text/plain", []) : Text(400, err);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return Text(400, "Bad request");
        }
    }

    private bool FromDashboard(Request req) =>
        req.Headers.ContainsKey("X-ScreenTime")
        && req.Headers.TryGetValue("Origin", out var o)
        && (o == $"http://127.0.0.1:{_port}" || o == $"http://localhost:{_port}");

    private Response PostItem(Request req)
    {
        try
        {
            using var doc = JsonDocument.Parse(req.Body);
            var r = doc.RootElement;
            bool site = r.GetProperty("kind").GetString() == "site";
            int id = r.GetProperty("id").GetInt32();
            string action = r.GetProperty("action").GetString();
            string value = r.TryGetProperty("value", out var v) ? (v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.GetString()) : null;
            _tracker.Flush();
            string err = _db.ItemAction(site, id, action, value);
            return err == null ? new Response(204, "text/plain", []) : Text(400, err);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return Text(400, "Bad request");
        }
    }

    private Response PostLimit(Request req)
    {
        try
        {
            using var doc = JsonDocument.Parse(req.Body);
            var r = doc.RootElement;
            string kind = r.GetProperty("kind").GetString();
            if (kind != "total" && kind != "app" && kind != "site") return Text(400, "Bad kind");
            int id = kind == "total" ? 0 : r.GetProperty("id").GetInt32();
            int minutes = Math.Clamp(r.GetProperty("minutes").GetInt32(), 0, 24 * 60);
            _db.SetLimit(kind, id, minutes);
            return new Response(204, "text/plain", []);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return Text(400, "Bad request");
        }
    }

    private Response Limits()
    {
        _tracker.Flush();
        var list = _db.Limits(Db.DayOf(DateTime.Now));
        return Json(w =>
        {
            w.WriteStartArray();
            foreach (var l in list)
            {
                w.WriteStartObject();
                w.WriteString("kind", l.Kind);
                w.WriteNumber("id", l.Target);
                w.WriteString("name", l.Name);
                w.WriteNumber("minutes", l.Minutes);
                w.WriteNumber("usedMs", l.UsedMs);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
    }

    private Response PostTab(Request req)
    {
        // Web pages can't send custom headers cross-origin without a preflight we never approve.
        if (!req.Headers.ContainsKey("X-ScreenTime")) return Text(403, "Forbidden");
        if (req.Headers.TryGetValue("Origin", out var origin) && origin.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return Text(403, "Forbidden");

        string browser, domain = null, icon = null, instance = null, title = null, reason = null;
        List<string> openDomains = null;
        bool? focused = null;
        long clientTs = 0;
        try
        {
            using var doc = JsonDocument.Parse(req.Body);
            var root = doc.RootElement;
            browser = root.GetProperty("browser").GetString();
            if (root.TryGetProperty("domain", out var d) && d.ValueKind == JsonValueKind.String) domain = d.GetString();
            if (root.TryGetProperty("icon", out var i) && i.ValueKind == JsonValueKind.String) icon = i.GetString();
            if (root.TryGetProperty("instance", out var n) && n.ValueKind == JsonValueKind.String) instance = n.GetString();
            if (root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String) title = t.GetString();
            if (root.TryGetProperty("open", out var o) && o.ValueKind == JsonValueKind.Array)
            {
                openDomains = new List<string>();
                foreach (var d2 in o.EnumerateArray())
                    if (d2.ValueKind == JsonValueKind.String && d2.GetString() is { Length: > 0 and <= 253 } od && openDomains.Count < 300)
                        openDomains.Add(od.ToLowerInvariant());
            }
            if (root.TryGetProperty("reason", out var why) && why.ValueKind == JsonValueKind.String) reason = why.GetString();
            if (root.TryGetProperty("focused", out var f) && f.ValueKind is JsonValueKind.True or JsonValueKind.False) focused = f.GetBoolean();
            if (root.TryGetProperty("t", out var ts) && ts.ValueKind == JsonValueKind.Number && ts.TryGetInt64(out long tsv)) clientTs = tsv;
        }
        catch { return Text(400, "Bad JSON"); }

        if (string.IsNullOrEmpty(browser) || browser.Length > 40 || !browser.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return Text(400, "Bad browser");
        if (domain != null && (domain.Length == 0 || domain.Length > 253)) domain = null;
        if (icon != null && !(icon.Length <= 2048 && icon.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                              || icon.Length <= 12_000 && icon.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)))
            icon = null;

        // Only learn right after the user focused the browser or clicked a tab: then the browser is certainly
        // the window in front (a heartbeat could arrive while e.g. the Discord app shows the same title).
        if (instance != null && instance.Length is > 0 and <= 64)
        {
            browser = BrowserFor(instance, reason is "focus" or "activated" ? title : null) ?? browser;
            lock (_instances)
            {
                long now = Environment.TickCount64;
                _instances[instance] = _instances.TryGetValue(instance, out var seen) ? (browser, seen.First, now) : (browser, now, now);
            }
        }

        int siteId = domain != null ? _db.SiteId(domain.ToLowerInvariant(), icon) : -1;
        _tracker.SetBrowserTab(browser, domain, siteId);
        if (instance != null)
        {
            // Each extension install is one browser installation/profile with its own tracked time.
            int browserId = _db.BrowserId(instance, browser);
            int[] open = openDomains != null && _settings.TrackOpenTime ? openDomains.Distinct().Select(d => _db.SiteId(d, null)).ToArray() : null;
            _tracker.ReportInstance(instance, browser, browserId, domain, siteId, focused, clientTs, open);
        }
        // Lets the extension notice that the app (and the extension files it installed) were updated.
        return new Response(204, "text/plain", [], Extra: $"X-ScreenTime-Version: {Updater.CurrentText}\r\n");
    }

    // ---- which browser is this extension running in? ----
    // Many Chromium browsers (Vivaldi, Opera, Arc, ...) report themselves to extensions as plain Chrome,
    // so the extension's guess can't be trusted. Instead: when the window in front has a title that starts
    // with the extension's active tab title (Chromium titles its windows "<tab title> - <browser>"), the
    // foreground process is that browser. Changing an answer already learned needs two agreeing sightings.
    // Callers pass a title only for focus/tab-click reports, so this never learns from background updates.
    private readonly Dictionary<string, string> _learned = new(StringComparer.Ordinal);
    // Extension installs that reported since the app started (lets the setup wizard see a new one connect).
    private readonly Dictionary<string, (string Browser, long First, long Last)> _instances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Exe, int Count)> _candidates = new(StringComparer.Ordinal);

    private string BrowserFor(string instance, string tabTitle)
    {
        lock (_learned)
        {
            if (!_learned.TryGetValue(instance, out var known))
            {
                known = _db.GetMeta("ext_browser:" + instance);
                if (string.IsNullOrEmpty(known)) known = null;
                _learned[instance] = known;
            }

            if (tabTitle != null && tabTitle.Trim().Length >= 2)
            {
                var (exe, winTitle) = Native.Foreground();
                if (exe != null && winTitle != null && winTitle.StartsWith(tabTitle, StringComparison.Ordinal)
                    && winTitle.Length - tabTitle.Length <= 40
                    && !exe.Equals("ScreenTime.exe", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(known, exe, StringComparison.OrdinalIgnoreCase)) _candidates.Remove(instance);
                    else
                    {
                        int count = _candidates.TryGetValue(instance, out var c) && string.Equals(c.Exe, exe, StringComparison.OrdinalIgnoreCase) ? c.Count + 1 : 1;
                        if (count >= 2 || known == null)
                        {
                            _learned[instance] = known = exe;
                            _candidates.Remove(instance);
                            _db.SetMeta("ext_browser:" + instance, exe);
                        }
                        else _candidates[instance] = (exe, count);
                    }
                }
            }
            return known;
        }
    }

    /// <summary>Known browsers plus whatever browser the extension turned out to be running in.</summary>
    private bool IsBrowser(string exe)
    {
        if (Browsers.IsKnownBrowser(exe)) return true;
        lock (_instances) foreach (var i in _instances.Values) if (string.Equals(i.Browser, exe, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ---- settings ----

    private void WriteSettings(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteBoolean("trackOpen", _settings.TrackOpenTime);
        w.WriteNumber("idleMinutes", _settings.IdleMinutes);
        w.WriteBoolean("countMedia", _settings.CountMedia);
        w.WriteBoolean("autoStart", _settings.AutoStart);
        w.WriteBoolean("autoUpdate", _settings.AutoUpdate);
        w.WriteEndObject();
    }

    private Response PostSettings(Request req)
    {
        try
        {
            using var doc = JsonDocument.Parse(req.Body);
            var r = doc.RootElement;
            if (r.TryGetProperty("trackOpen", out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False) _settings.TrackOpenTime = v.GetBoolean();
            if (r.TryGetProperty("countMedia", out v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False) _settings.CountMedia = v.GetBoolean();
            if (r.TryGetProperty("autoStart", out v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False) _settings.AutoStart = v.GetBoolean();
            if (r.TryGetProperty("autoUpdate", out v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False) _settings.AutoUpdate = v.GetBoolean();
            if (r.TryGetProperty("idleMinutes", out v) && v.ValueKind == JsonValueKind.Number) _settings.IdleMinutes = v.GetInt32();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return Text(400, "Bad request");
        }
        return Json(WriteSettings);
    }

    // ---- pause / updates ----

    /// <summary>{"minutes": n} pauses for n minutes; -1 until resumed, -2 until tomorrow, 0 resumes.</summary>
    private Response PostPause(Request req)
    {
        try
        {
            using var doc = JsonDocument.Parse(req.Body);
            int minutes = doc.RootElement.GetProperty("minutes").GetInt32();
            DateTime? until = minutes switch
            {
                0 => null,
                -1 => DateTime.MaxValue,
                -2 => DateTime.Today.AddDays(1),
                > 0 and <= 7 * 24 * 60 => DateTime.Now.AddMinutes(minutes),
                _ => throw new FormatException(),
            };
            SetPause?.Invoke(until);
            return new Response(204, "text/plain", []);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return Text(400, "Bad request");
        }
    }

    /// <summary>{"action": "check"} looks for a new version now; {"action": "install"} installs it.</summary>
    private Response PostUpdate(Request req)
    {
        string action;
        try
        {
            using var doc = JsonDocument.Parse(req.Body);
            action = doc.RootElement.GetProperty("action").GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return Text(400, "Bad request");
        }
        if (action == "check") _ = _updater.CheckAsync(install: false);
        else if (action == "install") _ = _updater.InstallAsync();
        else return Text(400, "Bad action");
        return Json(WriteUpdate);   // both set "checking" / "downloading" before their first await
    }

    private void WriteUpdate(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("state", _updater.Status.ToString().ToLowerInvariant());
        w.WriteString("current", Updater.CurrentText);
        w.WriteString("repo", Updater.Repo);
        if (_updater.Latest is { } rel)
        {
            w.WriteString("latest", rel.VersionText);
            w.WriteString("name", rel.Name);
            w.WriteString("notes", rel.Notes.Length > 4000 ? rel.Notes[..4000] + "…" : rel.Notes);
            if (rel.PageUrl != null) w.WriteString("url", rel.PageUrl);
        }
        if (_updater.Status == Updater.State.Downloading) w.WriteNumber("percent", _updater.DownloadPercent);
        if (_updater.Error != null) w.WriteString("error", _updater.Error);
        if (_updater.LastCheck != default) w.WriteNumber("lastCheck", new DateTimeOffset(_updater.LastCheck).ToUnixTimeMilliseconds());
        w.WriteEndObject();
    }

    // ---- ActivityWatch import ----

    private Response ImportScan()
    {
        string folder = Native.DownloadsFolder();
        var files = ActivityWatchImport.FindFiles(folder);
        return Json(w =>
        {
            w.WriteStartObject();
            w.WriteString("folder", folder);
            w.WriteStartArray("files");
            foreach (var f in files)
            {
                w.WriteStartObject();
                w.WriteString("name", f.Name);
                w.WriteNumber("size", f.Length);
                w.WriteString("modified", f.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
                w.WriteString("kind", ActivityWatchImport.GuessKind(f.Name));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    private Response PostImport(Request req)
    {
        var names = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(req.Body);
            foreach (var n in doc.RootElement.GetProperty("files").EnumerateArray()) names.Add(n.GetString());
        }
        catch { return Text(400, "Bad request"); }

        string folder = Native.DownloadsFolder();
        var available = ActivityWatchImport.FindFiles(folder).ToDictionary(f => f.Name, f => f.FullName, StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>();
        foreach (var n in names)
        {
            // Only files the scan offered: no paths, nothing outside the Downloads folder.
            if (n == null || !available.TryGetValue(n, out var full)) return Text(400, "Unknown file: " + n);
            paths.Add(full);
        }
        if (paths.Count == 0) return Text(400, "No files selected");

        try
        {
            _tracker.Flush();
            var r = ActivityWatchImport.Run(_db, paths);
            return Json(w =>
            {
                w.WriteStartObject();
                w.WriteNumber("appMs", r.AppMs);
                w.WriteNumber("siteMs", r.SiteMs);
                w.WriteNumber("skippedMs", r.SkippedMs);
                w.WriteNumber("from", Db.DayOf(DateTimeOffset.FromUnixTimeMilliseconds(r.FromMs).LocalDateTime));
                w.WriteNumber("to", Db.DayOf(DateTimeOffset.FromUnixTimeMilliseconds(r.ToMs).LocalDateTime));
                w.WriteNumber("apps", r.Apps);
                w.WriteNumber("sites", r.Sites);
                if (r.Warning != null) w.WriteString("warning", r.Warning);
                w.WriteEndObject();
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or IOException or UnauthorizedAccessException)
        {
            return Text(400, ex.Message);
        }
    }

    private byte[] AppIcon(int appId)
    {
        try
        {
            string path = _db.AppPath(appId);
            if (path == null || !File.Exists(path)) return [];
            using var icon = Icon.ExtractAssociatedIcon(path);
            if (icon == null) return [];
            using var bmp = icon.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch { return []; }
    }

    private static int Int(Dictionary<string, string> q, string k, int def) =>
        q.TryGetValue(k, out var v) && int.TryParse(v, out int n) ? n : def;
}
