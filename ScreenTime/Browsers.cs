using Microsoft.Win32;

namespace ScreenTime;

/// <summary>How a browser's extensions page is laid out (decides the instructions and pictures).</summary>
internal enum ExtensionPageLayout { ChromeStyle, EdgeStyle, Unsupported }

internal sealed record BrowserInfo(string Name, string ExePath, string ExtensionsUrl, ExtensionPageLayout Layout, bool IsDefault)
{
    public string Exe => Path.GetFileName(ExePath ?? "");
    public bool Supported => Layout != ExtensionPageLayout.Unsupported;

    /// <summary>Where the "Developer mode" switch is, in words.</summary>
    public string DevModeWhere => Layout == ExtensionPageLayout.EdgeStyle
        ? "in the menu on the left side of the page (near the bottom)"
        : "in the top-right corner of the page";

    public string LoadUnpackedWhere => Layout == ExtensionPageLayout.EdgeStyle
        ? "near the top of the page, next to “Installed extensions”"
        : "in the bar that appeared at the top-left of the page";
}

/// <summary>
/// Optional automatic install. Browsers only let programs add extensions that are published on their
/// store. Once this extension is published, put its store id here: the setup wizard then registers it
/// through the browser's official "external extensions" registry keys, and the browser asks the user to
/// turn it on. While these are empty, the wizard uses the guided manual steps instead.
/// </summary>
internal static class ExtensionStore
{
    public const string ChromeWebStoreId = "";   // used by Chrome and Brave
    public const string EdgeAddonsId = "";       // used by Edge

    private const string ChromeUpdateUrl = "https://clients2.google.com/service/update2/crx";
    private const string EdgeUpdateUrl = "https://edge.microsoft.com/extensionwebstorebase/v1/crx";

    /// <summary>Registry location + update URL for browsers that support external installs, if an id is configured.</summary>
    private static (string Key, string Id, string Url)? Target(BrowserInfo b) => b.Exe.ToLowerInvariant() switch
    {
        "chrome.exe" when ChromeWebStoreId != "" => (@"Software\Google\Chrome\Extensions\" + ChromeWebStoreId, ChromeWebStoreId, ChromeUpdateUrl),
        "brave.exe" when ChromeWebStoreId != "" => (@"Software\BraveSoftware\Brave-Browser\Extensions\" + ChromeWebStoreId, ChromeWebStoreId, ChromeUpdateUrl),
        "msedge.exe" when EdgeAddonsId != "" => (@"Software\Microsoft\Edge\Extensions\" + EdgeAddonsId, EdgeAddonsId, EdgeUpdateUrl),
        _ => null,
    };

    public static bool CanAutoInstall(BrowserInfo b) => Target(b) != null;

    public static bool TryAutoInstall(BrowserInfo b)
    {
        var t = Target(b);
        if (t == null) return false;
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(t.Value.Key);
            k.SetValue("update_url", t.Value.Url);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Removes the registrations again (used by the uninstaller).</summary>
    public static void RemoveAutoInstalls()
    {
        foreach (var (root, id) in new[]
        {
            (@"Software\Google\Chrome\Extensions\", ChromeWebStoreId),
            (@"Software\BraveSoftware\Brave-Browser\Extensions\", ChromeWebStoreId),
            (@"Software\Microsoft\Edge\Extensions\", EdgeAddonsId),
        })
            if (id != "") try { Registry.CurrentUser.DeleteSubKeyTree(root + id, false); } catch { }
    }
}

internal static class Browsers
{
    private static readonly Dictionary<string, (string Name, string Scheme, ExtensionPageLayout Layout)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome.exe"] = ("Google Chrome", "chrome", ExtensionPageLayout.ChromeStyle),
        ["msedge.exe"] = ("Microsoft Edge", "edge", ExtensionPageLayout.EdgeStyle),
        ["brave.exe"] = ("Brave", "brave", ExtensionPageLayout.ChromeStyle),
        ["vivaldi.exe"] = ("Vivaldi", "vivaldi", ExtensionPageLayout.ChromeStyle),
        ["opera.exe"] = ("Opera", "opera", ExtensionPageLayout.ChromeStyle),
        ["chromium.exe"] = ("Chromium", "chrome", ExtensionPageLayout.ChromeStyle),
        ["thorium.exe"] = ("Thorium", "chrome", ExtensionPageLayout.ChromeStyle),
        ["yandex.exe"] = ("Yandex Browser", "browser", ExtensionPageLayout.ChromeStyle),
        ["firefox.exe"] = ("Firefox", null, ExtensionPageLayout.Unsupported),
        ["librewolf.exe"] = ("LibreWolf", null, ExtensionPageLayout.Unsupported),
        ["waterfox.exe"] = ("Waterfox", null, ExtensionPageLayout.Unsupported),
        ["floorp.exe"] = ("Floorp", null, ExtensionPageLayout.Unsupported),
        ["zen.exe"] = ("Zen Browser", null, ExtensionPageLayout.Unsupported),
    };

    /// <summary>True for the exe names of known browsers (used to split browser time into websites).</summary>
    public static bool IsKnownBrowser(string exe) => exe != null && Known.ContainsKey(exe);

    /// <summary>Installed browsers (from the "Start menu internet" registrations), default browser first.</summary>
    public static List<BrowserInfo> Detect()
    {
        string defaultExe = DefaultBrowserPath();
        var found = new Dictionary<string, BrowserInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, path) in new[]
        {
            (Registry.CurrentUser, @"Software\Clients\StartMenuInternet"),
            (Registry.LocalMachine, @"Software\Clients\StartMenuInternet"),
            (Registry.LocalMachine, @"Software\WOW6432Node\Clients\StartMenuInternet"),
        })
        {
            try
            {
                using var root = hive.OpenSubKey(path);
                if (root == null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(sub);
                    using var cmd = k?.OpenSubKey(@"shell\open\command");
                    string exePath = ExeFromCommand(cmd?.GetValue(null) as string);
                    if (exePath == null || !File.Exists(exePath) || found.ContainsKey(exePath)) continue;
                    if (Path.GetFileName(exePath).Equals("iexplore.exe", StringComparison.OrdinalIgnoreCase)) continue;   // retired, no extensions
                    string display = (k.GetValue(null) as string)?.Trim();
                    found[exePath] = Make(exePath, display, string.Equals(exePath, defaultExe, StringComparison.OrdinalIgnoreCase));
                }
            }
            catch { }
        }
        if (defaultExe != null && File.Exists(defaultExe) && !found.ContainsKey(defaultExe))
            found[defaultExe] = Make(defaultExe, null, true);

        return found.Values
            .OrderByDescending(b => b.IsDefault).ThenByDescending(b => b.Supported).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Generic entry for a Chromium browser the list doesn't know.</summary>
    public static BrowserInfo Other() => new("Other Chromium browser", null, "chrome://extensions", ExtensionPageLayout.ChromeStyle, false);

    private static BrowserInfo Make(string exePath, string display, bool isDefault)
    {
        string exe = Path.GetFileName(exePath);
        if (Known.TryGetValue(exe, out var k))
        {
            string name = k.Name;
            if (exe.Equals("opera.exe", StringComparison.OrdinalIgnoreCase) && exePath.Contains("Opera GX", StringComparison.OrdinalIgnoreCase)) name = "Opera GX";
            return new BrowserInfo(name, exePath, k.Scheme != null ? k.Scheme + "://extensions" : null, k.Layout, isDefault);
        }
        // Unknown browser: most are Chromium-based and accept chrome://extensions.
        string fallback = string.IsNullOrEmpty(display) ? Path.GetFileNameWithoutExtension(exe) : display;
        return new BrowserInfo(fallback, exePath, "chrome://extensions", ExtensionPageLayout.ChromeStyle, isDefault);
    }

    private static string ExeFromCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        int exeEnd = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeEnd > 0 ? command[..(exeEnd + 4)] : null;
    }

    private static string DefaultBrowserPath()
    {
        try
        {
            using var choice = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            string progId = choice?.GetValue("ProgId") as string;
            if (string.IsNullOrEmpty(progId)) return null;
            using var cmd = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command");
            return ExeFromCommand(cmd?.GetValue(null) as string);
        }
        catch { return null; }
    }
}
