using System.Runtime.InteropServices;

namespace Murmur.Platform;

/// <summary>The few GDI+ flat-API calls the dictation pill's renderer needs. GDI+ draws antialiased shapes and text into a 32bpp premultiplied-alpha bitmap we own.</summary>
internal static class Gdip
{
    const string Lib = "gdiplus.dll";
    public const int PixelFormat32bppPARGB = 0xE200B;
    static IntPtr token;
    static readonly object gate = new();

    [StructLayout(LayoutKind.Sequential)] struct StartupInput { public uint Version; public IntPtr Callback; public int SuppressBackgroundThread, SuppressExternalCodecs; }
    [StructLayout(LayoutKind.Sequential)] public struct RectF { public float X, Y, W, H; public RectF(float x, float y, float w, float h) { X = x; Y = y; W = w; H = h; } }
    [StructLayout(LayoutKind.Sequential)] public struct CharRange { public int First, Length; }

    [DllImport(Lib)] static extern int GdiplusStartup(out IntPtr token, ref StartupInput input, IntPtr output);
    [DllImport(Lib)] public static extern int GdipCreateBitmapFromScan0(int w, int h, int stride, int format, IntPtr scan0, out IntPtr bitmap);
    [DllImport(Lib)] public static extern int GdipGetImageGraphicsContext(IntPtr image, out IntPtr graphics);
    [DllImport(Lib)] public static extern int GdipDisposeImage(IntPtr image);
    [DllImport(Lib)] public static extern int GdipDeleteGraphics(IntPtr graphics);
    [DllImport(Lib)] public static extern int GdipSetSmoothingMode(IntPtr g, int mode);
    [DllImport(Lib)] public static extern int GdipSetPixelOffsetMode(IntPtr g, int mode);
    [DllImport(Lib)] public static extern int GdipSetTextRenderingHint(IntPtr g, int hint);
    [DllImport(Lib)] public static extern int GdipGraphicsClear(IntPtr g, uint argb);
    [DllImport(Lib)] public static extern int GdipCreateSolidFill(uint argb, out IntPtr brush);
    [DllImport(Lib)] public static extern int GdipDeleteBrush(IntPtr brush);
    [DllImport(Lib)] public static extern int GdipCreatePen1(uint argb, float width, int unit, out IntPtr pen);
    [DllImport(Lib)] public static extern int GdipDeletePen(IntPtr pen);
    [DllImport(Lib)] public static extern int GdipCreatePath(int fillMode, out IntPtr path);
    [DllImport(Lib)] public static extern int GdipDeletePath(IntPtr path);
    [DllImport(Lib)] public static extern int GdipAddPathArc(IntPtr path, float x, float y, float w, float h, float start, float sweep);
    [DllImport(Lib)] public static extern int GdipClosePathFigure(IntPtr path);
    [DllImport(Lib)] public static extern int GdipFillPath(IntPtr g, IntPtr brush, IntPtr path);
    [DllImport(Lib)] public static extern int GdipDrawPath(IntPtr g, IntPtr pen, IntPtr path);
    [DllImport(Lib)] public static extern int GdipFillEllipse(IntPtr g, IntPtr brush, float x, float y, float w, float h);
    [DllImport(Lib, CharSet = CharSet.Unicode)] public static extern int GdipCreateFontFamilyFromName(string name, IntPtr collection, out IntPtr family);
    [DllImport(Lib)] public static extern int GdipDeleteFontFamily(IntPtr family);
    [DllImport(Lib)] public static extern int GdipCreateFont(IntPtr family, float emSize, int style, int unit, out IntPtr font);
    [DllImport(Lib)] public static extern int GdipDeleteFont(IntPtr font);
    [DllImport(Lib)] public static extern int GdipStringFormatGetGenericTypographic(out IntPtr format);
    [DllImport(Lib)] public static extern int GdipCloneStringFormat(IntPtr format, out IntPtr clone);
    [DllImport(Lib)] public static extern int GdipDeleteStringFormat(IntPtr format);
    [DllImport(Lib)] public static extern int GdipSetStringFormatFlags(IntPtr format, int flags);
    [DllImport(Lib)] public static extern int GdipSetStringFormatTrimming(IntPtr format, int trimming);
    [DllImport(Lib)] public static extern int GdipSetStringFormatAlign(IntPtr format, int align);
    [DllImport(Lib)] public static extern int GdipSetStringFormatLineAlign(IntPtr format, int align);
    [DllImport(Lib)] public static extern int GdipSetStringFormatMeasurableCharacterRanges(IntPtr format, int count, CharRange[] ranges);
    [DllImport(Lib, CharSet = CharSet.Unicode)] public static extern int GdipMeasureString(IntPtr g, string text, int length, IntPtr font, ref RectF layout, IntPtr format, out RectF bounds, out int codepoints, out int lines);
    [DllImport(Lib, CharSet = CharSet.Unicode)] public static extern int GdipDrawString(IntPtr g, string text, int length, IntPtr font, ref RectF layout, IntPtr format, IntPtr brush);
    [DllImport(Lib, CharSet = CharSet.Unicode)] public static extern int GdipMeasureCharacterRanges(IntPtr g, string text, int length, IntPtr font, ref RectF layout, IntPtr format, int count, IntPtr[] regions);
    [DllImport(Lib)] public static extern int GdipCreateRegion(out IntPtr region);
    [DllImport(Lib)] public static extern int GdipDeleteRegion(IntPtr region);
    [DllImport(Lib)] public static extern int GdipSetClipRegion(IntPtr g, IntPtr region, int combine);
    [DllImport(Lib)] public static extern int GdipSetClipRect(IntPtr g, float x, float y, float w, float h, int combine);
    [DllImport(Lib)] public static extern int GdipResetClip(IntPtr g);

    /// <summary>Starts GDI+ once per process (it lives until exit).</summary>
    public static void EnsureStarted()
    {
        lock (gate)
        {
            if (token != 0) return;
            var input = new StartupInput { Version = 1 };
            if (GdiplusStartup(out token, ref input, 0) != 0) throw new InvalidOperationException("GDI+ did not start.");
        }
    }
}
