namespace RvtIO.Pdf;

/// <summary>Where the print pipeline finds a TrueType font to embed.</summary>
public static class PdfFonts
{
    /// <summary>The font files tried in order: Arial, Segoe UI and Tahoma on Windows, DejaVu Sans on Linux, Arial and
    /// Helvetica Neue on macOS.</summary>
    private static readonly string[] Candidates =
    [
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/TTF/DejaVuSans.ttf",
        "/System/Library/Fonts/Supplemental/Arial.ttf",
        "/System/Library/Fonts/Helvetica.ttc",
        @"C:\Windows\Fonts\arial.ttf",
        @"C:\Windows\Fonts\segoeui.ttf",
        @"C:\Windows\Fonts\tahoma.ttf",
    ];

    /// <summary>Loads the first font file of <see cref="Candidates"/> that exists and parses.</summary>
    /// <param name="path">The file that was used, or null when none exists.</param>
    /// <returns>The font, or null when no candidate is a TrueType font this reader supports.</returns>
    public static PdfTrueTypeFont? TryFind(out string? path)
    {
        path = null;
        foreach (string candidate in Candidates)
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var font = new PdfTrueTypeFont(candidate);
                path = candidate;
                return font;
            }
            catch (Exception exception) when (exception is NotSupportedException or IOException or UnauthorizedAccessException)
            {
                // A .ttc collection or a font without a Unicode cmap: try the next candidate.
            }
        }
        return null;
    }
}
