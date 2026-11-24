using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The axis-aligned outline the ElementHeader stores: the element box of every release, written
/// behind a tag pair of the embedded format schema, absent where the application reports none, and
/// never taken from a counter.</summary>
public class ElementHeaderBoundingBoxTests
{
    private const ushort OutlineTag = 297, ParentsTag = 1352;
    private const uint NewObject = 0xFFFFFFFF;

    /// <summary>One entry of the regeneration history: 14 bytes of the Revit 2022 layout.</summary>
    private static byte[] HistoryEntry(params byte[] leading)
    {
        var entry = new byte[14];
        leading.CopyTo(entry, 0);
        return entry;
    }

    /// <summary>An ElementHeader record (class 1340) as the files store it: the regeneration history, the six ids (category, family, owner view,
    /// design option, unplaced owner, misc), m_viewRules, m_abFlags4Bytes, m_classDef (two bytes), and then <paramref name="pointers"/>: the
    /// pointers m_pBBox and m_parents and the objects they name.</summary>
    private static byte[] Record(byte[] pointers, byte[][]? history = null, uint viewRules = 0xFFFF8000)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write((ushort)0x053C);
        writer.Write((uint)(history?.Length ?? 0));
        foreach (byte[] entry in history ?? []) writer.Write(entry);
        writer.Write(-2000112);             // m_categroryId (OST_Phases)
        for (int i = 0; i < 5; i++) writer.Write(-1);   // m_familyId, m_ownerViewId, m_designOptionId, m_unplacedOwnerId, m_miscId
        writer.Write(viewRules);            // m_viewRules
        writer.Write(0x000109B8u);          // m_abFlags4Bytes
        writer.Write((ushort)0x0725);       // m_classDef
        writer.Write(pointers);
        byte[] record = stream.ToArray();
        BitConverter.GetBytes((uint)(record.Length - 12)).CopyTo(record, 4);
        return record;
    }

    private static byte[] OutlineBody(double[] box) => Pointers(OutlineTag, ParentsTag, [.. box]);

    private static byte[] AbsentBody() => Pointers(0, ParentsTag, []);

    /// <summary>m_pBBox (a new object of class <paramref name="outlineTag"/>, or 0: none), m_parents (a new object of class
    /// <paramref name="parentsTag"/>) and the six doubles of the Outline.</summary>
    private static byte[] Pointers(ushort outlineTag, ushort parentsTag, double[] box, uint boxHandle = NewObject)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        if (outlineTag == 0) writer.Write(0u);
        else
        {
            writer.Write(boxHandle);
            writer.Write(outlineTag);
        }
        writer.Write(NewObject);
        writer.Write(parentsTag);
        foreach (double value in box) writer.Write(value);
        return stream.ToArray();
    }

    [Fact]
    public void Reader_ReadsTheStoredOutline()
    {
        double[] box = [-1.5, -2.25, 0.0, 3.75, 4.5, 12.0];
        var verdict = ElementHeaderCategory.ReadBoundingBox(Record(OutlineBody(box)), 0);
        Assert.NotNull(verdict);
        Assert.True(verdict.Present);
        Assert.Equal(box, verdict.Box);
    }

    [Fact]
    public void Reader_AcceptsTheStoredAbsenceOfAnOutline()
    {
        var verdict = ElementHeaderCategory.ReadBoundingBox(Record(AbsentBody()), 0);
        Assert.NotNull(verdict);
        Assert.False(verdict.Present);
        Assert.Null(verdict.Box);
        Assert.Same(verdict, ElementHeaderCategory.ReadBoundingBox(Record(AbsentBody()), 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Reader_TakesNoCounterForABox(int mutation)
    {
        // A pattern whose doubles do not form a box: a NaN one, or a max below its min.
        double[] box = mutation == 0
            ? [double.NaN, -1.0, 0.0, 1.0, 2.0, 3.0]
            : [3.0, 2.0, 1.0, 0.0, -1.0, -2.0];
        Assert.Null(ElementHeaderCategory.ReadBoundingBox(Record(OutlineBody(box)), 0));
    }

    [Fact]
    public void Reader_LeavesARecordWithoutTheHeaderStructureUnknown()
    {
        double[] box = [0, 0, 0, 1, 1, 1];
        Assert.Null(ElementHeaderCategory.ReadBoundingBox(Record([1, 2, 3, 4]), 0));
        // A header the reader cannot place (a wrong class tag) is unknown as well.
        byte[] record = Record(OutlineBody(box));
        record[8] ^= 0xFF;
        Assert.Null(ElementHeaderCategory.ReadBoundingBox(record, 0));
        // The structure counts as read where its tags stand: another class behind m_pBBox or m_parents, and a handle that is neither
        // 0 nor a new object, are unknown.
        Assert.Null(ElementHeaderCategory.ReadBoundingBox(Record(Pointers(OutlineTag + 1, ParentsTag, box)), 0));
        Assert.Null(ElementHeaderCategory.ReadBoundingBox(Record(Pointers(OutlineTag, ParentsTag + 1, box)), 0));
        Assert.Null(ElementHeaderCategory.ReadBoundingBox(Record(Pointers(OutlineTag, ParentsTag, box, boxHandle: 7)), 0));
        // A record cut inside the six doubles.
        byte[] cut = Record(OutlineBody(box));
        Assert.Null(ElementHeaderCategory.ReadBoundingBox(cut[..^8], 0));
    }

    [Fact]
    public void Reader_ReadsTheOutlineWhateverBitsTheViewRulesHold()
    {
        // m_viewRules is four bytes of flags, and all ones (no rule set) is a value like the others: the outline behind it is read.
        double[] box = [-1.5, -2.25, 0.0, 3.75, 4.5, 12.0];
        foreach (uint viewRules in new[] { NewObject, 0u, 0xFFFF8000u, 0xFFFFEF7Fu })
        {
            var verdict = ElementHeaderCategory.ReadBoundingBox(Record(OutlineBody(box), viewRules: viewRules), 0);
            Assert.NotNull(verdict);
            Assert.Equal(box, verdict.Box);
            Assert.False(ElementHeaderCategory.ReadBoundingBox(Record(AbsentBody(), viewRules: viewRules), 0)!.Present);
        }
    }

    [Fact]
    public void Reader_TakesThePositionOfTheOutlineFromTheStructure_NotFromTheBytesOfTheRegenerationHistory()
    {
        double[] box = [-1.5, -2.25, 0.0, 3.75, 4.5, 12.0];
        // A regeneration entry that holds the bytes 00 00 FF FF FF FF 48 05 (what an absent outline looks like in front of m_parents):
        // the real outline that stands behind the fields is still the one read.
        byte[] looksAbsent = HistoryEntry(0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0x48, 0x05);
        var verdict = ElementHeaderCategory.ReadBoundingBox(Record(OutlineBody(box), [looksAbsent]), 0);
        Assert.NotNull(verdict);
        Assert.True(verdict.Present);
        Assert.Equal(box, verdict.Box);

        // The bytes of a present outline in a regeneration entry do not make a box for a header that stores none, and a count of
        // entries that moves the fields moves the read with them.
        byte[] looksPresent = [.. HistoryEntry(0x29, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0x48, 0x05)];
        byte[] garbage = [.. Enumerable.Range(0, 6).SelectMany(i => BitConverter.GetBytes((double)i))];
        byte[] entryWithBox = [.. looksPresent[..8], .. garbage[..6]];
        var absent = ElementHeaderCategory.ReadBoundingBox(Record(AbsentBody(), [entryWithBox, entryWithBox]), 0);
        Assert.NotNull(absent);
        Assert.False(absent.Present);
        var withHistory = ElementHeaderCategory.ReadBoundingBox(Record(OutlineBody(box), [entryWithBox, looksAbsent, entryWithBox]), 0);
        Assert.NotNull(withHistory);
        Assert.Equal(box, withHistory.Box);
    }

    [Fact]
    public void Reader_ReadsTheOutlineOfAHeaderWhoseIdsAreEightBytesWide()
    {
        double[] box = [-1.5, -2.25, 0.0, 3.75, 4.5, 12.0];
        // categoryAt, six ids of eight bytes, then the same fields as the 32-bit layout: the layout the files of Revit 2024 and later store
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        for (int i = 0; i < 6; i++) writer.Write(-1L);
        writer.Write(0xFFFF8000u);
        writer.Write(0x000109B8u);
        writer.Write((ushort)0x0725);
        writer.Write(OutlineBody(box));
        byte[] fields = stream.ToArray();
        var verdict = ElementHeaderCategory.ReadOutline(fields, 0, fields.Length, 8, OutlineTag, ParentsTag);
        Assert.NotNull(verdict);
        Assert.Equal(box, verdict.Box);
        // read as the 32-bit layout it is not an outline
        Assert.Null(ElementHeaderCategory.ReadOutline(fields, 0, fields.Length, 4, OutlineTag, ParentsTag));
    }

    [Fact]
    public void Recorder_FollowsTheRecordsAndStaysUnknownOnDisagreement()
    {
        double[] box = [0, 0, 0, 1, 1, 1];
        var state = new ParsingState();
        ElementHeaderCategory.Record(state, Record(OutlineBody(box)), 0, 100);
        var verdict = Assert.IsType<ElementHeaderBox>(state.HeaderBoundingBoxes[100]);
        Assert.True(verdict.Present);
        Assert.Equal(box, verdict.Box);
        // A second record of the same element that agrees keeps the verdict.
        ElementHeaderCategory.Record(state, Record(OutlineBody(box)), 0, 100);
        Assert.Same(verdict, state.HeaderBoundingBoxes[100]);
        // One that stores no outline instead turns the verdict unknown.
        ElementHeaderCategory.Record(state, Record(AbsentBody()), 0, 100);
        Assert.Null(state.HeaderBoundingBoxes[100]);
        // A record of another element starts fresh.
        ElementHeaderCategory.Record(state, Record(AbsentBody()), 0, 101);
        Assert.False(Assert.IsType<ElementHeaderBox>(state.HeaderBoundingBoxes[101]).Present);
    }
    [Fact]
    public void Applier_PutsTheStoredOutlineOnTheElementAndClearsTheUnstoredOne()
    {
        double[] box = [-1.0, -1.0, 0.0, 1.0, 1.0, 2.0];
        var state = new ParsingState();
        state.HeaderBoundingBoxes[100] = new ElementHeaderBox(true, box);
        state.HeaderBoundingBoxes[101] = new ElementHeaderBox(false, null);
        state.HeaderBoundingBoxes[102] = null;                                   // the records disagree
        state.HeaderBoundingBoxes[103] = new ElementHeaderBox(true, box);
        var boxed = new Element { Id = 100, BoundingBox = [9, 9, 9, 9, 9, 9] };
        var unstored = new Element { Id = 101, BoundingBox = [9, 9, 9, 9, 9, 9] };
        var disagreeing = new Element { Id = 102, BoundingBox = [9, 9, 9, 9, 9, 9] };
        var unrecorded = new Element { Id = 104, BoundingBox = [9, 9, 9, 9, 9, 9] };
        ElementBuilder.ApplyHeaderBoundingBoxes(
            new RevitFileInfo { Elements = [boxed, unstored, disagreeing, unrecorded] }, state);
        Assert.Equal(box, boxed.BoundingBox);
        Assert.True(boxed.BoundingBoxIsHeaderOutline);
        Assert.Null(unstored.BoundingBox);
        Assert.False(unstored.BoundingBoxIsHeaderOutline);
        Assert.False(disagreeing.BoundingBoxIsHeaderOutline);
        Assert.Equal([9, 9, 9, 9, 9, 9], disagreeing.BoundingBox);
        Assert.Equal([9, 9, 9, 9, 9, 9], unrecorded.BoundingBox);
        Assert.Same(box, state.HeaderBoundingBoxes[103]!.Box);
    }

    [Fact]
    public void DecodedBox_IsTheBoxOfTheGeometry_NeverTheOutlineOfTheHeader()
    {
        double[] outline = [0, 0, 0, 10, 10, 10], tight = [1, 1, 1, 9, 9, 9], own = [2, 2, 2, 8, 8, 8];
        // an element that was not read from a file has its own box, as it had before the outline was read: nothing else stands in for it
        Assert.Same(own, new Element { BoundingBox = own, Geometry = new GeometryData { BoundingBox = tight } }.DecodedBox);
        Assert.Null(new Element { Geometry = new GeometryData { BoundingBox = tight } }.DecodedBox);
        Assert.Null(new Element().DecodedBox);
        // an element whose box is the outline of its header: the box of its geometry, and none without geometry (the outline says
        // nothing about a body: the application reports one for a roof that only holds curtain panels)
        Assert.Same(tight, new Element { BoundingBox = outline, BoundingBoxIsHeaderOutline = true, Geometry = new GeometryData { BoundingBox = tight } }.DecodedBox);
        Assert.Null(new Element { BoundingBox = outline, BoundingBoxIsHeaderOutline = true }.DecodedBox);
        Assert.Null(new Element { BoundingBox = outline, BoundingBoxIsHeaderOutline = true, Geometry = new GeometryData() }.DecodedBox);
    }

    [SkippableTheory]
    [InlineData(2020)]
    [InlineData(2021)]
    [InlineData(2022)]
    [InlineData(2023)]
    [InlineData(2024)]
    [InlineData(2025)]
    [InlineData(2026)]
    [InlineData(2027)]
    public void StoredOutline_IsTheElementBoxOfEveryRelease(int release)
    {
        string path = FirstOfRelease(release) ?? throw TestFiles.Missing($"a project of release {release}");
        // The anchored walk that reads the outline anchors the records of the Revit 2022 layout:
        // the reads of the later releases take it through the R22 layout (the IFC export does too).
        var info = new RvtFileReader(new RvtReaderOptions { R22Layout = true }).Read(path);

        var boxed = info.Elements.Where(e => e.BoundingBox is { Length: 6 }).ToList();
        Assert.NotEmpty(boxed);
        // The outline bounds the body: every geometry box the element decoded lies inside it (the
        // tessellation of a curved body may pass the stored envelope by a hair).
        foreach (var element in boxed)
            if (element.Geometry?.BoundingBox is { Length: 6 } tight)
            {
                double[] box = element.BoundingBox!;
                Assert.True(box[0] <= tight[0] + 1e-3 && box[1] <= tight[1] + 1e-3 && box[2] <= tight[2] + 1e-3
                    && box[3] >= tight[3] - 1e-3 && box[4] >= tight[4] - 1e-3 && box[5] >= tight[5] - 1e-3,
                    $"element {element.Id} of release {release}: [{string.Join(", ", box)}] does not contain [{string.Join(", ", tight)}]");
            }
        // A grid or a level has no stored outline in any release.
        Assert.All(info.Elements.Where(e => e.CategoryId is (int)BuiltInCategory.OST_Grids or (int)BuiltInCategory.OST_Levels),
            e => Assert.Null(e.BoundingBox));
    }

    [SkippableTheory]
    [InlineData(2023)]
    [InlineData(2024)]
    [InlineData(2025)]
    [InlineData(2026)]
    [InlineData(2027)]
    public void StoredOutline_IsReadByTheDefaultReaderOfEveryRelease(int release)
    {
        string path = FirstOfRelease(release) ?? throw TestFiles.Missing($"a project of release {release}");
        // The files of these releases carry their own class schema and are read from it, without the R22 layout: the outline is
        // read there too, and is the box the R22 layout reads for the same element.
        var plain = new RvtFileReader().Read(path);
        var boxed = plain.Elements.Where(e => e.BoundingBox is { Length: 6 }).ToList();
        Assert.NotEmpty(boxed);
        // the box is the outline of the header, and no geometry is decoded there: the export measures no body by it
        Assert.All(boxed, e => Assert.True(e.BoundingBoxIsHeaderOutline));
        Assert.All(boxed, e => Assert.Null(e.DecodedBox));

        var viaR22 = new RvtFileReader(new RvtReaderOptions { R22Layout = true }).Read(path).Elements
            .Where(e => e.BoundingBox is { Length: 6 }).GroupBy(e => e.Id).ToDictionary(group => group.Key, group => group.First().BoundingBox!);
        int compared = 0;
        foreach (var element in boxed)
            if (viaR22.TryGetValue(element.Id, out double[]? other))
            {
                Assert.Equal(other, element.BoundingBox);
                compared++;
            }
        Assert.True(compared > 0, $"release {release}: no element has a box in both reads");
        Assert.All(plain.Elements.Where(e => e.CategoryId is (int)BuiltInCategory.OST_Grids or (int)BuiltInCategory.OST_Levels),
            e => Assert.Null(e.BoundingBox));
    }

    private static string? FirstOfRelease(int release)
    {
        foreach (string path in PageCodecSampleTests.ProjectFiles())
        {
            using var root = OpenMcdf.RootStorage.OpenRead(path);
            if (TfSchemaReader.Read(root) is { } document && SchemaRelease.FromSchema(new FormatsSchema(document)) == release) return path;
        }
        return null;
    }
}
