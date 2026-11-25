using RvtIO.Internal;
using RvtIO.Model;

namespace RvtIO.Parsing;

internal static partial class ElementBuilder
{
    internal static void ApplyOwnFamilyCutoutInputs(IReadOnlyList<Element> elements, ParsingState state,
        IReadOnlyDictionary<int, Element>? unique = null)
    {
        var counts = unique == null
            ? elements.GroupBy(element => element.Id).ToDictionary(group => group.Key, group => group.Count())
            : null;
        bool IsUnique(int id) => unique?.ContainsKey(id) ?? counts!.GetValueOrDefault(id) == 1;
        foreach (var element in elements)
        {
            element.OwnSingleFamilyCutout = null;
            element.OwnWallCutoutInstanceIds = null;
            element.OwnWallCutoutSteps = null;
            element.OwnWallAxisLine = null;
            element.OwnWallTrimmedAxis = null;
            element.OwnWallCrossSection = null;
            element.OwnWallHelper = null;
            if (element.Id <= 0 || !IsUnique(element.Id)
                || !state.ComputedSymbolTargetClasses.TryGetValue((uint)element.Id, out var root)
                || root.BlockCount != 1) continue;
            if (root.ClassId is 3524 or 361
                && state.OwnWallHelpers.TryGetValue((uint)element.Id, out var helper) && helper.BlockCount == 1)
                element.OwnWallHelper = helper.Input;
            if (root.ClassId is 3524 or 361
                && state.OwnWallCutoutSteps.TryGetValue((uint)element.Id, out var wall)
                && wall is { BlockCount: 1, Steps: not null })
            {
                element.OwnWallCutoutInstanceIds = wall.Steps.Select(step => step.InstanceId).ToArray();
                element.OwnWallCutoutSteps = wall.Steps;
                element.OwnWallCrossSection = wall.CrossSection;
                if (wall.Axis is { OriginPoint.Length: 3, DirectionVector.Length: 3, EndParams.Length: 2 } axis)
                    element.OwnWallAxisLine = axis.EndParams.Select(parameter => Enumerable.Range(0, 3)
                        .Select(index => axis.OriginPoint[index] + parameter * axis.DirectionVector[index]).ToArray()).ToArray();
                if (wall.Axis is { } trimmedAxis && state.OwnWallTrimmedRanges.TryGetValue((uint)element.Id, out var range))
                    element.OwnWallTrimmedAxis = new[] { range.Lo, range.Hi }.Select(parameter => Enumerable.Range(0, 3)
                        .Select(index => trimmedAxis.OriginPoint[index] + parameter * trimmedAxis.DirectionVector[index]).ToArray()).ToArray();
            }
            if (root.ClassId != 1794 || element.ValidatedOriginalFamilySymbolId is not int symbolId
                || !state.ComputedFamilySymbols.TryGetValue((uint)element.Id, out var instance)
                || instance is not { BlockCount: 1, Symbol: { SubInstanceCount: 0, Transform.Length: 12,
                    HostId: > 0, InsertOrientation: 0 } source }
                || source.HostId != element.SerializedHostId
                || source.SymbolId != symbolId || !IsUniqueRoot(symbolId, 1829)
                || !IsUnique(symbolId)
                || !state.OwnFamilyCutSymbols.TryGetValue((uint)symbolId, out var computed)
                || computed is not { BlockCount: 1, Symbol: { Active: true }, Loops.Count: 1 }
                || !state.FamilyInstanceSymbolIds.TryGetValue((uint)element.Id, out var type)
                || type is not { BlockCount: 1, TypeId: > 0 } || !IsUniqueRoot(type.TypeId.Value, 1829)
                || !state.OwnFamilyCutSymbols.TryGetValue((uint)type.TypeId.Value, out var master)
                || master is not { BlockCount: 1, Symbol: not null }
                || !IsUniqueRoot(master.Symbol.FamilyId, 1772)
                || !state.OwnFamilyInPlace.TryGetValue((uint)master.Symbol.FamilyId, out var family)
                || family is not { BlockCount: 1, InPlace: false }) continue;
            element.OwnSingleFamilyCutout = (symbolId, source.HostId.Value, source.Transform, computed.Loops[0]);
        }

        bool IsUniqueRoot(int id, ushort cls) => id > 0
            && state.ComputedSymbolTargetClasses.TryGetValue((uint)id, out var root)
            && root.BlockCount == 1 && root.ClassId == cls;
    }

    /// <summary>Trimmed axis of a straight wall without a face-history trim: its location line bounded by the extreme parameters
    /// (along the curve driver) of the vertices of the vertical, non-parallel faces of its first solid.</summary>
    internal static void ApplySolidTrimmedAxis(IReadOnlyList<Element> elements)
    {
        foreach (var element in elements)
        {
            if (element.OwnWallTrimmedAxis != null || element.CategoryId != -2000011
                || element.Location is not { CurveType: "Line", DriverOrigin: { Length: 3 } o, DriverDirection: { Length: 3 } d,
                    DriverEndParams: { Length: 2 } endParams }
                || element.Geometry?.Faces is not { Count: > 0 } faces) continue;
            var solidFaces = element.Geometry.BrepSolids is { Count: > 0 } solids
                ? solids[0].FaceIndices.Where(i => (uint)i < (uint)faces.Count).Select(i => faces[i]) : faces;
            if (SolidTrimmedRange(solidFaces, o, d, endParams) is not var (lo, hi)) continue;
            element.OwnWallTrimmedAxis = new[] { lo, hi }.Select(parameter => Enumerable.Range(0, 3)
                .Select(index => o[index] + parameter * d[index]).ToArray()).ToArray();
        }
    }

    /// <summary>The tolerance (feet) within which two vertices of a solid count as one, the vertex tolerance of the meshers.</summary>
    private const double SolidVertexToleranceFt = 0.0005233832795;

    /// <summary>The parameter range of the end-face vertices on the curve driver (origin, direction, end parameters); a maximum that is not
    /// above 1e-10 (the whole wall at negative parameters) is replaced by the upper end parameter. The range is only taken when both
    /// ends of the solid are among the end faces: there are at least two of them and their vertices reach the lowest and the highest
    /// parameter of any vertex of the solid, else an end face is missing and the vertices of the one that is left are not the range of
    /// the wall. Null then, without a usable end face and for an empty range.</summary>
    internal static (double Lo, double Hi)? SolidTrimmedRange(IEnumerable<GeoFace> faces, double[] origin, double[] direction, double[] endParams)
    {
        double lengthSquared = direction.Sum(v => v * v);
        if (!(lengthSquared > 0)) return null;
        double inverseLength = 1 / Math.Sqrt(lengthSquared), tolerance = SolidVertexToleranceFt * inverseLength;
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity, solidLo = double.PositiveInfinity, solidHi = double.NegativeInfinity;
        int endFaces = 0;
        foreach (var face in faces)
        {
            if (face.OuterVertices is not { Count: > 0 } ring) continue;
            bool endFace = Math.Abs(face.Normal[2]) <= 1e-6
                && Math.Abs((face.Normal[0] * direction[0] + face.Normal[1] * direction[1] + face.Normal[2] * direction[2]) * inverseLength) > 1e-6;
            if (endFace) endFaces++;
            foreach (var p in ring)
            {
                double t = ((p[0] - origin[0]) * direction[0] + (p[1] - origin[1]) * direction[1] + (p[2] - origin[2]) * direction[2]) / lengthSquared;
                if (!double.IsFinite(t)) return null;
                solidLo = Math.Min(solidLo, t);
                solidHi = Math.Max(solidHi, t);
                if (!endFace) continue;
                lo = Math.Min(lo, t);
                hi = Math.Max(hi, t);
            }
        }
        if (endFaces < 2 || lo > solidLo + tolerance || hi < solidHi - tolerance) return null;
        if (hi <= 1e-10) hi = endParams[1];
        return hi > lo ? (lo, hi) : null;
    }
}
