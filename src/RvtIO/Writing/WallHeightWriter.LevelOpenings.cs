using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Writing;

internal static partial class WallHeightWriter
{
    private static long? FindOpeningHost(RootStorage root, PartitionFraming.Tags tags, int idWidth,
        WideBlockWalker walker, ParameterWriter.Edit edit, ParameterWriter.Pending pending)
    {
        byte[]? body = null;
        ParameterWriter.ScanPartitions(root, tags, idWidth, (_, record) =>
        {
            if (record.Block != 0 || record.Channel != ElementChannel || record.Owner != (ulong)edit.ElementId)
                return;
            if (body is not null)
                throw Layout();
            body = record.Body.ToArray();
        }, pending);
        if (body is not { Length: >= sizeof(ushort) })
            return null;
        int tag = BinaryPrimitives.ReadUInt16LittleEndian(body);
        if (walker.Class(tag)?.Name != "SWallRectOpening")
            return null;
        var walk = walker.Run(body, 0, body.Length, tag);
        var opening = new Archive(body, walk, walker);
        if (!walk.Exact || walk.EmptyEntityAt >= 0 || opening.Integer(-1, "Element.m_id") != edit.ElementId)
            throw Layout();
        return opening.Integer(-1, "OpeningBase.m_hostId");
    }

    private static (HeightInterval Height, Dictionary<int, HeightInterval>? FaceHeights,
        List<RvtPartitionRecordEdit> Edits, OpeningInterval[]? Rebuild) MoveOpenings(
        RootStorage root, PartitionFraming.Tags tags, int idWidth, ParameterWriter.Pending pending,
        Archive wall, WideBlockWalker walker, HeightInterval wallHeight, IReadOnlyList<OpeningInterval> openings,
        LevelMove? move, ParameterWriter.Edit? openingEdit, long? openingLevel,
        Func<byte[], string, bool, Archive> read, Func<long, double> levelElevation)
    {
        var owners = openings.Select(o => wall.Integer(o.Step, "WallCutoutGStepBase.m_instId")).ToHashSet();
        var records = new Dictionary<(long Id, int Channel), (int Partition, byte[] Body)>();
        ParameterWriter.ScanPartitions(root, tags, idWidth, (partition, record) =>
        {
            long id = (long)record.Owner;
            if (record.Block == 0 && owners.Contains(id)
                && !records.TryAdd((id, record.Channel), (partition, record.Body.ToArray())))
                throw Layout();
        }, pending);
        if (records.Count != owners.Count * 3)
            throw Layout();

        long wallBaseLevel = wall.Integer(-1, "Element.m_assocLevelId");
        long wallId = wall.Integer(-1, "Element.m_id");
        double wallDatum = levelElevation(wallBaseLevel);
        double nextWallDatum = Destination(wallBaseLevel, wallDatum);
        int? savedRelease = SchemaRelease.Saved(BasicFileInfoReader.Read(root).GetValueOrDefault("RevitVersion"));
        if (savedRelease is not (>= 2020 and <= 2027))
            throw ParameterWriter.Unverified("The saving release is needed to verify rectangular opening movement.");
        bool translatesWithHost = savedRelease <= 2022;
        var elevations = new Dictionary<double, double>();
        var openingHeights = new List<(OpeningInterval Before, OpeningInterval After)>();
        bool sharedElevationMovesApart = false;
        var edits = new List<RvtPartitionRecordEdit>();
        foreach (var cut in openings)
        {
            long id = wall.Integer(cut.Step, "WallCutoutGStepBase.m_instId");
            var (partition, body) = Record(id, ElementChannel);
            var (headerPartition, headerBody) = Record(id, HeaderChannel);
            var (geometryPartition, geometryBody) = Record(id, GeometryChannel);
            if (partition != headerPartition || partition != geometryPartition)
                throw Layout();

            var opening = read(body, "SWallRectOpening", false);
            var header = read(headerBody, "ElementHeader", false);
            var geometry = read(geometryBody, "GElement", true);
            if (opening.Integer(-1, "Element.m_designOptionId") != -4
                || opening.Integer(-1, "Element.m_constrInfo#") != 0
                || opening.Walk.Queue.Any(tag => walker.IsA(tag, "GroupMembership") || walker.IsA(tag, "ParametrizedElemParamsCellBase"))
                || header.Integer(-1, "ElementHeader.m_designOptionId") != -1
                || header.Integer(-1, "ElementHeader.m_categroryId") != (int)BuiltInCategory.OST_SWallRectOpening
                || !GElementArchiveHandles.IsCanonical(geometry.Walk)
                || geometry.Integer(-1, idWidth == sizeof(long) ? "GRep.m_elementId" : "GInfo.m_tag") != id)
                throw ParameterWriter.Unverified("The rectangular opening has unverified geometry or dependencies.");

            var lowerReference = Value(opening, id, BuiltInParameter.WALL_BASE_CONSTRAINT, StorageType.ElementId);
            var upperReference = Value(opening, id, BuiltInParameter.WALL_HEIGHT_TYPE, StorageType.ElementId);
            var associatedLevel = opening.Field(-1, "Element.m_assocLevelId");
            long lowerLevel = lowerReference.Integer, upperLevel = upperReference.Integer;
            if (associatedLevel.Integer != lowerLevel)
                throw Layout();

            var lowerOffset = Value(opening, id, BuiltInParameter.WALL_BASE_OFFSET, StorageType.Double);
            var upperOffset = Value(opening, id, BuiltInParameter.WALL_TOP_OFFSET, StorageType.Double);
            long nextLowerLevel = lowerLevel, nextUpperLevel = upperLevel;
            double nextLowerOffset = lowerOffset.Real, nextUpperOffset = upperOffset.Real;
            if (openingEdit is { } edit && edit.ElementId == id)
            {
                if (edit.Parameter == BuiltInParameter.WALL_BASE_CONSTRAINT)
                    nextLowerLevel = openingLevel ?? throw Layout();
                else if (edit.Parameter == BuiltInParameter.WALL_HEIGHT_TYPE)
                    nextUpperLevel = openingLevel ?? throw Layout();
                else if (ParameterMeasurement.Parse(UnitType.UT_Length, edit.Value!) is not double measurement)
                    throw new RvtWriteRefusedException("invalid-parameter-value", "An opening offset requires a finite length with an explicit unit.");
                else if (edit.Parameter == BuiltInParameter.WALL_BASE_OFFSET)
                    nextLowerOffset = measurement;
                else if (edit.Parameter == BuiltInParameter.WALL_TOP_OFFSET)
                    nextUpperOffset = measurement;
                else
                    throw Layout();
            }

            bool constraintChanged = nextLowerLevel != lowerLevel || nextUpperLevel != upperLevel;
            bool propertyChanged = constraintChanged || nextLowerOffset != lowerOffset.Real || nextUpperOffset != upperOffset.Real;
            bool constraintMoved = move is { } linked && linked.Previous != linked.Destination
                && (linked.Id == lowerLevel || linked.Id == upperLevel);
            double lowerDatum = levelElevation(lowerLevel), upperDatum = levelElevation(upperLevel);
            double bottom = Destination(nextLowerLevel, levelElevation(nextLowerLevel)) + nextLowerOffset;
            double top = Destination(nextUpperLevel, levelElevation(nextUpperLevel)) + nextUpperOffset;
            // Revit 2020-2022 translate both edges with the host until an opening constraint regenerates them.
            if (translatesWithHost && !propertyChanged && !constraintMoved)
            {
                bottom = cut.Bottom + (nextWallDatum - wallDatum);
                top = cut.Top + (nextWallDatum - wallDatum);
            }
            int outline = header.Target(-1, "ElementHeader.m_pBBox", "Outline");
            int rectangle = opening.Target(-1, "OpeningBase.m_oRectOpeningData", "RectOpeningData");
            var savedHeight = Value(opening, id, BuiltInParameter.WALL_USER_HEIGHT_PARAM, StorageType.Double);
            var interval = new HeightInterval(cut.Bottom, savedHeight.Real, top - bottom) { NewBase = bottom };
            double savedSillShift = cut.Bottom - (lowerDatum + lowerOffset.Real);
            double savedHeadShift = cut.Top - (upperDatum + upperOffset.Real);
            if (savedSillShift != savedHeadShift || (!translatesWithHost && savedSillShift != 0)
                || opening.Real(rectangle, "RectOpeningData.m_aPoint@0/2") != cut.Bottom
                || opening.Real(rectangle, "RectOpeningData.m_aPoint@1/2") != cut.Top
                || opening.Real(rectangle, "RectOpeningData.m_lowerLeftCorner@1") != cut.Bottom - wallDatum
                || opening.Real(rectangle, "RectOpeningData.m_upperRightCorner@1") != cut.Top - wallDatum
                || interval.Top != cut.Top || interval.NewTop != top
                || header.Real(outline, "Outline.m_minmax@0/2") != interval.BoundsBase
                || header.Real(outline, "Outline.m_minmax@1/2") != cut.Top)
                throw Layout();
            var destination = CutAt(wallHeight.NewBase, wallHeight.NewTop, bottom, top);
            if (top - bottom <= VertexTolerance || destination == OpeningCut.Unverified)
                throw ParameterWriter.Unverified("Changing the rectangular opening would produce an unverified cut.");
            if (!FitsModelRadius(header, outline, interval.NewBoundsBase, top))
                throw new RvtWriteRefusedException("invalid-parameter-value", "The rectangular opening would extend beyond the verified model coordinate range.");

            if (!IsOutsideWall(cut.Cut))
            {
                sharedElevationMovesApart |= !AddElevation(cut.Bottom, bottom);
                sharedElevationMovesApart |= !AddElevation(cut.Top, top);
            }
            var resized = cut with { Bottom = bottom, Top = top, Destination = destination };
            if (openingHeights.Any(other => !OpeningCutsRemainSeparate(other.After, resized)))
                throw ParameterWriter.Unverified("Changing the rectangular opening would merge or touch another wall cut.");
            openingHeights.Add((cut, resized));
            _ = ResizeGeometry(geometry, interval, [], openingSolid: true);
            if (!propertyChanged && bottom == cut.Bottom && top == cut.Top && nextWallDatum == wallDatum)
                continue;

            if (nextLowerOffset != lowerOffset.Real)
                opening.Write(lowerOffset, nextLowerOffset);
            if (nextUpperOffset != upperOffset.Real)
                opening.Write(upperOffset, nextUpperOffset);
            if (nextLowerLevel != lowerLevel)
            {
                opening.WriteId(associatedLevel, nextLowerLevel, idWidth);
                opening.WriteId(lowerReference, nextLowerLevel, idWidth);
            }
            if (nextUpperLevel != upperLevel)
                opening.WriteId(upperReference, nextUpperLevel, idWidth);
            opening.Write(rectangle, "RectOpeningData.m_aPoint@0/2", bottom);
            opening.Write(rectangle, "RectOpeningData.m_aPoint@1/2", top);
            // The rectangle is relative to the host's base level, even when the opening has its own levels.
            opening.Write(rectangle, "RectOpeningData.m_lowerLeftCorner@1", bottom - nextWallDatum);
            opening.Write(rectangle, "RectOpeningData.m_upperRightCorner@1", top - nextWallDatum);
            opening.Write(savedHeight, interval.After);
            header.Write(outline, "Outline.m_minmax@0/2", interval.NewBoundsBase);
            header.Write(outline, "Outline.m_minmax@1/2", top);
            if (openingEdit?.ElementId == id)
            {
                headerBody = UpdateOpeningParents(header, opening, id, wallId, lowerLevel, upperLevel, nextLowerLevel, nextUpperLevel, idWidth);
                _ = read(headerBody, "ElementHeader", false);
            }
            _ = read(body, "SWallRectOpening", false);
            _ = read(geometryBody, "GElement", true);
            edits.AddRange([new(partition, HeaderChannel, (ulong)id, headerBody),
                new(partition, ElementChannel, (ulong)id, body), new(partition, GeometryChannel, (ulong)id, geometryBody)]);
        }
        bool hasBoundaryCut = openingHeights.Any(o =>
            o.Before.Cut != OpeningCut.Interior && !IsOutsideWall(o.Before.Cut)
            || o.After.Destination != OpeningCut.Interior && !IsOutsideWall(o.After.Destination)
            || o.Before.Cut != o.After.Destination);
        if (hasBoundaryCut && (edits.Count != 0 || wallHeight.Base != wallHeight.NewBase || wallHeight.Top != wallHeight.NewTop))
            return (wallHeight, null, edits, openingHeights.Select(o => o.After).ToArray());
        if (!sharedElevationMovesApart)
            return (wallHeight with { OpeningElevations = elevations }, null, edits, null);

        // Shared elevations have to be resolved through the original cutting faces.
        wallHeight = wallHeight with { OpeningElevationsRequiringOwner = elevations.Keys.ToHashSet() };
        var cuttingFaces = new Dictionary<int, HeightInterval>();
        foreach (var (before, after) in openingHeights)
        {
            if (IsOutsideWall(before.Cut))
                continue;
            var height = wallHeight with
            {
                OpeningElevations = new Dictionary<double, double> { [before.Bottom] = after.Bottom, [before.Top] = after.Top },
            };
            for (int side = 0; side < 4; side++)
            {
                long tag = wall.Integer(before.Step, $"WallRectOpeningGStep.m_OpeningFacesIds@{side}");
                if (tag <= 0 || tag >= int.MaxValue || !cuttingFaces.TryAdd((int)tag, height))
                    throw Layout();
            }
        }
        return (wallHeight, cuttingFaces, edits, null);

        (int Partition, byte[] Body) Record(long id, int channel) =>
            records.TryGetValue((id, channel), out var record) ? record : throw Layout();

        WideBlockWalker.Capture Value(Archive opening, long id, BuiltInParameter parameter, StorageType type) =>
            ParameterWriter.FindValue(walker, opening.Walk, new(id, (int)parameter, null), type)?.Value ?? throw Layout();

        double Destination(long id, double previous) => move is not { } movement || id != movement.Id ? previous
            : previous == movement.Previous ? movement.Destination : throw Layout();

        bool AddElevation(double before, double after) =>
            elevations.TryAdd(before, after) || elevations[before] == after;
    }

    internal static bool OpeningCutsRemainSeparate(OpeningInterval first, OpeningInterval second) =>
        second.Left - first.Right > VertexTolerance || first.Left - second.Right > VertexTolerance
        || second.Bottom - first.Top > VertexTolerance || first.Bottom - second.Top > VertexTolerance;

    private static byte[] UpdateOpeningParents(Archive header, Archive opening, long id, long wall,
        long lower, long upper, long nextLower, long nextUpper, int idWidth)
    {
        int parents = header.Target(-1, "ElementHeader.m_parents", "ElementParents");
        long phase = opening.Integer(-1, "Element.m_createdPhaseId");
        if (phase <= 0 || opening.Integer(-1, "Element.m_demolishedPhaseId") != -1
            || header.Walk.Captures.Any(c => c.QueueIndex == parents && c.DeclaringClass == "ElementParents"
                && c.Key.EndsWith('#') && c.Integer != 0 && c.Field is not ("m_deletion" or "m_appearanceParents"))
            || header.Walk.Captures.Any(c => c.DeclaringClass == "ElementRegenerationInfo" && c.Field == "m_otherElementId" && c.Integer > 0))
            throw Layout();

        long[] original = ParentIds(lower, upper);
        int start = header.Field(parents, "ElementParents.m_deletion#").ValueOffset;
        int end = header.Field(parents, "ElementParents.m_regenOnly#").ValueOffset;
        if (header.Integer(parents, "ElementParents.m_appearanceParents#") != 1
            || header.Integer(parents, "ElementParents.m_appearanceParents@0") != lower
            || !header.Matches(start, end, EncodeParents(original)))
            throw Layout();

        if (lower == nextLower && upper == nextUpper)
            return header.Body;
        if (lower != nextLower)
            header.WriteId(header.Field(parents, "ElementParents.m_appearanceParents@0"), nextLower, idWidth);
        return ParameterWriter.ReplaceValue(header.Body, start, end - start, EncodeParents(ParentIds(nextLower, nextUpper)));

        long[] ParentIds(long bottom, long top) => new[] { phase, wall, id, bottom, top }.Distinct().Order().ToArray();

        byte[] EncodeParents(long[] values)
        {
            byte[] encoded = new byte[sizeof(uint) + values.Length * idWidth];
            BinaryPrimitives.WriteUInt32LittleEndian(encoded, (uint)values.Length);
            for (int i = 0; i < values.Length; i++)
                UserParameterWriter.EncodeElementId(values[i], idWidth).CopyTo(encoded.AsSpan(sizeof(uint) + i * idWidth));
            return encoded;
        }
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
