using RvtIO.Model;

namespace RvtIO.Geometry;

/// <summary>Measures decoded Revit faces, shells and solid nodes.</summary>
internal static class BrepMeasures
{
    private const double FeetToMetres = 0.3048;
    internal const double SquareFeetToSquareMetres = FeetToMetres * FeetToMetres;
    internal const double CubicFeetToCubicMetres = FeetToMetres * FeetToMetres * FeetToMetres;

    /// <summary>One decoded shell: its faces, its volume (the divergence theorem over its planar faces) and the sum of
    /// its face areas (holes subtracted), in feet. Complete analytic nodes use source surface integrals.</summary>
    internal sealed record ShellInfo(IReadOnlyList<GeoFace> Faces, bool Closed, double Volume, double Area)
    {
        public bool SourceMeasured { get; init; }
    }

    /// <summary>The prism a shell is, in feet: Depth is the distance of the two end faces, ProfileArea the net area of an
    /// end face, OuterPerimeter the length of its outer loop and ExtentX/ExtentY the world extents of the loop that is measured
    /// (they describe the profile only for a vertical prism): the first loop of the lower end face as the Face serializes its loops,
    /// which is a hole when the Face lists one first.</summary>
    internal readonly record struct ShellPrism(
        double Depth, double ProfileArea, double OuterPerimeter, double ExtentX, double ExtentY, bool Vertical,
        double InnerPerimeter = 0);

    /// <summary>The total surface area (feet squared) of the solids of a geometry: the sum over its shells, the holes of the faces
    /// subtracted.</summary>
    internal static double SurfaceAreaCurrent(GeometryData? geometry) => ShellsOfCurrent(geometry).Sum(shell => shell.SourceMeasured ? shell.Area :
        shell.Faces.GroupBy(face => (face.GeometryNode, Marker: face.SourceFace != 0 ? face.SourceFace : face.MeshMarker))
            .Sum(group => group.Key.Marker != 0 && group.First().QuadratureArea is { } exact
                ? exact : group.Sum(face => NetFaceArea(face) ?? 0)));

    private static double[] RingNormal(IReadOnlyList<double[]> ring)
    {
        double nx = 0, ny = 0, nz = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            nx += (a[1] - b[1]) * (a[2] + b[2]);
            ny += (a[2] - b[2]) * (a[0] + b[0]);
            nz += (a[0] - b[0]) * (a[1] + b[1]);
        }
        return [nx, ny, nz];
    }

    /// <summary>Area of the polygon of a (planar) ring.</summary>
    internal static double RingArea(IReadOnlyList<double[]> ring)
    {
        if (ring.Count < 3) return 0;
        var n = RingNormal(ring);
        return 0.5 * Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
    }

    internal static double RingPerimeter(IReadOnlyList<double[]> ring)
    {
        double length = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            length += Math.Sqrt((b[0] - a[0]) * (b[0] - a[0]) + (b[1] - a[1]) * (b[1] - a[1]) + (b[2] - a[2]) * (b[2] - a[2]));
        }
        return length;
    }

    /// <summary>Face area with its inner loops subtracted; null when the face has no outer loop.</summary>
    internal static double? NetFaceArea(GeoFace face)
    {
        if (face.OuterVertices is not { Count: >= 3 } outer) return null;
        double area = RingArea(outer);
        if (face.InnerVertices != null)
            foreach (var hole in face.InnerVertices) area -= RingArea(hole);
        return area;
    }

    /// <summary>Unit normal of the outer loop by its winding; null for a degenerate loop.</summary>
    internal static double[]? FaceUnitNormal(GeoFace face)
    {
        if (face.OuterVertices is not { Count: >= 3 } outer) return null;
        var n = RingNormal(outer);
        double length = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
        return length < 1e-12 ? null : [n[0] / length, n[1] / length, n[2] / length];
    }

    /// <summary>The shells of an element geometry (all faces as one shell when none are listed).</summary>
    internal static List<ShellInfo> ShellsOfCurrent(GeometryData? geometry,
        HashSet<(long X, long Y, long Z)>? splitVertices = null)
    {
        var result = new List<ShellInfo>();
        if (geometry?.Faces is not { Count: > 0 } faces) return result;
        IEnumerable<(List<GeoFace> Faces, bool Closed)> shells = geometry.BrepSolids is { Count: > 0 } solids
            ? solids.Select(solid => (solid.FaceIndices.Where(i => (uint)i < (uint)faces.Count).Select(i => faces[i]).ToList(), solid.IsClosed))
            : [(faces, false)];
        foreach (var (shellFaces, closed) in shells)
        {
            double volume = 0, area = 0;
            // The signed volume needs every face wound the same way (outward); a closed shell that orients exactly is summed oriented.
            IReadOnlyList<GeoFace> computationFaces = shellFaces;
            if (closed && shellFaces.Count >= 4
                && FacetedShell.TryOrientExactFacetedShell(shellFaces, Enumerable.Range(0, shellFaces.Count).ToList(),
                    out var orientedShell, triangulateNonPlanarFaces: false, splitVertices))
                computationFaces = orientedShell;
            foreach (var face in computationFaces)
            {
                if (NetFaceArea(face) is not { } net || FaceUnitNormal(face) is not { } normal) continue;
                area += net;
                var p = face.OuterVertices![0];
                volume += (p[0] * normal[0] + p[1] * normal[1] + p[2] * normal[2]) * net / 3;
            }
            bool sourceMeasured = false;
            if (closed && shellFaces.Select(face => face.GeometryNode).Distinct().ToList() is [var nodeId]
                && geometry.NodeMeasures?.Any(measure => measure.Node == nodeId && measure.Volume < 0) != true  // a cutting node has no solid to measure
                && geometry.MeshNodes?.FirstOrDefault(node => node.Node == nodeId) is { SourceMeasures: { } measures } source
                && shellFaces.Select(face => face.SourceFace != 0 ? face.SourceFace : face.MeshMarker).ToHashSet()
                    .SetEquals(source.Faces.Select(face => (uint)face.Marker)))
            {
                area = measures.Area;
                volume = measures.Volume;
                sourceMeasured = true;
            }
            result.Add(new ShellInfo(shellFaces, closed, Math.Abs(volume), area) { SourceMeasured = sourceMeasured });
        }
        return result;
    }

    /// <summary>Orients the complete planar shell of a single solid with a stored measure.</summary>
    internal static ShellInfo? MeasuredPlanarShell(GeometryData? geometry, out double geometricVolume)
    {
        geometricVolume = 0;
        if (geometry is not { UntriangulatedFaces: 0, BrepSolids: [{ IsClosed: true } solid],
                NodeMeasures: [{ Node: > 0, Volume: > 0 } measure] }
            || !double.IsFinite(measure.Volume)
            || solid.FaceIndices.Count != geometry.Faces.Count
            || geometry.Faces.Any(face => face.CurvedSurface || face.GeometryNode != measure.Node)
            || !FacetedShell.TryOrientExactFacetedShell(geometry.Faces, solid.FaceIndices,
                out var faces, out geometricVolume, triangulateNonPlanarFaces: false))
            return null;
        return new ShellInfo(faces, Closed: true, measure.Volume, measure.Area);
    }

    /// <summary>Measures a single planar extrusion whose closed faces and stored node volume agree.</summary>
    internal static (ShellPrism Prism, double Volume)? MeasuredPlanarPrism(GeometryData? geometry, bool wallSides = false)
    {
        if (MeasuredPlanarShell(geometry, out _) is not { } shell
            || FindPrismCurrent(shell, verticalOnly: !wallSides, wallSides: wallSides) is not { ProfileArea: > 0 } prism
            || !(Math.Abs(prism.ProfileArea * prism.Depth - shell.Volume) <= shell.Volume * 1e-6))
            return null;
        return (prism, shell.Volume);
    }

    /// <summary>The prism of a shell: two planar faces of equal net area (within 1e-4 ft2) facing each other on
    /// different planes, with every other face parallel to their common normal. Null for any other shape, a curved face
    /// included.
    /// With <paramref name="arcSides"/> the vertical cylinders that tessellate the arcs of the outline are allowed as sides and left
    /// out of the search (the perimeters stay those of the chords), and a shell with any other curved face is no prism.</summary>
    /// <remarks>For a straight wall, the largest pair of vertical end faces bounds its thickness.</remarks>
    internal static ShellPrism? FindPrismCurrent(ShellInfo shell, bool verticalOnly = false, bool arcSides = false,
        bool wallSides = false)
    {
        var faces = shell.Faces;
        if (faces.Count < 4) return null;
        if (arcSides)
        {
            var cylinders = faces.Where(face => face.CurvedSurface).ToList();
            if (cylinders.Any(face => face.AnalyticCylinderAxis is not { Length: >= 3 } axis || Math.Abs(Math.Abs(axis[2]) - 1) > 1e-9
                || !(face.AnalyticCylinderRadius > 0))) return null;
            faces = faces.Where(face => !face.CurvedSurface).ToList();
        }
        int count = faces.Count;
        if (count < 2 || faces.Any(face => face.CurvedSurface)) return null;
        var normals = new double[count][];
        var areas = new double[count];
        for (int i = 0; i < count; i++)
        {
            if (FaceUnitNormal(faces[i]) is not { } n || NetFaceArea(faces[i]) is not { } a) return null;
            normals[i] = n;
            areas[i] = a;
        }
        ShellPrism? best = null;
        for (int i = 0; i < count; i++)
        {
            for (int j = i + 1; j < count; j++)
            {
                var a = normals[i];
                var b = normals[j];
                if (a[0] * b[0] + a[1] * b[1] + a[2] * b[2] > -1 + 1e-9 || Math.Abs(areas[i] - areas[j]) > 1e-4) continue;
                bool vertical = Math.Abs(a[2]) > 1 - 1e-9;
                if (verticalOnly && !vertical || wallSides && Math.Abs(a[2]) > 1e-9) continue;
                bool others = true;
                for (int k = 0; k < count && others; k++)
                    if (k != i && k != j)
                        others = Math.Abs(normals[k][0] * a[0] + normals[k][1] * a[1] + normals[k][2] * a[2]) < 1e-6;
                if (!others) continue;
                var pi = faces[i].OuterVertices![0];
                var pj = faces[j].OuterVertices![0];
                double depth = Math.Abs((pj[0] - pi[0]) * a[0] + (pj[1] - pi[1]) * a[1] + (pj[2] - pi[2]) * a[2]);
                // A box is a prism along each of its three axes; its long axis is the one a bar is extruded along.
                if (depth < 1e-9 || best is { } known && (wallSides ? known.ProfileArea >= areas[i] : known.Depth >= depth)) continue;
                var outer = faces[i].OuterVertices!;
                var lower = vertical && MeanHeightCurrent(faces[j]) < MeanHeightCurrent(faces[i]) ? faces[j] : faces[i];
                var measured = lower.FirstSourceLoop ?? lower.OuterVertices!;
                best = new ShellPrism(depth, areas[i], RingPerimeter(outer),
                    measured.Max(p => p[0]) - measured.Min(p => p[0]), measured.Max(p => p[1]) - measured.Min(p => p[1]), vertical,
                    faces[i].InnerVertices?.Sum(RingPerimeter) ?? 0);
            }
        }
        return best;
    }

    /// <summary>Mean height of the vertices of a face, in feet.</summary>
    internal static double MeanHeightCurrent(GeoFace face) => face.OuterVertices!.Average(point => point[2]);

    /// <summary>Volume (m3) and surface area (m2) of the solid geometry nodes, summed from their surface integrals
    /// (a cutting node, whose measure has a negative volume, is not taken). Null unless every node of the geometry has its integrals.</summary>
    internal static (double Volume, double Area)? NodeSolidTotalsCurrent(GeometryData? geometry)
    {
        if (geometry?.NodeMeasures is not { Count: > 0 } measures || geometry.Faces.Count == 0) return null;
        var byNode = new Dictionary<int, RvtIO.Parsing.MeshNodeMeasure>();
        foreach (var measure in measures) byNode.TryAdd(measure.Node, measure);
        if (geometry.Faces.Any(face => face.GeometryNode > 0 && !byNode.ContainsKey(face.GeometryNode))) return null;
        var taken = byNode.Values.Where(measure => measure.Volume > 0).ToList();
        if (taken.Count == 0) return null;
        return (taken.Sum(measure => measure.Volume) * CubicFeetToCubicMetres, taken.Sum(measure => measure.Area) * SquareFeetToSquareMetres);
    }
}
