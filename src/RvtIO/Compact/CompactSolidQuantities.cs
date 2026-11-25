using RvtIO.Geometry;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Compact;

/// <summary>
/// Quantities that the wide layouts (Revit 2020 and 2023 to 2026) do not decode for the compact JSON export and that the reader
/// of the IFC export has: the file is read a second time in the Revit 2022 layout (<see cref="RvtReaderOptions.R22Layout"/>,
/// the options of the IFC export for the current profile) and the values of its elements are looked up by element id.
/// </summary>
internal sealed class CompactSolidQuantities
{
    private const int CurveElemLengthParameter = -1004005;

    /// <summary>The solids of an element at its measuring level.</summary>
    internal readonly record struct Solids(string Shape, int Level, int Count, double Volume, double Area, double LargestFace, double? PrismDepth, int Category = 0);

    private readonly Dictionary<int, double> _curveLength = [];
    private readonly Dictionary<int, Solids> _solids = [];

    private CompactSolidQuantities() { }

    /// <summary>The quantities of given elements (for tests).</summary>
    internal static CompactSolidQuantities Of(IEnumerable<(int Id, Solids Solids)> solids, IEnumerable<(int Id, double Length)> curves)
    {
        var result = new CompactSolidQuantities();
        foreach (var (id, value) in solids) result._solids[id] = value;
        foreach (var (id, length) in curves) result._curveLength[id] = length;
        return result;
    }

    /// <summary>Reads the file in the Revit 2022 layout. This second read reports no phases of its own: the single update
    /// <c>quantities</c> to <see cref="RvtReaderOptions.Progress"/> marks it, because the first read has already reported <c>complete</c>.</summary>
    internal static CompactSolidQuantities Read(string path, RvtReaderOptions options, Func<int, bool>? wantsSolids)
    {
        options.Progress?.Report(new("quantities"));
        var reader = new RvtFileReader(new RvtReaderOptions
        {
            SystemFamilyNameLocale = "ENU",
            SeparateFacesWithDistinctOpenings = true,
            R22Layout = true,
            ResolveCurrentCopies = options.ResolveCurrentCopies,
        });
        var info = reader.Read(path);
        var state = reader.LastParsingState ?? throw new InvalidOperationException("The reader kept no parsing state.");
        return From(info, state, wantsSolids);
    }

    /// <summary>Extracts quantities directly from an already-decoded Revit 2022 layout model.</summary>
    internal static CompactSolidQuantities From(RevitFileInfo info, ParsingState state, Func<int, bool>? wantsSolids)
    {
        var result = new CompactSolidQuantities();
        var previousCache = state.SymbolPlacementGeometry;
        state.SymbolPlacementGeometry ??= new(ReferenceEqualityComparer.Instance);
        try
        {
            foreach (var element in info.Elements)
            {
                if (element.Parameters.FirstOrDefault(p => p.Id == CurveElemLengthParameter) is { Value: double length })
                    result._curveLength.TryAdd(element.Id, length);
                if (element.CategoryId is int category && (wantsSolids?.Invoke(category) ?? true))
                    result._solids.TryAdd(element.Id, Measure(element, state) with { Category = category });
            }
        }
        finally { state.SymbolPlacementGeometry = previousCache; }
        return result;
    }

    /// <summary>The length (CURVE_ELEM_LENGTH, feet) of the curve of a curve element, null when the Revit 2022 reader holds none.</summary>
    internal double? CurveLength(int elementId) => _curveLength.TryGetValue(elementId, out double length) ? length : null;

    internal Solids? SolidsOf(int elementId) => _solids.TryGetValue(elementId, out var solids) ? solids : null;

    internal IEnumerable<KeyValuePair<int, Solids>> AllSolids => _solids;

    internal IEnumerable<KeyValuePair<int, double>> AllCurveLengths => _curveLength;

    internal static Solids Measure(Element element, ParsingState state)
    {
        for (int level = 1; level <= 2; level++)
            if (ElementBuilder.GeometryAtDetailLevel(element, state, level) is { BrepSolids.Count: > 0 } geometry)
                return Measure(geometry, level - 1);
        return element.Geometry is { BrepSolids.Count: > 0 } fine
            ? Measure(fine, 2)
            : new Solids("none", 2, 0, 0, 0, 0, null);
    }

    internal static Solids Measure(GeometryData geometry, int level)
    {
        int count = geometry.BrepSolids?.Count ?? 0;
        var shells = BrepMeasures.ShellsOfCurrent(geometry);
        bool plane = geometry.Faces.All(face => face.PlaneSurface);
        string shape = count == 0 ? "none" : shells.All(shell => shell.Closed) && plane ? count == 1 ? "one" : "many" : "other";
        double largest = geometry.Faces.Select(face => BrepMeasures.NetFaceArea(face) ?? 0).DefaultIfEmpty(0).Max();
        double? depth = shells.Count == 1 && shells[0].Closed && BrepMeasures.FindPrismCurrent(shells[0]) is { } prism ? prism.Depth : null;
        return new Solids(shape, level, count, shells.Sum(shell => shell.Volume), shells.Sum(shell => shell.Area), largest, depth);
    }

    /// <summary>The number a solid rule gives (<c>volume</c>, <c>half</c> the half surface, <c>maxface</c> the largest face, <c>prism</c> the depth of the
    /// prism), null when the solids do not hold it.</summary>
    internal static double? Value(Solids solids, string formula) => formula switch
    {
        "volume" => solids.Volume,
        "half" => solids.Area / 2,
        "maxface" => solids.LargestFace,
        "prism" => solids.PrismDepth,
        _ => throw new ArgumentException("Unknown solid formula " + formula, nameof(formula)),
    };
}
