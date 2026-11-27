using RvtIO.Ifc;
using RvtIO.Model;
using System.Text.RegularExpressions;
using Xunit;

namespace RvtIO.Tests;

/// <summary>
/// Types: <c>IfcXxxType</c>/<c>IfcXxxStyle</c> objects plus <c>IfcRelDefinesByType</c> and type-held <c>HasPropertySets</c>.
/// One type object per distinct canonical TypeId (post-ApplyNativeParams <see cref="Element.FamilyTypeId"/>) among the emitted products
/// of the governed classes; IFCRAILING is gated on <see cref="Element.NativeMasterSymbolId"/>; IFCROOF is never typed.
/// Type-held IsExternal covers railings and basic wall types with unique native root source.
/// Missing or conflicting wall source leaves HasPropertySets empty.
/// </summary>
public class IfcExporterTypeObjectsTests
{
    private const int RailingCategoryId = -2000126;   // OST_StairsRailing
    private const int RoofCategoryId = -2000035;       // OST_Roofs
    private const int DoorCategoryId = -2000023;       // OST_Doors
    private const int WallCategoryId = -2000011;       // OST_Walls
    private const int ElemTypeParamId = -1002050;      // ELEM_TYPE_PARAM
    private const int PhaseCreatedParamId = -1012100;

    [Fact]
    public void Railing_WithNativeMasterSymbolIdAndResolvableType_GetsIfcRailingTypeAndRel()
    {
        Element type = TypeElement(id: 500, name: "h 900", familyName: "Railing family");
        Element railing = Railing(id: 100, elemTypeParamId: 500, nativeMasterSymbolId: 500);
        string ifc = ExportToText(type, railing);

        int typeId = SoleEntityId(ifc, "IFCRAILINGTYPE");
        int railingId = SoleEntityId(ifc, "IFCRAILING");
        var args = EntityArgs(ifc, typeId, "IFCRAILINGTYPE");

        // GlobalId, OwnerHistory, Name, Description, ApplicableOccurrence, HasPropertySets,
        // RepresentationMaps, Tag, ElementType, PredefinedType.
        Assert.Equal(10, args.Count);
        Assert.Equal($"'{IfcGuid.FromName("type:500")}'", args[0]);
        Assert.Equal("'Railing family:h 900'", args[2]);
        Assert.Matches(@"^\(#\d+\)$", args[5]); // HasPropertySets: one inline Pset_RailingCommon.
        Assert.Equal("'500'", args[7]); // Tag = the TYPE element's own Revit ElementId.
        Assert.Equal("$", args[8]); // ElementType always empty.
        Assert.Equal(".NOTDEFINED.", args[9]); // Required enum cannot be omitted.

        var relArgs = SoleRelDefinesByType(ifc);
        Assert.Equal($"(#{railingId})", relArgs.RelatedObjects);
        Assert.Equal(typeId, relArgs.RelatingType);
        Assert.Equal(IfcGuid.FromName("rel-defines-by-type:500"), relArgs.GlobalId);
    }

    [Fact]
    public void Railing_WithoutNativeMasterSymbolId_StaysUntyped()
    {
        // Railings without a decoded own FamilyInstance.m_masterSymbolId are never typed: a resolvable ELEM_TYPE_PARAM alone is not
        // enough for IFCRAILING, unlike every other governed class.
        Element type = TypeElement(id: 500, name: "h 900", familyName: "Railing family");
        Element railing = Railing(id: 100, elemTypeParamId: 500, nativeMasterSymbolId: null);
        string ifc = ExportToText(type, railing);

        Assert.DoesNotContain("IFCRAILINGTYPE", ifc);
        Assert.DoesNotContain("IFCRELDEFINESBYTYPE", ifc);
    }

    [Fact]
    public void TwoRailingsSharingSameCanonicalType_AreGroupedUnderOneTypeObject()
    {
        Element type = TypeElement(id: 500, name: "h 900", familyName: "Railing family");
        Element railingA = Railing(id: 100, elemTypeParamId: 500, nativeMasterSymbolId: 500);
        Element railingB = Railing(id: 200, elemTypeParamId: 500, nativeMasterSymbolId: 500, centreX: 10);
        string ifc = ExportToText(type, railingA, railingB);

        Assert.Single(EntityIds(ifc, "IFCRAILINGTYPE"));
        int railingAId = SoleEntityIdForTag(ifc, "IFCRAILING", "100");
        int railingBId = SoleEntityIdForTag(ifc, "IFCRAILING", "200");

        var relArgs = SoleRelDefinesByType(ifc);
        Assert.Contains($"#{railingAId}", relArgs.RelatedObjects);
        Assert.Contains($"#{railingBId}", relArgs.RelatedObjects);
    }

    [Fact]
    public void Railing_WithUnresolvableCanonicalType_StaysUntyped()
    {
        // Fail-closed: ELEM_TYPE_PARAM points to an id absent from the model entirely.
        Element railing = Railing(id: 100, elemTypeParamId: 999999, nativeMasterSymbolId: 999999);
        string ifc = ExportToText(railing);

        Assert.DoesNotContain("IFCRAILINGTYPE", ifc);
        Assert.DoesNotContain("IFCRELDEFINESBYTYPE", ifc);
    }

    [Fact]
    public void Railing_WithoutElemTypeParam_StaysUntyped()
    {
        // Fail-closed: no canonical TypeId at all (an unemitted type is honest
        // under-coverage, never a guessed relation).
        Element railing = Railing(id: 100, elemTypeParamId: null, nativeMasterSymbolId: null);
        string ifc = ExportToText(railing);

        Assert.DoesNotContain("IFCRAILINGTYPE", ifc);
        Assert.DoesNotContain("IFCRELDEFINESBYTYPE", ifc);
    }

    [Fact]
    public void Roof_NeverGetsTyped_EvenWithFullyResolvableType()
    {
        // IFCROOF is deliberately absent from the governed-class map: the roofs that carry a type are the geometry-less container roofs
        // this exporter never emits as products, so an emitted roof stays untyped even when its TypeId would resolve.
        Element type = TypeElement(id: 500, name: "Roof type", familyName: "Roof family");
        Element roof = Product(
            id: 100, categoryId: RoofCategoryId, category: "OST_Roofs",
            elemTypeParamId: 500, nativeMasterSymbolId: null);
        string ifc = ExportToText(type, roof);

        Assert.Contains("IFCROOF(", ifc);
        Assert.DoesNotContain("IFCROOFTYPE", ifc);
        Assert.DoesNotContain("IFCRELDEFINESBYTYPE", ifc);
    }

    [Fact]
    public void Door_GetsIfcDoorStyle_NotIfcElementTypeShape()
    {
        // IfcDoorStyle is an IfcTypeProduct subtype, not IfcElementType: no ElementType attribute; instead OperationType/ConstructionType/
        // ParameterTakesPrecedence/Sizeable (safe defaults).
        Element type = TypeElement(id: 500, name: "Door type", familyName: "Door family");
        Element door = Product(
            id: 100, categoryId: DoorCategoryId, category: "OST_Doors",
            elemTypeParamId: 500, nativeMasterSymbolId: null);
        string ifc = ExportToText(type, door);

        Assert.DoesNotContain("IFCDOORTYPE", ifc);
        int typeId = SoleEntityId(ifc, "IFCDOORSTYLE");
        var args = EntityArgs(ifc, typeId, "IFCDOORSTYLE");
        Assert.Equal(12, args.Count);
        Assert.Equal("'500'", args[7]); // Tag
        Assert.Equal(".NOTDEFINED.", args[8]); // OperationType
        Assert.Equal(".NOTDEFINED.", args[9]); // ConstructionType
        Assert.Equal(".F.", args[10]); // ParameterTakesPrecedence
        Assert.Equal(".F.", args[11]); // Sizeable
    }

    [Fact]
    public void TypeName_CombinesFamilyNameAndName_WhenBothPresent()
    {
        Element type = TypeElement(id: 500, name: "50x150 mm", familyName: "Steel profile");
        Element railing = Railing(id: 100, elemTypeParamId: 500, nativeMasterSymbolId: 500);
        string ifc = ExportToText(type, railing);

        int typeId = SoleEntityId(ifc, "IFCRAILINGTYPE");
        Assert.Equal("'Steel profile:50x150 mm'", EntityArgs(ifc, typeId, "IFCRAILINGTYPE")[2]);
    }

    [Fact]
    public void TypeName_FallsBackToBareName_WhenFamilyNameMissing()
    {
        // System-family classes (Wall/Slab/Covering/...) have no FamilyName in the file, so the bare TypeName is used
        // instead of fabricating a family segment.
        Element type = TypeElement(id: 500, name: "Bare type name", familyName: null);
        Element railing = Railing(id: 100, elemTypeParamId: 500, nativeMasterSymbolId: 500);
        string ifc = ExportToText(type, railing);

        int typeId = SoleEntityId(ifc, "IFCRAILINGTYPE");
        Assert.Equal("'Bare type name'", EntityArgs(ifc, typeId, "IFCRAILINGTYPE")[2]);
    }

    [Fact]
    public void Railing_InlinePset_IsPsetRailingCommonWithIsExternalFalse()
    {
        // Every IFCRAILINGTYPE carries exactly Pset_RailingCommon with one property, IsExternal, constant .F.: no FUNCTION_PARAM path or
        // category rule applies to railings, so the value always falls through to the default.
        Element type = TypeElement(id: 500, name: "h 900", familyName: "Railing family");
        Element railing = Railing(id: 100, elemTypeParamId: 500, nativeMasterSymbolId: 500);
        string ifc = ExportToText(type, railing);

        int typeId = SoleEntityId(ifc, "IFCRAILINGTYPE");
        var typeArgs = EntityArgs(ifc, typeId, "IFCRAILINGTYPE");
        int psetId = int.Parse(typeArgs[5].Trim('(', ')').TrimStart('#'));

        var psetArgs = EntityArgs(ifc, psetId, "IFCPROPERTYSET");
        Assert.Equal(5, psetArgs.Count); // GlobalId, OwnerHistory, Name, Description, HasProperties.
        Assert.Equal("'Pset_RailingCommon'", psetArgs[2]);
        int valueId = int.Parse(psetArgs[4].Trim('(', ')').TrimStart('#'));

        var valueArgs = EntityArgs(ifc, valueId, "IFCPROPERTYSINGLEVALUE");
        Assert.Equal("'IsExternal'", valueArgs[0]);
        Assert.Equal("IFCBOOLEAN(.F.)", valueArgs[2]);
    }

    [Fact]
    public void TwoRailingTypes_ShareOnePooledIsExternalPropertySingleValueInstance()
    {
        // IsExternal=.F. is backed by the same pooled IfcPropertySingleValue entity across every type and product that carries it.
        // Two distinct railing types must reference the identical pooled value entity, not two separate ones with the same content.
        Element typeA = TypeElement(id: 500, name: "h 900", familyName: "Railing family A");
        Element typeB = TypeElement(id: 600, name: "h 1100", familyName: "Railing family B");
        Element railingA = Railing(id: 100, elemTypeParamId: 500, nativeMasterSymbolId: 500);
        Element railingB = Railing(id: 200, elemTypeParamId: 600, nativeMasterSymbolId: 600, centreX: 10);
        string ifc = ExportToText(typeA, typeB, railingA, railingB);

        var typeIds = EntityIds(ifc, "IFCRAILINGTYPE");
        Assert.Equal(2, typeIds.Count);
        var valueIds = typeIds
            .Select(typeId => EntityArgs(ifc, typeId, "IFCRAILINGTYPE")[5])
            .Select(hps => int.Parse(hps.Trim('(', ')').TrimStart('#')))
            .Select(psetId => EntityArgs(ifc, psetId, "IFCPROPERTYSET")[4])
            .Select(hasProps => int.Parse(hasProps.Trim('(', ')').TrimStart('#')))
            .ToList();
        Assert.Equal(valueIds[0], valueIds[1]);
    }

    [Fact]
    public void Wall_HasPropertySets_StaysEmpty_WithoutOwnRootSource()
    {
        // IFCWALLTYPE's only property (Pset_WallCommon.IsExternal via BuiltInParameter.FUNCTION_PARAM on the wall type element) has no decoded
        // source, so HasPropertySets stays "$" for Wall: an honest omission, not a guess.
        Element type = TypeElement(id: 500, name: "200mm", familyName: null);
        Element wall = Product(
            id: 100, categoryId: WallCategoryId, category: "OST_Walls",
            elemTypeParamId: 500, nativeMasterSymbolId: null);
        string ifc = ExportToText(type, wall);

        int typeId = SoleEntityId(ifc, "IFCWALLTYPE");
        Assert.Equal("$", EntityArgs(ifc, typeId, "IFCWALLTYPE")[5]);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void BasicWallType_OwnFunctionProducesIsExternal(int function)
    {
        var type = TypeElement(500, "Own wall", null);
        type.CategoryId = WallCategoryId;
        type.SerializedBasicWallTypeFunction = function;
        var wall = Product(100, WallCategoryId, "OST_Walls", 500, null);
        string ifc = ExportToText(type, wall);
        int typeId = SoleEntityId(ifc, "IFCWALLTYPE");
        var args = EntityArgs(ifc, typeId, "IFCWALLTYPE");
        int psetId = int.Parse(args[5].Trim('(', ')').TrimStart('#'));
        var pset = EntityArgs(ifc, psetId, "IFCPROPERTYSET");
        Assert.Equal("'Pset_WallCommon'", pset[2]);
        Assert.Equal($"'{IfcGuid.FromName("type-wall-common-pset:500")}'", pset[0]);
        Assert.Matches(@"^\(#\d+\)$", pset[4]);
        int valueId = int.Parse(pset[4].Trim('(', ')').TrimStart('#'));
        var value = EntityArgs(ifc, valueId, "IFCPROPERTYSINGLEVALUE");
        Assert.Equal("'IsExternal'", value[0]);
        Assert.Equal(function == 0 ? "IFCBOOLEAN(.F.)" : "IFCBOOLEAN(.T.)", value[2]);
    }

    [Theory]
    [InlineData("missing")] [InlineData("category")] [InlineData("range")]
    [InlineData("IsExternal")] [InlineData("IsExternal[Type]")]
    [InlineData("conflict")] [InlineData("storage")] [InlineData("approximate")]
    public void BasicWallType_IncompleteOrOverriddenFunctionStaysUnvalidated(string mutation)
    {
        var type = TypeElement(500, "Own wall", null);
        type.CategoryId = mutation == "category" ? -2000032 : WallCategoryId;
        type.SerializedBasicWallTypeFunction = mutation == "missing" ? null : mutation == "range" ? 6 : 1;
        if (mutation.StartsWith("IsExternal"))
            type.Parameters.Add(new Parameter { Definition = new Definition { Name = mutation }, StorageType = StorageType.Integer, Value = 0 });
        if (mutation is "conflict" or "storage" or "approximate")
            type.Parameters.Add(new Parameter { Id = -1001006,
                StorageType = mutation == "storage" ? StorageType.String : StorageType.Integer,
                Value = mutation == "storage" ? "1" : mutation == "conflict" ? 0 : 1,
                IsApproximate = mutation == "approximate" });
        string ifc = ExportToText(type, Product(100, WallCategoryId, "OST_Walls", 500, null));
        Assert.Equal("$", EntityArgs(ifc, SoleEntityId(ifc, "IFCWALLTYPE"), "IFCWALLTYPE")[5]);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void CurtainWallType_OwnFunctionProducesOnlyCurtainCommon(int function)
    {
        var type = TypeElement(500, "Own curtain", null);
        type.CategoryId = WallCategoryId;
        type.SerializedCurtainWallFunction = function;
        string ifc = ExportToText(type, Product(100, WallCategoryId, "OST_Walls", 500, null),
            new Element { Id = 101, CategoryId = -2000171, References = [100] });
        var args = EntityArgs(ifc, SoleEntityId(ifc, "IFCCURTAINWALLTYPE"), "IFCCURTAINWALLTYPE");
        int psetId = int.Parse(args[5].Trim('(', ')').TrimStart('#'));
        var pset = EntityArgs(ifc, psetId, "IFCPROPERTYSET");
        Assert.Equal("'Pset_CurtainWallCommon'", pset[2]);
        Assert.Equal($"'{IfcGuid.FromName("type-curtain-wall-common-pset:500")}'", pset[0]);
        int valueId = int.Parse(pset[4].Trim('(', ')').TrimStart('#'));
        var value = EntityArgs(ifc, valueId, "IFCPROPERTYSINGLEVALUE");
        Assert.Equal("'IsExternal'", value[0]);
        Assert.Equal(function == 0 ? "IFCBOOLEAN(.F.)" : "IFCBOOLEAN(.T.)", value[2]);
        Assert.Equal(".NOTDEFINED.", args[9]);
        Assert.DoesNotContain("'Pset_WallCommon'", ifc);
    }

    [Theory]
    [InlineData("missing")] [InlineData("category")] [InlineData("range")]
    [InlineData("IsExternal")] [InlineData("IsExternal[Type]")]
    [InlineData("conflict")] [InlineData("storage")] [InlineData("approximate")]
    [InlineData("ambiguous_roots")] [InlineData("null_conflict")]
    public void CurtainWallType_IncompleteOrConflictingFunctionIsOmitted(string mutation)
    {
        var type = TypeElement(500, "Own curtain", null);
        type.CategoryId = mutation == "category" ? -2000032 : WallCategoryId;
        type.SerializedCurtainWallFunction = mutation == "missing" ? null : mutation == "range" ? 6 : 1;
        if (mutation == "ambiguous_roots") type.SerializedBasicWallTypeFunction = 1;
        if (mutation.StartsWith("IsExternal"))
            type.Parameters.Add(new Parameter { Definition = new Definition { Name = mutation }, StorageType = StorageType.Integer, Value = 0 });
        if (mutation is "conflict" or "storage" or "approximate" or "null_conflict")
            type.Parameters.Add(new Parameter { Id = -1001006,
                StorageType = mutation == "storage" ? StorageType.String : StorageType.Integer,
                Value = mutation == "null_conflict" ? null : mutation == "storage" ? "1" : mutation == "conflict" ? 0 : 1,
                IsApproximate = mutation == "approximate" });
        string ifc = ExportToText(type, Product(100, WallCategoryId, "OST_Walls", 500, null),
            new Element { Id = 101, CategoryId = -2000171, References = [100] });
        Assert.Equal("$", EntityArgs(ifc, SoleEntityId(ifc, "IFCCURTAINWALLTYPE"), "IFCCURTAINWALLTYPE")[5]);
    }

    [Fact]
    public void CurtainWallType_OccurrenceOverrideDoesNotBecomeATypeProperty()
    {
        var type = TypeElement(500, "Own curtain", null);
        type.CategoryId = WallCategoryId;
        type.SerializedCurtainWallFunction = 1;
        var wall = Product(100, WallCategoryId, "OST_Walls", 500, null);
        wall.Parameters.Add(new Parameter { Definition = new Definition { Name = "IsExternal" }, StorageType = StorageType.Integer, Value = 0 });
        string ifc = ExportToText(type, wall, new Element { Id = 101, CategoryId = -2000171, References = [100] });
        var args = EntityArgs(ifc, SoleEntityId(ifc, "IFCCURTAINWALLTYPE"), "IFCCURTAINWALLTYPE");
        var pset = EntityArgs(ifc, int.Parse(args[5].Trim('(', ')').TrimStart('#')), "IFCPROPERTYSET");
        var value = EntityArgs(ifc, int.Parse(pset[4].Trim('(', ')').TrimStart('#')), "IFCPROPERTYSINGLEVALUE");
        Assert.Equal("IFCBOOLEAN(.T.)", value[2]);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void CurtainWallOccurrence_UsesCanonicalOwnTypeFunction(int function)
    {
        var type = TypeElement(500, "Own curtain", null);
        type.CategoryId = WallCategoryId;
        type.SerializedCurtainWallFunction = function;
        string ifc = ExportToText(type, Product(100, WallCategoryId, "OST_Walls", 500, null),
            new Element { Id = 101, CategoryId = -2000171, References = [100] });
        int wallId = SoleEntityId(ifc, "IFCCURTAINWALL");
        int psetId = CurtainOccurrencePsetId(ifc, wallId);
        var pset = EntityArgs(ifc, psetId, "IFCPROPERTYSET");
        Assert.Equal($"'{IfcGuid.FromName("curtain-wall-common-pset:100")}'", pset[0]);
        int valueId = int.Parse(pset[4].Trim('(', ')').TrimStart('#'));
        Assert.Equal(function == 0 ? "IFCBOOLEAN(.F.)" : "IFCBOOLEAN(.T.)",
            EntityArgs(ifc, valueId, "IFCPROPERTYSINGLEVALUE")[2]);
        var typeArgs = EntityArgs(ifc, SoleEntityId(ifc, "IFCCURTAINWALLTYPE"), "IFCCURTAINWALLTYPE");
        Assert.Equal(pset[4], EntityArgs(ifc, int.Parse(typeArgs[5].Trim('(', ')').TrimStart('#')), "IFCPROPERTYSET")[4]);
    }

    [Theory]
    [InlineData("missing_type")] [InlineData("generic_type_only")] [InlineData("missing_function")]
    [InlineData("ambiguous_roots")] [InlineData("wrong_type_category")]
    [InlineData("instance_override")] [InlineData("type_override")]
    [InlineData("parameter_conflict")] [InlineData("approximate")]
    public void CurtainWallOccurrence_UnvalidatedOrOverriddenValueIsNotWritten(string mutation)
    {
        var type = TypeElement(500, "Own curtain", null);
        type.CategoryId = mutation == "wrong_type_category" ? -2000032 : WallCategoryId;
        type.SerializedCurtainWallFunction = mutation == "missing_function" ? null : 1;
        if (mutation == "ambiguous_roots") type.SerializedBasicWallTypeFunction = 1;
        var wall = Product(100, WallCategoryId, "OST_Walls", mutation == "generic_type_only" ? null : 500, null);
        if (mutation == "generic_type_only") wall.TypeId = 500;
        if (mutation is "instance_override" or "type_override")
            (mutation == "instance_override" ? wall : type).Parameters.Add(new Parameter
            { Definition = new Definition { Name = "IsExternal" }, StorageType = StorageType.Integer, Value = 0 });
        if (mutation is "parameter_conflict" or "approximate")
            type.Parameters.Add(new Parameter { Id = -1001006, StorageType = StorageType.Integer,
                Value = mutation == "parameter_conflict" ? 0 : 1, IsApproximate = mutation == "approximate" });
        var elements = new List<Element> { wall, new Element { Id = 101, CategoryId = -2000171, References = [100] } };
        if (mutation != "missing_type") elements.Add(type);
        string ifc = ExportToText(elements.ToArray());
        Assert.Single(EntityIds(ifc, "IFCCURTAINWALL"));
        Assert.DoesNotContain($"'{IfcGuid.FromName("curtain-wall-common-pset:100")}'", ifc);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void IfcCurtainWall_WithOwnBasicWallType_UsesTheSameFunctionRule(int function)
    {
        var type = TypeElement(500, "Own basic wall source", null);
        type.CategoryId = WallCategoryId;
        type.SerializedBasicWallTypeFunction = function;
        string ifc = ExportToText(type, Product(100, WallCategoryId, "OST_Walls", 500, null),
            new Element { Id = 101, CategoryId = -2000171, References = [100] });
        int occurrencePset = CurtainOccurrencePsetId(ifc, SoleEntityId(ifc, "IFCCURTAINWALL"));
        var typeArgs = EntityArgs(ifc, SoleEntityId(ifc, "IFCCURTAINWALLTYPE"), "IFCCURTAINWALLTYPE");
        var typePset = EntityArgs(ifc, int.Parse(typeArgs[5].Trim('(', ')').TrimStart('#')), "IFCPROPERTYSET");
        Assert.Equal("'Pset_CurtainWallCommon'", typePset[2]);
        Assert.Equal(typePset[4], EntityArgs(ifc, occurrencePset, "IFCPROPERTYSET")[4]);
        int valueId = int.Parse(typePset[4].Trim('(', ')').TrimStart('#'));
        Assert.Equal(function == 0 ? "IFCBOOLEAN(.F.)" : "IFCBOOLEAN(.T.)",
            EntityArgs(ifc, valueId, "IFCPROPERTYSINGLEVALUE")[2]);
        Assert.DoesNotContain("'Pset_WallCommon'", ifc);
    }

    [Fact]
    public void ValidatedStackedParent_IsNotAProduct_WhileItsComponentWallRemains()
    {
        var parentType = TypeElement(500, "Stacked parent", null);
        parentType.CategoryId = -2003500; parentType.IsSerializedStackedWallType = true;
        var childType = TypeElement(600, "Basic component", null);
        childType.CategoryId = WallCategoryId; childType.SerializedBasicWallTypeFunction = 1;
        var parent = Product(100, WallCategoryId, "OST_Walls", 500, null);
        var child = Product(200, WallCategoryId, "OST_Walls", 600, null, 10);
        string ifc = ExportToText(parentType, childType, parent, child,
            new Element { Id = 101, CategoryId = -2000171, References = [100] });
        Assert.Empty(EntityIds(ifc, "IFCCURTAINWALL"));
        Assert.Empty(EntityIds(ifc, "IFCCURTAINWALLTYPE"));
        Assert.Contains($"'{IfcGuid.FromElementId(200)}'", ifc);
        Assert.DoesNotContain($"'{IfcGuid.FromElementId(100)}'", ifc);
        Assert.Contains($"'{IfcGuid.FromName("type:600")}'", ifc);
        Assert.DoesNotContain($"'{IfcGuid.FromName("type:500")}'", ifc);
        Assert.NotNull(parent.Geometry);
    }

    private static int CurtainOccurrencePsetId(string ifc, int wallId)
    {
        var relations = EntityIds(ifc, "IFCRELDEFINESBYPROPERTIES")
            .Select(id => EntityArgs(ifc, id, "IFCRELDEFINESBYPROPERTIES"))
            .Where(args => args[4] == $"(#{wallId})")
            .Select(args => int.Parse(args[5].TrimStart('#')))
            .Where(id => EntityArgs(ifc, id, "IFCPROPERTYSET")[2] == "'Pset_CurtainWallCommon'");
        return Assert.Single(relations);
    }

    [Theory]
    [InlineData(-2000032)]
    [InlineData(-2000920)]
    [InlineData(-2001300)]
    public void RegisteredSlabType_CommonIsExternalUsesItsOwnTypeScope(int category)
    {
        var type = TypeElement(500, "Own slab type", null);
        type.CategoryId = category;
        var slab = Product(100, category, "Slab", 500, 500);
        slab.Parameters.Add(new Parameter { Definition = new Definition { Name = "IsExternal" },
            StorageType = StorageType.Integer, Value = 1 });
        string ifc = ExportToText(type, slab);
        Assert.Equal("IFCBOOLEAN(.F.)", TypeProperty(ifc, "IFCSLABTYPE", "Pset_SlabCommon", "IsExternal"));
        Assert.Null(TypeProperty(ifc, "IFCSLABTYPE", "Pset_SlabCommon", "Reference"));
    }

    [Theory]
    [InlineData("IsExternal", 1, true)]
    [InlineData("IsExternal[Type]", 0, false)]
    [InlineData("FUNCTION_PARAM", -1, true)]
    public void SlabType_ExactParameterPrecedenceIsNotAnOccurrenceDefault(string name, int value, bool expected)
    {
        var type = TypeElement(500, "Slab", null);
        type.CategoryId = -2000032;
        type.Parameters.Add(new Parameter { Id = name == "FUNCTION_PARAM" ? -1001006 : 123,
            Definition = new Definition { Name = name }, StorageType = StorageType.Integer, Value = value });
        string ifc = ExportToText(type, Product(100, -2000032, "Floor", 500, 500));
        Assert.Equal(expected ? "IFCBOOLEAN(.T.)" : "IFCBOOLEAN(.F.)",
            TypeProperty(ifc, "IFCSLABTYPE", "Pset_SlabCommon", "IsExternal"));
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("wrong-category")]
    [InlineData("host-on-type")]
    [InlineData("occurrence-as-type")]
    public void SlabType_UnregisteredOrBlockedScopeDoesNotCreateACommonSet(string mode)
    {
        var type = TypeElement(500, "Slab", null);
        type.CategoryId = mode == "wrong-category" ? -2001330 : -2000032;
        if (mode == "host-on-type") type.SerializedHostId = 900;
        if (mode == "blocked")
            type.Parameters.Add(new Parameter { Definition = new Definition { Name = "IsExternal" },
                StorageType = StorageType.String, Value = "{Width}" });
        var slab = Product(100, -2000032, "Floor", mode == "occurrence-as-type" ? 100 : 500, 500);
        string ifc = ExportToText(type, slab);
        Assert.Equal("$", EntityArgs(ifc, SoleEntityId(ifc, "IFCSLABTYPE"), "IFCSLABTYPE")[5]);
    }

    [Theory]
    [InlineData("absent", true)]
    [InlineData("zero", false)]
    [InlineData("one", true)]
    [InlineData("typed-zero", false)]
    [InlineData("double", false)]
    [InlineData("string", false)]
    [InlineData("unparseable", true)]
    [InlineData("null", true)]
    public void ColumnType_LoadBearingReadsOwnParameterBeforeStructuralCategory(string mode, bool expected)
    {
        var type = TypeElement(500, "Column", "Family");
        type.CategoryId = -2001330;
        if (mode != "absent")
            type.Parameters.Add(new Parameter {
                Definition = new Definition { Name = mode == "typed-zero" ? "LoadBearing[Type]" : "LoadBearing" },
                StorageType = mode == "double" ? StorageType.Double
                    : mode is "string" or "unparseable" ? StorageType.String : StorageType.Integer,
                Value = mode switch { "one" => 1, "double" => 0.9, "string" => "0",
                    "unparseable" => "not-an-integer", "null" => null, _ => 0 } });
        var column = Product(100, -2001330, "Column", 500, 500);
        column.Parameters.Add(new Parameter { Definition = new Definition { Name = "LoadBearing" },
            StorageType = StorageType.Integer, Value = expected ? 0 : 1 });
        string ifc = ExportToText(type, column);
        Assert.Equal(expected ? "IFCBOOLEAN(.T.)" : "IFCBOOLEAN(.F.)",
            TypeProperty(ifc, "IFCCOLUMNTYPE", "Pset_ColumnCommon", "LoadBearing"));
        Assert.Equal("IFCBOOLEAN(.F.)", TypeProperty(ifc, "IFCCOLUMNTYPE", "Pset_ColumnCommon", "IsExternal"));
    }

    [Fact]
    public void ComponentType_ReceivesTheMaterialOfItsFirstInstance()
    {
        // A mullion whose type holds an unset Material parameter (id -1002107) gets the shared "<Unnamed>" material;
        // the type is associated with the material list of the instance that created it.
        var type = TypeElement(500, "Type", "Family");
        type.CategoryId = -2000171;
        type.Parameters.Add(new Parameter { Id = -1002107, StorageType = StorageType.ElementId, Value = -1 });
        var component = Product(100, -2000171, "Mullion", 500, 500);
        component.TypeId = 500;
        string ifc = ExportToText(type, component);

        int material = SoleEntityId(ifc, "IFCMATERIAL");
        Assert.Equal("'<Unnamed>'", EntityArgs(ifc, material, "IFCMATERIAL")[0]);
        int memberEntity = SoleEntityId(ifc, "IFCMEMBER");
        int typeEntity = SoleEntityId(ifc, "IFCMEMBERTYPE");
        var relations = EntityIds(ifc, "IFCRELASSOCIATESMATERIAL")
            .Select(id => EntityArgs(ifc, id, "IFCRELASSOCIATESMATERIAL")).ToList();
        Assert.Equal(2, relations.Count);
        Assert.All(relations, args => Assert.Equal($"#{material}", args[5]));
        var productRel = Assert.Single(relations, args => args[4] == $"(#{memberEntity})");
        var typeRel = Assert.Single(relations, args => args[4] == $"(#{typeEntity})");
        Assert.Equal($"'{IfcGuid.FromName("materials:0")}'", productRel[0]);
        Assert.Equal($"'{IfcGuid.FromName("type-materials:0")}'", typeRel[0]);
    }

    [Fact]
    public void LegacyTypeMaterialRelationGlobalIds_StayStableWhenEarlierStepEntitiesShift()
    {
        Element MaterialElement(int id, string name) => new()
        {
            Id = id,
            TypeId = -1,
            IsBlockOnly = true,
            CategoryId = -2000700,
            Category = "OST_Materials",
            Name = name,
            HasShadingAppearance = true,
            Parameters =
            [
                new Parameter { Id = id + 1, Definition = new Definition { Name = "MATERIAL_PARAM_COLOR" }, Value = 0x112233, StorageType = StorageType.Integer },
                new Parameter { Id = id + 2, Definition = new Definition { Name = "MATERIAL_PARAM_SHININESS" }, Value = 25, StorageType = StorageType.Integer },
            ],
        };

        Element[] BuildBaseElements()
        {
            var steel = MaterialElement(700, "Steel");
            var glass = MaterialElement(710, "Glass");
            var wood = MaterialElement(720, "Wood");

            var singleType = TypeElement(500, "SingleType", "MullionFamily");
            singleType.CategoryId = -2000171;
            singleType.Parameters.Add(new Parameter { Id = -1002107, StorageType = StorageType.ElementId, Value = 700 });
            var singleMullion = Product(100, -2000171, "Mullion", 500, 500, centreX: 0);
            singleMullion.TypeId = 500;
            foreach (var face in singleMullion.Geometry!.Faces)
                face.RenderStyleId = 700;

            var multiType = TypeElement(510, "MultiType", "DoorFamily");
            multiType.CategoryId = DoorCategoryId;
            var door = Product(110, DoorCategoryId, "Doors", 510, 510, centreX: 10);
            door.TypeId = 510;
            door.Geometry!.Faces[0].RenderStyleId = 710;
            door.Geometry.Faces[1].RenderStyleId = 720;
            for (int i = 2; i < door.Geometry.Faces.Count; i++)
                door.Geometry.Faces[i].RenderStyleId = 710;

            var hostWall = Product(999, WallCategoryId, "Walls", elemTypeParamId: null, nativeMasterSymbolId: null, centreX: 20);
            return [steel, glass, wood, hostWall, singleType, singleMullion, multiType, door];
        }

        string baselineIfc = ExportToText(IfcProfile.Legacy, BuildBaseElements());

        // Prepend extra elements and custom properties so STEP entity numbers of materials,
        // material lists, products and types all shift significantly.
        var extraColumnType = TypeElement(400, "ExtraColType", "ExtraFamily");
        extraColumnType.CategoryId = -2001330;
        var extraColumn = Product(50, -2001330, "Structural Columns", 400, 400, centreX: -20);
        for (int p = 0; p < 12; p++)
        {
            extraColumn.Parameters.Add(new Parameter
            {
                Id = 800000 + p,
                Definition = new Definition { Id = 800000 + p, Name = $"SpanExtra_{p}" },
                StorageType = StorageType.Double,
                Value = 10.0 + p,
            });
        }
        string shiftedIfc = ExportToText(IfcProfile.Legacy, [extraColumnType, extraColumn, .. BuildBaseElements()]);

        // Verify the relating IFCMATERIAL and IFCMATERIALLIST STEP numbers actually shifted.
        int baseSteelEntity = EntityIds(baselineIfc, "IFCMATERIAL")
            .Single(id => EntityArgs(baselineIfc, id, "IFCMATERIAL")[0] == "'Steel'");
        int shiftedSteelEntity = EntityIds(shiftedIfc, "IFCMATERIAL")
            .Single(id => EntityArgs(shiftedIfc, id, "IFCMATERIAL")[0] == "'Steel'");
        Assert.NotEqual(baseSteelEntity, shiftedSteelEntity);

        int baseMatList = SoleEntityId(baselineIfc, "IFCMATERIALLIST");
        int shiftedMatList = SoleEntityId(shiftedIfc, "IFCMATERIALLIST");
        Assert.NotEqual(baseMatList, shiftedMatList);

        string expectedSingleProductGuid = $"'{IfcGuid.FromName("materials:700")}'";
        string expectedSingleTypeGuid = $"'{IfcGuid.FromName("type-materials:700")}'";
        string expectedMultiProductGuid = $"'{IfcGuid.FromName("materials:710,720")}'";
        string expectedMultiTypeGuid = $"'{IfcGuid.FromName("type-materials:710,720")}'";

        foreach ((string ifc, int steelEntity, int matListEntity) in new[]
        {
            (baselineIfc, baseSteelEntity, baseMatList),
            (shiftedIfc, shiftedSteelEntity, shiftedMatList),
        })
        {
            var rels = EntityIds(ifc, "IFCRELASSOCIATESMATERIAL")
                .Select(id => EntityArgs(ifc, id, "IFCRELASSOCIATESMATERIAL")).ToList();
            int memberTypeEntity = SoleEntityId(ifc, "IFCMEMBERTYPE");
            int doorStyleEntity = SoleEntityId(ifc, "IFCDOORSTYLE");

            var singleTypeRel = Assert.Single(rels, a => a[4] == $"(#{memberTypeEntity})" && a[5] == $"#{steelEntity}");
            Assert.Equal(expectedSingleTypeGuid, singleTypeRel[0]);

            var multiTypeRel = Assert.Single(rels, a => a[4] == $"(#{doorStyleEntity})" && a[5] == $"#{matListEntity}");
            Assert.Equal(expectedMultiTypeGuid, multiTypeRel[0]);

            Assert.Contains(rels, a => a[0] == expectedSingleProductGuid && a[5] == $"#{steelEntity}");
            Assert.Contains(rels, a => a[0] == expectedMultiProductGuid && a[5] == $"#{matListEntity}");
        }

        // Current profile keeps its existing MD5("{project GlobalId}IfcRelAssociatesMaterial:{first related GlobalId}") seed.
        string currentIfc = ExportToText(IfcProfile.Current, BuildBaseElements());
        int projectEntity = SoleEntityId(currentIfc, "IFCPROJECT");
        string projectGuid = EntityArgs(currentIfc, projectEntity, "IFCPROJECT")[0].Trim('\'');
        int currentMemberType = SoleEntityId(currentIfc, "IFCMEMBERTYPE");
        string currentMemberTypeGuid = EntityArgs(currentIfc, currentMemberType, "IFCMEMBERTYPE")[0].Trim('\'');
        var currentRels = EntityIds(currentIfc, "IFCRELASSOCIATESMATERIAL")
            .Select(id => EntityArgs(currentIfc, id, "IFCRELASSOCIATESMATERIAL")).ToList();
        var mergedSteelRel = Assert.Single(currentRels, a => a[4].Contains($"#{currentMemberType}"));
        Assert.Equal(
            $"'{IfcGuid.FromMd5($"{projectGuid}IfcRelAssociatesMaterial:{currentMemberTypeGuid}")}'",
            mergedSteelRel[0]);
    }

    [Theory]
    [InlineData("expression")]
    [InlineData("approximate")]
    [InlineData("conflict")]
    [InlineData("nonfinite")]
    public void ColumnType_BlockedLoadBearingDoesNotSuppressItsIndependentIsExternal(string mode)
    {
        var type = TypeElement(500, "Column", "Family");
        type.CategoryId = -2001330;
        type.Parameters.Add(new Parameter { Definition = new Definition { Name = "LoadBearing" },
            StorageType = mode == "expression" ? StorageType.String : mode == "nonfinite" ? StorageType.Double : StorageType.Integer,
            Value = mode == "expression" ? "{Height}" : mode == "nonfinite" ? double.NaN : 1,
            IsApproximate = mode == "approximate" });
        if (mode == "conflict")
            type.Parameters.Add(new Parameter { Definition = new Definition { Name = "LoadBearing" },
                StorageType = StorageType.Integer, Value = 0 });
        string ifc = ExportToText(type, Product(100, -2001330, "Column", 500, 500));
        Assert.Null(TypeProperty(ifc, "IFCCOLUMNTYPE", "Pset_ColumnCommon", "LoadBearing"));
        Assert.Equal("IFCBOOLEAN(.F.)", TypeProperty(ifc, "IFCCOLUMNTYPE", "Pset_ColumnCommon", "IsExternal"));
    }

    [Fact]
    public void ColumnType_BlockedIsExternalDoesNotSuppressItsIndependentLoadBearing()
    {
        var type = TypeElement(500, "Column", "Family");
        type.CategoryId = -2001330;
        type.Parameters.Add(new Parameter { Definition = new Definition { Name = "IsExternal" },
            StorageType = StorageType.String, Value = "{Host}" });
        string ifc = ExportToText(type, Product(100, -2001330, "Column", 500, 500));
        Assert.Null(TypeProperty(ifc, "IFCCOLUMNTYPE", "Pset_ColumnCommon", "IsExternal"));
        Assert.Equal("IFCBOOLEAN(.T.)", TypeProperty(ifc, "IFCCOLUMNTYPE", "Pset_ColumnCommon", "LoadBearing"));
    }

    private static string? TypeProperty(string ifc, string typeClass, string psetName, string name)
    {
        var type = EntityArgs(ifc, SoleEntityId(ifc, typeClass), typeClass);
        if (type[5] == "$") return null;
        int pset = int.Parse(type[5].Trim('(', ')', '#'));
        var psetArgs = EntityArgs(ifc, pset, "IFCPROPERTYSET");
        Assert.Equal($"'{psetName}'", psetArgs[2]);
        var properties = psetArgs[4].Trim('(', ')').Split(',')
            .Select(token => EntityArgs(ifc, int.Parse(token.TrimStart('#')), "IFCPROPERTYSINGLEVALUE"))
            .Where(args => args[0] == $"'{name}'").ToList();
        return properties.Count == 0 ? null : Assert.Single(properties)[2];
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArchitecturalColumnType_AuthoredLoadBearingWorksWithoutStructuralDefault(bool authored)
    {
        var type = TypeElement(500, "Architectural column", "Family");
        type.CategoryId = -2000100;
        if (authored)
            type.Parameters.Add(new Parameter { Definition = new Definition { Name = "LoadBearing" },
                StorageType = StorageType.Integer, Value = 0 });
        string ifc = ExportToText(type, Product(100, -2000100, "Column", 500, 500));
        Assert.Equal(authored ? "IFCBOOLEAN(.F.)" : null,
            TypeProperty(ifc, "IFCCOLUMNTYPE", "Pset_ColumnCommon", "LoadBearing"));
    }

    [Theory]
    [InlineData(-2000023, "IFCDOORSTYLE", "Pset_DoorCommon")]
    [InlineData(-2000014, "IFCWINDOWSTYLE", "Pset_WindowCommon")]
    [InlineData(-2001320, "IFCBEAMTYPE", "Pset_BeamCommon")]
    public void RegisteredFamilyType_CommonUsesOwnScopeWithoutOccurrenceOrHostValue(
        int category, string typeClass, string psetName)
    {
        var type = TypeElement(500, "Own type", "Family");
        type.CategoryId = category;
        type.Parameters.Add(new Parameter { Id = -1001006, StorageType = StorageType.Integer, Value = 0 });
        var product = Product(100, category, "Product", 500, 500);
        product.SerializedHostId = 900;
        product.Parameters.Add(new Parameter { Definition = new Definition { Name = "IsExternal" },
            StorageType = StorageType.Integer, Value = 1 });
        var host = TypeElement(900, "Exterior host", null);
        host.CategoryId = RoofCategoryId;
        string ifc = ExportToText(type, product, host);
        Assert.Equal("IFCBOOLEAN(.F.)", TypeProperty(ifc, typeClass, psetName, "IsExternal"));
        Assert.Null(TypeProperty(ifc, typeClass, psetName, "Reference"));
        Assert.Equal(typeClass == "IFCBEAMTYPE" ? "IFCBOOLEAN(.T.)" : null,
            TypeProperty(ifc, typeClass, psetName, "LoadBearing"));
        int pset = int.Parse(EntityArgs(ifc, SoleEntityId(ifc, typeClass), typeClass)[5].Trim('(', ')', '#'));
        Assert.DoesNotContain($"(#{SoleEntityId(ifc, typeClass)}),#{pset}", ifc);
    }

    [Theory]
    [InlineData(-2000023, "IsExternal", 0, false)]
    [InlineData(-2000023, "IsExternal[Type]", 1, true)]
    [InlineData(-2000023, "FUNCTION_PARAM", -1, true)]
    [InlineData(-2000014, "IsExternal", 0, false)]
    [InlineData(-2000014, "IsExternal[Type]", 1, true)]
    [InlineData(-2000014, "FUNCTION_PARAM", -1, true)]
    [InlineData(-2001320, "IsExternal", 0, false)]
    [InlineData(-2001320, "IsExternal[Type]", 1, true)]
    [InlineData(-2001320, "FUNCTION_PARAM", -1, true)]
    public void RegisteredFamilyType_CommonPreservesOwnParameterPrecedence(
        int category, string name, int value, bool expected)
    {
        var type = TypeElement(500, "Own type", "Family"); type.CategoryId = category;
        type.Parameters.Add(new Parameter { Id = name == "FUNCTION_PARAM" ? -1001006 : 123,
            Definition = new Definition { Name = name }, StorageType = StorageType.Integer, Value = value });
        if (name != "FUNCTION_PARAM")
            type.Parameters.Add(new Parameter { Id = -1001006, StorageType = StorageType.String, Value = "{Unknown}" });
        var (typeClass, pset) = FamilyTypeCommon(category);
        string ifc = ExportToText(type, Product(100, category, "Product", 500, 500));
        Assert.Equal(expected ? "IFCBOOLEAN(.T.)" : "IFCBOOLEAN(.F.)",
            TypeProperty(ifc, typeClass, pset, "IsExternal"));
    }

    [Theory]
    [InlineData("expression")] [InlineData("approximate")] [InlineData("conflict")]
    [InlineData("function")] [InlineData("host")] [InlineData("category")]
    [InlineData("duplicate-type")] [InlineData("duplicate-occurrence")]
    [InlineData("block-only")] [InlineData("self")]
    public void RegisteredFamilyType_UnvalidatedScopeDoesNotPublishFalse(string mode)
    {
        foreach (int category in new[] { -2000023, -2000014, -2001320 })
        {
            var type = TypeElement(500, "Own type", "Family"); type.CategoryId = category;
            var product = Product(100, category, "Product", mode == "self" ? 100 : 500, 500);
            if (mode == "category") type.CategoryId = WallCategoryId;
            if (mode == "host") type.SerializedHostId = 900;
            if (mode == "block-only") type.IsBlockOnly = true;
            if (mode is "expression" or "approximate" or "conflict" or "function")
                type.Parameters.Add(new Parameter { Id = mode == "function" ? -1001006 : 123,
                    Definition = new Definition { Name = "IsExternal" },
                    StorageType = mode is "expression" or "function" ? StorageType.String : StorageType.Integer,
                    Value = mode is "expression" or "function" ? "{Unknown}" : 1,
                    IsApproximate = mode == "approximate" });
            if (mode == "function") type.Parameters[^1].Definition = null;
            if (mode == "conflict")
                type.Parameters.Add(new Parameter { Definition = new Definition { Name = "IsExternal" },
                    StorageType = StorageType.Integer, Value = 0 });
            var elements = new List<Element> { type, product };
            if (mode == "duplicate-type") elements.Add(type);
            if (mode == "duplicate-occurrence") elements.Add(product);
            var (typeClass, pset) = FamilyTypeCommon(category);
            string ifc = ExportToText(elements.ToArray());
            if (category == -2001320 && mode is "expression" or "approximate" or "conflict" or "function")
            {
                Assert.Null(TypeProperty(ifc, typeClass, pset, "IsExternal"));
                Assert.Equal("IFCBOOLEAN(.T.)", TypeProperty(ifc, typeClass, pset, "LoadBearing"));
            }
            else if (category != -2001320 && mode != "category")
                // A door or window style always carries IsExternal (the type's cached value; false without a host wall).
                Assert.Equal("IFCBOOLEAN(.F.)", TypeProperty(ifc, typeClass, pset, "IsExternal"));
            else
                Assert.Equal("$", EntityArgs(ifc, SoleEntityId(ifc, typeClass), typeClass)[5]);
        }
    }

    [Theory]
    [InlineData("LoadBearing", 0)] [InlineData("LoadBearing[Type]", 0)]
    [InlineData("LoadBearing", 1)]
    public void BeamType_AuthoredLoadBearingPrecedesStructuralDefault(string name, int value)
    {
        var type = TypeElement(500, "Beam", "Family"); type.CategoryId = -2001320;
        type.Parameters.Add(new Parameter { Id = -1001006, StorageType = StorageType.Integer, Value = 0 });
        type.Parameters.Add(new Parameter { Definition = new Definition { Name = name },
            StorageType = StorageType.Integer, Value = value });
        string ifc = ExportToText(type, Product(100, -2001320, "Beam", 500, 500));
        Assert.Equal(value != 0 ? "IFCBOOLEAN(.T.)" : "IFCBOOLEAN(.F.)",
            TypeProperty(ifc, "IFCBEAMTYPE", "Pset_BeamCommon", "LoadBearing"));
        Assert.Equal("IFCBOOLEAN(.F.)", TypeProperty(ifc, "IFCBEAMTYPE", "Pset_BeamCommon", "IsExternal"));
    }

    [Theory]
    [InlineData("expression")] [InlineData("approximate")] [InlineData("conflict")]
    [InlineData("nonfinite")]
    public void BeamType_BlockedLoadBearingDoesNotSuppressOwnIsExternal(string mode)
    {
        var type = TypeElement(500, "Beam", "Family"); type.CategoryId = -2001320;
        type.Parameters.Add(new Parameter { Id = -1001006, StorageType = StorageType.Integer, Value = 0 });
        type.Parameters.Add(new Parameter { Definition = new Definition { Name = "LoadBearing" },
            StorageType = mode == "expression" ? StorageType.String : mode == "nonfinite" ? StorageType.Double : StorageType.Integer,
            Value = mode == "expression" ? "{Height}" : mode == "nonfinite" ? double.NaN : 1,
            IsApproximate = mode == "approximate" });
        if (mode == "conflict")
            type.Parameters.Add(new Parameter { Definition = new Definition { Name = "LoadBearing" },
                StorageType = StorageType.Integer, Value = 0 });
        string ifc = ExportToText(type, Product(100, -2001320, "Beam", 500, 500));
        Assert.Null(TypeProperty(ifc, "IFCBEAMTYPE", "Pset_BeamCommon", "LoadBearing"));
        Assert.Equal("IFCBOOLEAN(.F.)", TypeProperty(ifc, "IFCBEAMTYPE", "Pset_BeamCommon", "IsExternal"));
    }

    [Fact]
    public void RegisteredFamilyType_SkippedOccurrenceDoesNotRegisterTypeProperties()
    {
        var type = TypeElement(500, "Door", "Family"); type.CategoryId = DoorCategoryId;
        var door = Product(100, DoorCategoryId, "Door", 500, 500);
        door.Geometry = null; door.BoundingBox = null;
        string ifc = ExportToText(type, door);
        Assert.Empty(EntityIds(ifc, "IFCDOORSTYLE"));
        Assert.DoesNotContain("'Pset_DoorCommon'", ifc);
    }

    [Fact]
    public void BeamType_BothBlockedPropertiesDoNotCreateAnEmptySet()
    {
        var type = TypeElement(500, "Beam", "Family"); type.CategoryId = -2001320;
        foreach (string name in new[] { "IsExternal", "LoadBearing" })
            type.Parameters.Add(new Parameter { Definition = new Definition { Name = name },
                StorageType = StorageType.String, Value = "{Unknown}" });
        string ifc = ExportToText(type, Product(100, -2001320, "Beam", 500, 500));
        Assert.Equal("$", EntityArgs(ifc, SoleEntityId(ifc, "IFCBEAMTYPE"), "IFCBEAMTYPE")[5]);
    }

    [Theory]
    [InlineData(-2000023)] [InlineData(-2000014)] [InlineData(-2001320)]
    public void RegisteredFamilyType_MissingPublicFunctionDoesNotConfirmCalculatorDefault(int category)
    {
        var type = TypeElement(500, "Own type", "Family"); type.CategoryId = category;
        var (typeClass, pset) = FamilyTypeCommon(category);
        string ifc = ExportToText(type, Product(100, category, "Product", 500, 500));
        // Beam types stay unvalidated; door and window styles carry the type-cached IsExternal (false without a host wall).
        if (category == -2001320) Assert.Null(TypeProperty(ifc, typeClass, pset, "IsExternal"));
        else Assert.Equal("IFCBOOLEAN(.F.)", TypeProperty(ifc, typeClass, pset, "IsExternal"));
        if (category == -2001320)
            Assert.Equal("IFCBOOLEAN(.T.)", TypeProperty(ifc, typeClass, pset, "LoadBearing"));
    }

    private static (string Type, string Pset) FamilyTypeCommon(int category) => category switch
    {
        -2000023 => ("IFCDOORSTYLE", "Pset_DoorCommon"),
        -2000014 => ("IFCWINDOWSTYLE", "Pset_WindowCommon"),
        _ => ("IFCBEAMTYPE", "Pset_BeamCommon"),
    };

    [Theory]
    [InlineData(-2000023, "IFCDOORSTYLE")]
    [InlineData(-2000014, "IFCWINDOWSTYLE")]
    public void FamilyType_ManufacturerComesFromAllModelManufacturerParameter(int category, string typeClass)
    {
        var type = TypeElement(500, "Own type", "Family");
        type.CategoryId = category;
        type.Parameters.Add(new Parameter { Id = -1010108, StorageType = StorageType.String, Value = "Rehau" });
        string ifc = ExportToText(type, Product(100, category, "Product", 500, null));
        Assert.Equal("IFCLABEL('Rehau')", TypePsetProperty(
            ifc, typeClass, "Pset_ManufacturerTypeInformation", "Manufacturer"));
    }

    [Fact]
    public void FamilyType_EmptyManufacturerCreatesNoSet()
    {
        var type = TypeElement(500, "Own type", "Family");
        type.CategoryId = DoorCategoryId;
        type.Parameters.Add(new Parameter { Id = -1010108, StorageType = StorageType.String, Value = "" });
        string ifc = ExportToText(type, Product(100, DoorCategoryId, "OST_Doors", 500, null));
        Assert.DoesNotContain("Pset_ManufacturerTypeInformation", ifc);
    }

    [Fact]
    public void WindowType_ThermalTransmittanceIsTheStoredHeatTransferCoefficient()
    {
        var type = TypeElement(500, "Own type", "Family");
        type.CategoryId = -2000014;
        type.Parameters.Add(new Parameter { Id = -1005430, StorageType = StorageType.Double, Value = 3.6886 });
        string ifc = ExportToText(type, Product(100, -2000014, "OST_Windows", 500, null));
        Assert.Equal("IFCTHERMALTRANSMITTANCEMEASURE(3.6886000000000001)",
            TypePsetProperty(ifc, "IFCWINDOWSTYLE", "Pset_WindowCommon", "ThermalTransmittance"));
    }

    [Fact]
    public void WindowType_WithoutHeatTransferCoefficientCreatesNoThermalTransmittance()
    {
        var type = TypeElement(500, "Own type", "Family");
        type.CategoryId = -2000014;
        string ifc = ExportToText(type, Product(100, -2000014, "OST_Windows", 500, null));
        Assert.DoesNotContain("ThermalTransmittance", ifc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WindowSymbolSolidWithoutFaceMaterial_AddsTheNullMaterial(bool unstyledSolid)
    {
        var type = TypeElement(500, "Window type", "Window family");
        type.CategoryId = -2000014;
        type.References = [700];
        double[][] square = [[0, 0, 0], [1, 0, 0], [1, 1, 0], [0, 1, 0]];
        type.Geometry = new GeometryData
        {
            Faces = Enumerable.Range(0, 4).Select(_ => new GeoFace
                { OuterVertices = square.ToList(), RenderStyleId = unstyledSolid ? null : 700 }).ToList(),
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3] }],
        };
        var material = new Element { Id = 700, TypeId = -1, IsBlockOnly = true, CategoryId = -2000700,
            Category = "OST_Materials", Name = "Frame material", HasShadingAppearance = true, Parameters = [
                new Parameter { Id = 701, Definition = new Definition { Name = "MATERIAL_PARAM_COLOR" }, Value = 0x112233, StorageType = StorageType.Integer },
                new Parameter { Id = 702, Definition = new Definition { Name = "MATERIAL_PARAM_SHININESS" }, Value = 25, StorageType = StorageType.Integer }] };
        string ifc = ExportToText(type, Product(100, -2000014, "OST_Windows", 500, null), material);
        Assert.Contains("IFCMATERIAL('Frame material')", ifc);
        Assert.Equal(unstyledSolid, ifc.Contains("IFCMATERIAL('<Unnamed>')"));
    }

    [Fact]
    public void SymbolMaterialReferencedWithoutAFaceUsingIt_IsNotPartOfTheProductMaterials()
    {
        var type = TypeElement(500, "Door type", "Door family");
        type.CategoryId = -2000023;
        type.References = [700, 710];
        double[][] square = [[0, 0, 0], [1, 0, 0], [1, 1, 0], [0, 1, 0]];
        type.Geometry = new GeometryData
        {
            Faces = Enumerable.Range(0, 4).Select(_ => new GeoFace
                { OuterVertices = square.ToList(), RenderStyleId = 700 }).ToList(),
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3] }],
        };
        Element Material(int id, string name) => new() { Id = id, TypeId = -1, IsBlockOnly = true, CategoryId = -2000700,
            Category = "OST_Materials", Name = name, HasShadingAppearance = true, Parameters = [
                new Parameter { Id = id + 1, Definition = new Definition { Name = "MATERIAL_PARAM_COLOR" }, Value = 0x112233, StorageType = StorageType.Integer },
                new Parameter { Id = id + 2, Definition = new Definition { Name = "MATERIAL_PARAM_SHININESS" }, Value = 25, StorageType = StorageType.Integer }] };
        string ifc = ExportToText(type, Product(100, -2000023, "OST_Doors", 500, null),
            Material(700, "Frame material"), Material(710, "Glass material"));
        Assert.Contains("IFCMATERIAL('Frame material')", ifc);
        Assert.DoesNotContain("IFCMATERIAL('Glass material')", ifc);
    }

    [Fact]
    public void CoveringType_TotalThicknessIsTheComputedCeilingThickness()
    {
        var type = TypeElement(500, "Ceiling type", "Compound Ceiling");
        type.CategoryId = -2000038;
        type.ValidatedCeilingTypeThicknessFeet = 0.328083989501312;
        var writer = new StepWriter();
        var method = typeof(IfcExporter).GetMethod("BuildTypeCommonPropertySet",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var pool = new Dictionary<(string PropertyName, string ValueExpr), int>();
        var psetId = (int?)method.Invoke(null, [writer, 20, "IFCCOVERINGTYPE", type, pool,
            new Dictionary<int, bool?>(), "500"]);
        Assert.NotNull(psetId);
        string text = string.Join("\n", Enumerable.Range(0, writer.EntityCount)
            .Select(index => writer.EntityAt(index)).Select(entity => $"{entity.Type}({entity.Args})"));
        Assert.Contains("'Pset_CoveringCommon'", text);
        Assert.Matches(@"'TotalThickness',\$,IFCPOSITIVELENGTHMEASURE\(99\.99999999999\d+\),\$", text);
    }

    private static Element TypedTerminal(int id, int typeId, List<(int Index, int Direction, int SystemType)> connectors) => new()
    {
        Id = id,
        SerializedMepConnectors = connectors,
        Parameters = [new Parameter
        {
            Id = -1002050, Definition = new Definition { Name = "ELEM_TYPE_PARAM" },
            StorageType = StorageType.ElementId, Value = typeId,
        }],
    };

    [Fact]
    public void FlowTerminal_ConnectorWithoutStoredDirectionTakesTheKindOfTheSameTypeAndIndex()
    {
        var first = TypedTerminal(100, 500, [(1, 0, 0), (2, 0, 0)]);
        var second = TypedTerminal(101, 500, [(1, 1, 20), (2, 0, 19)]);
        var other = TypedTerminal(102, 501, [(1, 0, 0)]);
        var writer = new StepWriter();
        var method = typeof(IfcExporter).GetMethod("WriteDistributionPorts",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        method.Invoke(null, [writer, 20,
            new Dictionary<int, Element> { [100] = first, [101] = second, [102] = other },
            new Dictionary<int, (int entityId, int placementId, string ifcType)>
            {
                [100] = (7, 8, "IFCFLOWTERMINAL"), [101] = (9, 10, "IFCFLOWTERMINAL"), [102] = (11, 12, "IFCFLOWTERMINAL"),
            },
            false]);
        var ports = Enumerable.Range(0, writer.EntityCount).Select(index => writer.EntityAt(index))
            .Where(entity => entity.Type == "IFCDISTRIBUTIONPORT").ToList();
        // Type 500 index 1 is a SINK (stored by element 101), index 2 SOURCEANDSINK; type 501 has no stored kind.
        Assert.Equal(4, ports.Count);
        Assert.Contains(ports, port => port.Args.Contains("'InPort_100_1'") && port.Args.EndsWith(".SINK."));
        Assert.Contains(ports, port => port.Args.Contains("'Port_100_2'") && port.Args.EndsWith(".SOURCEANDSINK."));
        Assert.Contains(ports, port => port.Args.Contains("'InPort_101_1'"));
        Assert.Contains(ports, port => port.Args.Contains("'Port_101_2'"));
        Assert.DoesNotContain(ports, port => port.Args.Contains("_102_"));
    }

    [Fact]
    public void FlowTerminal_ConnectorsBecomeDistributionPortsWithStoredFlowDirections()
    {
        var terminal = new Element { Id = 100, SerializedMepConnectors = [(1, 1, 19), (2, 2, 16), (3, 2, 22)] };
        var writer = new StepWriter();
        var method = typeof(IfcExporter).GetMethod("WriteDistributionPorts",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        method.Invoke(null, [writer, 20, new Dictionary<int, Element> { [100] = terminal },
            new Dictionary<int, (int entityId, int placementId, string ifcType)> { [100] = (7, 8, "IFCFLOWTERMINAL") }, false]);
        var entities = Enumerable.Range(0, writer.EntityCount).Select(index => writer.EntityAt(index)).ToList();
        var ports = entities.Where(entity => entity.Type == "IFCDISTRIBUTIONPORT").ToList();
        // Connector 3 has direction 2 of another system type: that direction is not carried by the stored calculation.
        Assert.Equal(2, ports.Count);
        Assert.Contains("'InPort_100_1','Flow',$,$,$,.SINK.", ports[0].Args);
        Assert.Contains("'OutPort_100_2','Flow',$,$,$,.SOURCE.", ports[1].Args);
        var relations = entities.Where(entity => entity.Type == "IFCRELCONNECTSPORTTOELEMENT").ToList();
        Assert.Equal(2, relations.Count);
        Assert.Matches(@"'100\|[^']+','Flow',#\d+,#7$", relations[0].Args);
    }

    private static List<(string Type, string Args)> WritePorts(
        Dictionary<int, Element> elements, Dictionary<int, (int entityId, int placementId, string ifcType)> products, bool currentProfile)
    {
        var writer = new StepWriter();
        // The writer reads each host's GlobalId back from its entity.
        foreach (var (id, product) in products.OrderBy(pair => pair.Value.entityId))
            while (writer.EntityCount < product.entityId - 1)
                writer.Add("IFCCARTESIANPOINT", "(0.,0.)");
        foreach (var (id, product) in products.OrderBy(pair => pair.Value.entityId))
            writer.Add(product.ifcType, $"'{IfcGuid.FromName($"host:{id}")}',#1,'host',$,$,$,$,'{id}'");
        var method = typeof(IfcExporter).GetMethod("WriteDistributionPorts",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        method.Invoke(null, [writer, 20, elements, products, currentProfile]);
        return Enumerable.Range(0, writer.EntityCount).Select(index => writer.EntityAt(index))
            .Select(entity => (entity.Type, entity.Args)).ToList();
    }

    private static Element ConnectorHost(int id, params (int Index, int Direction, (int Id, int Index, int Type)[] Refs)[] connectors) =>
        new() { Id = id, SerializedConnectorGraph = connectors.Select(connector =>
            (connector.Index, connector.Direction, (IReadOnlyList<(int Id, int Index, int Type)>)connector.Refs)).ToList() };

    [Fact]
    public void ConnectorGraph_PairsConnectorsOfTheSameIndexAndGivesTheOthersPortsOfTheirOwn()
    {
        // Segment 10 and fitting 12 reference each other through connector index 1 (the segment also references itself,
        // as every connector references the element's other one). Fitting connector 2 points at element 99, which is
        // not exported, so it is as good as unconnected.
        var segment = ConnectorHost(10, (0, 0, [(10, 1, 1)]), (1, 0, [(10, 0, 1), (12, 1, 1)]));
        var fitting = ConnectorHost(12, (1, 0, [(10, 1, 1)]), (2, 0, [(99, 0, 1)]));
        var entities = WritePorts(
            new Dictionary<int, Element> { [10] = segment, [12] = fitting },
            new Dictionary<int, (int entityId, int placementId, string ifcType)>
            {
                [10] = (3, 4, "IFCFLOWSEGMENT"), [12] = (5, 6, "IFCFLOWFITTING"),
            }, true);
        var ports = entities.Where(entity => entity.Type == "IFCDISTRIBUTIONPORT").Select(entity => entity.Args).ToList();
        Assert.Equal(4, ports.Count);
        Assert.Contains(ports, port => port.Contains("'Port_10_0','Flow','',$,$,.SOURCEANDSINK."));
        Assert.Contains(ports, port => port.Contains("'Port_10_1','Flow','',$,$,.SOURCEANDSINK."));
        Assert.Contains(ports, port => port.Contains("'Port_12_1','Flow','',$,$,.SOURCEANDSINK."));
        Assert.Contains(ports, port => port.Contains("'Port_12_2','Flow','',$,$,.SOURCEANDSINK."));
        var connection = Assert.Single(entities, entity => entity.Type == "IFCRELCONNECTSPORTS");
        Assert.EndsWith(",'Flow',#" + (entities.FindIndex(e => e.Args.Contains("'Port_10_1'")) + 1)
            + ",#" + (entities.FindIndex(e => e.Args.Contains("'Port_12_1'")) + 1) + ",$", connection.Args);
        Assert.Equal(4, entities.Count(entity => entity.Type == "IFCRELCONNECTSPORTTOELEMENT"));
    }

    [Fact]
    public void ConnectorGraph_TakesAConnectionFromTheOtherSideWhenOneSideHoldsAStaleReference()
    {
        // The segment's connector points at a ghost element 77; the fitting points at the segment.
        var segment = ConnectorHost(10, (0, 0, [(10, 1, 1)]), (1, 0, [(10, 0, 1), (77, 1, 1)]));
        var fitting = ConnectorHost(12, (1, 0, [(10, 1, 1)]));
        var entities = WritePorts(
            new Dictionary<int, Element> { [10] = segment, [12] = fitting },
            new Dictionary<int, (int entityId, int placementId, string ifcType)>
            {
                [10] = (3, 4, "IFCFLOWSEGMENT"), [12] = (5, 6, "IFCFLOWFITTING"),
            }, true);
        // The segment is visited first, so it is the in port.
        var connection = Assert.Single(entities, entity => entity.Type == "IFCRELCONNECTSPORTS");
        Assert.EndsWith(",'Flow',#" + (entities.FindIndex(e => e.Args.Contains("'Port_10_1'")) + 1)
            + ",#" + (entities.FindIndex(e => e.Args.Contains("'Port_12_1'")) + 1) + ",$", connection.Args);
    }

    [Fact]
    public void ConnectorGraph_IsNotUsedForLegacy()
    {
        var segment = ConnectorHost(10, (0, 0, [(10, 1, 1)]), (1, 0, [(10, 0, 1)]));
        var entities = WritePorts(
            new Dictionary<int, Element> { [10] = segment },
            new Dictionary<int, (int entityId, int placementId, string ifcType)> { [10] = (3, 4, "IFCFLOWSEGMENT") }, false);
        Assert.DoesNotContain(entities, entity => entity.Type == "IFCDISTRIBUTIONPORT");
    }

    [Fact]
    public void ConnectorGraph_GivesFamilyInstanceHostsPortsWithTheStoredFlowDirections()
    {
        // Stored direction 1 is a source (OutPort_), 2 a sink (InPort_), none or electrical a bidirectional Port_.
        var terminal = ConnectorHost(20, (1, 1, [(20, 2, 1)]), (2, 2, [(20, 1, 1)]), (3, 0, [(20, 1, 1)]), (4, -1, [(20, 1, 1)]));
        var entities = WritePorts(
            new Dictionary<int, Element> { [20] = terminal },
            new Dictionary<int, (int entityId, int placementId, string ifcType)> { [20] = (3, 4, "IFCFLOWTERMINAL") }, true);
        var ports = entities.Where(entity => entity.Type == "IFCDISTRIBUTIONPORT").Select(entity => entity.Args).ToList();
        Assert.Equal(4, ports.Count);
        Assert.Contains(ports, port => port.Contains("'OutPort_20_1','Flow','',$,$,.SOURCE."));
        Assert.Contains(ports, port => port.Contains("'InPort_20_2','Flow','',$,$,.SINK."));
        Assert.Contains(ports, port => port.Contains("'Port_20_3','Flow','',$,$,.SOURCEANDSINK."));
        Assert.Contains(ports, port => port.Contains("'Port_20_4','Flow','',$,$,.SOURCEANDSINK."));
        Assert.Equal(4, entities.Count(entity => entity.Type == "IFCRELCONNECTSPORTTOELEMENT"));
    }

    [Fact]
    public void ConnectorGraph_FurnishingElementsGetNoPorts()
    {
        var furniture = ConnectorHost(20, (1, 1, [(20, 2, 1)]));
        var entities = WritePorts(
            new Dictionary<int, Element> { [20] = furniture },
            new Dictionary<int, (int entityId, int placementId, string ifcType)> { [20] = (3, 4, "IFCFURNISHINGELEMENT") }, true);
        Assert.DoesNotContain(entities, entity => entity.Type == "IFCDISTRIBUTIONPORT");
    }

    [Fact]
    public void ConnectorGraph_ConnectsAFamilyInstanceToASegment()
    {
        // The terminal's connector 1 has the stored direction 2 and references the segment's connector 1, which
        // references it back: the terminal is visited first, so it gets the out port and the segment the in port.
        var terminal = ConnectorHost(10, (1, 2, [(10, 2, 1), (20, 1, 1)]));
        var segment = ConnectorHost(20, (1, 0, [(10, 1, 1)]));
        var entities = WritePorts(
            new Dictionary<int, Element> { [10] = terminal, [20] = segment },
            new Dictionary<int, (int entityId, int placementId, string ifcType)>
            {
                [10] = (3, 4, "IFCFLOWTERMINAL"), [20] = (5, 6, "IFCFLOWSEGMENT"),
            }, true);
        var ports = entities.Where(entity => entity.Type == "IFCDISTRIBUTIONPORT").Select(entity => entity.Args).ToList();
        Assert.Equal(2, ports.Count);
        Assert.Contains(ports, port => port.Contains("'OutPort_10_1','Flow','',$,$,.SOURCE."));
        Assert.Contains(ports, port => port.Contains("'InPort_20_1','Flow','',$,$,.SINK."));
        Assert.Single(entities, entity => entity.Type == "IFCRELCONNECTSPORTS");
    }

    [Fact]
    public void ApplyConnectorGraph_TakesTheDirectionFromTheCalculationOfTheElementKind()
    {
        var terminal = new Element { Id = 100, SerializedMepConnectors = [(1, 2, 19), (2, 1, 20)] };
        var pipe = new Element { Id = 101 };
        var plain = new Element { Id = 102 };
        var info = new RevitFileInfo();
        info.Elements.AddRange([terminal, pipe, plain]);
        var state = new RvtIO.Internal.ParsingState();
        void Connector(RvtIO.Parsing.TfBlockDeserializer.NativeParams native, int k, int index)
        {
            native.Fields[$"771#{k}:m_nIndex"] = index;
            native.Fields[$"771#{k}:m_arrRefs#"] = 0;
        }
        var terminalParams = new RvtIO.Parsing.TfBlockDeserializer.NativeParams();
        Connector(terminalParams, 0, 1);
        Connector(terminalParams, 1, 2);
        var pipeParams = new RvtIO.Parsing.TfBlockDeserializer.NativeParams();
        Connector(pipeParams, 0, 0);
        pipeParams.Fields["3556#0:m_eDirection"] = 1;
        var plainParams = new RvtIO.Parsing.TfBlockDeserializer.NativeParams();
        Connector(plainParams, 0, 0);
        state.NativeParams[100] = terminalParams;
        state.NativeParams[101] = pipeParams;
        state.NativeParams[102] = plainParams;

        RvtIO.Parsing.ElementBuilder.ApplyConnectorGraph(info, state);

        Assert.Equal([2, 1], terminal.SerializedConnectorGraph!.Select(connector => connector.Direction));
        Assert.Equal([1], pipe.SerializedConnectorGraph!.Select(connector => connector.Direction));
        Assert.Equal([0], plain.SerializedConnectorGraph!.Select(connector => connector.Direction));
    }

    [Fact]
    public void ModelGroup_AssignsItsExportedMembersAndSkipsGroupsWithoutAny()
    {
        var group = new Element { Id = 50, CategoryId = -2000095, Name = "Lift", SerializedGroupFamilyLabel = "Model group" };
        var emptyGroup = new Element { Id = 51, CategoryId = -2000095, Name = "Empty", SerializedGroupFamilyLabel = "Model group" };
        var member = new Element { Id = 100, SerializedGroupId = 50 };
        var unexported = new Element { Id = 101, SerializedGroupId = 51 };
        var elements = new Dictionary<int, Element> { [50] = group, [51] = emptyGroup, [100] = member, [101] = unexported };
        var writer = new StepWriter();
        typeof(IfcExporter).GetMethod("WriteModelGroups", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(new IfcExporter() { Profile = IfcProfile.Legacy }, [writer, 20, elements,
                new Dictionary<int, (int entityId, int placementId, string ifcType)> { [100] = (7, 8, "IFCBUILDINGELEMENTPROXY") }]);
        var entities = Enumerable.Range(0, writer.EntityCount).Select(index => writer.EntityAt(index)).ToList();
        var written = Assert.Single(entities, entity => entity.Type == "IFCGROUP");
        Assert.EndsWith(",#20,'Model group:Lift:50',$,'Model group:Lift'", written.Args[written.Args.IndexOf(",#20,", StringComparison.Ordinal)..]);
        var relation = Assert.Single(entities, entity => entity.Type == "IFCRELASSIGNSTOGROUP");
        Assert.EndsWith($",#20,$,$,(#7),$,#{written.Id}", relation.Args[relation.Args.IndexOf(",#20,", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void StairFlight_PropertiesComeFromTheRunNumbersAndItsStairs()
    {
        var numbers = new RvtIO.Parsing.TfBlockDeserializer.StairRunNumbers(0, 3.0, 3.0, 0, 6, 1, 1);
        var input = new RvtIO.Parsing.TfBlockDeserializer.StairRunInput(200, 100, RvtIO.Parsing.TfBlockDeserializer.StairRunStyle.Straight, [new(1, 0, 0)], numbers);
        var stairs = new Element { Id = 100, SerializedActualTreadDepthFeet = 1.0 };
        var runType = new Element { Id = 300, CategoryId = -2000919, SerializedStairRunTypeNosingFeet = 0 };
        var run = new Element { Id = 200, CategoryId = -2000919, TypeId = 300, SerializedStairRunInput = input };
        var elements = new Dictionary<int, Element> { [100] = stairs, [200] = run, [300] = runType };
        var writer = new StepWriter();
        var values = new List<int>();
        typeof(IfcExporter).GetMethod("AddStairFlightProperties", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [writer, run, elements, values, false, new Dictionary<int, List<Element>> { [stairs.Id] = [run] }]);
        var written = values.Select(id => Enumerable.Range(0, writer.EntityCount).Select(writer.EntityAt).Single(e => e.Id == id).Args).ToList();
        Assert.Equal(
        [
            "'NumberOfRiser',$,IFCCOUNTMEASURE(6.),$",
            "'NumberOfTreads',$,IFCCOUNTMEASURE(5.),$",
            "'RiserHeight',$,IFCPOSITIVELENGTHMEASURE(" + (3.0 / 6 * 304.8).ToString("G17", System.Globalization.CultureInfo.InvariantCulture) + "),$",
            "'TreadLength',$,IFCPOSITIVELENGTHMEASURE(" + (1.0 * 304.8).ToString("G17", System.Globalization.CultureInfo.InvariantCulture) + "),$",
            "'NosingLength',$,IFCLENGTHMEASURE(0.),$",
            "'WalkingLineOffset',$,IFCPOSITIVELENGTHMEASURE(1.5),$",
            "'TreadLengthAtOffset',$,IFCPOSITIVELENGTHMEASURE(" + (1.0 * 304.8).ToString("G17", System.Globalization.CultureInfo.InvariantCulture) + "),$",
        ], written);
    }

    /// <summary>Like <see cref="TypeProperty"/> for a type holding several sets: the value of the property in the named set.</summary>
    private static string? TypePsetProperty(string ifc, string typeClass, string psetName, string name)
    {
        var type = EntityArgs(ifc, SoleEntityId(ifc, typeClass), typeClass);
        if (type[5] == "$") return null;
        foreach (string token in type[5].Trim('(', ')').Split(','))
        {
            var psetArgs = EntityArgs(ifc, int.Parse(token.TrimStart('#')), "IFCPROPERTYSET");
            if (psetArgs[2] != $"'{psetName}'") continue;
            var properties = psetArgs[4].Trim('(', ')').Split(',')
                .Select(item => EntityArgs(ifc, int.Parse(item.TrimStart('#')), "IFCPROPERTYSINGLEVALUE"))
                .Where(args => args[0] == $"'{name}'").ToList();
            return properties.Count == 0 ? null : Assert.Single(properties)[2];
        }
        return null;
    }


    [Fact]
    public void TypeName_UsesOwnValidatedNameWithoutMutatingGeneralNames()
    {
        Element type = TypeElement(500, "Nested symbol", "Wrong family");
        type.ValidatedIfcTypeObjectName = "Own family:Own type";
        string ifc = ExportToText(type, Railing(100, 500, 500));
        Assert.Equal("'Own family:Own type'", EntityArgs(ifc, SoleEntityId(ifc, "IFCRAILINGTYPE"), "IFCRAILINGTYPE")[2]);
        Assert.Equal("Nested symbol", type.Name);
        Assert.Equal("Wrong family", type.FamilyName);
    }

    private static Element TypeElement(int id, string? name, string? familyName) => new()
    {
        Id = id,
        Name = name,
        FamilyName = familyName,
    };

    [Theory]
    [InlineData(-2000171, "IFCMEMBERTYPE", "Pset_MemberCommon")]
    [InlineData(-2000170, "IFCPLATETYPE", "Pset_PlateCommon")]
    [InlineData(-2001330, "IFCCOLUMNTYPE", "Pset_ColumnCommon")]
    public void ComponentType_CommonIsExternalUsesItsOwnTypeScope(
        int category, string typeClass, string psetName)
    {
        var type = TypeElement(500, "Own type", "Own family");
        type.CategoryId = category;
        var component = Product(100, category, "Component", 500, 500);
        component.Parameters.Add(new Parameter { Definition = new Definition { Name = "IsExternal" },
            StorageType = StorageType.Integer, Value = 1 });
        string ifc = ExportToText(type, component);
        int typeEntity = SoleEntityId(ifc, typeClass);
        int pset = int.Parse(EntityArgs(ifc, typeEntity, typeClass)[5].Trim('(', ')', '#'));
        var psetArgs = EntityArgs(ifc, pset, "IFCPROPERTYSET");
        Assert.Equal($"'{psetName}'", psetArgs[2]);
        Assert.Equal("IFCBOOLEAN(.F.)", TypeProperty(ifc, typeClass, psetName, "IsExternal"));
        // This set is attached inline, not as an occurrence relationship to the type.
        Assert.DoesNotContain($"(#{typeEntity}),#{pset}", ifc);
    }

    [Theory]
    [InlineData("named", 1, true)]
    [InlineData("named", 0, false)]
    [InlineData("function", 1, true)]
    [InlineData("function", 0, false)]
    [InlineData("function", -1, true)]
    public void ComponentType_ExactOwnParameterPrecedesCategoryDefault(
        string source, int value, bool expected)
    {
        var type = TypeElement(500, "Type", "Family");
        type.CategoryId = -2000171;
        type.Parameters.Add(new Parameter {
            Id = source == "function" ? -1001006 : 123,
            Definition = new Definition { Name = source == "function" ? "FUNCTION_PARAM" : "IsExternal" },
            StorageType = StorageType.Integer, Value = value });
        var component = Product(100, -2000171, "Mullion", 500, 500);
        string ifc = ExportToText(type, component);
        int pset = int.Parse(EntityArgs(ifc, SoleEntityId(ifc, "IFCMEMBERTYPE"), "IFCMEMBERTYPE")[5].Trim('(', ')', '#'));
        int property = int.Parse(EntityArgs(ifc, pset, "IFCPROPERTYSET")[4].Trim('(', ')', '#'));
        Assert.Equal(expected ? "IFCBOOLEAN(.T.)" : "IFCBOOLEAN(.F.)",
            EntityArgs(ifc, property, "IFCPROPERTYSINGLEVALUE")[2]);
    }

    [Theory]
    [InlineData("expression")] [InlineData("approximate")] [InlineData("conflict")]
    [InlineData("bad-function")] [InlineData("host-on-type")] [InlineData("wrong-category")]
    [InlineData("stringer-placeholder")]
    public void ComponentType_UnvalidatedOwnScopeDoesNotPublishFalse(
        string mode)
    {
        var type = TypeElement(500, "Type", "Family");
        type.CategoryId = mode == "wrong-category" ? -2000011
            : mode == "stringer-placeholder" ? -2000123 : -2000171;
        if (mode is "expression" or "approximate" or "conflict")
            type.Parameters.Add(new Parameter { Definition = new Definition { Name = "IsExternal" },
                StorageType = mode == "expression" ? StorageType.String : StorageType.Integer,
                Value = mode == "expression" ? "{Host}" : 1, IsApproximate = mode == "approximate" });
        if (mode == "conflict")
            type.Parameters.Add(new Parameter { Definition = new Definition { Name = "IsExternal" },
                StorageType = StorageType.Integer, Value = 0 });
        if (mode == "bad-function")
            type.Parameters.Add(new Parameter { Id = -1001006, StorageType = StorageType.String, Value = "?" });
        if (mode == "host-on-type") type.SerializedHostId = 900;
        var component = Product(100, mode == "stringer-placeholder" ? -2000123 : -2000171,
            "Component", 500, 500);
        string ifc = ExportToText(type, component);
        Assert.Equal("$", EntityArgs(ifc, SoleEntityId(ifc, "IFCMEMBERTYPE"), "IFCMEMBERTYPE")[5]);
    }

    [Fact]
    public void ComponentType_GeometryQualifiedGroupsHaveDistinctPsetGuidsAndPooledValues()
    {
        var type = TypeElement(500, "Type", "Family");
        type.CategoryId = -2000171;
        var first = Product(100, -2000171, "Mullion", 500, 500);
        var second = Product(200, -2000171, "Mullion", 500, 500, 10);
        first.Geometry!.ExactMappedInstances = [new GeoInstance { SymbolId = 601 }];
        second.Geometry!.ExactMappedInstances = [new GeoInstance { SymbolId = 602 }];
        string ifc = ExportToText(type, first, second,
            new Element { Id = 601 }, new Element { Id = 602 });
        var typeIds = EntityIds(ifc, "IFCMEMBERTYPE");
        Assert.Equal(2, typeIds.Count);
        var psets = typeIds.Select(id => int.Parse(
            EntityArgs(ifc, id, "IFCMEMBERTYPE")[5].Trim('(', ')', '#')))
            .Select(id => EntityArgs(ifc, id, "IFCPROPERTYSET")).ToList();
        Assert.Equal(2, psets.Select(args => args[0]).Distinct().Count());
        Assert.Equal(psets[0][4], psets[1][4]);
        int property = int.Parse(psets[0][4].Trim('(', ')', '#'));
        var propertyArgs = EntityArgs(ifc, property, "IFCPROPERTYSINGLEVALUE");
        Assert.Equal("'IsExternal'", propertyArgs[0]);
        Assert.Equal("IFCBOOLEAN(.F.)", propertyArgs[2]);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void ComponentType_NamedValueDoesNotReadLowerPriorityBlockedFunction(int value)
    {
        var type = TypeElement(500, "Type", "Family");
        type.CategoryId = -2000171;
        type.Parameters.Add(new Parameter { Definition = new Definition { Name = "IsExternal" },
            StorageType = StorageType.Integer, Value = value });
        type.Parameters.Add(new Parameter { Id = -1001006, StorageType = StorageType.String, Value = "?" });
        string ifc = ExportToText(type, Product(100, -2000171, "Mullion", 500, 500));
        int pset = int.Parse(EntityArgs(ifc, SoleEntityId(ifc, "IFCMEMBERTYPE"), "IFCMEMBERTYPE")[5].Trim('(', ')', '#'));
        int property = int.Parse(EntityArgs(ifc, pset, "IFCPROPERTYSET")[4].Trim('(', ')', '#'));
        Assert.Equal(value == 0 ? "IFCBOOLEAN(.F.)" : "IFCBOOLEAN(.T.)",
            EntityArgs(ifc, property, "IFCPROPERTYSINGLEVALUE")[2]);
    }

    [Theory]
    [InlineData(false, -2000170, "IFCPLATE")] [InlineData(true, -2000170, "IFCPLATE")]
    [InlineData(false, -2000014, "IFCWINDOW")] [InlineData(true, -2000023, "IFCDOOR")]
    [InlineData(false, -2000011, "IFCWALL")]
    public void GeometrylessCurtainParent_RequiresAnActuallyEmittedOwnedChild(bool childFirst, int childCategory, string childIfcClass)
    {
        var type = TypeElement(500, "Curtain type", "Curtain family");
        type.CategoryId = WallCategoryId; type.SerializedCurtainWallFunction = 1;
        var parent = Product(100, WallCategoryId, "OST_Walls", 500, null);
        parent.TypeId = 500; parent.Geometry = null; parent.BoundingBox = null;
        var child = Product(101, childCategory, childCategory == -2000014 ? "OST_Windows" : childCategory == -2000023 ? "OST_Doors" : "OST_CurtainWallPanels", null, null);
        child.SerializedCurtainHostId = 100;
        string ifc = childFirst ? ExportToText(type, child, parent) : ExportToText(type, parent, child);
        int parentId = SoleEntityId(ifc, "IFCCURTAINWALL");
        int childId = SoleEntityId(ifc, childIfcClass);
        Assert.Equal("$", EntityArgs(ifc, parentId, "IFCCURTAINWALL")[6]);
        var rel = Assert.Single(EntityIds(ifc, "IFCRELAGGREGATES")
            .Select(id => EntityArgs(ifc, id, "IFCRELAGGREGATES")),
            args => args[4] == $"#{parentId}");
        Assert.Equal($"(#{childId})", rel[5]);
        var containments = EntityIds(ifc, "IFCRELCONTAINEDINSPATIALSTRUCTURE")
            .Select(id => EntityArgs(ifc, id, "IFCRELCONTAINEDINSPATIALSTRUCTURE")[4]).ToArray();
        Assert.Contains(containments, list => list.Contains($"#{parentId}"));
        Assert.DoesNotContain(containments, list => Regex.IsMatch(list, $@"#{childId}(?!\d)"));
        Assert.NotEmpty(EntityIds(ifc, "IFCCURTAINWALLTYPE"));
        Assert.True(CurtainOccurrencePsetId(ifc, parentId) > 0);
        Assert.Null(parent.Geometry); Assert.Null(parent.BoundingBox);
        Assert.DoesNotContain($"'{IfcGuid.FromName("qto-pset:100")}'", ifc);
    }

    [Fact]
    public void Current_RoofContainerOfACurtainGrid_HasNoMaterialAssociation()
    {
        // A roof with a curtain grid is a container without material information: curtain roofs carry no material association.
        var parent = Product(100, -2000035, "OST_Roofs", null, null);
        parent.TypeId = 500; parent.Geometry = null; parent.BoundingBox = null;
        // an unset material parameter would otherwise give the roof the null material
        parent.Parameters.Add(new Parameter { Id = -1005500, StorageType = StorageType.ElementId, Value = -1,
            Definition = new Definition { Id = -1005500, Name = "STRUCTURAL_MATERIAL_PARAM" } });
        var child = Product(101, -2000170, "OST_CurtainWallPanels", null, null);
        child.SerializedCurtainHostId = 100;
        var info = new RevitFileInfo();
        info.Elements.AddRange([TypeElement(500, "Sloped Glazing", "Basic Roof"), parent, child]);
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-roof-container-{Guid.NewGuid():N}.ifc");
        string ifc;
        try
        {
            new IfcExporter().Export(info, path);
            ifc = File.ReadAllText(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }

        int roofId = SoleEntityId(ifc, "IFCROOF");
        var associated = EntityIds(ifc, "IFCRELASSOCIATESMATERIAL")
            .Select(id => EntityArgs(ifc, id, "IFCRELASSOCIATESMATERIAL")[4]).ToList();
        Assert.DoesNotContain(associated, list => Regex.IsMatch(list, $@"#{roofId}(?!\d)"));
    }

    [Fact]
    public void Current_RoofContainerOfACurtainGrid_IsTheSameRoofWithTheHeaderOutlineOfAFile()
    {
        // A file read gives the container roof the outline its header stores (the box the application reports) though the roof has no
        // geometry of its own: the outline is no sign of a body, and the roof is still the container of its panels.
        string Export(bool withOutline)
        {
            var parent = Product(100, -2000035, "OST_Roofs", null, null);
            parent.TypeId = 500; parent.Geometry = null; parent.BoundingBox = null;
            if (withOutline)
            {
                parent.BoundingBox = [0, 0, 10, 5, 5, 12];
                parent.BoundingBoxIsHeaderOutline = true;
            }
            parent.Parameters.Add(new Parameter { Id = -1005500, StorageType = StorageType.ElementId, Value = -1,
                Definition = new Definition { Id = -1005500, Name = "STRUCTURAL_MATERIAL_PARAM" } });
            var child = Product(101, -2000170, "OST_CurtainWallPanels", null, null);
            child.SerializedCurtainHostId = 100;
            var info = new RevitFileInfo();
            info.Elements.AddRange([TypeElement(500, "Sloped Glazing", "Basic Roof"), parent, child]);
            string path = Path.Combine(Path.GetTempPath(), $"revitreader-roof-outline-{Guid.NewGuid():N}.ifc");
            try
            {
                new IfcExporter().Export(info, path);
                string ifc = File.ReadAllText(path);
                return ifc[ifc.IndexOf("DATA;", StringComparison.Ordinal)..];
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        string without = Export(withOutline: false);
        string with = Export(withOutline: true);

        Assert.Single(Regex.Matches(with, @"=IFCROOF\("));
        Assert.Equal(without, with);
    }

    [Theory]
    [InlineData(IfcProfile.Legacy, "IFCWALL", 1)]
    [InlineData(IfcProfile.Current, "IFCCURTAINWALL", 0)]
    public void BasicWallBoundToACurtainWallAsAPanel_ExportsAndAggregatesUnderCurtainWall(
        IfcProfile version, string childIfcType, int wallCount)
    {
        // Reference exports write basic-wall curtain panels as IfcCurtainWall aggregated under their
        // parent IfcCurtainWall (Legacy emits them as IfcWall).
        var type = TypeElement(500, "Curtain type", "Curtain family");
        type.CategoryId = WallCategoryId; type.SerializedCurtainWallFunction = 1;
        var parent = Product(100, WallCategoryId, "OST_Walls", 500, null);
        parent.TypeId = 500; parent.Geometry = null; parent.BoundingBox = null;
        var panel = Product(101, -2000170, "OST_CurtainWallPanels", null, null);
        panel.SerializedCurtainHostId = 100;
        var wallType = TypeElement(501, "Panel wall", "Basic Wall");
        wallType.CategoryId = WallCategoryId; wallType.SerializedBasicWallTypeFunction = 1;
        wallType.SerializedBasicWallTypeName = "Panel wall";
        var wallPanel = Product(102, WallCategoryId, "OST_Walls", 501, null);
        wallPanel.SerializedCurtainHostId = 100;
        string ifc = ExportToText(version, type, wallType, parent, panel, wallPanel);
        int parentId = SoleEntityIdForTag(ifc, "IFCCURTAINWALL", "100");
        int plateId = SoleEntityId(ifc, "IFCPLATE");
        int wallChildId = SoleEntityIdForTag(ifc, childIfcType, "102");
        var rel = Assert.Single(EntityIds(ifc, "IFCRELAGGREGATES")
            .Select(id => EntityArgs(ifc, id, "IFCRELAGGREGATES")),
            args => args[4] == $"#{parentId}");
        var expectedChildren = new[] { plateId, wallChildId }.OrderBy(id => id).Select(id => $"#{id}");
        Assert.Equal($"({string.Join(",", expectedChildren)})", rel[5]);
        Assert.Equal(wallCount, EntityIds(ifc, "IFCWALL").Count + EntityIds(ifc, "IFCWALLSTANDARDCASE").Count);
        if (version == IfcProfile.Current)
        {
            Assert.Contains(EntityIds(ifc, "IFCCURTAINWALLTYPE"), id => EntityArgs(ifc, id, "IFCCURTAINWALLTYPE")[7] == "'501'");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WallTypeSharedWithACurtainPanelWall_KeepsItsOwnGlobalId(bool withRegularWall)
    {
        var exportId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var curtainType = TypeElement(500, "Curtain type", "Curtain family");
        curtainType.CategoryId = WallCategoryId; curtainType.SerializedCurtainWallFunction = 1;
        var parent = Product(100, WallCategoryId, "OST_Walls", 500, null);
        parent.TypeId = 500; parent.Geometry = null; parent.BoundingBox = null;
        var panel = Product(101, -2000170, "OST_CurtainWallPanels", null, null);
        panel.SerializedCurtainHostId = 100;
        var wallType = TypeElement(501, "Shared wall", "Basic Wall");
        wallType.CategoryId = WallCategoryId; wallType.SerializedBasicWallTypeFunction = 1;
        wallType.SerializedBasicWallTypeName = "Shared wall";
        wallType.SerializedSystemTypeClassId = 512; wallType.SerializedExportId = exportId;
        var wallPanel = Product(102, WallCategoryId, "OST_Walls", 501, null, centreX: 10);
        wallPanel.SerializedCurtainHostId = 100;
        wallPanel.TypeId = 501; wallPanel.SerializedSystemTypeId = 501;
        var regularWall = Product(103, WallCategoryId, "OST_Walls", 501, null, centreX: 20);
        regularWall.TypeId = 501; regularWall.SerializedSystemTypeId = 501;
        Element[] elements = [curtainType, wallType, parent, panel, wallPanel];

        string ifc = ExportToText(IfcProfile.Current, withRegularWall ? [.. elements, regularWall] : elements);

        string expected = $"'{RvtIO.Ifc.IfcGuid.ToIfcGuid(exportId)}'";
        var panelType = Assert.Single(EntityIds(ifc, "IFCCURTAINWALLTYPE")
            .Select(id => EntityArgs(ifc, id, "IFCCURTAINWALLTYPE")), args => args[7] == "'501'");
        if (!withRegularWall)
        {
            Assert.Equal(expected, panelType[0]);
            return;
        }
        var basicType = Assert.Single(EntityIds(ifc, "IFCWALLTYPE")
            .Select(id => EntityArgs(ifc, id, "IFCWALLTYPE")), args => args[7] == "'501'");
        Assert.Equal(expected, basicType[0]);
        Assert.NotEqual(expected, panelType[0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CurtainPanelWall_KeepsItsLayerGroupApartFromHostsWithTheSameLayers(bool panelFirst)
    {
        var curtainType = TypeElement(500, "Curtain type", "Curtain family");
        curtainType.CategoryId = WallCategoryId; curtainType.SerializedCurtainWallFunction = 1;
        var parent = Product(100, WallCategoryId, "OST_Walls", 500, null);
        parent.TypeId = 500; parent.Geometry = null; parent.BoundingBox = null;
        var panel = Product(101, -2000170, "OST_CurtainWallPanels", null, null);
        panel.SerializedCurtainHostId = 100;
        var wallPanel = Product(102, WallCategoryId, "OST_Walls", null, null, centreX: 10);
        wallPanel.SerializedCurtainHostId = 100;
        var floor = Product(103, -2000032, "OST_Floors", null, null, centreX: 20);
        static List<MaterialLayer> Layers() =>
            [new MaterialLayer { LayerIndex = 0, MaterialId = 700, MaterialName = "Concrete", ThicknessMm = 200 }];
        wallPanel.Layers = Layers(); floor.Layers = Layers();
        Element[] hosts = panelFirst ? [wallPanel, floor] : [floor, wallPanel];

        string ifc = ExportToText(IfcProfile.Current, [curtainType, parent, panel, .. hosts]);

        Assert.Equal(".AXIS2.", LayerUsageDirection(ifc, SoleEntityIdForTag(ifc, "IFCCURTAINWALL", "102")));
        Assert.Equal(".AXIS3.", LayerUsageDirection(ifc, SoleEntityIdForTag(ifc, "IFCSLAB", "103")));
        // Nor may a panel join the walls: the first member of a group names and orients it.
        var none = new Dictionary<int, Element>();
        var counts = new Dictionary<int, int>();
        Assert.NotEqual(IfcExporter.MaterialLayerSetGroupKey(wallPanel, "IFCWALL", none, counts),
            IfcExporter.MaterialLayerSetGroupKey(wallPanel, "IFCCURTAINWALL", none, counts));
    }

    [Theory]
    // In a curtain wall itself the area is height times width: 7 ft x 3 ft.
    [InlineData(100, 21.0)]
    // A wall standing as a panel is still a wall to its door: half of the 40 ft2 surface of the door.
    [InlineData(102, 20.0)]
    public void DoorArea_FollowsWhetherTheHostIsACurtainWallOrAPanelWall(int hostId, double squareFeet)
    {
        var curtainType = TypeElement(500, "Curtain type", "Curtain family");
        curtainType.CategoryId = WallCategoryId; curtainType.SerializedCurtainWallFunction = 1;
        var parent = Product(100, WallCategoryId, "OST_Walls", 500, null);
        parent.TypeId = 500; parent.Geometry = null; parent.BoundingBox = null;
        var panel = Product(101, -2000170, "OST_CurtainWallPanels", null, null);
        panel.SerializedCurtainHostId = 100;
        var wallPanel = Product(102, WallCategoryId, "OST_Walls", null, null, centreX: 10);
        wallPanel.SerializedCurtainHostId = 100;
        var door = Product(104, -2000023, "OST_Doors", null, null, centreX: 20);
        door.SerializedHostId = hostId;
        door.CoarseSurfaceArea = 40;
        foreach (var (id, feet) in new[] { (-1001300, 7.0), (-1001301, 3.0) })
            door.Parameters.Add(new Parameter
            {
                Id = id, StorageType = StorageType.Double, Value = feet,
                Definition = new Definition { Id = id, Name = id == -1001300 ? "Height" : "Width" },
            });

        string ifc = ExportToText(IfcProfile.Current, curtainType, parent, panel, wallPanel, door);

        int doorId = SoleEntityId(ifc, "IFCDOOR");
        var definitions = EntityIds(ifc, "IFCRELDEFINESBYPROPERTIES")
            .Select(id => EntityArgs(ifc, id, "IFCRELDEFINESBYPROPERTIES"))
            .Where(args => args[4].Trim('(', ')').Split(',').Contains($"#{doorId}"))
            .Select(args => int.Parse(args[5].TrimStart('#')));
        int quantitySet = Assert.Single(definitions.Intersect(EntityIds(ifc, "IFCELEMENTQUANTITY")));
        var members = EntityArgs(ifc, quantitySet, "IFCELEMENTQUANTITY")[5].Trim('(', ')').Split(',')
            .Select(reference => int.Parse(reference.TrimStart('#')));
        int areaId = Assert.Single(members.Intersect(EntityIds(ifc, "IFCQUANTITYAREA")));
        double area = double.Parse(EntityArgs(ifc, areaId, "IFCQUANTITYAREA")[3],
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(squareFeet * 0.09290304, area, 6);
    }

    private static string LayerUsageDirection(string ifc, int productId)
    {
        var rel = Assert.Single(EntityIds(ifc, "IFCRELASSOCIATESMATERIAL")
            .Select(id => EntityArgs(ifc, id, "IFCRELASSOCIATESMATERIAL")),
            args => args[4].Trim('(', ')').Split(',').Contains($"#{productId}"));
        return EntityArgs(ifc, int.Parse(rel[5].TrimStart('#')), "IFCMATERIALLAYERSETUSAGE")[1];
    }

    /// <summary>A 4 x 3 x 0.5 box roof: bottom and top as horizontal faces with a world-aligned plane frame.</summary>
    private static GeometryData RoofPrism(bool slopedTop)
    {
        double[][] plan = [[0, 0], [4, 0], [4, 3], [0, 3]];
        double[] u = [1, 0, 0], v = [0, 1, 0];
        var faces = new List<GeoFace>
        {
            new() { Origin = [0, 0, 0], Normal = [0, 0, -1], PlaneXAxis = u, PlaneYAxis = v,
                OuterVertices = plan.AsEnumerable().Reverse().Select(p => new[] { p[0], p[1], 0d }).ToList() },
            new() { Origin = [0, 0, 0.5], Normal = slopedTop ? [0.1, 0, Math.Sqrt(0.99)] : [0, 0, 1], PlaneXAxis = u, PlaneYAxis = v,
                OuterVertices = plan.Select(p => new[] { p[0], p[1], 0.5 }).ToList() },
        };
        for (int i = 0; i < plan.Length; i++)
        {
            var p = plan[i];
            var q = plan[(i + 1) % plan.Length];
            double dx = q[0] - p[0], dy = q[1] - p[1], length = Math.Sqrt(dx * dx + dy * dy);
            faces.Add(new GeoFace
            {
                Origin = [p[0], p[1], 0], Normal = [dy / length, -dx / length, 0],
                OuterVertices = [[p[0], p[1], 0], [q[0], q[1], 0], [q[0], q[1], 0.5], [p[0], p[1], 0.5]],
            });
        }
        return new GeometryData
        {
            BoundingBox = [0, 0, 0, 4, 3, 0.5], Faces = faces,
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
        };
    }

    [Theory]
    [InlineData(IfcProfile.Legacy, false)]
    [InlineData(IfcProfile.Current, false)]
    [InlineData(IfcProfile.Current, true)]
    public void RoofWhoseBodyIsAVerticalExtrusion_IsAContainerOfASlabInCurrent(IfcProfile version, bool slopedTop)
    {
        // An extrusion body goes to an IfcSlab (ROOF) aggregated by the IfcRoof; the type is the slab's. A sloped body stays on the roof.
        var type = TypeElement(500, "Warm roof", "Basic Roof");
        type.CategoryId = -2000035;
        var roof = Product(100, -2000035, "OST_Roofs", 500, null);
        roof.Geometry = RoofPrism(slopedTop); roof.BoundingBox = [0, 0, 0, 4, 3, 0.5];
        string ifc = ExportToText(version, type, roof);
        int roofId = SoleEntityId(ifc, "IFCROOF");
        var roofArgs = EntityArgs(ifc, roofId, "IFCROOF");
        if (version == IfcProfile.Legacy || slopedTop)
        {
            Assert.NotEqual("$", roofArgs[6]);
            Assert.Empty(EntityIds(ifc, "IFCSLAB"));
            return;
        }
        Assert.Equal("$", roofArgs[6]);
        int slabId = SoleEntityId(ifc, "IFCSLAB");
        var slab = EntityArgs(ifc, slabId, "IFCSLAB");
        Assert.Equal(roofArgs[2].TrimEnd('\'') + ":1'", slab[2]);
        Assert.Equal(roofArgs[4], slab[4]);
        Assert.NotEqual("$", slab[6]);
        Assert.Equal(roofArgs[7], slab[7]);
        Assert.Equal(".ROOF.", slab[8]);
        var rel = Assert.Single(EntityIds(ifc, "IFCRELAGGREGATES").Select(id => EntityArgs(ifc, id, "IFCRELAGGREGATES")),
            args => args[4] == $"#{roofId}");
        Assert.Equal($"(#{slabId})", rel[5]);
        int typeId = SoleEntityId(ifc, "IFCSLABTYPE");
        Assert.Equal(".ROOF.", EntityArgs(ifc, typeId, "IFCSLABTYPE")[9]);
        Assert.Equal($"(#{slabId})", SoleRelDefinesByType(ifc).RelatedObjects);
        // One Pset_QuantityTakeOff for both, Pset_SlabCommon for the slab only.
        Assert.Contains(EntityIds(ifc, "IFCRELDEFINESBYPROPERTIES").Select(id => EntityArgs(ifc, id, "IFCRELDEFINESBYPROPERTIES")),
            args => args[4] == $"(#{roofId},#{slabId})");
        Assert.Contains(EntityIds(ifc, "IFCPROPERTYSET").Select(id => EntityArgs(ifc, id, "IFCPROPERTYSET")),
            args => args[2] == "'Pset_SlabCommon'");
    }

    [Theory]
    [InlineData("Basic Roof:XS_ЛОТ_Тип_21:9792381", "Basic Roof:XS_Р\u203AРћРў_РўРёРї_21:9792381:1")]
    [InlineData("Basic Roof:Warm Roof - Timber:724430", "Basic Roof:Warm Roof - Timber:724430:1")]
    public void RoofSlabName_ReadsTheUtf8BytesOfTheRoofNameAsWindows1251(string roofName, string expected) =>
        Assert.Equal(expected, IfcExporter.RoofSlabNameCurrent(roofName));

    [Theory]
    [InlineData("instance", true)] [InlineData("type", true)]
    [InlineData("expression", false)] [InlineData("conflict", false)]
    public void NewCurtainContainer_ExplicitReferencePrecedesOwnName(string mode, bool expected)
    {
        var type = TypeElement(500, "Unvalidated type-name fallback", null);
        type.CategoryId = WallCategoryId; type.SerializedCurtainWallFunction = 1;
        type.SerializedCurtainWallTypeName = "Own native name";
        var parent = Product(100, WallCategoryId, "OST_Walls", 500, null);
        parent.TypeId = 500; parent.Geometry = null; parent.BoundingBox = null;
        var child = Product(101, -2000170, "OST_CurtainWallPanels", null, null);
        child.SerializedCurtainHostId = 100;
        var scope = mode == "type" ? type : parent;
        scope.Parameters.Add(new Parameter { Definition = new Definition { Name = "Reference" },
            StorageType = StorageType.String, Value = mode == "expression" ? "{Name}" : "Authored Reference" });
        if (mode == "conflict") scope.Parameters.Add(new Parameter { Definition = new Definition { Name = "Reference" }, StorageType = StorageType.String, Value = "Different Reference" });
        string ifc = ExportToText(type, parent, child);
        Assert.Equal(expected, ifc.Contains($"'{IfcGuid.FromName("qto-pset:100")}'"));
        Assert.Single(EntityIds(ifc, "IFCCURTAINWALL"));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public void CurtainReference_UsesOwnNameForReferenceAndProductName(bool geometryless, bool basicType)
    {
        var type = TypeElement(500, "Legacy mark", null);
        type.CategoryId = WallCategoryId;
        if (basicType) type.SerializedBasicWallTypeFunction = 1;
        else type.SerializedCurtainWallFunction = 1;
        var parent = Product(100, WallCategoryId, "OST_Walls", 500, null);
        if (geometryless) { parent.TypeId = 500; parent.Geometry = null; parent.BoundingBox = null; }
        var child = Product(101, -2000170, "OST_CurtainWallPanels", null, null);
        child.SerializedCurtainHostId = 100;
        if (basicType) child.References!.Add(100);
        var level = new Element { Id = 900, Name = "Level 1", IsNativeLevel = true,
            SerializedFamilyId = -1, SerializedUnplacedOwnerId = -1, SerializedOwnerDbViewId = -1,
            Parameters = [new Parameter { Id = -1007111, StorageType = StorageType.Integer, Value = 1 },
                new Parameter { Id = -1007102, StorageType = StorageType.Double, Value = 0d }] };
        string before = ExportToText(type, parent, child, level);
        if (basicType) type.SerializedBasicWallTypeName = "Level 1";
        else type.SerializedCurtainWallTypeName = "Level 1";
        string after = ExportToText(type, parent, child, level);
        Assert.Contains(EntityIds(after, "IFCBUILDINGSTOREY"), id => EntityArgs(after, id, "IFCBUILDINGSTOREY")[2] == "'Level 1'");
        int beforeId = SoleEntityId(before, "IFCCURTAINWALL"), afterId = SoleEntityId(after, "IFCCURTAINWALL");
        foreach (int index in new[] { 0, 7 })
            Assert.Equal(EntityArgs(before, beforeId, "IFCCURTAINWALL")[index], EntityArgs(after, afterId, "IFCCURTAINWALL")[index]);
        // The own type name also names the product.
        Assert.Equal("'OST_Walls:Level 1:100'", EntityArgs(after, afterId, "IFCCURTAINWALL")[2]);
        Assert.Equal("'OST_Walls:Level 1'", EntityArgs(after, afterId, "IFCCURTAINWALL")[4]);
        int qto = Assert.Single(EntityIds(after, "IFCPROPERTYSET"), id =>
            EntityArgs(after, id, "IFCPROPERTYSET")[0] == $"'{IfcGuid.FromName("qto-pset:100")}'");
        string propertyRef = EntityArgs(after, qto, "IFCPROPERTYSET")[4].Trim('(', ')');
        Assert.Equal("IFCIDENTIFIER('Level 1')", EntityArgs(after, int.Parse(propertyRef.TrimStart('#')), "IFCPROPERTYSINGLEVALUE")[2]);
        int common = CurtainOccurrencePsetId(after, afterId);
        var commonProperties = EntityArgs(after, common, "IFCPROPERTYSET")[4].Trim('(', ')').Split(',');
        Assert.Contains(propertyRef, commonProperties);
        Assert.Equal(2, commonProperties.Length);
        // Reference is an occurrence property, not a property of the type of a type.
        int typeId = SoleEntityId(after, "IFCCURTAINWALLTYPE");
        string typePsetRef = EntityArgs(after, typeId, "IFCCURTAINWALLTYPE")[5].Trim('(', ')');
        int typePset = int.Parse(typePsetRef.TrimStart('#'));
        string typePropertyRef = EntityArgs(after, typePset, "IFCPROPERTYSET")[4].Trim('(', ')');
        Assert.Equal("'IsExternal'", EntityArgs(after, int.Parse(typePropertyRef.TrimStart('#')), "IFCPROPERTYSINGLEVALUE")[0]);
        Assert.Equal("Legacy mark", type.Name);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CurtainCommon_ReferenceAndIsExternalHaveIndependentValidation(bool explicitReference)
    {
        var type = TypeElement(500, "Unvalidated legacy", null);
        type.CategoryId = WallCategoryId; type.SerializedCurtainWallFunction = 1;
        if (!explicitReference) type.SerializedCurtainWallTypeName = "Own";
        var parent = Product(100, WallCategoryId, "OST_Walls", 500, null);
        parent.Parameters.Add(new Parameter { Definition = new Definition { Name = "IsExternal" }, StorageType = StorageType.String, Value = "{Expression}" });
        if (explicitReference) parent.Parameters.Add(new Parameter { Definition = new Definition { Name = "Reference" }, StorageType = StorageType.String, Value = "Authored" });
        var child = Product(101, -2000170, "OST_CurtainWallPanels", null, null);
        child.SerializedCurtainHostId = 100;
        string ifc = ExportToText(type, parent, child);
        int common = CurtainOccurrencePsetId(ifc, SoleEntityId(ifc, "IFCCURTAINWALL"));
        string propertyRef = EntityArgs(ifc, common, "IFCPROPERTYSET")[4].Trim('(', ')');
        var property = EntityArgs(ifc, int.Parse(propertyRef.TrimStart('#')), "IFCPROPERTYSINGLEVALUE");
        Assert.Equal("'Reference'", property[0]);
        Assert.Equal(explicitReference ? "IFCIDENTIFIER('Authored')" : "IFCIDENTIFIER('Own')", property[2]);
    }

    [Theory]
    [InlineData("legacy")] [InlineData("expression")] [InlineData("conflict")]
    public void CurtainCommon_UnvalidatedReferenceDoesNotRemoveIsExternal(string mode)
    {
        var type = TypeElement(500, "Unvalidated legacy", null);
        type.CategoryId = WallCategoryId; type.SerializedCurtainWallFunction = 1;
        var parent = Product(100, WallCategoryId, "OST_Walls", 500, null);
        if (mode != "legacy")
        {
            type.SerializedCurtainWallTypeName = "Own";
            parent.Parameters.Add(new Parameter { Definition = new Definition { Name = "Reference" }, StorageType = StorageType.String, Value = mode == "expression" ? "{Name}" : "One" });
            if (mode == "conflict") parent.Parameters.Add(new Parameter { Definition = new Definition { Name = "Reference" }, StorageType = StorageType.String, Value = "Two" });
        }
        var child = Product(101, -2000170, "OST_CurtainWallPanels", null, null);
        child.SerializedCurtainHostId = 100;
        string ifc = ExportToText(type, parent, child);
        int common = CurtainOccurrencePsetId(ifc, SoleEntityId(ifc, "IFCCURTAINWALL"));
        string propertyRef = EntityArgs(ifc, common, "IFCPROPERTYSET")[4].Trim('(', ')');
        Assert.Equal("'IsExternal'", EntityArgs(ifc, int.Parse(propertyRef.TrimStart('#')), "IFCPROPERTYSINGLEVALUE")[0]);
    }

    [Theory]
    [InlineData("instance", "Occurrence")] [InlineData("type", "Type")]
    [InlineData("type_alias", "Type alias")] [InlineData("none", "Own")]
    [InlineData("expression", null)] [InlineData("conflict", null)] [InlineData("nonstring", null)]
    public void CurtainReference_PreservesExplicitPrecedenceAndBlocks(string mode, string? expected)
    {
        var type = TypeElement(500, "Legacy", null);
        type.CategoryId = WallCategoryId; type.SerializedCurtainWallFunction = 1;
        type.SerializedCurtainWallTypeName = "Own";
        var parent = Product(100, WallCategoryId, "OST_Walls", 500, null);
        if (mode != "none")
        {
            var scope = mode.StartsWith("type") ? type : parent;
            scope.Parameters.Add(new Parameter { Definition = new Definition { Name = mode == "type_alias" ? "Reference[Type]" : "Reference" },
                StorageType = mode == "nonstring" ? StorageType.Integer : StorageType.String,
                Value = mode switch { "instance" => "Occurrence", "type" => "Type", "type_alias" => "Type alias", "expression" => "{Name}", "nonstring" => 1, _ => "One" } });
            if (mode == "conflict") scope.Parameters.Add(new Parameter { Definition = new Definition { Name = "Reference" }, StorageType = StorageType.String, Value = "Two" });
        }
        Assert.Equal(expected, IfcExporter.ResolveCurtainWallReference(parent, type, "Legacy"));
    }

    [Theory]
    [InlineData("noncanonical")] [InlineData("wrong_type")] [InlineData("type_category")]
    [InlineData("occurrence_category")] [InlineData("missing_function")] [InlineData("missing_name")]
    [InlineData("basic_conflict")] [InlineData("stacked_conflict")]
    [InlineData("type_override")] [InlineData("occurrence_override")]
    public void CurtainReference_MissingValidationRetainsOnlyTheLegacyFallback(string mutation)
    {
        var type = TypeElement(500, "Legacy", null);
        type.CategoryId = WallCategoryId; type.SerializedCurtainWallFunction = 1;
        type.SerializedCurtainWallTypeName = "Own";
        var parent = Product(100, WallCategoryId, "OST_Walls", 500, null);
        switch (mutation)
        {
            case "noncanonical": parent.Parameters.RemoveAll(p => p.Id == ElemTypeParamId); parent.TypeId = 500; break;
            case "wrong_type": type.Id = 501; break;
            case "type_category": type.CategoryId = -2003500; break;
            case "occurrence_category": parent.CategoryId = -2000170; break;
            case "missing_function": type.SerializedCurtainWallFunction = null; break;
            case "missing_name": type.SerializedCurtainWallTypeName = null; break;
            case "basic_conflict": type.SerializedBasicWallTypeFunction = 1; break;
            case "stacked_conflict": type.IsSerializedStackedWallType = true; break;
            default: (mutation == "type_override" ? type : parent).Parameters.Add(new Parameter {
                Definition = new Definition { Name = "IfcExportAs" }, StorageType = StorageType.String, Value = "IfcCurtainWall" }); break;
        }
        Assert.Equal("Legacy", IfcExporter.ResolveCurtainWallReference(parent, type, "Legacy"));
        Assert.Null(IfcExporter.ResolveCurtainWallReference(parent, type, null));
    }

    [Fact]
    public void CurtainReference_BasicNameCannotBorrowCurtainValidationOrViceVersa()
    {
        var type = TypeElement(500, "Legacy", null);
        type.CategoryId = WallCategoryId; type.SerializedBasicWallTypeFunction = 1;
        type.SerializedCurtainWallTypeName = "Unrelated";
        var parent = Product(100, WallCategoryId, "OST_Walls", 500, null);
        Assert.Null(IfcExporter.ResolveCurtainWallReference(parent, type, null));
        type.SerializedBasicWallTypeName = "Basic own";
        Assert.Equal("Basic own", IfcExporter.ResolveCurtainWallReference(parent, type, null));
        type.SerializedCurtainWallFunction = 1;
        Assert.Null(IfcExporter.ResolveCurtainWallReference(parent, type, null));
        type.SerializedBasicWallTypeFunction = null;
        type.SerializedCurtainWallTypeName = null;
        Assert.Null(IfcExporter.ResolveCurtainWallReference(parent, type, null));
    }

    [Theory]
    [InlineData("unemitted_child")] [InlineData("generic_reference")]
    [InlineData("missing_type_validation")] [InlineData("ambiguous_type")]
    [InlineData("type_override")] [InlineData("duplicate_child")]
    [InlineData("duplicate_parent")] [InlineData("duplicate_type")]
    [InlineData("noncanonical_type")] [InlineData("wrong_child_category")]
    public void GeometrylessCurtainParent_IsNotCreatedFromUnvalidatedMembership(string mutation)
    {
        var type = TypeElement(500, "Curtain type", null);
        type.CategoryId = WallCategoryId; type.SerializedCurtainWallFunction = 1;
        var parent = Product(100, WallCategoryId, "OST_Walls", 500, null);
        parent.TypeId = 500; parent.Geometry = null; parent.BoundingBox = null;
        var child = Product(101, -2000170, "OST_CurtainWallPanels", null, null);
        child.SerializedCurtainHostId = 100; child.References!.Add(100);
        var elements = new List<Element> { type, parent, child };
        switch (mutation)
        {
            case "unemitted_child": child.Geometry = null; child.BoundingBox = null; break;
            case "generic_reference": child.SerializedCurtainHostId = null; break;
            case "missing_type_validation": type.SerializedCurtainWallFunction = null; break;
            case "ambiguous_type": type.SerializedBasicWallTypeFunction = 1; break;
            case "type_override": type.Parameters.Add(new Parameter { Definition = new Definition { Name = "IfcExportAs" }, StorageType = StorageType.String, Value = "IfcWall" }); break;
            case "duplicate_child": elements.Add(child); break;
            case "duplicate_parent": elements.Add(parent); break;
            case "duplicate_type": elements.Add(type); break;
            case "noncanonical_type": parent.Parameters.RemoveAll(p => p.Id == ElemTypeParamId); break;
            case "wrong_child_category": child.CategoryId = -2000340; break;
        }
        string ifc = ExportToText(elements.ToArray());
        Assert.Empty(EntityIds(ifc, "IFCCURTAINWALL"));
    }

    [Fact]
    public void CurtainParentWithBoundChildren_IsARepresentationlessAggregateContainer()
    {
        var type = TypeElement(500, "Curtain type", null);
        type.CategoryId = WallCategoryId; type.SerializedCurtainWallFunction = 1;
        var parent = Product(100, WallCategoryId, "OST_Walls", 500, null);
        var child = Product(101, -2000170, "OST_CurtainWallPanels", null, null);
        child.SerializedCurtainHostId = 100;
        string ifc = ExportToText(type, parent, child);
        int parentId = SoleEntityId(ifc, "IFCCURTAINWALL");
        // A curtain wall with exactly bound components is a container without a body, whatever geometry Revit holds, and aggregates
        // its components.
        Assert.Equal("$", EntityArgs(ifc, parentId, "IFCCURTAINWALL")[6]);
        Assert.Contains(EntityIds(ifc, "IFCRELAGGREGATES")
            .Select(id => EntityArgs(ifc, id, "IFCRELAGGREGATES")), args => args[4] == $"#{parentId}");
    }

    private static Element Railing(
        int id, int? elemTypeParamId, int? nativeMasterSymbolId, double centreX = 0) =>
        Product(id, RailingCategoryId, "OST_StairsRailing", elemTypeParamId,
            nativeMasterSymbolId, centreX);

    private static Element Product(
        int id, int categoryId, string category, int? elemTypeParamId,
        int? nativeMasterSymbolId, double centreX = 0)
    {
        var parameters = new List<Parameter>
        {
            new() { Id = PhaseCreatedParamId, StorageType = StorageType.ElementId, Value = 3 },
        };
        if (elemTypeParamId is int etp)
            parameters.Add(new Parameter
            {
                Id = ElemTypeParamId,
                Definition = new Definition { Id = ElemTypeParamId, Name = "ELEM_TYPE_PARAM" },
                StorageType = StorageType.ElementId,
                Value = etp,
            });

        return new Element
        {
            Id = id,
            SuperInstanceId = 999, // satisfies the hosted-category (door/window) host gate
            IsDiscovered = true,
            CategoryId = categoryId,
            Category = category,
            Name = $"Product {id}",
            References = [3],
            Parameters = parameters,
            NativeMasterSymbolId = nativeMasterSymbolId,
            BoundingBox = [centreX, 0, 0, centreX + 4, 3, 2],
            Geometry = SolidPrism(centreX),
        };
    }

    private static GeometryData SolidPrism(double centreX)
    {
        double[][] points =
        [
            [centreX, 0, 0], [centreX + 4, 0, 0],
            [centreX + 4, 3, 0], [centreX, 3, 0],
            [centreX, 0, 2], [centreX + 4, 0, 2],
            [centreX + 4, 3, 2], [centreX, 3, 2],
        ];
        int[][] faces =
        [
            [0, 3, 2, 1], [4, 5, 6, 7],
            [0, 1, 5, 4], [1, 2, 6, 5],
            [2, 3, 7, 6], [3, 0, 4, 7],
        ];
        return new GeometryData
        {
            BoundingBox = [centreX, 0, 0, centreX + 4, 3, 2],
            Faces = faces.Select(indices => new GeoFace
            {
                OuterVertices = indices.Select(index => points[index]).ToList(),
            }).ToList(),
            BrepSolids =
            [
                new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] },
            ],
        };
    }

    private static string ExportToText(params Element[] elements) => ExportToText(IfcProfile.Legacy, elements);

    private static string ExportToText(IfcProfile version, params Element[] elements)
    {
        var info = new RevitFileInfo();
        info.Elements.AddRange(elements);
        string path = Path.Combine(
            Path.GetTempPath(), $"revitreader-type-objects-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter() { Profile = version }.Export(info, path);
            return File.ReadAllText(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static List<int> EntityIds(string ifc, string ifcType) =>
        Regex.Matches(ifc, $@"#(\d+)={ifcType}\(")
            .Select(match => int.Parse(match.Groups[1].Value)).ToList();

    private static int SoleEntityId(string ifc, string ifcType) =>
        Assert.Single(EntityIds(ifc, ifcType));

    /// <summary>Finds the sole product of the given IFC class whose Tag argument (the
    /// second-to-last top-level argument for the classes used in these tests) equals
    /// <paramref name="tag"/>, avoiding any dependency on entity id assignment order.</summary>
    private static int SoleEntityIdForTag(string ifc, string ifcType, string tag)
    {
        foreach (int id in EntityIds(ifc, ifcType))
        {
            var args = EntityArgs(ifc, id, ifcType);
            if (args.Count > 7 && args[7] == $"'{tag}'") return id;
        }
        throw new Xunit.Sdk.XunitException($"no {ifcType} with Tag '{tag}' found");
    }

    /// <summary>Splits one entity's raw argument list on TOP-LEVEL commas only (depth-aware,
    /// so a nested list like RelatedObjects' "(#1,#2)" is not split internally).</summary>
    private static List<string> EntityArgs(string ifc, int entityId, string ifcType)
    {
        var match = Assert.Single(Regex.Matches(
            ifc, $@"#{entityId}={ifcType}\((.*)\);\r?\n"));
        string raw = match.Groups[1].Value;
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(raw[start..i]);
                start = i + 1;
            }
        }
        parts.Add(raw[start..]);
        return parts;
    }

    private static (string GlobalId, int RelatingType, string RelatedObjects) SoleRelDefinesByType(string ifc)
    {
        int relId = SoleEntityId(ifc, "IFCRELDEFINESBYTYPE");
        var args = EntityArgs(ifc, relId, "IFCRELDEFINESBYTYPE");
        Assert.Equal(6, args.Count);
        string globalId = args[0].Trim('\'');
        string relatedObjects = args[4];
        int relatingType = int.Parse(args[5].TrimStart('#'));
        return (globalId, relatingType, relatedObjects);
    }
}
