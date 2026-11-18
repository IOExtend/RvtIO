using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using OpenMcdf;
using RvtIO.Internal;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;
using Cls = RvtIO.Tests.SchemaStreamEditorTests.Cls;
using Fld = RvtIO.Tests.SchemaStreamEditorTests.Fld;
using NoRef = RvtIO.Tests.SchemaStreamEditorTests.NoRef;

namespace RvtIO.Tests;

public sealed class RvtWriterTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rvtio-writer-" + Guid.NewGuid().ToString("N"));

    public RvtWriterTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string P(string name) => Path.Combine(_folder, name);

    private static readonly IReadOnlyDictionary<int, (int ContentMarkerId, int TotalClasses)> ReleaseTableSpecs =
        new Dictionary<int, (int, int)>
        {
            [2020] = (779, 4164),
            [2021] = (801, 4168),
            [2022] = (817, 4307),
            [2023] = (869, 4418),
            [2024] = (891, 4492),
            [2025] = (913, 4600),
            [2026] = (931, 4690),
            [2027] = (951, 4757),
        };

    internal static byte[] BuildSchemaBytesForRelease(int release, bool includeFramingMarkers = true)
    {
        var writer = new SchemaStreamEditorTests.Writer();
        if (release == 2019)
        {
            for (int i = 0; i < 4098; i++)
                writer.Top(new Cls("R19Class" + i, new NoRef(), 1, [], []));
            return writer.Build();
        }

        var (markerId, totalClasses) = ReleaseTableSpecs[release];
        // SchemaStreamEditorTests.Writer assigns class IDs starting at 12.
        int currentId = 12;
        int targetBeforeContentKey = includeFramingMarkers ? markerId - 1 : markerId + 10;
        while (currentId < targetBeforeContentKey)
        {
            writer.Top(new Cls("Filler" + currentId, new NoRef(), 1, [], []));
            currentId++;
        }

        if (includeFramingMarkers)
        {
            writer.Top(new Cls("ContentKey", new NoRef(), 1, [], []));       // markerId - 1
            writer.Top(new Cls("ContentMarker", new NoRef(), 1, [], []));    // markerId
            writer.Top(new Cls("SegmentMarker", new NoRef(), 1, [], []));    // markerId + 1
            writer.Top(new Cls("SegmentCheckback", new NoRef(), 1, [], [])); // markerId + 2
            writer.Top(new Cls("SignatureMarker", new NoRef(), 1, [], []));  // markerId + 3
            // Sample domain class with one int32 field (type 0x05) so WideBlockWalker walks it cleanly!
            writer.Top(new Cls("SyntheticItem", new NoRef(), 2, [new Fld("m_code", 0x05)], ["00112233445566778899aabbccddeeff"]));
            writer.Top(new Cls("RvtLinkSymbol", new NoRef(), 1, [new Fld("m_code", 0x05)], ["112233445566778899aabbccddeeff00"]));
        }

        while (writer.Placed.Count < totalClasses)
            writer.Top(new Cls("TailClass" + writer.Placed.Count, new NoRef(), 1, [], []));

        return writer.Build();
    }

    internal static byte[] BuildBasicFileInfoBytes(
        int? savedRelease,
        string? worksharing = "Not enabled",
        string? cloudModel = "False",
        string? centralPath = null,
        string? lastSavePath = null)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(13); // StreamVersion
        void WriteLengthPrefixedUtf16(string text)
        {
            bw.Write(text.Length);
            bw.Write(Encoding.Unicode.GetBytes(text));
        }

        if (savedRelease is int year)
            WriteLengthPrefixedUtf16(year.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (lastSavePath is not null)
            WriteLengthPrefixedUtf16(lastSavePath);
        bw.Write(0); // Separator before the UTF-16 key-value lines block

        var lines = new StringBuilder();
        if (worksharing is not null)
            lines.Append("Worksharing: ").Append(worksharing).Append("\r\n");
        if (savedRelease is int formatYear)
            lines.Append("Format: ").Append(formatYear).Append("\r\n");
        if (centralPath is not null)
            lines.Append("Central Model Path: ").Append(centralPath).Append("\r\n");
        if (cloudModel is not null)
            lines.Append("IsSingleUserCloudModel: ").Append(cloudModel).Append("\r\n");
        bw.Write(Encoding.Unicode.GetBytes(lines.ToString()));
        bw.Write((ushort)0); // Trailing UTF-16 NUL terminator ending the text run in BasicFileInfo
        return ms.ToArray();
    }

    internal static byte[] BuildTransmissionDataBytes(
        string linkedPath,
        string pathType = "Relative",
        string loadState = "Loaded",
        string? extraReferenceXml = null)
    {
        string escapedPath = System.Security.SecurityElement.Escape(linkedPath) ?? "";
        string escapedType = System.Security.SecurityElement.Escape(pathType) ?? "";
        string escapedState = System.Security.SecurityElement.Escape(loadState) ?? "";
        string xml = $"""<TransmissionData version="4" isTransmitted="false"><ExternalFileReference><ElementId>501</ElementId><ExternalFileReferenceType>Revit Link</ExternalFileReferenceType><LastSavedPath>{escapedPath}</LastSavedPath><LastSavedPathType>{escapedType}</LastSavedPathType><LastSavedLoadState>{escapedState}</LastSavedLoadState></ExternalFileReference>{extraReferenceXml ?? ""}</TransmissionData>""";
        byte[] utf16 = Encoding.Unicode.GetBytes(xml);
        byte[] data = new byte[4 + utf16.Length];
        BinaryPrimitives.WriteInt32LittleEndian(data, xml.Length);
        utf16.CopyTo(data.AsSpan(4));
        return data;
    }

    internal sealed class SyntheticFileOptions
    {
        public string FileName { get; init; } = "model.rvt";
        public RvtFileKind Kind { get; init; } = RvtFileKind.Project;
        public int SchemaRelease { get; init; } = 2025;
        public int? SavedRelease { get; init; } = 2025;
        public string? Worksharing { get; init; } = "Not enabled";
        public string? CloudModel { get; init; } = "False";
        public string? CentralPath { get; init; }
        public bool IncludeBasicFileInfo { get; init; } = true;
        public byte[]? RawBasicFileInfoBytes { get; init; }
        public OpenMcdf.Version SectorVersion { get; init; } = OpenMcdf.Version.V4;
        public bool IncludeSchema { get; init; } = true;
        public bool IncludeFramingMarkers { get; init; } = true;
        public bool IncludePartitions { get; init; } = true;
        public string? LinkedPath { get; init; } = @"links\Arch_Link.rvt";
        public string LinkedPathType { get; init; } = "Relative";
        public string LinkedLoadState { get; init; } = "Loaded";
        public byte[]? RawTransmissionDataBytes { get; init; }
        public bool IncludeRevitLinkRecord { get; init; }
    }

    internal string CreateSyntheticRevitFile(SyntheticFileOptions? options = null)
    {
        var opt = options ?? new SyntheticFileOptions();
        return CreateSyntheticRevitFile(P(opt.FileName), opt);
    }

    internal static string CreateSyntheticRevitFile(string path, SyntheticFileOptions? options = null)
    {
        var opt = options ?? new SyntheticFileOptions();
        using var root = RootStorage.Create(path, opt.SectorVersion);

        // BasicFileInfo
        if (opt.IncludeBasicFileInfo)
        {
            using var bfi = root.CreateStream("BasicFileInfo");
            if (opt.RawBasicFileInfoBytes is not null)
            {
                bfi.Write(opt.RawBasicFileInfoBytes);
            }
            else
            {
                string defaultLastSave = opt.Kind == RvtFileKind.Family ? @"C:\Models\Sample.rfa" : opt.Kind == RvtFileKind.Project ? @"C:\Models\Sample.rvt" : "";
                bfi.Write(BuildBasicFileInfoBytes(
                    opt.SavedRelease,
                    opt.Worksharing,
                    opt.CloudModel,
                    opt.CentralPath,
                    string.IsNullOrEmpty(defaultLastSave) ? null : defaultLastSave));
            }
        }

        // Kind-defining stream + custom opaque vendor stream + custom opaque vendor storage with metadata
        if (opt.Kind == RvtFileKind.Family)
        {
            using var atom = root.CreateStream("PartAtom");
            atom.Write(Encoding.UTF8.GetBytes("<entry xmlns='http://www.w3.org/2005/Atom'><title>SyntheticFamily</title></entry>"));
        }
        else if (opt.Kind == RvtFileKind.Project)
        {
            using var pi = root.CreateStream("ProjectInformation");
            pi.Write([1, 2, 3, 4, 5, 6, 7, 8]);
        }

        if (opt.RawTransmissionDataBytes is not null)
        {
            using var td = root.CreateStream("TransmissionData");
            td.Write(opt.RawTransmissionDataBytes);
        }
        else if (opt.LinkedPath is not null)
        {
            using var td = root.CreateStream("TransmissionData");
            td.Write(BuildTransmissionDataBytes(opt.LinkedPath, opt.LinkedPathType, opt.LinkedLoadState));
        }

        using (var custom = root.CreateStream("CustomOpaqueVendorStream"))
            custom.Write([0xCA, 0xFE, 0xBA, 0xBE, 0x42]);

        var vendorStorage = root.CreateStorage("CustomOpaqueVendorStorage");
        using (var vendorStream = vendorStorage.CreateStream("OpaqueBlob"))
            vendorStream.Write([0xDE, 0xAD, 0xBE, 0xEF]);
        vendorStorage.CLSID = Guid.Parse("01020304-0506-0708-090a-0b0c0d0e0f10");
        vendorStorage.StateBits = 0x12345678u;
        vendorStorage.CreationFileTime = 130_111_222_333_444_555UL;
        vendorStorage.ModifiedFileTime = 131_222_333_444_555_666UL;

        // Global storage with 8-byte prefixes matching real Revit files
        var global = root.CreateStorage("Global");
        using (var gl = global.CreateStream("Latest"))
            gl.Write(PagedStreamCodec.Encode([5, 0, 0, 0, 0, 0, 0, 0], [10, 20, 30, 40]));
        using (var pt = global.CreateStream("PartitionTable"))
            pt.Write(PagedStreamCodec.Encode([0, 0, 0, 0, 0, 0, 0, 0], [1, 0, 0, 0]));

        // Formats storage
        var formats = root.CreateStorage("Formats");
        SchemaDocument? parsedSchema = null;
        if (opt.IncludeSchema)
        {
            byte[] schemaBytes = BuildSchemaBytesForRelease(opt.SchemaRelease, opt.IncludeFramingMarkers);
            parsedSchema = TfSchemaReader.Parse(schemaBytes);
            using var fl = formats.CreateStream("Latest");
            fl.Write(PagedStreamCodec.Encode([], schemaBytes));
        }

        // Partitions storage
        var partitions = root.CreateStorage("Partitions");
        if (opt.IncludePartitions && parsedSchema is not null && opt.IncludeFramingMarkers && opt.SchemaRelease >= 2020)
        {
            var tags = PartitionFraming.TagsOf(new FormatsSchema(parsedSchema))!.Value;
            int idWidth = opt.SchemaRelease >= 2024 ? 8 : 4;
            ushort syntheticItemTag = (ushort)parsedSchema.Classes.Single(c => c.Name == "SyntheticItem").Id;

            byte[] code100 = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(code100, 100);
            byte[] unknownOpaqueBody = [0x99, 0x88, 0x77, 0x66, 0x55];

            var projectRecords = new List<(ulong Owner, ushort ClassTag, byte[] Payload)>
            {
                (101UL, syntheticItemTag, code100),
                (202UL, 65000, unknownOpaqueBody), // unknown class tag + opaque payload
            };
            if (opt.IncludeRevitLinkRecord)
            {
                ushort linkTag = (ushort)parsedSchema.Classes.Single(c => c.Name == "RvtLinkSymbol").Id;
                projectRecords.Add((501UL, linkTag, code100));
            }

            var projectBlock = PartitionStreamEditorTests.MakeStandardBlock(
                idWidth,
                keyed: false,
                projectRecords.ToArray());
            var familyBlock = PartitionStreamEditorTests.MakeStandardBlock(
                idWidth,
                keyed: true,
                [(101UL, syntheticItemTag, code100)]);

            byte[] partBytes = PartitionStreamEditorTests.BuildPartitionStream(idWidth, tags, projectBlock, familyBlock);
            using var p1 = partitions.CreateStream("1");
            p1.Write(partBytes);
        }

        return path;
    }

    private static string FileSha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Theory]
    [InlineData(2020, RvtFileKind.Project, 4)]
    [InlineData(2020, RvtFileKind.Family, 4)]
    [InlineData(2021, RvtFileKind.Project, 4)]
    [InlineData(2022, RvtFileKind.Family, 4)]
    [InlineData(2023, RvtFileKind.Project, 4)]
    [InlineData(2024, RvtFileKind.Project, 8)]
    [InlineData(2025, RvtFileKind.Family, 8)]
    [InlineData(2026, RvtFileKind.Project, 8)]
    [InlineData(2027, RvtFileKind.Project, 8)]
    [InlineData(2027, RvtFileKind.Family, 8)]
    public void InspectCapabilities_SupportsAllOperations_Across2020To2027ProjectsAndFamilies(
        int release,
        RvtFileKind kind,
        int expectedIdWidth)
    {
        string ext = kind == RvtFileKind.Family ? ".rfa" : ".rvt";
        string path = CreateSyntheticRevitFile(new SyntheticFileOptions
        {
            FileName = $"model_{release}{ext}",
            Kind = kind,
            SchemaRelease = release,
            SavedRelease = release,
        });

        var caps = RvtWriter.InspectCapabilities(path);

        Assert.Equal(kind, caps.FileKind);
        Assert.Equal(release, caps.SavedRelease);
        Assert.Equal(release, caps.SchemaRelease);
        Assert.Equal(release, caps.EffectiveRelease);
        Assert.Equal(expectedIdWidth, caps.ElementIdWidth);
        Assert.False(caps.IsWorkshared);
        Assert.False(caps.IsCloudModel);
        Assert.Equal(1, caps.PartitionStreamCount);
        Assert.True(caps.Supports(RvtWriteOperation.SaveAs));
        Assert.True(caps.Supports(RvtWriteOperation.PagedStreamRewrite));
        Assert.True(caps.Supports(RvtWriteOperation.PartitionRecordRewrite));
    }

    [Fact]
    public void InspectCapabilities_AndWrite_RefuseUnsupportedStatesWithStableCodes()
    {
        // 1. Release 2019 -> unsupported-release
        string r2019 = CreateSyntheticRevitFile(new SyntheticFileOptions
        {
            FileName = "r2019.rvt",
            SchemaRelease = 2019,
            SavedRelease = 2019,
            IncludePartitions = false,
        });
        var caps2019 = RvtWriter.InspectCapabilities(r2019);
        Assert.False(caps2019.Supports(RvtWriteOperation.SaveAs));
        Assert.Equal("unsupported-release", caps2019.Operations[RvtWriteOperation.SaveAs].RefusalCode);
        var ex2019 = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SaveAs(r2019, P("r2019_out.rvt")));
        Assert.Equal("unsupported-release", ex2019.Kind);
        Assert.False(File.Exists(P("r2019_out.rvt")));

        // 2. SavedRelease != SchemaRelease -> release-mismatch
        string mismatch = CreateSyntheticRevitFile(new SyntheticFileOptions
        {
            FileName = "mismatch.rvt",
            SchemaRelease = 2024,
            SavedRelease = 2025,
        });
        Assert.Equal("release-mismatch", RvtWriter.InspectCapabilities(mismatch).Operations[RvtWriteOperation.SaveAs].RefusalCode);

        // 3. Workshared model -> workshared-model
        string workshared = CreateSyntheticRevitFile(new SyntheticFileOptions
        {
            FileName = "workshared.rvt",
            Worksharing = "Central",
        });
        string wsHash = FileSha256(workshared);
        var exWs = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SaveAs(workshared, P("ws_out.rvt")));
        Assert.Equal("workshared-model", exWs.Kind);
        Assert.Equal(wsHash, FileSha256(workshared));
        Assert.False(File.Exists(P("ws_out.rvt")));

        // 4. Single-user cloud model & cloud central path -> cloud-model
        string cloud1 = CreateSyntheticRevitFile(new SyntheticFileOptions
        {
            FileName = "cloud1.rvt",
            CloudModel = "True",
        });
        Assert.Equal("cloud-model", Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SaveAs(cloud1, P("c1_out.rvt"))).Kind);

        string cloud2 = CreateSyntheticRevitFile(new SyntheticFileOptions
        {
            FileName = "cloud2.rvt",
            CentralPath = "Autodesk Docs://Project/Model.rvt",
        });
        Assert.Equal("cloud-model", Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SaveAs(cloud2, P("c2_out.rvt"))).Kind);

        // 5. Unknown file kind -> unknown-file-kind
        string unknownKind = CreateSyntheticRevitFile(new SyntheticFileOptions
        {
            FileName = "mystery.bin",
            Kind = RvtFileKind.Unknown,
        });
        Assert.Equal("unknown-file-kind", RvtWriter.InspectCapabilities(unknownKind).Operations[RvtWriteOperation.SaveAs].RefusalCode);

        // 6. Missing schema -> missing-schema
        string noSchema = CreateSyntheticRevitFile(new SyntheticFileOptions
        {
            FileName = "noschema.rvt",
            IncludeSchema = false,
            IncludePartitions = false,
        });
        Assert.Equal("missing-schema", RvtWriter.InspectCapabilities(noSchema).Operations[RvtWriteOperation.SaveAs].RefusalCode);

        // 7. Missing partitions -> SaveAs & PagedStreamRewrite supported, PartitionRecordRewrite refused with missing-partitions!
        string noParts = CreateSyntheticRevitFile(new SyntheticFileOptions
        {
            FileName = "noparts.rvt",
            IncludePartitions = false,
        });
        var capsNoParts = RvtWriter.InspectCapabilities(noParts);
        Assert.True(capsNoParts.Supports(RvtWriteOperation.SaveAs));
        Assert.True(capsNoParts.Supports(RvtWriteOperation.PagedStreamRewrite));
        Assert.False(capsNoParts.Supports(RvtWriteOperation.PartitionRecordRewrite));
        Assert.Equal("missing-partitions", capsNoParts.Operations[RvtWriteOperation.PartitionRecordRewrite].RefusalCode);
    }

    [Theory]
    [InlineData(OpenMcdf.Version.V3)]
    [InlineData(OpenMcdf.Version.V4)]
    public void SaveAs_AndDocumentSaveAs_PreserveEveryStreamExternalRefAndUnknownPayload(OpenMcdf.Version sectorVersion)
    {
        string source = CreateSyntheticRevitFile(new SyntheticFileOptions
        {
            FileName = $"source_{sectorVersion}.rvt",
            SectorVersion = sectorVersion,
            SchemaRelease = 2026,
            SavedRelease = 2026,
            LinkedPath = @"links\Structural_2026.rvt",
        });
        string sourceSha = FileSha256(source);
        string copy1 = P($"copy1_{sectorVersion}.rvt");
        string copy2 = P($"copy2_{sectorVersion}.rvt");

        var writeResult = RvtWriter.SaveAs(source, copy1);
        Assert.Equal(RvtFileKind.Project, writeResult.FileKind);
        Assert.Equal(2026, writeResult.EffectiveRelease);
        Assert.Equal(0, writeResult.ReplacedStreamCount);
        Assert.Equal(0, writeResult.RewrittenRecordCount);

        var doc = RvtDocument.Open(copy1);
        Assert.Equal(Path.GetFullPath(copy1), doc.SourcePath);
        Assert.NotNull(doc.TransmissionData);
        var extRef = Assert.Single(doc.TransmissionData!.ExternalReferences);
        Assert.Equal(@"links\Structural_2026.rvt", extRef.Path);
        Assert.Equal(501, extRef.ElementId);

        // SaveAs directly from RvtDocument
        var docSaveResult = doc.SaveAs(copy2);
        Assert.Equal(writeResult.StreamCount, docSaveResult.StreamCount);

        // Census matches identically with 0 checksum mismatches, including unknown class #65000
        var censusSource = CoverageCensus.Read(source);
        var censusCopy = CoverageCensus.Read(copy2);
        Assert.Equal(0, censusCopy.Totals.ChecksumMismatches);
        Assert.Equal(censusSource.Totals.Items, censusCopy.Totals.Items);
        Assert.Equal(censusSource.Totals.Exact, censusCopy.Totals.Exact);
        Assert.Contains(censusCopy.Classes, c => c.Class == "(class 65000)");

        // Opaque custom stream and opaque vendor storage directory-entry metadata preserved across both SaveAs hops
        foreach (string savedCopy in new[] { copy1, copy2 })
        {
            using var root = RootStorage.OpenRead(savedCopy);
            using (var custom = root.OpenStream("CustomOpaqueVendorStream"))
            {
                byte[] buf = new byte[custom.Length];
                custom.ReadExactly(buf);
                Assert.Equal(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE, 0x42 }, buf);
            }

            var vendorStorage = root.OpenStorage("CustomOpaqueVendorStorage");
            Assert.Equal(Guid.Parse("01020304-0506-0708-090a-0b0c0d0e0f10"), vendorStorage.CLSID);
            Assert.Equal(0x12345678u, vendorStorage.StateBits);
            Assert.Equal(130_111_222_333_444_555UL, vendorStorage.CreationFileTime);
            Assert.Equal(131_222_333_444_555_666UL, vendorStorage.ModifiedFileTime);
            using (var vendorStream = vendorStorage.OpenStream("OpaqueBlob"))
            {
                byte[] blob = new byte[vendorStream.Length];
                vendorStream.ReadExactly(blob);
                Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, blob);
            }
        }

        Assert.Equal(sourceSha, FileSha256(source));

        // Document FromInfo without backing path refuses SaveAs cleanly
        var inMemoryDoc = RvtDocument.FromInfo(doc.RawData);
        Assert.Null(inMemoryDoc.SourcePath);
        Assert.Throws<InvalidOperationException>(() => inMemoryDoc.SaveAs(P("should_not_exist.rvt")));
    }

    [Fact]
    public void Rewrite_AppliesTargetedPagedAndPartitionEdits_WhilePreservingUnchangedStreamsAndUnknownPayloads()
    {
        string source = CreateSyntheticRevitFile(new SyntheticFileOptions
        {
            FileName = "editable_2025.rfa",
            Kind = RvtFileKind.Family,
            SchemaRelease = 2025,
            SavedRelease = 2025,
        });
        string sourceSha = FileSha256(source);

        SchemaDocument schemaDoc;
        using (var srcRoot = RootStorage.OpenRead(source))
            schemaDoc = TfSchemaReader.Read(srcRoot)!;
        ushort syntheticItemTag = (ushort)schemaDoc.Classes.Single(c => c.Name == "SyntheticItem").Id;

        // Lower SyntheticItem version from 2 -> 1 in Formats/Latest via SchemaStreamEditor
        byte[] rawSchemaPayload;
        using (var srcRoot = RootStorage.OpenRead(source))
            rawSchemaPayload = PagedGzipStream.Read(srcRoot, "Formats", "Latest")!;
        byte[] editedSchemaPayload = SchemaStreamEditor.Apply(rawSchemaPayload, [new SchemaEdit("SyntheticItem", Version: 1)]);

        // Replace SyntheticItem (owner 101) m_code from 100 -> 777777 in Partitions/1
        byte[] newCodePayload = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(newCodePayload, 777_777);
        byte[] newRecordBody = PartitionStreamEditorTests.TaggedBody(syntheticItemTag, newCodePayload);

        string output = P("edited_2025.rfa");
        var result = RvtWriter.Rewrite(source, output, new RvtWriteRequest
        {
            PagedStreamEdits =
            [
                new RvtPagedStreamEdit("Formats/Latest", editedSchemaPayload),
                new RvtPagedStreamEdit("Global/Latest", new byte[] { 88, 99, 111 }),
            ],
            PartitionRecordEdits =
            [
                new RvtPartitionRecordEdit(PartitionNumber: 1, Channel: 102, OwnerId: 101UL, Body: newRecordBody),
            ],
        });

        Assert.Equal(RvtFileKind.Family, result.FileKind);
        Assert.Equal(2025, result.EffectiveRelease);
        Assert.Equal(3, result.ReplacedStreamCount);
        Assert.Equal(1, result.RewrittenRecordCount);
        Assert.Equal(sourceSha, FileSha256(source));

        // Read back and verify all invariants
        var census = CoverageCensus.Read(output);
        Assert.Null(census.RecordsFailure);
        Assert.Equal(0, census.Totals.ChecksumMismatches);
        // SyntheticItem records in both project block and family block walk to exact end; unknown #65000 stays preserved!
        var syntheticRow = census.Classes.Single(c => !c.Family && c.Channel == 102 && c.Class == "SyntheticItem");
        Assert.Equal(1, syntheticRow.Sums.Exact);
        Assert.Contains(census.Classes, c => !c.Family && c.Channel == 102 && c.Class == "(class 65000)");

        using var outRoot = RootStorage.OpenRead(output);
        Assert.Equal(1, TfSchemaReader.Read(outRoot)!.Classes.Single(c => c.Name == "SyntheticItem").Version);
        Assert.Equal(new byte[] { 88, 99, 111 }, PagedGzipStream.Read(outRoot, "Global", "Latest")!);

        // Global/Latest kept its (5,0) 8-byte prefix!
        using var glStream = outRoot.OpenStorage("Global").OpenStream("Latest");
        byte[] glStored = new byte[glStream.Length];
        glStream.ReadExactly(glStored);
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(glStored.AsSpan(0, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(glStored.AsSpan(4, 4)));
    }

    [Fact]
    public void Rewrite_RefusesInvalidTargetsAndLeavesSourceAndDestinationUntouched()
    {
        string source = CreateSyntheticRevitFile(new SyntheticFileOptions { FileName = "intact.rvt" });
        string sourceSha = FileSha256(source);

        // Existing destination is never overwritten
        string existing = P("existing.rvt");
        File.WriteAllBytes(existing, [1, 2, 3]);
        Assert.Throws<IOException>(() => RvtWriter.SaveAs(source, existing));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(existing));

        // Source as destination is refused
        Assert.Throws<IOException>(() => RvtWriter.SaveAs(source, source));
        Assert.Equal(sourceSha, FileSha256(source));

        // Non-paged stream in PagedStreamEdits
        var exPlain = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.Rewrite(source, P("bad1.rvt"), new RvtWriteRequest
        {
            PagedStreamEdits = [new RvtPagedStreamEdit("BasicFileInfo", new byte[] { 1, 2 })],
        }));
        Assert.Equal("invalid-stream-target", exPlain.Kind);

        // Corrupt schema payload for Formats/Latest
        var exBadSchema = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.Rewrite(source, P("bad2.rvt"), new RvtWriteRequest
        {
            PagedStreamEdits = [new RvtPagedStreamEdit("Formats/Latest", new byte[] { 0xFF, 0xFF, 0xFF })],
        }));
        Assert.Equal("invalid-schema-payload", exBadSchema.Kind);

        // Missing partition number
        var exMissingPart = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.Rewrite(source, P("bad3.rvt"), new RvtWriteRequest
        {
            PartitionRecordEdits = [new RvtPartitionRecordEdit(99, 102, 101UL, new byte[] { 1, 2, 3 })],
        }));
        Assert.Equal("missing-partition", exMissingPart.Kind);

        // Missing record owner
        var exMissingRec = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.Rewrite(source, P("bad4.rvt"), new RvtWriteRequest
        {
            PartitionRecordEdits = [new RvtPartitionRecordEdit(1, 102, 99999UL, new byte[] { 1, 2, 3 })],
        }));
        Assert.Equal("missing-record", exMissingRec.Kind);

        // Duplicate record edit
        var exDupRec = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.Rewrite(source, P("bad5.rvt"), new RvtWriteRequest
        {
            PartitionRecordEdits =
            [
                new RvtPartitionRecordEdit(1, 102, 101UL, new byte[] { 1, 2, 3 }),
                new RvtPartitionRecordEdit(1, 102, 101UL, new byte[] { 1, 2, 4 }),
            ],
        }));
        Assert.Equal("duplicate-edit", exDupRec.Kind);

        // Invalid record body (< 2 bytes)
        var exShort = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.Rewrite(source, P("bad6.rvt"), new RvtWriteRequest
        {
            PartitionRecordEdits = [new RvtPartitionRecordEdit(1, 102, 101UL, new byte[] { 1 })],
        }));
        Assert.Equal("invalid-record-target", exShort.Kind);

        // Verify source file untouched and zero temp/bad files left behind
        Assert.Equal(sourceSha, FileSha256(source));
        Assert.Equal(
            new[] { existing, source }.Order(StringComparer.Ordinal),
            Directory.EnumerateFileSystemEntries(_folder).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void InspectCapabilities_AndWrite_RefuseMissingUnreadableOrIncompleteBasicFileInfo()
    {
        // 1. Workshared + cloud 2025 container stays refused even after BasicFileInfo is deleted
        string cloudWorkshared = CreateSyntheticRevitFile(new SyntheticFileOptions
        {
            FileName = "stripped_cloud_ws.rvt",
            SchemaRelease = 2025,
            SavedRelease = 2025,
            Worksharing = "Enabled",
            CloudModel = "True",
        });
        var initialCaps = RvtWriter.InspectCapabilities(cloudWorkshared);
        Assert.True(initialCaps.IsWorkshared);
        Assert.True(initialCaps.IsCloudModel);
        Assert.False(initialCaps.Supports(RvtWriteOperation.SaveAs));

        using (var root = RootStorage.Open(cloudWorkshared, FileMode.Open))
            root.Delete("BasicFileInfo");

        var strippedCaps = RvtWriter.InspectCapabilities(cloudWorkshared);
        Assert.Null(strippedCaps.IsWorkshared);
        Assert.Null(strippedCaps.IsCloudModel);
        Assert.Null(strippedCaps.SavedRelease);
        foreach (RvtWriteOperation op in Enum.GetValues<RvtWriteOperation>())
        {
            Assert.False(strippedCaps.Supports(op));
            Assert.Equal("missing-metadata", strippedCaps.Operations[op].RefusalCode);
        }
        var exStripped = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SaveAs(cloudWorkshared, P("stripped_out.rvt")));
        Assert.Equal("missing-metadata", exStripped.Kind);
        Assert.False(File.Exists(P("stripped_out.rvt")));

        // 2. Worksharing: Enabled with IsSingleUserCloudModel: False -> workshared-model
        string wsEnabled = CreateSyntheticRevitFile(new SyntheticFileOptions
        {
            FileName = "ws_enabled.rvt",
            Worksharing = "Enabled",
            CloudModel = "False",
        });
        var wsEnabledCaps = RvtWriter.InspectCapabilities(wsEnabled);
        Assert.True(wsEnabledCaps.IsWorkshared);
        Assert.False(wsEnabledCaps.IsCloudModel);
        Assert.Equal("workshared-model", Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SaveAs(wsEnabled, P("ws_enabled_out.rvt"))).Kind);

        // 3. Truncated / undecodable BasicFileInfo -> missing-metadata
        string truncatedBfi = CreateSyntheticRevitFile(new SyntheticFileOptions
        {
            FileName = "truncated_bfi.rvt",
            RawBasicFileInfoBytes = [1, 2, 3, 4],
        });
        var truncCaps = RvtWriter.InspectCapabilities(truncatedBfi);
        Assert.Null(truncCaps.IsWorkshared);
        Assert.Null(truncCaps.IsCloudModel);
        Assert.Equal("missing-metadata", Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SaveAs(truncatedBfi, P("trunc_out.rvt"))).Kind);

        // 4. Missing or unrecognised Worksharing field -> missing-metadata
        string missingWs = CreateSyntheticRevitFile(new SyntheticFileOptions
        {
            FileName = "missing_ws.rvt",
            Worksharing = null,
        });
        Assert.Null(RvtWriter.InspectCapabilities(missingWs).IsWorkshared);
        Assert.Equal("missing-metadata", Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SaveAs(missingWs, P("mws_out.rvt"))).Kind);

        string unknownWs = CreateSyntheticRevitFile(new SyntheticFileOptions
        {
            FileName = "unknown_ws.rvt",
            Worksharing = "CustomMode",
        });
        Assert.Null(RvtWriter.InspectCapabilities(unknownWs).IsWorkshared);
        Assert.Equal("missing-metadata", Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SaveAs(unknownWs, P("uws_out.rvt"))).Kind);

        // 5. Missing or unrecognised IsSingleUserCloudModel field -> missing-metadata
        string missingCloud = CreateSyntheticRevitFile(new SyntheticFileOptions
        {
            FileName = "missing_cloud.rvt",
            CloudModel = null,
        });
        Assert.Null(RvtWriter.InspectCapabilities(missingCloud).IsCloudModel);
        Assert.Equal("missing-metadata", Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SaveAs(missingCloud, P("mc_out.rvt"))).Kind);

        string unknownCloud = CreateSyntheticRevitFile(new SyntheticFileOptions
        {
            FileName = "unknown_cloud.rvt",
            CloudModel = "Maybe",
        });
        Assert.Null(RvtWriter.InspectCapabilities(unknownCloud).IsCloudModel);
        Assert.Equal("missing-metadata", Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SaveAs(unknownCloud, P("uc_out.rvt"))).Kind);

        // 6. Missing saving release in BasicFileInfo -> missing-metadata
        string missingSavedRel = CreateSyntheticRevitFile(new SyntheticFileOptions
        {
            FileName = "missing_rel.rvt",
            SavedRelease = null,
        });
        Assert.Null(RvtWriter.InspectCapabilities(missingSavedRel).SavedRelease);
        Assert.Equal("missing-metadata", Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SaveAs(missingSavedRel, P("mr_out.rvt"))).Kind);
    }

    [Fact]
    public void Rewrite_RefusesValidButIncompatibleReplacementSchema_AndWritesNoOutput()
    {
        string source = CreateSyntheticRevitFile(new SyntheticFileOptions
        {
            FileName = "source_2025.rvt",
            SchemaRelease = 2025,
            SavedRelease = 2025,
        });
        string sourceSha = FileSha256(source);

        // 1. Valid 2024 schema into a 2025 file -> release-mismatch, no output published
        byte[] valid2024Schema = BuildSchemaBytesForRelease(2024);
        var exMismatch = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.Rewrite(source, P("out_2024.rvt"), new RvtWriteRequest
        {
            PagedStreamEdits = [new RvtPagedStreamEdit("Formats/Latest", valid2024Schema)],
        }));
        Assert.Equal("release-mismatch", exMismatch.Kind);

        // 2. Valid 2019 schema into a 2025 file -> release-mismatch, no output published
        byte[] valid2019Schema = BuildSchemaBytesForRelease(2019);
        var ex2019 = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.Rewrite(source, P("out_2019.rvt"), new RvtWriteRequest
        {
            PagedStreamEdits = [new RvtPagedStreamEdit("Formats/Latest", valid2019Schema)],
        }));
        Assert.Equal("release-mismatch", ex2019.Kind);

        // 3. Syntactically valid schema with unrecognised release -> unrecognised-release
        var customWriter = new SchemaStreamEditorTests.Writer();
        for (int i = 0; i < 64; i++)
            customWriter.Top(new Cls("Custom" + i, new NoRef(), 1, [], []));
        var exUnrec = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.Rewrite(source, P("out_unrec.rvt"), new RvtWriteRequest
        {
            PagedStreamEdits = [new RvtPagedStreamEdit("Formats/Latest", customWriter.Build())],
        }));
        Assert.Equal("unrecognised-release", exUnrec.Kind);

        // 4. Release-2025 class count without framing markers when Partitions/1 exists -> missing-framing-markers
        byte[] missingMarkers2025 = BuildSchemaBytesForRelease(2025, includeFramingMarkers: false);
        var exNoMarkers = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.Rewrite(source, P("out_nomarkers.rvt"), new RvtWriteRequest
        {
            PagedStreamEdits = [new RvtPagedStreamEdit("Formats/Latest", missingMarkers2025)],
        }));
        Assert.Equal("missing-framing-markers", exNoMarkers.Kind);

        // 5. Release-2025 schema with ContentMarker still at 913 and 4600 classes, but swapped SegmentMarker / SegmentCheckback IDs -> incompatible-schema-payload
        var shiftedWriter = new SchemaStreamEditorTests.Writer();
        int currentId = 12;
        while (currentId < 912)
        {
            shiftedWriter.Top(new Cls("Filler" + currentId, new NoRef(), 1, [], []));
            currentId++;
        }
        shiftedWriter.Top(new Cls("ContentKey", new NoRef(), 1, [], []));       // 912
        shiftedWriter.Top(new Cls("ContentMarker", new NoRef(), 1, [], []));    // 913 (still matches 2025!)
        shiftedWriter.Top(new Cls("SegmentCheckback", new NoRef(), 1, [], [])); // 914 (swapped!)
        shiftedWriter.Top(new Cls("SegmentMarker", new NoRef(), 1, [], []));    // 915 (swapped!)
        shiftedWriter.Top(new Cls("SignatureMarker", new NoRef(), 1, [], []));  // 916
        while (shiftedWriter.Placed.Count < 4600)
            shiftedWriter.Top(new Cls("TailClass" + shiftedWriter.Placed.Count, new NoRef(), 1, [], []));

        var exShifted = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.Rewrite(source, P("out_shifted.rvt"), new RvtWriteRequest
        {
            PagedStreamEdits = [new RvtPagedStreamEdit("Formats/Latest", shiftedWriter.Build())],
        }));
        Assert.Equal("incompatible-schema-payload", exShifted.Kind);

        Assert.Equal(sourceSha, FileSha256(source));
        Assert.Equal(new[] { source }, Directory.EnumerateFileSystemEntries(_folder).Order(StringComparer.Ordinal));
    }

    private static string ResolveExternalReferenceTarget(string hostPath)
    {
        var doc = RvtDocument.Open(hostPath);
        Assert.NotNull(doc.TransmissionData);
        var extRef = Assert.Single(doc.TransmissionData!.ExternalReferences);
        Assert.False(string.IsNullOrWhiteSpace(extRef.Path));
        string normalized = extRef.Path!
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        string hostDir = Path.GetDirectoryName(Path.GetFullPath(hostPath))!;
        return Path.GetFullPath(Path.Combine(hostDir, normalized));
    }

    // "Relative to Central Model" is what Revit 2022 writes for a relative link
    [Theory]
    [InlineData("Relative to Central Model")]
    [InlineData("Relative")]
    public void SaveAs_AndRewrite_WithValidRelativeLink_PreserveTargetInSameDirectory_AndRefuseDifferentDirectory(string pathType)
    {
        string linksDir = Path.Combine(_folder, "links");
        string inputDir = Path.Combine(_folder, "input");
        string nestedDir = Path.Combine(_folder, "output", "nested");
        Directory.CreateDirectory(linksDir);
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(nestedDir);

        string linkedFile = CreateSyntheticRevitFile(
            Path.Combine(linksDir, "linked.rvt"),
            new SyntheticFileOptions
            {
                FileName = "linked.rvt",
                SchemaRelease = 2022,
                SavedRelease = 2022,
                LinkedPath = null,
            });
        string linkedSha = FileSha256(linkedFile);

        string hostFile = CreateSyntheticRevitFile(
            Path.Combine(inputDir, "host.rvt"),
            new SyntheticFileOptions
            {
                FileName = "host.rvt",
                SchemaRelease = 2022,
                SavedRelease = 2022,
                LinkedPath = @"..\links\linked.rvt",
                LinkedPathType = pathType,
                LinkedLoadState = "Loaded",
                IncludeRevitLinkRecord = true,
            });
        string hostSha = FileSha256(hostFile);

        // Precondition: the relative reference in input/host.rvt resolves to the real linked.rvt file on disk.
        string initialResolved = ResolveExternalReferenceTarget(hostFile);
        Assert.True(File.Exists(initialResolved));
        Assert.True(FileIdentity.SameFile(linkedFile, initialResolved));

        byte[] schemaBytes = BuildSchemaBytesForRelease(2022);
        ushort itemTag = (ushort)TfSchemaReader.Parse(schemaBytes).Classes.Single(c => c.Name == "SyntheticItem").Id;
        byte[] updatedItemBody = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(updatedItemBody.AsSpan(0, 2), itemTag);
        BinaryPrimitives.WriteInt32LittleEndian(updatedItemBody.AsSpan(2, 4), 777);

        var rewriteRequest = new RvtWriteRequest
        {
            PagedStreamEdits = [new RvtPagedStreamEdit("Global/Latest", new byte[] { 42, 43, 44, 45 })],
            PartitionRecordEdits = [new RvtPartitionRecordEdit(1, 102, 101UL, updatedItemBody)],
        };

        // 1. Same-directory destinations preserve the resolved link target on disk for SaveAs, RvtDocument.SaveAs, and Rewrite.
        string sameSaveAs = Path.Combine(inputDir, "host_saveas.rvt");
        var saveAsRes = RvtWriter.SaveAs(hostFile, sameSaveAs);
        Assert.Equal(0, saveAsRes.ReplacedStreamCount);
        string sameSaveAsResolved = ResolveExternalReferenceTarget(sameSaveAs);
        Assert.True(File.Exists(sameSaveAsResolved));
        Assert.True(FileIdentity.SameFile(linkedFile, sameSaveAsResolved));

        string sameDocSaveAs = Path.Combine(inputDir, "host_doc_saveas.rvt");
        RvtDocument.Open(hostFile).SaveAs(sameDocSaveAs);
        string sameDocResolved = ResolveExternalReferenceTarget(sameDocSaveAs);
        Assert.True(File.Exists(sameDocResolved));
        Assert.True(FileIdentity.SameFile(linkedFile, sameDocResolved));

        string sameRewrite = Path.Combine(inputDir, "host_rewrite.rvt");
        var rewriteRes = RvtWriter.Rewrite(hostFile, sameRewrite, rewriteRequest);
        Assert.Equal(1, rewriteRes.RewrittenRecordCount);
        string sameRewriteResolved = ResolveExternalReferenceTarget(sameRewrite);
        Assert.True(File.Exists(sameRewriteResolved));
        Assert.True(FileIdentity.SameFile(linkedFile, sameRewriteResolved));

        // 2. Different-directory destinations would break ../links/linked.rvt resolution and must be refused before publishing output.
        string brokenCandidateTarget = Path.GetFullPath(Path.Combine(nestedDir, "..", "links", "linked.rvt"));
        Assert.False(File.Exists(brokenCandidateTarget));
        Assert.False(FileIdentity.SameFile(linkedFile, brokenCandidateTarget));

        string nestedSaveAs = Path.Combine(nestedDir, "host_saveas.rvt");
        var exSaveAs = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SaveAs(hostFile, nestedSaveAs));
        Assert.Equal("relative-reference-relocation", exSaveAs.Kind);

        string nestedDocSaveAs = Path.Combine(nestedDir, "host_doc_saveas.rvt");
        var exDocSaveAs = Assert.Throws<RvtWriteRefusedException>(() => RvtDocument.Open(hostFile).SaveAs(nestedDocSaveAs));
        Assert.Equal("relative-reference-relocation", exDocSaveAs.Kind);

        string nestedRewrite = Path.Combine(nestedDir, "host_rewrite.rvt");
        var exRewrite = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.Rewrite(hostFile, nestedRewrite, rewriteRequest));
        Assert.Equal("relative-reference-relocation", exRewrite.Kind);

        Assert.Empty(Directory.EnumerateFileSystemEntries(nestedDir));
        Assert.Equal(hostSha, FileSha256(hostFile));
        Assert.Equal(linkedSha, FileSha256(linkedFile));
    }

    [Fact]
    public void SaveAs_AndRewrite_ToDifferentDirectory_SucceedForNonRelativeLinks_AndRefuseCorruptOrStrippedLinkMetadata()
    {
        string linksDir = Path.Combine(_folder, "links");
        string inputDir = Path.Combine(_folder, "input");
        string nestedDir = Path.Combine(_folder, "output", "nested");
        Directory.CreateDirectory(linksDir);
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(nestedDir);

        string linkedFile = CreateSyntheticRevitFile(
            Path.Combine(linksDir, "linked.rvt"),
            new SyntheticFileOptions
            {
                FileName = "linked.rvt",
                SchemaRelease = 2025,
                SavedRelease = 2025,
                LinkedPath = null,
            });
        string fullLinked = Path.GetFullPath(linkedFile);

        // 1. Absolute + Content external references remain valid across directories.
        const string keynoteXml = "<ExternalFileReference><ElementId>502</ElementId><ExternalFileReferenceType>Keynote Table</ExternalFileReferenceType><LastSavedPath>Keynotes.txt</LastSavedPath><LastSavedPathType>Content</LastSavedPathType><LastSavedLoadState>Loaded</LastSavedLoadState></ExternalFileReference>";
        string absHost = CreateSyntheticRevitFile(
            Path.Combine(inputDir, "host_abs.rvt"),
            new SyntheticFileOptions
            {
                FileName = "host_abs.rvt",
                SchemaRelease = 2025,
                SavedRelease = 2025,
                RawTransmissionDataBytes = BuildTransmissionDataBytes(fullLinked, "Absolute", "Loaded", keynoteXml),
                IncludeRevitLinkRecord = true,
            });

        string nestedAbsSaveAs = Path.Combine(nestedDir, "host_abs_saveas.rvt");
        RvtWriter.SaveAs(absHost, nestedAbsSaveAs);
        var reopenedAbs = RvtDocument.Open(nestedAbsSaveAs);
        Assert.NotNull(reopenedAbs.TransmissionData);
        Assert.Equal(2, reopenedAbs.TransmissionData!.ExternalReferences.Count);
        string resolvedAbs = Path.GetFullPath(reopenedAbs.TransmissionData.ExternalReferences[0].Path!);
        Assert.True(File.Exists(resolvedAbs));
        Assert.True(FileIdentity.SameFile(linkedFile, resolvedAbs));

        string nestedAbsRewrite = Path.Combine(nestedDir, "host_abs_rewrite.rvt");
        RvtWriter.Rewrite(absHost, nestedAbsRewrite, new RvtWriteRequest
        {
            PagedStreamEdits = [new RvtPagedStreamEdit("Global/Latest", new byte[] { 1, 2, 3, 4 })],
        });
        Assert.True(File.Exists(nestedAbsRewrite));

        // 2. Malformed TransmissionData succeeds in the same directory (verbatim copy) and refuses cross-directory relocation.
        string corruptTdHost = CreateSyntheticRevitFile(
            Path.Combine(inputDir, "host_corrupt_td.rvt"),
            new SyntheticFileOptions
            {
                FileName = "host_corrupt_td.rvt",
                SchemaRelease = 2025,
                SavedRelease = 2025,
                RawTransmissionDataBytes = [0x05, 0x00, 0x00, 0x00, 0x3C, 0x00], // truncated UTF-16 XML
            });

        string sameCorruptCopy = Path.Combine(inputDir, "host_corrupt_td_copy.rvt");
        RvtWriter.SaveAs(corruptTdHost, sameCorruptCopy);
        Assert.True(File.Exists(sameCorruptCopy));

        string nestedCorruptCopy = Path.Combine(nestedDir, "host_corrupt_td.rvt");
        var exCorrupt = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SaveAs(corruptTdHost, nestedCorruptCopy));
        Assert.Equal("unreadable-transmission-data", exCorrupt.Kind);
        Assert.False(File.Exists(nestedCorruptCopy));

        // 3. RvtLinkSymbol record in Partitions/1 with stripped TransmissionData refuses cross-directory relocation.
        string strippedTdHost = CreateSyntheticRevitFile(
            Path.Combine(inputDir, "host_stripped_td.rvt"),
            new SyntheticFileOptions
            {
                FileName = "host_stripped_td.rvt",
                SchemaRelease = 2025,
                SavedRelease = 2025,
                LinkedPath = null,
                IncludeRevitLinkRecord = true,
            });

        string sameStrippedCopy = Path.Combine(inputDir, "host_stripped_td_copy.rvt");
        RvtWriter.SaveAs(strippedTdHost, sameStrippedCopy);
        Assert.True(File.Exists(sameStrippedCopy));

        string nestedStrippedCopy = Path.Combine(nestedDir, "host_stripped_td.rvt");
        var exStripped = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SaveAs(strippedTdHost, nestedStrippedCopy));
        Assert.Equal("missing-metadata", exStripped.Kind);
        Assert.False(File.Exists(nestedStrippedCopy));
    }

    [Fact]
    public void SaveAs_ToDifferentDirectory_RefusesALinkWithoutAUsableTransmissionDataEntry()
    {
        string inputDir = Path.Combine(_folder, "input");
        string nestedDir = Path.Combine(_folder, "output", "nested");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(nestedDir);
        const string keynoteXml = "<ExternalFileReference><ElementId>502</ElementId><ExternalFileReferenceType>Keynote Table</ExternalFileReferenceType><LastSavedPath></LastSavedPath><LastSavedPathType>Absolute</LastSavedPathType><LastSavedLoadState>Not Found</LastSavedLoadState></ExternalFileReference>";
        byte[] Xml(string body)
        {
            string xml = $"<?xml version=\"1.0\"?><TransmissionData isTransmitted=\"false\" userData=\"\" version=\"5\">{body}</TransmissionData>";
            byte[] data = new byte[4 + xml.Length * 2];
            BinaryPrimitives.WriteInt32LittleEndian(data, xml.Length);
            Encoding.Unicode.GetBytes(xml).CopyTo(data.AsSpan(4));
            return data;
        }

        string Link(string path, string type) =>
            $"<ExternalFileReference><ElementId>501</ElementId><ExternalFileReferenceType>Revit Link</ExternalFileReferenceType><LastSavedPath>{path}</LastSavedPath><LastSavedPathType>{type}</LastSavedPathType><LastSavedLoadState>Loaded</LastSavedLoadState></ExternalFileReference>";

        // Partitions/1 holds RvtLinkSymbol 501: a valid but empty TransmissionData, one that lists only another reference, and ones whose
        // entry for 501 lacks the saved path, its type or both (or adds such an entry to a complete one)
        foreach (var (name, td) in new[]
        {
            ("empty", Xml("")),
            ("other", Xml(keynoteXml)),
            ("bare", Xml(Link("", ""))),
            ("nopath", Xml(Link("", "Relative to Central Model"))),
            ("notype", Xml(Link(@"..\links\linked.rvt", ""))),
            ("second", Xml(Link(@"C:\links\linked.rvt", "Absolute") + Link("", ""))),
        })
        {
            string host = CreateSyntheticRevitFile(
                Path.Combine(inputDir, $"host_{name}.rvt"),
                new SyntheticFileOptions { SchemaRelease = 2022, SavedRelease = 2022, RawTransmissionDataBytes = td, IncludeRevitLinkRecord = true });

            RvtWriter.SaveAs(host, Path.Combine(inputDir, $"host_{name}_copy.rvt"));
            string nested = Path.Combine(nestedDir, $"host_{name}.rvt");
            var ex = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SaveAs(host, nested));
            Assert.Equal("missing-metadata", ex.Kind);
            Assert.Contains("501", ex.Message);
            Assert.False(File.Exists(nested));
        }
    }

    [Fact]
    public void SaveAs_ToDifferentDirectory_RefusesAnUnrecognisedPathType()
    {
        string inputDir = Path.Combine(_folder, "input");
        string nestedDir = Path.Combine(_folder, "output", "nested");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(nestedDir);
        string host = CreateSyntheticRevitFile(
            Path.Combine(inputDir, "host.rvt"),
            new SyntheticFileOptions
            {
                SchemaRelease = 2022,
                SavedRelease = 2022,
                LinkedPath = @"..\links\linked.rvt",
                LinkedPathType = "Relative to Somewhere Else",
                IncludeRevitLinkRecord = true,
            });

        RvtWriter.SaveAs(host, Path.Combine(inputDir, "host_copy.rvt"));
        string nested = Path.Combine(nestedDir, "host.rvt");
        var ex = Assert.Throws<RvtWriteRefusedException>(() => RvtWriter.SaveAs(host, nested));
        Assert.Equal("relative-reference-relocation", ex.Kind);
        Assert.Contains("Relative to Somewhere Else", ex.Message);
        Assert.False(File.Exists(nested));
    }
}
