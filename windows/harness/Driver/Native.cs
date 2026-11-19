using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Murmur.Harness.Driver;

/// <summary>Win32 input and clipboard primitives. Clipboard helpers never log text.</summary>
static class Native
{
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy, mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public int dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public int type; public INPUTUNION u; }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, int flags, UIntPtr extra);

    const int KEYEVENTF_EXTENDEDKEY = 1, KEYEVENTF_KEYUP = 2, KEYEVENTF_UNICODE = 4, KEYEVENTF_SCANCODE = 8;
    public const ushort VK_RCONTROL = 0xA3, VK_CONTROL = 0x11, VK_RETURN = 0x0D, VK_DELETE = 0x2E;

    static void Send(params INPUT[] i) { if (SendInput((uint)i.Length, i, Marshal.SizeOf<INPUT>()) != i.Length) throw new InvalidOperationException("SendInput blocked: " + Marshal.GetLastWin32Error()); }
    static INPUT Key(ushort vk, ushort scan, int flags) => new() { type = 1, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } } };

    /// <summary>Right Ctrl as hardware-style input (virtual key and scan code, extended bit set).</summary>
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    public static bool IsRightCtrlDown() => (GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0;
    public static void RightCtrl(bool down) =>
        Send(Key(VK_RCONTROL, (ushort)MapVirtualKey(VK_RCONTROL, 0), KEYEVENTF_EXTENDEDKEY | (down ? 0 : KEYEVENTF_KEYUP)));

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    /// <summary>Puts a window above every non-topmost window (an elevated window in front would swallow our click otherwise).</summary>
    public static void MakeTopmost(IntPtr h) => SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040); // NOSIZE|NOMOVE|SHOWWINDOW
    /// <summary>Left click at screen pixel coordinates (primary monitor). Clicking activates a window even when the foreground
    /// lock refuses SetForegroundWindow, for example while an elevated window is in front.</summary>
    public static void Click(int x, int y)
    {
        int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
        INPUT Mouse(int flags) => new() { type = 0, u = new INPUTUNION { mi = new MOUSEINPUT { dx = x * 65535 / w, dy = y * 65535 / h, dwFlags = flags } } };
        const int MOVE = 1, DOWN = 2, UP = 4, ABS = 0x8000;
        Send(Mouse(MOVE | ABS)); Thread.Sleep(50); Send(Mouse(DOWN | ABS)); Send(Mouse(UP | ABS));
    }

    public static void Chord(ushort mod, ushort vk)
    {
        Send(Key(mod, 0, 0)); Thread.Sleep(30); Send(Key(vk, 0, 0), Key(vk, 0, KEYEVENTF_KEYUP)); Thread.Sleep(30); Send(Key(mod, 0, KEYEVENTF_KEYUP));
    }
    public static void Tap(ushort vk) => Send(Key(vk, 0, 0), Key(vk, 0, KEYEVENTF_KEYUP));

    /// <summary>Types text as Unicode key events (layout independent).</summary>
    public static void TypeText(string text)
    {
        foreach (var c in text)
        {
            Send(Key(0, c, KEYEVENTF_UNICODE), Key(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
            Thread.Sleep(25);
        }
    }

    /// <summary>Brings a window to the foreground. Plain SetForegroundWindow first; only if the foreground lock refuses, an Alt tap
    /// (then Esc to leave the access-key mode that Alt starts in Notepad, which would swallow the next keystrokes).</summary>
    public static bool Focus(IntPtr h)
    {
        if (IsIconic(h)) ShowWindow(h, 9);
        if (GetForegroundWindow() == h) return true;
        SetForegroundWindow(h);
        for (int i = 0; i < 10 && GetForegroundWindow() != h; i++) Thread.Sleep(50);
        if (GetForegroundWindow() == h) return true;
        SwitchToThisWindow(h, true);
        for (int i = 0; i < 10 && GetForegroundWindow() != h; i++) Thread.Sleep(50);
        if (GetForegroundWindow() == h) return true;
        var fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _); var me = GetCurrentThreadId();
        AttachThreadInput(me, fgThread, true); SetForegroundWindow(h); BringWindowToTop(h); AttachThreadInput(me, fgThread, false);
        for (int i = 0; i < 10 && GetForegroundWindow() != h; i++) Thread.Sleep(50);
        if (GetForegroundWindow() == h) return true;
        keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        SetForegroundWindow(h);
        for (int i = 0; i < 20 && GetForegroundWindow() != h; i++) Thread.Sleep(50);
        Tap(0x1B);
        return GetForegroundWindow() == h;
    }
    public static string ForegroundProcess()
    {
        try
        {
            var h = GetForegroundWindow(); GetWindowThreadProcessId(h, out var pid);
            var title = new StringBuilder(80); GetWindowText(h, title, 80);
            return $"{System.Diagnostics.Process.GetProcessById((int)pid).ProcessName} pid {pid} title '{title}'";
        }
        catch (Exception) { return "unknown"; }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern void SwitchToThisWindow(IntPtr h, bool alt);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    // ---- clipboard ----
    [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint format, IntPtr mem);
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClipboardFormatName(uint format, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr h);
    const uint CF_UNICODETEXT = 13, GMEM_MOVEABLE = 2;

    public static readonly string[] ExclusionFormats =
        { "ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" };

    public static bool Open(IntPtr owner) { for (int i = 0; i < 20; i++) { if (OpenClipboard(owner)) return true; Thread.Sleep(15); } return false; }
    public static void Close() => CloseClipboard();

    /// <summary>Replaces the clipboard with text, optionally adding the three exclusion formats (DWORD 0).</summary>
    public static void SetClipboard(string text, bool withExclusions)
    {
        if (!Open(IntPtr.Zero)) throw new InvalidOperationException("clipboard busy");
        try
        {
            EmptyClipboard();
            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            SetClipboardData(CF_UNICODETEXT, Alloc(bytes));
            if (withExclusions) foreach (var n in ExclusionFormats) SetClipboardData(RegisterClipboardFormat(n), Alloc(new byte[4]));
        }
        finally { CloseClipboard(); }
    }
    static IntPtr Alloc(byte[] b)
    {
        var h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)b.Length);
        var p = GlobalLock(h); Marshal.Copy(b, 0, p, b.Length); GlobalUnlock(h);
        return h;
    }

    /// <summary>Clipboard text, or null. Opens and closes the clipboard itself.</summary>
    public static string? GetClipboardText()
    {
        if (!Open(IntPtr.Zero)) return null;
        try { return ReadTextOpen(); } finally { CloseClipboard(); }
    }
    /// <summary>Call only while the clipboard is open.</summary>
    public static string? ReadTextOpen()
    {
        var h = GetClipboardData(CF_UNICODETEXT);
        if (h == IntPtr.Zero) return null;
        var p = GlobalLock(h);
        try { return Marshal.PtrToStringUni(p); } finally { GlobalUnlock(h); }
    }
    /// <summary>Format names on the open clipboard (standard formats as CF_n). Never reads content.</summary>
    public static List<string> FormatNamesOpen()
    {
        var names = new List<string>();
        for (uint f = EnumClipboardFormats(0); f != 0; f = EnumClipboardFormats(f))
        {
            var sb = new StringBuilder(256);
            names.Add(GetClipboardFormatName(f, sb, 256) > 0 ? sb.ToString() : "CF_" + f);
        }
        return names;
    }

    // ---- clipboard format listener window ----
    delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASS { public uint style; public WndProc proc; public int cls, wnd; public IntPtr inst, icon, cursor, bg; public string? menu, name; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr w, l; public uint time; public int x, y; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassW(ref WNDCLASS c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(int ex, string cls, string? title, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr p);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG m, IntPtr h, uint a, uint b);
    [DllImport("user32.dll")] static extern bool DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr h);
    [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr h);
    const uint WM_CLIPBOARDUPDATE = 0x031D, WM_CLOSE = 0x10;

    /// <summary>Runs a message-only window on its own thread; calls onUpdate (on that thread) per clipboard change. Dispose to stop.</summary>
    public sealed class ClipboardListener : IDisposable
    {
        readonly Thread thread; IntPtr hwnd; readonly ManualResetEventSlim ready = new(); WndProc? keep;
        public ClipboardListener(Action<IntPtr> onUpdate)
        {
            thread = new Thread(() =>
            {
                keep = (h, m, w, l) => { if (m == WM_CLIPBOARDUPDATE) onUpdate(h); return DefWindowProcW(h, m, w, l); };
                var wc = new WNDCLASS { proc = keep, name = "MurmurHarnessClip" + Environment.ProcessId };
                RegisterClassW(ref wc);
                hwnd = CreateWindowExW(0, wc.name!, null, 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                AddClipboardFormatListener(hwnd);
                ready.Set();
                while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0) DispatchMessageW(ref msg);
            }) { IsBackground = true, Name = "clip-listener" };
            thread.Start(); ready.Wait();
        }
        public void Dispose() { RemoveClipboardFormatListener(hwnd); PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }
    }
}
