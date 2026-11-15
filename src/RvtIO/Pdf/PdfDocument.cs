using System.Globalization;
using System.Text;

namespace RvtIO.Pdf;

/// <summary>A paper size in millimetres, portrait: the width is the shorter side. Use <see cref="Landscape"/> for the
/// rotated page, which is what a sheet whose title block is wider than tall is printed on.</summary>
public readonly record struct PdfPageSize(double WidthMillimetres, double HeightMillimetres)
{
    /// <summary>A4, 210 by 297 mm.</summary>
    public static PdfPageSize A4 => new(210, 297);

    /// <summary>A3, 297 by 420 mm.</summary>
    public static PdfPageSize A3 => new(297, 420);

    /// <summary>A2, 420 by 594 mm.</summary>
    public static PdfPageSize A2 => new(420, 594);

    /// <summary>A1, 594 by 841 mm.</summary>
    public static PdfPageSize A1 => new(594, 841);

    /// <summary>A0, 841 by 1189 mm.</summary>
    public static PdfPageSize A0 => new(841, 1189);

    /// <summary>The same paper turned by a quarter: width and height swapped.</summary>
    public PdfPageSize Landscape => new(Math.Max(WidthMillimetres, HeightMillimetres), Math.Min(WidthMillimetres, HeightMillimetres));

    /// <summary>The page in PostScript points (1/72 inch), as the PDF page box is written.</summary>
    public (double Width, double Height) InPoints => (WidthMillimetres * 72.0 / 25.4, HeightMillimetres * 72.0 / 25.4);
}

/// <summary>Horizontal placement of a text anchor.</summary>
public enum PdfTextAlignment
{
    /// <summary>The anchor is the left end of the baseline.</summary>
    Left,
    /// <summary>The anchor is the middle of the baseline.</summary>
    Center,
    /// <summary>The anchor is the right end of the baseline.</summary>
    Right,
}

/// <summary>A PDF document written without a third-party library: pages of vector lines, polygons and text.
/// Coordinates are millimetres from the lower-left corner of the page, the origin a printed sheet uses.</summary>
public sealed class PdfDocument
{
    private readonly List<PdfPage> _pages = new();

    /// <summary>
    /// The font text is written with. Null uses the standard Helvetica font, whose characters are printable ASCII only.
    /// A <see cref="PdfTrueTypeFont"/> is embedded whole as a Type0 font, so text in the scripts it covers (Cyrillic, for
    /// one) is written as real, selectable text.
    /// </summary>
    public PdfTrueTypeFont? Font { get; set; }

    /// <summary>True writes the page content streams, the embedded font and the ToUnicode map with Flate compression;
    /// false (the default) writes them raw, which keeps a small document readable in a text editor but makes a many-page
    /// drawing set tens of megabytes.</summary>
    public bool Compress { get; set; }

    /// <summary>The characters drawn with <see cref="Font"/>, which the font's width array and ToUnicode map hold.</summary>
    internal HashSet<char> UsedCharacters { get; } = new();

    /// <summary>The pages, in the order they were added.</summary>
    public IReadOnlyList<PdfPage> Pages => _pages;

    /// <summary>Adds a blank page of the given paper size.</summary>
    /// <param name="size">The page size; portrait, unless <see cref="PdfPageSize.Landscape"/> is used.</param>
    public PdfPage AddPage(PdfPageSize size)
    {
        if (size.WidthMillimetres <= 0 || size.HeightMillimetres <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), "A page needs a positive width and height.");
        var page = new PdfPage(size, this);
        _pages.Add(page);
        return page;
    }

    /// <summary>Writes the document to a file, replacing it when it exists.</summary>
    /// <param name="path">The output path.</param>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = File.Create(path);
        Save(stream);
    }

    /// <summary>Writes the document to a stream.</summary>
    /// <param name="stream">The output stream, left open.</param>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        PdfSerializer.Write(stream, this);
    }
}

/// <summary>One page of a <see cref="PdfDocument"/>. Every drawing call appends to the page content stream; the
/// coordinates are millimetres from the lower-left corner, y up.</summary>
public sealed class PdfPage
{
    private readonly StringBuilder _content = new();
    private readonly PdfDocument _document;

    internal PdfPage(PdfPageSize size, PdfDocument document)
    {
        Size = size;
        _document = document;
    }

    /// <summary>The paper size of the page.</summary>
    public PdfPageSize Size { get; }

    internal string Content => _content.ToString();

    /// <summary>The document the page belongs to: the font and the used characters live there.</summary>
    internal PdfDocument Owner => _document;

    /// <summary>Draws a line of the given width.</summary>
    /// <param name="x1">Start x in millimetres.</param>
    /// <param name="y1">Start y in millimetres.</param>
    /// <param name="x2">End x in millimetres.</param>
    /// <param name="y2">End y in millimetres.</param>
    /// <param name="widthMillimetres">Line width in millimetres.</param>
    /// <param name="gray">Ink from 0 (black) to 1 (white).</param>
    public void DrawLine(double x1, double y1, double x2, double y2, double widthMillimetres = 0.25, double gray = 0)
    {
        Stroke(widthMillimetres, gray);
        _content.Append(Number(Points(x1))).Append(' ').Append(Number(Points(y1))).Append(" m ")
                .Append(Number(Points(x2))).Append(' ').Append(Number(Points(y2))).Append(" l S\n");
    }

    /// <summary>Draws a circular arc of the given width, approximated by cubic Bézier segments.</summary>
    /// <param name="centerX">Centre x in millimetres.</param>
    /// <param name="centerY">Centre y in millimetres.</param>
    /// <param name="radius">Radius in millimetres.</param>
    /// <param name="startDegrees">Start angle in degrees, counter-clockwise from +x.</param>
    /// <param name="endDegrees">End angle in degrees; a negative span draws clockwise.</param>
    /// <param name="widthMillimetres">Line width in millimetres.</param>
    /// <param name="gray">Ink from 0 (black) to 1 (white).</param>
    public void DrawArc(double centerX, double centerY, double radius, double startDegrees, double endDegrees,
        double widthMillimetres = 0.25, double gray = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);
        if (endDegrees < startDegrees) (startDegrees, endDegrees) = (endDegrees, startDegrees);
        double span = endDegrees - startDegrees;
        if (span <= 0) return;
        double sweep = span * Math.PI / 180;
        int segments = Math.Max(1, (int)Math.Ceiling(sweep / (Math.PI / 2)));
        double step = sweep / segments;
        double handle = 4.0 / 3.0 * Math.Tan(step / 4);
        double angle = startDegrees * Math.PI / 180;
        double x0 = centerX + radius * Math.Cos(angle), y0 = centerY + radius * Math.Sin(angle);
        Stroke(widthMillimetres, gray);
        _content.Append(Number(Points(x0))).Append(' ').Append(Number(Points(y0))).Append(" m ");
        for (int i = 0; i < segments; i++)
        {
            double next = angle + step;
            double x1 = centerX + radius * (Math.Cos(angle) - handle * Math.Sin(angle));
            double y1 = centerY + radius * (Math.Sin(angle) + handle * Math.Cos(angle));
            double x2 = centerX + radius * (Math.Cos(next) + handle * Math.Sin(next));
            double y2 = centerY + radius * (Math.Sin(next) - handle * Math.Cos(next));
            double x3 = centerX + radius * Math.Cos(next), y3 = centerY + radius * Math.Sin(next);
            _content.Append(Number(Points(x1))).Append(' ').Append(Number(Points(y1))).Append(' ')
                    .Append(Number(Points(x2))).Append(' ').Append(Number(Points(y2))).Append(' ')
                    .Append(Number(Points(x3))).Append(' ').Append(Number(Points(y3))).Append(" c ");
            angle = next;
        }
        _content.Append("S\n");
    }

    /// <summary>Draws a polyline through the points, optionally closed, either stroked or filled.</summary>
    /// <param name="points">The points, x and y in millimetres.</param>
    /// <param name="widthMillimetres">Line width in millimetres; 0 when the outline is not drawn.</param>
    /// <param name="closed">True to close the path.</param>
    /// <param name="fillGray">A fill from 0 (black) to 1 (white); null leaves the path unfilled.</param>
    public void DrawPolyline(IReadOnlyList<(double X, double Y)> points, double widthMillimetres = 0.25,
        bool closed = false, double? fillGray = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2) return;
        bool stroke = widthMillimetres > 0;
        if (!stroke && fillGray == null) return;
        if (stroke) Stroke(widthMillimetres, 0);
        if (fillGray is double fill) Fill(fill);
        for (int i = 0; i < points.Count; i++)
        {
            _content.Append(Number(Points(points[i].X))).Append(' ').Append(Number(Points(points[i].Y)))
                    .Append(i == 0 ? " m " : " l ");
        }
        if (closed) _content.Append("h ");
        _content.Append(stroke && fillGray != null ? "B\n" : stroke ? "S\n" : "f\n");
    }

    /// <summary>Starts a clipping region: everything drawn until <see cref="EndClip"/> is cut to the rectangle.</summary>
    /// <param name="x">Left edge, millimetres.</param>
    /// <param name="y">Bottom edge, millimetres.</param>
    /// <param name="width">Width, millimetres.</param>
    /// <param name="height">Height, millimetres.</param>
    public void BeginClip(double x, double y, double width, double height)
    {
        _content.Append("q ").Append(Number(Points(x))).Append(' ').Append(Number(Points(y))).Append(' ')
            .Append(Number(Points(width))).Append(' ').Append(Number(Points(height))).Append(" re W n\n");
    }

    /// <summary>Ends the clipping region started by <see cref="BeginClip"/>.</summary>
    public void EndClip() => _content.Append("Q\n");

    /// <summary>Sets the dash pattern of the following strokes, in millimetres.</summary>
    /// <param name="dashMillimetres">Length of a dash.</param>
    /// <param name="gapMillimetres">Length of the gap after it.</param>
    public void SetDash(double dashMillimetres, double gapMillimetres) =>
        _content.Append('[').Append(Number(Points(dashMillimetres))).Append(' ')
            .Append(Number(Points(gapMillimetres))).Append("] 0 d\n");

    /// <summary>Clears the dash pattern.</summary>
    public void ResetDash() => _content.Append("[] 0 d\n");

    /// <summary>Draws an optionally filled rectangle.</summary>
    /// <param name="x">Left x in millimetres.</param>
    /// <param name="y">Bottom y in millimetres.</param>
    /// <param name="width">Width in millimetres.</param>
    /// <param name="height">Height in millimetres.</param>
    /// <param name="widthMillimetres">Line width in millimetres; 0 when the outline is not drawn.</param>
    /// <param name="fillGray">A fill from 0 (black) to 1 (white); null leaves the rectangle unfilled.</param>
    public void DrawRectangle(double x, double y, double width, double height, double widthMillimetres = 0.25,
        double? fillGray = null)
    {
        if (width <= 0 || height <= 0) return;
        bool stroke = widthMillimetres > 0;
        if (stroke) Stroke(widthMillimetres, 0);
        if (fillGray is double fill) Fill(fill);
        _content.Append(Number(Points(x))).Append(' ').Append(Number(Points(y))).Append(' ')
                .Append(Number(Points(width))).Append(' ').Append(Number(Points(height))).Append(" re ");
        _content.Append(stroke && fillGray != null ? "B\n" : stroke ? "S\n" : "f\n");
    }

    /// <summary>Draws one line of text. With <see cref="PdfDocument.Font"/> set the text is written with the embedded
    /// font and may hold any character the font covers; without it the standard Helvetica font is used and the text must
    /// be printable ASCII.</summary>
    /// <param name="text">The text.</param>
    /// <param name="x">Anchor x in millimetres.</param>
    /// <param name="y">Baseline y in millimetres.</param>
    /// <param name="heightMillimetres">Font size in millimetres (the em size: the cap height is about 0.7 of it).</param>
    /// <param name="alignment">Where the anchor sits along the line.</param>
    /// <param name="gray">Ink from 0 (black) to 1 (white).</param>
    /// <exception cref="ArgumentException">No font is set and the text holds a character outside printable ASCII.</exception>
    public void DrawText(string text, double x, double y, double heightMillimetres = 3,
        PdfTextAlignment alignment = PdfTextAlignment.Left, double gray = 0)
    {
        if (string.IsNullOrEmpty(text)) return;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightMillimetres);
        double fontPoints = heightMillimetres * 72.0 / 25.4;
        // Text is painted with the non-stroking colour, so set the fill (not the stroke) colour: after a white-filled
        // bubble or floor the fill could still be white and the text would disappear.
        Fill(gray);
        if (_document.Font is { } font)
        {
            var encoded = new StringBuilder(text.Length * 4);
            double width = 0;
            foreach (char character in text)
            {
                int glyph = font.GlyphOf(character);
                _document.UsedCharacters.Add(character);
                width += font.AdvanceOf(glyph) / (double)font.UnitsPerEm * heightMillimetres;
                encoded.Append(glyph.ToString("X4", CultureInfo.InvariantCulture));
            }
            double anchor = alignment switch
            {
                PdfTextAlignment.Center => x - width / 2,
                PdfTextAlignment.Right => x - width,
                _ => x,
            };
            _content.Append("BT /F2 ").Append(Number(fontPoints)).Append(" Tf ")
                    .Append(Number(Points(anchor))).Append(' ').Append(Number(Points(y))).Append(" Td <")
                    .Append(encoded).Append("> Tj ET\n");
            return;
        }

        foreach (char character in text)
            if (character < ' ' || character > '~')
                throw new ArgumentException(
                    $"The standard PDF font holds printable ASCII only; '{character}' (U+{(int)character:X4}) needs an embedded font.",
                    nameof(text));
        double asciiWidth = text.Length * heightMillimetres * 0.5;
        double asciiAnchor = alignment switch
        {
            PdfTextAlignment.Center => x - asciiWidth / 2,
            PdfTextAlignment.Right => x - asciiWidth,
            _ => x,
        };
        _content.Append("BT /F1 ").Append(Number(fontPoints)).Append(" Tf ")
                .Append(Number(Points(asciiAnchor))).Append(' ').Append(Number(Points(y))).Append(" Td (")
                .Append(text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)"))
                .Append(") Tj ET\n");
    }

    /// <summary>Millimetres to the PDF user space unit (1/72 inch), which the page box and the content share.</summary>
    private static double Points(double millimetres) => millimetres * 72.0 / 25.4;

    private void Stroke(double widthMillimetres, double gray)
    {
        if (widthMillimetres > 0)
            _content.Append(Number(widthMillimetres * 72.0 / 25.4)).Append(" w\n");
        _content.Append(Number(Math.Clamp(gray, 0, 1))).Append(" G\n");
    }

    private void Fill(double gray) =>
        _content.Append(Number(Math.Clamp(gray, 0, 1))).Append(" g\n");

    private static string Number(double value) =>
        Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
}
