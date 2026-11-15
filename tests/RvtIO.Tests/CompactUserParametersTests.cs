using RvtIO.Compact;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The user parameters of an element as the expected export's property list prints them.</summary>
public class CompactUserParametersTests
{
    private static TfBlockDeserializer.NativeParams Definition(int definitionClass, string caption, int group, string? spec = null, int? instance = null)
    {
        var np = new TfBlockDeserializer.NativeParams();
        np.Fields["2870:m_pParamDef.class"] = definitionClass;
        np.StringFields["301:m_caption"] = caption;
        np.Fields["301:m_groupElemId"] = group;
        np.Fields["301:m_readOnly"] = 0;
        np.Fields["301:m_userVisible"] = 1;
        np.StringFields["301:m_typeId"] = "autodesk.parameter.aec.revit.family:x-1.0.0";
        if (spec != null) np.StringFields["612:m_specTypeId"] = spec;
        if (instance != null) np.Fields["2874:m_instanceParam"] = instance.Value;
        return np;
    }

    private static TfBlockDeserializer.NamedParamRecord Named(int id, string? text = null, double number = 0, int elementId = 0, int integer = 0)
        => new(id, text, number, elementId, integer, true, false);

    /// <summary>One instance of a family whose instance parameters cover each storage kind.</summary>
    private static (RevitFileInfo Info, Element Instance) Sample(int yesNoValue = 1, int referenced = 77, string? referencedName = "Oak",
        double? depth = 0.8202099737532809)
    {
        var state = new ParsingState();
        void Add(int id, ushort cls, TfBlockDeserializer.NativeParams np)
        {
            state.ElemClassIdx[(uint)id] = cls; state.NativeParams[(uint)id] = np;
            if (cls == 2874) np.Fields["301:m_paramElemId"] = id;
        }
        // 10 text (no group), 11 text (group Other, same caption as 10), 12 yes/no, 13 integer, 14 material, 15 length, 16 url
        Add(10, 2874, Definition(606, "Note", -1, instance: 1));
        Add(11, 2874, Definition(606, "Note", -5000100, instance: 1));
        Add(12, 2874, Definition(2866, "Fire Rated", -5000100, instance: 1));
        Add(13, 2874, Definition(2756, "Count", -5000101, instance: 1));
        Add(14, 2874, Definition(2781, "Finish", -5000105, instance: 1));
        Add(15, 2874, Definition(612, "Depth", -5000101, "autodesk.spec.aec:length-1.0.0", 1));
        Add(16, 2874, Definition(2852, "Link", -5000100, instance: 1));
        Add(17, 2874, Definition(2752, "Picture", -5000104, instance: 1));
        Add(18, 2874, Definition(2848, "Description", -5000100, instance: 1));
        var family = new TfBlockDeserializer.NativeParams();
        family.Fields["1773:m_categoryId"] = -2000023;
        foreach (int id in new[] { 10, 11, 12, 13, 14, 15, 16, 17, 18 }) family.NamedParams.Add(new(id, null, 0, 0, 0, true, false));
        Add(100, 1772, family);
        var symbol = new TfBlockDeserializer.NativeParams();
        symbol.Fields["1829:m_familyId"] = 100;
        Add(101, 1829, symbol);
        var instance = new TfBlockDeserializer.NativeParams();
        instance.Fields["m_masterSymbolId"] = 101;
        instance.NamedParams.Add(Named(10, text: "a\tb \"quoted\""));
        instance.NamedParams.Add(Named(11, text: ""));
        instance.NamedParams.Add(Named(12, integer: yesNoValue));
        instance.NamedParams.Add(Named(13, integer: 33615));
        instance.NamedParams.Add(Named(14, elementId: referenced));
        if (depth is double raw) instance.NamedParams.Add(Named(15, number: raw));
        instance.NamedParams.Add(Named(16, text: "https://example.org"));
        instance.NamedParams.Add(Named(17, elementId: -1));   // an image parameter without an image (a stored image is left out, see CompactOwnValueRulesTests)
        instance.NamedParams.Add(Named(18, text: "line 1\r\nline 2"));
        Add(102, 1794, instance);
        var info = new RevitFileInfo();
        info.Elements.Add(new Element { Id = 77, Name = referencedName, SerializedOwnElementClassId = 2486, SerializedMaterialName = referencedName });
        var element = new Element { Id = 102, SerializedOwnElementClassId = 1794 };
        info.Elements.Add(element);
        info.UserParameters = UserParameterCatalog.Build(state);
        return (info, element);
    }

    [Fact]
    public void Entries_HaveTheCaptionAsLabelTheGroupLabelOrInvalidGroupAndEachKindOfValue()
    {
        var (info, element) = Sample();
        var entries = new CompactUserParameters(info).GetEntries(element);
        Assert.Equal(new[] { 10, 11, 12, 13, 14, 15, 16, 17, 18 }, entries.Select(e => e.ParameterId));
        Assert.Equal(("Invalid group", "Note", "a\tb \"quoted\""), (entries[0].Group, entries[0].Label, entries[0].Text));
        Assert.Equal(("Identity Data", "Note", ""), (entries[1].Group, entries[1].Label, entries[1].Text));
        Assert.Equal(("Identity Data", "Fire Rated", "Yes"), (entries[2].Group, entries[2].Label, entries[2].Text));
        Assert.Equal(("Dimensions", "Count", "33615"), (entries[3].Group, entries[3].Label, entries[3].Text));   // an integer: %d, no label lookup
        Assert.Equal(("Materials and Finishes", "Finish", "Oak"), (entries[4].Group, entries[4].Label, entries[4].Text));
        Assert.Equal(("Dimensions", "Depth", null, 0.8202099737532809, "autodesk.spec.aec:length-1.0.0"),
            (entries[5].Group, entries[5].Label, entries[5].Text, entries[5].Raw, entries[5].Spec));
        Assert.Equal("https://example.org", entries[6].Text);
        Assert.Equal(("Graphics", "Picture", ""), (entries[7].Group, entries[7].Label, entries[7].Text));    // an image parameter without an image prints ""
        Assert.Equal("line 1\r\nline 2", entries[8].Text);                                               // multi-line text, as stored
    }

    [Theory]
    [InlineData(0, "No")]
    [InlineData(1, "Yes")]
    [InlineData(5, "Yes")]
    public void YesNoParameter_IsYesForAnyNonZeroValue(int stored, string expected)
    {
        var (info, element) = Sample(yesNoValue: stored);
        Assert.Equal(expected, new CompactUserParameters(info).GetEntries(element).Single(e => e.ParameterId == 12).Text);
    }

    [Fact]
    public void ElementReference_IsNoneForTheInvalidIdAndLeftOutWhenTheNameIsUnknown()
    {
        var (none, noneElement) = Sample(referenced: -1);
        Assert.Equal("None", new CompactUserParameters(none).GetEntries(noneElement).Single(e => e.ParameterId == 14).Text);
        var (unnamed, unnamedElement) = Sample(referencedName: null);
        Assert.DoesNotContain(new CompactUserParameters(unnamed).GetEntries(unnamedElement), e => e.ParameterId == 14);
        var (named, namedElement) = Sample();
        Assert.Equal("Birch", new CompactUserParameters(named, elementName: id => id == 77 ? "Birch" : null).GetEntries(namedElement).Single(e => e.ParameterId == 14).Text);
    }

    [Fact]
    public void GetProperties_FormatsNumbersThroughTheDelegateAndLeavesThemOutWithoutOne()
    {
        var (info, element) = Sample();
        Assert.DoesNotContain(new CompactUserParameters(info).GetProperties(element), p => p.Label == "Depth");
        var properties = new CompactUserParameters(info, (spec, value) => spec.Contains("length") ? Math.Round(value * 304.8).ToString() : null).GetProperties(element);
        Assert.Equal(new CompactProperty("Dimensions", "Depth", "250"), properties.Single(p => p.Label == "Depth"));
        Assert.Equal(new CompactProperty("Identity Data", "Fire Rated", "Yes"), properties.Single(p => p.Label == "Fire Rated"));
        Assert.Equal(9, properties.Count);
    }

    [Fact]
    public void UnknownNumericFormatIsDistinctFromStoredZeroAndUnsetValue()
    {
        var (info, element) = Sample(depth: 0);
        var unknown = new CompactUserParameters(info, (_, _) => null);
        Assert.Equal(0d, unknown.GetEntries(element).Single(e => e.ParameterId == 15).Raw);
        Assert.DoesNotContain(unknown.GetProperties(element), p => p.Label == "Depth");
        Assert.Equal("0", new CompactUserParameters(info, (_, raw) => raw.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .GetProperties(element).Single(p => p.Label == "Depth").Value);

        var (unsetInfo, unsetElement) = Sample(depth: null);
        Assert.Null(unsetInfo.UserParameters!.EntriesOf(unsetElement, includeUnvalidated: true).Single(e => e.Definition.Id == 15).Value);
        Assert.DoesNotContain(new CompactUserParameters(unsetInfo, (_, _) => throw new InvalidOperationException("An unset value must not be formatted"))
            .GetProperties(unsetElement), p => p.Label == "Depth");
    }

    [SkippableFact]
    public void StructuralSample2022RecoversNumericFamilyParameterFormats()
    {
        var info = new RvtFileReader().Read(TestFileFixture.FindTestFile("rstbasicsampleproject.rvt"));
        var settings = info.SerializedFormatSettings;
        Assert.NotNull(settings);
        foreach (string spec in new[] { "autodesk.spec.aec:length-1.0.0", "autodesk.spec.aec:angle-1.0.0",
            "autodesk.spec.aec.piping:pipeSize-1.0.0", "autodesk.spec.aec.hvac:ductSize-1.0.0" })
            Assert.NotNull(settings.Find(spec));
        var element = info.Elements.Single(e => e.Id == 12344);
        var source = new CompactUserParameters(info, (spec, raw) => CompactValueFormatter.Format(raw, spec, settings));
        Assert.Equal(25 / 304.8, source.GetEntries(element).Single(e => e.Label == "Width").Raw!.Value, 12);
        Assert.Equal(new CompactProperty("Dimensions", "Width", "25"), source.GetProperties(element).Single(p => p.Label == "Width"));
    }

    [Fact]
    public void FileWithoutACatalog_HasNoUserParameters()
        => Assert.Empty(new CompactUserParameters(new RevitFileInfo()).GetProperties(new Element { Id = 1, SerializedOwnElementClassId = 1794 }));

    // a real file: entries of Test_2022 (element|parameter|group|label|text|raw)

    private static readonly string[] Test2022Vectors =
    [
        "414482|412506|Dimensions|Capital Top Offset||0.8202099737532809",
        "744525|197324|Constraints|Install Depth (from outside)||0",
        "767870|753917|Dimensions|Spacing Left||0.5",
        "929139|928917|Dimensions|right||4.425248343718884",
        "12344|18134|Dimensions|Width||0.08202099737532809",
        "420952|412504|Dimensions|Drop Panel Length||4.921259842519685",
        "698708|698505|Constraints|Cold Water Connection Radius||0.015625",
        "818875|197320|Construction|Frame Depth||0.19685039370078738",
        "23|198377|Green Building Properties|Recycled Content||",
        "457479|197328|Materials and Finishes|Glass|None|",
        "29292|581332|Materials and Finishes|Baluster Material|None|",
        "29292|581348|Materials and Finishes|Baluster Material|None|",
        "29292|581375|Materials and Finishes|Material|None|",
        "457479|197326|Materials and Finishes|Frame|SH_Aluminum, Anodized Black|",
        "735853|197333|Materials and Finishes|Window Cill Interior|Wood_Walnut black|",
        "812954|802337|Materials and Finishes|Material|SH_Aluminum, Anodized Black|",
        "12344|25978|Materials and Finishes|Baluster Material|Iron, Cast|",
        "674355|673827|Materials and Finishes|Logo|Logo|",
        "686169|685901|Materials and Finishes|Plate|Black Plastic|",
        "907609|907263|Materials and Finishes|Door Material|Door - Panel|",
        "757198|753908|Dimensions|Lamp Quantity|1|",
        "964242|970355|Identity Data|Order|9|",
        "678974|678550|Electrical|Voltage|230|",
        "696969|696707|Electrical|Voltage|230|",
        "802440|761980|Electrical - Lighting|Number Of Lamps|2|",
        "856593|749047|Electrical|Number of Poles|1|",
        "414482|412509|Construction|Show Drop Panel|Yes|",
        "744522|197331|Graphics|Casement Swing in Plan|No|",
        "767857|753922|Invalid group|Lamp 4|No|",
        "767917|753927|Invalid group|Lamp 9|No|",
        "29292|581326|Invalid group|Horizontal|Yes|",
        "128305|128301|Graphics|View Name|Yes|",
        "684776|681656|Invalid group|Joinery cut line on/off|No|",
        "778223|778086|Invalid group|Overflow Left|No|",
        "857191|26010|Identity Data|Occupant||",
        "964238|964512|General|Summary|Place grids into a project and control grid heads display.|",
        "970388|970354|General|Location|Floor Plan Level 2|",
        "970500|972682|Identity Data|Learning Level|03 - Advanced|",
        "971597|974323|General|Learning Link|http://help.autodesk.com/view/RVT/2020/ENU/?contextId=EXLINK_DETAILLINES|",
        "24584|24562|Invalid group|Name||",
        "678974|678554|Invalid group|Connection cable length||",
        "696969|696705|Dimensions|Face height|460.5|",
        "856593|753915|Identity Data|Catalog Number||",
    ];

    [SkippableFact]
    public void Test2022_EntriesEqualTheReferenceExport()
    {
        var info = UserParameterCatalogTests.Test2022.Value;
        var source = new CompactUserParameters(info);
        var byId = info.Elements.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (string vector in Test2022Vectors)
        {
            var part = vector.Split('|');
            int elementId = int.Parse(part[0]), parameter = int.Parse(part[1]);
            var entry = source.GetEntries(byId[elementId]).SingleOrDefault(e => e.ParameterId == parameter);
            Assert.True(entry.Label != null, vector);
            Assert.Equal(part[2], entry.Group);
            Assert.Equal(part[3], entry.Label);
            if (part[5].Length > 0)
            {
                Assert.Null(entry.Text);
                Assert.Equal(double.Parse(part[5], System.Globalization.CultureInfo.InvariantCulture), entry.Raw!.Value, 12);
            }
            else Assert.Equal(part[4], entry.Text);
        }
    }

    [SkippableFact]
    public void Test2022_ListsExactlyTheReferenceParametersOfTheSampleElements()
    {
        var info = UserParameterCatalogTests.Test2022.Value;
        var source = new CompactUserParameters(info);
        var byId = info.Elements.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());
        // The expected export: symbol 12344 lists Width and Baluster Material; instance 414482 the four instance parameters of its family;
        // a room lists the project parameter Occupant (no value); an element of a class that has no category lists none.
        Assert.Equal(new[] { 18134, 25978 }, source.GetEntries(byId[12344]).Select(e => e.ParameterId));
        Assert.Equal(new[] { 412506, 412508, 412509, 412510 }, source.GetEntries(byId[414482]).Select(e => e.ParameterId));
        Assert.Equal(new[] { 26010 }, source.GetEntries(byId[857191]).Select(e => e.ParameterId));
        Assert.Equal("", source.GetEntries(byId[857191])[0].Text);
        Assert.Empty(source.GetEntries(byId[30]));
    }
}
