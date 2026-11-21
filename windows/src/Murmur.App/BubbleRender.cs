using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Murmur.App;

/// <summary>
/// Development aid (<c>--render-bubble &lt;folder&gt;</c>): renders every pill state offscreen with RenderTargetBitmap at 100% and 150%, saves PNGs,
/// and reports size and edge checks. Never takes the single-instance mutex, never reads user data, and parks its window far off every screen.
/// </summary>
internal sealed partial class Bubble
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetClientRect(nint hwnd, out Rect rect);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct Rect { public int L, T, R, B; }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern nint CreateCompatibleBitmap(nint dc, int w, int h);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteObject(nint obj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern int GetDIBits(nint dc, nint bmp, uint start, uint lines, byte[] bits, byte[] info, uint usage);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern nint GetDC(nint hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int ReleaseDC(nint hwnd, nint dc);

    /// <summary>The real composited window (this window only, never the desktop), via PrintWindow(PW_RENDERFULLCONTENT), saved as a PNG (opaque; GDI has no alpha).</summary>
    async Task CaptureWindowAsync(string file, StringBuilder report, string name)
    {
        GetClientRect(hwnd, out var rc);
        int w = Math.Max(1, rc.R), h = Math.Max(1, rc.B);
        var screen = GetDC(0);
        var dc = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(dc, bmp);
        bool ok = PrintWindow(hwnd, dc, 2);
        var info = new byte[40];
        BitConverter.GetBytes(40).CopyTo(info, 0); BitConverter.GetBytes(w).CopyTo(info, 4); BitConverter.GetBytes(-h).CopyTo(info, 8);
        BitConverter.GetBytes((short)1).CopyTo(info, 12); BitConverter.GetBytes((short)32).CopyTo(info, 14);
        var px = new byte[w * h * 4];
        GetDIBits(dc, bmp, 0, (uint)h, px, info, 0);
        SelectObject(dc, old); DeleteObject(bmp); DeleteDC(dc); ReleaseDC(0, screen);
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        int nonTransparentBlack = 0;
        for (int i = 0; i < px.Length; i += 4) if (px[i] != 0 || px[i + 1] != 0 || px[i + 2] != 0) nonTransparentBlack++;
        report.AppendLine($"{name} window capture {w}x{h} PrintWindow={ok} non-black px {nonTransparentBlack}");
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)w, (uint)h, 96, 96, px);
        await encoder.FlushAsync();
        stream.Seek(0);
        var bytes = new byte[stream.Size];
        await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
        File.WriteAllBytes(file, bytes);
    }

    internal async Task RenderStatesAsync(string folder)
    {
        Offscreen = true;
        Directory.CreateDirectory(folder);
        var report = new StringBuilder();
        report.AppendLine($"FRAME before chrome removal (as the presenter leaves it): {FrameBefore}");
        var states = new (string Name, Action Show, bool Minimal)[]
        {
            ("listening", ShowListening, true),
            ("listening-voice", () => { ShowListening(); SetLevel(0.6f); }, true),
            ("processing", () => ShowProcessing(), true),
            ("live-words", () => { ShowListening(); SetPartial("Add two eggs and a litre of milk to the shopping list"); }, false),
            ("notice", () => ShowNotice("Dictation saved", 60), false),
        };
        foreach (var (name, show, minimal) in states)
        {
            show();
            await Task.Delay(400);
            var pr = (OverlappedPresenter)window.AppWindow.Presenter;
            report.AppendLine($"{name} FRAME after show: {Native.DescribeFrame(hwnd)}; presenter border {pr.HasBorder} titlebar {pr.HasTitleBar}");
            await CaptureWindowAsync(Path.Combine(folder, $"{name}-window.png"), report, name);
            foreach (var scale in new[] { 1.0, 1.5 })
            {
                // RenderAsync takes sizes in the XAML root's own units: ask for dip x scale / rasterization scale to get exactly dip x scale physical pixels.
                var raster = surface.XamlRoot.RasterizationScale;
                int w = (int)Math.Round(surface.ActualWidth * scale / raster), h = (int)Math.Round(surface.ActualHeight * scale / raster);
                var rtb = new RenderTargetBitmap();
                await rtb.RenderAsync(surface, w, h);
                var pixels = (await rtb.GetPixelsAsync()).ToArray();
                int rw = rtb.PixelWidth, rh = rtb.PixelHeight;
                var file = Path.Combine(folder, $"{name}-{(int)(scale * 100)}.png");
                using (var stream = new InMemoryRandomAccessStream())
                {
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                    encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)rw, (uint)rh, 96, 96, pixels);
                    await encoder.FlushAsync();
                    stream.Seek(0);
                    var bytes = new byte[stream.Size];
                    await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
                    File.WriteAllBytes(file, bytes);
                }
                // Edge check: the outermost ring of pixels, and (minimal) every visible pixel being the blue accent (R below G and B, no white or gray halo).
                int edgeOpaque = 0, visible = 0, nonBlue = 0;
                for (int y = 0; y < rh; y++)
                    for (int x = 0; x < rw; x++)
                    {
                        int i = (y * rw + x) * 4;
                        byte a = pixels[i + 3];
                        if (a == 0) continue;
                        // Un-premultiply, so anti-aliased edge pixels are judged by their colour, not their coverage.
                        byte b = (byte)Math.Min(255, pixels[i] * 255 / a), r = (byte)Math.Min(255, pixels[i + 2] * 255 / a);
                        visible++;
                        if (x == 0 || y == 0 || x == rw - 1 || y == rh - 1) edgeOpaque++;
                        if (!(b > r + 40)) nonBlue++;
                    }
                string corners = "";
                if (minimal)
                {
                    // Each bar's bounding box (runs of non-empty columns): the four box-corner pixels must be transparent or anti-aliased, never solid.
                    int bars = 0, solidCorners = 0, maxCorner = 0;
                    for (int x = 0; x < rw;)
                    {
                        int top = int.MaxValue, bottom = -1, x2 = x;
                        for (; x2 < rw; x2++)
                        {
                            bool any = false;
                            for (int y = 0; y < rh; y++) if (pixels[(y * rw + x2) * 4 + 3] > 0) { any = true; top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
                            if (!any) break;
                        }
                        if (x2 > x)
                        {
                            bars++;
                            foreach (var (cx, cy) in new[] { (x, top), (x2 - 1, top), (x, bottom), (x2 - 1, bottom) })
                            {
                                int a = pixels[(cy * rw + cx) * 4 + 3];
                                maxCorner = Math.Max(maxCorner, a);
                                if (a >= 128) solidCorners++;
                            }
                        }
                        x = x2 + 1;
                    }
                    corners = $", bars {bars}, bar-corner px max alpha {maxCorner}, solid corners {solidCorners}" + (bars == 5 && solidCorners == 0 ? " CORNERS-OK" : " CORNERS-FAIL");
                }
                report.AppendLine($"{name} @{(int)(scale * 100)}%: {rw}x{rh} px ({surface.ActualWidth:0.##}x{surface.ActualHeight:0.##} dip, raster scale {raster:0.##}), visible px {visible}, outermost-ring px {edgeOpaque}, non-blue visible px {nonBlue}{corners}" + (minimal ? (edgeOpaque == 0 && nonBlue <= visible / 200 ? " PASS" : " FAIL") : ""));
            }
        }
        report.AppendLine($"DWM chrome removal HRESULTs ok: {Native.RemoveWindowChrome(hwnd)}");
        File.WriteAllText(Path.Combine(folder, "render-report.txt"), report.ToString());
        Hide();
    }
}
