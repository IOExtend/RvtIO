using System.Buffers.Binary;
using System.Text;
using OpenMcdf;
using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;

namespace RvtIO.Tests;

public class SpatialPhaseTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Decoder_PreservesAuthoredOrder(int prefix)
    {
        byte[] block = Block([90, 7, 31]);
        byte[] wrapped = [..new byte[prefix], ..block, 0, 0];
        Assert.True(TfBlockDeserializer.TryExtractExactProjectPhaseOrder(
            wrapped, prefix, Size(block), 187, out var phases));
        Assert.Equal(new[] { 90, 7, 31 }, phases);
    }

    [Fact]
    public void Decoder_AcceptsPhaseIdZero()
    {
        // Older projects keep phases with ids 0 and 2 (advanced structural sample project; the Revit API reports Phase Created = 0).
        byte[] block = Block([2, 0]);
        Assert.True(TfBlockDeserializer.TryExtractExactProjectPhaseOrder(block, 0, Size(block), 187, out var phases));
        Assert.Equal(new[] { 2, 0 }, phases);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("invalid")]
    [InlineData("owner")]
    [InlineData("class")]
    [InlineData("footer")]
    [InlineData("size")]
    [InlineData("truncated")]
    [InlineData("tail")]
    [InlineData("unknown-tail")]
    [InlineData("count")]
    public void Decoder_RejectsIncompleteOrAmbiguousSource(string mutation)
    {
        byte[] block = Block(mutation switch
        {
            "empty" => [], "duplicate" => [3, 3], "invalid" => [3, -1], _ => [90, 7, 31],
        }, mutation);
        uint size = Size(block);
        switch (mutation)
        {
            case "owner": BitConverter.GetBytes(2).CopyTo(block, 0); break;
            case "class": BitConverter.GetBytes((ushort)3065).CopyTo(block, 12); break;
            case "footer": block[^1] ^= 1; break;
            case "size": size++; break;
            case "truncated": block = block[..^1]; break;
        }
        Assert.False(TfBlockDeserializer.TryExtractExactProjectPhaseOrder(block, 0, size, 187, out var phases));
        Assert.Null(phases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recorder_DuplicatesNeverRestoreOrder(bool malformedFirst)
    {
        byte[] block = Block([90, 7, 31]);
        var state = new ParsingState();
        RvtFileReader.RecordProjectPhaseOrderSource(state, malformedFirst ? block[..^1] : block,
            0, Size(block), 187, 1);
        RvtFileReader.RecordProjectPhaseOrderSource(state, block, 0, Size(block), 187, 1);
        RvtFileReader.RecordProjectPhaseOrderSource(state, block, 0, Size(block), 187, 1);
        Assert.Equal(3, state.ProjectPhaseOrderSource.BlockCount);
        Assert.Null(state.ProjectPhaseOrderSource.PhaseIds);
    }

    [Theory]
    [InlineData("valid", 31)]
    [InlineData("missing", null)]
    [InlineData("duplicate", null)]
    [InlineData("wrong-class", null)]
    [InlineData("duplicate-id", null)]
    [InlineData("empty", null)]
    public void Builder_UsesLastResolvedSourcePhase(string mutation, int? expected)
    {
        var state = new ParsingState { ProjectPhaseOrderSource = (1, [90, 7, 31]) };
        foreach (uint id in new uint[] { 90, 7, 31 }) state.ElemClassIdx[id] = 3065;
        switch (mutation)
        {
            case "missing": state.ElemClassIdx.Remove(7); break;
            case "duplicate": state.ProjectPhaseOrderSource = (2, [90, 7, 31]); break;
            case "wrong-class": state.ElemClassIdx[31] = 3471; break;
            case "duplicate-id": state.ProjectPhaseOrderSource = (1, [90, 31, 31]); break;
            case "empty": state.ProjectPhaseOrderSource = (1, []); break;
        }
        var room = new Element { Id = 100, CategoryId = -2000160 };
        var area = new Element { Id = 101, CategoryId = -2003200 };
        var duplicate = new Element { Id = 102, CategoryId = -2000160 };
        var info = new RevitFileInfo { Elements = [room, area, duplicate], SerializedDefaultExportPhaseId = 999 };
        foreach (uint id in new uint[] { 100, 101, 102 })
        {
            state.RoomNameSource[id] = (id == 102 ? 2 : 1, "Room");
            state.ExactRoomPhaseIds[id] = 7;
        }
        ElementBuilder.ApplySerializedSpatialPhases(info, state);
        Assert.Equal(expected, info.SerializedDefaultExportPhaseId);
        Assert.Equal(7, room.SerializedRoomPhaseId);
        Assert.Null(area.SerializedRoomPhaseId);
        Assert.Null(duplicate.SerializedRoomPhaseId);
    }

    [Theory]
    [InlineData(true, 0, 0)]
    [InlineData(true, 7, 7)]
    [InlineData(false, 0, null)]
    public void Builder_ReadsPhaseZeroOfARoomOnlyInAWideIdFile(bool wide, int phase, int? expected)
    {
        var state = new ParsingState { WideSchema = wide ? new FileSchema(new SchemaDocument()) : null };
        var room = new Element { Id = 100, CategoryId = -2000160 };
        var info = new RevitFileInfo { Elements = [room] };
        state.RoomNameSource[100] = (1, "Room");
        state.ExactRoomPhaseIds[100] = phase;
        ElementBuilder.ApplySerializedSpatialPhases(info, state);
        Assert.Equal(expected, room.SerializedRoomPhaseId);
    }

    [Fact]
    public void Builder_AddsTheRoomPhaseParametersOfPhaseZeroFromTheRoomRecord()
    {
        var state = new ParsingState { WideSchema = new FileSchema(new SchemaDocument()) };
        var room = new Element { Id = 100, CategoryId = -2000160 };
        var info = new RevitFileInfo { Elements = [room] };
        state.RoomNameSource[100] = (1, "Room");
        state.ExactRoomPhaseIds[100] = 0;
        ElementBuilder.ApplySerializedSpatialPhases(info, state);
        ElementBuilder.ApplyRoomParams(info);
        Assert.Equal(0, room.Parameters.Single(p => p.Id == -1012113).Value);
        Assert.Equal(0, room.Parameters.Single(p => p.Id == -1012112).Value);
    }

    [Fact]
    public void Builder_TakesTheRoomPhaseParametersFromThePhaseReferenceWhenItResolves()
    {
        var state = new ParsingState { WideSchema = new FileSchema(new SchemaDocument()) };
        var room = new Element { Id = 100, CategoryId = -2000160 };
        room.Parameters.Add(new Parameter { Id = -1012100, Value = 7, StorageType = StorageType.ElementId });
        var info = new RevitFileInfo { Elements = [room] };
        state.RoomNameSource[100] = (1, "Room");
        state.ExactRoomPhaseIds[100] = 0;
        ElementBuilder.ApplySerializedSpatialPhases(info, state);
        ElementBuilder.ApplyRoomParams(info);
        Assert.Equal(7, room.Parameters.Single(p => p.Id == -1012113).Value);
        Assert.Equal(7, room.Parameters.Single(p => p.Id == -1012112).Value);
    }

    [Fact]
    public void Builder_AddsNoRoomPhaseParametersWhenNoPhaseIsKnown()
    {
        // A room of a Revit 2022 layout file whose record names no phase keeps neither parameter.
        var room = new Element { Id = 100, CategoryId = -2000160 };
        var info = new RevitFileInfo { Elements = [room] };
        ElementBuilder.ApplyRoomParams(info);
        Assert.Empty(room.Parameters);
    }

    [Fact]
    public void RunTfStreamWalk_InFramedFileIgnoresFamilyPhaseOrderBlocks()
    {
        byte[] project = Block([10, 20]);
        byte[] family = Block([99]);
        BitConverter.GetBytes(2u).CopyTo(family, 0);
        byte[] partition = [.. project, .. family];
        var state = new ParsingState
        {
            AllKnownIds = [1],
            Chunks = [partition],
            PartitionChunkCount = 1,
            FramedPartitions = [new PartitionFraming.Framed([partition], [new(102, 1, 0, project.Length)], 4, Partition: 0)],
        };

        RvtFileReader.RunTfStreamWalk(state);

        Assert.Equal(1, state.ProjectPhaseOrderSource.BlockCount);
        Assert.Equal([10, 20], state.ProjectPhaseOrderSource.PhaseIds);
    }

    [Fact]
    public void RecordFramedRoots_RoutesDuplicatePhaseOrderBlocksViaIncrementRoutedPartitions()
    {
        byte[] stale = Block([10]);
        byte[] current = Block([10, 20]);
        var state = new ParsingState
        {
            AllKnownIds = [1],
            IncrementRoutedPartitions = new Dictionary<ulong, int> { [1] = 1 },
            FramedPartitions =
            [
                new PartitionFraming.Framed([stale], [new(102, 1, 0, stale.Length)], 4, Partition: 0),
                new PartitionFraming.Framed([current], [new(102, 1, 0, current.Length)], 4, Partition: 1),
            ],
        };

        RvtFileReader.RecordFramedRoots(state);

        Assert.Equal(1, state.ProjectPhaseOrderSource.BlockCount);
        Assert.Equal([10, 20], state.ProjectPhaseOrderSource.PhaseIds);
    }

    [Fact]
    public void Builder_ResolvesThirtyTwoBitProjectPhaseWithIdZeroAndExplicitFilter()
    {
        byte[] phases = Block([2, 0]);
        var state = new ParsingState { AllKnownIds = [1, 2, 0] };
        state.ElemClassIdx[1] = 187;
        state.ElemClassIdx[2] = 3065;
        state.ElemClassIdx[0] = 3065;
        RvtFileReader.RecordProjectPhaseOrderSource(state, phases, 0, Size(phases), 187, 1);

        var phaseZero = new Element { Id = 0, CategoryId = -2000112, Category = "OST_Phases", Name = "New Construction" };
        var phaseExisting = new Element { Id = 2, CategoryId = -2000112, Category = "OST_Phases", Name = "Existing" };
        var wallCreatedInZero = new Element { Id = 10, CategoryId = -2000011, SerializedCreatedPhaseId = 0 };
        var wallDemolishedInTwo = new Element { Id = 11, CategoryId = -2000011, SerializedCreatedPhaseId = 2, SerializedDemolishedPhaseId = 2 };
        var info = new RevitFileInfo { Elements = [phaseZero, phaseExisting, wallCreatedInZero, wallDemolishedInTwo] };

        ElementBuilder.ApplySerializedSpatialPhases(info, state);

        Assert.Equal([2, 0], info.SerializedProjectPhaseIds);
        Assert.Equal(0, info.SerializedDefaultExportPhaseId);
        Assert.Equal(0, ProductRules.LastProjectPhase(info));

        var phaseZeroFilter = ProductRules.BuildExportPhaseFilter(info, exportPhaseId: 0, lastPhaseByDefault: false)!;
        Assert.False(phaseZeroFilter.Excludes(wallCreatedInZero));
        Assert.True(phaseZeroFilter.Excludes(wallDemolishedInTwo));

        var phaseTwoFilter = ProductRules.BuildExportPhaseFilter(info, exportPhaseId: 2, lastPhaseByDefault: false)!;
        Assert.True(phaseTwoFilter.Excludes(wallCreatedInZero));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeR22Reader_AndDefaultCurrentIfcExport_RetainPhaseZeroAndExportPhaseZeroProducts(bool releaseExportPartitions)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "rvtio_r22_phase0_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string rvtPath = Path.Combine(tempDir, "native_r22_phase_zero.rvt");
            string ifcPath = Path.Combine(tempDir, "native_r22_phase_zero.ifc");
            string legacyIfcPath = Path.Combine(tempDir, "native_r22_phase_zero_legacy.ifc");

            WriteSyntheticNativeR22ModelWithPhaseZero(rvtPath);

            using (var root = RootStorage.OpenRead(rvtPath))
            {
                var formats = FileSchema.Read(root);
                Assert.NotNull(formats);
                Assert.Equal(2022, formats!.Release);
                Assert.Equal(
                    new uint[] { 0u, 1u, 2u, 10u, 20u, 21u },
                    ElemTableReader.Read(root).Keys.Order().ToArray());
            }

            var reader = new RvtFileReader { ReleaseExportPartitions = releaseExportPartitions };
            var info = reader.Read(rvtPath);

            Assert.Null(reader.LastParsingState!.WideSchema);
            Assert.True(info.ElemTableIsExact);

            var phaseZero = Assert.Single(info.Elements, e => e.Id == 0);
            Assert.Equal(-2000112, phaseZero.CategoryId);
            Assert.Equal("OST_Phases", phaseZero.Category);
            Assert.Equal("New Construction", phaseZero.Name);
            Assert.NotNull(phaseZero.SerializedExportId);
            Assert.False(string.IsNullOrEmpty(phaseZero.UniqueId));

            Assert.Equal([2, 0], info.SerializedProjectPhaseIds);
            Assert.Equal(0, info.SerializedDefaultExportPhaseId);
            Assert.Equal(0, ProductRules.LastProjectPhase(info));

            var floor20 = Assert.Single(info.Elements, e => e.Id == 20);
            Assert.Equal(-2000032, floor20.CategoryId);
            Assert.Equal(0, floor20.SerializedCreatedPhaseId);
            var phaseParameter = floor20.Parameters.Single(p => p.Id == -1012100);
            Assert.Equal(0, phaseParameter.Value);
            Assert.True(phaseParameter.HasResolvedZeroElementId);
            Assert.NotNull(floor20.SerializedExportId);
            Assert.NotNull(floor20.Geometry);

            var floor21 = Assert.Single(info.Elements, e => e.Id == 21);
            Assert.NotNull(floor21.SerializedExportId);

            new IfcExporter { Profile = IfcProfile.Current }.Export(info, ifcPath);
            string ifc = File.ReadAllText(ifcPath);
            string floor20Guid = IfcGuid.ToIfcGuid(floor20.SerializedExportId!.Value);
            string floor21Guid = IfcGuid.ToIfcGuid(floor21.SerializedExportId!.Value);
            Assert.Contains($"IFCSLAB('{floor20Guid}'", ifc);
            Assert.Contains("IFCBUILDINGSTOREY(", ifc);
            Assert.DoesNotContain(floor21Guid, ifc);

            new IfcExporter { Profile = IfcProfile.Legacy }.Export(info, legacyIfcPath);
            string legacyIfc = File.ReadAllText(legacyIfcPath);
            Assert.Contains($"IFCSLAB('{floor20Guid}'", legacyIfc);
            Assert.Contains($"IFCSLAB('{floor21Guid}'", legacyIfc);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Builder_ApplyNativeParamsEmitsPhaseZeroCreatedAndDemolishedOnNativeR22NonListedClasses()
    {
        var nonListedElem = new Element { Id = 50, CategoryId = -2000151 };
        var info = new RevitFileInfo { Elements = [nonListedElem] };
        var state = new ParsingState();
        state.ElemClassIdx[0] = 3065;
        state.ElemClassIdx[50] = 1794;
        var np = new TfBlockDeserializer.NativeParams();
        np.Fields["m_createdPhaseId"] = 0;
        np.Fields["m_demolishedPhaseId"] = 0;
        state.NativeParams[50] = np;

        ElementBuilder.ApplyNativeParams(info, state);

        Assert.Equal(0, nonListedElem.SerializedCreatedPhaseId);
        Assert.Equal(0, nonListedElem.SerializedDemolishedPhaseId);
        Assert.Equal(0, nonListedElem.Parameters.Single(p => p.Id == -1012100).Value);
        Assert.Equal(0, nonListedElem.Parameters.Single(p => p.Id == -1012101).Value);
    }

    internal static void WriteSyntheticNativeR22ModelWithPhaseZero(string rvtPath)
    {
        uint[] ids = [0u, 1u, 2u, 10u, 20u, 21u];

        using var root = RootStorage.Create(rvtPath, OpenMcdf.Version.V3);
        using (var bfi = root.CreateStream("BasicFileInfo"))
            bfi.Write(RvtWriterTests.BuildBasicFileInfoBytes(2022, lastSavePath: @"C:\Models\SyntheticR22.rvt"));
        using (var pi = root.CreateStream("ProjectInformation"))
            pi.Write([1, 2, 3, 4, 5, 6, 7, 8]);

        byte[] schemaBytes = RvtWriterTests.BuildSchemaBytesForRelease(2022, includeFramingMarkers: true);
        var tags = PartitionFraming.TagsOf(new FormatsSchema(TfSchemaReader.Parse(schemaBytes)))!.Value;
        var formats = root.CreateStorage("Formats");
        using (var latest = formats.CreateStream("Latest"))
            latest.Write(PagedStreamCodec.Encode([], schemaBytes));

        byte[] elemTableBytes = new byte[6 + ids.Length * ElementExportIds.ElemRecLength];
        BinaryPrimitives.WriteUInt16LittleEndian(elemTableBytes.AsSpan(0, 2), ElementExportIds.ElemTableClassId);
        BinaryPrimitives.WriteInt32LittleEndian(elemTableBytes.AsSpan(2, 4), ids.Length);
        for (int i = 0; i < ids.Length; i++)
        {
            int offset = 6 + i * ElementExportIds.ElemRecLength;
            uint id = ids[i];
            BinaryPrimitives.WriteUInt32LittleEndian(elemTableBytes.AsSpan(offset, 4), id);
            BinaryPrimitives.WriteUInt32LittleEndian(elemTableBytes.AsSpan(offset + 4, 4), id + 1000u);
            BinaryPrimitives.WriteInt32LittleEndian(elemTableBytes.AsSpan(offset + 24, 4), -1);
        }

        byte[] historyBytes = new byte[127];
        new byte[] { 0xA2, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00 }.CopyTo(historyBytes, 0);
        Guid.Parse("11111111-2222-3333-4444-555555555555").ToByteArray().CopyTo(historyBytes, 38);
        BinaryPrimitives.WriteUInt32LittleEndian(historyBytes.AsSpan(106, 4), 1u);
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee").ToByteArray().CopyTo(historyBytes, 110);
        historyBytes[126] = 1;

        var global = root.CreateStorage("Global");
        using (var elemTable = global.CreateStream("ElemTable"))
            elemTable.Write(PagedStreamCodec.Encode([5, 0, 0, 0, 0, 0, 0, 0], elemTableBytes));
        using (var history = global.CreateStream("History"))
            history.Write(PagedStreamCodec.Encode([5, 0, 0, 0, 0, 0, 0, 0], historyBytes));

        var ch101 = ids
            .Select(id => new PartitionStreamEditorTests.SpecRecord(
                id,
                PartitionStreamEditorTests.TaggedBody(1, [(byte)(id & 0xFF)])))
            .Append(new PartitionStreamEditorTests.SpecRecord(uint.MaxValue, []))
            .ToArray();
        var ch102 = new PartitionStreamEditorTests.SpecRecord[]
        {
            new(0u, BuildR22RootBody(3065, elemId: 0, name: "New Construction")),
            new(1u, Block([2, 0])[12..^4]),
            new(2u, BuildR22RootBody(3065, elemId: 2, name: "Existing")),
            new(10u, BuildR22RootBody(2299, elemId: 10, name: "Level 1")),
            new(20u, BuildR22RootBody(1913, elemId: 20, assocLevelId: 10, createdPhaseId: 0, demolishedPhaseId: -1)),
            new(21u, BuildR22RootBody(1913, elemId: 21, assocLevelId: 10, createdPhaseId: 2, demolishedPhaseId: 2)),
            new(uint.MaxValue, []),
        };
        byte[] grepBody = Convert.FromBase64String(WallFaceLoopTests.Fixture)[12..^4];
        var ch103 = new PartitionStreamEditorTests.SpecRecord[]
        {
            new(20u, grepBody),
            new(21u, grepBody),
            new(uint.MaxValue, []),
        };

        byte[] partitionBytes = PartitionStreamEditorTests.BuildPartitionStream(
            4,
            tags,
            new PartitionStreamEditorTests.SpecBlock(
                Keyed: false,
                Chains:
                [
                    new PartitionStreamEditorTests.SpecChain(101, ch101),
                    new PartitionStreamEditorTests.SpecChain(102, ch102),
                    new PartitionStreamEditorTests.SpecChain(103, ch103),
                ]));

        var partitions = root.CreateStorage("Partitions");
        using (var part1 = partitions.CreateStream("1"))
            part1.Write(partitionBytes);
    }

    private static byte[] BuildR22RootBody(
        ushort classId,
        int elemId,
        int assocLevelId = -1,
        int createdPhaseId = -1,
        int demolishedPhaseId = -1,
        string? name = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(classId);
        foreach (var field in TfBlockDeserializer.SerializationFields(classId)!)
        {
            if (field is { Declaring: 1052, Name: "m_refPointsForNewViews" })
            {
                writer.Write(new byte[2 * 3 * 8]);
                continue;
            }
            if (field.Type == 8 && field.Mods == 96)
            {
                string text = (field.Declaring, field.Name) switch
                {
                    (3065, "m_name") => name ?? "",
                    (1052, "m_text") => name ?? "",
                    _ => "",
                };
                writer.Write((uint)text.Length);
                writer.Write(Encoding.Unicode.GetBytes(text));
                continue;
            }
            if ((field.Mods >> 4) == 5)
            {
                writer.Write(0u);
                continue;
            }
            int repeat = (field.Mods >> 4) == 1 ? field.Cnt!.Value : 1;
            for (int i = 0; i < repeat; i++)
            {
                switch (field.Type)
                {
                    case 1:
                        writer.Write((byte)(field.Name == "m_isBuildingStory" ? 1 : 0));
                        break;
                    case 4:
                        writer.Write(0);
                        break;
                    case 7:
                        writer.Write(0.0);
                        break;
                    case 14 when (field.Mods & 0x0F) is 1 or 2:
                        if (classId == 2299 && field is { Declaring: 1052, Name: "m_pSurface" })
                        {
                            writer.Write(1u);
                            writer.Write((ushort)522);
                        }
                        else
                        {
                            writer.Write(0u);
                        }
                        break;
                    case 14 when field.Name == "m_docAccess":
                        writer.Write(1u);
                        break;
                    case 14 when field.Ref == 20:
                        writer.Write(field.Name switch
                        {
                            "m_id" => elemId,
                            "m_assocLevelId" => assocLevelId,
                            "m_createdPhaseId" => createdPhaseId,
                            "m_demolishedPhaseId" => demolishedPhaseId,
                            _ => -1,
                        });
                        break;
                    case 14:
                        writer.Write(0u);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported field {field.Declaring}:{field.Name} t={field.Type} m={field.Mods}");
                }
            }
        }
        if (classId == 2299)
        {
            writer.Write(-100.0);
            writer.Write(-100.0);
            writer.Write(100.0);
            writer.Write(100.0);
            writer.Write((byte)1);
            writer.Write(0.0); writer.Write(0.0); writer.Write(0.0);
            writer.Write(1.0); writer.Write(0.0); writer.Write(0.0);
            writer.Write(0.0); writer.Write(1.0); writer.Write(0.0);
        }
        return stream.ToArray();
    }

    private static uint Size(byte[] block) => BitConverter.ToUInt32(block, 8);

    private static byte[] Block(int[] phases, string? mutation = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(1u); writer.Write(0u); writer.Write(0u); writer.Write((ushort)187);
        foreach (var field in TfBlockDeserializer.SerializationFields(187)!)
        {
            if (field.Name == "m_phaseIds")
            {
                writer.Write(mutation == "count" ? uint.MaxValue : (uint)phases.Length);
                foreach (int phase in phases) writer.Write(phase);
            }
            else if (field.Name == "m_cellList" && mutation == "unknown-tail")
            {
                writer.Write(uint.MaxValue); writer.Write(ushort.MaxValue);
            }
            else if (field.Name == "m_id") writer.Write(1);
            else if (field.Type == 1) writer.Write((byte)0);
            else
            {
                Assert.Equal((byte)14, field.Type);
                for (int i = 0; i < ((field.Mods >> 4) == 1 ? field.Cnt!.Value : 1); i++) writer.Write(0u);
            }
        }
        if (mutation == "tail") writer.Write(1234u);
        writer.Write(0u);
        byte[] block = stream.ToArray();
        uint size = (uint)block.Length - 16;
        BitConverter.GetBytes(size).CopyTo(block, 8);
        BitConverter.GetBytes(size).CopyTo(block, block.Length - 4);
        return block;
    }
}
