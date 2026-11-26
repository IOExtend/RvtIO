using RvtIO.Geometry;
using RvtIO.Internal;
using RvtIO.Model;

namespace RvtIO.Parsing;

/// <summary>Measures the closed bodies in the current project's saved geometry blocks.</summary>
internal sealed class WideSolidQuantities
{
    internal enum Source { Default, Analytic, Directional, Fitting, StructuralFraming, FilledSurface, VisibleVolume, RawBodies, DirectionalRawSurface }

    internal readonly record struct Solid(double Volume, double Surface, double? LargestFace, int BodyCount, double? SharedCoarseVolume = 0)
    {
        internal Solid Add(Solid inner) => new(Volume + inner.Volume, Surface + inner.Surface,
            LargestFace is double a && inner.LargestFace is double b ? Math.Max(a, b) : null,
            BodyCount + inner.BodyCount, SharedCoarseVolume + inner.SharedCoarseVolume);
    }

    private static readonly Lazy<SchemaDocument> GeometrySchema = new(() =>
    {
        using var source = typeof(WideSolidQuantities).Assembly.GetManifestResourceStream("RvtIO.Resources.r22_schemas.json")!;
        return TfSchemaReader.ReadShipped2022(source);
    });

    private readonly R22Transcoder _geometryLayout;
    private readonly Dictionary<int, (PartitionFraming.Framed Frame, PartitionFraming.ProjectItem Item)> _records = [];
    private readonly Dictionary<(int Id, Source Source), Solid?[]?> _results = [];
    private readonly Dictionary<int, (double? Top, double? Bottom, double? Planar)> _massAreas = [];
    private readonly HashSet<(long X, long Y, long Z)> _splitVertices = [];

    internal WideSolidQuantities(FileSchema schema, IReadOnlyList<PartitionFraming.Framed?> frames, ElementIdMap? ids = null)
        : this(schema.Document, frames, 8, ids) { }

    internal WideSolidQuantities(SchemaDocument schema, IReadOnlyList<PartitionFraming.Framed?> frames, int idWidth, ElementIdMap? ids)
    {
        _geometryLayout = new(schema, GeometrySchema.Value, idWidth, ids);
        var ambiguous = new HashSet<int>();
        foreach (var frame in frames)
        {
            if (frame == null || frame.IdWidth != idWidth) continue;
            foreach (var item in frame.Items)
            {
                if (item.Channel != 103 || item.Owner is 0 or > long.MaxValue) continue;
                int id;
                if (ids == null)
                {
                    if (item.Owner > int.MaxValue) continue;
                    id = (int)item.Owner;
                }
                else if (!ids.TryGetKey((long)item.Owner, out id)) continue;
                if (!_records.TryAdd(id, (frame, item))) ambiguous.Add(id);
            }
        }
        foreach (int id in ambiguous) _records.Remove(id);
    }

    internal Solid?[]? Measure(int id, Source source = Source.Default) => Measure(id, source, []);

    internal double? OwnCoarseVolume(int id) => OwnCoarseVolume(Read(id, Source.VisibleVolume));

    internal static double? OwnCoarseVolume(GRepData? fine)
    {
        var coarse = fine?.Coarse ?? fine;
        return coarse is { IsOpenSurface: false, Instances.Count: 0, SourceBodyMeasures: { Count: > 0, Volume: > 0 } body }
            && double.IsFinite(body.Volume) ? body.Volume : null;
    }

    internal double? WallArea(int id, double dx, double dy, double dz, double? width, bool curtain)
    {
        if (Read(id) is not { } fine) return null;
        return WallSideArea(fine.Coarse ?? fine, dx, dy, dz, width, curtain);
    }

    internal double? HorizontalArea(int id, bool bottom) => HorizontalArea(id, bottom, []);

    internal double? MassArea(int id, Solid solid)
    {
        var areas = MassAreas(id);
        return MassArea(solid, areas.Top, areas.Bottom) ?? areas.Planar / 2;
    }

    internal bool HasOneHorizontalCap(int id)
    {
        var (top, bottom, _) = MassAreas(id);
        return top is > 0 && bottom == 0 || bottom is > 0 && top == 0;
    }

    private (double? Top, double? Bottom, double? Planar) MassAreas(int id)
    {
        if (_massAreas.TryGetValue(id, out var known)) return known;
        return _massAreas[id] = Collect(id, []);

        (double? Top, double? Bottom, double? Planar) Collect(int elementId, HashSet<int> visiting)
        {
            if (visiting.Count >= 32 || !visiting.Add(elementId)) return default;
            try
            {
                if (Read(elementId, Source.Analytic) is not { } fine) return default;
                var source = fine.Coarse ?? fine;
                var top = HorizontalArea(source, bottom: false);
                var bottom = HorizontalArea(source, bottom: true);
                var planar = HorizontalArea(source, bottom: false, planarMass: true);
                foreach (var instance in source.Instances)
                {
                    var placement = instance.Transform;
                    if (!ElementBuilder.IsRigidPlacement(placement) || Math.Abs(placement[8]) < 1 - 1e-9) return default;
                    var child = Collect(instance.SymbolElemId, visiting);
                    bool reversed = placement[8] < 0;
                    top += reversed ? child.Bottom : child.Top;
                    bottom += reversed ? child.Top : child.Bottom;
                    planar += child.Planar;
                }
                return (top, bottom, planar);
            }
            finally { visiting.Remove(elementId); }
        }
    }

    internal double? RoofArea(int id, IReadOnlySet<int> faceTags)
    {
        if (Read(id, Source.Analytic) is not { } fine) return null;
        return RoofArea(fine.Coarse ?? fine, faceTags);
    }

    internal static double? RoofArea(GRepData source, IReadOnlySet<int> faceTags)
    {
        if (source.Instances.Count != 0 || source.Solids.Count != 1 || !HasCompletePlanarShells(source)
            || source.Faces.Any(face => face.GInfoTag == null)) return null;
        var measured = new HashSet<uint>();
        double area = 0;
        foreach (var face in source.Faces)
            if (face.XVector[2] < -1e-9 && face.GInfoTag is int tag && faceTags.Contains(tag))
                area += PlanarArea(face, measured);
        return area > 0 && double.IsFinite(area) ? area : null;
    }

    internal double? FilledArea(int id) => Read(id, Source.FilledSurface)?.FilledArea;

    internal double? CurtainDoorArea(int id)
    {
        if (Read(id) is not { } own || (own.Coarse ?? own).Instances is not [var mapped]
            || (own.Coarse ?? own).Faces.Count != 0 || !ElementBuilder.IsRigidPlacement(mapped.Transform)
            || Read(mapped.SymbolElemId) is not { } symbol) return null;
        return CurtainDoorArea(symbol.Coarse ?? symbol);
    }

    internal static double? CurtainDoorArea(GRepData geometry)
    {
        const double tolerance = 1e-7;
        if (geometry.IsOpenSurface || geometry.UntriangulatedFaces != 0 || geometry.Instances.Count != 0) return null;
        var panels = new List<(double Left, double Right, double Bottom, double Top, double Front)>();
        foreach (var solid in geometry.Solids.Where(solid => solid.IsClosed && solid.FaceIndices.Count == 6))
        {
            var faces = solid.FaceIndices.Select(index => geometry.Faces[index]).ToList();
            if (!faces.All(face => face.PlaneSurface && face.Vertices is { Count: 4 } && face.InnerVertices is not { Count: > 0 }
                && face.MeshArcs is not { Count: > 0 })) continue;
            if (faces.Where(face => face.XVector[1] > 1 - tolerance).ToList() is not [var front]) continue;
            var points = front.Vertices!;
            double left = points.Min(point => point[0]), right = points.Max(point => point[0]);
            double bottom = points.Min(point => point[2]), top = points.Max(point => point[2]);
            if (Math.Abs(BrepMeasures.RingArea(points) - (right - left) * (top - bottom)) > tolerance) return null;
            panels.Add((left, right, bottom, top, front.Origin[1]));
        }
        if (panels.Count is < 1 or > 2) return null;
        var first = panels[0];
        if (panels.Any(panel => Math.Abs(panel.Bottom - first.Bottom) > tolerance || Math.Abs(panel.Top - first.Top) > tolerance
            || Math.Abs(panel.Front - first.Front) > tolerance)) return null;
        return (panels.Max(panel => panel.Right) - panels.Min(panel => panel.Left)) * (first.Top - first.Bottom);
    }

    private double? HorizontalArea(int id, bool bottom, HashSet<int> visiting)
    {
        if (visiting.Count >= 32 || !visiting.Add(id)) return null;
        try
        {
            if (Read(id, Source.Analytic) is not { } fine) return null;
            var source = fine.Coarse ?? fine;
            double? area = HorizontalArea(source, bottom);
            foreach (var instance in source.Instances)
            {
                var placement = instance.Transform;
                if (!ElementBuilder.IsRigidPlacement(placement) || Math.Abs(placement[8]) < 1 - 1e-9) return null;
                area += HorizontalArea(instance.SymbolElemId, bottom ^ (placement[8] < 0), visiting);
            }
            return area;
        }
        finally { visiting.Remove(id); }
    }

    internal static double? HorizontalArea(GRepData source, bool bottom, bool planarMass = false)
    {
        if (planarMass && !HasCompletePlanarShells(source)) return null;
        if (source.SourceBodyMeasures.HasValue)
            return source.SourceCapAreas is { } caps ? (bottom ? caps.Bottom : caps.Top) : null;
        if (source.UntriangulatedFaces != 0 || source.IsOpenSurface) return null;
        var measured = new HashSet<uint>();
        double area = 0;
        foreach (var face in source.Faces)
        {
            if (!face.PlaneSurface || (planarMass ? Math.Abs(face.XVector[2]) <= 1e-9
                : (bottom ? -face.XVector[2] : face.XVector[2]) < 1 - 1e-9)) continue;
            area += PlanarArea(face, measured);
            if (!double.IsFinite(area)) return null;
        }
        return area;
    }

    private static bool HasCompletePlanarShells(GRepData source)
        => !source.IsOpenSurface && source.UntriangulatedFaces == 0 && !source.SourceBodyMeasures.HasValue
            && source.Faces.All(face => face.PlaneSurface)
            && source.Solids.All(solid => solid.IsClosed)
            && source.Solids.SelectMany(solid => solid.FaceIndices).ToHashSet().SetEquals(Enumerable.Range(0, source.Faces.Count));

    internal static double? MassArea(Solid solid, double? top, double? bottom)
    {
        if (solid.BodyCount != 1 || bottom is not > 0) return null;
        if (top is > 0 && Math.Abs(top.Value - bottom.Value) <= 1e-8 * bottom.Value) return top;
        return top == 0 && solid.LargestFace.HasValue ? solid.Surface / 2 : null;
    }

    private static double PlanarArea(GRepFace face, HashSet<uint> measured)
    {
        if (face.SourceFace == 0 || face.Vertices is not { Count: >= 3 } outer) return double.NaN;
        if (face.QuadratureArea is double area) return measured.Add(face.SourceFace) ? area : 0;
        if (face.MeshArcs is { Count: > 0 }) return double.NaN;
        return BrepMeasures.RingArea(outer) - (face.InnerVertices?.Sum(BrepMeasures.RingArea) ?? 0);
    }

    internal static double? WallSideArea(GRepData source, double dx, double dy, double dz, double? width, bool curtain)
    {
        const double tolerance = 1e-6;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (source.UntriangulatedFaces > 0 || source.Instances.Count != 0 || source.IsOpenSurface
            || !double.IsFinite(length) || length <= 0 || !double.IsFinite(dz) || Math.Abs(dz) > tolerance
            || width is double w && (!double.IsFinite(w) || w <= 0)) return null;
        double nx = -dy / length, ny = dx / length;

        if (!curtain) return Sides(source.Faces)?.Area;

        // The curtain wall's closed profile occurs twice; the open faces describe its panel grid.
        var profiles = source.Solids.Where(solid => solid.IsClosed).ToList();
        if (profiles.Count != 2) return null;
        var a = Sides(profiles[0].FaceIndices.Select(index => source.Faces[index]));
        var b = Sides(profiles[1].FaceIndices.Select(index => source.Faces[index]));
        return a is { } first && b is { } second && Math.Abs(first.Min - second.Min) <= tolerance
            && Math.Abs(first.Max - second.Max) <= tolerance
            && Math.Abs(first.Area - second.Area) <= tolerance ? first.Area : null;

        (double Min, double Max, double Area)? Sides(IEnumerable<GRepFace> faces)
        {
            var measured = new HashSet<uint>();
            var planes = new List<(double Offset, double Area)>();
            foreach (var face in faces)
            {
                if (!face.PlaneSurface) continue;
                var normal = face.XVector;
                if (Math.Abs(normal[2]) > tolerance || Math.Abs(normal[0] * nx + normal[1] * ny) < 1 - tolerance) continue;
                double area = PlanarArea(face, measured);
                if (!double.IsFinite(area) || area < 0) return null;
                if (area == 0) continue;
                double offset = face.Origin[0] * nx + face.Origin[1] * ny;
                int index = planes.FindIndex(plane => Math.Abs(plane.Offset - offset) <= tolerance);
                if (index < 0) planes.Add((offset, area));
                else planes[index] = (planes[index].Offset, planes[index].Area + area);
            }
            if (planes.Count < 2) return null;
            var low = planes.MinBy(plane => plane.Offset);
            var high = planes.MaxBy(plane => plane.Offset);
            if (width is double stored && Math.Abs(high.Offset - low.Offset - stored) > tolerance) return null;
            if (curtain && (planes.Count != 2 || Math.Abs(high.Area - low.Area) > tolerance)) return null;
            return (low.Offset, high.Offset, Math.Max(low.Area, high.Area));
        }
    }

    private Solid?[]? Measure(int id, Source source, HashSet<int> visiting)
    {
        var key = (id, source);
        if (_results.TryGetValue(key, out var known)) return known;
        if (visiting.Count >= 32 || !visiting.Add(id)) return null;
        try
        {
            if (Read(id, source) is not { } fine) return _results[key] = null;
            GRepData[] levels = [fine.Coarse ?? fine, fine.Medium ?? fine, fine];
            var localFine = Local(fine, source, _splitVertices);
            Solid?[] result = [fine.Coarse == null ? localFine : Local(fine.Coarse, source, _splitVertices),
                fine.Medium == null ? localFine : Local(fine.Medium, source, _splitVertices), localFine];
            var localCoarse = result[0];
            for (int detail = 0; detail < levels.Length; detail++)
            {
                if (detail > 0 && source is not (Source.Directional or Source.VisibleVolume or Source.RawBodies or Source.DirectionalRawSurface)
                    && result[detail] is { } measured)
                    result[detail] = measured with { SharedCoarseVolume = SharedLocalVolume(levels[0], levels[detail], localCoarse) };
                if (source == Source.Fitting && detail > 0 && result[detail]?.SharedCoarseVolume == null
                    && WithoutReusedCoarseBodies(levels[0], levels[detail]) is { } body)
                    result[detail] = Local(body, splitVertices: _splitVertices);
                foreach (var instance in levels[detail].Instances)
                {
                    if (result[detail] is not { } local) break;
                    if (!ElementBuilder.IsRigidPlacement(instance.Transform)
                        || Measure(instance.SymbolElemId, source, visiting)?[detail] is not { } placed)
                    {
                        result[detail] = null;
                        break;
                    }
                    double? shared = detail == 0 || levels[0].Instances.Count == 0 ? 0
                        : levels[0].Instances is [var coarse] && levels[detail].Instances.Count == 1
                            && coarse.SymbolElemId == instance.SymbolElemId && coarse.Transform.SequenceEqual(instance.Transform)
                            ? placed.SharedCoarseVolume : null;
                    result[detail] = local.Add(placed with { SharedCoarseVolume = shared });
                }
            }
            if (source == Source.StructuralFraming && result[2] is { BodyCount: > 1 } && (fine.Instances.Count != 1 || fine.Faces.Count != 0))
            {
                // A detailed frame can repeat the complete Medium bodies and add detail solids.
                var medium = result[1];
                result[2] = fine.Instances.Count == 0 && levels[0].Faces.Count == 0 && medium is { Volume: > 0 } body
                    && SharedLocalVolume(levels[1], fine, body) == body.Volume ? medium : null;
            }
            return _results[key] = result;
        }
        finally { visiting.Remove(id); }
    }

    internal static double? SharedLocalVolume(GRepData coarse, GRepData detailed, Solid? coarseMeasure)
    {
        if (coarse.Faces.Count == 0 || detailed.Faces.Count == 0) return 0;
        if (coarseMeasure == null) return null;
        if (ReferenceEquals(coarse, detailed)) return coarseMeasure.Value.Volume;
        var coarseFaces = coarse.Faces.Select(face => face.SourceFace).ToHashSet();
        var detailedFaces = detailed.Faces.Select(face => face.SourceFace).ToHashSet();
        if (coarseFaces.Contains(0) || detailedFaces.Contains(0)) return null;
        if (!coarseFaces.Overlaps(detailedFaces)) return 0;

        // A fitting's coarse proxy can also occur in a detailed view. Its complete solid must match by source face.
        if (coarse.Solids.Count != coarseMeasure.Value.BodyCount || detailed.Solids.Count == 0
            || coarse.Solids.Any(body => body.FaceIndices.Any(index => (uint)index >= (uint)coarse.Faces.Count))
            || detailed.Solids.Any(body => body.FaceIndices.Any(index => (uint)index >= (uint)detailed.Faces.Count))) return null;
        var detailedBodies = detailed.Solids.Select(body => body.FaceIndices.Select(index => detailed.Faces[index].SourceFace).ToHashSet()).ToArray();
        foreach (var body in coarse.Solids)
        {
            var faces = body.FaceIndices.Select(index => coarse.Faces[index].SourceFace).ToHashSet();
            if (detailedBodies.Count(other => other.SetEquals(faces)) != 1) return null;
        }
        return coarseMeasure.Value.Volume;
    }

    private static GRepData? WithoutReusedCoarseBodies(GRepData coarse, GRepData detailed)
    {
        if (coarse.Solids.Count == 0 || detailed.Solids.Count == 0 || ReferenceEquals(coarse, detailed)
            || coarse.Solids.Any(body => !body.IsClosed) || detailed.Solids.Any(body => !body.IsClosed)
            || coarse.Solids.SelectMany(body => body.FaceIndices).Distinct().Count() != coarse.Faces.Count
            || detailed.Solids.SelectMany(body => body.FaceIndices).Distinct().Count() != detailed.Faces.Count) return null;

        var excluded = new HashSet<int>();
        var fineFaces = detailed.Solids.Select(body => body.FaceIndices.Select(index => detailed.Faces[index].SourceFace).ToHashSet()).ToArray();
        foreach (var body in coarse.Solids)
        {
            var ids = body.FaceIndices.Select(index => coarse.Faces[index].SourceFace).ToHashSet();
            if (ids.Contains(0)) return null;
            var matches = Enumerable.Range(0, fineFaces.Length).Where(index => fineFaces[index].Overlaps(ids)).ToList();
            if (matches.Count != 1 || !fineFaces[matches[0]].SetEquals(ids) || !excluded.Add(matches[0])) return null;
        }

        var faces = new List<GRepFace>();
        var solids = new List<GRepSolid>();
        for (int i = 0; i < detailed.Solids.Count; i++)
        {
            if (excluded.Contains(i)) continue;
            var indices = detailed.Solids[i].FaceIndices;
            solids.Add(new GRepSolid { IsClosed = true, FaceIndices = Enumerable.Range(faces.Count, indices.Count).ToList() });
            faces.AddRange(indices.Select(index => detailed.Faces[index]));
        }
        return detailed with { Faces = faces, Solids = solids };
    }

    private GRepData? Read(int id, Source source = Source.Default)
    {
        if (!_records.TryGetValue(id, out var record)) return null;
        int size = record.Item.Length - record.Frame.IdWidth - 12;
        if (size is < 2 or > (int)BlockParser.MaxBlockSize) return null;
        var (data, start, _) = record.Frame.Bytes(record.Item);
        int payloadStart = start + record.Frame.IdWidth + 8;
        if (_geometryLayout.TargetId(BitConverter.ToUInt16(data, payloadStart)) is not (2006 or 2007)
            || _geometryLayout.Payload(data, payloadStart, size) is not { } payload || payload.Length > BlockParser.MaxBlockSize)
            return null;

        byte[] block = new byte[payload.Length + 16];
        BitConverter.TryWriteBytes(block, id);
        BitConverter.TryWriteBytes(block.AsSpan(8), payload.Length);
        payload.CopyTo(block, 12);
        BitConverter.TryWriteBytes(block.AsSpan(12 + payload.Length), payload.Length);
        return GRepParserV2.Parse(block, 0, (uint)payload.Length, requireCompleteWalk: true,
            measureSourceNodes: source is Source.Analytic or Source.Fitting or Source.StructuralFraming,
            measureLighting: source == Source.Directional, measureFills: source == Source.FilledSurface,
            bodyMeasure: source switch
            {
                Source.VisibleVolume => GRepParserV2.BodyMeasure.VisibleVolume,
                Source.RawBodies => GRepParserV2.BodyMeasure.RawBodies,
                Source.DirectionalRawSurface => GRepParserV2.BodyMeasure.DirectionalRawSurface,
                _ => GRepParserV2.BodyMeasure.None,
            });
    }

    internal static Solid? Local(GRepData source, Source quantitySource = Source.Default,
        HashSet<(long X, long Y, long Z)>? splitVertices = null)
    {
        if (quantitySource == Source.Directional)
            return source.LightingBodyMeasures is { } analytic
                ? new Solid(analytic.Volume, analytic.Area, null, analytic.Count) : null;
        if (quantitySource == Source.StructuralFraming && source.TrimmedBodyMeasures is { Volume: > 0 } trimmed)
            return new Solid(trimmed.Volume, trimmed.Area, null, 1);
        if (source.SourceBodyMeasures is { } complete && !source.IsOpenSurface)
            return new Solid(complete.Volume, complete.Area, null, complete.Count);
        if (quantitySource is Source.VisibleVolume or Source.RawBodies or Source.DirectionalRawSurface) return null;
        if (source.NodeMeasures?.Any(measure => measure.SourceFaces != null) == true)
            source = source with { PreserveSourceFaceIdentity = true };
        if (source.UntriangulatedFaces > 0 || source.IsOpenSurface) return null;
        if (source.Faces.Count == 0) return new Solid(0, 0, 0, 0);
        if (ElementBuilder.BuildGeometryData(source, separateFacesWithDistinctOpenings: true) is not { } geometry
            || geometry.BrepSolids is not { Count: > 0 } solids || solids.Any(solid => !solid.IsClosed)
            || !solids.SelectMany(solid => solid.FaceIndices).ToHashSet().SetEquals(Enumerable.Range(0, geometry.Faces.Count)))
            return null;

        if (geometry.Faces.All(face => face.PlaneSurface && face.MeshArcs is not { Count: > 0 })
            && SolidQuantities.OfAllSolids(geometry) is { } polyhedron)
            return new Solid(polyhedron.Volume, polyhedron.SurfaceArea, polyhedron.LargestFaceArea, solids.Count);

        var shells = BrepMeasures.ShellsOfCurrent(geometry, splitVertices);
        if (shells.Count != solids.Count) return null;
        Solid total = new(0, 0, 0, 0);
        var measuredBodies = new HashSet<int>();
        if (source.NodeMeasures is { Count: > 0 } measures)
        {
            var shellFaces = shells.Select(shell => shell.Faces.Select(face => face.SourceFace).ToHashSet()).ToArray();
            foreach (var measure in measures)
            {
                if (measure.SourceFaces is not { } sourceFaces || measure.Volume <= 0) continue;
                var bodies = Enumerable.Range(0, shells.Count).Where(index => !shellFaces[index].Contains(0)
                    && shellFaces[index].IsSubsetOf(sourceFaces)).ToList();
                if (bodies.Count == 0 || bodies.Any(measuredBodies.Contains)
                    || !bodies.SelectMany(index => shellFaces[index]).ToHashSet().SetEquals(sourceFaces)) continue;
                measuredBodies.UnionWith(bodies);
                total = total.Add(new Solid(measure.Volume, measure.Area, null, bodies.Count));
            }
        }
        for (int i = 0; i < shells.Count; i++)
        {
            if (measuredBodies.Contains(i)) continue;
            var shell = shells[i];
            if (shell.SourceMeasured)
                total = total.Add(new Solid(shell.Volume, shell.Area, null, 1));
            else if (shell.Faces.All(face => face.PlaneSurface && face.MeshArcs is not { Count: > 0 })
                && SolidQuantities.OfSingleClosedShell(new GeometryData { Faces = geometry.Faces, BrepSolids = [solids[i]] }) is { } flat)
                total = total.Add(new Solid(flat.Volume, flat.SurfaceArea, flat.LargestFaceArea, 1));
            else return null;
        }
        return total;
    }
}
