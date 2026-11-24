using System.Buffers.Binary;

namespace RvtIO.Parsing;

/// <summary>
/// History of one element as stored in its ElemRec record: the id the element was created with and
/// the episode it was created in (a reverse index into the episode list).
/// </summary>
internal readonly record struct ElemRecHistory(long OriginalElementId, int CreationDate);

/// <summary>Element UniqueId and export id from the document's own tables.</summary>
internal static class ElementExportIds
{
    internal const ushort ElemTableClassId = 1312;
    internal const int ElemRecClassId = 1308;
    internal const int ElementHistoryClassId = 819;
    /// <summary>
    /// m_id, the four ElementHistory fields, m_partitionId and m_OwningElementId, four bytes each.
    /// </summary>
    internal const int ElemRecLength = 28;
    private const int ElemTableHeaderLength = 6;

    /// <summary>
    /// Element id -> ElemRec history from a decoded Global/ElemTable stream: the class id, the
    /// m_elemArr count, then count records in strictly increasing id order.
    /// </summary>
    internal static Dictionary<uint, ElemRecHistory>? ParseElemTable(byte[] data)
        => ParseElemTable(data, out _);

    /// <summary>
    /// Also retains the independent ElemRec.m_OwningElementId authority. No partially parsed
    /// ownership table survives a malformed record array.
    /// </summary>
    internal static Dictionary<uint, ElemRecHistory>? ParseElemTable(
        byte[] data, out Dictionary<uint, int>? owningElementIds)
    {
        owningElementIds = null;
        if (!SchemaDeclaresElemRecLayout()
            || data.Length < ElemTableHeaderLength
            || BitConverter.ToUInt16(data, 0) != ElemTableClassId)
            return null;
        uint count = BitConverter.ToUInt32(data, 2);
        if (ElemTableHeaderLength + (long)count * ElemRecLength > data.Length) return null;

        var result = new Dictionary<uint, ElemRecHistory>((int)count);
        var owners = new Dictionary<uint, int>((int)count);
        long previous = -1;
        for (int i = 0; i < count; i++)
        {
            int offset = ElemTableHeaderLength + i * ElemRecLength;
            uint id = BitConverter.ToUInt32(data, offset);
            if (id <= previous) return null;
            previous = id;
            result[id] = new ElemRecHistory(
                BitConverter.ToUInt32(data, offset + 4), BitConverter.ToInt32(data, offset + 8));
            owners[id] = BitConverter.ToInt32(data, offset + 24);
        }
        owningElementIds = owners;
        return result;
    }

    private static bool SchemaDeclaresElemRecLayout()
    {
        var table = TfBlockDeserializer.SerializationFields(ElemTableClassId);
        var record = TfBlockDeserializer.SerializationFields(ElemRecClassId);
        var history = TfBlockDeserializer.SerializationFields(ElementHistoryClassId);
        return table is { Count: > 0 }
            && table[0] is { Name: "m_elemArr", Ref: ElemRecClassId }
            && record != null
            && record.Select(field => field.Name).SequenceEqual(
                new[] { "m_id", "m_history", "m_partitionId", "m_OwningElementId" })
            && record[1].Ref == ElementHistoryClassId
            && history != null
            && history.Select(field => field.Name).SequenceEqual(
                new[] { "m_originalElementId", "m_creationDate", "m_lastModificationDate", "m_lastUserModificationDate" });
    }

    /// <summary>
    /// The episode GUID of an element, or null when the creation date points outside the list or
    /// the list is not complete.
    /// </summary>
    internal static Guid? EpisodeGuid(ElemRecHistory history, IReadOnlyList<string> episodes, uint? episodeCount)
    {
        if (episodeCount is not uint count || count != episodes.Count) return null;
        long index = (long)count - 1 - history.CreationDate;
        if (index < 0 || index >= count) return null;
        return Guid.TryParseExact(episodes[(int)index], "D", out var guid) ? guid : null;
    }

    /// <summary>Revit UniqueId: the episode GUID and the original element id.</summary>
    internal static string UniqueId(Guid episode, ulong originalElementId) =>
        $"{episode:D}-{originalElementId:x8}";

    internal static string UniqueIdFromExportId(Guid exportId, ulong originalElementId) =>
        UniqueId(XorTail(exportId, originalElementId), originalElementId);

    /// <summary>
    /// The export-id rule: the episode GUID with the original element id XORed into its last 64 bits.
    /// </summary>
    internal static Guid ExportId(Guid episode, ulong originalElementId) =>
        XorTail(episode, originalElementId);

    /// <summary>
    /// <c>ExporterIFCUtils.CreateSubElementGUID</c>: the export id with the index XORed into bits
    /// 32-63.
    /// </summary>
    internal static Guid SubElementId(Guid exportId, int index) =>
        XorTail(exportId, (ulong)unchecked((uint)index) << 32);

    // The last eight GUID bytes are stored in text order.
    private static Guid XorTail(Guid guid, ulong value)
    {
        Span<byte> bytes = stackalloc byte[16];
        guid.TryWriteBytes(bytes);
        var tail = bytes[8..];
        BinaryPrimitives.WriteUInt64BigEndian(tail, BinaryPrimitives.ReadUInt64BigEndian(tail) ^ value);
        return new Guid(bytes);
    }
}
