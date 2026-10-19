using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ScreenTime;

/// <summary>
/// Updates from GitHub releases. Every few hours it asks GitHub for the latest release of <see cref="Repo"/>;
/// if its tag (e.g. "v1.5.0") is a higher version than this build and it has a ScreenTime-Setup.exe asset,
/// that setup is downloaded, checked, and run with --update: it installs silently into the same folder,
/// keeps the user's shortcuts and autostart choice, and restarts Screen Time. Nothing else is ever sent.
/// </summary>
internal sealed class Updater
{
    /// <summary>The GitHub repository ("owner/name") whose releases are the updates. It must be public.</summary>
    public const string Repo = "hamzamake/screen-time";
    public const string AssetName = "ScreenTime-Setup.exe";
    private static readonly TimeSpan FirstCheckAfter = TimeSpan.FromMinutes(2), CheckEvery = TimeSpan.FromHours(6);

    public static readonly Version Current = Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));
    public static string CurrentText => Text(Current);
    public static readonly string Folder = Path.Combine(Path.GetTempPath(), "ScreenTime-Update");
    /// <summary>Written by the setup when an --update install fails (read and removed on the next start).</summary>
    private static readonly string ErrorFile = Path.Combine(Folder, "last-error.txt");

    public sealed record Release(Version Version, string Name, string Notes, string PageUrl, string AssetUrl, long Size, string Sha256)
    {
        public string VersionText => Text(Version);
    }

    public enum State { Idle, Checking, UpToDate, Available, Downloading, Installing, Failed }

    private readonly Settings _settings;
    private readonly object _sync = new();
    private System.Threading.Timer _timer;
    private int _busy;

    public State Status { get; private set; } = State.Idle;
    /// <summary>A newer release, once a check found one.</summary>
    public Release Latest { get; private set; }
    public string Error { get; private set; }
    public DateTime LastCheck { get; private set; }
    public int DownloadPercent { get; private set; }

    /// <summary>Fires after any change of <see cref="Status"/>, on a thread-pool thread.</summary>
    public event Action Changed;

    public Updater(Settings settings)
    {
        _settings = settings;
        try
        {
            if (File.Exists(ErrorFile))
            {
                Error = "The last update could not be installed: " + File.ReadAllText(ErrorFile).Trim();
                Status = State.Failed;
                File.Delete(ErrorFile);
            }
        }
        catch { }
    }

    public void Start() =>
        _timer = new System.Threading.Timer(_ => _ = CheckAsync(install: _settings.AutoUpdate), null, FirstCheckAfter, CheckEvery);

    public void Stop() => _timer?.Dispose();

    /// <summary>Asks GitHub for the latest release; installs it when <paramref name="install"/> and it's newer.</summary>
    public async Task CheckAsync(bool install)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            Set(State.Checking);
            CleanUp();
            Latest = await FetchLatest().ConfigureAwait(false);
            LastCheck = DateTime.Now;
            Error = null;
            if (Latest == null) { Set(State.UpToDate); return; }
            Set(State.Available);
            if (install) await InstallLocked().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LastCheck = DateTime.Now;
            Error = Describe(ex);
            Set(State.Failed);
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>Downloads and runs the setup of the release a check found (checks first if needed).</summary>
    public Task InstallAsync() => Latest == null ? CheckAsync(install: true) : InstallNowAsync();

    private async Task InstallNowAsync()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try { await InstallLocked().ConfigureAwait(false); }
        catch (Exception ex) { Error = Describe(ex); Set(State.Failed); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private async Task InstallLocked()
    {
        var rel = Latest;
        DownloadPercent = 0;
        Set(State.Downloading);
        Directory.CreateDirectory(Folder);
        string file = Path.Combine(Folder, $"ScreenTime-Setup-{rel.VersionText}.exe"), part = file + ".part";

        using (var http = Client(TimeSpan.FromMinutes(15)))
        using (var res = await http.GetAsync(rel.AssetUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
        {
            res.EnsureSuccessStatusCode();
            long total = res.Content.Headers.ContentLength ?? rel.Size;
            await using var src = await res.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n)).ConfigureAwait(false);
                done += n;
                int pct = total > 0 ? (int)(done * 100 / total) : 0;
                if (pct != DownloadPercent) { DownloadPercent = pct; Changed?.Invoke(); }
            }
        }

        // Make sure this is really the Screen Time setup for that version before running it.
        if (rel.Size > 0 && new FileInfo(part).Length != rel.Size) throw new InvalidDataException("The download was incomplete.");
        if (rel.Sha256 != null)
        {
            await using var fs = File.OpenRead(part);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(fs).ConfigureAwait(false));
            if (!hash.Equals(rel.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The download is damaged (checksum mismatch).");
        }
        var info = FileVersionInfo.GetVersionInfo(part);
        if (info.ProductName != "Screen Time" || !TryParse(info.ProductVersion, out var fileVersion) || fileVersion < rel.Version)
            throw new InvalidDataException($"The release's {AssetName} isn't the Screen Time {rel.VersionText} setup.");
        File.Move(part, file, true);

        Set(State.Installing);
        // The setup asks this app to save and exit, installs, and starts the new version.
        Process.Start(new ProcessStartInfo(file, "--update") { UseShellExecute = false, WorkingDirectory = Folder });
    }

    private static async Task<Release> FetchLatest()
    {
        using var http = Client(TimeSpan.FromSeconds(30));
        using var res = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest").ConfigureAwait(false);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;   // no release published yet
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStreamAsync().ConfigureAwait(false));
        var r = doc.RootElement;
        if (!TryParse(Str(r, "tag_name"), out var version) || version <= Current) return null;
        foreach (var a in r.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(Str(a, "name"), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            string digest = Str(a, "digest");
            return new Release(version,
                Str(r, "name") is { Length: > 0 } name ? name : "Screen Time " + Text(version),
                Str(r, "body") ?? "",
                Str(r, "html_url"),
                Str(a, "browser_download_url"),
                a.TryGetProperty("size", out var s) && s.TryGetInt64(out long size) ? size : 0,
                digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null);
        }
        return null;   // a release without the setup attached isn't an update
    }

    private static HttpClient Client(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ScreenTime/" + CurrentText);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Removes setups downloaded earlier (one may still be running right after an update; it's skipped).</summary>
    private static void CleanUp()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            foreach (var f in Directory.EnumerateFiles(Folder, "ScreenTime-Setup-*"))
                try { File.Delete(f); } catch { }
        }
        catch { }
    }

    private void Set(State s)
    {
        Status = s;
        Changed?.Invoke();
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } => "GitHub is limiting requests right now. Try again later.",
        HttpRequestException or TaskCanceledException => "Couldn't reach GitHub. Check your internet connection.",
        _ => ex.Message,
    };

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>"v1.5.0", "1.5", "1.5.0-beta" → 1.5.0.</summary>
    public static bool TryParse(string s, out Version v)
    {
        v = null;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) s = s[..cut];
        if (!Version.TryParse(s.Contains('.') ? s : s + ".0", out var parsed)) return false;
        v = Normalize(parsed);
        return true;
    }

    private static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
    private static string Text(Version v) => $"{v.Major}.{v.Minor}.{v.Build}";
}
