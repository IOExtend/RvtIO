using RvtIO.Geometry;
using RvtIO.Model;

namespace RvtIO.Ifc;

/// <summary>
/// The standard-case wall of an arc location curve the way the current profile writes it: the local placement at the start of
/// the arc with X along the tangent there, the Axis as an IfcTrimmedCurve over an IfcCircle, trimmed to the origin and the end
/// point, and the Body as the extrusion of the loop of the lower end face of the body whose circular edges are sampled by the
/// profile sampler.
/// </summary>
public partial class IfcExporter
{
    /// <summary>Rewrites the arc wall of the finished writer. Returns false, leaving the writer as it was, when the wall is not the
    /// extrusion of a face whose circular edges are known or its arc is not horizontal.</summary>
    private static bool TryConvertArcStandardCaseWallCurrent(
        StepWriter w, Dictionary<int, int> indexById, GlobalsCurrent globals, string[] wall, int placementIndex, string[] placement, int frameIndex, int frameId,
        double[] oldOrigin, int axisCurveIndex, int solidIndex, List<int> detached, Element element)
    {
        var location = element.Location;
        if (location is not { CurveType: "Arc", ArcRadius: double radiusFeet, ArcCenter: { Length: 3 } centre,
                ArcXDirection: { Length: 3 } arcX, ArcYDirection: { Length: 3 } arcY }
            || !(location.ArcEndParameter != location.ArcStartParameter))
            return false;
        if (FirstSolidFacesCurrent(element) is not { Count: > 0 } solidFaces
            || WallExtrusion.FindAxisExtrusion(solidFaces, [0, 0, 1], out _) is not { Arcs: { Count: > 0 } } extrusion)
            return false;

        double[] normal = CrossCurrent(arcX, arcY);
        if (Math.Abs(Math.Abs(normal[2]) - 1) > 1e-9) return false;
        double t0 = location.ArcStartParameter, direction = Math.Sign(location.ArcEndParameter - t0);
        double[] tangent3 = [direction * (-Math.Sin(t0) * arcX[0] + Math.Cos(t0) * arcY[0]),
            direction * (-Math.Sin(t0) * arcX[1] + Math.Cos(t0) * arcY[1]), 0];
        double[] u = CleanAxisDirectionCurrent(tangent3[0], tangent3[1]);
        double[] origin = [location.StartPoint[0] * FeetToMetres, location.StartPoint[1] * FeetToMetres, oldOrigin[2]];

        double[] ToFrame(double[] point) =>
        [
            (point[0] * FeetToMetres - origin[0]) * u[0] + (point[1] * FeetToMetres - origin[1]) * u[1],
            -(point[0] * FeetToMetres - origin[0]) * u[1] + (point[1] * FeetToMetres - origin[1]) * u[0],
            point[2] * FeetToMetres - origin[2],
        ];
        double[] Rotate(double[] vector) => [vector[0] * u[0] + vector[1] * u[1], -vector[0] * u[1] + vector[1] * u[0], vector[2]];

        var rings = extrusion.Loops.Select(loop => loop.Select(ToFrame).ToList()).ToList();
        double area = 0;
        for (int i = 0; i < rings[0].Count; i++)
            area += CrossCurrent(rings[0][i], rings[0][(i + 1) % rings[0].Count])[2];
        if (!(Math.Abs(area) > 1e-12)) return false;
        double[] planeNormal = [0, 0, area > 0 ? 1 : -1];
        var frameArcs = extrusion.Arcs.Select(arc => arc.Transformed(ToFrame, Rotate, FeetToMetres)).ToList();
        var loops = new List<IReadOnlyList<double[]>>();
        List<double[]> outerCorners = [];
        for (int i = 0; i < rings.Count; i++)
        {
            loops.Add(CurveLoopCurrent(rings[i], planeNormal, outer: i == 0, frameArcs, out var corners, shift: 0));
            if (i == 0) outerCorners = corners;
        }
        // a ring whose circular edges could not all be told is left to the writer of the straight form
        if (outerCorners.Count >= rings[0].Count) return false;

        var centreLocal = ToFrame(centre);
        var referenceLocal = Rotate(arcX);
        int circlePlacement = w.Add("IFCAXIS2PLACEMENT3D",
            $"{StepWriter.Ref(globals.Point3D(centreLocal[0], centreLocal[1], 0))},{StepWriter.Ref(globals.Direction3D(0, 0, normal[2] > 0 ? 1 : -1))},"
            + StepWriter.Ref(globals.Direction3D(referenceLocal[0], referenceLocal[1], 0)));
        int circle = w.Add("IFCCIRCLE", $"{StepWriter.Ref(circlePlacement)},{StepWriter.Real(radiusFeet * FeetToMetres)}");
        var endLocal = ToFrame(location.EndPoint);
        int endPoint = globals.Point3D(endLocal[0], endLocal[1], 0);

        var solid = SplitStepArguments(w.EntityAt(solidIndex).Args);
        if (solid.Length != 4 || !TryEntityId(solid[0], out int profileId) || !TryEntityId(solid[1], out int positionId)
            || !TryEntityId(solid[2], out int solidDirectionId))
            return false;
        string[] nameParts = wall[2].Trim('\'').Split(':');
        var parts = WriteExtrusionProfileCurrent(w, globals, nameParts.Length >= 3 ? $"'{nameParts[1]}'" : "''", loops, planeNormal,
            [u[1], u[0], 0], extrusion: [0, 0, 1], originPoints: outerCorners);
        detached.AddRange(ReferencedIdsCurrent(w.EntityAt(frameIndex).Args));
        detached.Add(frameId);
        detached.AddRange(ReferencedIdsCurrent(w.EntityAt(axisCurveIndex).Args));
        foreach (int oldId in new[] { profileId, positionId })
            if (indexById.TryGetValue(oldId, out int oldIndex)) detached.AddRange(ReferencedIdsCurrent(w.EntityAt(oldIndex).Args));
        detached.AddRange([profileId, positionId, solidDirectionId]);

        int newFrame = globals.Axis3D(origin, [0, 0, 1], [u[0], u[1], 0]);
        w.ReplaceArgs(placementIndex, $"{placement[0]},{StepWriter.Ref(newFrame)}");
        w.ReplaceArgs(solidIndex, $"{StepWriter.Ref(parts.ProfileId)},{StepWriter.Ref(parts.PositionId)},{StepWriter.Ref(parts.DirectionId)},"
            + StepWriter.Real(extrusion.LengthFeet * FeetToMetres));
        w.ReplaceEntity(axisCurveIndex, "IFCTRIMMEDCURVE",
            $"{StepWriter.Ref(circle)},{StepWriter.List(StepWriter.Ref(globals.Origin3D()))},{StepWriter.List(StepWriter.Ref(endPoint))},.T.,.CARTESIAN.");
        return true;
    }
}
