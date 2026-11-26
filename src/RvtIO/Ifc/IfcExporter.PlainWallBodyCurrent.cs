using RvtIO.Model;

namespace RvtIO.Ifc;

public partial class IfcExporter
{
    /// <summary>Closed pieces that cover both ends of the authored line with a full-height gap between them.</summary>
    private static bool HasSeparatedFullHeightWallPieces(Element wall)
    {
        if (wall.Geometry is not { BrepSolids: { Count: > 1 } solids } geometry
            || wall.Location is not { CurveType: "Line", ArcRadius: null, StartPoint.Length: 3, EndPoint.Length: 3 } axis)
            return false;

        double dx = axis.EndPoint[0] - axis.StartPoint[0], dy = axis.EndPoint[1] - axis.StartPoint[1];
        double length = Math.Sqrt(dx * dx + dy * dy);
        const double toleranceFt = IfcDeclaredPrecisionMetres / FeetToMetres;
        if (!double.IsFinite(length) || length <= toleranceFt)
            return false;
        double ux = dx / length, uy = dy / length;
        double Along(double[] point) =>
            (point[0] - axis.StartPoint[0]) * ux + (point[1] - axis.StartPoint[1]) * uy;
        double Across(double[] point) =>
            (point[1] - axis.StartPoint[1]) * ux - (point[0] - axis.StartPoint[0]) * uy;
        var usedFaces = new HashSet<int>();
        var spans = new List<(double Start, double End)>(solids.Count);
        (double Bottom, double Top, double Left, double Right)? section = null;
        foreach (var solid in solids)
        {
            if (!solid.IsClosed
                || !solid.FaceIndices.All(index => (uint)index < geometry.Faces.Count && usedFaces.Add(index)
                    && geometry.Faces[index] is { GeometryNode: >= 0, CurvedSurface: false, MeshArcs: not { Count: > 0 } }
                    && FaceCanEmitAtIfcPrecision(geometry.Faces[index]))
                || !TryOrientExactFacetedShell(geometry.Faces, solid.FaceIndices, out _, triangulateNonPlanarFaces: false))
                return false;
            var points = solid.FaceIndices.SelectMany(index => geometry.Faces[index].OuterVertices!).ToList();
            double bottom = points.Min(point => point[2]), top = points.Max(point => point[2]);
            double left = points.Min(Across), right = points.Max(Across);
            if (top - bottom <= toleranceFt || right - left <= toleranceFt
                || left > toleranceFt || right < -toleranceFt)
                return false;
            if (section is { } first && (Math.Abs(bottom - first.Bottom) > toleranceFt
                || Math.Abs(top - first.Top) > toleranceFt
                || Math.Abs(left - first.Left) > toleranceFt
                || Math.Abs(right - first.Right) > toleranceFt))
                return false;
            section ??= (bottom, top, left, right);
            spans.Add((points.Min(Along), points.Max(Along)));
        }
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        if (usedFaces.Count != geometry.Faces.Count
            || Math.Abs(spans[0].Start) > toleranceFt || Math.Abs(spans[^1].End - length) > toleranceFt)
            return false;
        for (int i = 1; i < spans.Count; i++)
            if (spans[i].Start - spans[i - 1].End <= toleranceFt)
                return false;
        return true;
    }

    /// <summary>The Body of a plain IfcWall as the current profile writes it: the Brep of the decoded closed solid, the cuts of the wall (the
    /// holes and reveals of its doors and windows) included. A curved face belongs to the body only through the face mesh of its
    /// Geometry node (<see cref="WriteTriangleNodes"/>), never through its dense patches. False, with nothing written, when
    /// the geometry holds neither a closed solid nor a meshed Geometry node, a curved face has no mesh or the shell does not come out as a closed Brep.</summary>
    internal bool TryWriteDecodedPlainWallBodyCurrent(StepWriter w, Element elem, int subCtxId, out int shapeId)
    {
        shapeId = 0;
        if (!CurrentProfile || elem.Geometry is not { BrepSolids: { Count: > 0 } solids } geometry
            || !solids.Any(solid => solid.IsClosed) && geometry.MeshNodes is not { Count: > 0 })
            return false;
        var meshedNodes = geometry.MeshNodes?.Select(node => node.Node).ToHashSet() ?? [];
        if (geometry.Faces.Any(face => face.CurvedSurface && !meshedNodes.Contains(face.GeometryNode)))
            return false;
        var checkpoint = w.CreateCheckpoint();
        var decoded = CreateFacetedBrepWithOutcome(w, geometry, subCtxId, preserveValidatedPlanarFaces: true,
            keepClosedComponentsSeparate: HasSeparatedFullHeightWallPieces(elem));
        if (decoded.ShapeId != 0 && decoded.Outcome is { HasSurfaceModel: false, HasClosedBrepOrSweptSolidBody: true })
        {
            shapeId = decoded.ShapeId;
            return true;
        }
        DiscardBodyItems(w, checkpoint);
        return false;
    }

    /// <summary>Takes back what was written since the checkpoint, the origins of its body items included (their ids are written again).</summary>
    private void DiscardBodyItems(StepWriter w, StepWriter.Checkpoint checkpoint)
    {
        foreach (int id in _bodyItemOrigins.Keys.Where(id => id >= checkpoint.NextId).ToList()) _bodyItemOrigins.Remove(id);
        w.Rollback(checkpoint);
    }

    /// <summary>The Body of a room whose boundary holds round walls: one IfcFacetedBrep per Geometry node with the triangles of the face
    /// meshes (<see cref="WriteTriangleNodes"/>). False, with nothing written, unless every node comes out as a triangle set.</summary>
    private bool TryWriteMeshedSpaceBodyCurrent(StepWriter w, GeometryData boundary, int subCtxId, double offZ, out FacetedShapeWriteResult result)
    {
        result = default;
        if (!CurrentProfile || boundary.MeshNodes is not { Count: > 0 } nodes) return false;
        var checkpoint = w.CreateCheckpoint();
        var items = new List<(long Order, int Node, int BrepId)>();
        var written = WriteTriangleNodes(w, boundary, 0, 0, offZ, null, items);
        if (written.Count != nodes.Count || boundary.Faces.Any(face => !written.Contains(face.GeometryNode)))
        {
            DiscardBodyItems(w, checkpoint);
            return false;
        }
        int representationId = w.Add("IFCSHAPEREPRESENTATION",
            $"{R(subCtxId)},{S("Body")},{S("Brep")},{StepWriter.List(items.OrderBy(item => item.Order).ThenBy(item => item.Node).Select(item => R(item.BrepId)))}");
        result = new FacetedShapeWriteResult([representationId],
            new BodyRepresentationWriteOutcome(HasSurfaceModel: false, HasClosedBrepOrSweptSolidBody: true, OpenShellCount: 0, OpenShellFaceCount: 0));
        return true;
    }
}
