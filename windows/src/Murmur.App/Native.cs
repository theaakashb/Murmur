using System.Runtime.InteropServices;
using System.Text;

namespace Murmur.App;

/// <summary>The few Win32 calls the shell needs: window styles for the bubble, and a plain-text clipboard write for Copy buttons.</summary>
internal static class Native
{
    public const int GwlStyle = -16, GwlExStyle = -20;
    public const long WsExTopmost = 0x8, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExLayered = 0x80000, WsExNoActivate = 0x08000000;
    public static readonly nint HwndTopmost = -1;
    public const uint SwpNoActivate = 0x10, SwpShowWindow = 0x40;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    /// <summary>True when the window the user is looking at belongs to this process (Welcome, Home, Settings; the pill never takes focus).</summary>
    public static bool ForegroundIsThisProcess() => GetForegroundWindow() is var fg && fg != 0 && GetWindowThreadProcessId(fg, out var pid) != 0 && pid == (uint)Environment.ProcessId;
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] public static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
    [DllImport("gdi32.dll")] public static extern nint CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("user32.dll")] static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern nint SetClipboardData(uint format, nint mem);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormatW(string name);
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetKeyNameTextW(int lParam, StringBuilder text, int size);
    [DllImport("user32.dll")] static extern uint MapVirtualKeyW(uint code, uint type);
    [DllImport("kernel32.dll")] static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] static extern nint GlobalLock(nint mem);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(nint mem);

    /// <summary>The marker ClipboardPaste puts on the item it stages; its absence means the clipboard was restored (or replaced).</summary>
    public static bool MurmurClipboardItemPresent() => IsClipboardFormatAvailable(RegisterClipboardFormatW("Murmur.ClipboardOwner"));

    /// <summary>Plain Unicode text onto the clipboard without needing window focus (the bubble never takes focus).</summary>
    public static bool CopyText(string text)
    {
        for (int i = 0; i < 20; i++)
        {
            if (!OpenClipboard(0)) { Thread.Sleep(10); continue; }
            try
            {
                EmptyClipboard();
                var bytes = Encoding.Unicode.GetBytes(text + "\0");
                var h = GlobalAlloc(0x2, (nuint)bytes.Length);
                if (h == 0) return false;
                var p = GlobalLock(h);
                Marshal.Copy(bytes, 0, p, bytes.Length);
                GlobalUnlock(h);
                return SetClipboardData(13, h) != 0;
            }
            finally { CloseClipboard(); }
        }
        return false;
    }

    [DllImport("user32.dll")] static extern bool MessageBeep(uint type);
    public static void Cue(bool start) => MessageBeep(start ? 0x40u : 0u);

    public static string KeyName(int vk)
    {
        var sb = new StringBuilder(64);
        int scan = (int)MapVirtualKeyW((uint)vk, 0);
        bool extended = vk is 0xA3 or 0xA5 or 0x2D or 0x2E or 0x24 or 0x23 or 0x21 or 0x22 or 0x25 or 0x26 or 0x27 or 0x28 or 0x5B or 0x5C;
        if (scan != 0 && GetKeyNameTextW((scan << 16) | (extended ? 1 << 24 : 0), sb, sb.Capacity) > 0) return sb.ToString();
        return $"Key 0x{vk:X2}";
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    internal static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
