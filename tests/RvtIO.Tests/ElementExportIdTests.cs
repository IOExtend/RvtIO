using System.IO.Compression;
using OpenMcdf;
using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>
/// Element export ids: the exact paged decode of the Global streams, the ElemRec table, and the export id / UniqueId / sub-element formulas.
/// The expected GlobalIds come from IFC exports of the same models. The reader test lives in <see cref="ApiTests"/>, which already holds the parsed TESTFILE.
/// </summary>
public class ElementExportIdTests
{

    [Theory]
    // TESTFILE wall 263584 (episode 7 of 529).
    [InlineData("d45b1816-cbcc-4dc6-b0bb-007467f3c600", 263584u, "3KMnWMoynDnh2x07HdzyEW")]
    // Test_2022 roof 724430: the IfcRoof carries the export id.
    [InlineData("1551350c-45e5-41b8-bdf1-bcfa6794dc6a", 724430u, "0LKJKCHUL1kBtnlFfddz6a")]
    // sample project A wall 6900242 was renumbered: its UniqueId keeps the original id 6879768.
    [InlineData("6a7c3860-b4f0-401b-a699-81014e2efddb", 6879768u, "1gV3XWjF106wQPWG5EHWV3")]
    public void ExportId_XorsTheOriginalElementIdIntoTheEpisodeGuid(
        string episode, uint originalElementId, string expectedGlobalId)
    {
        Guid exportId = ElementExportIds.ExportId(Guid.Parse(episode), originalElementId);
        Assert.Equal(expectedGlobalId, IfcGuid.ToIfcGuid(exportId));
    }

    [Fact]
    public void ExportId_OfTheCurrentIdOfARenumberedElementIsNotTheReferenceGlobalId()
    {
        Guid exportId = ElementExportIds.ExportId(Guid.Parse("6a7c3860-b4f0-401b-a699-81014e2efddb"), 6900242u);
        Assert.NotEqual("1gV3XWjF106wQPWG5EHWV3", IfcGuid.ToIfcGuid(exportId));
    }

    [Theory]
    // Test_2022 roof 724430: its aggregated IfcSlab is sub-element 2 of the roof.
    [InlineData("1551350c-45e5-41b8-bdf1-bcfa6794dc6a", 724430u, 2, "0LKJKCHUL1kBtnlFXddz6a")]
    // Test_2022 IfcBuildingElementProxyType 628357 is InstanceAsType (2048) of instance 628359.
    [InlineData("5b7ac240-e9a7-43b2-b6bf-246956549a40", 628359u, 2048, "1RUi90wQT3ihQ$B6bMNGp7")]
    public void SubElementId_XorsTheIndexIntoBits32To63(
        string episode, uint originalElementId, int index, string expectedGlobalId)
    {
        Guid exportId = ElementExportIds.ExportId(Guid.Parse(episode), originalElementId);
        Assert.Equal(expectedGlobalId, IfcGuid.ToIfcGuid(ElementExportIds.SubElementId(exportId, index)));
    }

    [Fact]
    public void UniqueId_IsTheEpisodeGuidAndTheOriginalIdInHex()
    {
        Assert.Equal("d45b1816-cbcc-4dc6-b0bb-007467f3c600-000405a0",
            ElementExportIds.UniqueId(Guid.Parse("d45b1816-cbcc-4dc6-b0bb-007467f3c600"), 263584u));
    }

    [Fact]
    public void BuildUniqueId_IsNullForAnElementWithoutAnEpisodeOfItsOwn()
    {
        // The episode list of the file, a UniqueId of another element in the element's own strings, and no ElemRec row for
        // element 7: none of them says which episode created element 7, so it gets no UniqueId rather than a wrong one.
        const string otherEpisode = "aaaaaaaa-1111-2222-3333-444455556666";
        var state = new ParsingState
        {
            EpisodeGuids = [otherEpisode, "bbbbbbbb-1111-2222-3333-444455556666"],
            HistoryEpisodeGuids = [otherEpisode, "bbbbbbbb-1111-2222-3333-444455556666"],
            EpisodeTableX = 2,
            ElemRecHistories = new() { [5] = new ElemRecHistory(5, 0) },
            ElemStrings = new() { [7] = [$"{otherEpisode}-00000005"] },
        };

        Assert.Null(ElementBuilder.BuildUniqueId(7, state));
    }

    [Fact]
    public void BuildUniqueId_TakesTheEpisodeAnExternalDumpGivesTheElement()
    {
        var state = new ParsingState
        {
            EpisodeGuids = ["aaaaaaaa-1111-2222-3333-444455556666", "bbbbbbbb-1111-2222-3333-444455556666"],
            ElemEpisodeNumbers = new() { [7] = 1, [8] = 2 },
        };

        Assert.Equal("bbbbbbbb-1111-2222-3333-444455556666-00000007", ElementBuilder.BuildUniqueId(7, state));
        Assert.Null(ElementBuilder.BuildUniqueId(8, state)); // index beyond the dump's list
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ApplyExportIds_GivesTheUniqueIdOfTheElementHistoryAndNoneWithout(bool keepExportProvenance)
    {
        var episodes = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var state = new ParsingState
        {
            KeepExportProvenance = keepExportProvenance,
            HistoryEpisodeGuids = episodes.Select(g => g.ToString("D")).ToList(),
            EpisodeTableX = 3,
            // 5: created in the last episode; 6: renumbered from 4 in the first; 9: a creation date beyond the list.
            ElemRecHistories = new() { [5] = new(5, 0), [6] = new(4, 2), [9] = new(9, 3), [13] = new(-1, 0) },
        };
        var created = new Element { Id = 5 };
        var renumbered = new Element { Id = 6, UniqueId = $"{episodes[0]:D}-00000006" };
        var outsideList = new Element { Id = 9 };
        var withoutRow = new Element { Id = 12 };
        var invalidOriginal = new Element { Id = 13 };

        var (applied, replaced) = ElementBuilder.ApplyExportIds([created, renumbered, outsideList, withoutRow, invalidOriginal], state);

        Assert.Equal((2, 1), (applied, replaced));
        Assert.Equal($"{episodes[2]:D}-00000005", created.UniqueId);
        Assert.Equal(ElementExportIds.ExportId(episodes[2], 5), created.SerializedExportId);
        Assert.Equal($"{episodes[0]:D}-00000004", renumbered.UniqueId);
        Assert.Equal(ElementExportIds.ExportId(episodes[0], 6), renumbered.SerializedHandleExportId);
        Assert.Null(outsideList.UniqueId);
        Assert.Null(outsideList.SerializedExportId);
        Assert.Null(withoutRow.UniqueId);
        Assert.Null(withoutRow.SerializedExportId);
        Assert.Equal(-1L, invalidOriginal.OriginalElementId);
        Assert.Null(invalidOriginal.UniqueId);
        Assert.Null(invalidOriginal.SerializedExportId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ApplyExportIds_MatchesRevit2027ForAWallAboveUInt32(bool keepExportProvenance)
    {
        // A generated two-wall model reopened in Revit 2027.2 and exported to IFC2x3.
        const long elementId = 4_295_967_297;
        const int key = int.MaxValue;
        var state = new ParsingState
        {
            KeepExportProvenance = keepExportProvenance,
            HistoryEpisodeGuids = ["95db5b0a-a7bf-4747-af03-b1f5fe59fca1"],
            EpisodeTableX = 1,
            ElemRecHistories = new() { [key] = new(elementId, 0) },
        };
        var wall = new Element { Id = key, NativeId = elementId };

        Assert.Equal((1, 0), ElementBuilder.ApplyExportIds([wall], state));

        Assert.Equal(elementId, wall.OriginalElementId);
        Assert.Equal("95db5b0a-a7bf-4747-af03-b1f5fe59fca1-1000f4241", wall.UniqueId);
        Assert.Equal(Guid.Parse("95db5b0a-a7bf-4747-af03-b1f4fe56bee0"), wall.SerializedExportId);
        Assert.Equal(wall.SerializedExportId, wall.SerializedHandleExportId);
        Assert.Equal("2LsriAfxz7Hwy3iVJ_LhxW", IfcExporter.ResolveExactElementGlobalId(wall, new HashSet<Guid>()));

        state.ElemRecHistories!.Clear();
        Assert.Equal((0, 0), ElementBuilder.ApplyExportIds([wall], state));
        Assert.Null(wall.SerializedExportId);
        Assert.Equal("95db5b0a-a7bf-4747-af03-b1f5fe59fca1-1000f4241", wall.UniqueId);
        wall.UniqueId = "explicit identity";
        Assert.Equal("explicit identity", wall.UniqueId);
    }

    [Fact]
    public void EpisodeGuid_CountsTheCreationDateBackFromTheEndOfTheList()
    {
        var episodes = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() }.Select(g => g.ToString("D")).ToList();
        Assert.Equal(Guid.Parse(episodes[2]), ElementExportIds.EpisodeGuid(new ElemRecHistory(1, 0), episodes, 3));
        Assert.Equal(Guid.Parse(episodes[0]), ElementExportIds.EpisodeGuid(new ElemRecHistory(1, 2), episodes, 3));
    }

    [Theory]
    [InlineData(3, 3u)]   // creation date beyond the list
    [InlineData(-1, 3u)]  // negative creation date
    [InlineData(0, 4u)]   // count field disagrees with the records read
    public void EpisodeGuid_IsNullOutsideACompleteList(int creationDate, uint count)
    {
        var episodes = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() }.Select(g => g.ToString("D")).ToList();
        Assert.Null(ElementExportIds.EpisodeGuid(new ElemRecHistory(1, creationDate), episodes, count));
        Assert.Null(ElementExportIds.EpisodeGuid(new ElemRecHistory(1, 0), episodes, null));
    }


    [Fact]
    public void ParseElemTable_ReadsOriginalIdAndCreationDateOfEveryRecord()
    {
        var table = ElementExportIds.ParseElemTable(ElemTable((5, 5, 7), (9, 3, 0)));
        Assert.NotNull(table);
        Assert.Equal(new ElemRecHistory(5, 7), table[5]);
        Assert.Equal(new ElemRecHistory(3, 0), table[9]);
    }

    [Fact]
    public void ParseElemTable_RejectsAnotherClassATruncatedArrayAndUnorderedIds()
    {
        byte[] otherClass = ElemTable((5, 5, 7));
        otherClass[0] = 0xDC; otherClass[1] = 0x04; // the R20 ElemTable class id
        Assert.Null(ElementExportIds.ParseElemTable(otherClass));

        byte[] truncated = ElemTable((5, 5, 7), (9, 9, 1));
        Assert.Null(ElementExportIds.ParseElemTable(truncated[..^20]));

        Assert.Null(ElementExportIds.ParseElemTable(ElemTable((9, 9, 7), (5, 5, 1))));
        Assert.Null(ElementExportIds.ParseElemTable(ElemTable((5, 5, 7), (5, 5, 1))));
    }

    [SkippableFact]
    public void ParseElemTable_ReadsTheRealTestFileTable()
    {
        using var root = RootStorage.OpenRead(TestFileFixture.FindTestFile("TESTFILE.rvt"));
        byte[]? data = PagedGzipStream.Read(root, "Global", "ElemTable");
        Assert.NotNull(data);
        var table = ElementExportIds.ParseElemTable(data);
        Assert.NotNull(table);
        Assert.Equal(8135, table.Count);
        Assert.Equal(new ElemRecHistory(263584, 521), table[263584]);
    }


    [Fact]
    public void RemoveSegments_DropsTheSegmentAfterEveryPage()
    {
        byte[] data = Enumerable.Range(0, 2 * PagedGzipStream.PageDataLength + 10).Select(i => (byte)(i % 251)).ToArray();
        byte[] raw = InsertSegments(data);
        Assert.Equal(data.Length + 2 * PagedGzipStream.SegmentLength, raw.Length);
        Assert.Equal(data, PagedGzipStream.RemoveSegments(raw));

        byte[] shortStream = data[..PagedGzipStream.PageDataLength];
        Assert.Equal(shortStream, PagedGzipStream.RemoveSegments(shortStream));
    }

    [Fact]
    public void RemoveSegments_RejectsAMissingTagOrAStreamEndingInsideASegment()
    {
        byte[] data = Enumerable.Range(0, PagedGzipStream.PageDataLength + 10).Select(i => (byte)(i % 251)).ToArray();
        byte[] raw = InsertSegments(data);
        raw[PagedGzipStream.PageDataLength] = 1;
        Assert.Null(PagedGzipStream.RemoveSegments(raw));

        raw = InsertSegments(data);
        raw[PagedGzipStream.PageDataLength + 1] = 0x01;
        Assert.Null(PagedGzipStream.RemoveSegments(raw));

        Assert.Null(PagedGzipStream.RemoveSegments(InsertSegments(data)[..(PagedGzipStream.PageDataLength + 100)]));
    }

    [Fact]
    public void Decompress_InflatesAMemberSplitByPageSegmentsAndChecksItsTrailer()
    {
        var random = new Random(43);
        byte[] payload = new byte[3 * PagedGzipStream.PageDataLength];
        random.NextBytes(payload);
        byte[] member = Gzip(payload);
        Assert.True(member.Length > 2 * PagedGzipStream.PageDataLength);
        byte[] stream = [.. new byte[PagedGzipStream.PrefixLength], .. member, .. new byte[100]];

        Assert.Equal(payload, PagedGzipStream.Decompress(InsertSegments(stream)));

        byte[] wrongCrc = (byte[])stream.Clone();
        wrongCrc[PagedGzipStream.PrefixLength + member.Length - 8] ^= 1;
        Assert.Null(PagedGzipStream.Decompress(InsertSegments(wrongCrc)));

        // Without its segments the stream loses page data and cannot match the trailer.
        Assert.Null(PagedGzipStream.Decompress(stream));
    }

    [SkippableTheory]
    // Every multi-page stream of TESTFILE: the decode reaches the gzip trailer's length.
    [InlineData("Formats", "Latest", 453245)]
    [InlineData("Global", "Latest", 1042088)]
    [InlineData("Global", "ContentDocuments", 2905651)]
    [InlineData("Global", "ElemTable", 227805)]
    public void Read_DecodesTheRealGlobalStreamsExactly(string storage, string stream, int length)
    {
        using var root = RootStorage.OpenRead(TestFileFixture.FindTestFile("TESTFILE.rvt"));
        byte[]? data = PagedGzipStream.Read(root, storage, stream);
        Assert.NotNull(data);
        Assert.Equal(length, data.Length);
    }

    [SkippableFact]
    public void Read_ReturnsNullForAMissingStream()
    {
        using var root = RootStorage.OpenRead(TestFileFixture.FindTestFile("TESTFILE.rvt"));
        Assert.Null(PagedGzipStream.Read(root, "Global", "NoSuchStream"));
        Assert.Null(PagedGzipStream.Read(root, "NoSuchStorage", "History"));
    }


    [Fact]
    public void ExactElementGlobalId_IsTheExportIdOnceAndThenNull()
    {
        var used = new HashSet<Guid>();
        var element = new Element { Id = 263584, SerializedExportId = ElementExportIds.ExportId(
            Guid.Parse("d45b1816-cbcc-4dc6-b0bb-007467f3c600"), 263584u) };
        Assert.Equal("3KMnWMoynDnh2x07HdzyEW", IfcExporter.ResolveExactElementGlobalId(element, used));
        Assert.Null(IfcExporter.ResolveExactElementGlobalId(element, used));
        Assert.Null(IfcExporter.ResolveExactElementGlobalId(new Element { Id = 1 }, used));
    }

    [Fact]
    public void ExactElementGlobalId_OfASubElementXorsItsIndex()
    {
        var element = new Element { Id = 628359, SerializedExportId = ElementExportIds.ExportId(
            Guid.Parse("5b7ac240-e9a7-43b2-b6bf-246956549a40"), 628359u) };
        Assert.Equal("1RUi90wQT3ihQ$B6bMNGp7", IfcExporter.ResolveExactElementGlobalId(
            element, new HashSet<Guid>(), IfcExporter.InstanceAsTypeSubElementIndex));
    }

    [Theory]
    [InlineData(-1019000)] // IFC_GUID
    [InlineData(-1019001)] // IFC_TYPE_GUID
    public void ExactElementGlobalId_YieldsToAnIfcGuidParameter(int parameterId)
    {
        var element = new Element
        {
            Id = 263584,
            SerializedExportId = Guid.NewGuid(),
            Parameters = [new Parameter { Id = parameterId, Definition = new Definition { Id = parameterId },
                StorageType = StorageType.String, Value = "3KMnWMoynDnh2x07HdzyEW" }],
        };
        Assert.Null(IfcExporter.ResolveExactElementGlobalId(element, new HashSet<Guid>()));
    }

    [Theory]
    [InlineData(StorageType.String, "3KMnWMoynDnh2x07HdzyEW", true, false)]
    [InlineData(StorageType.Integer, -1, false, true)]
    [InlineData(StorageType.String, "", false, true)]
    [InlineData(StorageType.String, "3KMnWMoynDnh2x07HdzyEW", false, false)]
    public void ExactElementGlobalId_IfcGuidParameterOverridesOnlyAsNonEmptyText22_8(
        StorageType storage, object value, bool currentProfile, bool expectGuid)
    {
        var element = new Element
        {
            Id = 263584,
            SerializedExportId = ElementExportIds.ExportId(Guid.Parse("d45b1816-cbcc-4dc6-b0bb-007467f3c600"), 263584u),
            Parameters = [new Parameter { Id = -1019000, Definition = new Definition { Id = -1019000 },
                StorageType = storage, Value = value }],
        };
        Assert.Equal(expectGuid || currentProfile ? "3KMnWMoynDnh2x07HdzyEW" : null,
            IfcExporter.ResolveExactElementGlobalId(element, new HashSet<Guid>(), handleBased: currentProfile));
    }
    private static byte[] ElemTable(params (uint Id, uint Original, int Creation)[] records)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(ElementExportIds.ElemTableClassId));
        bytes.AddRange(BitConverter.GetBytes((uint)records.Length));
        foreach (var (id, original, creation) in records)
        {
            bytes.AddRange(BitConverter.GetBytes(id));
            bytes.AddRange(BitConverter.GetBytes(original));
            bytes.AddRange(BitConverter.GetBytes(creation));
            bytes.AddRange(BitConverter.GetBytes(creation));
            bytes.AddRange(BitConverter.GetBytes(creation));
            bytes.AddRange(BitConverter.GetBytes(0u));
            bytes.AddRange(BitConverter.GetBytes(uint.MaxValue));
        }
        bytes.AddRange(new byte[19]);
        return bytes.ToArray();
    }

    /// <summary>The physical stream: a tagged 353-byte segment after every 64,896 bytes.</summary>
    private static byte[] InsertSegments(byte[] data)
    {
        var raw = new List<byte>();
        for (int offset = 0; offset < data.Length; offset += PagedGzipStream.PageDataLength)
        {
            int length = Math.Min(PagedGzipStream.PageDataLength, data.Length - offset);
            raw.AddRange(data.AsSpan(offset, length).ToArray());
            if (length == PagedGzipStream.PageDataLength && offset + length < data.Length)
            {
                var segment = new byte[PagedGzipStream.SegmentLength];
                for (int i = 2; i < segment.Length - 1; i++) segment[i] = (byte)(i * 7 + offset);
                segment[1] = 0xA0;
                raw.AddRange(segment);
            }
        }
        return raw.ToArray();
    }

    private static byte[] Gzip(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(payload);
        return output.ToArray();
    }
}

/// <summary>
/// Document-level GlobalIds: DocumentHistory.m_detachGUID of Global/History XORed with project-level ids and level ids.
/// </summary>
public class DocumentExportIdTests
{
    private static readonly Guid Creation = Guid.Parse("28c39f05-e0ac-49cf-8388-f20aea7065ab");
    private static readonly Guid Detach = Guid.Parse("4a659493-2d45-4ea1-a078-3a0ebcf97857");

    [Fact]
    public void ParseEpisodeGuids_ReadsTheDetachGuidSlot()
    {
        var result = RvtFileReader.ParseEpisodeGuids(new List<byte[]> { History(Creation, Detach) }, null);
        Assert.Equal(Detach, result.DetachGuid);
        Assert.Single(result.Guids);
    }

    [Fact]
    public void ParseEpisodeGuids_HasNoDetachGuidWhenTheSlotIsEmptyOrTheTableInvalid()
    {
        Assert.Null(RvtFileReader.ParseEpisodeGuids(new List<byte[]> { History(Creation, Guid.Empty) }, null).DetachGuid);
        byte[] wrongSignature = History(Creation, Detach);
        wrongSignature[0] = 0;
        Assert.Null(RvtFileReader.ParseEpisodeGuids(new List<byte[]> { wrongSignature }, null).DetachGuid);
    }

    [SkippableFact]
    public void ParseEpisodeGuids_ReadsTheRealTestFileDocumentGuid()
    {
        using var root = RootStorage.OpenRead(TestFileFixture.FindTestFile("TESTFILE.rvt"));
        byte[]? history = PagedGzipStream.Read(root, "Global", "History");
        Assert.NotNull(history);
        var result = RvtFileReader.ParseEpisodeGuids(new List<byte[]> { history }, null);
        Assert.Equal(Guid.Parse("441ce1eb-b592-4c9f-b664-6bc2cf0d55e2"), result.DetachGuid);
    }

    [Theory]
    // sample project A: the detach GUID, not the creation GUID, gives the expected export project, building,
    // site and storey GlobalIds.
    [InlineData("4a659493-2d45-4ea1-a078-3a0ebcf97857", -16, "1APPIJBKLEeQ1uEWv31eUd")]
    [InlineData("4a659493-2d45-4ea1-a078-3a0ebcf97857", -15, "1APPIJBKLEeQ1uEWv31eUc")]
    [InlineData("4a659493-2d45-4ea1-a078-3a0ebcf97857", -14, "1APPIJBKLEeQ1uEWv31eUb")]
    [InlineData("4a659493-2d45-4ea1-a078-3a0ebcf97857", 9124, "1APPIJBKLEeQ1uEWwy_Llp")]
    // Test_2022: project and storey "Level 1" (level 311).
    [InlineData("e340d1fc-19c1-4200-9ff0-415bc126641d", -16, "3ZGD7y6S5209$mGLi_sPlj")]
    [InlineData("e340d1fc-19c1-4200-9ff0-415bc126641d", 311, "3ZGD7y6S5209$mGLl19cKg")]
    public void DocumentGlobalId_XorsTheIdIntoTheDocumentGuid(string documentGuid, int id, string expected)
    {
        var info = new RevitFileInfo { SerializedDocumentGuid = Guid.Parse(documentGuid) };
        var used = new HashSet<Guid>();
        Assert.Equal(expected, IfcExporter.ResolveDocumentGlobalId(info, id, used));
        Assert.Null(IfcExporter.ResolveDocumentGlobalId(info, id, used));
        Assert.Null(IfcExporter.ResolveDocumentGlobalId(new RevitFileInfo(), id, new HashSet<Guid>()));
    }

    [Fact]
    public void DocumentGlobalId_OfTheCreationGuidIsNotTheSampleProjectGlobalId()
    {
        var info = new RevitFileInfo { SerializedDocumentGuid = Creation };
        Assert.NotEqual("1APPIJBKLEeQ1uEWv31eUd", IfcExporter.ResolveDocumentGlobalId(info, -16, new HashSet<Guid>()));
    }

    [Theory]
    [InlineData(-1019002, null)]           // IFC_PROJECT_GUID
    [InlineData(-1019003, null)]           // IFC_BUILDING_GUID
    [InlineData(-1019004, null)]           // IFC_SITE_GUID
    [InlineData(0, "IfcProject GUID")]
    [InlineData(0, "IfcSiteGUID")]
    public void ProjectLevelGuidParameter_IsDetected(int id, string? name)
    {
        var info = new RevitFileInfo();
        info.Elements.Add(new Element
        {
            Id = 1,
            Parameters = [new Parameter { Id = id, Definition = new Definition { Id = id, Name = name },
                StorageType = StorageType.String, Value = "1APPIJBKLEeQ1uEWv31eUd" }],
        });
        Assert.True(IfcExporter.HasProjectLevelGuidParameter(info));
        Assert.False(IfcExporter.HasProjectLevelGuidParameter(new RevitFileInfo()));
    }

    /// <summary>A DocumentHistory stream: signature, the five GUID slots, an empty extra list,
    /// a count of one and one episode record.</summary>
    private static byte[] History(Guid creation, Guid detach)
    {
        var bytes = new byte[110 + 17];
        new byte[] { 0xA2, 0x04, 0x01, 0, 0, 0, 0, 0 }.CopyTo(bytes, 0);
        creation.ToByteArray().CopyTo(bytes, 22);
        detach.ToByteArray().CopyTo(bytes, RvtFileReader.EpisodeTableDetachGuidOffset);
        creation.ToByteArray().CopyTo(bytes, 54);
        creation.ToByteArray().CopyTo(bytes, 86);
        BitConverter.GetBytes(1u).CopyTo(bytes, 106);
        Guid.NewGuid().ToByteArray().CopyTo(bytes, 110);
        bytes[126] = 0x28;
        return bytes;
    }
}

