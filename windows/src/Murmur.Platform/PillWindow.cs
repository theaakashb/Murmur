using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Murmur.Platform;

/// <summary>
/// The dictation pill's window: a plain Win32 popup with WS_EX_LAYERED whose content is the bitmap <see cref="PillRenderer"/> draws, handed to
/// UpdateLayeredWindow with per-pixel alpha (ULW_ALPHA, AC_SRC_ALPHA). No WinUI, no DWM frame, no border: the pixels outside the shapes are alpha 0.
/// Never activates. Click-through (WS_EX_TRANSPARENT) except while a card with buttons is up; then only the card's shape takes clicks.
/// Create and use it on the UI thread (it needs that thread's message loop); only <see cref="Level"/> may be written from another thread.
/// </summary>
public sealed class PillWindow : IDisposable
{
    const int WsPopup = unchecked((int)0x80000000);
    const int WsExTopmost = 0x8, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExLayered = 0x80000, WsExNoActivate = 0x08000000;
    const uint WmNcHitTest = 0x84, WmMouseActivate = 0x21, WmSetCursor = 0x20, WmLButtonUp = 0x202, WmLButtonDown = 0x201, WmTimer = 0x113, WmNcDestroy = 0x82;
    const int HtClient = 1, HtTransparent = -1;
    const nuint TimerId = 1;

    readonly PillRenderer renderer = new();
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly WndProc proc;
    readonly nint hwnd;
    PillModel? model;
    double scale = 1;
    int x, y, width, height;
    nint memDc, dib, oldBitmap, bits;
    bool visible, timerOn, disposed;
    PillHit pressed;
    volatile float level;

    /// <summary>The latest normalized microphone level (0 to 1); the animation reads it each frame.</summary>
    public float Level { get => level; set => level = value; }

    /// <summary>A card button was clicked.</summary>
    public Action<PillHit>? Clicked { get; set; }

    public nint Handle => hwnd;
    public bool Visible => visible;
    public PillModel? Model => model;

    delegate nint WndProc(nint hwnd, uint msg, nint w, nint l);

    public PillWindow()
    {
        proc = OnMessage;
        var instance = GetModuleHandleW(0);
        var cls = new WndClassEx { Size = (uint)Marshal.SizeOf<WndClassEx>(), Proc = Marshal.GetFunctionPointerForDelegate(proc), Instance = instance, Cursor = LoadCursorW(0, 32512), ClassName = "MurmurPillWindow" };
        RegisterClassExW(ref cls);
        hwnd = CreateWindowExW(WsExLayered | WsExTransparent | WsExTopmost | WsExToolWindow | WsExNoActivate, "MurmurPillWindow", "Murmur", WsPopup, 0, 0, 1, 1, 0, 0, instance, 0);
        if (hwnd == 0) throw new InvalidOperationException("The pill window could not be created.");
    }

    /// <summary>The window size in pixels for a model at a display scale (1.0 = 100%).</summary>
    public (int Width, int Height) Measure(PillModel m, double displayScale)
    {
        var lay = renderer.Layout(m, displayScale);
        return (lay.Width, lay.Height);
    }

    /// <summary>Shows (or updates) the pill with its top-left corner at the given screen pixel, without taking focus.</summary>
    public void Show(PillModel m, double displayScale, int left, int top)
    {
        model = m; scale = displayScale; x = left; y = top;
        var lay = renderer.Layout(m, displayScale);
        EnsureBitmap(lay.Width, lay.Height);
        // WS_EX_TRANSPARENT lets every click through; a card with buttons drops it and answers hit tests itself.
        long ex = (long)GetWindowLongPtrW(hwnd, -20);
        ex = m.Clickable ? ex & ~WsExTransparent : ex | WsExTransparent;
        SetWindowLongPtrW(hwnd, -20, (nint)ex);
        Paint(position: true);
        SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x40); // topmost, no move/size/activate, show
        visible = true;
        if (m.Animated && !timerOn) { SetTimer(hwnd, TimerId, 15, 0); timerOn = true; }
        else if (!m.Animated && timerOn) { KillTimer(hwnd, TimerId); timerOn = false; }
    }

    public void Hide()
    {
        if (timerOn) { KillTimer(hwnd, TimerId); timerOn = false; }
        if (!visible) return;
        visible = false;
        ShowWindow(hwnd, 0);
    }

    void EnsureBitmap(int w, int h)
    {
        if (dib != 0 && w == width && h == height) return;
        FreeBitmap();
        width = w; height = h;
        var screen = GetDC(0);
        memDc = CreateCompatibleDC(screen);
        var info = new BitmapInfoHeader { Size = 40, Width = w, Height = -h, Planes = 1, BitCount = 32 };
        dib = CreateDIBSection(memDc, ref info, 0, out bits, 0, 0);
        oldBitmap = SelectObject(memDc, dib);
        ReleaseDC(0, screen);
    }

    void FreeBitmap()
    {
        if (memDc != 0) { if (oldBitmap != 0) SelectObject(memDc, oldBitmap); DeleteDC(memDc); }
        if (dib != 0) DeleteObject(dib);
        memDc = dib = oldBitmap = bits = 0;
    }

    /// <summary>Draws the model into the DIB and hands it to the compositor: this is the whole display path.</summary>
    void Paint(bool position)
    {
        if (model is null || dib == 0 || bits == 0) return;
        renderer.Draw(model, scale, new PillFrame(clock.Elapsed.TotalSeconds, level), bits, width, height);
        var size = new Pt { X = width, Y = height };
        var src = new Pt();
        var dst = new Pt { X = x, Y = y };
        var blend = new Blend { Op = 0, Flags = 0, Alpha = 255, Format = 1 }; // AC_SRC_OVER, 255, AC_SRC_ALPHA
        if (position) UpdateLayeredWindow(hwnd, 0, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
        else UpdateLayeredWindow(hwnd, 0, 0, ref size, memDc, ref src, 0, ref blend, 2);
    }

    nint OnMessage(nint h, uint msg, nint w, nint l)
    {
        switch (msg)
        {
            case WmNcHitTest when model is { Clickable: true }:
            {
                int sx = (short)(l.ToInt64() & 0xFFFF), sy = (short)((l.ToInt64() >> 16) & 0xFFFF);
                GetWindowRect(h, out var r);
                return renderer.Layout(model, scale).InShape(sx - r.L, sy - r.T) ? HtClient : HtTransparent;
            }
            case WmMouseActivate:
                return 3; // MA_NOACTIVATE
            case WmSetCursor when model is { Clickable: true } && (l.ToInt64() & 0xFFFF) == HtClient:
            {
                GetCursorPos(out var p);
                GetWindowRect(h, out var r);
                SetCursor(LoadCursorW(0, renderer.Layout(model, scale).HitTest(p.X - r.L, p.Y - r.T) != PillHit.None ? 32649 : 32512));
                return 1;
            }
            case WmLButtonDown when model is { Clickable: true }:
                pressed = renderer.Layout(model, scale).HitTest((short)(l.ToInt64() & 0xFFFF), (short)((l.ToInt64() >> 16) & 0xFFFF));
                return 0;
            case WmLButtonUp when model is { Clickable: true }:
            {
                var hit = renderer.Layout(model, scale).HitTest((short)(l.ToInt64() & 0xFFFF), (short)((l.ToInt64() >> 16) & 0xFFFF));
                var down = pressed;
                pressed = PillHit.None;
                if (hit != PillHit.None && hit == down) Clicked?.Invoke(hit);
                return 0;
            }
            case WmTimer when w == (nint)TimerId:
                if (visible) Paint(position: false);
                return 0;
            case WmNcDestroy:
                break;
        }
        return DefWindowProcW(h, msg, w, l);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (timerOn) KillTimer(hwnd, TimerId);
        DestroyWindow(hwnd);
        FreeBitmap();
        renderer.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] struct Pt { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct Rc { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct Blend { public byte Op, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)] struct BitmapInfoHeader { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPels, YPels; public uint Used, Important; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WndClassEx { public uint Size, Style; public nint Proc; public int ClsExtra, WndExtra; public nint Instance, Icon, Cursor, Brush; public string? Menu; public string ClassName; public nint IconSm; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WndClassEx c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint CreateWindowExW(int ex, string cls, string title, int style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] static extern nint DefWindowProcW(nint hwnd, uint msg, nint w, nint l);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(nint hwnd, nint dstDc, ref Pt dst, ref Pt size, nint srcDc, ref Pt src, uint key, ref Blend blend, uint flags);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(nint hwnd, nint dstDc, nint dst, ref Pt size, nint srcDc, ref Pt src, uint key, ref Blend blend, uint flags);
    [DllImport("user32.dll")] static extern nuint SetTimer(nint hwnd, nuint id, uint ms, nint proc);
    [DllImport("user32.dll")] static extern bool KillTimer(nint hwnd, nuint id);
    [DllImport("user32.dll")] static extern nint LoadCursorW(nint instance, int id);
    [DllImport("user32.dll")] static extern nint SetCursor(nint cursor);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Pt p);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint hwnd, out Rc r);
    [DllImport("user32.dll")] static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);
    [DllImport("kernel32.dll")] static extern nint GetModuleHandleW(nint name);
}
