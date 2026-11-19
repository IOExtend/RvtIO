using RvtIO.Compact;
using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The "IFC Parameters" group of the compact JSON properties block. The vectors are one element per
/// (class, category, kind) of the all-element JSON of six sample projects; the expectation is the group of that element.</summary>
public class CompactIfcParametersTests
{
    private static ushort ClassId(string schemaName) =>
        (ushort)TfBlockDeserializer.Schemas.First(entry => entry.Value.Name == schemaName).Key;

    private static string Text(IEnumerable<CompactProperty> properties) =>
        string.Join(";", properties.OrderBy(p => p.Label, StringComparer.Ordinal).Select(p => $"{p.Label}={p.Value}"));


    [Theory]
    // Test_2022 roof 724430: the export id is the episode GUID with the id in its last 32 bits (IfcRoof of the expected export).
    [InlineData("1551350c-45e5-41b8-bdf1-bcfa6794dc6a-000b0dce", "0LKJKCHUL1kBtnlFfddz6a")]
    // sample project A wall 6900242 was renumbered: its UniqueId keeps the original id 6879768 (0x0068fa18).
    [InlineData("6a7c3860-b4f0-401b-a699-81014e2efddb-0068fa18", "1gV3XWjF106wQPWG5EHWV3")]
    [InlineData("95db5b0a-a7bf-4747-af03-b1f5fe59fca1-1000f4241", "2LsriAfxz7Hwy3iVJ_LhxW")]
    public void IfcGuid_IsTheIfcBase64OfTheEpisodeGuidXoredWithTheId(string uniqueId, string expected) =>
        Assert.Equal(expected, CompactIfcParameters.IfcGuidOf(uniqueId));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1551350c-45e5-41b8-bdf1-bcfa6794dc6a")]
    [InlineData("1551350c-45e5-41b8-bdf1-bcfa6794dc6a_000b0dce")]
    [InlineData("1551350c-45e5-41b8-bdf1-bcfa6794dc6a-000b0a4g")]
    [InlineData("zz51350c-45e5-41b8-bdf1-bcfa6794dc6a-000b0dce")]
    public void IfcGuid_OfSomethingThatIsNotAUniqueId_IsNull(string? uniqueId) =>
        Assert.Null(CompactIfcParameters.IfcGuidOf(uniqueId));


    [Fact]
    public void Properties_ComeInAscendingParameterIdOrder_WithTheConstantValues()
    {
        Assert.Equal(
            ["IFC Predefined Type=", "Export to IFC As=", "Export to IFC=0", "IfcGUID=G"],
            CompactIfcParameters.Properties(CompactIfcParameters.Form.Instance, "G", null).Select(p => $"{p.Label}={p.Value}"));
        Assert.Equal(
            ["Type IFC Predefined Type=", "Export Type to IFC As=", "Export Type to IFC=0", "Type IfcGUID=G"],
            CompactIfcParameters.Properties(CompactIfcParameters.Form.Type, "G", null).Select(p => $"{p.Label}={p.Value}"));
        Assert.Equal(["Export to IFC=0", "IfcGUID=G"],
            CompactIfcParameters.Properties(CompactIfcParameters.Form.InstanceTwo, "G", null).Select(p => $"{p.Label}={p.Value}"));
        Assert.Equal(["Export Type to IFC=0", "Type IfcGUID=G"],
            CompactIfcParameters.Properties(CompactIfcParameters.Form.TypeTwo, "G", null).Select(p => $"{p.Label}={p.Value}"));
        Assert.Equal(["Export to IFC=0"],
            CompactIfcParameters.Properties(CompactIfcParameters.Form.ExportOnly, "G", null).Select(p => $"{p.Label}={p.Value}"));
        Assert.All(CompactIfcParameters.Properties(CompactIfcParameters.Form.Instance, "G", null),
            p => Assert.Equal("IFC Parameters", p.Group));
        Assert.Empty(CompactIfcParameters.Properties(CompactIfcParameters.Form.None, "G", "x"));
    }

    [Fact]
    public void Properties_LeaveOutTheGuidWhenItIsUnknown_AndTheLabelsTheFileStoresItself()
    {
        Assert.Equal(["IFC Predefined Type=", "Export to IFC As=", "Export to IFC=0"],
            CompactIfcParameters.Properties(CompactIfcParameters.Form.Instance, null, null).Select(p => $"{p.Label}={p.Value}"));
        Assert.Equal(["IFC Predefined Type=", "Export to IFC As=", "IfcGUID=G"],
            CompactIfcParameters.Properties(CompactIfcParameters.Form.Instance, "G", null,
                new HashSet<int> { (int)BuiltInParameter.IFC_EXPORT_ELEMENT }).Select(p => $"{p.Label}={p.Value}"));
    }

    [Fact]
    public void Operation_FollowsTheOtherLabels_AndCanBeEmpty()
    {
        Assert.Equal(["Export Type to IFC=0", "Type IfcGUID=G", "Operation=SinglePanel"],
            CompactIfcParameters.Properties(CompactIfcParameters.Form.TypeTwo, "G", "SinglePanel").Select(p => $"{p.Label}={p.Value}"));
        Assert.Equal("Operation=", CompactIfcParameters.Properties(CompactIfcParameters.Form.Type, "G", "")
            .Select(p => $"{p.Label}={p.Value}").Last());
    }

    [Theory]
    [InlineData("SWall", -2000011, false, "Instance")]
    [InlineData("BasicWallType", -2000011, true, "Type")]
    [InlineData("FamilyInstance", -2000023, false, "Instance")]
    [InlineData("FamilySymbol", -2000023, true, "Type")]
    [InlineData("Grid", -2000220, false, "InstanceTwo")]
    [InlineData("LevelAttributes", -2000240, true, "TypeTwo")]
    [InlineData("DBViewSchedule", -2000573, false, "ExportOnly")]
    // an annotation family has no IFC parameters, a detail item has
    [InlineData("FamilyInstance", -2000150, false, "None")]
    [InlineData("FamilyInstance", -2002000, false, "Instance")]
    // a class that never lists them does not, whatever category the reader gives it
    [InlineData("GStyleElem", -2000700, false, "None")]
    [InlineData("RebarHookType", -2009000, true, "None")]
    [InlineData("RepeatingDetailAttributes", -2002000, true, "None")]
    [InlineData("MultistoryStairs", -2000980, false, "None")]
    [InlineData("Text3dElem", -2000151, false, "None")]
    // lists the four type labels and an empty IfcGUID in the one file that has it: not shipped
    [InlineData("DirectShape", -2000100, false, "None")]
    // 6 of 72 281 curve elements list them, and the reader's CategoryId of the others is not reliable: not shipped
    [InlineData("CurveElem", -2002000, false, "None")]
    [InlineData("SegmentCenterLine", -2008001, false, "Instance")]
    [InlineData("DuctFittingCenterLine", -2008066, false, "Instance")]
    public void FormOf_FollowsTheClassAndTheCategory(string recordClass, int category, bool isType, string expected) =>
        Assert.Equal(expected, CompactIfcParameters.FormOf(recordClass, category, isType).ToString());

    [Fact]
    public void FormOf_UnknownCategory_ListsNothing() =>
        Assert.Equal(CompactIfcParameters.Form.None, CompactIfcParameters.FormOf("SWall", null, false));

    [Fact]
    public void FormOf_Viewport_ListsExportToIfcOnlyWhenItShowsASchedule()
    {
        Assert.Equal(CompactIfcParameters.Form.ExportOnly, CompactIfcParameters.FormOf("Viewport", -2000510, false, scheduleViewport: true));
        Assert.Equal(CompactIfcParameters.Form.None, CompactIfcParameters.FormOf("Viewport", -2000510, false));
    }

    [Theory]
    [InlineData("FamilySymbol", -2000023, true)]
    [InlineData("FamilySymbol", -2000014, true)]
    [InlineData("FamilySymbol", -2000080, false)]
    [InlineData("BasicWallType", -2000023, false)]
    public void Operation_IsListedByDoorAndWindowSymbolsOnly(string recordClass, int category, bool expected) =>
        Assert.Equal(expected, CompactIfcParameters.ListsOperation(recordClass, category, CompactIfcParameters.Form.Type));


    public static IEnumerable<object[]> Vectors() => ReferenceVectors.Select(line => new object[] { line });

    [Theory]
    [MemberData(nameof(Vectors))]
    public void ReferenceElement_ListsTheGroupReferenceListsForIt(string line)
    {
        string[] f = line.Split('|');
        string recordClass = f[0];
        int? category = f[1].Length == 0 ? null : int.Parse(f[1]);
        bool isType = f[2] == "1", schedule = f[3] == "1";
        string expected = f[5];

        var form = CompactIfcParameters.FormOf(recordClass, category, isType, schedule);
        string? operation = CompactIfcParameters.ListsOperation(recordClass, category, form)
            ? expected.Split(';').Single(pair => pair.StartsWith("Operation=")).Substring("Operation=".Length) : null;
        var properties = CompactIfcParameters.Properties(form, CompactIfcParameters.IfcGuidOf(f[4]), operation);

        Assert.Equal(expected, Text(properties));
    }


    private static Element Wall(int id, string uniqueId = "1551350c-45e5-41b8-bdf1-bcfa6794dc6a-000b0dce") => new()
    {
        Id = id, CategoryId = -2000011, NativeHeaderCategoryId = -2000011, SerializedOwnElementClassId = ClassId("SWall"),
        SerializedExportId = ElementExportIds.ExportId(Guid.Parse(uniqueId[..36]), 0x000b0dceu),
        UniqueId = uniqueId,
    };

    [Theory]
    [InlineData(-2002000, 4)]   // Detail Components: the filled regions of every reference project
    [InlineData(-2000190, 0)]   // Filled Region: the regions of the family files list none, although the reader's CategoryId is Detail Components
    public void FilledRegion_ListsTheInstanceFormByItsHeaderCategory(int headerCategory, int count)
    {
        var region = new Element
        {
            Id = 1, CategoryId = -2002000, NativeHeaderCategoryId = headerCategory,
            SerializedOwnElementClassId = ClassId("FilledRegion"), UniqueId = "d32651fd-8177-472a-a10a-14863a6ba772-001526c9",
            SerializedExportId = ElementExportIds.ExportId(Guid.Parse("d32651fd-8177-472a-a10a-14863a6ba772"), 0x001526c9u),
        };
        Assert.Equal(count, new CompactIfcParameters().GetProperties(region).Count);
    }

    [Fact]
    public void Element_ListsTheInstanceFormWithItsExportId()
    {
        var properties = new CompactIfcParameters().GetProperties(Wall(724430));
        Assert.Equal("Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=0LKJKCHUL1kBtnlFfddz6a", Text(properties));
    }

    [Theory]
    [InlineData(-2000011, 4)]   // Walls
    [InlineData(-2000170, 4)]   // Curtain Panels: a wall used as a curtain panel lists them as well
    [InlineData(-2003500, 0)]   // Stacked Walls: none, although the reader's category of the wall is Walls
    public void Wall_ListsTheInstanceFormByItsHeaderCategory(int headerCategory, int count)
    {
        Element wall = Wall(1);
        wall.NativeHeaderCategoryId = headerCategory;
        Assert.Equal(count, new CompactIfcParameters().GetProperties(wall).Count);
    }

    [SkippableFact]
    public void Test2022_EveryWallHasTheHeaderCategoryWalls_AndListsTheInstanceForm()
    {
        var info = UserParameterCatalogTests.Test2022.Value;
        var ifc = new CompactIfcParameters(info);
        var walls = info.Elements.Where(e => CompactIfcParameters.ClassName(e) == "SWall").ToList();
        Assert.Equal(60, walls.Count);   // the expected export: 60 SWall of header category Walls (-2000011)
        Assert.All(walls, wall =>
        {
            Assert.Equal(-2000011, wall.NativeHeaderCategoryId);
            Assert.Equal(wall.SerializedFamilyId is null or -1 ? -2000011 : null, info.UserParameters!.BoundCategoryOf(wall));   // a wall with a family id lists no bound parameter (a sample project)
            Assert.Equal(4, ifc.GetProperties(wall).Count);
        });
    }

    [Fact]
    public void Wall_WithoutHeaderCategory_ListsNothing()
    {
        Element wall = Wall(1);
        wall.NativeHeaderCategoryId = null;
        Assert.Empty(new CompactIfcParameters().GetProperties(wall));
    }

    [Fact]
    public void Element_OfATypeClass_ListsTheTypeForm()
    {
        var type = new Element
        {
            Id = 5, CategoryId = -2000011, SerializedOwnElementClassId = ClassId("BasicWallType"),
            SerializedExportId = Guid.Parse("1551350c-45e5-41b8-bdf1-bcfa6794dc6a"),
        };
        Assert.Equal(["Type IFC Predefined Type", "Export Type to IFC As", "Export Type to IFC", "Type IfcGUID"],
            new CompactIfcParameters().GetProperties(type).Select(p => p.Label));
    }

    [Fact]
    public void Element_WithoutExportId_LeavesOutTheGuid()
    {
        Element wall = Wall(1);
        wall.SerializedExportId = null;
        Assert.DoesNotContain(new CompactIfcParameters().GetProperties(wall), p => p.Label == "IfcGUID");
        Assert.Equal(3, new CompactIfcParameters().GetProperties(wall).Count);
    }

    [Fact]
    public void CenterLine_ListsTheIfcGuidOfItsOwner()
    {
        var info = new RevitFileInfo();
        Element pipe = Wall(100);
        pipe.SerializedOwnElementClassId = ClassId("RbsPipeCurve");
        pipe.CategoryId = -2008044;
        var centerLine = new Element
        {
            Id = 101, CategoryId = -2008045, SerializedOwnElementClassId = ClassId("SegmentCenterLine"),
            SerializedOwningElementId = 100, SerializedExportId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
        };
        info.Elements.AddRange([pipe, centerLine]);

        var properties = new CompactIfcParameters(info).GetProperties(centerLine);
        Assert.Equal("0LKJKCHUL1kBtnlFfddz6a", properties.Single(p => p.Label == "IfcGUID").Value);
        // the owner is not known: the GUID is left out, not replaced by the center line's own
        centerLine.SerializedOwningElementId = 999;
        Assert.DoesNotContain(new CompactIfcParameters(info).GetProperties(centerLine), p => p.Label == "IfcGUID");
    }

    [Theory]
    [InlineData("SWall", "SWall")]
    [InlineData("PathRein", "PathReinforcement")]
    [InlineData("NestedFamSymRefElem", "NestedFamilyTypeReference")]
    public void ClassName_IsTheTfClassName_ExceptForRenamedClasses(string schemaName, string expected) =>
        Assert.Equal(expected, CompactIfcParameters.ClassName(new Element { SerializedOwnElementClassId = ClassId(schemaName) }));

    [Fact]
    public void Element_WithoutAClass_ListsNothing()
    {
        Element wall = Wall(1);
        wall.SerializedOwnElementClassId = null;
        Assert.Empty(new CompactIfcParameters().GetProperties(wall));
    }

    [Fact]
    public void Element_StoringAnIfcParameter_LeavesThatLabelToTheGenericPath()
    {
        Element wall = Wall(1);
        wall.Parameters.Add(new Parameter { Id = (int)BuiltInParameter.IFC_EXPORT_ELEMENT, Value = 2 });
        var labels = new CompactIfcParameters().GetProperties(wall).Select(p => p.Label).ToList();
        Assert.DoesNotContain("Export to IFC", labels);
        Assert.Contains("IfcGUID", labels);
    }

    [Fact]
    public void DoorSymbol_ListsItsStoredOperation_OrAnEmptyOne()
    {
        Element Symbol(string? operation)
        {
            var symbol = new Element { Id = 9, CategoryId = -2000023, SerializedOwnElementClassId = ClassId("FamilySymbol") };
            if (operation != null)
                symbol.Parameters.Add(new Parameter { Id = (int)BuiltInParameter.DOOR_OPERATION_TYPE, Value = operation });
            return symbol;
        }
        Assert.Equal("Operation=SinglePanel", Text(new CompactIfcParameters().GetProperties(Symbol("SinglePanel"))
            .Where(p => p.Label == "Operation")));
        Assert.Equal("Operation=", Text(new CompactIfcParameters().GetProperties(Symbol(null)).Where(p => p.Label == "Operation")));
    }

    [Fact]
    public void Viewport_ThatReferencesASchedule_ListsExportToIfc()
    {
        var info = new RevitFileInfo();
        var schedule = new Element { Id = 10, CategoryId = -2000573, SerializedOwnElementClassId = ClassId("DBViewSchedule") };
        var plan = new Element { Id = 11, CategoryId = -2000279, SerializedOwnElementClassId = ClassId("DBViewPlan") };
        Element Viewport(int id, int shown) => new()
        {
            Id = id, CategoryId = -2000510, SerializedOwnElementClassId = ClassId("Viewport"), References = [id, shown],
        };
        Element ofSchedule = Viewport(20, 10), ofPlan = Viewport(21, 11);
        info.Elements.AddRange([schedule, plan, ofSchedule, ofPlan]);

        var source = new CompactIfcParameters(info);
        Assert.Equal("Export to IFC=0", Text(source.GetProperties(ofSchedule)));
        Assert.Empty(source.GetProperties(ofPlan));
    }

    private static readonly string[] ReferenceVectors =
    [
        "AbsDuctType|-2008000|1|0|5de47e85-f1ce-4dd7-9a1a-e25cc251c808-000ee04f|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=1Tv7w5ySvDrveQubp2NoX7",
        "AbsFlexDuctType|-2008020|1|0|5de47e85-f1ce-4dd7-9a1a-e25cc251c808-000ee052|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=1Tv7w5ySvDrveQubp2NoXQ",
        "AnalyticalLinkType|-2009657|1|0|c7dfb014-08c7-426e-b3e5-26576aa11b01-0002186a|",
        "AnalyticalModelWall|-2009640|0|0|17836a3c-e764-47fa-a2e0-08216444f621-0007882d|",
        "ArcAlignment|-2000262|0|0|869691d2-0c0e-11d5-9312-0000863f27ad-00000dc7|",
        "ArcWall|-2000011|0|0|01ce1928-1585-4006-b766-c433c9c35299-0010d040|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=01pXae5OL01hTcn3F9quBP",
        "AreaReinforcementType|-2009003|1|0|73c78223-6f1d-4d62-bc0f-5ca1616bc7a3-0014162a|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=1pnu8ZRnrDOhmFNA5XVz69",
        "AssemblyCodeTable||0|0|0bfbc3fc-18df-451c-9be1-0d3b955eb870-000f4bdb|",
        "BaseRailing|-2000126|0|0|79010029-8aa9-4c91-b2c8-b7f94cb6df11-0008a53c|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=1v0G0fYgbCaRB8j$bCldej",
        "BasicWallType|-2000011|1|0|e3e052f9-0156-11d5-9301-0000863f27ad-0000018d|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=3Zu5Bv0LOHrPC10026FoOW",
        "BeamSystemTag|-2005130|0|0|9740b1f6-41ac-4e59-9332-6cfe47e333cb-0002751f|",
        "BuildingPad|-2001263|0|0|d9a4583b-3667-47f2-9136-29137bbc6cf9-00077b4f|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=3Pf5WxDcT7yf4sAHDxknUs",
        "BuildingPadType|-2001263|1|0|ef4ad1cb-015c-4bb6-9030-e7bb82f1dd34-00065411|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=3lIj7B0LnBjf0mvxk2zuab",
        "CableTray|-2008130|0|0|398c98ed-7bc4-4476-873a-54e2ad032794-000c2ba5|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=0vZ9ZjUyH4TeSwLEAj3mmn",
        "CategoryElem||0|0|e3e052f9-0156-11d5-9301-0000863f27ad-000000eb|",
        "Ceiling|-2000038|0|0|f714ef06-3fa3-4b95-b3e7-bbbd1e6eda49-000aa50c|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=3t5Ey6FwDBbRFdkxqUP7z5",
        "CompoundCeilingType|-2000038|1|0|458c0f0a-01bb-11d5-9302-0000863f27ad-000002f5|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=15Z0yA0RiHrPC20026FoLO",
        "ConceptualConstructionType|-2003418|1|0|584be8a3-b212-45c9-9779-0cdbc232fa66-0001db8b|",
        "ConduitFittingCenterLine|-2008141|0|0|b09dffb9-3b4b-4377-9202-9dca3441933d-000a5d06|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=2mdV_vEqj3Tv82dSeqIyux",
        "ConduitRun|-2008149|0|0|b09dffb9-3b4b-4377-9202-9dca3441933d-000a5cb2|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=2mdV_vEqj3Tv82dSeqIy_F",
        "ConstructionSet|-2008102|1|0|5274398b-7682-40ad-93eb-d9cfa45ef8a7-00015ce0|",
        "ContFooting|-2001300|0|0|f4bb716f-cdfc-4bdf-af35-1a12450670d4-0011f9a3|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=3qkt5lpVnBtwyr6X955ubt",
        "ContFootingType|-2001300|1|0|c0cc0b67-41ac-46d7-bc80-e9943b96122f-00014c79|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=30p0jdGQn6rxo0wPGxbrvM",
        "CopyWatchProperties||0|0|fc5f6326-6e41-4987-a437-f8bed52d8393-001312ef|",
        "CorniceAttr|-2000181|1|0|2d99307a-66cd-4439-b173-5c88d252416e-00001ea4|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=0jcJ1wPir4ER5pN8ZIKb$A",
        "CurtaSystemAttr|-2000340|1|0|281263d6-c0be-4b93-a3e6-255e17a505c0-0000bf8a|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=0e4cFMmBvBawFc9LuNfRfA",
        "CurtainRoofAttributes|-2000035|1|0|e3e052f9-0156-11d5-9301-0000863f27ad-00000152|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=3Zu5Bv0LOHrPC10026FoR$",
        "CurveElem|-2000067|0|0|a3bcf3f5-e669-4d93-9730-faa0d5bf737d-000a33d4|",
        "CurveElem|-2000954|0|0|79010029-8aa9-4c91-b2c8-b7f94cb6df11-0008a551|",
        "CutMarkType|-2000930|1|0|9020f828-ce55-4f5d-9922-27877ae62719-0002299b|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=2G8FWepbLFNPaY9uTwv0w2",
        "DBViewGraphSchedColumn|-2000279|0|0|fc5f6326-6e41-4987-a437-f8bed52d8393-001312f8|",
        "DBViewSchedule|-2000573|0|0|666da4f5-76ba-443a-9fe6-0bcd8a1bcfe9-000ea403|Export to IFC=0",
        "DBViewTypeAreaPlan||1|0|e6ac360b-aaed-4c3b-a130-36b4c2ac9d13-000d1486|",
        "DPart|-2000269|0|0|dded2a59-7629-4456-bb60-fb05ab8364a4-0011621e|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=3TxIfPTYb4LhjW_mMhaWQw",
        "DecalElement|-2000151|0|0|349b0b44-0f8f-4d71-b0b3-48cc4c2345c6-00049dd7|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=0qcmj43uzDSR2pICnC9zWH",
        "DecalType|-2000151|1|0|349b0b44-0f8f-4d71-b0b3-48cc4c2345c6-00049dad|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=0qcmj43uzDSR2pICnC9zXh",
        "DrawOrder3dElem||0|0|1f6d39b4-8c4b-45e4-a0dc-14474fd9bcb9-0010d41c|",
        "DuctFittingCenterLine|-2008066|0|0|a4d2a650-712c-4311-9076-3ed139255683-00082fa9|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=2aqgPGSIn34P1sFj4vBNag",
        "EdgeSlabAttr|-2001392|1|0|e8e255a3-8f4e-49b9-8324-0178422c4a87-00004daf|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=3eubMZZqv9kOCa0NX2B0Se",
        "ElementGroup|-2000095|0|0|d5881ba7-c6fb-4696-aebc-8d7ab6f02f63-000c35d5|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=3LY1kdnlj6bgwyZNgs$1gs",
        "ElementGroup|-2000096|0|0|cfb3fc7c-8083-4b5b-bc95-e11893627538-000454cb|",
        "ElementGroupType|-2000095|1|0|d5881ba7-c6fb-4696-aebc-8d7ab6f02f63-000c35d6|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=3LY1kdnlj6bgwyZNgs$1gr",
        "ElementGroupType|-2000096|1|0|cfb3fc7c-8083-4b5b-bc95-e11893627538-000454a0|",
        "ExtrusionElem||0|0|96834e54-f091-4317-9b1a-bbe8b647201d-0007b931|",
        "FabricAreaType|-2009017|1|0|7e413dfb-48a3-4ccb-b102-c9e5a33964ae-0014a3d3|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=1_GJtxIADCox42oUMZBSTz",
        "FabricSheetType|-2009016|1|0|7e413dfb-48a3-4ccb-b102-c9e5a33964ae-0014a40f|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=1_GJtxIADCox42oUMZBS2X",
        "FabricWireType|-2009016|1|0|7e413dfb-48a3-4ccb-b102-c9e5a33964ae-0014a40e|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=1_GJtxIADCox42oUMZBS2W",
        "FaceRoof|-2000035|0|0|220ce3a9-48c8-48bf-ba85-6033844239db-0002c0f7|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=0Y3EEfICX8lxg5O3E4GFai",
        "Family||0|0|2d99307a-66cd-4439-b173-5c88d252416e-00002122|",
        "FamilyInstance|-2000014|0|0|6cbabf1d-e8d0-47f0-ac4d-9a7923128d37-0006fb07|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=1ikhyTwD17yAnDcdaZ57Om",
        "FamilyInstance|-2000023|0|0|59371552-5800-43eb-9ba3-609565158fc5-00067242|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=1PDnLIM013wvkZO9Lb4$s7",
        "FamilyInstance|-2000080|0|0|57f8477a-60e4-4504-911c-957c1b7fc5d4-000b39fb|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=1N_4TwOEH5194SbNmRTFml",
        "FamilyInstance|-2000100|0|0|446f5fcc-4b31-41b8-b3fe-f1c9ef48790d-000a8b6d|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=14Rr$CIp51kBF_ySdlGl9W",
        "FamilyInstance|-2000127|0|0|79010029-8aa9-4c91-b2c8-b7f94cb6df11-0008a547|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=1v0G0fYgbCaRB8j$bCldfM",
        "FamilyInstance|-2000150|0|0|0712468e-1b41-4ca5-b1a4-550147bf5c41-000e5f2a|",
        "FamilyInstance|-2000151|0|0|1ac40ecd-9b56-404a-8189-99248e0832e3-00079f28|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=0Qn0xDcrP0Ie69cIIE3wtB",
        "FamilyInstance|-2000170|0|0|59371552-5800-43eb-9ba3-609565158fc5-000674bc|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=1PDnLIM013wvkZO9Lb4$jv",
        "FamilyInstance|-2000171|0|0|59371552-5800-43eb-9ba3-609565158fc5-000674de|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=1PDnLIM013wvkZO9Lb4$iR",
        "FamilyInstance|-2000280|0|0|6d3acd40-53b7-41b4-9d96-72e9eaf4bc89-0005d69d|",
        "FamilyInstance|-2000515|0|0|16efa708-a2dc-4f6d-b27b-bc576567a2a9-00026ffc|",
        "FamilyInstance|-2001000|0|0|89b7d9e7-00a9-45d9-8498-e33a7de65df7-000a87a0|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=29jzdd0Ab5sOIOupfzxDfN",
        "FamilyInstance|-2001040|0|0|05305b4b-b8cf-4c6b-ab67-2f5e121c1c6f-000be04c|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=05C5jBkCzCQwjdBruI5$mZ",
        "FamilyInstance|-2001060|0|0|c59e7c0c-b35d-44aa-9a0b-0fe7965f2865-0006f34e|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=35ddmCirr4gfeB3_UMMTih",
        "FamilyInstance|-2001100|0|0|96834e54-f091-4317-9b1a-bbe8b647201d-0007b938|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=2MWqvKy9535viQk_YsG9ab",
        "FamilyInstance|-2001120|0|0|df146ad3-a574-4183-b3dd-a6f6589001b3-000b8dce|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=3V56hJfNH1WxFTflPOcunz",
        "FamilyInstance|-2001140|0|0|ac8b9fc6-24ff-4c3b-a4c6-035f009e396e-0005e3ef|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=2iYv$69FzCEwJ60ry0czg1",
        "FamilyInstance|-2001160|0|0|88b03b05-80d3-484f-8022-471f61604c8d-00033b8a|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=28i3i5WDD8Ju0YHnzXOtS7",
        "FamilyInstance|-2001180|0|0|6b5c139f-f43e-4550-9c53-dfaa00419961-00029a46|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=1hN1EVz3v5K9nJtwe0GmCd",
        "FamilyInstance|-2001260|0|0|d85573c2-f8d5-46ae-966a-ac82fa18f500-000664a1|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=3OLNF2_DL6hfPgh8Bw7f6X",
        "FamilyInstance|-2001300|0|0|656284ae-d84a-4c5c-ae16-d638fa151c7e-0007d115|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=1bOeIks4fCNAuMrZZw4irh",
        "FamilyInstance|-2001320|0|0|fd542353-5aed-4ee2-8baa-b1272d401016-000ff6fc|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=3zL2DJMkrEuekgiISjJ_Rg",
        "FamilyInstance|-2001330|0|0|ef4ad1cb-015c-4bb6-9030-e7bb82f1dd34-00065312|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=3lIj7B0LnBjf0mvxk2zuuc",
        "FamilyInstance|-2001350|0|0|89b7d9e7-00a9-45d9-8498-e33a7de65df7-000a573f|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=29jzdd0Ab5sOIOupfzx0h8",
        "FamilyInstance|-2001354|0|0|f19a9892-0b03-4cbd-a41f-a600e9f1df02-00021951|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=3ncfYI2mDClQGVfW3fyyPJ",
        "FamilyInstance|-2001360|0|0|ae8b4e75-e47d-473a-8255-a4634e701f79-000e6ff4|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=2kYqvrv7r7Ee9Lf6DEVd2D",
        "FamilyInstance|-2001370|0|0|ae8b4e75-e47d-473a-8255-a4634e701f79-000e805f|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=2kYqvrv7r7Ee9Lf6DEVfyc",
        "FamilyInstance|-2002000|0|0|dda0c528-df04-4b54-b52b-7838a5b0f937-000e2d73|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=3TeCKetmHBLBKhU3YbljH4",
        "FamilyInstance|-2003400|0|0|5b7ac240-e9a7-43b2-b6bf-246956549a40-00099687|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=1RUi90wQT3ihQ$96bMNGp7",
        "FamilyInstance|-2005100|0|0|16efa708-a2dc-4f6d-b27b-bc576567a2a9-00026ff6|",
        "FamilyInstance|-2005110|0|0|8e0e6e3d-8805-4c98-9f77-4721d05f0325-00020d8c|",
        "FamilyInstance|-2005130|0|0|9740b1f6-41ac-4e59-9332-6cfe47e333cb-00027520|",
        "FamilyInstance|-2005301|0|0|690ede92-80a7-44e5-b9af-b4341b92c065-00156f45|",
        "FamilyInstance|-2006040|0|0|16efa708-a2dc-4f6d-b27b-bc576567a2a9-00026ff2|",
        "FamilyInstance|-2008010|0|0|7485c179-9364-48d7-b68b-6736718012c4-0005fc0b|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=1qXS5vasH8rxQBPpPnXUxF",
        "FamilyInstance|-2008013|0|0|3e918856-4041-4d31-ba0b-7a3a3b8705b8-0005ca47|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=0_aOXMG45DCReBUZexWi$$",
        "FamilyInstance|-2008049|0|0|810e919f-3214-4598-bcad-93bb149143c0-0007d73e|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=213f6VCXH5cBojaxiKbfJ_",
        "FamilyInstance|-2008087|0|0|9bb81276-09be-4795-9e22-aa1e20329f1a-0006af0a|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=2Rk19s2Rv7bPuYgXuWD30G",
        "FamilyInstance|-2008099|0|0|7a2419bd-e042-4b38-8b95-781bc33e7dd8-00085389|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=1w91czu49BE8kLU1l3DYvH",
        "FamilyInstance|-2008126|0|0|398c98ed-7bc4-4476-873a-54e2ad032794-000c2bd1|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=0vZ9ZjUyH4TeSwLEAj3mn5",
        "FamilyInstance|-2008128|0|0|b09dffb9-3b4b-4377-9202-9dca3441933d-000a5d06|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=2mdV_vEqj3Tv82dSeqIyux",
        "FamilyInstance|-2008165|0|0|e798f04a-6454-447f-affa-043065a9b697-0011ae53|",
        "FamilyInstance|-2009010|0|0|f2c40aea-6bff-480d-a190-20fabac067bf-0002635d|",
        "FamilyInstance|-2009020|0|0|16efa708-a2dc-4f6d-b27b-bc576567a2a9-00026fee|",
        "FamilyInstance|-2009600|0|0|0250fc8b-5c5b-4955-a47f-61f37dcc1f7f-000205f5|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=02KFoBN5j9LQH$OVDzpXgA",
        "FamilySymbol|-2000014|1|0|ef57b02a-5e81-49e7-93bb-ae5f002d921c-000306de|Export Type to IFC=0;Export Type to IFC As=;Operation=SinglePanel;Type IFC Predefined Type=;Type IfcGUID=3lLx0gNe59vvExhby0BfJ2",
        "FamilySymbol|-2000023|1|0|88b03b05-80d3-484f-8022-471f61604c8d-00038d4c|Export Type to IFC=0;Export Type to IFC As=;Operation=;Type IFC Predefined Type=;Type IfcGUID=28i3i5WDD8Ju0YHnzXOy71",
        "FamilySymbol|-2000080|1|0|57f8477a-60e4-4504-911c-957c1b7fc5d4-000b39ec|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=1N_4TwOEH5194SbNmRTFmu",
        "FamilySymbol|-2000100|1|0|61d2b0c3-90b6-45e5-b190-401d5dc6639b-0001382e|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=1Xqh33aBP5vR6GG1rTnrkr",
        "FamilySymbol|-2000127|1|0|6c4c5a1e-a051-47b9-b462-2ab655b01df8-00003038|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=1iJ5eUe557kRHYAhPLi2t0",
        "FamilySymbol|-2000150|1|0|0712468e-1b41-4ca5-b1a4-550147bf5c41-000e5f28|",
        "FamilySymbol|-2000151|1|0|32c6aad3-999a-4b41-9ecd-2451f2cb35d0-000461a5|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=0onghJcPfBGPxD957oprHr",
        "FamilySymbol|-2000197|1|0|5beb3c18-5531-4220-bce0-15a136c3cf67-00018742|",
        "FamilySymbol|-2000280|1|0|6d3acd40-53b7-41b4-9d96-72e9eaf4bc89-0005d697|",
        "FamilySymbol|-2000400|1|0|458c1092-01bb-11d5-9302-0000863f27ad-00000419|",
        "FamilySymbol|-2000450|1|0|e6ac360b-aaed-4c3b-a130-36b4c2ac9d13-000d1bce|",
        "FamilySymbol|-2000460|1|0|b9aeca9b-45cb-4d6c-9f11-c9a50b11f25e-000d2abe|",
        "FamilySymbol|-2000480|1|0|e6ac360b-aaed-4c3b-a130-36b4c2ac9d13-000d1460|",
        "FamilySymbol|-2000485|1|0|5bb7160b-fcee-4dca-942f-3ce8c7b571d6-00055d91|",
        "FamilySymbol|-2000515|1|0|fca46435-22e5-11d5-932d-0000863f27ad-00001049|",
        "FamilySymbol|-2000538|1|0|458c1092-01bb-11d5-9302-0000863f27ad-00000475|",
        "FamilySymbol|-2000948|1|0|9c7bf9db-3143-4fa2-9339-88d4d04d233d-00023e78|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=2SU$dRCKDFefCvYDJGJnr5",
        "FamilySymbol|-2001000|1|0|89b7d9e7-00a9-45d9-8498-e33a7de65df7-000a877f|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=29jzdd0Ab5sOIOupfzxDg8",
        "FamilySymbol|-2001040|1|0|05305b4b-b8cf-4c6b-ab67-2f5e121c1c6f-000bda87|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=05C5jBkCzCQwjdBruI5yRe",
        "FamilySymbol|-2001060|1|0|81291fbb-89d5-4984-8ef5-67d0820cf8b0-00053a7c|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=21AH_xYTL9X8xrPz222SBC",
        "FamilySymbol|-2001100|1|0|96834e54-f091-4317-9b1a-bbe8b647201d-0007b936|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=2MWqvKy9535viQk_YsG9ah",
        "FamilySymbol|-2001120|1|0|e74fbea1-1e07-45a5-b4f5-538a6110ed43-000b8160|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=3dJxwX7WT5fRJrKufX6smZ",
        "FamilySymbol|-2001140|1|0|d2c76d0a-5774-42dd-8a54-b4c401dc3bd1-0005d4f8|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=3InsqALtH2tOfKjCG1sUyf",
        "FamilySymbol|-2001160|1|0|88b03b05-80d3-484f-8022-471f61604c8d-00033b5f|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=28i3i5WDD8Ju0YHnzXOtVI",
        "FamilySymbol|-2001180|1|0|1d56ea0b-adc2-449e-8844-19bceb15a369-00009087|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=0TLkeBhS94deX46Rph5JFk",
        "FamilySymbol|-2001260|1|0|73f93e8c-79ed-4339-b869-a6f220073e64-00061051|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=1p_JwCUUr3ERXffl8W0Iur",
        "FamilySymbol|-2001269|1|0|ae8b4e75-e47d-473a-8255-a4634e701f79-000e7968|",
        "FamilySymbol|-2001300|1|0|656284ae-d84a-4c5c-ae16-d638fa151c7e-0007c61c|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=1bOeIks4fCNAuMrZZw4jfY",
        "FamilySymbol|-2001320|1|0|cb4373ed-72c2-4885-8dda-08d3bf962cc6-000f0584|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=3BGtFjSi98XOtQ2DE$cIb2",
        "FamilySymbol|-2001330|1|0|81595c38-a4c2-4a22-9ddf-5884d5e7382c-00066c58|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=21MLmufC9A8ftVM8JLuLHq",
        "FamilySymbol|-2001350|1|0|89b7d9e7-00a9-45d9-8498-e33a7de65df7-000a5730|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=29jzdd0Ab5sOIOupfzx0h7",
        "FamilySymbol|-2001354|1|0|1a8044ef-536b-4599-a976-30f4bd2a9d04-00017e7c|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=0QW4JlKsj5cQbsCFIzA_Du",
        "FamilySymbol|-2001360|1|0|ae8b4e75-e47d-473a-8255-a4634e701f79-000e6f46|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=2kYqvrv7r7Ee9Lf6DEVd0$",
        "FamilySymbol|-2001370|1|0|ae8b4e75-e47d-473a-8255-a4634e701f79-000e7fc8|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=2kYqvrv7r7Ee9Lf6DEVc2n",
        "FamilySymbol|-2002000|1|0|009de6df-1eda-42c3-b66f-c5cf297bce54-0000726c|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=00dURV7jf2mxPlnSyfUxmu",
        "FamilySymbol|-2003000|1|0|8ea7ba6d-b5c0-4453-9a12-cbe742c08a48-00003f58|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=2EfxfjjS14KveIo_T2mBKG",
        "FamilySymbol|-2003400|1|0|5b7ac240-e9a7-43b2-b6bf-246956549a40-00099685|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=1RUi90wQT3ihQ$96bMNGp5",
        "FamilySymbol|-2005003|1|0|0276923c-1fc4-450f-9692-84e2ccd96831-0005671a|",
        "FamilySymbol|-2005008|1|0|0276923c-1fc4-450f-9692-84e2ccd96831-00056a44|",
        "FamilySymbol|-2005009|1|0|182f1293-f68d-4188-890f-a7db109b26cc-000771dd|",
        "FamilySymbol|-2005011|1|0|574c8bc5-51c8-4ae8-8e82-5d772d201b96-0000f4a2|",
        "FamilySymbol|-2005015|1|0|f5851179-3be7-4ffe-8290-9415f117b789-001315f3|",
        "FamilySymbol|-2005018|1|0|7cbef8e2-f722-4452-a716-0d60bc8c457e-00131eb2|",
        "FamilySymbol|-2005019|1|0|7cbef8e2-f722-4452-a716-0d60bc8c457e-001320c8|",
        "FamilySymbol|-2005020|1|0|b8db3a7b-b912-488e-8ced-09a4bd07373c-00006fb1|",
        "FamilySymbol|-2005021|1|0|666da4f5-76ba-443a-9fe6-0bcd8a1bcfe9-000ea39a|",
        "FamilySymbol|-2005027|1|0|283f8c61-4358-484e-95ca-2fb85551d793-00013ecb|",
        "FamilySymbol|-2005029|1|0|283f8c61-4358-484e-95ca-2fb85551d793-00013e02|",
        "FamilySymbol|-2005030|1|0|3f9ac6b9-0c8d-4ac3-bfda-65c586467c31-00018476|",
        "FamilySymbol|-2005100|1|0|c881ef12-8bcc-40e0-b9a7-66b44a62b6b0-00005cba|",
        "FamilySymbol|-2005110|1|0|76eb092d-eb82-4ff3-83fb-38f20e897b08-00012eb1|",
        "FamilySymbol|-2005130|1|0|7aa5a0cb-12cc-49f9-bdaf-33e09c8f657c-00016e6e|",
        "FamilySymbol|-2005301|1|0|bbb6db12-1332-4ead-a452-269d036c3831-00153567|",
        "FamilySymbol|-2006020|1|0|78f7f91b-ffb7-49fd-8414-2bd250ee70c9-00006123|",
        "FamilySymbol|-2006040|1|0|78f7f91b-ffb7-49fd-8414-2bd250ee70c9-00006008|",
        "FamilySymbol|-2006045|1|0|7a60b0e9-ccff-4a92-bcce-eafa6d93788e-0001e864|",
        "FamilySymbol|-2006080|1|0|6b9ebba5-9f22-4b69-a307-692ef254b436-000083da|",
        "FamilySymbol|-2006100|1|0|ef2bc9d1-38fc-4de5-835b-1c7881ef16a1-0000968b|",
        "FamilySymbol|-2006110|1|0|ef2bc9d1-38fc-4de5-835b-1c7881ef16a1-00009741|",
        "FamilySymbol|-2008003|1|0|f72ed939-20fb-4570-b23e-88f5281ad539-0001916b|",
        "FamilySymbol|-2008004|1|0|a45477e2-e0f7-4508-9229-3dd7d76a9d94-00094574|",
        "FamilySymbol|-2008010|1|0|f72ed939-20fb-4570-b23e-88f5281ad539-0001a080|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=3tBjav8Fj5SB8_YFKe6tMv",
        "FamilySymbol|-2008013|1|0|492284f7-77a2-4384-aaae-8938e122583e-0004820d|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=198eJtTw93XAgkYJZX9jep",
        "FamilySymbol|-2008014|1|0|f72ed939-20fb-4570-b23e-88f5281ad539-000190b9|",
        "FamilySymbol|-2008047|1|0|b3297919-72d8-462a-8539-06f1607cea7e-000946cd|",
        "FamilySymbol|-2008049|1|0|f72ed939-20fb-4570-b23e-88f5281ad539-0001fdf6|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=3tBjav8Fj5SB8_YFKe6oZF",
        "FamilySymbol|-2008057|1|0|0276923c-1fc4-450f-9692-84e2ccd96831-00056404|",
        "FamilySymbol|-2008087|1|0|81291fbb-89d5-4984-8ef5-67d0820cf8b0-00053e48|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=21AH_xYTL9X8xrPz222SRu",
        "FamilySymbol|-2008099|1|0|07536fff-8fa0-457d-a739-41c87923811b-00052743|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=07Ks$$Zw15VQSvGSXv9gPO",
        "FamilySymbol|-2008115|1|0|5bb7160b-fcee-4dca-942f-3ce8c7b571d6-00055e69|",
        "FamilySymbol|-2008126|1|0|d8ad27e3-c00e-4582-9797-ab32080a9350-0009d332|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=3OhIVZm0v5WfUNgp880q1Y",
        "FamilySymbol|-2008128|1|0|d8ad27e3-c00e-4582-9797-ab32080a9350-000a1bbb|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=3OhIVZm0v5WfUNgp8808Zh",
        "FamilySymbol|-2008165|1|0|e798f04a-6454-447f-affa-043065a9b697-0011ae51|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=3dcF1AP5H4Vw$w131bk1Z6",
        "FamilySymbol|-2009005|1|0|220167d7-78e1-460e-92d8-cafa8d85e27d-00013d15|",
        "FamilySymbol|-2009010|1|0|9cfefc2a-7d63-49a9-a59b-983287e0b7a9-00016d86|",
        "FamilySymbol|-2009011|1|0|95e5a8a8-0a42-49df-8061-6399521d40f1-00014452|",
        "FamilySymbol|-2009013|1|0|61f6dfee-c37b-4c36-aa41-036e93aac794-00018b8b|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=1Xzj$kmtjCDgf10swJgqmV",
        "FamilySymbol|-2009020|1|0|7aa5a0cb-12cc-49f9-bdaf-33e09c8f657c-00016f5f|",
        "FamilySymbol|-2009021|1|0|7845e5ab-9e97-4b6f-bd9a-b17a5ee934b8-0001313b|",
        "FamilySymbol|-2009600|1|0|f0aa6723-43fb-42b4-872d-d27158c532e6-000170c4|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=3mgcSZG$j2j8Sjqd5On48Y",
        "FasciaAttr|-2001390|1|0|e8e255a3-8f4e-49b9-8324-0178422c4a87-00004dad|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=3eubMZZqv9kOCa0NX2B0Sg",
        "FilledRegion|-2002000|0|0|d32651fd-8177-472a-a10a-14863a6ba772-001526c9|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=3J9b7zWNT7Ag4A58OwVe6x",
        "FilledRegionAttributes||1|0|23afd781-9f1e-4b22-8309-8c5afbe244cf-0002437a|",
        "FilledRegionAttributes|-2002000|1|0|fea3c4bf-e801-4aeb-a788-a17fbcee79a5-0001507f|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=3_eyI$w05AwwU8eN_yxodQ",
        "FlexCenterLine|-2008021|0|0|0653a761-f461-47be-a144-01ab5270db1a-000664e2|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=06KwTXz657lg540QjITh$u",
        "Floor|-2000032|0|0|203cff26-4c9d-4eb4-93e5-28d28a01f256-0002b2a4|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=0WFFycJ9rEj9FbADAA0q3o",
        "Floor|-2001300|0|0|c82e0dd9-31e8-407e-a294-c30431c2fda3-000bb286|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=38BWtPCUX0VgAKmmGnoKyb",
        "FloorAttributes|-2000032|1|0|e3e052f9-0156-11d5-9301-0000863f27ad-00000153|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=3Zu5Bv0LOHrPC10026FoR_",
        "FloorAttributes|-2001300|1|0|5adccf95-7ef8-4f40-aa7c-dd3dcfd6d307-00015129|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=1QtC_LVlXFGAfytJtFru8k",
        "GStyleElem||0|0|e3e052f9-0156-11d5-9301-0000863f27ad-0000001e|",
        "Grid|-2000220|0|0|ef57b02a-5e81-49e7-93bb-ae5f002d921c-0002fa61|Export to IFC=0;IfcGUID=3lLx0gNe59vvExhby0BsXz",
        "GridAttributes|-2000220|1|0|e3e052f9-0156-11d5-9301-0000863f27ad-00000155|Export Type to IFC=0;Type IfcGUID=3Zu5Bv0LOHrPC10026FoRu",
        "GutterAttr|-2001391|1|0|e8e255a3-8f4e-49b9-8324-0178422c4a87-00004dae|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=3eubMZZqv9kOCa0NX2B0Sf",
        "HalftoneUnderlaySettings||0|0|ff5057b3-20f2-4623-9feb-99c9a41823e7-00019d33|",
        "HandRailType|-2000947|1|0|9020f828-ce55-4f5d-9922-27877ae62719-00021fc2|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=2G8FWepbLFNPaY9uTwv3ZR",
        "ImportInstance|1382678|0|0|050679c1-d082-430a-963e-ea92a3b99fc0-0015195a|",
        "IndependentTag|-2005008|0|0|aadb9a02-a6e5-427b-ba35-4af4460fddf4-0007b535|",
        "IndependentTag|-2008003|0|0|15a730d3-2ef7-44cd-be1e-2653779bddb6-00075d0a|",
        "InteriorElevArrow|-2000278|0|0|f9c62406-7c50-11d5-93b8-0000863f27ad-00001fd8|",
        "JoistSystem|-2001327|0|0|d88be54c-45e2-4c41-91ae-e8312a0a09df-0011f0f9|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=3OY_LCHU9CGP6kw34g6$ac",
        "JoistSystemAttr|-2001327|1|0|281263d6-c0be-4b93-a3e6-255e17a505c0-0000bf82|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=0e4cFMmBvBawFc9LuNfRf2",
        "LegendComponent|-2000575|0|0|30a3bbb8-9fd7-4526-8fb5-b921e7dd1c83-00094b55|",
        "Level|-2000240|0|0|e3e052f9-0156-11d5-9301-0000863f27ad-00000137|Export to IFC=0;IfcGUID=3Zu5Bv0LOHrPC10026FoQQ",
        "LevelAttributes|-2000240|1|0|e3e052f9-0156-11d5-9301-0000863f27ad-00000131|Export Type to IFC=0;Type IfcGUID=3Zu5Bv0LOHrPC10026FoQS",
        "LinePatternElem||0|0|e3e052f9-0156-11d5-9301-0000863f27ad-0000000a|",
        "MEPComponentTracker||0|0|5de47e85-f1ce-4dd7-9a1a-e25cc251c808-000ee04c|",
        "MassLevelData|-2003403|0|0|66d48c91-c014-4544-9855-4b7228f34fb8-000d6030|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=1cr8oHm1H5H9XLIt8e$Y_8",
        "MasterImportSymbol|1382768|1|0|050679c1-d082-430a-963e-ea92a3b99fc0-0015196f|",
        "MaterialElem|-2000700|0|0|e3e052f9-0156-11d5-9301-0000863f27ad-00000017|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=3Zu5Bv0LOHrPC10026FoUw",
        "MultipleValuesIndicationSettings||0|0|db45d36d-7001-406a-ad65-377c2aacfb9d-0010d335|",
        "MultistoryStairs|-2000980|0|0|90ca1a0f-0050-420c-86e3-1efbab1aba65-000568bf|",
        "NestedFamilyTypeReference|-2000023|0|0|e1d4bdfb-2688-47c1-a386-3b48116e5fd2-00087c32|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=3XrBtx9eX7mQE6EqWHPYFW",
        "NestedFamilyTypeReference|-2000150|0|0|2370dbd3-341a-46f4-a944-43c0c5fcd9c3-000287b6|",
        "NewCurtainWallType|-2000011|1|0|6cbabf1d-e8d0-47f0-ac4d-9a7923128d37-000700af|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=1ikhyTwD17yAnDcdaZ5OsO",
        "PanelScheduleTemplate|-2008147|0|0|3fd56013-b5d2-4097-9cfb-42ee8d6381e5-0001c573|",
        "ParameterFilterElement||0|0|3fd56013-b5d2-4097-9cfb-42ee8d6381e5-0001c583|",
        "PathReinforcement|-2009009|0|0|f2c40aea-6bff-480d-a190-20fabac067bf-00026273|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=3on0hgQ$z83Q6G8FgwmWNC",
        "PathReinforcementType|-2009009|1|0|ffcb60fe-c632-482c-a273-70f7215d1f65-0002d1b1|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=3$os3_nZ98BA9pSFSXNyxK",
        "PipeFittingCenterLine|-2008072|0|0|810e919f-3214-4598-bcad-93bb149143c0-0007d73e|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=213f6VCXH5cBojaxiKbfJ_",
        "PointLoadType|-2005200|1|0|f7d54af9-82bd-43de-9210-0e8af02555e6-00009989|",
        "ProfileRoof|-2000035|0|0|1551350c-45e5-41b8-bdf1-bcfa6794dc6a-000b0dce|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=0LKJKCHUL1kBtnlFfddz6a",
        "Property|-2001265|0|0|ae8b4e75-e47d-473a-8255-a4634e701f79-000e752a|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=2kYqvrv7r7Ee9Lf6DEVcfJ",
        "PropertyAttr|-2001265|1|0|c3f5348f-6947-4ddf-aa1e-749882f86acc-00005cbe|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=33zJIFQKTDtweUT9Y2_3Po",
        "PropertyLineSegment|-2001268|0|0|ae8b4e75-e47d-473a-8255-a4634e701f79-000e776d|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=2kYqvrv7r7Ee9Lf6DEVcWK",
        "PropertySetElement|-2000924|0|0|88566e0d-6daf-48ce-b050-ac0348546712-0002a98b|",
        "RampAttributes|-2000180|1|0|e3e052f9-0156-11d5-9301-0000863f27ad-0000015e|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=3Zu5Bv0LOHrPC10026FoRp",
        "RbsCableTrayType|-2008130|1|0|5de47e85-f1ce-4dd7-9a1a-e25cc251c808-000ee056|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=1Tv7w5ySvDrveQubp2NoXU",
        "RbsConduitCurve|-2008132|0|0|b09dffb9-3b4b-4377-9202-9dca3441933d-000a5b03|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=2mdV_vEqj3Tv82dSeqIyW_",
        "RbsConduitType|-2008132|1|0|5de47e85-f1ce-4dd7-9a1a-e25cc251c808-000ee05a|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=1Tv7w5ySvDrveQubp2NoXI",
        "RbsDuctCurve|-2008000|0|0|7485c179-9364-48d7-b68b-6736718012c4-0005fbe8|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=1qXS5vasH8rxQBPpPnXUai",
        "RbsDuctInsulationType|-2008123|1|0|4abf196c-8bf8-4620-8543-2d548bc9244f-000ae14e|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=1AlnbiY$X688L3BLIBmyK1",
        "RbsDuctLiningType|-2008124|1|0|4abf196c-8bf8-4620-8543-2d548bc9244f-000ae14f|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=1AlnbiY$X688L3BLIBmyK0",
        "RbsDuctSizesElem||0|0|3eac4dbf-eafd-4417-af61-3e64b2f4fe60-00018eee|",
        "RbsElectricalSystem|-2008037|0|0|f9b0887c-fa64-4a05-bfea-7b28c0d4a171-000a9409|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=3vi8Xy_cHA1R$gUoZ0tZLu",
        "RbsFlexDuctCurve|-2008020|0|0|0653a761-f461-47be-a144-01ab5270db1a-000664e2|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=06KwTXz657lg540QjITh$u",
        "RbsFlexPipeType|-2008050|1|0|5de47e85-f1ce-4dd7-9a1a-e25cc251c808-000ee055|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=1Tv7w5ySvDrveQubp2NoXT",
        "RbsHvacSystem|-2008015|0|0|aa0f3eb4-4df4-4268-8257-eac715dfb56a-0005e94b|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=2g3pwqJVH2Q89NwiSLsbmX",
        "RbsPipeCurve|-2008044|0|0|810e919f-3214-4598-bcad-93bb149143c0-0007d6dc|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=213f6VCXH5cBojaxiKbfKS",
        "RbsPipeInsulationType|-2008122|1|0|4abf196c-8bf8-4620-8543-2d548bc9244f-000ae150|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=1AlnbiY$X688L3BLIBmyKV",
        "RbsPipeType|-2008044|1|0|5de47e85-f1ce-4dd7-9a1a-e25cc251c808-000ee054|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=1Tv7w5ySvDrveQubp2NoXS",
        "RbsPipingSystem|-2008043|0|0|7a2419bd-e042-4b38-8b95-781bc33e7dd8-00084b6f|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=1w91czu49BE8kLU1l3DZQt",
        "RbsPipingSystemType|-2008043|1|0|2e2218ce-7ae8-4c53-826a-5bf15e536708-00020577|",
        "RbsWireCurve|-2008039|0|0|6e42bd50-0930-42ab-bddf-bbfadffffe5e-000729d9|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=1kGhrG2J12gxtVk$hV_DU7",
        "RbsWireType|-2008039|1|0|eb0726c0-8df6-4f21-bf23-9b9f8b698fae-0000d783|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=3h1oR0ZVPF8RyZcv_BQLWj",
        "Rebar|-2009000|0|0|c159dbca-f117-4601-9d11-c73e835f2ff5-0014f085|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=31MTlAyHT60PqHnpw3Izzm",
        "RebarBarType|-2009000|1|0|73c78223-6f1d-4d62-bc0f-5ca1616bc7a3-00141628|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=1pnu8ZRnrDOhmFNA5XVz6B",
        "RebarBendingDetailType|-2001104|0|0|1f01220e-0b15-402f-8a4f-d8565cc41524-0010d4b9|",
        "RebarInSystem|-2009000|0|0|73c78223-6f1d-4d62-bc0f-5ca1616bc7a3-001416d7|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=1pnu8ZRnrDOhmFNA5XVz5q",
        "RebarShape|-2009000|1|0|c159dbca-f117-4601-9d11-c73e835f2ff5-0014f084|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=31MTlAyHT60PqHnpw3Izzn",
        "RebarSystem|-2009003|0|0|73c78223-6f1d-4d62-bc0f-5ca1616bc7a3-0014162d|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=1pnu8ZRnrDOhmFNA5XVz6E",
        "ReferenceViewerAttributes||1|0|d227b3d4-3a43-463c-bd6e-c15a03ddc915-00018646|",
        "RepeatingDetailAttributes|-2002000|1|0|009de6df-1eda-42c3-b66f-c5cf297bce54-000071b5|",
        "Roof|-2000035|0|0|a6aa132d-ccd7-408f-b2f9-ed67350c8c3a-0003b64a|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=2cgXCjpDT0ZxBvxMSr3pfm",
        "RoofAttributes|-2000035|1|0|96834e54-f091-4317-9b1a-bbe8b647201d-0007bc90|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=2MWqvKy9535viQk_YsG9oD",
        "RoofSoffitType|-2001393|1|0|e8e255a3-8f4e-49b9-8324-0178422c4a87-00004da6|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=3eubMZZqv9kOCa0NX2B0SX",
        "RoomElem|-2000160|0|0|e6ac360b-aaed-4c3b-a130-36b4c2ac9d13-000d1467|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=3ch3OBgkrCEw4mDhJ2eObq",
        "RoomElem|-2003200|0|0|3a835854-c2f4-49c8-9d38-f2549c828766-000f556e|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=0wWrXKmlH9o9quybISZT88",
        "RoomElem|-2003600|0|0|fd446580-7018-41fe-8535-07e069840ced-000e5f49|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=3zH6M0S1X1$eKr1_1fYbEa",
        "RoomTag|-2000480|0|0|e6ac360b-aaed-4c3b-a130-36b4c2ac9d13-000d1468|",
        "RvtLinkInstance|-2001352|0|0|fc5f6326-6e41-4987-a437-f8bed52d8393-001312ee|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=3yNsCcRa59XwGt_BxLFf5z",
        "RvtLinkSymbol|-2001352|1|0|fc5f6326-6e41-4987-a437-f8bed52d8393-001312ed|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=3yNsCcRa59XwGt_BxLFf5_",
        "SWall|-2000011|0|0|ef57b02a-5e81-49e7-93bb-ae5f002d921c-00030826|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=3lLx0gNe59vvExhby0Bfew",
        "ScheduleInstance|-2000570|0|0|666da4f5-76ba-443a-9fe6-0bcd8a1bcfe9-000ea409|",
        "SegmentCenterLine|-2008001|0|0|a4d2a650-712c-4311-9076-3ed139255683-00082f9c|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=2aqgPGSIn34P1sFj4vBNaV",
        "SegmentCenterLine|-2008045|0|0|810e919f-3214-4598-bcad-93bb149143c0-0007d6dc|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=213f6VCXH5cBojaxiKbfKS",
        "SegmentCenterLine|-2008139|0|0|b09dffb9-3b4b-4377-9202-9dca3441933d-000a5b03|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=2mdV_vEqj3Tv82dSeqIyW_",
        "SiteSurface|-2001340|0|0|ef4ad1cb-015c-4bb6-9030-e7bb82f1dd34-0006473c|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=3lIj7B0LnBjf0mvxk2zve8",
        "SketchPlane||0|0|54cd0060-067b-11d5-9309-0000863f27ad-00000cfd|",
        "StackedWallType|-2003500|1|0|4d24d0d6-38f1-47b8-a376-b40c6432dad5-0000d509|",
        "Stairs|-2000120|0|0|0425ae0b-e770-4cac-a0ff-77ecabcd8fcb-00034410|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=049QuBvt1ChA3$T_ohpilR",
        "StairsAttributes|-2000120|1|0|c9b18df7-1ca6-432a-95d4-4f51a23b84f0-00006fb6|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=39iOtt7AP3AfNKJr6YE_j6",
        "StairsElement|-2000120|0|0|079d150e-b61b-46bb-a991-2bbcfc2d317a-0007d4e6|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=07dHKEjXj6kwcHAxpyAkMS",
        "StairsLanding|-2000920|0|0|57f8477a-60e4-4504-911c-957c1b7fc5d4-000b1194|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=1N_4TwOEH5194SbNmRTDH0",
        "StairsLandingType|-2000920|1|0|9020f828-ce55-4f5d-9922-27877ae62719-000229a3|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=2G8FWepbLFNPaY9uTwv0ww",
        "StairsRailingAttr|-2000126|1|0|4269d745-d78a-4cca-860f-a55b170715ad-00077a91|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=12QTT5rufCoeOFfLiN06yy",
        "StairsRun|-2000919|0|0|079d150e-b61b-46bb-a991-2bbcfc2d317a-0007d51d|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=07dHKEjXj6kwcHAxpyAkHd",
        "StairsRunType|-2000919|1|0|9020f828-ce55-4f5d-9922-27877ae62719-000229a2|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=2G8FWepbLFNPaY9uTwv0wx",
        "StairsSupport|-2000123|0|0|bbb6db12-1332-4ead-a452-269d036c3831-00156dd6|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=2xjjiI4p9EhQHI9fq3ULNd",
        "StairsSupportType|-2000123|1|0|9020f828-ce55-4f5d-9922-27877ae62719-000229a4|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=2G8FWepbLFNPaY9uTwv0wz",
        "StairsType|-2000120|1|0|9020f828-ce55-4f5d-9922-27877ae62719-000229a6|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=2G8FWepbLFNPaY9uTwv0w$",
        "SunAndShadowSettings|-2009609|0|0|b8feb22d-9c38-47b2-9356-5f4b0ef0758a-0000d4f8|",
        "SweepHost|-2001392|0|0|fe14d674-c577-4a22-a48c-b6832dafbea7-00042a0a|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=3_5DPqnNTA8gICjeCjgvIj",
        "SysMullionFamSym|-2000171|1|0|88b03b05-80d3-484f-8022-471f61604c8d-00038d42|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=28i3i5WDD8Ju0YHnzXOy7F",
        "SysPanelFamSym|-2000170|1|0|4d24d0d6-38f1-47b8-a376-b40c6432dad5-0000d50b|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=1D9D3MEF57kADsj0naCW$U",
        "Text3dAttrSymbol|-2000151|1|0|2d99307a-66cd-4439-b173-5c88d252416e-00001ea8|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=0jcJ1wPir4ER5pN8ZIKb$6",
        "Text3dElem|-2000151|0|0|e1d4bdfb-2688-47c1-a386-3b48116e5fd2-0008d11d|",
        "TopRail|-2000946|0|0|79010029-8aa9-4c91-b2c8-b7f94cb6df11-0008a548|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=1v0G0fYgbCaRB8j$bCldfP",
        "TopRailType|-2000946|1|0|9020f828-ce55-4f5d-9922-27877ae62719-000228ec|Export Type to IFC=0;Export Type to IFC As=;Type IFC Predefined Type=;Type IfcGUID=2G8FWepbLFNPaY9uTwv0$r",
        "ToposolidType|-2001079|0|0|1f01220e-0b15-402f-8a4f-d8565cc41524-0010d4b5|",
        "TrussElement|-2001336|0|0|0250fc8b-5c5b-4955-a47f-61f37dcc1f7f-000205f6|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=02KFoBN5j9LQH$OVDzpXg9",
        "Viewport|-2000510|0|0|c3f5348f-6947-4ddf-aa1e-749882f86acc-000000e8|",
        "WallSweep|-2000181|0|0|a91680c6-bf25-4080-ad59-4d01c0ab905c-0003bf72|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=2f5e36loL0WArPJG70g2yk",
        "WallSweep|-2000182|0|0|a91680c6-bf25-4080-ad59-4d01c0ab905c-0003c08c|",
        "ZoneElement|-2008107|0|0|de0d18ae-d6da-4e8e-8196-33bb609646bc-0001903e|Export to IFC=0;Export to IFC As=;IFC Predefined Type=;IfcGUID=3U3HYkrjfEZe6MCxjWbzQ2",
    ];
}
