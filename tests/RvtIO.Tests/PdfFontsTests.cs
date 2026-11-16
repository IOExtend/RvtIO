using RvtIO.Pdf;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The font search of the print pipeline: metric-compatible substitutes and extra folders.</summary>
public class PdfFontsTests
{
    [Fact]
    public void CandidateFiles_ListsSubstitutesBeforeDejaVu()
    {
        var files = PdfFonts.CandidateFiles(_ => null).ToList();

        int liberation = files.FindIndex(f => f.Contains("LiberationSans-Regular", StringComparison.Ordinal));
        int arimo = files.FindIndex(f => f.Contains("Arimo-Regular", StringComparison.Ordinal));
        int dejavu = files.FindIndex(f => f.Contains("DejaVuSans", StringComparison.Ordinal));
        Assert.True(liberation >= 0 && arimo >= 0 && dejavu >= 0);
        Assert.True(liberation < dejavu && arimo < dejavu);
    }

    [Fact]
    public void CandidateFiles_ExtraFoldersComeFirst()
    {
        string extra = string.Join(Path.PathSeparator, ["/fonts/a", "/fonts/b"]);
        var files = PdfFonts.CandidateFiles(name => name == PdfFonts.FontsEnvVar ? extra : null).ToList();

        Assert.StartsWith("/fonts/a", files[0]);
        Assert.StartsWith("/fonts/b", files[5]);
        Assert.True(files.Count > 10);
        int systemFirst = files.FindIndex(f => !f.StartsWith("/fonts/", StringComparison.Ordinal));
        Assert.Equal(10, systemFirst);
    }

    [Fact]
    public void CandidateFiles_EmptyEnvGivesMachineCandidatesOnly()
    {
        var files = PdfFonts.CandidateFiles(_ => "  ");

        Assert.DoesNotContain(files, f => f.StartsWith("/fonts/", StringComparison.Ordinal));
        Assert.Contains(files, f => f.Contains("DejaVuSans", StringComparison.Ordinal));
    }
}
