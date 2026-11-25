using RvtIO.Model;

namespace RvtIO.Ifc;

public partial class IfcExporter
{
    internal bool TryCreateOwnFamilyCutoutBodyCurrent(StepWriter writer, Element insert, Element host,
        IReadOnlyDictionary<int, Element> elements, HostedOpeningGeometry opening, int subContextId,
        out int shapeId)
    {
        shapeId = 0;
        if (!CurrentProfile || !(TryBuildOwnSecondaryFamilyCutout(insert, host, elements, out var source)
                || TryBuildOwnUnfilledPrimaryFamilyCutout(insert, host, out source))
            || opening != source || host.OwnWallCrossSection != 1
            || host.OwnWallAxisLine is not { Length: 2 } axis
            || axis.Any(point => point.Length != 3 || point.Any(value => !double.IsFinite(value)))) return false;
        double dx = axis[1][0] - axis[0][0], dy = axis[1][1] - axis[0][1];
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (!(length > 0) || !double.IsFinite(length)) return false;
        var localWall = new LocationCurve
        {
            CurveType = "Line", StartPoint = [0, 0, 0],
            EndPoint = [dx * opening.AxisX + dy * opening.AxisY,
                -dx * opening.AxisY + dy * opening.AxisX, 0],
        };
        var local = opening with { CentreXFt = 0, CentreYFt = 0, AxisX = 1, AxisY = 0, BaseZFt = 0 };
        if (!TryBuildExactRectangularOpeningBoundary(local, out var geometry)) return false;
        if (RectangularOpeningHasWallExtrusionAxisCurrent(local, localWall))
            return TryCreateRectangularOpeningBodyCurrent(writer, geometry, localWall, subContextId,
                out shapeId, out _);
        // The body export is restricted to the custom extrusion; an oblique axis leaves a BRep and no extrusion quantities.
        shapeId = CreateFacetedBrep(writer, geometry, subContextId, preserveValidatedPlanarFaces: true);
        return shapeId > 0;
    }

    internal static bool TryBuildOwnSecondaryFamilyCutout(Element insert, Element host,
        IReadOnlyDictionary<int, Element> elements, out HostedOpeningGeometry opening)
        => TryBuildOwnExtraHostFamilyCutout(insert, host, elements, out opening)
            || TryBuildOwnShadowFamilyCutout(insert, host, elements, out opening);

    internal static bool TryBuildOwnUnfilledPrimaryFamilyCutout(Element insert, Element host,
        out HostedOpeningGeometry opening)
    {
        opening = default;
        if (host.OwnWallCutoutSteps?.Where(step => step.InstanceId == insert.Id).ToArray()
                is not [{ ClassId: 4137 }]
            || !TryBuildOwnPrimaryFamilyCutout(insert, host, out var primary)) return false;
        var source = insert.OwnSingleFamilyCutout!.Value;
        var matrix = source.Transform;
        double x = source.Loop.Vertices.Average(point => point[0]);
        double y = source.Loop.Vertices.Average(point => point[1]);
        double z = source.Loop.Vertices.Average(point => point[2]);
        // Without door/window info, the delayed creator keeps the original cutting solid [-2W,+2W].
        opening = primary with
        {
            CentreXFt = matrix[0] * x + matrix[1] * y + matrix[2] * z + matrix[9],
            CentreYFt = matrix[3] * x + matrix[4] * y + matrix[5] * z + matrix[10],
            DepthFt = 4 * primary.DepthFt,
        };
        return double.IsFinite(opening.DepthFt) && double.IsFinite(opening.CentreXFt)
            && double.IsFinite(opening.CentreYFt);
    }

    internal static bool TryBuildOwnExtraHostFamilyCutout(Element insert, Element host,
        IReadOnlyDictionary<int, Element> elements, out HostedOpeningGeometry opening)
    {
        opening = default;
        var steps = host.OwnWallCutoutSteps?.Where(step => step.InstanceId == insert.Id).ToArray();
        if (steps is not [var step]) return false;
        var effective = host;
        if (step.ClassId == 4155)
        {
            if (step.OtherWallId is not > 0 || step.OtherWallId == host.Id
                || step.OtherGeneratorId is not int generator
                || !elements.TryGetValue(step.OtherWallId.Value, out effective!)
                || effective.OwnWallCutoutSteps?.Where(item => item.GeneratorId == generator).ToArray()
                    is not [{ ClassId: 4137 } original] || original.InstanceId != insert.Id) return false;
        }
        else if (step.ClassId != 4137) return false;
        if (insert.OwnSingleFamilyCutout is not { } source || effective.Id == source.HostId
            || insert.ExtraHostIds?.Contains(effective.Id) != true
            || !elements.TryGetValue(source.HostId, out var primaryWall)
            || !TryBuildOwnPrimaryFamilyCutout(insert, primaryWall, out var primary)) return false;
        double[]? normal = primaryWall.OwnWallSide0NormalCurrent;
        if (normal == null && primaryWall.Location is { CurveType: "Line", ArcRadius: null } loc
            && loc.StartPoint is { Length: >= 2 } sp && loc.EndPoint is { Length: >= 2 } ep)
        {
            double ldx = ep[0] - sp[0], ldy = ep[1] - sp[1];
            double llen = Math.Sqrt(ldx * ldx + ldy * ldy);
            if (llen > 0) normal = [-ldy / llen, ldx / llen, 0];
        }
        if (normal is not { Length: 3 }
            || normal.Any(value => !double.IsFinite(value)) || normal[2] != 0
            || effective.ValidatedWallTypeThicknessFeet is not double width
            || !(width > 0) || !double.IsFinite(width)) return false;
        const double directionRoundoff = 4096 * 2.2204460492503131e-16;
        if (Math.Abs(normal.Sum(value => value * value) - 1) > directionRoundoff
            || Math.Abs(primary.AxisX * normal[0] + primary.AxisY * normal[1]) > directionRoundoff) return false;
        var matrix = source.Transform;
        double x = source.Loop.Vertices.Average(point => point[0]);
        double y = source.Loop.Vertices.Average(point => point[1]);
        double z = source.Loop.Vertices.Average(point => point[2]);
        // ExtraHostIds uses primary side0 and the passed wall's width over [-W,0].
        opening = primary with
        {
            CentreXFt = matrix[0] * x + matrix[1] * y + matrix[2] * z + matrix[9] - width * normal[0] / 2,
            CentreYFt = matrix[3] * x + matrix[4] * y + matrix[5] * z + matrix[10] - width * normal[1] / 2,
            DepthFt = width,
            OwnPrimaryFamilyHostId = 0,
        };
        return double.IsFinite(opening.CentreXFt) && double.IsFinite(opening.CentreYFt);
    }

    internal static bool TryBuildOwnShadowFamilyCutout(Element insert, Element host,
        IReadOnlyDictionary<int, Element> elements, out HostedOpeningGeometry opening)
    {
        opening = default;
        var shadows = host.OwnWallCutoutSteps?.Where(step => step.InstanceId == insert.Id).ToArray();
        if (shadows is not { Length: 1 } || shadows[0] is not { ClassId: 4155,
                OtherWallId: > 0, OtherGeneratorId: int generator } shadow
            || shadow.OtherWallId == host.Id
            || !elements.TryGetValue(shadow.OtherWallId.Value, out var original)
            || original.OwnWallCutoutSteps?.Where(step => step.GeneratorId == generator).ToArray()
                is not [{ ClassId: 4137 } sourceStep]
            || sourceStep.InstanceId != insert.Id
            || !TryBuildOwnPrimaryFamilyCutout(insert, original, out var primary)) return false;
        var source = insert.OwnSingleFamilyCutout!.Value;
        var matrix = source.Transform;
        double x = source.Loop.Vertices.Average(point => point[0]);
        double y = source.Loop.Vertices.Average(point => point[1]);
        double z = source.Loop.Vertices.Average(point => point[2]);
        // The shadow delegates to the original step: the primary host's cutting solid extrudes [-2W,+2W].
        opening = primary with
        {
            CentreXFt = matrix[0] * x + matrix[1] * y + matrix[2] * z + matrix[9],
            CentreYFt = matrix[3] * x + matrix[4] * y + matrix[5] * z + matrix[10],
            DepthFt = 4 * primary.DepthFt,
            OwnPrimaryFamilyHostId = 0,
        };
        return double.IsFinite(opening.DepthFt) && double.IsFinite(opening.CentreXFt)
            && double.IsFinite(opening.CentreYFt);
    }

    internal static bool TryBuildOwnPrimaryFamilyCutout(Element insert, Element host,
        out HostedOpeningGeometry opening)
    {
        opening = default;
        if (insert.CategoryId is not (-2000023 or -2000014)
            || insert.OwnSingleFamilyCutout is not { } source || source.HostId != host.Id
            || insert.SerializedHostId != host.Id || source.SymbolId != insert.ValidatedOriginalFamilySymbolId
            || source.Transform is not { Length: 12 } matrix || matrix.Any(value => !double.IsFinite(value))
            || source.Loop is not { Vertices.Count: 4, Direction.Length: 3 }
            || host.OwnWallCutoutInstanceIds?.Contains(insert.Id) != true
            || host.OwnWallCrossSection != 1 || host.OwnWallAxisLine is not { Length: 2 } axis
            || axis.Any(point => point.Length != 3 || point.Any(value => !double.IsFinite(value)))
            || host.ValidatedWallTypeThicknessFeet is not double width || !(width > 0) || !double.IsFinite(width))
            return false;
        double dx = axis[1][0] - axis[0][0], dy = axis[1][1] - axis[0][1];
        double length = Math.Sqrt(dx * dx + dy * dy);
        const double directionRoundoff = 4096 * 2.2204460492503131e-16;
        if (!(length > 0) || !double.IsFinite(length)
            || Math.Abs(axis[1][2] - axis[0][2]) > directionRoundoff * length) return false;
        var transform = new InstanceTransform
        {
            SymbolId = source.SymbolId, Origin = [matrix[9], matrix[10], matrix[11]],
            BasisX = [matrix[0], matrix[3], matrix[6]],
            BasisY = [matrix[1], matrix[4], matrix[7]],
            BasisZ = [matrix[2], matrix[5], matrix[8]],
        };
        if (!TryBuildRectangularFamilyCutLoop(source.Loop, transform, width, out var profile)) return false;
        double ux = dx / length, uy = dy / length, nx = -uy, ny = ux;
        if (Math.Abs(profile.AxisX * uy - profile.AxisY * ux) > directionRoundoff) return false;
        // The straight vertical branch moves the profile to a wall side
        // and extrudes by the wall type width: the resulting box is centred on the own axis plane.
        double offset = (axis[0][0] - profile.CentreXFt) * nx + (axis[0][1] - profile.CentreYFt) * ny;
        opening = profile with
        {
            CentreXFt = profile.CentreXFt + offset * nx,
            CentreYFt = profile.CentreYFt + offset * ny,
            IsFamilyCutLoop = false,
            OwnPrimaryFamilyHostId = host.Id,
        };
        return new[] { opening.CentreXFt, opening.CentreYFt, opening.WidthFt,
            opening.DepthFt, opening.BaseZFt, opening.HeightFt }.All(double.IsFinite);
    }

    private static bool HostedFillingProductCanBeEmitted(Element cutter,
        IReadOnlyDictionary<int, Element> elements, IReadOnlyDictionary<int, int> storeys,
        bool hasProjectPhaseReference)
    {
        if (cutter.IsBlockOnly || !hasProjectPhaseReference || IsNonPhysicalCategory(cutter.Category)
            || cutter.DecodedBox is not { Length: >= 6 } box
            || cutter.Geometry is not { Faces.Count: > 0 } geometry
            || !geometry.Faces.Any(face => face.OuterVertices is { Count: >= 3 })
            || FacetedGeometryCannotCreateProductShapeAtIfcPrecision(geometry)) return false;
        var levels = cutter.Parameters.Where(parameter => parameter.DefinitionName is
                "LEVEL_PARAM" or "FAMILY_LEVEL_PARAM" or "SCHEDULE_LEVEL_PARAM" or "INSTANCE_REFERENCE_LEVEL_PARAM")
            .Select(parameter => parameter.Value).OfType<int>()
            .Where(id => id > 0 && elements.TryGetValue(id, out var level) && level.CategoryId == -2000240).ToArray();
        return (cutter.Transform == null || levels.Length == 0 || levels.Any(storeys.ContainsKey))
            && Enumerable.Range(0, 3).Any(index => Math.Abs(box[index + 3] - box[index]) * FeetToMetres >= 0.01);
    }
}
