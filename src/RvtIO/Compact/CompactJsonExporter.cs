using System.Globalization;
using RvtIO.Model;

namespace RvtIO.Compact;

/// <summary>What an export wrote.</summary>
/// <param name="Nodes">Number of element nodes.</param>
/// <param name="Bytes">Size of the document.</param>
/// <param name="UnresolvedClassNames">Number of nodes whose "name" is empty because their class could not be named.</param>
internal readonly record struct CompactExportStats(int Nodes, long Bytes, int UnresolvedClassNames);

/// <summary>Exports a <see cref="RevitFileInfo"/> as the compact JSON properties collection: one node per ElemTable row in
/// ascending ElementId (<see cref="CompactUniverse"/>), identity from <see cref="CompactNodeBuilder"/>, properties from an
/// <see cref="ICompactPropertySource"/> (none by default), written by <see cref="CompactJsonWriter"/> one node at a time.</summary>
public static class CompactJsonExporter
{
    private static readonly IReadOnlyList<CompactProperty> NoProperties = [];

    /// <summary>Reads a Revit file and writes its compact JSON properties collection to <paramref name="outputPath"/>.</summary>
    /// <param name="rvtPath">The .rvt or .rfa file to read.</param>
    /// <param name="outputPath">The JSON file to create or overwrite.</param>
    /// <param name="readerOptions">Options of the read; null uses the defaults. Its DiagnosticOutput also receives the export report.
    /// Its Progress gets the updates of the first read, which ends with <c>complete</c>. A file of a wide layout is then read again for
    /// quantities and reports only <c>quantities</c>, and writing the JSON is not reported.</param>
    /// <param name="options">Options of the JSON output; null uses the defaults.</param>
    public static void Export(string rvtPath, string outputPath, RvtReaderOptions? readerOptions = null, CompactJsonExportOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(rvtPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        readerOptions ??= new RvtReaderOptions();
        var diagnostics = readerOptions.DiagnosticOutput;
        var reader = new RvtFileReader(readerOptions);
        var info = reader.Read(rvtPath);
        var state = reader.LastParsingState ?? throw new InvalidOperationException("The reader kept no parsing state.");
        // The compact export reads Element.Name only where it is validated.
        RvtIO.Export.ValidatedElementNames.Apply(info, state);
        var identity = new CompactIdentity(CompactUniverse.Select(info));
        // A wide layout does not decode geometry or curves: their quantities come from a second read in the Revit 2022 layout.
        var gate = CompactValueGate.ForLayout(info.GeometryUndecoded);
        var quantities = info.GeometryUndecoded ? CompactSolidQuantities.Read(rvtPath, readerOptions, gate.HasSolidRules) : null;
        // A project (.rvt) is a document without an owner family; an .rfa is not decided.
        var source = CompactPropertySources.Create(info, state,
            rvtPath.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) ? true : null, gated: true, identity: identity, quantities: quantities);
        Export(info, outputPath, source, diagnostics, identity, options);
        if (diagnostics != null) source.Builtin.WriteReport(diagnostics);
    }

    internal static CompactExportStats Export(RevitFileInfo info, string path, ICompactPropertySource? source = null, TextWriter? diagnostics = null,
        CompactIdentity? identity = null, CompactJsonExportOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        var stats = Export(info, file, source, diagnostics, identity, options);
        diagnostics?.WriteLine($"Compact JSON written to {path}");
        return stats;
    }

    /// <summary>Writes to <paramref name="output"/> and leaves it open.</summary>
    internal static CompactExportStats Export(RevitFileInfo info, Stream output, ICompactPropertySource? source = null, TextWriter? diagnostics = null,
        CompactIdentity? identity = null, CompactJsonExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(output);
        using var diagnosticScope = DiagnosticSink.Use(diagnostics);
        var rows = CompactUniverse.Select(info);
        var builder = new CompactNodeBuilder(identity ?? new CompactIdentity(rows));
        int nodes = 0;
        long bytes;
        using (var writer = new CompactJsonWriter(output, options?.JsonClassNamePrefix ?? ""))
        {
            foreach (var element in rows)
            {
                var properties = source == null ? NoProperties
                    : source.GetProperties(element) ?? throw new InvalidDataException($"The property source returned no list for element {element.Id}.");
                writer.WriteNode(builder.Build(element, properties));
                nodes++;
            }
            writer.Complete();
            bytes = writer.BytesWritten;
        }
        var stats = new CompactExportStats(nodes, bytes, builder.UnresolvedClassNameCount);
        if (diagnostics != null) WriteDiagnostics(diagnostics, stats, builder);
        return stats;
    }

    private static void WriteDiagnostics(TextWriter diagnostics, CompactExportStats stats, CompactNodeBuilder builder)
    {
        diagnostics.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Compact JSON: {stats.Nodes} nodes, {stats.Bytes} bytes"));
        if (stats.UnresolvedClassNames > 0)
            diagnostics.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Compact JSON: {stats.UnresolvedClassNames} nodes without a class name (written empty): ")
                + string.Join("; ", builder.UnresolvedClassNames.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => string.Create(CultureInfo.InvariantCulture, $"{pair.Key} x{pair.Value}"))));
        if (builder.UnknownPseudoCount > 0)
            diagnostics.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Compact JSON: {builder.UnknownPseudoCount} nodes where the reader cannot decide whether the expected export writes the category and family objects (written without): ")
                + string.Join("; ", builder.UnknownPseudo.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .Take(12).Select(pair => string.Create(CultureInfo.InvariantCulture, $"{pair.Key} x{pair.Value}"))));
        if (builder.MissingUniqueIdCount > 0)
            diagnostics.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Compact JSON: {builder.MissingUniqueIdCount} nodes without a unique id (externalId written empty)"));
    }
}
