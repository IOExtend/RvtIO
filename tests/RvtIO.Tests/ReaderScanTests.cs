using System.Buffers.Binary;
using System.Runtime.InteropServices;
using RvtIO.Internal;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class ReaderScanTests
{
    [Fact]
    public void NameTableKeepsBothMarkersAndTheFirstNameForEachElement()
    {
        byte[] chunk = new byte[388];
        WriteEntry(12, 301, 201, "Longer name", masked: false);
        WriteEntry(123, 302, 202, "Short", masked: true);
        WriteEntry(239, 303, 201, "Later name", masked: true);
        var state = new ParsingState();

        NameTableScanner.Scan(chunk, state);

        Assert.Equal(2, state.ElementNameTable.Count);
        Assert.Equal("Longer name", state.ElementNameTable[201]);
        Assert.Equal("Short", state.ElementNameTable[202]);
        Assert.Equal(201u, state.NameHolderTarget[303]);

        void WriteEntry(int start, uint holder, uint element, string name, bool masked)
        {
            chunk[start] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(start + 4), holder);
            chunk.AsSpan(start + 8, 28).Fill(0xFF);
            int marker = start + 38;
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(marker), masked ? 0xFFFFFF04u : 1u);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(marker + 4), element);
            uint length = (uint)name.Length;
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(marker + 8), masked ? 0xFFFFFF00u | length : length);
            for (int index = 0; index < name.Length; index++)
            {
                chunk[marker + 12 + 2 * index] = (byte)name[index];
                chunk[marker + 13 + 2 * index] = masked ? (byte)0xFF : (byte)0;
            }
            BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(marker + 12 + 2 * name.Length), -2000011);
        }
    }

    [Fact]
    public void KeyedStringsKeepUnalignedBoundaryIdsAndTheLastPossibleEntry()
    {
        byte[] chunk = new byte[16 + 31];
        BinaryPrimitives.WriteUInt32LittleEndian(chunk, 42);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(8), 31);
        WriteString(17, -1_200_000, 'A');
        WriteString(27, -1_200_001, 'B');
        WriteString(37, -1_000_000, 'C');
        var state = new ParsingState();

        BlockParser.CollectBipStrings(chunk, state);

        Assert.Equal(new[] { (-1_200_000, "A"), (-1_000_000, "C") },
            state.BipKeyedStrings[42].Select(pair => (pair.Key, pair.Value)));

        void WriteString(int offset, int id, char value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(offset), id);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(offset + 4), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(offset + 8), value);
        }
    }

    [Fact]
    public void BlockStringsKeepTheFirstTwentyDistinctTrimmedValues()
    {
        byte[] chunk = new byte[28 * 12];
        for (int index = 0; index < 28; index++)
        {
            int offset = index * 12;
            string value = $" {Math.Max(0, index - 1):D3}";
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(offset), (uint)value.Length);
            System.Text.Encoding.Unicode.GetBytes(value, chunk.AsSpan(offset + 4, 8));
        }
        var strings = new Dictionary<uint, List<string>>();
        BlockParser.ExtractBlockStrings(chunk, 0, chunk.Length, 42, strings);
        BlockParser.ExtractBlockStrings(chunk, 26 * 12, chunk.Length, 42, strings);

        Assert.Equal(Enumerable.Range(0, 20).Select(value => value.ToString("D3")), strings[42]);
    }

    [Theory]
    [InlineData(31, 2, 0)]
    [InlineData(7, 500, 40)]
    public void BlockStringsReadUnalignedLengthsAtBothEndsOfTheScan(int offset, int length, int trailingBytes)
    {
        byte[] data = new byte[offset + 4 + 2 * length + trailingBytes];
        Array.Fill(data, byte.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), (uint)length);
        string value = new('Д', length);
        System.Text.Encoding.Unicode.GetBytes(value, data.AsSpan(offset + 4));
        var strings = new Dictionary<uint, List<string>>();

        BlockParser.ExtractBlockStrings(data, 0, data.Length, 42, strings);

        Assert.Equal(value, Assert.Single(strings[42]));
    }

    [Fact]
    public void NearbyNameWindowsKeepExactBoundariesAndStringOrder()
    {
        byte[] chunk = new byte[2_400];
        WriteName(650, "First name");
        WriteName(690, "Second name");
        WriteName(2_000, "Distant name");
        WriteId(50, 70_000); // outside the first window
        WriteId(55, 70_001);
        WriteId(745, 70_002);
        WriteId(751, 70_003);
        WriteId(1_403, 70_002); // the same owner is encountered again in a disjoint window
        var strings = new Dictionary<uint, List<string>>();

        StringScanner.ScanChunk(chunk, [70_000, 70_001, 70_002, 70_003], strings);

        Assert.False(strings.ContainsKey(70_000));
        Assert.Equal(["First name"], strings[70_001]);
        Assert.Equal(["First name", "Second name", "Distant name"], strings[70_002]);
        Assert.Equal(["Second name"], strings[70_003]);

        void WriteId(int offset, uint id) => BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(offset), id);

        void WriteName(int offset, string value)
        {
            WriteId(offset, (uint)value.Length);
            System.Text.Encoding.Unicode.GetBytes(value, chunk.AsSpan(offset + 4));
        }
    }

    [Theory]
    [InlineData("Ширина", true)]
    [InlineData("abc123def", true)]
    [InlineData("abcd123def", false)]
    [InlineData("12. Дверь 1", false)]
    [InlineData("folder\\name", false)]
    [InlineData("(X,Y)", false)]
    public void ParameterNamesKeepTheOriginalFilters(string name, bool expected)
        => Assert.Equal(expected, StringScanner.IsValidParamName(name));

    [Fact]
    public void ParameterNamesUseTheCurrentCultureForTheMetadataPrefix()
    {
        const string name = "\u00adautodesk.spec";
        Assert.Equal(!name.StartsWith("autodesk.", StringComparison.CurrentCulture), StringScanner.IsValidParamName(name));
    }

    [Theory]
    [InlineData(0x053C)]
    [InlineData(0x04F8)]
    public void R22ScanFindsTheLastPossibleUnalignedRecord(ushort marker)
    {
        byte[] chunk = new byte[19];
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(6), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(14), marker);

        Assert.True(RecordParser.ContainsAnchoredR22Record(chunk, new HashSet<uint> { 42 }));
        Assert.False(RecordParser.ContainsAnchoredR22Record(chunk, new HashSet<uint>()));
        Assert.False(RecordParser.ContainsAnchoredR22Record(chunk[..^1], new HashSet<uint> { 42 }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordBodiesShareTheChunkAndExcludeHeadersAndFooters(bool r25)
    {
        const int start = 1;
        int headerSize = r25 ? 16 : 12;
        int footerSize = r25 ? 4 : 0;
        // "AB" fits inside the first body. "CD" would require bytes past its end.
        byte[] first = new byte['D'];
        byte[] prefix = [0xEF, 0x01, 2, 0, 0, 0, (byte)'A', 0, (byte)'B', 0];
        prefix.CopyTo(first, 0);
        first[^6] = 2;
        first[^2] = (byte)'C';
        byte[] second = [0xEF, 0x01, 0, 0];
        int secondStart = start + headerSize + first.Length + footerSize;
        var chunk = new byte[secondStart + headerSize + second.Length + footerSize];
        WriteRecord(start, 42, first);
        WriteRecord(secondStart, 'D', second);
        var state = new ParsingState();
        state.AllKnownIds.Add(42);

        if (r25)
            RecordParser.ParseChunkR25(chunk, state);
        else
            RecordParser.ParseChunk(chunk, state);

        AssertBody(42, start + headerSize, first);
        AssertBody('D', secondStart + headerSize, second);
        // "D" starts the next header in R22 and the footer's size field in R25.
        Assert.Equal(["AB"], state.ElemStrings[42]);

        void WriteRecord(int offset, uint id, byte[] body)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(offset), id);
            BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(offset + (r25 ? 12 : 4)), body.Length);
            if (r25)
                BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(offset + headerSize + body.Length), body.Length);
            else
                BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(offset + 8), 0x053C);
            body.CopyTo(chunk, offset + headerSize);
        }

        void AssertBody(uint id, int offset, byte[] expected)
        {
            var body = state.Records[id].Body;
            Assert.True(MemoryMarshal.TryGetArray(body, out var slice));
            Assert.Same(chunk, slice.Array);
            Assert.Equal(offset, slice.Offset);
            Assert.Equal(expected, body.ToArray());
        }
    }

    [Fact]
    public void HvacScanFindsAnUnalignedTableAtTheEndOfTheChunk()
    {
        const int start = 5;
        var state = new ParsingState();
        var chunk = new byte[start + 0xCF];
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(start + 0x24), 42);
        BinaryPrimitives.WriteDoubleLittleEndian(chunk.AsSpan(start + 0x57), 251);
        BinaryPrimitives.WriteDoubleLittleEndian(chunk.AsSpan(start + 0xAF), 329);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(start + 0xC3), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(start + 0xC7), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(start + 0xCB), 3);
        var ids = new HashSet<uint> { 42 };

        BlockParser.CollectHvacTables(chunk[..^1], ids, state.HvacSpaceTypeParams, state.HvacScheduleRefs);
        Assert.Empty(state.HvacSpaceTypeParams);
        BlockParser.CollectHvacTables(chunk, ids, state.HvacSpaceTypeParams, state.HvacScheduleRefs);

        var parameters = Assert.Contains(42u, state.HvacSpaceTypeParams);
        Assert.Equal(251, parameters[-1114709]);
        Assert.Equal(329, parameters[-1114708]);
        Assert.Equal((1, 2, 3), state.HvacScheduleRefs[42]);
    }

    [Fact]
    public void SharedTransformKeepsTheWinningAnglesFirstMatrixByAlignment()
    {
        const double angle = -0.5, laterAlignmentAngle = -0.5000001;
        var chunk = new byte[9 * 80];
        for (int index = 0; index < 8; index++)
        {
            int start = index * 80 + (index == 0 ? 1 : 0);
            var (s, c) = Math.SinCos(index == 0 ? laterAlignmentAngle : angle);
            double[] matrix = [c, s, 0, -s, c, 0, 0, 0, 1];
            for (int entry = 0; entry < matrix.Length; entry++)
                BinaryPrimitives.WriteDoubleLittleEndian(chunk.AsSpan(start + entry * sizeof(double)), matrix[entry]);
        }

        var transform = RvtFileReader.FindSharedTransform([chunk],
            new Dictionary<uint, (double[], double[])>(), new Dictionary<uint, ElementRecord>(), null);

        Assert.NotNull(transform);
        var (sin, cos) = Math.SinCos(angle);
        Assert.Equal(cos, transform[0]);
        Assert.Equal(sin, transform[1]);
    }
}
