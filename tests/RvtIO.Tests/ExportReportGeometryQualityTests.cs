using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenMcdf;
using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Per-stream unpacked byte sizes and per-element geometry quality classes in the export report (docs/export-report.md).</summary>
public sealed class ExportReportGeometryQualityTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rvtio-report-geom-" + Guid.NewGuid().ToString("N"));

    public ExportReportGeometryQualityTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private static GeometryData ClosedBox(double x0, double x1, double y0, double y1, double z0, double z1)
    {
        double[][] p =
        [
            [x0, y0, z0], [x1, y0, z0], [x1, y1, z0], [x0, y1, z0],
            [x0, y0, z1], [x1, y0, z1], [x1, y1, z1], [x0, y1, z1],
        ];
        int[][] faces = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
        return new GeometryData
        {
            BoundingBox = [x0, y0, z0, x1, y1, z1],
            Faces = faces.Select(indices => new GeoFace
            {
                OuterVertices = indices.Select(vertex => p[vertex]).ToList(),
                PlaneSurface = true,
            }).ToList(),
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
        };
    }

    private static Element StoreyLevel(int id = 10) => new()
    {
        Id = id,
        Name = "Level 1",
        CategoryId = -2000240,
        Category = "OST_Levels",
        IsNativeLevel = true,
        SerializedFamilyId = -1,
        SerializedUnplacedOwnerId = -1,
        SerializedOwnerDbViewId = -1,
        Parameters =
        [
            new Parameter { Id = -1007102, Definition = new Definition { Name = "LEVEL_ELEV" }, StorageType = StorageType.Double, Value = 0.0 },
            new Parameter { Id = -1007111, StorageType = StorageType.Integer, Value = 1 },
        ],
    };

    private static Element GenericProduct(int id, GeometryData? geometry) => new()
    {
        Id = id,
        CategoryId = -2000151,
        Category = "OST_GenericModel",
        Name = $"Product {id}",
        References = [3],
        Parameters = [new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = 3 }],
        BoundingBox = geometry?.BoundingBox is { } box ? (double[])box.Clone() : null,
        Geometry = geometry,
    };

    /// <summary>First small sample: five exported IFC products spanning exact, approximated, box-only and none.</summary>
    private static RevitFileInfo BuildSmallSampleAlpha()
    {
        var exactGeom = ClosedBox(0, 4, 0, 3, 0, 2);
        var approxGeom = ClosedBox(6, 10, 0, 3, 0, 2);
        approxGeom.UnresolvedSymbol = true;
        var boxOnlyGeom = ClosedBox(12, 15, 0, 3, 0, 2);
        boxOnlyGeom.PlacedSymbolBox = true;

        var roof = GenericProduct(200, null);
        roof.CategoryId = -2000035;
        roof.Category = "OST_Roofs";
        roof.TypeId = 500;

        var panel = GenericProduct(201, ClosedBox(0, 4, 5, 8, 3, 3.2));
        panel.CategoryId = -2000170;
        panel.Category = "OST_CurtainWallPanels";
        panel.SerializedHostId = 200;

        return new RevitFileInfo
        {
            ElemTableIsExact = true,
            Elements =
            [
                StoreyLevel(),
                GenericProduct(100, exactGeom),
                GenericProduct(101, approxGeom),
                GenericProduct(102, boxOnlyGeom),
                roof,
                panel,
            ],
        };
    }

    /// <summary>Second small sample: four exported IFC products exercising wall extrusion fallback, wall bbox fallback, pure wall-cut door and exact BRep.</summary>
    private static RevitFileInfo BuildSmallSampleBeta()
    {
        var wallType = new Element
        {
            Id = 500,
            CategoryId = -2000011,
            Category = "OST_Walls",
            SerializedSystemTypeClassId = 512,
            SerializedHostTypeLayers = [new RawCompoundStructureLayer(BitConverter.DoubleToInt64Bits(1.0), 1, -1, 700, -1, 0, 1)],
            ValidatedHostTypeLayerSetName = "Basic Wall:Generic",
            ValidatedIfcTypeObjectName = "Basic Wall:Generic",
        };

        var approxWall = new Element
        {
            Id = 300,
            CategoryId = -2000011,
            Category = "OST_Walls",
            Name = "Wall 300",
            SerializedSystemTypeId = 500,
            References = [3],
            Parameters =
            [
                new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = 3 },
                new Parameter { Id = -1002050, StorageType = StorageType.ElementId, Value = 500 },
            ],
            Location = new LocationCurve
            {
                CurveType = "Line",
                Length = 10,
                StartPoint = [0, 0, 0],
                EndPoint = [10, 0, 0],
            },
            BoundingBox = [0, -0.5, 0, 10, 0.5, 9],
            Geometry = new GeometryData
            {
                BoundingBox = [0, -0.5, 0, 10, 0.5, 9],
                Solids =
                [
                    new GeoSolid
                    {
                        Position = [5, 0, 0],
                        RefDirection = [1, 0, 0],
                        ExtrusionDirection = [0, 0, 1],
                        ProfileWidth = 10,
                        ProfileDepth = 1,
                        ExtrusionHeight = 9,
                    },
                ],
            },
        };

        var boxOnlyWall = new Element
        {
            Id = 301,
            CategoryId = -2000011,
            Category = "OST_Walls",
            Name = "Wall 301",
            SerializedSystemTypeId = 500,
            References = [3],
            Parameters =
            [
                new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = 3 },
                new Parameter { Id = -1002050, StorageType = StorageType.ElementId, Value = 500 },
            ],
            BoundingBox = [0, 5, 0, 8, 6, 9],
            Geometry = new GeometryData { BoundingBox = [0, 5, 0, 8, 6, 9] },
        };

        var pureCutDoor = new Element
        {
            Id = 302,
            CategoryId = -2000023,
            Category = "OST_Doors",
            Name = "Door 302",
            SerializedHostId = 300,
            References = [300, 3],
            Parameters = [new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = 3 }],
            BoundingBox = [2, -0.5, 0, 5, 0.5, 7],
            Geometry = new GeometryData { BoundingBox = [2, -0.5, 0, 5, 0.5, 7] },
        };

        var exactBlock = GenericProduct(303, ClosedBox(12, 16, 0, 4, 0, 3));

        return new RevitFileInfo
        {
            ElemTableIsExact = true,
            Elements = [StoreyLevel(), wallType, approxWall, boxOnlyWall, pureCutDoor, exactBlock],
        };
    }

    private ExportReport ExportIfcWithReport(string sampleName, RevitFileInfo info)
    {
        string ifcPath = Path.Combine(_folder, sampleName + ".ifc");
        var skipLog = new SkipLog();
        var exporter = new IfcExporter { Profile = IfcProfile.Current, SkipLog = skipLog };
        exporter.Export(info, ifcPath);
        var diagnostics = new RvtReadDiagnostics();
        diagnostics.Streams.Add(new StreamRecord("Partitions/0", 1200, 2, 2, 0, 0, 0, 4096));
        var report = ExportReport.ForRead(sampleName + ".rvt", 2048, diagnostics, false, false,
            info.Elements.Select(e => (uint)e.Id).ToList(), info);
        report.SetExport("ifc", exporter.ExportedProducts, skipLog, exporter.ExportedElementQualities);
        return report;
    }

    private static void AssertExportTotalsConsistent(ExportReport.ReportExport export)
    {
        Assert.Equal(export.Exported, export.Elements.Count);
        Assert.Equal(
            export.Exported,
            export.Geometry.Exact + export.Geometry.Approximated + export.Geometry.BoxOnly + export.Geometry.None);
        Assert.Equal(export.Geometry.Exact, export.Elements.Count(e => e.Quality == "exact"));
        Assert.Equal(export.Geometry.Approximated, export.Elements.Count(e => e.Quality == "approximated"));
        Assert.Equal(export.Geometry.BoxOnly, export.Elements.Count(e => e.Quality == "box-only"));
        Assert.Equal(export.Geometry.None, export.Elements.Count(e => e.Quality == "none"));
    }

    [Fact]
    public void PinsIfcAndJsonGeometryQualityTotalsAcrossTwoSmallSamples()
    {
        // Sample 1: five IFC products (2 exact, 1 approximated, 1 box-only, 1 none) and six compact JSON rows.
        var alpha = BuildSmallSampleAlpha();
        var alphaIfcReport = ExportIfcWithReport("sample-alpha", alpha);
        AssertExportTotalsConsistent(alphaIfcReport.Export!);
        Assert.Equal((6, 5, 1), (alphaIfcReport.Export!.Considered, alphaIfcReport.Export.Exported, alphaIfcReport.Export.Skipped));
        Assert.Equal((2, 1, 1, 1), (
            alphaIfcReport.Export.Geometry.Exact,
            alphaIfcReport.Export.Geometry.Approximated,
            alphaIfcReport.Export.Geometry.BoxOnly,
            alphaIfcReport.Export.Geometry.None));
        Assert.Equal(
            [(100, "exact"), (101, "approximated"), (102, "box-only"), (201, "exact"), (200, "none")],
            alphaIfcReport.Export.Elements.Select(e => (e.Id, e.Quality)).ToArray());

        var alphaJsonReport = ExportReport.ForRead("sample-alpha.rvt", 2048, new RvtReadDiagnostics(), false, false, [10u], alpha);
        alphaJsonReport.SetJsonExport(alpha, full: false);
        AssertExportTotalsConsistent(alphaJsonReport.Export!);
        Assert.Equal((6, 6, 0), (alphaJsonReport.Export!.Considered, alphaJsonReport.Export.Exported, alphaJsonReport.Export.Skipped));
        Assert.Equal((2, 1, 1, 2), (
            alphaJsonReport.Export.Geometry.Exact,
            alphaJsonReport.Export.Geometry.Approximated,
            alphaJsonReport.Export.Geometry.BoxOnly,
            alphaJsonReport.Export.Geometry.None));

        // Sample 2: four IFC products (1 exact, 1 approximated, 1 box-only, 1 none) and six compact JSON rows.
        var beta = BuildSmallSampleBeta();
        var betaIfcReport = ExportIfcWithReport("sample-beta", beta);
        AssertExportTotalsConsistent(betaIfcReport.Export!);
        Assert.Equal((6, 4, 2), (betaIfcReport.Export!.Considered, betaIfcReport.Export.Exported, betaIfcReport.Export.Skipped));
        Assert.Equal((1, 1, 1, 1), (
            betaIfcReport.Export.Geometry.Exact,
            betaIfcReport.Export.Geometry.Approximated,
            betaIfcReport.Export.Geometry.BoxOnly,
            betaIfcReport.Export.Geometry.None));
        Assert.Equal(
            [(300, "approximated"), (301, "box-only"), (302, "none"), (303, "exact")],
            betaIfcReport.Export.Elements.Select(e => (e.Id, e.Quality)).ToArray());

        var betaJsonReport = ExportReport.ForRead("sample-beta.rvt", 2048, new RvtReadDiagnostics(), false, false, [10u], beta);
        betaJsonReport.SetJsonExport(beta, full: false);
        AssertExportTotalsConsistent(betaJsonReport.Export!);
        Assert.Equal((6, 6, 0), (betaJsonReport.Export!.Considered, betaJsonReport.Export.Exported, betaJsonReport.Export.Skipped));
        Assert.Equal((2, 0, 2, 2), (
            betaJsonReport.Export.Geometry.Exact,
            betaJsonReport.Export.Geometry.Approximated,
            betaJsonReport.Export.Geometry.BoxOnly,
            betaJsonReport.Export.Geometry.None));

        using var jsonDoc = JsonDocument.Parse(betaIfcReport.ToJson());
        var root = jsonDoc.RootElement;
        Assert.Equal(4096L, root.GetProperty("streams")[0].GetProperty("unpackedBytes").GetInt64());
        var geomNode = root.GetProperty("export").GetProperty("geometry");
        Assert.Equal(1, geomNode.GetProperty("exact").GetInt32());
        Assert.Equal(1, geomNode.GetProperty("approximated").GetInt32());
        Assert.Equal(1, geomNode.GetProperty("boxOnly").GetInt32());
        Assert.Equal(1, geomNode.GetProperty("none").GetInt32());
        Assert.Equal(4, root.GetProperty("export").GetProperty("elements").GetArrayLength());
    }

    [Fact]
    public void PinsPerStreamUnpackedBytesAcrossTwoSmallCompoundFiles()
    {
        byte[] partitionA = CoverageCensusTests.Partition(
            CoverageCensusTests.Block(false, CoverageCensusTests.R(1, CoverageCensusTests.Body("Plain", 5, 0, 0, 0))));
        byte[] partitionB = CoverageCensusTests.Partition(
            CoverageCensusTests.Block(false,
                CoverageCensusTests.R(1, CoverageCensusTests.Body("Plain", 9, 0, 0, 0)),
                CoverageCensusTests.R(2, CoverageCensusTests.Body("Store", 11, 0, 0, 0))));
        byte[] formatsDecoded = CoverageCensusTests.TableBytes;
        byte[] historyDecoded = Enumerable.Range(0, 640).Select(i => (byte)(i * 3)).ToArray();
        byte[] elemTableDecoded = Enumerable.Range(0, 320).Select(i => (byte)(i * 5)).ToArray();

        string sampleOnePath = Path.Combine(_folder, "streams-one.rvt");
        CoverageCensusTests.WriteFile(
            sampleOnePath,
            ("Formats/Latest", PageCodec.EncodePadded(GzipMember.Write(formatsDecoded))),
            ("Global/ElemTable", PageCodec.EncodePadded(GzipMember.Write(elemTableDecoded))),
            ("Global/History", PageCodec.EncodePadded(GzipMember.Write(historyDecoded))),
            ("Partitions/0", partitionA));

        var diagOne = new RvtReadDiagnostics();
        var readerOne = new RvtFileReader(new RvtReaderOptions { Diagnostics = diagOne });
        var infoOne = readerOne.Read(sampleOnePath);
        var reportOne = ExportReport.ForRead("streams-one.rvt", new FileInfo(sampleOnePath).Length, diagOne, false, false, [1u], infoOne);

        Assert.Equal(formatsDecoded.Length, reportOne.Streams.Single(s => s.Name == "Formats/Latest").UnpackedBytes);
        Assert.Equal(elemTableDecoded.Length, reportOne.Streams.Single(s => s.Name == "Global/ElemTable").UnpackedBytes);
        Assert.Equal(historyDecoded.Length, reportOne.Streams.Single(s => s.Name == "Global/History").UnpackedBytes);
        var streamOnePartition = reportOne.Streams.Single(s => s.Name == "Partitions/0");
        Assert.Equal((partitionA.Length, 62L), (streamOnePartition.RawBytes, streamOnePartition.UnpackedBytes));
        Assert.All(reportOne.Streams, s => Assert.True(s.RawBytes > 0 && s.UnpackedBytes > 0));

        byte[] damagedHistory = new byte[512];
        damagedHistory[0] = 0x42;
        string sampleTwoPath = Path.Combine(_folder, "streams-two.rvt");
        CoverageCensusTests.WriteFile(
            sampleTwoPath,
            ("Formats/Latest", PageCodec.EncodePadded(GzipMember.Write(formatsDecoded))),
            ("Global/History", damagedHistory),
            ("Partitions/0", partitionA),
            ("Partitions/1", partitionB));

        var diagTwo = new RvtReadDiagnostics();
        var readerTwo = new RvtFileReader(new RvtReaderOptions { Diagnostics = diagTwo });
        var infoTwo = readerTwo.Read(sampleTwoPath);
        var reportTwo = ExportReport.ForRead("streams-two.rvt", new FileInfo(sampleTwoPath).Length, diagTwo, false, false, [1u], infoTwo);

        Assert.Equal("partial", reportTwo.Status);
        Assert.Equal(formatsDecoded.Length, reportTwo.Streams.Single(s => s.Name == "Formats/Latest").UnpackedBytes);
        Assert.Equal((512L, 0L), (
            reportTwo.Streams.Single(s => s.Name == "Global/History").RawBytes,
            reportTwo.Streams.Single(s => s.Name == "Global/History").UnpackedBytes));
        Assert.Equal((partitionA.Length, 62L), (
            reportTwo.Streams.Single(s => s.Name == "Partitions/0").RawBytes,
            reportTwo.Streams.Single(s => s.Name == "Partitions/0").UnpackedBytes));
        Assert.Equal((partitionB.Length, 124L), (
            reportTwo.Streams.Single(s => s.Name == "Partitions/1").RawBytes,
            reportTwo.Streams.Single(s => s.Name == "Partitions/1").UnpackedBytes));
    }

    private static IEnumerable<string> EnumerateSampleFiles() => TestFiles.Roots()
        .Where(Directory.Exists)
        .SelectMany(root => Directory.EnumerateFiles(
            root,
            "*.rvt",
            new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        .OrderBy(path => new FileInfo(path).Length);

    [SkippableFact]
    public void PublicSamplesReportUnpackedStreamBytesAndConsistentGeometryTotals()
    {
        var samples = EnumerateSampleFiles().Take(2).ToList();
        if (samples.Count == 0)
            throw new SkipException("No sample .rvt files found in RVTIO_TEST_FILES.");

        foreach (string sample in samples)
        {
            var diagnostics = new RvtReadDiagnostics();
            var reader = new RvtFileReader(new RvtReaderOptions { Diagnostics = diagnostics });
            var info = reader.Read(sample);
            var report = ExportReport.ForRead(Path.GetFileName(sample), new FileInfo(sample).Length, diagnostics, false, false,
                reader.LastParsingState?.ElemTable.Keys.ToList() ?? [], info);

            Assert.NotEmpty(report.Streams);
            Assert.All(report.Streams, stream =>
            {
                if (stream.Decoded > 0)
                    Assert.True(stream.UnpackedBytes > 0, $"{stream.Name} decoded {stream.Decoded} members with 0 unpackedBytes.");
                else
                    Assert.Equal(0L, stream.UnpackedBytes);
            });

            string ifcPath = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".ifc");
            var skipLog = new SkipLog();
            var exporter = new IfcExporter { Profile = IfcProfile.Current, SkipLog = skipLog };
            exporter.Export(info, ifcPath);
            report.SetExport("ifc", exporter.ExportedProducts, skipLog, exporter.ExportedElementQualities);
            AssertExportTotalsConsistent(report.Export!);

            report.SetJsonExport(info, full: false);
            AssertExportTotalsConsistent(report.Export!);

            report.SetJsonExport(info, full: true);
            AssertExportTotalsConsistent(report.Export!);
        }
    }

    private delegate void DeconstructStreamRecord7(
        StreamRecord record, out string name, out long rawBytes, out int members, out int decoded, out int failed, out int recovered, out int unverified);

    [Fact]
    public void StreamRecord_PreservesSevenArgumentConstructorAndDeconstructForExistingConsumers()
    {
        var ctor7 = typeof(StreamRecord).GetConstructor(
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            [typeof(string), typeof(long), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int)],
            modifiers: null);
        Assert.NotNull(ctor7);

        var deconstruct7 = typeof(StreamRecord).GetMethod(
            nameof(StreamRecord.Deconstruct),
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            [
                typeof(string).MakeByRefType(),
                typeof(long).MakeByRefType(),
                typeof(int).MakeByRefType(),
                typeof(int).MakeByRefType(),
                typeof(int).MakeByRefType(),
                typeof(int).MakeByRefType(),
                typeof(int).MakeByRefType(),
            ],
            modifiers: null);
        Assert.NotNull(deconstruct7);

        // Simulate a consumer compiled against main's seven-parameter StreamRecord running without recompilation.
        var constructMethod = new DynamicMethod(
            "ConstructStreamRecord7",
            typeof(StreamRecord),
            [typeof(string), typeof(long), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int)],
            typeof(ExportReportGeometryQualityTests).Module,
            skipVisibility: false);
        var ilCtor = constructMethod.GetILGenerator();
        ilCtor.Emit(OpCodes.Ldarg_0);
        ilCtor.Emit(OpCodes.Ldarg_1);
        ilCtor.Emit(OpCodes.Ldarg_2);
        ilCtor.Emit(OpCodes.Ldarg_3);
        ilCtor.Emit(OpCodes.Ldarg_S, (byte)4);
        ilCtor.Emit(OpCodes.Ldarg_S, (byte)5);
        ilCtor.Emit(OpCodes.Ldarg_S, (byte)6);
        ilCtor.Emit(OpCodes.Newobj, ctor7);
        ilCtor.Emit(OpCodes.Ret);
        var legacyConstruct = constructMethod.CreateDelegate<Func<string, long, int, int, int, int, int, StreamRecord>>();

        var deconstructMethod = new DynamicMethod(
            "DeconstructStreamRecord7",
            typeof(void),
            [
                typeof(StreamRecord),
                typeof(string).MakeByRefType(),
                typeof(long).MakeByRefType(),
                typeof(int).MakeByRefType(),
                typeof(int).MakeByRefType(),
                typeof(int).MakeByRefType(),
                typeof(int).MakeByRefType(),
                typeof(int).MakeByRefType(),
            ],
            typeof(ExportReportGeometryQualityTests).Module,
            skipVisibility: false);
        var ilDeconstruct = deconstructMethod.GetILGenerator();
        ilDeconstruct.Emit(OpCodes.Ldarg_0);
        ilDeconstruct.Emit(OpCodes.Ldarg_1);
        ilDeconstruct.Emit(OpCodes.Ldarg_2);
        ilDeconstruct.Emit(OpCodes.Ldarg_3);
        ilDeconstruct.Emit(OpCodes.Ldarg_S, (byte)4);
        ilDeconstruct.Emit(OpCodes.Ldarg_S, (byte)5);
        ilDeconstruct.Emit(OpCodes.Ldarg_S, (byte)6);
        ilDeconstruct.Emit(OpCodes.Ldarg_S, (byte)7);
        ilDeconstruct.Emit(OpCodes.Callvirt, deconstruct7);
        ilDeconstruct.Emit(OpCodes.Ret);
        var legacyDeconstruct = deconstructMethod.CreateDelegate<DeconstructStreamRecord7>();

        var fromLegacyConsumer = legacyConstruct("Partitions/0", 1200L, 4, 3, 1, 1, 1);
        Assert.Equal(0L, fromLegacyConsumer.UnpackedBytes);

        var withUnpacked = new StreamRecord("Partitions/0", 1200L, 4, 3, 1, 1, 1, 8192L);
        legacyDeconstruct(withUnpacked, out string name, out long rawBytes, out int members, out int decoded, out int failed, out int recovered, out int unverified);
        Assert.Equal(("Partitions/0", 1200L, 4, 3, 1, 1, 1), (name, rawBytes, members, decoded, failed, recovered, unverified));

        var sevenArgCSharp = new StreamRecord("Partitions/0", 1200L, 4, 3, 1, 1, 1);
        var (csName, csRaw, csMembers, csDecoded, csFailed, csRecovered, csUnverified) = sevenArgCSharp;
        Assert.Equal(("Partitions/0", 1200L, 4, 3, 1, 1, 1), (csName, csRaw, csMembers, csDecoded, csFailed, csRecovered, csUnverified));
        Assert.Equal(sevenArgCSharp, fromLegacyConsumer);
        Assert.Equal(withUnpacked, sevenArgCSharp with { UnpackedBytes = 8192L });

        var (name8, raw8, members8, decoded8, failed8, recovered8, unverified8, unpacked8) = withUnpacked;
        Assert.Equal(("Partitions/0", 1200L, 4, 3, 1, 1, 1, 8192L), (name8, raw8, members8, decoded8, failed8, recovered8, unverified8, unpacked8));
    }

    [Fact]
    public void OwnSchemaR22Rewrite_ClassifiesExportedJsonGeometryAndGatesUnrewrittenLayout()
    {
        var unresolvedGeom = ClosedBox(6, 10, 0, 3, 0, 2);
        unresolvedGeom.UnresolvedSymbol = true;
        var boxOnlySymbolGeom = ClosedBox(12, 15, 0, 3, 0, 2);
        boxOnlySymbolGeom.PlacedSymbolBox = true;
        var mappedApproxGeom = ClosedBox(16, 20, 0, 3, 0, 2);
        mappedApproxGeom.ExactMappedInstances = [new GeoInstance { SymbolId = 900, Transform = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0] }];

        var mappedSymbol = GenericProduct(900, null);
        mappedSymbol.IsBlockOnly = true;

        var headerOutlineOnly = GenericProduct(104, null);
        var internalRebarOnly = GenericProduct(105, null);
        internalRebarOnly.RebarBars = new RebarBars(
            10,
            -1,
            1.0,
            [[new RebarSegment(false, [0, 0, 0], [1, 0, 0], [0, 1, 0], 0, 0, 1)]]);
        var invalidBoundsOnly = GenericProduct(106, null);
        invalidBoundsOnly.BoundingBox = [5, 0, 0, -1, 1, 1];

        var info = new RevitFileInfo
        {
            ElemTableIsExact = true,
            // Own-schema releases keep GeometryUndecoded=true even after R22Layout rewrites and decodes geometry.
            GeometryUndecoded = true,
            WideLayoutNotRewritten = false,
            Elements =
            [
                GenericProduct(100, ClosedBox(0, 4, 0, 3, 0, 2)),
                GenericProduct(101, unresolvedGeom),
                GenericProduct(102, boxOnlySymbolGeom),
                GenericProduct(103, new GeometryData { BoundingBox = [0, 0, 0, 2, 3, 4] }),
                headerOutlineOnly,
                internalRebarOnly,
                invalidBoundsOnly,
                GenericProduct(107, mappedApproxGeom),
                mappedSymbol,
            ],
        };

        var parsingState = new ParsingState();
        parsingState.HeaderBoundingBoxes[104u] = new ElementHeaderBox(true, [0, 0, 0, 5, 5, 5]);
        ElementBuilder.ApplyHeaderBoundingBoxes(info, parsingState);
        Assert.True(headerOutlineOnly.BoundingBoxIsHeaderOutline);
        Assert.Null(headerOutlineOnly.DecodedBox);

        // Verify what Full JSON actually exposes on disk: public Geometry is serialized, internal RebarBars is not.
        string serializedModel = JsonSerializer.Serialize(info, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        });
        Assert.Contains("\"Faces\"", serializedModel, StringComparison.Ordinal);
        Assert.DoesNotContain("RebarBars", serializedModel, StringComparison.Ordinal);

        var rewrittenReport = ExportReport.ForRead("own-schema-r22.rvt", 4096, new RvtReadDiagnostics(), false, false, [100u], info);
        rewrittenReport.SetJsonExport(info, full: true);
        AssertExportTotalsConsistent(rewrittenReport.Export!);
        Assert.Equal("json-full", rewrittenReport.Export!.Format);
        Assert.Equal((9, 9, 0), (rewrittenReport.Export.Considered, rewrittenReport.Export.Exported, rewrittenReport.Export.Skipped));
        Assert.Equal((1, 2, 2, 4), (
            rewrittenReport.Export.Geometry.Exact,
            rewrittenReport.Export.Geometry.Approximated,
            rewrittenReport.Export.Geometry.BoxOnly,
            rewrittenReport.Export.Geometry.None));
        Assert.Equal(
            [
                (100, "exact"),
                (101, "approximated"),
                (102, "box-only"),
                (103, "box-only"),
                (104, "none"),
                (105, "none"),
                (106, "none"),
                (107, "approximated"),
                (900, "none"),
            ],
            rewrittenReport.Export.Elements.Select(e => (e.Id, e.Quality)).ToArray());

        using var rewrittenJson = JsonDocument.Parse(rewrittenReport.ToJson());
        var geomNode = rewrittenJson.RootElement.GetProperty("export").GetProperty("geometry");
        Assert.Equal(1, geomNode.GetProperty("exact").GetInt32());
        Assert.Equal(2, geomNode.GetProperty("approximated").GetInt32());
        Assert.Equal(2, geomNode.GetProperty("boxOnly").GetInt32());
        Assert.Equal(4, geomNode.GetProperty("none").GetInt32());

        rewrittenReport.SetJsonExport(info, full: false);
        AssertExportTotalsConsistent(rewrittenReport.Export!);
        Assert.Equal("json", rewrittenReport.Export.Format);
        Assert.Equal((9, 8, 1), (rewrittenReport.Export.Considered, rewrittenReport.Export.Exported, rewrittenReport.Export.Skipped));
        Assert.Equal((1, 2, 2, 3), (
            rewrittenReport.Export.Geometry.Exact,
            rewrittenReport.Export.Geometry.Approximated,
            rewrittenReport.Export.Geometry.BoxOnly,
            rewrittenReport.Export.Geometry.None));

        // When the same own-schema file was not rewritten into the R22 layout, JSON export marks every row none.
        info.WideLayoutNotRewritten = true;
        var unrewrittenReport = ExportReport.ForRead("own-schema-plain.rvt", 4096, new RvtReadDiagnostics(), false, false, [100u], info);
        unrewrittenReport.SetJsonExport(info, full: true);
        AssertExportTotalsConsistent(unrewrittenReport.Export!);
        Assert.Equal((0, 0, 0, 9), (
            unrewrittenReport.Export!.Geometry.Exact,
            unrewrittenReport.Export.Geometry.Approximated,
            unrewrittenReport.Export.Geometry.BoxOnly,
            unrewrittenReport.Export.Geometry.None));

        unrewrittenReport.SetJsonExport(info, full: false);
        AssertExportTotalsConsistent(unrewrittenReport.Export!);
        Assert.Equal((0, 0, 0, 8), (
            unrewrittenReport.Export.Geometry.Exact,
            unrewrittenReport.Export.Geometry.Approximated,
            unrewrittenReport.Export.Geometry.BoxOnly,
            unrewrittenReport.Export.Geometry.None));
    }

    [SkippableFact]
    public void OwnSchemaSampleWithR22Rewrite_ClassifiesDecodedFacesInFullJsonReport()
    {
        string? ownSchemaSample = EnumerateSampleFiles()
            .FirstOrDefault(path =>
            {
                try
                {
                    using var root = RootStorage.OpenRead(path);
                    return FileSchema.Read(root) is { Release: >= 2020 and not 2022 };
                }
                catch (Exception)
                {
                    return false;
                }
            });
        if (ownSchemaSample == null)
            throw new SkipException("No own-schema sample .rvt files found in RVTIO_TEST_FILES.");

        var plainDiagnostics = new RvtReadDiagnostics();
        var plainReader = new RvtFileReader(new RvtReaderOptions { Diagnostics = plainDiagnostics });
        var plainInfo = plainReader.Read(ownSchemaSample);
        Assert.True(plainInfo.GeometryUndecoded);
        Assert.True(plainInfo.WideLayoutNotRewritten);

        var plainReport = ExportReport.ForRead(Path.GetFileName(ownSchemaSample), new FileInfo(ownSchemaSample).Length,
            plainDiagnostics, false, false, plainReader.LastParsingState?.ElemTable.Keys.ToList() ?? [], plainInfo);
        plainReport.SetJsonExport(plainInfo, full: true);
        AssertExportTotalsConsistent(plainReport.Export!);
        Assert.Equal(plainReport.Export!.Exported, plainReport.Export.Geometry.None);

        var r22Diagnostics = new RvtReadDiagnostics();
        var r22Reader = new RvtFileReader(new RvtReaderOptions { R22Layout = true, Diagnostics = r22Diagnostics });
        var r22Info = r22Reader.Read(ownSchemaSample);
        Assert.True(r22Info.GeometryUndecoded);
        Assert.False(r22Info.WideLayoutNotRewritten);

        var r22Report = ExportReport.ForRead(Path.GetFileName(ownSchemaSample), new FileInfo(ownSchemaSample).Length,
            r22Diagnostics, false, false, r22Reader.LastParsingState?.ElemTable.Keys.ToList() ?? [], r22Info);
        r22Report.SetJsonExport(r22Info, full: true);
        AssertExportTotalsConsistent(r22Report.Export!);

        Assert.Equal(r22Info.Elements.Count, r22Report.Export!.Elements.Count);
        int elementsWithFaces = 0;
        for (int i = 0; i < r22Info.Elements.Count; i++)
        {
            var element = r22Info.Elements[i];
            if (element.Geometry?.Faces.Any(f => f.OuterVertices is { Count: >= 3 }) == true)
            {
                elementsWithFaces++;
                Assert.Equal(element.Id, r22Report.Export.Elements[i].Id);
                Assert.NotEqual("none", r22Report.Export.Elements[i].Quality);
            }
        }
        Assert.True(elementsWithFaces > 0, "Expected at least one element with decoded faces after R22 rewrite.");
        Assert.True(r22Report.Export.Geometry.Exact + r22Report.Export.Geometry.Approximated + r22Report.Export.Geometry.BoxOnly >= elementsWithFaces);
    }
}
