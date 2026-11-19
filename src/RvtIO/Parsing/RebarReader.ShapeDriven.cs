using System.Globalization;

namespace RvtIO.Parsing;

/// <summary>Shape driven bars that the file stores no curves for (Revit 2020 and 2022): what the RebarInstance, the RebarShape, the hook
/// types and the bar type hold is read here and the curves are rebuilt by <see cref="RebarShapeSolver"/>.</summary>
internal static partial class RebarReader
{
    /// <summary>The key of the Trf that follow the last counted array of a RebarInstance (m_shapeInstTrf: the frame of the shape).</summary>
    private const string Trailing = "trailing";

    /// <summary>The inclusion flags of the bar positions (RebarInstBase.m_includedBars), empty when the walk captured none.</summary>
    private static List<long> IncludedFlags(IEnumerable<WideBlockWalker.Capture> captures) =>
        captures.Where(c => c.Key.StartsWith(WideBlockWalker.AllItemsKeyPrefix + "RebarInstBase.m_includedBars@", StringComparison.Ordinal)).Select(c => c.Integer).ToList();

    /// <summary>The real value captured under the key, null when the walk captured none.</summary>
    private static double? RealOf(IEnumerable<WideBlockWalker.Capture>? captures, string key)
    {
        foreach (var c in captures ?? []) if (c.Key == key && c.Kind == WideBlockWalker.CaptureKind.Real) return c.Real;
        return null;
    }

    /// <summary>The integer captured under the key, null when the walk captured none (unlike <c>Integer</c>, which yields 0).</summary>
    private static long? IntegerOf(IEnumerable<WideBlockWalker.Capture>? captures, string key)
    {
        foreach (var c in captures ?? []) if (c.Key == key && c.Kind == WideBlockWalker.CaptureKind.Integer) return c.Integer;
        return null;
    }

    /// <summary>Bend diameters of a bar type and its hook length settings (hook, offset, tangent length; -1 for none) by hook type id.</summary>
    internal sealed record BarKind(double StandardBend, double StandardHookBend, double StirrupTieBend,
        IReadOnlyDictionary<int, (double Hook, double Offset, double Tangent)> HookLengths);

    internal sealed record HookKind(double Angle, double Multiplier, int Style);

    /// <summary>The shape of a shape driven bar: per edge its direction in the plane and the id of the parameter that holds its dimension,
    /// per vertex its turn, the style (0 standard, 1 stirrup / tie) and the orientation of the two hooks (1 left, 3 right).
    /// The middle edge of a joggle of three edges may be given by its length and two projections (the id of the parameter that holds the length
    /// of its projection and the direction projected on): it has no fixed direction (a zero vector in <c>Directions</c>) and <c>Projections</c> are its two.</summary>
    internal sealed record ShapeDefinition(IReadOnlyList<double[]> Directions, IReadOnlyList<long> ParameterIds, IReadOnlyList<int> Turns,
        int Style, IReadOnlyList<long> HookOrientation, IReadOnlyList<(long ParameterId, double[] Direction)>? Projections = null);

    /// <summary>A shape driven bar with no cached curves: what its RebarInstance holds to rebuild them.</summary>
    internal sealed record PendingBar(int BarTypeId, int ShapeId, int HookId0, int HookId1, long HookOrientation0, long HookOrientation1, double BarLength,
        IReadOnlyDictionary<long, int> ParameterIndex, IReadOnlyList<double> Doubles, IReadOnlyList<double[]> Positions, IReadOnlyList<long> Included,
        double[] Frame, bool InAssembly, bool Held = false);

    /// <summary>True when the queued objects from <paramref name="first"/> on are the constraints of the edges of a shape and their values
    /// are those of constraints: unit directions in the plane, small reference types, positive parameter ids. A start position of the read
    /// that is off by a few bytes yields none of these.</summary>
    private static bool ConstraintsValid(WideBlockWalker walker, WideBlockWalker.Walk walk, int first)
    {
        var byQueue = ByQueue(walk);
        for (int q = first; q < walk.Queue.Count; q++)
        {
            var caps = byQueue.GetValueOrDefault(q);
            switch (walker.Class(walk.Queue[q])?.Name)
            {
                case "RebarShapeConstraintFixedEdgeDir":
                    if (RealOf(caps, "XYZ.m_xyz@0") is not double x || RealOf(caps, "XYZ.m_xyz@1") is not double y || RealOf(caps, "XYZ.m_xyz@2") is not double z
                        || z != 0 || Math.Abs(x * x + y * y - 1) > 1e-9) return false;
                    break;
                case "RebarShapeConstraintEdgeLength":
                    if (IntegerOf(caps, "RebarShapeConstraintEdgeLength.m_paramId") is not > 0 and < 1_000_000_000
                        || IntegerOf(caps, "RebarShapeConstraintEdgeLength.m_refType@0") is not >= 0 and < 16
                        || IntegerOf(caps, "RebarShapeConstraintEdgeLength.m_refType@1") is not >= 0 and < 16) return false;
                    break;
                default:
                    return false;
            }
        }
        return true;
    }

    private static BarKind? ReadBarKind(Dictionary<int, List<WideBlockWalker.Capture>> byQueue)
    {
        var root = byQueue.GetValueOrDefault(-1);
        if (root == null || RealOf(root, "RebarBarType.m_standardBendDiam") is not double standard || RealOf(root, "RebarBarType.m_standardHookBendDiam") is not double hook
            || RealOf(root, "RebarBarType.m_stirrupTieBendDiam") is not double stirrup) return null;
        var lengths = new Dictionary<int, (double, double, double)>();
        int id = 0;
        double length = -1, offset = -1;
        foreach (var c in root)
        {
            if (c.Key == "std::pair< ElementId, HookLengthSettings >.first") id = (int)c.Integer;
            else if (c.Key == "HookLengthSettings.m_hookLength") length = c.Real;
            else if (c.Key == "HookLengthSettings.m_offsetLength") offset = c.Real;
            else if (c.Key == "HookLengthSettings.m_tangentLength") lengths[id] = (length, offset, c.Real);
        }
        return new BarKind(standard, hook, stirrup, lengths);
    }

    /// <summary>The definition by segments of a RebarShape (null for anything but straight edges with a fixed direction and a length
    /// parameter between plain bend vertices of a planar shape, the only kind supported).</summary>
    private static ShapeDefinition? ReadShapeDefinition(WideBlockWalker walker, WideBlockWalker.Walk walk, Dictionary<int, List<WideBlockWalker.Capture>> byQueue)
    {
        var root = byQueue.GetValueOrDefault(-1);
        int definition = walk.Queue.FindIndex(c => walker.Class(c)?.Name == "RebarShapeDefinitionBySegments");
        if (root == null || definition < 0 || !byQueue.TryGetValue(definition, out var caps)) return null;
        // the pointer is captured as a class tag: 0 for none
        if (root.Where(c => c.Key == "RebarShape.m_oMultiplanarDefinition").Select(c => (long?)c.Integer).FirstOrDefault() != 0) return null;
        if (IntegerOf(root, "RebarShape.m_stirrupTieAttachment") != 0 || IntegerOf(root, "RebarShape.m_higherEnd") != 0
            || (RealOf(root, "RebarShape.m_orientationAngle@0") ?? 0) != 0 || (RealOf(root, "RebarShape.m_orientationAngle@1") ?? 0) != 0) return null;
        if (IntegerOf(root, "RebarShape.m_style") is not long style || IntegerOf(root, "RebarShape.m_hookOri@0") is not long ori0 || IntegerOf(root, "RebarShape.m_hookOri@1") is not long ori1)
            return null;
        long edgeCount = IntegerOf(caps, "RebarShapeDefinitionBySegments.m_edges#") ?? -1, vertexCount = IntegerOf(caps, "RebarShapeDefinitionBySegments.m_vertices#") ?? -1;
        var children = Children(walk, definition).ToList();
        // an edge has two constraints (length and fixed direction); the middle edge of a joggle has three (length and two projections)
        var constraintCounts = caps.Where(c => c.Key == "RebarShapeEdge.m_constraints#" && c.Kind == WideBlockWalker.CaptureKind.Integer).Select(c => (int)c.Integer).ToList();
        if (edgeCount < 1 || vertexCount != edgeCount + 1 || constraintCounts.Count != edgeCount
            || constraintCounts.Where((n, i) => n != 2 && !(n == 3 && edgeCount == 3 && i == 1)).Any() || children.Count != constraintCounts.Sum()) return null;
        IEnumerable<long> Values(string key) => caps.Where(c => c.Key == key && c.Kind == WideBlockWalker.CaptureKind.Integer).Select(c => c.Integer);
        if (Values("RebarShapeEdge.m_type").Any(t => t != 0)
            || Values("RebarShapeEdge.m_arcHeightParamId").Any(v => v != -1) || Values("RebarShapeEdge.m_radiusParamId").Any(v => v != -1)
            || Values("RebarShapeVertex.m_constraints#").Any(n => n != 0) || Values("RebarShapeVertex.m_bendAngle").Any(a => a is not (1 or 3))) return null;
        var turns = Values("RebarShapeVertex.m_turn").Select(t => (int)t).ToList();
        if (turns.Count != vertexCount || turns.Any(t => t is < -1 or > 1)) return null;
        var directions = new List<double[]>();
        var parameters = new List<long>();
        var projections = new List<(long, double[])>();
        int offset = 0;
        for (int edge = 0; edge < edgeCount; edge++)
        {
            long? parameter = null;
            double[]? direction = null;
            var edgeProjections = new List<(long, double[])>();
            foreach (int child in children.Skip(offset).Take(constraintCounts[edge]))
            {
                if (!byQueue.TryGetValue(child, out var childCaps)) return null;
                switch (walker.Class(walk.Queue[child])?.Name)
                {
                    case "RebarShapeConstraintEdgeLength":
                        // both reference types 1: the dimension runs between outer surfaces
                        if (IntegerOf(childCaps, "RebarShapeConstraintEdgeLength.m_refType@0") != 1 || IntegerOf(childCaps, "RebarShapeConstraintEdgeLength.m_refType@1") != 1) return null;
                        parameter = IntegerOf(childCaps, "RebarShapeConstraintEdgeLength.m_paramId");
                        break;
                    case "RebarShapeConstraintFixedEdgeDir":
                        if (RealOf(childCaps, "XYZ.m_xyz@0") is double x && RealOf(childCaps, "XYZ.m_xyz@1") is double y && RealOf(childCaps, "XYZ.m_xyz@2") is double z)
                            direction = [x, y, z];
                        break;
                    case "RebarShapeConstraintProjEdgeLength":
                        // the length of the projection of the edge on a direction, between outer surfaces too
                        if (IntegerOf(childCaps, "RebarShapeConstraintProjEdgeLength.m_refType@0") != 1 || IntegerOf(childCaps, "RebarShapeConstraintProjEdgeLength.m_refType@1") != 1
                            || IntegerOf(childCaps, "RebarShapeConstraintProjEdgeLength.m_paramId") is not long projected
                            || RealOf(childCaps, "XYZ.m_xyz@0") is not double px || RealOf(childCaps, "XYZ.m_xyz@1") is not double py || RealOf(childCaps, "XYZ.m_xyz@2") is not double pz
                            || !double.IsFinite(px) || !double.IsFinite(py) || pz != 0 || Math.Abs(px * px + py * py - 1) > 1e-9) return null;
                        edgeProjections.Add((projected, [px, py, pz]));
                        break;
                }
            }
            offset += constraintCounts[edge];
            if (constraintCounts[edge] == 3)
            {
                if (parameter == null || direction != null || edgeProjections.Count != 2) return null;
                projections = edgeProjections;
                direction = [0, 0, 0];
            }
            if (parameter == null || direction == null) return null;
            parameters.Add(parameter.Value);
            directions.Add(direction);
        }
        return new ShapeDefinition(directions, parameters, turns, (int)style, [ori0, ori1], projections.Count == 0 ? null : projections);
    }

    /// <summary>The RebarInstance of a Rebar that has no cache: its shape, hooks, parameter values and bar positions.</summary>
    private static PendingBar? ReadPending(WideBlockWalker walker, WideBlockWalker.Walk walk, Dictionary<int, List<WideBlockWalker.Capture>> byQueue)
    {
        int instance = walk.Queue.FindIndex(c => walker.Class(c)?.Name == "RebarInstance");
        return instance < 0 || !byQueue.TryGetValue(instance, out var caps) ? null : ReadPending(caps, InAssembly(walker, walk, byQueue));
    }

    /// <summary>The bars of a RebarSystem of Revit 2020 and 2021, whose layers hold no curves: the RebarSystemGStep keeps one held instance per
    /// RebarInSystem, a HeldRebarInstanceKey (m_index, m_layer) followed by the RebarInstBase of its bars. The result is by (layer, index).</summary>
    internal static Dictionary<(int Layer, int Index), PendingBar> ReadHeld(IEnumerable<WideBlockWalker.Capture> captures)
    {
        var groups = new List<(int Layer, int Index, int Queue, List<WideBlockWalker.Capture> Caps)>();
        long? index = null;
        foreach (var c in captures)
        {
            if (c.Key == "HeldRebarInstanceKey.m_index") index = c.Integer;
            else if (c.Key == "HeldRebarInstanceKey.m_layer" && index is long i) { groups.Add(((int)c.Integer, (int)i, c.QueueIndex, [])); index = null; }
            else if (groups.Count > 0 && c.QueueIndex == groups[^1].Queue) groups[^1].Caps.Add(c);
        }
        var result = new Dictionary<(int, int), PendingBar>();
        foreach (var (layer, held, _, caps) in groups)
            if (ReadPending(caps, false) is { } bar) result.TryAdd((layer, held), bar with { Held = true });
        return result;
    }

    /// <param name="containerItem">The item of a RebarContainer: one bar at the position of the shape frame (the position arrays hold nothing).</param>
    private static PendingBar? ReadPending(List<WideBlockWalker.Capture> caps, bool inAssembly, bool containerItem = false)
    {
        if (IntegerOf(caps, "RebarInstBase.m_shapeId") is not long shape || IntegerOf(caps, "RebarInstBase.m_barTypeId") is not long barType
            || IntegerOf(caps, "RebarInstBase.m_hookTypeId0") is not long hook0 || IntegerOf(caps, "RebarInstBase.m_hookTypeId1") is not long hook1
            || IntegerOf(caps, "RebarInstBase.m_hookOrient0") is not long orient0 || IntegerOf(caps, "RebarInstBase.m_hookOrient1") is not long orient1
            || RealOf(caps, "RebarInstBase.m_barLength") is not double barLength || IntegerOf(caps, "RebarInstBase.m_numberOfBarPositions") is not long count) return null;
        // no end treatment, coupler or orientation angle: the plain shape only
        // (Revit 2020 has no end treatment and no orientation angle fields)
        if ((IntegerOf(caps, "RebarInstBase.m_endTreatmentTypeId0") ?? -1) != -1 || (IntegerOf(caps, "RebarInstBase.m_endTreatmentTypeId1") ?? -1) != -1
            || IntegerOf(caps, "RebarInstBase.m_couplerId@0") != -1 || IntegerOf(caps, "RebarInstBase.m_couplerId@1") != -1
            || (RealOf(caps, "RebarInstBase.m_orientationAngleAtStart") ?? 0) != 0 || (RealOf(caps, "RebarInstBase.m_orientationAngleAtEnd") ?? 0) != 0) return null;
        var ids = caps.Where(c => c.Key == "std::pair< ElementId, ParamVectorIterator >.first").Select(c => c.Integer).ToList();
        var indexes = caps.Where(c => c.Key == "ParamVectorIterator.m_index").Select(c => (int)c.Integer).ToList();
        var doubles = caps.Where(c => c.Key.StartsWith(WideBlockWalker.AllItemsKeyPrefix + "ParamVectorValues.m_doubles@", StringComparison.Ordinal) && c.Kind == WideBlockWalker.CaptureKind.Real)
            .Select(c => c.Real).ToList();
        if (ids.Count != indexes.Count || ids.Count == 0 || ids.Distinct().Count() != ids.Count || indexes.Any(i => i < 0 || i >= doubles.Count)) return null;
        var trfs = Transforms(caps);
        if (!trfs.TryGetValue(Trailing, out var frame) || frame.Count != 1) return null;
        // a count from the file sizes the lists below
        if (count is < 1 or > MaxBarPositions) return null;
        List<double[]> positions;
        List<long> included;
        if (containerItem)
        {
            if (count != 1 || trfs.GetValueOrDefault(Positions) is not { Count: 0 }) return null;
            positions = [[1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0]];
            included = [1];
        }
        else if (trfs.TryGetValue(Positions, out var stored))
        {
            positions = stored;
            included = IncludedFlags(caps);
        }
        else
        {
            // Revit 2020 stores no positions: bar k lies k steps along the normal of the shape plane (third column of the frame); the step is
            // the stored spacing signed by the set length for a fixed number with spacing (layout 3), else the set length over the gaps
            if (RealOf(caps, "RebarInstBase.m_endPar") is not double setLength || RealOf(caps, "RebarInstBase.m_maxSpacing") is not double spacing
                || IntegerOf(caps, "RebarInstBase.m_layoutRule") is not long layout || IntegerOf(caps, "RebarInstBase.m_includeFirstBar") is not long first
                || IntegerOf(caps, "RebarInstBase.m_includeLastBar") is not long last || count < 1 || layout is < 0 or > 3) return null;
            double step = count == 1 ? 0 : layout == 3 ? Math.CopySign(spacing, setLength) : setLength / (count - 1);
            double[] normal = [frame[0][2], frame[0][5], frame[0][8]];
            positions = Enumerable.Range(0, (int)count).Select(k => new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, k * step * normal[0], k * step * normal[1], k * step * normal[2] }).ToList();
            included = Enumerable.Repeat(1L, (int)count).ToList();
            if (count > 1) { included[0] = first; included[^1] = last; }
        }
        if (positions.Count != count || included.Count != 0 && included.Count != count) return null;
        return new PendingBar((int)barType, (int)shape, (int)hook0, (int)hook1, orient0, orient1, barLength,
            ids.Zip(indexes).ToDictionary(pair => pair.First, pair => pair.Second), doubles, positions, included, frame[0], inAssembly);
    }

    private const long MaxBarPositions = 100_000;

    /// <summary>A hook of a shape driven bar; null when the bar has none at that end, <paramref name="unsupported"/> set when it has one
    /// that is not supported (an orientation of the instance, a hook length setting that is not a plain tangent length).</summary>
    private static RebarShapeSolver.Hook? HookOf(int hookId, long instanceOrientation, int end, ShapeDefinition shape,
        Dictionary<int, HookKind> hookKinds, BarKind barKind, double diameter, ref bool unsupported)
    {
        if (hookId < 0) return null;
        if (!hookKinds.TryGetValue(hookId, out var kind) || instanceOrientation is not (0 or -1)) { unsupported = true; return null; }
        // the shape orients the hooks seen from the end of the bar towards its inside (1 left, 3 right); along the bar the end hook is mirrored
        int side = (shape.HookOrientation[end], end) switch { (1, 0) => 1, (3, 0) => -1, (1, 1) => -1, (3, 1) => 1, _ => 0 };
        double? tangent = null;
        if (barKind.HookLengths.TryGetValue(hookId, out var setting) && (setting.Hook > 0 || setting.Offset > 0 || setting.Tangent > 0))
        {
            if (setting.Hook != setting.Tangent || setting.Offset >= 0) unsupported = true;
            tangent = setting.Tangent;
        }
        if (side == 0) { unsupported = true; return null; }
        double radius = ((kind.Style == 1 ? barKind.StirrupTieBend : barKind.StandardHookBend) + diameter) / 2;
        return new RebarShapeSolver.Hook(kind.Angle, radius, side, tangent, kind.Multiplier);
    }

    /// <summary>The bars of a shape driven bar with no cache, from <see cref="RebarShapeSolver"/> (null when the shape is outside what it
    /// covers). The bar positions move the first bar twice, as for the cached bars; the positions of a held instance are
    /// the bar steps themselves and move it once.</summary>
    internal static RebarBars? SolveShapeDriven(PendingBar bar, ShapeDefinition shape, Dictionary<int, HookKind> hookKinds, BarKind barKind, double diameter)
    {
        var lengths = new List<double>();
        foreach (long parameter in shape.ParameterIds)
        {
            if (!bar.ParameterIndex.TryGetValue(parameter, out int index)) return null;
            lengths.Add(bar.Doubles[index]);
        }
        // the middle edge of a joggle given by projections: the vector between its outer corners is the sum of the projections
        double[]? joggle = null;
        if (shape.Projections != null)
        {
            joggle = [0, 0, 0];
            foreach (var (parameter, direction) in shape.Projections)
            {
                if (!bar.ParameterIndex.TryGetValue(parameter, out int index)) return null;
                for (int k = 0; k < 3; k++) joggle[k] += bar.Doubles[index] * direction[k];
            }
            // its length parameter must say the same
            if (Math.Abs(Math.Sqrt(joggle[0] * joggle[0] + joggle[1] * joggle[1]) - lengths[1]) > 1e-6) return null;
        }
        bool unsupported = false;
        var start = HookOf(bar.HookId0, bar.HookOrientation0, 0, shape, hookKinds, barKind, diameter, ref unsupported);
        var end = HookOf(bar.HookId1, bar.HookOrientation1, 1, shape, hookKinds, barKind, diameter, ref unsupported);
        if (unsupported) return null;
        double corner = ((shape.Style == 1 ? barKind.StirrupTieBend : barKind.StandardBend) + diameter) / 2;
        var pieces = joggle != null
            ? RebarShapeSolver.SolveJoggle(shape.Directions[0], shape.Directions[2], joggle, [lengths[0], lengths[2]], shape.Turns, diameter, corner, start == null && end == null)
            : RebarShapeSolver.Solve(shape.Directions, lengths, shape.Turns, diameter, corner, start, end);
        if (pieces == null) return null;
        var first = pieces.Select(piece => piece.Moved(bar.Frame)).ToList();
        var bars = new List<IReadOnlyList<RebarSegment>>();
        List<IReadOnlyList<RebarSegment>>? modelPaths = bar.Held ? null : [];
        for (int i = 0; i < bar.Positions.Count; i++)
        {
            if (bar.Included.Count != 0 && bar.Included[i] == 0) continue;
            var placed = first.Select(s => s.Moved(bar.Positions[i])).ToList();
            modelPaths?.Add(placed);
            bars.Add(bar.Held ? placed : placed.Select(s => s.Moved(bar.Positions[i])).ToList());
        }
        return new RebarBars(bar.BarTypeId, 0, bar.BarLength, bars, ShapeName: bar.ShapeId.ToString(CultureInfo.InvariantCulture),
            InAssembly: bar.InAssembly, ShapeId: bar.ShapeId, ModelPaths: modelPaths);
    }
}
