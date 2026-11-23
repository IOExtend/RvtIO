namespace RvtIO.Parsing;

internal static partial class TfBlockDeserializer
{
    internal enum StairRunStyle { Sketched = 2, Straight = 3, Spiral = 4 }
    internal readonly record struct StairPathDirection(double X, double Y, double Z);
    /// <summary>StairsRun.m_bottomElevation / m_topElevation / m_actualRunWidth in feet and the base and top
    /// riser indices (StairsRunAndLanding.m_baseRiserIndex, StairsRun.m_topRiserIndex).</summary>
    internal sealed record StairRunNumbers(double BottomElevation, double TopElevation, double ActualRunWidth,
        int BaseRiserIndex, int TopRiserIndex, int BeginWithRiser = -1, int EndWithRiser = -1);
    internal sealed record StairRunInput(int OwnerId, int StairsId, StairRunStyle Style,
        StairPathDirection[] Directions, StairRunNumbers? Numbers = null, StairPlanInput? Plan = null);
    internal readonly record struct StairPlanLine(double X1, double Y1, double X2, double Y2, double Elevation);
    internal sealed record StairPlanInput(StairPlanLine[] Treads, StairPlanLine[] Left, StairPlanLine[] Right,
        StairPlanLine[] Center);

    /// <summary>Own R22 StairsRun graph, exact FIFO and footer. getRunType checks the own CellList run cell;
    /// getStairsPath2D uses m_oRefCurvesArr[0], not the cell's base lines. Only Straight/Sketched runs and
    /// horizontal GLine paths are admitted. No geometry or generic TF contract is changed.</summary>
    internal static StairRunInput? ExtractStairRunInput(byte[] data, int start, uint size, ushort classId)
    {
        if (classId != 3708 || !TryExtractOwnElementClass(data, start, size, classId, out int owner)) return null;
        var cursor = new Cursor { D = data, Pos = start + 14, End = checked((int)((long)start + 12 + size)), RootClassId = classId };
        var queue = new List<(ushort Cls, uint Id)>();
        int cellList = -1, cachedLoop = -1, cell = -1, stairId = 0;
        int[]? cachedCurves = null, outlineLoops = null;
        int firstTread = -1, pastLastTread = -1;
        var planLoops = new Dictionary<int, int[]>();
        double bottom = double.NaN, top = double.NaN, width = double.NaN;
        int baseIndex = int.MinValue, topIndex = int.MinValue;
        int beginRiser = -1, endRiser = -1;
        var directions = new Dictionary<int, StairPathDirection>();
        var origins = new Dictionary<int, StairPathDirection>();
        var bounds = new Dictionary<int, (double First, double Last)>();
        try
        {
            foreach (var field in SerializationFields(classId) ?? throw Fail)
            {
                int position = cursor.Pos, before = queue.Count;
                if (field is { Declaring: 3687, Name: "m_compatibleCrvInfo", Type: 14, Mods: 0, Ref: null })
                {
                    if (!StairGraphLayout.TryLayoutInfo(data, position, cursor.End, out int finish, out _, out var additions)) throw Fail;
                    cursor.Pos = finish;
                    queue.AddRange(additions);
                }
                else if (field is { Declaring: 3687, Name: "m_logicJoinInfo", Type: 14, Mods: 0, Ref: null })
                {
                    uint count = ReadU32(cursor);
                    if (count == uint.MaxValue) count = 0;
                    if (count > 100_000 || count * 16L > cursor.End - cursor.Pos) throw Fail;
                    cursor.Pos += checked((int)count * 16);
                }
                else ReadField(cursor, field, queue, 0, null);
                if (field is { Declaring: 3708, Type: 7, Mods: 0 } && cursor.Pos - position == 8)
                {
                    double value = BitConverter.ToDouble(data, position);
                    if (field.Name == "m_bottomElevation") bottom = value;
                    else if (field.Name == "m_topElevation") top = value;
                    else if (field.Name == "m_actualRunWidth") width = value;
                }
                else if (field is { Declaring: 3708, Type: 1, Mods: 0 } && cursor.Pos - position == 1
                    && field.Name is "m_beginWithRiser" or "m_endWithRiser")
                {
                    if (field.Name == "m_beginWithRiser") beginRiser = data[position]; else endRiser = data[position];
                }
                else if (field is { Type: 4, Mods: 0 } && cursor.Pos - position == 4
                    && (field.Declaring, field.Name) is (3687, "m_baseRiserIndex") or (3708, "m_topRiserIndex"))
                {
                    int value = BitConverter.ToInt32(data, position);
                    if (field.Name == "m_baseRiserIndex") baseIndex = value; else topIndex = value;
                }
                if (field is { Declaring: 37, Name: "m_cellList", Type: 14, Mods: 1, Ref: null })
                {
                    if (queue.Count != before + 1 || queue[before].Cls != 653) throw Fail;
                    cellList = before;
                }
                if (field is { Declaring: 3687, Name: "m_stairsId", Type: 14, Mods: 0, Ref: 20 })
                    stairId = BitConverter.ToInt32(data, position);
                if (field is { Declaring: 3708, Name: "m_oRefCurvesArr", Type: 14, Mods: 81, Ref: null })
                {
                    // Never substitute a later nonnull slot for the API's first slot.
                    if (cursor.Pos - position < 10 || BitConverter.ToUInt32(data, position) is 0 or uint.MaxValue
                        || BitConverter.ToUInt32(data, position + 4) == 0
                        || BitConverter.ToUInt16(data, position + 8) != 938
                        || queue.Count <= before || queue[before].Cls != 938) throw Fail;
                    cachedLoop = before;
                    // The two projected open side paths follow the ten path/riser reference groups.
                    if (BitConverter.ToUInt32(data, position) == 12 && queue.Count - before == 12
                        && queue.Skip(before).All(item => item.Cls == 938))
                        outlineLoops = [before + 10, before + 11];
                }
                if (field is { Declaring: 3708, Name: "m_orderedNosingFront", Type: 14, Mods: 81, Ref: null }
                    && queue.Count > before && BitConverter.ToUInt32(data, position) == queue.Count - before
                    && queue.Skip(before).All(item => item.Cls == 938))
                    (firstTread, pastLastTread) = (before, queue.Count);
                if (queue.Count > 100_000) throw Fail;
            }
            if (cellList < 0 || cachedLoop < 0 || stairId <= 0 || stairId == owner) throw Fail;
            for (int index = 0; index < queue.Count; index++)
            {
                // A spiral run needs no more than its cell class and base line; the curved geometry behind them is not decoded.
                if (cell >= 0 && cachedCurves != null && queue[cell].Cls == 3723) break;
                if (queue.Count > 100_000) throw Fail;
                ushort cls = queue[index].Cls;
                bool step = IsSubclassOf(cls, 96);
                bool direct = step || cls is 653 or 3682 or 3712 or 938
                    || IsSubclassOf(cls, 1246) || IsSubclassOf(cls, 523) || IsSubclassOf(cls, 70);
                if (!direct) { ReadDeferredObject(cursor, cls, queue); continue; }
                if (step)
                {
                    if (!StairGraphLayout.TryCurrentHistoryEnd(data, cursor.Pos, cursor.End, out int finish)) throw Fail;
                    cursor.Pos = finish;
                }
                foreach (var sourceField in SerializationFields(cls) ?? throw Fail)
                {
                    if (step && sourceField.Declaring == 97 && sourceField.Name is "m_faceHistTable"
                        or "m_curveHistTableSet" or "m_edgeHistTable" or "m_edgeHistTableReverse") continue;
                    var field = sourceField;
                    int position = cursor.Pos, before = queue.Count;
                    if (field is { Declaring: 677, Name: "m_affectedEdgesTags", Type: 14, Mods: 80, Ref: null })
                    {
                        if (!StairGraphLayout.TryAffectedTags(data, position, cursor.End, out int finish, out _)) throw Fail;
                        cursor.Pos = finish;
                    }
                    else
                    {
                        if (field is { Declaring: 1246, Name: "m_GInfo", Type: 14, Mods: 0, Ref: null })
                        {
                            if (position > cursor.End - 16) throw Fail;
                            field = field with { Name = "stair_source_GInfo", Declaring = 0, Ref = 1247 };
                        }
                        else if (field is { Declaring: 523, Name: "m_Envelope", Type: 14, Mods: 0, Ref: null })
                            field = field with { Ref = 524 };
                        else if (field is { Type: 13, Mods: 16, Cnt: not null } && field.Ref is not (524 or 1248))
                            field = field with { Type = 7, Cnt = checked(field.Cnt.Value * 3) };
                        ReadField(cursor, field, queue, 1, null);
                    }
                    if (index == cellList && sourceField is { Declaring: 653, Name: "m_cells", Type: 14, Mods: 81, Ref: null })
                    {
                        var cells = Enumerable.Range(before, queue.Count - before)
                            .Where(i => IsSubclassOf(queue[i].Cls, 3676)).ToArray();
                        if (cells.Length != 1 || queue[cells[0]].Cls is not (3725 or 3721 or 3723)) throw Fail;
                        cell = cells[0];
                    }
                    if (index == cachedLoop && sourceField is { Declaring: 938, Name: "m_open", Type: 1, Mods: 0 })
                    {
                        if (data[position] > 1) throw Fail;
                    }
                    if (index == cachedLoop && sourceField is { Declaring: 938, Name: "m_curves", Type: 14, Mods: 81, Ref: null })
                    {
                        uint count = BitConverter.ToUInt32(data, position);
                        if (count == 0 || count == uint.MaxValue || count != queue.Count - before) throw Fail;
                        cachedCurves = Enumerable.Range(before, queue.Count - before).ToArray();
                        if (cachedCurves.Any(i => queue[i].Cls is not (1736 or 1974))) throw Fail;
                    }
                    if (sourceField is { Declaring: 938, Name: "m_curves", Type: 14, Mods: 81, Ref: null }
                        && (index == cachedLoop || outlineLoops?.Contains(index) == true
                            || index >= firstTread && index < pastLastTread)
                        && queue.Count > before && BitConverter.ToUInt32(data, position) == queue.Count - before
                        && queue.Skip(before).All(item => item.Cls == 1736))
                        planLoops.Add(index, Enumerable.Range(before, queue.Count - before).ToArray());
                    if (cls == 1736 && sourceField is { Declaring: 1736, Name: "m_dirVec", Type: 7, Mods: 16, Cnt: 3 })
                    {
                        var direction = new StairPathDirection(BitConverter.ToDouble(data, position),
                            BitConverter.ToDouble(data, position + 8), BitConverter.ToDouble(data, position + 16));
                        directions.Add(index, direction);
                    }
                    if (cls == 1736 && sourceField is { Declaring: 1736, Name: "m_origin", Type: 7, Mods: 16, Cnt: 3 }
                        && Enumerable.Range(0, 3).All(i => double.IsFinite(BitConverter.ToDouble(data, position + i * 8))))
                        origins.Add(index, new(BitConverter.ToDouble(data, position),
                            BitConverter.ToDouble(data, position + 8), BitConverter.ToDouble(data, position + 16)));
                    if (cls == 1736 && sourceField is { Declaring: 1737, Name: "m_endParams", Type: 7, Mods: 16, Cnt: 2 })
                    {
                        double first = BitConverter.ToDouble(data, position), last = BitConverter.ToDouble(data, position + 8);
                        if (double.IsFinite(first) && double.IsFinite(last) && first < last) bounds.Add(index, (first, last));
                    }
                }
            }
            // A spiral run has arcs for a base line and no direction and stops the walk early; every other style needs bounded horizontal lines and the exact footer.
            bool spiral = cell >= 0 && queue[cell].Cls == 3723;
            if (cell < 0 || cachedCurves == null || !spiral && cursor.Pos != cursor.End
                || !spiral && cachedCurves.Any(i => !directions.ContainsKey(i) || !origins.ContainsKey(i) || !bounds.ContainsKey(i))) throw Fail;
            StairPathDirection[] path = spiral ? [] : cachedCurves.Select(i => directions[i]).ToArray();
            if (path.Any(direction => !double.IsFinite(direction.X) || !double.IsFinite(direction.Y)
                || direction.Z != 0 || direction.X == 0 && direction.Y == 0)) throw Fail;
            var numbers = double.IsFinite(bottom) && double.IsFinite(top) && double.IsFinite(width)
                && baseIndex != int.MinValue && topIndex != int.MinValue
                ? new StairRunNumbers(bottom, top, width, baseIndex, topIndex, beginRiser, endRiser) : null;
            StairPlanInput? plan = null;
            if (!spiral && outlineLoops is [int left, int right] && pastLastTread - firstTread > 1
                && ReadPlanLines([left]) is { } leftSide && ReadPlanLines([right]) is { } rightSide)
            {
                int[] treadLoops = Enumerable.Range(firstTread, pastLastTread - firstTread).ToArray();
                if (treadLoops.All(loop => planLoops.TryGetValue(loop, out var lines) && lines.Length == 1)
                    && ReadPlanLines(treadLoops) is { } treads)
                    plan = new(treads, leftSide, rightSide, ReadPlanLines([cachedLoop]) ?? []);
            }
            return new(owner, stairId, queue[cell].Cls switch { 3725 => StairRunStyle.Straight, 3723 => StairRunStyle.Spiral, _ => StairRunStyle.Sketched },
                path, numbers, plan);
        }
        catch (DecodeFail) { return null; }
        catch (OverflowException) { return null; }

        StairPlanLine[]? ReadPlanLines(int[] loops)
        {
            var lines = new List<StairPlanLine>();
            foreach (int loop in loops)
            {
                if (!planLoops.TryGetValue(loop, out var indices)) return null;
                foreach (int index in indices)
                {
                    if (ReadPlanLine(index) is not { } line) return null;
                    lines.Add(line);
                }
            }
            return lines.ToArray();
        }

        StairPlanLine? ReadPlanLine(int index)
        {
            if (!origins.TryGetValue(index, out var p) || !directions.TryGetValue(index, out var v)
                || v.Z != 0 || !bounds.TryGetValue(index, out var t)) return null;
            var line = new StairPlanLine(p.X + t.First * v.X, p.Y + t.First * v.Y,
                p.X + t.Last * v.X, p.Y + t.Last * v.Y, p.Z);
            return double.IsFinite(line.X1) && double.IsFinite(line.Y1)
                && double.IsFinite(line.X2) && double.IsFinite(line.Y2)
                && (line.X1 != line.X2 || line.Y1 != line.Y2) ? line : null;
        }
    }
}
