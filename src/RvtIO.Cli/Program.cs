using RvtIO;
using RvtIO.Ifc;

/// <summary>The command line of the <c>rvtio</c> tool. This file holds the entry point and the usage text; the commands are in
/// ExportCommand.cs (export), ReadCommands.cs (reports through the library), DiagnosticsCommands.cs (structure reports) and
/// DeveloperCommands.cs (raw analysis).</summary>
internal static partial class Program
{
    private const string UsageText = """
        Usage: rvtio <file.rvt|file.rfa> [output.ifc|output.json] [options]
               rvtio --help | --version

        Reads a Revit file and writes IFC or JSON. A .ifc output path writes IFC; any other path writes JSON
        (default output.json). Diagnostics go to standard error.

        Export options:
          --json-full                       write the full JSON dump (every parameter and the geometry) instead of the compact JSON
          --compact-json                    write the compact JSON, which is the default for a JSON output
          --json-class-prefix <text>        text put in front of the class names in the compact JSON "name" field
          --ifc-profile <legacy|current>    IFC output profile (default: current)
          --ifc-revit-parameters            write the Revit parameters as IFC property sets
          --ifc-revit-parameter-groups      write them as one property set per parameter group (implies --ifc-revit-parameters)
          --ifc-no-base-quantities          leave out the base quantity sets
          --ifc-opening-legacy-axes         name the sides of the opening of a door or window by the frame they are measured in (Height
                                            along the wall, Width vertical), as earlier releases did; by default the opening has the Width
                                            (along the wall) and Height of the door or window, as Revit has them
          --ifc-opening-host-axes           accepted and ignored: this is now the default
          --ifc-phase <last|all|id>         phase whose state is exported: elements demolished at or before it or created after it and, unless
                                            --ifc-room-phase says otherwise, rooms of other phases are left out. Default: the last phase for the
                                            current profile, every phase for the legacy profile (IFC only)
          --ifc-room-phase <last|all|id>    rooms to export: the last phase, every phase or one native phase id (IFC only)
          --ifc-length-unit <mm|m|ft>       length unit family written to the IFC file: mm (millimetre), m (metre) or ft (foot); default: the
                                            unit stored in the project for the current profile, mm for the legacy profile (IFC only)
          --ifc-timestamp <timestamp>       UTC timestamp for the STEP FILE_NAME header (ISO-8601 or Unix epoch seconds; IFC only)
          --revit-localization <dir>        deprecated, ignored: the system family names are built in
          --r22-layout                      read the Revit 2022 layout for a JSON export too (an IFC export does it by default)
          --no-r22-layout                   keep the 64-bit schema path alone in an IFC export
          --keep-all-copies                 decode every copy of an element that several partition streams hold, instead of only the current one
                                            (a file saved in several increments; the current copy is kept by default)
          --resolve-current-copies          the earlier opt-in for the default, accepted without effect
          --report <report.json>            write the JSON report of the run (read streams, limits, skipped elements, exported fraction)
          --allow-partial                   export what was read when a reader limit cut a stream (exit code 0 instead of 3)
          --strict                          fail when a read is partial even without hitting a limit (exit code 6 instead of 0; nothing is exported)
          --raise-limits                    raise the limits that cut a damaged stream while it is recovered (very large models)

        Exit codes: 0 done; 1 usage error; 2 the input cannot be read (missing, not a Revit file, cut or damaged);
        3 a stream exceeded a reader limit and the read was cut; 4 a decoder failed on the content of a readable file;
        6 the read was partial and --strict was given.
        The report format is described in docs/export-report.md.

        Report options (read the file and print or write a report instead of exporting):
          --api-demo                        print a tour of the document API
          --inspect <id1,id2,...>           print identity, parameters and geometry counts of elements
          --diag-dump <id1,id2,...>         hex dump of the blocks of elements
          --find-str <text>                 find a UTF-16 text in the decompressed partition chunks
          --sketch-owner-dump <path.csv>    write the closed sketch loops of every sketch-owning element
          --inspect-batch <ids-file> <out.csv>    inspect many ids in one pass (ids: one per line or comma separated)
          --list-category <categoryId> <out.csv>  list the elements of one category

        Developer options (measurement and comparison dumps, not a stable interface):
          --dev-external-dump <path>        text dump of the model for CompoundStructure layers
          --dev-external-dump-prefix <regex>  class name prefix in that dump
          --dev-candidates <tsv>            compact JSON: write every listed parameter before the value gate
          --dev-trace <tsv>                 compact JSON: write why each listed parameter was written or dropped
          --dev-facts <tsv>                 compact JSON: write the facts that decide the phase parameters
          --dev-fact-names <a,b,...>        extra facts for --dev-facts
          --dev-user-params <tsv>           compact JSON: write the parameter definitions of the file
          --dev-collisions <tsv>            compact JSON: write user parameters that share a group and label
          --dev-solids <tsv>                compact JSON: write the solid quantities; with --dev-solids-only stop after it
          --dev-native <ids>                compact JSON: print the native record of elements
          downgrade <source.rvt> --target-profile <file> --check [--guid-rule exact|minimal] [--report <report.json>]
                                            (first argument) plan the class table edit that would let an older release load a newer
                                            project and report it; the profile is a project or family saved by that older release;
                                            writes nothing but the report; exit code 5 when the releases are out of scope
          save-as <source.rvt|source.rfa> [<output.rvt|output.rfa>] [--check] [--report <report.json>]
                                            (first argument) inspect the write-capability matrix (--check) or write a verified lossless
                                            copy of a local 2020-2027 project or family; exit code 5 when refused by the matrix
          coverage <file.rvt> [--report <report.json>] [--top <n>]
                                            (first argument) count the records of the partition streams that the class schema of the
                                            file walks to their last byte, per channel and class; writes nothing but the report
          coverage <file.rvt> --trace <class|owner,...> [--trace-reason <text>] [--trace-max <n>] [--trace-channel <n>]
                                            [--trace-bytes <n>] [--trace-family]
                                            (developer aid) print the field walk of single records instead of counting them
          --schema-dump <file> <out.json>   (first argument) write the class schema embedded in a file
          --schema-check <file|json> [out.txt]   (first argument) validate a class schema
          --schema-diff <a> <b> [out.txt]   (first argument) compare two class schemas
          --rebar-dump <file> ...           (first argument) dump rebar records
          --parse-block <block.bin>...      (first argument) decode raw geometry block files
          --elemtable-diag [--wide] [--wide-at n] [--wide-lo n] [--wide-hi n] [--blocks] [--no-recover] [--no-d64] [--trim]
                                            [--sweep n] [--pin1 n] [--force idx:delta,...] [--dump-partial]
          --class-census <out.json>  --tf-calibrate  --episode-check <ids>  --test-decoders <gzfile>
          --dump-chunks <dir>  --dump-storage <name> <dir>  --name-scan  --typemap  --block-map  --block-dump <0xCLASS>
          --decode-blocks  --decode-catc  --search-global <text>  --hex-search <hex>  --chunk-dump <idx>:<off>:<len>
          --block-scan <ids>  --rec-dump <ids>  --grep-inspect <ids>
        """;

    // Options that take one value, and options that take two; every other option starting with "--" is a switch.
    private static readonly HashSet<string> OneValueOptions =
    [
        "--ifc-profile", "--ifc-phase", "--ifc-room-phase", RvtIO.Cli.IfcLengthUnitOption.Name, RvtIO.Cli.IfcTimestampOption.Name, "--json-class-prefix", "--revit-localization", "--inspect", "--diag-dump", "--find-str",
        "--sketch-owner-dump", "--dev-external-dump", "--dev-external-dump-prefix", "--dev-candidates", "--dev-trace", "--dev-facts",
        "--dev-fact-names", "--dev-user-params", "--dev-collisions", "--dev-solids", "--dev-native", "--class-census", "--episode-check",
        "--test-decoders", "--dump-chunks", "--block-dump", "--search-global", "--hex-search", "--chunk-dump", "--block-scan", "--rec-dump",
        "--grep-inspect", "--wide-at", "--wide-lo", "--wide-hi", "--sweep", "--pin1", "--force", "--report",
    ];

    private static readonly HashSet<string> TwoValueOptions = ["--dump-storage", "--inspect-batch", "--list-category"];

    private static readonly HashSet<string> SwitchOptions =
    [
        "--json-full", "--compact-json", "--ifc-revit-parameters", "--ifc-revit-parameter-groups", "--ifc-no-base-quantities", ..RvtIO.Cli.OpeningAxesOption.Names, "--r22-layout",
        "--no-r22-layout", ..RvtIO.Cli.CurrentCopiesOption.Names, "--allow-partial", "--strict", "--raise-limits", "--api-demo", "--dev-solids-only", "--elemtable-diag", "--tf-calibrate", "--name-scan", "--typemap", "--block-map",
        "--decode-blocks", "--decode-catc", "--wide", "--blocks", "--no-recover", "--no-d64", "--trim", "--dump-partial",
    ];

    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine(UsageText);
            return 1;
        }
        if (args[0] is "--help" or "-h")
        {
            Console.Out.WriteLine(UsageText);
            return 0;
        }
        if (args[0] == "--version")
        {
            var version = typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
                ?? typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
            Console.Out.WriteLine($"rvtio {version.Split('+')[0]}");
            return 0;
        }

        // The library prints nothing by itself: the tool routes the diagnostics of every parser to standard error.
        using var diagnostics = DiagnosticSink.Use(Console.Error);

        // --schema-dump / --schema-check / --schema-diff: the class schema embedded in a Revit file (Formats/Latest), see TfSchemaTools.
        if (args[0].StartsWith("--schema-")) return RvtIO.Parsing.TfSchemaTools.Run(args, Console.Out, Console.Error);
        if (args[0] == "downgrade") return RvtIO.Cli.DowngradeCommand.Run(args[1..], Console.Out, Console.Error);
        if (args[0] == "save-as") return RvtIO.Cli.SaveAsCommand.Run(args[1..], Console.Out, Console.Error);
        if (args[0] == "coverage") return RvtIO.Cli.CoverageCommand.Run(args[1..], Console.Out, Console.Error);
        if (args[0] == "--rebar-dump") return RvtIO.RebarDump.Run(args[1..]);
        if (args[0] == "--parse-block") return ParseBlock(args);

        if (FindUnknownOption(args) is { } unknownOption)
        {
            Console.Error.WriteLine($"Unknown option: {unknownOption}. Run 'rvtio --help' for the list of options.");
            return 1;
        }

        string rvtPath = args[0];
        string outputPath = args.Length >= 2 && !args[1].StartsWith("--") ? args[1] : "output.json";
        string? externalDumpPath = null;
        string? externalDumpClassPrefix = null;
        string? diagDumpIds = null;
        bool apiDemo = args.Contains("--api-demo");
        bool revitParameterGroups = args.Contains("--ifc-revit-parameter-groups");
        bool includeIfcRevitParameters = args.Contains("--ifc-revit-parameters") || revitParameterGroups;
        bool baseQuantities = !args.Contains("--ifc-no-base-quantities");
        bool openingLegacyAxes = RvtIO.Cli.OpeningAxesOption.Legacy(args);
        string? roomPhaseOption = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] != "--ifc-room-phase") continue;
            if (roomPhaseOption != null || i + 1 >= args.Length
                || (args[i + 1] is not ("last" or "all") && (!int.TryParse(args[i + 1],
                    System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                    out int phase) || phase < 0)))
            {
                Console.Error.WriteLine("--ifc-room-phase requires one value: last, all or a native phase ID.");
                return 1;
            }
            roomPhaseOption = args[++i];
        }
        string? exportPhaseOption = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] != "--ifc-phase") continue;
            if (exportPhaseOption != null || i + 1 >= args.Length
                || (args[i + 1] is not ("last" or "all") && !int.TryParse(args[i + 1],
                    System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _)))
            {
                Console.Error.WriteLine("--ifc-phase requires one value: last, all or a native phase ID.");
                return 1;
            }
            exportPhaseOption = args[++i];
        }
        var profile = RvtIO.Ifc.IfcProfile.Current;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] != "--ifc-profile") continue;
            if (i + 1 >= args.Length || args[i + 1] is not ("legacy" or "current"))
            {
                Console.Error.WriteLine("--ifc-profile requires one value: legacy or current.");
                return 1;
            }
            profile = args[i + 1] == "legacy" ? RvtIO.Ifc.IfcProfile.Legacy : RvtIO.Ifc.IfcProfile.Current;
        }
        if (!RvtIO.Cli.JsonClassPrefixOption.TryParse(args, out string jsonClassPrefix, out string? jsonClassPrefixError))
        {
            Console.Error.WriteLine(jsonClassPrefixError);
            return 1;
        }
        if (!RvtIO.Cli.IfcTimestampOption.TryParse(args, out DateTimeOffset? ifcTimestamp, out string? ifcTimestampError))
        {
            Console.Error.WriteLine(ifcTimestampError);
            return 1;
        }
        if (!RvtIO.Cli.IfcLengthUnitOption.TryParse(args, out IfcUnitFamily? ifcLengthUnit, out string? ifcLengthUnitError))
        {
            Console.Error.WriteLine(ifcLengthUnitError);
            return 1;
        }
        // A JSON output is the compact data export (the properties collection); --json-full writes the full legacy dump
        // (every parameter, geometry, all fields). --compact-json is kept as the explicit spelling of the default.
        bool jsonFull = args.Contains("--json-full");
        bool isIfcOutput = outputPath.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase);
        if (jsonFull && args.Contains("--compact-json"))
        {
            Console.Error.WriteLine("--compact-json and --json-full exclude each other.");
            return 1;
        }
        if ((jsonFull || args.Contains("--compact-json")) && isIfcOutput)
        {
            Console.Error.WriteLine("--compact-json and --json-full write JSON: use a non-.ifc output path.");
            return 1;
        }
        bool compactJson = !jsonFull && !isIfcOutput;
        if (exportPhaseOption != null && !isIfcOutput)
        {
            Console.Error.WriteLine("--ifc-phase requires IFC output.");
            return 1;
        }
        if (roomPhaseOption != null && !outputPath.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("--ifc-room-phase requires IFC output.");
            return 1;
        }
        if (ifcTimestamp != null && !isIfcOutput)
        {
            Console.Error.WriteLine("--ifc-timestamp requires IFC output.");
            return 1;
        }
        if (ifcLengthUnit != null && !isIfcOutput)
        {
            Console.Error.WriteLine("--ifc-length-unit requires IFC output.");
            return 1;
        }
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--dev-external-dump") externalDumpPath = args[i + 1];
            if (args[i] == "--dev-external-dump-prefix") externalDumpClassPrefix = args[i + 1];
            if (args[i] == "--diag-dump") diagDumpIds = args[i + 1];
        }

        string? inspectIds = ValueOf(args, "--inspect");
        string? searchStr = ValueOf(args, "--find-str");
        string? recDumpIds = ValueOf(args, "--rec-dump");
        string? hexSearch = ValueOf(args, "--hex-search");
        string? blockScanIds = ValueOf(args, "--block-scan");
        string? grepInspectIds = ValueOf(args, "--grep-inspect");
        string? chunkDumpArg = ValueOf(args, "--chunk-dump");
        string? classCensusPath = ValueOf(args, "--class-census");
        string? episodeCheckIds = ValueOf(args, "--episode-check");
        string? testDecodersFile = ValueOf(args, "--test-decoders");
        string? dumpChunksDir = ValueOf(args, "--dump-chunks");
        string? blockDumpArg = ValueOf(args, "--block-dump");
        string? searchStream = ValueOf(args, "--search-global");
        string? sketchOwnerDumpPath = ValueOf(args, "--sketch-owner-dump");

        // --dump-storage <name> <outDir>
        string? dumpStorageName = null, dumpStorageDir = null;
        for (int i = 0; i < args.Length - 2; i++)
            if (args[i] == "--dump-storage") { dumpStorageName = args[i + 1]; dumpStorageDir = args[i + 2]; }
        // --inspect-batch <ids-file> <out.csv>
        string? inspectBatchIdsFile = null;
        string? inspectBatchOutPath = null;
        for (int i = 0; i < args.Length - 2; i++)
            if (args[i] == "--inspect-batch") { inspectBatchIdsFile = args[i + 1]; inspectBatchOutPath = args[i + 2]; }
        // --list-category <categoryId> <out.csv>
        string? listCategoryOutPath = null;
        int? listCategoryId = null;
        for (int i = 0; i < args.Length - 2; i++)
            if (args[i] == "--list-category" && int.TryParse(args[i + 1], out int lcCat))
            { listCategoryId = lcCat; listCategoryOutPath = args[i + 2]; }

        if (chunkDumpArg != null) return ChunkDump(rvtPath, chunkDumpArg);
        if (args.Contains("--elemtable-diag")) return ElemTableDiag(args, rvtPath);
        if (grepInspectIds != null) return GrepInspect(rvtPath, grepInspectIds);
        if (classCensusPath != null) return ClassCensus(rvtPath, classCensusPath);
        if (args.Contains("--tf-calibrate")) return TfCalibrate(rvtPath);
        if (blockScanIds != null) return BlockScan(rvtPath, blockScanIds);
        if (hexSearch != null) return HexSearch(rvtPath, hexSearch);
        if (recDumpIds != null) return RecDump(rvtPath, recDumpIds);
        if (episodeCheckIds != null) return EpisodeCheck(rvtPath, episodeCheckIds);
        if (testDecodersFile != null) return TestDecoders(testDecodersFile);
        if (dumpChunksDir != null) return DumpChunks(rvtPath, dumpChunksDir);
        if (args.Contains("--name-scan")) return NameScan(rvtPath);
        if (args.Contains("--typemap")) return TypeMap(rvtPath);
        if (args.Contains("--block-map")) return BlockMap(rvtPath);
        if (blockDumpArg != null) return BlockDump(rvtPath, blockDumpArg);
        if (args.Contains("--decode-blocks")) return DecodeBlocks(rvtPath);
        if (args.Contains("--decode-catc")) return DecodeCatc(rvtPath);
        if (searchStream != null) return SearchGlobal(rvtPath, searchStream);
        if (searchStr != null) return FindStr(rvtPath, searchStr);
        if (inspectIds != null) return Inspect(rvtPath, externalDumpPath, externalDumpClassPrefix, inspectIds);
        if (diagDumpIds != null) return DiagDumpIds(rvtPath, diagDumpIds);
        if (apiDemo) return ApiDemo(rvtPath);
        if (dumpStorageName != null && dumpStorageDir != null) return DumpStorage(rvtPath, dumpStorageName, dumpStorageDir);
        if (sketchOwnerDumpPath != null) return SketchOwnerDump(rvtPath, sketchOwnerDumpPath);
        if (inspectBatchIdsFile != null && inspectBatchOutPath != null) return InspectBatch(rvtPath, inspectBatchIdsFile, inspectBatchOutPath);
        if (listCategoryOutPath != null) return ListCategory(rvtPath, listCategoryId, listCategoryOutPath);

        return Export(new ExportArguments(args, rvtPath, outputPath, externalDumpPath, externalDumpClassPrefix, profile, isIfcOutput,
            exportPhaseOption, roomPhaseOption, includeIfcRevitParameters, revitParameterGroups, baseQuantities, jsonClassPrefix, compactJson, openingLegacyAxes, ifcTimestamp, ifcLengthUnit));
    }

    /// <summary>The value after the last occurrence of <paramref name="option"/>, null when the option is absent or ends the arguments.</summary>
    private static string? ValueOf(string[] args, string option)
    {
        string? value = null;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == option) value = args[i + 1];
        return value;
    }

    /// <summary>The first argument that starts with "--" and is neither a known option nor the value of one; null when there is none.</summary>
    private static string? FindUnknownOption(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (OneValueOptions.Contains(arg)) { i += 1; continue; }
            if (TwoValueOptions.Contains(arg)) { i += 2; continue; }
            if (arg.StartsWith("--", StringComparison.Ordinal) && !SwitchOptions.Contains(arg)) return arg;
        }
        return null;
    }
}
