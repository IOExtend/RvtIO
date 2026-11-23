using System.Buffers.Binary;
using ICSharpCode.SharpZipLib;
using ICSharpCode.SharpZipLib.Checksum;
using ICSharpCode.SharpZipLib.Zip.Compression;
using OpenMcdf;

namespace RvtIO.Parsing;

/// <summary>
/// Exact decoder for the single gzip member streams of the Global and Formats storages: one
/// gzip member after an 8-byte prefix (Global streams) or at the start (Formats/Latest),
/// followed by unused trailing bytes.
///
/// A stream longer than one page carries a foreign 353-byte segment after every 64,896 bytes:
/// the segments start at stream offsets 64,896 + k * 65,249. Each one begins with a zero byte
/// followed by a byte whose low nibble is zero, like the removable segments of partition pages
/// (<see cref="ChunkDecompressor"/>). A short stream has no segment and is decoded unchanged.
/// The result is accepted only when the CRC-32 and length of the gzip trailer that follows the
/// deflate data match, so a wrong page rule yields no data rather than wrong data.
/// </summary>
internal static class PagedGzipStream
{
    internal const int PageDataLength = 64_896;
    internal const int SegmentLength = 353;
    internal const int PageStride = PageDataLength + SegmentLength;
    /// <summary>Length of the prefix before the gzip member of a Global stream.</summary>
    internal const int PrefixLength = 8;

    /// <summary>Decoded bytes of <paramref name="storageName"/>/<paramref name="streamName"/>,
    /// or null when the stream is missing or does not decode exactly.</summary>
    internal static byte[]? Read(RootStorage root, string storageName, string streamName) =>
        Read(root, storageName, streamName, out _);

    /// <summary>Decoded bytes of <paramref name="storageName"/>/<paramref name="streamName"/> with raw stream length,
    /// or null when the stream is missing or does not decode exactly.</summary>
    internal static byte[]? Read(RootStorage root, string storageName, string streamName, out long rawLength)
    {
        rawLength = 0;
        try
        {
            var storage = root.OpenStorage(storageName);
            using var stream = storage.OpenStream(streamName);
            rawLength = stream.Length;
            var raw = new byte[stream.Length];
            stream.ReadExactly(raw);
            return Decompress(raw);
        }
        catch (Exception exception) when (exception is IOException or FileNotFoundException
            or DirectoryNotFoundException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Inflates the gzip member once every page segment is removed. Null when a
    /// segment is malformed, no deflate gzip header starts at offset 0 or 8, or the member does
    /// not match its own CRC-32 and length.</summary>
    internal static byte[]? Decompress(byte[] raw)
    {
        // The last page of a stream may end before its segment position (the member is complete and only padding
        // follows, measured on Global/ElemTable of rmebasic21): the strict removal fails there, the tail-tolerant one
        // keeps the padding as trailing bytes. The gzip CRC-32 and length below still prove the result.
        byte[]? body = RemoveSegments(raw) ?? RemoveSegments(raw, tolerateTail: true);
        if (body == null) return null;
        int start = IsGzipHeader(body, 0) ? 0 : IsGzipHeader(body, PrefixLength) ? PrefixLength : -1;
        if (start < 0) return null;
        return Inflate(body, start);
    }

    /// <summary>Inflates one gzip member and requires its CRC-32 and length trailer right after the deflate data.
    /// GZipStream cannot be used: it does not fail on a member cut before the trailer.</summary>
    private static byte[]? Inflate(byte[] body, int start)
    {
        int deflateStart = HeaderEnd(body, start);
        if (deflateStart < 0) return null;
        var inflater = new Inflater(noHeader: true);
        inflater.SetInput(body, deflateStart, body.Length - deflateStart);
        using var output = new MemoryStream();
        var buffer = new byte[81_920];
        try
        {
            while (!inflater.IsFinished)
            {
                int count = inflater.Inflate(buffer, 0, buffer.Length);
                if (count == 0 && (inflater.IsNeedingInput || inflater.IsNeedingDictionary)) return null;
                output.Write(buffer, 0, count);
            }
        }
        // the inflater indexes its code tables with unchecked lengths of a corrupt dynamic header
        catch (Exception exception) when (exception is SharpZipBaseException or IndexOutOfRangeException)
        {
            return null;
        }
        int trailer = deflateStart + (int)inflater.TotalIn;
        if (trailer + 8 > body.Length) return null;
        byte[] decoded = output.ToArray();
        var crc = new Crc32();
        crc.Update(decoded);
        return BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(trailer)) == (uint)crc.Value
            && BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(trailer + 4)) == (uint)decoded.Length
            ? decoded : null;
    }

    /// <summary>Offset of the deflate data after the gzip header (RFC 1952), or -1 when the header is cut,
    /// sets a reserved flag or carries a header CRC-16 that does not match.</summary>
    private static int HeaderEnd(byte[] data, int start)
    {
        int flags = data[start + 3], at = start + 10;
        if ((flags & 0xE0) != 0) return -1;
        if ((flags & 4) != 0)
        {
            if (at + 2 > data.Length) return -1;
            at += 2 + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
        }
        foreach (int bit in new[] { 8, 16 })
        {
            if ((flags & bit) == 0) continue;
            while (at < data.Length && data[at] != 0) at++;
            at++;
        }
        if ((flags & 2) != 0)
        {
            if (at + 2 > data.Length) return -1;
            var crc = new Crc32();
            crc.Update(data.AsSpan(start, at - start).ToArray());
            if (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)) != (ushort)crc.Value) return -1;
            at += 2;
        }
        return at <= data.Length ? at : -1;
    }

    private static bool IsGzipHeader(byte[] data, int offset) =>
        data.Length >= offset + 18
        && data[offset] == 0x1F && data[offset + 1] == 0x8B && data[offset + 2] == 0x08;

    /// <summary>True when a whole segment lies at <paramref name="start"/> of <paramref name="raw"/> and starts with its tag: a zero byte,
    /// then a byte whose low nibble is zero. The one rule behind every reader of the page segments.</summary>
    internal static bool HoldsSegment(ReadOnlySpan<byte> raw, long start) =>
        start + SegmentLength <= raw.Length && raw[(int)start] == 0 && (raw[(int)start + 1] & 0x0F) == 0;

    /// <summary>The stream without its page segments, or null when a segment position does
    /// not hold a segment (wrong tag, or the stream ends inside one). With <paramref name="tolerateTail"/> the first
    /// position after the first segment that holds none ends the removal and the rest of the stream is kept unchanged.</summary>
    internal static byte[]? RemoveSegments(byte[] raw, bool tolerateTail = false)
    {
        var body = new MemoryStream(raw.Length);
        int source = 0;
        for (long segment = PageDataLength; segment < raw.Length; segment += PageStride)
        {
            int start = (int)segment;
            if (!HoldsSegment(raw, start))
            {
                if (!tolerateTail || source == 0) return null; // the first boundary of a multi-page stream always holds a segment
                break;
            }
            body.Write(raw, source, start - source);
            source = start + SegmentLength;
        }
        body.Write(raw, source, raw.Length - source);
        return body.ToArray();
    }
}
