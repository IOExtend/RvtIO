using System.Diagnostics;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The <c>sheets</c> command of the command line tool (<c>rvtio sheets &lt;file&gt; [output.json]</c>): its usage
/// and input errors. The tool is started as a process of its own, as in <see cref="CliExitCodeTests"/>.</summary>
public class SheetsCommandTests
{
    private static int Run(params string[] arguments)
    {
        var start = new ProcessStartInfo(CliExitCodeTests.DotnetExecutable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "RvtIO.Cli.dll"));
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "The tool did not end within two minutes.");
        Task.WaitAll(output, error);
        Assert.False(string.IsNullOrWhiteSpace(error.Result), "The tool reported nothing on standard error.");
        return process.ExitCode;
    }

    [Fact]
    public void WithoutFile_IsUsageError() => Assert.Equal(RvtExitCodes.Usage, Run("sheets"));

    [Fact]
    public void WithMissingFile_IsInputError()
    {
        string missing = Path.Combine(Path.GetTempPath(), "rvtio-missing-" + Guid.NewGuid().ToString("N") + ".rvt");
        Assert.Equal(RvtExitCodes.InputError, Run("sheets", missing));
    }

    [Fact]
    public void WithUnknownOption_IsUsageError() => Assert.Equal(RvtExitCodes.Usage, Run("sheets", "--nope"));

    [Fact]
    public void WithSamePathOrHardLink_IsUsageError()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rvtio-sheets-coll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string rvt = Path.Combine(dir, "model.rvt");
            CliExitCodeTests.WriteSyntheticRvt(rvt);
            byte[] initialBytes = File.ReadAllBytes(rvt);

            // Same path
            Assert.Equal(RvtExitCodes.Usage, Run("sheets", rvt, rvt));
            Assert.Equal(initialBytes, File.ReadAllBytes(rvt));

            // Hard link (if supported on platform)
            string link = Path.Combine(dir, "link.json");
            bool linked = false;
            if (OperatingSystem.IsWindows())
            {
                linked = CreateHardLink(link, rvt, IntPtr.Zero);
            }
            if (linked)
            {
                Assert.Equal(RvtExitCodes.Usage, Run("sheets", rvt, link));
                Assert.Equal(initialBytes, File.ReadAllBytes(rvt));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
