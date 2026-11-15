using System.Diagnostics;
using RvtIO;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The <c>print</c> command of the command line tool (src/RvtIO.Cli/PrintCommand): the output path is refused
/// when it names the input file through a hard link, and the PDF is written beside the target and moved over it.
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
