using IOScene;
using RvtIO.Scene;

namespace RvtIO.Nwc;

internal static class Program
{
    private const string Usage = "Usage: RvtIO.Nwc <input.rvt> <output.nwc> [--native | --navisworks <Roamer.exe>]";

    public static int Main(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            Console.Out.WriteLine(Usage);
            return 0;
        }

        string? navisworks = null;
        bool native = args is [_, _, "--native"];
        if (native)
            args = args[..2];
        else if (args is [_, _, "--navisworks", var executable])
        {
            navisworks = executable;
            args = args[..2];
        }

        if (args is not [var input, var output] ||
            !string.Equals(Path.GetExtension(input), ".rvt", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(output), ".nwc", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine(Usage);
            return 1;
        }

        try
        {
            if (File.Exists(output))
                throw new IOException($"The output file already exists: '{output}'.");

            if (navisworks is not null)
            {
                NavisworksExport.Save(input, output, navisworks);
                Console.Out.WriteLine($"Exported '{output}' with Autodesk Navisworks.");
                return 0;
            }

            var source = new FileInfo(input);
            long length = source.Length;
            var scene = RvtSceneReader.Read(input);
            foreach (var diagnostic in scene.Diagnostics.Where(d => d.Severity != SceneSeverity.Info))
                Console.Error.WriteLine($"RVT {diagnostic.Severity} [{diagnostic.Code}]: {diagnostic.Message}");
            if (scene.Diagnostics.Any(d => d.Severity == SceneSeverity.Error))
                return 2;

            var cache = NwcExport.Build(scene.Document, source.Name, length, source.LastWriteTimeUtc, native);
            NwcExport.Save(cache, output, native);
            Console.Out.WriteLine($"Exported {cache.Meshes.Count} triangle meshes to '{output}'.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidDataException or NotSupportedException or RvtInputException or RvtLimitException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }
}
