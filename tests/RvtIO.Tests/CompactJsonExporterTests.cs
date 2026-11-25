using System.Text;
using System.Text.Json;
using RvtIO.Compact;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

public class CompactJsonExporterTests
{
    // Class ids of r22_schemas.json: 1772 Family, 1829 FamilySymbol, 1794 FamilyInstance, 3524 SWall, 512 BasicWallType.
    private static Element Row(int id, ushort? classId, int? categoryId, int typeId = 0, string? name = null, int? symbolFamily = null,
        bool discovered = false, bool blockOnly = false) => new()
    {
        Id = id,
        UniqueId = $"e3e052f9-0156-11d5-9301-0000863f27ad-{id:x8}",
        CategoryId = categoryId, NativeHeaderCategoryId = categoryId,
        TypeId = typeId,
        Name = name,
        SymbolFamilyId = symbolFamily,
        SerializedOwnElementClassId = classId,
        IsDiscovered = discovered,
        IsBlockOnly = blockOnly,
    };

    private static RevitFileInfo Info() => new()
    {
        ElemTableIsExact = true,
        Elements =
        {
            Row(30, 1794, -2000023, typeId: 6),
            Row(20, 3524, -2000011, typeId: 10),
            Row(25, 3524, -2000011, typeId: 10, discovered: true),
            Row(10, 512, -2000011),
            Row(40, null, null),
            Row(50, 3524, -2000011, typeId: 10, blockOnly: true),
            Row(5, 1772, null, name: "Single-Flush"),
            Row(6, 1829, null, symbolFamily: 5),
        },
    };

    private sealed class RecordingSource : ICompactPropertySource
    {
        public List<int> Asked { get; } = [];

        public IReadOnlyList<CompactProperty> GetProperties(Element element)
        {
            Asked.Add(element.Id);
            return
            [
                new CompactProperty("Identity Data", "Type Name", $"type {element.Id}"),
                new CompactProperty("Invalid group", "Category", "OST_Walls"),
                new CompactProperty("Identity Data", "Design Option", "None"),
            ];
        }
    }

    private static byte[] ExportBytes(RevitFileInfo info, ICompactPropertySource? source = null)
    {
        using var stream = new MemoryStream();
        CompactJsonExporter.Export(info, stream, source);
        return stream.ToArray();
    }

    [Fact]
    public void Nodes_CarryIdentityFields()
    {
        using var doc = JsonDocument.Parse(ExportBytes(Info()));
        var elements = doc.RootElement.GetProperty("data").GetProperty("collection").EnumerateArray()
            .Where(item => item.TryGetProperty("object", out _)).ToArray();
        Assert.Equal(new[] { "5", "6", "10", "20", "30", "40" }, elements.Select(item => item.GetProperty("object").GetString()).ToArray());
        Assert.Equal(new[] { "Family", "FamilySymbol", "BasicWallType", "SWall", "FamilyInstance", "" },
            elements.Select(item => item.GetProperty("name").GetString()).ToArray());
        Assert.Equal("e3e052f9-0156-11d5-9301-0000863f27ad-0000001e", elements[4].GetProperty("externalId").GetString());
        Assert.All(elements, item => Assert.Equal(0, item.GetProperty("properties").GetArrayLength()));
    }

    [Fact]
    public void LargeExport_PreservesSerialBytesAndReport()
    {
        var info = Info();
        for (int id = 2200; id >= 100; id--)
            info.Elements.Add(id % 2 == 0 ? Row(id, 3524, -2000011, typeId: 10) : Row(id, null, null));

        var identity = new CompactIdentity(CompactUniverse.Select(info));
        var serial = CompactPropertySources.Create(info, null, true, CompactHeaderCategory.Of, identity: identity);
        var parallel = CompactPropertySources.Create(info, null, true, identity: identity);
        Assert.False(serial.CanFork);
        Assert.True(parallel.CanFork);

        static (byte[] Bytes, CompactExportStats Stats, string Report) Export(
            RevitFileInfo info, CompactIdentity identity, CompactPropertySources source)
        {
            using var output = new MemoryStream();
            using var report = new StringWriter();
            var stats = CompactJsonExporter.Export(info, output, source, report, identity);
            source.Builtin.WriteReport(report);
            return (output.ToArray(), stats, report.ToString());
        }

        var expected = Export(info, identity, serial);
        var actual = Export(info, identity, parallel);
        Assert.Equal(expected.Bytes, actual.Bytes);
        Assert.Equal(expected.Stats, actual.Stats);
        Assert.Equal(expected.Report, actual.Report);
        Assert.Equal(serial.Builtin.LastIds, parallel.Builtin.LastIds);
    }

    private static string[] ElementNames(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("data").GetProperty("collection").EnumerateArray()
            .Where(item => item.TryGetProperty("properties", out _)).Select(item => item.GetProperty("name").GetString()!).ToArray();
    }

    private static byte[] ExportBytes(CompactJsonExportOptions options)
    {
        using var stream = new MemoryStream();
        CompactJsonExporter.Export(Info(), stream, options: options);
        return stream.ToArray();
    }

    [Fact]
    public void JsonClassNamePrefix_IsEmptyByDefault_AndTheNamesAreTheClassNamesAlone()
    {
        Assert.Equal("", new CompactJsonExportOptions().JsonClassNamePrefix);
        Assert.Equal(ElementNames(ExportBytes(Info())), ElementNames(ExportBytes(new CompactJsonExportOptions())));
        Assert.Equal(new[] { "Family", "FamilySymbol", "BasicWallType", "SWall", "FamilyInstance", "" }, ElementNames(ExportBytes(Info())));
    }

    [Fact]
    public void JsonClassNamePrefix_IsPutInFrontOfEveryNonEmptyClassName_AndAnEmptyNameStaysEmpty()
        => Assert.Equal(new[] { "ClsFamily", "ClsFamilySymbol", "ClsBasicWallType", "ClsSWall", "ClsFamilyInstance", "" },
            ElementNames(ExportBytes(new CompactJsonExportOptions { JsonClassNamePrefix = "Cls" })));

    [Fact]
    public void JsonClassNamePrefix_ChangesNothingButTheNameFieldsOfTheElements()
    {
        string plain = Encoding.UTF8.GetString(ExportBytes(Info()));
        string prefixed = Encoding.UTF8.GetString(ExportBytes(new CompactJsonExportOptions { JsonClassNamePrefix = "Cls" }));
        Assert.NotEqual(plain, prefixed);
        Assert.Equal(plain, prefixed.Replace("\"name\":\"Cls", "\"name\":\""));
    }

    [Fact]
    public void JsonClassNamePrefix_RefusesNull()
        => Assert.Throws<ArgumentNullException>(() => new CompactJsonExportOptions { JsonClassNamePrefix = null! });

    [Fact]
    public void PseudoObjects_PrecedeOnlyTheElementsWithBothCategoryLabelAndFamilyName()
    {
        using var doc = JsonDocument.Parse(ExportBytes(Info()));
        var collection = doc.RootElement.GetProperty("data").GetProperty("collection").EnumerateArray().ToArray();
        string Kind(JsonElement item) => item.TryGetProperty("object", out var id) ? "e" + id.GetString() : "p:" + item.GetProperty("name").GetString();
        // the symbol has no header category (undecided) and the family object has no family name; the wall type, the wall and the
        // door get both objects, the element of an unknown class none
        Assert.Equal(new[] { "e5", "e6", "p:Walls", "p:Basic Wall", "e10", "p:Walls", "p:Basic Wall", "e20", "p:Doors", "p:Single-Flush", "e30", "e40" },
            collection.Select(Kind).ToArray());
        Assert.All(collection.Where(item => item.TryGetProperty("objectId", out _)), item =>
        {
            Assert.Equal("", item.GetProperty("objectId").GetString());
            Assert.Equal("", item.GetProperty("externalId").GetString());
        });
    }

    [Fact]
    public void Source_IsAskedForTheRowsOnly_AndItsPropertiesAreSorted()
    {
        var source = new RecordingSource();
        string json = Encoding.UTF8.GetString(ExportBytes(Info(), source));
        Assert.Equal(new[] { 5, 6, 10, 20, 30, 40 }, source.Asked);
        Assert.Contains("\"properties\":[\"Identity Data\",{\"Design Option\":\"None\",\"Type Name\":\"type 10\"},\"Invalid group\",{\"Category\":\"OST_Walls\"}]", json);
    }

    private sealed class NullListSource : ICompactPropertySource
    {
        public IReadOnlyList<CompactProperty> GetProperties(Element element) => null!;
    }

    [Fact]
    public void SourceReturningNoList_IsRefusedNotTreatedAsEmpty()
    {
        var error = Assert.Throws<InvalidDataException>(() => ExportBytes(Info(), new NullListSource()));
        Assert.Contains("element 5", error.Message);
    }

    [Fact]
    public void TwoExports_AreByteIdentical()
        => Assert.Equal(ExportBytes(Info(), new RecordingSource()), ExportBytes(Info(), new RecordingSource()));

    [Fact]
    public void PathAndStreamExports_WriteTheSameBytes_AndTheStreamStaysOpen()
    {
        string path = Path.Combine(Path.GetTempPath(), $"rvtio-compact-{Guid.NewGuid():N}.json");
        try
        {
            var stats = CompactJsonExporter.Export(Info(), path, new RecordingSource());
            byte[] fromPath = File.ReadAllBytes(path);
            Assert.Equal(ExportBytes(Info(), new RecordingSource()), fromPath);
            Assert.Equal(new CompactExportStats(6, fromPath.Length, 1), stats);

            using var stream = new MemoryStream();
            CompactJsonExporter.Export(Info(), stream);
            Assert.True(stream.CanWrite);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Diagnostics_NameTheCountsAndTheUnresolvedClassNames()
    {
        var diagnostics = new StringWriter();
        using var stream = new MemoryStream();
        CompactJsonExporter.Export(Info(), stream, diagnostics: diagnostics);
        string text = diagnostics.ToString();
        Assert.Contains($"Compact JSON: 6 nodes, {stream.Length} bytes", text);
        Assert.Contains("Compact JSON: 1 nodes without a class name (written empty): no root class id x1", text);
    }

    [Fact]
    public void NoElements_GiveTheEmptyCollection()
    {
        using var stream = new MemoryStream();
        var stats = CompactJsonExporter.Export(new RevitFileInfo(), stream);
        Assert.Equal("{\"data\":{\"type\":\"properties\",\"collection\":[]}}", Encoding.UTF8.GetString(stream.ToArray()));
        Assert.Equal(new CompactExportStats(0, stream.Length, 0), stats);
    }

    [Fact]
    public void ThousandsOfElements_AreStreamedInChunksNotBufferedWhole()
    {
        var info = new RevitFileInfo { ElemTableIsExact = true };
        for (int id = 20_000; id >= 2; id--) info.Elements.Add(Row(id, 3524, -2000011, typeId: 1));
        info.Elements.Add(Row(1, 512, -2000011));
        var stream = new ChunkRecordingStream();
        CompactJsonExporter.Export(info, stream, new RecordingSource());
        Assert.True(stream.Total > 5_000_000, $"{stream.Total} bytes");
        Assert.True(stream.Largest <= 4 * CompactJsonWriter.FlushThreshold, $"largest write {stream.Largest}");
    }

    private sealed class ChunkRecordingStream : Stream
    {
        public long Total;
        public int Largest;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Total;
        public override long Position { get => Total; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Count(count);
        public override void Write(ReadOnlySpan<byte> buffer) => Count(buffer.Length);

        private void Count(int count)
        {
            Total += count;
            Largest = Math.Max(Largest, count);
        }
    }
}
