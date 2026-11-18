using RvtIO.Export;
using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The JSON "Name" is a validated name or null. The fixtures reproduce what the reader holds for the Test_2022 roof
/// 243274 and its type 507024: the roof carries a scanned wind generator name, the type a native string "B1020" (its
/// assembly code) and only its own HostObjAttr root holds the real name, "SG Metal Panels roof".</summary>
public class ValidatedElementNamesTests
{
    private static Parameter TypeLink(int typeId) => new()
    {
        Id = -1002050, Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" },
        StorageType = StorageType.ElementId, Value = typeId,
    };

    private static RawCompoundStructureLayer[] Layers() => [new(0, 1, 0, 700, 0, 0, 0)];

    /// <summary>The roof 243274 and its type 507024 as the reader holds them: the roof's name is scanned, the type's name
    /// native; the unique HostObjAttr and RoofAttributes roots of the type hold the real name.</summary>
    private static (Element Roof, Element Type, ParsingState State) RoofCase()
    {
        var type = new Element
        {
            Id = 507024, CategoryId = -2000035, Name = "B1020", SerializedSystemTypeClassId = 3452,
            SerializedHostTypeLayers = Layers(),
        };
        var roof = new Element
        {
            Id = 243274, CategoryId = -2000035, Name = "M_Wind Power Generator", TypeId = 507024,
            SerializedSystemTypeId = 507024,
        };
        roof.Parameters.Add(TypeLink(507024));
        var state = new ParsingState();
        state.NativeElementNames[507024] = "B1020";
        state.ElemScanStrings[243274] = ["M_Wind Power Generator", "Wind Power Generator-0000-CAN-ENU", "Master Bedroom"];
        state.HostTypeCompoundStructures[507024] = (1, 3452, Layers(), "SG Metal Panels roof");
        state.RoofOrRailingTypeNames[507024] = (1, 3452, "SG Metal Panels roof");
        return (roof, type, state);
    }

    private static string? Name(Element element, ParsingState state, params Element[] others)
    {
        var all = new List<Element> { element };
        all.AddRange(others);
        var byId = new Dictionary<int, Element>();
        var counts = new Dictionary<int, int>();
        foreach (Element e in all)
        {
            byId.TryAdd(e.Id, e);
            counts[e.Id] = counts.GetValueOrDefault(e.Id) + 1;
        }
        return ValidatedElementNames.Resolve(element, byId, counts, state)?.Name;
    }

    [Fact]
    public void Test2022Roof_TakesTheOwnTypeNameNotTheScannedWindGenerator()
    {
        var (roof, type, state) = RoofCase();
        Assert.Equal("SG Metal Panels roof", Name(roof, state, type));
        // the general name is untouched by resolving: the IFC exporter and the API read it
        Assert.Equal("M_Wind Power Generator", roof.Name);
    }

    [Fact]
    public void Test2022RoofType_TakesItsOwnRootNotTheNativeAssemblyCode()
    {
        var (roof, type, state) = RoofCase();
        Assert.Equal("SG Metal Panels roof", Name(type, state, roof));
    }

    [Fact]
    public void ScannedOnlyName_BecomesNull()
    {
        var element = new Element { Id = 100, CategoryId = -2000035, Name = "Arial" };
        var state = new ParsingState();
        state.ElemScanStrings[100] = ["Arial", "Ref. Level"];
        state.ElemStrings[100] = ["Width"];
        Assert.Null(Name(element, state));
    }

    [Fact]
    public void NativeBlockNameAlone_IsNotAValidation()
    {
        var type = new Element { Id = 507024, CategoryId = -2000035, Name = "B1020" };
        var state = new ParsingState();
        state.NativeElementNames[507024] = "B1020";
        Assert.Null(Name(type, state));
    }

    [Fact]
    public void NameTableName_Stays_AndBeatsTheNativeString()
    {
        var type = new Element { Id = 428955, CategoryId = -2000170, Name = "Structural Insulation Panel Wall" };
        var state = new ParsingState();
        state.NativeElementNames[428955] = "Structural Insulation Panel Wall";
        state.ElementNameTable[428955] = "SIP 202mm Wall - conc clad";
        Assert.Equal("SIP 202mm Wall - conc clad", Name(type, state));
    }

    [Fact]
    public void NameTableName_WinsOverTheNameOfItsOwnNestedType()
    {
        // a type that has a type link of its own (a nested symbol) is named by its own entry
        var nested = new Element { Id = 20, CategoryId = -2001350 };
        nested.Parameters.Add(TypeLink(20));
        var type = new Element { Id = 10, CategoryId = -2001350, TypeId = 20 };
        type.Parameters.Add(TypeLink(20));
        var state = new ParsingState();
        state.ElementNameTable[10] = "600 x 600 x 900mm";
        Assert.Equal("600 x 600 x 900mm", Name(type, state, nested));
    }

    [Fact]
    public void FamilyInstance_TakesTheNameTableNameOfItsAgreedType_AsTheIfcExporterNamesIt()
    {
        var type = new Element { Id = 500, CategoryId = -2000023, Name = "Arial", ValidatedIfcTypeObjectName = "Single-Flush:800 x 2100", ValidatedIfcTypeOwnName = "800 x 2100" };
        var door = new Element { Id = 100, CategoryId = -2000023, Name = "Master Bedroom", TypeId = 500 };
        door.Parameters.Add(TypeLink(500));
        var state = new ParsingState();
        Assert.Equal("800 x 2100", Name(door, state, type));
        // the IFC product name carries the same type name
        string ifc = IfcExporter.ResolveProductFamilyAndTypeName(door, "IFCDOOR",
            new Dictionary<int, Element> { [100] = door, [500] = type }, new Dictionary<int, int> { [100] = 1, [500] = 1 }, true);
        Assert.Equal("Single-Flush:800 x 2100", ifc);
    }

    [Theory]
    [InlineData("disagree")] [InlineData("self")] [InlineData("duplicate")] [InlineData("unvalidated_type")]
    public void FamilyInstance_WithoutAnAgreedUniqueValidatedType_IsNull(string mode)
    {
        var type = new Element { Id = 500, CategoryId = -2000023, Name = "800 x 2100", ValidatedIfcTypeOwnName = mode == "unvalidated_type" ? null : "800 x 2100" };
        var door = new Element { Id = 100, CategoryId = -2000023, Name = "800 x 2100", TypeId = mode == "disagree" ? 501 : 500 };
        door.Parameters.Add(TypeLink(mode == "self" ? 100 : 500));
        var state = new ParsingState();
        var others = mode == "duplicate" ? new[] { type, type } : new[] { type };
        Assert.Null(Name(door, state, others));
    }

    [Fact]
    public void TypeElementWithASymbolRoot_NeverTakesTheNameOfItsTypeLink()
    {
        // sample project A window symbol 10109851: it owns a Symbol root whose name is unreadable, its type link points at the
        // symbol "PVC", and the current profile IFC tags a window style named after the balcony door type with its id.
        var other = new Element { Id = 1754581, CategoryId = -2000014, ValidatedIfcTypeObjectName = "Окно:ПВХ", ValidatedIfcTypeOwnName = "ПВХ" };
        var symbol = new Element { Id = 10109851, CategoryId = -2000014, Name = "ПВХ", TypeId = 1754581 };
        symbol.Parameters.Add(TypeLink(1754581));
        var state = new ParsingState();
        state.SystemTypeRoots[10109851] = (1, 1829, null);
        state.FamilySymbolNames[10109851] = (1, null);
        Assert.Null(Name(symbol, state, other));
        // the same link on an occurrence (no Symbol root) names it after its type
        state.SystemTypeRoots.Remove(10109851);
        state.FamilySymbolNames.Remove(10109851);
        Assert.Equal("ПВХ", Name(symbol, state, other));
        // a Symbol root with a validated own name of its own keeps that name
        state.SystemTypeRoots[10109851] = (1, 1829, null);
        state.FamilySymbolNames[10109851] = (1, "Балкон дверь_2створки_1500х2390");
        Assert.Equal("Балкон дверь_2створки_1500х2390", Name(symbol, state, other));
    }

    [Fact]
    public void FloorReferenceSource_IsNotATypeName_TheHostTypeRootIs()
    {
        // sample project C floor 180171: the 0xFFFF077B terminal string of its type is the assembly code
        var type = new Element { Id = 199698, CategoryId = -2000032, Name = "B1010", SerializedHostTypeLayers = Layers() };
        var floor = new Element { Id = 180171, CategoryId = -2000032, Name = "B1010", TypeId = 199698, ValidatedFloorTypeReferenceName = "B1010", SerializedSystemTypeId = 199698 };
        floor.Parameters.Add(TypeLink(199698));
        var state = new ParsingState();
        Assert.Null(Name(floor, state, new Element { Id = 199698, CategoryId = -2000032 }));
        state.HostTypeCompoundStructures[199698] = (1, 1915, Layers(), "8\" Hollow Core Plank w/2\" Topping");
        Assert.Equal("8\" Hollow Core Plank w/2\" Topping", Name(floor, state, type));
        Assert.Equal("8\" Hollow Core Plank w/2\" Topping", Name(type, state, floor));
    }

    [Fact]
    public void HostTypeRootWithoutTheValidatedClassAndCategoryValidation_IsNotUsed()
    {
        var type = new Element { Id = 199698, CategoryId = -2000032 }; // SerializedHostTypeLayers stays null
        var floor = new Element { Id = 180171, CategoryId = -2000032, SerializedSystemTypeId = 199698 };
        var state = new ParsingState();
        state.HostTypeCompoundStructures[199698] = (1, 1915, Layers(), "8\" Hollow Core Plank w/2\" Topping");
        Assert.Null(Name(floor, state, type));
        Assert.Null(Name(type, state, floor));
    }

    [Fact]
    public void WallTakesItsTypesOwnSymbolName()
    {
        var type = new Element { Id = 232754, CategoryId = -2000011, SerializedBasicWallTypeFunction = 0, SerializedBasicWallTypeName = "Interior - 165 Partition (1-hr)", Name = "C1010145" };
        var wall = new Element { Id = 200, CategoryId = -2000011, Name = "201A", TypeId = 232754 };
        wall.Parameters.Add(TypeLink(232754));
        var state = new ParsingState();
        Assert.Equal("Interior - 165 Partition (1-hr)", Name(wall, state, type));
        Assert.Equal("Interior - 165 Partition (1-hr)", Name(type, state, wall));
    }

    [Fact]
    public void ExporterClassValidation_NamesAWindowByItsSymbol()
    {
        var type = new Element { Id = 600, CategoryId = -2000014, Name = "Arial" };
        var window = new Element { Id = 300, CategoryId = -2000014, Name = "Arial", TypeId = 600, ValidatedWindowTypeReferenceName = "M_Fixed 0915 x 1220mm" };
        window.Parameters.Add(TypeLink(600));
        Assert.Equal("M_Fixed 0915 x 1220mm", Name(window, new ParsingState(), type));
        window.ValidatedWindowTypeReferenceIsExplicit = true; // an explicit Reference parameter says nothing about the type
        Assert.Null(Name(window, new ParsingState(), type));
    }

    [Fact]
    public void MepCurve_TakesItsOwnTypeRoot_NotTheNameTableString()
    {
        var type = new Element { Id = 700, CategoryId = -2008044, ValidatedMepTypeName = "Standard" };
        var pipe = new Element { Id = 701, CategoryId = -2008044, Name = "Pipe Types", SerializedMepCurveTypeId = 700 };
        var state = new ParsingState();
        state.ElementNameTable[700] = "Pipe Types";
        Assert.Equal("Standard", Name(type, state, pipe));
        Assert.Equal("Standard", Name(pipe, state, type));
    }

    [Fact]
    public void Level_TakesTheDatumPlaneText_AndMaterialItsOwnRoot()
    {
        var level = new Element { Id = 311, CategoryId = -2000240, Name = "8mm Head" };
        var notLevel = new Element { Id = 312, CategoryId = -2000240, Name = "Elevation Mark Body" };
        var material = new Element { Id = 26, CategoryId = -2000700, Name = "Soda Lime Glass", SerializedMaterialName = "Glass" };
        var state = new ParsingState();
        var levelParams = new TfBlockDeserializer.NativeParams();
        levelParams.Fields["__isLevel"] = 1;
        levelParams.StringFields["m_text"] = "Level 1";
        state.NativeParams[311] = levelParams;
        var otherParams = new TfBlockDeserializer.NativeParams();
        otherParams.StringFields["m_text"] = "Not a level";
        state.NativeParams[312] = otherParams;
        Assert.Equal("Level 1", Name(level, state));
        Assert.Null(Name(notLevel, state));
        Assert.Equal("Glass", Name(material, state));
    }

    [Fact]
    public void Apply_ReplacesTheNameOfEveryElementAndCountsTheValidatedOnes()
    {
        var (roof, type, state) = RoofCase();
        var scanned = new Element { Id = 100, CategoryId = -2000035, Name = "Arial" };
        var info = new RevitFileInfo { Elements = [roof, type, scanned] };
        Assert.Equal(2, ValidatedElementNames.Apply(info, state));
        Assert.Equal("SG Metal Panels roof", roof.Name);
        Assert.Equal("SG Metal Panels roof", type.Name);
        Assert.Null(scanned.Name);
    }

    [Fact]
    public void NamedElement_ReportsOnlyItsOwnName()
    {
        // the name table entry of a type and the chain name of its nested symbol are different elements' names
        var nested = new Element { Id = 20, CategoryId = -2001350, ValidatedIfcTypeOwnName = "600mm Diameter" };
        var type = new Element { Id = 10, CategoryId = -2001350, TypeId = 20 };
        type.Parameters.Add(TypeLink(20));
        var state = new ParsingState();
        state.ElementNameTable[10] = "600 x 600 x 900mm";
        var byId = new Dictionary<int, Element> { [10] = type, [20] = nested };
        var counts = new Dictionary<int, int> { [10] = 1, [20] = 1 };
        var names = ValidatedElementNames.Candidates(type, byId, counts, state).Select(validation => validation.Name).Distinct().ToList();
        Assert.Equal(["600 x 600 x 900mm"], names);
    }
}

/// <summary>The real Test_2022.rvt: the reader's own state, the validated names of the roof 243274 and its neighbours, and
/// the unchanged general name that the IFC exporter and the API keep reading.</summary>
public class ValidatedElementNamesTest2022Tests : IDisposable
{
    private readonly Dictionary<int, string> _original = new();
    private readonly Dictionary<int, string?> _validated = new();
    private readonly int _named;

    public ValidatedElementNamesTest2022Tests()
    {
        string path = TestFileFixture.FindTestFile("Test_2022.rvt");
        var reader = new RvtFileReader();
        RevitFileInfo info = reader.Read(path);
        ParsingState state = reader.LastParsingState!;
        foreach (Element element in info.Elements)
            if (element.Name != null) _original.TryAdd(element.Id, element.Name);
        _named = ValidatedElementNames.Apply(info, state);
        foreach (Element element in info.Elements)
            _validated.TryAdd(element.Id, element.Name);
    }

    public void Dispose() { }

    [SkippableFact]
    public void Roof243274_IsNotNamedAfterTheWindGenerator()
    {
        Assert.Equal("M_Wind Power Generator", _original[243274]); // Element.Name keeps the scanned value
        Assert.Equal("SG Metal Panels roof", _validated[243274]);
        Assert.Equal("SG Metal Panels roof", _validated[507024]); // the type element, whose native string is "B1020"
    }

    [SkippableFact]
    public void ScannedAndFallbackNames_BecomeNullAndValidatedOnesStay()
    {
        Assert.Equal("Generic 300", _validated[418183]);        // floor, scanned "Master Bedroom"
        Assert.Equal("SH_Curtain wall", _validated[423099]);    // curtain wall, scanned "201A"
        Assert.Null(_validated[513254]);                        // stairs: its type has no validated name
        Assert.Equal("Level 1", _validated[311]);               // level, DatumPlane.m_text
        Assert.True(_named > 0);
        Assert.DoesNotContain(_validated.Values, name => name is "Arial" or "B1020");
    }
}
