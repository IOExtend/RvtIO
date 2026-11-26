using OpenMcdf;
using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Writing;

internal static partial class WallHeightWriter
{
    private static (HeightInterval Height, Dictionary<int, HeightInterval>? FaceHeights, List<RvtPartitionRecordEdit> Edits)
        MoveLevelOpenings(
        RootStorage root, PartitionFraming.Tags tags, int idWidth, ParameterWriter.Pending pending,
        Archive wall, WideBlockWalker walker, HeightInterval wallHeight, IReadOnlyList<OpeningInterval> openings,
        LevelMove move, Func<byte[], string, bool, Archive> read, Func<long, double> levelElevation)
    {
        var owners = openings.ToDictionary(o => wall.Integer(o.Step, "WallCutoutGStepBase.m_instId"));
        var records = new Dictionary<(long Id, int Channel), (int Partition, byte[] Body)>();
        ParameterWriter.ScanPartitions(root, tags, idWidth, (partition, record) =>
        {
            long id = (long)record.Owner;
            if (record.Block == 0 && owners.ContainsKey(id)
                && !records.TryAdd((id, record.Channel), (partition, record.Body.ToArray())))
                throw Layout();
        }, pending);
        if (records.Count != owners.Count * 3)
            throw Layout();

        long wallBaseLevel = wall.Integer(-1, "Element.m_assocLevelId");
        long wallTopLevel = wall.Integer(-1, "VWall.m_upToLevelId");
        var elevations = new Dictionary<double, double>();
        var openingHeights = new List<(int Step, double PreviousBottom, double PreviousTop, double Bottom, double Top)>();
        bool sharedElevationMovesApart = false;
        var edits = new List<RvtPartitionRecordEdit>();
        foreach (var (id, cut) in owners)
        {
            var (partition, body) = Record(id, ElementChannel);
            var (headerPartition, headerBody) = Record(id, HeaderChannel);
            var (geometryPartition, geometryBody) = Record(id, GeometryChannel);
            if (partition != headerPartition || partition != geometryPartition)
                throw Layout();

            var opening = read(body, "SWallRectOpening", false);
            var header = read(headerBody, "ElementHeader", false);
            var geometry = read(geometryBody, "GElement", true);
            if (cut.Cut != OpeningCut.Interior || opening.Integer(-1, "Element.m_designOptionId") != -4
                || opening.Integer(-1, "Element.m_constrInfo#") != 0
                || opening.Walk.Queue.Any(tag => walker.IsA(tag, "GroupMembership") || walker.IsA(tag, "ParametrizedElemParamsCellBase"))
                || header.Integer(-1, "ElementHeader.m_designOptionId") != -1
                || header.Integer(-1, "ElementHeader.m_categroryId") != (int)BuiltInCategory.OST_SWallRectOpening
                || !GElementArchiveHandles.IsCanonical(geometry.Walk)
                || geometry.Integer(-1, idWidth == sizeof(long) ? "GRep.m_elementId" : "GInfo.m_tag") != id)
                throw ParameterWriter.Unverified("The level's rectangular opening has unverified geometry or dependencies.");

            long lowerLevel = Value(opening, id, BuiltInParameter.WALL_BASE_CONSTRAINT, StorageType.ElementId).Integer;
            long upperLevel = Value(opening, id, BuiltInParameter.WALL_HEIGHT_TYPE, StorageType.ElementId).Integer;
            if (lowerLevel != wallBaseLevel || upperLevel != (wallTopLevel > 0 ? wallTopLevel : wallBaseLevel))
                throw ParameterWriter.Unverified("The opening's constraints do not match the verified wall levels.");

            double lowerDatum = levelElevation(lowerLevel), upperDatum = levelElevation(upperLevel);
            double lowerOffset = Value(opening, id, BuiltInParameter.WALL_BASE_OFFSET, StorageType.Double).Real;
            double upperOffset = Value(opening, id, BuiltInParameter.WALL_TOP_OFFSET, StorageType.Double).Real;
            double nextLowerDatum = Destination(lowerLevel, lowerDatum);
            double bottom = nextLowerDatum + lowerOffset;
            double top = Destination(upperLevel, upperDatum) + upperOffset;
            int outline = header.Target(-1, "ElementHeader.m_pBBox", "Outline");
            int rectangle = opening.Target(-1, "OpeningBase.m_oRectOpeningData", "RectOpeningData");
            var savedHeight = Value(opening, id, BuiltInParameter.WALL_USER_HEIGHT_PARAM, StorageType.Double);
            var interval = new HeightInterval(cut.Bottom, savedHeight.Real, top - bottom) { NewBase = bottom };
            if (lowerDatum + lowerOffset != cut.Bottom || upperDatum + upperOffset != cut.Top
                || opening.Real(rectangle, "RectOpeningData.m_aPoint@0/2") != cut.Bottom
                || opening.Real(rectangle, "RectOpeningData.m_aPoint@1/2") != cut.Top
                || opening.Real(rectangle, "RectOpeningData.m_lowerLeftCorner@1") != cut.Bottom - lowerDatum
                || opening.Real(rectangle, "RectOpeningData.m_upperRightCorner@1") != cut.Top - lowerDatum
                || interval.Top != cut.Top || interval.NewTop != top
                || header.Real(outline, "Outline.m_minmax@0/2") != interval.BoundsBase
                || header.Real(outline, "Outline.m_minmax@1/2") != cut.Top)
                throw Layout();
            if (!double.IsFinite(bottom) || !double.IsFinite(top) || top <= bottom
                || CutAt(wallHeight.NewBase, wallHeight.NewTop, bottom, top) != OpeningCut.Interior)
                throw ParameterWriter.Unverified("Moving the level would take a rectangular opening outside its verified interior cut.");

            sharedElevationMovesApart |= !AddElevation(cut.Bottom, bottom);
            sharedElevationMovesApart |= !AddElevation(cut.Top, top);
            openingHeights.Add((cut.Step, cut.Bottom, cut.Top, bottom, top));
            _ = ResizeGeometry(geometry, interval, [], openingSolid: true);
            if (bottom == cut.Bottom && top == cut.Top && nextLowerDatum == lowerDatum)
                continue;

            opening.Write(rectangle, "RectOpeningData.m_aPoint@0/2", bottom);
            opening.Write(rectangle, "RectOpeningData.m_aPoint@1/2", top);
            opening.Write(rectangle, "RectOpeningData.m_lowerLeftCorner@1", bottom - nextLowerDatum);
            opening.Write(rectangle, "RectOpeningData.m_upperRightCorner@1", top - nextLowerDatum);
            opening.Write(savedHeight, interval.After);
            header.Write(outline, "Outline.m_minmax@0/2", interval.NewBoundsBase);
            header.Write(outline, "Outline.m_minmax@1/2", top);
            _ = read(body, "SWallRectOpening", false);
            _ = read(geometryBody, "GElement", true);
            edits.AddRange([new(partition, HeaderChannel, (ulong)id, headerBody),
                new(partition, ElementChannel, (ulong)id, body), new(partition, GeometryChannel, (ulong)id, geometryBody)]);
        }
        if (!sharedElevationMovesApart)
            return (wallHeight with { OpeningElevations = elevations }, null, edits);

        // Shared elevations have to be resolved through the original cutting faces.
        wallHeight = wallHeight with { OpeningElevationsRequiringOwner = elevations.Keys.ToHashSet() };
        var cuttingFaces = new Dictionary<int, HeightInterval>();
        foreach (var (step, previousBottom, previousTop, bottom, top) in openingHeights)
        {
            var height = wallHeight with
            {
                OpeningElevations = new Dictionary<double, double> { [previousBottom] = bottom, [previousTop] = top },
            };
            for (int side = 0; side < 4; side++)
            {
                long tag = wall.Integer(step, $"WallRectOpeningGStep.m_OpeningFacesIds@{side}");
                if (tag <= 0 || tag >= int.MaxValue || !cuttingFaces.TryAdd((int)tag, height))
                    throw Layout();
            }
        }
        return (wallHeight, cuttingFaces, edits);

        (int Partition, byte[] Body) Record(long id, int channel) =>
            records.TryGetValue((id, channel), out var record) ? record : throw Layout();

        WideBlockWalker.Capture Value(Archive opening, long id, BuiltInParameter parameter, StorageType type) =>
            ParameterWriter.FindValue(walker, opening.Walk, new(id, (int)parameter, null), type)?.Value ?? throw Layout();

        double Destination(long id, double previous) => id != move.Id ? previous
            : previous == move.Previous ? move.Destination : throw Layout();

        bool AddElevation(double before, double after) =>
            elevations.TryAdd(before, after) || elevations[before] == after;
    }

    private static (Dictionary<uint, HeightInterval> Faces, Dictionary<int, HeightInterval> Edges) FindOpeningGeometry(
        Archive geometry, IReadOnlyDictionary<int, HeightInterval> cuttingFaces, IReadOnlyDictionary<int, uint> faceHandles,
        IReadOnlySet<int> physicalFaces, IReadOnlyDictionary<uint, int> edges)
    {
        var faces = new Dictionary<uint, HeightInterval>();
        foreach (int face in physicalFaces)
            if (cuttingFaces.TryGetValue(GeometryTag(geometry, face), out var height))
                faces.Add(faceHandles[face], height);
        if (faces.Count != cuttingFaces.Count)
            throw Layout();

        var sides = edges.Values.ToDictionary(edge => edge, edge =>
            (First: geometry.BackReference(edge, "GEdge.m_pFace@0"), Second: geometry.BackReference(edge, "GEdge.m_pFace@1")));
        var virtualFaces = new Dictionary<uint, HeightInterval>();
        foreach (int face in physicalFaces)
        {
            uint handle = faceHandles[face];
            if (geometry.OptionalTarget(face, "GFace.m_pFirstLoop", "EdgeLoop") is not null)
                continue;
            if (faces.ContainsKey(handle))
                throw Layout();
            var adjacent = sides.Values.Where(pair => pair.First == handle || pair.Second == handle)
                .Select(pair => pair.First == handle ? pair.Second : pair.First).Distinct().ToArray();
            if (adjacent.Length != 4 || !faces.TryGetValue(adjacent[0], out var owner)
                || adjacent.Any(other => !faces.TryGetValue(other, out var height) || height != owner))
                throw Layout();
            virtualFaces.Add(handle, owner);
        }
        foreach (var (handle, owner) in virtualFaces)
            faces.Add(handle, owner);
        if (faces.Values.GroupBy(owner => owner).Any(group => group.Count() != 6))
            throw Layout();

        var edgeHeights = new Dictionary<int, HeightInterval>();
        foreach (var (edge, (first, second)) in sides)
        {
            bool firstOpening = faces.TryGetValue(first, out var a);
            bool secondOpening = faces.TryGetValue(second, out var b);
            if (firstOpening && secondOpening && a != b)
                throw Layout();
            if (firstOpening || secondOpening)
                edgeHeights.Add(edge, firstOpening ? a : b);
        }
        return (faces, edgeHeights);
    }
}
