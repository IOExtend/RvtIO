using System.Globalization;
using System.Text;
using System.Xml;
using RvtIO.Pdf;

namespace RvtIO.Print;

/// <summary>Shared 2D vector drawing surface in page millimetres (lower-left origin, +y up) consumed by
/// <see cref="PlanPrinter"/> so PDF and SVG output share the exact same geometry, layout, and annotations.</summary>
internal interface IPlanCanvas
{
    bool CanDrawText { get; }
    void DrawLine(double x1, double y1, double x2, double y2, double widthMillimetres = 0.25, double gray = 0);
    void DrawArc(double centerX, double centerY, double radius, double startDegrees, double endDegrees,
        double widthMillimetres = 0.25, double gray = 0);
    void DrawRectangle(double x, double y, double width, double height, double widthMillimetres = 0.25,
        double? fillGray = null);
    void DrawPolyline(IReadOnlyList<(double X, double Y)> points, double widthMillimetres = 0.25,
        bool closed = false, double? fillGray = null);
    void DrawPolyline(IReadOnlyList<(double X, double Y)> points, double widthMillimetres, bool closed,
        PdfColor? fillColor);
    void DrawText(string text, double x, double y, double heightMillimetres = 3,
        PdfTextAlignment alignment = PdfTextAlignment.Left, double gray = 0);
    void DrawTextRotated(string text, double x, double y, double heightMillimetres, double degrees,
        PdfTextAlignment alignment = PdfTextAlignment.Left, double gray = 0);
}

internal sealed class PdfPlanCanvas(PdfPage page) : IPlanCanvas
{
    public bool CanDrawText => page.Owner.Font != null;

    public void DrawLine(double x1, double y1, double x2, double y2, double widthMillimetres = 0.25, double gray = 0) =>
        page.DrawLine(x1, y1, x2, y2, widthMillimetres, gray);

    public void DrawArc(double centerX, double centerY, double radius, double startDegrees, double endDegrees,
        double widthMillimetres = 0.25, double gray = 0) =>
        page.DrawArc(centerX, centerY, radius, startDegrees, endDegrees, widthMillimetres, gray);

    public void DrawRectangle(double x, double y, double width, double height, double widthMillimetres = 0.25,
        double? fillGray = null) =>
        page.DrawRectangle(x, y, width, height, widthMillimetres, fillGray);

    public void DrawPolyline(IReadOnlyList<(double X, double Y)> points, double widthMillimetres = 0.25,
        bool closed = false, double? fillGray = null) =>
        page.DrawPolyline(points, widthMillimetres, closed, fillGray);

    public void DrawPolyline(IReadOnlyList<(double X, double Y)> points, double widthMillimetres, bool closed,
        PdfColor? fillColor) =>
        page.DrawPolyline(points, widthMillimetres, closed, fillColor);

    public void DrawText(string text, double x, double y, double heightMillimetres = 3,
        PdfTextAlignment alignment = PdfTextAlignment.Left, double gray = 0) =>
        page.DrawText(text, x, y, heightMillimetres, alignment, gray);

    public void DrawTextRotated(string text, double x, double y, double heightMillimetres, double degrees,
        PdfTextAlignment alignment = PdfTextAlignment.Left, double gray = 0) =>
        page.DrawTextRotated(text, x, y, heightMillimetres, degrees, alignment, gray);
}

/// <summary>Writes well-formed UTF-8 SVG XML in page millimetres (1 user unit = 1 mm, upper-left origin, +y down).</summary>
internal sealed class SvgPlanCanvas : IPlanCanvas, IDisposable
{
    private const string SvgNs = "http://www.w3.org/2000/svg";
    private const string RvtioNs = "https://ioextend.github.io/rvtio/svg";
    private const string FontFamily = "Arial, 'Segoe UI', Tahoma, 'DejaVu Sans', sans-serif";

    private readonly XmlWriter _xml;
    private readonly double _pageWidthMm;
    private readonly double _pageHeightMm;

    public bool CanDrawText => true;

    public SvgPlanCanvas(
        Stream output,
        PdfPageSize paper,
        int levelId,
        string? title,
        int nominalScale,
        double effectiveScale,
        double marginMm,
        double offsetXMm,
        double offsetYMm,
        double modelMinXMm,
        double modelMinYMm,
        double modelMaxXMm,
        double modelMaxYMm)
    {
        _pageWidthMm = paper.WidthMillimetres;
        _pageHeightMm = paper.HeightMillimetres;

        _xml = XmlWriter.Create(output, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            IndentChars = "  ",
            CloseOutput = false,
            CheckCharacters = true,
        });

        string widthText = Num(_pageWidthMm);
        string heightText = Num(_pageHeightMm);

        _xml.WriteStartDocument();
        _xml.WriteStartElement("svg", SvgNs);
        _xml.WriteAttributeString("width", widthText + "mm");
        _xml.WriteAttributeString("height", heightText + "mm");
        _xml.WriteAttributeString("viewBox", $"0 0 {widthText} {heightText}");
        _xml.WriteAttributeString("data-scale", Num(effectiveScale));
        _xml.WriteAttributeString("data-nominal-scale", nominalScale.ToString(CultureInfo.InvariantCulture));
        _xml.WriteAttributeString("data-width-mm", widthText);
        _xml.WriteAttributeString("data-height-mm", heightText);
        _xml.WriteAttributeString("data-level-id", levelId.ToString(CultureInfo.InvariantCulture));

        _xml.WriteStartElement("title", SvgNs);
        _xml.WriteString(SanitizeXml(string.IsNullOrEmpty(title) ? $"Level {levelId.ToString(CultureInfo.InvariantCulture)}" : title));
        _xml.WriteEndElement();

        _xml.WriteStartElement("metadata", SvgNs);
        _xml.WriteStartElement("rvtio", "plan", RvtioNs);
        _xml.WriteAttributeString("levelId", levelId.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrEmpty(title))
            _xml.WriteAttributeString("levelName", SanitizeXml(title));
        _xml.WriteAttributeString("nominalScale", nominalScale.ToString(CultureInfo.InvariantCulture));
        _xml.WriteAttributeString("scale", Num(effectiveScale));
        _xml.WriteAttributeString("printedScale", ((int)Math.Round(effectiveScale)).ToString(CultureInfo.InvariantCulture));
        _xml.WriteAttributeString("widthMm", widthText);
        _xml.WriteAttributeString("heightMm", heightText);
        _xml.WriteAttributeString("marginMm", Num(marginMm));
        _xml.WriteAttributeString("offsetXMm", Num(offsetXMm));
        _xml.WriteAttributeString("offsetYMm", Num(offsetYMm));
        _xml.WriteAttributeString("modelMinXMm", Num(modelMinXMm));
        _xml.WriteAttributeString("modelMinYMm", Num(modelMinYMm));
        _xml.WriteAttributeString("modelMaxXMm", Num(modelMaxXMm));
        _xml.WriteAttributeString("modelMaxYMm", Num(modelMaxYMm));
        _xml.WriteEndElement();
        _xml.WriteEndElement();

        // White paper sheet matching the PDF background.
        _xml.WriteStartElement("rect", SvgNs);
        _xml.WriteAttributeString("x", "0");
        _xml.WriteAttributeString("y", "0");
        _xml.WriteAttributeString("width", widthText);
        _xml.WriteAttributeString("height", heightText);
        _xml.WriteAttributeString("fill", "#ffffff");
        _xml.WriteEndElement();
    }

    public void DrawLine(double x1, double y1, double x2, double y2, double widthMillimetres = 0.25, double gray = 0)
    {
        if (widthMillimetres < 0) return;
        _xml.WriteStartElement("line", SvgNs);
        _xml.WriteAttributeString("x1", Num(SvgX(x1)));
        _xml.WriteAttributeString("y1", Num(SvgY(y1)));
        _xml.WriteAttributeString("x2", Num(SvgX(x2)));
        _xml.WriteAttributeString("y2", Num(SvgY(y2)));
        _xml.WriteAttributeString("stroke", GrayHex(gray));
        _xml.WriteAttributeString("stroke-width", Num(widthMillimetres));
        _xml.WriteAttributeString("stroke-linecap", "butt");
        _xml.WriteEndElement();
    }

    public void DrawArc(double centerX, double centerY, double radius, double startDegrees, double endDegrees,
        double widthMillimetres = 0.25, double gray = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);
        if (endDegrees < startDegrees) (startDegrees, endDegrees) = (endDegrees, startDegrees);
        double span = endDegrees - startDegrees;
        if (span <= 0) return;

        if (span >= 359.999)
        {
            _xml.WriteStartElement("circle", SvgNs);
            _xml.WriteAttributeString("cx", Num(SvgX(centerX)));
            _xml.WriteAttributeString("cy", Num(SvgY(centerY)));
            _xml.WriteAttributeString("r", Num(radius));
            _xml.WriteAttributeString("fill", "none");
            _xml.WriteAttributeString("stroke", GrayHex(gray));
            _xml.WriteAttributeString("stroke-width", Num(widthMillimetres));
            _xml.WriteEndElement();
            return;
        }

        double a0 = startDegrees * Math.PI / 180;
        double a1 = endDegrees * Math.PI / 180;
        double sx = SvgX(centerX + radius * Math.Cos(a0));
        double sy = SvgY(centerY + radius * Math.Sin(a0));
        double ex = SvgX(centerX + radius * Math.Cos(a1));
        double ey = SvgY(centerY + radius * Math.Sin(a1));
        string r = Num(radius);
        string largeArc = span > 180 ? "1" : "0";
        // Y-up counter-clockwise sweep maps to sweep-flag=0 in SVG's Y-down coordinates.
        string d = $"M {Num(sx)} {Num(sy)} A {r} {r} 0 {largeArc} 0 {Num(ex)} {Num(ey)}";

        _xml.WriteStartElement("path", SvgNs);
        _xml.WriteAttributeString("d", d);
        _xml.WriteAttributeString("fill", "none");
        _xml.WriteAttributeString("stroke", GrayHex(gray));
        _xml.WriteAttributeString("stroke-width", Num(widthMillimetres));
        _xml.WriteEndElement();
    }

    public void DrawRectangle(double x, double y, double width, double height, double widthMillimetres = 0.25,
        double? fillGray = null)
    {
        if (width <= 0 || height <= 0) return;
        bool stroke = widthMillimetres > 0;
        if (!stroke && fillGray == null) return;

        _xml.WriteStartElement("rect", SvgNs);
        _xml.WriteAttributeString("x", Num(SvgX(x)));
        _xml.WriteAttributeString("y", Num(SvgY(y + height)));
        _xml.WriteAttributeString("width", Num(width));
        _xml.WriteAttributeString("height", Num(height));
        _xml.WriteAttributeString("fill", fillGray is double fill ? GrayHex(fill) : "none");
        if (stroke)
        {
            _xml.WriteAttributeString("stroke", "#000000");
            _xml.WriteAttributeString("stroke-width", Num(widthMillimetres));
        }
        else
        {
            _xml.WriteAttributeString("stroke", "none");
        }
        _xml.WriteEndElement();
    }

    public void DrawPolyline(IReadOnlyList<(double X, double Y)> points, double widthMillimetres = 0.25,
        bool closed = false, double? fillGray = null) =>
        DrawPolyline(points, widthMillimetres, closed,
            fillGray is double fill ? new PdfColor(fill, fill, fill) : null);

    public void DrawPolyline(IReadOnlyList<(double X, double Y)> points, double widthMillimetres, bool closed,
        PdfColor? fillColor)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2) return;
        bool stroke = widthMillimetres > 0;
        if (!stroke && fillColor == null) return;

        var pts = new StringBuilder(points.Count * 16);
        for (int i = 0; i < points.Count; i++)
        {
            if (i > 0) pts.Append(' ');
            pts.Append(Num(SvgX(points[i].X))).Append(',').Append(Num(SvgY(points[i].Y)));
        }

        _xml.WriteStartElement(closed ? "polygon" : "polyline", SvgNs);
        _xml.WriteAttributeString("points", pts.ToString());
        _xml.WriteAttributeString("fill", fillColor is PdfColor color ? ColorHex(color) : "none");
        if (stroke)
        {
            _xml.WriteAttributeString("stroke", "#000000");
            _xml.WriteAttributeString("stroke-width", Num(widthMillimetres));
            _xml.WriteAttributeString("stroke-linejoin", "miter");
        }
        else
        {
            _xml.WriteAttributeString("stroke", "none");
        }
        _xml.WriteEndElement();
    }

    public void DrawText(string text, double x, double y, double heightMillimetres = 3,
        PdfTextAlignment alignment = PdfTextAlignment.Left, double gray = 0)
    {
        if (string.IsNullOrEmpty(text)) return;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightMillimetres);
        WriteTextElement(text, x, y, heightMillimetres, 0, alignment, gray);
    }

    public void DrawTextRotated(string text, double x, double y, double heightMillimetres, double degrees,
        PdfTextAlignment alignment = PdfTextAlignment.Left, double gray = 0)
    {
        if (string.IsNullOrEmpty(text)) return;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightMillimetres);
        WriteTextElement(text, x, y, heightMillimetres, degrees, alignment, gray);
    }

    public void Dispose()
    {
        _xml.WriteEndElement(); // </svg>
        _xml.WriteEndDocument();
        _xml.Flush();
        _xml.Dispose();
    }

    private void WriteTextElement(string text, double x, double y, double heightMillimetres, double degrees,
        PdfTextAlignment alignment, double gray)
    {
        string sx = Num(SvgX(x));
        string sy = Num(SvgY(y));
        string anchor = alignment switch
        {
            PdfTextAlignment.Center => "middle",
            PdfTextAlignment.Right => "end",
            _ => "start",
        };

        _xml.WriteStartElement("text", SvgNs);
        _xml.WriteAttributeString("x", sx);
        _xml.WriteAttributeString("y", sy);
        if (degrees != 0)
            _xml.WriteAttributeString("transform", $"rotate({Num(-degrees)} {sx} {sy})");
        _xml.WriteAttributeString("font-family", FontFamily);
        _xml.WriteAttributeString("font-size", Num(heightMillimetres));
        _xml.WriteAttributeString("fill", GrayHex(gray));
        _xml.WriteAttributeString("text-anchor", anchor);
        _xml.WriteString(SanitizeXml(text));
        _xml.WriteEndElement();
    }

    private double SvgX(double xMm) => xMm;
    private double SvgY(double yMm) => _pageHeightMm - yMm;

    private static string Num(double value)
    {
        double rounded = Math.Round(value, 4);
        if (rounded == 0) rounded = 0;
        return rounded.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static string GrayHex(double gray) => ColorHex(new PdfColor(gray, gray, gray));

    private static string ColorHex(PdfColor color)
    {
        byte r = (byte)Math.Clamp((int)Math.Round(color.R * 255), 0, 255);
        byte g = (byte)Math.Clamp((int)Math.Round(color.G * 255), 0, 255);
        byte b = (byte)Math.Clamp((int)Math.Round(color.B * 255), 0, 255);
        return $"#{r:x2}{g:x2}{b:x2}";
    }

    private static string SanitizeXml(string text)
    {
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (!XmlConvert.IsXmlChar((char)Math.Min(rune.Value, 0xFFFF)) && rune.IsBmp)
                goto Rebuild;
            if (!IsValidXmlRune(rune.Value))
                goto Rebuild;
        }
        return text;

    Rebuild:
        var sb = new StringBuilder(text.Length);
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (IsValidXmlRune(rune.Value))
                sb.Append(rune.ToString());
        }
        return sb.ToString();
    }

    private static bool IsValidXmlRune(int codePoint) =>
        codePoint is 0x9 or 0xA or 0xD
        || (codePoint >= 0x20 && codePoint <= 0xD7FF)
        || (codePoint >= 0xE000 && codePoint <= 0xFFFD)
        || (codePoint >= 0x10000 && codePoint <= 0x10FFFF);
}
