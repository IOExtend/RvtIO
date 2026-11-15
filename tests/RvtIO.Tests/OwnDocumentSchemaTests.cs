using System.Text;
using RvtIO.Compact;
using RvtIO.Internal;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class OwnDocumentSchemaTests
{
    private static readonly Dictionary<(int,string),int> Refs = new()
    {
        [(561,"m_folderNameToIdMap")]=562,[(562,"second")]=563,[(561,"m_setExpandedNodes")]=564,
        [(115,"m_mapDuctLevelToOffsets")]=116,[(116,"second")]=117,[(115,"m_sysNavStateData")]=118,
        [(118,"m_mapColumnSettings")]=119,[(119,"second")]=120,[(120,"m_sysNavColumnSettings")]=121,
        [(121,"m_columnArray")]=122,[(32,"m_addInNames")]=33,[(33,"first")]=34,[(34,"m_value")]=30,
        [(1365,"m_elemIdSetMap")]=1366,[(1366,"second")]=1367,[(1367,"m_idSet")]=20,
        [(3752,"second")]=20,[(1227,"m_modifyingUpdaterInfos")]=1228,[(1228,"first")]=1229,
        [(1228,"second")]=1230,[(1229,"m_value")]=30,[(2633,"m_itemSet")]=2634,[(2634,"m_bindingIds")]=20,
        [(24,"second")]=20,
        [(501,"m_mIdToBasis")]=502,[(502,"second")]=503,[(503,"m_creationEpisode")]=504,
        [(501,"m_mBasisToIdSet")]=505,[(505,"second")]=506,[(506,"m_sCommonBasisIds")]=20,
    };
    private static readonly ushort[] AppInfos=[561,115,32,1365,4197,1227,2633,501,4030];
    private static readonly (string Key,string Value)[] Entries =
    [ ("opaque#key-1.0.0","{\n\t\"typeid\":\"opaque#key-1.0.0\",\"v\":-0.0}"),
      ("я\U0001F600\uFFFD","unparsed \0 text \U0001F600\r\n") ];

    [Fact]
    public void EveryNonemptyNewRecordAndMapHasExactFieldsAndVerbatimValues()
    {
        var f=Fixture();var r=Read(f.Data,out var error);Assert.True(r!=null,error);Assert.NotNull(r);
        Assert.Equal(f.Start,r.Start);Assert.Equal(f.End,r.End);Assert.Equal(f.Fields,r.Fields);
        Assert.Equal(f.Objects,r.Objects);Assert.Equal(AppInfos.Length+2,r.QueueIndex);
        foreach(var entry in r.Units.Entries.Zip(Entries))
            Assert.Equal(entry.Second,(entry.First.Key,entry.First.Value));
        Assert.Equal(Entries[1],(Assert.Single(r.Parameters.Entries).Key,r.Parameters.Entries[0].Value));
        foreach(var key in Refs.Keys)
            Assert.Contains(r.Fields,x=>(x.Declaring,x.Name)==key && x.End>x.Start);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void BasisMapsHaveInlineElementEpisodeAndOriginalIdsAndCountedIdSets(int count)
    {
        var fixture = Fixture();
        var tracker = Read(fixture.Data, out _)!.Objects.Single(o => o.ClassId == 501);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(1u); // document backreference
        writer.Write((uint)count);
        for (int i = 0; i < count; i++)
        {
            writer.Write(876493 + i); // element id
            writer.Write(738); // creation episode
            writer.Write(875644 + i); // original element id
        }
        writer.Write((uint)count);
        for (int i = 0; i < count; i++)
        {
            writer.Write(738);
            writer.Write(875644 + i);
            writer.Write(2u);
            writer.Write(876493 + i);
            writer.Write(900000 + i);
        }
        byte[] data = [..fixture.Data[..tracker.Start], ..stream.ToArray(), ..fixture.Data[tracker.End..]];
        var result = Read(data, out var failure);
        Assert.True(result != null, failure);
        Assert.Equal(data.Length, result!.End);
        Assert.Equal(Entries[0], (result.Units.Entries[0].Key, result.Units.Entries[0].Value));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StorageFollowsManagerArrayOrderIncludingBeforeUnits(bool first)
    {
        var f=Fixture(first:first);var r=Read(f.Data,out _);Assert.NotNull(r);
        Assert.Equal(first?2:AppInfos.Length+2,r.QueueIndex);Assert.Equal(f.End,r.End);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void MissingOrDuplicateManagerOwnedStorageRejects(int owners)=>Assert.Null(Read(Fixture(owners:owners).Data,out _));
    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(uint.MaxValue)]
    public void StorageBackreferenceMustBeRoot(uint value)
    {var f=Fixture();BitConverter.GetBytes(value).CopyTo(f.Data,f.Start);Assert.Null(Read(f.Data,out _));}
    [Theory]
    [InlineData(100001u)]
    [InlineData(uint.MaxValue-1)]
    public void MapCountsAreBoundedBeforeAllocation(uint count)
    {
        var f=Fixture();var r=Read(f.Data,out _)!;
        foreach(int p in new[]{r.Units.Start,r.Parameters.Start})
        {byte[] data=[..f.Data];BitConverter.GetBytes(count).CopyTo(data,p);Assert.Null(Read(data,out _));}
    }
    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    public void EmptyMapsAcceptBothSentinels(uint sentinel)
    {var r=Read(Fixture(empty:sentinel).Data,out _);Assert.NotNull(r);Assert.Empty(r.Units.Entries);Assert.Empty(r.Parameters.Entries);}
    [Fact]
    public void DuplicateKeysRejectWholePrefix()
        =>Assert.Null(Read(Fixture(entries:[Entries[0],Entries[0]]).Data,out _));
    [Fact]
    public void MapStringsStrictUtf16AndBounded()
    {
        var f=Fixture();var r=Read(f.Data,out _)!;
        foreach(var entry in r.Units.Entries.Concat(r.Parameters.Entries))
        foreach(int p in new[]{entry.Start,entry.Start+4+entry.Key.Length*2})
        {
            byte[] data=[..f.Data];data[p+4]=0;data[p+5]=0xD8;Assert.Null(Read(data,out _));
            BitConverter.GetBytes(10001u).CopyTo(data,p);Assert.Null(Read(data,out _));
        }
    }
    [Fact]
    public void AllTruncationsRejectWithoutResyncOrSuffixAcceptance()
    {
        var f=Fixture();for(int i=0;i<f.End;i++)Assert.Null(Read(f.Data[..i],out _));
        Assert.Equal(f.End,Read([..f.Data,9,8,7],out _)!.End);
        Assert.Null(Read([9,8,..f.Data],out _));
    }
    [Fact]
    public void DeferredNewCollectionsBackreferencesAndBoolsRejectMalformedData()
    {
        var f=Fixture();
        foreach(var span in f.Fields)
        {
            var field=TfBlockDeserializer.Schemas[span.Declaring].Fields.Single(x=>x.Name==span.Name);
            byte[] data=[..f.Data];
            if(field.Mods>>4==5)BitConverter.GetBytes(100001u).CopyTo(data,span.Start);
            else if(field.Type==1)data[span.Start]=2;
            else if(span.Name=="m_pADoc")BitConverter.GetBytes(2u).CopyTo(data,span.Start);
            else continue;
            Assert.Null(Read(data,out _));
        }
    }

    [Theory]
    [InlineData("autodesk.spec.aec:length-1.0.0", "millimeters-1.0.1", "mm-1.0.1", "mm", 1, "304.80 mm")]
    [InlineData("autodesk.spec.aec:angle-1.0.0", "degrees-1.0.1", "degree-1.0.1", "°", Math.PI / 6, "30.00°")]
    [InlineData("autodesk.spec.aec.piping:pipeSize-1.0.0", "millimeters-1.0.1", "mm-1.0.1", "mm", 1, "304.80 mm")]
    [InlineData("autodesk.spec.aec.hvac:ductSize-1.0.0", "millimeters-1.0.1", "mm-1.0.1", "mm", 1, "304.80 mm")]
    public void FormatsBehindNonemptyBasisMapsConvertNumericSpecifications(string spec, string unit, string symbol, string text,
        double raw, string expected)
    {
        string symbolId = "autodesk.unit.symbol:" + symbol;
        string label = "{\"constants\":[{\"id\":\"text\",\"value\":\"" + text
            + "\"},{\"id\":\"placement\",\"value\":\"Suffix\"},{\"id\":\"space\",\"value\":"
            + (text == "°" ? "false" : "true") + "}]}";
        var recovered = Read(Fixture(entries: [(symbolId, label)]).Data, out var failure);
        Assert.True(recovered != null, failure);
        var state = new ParsingState();
        RvtFileReader.RecordOwnDocumentSchemas(state, recovered, null);
        var option = new TfBlockDeserializer.UnitFormatOption(spec, symbolId, "autodesk.unit.unit:" + unit,
            .01, 0, 0, 0, 0, 0, 0, 0);
        CompactFormatSettings Settings(TfBlockDeserializer.UnitFormatOption value)
            => CompactFormatSettings.Create(new(77, -1, 1, 0, 0, new([value], 1, 0, 1, 1)),
                state.OwnDocumentUnitConversions, state.OwnDocumentSchemas)!;

        Assert.Equal(expected, CompactValueFormatter.Format(raw, spec, Settings(option)));
        // These flags affect fractional layouts; decimal symbols keep their persisted spacing and leading zero.
        Assert.Equal(expected, CompactValueFormatter.Format(raw, spec, Settings(option with { SuppressSpaces = 1, SuppressLeadingZeros = 1 })));
        string suffix = text == "°" ? "°" : " mm";
        Assert.Equal("0.00" + suffix, CompactValueFormatter.Format(0, spec, Settings(option)));
        Assert.Equal("0" + suffix, CompactValueFormatter.Format(0, spec, Settings(option with { SuppressTrailingZeros = 1 })));
        Assert.Equal("0.00" + suffix, CompactValueFormatter.Format(0, spec, Settings(option with { SuppressLeadingZeros = 1 })));
        Assert.Null(CompactValueFormatter.Format(0, "unknown:spec-1.0.0", Settings(option)));
    }

    private static TfBlockDeserializer.DocumentSchemaPrefix? Read(byte[] data,out string? error)
        =>TfBlockDeserializer.ExtractOwnDocumentSchemas(data,out error);
    private sealed record FixtureData(byte[] Data,int Start,int End,
        TfBlockDeserializer.DocumentGraphField[] Fields,TfBlockDeserializer.DocumentGraphObject[] Objects);
    private static FixtureData Fixture(int owners=1,bool first=false,uint? empty=null,(string Key,string Value)[]? entries=null)
    {
        entries??=Entries;
        using var s=new MemoryStream();using var w=new BinaryWriter(s);
        var fields=new List<TfBlockDeserializer.DocumentGraphField>();var objects=new List<TfBlockDeserializer.DocumentGraphObject>();
        int Pos()=>(int)s.Position;
        void Ptr(uint h,ushort cls){w.Write(h);if(h!=0)w.Write(cls);}
        void String(string value){w.Write((uint)value.Length);w.Write(Encoding.Unicode.GetBytes(value));}
        w.Write((ushort)28);Ptr(0,0);w.Write(0u);Ptr(uint.MaxValue,821);w.Write(1u);Ptr(uint.MaxValue,338);
        for(int i=0;i<5;i++)Ptr(0,0);
        w.Write(-1);w.Write(-1);w.Write(17);w.Write(2662);w.Write(new byte[3]);w.Write(0u);w.Write(0u);Ptr(0,0);
        w.Write(1u);w.Write(0u);w.Write(1u);w.Write((uint)(AppInfos.Length+owners));
        ushort[] children=first?[..Enumerable.Repeat((ushort)1238,owners),..AppInfos]:[..AppInfos,..Enumerable.Repeat((ushort)1238,owners)];
        foreach(var cls in children)Ptr(uint.MaxValue,cls);
        void Record(int cls,int index,int depth=0)
        {
            Assert.True(depth<30);var chain=new List<int>();
            for(int? c=cls;c.HasValue;c=TfBlockDeserializer.Schemas[c.Value].Base)chain.Add(c.Value);
            foreach(int decl in chain.AsEnumerable().Reverse())
            foreach(var f in TfBlockDeserializer.Schemas[decl].Fields)
            {
                int start=Pos();int? reference=Refs.TryGetValue((decl,f.Name),out int r)?r:f.Ref;
                void Scalar()
                {
                    switch(f.Type)
                    {
                        case 1:w.Write((byte)1);break;
                        case 2:case 3:w.Write((byte)7);break;
                        case 4:case 5:case 6:w.Write(31);break;
                        case 7:case 10:w.Write(3.25);break;
                        case 8:String("я\U0001F600");break;
                        case 9:w.Write(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff").ToByteArray());break;
                        case 13:w.Write(new byte[f.Cnt!.Value*8]);break;
                        case 14:
                            int lo=f.Mods&15;
                            if(lo==3)w.Write(1u);
                            else if(lo is 1 or 2)Ptr(0,0);
                            else if(reference==20)w.Write(71);
                            else if(reference.HasValue)Record(reference.Value,index,depth+1);
                            else w.Write(0u);
                            break;
                        default:throw new InvalidOperationException($"Unexpected fixture type {decl}.{f.Name}");
                    }
                }
                int hi=f.Mods>>4;
                if(hi==6)String("я\U0001F600");
                else if(hi==5){bool nonempty=reference.HasValue || f.Type!=14;w.Write(nonempty?1u:0u);if(nonempty)Scalar();}
                else if(hi==1){for(int i=0;i<f.Cnt!.Value;i++)Scalar();}
                else Scalar();
                fields.Add(new(decl,f.Name,start,Pos(),index));
            }
        }
        int ownerStart=-1,end=-1;
        void Map((string Key,string Value)[] rows)
        {w.Write(empty??(uint)rows.Length);if(!empty.HasValue)foreach(var row in rows){String(row.Key);String(row.Value);}}
        for(int i=0;i<children.Length;i++)
        {
            if(children[i]==1238)
            {ownerStart=Pos();w.Write(1u);Map(entries);Map([Entries[1]]);end=Pos();break;}
            int start=Pos();Record(children[i],i+2);objects.Add(new(children[i],i+2,start,Pos()));
        }
        return new(s.ToArray(),ownerStart,end,fields.ToArray(),objects.ToArray());
    }
}
