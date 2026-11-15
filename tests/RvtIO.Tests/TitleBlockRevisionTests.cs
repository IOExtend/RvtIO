using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The revision table of a title block (TitleBlockPrinter): the revisions the file stores print as rows of the
/// table, the sequence number, the description and the date of each.
/// </summary>
public class TitleBlockRevisionTests
{
    private const double FeetToMillimetres = 304.8;

    private static void Add(Element element, BuiltInParameter id, StorageType storage, object value) =>
        element.Parameters.Add(BipHelpers.MakeBipParam((int)id, id.ToString(), storage, value));

    private static Element Sheet(int id)
    {
        var sheet = new Element { Id = id, CategoryId = (int)BuiltInCategory.OST_Sheets };
        Add(sheet, BuiltInParameter.SHEET_NUMBER, StorageType.String, "A104");
        Add(sheet, BuiltInParameter.SHEET_NAME, StorageType.String, "Elev/Sec/Det");
        return sheet;
    }

    private static Element Revision(int id, int sequence, string description, string date)
    {
        var revision = new Element { Id = id, CategoryId = (int)BuiltInCategory.OST_Revisions, Name = "Revision" };
        Add(revision, BuiltInParameter.PROJECT_REVISION_SEQUENCE_NUM, StorageType.Integer, sequence);
        Add(revision, BuiltInParameter.PROJECT_REVISION_REVISION_DESCRIPTION, StorageType.String, description);
        Add(revision, BuiltInParameter.PROJECT_REVISION_REVISION_DATE, StorageType.String, date);
        return revision;
    }

    private static RevitFileInfo SheetWith(params Element[] extra)
    {
        var info = new RevitFileInfo();
        info.Elements.Add(Sheet(900));
        info.Elements.Add(new Element
        {
            Id = 901,
            CategoryId = (int)BuiltInCategory.OST_TitleBlocks,
            SerializedOwnerDbViewId = 900,
            TypeId = 902,
        });
        info.Elements.Add(new Element
        {
            Id = 902,
            CategoryId = (int)BuiltInCategory.OST_TitleBlocks,
            BoundingBox = [0, 0, 0, 841 / FeetToMillimetres, 594 / FeetToMillimetres, 0],
        });
        foreach (var element in extra) info.Elements.Add(element);
        return info;
    }

    private static int TextCount(RevitFileInfo info)
    {
        using var stream = new MemoryStream();
        SheetComposer.Render(RvtDocument.FromInfo(info), 900).Save(stream);
        string content = PdfTestContent.Read(stream.ToArray());
        int count = 0;
        for (int at = content.IndexOf("BT /F2", StringComparison.Ordinal); at >= 0;
            at = content.IndexOf("BT /F2", at + 7, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Fact]
    public void Render_PrintsARevisionRowOfTheTitleBlock()
    {
        int without = TextCount(SheetWith());
        int with = TextCount(SheetWith(Revision(903, 1, "Revision 1", "Date 1")));

        // The row adds its number, its description and its date to the strip.
        Assert.Equal(without + 3, with);
    }
}
