using RvtIO.Export;
using RvtIO.Compact;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The value rules that follow the element's own FamilyParams records and stores: hand-made cases per rule and the real Test_2022 file.</summary>
public class CompactOwnValueRulesTests
{
    private const ushort SymbolClass = 1829, InstanceClass = 1794, OpeningClass = 3525;
    private const int Model = -1010109, Description = -1010103, ConstructionType = -1001207, TypeImage = -1152384;
    private const int AnalyticConstruction = -1005437, HeatTransfer = -1005430, SolarHeatGain = -1005432, VisualLight = -1005433;
    private const int BaseOffset = -1001108, HostArea = -1012805, SurveyBottom = -1001658;

    private const string Gate = "S\t-1001207\t1\nC\tFamilySymbol\t-1010109\t1\nC\tFamilySymbol\t-1010103\t1\nS\t-1001108\t1\n"
        + "C\tFamilyInstance\t-1012805\t1\nC\tFamilyInstance\t-1001658\t1\n";

    private static TfBlockDeserializer.NamedParamRecord Record(int id, string? text = null, int elementId = -1)
        => new(id, text, 0, elementId, 0, false, false);

    private static Parameter Text(int id, string value) => BipHelpers.MakeBipParam(id, id.ToString(), StorageType.String, value);

    private static IReadOnlyDictionary<int, string> Written(ushort cls, int header, Action<ParsingState, Element> arrange, int[] ids,
        string? lookupNone = null, double? survey = null, CompactFormatSettings? settings = null, params Element[] others)
    {
        var state = new ParsingState();
        var element = new Element { Id = 10, SerializedOwnElementClassId = cls, NativeRecordClass = cls, NativeParameterStoresComplete = true, NativeParameterStoreIds = [] };
        state.NativeParams[10] = new TfBlockDeserializer.NativeParams();
        arrange(state, element);
        var info = new RevitFileInfo();
        info.Elements.Add(element);
        info.Elements.AddRange(others);
        var parameters = new CompactBuiltinParameters(new CompactElementNames(info, state), settings, new CompactListFileFacts(null, true, false),
            _ => header, CompactValueGate.Parse(Gate), state, survey, lookupNone) { IdListOverride = _ => ids };
        return parameters.GetEntries(element).Entries.ToDictionary(e => e.Id, e => e.Property.Value);
    }

    [Fact]
    public void SymbolText_IsTheTextOfItsOwnRecord_NotTheTextTheReaderPutInPlaceOfAnEmptyOne()
    {
        // the record holds "" for Model and Description while the parameters read by the reader are not empty
        var result = Written(SymbolClass, -2000014, (state, element) =>
        {
            state.NativeParams[10].NamedParams.Add(Record(Model, ""));
            state.NativeParams[10].NamedParams.Add(Record(Description, "Окно"));
            element.Parameters.Add(Text(Model, "Подоконник"));
            element.Parameters.Add(Text(Description, "Отлив"));
        }, [Model, Description]);
        Assert.Equal("", result[Model]);
        Assert.Equal("Окно", result[Description]);
    }

    [Fact]
    public void StoredText_OfASymbolWithARecordComesFromTheRecord_AndIsEmptyWithout()
    {
        // sample project A symbol 204947: Construction Type is held by the FamilyParams record only
        var withRecord = Written(SymbolClass, -2000023, (state, _) => state.NativeParams[10].NamedParams.Add(Record(ConstructionType, "Дверь деревянная")),
            [ConstructionType]);
        Assert.Equal("Дверь деревянная", withRecord[ConstructionType]);
        var without = Written(SymbolClass, -2000023, (_, _) => { }, [ConstructionType]);
        Assert.Equal("", without[ConstructionType]);
    }

    [Fact]
    public void TypeImage_PrintsNoneForAStoredNullReference_EmptyWithoutARecord_AndNothingForAnImage()
    {
        Assert.Equal("None", Written(SymbolClass, -2000014, (state, _) => state.NativeParams[10].NamedParams.Add(Record(TypeImage, "", -1)), [TypeImage])[TypeImage]);
        Assert.Equal("", Written(SymbolClass, -2000014, (_, _) => { }, [TypeImage])[TypeImage]);
        Assert.Empty(Written(SymbolClass, -2000014, (state, _) => state.NativeParams[10].NamedParams.Add(Record(TypeImage, "", 4242)), [TypeImage]));
    }

    private static void Construction(ParsingState state, string name)
        => state.NativeParams[10].NamedParams.Add(Record(AnalyticConstruction, name));

    [Theory]
    [InlineData("<None>", null, true)]
    [InlineData("<Нет>", "<Нет>", true)]            // the localized empty entry, from the resources of the saved locale
    [InlineData("<Нет>", null, false)]              // no resources: not recognized
    [InlineData("Окна с одинарным остеклением - для жилых зданий", "<Нет>", false)]
    [InlineData("Metal", null, false)]
    public void AnalyticConstruction_IsWrittenOnlyForTheEmptyEntry_AndThenThermalValuesAreEmpty(string stored, string? lookupNone, bool written)
    {
        int[] ids = [AnalyticConstruction, HeatTransfer, SolarHeatGain, VisualLight];
        var result = Written(SymbolClass, -2000014, (state, _) => Construction(state, stored), ids, lookupNone);
        if (!written) { Assert.Empty(result); return; }
        Assert.Equal("<None>", result[AnalyticConstruction]);
        Assert.Equal("", result[HeatTransfer]);
        Assert.Equal("", result[SolarHeatGain]);
        Assert.Equal("", result[VisualLight]);
    }

    [Fact]
    public void SegmentCenterLine_HasNoDecidedFamilyName()
    {
        // sample project A 6095568: the expected export prints "Round Duct", the element is not derived from Symbol
        Assert.Empty(Written(3553, -2008044, (_, _) => { }, [-1002002]));
        Assert.Equal("", Written(3525, -2000011, (_, _) => { }, [-1002002])[-1002002]);   // any other non-type prints ""
    }

    [SkippableTheory]
    [InlineData(SymbolClass, false)]     // a panel of a loadable family: sample project A 11 of 107 areas differ from the expected export's
    [InlineData(3912, true)]             // a system panel (SysPanelFamSym): 7,689 of 7,689 equal
    public void CurtainPanelArea_IsNotWrittenForALoadableFamily(ushort symbolClass, bool written)
    {
        var area = BipHelpers.MakeBipParam(HostArea, "HOST_AREA_COMPUTED", StorageType.Double, 2.0);
        var symbol = new Element { Id = 11, SerializedOwnElementClassId = symbolClass, NativeRecordClass = symbolClass };
        var result = Written(InstanceClass, -2000170, (_, element) => { element.Parameters.Add(area); element.NativeMasterSymbolId = 11; },
            [HostArea], settings: Test2022Settings(), others: symbol);
        Assert.Equal(written, result.ContainsKey(HostArea));
    }

    [SkippableFact]
    public void StoredNumber_IsPrintedFromTheStoreNotFromTheReadersParameter()
    {
        // sample project A wall opening 10636900: stored 0.3576416728425062 ft = 109,01 mm (the expected export), the reader's parameter says 0
        var settings = Test2022Settings();
        var result = Written(OpeningClass, -2000011, (state, element) =>
        {
            state.NativeParams[10].Doubles.Add((BaseOffset, 0.3576416728425062));
            element.NativeParameterStoreIds = [BaseOffset];
            element.Parameters.Add(BipHelpers.MakeBipParam(BaseOffset, "WALL_BASE_OFFSET", StorageType.Double, 0.0));
        }, [BaseOffset], settings: settings);
        Assert.Equal("109", result[BaseOffset]);   // Test_2022 prints lengths in whole millimetres
    }

    [SkippableFact]
    public void SurveyElevation_NeedsTheActiveLocationAndAddsItsElevation()
    {
        var settings = Test2022Settings();
        void Arrange(ParsingState state, Element element)
            => element.Parameters.Add(BipHelpers.MakeBipParam(SurveyBottom, "SURVEY", StorageType.Double, -18.8648293963254));   // -5750 mm
        Assert.Empty(Written(InstanceClass, -2001330, Arrange, [SurveyBottom], settings: settings));
        string moved = Written(InstanceClass, -2001330, Arrange, [SurveyBottom], survey: 11.975065616797901, settings: settings)[SurveyBottom];   // +3650 mm
        Assert.Equal("-2100", moved);
    }

    private static CompactFormatSettings Test2022Settings()
    {
        var reader = new RvtFileReader();
        var info = reader.Read(TestFileFixture.FindTestFile("Test_2022.rvt"));
        return info.SerializedFormatSettings ?? throw new InvalidOperationException("Test_2022 has no format settings");
    }

    [Theory]
    [InlineData("RUS", "<Нет>")]
    [InlineData("ENU", "<None>")]
    public void LookupNoneText_IsTheResourceTextOfTheSavedLocale(string locale, string expected)
    {
        Assert.Equal(expected, RvtIO.Lookup.RevitSystemFamilyNames.Load("2022", locale)!.Get(RvtIO.Lookup.RevitUiText.LookupNone));
    }


    private static TfBlockDeserializer.NamedParamRecord UserRecord(int id, bool instance, string? text = null, int integer = 0, int elementId = -1)
        => new(id, text, 0, elementId, integer, instance, false);

    private static (UserParameterCatalog Catalog, Element Instance, Element Symbol) Catalog(bool withInfoSymbol, int category = -2000023)
    {
        var state = new ParsingState();
        void Add(int id, ushort cls, TfBlockDeserializer.NativeParams np) { state.ElemClassIdx[(uint)id] = cls; state.NativeParams[(uint)id] = np; }
        var definition = new TfBlockDeserializer.NativeParams();
        definition.Fields["2870:m_pParamDef.class"] = 606;
        definition.StringFields["301:m_caption"] = "Opening";
        definition.Fields["301:m_groupElemId"] = -1;
        definition.Fields["301:m_paramElemId"] = 5000;
        definition.StringFields["301:m_typeId"] = "autodesk.parameter.aec.revit.project:5000-1.0.0";
        Add(5000, 2876, definition);
        var typeBinding = new TfBlockDeserializer.NativeParams();
        typeBinding.Fields["2632:m_paramId"] = 5000; typeBinding.Fields["2632:m_categoryId"] = category; typeBinding.Fields["2632:m_elemOrSymbol"] = 2;
        Add(5001, 2632, typeBinding);
        var instanceBinding = new TfBlockDeserializer.NativeParams();
        instanceBinding.Fields["2632:m_paramId"] = 5000; instanceBinding.Fields["2632:m_categoryId"] = category; instanceBinding.Fields["2632:m_elemOrSymbol"] = 1;
        Add(5002, 2632, instanceBinding);
        var family = new TfBlockDeserializer.NativeParams();
        family.Fields["1773:m_categoryId"] = category;
        Add(100, 1772, family);
        foreach (int symbolId in new[] { 101, 102 })
        {
            var symbol = new TfBlockDeserializer.NativeParams();
            symbol.Fields["1829:m_familyId"] = 100;
            symbol.NamedParams.Add(UserRecord(5000, true, symbolId == 101 ? "Left" : "Right"));
            Add(symbolId, 1829, symbol);
        }
        var instance = new TfBlockDeserializer.NativeParams();
        instance.Fields["m_masterSymbolId"] = 101;
        if (withInfoSymbol) instance.Fields["2237:m_symbolId"] = 102;
        Add(103, 1794, instance);
        return (UserParameterCatalog.Build(state), new Element { Id = 103, SerializedOwnElementClassId = 1794 },
            new Element { Id = 101, SerializedOwnElementClassId = 1829 });
    }

    [Theory]
    [InlineData(true, "Right")]    // the symbol of the InstanceInfo wins over the master symbol
    [InlineData(false, "Left")]    // without an InstanceInfo symbol the master symbol is the only one known
    public void InstanceShowsTheValueOfTheSymbolOfItsInstanceInfo(bool withInfoSymbol, string expected)
    {
        var (catalog, instance, _) = Catalog(withInfoSymbol);
        var entry = Assert.Single(catalog.EntriesOf(instance), e => e.Definition.Id == 5000);
        Assert.Equal(expected, entry.Value!.Value.Text);
    }

    [Fact]
    public void SymbolRecordFlaggedAsInstanceRecordIsNoTypeValue()
    {
        // shared type parameters of two symbols print ""
        var (catalog, _, symbol) = Catalog(true);
        var entry = Assert.Single(catalog.EntriesOf(symbol), e => e.Definition.Id == 5000);
        Assert.Null(entry.Value);
    }

    [Theory]
    [InlineData(9099028, false)]   // sample project A symbol 9100048: the image "image.png" is referenced, the expected export prints its name
    [InlineData(-1, true)]         // no image: ""
    public void ImageParameter_PrintsEmptyOnlyWithoutAReference(int referenced, bool written)
    {
        var state = new ParsingState();
        void Add(int id, ushort cls, TfBlockDeserializer.NativeParams np) { state.ElemClassIdx[(uint)id] = cls; state.NativeParams[(uint)id] = np; }
        var definition = new TfBlockDeserializer.NativeParams();
        definition.Fields["2870:m_pParamDef.class"] = 2752;
        definition.StringFields["301:m_caption"] = "Legend";
        definition.Fields["301:m_groupElemId"] = -1;
        definition.Fields["301:m_paramElemId"] = 5000;
        definition.StringFields["301:m_typeId"] = "autodesk.parameter.aec.revit.project:5000-1.0.0";
        Add(5000, 2876, definition);
        var binding = new TfBlockDeserializer.NativeParams();
        binding.Fields["2632:m_paramId"] = 5000; binding.Fields["2632:m_categoryId"] = -2000023; binding.Fields["2632:m_elemOrSymbol"] = 2;
        Add(5001, 2632, binding);
        var family = new TfBlockDeserializer.NativeParams();
        family.Fields["1773:m_categoryId"] = -2000023;
        Add(100, 1772, family);
        var symbol = new TfBlockDeserializer.NativeParams();
        symbol.Fields["1829:m_familyId"] = 100;
        symbol.NamedParams.Add(UserRecord(5000, false, "", elementId: referenced));
        Add(101, 1829, symbol);
        var element = new Element { Id = 101, SerializedOwnElementClassId = 1829, NativeParameterStoresComplete = true };
        var info = new RevitFileInfo { UserParameters = UserParameterCatalog.Build(state) };
        info.Elements.Add(element);
        var entries = new CompactUserParameters(info).GetEntries(element);
        if (written) Assert.Equal("", Assert.Single(entries).Text);
        else Assert.Empty(entries);
    }


    [SkippableFact]
    public void Test2022_SymbolsWithTheEmptyConstructionEntryPrintEmptyThermalValues_AndOthersNoValue()
    {
        var reader = new RvtFileReader();
        var info = reader.Read(TestFileFixture.FindTestFile("Test_2022.rvt"));
        ValidatedElementNames.Apply(info, reader.LastParsingState!);
        var source = CompactPropertySources.Create(info, reader.LastParsingState, true);
        // The expected export: 198366 lists "Analytic Construction" "<None>" and Heat Transfer Coefficient (U) ""; 488864 stores another entry
        var empty = source.GetProperties(info.Elements.First(e => e.Id == 198366));
        Assert.Contains(new CompactProperty("Analytical Properties", "Analytic Construction", "<None>"), empty);
        Assert.Contains(new CompactProperty("Analytical Properties", "Heat Transfer Coefficient (U)", ""), empty);
        var other = source.GetProperties(info.Elements.First(e => e.Id == 488864));
        Assert.DoesNotContain(other, p => p.Label is "Heat Transfer Coefficient (U)" or "Analytic Construction");
    }
}
