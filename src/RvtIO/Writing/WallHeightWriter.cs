using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Writing;

internal static partial class WallHeightWriter
{
    private const int HeaderChannel = 101;
    private const int ElementChannel = 102;
    private const int GeometryChannel = 103;
    // This internal slot is distinct from the wall type's height parameter.
    private const int ComputedWallHeight = -1001101;
    private const int ComputedWallBaseOffset = -1001111;
    private const double MinimumVerifiedHeight = 0.1;
    // Revit's Application.VertexTolerance in internal feet.
    private const double VertexTolerance = 0.0005233832795;
    private const double MinimumNotchGap = VertexTolerance / 0.9;
    private const double ModelRadius = 10 * 5280;
    private const string All = WideBlockWalker.AllItemsKeyPrefix;

    internal static bool IsWallDimension(BuiltInParameter parameter) =>
        parameter is BuiltInParameter.WALL_USER_HEIGHT_PARAM or BuiltInParameter.WALL_BASE_OFFSET or BuiltInParameter.WALL_TOP_OFFSET;

    internal static ParameterWriter.PlanResult? Plan(RootStorage root, PartitionFraming.Tags tags, int idWidth,
        SchemaDocument schema, IReadOnlySet<long> elementIds, ParameterWriter.Edit edit, ParameterWriter.Pending pending)
    {
        var walker = new WideBlockWalker(schema, CaptureKey, []);
        var headerWalker = new WideBlockWalker(schema, (c, f) =>
            c.Name is "ElementParents" or "ElementRegenerationInfo" or "ClassDefinitionRef"
            || (c.Name, f.Name) is ("ElementHeader", "m_categroryId") ? CaptureKey(c, f) : null, []);
        var planWalker = new WideBlockWalker(schema, (c, f) =>
            (c.Name, f.Name) is ("AreaPlanTopologiesInterface", "m_pSet") or ("PlanTopologies", "m_set")
            ? CaptureKey(c, f) : null, []);
        var records = new Dictionary<int, (int Partition, byte[] Body)>();
        var openingRecords = new Dictionary<long, byte[]?>();
        bool offsetEdit = edit.Parameter != BuiltInParameter.WALL_USER_HEIGHT_PARAM;
        var levels = new Dictionary<long, byte[]?>();
        var globals = new HashSet<long>();
        var wallTypes = new HashSet<long>();
        var related = new HashSet<ulong>();
        var warnings = new List<byte[]>();
        ulong highestStoredOwner = 0;
        bool incompleteDependencies = false;
        bool hasSpatialContext = false;
        bool hasOtherWall = false;
        ulong owner = (ulong)edit.ElementId;
        ulong terminator = idWidth == sizeof(long) ? ulong.MaxValue : uint.MaxValue;
        ParameterWriter.ScanPartitions(root, tags, idWidth, (partition, record) =>
        {
            if (record.Block != 0 || record.Owner == terminator)
                return;
            if (record.Owner <= (idWidth == sizeof(int) ? (ulong)int.MaxValue : long.MaxValue))
                highestStoredOwner = Math.Max(highestStoredOwner, record.Owner);
            int classTag = record.Body.Length >= sizeof(ushort) ? BinaryPrimitives.ReadUInt16LittleEndian(record.Body.Span) : -1;
            string? name = walker.Class(classTag)?.Name;
            if (record.Owner == owner && !records.TryAdd(record.Channel, (partition, record.Body.ToArray())))
                throw ParameterWriter.Unverified("The wall has duplicate project records.");

            if (record.Channel == HeaderChannel && record.Owner != owner)
            {
                var header = headerWalker.Run(record.Body.ToArray(), 0, record.Body.Length, classTag);
                if (name != "ElementHeader" || !header.Exact)
                    incompleteDependencies = true;
                else
                {
                    long? category = UserParameterWriter.Integer(header, All + "ElementHeader.m_categroryId");
                    hasSpatialContext |= category is null or (int)BuiltInCategory.OST_Rooms or (int)BuiltInCategory.OST_MEPSpaces
                        or (int)BuiltInCategory.OST_Areas;
                    if (!hasOtherWall && category is null or (int)BuiltInCategory.OST_Walls)
                    {
                        long? elementClass = UserParameterWriter.Integer(header, All + "ClassDefinitionRef.m_ref");
                        hasOtherWall |= elementClass is not (>= 0 and <= ushort.MaxValue and long tag)
                            || !walker.IsA((int)tag, "WallType");
                    }
                    if (header.Captures.Any(c => c.Kind == WideBlockWalker.CaptureKind.Integer && c.Integer == edit.ElementId
                        && (c.DeclaringClass == "ElementParents" && c.Key.Contains('@')
                            || c.DeclaringClass == "ElementRegenerationInfo" && c.Field == "m_otherElementId")))
                        related.Add(record.Owner);
                }
            }
            if (record.Channel == ElementChannel)
            {
                hasOtherWall = hasOtherWall || (record.Owner != owner && (name is null || walker.IsA(classTag, "VWall")));
                hasSpatialContext |= name is null or "LevelRoomPlan" or "EnergyAnalysisSpaceElem";
                if (!hasSpatialContext && walker.IsA(classTag, "AreaPlanTopologiesInterface"))
                {
                    var body = record.Body.ToArray();
                    hasSpatialContext = !HasEmptyPlanTopologies(planWalker.Run(body, 0, body.Length, classTag), planWalker);
                }
            }
            if (record.Channel == ElementChannel && elementIds.Contains((long)record.Owner))
            {
                if (name == "PostedWarningElem")
                    warnings.Add(record.Body.ToArray());
                if (name == "ParamElemGlobal" && !globals.Add((long)record.Owner))
                    throw ParameterWriter.Unverified("A global parameter has duplicate project records.");
                if (name == "BasicWallType")
                    wallTypes.Add((long)record.Owner);
                if (offsetEdit && name == "Level" && !levels.TryAdd((long)record.Owner, record.Body.ToArray()))
                    levels[(long)record.Owner] = null;
                if (name == "SWallRectOpening" && !openingRecords.TryAdd((long)record.Owner, record.Body.ToArray()))
                    openingRecords[(long)record.Owner] = null;
            }
        }, pending);

        var wall = Read(ElementChannel, "SWall");
        var headerRecord = Read(HeaderChannel, "ElementHeader");
        if (wall.Integer(-1, "Element.m_id") != edit.ElementId
            || wall.Integer(-1, "Element.m_designOptionId") != -1
            || headerRecord.Integer(-1, "ElementHeader.m_designOptionId") != -1
            || headerRecord.Integer(-1, "ElementHeader.m_categroryId") != (int)BuiltInCategory.OST_Walls
            || !wallTypes.Contains(wall.Integer(-1, "VWall.m_WallAttributesId")))
            throw ParameterWriter.Unverified("The element is outside the verified basic wall context.");

        long topConstraint = wall.Integer(-1, "VWall.m_upToLevelId");
        if (!offsetEdit && topConstraint > 0)
            throw new RvtWriteRefusedException("read-only-parameter", "A level constrains the top of the wall.");
        if (edit.Parameter == BuiltInParameter.WALL_TOP_OFFSET && topConstraint == -1)
            throw new RvtWriteRefusedException("read-only-parameter", "The top of the wall has no level constraint.");
        GlobalParameterWriter.VerifyElementParameter(walker, wall.Walk, idWidth, elementIds, globals, edit.ParameterId);
        long roomBounding = wall.Integer(-1, "HostObj.m_roomBounding");
        if (topConstraint is 0 or < -1 || wall.Walk.Queue.Any(tag => walker.IsA(tag, "GroupMembership"))
            || roomBounding is not (0 or 1) || (roomBounding == 1 && hasSpatialContext)
            || wall.Integer(-1, "Element.m_constrInfo#") != 0
            || wall.Integer(-1, "VWall.m_embeddedTo#") != 0 || wall.Integer(-1, "VWall.m_isStructuralSignificant") != 0
            || incompleteDependencies)
            throw ParameterWriter.Unverified("The wall has dependencies that have not been verified for height changes.");

        int parents = headerRecord.Target(-1, "ElementHeader.m_parents", "ElementParents");
        if (headerRecord.Integer(parents, "ElementParents.m_hasNonDetermRegenChildren") != 0
            || headerRecord.Integer(parents, "ElementParents.m_nonDetermRegenChildren#") != 0)
            throw ParameterWriter.Unverified("The wall can regenerate unverified dependent elements.");

        double value = ParameterMeasurement.Parse(UnitType.UT_Length, edit.Value!) is double measured
            && (offsetEdit || measured >= MinimumVerifiedHeight)
            ? measured : throw new RvtWriteRefusedException("invalid-parameter-value", offsetEdit
                ? "A wall offset requires a finite length with an explicit unit."
                : "Unconnected Height requires an explicit unit and a length of at least 0.1 ft.");
        var original = ParameterWriter.FindValue(walker, wall.Walk, edit, StorageType.Double)?.Value
            ?? throw Layout();
        var mirror = ParameterWriter.FindValue(walker, wall.Walk, edit with { ParameterId = ComputedWallHeight }, StorageType.Double)?.Value
            ?? throw Layout();
        int outline = headerRecord.Target(-1, "ElementHeader.m_pBBox", "Outline");
        var interval = offsetEdit ? PlanOffset(wall, walker, edit, topConstraint, value, mirror, LevelElevation)
            : new HeightInterval(headerRecord.Real(outline, "Outline.m_minmax@0/2"), original.Real, value);
        if (mirror.Real != interval.Before || !double.IsFinite(interval.Top) || interval.Top <= interval.Base
            || headerRecord.Real(outline, "Outline.m_minmax@0/2") != interval.BoundsBase
            || !SameCoordinate(headerRecord.Real(outline, "Outline.m_minmax@1/2"), interval.Top))
            throw Layout();
        double x = Math.Max(Math.Abs(headerRecord.Real(outline, "Outline.m_minmax@0/0")),
            Math.Abs(headerRecord.Real(outline, "Outline.m_minmax@1/0")));
        double y = Math.Max(Math.Abs(headerRecord.Real(outline, "Outline.m_minmax@0/1")),
            Math.Abs(headerRecord.Real(outline, "Outline.m_minmax@1/1")));
        double z = Math.Max(Math.Abs(interval.NewBoundsBase), Math.Abs(interval.NewTop));
        if (interval.After < MinimumVerifiedHeight || interval.NewTop <= interval.NewBase || x * x + y * y + z * z > ModelRadius * ModelRadius)
            throw new RvtWriteRefusedException("invalid-parameter-value", "The wall would extend beyond the verified model coordinate range.");

        var openings = ReadOpenings(wall, walker, interval, edit.ElementId, related, openingRecords);
        var changes = openings.Where(o => o.Cut != o.Destination).ToArray();
        bool rebuildCut = changes.Length != 0 || openings.Any(o => IsAtBoundary(o.Cut) || IsAtBaseBoundary(o.Cut));
        bool changed = value != original.Real;
        bool bottomTransition = CanChangeBottomOpening(openings);
        bool removesCuts = CanRemoveOpeningCuts(openings);
        bool activatesCuts = CanActivateOpeningCuts(openings);
        bool replaysFloor = CanReplayBaseBoundaryCuts(openings);
        bool movesPastOpeningHead = changes.All(o =>
            o.Cut == OpeningCut.BelowWall && o.Destination is OpeningCut.Interior or OpeningCut.ThroughBottom
            || o.Destination == OpeningCut.BelowWall && o.Cut is OpeningCut.Interior or OpeningCut.ThroughBottom);
        if (changed && interval.NewBase != interval.Base && rebuildCut && !bottomTransition && !replaysFloor
            && !(movesPastOpeningHead && (removesCuts || activatesCuts)))
            throw ParameterWriter.Unverified("Moving the wall base would require an unverified opening rebuild.");
        bool replay = replaysFloor || CanReplayTopCuts(openings) || CanReplayBoundaryCuts(openings);
        if (changed && rebuildCut && !removesCuts && !activatesCuts && !replay
            && !bottomTransition && !CanChangeTopOpening(openings))
            throw ParameterWriter.Unverified("The opening topology change is outside the verified wall layout.");
        var geometry = Read(GeometryChannel, "GElement");
        if (!GElementArchiveHandles.IsCanonical(geometry.Walk))
            throw Layout();
        if (geometry.Integer(-1, idWidth == sizeof(long) ? "GRep.m_elementId" : "GInfo.m_tag") != edit.ElementId)
            throw Layout();
        ResizeReferencePlanes(wall, interval, openings.Count, hasOtherWall);
        byte[] faceTopology = ResizeGeometry(geometry, interval, openings);
        int[] topologyNodes = wall.Nodes("GeometryTopology").ToArray();
        if (topologyNodes.Length != 2)
            throw ParameterWriter.Unverified("The wall does not have both verified face topology tables.");
        var tables = new (int Start, int End)[topologyNodes.Length];
        for (int i = 0; i < topologyNodes.Length; i++)
        {
            int topology = topologyNodes[i];
            var start = wall.Field(topology, "GeometryTopology.m_faces#");
            var end = wall.Field(topology, "FaceBackupMgr.m_faceBackup#");
            if (wall.Integer(topology, "GeometryTopology.m_complete") != 1 || !wall.Matches(start.ValueOffset, end.ValueOffset, faceTopology))
                throw ParameterWriter.Unverified("The wall face topology does not match the cached solid.");
            tables[i] = (start.ValueOffset, end.ValueOffset);
        }
        wall.Write(original, value);
        wall.Write(mirror, interval.After);
        headerRecord.Write(outline, "Outline.m_minmax@0/2", interval.NewBoundsBase);
        headerRecord.Write(outline, "Outline.m_minmax@1/2", interval.NewTop);

        if (!changed)
            return null;
        if (rebuildCut)
        {
            var cutters = new List<(OpeningInterval Opening, Archive Geometry)>();
            double baseOffset = 0;
            if (activatesCuts || replay)
            {
                var sources = replay ? openings.Where(o => !IsOutsideWall(o.Destination)).ToArray() : changes;
                long[] openingIds = sources.Select(OpeningId).ToArray();
                baseOffset = ParameterWriter.FindValue(walker, wall.Walk,
                    edit with { ParameterId = (int)BuiltInParameter.WALL_BASE_OFFSET }, StorageType.Double)?.Value.Real ?? throw Layout();
                var openingGeometry = new Dictionary<long, Archive>();
                ParameterWriter.ScanPartitions(root, tags, idWidth, (_, record) =>
                {
                    long id = (long)record.Owner;
                    if (record.Block != 0 || record.Channel != GeometryChannel || !openingIds.Contains(id))
                        return;
                    var cutter = ReadBody(record.Body.ToArray(), "GElement", true);
                    if (!GElementArchiveHandles.IsCanonical(cutter.Walk)
                        || cutter.Integer(-1, idWidth == sizeof(long) ? "GRep.m_elementId" : "GInfo.m_tag") != id
                        || !openingGeometry.TryAdd(id, cutter))
                        throw Layout();
                }, pending);
                foreach (var (opening, id) in sources.Zip(openingIds))
                    cutters.Add((opening, openingGeometry.GetValueOrDefault(id) ?? throw Layout()));
            }

            var transition = new OpeningTransition(wall, changes.Length == 0 ? openings[0] : changes[0],
                removesCuts, activatesCuts || replay, replaysFloor);
            byte[] solid = transition.Rebuild(geometry, interval, openings, removesCuts, cutters, idWidth, baseOffset, replay);
            transition.ClearCutSteps(changes.Where(o => IsOutsideWall(o.Destination) && CutsHost(o.Cut)));
            var resized = ReadBody(solid, "GElement", true);
            if (!GElementArchiveHandles.IsCanonical(resized.Walk))
                throw Layout();
            var finalHeight = new HeightInterval(interval.NewBase, interval.After, interval.After) { EditsOffset = interval.EditsOffset };
            byte[] topology = ResizeGeometry(resized, finalHeight,
                openings.Select(o => o with { Cut = o.Destination }).ToArray());
            byte[] element = transition.Apply(topology, tables);
            _ = ReadBody(element, "SWall", false);
            var removedIds = changes.Where(o => CutsHost(o.Cut) && !CutsHost(o.Destination)).Select(OpeningId).ToArray();
            if (removedIds.Length != 0)
            {
                foreach (byte[] warning in warnings)
                {
                    var saved = ReadBody(warning, "PostedWarningElem", false);
                    if (saved.Walk.Captures.Any(c => c.DeclaringClass == "WallRectOpeningWarning" && c.Field == "m_elemIds"
                            && c.Key.Contains('@') && removedIds.Contains(c.Integer)))
                        throw Layout();
                }
                if (records.Values.Select(record => record.Partition).Distinct().Count() != 1)
                    throw Layout();
                WallOpeningWarnings.Stage(root, schema, idWidth, records[ElementChannel].Partition, edit.ElementId,
                    removedIds, highestStoredOwner, pending);
            }
            var activatedIds = changes.Where(o => !CutsHost(o.Cut) && CutsHost(o.Destination)).Select(OpeningId).ToArray();
            if (activatedIds.Length != 0)
                WallOpeningWarnings.StageRemoval(root, schema, tags, idWidth, edit.ElementId, activatedIds, pending);
            return new([Updated(HeaderChannel), Updated(ElementChannel, element), Updated(GeometryChannel, solid)], null);
        }
        return new([Updated(HeaderChannel), Updated(ElementChannel), Updated(GeometryChannel)], null);

        Archive Read(int channel, string expectedClass)
        {
            if (!records.TryGetValue(channel, out var record) || record.Body.Length < sizeof(ushort))
                throw Layout();
            return ReadBody(record.Body, expectedClass, channel == GeometryChannel);
        }

        double LevelElevation(long id)
        {
            if (!levels.TryGetValue(id, out var body) || body is null)
                throw ParameterWriter.Unverified("A wall constraint level does not have a unique project record.");
            var level = ReadBody(body, "Level", false);
            if (level.Integer(-1, "Element.m_id") != id || level.Integer(-1, "Element.m_designOptionId") != -1)
                throw Layout();
            int datum = level.Target(-1, "DatumPlane.m_pSurface", "Plane");
            return level.Real(datum, "Plane.m_origin@2");
        }

        Archive ReadBody(byte[] body, string expectedClass, bool traceReferences)
        {
            int tag = BinaryPrimitives.ReadUInt16LittleEndian(body);
            if (walker.Class(tag)?.Name != expectedClass)
                throw new RvtWriteRefusedException("unsupported-parameter-owner", "A wall dimension requires a verified straight basic wall.");
            var walk = walker.Run(body, 0, body.Length, tag, traceArchiveReferences: traceReferences);
            if (!walk.Exact || walk.EmptyEntityAt >= 0)
                throw Layout();
            return new(body, walk, walker);
        }

        RvtPartitionRecordEdit Updated(int channel, byte[]? body = null) =>
            new(records[channel].Partition, channel, owner, body ?? records[channel].Body);

        long OpeningId(OpeningInterval opening) => wall.Integer(opening.Step, "WallCutoutGStepBase.m_instId");
    }

    private static HeightInterval PlanOffset(Archive wall, WideBlockWalker walker, ParameterWriter.Edit edit,
        long topConstraint, double value, WideBlockWalker.Capture computedHeight, Func<long, double> levelElevation)
    {
        WideBlockWalker.Capture ReadDouble(int parameterId) =>
            ParameterWriter.FindValue(walker, wall.Walk, edit with { ParameterId = parameterId }, StorageType.Double)?.Value
            ?? throw Layout();

        var baseOffset = ReadDouble((int)BuiltInParameter.WALL_BASE_OFFSET);
        var baseMirror = ReadDouble(ComputedWallBaseOffset);
        if (baseOffset.Real != baseMirror.Real)
            throw Layout();

        double datum = levelElevation(wall.Integer(-1, "Element.m_assocLevelId"));
        double originalBase = datum + baseOffset.Real;
        bool moveBase = edit.Parameter == BuiltInParameter.WALL_BASE_OFFSET;
        double newBase = datum + (moveBase ? value : baseOffset.Real);
        double originalHeight, newHeight;
        if (topConstraint == -1)
        {
            originalHeight = ReadDouble((int)BuiltInParameter.WALL_USER_HEIGHT_PARAM).Real;
            newHeight = originalHeight;
        }
        else
        {
            double topDatum = levelElevation(topConstraint);
            double topOffset = ReadDouble((int)BuiltInParameter.WALL_TOP_OFFSET).Real;
            originalHeight = ConstrainedHeight(datum, baseOffset.Real, topDatum, topOffset);
            newHeight = ConstrainedHeight(datum, moveBase ? value : baseOffset.Real, topDatum, moveBase ? topOffset : value);
        }
        if (originalHeight != computedHeight.Real)
            throw Layout();
        if (moveBase)
            wall.Write(baseMirror, value);
        return new(originalBase, originalHeight, newHeight) { NewBase = newBase, EditsOffset = true };
    }

    // Revit removes the base offset before subtracting the base level's elevation.
    internal static double ConstrainedHeight(double baseElevation, double baseOffset, double topElevation, double topOffset) =>
        topElevation + topOffset - baseOffset - baseElevation;

    private static IReadOnlyList<OpeningInterval> ReadOpenings(Archive wall, WideBlockWalker walker, HeightInterval height,
        long owner, IReadOnlySet<ulong> related, IReadOnlyDictionary<long, byte[]?> openingRecords)
    {
        int[] steps = wall.Nodes("WallRectOpeningGStep").ToArray();
        var cutOwners = steps.Select(step => (ulong)wall.Integer(step, "WallCutoutGStepBase.m_instId")).ToHashSet();
        if (steps.Length != cutOwners.Count || !related.SetEquals(cutOwners))
            throw ParameterWriter.Unverified("The wall has dependencies that have not been verified for height changes.");
        if (steps.Length == 0)
            return [];

        int stepList = wall.Target(-1, "Element.m_geomSteps", "GeomStepList");
        int refFace = wall.Target(-1, "VWall.m_pRefFaces@0", "Face");
        int refPlane = wall.Target(refFace, "Face.m_pSurf", "Plane");
        double leftLimit = wall.Real(refPlane, "Envelope.m_corners@0/0");
        double rightLimit = wall.Real(refPlane, "Envelope.m_corners@1/0");
        var openings = new List<OpeningInterval>(steps.Length);
        for (int i = 0; i < steps.Length; i++)
        {
            int step = wall.Target(stepList, $"GeomStepList.m_bRepFormGList@{i + 1}", "WallRectOpeningGStep");
            long id = wall.Integer(step, "WallCutoutGStepBase.m_instId");
            if (step != steps[i] || id <= 0 || !openingRecords.TryGetValue(id, out var body) || body is null
                || wall.Integer(step, "WallCutoutGStepBase.m_bIsTrueBoolean") != 0
                || wall.Integer(step, "WallCutoutGStepBase.m_bUseUnattachedVoids") != 0
                || wall.Integer(step, "WallCutoutGStepBase.m_removedWholeGeometry") != 0
                || wall.Integer(step, "WallCutoutGStepBase.m_infillIdBefore") != -1
                || wall.Integer(step, "WallCutoutGStepBase.m_infillIdAfter") != -1
                || wall.Integer(step, "WallRectOpeningGStep.m_midEndsIds@0") != -1
                || wall.Integer(step, "WallRectOpeningGStep.m_midEndsIds@1") != -1)
                throw Layout();

            var walk = walker.Run(body, 0, body.Length, BinaryPrimitives.ReadUInt16LittleEndian(body));
            var opening = new Archive(body, walk, walker);
            if (!walk.Exact || walk.EmptyEntityAt >= 0 || opening.Integer(-1, "Element.m_id") != id
                || opening.Integer(-1, "OpeningBase.m_hostId") != owner || opening.Integer(-1, "OpeningBase.m_version") != 1
                || opening.Nodes("SWallRectOpeningGStep").ToArray() is not [var sourceStep]
                || opening.Integer(sourceStep, "GeomGenerator.m_id") != wall.Integer(step, "WallRectOpeningGStep.m_OpeningStepId"))
                throw Layout();

            int rectangle = opening.Target(-1, "OpeningBase.m_oRectOpeningData", "RectOpeningData");
            double left = opening.Real(rectangle, "RectOpeningData.m_lowerLeftCorner@0");
            double right = opening.Real(rectangle, "RectOpeningData.m_upperRightCorner@0");
            double z0 = opening.Real(rectangle, "RectOpeningData.m_aPoint@0/2");
            double z1 = opening.Real(rectangle, "RectOpeningData.m_aPoint@1/2");
            double bottom = Math.Min(z0, z1);
            double top = Math.Max(z0, z1);
            var cut = CutAt(height.Base, height.Top, bottom, top);
            var destination = CutAt(height.NewBase, height.NewTop, bottom, top);
            if (opening.Integer(rectangle, "RectOpeningData.m_nUvFaceId") != 0 || left <= leftLimit || right >= rightLimit
                || right <= left || cut == OpeningCut.Unverified || destination == OpeningCut.Unverified)
                throw ParameterWriter.Unverified("The opening may change topology at the requested height.");

            if (wall.Integer(step, "WallCutoutGStepBase.m_bCutSomething") != (CutsHost(cut) ? 1 : 0))
                throw Layout();
            for (int side = 0; side < 4; side++)
            {
                long face = wall.Integer(step, $"WallRectOpeningGStep.m_OpeningFacesIds@{side}");
                bool missing = !CutsHost(cut) || cut is OpeningCut.ThroughTop or OpeningCut.AtHead && side == 1
                    || cut is OpeningCut.ThroughBottom or OpeningCut.AtBaseSill && side == 0;
                if (missing ? face != -1 : face <= 0)
                    throw Layout();
            }
            openings.Add(new(step, left, right, bottom, top, cut, destination));
        }
        return openings;
    }

    internal enum OpeningCut { Unverified, Interior, ThroughTop, AboveWall, AtSill, AtHead, ThroughBottom, BelowWall, AtBaseSill, AtBaseHead }

    private static bool CutsHost(OpeningCut cut) =>
        cut is OpeningCut.Interior or OpeningCut.ThroughTop or OpeningCut.AtHead or OpeningCut.ThroughBottom or OpeningCut.AtBaseSill;

    private static bool IsAtBoundary(OpeningCut cut) => cut is OpeningCut.AtSill or OpeningCut.AtHead;

    private static bool IsAtBaseBoundary(OpeningCut cut) => cut is OpeningCut.AtBaseSill or OpeningCut.AtBaseHead;

    private static bool IsOutsideWall(OpeningCut cut) => cut is OpeningCut.AboveWall or OpeningCut.BelowWall;

    internal static bool CanReplayBoundaryCuts(IReadOnlyList<OpeningInterval> openings) =>
        openings.Count is 1 or 2 && SeparatedOpeningStations(openings)
        && openings.All(o => o.Cut is not (OpeningCut.Unverified or OpeningCut.ThroughBottom or OpeningCut.BelowWall)
            && o.Destination is not (OpeningCut.Unverified or OpeningCut.ThroughBottom or OpeningCut.BelowWall)
            && !IsAtBaseBoundary(o.Cut) && !IsAtBaseBoundary(o.Destination))
        && openings.Any(o => IsAtBoundary(o.Cut) || IsAtBoundary(o.Destination));

    internal static bool CanReplayBaseBoundaryCuts(IReadOnlyList<OpeningInterval> openings) =>
        openings.Count is 1 or 2 && SeparatedOpeningStations(openings)
        && openings.All(o => IsBaseLayout(o.Cut) && IsBaseLayout(o.Destination))
        && openings.Any(o => IsAtBaseBoundary(o.Cut) || IsAtBaseBoundary(o.Destination));

    private static bool IsBaseLayout(OpeningCut cut) =>
        cut is OpeningCut.Interior or OpeningCut.ThroughBottom or OpeningCut.BelowWall || IsAtBaseBoundary(cut);

    internal static bool CanChangeTopOpening(IReadOnlyList<OpeningInterval> openings) =>
        CanChangeOpening(openings, OpeningCut.ThroughTop);

    private static bool CanChangeBottomOpening(IReadOnlyList<OpeningInterval> openings) =>
        CanChangeOpening(openings, OpeningCut.ThroughBottom);

    private static bool CanChangeOpening(IReadOnlyList<OpeningInterval> openings, OpeningCut boundary)
    {
        if (openings.Count is < 1 or > 2 || openings.Count(o => o.Cut != o.Destination) != 1
            || openings.Any(o => o.Cut != OpeningCut.Interior && o.Cut != boundary
                || o.Destination != OpeningCut.Interior && o.Destination != boundary
                || o.Cut == boundary && o.Destination == boundary))
            return false;
        return SeparatedOpeningStations(openings);
    }

    internal static bool CanRemoveOpeningCuts(IReadOnlyList<OpeningInterval> openings) =>
        openings.Count is 1 or 2 && SeparatedOpeningStations(openings)
        && openings.All(o => o.Cut != OpeningCut.Unverified
            && (IsOutsideWall(o.Destination) && (!IsOutsideWall(o.Cut) || o.Cut == o.Destination)
                || o.Cut == OpeningCut.Interior && o.Destination == OpeningCut.Interior))
        && openings.Any(o => o.Cut != o.Destination);

    internal static bool CanActivateOpeningCuts(IReadOnlyList<OpeningInterval> openings)
    {
        if (openings.Count is < 1 or > 2 || !SeparatedOpeningStations(openings))
            return false;
        bool opensBottom = openings.Any(o => o.Cut == OpeningCut.BelowWall && o.Destination == OpeningCut.ThroughBottom);
        if (opensBottom && openings.Count(o => o.Cut != o.Destination) != 1)
            return false;
        return openings.All(o => (IsOutsideWall(o.Cut) || o.Cut == OpeningCut.Interior)
                && (o.Cut == o.Destination || IsOutsideWall(o.Cut)
                    && (o.Destination == OpeningCut.Interior
                        || o.Cut == OpeningCut.AboveWall && o.Destination == OpeningCut.ThroughTop
                        || o.Cut == OpeningCut.BelowWall && o.Destination == OpeningCut.ThroughBottom)))
            && openings.Any(o => o.Cut != o.Destination);
    }

    internal static bool CanReplayTopCuts(IReadOnlyList<OpeningInterval> openings) =>
        openings.Count == 2 && SeparatedOpeningStations(openings)
        && openings.All(o => o.Cut is not (OpeningCut.Unverified or OpeningCut.BelowWall)
            && o.Destination is not (OpeningCut.Unverified or OpeningCut.BelowWall))
        && (openings.All(o => o.Cut == OpeningCut.ThroughTop)
            || openings.All(o => o.Destination == OpeningCut.ThroughTop) && openings.Any(o => o.Cut != OpeningCut.AboveWall))
        && openings.Any(o => o.Destination != OpeningCut.AboveWall)
        && openings.Any(o => o.Cut != o.Destination);

    private static bool SeparatedOpeningStations(IReadOnlyList<OpeningInterval> openings) =>
        openings.Count == 1 || openings[0].Right < openings[1].Left || openings[1].Right < openings[0].Left;

    internal static OpeningCut CutAt(double wallBase, double wallTop, double openingBottom, double openingTop)
    {
        if (!double.IsFinite(openingBottom) || !double.IsFinite(openingTop) || openingTop <= openingBottom)
            return OpeningCut.Unverified;
        if (wallBase > openingTop + VertexTolerance)
            return OpeningCut.BelowWall;
        bool distinctContacts = openingTop - openingBottom > 2 * VertexTolerance;
        if (wallBase == openingTop || distinctContacts && WithinBaseContact(wallBase, openingTop))
            return OpeningCut.AtBaseHead;
        if ((wallBase == openingBottom || distinctContacts && WithinBaseContact(wallBase, openingBottom))
            && wallTop > openingTop + VertexTolerance)
            return OpeningCut.AtBaseSill;
        if (openingBottom < wallBase + MinimumVerifiedHeight)
        {
            if (wallTop <= openingTop + VertexTolerance)
                return OpeningCut.Unverified;
            if (wallBase < Math.BitDecrement(openingBottom - MinimumNotchGap))
                return OpeningCut.Interior;
            return wallBase > Math.BitIncrement(openingBottom + MinimumNotchGap)
                && wallBase < Math.BitDecrement(openingTop - MinimumNotchGap) ? OpeningCut.ThroughBottom : OpeningCut.Unverified;
        }
        double upperSill = openingBottom + VertexTolerance;
        double lowerHead = openingTop - VertexTolerance;
        if (wallTop < openingBottom - VertexTolerance)
            return OpeningCut.AboveWall;
        if (wallTop < upperSill)
            return OpeningCut.AtSill;
        if (wallTop > openingTop + VertexTolerance)
            return OpeningCut.Interior;
        if (wallTop >= lowerHead)
            return OpeningCut.AtHead;
        // At the upper sill limit, Revit can keep the opening dormant or cut through the top.
        if (wallTop <= Math.BitIncrement(upperSill) || wallTop >= Math.BitDecrement(openingTop - MinimumNotchGap))
            return OpeningCut.Unverified;
        return OpeningCut.ThroughTop;

        // Revit can retain an intermediate cut at the tolerance limit itself.
        static bool WithinBaseContact(double elevation, double openingEdge) =>
            elevation > Math.BitIncrement(openingEdge - VertexTolerance) && elevation < Math.BitDecrement(openingEdge + VertexTolerance);
    }

    internal readonly record struct OpeningInterval(int Step, double Left, double Right, double Bottom, double Top,
        OpeningCut Cut, OpeningCut Destination);

    internal static bool HasEmptyPlanTopologies(WideBlockWalker.Walk walk, WideBlockWalker walker)
    {
        if (!walk.Exact || walk.EmptyEntityAt >= 0 || walk.Captures.Where(c =>
            c.QueueIndex == -1 && c.Key == All + "AreaPlanTopologiesInterface.m_pSet").ToArray()
            is not [{ Kind: WideBlockWalker.CaptureKind.PointerClass, QueuePosition: >= 0 and int set, Integer: var tag }]
            || set >= walk.Queue.Count || walk.Queue[set] != tag || walker.Class((int)tag)?.Name != "PlanTopologies")
            return false;
        return UserParameterWriter.Integer(walk, All + "PlanTopologies.m_set#", set) == 0;
    }

    private static void ResizeReferencePlanes(Archive wall, HeightInterval height, int openingCount, bool hasOtherWall)
    {
        int driver = wall.Target(-1, "VWall.m_pCurveDriver", "VWallDriver");
        _ = wall.Target(driver, "CurveDriver.m_pCrv", "GLine");
        long startJoinDisabled = wall.Integer(driver, "VWallDriver.m_noJoinAtEnd@0");
        long endJoinDisabled = wall.Integer(driver, "VWallDriver.m_noJoinAtEnd@1");
        bool allowsEndJoins = startJoinDisabled == 0 || endJoinDisabled == 0;
        // Extending a wall into the elevation of another wall can create a join at unchanged plan endpoints.
        if (startJoinDisabled is not (0 or 1) || endJoinDisabled is not (0 or 1) || (allowsEndJoins && hasOtherWall)
            || wall.Integer(driver, "CurveDriver.m_sideJoins#") != 0 || wall.Integer(driver, "CurveDriver.m_controlJoinsSet#") != 0
            || wall.Integer(driver, "CurveDriver.m_sideAlignments#") != 0 || wall.Integer(-1, "VWall.m_pRefFaces#") != 4
            || wall.Integer(-1, "VWall.m_oDefiningFaceRefs") != 0 || wall.Integer(-1, "HostObj.m_oGeomToElemMap") != 0
            || wall.Integer(-1, "Element.m_dummy") != 0 || wall.Integer(-1, "Element.m_moribund") != 0)
            throw Layout();

        int steps = wall.Target(-1, "Element.m_geomSteps", "GeomStepList");
        if (wall.Integer(steps, "GeomStepList.m_bRepFormGList#") != 1L + openingCount || wall.Integer(steps, "GeomStepList.m_bRepAdjustGList#") != 1
            || wall.Integer(steps, "GeomStepList.m_bRepCutOutGList#") != 0 || wall.Integer(steps, "GeomStepList.m_bRepPostCutOutGList#") != 0
            || wall.Integer(steps, "GeomStepList.m_nonBRepGList#") != 1 || wall.Integer(steps, "GeomStepList.m_bRepTweakGList#") != 2)
            throw Layout();
        _ = wall.Target(steps, "GeomStepList.m_bRepFormGList@0", "BaseWallGStep");
        _ = wall.Target(steps, "GeomStepList.m_bRepAdjustGList@0", "VerticalExtensionOfLayersGStep");
        _ = wall.Target(steps, "GeomStepList.m_nonBRepGList@0", "WallRefPlanesGStep");
        int join = wall.Target(steps, "GeomStepList.m_bRepTweakGList@0", "WallJoinTweakGStep");
        if (allowsEndJoins && (wall.Integer(driver, "VWallDriver.m_noJoinAtMidEnds#") != 0
            || wall.Integer(join, "WallJoinTweakGStep.m_affectedEdgesTags#") != 0
            || wall.Integer(join, "WallJoinTweakGStep.m_idGeomJoined#") != 0
            || wall.Integer(join, "WallJoinTweakGStep.m_healingParents#") != 0))
            throw Layout();
        _ = wall.Target(steps, "GeomStepList.m_bRepTweakGList@1", "VCWSplitFaceGStep");
        if (wall.Nodes("GeometryTopology").Any(q => wall.Integer(q, "GBRep.m_pFaces#") != 0
                || wall.Integer(q, "Geometry.m_pEdges#") != 0
                || wall.Integer(q, "FaceBackupMgr.m_faceBackup#") != 0 || wall.Integer(q, "EdgeBackupMgr.m_edgeBackup#") != 0)
            || wall.Nodes("GElement").Any(q => wall.Integer(q, "GGroup.m_subNodes#") != 0))
            throw Layout();

        var planes = new HashSet<int>();
        for (int i = 0; i < 4; i++)
        {
            int face = wall.Target(-1, $"VWall.m_pRefFaces@{i}", "Face");
            int plane = wall.Target(face, "Face.m_pSurf", "Plane");
            var frame = ReadPlane(wall, plane, height, []);
            if (!planes.Add(plane) || frame != new PlaneFrame(1, 1, height.Base))
                throw Layout();
            ResizeEnvelope(wall, plane, frame, height, []);
            if (height.NewBase != height.Base)
                wall.Write(plane, "Plane.m_origin@2", height.NewBase);
        }
        if (!planes.SetEquals(wall.Nodes("Plane")))
            throw Layout();
    }

    private static byte[] ResizeGeometry(Archive geometry, HeightInterval height, IReadOnlyList<OpeningInterval> openings)
    {
        var openingLevels = openings.Where(o => !IsOutsideWall(o.Cut)).SelectMany(o => new[] { o.Bottom, o.Top }).ToHashSet();
        int interiorCount = openings.Count(o => o.Cut == OpeningCut.Interior);
        int boundaryCutCount = openings.Count(o => o.Cut is OpeningCut.ThroughTop or OpeningCut.ThroughBottom);
        int dormantContactCount = openings.Count(o => o.Cut is OpeningCut.AtSill or OpeningCut.AtBaseHead);
        int cuttingContactCount = openings.Count(o => o.Cut is OpeningCut.AtHead or OpeningCut.AtBaseSill);
        foreach (string field in new[] { "GRep.m_bBox", "GRep.m_tightbBox" })
        {
            if (geometry.Real(-1, field + "@0/2") != height.BoundsBase || !SameCoordinate(geometry.Real(-1, field + "@1/2"), height.Top))
                throw Layout();
            geometry.Write(-1, field + "@0/2", height.NewBoundsBase);
            geometry.Write(-1, field + "@1/2", height.NewTop);
        }
        if (geometry.Integer(-1, "GGroup.m_subNodes#") != 5)
            throw Layout();
        int solid = geometry.Target(-1, "GGroup.m_subNodes@4", "Geometry");
        var groups = new HashSet<int>();
        var solids = new HashSet<int> { solid };
        var solidFaces = new Dictionary<int, uint>();
        var solidEdges = new Dictionary<uint, int>();
        var physicalSolidFaces = new HashSet<int>();
        for (int i = 0; i < 4; i++)
        {
            int group = geometry.Target(-1, $"GGroup.m_subNodes@{i}", "GGroup");
            if (!groups.Add(group) || geometry.Integer(group, "GGroup.m_subNodes#") != 1
                || !solids.Add(geometry.Target(group, "GGroup.m_subNodes@0", "Geometry")))
                throw Layout();
        }
        foreach (int q in solids)
        {
            int faceCount = q == solid ? 6 + 6 * (interiorCount + cuttingContactCount) + 5 * (boundaryCutCount + dormantContactCount) : 1;
            int edgeCount = q == solid ? 12 + 20 * interiorCount + 38 * boundaryCutCount + 32 * cuttingContactCount : 4;
            if (geometry.Integer(q, "GBRep.m_pFaces#") != faceCount || geometry.Integer(q, "Geometry.m_pEdges#") != edgeCount
                || geometry.Integer(q, "Geometry.m_sharedSurfInfo#") != 0)
                throw Layout();
            ResizeGeometryControl(geometry, q, q == solid, height);
            for (int i = 0; i < faceCount; i++)
            {
                string field = $"GBRep.m_pFaces@{i}";
                int face = geometry.Target(q, field, "Face");
                if (!solidFaces.TryAdd(face, geometry.Handle(q, field)))
                    throw Layout();
                if (q == solid)
                    physicalSolidFaces.Add(face);
            }
            for (int i = 0; i < edgeCount; i++)
                if (!solidEdges.TryAdd(geometry.Handle(q, $"Geometry.m_pEdges@{i}"), geometry.Target(q, $"Geometry.m_pEdges@{i}", "Edge")))
                    throw Layout();
        }

        var facePlanes = solidFaces.Keys.ToDictionary(face => face, face => geometry.Target(face, "Face.m_pSurf", "Plane"));
        int planeCount = facePlanes.Values.Distinct().Count();
        if (planeCount != facePlanes.Count)
            throw Layout();
        var faces = solidFaces.ToDictionary(pair => pair.Value, pair => ReadPlane(geometry, facePlanes[pair.Key], height, openingLevels));
        bool project = height.NeedsEndpointProjection;
        uint reversedEnd = 0;
        var projectedSides = new HashSet<uint>();
        var auxiliaryFaces = new HashSet<uint>();
        if (project)
        {
            foreach (var (face, handle) in solidFaces)
                if (!physicalSolidFaces.Contains(face))
                    auxiliaryFaces.Add(handle);
            if (physicalSolidFaces.Select(face => solidFaces[face]).Where(handle =>
                    faces[handle] is { Axis: 1, Direction: -1 } frame && frame.Origin == height.Top).ToArray()
                is not [var end])
                throw ParameterWriter.Unverified("The offset would require projecting endpoints in an unverified wall shape.");
            reversedEnd = end;
            foreach (int edge in solidEdges.Values)
            {
                uint a = geometry.BackReference(edge, "GEdge.m_pFace@0");
                uint b = geometry.BackReference(edge, "GEdge.m_pFace@1");
                uint side = a == end ? b : b == end ? a : 0;
                if (faces.TryGetValue(side, out var frame) && frame == new PlaneFrame(1, 1, height.Base))
                    projectedSides.Add(side);
            }
            if (projectedSides.Count != 2)
                throw Layout();
        }
        var loops = new HashSet<int>();
        var fillings = new HashSet<int>();
        bool cutsThroughBase = openings.Any(o => o.Cut == OpeningCut.ThroughBottom);
        var faceTopology = new SortedDictionary<int, List<List<int>>>();
        foreach (var (face, handle) in solidFaces)
        {
            int plane = facePlanes[face];
            int loopOwner = face;
            string loopField = "GFace.m_pFirstLoop";
            int? loop = geometry.OptionalTarget(loopOwner, loopField, "EdgeLoop");
            var frame = faces[handle];
            if (loop is null && !openingLevels.Contains(frame.Origin)
                || geometry.Integer(face, "GFace.m_oBackgroundFilling") != 0)
                throw Layout();
            List<List<int>>? contours = null;
            if (loop is not null && physicalSolidFaces.Contains(face))
            {
                contours = [];
                if (!faceTopology.TryAdd(GeometryTag(geometry, face), contours))
                    throw Layout();
            }
            while (loop is int loopIndex)
            {
                if (!loops.Add(loopIndex) || geometry.BackReference(loopIndex, "GEdgeLoop.m_pFace") != handle
                    || geometry.Integer(loopIndex, "GEdgeLoop.m_open") != 0)
                    throw Layout();
                List<int>? contour = contours is null ? null : [];
                VerifyEdgeLoop(geometry, loopIndex, geometry.Handle(loopOwner, loopField), handle, solidEdges, contour);
                if (contour is not null)
                    contours!.Add(contour);
                ResizeSavedEnvelope(loopIndex, frame, handle);
                loopOwner = loopIndex;
                loopField = "GEdgeLoop.m_nextLoop";
                loop = geometry.OptionalTarget(loopOwner, loopField, "EdgeLoop");
            }
            ResizeSavedEnvelope(plane, frame, handle);

            double newOrigin = height.MoveEndpoint(frame.Origin);
            if (newOrigin != frame.Origin)
                geometry.Write(plane, "Plane.m_origin@2", newOrigin);
            if (geometry.OptionalTarget(face, "GFace.m_pGFilling", "GFilling") is int filling)
            {
                if (!fillings.Add(filling) || geometry.Integer(filling, "GFilling.m_pGFace") != handle
                    || geometry.Integer(filling, "GFilling.m_data") != 0)
                    throw Layout();
                if (newOrigin != frame.Origin && frame.Axis >= 0)
                {
                    var origin = geometry.Field(filling, $"FillPatternPlacer.m_origin@{frame.Axis}");
                    geometry.Write(origin, origin.Real - (newOrigin - frame.Origin) * frame.Direction);
                }
            }
        }
        if (groups.Count + solids.Count + solidFaces.Count + solidEdges.Count + planeCount + loops.Count + fillings.Count != geometry.Walk.Queue.Count)
            throw Layout();

        foreach (int edge in solidEdges.Values)
        {
            if (geometry.Integer(edge, "GEdge.m_interiorEdgePnts#") != 0)
                throw Layout();
            for (int side = 0; side < 2; side++)
            {
                uint handle = geometry.BackReference(edge, $"GEdge.m_pFace@{side}");
                if (handle == 0)
                    continue;
                if (!faces.TryGetValue(handle, out var frame))
                    throw Layout();
                if (frame.Axis < 0)
                    continue;
                if (geometry.Values(edge, $"EdgePnt.uv@{side}/{frame.Axis}").ToArray() is not [var first, var last])
                    throw Layout();
                if (project)
                {
                    uint other = geometry.BackReference(edge, $"GEdge.m_pFace@{1 - side}");
                    bool projectsBase = projectedSides.Contains(handle) && other == reversedEnd;
                    double oldBase = projectsBase ? height.ProjectedBase : height.Base;
                    double newBase = projectsBase ? height.NewProjectedBase : height.NewBase;
                    bool cutoff = cutsThroughBase && openingLevels.Contains(frame.Origin)
                        && faces.TryGetValue(other, out var otherFrame) && otherFrame.Axis >= 0 && openingLevels.Contains(otherFrame.Origin);
                    ResizeProjectedUv(geometry, first, frame, height, oldBase, newBase, auxiliaryFaces.Contains(handle), openingLevels, cutoff);
                    ResizeProjectedUv(geometry, last, frame, height, oldBase, newBase, auxiliaryFaces.Contains(handle), openingLevels, cutoff);
                }
                else
                {
                    ResizeUv(geometry, first, frame, height, openingLevels);
                    ResizeUv(geometry, last, frame, height, openingLevels);
                }
            }
        }
        return EncodeFaceTopology(faceTopology);

        void ResizeSavedEnvelope(int index, PlaneFrame frame, uint handle)
        {
            if (!project)
            {
                ResizeEnvelope(geometry, index, frame, height, openingLevels);
                return;
            }
            if (frame.Axis < 0)
                return;
            double oldBase = projectedSides.Contains(handle) ? height.BoundsBase : height.Base;
            double newBase = projectedSides.Contains(handle) ? height.NewBoundsBase : height.NewBase;
            bool auxiliary = auxiliaryFaces.Contains(handle);
            ResizeProjectedUv(geometry, geometry.Field(index, $"Envelope.m_corners@0/{frame.Axis}"), frame, height, oldBase, newBase, auxiliary, openingLevels);
            ResizeProjectedUv(geometry, geometry.Field(index, $"Envelope.m_corners@1/{frame.Axis}"), frame, height, oldBase, newBase, auxiliary, openingLevels);
        }
    }

    private static void ResizeGeometryControl(Archive geometry, int solid, bool physical, HeightInterval height)
    {
        const int hasControlCommand = 0x4000;
        var x = Bounds(0);
        var y = Bounds(1);
        var command = EncodeGeometryControl(x, y, (physical ? height.NewBoundsBase : height.NewBase, height.NewTop));
        int flags = geometry.Int32(solid, "GInfo.m_flags");
        geometry.WriteInt32(solid, "GInfo.m_controlCommand", unchecked((int)command.GetValueOrDefault()));
        geometry.WriteInt32(solid, "GInfo.m_flags", command.HasValue ? flags | hasControlCommand : flags & ~hasControlCommand);

        (double Min, double Max) Bounds(int axis)
        {
            if (physical)
                return (geometry.Real(-1, $"GRep.m_tightbBox@0/{axis}"), geometry.Real(-1, $"GRep.m_tightbBox@1/{axis}"));

            // Each auxiliary solid is a single planar face. Use its own footprint, which can lie
            // in a different horizontal cell from the physical wall.
            int face = geometry.Target(solid, "GBRep.m_pFaces@0", "Face");
            int plane = geometry.Target(face, "Face.m_pSurf", "Plane");
            double origin = geometry.Real(plane, $"Plane.m_origin@{axis}");
            double u = geometry.Real(plane, $"Plane.m_xVec@{axis}");
            double v = geometry.Real(plane, $"Plane.m_yVec@{axis}");
            double u0 = u * geometry.Real(plane, "Envelope.m_corners@0/0");
            double u1 = u * geometry.Real(plane, "Envelope.m_corners@1/0");
            double v0 = v * geometry.Real(plane, "Envelope.m_corners@0/1");
            double v1 = v * geometry.Real(plane, "Envelope.m_corners@1/1");
            return (origin + Math.Min(u0, u1) + Math.Min(v0, v1), origin + Math.Max(u0, u1) + Math.Max(v0, v1));
        }
    }

    internal static uint? EncodeGeometryControl((double Min, double Max) x, (double Min, double Max) y,
        (double Min, double Max) z)
    {
        int? xCode = EncodeAxis(x, 254, 8);
        int? yCode = EncodeAxis(y, 254, 8);
        int? zCode = EncodeAxis(z, 30, 4);
        // An unencodable axis omits the entire command. A command can also encode to the value zero.
        return xCode is int a && yCode is int b && zCode is int c ? (uint)a | (uint)b << 12 | (uint)c << 24 : null;

        static int? EncodeAxis((double Min, double Max) bounds, int largestCenter, int signBit)
        {
            const double padding = 1e-9;
            if (!double.IsFinite(bounds.Min) || !double.IsFinite(bounds.Max) || bounds.Min > bounds.Max)
                throw Layout();

            double midpoint = bounds.Min * 0.5 + bounds.Max * 0.5;
            double lower = bounds.Min - padding;
            double upper = bounds.Max + padding;
            double step = 4;
            // Start with a four-foot cell and quadruple its size while the padded bounds do not fit.
            for (int scale = 0; scale < signBit; scale++, step *= 4)
            {
                double index = Math.Abs(midpoint) / step;
                double magnitude = Math.Truncate(index);
                if (index - magnitude > 0.5)
                    magnitude++;
                int cell = (int)Math.Min(magnitude, largestCenter);
                double center = (midpoint < 0 ? -cell : cell) * step;
                double distance = Math.Max(center - lower, upper - center);
                if (distance < step || (scale == signBit - 1 && distance == step))
                    return cell * (signBit * 2) | (midpoint < 0 ? signBit : 0) | scale;
            }
            return null;
        }
    }

    private static void VerifyEdgeLoop(Archive geometry, int loop, uint loopHandle, uint faceHandle,
        IReadOnlyDictionary<uint, int> edges, List<int>? contour)
    {
        uint previous = loopHandle;
        uint current = geometry.BackReference(loop, "GEdgeLoop.m_next");
        int traversed = 0;
        while (current != loopHandle)
        {
            if (traversed++ >= edges.Count || !edges.TryGetValue(current, out int edge))
                throw Layout();
            bool first = geometry.BackReference(edge, "GEdge.m_pFace@0") == faceHandle;
            bool second = geometry.BackReference(edge, "GEdge.m_pFace@1") == faceHandle;
            int side = first ? 0 : 1;
            if (first == second || geometry.BackReference(edge, $"GEdge.m_prev@{side}") != previous)
                throw Layout();
            if (contour is not null)
            {
                int signedTag = GeometryTag(geometry, edge) + 1;
                contour.Add(first ? signedTag : -signedTag);
            }
            previous = current;
            current = geometry.BackReference(edge, $"GEdge.m_next@{side}");
        }
        if (traversed < 3 || previous != geometry.BackReference(loop, "GEdgeLoop.m_prev"))
            throw Layout();
    }

    private static int GeometryTag(Archive geometry, int node) => geometry.Integer(node, "GInfo.m_tag") is >= 0 and < int.MaxValue and var tag
        ? (int)tag : throw Layout();

    internal static byte[] EncodeFaceTopology(SortedDictionary<int, List<List<int>>> faces)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        writer.Write(faces.Count);
        foreach (var (tag, contours) in faces)
        {
            writer.Write(tag);
            writer.Write(contours.Sum(contour => 1 + contour.Count));
            foreach (var contour in contours)
            {
                writer.Write(contour.Count);
                foreach (int edge in contour)
                    writer.Write(edge);
            }
        }
        return buffer.ToArray();
    }

    private readonly record struct HeightInterval(double Base, double Before, double After)
    {
        internal double Top => Base + Before;
        internal double NewBase { get; init; } = Base;
        internal double NewTop => NewBase + After;
        internal bool EditsOffset { get; init; }
        // The end plane facing down from the top reprojects its lower vertices onto adjoining
        // sides. That round trip can move the stored lower bound by a floating-point rounding step.
        internal double ProjectedBase => Top - (Top - Base);
        internal double NewProjectedBase => NewTop - (NewTop - NewBase);
        internal double BoundsBase => EditsOffset ? Math.Min(Base, ProjectedBase) : Base;
        internal double NewBoundsBase => EditsOffset ? Math.Min(NewBase, NewProjectedBase) : NewBase;
        internal bool NeedsEndpointProjection => EditsOffset && (Top - Base != Before || NewTop - NewBase != After
            || ProjectedBase != Base || NewProjectedBase != NewBase);
        internal double MoveEndpoint(double position) => position == Base ? NewBase : position == Top ? NewTop : position;
    }

    private readonly record struct PlaneFrame(int Axis, double Direction, double Origin);

    private static PlaneFrame ReadPlane(Archive record, int plane, HeightInterval height, IReadOnlyCollection<double> openingLevels)
    {
        double origin = record.Real(plane, "Plane.m_origin@2");
        if (SameCoordinate(origin, height.Top))
            origin = height.Top;
        double u = record.Real(plane, "Plane.m_xVec@2");
        double v = record.Real(plane, "Plane.m_yVec@2");
        if (origin != height.Base && origin != height.Top && !openingLevels.Contains(origin)
            || u is not (-1 or 0 or 1) || v is not (-1 or 0 or 1) || u != 0 && v != 0)
            throw Layout();
        return u != 0 ? new(0, u, origin) : new(v != 0 ? 1 : -1, v, origin);
    }

    private static void ResizeEnvelope(Archive record, int queueIndex, PlaneFrame plane, HeightInterval height,
        IReadOnlyCollection<double> openingLevels)
    {
        if (plane.Axis < 0)
            return;
        ResizeUv(record, record.Field(queueIndex, $"Envelope.m_corners@0/{plane.Axis}"), plane, height, openingLevels);
        ResizeUv(record, record.Field(queueIndex, $"Envelope.m_corners@1/{plane.Axis}"), plane, height, openingLevels);
    }

    private static void ResizeUv(Archive record, WideBlockWalker.Capture point, PlaneFrame plane, HeightInterval height,
        IReadOnlyCollection<double> openingLevels)
    {
        if (point.Kind != WideBlockWalker.CaptureKind.Real)
            throw Layout();
        // Keep the stored length exact in the two frames anchored at the wall ends.
        double endDirection = plane.Origin == height.Base ? plane.Direction : plane.Origin == height.Top ? -plane.Direction : 0;
        if (endDirection != 0 && SameCoordinate(point.Real, endDirection * height.Before))
        {
            if (height.After != height.Before)
                record.Write(point, endDirection * height.After);
            return;
        }
        double worldZ = plane.Origin + point.Real * plane.Direction;
        bool movesWithTop = SameCoordinate(worldZ, height.Top);
        if (worldZ != height.Base && !movesWithTop && !openingLevels.Contains(worldZ))
            throw Layout();
        double destinationZ = movesWithTop ? height.NewTop : height.MoveEndpoint(worldZ);
        double destinationOrigin = height.MoveEndpoint(plane.Origin);
        if (destinationZ != worldZ || destinationOrigin != plane.Origin)
        {
            double coordinate = (destinationZ - destinationOrigin) * plane.Direction;
            if (coordinate != point.Real)
                record.Write(point, coordinate);
        }
    }

    private static void ResizeProjectedUv(Archive record, WideBlockWalker.Capture point, PlaneFrame frame, HeightInterval height,
        double oldBase, double newBase, bool auxiliary, IReadOnlyCollection<double> openingLevels, bool cutoff = false)
    {
        if (point.Kind != WideBlockWalker.CaptureKind.Real)
            throw Layout();
        // An auxiliary face can project the upper vertex one world-coordinate rounding step away from the physical solid.
        double world = frame.Origin + point.Real * frame.Direction;
        double destination;
        if (point.Real == (oldBase - frame.Origin) * frame.Direction)
            destination = newBase;
        else if (point.Real == (height.Top - frame.Origin) * frame.Direction || auxiliary && SameCoordinate(world, height.Top))
            destination = height.NewTop;
        else
            destination = MatchProjectedOpeningLevel(point.Real, frame.Origin, frame.Direction, openingLevels)
                // Split edges between vertical cutter faces can round at the base by one world-coordinate step.
                ?? (cutoff && SameCoordinate(world, oldBase) ? newBase : throw Layout());
        double coordinate = (destination - height.MoveEndpoint(frame.Origin)) * frame.Direction;
        if (coordinate != point.Real)
            record.Write(point, coordinate);
    }

    internal static double? MatchProjectedOpeningLevel(double coordinate, double origin, double direction,
        IReadOnlyCollection<double> openingLevels)
    {
        double? match = null;
        foreach (double level in openingLevels)
        {
            if (coordinate != (level - origin) * direction)
                continue;
            if (match.HasValue)
                throw Layout();
            match = level;
        }
        return match;
    }

    // Revit can round a cut's top coordinate to the adjacent double when projecting it onto a face.
    private static bool SameCoordinate(double actual, double expected) =>
        actual == expected || actual == Math.BitIncrement(expected) || actual == Math.BitDecrement(expected);

    private static string CaptureKey(SchemaClass declaring, SchemaField field) =>
        GlobalParameterWriter.CaptureKey(declaring, field) ?? All + declaring.Name + "." + field.Name;

    private static RvtWriteRefusedException Layout() =>
        ParameterWriter.Unverified("The wall height or dependent geometry does not have a verified layout.");

    private sealed class Archive(byte[] body, WideBlockWalker.Walk walk, WideBlockWalker walker)
    {
        private readonly ILookup<(int Queue, string Key), WideBlockWalker.Capture> _fields =
            walk.Captures.ToLookup(c => (c.QueueIndex, c.Key));

        internal WideBlockWalker.Walk Walk => walk;
        internal byte[] Body => body;

        internal bool Matches(int start, int end, ReadOnlySpan<byte> expected) =>
            start >= sizeof(ushort) && end >= start && end < body.Length && body.AsSpan(start, end - start).SequenceEqual(expected);

        internal IEnumerable<int> Nodes(string name) => Enumerable.Range(0, walk.Queue.Count)
            .Where(index => walker.Class(walk.Queue[index])?.Name == name);

        internal IEnumerable<WideBlockWalker.Capture> Values(int queue, string key) => _fields[(queue, All + key)];

        internal WideBlockWalker.Capture Field(int queue, string key) =>
            Values(queue, key).ToArray() is [var field] ? field : throw Layout();

        internal long Integer(int queue, string key) => Field(queue, key) is
            { Kind: WideBlockWalker.CaptureKind.Integer or WideBlockWalker.CaptureKind.PointerClass, Integer: var value } ? value : throw Layout();

        internal int Int32(int queue, string key) => Integer(queue, key) is >= int.MinValue and <= int.MaxValue and var value
            ? (int)value : throw Layout();

        internal void WriteInt32(int queue, string key, int value)
        {
            if (Field(queue, key) is not { Kind: WideBlockWalker.CaptureKind.Integer, ValueOffset: >= sizeof(ushort) and int offset }
                || offset > body.Length - sizeof(int))
                throw Layout();
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(offset), value);
        }

        internal double Real(int queue, string key) => Field(queue, key) is
            { Kind: WideBlockWalker.CaptureKind.Real, Real: var value } && double.IsFinite(value) ? value : throw Layout();

        internal int Target(int queue, string key, string expectedClass) =>
            OptionalTarget(queue, key, expectedClass) ?? throw Layout();

        internal uint Handle(int queue, string key)
        {
            var field = Field(queue, key);
            if (field is not { Kind: WideBlockWalker.CaptureKind.PointerClass, Integer: > 0, ValueOffset: >= sizeof(ushort) }
                || field.ValueOffset > body.Length - sizeof(uint))
                throw Layout();
            return BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(field.ValueOffset));
        }

        internal uint BackReference(int queue, string key) => Field(queue, key) is
            { Kind: WideBlockWalker.CaptureKind.Integer, ValueOffset: >= sizeof(ushort), Integer: >= 0 and <= uint.MaxValue and var value }
                ? (uint)value : throw Layout();

        internal int? OptionalTarget(int queue, string key, string expectedClass)
        {
            var field = Field(queue, key);
            if (field is { Kind: WideBlockWalker.CaptureKind.PointerClass, Integer: 0 })
                return null;
            if (field is not { Kind: WideBlockWalker.CaptureKind.PointerClass, Integer: > 0, QueuePosition: >= 0 }
                || field.QueuePosition >= walk.Queue.Count || walk.Queue[field.QueuePosition] != field.Integer
                || walker.Class((int)field.Integer)?.Name != expectedClass)
                throw Layout();
            return field.QueuePosition;
        }

        internal void Write(int queue, string key, double value) => Write(Field(queue, key), value);

        internal void Write(WideBlockWalker.Capture field, double value)
        {
            if (field.Kind != WideBlockWalker.CaptureKind.Real || field.ValueOffset < sizeof(ushort)
                || field.ValueOffset > body.Length - sizeof(double) || !double.IsFinite(value))
                throw Layout();
            BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(field.ValueOffset), value);
        }
    }
}
