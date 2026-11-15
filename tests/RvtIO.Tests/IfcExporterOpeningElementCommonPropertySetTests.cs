using System.Text.RegularExpressions;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Pset_OpeningElementCommon (plus Pset_QuantityTakeOff) on synthetic IfcOpeningElements, the wall and slab cuts created outside the main element loop; exercised through real wall-cutting geometry that goes through <c>CreateOpeningElement</c>, the single point every synthetic opening passes through.</summary>
public class IfcExporterOpeningElementCommonPropertySetTests
{
    [Fact]
    public void SourceOwnedWallOpening_ReceivesOpeningElementCommonAndQuantityTakeOff()
    {
        var (wall, boundary) = SourceOwnedOpeningWall();
        wall.Name = "SourceOwnedWallType";
        wall.CuttingElementIds = [];
        wall.IsDiscovered = true;
        wall.Geometry = boundary;

        var info = new RevitFileInfo();
        info.Elements.Add(wall);
        string path = TempIfcPath("opening-source-owned");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            var openings = OpeningEntities(ifc);
            var opening = Assert.Single(openings);
            // The opening's own Tag happens to equal the wall id in this fixture. Reference
            // comes from the wall because the wall owns the cut, not because the ids match.
            Assert.Equal(wall.Id.ToString(), opening.Tag);

            // Only the opening entity gets Pset_OpeningElementCommon - not the wall itself,
            // and not duplicated anywhere in the file.
            Assert.Single(Regex.Matches(ifc, "'Pset_OpeningElementCommon'"));

            var oecValueIds = PsetValueIdsForEntity(ifc, opening.EntityId, "Pset_OpeningElementCommon");
            var oecProps = ResolveProperties(ifc, oecValueIds);
            Assert.Equal(new[] { "Reference" }, oecProps.Keys);
            Assert.Equal("IFCIDENTIFIER('SourceOwnedWallType')", oecProps["Reference"]);

            var qtoValueIds = PsetValueIdsForEntity(ifc, opening.EntityId, "Pset_QuantityTakeOff");
            var qtoProps = ResolveProperties(ifc, qtoValueIds);
            Assert.Equal(new[] { "Reference" }, qtoProps.Keys);
            Assert.Equal("IFCIDENTIFIER('SourceOwnedWallType')", qtoProps["Reference"]);

            Assert.Equal(oecValueIds.Single(), qtoValueIds.Single());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void WallOpening_IsNotContainedInAStorey()
    {
        var (wall, boundary) = SourceOwnedOpeningWall();
        wall.CuttingElementIds = [];
        wall.IsDiscovered = true;
        wall.Geometry = boundary;
        var info = new RevitFileInfo();
        info.Elements.Add(wall);
        string path = TempIfcPath("opening-not-contained");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);
            var opening = Assert.Single(OpeningEntities(ifc));
            var wallEntity = Regex.Match(ifc, @"#(\d+)=IFCWALL(?:STANDARDCASE)?\(");
            Assert.True(wallEntity.Success);
            var contained = Regex.Matches(ifc, @"#\d+=IFCRELCONTAINEDINSPATIALSTRUCTURE\(.*\);")
                .Select(match => match.Value).ToList();
            Assert.NotEmpty(contained);
            Assert.All(contained, relation => Assert.DoesNotMatch($@"[(,]#{opening.EntityId}[,)]", relation));
            Assert.Contains(contained, relation => Regex.IsMatch(relation, $@"[(,]#{wallEntity.Groups[1].Value}[,)]"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void SourceOwnedWallOpening_WhoseReferenceElementHasNoName_ReceivesNeitherPsetInsteadOfAPlaceholder()
    {
        var (wall, boundary) = SourceOwnedOpeningWall();
        // wall.Name intentionally left null - see comment above.
        wall.CuttingElementIds = [];
        wall.IsDiscovered = true;
        wall.Geometry = boundary;

        var info = new RevitFileInfo();
        info.Elements.Add(wall);
        string path = TempIfcPath("opening-nameless-reference");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            var openings = OpeningEntities(ifc);
            var opening = Assert.Single(openings);
            // The Tag remains present; omission is specifically caused by the selected wall
            // source having no Name.
            Assert.Equal(wall.Id.ToString(), opening.Tag);

            Assert.False(HasPsetAttached(ifc, opening.EntityId, "Pset_OpeningElementCommon"));
            Assert.False(HasPsetAttached(ifc, opening.EntityId, "Pset_QuantityTakeOff"));
            // Nothing in this file resembles the old placeholder text at all - the only
            // opening's Pset_OpeningElementCommon simply does not exist (checked above), and
            // this file contains exactly one opening, so the whole file must be free of it.
            Assert.DoesNotContain("Pset_OpeningElementCommon", ifc);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void SourceOwnedSlabOpening_ValidatedFloorNameDoesNotCreateMissingAttachments()
    {
        var (info, slab, sketchLine) = SourceOwnedSlabOpening();
        slab.ValidatedFloorTypeReferenceName = "Canonical floor type";
        string path = TempIfcPath("opening-slab-unresolved-reference");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            var opening = Assert.Single(OpeningEntities(ifc));
            Assert.Equal(sketchLine.Id.ToString(), opening.Tag);
            Assert.False(HasPsetAttached(
                ifc, opening.EntityId, "Pset_OpeningElementCommon"));
            Assert.False(HasPsetAttached(
                ifc, opening.EntityId, "Pset_QuantityTakeOff"));
        }
        finally { Cleanup(path); }
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData(int.MinValue, false, false)]
    [InlineData(-2, false, false)]
    [InlineData(-1, false, true)]
    [InlineData(0, false, false)]
    [InlineData(1, true, false)]
    [InlineData(int.MaxValue, true, false)]
    public void SerializedSlabOpeningSourceGates_RequireExactUnplacedOwnerSource(
        int? unplacedOwnerId, bool suppressOpening, bool allowHostReference)
    {
        Assert.Equal(suppressOpening,
            IfcExporter.ShouldSuppressSerializedSlabOpeningSource(unplacedOwnerId));
        Assert.Equal(allowHostReference,
            IfcExporter.CanUseSerializedSlabOpeningHostReference(unplacedOwnerId));
    }

    [Fact]
    public void PositiveUnplacedOwnerSketchLine_EmitsNoSlabOpeningEvenWhenNamed()
    {
        var (info, slab, sketchLine) = SourceOwnedSlabOpening();
        sketchLine.SerializedUnplacedOwnerId = 4424388;
        sketchLine.Name = "Named nested sketch line";
        slab.ValidatedFloorTypeReferenceName = "Canonical floor type";
        string path = TempIfcPath("opening-slab-positive-unplaced-owner");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Empty(OpeningEntities(ifc));
            Assert.DoesNotContain("=IFCRELVOIDSELEMENT(", ifc);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void MinusOneUnplacedOwnerNamelessSketchLine_UsesHostReferenceAndExactStepId()
    {
        var (info, slab, sketchLine) = SourceOwnedSlabOpening();
        sketchLine.SerializedUnplacedOwnerId = -1;
        slab.ValidatedFloorTypeReferenceName = "Canonical floor type";
        string path = TempIfcPath("opening-slab-no-unplaced-owner-reference");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            var opening = Assert.Single(OpeningEntities(ifc));
            Assert.Equal(sketchLine.Id.ToString(), opening.Tag);
            var openingCommonValues = PsetValueIdsForEntity(
                ifc, opening.EntityId, "Pset_OpeningElementCommon");
            var openingQuantityValues = PsetValueIdsForEntity(
                ifc, opening.EntityId, "Pset_QuantityTakeOff");
            Assert.Equal(
                "IFCIDENTIFIER('Canonical floor type')",
                ResolveProperties(ifc, openingCommonValues)["Reference"]);

            Match slabMatch = Assert.Single(Regex.Matches(
                ifc, $@"#(\d+)=IFCSLAB\([^;]*,'{slab.Id}',\.[A-Z]+\.\);"));
            var slabQuantityValues = PsetValueIdsForEntity(
                ifc, int.Parse(slabMatch.Groups[1].Value), "Pset_QuantityTakeOff");
            Assert.Equal(openingCommonValues.Single(), openingQuantityValues.Single());
            Assert.Equal(openingCommonValues.Single(), slabQuantityValues.Single());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void MinusOneUnplacedOwnerSketchLine_CannotOverrideEmittedSlabQtoReference()
    {
        var (info, slab, sketchLine) = SourceOwnedSlabOpening();
        sketchLine.SerializedUnplacedOwnerId = -1;
        sketchLine.Name = "Sketch-line fallback";
        sketchLine.Parameters.Add(new Parameter
        {
            Id = 9001,
            Definition = new Definition { Id = 9001, Name = "Reference" },
            StorageType = StorageType.String,
            Value = "Conflicting sketch-line Reference",
        });
        slab.ValidatedFloorTypeReferenceName = "Canonical floor type";
        Assert.Equal(
            "Conflicting sketch-line Reference",
            IfcExporter.ResolveSyntheticOpeningReferenceValue(
                sketchLine,
                info.Elements.ToDictionary(element => element.Id),
                slab.ValidatedFloorTypeReferenceName));
        string path = TempIfcPath("opening-slab-reuses-emitted-qto-reference");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            var opening = Assert.Single(OpeningEntities(ifc));
            var openingValues = PsetValueIdsForEntity(
                ifc, opening.EntityId, "Pset_OpeningElementCommon");
            Assert.Equal(
                "IFCIDENTIFIER('Canonical floor type')",
                ResolveProperties(ifc, openingValues)["Reference"]);
            Assert.DoesNotContain(
                "IFCIDENTIFIER('Conflicting sketch-line Reference')", ifc);

            Match slabMatch = Assert.Single(Regex.Matches(
                ifc, $@"#(\d+)=IFCSLAB\([^;]*,'{slab.Id}',\.[A-Z]+\.\);"));
            var slabValues = PsetValueIdsForEntity(
                ifc, int.Parse(slabMatch.Groups[1].Value), "Pset_QuantityTakeOff");
            Assert.Equal(slabValues.Single(), openingValues.Single());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void MinusOneUnplacedOwnerWithBlockedSlabQto_EmitsNoOrphanOpeningPsets()
    {
        var (info, slab, sketchLine) = SourceOwnedSlabOpening();
        sketchLine.SerializedUnplacedOwnerId = -1;
        sketchLine.Name = "Sketch-line fallback";
        sketchLine.Parameters.Add(new Parameter
        {
            Id = 9001,
            Definition = new Definition { Id = 9001, Name = "Reference" },
            StorageType = StorageType.String,
            Value = "Explicit sketch-line Reference",
        });
        slab.ValidatedFloorTypeReferenceName = "Blocked floor type";
        slab.ValidatedFloorTypeReferenceIsBlocked = true;
        Assert.Equal(
            "Explicit sketch-line Reference",
            IfcExporter.ResolveSyntheticOpeningReferenceValue(
                sketchLine,
                info.Elements.ToDictionary(element => element.Id),
                slab.ValidatedFloorTypeReferenceName,
                slab.ValidatedFloorTypeReferenceIsBlocked));
        string path = TempIfcPath("opening-slab-blocked-qto-reference");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            var opening = Assert.Single(OpeningEntities(ifc));
            Assert.False(HasPsetAttached(
                ifc, opening.EntityId, "Pset_OpeningElementCommon"));
            Assert.False(HasPsetAttached(
                ifc, opening.EntityId, "Pset_QuantityTakeOff"));
            Assert.DoesNotContain("'Pset_QuantityTakeOff'", ifc);
            Assert.DoesNotContain("'Pset_OpeningElementCommon'", ifc);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void SourceOwnedSlabOpening_UsesValidatedFloorNameOnlyForExistingAttachments()
    {
        var (info, slab, sketchLine) = SourceOwnedSlabOpening();
        sketchLine.Name = "Wrong sketch-line name";
        slab.ValidatedFloorTypeReferenceName = "Canonical floor type";
        string path = TempIfcPath("opening-slab-validated-reference");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            var opening = Assert.Single(OpeningEntities(ifc));
            Assert.Equal(sketchLine.Id.ToString(), opening.Tag);
            var openingCommonValues = PsetValueIdsForEntity(
                ifc, opening.EntityId, "Pset_OpeningElementCommon");
            var quantityValues = PsetValueIdsForEntity(
                ifc, opening.EntityId, "Pset_QuantityTakeOff");
            Assert.Equal(
                "IFCIDENTIFIER('Canonical floor type')",
                ResolveProperties(ifc, openingCommonValues)["Reference"]);
            Assert.Equal(
                "IFCIDENTIFIER('Canonical floor type')",
                ResolveProperties(ifc, quantityValues)["Reference"]);
            Assert.Equal(openingCommonValues.Single(), quantityValues.Single());

            // The 0xFFFF077B-derived value is intentionally scoped to opening Psets.
            Assert.Equal("HostFloorTypeMustNotBeInferred", slab.Name);
            Assert.DoesNotContain(
                "IFCIDENTIFIER('Wrong sketch-line name')", ifc);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void SourceOwnedSlabOpening_ExplicitTagReferenceWinsValidatedFloorName()
    {
        var (info, slab, sketchLine) = SourceOwnedSlabOpening();
        sketchLine.Name = "Sketch-line fallback";
        sketchLine.Parameters.Add(new Parameter
        {
            Id = 9001,
            Definition = new Definition { Id = 9001, Name = "Reference" },
            StorageType = StorageType.String,
            Value = "Explicit opening Reference",
        });
        slab.ValidatedFloorTypeReferenceName = "Canonical floor type";
        string path = TempIfcPath("opening-slab-explicit-reference");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            var opening = Assert.Single(OpeningEntities(ifc));
            var values = PsetValueIdsForEntity(
                ifc, opening.EntityId, "Pset_OpeningElementCommon");
            Assert.Equal(
                "IFCIDENTIFIER('Explicit opening Reference')",
                ResolveProperties(ifc, values)["Reference"]);
        }
        finally { Cleanup(path); }
    }

    [Theory]
    [InlineData("Reference")]
    [InlineData("Reference[Type]")]
    public void SourceOwnedSlabOpening_ExplicitTagTypeReferenceWinsFallbacks(
        string typeParameterName)
    {
        var (info, slab, sketchLine) = SourceOwnedSlabOpening();
        sketchLine.Name = "Sketch-line fallback";
        var sketchLineType = new Element { Id = 121 };
        sketchLine.Parameters.Add(new Parameter
        {
            Id = -1002050,
            Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" },
            StorageType = StorageType.ElementId,
            Value = sketchLineType.Id,
        });
        sketchLineType.Parameters.Add(new Parameter
        {
            Id = 9002,
            Definition = new Definition { Id = 9002, Name = typeParameterName },
            StorageType = StorageType.String,
            Value = $"Explicit type {typeParameterName}",
        });
        info.Elements.Add(sketchLineType);
        slab.ValidatedFloorTypeReferenceName = "Canonical floor type";
        string path = TempIfcPath($"opening-slab-explicit-type-reference-{typeParameterName}");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            var opening = Assert.Single(OpeningEntities(ifc));
            var values = PsetValueIdsForEntity(
                ifc, opening.EntityId, "Pset_OpeningElementCommon");
            Assert.Equal(
                $"IFCIDENTIFIER('Explicit type {typeParameterName}')",
                ResolveProperties(ifc, values)["Reference"]);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void HostedDoorOpening_AtADifferentCallSiteThanTheWallOpening_IsFilledSoItGetsNeitherPset()
    {
        const int phaseId = 99;
        var (wall, firstCutter, boundary) = MixedHostedAndSourceOwnedOpeningWall();
        wall.IsDiscovered = true;
        wall.Name = "MixedSourceOwnedWallType";
        wall.CuttingElementIds = null;
        wall.Geometry = boundary;
        firstCutter.IsDiscovered = true;
        firstCutter.Name = "HostedDoorType_900x2100";
        firstCutter.HostId = wall.Id;
        firstCutter.References = [wall.Id, phaseId];
        firstCutter.Parameters =
        [
            new Parameter
            {
                Id = -1012100, // PHASE_CREATED
                StorageType = StorageType.ElementId,
                Value = phaseId,
            },
            new Parameter
            {
                Id = -1013201,
                StorageType = StorageType.Integer,
                Value = -4,
            },
        ];
        firstCutter.Geometry = BoxGeometry(1, -0.5, 1, 2, 0.5, 2);

        var info = new RevitFileInfo();
        info.Elements.AddRange([wall, firstCutter]);
        string path = TempIfcPath("opening-mixed-hosted-source-owned");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            var openings = OpeningEntities(ifc);
            Assert.Equal(2, openings.Count);

            var wallTaggedOpening = Assert.Single(
                openings, o => o.Tag == wall.Id.ToString());
            var doorTaggedOpening = Assert.Single(
                openings, o => o.Tag == firstCutter.Id.ToString());
            Assert.NotEqual(wallTaggedOpening.EntityId, doorTaggedOpening.EntityId);

            // The wall's own source-owned opening: same claim as the previous test, on the
            // same call site (CreateExactSourceWallOpening).
            var wallOecProps = ResolveProperties(ifc,
                PsetValueIdsForEntity(ifc, wallTaggedOpening.EntityId, "Pset_OpeningElementCommon"));
            Assert.Equal("IFCIDENTIFIER('MixedSourceOwnedWallType')", wallOecProps["Reference"]);
            var wallQtoProps = ResolveProperties(ifc,
                PsetValueIdsForEntity(ifc, wallTaggedOpening.EntityId, "Pset_QuantityTakeOff"));
            Assert.Equal("IFCIDENTIFIER('MixedSourceOwnedWallType')", wallQtoProps["Reference"]);

            // The door's hosted opening comes from a different call site (the hosted-opening pipeline). First check that IfcRelFillsElement links the door to this exact opening, otherwise the "no Pset" assertions below would pass for the wrong reason.
            Assert.True(Regex.IsMatch(ifc,
                $@"IFCRELFILLSELEMENT\('[^']*',#\d+,\$,\$,#{doorTaggedOpening.EntityId},#\d+\);"),
                "Expected IfcRelFillsElement linking the door to its opening.");
            Assert.False(HasPsetAttached(ifc, doorTaggedOpening.EntityId, "Pset_OpeningElementCommon"));
            Assert.False(HasPsetAttached(ifc, doorTaggedOpening.EntityId, "Pset_QuantityTakeOff"));

            // Exactly one Pset_OpeningElementCommon instance in the whole file - the wall's
            // unfilled opening only; the door's filled opening contributes none.
            Assert.Single(Regex.Matches(ifc, "'Pset_OpeningElementCommon'"));
        }
        finally { Cleanup(path); }
    }

    /// <summary>The wall of <see cref="HostedDoorOpening_AtADifferentCallSiteThanTheWallOpening_IsFilledSoItGetsNeitherPset"/>
    /// with its door cutter; a wall without a Line or Arc location is written as a plain IfcWall.</summary>
    private static RevitFileInfo PlainWallWithAHostedDoorAndASourceOwnedOpening()
    {
        const int phaseId = 99;
        var (wall, cutter, boundary) = MixedHostedAndSourceOwnedOpeningWall();
        wall.IsDiscovered = true;
        wall.Name = "PlainWallType";
        wall.CuttingElementIds = null;
        wall.Geometry = boundary;
        wall.Location = null;
        cutter.IsDiscovered = true;
        cutter.Name = "HostedDoorType_900x2100";
        cutter.HostId = wall.Id;
        cutter.References = [wall.Id, phaseId];
        cutter.Parameters =
        [
            new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = phaseId },
            new Parameter { Id = -1013201, StorageType = StorageType.Integer, Value = -4 },
        ];
        cutter.Geometry = BoxGeometry(1, -0.5, 1, 2, 0.5, 2);
        var info = new RevitFileInfo();
        info.Elements.AddRange([wall, cutter]);
        return info;
    }

    [Fact]
    public void Current_WallWrittenAsPlainIfcWall_HasNoOpeningsBesideIt()
    {
        string path = TempIfcPath("opening-plain-wall-228");
        string pathCurrent = TempIfcPath("opening-plain-wall-277");
        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }
                .Export(PlainWallWithAHostedDoorAndASourceOwnedOpening(), path);
            new IfcExporter() { Profile = IfcProfile.Current }
                .Export(PlainWallWithAHostedDoorAndASourceOwnedOpening(), pathCurrent);
            string ifcLegacy = File.ReadAllText(path), ifcCurrent = File.ReadAllText(pathCurrent);

            Assert.NotEmpty(OpeningEntities(ifcLegacy));
            Assert.Contains("=IFCRELFILLSELEMENT(", ifcLegacy);
            Assert.Matches(@"#\d+=IFCWALL\(", ifcCurrent);
            Assert.Empty(OpeningEntities(ifcCurrent));
            Assert.DoesNotContain("=IFCRELVOIDSELEMENT(", ifcCurrent);
            Assert.DoesNotContain("=IFCRELFILLSELEMENT(", ifcCurrent);
        }
        finally
        {
            Cleanup(path);
            Cleanup(pathCurrent);
        }
    }

    [Theory]
    [InlineData(1001)] [InlineData(0)] [InlineData(-1)]
    public void PureDoorCutterOnSerializedPrimaryHost_GetsNeitherOpeningPset(int typeId)
    {
        const int phaseId = 99;
        var (wall, cutter, boundary) = MixedHostedAndSourceOwnedOpeningWall();
        wall.IsDiscovered = true;
        wall.Name = "PureCutterHostWallType";
        wall.CuttingElementIds = null;
        wall.Geometry = boundary;
        cutter.IsDiscovered = true;
        cutter.Name = "PureDoorCutterType";
        cutter.HostId = wall.Id;
        cutter.SerializedHostId = wall.Id;
        cutter.References = [wall.Id, phaseId];
        cutter.Parameters =
        [
            new Parameter
            {
                Id = -1012100,
                StorageType = StorageType.ElementId,
                Value = phaseId,
            },
            new Parameter
            {
                Id = -1013201,
                StorageType = StorageType.Integer,
                Value = -4,
            },
        ];
        cutter.Geometry = null;
        cutter.TypeId = typeId;
        cutter.Location = null;

        var info = new RevitFileInfo();
        info.Elements.AddRange([wall, cutter]);
        string path = TempIfcPath("opening-pure-primary-door-cutter");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.DoesNotContain("=IFCDOOR(", ifc);
            var opening = Assert.Single(
                OpeningEntities(ifc), item => item.Tag == cutter.Id.ToString());
            Assert.DoesNotMatch(
                $@"IFCRELFILLSELEMENT\('[^']*',#\d+,\$,\$,#{opening.EntityId},#\d+\);",
                ifc);
            Assert.False(HasPsetAttached(
                ifc, opening.EntityId, "Pset_OpeningElementCommon"));
            Assert.False(HasPsetAttached(
                ifc, opening.EntityId, "Pset_QuantityTakeOff"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void HostedOpeningReferenceSource_DistinguishesPrimaryPureCutterFromOtherHosts()
    {
        var primaryHost = new Element { Id = 10, Name = "PrimaryWall" };
        var secondaryHost = new Element { Id = 11, Name = "SecondaryWall" };
        var door = new Element
        {
            Id = 20,
            Name = "DoorType",
            CategoryId = -2000023,
            SerializedHostId = primaryHost.Id,
            ExtraHostIds = [secondaryHost.Id],
        };
        var genericCutter = new Element
        {
            Id = 21,
            Name = "GenericCutterType",
            CategoryId = -2000151,
            SerializedHostId = primaryHost.Id,
        };
        var window = new Element
        {
            Id = 22,
            Name = "WindowType",
            CategoryId = -2000014,
            SerializedHostId = primaryHost.Id,
        };

        Assert.Null(IfcExporter.ResolveHostedOpeningPropertySetReferenceElement(
            door, hostedIfcId: 0, primaryHost, createsFillRelationship: false));
        Assert.Same(door, IfcExporter.ResolveHostedOpeningPropertySetReferenceElement(
            door, hostedIfcId: 0, secondaryHost, createsFillRelationship: false));
        Assert.Same(genericCutter,
            IfcExporter.ResolveHostedOpeningPropertySetReferenceElement(
                genericCutter, hostedIfcId: 0, primaryHost,
                createsFillRelationship: false));
        Assert.Same(door, IfcExporter.ResolveHostedOpeningPropertySetReferenceElement(
            door, hostedIfcId: 100, primaryHost,
            createsFillRelationship: false));
        Assert.Null(IfcExporter.ResolveHostedOpeningPropertySetReferenceElement(
            door, hostedIfcId: 100, secondaryHost, createsFillRelationship: true));
        Assert.Null(IfcExporter.ResolveHostedOpeningPropertySetReferenceElement(
            window, hostedIfcId: 0, primaryHost, createsFillRelationship: false));
    }

    [Fact]
    public void HostedOpeningFillRelationship_RequiresSerializedPrimaryHost()
    {
        var door = new Element
        {
            Id = 20,
            CategoryId = -2000023,
            SerializedHostId = 10,
        };
        var filledIds = new HashSet<int>();

        Assert.False(IfcExporter.TryRegisterHostedOpeningFillRelationship(
            door, hostedIfcId: 100, hostWallId: 11, filledIds));
        Assert.Empty(filledIds);
        Assert.True(IfcExporter.TryRegisterHostedOpeningFillRelationship(
            door, hostedIfcId: 100, hostWallId: 10, filledIds));
        Assert.False(IfcExporter.TryRegisterHostedOpeningFillRelationship(
            door, hostedIfcId: 100, hostWallId: 10, filledIds));

        door.SerializedHostId = null;
        filledIds.Clear();
        Assert.False(IfcExporter.TryRegisterHostedOpeningFillRelationship(
            door, hostedIfcId: 100, hostWallId: 10, filledIds));
        Assert.False(IfcExporter.TryRegisterHostedOpeningFillRelationship(
            door, hostedIfcId: 0, hostWallId: 10, filledIds));
    }

    [Fact]
    public void SourceOwnedWallOpeningReferenceSource_RequiresTagAndUsesWallInsteadOfSketchLine()
    {
        var wall = new Element { Id = 10, Name = "WallType" };
        var sketchLine = new Element { Id = 20, Name = "SketchLine" };

        Assert.Same(wall,
            IfcExporter.ResolveSourceOwnedWallOpeningPropertySetReferenceElement(
                wall, sketchLine.Id));
        Assert.Null(
            IfcExporter.ResolveSourceOwnedWallOpeningPropertySetReferenceElement(
                wall, sourceOwnerTagId: null));
    }

    [Fact]
    public void JoinedWallEndOpeningReferenceSource_IsTheCuttingWallItsTagNames()
    {
        var host = new Element { Id = 1485448, Name = "XS_Отделка_Тип 4" };
        var cutter = new Element { Id = 1487082, Name = "ТР_Минеральная вата(150),Штукатурка(5)_155" };
        var sketchLine = new Element { Id = 20, Name = "SketchLine" };
        var elementsById = new[] { host, cutter, sketchLine }.ToDictionary(element => element.Id);

        Element? source = IfcExporter.ResolveSourceWallProfileOpeningPropertySetReferenceElement(
            host, cutter.Id, elementsById, propertiesFromTagElement: true);
        Assert.Same(cutter, source);
        Assert.Equal("ТР_Минеральная вата(150),Штукатурка(5)_155",
            IfcExporter.ResolveSyntheticOpeningReferenceValue(source, elementsById, null));
        Assert.Null(IfcExporter.ResolveSourceWallProfileOpeningPropertySetReferenceElement(
            host, 99, elementsById, propertiesFromTagElement: true));
        // An edited profile's sketch line keeps the wall.
        Assert.Same(host, IfcExporter.ResolveSourceWallProfileOpeningPropertySetReferenceElement(
            host, sketchLine.Id, elementsById, propertiesFromTagElement: false));
    }

    [Fact]
    public void TwoSourceOwnedWallOpenings_WithSameTypeName_ShareOnePropertySingleValueInstance()
    {
        var (wallA, boundaryA) = SourceOwnedOpeningWall();
        wallA.Name = "SharedOpeningWallType";
        wallA.CuttingElementIds = [];
        wallA.IsDiscovered = true;
        wallA.Geometry = boundaryA;

        var (wallB, boundaryB) = SourceOwnedOpeningWall();
        wallB.Id += 5000;
        wallB.TypeId += 5000;
        wallB.Name = "SharedOpeningWallType";
        wallB.CuttingElementIds = [];
        wallB.IsDiscovered = true;
        wallB.Geometry = boundaryB;

        var info = new RevitFileInfo();
        info.Elements.AddRange([wallA, wallB]);
        string path = TempIfcPath("opening-shared-value");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            var openings = OpeningEntities(ifc);
            Assert.Equal(2, openings.Count);

            Assert.Equal(2, Regex.Matches(ifc, "'Pset_OpeningElementCommon'").Count);
            Assert.Single(Regex.Matches(
                ifc, @"IFCPROPERTYSINGLEVALUE\('Reference',(?:\$|''),IFCIDENTIFIER\('SharedOpeningWallType'\)"));

            var idsA = PsetValueIdsForEntity(ifc, openings[0].EntityId, "Pset_OpeningElementCommon");
            var idsB = PsetValueIdsForEntity(ifc, openings[1].EntityId, "Pset_OpeningElementCommon");
            Assert.Equal(idsA.Single(), idsB.Single());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void NonOpeningElement_DoesNotReceiveOpeningElementCommon()
    {
        var beam = new Element
        {
            Id = 700,
            TypeId = 701,
            IsDiscovered = true,
            CategoryId = -2001320,
            Category = "OST_StructuralFraming",
            Name = "BeamType_200x300",
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
            BoundingBox = [0, 4.8, 3.8, 10, 5.2, 4.2],
            Geometry = BoxGeometry(0, 4.8, 3.8, 10, 5.2, 4.2),
        };
        var info = new RevitFileInfo();
        info.Elements.Add(beam);
        string path = TempIfcPath("opening-non-opening-element");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Single(Regex.Matches(ifc, "'Pset_QuantityTakeOff'"));
            Assert.DoesNotContain("Pset_OpeningElementCommon", ifc);
            Assert.DoesNotContain("=IFCOPENINGELEMENT(", ifc);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void MainLoopWallOpening_WithAuthoritativeEmptyHostCutRoster_ReusesHostQtoReferenceStepId()
    {
        var (info, wall, opening) = MainLoopWallOpening();
        string path = TempIfcPath("opening-main-loop-empty-host-cut-roster");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            var openingEntity = Assert.Single(
                OpeningEntities(ifc), entity => entity.Tag == opening.Id.ToString());
            Match wallMatch = Assert.Single(Regex.Matches(
                ifc,
                $@"#(\d+)=IFCWALL(?:STANDARDCASE)?\([^\r\n]*,'{wall.Id}'\);"));
            int wallEntityId = int.Parse(wallMatch.Groups[1].Value);

            var openingCommonValues = PsetValueIdsForEntity(
                ifc, openingEntity.EntityId, "Pset_OpeningElementCommon");
            var openingQuantityValues = PsetValueIdsForEntity(
                ifc, openingEntity.EntityId, "Pset_QuantityTakeOff");
            var wallQuantityValues = PsetValueIdsForEntity(
                ifc, wallEntityId, "Pset_QuantityTakeOff");

            Assert.Equal(openingCommonValues.Single(), openingQuantityValues.Single());
            Assert.Equal(openingCommonValues.Single(), wallQuantityValues.Single());
            Assert.Equal(
                "IFCIDENTIFIER('MainLoopHostWallReference')",
                ResolveProperties(ifc, openingCommonValues)["Reference"]);
            Assert.Single(Regex.Matches(
                ifc,
                @"IFCPROPERTYSINGLEVALUE\('Reference',(?:\$|''),IFCIDENTIFIER\('MainLoopHostWallReference'\),\$\)"));
            Assert.Contains(IfcGuid.FromName($"qto-pset:{opening.Id}"), ifc);
            Assert.Contains(IfcGuid.FromName($"qto-rel:{opening.Id}"), ifc);
            Assert.Contains(
                IfcGuid.FromName($"opening-common-pset:{opening.Id}"), ifc);
            Assert.Contains(
                IfcGuid.FromName($"opening-common-rel:{opening.Id}"), ifc);
        }
        finally { Cleanup(path); }
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void MainLoopWallOpening_CurrentWritesItsOwnExactBodyAcrossTheSerializedHost(
        bool currentProfile, bool matchingHost, bool expectedExtrusion)
    {
        var (info, wall, opening) = MainLoopWallOpening();
        if (!matchingHost) opening.SerializedHostId = wall.Id + 100;
        string path = TempIfcPath("opening-main-loop-extrusion");
        try
        {
            new IfcExporter { Profile = currentProfile ? IfcProfile.Current : IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);
            var product = Assert.Single(OpeningEntities(ifc));
            var w = new StepWriter();
            foreach (Match row in Regex.Matches(ifc, @"^#(\d+)=(\w+)\((.*)\);\r?$", RegexOptions.Multiline))
                w.AddWithId(int.Parse(row.Groups[1].Value), row.Groups[2].Value, row.Groups[3].Value);
            var index = Enumerable.Range(0, w.EntityCount).ToDictionary(i => w.EntityAt(i).Id);
            var attributes = IfcExporter.SplitStepArguments(w.EntityAt(index[product.EntityId]).Args);
            var body = IfcExporter.ReadBodyExtrusionCurrent(w, index, attributes[6]);
            Assert.Equal(expectedExtrusion, body.HasValue);
            if (expectedExtrusion) Assert.Equal(1.5 * 304.8, body!.Value.Depth, 9);
        }
        finally { Cleanup(path); }
    }

    [Theory]
    [InlineData("sample-desired-null-roster")]
    [InlineData("missing-host")]
    [InlineData("non-wall-host")]
    [InlineData("sample-surplus-nonempty-nonmember-roster")]
    [InlineData("cutter-member-roster")]
    [InlineData("duplicate-host-id")]
    public void MainLoopWallOpeningHostReferenceValidation_FailsClosed(
        string rejectedSource)
    {
        var (info, wall, opening) = MainLoopWallOpening();

        switch (rejectedSource)
        {
            case "sample-desired-null-roster":
                wall.CuttingElementIds = null;
                break;
            case "missing-host":
                opening.SerializedHostId = 909_001;
                opening.HostId = 909_001;
                break;
            case "non-wall-host":
                var nonWallHost = new Element
                {
                    Id = 909_002,
                    TypeId = 909_003,
                    IsDiscovered = true,
                    CategoryId = -2001320,
                    Category = "OST_StructuralFraming",
                    Name = "Not a wall host",
                    References = [99],
                    Parameters =
                    [
                        new Parameter
                        {
                            Id = -1012100,
                            StorageType = StorageType.ElementId,
                            Value = 99,
                        },
                    ],
                    BoundingBox = [0, 2, 0, 4, 3, 1],
                    Geometry = BoxGeometry(0, 2, 0, 4, 3, 1),
                };
                info.Elements.Insert(1, nonWallHost);
                opening.SerializedHostId = nonWallHost.Id;
                opening.HostId = nonWallHost.Id;
                break;
            case "sample-surplus-nonempty-nonmember-roster":
                wall.CuttingElementIds = [opening.Id + 1];
                break;
            case "cutter-member-roster":
                wall.CuttingElementIds = [opening.Id];
                break;
            case "duplicate-host-id":
                info.Elements.Insert(1, new Element
                {
                    Id = wall.Id,
                    TypeId = wall.TypeId + 100,
                    IsDiscovered = true,
                    CategoryId = -2000011,
                    Category = "OST_Walls",
                    Name = wall.Name,
                    References = [99],
                    Parameters =
                    [
                        new Parameter
                        {
                            Id = -1012100,
                            StorageType = StorageType.ElementId,
                            Value = 99,
                        },
                    ],
                    Location = new LocationCurve
                    {
                        CurveType = "Line",
                        StartPoint = [0, 4, 0],
                        EndPoint = [4, 4, 0],
                    },
                    BoundingBox = [0, 3.5, 0, 4, 4.5, 3],
                    Geometry = BoxGeometry(0, 3.5, 0, 4, 4.5, 3),
                    CuttingElementIds = [],
                });
                break;
            default:
                Assert.Fail($"Unknown rejected source: {rejectedSource}");
                break;
        }

        string path = TempIfcPath($"opening-main-loop-reject-{rejectedSource}");
        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            var openingEntity = Assert.Single(
                OpeningEntities(ifc), entity => entity.Tag == opening.Id.ToString());
            Assert.False(HasPsetAttached(
                ifc, openingEntity.EntityId, "Pset_OpeningElementCommon"));
            Assert.False(HasPsetAttached(
                ifc, openingEntity.EntityId, "Pset_QuantityTakeOff"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void MainLoopWallOpening_SelectedHostWithoutQto_DoesNotFallBackToOpeningName()
    {
        var (info, wall, opening) = MainLoopWallOpening();
        wall.Name = null;
        opening.Name = "Forbidden opening-name fallback";
        string path = TempIfcPath("opening-main-loop-missing-host-qto");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            var openingEntity = Assert.Single(
                OpeningEntities(ifc), entity => entity.Tag == opening.Id.ToString());
            Assert.False(HasPsetAttached(
                ifc, openingEntity.EntityId, "Pset_OpeningElementCommon"));
            Assert.False(HasPsetAttached(
                ifc, openingEntity.EntityId, "Pset_QuantityTakeOff"));
            Assert.DoesNotContain(
                "IFCIDENTIFIER('Forbidden opening-name fallback')", ifc);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void MainLoopWallOpening_HostExportedAfterOpening_ReusesDeferredHostQtoReference()
    {
        var (info, wall, opening) = MainLoopWallOpening();
        info.Elements.Remove(opening);
        info.Elements.Insert(0, opening);
        string path = TempIfcPath("opening-main-loop-host-after-opening");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);
            var openingEntity = Assert.Single(
                OpeningEntities(ifc), entity => entity.Tag == opening.Id.ToString());
            Match wallMatch = Assert.Single(Regex.Matches(
                ifc,
                $@"#(\d+)=IFCWALL(?:STANDARDCASE)?\([^\r\n]*,'{wall.Id}'\);"));
            int wallEntityId = int.Parse(wallMatch.Groups[1].Value);

            int openingCommonValue = PsetValueIdsForEntity(
                ifc, openingEntity.EntityId, "Pset_OpeningElementCommon").Single();
            Assert.Equal(openingCommonValue, PsetValueIdsForEntity(
                ifc, openingEntity.EntityId, "Pset_QuantityTakeOff").Single());
            Assert.Equal(openingCommonValue, PsetValueIdsForEntity(
                ifc, wallEntityId, "Pset_QuantityTakeOff").Single());
            Assert.Single(Regex.Matches(
                ifc,
                @"IFCPROPERTYSINGLEVALUE\('Reference',(?:\$|''),IFCIDENTIFIER\('MainLoopHostWallReference'\),\$\)"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void MainLoopWallOpening_DuplicateOpeningId_FailsClosed()
    {
        var (info, _, opening) = MainLoopWallOpening();
        info.Elements.Add(new Element
        {
            Id = opening.Id,
            TypeId = opening.TypeId,
            IsDiscovered = true,
            CategoryId = opening.CategoryId,
            Category = opening.Category,
            Name = opening.Name,
            HostId = opening.HostId,
            SerializedHostId = opening.SerializedHostId,
            References = opening.References?.ToList(),
            Parameters = opening.Parameters.Select(parameter => new Parameter
            {
                Id = parameter.Id,
                StorageType = parameter.StorageType,
                Value = parameter.Value,
            }).ToList(),
            BoundingBox = opening.BoundingBox?.ToArray(),
            Geometry = opening.Geometry,
        });
        string path = TempIfcPath("opening-main-loop-duplicate-opening-id");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);
            var duplicateOpeningEntities = OpeningEntities(ifc)
                .Where(entity => entity.Tag == opening.Id.ToString())
                .ToList();
            Assert.Equal(2, duplicateOpeningEntities.Count);
            foreach (var entity in duplicateOpeningEntities)
            {
                Assert.False(HasPsetAttached(
                    ifc, entity.EntityId, "Pset_OpeningElementCommon"));
                Assert.False(HasPsetAttached(
                    ifc, entity.EntityId, "Pset_QuantityTakeOff"));
            }
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void MainLoopWallOpening_RejectedHostAuthority_PreservesLegacyOpeningNamePath()
    {
        var (info, wall, opening) = MainLoopWallOpening();
        wall.CuttingElementIds = [opening.Id + 1];
        opening.Name = "LegacyOpeningReference";
        string path = TempIfcPath("opening-main-loop-legacy-reference");

        try
        {
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            var openingEntity = Assert.Single(
                OpeningEntities(ifc), entity => entity.Tag == opening.Id.ToString());
            var openingCommonValues = PsetValueIdsForEntity(
                ifc, openingEntity.EntityId, "Pset_OpeningElementCommon");
            var openingQuantityValues = PsetValueIdsForEntity(
                ifc, openingEntity.EntityId, "Pset_QuantityTakeOff");
            Assert.Equal(openingCommonValues.Single(), openingQuantityValues.Single());
            Assert.Equal(
                "IFCIDENTIFIER('LegacyOpeningReference')",
                ResolveProperties(ifc, openingCommonValues)["Reference"]);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void StandaloneWallOpening_CurrentPrecedingHostWall_ExportsVoidRelationWithoutOrphans()
    {
        var (info, wall, opening) = MainLoopWallOpening();
        info.Elements.Remove(opening);
        info.Elements.Insert(0, opening);
        string path = TempIfcPath("opening-current-preceding-host");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            var openings = OpeningEntities(ifc);
            var emittedOpening = Assert.Single(openings);
            Assert.Equal(wall.Id.ToString(), emittedOpening.Tag);
            Match wallMatch = Assert.Single(Regex.Matches(
                ifc,
                $@"#(\d+)=IFCWALL(?:STANDARDCASE)?\([^\r\n]*,'{wall.Id}'\);"));
            int wallEntityId = int.Parse(wallMatch.Groups[1].Value);
            Assert.Single(Regex.Matches(ifc, $@"=IFCRELVOIDSELEMENT\([^;]*,#{wallEntityId},#{emittedOpening.EntityId}\);"));
            Assert.Equal(openings.Count, Regex.Matches(ifc, @"=IFCRELVOIDSELEMENT\(").Count);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void StandaloneWallOpening_CurrentPrefersVisibleSerializedHostOverHiddenWallReference()
    {
        var (info, wall, opening) = MainLoopWallOpening();
        var hiddenWall = new Element
        {
            Id = 1500,
            TypeId = 1001,
            CategoryId = -2000011,
            Category = "OST_Walls",
        };
        info.Elements.Add(hiddenWall);
        opening.References = [hiddenWall.Id, wall.Id, 99];
        opening.SerializedHostId = wall.Id;
        string path = TempIfcPath("opening-current-serialized-host-over-hidden-ref");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            var openings = OpeningEntities(ifc);
            var emittedOpening = Assert.Single(openings);
            Assert.Equal(wall.Id.ToString(), emittedOpening.Tag);
            Match wallMatch = Assert.Single(Regex.Matches(
                ifc,
                $@"#(\d+)=IFCWALL(?:STANDARDCASE)?\([^\r\n]*,'{wall.Id}'\);"));
            int wallEntityId = int.Parse(wallMatch.Groups[1].Value);
            Assert.Single(Regex.Matches(ifc, $@"=IFCRELVOIDSELEMENT\([^;]*,#{wallEntityId},#{emittedOpening.EntityId}\);"));
            Assert.Equal(openings.Count, Regex.Matches(ifc, @"=IFCRELVOIDSELEMENT\(").Count);
        }
        finally { Cleanup(path); }
    }

    private static (RevitFileInfo Info, Element Wall, Element Opening)
        MainLoopWallOpening()
    {
        const int phaseId = 99;
        var wall = new Element
        {
            Id = 1000,
            TypeId = 1001,
            IsDiscovered = true,
            CategoryId = -2000011,
            Category = "OST_Walls",
            Name = "MainLoopHostWallReference",
            References = [phaseId],
            Parameters =
            [
                new Parameter
                {
                    Id = -1012100,
                    StorageType = StorageType.ElementId,
                    Value = phaseId,
                },
            ],
            Location = new LocationCurve
            {
                CurveType = "Line",
                StartPoint = [0, 0, 0],
                EndPoint = [4, 0, 0],
            },
            BoundingBox = [0, -0.5, 0, 4, 0.5, 3],
            Geometry = BoxGeometry(0, -0.5, 0, 4, 0.5, 3),
            CuttingElementIds = [],
        };
        var opening = new Element
        {
            Id = 2000,
            TypeId = 841,
            IsDiscovered = true,
            CategoryId = -2000997,
            Category = "OST_SWallRectOpening",
            Name = null,
            HostId = wall.Id,
            SerializedHostId = wall.Id,
            References = [wall.Id, phaseId],
            Parameters =
            [
                new Parameter
                {
                    Id = -1012100,
                    StorageType = StorageType.ElementId,
                    Value = phaseId,
                },
                new Parameter
                {
                    Id = -1013201,
                    StorageType = StorageType.Integer,
                    Value = -4,
                },
            ],
            BoundingBox = [1, -0.75, 1, 2, 0.75, 2],
            Geometry = BoxGeometry(1, -0.75, 1, 2, 0.75, 2),
        };

        var info = new RevitFileInfo();
        info.Elements.AddRange([wall, opening]);
        return (info, wall, opening);
    }

    private static (RevitFileInfo Info, Element Slab, Element SketchLine)
        SourceOwnedSlabOpening()
    {
        const int phaseId = 99;
        var low = new List<double[]>
        {
            new[] { 1.0, 1.0, 0.0 },
            new[] { 2.0, 1.0, 0.0 },
            new[] { 2.0, 2.0, 0.0 },
            new[] { 1.0, 2.0, 0.0 },
        };
        var high = low.Select(point =>
            new[] { point[0], point[1], 0.5 }).ToList();
        var bottom = Face(
            [[0, 0, 0], [4, 0, 0], [4, 4, 0], [0, 4, 0]]);
        bottom.InnerVertices = [low];
        var top = Face(
            [[0, 0, 0.5], [0, 4, 0.5], [4, 4, 0.5], [4, 0, 0.5]]);
        top.InnerVertices = [high];
        var slab = new Element
        {
            Id = 100,
            TypeId = 101,
            Name = "HostFloorTypeMustNotBeInferred",
            CategoryId = -2000032,
            Category = "OST_Floors",
            References = [phaseId],
            Parameters =
            [
                new Parameter
                {
                    Id = -1012100,
                    StorageType = StorageType.Integer,
                    Value = phaseId,
                },
            ],
            BoundingBox = [0, 0, 0, 4, 4, 0.5],
            Geometry = new GeometryData
            {
                BoundingBox = [0, 0, 0, 4, 4, 0.5],
                Faces = [bottom, top],
            },
        };
        var sketchLine = new Element
        {
            Id = 120,
            CategoryId = -2000045,
            Category = "OST_SketchLines",
            References = [slab.Id],
            Location = new LocationCurve
            {
                CurveType = "Line",
                StartPoint = [1, 1, 7],
                EndPoint = [2, 1, 7],
            },
        };
        var info = new RevitFileInfo();
        info.Elements.AddRange([slab, sketchLine]);
        return (info, slab, sketchLine);
    }

    /// <summary>A wall with a "blind recess", a source-owned opening that does not go all the way through the wall. The exporter tags the resulting opening with the wall's own id in this case.</summary>
    private static (Element Wall, GeometryData Boundary) SourceOwnedOpeningWall()
    {
        double[][] Rectangle(double minimumAlong, double maximumAlong,
            double plane, double minimumZ, double maximumZ) =>
        [
            [minimumAlong, plane, minimumZ], [maximumAlong, plane, minimumZ],
            [maximumAlong, plane, maximumZ], [minimumAlong, plane, maximumZ],
        ];
        void AddConnectors(List<GeoFace> target,
            IReadOnlyList<double[]> first, IReadOnlyList<double[]> second)
        {
            for (int index = 0; index < 4; index++)
            {
                int next = (index + 1) % 4;
                target.Add(Face([first[index], first[next], second[next], second[index]]));
            }
        }

        double[][] lowOuter = Rectangle(0, 4, -0.5, 0, 3);
        double[][] highOuter = Rectangle(0, 4, 0.5, 0, 3);
        double[][] blindMouth = Rectangle(0.5, 3.5, -0.5, 0.25, 2.75);
        double[][] blindBack = Rectangle(0.5, 3.5, -0.49, 0.25, 2.75);
        double[][] filledRing = Rectangle(0.4, 3.4, 0.5, 0.25, 2.75);
        var lowSide = Face(lowOuter);
        lowSide.InnerVertices = [blindMouth.ToList()];
        var highSide = Face(highOuter.AsEnumerable().Reverse().ToArray());
        highSide.InnerVertices = [filledRing.AsEnumerable().Reverse().ToList()];
        var faces = new List<GeoFace> { lowSide, highSide };
        AddConnectors(faces, lowOuter, highOuter);
        faces.Add(Face(filledRing));
        faces.Add(Face(blindBack));
        AddConnectors(faces, blindMouth, blindBack);
        // A disconnected, exterior parallel remnant is not a recess back face.
        faces.Add(Face(Rectangle(0.5, 3.5, -0.52, 0.25, 2.75)));

        var boundary = new GeometryData
        {
            // The stored bounds name the wall's own extent; the remnant face is decoded separately.
            BoundingBox = [0, -0.5, 0, 4, 0.5, 3],
            Faces = faces,
            BrepSolids = [new GeoBrepSolid
            {
                FaceIndices = Enumerable.Range(0, faces.Count).ToList(),
                IsClosed = false,
            }],
        };
        var wall = new Element
        {
            Id = 1201,
            TypeId = 1202,
            CategoryId = -2000011,
            Category = "OST_Walls",
            BoundingBox = [0, -0.5, 0, 4, 0.5, 3],
            Location = new LocationCurve
            {
                CurveType = "Line",
                StartPoint = [0, 0, 0],
                EndPoint = [4, 0, 0],
            },
            Layers = [new MaterialLayer { LayerIndex = 0, ThicknessMm = 304.8 }],
            Parameters =
            [
                new Parameter { Id = -1001105, StorageType = StorageType.Double, Value = 3.0 },
            ],
            CuttingElementIds = [],
        };
        return (wall, boundary);
    }

    private static (Element Wall, Element FirstCutter, GeometryData Boundary)
        MixedHostedAndSourceOwnedOpeningWall()
    {
        static double[][] Rectangle(double minimumAlong, double maximumAlong,
            double plane, double minimumZ, double maximumZ) =>
        [
            [minimumAlong, plane, minimumZ], [maximumAlong, plane, minimumZ],
            [maximumAlong, plane, maximumZ], [minimumAlong, plane, maximumZ],
        ];

        double[][] lowOuter = Rectangle(0, 6, -0.5, 0, 3);
        double[][] highOuter = Rectangle(0, 6, 0.5, 0, 3);
        double[][] lowFirstHole = Rectangle(1, 2, -0.5, 1, 2);
        double[][] highFirstHole = Rectangle(1, 2, 0.5, 1, 2);
        double[][] lowSecondHole = Rectangle(3, 4, -0.5, 1, 2);
        double[][] highSecondHole = Rectangle(3, 4, 0.5, 1, 2);

        var lowSide = Face(lowOuter);
        lowSide.InnerVertices = [lowFirstHole.ToList(), lowSecondHole.ToList()];
        var highSide = Face(highOuter.AsEnumerable().Reverse().ToArray());
        highSide.InnerVertices = [highSecondHole.AsEnumerable().Reverse().ToList(), highFirstHole.AsEnumerable().Reverse().ToList()];
        var faces = new List<GeoFace> { lowSide, highSide };
        static void AddConnectors(List<GeoFace> target,
            IReadOnlyList<double[]> low, IReadOnlyList<double[]> high)
        {
            for (int index = 0; index < 4; index++)
            {
                int next = (index + 1) % 4;
                target.Add(Face([low[index], low[next], high[next], high[index]]));
            }
        }
        AddConnectors(faces, lowOuter, highOuter);
        AddConnectors(faces, lowFirstHole, highFirstHole);
        AddConnectors(faces, lowSecondHole, highSecondHole);

        var boundary = new GeometryData
        {
            BoundingBox = [0, -0.5, 0, 6, 0.5, 3],
            Faces = faces,
            BrepSolids = [new GeoBrepSolid
            {
                FaceIndices = Enumerable.Range(0, faces.Count).ToList(),
                IsClosed = true,
            }],
        };
        var wall = new Element
        {
            Id = 1501,
            TypeId = 1502,
            CategoryId = -2000011,
            Category = "OST_Walls",
            BoundingBox = [0, -0.5, 0, 6, 0.5, 3],
            Location = new LocationCurve
            {
                CurveType = "Line",
                StartPoint = [0, 0, 0],
                EndPoint = [6, 0, 0],
            },
            Layers = [new MaterialLayer { LayerIndex = 0, ThicknessMm = 304.8 }],
            Parameters =
            [
                new Parameter { Id = -1001105, StorageType = StorageType.Double, Value = 3.0 },
            ],
            CuttingElementIds = [1601, 1602],
        };
        var firstCutter = new Element
        {
            Id = 1601,
            TypeId = 1701,
            CategoryId = -2000023,
            Category = "OST_Doors",
            SerializedHostId = wall.Id,
            BoundingBox = [1, -0.5, 1, 2, 0.5, 2],
            Transform = new InstanceTransform
            {
                Origin = [1.5, 0, 1],
                BasisX = [1, 0, 0],
                BasisY = [0, 1, 0],
                BasisZ = [0, 0, 1],
            },
        };
        return (wall, firstCutter, boundary);
    }

    private static GeoFace Face(IEnumerable<double[]> vertices) => new()
    {
        OuterVertices = vertices.Select(vertex => (double[])vertex.Clone()).ToList(),
    };

    private static GeometryData BoxGeometry(
        double x0, double y0, double z0, double x1, double y1, double z1)
    {
        double[][] points =
        [
            [x0, y0, z0], [x1, y0, z0], [x1, y1, z0], [x0, y1, z0],
            [x0, y0, z1], [x1, y0, z1], [x1, y1, z1], [x0, y1, z1],
        ];
        int[][] faceIndices =
        [
            [0, 1, 2, 3], [4, 7, 6, 5],
            [0, 4, 5, 1], [1, 5, 6, 2], [2, 6, 7, 3], [3, 7, 4, 0],
        ];
        return new GeometryData
        {
            BoundingBox = [x0, y0, z0, x1, y1, z1],
            Faces = faceIndices.Select(indices => new GeoFace
            {
                OuterVertices = indices.Select(index => points[index]).ToList(),
            }).ToList(),
            BrepSolids = [new GeoBrepSolid
            {
                IsClosed = true,
                FaceIndices = Enumerable.Range(0, faceIndices.Length).ToList(),
            }],
        };
    }

    private static string TempIfcPath(string tag) =>
        Path.Combine(Path.GetTempPath(), $"revitreader-{tag}-{Guid.NewGuid():N}.ifc");

    private static void Cleanup(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private readonly record struct OpeningEntity(int EntityId, string Tag);

    /// <summary>Every "#id=IFCOPENINGELEMENT(...);" entity in the file, with its Tag argument
    /// (the last positional argument, a quoted digit string or "$").</summary>
    private static List<OpeningEntity> OpeningEntities(string ifc)
    {
        var pattern = new Regex(
            @"#(\d+)=IFCOPENINGELEMENT\([^;]*?,(?:'(\d+)'|\$)\);");
        return pattern.Matches(ifc)
            .Select(m => new OpeningEntity(
                int.Parse(m.Groups[1].Value),
                m.Groups[2].Success ? m.Groups[2].Value : "$"))
            .ToList();
    }

    /// <summary>Resolves the Pset of the given name attached to the given entity id via IfcRelDefinesByProperties and returns the ordered ids of its property value entities. Fails the test if no such attachment exists.</summary>
    private static List<int> PsetValueIdsForEntity(string ifc, int entityId, string psetName)
    {
        var relPattern = new Regex(
            $@"IFCRELDEFINESBYPROPERTIES\('[^']*',#\d+,(?:\$|''),(?:\$|''),\(#{entityId}\),#(\d+)\);");
        foreach (Match relMatch in relPattern.Matches(ifc))
        {
            int psetId = int.Parse(relMatch.Groups[1].Value);
            var psetPattern = new Regex(
                $@"#{psetId}=IFCPROPERTYSET\('[^']*',#\d+,'{Regex.Escape(psetName)}',(?:\$|''),\(([^)]*)\)\);");
            var psetMatch = psetPattern.Match(ifc);
            if (psetMatch.Success)
                return Regex.Matches(psetMatch.Groups[1].Value, @"#(\d+)")
                    .Select(m => int.Parse(m.Groups[1].Value)).ToList();
        }
        Assert.Fail($"No {psetName} attached to entity #{entityId} was found.");
        return [];
    }

    /// <summary>Same lookup as <see cref="PsetValueIdsForEntity"/>, but reports presence as a
    /// bool instead of failing the test - for asserting a Pset is deliberately absent (e.g. a
    /// filled opening must carry neither Pset_OpeningElementCommon nor Pset_QuantityTakeOff).</summary>
    private static bool HasPsetAttached(string ifc, int entityId, string psetName)
    {
        var relPattern = new Regex(
            $@"IFCRELDEFINESBYPROPERTIES\('[^']*',#\d+,(?:\$|''),(?:\$|''),\(#{entityId}\),#(\d+)\);");
        foreach (Match relMatch in relPattern.Matches(ifc))
        {
            int psetId = int.Parse(relMatch.Groups[1].Value);
            var psetPattern = new Regex(
                $@"#{psetId}=IFCPROPERTYSET\('[^']*',#\d+,'{Regex.Escape(psetName)}',(?:\$|''),\(([^)]*)\)\);");
            if (psetPattern.IsMatch(ifc)) return true;
        }
        return false;
    }

    /// <summary>Resolves each value entity id to its IfcPropertySingleValue and returns a map
    /// of property name -> the "TYPE(literal)" text of its NominalValue.</summary>
    private static Dictionary<string, string> ResolveProperties(string ifc, List<int> valueIds)
    {
        var result = new Dictionary<string, string>();
        foreach (int id in valueIds)
        {
            var pattern = new Regex($@"#{id}=IFCPROPERTYSINGLEVALUE\('([^']*)',(?:\$|''),([^,]+(?:\([^)]*\))?),\$\);");
            var match = Assert.Single(pattern.Matches(ifc));
            result[match.Groups[1].Value] = match.Groups[2].Value;
        }
        return result;
    }
}
