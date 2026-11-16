namespace RvtIO.Pdf;

/// <summary>Where the print pipeline finds a TrueType font to embed.</summary>
public static class PdfFonts
{
    /// <summary>Environment variable with extra folders of font files, searched first.
    /// Several folders are separated by <c>:</c> on Linux/macOS and <c>;</c> on Windows,
    /// as in rvt-prt's <c>RVT_PRT_FONTS</c>.</summary>
    public const string FontsEnvVar = "RVTIO_FONTS";

    /// <summary>File names tried in each extra folder first: Arial and its metric-compatible
    /// substitutes (Liberation Sans, Arimo), which wrap and fit text as Arial does.</summary>
    private static readonly string[] ExtraFileNames =
    [
        "arial.ttf",
        "Arial.ttf",
        "LiberationSans-Regular.ttf",
        "Arimo-Regular.ttf",
        "DejaVuSans.ttf",
    ];

    /// <summary>The font files tried in order: Arial and its metric-compatible substitutes
    /// (Liberation Sans, Arimo) on Linux, Arial on macOS and Windows, then Segoe UI and Tahoma
    /// on Windows, DejaVu Sans and Helvetica Neue. A substitute keeps the widths of Arial,
    /// so a title wraps the same on every machine.</summary>
    private static readonly string[] Candidates =
    [
        "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
        "/usr/share/fonts/TTF/LiberationSans-Regular.ttf",
        "/usr/share/fonts/truetype/arimo/Arimo-Regular.ttf",
        "/usr/share/fonts/TTF/Arimo-Regular.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/TTF/DejaVuSans.ttf",
        "/System/Library/Fonts/Supplemental/Arial.ttf",
        "/System/Library/Fonts/Helvetica.ttc",
        @"C:\Windows\Fonts\arial.ttf",
        @"C:\Windows\Fonts\segoeui.ttf",
        @"C:\Windows\Fonts\tahoma.ttf",
    ];

    /// <summary>Loads the first font file of <see cref="Candidates"/> that exists and parses,
    /// after the folders of <see cref="FontsEnvVar"/>.</summary>
    /// <param name="path">The file that was used, or null when none exists.</param>
    /// <returns>The font, or null when no candidate is a TrueType font this reader supports.</returns>
    public static PdfTrueTypeFont? TryFind(out string? path) =>
        TryFind(Environment.GetEnvironmentVariable, out path);

    internal static PdfTrueTypeFont? TryFind(Func<string, string?> getEnv, out string? path)
    {
        path = null;
        foreach (string candidate in CandidateFiles(getEnv))
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

    /// <summary>Every font file tried in order, without touching the disk: the files of the
    /// <see cref="FontsEnvVar"/> folders first, then <see cref="Candidates"/>.</summary>
    internal static IReadOnlyList<string> CandidateFiles(Func<string, string?> getEnv)
    {
        var files = new List<string>();
        string? extra = null;
        try
        {
            extra = getEnv(FontsEnvVar);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // No extra folders then; the machine candidates still stand.
        }
        if (!string.IsNullOrWhiteSpace(extra))
        {
            foreach (string folder in extra.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                foreach (string name in ExtraFileNames)
                    files.Add(Path.Combine(folder, name));
            }
        }
        files.AddRange(Candidates);
        return files;
    }
}
