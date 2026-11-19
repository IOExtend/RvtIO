using System.Reflection;
using OpenMcdf;
using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public sealed class Revit2027ApiValuesTests
{
    private static ParsingState Wide2027()
    {
        string path = TestFiles.Scratch($"schema-api-values-{Guid.NewGuid():N}.rvt");
        try
        {
            SchemaReleaseTests.WriteSyntheticFile(path, wide: true, marker: 951, saved: 2027);
            using var root = RootStorage.OpenRead(path);
            var schema = FileSchema.Read(root)!;
            Assert.Equal(2027, schema.Release);
            return new ParsingState { WideSchema = schema };
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false, 0, -1)]
    [InlineData(true, 0, 0)]
    [InlineData(true, 55, 55)]
    [InlineData(null, 0, 0)]
    public void WorksetMembershipAndParameterHaveSeparateValues(bool? workshared, int stored, int shown)
    {
        var state = Wide2027();
        var element = new Element { Id = 10 };
        state.ElemWorksets[10] = (uint)stored;
        var info = new RevitFileInfo { Elements = [element], SerializedIsWorkshared = workshared };

        typeof(ElementBuilder).GetMethod("ApplyWorksets", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [info, state]);

        Assert.Equal(stored, element.WorksetId);
        var parameter = Assert.Single(element.Parameters);
        Assert.Equal(-1002053, parameter.Id);
        Assert.Equal(StorageType.Integer, parameter.StorageType);
        Assert.Equal(shown, parameter.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PropertySetOnlyHasWorksetMembershipInLocal2027Files(bool workshared)
    {
        var state = Wide2027();
        state.ElemWorksets[10] = 0;
        var element = new Element { Id = 10, NativeRootClassName = "PropertySetElement" };
        element.Parameters.Add(BipHelpers.MakeBipParam(-1002053, "ELEM_PARTITION_PARAM", StorageType.Integer, 0));
        var info = new RevitFileInfo { Elements = [element], SerializedIsWorkshared = workshared };

        typeof(ElementBuilder).GetMethod("ApplyWorksets", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [info, state]);

        Assert.Equal(0, element.WorksetId);
        Assert.Equal(workshared, element.Parameters.Any(p => p.Id == -1002053));
    }

    [Theory]
    [InlineData(false, -2000485)]
    [InlineData(true, -2000480)]
    public void KnownWideHeaderCategoryTakesPrecedenceOverTheLegacyClassDefault(bool legacy, int expected)
    {
        var state = Wide2027();
        state.R22View = legacy;
        state.ElemClassIdx[10] = (ushort)TfClassNames.IdOf("RoomTag");
        var element = new Element { Id = 10, NativeHeaderCategoryId = -2000485, CategoryId = -2000485 };
        var info = new RevitFileInfo { Elements = [element] };

        typeof(ElementBuilder).GetMethod("FixCategoryFromClassIdx", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [info, state]);

        Assert.Equal(expected, element.CategoryId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExactViewAndCenterLineLinksReadValuesFromTheirOwners(bool exact)
    {
        var state = Wide2027();
        var viewType = ElementRoot(state, 1, "DBViewType");
        var view = ElementRoot(state, 2, "DBViewPlan", ("m_dbViewTypeId", 1));
        var viewer = ElementRoot(state, 3, "Viewer", (WideNativeParams.ViewerViewKey, 2));
        viewer.NativeRootClassChain = ["Viewer", "Element"];
        var pipe = ElementRoot(state, 4, "RbsPipeCurve");
        pipe.Parameters.Add(BipHelpers.MakeBipParam(-1001203, "ALL_MODEL_MARK", StorageType.String, "P01"));
        pipe.Parameters.Add(BipHelpers.MakeBipParam(-1140225, "RBS_PIPE_DIAMETER_PARAM", StorageType.Double, 0.25));
        var centerline = ElementRoot(state, 5, "SegmentCenterLine", (TfClassNames.Key("CenterLine", "m_OwnerId"), 4));
        centerline.NativeRootClassChain = ["SegmentCenterLine", "CenterLine", "Element"];
        state.NativeParams[2].TailWalkComplete = exact;
        state.NativeParams[4].TailWalkComplete = exact;
        var info = new RevitFileInfo { Elements = [viewType, view, viewer, pipe, centerline] };

        Revit2027ApiValues.Apply(info, state, info.Elements.ToDictionary(element => element.Id));

        Assert.Equal(exact ? 1 : -1, viewer.TypeId);
        if (exact)
            Assert.Equal(new object?[] { "P01", 0.25 }, centerline.Parameters.Select(p => p.Value));
        else
            Assert.Empty(centerline.Parameters);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ElevationAndStairPartsUseTheirLinkedTypes(bool linked)
    {
        var state = Wide2027();
        var viewType = ElementRoot(state, 1, "DBViewType");
        var marker = ElementRoot(state, 2, "InteriorElev", (TfClassNames.Key("InteriorElev", "m_symbolId"), 1));
        var arrow = ElementRoot(state, 3, "InteriorElevArrow", (WideNativeParams.ViewerViewKey, 4),
            (TfClassNames.Key("InteriorElevArrow", "m_parentId"), marker.Id));
        arrow.NativeRootClassChain = ["InteriorElevArrow", "Viewer", "Element"];
        var section = ElementRoot(state, 4, "DBViewSection", ("m_dbViewTypeId", -1),
            (TfClassNames.Key("DBView", "m_viewerId"), linked ? arrow.Id : -1));
        var runType = ElementRoot(state, 5, linked ? "StairsRunType" : "StairsLandingType");
        var stairsType = ElementRoot(state, 6, "StairsType", (TfClassNames.Key("StairsType", "m_runTypeId"), 5));
        var stairs = ElementRoot(state, 7, "StairsElement", (TfClassNames.Key("StairsElement", "m_typeId"), 6));
        var run = ElementRoot(state, 8, "StairsRun", (TfClassNames.Key("StairsRunAndLanding", "m_stairsId"), 7));
        var info = new RevitFileInfo { Elements = [viewType, marker, arrow, section, runType, stairsType, stairs, run] };

        Revit2027ApiValues.Apply(info, state, info.Elements.ToDictionary(element => element.Id));

        Assert.Equal(linked ? viewType.Id : -1, arrow.TypeId);
        Assert.Equal(linked ? viewType.Id : -1, section.TypeId);
        Assert.Equal(linked ? runType.Id : -1, run.TypeId);
        if (linked)
            Assert.Equal(new object?[] { runType.Id, runType.Id }, run.Parameters.Select(p => p.Value));
        else
            Assert.Empty(run.Parameters);
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_MassRoof, 0, "High Insulation - Cool Roof")]
    [InlineData(BuiltInCategory.OST_MassWallsAll, 0, "Lightweight Construction - High Insulation")]
    [InlineData(BuiltInCategory.OST_MassRoof, -1, "")]
    public void ConstructionNameRequiresBothStoredDiscriminators(BuiltInCategory category, int index, string expected)
    {
        var state = Wide2027();
        var type = ElementRoot(state, 1, "ConceptualConstructionType",
            (TfClassNames.Key("ConceptualConstructionType", "m_massSurfaceSubCategoryId"), (int)category),
            (TfClassNames.Key("ConceptualConstructionType", "m_conceptualConstructionTypeEnumAsInt"), index));
        type.NativeIsSymbol = true;
        type.Parameters.Add(BipHelpers.MakeBipParam((int)BuiltInParameter.ALL_MODEL_TYPE_NAME, "ALL_MODEL_TYPE_NAME", StorageType.String, ""));
        var info = new RevitFileInfo { Elements = [type] };

        Revit2027ApiValues.Apply(info, state, info.Elements.ToDictionary(element => element.Id));

        Assert.Equal(expected, Assert.Single(type.Parameters).Value);
    }

    [Theory]
    [InlineData(127, true)]
    [InlineData(128, false)]
    public void StraightDuctVolumeRequiresTheRectangularProfile(int profile, bool hasVolume)
    {
        var state = Wide2027();
        var type = ElementRoot(state, 1, "AbsDuctType");
        state.SystemTypeRoots[1] = (1, 110, profile);
        var duct = ElementRoot(state, 2, "RbsDuctCurve", ("602:m_idType", type.Id));
        duct.CategoryId = (int)BuiltInCategory.OST_DuctCurves;
        duct.Parameters.Add(BipHelpers.MakeBipParam((int)BuiltInParameter.CURVE_ELEM_LENGTH, "CURVE_ELEM_LENGTH", StorageType.Double, 5d));
        var fields = state.NativeParams[2].DoubleFields;
        fields["87:m_pCrv.endParams@0"] = 0;
        fields["602:m_dWidthOrDiameter"] = 2;
        fields["602:m_dHeight"] = 2;
        var info = new RevitFileInfo { Elements = [type, duct] };

        Revit2027ApiValues.Apply(info, state, info.Elements.ToDictionary(element => element.Id));

        var volume = duct.Parameters.SingleOrDefault(p => p.Id == (int)BuiltInParameter.HOST_VOLUME_COMPUTED);
        if (hasVolume) Assert.Equal(20d, volume?.Value);
        else Assert.Null(volume);
    }

    [Theory]
    [InlineData(true, 6d)]
    [InlineData(false, null)]
    public void RegionAreaRequiresOneClosedLineLoop(bool closed, double? expected)
    {
        double[] a = [1200, 800, 0], b = [1203, 800, 0], c = [1203, 802, 0], d = [1200, 802, 0];
        var area = WideNativeParams.ClosedLineLoopArea([(a, b), (c, d), (c, b), (d, closed ? a : [1199, 800, 0])]);
        Assert.Equal(expected, area);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RegionAreaRequiresItsOwnSketch(bool linked)
    {
        var state = Wide2027();
        var sketch = ElementRoot(state, 1, "VarSketch", (TfClassNames.Key("SketchBase", "m_userId"), linked ? 2 : 3));
        state.NativeParams[1].DoubleFields[WideNativeParams.SketchLineArea] = 6;
        var region = ElementRoot(state, 2, "FilledRegion", (TfClassNames.Key("SketchedRegion", "m_sketchId"), sketch.Id));
        var info = new RevitFileInfo { Elements = [sketch, region] };

        Revit2027ApiValues.Apply(info, state, info.Elements.ToDictionary(element => element.Id));

        if (linked) Assert.Equal(6d, Assert.Single(region.Parameters).Value);
        else Assert.Empty(region.Parameters);
    }

    private static Element ElementRoot(ParsingState state, uint id, string kind, params (string Key, int Value)[] fields)
    {
        var root = new TfBlockDeserializer.NativeParams { TailWalkComplete = true };
        root.Fields["m_id"] = (int)id;
        foreach (var (key, value) in fields) root.Fields[key] = value;
        state.NativeParams[id] = root;
        state.NativeParamsFromRootClass.Add(id);
        state.NativeRootClassNames[id] = kind;
        return new Element { Id = (int)id, TypeId = -1, NativeRootClassName = kind };
    }
}
