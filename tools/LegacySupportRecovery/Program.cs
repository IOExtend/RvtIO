using System.IO;

if (args is not ["initialize", _, _, _] && args is not ["restore", _, _, _, _, _])
{
    Console.Error.WriteLine("Usage: initialize <input-2021.rvt> <empty-native-2021.rvt> <output.rvt>");
    Console.Error.WriteLine("       restore <initialized-2021.rvt> <original-2023-plus.rvt> <native-2021-donor.rvt> <mapping.tsv> <output.rvt>");
    return 2;
}

try
{
    string output = Path.GetFullPath(args[^1]);
    if (File.Exists(output) || Directory.Exists(output)) throw new IOException("Choose an unused output path.");
    if (args[0] == "initialize") Octree.Initialize(args[1], args[2], output);
    else Recovery.Run(args[1..]);
    return 0;
}
catch (Exception error) when (error is IOException or InvalidDataException or FormatException or InvalidOperationException or
    KeyNotFoundException or OverflowException or ArgumentException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
