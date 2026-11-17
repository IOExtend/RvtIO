using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Pdf;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

public class SiteTitleTests
{
    [Fact]
    public void SampleFile_SolarAnalysisViewportIsTitled()
    {
        // A sun study's content is an image, so the composer draws no vector frame for its viewport; the title
        // line and label are sheet annotation the reference prints, and must not be dropped with the frame.
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        if (PdfFonts.TryFind(out _) == null) throw new SkipException("No PDF font is available.");
        var document = RvtDocument.Open(path);
        var sheet = document.Elements.First(e => e.CategoryId == (int)BuiltInCategory.OST_Sheets
            && e.LookupParameter(BuiltInParameter.SHEET_NUMBER)?.Value as string == "A101");
        var viewport = document.Elements.FirstOrDefault(e => e.CategoryId == (int)BuiltInCategory.OST_Viewports
            && e.OwnerViewId == sheet.Id
            && e.ViewportViewId is int viewId
            && document.GetElement(viewId) is { } analysis
            && analysis.Name == "Solar Analysis");
        if (viewport == null) throw new SkipException("Sheet A101 has no Solar Analysis viewport.");
        Assert.NotNull(viewport.ViewportLabelOrigin);
        Assert.NotNull(viewport.ViewportLabelLength);

        var pdf = SheetComposer.Render(document, sheet.Id);
        using var stream = new MemoryStream();
        pdf.Save(stream);
        string content = PdfTestContent.Read(stream.ToArray());

        // The label line inks from the stored origin for the stored length, in points.
        double expected = viewport.ViewportLabelLength!.Value * 304.8 * 72 / 25.4;
        bool found = false;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
            content, @"([\d.]+) ([\d.]+) m ([\d.]+) ([\d.]+) l"))
        {
            double x0 = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            double y0 = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            double x1 = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            double y1 = double.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (Math.Abs(y1 - y0) > 0.01) continue;
            if (Math.Abs(Math.Abs(x1 - x0) - expected) < 0.5) { found = true; break; }
        }
        Assert.True(found, $"no title line of {expected:F1} pt for the Solar Analysis viewport");
    }
}
