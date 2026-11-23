using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>
/// ElementHeader.m_categroryId of an element record. Record layout
/// ([id:4][bodySize:4][class:2][m_regenHistory: count u32 + count x 14-byte entries][m_categroryId:4]...) as seen in header dumps of R22 and R20 files; see <see cref="ElementHeaderCategory"/>.
/// </summary>
public class ElementHeaderCategoryTests
{
    /// <summary>A hand-made record: id, size, class tag, the entry count with <paramref name="entries"/> 14-byte entries, the
    /// category and a tail of <paramref name="tail"/> filler bytes (the other header fields).</summary>
    private static byte[] Record(uint id, int category, int entries = 0, ushort recordClass = 0x053C, int tail = 24, byte entryFill = 0x11)
    {
        int length = 14 + 14 * entries + 4 + tail;
        var bytes = new byte[length];
        BitConverter.GetBytes(id).CopyTo(bytes, 0);
        BitConverter.GetBytes((uint)(length - 12)).CopyTo(bytes, 4);
        BitConverter.GetBytes(recordClass).CopyTo(bytes, 8);
        BitConverter.GetBytes((uint)entries).CopyTo(bytes, 10);
        for (int i = 14; i < 14 + 14 * entries; i++) bytes[i] = entryFill;
        BitConverter.GetBytes(category).CopyTo(bytes, 14 + 14 * entries);
        for (int i = 18 + 14 * entries; i < length; i++) bytes[i] = 0xFF;
        return bytes;
    }

    [Fact]
    public void Read_NoRegenHistory_ReturnsTheFirstFieldAfterTheCount()
    {
        Assert.Equal(-2000011, ElementHeaderCategory.Read(Record(5, -2000011), 0));
    }

    [Fact]
    public void Read_NullCategoryIsMinusOne_AndAPositiveElementIdIsKept()
    {
        Assert.Equal(-1, ElementHeaderCategory.Read(Record(5, -1), 0));
        Assert.Equal(198353, ElementHeaderCategory.Read(Record(6, 198353), 0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(17)]
    [InlineData(69)]
    public void Read_SkipsOneFourteenByteEntryPerRegenHistoryItem(int entries)
    {
        // entries filled with a negative-looking pattern: a reader that stopped at the first value of that kind would return it
        Assert.Equal(-2001040, ElementHeaderCategory.Read(Record(7, -2001040, entries, entryFill: 0xE1), 0));
    }

    [Fact]
    public void Read_ReadsTheRecordAtItsStartOffsetInALargerBuffer()
    {
        var first = Record(8, -2000032, 1);
        var second = Record(9, -2000035);
        var buffer = first.Concat(second).ToArray();
        Assert.Equal(-2000032, ElementHeaderCategory.Read(buffer, 0));
        Assert.Equal(-2000035, ElementHeaderCategory.Read(buffer, first.Length));
    }

    [Fact]
    public void Read_AcceptsTheR20RecordClassTag()
    {
        Assert.Equal(-2000038, ElementHeaderCategory.Read(Record(10, -2000038, 3, recordClass: 0x04F8), 0));
    }

    [Theory]
    [InlineData((ushort)0x0548)]
    [InlineData((ushort)0x0000)]
    [InlineData((ushort)0x07D7)]
    public void Read_RejectsAnyOtherClassTag(ushort recordClass)
    {
        Assert.Null(ElementHeaderCategory.Read(Record(11, -2000011, 0, recordClass), 0));
    }

    [Fact]
    public void Read_RejectsAnEntryCountThatLeavesNoRoomForTheCategory()
    {
        var bytes = Record(12, -2000011, 1);
        BitConverter.GetBytes(0x7FFFFFFFu).CopyTo(bytes, 10);
        Assert.Null(ElementHeaderCategory.Read(bytes, 0));
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(bytes, 10);
        Assert.Null(ElementHeaderCategory.Read(bytes, 0));
        // exactly one entry too many for the declared size
        BitConverter.GetBytes(2u).CopyTo(bytes, 10);
        BitConverter.GetBytes((uint)(14 + 14 + 4 - 12)).CopyTo(bytes, 4);
        Assert.Null(ElementHeaderCategory.Read(bytes, 0));
    }

    [Fact]
    public void Read_RejectsADeclaredSizeBeyondTheBuffer_AndShortOrOutOfRangeInput()
    {
        var bytes = Record(13, -2000011);
        Assert.Null(ElementHeaderCategory.Read(bytes[..^5], 0));
        Assert.Null(ElementHeaderCategory.Read(bytes[..12], 0));
        Assert.Null(ElementHeaderCategory.Read(bytes, -1));
        Assert.Null(ElementHeaderCategory.Read(bytes, bytes.Length));
        Assert.Null(ElementHeaderCategory.Read([], 0));
    }

    [Fact]
    public void Record_KeepsAValueThatEveryRecordOfTheIdAgreesOn()
    {
        var state = new ParsingState();
        var a = Record(20, -2000011);
        var b = Record(20, -2000011, 2);
        ElementHeaderCategory.Record(state, a, 0, 20);
        ElementHeaderCategory.Record(state, b, 0, 20);
        Assert.Equal(-2000011, state.HeaderCategories[20]);
    }

    [Fact]
    public void Record_AConflictOrAnUnreadableHeaderMakesTheIdUnknownForGood()
    {
        var state = new ParsingState();
        ElementHeaderCategory.Record(state, Record(21, -2000011), 0, 21);
        ElementHeaderCategory.Record(state, Record(21, -2000035), 0, 21);
        Assert.True(state.HeaderCategories.ContainsKey(21));
        Assert.Null(state.HeaderCategories[21]);
        ElementHeaderCategory.Record(state, Record(21, -2000011), 0, 21); // a later agreeing record does not revive it
        Assert.Null(state.HeaderCategories[21]);

        ElementHeaderCategory.Record(state, Record(22, -2000011, 0, 0x0548), 0, 22); // unreadable first
        ElementHeaderCategory.Record(state, Record(22, -2000011), 0, 22);
        Assert.Null(state.HeaderCategories[22]);
    }

    [Fact]
    public void Walk_TakesTheChainedRecordNotAFalseHeaderOfAKnownId()
    {
        // a header-looking run of a known id in the middle of other data (its successor is not an item) comes first and
        // carries another category; the real, chained records of the ids 31 and 32 come after it
        var fake = Record(31, 12345, 0, tail: 30);
        var junk = Enumerable.Repeat((byte)0xAB, 64).ToArray();
        var real31 = Record(31, -2000032, 2);
        var real32 = Record(32, -2000035);
        var chunk = junk.Concat(fake).Concat(junk).Concat(real31).Concat(real32).ToArray();

        var state = new ParsingState();
        var seen = new List<uint>();
        TfStreamWalker.Walk([chunk], new HashSet<uint> { 31, 32 }, (_, _, _, _, _) => { }, (buffer, start, id) =>
        {
            seen.Add(id);
            ElementHeaderCategory.Record(state, buffer, start, id);
        });

        Assert.Equal([31u, 32u], seen);
        Assert.Equal(-2000032, state.HeaderCategories[31]);
        Assert.Equal(-2000035, state.HeaderCategories[32]);
    }

    [Fact]
    public void Walk_DoesNotVisitARecordOfAnIdThatIsNotKnown()
    {
        var chunk = Record(40, -2000011).Concat(Record(41, -2000035)).ToArray();
        var seen = new List<uint>();
        TfStreamWalker.Walk([chunk], new HashSet<uint> { 41 }, (_, _, _, _, _) => { }, (_, _, id) => seen.Add(id));
        Assert.DoesNotContain(40u, seen);
    }

    [Fact]
    public void Walk_ReportsByteOffsetsWithinASegment()
    {
        var junk = new byte[2 * (1 << 20) + 32];
        var updates = new List<(long Completed, long Total)>();
        TfStreamWalker.Walk([junk], [], (_, _, _, _, _) => { },
            progress: (completed, total) => updates.Add((completed, total)));

        Assert.Equal([0L, 1L << 20, 2L << 20, junk.LongLength], updates.Select(update => update.Completed));
        Assert.All(updates, update => Assert.Equal(junk.LongLength, update.Total));
    }
}

/// <summary>Test_2022.rvt against the header category of the current profile, one element per (class, category) pair; the vectors are the ones below.</summary>
public class ElementHeaderCategoryTest2022Tests : IClassFixture<ElementHeaderCategoryTest2022Tests.Fixture>
{
    public sealed class Fixture
    {
        public RevitFileInfo Info { get; } = new RvtFileReader().Read(TestFileFixture.FindTestFile("Test_2022.rvt"));
    }

    private readonly Fixture _fixture;
    public ElementHeaderCategoryTest2022Tests(Fixture fixture) => _fixture = fixture;

    private static readonly (int Id, int Category)[] MeshVectors =
    [
        (1, -1), (2, -1), (3, -1), (10, -1), (23, -2000700), (30, -1), (230, -1), (231, -1),
        (232, -2000510), (233, -1), (234, -1), (235, -1), (277, -1), (280, -1), (299, -2000550), (305, -2000240),
        (311, -2000240), (312, -2000279), (338, -2000035), (339, -2000032), (341, -2000220), (350, -2000180), (397, -2000011), (757, -2000038),
        (1049, -2000400), (1141, -2000538), (4169, -2000515), (7843, -2000182), (7844, -2000181), (7848, -2000151), (8142, -2000535), (8152, -2000278),
        (8157, -2000279), (8953, -2001342), (9490, -2003201), (12344, -2000127), (12589, -2000112), (16216, -2003000), (19878, -2001393), (19885, -2001390),
        (19886, -2001391), (19887, -2001392), (21747, -2000111), (21748, -2000110), (23738, -2005100), (23742, -2001265), (24584, -2006040), (24867, -2006020),
        (24927, -2000055), (28598, -2000120), (29109, -2002000), (29292, -2002000), (49026, -2001327), (49029, -2006060), (49030, -2006070), (49034, -2000340),
        (49504, -2003101), (51439, -2005210), (54520, -2009609), (54537, -2003500), (54539, -2000170), (55171, -2008039), (55359, -2008040), (55360, -2008041),
        (85113, -2001300), (86143, -2002000), (86204, -2000977), (86313, -2001300), (99859, -2000552), (99880, -2009014), (100162, -2000197), (102356, -2008104),
        (102357, -2008103), (102360, -2008105), (102380, -2008106), (102411, -2008111), (102415, -2008112), (102441, -2008113), (102462, -2008107), (105532, -2008121),
        (105557, -2008120), (105590, -2008119), (111426, -2001272), (111429, -2001271), (112390, -2003331), (113159, -2008142), (113160, -2008143), (113164, -2008144),
        (116082, -2008145), (116083, -2008147), (116084, -2008146), (121707, -2009609), (121716, -2003421), (121725, -2003414), (121732, -2003422), (121739, -2003418),
        (121740, -2003423), (121751, -2003417), (125028, -2006045), (127547, -2008148), (132467, -2008015), (132471, -2008043), (137322, -2009657), (137323, -2008163),
        (139202, -2000947), (141548, -2000946), (141723, -2000930), (141728, -2000938), (141730, -2000919), (141731, -2000920), (141732, -2000123), (141734, -2000120),
        (147064, -2000948), (174475, -2000924), (176804, -2000032), (195169, -2000220), (198366, -2000014), (198694, -2000011), (211807, -2001160), (211850, -2001160),
        (232770, -2000171), (232780, -2000023), (243253, -2000278), (243274, -2000035), (243275, -2000530), (286092, -2000560), (287141, -2000151), (382615, -2000280),
        (382617, -2003100), (382621, -2000280), (397393, -2001260), (411452, -2001340), (414482, -2001330), (414483, -2009636), (414562, -2009645), (414563, -2009645),
        (414737, -2001263), (415653, -2000045), (418977, -2001260), (420952, -2001330), (422466, -2000023), (423100, -2000170), (423106, -2000321), (423134, -2000171),
        (429028, -2000262), (457479, -2000014), (458927, -2000011), (490129, -2000126), (490319, -2001263), (493613, -2009640), (495353, -2009639), (499496, -2000151),
        (504396, -2000997), (506166, -2001100), (506168, -2001100), (507024, -2000035), (509468, -2001300), (512277, -2001300), (512278, -2009641), (513254, -2000120),
        (513309, -2000919), (513310, -2000921), (513460, -2000938), (549060, -2000261), (566588, -2000126), (566599, -2000127), (566600, -2000946), (566609, -2000954),
        (566610, -2000530), (626581, -2000350), (628357, -2003400), (628359, -2003400), (628366, -2003404), (628367, -2000710), (668628, -2000067), (668634, -2000068),
        (668651, -2000945), (669213, -2000260), (677680, -2001350), (677695, -2001350), (690047, -2001000), (690080, -2001000), (693257, -2008037), (697612, -2000038),
        (699849, -2000500), (699850, -2000301), (699851, -2000279), (724430, -2000035), (725396, -2000920), (735724, -2000080), (735739, -2000080), (754016, -2001120),
        (757198, -2001120), (766598, -2001300), (766599, -2009643), (776839, -2001040), (778316, -2001040), (800213, -2000095), (800214, -2000095), (800282, -2000091),
        (800283, -2000262), (857184, -2000480), (857191, -2000160), (857192, -2000480), (857234, -2000079), (857393, -2000066), (859086, -2000450), (862910, -2000460),
        (929139, -2002000), (940773, -2000300), (940871, -2000450), (940956, -2000460), (941324, -2000550), (941493, -2000051), (941864, -2000150), (941866, -2000150),
        (945990, -2001360), (946164, -2001360), (947498, -2001265), (948077, -2001268), (948584, -2001269), (948595, -2001269), (950216, -2001370), (950367, -2001370),
        (957750, -2000192), (959386, -2005021), (959432, -2005021), (959491, -2000573), (959497, -2000570), (960064, -2000279), (960072, -2000560), (970620, -2000077),
        (974907, -2008190), (974927, -2008000), (974930, -2008020), (974932, -2008044), (974933, -2008050), (974934, -2008130), (974938, -2008132), (974959, -2000576),
        (1086641, -2000983), (1092955, -2000985), (1094660, -2001001), (1101888, -2000011), (1102649, -2006071),
    ];

    [SkippableFact]
    public void NativeHeaderCategoryId_EqualsTheReferenceHeaderCategory()
    {
        var byId = _fixture.Info.Elements.Where(e => !e.IsDiscovered && !e.IsBlockOnly).ToDictionary(e => e.Id);
        var wrong = MeshVectors.Where(v => !byId.TryGetValue(v.Id, out var e) || e.NativeHeaderCategoryId != v.Category).ToList();
        Assert.Empty(wrong);
    }

    [SkippableFact]
    public void EveryElementTableRow_HasAHeaderCategory()
    {
        Assert.Empty(_fixture.Info.Elements.Where(e => !e.IsDiscovered && !e.IsBlockOnly && e.NativeHeaderCategoryId == null).Select(e => e.Id));
    }
}

/// <summary>TESTFILE.rvt: some accepted records carry regeneration history entries (up to 69), which move the category
/// field. The vectors include the first and the last element of every entry count.</summary>
public class ElementHeaderCategoryTestFileTests : IClassFixture<ElementHeaderCategoryTestFileTests.Fixture>
{
    public sealed class Fixture
    {
        public RevitFileInfo Info { get; } = new RvtFileReader().Read(TestFileFixture.FindTestFile("TESTFILE.rvt"));
    }

    private readonly Fixture _fixture;
    public ElementHeaderCategoryTestFileTests(Fixture fixture) => _fixture = fixture;

    private static readonly (int Id, int Category)[] MeshVectors =
    [
        (2, -2000112), (24, -2000700), (268, -2000510), (343, -2000550), (349, -2000240), (355, -2000240), (356, -2000279), (358, -1),
        (382, -2000011), (386, -2000035), (388, -2001263), (390, -2000220), (399, -2000180), (401, -2000181), (402, -2000182), (403, -2000151),
        (407, -2000171), (444, -2001342), (445, -2003201), (562, -2000538), (567, -2000038), (706, -2000150), (743, -2002000), (1214, -2000127),
        (1218, -2003000), (1238, -2000032), (1398, -2000035), (1561, -2000400), (1735, -2000515), (1738, -2000011), (2305, -2000535), (2306, -2000278),
        (4664, -2000279), (4667, -2000510), (10658, -1), (62380, -1), (75995, -1), (75996, -1), (75997, -1), (105725, -1),
        (105802, -2009609), (119963, -1), (263500, -2001352), (263501, -2001352), (263502, -1), (263525, -2000500), (263527, -2000279), (263584, -2000011),
        (263585, -2009640), (263597, -2000011), (263598, -2009640), (263610, -2000011), (263611, -2009640), (263623, -2000011), (263624, -2009640), (263649, -2009645),
        (263650, -2009645), (263733, -2000011), (263734, -2009640), (263757, -2009645), (263865, -2000011), (263866, -2009640), (263877, -2009645), (263931, -2001352),
        (263932, -2001352), (263934, -1), (267711, -2009000), (267746, -2000151), (267748, -2000151), (267770, -2000576), (274213, -2001370), (274223, -2000151),
        (274439, -1), (280754, -1), (288970, -2000576), (289089, -2000151), (289108, -2000301), (289109, -2000279), (289117, -1), (289118, -2000151),
        (289128, -2000151), (289135, -2000151), (289136, -2000151), (289137, -2000151), (289159, -2000151), (289169, -2000151), (289184, -2009645), (289185, -2009645),
    ];

    [SkippableFact]
    public void NativeHeaderCategoryId_EqualsTheReferenceHeaderCategory_AlsoWithRegenHistoryEntries()
    {
        var byId = _fixture.Info.Elements.Where(e => !e.IsDiscovered && !e.IsBlockOnly).ToDictionary(e => e.Id);
        var wrong = MeshVectors.Where(v => !byId.TryGetValue(v.Id, out var e) || e.NativeHeaderCategoryId != v.Category).ToList();
        Assert.Empty(wrong);
    }
}
