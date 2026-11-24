namespace RvtIO.Parsing;

/// <summary>
/// Which copy of an element's records is the current one when several partition streams hold the element (a file saved
/// incrementally appends a partition with the modified elements and leaves the older copies in the older partitions).
///
/// The rule, checked on real files: the element row's m_lastModificationDate selects an increment through
/// Global/DocumentIncrementTable (<see cref="DocumentIncrementTable.Route"/>), the increment ordinal is the number of the partition
/// stream, and that partition's copy is current. The table is followed only where the copy it names is the latest holder's copy or has
/// its bytes (measured: 1,559 of the 1,570 ids whose copies differ, and 10 more); any other case keeps every copy, as the reader did
/// before. Copies that are byte-identical in all three channels are interchangeable: the one in the stream the reader decompresses
/// first stays.
/// Records of the keyed content blocks (loaded family documents) are never project elements and are not touched.
/// An element without an ElemTable row, or a table that does not decode, leaves every copy in place.
/// </summary>
internal static class RecordRouting
{
    /// <summary>One element record (all three channels counted) of the null-key map of one partition stream.</summary>
    internal readonly record struct Item(int Partition, ulong Owner, long Offset, int Length);

    /// <summary>The items of every partition stream and how to read them; build with <see cref="Collect"/>.</summary>
    internal sealed class Census
    {
        internal required FormatsSchema Schema { get; init; }
        internal required PartitionFraming.Tags Tags { get; init; }
        internal required int IdWidth { get; init; }
        internal Dictionary<ulong, List<int>> Holders { get; } = [];
        /// <summary>SHA-256 over all items (the three channels in file order) of one element in one partition stream.</summary>
        internal Dictionary<(ulong Owner, int Partition), byte[]> Digests { get; } = [];
        /// <summary>Stored length of each partition stream: the reader decompresses the longest first.</summary>
        internal Dictionary<int, long> StreamLengths { get; } = [];
        internal List<Item> Items { get; } = [];
        internal Dictionary<int, List<int>> SegmentLengths { get; } = [];
        internal int KeyedRecords { get; set; }
    }

    /// <summary>The marker classes and id width of the file, or null (with the reason) when the schema does not declare them.</summary>
    internal static Census? Begin(FormatsSchema? schema, out string? failure)
    {
        failure = null;
        if (schema == null) { failure = "no schema"; return null; }
        if (PartitionFraming.TagsOf(schema) is not { } tags) { failure = "schema has no framing marker classes"; return null; }
        if (ElemTableLayout.IdWidth(schema) is not int width) { failure = "schema has no element id width of the release in force"; return null; }
        return new Census { Schema = schema, Tags = tags, IdWidth = width };
    }

    /// <summary>Walks one partition stream into the census; false (with the reason) when its framing does not hold.</summary>
    internal static bool Collect(Census census, int partition, byte[] raw, out string? failure)
        => CollectCore(census, partition, raw, null, out failure);

    internal static bool CollectStream(Census census, int partition, Stream stream, out string? failure)
        => CollectCore(census, partition, null, stream, out failure);

    private static bool CollectCore(Census census, int partition, byte[]? raw, Stream? stream, out string? failure)
    {
        var lengths = new List<int>();
        var holders = new List<(ulong Owner, long Offset, int Length)>();
        var hashers = new Dictionary<ulong, System.Security.Cryptography.IncrementalHash>();
        int keyed = 0;
        bool ok;
        try
        {
            void CollectRecord(PartitionFraming.Record r)
            {
                if (r.Owner == (census.IdWidth == 8 ? ulong.MaxValue : uint.MaxValue)) return;
                if (r.Block != 0) { if (r.Channel == 101) keyed++; return; }
                holders.Add((r.Owner, r.Offset, r.Item.Length));
                if (!hashers.TryGetValue(r.Owner, out var hasher))
                    hashers[r.Owner] = hasher = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                hasher.AppendData(r.Item.Span);
            }
            ok = stream == null ? PartitionFraming.Walk(raw!, census.Tags, census.IdWidth, CollectRecord, out failure, out _, lengths)
                : PartitionFraming.WalkStream(stream, census.Tags, census.IdWidth, CollectRecord, out failure, out _, lengths);
            if (!ok) return false;
            foreach (var (owner, hasher) in hashers) census.Digests[(owner, partition)] = hasher.GetHashAndReset();
        }
        finally
        {
            foreach (var hasher in hashers.Values) hasher.Dispose();
        }
        census.StreamLengths[partition] = raw?.LongLength ?? stream!.Length;
        census.SegmentLengths[partition] = lengths;
        census.KeyedRecords += keyed;
        foreach (var (owner, offset, length) in holders)
        {
            census.Items.Add(new Item(partition, owner, offset, length));
            if (!census.Holders.TryGetValue(owner, out var list)) census.Holders[owner] = list = [];
            if (!list.Contains(partition)) list.Add(partition);
        }
        return true;
    }

    /// <summary>The partition whose copy of every repeated element is current (elements held by one partition are absent).</summary>
    internal static Dictionary<ulong, int> Current(Census census, IReadOnlyList<ElemTableLayout.Row> rows, DocumentIncrementTable.Table table)
    {
        var byId = new Dictionary<ulong, ElemTableLayout.Row>(rows.Count);
        foreach (var row in rows) byId[(ulong)row.Id] = row;
        var current = new Dictionary<ulong, int>();
        foreach (var (owner, partitions) in census.Holders)
        {
            if (partitions.Count < 2 || !byId.TryGetValue(owner, out var row)) continue;
            int? routed = row.LastModificationDate is int episode ? DocumentIncrementTable.Route(table, episode) : null;
            // Byte-identical copies are interchangeable. Keep the one in the stream the reader decompresses first (the longest, then
            // the lowest number): the scanners that walk the chunks in order have always reached that one.
            var digests = partitions.Select(p => census.Digests[(owner, p)]).ToList();
            int keep;
            if (digests.All(d => d.AsSpan().SequenceEqual(digests[0])))
                keep = partitions.OrderByDescending(p => census.StreamLengths[p]).ThenBy(p => p).First();
            // Differing copies: the table is followed where its copy has the content of the latest holder's copy (it is that copy in
            // 1,559 of 1,570 ids, and the same bytes in 10 more). Where it names another copy or none, nothing proves it and every copy
            // stays, as before.
            else if (routed is int r && partitions.Contains(r) && digests[partitions.IndexOf(r)].AsSpan().SequenceEqual(digests[partitions.IndexOf(partitions.Max())]))
                keep = partitions.Max();
            else continue;
            current[owner] = keep;
        }
        return current;
    }

    /// <summary>Byte ranges (in the inflated stream of the partition) of the records that are not the current copy.</summary>
    internal static List<(long Start, int Length)> Stale(Census census, Dictionary<ulong, int> current, int partition)
    {
        var ranges = new List<(long Start, int Length)>();
        foreach (var item in census.Items)
            if (item.Partition == partition && current.TryGetValue(item.Owner, out int keep) && keep != partition)
                ranges.Add((item.Offset, item.Length));
        ranges.Sort();
        return ranges;
    }

    /// <summary>The records of the framing of one partition stream without those the given ranges (<see cref="Stale"/>) mark as not
    /// current: both walks of the stream place an item at the same offset.</summary>
    internal static void Drop(List<PartitionFraming.ProjectItem> items, IReadOnlyList<(long Start, int Length)> stale)
    {
        var staleAt = stale.Select(s => s.Start).ToHashSet();
        items.RemoveAll(i => staleAt.Contains(i.Offset));
    }

    /// <summary>The chunks of one partition stream without the given byte ranges of its inflated stream. Null when the chunks are
    /// not the segments of the framing (same count, same lengths), so a range could not be placed exactly.</summary>
    internal static List<byte[]>? Prune(IReadOnlyList<byte[]> chunks, IReadOnlyList<int> segmentLengths, IReadOnlyList<(long Start, int Length)> ranges)
        => PruneCore(chunks, segmentLengths, ranges)?.ToList();

    internal static PartitionChunks? PrunePartition(PartitionChunks chunks, IReadOnlyList<int> segmentLengths, IReadOnlyList<(long Start, int Length)> ranges)
        => PruneCore(chunks, segmentLengths, ranges);

    private static PartitionChunks? PruneCore(IReadOnlyList<byte[]> chunks, IReadOnlyList<int> segmentLengths, IReadOnlyList<(long Start, int Length)> ranges)
    {
        if (chunks.Count != segmentLengths.Count) return null;
        for (int i = 0; i < chunks.Count; i++)
            if (PartitionChunks.LengthAt(chunks, i) != segmentLengths[i]) return null;
        var stored = chunks as PartitionChunks;
        var result = new PartitionChunks(stored?.Store);
        long chunkStart = 0;
        int next = 0;
        for (int i = 0; i < chunks.Count; i++)
        {
            long chunkEnd = chunkStart + segmentLengths[i];
            // a chunk no range reaches is kept as it is (the framing of the stream holds on to it as well)
            if (next >= ranges.Count || ranges[next].Start >= chunkEnd)
            {
                if (stored == null) result.Add(chunks[i]);
                else result.Add(stored.Reference(i));
                chunkStart = chunkEnd;
                continue;
            }
            var chunk = chunks[i];
            using var kept = new MemoryStream(chunk.Length);
            long position = chunkStart;
            while (next < ranges.Count && ranges[next].Start < chunkEnd)
            {
                var (start, length) = ranges[next];
                long from = Math.Max(start, chunkStart), to = Math.Min(start + length, chunkEnd);
                if (from > position) kept.Write(chunk, (int)(position - chunkStart), (int)(from - position));
                position = Math.Max(position, to);
                if (start + length <= chunkEnd) next++; else break; // a range that continues into the next chunk stays current
            }
            if (position < chunkEnd) kept.Write(chunk, (int)(position - chunkStart), (int)(chunkEnd - position));
            result.Add(kept.ToArray());
            chunkStart = chunkEnd;
        }
        return result;
    }
}
