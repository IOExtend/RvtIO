using System.Buffers.Binary;
using RvtIO.Parsing;

namespace RvtIO.Writing;

/// <summary>
/// Refreshes the element totals of the current local increment in a transcoded
/// <c>Global/DocumentIncrementTable</c> stream. The last <c>DocumentIncrement</c> of
/// <c>m_localIncrements</c> (and its version with <c>m_causedByIncrementNumber</c> of -1)
/// carries the element count of the file as it will be saved; after elements are left out
/// of the downgrade that count is stale and an older Revit refuses the epoch whose
/// partition streams hold fewer elements than the table claims.
/// </summary>
internal static class DocumentIncrementFixup
{
    internal static byte[] RefreshTotals(byte[] data, SchemaDocument doc, uint totalElements)
    {
        int rootClass = BinaryPrimitives.ReadUInt16LittleEndian(data);
        if (doc.ById.GetValueOrDefault(rootClass)?.Name != "DocumentIncrementTable") return data;
        int tableId = rootClass;
        int incrementId = doc.Classes.FirstOrDefault(c => c.Name == "DocumentIncrement")?.Id ?? 0;
        int versionId = doc.Classes.FirstOrDefault(c => c.Name == "DocumentIncrementVersion")?.Id ?? 0;
        if (incrementId == 0 || versionId == 0) return data;

        var rootFields = PayloadSpan.Fields(data, 2, data.Length, doc, tableId);
        var local = rootFields.FirstOrDefault(f => f.Name == "m_localIncrements");
        if (local == default || local.End - local.Start < 4) return data;
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(local.Start, 4));
        if (count is <= 0 or > 100_000) return data;

        // inline items of the array, in stored order; the last one is the current increment
        var items = new List<(int Start, int End)>();
        int at = local.Start + 4;
        for (int i = 0; i < count; i++)
        {
            var fields = PayloadSpan.Fields(data, at, local.End, doc, incrementId);
            if (fields.Count == 0) return data;
            int end = fields[^1].End;
            if (end <= at || end > local.End) return data;
            items.Add((at, end));
            at = end;
        }
        if (items.Count == 0) return data;
        var last = items[^1];

        var result = data;
        bool changed = false;
        changed |= PatchU32(ref result, data, last, doc, incrementId, "m_totalElements", totalElements);

        // the versions of the last increment: the one with no causing increment carries the same total
        var lastFields = PayloadSpan.Fields(result, last.Start, last.End, doc, incrementId);
        var versions = lastFields.FirstOrDefault(f => f.Name == "m_incrementVersions");
        if (versions != default && versions.End - versions.Start >= 4)
        {
            int versionCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(versions.Start, 4));
            int vat = versions.Start + 4;
            for (int i = 0; i < versionCount; i++)
            {
                var vfields = PayloadSpan.Fields(result, vat, versions.End, doc, versionId);
                if (vfields.Count == 0) break;
                int vend = vfields[^1].End;
                if (vend <= vat || vend > versions.End) break;
                long causedBy = -2, total = -1;
                int totalAt = -1;
                foreach (var (fname, ffrom, fto) in vfields)
                {
                    if (fname == "m_causedByIncrementNumber" && fto - ffrom == 4)
                        causedBy = BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(ffrom, 4));
                    if (fname == "m_totalElements" && fto - ffrom == 4)
                    {
                        total = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(ffrom, 4));
                        totalAt = ffrom;
                    }
                }
                if (causedBy == -1 && total >= 0 && (uint)total != totalElements && totalAt >= 0)
                {
                    var next = new byte[result.Length];
                    Buffer.BlockCopy(result, 0, next, 0, result.Length);
                    BinaryPrimitives.WriteUInt32LittleEndian(next.AsSpan(totalAt, 4), totalElements);
                    result = next;
                    changed = true;
                    break;
                }
                vat = vend;
            }
        }
        return changed ? result : data;
    }

    private static bool PatchU32(ref byte[] result, byte[] data,
        (int Start, int End) item, SchemaDocument doc, int classId, string field, uint value)
    {
        foreach (var (name, from, to) in PayloadSpan.Fields(data, item.Start, item.End, doc, classId))
        {
            if (name != field || to - from != 4) continue;
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(from, 4)) == value) return false;
            var next = new byte[data.Length];
            Buffer.BlockCopy(data, 0, next, 0, data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(next.AsSpan(from, 4), value);
            result = next;
            return true;
        }
        return false;
    }
}
