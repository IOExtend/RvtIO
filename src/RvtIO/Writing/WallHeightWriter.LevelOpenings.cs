using OpenMcdf;
using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Writing;

internal static partial class WallHeightWriter
{
    private static (Dictionary<double, double> Elevations, List<RvtPartitionRecordEdit> Edits) MoveLevelOpenings(
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

            AddElevation(cut.Bottom, bottom);
            AddElevation(cut.Top, top);
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
        return (elevations, edits);

        (int Partition, byte[] Body) Record(long id, int channel) =>
            records.TryGetValue((id, channel), out var record) ? record : throw Layout();

        WideBlockWalker.Capture Value(Archive opening, long id, BuiltInParameter parameter, StorageType type) =>
            ParameterWriter.FindValue(walker, opening.Walk, new(id, (int)parameter, null), type)?.Value ?? throw Layout();

        double Destination(long id, double previous) => id != move.Id ? previous
            : previous == move.Previous ? move.Destination : throw Layout();

        void AddElevation(double before, double after)
        {
            if (!elevations.TryAdd(before, after) && elevations[before] != after)
                throw ParameterWriter.Unverified("Two opening edges at the same elevation would move to different elevations.");
        }
    }
}
