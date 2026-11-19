using System.Runtime.InteropServices;

namespace ScreenTime;

/// <summary>
/// Tells your own keyboard/mouse input apart from input a program sends (autoclickers, TinyTask-style macros,
/// remote-control tools). Windows flags input made with SendInput / keybd_event / mouse_event as "injected" in
/// low-level hooks; this watches that flag. The hooks run on their own thread with nothing else to do, so they
/// never make the mouse lag, and each event costs a couple of memory writes.
///
/// <see cref="LastRealInput"/> combines both sources: Windows' "last input" time counts unless the hook saw an
/// injected event with that exact time stamp, and the hook's newest real event counts too. When the hook can't
/// see input (an app running as administrator is in front), that falls back to Windows' time: no worse than before.
/// Programs that fake input through a driver aren't flagged by Windows, so they still look real.
/// </summary>
internal static class InputWatch
{
    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    private const int LLKHF_INJECTED = 0x10, LLMHF_INJECTED = 0x01;
    private const uint WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookExW(int id, HookProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);

    // Kept in static fields so the GC never collects a delegate Windows is still calling.
    private static readonly HookProc KeyProc = OnKey, MouseProc = OnMouse;
    private static readonly object Gate = new();
    private static Thread _thread;
    private static uint _threadId;
    private static volatile bool _running;
    private static int _realTick, _fakeTick;   // event time stamps (same clock as Environment.TickCount)
    private static uint _lastGili, _giliReal;

    public static bool Running => _running;

    public static void Start()
    {
        lock (Gate)
        {
            if (_thread is { IsAlive: true }) return;
            uint now = Native.LastInputTick();
            _lastGili = _giliReal = now;
            _realTick = unchecked((int)now);
            _fakeTick = unchecked((int)now - 100_000_000);
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                IntPtr module = GetModuleHandleW(null);
                IntPtr kb = SetWindowsHookExW(WH_KEYBOARD_LL, KeyProc, module, 0);
                IntPtr mouse = SetWindowsHookExW(WH_MOUSE_LL, MouseProc, module, 0);
                _running = kb != IntPtr.Zero && mouse != IntPtr.Zero;
                ready.Set();
                if (_running) while (GetMessageW(out _, IntPtr.Zero, 0, 0) > 0) { }
                _running = false;
                if (kb != IntPtr.Zero) UnhookWindowsHookEx(kb);
                if (mouse != IntPtr.Zero) UnhookWindowsHookEx(mouse);
            }) { IsBackground = true, Name = "InputWatch", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
            ready.Wait(2000);
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            if (_thread is not { IsAlive: true }) return;
            PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(2000);
            _thread = null;
        }
    }

    // KBDLLHOOKSTRUCT: vkCode, scanCode, flags (+8), time (+12). MSLLHOOKSTRUCT: pt (8 bytes), mouseData, flags (+12), time (+16).
    private static IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0) Mark((Marshal.ReadInt32(lParam, 8) & LLKHF_INJECTED) != 0, Marshal.ReadInt32(lParam, 12));
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private static IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0) Mark((Marshal.ReadInt32(lParam, 12) & LLMHF_INJECTED) != 0, Marshal.ReadInt32(lParam, 16));
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private static void Mark(bool injected, int time)
    {
        if (injected) Volatile.Write(ref _fakeTick, time);
        else Volatile.Write(ref _realTick, time);
    }

    /// <summary>
    /// Time of the last input that came from you, given Windows' last-input time <paramref name="gili"/>.
    /// Returns <paramref name="gili"/> unchanged while the hooks aren't running.
    /// </summary>
    public static uint LastRealInput(uint gili)
    {
        if (!_running) return gili;
        lock (Gate)
        {
            if (gili != _lastGili)
            {
                _lastGili = gili;
                if (Math.Abs(unchecked((int)(gili - (uint)Volatile.Read(ref _fakeTick)))) > 15) _giliReal = gili;
            }
            uint hook = unchecked((uint)Volatile.Read(ref _realTick));
            return unchecked((int)(hook - _giliReal)) > 0 ? hook : _giliReal;
        }
    }

    /// <summary>A program sent keyboard/mouse input within the last <paramref name="ms"/> milliseconds.</summary>
    public static bool FakeWithin(int ms) => _running && unchecked(Environment.TickCount - Volatile.Read(ref _fakeTick)) < ms;
}
