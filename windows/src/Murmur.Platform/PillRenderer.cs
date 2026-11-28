using static Murmur.Platform.Gdip;

namespace Murmur.Platform;

public enum PillKind
{
    /// <summary>Just the five bars (listening, or the processing sweep). No background.</summary>
    Dots,
    /// <summary>Processing with a label ("Editing with your Custom Provider…"): a card with the bars and the text.</summary>
    DotsLabel,
    /// <summary>The live-words card: the bars, then the words (the last one dimmed), or a dim "Listening…".</summary>
    LiveWords,
    Notice,
    Error,
    /// <summary>The paste-failure card: title, the text, Copy.</summary>
    Copy,
    /// <summary>The changes chip: "N changes" (opens History) and "Copy original".</summary>
    Changes,
}

public enum PillHit { None, Copy, Dismiss, Open }

/// <summary>What the pill shows. Immutable; the renderer caches its layout per model and scale.</summary>
public sealed record PillModel(PillKind Kind)
{
    /// <summary>Dots only: the processing sweep instead of the level-driven wave.</summary>
    public bool Processing { get; init; }
    /// <summary>The label (DotsLabel), the notice or error text, or the chip's "N changes".</summary>
    public string Text { get; init; } = "";
    /// <summary>LiveWords: the settled words and the last (dimmed) word. Both empty shows a dim "Listening…".</summary>
    public string Settled { get; init; } = "";
    public string Guess { get; init; } = "";
    public string Title { get; init; } = "";
    public string Preview { get; init; } = "";
    /// <summary>Changes chip: a writing mode shaped the text (else the cleanup icon).</summary>
    public bool UsedMode { get; init; }
    public bool Dark { get; init; }
    /// <summary>False when the user turned animations off: still bars, no sweep.</summary>
    public bool Motion { get; init; } = true;

    public bool Animated => Kind is PillKind.Dots or PillKind.DotsLabel or PillKind.LiveWords;
    public bool Clickable => Kind is PillKind.Copy or PillKind.Changes;
}

/// <summary>The animated inputs of a frame: seconds on a monotonic clock and the microphone level (0 to 1).</summary>
public readonly record struct PillFrame(double Time, float Level);

/// <summary>Where things are, in physical pixels.</summary>
public sealed class PillLayout
{
    public int Width, Height;
    public float Radius;
    public bool Filled, Clickable;
    internal float Border, DotsX, DotsY = -1, Scale;
    internal RectF TextRect, TitleRect, PreviewRect, HintRect, DotRect, PasteIcon, ChipIcon, ChipText, CopyButton, DismissButton, OpenButton, CopyLabel;
    internal int TextLines, PreviewLines;

    public (float X, float Y, float W, float H) CopyRect => (CopyButton.X, CopyButton.Y, CopyButton.W, CopyButton.H);

    /// <summary>The pointer over the window: a button, or nothing.</summary>
    public PillHit HitTest(int x, int y)
    {
        static bool In(RectF r, int x, int y) => r.W > 0 && x >= r.X && x < r.X + r.W && y >= r.Y && y < r.Y + r.H;
        if (In(DismissButton, x, y)) return PillHit.Dismiss;
        if (In(CopyButton, x, y)) return PillHit.Copy;
        if (In(OpenButton, x, y)) return PillHit.Open;
        return PillHit.None;
    }

    /// <summary>True inside the pill's shape (the rounded rectangle): only that accepts clicks while the card is up.</summary>
    public bool InShape(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return false;
        float r = Radius;
        float cx = x + 0.5f < r ? r : x + 0.5f > Width - r ? Width - r : x + 0.5f;
        float cy = y + 0.5f < r ? r : y + 0.5f > Height - r ? Height - r : y + 0.5f;
        float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
        return dx * dx + dy * dy <= r * r;
    }
}

/// <summary>
/// Draws the dictation pill and its cards with GDI+ into a 32bpp premultiplied-alpha bitmap (the memory of a DIB section, or any buffer).
/// The pixels outside the shapes are alpha 0, the edges are antialiased, and the layered window shows exactly this bitmap.
/// Sizes are the Mac's (app/Bubble.swift) in device-independent pixels, multiplied by the display scale.
/// </summary>
public sealed class PillRenderer : IDisposable
{
    static readonly double[] BarWeights = [0.6, 0.85, 1, 0.85, 0.6];

    readonly Dictionary<(string, int, int), IntPtr> fonts = [];
    readonly Dictionary<IntPtr, float> lineHeights = [];
    readonly Dictionary<string, IntPtr> families = [];
    IntPtr scratchBitmap, scratch, fmtDraw, fmtMeasure, fmtCenter, fmtOne, scratchBits;
    PillModel? cachedModel;
    double cachedScale;
    PillLayout? cachedLayout;

    public PillRenderer()
    {
        EnsureStarted();
        scratchBits = System.Runtime.InteropServices.Marshal.AllocHGlobal(4 * 4);
        GdipCreateBitmapFromScan0(2, 2, 8, PixelFormat32bppPARGB, scratchBits, out scratchBitmap);
        GdipGetImageGraphicsContext(scratchBitmap, out scratch);
        GdipSetTextRenderingHint(scratch, TextHint);
        // Measure and draw with the same format: typographic (no side padding), whole lines, ellipsis on the last, trailing spaces measured.
        fmtDraw = Format(0x800 | 0x2000 | 0x4000 | 0x20000, 3);
        fmtMeasure = Format(0x800 | 0x4000 | 0x20000, 0);
        fmtOne = Format(0x1000 | 0x4000 | 0x20000, 1);
        fmtCenter = Format(0x1000 | 0x4000 | 0x20000, 0);
        GdipSetStringFormatAlign(fmtCenter, 1);
        GdipSetStringFormatLineAlign(fmtCenter, 1);
    }

    // TextRenderingHintAntiAliasGridFit: grayscale antialiasing, which is the only kind that carries alpha.
    const int TextHint = 3;

    static IntPtr Format(int flags, int trimming)
    {
        GdipStringFormatGetGenericTypographic(out var generic);
        GdipCloneStringFormat(generic, out var fmt);
        GdipSetStringFormatFlags(fmt, flags);
        GdipSetStringFormatTrimming(fmt, trimming);
        return fmt;
    }

    // ---- palette: the App.xaml Murmur* brushes (light and dark) ----
    static uint Argb(int a, int r, int g, int b) => (uint)(a << 24 | r << 16 | g << 8 | b);
    static uint Hex(uint rgb, int a = 255) => (uint)a << 24 | rgb;

    readonly record struct Palette(uint Paper, uint Rule, uint Ink, uint Muted, uint Dim, uint Accent, uint Critical, uint ButtonFill, uint OnAccent);

    static Palette PaletteFor(bool dark) => dark
        ? new(Hex(0x181818), Hex(0x393939), Hex(0xFAFAFA), Hex(0xBBBBBB), Argb(0xC5, 255, 255, 255), Hex(0x4D6FF0), Hex(0xFF99A4), Hex(0x2B2B2B), Hex(0xFFFFFF))
        : new(Hex(0xFAFAFA), Hex(0xDEDEDE), Hex(0x202020), Hex(0x555555), Argb(0x9E, 0, 0, 0), Hex(0x4D6FF0), Hex(0xC42B1C), Hex(0xFFFFFF), Hex(0xFFFFFF));

    // ---- fonts ----
    IntPtr Family(string preferred, string fallback)
    {
        if (families.TryGetValue(preferred, out var f)) return f;
        if (GdipCreateFontFamilyFromName(preferred, 0, out f) != 0 && GdipCreateFontFamilyFromName(fallback, 0, out f) != 0) f = 0;
        families[preferred] = f;
        return f;
    }

    /// <summary>The XAML text styles: Segoe UI Variable (Segoe UI before Windows 11), 13 px body, 14 title, 12 caption.</summary>
    IntPtr Font(string role, double dip, double scale)
    {
        int px = (int)Math.Round(dip * scale * 100);
        if (fonts.TryGetValue((role, px, 0), out var font)) return font;
        var family = role switch
        {
            "title" or "button" => Family("Segoe UI Variable Text Semibold", "Segoe UI Semibold"),
            "icon" => Family("Segoe Fluent Icons", "Segoe MDL2 Assets"),
            _ => Family("Segoe UI Variable Text", "Segoe UI"),
        };
        if (family == 0 && (family = Family("Segoe UI", "Arial")) == 0) return 0;
        GdipCreateFont(family, (float)(dip * scale), 0, 2, out font);
        fonts[(role, px, 0)] = font;
        return font;
    }

    float LineHeight(IntPtr font)
    {
        if (lineHeights.TryGetValue(font, out var h)) return h;
        var layout = new RectF(0, 0, 10000, 10000);
        GdipMeasureString(scratch, "Ag", -1, font, ref layout, fmtOne, out var b, out _, out _);
        return lineHeights[font] = Math.Max(1, b.H);
    }

    (float Width, float Height, int Lines) MeasureText(string s, IntPtr font, float width, int maxLines)
    {
        if (s.Length == 0 || font == 0) return (0, 0, 0);
        var layout = new RectF(0, 0, Math.Max(1, width), 10000);
        GdipMeasureString(scratch, s, -1, font, ref layout, fmtMeasure, out var b, out _, out int lines);
        int n = Math.Clamp(lines, 1, maxLines);
        return (Math.Min(b.W, width), n * LineHeight(font), n);
    }

    float TextWidth(string s, IntPtr font)
    {
        if (s.Length == 0 || font == 0) return 0;
        var layout = new RectF(0, 0, 10000, 10000);
        GdipMeasureString(scratch, s, -1, font, ref layout, fmtOne, out var b, out _, out _);
        return b.W;
    }

    // ---- layout (dip -> px) ----
    public PillLayout Layout(PillModel model, double scale)
    {
        if (cachedLayout is not null && ReferenceEquals(cachedModel, model) && cachedScale == scale) return cachedLayout;
        cachedLayout = Build(model, scale);
        cachedModel = model; cachedScale = scale;
        return cachedLayout;
    }

    static RectF R(double x, double y, double w, double h) => new((float)x, (float)y, (float)w, (float)h);

    PillLayout Build(PillModel m, double s)
    {
        var lay = new PillLayout { Scale = (float)s, Filled = m.Kind != PillKind.Dots, Clickable = m.Clickable };
        var body = Font("body", 13, s);
        float pxDip = (float)s;
        float border = lay.Filled ? pxDip : 0;
        lay.Border = border;
        switch (m.Kind)
        {
            case PillKind.Dots:
                lay.Width = (int)Math.Ceiling(80 * s); lay.Height = (int)Math.Ceiling(40 * s);
                lay.DotsX = 0; lay.DotsY = 0; lay.Radius = (float)(16 * s);
                break;
            case PillKind.DotsLabel:
            case PillKind.LiveWords:
            {
                // Width 300 (label) or 420 (words); the bars take the first 80 x 40, then the text with a 6 / 7 margin.
                double width = m.Kind == PillKind.LiveWords ? 420 : 300;
                float tx = border + (float)((80 + 6) * s), tw = (float)(width * s) - tx - (float)(7 * s) - border;
                string text = m.Kind == PillKind.LiveWords ? m.Settled + m.Guess : m.Text;
                if (m.Kind == PillKind.LiveWords && text.Length == 0) text = "Listening…";
                var mt = MeasureText(text, body, tw, m.Kind == PillKind.LiveWords ? 2 : 3);
                float inner = Math.Max((float)(40 * s), mt.Height);
                lay.Width = (int)Math.Ceiling(width * s); lay.Height = (int)Math.Ceiling(inner + 2 * border);
                lay.DotsX = border; lay.DotsY = border + (inner - (float)(40 * s)) / 2;
                lay.TextRect = new RectF(tx, border + (inner - mt.Height) / 2, tw, mt.Height);
                lay.TextLines = mt.Lines;
                lay.Radius = (float)((m.Kind == PillKind.LiveWords ? 22 : 16) * s);
                break;
            }
            case PillKind.Notice:
            case PillKind.Error:
            {
                double width = m.Kind == PillKind.Error ? 240 : 220;
                float pad = border + (float)(10 * s), dotW = (float)(8 * s), gap = (float)(8 * s);
                float tw = (float)(width * s) - 2 * pad - dotW - gap;
                var mt = MeasureText(m.Text, body, tw, 4);
                float inner = Math.Max(dotW, mt.Height);
                lay.Width = (int)Math.Ceiling(width * s); lay.Height = (int)Math.Ceiling(inner + 2 * pad);
                lay.DotRect = new RectF(pad, pad + (inner - dotW) / 2, dotW, dotW);
                lay.TextRect = new RectF(pad + dotW + gap, pad + (inner - mt.Height) / 2, tw, mt.Height);
                lay.TextLines = mt.Lines;
                lay.Radius = (float)(18 * s);
                break;
            }
            case PillKind.Copy:
            {
                float pad = border + (float)(10 * s), row = (float)(32 * s), gap = (float)(8 * s);
                float cw = (float)(320 * s) - 2 * pad;
                // Header: paste icon, title, dismiss. Footer: hint and Copy.
                lay.PasteIcon = new RectF(pad, pad, (float)(20 * s), row);
                lay.DismissButton = new RectF(pad + cw - row, pad, row, row);
                lay.TitleRect = new RectF(pad + (float)(28 * s), pad, cw - (float)(28 * s) - row - gap, row);
                float y = pad + row + gap;
                var mt = MeasureText(m.Preview, Font("body", 13, s), cw, 3);
                lay.PreviewRect = new RectF(pad, y, cw, mt.Height);
                lay.PreviewLines = mt.Lines;
                y += mt.Height + gap;
                float copyW = (float)(64 * s);
                lay.CopyButton = new RectF(pad + cw - copyW, y, copyW, row);
                lay.HintRect = new RectF(pad, y, cw - copyW - gap, row);
                lay.Width = (int)Math.Ceiling(320 * s); lay.Height = (int)Math.Ceiling(y + row + pad);
                lay.Radius = (float)(18 * s);
                break;
            }
            case PillKind.Changes:
            {
                float pad = border + (float)(10 * s), row = (float)(32 * s), gap = (float)(8 * s);
                float labelW = TextWidth(m.Text, body);
                float openW = (float)((4 + 16 + 8 + 4) * s) + labelW;
                lay.OpenButton = new RectF(pad, pad, openW, row);
                lay.ChipIcon = new RectF(pad + (float)(4 * s), pad, (float)(16 * s), row);
                lay.ChipText = new RectF(pad + (float)(28 * s), pad, labelW + 2, row);
                float copyW = TextWidth("Copy original", body) + (float)(18 * s);
                lay.CopyButton = new RectF(pad + openW + gap, pad, copyW, row);
                lay.Width = (int)Math.Ceiling(260 * s); lay.Height = (int)Math.Ceiling(row + 2 * pad);
                lay.Radius = (float)(18 * s);
                break;
            }
        }
        lay.Radius = Math.Min(lay.Radius, Math.Min(lay.Width, lay.Height) / 2f);
        return lay;
    }

    // ---- drawing ----
    /// <summary>Draws the frame into <paramref name="bits"/> (width x height, 4 bytes a pixel, premultiplied BGRA, top row first). Everything not drawn is alpha 0.</summary>
    public void Draw(PillModel m, double scale, PillFrame frame, IntPtr bits, int width, int height)
    {
        var lay = Layout(m, scale);
        var pal = PaletteFor(m.Dark);
        GdipCreateBitmapFromScan0(width, height, width * 4, PixelFormat32bppPARGB, bits, out var bmp);
        GdipGetImageGraphicsContext(bmp, out var g);
        try
        {
            GdipSetSmoothingMode(g, 4);
            GdipSetPixelOffsetMode(g, 2);
            GdipSetTextRenderingHint(g, TextHint);
            GdipGraphicsClear(g, 0);
            float s = lay.Scale;
            if (lay.Filled)
            {
                Round(g, 0, 0, lay.Width, lay.Height, lay.Radius, pal.Rule);
                Round(g, lay.Border, lay.Border, lay.Width - 2 * lay.Border, lay.Height - 2 * lay.Border, Math.Max(0, lay.Radius - lay.Border), pal.Paper);
            }
            var body = Font("body", 13, s);
            switch (m.Kind)
            {
                case PillKind.Dots:
                case PillKind.DotsLabel:
                case PillKind.LiveWords:
                    Bars(g, lay, m, frame, pal.Accent);
                    if (m.Kind == PillKind.DotsLabel) Words(g, lay, m.Text, "", body, pal);
                    else if (m.Kind == PillKind.LiveWords)
                    {
                        if (m.Settled.Length + m.Guess.Length == 0) Words(g, lay, "", "Listening…", body, pal);
                        else Words(g, lay, m.Settled, m.Guess, body, pal);
                    }
                    break;
                case PillKind.Notice:
                case PillKind.Error:
                    Dot(g, m.Kind == PillKind.Error ? pal.Critical : pal.Accent, lay.DotRect);
                    Words(g, lay, m.Text, "", body, pal);
                    break;
                case PillKind.Copy:
                    Glyph(g, "", Font("icon", 16, s), lay.PasteIcon, pal.Ink, left: true);
                    Line(g, m.Title, Font("title", 14, s), pal.Ink, lay.TitleRect);
                    Button(g, lay.DismissButton, "×", Font("body", 16, s), pal, filled: false);
                    DrawText(g, m.Preview, body, pal.Muted, lay.PreviewRect, lay.PreviewLines);
                    Line(g, "Ctrl+Win+Shift+V pastes it too", Font("body", 12, s), pal.Muted, lay.HintRect);
                    Button(g, lay.CopyButton, "Copy", Font("button", 14, s), pal, filled: true);
                    break;
                case PillKind.Changes:
                    Glyph(g, m.UsedMode ? "" : "", Font("icon", 16, s), lay.ChipIcon, pal.Ink, left: true);
                    Line(g, m.Text, body, pal.Ink, lay.ChipText);
                    Button(g, lay.CopyButton, "Copy original", body, pal, filled: false);
                    break;
            }
        }
        finally { GdipDeleteGraphics(g); GdipDisposeImage(bmp); }
    }

    void Bars(IntPtr g, PillLayout lay, PillModel m, PillFrame f, uint accent)
    {
        double s = lay.Scale, t = f.Time;
        bool listening = m.Kind != PillKind.DotsLabel && !m.Processing;
        double voice = Math.Sqrt(Math.Clamp(f.Level, 0, 1));
        for (int i = 0; i < BarWeights.Length; i++)
        {
            double wobble = m.Motion ? 0.65 + 0.35 * Math.Sin(t * 9 + i * 1.7) : 1;
            double sweep = m.Motion ? Math.Exp(-Math.Pow(t * 0.8 % 1 * 7 - i * 1.2 - 0.6, 2)) : 1;
            double h = (listening ? 13 + 27 * voice * wobble * BarWeights[i] : 13) * s;
            double opacity = listening ? 1 : 0.35 + 0.65 * sweep;
            double w = 9 * s;
            double x = lay.DotsX + (5 + 2.5 + 14 * i) * s, y = lay.DotsY + 20 * s - h / 2;
            Round(g, (float)x, (float)y, (float)w, (float)h, (float)(w / 2), (accent & 0xFFFFFF) | (uint)Math.Round(opacity * 255) << 24);
        }
    }

    static void Dot(IntPtr g, uint argb, RectF r)
    {
        GdipCreateSolidFill(argb, out var brush);
        GdipFillEllipse(g, brush, r.X, r.Y, r.W, r.H);
        GdipDeleteBrush(brush);
    }

    static void Round(IntPtr g, float x, float y, float w, float h, float radius, uint argb)
    {
        if (w <= 0 || h <= 0) return;
        radius = Math.Min(radius, Math.Min(w, h) / 2);
        float d = radius * 2;
        GdipCreatePath(0, out var path);
        d = Math.Max(d, 0.01f);
        GdipAddPathArc(path, x, y, d, d, 180, 90);
        GdipAddPathArc(path, x + w - d, y, d, d, 270, 90);
        GdipAddPathArc(path, x + w - d, y + h - d, d, d, 0, 90);
        GdipAddPathArc(path, x, y + h - d, d, d, 90, 90);
        GdipClosePathFigure(path);
        GdipCreateSolidFill(argb, out var brush);
        GdipFillPath(g, brush, path);
        GdipDeleteBrush(brush);
        GdipDeletePath(path);
    }

    void Button(IntPtr g, RectF r, string label, IntPtr font, Palette pal, bool filled)
    {
        float radius = 4 * (r.H / 32f);
        Round(g, r.X, r.Y, r.W, r.H, radius, filled ? pal.Accent : pal.Rule);
        float b = Math.Max(1, r.H / 32f);
        if (!filled) Round(g, r.X + b, r.Y + b, r.W - 2 * b, r.H - 2 * b, Math.Max(0, radius - b), pal.ButtonFill);
        GdipCreateSolidFill(filled ? pal.OnAccent : pal.Ink, out var brush);
        var layout = r;
        if (font != 0) GdipDrawString(g, label, -1, font, ref layout, fmtCenter, brush);
        GdipDeleteBrush(brush);
    }

    void Glyph(IntPtr g, string glyph, IntPtr font, RectF r, uint argb, bool left)
    {
        if (font == 0) return;
        GdipCreateSolidFill(argb, out var brush);
        var layout = r;
        GdipDrawString(g, glyph, -1, font, ref layout, fmtCenter, brush);
        GdipDeleteBrush(brush);
    }

    void Line(IntPtr g, string text, IntPtr font, uint argb, RectF r)
    {
        if (font == 0 || text.Length == 0) return;
        GdipCreateSolidFill(argb, out var brush);
        float h = LineHeight(font);
        var layout = new RectF(r.X, r.Y + (r.H - h) / 2, r.W, h + 1);
        GdipDrawString(g, text, -1, font, ref layout, fmtOne, brush);
        GdipDeleteBrush(brush);
    }

    void DrawText(IntPtr g, string text, IntPtr font, uint argb, RectF r, int lines)
    {
        if (font == 0 || text.Length == 0 || lines == 0) return;
        GdipCreateSolidFill(argb, out var brush);
        var layout = new RectF(r.X, r.Y, r.W, r.H + LineHeight(font) * 0.3f);
        GdipDrawString(g, text, -1, font, ref layout, fmtDraw, brush);
        GdipDeleteBrush(brush);
    }

    /// <summary>Settled words in the ink colour and the guess (the last word, or "Listening…") dimmed, wrapped as one text.</summary>
    void Words(IntPtr g, PillLayout lay, string settled, string guess, IntPtr font, Palette pal)
    {
        if (font == 0 || lay.TextLines == 0) return;
        var rect = lay.TextRect;
        string full = settled + guess;
        if (guess.Length == 0) { DrawText(g, full, font, pal.Ink, rect, lay.TextLines); return; }
        if (settled.Length == 0) { DrawText(g, full, font, pal.Dim, rect, lay.TextLines); return; }
        var layout = new RectF(rect.X, rect.Y, rect.W, rect.H + LineHeight(font) * 0.3f);
        GdipCreateRegion(out var region);
        GdipSetStringFormatMeasurableCharacterRanges(fmtDraw, 1, [new CharRange { First = 0, Length = settled.Length }]);
        GdipMeasureCharacterRanges(g, full, -1, font, ref layout, fmtDraw, 1, [region]);
        GdipSetClipRegion(g, region, 0);
        DrawText(g, full, font, pal.Ink, rect, lay.TextLines);
        GdipResetClip(g);
        GdipSetClipRegion(g, region, 4);
        DrawText(g, full, font, pal.Dim, rect, lay.TextLines);
        GdipResetClip(g);
        GdipDeleteRegion(region);
    }

    public void Dispose()
    {
        foreach (var f in fonts.Values) if (f != 0) GdipDeleteFont(f);
        foreach (var f in families.Values) if (f != 0) GdipDeleteFontFamily(f);
        fonts.Clear(); families.Clear();
        foreach (var f in new[] { fmtDraw, fmtMeasure, fmtCenter, fmtOne }) if (f != 0) GdipDeleteStringFormat(f);
        if (scratch != 0) GdipDeleteGraphics(scratch);
        if (scratchBitmap != 0) GdipDisposeImage(scratchBitmap);
        if (scratchBits != 0) System.Runtime.InteropServices.Marshal.FreeHGlobal(scratchBits);
        scratch = scratchBitmap = scratchBits = 0;
    }
}
