using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Murmur.Platform;

/// <summary>Why nothing was pasted. Mirrors the InsertionError texts in app/Insertion.swift; never carries clipboard or field content.</summary>
public enum PasteFailure
{
    EmptyText,
    PasswordField,
    ClipboardNotPreserved,
    ClipboardChangedDuringPreservation,
    ClipboardBusy,
    CannotPrepareText,
    CouldNotStage,
    DestinationChanged,
    OwnershipChanged,
    CannotPostInput,
    /// <summary>The focused window runs elevated and Murmur does not: Windows (UIPI) would drop the Ctrl+V silently.</summary>
    ElevatedTarget,
}

public readonly record struct PasteResult(PasteFailure? Failure, long PostedAtTimestamp)
{
    public bool Pasted => Failure is null;
}

/// <summary>
/// Clipboard paste with save/stage/restore, as app/Insertion.swift pasteThroughClipboard: save every readable format, stage the text
/// (kept out of clipboard history and cloud sync), post Ctrl+V, and restore the saved clipboard after the lease only if our item is still there.
/// Delayed-render formats that cannot be read, and GDI-handle formats (bitmaps, metafiles), are not saved and are lost on restore.
/// </summary>
public sealed class ClipboardPaste
{
    const uint CF_UNICODETEXT = 13, GMEM_MOVEABLE = 2;
    const int INPUT_KEYBOARD = 1, VK_CONTROL = 0x11, VK_V = 0x56, KEYEVENTF_KEYUP = 2;
    const int TOKEN_QUERY = 8, TokenElevation = 20;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public int type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll", SetLastError = true)] static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] static extern nint SetClipboardData(uint format, nint data);
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint RegisterClipboardFormatW(string name);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern uint MapVirtualKeyW(uint code, uint mapType);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll")] static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] static extern nint GlobalLock(nint mem);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(nint mem);
    [DllImport("kernel32.dll")] static extern nuint GlobalSize(nint mem);
    [DllImport("kernel32.dll")] static extern nint GlobalFree(nint mem);
    [DllImport("kernel32.dll", SetLastError = true)] static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(nint token, int cls, out int info, int length, out int returned);

    static readonly uint ExcludeFormat = RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
    static readonly uint HistoryFormat = RegisterClipboardFormatW("CanIncludeInClipboardHistory");
    static readonly uint CloudFormat = RegisterClipboardFormatW("CanUploadToCloudClipboard");
    static readonly uint OwnerFormat = RegisterClipboardFormatW("Murmur.ClipboardOwner");

    sealed record Saved(uint Format, byte[] Data);

    sealed class PendingRestore(Action action)
    {
        readonly object gate = new();
        Action? action = action;
        public Timer? Timer;
        public void Run() { Action? a; lock (gate) { a = action; action = null; } Timer?.Dispose(); a?.Invoke(); }
    }

    /// <summary>Test seam: replaces the SendInput Ctrl+V.</summary>
    internal Func<bool> PostInput { get; init; } = PostCtrlV;

    readonly TimeSpan lease;
    readonly Func<FocusInfo?> focus;
    readonly object pendingGate = new();
    PendingRestore? pending;

    /// <param name="lease">How long staged text stays on the clipboard after Ctrl+V (0.5 s, as the Mac).</param>
    /// <param name="focusProbe">Focus for the password and integrity checks; defaults to <see cref="FocusProbe.Capture"/> without text reads.</param>
    public ClipboardPaste(TimeSpan? lease = null, Func<FocusInfo?>? focusProbe = null)
    {
        this.lease = lease ?? TimeSpan.FromMilliseconds(500);
        focus = focusProbe ?? (() => FocusProbe.Capture(readText: false));
    }

    /// <summary>Restores the clipboard now if a paste's restore is still waiting. Call before the next paste and when Murmur quits.</summary>
    public void FlushRestore()
    {
        PendingRestore? p;
        lock (pendingGate) { p = pending; pending = null; }
        p?.Run();
    }

    /// <summary>Stages <paramref name="text"/>, posts Ctrl+V and returns without waiting for the restore. <paramref name="stillTarget"/> is checked once more after staging.</summary>
    public PasteResult Paste(string text, Func<bool>? stillTarget = null)
    {
        FlushRestore();
        if (text.Length == 0) return Refused(PasteFailure.EmptyText);
        var target = focus();
        if (target is { IsPassword: true }) return Refused(PasteFailure.PasswordField);
        if (target is not null && TargetIsElevated(target.ProcessId)) return Refused(PasteFailure.ElevatedTarget);

        List<Saved> saved;
        uint oldSeq = GetClipboardSequenceNumber();
        if (!TryOpen()) return Refused(PasteFailure.ClipboardBusy);
        try
        {
            if (!Save(out saved)) return Refused(PasteFailure.ClipboardNotPreserved);
            if (GetClipboardSequenceNumber() != oldSeq) return Refused(PasteFailure.ClipboardChangedDuringPreservation);
        }
        finally { CloseClipboard(); }

        var token = Encoding.Unicode.GetBytes(Guid.NewGuid().ToString("N") + "\0");
        uint stagedSeq;
        if (!TryOpen()) return Refused(PasteFailure.ClipboardBusy);
        try
        {
            if (GetClipboardSequenceNumber() != oldSeq) return Refused(PasteFailure.ClipboardChangedDuringPreservation);
            if (!EmptyClipboard()) return Refused(PasteFailure.CouldNotStage);
            var dword = new byte[4];
            if (!Put(CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + "\0")) || !Put(OwnerFormat, token)
                || !Put(ExcludeFormat, dword) || !Put(HistoryFormat, dword) || !Put(CloudFormat, dword))
            {
                // our own partial write: put the user's clipboard back
                EmptyClipboard();
                foreach (var s in saved) Put(s.Format, s.Data);
                return Refused(PasteFailure.CouldNotStage);
            }
        }
        finally { CloseClipboard(); }
        stagedSeq = GetClipboardSequenceNumber();

        bool posted = false;
        try
        {
            WaitForModifiersReleased();
            if (stillTarget is not null && !stillTarget()) return Refused(PasteFailure.DestinationChanged);
            if (!Owns(stagedSeq, token)) return Refused(PasteFailure.OwnershipChanged);
            if (!PostInput()) return Refused(PasteFailure.CannotPostInput);
            posted = true;
            long postedAt = Stopwatch.GetTimestamp();
            var restore = new PendingRestore(() => RestoreIfOwned(saved, stagedSeq, token));
            restore.Timer = new Timer(_ =>
            {
                lock (pendingGate) { if (ReferenceEquals(pending, restore)) pending = null; }
                restore.Run();
            }, null, lease, Timeout.InfiniteTimeSpan);
            lock (pendingGate) pending = restore;
            return new PasteResult(null, postedAt);
        }
        finally { if (!posted) RestoreIfOwned(saved, stagedSeq, token); }
    }

    static PasteResult Refused(PasteFailure why) => new(why, 0);

    static bool TryOpen()
    {
        for (int i = 0; i < 20; i++)
        {
            if (OpenClipboard(0)) return true;
            Thread.Sleep(10); // another app holds the clipboard open for a moment
        }
        return false;
    }

    /// <summary>Formats whose handle is not a global memory block (GDI objects), plus owner-display and private GDI ranges.</summary>
    static bool IsMemoryFormat(uint f) => f is not (2 or 3 or 9 or 14 or 0x80) && !(f >= 0x81 && f <= 0x8E) && !(f >= 0x300 && f <= 0x3FF);

    static bool Save(out List<Saved> saved)
    {
        saved = [];
        int seen = 0;
        for (uint f = EnumClipboardFormats(0); f != 0; f = EnumClipboardFormats(f))
        {
            seen++;
            if (!IsMemoryFormat(f)) continue;
            var h = GetClipboardData(f);
            if (h == 0) continue; // delayed render that failed, or not readable
            var size = (int)GlobalSize(h);
            var p = GlobalLock(h);
            if (p == 0) continue;
            try { var data = new byte[size]; Marshal.Copy(p, data, 0, size); saved.Add(new Saved(f, data)); }
            finally { GlobalUnlock(h); }
        }
        return seen == 0 || saved.Count > 0;
    }

    static bool Put(uint format, byte[] data)
    {
        var h = GlobalAlloc(GMEM_MOVEABLE, (nuint)Math.Max(data.Length, 1));
        if (h == 0) return false;
        var p = GlobalLock(h);
        if (p == 0) { GlobalFree(h); return false; }
        Marshal.Copy(data, 0, p, data.Length);
        GlobalUnlock(h);
        if (SetClipboardData(format, h) != 0) return true; // the clipboard owns the block now
        GlobalFree(h);
        return false;
    }

    /// <summary>Must be called with the clipboard open: the sequence number and our marker are checked against one stable clipboard.</summary>
    static bool OwnsOpen(uint seq, byte[] token)
    {
        if (GetClipboardSequenceNumber() != seq || !IsClipboardFormatAvailable(OwnerFormat)) return false;
        var h = GetClipboardData(OwnerFormat);
        if (h == 0 || (int)GlobalSize(h) < token.Length) return false;
        var p = GlobalLock(h);
        if (p == 0) return false;
        try
        {
            var actual = new byte[token.Length];
            Marshal.Copy(p, actual, 0, actual.Length);
            return actual.AsSpan().SequenceEqual(token);
        }
        finally { GlobalUnlock(h); }
    }

    static bool Owns(uint seq, byte[] token)
    {
        if (!TryOpen()) return false;
        try { return OwnsOpen(seq, token); }
        finally { CloseClipboard(); }
    }

    static void RestoreIfOwned(List<Saved> saved, uint seq, byte[] token)
    {
        if (!TryOpen()) return;
        try
        {
            if (!OwnsOpen(seq, token)) return; // someone else copied: leave their clipboard alone
            EmptyClipboard();
            foreach (var s in saved) Put(s.Format, s.Data);
        }
        finally { CloseClipboard(); }
    }

    /// <summary>Held Shift/Alt/Win/Ctrl (for example the paste-last chord) would turn Ctrl+V into another shortcut; wait briefly for release.</summary>
    static void WaitForModifiersReleased()
    {
        var limit = Stopwatch.StartNew();
        while (limit.ElapsedMilliseconds < 1500 && new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(vk => (GetAsyncKeyState(vk) & 0x8000) != 0)) Thread.Sleep(10);
    }

    static INPUT Key(int vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)vk, wScan = (ushort)MapVirtualKeyW((uint)vk, 0), dwFlags = up ? (uint)KEYEVENTF_KEYUP : 0 } },
    };

    static bool PostCtrlV()
    {
        INPUT[] inputs = [Key(VK_CONTROL, false), Key(VK_V, false), Key(VK_V, true), Key(VK_CONTROL, true)];
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    static readonly bool SelfElevated = IsElevated(Environment.ProcessId) == true;

    /// <summary>UIPI: an elevated target ignores our SendInput unless we are elevated too. Unknown (cannot open the process) counts as not elevated.</summary>
    internal static bool TargetIsElevated(int pid) => !SelfElevated && IsElevated(pid) == true;

    static bool? IsElevated(int pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == 0) return null;
        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out var token)) return null;
            try { return GetTokenInformation(token, TokenElevation, out int elevated, 4, out _) ? elevated != 0 : null; }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    /// <summary>Test hook: the text currently on the clipboard, or null.</summary>
    internal static string? ReadText()
    {
        if (!TryOpen()) return null;
        try
        {
            var h = GetClipboardData(CF_UNICODETEXT);
            if (h == 0) return null;
            var p = GlobalLock(h);
            try { return Marshal.PtrToStringUni(p); } finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    /// <summary>Test hook: replaces the clipboard with the given formats (registered format names or numeric ids).</summary>
    internal static void SetFormats(params (uint Format, byte[] Data)[] items)
    {
        if (!TryOpen()) throw new Win32Exception(Marshal.GetLastWin32Error());
        try { EmptyClipboard(); foreach (var (f, d) in items) Put(f, d); }
        finally { CloseClipboard(); }
    }

    internal static uint RegisterFormat(string name) => RegisterClipboardFormatW(name);

    /// <summary>Test hook: formats currently on the clipboard with their bytes.</summary>
    internal static Dictionary<uint, byte[]> Snapshot()
    {
        if (!TryOpen()) throw new Win32Exception(Marshal.GetLastWin32Error());
        try { Save(out var s); return s.ToDictionary(x => x.Format, x => x.Data); }
        finally { CloseClipboard(); }
    }

    internal static uint Exclude => ExcludeFormat;
    internal static uint History => HistoryFormat;
    internal static uint Cloud => CloudFormat;
    internal static uint Owner => OwnerFormat;
}
