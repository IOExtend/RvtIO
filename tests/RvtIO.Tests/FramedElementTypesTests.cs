using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class FramedElementTypesTests
{
    [Theory]
    [InlineData(8, "Viewer", "DBViewProject", BuiltInCategory.INVALID)]
    [InlineData(4, "Section", "DBViewSection", BuiltInCategory.OST_Views)]
    [InlineData(4, "SegmentCenterLine", "RbsPipeCurve", BuiltInCategory.OST_PipeCurves)]
    [InlineData(8, "PipeFittingCenterLine", "FamilyInstance", BuiltInCategory.OST_PipeFitting)]
    [InlineData(4, "DuctFittingCenterLine", "FamilyInstance", BuiltInCategory.OST_DuctFitting)]
    public void CategoryParametersUseTheVerifiedViewOrCenterLineOwner(int width, string source, string host, BuiltInCategory category)
    {
        var f = new Fixture(width);
        long target = width == 8 ? (long)int.MaxValue + 10 : 20;
        f.Add(source, 10, w =>
        {
            f.Id(w, target);
            if (source is "Viewer" or "Section") w.Write(false);
        }, partition: 1);
        f.AddHeader(10, BuiltInCategory.OST_Lines, partition: 1);
        f.Add(host, target, w =>
        {
            if (host.StartsWith("DBView", StringComparison.Ordinal)) { f.Id(w, -1); f.Id(w, -1); }
            if (host == "FamilyInstance") f.Id(w, -1);
        }, partition: 3);
        f.AddHeader(target, category, partition: 3);
        var first = BipHelpers.MakeStoredBipParam(-1140362, StorageType.ElementId, -1, true);
        var occurrence = new Element { Id = 10, CategoryId = (int)BuiltInCategory.OST_Lines, Parameters = [first] };
        f.Apply(occurrence);
        Assert.Equal((int)BuiltInCategory.OST_Lines, occurrence.CategoryId);
        Assert.Same(first, occurrence.Parameters[0]);
        Assert.Equal(new[] { -1140362, -1140363 }, occurrence.Parameters.Select(p => p.Id));
        Assert.All(occurrence.Parameters, p =>
        {
            Assert.Equal((int)category, p.Value);
            Assert.Equal(StorageType.ElementId, p.StorageType);
            Assert.True(p.HasRevitValue);
        });
        if (category == BuiltInCategory.INVALID)
        {
            Assert.True(first.ShallowCopy(-1).HasRevitValue);
            var edited = first.ShallowCopy(17);
            Assert.False(edited.HasComputedInvalidCategory);
            first.Value = -1;
            Assert.False(first.HasRevitValue);
        }
    }

    [Theory]
    [InlineData("conflicting category")]
    [InlineData("conflicting source link")]
    [InlineData("unpaired target")]
    [InlineData("different owner")]
    [InlineData("wrong host class")]
    [InlineData("missing source header")]
    [InlineData("unexpected fitting category")]
    public void AmbiguousCenterLineEvidencePreservesBothParameters(string condition)
    {
        var f = new Fixture();
        f.Add("PipeFittingCenterLine", 10, w => f.Id(w, 20), partition: 1);
        if (condition != "missing source header") f.AddHeader(10, BuiltInCategory.OST_Lines, partition: 1);
        if (condition == "conflicting source link")
        {
            f.Add("PipeFittingCenterLine", 10, w => f.Id(w, 21), partition: 2);
            f.AddHeader(10, BuiltInCategory.OST_Lines, partition: 2);
        }
        if (condition == "wrong host class") f.Add("Other", 20, partition: 3);
        else f.Add("FamilyInstance", 20, w => f.Id(w, -1), storedOwner: condition == "different owner" ? 21 : 20, partition: 3);
        f.AddHeader(20, condition == "unexpected fitting category" ? BuiltInCategory.OST_Walls : BuiltInCategory.OST_PipeFitting,
            partition: condition == "unpaired target" ? 7 : 3);
        if (condition == "conflicting category")
        {
            f.Add("FamilyInstance", 20, w => f.Id(w, -1), partition: 4);
            f.AddHeader(20, BuiltInCategory.OST_DuctFitting, partition: 4);
        }
        var element = new Element { Id = 10 };
        f.Apply(element);
        Assert.Empty(element.Parameters);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void PropertySetElementHasACategoryAndTwoUnsetCategoryParameters(int width)
    {
        var f = new Fixture(width);
        f.Add("PropertySetElement", 10, partition: 1);
        f.AddHeader(10, BuiltInCategory.OST_PropertySet, partition: 1);
        var element = new Element { Id = 10 };
        f.Apply(element);
        Assert.Equal((int)BuiltInCategory.OST_PropertySet, element.CategoryId);
        Assert.Equal(2, element.Parameters.Count);
        Assert.All(element.Parameters, p => { Assert.Equal(-1, p.Value); Assert.False(p.HasRevitValue); });
    }

    [Theory]
    [InlineData(4, "GStyleElem", true)]
    [InlineData(8, "CategoryElem", true)]
    [InlineData(8, "ParamElemFamily", true)]
    [InlineData(4, "FontElem", true)]
    [InlineData(4, "ParamBinding", true)]
    [InlineData(8, "SelectionFilterElement", true)]
    [InlineData(4, "FamilySurrogate", true)]
    [InlineData(8, "NonPlanarSketch", true)]
    [InlineData(4, "Family", true)]
    [InlineData(8, "SketchPlane", true)]
    [InlineData(4, "PostedWarningElem", true)]
    [InlineData(8, "DBDrawing", true)]
    [InlineData(4, "AppearanceAssetElem", true)]
    [InlineData(8, "ExtentElem", true)]
    [InlineData(4, "FillPatternElem", true)]
    [InlineData(8, "RefTableElem", true)]
    [InlineData(4, "LinePatternElem", true)]
    [InlineData(4, "Other", false)]
    public void InvalidCategoryCanBeAComputedValueForAnUncategorizedOwner(int width, string kind, bool expected)
    {
        var f = new Fixture(width);
        f.Add(kind, 10, partition: 1);
        f.AddHeader(10, BuiltInCategory.INVALID, partition: 1);
        var element = new Element { Id = 10 };
        f.Apply(element);
        Assert.Null(element.CategoryId);
        Assert.Equal(expected ? 2 : 0, element.Parameters.Count);
        Assert.All(element.Parameters, p => { Assert.Equal(-1, p.Value); Assert.True(p.HasRevitValue); });
    }

    [Theory]
    [InlineData("conflicting headers")]
    [InlineData("positive category")]
    [InlineData("different record owner")]
    [InlineData("missing header")]
    public void InvalidCategoryParameterRequiresAConsistentOwnHeader(string condition)
    {
        var f = new Fixture();
        f.Add("GStyleElem", 10, storedOwner: condition == "different record owner" ? 11 : 10, partition: 1);
        if (condition != "missing header")
            f.AddHeader(10, condition == "positive category" ? BuiltInCategory.OST_Lines : BuiltInCategory.INVALID, partition: 1);
        if (condition == "conflicting headers")
        {
            f.Add("GStyleElem", 10, partition: 3);
            f.AddHeader(10, BuiltInCategory.OST_Lines, partition: 3);
        }
        var element = new Element { Id = 10 };
        f.Apply(element);
        Assert.Empty(element.Parameters);
    }

    [Theory]
    [InlineData(4, "RoomTag", -2000485, true)]
    [InlineData(8, "LegendComponent", -2000576, true)]
    [InlineData(4, "LinearDimString", -2000261, true)]
    [InlineData(8, "DBViewPlan", -2000279, true)]
    [InlineData(4, "DBViewPlan", -1, true)]
    [InlineData(4, "RoomTag", -1, false)]
    [InlineData(8, "LinearDimString", -1, false)]
    [InlineData(4, "Family", -2000279, false)]
    [InlineData(8, "LegendComponent", 42, false)]
    [InlineData(4, "DBViewPlan", -12345678, false)]
    public void DrawingCategoryParametersRequireTheVerifiedHeaderValue(int width, string kind, int category, bool expected)
    {
        var f = new Fixture(width);
        f.Add(kind, 10, writer =>
        {
            if (kind is "RoomTag" or "LinearDimString" or "DBViewPlan") f.Id(writer, -1);
            if (kind == "DBViewPlan") f.Id(writer, -1);
        }, partition: 2);
        f.AddHeaderId(10, category, partition: 2);
        var element = new Element { Id = 10 };
        f.Apply(element);
        var parameters = element.Parameters.Where(p => p.Id is -1140362 or -1140363).ToArray();
        Assert.Equal(expected ? new[] { -1140362, -1140363 } : [], parameters.Select(p => p.Id));
        Assert.All(parameters, p => { Assert.Equal(category, p.Value); Assert.True(p.HasRevitValue); });
    }

    [Theory]
    [InlineData(4, "ParamElemProject", BuiltInCategory.OST_Walls)]
    [InlineData(8, "DBViewTypeAreaPlan", BuiltInCategory.OST_Views)]
    [InlineData(8, "SysPanelFamSym", BuiltInCategory.INVALID)]
    public void CategoryParametersUseAnInheritedSymbolOrParameterHeader(int width, string kind, BuiltInCategory category)
    {
        var f = new Fixture(width);
        foreach (int edition in new[] { 1, 3 })
        {
            f.Add(kind, 10, partition: edition);
            f.AddHeader(10, category, partition: edition);
        }
        var element = new Element { Id = 10, TypeId = -1 };
        f.Apply(element);
        Assert.Equal(new[] { -1140362, -1140363 }, element.Parameters.Select(p => p.Id));
        Assert.All(element.Parameters, p => { Assert.Equal((int)category, p.Value); Assert.True(p.HasRevitValue); });
    }

    [Theory]
    [InlineData(4, "MasterImportSymbol", "complete", true)]
    [InlineData(8, "SlaveImportSymbol", "complete", true)]
    [InlineData(8, "MasterImportSymbol", "missing header", false)]
    [InlineData(4, "SlaveImportSymbol", "wrong root", false)]
    [InlineData(4, "SlaveImportSymbol", "conflicting editions", false)]
    [InlineData(4, "SysPanelFamSym", "complete", false)]
    public void ImportSymbolResolvesItsDocumentCategory(int width, string kind, string state, bool expected)
    {
        var f = new Fixture(width);
        long target = width == 4 ? 30 : (long)int.MaxValue + 30;
        f.Add(kind, 10, partition: 1);
        f.AddHeaderId(10, target, partition: 1);
        f.Add(state == "wrong root" ? "Other" : "CategoryElem", target, partition: 2);
        if (state != "missing header") f.AddHeader(target, BuiltInCategory.INVALID, partition: 2);
        if (state == "conflicting editions")
        {
            f.Add("CategoryElem", target, partition: 3);
            f.AddHeader(target, BuiltInCategory.OST_Lines, partition: 3);
        }
        var element = new Element { Id = 10, TypeId = -1 };
        f.Apply(element);
        Assert.Equal(expected ? 2 : 0, element.Parameters.Count);
        Assert.All(element.Parameters, p => { Assert.Equal(target, p.AsElementId64()); Assert.True(p.HasRevitValue); });
    }

    [Theory]
    [InlineData("conflicting binding header")]
    [InlineData("unpaired binding")]
    [InlineData("mixed roots")]
    [InlineData("unknown category")]
    [InlineData("family document")]
    public void UnverifiedParameterBindingsAndSymbolsKeepTheirSlots(string condition)
    {
        var f = new Fixture();
        string kind = condition == "unknown category" ? "SysPanelFamSym" : "ParamBinding";
        f.Add(kind, 10, partition: 1);
        f.AddHeader(10, condition == "unknown category" ? (BuiltInCategory)42 : BuiltInCategory.INVALID, partition: 1);
        if (condition is "mixed roots" or "unpaired binding" or "conflicting binding header")
        {
            f.Add(condition == "mixed roots" ? "Other" : kind, 10, partition: 2);
            if (condition != "unpaired binding")
                f.AddHeader(10, condition == "conflicting binding header" ? BuiltInCategory.OST_Walls : BuiltInCategory.INVALID, partition: 2);
        }
        var element = new Element { Id = 10, TypeId = -1 };
        f.Apply(element, projectCategories: condition != "family document");
        Assert.Empty(element.Parameters);
    }

    [Theory]
    [InlineData(8, 0, true, false, "Symbolic Lines")]
    [InlineData(4, -1, false, false, "Model Lines")]
    [InlineData(8, 49154, false, false, "Model Lines")]
    [InlineData(4, 0, false, false, null)]
    [InlineData(8, 2, true, false, null)]
    [InlineData(4, 0, true, true, null)]
    public void FamilyLinesUseTheirOwnVisibilityAndIntegerParameters(
        int width, int flags, bool headOnPlacement, bool duplicateKey, string? expected)
    {
        var fixture = new Fixture(width, familyVisibility: true);
        fixture.Add("Family", 20, w => fixture.Pointer(w, null));
        var keys = new List<BuiltInParameter> { BuiltInParameter.IS_VISIBLE_PARAM };
        if (headOnPlacement) keys.Add(BuiltInParameter.HEAD_ON_PLACEMENT_METHOD);
        if (duplicateKey) keys.Add(keys[0]);
        fixture.AddFamilyLine(10, 20, flags, [.. keys]);
        var curve = new Element { Id = 10, NativeHeaderCategoryId = (int)BuiltInCategory.OST_Lines };
        fixture.Apply(curve, englishCurveNames: true);
        Assert.Equal(expected, curve.ValidatedCurveName);
    }

    [Fact]
    public void FamilyLinesRequireAgreementAcrossCopiesAndAnOwnVisibilityParameter()
    {
        var fixture = new Fixture(familyVisibility: true);
        fixture.Add("Family", 20, w => fixture.Pointer(w, null));
        fixture.AddFamilyLine(10, 20, 0, BuiltInParameter.IS_VISIBLE_PARAM, BuiltInParameter.HEAD_ON_PLACEMENT_METHOD);
        fixture.AddFamilyLine(10, 20, 2, BuiltInParameter.IS_VISIBLE_PARAM);
        fixture.AddFamilyLine(11, 20, 0, BuiltInParameter.HEAD_ON_PLACEMENT_METHOD);
        foreach (int id in new[] { 10, 11 })
        {
            var curve = new Element { Id = id, Name = "original", NativeHeaderCategoryId = (int)BuiltInCategory.OST_Lines };
            fixture.Apply(curve, englishCurveNames: true);
            Assert.Null(curve.ValidatedCurveName);
            Assert.Equal("original", curve.Name);
        }
    }

    [Theory]
    [InlineData(4, "Family", "Line")]
    [InlineData(8, "Family", "Line")]
    [InlineData(4, "Other", null)]
    [InlineData(4, null, null)]
    public void FamilySketchCurveNamesRequireTheStoredFamilyLink(int width, string? target, string? expected)
    {
        var fixture = new Fixture(width);
        long family = width == 8 ? (long)int.MaxValue + 1 : 20;
        if (target != null) fixture.Add(target, family);
        fixture.Add("CurveElem", 10, w => { w.Write(false); fixture.Pointer(w, null); }, family: family);
        var curve = new Element { Id = 10, NativeHeaderCategoryId = (int)BuiltInCategory.OST_SketchLines };
        fixture.Apply(curve, englishCurveNames: true);
        Assert.Equal(expected, curve.ValidatedCurveName);
    }

    [Fact]
    public void ConflictingFamilyLinksDoNotSupplyACurveName()
    {
        var fixture = new Fixture();
        fixture.Add("Family", 20);
        foreach (var family in new[] { 20, -1 })
            fixture.Add("CurveElem", 10, w => { w.Write(false); fixture.Pointer(w, null); }, family: family);
        var curve = new Element { Id = 10, NativeHeaderCategoryId = (int)BuiltInCategory.OST_SketchLines };
        fixture.Apply(curve, englishCurveNames: true);
        Assert.Null(curve.ValidatedCurveName);
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_Lines, null, 20, 40, 1, "Line")]
    [InlineData(BuiltInCategory.OST_SketchLines, "FilledRegionCurveData", 20, 40, 1, "Line")]
    [InlineData(BuiltInCategory.OST_Lines, "FilledRegionCurveData", 20, 40, 1, null)]
    [InlineData(BuiltInCategory.OST_Lines, null, 21, 40, 1, null)]
    [InlineData(BuiltInCategory.OST_Lines, null, 20, 41, 1, null)]
    [InlineData(BuiltInCategory.OST_Lines, null, 20, 40, 0, null)]
    [InlineData(BuiltInCategory.OST_Lines, null, 20, 40, 2, null)]
    public void DetailCurveNameRequiresItsOwnFamilySketch(BuiltInCategory category, string? data,
        int sketchFamily, int sketchPlane, int memberships, string? expected)
    {
        var fixture = new Fixture(8, detailSketch: true);
        const long sketch = (long)int.MaxValue + 3;
        fixture.Add("Family", 20, w => fixture.Pointer(w, null));
        fixture.Add("VarSketch", sketch, w =>
        {
            fixture.Pointer(w, null);
            fixture.Id(w, sketchPlane);
        }, family: sketchFamily);
        fixture.Add("CurveElem", 10, w =>
        {
            fixture.Pointer(w, "CellList");
            w.Write(true);
            fixture.Pointer(w, data);
            fixture.Id(w, 40);
            fixture.Pointer(w, memberships > 0 ? "SketchMembership" : null);
            fixture.Pointer(w, memberships > 1 ? "SketchMembership" : null);
            for (int i = 0; i < memberships; i++) fixture.Id(w, sketch);
        }, family: 20);
        var curve = new Element { Id = 10, TypeId = -1, Name = "original", NativeHeaderCategoryId = (int)category };
        fixture.Apply(curve, englishCurveNames: true);
        Assert.Equal(expected, curve.ValidatedCurveName);
        Assert.Equal(expected ?? "original", curve.Name);
        Assert.Equal(-1, curve.TypeId);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(8, false)]
    [InlineData(4, true)]
    public void CurveNameCanUseItsRetainedHeadersWhenTheCategoryWasNotIndexed(int width, bool conflicting)
    {
        var fixture = new Fixture(width);
        fixture.Add("CurveElem", 10, w => { w.Write(true); fixture.Pointer(w, null); });
        fixture.AddHeader(10, BuiltInCategory.OST_Lines);
        fixture.AddHeader(10, conflicting ? BuiltInCategory.OST_Walls : BuiltInCategory.OST_Lines);
        var element = new Element { Id = 10 };
        fixture.Apply(element, englishCurveNames: true);
        Assert.Equal(conflicting ? null : "Detail Lines", element.ValidatedCurveName);
        Assert.Null(element.NativeHeaderCategoryId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CurveNameNeedsCompleteHeaderEvidenceWhenOnlyTheDisplayCategoryIsKnown(bool malformed)
    {
        var fixture = new Fixture();
        fixture.Add("CurveElem", 10, w => { w.Write(true); fixture.Pointer(w, null); });
        if (malformed)
        {
            fixture.AddHeader(10, BuiltInCategory.OST_Lines, trailingByte: true);
            fixture.AddHeader(10, BuiltInCategory.OST_Lines);
        }
        var element = new Element { Id = 10, CategoryId = (int)BuiltInCategory.OST_Lines };
        fixture.Apply(element, englishCurveNames: true);
        Assert.Null(element.ValidatedCurveName);
    }

    [Theory]
    [InlineData("GStyleElem", 4)]
    [InlineData("CategoryElem", 8)]
    [InlineData("FontElem", 4)]
    [InlineData("ParamElemFamily", 8)]
    [InlineData("KeynoteTable", 4)]
    public void AnUntypedRecordWithANullHeaderCategoryClearsItsInferredCategory(string kind, int width)
    {
        var fixture = new Fixture(width);
        fixture.Add(kind, 10);
        fixture.Add(kind, 10);
        fixture.AddHeader(10, BuiltInCategory.INVALID);
        fixture.AddHeader(10, BuiltInCategory.INVALID);
        var element = new Element { Id = 10, TypeId = -1, CategoryId = (int)BuiltInCategory.OST_Lines,
            Category = "OST_Lines", Name = "original", References = [11] };

        Assert.Equal(-1, fixture.Apply(element));
        Assert.Null(element.CategoryId);
        Assert.Null(element.Category);
        Assert.Equal("original", element.Name);
        Assert.Equal([11], element.References);
    }

    [Theory]
    [InlineData("no header")]
    [InlineData("valid category")]
    [InlineData("conflicting headers")]
    [InlineData("malformed header")]
    [InlineData("different owner")]
    [InlineData("competing root")]
    [InlineData("derived boundary category")]
    [InlineData("header in another partition")]
    [InlineData("unpaired body")]
    [InlineData("curve names only")]
    [InlineData("family document")]
    public void ClearingACategoryRequiresItsOwnClassAndAllRetainedHeaders(string condition)
    {
        var fixture = new Fixture();
        fixture.Add(condition == "derived boundary category" ? "BoundaryConditions" : "CategoryElem", 10,
            storedOwner: condition == "different owner" ? 11 : 10);
        if (condition == "unpaired body") fixture.Add("CategoryElem", 10);
        if (condition == "competing root")
        {
            fixture.Add("Other", 10);
            fixture.AddHeader(10, BuiltInCategory.INVALID);
        }
        if (condition != "no header") fixture.AddHeader(10,
            condition == "valid category" ? BuiltInCategory.OST_Lines : BuiltInCategory.INVALID,
            partition: condition == "header in another partition" ? 7 : null);
        if (condition is "conflicting headers" or "malformed header")
        {
            fixture.Add("CategoryElem", 10);
            fixture.AddHeader(10, condition == "conflicting headers" ? BuiltInCategory.OST_Walls : BuiltInCategory.INVALID,
                trailingByte: condition == "malformed header");
        }
        var element = new Element { Id = 10, TypeId = -1, CategoryId = (int)BuiltInCategory.OST_Lines,
            Category = "OST_Lines" };
        fixture.Apply(element, englishCurveNames: true, applyTypeLinks: condition != "curve names only",
            projectCategories: condition != "family document");
        Assert.Equal((int)BuiltInCategory.OST_Lines, element.CategoryId);
        Assert.Equal("OST_Lines", element.Category);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void OwnDimensionHeaderOverridesAnInferredCategoryWithoutChangingItsType(int width)
    {
        var fixture = new Fixture(width);
        fixture.Add("LinearDimString", 10, w => fixture.Id(w, -1), partition: 7);
        fixture.AddHeader(10, BuiltInCategory.OST_Constraints, partition: 7);
        var element = new Element { Id = 10, TypeId = 11, CategoryId = (int)BuiltInCategory.OST_Dimensions,
            Category = "OST_Dimensions", Name = "dimension" };
        fixture.Apply(element, anchored: true);
        Assert.Equal(11, element.TypeId);
        Assert.Equal((int)BuiltInCategory.OST_Constraints, element.CategoryId);
        Assert.Equal("OST_Constraints", element.Category);
        Assert.Equal("dimension", element.Name);
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_Lines, true, null, "Detail Lines")]
    [InlineData(BuiltInCategory.OST_SketchLines, true, "FilledRegionCurveData", "Detail Lines")]
    [InlineData(BuiltInCategory.OST_SketchLines, false, "JoistSystemDirSpanData", "Span Direction Edges")]
    [InlineData(BuiltInCategory.OST_SketchLines, false, "RailingSketchCurveData", "Model Lines")]
    [InlineData(BuiltInCategory.OST_SketchLines, false, "FabricAreaCurveData", "Model Lines")]
    [InlineData(BuiltInCategory.OST_StairsSketchPathLines, false, "StairsCurveData", "Model Lines")]
    [InlineData(BuiltInCategory.OST_StairsSketchBoundaryLines, false, "StairsCurveData", "Model Lines")]
    [InlineData(BuiltInCategory.OST_StairsSketchBoundaryLines, true, "StairsCurveData", null)]
    [InlineData(BuiltInCategory.OST_SketchLines, false, "StairsCurveData", null)]
    [InlineData(BuiltInCategory.OST_AreaSchemeLines, false, "AreaMeasureCurveData", "Model Lines")]
    [InlineData(BuiltInCategory.OST_Lines, false, "Other", null)]
    [InlineData(BuiltInCategory.OST_Walls, false, null, null)]
    public void EnglishCurveNameUsesItsStoredKind(BuiltInCategory category, bool detail, string? data, string? expected)
    {
        var fixture = new Fixture();
        fixture.Add("CurveElem", 10, w => { w.Write(detail); fixture.Pointer(w, data); });
        var element = new Element { Id = 10, Name = "original", NativeHeaderCategoryId = (int)category };
        fixture.Apply(element);
        Assert.Null(element.ValidatedCurveName);
        fixture.Apply(element, englishCurveNames: true);
        Assert.Equal(expected, element.ValidatedCurveName);
        Assert.Equal(expected ?? "original", element.Name);
        if (expected != null)
        {
            var info = new RevitFileInfo();
            info.Elements.Add(element);
            RvtIO.Export.ValidatedElementNames.Apply(info, new ParsingState());
            Assert.Equal(expected, element.Name);
        }
    }

    [Fact]
    public void ConflictingOwnCurveFlagsDoNotSupplyAName()
    {
        var fixture = new Fixture();
        foreach (bool detail in new[] { false, true })
            fixture.Add("CurveElem", 10, w => { w.Write(detail); fixture.Pointer(w, null); });
        var element = new Element { Id = 10, NativeHeaderCategoryId = (int)BuiltInCategory.OST_Lines };
        fixture.Apply(element, englishCurveNames: true);
        Assert.Null(element.ValidatedCurveName);
    }

    [Fact]
    public void ReadingCurveNamesAlonePreservesTheNativeViewType()
    {
        var fixture = new Fixture();
        fixture.Add("Viewer3d", 10, w => { fixture.Id(w, 20); w.Write(false); });
        fixture.Add("DBView3d", 20, w => { fixture.Id(w, 30); fixture.Id(w, 10); });
        fixture.Add("DBViewType", 30);
        fixture.Add("CurveElem", 40, w => { w.Write(true); fixture.Pointer(w, null); });
        var curve = new Element { Id = 40, NativeHeaderCategoryId = (int)BuiltInCategory.OST_Lines };
        fixture.Apply(curve, englishCurveNames: true, applyTypeLinks: false);
        Assert.Equal("Detail Lines", curve.ValidatedCurveName);
        Assert.Equal(99, fixture.Apply(new Element { Id = 10, TypeId = 99 }, englishCurveNames: true, applyTypeLinks: false));
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(8, true)]
    [InlineData(4, false)]
    public void AViewerNeedsTheBacklinkToItsOwnView(int width, bool reciprocal)
    {
        var fixture = new Fixture(width);
        long type = width == 8 ? (long)int.MaxValue + 20 : 30;
        fixture.Add("Viewer3d", 10, w => { fixture.Id(w, 20); w.Write(false); });
        fixture.Add("DBView3d", 20, w => { fixture.Id(w, type); fixture.Id(w, reciprocal ? 10 : 11); });
        fixture.Add("DBViewType", type);

        Assert.Equal(reciprocal ? type : 99, fixture.TypeOf(10));
    }

    [Theory]
    [InlineData("DBViewLegend", "DBViewType", 30L, 10, false, 30L)]
    [InlineData("DBViewPlan", "DBViewTypeAreaPlan", 2147483655L, 10, false, 2147483655L)]
    [InlineData("DBViewLegend", null, -1L, 10, false, -1L)]
    [InlineData("DBViewLegend", null, 30L, 10, false, 99L)]
    [InlineData("DBViewLegend", "Family", 30L, 10, false, 99L)]
    [InlineData("DBViewLegend", "DBViewType", 30L, 11, false, 99L)]
    [InlineData("DBViewLegend", "DBViewType", 30L, 10, true, 99L)]
    public void APrimaryViewerUsesTheTypeOfItsReciprocalView(string view, string? target, long type,
        int backlink, bool reference, long expected)
    {
        var fixture = new Fixture(8);
        fixture.Add("Viewer", 10, w => { fixture.Id(w, 20); w.Write(reference); });
        fixture.Add(view, 20, w => { fixture.Id(w, type); fixture.Id(w, backlink); });
        if (target != null) fixture.Add(target, type);
        var marker = new Element { Id = 10, TypeId = 99, References = [20], Name = "marker" };
        Assert.Equal(expected, fixture.Apply(marker));
        Assert.Equal([20], marker.References);
        Assert.Equal("marker", marker.Name);
    }

    [Fact]
    public void ConflictingViewCopiesDoNotSupplyAViewerType()
    {
        var fixture = new Fixture();
        fixture.Add("Viewer", 10, w => { fixture.Id(w, 20); w.Write(false); });
        foreach (int type in new[] { -1, 30 })
            fixture.Add("DBViewLegend", 20, w => { fixture.Id(w, type); fixture.Id(w, 10); });
        fixture.Add("DBViewType", 30);
        Assert.Equal(99, fixture.TypeOf(10));
    }

    [Theory]
    [InlineData("DBViewSection", false, true, true, 30)]
    [InlineData("DBViewPlan", false, true, true, 30)]
    [InlineData("DBViewPlan", true, true, true, 30)]
    [InlineData("DBViewPlan", true, false, true, 99)]
    [InlineData("DBViewPlan", false, false, true, 99)]
    [InlineData("DBViewPlan", false, true, false, 99)]
    [InlineData("DBView3d", false, true, true, 99)]
    public void SectionMarkersRequireTheirViewAndPrimaryMarker(string viewClass, bool reference,
        bool reciprocal, bool validType, long expected)
    {
        var fixture = new Fixture();
        fixture.Add("Section", 10, w => { fixture.Id(w, 20); w.Write(reference); });
        if (reference) fixture.Add("Section", 11, w => { fixture.Id(w, reciprocal ? 20 : 22); w.Write(false); });
        fixture.Add(viewClass, 20, w => { fixture.Id(w, 30); fixture.Id(w, reference || !reciprocal ? 11 : 10); });
        fixture.Add(validType ? "DBViewType" : "Family", 30);
        Assert.Equal(expected, fixture.TypeOf(10));
    }

    [Theory]
    [InlineData("EnergyAnalysisSurface")]
    [InlineData("EnergyAnalysisOpening")]
    [InlineData("LegendComponent")]
    [InlineData("AreaMeasureElem")]
    [InlineData("PlanRegion")]
    [InlineData("RefPlane")]
    [InlineData("AnalysisDisplayStyle")]
    [InlineData("CurtainGridLine")]
    [InlineData("Opening")]
    [InlineData("ReferencePoint")]
    [InlineData("ScheduleInstance")]
    [InlineData("BuildingOperatingYearSchedule")]
    [InlineData("DPart")]
    [InlineData("FaceSplitter")]
    [InlineData("PartMaker")]
    [InlineData("Matchline")]
    [InlineData("Property")]
    [InlineData("RevisionCloud")]
    [InlineData("ShaftOpening")]
    [InlineData("BoundaryConditions")]
    [InlineData("ProjectPhase")]
    [InlineData("GStyleElem")]
    [InlineData("CategoryElem")]
    [InlineData("FontElem")]
    [InlineData("Family")]
    [InlineData("FamilySurrogate")]
    [InlineData("FamSymSurrogate")]
    [InlineData("SketchPlane")]
    [InlineData("PostedWarningElem")]
    [InlineData("DBDrawing")]
    [InlineData("NestedFamSymRefElem")]
    [InlineData("ParamElemFamily")]
    [InlineData("TextNoteAttributes")]
    [InlineData("SysPanelFamSym")]
    [InlineData("DBViewTypeAreaPlan")]
    public void AnUntypedElementKeepsItsStoredLinks(string kind)
    {
        var fixture = new Fixture();
        fixture.Add(kind, 10);
        fixture.Add(kind, 11, storedOwner: 12);
        fixture.Add(kind, 13);
        fixture.Add("Other", 13);
        var e = new Element { Id = 10, TypeId = 99, References = [99], Name = "original" };
        Assert.Equal(-1, fixture.Apply(e));
        Assert.Equal("original", e.Name);
        Assert.Equal([99], e.References);
        Assert.Equal(99, fixture.TypeOf(11));
        Assert.Equal(99, fixture.TypeOf(13));
    }

    [Theory]
    [InlineData(-1, -1, -1)]
    [InlineData(30, -1, 99)]
    [InlineData(30, 30, 99)]
    public void OnlyAnAgreedInvalidScheduleTypeClearsAnUnrelatedReference(int first, int second, long expected)
    {
        var fixture = new Fixture();
        foreach (int type in new[] { first, second })
            fixture.Add("DBViewSchedule", 10, w => { fixture.Id(w, type); fixture.Id(w, -1); });
        Assert.Equal(expected, fixture.TypeOf(10));
    }

    [Theory]
    [InlineData("WallSweep", -1, -1, -1)]
    [InlineData("WallSweep", -1, 20, 99)]
    [InlineData("DBViewPlan", -1, -1, -1)]
    [InlineData("DBViewPlan", 20, -1, 99)]
    [InlineData("DBViewProject", -1, -1, -1)]
    [InlineData("DBViewProject", 20, 20, 99)]
    [InlineData("RbsDbViewSystemNavigator", -1, -1, -1)]
    [InlineData("RbsDbViewSystemNavigator", -1, 20, 99)]
    public void InvalidTypeFieldsAreReadFromEveryCopy(string kind, int first, int second, long expected)
    {
        var fixture = new Fixture();
        foreach (int type in new[] { first, second })
            fixture.Add(kind, 10, w => { fixture.Id(w, type); if (kind != "WallSweep") fixture.Id(w, -1); });
        Assert.Equal(expected, fixture.TypeOf(10));
    }

    [Theory]
    [InlineData(-1L, -1L, null, -1L)]
    [InlineData(2147483655L, 2147483655L, "ViewportAttributes", 2147483655L)]
    [InlineData(-1L, 20L, "ViewportAttributes", 99L)]
    [InlineData(20L, 20L, "Family", 99L)]
    [InlineData(20L, 20L, null, 99L)]
    [InlineData(0L, 0L, null, 99L)]
    public void ViewportUsesItsAgreedAttributeId(long first, long second, string? target, long expected)
    {
        var fixture = new Fixture(8);
        foreach (long type in new[] { first, second })
            fixture.Add("Viewport", 10, w => fixture.Id(w, type));
        if (target != null) fixture.Add(target, second);
        Assert.Equal(expected, fixture.TypeOf(10));
    }

    [Theory]
    [InlineData(false, false, false, 20)]
    [InlineData(true, false, false, 99)]
    [InlineData(false, true, false, 99)]
    [InlineData(false, false, true, 99)]
    public void KeptFamilyInstanceCanReplaceANonSymbolType(bool repeated, bool priorIsSymbol, bool wrongTarget, long expected)
    {
        var fixture = new Fixture();
        fixture.Add("FamilyInstance", 10, w => fixture.Id(w, 20));
        if (repeated) fixture.Add("FamilyInstance", 10, w => fixture.Id(w, 21));
        fixture.Add(wrongTarget ? "Other" : "FamilySymbol", 20);
        fixture.Add(priorIsSymbol ? "SysPanelFamSym" : "ParamElemFamily", 99);
        var element = new Element { Id = 10, TypeId = 99, Name = "Original", HostId = 11 };
        Assert.Equal(expected, fixture.Apply(element, recoverUnanchoredTypes: true));
        Assert.Equal("Original", element.Name);
        Assert.Equal(11, element.HostId);
    }

    [Theory]
    [InlineData("Section", true, false, 2147483678L)]
    [InlineData("Section", false, false, 99L)]
    [InlineData("Section", true, true, 99L)]
    [InlineData("Viewer", true, false, 2147483678L)]
    [InlineData("Viewer", false, false, 99L)]
    [InlineData("Viewer", true, true, 99L)]
    public void MarkerUsesTheTypeFromTheUniquelyRoutedViewCopy(string kind, bool routed, bool duplicate,
        long expected)
    {
        var fixture = new Fixture(8);
        const long view = (long)int.MaxValue + 20, type = (long)int.MaxValue + 31;
        fixture.Add(kind, 10, w => { fixture.Id(w, view); w.Write(false); });
        fixture.Add("DBViewSection", view, w => { fixture.Id(w, type); fixture.Id(w, 10); }, partition: 1);
        fixture.Add("DBViewSection", view, w => { fixture.Id(w, 30); fixture.Id(w, 10); }, partition: duplicate ? 1 : 0);
        fixture.Add("DBViewType", 30);
        fixture.Add("DBViewType", type);
        var marker = new Element { Id = 10, TypeId = 99, Name = "original", References = [41] };
        Assert.Equal(expected, fixture.Apply(marker, recoverUnanchoredTypes: true, routedView: routed ? view : null));
        Assert.Equal("original", marker.Name);
        Assert.Equal([41], marker.References);
    }

    [Theory]
    [InlineData(true, true, 70)]
    [InlineData(true, false, 99)]
    [InlineData(false, true, 99)]
    public void AReferencedElevationArrowNeedsAgreementBetweenBothMarkers(
        bool containsArrow, bool sameType, long referenceType)
    {
        var fixture = new Fixture();
        fixture.Add("DBViewSection", 10, w => { fixture.Id(w, -1); fixture.Id(w, 20); });
        fixture.Add("InteriorElevArrow", 20, w => { fixture.Id(w, 10); w.Write(false); fixture.Id(w, 30); });
        fixture.Add("InteriorElev", 30, w => { fixture.Id(w, 70); w.Write(1u); fixture.Id(w, 20); });
        fixture.Add("InteriorElevArrow", 40, w => { fixture.Id(w, 10); w.Write(true); fixture.Id(w, 50); });
        fixture.Add("InteriorElev", 50, w =>
        {
            fixture.Id(w, sameType ? 70 : 71);
            w.Write(1u);
            fixture.Id(w, containsArrow ? 40 : 41);
        });
        fixture.Add("DBViewType", 70);
        fixture.Add("DBViewType", 71);

        Assert.Equal(70, fixture.TypeOf(10));
        Assert.Equal(70, fixture.TypeOf(20));
        Assert.Equal(referenceType, fixture.TypeOf(40));
    }

    [Theory]
    [InlineData("StairsRun", 40)]
    [InlineData("StairsLanding", 50)]
    public void StairPartsTakeTheirTypeFromTheOwningStairType(string kind, long expected)
    {
        var fixture = new Fixture();
        fixture.Add(kind, 10, w => fixture.Id(w, 20));
        fixture.Add("StairsElement", 20, w => fixture.Id(w, 30));
        fixture.Add("StairsType", 30, w => fixture.StairsType(w, 40, 50));
        fixture.Add("StairsRunType", 40);
        fixture.Add("StairsLandingType", 50);

        Assert.Equal(expected, fixture.TypeOf(10));
    }

    [Theory]
    [InlineData("StairsRun", 20, false, false, 40, 40)]
    [InlineData("StairsLanding", 20, false, false, 40, 40)]
    [InlineData("StairsRun", 21, false, false, 40, 99)]
    [InlineData("StairsRun", 20, true, false, 40, 99)]
    [InlineData("StairsRun", 20, false, true, 40, 99)]
    [InlineData("StairsRun", 20, false, false, 41, 99)]
    public void AStairSupportNeedsMatchingSideTypesOnItsOwningStair(
        string hostKind, int hostStair, bool overridden, bool intermediate, int right, long expected)
    {
        var fixture = new Fixture();
        fixture.Add("StairsSupport", 10, w => { fixture.Id(w, 11); w.Write(overridden); });
        fixture.Add(hostKind, 11, w => fixture.Id(w, hostStair));
        fixture.Add("StairsElement", 20, w => fixture.Id(w, 30));
        fixture.Add("StairsType", 30, w => fixture.StairsType(w, -1, -1, 40, right, intermediate));
        fixture.Add("StairsSupportType", 40);

        Assert.Equal(expected, fixture.TypeOf(10, stairOwner: 20));
    }

    [Theory]
    [InlineData("FabricSheet", "FabricSheetType")]
    [InlineData("HandRail", "HandRailType")]
    [InlineData("TopRail", "TopRailType")]
    [InlineData("GridChain", "GridAttributes")]
    [InlineData("RebarContainer", "RebarContainerType")]
    [InlineData("RoofSoffit", "RoofSoffitType")]
    [InlineData("Ramp", "RampAttributes")]
    public void DirectLinksValidateTheDestinationClass(string kind, string target)
    {
        var fixture = new Fixture();
        fixture.Add(kind, 10, w => fixture.Id(w, 20));
        fixture.Add(kind, 11, w => fixture.Id(w, 21));
        fixture.Add(target, 20);
        fixture.Add("DBViewType", 21);

        Assert.Equal(20, fixture.TypeOf(10));
        Assert.Equal(99, fixture.TypeOf(11));
    }

    [Theory]
    [InlineData(true, 20)]
    [InlineData(false, 99)]
    public void RebarTypeComesFromItsOwnInstancePointer(bool hasInstance, long expected)
    {
        var fixture = new Fixture();
        fixture.Add("Rebar", 10, w =>
        {
            fixture.Pointer(w, "RebarInstance");
            fixture.Pointer(w, hasInstance ? "RebarInstance" : null);
            fixture.Id(w, 21);
            if (hasInstance) fixture.Id(w, 20);
        });
        fixture.Add("RebarBarType", 20);
        fixture.Add("RebarBarType", 21);

        Assert.Equal(expected, fixture.TypeOf(10));
    }

    [Theory]
    [InlineData(20, 20)]
    [InlineData(21, 99)]
    public void ASpanTagNeedsBothStoredSymbolFieldsToAgree(int actual, long expected)
    {
        var fixture = new Fixture();
        fixture.Add("SpanDirectionSymbol", 10, w => { fixture.Id(w, 20); fixture.Id(w, actual); });
        fixture.Add("FamilySymbol", 20);
        fixture.Add("FamilySymbol", 21);

        Assert.Equal(expected, fixture.TypeOf(10));
    }

    [Theory]
    [InlineData(false, false, BuiltInCategory.OST_DetailComponents, 20)]
    [InlineData(true, false, BuiltInCategory.OST_DetailComponents, 99)]
    [InlineData(false, true, BuiltInCategory.OST_DetailComponents, 99)]
    [InlineData(false, false, BuiltInCategory.OST_Doors, 99)]
    public void ADetailComponentMustHaveOneUnroutedOwnRecord(
        bool duplicate, bool wasRouted, BuiltInCategory category, long expected)
    {
        var fixture = new Fixture();
        fixture.Add("FamilyInstance", 10, w => fixture.Id(w, 20));
        if (duplicate) fixture.Add("FamilyInstance", 10, w => fixture.Id(w, 20));
        fixture.Add("FamilySymbol", 20);

        Assert.Equal(expected, fixture.TypeOf(10, category, wasRouted));
    }

    [Theory]
    [InlineData(4, 1, false, false, false, true)]
    [InlineData(8, 1, false, false, false, true)]
    [InlineData(4, 0, false, false, false, false)]
    [InlineData(4, 1, true, false, false, false)]
    [InlineData(4, 1, false, true, false, false)]
    [InlineData(4, 1, false, false, true, false)]
    public void AnUnroutedFamilyRecoversOnlyTypeIdFromTheLatestHolder(
        int width, int routedIncrement, bool permutation, bool wrongType, bool ambiguous, bool corrected)
    {
        var fixture = new Fixture(width);
        long latest = width == 8 ? (long)int.MaxValue + 50 : 21;
        fixture.Add("FamilyInstance", 10, w => fixture.Id(w, latest), partition: 2);
        fixture.Add("FamilyInstance", 10, w => fixture.Id(w, 20), partition: 0);
        if (ambiguous) fixture.Add("FamilyInstance", 10, w => fixture.Id(w, 20), partition: 2);
        fixture.Add("FamilySymbol", 20);
        fixture.Add(wrongType ? "Other" : "SysPanelFamSym", latest);

        var rows = new[] { new ElemTableLayout.Row(10, 10, 0, 0, -1, routedIncrement) };
        var increments = Enumerable.Range(0, 3)
            .Select(i => new DocumentIncrementTable.Increment(i, 1, 1, [])).ToArray();
        var table = new DocumentIncrementTable.Table(increments, increments, permutation ? [(1, 2)] : []);
        var typePartitions = FramedElementTypes.TypePartitionsWithoutRoutedCopy(
            new Dictionary<ulong, List<int>> { [10] = [0, 2] }, rows, table, new Dictionary<ulong, int>());
        var element = new Element
        {
            Id = 10, TypeId = 20, Name = "Earlier name", HostId = 32,
            Parameters = [new Parameter { Definition = new Definition { Name = "ELEM_TYPE_PARAM" }, Value = 20 }],
        };
        var parameters = element.Parameters;

        Assert.Equal(corrected ? latest : 20, fixture.Apply(element, typePartitions: typePartitions));
        Assert.Equal("Earlier name", element.Name);
        Assert.Equal(32, element.HostId);
        Assert.Equal(20, element.FamilyTypeId);
        Assert.Same(parameters, element.Parameters);
    }

    [Theory]
    [InlineData("FamilyInstance", "FamilySymbol", true, true, 4)]
    [InlineData("FamilyInstance", "SysPanelFamSym", false, true, 8)]
    [InlineData("SWall", "NewCurtainWallType", true, true, 4)]
    [InlineData("FamilyInstance", "Other", true, false, 4)]
    public void ConflictingCategoriesRequireTheRoutedTypeLinkAndItsOwnHeader(
        string kind, string targetClass, bool routed, bool correct, int width)
    {
        var fixture = new Fixture(width);
        fixture.Add(kind, 10, w => fixture.Id(w, 20), partition: 0);
        fixture.Add(kind, 10, w => fixture.Id(w, 21), partition: 1);
        fixture.AddHeader(10, BuiltInCategory.OST_Doors, partition: 0);
        fixture.AddHeader(10, BuiltInCategory.OST_Windows, partition: 1);
        fixture.Add(targetClass, 21, partition: 3);
        fixture.AddHeader(21, BuiltInCategory.OST_Windows, partition: 3);
        var element = new Element { Id = 10, TypeId = 21, CategoryId = (int)BuiltInCategory.OST_Doors,
            Name = "retained", HostId = 41 };
        fixture.Apply(element, wasRouted: routed, recoverUnanchoredTypes: routed,
            typePartitions: routed ? null : new Dictionary<ulong, int> { [10] = 1 });
        Assert.Equal(21, element.TypeId);
        Assert.Equal((int)(correct ? BuiltInCategory.OST_Windows : BuiltInCategory.OST_Doors), element.CategoryId);
        Assert.Equal("retained", element.Name);
        Assert.Equal(41, element.HostId);
    }

    [Theory]
    [InlineData("unknown partition")]
    [InlineData("different linked category")]
    [InlineData("ambiguous target header")]
    [InlineData("missing occurrence header")]
    [InlineData("duplicate edition header")]
    [InlineData("same type in disagreeing editions")]
    public void AnUnprovenTypeCategoryLeavesConflictingCopiesAlone(string condition)
    {
        var fixture = new Fixture();
        fixture.Add("FamilyInstance", 10, w => fixture.Id(w, condition == "same type in disagreeing editions" ? 21 : 20), partition: 0);
        fixture.Add("FamilyInstance", 10, w => fixture.Id(w, 21), partition: 1);
        if (condition != "missing occurrence header") fixture.AddHeader(10, BuiltInCategory.OST_Doors, partition: 0);
        fixture.AddHeader(10, BuiltInCategory.OST_Windows, partition: 1);
        if (condition == "duplicate edition header") fixture.AddHeader(10, BuiltInCategory.OST_Windows, partition: 1);
        fixture.Add("FamilySymbol", 21, partition: 3);
        fixture.AddHeader(21, condition == "different linked category" ? BuiltInCategory.OST_Walls : BuiltInCategory.OST_Windows, partition: 3);
        if (condition == "ambiguous target header")
        {
            fixture.Add("FamilySymbol", 21, partition: 4);
            fixture.AddHeader(21, BuiltInCategory.OST_Doors, partition: 4);
        }
        var element = new Element { Id = 10, TypeId = 21, CategoryId = (int)BuiltInCategory.OST_Doors };
        fixture.Apply(element, wasRouted: condition != "unknown partition", recoverUnanchoredTypes: true);
        Assert.Equal((int)BuiltInCategory.OST_Doors, element.CategoryId);
    }

    [Fact]
    public void OtherOrInvalidProjectRecordsPreventTypeRecovery()
    {
        var fixture = new Fixture();
        fixture.Add("FabricSheet", 10, w => fixture.Id(w, 20));
        fixture.Add("Other", 10);
        fixture.Add("FabricSheet", 11, w => fixture.Id(w, 20), storedOwner: 18);
        fixture.Add("FabricSheet", 12, w => { fixture.Id(w, 20); w.Write((byte)0); });
        fixture.Add("FabricSheetType", 20);

        Assert.All(new[] { 10, 11, 12 }, id => Assert.Equal(99, fixture.TypeOf(id)));
    }

    [Theory]
    [InlineData("LinearDimString", "DimensionStyle", -1)]
    [InlineData("TextNote", "TextNoteAttributes", -1)]
    [InlineData("SWall", "BasicWallType", 30)]
    public void KeepCopiesRestoresOnlyTypeIdFromASingleUnanchoredRecord(string kind, string target, int oldType)
    {
        var fixture = new Fixture();
        fixture.Add(kind, 10, w => fixture.Id(w, 20), partition: 1);
        fixture.Add(target, 20, partition: 0);
        var element = new Element { Id = 10, TypeId = oldType, Name = "Stored name", HostId = 45 };
        element.Parameters.Add(new Parameter { Value = "Stored parameter" });

        Assert.Equal(20, fixture.Apply(element, recoverUnanchoredTypes: true));
        Assert.Equal("Stored name", element.Name);
        Assert.Equal(45, element.HostId);
        Assert.Equal("Stored parameter", Assert.Single(element.Parameters).Value);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, false, true)]
    public void UnanchoredWallRequiresKeepModeAnUnambiguousRootAndARealType(
        bool keepCopies, bool anchored, bool otherCopy, bool wrongTarget)
    {
        var fixture = new Fixture();
        fixture.Add("SWall", 10, w => fixture.Id(w, 20), partition: 1);
        if (otherCopy) fixture.Add("SWall", 10, w => fixture.Id(w, 30), partition: 0);
        if (wrongTarget) fixture.Add("SWall", 20, w => fixture.Id(w, 30));
        else fixture.Add("BasicWallType", 20);
        fixture.Add("BasicWallType", 30);

        var element = new Element { Id = 10, TypeId = 30 };
        Assert.Equal(30, fixture.Apply(element, recoverUnanchoredTypes: keepCopies, anchored: anchored));
    }

    [Theory]
    [InlineData("SWall", "BasicWallType", 20L, 4)]
    [InlineData("SWall", "NewCurtainWallType", 20L, 4)]
    [InlineData("SWall", "StackedWallType", 20L, 4)]
    [InlineData("FamilyInstance", "SysPanelFamSym", 2147483668L, 8)]
    [InlineData("RoomTag", "FamilySymbol", 20L, 4)]
    [InlineData("ProfileRoof", "RoofAttributes", 20L, 4)]
    [InlineData("LinearDimString", "DimensionStyle", 20L, 4)]
    [InlineData("Floor", "FloorAttributes", 20L, 4)]
    [InlineData("DBViewPlan", "DBViewType", 20L, 4)]
    [InlineData("DBViewSection", "DBViewType", 20L, 4)]
    [InlineData("BaseRailing", "StairsRailingAttr", 20L, 4)]
    public void KeepCopiesUsesTheRoutedTypeWithoutReplacingOtherFields(string kind, string target, long type, int width)
    {
        var fixture = new Fixture(width);
        void WriteOccurrence(BinaryWriter writer, long storedType)
        {
            fixture.Id(writer, storedType);
            if (kind is "DBViewPlan" or "DBViewSection") fixture.Id(writer, -1);
        }

        fixture.Add(kind, 10, w => WriteOccurrence(w, 30), partition: 0);
        fixture.Add(kind, 10, w => WriteOccurrence(w, type), partition: 1);
        fixture.Add(target, type);
        var element = new Element { Id = 10, TypeId = 30, Name = "Historical copy", HostId = 40 };

        Assert.Equal(type, fixture.Apply(element, wasRouted: true, recoverUnanchoredTypes: true, anchored: true));
        Assert.Equal("Historical copy", element.Name);
        Assert.Equal(40, element.HostId);
    }

    [Theory]
    [InlineData("ordinary mode")]
    [InlineData("no route")]
    [InlineData("missing current partition")]
    [InlineData("duplicate current partition")]
    [InlineData("wrong owner")]
    [InlineData("different root")]
    [InlineData("missing type")]
    [InlineData("different target class")]
    [InlineData("unsupported target copy")]
    [InlineData("unrelated old type")]
    public void RoutedTypeRequiresExactCopiesAStoredOldTypeAndARealTarget(string reason)
    {
        var fixture = new Fixture();
        fixture.Add("SWall", 10, w => fixture.Id(w, 30), partition: 0);
        if (reason == "different root") fixture.Add("Other", 10, partition: 1);
        else fixture.Add("SWall", 10, w => fixture.Id(w, 20),
            storedOwner: reason == "wrong owner" ? 11 : null, partition: reason == "missing current partition" ? 2 : 1);
        if (reason == "duplicate current partition")
            fixture.Add("SWall", 10, w => fixture.Id(w, 20), partition: 1);
        if (reason != "missing type")
            fixture.Add(reason == "different target class" ? "DimensionStyle" : "BasicWallType", 20);
        if (reason == "unsupported target copy") fixture.Add("Other", 20);
        fixture.Add("BasicWallType", 30);

        int oldType = reason == "unrelated old type" ? 40 : 30;
        var element = new Element { Id = 10, TypeId = oldType };
        Assert.Equal(oldType, fixture.Apply(element, wasRouted: reason != "no route",
            recoverUnanchoredTypes: reason != "ordinary mode", anchored: true));
    }

    [Theory]
    [InlineData(20, 20)]
    [InlineData(-1, 30)]
    public void RoutedGroupTypeComesFromIts2022MembershipCell(int currentType, int expectedType)
    {
        using var resource = typeof(TfBlockDeserializer).Assembly.GetManifestResourceStream("RvtIO.Resources.r22_schemas.json")!;
        var schema = TfSchemaReader.ReadShipped2022(resource);
        // The shipped table preserves the transform dimensions, but omits the template argument.
        var fields = schema.Classes.Single(cls => cls.Name == "Trf").Fields;
        int matrix = fields.FindIndex(field => field.Name == "m_3x3");
        fields[matrix] = fields[matrix] with { Args = [new("", 7, 16, 0, 3, null, null, null)] };
        var fixture = new Fixture(schema: schema);

        void AddGroup(int type, int partition)
        {
            fixture.AddBlock("ElementGroup", 10, writer =>
            {
                foreach (var field in TfBlockDeserializer.SerializationFields(1322)!)
                {
                    if (field is { Declaring: 37, Name: "m_cellList" }) fixture.Pointer(writer, "CellList");
                    else if (field is { Declaring: 37, Name: "m_id" }) writer.Write(10);
                    else WriteDefault2022Field(writer, field);
                }
                writer.Write(1u);
                fixture.Pointer(writer, "GroupMembership");
                writer.Write(type);
            }, partition);
        }

        AddGroup(30, 0);
        AddGroup(currentType, 1);
        fixture.AddBlock("ElementGroupType", 20, writer =>
        {
            foreach (var field in TfBlockDeserializer.SerializationFields(1333)!)
                if (field is { Declaring: 37, Name: "m_id" }) writer.Write(20);
                else WriteDefault2022Field(writer, field);
        });

        Assert.Equal(expectedType, fixture.Apply(new Element { Id = 10, TypeId = 30 },
            wasRouted: true, recoverUnanchoredTypes: true));
    }

    private static void WriteDefault2022Field(BinaryWriter writer, TfBlockDeserializer.TfField field)
    {
        if (field.Mods >> 4 == 5) { writer.Write(0u); return; }
        int count = field.Mods >> 4 == 1 ? field.Cnt!.Value : 1;
        for (int i = 0; i < count; i++)
            switch (field.Type)
            {
                case 1: writer.Write((byte)0); break;
                case 4: writer.Write(0); break;
                case 7: writer.Write(0d); break;
                case 13 when field is { Declaring: 78, Name: "m_3x3" }:
                    for (int column = 0; column < 3; column++) writer.Write(i == column ? 1d : 0d);
                    break;
                case 14 when (field.Mods & 15) == 0 && field.Ref is int reference && reference != 20:
                    foreach (var nested in TfBlockDeserializer.SerializationFields(reference)!) WriteDefault2022Field(writer, nested);
                    break;
                case 14: writer.Write(field.Ref == 20 ? -1 : 0); break;
                default: throw new InvalidOperationException(field.Name);
            }
    }

    private sealed class Fixture
    {
        private readonly SchemaDocument _schema;
        private readonly List<PartitionFraming.Framed?> _frames = [];
        private readonly HashSet<long> _ids = [];
        private readonly int _width;

        internal Fixture(int width = 4, SchemaDocument? schema = null, bool familyVisibility = false, bool detailSketch = false)
        {
            _width = width;
            _schema = schema ?? new();
            if (schema != null) return;
            int identifier = Class("Identifier", 0, new SchemaField("m_id", (byte)(width == 8 ? 11 : 4), 0, 0, null, null, null, null));
            int elementId = Class("ElementId", 0, new SchemaField("m_id", 14, 0, 0, null, identifier, null, null));
            SchemaField Ref(string field, byte mods = 0) => new(field, 14, mods, 0, null, elementId, null, null);
            SchemaField Int(string field) => new(field, 4, 0, 0, null, null, null, null);
            SchemaField Bool(string field) => new(field, 1, 0, 0, null, null, null, null);
            SchemaField Owned(string field) => new(field, 14, 1, 0, null, null, null, null);
            int element = familyVisibility
                ? Class("Element", 0, Ref("m_id"), Ref("m_famId"), Owned("m_pParamValueSetInt"))
                : detailSketch ? Class("Element", 0, Ref("m_id"), Ref("m_famId"), Owned("m_cellList"))
                : Class("Element", 0, Ref("m_id"), Ref("m_famId"));
            Class("Family", element);
            Class("PropertySetElement", element);
            int centerLine = Class("CenterLine", element, Ref("m_OwnerId"));
            foreach (string kind in new[] { "SegmentCenterLine", "PipeFittingCenterLine", "DuctFittingCenterLine" })
                Class(kind, centerLine);
            Class("RbsPipeCurve", element);
            int parameterElement = Class("ParamElem", element);
            Class("ParamElemFamily", parameterElement);
            Class("ParamElemProject", parameterElement);
            Class("ParamBinding", element);
            Class("SelectionFilterElement", Class("FilterElement", element));
            Class("FamilySurrogate", Class("FamilySurrogateBase", element));
            Class("NonPlanarSketch", Class("SketchBase", element));
            int view = Class("DBView", element, Ref("m_dbViewTypeId"), Ref("m_viewerId"));
            int viewer = Class("Viewer", element, Ref("m_dbViewId"), new SchemaField("m_bIsViewerReference", 1, 0, 0, null, null, null, null));
            Class("Viewer3d", viewer);
            Class("Section", viewer);
            Class("DBView3d", view);
            Class("DBViewPlan", view);
            Class("DBViewSection", view);
            Class("DBViewSchedule", view);
            Class("DBViewLegend", view);
            Class("DBViewProject", view);
            Class("RbsDbViewSystemNavigator", view);
            foreach (string kind in new[] { "EnergyAnalysisSurface", "EnergyAnalysisOpening", "LegendComponent", "AreaMeasureElem",
                "PlanRegion", "RefPlane", "AnalysisDisplayStyle", "CurtainGridLine", "Opening", "ReferencePoint",
                "ScheduleInstance", "BuildingOperatingYearSchedule", "DPart", "FaceSplitter", "PartMaker", "Matchline",
                "Property", "RevisionCloud", "ShaftOpening", "BoundaryConditions", "ProjectPhase",
                "GStyleElem", "CategoryElem", "FontElem", "SketchPlane", "PostedWarningElem", "DBDrawing",
                "AppearanceAssetElem", "ExtentElem", "FillPatternElem", "RefTableElem", "LinePatternElem",
                "FamSymSurrogate", "NestedFamSymRefElem", "KeynoteTable" })
                Class(kind, element);
            Class("WallSweep", element, Ref("m_typeId"));
            int symbol = Class("Symbol", element);
            Class("Viewport", element, Ref("m_attrId"));
            Class("ViewportAttributes", symbol);
            Class("DBViewTypeAreaPlan", Class("DBViewType", symbol));
            Class("InteriorElevArrow", viewer, Ref("m_parentId"));
            Class("InteriorElev", element, Ref("m_symbolId"), Ref("m_arrowIds", 0x50));
            int stairPart = Class("StairsRunAndLanding", element, Ref("m_stairsId"));
            Class("StairsRun", stairPart);
            Class("StairsLanding", stairPart);
            Class("StairsElement", element, Ref("m_typeId"));
            Class("StairsType", element, Ref("m_runTypeId"), Ref("m_landingTypeId"), Ref("m_leftSideSupportTypeId"),
                Ref("m_rightSideSupportTypeId"), Int("m_leftSupportStyle"), Int("m_rightSupportStyle"), Bool("m_hasIntermediateSupport"));
            Class("StairsRunType", element);
            Class("StairsLandingType", element);
            Class("StairsSupport", element, Ref("m_hostCompId"), Bool("m_isTypeOverridden"));
            Class("StairsSupportType", element);
            Class("FabricSheet", element, Ref("m_sheetTypeId"));
            Class("FabricSheetType", element);
            int rail = Class("ContinuousRail", element, Ref("m_typeId"));
            foreach (string kind in new[] { "HandRail", "TopRail" })
            {
                Class(kind, rail);
                Class(kind + "Type", element);
            }
            int tag = Class("IndependentTag", element, Ref("m_symbolId"));
            Class("SpanDirectionSymbol", tag, Ref("m_actualSymbolId"));
            Class("FamilyInstance", element, Ref("m_masterSymbolId"));
            Class("SysPanelFamSym", Class("FamilySymbol", symbol));
            Class("MasterImportSymbol", symbol);
            Class("SlaveImportSymbol", symbol);
            Class("GridChain", element, Ref("m_typeId"));
            Class("GridAttributes", element);
            Class("RebarContainer", element, Ref("m_rebarContainerTypeId"));
            Class("RebarContainerType", element);
            Class("Rebar", element, Owned("m_other"), Owned("m_oInstance"));
            Class("RebarInstance", Class("RebarInstBase", 0, Ref("m_barTypeId")));
            Class("RebarBarType", element);
            Class("RoofSoffit", Class("Ceiling", element, Ref("m_ceilingAttributesId")));
            Class("RoofSoffitType", element);
            Class("Ramp", element, Ref("m_rampAttrId"));
            Class("RampAttributes", element);
            Class("RoomTag", element, Ref("m_symbolId"));
            Class("ProfileRoof", Class("RoofBase", element, Ref("m_roofAttributesId")));
            Class("RoofAttributes", element);
            Class("Floor", element, Ref("m_floorAttributesId"));
            Class("FloorAttributes", element);
            Class("BaseRailing", element, Ref("m_stairsRailingAttrId"));
            Class("StairsRailingAttr", element);
            Class("LinearDimString", Class("Dimension", element, Ref("m_styleSymbolId")));
            Class("DimensionStyle", element);
            Class("TextNote", Class("TextElement", element, Ref("m_attrId")));
            Class("TextNoteAttributes", Class("TextElementAttributes", symbol));
            Class("SWall", Class("VWall", element, Ref("m_WallAttributesId")));
            Class("BasicWallType", element);
            Class("NewCurtainWallType", element);
            Class("StackedWallType", element);
            Class("Other", element);
            int curveData = Class("CurveElemData", 0, Owned("m_oUserData"));
            var data = new SchemaField("m_data", 14, 0, 0, null, curveData, null, null);
            if (familyVisibility)
            {
                int visibility = Class("FamElemVisibility", 0, Int("m_flags"));
                int parameter = Class("ParamValueInt", 0, Int("m_paramId"), Int("m_value"));
                Class("ParamValueSetInt", 0, new SchemaField("m_paramSet", 14, 0x50, 0, null, parameter, null, null));
                Class("CurveElem", element, Bool("m_detail"), data,
                    new SchemaField("m_visibility", 14, 0, 0, null, visibility, null, null));
            }
            else if (detailSketch)
            {
                Class("CurveElem", element, Bool("m_detail"), data, Ref("m_sketchPlaneId"));
                Class("VarSketch", Class("Sketch", element, Ref("m_sketchPlaneId")));
                Class("CellList", 0, Owned("m_first"), Owned("m_second"));
                Class("SketchMembership", Class("GroupMembership", 0, Ref("m_groupId")));
            }
            else Class("CurveElem", element, Bool("m_detail"), data);
            Class("FilledRegionCurveData", 0);
            Class("RailingSketchCurveData", 0);
            Class("FabricAreaCurveData", 0);
            Class("StairsCurveData", 0);
            Class("AreaMeasureCurveData", 0);
            Class("JoistSystemDirSpanData", 0);
            Class("ElementHeader", 0, Ref("m_categroryId"));
        }

        private int Class(string name, int baseId, params SchemaField[] fields)
        {
            int id = 12 + _schema.Classes.Count;
            _schema.Classes.Add(new(id, name, baseId, 1, [.. fields], []));
            return id;
        }

        internal void Id(BinaryWriter writer, long id)
        {
            if (_width == 4) writer.Write(checked((int)id));
            else writer.Write(id);
        }

        internal void StairsType(BinaryWriter writer, long run, long landing, long left = -1, long right = -1, bool intermediate = false)
        {
            Id(writer, run);
            Id(writer, landing);
            Id(writer, left);
            Id(writer, right);
            writer.Write(1);
            writer.Write(1);
            writer.Write(intermediate);
        }

        internal void Pointer(BinaryWriter writer, string? kind)
        {
            writer.Write(kind == null ? 0u : uint.MaxValue);
            if (kind != null) writer.Write((ushort)_schema.Classes.Single(cls => cls.Name == kind).Id);
        }

        internal void AddFamilyLine(long owner, long family, int flags, params BuiltInParameter[] keys)
            => Add("CurveElem", owner, w =>
            {
                Pointer(w, "ParamValueSetInt");
                w.Write(false);
                Pointer(w, null);
                w.Write(flags);
                w.Write(keys.Length);
                foreach (var key in keys)
                {
                    w.Write((int)key);
                    w.Write(0);
                }
            }, family: family);

        internal void Add(string kind, long owner, Action<BinaryWriter>? payload = null, long? storedOwner = null,
            int? partition = null, long family = -1)
            => AddBlock(kind, owner, writer =>
            {
                Id(writer, storedOwner ?? owner);
                Id(writer, family);
                payload?.Invoke(writer);
            }, partition);

        internal void AddHeader(long owner, BuiltInCategory category, bool trailingByte = false, int? partition = null)
            => AddHeaderId(owner, (int)category, trailingByte, partition);

        internal void AddHeaderId(long owner, long category, bool trailingByte = false, int? partition = null)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            Id(writer, owner);
            uint size = (uint)(_width + sizeof(ushort) + (trailingByte ? 1 : 0));
            writer.Write(size);
            writer.Write((ushort)_schema.Classes.Single(cls => cls.Name == "ElementHeader").Id);
            Id(writer, category);
            if (trailingByte) writer.Write((byte)0);
            writer.Write(size);
            byte[] bytes = stream.ToArray();
            _frames.Add(new([bytes], [new(101, (ulong)owner, 0, bytes.Length)], _width, partition));
        }

        internal void AddBlock(string kind, long owner, Action<BinaryWriter> payload, int? partition = null)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            Id(writer, owner);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write((ushort)_schema.Classes.Single(cls => cls.Name == kind).Id);
            payload(writer);
            uint size = checked((uint)stream.Length - (uint)_width - 8);
            writer.Write(size);
            stream.Position = _width + 4;
            writer.Write(size);
            byte[] bytes = stream.ToArray();
            _frames.Add(new([bytes], [new(102, (ulong)owner, 0, bytes.Length)], _width, partition));
            _ids.Add(owner);
        }

        internal long TypeOf(int owner, BuiltInCategory category = BuiltInCategory.INVALID, bool wasRouted = false, int? stairOwner = null)
            => Apply(new Element { Id = owner, TypeId = 99, CategoryId = (int)category }, wasRouted, stairOwner);

        internal long Apply(Element element, bool wasRouted = false, int? stairOwner = null,
            IReadOnlyDictionary<ulong, int>? typePartitions = null, bool recoverUnanchoredTypes = false, bool anchored = false,
            bool englishCurveNames = false, bool applyTypeLinks = true, long? routedView = null,
            bool projectCategories = true)
        {
            var ids = new ElementIdMap(_ids.Select(id => new ElemTableLayout.Row(id, id, 0, 0, 0)).ToArray());
            var state = new ParsingState { ElementIds = ids, ElemTable = _ids.ToDictionary(id => (uint)ids.Key(id), _ => 0u) };
            if (wasRouted) state.IncrementRoutedPartitions = new Dictionary<ulong, int> { [(uint)element.Id] = 1 };
            if (routedView is long view) state.IncrementRoutedPartitions = new Dictionary<ulong, int> { [(uint)ids.Key(view)] = 1 };
            if (stairOwner is int stair) state.ElemRecOwningElementIds = new Dictionary<uint, int> { [(uint)element.Id] = stair };
            if (anchored) state.NativeParamsFromRootClass.Add((uint)element.Id);
            FramedElementTypes.Apply(_schema, new Dictionary<Guid, SchemaField[]?>(), _frames, state, [element],
                typePartitions, recoverUnanchoredTypes, englishCurveNames, applyTypeLinks, projectCategories);
            new RevitFileInfo { ElementIds = ids, Elements = [element] }.AttachSourceElementIds();
            return ids.SourceId(element.TypeId);
        }
    }
}
