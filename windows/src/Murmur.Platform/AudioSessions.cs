using System.Diagnostics;
using System.Runtime.InteropServices;
using Murmur.Core;

namespace Murmur.Platform;

/// <summary>Audio sessions per process (IAudioSessionManager2) and the process list; the Windows input of the call detector and of target resolution. Reading them needs no permission.</summary>
public static class AudioSessions
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorClass { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(nint groupingParam, uint flags, out nint control);
        [PreserveSig] int GetSimpleAudioVolume(nint groupingParam, uint flags, out nint volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        // IAudioSessionControl's nine methods come first in the vtable.
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(nint name);
        [PreserveSig] int SetDisplayName(nint name, nint context);
        [PreserveSig] int GetIconPath(nint path);
        [PreserveSig] int SetIconPath(nint path, nint context);
        [PreserveSig] int GetGroupingParam(nint param);
        [PreserveSig] int SetGroupingParam(nint param, nint context);
        [PreserveSig] int RegisterAudioSessionNotification(nint events);
        [PreserveSig] int UnregisterAudioSessionNotification(nint events);
        [PreserveSig] int GetSessionIdentifier(nint id);
        [PreserveSig] int GetSessionInstanceIdentifier(nint id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    const int eRender = 0, eCapture = 1, DEVICE_STATE_ACTIVE = 1, CLSCTX_ALL = 23, AudioSessionStateActive = 1;
    static readonly Guid SessionManagerId = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    /// <summary>Processes with an active session on an active capture endpoint (Input) or render endpoint (Output). A process appears once per direction it uses. Empty when there are no audio devices.</summary>
    public static IReadOnlyList<AudioClient> Clients()
    {
        var found = new Dictionary<int, (bool Input, bool Output)>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorClass();
            foreach (var flow in new[] { eCapture, eRender })
                foreach (var pid in ActivePids(enumerator, flow))
                    found[pid] = found.TryGetValue(pid, out var seen) ? (seen.Input || flow == eCapture, seen.Output || flow == eRender) : (flow == eCapture, flow == eRender);
        }
        catch (Exception e) when (e is COMException or InvalidCastException) { return []; }
        return found.Select(item => new AudioClient(item.Key, item.Value.Input, item.Value.Output)).ToArray();
    }

    static IEnumerable<int> ActivePids(IMMDeviceEnumerator enumerator, int flow)
    {
        if (enumerator.EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, out var devices) != 0 || devices.GetCount(out uint count) != 0) yield break;
        for (uint i = 0; i < count; i++)
        {
            if (devices.Item(i, out var device) != 0) continue;
            var iid = SessionManagerId;
            if (device.Activate(ref iid, CLSCTX_ALL, 0, out var manager) != 0) continue;
            if (((IAudioSessionManager2)manager).GetSessionEnumerator(out var sessions) != 0 || sessions.GetCount(out int n) != 0) continue;
            for (int s = 0; s < n; s++)
            {
                if (sessions.GetSession(s, out var session) != 0) continue;
                if (session.GetState(out int state) == 0 && state == AudioSessionStateActive && session.GetProcessId(out uint pid) == 0 && pid != 0) yield return (int)pid;
            }
        }
    }

    // --- process list (Toolhelp) ---
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern nint CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] static extern bool Process32First(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] static extern bool Process32Next(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);

    public static IReadOnlyList<ProcessInfo> Processes()
    {
        const uint TH32CS_SNAPPROCESS = 2;
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == -1) return [];
        try
        {
            var list = new List<ProcessInfo>();
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (bool ok = Process32First(snapshot, ref entry); ok; ok = Process32Next(snapshot, ref entry))
                list.Add(new((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile));
            return list;
        }
        finally { CloseHandle(snapshot); }
    }

    public static int OwnPid => Environment.ProcessId;
}
