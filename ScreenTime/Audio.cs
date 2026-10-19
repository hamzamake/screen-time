using System.Runtime.InteropServices;

namespace ScreenTime;

/// <summary>
/// Asks Windows' audio session manager whether a given app is producing sound right now
/// (e.g. a video or a call in the foreground app). Only used while the user is idle, and the
/// tracker calls it at most every few seconds.
///
/// The session managers are looked up fresh every time on purpose: a cached manager's session
/// list never shows sessions created after it (unless you register for COM notifications).
/// </summary>
internal static class Audio
{
    private static readonly List<IAudioSessionManager2> Managers = new();

    /// <summary>True if any process named <paramref name="exe"/> has an audio session with audible output.</summary>
    public static bool IsPlaying(string exe, Func<uint, string> pathOf)
    {
        if (exe == null) return false;
        try
        {
            LoadManagers();
            foreach (var m in Managers)
                if (HasSound(m, exe, pathOf)) return true;
        }
        catch { }
        finally
        {
            foreach (var m in Managers) Marshal.ReleaseComObject(m);
            Managers.Clear();
        }
        return false;
    }

    private static void LoadManagers()
    {
        IMMDeviceEnumerator enumerator = null;
        IMMDeviceCollection devices = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.EnumAudioEndpoints(0 /* eRender */, 1 /* DEVICE_STATE_ACTIVE */, out devices) != 0) return;
            devices.GetCount(out uint count);
            Guid iid = typeof(IAudioSessionManager2).GUID;
            for (uint i = 0; i < count; i++)
            {
                if (devices.Item(i, out IMMDevice device) != 0) continue;
                try
                {
                    if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object o) == 0) Managers.Add((IAudioSessionManager2)o);
                }
                finally { Marshal.ReleaseComObject(device); }
            }
        }
        finally
        {
            if (devices != null) Marshal.ReleaseComObject(devices);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
    }

    private static bool HasSound(IAudioSessionManager2 manager, string exe, Func<uint, string> pathOf)
    {
        IAudioSessionEnumerator sessions = null;
        try
        {
            if (manager.GetSessionEnumerator(out sessions) != 0) return false;
            sessions.GetCount(out int n);
            for (int i = 0; i < n; i++)
            {
                if (sessions.GetSession(i, out IAudioSessionControl2 s) != 0) continue;
                try
                {
                    if (s.GetState(out int state) != 0 || state != 1 /* Active */) continue;
                    if (s.GetProcessId(out uint pid) != 0 || pid == 0) continue;
                    string path = pathOf(pid);
                    if (path == null || !Path.GetFileName(path).Equals(exe, StringComparison.OrdinalIgnoreCase)) continue;
                    if (s is IAudioMeterInformation meter && meter.GetPeakValue(out float peak) == 0 && peak > 0.0005f) return true;
                }
                finally { Marshal.ReleaseComObject(s); }
            }
        }
        finally
        {
            if (sessions != null) Marshal.ReleaseComObject(sessions);
        }
        return false;
    }

    // ---- Core Audio COM interfaces (only the members we call, in vtable order) ----

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr groupingParam, int streamFlags, out IntPtr sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr groupingParam, int streamFlags, out IntPtr audioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, IntPtr eventContext);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, IntPtr eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(IntPtr groupingParam, IntPtr eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }
}
