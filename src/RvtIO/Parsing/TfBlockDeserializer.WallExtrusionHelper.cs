namespace RvtIO.Parsing;

internal static partial class TfBlockDeserializer
{
    /// <summary>The serialized profile helper of an own wall root: <paramref name="Present"/> is false for a readable
    /// wall without a VWallExtrusionHelper cell, otherwise Complete is ExtrusionHelper.m_complete and
    /// LoopCurveCounts the number of curves of each ElementExtrusionHelper.m_pCurveLoops loop, in source order.</summary>
    internal sealed record OwnWallHelperInput(ushort RootClassId, bool Present, bool Complete, IReadOnlyList<int> LoopCurveCounts);

    /// <summary>Reads the profile helper from the complete owned FIFO of an own wall root (class 3524/361).
    /// Null when the frame is not completely readable or the helper is ambiguous (several cells, unknown loop class).</summary>
    internal static OwnWallHelperInput? ExtractOwnWallHelperInput(byte[] data, int start, uint size, ushort classId,
        WallCompleteInputRead? shared = null)
    {
        var input = shared == null
            ? ExtractOwnWallCompleteInputs(data, start, size, classId, out _)
            : shared.Read(out _);
        if (input == null) return null;
        var helpers = input.LaterObjects.Where(o => o.ClassId == 4041).ToArray();
        if (helpers.Length == 0) return new(classId, false, false, []);
        if (helpers.Length != 1) return null;
        var complete = helpers[0].Fields.Where(f => f.Declaring == 570 && f.Name == "m_complete").ToArray();
        var loops = helpers[0].Fields.Where(f => f.Declaring == 569 && f.Name == "m_pCurveLoops").ToArray();
        if (complete is not [{ Bytes.Length: 1 } completeField] || loops is not [var loopsField]) return null;
        var byIndex = input.LaterObjects.ToDictionary(o => o.QueueIndex);
        var counts = new List<int>();
        foreach (int index in loopsField.QueuedObjects)
        {
            if (!byIndex.TryGetValue(index, out var loop) || loop.ClassId != 938) return null;
            var curves = loop.Fields.Where(f => f.Declaring == 938 && f.Name == "m_curves").ToArray();
            if (curves is not [var curvesField] || curvesField.Bytes.Length < 4) return null;
            uint count = BitConverter.ToUInt32(curvesField.Bytes, 0);
            if (count == uint.MaxValue || count != curvesField.QueuedObjects.Count) return null;
            counts.Add((int)count);
        }
        return new(classId, true, completeField.Bytes[0] != 0, counts);
    }
}
