namespace RvtIO.Parsing;

internal static partial class TfBlockDeserializer
{
    internal sealed record ComputedFamilySymbol(int SymbolId, int QueueIndex, int Start, int End)
    {
        public double[]? Transform { get; init; }
        public uint? SubInstanceCount { get; init; }
        public int? HostId { get; init; }
        public int? InsertOrientation { get; init; }
        /// <summary>FamilyInstance.m_flippedX and m_flippedY (hand and facing flip) of the same root.</summary>
        public bool? FlippedX { get; init; }
        public bool? FlippedY { get; init; }
    }

    private static readonly Lazy<(List<TfField> Root, List<TfField> Info)?> ComputedFamilySymbolFields = new(() =>
    {
        var root = SerializationFields(1794);
        var info = SerializationFields(2238);
        var transform = SerializationFields(78);
        if (root == null
            || !HasExactSchemaField(root, 37, "m_id", 14, 0, 20)
            || !HasExactSchemaField(root, 37, "m_docAccess", 14, 0, null)
            || !HasExactSchemaField(root, 1796, "m_pInstanceInfo", 14, 2, null)
            || info is not { Count: 3 }
            || !HasExactSchemaField(info, 2237, "m_Trf", 14, 0, 78)
            || !HasExactSchemaField(info, 2237, "m_symbolId", 14, 0, 20)
            || !HasExactSchemaField(info, 2238, "m_cda", 14, 0, 413)
            || info[0].Name != "m_Trf" || info[1].Name != "m_symbolId"
            || info[2].Name != "m_cda" || transform is not { Count: 2 }
            || transform[0] is not { Declaring: 78, Name: "m_3x3", Type: 13, Mods: 16, Cnt: 3, Ref: null }
            || transform[1] is not { Declaring: 78, Name: "m_or", Type: 7, Mods: 16, Cnt: 3, Ref: 77 })
            return null;
        return (root, info);
    });

    /// <summary>FamilyInstance's own Instance.m_pInstanceInfo -> InstInfoBase.m_symbolId.
    /// getOriginalSymbol_ delegates to FamilyInstance.getComputedSymbol, which reads this member, not
    /// FamilyInstance.m_masterSymbolId. Only the complete root and the owned deferred prefix through InstanceInfo
    /// are covered; its finite raw Trf is kept with that symbol. Later objects, placement normalization and
    /// DoorWindowInfo.FlippedSymbol are not covered.</summary>
    internal static ComputedFamilySymbol? ExtractComputedFamilySymbol(
        byte[] data, int start, uint size, ushort classId)
    {
        if (classId != 1794 || start < 0 || start > data.Length - 16 || size < 4
            || size > int.MaxValue || (long)start + 16 + size > data.Length
            || BitConverter.ToUInt32(data, start + 8) != size
            || BitConverter.ToUInt16(data, start + 12) != classId
            || BitConverter.ToUInt32(data, start + 12 + (int)size) != size)
            return null;
        if (ComputedFamilySymbolFields.Value is not { } layout) return null;
        var (rootFields, infoFields) = layout;
        var cursor = new Cursor { D = data, Pos = start + 14,
            End = start + 12 + (int)size, RootClassId = classId };
        var queue = new List<(ushort Cls, uint Id)>();
        int owner = 0, target = -1;
        uint? subInstanceCount = null;
        int? hostId = null, insertOrientation = null;
        bool? flippedX = null, flippedY = null;
        try
        {
            foreach (var field in rootFields)
            {
                if (field.Declaring == 37 && field.Name == "m_id") owner = ReadI32(cursor);
                else if (field.Declaring == 37 && field.Name == "m_docAccess")
                {
                    if (ReadU32(cursor) != 1) return null;
                }
                else if (field.Declaring == 1796 && field.Name == "m_pInstanceInfo")
                {
                    int pointerStart = cursor.Pos, before = queue.Count;
                    ReadPtr(cursor, queue);
                    if (BitConverter.ToUInt32(data, pointerStart) != uint.MaxValue
                        || queue.Count != before + 1 || queue[before].Cls != 2238)
                        return null;
                    target = before;
                }
                else if (field is { Declaring: 1795, Name: "m_hostId", Type: 14, Mods: 0, Ref: 20, Cnt: null })
                    hostId = ReadI32(cursor);
                else if (field is { Declaring: 1794, Name: "m_insertOrientation", Type: 4, Mods: 0, Ref: null, Cnt: null })
                    insertOrientation = ReadI32(cursor);
                else
                {
                    if (field is { Declaring: 1794, Name: "m_flippedX" or "m_flippedY", Type: 1, Mods: 0, Ref: null, Cnt: null }
                        && cursor.Pos < cursor.End && data[cursor.Pos] <= 1)
                    {
                        if (field.Name == "m_flippedX") flippedX = data[cursor.Pos] == 1;
                        else flippedY = data[cursor.Pos] == 1;
                    }
                    if (field is { Declaring: 1794, Name: "m_subInstTable", Type: 14, Mods: 80, Ref: 19, Cnt: null }
                        && cursor.Pos <= cursor.End - 4)
                        subInstanceCount = BitConverter.ToUInt32(data, cursor.Pos);
                    ReadField(cursor, field, queue, 0, null);
                }
            }
            if (owner <= 0 || owner != BitConverter.ToInt32(data, start) || target < 0)
                return null;
            for (int index = 0; index < target; index++)
                ReadDeferredObject(cursor, queue[index].Cls, queue);
            int infoStart = cursor.Pos - start;
            ReadField(cursor, infoFields[0], queue, 1, null);
            double[]? transform = null;
            if (cursor.Pos - start - infoStart == 96)
            {
                var values = new double[12];
                for (int index = 0; index < values.Length; index++)
                    values[index] = BitConverter.ToDouble(data, start + infoStart + index * 8);
                if (values.All(double.IsFinite)) transform = values;
            }
            int symbol = ReadI32(cursor);
            ReadField(cursor, infoFields[2], queue, 1, null);
            return symbol > 0 && symbol != owner
                ? new ComputedFamilySymbol(symbol, target, infoStart, cursor.Pos - start)
                    { Transform = transform, SubInstanceCount = subInstanceCount,
                        HostId = hostId, InsertOrientation = insertOrientation,
                        FlippedX = flippedX, FlippedY = flippedY } : null;
        }
        catch (DecodeFail) { return null; }
    }
}
