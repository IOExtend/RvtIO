using System.Diagnostics;
using RvtIO;
using RvtIO.Pdf;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The <c>print</c> command of the command line tool (src/RvtIO.Cli/PrintCommand): the output path is refused
/// when it names the input file through a hard link, and the PDF is written beside the target and moved over it.
/// Explicit <c>--sheet</c> names resolve against the complete catalogue before the <c>--where</c> intersection.
/// </summary>
public class PrintCommandTests
{
    private static int Run(TextWriter error, params string[] arguments) =>
        RvtIO.Cli.PrintCommand.Run(arguments, TextWriter.Null, error);

    [SkippableFact]
    public void OutputHardLinkedToTheInput_IsRefusedAndTheModelStaysIntact()
    {
        string folder = Path.Combine(Path.GetTempPath(), "rvtio-print-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string input = Path.Combine(folder, "model.rvt");
            File.WriteAllBytes(input, [1, 2, 3, 4, 5, 6, 7, 8]);
            string alias = Path.Combine(folder, "alias.pdf");
            CreateHardLink(alias, input);
            byte[] before = File.ReadAllBytes(input);

            var error = new StringWriter();
            int exit = Run(error, input, alias, "--sheet", "A102");

            Assert.Equal(RvtExitCodes.Usage, exit);
            Assert.Contains("The output path is the input file.", error.ToString());
            Assert.Equal(before, File.ReadAllBytes(input));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void WithoutOutput_IsUsageError()
    {
        var error = new StringWriter();
        Assert.Equal(RvtExitCodes.Usage, Run(error, "model.rvt"));
        Assert.Contains("output PDF path", error.ToString());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-20")]
    [InlineData("NaN")]
    public void ViewportImageWithInvalidWidth_IsUsageError(string width)
    {
        var error = new StringWriter();
        Assert.Equal(RvtExitCodes.Usage, Run(error, "model.rvt", "out.pdf", "--all-sheets",
            "--viewport-image", "199", "20", "30", width, "50", "render.jpg"));
        Assert.Contains("--viewport-image requires", error.ToString());
    }

    [Fact]
    public void WhereWithoutValue_IsUsageError()
    {
        var error = new StringWriter();
        Assert.Equal(RvtExitCodes.Usage, Run(error, "model.rvt", "out.pdf", "--sheet", "A102", "--where"));
        Assert.Contains("--where needs a filter", error.ToString());
    }

    [Fact]
    public void WhereWithBadFilter_IsUsageError()
    {
        var error = new StringWriter();
        Assert.Equal(RvtExitCodes.Usage, Run(error, "model.rvt", "out.pdf", "--sheet", "A102", "--where", "no-operator-here"));
        Assert.Contains("Bad filter", error.ToString());
    }

    [Fact]
    public void WhereWithoutSheetContext_IsUsageError()
    {
        var error = new StringWriter();
        Assert.Equal(RvtExitCodes.Usage, Run(error, "model.rvt", "out.pdf", "--level", "L1", "--where", "Drawn By=AB"));
        Assert.Contains("--where selects sheets", error.ToString());
    }

    private static (IReadOnlyList<PrintableSheet> Sheets, IReadOnlyList<ComposedSheet> Composed) TwoSheets() =>
    (
        [
            new PrintableSheet(101, "A101", "Plan", 100, 100, 1, 0),
            new PrintableSheet(102, "A102", "Plan", 100, 100, 1, 0),
        ],
        [
            new ComposedSheet(101, "A101", "Plan", PdfPageSize.A4, 1, 0),
            new ComposedSheet(102, "A102", "Plan", PdfPageSize.A4, 1, 0),
        ]
    );

    [Fact]
    public void SelectExplicitSheets_MixedMatchingAndNonMatching_KeepsOnlyTheMatch()
    {
        var (sheets, composed) = TwoSheets();

        // The review's repro: --sheet A101,A102 with --where Sheet Number=A102 prints only A102,
        // instead of failing that A101 does not exist.
        var selection = RvtIO.Cli.PrintCommand.SelectExplicitSheets(
            ["A101", "A102"], sheets, composed, new HashSet<int> { 102 });

        Assert.Null(selection.Missing);
        Assert.Equal([102], selection.ComposedIds);
        Assert.Empty(selection.SheetIds);
    }

    [Fact]
    public void SelectExplicitSheets_UnknownName_ReportsMissingBeforeFiltering()
    {
        var (sheets, composed) = TwoSheets();

        var selection = RvtIO.Cli.PrintCommand.SelectExplicitSheets(
            ["A101", "A999"], sheets, composed, new HashSet<int> { 101 });

        Assert.Equal("A999", selection.Missing);
    }

    [Fact]
    public void SelectExplicitSheets_AllFilteredOut_LeavesEmptySelection()
    {
        var (sheets, composed) = TwoSheets();

        var selection = RvtIO.Cli.PrintCommand.SelectExplicitSheets(
            ["A101"], sheets, composed, new HashSet<int> { 102 });

        Assert.Null(selection.Missing);
        Assert.Empty(selection.ComposedIds);
        Assert.Empty(selection.SheetIds);
    }

    [Fact]
    public void SelectExplicitSheets_WithoutFilter_KeepsEverything()
    {
        var (sheets, composed) = TwoSheets();

        var selection = RvtIO.Cli.PrintCommand.SelectExplicitSheets(
            ["A101", "A102"], sheets, composed, null);

        Assert.Null(selection.Missing);
        Assert.Equal([101, 102], selection.ComposedIds);
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int LinuxLink(string existing, string link);

    [System.Runtime.InteropServices.DllImport("libSystem.dylib", EntryPoint = "link", SetLastError = true)]
    private static extern int DarwinLink(string existing, string link);

    private static void CreateHardLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd.exe", "/c mklink /H \"" + link + "\" \"" + target + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(start)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0 || !File.Exists(link))
                throw new SkipException("hard links cannot be created here");
            return;
        }

        int rc = OperatingSystem.IsMacOS() ? DarwinLink(target, link) : LinuxLink(target, link);
        if (rc != 0 || !File.Exists(link))
            throw new SkipException("hard links cannot be created here");
    }
}
