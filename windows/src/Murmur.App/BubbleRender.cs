using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
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
    internal async Task RenderStatesAsync(string folder)
    {
        Offscreen = true;
        Directory.CreateDirectory(folder);
        var report = new StringBuilder();
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
                report.AppendLine($"{name} @{(int)(scale * 100)}%: {rw}x{rh} px ({surface.ActualWidth:0.##}x{surface.ActualHeight:0.##} dip, raster scale {raster:0.##}), visible px {visible}, outermost-ring px {edgeOpaque}, non-blue visible px {nonBlue}" + (minimal ? (edgeOpaque == 0 && nonBlue <= visible / 200 ? " PASS" : " FAIL") : ""));
            }
        }
        report.AppendLine($"DWM chrome removal HRESULTs ok: {Native.RemoveWindowChrome(hwnd)}");
        File.WriteAllText(Path.Combine(folder, "render-report.txt"), report.ToString());
        Hide();
    }
}
