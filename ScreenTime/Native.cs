using System.Runtime.InteropServices;

namespace ScreenTime;

/// <summary>Thin Win32 bindings. Everything here is a cheap syscall, safe to call every second.</summary>
internal static class Native
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, [Out] char[] buffer, ref uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, [Out] char[] text, int maxCount);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>Tick count (Environment.TickCount, 32-bit) of the last keyboard/mouse input in this session.</summary>
    public static uint LastInputTick()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? info.dwTime : unchecked((uint)Environment.TickCount);
    }

    /// <summary>Full image path of a process, or null if it can't be queried.</summary>
    public static string ProcessPath(uint pid)
    {
        if (pid == 0) return null;
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];   // per call: this runs on both the UI thread and server threads
            uint size = (uint)buffer.Length;
            return QueryFullProcessImageNameW(h, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally { CloseHandle(h); }
    }

    // UWP apps are hosted by ApplicationFrameHost.exe; the real app owns a child window.
    private static uint _hostPid, _childPid;
    private static readonly EnumWindowsProc ChildFinder = (hwnd, _) =>
    {
        GetWindowThreadProcessId(hwnd, out uint p);
        if (p != _hostPid) { _childPid = p; return false; }
        return true;
    };

    public static uint FindUwpChildPid(IntPtr frame, uint hostPid)
    {
        _hostPid = hostPid;
        _childPid = 0;
        EnumChildWindows(frame, ChildFinder, IntPtr.Zero);
        return _childPid;
    }

    // ---------- open windows (for "open time") ----------

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hWnd, [Out] char[] name, int max);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

    private const uint GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const int DWMWA_CLOAKED = 14;

    private static readonly char[] ClassBuffer = new char[64];
    private static List<(IntPtr, uint)> _windowsOut;
    private static readonly EnumWindowsProc WindowCollector = (hwnd, _) =>
    {
        if (!IsWindowVisible(hwnd) || GetWindowTextLengthW(hwnd) == 0 || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
        if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return true;
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;   // hidden UWP / other desktop
        int n = GetClassNameW(hwnd, ClassBuffer, ClassBuffer.Length);
        var cls = new ReadOnlySpan<char>(ClassBuffer, 0, Math.Max(0, n));
        if (cls.SequenceEqual("Progman") || cls.SequenceEqual("WorkerW") || cls.StartsWith("Shell_") ) return true;   // desktop & taskbar
        GetWindowThreadProcessId(hwnd, out uint pid);
        _windowsOut.Add((hwnd, pid));
        return true;
    };

    /// <summary>
    /// Top-level windows a person would call "open": visible (minimized counts), titled, not owned
    /// popups, not tool windows, not cloaked. Apps that only live in the tray have none. UI thread only.
    /// </summary>
    public static void OpenWindows(List<(IntPtr Hwnd, uint Pid)> into)
    {
        into.Clear();
        _windowsOut = into;
        EnumWindows(WindowCollector, IntPtr.Zero);
        _windowsOut = null;
    }

    /// <summary>Exe file name and title of the window currently in front (either may be null).</summary>
    public static (string Exe, string Title) Foreground()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return (null, null);
        GetWindowThreadProcessId(hwnd, out uint pid);
        string path = ProcessPath(pid);
        var buf = new char[512];
        int n = GetWindowTextW(hwnd, buf, buf.Length);
        return (path != null ? Path.GetFileName(path) : null, n > 0 ? new string(buf, 0, n) : null);
    }

    /// <summary>The user's Downloads folder (respects it being moved to another drive).</summary>
    public static string DownloadsFolder()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out IntPtr p) == 0)
        {
            try { return Marshal.PtrToStringUni(p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}
