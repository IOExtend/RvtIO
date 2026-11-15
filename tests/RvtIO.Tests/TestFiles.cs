namespace RvtIO.Tests;

/// <summary>Where the tests find private input that is not part of the repository: Revit project files, captured class blocks
/// (TestData/*.bin) and other dumps. The folder is named by the environment variable RVTIO_TEST_FILES; when it is not set,
/// the folder test_files in the repository root (or in an ancestor of the test binary) is used. Files may lie directly in the
/// folder or in one level of subfolders (2020, 2022, ...). A test that needs a file which is not there is reported as skipped
/// with the reason, never passed.</summary>
internal static class TestFiles
{
    public const string EnvironmentVariable = "RVTIO_TEST_FILES";

    /// <summary>Folders searched for fixtures, the one named by the environment variable first.</summary>
    public static IEnumerable<string> Roots()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } configured && Directory.Exists(configured))
            yield return configured;
        for (var directory = AppContext.BaseDirectory; directory != null; directory = Path.GetDirectoryName(directory))
        {
            string candidate = Path.Combine(directory, "test_files");
            if (Directory.Exists(candidate)) yield return candidate;
        }
    }

    /// <summary>The path of the fixture with this file name, or null when it is not available.</summary>
    public static string? TryFind(string fileName)
    {
        foreach (string root in Roots())
        {
            string direct = Path.Combine(root, fileName);
            if (File.Exists(direct)) return direct;
            foreach (string subfolder in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
            {
                string nested = Path.Combine(subfolder, fileName);
                if (File.Exists(nested)) return nested;
            }
        }
        return null;
    }

    /// <summary>The path of the fixture, or a <see cref="SkipException"/> when it is not available.</summary>
    public static string Find(string fileName) => TryFind(fileName) ?? throw Missing(fileName);

    /// <summary>The path of a fixture given relative to the fixture folder (for example 2022/Test_2022.rvt), or null.</summary>
    public static string? TryFindRelative(string relative) =>
        Roots().Select(root => Path.Combine(root, relative)).FirstOrDefault(File.Exists);

    public static string FindRelative(string relative) => TryFindRelative(relative) ?? throw Missing(relative);

    /// <summary>A captured class block, TestData/&lt;name&gt; of the fixture folder (name includes the extension).</summary>
    public static Stream OpenBlock(string name) => File.OpenRead(FindRelative(Path.Combine("TestData", name)));

    /// <summary>A file a test may write (diagnostic dumps): a folder of the system temporary directory, created on demand. Each test
    /// process gets its own subfolder, so the net8.0 and net10.0 hosts of one run, and runs from other checkouts, never share a file.</summary>
    public static string Scratch(string fileName) => Path.Combine(ScratchDirectory.Value, fileName);

    private static readonly Lazy<string> ScratchDirectory = new(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "rvtio-tests", $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        // The tests delete their own files, and a folder left empty goes too.
        // shortcut: a folder that still holds diagnostic dumps stays (about 120 KB per run), prune old ones if that ever adds up.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        };
        return directory;
    });

    /// <summary>The existing file named by the environment variable, or a <see cref="SkipException"/> naming the variable.</summary>
    public static string FileFromEnvironment(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } path && File.Exists(path)
            ? path
            : throw new SkipException($"{variable} does not name an existing file.");

    public static SkipException Missing(string what) =>
        new($"Private fixture '{what.Replace('\\', '/')}' is not available. Set {EnvironmentVariable} to the folder that holds the private test files.");
}
