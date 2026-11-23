using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using OpenMcdf;
using RvtIO.BuiltIns;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;
using Back = RvtIO.Tests.SchemaStreamEditorTests.Back;
using Fld = RvtIO.Tests.SchemaStreamEditorTests.Fld;
using NoRef = RvtIO.Tests.SchemaStreamEditorTests.NoRef;

namespace RvtIO.Tests;

/// <summary>
/// Project parameter writes on the 2023 layout (4-byte ids, newer tags) and from 2024 on (8-byte ids with a nonzero high word),
/// which <see cref="SetParameterTests"/> does not reach: it builds 2020 to 2022 files only.
/// </summary>
public sealed class WideProjectParameterTests : IDisposable
{
    private const int ProjectId = 70, WallId = 71, DefinitionId = 72, BindingId = 73;
    private const ushort ProjectInfoClass = 21, WallClass = 22, DefinitionClass = 28, BindingClass = 29, HeaderClass = 30;
    private const int StringSet = 19;
    private readonly string _folder = Directory.CreateTempSubdirectory("rvtio-wide-").FullName;
    private string Output => Path.Combine(_folder, "output.rvt");
    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static PartitionFraming.Tags Tags(int release) => release switch
    {
        2023 => new(869, 868, 870, 871, 872),
        2024 => new(891, 890, 892, 893, 894),
        _ => new(951, 950, 952, 953, 954),
    };

    private static bool Wide(int release) => release >= 2024;
    private static long Id(long id, int release) => Wide(release) ? (1L << 32) + id : id;

    [Theory]
    [InlineData(2023)]
    [InlineData(2024)]
    [InlineData(2027)]
    public void WritesTheBuiltInProjectInformationAndItsRecord(int release)
    {
        string source = CreateSource(release);
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var result = RvtWriter.SetParameter(source, Output, Id(ProjectId, release), BuiltInParameter.CLIENT_NAME, "New client");
        Assert.Equal(2, result.ReplacedStreamCount);

        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(Tagged(ProjectInfoClass, ProjectBody(release, "New client")), ReadBody(root, release, ProjectId));
        using var info = root.OpenStream("ProjectInformation");
        using var archive = new ZipArchive(info);
        using var xml = Assert.Single(archive.Entries).Open();
        Assert.Equal("New client", Assert.Single(XDocument.Load(xml).Descendants(), e => e.Name.LocalName == "Client_Name").Value);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Theory]
    [InlineData(2023)]
    [InlineData(2024)]
    [InlineData(2027)]
    public void WritesTheInstanceCommentOfAWallAndKeepsItsOtherValues(int release)
    {
        string source = CreateSource(release);
        RvtWriter.SetParameter(source, Output, Id(WallId, release), BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, "Changed");
        using var root = RootStorage.OpenRead(Output);
        Assert.Equal(Tagged(WallClass, WallBody(release, "Changed", "Old")), ReadBody(root, release, WallId));
    }

    [Theory]
    [InlineData(2023)]
    [InlineData(2024)]
    [InlineData(2027)]
    public void WritesAndClearsASharedParameterOnAWall(int release)
    {
        string source = CreateSource(release);
        long wall = Id(WallId, release), definition = Id(DefinitionId, release);
        var result = RvtWriter.SetParameters(source, Output,
            [new(wall, definition, "First"), new(wall, definition, null), new(wall, definition, "Last")]);
        Assert.Equal(1, result.RewrittenRecordCount);
        using (var root = RootStorage.OpenRead(Output))
            Assert.Equal(Tagged(WallClass, WallBody(release, "Old", "Last")), ReadBody(root, release, WallId));

        string cleared = Path.Combine(_folder, "cleared.rvt");
        RvtWriter.SetParameters(source, cleared, [new(wall, definition, null)]);
        using var clearedRoot = RootStorage.OpenRead(cleared);
        Assert.Equal(Tagged(WallClass, WallBody(release, "Old", null)), ReadBody(clearedRoot, release, WallId));
    }

    [Theory]
    [InlineData(2024)]
    [InlineData(2027)]
    public void DoesNotMatchAnElementByTheLowHalfOfItsId(int release)
    {
        string source = CreateSource(release);
        var refused = Assert.Throws<RvtWriteRefusedException>(() =>
            RvtWriter.SetParameter(source, Output, WallId, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, "Changed"));
        Assert.Equal("missing-element", refused.Kind);
        Assert.False(File.Exists(Output));
        refused = Assert.Throws<RvtWriteRefusedException>(() =>
            RvtWriter.SetParameter(source, Output, Id(WallId, release), DefinitionId, "Changed"));
        Assert.Equal("missing-parameter-definition", refused.Kind);
        Assert.False(File.Exists(Output));
    }

    private static byte[] Tagged(ushort tag, byte[] payload) => PartitionStreamEditorTests.TaggedBody(tag, payload);

    private static byte[] ReadBody(RootStorage root, int release, int element)
    {
        using var stream = root.OpenStorage("Partitions").OpenStream("0");
        byte[] raw = new byte[stream.Length];
        stream.ReadExactly(raw);
        var matches = new List<byte[]>();
        Assert.True(PartitionFraming.Walk(raw, Tags(release), Wide(release) ? 8 : 4, record =>
        {
            if (record.Block == 0 && record.Channel == 102 && record.Owner == (ulong)Id(element, release))
                matches.Add(record.Body.ToArray());
        }, out var failure, out _), failure);
        return Assert.Single(matches);
    }

    private string CreateSource(int release)
    {
        bool wide = Wide(release);
        var tags = Tags(release);
        var schema = new SchemaStreamEditorTests.Writer();
        void Add(string name, params Fld[] fields) => schema.Top(new(name, new NoRef(), 1, fields, []));
        Fld Reference(string name, int id, byte mods = 0) => new(name, 14, mods, Target: new Back(id));
        Add("Identifier", new Fld(wide ? "m_id64" : "m_id", wide ? (byte)11 : (byte)4)); // 12
        Add("OriginalElementId", Reference("m_id", 12)); // 13
        Add("ElementId", Reference("m_id", 12)); // 14
        Add("ElemHistory", Reference("m_originalElementId", 13), new("m_creationDate", 4), new("m_lastModificationDate", 4),
            new("m_lastUserModificationDate", 4)); // 15
        Add("ElemRec", Reference("m_id", 14), Reference("m_history", 15), new("m_partitionId", 4), Reference("m_OwningElementId", 14)); // 16
        Add("ElemTable", Reference("m_elemArr", 16, 0x50)); // 17
        Add("ParamValueAString", Reference("m_paramId", 14), new("m_value", 8)); // 18
        Add("ParamValueSetAString", Reference("m_paramSet", 18, 0x50)); // 19
        Add("Element", Reference("m_id", 14), new("m_pParamValueSetAString", 14, 1), new("m_pParamValueSetInt", 14, 1),
            new("m_pParamValueSetDouble", 14, 1)); // 20
        schema.Top(new("ProjectInfo", new Back(20), 1, [], [])); // 21
        schema.Top(new("SWall", new Back(20), 1, [new("m_pGroup", 14, 1)], [])); // 22
        Add("GroupMembership"); // 23
        Add("ParamElemGlobal"); // 24
        Add("ParamDef", Reference("m_paramElemId", 14), new("m_readOnly", 1)); // 25
        schema.Top(new("ParamDefString", new Back(25), 1, [], [])); // 26
        schema.Top(new("ParamElem", new Back(20), 1, [new("m_pParamDef", 14, 1)], [])); // 27
        schema.Top(new("ParamElemExternal", new Back(27), 1,
            [Reference("m_bindingIds", 14, 0x50), new("m_userModifiable", 1), new("m_hideWhenNoValue", 1)], [])); // 28
        schema.Top(new("ParamBinding", new Back(20), 1,
            [Reference("m_paramId", 14), Reference("m_categoryId", 14), new("m_elemOrSymbol", 4)], [])); // 29
        Add("ElementHeader", new Fld("m_regenHistoryCount", 4), Reference("m_categroryId", 14), Reference("m_familyId", 14),
            Reference("m_ownerViewId", 14), Reference("m_designOptionId", 14)); // 30
        for (int i = 31; i < tags.ContentKey; i++)
            Add("Filler" + i);
        foreach (string marker in new[] { "ContentKey", "ContentMarker", "SegmentMarker", "SegmentCheckback", "SignatureMarker" })
            Add(marker);

        string path = Path.Combine(_folder, "source.rvt");
        using var root = RootStorage.Create(path, OpenMcdf.Version.V4);
        using (var basic = root.CreateStream("BasicFileInfo"))
            basic.Write(RvtWriterTests.BuildBasicFileInfoBytes(release));
        using (var formats = root.CreateStorage("Formats").CreateStream("Latest"))
            formats.Write(PagedStreamCodec.Encode([], schema.Build()));
        using (var table = root.CreateStorage("Global").CreateStream("ElemTable"))
        {
            int[] ids = [ProjectId, WallId, DefinitionId, BindingId];
            using var bytes = new MemoryStream();
            using var writer = new BinaryWriter(bytes);
            writer.Write((ushort)17);
            writer.Write(ids.Length);
            foreach (int id in ids)
            {
                WriteId(writer, Id(id, release), wide);
                WriteId(writer, Id(id, release), wide);
                writer.Write(new byte[16 + (wide ? 8 : 4)]);
            }
            table.Write(PagedStreamCodec.Encode(new byte[8], bytes.ToArray()));
        }
        using (var partition = root.CreateStorage("Partitions").CreateStream("0"))
        {
            var records = new (ulong, ushort, byte[])[]
            {
                ((ulong)Id(ProjectId, release), ProjectInfoClass, ProjectBody(release, "Owner")),
                ((ulong)Id(WallId, release), WallClass, WallBody(release, "Old", "Old")),
                ((ulong)Id(DefinitionId, release), DefinitionClass, Pack(w =>
                {
                    WriteElement(w, DefinitionId, release);
                    Pointer(w, 26);
                    w.Write(1);
                    WriteId(w, Id(BindingId, release), wide);
                    w.Write(false); // Read-only in the UI.
                    w.Write(true);  // Hide when there is no value.
                    WriteId(w, Id(DefinitionId, release), wide);
                    w.Write(false);
                })),
                ((ulong)Id(BindingId, release), BindingClass, Pack(w =>
                {
                    WriteElement(w, BindingId, release);
                    WriteId(w, Id(DefinitionId, release), wide);
                    WriteId(w, (int)BuiltInCategory.OST_Walls, wide);
                    w.Write(1);
                })),
            };
            var block = PartitionStreamEditorTests.MakeStandardBlock(wide ? 8 : 4, keyed: false, records);
            int wall = Array.FindIndex(block.Chains[0].Records, entry => entry.Owner == (ulong)Id(WallId, release));
            block.Chains[0].Records[wall] = new((ulong)Id(WallId, release), Pack(w =>
            {
                w.Write(HeaderClass);
                w.Write(0);
                WriteId(w, (int)BuiltInCategory.OST_Walls, wide);
                WriteId(w, -1, wide);
                WriteId(w, -1, wide);
                WriteId(w, -1, wide);
            }));
            partition.Write(PartitionStreamEditorTests.BuildPartitionStream(wide ? 8 : 4, tags, block));
        }
        using var info = root.CreateStream("ProjectInformation");
        using var zip = new ZipArchive(info, ZipArchiveMode.Create);
        using var entry = zip.CreateEntry("sample.project.xml").Open();
        using var xml = new StreamWriter(entry);
        xml.Write("""<entry xmlns="http://www.w3.org/2005/Atom" xmlns:A="urn:schemas-autodesk-com:partatom"><A:features><A:feature><A:group><Client_Name type="system">Owner</Client_Name><Project_Name type="system">Owner</Project_Name></A:group></A:feature></A:features></entry>""");
        return path;
    }

    private static byte[] ProjectBody(int release, string client) => Pack(w =>
    {
        WriteElement(w, ProjectId, release, StringSet);
        w.Write(2);
        Row(w, release, (int)BuiltInParameter.CLIENT_NAME, client);
        Row(w, release, (int)BuiltInParameter.PROJECT_NAME, "Owner");
    });

    // The built-in comment row sorts before the positive id of the user parameter.
    private static byte[] WallBody(int release, string comment, string? user) => Pack(w =>
    {
        WriteElement(w, WallId, release, StringSet);
        Pointer(w, 0);
        w.Write(user is null ? 1 : 2);
        Row(w, release, (int)BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS, comment);
        if (user is not null)
            Row(w, release, Id(DefinitionId, release), user);
    });

    private static void Row(BinaryWriter w, int release, long id, string text)
    {
        WriteId(w, id, Wide(release));
        w.Write(text.Length);
        w.Write(Encoding.Unicode.GetBytes(text));
    }

    private static void WriteElement(BinaryWriter w, int id, int release, int strings = 0)
    {
        WriteId(w, Id(id, release), Wide(release));
        Pointer(w, strings); Pointer(w, 0); Pointer(w, 0);
    }

    private static void WriteId(BinaryWriter w, long id, bool wide)
    {
        if (wide)
            w.Write(id);
        else
            w.Write((int)id);
    }

    private static void Pointer(BinaryWriter w, int tag)
    {
        w.Write(tag == 0 ? 0u : uint.MaxValue);
        if (tag != 0)
            w.Write((ushort)tag);
    }

    private static byte[] Pack(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        write(writer);
        return stream.ToArray();
    }
}
