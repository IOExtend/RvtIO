using RvtIO.Compact;
using RvtIO.Model;

namespace RvtIO.Ifc;

/// <summary>
/// The current profile: the room of a family instance. One test point of the instance is looked up in the rooms of the
/// instance's created phase; the first room whose vertical extent and solid hold it wins (a point on the boundary is inside).
/// The test point, where the symbol is the one the instance's placement names (the type symbol, or the private copy of it
/// that a modified instance stands on):
/// - an instance of a family with a room calculation point: that point of the symbol, placed with the instance
///   (<see cref="CalculationPointCurrent"/>);
/// - else an instance of a hosted family in a wall: the placed origin of the symbol, projected on a side face of the wall and moved
///   one foot along that face's normal (<see cref="WallSideTestPointCurrent"/>);
/// - else a work-plane-based instance: the placed origin of the symbol, one foot along the instance's Z;
/// - else an instance of a hosted family on a floor or a ceiling: the placed origin of the symbol, one foot against the
///   instance's Z, along it for an instance flipped in Y (<see cref="FloorOrCeilingHostTestPointCurrent"/>);
/// - else the centre of the box stored with the instance's own geometry, or without one the placed centre of the symbol's
///   stored box, moved by at most half a foot towards the elevation of the instance's associated level.
/// A room stored with the instance is kept; this lookup only decides the instances without one.
/// </summary>
public partial class IfcExporter
{
    /// <summary>Categories whose instances ask for a room.</summary>
    internal static readonly HashSet<int> RoomRelationCategoriesCurrent =
    [
        -2001000, -2000100, -2000023, -2001040, -2001060, -2000080, -2001100, -2000151, -2001120, -2001140, -2001180,
        -2001160, -2001260, -2001350, -2001330, -2001300, -2000014, -2001360, -2008010, -2008013, -2008016, -2008055,
        -2008049, -2008075, -2008077, -2008079, -2008081, -2008083, -2008085, -2008087, -2008099,
    ];

    /// <summary>The classes that are room related (generic family instances only).</summary>
    internal static bool IsRoomRelatedClassCurrent(string ifcType) =>
        ifcType.StartsWith("IFCFLOW", StringComparison.Ordinal) || ifcType.StartsWith("IFCDISTRIBUTION", StringComparison.Ordinal)
        || ifcType is "IFCENERGYCONVERSIONDEVICE" or "IFCELECTRICDISTRIBUTIONPOINT" or "IFCELECTRICDISTRIBUTIONBOARD"
            or "IFCOUTLET" or "IFCSWITCHINGDEVICE" or "IFCFURNISHINGELEMENT" or "IFCFURNITURE"
            or "IFCSYSTEMFURNITUREELEMENT" or "IFCBUILDINGELEMENTPROXY";

    /// <summary>Whether the instance is asked for a room at all: a plain family instance that is exported as a room-related
    /// class and whose family is of a listed category. The category of the instance stands in where its family is not
    /// decoded.</summary>
    internal static bool IsRoomInstanceCurrent(Element instance, string ifcType, IReadOnlyDictionary<int, Element>? elementsById = null)
    {
        if (!IsRoomRelatedClassCurrent(ifcType) || instance.RuleClassName is not (null or "FamilyInstance")) return false;
        int? category = (elementsById == null ? null : CompactFamilyInstanceFacts.SymbolCategory(instance, id => elementsById.GetValueOrDefault(id)))
            ?? instance.CategoryId;
        return category is int listed && RoomRelationCategoriesCurrent.Contains(listed);
    }

    private const double RoomPointTolerance = 1e-6;
    private const double LevelPullFeet = 0.5;

    private const double WallSideOffsetFeet = 1.0;

    private const int CeilingsCategoryId = -2000038;

    private const string FlippedYField = "1794:m_flippedY", WorkPlaneBasedField = "1794:m_workPlaneBased", AssociatedLevelField = "37:m_assocLevelId";

    private const string CalculationPointField = "3639:m_position", HasCalculationPointField = "1773:m_hasRoomCalculationPoint",
        SymbolFamilyField = "1829:m_familyId";

    /// <summary>The symbol the instance's placement names; null when the placement or the symbol is not decoded.</summary>
    private static Element? PlacedSymbolCurrent(Element instance, IReadOnlyDictionary<int, Element> elementsById) =>
        instance.Transform is { SymbolId: > 0 } placement ? elementsById.GetValueOrDefault(placement.SymbolId) : null;

    /// <summary>A point of the symbol in project coordinates; null when the instance has no placement.</summary>
    private static double[]? PlacedCurrent(Element instance, double x, double y, double z) =>
        instance.Transform is { Origin: { Length: 3 } o, BasisX: { Length: 3 } bx, BasisY: { Length: 3 } by, BasisZ: { Length: 3 } bz }
            ? [o[0] + bx[0] * x + by[0] * y + bz[0] * z, o[1] + bx[1] * x + by[1] * y + bz[1] * z, o[2] + bx[2] * x + by[2] * y + bz[2] * z]
            : null;

    /// <summary>The stored origin of the instance's symbol in project coordinates; the zero point of the
    /// placement when the symbol or its origin is not decoded; null when the instance has no placement.</summary>
    internal static double[]? PlacedSymbolOriginCurrent(Element instance, IReadOnlyDictionary<int, Element> elementsById)
    {
        var fields = PlacedSymbolCurrent(instance, elementsById)?.NativeRoomFields;
        return PlacedCurrent(instance, fields?.GetValueOrDefault("1829:m_origin@0") ?? 0, fields?.GetValueOrDefault("1829:m_origin@1") ?? 0,
            fields?.GetValueOrDefault("1829:m_origin@2") ?? 0);
    }

    /// <summary>The room calculation point of an instance in project coordinates: the point its symbol stores, placed with the
    /// instance. Revit reports the room at this point for the instances of a family that has the point turned on, of level,
    /// wall, floor, ceiling and face based families alike. Null when the family of the symbol does not turn the point on,
    /// the symbol, its family or the point is not decoded, or the instance has no placement.</summary>
    internal static double[]? CalculationPointCurrent(Element instance, IReadOnlyDictionary<int, Element> elementsById)
    {
        if (PlacedSymbolCurrent(instance, elementsById) is not { NativeRoomFields: { } point } symbol
            || !point.TryGetValue(CalculationPointField + "@0", out double x) || !point.TryGetValue(CalculationPointField + "@1", out double y)
            || !point.TryGetValue(CalculationPointField + "@2", out double z)
            || symbol.NativeListFields is not { } symbolFields || !symbolFields.TryGetValue(SymbolFamilyField, out int familyId)
            || elementsById.GetValueOrDefault(familyId)?.NativeRoomFields?.GetValueOrDefault(HasCalculationPointField) != 1)
            return null;
        return PlacedCurrent(instance, x, y, z);
    }

    /// <summary>The test point of an instance of a hosted family in a wall: the placed origin of its symbol projected on a side
    /// face of the wall, and one foot along the normal of that face. The two side faces are the largest upright plane faces
    /// of the two opposite directions; they carry consecutive face handles, and the face taken is the one with the higher
    /// handle, the lower one for an instance flipped in Y. Null when the family is free of a host, the host is no wall, or
    /// the wall shows no such pair of faces.</summary>
    internal static double[]? WallSideTestPointCurrent(Element instance, IReadOnlyDictionary<int, Element> elementsById)
    {
        // A family that takes no host hides the host stored with its instance.
        if (CompactFamilyInstanceFacts.Get("fi:isFreeFamilySymbol", instance, id => elementsById.GetValueOrDefault(id)) == 1) return null;
        if (instance.SerializedHostId is not int hostId || hostId <= 0 || !elementsById.TryGetValue(hostId, out var host)
            || host.CategoryId != WallsCategoryId || host.Geometry?.Faces is not { Count: > 0 } faces
            || PlacedSymbolOriginCurrent(instance, elementsById) is not { } origin)
            return null;

        (double[] Normal, double[] Point, double Area, uint Handle, double[] Surface)? first = null, second = null;
        var sides = new List<(double[] Normal, double[] Point, double Area, uint Handle, double[] Surface)>();
        foreach (var face in faces)
        {
            if (!face.PlaneSurface || face.OuterVertices is not { Count: >= 3 } ring) continue;
            // Newell normal of the outline
            double nx = 0, ny = 0, nz = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                double[] a = ring[i], b = ring[(i + 1) % ring.Count];
                nx += (a[1] - b[1]) * (a[2] + b[2]);
                ny += (a[2] - b[2]) * (a[0] + b[0]);
                nz += (a[0] - b[0]) * (a[1] + b[1]);
            }
            double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (!(length > 1e-12) || Math.Abs(nz / length) > 0.01) continue; // upright faces only
            double[] normal = [nx / length, ny / length, nz / length];
            // the normal of the face's own plane where it is stored, else the normal of the outline
            double[] surface = normal;
            if (face.Normal is { Length: 3 } stored)
            {
                double storedLength = Math.Sqrt(stored[0] * stored[0] + stored[1] * stored[1] + stored[2] * stored[2]);
                if (storedLength > 1e-10 && double.IsFinite(storedLength))
                    surface = [stored[0] / storedLength, stored[1] / storedLength, stored[2] / storedLength];
            }
            sides.Add((normal, ring[0], length / 2, face.SourceFace, surface));
        }
        if (sides.Count == 0) return null;
        var largest = sides.MaxBy(side => side.Area);
        static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        foreach (var side in sides)
        {
            double along = Dot(side.Normal, largest.Normal);
            if (along > 0.9 && (first == null || side.Area > first.Value.Area)) first = side;
            else if (along < -0.9 && (second == null || side.Area > second.Value.Area)) second = side;
        }
        if (first is not { } one || second is not { } other) return null;
        uint low = Math.Min(one.Handle, other.Handle), high = Math.Max(one.Handle, other.Handle);
        if (high != low + 1) return null; // not the pair of side faces of one wall body
        bool flippedY = instance.NativeRoomFields?.GetValueOrDefault(FlippedYField) == 1;
        var chosen = (one.Handle == high) != flippedY ? one : other;

        double distance = Dot(chosen.Normal, [chosen.Point[0] - origin[0], chosen.Point[1] - origin[1], chosen.Point[2] - origin[2]]);
        return
        [
            origin[0] + chosen.Normal[0] * distance + chosen.Surface[0] * WallSideOffsetFeet,
            origin[1] + chosen.Normal[1] * distance + chosen.Surface[1] * WallSideOffsetFeet,
            origin[2] + chosen.Normal[2] * distance + chosen.Surface[2] * WallSideOffsetFeet,
        ];
    }

    /// <summary>The test point of an instance of a hosted family on a floor or a ceiling: the placed origin of its symbol, one
    /// foot against the instance's Z, and one foot along it for an instance flipped in Y. Revit reports the room at this
    /// point for such instances: under the top of the floor an upright instance stands on (the centre of its box gives the
    /// room over the floor instead), under the ceiling an upright instance hangs from, and over that ceiling, where no room
    /// is, when the instance is flipped. Null when the family is free of a host or not decoded, the host is neither a floor
    /// nor a ceiling, the instance has no placement, or its Z has no direction.</summary>
    internal static double[]? FloorOrCeilingHostTestPointCurrent(Element instance, IReadOnlyDictionary<int, Element> elementsById)
    {
        // Only a family known to take a host: a family that takes none stores the floor it stands on as well.
        if (CompactFamilyInstanceFacts.Get("fi:isFreeFamilySymbol", instance, id => elementsById.GetValueOrDefault(id)) != 0) return null;
        // shortcut: only hosts of the floors and ceilings categories and the flip in Y were compared with Revit; extend when
        // another host kind or a flip in X alone is measured
        if (instance.SerializedHostId is not int hostId || hostId <= 0 || !elementsById.TryGetValue(hostId, out var host)
            || host.CategoryId is not (FloorsCategoryId or CeilingsCategoryId) || instance.Transform?.BasisZ is not { Length: 3 } axis
            || PlacedSymbolOriginCurrent(instance, elementsById) is not { } origin)
            return null;
        double length = Math.Sqrt(axis[0] * axis[0] + axis[1] * axis[1] + axis[2] * axis[2]);
        if (!(length > 1e-10) || !double.IsFinite(length)) return null;
        double step = (instance.NativeRoomFields?.GetValueOrDefault(FlippedYField) == 1 ? 1 : -1) / length;
        return [origin[0] + axis[0] * step, origin[1] + axis[1] * step, origin[2] + axis[2] * step];
    }

    /// <summary>The test point (feet) of an instance: the room calculation point of its family
    /// (<see cref="CalculationPointCurrent"/>); else beside its host wall (<see cref="WallSideTestPointCurrent"/>); else, for a
    /// work-plane-based instance, the placed origin of its symbol and one foot along its Z; else, for an instance of a hosted
    /// family on a floor or a ceiling, one foot against its Z or along it (<see cref="FloorOrCeilingHostTestPointCurrent"/>);
    /// else the centre of the box of its own geometry, moved by at most half a foot towards the elevation of its associated
    /// level. An instance without such a box takes the placed centre of the stored box of its symbol, and without a decoded
    /// placement or symbol box the centre of the box it carries itself (<see cref="Element.DecodedBox"/>). Null without any
    /// box.</summary>
    internal static double[]? RoomTestPointCurrent(Element instance, IReadOnlyDictionary<int, double> levelElevations,
        IReadOnlyDictionary<int, Element>? elementsById = null)
    {
        if (elementsById != null)
        {
            if (CalculationPointCurrent(instance, elementsById) is { } calculationPoint) return calculationPoint;
            if (WallSideTestPointCurrent(instance, elementsById) is { } wallPoint) return wallPoint;
            if (instance.NativeListFields?.GetValueOrDefault(WorkPlaneBasedField) == 1
                && PlacedSymbolOriginCurrent(instance, elementsById) is { } origin && instance.Transform?.BasisZ is { Length: 3 } axis)
            {
                double length = Math.Sqrt(axis[0] * axis[0] + axis[1] * axis[1] + axis[2] * axis[2]);
                return length > 1e-10 && double.IsFinite(length)
                    ? [origin[0] + axis[0] / length, origin[1] + axis[1] / length, origin[2] + axis[2] / length]
                    : origin;
            }
            if (FloorOrCeilingHostTestPointCurrent(instance, elementsById) is { } hostPoint) return hostPoint;
        }
        double[]? center = null;
        // The box of the geometry comes first: the outline stored with a symbol can be wider than what the symbol holds, and
        // Revit then reports the room at the centre of the geometry.
        if (instance.Geometry?.BoundingBox is { Length: >= 6 } own)
            center = [(own[0] + own[3]) / 2, (own[1] + own[4]) / 2, (own[2] + own[5]) / 2];
        else if (elementsById != null && PlacedSymbolCurrent(instance, elementsById) is { BoundingBoxIsHeaderOutline: true, BoundingBox: { Length: >= 6 } symbolBox })
            center = PlacedCurrent(instance, (symbolBox[0] + symbolBox[3]) / 2, (symbolBox[1] + symbolBox[4]) / 2, (symbolBox[2] + symbolBox[5]) / 2);
        if (center == null)
        {
            if (instance.DecodedBox is not { Length: >= 6 } box) return null;
            center = [(box[0] + box[3]) / 2, (box[1] + box[4]) / 2, (box[2] + box[5]) / 2];
        }
        // The associated level as stored with the instance; an instance whose record is not decoded takes it as the
        // storey resolution does.
        int? levelId = null;
        if (instance.NativeListFields is { } stored && stored.TryGetValue(AssociatedLevelField, out int associated)) levelId = associated;
        else
            foreach (int parameterId in new[] { -1001952, -1002062, -1114000, -1001350, -1001365, -1001383, -1002063 })
                if (instance.Parameters.FirstOrDefault(parameter => parameter.Id == parameterId)?.Value is int parameterLevel
                    && levelElevations.ContainsKey(parameterLevel))
                {
                    levelId = parameterLevel;
                    break;
                }
        if (levelId is int level && levelElevations.TryGetValue(level, out double elevation))
        {
            double delta = elevation - center[2];
            if (Math.Abs(delta) > 1e-9) center[2] += Math.Sign(delta) * Math.Min(LevelPullFeet, Math.Abs(delta));
        }
        return center;
    }

    /// <summary>The room each room-related family instance is in (0 when none of the rooms with a decoded solid holds its
    /// test point): element id -> room element id, for the instances whose test point can be decided.</summary>
    internal static Dictionary<int, int> ResolveInstanceRoomsCurrent(IReadOnlyCollection<Element> allElements,
        IReadOnlyDictionary<int, Element> elementsById, IEnumerable<KeyValuePair<int, string>> instanceClasses,
        IReadOnlyDictionary<int, double> levelElevations)
    {
        var volumesByPhase = new Dictionary<int, List<RoomVolumeCurrent>>();
        foreach (var room in allElements.Where(element => element.CategoryId == RoomCategoryId && element.SerializedRoomIsLocationless != true)
                     .OrderBy(element => element.Id))
        {
            // The phase of the room: the validated one, else the phase id as the room's record stores it.
            int roomPhase;
            if (room.SerializedRoomPhaseId is int validated) roomPhase = validated;
            else if (room.NativeRoomFields is { } fields && fields.TryGetValue("3471:m_phaseId", out double stored)) roomPhase = (int)stored;
            else continue;
            if (roomPhase < 0 || RoomVolumeCurrent.Create(room, elementsById, levelElevations) is not { } volume) continue;
            if (!volumesByPhase.TryGetValue(roomPhase, out var list)) volumesByPhase[roomPhase] = list = new List<RoomVolumeCurrent>();
            list.Add(volume);
        }
        var result = new Dictionary<int, int>();
        foreach (var (elementId, ifcType) in volumesByPhase.Count == 0 ? Enumerable.Empty<KeyValuePair<int, string>>() : instanceClasses)
        {
            if (!elementsById.TryGetValue(elementId, out var instance) || !IsRoomInstanceCurrent(instance, ifcType, elementsById)
                || instance.SerializedCreatedPhaseId is not int phase
                || RoomTestPointCurrent(instance, levelElevations, elementsById) is not { } point)
                continue;
            int found = 0;
            if (volumesByPhase.TryGetValue(phase, out var volumes))
                foreach (var volume in volumes)
                    if (volume.HoldsElevation(point[2]) && volume.Contains(point[0], point[1], point[2], RoomPointTolerance))
                    {
                        found = volume.RoomId;
                        break;
                    }
            result[elementId] = found;
        }
        return result;
    }

    /// <summary>A room solid: a point inside or on the boundary belongs
    /// to the room. Lengths are feet in project coordinates.</summary>
    internal sealed class RoomVolumeCurrent
    {
        private sealed record Face(List<(double X, double Y, double Z)> Outer, List<List<(double X, double Y, double Z)>> Holes,
            double Nx, double Ny, double Nz, double D, double MinX, double MaxX, double MinY, double MaxY, double MinZ, double MaxZ);

        private readonly List<Face> _faces;

        public int RoomId { get; }
        public double ZMin { get; }
        public double ZMax { get; }

        /// <summary>The vertical extent of the room by its levels and offsets (feet); unbounded where they are not decoded.</summary>
        public double Bottom { get; private init; } = double.NegativeInfinity;
        public double Top { get; private init; } = double.PositiveInfinity;

        /// <summary>Whether an elevation is within the vertical extent of the room, both ends included.</summary>
        public bool HoldsElevation(double z) => z + 1e-10 >= Bottom && Top >= z - 1e-10;
        private readonly double _minX, _minY, _maxX, _maxY;

        private RoomVolumeCurrent(int roomId, List<Face> faces)
        {
            RoomId = roomId;
            _faces = faces;
            ZMin = faces.Min(face => face.MinZ);
            ZMax = faces.Max(face => face.MaxZ);
            _minX = faces.Min(face => face.MinX);
            _maxX = faces.Max(face => face.MaxX);
            _minY = faces.Min(face => face.MinY);
            _maxY = faces.Max(face => face.MaxY);
        }

        /// <summary>The solid of a room whose boundary cache is decoded (the one its body is written from); null otherwise.
        /// The vertical extent runs from the room's level plus its lower offset up to its upper level plus the upper offset,
        /// or, without an upper level, over its height, and in both cases plus the room computation offset of that level.</summary>
        public static RoomVolumeCurrent? Create(Element room, IReadOnlyDictionary<int, Element>? elementsById = null,
            IReadOnlyDictionary<int, double>? levelElevations = null)
        {
            if ((room.SerializedSpaceBoundaryCurrent ?? room.SerializedLiteralSpaceBoundary) is not { Faces.Count: > 0 } boundary) return null;
            double bottom = double.NegativeInfinity, top = double.PositiveInfinity;
            if (elementsById != null && levelElevations != null && room.NativeRoomFields is { } fields
                && fields.TryGetValue("3471:m_levelId", out double levelId) && levelElevations.TryGetValue((int)levelId, out double elevation)
                && fields.TryGetValue("3471:m_lowerOffset", out double lowerOffset))
            {
                bottom = elevation + lowerOffset;
                static double? ComputationOffset(IReadOnlyDictionary<int, Element> elements, int level) =>
                    elements.GetValueOrDefault(level)?.NativeRoomFields is { } levelFields
                    && levelFields.TryGetValue("2299:m_roomComputationElevationOffset", out double offset) ? offset : null;
                if (fields.TryGetValue("3471:m_upperLevelId", out double upperLevelId) && upperLevelId > 0)
                {
                    if (levelElevations.TryGetValue((int)upperLevelId, out double upperElevation)
                        && fields.TryGetValue("3471:m_upperOffset", out double upperOffset)
                        && ComputationOffset(elementsById, (int)upperLevelId) is double upperComputation)
                        top = upperElevation + upperOffset + upperComputation;
                }
                else if (fields.TryGetValue("3471:m_height", out double height) && ComputationOffset(elementsById, (int)levelId) is double computation)
                    top = bottom + height + computation;
            }
            var faces = new List<Face>();
            foreach (var source in boundary.Faces)
            {
                if (source.OuterVertices is not { Count: >= 3 } outerSource) continue;
                var outer = outerSource.Select(vertex => (X: vertex[0], Y: vertex[1], Z: vertex[2])).ToList();
                // Newell normal of the outer loop.
                double nx = 0, ny = 0, nz = 0;
                for (int i = 0; i < outer.Count; i++)
                {
                    var a = outer[i]; var b = outer[(i + 1) % outer.Count];
                    nx += (a.Y - b.Y) * (a.Z + b.Z);
                    ny += (a.Z - b.Z) * (a.X + b.X);
                    nz += (a.X - b.X) * (a.Y + b.Y);
                }
                double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (length < 1e-12) continue;
                nx /= length; ny /= length; nz /= length;
                var holes = source.InnerVertices?.Select(loop => loop.Select(vertex => (X: vertex[0], Y: vertex[1], Z: vertex[2])).ToList()).ToList() ?? [];
                faces.Add(new Face(outer, holes, nx, ny, nz, nx * outer[0].X + ny * outer[0].Y + nz * outer[0].Z,
                    outer.Min(v => v.X), outer.Max(v => v.X), outer.Min(v => v.Y), outer.Max(v => v.Y),
                    outer.Min(v => v.Z), outer.Max(v => v.Z)));
            }
            return faces.Count == 0 ? null : new RoomVolumeCurrent(room.Id, faces) { Bottom = bottom, Top = top };
        }

        /// <summary>Whether the point is inside the solid or within <paramref name="tolerance"/> of its boundary.</summary>
        public bool Contains(double x, double y, double z, double tolerance)
        {
            if (z < ZMin - tolerance || z > ZMax + tolerance
                || x < _minX - tolerance || x > _maxX + tolerance || y < _minY - tolerance || y > _maxY + tolerance)
                return false;
            foreach (var face in _faces)
                if (OnFace(face, x, y, z, tolerance)) return true;
            // Vertical ray upwards: an odd number of faces above the point means it is inside.
            int crossings = 0;
            foreach (var face in _faces)
            {
                if (Math.Abs(face.Nz) < 1e-9 || x < face.MinX || x > face.MaxX || y < face.MinY || y > face.MaxY) continue;
                double faceZ = (face.D - face.Nx * x - face.Ny * y) / face.Nz;
                if (faceZ > z && InPolygon(face.Outer, x, y) && !face.Holes.Any(hole => InPolygon(hole, x, y))) crossings++;
            }
            return crossings % 2 == 1;
        }

        private static bool OnFace(Face face, double x, double y, double z, double tolerance)
        {
            if (x < face.MinX - tolerance || x > face.MaxX + tolerance || y < face.MinY - tolerance || y > face.MaxY + tolerance
                || z < face.MinZ - tolerance || z > face.MaxZ + tolerance)
                return false;
            double distance = face.Nx * x + face.Ny * y + face.Nz * z - face.D;
            if (Math.Abs(distance) > tolerance) return false;
            // Project the point onto the face's plane and test it against the outline in the dominant plane.
            double px = x - distance * face.Nx, py = y - distance * face.Ny, pz = z - distance * face.Nz;
            double ax = Math.Abs(face.Nx), ay = Math.Abs(face.Ny), az = Math.Abs(face.Nz);
            Func<(double X, double Y, double Z), (double, double)> project = ax >= ay && ax >= az
                ? v => (v.Y, v.Z) : ay >= az ? v => (v.X, v.Z) : v => (v.X, v.Y);
            var (u, v2) = project((px, py, pz));
            return InPolygon2(face.Outer.Select(project).ToList(), u, v2, tolerance)
                && !face.Holes.Any(hole => InPolygon2(hole.Select(project).ToList(), u, v2, -tolerance));
        }

        private static bool InPolygon(List<(double X, double Y, double Z)> ring, double x, double y) =>
            InPolygon2(ring.Select(v => (v.X, v.Y)).ToList(), x, y, 0);

        /// <summary>Even-odd containment; a point within <paramref name="margin"/> of an edge counts as inside for a
        /// positive margin and as outside for a negative one.</summary>
        private static bool InPolygon2(List<(double, double)> ring, double x, double y, double margin)
        {
            bool inside = false;
            double marginSquared = margin * margin;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                var (ax, ay) = ring[j];
                var (bx, by) = ring[i];
                if ((ay > y) != (by > y) && x < (bx - ax) * (y - ay) / (by - ay) + ax) inside = !inside;
                if (margin != 0)
                {
                    double dx = bx - ax, dy = by - ay;
                    double lengthSquared = dx * dx + dy * dy;
                    double t = lengthSquared == 0 ? 0 : Math.Clamp(((x - ax) * dx + (y - ay) * dy) / lengthSquared, 0, 1);
                    double ex = ax + t * dx - x, ey = ay + t * dy - y;
                    if (ex * ex + ey * ey <= marginSquared) return margin > 0;
                }
            }
            return inside;
        }
    }
}
