using System.Globalization;
using System.Text;

namespace RvtIO.Pdf;

/// <summary>Writes the objects of a <see cref="PdfDocument"/> as PDF 1.4: catalog, page tree, one content stream per
/// page, a standard Helvetica font, and the cross-reference table. Everything is written uncompressed, so a page can be
/// inspected in a text editor while the writer is developed.</summary>
internal static class PdfSerializer
{
    private const int CatalogId = 1;
    private const int PagesId = 2;
    private const int FontId = 3;
    private const int FirstPageId = 4;

    private static int PageId(int index) => FirstPageId + index * 2;

    private static int ContentId(int index) => FirstPageId + index * 2 + 1;

    internal static void Write(TextWriter writer, IReadOnlyList<PdfPage> pages)
    {
        var offsets = new List<long>();
        long position = 0;
        void Raw(string text)
        {
            writer.Write(text);
            position += Encoding.ASCII.GetByteCount(text);
        }

        void BeginObject(int id)
        {
            while (offsets.Count < id) offsets.Add(0);
            offsets[id - 1] = position;
            Raw($"{id} 0 obj\n");
        }

        void EndObject() => Raw("endobj\n");

        Raw("%PDF-1.4\n");

        BeginObject(CatalogId);
        Raw($"<< /Type /Catalog /Pages {PagesId} 0 R >>\n");
        EndObject();

        BeginObject(PagesId);
        var kids = new StringBuilder();
        for (int i = 0; i < pages.Count; i++) kids.Append(PageId(i)).Append(" 0 R ");
        Raw($"<< /Type /Pages /Kids [ {kids.ToString().TrimEnd()} ] /Count {pages.Count} >>\n");
        EndObject();

        BeginObject(FontId);
        Raw("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\n");
        EndObject();

        for (int i = 0; i < pages.Count; i++)
        {
            PdfPage page = pages[i];
            var (width, height) = page.Size.InPoints;
            var pageObject = new StringBuilder("<< /Type /Page ")
                .Append($"/Parent {PagesId} 0 R /MediaBox [0 0 {Number(width)} {Number(height)}] ")
                .Append($"/Resources << /Font << /F1 {FontId} 0 R >> >> ")
                .Append($"/Contents {ContentId(i)} 0 R >>\n");
            BeginObject(PageId(i));
            Raw(pageObject.ToString());
            EndObject();

            string content = page.Content;
            BeginObject(ContentId(i));
            Raw($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n");
            Raw(content);
            Raw("\nendstream\n");
            EndObject();
        }

        long xref = position;
        Raw($"xref\n0 {offsets.Count + 1}\n");
        Raw("0000000000 65535 f \n");
        foreach (long offset in offsets)
            Raw(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Raw($"trailer\n<< /Size {offsets.Count + 1} /Root {CatalogId} 0 R >>\n");
        Raw($"startxref\n{xref}\n%%EOF\n");
    }

    private static string Number(double value) =>
        Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
}
