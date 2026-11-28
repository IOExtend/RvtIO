using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace RvtIO.Pdf;

/// <summary>Writes the objects of a <see cref="PdfDocument"/> as PDF 1.4: catalog, page tree, one content stream per
/// page, the standard Helvetica font, the embedded TrueType font when the document has one, and the cross-reference
/// table. Everything is written uncompressed, so a page can be inspected in a text editor while the writer is
/// developed.</summary>
internal static class PdfSerializer
{
    private const int CatalogId = 1;
    private const int PagesId = 2;
    private const int HelveticaId = 3;
    private const int Type0Id = 4;
    private const int FirstPageWithFont = 9;
    private const int FirstPageWithoutFont = 4;

    private static string EncodeName(string value)
    {
        var name = new StringBuilder();
        foreach (byte b in Encoding.UTF8.GetBytes(value))
        {
            if (b is >= 33 and <= 126 && !"#%()/<>[]{}".Contains((char)b))
                name.Append((char)b);
            else
                name.Append('#').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return name.ToString();
    }

    internal static void Write(Stream stream, PdfDocument document)
    {
        IReadOnlyList<PdfPage> pages = document.Pages;
        PdfTrueTypeFont? font = document.Font;
        int firstPage = font != null ? FirstPageWithFont : FirstPageWithoutFont;
        var bold = document.UsedBoldCharacters.Count > 0 ? document.BoldFont : null;
        int boldId = firstPage;
        if (bold != null) firstPage += 5;
        int PageId(int index) => firstPage + index * 2;
        int ContentId(int index) => firstPage + index * 2 + 1;
        int ImageId(int index) => firstPage + pages.Count * 2 + index;
        int PatternId(int index) => ImageId(document.Images.Count) + index;
        int GradientId(int index) => PatternId(document.LinePatterns.Count) + index;
        int outlinesId = GradientId(document.VerticalGradients.Count);

        var offsets = new List<long>();
        long position = 0;
        void Raw(string text) => WriteBytes(Encoding.ASCII.GetBytes(text));
        void WriteBytes(byte[] bytes)
        {
            stream.Write(bytes);
            position += bytes.Length;
        }
        void BeginObject(int id)
        {
            while (offsets.Count < id) offsets.Add(0);
            offsets[id - 1] = position;
            Raw($"{id} 0 obj\n");
        }
        void EndObject() => Raw("endobj\n");
        string Number(double value) => Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);

        Raw("%PDF-1.4\n");

        BeginObject(CatalogId);
        var outlines = new List<(int Page, string Title)>();
        for (int i = 0; i < pages.Count; i++)
            if (pages[i].OutlineTitle is { Length: > 0 } title)
                outlines.Add((i, title));
        if (outlines.Count > 0)
        {
            Raw($"<< /Type /Catalog /Pages {PagesId} 0 R /Outlines {outlinesId} 0 R /PageMode /UseOutlines >>\n");
        }
        else
        {
            Raw($"<< /Type /Catalog /Pages {PagesId} 0 R >>\n");
        }
        EndObject();

        BeginObject(PagesId);
        var kids = new StringBuilder();
        for (int i = 0; i < pages.Count; i++) kids.Append(PageId(i)).Append(" 0 R ");
        Raw($"<< /Type /Pages /Kids [ {kids.ToString().TrimEnd()} ] /Count {pages.Count} >>\n");
        EndObject();

        BeginObject(HelveticaId);
        Raw("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\n");
        EndObject();

        void WriteFont(PdfTrueTypeFont font, HashSet<char> characters, int id)
        {
            string name = EncodeName(font.Name);
            double scale = 1000.0 / font.UnitsPerEm;
            BeginObject(id);
            Raw($"<< /Type /Font /Subtype /Type0 /BaseFont /{name} /Encoding /Identity-H "
                + $"/DescendantFonts [ {(id + 1)} 0 R ] /ToUnicode {(id + 4)} 0 R >>\n");
            EndObject();

            var widths = new StringBuilder();
            var used = new SortedSet<int>();
            foreach (char character in characters) used.Add(font.GlyphOf(character));
            foreach (int glyph in used)
            {
                widths.Append(glyph).Append(" [ ")
                      .Append(Number(font.AdvanceOf(glyph) * scale)).Append(" ] ");
            }
            BeginObject((id + 1));
            Raw($"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{name} "
                + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
                + $"/FontDescriptor {(id + 2)} 0 R /CIDToGIDMap /Identity /DW 1000 /W [ {widths.ToString().TrimEnd()} ] >>\n");
            EndObject();

            BeginObject((id + 2));
            Raw($"<< /Type /FontDescriptor /FontName /{name} /Flags 32 "
                + $"/FontBBox [ {Number(font.XMin * scale)} {Number(font.YMin * scale)} "
                + $"{Number(font.XMax * scale)} {Number(font.YMax * scale)} ] /ItalicAngle 0 "
                + $"/Ascent {Number(font.Ascent * scale)} /Descent {Number(font.Descent * scale)} "
                + $"/CapHeight {Number(font.Ascent * 0.7 * scale)} /StemV 80 /FontFile2 {(id + 3)} 0 R >>\n");
            EndObject();

            byte[] file = font.File_;
            byte[] fontPayload = document.Compress ? Zlib(file) : file;
            BeginObject((id + 3));
            Raw($"<< /Length {fontPayload.Length} /Length1 {file.Length}"
                + (document.Compress ? " /Filter /FlateDecode" : string.Empty) + " >>\nstream\n");
            WriteBytes(fontPayload);
            Raw("\nendstream\n");
            EndObject();

            BeginObject((id + 4));
            string cmap = ToUnicode(characters, font);
            byte[] cmapBytes = Encoding.ASCII.GetBytes(cmap);
            byte[] cmapPayload = document.Compress ? Zlib(cmapBytes) : cmapBytes;
            Raw($"<< /Length {cmapPayload.Length}"
                + (document.Compress ? " /Filter /FlateDecode" : string.Empty) + " >>\nstream\n");
            WriteBytes(cmapPayload);
            Raw("\nendstream\n");
            EndObject();
        }

        if (font != null) WriteFont(font, document.UsedCharacters, Type0Id);
        if (bold != null) WriteFont(bold, document.UsedBoldCharacters, boldId);

        for (int i = 0; i < pages.Count; i++)
        {
            PdfPage page = pages[i];
            var (width, height) = page.Size.InPoints;
            string fonts = font != null ? $"/F1 {HelveticaId} 0 R /F2 {Type0Id} 0 R" : $"/F1 {HelveticaId} 0 R";
            if (bold != null) fonts += $" /F3 {boldId} 0 R";
            var xobjects = new StringBuilder();
            for (int k = 0; k < page.Images.Count; k++)
                xobjects.Append($"/Im{k} {ImageId(document.ImageIndex(page.Images[k]))} 0 R ");
            string resources = $"/Resources << /Font << {fonts} >>";
            if (xobjects.Length > 0) resources += $" /XObject << {xobjects.ToString().TrimEnd()} >>";
            if (page.LinePatternIndices.Count > 0)
                resources += $" /Pattern << {string.Join(" ", page.LinePatternIndices.Order()
                    .Select(index => $"/P{index} {PatternId(index)} 0 R"))} >>";
            if (page.VerticalGradientIndices.Count > 0)
                resources += $" /Shading << {string.Join(" ", page.VerticalGradientIndices.Order()
                    .Select(index => $"/Sh{index} {GradientId(index)} 0 R"))} >>";
            if (page.FillOpacities.Count > 0)
                resources += $" /ExtGState << {string.Join(" ", page.FillOpacities.Select((opacity, index) =>
                    $"/Gs{index} << /Type /ExtGState /ca {Number(opacity)} >>"))} >>";
            resources += " >>";
            BeginObject(PageId(i));
            Raw($"<< /Type /Page /Parent {PagesId} 0 R /MediaBox [0 0 {Number(width)} {Number(height)}] "
                + resources + $" /Contents {ContentId(i)} 0 R >>\n");
            EndObject();

            byte[] content = Encoding.ASCII.GetBytes(page.Content);
            byte[] payload = document.Compress ? Zlib(content) : content;
            BeginObject(ContentId(i));
            Raw($"<< /Length {payload.Length}" + (document.Compress ? " /Filter /FlateDecode" : string.Empty)
                + " >>\nstream\n");
            WriteBytes(payload);
            Raw("\nendstream\n");
            EndObject();
        }

        // The images, after the pages: a page names them in its /XObject resources.
        for (int k = 0; k < document.Images.Count; k++)
        {
            IPdfImage image = document.Images[k];
            bool flate = image.Filter == null && document.Compress;
            byte[] pixels = flate ? Zlib(image.Encoded) : image.Encoded;
            string filter = image.Filter != null ? $" /Filter /{image.Filter}"
                : flate ? " /Filter /FlateDecode" : string.Empty;
            BeginObject(ImageId(k));
            Raw($"<< /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} "
                + $"/ColorSpace {image.ColorSpace} /BitsPerComponent 8" + filter
                + (image is PdfRasterImage { Interpolate: true } ? " /Interpolate true" : string.Empty)
                + (image is PdfRasterImage { TransparentWhite: true } ? " /Mask [255 255 255 255 255 255]" : string.Empty)
                + $" /Length {pixels.Length} >>\nstream\n");
            WriteBytes(pixels);
            Raw("\nendstream\n");
            EndObject();
        }

        const double pointsPerMillimetre = 72.0 / 25.4;
        double span = pages.Select(p => p.Size.InPoints.Width + p.Size.InPoints.Height).DefaultIfEmpty().Max();
        for (int k = 0; k < document.LinePatterns.Count; k++)
        {
            var (spacing, angle, x, y, width, shift, segments) = document.LinePatterns[k];
            double step = spacing * pointsPerMillimetre;
            double inkWidth = width * pointsPerMillimetre;
            double sin = Math.Sin(angle), cos = Math.Cos(angle);
            if (segments != null)
            {
                double period = segments.Sum() * pointsPerMillimetre;
                double skew = Math.IEEERemainder(shift * pointsPerMillimetre, period) / step;
                double half = inkWidth / 2, pad = Math.Abs(skew) * half;
                static string Exact(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
                string dashedMatrix = $"{Exact(cos)} {Exact(sin)} {Exact(-sin + cos * skew)} {Exact(cos + sin * skew)} " +
                    $"{Exact(x * pointsPerMillimetre)} {Exact(y * pointsPerMillimetre)}";
                var tile = new StringBuilder("0 g\n");
                double start = 0;
                for (int i = 0; i < segments.Length; i++)
                {
                    double end = start + segments[i] * pointsPerMillimetre;
                    if (i % 2 == 0 && end > start)
                    {
                        double slant = skew * half;
                        tile.Append($"{Exact(start + slant)} {Exact(-half)} m {Exact(end + slant)} {Exact(-half)} l " +
                            $"{Exact(end - slant)} {Exact(half)} l {Exact(start - slant)} {Exact(half)} l h ");
                    }
                    start = end;
                }
                tile.Append("f\n");
                BeginObject(PatternId(k));
                Raw($"<< /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 2 " +
                    $"/BBox [{Exact(-pad)} {Exact(-step / 2)} {Exact(period + pad)} {Exact(step / 2)}] " +
                    $"/XStep {Exact(period)} /YStep {Exact(step)} /Matrix [{dashedMatrix}] /Resources << >> /Length {tile.Length} >>\nstream\n");
                Raw(tile.ToString());
                Raw("endstream\n");
                EndObject();
                continue;
            }
            double normalOffset = ((x * sin - y * cos) * pointsPerMillimetre) % step - step / 2;
            string matrix = $"{Number(sin)} {Number(-cos)} {Number(cos)} {Number(sin)} " +
                $"{Number(sin * normalOffset)} {Number(-cos * normalOffset)}";
            // One tile spans the page along the line, so the print has no seams between line ends.
            string ink = $"0 g {Number((step - inkWidth) / 2)} {Number(-span)} {Number(inkWidth)} {Number(2 * span)} re f\n";
            BeginObject(PatternId(k));
            Raw($"<< /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 2 /BBox [0 {Number(-span)} {Number(step)} {Number(span)}] " +
                $"/XStep {Number(step)} /YStep {Number(2 * span)} /Matrix [{matrix}] /Resources << >> /Length {ink.Length} >>\nstream\n");
            Raw(ink);
            Raw("endstream\n");
            EndObject();
        }

        for (int k = 0; k < document.VerticalGradients.Count; k++)
        {
            var (bottomY, topY, bottom, top) = document.VerticalGradients[k];
            string Rgb(PdfColor color) => $"{Number(color.R)} {Number(color.G)} {Number(color.B)}";
            BeginObject(GradientId(k));
            Raw($"<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 {Number(bottomY)} 0 {Number(topY)}] " +
                $"/Function << /FunctionType 2 /Domain [0 1] /C0 [{Rgb(bottom)}] /C1 [{Rgb(top)}] /N 1 >> >>\n");
            EndObject();
        }

        // The document outline, after the resources: one bookmark per titled page, in page order, each pointing
        // at its page. Untitled documents write no outline, as before.
        if (outlines.Count > 0)
        {
            int firstItem = outlinesId + 1;
            BeginObject(outlinesId);
            Raw($"<< /Type /Outlines /First {firstItem} 0 R /Last {firstItem + outlines.Count - 1} 0 R "
                + $"/Count {outlines.Count} >>\n");
            EndObject();
            for (int o = 0; o < outlines.Count; o++)
            {
                int id = firstItem + o;
                string prev = o > 0 ? $" /Prev {id - 1} 0 R" : string.Empty;
                string next = o < outlines.Count - 1 ? $" /Next {id + 1} 0 R" : string.Empty;
                BeginObject(id);
                Raw($"<< /Title {OutlineTitle(outlines[o].Title)} /Parent {outlinesId} 0 R{prev}{next} "
                    + $"/Dest [{PageId(outlines[o].Page)} 0 R /Fit] >>\n");
                EndObject();
            }
        }

        long xref = position;
        Raw($"xref\n0 {offsets.Count + 1}\n");
        Raw("0000000000 65535 f \n");
        foreach (long offset in offsets)
            Raw(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Raw($"trailer\n<< /Size {offsets.Count + 1} /Root {CatalogId} 0 R >>\n");
        Raw($"startxref\n{xref}\n%%EOF\n");
    }

    /// <summary>A bookmark title as a PDF string: printable ASCII in parentheses, anything else as UTF-16BE
    /// hex (sheet names can be Cyrillic).</summary>
    private static string OutlineTitle(string title)
    {
        bool ascii = true;
        foreach (char character in title)
            if (character < ' ' || character > '~') { ascii = false; break; }
        if (ascii)
            return "(" + title.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)") + ")";
        var bytes = new byte[2 + title.Length * 2];
        bytes[0] = 0xFE; bytes[1] = 0xFF;
        for (int i = 0; i < title.Length; i++)
        {
            bytes[2 + i * 2] = (byte)(title[i] >> 8);
            bytes[2 + i * 2 + 1] = (byte)(title[i] & 0xFF);
        }
        return "<" + BitConverter.ToString(bytes).Replace("-", string.Empty) + ">";
    }

    /// <summary>Zlib (RFC 1950) wrapping of the payload, which the PDF /FlateDecode filter reads.</summary>
    private static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(data, 0, data.Length);
        return output.ToArray();
    }

    private static string ToUnicode(HashSet<char> characters, PdfTrueTypeFont font)
    {
        var byGlyph = new SortedDictionary<int, char>();
        foreach (char character in characters)
            byGlyph.TryAdd(font.GlyphOf(character), character);
        var text = new StringBuilder();
        text.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n")
            .Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n")
            .Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n")
            .Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n")
            .Append($"{byGlyph.Count} beginbfchar\n");
        foreach (var (glyph, character) in byGlyph)
            text.Append('<').Append(glyph.ToString("X4", CultureInfo.InvariantCulture)).Append("> <")
                .Append(((int)character).ToString("X4", CultureInfo.InvariantCulture)).Append(">\n");
        text.Append("endbfchar\nendcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return text.ToString();
    }
}
