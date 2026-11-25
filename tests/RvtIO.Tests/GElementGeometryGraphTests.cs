using System.Text.Json;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class GElementGeometryGraphTests
{
    [Fact]
    public void CompleteFifoResolvesOwnedSurfacesByQueueIdentityDespiteDuplicateHandles()
    {
        var (bytes, offsets) = Block();
        var graph = Read(bytes, out var failure)!;
        Assert.Null(failure); Assert.Equal(93, graph.OwnerId); Assert.Equal(0xdecafbadU, graph.OpaqueHeader);
        Assert.Equal(bytes.Length - 4, graph.End);
        Assert.Equal(new ushort[] { 2007,2092,1589,1589,1243,1254,522,1254,983 },
            graph.Nodes.Select(n => n.ClassId));
        Assert.Equal(Enumerable.Range(-1,9), graph.Nodes.Select(n => n.QueueIndex));
        Assert.All(graph.Nodes.Skip(1), n => Assert.Equal(7u,n.Handle));
        Assert.Equal(new[] { (1,5), (2,7) }, graph.FaceSurfaces.Select(l => (l.FaceQueueIndex,l.SurfaceQueueIndex)));
        Assert.Equal(offsets["plane"],graph.Nodes[6].Start);
        Assert.Equal(offsets["cylinder"],graph.Nodes[8].Start);
        Assert.Equal(2u,Field(graph.Nodes[1],"m_pFaces").Count);
        Assert.Equal(1u,Field(graph.Nodes[1],"m_pEdges").Count);
        Assert.Equal(uint.MaxValue,Field(graph.Nodes[1],"m_sharedSurfInfo").Count);
        Assert.Equal(new long[] { -1,int.MinValue,int.MaxValue,uint.MaxValue },
            Field(graph.Nodes[2],"m_GInfo").Items.Single().Values);
        var edge = graph.Nodes[4];
        Assert.Equal(new long[] { 7,uint.MaxValue },Field(edge,"m_pFace").Items.SelectMany(i=>i.Values));
        Assert.All(Field(edge,"m_pFace").Items,i=>Assert.Null(i.Pointer)); // raw backrefs, no guessed resolution
        var interior = Field(edge,"m_interiorEdgePnts");
        Assert.Equal(1u,interior.Count); Assert.Equal(36,interior.End-interior.Start);
        Assert.Equal(32,interior.Items.Single().End-interior.Items.Single().Start);
        Assert.Equal(64,Field(edge,"m_firstAndLastEdgePnts").End-Field(edge,"m_firstAndLastEdgePnts").Start);
        Assert.Equal(255,Field(edge,"m_flags").Items.Single().Values.Single());
        var planeEnvelope = Field(graph.Nodes[6],"m_Envelope").Items.Single().Fields.Single();
        Assert.Equal(2u,planeEnvelope.Count); Assert.All(planeEnvelope.Items,i=>Assert.Equal(2,i.Values.Count));
        Assert.All(graph.Nodes.Zip(graph.Nodes.Skip(1)),pair=>Assert.Equal(pair.First.End,pair.Second.Start));
    }

    [Fact]
    public void ExceptionalDoubleBitsAreKeptWithoutFiniteOrShapeFilters()
    {
        var (bytes,_) = Block(); var graph = Read(bytes,out _)!;
        Assert.Equal(Bits,Field(graph.Nodes[6],"m_origin").Items.SelectMany(i=>i.Values));
        Assert.Equal(long.MinValue,Field(graph.Nodes[8],"m_radius").Items.Single().Values.Single());
        Assert.Equal(uint.MaxValue,Field(graph.Nodes[1],"m_flags").Items.Single().Values.Single() & uint.MaxValue);
    }

    [Fact]
    public void InlinePrivateChainsFillPlacerAndInstanceTransformHaveExactWidthsAndFieldOrder()
    {
        using var s=new MemoryStream();using var w=new BinaryWriter(s);
        void Pointer(ushort cls){w.Write(uint.MaxValue);w.Write(cls);}
        void Uv(){foreach(var bit in Bits.Take(2).Concat(Bits.Take(2)))w.Write(bit);}
        Header(w);Info(w);w.Write(3u);Pointer(2014);Pointer(1257);Pointer(2238);
        w.Write(new byte[96]);w.Write(0);w.Write(0u);
        // GFilling: persisted sort is patternId before fillColor, unlike declaration calls.
        Info(w);w.Write(17u);w.Write(Bits[0]);foreach(var bit in Bits.Take(2))w.Write(bit);
        foreach(var bit in Bits.Take(2))w.Write(bit);foreach(var bit in Bits.Take(2))w.Write(bit);
        w.Write((byte)255);w.Write((byte)0);Pointer(1850);w.Write(-777);w.Write(0xfedcba98u);w.Write(int.MinValue);
        // EdgeLoopWithChainEnvelopes: inherited loop then one inline chain.
        Info(w);w.Write(0u);w.Write(17u);w.Write(19u);w.Write(21u);Uv();w.Write((byte)1);
        w.Write(1u);w.Write(31u);Uv();
        // InstanceInfo: Trf = 3*XYZ matrix + XYZ origin; scalar ElementId and nonowning doc.
        for(int i=0;i<4;i++)foreach(var bit in Bits)w.Write(bit);
        w.Write(-123);w.Write(uint.MaxValue);
        // Deferred FillPatternData and its deferred FillGrid.
        for(int i=0;i<4;i++)w.Write(Bits[1]);w.Write(1u);Pointer(1848);
        w.Write(Bits[2]);foreach(var bit in Bits.Take(2))w.Write(bit);foreach(var bit in Bits.Take(2))w.Write(bit);
        w.Write(2u);w.Write(Bits[0]);w.Write(Bits[1]);w.Write(0u);
        var bytes=s.ToArray();Frame(bytes);var graph=Read(bytes,out var failure)!;
        Assert.Null(failure);Assert.Equal(6,graph.Nodes.Count);Assert.Empty(graph.FaceSurfaces);
        var fill=graph.Nodes[1];
        Assert.Equal(-777,Field(fill,"m_patternId").Items.Single().Fields.Single().Items.Single().Values.Single());
        Assert.Equal(0xfedcba98L,Field(fill,"m_fillColor").Items.Single().Values.Single());
        var placer=Field(fill,"m_placer").Items.Single();Assert.Equal(58,placer.End-placer.Start);
        var chains=Field(graph.Nodes[2],"m_chainsWithEnvelopes");Assert.Equal(1u,chains.Count);
        Assert.Equal(36,chains.Items.Single().End-chains.Items.Single().Start);
        var trf=Field(graph.Nodes[3],"m_Trf").Items.Single();Assert.Equal(96,trf.End-trf.Start);
        Assert.Equal(3u,trf.Fields.Single(f=>f.Name=="m_3x3").Count);
        Assert.All(trf.Fields.Single(f=>f.Name=="m_3x3").Items,i=>Assert.Equal(Bits,i.Values));
        Assert.Equal(2u,Field(graph.Nodes[5],"m_segs").Count);
    }

    [Theory]
    [InlineData(2015)] [InlineData(2009)] [InlineData(1996)] [InlineData(1999)]
    [InlineData(1736)] [InlineData(1974)] [InlineData(1991)]
    public void OtherAdmittedPhysicalKindsConsumeTheirOwnFieldsNotAnUnconfirmedTail(int cls)
    {
        using var s=new MemoryStream();using var w=new BinaryWriter(s);
        Header(w);Info(w);w.Write(1u);w.Write(uint.MaxValue);w.Write((ushort)cls);
        w.Write(new byte[96]);w.Write(0);w.Write(0u);
        if(cls is 2015 or 2009 or 1736 or 1974 or 1991)Info(w);
        if(cls is 2015 or 2009)w.Write(uint.MaxValue);
        if(cls==2015)w.Write(0u);
        if(cls is 1996 or 1999)w.Write(int.MinValue);
        if(cls==1996){foreach(var bit in Bits)w.Write(bit);w.Write((byte)255);}
        if(cls==1999){w.Write(int.MinValue);w.Write(int.MaxValue);}
        if(cls is 1736 or 1974){w.Write(Bits[0]);w.Write(Bits[1]);}
        if(cls==1736){foreach(var bit in Bits)w.Write(bit);foreach(var bit in Bits)w.Write(bit);}
        if(cls==1974){for(int i=0;i<2;i++)foreach(var bit in Bits)w.Write(bit);w.Write(Bits[0]);foreach(var bit in Bits)w.Write(bit);w.Write((byte)255);}
        if(cls==1991){w.Write(77u);w.Write((ushort)0);}
        w.Write(0u);var bytes=s.ToArray();Frame(bytes);var graph=Read(bytes,out var failure)!;
        Assert.Null(failure);Assert.Equal((ushort)cls,graph.Nodes[1].ClassId);Assert.Equal(bytes.Length-4,graph.End);
    }

    [Fact]
    public void EveryByteTruncationIncludingLaterPayloadRejectsAtomicallyWithReframedFooter()
    {
        var (bytes,_) = Block();
        for (int end=14;end<bytes.Length-4;end++)
        {
            var cut = bytes.AsSpan(0,end).ToArray().Concat(new byte[4]).ToArray(); Frame(cut);
            Assert.Null(Read(cut,out var failure)); Assert.NotNull(failure);
        }
    }

    [Theory]
    [InlineData("faces")] [InlineData("edges")] [InlineData("interior")]
    public void CountLimitAndRemainingBytesAreBothEnforced(string key)
    {
        foreach(uint count in new[] { 100001u,100000u })
        {
            var (bytes,offsets) = Block(); BitConverter.GetBytes(count).CopyTo(bytes,offsets[key]);
            Assert.Null(Read(bytes,out var failure)); Assert.NotNull(failure);
        }
    }

    [Theory]
    [InlineData("owner")] [InlineData("size")] [InlineData("footer")] [InlineData("class")]
    [InlineData("unknown-kind")] [InlineData("unsupported-kind")] [InlineData("surface-kind")]
    public void InvalidFrameAndLaterKindAreNotSkipped(string mutation)
    {
        var (bytes,offsets) = Block();
        switch(mutation)
        {
            case "owner": BitConverter.GetBytes(-1).CopyTo(bytes,0); break;
            case "size": bytes[8]^=1; break;
            case "footer": bytes[^1]^=1; break;
            case "class": bytes[12]^=1; break;
            case "unknown-kind": BitConverter.GetBytes(ushort.MaxValue).CopyTo(bytes,offsets["surface-pointer"]+4); break;
            case "unsupported-kind": BitConverter.GetBytes((ushort)523).CopyTo(bytes,offsets["surface-pointer"]+4); break;
            case "surface-kind": BitConverter.GetBytes((ushort)1736).CopyTo(bytes,offsets["surface-pointer"]+4); break;
        }
        Assert.Null(Read(bytes,out var failure)); Assert.NotNull(failure);
        if (mutation == "unknown-kind") Assert.Equal($"field:1589:m_pSurf:{offsets["surface-pointer"]}", failure);
        if (mutation == "unsupported-kind") Assert.Equal("fifo:5:523", failure);
    }

    [Theory]
    [InlineData(-1)] [InlineData(int.MaxValue)]
    public void InvalidStartDoesNotOverflowBounds(int start)
    {
        var (bytes,_) = Block();
        Assert.Null(TfBlockDeserializer.ExtractOwnGElementGeometryGraph(bytes,start,
            BitConverter.ToUInt32(bytes,8),2007,out var failure)); Assert.Equal("frame",failure);
    }

    [Theory]
    [InlineData(uint.MaxValue)] [InlineData(0x80000000u)]
    public void InvalidSizeDoesNotOverflowBounds(uint size)
    {
        var (bytes,_) = Block();
        Assert.Null(TfBlockDeserializer.ExtractOwnGElementGeometryGraph(bytes,0,size,2007,out var failure));
        Assert.Equal("frame",failure);
    }

    [Fact]
    public void NonzeroStartRetainsIdenticalFrameRelativeValuesAndSpans()
    {
        var (bytes,_) = Block(); var larger = new byte[bytes.Length+31]; bytes.CopyTo(larger,17);
        var actual = TfBlockDeserializer.ExtractOwnGElementGeometryGraph(larger,17,
            BitConverter.ToUInt32(bytes,8),2007,out var failure);
        Assert.Null(failure); Assert.Equal(JsonSerializer.Serialize(Read(bytes,out _)),JsonSerializer.Serialize(actual));
    }

    [Fact]
    public void ExtraPayloadByteIsNotAcceptedAsACompleteGraph()
    {
        var (bytes,_) = Block(); var extra=bytes[..^4].Concat(new byte[5]).ToArray(); Frame(extra);
        Assert.Null(Read(extra,out var failure)); Assert.Equal("tail",failure);
    }

    [Theory]
    [InlineData(0u)] [InlineData(uint.MaxValue)]
    public void EmptyRootHasExactFooterAndDistinctEmptyVersusNullCounts(uint count)
    {
        using var s=new MemoryStream(); using var w=new BinaryWriter(s);
        Header(w); Info(w); w.Write(count); w.Write(new byte[96]); w.Write(0); w.Write(0u); w.Write(0u);
        var bytes=s.ToArray(); Frame(bytes); var graph=Read(bytes,out var failure)!;
        Assert.Null(failure); Assert.Single(graph.Nodes); Assert.Empty(graph.FaceSurfaces);
        Assert.Equal(count,Field(graph.Nodes[0],"m_subNodes").Count); Assert.Equal(bytes.Length-4,graph.End);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void NullExternalAndHandPointersDoNotAllocateDeferredIdentities(int kind)
    {
        using var s=new MemoryStream(); using var w=new BinaryWriter(s);
        Header(w); Info(w); w.Write(1u);
        w.Write(kind==0?0u:7u);
        if(kind==1)w.Write((ushort)0);
        if(kind==2) { w.Write((ushort)4); w.Write((ushort)0x0108); foreach(var bit in Bits.Concat(Bits))w.Write(bit); w.Write(uint.MaxValue);w.Write(int.MinValue); }
        w.Write(new byte[96]);w.Write(0);w.Write(0u);w.Write(0u);
        var bytes=s.ToArray();Frame(bytes);var graph=Read(bytes,out var failure)!;
        Assert.Null(failure);Assert.Single(graph.Nodes);
        var pointer=Field(graph.Nodes[0],"m_subNodes").Items.Single().Pointer!;
        Assert.Null(pointer.QueueIndex);
        if(kind==2) { Assert.Equal((ushort)0x0108,pointer.HandToken);Assert.Equal(8,pointer.HandBits.Count); }
    }

    [Theory]
    [InlineData(0x0009)] [InlineData(0x0208)] [InlineData(0x0109)]
    public void UnsupportedHandTokenRejectsWithoutPartialGraph(int token)
    {
        using var s=new MemoryStream(); using var w=new BinaryWriter(s);
        Header(w);Info(w);w.Write(1u);w.Write(7u);w.Write((ushort)4);w.Write((ushort)token);
        w.Write(new byte[96]);w.Write(0);w.Write(0u);w.Write(0u);var bytes=s.ToArray();Frame(bytes);
        Assert.Null(Read(bytes,out var failure));Assert.NotNull(failure);
    }

    private static readonly long[] Bits=[long.MinValue,0x7ff8000000000042L,0x7ff0000000000000L];
    private static TfBlockDeserializer.GElementGraphField Field(TfBlockDeserializer.GElementGraphNode node,string name)
        =>node.Fields.Single(f=>f.Name==name);
    private static TfBlockDeserializer.OwnGElementGeometryGraph? Read(byte[] bytes,out string? failure)
        =>TfBlockDeserializer.ExtractOwnGElementGeometryGraph(bytes,0,BitConverter.ToUInt32(bytes,8),
            BitConverter.ToUInt16(bytes,12),out failure);
    private static void Frame(byte[] bytes)
    {uint size=(uint)bytes.Length-16;BitConverter.GetBytes(size).CopyTo(bytes,8);BitConverter.GetBytes(size).CopyTo(bytes,bytes.Length-4);}
    private static void Header(BinaryWriter w)
    {w.Write(93);w.Write(0xdecafbadU);w.Write(0u);w.Write((ushort)2007);}
    private static void Info(BinaryWriter w)
    {w.Write(-1);w.Write(int.MinValue);w.Write(int.MaxValue);w.Write(uint.MaxValue);}

    private static (byte[] Bytes,Dictionary<string,int> Offsets) Block()
    {
        using var s=new MemoryStream();using var w=new BinaryWriter(s);var offsets=new Dictionary<string,int>();
        void Mark(string key)=>offsets[key]=(int)s.Position;
        void Pointer(ushort cls){w.Write(7u);w.Write(cls);}
        void Uv(){foreach(var bit in Bits.Take(2).Concat(Bits.Take(2)))w.Write(bit);}
        void Face(bool first)
        {Info(w);Pointer(1254);w.Write(uint.MaxValue);w.Write(0u);w.Write(0u);w.Write(-7);w.Write(uint.MaxValue);w.Write(int.MinValue);
            if(first)Mark("surface-pointer");Pointer(first?(ushort)522:(ushort)983);}
        void Loop(){Info(w);w.Write(0u);w.Write(7u);w.Write(uint.MaxValue);w.Write(0u);Uv();w.Write((byte)255);}
        Header(w);Info(w);w.Write(1u);Pointer(2092);w.Write(new byte[96]);w.Write(int.MinValue);w.Write(uint.MaxValue);
        Info(w);Mark("faces");w.Write(2u);Pointer(1589);Pointer(1589);w.Write(-1);w.Write(int.MinValue);w.Write(int.MaxValue);w.Write(-1);
        Mark("edges");w.Write(1u);Pointer(1243);w.Write(uint.MaxValue);
        Face(true);Face(false);Info(w);
        for(int i=0;i<3;i++){w.Write(7u);w.Write(uint.MaxValue);}
        Mark("interior");w.Write(1u);Uv();Uv();Uv();w.Write((byte)255);
        Loop();Mark("plane");Uv();w.Write((byte)255);for(int i=0;i<3;i++)foreach(var bit in Bits)w.Write(bit);
        Loop();Mark("cylinder");Uv();w.Write((byte)0);for(int i=0;i<4;i++)foreach(var bit in Bits)w.Write(bit);w.Write(long.MinValue);
        w.Write(0u);var bytes=s.ToArray();Frame(bytes);return(bytes,offsets);
    }
}
