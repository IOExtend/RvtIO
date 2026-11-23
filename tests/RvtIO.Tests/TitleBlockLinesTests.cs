using RvtIO.Model;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Title block symbol (TfBlockDeserializer.TryExtractTitleBlockSymbol): the GLine linework, fixed texts
/// (TextNoteData + RtfData) and labels (TagNoteData) of a title block family symbol, in symbol feet
/// (rvt-prt titleblock.py).</summary>
public class TitleBlockLinesTests
{
    [Theory]
    [InlineData(0x200, -9)]
    [InlineData(0x400, -3)]
    [InlineData(0x800, 3)]
    public void FirstBaseline_UsesTheStoredVerticalAnchor(int flags, double expected)
    {
        Assert.Equal(expected, SheetComposer.TitleBlockFirstBaseline(9, 12, flags));
    }

    [Fact]
    public void SheetScale_ReportsMixedScalesInTheOutputLanguage()
    {
        Assert.Empty(SheetComposer.TitleBlockScale([], false));
        Assert.Equal(" 1 : 100", SheetComposer.TitleBlockScale([100], false));
        Assert.Equal("As indicated", SheetComposer.TitleBlockScale([50, 100], false));
        Assert.Equal("Как указано", SheetComposer.TitleBlockScale([50, 100], true));
    }

    [SkippableFact]
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

    [SkippableFact]
    public void SampleFile_DecodesTitleBlockTextsAndLabels()
    {
        string? path = TestFiles.TryFind("Test_2022.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var texts = document.RawData.TitleBlockTexts;
        var labels = document.RawData.TitleBlockLabels;
        Assert.NotNull(texts);
        Assert.NotNull(labels);
        // 10 fixed texts and 10 labels (rvt-prt read_symbol on 382615).
        Assert.True(texts!.ContainsKey(382615), "Title block texts did not decode.");
        Assert.True(labels!.ContainsKey(382615), "Title block labels did not decode.");
        Assert.Equal(10, texts[382615].Count);
        Assert.Equal(10, labels[382615].Count);
        // The first fixed texts rvt-prt reads: the URL and the "Scale" caption.
        var first = texts[382615][0];
        var parsed = RvtIO.Parsing.RtfText.Parse(first.Rtf, 0, first.Rtf.Length);
        Assert.NotNull(parsed);
        Assert.Contains("autodesk.com", parsed!.Value.Text);
        var second = RvtIO.Parsing.RtfText.Parse(texts[382615][1].Rtf, 0, texts[382615][1].Rtf.Length);
        Assert.Equal("Scale", second!.Value.Text.Trim());
        // The labels print the sheet and project parameters (BuiltInParameter ids).
        var paramsSeen = labels[382615].Select(label => label.ParamId).ToHashSet();
        foreach (int expected in new[] { -1007400, -1007401, -1007402, -1007403, -1007404, -1007405, -1006316, -1006317, -1006319, -1006321 })
            Assert.Contains(expected, paramsSeen);
        foreach (var label in labels[382615])
        {
            Assert.True(double.IsFinite(label.Ox) && double.IsFinite(label.Oy));
            Assert.True(label.Height > 0 && label.Width >= 0);
        }
        var heightByType = document.RawData.TitleBlockCapHeightsFeet!;
        var number = Assert.Single(labels[382615], label => label.ParamId == -1007401);
        Assert.Equal(8, Assert.Contains(number.AttrId, heightByType) * 304.8, 7);
        Assert.True(Assert.Single(labels[382615], label => label.ParamId == -1007400).Wrap);
        var scale = Assert.Single(labels[382615], label => label.ParamId == -1007402);
        Assert.Equal(5, Assert.Contains(scale.AttrId, heightByType) * 304.8, 7);
    }
}
