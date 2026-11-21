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
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out RectI value, int size);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hwnd, out RectI rect);
    [StructLayout(LayoutKind.Sequential)] public struct RectI { public int L, T, R, B; }

    // Non-client styles: caption (with border and dialog frame), thick frame, system menu, min/max boxes; and the extended edges.
    const long WsCaption = 0xC00000, WsThickFrame = 0x40000, WsSysMenu = 0x80000, WsMinimizeBox = 0x20000, WsMaximizeBox = 0x10000;
    const long WsExDlgModalFrame = 0x1, WsExWindowEdge = 0x100, WsExClientEdge = 0x200, WsExStaticEdge = 0x20000;
    const uint SwpNoSize = 1, SwpNoMove = 2, SwpNoZOrder = 4, SwpFrameChanged = 0x20;

    /// <summary>Makes the window fully frameless: no caption, thick frame or edge styles, no DWM border or shadow, square corners. Each call's HRESULT is checked; false means a call failed (an older Windows ignores the ones it does not know).</summary>
    public static bool RemoveWindowChrome(nint hwnd, bool stripStyles = true, bool disableNcRendering = false)
    {
        const int DwmwaNcRenderingPolicy = 2, DwmwaWindowCornerPreference = 33, DwmwaBorderColor = 34;
        bool ok = true;
        if (stripStyles)
        {
            long style = (long)GetWindowLongPtr(hwnd, GwlStyle) & ~(WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox);
            SetWindowLongPtr(hwnd, GwlStyle, (nint)style);
            long ex = (long)GetWindowLongPtr(hwnd, GwlExStyle) & ~(WsExDlgModalFrame | WsExWindowEdge | WsExClientEdge | WsExStaticEdge);
            SetWindowLongPtr(hwnd, GwlExStyle, (nint)ex);
            ok = SetWindowPos(hwnd, 0, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
        int none = unchecked((int)0xFFFFFFFE), doNotRound = 1, disabled = 1;
        ok &= DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref none, 4) >= 0;
        ok &= DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref doNotRound, 4) >= 0;
        if (disableNcRendering) ok &= DwmSetWindowAttribute(hwnd, DwmwaNcRenderingPolicy, ref disabled, 4) >= 0;
        return ok;
    }

    [DllImport("comctl32.dll")] static extern bool SetWindowSubclass(nint hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")] static extern nint DefSubclassProc(nint hwnd, uint msg, nint w, nint l);
    delegate nint SubclassProc(nint hwnd, uint msg, nint w, nint l, nuint id, nuint data);
    static SubclassProc? clickThrough;

    /// <summary>Click-through without WS_EX_LAYERED: while <paramref name="passClicks"/> is true the window answers hit tests with HTTRANSPARENT.</summary>
    public static void PassClicksThrough(nint hwnd, Func<bool> passClicks)
    {
        clickThrough = (h, msg, w, l, id, data) => msg == 0x84 && passClicks() ? -1 : DefSubclassProc(h, msg, w, l);
        SetWindowSubclass(hwnd, clickThrough, 1, 0);
    }

    [StructLayout(LayoutKind.Sequential)] struct Margins { public int L, R, T, B; }
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);

    /// <summary>Extends the DWM frame over the whole client area (margins -1): sheet-of-glass, per-pixel alpha from the content.</summary>
    public static bool ExtendFrameIntoClient(nint hwnd)
    {
        var m = new Margins { L = -1, R = -1, T = -1, B = -1 };
        return DwmExtendFrameIntoClientArea(hwnd, ref m) >= 0;
    }

    /// <summary>One line describing the window's frame, for the render report: styles, DWM attributes, and whether the window, DWM frame and client rectangles agree.</summary>
    public static string DescribeFrame(nint hwnd)
    {
        GetWindowRect(hwnd, out var win);
        int hr = DwmGetWindowAttribute(hwnd, 9, out RectI ext, 16);
        GetClientRect(hwnd, out var cli);
        DwmGetWindowAttribute(hwnd, 34, out int border, 4);
        DwmGetWindowAttribute(hwnd, 2, out int policy, 4);
        DwmGetWindowAttribute(hwnd, 33, out int corner, 4);
        string R(RectI r) => $"{r.R - r.L}x{r.B - r.T}@{r.L},{r.T}";
        return $"style 0x{(long)GetWindowLongPtr(hwnd, GwlStyle):X8} exstyle 0x{(long)GetWindowLongPtr(hwnd, GwlExStyle):X8}; window {R(win)} dwm-frame {R(ext)} (hr {hr:X}) client {cli.R - cli.L}x{cli.B - cli.T}; border colour 0x{border:X8}, nc policy {policy}, corner pref {corner}; non-client {(win.R - win.L) - (cli.R - cli.L)}x{(win.B - win.T) - (cli.B - cli.T)}, frame-vs-window delta {(ext.R - ext.L) - (win.R - win.L)}x{(ext.B - ext.T) - (win.B - win.T)}";
    }
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] static extern bool GetClientRect(nint hwnd, out RectI rect);
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
