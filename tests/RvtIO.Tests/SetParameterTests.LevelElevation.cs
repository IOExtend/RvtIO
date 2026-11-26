using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.BuiltIns;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;
using Back = RvtIO.Tests.SchemaStreamEditorTests.Back;
using Fld = RvtIO.Tests.SchemaStreamEditorTests.Fld;
using NoRef = RvtIO.Tests.SchemaStreamEditorTests.NoRef;

namespace RvtIO.Tests;

public sealed partial class SetParameterTests
{
    private const int ElevationLevelId = 181, ElevationTypeId = 182, ElevationLocationId = 183, ElevationProjectLocationId = 184,
        ElevationTrackingId = 185, ElevationDependentId = 186;

    [Theory]
    [InlineData(0, "3.25 ft", 3.25)]
    [InlineData(1, "3.25 ft", -120)]
    [InlineData(0, "1e-9 ft", 1e-9)]
    [InlineData(0, "1e99 ft", 1e99)]
    public void WritesBothLevelPlanesInTheSelectedElevationBase(int elevationBase, string value, double expected)
    {
        string source = CreateElevationSource(elevationBase);

        RvtWriter.SetParameter(source, Output, ElevationLevelId, (int)BuiltInParameter.LEVEL_ELEV, value);

        using var output = RootStorage.OpenRead(Output);
        Assert.Equal(ElevationLevelBody(expected, expected), ReadBody(output, ElevationLevelId));
    }

    [Theory]
    [InlineData(0, "5e-10 ft")]
    [InlineData(1, "123.250000245 ft")]
    public void RetainsChangesInsideTheDisplayedElevationTolerance(int elevationBase, string value)
    {
        string source = CreateElevationSource(elevationBase);

        var result = RvtWriter.SetParameter(source, Output, ElevationLevelId, (int)BuiltInParameter.LEVEL_ELEV, value);

        Assert.Equal(0, result.RewrittenRecordCount);
        using var output = RootStorage.OpenRead(Output);
        Assert.Equal(ElevationLevelBody(0, 0), ReadBody(output, ElevationLevelId));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RefusesALevelWithUnverifiedDependentsOrConflictingPlanes(bool dependent, bool differentFace)
    {
        string source = CreateElevationSource(0, dependent, differentFace);
        byte[] original = File.ReadAllBytes(source);

        var error = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SetParameters(source, Output,
        [
            new(ElevationLevelId, (int)BuiltInParameter.LEVEL_ELEV, "0 ft"),
            new(ElevationLevelId, (int)BuiltInParameter.LEVEL_ELEV, "3.25 ft"),
        ]));

        Assert.Equal("unverified-parameter-storage", error.Kind);
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.False(File.Exists(Output));
    }

    private string CreateElevationSource(int elevationBase, bool dependent = false, bool differentFace = false)
    {
        var tags = Tags(2022);
        var schema = new SchemaStreamEditorTests.Writer();
        void Add(string name, params Fld[] fields) => schema.Top(new(name, new NoRef(), 1, fields, []));
        static Fld R(string name, int tag, byte mods = 0) => new(name, 14, mods, Target: new Back(tag));
        static Fld Vector(string name) => new(name, 7, 0x10, Fixed: 3);
        static void Element(BinaryWriter writer, int id) { writer.Write(id); writer.Write(-1); }
        Add("Identifier", new Fld("m_id", 4)); // 12
        Add("OriginalElementId", R("m_id", 12)); // 13
        Add("ElementId", R("m_id", 12)); // 14
        Add("ElemHistory", R("m_originalElementId", 13), new("m_creationDate", 4), new("m_lastModificationDate", 4), new("m_lastUserModificationDate", 4)); // 15
        Add("ElemRec", R("m_id", 14), R("m_history", 15), new("m_partitionId", 4), R("m_OwningElementId", 14)); // 16
        Add("ElemTable", R("m_elemArr", 16, 0x50)); // 17
        Add("Element", R("m_id", 14), R("m_designOptionId", 14)); // 18
        Add("ElementParents", R("m_deletion", 14, 0x50)); // 19
        Add("ElementHeader", R("m_categroryId", 14), R("m_designOptionId", 14), new("m_pBBox", 14, 1), new("m_parents", 14, 1)); // 20
        Add("Plane", Vector("m_origin"), Vector("m_xVec"), Vector("m_yVec")); // 21
        Add("Face", new Fld("m_pSurf", 14, 1)); // 22
        schema.Top(new("DatumPlane", new Back(18), 1, [new("m_pFace", 14, 1), new("m_pSurface", 14, 1)], [])); // 23
        schema.Top(new("Level", new Back(23), 1, [R("m_attrId", 14)], [])); // 24
        schema.Top(new("LevelAttributes", new Back(18), 1, [new("m_baseElevation", 4)], [])); // 25
        Add("Trf", new("m_3x3", 13, 0x10, Fixed: 3, Args: [Vector("")]), Vector("m_or")); // 26
        Add("InstanceInfo", R("m_trf", 26)); // 27
        schema.Top(new("Instance", new Back(18), 1, [new("m_pInstanceInfo", 14, 1)], [])); // 28
        schema.Top(new("GeoLocation", new Back(28), 1, [], [])); // 29
        schema.Top(new("ActiveGeoLocationTrackingElement", new Back(18), 1,
            [R("m_activeGeoLocationId", 14), R("m_projectGeoLocationId", 14)], [])); // 30
        Add("SerializedDummy"); // 31
        schema.Top(new("Dependent", new Back(18), 1, [], [])); // 32
        for (int tag = 33; tag < tags.ContentKey; tag++)
            Add("Filler" + tag);
        foreach (string name in new[] { "ContentKey", "ContentMarker", "SegmentMarker", "SegmentCheckback", "SignatureMarker" })
            Add(name);

        (ulong Owner, ushort ClassTag, byte[] Payload)[] records =
        [
            (ElevationLevelId, 24, ElevationLevelBody(0, differentFace ? 1 : 0)[2..]),
            (ElevationTypeId, 25, Pack(w => { Element(w, ElevationTypeId); w.Write(elevationBase); })),
            (ElevationLocationId, 29, Location(ElevationLocationId, -123.25)),
            (ElevationProjectLocationId, 29, Location(ElevationProjectLocationId, 0)),
            (ElevationTrackingId, 30, Pack(w => { Element(w, ElevationTrackingId); w.Write(ElevationLocationId); w.Write(ElevationProjectLocationId); })),
        ];
        if (dependent)
            records = [.. records, (ElevationDependentId, (ushort)32, Pack(w => Element(w, ElevationDependentId)))];

        string file = Path.Combine(_folder, "elevation.rvt");
        using var root = RootStorage.Create(file, OpenMcdf.Version.V4);
        using (var info = root.CreateStream("BasicFileInfo"))
            info.Write(RvtWriterTests.BuildBasicFileInfoBytes(2022));
        using (var formats = root.CreateStorage("Formats").CreateStream("Latest"))
            formats.Write(PagedStreamCodec.Encode([], schema.Build()));
        byte[] table = new byte[6 + 28 * records.Length];
        table[0] = 17;
        table[2] = (byte)records.Length;
        for (int i = 0; i < records.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(6 + 28 * i), (int)records[i].Owner);
        using (var elemTable = root.CreateStorage("Global").CreateStream("ElemTable"))
            elemTable.Write(PagedStreamCodec.Encode(new byte[8], table));

        using var partition = root.CreateStorage("Partitions").CreateStream("0");
        var block = PartitionStreamEditorTests.MakeStandardBlock(4, keyed: false, records);
        for (int i = 0; i < records.Length; i++)
        {
            ulong owner = records[i].Owner;
            block.Chains[0].Records[i] = new(owner, Pack(w =>
            {
                w.Write((ushort)20);
                w.Write(owner == ElevationLevelId ? (int)BuiltInCategory.OST_Levels : 0);
                w.Write(-1);
                WritePointer(w, 0);
                WritePointer(w, owner == ElevationDependentId ? 19 : 0);
                if (owner == ElevationDependentId)
                {
                    w.Write(1);
                    w.Write(ElevationLevelId);
                }
            }));
            block.Chains[2].Records[i] = new(owner, [31, 0]);
        }
        partition.Write(PartitionStreamEditorTests.BuildPartitionStream(4, tags, block));
        return file;

        static byte[] Location(int id, double elevation) => Pack(w =>
        {
            Element(w, id);
            WritePointer(w, 27);
            foreach (double value in new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, elevation })
                w.Write(value);
        });
    }

    private static byte[] ElevationLevelBody(double surface, double face) => Pack(w =>
    {
        w.Write((ushort)24);
        w.Write(ElevationLevelId);
        w.Write(-1);
        WritePointer(w, 22);
        WritePointer(w, 21);
        w.Write(ElevationTypeId);
        WritePointer(w, 21);
        foreach (double height in new[] { surface, face })
            foreach (double value in new double[] { 0, 0, height, 1, 0, 0, 0, 1, 0 })
                w.Write(value);
    });
}
