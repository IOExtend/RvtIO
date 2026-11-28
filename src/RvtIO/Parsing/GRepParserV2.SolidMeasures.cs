using RvtIO.Geometry.Meshing;

namespace RvtIO.Parsing;

internal static partial class GRepParserV2
{
    private static (double Volume, double Area, int Count)? MeasureClosedSourceBodies(Cur c, HashSet<uint> faces,
        Dictionary<uint, SurfaceRec> surfaces, Dictionary<uint, List<int>> loopsByFace, bool volumeOnly = false, bool rawCoedges = false)
    {
        var nodeByFace = ResolveGeometryNodes(c.Queue, c.QueueLinks, c.ObjectGInfo);
        if (faces.Any(face => !nodeByFace.ContainsKey(face))) return null;
        var selected = faces.ToDictionary(face => face, face => nodeByFace[face]);
        var closed = ClosedNodes(ContourEdges(c), selected);
        var edges = EdgesByHandle(c);
        double volume = 0, area = 0;
        int count = 0;
        foreach (var group in c.FaceOrder.Where(faces.Contains).GroupBy(face => nodeByFace[face].Node))
        {
            if (!closed.Contains(group.Key)) return null;
            var solid = MeasureAnalyticSolid(c, group, surfaces, loopsByFace, edges, true, rawCoedges);
            if (solid == null && !rawCoedges)
                solid = MeasureEllipticalExtrusion(c, group, surfaces, loopsByFace, edges, volumeOnly);
            if (solid is not { Volume: > 0 } body) return null;
            volume += body.Volume;
            if (!volumeOnly) area += body.Area;
            count++;
        }
        return count > 0 && double.IsFinite(volume) && double.IsFinite(area) ? (volume, area, count) : null;
    }

    // Edges with neither next link are detached from the saved contour after a cut.
    private static IEnumerable<(uint FaceA, uint FaceB)> ContourEdges(Cur c)
        => c.Edges.Where(edge => edge.NextA != 0 || edge.NextB != 0).Select(edge => (edge.FaceA, edge.FaceB));

    private static (double Area, double Volume)? MeasureTrimmedRuledFace(Cur c, SurfaceRec surface, Pt3 reference,
        IReadOnlyList<FaceArea.Coedge> coedges)
    {
        var start = RuledProfile(c, surface.ProfileHandle, surface.P1);
        var end = RuledProfile(c, surface.ProfileHandle2, surface.P2);
        if (start == null || end == null) return null;
        double anchor = surface.ProfileHandle2 is 0 or uint.MaxValue ? 1 : 0;
        var area = MeasureRuledArea(start, end, coedges);
        var volume = FaceArea.Integrate(coedges, (u, v, du, _) =>
        {
            if (du == 0 || v == anchor) return 0;
            var (a, da) = start(u);
            var (b, db) = end(u);
            var ruling = b - a;
            var normal = Pt3.Cross(da, ruling);
            var change = Pt3.Cross(db - da, ruling);
            return -(a - reference).Dot(normal + (v + anchor) / 2 * change) * (v - anchor) * du / 3;
        });
        return area is double surfaceArea && volume is double signedVolume ? (surfaceArea, signedVolume) : null;
    }

    private static Func<double, (Pt3 Point, Pt3 Tangent)>? RuledProfile(Cur c, uint handle, double[]? pole)
    {
        if (handle is 0 or uint.MaxValue)
            return pole is { Length: 3 } && Pt3.From(pole).IsFinite ? _ => (Pt3.From(pole), default) : null;
        if (!c.Curves.TryGetValue(handle, out var profile) || profile.NurbControlPoints is { Count: > 0 }
            || !(profile.T1 > profile.T0)) return null;
        double interval = profile.T1 - profile.T0;
        if (profile.HermiteNodes is { Count: > 0 } nodes)
        {
            if (!ValidHermiteProfile(nodes)) return null;
            return u =>
            {
                var (p, d) = EvaluateHermiteProfile(nodes, profile.T0 + u * interval);
                return (p, interval * d);
            };
        }
        var center = Pt3.From(profile.C);
        var x = Pt3.From(profile.X);
        var y = Pt3.From(profile.Y);
        if (!center.IsFinite || !x.IsFinite || !y.IsFinite
            || !double.IsFinite(profile.R) || !double.IsFinite(profile.RY) || profile.R < 0 || profile.RY < 0) return null;
        if (profile.R == 0) return u => (center + (profile.T0 + u * interval) * x, interval * x);
        x *= profile.R;
        y *= profile.RY == 0 ? profile.R : profile.RY;
        return u =>
        {
            var (sin, cos) = Math.SinCos(profile.T0 + u * interval);
            return (center + cos * x + sin * y, interval * (cos * y - sin * x));
        };
    }

    private static double? MeasureRuledArea(Func<double, (Pt3 Point, Pt3 Tangent)> start,
        Func<double, (Pt3 Point, Pt3 Tangent)> end, IReadOnlyList<FaceArea.Coedge> coedges)
    {
        var area = FaceArea.Integrate(coedges, (u, v, du, _) =>
        {
            if (du == 0 || v == 0) return 0;
            var (a, da) = start(u);
            var (b, db) = end(u);
            var ruling = b - a;
            var change = Pt3.Cross(db - da, ruling);
            var normal = Pt3.Cross(da, ruling);
            double length = change.Norm;
            if (length == 0 || length <= 1e-8 * normal.Norm)
                return -(normal + v / 2 * change).Norm * v * du;
            double projection = normal.Dot(change) / length;
            double perpendicular = Pt3.Cross(normal, change).Norm / length;
            double Primitive(double x) => perpendicular == 0 ? x * Math.Abs(x)
                : x * double.Hypot(x, perpendicular) + perpendicular * perpendicular * Math.Asinh(x / perpendicular);
            return -(Primitive(projection + v * length) - Primitive(projection)) / (2 * length) * du;
        });
        return area is double value ? Math.Abs(value) : null;
    }

    private static (double Area, double Volume)? MeasureEllipticalExtrusion(Cur c, IEnumerable<uint> handles,
        Dictionary<uint, SurfaceRec> surfaces, Dictionary<uint, List<int>> loopsByFace, Dictionary<uint, EdgeRec?> edges,
        bool volumeOnly)
    {
        const double tolerance = 1e-9;
        var faces = handles.ToList();
        var extrusions = new Dictionary<uint, (ArcRec Profile, Pt3 Ruling)>();
        foreach (uint face in faces)
        {
            if (!surfaces.TryGetValue(face, out var surface)) return null;
            if (surface.Kind != SurfKind.Ruled) continue;
            if (!c.Curves.TryGetValue(surface.ProfileHandle, out var a) || !c.Curves.TryGetValue(surface.ProfileHandle2, out var b)
                || a.HermiteNodes != null || a.NurbControlPoints != null || b.HermiteNodes != null || b.NurbControlPoints != null
                || a.R <= 0 || a.RY <= 0 || !(a.T1 > a.T0) || Math.Abs(a.R - b.R) > tolerance || Math.Abs(a.RY - b.RY) > tolerance
                || Math.Abs(a.T0 - b.T0) > tolerance || Math.Abs(a.T1 - b.T1) > tolerance
                || (Pt3.From(a.X) - Pt3.From(b.X)).Norm > tolerance || (Pt3.From(a.Y) - Pt3.From(b.Y)).Norm > tolerance)
                return null;
            extrusions.Add(face, (a, Pt3.From(b.C) - Pt3.From(a.C)));
        }
        if (extrusions.Count == 0) return null;
        var ruling = extrusions.Values.First().Ruling;
        double height = ruling.Norm;
        if (!double.IsFinite(height) || height <= tolerance) return null;
        var axis = ruling / height;
        if (extrusions.Values.Any(value => (value.Ruling - ruling).Norm > tolerance && (value.Ruling + ruling).Norm > tolerance
            || Math.Abs(axis.Dot(Pt3.From(value.Profile.X))) > tolerance || Math.Abs(axis.Dot(Pt3.From(value.Profile.Y))) > tolerance))
            return null;

        var caps = new List<(double Offset, double SignedArea, double SurfaceArea, double Perimeter)>();
        foreach (uint face in faces)
        {
            var surface = surfaces[face];
            if (surface.Kind == SurfKind.Ruled) continue;
            if (surface.Kind == SurfKind.Cylinder && Math.Abs(Math.Abs(axis.Dot(Pt3.From(surface.Z))) - 1) <= tolerance) continue;
            if (surface.Kind != SurfKind.Plane) return null;
            var x = Pt3.From(surface.X);
            var y = Pt3.From(surface.Y);
            var normal = Pt3.Cross(x, y);
            double direction = Math.Abs(axis.Dot(normal));
            if (direction <= tolerance) continue;
            if (Math.Abs(direction - 1) > tolerance || !loopsByFace.TryGetValue(face, out var loops)
                || SurfaceCoedges(c, face, loops, edges) is not { Count: > 0 } coedges) return null;

            double signedArea = 0, perimeter = 0;
            foreach (var coedge in coedges)
            {
                var (u0, v0) = coedge.Points[0];
                var (u1, v1) = coedge.Points[^1];
                var edge = edges[coedge.SourceEdge]!;
                uint neighbor = coedge.SecondFace ? edge.FaceA : edge.FaceB;
                if (!surfaces.TryGetValue(neighbor, out var side)) return null;
                var uv = edge.AuthoredPoints().Select(point => coedge.SecondFace ? (point.UA, point.VA) : (point.UB, point.VB)).ToList();
                double segment = 0, length = Math.Sqrt((u1 - u0) * (u1 - u0) + (v1 - v0) * (v1 - v0));
                if (side.Kind == SurfKind.Cylinder || extrusions.ContainsKey(neighbor))
                {
                    double sweep = uv[^1].Item1 - uv[0].Item1;
                    if (uv.Any(point => Math.Abs(point.Item2 - uv[0].Item2) > tolerance)) return null;
                    double orientedRadii = side.R * side.R * normal.Dot(Pt3.Cross(Pt3.From(side.X), Pt3.From(side.Y)));
                    length = Math.Abs(sweep * side.R);
                    if (extrusions.TryGetValue(neighbor, out var ellipse))
                    {
                        var p = ellipse.Profile;
                        double interval = p.T1 - p.T0;
                        sweep *= interval;
                        orientedRadii = p.R * p.RY * normal.Dot(Pt3.Cross(Pt3.From(p.X), Pt3.From(p.Y)));
                        if (!volumeOnly)
                        {
                            if (FaceArea.EllipticalArcLength(p.R, p.RY, p.T0 + uv[0].Item1 * interval,
                                    p.T0 + uv[^1].Item1 * interval) is not double arcLength) return null;
                            length = arcLength;
                        }
                    }
                    // Green's formula for the segment between a conic arc and its chord.
                    segment = orientedRadii * (sweep - Math.Sin(sweep));
                }
                else if (side.Kind != SurfKind.Plane || coedge.Points.Count != 2) return null;
                signedArea += (coedge.Reversed ? -1 : 1) * (u0 * v1 - v0 * u1 + segment) / 2;
                perimeter += length;
            }
            // Volume uses the geometric cross-section. Revit reports cap area by quadrature over the distinct UV samples.
            double? area = volumeOnly ? 0 : FaceArea.ComputeWithDistinctSamples(coedges);
            if (area is not double capArea || !double.IsFinite(signedArea)) return null;
            caps.Add((axis.Dot(Pt3.From(surface.O)), signedArea * axis.Dot(normal) * (surface.Orient == 0 ? -1 : 1),
                capArea, perimeter));
        }
        if (caps is not [var first, var second] || first.SignedArea * second.SignedArea >= 0
            || Math.Abs(first.SignedArea + second.SignedArea) > tolerance
            || (!volumeOnly && Math.Abs(first.Perimeter - second.Perimeter) > tolerance)
            || Math.Abs(Math.Abs(first.Offset - second.Offset) - height) > tolerance) return null;
        double volume = (first.Offset > second.Offset ? first.SignedArea : second.SignedArea) * height;
        double surfaceArea = volumeOnly ? 0 : first.SurfaceArea + second.SurfaceArea + height * first.Perimeter;
        return double.IsFinite(surfaceArea) && double.IsFinite(volume) ? (surfaceArea, volume) : null;
    }

    private static (double Top, double Bottom)? MeasureHorizontalCaps(Cur c, HashSet<uint> faces,
        Dictionary<uint, SurfaceRec> surfaces, Dictionary<uint, List<int>> loopsByFace)
    {
        double top = 0, bottom = 0;
        var edges = EdgesByHandle(c);
        foreach (uint face in faces)
        {
            if (!surfaces.TryGetValue(face, out var surface) || surface.Kind != SurfKind.Plane) continue;
            double normal = MeshCross(surface.X, surface.Y)[2] * (MeshFlipped(c, face, surface) ? -1 : 1);
            if (Math.Abs(normal) < 1 - 1e-9) continue;
            if (!loopsByFace.TryGetValue(face, out var loops) || SurfaceCoedges(c, face, loops, edges) is not { } coedges
                || FaceArea.Compute(coedges) is not double area) return null;
            if (normal > 0) top += area;
            else bottom += area;
        }
        return double.IsFinite(top) && double.IsFinite(bottom) ? (top, bottom) : null;
    }

    /// <summary>Area and SIGNED volume (negative when the faces point into the body, as the ones of a cutting body do) of a Geometry node
    /// made of planes, cylinders, cones and supported surfaces of revolution, ruled surfaces or Hermite surfaces.</summary>
    // shortcut: unsupported surface kinds retain polygon quantities until their surface integrals are decoded.
    private static (double Area, double Volume)? MeasureAnalyticSolid(Cur c, IEnumerable<uint> handles,
        Dictionary<uint, SurfaceRec> surfaces, Dictionary<uint, List<int>> loopsByFace,
        Dictionary<uint, EdgeRec?> edges, bool measureRevolved, bool rawCoedges = false)
    {
        var faces = handles.ToList();
        if (faces.Count == 0 || !surfaces.TryGetValue(faces[0], out var first)
            || first.Env is not { Length: 4 } envelope
            || MeshEvaluate(c, faces[0], first, (envelope[0] + envelope[2]) / 2,
                (envelope[1] + envelope[3]) / 2) is not { } reference)
            return null;

        double area = 0, volume = 0;
        foreach (uint handle in faces)
        {
            if (!surfaces.TryGetValue(handle, out var surface)
                || !loopsByFace.TryGetValue(handle, out var loops)
                || SurfaceCoedges(c, handle, loops, edges, rawOnly: rawCoedges,
                    closeEndPole: rawCoedges && surface.Kind == SurfKind.Ruled && surface.P2 != null
                        && surface.ProfileHandle2 is 0 or uint.MaxValue) is not { Count: > 0 } coedges)
                return null;

            var result = surface.Kind switch
            {
                SurfKind.Rev when measureRevolved && c.Curves.TryGetValue(surface.ProfileHandle, out var profile)
                    => MeasureRevolvedFace(surface, profile, reference, coedges),
                SurfKind.Ruled when rawCoedges => MeasureTrimmedRuledFace(c, surface, Pt3.From(reference), coedges),
                SurfKind.Ruled when measureRevolved => MeasureRuledFace(c, surface, reference, coedges),
                SurfKind.Hermite when rawCoedges && loops.Count == 1 => MeasureHermiteFace(surface, reference, coedges),
                _ => MeasureAnalyticFace(surface, reference, coedges),
            };
            if (result is not { } measure || rawCoedges && measure.Area <= 0) return null;
            double a = measure.Area, v = measure.Volume;
            area += a;
            volume += v;
        }
        return double.IsFinite(area) && double.IsFinite(volume) ? (area, volume) : null;
    }
    /// <summary>The Geometry nodes that are closed shells: every serialized Edge of a Face of the node joins two Faces of that node, so the
    /// signed volume of its faces means something (an open sheet has none).</summary>
    internal static HashSet<int> ClosedNodes(IEnumerable<(uint FaceA, uint FaceB)> edges, IReadOnlyDictionary<uint, (int Node, int Style)> nodeByFace)
    {
        static bool Valid(uint handle) => handle != 0 && handle != 0xFFFFFFFF;
        var touched = new HashSet<int>();
        var open = new HashSet<int>();
        foreach (var edge in edges)
        {
            (int Node, int Style) ownerA = default, ownerB = default;
            bool a = Valid(edge.FaceA) && nodeByFace.TryGetValue(edge.FaceA, out ownerA);
            bool b = Valid(edge.FaceB) && nodeByFace.TryGetValue(edge.FaceB, out ownerB);
            int nodeA = ownerA.Node, nodeB = ownerB.Node;
            if (a) touched.Add(nodeA);
            if (b) touched.Add(nodeB);
            if (a && b && nodeA == nodeB) continue;
            if (a) open.Add(nodeA);
            if (b) open.Add(nodeB);
        }
        touched.ExceptWith(open);
        return touched;
    }

    internal static (double Area, double Volume)? MeasureAnalyticFace(SurfaceRec surface, double[] reference,
        IReadOnlyList<FaceArea.Coedge> coedges)
    {
        if (surface.Kind is not (SurfKind.Plane or SurfKind.Cylinder or SurfKind.Cone)
            || reference.Length != 3 || coedges.Count == 0) return null;
        double[] delta = [reference[0] - surface.O[0], reference[1] - surface.O[1], reference[2] - surface.O[2]];
        double x = Dot3(delta, surface.X), y = Dot3(delta, surface.Y), z = Dot3(delta, surface.Z);
        double normal = Dot3(delta, MeshCross(surface.X, surface.Y)) * (surface.Orient == 0 ? -1 : 1);
        double handedness = Dot3(surface.X, MeshCross(surface.Y, surface.Z)) < 0 ? -1 : 1;
        double radius = surface.R, sine = Math.Sin(radius), cosine = Math.Cos(radius);

        double? faceArea = surface.Kind switch
        {
            SurfKind.Plane => FaceArea.Compute(coedges),
            SurfKind.Cylinder => FaceArea.Compute(coedges) * radius,
            _ => FaceArea.Integrate(coedges, (u, v, du, dv) => sine * u * v * dv)
                is { } coneArea ? Math.Abs(coneArea) : null,
        };
        double? faceVolume = FaceArea.Integrate(coedges, (u, v, du, dv) => surface.Kind switch
        {
            SurfKind.Plane => normal * v / 3 * du,
            SurfKind.Cylinder => handedness * ((Math.Sin(u) * y + Math.Cos(u) * x - radius) * v) * radius / 3 * du,
            _ => handedness * ((Math.Cos(u) * y - Math.Sin(u) * x) * cosine * sine + sine * sine * z * u) * v / 3 * dv,
        });
        return faceArea is { } a && faceVolume is { } v && double.IsFinite(a) && a >= 0 ? (a, v) : null;
    }

    internal static (double Area, double Volume)? MeasureRevolvedFace(SurfaceRec surface, ArcRec profile, double[] reference,
        IReadOnlyList<FaceArea.Coedge> coedges)
    {
        if (surface.Kind != SurfKind.Rev || surface.Env is not { Length: 4 } env || reference.Length != 3
            || coedges.Count == 0 || MeridionalProfile(profile, env[1], env[3]) is not { } evaluate) return null;
        bool circle = profile.R > 0 && MeshProfileIsLineOrCircle(profile);
        double[] delta = [reference[0] - surface.O[0], reference[1] - surface.O[1], reference[2] - surface.O[2]];
        double x = Dot3(delta, surface.X), y = Dot3(delta, surface.Y), z = Dot3(delta, surface.Z);
        double handedness = Dot3(surface.X, MeshCross(surface.Y, surface.Z)) < 0 ? -1 : 1;

        // Integrate the area and the flux of (point - reference)/3 over the trimmed UV region via its coedges.
        var area = FaceArea.Integrate(coedges, (u, v, _, dv) =>
        {
            if (dv == 0) return 0;
            var (point, tangent) = evaluate(v);
            double speed = circle ? profile.R : Math.Sqrt(tangent.X * tangent.X + tangent.Z * tangent.Z);
            return u * point.X * speed * dv;
        });
        var volume = FaceArea.Integrate(coedges, (u, v, _, dv) =>
        {
            if (dv == 0) return 0;
            var (point, tangent) = evaluate(v);
            return handedness * point.X / 3 * ((point.X * tangent.Z - (point.Z - z) * tangent.X) * u
                - tangent.Z * (x * Math.Sin(u) - y * Math.Cos(u))) * dv;
        });
        return area is { } a && volume is { } b ? (Math.Abs(a), b) : null;
    }

    private static Func<double, (Pt3 Point, Pt3 Tangent)>? MeridionalProfile(ArcRec profile, double from, double to)
    {
        const double tolerance = 1e-9;
        if (!double.IsFinite(from) || !double.IsFinite(to) || from > to || profile.NurbControlPoints is { Count: > 0 })
            return null;
        if (profile.HermiteNodes is { Count: > 0 } nodes)
        {
            if (!ValidHermiteProfile(nodes) || from < nodes[0].T - tolerance || to > nodes[^1].T + tolerance
                || nodes.Any(node => Math.Abs(node.Point[1]) > tolerance || Math.Abs(node.Tangent[1]) > tolerance)) return null;
            for (int i = 1; i < nodes.Count; i++)
            {
                var a = nodes[i - 1];
                var b = nodes[i];
                double dt = b.T - a.T;
                // The Bernstein control radii bound each cubic span on the positive side of the axis.
                if (Math.Min(a.Point[0], b.Point[0]) < -tolerance
                    || Math.Min(a.Point[0] + dt * a.Tangent[0] / 3, b.Point[0] - dt * b.Tangent[0] / 3) < -tolerance)
                    return null;
            }
            return v => EvaluateHermiteProfile(nodes, v);
        }

        var center = Pt3.From(profile.C);
        var x = Pt3.From(profile.X);
        var y = Pt3.From(profile.Y);
        if (!center.IsFinite || !x.IsFinite || !y.IsFinite || Math.Abs(x.Y) > tolerance || Math.Abs(y.Y) > tolerance
            || !double.IsFinite(profile.R) || !double.IsFinite(profile.RY) || profile.R < 0 || profile.RY < 0) return null;
        if (profile.R == 0)
            return Math.Min(center.X + from * x.X, center.X + to * x.X) < -tolerance ? null : v => (center + v * x, x);
        bool circle = MeshProfileIsLineOrCircle(profile);
        var radialX = profile.R * x;
        var radialY = (circle ? profile.R : profile.RY) * y;
        double Radius(double v) => center.X + Math.Cos(v) * radialX.X + Math.Sin(v) * radialY.X;
        double trough = Math.Atan2(radialY.X, radialX.X) + Math.PI;
        trough += Math.Ceiling((from - trough) / (2 * Math.PI)) * (2 * Math.PI);
        double minimum = Math.Min(Radius(from), Radius(to));
        if (trough < to) minimum = Math.Min(minimum, Radius(trough));
        if (!double.IsFinite(minimum) || minimum < -tolerance) return null;
        if (circle)
            return v =>
            {
                double cos = Math.Cos(v), sin = Math.Sin(v);
                return (center + profile.R * (cos * x + sin * y), profile.R * (-sin * x + cos * y));
            };
        return v =>
        {
            var (sin, cos) = Math.SinCos(v);
            return (center + cos * radialX + sin * radialY, cos * radialY - sin * radialX);
        };
    }

    private static bool ValidHermiteProfile(IReadOnlyList<HermiteNode> nodes)
        => nodes.Count >= 2 && nodes.All(node => double.IsFinite(node.T) && Pt3.From(node.Point).IsFinite && Pt3.From(node.Tangent).IsFinite)
            && nodes.Zip(nodes.Skip(1)).All(pair => pair.First.T < pair.Second.T);

    private static (Pt3 Point, Pt3 Tangent) EvaluateHermiteProfile(IReadOnlyList<HermiteNode> nodes, double v)
    {
        int i = 1;
        while (i < nodes.Count - 1 && v > nodes[i].T) i++;
        var a = nodes[i - 1];
        var b = nodes[i];
        double dt = b.T - a.T;
        Span<double> weights = stackalloc double[4];
        Span<double> derivatives = stackalloc double[4];
        FaceArea.HermiteBasis((v - a.T) / dt, weights, derivatives);
        Pt3 pa = Pt3.From(a.Point), pb = Pt3.From(b.Point);
        Pt3 ta = dt * Pt3.From(a.Tangent), tb = dt * Pt3.From(b.Tangent);
        return (weights[0] * pa + weights[1] * pb + weights[2] * ta + weights[3] * tb,
            (derivatives[0] * pa + derivatives[1] * pb + derivatives[2] * ta + derivatives[3] * tb) / dt);
    }

    private static (double Area, double Volume)? MeasureRuledFace(Cur c, SurfaceRec surface, double[] reference,
        IReadOnlyList<FaceArea.Coedge> coedges)
    {
        bool firstIsApex = surface.ProfileHandle is 0 or uint.MaxValue;
        bool secondIsApex = surface.ProfileHandle2 is 0 or uint.MaxValue;
        if (!firstIsApex && !secondIsApex
            && c.Curves.TryGetValue(surface.ProfileHandle, out var first)
            && c.Curves.TryGetValue(surface.ProfileHandle2, out var second))
            return MeasureRuledProfiles(first, second, Pt3.From(reference), coedges);
        if (firstIsApex == secondIsApex || (firstIsApex ? surface.P1 : surface.P2) is not { Length: 3 } apex
            || !c.Curves.TryGetValue(firstIsApex ? surface.ProfileHandle2 : surface.ProfileHandle, out var profile)) return null;
        return MeasureRuledApex(profile, Pt3.From(apex), secondIsApex, Pt3.From(reference), coedges);
    }

    internal static (double Area, double Volume)? MeasureRuledProfiles(ArcRec first, ArcRec second, Pt3 reference,
        IReadOnlyList<FaceArea.Coedge> coedges)
    {
        const double tolerance = 1e-9;
        if (first.R <= 0 || second.R <= 0 || !MeshProfileIsLineOrCircle(first) || !MeshProfileIsLineOrCircle(second)
            || !(first.T1 > first.T0) || Math.Abs(first.T0 - second.T0) > tolerance || Math.Abs(first.T1 - second.T1) > tolerance
            || (Pt3.From(first.X) - Pt3.From(second.X)).Norm > tolerance
            || (Pt3.From(first.Y) - Pt3.From(second.Y)).Norm > tolerance
            || coedges.Count != 4) return null;

        int sides = 0;
        foreach (var edge in coedges)
        {
            if (edge.Points.Count < 2) return null;
            var (u0, v0) = edge.Points[0];
            var (u1, v1) = edge.Points[^1];
            if (Math.Abs(u0 - u1) <= tolerance && Math.Abs(Math.Abs(v0 - v1) - 1) <= tolerance
                && (Math.Abs(u0) <= tolerance || Math.Abs(u0 - 1) <= tolerance)
                && edge.Points.All(point => Math.Abs(point.U - u0) <= tolerance))
                sides |= Math.Abs(u0) <= tolerance ? 1 : 2;
            else if (Math.Abs(v0 - v1) <= tolerance && Math.Abs(Math.Abs(u0 - u1) - 1) <= tolerance
                && (Math.Abs(v0) <= tolerance || Math.Abs(v0 - 1) <= tolerance)
                && edge.Points.All(point => Math.Abs(point.V - v0) <= tolerance))
                sides |= Math.Abs(v0) <= tolerance ? 4 : 8;
        }
        if (sides != 15) return null;

        static Func<double, (Pt3 Point, Pt3 Tangent)> Profile(ArcRec curve)
        {
            var center = Pt3.From(curve.C);
            var x = curve.R * Pt3.From(curve.X);
            var y = curve.R * Pt3.From(curve.Y);
            double interval = curve.T1 - curve.T0;
            return u =>
            {
                var (sin, cos) = Math.SinCos(curve.T0 + u * interval);
                return (center + cos * x + sin * y, interval * (cos * y - sin * x));
            };
        }

        var a = Profile(first);
        var b = Profile(second);
        (Pt3 Point, Pt3 Normal, Pt3 Change) Section(double u)
        {
            var (point, tangent) = a(u);
            var (end, endTangent) = b(u);
            var ruling = end - point;
            return (point, Pt3.Cross(tangent, ruling), Pt3.Cross(endTangent - tangent, ruling));
        }

        var area = MeasureRuledArea(a, b, coedges);
        var volume = FaceArea.Integrate(coedges, (u, v, du, _) =>
        {
            var (point, normal, change) = Section(u);
            return -(point - reference).Dot(normal + v / 2 * change) * v * du / 3;
        });
        return area is { } surface && volume is { } solid ? (surface, solid) : null;
    }

    internal static (double Area, double Volume)? MeasureRuledApex(ArcRec profile, Pt3 apex, bool apexAtEnd, Pt3 reference,
        IReadOnlyList<FaceArea.Coedge> coedges)
    {
        if (!MeshProfileIsLineOrCircle(profile) || !(profile.T1 > profile.T0)) return null;
        var center = Pt3.From(profile.C);
        var x = Pt3.From(profile.X);
        var y = Pt3.From(profile.Y);
        double interval = profile.T1 - profile.T0;
        double collapsedV = apexAtEnd ? 1 : 0;
        Pt3 Normal(double u)
        {
            double t = profile.T0 + u * interval;
            if (profile.R == 0) return Pt3.Cross(interval * x, center + t * x - apex);
            var (sin, cos) = Math.SinCos(t);
            var point = center + profile.R * (cos * x + sin * y);
            return Pt3.Cross((profile.R * interval) * (cos * y - sin * x), point - apex);
        }

        // A collapsed edge has no authored coedge, so integrate from that edge to make its contribution zero.
        double Primitive(double v) => (v - collapsedV) * (v - collapsedV);
        double? area = FaceArea.Integrate(coedges, (u, v, du, _) => Normal(u).Norm * Primitive(v) * du / 2);
        double? volume = FaceArea.Integrate(coedges, (u, v, du, _) => -(apex - reference).Dot(Normal(u)) * Primitive(v) * du / 6);
        return area is { } a && volume is { } b ? (Math.Abs(a), b) : null;
    }
}
