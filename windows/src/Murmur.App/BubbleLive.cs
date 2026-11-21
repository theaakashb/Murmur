using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Murmur.App;

/// <summary>
/// Development aid (<c>--live-bubble &lt;folder&gt;</c>): puts the real, on-screen pill over a solid magenta window of its own, captures the monitor with
/// Windows.Graphics.Capture (what DWM really composes, unlike PrintWindow), crops to the magenta window in memory and discards the rest of the frame.
/// Only the crop is written. No mutex, no services, no user data. The pill's frame variant comes from MURMUR_PILL_VARIANT.
/// </summary>
internal sealed partial class Bubble
{
    [DllImport("d3d11.dll")] static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags, nint levels, uint count, uint sdk, out nint device, out int level, out nint context);
    [DllImport("d3d11.dll")] static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint device);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct WndClass { public uint Style; public nint Proc; public int ClsExtra, WndExtra; public nint Instance, Icon, Cursor, Brush; public string? Menu; public string Name; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassW(ref WndClass c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint CreateWindowExW(int ex, string cls, string title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] static extern nint DefWindowProcW(nint hwnd, uint msg, nint w, nint l);
    [DllImport("gdi32.dll")] static extern nint CreateSolidBrush(uint colorRef);
    [DllImport("kernel32.dll")] static extern nint GetModuleHandleW(nint name);

    /// <summary>A plain Win32 popup, solid magenta (RGB 255, 0, 255), topmost and never activated: the known backdrop the pill is captured over.</summary>
    static nint CreateBackdrop()
    {
        var inst = GetModuleHandleW(0);
        var proc = Marshal.GetFunctionPointerForDelegate<WndProc>(DefWindowProcW);
        keep = proc;
        var cls = new WndClass { Proc = proc, Instance = inst, Brush = CreateSolidBrush(0x00FF00FF), Name = "MurmurLiveBackdrop" };
        RegisterClassW(ref cls);
        var h = CreateWindowExW(0x8 | 0x80 | 0x08000000, "MurmurLiveBackdrop", "", 0x80000000, 0, 0, 100, 100, 0, 0, inst, 0);
        return h;
    }
    delegate nint WndProc(nint hwnd, uint msg, nint w, nint l);
    static nint keep;

    internal async Task LiveStatesAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var report = new StringBuilder();
        string variant = Environment.GetEnvironmentVariable("MURMUR_PILL_VARIANT") ?? "(default)";
        var all = DisplayArea.FindAll();
        var pick = Environment.GetEnvironmentVariable("MURMUR_LIVE_DISPLAY");
        if (int.TryParse(pick, out var which) && which < all.Count) LiveDisplay = () => all[which];
        report.AppendLine($"variant {variant}; displays {string.Join(" | ", Enumerable.Range(0, all.Count).Select(i => $"#{i} {all[i].OuterBounds.Width}x{all[i].OuterBounds.Height}@{all[i].OuterBounds.X},{all[i].OuterBounds.Y}"))}; pinned {pick ?? "none"}");
        report.AppendLine($"FRAME before: {FrameBefore}");
        report.AppendLine($"backdrop status at start: {ClearBackdrop.Status}");

        var backdropHwnd = CreateBackdrop();

        var states = new (string Name, Action Show, bool Card)[]
        {
            ("pill", ShowListening, false),
            ("live-words", () => { ShowListening(); SetPartial("Add two eggs and a litre of milk to the shopping list"); }, true),
            ("live-start", () => { ShowListening(); ShowLiveWords(); }, true),
            ("notice", () => ShowNotice("Dictation saved", 60), true),
            ("changes", () => ShowChanges(3, true, "original words"), true),
        };
        foreach (var (name, show, card) in states)
        {
            show();
            if (Environment.GetEnvironmentVariable("MURMUR_LIVE_HIDE") == "1") { await Task.Delay(300); Hide(); await Task.Delay(300); show(); }
            await Task.Delay(700);
            Native.GetWindowRect(hwnd, out var pillRect);
            const int pad = 40;
            var area = new Windows.Graphics.RectInt32(pillRect.L - pad, pillRect.T - pad, pillRect.R - pillRect.L + 2 * pad, pillRect.B - pillRect.T + 2 * pad);
            SetWindowPos(backdropHwnd, Native.HwndTopmost, area.X, area.Y, area.Width, area.Height, 0x10 | 0x40);
            await Task.Delay(500);
            // The pill goes back above the backdrop (both are topmost; the later one wins).
            SetWindowPos(hwnd, Native.HwndTopmost, 0, 0, 0, 0, 0x1 | 0x2 | 0x10);
            await Task.Delay(900);
            Native.GetWindowRect(hwnd, out pillRect);
            Native.GetWindowRect(backdropHwnd, out var bd);
            report.AppendLine($"{name} xaml: surface {surface.ActualWidth}x{surface.ActualHeight} bg {(surface.Background is null ? "none" : "set")}, bar0 {bars[0].ActualWidth}x{bars[0].ActualHeight} op {bars[0].Opacity} vis {bars[0].Visibility}, waveform {waveform.Visibility}, indicator {indicator.Visibility}, visible {visible}, win visible {Native.IsWindowVisible(hwnd)}");
            try { await CaptureCropAsync(folder, name, bd, pillRect, card ? (int)Math.Round(surface.CornerRadius.TopLeft * Math.Max(96, Native.GetDpiForWindow(hwnd)) / 96.0) : 0, report); }
            catch (Exception e) { report.AppendLine($"{name} CAPTURE-ERROR {e.GetType().Name}: {e.Message}"); }
        }
        report.AppendLine($"FRAME after: {Native.DescribeFrame(hwnd)}; backdrop status {ClearBackdrop.Status}");
        File.WriteAllText(Path.Combine(folder, $"live-report-{variant.Replace(',', '+')}.txt"), report.ToString());
        Hide();
        DestroyWindow(backdropHwnd);
    }

    async Task CaptureCropAsync(string folder, string name, Native.RectI backdrop, Native.RectI pill, int cornerPx, StringBuilder report)
    {
        var display = DisplayArea.GetFromRect(new Windows.Graphics.RectInt32(backdrop.L, backdrop.T, backdrop.R - backdrop.L, backdrop.B - backdrop.T), DisplayAreaFallback.Nearest);
        var item = GraphicsCaptureItem.TryCreateFromDisplayId(new Windows.Graphics.DisplayId { Value = display.DisplayId.Value }) ?? throw new InvalidOperationException("no capture item");
        D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out var d3d, out _, out _);
        var iidDxgi = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        Marshal.QueryInterface(d3d, in iidDxgi, out var dxgi);
        CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var abi);
        var device = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(abi);
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        try { session.IsBorderRequired = false; } catch { }
        session.StartCapture();
        Direct3D11CaptureFrame? frame = null;
        for (int i = 0; i < 60 && (frame = pool.TryGetNextFrame()) is null; i++) await Task.Delay(50);
        if (frame is null) { report.AppendLine($"{name} CAPTURE-ERROR no frame"); return; }
        byte[] full; int fw, fh;
        using (frame)
        {
            using var sb = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
            fw = sb.PixelWidth; fh = sb.PixelHeight;
            full = new byte[fw * fh * 4];
            sb.CopyToBuffer(full.AsBuffer());
        }
        // Crop to the backdrop in memory; the rest of the frame (the user's screen) is dropped right here.
        int ox = backdrop.L - display.OuterBounds.X, oy = backdrop.T - display.OuterBounds.Y;
        int w = backdrop.R - backdrop.L, h = backdrop.B - backdrop.T;
        var crop = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            if (oy + y >= 0 && oy + y < fh)
                for (int x = 0; x < w; x++)
                    if (ox + x >= 0 && ox + x < fw)
                        System.Buffer.BlockCopy(full, ((oy + y) * fw + ox + x) * 4, crop, (y * w + x) * 4, 4);
        Array.Clear(full);
        full = [];

        // Analysis. BGRA; the backdrop is exactly (R255, G0, B255).
        int px0 = pill.L - backdrop.L, py0 = pill.T - backdrop.T, pw = pill.R - pill.L, ph = pill.B - pill.T;
        bool Magenta(int x, int y) { int i = (y * w + x) * 4; return Math.Abs(crop[i] - 255) <= 2 && crop[i + 1] <= 2 && Math.Abs(crop[i + 2] - 255) <= 2; }
        bool Black(int x, int y) { int i = (y * w + x) * 4; return crop[i] < 64 && crop[i + 1] < 64 && crop[i + 2] < 64; }
        int outsideTotal = 0, outsideMagenta = 0, inNon = 0, inBlack = 0, ring = 0, ringBlack = 0, corner = 0, cornerBlack = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool inside = x >= px0 && x < px0 + pw && y >= py0 && y < py0 + ph;
                bool mag = Magenta(x, y), blk = Black(x, y);
                if (!inside) { outsideTotal++; if (mag) outsideMagenta++; continue; }
                int lx = x - px0, ly = y - py0;
                if (!mag) inNon++;
                if (blk) inBlack++;
                if (lx < 2 || ly < 2 || lx >= pw - 2 || ly >= ph - 2) { if (!mag) ring++; if (blk) ringBlack++; }
                if (cornerPx > 0)
                {
                    // Outside the card's rounded rectangle (one px of anti-aliasing slack): the four corner squares beyond the arc.
                    int cx = lx < cornerPx ? cornerPx : lx >= pw - cornerPx ? pw - cornerPx - 1 : -1, cy = ly < cornerPx ? cornerPx : ly >= ph - cornerPx ? ph - cornerPx - 1 : -1;
                    if (cx >= 0 && cy >= 0 && Math.Sqrt((lx - cx) * (lx - cx) + (ly - cy) * (ly - cy)) > cornerPx + 1) { if (!mag) corner++; if (blk) cornerBlack++; }
                }
            }
        report.AppendLine($"{name}: crop {w}x{h}, pill {pw}x{ph} at +{px0},{py0}, backdrop-only px magenta {outsideMagenta}/{outsideTotal}; inside pill: non-magenta {inNon}, near-black {inBlack}, outer-2px ring non-magenta {ring} (black {ringBlack}), outside-card-corner non-magenta {corner} (black {cornerBlack}), corner radius {cornerPx} px");

        // Write the crop only.
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)w, (uint)h, 96, 96, crop);
        await encoder.FlushAsync();
        stream.Seek(0);
        var bytes = new byte[stream.Size];
        await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
        File.WriteAllBytes(Path.Combine(folder, $"live-{(Environment.GetEnvironmentVariable("MURMUR_PILL_VARIANT") ?? "default").Replace(',', '+')}-{name}.png"), bytes);
    }
}
