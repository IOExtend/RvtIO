using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Line weights (Object Styles pens + the Line Weights table): the full graphics style row,
/// the pen table and the width a category prints at (rvt-prt pens.py).</summary>
public class PensDecodeTests
{
    // Exact GStyleElem blocks of Test_2022.rvt (see GraphicsStyleMaterialTests): the graphics style 198354
    // of the family subcategory 198353 (Casement), and 36, the built-in style of a window category.
    private const string FamilyStyleBlock =
        "0gYDAMUpMyhsAAAAAggAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEAAADSBgMA/////////////////////////////////P///wAAAP/////+B9EGAwD/////AQAAAAEAAAAAAAAANjnS/9sGAwAAbAAAAA==";
    private const string BuiltInStyleBlock =
        "JAAAALEzxEqAAAAAAggAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/////40CAQAAACQAAAD/////////////////////////////////////AAAA//////4HcHvh//////8BAAAAAQAAAP/////wAQEAAAAAAAAANjnS/xoAAAAAAAAAAAAAAACAAAAA";

    [Theory]
    [InlineData(FamilyStyleBlock, 198354u, 198363, 198353)]
    [InlineData(BuiltInStyleBlock, 36u, 26, -2000016)]
    public void FullStyle_AgreesWithMaterialAndCategory(
        string base64, uint owner, int expectedMaterial, int expectedCategory)
    {
        byte[] block = Convert.FromBase64String(base64);
        uint dataSize = BitConverter.ToUInt32(block, 8);
        Assert.True(TfBlockDeserializer.TryExtractGraphicsStyleFull(block, 0, dataSize, 2050,
            out int category, out _, out _, out int pen, out int color, out int pattern, out int material));
        Assert.Equal(expectedCategory, category);
        Assert.Equal(expectedMaterial, material);
        // A pen number Revit can print (1..16), a 24-bit color and a pattern id (an element, -1 for none,
        // or a negative built-in such as -3000010 for a solid line).
        Assert.InRange(pen, 1, 16);
        Assert.InRange(color, 0, 0xFFFFFF);
        Assert.True(pattern == -1 || pattern > 0 || pattern < -3000000);
    }

    [Fact]
    public void FullStyle_IsNotReadFromAWrongClassOrABrokenFooter()
    {
        byte[] block = Convert.FromBase64String(FamilyStyleBlock);
        uint dataSize = BitConverter.ToUInt32(block, 8);
        Assert.False(TfBlockDeserializer.TryExtractGraphicsStyleFull(block, 0, dataSize, 2046,
            out _, out _, out _, out _, out _, out _, out _));
        byte[] footer = (byte[])block.Clone();
        footer[^1] ^= 0xFF;
        Assert.False(TfBlockDeserializer.TryExtractGraphicsStyleFull(footer, 0, dataSize, 2050,
            out _, out _, out _, out _, out _, out _, out _));
    }

    private static RvtDocument DocumentWithPens(
        Dictionary<int, (int CategoryId, int OwnerId, int GStyleType, int PenNumber, int Color, int LinePatternId)> styles,
        Dictionary<int, double[]> rows, double[] draft)
    {
        var info = new RevitFileInfo
        {
            GraphicsStylePens = styles,
            PenWidthTable = (rows, draft),
        };
        return RvtDocument.FromInfo(info);
    }

    private static Dictionary<int, double[]> Rows(params int[] inverted)
    {
        var rows = new Dictionary<int, double[]>();
        foreach (int inv in inverted)
        {
            var pens = new double[16];
            for (int i = 0; i < 16; i++) pens[i] = 0.001 * (i + 1);
            rows[inv] = pens;
        }
        return rows;
    }

    [Fact]
    public void PenWidth_MatchesRvtPrtModel()
    {
        // rvt-prt pens.py: test02 at 1:100, Walls cut pen 2 = 0.37 pt; the table stores feet, 864 pt per foot.
        var styles = new Dictionary<int, (int, int, int, int, int, int)>
        {
            [1] = (-2000011, -1, 2, 2, 0, -1),
        };
        double pen2Feet = 0.37 / 864.0;
        var rows = new Dictionary<int, double[]>
        {
            [100] = [0.0001, pen2Feet, 0.001, 0.002, 0.003, 0.004, 0.005, 0.006,
                0.007, 0.008, 0.009, 0.010, 0.011, 0.012, 0.013, 0.014],
        };
        var document = DocumentWithPens(styles, rows, new double[16]);

        Assert.True(PenWidths.TryGetWidth(document, -2000011, true, 100, out double widthMm));
        Assert.Equal(0.37 * 25.4 / 72.0, widthMm, 4);
    }

    [Fact]
    public void PenWidth_AppliesTheMinimumPrintedWidth()
    {
        // rvt-prt MIN_PT (1/300 in): Snowdon's pen 1 (0.216 pt) prints at 0.24 pt.
        var styles = new Dictionary<int, (int, int, int, int, int, int)>
        {
            [1] = (-2000011, -1, 1, 1, 0, -1),
        };
        var rows = new Dictionary<int, double[]>
        {
            [96] = [0.00025, 0.001, 0.002, 0.003, 0.004, 0.005, 0.006, 0.007,
                0.008, 0.009, 0.010, 0.011, 0.012, 0.013, 0.014, 0.015],
        };
        var document = DocumentWithPens(styles, rows, new double[16]);

        Assert.True(PenWidths.TryGetWidth(document, -2000011, false, 96, out double widthMm));
        Assert.Equal(0.24 * 25.4 / 72.0, widthMm, 4);
    }

    [Fact]
    public void PenWidth_PicksTheNearestTableRow()
    {
        var styles = new Dictionary<int, (int, int, int, int, int, int)>
        {
            [1] = (-2000011, -1, 2, 1, 0, -1),
        };
        var rows = Rows(50, 100);
        rows[50][0] = 0.001;
        rows[100][0] = 0.002;
        var document = DocumentWithPens(styles, rows, new double[16]);

        // 1:60 is nearer to the 1:50 row than to 1:100 by relative distance
        // (|50-60|/50 = 0.2 vs |100-60|/100 = 0.4, the rvt-prt nearest-row rule).
        Assert.True(PenWidths.TryGetWidth(document, -2000011, true, 60, out double widthMm));
        Assert.Equal(0.001 * 864.0 * 25.4 / 72.0, widthMm, 6);
    }

    [SkippableFact]
    public void GridProjection_UsesAnnotationPenOne()
    {
        // Grids are datum elements: the reference inks them 0.283 pt at every scale, which is annotation
        // pen 1 (0.1 mm), not the scale-dependent model table (0.18 mm at 1:50). The file decodes the
        // grids-projection style to pen 1.
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);

        Assert.True(PenWidths.TryGetPenNumber(document, (int)BuiltInCategory.OST_Grids, false, out int pen));
        Assert.Equal(1, pen);
        Assert.True(PenWidths.TryAnnotationWidth(document, pen, out double mm));
        Assert.Equal(0.1, mm, 3);
    }

    [Fact]
    public void PenWidth_ReturnsFalseWithoutAStyleOrTable()
    {
        var empty = DocumentWithPens(new(), new(), []);
        Assert.False(PenWidths.TryGetWidth(empty, -2000011, true, 100, out _));

        var styles = new Dictionary<int, (int, int, int, int, int, int)>
        {
            [1] = (-2000011, -1, 1, 1, 0, -1),
        };
        // Projection style only: no cut pen for walls.
        var document = DocumentWithPens(styles, Rows(100), new double[16]);
        Assert.False(PenWidths.TryGetWidth(document, -2000011, true, 100, out _));
        Assert.False(PenWidths.TryGetWidth(document, -2000011, false, 0, out _));
    }

    [SkippableFact]
    public void SampleFile_DecodesWallsCutPen3AndSixTableRows()
    {
        string? path = TestFiles.TryFind("Test_2022.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var info = document.RawData;
        Assert.NotNull(info.GraphicsStylePens);
        Assert.NotNull(info.PenWidthTable);
        // The document's own styles for Walls (-2000011): projection pen 1, cut pen 3 (rvt-prt category_pens).
        var walls = info.GraphicsStylePens!.Values
            .Where(s => s.CategoryId == -2000011 && s.OwnerId == -1)
            .ToDictionary(s => s.GStyleType, s => s.PenNumber);
        Assert.Equal(1, walls[1]);
        Assert.Equal(3, walls[2]);
        Assert.Equal(6, info.PenWidthTable!.Value.ModelRows.Count);
        Assert.Equal(16, info.PenWidthTable.Value.DraftPens.Length);
        // Walls cut at 1:100 prints with the table's pen 3 of the 1:100 row.
        Assert.True(PenWidths.TryGetWidth(document, -2000011, true, 100, out double widthMm));
        double expectedPt = Math.Max(info.PenWidthTable.Value.ModelRows[100][2] * 864.0, 0.24);
        Assert.Equal(expectedPt * 25.4 / 72.0, widthMm, 6);
    }
}
