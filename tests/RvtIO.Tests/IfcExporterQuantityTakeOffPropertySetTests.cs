using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using System.Text.RegularExpressions;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Pset_QuantityTakeOff: one Pset, one property (Reference = the element's type name), attached to every tagged product on the default export path (independent of --ifc-revit-parameters).</summary>
public class IfcExporterQuantityTakeOffPropertySetTests
{
    [Fact]
    public void QualifyingElement_GetsOwnPsetAttachedToItsOwnEntity()
    {
        var beam = Beam(id: 200, typeId: 201, name: "BeamType_200x300", centreX: 0);
        var info = new RevitFileInfo();
        info.Elements.Add(beam);
        string path = TempIfcPath("qto-single");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            // Exactly one qualifying element in the model -> exactly one attachment, even
            // though the file also always contains an IfcProject/IfcSite/IfcBuilding/
            // IfcBuildingStorey spatial hierarchy that must NOT receive this Pset.
            Assert.Single(Regex.Matches(ifc, "'Pset_QuantityTakeOff'"));

            int beamId = SoleEntityId(ifc, "IFCBEAM");
            (int psetId, int valueId) = SoleQuantityTakeOffPset(ifc);
            int relatedId = SoleRelDefinesByPropertiesTarget(ifc, psetId);
            Assert.Equal(beamId, relatedId);

            string referenceLiteral = ReferenceLiteral(ifc, valueId);
            Assert.Equal("BeamType_200x300", referenceLiteral);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void NamelessElement_ReceivesNoPsetInsteadOfAPlaceholder()
    {
        var beam = Beam(id: 240, typeId: 241, name: null, centreX: 0);
        var info = new RevitFileInfo();
        info.Elements.Add(beam);
        string path = TempIfcPath("qto-nameless");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            // The beam entity itself is still exported...
            int beamId = SoleEntityId(ifc, "IFCBEAM");
            Assert.True(beamId > 0);
            // ...but carries no Pset_QuantityTakeOff at all - not with a placeholder value,
            // not with any value. It is the only element in the file, so this also confirms no
            // OTHER element's attachment is somehow rerouted here.
            Assert.DoesNotContain("Pset_QuantityTakeOff", ifc);
            // The placeholder text must not appear as an IFCIDENTIFIER literal (the shape a Reference property value takes). The IFCBEAM Name attribute still falls back to the same "Element {id}" text, so the check is specific to the Pset value shape.
            Assert.DoesNotContain($"IFCIDENTIFIER('Element {beam.Id}')", ifc);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void TwoElementsWithSameTypeName_ShareOnePropertySingleValueInstance()
    {
        var beamA = Beam(id: 210, typeId: 211, name: "BeamType_200x300", centreX: 0);
        var beamB = Beam(id: 212, typeId: 213, name: "BeamType_200x300", centreX: 20);
        var info = new RevitFileInfo();
        info.Elements.Add(beamA);
        info.Elements.Add(beamB);
        string path = TempIfcPath("qto-shared-value");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Equal(2, Regex.Matches(ifc, "'Pset_QuantityTakeOff'").Count);
            Assert.Single(Regex.Matches(
                ifc,
                @"IFCPROPERTYSINGLEVALUE\('Reference',(?:\$|''),IFCIDENTIFIER\('BeamType_200x300'\),\$\)"));

            var psetIds = QuantityTakeOffPsetIds(ifc);
            Assert.Equal(2, psetIds.Count);
            // The two IfcPropertySet instances are themselves distinct entities (each
            // element gets its own set) even though they share the referenced value.
            Assert.NotEqual(psetIds[0].psetId, psetIds[1].psetId);
            Assert.Equal(psetIds[0].valueId, psetIds[1].valueId);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void TwoElementsWithDifferentTypeNames_GetDistinctPropertySingleValues()
    {
        var beamA = Beam(id: 220, typeId: 221, name: "TypeAlpha", centreX: 0);
        var beamB = Beam(id: 222, typeId: 223, name: "TypeBeta", centreX: 20);
        var info = new RevitFileInfo();
        info.Elements.Add(beamA);
        info.Elements.Add(beamB);
        string path = TempIfcPath("qto-distinct-values");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Equal(2, Regex.Matches(
                ifc,
                @"IFCPROPERTYSINGLEVALUE\('Reference',(?:\$|''),IFCIDENTIFIER\(").Count);
            Assert.Contains("IFCIDENTIFIER('TypeAlpha')", ifc);
            Assert.Contains("IFCIDENTIFIER('TypeBeta')", ifc);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void RawParameterOptIn_DoesNotAffectOrDuplicateTheDefaultPset()
    {
        var beam = Beam(id: 230, typeId: 231, name: "BeamType_200x300", centreX: 0);
        beam.Parameters.Add(new Parameter
        {
            Id = -1099999,
            Definition = new Definition { Name = "Comments" },
            StorageType = StorageType.String,
            Value = "some raw parameter value",
        });
        var infoDefault = new RevitFileInfo();
        infoDefault.Elements.Add(beam);
        string pathDefault = TempIfcPath("qto-flag-off");
        string pathOptIn = TempIfcPath("qto-flag-on");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(infoDefault, pathDefault);
            string ifcDefault = File.ReadAllText(pathDefault);
            Assert.Single(Regex.Matches(ifcDefault, "'Pset_QuantityTakeOff'"));
            Assert.DoesNotContain("RevitParameters", ifcDefault);

            var infoOptIn = new RevitFileInfo();
            infoOptIn.Elements.Add(beam);
            new IfcExporter(includeRevitPropertySets: true) { Profile = IfcProfile.Legacy }.Export(infoOptIn, pathOptIn);
            string ifcOptIn = File.ReadAllText(pathOptIn);

            // --ifc-revit-parameters adds its own separate "RevitParameters" Pset but must
            // not duplicate or otherwise disturb Pset_QuantityTakeOff.
            Assert.Single(Regex.Matches(ifcOptIn, "'Pset_QuantityTakeOff'"));
            Assert.Contains("'RevitParameters'", ifcOptIn);
        }
        finally { Cleanup(pathDefault); Cleanup(pathOptIn); }
    }

    [Fact]
    public void RevitParameterGroups_WritesApiNamesInGroupPropertySets()
    {
        var beam = Beam(id: 240, typeId: 241, name: "BeamType_200x300", centreX: 0);
        beam.Parameters.Add(new Parameter
        {
            Id = -1012101, // PHASE_DEMOLISHED: public API name "Phase Demolished", group Phasing
            Definition = new Definition { Name = "PHASE_DEMOLISHED" },
            StorageType = StorageType.Integer,
            Value = 7,
        });
        beam.Parameters.Add(new Parameter
        {
            Id = -1099998, // not a captured built-in: keeps its own name, group Other
            Definition = new Definition { Name = "CustomCode" },
            StorageType = StorageType.String,
            Value = "x",
        });
        var info = new RevitFileInfo();
        info.Elements.Add(beam);
        string path = TempIfcPath("revit-parameter-groups");

        try
        {
            new IfcExporter(includeRevitPropertySets: true) { RevitParameterGroups = true, Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.DoesNotContain("'RevitParameters'", ifc);
            Assert.Single(Regex.Matches(ifc, @"IFCPROPERTYSET\('[^']*',#\d+,'Phasing'"));
            Assert.Single(Regex.Matches(ifc, @"IFCPROPERTYSET\('[^']*',#\d+,'Other'"));
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Phase Demolished',$,IFCINTEGER(7),$)", ifc);
            Assert.Contains("IFCPROPERTYSINGLEVALUE('CustomCode',$,IFCLABEL('x'),$)", ifc);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void RevitParameterGroups_TypesValuesBySpecAndResolvesReferences()
    {
        var beam = Beam(id: 260, typeId: 261, name: "BeamType_200x300", centreX: 0);
        beam.Parameters.Clear();
        beam.Parameters.Add(new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = 3 }); // Phase Created
        beam.Parameters.Add(new Parameter { Id = -1001361, StorageType = StorageType.Double, Value = 10.0 }); // Sill Height, length
        beam.Parameters.Add(new Parameter { Id = -1001133, StorageType = StorageType.Double, Value = 100.0 }); // Area
        beam.Parameters.Add(new Parameter { Id = -1001007, StorageType = StorageType.Integer, Value = 1 }); // Room Bounding, bool
        beam.Parameters.Add(new Parameter { Id = -1001203, StorageType = StorageType.String, Value = "B-1" }); // Mark
        beam.Parameters.Add(new Parameter { Id = -1140362, StorageType = StorageType.ElementId, Value = -2001320 }); // Category

        beam.Parameters.Add(new Parameter { Id = -1002502, StorageType = StorageType.String, Value = "23.30" }); // OmniClass Number
        beam.Parameters.Add(new Parameter { Id = -1002503, StorageType = StorageType.String, Value = "Doors" }); // OmniClass Title
        var info = new RevitFileInfo();
        info.Elements.Add(new Element { Id = 3, CategoryId = -2003000, Name = "New Construction" });
        info.Elements.Add(beam);
        string path = TempIfcPath("revit-parameter-values");

        try
        {
            new IfcExporter(includeRevitPropertySets: true) { RevitParameterGroups = true, Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Contains("IFCPROPERTYSINGLEVALUE('Phase Created',$,IFCLABEL('New Construction'),$)", ifc);
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Sill Height',$,IFCLENGTHMEASURE(3048.),$)", ifc);
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Area',$,IFCAREAMEASURE(9.290304),$)", ifc);
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Room Bounding',$,IFCBOOLEAN(.T.),$)", ifc);
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Mark',$,IFCTEXT('B-1'),$)", ifc);
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Category',$,IFCLABEL('OST_StructuralFraming'),$)", ifc);
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Classification Number',$,IFCTEXT('23.30'),$)", ifc);
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Classification Title',$,IFCTEXT('Doors'),$)", ifc);
        }
        finally { Cleanup(path); }
    }

    [Theory]
    [InlineData(IfcSchema.Ifc2x3)]
    [InlineData(IfcSchema.Ifc4)]
    public void RevitParameterGroups_GivesEveryMemberOfANameClashItsParameterId(IfcSchema schema)
    {
        var beam = Beam(id: 280, typeId: 281, name: "BeamType_200x300", centreX: 0);
        // distinct built-ins that the public API gives one name in one group
        beam.Parameters.Add(new Parameter { Id = -1002066, StorageType = StorageType.Double, Value = 10.0 }); // Top Offset
        beam.Parameters.Add(new Parameter { Id = -1001358, StorageType = StorageType.Double, Value = 20.0 }); // Top Offset
        // one name in two groups lands in two sets, which is no clash
        beam.Parameters.Add(new Parameter { Id = -1002062, StorageType = StorageType.String, Value = "L1" }); // Level, Constraints
        beam.Parameters.Add(new Parameter { Id = -1012014, StorageType = StorageType.String, Value = "L2" }); // Level, Dimensions
        // one ID twice: the ID alone does not tell them apart
        beam.Parameters.Add(new Parameter { Id = -1001203, StorageType = StorageType.String, Value = "A" }); // Mark
        beam.Parameters.Add(new Parameter { Id = -1001203, StorageType = StorageType.String, Value = "B" });
        var info = new RevitFileInfo();
        info.Elements.Add(beam);
        string path = TempIfcPath("revit-parameter-name-clash");

        try
        {
            new IfcExporter(includeRevitPropertySets: true)
            {
                RevitParameterGroups = true,
                Profile = IfcProfile.Legacy,
                Schema = schema,
            }.Export(info, path);
            string ifc = File.ReadAllText(path);

            AssertUniquePropertyNamesPerSet(ifc);
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Phase Created',", ifc); // unique: stays plain
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Top Offset [-1002066]',$,IFCLENGTHMEASURE(3048.),$)", ifc);
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Top Offset [-1001358]',$,IFCLENGTHMEASURE(6096.),$)", ifc);
            Assert.Equal(2, Regex.Matches(ifc, @"IFCPROPERTYSINGLEVALUE\('Level',").Count);
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Mark [-1001203]',$,IFCTEXT('A'),$)", ifc);
            Assert.Contains("IFCPROPERTYSINGLEVALUE('Mark [-1001203] #2',$,IFCTEXT('B'),$)", ifc);
        }
        finally { Cleanup(path); }
    }

    private static void AssertUniquePropertyNamesPerSet(string ifc)
    {
        var names = Regex.Matches(ifc, @"#(\d+)=IFCPROPERTYSINGLEVALUE\('((?:[^']|'')*)'")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        var sets = Regex.Matches(ifc, @"IFCPROPERTYSET\('[^']*',#\d+,'([^']*)',\$,\(([^)]*)\)\)");
        Assert.NotEmpty(sets);
        foreach (Match set in sets)
        {
            var members = set.Groups[2].Value.Split(',').Select(id => names[id.Trim().TrimStart('#')]).ToList();
            Assert.True(members.Distinct(StringComparer.Ordinal).Count() == members.Count,
                $"Duplicate property names in {set.Groups[1].Value}: {string.Join(", ", members)}");
        }
    }

    [Fact]
    public void FloorTypeValidation_SetsQtoReferenceAndTheProductTypeName()
    {
        var slab = Beam(id: 250, typeId: 251,
            name: "Existing slab identity", centreX: 0);
        slab.CategoryId = -2000032;
        slab.Category = "OST_Floors";
        slab.ValidatedFloorTypeReferenceName = "Canonical floor type";
        var info = new RevitFileInfo();
        info.Elements.Add(slab);
        string path = TempIfcPath("qto-floor-validation");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            var (psetId, valueId) = SoleQuantityTakeOffPset(ifc);
            Assert.Equal(SoleEntityId(ifc, "IFCSLAB"),
                SoleRelDefinesByPropertiesTarget(ifc, psetId));
            Assert.Equal("Canonical floor type", ReferenceLiteral(ifc, valueId));

            string slabEntity = Assert.Single(Regex.Matches(
                ifc, @"#\d+=IFCSLAB\([^;]+\);")).Value;
            Assert.Contains("'OST_Floors:Canonical floor type:250'", slabEntity);
            Assert.Contains("'OST_Floors:Canonical floor type'", slabEntity);
            Assert.Equal("Existing slab identity", slab.Name);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void FloorTypeValidation_StillCreatesQtoWhenTheBaselineNameHappensToMatchALevel()
    {
        // The level-name suppression must apply only to the FINAL resolved value, never to the baseline that the floor validation needs (baselineReferenceValue != null) to create an attachment, even when the slab's Name equals a Level's Name.
        var level = Level(id: 1, name: "12_+35,850_Level_Zh");
        var slab = Beam(id: 258, typeId: 259, name: "12_+35,850_Level_Zh", centreX: 0);
        slab.CategoryId = -2000032;
        slab.Category = "OST_Floors";
        slab.ValidatedFloorTypeReferenceName = "Canonical floor type";
        var info = new RevitFileInfo();
        info.Elements.Add(level);
        info.Elements.Add(slab);
        string path = TempIfcPath("qto-floor-validation-level-coincidence");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            var (psetId, valueId) = SoleQuantityTakeOffPset(ifc);
            Assert.Equal(SoleEntityId(ifc, "IFCSLAB"),
                SoleRelDefinesByPropertiesTarget(ifc, psetId));
            Assert.Equal("Canonical floor type", ReferenceLiteral(ifc, valueId));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void FloorTypeValidation_DoesNotCreateQtoWhenBaselineReferenceIsMissing()
    {
        var slab = Beam(id: 252, typeId: 253, name: null, centreX: 0);
        slab.CategoryId = -2000032;
        slab.Category = "OST_Floors";
        slab.ValidatedFloorTypeReferenceName = "Canonical floor type";
        var info = new RevitFileInfo();
        info.Elements.Add(slab);
        string path = TempIfcPath("qto-floor-validation-null-baseline");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.True(SoleEntityId(ifc, "IFCSLAB") > 0);
            Assert.DoesNotContain("Pset_QuantityTakeOff", ifc);
            Assert.DoesNotContain("IFCIDENTIFIER('Canonical floor type')", ifc);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ExplicitReference_CreatesMainFloorQtoWithoutChangingNamelessIdentity()
    {
        var slab = Beam(id: 254, typeId: 255, name: null, centreX: 0);
        slab.CategoryId = -2000032;
        slab.Category = "OST_Floors";
        slab.Parameters.Add(new Parameter
        {
            Id = 9001,
            Definition = new Definition { Id = 9001, Name = "Re fer ence" },
            StorageType = StorageType.String,
            Value = "Authored Reference",
        });
        var info = new RevitFileInfo();
        info.Elements.Add(slab);
        string path = TempIfcPath("qto-floor-explicit-reference");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            var (_, valueId) = SoleQuantityTakeOffPset(ifc);
            Assert.Equal("Authored Reference", ReferenceLiteral(ifc, valueId));
            string slabEntity = Assert.Single(Regex.Matches(
                ifc, @"#\d+=IFCSLAB\([^;]+\);")).Value;
            Assert.Contains("'OST_Floors:Element 254:254'", slabEntity);
            Assert.DoesNotContain("Authored Reference", slabEntity);
            Assert.Null(slab.Name);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void UnreproducibleExplicitReference_BlocksFloorTerminalNameFallback()
    {
        var slab = Beam(id: 256, typeId: 257, name: "Baseline", centreX: 0);
        slab.CategoryId = -2000032;
        slab.Category = "OST_Floors";
        slab.ValidatedFloorTypeReferenceName = "Terminal type name";
        slab.Parameters.Add(new Parameter
        {
            Id = 9002,
            Definition = new Definition { Id = 9002, Name = "Reference" },
            StorageType = StorageType.Double,
            Value = 12.5,
        });
        var info = new RevitFileInfo();
        info.Elements.Add(slab);
        string path = TempIfcPath("qto-floor-blocked-reference");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.True(SoleEntityId(ifc, "IFCSLAB") > 0);
            Assert.DoesNotContain("Pset_QuantityTakeOff", ifc);
            Assert.DoesNotContain("IFCIDENTIFIER('Terminal type name')", ifc);
        }
        finally { Cleanup(path); }
    }

    [Theory]
    [InlineData("IFCROOF", -2000032)]
    [InlineData("IFCSLAB", -2000035)]
    [InlineData("IFCSLAB", -2000011)]
    public void FloorTypeValidation_IsIgnoredOutsideMainFloorIfcSlab(
        string ifcType, int categoryId)
    {
        var element = new Element
        {
            CategoryId = categoryId,
            Name = "Baseline",
            ValidatedFloorTypeReferenceName = "Unrelated floor validation",
        };

        Assert.Equal("Baseline", IfcExporter.ResolveQuantityTakeOffReferenceValue(
            element, ifcType, element.Name));
    }

    [Theory]
    [InlineData(-2000032)]
    [InlineData(-2001300)]
    public void FloorTypeValidation_IsAppliedForFloorsAndStructuralFoundations(int categoryId)
    {
        var element = new Element
        {
            CategoryId = categoryId,
            Name = "Baseline",
            ValidatedFloorTypeReferenceName = "Validated floor type",
        };

        Assert.Equal("Validated floor type", IfcExporter.ResolveQuantityTakeOffReferenceValue(
            element, "IFCSLAB", element.Name));
    }

    [Theory]
    [InlineData("terminal-name", "Canonical ceiling type")]
    [InlineData("instance-reference", "Instance Reference")]
    [InlineData("type-reference", "Type Reference")]
    [InlineData("type-reference-suffix", "Type Reference suffix")]
    [InlineData("blocked-instance-reference", null)]
    public void PostProcessedRawCeilingLink_DrivesExactQtoReferencePrecedence(
        string source, string? expectedReference)
    {
        const int trueTypeId = 270;
        const int preliminaryTypeId = 271;
        const int coveringId = 272;
        var trueType = CeilingType(
            trueTypeId, "Wrong parsed true type name");
        var preliminaryType = CeilingType(
            preliminaryTypeId, "Wrong preliminary type name");
        AddStringParameter(
            preliminaryType, "Reference", "Wrong preliminary Reference", 9200);
        var covering = Beam(
            coveringId, preliminaryTypeId, "Wrong occurrence name", centreX: 0);
        covering.CategoryId = -2000038;
        covering.Category = "OST_Ceilings";
        AddTypeParameters(covering, preliminaryTypeId);

        switch (source)
        {
            case "terminal-name":
                break;
            case "instance-reference":
                AddStringParameter(
                    covering, "Reference", "Instance Reference", 9201);
                AddStringParameter(
                    trueType, "Reference", "Must lose to instance", 9202);
                break;
            case "type-reference":
                AddStringParameter(
                    trueType, "Reference", "Type Reference", 9203);
                AddStringParameter(
                    trueType, "Reference[Type]", "Must lose to Reference", 9204);
                break;
            case "type-reference-suffix":
                AddStringParameter(
                    trueType, "Reference[Type]", "Type Reference suffix", 9205);
                break;
            case "blocked-instance-reference":
                covering.Parameters.Add(new Parameter
                {
                    Id = 9206,
                    Definition = new Definition
                    {
                        Id = 9206,
                        Name = "Reference",
                    },
                    StorageType = StorageType.Double,
                    Value = 12.5,
                });
                AddStringParameter(
                    trueType, "Reference", "Must not bypass block", 9207);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(source));
        }

        var info = new RevitFileInfo();
        info.Elements.AddRange([trueType, preliminaryType, covering]);
        var state = new ParsingState();
        var nativeParams = new TfBlockDeserializer.NativeParams();
        nativeParams.Fields["m_id"] = coveringId;
        nativeParams.Fields["m_ceilingAttributesId"] = trueTypeId;
        state.NativeParams[coveringId] = nativeParams;
        state.ElemClassIdx[coveringId] = 642;
        state.CeilingTypeNameSource[trueTypeId] =
            (1, "Canonical ceiling type");

        ElementBuilder.PostProcess(info, state);

        Assert.Equal(preliminaryTypeId, covering.TypeId);
        Assert.Equal(preliminaryTypeId, covering.FamilyTypeId);
        foreach (int parameterId in new[] { -1002050, -1002000, -1002052 })
        {
            Assert.Equal(preliminaryTypeId, Assert.Single(
                covering.Parameters, parameter =>
                    parameter.Id == parameterId).Value);
        }
        Assert.Equal("Wrong preliminary type name", covering.Name);
        string path = TempIfcPath($"qto-ceiling-{source}");
        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);
            Assert.True(SoleEntityId(ifc, "IFCCOVERING") > 0);
            string coveringEntity = Assert.Single(Regex.Matches(
                ifc, @"#\d+=IFCCOVERING\([^\r\n]*\);")).Value;
            // The product is named after its type when the terminal type name is validated; an explicit Reference says nothing about it.
            string expectedTypeName = source == "terminal-name"
                ? "Canonical ceiling type" : "Wrong preliminary type name";
            Assert.Contains(
                $"'OST_Ceilings:{expectedTypeName}:{coveringId}',$,'OST_Ceilings:{expectedTypeName}'",
                coveringEntity);
            if (expectedReference == null)
            {
                Assert.DoesNotContain("'Pset_QuantityTakeOff'", ifc);
            }
            else
            {
                var (_, valueId) = SoleQuantityTakeOffPset(ifc);
                Assert.Equal(expectedReference, ReferenceLiteral(ifc, valueId));
                Assert.DoesNotContain(
                    "IFCIDENTIFIER('Wrong preliminary Reference')", ifc);
            }
        }
        finally { Cleanup(path); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PostProcessedRawCeilingLink_CorrectsQtoWithoutChangingMaterialTypeGraph(
        bool preliminaryTypeHasMaterial)
    {
        const int trueTypeId = 280;
        const int preliminaryTypeId = 281;
        const int coveringId = 282;
        const int trueMaterialId = 283;
        const int preliminaryMaterialId = 284;
        var trueType = CeilingType(trueTypeId, "Wrong parsed true type name");
        AddElementIdParameter(
            trueType, "MATERIAL_ID_PARAM", trueMaterialId, 9501);
        var preliminaryType = CeilingType(
            preliminaryTypeId, "Wrong preliminary type name");
        if (preliminaryTypeHasMaterial)
            AddElementIdParameter(
                preliminaryType, "MATERIAL_ID_PARAM", preliminaryMaterialId, 9502);
        var covering = Beam(
            coveringId, preliminaryTypeId, "Wrong occurrence name", centreX: 0);
        covering.CategoryId = -2000038;
        covering.Category = "OST_Ceilings";
        AddTypeParameters(covering, preliminaryTypeId);
        var trueMaterial = SerializedMaterial(
            trueMaterialId, "Raw linked material");
        var preliminaryMaterial = SerializedMaterial(
            preliminaryMaterialId, "Preliminary material");

        var info = new RevitFileInfo();
        info.Elements.AddRange(
            [trueType, preliminaryType, covering, trueMaterial, preliminaryMaterial]);
        var state = new ParsingState();
        var nativeParams = new TfBlockDeserializer.NativeParams();
        nativeParams.Fields["m_id"] = coveringId;
        nativeParams.Fields["m_ceilingAttributesId"] = trueTypeId;
        state.NativeParams[coveringId] = nativeParams;
        state.ElemClassIdx[coveringId] = 642;
        state.CeilingTypeNameSource[trueTypeId] =
            (1, "Canonical ceiling type");

        ElementBuilder.PostProcess(info, state);

        string path = TempIfcPath(
            $"qto-ceiling-material-{preliminaryTypeHasMaterial}");
        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);
            string coveringEntity = Assert.Single(Regex.Matches(
                ifc, @"#\d+=IFCCOVERING\([^\r\n]*\);")).Value;
            Assert.Contains(
                $"'OST_Ceilings:Canonical ceiling type:{coveringId}',$,'OST_Ceilings:Canonical ceiling type'",
                coveringEntity);
            var (_, valueId) = SoleQuantityTakeOffPset(ifc);
            Assert.Equal(
                "Canonical ceiling type", ReferenceLiteral(ifc, valueId));
            Assert.DoesNotContain("IFCMATERIAL('Raw linked material')", ifc);
            if (preliminaryTypeHasMaterial)
            {
                Assert.Contains("IFCMATERIAL('Preliminary material')", ifc);
                Assert.Single(Regex.Matches(ifc, "IFCRELASSOCIATESMATERIAL\\("));
            }
            else
            {
                Assert.DoesNotContain("IFCMATERIAL('Preliminary material')", ifc);
                Assert.Empty(Regex.Matches(ifc, "IFCRELASSOCIATESMATERIAL\\("));
            }
        }
        finally { Cleanup(path); }

        Assert.Equal(preliminaryTypeId, covering.TypeId);
        Assert.Equal(preliminaryTypeId, covering.FamilyTypeId);
        foreach (int parameterId in new[] { -1002050, -1002000, -1002052 })
        {
            Assert.Equal(preliminaryTypeId, Assert.Single(
                covering.Parameters, parameter =>
                    parameter.Id == parameterId).Value);
        }
    }

    [Theory]
    [InlineData("IFCSITE", false)]
    [InlineData("IFCWALLSTANDARDCASE", true)]
    [InlineData("IFCWALL", true)]
    [InlineData("IFCBEAM", true)]
    [InlineData("IFCOPENINGELEMENT", true)]
    [InlineData("IFCDOOR", true)]
    public void ExportsIfcTagAttribute_MatchesTheTagArmsInTheElementArgsSwitch(
        string ifcType, bool expected)
    {
        Assert.Equal(expected, IfcExporter.ExportsIfcTagAttribute(ifcType));
    }

    [Fact]
    public void ElementNameEqualsALevelName_OmitsQuantityTakeOffReferenceInsteadOfLeakingIt()
    {
        var level = Level(id: 1, name: "12_+35,850_Level_Zh");
        var beam = Beam(id: 260, typeId: 261, name: "12_+35,850_Level_Zh", centreX: 0);
        var info = new RevitFileInfo();
        info.Elements.Add(level);
        info.Elements.Add(beam);
        string path = TempIfcPath("qto-level-contaminated");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            // The beam entity itself is still exported (its own Name attribute is a
            // separate, wider question this fix does not touch - see the existing
            // NamelessElement_ReceivesNoPsetInsteadOfAPlaceholder test's rationale)...
            Assert.True(SoleEntityId(ifc, "IFCBEAM") > 0);
            // ...but its Pset_QuantityTakeOff Reference, which WOULD have been the Level's
            // own name, is omitted entirely rather than leaking a guaranteed-wrong value.
            Assert.DoesNotContain("Pset_QuantityTakeOff", ifc);
            Assert.DoesNotContain("IFCIDENTIFIER('12_+35,850_Level_Zh')", ifc);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ElementNameDiffersFromEveryLevelName_StillEmitsReferenceNormally()
    {
        var level = Level(id: 1, name: "12_+35,850_Level_Zh");
        var beam = Beam(id: 262, typeId: 263, name: "CurtainWallType_B2n_2", centreX: 0);
        var info = new RevitFileInfo();
        info.Elements.Add(level);
        info.Elements.Add(beam);
        string path = TempIfcPath("qto-level-present-not-contaminated");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            // A Level existing somewhere in the document must not suppress an unrelated
            // product's own, different Reference value - the guard is an exact-match
            // contamination signal, not a blanket "no Reference near a Level" rule.
            var (psetId, valueId) = SoleQuantityTakeOffPset(ifc);
            Assert.Equal(SoleEntityId(ifc, "IFCBEAM"),
                SoleRelDefinesByPropertiesTarget(ifc, psetId));
            Assert.Equal("CurtainWallType_B2n_2", ReferenceLiteral(ifc, valueId));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void OnlyTheElementWhoseNameMatchesALevelIsSuppressed_ItsSiblingIsUnaffected()
    {
        var level = Level(id: 1, name: "12_+35,850_Level_Zh");
        var contaminated = Beam(id: 264, typeId: 265, name: "12_+35,850_Level_Zh", centreX: 0);
        var clean = Beam(id: 266, typeId: 267, name: "CurtainWallType_B7_2", centreX: 20);
        var info = new RevitFileInfo();
        info.Elements.Add(level);
        info.Elements.Add(contaminated);
        info.Elements.Add(clean);
        string path = TempIfcPath("qto-level-selective-suppression");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            // Exactly one Pset_QuantityTakeOff in the file - the clean sibling's - even
            // though two qualifying beams exist.
            Assert.Single(Regex.Matches(ifc, "'Pset_QuantityTakeOff'"));
            var (psetId, valueId) = SoleQuantityTakeOffPset(ifc);
            Assert.Equal(SoleEntityId(ifc, "IFCBEAM", clean.Id),
                SoleRelDefinesByPropertiesTarget(ifc, psetId));
            Assert.Equal("CurtainWallType_B7_2", ReferenceLiteral(ifc, valueId));
            Assert.DoesNotContain("IFCIDENTIFIER('12_+35,850_Level_Zh')", ifc);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ExplicitReferenceParameter_IsNeverBlockedEvenIfItCoincidesWithALevelName()
    {
        // An AUTHORED Reference parameter takes precedence over the fallback entirely; the level-name contamination only reaches elem.Name through the untyped fallback path.
        // through the untyped fallback path, never through an explicit parameter.
        var level = Level(id: 1, name: "12_+35,850_Level_Zh");
        var beam = Beam(id: 268, typeId: 269, name: "Some other name", centreX: 0);
        beam.Parameters.Add(new Parameter
        {
            Id = 9001,
            Definition = new Definition { Id = 9001, Name = "Re fer ence" },
            StorageType = StorageType.String,
            Value = "12_+35,850_Level_Zh",
        });
        var info = new RevitFileInfo();
        info.Elements.Add(level);
        info.Elements.Add(beam);
        string path = TempIfcPath("qto-level-explicit-reference-not-blocked");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            var (_, valueId) = SoleQuantityTakeOffPset(ifc);
            Assert.Equal("12_+35,850_Level_Zh", ReferenceLiteral(ifc, valueId));
        }
        finally { Cleanup(path); }
    }

    /// <summary>A real, accepted project storey. Its own Name is the exact-match
    /// contamination signal ResolveIfcReferenceValue's levelNames set is built from.</summary>
    private static Element Level(int id, string name) => new()
    {
        Id = id,
        TypeId = id + 1000,
        CategoryId = -2000240,
        Category = "OST_Levels",
        Name = name,
        IsNativeLevel = true,
        SerializedFamilyId = -1,
        SerializedUnplacedOwnerId = -1,
        SerializedOwnerDbViewId = -1,
        Parameters =
        [
            new Parameter { Id = -1007102, StorageType = StorageType.Double, Value = 0.0 },
            new Parameter { Id = -1007111, StorageType = StorageType.Integer, Value = 1 },
        ],
    };

    /// <summary>Finds the entity id of the given IFC type whose Tag attribute (the final
    /// string literal before the closing paren) equals <paramref name="elementId"/> - used
    /// instead of SoleEntityId when more than one instance of that type is exported.</summary>
    private static int SoleEntityId(string ifc, string ifcType, int elementId)
    {
        var pattern = new Regex($@"#(\d+)={ifcType}\([^;]*'{elementId}'\);");
        var match = Assert.Single(pattern.Matches(ifc));
        return int.Parse(match.Groups[1].Value);
    }

    private static Element Beam(int id, int typeId, string? name, double centreX)
    {
        double[] axis = [0, 1, 0];
        double[] profileX = [1, 0, 0];
        double[] centre = [centreX, 5, 4];
        return new Element
        {
            Id = id,
            TypeId = typeId,
            IsDiscovered = true,
            CategoryId = -2001320,
            Category = "OST_StructuralFraming",
            Name = name,
            References = [3],
            Parameters =
            [
                new Parameter
                {
                    Id = -1012100,
                    StorageType = StorageType.ElementId,
                    Value = 3,
                },
            ],
            BoundingBox = PrismBounds(centre, axis, profileX, length: 10, width: 0.4, depth: 0.6),
            Geometry = RectangularPrism(centre, axis, profileX, length: 9.2, width: 0.4, depth: 0.6),
        };
    }

    private static Element CeilingType(int id, string name) => new()
    {
        Id = id,
        TypeId = -1,
        IsBlockOnly = true,
        CategoryId = -2000038,
        Category = "OST_Ceilings",
        Name = name,
    };

    private static void AddStringParameter(
        Element element, string name, string value, int id)
    {
        element.Parameters.Add(new Parameter
        {
            Id = id,
            Definition = new Definition { Id = id, Name = name },
            StorageType = StorageType.String,
            Value = value,
        });
    }

    private static void AddElementIdParameter(
        Element element, string name, int value, int id)
    {
        element.Parameters.Add(new Parameter
        {
            Id = id,
            Definition = new Definition { Id = id, Name = name },
            StorageType = StorageType.ElementId,
            Value = value,
        });
    }

    private static void AddTypeParameters(Element element, int typeId)
    {
        foreach (int parameterId in new[] { -1002050, -1002000, -1002052 })
        {
            element.Parameters.Add(new Parameter
            {
                Id = parameterId,
                Definition = new Definition
                {
                    Id = parameterId,
                    Name = parameterId == -1002050
                        ? "ELEM_TYPE_PARAM"
                        : parameterId.ToString(),
                },
                StorageType = StorageType.ElementId,
                Value = typeId,
            });
        }
    }

    private static Element SerializedMaterial(int id, string name)
    {
        var material = new Element
        {
            Id = id,
            TypeId = -1,
            IsBlockOnly = true,
            CategoryId = -2000700,
            Category = "OST_Materials",
            Name = name,
            HasShadingAppearance = true,
        };
        material.Parameters.Add(new Parameter
        {
            Id = 9300 + id,
            Definition = new Definition
            {
                Id = 9300 + id,
                Name = "MATERIAL_PARAM_COLOR",
            },
            StorageType = StorageType.Integer,
            Value = 0x112233,
        });
        material.Parameters.Add(new Parameter
        {
            Id = 9400 + id,
            Definition = new Definition
            {
                Id = 9400 + id,
                Name = "MATERIAL_PARAM_SHININESS",
            },
            StorageType = StorageType.Integer,
            Value = 32,
        });
        return material;
    }

    private static string TempIfcPath(string tag) =>
        Path.Combine(Path.GetTempPath(), $"revitreader-{tag}-{Guid.NewGuid():N}.ifc");

    private static void Cleanup(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Finds the single "#id=TYPE(" entity of the given IFC type and returns its id.</summary>
    private static int SoleEntityId(string ifc, string ifcType)
    {
        var match = Assert.Single(Regex.Matches(ifc, $@"#(\d+)={ifcType}\("));
        return int.Parse(match.Groups[1].Value);
    }

    /// <summary>Finds the single Pset_QuantityTakeOff IFCPROPERTYSET and returns
    /// (its own entity id, the id of the single referenced value it carries).</summary>
    private static (int psetId, int valueId) SoleQuantityTakeOffPset(string ifc)
    {
        var results = QuantityTakeOffPsetIds(ifc);
        return Assert.Single(results);
    }

    private static List<(int psetId, int valueId)> QuantityTakeOffPsetIds(string ifc)
    {
        var matches = Regex.Matches(ifc,
            @"#(\d+)=IFCPROPERTYSET\('[^']*',#\d+,'Pset_QuantityTakeOff',(?:\$|''),\(#(\d+)\)\);");
        Assert.True(matches.Count > 0, "expected at least one Pset_QuantityTakeOff IFCPROPERTYSET");
        return matches.Select(m => (
            int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value))).ToList();
    }

    /// <summary>Finds the single IFCRELDEFINESBYPROPERTIES whose RelatingPropertyDefinition
    /// is <paramref name="psetId"/> and returns its (single) RelatedObjects entity id.</summary>
    private static int SoleRelDefinesByPropertiesTarget(string ifc, int psetId)
    {
        var pattern = new Regex(
            $@"IFCRELDEFINESBYPROPERTIES\('[^']*',#\d+,(?:\$|''),(?:\$|''),\(#(\d+)\),#{psetId}\);");
        var match = Assert.Single(pattern.Matches(ifc));
        return int.Parse(match.Groups[1].Value);
    }

    private static string ReferenceLiteral(string ifc, int valueId)
    {
        var pattern = new Regex(
            $@"#{valueId}=IFCPROPERTYSINGLEVALUE\('Reference',(?:\$|''),IFCIDENTIFIER\('([^']*)'\),\$\);");
        var match = Assert.Single(pattern.Matches(ifc));
        return match.Groups[1].Value;
    }

    private static GeometryData RectangularPrism(
        double[] centre, double[] axis, double[] profileX,
        double length, double width, double depth)
    {
        double[] profileY = Cross(axis, profileX);
        double[][] points = PrismPoints(centre, axis, profileX, profileY, length, width, depth);
        int[][] faceIndices =
        [
            [0, 1, 3, 2],
            [4, 6, 7, 5],
            [0, 4, 5, 1],
            [2, 3, 7, 6],
            [0, 2, 6, 4],
            [1, 5, 7, 3],
        ];
        return new GeometryData
        {
            BoundingBox = Bounds(points),
            Faces = faceIndices.Select(indices => new GeoFace
            {
                OuterVertices = indices.Select(index => points[index]).ToList(),
            }).ToList(),
            BrepSolids =
            [
                new GeoBrepSolid
                {
                    IsClosed = true,
                    FaceIndices = [0, 1, 2, 3, 4, 5],
                },
            ],
        };
    }

    private static double[] PrismBounds(
        double[] centre, double[] axis, double[] profileX,
        double length, double width, double depth)
    {
        double[] profileY = Cross(axis, profileX);
        return Bounds(PrismPoints(centre, axis, profileX, profileY, length, width, depth));
    }

    private static double[][] PrismPoints(
        double[] centre, double[] axis, double[] profileX, double[] profileY,
        double length, double width, double depth)
    {
        var points = new double[8][];
        int index = 0;
        foreach (int alongSign in new[] { -1, 1 })
            foreach (int widthSign in new[] { -1, 1 })
                foreach (int depthSign in new[] { -1, 1 })
                    points[index++] =
                    [
                        centre[0] + axis[0] * length / 2 * alongSign
                            + profileX[0] * width / 2 * widthSign
                            + profileY[0] * depth / 2 * depthSign,
                        centre[1] + axis[1] * length / 2 * alongSign
                            + profileX[1] * width / 2 * widthSign
                            + profileY[1] * depth / 2 * depthSign,
                        centre[2] + axis[2] * length / 2 * alongSign
                            + profileX[2] * width / 2 * widthSign
                            + profileY[2] * depth / 2 * depthSign,
                    ];
        return points;
    }

    private static double[] Bounds(IEnumerable<double[]> points)
    {
        var list = points.ToList();
        return
        [
            list.Min(point => point[0]),
            list.Min(point => point[1]),
            list.Min(point => point[2]),
            list.Max(point => point[0]),
            list.Max(point => point[1]),
            list.Max(point => point[2]),
        ];
    }

    private static double[] Cross(double[] first, double[] second) =>
    [
        first[1] * second[2] - first[2] * second[1],
        first[2] * second[0] - first[0] * second[2],
        first[0] * second[1] - first[1] * second[0],
    ];
}
