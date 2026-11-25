namespace RvtIO.Parsing;

internal static partial class TfBlockDeserializer
{
    internal sealed record WallCompleteInputField(int Declaring, string Name, int Start, int End,
        byte[] Bytes, IReadOnlyList<int> QueuedObjects);
    internal sealed record WallCompleteInputObject(int QueueIndex, ushort ClassId, uint Handle,
        int Start, int End, IReadOnlyList<WallCompleteInputField> Fields);
    internal sealed record OwnWallCompleteInputs(OwnWallSixPhaseGeometryHistories Histories,
        IReadOnlyList<WallCompleteInputObject> LaterObjects, byte[] FrameBytes, int End);

    /// <summary>Shares one validated FIFO walk and retains the late fields used by the model reader.</summary>
    internal sealed class WallCompleteInputRead(byte[] data, int start, uint size, ushort classId)
    {
        private bool _read;
        private OwnWallCompleteInputs? _input;
        private string? _failure;

        internal OwnWallCompleteInputs? Read(out string? failure)
        {
            if (!_read)
            {
                _input = ExtractOwnWallCompleteInputs(data, start, size, classId, out _failure, retainAll: false);
                _read = true;
            }
            failure = _failure;
            return _input;
        }
    }

    /// <summary>Own six-phase inputs and the full remaining unfiltered owned FIFO. Every later field has an exact retained
    /// byte span; the frame bytes also retain the root and prior payloads. Complete requires the exact footer boundary.
    /// This is serialized input, not a reconstructed table or active geometry.</summary>
    internal static OwnWallCompleteInputs? ExtractOwnWallCompleteInputs(
        byte[] data, int start, uint size, ushort classId, out string? failure)
        => ExtractOwnWallCompleteInputs(data, start, size, classId, out failure, retainAll: true);

    private static OwnWallCompleteInputs? ExtractOwnWallCompleteInputs(
        byte[] data, int start, uint size, ushort classId, out string? failure, bool retainAll)
    {
        var histories = ExtractOwnWallGeometryHistoriesCore(data, start, size, classId, true,
            out failure, out var tweaks, out var grids, out var cutouts, out var cursor, out var queue);
        if (histories is not { Complete: true } || failure != null) return null;
        failure = "complete-input-schema";
        if (!HasGElementGraphSchemas() || !HasCompleteWallInputSchemas()) return null;
        var c = cursor!;
        c.ExactWallCompleteInputs = true;
        var objects = new List<WallCompleteInputObject>();
        int failedIndex = 0, failedPosition = 0;
        ushort failedClass = 0;
        TfField? failedField = null;
        try
        {
            for (int index = histories.TargetQueueIndex + 1; index < queue!.Count; index++)
            {
                var (cls, handle) = queue[index];
                if (!IsCompleteWallInputClass(cls) || SerializationFields(cls) is not { } fields)
                { failure = $"late:{index}:{cls}:schema"; return null; }
                failedIndex = index;
                failedClass = cls;
                int first = c.Pos - start;
                // The reader consumes the grid panel ids, location curve, and the helper's loop links.
                List<WallCompleteInputField>? retained = retainAll || cls is 901 or 938 or 1736 or 4041 ? [] : null;
                foreach (var field in fields)
                {
                    int fieldStart = c.Pos, queueStart = queue.Count;
                    failedField = field;
                    failedPosition = fieldStart - start;
                    ReadField(c, field, queue, 1, null);
                    if (queue.Count > 100_000) throw Fail;
                    if (retained != null)
                        retained.Add(new(field.Declaring, field.Name, fieldStart - start, c.Pos - start,
                            data.AsSpan(fieldStart, c.Pos - fieldStart).ToArray(),
                            Enumerable.Range(queueStart, queue.Count - queueStart).ToArray()));
                }
                if (retained != null) objects.Add(new(index, cls, handle, first, c.Pos - start, retained));
            }
            failure = "late-footer";
            if (c.Pos != c.End) return null;
            failure = null;
            return new(new(histories, tweaks, grids, cutouts), objects,
                retainAll ? data.AsSpan(start, checked((int)size + 16)).ToArray() : [], c.Pos - start);
        }
        catch (DecodeFail)
        {
            failure = failedField is { } field
                ? $"late:{failedIndex}:{failedClass}:{field.Declaring}.{field.Name}:{failedPosition}"
                : $"late:{failedIndex}:{failedClass}:schema";
            return null;
        }
    }

    private static bool IsCompleteWallInputClass(ushort cls) => cls is
        89 or 90 or 308 or 311 or 367 or 445 or 522 or 532 or 586 or 588 or 877 or 901 or 938 or
        953 or 954 or 983 or 1231 or 1243 or 1254 or 1332 or 1589 or 1736 or 1859 or 1974 or
        2007 or 2075 or 2092 or 2097 or 2174 or 2503 or 3629 or 3634 or 4041 or 4042;

    private static bool TryReadExactCompleteWallField(Cursor c, TfField f,
        List<(ushort, uint)> queue, int depth)
    {
        if (depth > 40) throw Fail;
        if (f.Type == 13)
        {
            int dimensions = (f.Declaring, f.Name) switch
            {
                (1248, "uv") or (524, "m_corners") => 2,
                (78, "m_3x3") or (2008, "m_bBox") or (2008, "m_tightbBox") => 3,
                _ => throw Fail
            };
            if ((f.Mods >> 4) != 1 || f.Cnt is not > 0) throw Fail;
            if (!c.Take(checked(8 * dimensions * f.Cnt.Value))) throw Fail;
            return true;
        }
        if (f is { Declaring: 1246, Name: "m_GInfo", Type: 14, Mods: 0, Ref: null, Cnt: null })
        { if (!c.Take(16)) throw Fail; return true; }
        if (f.Type != 14 || f.Ref != null || f.Cnt != null) return false;
        int target = (f.Declaring, f.Name, f.Mods) switch
        {
            (523, "m_Envelope", 0) => 524,
            (1244, "m_interiorEdgePnts", 80) => 1248,
            (89, "m_cleanData", 0) => 90,
            (89, "m_info", 80) => 92,
            (140, "m_foreignElemIdRef", 0) => 141,
            _ => 0
        };
        if (target == 0) return false;
        ReadField(c, f with { Ref = target }, queue, depth, null);
        return true;
    }
}
