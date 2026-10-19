using System.Diagnostics;
using Microsoft.Win32;

namespace ScreenTime;

/// <summary>App-side install support: the uninstaller, and stopping a running copy (used by setup and uninstall).</summary>
internal static class Installer
{
    public const string QuitEventName = @"Local\ScreenTimeTracker.Quit";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ScreenTime";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Asks a running Screen Time to save and exit, and waits for it (kills it after 8 s).</summary>
    public static void StopOtherInstances()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(QuitEventName, out var ev))
                using (ev) ev.Set();
        }
        catch { }
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("ScreenTime"))
            using (p)
            {
                if (p.Id == self) continue;
                try { if (!p.WaitForExit(8000)) p.Kill(); } catch { }
            }
    }

    public static void Uninstall()
    {
        var deleteData = new TaskDialogVerificationCheckBox("Also delete my screen time history", false);
        var uninstall = new TaskDialogButton("Uninstall");
        var page = new TaskDialogPage
        {
            Caption = "Screen Time",
            Heading = "Uninstall Screen Time?",
            Text = "Screen Time will stop tracking and be removed from this PC.\n\n" +
                   "Your history is kept unless you tick the box below, so reinstalling later picks up where you left off.",
            Buttons = { uninstall, TaskDialogButton.Cancel },
            DefaultButton = TaskDialogButton.Cancel,
            Verification = deleteData,
            Icon = TaskDialogIcon.Warning,
        };
        if (TaskDialog.ShowDialog(page) != uninstall) return;

        StopOtherInstances();
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, true);
            run?.DeleteValue("ScreenTime", false);
        }
        catch { }
        foreach (var lnk in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Screen Time.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Screen Time.lnk"),
        })
            try { File.Delete(lnk); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        ExtensionStore.RemoveAutoInstalls();
        if (deleteData.Checked) try { Directory.Delete(Program.DataDir, true); } catch { }

        TaskDialog.ShowDialog(new TaskDialogPage
        {
            Caption = "Screen Time",
            Heading = "Screen Time was removed",
            Text = "To also remove the browser extension, open your browser's extensions page and click “Remove” on “Screen Time Connector”." +
                   (deleteData.Checked ? "" : $"\n\nYour history is still in {Program.DataDir}."),
            Icon = TaskDialogIcon.ShieldSuccessGreenBar,
        });

        // Remove the program folder once this process has exited (a running exe can't delete itself).
        string dir = AppContext.BaseDirectory.TrimEnd('\\');
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ScreenTime");
        if (string.Equals(dir, expected, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(dir, "ScreenTime.exe")))
        {
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{dir}\"")
                    { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch { }
        }
    }
}
