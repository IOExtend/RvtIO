using RvtIO.Geometry;
using RvtIO.Model;

namespace RvtIO.Ifc;

public partial class IfcExporter
{
    private const int RoomLowerOffsetParam = -1006925; // ROOM_LOWER_OFFSET

    /// <summary>The Body of a room or area that has no geometry of its own: the outline of
    /// the boundary segments (<see cref="Element.SerializedPlanBoundary"/>) extruded along z by the height of its level, from the elevation of the
    /// level plus the lower offset of the room, the frame of the profile at the origin of the plan. False, with nothing written, without an
    /// outline or when the profile cannot be written.</summary>
    private bool TryWritePlanSpaceBodyCurrent(StepWriter w, Element elem, int subCtxId, double storeyHeightMetres, out int shapeId)
    {
        shapeId = 0;
        if (!CurrentProfile || elem.SerializedPlanBoundary is not { Count: >= 3 } outline || !(storeyHeightMetres > 0)) return false;
        // The placement of the space is the one of its storey, so the lower offset is all that remains of the elevation.
        double lowerOffset = elem.Parameters.FirstOrDefault(parameter => parameter.Id == RoomLowerOffsetParam)?.Value is double offset ? offset : 0;
        var ring = outline.Select(point => new[] { point.X, point.Y, lowerOffset }).ToList();
        var extrusion = new OthersCurrentExtrusion
        {
            Axis = [0, 0, 1], LengthFt = storeyHeightMetres / FeetToMetres, Regions = [(ring, [], null)], FirstSourceLoop = ring,
        };
        var checkpoint = w.CreateCheckpoint();
        int solidId = WriteOthersCurrentSolid(w, extrusion, [], string.Empty, 0, 0, 0, originOverride: [0, 0, lowerOffset]);
        if (solidId <= 0)
        {
            w.Rollback(checkpoint);
            return false;
        }
        int representationId = w.Add("IFCSHAPEREPRESENTATION", $"{R(subCtxId)},{S("Body")},{S("SweptSolid")},{StepWriter.List([R(solidId)])}");
        shapeId = w.Add("IFCPRODUCTDEFINITIONSHAPE", $"$,$,{StepWriter.List([R(representationId)])}");
        return true;
    }

    /// <summary>The Body of a room whose cache is read literally (<see cref="Element.SerializedLiteralSpaceBoundary"/>): the extrusion along z when the
    /// extrusion calculator finds one for the solid, else a planar Brep, one IfcFace per
    /// serialized Face with a bound for every loop, the polygons exactly as the coedges give them. False, with nothing written, when neither can be
    /// written.</summary>
    private bool TryWriteLiteralSpaceBodyCurrent(StepWriter w, GeometryData boundary, int subCtxId, double offZ, out int shapeId)
    {
        shapeId = 0;
        if (!CurrentProfile || boundary.Faces.Count == 0 || boundary.Faces.Any(face => face.SourceLoops is not { Count: > 0 })) return false;
        if (TryCreateOthersCurrentSweptSolid(w, boundary, subCtxId, OthersCurrentAxesZ, string.Empty, out var swept, offZ: offZ))
        {
            shapeId = w.Add("IFCPRODUCTDEFINITIONSHAPE", $"$,$,{StepWriter.List(swept.RepresentationIds.Select(R))}");
            return true;
        }
        var checkpoint = w.CreateCheckpoint();
        var pointId = CreatePointCache(w, null, 0, 0, offZ);
        var faceIds = new List<int>();
        foreach (var face in boundary.Faces)
        {
            var loops = face.SourceLoops!;
            int outer = 0;
            for (int index = 1; index < loops.Count; index++)
                if (!Enumerable.Range(0, index).Any(earlier => GeometryValidation.AllVerticesStrictlyInside(loops[index], loops[earlier]))) outer = index;
            var bounds = new List<int>();
            for (int index = 0; index < loops.Count; index++)
            {
                if (loops[index].Count < 3) continue;
                int loopId = w.Add("IFCPOLYLOOP", StepWriter.List(loops[index].Select(vertex => R(pointId(vertex)))));
                bounds.Add(w.Add(index == outer ? "IFCFACEOUTERBOUND" : "IFCFACEBOUND", $"{R(loopId)},.T."));
            }
            if (bounds.Count > 0) faceIds.Add(w.Add("IFCFACE", StepWriter.List(bounds.Select(R))));
        }
        if (faceIds.Count == 0)
        {
            w.Rollback(checkpoint);
            return false;
        }
        int shellId = w.Add("IFCCLOSEDSHELL", StepWriter.List(faceIds.Select(R)));
        int brepId = w.Add("IFCFACETEDBREP", R(shellId));
        int representationId = w.Add("IFCSHAPEREPRESENTATION", $"{R(subCtxId)},{S("Body")},{S("Brep")},{StepWriter.List([R(brepId)])}");
        shapeId = w.Add("IFCPRODUCTDEFINITIONSHAPE", $"$,$,{StepWriter.List([R(representationId)])}");
        return true;
    }
}
