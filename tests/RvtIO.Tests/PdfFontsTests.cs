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
        // Debian Bookworm's fonts-liberation2 path, ahead of the older liberation path.
        Assert.Contains("/usr/share/fonts/truetype/liberation2/LiberationSans-Regular.ttf", files);
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

    [Fact]
    public void TruncatedFont_ReportsUnsupportedInsteadOfOverrunningTables()
    {
        // The review's repro: a 16-byte file with a valid magic and one declared
        // but truncated table directory entry.
        byte[] truncated = new byte[16];
        truncated[1] = 1;
        truncated[5] = 1;

        var error = Assert.Throws<NotSupportedException>(() => new PdfTrueTypeFont(truncated, "bad"));
        Assert.Contains("runcated", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryFind_InvalidPreferredFontFallsThroughWithoutThrowing()
    {
        string folder = Path.Combine(Path.GetTempPath(), "rvtio-bad-font-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            byte[] truncated = new byte[16];
            truncated[1] = 1;
            truncated[5] = 1;
            File.WriteAllBytes(Path.Combine(folder, "arial.ttf"), truncated);

            // Must not throw: a corrupt preferred font is skipped like an unsupported one.
            PdfFonts.TryFind(_ => folder, out string? _);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
