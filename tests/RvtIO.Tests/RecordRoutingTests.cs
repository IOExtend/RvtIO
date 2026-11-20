using System.Text;
using OpenMcdf;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Global/DocumentIncrementTable decoded by the file's own schema, the routing of a modification episode to a partition
/// stream, and the removal of the records that are not the current copy.</summary>
public class RecordRoutingTests
{
    internal static byte[] Schema(bool emptyRows = false)
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        void Class(string name, params (string Name, byte Type, byte Mods, int Ref)[] fields)
        {
            w.Write((ushort)0);
            w.Write((ushort)name.Length); w.Write(Encoding.ASCII.GetBytes(name));
            w.Write((ushort)0); w.Write(1u); w.Write((uint)fields.Length);
            foreach (var f in fields)
            {
                w.Write((uint)f.Name.Length); w.Write(Encoding.ASCII.GetBytes(f.Name));
                w.Write(f.Type); w.Write(f.Mods); w.Write((ushort)0);
                if (f.Type == 0x0E && (f.Mods & 0x0F) == 0) w.Write((ushort)f.Ref);
            }
            w.Write(0u);
        }
        Class("EpisodeId", ("m_id", 4, 0, 0));                                                                                // 12
        Class("DocumentIncrementVersion", ("m_causedByIncrementNumber", 4, 0, 0), ("m_totalElements", 4, 0, 0));              // 13
        if (emptyRows) Class("DocumentIncrement");                                                                            // 14
        else Class("DocumentIncrement", ("m_incrementVersions", 0x0E, 0x50, 13), ("m_userName", 8, 0x60, 0),
            ("m_greatest", 0x0E, 0, 12), ("m_totalEpisodes", 4, 0, 0), ("m_totalElements", 4, 0, 0), ("m_overwrite", 1, 0, 0)); // 14
        Class("std::pair< EpisodeId, int >", ("first", 0x0E, 0, 12), ("second", 4, 0, 0));                                    // 15
        Class("DocumentIncrementTable", ("m_increments", 0x0E, 0x50, 14), ("m_localIncrements", 0x0E, 0x50, 14),
            ("m_permutation", 0x0E, 0x50, 15));                                                                               // 16
        w.Write(new byte[8]);
        return stream.ToArray();
    }

    /// <summary>One increment: greatest episode, element count, versions as (cause, count).</summary>
    internal static byte[] Table(IEnumerable<(int Greatest, int Total, (int Cause, int Count)[] Versions)> rows, string user = "u")
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        w.Write((ushort)16);
        var list = rows.ToList();
        for (int pass = 0; pass < 2; pass++)
        {
            w.Write((uint)list.Count);
            foreach (var (greatest, total, versions) in list)
            {
                w.Write((uint)versions.Length);
                foreach (var (cause, count) in versions) { w.Write(cause); w.Write(count); }
                w.Write((uint)user.Length); w.Write(Encoding.Unicode.GetBytes(user));
                w.Write(greatest); w.Write(greatest + 1); w.Write(total); w.Write((byte)1);
            }
        }
        w.Write(0u); // m_permutation
        w.Write(0u); // terminal
        return stream.ToArray();
    }

    private static DocumentIncrementTable.Table Parse(byte[] bytes)
    {
        var schema = FormatsSchema.TryParse(Schema())!;
        var table = DocumentIncrementTable.TryParse(bytes, schema, out var failure);
        Assert.Null(failure);
        return table!;
    }

    // the shape of a 2022 file: older increments are emptied and redirect to the increment that holds their elements
    private static readonly (int, int, (int, int)[])[] Rows =
    [
        (824, 0, [(-1, 78931), (1, 0)]),
        (826, 0, [(-1, 78931), (2, 0)]),
        (846, 85336, [(-1, 85336)]),
        (847, 50, [(-1, 50)]),
    ];

    [Fact]
    public void RoutesAnEpisodeToTheIncrementThatKeepsItsElements()
    {
        var table = Parse(Table(Rows));
        Assert.Equal(4, table.Increments.Count);
        Assert.True(table.Local.Zip(table.Increments).All(p => p.First.SameRouting(p.Second)));
        Assert.Equal(2, DocumentIncrementTable.Route(table, 0));      // increment 0 -> 1 -> 2
        Assert.Equal(2, DocumentIncrementTable.Route(table, 826));    // the greatest episode of increment 1
        Assert.Equal(2, DocumentIncrementTable.Route(table, 846));
        Assert.Equal(3, DocumentIncrementTable.Route(table, 847));
        Assert.Null(DocumentIncrementTable.Route(table, 848));        // beyond every increment
    }

    [Fact]
    public void ABrokenVersionHistoryDoesNotRoute()
    {
        // terminal count differs from the increment's own count
        Assert.Null(DocumentIncrementTable.Route(Parse(Table([(10, 5, [(-1, 7)])])), 1));
        // an empty increment that redirects backwards
        Assert.Null(DocumentIncrementTable.Route(Parse(Table([(10, 0, [(-1, 7), (0, 0)]), (20, 3, [(-1, 3)])])), 1));
        // an empty increment without versions
        Assert.Null(DocumentIncrementTable.Route(Parse(Table([(10, 0, [])])), 1));
        // an empty increment that redirects past the last increment
        Assert.Null(DocumentIncrementTable.Route(Parse(Table([(10, 0, [(-1, 0), (5, 0)]), (20, 3, [(-1, 3)])])), 1));
    }

    [Fact]
    public void ARedirectBeyondTheIntRangeEndsTheWalk()
    {
        // 4294967296 wraps to 0 under an int cast and would loop on the first increment forever
        var table = new DocumentIncrementTable.Table(
            [new DocumentIncrementTable.Increment(10, 0, 0, [new DocumentIncrementTable.Version(4294967296L, 0)])], [], []);
        Assert.Null(DocumentIncrementTable.Route(table, 1));
    }

    [Fact]
    public void ATableThatDoesNotFollowTheSchemaIsRejected()
    {
        var schema = FormatsSchema.TryParse(Schema())!;
        byte[] good = Table(Rows);
        Assert.NotNull(DocumentIncrementTable.TryParse(good, schema, out _));
        Assert.Null(DocumentIncrementTable.TryParse(good[..^6], schema, out var truncated));
        Assert.NotNull(truncated);
        byte[] wrongClass = (byte[])good.Clone(); wrongClass[0] = 17;
        Assert.Null(DocumentIncrementTable.TryParse(wrongClass, schema, out _));
        Assert.Null(DocumentIncrementTable.TryParse([.. good, 1], schema, out var trailing));
        Assert.Equal("trailing bytes", trailing);
    }

    [Fact]
    public void ACountBeyondTheNodeBudgetIsRejectedBeforeAnyRowIsBuilt()
    {
        // rows without fields take no bytes: three containers of 60 rows each exceed the 64 nodes a 64-byte stream can hold
        var schema = FormatsSchema.TryParse(Schema(emptyRows: true))!;
        var bytes = new byte[64];
        BitConverter.GetBytes((ushort)16).CopyTo(bytes, 0);
        for (int container = 0; container < 3; container++) BitConverter.GetBytes(60u).CopyTo(bytes, 2 + 4 * container);
        Assert.Null(DocumentIncrementTable.TryParse(bytes, schema, out var failure));
        Assert.Equal("container count", failure);
        // a count that exceeds the stream is rejected as well
        BitConverter.GetBytes(uint.MaxValue).CopyTo(bytes, 2);
        Assert.Null(DocumentIncrementTable.TryParse(bytes, schema, out failure));
        Assert.Equal("container count", failure);
    }

    [Fact]
    public void MutatedTablesNeverThrowAndStayInsideTheBudget()
    {
        var schema = FormatsSchema.TryParse(Schema())!;
        byte[] good = Table(Rows);
        var random = new Random(32);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int round = 0; round < 3000; round++)
        {
            var bytes = (byte[])good.Clone();
            for (int flips = random.Next(1, 4); flips > 0; flips--)
            {
                // a random byte, and in half of the cases the three bytes behind it set to 0xFF (a huge count when it hits a count field)
                int at = random.Next(2, bytes.Length);
                bytes[at] = (byte)random.Next(256);
                if (random.Next(2) == 0 && at + 3 < bytes.Length) { bytes[at + 1] = 0xFF; bytes[at + 2] = 0xFF; bytes[at + 3] = 0xFF; }
            }
            // a failure is a null table with a reason, never an exception
            var table = DocumentIncrementTable.TryParse(bytes, schema, out var failure);
            Assert.True(table != null || failure != null);
        }
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"decoding took {watch.Elapsed}");
    }

    [Fact]
    public void TheTableIsFollowedOnlyWhereItNamesTheLatestHolderOrTheCopiesAreIdentical()
    {
        var schema = FormatsSchema.TryParse(Schema())!;
        var table = Parse(Table(Rows));   // episodes 827 to 846 route to increment 2, 847 to increment 3
        var census = new RecordRouting.Census { Schema = schema, Tags = new PartitionFraming.Tags(1, 2, 3, 4, 5), IdWidth = 4 };
        census.StreamLengths[0] = 5000; census.StreamLengths[2] = 1000; census.StreamLengths[3] = 10;
        void Holder(ulong owner, int partition, byte digest)
        {
            if (!census.Holders.TryGetValue(owner, out var list)) census.Holders[owner] = list = [];
            list.Add(partition);
            census.Digests[(owner, partition)] = [digest];
        }
        Holder(1, 0, 1); Holder(1, 2, 2);   // differing copies, route 2 holds one: 2
        Holder(2, 0, 1); Holder(2, 3, 2);   // differing copies, route 2 holds none: not proven, every copy stays
        Holder(6, 0, 1); Holder(6, 2, 2); Holder(6, 3, 3);   // differing copies, route 2 holds one but is not the latest: every copy stays
        Holder(7, 0, 1); Holder(7, 2, 5); Holder(7, 3, 5);   // route 2 holds the bytes of the latest copy: the latest, 3
        Holder(3, 2, 7); Holder(3, 3, 7);   // identical copies, route 3: the copy in the longer stream, 2
        Holder(4, 3, 9);                    // one copy: not repeated
        Holder(5, 0, 1); Holder(5, 2, 2);   // no ElemTable row: left alone
        var rows = new List<ElemTableLayout.Row>
        {
            new(1, 1, 0, 0, -1, 830), new(2, 2, 0, 0, -1, 830), new(3, 3, 0, 0, -1, 847), new(4, 4, 0, 0, -1, 847), new(6, 6, 0, 0, -1, 830), new(7, 7, 0, 0, -1, 830),
        };
        var current = RecordRouting.Current(census, rows, table);
        Assert.Equal(new Dictionary<ulong, int> { [1] = 2, [3] = 2, [7] = 3 }, current);
    }

    [Fact]
    public void RemovesRecordRangesAcrossChunkBoundaries()
    {
        byte[][] chunks = [[0, 1, 2, 3, 4], [5, 6, 7, 8, 9], [10, 11, 12]];
        int[] lengths = [5, 5, 3];
        // ranges: [1,3) inside a chunk, [4,7) across the first boundary, [9,13) across the second and to the end
        var pruned = RecordRouting.Prune(chunks, lengths, [(1, 2), (4, 3), (9, 4)])!;
        Assert.Equal([[0, 3], [7, 8], []], pruned.Select(c => c.ToArray()).ToArray());
        // no ranges keep every byte; a chunk no range reaches is the same array
        Assert.Equal(chunks.Select(c => c.ToArray()), RecordRouting.Prune(chunks, lengths, [])!.Select(c => c.ToArray()));
        var first = RecordRouting.Prune(chunks, lengths, [(1, 2)])!;
        Assert.Same(chunks[1], first[1]);
        Assert.Same(chunks[2], first[2]);
        Assert.Equal([0, 3, 4], first[0]);
        // chunks that are not the framed segments are not touched
        Assert.Null(RecordRouting.Prune(chunks, [5, 5, 4], [(1, 2)]));
        Assert.Null(RecordRouting.Prune(chunks, [5, 5], [(1, 2)]));
    }

    [Fact]
    public void DropsTheFramedRecordsThatAreNotTheCurrentCopy()
    {
        var items = new List<PartitionFraming.ProjectItem> { new(101, 1, 0, 20), new(101, 2, 20, 20), new(102, 1, 40, 24), new(102, 2, 64, 24) };
        RecordRouting.Drop(items, [(20, 20), (64, 24)]);
        Assert.Equal([new(101, 1, 0, 20), new(102, 1, 40, 24)], items);
    }

    /// <summary>TESTFILE.rvt is a Revit 2022 project saved in five increments (partition streams 0, 1, 3, 4, 5). Measured
    /// on the file: the null-key map holds 8050 + 88 + 1 + 90 + 0 element records, 68 element ids occur in more than one
    /// partition, and the 50,851 keyed records of the loaded families never carry a project element id.</summary>
    [SkippableFact]
    public void TheIncrementsOfARealFileRouteEveryRepeatedElementToAPartitionThatHoldsIt()
    {
        string path = TestFiles.Find("TESTFILE.rvt");
        using var root = RootStorage.OpenRead(path);
        var schema = FormatsSchema.TryRead(root)!;
        var rows = ElemTableLayout.TryParse(PagedGzipStream.Read(root, "Global", "ElemTable")!, schema)!;
        var table = DocumentIncrementTable.TryParse(PagedGzipStream.Read(root, "Global", "DocumentIncrementTable")!, schema, out var failure)!;
        Assert.Null(failure);
        Assert.Equal(6, table.Increments.Count);
        var census = RecordRouting.Begin(schema, out failure)!;
        Assert.Null(failure);
        var storage = root.OpenStorage("Partitions");
        foreach (int partition in new[] { 0, 1, 3, 4, 5 })
        {
            using var stream = storage.OpenStream(partition.ToString());
            var raw = new byte[stream.Length];
            stream.ReadExactly(raw);
            Assert.True(RecordRouting.Collect(census, partition, raw, out failure), failure);
        }
        Assert.Equal(8050 + 88 + 1 + 90, census.Items.Count / 3);
        Assert.Equal(50851, census.KeyedRecords);
        var ids = rows.Select(r => (ulong)r.Id).ToHashSet();
        Assert.Equal(4, census.Holders.Keys.Count(id => !ids.Contains(id))); // records of ids the ElemTable does not declare stay in place
        var current = RecordRouting.Current(census, rows, table);
        Assert.Equal(68, census.Holders.Count(h => h.Value.Count > 1 && ids.Contains(h.Key)));
        Assert.Equal(68, current.Count); // the table names the latest holder, or a copy with its bytes, for every one of them
        Assert.All(current, pair => Assert.Contains(pair.Value, census.Holders[pair.Key]));
        // the records dropped are exactly the copies of the other partitions, three channels each
        int stale = new[] { 0, 1, 3, 4, 5 }.Sum(p => RecordRouting.Stale(census, current, p).Count);
        Assert.Equal(census.Items.Count(i => current.ContainsKey(i.Owner)) - 3 * current.Count, stale);
    }
}
