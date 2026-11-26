using System.Buffers.Binary;
using RvtIO.BuiltIns;
using RvtIO.Parsing;

namespace RvtIO.Writing;

internal static partial class LevelElevationWriter
{
    private sealed record DependencyHeader(int Partition, long? Category, bool MainModelWithoutBounds, long[] Parents);
    private static readonly string[] ClipElevationFields =
        ["DBViewPlan.m_cutPlaneElev", "DBViewPlan.m_topClipElev", "DBViewPlan.m_bottomClipElev", "DBViewPlan.m_levelBelowElev"];
    private const double UnlimitedPlanElevation = 999999999.9999999;

    private static List<RvtPartitionRecordEdit> PlanViews(
        IReadOnlyList<long> dependents,
        IReadOnlyDictionary<long, DependencyHeader> headers,
        IReadOnlyDictionary<long, (int Partition, byte[] Body)> records,
        IReadOnlyDictionary<long, int> geometryPartitions,
        bool hasAnalyticalGeometry, long levelId, double previous, double destination,
        Func<byte[], string?> readClass,
        Func<byte[], string, long, WideBlockWalker.Walk> read,
        Func<WideBlockWalker.Walk, int, string, string, int> target,
        Func<WideBlockWalker.Walk, (WideBlockWalker.Capture Surface, WideBlockWalker.Capture Face)> readLevelPlanes)
    {
        var edits = new List<RvtPartitionRecordEdit>();
        var fixedSketchOwners = new Dictionary<long, long>();
        var sketches = new Dictionary<long, (int Partition, byte[] Body, WideBlockWalker.Walk Walk)>();

        foreach (long dependent in dependents)
        {
            if (!records.TryGetValue(dependent, out var record)
                || !headers.TryGetValue(dependent, out var header) || !header.MainModelWithoutBounds
                || header.Partition != record.Partition || !geometryPartitions.TryGetValue(dependent, out int dummy)
                || dummy != record.Partition || readClass(record.Body) is not string name)
                throw UnverifiedPlan();

            var walk = read(record.Body, name, dependent);
            long? option = Integer(walk, "Element.m_designOptionId");
            switch (name)
            {
                case "DBViewPlan" when option == -1 && header.Category == (int)BuiltInCategory.OST_Views:
                    long generatedLevel = UpdatePlan(dependent, record.Partition, record.Body, walk);
                    if (generatedLevel == levelId)
                    {
                        if (Integer(walk, "DBView.m_fixedSketchPlaneId") is not (> 0 and long sketchId)
                            || !fixedSketchOwners.TryAdd(sketchId, dependent))
                            throw UnverifiedPlan();
                    }
                    break;
                case "SketchPlane" when option == -1 && header.Category == -1:
                    sketches.Add(dependent, (record.Partition, record.Body, walk));
                    break;
                case "SunAndShadowSettings" when option == -4 && header.Category == (int)BuiltInCategory.OST_SunStudy
                    && Integer(walk, "SunAndShadowSettings.m_idLevel") == levelId:
                    break;
                case "EnergyDataSettings" when option == -1 && header.Category == -1 && !hasAnalyticalGeometry
                    && Integer(walk, "EnergyDataSettings.m_groundPlane") == levelId
                    && Integer(walk, "EnergyDataSettings.m_createAnalyticalModel") == 0
                    && Integer(walk, "EnergyDataSettings.m_energyModel") == 0:
                    break;
                default:
                    throw UnverifiedPlan();
            }
        }
        if (fixedSketchOwners.Keys.Any(id => !sketches.ContainsKey(id)))
            throw UnverifiedPlan();

        foreach (var (id, sketch) in sketches)
        {
            int reference;
            if (Integer(sketch.Walk, "SketchPlane.m_flipZ") != 0 || Integer(sketch.Walk, "SketchPlane.m_userId") != -1)
                throw UnverifiedPlan();
            long? view = fixedSketchOwners.TryGetValue(id, out long owner) ? owner : null;
            if (view is not null)
                reference = target(sketch.Walk, -1, "SketchPlane.m_oPlaneRef", "OnDBViewPlaneRef");
            else
                reference = target(sketch.Walk, -1, "SketchPlane.m_oPlaneRef", "OnDatumPlaneRef");
            if (Integer(sketch.Walk, "OneElementMovablePlaneRef.m_datumPlaneId", reference) != (view ?? levelId)
                || Real(sketch.Walk, reference, "OneElementMovablePlaneRef.m_vecInPlane@2").Real != 0
                || Real(sketch.Walk, reference, "OneElementMovablePlaneRef.m_rotation").Real != 0
                || Integer(sketch.Walk, "OneElementMovablePlaneRef.m_mirror", reference) != 0)
                throw UnverifiedPlan();
            foreach (var (child, header) in headers)
                if (child != id && child != view && header.Parents.Contains(id))
                    VerifySketchGrid(child, header, sketch.Partition);
            int transform = target(sketch.Walk, -1, "SketchPlane.m_oTrf", "Trf");
            var height = Real(sketch.Walk, transform, "Trf.m_or@2");
            if (height.Real != previous)
                throw UnverifiedPlan();
            if (previous != destination)
            {
                Write(sketch.Body, height, destination);
                _ = read(sketch.Body, "SketchPlane", id);
                edits.Add(new(sketch.Partition, 102, (ulong)id, sketch.Body));
            }
        }
        return edits;

        void VerifySketchGrid(long id, DependencyHeader header, int partition)
        {
            if (!header.MainModelWithoutBounds || header.Category != (int)BuiltInCategory.OST_IOSSketchGrid
                || header.Partition != partition || !records.TryGetValue(id, out var record)
                || record.Partition != partition || !geometryPartitions.TryGetValue(id, out int dummy) || dummy != partition)
                throw UnverifiedPlan();
            var grid = read(record.Body, "SketchGrid", id);
            if (Integer(grid, "Element.m_designOptionId") != -4 || grid.Queue.Count != 0
                || Integer(grid, "SketchGrid.m_dbviewId") is not long view || !fixedSketchOwners.ContainsValue(view)
                || !header.Parents.Contains(view))
                throw UnverifiedPlan();
        }

        long UpdatePlan(long id, int partition, byte[] body, WideBlockWalker.Walk view)
        {
            if (Integer(view, "DBView.m_genElemId") is not (> 0 and long generated)
                || Integer(view, "DBView.m_isTemplate") != 0 || Integer(view, "DBView.m_viewTemplateId") != -1
                || Integer(view, "DBView.m_primaryViewId") != -1 || Integer(view, "DBView.m_dependentViewIds#") != 0
                || Integer(view, "DBViewPlan.m_planRegionIds#") != 0 || Integer(view, "DBViewPlan.m_idUnderlayLevel") != -1
                || Integer(view, "DBViewPlan.m_idUnderlayTopLevel") != -1
                || Integer(view, "DBViewPlan.m_planViewType") is not ((1 or 2) and long direction))
                throw UnverifiedPlan();

            int legacy = target(view, -1, "DBViewPlan.m_pPlanViewRange", "PlanViewRange");
            int range = target(view, -1, "DBViewPlan.m_pPlanViewRange2", "PlanViewRange2");
            for (int i = 0; i < 5; i++)
                if (Integer(view, $"PlanViewRange.m_levelPos@{i}", legacy) != 0 || Real(view, legacy, $"PlanViewRange.m_offsets@{i}").Real != 0)
                    throw UnverifiedPlan();

            // The five saved positions kept the same semantics when the fields were renamed.
            var (levelField, offsetField) = Integer(view, "PlanViewRange2.m_levelIds@4", range) is not null
                ? ("PlanViewRange2.m_levelIds@", "PlanViewRange2.m_offsets@")
                : ("PlanViewRange2.m_viewDepthPlaneLevelIds@", "PlanViewRange2.m_viewDepthPlaneOffsets@");
            if (Integer(view, levelField + 4, range) != -3 || Real(view, range, offsetField + 4).Real != 0)
                throw UnverifiedPlan();

            double[] old = new double[4], next = new double[4];
            var replacements = new Dictionary<int, double>();
            bool dependsOnLevel = generated == levelId;
            for (int i = 0; i < 4; i++)
            {
                long? reference = Integer(view, levelField + i, range);
                double offset = Real(view, range, offsetField + i).Real;
                var field = Real(view, -1, ClipElevationFields[i]);
                if (reference is > 0 and long anchor)
                {
                    double height = ReadLevel(anchor);
                    old[i] = height + offset;
                    next[i] = (anchor == levelId ? destination : height) + offset;
                    dependsOnLevel |= anchor == levelId;
                }
                else if (direction == 2 && i is 1 or 3 && reference == -1 && offset == 0)
                    old[i] = next[i] = UnlimitedPlanElevation;
                else
                    throw UnverifiedPlan();

                if (!double.IsFinite(next[i]) || field.Real != old[i])
                    throw UnverifiedPlan();
                if (next[i] != old[i])
                    replacements.Add(field.ValueOffset, next[i]);
            }
            if (!dependsOnLevel)
                throw UnverifiedPlan();
            if (direction == 1 && (next[3] > next[2] || next[2] > next[0] || next[0] > next[1])
                || direction == 2 && (next[0] != next[2] || next[1] != UnlimitedPlanElevation || next[3] != UnlimitedPlanElevation))
                throw ParameterWriter.Unverified("The new level elevation falls outside the verified plan view range.");

            AddCutterPlane("DBViewPlan.m_pCutter", 0);
            if (direction == 1)
                AddCutterPlane("DBViewPlan.m_pViewDepthCutter", 3);
            else if (Integer(view, "DBViewPlan.m_pViewDepthCutter") != 0)
                throw UnverifiedPlan();

            if (replacements.Count != 0)
            {
                foreach (var (offset, value) in replacements)
                    BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(offset), value);
                _ = read(body, "DBViewPlan", id);
                edits.Add(new(partition, 102, (ulong)id, body));
            }
            return generated;

            void AddCutterPlane(string key, int index)
            {
                int cutter = target(view, -1, key, "GenericPlaneCutter");
                int plane = target(view, cutter, "GenericPlaneCutter.m_pPlane", "Plane");
                var height = Real(view, plane, "Plane.m_origin@2");
                if (height.Real != old[index] || Real(view, plane, "Plane.m_xVec@2").Real != 0
                    || Real(view, plane, "Plane.m_yVec@2").Real != 0)
                    throw UnverifiedPlan();
                if (next[index] != old[index])
                    replacements.Add(height.ValueOffset, next[index]);
            }
        }

        double ReadLevel(long id)
        {
            if (id == levelId)
                return previous;
            if (!records.TryGetValue(id, out var record) || !headers.TryGetValue(id, out var header)
                || header.Category != (int)BuiltInCategory.OST_Levels || !header.MainModelWithoutBounds
                || header.Partition != record.Partition)
                throw UnverifiedPlan();
            var level = read(record.Body, "Level", id);
            if (Integer(level, "Element.m_designOptionId") != -1)
                throw UnverifiedPlan();
            var (surface, face) = readLevelPlanes(level);
            return surface.Real == face.Real ? surface.Real : throw UnverifiedPlan();
        }
    }

    private static RvtWriteRefusedException UnverifiedPlan() =>
        ParameterWriter.Unverified("The level's dependent elements or plan view range could not be verified for writing.");

    private static void Write(byte[] body, WideBlockWalker.Capture field, double value) =>
        BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(field.ValueOffset), value);
}
