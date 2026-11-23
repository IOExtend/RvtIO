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
    public void MissingViewportImage_ReportsInputError()
    {
        var error = new StringWriter();
        string path = TestFiles.Scratch("missing-render.jpg");

        int exit = Run(error, "model.rvt", "out.pdf", "--all-sheets",
            "--viewport-image", "199", "20", "30", "40", "50", path);

        Assert.Equal(RvtExitCodes.InputError, exit);
        Assert.Contains($"Cannot read viewport image {path}", error.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ViewportImageWithBlankPath_IsUsageError(string path)
    {
        var error = new StringWriter();

        Assert.Equal(RvtExitCodes.Usage, Run(error, "model.rvt", "out.pdf", "--all-sheets",
            "--viewport-image", "199", "20", "30", "40", "50", path));
        Assert.Contains("--viewport-image requires", error.ToString());
    }

    [Fact]
    public void ViewportImageWithTooFewValues_IsUsageError()
    {
        var error = new StringWriter();

        Assert.Equal(RvtExitCodes.Usage, Run(error, "model.rvt", "out.pdf", "--all-sheets",
            "--viewport-image", "199", "20", "30", "40", "50"));
        Assert.Contains("--viewport-image requires", error.ToString());
    }

    [Fact]
    public void ViewportImageWithRepeatedId_IsUsageError()
    {
        var error = new StringWriter();

        Assert.Equal(RvtExitCodes.Usage, Run(error, "model.rvt", "out.pdf", "--all-sheets",
            "--viewport-image", "199", "20", "30", "40", "50", "first.jpg",
            "--viewport-image", "199", "20", "30", "40", "50", "second.jpg"));
        Assert.Contains("unique viewport ID", error.ToString());
    }

    [Theory]
    [InlineData("0", "20", "30", "40", "50")]
    [InlineData("-3", "20", "30", "40", "50")]
    [InlineData("two", "20", "30", "40", "50")]
    [InlineData("199", "NaN", "30", "40", "50")]
    [InlineData("199", "20", "Infinity", "40", "50")]
    [InlineData("199", "20", "30", "40", "0")]
    [InlineData("199", "20", "30", "40", "-5")]
    [InlineData("199", "20", "30", "40", "NaN")]
    public void ViewportImageWithInvalidNumber_IsUsageError(string id, string x, string y, string width, string height)
    {
        var error = new StringWriter();

        Assert.Equal(RvtExitCodes.Usage, Run(error, "model.rvt", "out.pdf", "--all-sheets",
            "--viewport-image", id, x, y, width, height, "render.jpg"));
        Assert.Contains("--viewport-image requires", error.ToString());
    }

    [Theory]
    [InlineData("--all-views", true)]
    [InlineData("--list-sheets", true)]
    [InlineData("--list-views", true)]
    [InlineData("--list-levels", true)]
    [InlineData("--all-views", false)]
    [InlineData("--level", false)]
    public void ViewportImageWithoutSheetSelection_IsUsageError(string option, bool withAllSheets)
    {
        var error = new StringWriter();
        var arguments = new List<string> { "model.rvt", "out.pdf" };
        if (withAllSheets) arguments.Add("--all-sheets");
        arguments.Add(option);
        if (option == "--level") arguments.Add("Level 1");
        arguments.AddRange(["--viewport-image", "199", "20", "30", "40", "50", "render.jpg"]);

        Assert.Equal(RvtExitCodes.Usage, Run(error, arguments.ToArray()));
        Assert.Contains("--viewport-image requires --sheet or --all-sheets.", error.ToString());
    }

    // The one-pixel JPEG of the writer tests: the command reads only the size from the header before it opens the model.
    private static readonly byte[] OnePixelJpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwc"
        + "KDcpLDAxNDQ0Hyc5PTgyPC4zNDL/wAALCAABAAEBAREA/8QAFAABAAAAAAAAAAAAAAAAAAAACf/EABQQAQAAAAAAAAAA"
        + "AAAAAAAAAAD/2gAIAQEAAD8AKp//2Q==");

    private static string ScratchFile(string name, byte[] bytes)
    {
        string path = TestFiles.Scratch(Guid.NewGuid().ToString("N") + "-" + name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void ViewportImageThatIsNotAJpeg_IsUsageErrorNamingTheFileWithoutParameterNames()
    {
        string image = ScratchFile("render.jpg", [1, 2, 3, 4, 5, 6, 7, 8]);
        try
        {
            var error = new StringWriter();

            int exit = Run(error, "model.rvt", "out.pdf", "--all-sheets", "--viewport-image", "199", "20", "30", "40", "50", image);

            Assert.Equal(RvtExitCodes.Usage, exit);
            Assert.Contains($"Viewport image 199 ({image}) is not a JPEG.", error.ToString());
            Assert.DoesNotContain("(Parameter", error.ToString());
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public void ViewportImageTooShortToBeAJpeg_IsUsageError()
    {
        string image = ScratchFile("short.jpg", [0xFF, 0xD8]);
        try
        {
            var error = new StringWriter();

            int exit = Run(error, "model.rvt", "out.pdf", "--all-sheets", "--viewport-image", "199", "20", "30", "40", "50", image);

            Assert.Equal(RvtExitCodes.Usage, exit);
            Assert.Contains("Viewport image 199 must be a JPEG no larger than 64 MiB.", error.ToString());
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public void ViewportImageLargerThan64MiB_IsUsageError()
    {
        // The file is only extended, so no 64 MiB are written; the command refuses it from its length alone.
        string image = TestFiles.Scratch(Guid.NewGuid().ToString("N") + "-large.jpg");
        try
        {
            using (var stream = File.Create(image)) stream.SetLength(64L * 1024 * 1024 + 1);
            var error = new StringWriter();

            int exit = Run(error, "model.rvt", "out.pdf", "--all-sheets", "--viewport-image", "199", "20", "30", "40", "50", image);

            Assert.Equal(RvtExitCodes.Usage, exit);
            Assert.Contains("Viewport image 199 must be a JPEG no larger than 64 MiB.", error.ToString());
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public void ViewportImageThatIsAFolder_ReportsInputError()
    {
        string folder = TestFiles.Scratch(Guid.NewGuid().ToString("N") + "-folder");
        Directory.CreateDirectory(folder);
        try
        {
            var error = new StringWriter();

            int exit = Run(error, "model.rvt", "out.pdf", "--all-sheets", "--viewport-image", "199", "20", "30", "40", "50", folder);

            Assert.Equal(RvtExitCodes.InputError, exit);
            Assert.Contains($"Cannot read viewport image {folder}", error.ToString());
        }
        finally
        {
            Directory.Delete(folder);
        }
    }

    [Fact]
    public void ViewportImageAsTheOutputPath_IsUsageErrorAndTheImageStaysIntact()
    {
        string image = ScratchFile("render.jpg", OnePixelJpeg);
        try
        {
            var error = new StringWriter();

            int exit = Run(error, "model.rvt", image, "--all-sheets", "--viewport-image", "199", "20", "30", "40", "50", image);

            Assert.Equal(RvtExitCodes.Usage, exit);
            Assert.Contains("The PDF output path is a viewport image input.", error.ToString());
            Assert.Equal(OnePixelJpeg, File.ReadAllBytes(image));
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public void ReadableViewportImage_LetsTheCommandGoOnToOpenTheModel()
    {
        string image = ScratchFile("render.jpg", OnePixelJpeg);
        string model = ScratchFile("model.rvt", [1, 2, 3, 4, 5, 6, 7, 8]);
        try
        {
            var error = new StringWriter();

            int exit = Run(error, model, TestFiles.Scratch("never-written.pdf"), "--all-sheets",
                "--viewport-image", "199", "20", "30", "40", "50", image);

            Assert.Equal(RvtExitCodes.InputError, exit);
            Assert.Contains($"Cannot read {Path.GetFileName(model)}", error.ToString());
        }
        finally
        {
            File.Delete(image);
            File.Delete(model);
        }
    }

    // The smallest projects of the fixture folder that have a numbered sheet: the command needs a decoded model to reach
    // the check of the viewport images against the sheets it composes.
    private static readonly Lazy<(string Path, string SheetNumber)?> SmallProject = new(FindSmallProjectWithASheet);

    private static (string Path, string SheetNumber) SmallProjectWithASheet() =>
        SmallProject.Value ?? throw new SkipException("No small private project with a numbered sheet is available.");

    private static (string Path, string SheetNumber)? FindSmallProjectWithASheet()
    {
        var candidates = TestFiles.Roots()
            .SelectMany(root => Directory.EnumerateFiles(root, "*.rvt", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateDirectories(root)
                    .SelectMany(folder => Directory.EnumerateFiles(folder, "*.rvt", SearchOption.TopDirectoryOnly))))
            .Where(path => new FileInfo(path).Length > 4096).OrderBy(path => new FileInfo(path).Length).Take(6);
        foreach (string path in candidates)
        {
            RvtDocument document;
            try { document = RvtDocument.Open(path); }
            catch (RvtInputException) { continue; }
            if (SheetComposer.Sheets(document).FirstOrDefault(sheet => sheet.Number != null)?.Number is { } number)
                return (path, number);
        }
        return null;
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void ViewportImageForAnElementThatIsNoViewport_IsUsageErrorBeforeAnyPageIsComposed(bool allSheets)
    {
        var (model, sheetNumber) = SmallProjectWithASheet();
        string image = ScratchFile("render.jpg", OnePixelJpeg);
        string output = TestFiles.Scratch(Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            var error = new StringWriter();
            string[] selection = allSheets ? ["--all-sheets"] : ["--sheet", sheetNumber];

            int exit = Run(error, [model, output, .. selection, "--viewport-image", "2000000000", "20", "30", "40", "50", image]);

            Assert.Equal(RvtExitCodes.Usage, exit);
            Assert.Contains("Element 2000000000 is not a viewport on the selected sheets.", error.ToString());
            Assert.False(File.Exists(output));
        }
        finally
        {
            File.Delete(image);
            if (File.Exists(output)) File.Delete(output);
        }
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
