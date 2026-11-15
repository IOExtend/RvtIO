using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.Checksum;
using OpenMcdf;
using RvtIO.DeflateManaged;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;
using Xunit.Abstractions;

namespace RvtIO.Tests;

/// <summary>Cross-checks compressed streams and gzip members against <see cref="DeflateStream"/>, an independent inflater
/// outside both <c>SharpZipLib</c> and <see cref="InflaterManaged"/>. Asserts equal byte length, equal SHA-256 digest, and
/// equality with format-stored decoded lengths (gzip <c>ISIZE</c> trailers and format-9 partition segment <c>unc</c> headers).
/// Chained members that require a preset LZ77 window dictionary or Deflate64 are recorded and reported as skipped with their
/// reason rather than silently ignored.</summary>
public sealed class IndependentInflaterCrossCheckTests(ITestOutputHelper output)
{
    private static readonly byte[] NoDictionary = new byte[32768];
    private static readonly byte[] RvtGzipHeader = [0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0B];

    internal sealed record SkippedMember(string File, string Stream, int MemberIndex, string Reason, int ReaderDecodedBytes);

    internal sealed class CrossCheckReport
    {
        public int Files { get; set; }
        public int StreamsCompared { get; set; }
        public int StreamsSkippedOrPartial { get; set; }
        public int PlainStreams { get; set; }
        public int MembersCompared { get; set; }
        public List<SkippedMember> Skipped { get; } = [];
    }

    [Fact]
    public void CiAvailableSamplesAndSyntheticContainer_EveryCompressedStreamMatchesIndependentInflater()
    {
        string folder = Path.Combine(Path.GetTempPath(), "rvtio-inflater-crosscheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var fixtureFiles = RepoFaceMeshFixtures().ToList();
            Assert.NotEmpty(fixtureFiles);

            // 1. Cross-check all checked-in compressed .gz sample fixtures directly through both reader inflaters
            //    (PagedGzipStream + InflaterManaged) and the independent framework DeflateStream.
            int directFixturesCompared = 0;
            foreach (string gzPath in fixtureFiles)
            {
                byte[] gzBytes = File.ReadAllBytes(gzPath);
                CrossCheckSingleGzipPayload(Path.GetFileName(gzPath), "fixture", gzBytes, startOffset: 0);
                directFixturesCompared++;
            }
            Assert.Equal(fixtureFiles.Count, directFixturesCompared);

            // 2. Build a multi-stream .rvt compound file from checked-in >64 KiB fixtures, format-9 framed partitions
            //    crossing 64,896-byte page boundaries, and an unframed partition containing both a >128 KiB paged member
            //    with 220 trailing container bytes (the exact #92 shape) and a dictionary-chained member.
            string syntheticRvt = Path.Combine(folder, "ci-crosscheck.rvt");
            BuildSyntheticRvtContainer(syntheticRvt, fixtureFiles);

            var report = new CrossCheckReport();
            VerifyCompoundFile(syntheticRvt, report);

            output.WriteLine(
                $"direct_gz_fixtures={directFixturesCompared} files={report.Files} " +
                $"streams_compared={report.StreamsCompared} streams_skipped={report.StreamsSkippedOrPartial} " +
                $"plain_streams={report.PlainStreams} members_compared={report.MembersCompared} " +
                $"members_skipped={report.Skipped.Count}");
            foreach (var s in report.Skipped)
                output.WriteLine($"  skipped: {s.File} {s.Stream}#{s.MemberIndex} ({s.ReaderDecodedBytes:N0} B) -> {s.Reason}");

            Assert.Equal(1, report.Files);
            Assert.Equal(4, report.StreamsCompared);
            Assert.Equal(1, report.StreamsSkippedOrPartial);
            Assert.Equal(7, report.MembersCompared);
            var skipped = Assert.Single(report.Skipped);
            Assert.Equal("Partitions/2", skipped.Stream);
            Assert.Equal(1, skipped.MemberIndex);
            Assert.Contains("dictionary", skipped.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void SixtyFourKilobyteWindowDrainBugOfIssue92_IsCaughtByIndependentInflaterCrossCheck()
    {
        // Load a checked-in sample > 128 KiB unpacked (getess.json.gz is 133,746 bytes) so multi-window output is real.
        string gzPath = RepoFaceMeshFixtures().First(p => Path.GetFileName(p) == "getess.json.gz");
        byte[] rawGzip = File.ReadAllBytes(gzPath);
        int deflateStart = ParseGzipHeader(rawGzip, 0, rawGzip.Length);
        Assert.True(deflateStart >= 10);
        int deflateLen = rawGzip.Length - deflateStart - 8;
        uint storedIsize = BinaryPrimitives.ReadUInt32LittleEndian(rawGzip.AsSpan(rawGzip.Length - 4, 4));
        Assert.True(storedIsize > 131_072);

        byte[] deflateSlice = rawGzip.AsSpan(deflateStart, deflateLen).ToArray();
        byte[] independent = InflateWithFrameworkDeflate(deflateSlice);
        byte[] readerDrained = ChunkDecompressor.InflateRaw(deflateSlice, NoDictionary, 0)!;

        Assert.NotNull(readerDrained);
        Assert.Equal((int)storedIsize, independent.Length);
        Assert.Equal(independent.Length, readerDrained.Length);
        Assert.Equal(Sha256Hex(independent), Sha256Hex(readerDrained));

        // Simulate the pre-#92 loop condition (stopping on Finished() instead of Drained while decoded bytes still sit in _output).
        byte[] buggy64KiBCut = InflateStoppingOnFinishedLikeIssue92(deflateSlice);
        Assert.True(buggy64KiBCut.Length < independent.Length,
            $"Expected pre-#92 Finished() loop to stop early (< {independent.Length}), got {buggy64KiBCut.Length}.");
        Assert.NotEqual((uint)buggy64KiBCut.Length, storedIsize);
        Assert.NotEqual(Sha256Hex(independent), Sha256Hex(buggy64KiBCut));
    }

    [Fact]
    public void SelfContainedSixtyOneHundredByteGzip_ComparesValidMemberAndFailsOnCorruptCrcIsizeOrTruncatedTrailer()
    {
        byte[] payload = BuildDeterministicPayload(6_100);
        byte[] standardGzip = EncodeStandardGzip(payload);
        byte[] rvtMember = GzipMember.Write(payload);
        byte[] rvtWithMetadata = [.. rvtMember, .. new byte[32]];
        byte[] chainedValid = [.. rvtMember, .. new byte[32], .. rvtMember, .. new byte[32]];

        static CrossCheckReport VerifyRaw(string label, byte[] bytes)
        {
            var report = new CrossCheckReport();
            VerifyMemberStreamWithChunkDecompressor("synthetic.rvt", label, bytes, report);
            return report;
        }

        // Valid baseline: both plain GZipStream and RVT-wrapped self-contained members compare cleanly.
        var stdOk = VerifyRaw("std-valid", standardGzip);
        Assert.Equal(1, stdOk.MembersCompared);
        Assert.Empty(stdOk.Skipped);

        var rvtOk = VerifyRaw("rvt-valid", rvtWithMetadata);
        Assert.Equal(1, rvtOk.MembersCompared);
        Assert.Empty(rvtOk.Skipped);

        var multiOk = VerifyRaw("multi-valid", chainedValid);
        Assert.Equal(2, multiOk.MembersCompared);
        Assert.Empty(multiOk.Skipped);

        // Corrupting only the CRC-32 byte must fail rather than falling through to dictionary skip classification.
        AssertFailsWithoutSkip("std-bad-crc", CloneWithXor(standardGzip, standardGzip.Length - 8, 0x01));
        AssertFailsWithoutSkip("rvt-bad-crc", CloneWithXor(rvtWithMetadata, rvtWithMetadata.Length - 40, 0x01));
        AssertFailsWithoutSkip("member1-bad-crc", CloneWithXor(chainedValid, chainedValid.Length - 40, 0x01));

        // Corrupting ISIZE or truncating the 8-byte gzip trailer must likewise fail.
        AssertFailsWithoutSkip("std-bad-isize", CloneWithXor(standardGzip, standardGzip.Length - 4, 0x01));
        AssertFailsWithoutSkip("rvt-bad-isize", CloneWithXor(rvtWithMetadata, rvtWithMetadata.Length - 36, 0x01));
        AssertFailsWithoutSkip("std-truncated-trailer", standardGzip[..^4]);

        static void AssertFailsWithoutSkip(string label, byte[] corruptStream)
        {
            var report = new CrossCheckReport();
            Assert.ThrowsAny<Exception>(() => VerifyMemberStreamWithChunkDecompressor("synthetic.rvt", label, corruptStream, report));
            Assert.Empty(report.Skipped);
        }
    }

    [Fact]
    public void IntegrityMismatches_FailAcrossDictionaryChainedPagedAndFramedStreams()
    {
        byte[] firstPayload = BuildDeterministicPayload(4_096);
        byte[] firstMember = GzipMember.Write(firstPayload);
        byte[] chainedMember = BuildDictionaryChainedGzipMember(firstPayload);

        byte[] validTwoMember = [.. firstMember, .. new byte[32], .. chainedMember, .. new byte[32]];
        var validReport = new CrossCheckReport();
        VerifyMemberStreamWithChunkDecompressor("synthetic.rvt", "Partitions/dict", validTwoMember, validReport);
        Assert.Equal(1, validReport.MembersCompared);
        var skipped = Assert.Single(validReport.Skipped);
        Assert.Equal(1, skipped.MemberIndex);
        Assert.Contains("dictionary", skipped.Reason, StringComparison.OrdinalIgnoreCase);

        // Flipping CRC or ISIZE on a genuinely dictionary-chained member must fail instead of reporting skipped.
        Assert.ThrowsAny<Exception>(() => VerifyMemberStreamWithChunkDecompressor(
            "synthetic.rvt", "dict-bad-crc", CloneWithXor(validTwoMember, validTwoMember.Length - 40, 0x01), new CrossCheckReport()));
        Assert.ThrowsAny<Exception>(() => VerifyMemberStreamWithChunkDecompressor(
            "synthetic.rvt", "dict-bad-isize", CloneWithXor(validTwoMember, validTwoMember.Length - 36, 0x01), new CrossCheckReport()));

        // An orphan dictionary back-reference at MemberIndex 0 (empty window history) must fail even on all-zero data.
        byte[] orphanChained = [.. BuildDictionaryChainedGzipMember(new byte[4_096]), .. new byte[32]];
        Assert.Throws<InvalidDataException>(() => VerifyMemberStreamWithChunkDecompressor(
            "synthetic.rvt", "orphan-dict", orphanChained, new CrossCheckReport()));

        // A reader output mismatch (wrong byte / wrong length) must fail on both self-contained and chained members.
        byte[] readerWrongByte = CloneWithXor(firstPayload, 128, 0xFF);
        Assert.ThrowsAny<Exception>(() => VerifySingleStrippedMember(
            "synthetic.rvt", "self-hash", 0, firstMember, [], readerWrongByte, out _));
        Assert.ThrowsAny<Exception>(() => VerifySingleStrippedMember(
            "synthetic.rvt", "self-len", 0, firstMember, [], firstPayload[..^1], out _));

        byte[] chainedDecoded = new ChunkDecompressor().DecompressAll(validTwoMember)[1];
        Assert.ThrowsAny<Exception>(() => VerifySingleStrippedMember(
            "synthetic.rvt", "dict-hash", 1, chainedMember, [firstPayload], CloneWithXor(chainedDecoded, 0, 0xFF), out _));

        // Corrupting CRC in a paged Global/Latest stream must fail in VerifyPagedStream.
        byte[] pagedValid = PagedStreamCodec.Encode([1, 0, 0, 0, 0, 0, 0, 0], firstPayload);
        byte[] pagedCorrupt = CloneWithXor(pagedValid, PagedGzipStream.PrefixLength + firstMember.Length - 8, 0x01);
        Assert.ThrowsAny<Exception>(() => VerifyPagedStream("synthetic.rvt", "Global/Latest", pagedCorrupt, new CrossCheckReport()));

        // Corrupting segment CRC in a format-9 framed partition must fail in VerifyPartitionStream.
        var tags = new PartitionFraming.Tags(
            CoverageCensusTests.Id("ContentMarker"),
            CoverageCensusTests.Id("ContentKey"),
            CoverageCensusTests.Id("SegmentMarker"),
            CoverageCensusTests.Id("SegmentCheckback"),
            CoverageCensusTests.Id("SignatureMarker"));
        byte[] framedValid = CoverageCensusTests.Partition(CoverageCensusTests.Block(false, CoverageCensusTests.R(1, firstPayload)));
        int marker = ChunkDecompressor.FindMemberMarkers(framedValid)[0];
        uint segSize = BinaryPrimitives.ReadUInt32LittleEndian(framedValid.AsSpan(marker - 16, 4));
        int crcOffset = marker + checked((int)segSize - 8) - 8;
        byte[] framedCorrupt = CloneWithXor(framedValid, crcOffset, 0x01);
        Assert.ThrowsAny<Exception>(() => VerifyPartitionStream(
            "synthetic.rvt", "Partitions/framed", framedCorrupt, (tags, 4), new CrossCheckReport()));
    }

    private static byte[] BuildDeterministicPayload(int length)
    {
        byte[] data = new byte[length];
        ReadOnlySpan<byte> template = "{\"id\":1024,\"category\":\"OST_Walls\",\"values\":[1.25,2.5,3.75,5.0]}\n"u8;
        for (int i = 0; i < data.Length; i++)
            data[i] = i % 97 == 0 ? (byte)((i * 31) & 0xFF) : template[i % template.Length];
        return data;
    }

    private static byte[] EncodeStandardGzip(byte[] payload)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(payload);
        return ms.ToArray();
    }

    private static byte[] CloneWithXor(byte[] source, int index, byte mask)
    {
        byte[] copy = (byte[])source.Clone();
        copy[index] ^= mask;
        return copy;
    }

    [SkippableFact]
    public void PublicSamples_EveryCompressedStreamMatchesIndependentInflaterAndReportsCounts()
    {
        var sampleFiles = SampleContainerFiles();
        var report = new CrossCheckReport();
        foreach (string path in sampleFiles)
            VerifyCompoundFile(path, report);

        output.WriteLine(
            $"files={report.Files} streams_compared={report.StreamsCompared} " +
            $"streams_skipped={report.StreamsSkippedOrPartial} plain_streams={report.PlainStreams} " +
            $"members_compared={report.MembersCompared} members_skipped={report.Skipped.Count}");
        foreach (var group in report.Skipped.GroupBy(s => (s.File, s.Stream, s.Reason)))
        {
            output.WriteLine(
                $"  skipped: {group.Key.File} {group.Key.Stream} count={group.Count()} " +
                $"bytes={group.Sum(x => (long)x.ReaderDecodedBytes):N0} reason={group.Key.Reason}");
        }

        Assert.True(report.Files > 0);
        Assert.True(report.StreamsCompared > 0);
        Assert.True(report.MembersCompared > 0);
    }

    private static List<string> SampleContainerFiles()
    {
        var files = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string root in TestFiles.Roots())
        {
            foreach (string pattern in new[] { "*.rvt", "*.rfa" })
            {
                foreach (string file in Directory.EnumerateFiles(root, pattern)) files.Add(file);
                foreach (string sub in Directory.EnumerateDirectories(root))
                    foreach (string file in Directory.EnumerateFiles(sub, pattern)) files.Add(file);
            }
        }
        if (files.Count == 0) throw TestFiles.Missing("*.rvt or *.rfa sample files");
        return [.. files];
    }

    private static IEnumerable<string> RepoFaceMeshFixtures()
    {
        for (string? dir = AppContext.BaseDirectory; dir != null; dir = Path.GetDirectoryName(dir))
        {
            string candidate = Path.Combine(dir, "tests", "RvtIO.Tests", "Fixtures", "FaceMesh");
            if (Directory.Exists(candidate))
                return Directory.EnumerateFiles(candidate, "*.gz").Order(StringComparer.Ordinal).ToList();
        }
        throw new DirectoryNotFoundException("Could not locate tests/RvtIO.Tests/Fixtures/FaceMesh.");
    }

    private static void VerifyCompoundFile(string path, CrossCheckReport report)
    {
        string fileLabel = Path.GetFileName(path);
        using var root = RootStorage.OpenRead(path);
        var framing = ResolveFraming(root);
        var entries = new List<(string StreamPath, Storage Parent, string Name, long Length)>();
        CollectStreams(root, "", entries);

        report.Files++;
        foreach (var (streamPath, parent, name, length) in entries)
        {
            bool isPaged = streamPath.StartsWith("Formats/", StringComparison.Ordinal)
                || streamPath.StartsWith("Global/", StringComparison.Ordinal);
            bool isPartition = streamPath.StartsWith("Partitions/", StringComparison.Ordinal);

            if (!isPaged && !isPartition)
            {
                report.PlainStreams++;
                continue;
            }
            if (length == 0)
            {
                report.PlainStreams++;
                continue;
            }

            byte[] raw = new byte[length];
            using (var s = parent.OpenStream(name))
                s.ReadExactly(raw);

            if (isPaged)
            {
                VerifyPagedStream(fileLabel, streamPath, raw, report);
            }
            else
            {
                VerifyPartitionStream(fileLabel, streamPath, raw, framing, report);
            }
        }
    }

    private static (PartitionFraming.Tags Tags, int IdWidth)? ResolveFraming(RootStorage root)
    {
        if (FormatsSchema.TryRead(root) is not { } formats) return null;
        if (PartitionFraming.TagsOf(formats) is not { } tags) return null;
        int? saved = SchemaRelease.Saved(BasicFileInfoReader.Read(root).GetValueOrDefault("RevitVersion"));
        int? effective = SchemaRelease.Effective(saved, SchemaRelease.FromSchema(formats));
        return SchemaRelease.IdWidth(effective) is int idWidth ? (tags, idWidth) : null;
    }

    private static void VerifyPagedStream(string fileLabel, string streamPath, byte[] raw, CrossCheckReport report)
    {
        byte[]? readerExact = PagedGzipStream.Decompress(raw);
        if (readerExact != null)
        {
            byte[] body = (PagedGzipStream.RemoveSegments(raw) ?? PagedGzipStream.RemoveSegments(raw, tolerateTail: true))!;
            int start = IsGzipMagic(body, 0) ? 0 : PagedGzipStream.PrefixLength;
            var (independent, storedCrc, storedIsize) = CrossCheckSingleGzipPayload(fileLabel, streamPath, body, start);

            Assert.Equal(independent.Length, readerExact.Length);
            Assert.Equal((uint)readerExact.Length, storedIsize);
            Assert.Equal(Crc32Value(readerExact), storedCrc);
            Assert.Equal(Sha256Hex(independent), Sha256Hex(readerExact));

            report.StreamsCompared++;
            report.MembersCompared++;
            return;
        }

        // Fallback for non-standard Global/Formats streams (e.g. tiny uncompressed index streams or multi-member streams).
        VerifyMemberStreamWithChunkDecompressor(fileLabel, streamPath, raw, report);
    }

    private static void VerifyPartitionStream(
        string fileLabel,
        string streamPath,
        byte[] raw,
        (PartitionFraming.Tags Tags, int IdWidth)? framing,
        CrossCheckReport report)
    {
        if (framing is var (tags, idWidth))
        {
            bool isFormat9 = raw.Length >= 10
                && BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(0, 4)) == 9
                && BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(4, 4)) == 0
                && BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(8, 2)) == tags.ContentMarker;
            var framedSegments = new List<byte[]>();
            bool walked = PartitionFraming.Walk(raw, tags, idWidth, _ => { }, out string? walkFailure, out _, null, framedSegments);
            if (isFormat9)
                Assert.True(walked, $"{fileLabel} {streamPath}: format-9 framed partition failed framing or segment integrity check: {walkFailure}.");
            if (walked)
            {
                VerifyFramedPartitionSegments(fileLabel, streamPath, raw, tags, idWidth, framedSegments);

                // Also cross-check ChunkDecompressor.DecompressAll when every framed segment exceeds its >10-byte filter,
                // ensuring the raw-deflate / page-stripping member path agrees byte-for-byte with both PartitionFraming and DeflateStream.
                if (raw.Length <= 4_000_000 && framedSegments.All(seg => seg.Length > 10))
                {
                    var decompressor = new ChunkDecompressor();
                    var memberChunks = decompressor.DecompressAll(raw);
                    if (memberChunks.Count == framedSegments.Count && decompressor.LastStats.Failed == 0 && decompressor.LastStats.Unverified == 0)
                    {
                        for (int i = 0; i < framedSegments.Count; i++)
                        {
                            Assert.Equal(framedSegments[i].Length, memberChunks[i].Length);
                            Assert.Equal(Sha256Hex(framedSegments[i]), Sha256Hex(memberChunks[i]));
                        }
                    }
                }

                report.StreamsCompared++;
                report.MembersCompared += framedSegments.Count;
                return;
            }
        }

        VerifyMemberStreamWithChunkDecompressor(fileLabel, streamPath, raw, report);
    }

    private static void VerifyFramedPartitionSegments(
        string fileLabel,
        string streamPath,
        byte[] raw,
        PartitionFraming.Tags tags,
        int idWidth,
        List<byte[]> readerSegments)
    {
        byte[] body = PartitionFraming.RemoveTails(raw)
            ?? throw new InvalidDataException($"{fileLabel} {streamPath}: page tails could not be removed.");
        int pos = 8; // format 9, 0
        int segIdx = 0;

        while (true)
        {
            ushort contentMarker = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(pos, 2));
            Assert.Equal(tags.ContentMarker, contentMarker);
            pos += 2;
            uint pointer = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4));
            pos += 4;
            if (pointer != 0) pos += 2; // ContentKey class
            uint expectedOwners = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4));
            pos += 4;
            if (pointer != 0) pos += 16; // 16-byte key
            if (expectedOwners == uint.MaxValue) break;

            using var chain = new MemoryStream();
            long expectedChainCount = 0;
            long expectedChainBodyBytes = 0;

            while (true)
            {
                ushort segMarker = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(pos, 2));
                Assert.Equal(tags.SegmentMarker, segMarker);
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos + 2, 4));
                uint count = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos + 6, 4));
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos + 10, 4));
                uint unc = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos + 14, 4));
                pos += 18;
                if (size == 0) break;

                uint channel = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4));
                pos += 8; // channel + opaque dword
                int payloadLen = checked((int)size - 8);
                byte[] payload = body.AsSpan(pos, payloadLen).ToArray();
                pos += payloadLen;
                pos += 6; // SegmentCheckback + size

                var (independent, storedCrc, storedIsize) = CrossCheckSingleGzipPayload(
                    fileLabel, $"{streamPath}[seg {segIdx}]", payload, startOffset: 0);

                byte[] readerSeg = readerSegments[segIdx];
                Assert.Equal(independent.Length, readerSeg.Length);
                Assert.Equal((uint)readerSeg.Length, storedIsize);
                Assert.Equal(Crc32Value(readerSeg), storedCrc);
                Assert.Equal(Sha256Hex(independent), Sha256Hex(readerSeg));

                chain.Write(independent);
                expectedChainCount += count;
                expectedChainBodyBytes += unc;
                segIdx++;

                uint mode = flags & 3;
                if (mode is 0 or 1)
                {
                    var (actualCount, actualBodyBytes) = MeasureRecordChain(chain.ToArray(), idWidth, hasExtra: channel != 101);
                    Assert.Equal(expectedChainCount, actualCount);
                    Assert.Equal(expectedChainBodyBytes, actualBodyBytes);
                    chain.SetLength(0);
                    expectedChainCount = 0;
                    expectedChainBodyBytes = 0;
                }
            }

            // SignatureMarker: u16 tag, u32 n, n bytes, u32 m, 2*m bytes
            Assert.Equal(tags.SignatureMarker, BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(pos, 2)));
            pos += 2;
            uint sigBytes = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4));
            pos += 4 + checked((int)sigBytes);
            uint sigWords = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos, 4));
            pos += 4 + checked((int)(2L * sigWords));
        }

        Assert.Equal(readerSegments.Count, segIdx);
    }

    private static (long RecordCount, long BodyBytesSum) MeasureRecordChain(byte[] chainBytes, int idWidth, bool hasExtra)
    {
        int p = 0;
        long c = 0, s = 0;
        int extraBytes = hasExtra ? 4 : 0;
        int header = idWidth + extraBytes + 4;
        while (p < chainBytes.Length)
        {
            if (p + header + 4 > chainBytes.Length)
                throw new InvalidDataException("Inflated segment chain ended mid-record header.");
            uint bodyLen = BinaryPrimitives.ReadUInt32LittleEndian(chainBytes.AsSpan(p + idWidth + extraBytes, 4));
            long end = (long)p + header + bodyLen;
            if (end + 4 > chainBytes.Length)
                throw new InvalidDataException("Inflated segment chain ended mid-record body.");
            uint footer = BinaryPrimitives.ReadUInt32LittleEndian(chainBytes.AsSpan((int)end, 4));
            if (footer != bodyLen)
                throw new InvalidDataException($"Inflated segment record footer {footer} did not match header {bodyLen}.");
            p = (int)end + 4;
            c++;
            s += bodyLen;
        }
        return (c, s);
    }

    private static void VerifyMemberStreamWithChunkDecompressor(
        string fileLabel,
        string streamPath,
        byte[] raw,
        CrossCheckReport report)
    {
        byte[] repaired = ChunkDecompressor.MoveSegmentsBeforeSplitHeaders(raw);
        var markers = ChunkDecompressor.FindMemberMarkers(repaired);
        if (markers.Count == 0)
        {
            report.PlainStreams++;
            return;
        }

        var decompressor = new ChunkDecompressor();
        var readerChunks = decompressor.DecompressAll(raw);
        Assert.Equal(0, decompressor.LastStats.Failed);
        Assert.Equal(markers.Count, readerChunks.Count);

        bool anySkipped = false;
        var windowHistory = new List<byte[]>();

        for (int mi = 0; mi < markers.Count; mi++)
        {
            int st = markers[mi];
            int end = mi + 1 < markers.Count ? markers[mi + 1] : repaired.Length;
            byte[] stripped = StripPageSegmentsInRange(repaired, st, end, raw.Length);
            byte[] readerChunk = readerChunks[mi];

            if (VerifySingleStrippedMember(fileLabel, streamPath, mi, stripped, windowHistory, readerChunk, out string? skipReason))
            {
                report.MembersCompared++;
            }
            else
            {
                report.Skipped.Add(new SkippedMember(fileLabel, streamPath, mi, skipReason!, readerChunk.Length));
                anySkipped = true;
            }

            windowHistory.Add(readerChunk);
        }

        if (anySkipped) report.StreamsSkippedOrPartial++;
        else report.StreamsCompared++;
    }

    private static (byte[] Independent, uint StoredCrc, uint StoredIsize) CrossCheckSingleGzipPayload(
        string fileLabel,
        string streamLabel,
        byte[] body,
        int startOffset)
    {
        int deflateStart = ParseGzipHeader(body, startOffset, body.Length - startOffset);
        Assert.True(deflateStart >= startOffset + 10, $"{fileLabel} {streamLabel}: invalid gzip header.");

        // 1. Reader's managed inflater (InflaterManaged, where #92 lived): decode and locate exact deflate end.
        var managed = new InflaterManaged(deflate64: false, uncompressedSize: -1) { Strict = true };
        managed.SetInput(body, deflateStart, body.Length - deflateStart);
        using var managedOut = new MemoryStream();
        byte[] buf = new byte[65536];
        while (!managed.Drained)
        {
            int n = managed.Inflate(buf, 0, buf.Length);
            if (n == 0) break;
            managedOut.Write(buf, 0, n);
        }
        Assert.True(managed.Finished(), $"{fileLabel} {streamLabel}: InflaterManaged did not finish.");
        byte[] managedBytes = managedOut.ToArray();

        int deflateByteLength = (body.Length - deflateStart) - managed.UnconsumedInputBytes;
        int trailerOffset = deflateStart + deflateByteLength;
        Assert.True(trailerOffset + 8 <= body.Length, $"{fileLabel} {streamLabel}: missing 8-byte gzip trailer.");
        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(trailerOffset, 4));
        uint storedIsize = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(trailerOffset + 4, 4));

        // 2. Also verify ChunkDecompressor.InflateRaw directly on the exact deflate slice.
        byte[]? rawHelperBytes = ChunkDecompressor.InflateRaw(body.AsSpan(deflateStart, deflateByteLength).ToArray(), NoDictionary, 0);
        Assert.NotNull(rawHelperBytes);

        // 3. Independent inflater: System.IO.Compression.DeflateStream on the exact deflate slice.
        byte[] independent = InflateWithFrameworkDeflate(body.AsSpan(deflateStart, deflateByteLength).ToArray());

        Assert.Equal((uint)independent.Length, storedIsize);
        Assert.Equal(Crc32Value(independent), storedCrc);
        Assert.Equal(independent.Length, managedBytes.Length);
        Assert.Equal(independent.Length, rawHelperBytes.Length);
        string expectedSha = Sha256Hex(independent);
        Assert.Equal(expectedSha, Sha256Hex(managedBytes));
        Assert.Equal(expectedSha, Sha256Hex(rawHelperBytes));

        return (independent, storedCrc, storedIsize);
    }

    /// <summary>Verifies one stripped gzip member. Returns true when compared against self-contained
    /// <see cref="DeflateStream"/>, or false with <paramref name="skipReason"/> only when strict decoding
    /// proves an unsupported preceding-dictionary dependency or Deflate64 codec and every trailer CRC,
    /// <c>ISIZE</c>, and <paramref name="readerChunk"/> digest check still passes.</summary>
    internal static bool VerifySingleStrippedMember(
        string fileLabel,
        string streamPath,
        int memberIndex,
        byte[] strippedMember,
        IReadOnlyList<byte[]> precedingChunks,
        byte[] readerChunk,
        out string? skipReason)
    {
        int headerEnd = ParseGzipHeader(strippedMember, 0, strippedMember.Length);
        Assert.True(headerEnd >= 10, $"{fileLabel} {streamPath}#{memberIndex}: invalid gzip header.");

        // First probe strict self-contained plain Deflate (no preceding dictionary, no Deflate64).
        // Once plain Deflate succeeds, never fall back to unsupported-codec classification: any
        // missing trailer, CRC-32/ISIZE mismatch, or reader hash mismatch must fail immediately.
        if (TryProbeSelfContainedPlainDeflate(strippedMember, headerEnd, out int deflateByteLength, out byte[]? probeBytes))
        {
            int trailer = headerEnd + deflateByteLength;
            Assert.True(trailer + 8 <= strippedMember.Length, $"{fileLabel} {streamPath}#{memberIndex}: missing 8-byte gzip trailer.");

            uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(strippedMember.AsSpan(trailer, 4));
            uint storedIsize = BinaryPrimitives.ReadUInt32LittleEndian(strippedMember.AsSpan(trailer + 4, 4));
            byte[] independent = InflateWithFrameworkDeflate(strippedMember.AsSpan(headerEnd, deflateByteLength).ToArray());

            Assert.Equal(storedIsize, (uint)independent.Length);
            Assert.Equal(storedCrc, Crc32Value(independent));
            Assert.Equal(independent.Length, probeBytes!.Length);
            Assert.Equal(Sha256Hex(independent), Sha256Hex(probeBytes));

            Assert.Equal(independent.Length, readerChunk.Length);
            Assert.Equal(storedIsize, (uint)readerChunk.Length);
            Assert.Equal(storedCrc, Crc32Value(readerChunk));
            Assert.Equal(Sha256Hex(independent), Sha256Hex(readerChunk));

            skipReason = null;
            return true;
        }

        skipReason = VerifyUnsupportedCodecOrDictionaryMember(
            fileLabel, streamPath, memberIndex, strippedMember, headerEnd, precedingChunks, readerChunk);
        return false;
    }

    private static bool TryProbeSelfContainedPlainDeflate(
        byte[] strippedMember,
        int headerEnd,
        out int deflateByteLength,
        out byte[]? probeBytes)
    {
        deflateByteLength = 0;
        probeBytes = null;

        try
        {
            var probe = new InflaterManaged(deflate64: false, uncompressedSize: -1) { Strict = true };
            probe.SetInput(strippedMember, headerEnd, strippedMember.Length - headerEnd);
            using var ms = new MemoryStream();
            byte[] scratch = new byte[65536];
            while (!probe.Drained)
            {
                int n = probe.Inflate(scratch, 0, scratch.Length);
                if (n == 0) break;
                ms.Write(scratch, 0, n);
            }
            if (!probe.Finished()) return false;

            deflateByteLength = (strippedMember.Length - headerEnd) - probe.UnconsumedInputBytes;
            probeBytes = ms.ToArray();
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static string VerifyUnsupportedCodecOrDictionaryMember(
        string fileLabel,
        string streamPath,
        int memberIndex,
        byte[] strippedMember,
        int headerEnd,
        IReadOnlyList<byte[]> precedingChunks,
        byte[] readerChunk)
    {
        byte[] dict = new byte[32768];
        int dictLen = 0;
        for (int i = precedingChunks.Count - 1; i >= 0 && dictLen < dict.Length; i--)
        {
            byte[] prev = precedingChunks[i];
            int take = Math.Min(prev.Length, dict.Length - dictLen);
            Array.Copy(prev, prev.Length - take, dict, dict.Length - dictLen - take, take);
            dictLen += take;
        }

        var candidates = new List<(bool Deflate64, bool UseDictionary, string Reason)>();
        if (dictLen > 0)
            candidates.Add((false, true, "chained member requires preceding 32 KiB LZ77 dictionary"));
        candidates.Add((true, false, "member uses Deflate64"));
        if (dictLen > 0)
            candidates.Add((true, true, "chained member uses Deflate64 and shared 32 KiB LZ77 dictionary"));

        foreach (var (d64, useDict, reason) in candidates)
        {
            byte[]? decoded = null;
            int unconsumed = 0;
            try
            {
                var inflater = new InflaterManaged(deflate64: d64, uncompressedSize: -1) { Strict = true };
                if (useDict) inflater.SetDictionary(dict, dict.Length - dictLen, dictLen);
                inflater.SetInput(strippedMember, headerEnd, strippedMember.Length - headerEnd);
                using var ms = new MemoryStream();
                byte[] buf = new byte[65536];
                while (!inflater.Drained)
                {
                    int n = inflater.Inflate(buf, 0, buf.Length);
                    if (n == 0) break;
                    ms.Write(buf, 0, n);
                }
                if (!inflater.Finished()) continue;
                decoded = ms.ToArray();
                unconsumed = inflater.UnconsumedInputBytes;
            }
            catch (InvalidDataException)
            {
                continue;
            }

            // Strictly validate trailer presence, ISIZE, CRC-32, and reader output outside the
            // InvalidDataException handler so integrity mismatches fail immediately.
            int trailer = strippedMember.Length - unconsumed;
            Assert.True(trailer + 8 <= strippedMember.Length, $"{fileLabel} {streamPath}#{memberIndex}: missing 8-byte gzip trailer.");

            uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(strippedMember.AsSpan(trailer, 4));
            uint storedIsize = BinaryPrimitives.ReadUInt32LittleEndian(strippedMember.AsSpan(trailer + 4, 4));

            Assert.Equal(storedIsize, (uint)decoded.Length);
            Assert.Equal(storedCrc, Crc32Value(decoded));
            Assert.Equal(decoded.Length, readerChunk.Length);
            Assert.Equal(storedIsize, (uint)readerChunk.Length);
            Assert.Equal(storedCrc, Crc32Value(readerChunk));
            Assert.Equal(Sha256Hex(decoded), Sha256Hex(readerChunk));
            return reason;
        }

        throw new InvalidDataException(
            $"{fileLabel} {streamPath}#{memberIndex}: member failed strict decode as self-contained Deflate, dictionary-chained Deflate, or Deflate64.");
    }

    private static byte[] StripPageSegmentsInRange(byte[] repaired, int start, int end, int totalRawLength)
    {
        using var body = new MemoryStream(end - start);
        int source = start;
        for (long segment = PagedGzipStream.PageDataLength; segment < end; segment += PagedGzipStream.PageStride)
        {
            if (segment < start) continue;
            if (segment + PagedGzipStream.SegmentLength > totalRawLength) break;
            long overrun = segment + PagedGzipStream.SegmentLength - end;
            long at = overrun >= 1 && overrun < RvtGzipHeader.Length ? segment - overrun : segment;
            if (at + PagedGzipStream.SegmentLength > end || repaired[at] != 0 || (repaired[at + 1] & 0x0F) != 0)
                break;
            body.Write(repaired, source, (int)at - source);
            source = (int)at + PagedGzipStream.SegmentLength;
        }
        body.Write(repaired, source, end - source);
        return body.ToArray();
    }

    private static byte[] InflateWithFrameworkDeflate(byte[] rawDeflate)
    {
        using var input = new MemoryStream(rawDeflate, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] InflateStoppingOnFinishedLikeIssue92(byte[] rawDeflate)
    {
        var inflater = new InflaterManaged(deflate64: false, uncompressedSize: -1);
        inflater.SetInput(rawDeflate, 0, rawDeflate.Length);
        using var ms = new MemoryStream();
        byte[] buf = new byte[65536];
        while (!inflater.Finished())
        {
            int n = inflater.Inflate(buf, 0, buf.Length);
            if (n == 0) break;
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    private static void BuildSyntheticRvtContainer(string outputPath, IReadOnlyList<string> fixtureGzFiles)
    {
        // Unpack two checked-in >128 KiB fixtures so the container exercises multi-page and multi-window decoding.
        byte[] getessPayload = DecompressGzipFile(fixtureGzFiles.First(p => Path.GetFileName(p) == "getess.json.gz"));
        byte[] planarPayload = DecompressGzipFile(fixtureGzFiles.First(p => Path.GetFileName(p) == "planar.mj.json.gz")).AsSpan(0, 180_000).ToArray();

        byte[] formatsLatest = PagedStreamCodec.Encode([], CoverageCensusTests.TableBytes);
        byte[] globalPrefix = [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        byte[] globalLatest = PagedStreamCodec.Encode(globalPrefix, getessPayload);
        byte[] globalHistory = PagedStreamCodec.Encode(globalPrefix, planarPayload);

        // Framed partition with a >130 KiB record body so its segment crosses 64,896-byte page boundaries.
        byte[] largeBody = [.. BitConverter.GetBytes(CoverageCensusTests.Id("Plain")), .. getessPayload];
        var rec1 = CoverageCensusTests.R(1, largeBody);
        byte[] rawFramed = CoverageCensusTests.Partition(CoverageCensusTests.Block(false, rec1));
        byte[] framedPartition = PageCodec.EncodePadded(rawFramed);

        // Unframed partition: member 0 is >140 KiB crossing page boundaries with 220 trailing container bytes (#92 layout),
        // followed by member 1 which uses a back-reference into member 0's 32 KiB LZ77 window dictionary.
        byte[] unframedPartition = BuildUnframedPartitionWithPagedAndDictionaryChainedMembers(planarPayload);

        CoverageCensusTests.WriteFile(
            outputPath,
            ("BasicFileInfo", System.Text.Encoding.Unicode.GetBytes("RevitVersion: Autodesk Revit 2022\r\n")),
            ("Formats/Latest", formatsLatest),
            ("Global/Latest", globalLatest),
            ("Global/History", globalHistory),
            ("Partitions/1", framedPartition),
            ("Partitions/2", unframedPartition));
    }

    private static byte[] BuildUnframedPartitionWithPagedAndDictionaryChainedMembers(byte[] firstPayload)
    {
        byte[] firstMember = GzipMember.Write(firstPayload);
        byte[] secondMember = BuildDictionaryChainedGzipMember(firstPayload);

        // Place 220 zero metadata bytes behind member 0 (matching Revit 2025 partition metadata) and 32 zero bytes behind member 1,
        // then insert 353-byte page segments at every 64,896-byte page boundary.
        byte[] logical = [.. firstMember, .. new byte[220], .. secondMember, .. new byte[32]];
        using var paged = new MemoryStream();
        long nextBoundary = PagedGzipStream.PageDataLength;
        for (int i = 0; i < logical.Length; i++)
        {
            if (paged.Length == nextBoundary)
            {
                paged.Write(new byte[PagedGzipStream.SegmentLength]);
                nextBoundary += PagedGzipStream.PageStride;
            }
            paged.WriteByte(logical[i]);
        }
        return paged.ToArray();
    }

    /// <summary>Builds a valid RVT gzip member whose fixed-Huffman deflate block emits 16 literal bytes and then copies
    /// 258 bytes at distance 1,024 from the preceding member's 32 KiB tail, followed by 32 literal bytes.</summary>
    private static byte[] BuildDictionaryChainedGzipMember(byte[] precedingOutput)
    {
        const int distance = 1024;
        const int matchLength = 258;
        ReadOnlySpan<byte> head = "chained-member-0"u8; // 16 bytes (< distance 1024, so match reaches into dictionary!)
        ReadOnlySpan<byte> tail = "chained-member-tail-verification"u8; // 32 bytes

        using var expected = new MemoryStream();
        expected.Write(head);
        // At the moment the match starts, 16 bytes of head have been written after precedingOutput,
        // so distance 1024 reaches precedingOutput[precedingOutput.Length - (1024 - 16)]!
        int dictSrc = precedingOutput.Length - (distance - head.Length);
        expected.Write(precedingOutput.AsSpan(dictSrc, matchLength));
        expected.Write(tail);
        byte[] expectedBytes = expected.ToArray();

        var deflateBytes = new List<byte>();
        int bitPos = 0, acc = 0;
        void WriteBits(int value, int count)
        {
            for (int i = 0; i < count; i++)
            {
                acc |= ((value >> i) & 1) << bitPos;
                if (++bitPos == 8) { deflateBytes.Add((byte)acc); acc = 0; bitPos = 0; }
            }
        }
        void WriteHuffmanCode(int code, int length)
        {
            for (int i = length - 1; i >= 0; i--) WriteBits((code >> i) & 1, 1);
        }
        void WriteLiteral(byte b)
        {
            if (b < 144) WriteHuffmanCode(0x30 + b, 8);
            else WriteHuffmanCode(0x190 + (b - 144), 9);
        }

        WriteBits(1, 1); // BFINAL = 1
        WriteBits(1, 2); // BTYPE = 01 (fixed Huffman)
        foreach (byte b in head) WriteLiteral(b);
        WriteHuffmanCode(0xC5, 8); // code 285 -> length 258 (0 extra bits)
        // Distance 1024: distance code 19 has base 769 and 8 extra bits (769 + 255 = 1024).
        WriteHuffmanCode(19, 5);
        WriteBits(1024 - 769, 8);
        foreach (byte b in tail) WriteLiteral(b);
        WriteHuffmanCode(0, 7); // code 256 -> end of block
        if (bitPos > 0) deflateBytes.Add((byte)acc);

        using var member = new MemoryStream();
        member.Write(RvtGzipHeader);
        member.Write(deflateBytes.ToArray());
        Span<byte> trailer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[..4], Crc32Value(expectedBytes));
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[4..], (uint)expectedBytes.Length);
        member.Write(trailer);
        return member.ToArray();
    }

    private static byte[] DecompressGzipFile(string path)
    {
        using var file = File.OpenRead(path);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }

    private static void CollectStreams(
        Storage storage,
        string prefix,
        List<(string StreamPath, Storage Parent, string Name, long Length)> entries)
    {
        foreach (var entry in storage.EnumerateEntries().OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (entry.Type == EntryType.Storage)
                CollectStreams(storage.OpenStorage(entry.Name), prefix + entry.Name + "/", entries);
            else if (entry.Type == EntryType.Stream)
                entries.Add((prefix + entry.Name, storage, entry.Name, entry.Length));
        }
    }

    private static bool IsGzipMagic(byte[] data, int offset) =>
        data.Length >= offset + 18 && data[offset] == 0x1F && data[offset + 1] == 0x8B && data[offset + 2] == 0x08;

    private static int ParseGzipHeader(byte[] data, int offset, int length)
    {
        if (length < 18 || offset < 0 || offset > data.Length - length
            || data[offset] != 0x1F || data[offset + 1] != 0x8B || data[offset + 2] != 0x08
            || (data[offset + 3] & 0xE0) != 0)
            return -1;

        int pos = offset + 10;
        byte flags = data[offset + 3];
        if ((flags & 0x04) != 0)
        {
            if (pos + 2 > offset + length) return -1;
            pos += 2 + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
        }
        if ((flags & 0x08) != 0)
            while (pos < offset + length && data[pos++] != 0) { }
        if ((flags & 0x10) != 0)
            while (pos < offset + length && data[pos++] != 0) { }
        if ((flags & 0x02) != 0)
            pos += 2;

        return pos <= offset + length ? pos : -1;
    }

    private static uint Crc32Value(byte[] data)
    {
        var crc = new Crc32();
        crc.Update(data);
        return (uint)crc.Value;
    }

    private static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
