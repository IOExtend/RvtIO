using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Writing;

internal static partial class LevelElevationWriter
{
    private const string All = WideBlockWalker.AllItemsKeyPrefix;
    private const double ElevationTolerance = 1e-9;

    internal static ParameterWriter.PlanResult? Plan(RootStorage root, PartitionFraming.Tags tags, int idWidth,
        SchemaDocument schema, IReadOnlySet<long> elementIds, ParameterWriter.Edit edit, ParameterWriter.Pending pending)
    {
        if (ParameterMeasurement.Parse(UnitType.UT_Length, edit.Value!) is not double elevation)
            throw new RvtWriteRefusedException("invalid-parameter-value", "Level elevation requires a finite length with units.");

        var walker = new WideBlockWalker(schema, (type, field) => type.Name switch
        {
            "Element" or "Level" or "LevelAttributes" or "DatumPlane" or "Face" or "Plane" or "Instance"
                or "Trf" or "ActiveGeoLocationTrackingElement" or "DBView" or "DBViewPlan" or "PlanViewRange" or "PlanViewRange2"
                or "GenericPlaneCutter" or "SketchPlane" or "SketchGrid" or "OneElementMovablePlaneRef" or "SunAndShadowSettings"
                or "EnergyDataSettings" or "OpeningBase" or "ParamValueElementId" => All + type.Name + "." + field.Name,
            _ => GlobalParameterWriter.CaptureKey(type, field),
        }, []);
        var headerWalker = new WideBlockWalker(schema, (type, field) => type.Name is
            "ElementHeader" or "ElementParents" or "ElementRegenerationInfo" or "ClassDefinitionRef"
            ? All + type.Name + "." + field.Name : null, []);
        var target = new Dictionary<int, (int Partition, byte[] Body)>();
        var headers = new Dictionary<long, DependencyHeader>();
        var globals = new HashSet<long>();
        var context = new Dictionary<long, byte[]>();
        long? trackerId = null;
        bool hasAnalyticalGeometry = false;
        var dependents = new List<long>();
        var walls = new HashSet<long>();
        var planRecords = new Dictionary<long, (int Partition, byte[] Body)>();
        var geometryPartitions = new Dictionary<long, int>();
        ParameterWriter.ScanPartitions(root, tags, idWidth, (partition, record) =>
        {
            long owner = (long)record.Owner;
            if (record.Block != 0 || !elementIds.Contains(owner))
                return;
            int tag = record.Body.Length >= sizeof(ushort) ? BinaryPrimitives.ReadUInt16LittleEndian(record.Body.Span) : -1;
            string? name = walker.Class(tag)?.Name;
            if (owner == edit.ElementId && !target.TryAdd(record.Channel, (partition, record.Body.ToArray())))
                throw ParameterWriter.Unverified("The level has duplicate project records.");
            if (record.Channel == 101)
            {
                if (name != "ElementHeader")
                    throw ParameterWriter.Unverified("A project element has an unverified header.");
                byte[] body = record.Body.ToArray();
                var header = headerWalker.Run(body, 0, body.Length, tag);
                if (!header.Exact)
                    throw ParameterWriter.Unverified("A project header could not be read exactly to check level dependencies.");
                long[] parents = header.Captures.Where(c => c.Kind == WideBlockWalker.CaptureKind.Integer
                    && c.Integer > 0 && (c.DeclaringClass == "ElementParents" && c.Key.Contains('@')
                        || c.DeclaringClass == "ElementRegenerationInfo" && c.Field == "m_otherElementId"))
                    .Select(c => c.Integer).Distinct().ToArray();
                if (!headers.TryAdd(owner, new(partition, Integer(header, "ElementHeader.m_categroryId"),
                    Integer(header, "ElementHeader.m_designOptionId") == -1 && Integer(header, "ElementHeader.m_pBBox") == 0, parents)))
                    throw ParameterWriter.Unverified("A project element has duplicate headers.");
                if (owner != edit.ElementId && parents.Contains(edit.ElementId))
                    dependents.Add(owner);
            }
            if (record.Channel == 103)
            {
                int geometryPartition = name == "SerializedDummy" && record.Body.Length == sizeof(ushort) ? partition : -1;
                if (!geometryPartitions.TryAdd(owner, geometryPartition))
                    geometryPartitions[owner] = -1;
            }
            if (record.Channel != 102)
                return;
            if (name == "SWall")
                walls.Add(owner);
            hasAnalyticalGeometry |= name is "EnergyAnalysisDetailModel" or "EnergyAnalysisSpaceElem" or "EnergyAnalysisSurface";
            if (name is ("Level" or "DBViewPlan" or "SketchPlane" or "SketchGrid" or "SunAndShadowSettings" or "EnergyDataSettings" or "SWallRectOpening")
                && !planRecords.TryAdd(owner, (partition, record.Body.ToArray())))
                throw ParameterWriter.Unverified("A level dependency has duplicate project records.");
            if (name == "ParamElemGlobal" && !globals.Add(owner))
                throw ParameterWriter.Unverified("A global parameter has duplicate project records.");
            if (name is "LevelAttributes" or "GeoLocation" or "ActiveGeoLocationTrackingElement")
            {
                if (!context.TryAdd(owner, record.Body.ToArray()) || (name == "ActiveGeoLocationTrackingElement" && trackerId is not null))
                    throw ParameterWriter.Unverified("The level has ambiguous type or project coordinate records.");
                if (name == "ActiveGeoLocationTrackingElement")
                    trackerId = owner;
            }
        }, pending);

        if (!target.TryGetValue(102, out var element) || Class(element.Body) != "Level")
            throw new RvtWriteRefusedException("unsupported-parameter-owner", "A level elevation edit requires a project level.");
        if (headers.Count != elementIds.Count)
            throw ParameterWriter.Unverified("The level's dependent elements require verification before changing its elevation.");
        if (target.Count != 3 || !target.TryGetValue(101, out var head) || !target.TryGetValue(103, out var geometry)
            || head.Partition != element.Partition || geometry.Partition != element.Partition
            || Class(geometry.Body) != "SerializedDummy"
            || !walker.Run(geometry.Body, 0, geometry.Body.Length, BinaryPrimitives.ReadUInt16LittleEndian(geometry.Body)).Exact)
            throw ParameterWriter.Unverified("The level does not have the expected header and geometry records.");
        var ownHeader = headerWalker.Run(head.Body, 0, head.Body.Length, BinaryPrimitives.ReadUInt16LittleEndian(head.Body));
        var level = Read(element.Body, "Level", edit.ElementId);
        if (Integer(ownHeader, "ElementHeader.m_categroryId") != (int)BuiltInCategory.OST_Levels
            || Integer(ownHeader, "ElementHeader.m_designOptionId") != -1
            || Integer(ownHeader, "ElementHeader.m_pBBox") != 0
            || Integer(level, "Element.m_designOptionId") != -1
            || level.Queue.Any(tag => walker.IsA(tag, "GroupMembership")))
            throw ParameterWriter.Unverified("The level is outside the verified main model context.");
        GlobalParameterWriter.VerifyElementParameter(walker, level, idWidth, elementIds, globals, edit.ParameterId);

        long typeId = Integer(level, "Level.m_attrId") ?? 0;
        var type = ReadContext(typeId, "LevelAttributes");
        long? elevationBase = Integer(type, "LevelAttributes.m_baseElevation");
        if (elevationBase is not (0 or 1) || trackerId is not long tracker)
            throw ParameterWriter.Unverified("The level's elevation base could not be identified.");
        var tracking = ReadContext(tracker, "ActiveGeoLocationTrackingElement");
        string locationField = elevationBase == 0 ? "m_projectGeoLocationId" : "m_activeGeoLocationId";
        var location = ReadContext(Integer(tracking, "ActiveGeoLocationTrackingElement." + locationField) ?? 0, "GeoLocation");
        int info = Target(location, -1, "Instance.m_pInstanceInfo", "InstanceInfo");
        if (Real(location, info, "Trf.m_3x3@0/2").Real != 0 || Real(location, info, "Trf.m_3x3@1/2").Real != 0
            || Real(location, info, "Trf.m_3x3@2/0").Real != 0 || Real(location, info, "Trf.m_3x3@2/1").Real != 0
            || Real(location, info, "Trf.m_3x3@2/2").Real != 1)
            throw ParameterWriter.Unverified("The project elevation transform does not preserve a vertical axis.");
        double origin = Real(location, info, "Trf.m_or@2").Real;
        double destination = elevation + origin;
        if (!double.IsFinite(destination))
            throw new RvtWriteRefusedException("invalid-parameter-value", "The elevation is outside the representable project coordinate range.");

        var (surface, face) = PlaneElevations(level);
        if (surface.Real != face.Real || surface.ValueOffset == face.ValueOffset)
            throw ParameterWriter.Unverified("The level's saved plane and face disagree.");
        bool retain = surface.Real == destination || IsRetainedElevation(surface.Real - origin, elevation);
        var wallDependents = dependents.Where(walls.Contains).ToHashSet();
        var openingHosts = new HashSet<long>();
        var openings = new HashSet<long>();
        foreach (var (id, record) in planRecords)
        {
            if (Class(record.Body) != "SWallRectOpening")
                continue;
            var header = headers[id];
            if (!dependents.Contains(id) && !header.Parents.Any(wallDependents.Contains))
                continue;
            var opening = Read(record.Body, "SWallRectOpening", id);
            if (Integer(opening, "OpeningBase.m_hostId") is not long host || !walls.Contains(host)
                || (!wallDependents.Contains(host) && !HasOpeningConstraint(opening, id))
                || header.Category != (int)BuiltInCategory.OST_SWallRectOpening || header.Partition != record.Partition
                || !header.Parents.Contains(host)
                || headers.Any(pair => pair.Key != id && pair.Key != host && pair.Value.Parents.Contains(id)))
                throw ParameterWriter.Unverified("The level has an opening with unverified host or dependencies.");
            openingHosts.Add(host);
            openings.Add(id);
        }

        bool HasOpeningConstraint(WideBlockWalker.Walk opening, long id) =>
            ParameterWriter.FindValue(walker, opening, new(id, (int)BuiltInParameter.WALL_BASE_CONSTRAINT, null), StorageType.ElementId)?.Value.Integer == edit.ElementId
            || ParameterWriter.FindValue(walker, opening, new(id, (int)BuiltInParameter.WALL_HEIGHT_TYPE, null), StorageType.ElementId)?.Value.Integer == edit.ElementId;

        long[] nonWallDependents = dependents.Where(id => !wallDependents.Contains(id) && !openings.Contains(id)).ToArray();
        double nextElevation = retain ? surface.Real : destination;
        var related = nonWallDependents.Length != 0
            ? PlanViews(nonWallDependents, headers, planRecords, geometryPartitions, hasAnalyticalGeometry, edit.ElementId,
                surface.Real, nextElevation, Class, Read, Target, PlaneElevations)
            : [];
        foreach (long wall in wallDependents.Union(openingHosts))
        {
            var move = new WallHeightWriter.LevelMove(edit.ElementId, surface.Real, nextElevation);
            if (WallHeightWriter.Plan(root, tags, idWidth, schema, elementIds, edit with { ElementId = wall }, pending, move) is { } plan)
                related.AddRange(plan.Records);
        }
        if (retain)
            return null;
        byte[] updated = element.Body;
        BinaryPrimitives.WriteDoubleLittleEndian(updated.AsSpan(surface.ValueOffset), destination);
        BinaryPrimitives.WriteDoubleLittleEndian(updated.AsSpan(face.ValueOffset), destination);
        var (revisedSurface, revisedFace) = PlaneElevations(Read(updated, "Level", edit.ElementId));
        if (revisedSurface.Real != destination || revisedFace.Real != destination)
            throw ParameterWriter.Unverified("The revised elevation could not be read back.");
        return new([new RvtPartitionRecordEdit(element.Partition, 102, (ulong)edit.ElementId, updated), .. related], null);

        string? Class(byte[] body) => body.Length >= sizeof(ushort)
            ? walker.Class(BinaryPrimitives.ReadUInt16LittleEndian(body))?.Name
            : null;
        WideBlockWalker.Walk ReadContext(long id, string name) => id > 0 && context.TryGetValue(id, out var body)
            ? Read(body, name, id)
            : throw ParameterWriter.Unverified("A level type or project elevation reference is missing.");
        WideBlockWalker.Walk Read(byte[] body, string name, long id)
        {
            if (Class(body) != name)
                throw ParameterWriter.Unverified("A level elevation record has an unexpected class.");
            var record = walker.Run(body, 0, body.Length, BinaryPrimitives.ReadUInt16LittleEndian(body));
            if (!record.Exact || Integer(record, "Element.m_id") != id)
                throw ParameterWriter.Unverified("A level elevation record could not be read exactly.");
            return record;
        }
        int Target(WideBlockWalker.Walk walk, int queue, string key, string name) =>
            walk.Captures.Where(c => c.QueueIndex == queue && c.Key == All + key).ToArray()
                is [{ Kind: WideBlockWalker.CaptureKind.PointerClass, Integer: > 0, QueuePosition: >= 0 } pointer]
            && pointer.QueuePosition < walk.Queue.Count && walk.Queue[pointer.QueuePosition] == pointer.Integer
            && walker.Class(walk.Queue[pointer.QueuePosition])?.Name == name
                ? pointer.QueuePosition
                : throw ParameterWriter.Unverified("A level elevation reference has unverified storage.");
        (WideBlockWalker.Capture Surface, WideBlockWalker.Capture Face) PlaneElevations(WideBlockWalker.Walk walk)
        {
            int surface = Target(walk, -1, "DatumPlane.m_pSurface", "Plane");
            int face = Target(walk, -1, "DatumPlane.m_pFace", "Face");
            int faceSurface = Target(walk, face, "Face.m_pSurf", "Plane");
            foreach (int plane in new[] { surface, faceSurface })
                if (Real(walk, plane, "Plane.m_xVec@2").Real != 0 || Real(walk, plane, "Plane.m_yVec@2").Real != 0)
                    throw ParameterWriter.Unverified("A saved level plane is not horizontal.");
            return (Real(walk, surface, "Plane.m_origin@2"), Real(walk, faceSurface, "Plane.m_origin@2"));
        }
    }

    // Revit compares the displayed elevations before transforming the new value into project coordinates.
    private static bool IsRetainedElevation(double current, double requested)
    {
        double magnitude = Math.Abs(current) + Math.Abs(requested);
        if (!double.IsFinite(magnitude))
            throw ParameterWriter.Unverified("The elevation comparison overflows the verified numeric range.");
        if (magnitude < ElevationTolerance
            || ParameterFormulaEvaluator.IsWithinVerifiedRelativeTolerance(current, requested))
            return true;
        if (Math.Abs(current - requested) <= Math.BitIncrement(magnitude * ElevationTolerance))
            throw ParameterWriter.Unverified("The elevation change is at Revit's numeric comparison cutoff.");
        return false;
    }

    private static long? Integer(WideBlockWalker.Walk walk, string field, int queue = -1) => UserParameterWriter.Integer(walk, All + field, queue);

    private static WideBlockWalker.Capture Real(WideBlockWalker.Walk walk, int queue, string field) =>
        walk.Captures.Where(c => c.QueueIndex == queue && c.Key == All + field).ToArray()
            is [{ Kind: WideBlockWalker.CaptureKind.Real, ValueOffset: >= sizeof(ushort) } capture] && double.IsFinite(capture.Real)
            ? capture
            : throw ParameterWriter.Unverified("A level coordinate could not be read exactly.");
}
