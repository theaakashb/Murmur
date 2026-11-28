using System.Runtime.InteropServices;
using Murmur.Platform;
using Xunit.Abstractions;

namespace Murmur.Platform.Tests;

/// <summary>
/// The pill window shows exactly the bitmap PillRenderer draws (UpdateLayeredWindow, per-pixel alpha), so these assertions on the bitmap are the
/// transparency guarantee: alpha 0 outside the shapes, antialiased edges, opaque interiors, valid premultiplied colour. Offscreen; touches no desktop.
/// </summary>
public sealed class PillRendererTests(ITestOutputHelper output) : IDisposable
{
    readonly PillRenderer renderer = new();
    public void Dispose() => renderer.Dispose();

    sealed record Shot(byte[] Px, int W, int H)
    {
        public (int A, int R, int G, int B) At(int x, int y) { int i = (y * W + x) * 4; return (Px[i + 3], Px[i + 2], Px[i + 1], Px[i]); }
        public int Count(Func<int, bool> alpha) { int n = 0; for (int i = 3; i < Px.Length; i += 4) if (alpha(Px[i])) n++; return n; }
    }

    Shot Render(PillModel m, double scale, float level = 0, double time = 1.0)
    {
        var lay = renderer.Layout(m, scale);
        var mem = Marshal.AllocHGlobal(lay.Width * lay.Height * 4);
        try
        {
            // Dirty memory: Draw must clear it, not rely on a fresh zeroed DIB.
            for (int i = 0; i < lay.Width * lay.Height * 4; i++) Marshal.WriteByte(mem, i, 0xCD);
            renderer.Draw(m, scale, new PillFrame(time, level), mem, lay.Width, lay.Height);
            var px = new byte[lay.Width * lay.Height * 4];
            Marshal.Copy(mem, px, 0, px.Length);
            return new Shot(px, lay.Width, lay.Height);
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    static void AssertPremultiplied(Shot s, string name)
    {
        for (int i = 0; i < s.Px.Length; i += 4)
        {
            int a = s.Px[i + 3];
            Assert.True(s.Px[i] <= a && s.Px[i + 1] <= a && s.Px[i + 2] <= a, $"{name}: pixel {i / 4} has colour above alpha ({s.Px[i + 2]},{s.Px[i + 1]},{s.Px[i]} a {a})");
        }
    }

    public static IEnumerable<object[]> Cards()
    {
        foreach (var dark in new[] { false, true })
            foreach (var scale in new[] { 1.0, 1.5, 2.0 })
            {
                var d = new PillModel(PillKind.Dots) { Dark = dark };
                yield return ["live-start", new PillModel(PillKind.LiveWords) { Dark = dark }, scale, 22];
                yield return ["live-words", new PillModel(PillKind.LiveWords) { Dark = dark, Settled = "Add two eggs and a litre of milk to the shopping ", Guess = "list" }, scale, 22];
                yield return ["live-two-lines", new PillModel(PillKind.LiveWords) { Dark = dark, Settled = "Add two eggs and a litre of milk to the shopping list and then call the plumber about the kitchen tap before ", Guess = "noon" }, scale, 22];
                yield return ["label", new PillModel(PillKind.DotsLabel) { Dark = dark, Text = "Editing with your Custom Provider…", Processing = true }, scale, 16];
                yield return ["notice", new PillModel(PillKind.Notice) { Dark = dark, Text = "Dictation saved" }, scale, 18];
                yield return ["error", new PillModel(PillKind.Error) { Dark = dark, Text = "The microphone is not available." }, scale, 18];
                yield return ["copy", new PillModel(PillKind.Copy) { Dark = dark, Title = "Couldn’t paste here", Preview = "Add two eggs and a litre of milk to the shopping list. Then call the plumber about the kitchen tap." }, scale, 18];
                yield return ["changes", new PillModel(PillKind.Changes) { Dark = dark, Text = "3 changes", UsedMode = true }, scale, 18];
                _ = d;
            }
    }

    [Theory, MemberData(nameof(Cards))]
    public void Card_corners_are_transparent_edges_are_antialiased_interior_is_opaque(string name, PillModel model, double scale, int radiusDip)
    {
        var s = Render(model, scale);
        var lay = renderer.Layout(model, scale);
        AssertPremultiplied(s, name);
        // Outside the rounded shape: the four corner pixels are alpha 0, not black.
        foreach (var (x, y) in new[] { (0, 0), (s.W - 1, 0), (0, s.H - 1), (s.W - 1, s.H - 1) })
            Assert.Equal((0, 0, 0, 0), s.At(x, y));
        // Every pixel outside the shape by more than a pixel of antialiasing is fully transparent.
        int stray = 0, partial = 0;
        for (int y = 0; y < s.H; y++)
            for (int x = 0; x < s.W; x++)
            {
                float r = lay.Radius;
                float cx = x + 0.5f < r ? r : x + 0.5f > s.W - r ? s.W - r : x + 0.5f;
                float cy = y + 0.5f < r ? r : y + 0.5f > s.H - r ? s.H - r : y + 0.5f;
                float dist = MathF.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                int a = s.At(x, y).A;
                if (dist > r + 1 && a != 0) stray++;
                if (a is > 0 and < 255) partial++;
            }
        Assert.Equal(0, stray);
        Assert.True(partial > 4 * (int)(radiusDip * scale) / 4, $"{name}: antialiased edge pixels {partial}");
        // The straight edge is the 1 px hairline (opaque), the interior the paper colour (opaque).
        Assert.Equal(255, s.At(s.W / 2, 0).A);
        var (pa, pr, pg, pb) = s.At(s.W / 2, (int)Math.Ceiling(2 * scale) + 1);
        Assert.Equal(255, pa);
        int paper = model.Dark ? 0x18 : 0xFA;
        Assert.True(pr == paper && pg == paper && pb == paper, $"{name}: interior colour {pr:X2}{pg:X2}{pb:X2}");
        // The window is exactly the card: its size is the design width in dip times the scale.
        Assert.True(s.W == (int)Math.Ceiling((name.StartsWith("live") ? 420 : name == "label" ? 300 : name == "error" ? 240 : name == "copy" ? 320 : name == "changes" ? 260 : 220) * scale));
        output.WriteLine($"{name} {(model.Dark ? "dark" : "light")} @{scale * 100}%: {s.W}x{s.H}, alpha0 {s.Count(a => a == 0)}, partial {partial}, opaque {s.Count(a => a == 255)}");
    }

    [Theory]
    [InlineData(1.0, false), InlineData(1.5, false), InlineData(2.0, true)]
    public void Dots_have_no_background_five_opaque_capsules_with_antialiased_edges(double scale, bool dark)
    {
        var model = new PillModel(PillKind.Dots) { Dark = dark };
        var s = Render(model, scale);
        AssertPremultiplied(s, "dots");
        Assert.Equal((int)Math.Ceiling(80 * scale), s.W);
        Assert.Equal((int)Math.Ceiling(40 * scale), s.H);
        // No background at all: every corner and the gaps between the bars are alpha 0.
        foreach (var (x, y) in new[] { (0, 0), (s.W - 1, 0), (0, s.H - 1), (s.W - 1, s.H - 1) }) Assert.Equal((0, 0, 0, 0), s.At(x, y));
        int mid = s.H / 2;
        int opaque = 0;
        for (int i = 0; i < 5; i++)
        {
            int cx = (int)((7.5 + 14 * i + 4.5) * scale);
            var (a, r, g, b) = s.At(cx, mid);
            Assert.Equal(255, a);
            Assert.True(Math.Abs(r - 0x4D) <= 1 && Math.Abs(g - 0x6F) <= 1 && Math.Abs(b - 0xF0) <= 1, $"bar {i} colour {r:X2}{g:X2}{b:X2}");
            // The gap right of the bar is transparent.
            if (i < 4) Assert.Equal((0, 0, 0, 0), s.At((int)((7.5 + 14 * i + 9 + 2.5) * scale), mid));
            // Its top row (13 dip tall, centred): fully clear above the capsule.
            Assert.Equal(0, s.At(cx, (int)(mid - 8 * scale)).A);
            opaque++;
        }
        int partial = s.Count(a => a is > 0 and < 255);
        Assert.True(partial > 40, $"antialiased edge pixels {partial}");
        Assert.True(s.Count(a => a == 0) > s.W * s.H / 2, "most of the window is transparent");
        // Capsule: the bar's corner pixel is clear, the cap's middle is solid.
        int bx = (int)(7.5 * scale), top = (int)(mid - 6.5 * scale);
        Assert.True(s.At(bx, top).A < 128, "square corner of a bar");
        Assert.Equal(5, opaque);
        output.WriteLine($"dots @{scale * 100}%: {s.W}x{s.H}, partial {partial}");
    }

    [Fact]
    public void Voice_level_grows_the_bars_by_weight_and_processing_dims_the_sweep()
    {
        var listening = new PillModel(PillKind.Dots) { Motion = false };
        var s = Render(listening, 1, level: 1);
        int Height(int bar) { int cx = (int)(7.5 + 14 * bar + 4.5); int n = 0; for (int y = 0; y < s.H; y++) if (s.At(cx, y).A >= 128) n++; return n; }
        Assert.InRange(Height(2), 38, 40);               // the middle bar: 13 + 27 x 1
        Assert.InRange(Height(0), 28, 31);               // weight 0.6: 13 + 27 x 0.6 = 29.2
        Assert.True(Height(1) > Height(0) && Height(2) > Height(1));
        var idle = Render(listening, 1, level: 0);
        int Idle(int bar) { int cx = (int)(7.5 + 14 * bar + 4.5); int n = 0; for (int y = 0; y < idle.H; y++) if (idle.At(cx, y).A >= 128) n++; return n; }
        Assert.InRange(Idle(2), 12, 14);

        var processing = new PillModel(PillKind.Dots) { Processing = true };
        var p = Render(processing, 1, time: 0.3);
        AssertPremultiplied(p, "processing");
        // The sweep highlights some bars: their alpha differs, and all are at least 35% opaque.
        var alphas = Enumerable.Range(0, 5).Select(i => p.At((int)(7.5 + 14 * i + 4.5), p.H / 2).A).ToArray();
        output.WriteLine("processing bar alphas " + string.Join(",", alphas));
        Assert.All(alphas, a => Assert.InRange(a, 88, 255));
        Assert.True(alphas.Max() - alphas.Min() > 30);
    }

    [Fact]
    public void Live_words_dim_only_the_last_word()
    {
        var m = new PillModel(PillKind.LiveWords) { Settled = "Add two eggs and a litre of milk ", Guess = "today" };
        var s = Render(m, 2.0);
        // Words sit in columns right of the bars; ink pixels (dark, light theme) exist, and so do dimmer ones: the guess.
        int ink = 0, dim = 0;
        for (int y = 0; y < s.H; y++)
            for (int x = (int)(86 * 2.0); x < s.W - 20; x++)
            {
                var (a, r, _, _) = s.At(x, y);
                if (a == 255 && r < 0x40) ink++;
                if (a == 255 && r is > 0x70 and < 0xB0) dim++;
            }
        Assert.True(ink > 200, $"ink pixels {ink}");
        Assert.True(dim > 20, $"dim pixels {dim}");
        // "Listening…" alone is all dim.
        var start = Render(new PillModel(PillKind.LiveWords), 2.0);
        int inkOnly = 0;
        for (int y = 0; y < start.H; y++) for (int x = (int)(86 * 2.0); x < start.W - 20; x++) if (start.At(x, y) is { A: 255, R: < 0x50 }) inkOnly++;
        Assert.True(inkOnly < 5, $"ink in the dim-only start state: {inkOnly}");
    }

    [Fact]
    public void Dark_theme_card_uses_the_dark_palette_and_the_critical_dot_is_red()
    {
        var s = Render(new PillModel(PillKind.Error) { Dark = true, Text = "Failed" }, 1);
        var (_, r, g, b) = s.At(1 + 10 + 4, s.H / 2);
        Assert.True(r > g + 40 && r > b + 30, $"error dot {r:X2}{g:X2}{b:X2}");
        var lay = renderer.Layout(new PillModel(PillKind.Error) { Dark = true, Text = "Failed" }, 1);
        Assert.True(lay.Height >= 22 + 8);
    }

    [Fact]
    public void Only_the_card_shape_and_its_buttons_take_clicks()
    {
        var copy = new PillModel(PillKind.Copy) { Title = "Not pasted", Preview = "Some words." };
        var lay = renderer.Layout(copy, 1);
        Assert.True(copy.Clickable);
        Assert.False(lay.InShape(0, 0));
        Assert.False(lay.InShape(lay.Width - 1, lay.Height - 1));
        Assert.True(lay.InShape(lay.Width / 2, lay.Height / 2));
        Assert.Equal(PillHit.Copy, lay.HitTest((int)(lay.CopyRect.X + 5), (int)(lay.CopyRect.Y + 5)));
        Assert.Equal(PillHit.None, lay.HitTest(lay.Width / 2, lay.Height / 2 - 4));
        Assert.False(new PillModel(PillKind.Dots).Clickable);
        Assert.False(new PillModel(PillKind.Notice).Clickable);
        Assert.False(new PillModel(PillKind.LiveWords).Clickable);
        var chip = renderer.Layout(new PillModel(PillKind.Changes) { Text = "2 changes" }, 1);
        Assert.Equal(PillHit.Open, chip.HitTest(14, 16));
        Assert.Equal(PillHit.Copy, chip.HitTest((int)(chip.CopyRect.X + 5), 16));
    }

    [Fact]
    public void Sizes_are_the_Mac_sizes_scaled()
    {
        Assert.Equal((80, 40), (renderer.Layout(new PillModel(PillKind.Dots), 1).Width, renderer.Layout(new PillModel(PillKind.Dots), 1).Height));
        Assert.Equal((120, 60), (renderer.Layout(new PillModel(PillKind.Dots), 1.5).Width, renderer.Layout(new PillModel(PillKind.Dots), 1.5).Height));
        Assert.Equal(40 + 2, renderer.Layout(new PillModel(PillKind.LiveWords), 1).Height);
    }
}
