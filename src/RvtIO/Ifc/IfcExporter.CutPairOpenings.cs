using RvtIO.Geometry;
using RvtIO.Model;

namespace RvtIO.Ifc;

public partial class IfcExporter
{
    private const ushort CutPairGStepClassId = 977, EmbedWallGStepClassId = 4138, ShadowCutoutGStepClassId = 4155,
        CutoutGStepClassId = 4137;
    private const int WallsCategoryId = -2000011, FloorsCategoryId = -2000032, GenericModelsCategoryId = -2000151,
        DoorsCategoryId = -2000023, WindowsCategoryId = -2000014;

    private const double CutPairOverlapToleranceFt = 1e-3;

    /// <summary>The openings written for each host wall by <see cref="WriteCutPairOpeningsCurrent"/>, in order.</summary>
    private readonly Dictionary<int, List<int>> _cutPairOpeningsByHostCurrent = new();

    /// <summary>The cutters per host element that <see cref="WriteCutPairOpeningsCurrent"/> found to open the host but could not
    /// write (no extent), which <see cref="ApplyCutOpeningIdentities"/> still numbers.</summary>
    private readonly Dictionary<int, HashSet<int>> _unbuiltOpeningCuttersCurrent = new();

    private void WriteCutPairOpeningsCurrent(
        StepWriter w,
        IReadOnlyDictionary<int, (int entityId, int placementId, int storeyId)> wallIfcIds,
        IReadOnlyDictionary<int, (int entityId, int placementId, string ifcType)> exportedIfcElements,
        IReadOnlyDictionary<int, Element> elemById,
        IReadOnlyDictionary<int, int> wallQuantityTakeOffReferenceValueIds,
        IReadOnlyDictionary<int, int> slabQuantityTakeOffReferenceValueIds,
        IReadOnlyDictionary<int, (ProfileWallThroughOpening Opening, int TagId)> exactJoinedWallEndProfileOpenings,
        Dictionary<int, List<int>> storeyContents,
        int parentPlacementId, int ownerHistId, int subCtxId,
        Dictionary<(string PropertyName, string ValueExpr), int> propertySingleValueIds)
    {
        static bool IsPairOrEmbedStep((ushort ClassId, int ElementId, bool Cutout) step) =>
            step.ClassId is CutPairGStepClassId or EmbedWallGStepClassId;
        bool IsOpeningStep(int hostId, (ushort ClassId, int ElementId, bool Cutout) step) =>
            IsPairOrEmbedStep(step) || step.ClassId == ShadowCutoutGStepClassId
            || step.ClassId == CutoutGStepClassId && elemById.TryGetValue(step.ElementId, out var inserted)
                && (inserted.CategoryId == GenericModelsCategoryId
                    || inserted.CategoryId is DoorsCategoryId or WindowsCategoryId && inserted.SerializedHostId != hostId);
        if (!wallIfcIds.Keys.Any(id => elemById.TryGetValue(id, out var wall)
                && wall.CutOutSteps?.Any(step => IsOpeningStep(id, step)) == true))
            return;

        // (host entity, cutter id) of every opening another writer has already produced.
        var openingTags = new Dictionary<int, string>();
        var hostOfOpening = new Dictionary<int, int>();
        for (int i = 0; i < w.EntityCount; i++)
        {
            var (id, type, args) = w.EntityAt(i);
            if (type == "IFCOPENINGELEMENT")
            {
                if (SplitStepArguments(args) is { Length: 8 } attributes) openingTags[id] = attributes[7].Trim('\'');
            }
            else if (type == "IFCRELVOIDSELEMENT" && SplitStepArguments(args) is { Length: 6 } relation
                && TryEntityId(relation[4], out int relating) && TryEntityId(relation[5], out int related))
                hostOfOpening[related] = relating;
        }
        var existing = openingTags
            .Where(pair => hostOfOpening.ContainsKey(pair.Key))
            .Select(pair => (hostOfOpening[pair.Key], pair.Value)).ToHashSet();

        var writtenPairs = new HashSet<(int Host, int Cutter)>();
        int written = 0, insertedWritten = 0, extrusionHosts = 0, otherHosts = 0, boxMisses = 0;
        foreach (var (hostId, wallInfo) in wallIfcIds.OrderBy(pair => pair.Key))
        {
            if (WallWritesNoBodyOpeningsCurrent(exportedIfcElements, hostId)
                || !elemById.TryGetValue(hostId, out var host)
                || host.CutOutSteps is not { Count: > 0 } steps
                || !steps.Any(step => IsOpeningStep(hostId, step)))
                continue;
            if (!IsAxisExtrusionHostCurrent(host))
            {
                otherHosts++;
                continue;
            }
            extrusionHosts++;
            foreach (var (stepClass, cutterId) in steps.Where(step => IsOpeningStep(hostId, step))
                         .Select(step => (step.ClassId, step.ElementId)).Distinct())
            {
                if (cutterId == hostId || writtenPairs.Contains((hostId, cutterId))
                    || existing.Contains((wallInfo.entityId, cutterId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                    || exactJoinedWallEndProfileOpenings.TryGetValue(hostId, out var joined) && joined.TagId == cutterId
                    || !elemById.TryGetValue(cutterId, out var cutter))
                    continue;
                bool inserted = stepClass is ShadowCutoutGStepClassId or CutoutGStepClassId;
                if (!inserted)
                {
                    if (cutter.CategoryId is not (WallsCategoryId or FloorsCategoryId or GenericModelsCategoryId)
                        || !exportedIfcElements.TryGetValue(cutterId, out var cutterProduct))
                        continue;
                    if (stepClass == EmbedWallGStepClassId)
                    {
                        if (cutterProduct.ifcType != "IFCCURTAINWALL") continue;
                    }
                    else if (!CutterBoxIntersectsHostCurrent(host, cutter))
                    {
                        boxMisses++;
                        continue;
                    }
                }
                HostedOpeningGeometry opening;
                if (!(inserted && TryBuildOwnSecondaryFamilyCutout(cutter, host, elemById, out opening))
                    && !TryBuildCutPairOpening(host, cutter, out opening))
                {
                    if (!_unbuiltOpeningCuttersCurrent.TryGetValue(hostId, out var unbuilt))
                        _unbuiltOpeningCuttersCurrent[hostId] = unbuilt = [];
                    unbuilt.Add(cutterId);
                    continue;
                }
                if (!storeyContents.TryGetValue(wallInfo.storeyId, out var wallStoreyList))
                    storeyContents[wallInfo.storeyId] = wallStoreyList = new List<int>();
                // The opening carries the cutter's type as Reference, as the cutter's own product does.
                int? referenceValueId = (wallQuantityTakeOffReferenceValueIds.TryGetValue(cutterId, out int valueId)
                    || slabQuantityTakeOffReferenceValueIds.TryGetValue(cutterId, out valueId))
                    ? valueId : null;
                int openingEntityId = CreateHostedOpeningSweptBox(w, cutter, 0, host, wallInfo.entityId, parentPlacementId,
                    ownerHistId, subCtxId, wallStoreyList, elemById, createFillRelationship: false,
                    propertySingleValueIds, opening, referenceValueId);
                if (!_cutPairOpeningsByHostCurrent.TryGetValue(hostId, out var hostOpenings))
                    _cutPairOpeningsByHostCurrent[hostId] = hostOpenings = [];
                hostOpenings.Add(openingEntityId);
                written++;
                writtenPairs.Add((hostId, cutterId));
                if (inserted) insertedWritten++;
            }
        }
        _diag?.WriteLine($"The current profile step openings: {written} (inserted-element steps {insertedWritten}; axis-extrusion hosts {extrusionHosts}, other hosts with steps {otherHosts}, pair cutters outside the host box {boxMisses})");
    }

    internal static bool IsAxisExtrusionHostCurrent(Element wall) =>
        wall.Location is { CurveType: "Line", ArcRadius: null } && PassesSimpleRectangularWallCurrent(wall) == true;

    internal static bool? PassesSimpleRectangularWallCurrent(Element wall)
    {
        if (wall.Geometry?.Faces is not { Count: > 0 } faces) return null;
        var solids = wall.Geometry.BrepSolids;
        var first = solids is { Count: > 0 }
            ? solids[0].FaceIndices.Where(index => (uint)index < (uint)faces.Count).Select(index => faces[index]).ToList()
            : faces;
        return first.Count > 0 ? WallExtrusion.IsSimpleRectangularWall(first, wall.OwnWallHelper, wall.Geometry.InactiveFaces) : null;
    }

    internal static bool CutterBoxIntersectsHostCurrent(Element host, Element cutter)
    {
        var a = host.DecodedBox ?? host.Geometry?.BoundingBox;
        var b = cutter.DecodedBox ?? cutter.Geometry?.BoundingBox;
        if (a is not { Length: >= 6 } || b is not { Length: >= 6 }
            || a.Take(6).Any(value => !double.IsFinite(value)) || b.Take(6).Any(value => !double.IsFinite(value)))
            return false;
        for (int axis = 0; axis < 3; axis++)
            if (!(Math.Min(a[axis + 3], b[axis + 3]) - Math.Max(a[axis], b[axis]) > CutPairOverlapToleranceFt))
                return false;
        return true;
    }

    /// <summary>
    /// The box of <paramref name="cutter"/> in the frame of the straight wall <paramref name="host"/> (along the location line,
    /// across it, vertical): the extent of the cutter's own body, as the current profile writes the cutter's whole solid as the
    /// opening and not its part inside the host.
    /// </summary>
    internal static bool TryBuildCutPairOpening(Element host, Element cutter, out HostedOpeningGeometry opening)
    {
        opening = default;
        if (host.Location is not { CurveType: "Line", ArcRadius: null } location) return false;
        double dx = location.EndPoint[0] - location.StartPoint[0];
        double dy = location.EndPoint[1] - location.StartPoint[1];
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (!(length > 0) || !double.IsFinite(length)) return false;
        double axisX = dx / length, axisY = dy / length;
        if (!TryGetFrameExtent(cutter, axisX, axisY, out var cutterBox))
            return false;

        // The frame's across axis is the left normal (-axisY, axisX).
        double centreAlong = (cutterBox[0] + cutterBox[1]) / 2, centreAcross = (cutterBox[2] + cutterBox[3]) / 2;
        opening = new HostedOpeningGeometry(
            centreAlong * axisX - centreAcross * axisY, centreAlong * axisY + centreAcross * axisX,
            axisX, axisY, cutterBox[1] - cutterBox[0], cutterBox[3] - cutterBox[2], cutterBox[4],
            cutterBox[5] - cutterBox[4], ExactDepth: true);
        return true;
    }

    /// <summary>Extent [minAlong, maxAlong, minAcross, maxAcross, minZ, maxZ] of an element's decoded body, or
    /// else of its bounding box, in the frame of the axis (<paramref name="axisX"/>, <paramref name="axisY"/>),
    /// in feet.</summary>
    private static bool TryGetFrameExtent(Element element, double axisX, double axisY, out double[] extent)
    {
        double[] found = NewExtent();
        int count = 0;
        void Add(double x, double y, double z)
        {
            double along = x * axisX + y * axisY, across = -x * axisY + y * axisX;
            found[0] = Math.Min(found[0], along); found[1] = Math.Max(found[1], along);
            found[2] = Math.Min(found[2], across); found[3] = Math.Max(found[3], across);
            found[4] = Math.Min(found[4], z); found[5] = Math.Max(found[5], z);
            count++;
        }
        foreach (var face in element.Geometry?.Faces ?? [])
            foreach (var point in face.OuterVertices ?? [])
                if (point.Length >= 3 && double.IsFinite(point[0]) && double.IsFinite(point[1])
                    && double.IsFinite(point[2]))
                    Add(point[0], point[1], point[2]);
        if (count < 8)
        {
            // No usable body: the world bounding box (exact for a wall parallel to the axes).
            found = NewExtent();
            if (element.DecodedBox is not { Length: 6 } box || box.Any(value => !double.IsFinite(value)))
            {
                extent = found;
                return false;
            }
            foreach (double x in new[] { box[0], box[3] })
                foreach (double y in new[] { box[1], box[4] })
                    foreach (double z in new[] { box[2], box[5] })
                        Add(x, y, z);
        }
        extent = found;
        return true;

        static double[] NewExtent() => [double.PositiveInfinity, double.NegativeInfinity,
            double.PositiveInfinity, double.NegativeInfinity, double.PositiveInfinity, double.NegativeInfinity];
    }
}
