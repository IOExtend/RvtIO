using RvtIO.Export;
using RvtIO.Compact;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The identity rules of the compact export: the two pseudo objects and the "Invalid group" values. The cases are small
/// hand-made documents; the vectors at the end are elements of Test_2022 with the text the current profile gives them.</summary>
public class CompactIdentityTests
{
    private static ushort ClassId(string schemaName) => (ushort)TfBlockDeserializer.Schemas.First(entry => entry.Value.Name == schemaName).Key;

    private static Element El(int id, string schemaClass, int? category = null, int typeId = 0, string? name = null, int? symbolFamily = null, (string Key, int Value)[]? fields = null)
    {
        var element = new Element
        {
            Id = id,
            UniqueId = "e3e052f9-0156-11d5-9301-0000863f27ad-" + id.ToString("x8"),
            SerializedOwnElementClassId = ClassId(schemaClass),
            CategoryId = category, NativeHeaderCategoryId = category,
            TypeId = typeId,
            Name = name,
            SymbolFamilyId = symbolFamily,
        };
        if (fields is { Length: > 0 })
        {
            element.NativeListFields = [];
            foreach (var (key, value) in fields) element.NativeListFields[key] = value;
        }
        return element;
    }

    private static CompactIdentity Identity(params Element[] rows) => new(rows);


    [Theory]
    [InlineData(-2000011, "Walls")]
    [InlineData(-2000023, "Doors")]
    [InlineData(-1, "")]          // the null id: no category
    [InlineData(7104823, "")]     // a category element (user category): the expected export has no label for it
    [InlineData(-2147483000, "")] // a built-in id that the expected export leaves unnamed
    public void CategoryLabel_IsTheReferenceLabelOfTheHeaderCategory(int category, string label)
        => Assert.Equal(label, Identity().CategoryLabel(El(1, "SWall", category)).Value);

    [Fact]
    public void CategoryLabel_IsUnknownWithoutAHeaderCategory()
        => Assert.False(Identity().CategoryLabel(El(1, "SWall", category: null)).IsKnown);

    [Fact]
    public void HeaderCategory_IsTakenThroughOneFunctionThatCanBeReplaced()
    {
        var wall = El(1, "SWall", category: -2000011);
        var identity = new CompactIdentity([wall], headerCategory: _ => -2000023);
        Assert.Equal(-2000023, identity.HeaderCategory(wall));
        Assert.Equal("Doors", identity.CategoryLabel(wall).Value);
        Assert.Equal(-2000011, CompactHeaderCategory.Of(wall));
    }


    [Theory]
    [InlineData("GStyleElem", "Graphic Style Information")]
    [InlineData("CategoryElem", "Category")]
    [InlineData("LinePatternElem", "Line Pattern")]
    [InlineData("LevelAttributes", "Level")]
    [InlineData("BasicWallType", "Basic Wall")]
    [InlineData("NewCurtainWallType", "Curtain Wall")]
    [InlineData("StackedWallType", "Stacked Wall")]
    [InlineData("DBView3d", "3D View")]
    [InlineData("DBViewSchedule", "Schedule")]
    [InlineData("RbsPipingSystemType", "Piping System")]
    [InlineData("TextNoteAttributes", "Text")]
    [InlineData("LeaderStyle", "Arrowhead")]
    [InlineData("AnalyticalModelBeam", "")]  // a constant that is the empty text
    public void FamilyName_ofAClassWithAConstant_IsThatConstant(string schemaClass, string expected)
        => Assert.Equal(expected, Identity().FamilyName(El(1, schemaClass)).Value);

    [Fact]
    public void FamilyName_ofTheSymbolBase_IsEmpty()
        => Assert.Equal("", Identity().FamilyName(El(1, "DBViewType")).Value);

    [Fact]
    public void FamilyName_ofAnInstance_IsTheFamilyNameOfItsType()
    {
        var wallType = El(10, "BasicWallType");
        var wall = El(11, "SWall", typeId: 10);
        var identity = Identity(wallType, wall);
        Assert.Equal("Basic Wall", identity.FamilyName(wall).Value);
        Assert.Equal(10, identity.TypeOf(wall).Type!.Id);
    }

    [Fact]
    public void FamilyName_ofAFamilyInstance_IsTheNameOfTheFamilyOfItsSymbol()
    {
        var family = El(20, "Family", name: "M_Single-Flush");
        var symbol = El(21, "FamilySymbol", symbolFamily: 20, name: "0915 x 2134mm");
        var instance = El(22, "FamilyInstance", typeId: 21);
        var identity = Identity(family, symbol, instance);
        Assert.Equal("M_Single-Flush", identity.FamilyName(symbol).Value);
        Assert.Equal("M_Single-Flush", identity.FamilyName(instance).Value);
    }

    [Fact]
    public void FamilyName_ofASymbolWhoseFamilyNameIsNotValidated_IsUnknown()
    {
        var family = El(20, "Family", name: null);
        var symbol = El(21, "FamilySymbol", symbolFamily: 20);
        Assert.False(Identity(family, symbol).FamilyName(symbol).IsKnown);
    }

    [Fact]
    public void FamilyName_ofAnElementWithoutAType_IsEmpty()
        => Assert.Equal("", Identity().FamilyName(El(1, "Hub")).Value);

    [Fact]
    public void FamilyName_ofAnInstanceWhoseTypeIsNotARow_IsUnknown()
    {
        var wall = El(11, "SWall", typeId: 999);
        CompactText family = Identity(wall).FamilyName(wall);
        Assert.False(family.IsKnown);
        Assert.Contains("not found", family.Unknown);
    }

    [Fact]
    public void FamilyName_ofATypeThatIsNotASymbol_IsEmpty()
    {
        // the type of this element is a plain element: the expected export asks the type only when it is a Symbol
        var style = El(30, "GStyleElem");
        var wall = El(11, "SWall", typeId: 30);
        Assert.Equal("", Identity(style, wall).FamilyName(wall).Value);
    }

    [Theory]
    [InlineData("IndependentTag")]   // the stored type is not what the reader keeps in TypeId
    [InlineData("CurveElem")]        // the type comes from a witness reference
    [InlineData("TilePatternType")]  // depends on the material and the unit system
    public void FamilyName_ofAClassWhoseRuleIsNotModelled_IsUnknown(string schemaClass)
        => Assert.False(Identity().FamilyName(El(1, schemaClass, typeId: 5)).IsKnown);

    [Fact]
    public void FamilyName_ofAClassTheTablesDoNotCover_IsUnknown()
    {
        var element = El(1, "SWall");
        element.SerializedOwnElementClassId = null;
        Assert.False(Identity().FamilyName(element).IsKnown);
    }


    [Theory]
    [InlineData(0, "Linear Dimension Style")]
    [InlineData(1, "Angular Dimension Style")]
    [InlineData(2, "Radial Dimension Style")]
    [InlineData(9, "Diameter Dimension Style")]
    public void FamilyName_ofADimensionStyle_FollowsItsStyleType(int styleType, string expected)
        => Assert.Equal(expected, Identity().FamilyName(El(1, "DimensionStyle", fields: [("174:m_dimensionStyleType", styleType)])).Value);

    [Fact]
    public void FamilyName_ofADimensionStyleWithAnUnseenType_IsUnknown()
        => Assert.False(Identity().FamilyName(El(1, "DimensionStyle", fields: [("174:m_dimensionStyleType", 5)])).IsKnown);

    [Fact]
    public void FamilyName_ofALinearDimension_IsTheFamilyOfItsDimensionStyle()
    {
        var style = El(40, "DimensionStyle", fields: [("174:m_dimensionStyleType", 0)]);
        var dimension = El(41, "LinearDimString", typeId: 40);
        Assert.Equal("Linear Dimension Style", Identity(style, dimension).FamilyName(dimension).Value);
    }

    [Theory]
    [InlineData("DBViewSection", "1016:m_sectionViewType", 0, "Building Section")]
    [InlineData("DBViewSection", "1016:m_sectionViewType", 3, "Section")]
    [InlineData("DBViewSection", "1016:m_sectionViewType", 2, "Elevation")]
    [InlineData("ConceptualConstructionType", "743:m_massSurfaceSubCategoryId", -2003421, "Mass Walls")]
    [InlineData("ConceptualConstructionType", "743:m_massSurfaceSubCategoryId", -2003414, "Mass Roof")]
    [InlineData("ConceptualConstructionType", "743:m_massSurfaceSubCategoryId", -2003422, "")]
    [InlineData("CompoundCeilingType", "105:m_pCompoundStructure", 0, "Basic Ceiling")]
    [InlineData("CompoundCeilingType", "105:m_pCompoundStructure", 737, "Compound Ceiling")]
    [InlineData("StairsType", "3742:m_constructionMethod", 0, "Assembled Stair")]
    [InlineData("StairsType", "3742:m_constructionMethod", 1, "Cast-In-Place Stair")]
    [InlineData("StairsType", "3742:m_constructionMethod", 2, "Precast Stair")]
    [InlineData("StairsRunType", "3718:m_hasMonolithicSupport", 1, "Monolithic Run")]
    [InlineData("StairsRunType", "3718:m_hasMonolithicSupport", 0, "Non-Monolithic Run")]
    [InlineData("StairsLandingType", "3697:m_hasMonolithicSupport", 1, "Monolithic Landing")]
    [InlineData("StairsLandingType", "3697:m_hasMonolithicSupport", 0, "Non-Monolithic Landing")]
    [InlineData("StairsSupportType", "3736:m_topsideSurfaceType", 0, "Stringer")]
    [InlineData("StairsSupportType", "3736:m_topsideSurfaceType", 1, "Carriage")]
    [InlineData("StairsPathType", "3703:m_stairsPathDirection", 0, "Fixed Up Direction")]
    [InlineData("StairsPathType", "3703:m_stairsPathDirection", 1, "Automatic Up/Down Direction")]
    [InlineData("RbsCableTrayType", "3130:m_bWithFitting", 1, "Cable Tray with Fittings")]
    [InlineData("RbsCableTrayType", "3130:m_bWithFitting", 0, "Cable Tray without Fittings")]
    [InlineData("RbsConduitType", "3135:m_bWithFitting", 1, "Conduit with Fittings")]
    [InlineData("RbsConduitType", "3135:m_bWithFitting", 0, "Conduit without Fittings")]
    [InlineData("AbsDuctType", "104:m_Profile", 124, "Round Duct")]
    [InlineData("AbsDuctType", "104:m_Profile", 126, "Oval Duct")]
    [InlineData("AbsDuctType", "104:m_Profile", 127, "Rectangular Duct")]
    [InlineData("AbsFlexDuctType", "104:m_Profile", 124, "Flex Duct Round")]
    [InlineData("AbsFlexDuctType", "104:m_Profile", 127, "Flex Duct Rectangular")]
    [InlineData("BrowserOrganization", "560:m_type", 0, "Browser - Views")]
    [InlineData("BrowserOrganization", "560:m_type", 1, "Browser - Sheets")]
    [InlineData("BrowserOrganization", "560:m_type", 3, "")]
    public void FamilyName_ofATypeThatDecidesOnAStoredField(string schemaClass, string key, int value, string expected)
        => Assert.Equal(expected, Identity().FamilyName(El(1, schemaClass, fields: [(key, value)])).Value);

    [Theory]
    [InlineData("DBViewSection")]
    [InlineData("StairsType")]
    [InlineData("RbsConduitType")]
    public void FamilyName_ofATypeWhoseFieldIsNotDecoded_IsUnknown(string schemaClass)
        => Assert.False(Identity().FamilyName(El(1, schemaClass)).IsKnown);

    [Fact]
    public void FamilyName_ofAFlexOvalDuctType_IsUnknown_BecauseItsTextWasNeverSeen()
        => Assert.False(Identity().FamilyName(El(1, "AbsFlexDuctType", fields: [("104:m_Profile", 126)])).IsKnown);

    [Theory]
    [InlineData(-2001300, "Foundation Slab")]
    [InlineData(-2000032, "Floor")]
    public void FamilyName_ofFloorAttributes_FollowsTheHeaderCategory(int category, string expected)
        => Assert.Equal(expected, Identity().FamilyName(El(1, "FloorAttributes", category)).Value);

    [Fact]
    public void FamilyName_ofTheProjectView_IsProject()
        => Assert.Equal("Project", Identity().FamilyName(El(1, "DBViewProject")).Value);

    [Fact]
    public void FamilyName_ofAPlanView_FollowsThePlanTypeAndTheViewType()
    {
        var floorType = El(50, "DBViewType");
        var areaType = El(51, "DBViewTypeAreaPlan");
        CompactIdentity identity = Identity(floorType, areaType);
        Element Plan(int plan, int type) => El(1, "DBViewPlan", fields: [("1005:m_planViewType", plan), ("108:m_dbViewTypeId", type)]);
        Assert.Equal("Floor Plan", identity.FamilyName(Plan(1, 50)).Value);
        Assert.Equal("Ceiling Plan", identity.FamilyName(Plan(2, 50)).Value);
        Assert.Equal("Ceiling Plan", identity.FamilyName(Plan(2, -1)).Value);
        Assert.Equal("", identity.FamilyName(Plan(1, -1)).Value);       // a view template has no view type
        Assert.False(identity.FamilyName(Plan(1, 51)).IsKnown);        // an area plan needs the area measure
    }

    [Fact]
    public void FamilyName_ofAGroupType_IsDecidedByThePlacementHelperAndTheCategory()
    {
        Element Group(int category, int helper) => El(1, "ElementGroupType", category, fields: [("1333:m_placementHelper", helper)]);
        CompactIdentity identity = Identity();
        Assert.Equal("Model Group", identity.FamilyName(Group(-2000095, 0)).Value);
        Assert.Equal("Detail Group", identity.FamilyName(Group(-2000096, 0)).Value);
        Assert.False(identity.FamilyName(Group(-2000097, 0)).IsKnown);   // attached detail groups ask another function
        Assert.False(identity.FamilyName(Group(-2000095, 12)).IsKnown);  // a helper gives its own name
    }

    [Fact]
    public void Type_ofAViewportAndAFilledRegion_IsTheirStoredAttributeId()
    {
        var attributes = El(60, "ViewportAttributes");
        var viewport = El(61, "Viewport", fields: [("1990:m_attrId", 60)]);
        var plain = El(62, "Viewport", fields: [("1990:m_attrId", -1)]);
        CompactIdentity identity = Identity(attributes, viewport, plain);
        Assert.Equal("Viewport", identity.FamilyName(viewport).Value);
        Assert.Equal("", identity.FamilyName(plain).Value);
        Assert.True(identity.TypeOf(plain).IsKnown);
        Assert.Null(identity.TypeOf(plain).Type);
        Assert.False(Identity(attributes, El(63, "FilledRegion")).FamilyName(El(63, "FilledRegion")).IsKnown);
    }


    [Fact]
    public void Pseudo_isWrittenWhenCategoryLabelAndFamilyNameAreBothNotEmpty()
    {
        var wallType = El(10, "BasicWallType");
        var wall = El(11, "SWall", category: -2000011, typeId: 10);
        CompactPseudo pseudo = Identity(wallType, wall).Pseudo(wall);
        Assert.Equal(new CompactPseudo(true, "Walls", "Basic Wall"), pseudo);
    }

    [Fact]
    public void Pseudo_isNotWritten_WhenOneNameIsEmpty()
    {
        CompactIdentity identity = Identity();
        Assert.Equal(new CompactPseudo(true, null, null), identity.Pseudo(El(1, "GStyleElem", category: -1)));     // no category label
        Assert.Equal(new CompactPseudo(true, null, null), identity.Pseudo(El(2, "Hub", category: -2000011)));      // no family name
    }

    [Fact]
    public void Pseudo_isUndecided_WhenTheReaderCannotTell()
    {
        CompactIdentity identity = Identity();
        Assert.False(identity.Pseudo(El(1, "IndependentTag", category: -2000011, typeId: 5)).IsKnown);  // family unknown, label not empty
        Assert.False(identity.Pseudo(El(2, "SWall", category: null, typeId: 5)).IsKnown);               // label unknown
        Assert.Equal(new CompactPseudo(true, null, null), identity.Pseudo(El(3, "Hub", category: null)));    // the family name is empty whatever the category
    }


    [Fact]
    public void CategoryValue_isTheEnumNameOfTheHeaderCategory()
    {
        CompactIdentity identity = Identity();
        Assert.Equal("OST_Walls", identity.CategoryValue(El(1, "SWall", -2000011)).Value);
        Assert.Equal("None", identity.CategoryValue(El(2, "SWall", CompactHeaderCategory.NullCategory)).Value);
        Assert.Equal("7104823", identity.CategoryValue(El(3, "SWall", 7104823)).Value);
        Assert.Equal("", identity.CategoryValue(El(4, "SWall", -2000924)).Value);   // OST_PropertySet: the expected export prints nothing
        Assert.False(identity.CategoryValue(El(5, "SWall", null)).IsKnown);
        Assert.False(identity.CategoryValue(El(6, "SWall", -2147483000)).IsKnown);  // no enum name known
    }

    [Fact]
    public void CategoryValue_isUnknown_ForAClassWhoseParameterIsStoredWithTheElement()
    {
        // a viewer keeps the Views category as a stored parameter: its value is not the header category
        var viewer = El(1, "Viewer", -2000278);
        viewer.NativeParameterStoresComplete = true;
        viewer.NativeParameterStoreIds = [];
        Assert.False(Identity().CategoryValue(viewer).IsKnown);
    }

    [Fact]
    public void FamilyNameParameter_isTheFamilyNameOfATypeAndEmptyForAnythingElse()
    {
        var family = El(20, "Family", name: "M_Single-Flush");
        var symbol = El(21, "FamilySymbol", symbolFamily: 20);
        var instance = El(22, "FamilyInstance", typeId: 21);
        CompactIdentity identity = Identity(family, symbol, instance);
        Assert.Equal("M_Single-Flush", identity.FamilyNameParameter(symbol).Value);
        Assert.Equal("", identity.FamilyNameParameter(instance).Value);
        Assert.Equal("", identity.FamilyNameParameter(El(1, "GStyleElem")).Value);
        Assert.Equal("Basic Wall", identity.FamilyNameParameter(El(2, "BasicWallType")).Value);
        Assert.False(identity.FamilyNameParameter(El(3, "DBViewType")).IsKnown);   // view types report another text
        Assert.False(identity.FamilyNameParameter(El(4, "SegmentCenterLine")).IsKnown);   // sample project A: the expected export prints "Round Duct" for it
    }

    [Fact]
    public void TypeValue_isTheElementNameOfTheTypeOrNone()
    {
        var wallType = El(10, "BasicWallType", name: "Generic - 200mm");
        var wall = El(11, "SWall", typeId: 10);
        var unnamed = El(12, "BasicWallType", name: null);
        var unnamedWall = El(13, "SWall", typeId: 12);
        var viewport = El(14, "Viewport", fields: [("1990:m_attrId", -1)]);
        CompactIdentity identity = Identity(wallType, wall, unnamed, unnamedWall, viewport);
        Assert.Equal("Generic - 200mm", identity.TypeValue(wall).Value);
        Assert.False(identity.TypeValue(unnamedWall).IsKnown);
        Assert.False(identity.TypeValue(viewport).IsKnown);   // a viewport reports the Family and Type of its view
    }


    [Fact]
    public void Properties_listCategoryAndFamilyNameAlways_AndTheTypeEntriesWhenReferenceListsThem()
    {
        var wallType = El(10, "BasicWallType", name: "Generic - 200mm");
        var wall = El(11, "SWall", category: -2000011, typeId: 10);
        var identity = Identity(wallType, wall);
        var withoutList = new CompactIdentityProperties(identity).GetProperties(wall);
        Assert.Equal(
            [new CompactProperty("Invalid group", "Category", "OST_Walls"), new CompactProperty("Invalid group", "Family Name", "")],
            withoutList);

        int[] ids = [-1140362, -1140363, -1002002, -1002001, -1002000, -1002050, -1002051, -1002052];
        var withList = new CompactIdentityProperties(identity, _ => ids).GetProperties(wall);
        Assert.Equal(6, withList.Count);
        // ascending built-in id: Category -1140363, Family and Type -1002052, Family -1002051, Type -1002050, Family Name -1002002, Type Id -1002000
        Assert.Equal(new[] { "Category", "Family and Type", "Family", "Type", "Family Name", "Type Id" }, withList.Select(p => p.Label).ToArray());
        Assert.All(withList.Where(p => p.Label is "Type Id" or "Type" or "Family" or "Family and Type"),
            p => Assert.Equal("Generic - 200mm", p.Value));
        Assert.Equal("OST_Walls", withList.Single(p => p.Label == "Category").Value);
    }

    [Fact]
    public void Properties_leaveOutAnEntryWhoseValueIsNotValidated()
    {
        var wall = El(11, "SWall", category: null, typeId: 999);
        var entries = new CompactIdentityProperties(Identity(wall), _ => new[] { -1002050 }).GetProperties(wall);
        Assert.Equal([new CompactProperty("Invalid group", "Family Name", "")], entries);
    }


    [Fact]
    public void RuleTable_hasOneWellFormedLineForEveryClass()
    {
        Assert.True(CompactIdentityRules.Classes.Length > 800);
        Assert.Equal(CompactIdentityRules.Classes.Length, CompactIdentityRules.Classes.Select(line => line.Split('|')[0]).Distinct().Count());
        Assert.True(CompactIdentity.IsKnownClass("SWall"));
        Assert.True(CompactIdentity.IsKnownClass("FamilyInstance"));
        Assert.False(CompactIdentity.IsKnownClass("NoSuchClass"));
        // the special rules the table names and this class does not model (they return an unknown family name; the schema spells
        // NestedFamilyTypeReference differently and is not looked up here, it is not modelled either)
        var unmodelled = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string line in CompactIdentityRules.Classes.Where(line => line.Contains("|X:")))
        {
            string cls = line.Split('|')[0];
            var schema = TfBlockDeserializer.Schemas.Where(e => e.Value.Name == cls).Select(e => (int?)e.Key).FirstOrDefault();
            if (schema is null) continue;   // a reference class the file schema does not have cannot occur in a file
            var element = new Element { Id = 1, SerializedOwnElementClassId = (ushort)schema.Value };
            if ((Identity().FamilyName(element).Unknown ?? "").Contains("not modelled")) unmodelled.Add(line.Split('|')[1][2..]);
        }
        Assert.Equal(
            new[]
            {
                "AssemblyType", "FabricationInsulationLiningSharedSymbol", "FabricationPartInsulationLining", "FabricationPartType",
                "MassLevelData", "SketchPlane", "SpotElevationStyle", "TilePatternType",
            },
            unmodelled.ToArray());
    }

    [Fact]
    public void RuleTable_symbolClassesNeverUseTheDefaultFamilyName()
        => Assert.DoesNotContain(CompactIdentityRules.Classes, line => line.Split('|') is [_, "-", _, "S", _, _]);

    // (id|header category of the expected export|class|category label|family name|Category|Family Name; "-" no pseudo object, "?" the reader does not know)

    private static readonly string[] Test2022Vectors =
    [
        "198694|-2000011|SWall|Walls|Basic Wall|OST_Walls|",
        "211850|-2001160|FamilyInstance|Plumbing Fixtures|Sink-Offset-Kohler-Vault-3823_4|OST_PlumbingFixtures|",
        "213811|-2001160|FamilyInstance|Plumbing Fixtures|Faucet-8inch_Reach-Kohler-Purist-7507|OST_PlumbingFixtures|",
        "1049|-2000400|FamilySymbol|Section Marks|M_Section Head - Filled|OST_SectionHeads|M_Section Head - Filled",
        "1141|-2000538|FamilySymbol|Callout Heads|M_Callout Head|OST_CalloutHeads|M_Callout Head",
        "232770|-2000171|SysMullionFamSym|Curtain Wall Mullions|Rectangular Mullion|OST_CurtainWallMullions|Rectangular Mullion",
        "549060|-2000261|LinearDimString|Automatic Sketch Dimensions|Linear Dimension Style|OST_WeakDims|",
        "429028|-2000262|Alignment|Constraints|Linear Dimension Style|OST_Constraints|",
        "30|-1|GStyleElem|-|-|None|",
        "235|-1|CategoryElem|-|-|None|",
        "16209|-1|ParamElemFamily|-|-|None|",
        "280|-1|FontElem|-|-|None|",
        "23|-2000700|MaterialElem|-|-|OST_Materials|",
        "361|-1|TextNoteAttributes|-|-|None|Text",
        "234|-1|LeaderStyle|-|-|None|Arrowhead",
        "365|-1|TagNoteAttributes|-|-|None|Tag Label",
        "147514|-1|FamSymSurrogate|-|-|None|",
        "8482|-1|Family|-|-|None|",
        "49552|-1|DBViewType|-|-|None|?",
        "974959|-2000576|LegendComponent|-|-|OST_PreviewLegendComponents|",
        "414562|-2009645|Hub|-|-|OST_AnalyticalNodes|",
        "232|-2000510|Viewport|-|-|OST_Viewports|",
    ];

    [SkippableFact]
    public void Test2022_PseudoObjectsAndCategoryEqualTheReferenceExport()
    {
        // The vectors are read with the header category of the file: this checks the label function and the family-name, category and
        // Family Name rules, not the header category decoder.
        var reader = new RvtFileReader();
        var info = reader.Read(TestFileFixture.FindTestFile("Test_2022.rvt"));
        ValidatedElementNames.Apply(info, reader.LastParsingState!);
        var rows = CompactUniverse.Select(info);
        var vectors = Test2022Vectors.Select(v => v.Split('|')).ToDictionary(p => int.Parse(p[0]));
        var identity = new CompactIdentity(rows, headerCategory: element => vectors.TryGetValue(element.Id, out var v) ? int.Parse(v[1]) : element.CategoryId);
        var seen = 0;
        foreach (var element in rows.Where(e => vectors.ContainsKey(e.Id)))
        {
            string[] v = vectors[element.Id];
            string id = v[0];
            Assert.Equal(v[2], CompactIdentity.RecordClassName(element));
            CompactPseudo pseudo = identity.Pseudo(element);
            Assert.True(pseudo.IsKnown, id);
            Assert.Equal(v[3] == "-" ? null : v[3], pseudo.CategoryLabel);
            Assert.Equal(v[4] == "-" ? null : v[4], pseudo.FamilyName);
            Assert.Equal(v[5], identity.CategoryValue(element).Value);
            CompactText familyParameter = identity.FamilyNameParameter(element);
            if (v[6] == "?") Assert.False(familyParameter.IsKnown, id);
            else Assert.Equal(v[6], familyParameter.Value);
            seen++;
        }
        Assert.Equal(vectors.Count, seen);
    }
}
