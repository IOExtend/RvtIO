using RvtIO.Geometry;
using System.Globalization;
using RvtIO.Model;

namespace RvtIO.Ifc;

/// <summary>
/// The extruded solids the current profile writes for every Body but those of walls and openings: slabs, coverings, roof slabs,
/// the bodies of the type of a family (column, member, plate, door, window, proxy, furniture...), spaces.
/// </summary>
public partial class IfcExporter
{
    private const double OthersCurrentAreaTolerance = 1e-4;
    private const double OthersCurrentAngleTolerance = 1e-10;
    private const double OthersCurrentZero = 1e-10;
    private const double OthersCurrentSameVertex = 1e-9;
    private const double OthersCurrentVertexTolerance = 0.0005233832795;

    internal static readonly double[][] OthersCurrentAxesZ = [[0, 0, 1]];
    internal static readonly double[][] OthersCurrentAxesXyz = [[1, 0, 0], [0, 1, 0], [0, 0, 1]];

    /// <summary>One IfcExtrudedAreaSolid: the profile regions of the lower face of the face pair, along the unit axis.</summary>
    internal sealed class OthersCurrentExtrusion
    {
        public required double[] Axis { get; init; }
        public required double LengthFt { get; init; }
        /// <summary>One region per profile: the outer loop and the loops of its holes (feet, as the decoded face holds them), and the arcs
        /// of the outer loop when its circular edges were resampled (<c>TryResampleArcsCurrent</c>).</summary>
        public required List<(List<double[]> Outer, List<List<double[]>> Inner, IReadOnlyList<ArcChordsCurrent>? OuterArcs)> Regions { get; init; }
        public IReadOnlyList<double[]>? FirstSourceLoop { get; init; }
        /// <summary>The corners of the outer loop of the first region when its circular edges were resampled (<c>ResampleArcsCurrent</c>); the
        /// origin of the frame is their mean, the samples of an arc do not count.</summary>
        public IReadOnlyList<double[]>? FirstLoopCorners { get; init; }
    }

    private sealed class OthersCurrentFace
    {
        public required List<GeoFace> Regions { get; init; }
        public double[] Normal { get; set; } = [0, 0, 0];
        public bool Valid { get; set; }
        public bool Curved { get; set; }
        public bool HasOpenings { get; set; }
        public double NetArea { get; set; }
        public double[] Point { get; set; } = [0, 0, 0];
    }

    private static double OthersCurrentDot(IReadOnlyList<double> a, IReadOnlyList<double> b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    private static double[] OthersCurrentCross(IReadOnlyList<double> a, IReadOnlyList<double> b) =>
        [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];

    private static double[] OthersCurrentSub(IReadOnlyList<double> a, IReadOnlyList<double> b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];

    private static double OthersCurrentLength(IReadOnlyList<double> a) => Math.Sqrt(OthersCurrentDot(a, a));

    private static double[] OthersCurrentUnit(IReadOnlyList<double> a)
    {
        double length = OthersCurrentLength(a);
        return length <= 0 ? [0, 0, 0] : [a[0] / length, a[1] / length, a[2] / length];
    }

    /// <summary>Newell's area vector of a loop (half the cross-product sum): its direction is the right-hand normal of the loop.</summary>
    private static double[] OthersCurrentAreaVector(IReadOnlyList<double[]> loop)
    {
        double x = 0, y = 0, z = 0;
        for (int i = 0; i < loop.Count; i++)
        {
            var a = loop[i];
            var b = loop[(i + 1) % loop.Count];
            x += (a[1] - b[1]) * (a[2] + b[2]);
            y += (a[2] - b[2]) * (a[0] + b[0]);
            z += (a[0] - b[0]) * (a[1] + b[1]);
        }
        return [x / 2, y / 2, z / 2];
    }

    private static bool OthersCurrentParallel(double[] a, double[] b)
    {
        var u = OthersCurrentUnit(a);
        var v = OthersCurrentUnit(b);
        return Enumerable.Range(0, 3).All(i => Math.Abs(u[i] - v[i]) <= OthersCurrentAngleTolerance)
            || Enumerable.Range(0, 3).All(i => Math.Abs(u[i] + v[i]) <= OthersCurrentAngleTolerance);
    }

    private static bool OthersCurrentPerpendicular(double[] a, double[] b) =>
        Math.Abs(OthersCurrentDot(OthersCurrentUnit(a), OthersCurrentUnit(b))) <= OthersCurrentAngleTolerance;

    private static bool OthersCurrentFinite(IReadOnlyList<double[]> loop) =>
        loop.All(p => p.Length >= 3 && double.IsFinite(p[0]) && double.IsFinite(p[1]) && double.IsFinite(p[2]));

    private static List<OthersCurrentFace> OthersCurrentMergeRegions(IEnumerable<GeoFace> faces)
    {
        var result = new List<OthersCurrentFace>();
        var bySource = new Dictionary<uint, OthersCurrentFace>();
        foreach (var face in faces)
        {
            if (face.SourceFace != 0 && bySource.TryGetValue(face.SourceFace, out var merged))
            {
                merged.Regions.Add(face);
                continue;
            }
            var meshFace = new OthersCurrentFace { Regions = [face] };
            if (face.SourceFace != 0) bySource[face.SourceFace] = meshFace;
            result.Add(meshFace);
        }
        foreach (var face in result)
        {
            var rings = face.Regions.Where(region => region.OuterVertices is { Count: >= 3 } outer && OthersCurrentFinite(outer)).ToList();
            face.Valid = rings.Count > 0;
            face.Curved = face.Regions.Any(region => region.CurvedSurface || region.AnalyticCylinderAxis != null && region.AnalyticCylinderRadius > 0);
            face.HasOpenings = face.Regions.Any(region => region.InnerVertices is { Count: > 0 });
            if (!face.Valid) continue;
            var normal = OthersCurrentAreaVector(rings[0].OuterVertices!);
            face.Normal = OthersCurrentUnit(normal);
            face.Point = rings[0].OuterVertices![0];
            face.NetArea = face.Regions.Select(region => region.QuadratureArea).FirstOrDefault(area => area != null)
                ?? rings.Sum(region => OthersCurrentLength(OthersCurrentAreaVector(region.OuterVertices!)))
                - rings.Sum(region => (region.InnerVertices ?? []).Sum(inner => OthersCurrentLength(OthersCurrentAreaVector(inner))));
        }
        return result;
    }

    internal static OthersCurrentExtrusion? CalculateOthersCurrentExtrusion(IReadOnlyList<GeoFace> solidFaces, IReadOnlyList<double[]> axes)
    {
        var merged = OthersCurrentMergeRegions(solidFaces);
        var planar = merged.Where(face => !face.Curved).ToList();
        if (planar.Count < 2) return null;

        double[] bodyMin = [double.MaxValue, double.MaxValue, double.MaxValue], bodyMax = [double.MinValue, double.MinValue, double.MinValue];
        bool authored = solidFaces.Any(face => face.CurvedSurface || face.AnalyticCylinderAxis != null && face.AnalyticCylinderRadius > 0)
            && solidFaces.All(face => face.SourceFace != 0 && face.MeshEdgeBounds is { Length: 6 });
        foreach (var face in solidFaces)
        {
            if (authored)
            {
                for (int a = 0; a < 3; a++)
                {
                    bodyMin[a] = Math.Min(bodyMin[a], face.MeshEdgeBounds![a]);
                    bodyMax[a] = Math.Max(bodyMax[a], face.MeshEdgeBounds![3 + a]);
                }
                continue;
            }
            foreach (var ring in new[] { face.OuterVertices }.Concat(face.InnerVertices ?? []))
                foreach (var p in ring ?? [])
                    if (p.Length >= 3 && double.IsFinite(p[0]) && double.IsFinite(p[1]) && double.IsFinite(p[2]))
                        for (int a = 0; a < 3; a++)
                        {
                            bodyMin[a] = Math.Min(bodyMin[a], p[a]);
                            bodyMax[a] = Math.Max(bodyMax[a], p[a]);
                        }
        }
        if (bodyMin[0] > bodyMax[0]) return null;

        foreach (var axis in axes)
        {
            var skip = new bool[planar.Count];
            var pairs = new List<(OthersCurrentFace Base, OthersCurrentFace Other, double Length)>();
            double[] endParams = [0, 0];
            bool failed = false;
            for (int i = 0; i < planar.Count - 1 && !failed; i++)
            {
                if (skip[i] || !planar[i].Valid || !OthersCurrentParallel(planar[i].Normal, axis)) { skip[i] = true; continue; }

                bool rejected = false;
                int j = i + 1;
                for (; j < planar.Count; j++)
                {
                    if (skip[j] || !planar[j].Valid) { skip[j] = true; continue; }
                    if (!OthersCurrentParallel(planar[i].Normal, planar[j].Normal)
                        || Math.Abs(planar[i].NetArea - planar[j].NetArea) > OthersCurrentAreaTolerance)
                        continue;
                    for (int k = 0; k < planar.Count; k++)
                    {
                        if (k == i || k == j || !planar[k].Valid) continue;
                        if (OthersCurrentPerpendicular(planar[k].Normal, axis) && planar[k].HasOpenings
                            || !OthersCurrentPerpendicular(planar[k].Normal, axis) && !OthersCurrentParallel(planar[k].Normal, axis))
                            rejected = true;
                    }
                    break;
                }
                if (rejected || j == planar.Count) continue;

                skip[i] = skip[j] = true;
                double firstAlong = OthersCurrentDot(planar[i].Point, axis), secondAlong = OthersCurrentDot(planar[j].Point, axis);
                double length = Math.Abs(secondAlong - firstAlong);
                if (length <= OthersCurrentZero) continue;

                if (pairs.Count > 0)
                {
                    double firstZ = axis[2] == 1 ? firstAlong : 0, secondZ = axis[2] == 1 ? secondAlong : 0;
                    bool Known(double z) => Math.Abs(z - endParams[0]) <= OthersCurrentZero || Math.Abs(z - endParams[1]) <= OthersCurrentZero;
                    if (!(Known(firstZ) && Known(secondZ))) { failed = true; continue; }
                }
                // The extrusion starts at the face with the smaller parameter along the axis.
                var (lower, upper) = firstAlong > secondAlong + OthersCurrentZero ? (planar[j], planar[i]) : (planar[i], planar[j]);
                pairs.Add((lower, upper, length));
                endParams = [firstAlong, secondAlong];
            }
            if (failed || pairs.Count == 0) continue;

            double[] facesMin = [double.MaxValue, double.MaxValue, double.MaxValue], facesMax = [double.MinValue, double.MinValue, double.MinValue];
            void AddPoint(double[] p)
            {
                for (int a = 0; a < 3; a++)
                {
                    facesMin[a] = Math.Min(facesMin[a], p[a]);
                    facesMax[a] = Math.Max(facesMax[a], p[a]);
                }
            }
            foreach (var (lower, upper, _) in pairs)
                foreach (var face in new[] { lower, upper })
                    OthersCurrentAddEnvelopeCorners(face, AddPoint, authored);
            if (OthersCurrentDistance(facesMin, bodyMin) > OthersCurrentVertexTolerance || OthersCurrentDistance(facesMax, bodyMax) > OthersCurrentVertexTolerance)
                continue;

            if (pairs.Count > 1 && pairs.Any(pair => Math.Abs(pair.Length - pairs[0].Length) * 304.8 > OthersCurrentZero))
                continue;

            var regions = new List<(List<double[]>, List<List<double[]>>, IReadOnlyList<ArcChordsCurrent>?)>();
            List<double[]>? firstCorners = null;
            foreach (var (lower, _, _) in pairs)
                foreach (var region in lower.Regions.Where(region => region.OuterVertices is { Count: >= 3 } outer && OthersCurrentFinite(outer)))
                {
                    var outerLoop = region.OuterVertices!.Select(p => p.Take(3).ToArray()).ToList();
                    var innerLoops = (region.InnerVertices ?? []).Where(inner => inner.Count >= 3 && OthersCurrentFinite(inner))
                        .Select(inner => inner.Select(p => p.Take(3).ToArray()).ToList()).ToList();
                    IReadOnlyList<ArcChordsCurrent>? outerArcs = null;
                    if (TryResampleArcsCurrent(outerLoop, innerLoops, region.MeshArcs, out var resampledOuter, out var resampledInner, out var corners,
                        out var arcs))
                    {
                        if (regions.Count == 0) firstCorners = corners;
                        (outerLoop, innerLoops, outerArcs) = (resampledOuter, resampledInner, arcs);
                    }
                    regions.Add((outerLoop, innerLoops, outerArcs));
                }
            if (regions.Count == 0) continue;
            if (pairs.Any(pair => pair.Base.Regions.Any(OthersCurrentHasShortCurve))) return null;
            // The same creation refuses a loop that crosses itself (the curve loop stays open) and a frame that is the identity.
            if (pairs.Any(pair => OthersCurrentLoopsIntersectThemselves(pair.Base)) || OthersCurrentFrameIsIdentity(pairs[0].Base, axis)) return null;
            return new OthersCurrentExtrusion { Axis = [axis[0], axis[1], axis[2]], LengthFt = pairs[0].Length,
                Regions = regions, FirstSourceLoop = pairs[0].Base.Regions[0].FirstSourceLoop, FirstLoopCorners = firstCorners };
        }
        return null;
    }

    private static bool OthersCurrentHasShortCurve(GeoFace region) =>
        region.MeshArcs is not { Count: > 0 }
        && new[] { region.OuterVertices }.Concat(region.InnerVertices ?? []).Any(ring =>
        {
            if (ring is not { Count: >= 3 }) return false;
            var vertices = new List<double[]>();
            foreach (var point in ring)
                if (vertices.Count == 0 || OthersCurrentDistance(vertices[^1], point) > OthersCurrentSameVertex) vertices.Add(point);
            if (vertices.Count > 1 && OthersCurrentDistance(vertices[0], vertices[^1]) <= OthersCurrentSameVertex) vertices.RemoveAt(vertices.Count - 1);
            return vertices.Count >= 3 && Enumerable.Range(0, vertices.Count).Any(index =>
                OthersCurrentDistance(vertices[index], vertices[(index + 1) % vertices.Count]) < WallExtrusion.ShortCurveTolerance);
        });

    private static double OthersCurrentDistance(IReadOnlyList<double> a, IReadOnlyList<double> b) => OthersCurrentLength(OthersCurrentSub(a, b));

    /// <summary>
    /// The two opposite corners of the (u, v) envelope of a face in the serialized plane frame; the whole outline when the
    /// plane frame was not read.
    /// </summary>
    private static void OthersCurrentAddEnvelopeCorners(OthersCurrentFace face, Action<double[]> add, bool storedEnvelope)
    {
        // The stored envelope of the face (not that of its dense outline) when the solid is judged by its authored samples.
        if (storedEnvelope && face.Regions[0].MeshPlaneEnvelopeCorners is { Length: 6 } corners)
        {
            add(corners[..3]);
            add(corners[3..]);
            return;
        }
        var outline = face.Regions.Where(region => region.OuterVertices is { Count: >= 3 } outer && OthersCurrentFinite(outer))
            .SelectMany(region => region.OuterVertices!).ToList();
        if (outline.Count == 0) return;
        if (face.Regions[0].PlaneXAxis is not { } x || face.Regions[0].PlaneYAxis is not { } y)
        {
            foreach (var p in outline) add(p);
            return;
        }
        var origin = outline[0];
        double minU = double.MaxValue, minV = double.MaxValue, maxU = double.MinValue, maxV = double.MinValue;
        foreach (var p in outline)
        {
            var d = OthersCurrentSub(p, origin);
            double u = OthersCurrentDot(d, x), v = OthersCurrentDot(d, y);
            minU = Math.Min(minU, u); maxU = Math.Max(maxU, u);
            minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
        }
        add([origin[0] + minU * x[0] + minV * y[0], origin[1] + minU * x[1] + minV * y[1], origin[2] + minU * x[2] + minV * y[2]]);
        add([origin[0] + maxU * x[0] + maxV * y[0], origin[1] + maxU * x[1] + maxV * y[1], origin[2] + maxU * x[2] + maxV * y[2]]);
    }

    internal double[][]? OthersCurrentSymbolAxes(Element element, string? ifcType)
    {
        if (!CurrentProfile || ifcType == null) return null;
        if (ifcType is not ("IFCBEAM" or "IFCCOLUMN" or "IFCMEMBER" or "IFCPILE")) return OthersCurrentAxesXyz;
        if (element.CurveDriverLineDirection is { } d) return [[d.X, d.Y, d.Z], [1, 0, 0], [0, 1, 0]];
        if (_diag != null && (element.CurveDriverRotation != null || element.CurveDriverRefLevelId != null || element.CurveDriverLineZ != null))
            _diag.WriteLine($"IFC axes: {ifcType} {element.Id} has a curve driver without a decoded line direction");
        return ifcType == "IFCBEAM" ? null : OthersCurrentAxesZ;
    }

    private bool OthersCurrentDirectBodyApplies(string ifcType, Element element) =>
        CurrentProfile && (ifcType is "IFCSLAB" or "IFCCOVERING" or "IFCBUILDINGELEMENTPROXY" or "IFCTRANSPORTELEMENT"
            || ifcType is "IFCCOLUMN" or "IFCPILE"
            || ifcType == "IFCROOF" && RoofIsExportedWithSlabCurrent(element));

    internal static List<(int Node, List<GeoFace> Faces)>? OthersCurrentSolids(GeometryData geo)
    {
        if (geo.Faces.Count == 0 || geo.GhostNodes is { Count: > 0 } || geo.BrepSolids is not { Count: > 0 } shells) return null;
        var closedFaces = shells.Where(shell => shell.IsClosed).SelectMany(shell => shell.FaceIndices).ToHashSet();
        bool anyNode = geo.Faces.Any(face => face.GeometryNode > 0);
        var solids = new List<(int Node, List<GeoFace> Faces)>();
        var slotByNode = new Dictionary<int, int>();
        for (int index = 0; index < geo.Faces.Count; index++)
        {
            var face = geo.Faces[index];
            if (face.GeometryNode < 0 && anyNode) continue;
            if (face.GeometryNode == 0 && anyNode || !closedFaces.Contains(index)) return null;
            if (!slotByNode.TryGetValue(face.GeometryNode, out int slot))
            {
                slotByNode[face.GeometryNode] = solids.Count;
                solids.Add((face.GeometryNode, []));
                slot = solids.Count - 1;
            }
            solids[slot].Faces.Add(face);
        }
        // Without any node every face belongs to one solid, as long as the decoded topology has one shell.
        if (!anyNode && shells.Count > 1) return null;
        // A face that two Geometry nodes share is decoded once, under the node that comes first, but each node has it in its own shell:
        // a node is the solid of its shells (the node of a shell is the largest node among its faces, as the Brep writer takes it).
        if (anyNode)
        {
            var members = solids.Select(solid => solid.Faces.ToHashSet()).ToList();
            foreach (var shell in shells.Where(shell => shell.IsClosed))
            {
                var shellFaces = shell.FaceIndices.Distinct().Where(index => (uint)index < geo.Faces.Count).ToList();
                int node = shellFaces.Select(index => geo.Faces[index].GeometryNode).DefaultIfEmpty(0).Max();
                if (node <= 0 || !slotByNode.TryGetValue(node, out int nodeSlot)) continue;
                foreach (int index in shellFaces)
                    if (geo.Faces[index].GeometryNode >= 0 && members[nodeSlot].Add(geo.Faces[index]))
                        solids[nodeSlot].Faces.Add(geo.Faces[index]);
            }
        }
        return solids.Count == 0 ? null : solids
            .OrderBy(solid => solid.Faces.FirstOrDefault(face => face.NodeOrder != 0)?.NodeOrder ?? long.MaxValue)
            .ThenBy(solid => solid.Node).ToList();
    }

    /// <summary>The solids of a geometry with the extrusion of each: the plan of a SweptSolid body.</summary>
    internal sealed class OthersCurrentPlan
    {
        public required List<(int Node, List<GeoFace> Faces)> Solids { get; init; }
        public required List<OthersCurrentExtrusion> Extrusions { get; init; }
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<GeometryData, Dictionary<object, OthersCurrentPlan?>> _othersCurrentPlans = new();

    /// <summary>
    /// The SweptSolid plan of a geometry along the axes, null when one of its solids has no extrusion there. A geometry is
    /// judged once per axes.
    /// </summary>
    internal OthersCurrentPlan? OthersCurrentGetPlan(GeometryData geo, double[][] axes)
    {
        var byAxes = _othersCurrentPlans.GetOrCreateValue(geo);
        if (byAxes.TryGetValue(axes, out var known)) return known;
        OthersCurrentPlan? plan = null;
        if (CurrentProfile && OthersCurrentSolids(geo) is { } solids)
        {
            var extrusions = new List<OthersCurrentExtrusion>();
            foreach (var (_, faces) in solids)
            {
                if (CalculateOthersCurrentExtrusion(faces, axes) is not { } extrusion) { extrusions = null!; break; }
                extrusions.Add(extrusion);
            }
            if (extrusions != null) plan = new OthersCurrentPlan { Solids = solids, Extrusions = extrusions };
        }
        byAxes[axes] = plan;
        return plan;
    }

    private bool TryCreateOthersCurrentSweptSolid(
        StepWriter w, GeometryData geo, int subCtxId, double[][] axes, string profileName,
        out FacetedShapeWriteResult result, double offX = 0, double offY = 0, double offZ = 0,
        bool allowCoaxialCurvedFaces = false)
    {
        result = default;
        if (OthersCurrentGetPlan(geo, axes) is not { } plan) return false;

        var checkpoint = w.CreateCheckpoint();
        var solidIds = new List<int>();
        for (int i = 0; i < plan.Solids.Count; i++)
        {
            var (node, faces) = plan.Solids[i];
            int solidId = WriteOthersCurrentSolid(w, plan.Extrusions[i], faces, profileName, offX, offY, offZ, allowCoaxialCurvedFaces);
            if (solidId <= 0)
            {
                w.Rollback(checkpoint);
                foreach (int id in solidIds) _bodyItemOrigins.Remove(id);
                return false;
            }
            solidIds.Add(solidId);
            if (node > 0) _bodyItemOrigins[solidId] = (geo, node);
        }
        int representationId = w.Add("IFCSHAPEREPRESENTATION",
            $"{R(subCtxId)},{S("Body")},{S("SweptSolid")},{StepWriter.List(solidIds.Select(R))}");
        result = new FacetedShapeWriteResult([representationId],
            new BodyRepresentationWriteOutcome(HasSurfaceModel: false, HasClosedBrepOrSweptSolidBody: true, OpenShellCount: 0, OpenShellFaceCount: 0));
        return true;
    }

    /// <summary>The Body of a product as one IfcProductDefinitionShape of the SweptSolid, else false.</summary>
    internal bool TryCreateOthersCurrentProductSweptSolid(
        StepWriter w, GeometryData geo, int subCtxId, double[][] axes, string profileName,
        out int shapeId, out BodyRepresentationWriteOutcome outcome)
    {
        shapeId = 0;
        outcome = default;
        if (!TryCreateOthersCurrentSweptSolid(w, geo, subCtxId, axes, profileName, out var written, allowCoaxialCurvedFaces: true)) return false;
        shapeId = w.Add("IFCPRODUCTDEFINITIONSHAPE", $"$,$,{StepWriter.List(written.RepresentationIds.Select(R))}");
        outcome = written.Outcome;
        return true;
    }

    private static string OthersCurrentProfileName(string familyAndType)
    {
        int colon = familyAndType.IndexOf(':');
        return colon < 0 ? familyAndType : familyAndType[(colon + 1)..];
    }

    private static double OthersCurrentClean(double value)
    {
        double ceil = Math.Ceiling(value), floor = Math.Floor(value);
        if (Math.Abs(value - ceil) <= 1e-9) return ceil;
        if (Math.Abs(value - floor) <= 1e-9) return floor;
        return value;
    }

    /// <summary>
    /// The plane frame of a normal: x runs along the normal's cross product with z up (with y when the normal is vertical), y
    /// completes the frame.
    /// </summary>
    private static (double[] X, double[] Y) OthersCurrentFrame(double[] normal)
    {
        double[] reference = Math.Abs(Math.Abs(normal[2]) - 1) < 1e-9 ? [0, 1, 0] : [0, 0, 1];
        var x = OthersCurrentUnit(OthersCurrentCross(reference, normal));
        return (x, OthersCurrentCross(normal, x));
    }

    /// <summary>A loop turned counter-clockwise (or clockwise) about the axis; the start vertex is kept.</summary>
    private static List<double[]> OthersCurrentOrient(List<double[]> loop, double[] axis, bool counterClockwise)
    {
        bool isCounterClockwise = OthersCurrentDot(OthersCurrentAreaVector(loop), axis) > 0;
        if (isCounterClockwise == counterClockwise) return loop;
        var result = new List<double[]>(loop.Count) { loop[0] };
        for (int i = loop.Count - 1; i >= 1; i--) result.Add(loop[i]);
        return result;
    }

    private static double[] OthersCurrentMean(IReadOnlyList<double[]> loop) =>
        [loop.Average(p => p[0]), loop.Average(p => p[1]), loop.Average(p => p[2])];

    private static bool OthersCurrentIsRectangle(IReadOnlyList<double[]> loop)
    {
        if (loop.Count != 4) return false;
        for (int i = 0; i < 4; i++)
        {
            var a = OthersCurrentSub(loop[(i + 1) % 4], loop[i]);
            var b = OthersCurrentSub(loop[(i + 2) % 4], loop[(i + 1) % 4]);
            double lengths = OthersCurrentLength(a) * OthersCurrentLength(b);
            if (lengths <= 1e-12 || Math.Abs(OthersCurrentDot(a, b)) > 1e-9 * lengths) return false;
        }
        return true;
    }

    /// <summary>A loop that is one full circle of the cylinder faces of the solid: every vertex on a cylinder about the axis, the
    /// polygon winding once. The decoded loop alone is not enough (a regular prism is not a cylinder).</summary>
    private static bool OthersCurrentTryGetCircle(
        IReadOnlyList<GeoFace> solidFaces, double[] axis, IReadOnlyList<double[]> loop, double[] offset,
        out double[] centre, out double radius)
    {
        centre = [];
        radius = 0;
        if (loop.Count < 8) return false;
        var cylinders = solidFaces.Where(face => face.AnalyticCylinderOrigin is { Length: >= 3 }
            && face.AnalyticCylinderAxis is { Length: >= 3 } cylinderAxis && OthersCurrentParallel(OthersCurrentUnit(cylinderAxis), axis)
            && face.AnalyticCylinderRadius > 0).ToList();
        foreach (var cylinder in cylinders)
        {
            var origin = OthersCurrentAdd(cylinder.AnalyticCylinderOrigin!, offset);
            double along = OthersCurrentDot(OthersCurrentSub(loop[0], origin), axis);
            double[] candidate = [origin[0] + along * axis[0], origin[1] + along * axis[1], origin[2] + along * axis[2]];
            double tolerance = 1e-6 * Math.Max(1, cylinder.AnalyticCylinderRadius);
            if (loop.Any(p => Math.Abs(OthersCurrentDistance(p, candidate) - cylinder.AnalyticCylinderRadius) > tolerance)) continue;
            if (centre.Length > 0 && (OthersCurrentDistance(centre, candidate) > tolerance
                || Math.Abs(radius - cylinder.AnalyticCylinderRadius) > tolerance)) return false;
            if (centre.Length == 0)
            {
                centre = candidate;
                radius = cylinder.AnalyticCylinderRadius;
            }
        }
        if (centre.Length == 0) return false;
        double winding = 0;
        for (int i = 0; i < loop.Count; i++)
        {
            var first = OthersCurrentSub(loop[i], centre);
            var second = OthersCurrentSub(loop[(i + 1) % loop.Count], centre);
            winding += Math.Atan2(OthersCurrentDot(axis, OthersCurrentCross(first, second)), OthersCurrentDot(first, second));
        }
        if (Math.Abs(Math.Abs(winding) - 2 * Math.PI) >= 1e-6) return false;
        return true;
    }

    private int OthersCurrentPoint2(StepWriter w, double xFt, double yFt) =>
        w.Add("IFCCARTESIANPOINT", StepWriter.List(D(xFt * FeetToMetres), D(yFt * FeetToMetres)));

    private int OthersCurrentDirection2(StepWriter w, double x, double y) =>
        w.Add("IFCDIRECTION", StepWriter.List(D(OthersCurrentClean(x)), D(OthersCurrentClean(y))));

    private int OthersCurrentPolyline(StepWriter w, IReadOnlyList<(double X, double Y)> points)
    {
        var ids = points.Select(point => OthersCurrentPoint2(w, point.X, point.Y)).ToList();
        ids.Add(ids[0]);
        return w.Add("IFCPOLYLINE", StepWriter.List(ids.Select(R)));
    }

    private int WriteOthersCurrentProfile(
        StepWriter w, IReadOnlyList<GeoFace> solidFaces, double[] axis,
        (List<double[]> Outer, List<List<double[]>> Inner, IReadOnlyList<ArcChordsCurrent>? OuterArcs) region,
        double[] origin, double[] x, double[] y, string profileName, double[] offset)
    {
        (double X, double Y) Project(double[] p)
        {
            var d = OthersCurrentSub(p, origin);
            return (OthersCurrentDot(d, x), OthersCurrentDot(d, y));
        }
        var outer = OthersCurrentOrient(region.Outer, axis, true);
        var inner = region.Inner.Select(loop => OthersCurrentOrient(loop, axis, false)).ToList();
        string name = S(profileName);

        if (inner.Count == 0 && OthersCurrentIsRectangle(outer))
        {
            var corners = outer.Select(Project).ToList();
            var start = corners[3];
            var xLength = (X: corners[0].X - start.X, Y: corners[0].Y - start.Y);
            var yLength = (X: corners[2].X - start.X, Y: corners[2].Y - start.Y);
            double xDim = Math.Sqrt(xLength.X * xLength.X + xLength.Y * xLength.Y);
            double yDim = Math.Sqrt(yLength.X * yLength.X + yLength.Y * yLength.Y);
            if (xDim <= 1e-12 || yDim <= 1e-12) return 0;
            int location = OthersCurrentPoint2(w, (start.X + corners[1].X) / 2, (start.Y + corners[1].Y) / 2);
            int refDirection = OthersCurrentDirection2(w, xLength.X / xDim, xLength.Y / xDim);
            int position = w.Add("IFCAXIS2PLACEMENT2D", $"{R(location)},{R(refDirection)}");
            return w.Add("IFCRECTANGLEPROFILEDEF",
                $".AREA.,{name},{R(position)},{D(xDim * FeetToMetres)},{D(yDim * FeetToMetres)}");
        }

        if (inner.Count <= 1 && OthersCurrentTryGetCircle(solidFaces, axis, outer, offset, out var centre, out double radius))
        {
            double innerRadius = 0;
            if (inner.Count == 1)
            {
                if (!OthersCurrentTryGetCircle(solidFaces, axis, inner[0], offset, out var innerCentre, out innerRadius)
                    || OthersCurrentDistance(centre, innerCentre) > 1e-9 * Math.Max(1, radius))
                    return 0;
            }
            var centreInPlane = Project(centre);
            int location = OthersCurrentPoint2(w, centreInPlane.X, centreInPlane.Y);
            int refDirection = OthersCurrentDirection2(w, 1, 0);
            int position = w.Add("IFCAXIS2PLACEMENT2D", $"{R(location)},{R(refDirection)}");
            return inner.Count == 0
                ? w.Add("IFCCIRCLEPROFILEDEF", $".AREA.,{name},{R(position)},{D(radius * FeetToMetres)}")
                : w.Add("IFCCIRCLEHOLLOWPROFILEDEF",
                    $".AREA.,{name},{R(position)},{D(radius * FeetToMetres)},{D((radius - innerRadius) * FeetToMetres)}");
        }

        int outerCurve = OthersCurrentPolyline(w, outer.Select(Project).ToList());
        // The arcs of the outline go with its polyline, at the points of the outline as written: a loop turned the other way
        // keeps its first point, so an arc then starts where it ended.
        if (region.OuterArcs is { Count: > 0 } arcs)
            w.PolylineArcs[outerCurve] = ReferenceEquals(outer, region.Outer) ? arcs
                : arcs.Select(arc => arc with { First = (2 * outer.Count - arc.First - arc.Chords) % outer.Count }).ToList();
        if (inner.Count == 0) return w.Add("IFCARBITRARYCLOSEDPROFILEDEF", $".AREA.,{name},{R(outerCurve)}");
        var innerCurves = inner.Select(loop => OthersCurrentPolyline(w, loop.Select(Project).ToList())).ToList();
        return w.Add("IFCARBITRARYPROFILEDEFWITHVOIDS",
            $".AREA.,{name},{R(outerCurve)},{StepWriter.List(innerCurves.Select(R))}");
    }

    private int WriteOthersCurrentSolid(
        StepWriter w, OthersCurrentExtrusion extrusion, IReadOnlyList<GeoFace> solidFaces, string profileName,
        double offX, double offY, double offZ, bool allowCoaxialCurvedFaces = false, double[]? originOverride = null)
    {
        double[] axis = extrusion.Axis;
        double[] offset = [offX, offY, offZ];
        List<(List<double[]> Outer, List<List<double[]>> Inner, IReadOnlyList<ArcChordsCurrent>? OuterArcs)> regions = extrusion.Regions
            .Select(region => (region.Outer.Select(p => OthersCurrentAdd(p, offset)).ToList(),
                region.Inner.Select(loop => loop.Select(p => OthersCurrentAdd(p, offset)).ToList()).ToList(), region.OuterArcs)).ToList();
        var (x, y) = OthersCurrentFrame(axis);

        // Polygonal caps do not preserve a curved side's boundary; only serialized complete circles are reproduced here
        // for direct bodies, whereas family symbol extrusions allow coaxial curved faces (such as fillets).
        if (!allowCoaxialCurvedFaces)
        {
            if (solidFaces.Any(face => face.CurvedSurface || face.AnalyticCylinderRadius > 0)
                && regions.Any(region => region.Inner.Count > 1
                    || !OthersCurrentTryGetCircle(solidFaces, axis, region.Outer, offset, out _, out _)
                    || region.Inner.Any(loop => !OthersCurrentTryGetCircle(solidFaces, axis, loop, offset, out _, out _))))
                return 0;
        }
        else
        {
            if (solidFaces.Any(face => face.CurvedSurface && face.AnalyticCylinderAxis == null
                || face.AnalyticCylinderAxis is { } cAxis && face.AnalyticCylinderRadius > 0 && !OthersCurrentParallel(OthersCurrentUnit(cAxis), axis)))
                return 0;
        }

        // The origin of the frame is the centre of the first loop; a circle is centred on its own centre.
        var first = OthersCurrentOrient(regions[0].Outer, axis, true);
        double[] origin = originOverride ?? (OthersCurrentTryGetCircle(solidFaces, axis, first, offset, out var centre, out _) ? centre
            : OthersCurrentMean(extrusion.FirstLoopCorners?.Select(p => OthersCurrentAdd(p, offset)).ToList() ?? first));

        var profiles = new List<int>();
        foreach (var region in regions)
        {
            int profileId = WriteOthersCurrentProfile(w, solidFaces, axis, region, origin, x, y, profileName, offset);
            if (profileId <= 0) return 0;
            profiles.Add(profileId);
        }
        int sweptArea = profiles.Count == 1 ? profiles[0]
            : w.Add("IFCCOMPOSITEPROFILEDEF", $".AREA.,{S(profileName)},{StepWriter.List(profiles.Select(R))},$");

        int originId = w.Add("IFCCARTESIANPOINT", StepWriter.List(
            D(origin[0] * FeetToMetres), D(origin[1] * FeetToMetres), D(origin[2] * FeetToMetres)));
        string axisArguments = Math.Abs(axis[2] - 1) <= OthersCurrentZero && Math.Abs(x[0] - 1) <= OthersCurrentZero
            ? $"{R(originId)},$,$"
            : $"{R(originId)},{R(OthersCurrentDirection3(w, axis))},{R(OthersCurrentDirection3(w, x))}";
        int placementId = w.Add("IFCAXIS2PLACEMENT3D", axisArguments);
        int directionId = OthersCurrentDirection3(w, [0, 0, 1]);
        return w.Add("IFCEXTRUDEDAREASOLID",
            $"{R(sweptArea)},{R(placementId)},{R(directionId)},{D(extrusion.LengthFt * FeetToMetres)}");
    }

    private int OthersCurrentDirection3(StepWriter w, double[] direction) =>
        w.Add("IFCDIRECTION", StepWriter.List(D(OthersCurrentClean(direction[0])), D(OthersCurrentClean(direction[1])), D(OthersCurrentClean(direction[2]))));

    private static double[] OthersCurrentAdd(double[] p, double[] offset) => [p[0] + offset[0], p[1] + offset[1], p[2] + offset[2]];
}
