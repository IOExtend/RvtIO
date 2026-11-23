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

/// <summary>A raster image of the document, 8 bits per channel and three channels (red, green, blue), the form the
/// PDF image object is written in.</summary>
public interface IPdfImage
{
    /// <summary>Width in pixels.</summary>
    int Width { get; }

    /// <summary>Height in pixels.</summary>
    int Height { get; }

    /// <summary>The colour space of the pixels.</summary>
    string ColorSpace { get; }

    /// <summary>The encoded pixel stream as it is written, or the raw pixels when <see cref="Filter"/> is null.</summary>
    byte[] Encoded { get; }

    /// <summary>The PDF filter of the stream, null when the pixels are written raw.</summary>
    string? Filter { get; }
}

/// <summary>A JPEG image read from the file, embedded as it is with the DCTDecode filter.</summary>
public sealed class PdfJpegImage : IPdfImage
{
    /// <summary>The JPEG file bytes.</summary>
    public byte[] Jpeg { get; }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>The number of colour components: 1 (grey), 3 (RGB) or 4 (CMYK).</summary>
    public int Components { get; }

    /// <summary>Creates a JPEG image, reading the size and the components from its header.</summary>
    /// <param name="jpeg">The JPEG file bytes.</param>
    public PdfJpegImage(byte[] jpeg) : this(jpeg, 0, 0, 0)
    {
    }

    private PdfJpegImage(byte[] jpeg, int width, int height, int components)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        Jpeg = jpeg;
        (Width, Height, Components) = ReadHeader(jpeg);
        if (Width <= 0 || Height <= 0) throw new ArgumentException("The bytes are not a JPEG.", nameof(jpeg));
    }

    /// <summary>The colour space of the components.</summary>
    public string ColorSpace => Components switch { 1 => "/DeviceGray", 4 => "/DeviceCMYK", _ => "/DeviceRGB" };

    /// <summary>The JPEG bytes.</summary>
    public byte[] Encoded => Jpeg;

    /// <summary>The DCTDecode filter.</summary>
    public string? Filter => "DCTDecode";

    /// <summary>Reads the size and the components from the JPEG start-of-frame marker.</summary>
    internal static (int Width, int Height, int Components) ReadHeader(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return (0, 0, 0);
        int at = 2;
        while (at + 4 <= jpeg.Length)
        {
            if (jpeg[at] != 0xFF) { at++; continue; }
            byte marker = jpeg[at + 1];
            if (marker is 0xD8 or 0xD9 or 0x01 || marker is >= 0xD0 and <= 0xD7) { at += 2; continue; }
            int length = (jpeg[at + 2] << 8) | jpeg[at + 3];
            bool isFrame = marker is >= 0xC0 and <= 0xCF && marker is not 0xC4 and not 0xC8 and not 0xCC;
            if (isFrame && at + 9 < jpeg.Length)
            {
                int height = (jpeg[at + 5] << 8) | jpeg[at + 6];
                int width = (jpeg[at + 7] << 8) | jpeg[at + 8];
                int components = jpeg[at + 9];
                return (width, height, components);
            }
            at += 2 + length;
        }
        return (0, 0, 0);
    }
}

/// <summary>An RGB raster image of the document, 8 bits per channel, written with Flate compression.</summary>
public sealed class PdfRasterImage : IPdfImage
{
    internal bool Interpolate { get; init; }
    internal bool TransparentWhite { get; init; }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>The pixels, row by row from the top, three bytes each.</summary>
    public byte[] Rgb { get; }

    /// <summary>Creates an image.</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="rgb">The pixels, three bytes each.</param>
    public PdfRasterImage(int width, int height, byte[] rgb)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(rgb);
        if (rgb.Length < width * height * 3) throw new ArgumentException("The pixel buffer is too small.", nameof(rgb));
        Width = width;
        Height = height;
        Rgb = rgb;
    }

    /// <summary>The pixels are RGB.</summary>
    public string ColorSpace => "/DeviceRGB";

    /// <summary>The pixels, compressed by the writer when the document compresses its streams.</summary>
    public byte[] Encoded => Rgb;

    /// <summary>The writer applies Flate when the document compresses.</summary>
    public string? Filter => null;
}

/// <summary>An ink colour, each component from 0 (none) to 1 (full).</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
public readonly record struct PdfColor(double R, double G, double B)
{
    /// <summary>A light tint of the colour, mixed with white: the fill of a room plan.</summary>
    /// <param name="amount">How much white to mix in, 0 (the colour) to 1 (white).</param>
    public PdfColor Lighten(double amount) => new(R + (1 - R) * amount, G + (1 - G) * amount,
        B + (1 - B) * amount);
}

internal sealed record PdfLinePattern(double Spacing, double Angle, double X, double Y, double Width,
    double Shift = 0, double[]? Segments = null);

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

    internal PdfTrueTypeFont? BoldFont { get; set; }
    internal HashSet<char> UsedBoldCharacters { get; } = new();

    internal List<PdfLinePattern> LinePatterns { get; } = new();
    internal List<(double BottomY, double TopY, PdfColor Bottom, PdfColor Top)> VerticalGradients { get; } = new();

    /// <summary>The pages, in the order they were added.</summary>
    public IReadOnlyList<PdfPage> Pages => _pages;

    /// <summary>The images placed on the pages, in the order they were first drawn.</summary>
    public IReadOnlyList<IPdfImage> Images => _images;

    private readonly List<IPdfImage> _images = new();

    /// <summary>Registers an image with the document (once) and returns it.</summary>
    /// <summary>The index of an image in the document list, -1 when it is not there.</summary>
    internal int ImageIndex(IPdfImage image) => _images.IndexOf(image);

    internal IPdfImage Register(IPdfImage image)
    {
        if (!_images.Contains(image)) _images.Add(image);
        return image;
    }

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

    /// <summary>The bookmark title of the page (the sheet number and name, the view name): null for no bookmark.
    /// Pages with a title appear in the document outline, one bookmark per page in page order.</summary>
    public string? OutlineTitle { get; set; }

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
        AppendLine(x1, y1, x2, y2);
    }

    internal void DrawLine(double x1, double y1, double x2, double y2, double widthMillimetres, PdfColor color)
    {
        Stroke(widthMillimetres, 0);
        _content.Append(Number(color.R)).Append(' ').Append(Number(color.G)).Append(' ')
            .Append(Number(color.B)).Append(" RG\n");
        AppendLine(x1, y1, x2, y2);
    }

    private void AppendLine(double x1, double y1, double x2, double y2)
    {
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
        Stroke(widthMillimetres, gray);
        AppendArc(centerX, centerY, radius, startDegrees, span);
        _content.Append("S\n");
    }

    internal void DrawFilledCircle(double centerX, double centerY, double radius, double widthMillimetres,
        PdfColor fill, PdfColor outline)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(radius);
        Stroke(widthMillimetres, 0);
        _content.Append(Number(outline.R)).Append(' ').Append(Number(outline.G)).Append(' ')
            .Append(Number(outline.B)).Append(" RG\n");
        FillColor(fill);
        AppendArc(centerX, centerY, radius, 0, 360);
        _content.Append("h B\n");
    }

    private void AppendArc(double centerX, double centerY, double radius, double startDegrees, double span)
    {
        double sweep = span * Math.PI / 180;
        int segments = Math.Max(1, (int)Math.Ceiling(sweep / (Math.PI / 2)));
        double step = sweep / segments;
        double handle = 4.0 / 3.0 * Math.Tan(step / 4);
        double angle = startDegrees * Math.PI / 180;
        double x0 = centerX + radius * Math.Cos(angle), y0 = centerY + radius * Math.Sin(angle);
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
    }

    /// <summary>Draws a polyline through the points, optionally closed, either stroked or filled.</summary>
    /// <param name="points">The points, x and y in millimetres.</param>
    /// <param name="widthMillimetres">Line width in millimetres; 0 when the outline is not drawn.</param>
    /// <param name="closed">True to close the path.</param>
    /// <param name="fillGray">A fill from 0 (black) to 1 (white); null leaves the path unfilled.</param>
    public void DrawPolyline(IReadOnlyList<(double X, double Y)> points, double widthMillimetres = 0.25,
        bool closed = false, double? fillGray = null) =>
        DrawPolyline(points, widthMillimetres, closed,
            fillGray is double fill ? new PdfColor(fill, fill, fill) : null);

    /// <summary>Draws a polyline through the points, optionally closed, either stroked or filled with a colour.</summary>
    /// <param name="points">The points, x and y in millimetres.</param>
    /// <param name="widthMillimetres">Line width in millimetres; 0 when the outline is not drawn.</param>
    /// <param name="closed">True to close the path.</param>
    /// <param name="fillColor">The fill colour; null leaves the path unfilled.</param>
    public void DrawPolyline(IReadOnlyList<(double X, double Y)> points, double widthMillimetres, bool closed,
        PdfColor? fillColor)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 2) return;
        bool stroke = widthMillimetres > 0;
        if (!stroke && fillColor == null) return;
        if (stroke) Stroke(widthMillimetres, 0);
        if (fillColor is PdfColor fill) FillColor(fill);
        for (int i = 0; i < points.Count; i++)
        {
            _content.Append(Number(Points(points[i].X))).Append(' ').Append(Number(Points(points[i].Y)))
                    .Append(i == 0 ? " m " : " l ");
        }
        if (closed) _content.Append("h ");
        _content.Append(stroke && fillColor != null ? "B\n" : stroke ? "S\n" : "f\n");
    }

    internal HashSet<int> LinePatternIndices { get; } = [];
    internal HashSet<int> VerticalGradientIndices { get; } = [];

    internal void FillVerticalGradient(double x, double y, double width, double height,
        PdfColor bottom, PdfColor top)
    {
        if (!(width > 0 && height > 0)) return;
        var gradient = (Points(y), Points(y + height), bottom, top);
        int index = _document.VerticalGradients.IndexOf(gradient);
        if (index < 0)
        {
            index = _document.VerticalGradients.Count;
            _document.VerticalGradients.Add(gradient);
        }
        VerticalGradientIndices.Add(index);
        BeginClip(x, y, width, height);
        _content.Append("/Sh").Append(index).Append(" sh\n");
        EndClip();
    }

    internal void DrawRoundDashedPolyline(IReadOnlyList<(double X, double Y)> points, double width, double[] dashes)
    {
        _content.Append("q 1 J 1 j\n");
        SetDashPoints(dashes);
        DrawPolyline(points, width);
        _content.Append("Q\n");
    }

    internal void FillWithParallelLines(IReadOnlyList<(double X, double Y)> polygon, double spacing,
        double angle, double anchorX, double anchorY, double width)
        => FillWithParallelLines([polygon], spacing, angle, anchorX, anchorY, width);

    internal void FillWithParallelLines(IReadOnlyList<IReadOnlyList<(double X, double Y)>> rings, double spacing,
        double angle, double anchorX, double anchorY, double width)
        => FillWithLinePattern(rings, new PdfLinePattern(spacing, angle, anchorX, anchorY, width));

    internal void FillWithClippedParallelLines(IReadOnlyList<IReadOnlyList<(double X, double Y)>> rings,
        double spacing, double angle, double anchorX, double anchorY, double width)
    {
        if (rings.Count == 0 || rings[0].Count < 3 || !(spacing > width && width > 0)
            || !double.IsFinite(spacing) || !double.IsFinite(angle)
            || !double.IsFinite(anchorX) || !double.IsFinite(anchorY)) return;

        double sin = Math.Sin(angle), cos = Math.Cos(angle);
        double minAcross = double.PositiveInfinity, maxAcross = double.NegativeInfinity;
        double minAlong = double.PositiveInfinity, maxAlong = double.NegativeInfinity;
        foreach (var ring in rings)
        foreach (var (x, y) in ring)
        {
            double across = x * sin - y * cos, along = x * cos + y * sin;
            minAcross = Math.Min(minAcross, across);
            maxAcross = Math.Max(maxAcross, across);
            minAlong = Math.Min(minAlong, along);
            maxAlong = Math.Max(maxAlong, along);
        }
        if (!(maxAcross > minAcross && maxAlong > minAlong)) return;

        double half = width / 2;
        double phase = (anchorX * sin - anchorY * cos) % spacing;
        double first = phase + Math.Ceiling((minAcross - half - phase) / spacing) * spacing;
        double last = maxAcross + half;
        double count = 1 + Math.Floor((last - first) / spacing);
        if (!(count > 0)) return;
        // Keep tiling for very dense patterns to bound the size of the filled path.
        if (count > 4096)
        {
            FillWithParallelLines(rings, spacing, angle, anchorX, anchorY, width);
            return;
        }

        BeginPolygonClip(rings, evenOdd: false);
        FillPolygonUnion(Strips((int)count), 0);
        EndClip();

        IEnumerable<IReadOnlyList<(double X, double Y)>> Strips(int stripCount)
        {
            for (int i = 0; i < stripCount; i++)
            {
                double across = first + i * spacing;
                yield return [InPage(across - half, minAlong), InPage(across + half, minAlong),
                    InPage(across + half, maxAlong), InPage(across - half, maxAlong)];
            }
        }

        (double X, double Y) InPage(double across, double along) =>
            (across * sin + along * cos, along * sin - across * cos);
    }

    internal void FillWithLinePattern(IReadOnlyList<IReadOnlyList<(double X, double Y)>> rings, PdfLinePattern pattern)
    {
        var (spacing, angle, anchorX, anchorY, width, _, _) = pattern;
        if (rings.Count == 0 || rings[0].Count < 3 || !(spacing > width && width > 0)
            || !double.IsFinite(spacing) || !double.IsFinite(angle)
            || !double.IsFinite(anchorX) || !double.IsFinite(anchorY)) return;
        int index = _document.LinePatterns.IndexOf(pattern);
        if (index < 0)
        {
            index = _document.LinePatterns.Count;
            _document.LinePatterns.Add(pattern);
        }
        LinePatternIndices.Add(index);
        _content.Append("q /Pattern cs /P").Append(index).Append(" scn\n");
        AppendFillRings(rings);
        _content.Append("f Q\n");
    }

    // Outer rings share one orientation and holes use the opposite orientation; nonzero winding unions overlaps.
    internal List<double> FillOpacities { get; } = [];

    internal void FillPolygonUnion(IEnumerable<IReadOnlyList<(double X, double Y)>> rings, double gray,
        double opacity = 1)
    {
        if (opacity <= 0) return;
        if (opacity < 1)
        {
            int index = FillOpacities.IndexOf(opacity);
            if (index < 0)
            {
                index = FillOpacities.Count;
                FillOpacities.Add(opacity);
            }
            _content.Append("q /Gs").Append(index).Append(" gs\n");
        }
        Fill(gray);
        AppendFillRings(rings);
        _content.Append(opacity < 1 ? "f\nQ\n" : "f\n");
    }

    private void AppendFillRings(IEnumerable<IReadOnlyList<(double X, double Y)>> rings)
    {
        foreach (var ring in rings)
        {
            if (ring.Count < 3) continue;
            for (int i = 0; i < ring.Count; i++)
                _content.Append(Number(Points(ring[i].X))).Append(' ').Append(Number(Points(ring[i].Y)))
                    .Append(i == 0 ? " m " : " l ");
            _content.Append("h ");
        }
    }

    internal void BeginPolygonClip(IEnumerable<IReadOnlyList<(double X, double Y)>> rings, bool evenOdd = true)
    {
        _content.Append("q ");
        AppendFillRings(rings);
        _content.Append(evenOdd ? "W* n\n" : "W n\n");
    }

    internal void IntersectPolygonClip(IEnumerable<IReadOnlyList<(double X, double Y)>> rings)
    {
        AppendFillRings(rings);
        _content.Append("W* n\n");
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



    /// <summary>The images of the page, in the order they were drawn.</summary>
    internal List<IPdfImage> Images { get; } = new();

    /// <summary>Draws a raster image with its lower-left corner at (x, y).</summary>
    /// <param name="image">The image; it is registered with the document.</param>
    /// <param name="x">Left edge, millimetres.</param>
    /// <param name="y">Bottom edge, millimetres.</param>
    /// <param name="widthMillimetres">Width, millimetres.</param>
    /// <param name="heightMillimetres">Height, millimetres.</param>
    public void DrawImage(IPdfImage image, double x, double y, double widthMillimetres,
        double heightMillimetres)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(widthMillimetres);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightMillimetres);
        var registered = _document.Register(image);
        int index = Images.IndexOf(registered);
        if (index < 0)
        {
            Images.Add(registered);
            index = Images.Count - 1;
        }
        // The image space has its origin at the lower-left corner, so the image is flipped over its own height.
        _content.Append("q ").Append(Number(Points(widthMillimetres))).Append(" 0 0 ")
            .Append(Number(Points(heightMillimetres))).Append(' ')
            .Append(Number(Points(x))).Append(' ').Append(Number(Points(y))).Append(" cm /Im")
            .Append(index.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(" Do Q\n");
    }

    /// <summary>Sets the dash pattern of the following strokes, in millimetres.</summary>
    /// <param name="dashMillimetres">Length of a dash.</param>
    /// <param name="gapMillimetres">Length of the gap after it.</param>
    public void SetDash(double dashMillimetres, double gapMillimetres) =>
        _content.Append('[').Append(Number(Points(dashMillimetres))).Append(' ')
            .Append(Number(Points(gapMillimetres))).Append("] 0 d\n");

    /// <summary>Sets an arbitrary dash pattern of the following strokes, in PDF points
    /// (a dash array alternating dash and gap lengths, starting with a dash).</summary>
    /// <param name="points">Dash and gap lengths in points.</param>
    public void SetDashPoints(double[] points)
    {
        _content.Append('[');
        for (int i = 0; i < points.Length; i++)
        {
            if (i > 0) _content.Append(' ');
            _content.Append(Number(points[i]));
        }
        _content.Append("] 0 d\n");
    }

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
        => DrawStyledText(text, x, y, heightMillimetres, alignment, gray, useBold: false);

    internal void DrawStyledText(string text, double x, double y, double heightMillimetres,
        PdfTextAlignment alignment, double gray, bool useBold, PdfColor? ink = null)
    {
        if (string.IsNullOrEmpty(text)) return;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightMillimetres);
        double fontPoints = heightMillimetres * 72.0 / 25.4;
        // Text is painted with the non-stroking colour, so set the fill (not the stroke) colour: after a white-filled
        // bubble or floor the fill could still be white and the text would disappear.
        if (ink is { } color) FillColor(color);
        else Fill(gray);
        var bold = useBold ? _document.BoldFont : null;
        if ((bold ?? _document.Font) is { } font)
        {
            var used = bold != null ? _document.UsedBoldCharacters : _document.UsedCharacters;
            var encoded = new StringBuilder(text.Length * 4);
            double width = 0;
            foreach (char character in text)
            {
                int glyph = font.GlyphOf(character);
                used.Add(character);
                width += font.AdvanceOf(glyph) / (double)font.UnitsPerEm * heightMillimetres;
                encoded.Append(glyph.ToString("X4", CultureInfo.InvariantCulture));
            }
            double anchor = alignment switch
            {
                PdfTextAlignment.Center => x - width / 2,
                PdfTextAlignment.Right => x - width,
                _ => x,
            };
            _content.Append(bold != null ? "BT /F3 " : "BT /F2 ").Append(Number(fontPoints)).Append(" Tf ")
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

    /// <summary>The width of one line of text in millimetres, with the same font metrics
    /// <see cref="DrawText"/> paints with (the embedded font's advances, else half an em per character).</summary>
    internal double MeasureText(string text, double heightMillimetres)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        if (_document.Font is { } font)
        {
            double width = 0;
            foreach (char character in text)
                width += font.AdvanceOf(font.GlyphOf(character)) / (double)font.UnitsPerEm * heightMillimetres;
            return width;
        }
        return text.Length * heightMillimetres * 0.5;
    }

    /// <summary>Draws one line of text rotated about its anchor. With <see cref="PdfDocument.Font"/> set the text uses
    /// the embedded font; without it the standard Helvetica font is used and the text must be printable ASCII.</summary>
    /// <param name="text">The text.</param>
    /// <param name="x">Anchor x in millimetres.</param>
    /// <param name="y">Baseline y in millimetres.</param>
    /// <param name="heightMillimetres">Font size in millimetres.</param>
    /// <param name="degrees">Rotation, counter-clockwise from the horizontal.</param>
    /// <param name="alignment">Where the anchor sits along the line.</param>
    /// <param name="gray">Ink from 0 (black) to 1 (white).</param>
    public void DrawTextRotated(string text, double x, double y, double heightMillimetres, double degrees,
        PdfTextAlignment alignment = PdfTextAlignment.Left, double gray = 0)
    {
        if (string.IsNullOrEmpty(text)) return;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightMillimetres);
        double fontPoints = heightMillimetres * 72.0 / 25.4;
        Fill(gray);
        double angle = degrees * Math.PI / 180;
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        string encoded;
        double width = 0;
        if (_document.Font is { } font)
        {
            var builder = new StringBuilder(text.Length * 4);
            foreach (char character in text)
            {
                int glyph = font.GlyphOf(character);
                _document.UsedCharacters.Add(character);
                width += font.AdvanceOf(glyph) / (double)font.UnitsPerEm * heightMillimetres;
                builder.Append(glyph.ToString("X4", CultureInfo.InvariantCulture));
            }
            encoded = builder.ToString();
        }
        else
        {
            foreach (char character in text)
                if (character < ' ' || character > '~')
                    throw new ArgumentException(
                        $"The standard PDF font holds printable ASCII only; '{character}' needs an embedded font.",
                        nameof(text));
            width = text.Length * heightMillimetres * 0.5;
            encoded = string.Concat(text.Select(character => ((int)character).ToString("X4")));
            // The standard font is not a CID font: rotate it with a text matrix and a plain string.
        }
        double anchor = alignment switch
        {
            PdfTextAlignment.Center => -width / 2,
            PdfTextAlignment.Right => -width,
            _ => 0,
        };
        // The text matrix places the glyphs along the rotated x axis; the anchor is applied along that axis.
        double tx = x + cos * anchor, ty = y + sin * anchor;
        _content.Append("BT /").Append(_document.Font != null ? "F2" : "F1").Append(' ')
            .Append(Number(fontPoints)).Append(" Tf ")
            .Append(Number(cos)).Append(' ').Append(Number(sin)).Append(' ')
            .Append(Number(-sin)).Append(' ').Append(Number(cos)).Append(' ')
            .Append(Number(Points(tx))).Append(' ').Append(Number(Points(ty))).Append(" Tm ");
        if (_document.Font != null) _content.Append('<').Append(encoded).Append("> Tj ET\n");
        else _content.Append('(').Append(text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)"))
            .Append(") Tj ET\n");
    }

    private void Stroke(double widthMillimetres, double gray)
    {
        if (widthMillimetres > 0)
            _content.Append(Number(widthMillimetres * 72.0 / 25.4)).Append(" w\n");
        _content.Append(Number(Math.Clamp(gray, 0, 1))).Append(" G\n");
    }

    private void Fill(double gray) =>
        _content.Append(Number(Math.Clamp(gray, 0, 1))).Append(" g\n");

    private void FillColor(PdfColor color) =>
        _content.Append(Number(Math.Clamp(color.R, 0, 1))).Append(' ')
            .Append(Number(Math.Clamp(color.G, 0, 1))).Append(' ')
            .Append(Number(Math.Clamp(color.B, 0, 1))).Append(" rg\n");

    private static string Number(double value) =>
        Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
}
