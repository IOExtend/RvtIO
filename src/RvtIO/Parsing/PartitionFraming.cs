using System.Buffers.Binary;
using ICSharpCode.SharpZipLib;
using ICSharpCode.SharpZipLib.Checksum;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace RvtIO.Parsing;

/// <summary>
/// The saved framing of one partition stream (format 9), with the marker classes looked up by NAME in the file's own
/// schema instead of fixed class numbers (they differ per release: ContentMarker 779 in 2020, 817 in 2022, 869 in 2023, 891 in
/// 2024, 913 in 2025, 931 in 2026).
///
///   stream   := u32 9, u32 0, block*, terminal
///   block    := u16 ContentMarker, u32 pointer (0 = the null-key map, 0xFFFFFFFF = a keyed block), [pointer != 0: u16 ContentKey],
///               u32 owner count, [pointer != 0: 16-byte key], segment*, empty segment, u16 SignatureMarker, u32 n, n bytes, u32 m, 2*m bytes
///   terminal := ContentMarker, pointer 0, owner count 0xFFFFFFFF
///   segment  := u16 SegmentMarker, u32 flags, u32 record count, u32 size, u32 uncompressed length, [size != 0: u32 channel 101..103,
///               u32 opaque, size - 8 bytes of one gzip member, u16 SegmentCheckback, u32 size]
/// A chain of segments (flags bit 0 = continues the previous, bit 1 = continues the next) inflates to records
/// [owner:W][channel != 101: u32 extra][u32 size][size bytes][u32 size] with W the byte width of the schema's element id.
/// The null-key map holds the project's own elements; each keyed block is the document of one loaded family, whose records
/// must not be taken for project elements (the same element id occurs in several keyed blocks).
/// Every length, count, CRC and ISIZE is checked; any violation fails the whole stream (null with a reason).
/// </summary>
internal static class PartitionFraming
{
    internal readonly record struct Tags(ushort ContentMarker, ushort ContentKey, ushort SegmentMarker, ushort SegmentCheckback, ushort SignatureMarker);

    /// <summary>One record of channel 101..103. <see cref="Block"/> is the index of the content block, 0 = the null-key map.</summary>
    /// <summary><see cref="Offset"/> is the position of the whole item (owner to closing size) in the partition's inflated stream,
    /// that is the concatenation of every segment's output in file order; the item is <c>Item.Length</c> bytes there.</summary>
    internal readonly record struct Record(int Block, string Key, int Channel, ulong Owner, uint Extra, ReadOnlyMemory<byte> Body, ReadOnlyMemory<byte> Item, long Offset);

    /// <summary>Where a record of the null-key map lies: its channel, owner, and the whole item (owner to closing size) at <see cref="Offset"/>
    /// for <see cref="Length"/> bytes in the stream's inflated output.</summary>
    internal readonly record struct ProjectItem(int Channel, ulong Owner, long Offset, int Length);

    /// <summary>A partition stream read by its framing: the output of every segment in file order, and the records of the project's own block
    /// (the null-key map) in file order, without the chain terminators. The records of the keyed blocks (loaded family documents) are not listed.
    /// <see cref="IdWidth"/> is the byte width of the owner the walk read; <see cref="Partition"/> is the partition stream number when known.</summary>
    internal sealed record Framed(IReadOnlyList<byte[]> Segments, List<ProjectItem> Items, int IdWidth, int? Partition = null)
    {
        private long[]? starts;

        /// <summary>Where the bytes of the item are: in its segment when it lies in one, else in a copy (an item can run into the next segment
        /// of its chain); Copied tells which.</summary>
        internal (byte[] Data, int Start, bool Copied) Bytes(ProjectItem item)
        {
            if (starts == null)
            {
                var at = new long[Segments.Count];
                for (int i = 1; i < at.Length; i++) at[i] = at[i - 1] + Segments[i - 1].Length;
                starts = at;
            }
            int s = Array.BinarySearch(starts, item.Offset);
            if (s < 0) s = ~s - 1;
            while (s < starts.Length - 1 && starts[s + 1] <= item.Offset && Segments[s].Length == 0) s++;
            int from = (int)(item.Offset - starts[s]);
            if (from + (long)item.Length <= Segments[s].Length) return (Segments[s], from, false);
            var copy = new byte[item.Length];
            for (int done = 0; done < item.Length; s++, from = 0)
            {
                int n = Math.Min(item.Length - done, Segments[s].Length - from);
                Buffer.BlockCopy(Segments[s], from, copy, done, n);
                done += n;
            }
            return (copy, 0, true);
        }
    }

    internal static Tags? TagsOf(FormatsSchema schema)
    {
        ushort? Id(string name) => schema.IdOf(name) is int id and <= ushort.MaxValue ? (ushort)id : null;
        return Id("ContentMarker") is { } cm && Id("ContentKey") is { } ck && Id("SegmentMarker") is { } sm
            && Id("SegmentCheckback") is { } sc && Id("SignatureMarker") is { } sig
            ? new Tags(cm, ck, sm, sc, sig) : null;
    }

    /// <summary>Walks the stream; <paramref name="onRecord"/> sees every record of every block in file order. Returns false
    /// with the reason in <paramref name="failure"/> when the framing does not hold. <paramref name="idWidth"/> is 4 or 8.</summary>
    internal static bool Walk(byte[] raw, Tags tags, int idWidth, Action<Record> onRecord, out string? failure, out int blocks,
        List<int>? segmentLengths = null, List<byte[]>? segmentBytes = null)
    {
        failure = null;
        blocks = 0;
        try
        {
            var bytes = RemoveTails(raw);
            Require(bytes != null, "page-framing");
            var input = new Cursor(bytes!);
            Require(input.U32() == 9 && input.U32() == 0, "format");
            var keys = new HashSet<string>();
            long inflated = 0; // output of all finished chains
            while (true)
            {
                Require(input.U16() == tags.ContentMarker, "content-marker");
                uint pointer = input.U32();
                Require(pointer is 0 or uint.MaxValue, "content-key-pointer");
                if (pointer != 0) Require(input.U16() == tags.ContentKey, "content-key-class");
                uint expectedOwners = input.U32();
                string key = pointer == 0 ? "" : Convert.ToHexString(input.Bytes(16));
                if (expectedOwners == uint.MaxValue)
                {
                    Require(pointer == 0 && blocks > 0, "terminal-marker");
                    // what follows the terminal is no part of the stream (measured: 432 of 434 partition streams of 52 files have bytes after it)
                    return true;
                }
                Require((blocks != 0 || pointer == 0) && keys.Add(key), "map-key");
                HashSet<ulong>[] owners = [new(), new(), new()];
                using var chain = new MemoryStream();
                uint? chainIndex = null;
                long countSum = 0, sizeSum = 0;
                while (true)
                {
                    Require(input.U16() == tags.SegmentMarker, "segment-marker");
                    uint flags = input.U32(), count = input.U32(), size = input.U32(), unc = input.U32();
                    if (size == 0)
                    {
                        Require(flags == 0 && count == 0 && unc == 0 && chainIndex == null, "empty-segment");
                        break;
                    }
                    Require(size >= 8 && size <= int.MaxValue, "segment-length");
                    uint index = input.U32();
                    input.U32(); // the opaque value of the deposit is neither an owner nor a length
                    Require(index is >= 101 and <= 103, "segment-index");
                    var payload = input.Bytes((int)size - 8);
                    Require(input.U16() == tags.SegmentCheckback && input.U32() == size, "segment-checkback");
                    uint mode = flags & 3;
                    if (mode is 0 or 2) Require(chainIndex == null, "chain-start");
                    else Require(chainIndex == index, "chain-continuation");
                    chainIndex = index;
                    int before = (int)chain.Length;
                    Inflate(payload, chain);
                    segmentLengths?.Add((int)chain.Length - before);
                    segmentBytes?.Add(chain.GetBuffer().AsSpan(before, (int)chain.Length - before).ToArray());
                    countSum += count;
                    sizeSum += unc;
                    if (mode is not (0 or 1)) continue;

                    var memory = chain.GetBuffer().AsMemory(0, checked((int)chain.Length));
                    var records = new Cursor(memory);
                    long actualCount = 0, actualSize = 0;
                    while (records.Remaining > 0)
                    {
                        int begin = records.Position;
                        ulong owner = idWidth == 8 ? records.U64() : records.U32();
                        uint extra = index != 101 ? records.U32() : 0;
                        uint bodySize = records.U32();
                        Require(bodySize <= int.MaxValue, "record-length");
                        int bodyAt = records.Position;
                        records.Bytes((int)bodySize);
                        Require(records.U32() == bodySize, "record-footer");
                        actualCount++;
                        actualSize += bodySize;
                        // the channel census only counts real owners; the id -1 record is the chain terminator of the block
                        bool terminator = owner == (idWidth == 8 ? ulong.MaxValue : uint.MaxValue);
                        if (!terminator) owners[index - 101].Add(owner);
                        onRecord(new Record(blocks, key, (int)index, owner, extra, memory.Slice(bodyAt, (int)bodySize), memory.Slice(begin, records.Position - begin), inflated + begin));
                    }
                    Require(actualCount == countSum && actualSize == sizeSum, "chain-counts");
                    inflated += chain.Length;
                    chain.SetLength(0);
                    chain.Position = 0;
                    chainIndex = null;
                    countSum = sizeSum = 0;
                }
                Require(owners[0].Count == expectedOwners && owners[0].SetEquals(owners[1]) && owners[0].SetEquals(owners[2]), "owner-census");
                Require(input.U16() == tags.SignatureMarker, "signature-marker");
                input.Skip(input.U32());
                input.Skip(2L * input.U32());
                blocks++;
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or SharpZipBaseException or OverflowException)
        {
            failure = exception.Message;
            return false;
        }
    }

    /// <summary>The stream without its 353-byte page tails (<see cref="PagedGzipStream.RemoveSegments"/>). A stream that ends
    /// exactly at a tail position has a final tail that does not carry the zero tag of the others (measured: the 270th tail of
    /// a 2022 partition); it is removed only when it is the last bytes of the stream, and the block framing
    /// that follows rejects a wrong removal.</summary>
    internal static byte[]? RemoveTails(byte[] raw)
    {
        long last = PagedGzipStream.PageDataLength + (long)((raw.Length - PagedGzipStream.PageDataLength) / PagedGzipStream.PageStride) * PagedGzipStream.PageStride;
        if (raw.Length > PagedGzipStream.PageDataLength && last + PagedGzipStream.SegmentLength == raw.Length
            && PagedGzipStream.RemoveSegments(raw[..(int)last]) is { } body) return body;
        return PagedGzipStream.RemoveSegments(raw);
    }

    private static void Inflate(ReadOnlySpan<byte> payload, MemoryStream chain)
    {
        Require(payload.Length >= 18 && payload[..10].SequenceEqual(new byte[] { 0x1f, 0x8b, 8, 0, 0, 0, 0, 0, 0, 0x0b }), "gzip-header");
        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(payload[^8..]);
        uint expectedLength = BinaryPrimitives.ReadUInt32LittleEndian(payload[^4..]);
        // shortcut: independently compressed members only; dictionary-dependent members reject
        // admission until their shared-window contract is implemented and tested.
        var inflater = new Inflater(noHeader: true);
        inflater.SetInput(payload[10..^8].ToArray());
        var crc = new Crc32();
        byte[] buffer = new byte[32768];
        long length = 0;
        while (!inflater.IsFinished)
        {
            int count;
            try { count = inflater.Inflate(buffer); }
            // the inflater indexes its code tables with unchecked lengths of a corrupt dynamic header
            catch (IndexOutOfRangeException) { throw new InvalidDataException("gzip-deflate"); }
            Require(count > 0 || inflater.IsFinished, "gzip-deflate");
            length += count;
            Require(length <= expectedLength && chain.Length + count <= int.MaxValue, "gzip-length");
            crc.Update(new ArraySegment<byte>(buffer, 0, count));
            chain.Write(buffer, 0, count);
        }
        Require(inflater.RemainingInput == 0 && length == expectedLength && crc.Value == expectedCrc, "gzip-crc-length-or-extent");
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidDataException(reason);
    }

    private sealed class Cursor(ReadOnlyMemory<byte> bytes)
    {
        internal int Position { get; private set; }
        internal int Remaining => bytes.Length - Position;
        internal ReadOnlySpan<byte> Bytes(int count)
        {
            Require(count >= 0 && count <= Remaining, "bounds");
            var value = bytes.Span.Slice(Position, count);
            Position += count;
            return value;
        }
        internal ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Bytes(2));
        internal uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4));
        internal ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Bytes(8));
        internal void Skip(long count)
        {
            Require(count >= 0 && count <= Remaining, "signature-bounds");
            Position += (int)count;
        }
    }
}
