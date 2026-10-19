using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace ScreenTime;

/// <summary>
/// Updates from GitHub releases of <see cref="Repo"/>. A release is an update when its tag (e.g. "v1.5.0") is a
/// higher version than this build and it has a ScreenTime-Setup.exe asset. That setup is downloaded, checked,
/// and run with --update: it installs silently into the same folder, keeps the user's shortcuts and autostart
/// choice, and restarts Screen Time. Nothing is ever sent anywhere.
///
/// Everything is configurable in <see cref="Settings"/>: scheduled checks on/off and how often, install by itself
/// or ask first, install right away or only while the user is away, betas, notifications, and a skipped version.
/// </summary>
internal sealed class Updater
{
    /// <summary>The GitHub repository ("owner/name") whose releases are the updates. It must be public.</summary>
    public const string Repo = "hamzamake/screen-time";
    public const string AssetName = "ScreenTime-Setup.exe";
    private static readonly TimeSpan FirstTickAfter = TimeSpan.FromMinutes(2), TickEvery = TimeSpan.FromMinutes(1);

    public static readonly Version Current = Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));
    public static string CurrentText => Text(Current);
    public static readonly string Folder = Path.Combine(Path.GetTempPath(), "ScreenTime-Update");
    /// <summary>Written by the setup when an --update install fails (read and removed on the next start).</summary>
    private static readonly string ErrorFile = Path.Combine(Folder, "last-error.txt");

    public sealed record Release(Version Version, string Name, string Notes, string PageUrl, string AssetUrl, long Size,
                                 string Sha256, bool Prerelease)
    {
        public string VersionText => Text(Version);
    }

    /// <summary>Ready: downloaded and checked, waiting to install (until the user is away, or told to).</summary>
    public enum State { Idle, Checking, UpToDate, Available, Downloading, Ready, Installing, Failed }

    private readonly Settings _settings;
    private readonly Func<bool> _isAway;
    private System.Threading.Timer _timer;
    private int _busy;
    private string _downloaded;   // verified setup of Latest, when Status is Ready

    public State Status { get; private set; } = State.Idle;
    /// <summary>A newer release, once a check found one.</summary>
    public Release Latest { get; private set; }
    public string Error { get; private set; }
    public int DownloadPercent { get; private set; }
    public DateTime LastCheck => _settings.UpdateLastCheck;
    /// <summary>The user chose to skip <see cref="Latest"/>.</summary>
    public bool Skipped => Latest != null && Latest.VersionText == _settings.UpdateSkip;

    /// <summary>Fires after any change of <see cref="Status"/>, on a thread-pool thread.</summary>
    public event Action Changed;

    /// <param name="isAway">True while the user is idle or the PC is locked (for "install when I'm away").</param>
    public Updater(Settings settings, Func<bool> isAway)
    {
        _settings = settings;
        _isAway = isAway;
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

    public void Start() => _timer = new System.Threading.Timer(_ => OnTimer(), null, FirstTickAfter, TickEvery);

    public void Stop() => _timer?.Dispose();

    /// <summary>Once a minute: install a downloaded update when it's time, and check when a check is due.</summary>
    private void OnTimer()
    {
        if (Volatile.Read(ref _busy) != 0) return;
        if (Status == State.Ready && _settings.AutoUpdate && !Skipped && InstallTimeNow())
        {
            _ = InstallAsync();
            return;
        }
        if (!_settings.UpdateCheck) return;
        // After a failure, try again within the hour rather than waiting a whole interval.
        int hours = Status == State.Failed ? 1 : _settings.UpdateHours;
        var since = DateTime.Now - LastCheck;
        if (since >= TimeSpan.FromHours(hours) || since < TimeSpan.Zero) _ = CheckAsync();
    }

    private bool InstallTimeNow() => _settings.UpdateWhen != "away" || _isAway();

    /// <summary>
    /// Looks for a new version now. What happens next follows the settings: with automatic updates on (and the
    /// version not skipped) it's downloaded, then installed right away or once the user is away.
    /// </summary>
    public async Task CheckAsync()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            Set(State.Checking);
            var rel = await FetchLatest(_settings.UpdatePrerelease).ConfigureAwait(false);
            _settings.UpdateLastCheck = DateTime.Now;
            Error = null;
            if (rel == null || Latest == null || rel.Version != Latest.Version) _downloaded = null;
            Latest = rel;
            CleanUp(_downloaded);
            if (rel == null) { Set(State.UpToDate); return; }
            Set(_downloaded != null ? State.Ready : State.Available);
            if (!_settings.AutoUpdate || Skipped) return;
            if (_downloaded == null) await DownloadLocked().ConfigureAwait(false);
            if (InstallTimeNow()) RunSetup();
            else Set(State.Ready);   // the minute timer installs it once the user is away
        }
        catch (Exception ex) { Fail(ex); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>"Update now": installs the newest version right away (looking for it first if needed), even if skipped.</summary>
    public async Task InstallAsync()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            if (Latest == null)
            {
                Set(State.Checking);
                Latest = await FetchLatest(_settings.UpdatePrerelease).ConfigureAwait(false);
                _settings.UpdateLastCheck = DateTime.Now;
                if (Latest == null) { Set(State.UpToDate); return; }
            }
            if (Skipped) _settings.UpdateSkip = "";
            if (_downloaded == null) await DownloadLocked().ConfigureAwait(false);
            RunSetup();
        }
        catch (Exception ex) { Fail(ex); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private async Task DownloadLocked()
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

        // Make sure this is really the Screen Time setup for that version before it can run.
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
        _downloaded = file;
    }

    /// <summary>Starts the downloaded setup. It asks this app to save and exit, installs, and starts the new version.</summary>
    private void RunSetup()
    {
        Set(State.Installing);
        Process.Start(new ProcessStartInfo(_downloaded, "--update") { UseShellExecute = false, WorkingDirectory = Folder });
    }

    private void Fail(Exception ex)
    {
        Error = Describe(ex);
        _downloaded = null;
        Set(State.Failed);
    }

    /// <summary>The highest-versioned release newer than this one that has the setup attached, or null.</summary>
    private static async Task<Release> FetchLatest(bool prerelease)
    {
        using var http = Client(TimeSpan.FromSeconds(30));
        using var res = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases?per_page=30").ConfigureAwait(false);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;   // repo not published (yet)
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStreamAsync().ConfigureAwait(false));
        Release best = null;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            bool pre = r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True;
            if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True || pre && !prerelease) continue;
            if (!TryParse(Str(r, "tag_name"), out var version) || version <= Current || best != null && version <= best.Version) continue;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                if (!string.Equals(Str(a, "name"), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                string digest = Str(a, "digest");
                best = new Release(version,
                    Str(r, "name") is { Length: > 0 } name ? name : "Screen Time " + Text(version),
                    Str(r, "body") ?? "",
                    Str(r, "html_url"),
                    Str(a, "browser_download_url"),
                    a.TryGetProperty("size", out var s) && s.TryGetInt64(out long size) ? size : 0,
                    digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null,
                    pre);
                break;
            }
        }
        return best;
    }

    private static HttpClient Client(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ScreenTime/" + CurrentText);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Removes setups downloaded earlier except <paramref name="keep"/> (one still running is skipped).</summary>
    private static void CleanUp(string keep)
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            foreach (var f in Directory.EnumerateFiles(Folder, "ScreenTime-Setup-*"))
                if (!string.Equals(f, keep, StringComparison.OrdinalIgnoreCase))
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
