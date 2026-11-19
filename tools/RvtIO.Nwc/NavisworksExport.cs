using System.ComponentModel;
using System.Diagnostics;
using RvtIO.Ifc;

namespace RvtIO.Nwc;

internal static class NavisworksExport
{
    internal static void Save(string input, string output, string navisworks)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The --navisworks export requires Autodesk Navisworks on Windows.");

        string executable = Path.GetFullPath(navisworks);
        if (!string.Equals(Path.GetFileName(executable), "Roamer.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(executable))
            throw new FileNotFoundException("Pass the installed Navisworks Manage or Simulate Roamer.exe to --navisworks.", executable);

        string destination = Path.GetFullPath(output);
        string directory = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"The output directory does not exist: '{directory}'.");

        string staging = Path.Combine(directory, $"rvtio-nwc-{Guid.NewGuid():N}");
        string ifcPath = Path.Combine(staging, Path.GetFileNameWithoutExtension(input) + ".ifc");
        string cachePath = Path.ChangeExtension(ifcPath, ".nwc");
        string logPath = Path.Combine(staging, "navisworks.log");

        var diagnostic = new RvtReadDiagnostics();
        var reader = new RvtFileReader(new RvtReaderOptions
        {
            R22Layout = true,
            SystemFamilyNameLocale = "ENU",
            SeparateFacesWithDistinctOpenings = true,
            Diagnostics = diagnostic,
        });
        var model = reader.Read(input);
        if (diagnostic.LimitHits.Count != 0 || diagnostic.Streams.Any(stream => stream.Failed != 0))
            throw new InvalidDataException("The RVT read was incomplete. Nothing was exported.");

        Directory.CreateDirectory(staging);
        try
        {
            var exporter = new IfcExporter(Console.Error, includeRevitPropertySets: true);
            exporter.Export(model, ifcPath);
            if (exporter.ExportedProducts == 0)
                throw new InvalidDataException("The RVT has no IFC products to convert.");

            using var process = Process.Start(CreateStartInfo(executable, ifcPath, cachePath, logPath))
                ?? throw new IOException("Could not start Autodesk Navisworks.");
            process.WaitForExit();

            if (process.ExitCode != 0 || !File.Exists(cachePath))
            {
                string? detail = File.Exists(logPath)
                    ? File.ReadLines(logPath).LastOrDefault(line => line.Contains("exception_message:", StringComparison.Ordinal))
                    : null;
                throw new IOException($"Navisworks failed to create or open the NWC (exit {process.ExitCode}). {detail}".TrimEnd());
            }

            File.Move(cachePath, destination);
        }
        catch (Win32Exception ex)
        {
            throw new IOException($"Could not run Autodesk Navisworks. {ex.Message}", ex);
        }
        finally
        {
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Could not remove the temporary export directory '{staging}'. {ex.Message}");
            }
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string ifc, string cache, string log)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        foreach (string argument in new[] { "-NoGui", "-log", log, "-CreateCache", ifc, "-OpenFile", cache, "-Exit" })
            info.ArgumentList.Add(argument);
        return info;
    }
}
