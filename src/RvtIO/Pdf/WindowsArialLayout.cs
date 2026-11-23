using System.Runtime.InteropServices;
using System.Text;

namespace RvtIO.Pdf;

internal static class WindowsArialLayout
{
    private const double MillimetresPerPixel = 25.4 / 384;

    internal static bool TryMeasure(string text, double sizeMm, out double width, out double ascent)
    {
        width = ascent = 0;
        // shortcut: the verified 12 pt Arial layout uses a 64-pixel em, extend when other sizes are verified.
        if (!OperatingSystem.IsWindows() || Math.Abs(sizeMm - 64 * MillimetresPerPixel) > 1e-6)
            return false;
        nint dc = CreateCompatibleDC(0);
        if (dc == 0) return false;
        nint font = CreateFontW(-64, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, "Arial");
        nint previous = 0;
        try
        {
            if (font == 0) return false;
            previous = SelectObject(dc, font);
            var face = new StringBuilder(32);
            if (previous == 0 || previous == -1 || GetTextFaceW(dc, face.Capacity, face) == 0
                || !string.Equals(face.ToString(), "Arial", StringComparison.OrdinalIgnoreCase)
                || !GetTextExtentPoint32W(dc, text, text.Length, out var size)
                || !GetTextMetricsW(dc, out var metrics))
                return false;
            width = size.Width * MillimetresPerPixel;
            ascent = metrics.Ascent * MillimetresPerPixel;
            return width > 0 && ascent > 0;
        }
        finally
        {
            if (previous != 0 && previous != -1) SelectObject(dc, previous);
            if (font != 0) DeleteObject(font);
            DeleteDC(dc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextSize
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Explicit, Size = 60)]
    private struct TextMetrics
    {
        [FieldOffset(4)]
        public int Ascent;
    }

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern nint CreateCompatibleDC(nint device);

    [DllImport("gdi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateFontW(int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeout, uint charSet, uint outputPrecision, uint clipPrecision,
        uint quality, uint pitchAndFamily, string face);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern nint SelectObject(nint device, nint obj);

    [DllImport("gdi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetTextFaceW(nint device, int count, StringBuilder face);

    [DllImport("gdi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTextExtentPoint32W(nint device, string text, int count, out TextSize size);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTextMetricsW(nint device, out TextMetrics metrics);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int DeleteObject(nint obj);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int DeleteDC(nint device);
}
