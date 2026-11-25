using System.Reflection;
using OpenMcdf;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

[CollectionDefinition("ElemTableStaticMaps", DisableParallelization = true)]
public sealed class ElemTableStaticMapsCollection { }

// Synthetic parses replace the legacy static maps used by concurrent real-file fixtures.
[Collection("ElemTableStaticMaps")]
public class ElemTableAuthorityTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(3)]
    public void ExactCountedRowsRegisterMembershipWithoutAType(int count)
    {
        var bytes = Table(count); var result = Parse(bytes);
        Assert.Equal(count, result.Count);
        Assert.All(result.Values, value => Assert.Equal(0u, value));
        for (int i = 0; i < count; i++)
        {
            uint id = (uint)(12345 + i);
            Assert.Equal((uint)(i + 7), ElemTableReader.LastWorksets[id]);
            Assert.Equal(64 + i, ElemTableReader.LastEpisodeNumbers[id]);
        }
    }

    [Theory]
    [InlineData("truncated")] [InlineData("duplicate")] [InlineData("unordered")] [InlineData("count")]
    public void MalformedCountedRowsNeverLeakAPartialTableOrStaleMaps(string mode)
    {
        _ = Parse(Table(1)); byte[] bytes = Table(2);
        if (mode == "truncated") bytes = bytes[..^1];
        if (mode == "duplicate") BitConverter.GetBytes(12345u).CopyTo(bytes, 34);
        if (mode == "unordered") BitConverter.GetBytes(12344u).CopyTo(bytes, 34);
        if (mode == "count") BitConverter.GetBytes(uint.MaxValue).CopyTo(bytes, 2);
        Assert.Empty(Parse(bytes)); Assert.Empty(ElemTableReader.LastWorksets); Assert.Empty(ElemTableReader.LastEpisodeNumbers);
    }

    [Fact]
    public void DoesNotScanTrailingBytesForAdditionalElements()
    {
        byte[] first = Table(1), extra = Table(2);
        var result = Parse(first.Concat(extra.AsSpan(6).ToArray()).ToArray());
        Assert.Single(result); Assert.True(result.ContainsKey(12345)); Assert.False(result.ContainsKey(12346));
    }

    [Fact]
    public void NonrepresentableIdsAndNegativePartitionSentinelsDoNotBecomePublicElementsOrWorksets()
    {
        byte[] bytes = Table(4);
        BitConverter.GetBytes(0u).CopyTo(bytes, 6);
        BitConverter.GetBytes(-1).CopyTo(bytes, 6 + 20);
        BitConverter.GetBytes(12346u).CopyTo(bytes, 6 + 28);
        BitConverter.GetBytes(-1).CopyTo(bytes, 6 + 28 + 20);
        BitConverter.GetBytes((uint)int.MaxValue + 1u).CopyTo(bytes, 6 + 56);
        BitConverter.GetBytes(uint.MaxValue).CopyTo(bytes, 6 + 84);
        var result = Parse(bytes);
        Assert.Equal([0u, 12346u], result.Keys.Order().ToArray());
        Assert.Empty(ElemTableReader.LastWorksets);
    }

    [Fact]
    public void PostProcessAddsEachMissingElementOnceInTableThenWorksetOrder()
    {
        var existing = new Element
        {
            Id = 10,
            Parameters = [BipHelpers.MakeBipParam(-1012100, "PHASE_CREATED", StorageType.ElementId, 75)],
        };
        var info = new RevitFileInfo { Elements = [existing] };
        var state = new ParsingState
        {
            ElemTable = new() { [10] = 0, [30] = 0, [20] = 0, [0] = 0 },
            ElemWorksets = new() { [20] = 1, [40] = 1, [30] = 1, [0x0F000000] = 1 },
        };

        ElementBuilder.PostProcess(info, state);

        Assert.Equal([10, 30, 20, 40], info.Elements.Select(element => element.Id));
        Assert.All(info.Elements.Skip(1), element =>
            Assert.Equal(75, Assert.Single(element.Parameters, parameter => parameter.Id == -1012100).Value));
    }

    [SkippableFact]
    public void ProductionReadUsesTheExactR22CensusAndNotHistoryAsType()
    {
        using var root = RootStorage.OpenRead(TestFileFixture.FindTestFile("TESTFILE.rvt"));
        var bytes = PagedGzipStream.Read(root, "Global", "ElemTable"); Assert.NotNull(bytes);
        var exact = ElementExportIds.ParseElemTable(bytes); Assert.NotNull(exact);
        var actual = ElemTableReader.Read(root);
        Assert.Equal(exact.Keys.Where(id => ElementIdEncoding.IsSupported(id) || id == 0).Order(), actual.Keys.Order());
        Assert.All(actual.Values, value => Assert.Equal(0u, value));
    }

    [SkippableFact]
    public void ReentrantDiagnosticsCannotReplaceTheCurrentReadersWorksetMap()
    {
        string path = TestFileFixture.FindTestFile("TESTFILE.rvt");
        Dictionary<uint, uint> expectedWorksets;
        using (var root = RootStorage.OpenRead(path))
            _ = ElemTableReader.Read(root, out expectedWorksets, out _, out _);
        using var diagnostic = new ReentrantDiagnostic(() => Parse(Table(1)));
        var reader = new RvtFileReader(new RvtReaderOptions { DiagnosticOutput = diagnostic });
        _ = reader.Read(path);
        Assert.True(diagnostic.Triggered);
        Assert.Equal(expectedWorksets.Keys.Order(), reader.LastParsingState!.ElemWorksets.Keys.Order());
    }

    [Theory]
    [InlineData(528u)] [InlineData(123456u)]
    public void BuilderNeverPublishesTheLegacyHistoryValueAsAType(uint history)
    {
        var state = new ParsingState(); state.ElemTable[100] = history;
        var info = new RevitFileInfo(); ElementBuilder.BuildElemTableElement(100, history, state, info);
        Assert.Equal(-1, Assert.Single(info.Elements).TypeId);
    }

    [Fact]
    public void BuilderKeepsParameterPrecedenceWhenReusingTheIdSet()
    {
        var state = new ParsingState();
        state.Records[100] = new ElementRecord
        {
            ElemId = 100,
            References = [200],
            BipParams = [new(501, null, 4, 10), new(501, null, 4, 20)],
        };
        state.ElemParamStrings[100] = new() { [501] = "stored", [502] = "first" };
        state.ElemBipDoubles[100] = new() { [502] = 7, [-1001000] = 1 };
        state.ElemBipDoubles[200] = new() { [502] = 8, [-1001000] = 2, [-1004005] = 3 };
        state.ElemBaseFields[100] = new() { AssocLevelId = 40 };
        state.ElemBipInts[101] = new() { [502] = 9 };
        var info = new RevitFileInfo();
        var scratch = new HashSet<int>();

        ElementBuilder.BuildElemTableElement(100, 0, state, info, scratch);
        ElementBuilder.BuildElemTableElement(101, 0, state, info, scratch);

        var first = info.Elements[0].Parameters;
        Assert.Equal([501, 502, -1001000, -1004005, -1001952], first.Take(5).Select(p => p.Id));
        Assert.Equal("stored", Assert.Single(first, p => p.Id == 501).Value);
        Assert.Equal("first", Assert.Single(first, p => p.Id == 502).Value);
        Assert.Equal(1.0, Assert.Single(first, p => p.Id == -1001000).Value);
        Assert.Equal(3.0, Assert.Single(first, p => p.Id == -1004005).Value);
        Assert.Equal(9, Assert.Single(info.Elements[1].Parameters, p => p.Id == 502).Value);
    }

    [Theory]
    [InlineData((ushort)2870, 1, true)] [InlineData((ushort)2873, 1, true)]
    [InlineData((ushort)2876, 1, true)] [InlineData((ushort)512, 1, false)]
    [InlineData((ushort)2870, 2, false)] [InlineData((ushort)0, 1, false)]
    public void ParameterGuardsUseOnlyUniqueOwnNativeClasses(ushort cls, int count, bool expected)
    {
        var state = new ParsingState(); state.ElemTable[100] = 523;
        state.ElemClassIdx[100] = 2870;
        state.OwnedElementClassRoots[100] = new(count, cls == 0 ? null : cls);
        var method = typeof(ParsingState).GetMethod("IsParameterElement", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method); Assert.Equal(expected, (bool)method.Invoke(state, [100u])!);
    }

    [Fact]
    public void TableMembershipPutsTheRecordBoxOnTheGeometryAndTheElementBoxOnTheHeader()
    {
        var state = new ParsingState(); state.ElemTable[100] = 0;
        state.GrepBlocks[100] = new GRepData { BoundingBox = [1, 2, 3, 4, 5, 6] };
        state.Records[100] = new ElementRecord { ElemId = 100, Geometry = [7, 8, 9, 10, 11, 12] };
        var info = new RevitFileInfo(); ElementBuilder.BuildElemTableElement(100, 0, state, info);
        var element = Assert.Single(info.Elements);
        // The tight box of the record lives on the geometry; the element box comes from the header
        // outline alone, and this direct build holds no header verdict.
        Assert.Null(element.BoundingBox);
        Assert.NotNull(element.Geometry);
        Assert.Equal(state.GrepBlocks[100].BoundingBox, element.Geometry.BoundingBox);
        Assert.Equal(-1, element.TypeId);
        // The header verdict of the read is applied last and takes the element box.
        state.HeaderBoundingBoxes[100] = new ElementHeaderBox(true, [7, 8, 9, 10, 11, 12]);
        ElementBuilder.ApplyHeaderBoundingBoxes(info, state);
        Assert.Equal([7, 8, 9, 10, 11, 12], element.BoundingBox);
        state.HeaderBoundingBoxes[100] = new ElementHeaderBox(false, null);
        ElementBuilder.ApplyHeaderBoundingBoxes(info, state);
        Assert.Null(element.BoundingBox);
    }

    [Fact]
    public void ApplyWorksets_AssignsWorksetIdAndParameter()
    {
        var elem1 = new Element { Id = 101 };
        var elem2 = new Element { Id = 102 };
        var info = new RevitFileInfo { Elements = [elem1, elem2] };
        var state = new ParsingState();
        state.ElemWorksets[(uint)elem1.Id] = 55;

        typeof(ElementBuilder).GetMethod("ApplyWorksets", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [info, state]);

        Assert.Equal(55, elem1.WorksetId);
        Assert.Equal(55, elem1.Parameters.FirstOrDefault(p => p.Id == -1002053)?.Value);
        Assert.Null(elem2.WorksetId);
    }

    private static Dictionary<uint, uint> Parse(byte[] bytes)
    {
        var result = new Dictionary<uint, uint>();
        typeof(ElemTableReader).GetMethod("ParseDecompressedElemTable", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [bytes, result]); return result;
    }
    private static byte[] Table(int count)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write((ushort)1312); writer.Write(count);
        for (int i = 0; i < count; i++)
        {
            writer.Write(12345 + i); writer.Write(100 + i); writer.Write(64 + i);
            writer.Write(523); writer.Write(999); writer.Write(7 + i); writer.Write(42);
        }
        return stream.ToArray();
    }

    private sealed class ReentrantDiagnostic(Action action) : StringWriter
    {
        internal bool Triggered { get; private set; }
        public override void WriteLine(string? value)
        {
            if (!Triggered && value?.StartsWith("ElemTable:", StringComparison.Ordinal) == true)
            {
                Triggered = true;
                action();
            }
            base.WriteLine(value);
        }
    }
}
