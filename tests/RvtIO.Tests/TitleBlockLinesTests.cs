using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Title block linework (TfBlockDeserializer.TryExtractTitleBlockLines): the GLine objects of a title
/// block family symbol, as origin + t*dirVec over their end parameters, in symbol feet (rvt-prt titleblock.py).</summary>
public class TitleBlockLinesTests
{
    [Fact]
    public void SampleFile_DecodesA1TitleBlockSymbol()
    {
        string? path = TestFiles.TryFind("Test_2022.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var lines = document.RawData.TitleBlockLines;
        Assert.NotNull(lines);
        // The A1 metric title block symbol of the sample template: 33 GLine objects (rvt-prt read_symbol).
        Assert.True(lines!.ContainsKey(382615), "Title block symbol 382615 did not decode.");
        var symbol = lines[382615];
        Assert.Equal(33, symbol.Count);
        // The outer frame, counter-clockwise from the lower-left: the first lines rvt-prt reads.
        Assert.Equal(0.0082, symbol[0].X0, 3);
        Assert.Equal(-0.0033, symbol[0].Y0, 3);
        Assert.Equal(2.7641, symbol[0].X1, 3);
        Assert.Equal(-0.0033, symbol[0].Y1, 3);
        Assert.Equal(149, symbol[0].StyleId);
        foreach (var line in symbol)
        {
            Assert.True(double.IsFinite(line.X0) && double.IsFinite(line.Y0)
                && double.IsFinite(line.X1) && double.IsFinite(line.Y1));
            Assert.True(Math.Abs(line.X0) < 1e6 && Math.Abs(line.Y0) < 1e6
                && Math.Abs(line.X1) < 1e6 && Math.Abs(line.Y1) < 1e6);
            // Title Blocks Wide (149) and Medium (150) lines, as rvt-prt style_pens names them.
            Assert.True(line.StyleId == 149 || line.StyleId == 150, $"Unexpected style {line.StyleId}.");
        }
        // Every title block instance of the file uses this symbol.
        foreach (var element in document.Elements)
        {
            if (element.CategoryId != -2000280 || element.RootClassName != "FamilyInstance") continue;
            Assert.Equal(382615, element.TypeId);
        }
    }
}
