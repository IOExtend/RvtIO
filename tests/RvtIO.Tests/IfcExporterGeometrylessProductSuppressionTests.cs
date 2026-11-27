using RvtIO.Ifc;
using RvtIO.Meshes;
using RvtIO.Model;
using RvtIO.Parsing;
using System.Text.RegularExpressions;
using Xunit;

namespace RvtIO.Tests;

/// <summary>A product of an IfcElement subclass is emitted only if the exporter emitted at least one shape representation with at least one item for it, or it is the RelatingObject of an IfcRelAggregates whose parts are emitted. Otherwise neither the product, nor its IfcRelContainedInSpatialStructure, nor its Pset_QuantityTakeOff is written. The scope is IfcElement subclasses only, and the predicate keys on the exporter's emitted representation, never on a property of the input element.</summary>
public class IfcExporterGeometrylessProductSuppressionTests
{

    [Fact]
    public void GeometrylessElement_YieldsNoProduct_NoContainment_NoPset()
    {
        var info = new RevitFileInfo();
        info.Elements.Add(Proxy(id: 300, typeId: 301, name: "GhostType", geometry: null));
        string path = TempIfcPath("geometryless-suppressed");

        try
        {
            var diagnostics = new StringWriter();
            new IfcExporter(diagnostics).Export(info, path);
            string ifc = File.ReadAllText(path);

            // No product...
            Assert.Empty(Regex.Matches(ifc, @"#\d+=IFCBUILDINGELEMENTPROXY\("));
            // ...no Pset_QuantityTakeOff (it would otherwise be attached to every tagged
            // product on the default path)...
            Assert.DoesNotContain("Pset_QuantityTakeOff", ifc);
            // ...and no spatial containment. The only IfcRelContainedInSpatialStructure the
            // file could contain is this element's, so any occurrence is a failure.
            Assert.Empty(Regex.Matches(ifc, @"#\d+=IFCRELCONTAINEDINSPATIALSTRUCTURE\("));

            // The suppression is reported as an invariant, broken down by IFC class.
            Assert.Contains(
                "IFC geometry-less element products suppressed: 1 - IFCBUILDINGELEMENTPROXY=1",
                diagnostics.ToString());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ElementWithEmittedRepresentation_IsStillExported()
    {
        // The other side of the same gate: give the identical element a real closed body and
        // it must survive untouched, complete with its containment and its Pset.
        var info = new RevitFileInfo();
        info.Elements.Add(Proxy(id: 300, typeId: 301, name: "GhostType", geometry: SolidPrism()));
        string path = TempIfcPath("geometryless-retained");

        try
        {
            var diagnostics = new StringWriter();
            new IfcExporter(diagnostics).Export(info, path);
            string ifc = File.ReadAllText(path);

            var product = Assert.Single(Regex.Matches(ifc, @"#\d+=IFCBUILDINGELEMENTPROXY\((.*?)\);"));
            // Representation attribute (7th) is a real reference, not $.
            Assert.DoesNotContain(",$,'300',", product.Groups[1].Value);
            Assert.Single(Regex.Matches(ifc, "'Pset_QuantityTakeOff'"));
            Assert.Single(Regex.Matches(ifc, @"#\d+=IFCRELCONTAINEDINSPATIALSTRUCTURE\("));
            Assert.Contains("IFC geometry-less element products suppressed: 0", diagnostics.ToString());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void TwoInstancesOfTheSameType_AreSplitPerInstance()
    {
        var info = new RevitFileInfo();
        info.Elements.Add(Proxy(id: 300, typeId: 301, name: "SharedType", geometry: null));
        info.Elements.Add(Proxy(id: 302, typeId: 301, name: "SharedType", geometry: SolidPrism()));
        string path = TempIfcPath("geometryless-per-instance");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            var products = Regex.Matches(ifc, @"#\d+=IFCBUILDINGELEMENTPROXY\('[^']*',#\d+,'[^']*',(?:\$|''),'[^']*',#\d+,[^,]+,'(\d+)'");
            Assert.Equal(["302"], products.Select(m => m.Groups[1].Value).ToArray());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ElementWhoseAuthoredFacesProduceNoRepresentation_IsSuppressed()
    {
        // Four authored faces with three vertices each pass every input-side geometry test, but the faces are collinear and collapse at IFC precision, so no representation is emitted. The rule follows the exporter's outcome: the product is suppressed.
        var info = new RevitFileInfo();
        info.Elements.Add(Proxy(id: 310, typeId: 311, name: "CollinearType",
            geometry: CollinearFacesWithoutRepresentation()));
        string path = TempIfcPath("geometryless-outcome-keyed");

        try
        {
            var diagnostics = new StringWriter();
            new IfcExporter(diagnostics).Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Empty(Regex.Matches(ifc, @"#\d+=IFCBUILDINGELEMENTPROXY\("));
            Assert.Contains(
                "IFC geometry-less element products suppressed: 1 - IFCBUILDINGELEMENTPROXY=1",
                diagnostics.ToString());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Predicate_ConsumesTheEmittedRepresentation_NotTheClassAlone()
    {
        Assert.True(IfcExporter.ShouldSuppressGeometrylessElementProduct(
            "IFCBUILDINGELEMENTPROXY", hasEmittedRepresentation: false,
            isEmittedAggregateParent: false));
        Assert.False(IfcExporter.ShouldSuppressGeometrylessElementProduct(
            "IFCBUILDINGELEMENTPROXY", hasEmittedRepresentation: true,
            isEmittedAggregateParent: false));
    }

    [Fact]
    public void AggregateParentWithoutRepresentation_IsNotSuppressed()
    {
        var info = new RevitFileInfo();
        info.Elements.Add(Proxy(id: 320, typeId: 321, name: "ContainerType", geometry: null));
        string path = TempIfcPath("geometryless-aggregate-parent");

        try
        {
            var exporter = new IfcExporter();
            exporter.RegisterEmittedAggregateParent(320);
            exporter.Export(info, path);
            string ifc = File.ReadAllText(path);

            var product = Assert.Single(Regex.Matches(ifc, @"#\d+=IFCBUILDINGELEMENTPROXY\("));
            Assert.True(product.Success);
            Assert.Single(Regex.Matches(ifc, "'Pset_QuantityTakeOff'"));
            Assert.Single(Regex.Matches(ifc, @"#\d+=IFCRELCONTAINEDINSPATIALSTRUCTURE\("));
        }
        finally { Cleanup(path); }
    }

    [Theory]
    [InlineData("IFCCURTAINWALL")]
    [InlineData("IFCSTAIR")]
    [InlineData("IFCROOF")]
    [InlineData("IFCBUILDINGELEMENTPROXY")]
    public void Predicate_ExemptsAggregateParentsOfEveryElementClass(string ifcType)
    {
        Assert.True(IfcExporter.ShouldSuppressGeometrylessElementProduct(
            ifcType, hasEmittedRepresentation: false, isEmittedAggregateParent: false));
        Assert.False(IfcExporter.ShouldSuppressGeometrylessElementProduct(
            ifcType, hasEmittedRepresentation: false, isEmittedAggregateParent: true));
    }

    [Fact]
    public void RepresentationlessSiteFamily_IsSuppressed()
    {
        var info = new RevitFileInfo();
        info.Elements.Add(SiteInstance(id: 330, typeId: 331));
        string path = TempIfcPath("geometryless-site-scope");

        try
        {
            var diagnostics = new StringWriter();
            new IfcExporter(diagnostics).Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Single(Regex.Matches(ifc, @"#\d+=IFCSITE\("));
            Assert.DoesNotContain("=IFCBUILDINGELEMENTPROXY(", ifc);
            Assert.Contains("IFC geometry-less element products suppressed: 1 - IFCBUILDINGELEMENTPROXY=1", diagnostics.ToString());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void SpatialStructure_SurvivesAFileWhoseEveryElementIsSuppressed()
    {
        var info = new RevitFileInfo();
        info.Elements.Add(new Element
        {
            Id = 10, TypeId = 1010, CategoryId = -2000240, Category = "OST_Levels", Name = "L1", IsNativeLevel = true,
            SerializedFamilyId = -1, SerializedUnplacedOwnerId = -1, SerializedOwnerDbViewId = -1,
            Parameters =
            [
                new Parameter { Id = -1007102, StorageType = StorageType.Double, Value = 0.0 },
                new Parameter { Id = -1007111, StorageType = StorageType.Integer, Value = 1 },
            ],
        });
        info.Elements.Add(Proxy(id: 340, typeId: 341, name: "GhostType", geometry: null));
        string path = TempIfcPath("geometryless-spatial-survives");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Single(Regex.Matches(ifc, @"#\d+=IFCPROJECT\("));
            Assert.Single(Regex.Matches(ifc, @"#\d+=IFCSITE\("));
            Assert.Single(Regex.Matches(ifc, @"#\d+=IFCBUILDING\("));
            Assert.True(Regex.Matches(ifc, @"#\d+=IFCBUILDINGSTOREY\(").Count >= 1);
        }
        finally { Cleanup(path); }
    }

    [Theory]
    // In scope: IfcElement subclasses this exporter emits.
    [InlineData("IFCBUILDINGELEMENTPROXY", true)]
    [InlineData("IFCFURNISHINGELEMENT", true)]
    [InlineData("IFCFLOWTERMINAL", true)]
    [InlineData("IFCCURTAINWALL", true)]
    [InlineData("IFCWALLSTANDARDCASE", true)]
    [InlineData("IFCSTAIR", true)]
    [InlineData("IFCROOF", true)]
    [InlineData("IFCSITE", false)]
    [InlineData("IFCBUILDING", false)]
    [InlineData("IFCBUILDINGSTOREY", false)]
    [InlineData("IFCSPACE", false)]
    [InlineData("IFCDISTRIBUTIONPORT", false)]
    [InlineData("IFCANNOTATION", false)]
    [InlineData("IFCGRID", false)]
    public void IsIfcElementSubclassProduct_DrawsTheScopeBoundary(string ifcType, bool expected)
    {
        Assert.Equal(expected, IfcExporter.IsIfcElementSubclassProduct(ifcType));
        // ...and the predicate inherits exactly that boundary.
        Assert.Equal(expected, IfcExporter.ShouldSuppressGeometrylessElementProduct(
            ifcType, hasEmittedRepresentation: false, isEmittedAggregateParent: false));
    }

    [Fact]
    public void OpenFourFaceSurfaceWithMaterializedOpening_ExportsOnlyTheOpening()
    {
        var fixture = HostedProxyCutter(OpenCutterShell(4), materializedOpening: true);
        string path = TempIfcPath("open-surface-cutter");

        try
        {
            var diagnostics = new StringWriter();
            new IfcExporter(diagnostics).Export(fixture.Info, path);
            string ifc = File.ReadAllText(path);

            Assert.DoesNotContain("=IFCBUILDINGELEMENTPROXY(", ifc);
            Assert.Single(Regex.Matches(ifc, "=IFCOPENINGELEMENT\\("));
            Assert.Single(Regex.Matches(ifc, "=IFCRELVOIDSELEMENT\\("));
            Assert.DoesNotContain("=IFCRELFILLSELEMENT(", ifc);
            // The decision occurs after the body writer; rollback must remove that attempted
            // SurfaceModel rather than leaving unreferenced STEP geometry behind.
            Assert.DoesNotContain("=IFCSHELLBASEDSURFACEMODEL(", ifc);
            Assert.Contains(
                "IFC open-surface hosted-cutter proxy products suppressed: 1",
                diagnostics.ToString());
            Assert.Contains(
                "IFC geometry-less element products suppressed: 0",
                diagnostics.ToString());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void MultiHostOpenSurfaceCutter_PreservesDirectAndPrecomputedSecondaryOpenings()
    {
        // The direct wall has no straight LocationCurve, so only the plain secondary host is materialized by precompute; the surviving-product branch still registers both inverse hosts. Suppression must preserve that host set.
        var fixture = HostedProxyCutter(
            OpenCutterShell(4), materializedOpening: true);
        const int directWallId = 200;
        var directBoundary = WriterUnresolvedHostGeometry();
        var directWall = new Element
        {
            Id = directWallId,
            TypeId = 201,
            CategoryId = -2000011,
            Category = "OST_Walls",
            Name = "Direct writer-unresolved wall",
            References = [3, fixture.Cutter.Id],
            CuttingElementIds = [fixture.Cutter.Id],
            Parameters =
            [
                PhaseParameter(),
                new Parameter
                {
                    Id = -1001000,
                    Definition = new Definition
                    {
                        Name = "WALL_ATTR_WIDTH_PARAM",
                    },
                    StorageType = StorageType.Double,
                    Value = 1.0,
                },
            ],
            BoundingBox = [0, -0.5, 0, 4, 0.5, 3],
            Geometry = directBoundary,
        };
        fixture.Cutter.SerializedHostId = directWallId;
        fixture.Cutter.HostId = directWallId;
        fixture.Cutter.References = [directWallId, 3];
        ShiftGeometryZ(fixture.Cutter.Geometry!, -1);
        fixture.Cutter.BoundingBox = [1, -0.6, 0, 2, 0.6, 1];
        fixture.Info.Elements.Insert(0, directWall);
        string path = TempIfcPath("multi-host-open-surface-cutter");

        try
        {
            // The legacy profile: the current profile writes no opening beside the writer-unresolved wall, which has no axis and is a plain IfcWall.
            new IfcExporter() { Profile = IfcProfile.Legacy }.Export(fixture.Info, path);
            string ifc = File.ReadAllText(path);

            Assert.DoesNotContain("=IFCBUILDINGELEMENTPROXY(", ifc);
            Assert.Equal(2, Regex.Matches(ifc, "=IFCOPENINGELEMENT\\(").Count);
            Assert.Equal(2, Regex.Matches(ifc, "=IFCRELVOIDSELEMENT\\(").Count);
            Assert.DoesNotContain("=IFCRELFILLSELEMENT(", ifc);
            Assert.Equal(11, Regex.Matches(ifc, "=IFCPROPERTYSET\\(").Count);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ClosedBrepWithMaterializedOpening_RetainsProxyAndOpening()
    {
        var fixture = HostedProxyCutter(ClosedCutterBody(), materializedOpening: true);
        string path = TempIfcPath("closed-cutter");

        try
        {
            var diagnostics = new StringWriter();
            new IfcExporter(diagnostics).Export(fixture.Info, path);
            string ifc = File.ReadAllText(path);

            Assert.Single(Regex.Matches(ifc, "=IFCBUILDINGELEMENTPROXY\\("));
            // The current profile writes the closed vertical box as a SweptSolid.
            Assert.Contains("=IFCEXTRUDEDAREASOLID(", ifc);
            Assert.Single(Regex.Matches(ifc, "=IFCOPENINGELEMENT\\("));
            Assert.Contains(
                "IFC open-surface hosted-cutter proxy products suppressed: 0",
                diagnostics.ToString());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void OpenFourFaceSurfaceWithoutMaterializedOpening_RetainsProxy()
    {
        var fixture = HostedProxyCutter(OpenCutterShell(4), materializedOpening: false);
        string path = TempIfcPath("open-surface-no-opening");

        try
        {
            var diagnostics = new StringWriter();
            new IfcExporter(diagnostics).Export(fixture.Info, path);
            string ifc = File.ReadAllText(path);

            Assert.Single(Regex.Matches(ifc, "=IFCBUILDINGELEMENTPROXY\\("));
            Assert.Single(Regex.Matches(ifc, "=IFCFACEBASEDSURFACEMODEL\\("));
            Assert.DoesNotContain("=IFCOPENINGELEMENT(", ifc);
            Assert.Contains(
                "IFC open-surface hosted-cutter proxy products suppressed: 0",
                diagnostics.ToString());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void OpenSurface_IsFaceBasedInCurrentAndShellBasedInLegacy()
    {
        foreach (var (version, written, other, set) in new[]
        {
            (IfcProfile.Current, "IFCFACEBASEDSURFACEMODEL", "IFCSHELLBASEDSURFACEMODEL", "IFCCONNECTEDFACESET"),
            (IfcProfile.Legacy, "IFCSHELLBASEDSURFACEMODEL", "IFCFACEBASEDSURFACEMODEL", "IFCOPENSHELL"),
        })
        {
            var fixture = HostedProxyCutter(OpenCutterShell(4), materializedOpening: false);
            string path = TempIfcPath("open-surface-dialect");
            try
            {
                new IfcExporter(new StringWriter()) { Profile = version }.Export(fixture.Info, path);
                string ifc = File.ReadAllText(path);
                Assert.Single(Regex.Matches(ifc, $"={written}\\("));
                Assert.DoesNotContain($"={other}(", ifc);
                Assert.Single(Regex.Matches(ifc, $"={set}\\("));
            }
            finally { Cleanup(path); }
        }
    }

    [Fact]
    public void OpenFiveFaceSurfaceWithMaterializedOpening_RetainsProxy()
    {
        var fixture = HostedProxyCutter(OpenCutterShell(5), materializedOpening: true);
        string path = TempIfcPath("five-face-surface-cutter");

        try
        {
            new IfcExporter().Export(fixture.Info, path);
            string ifc = File.ReadAllText(path);

            Assert.Single(Regex.Matches(ifc, "=IFCBUILDINGELEMENTPROXY\\("));
            Assert.Single(Regex.Matches(ifc, "=IFCFACEBASEDSURFACEMODEL\\("));
            Assert.Single(Regex.Matches(ifc, "=IFCOPENINGELEMENT\\("));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void MappedOpenFourFaceSurfaceWithMaterializedOpening_ExportsOnlyTheOpening()
    {
        var fixture = HostedProxyCutter(OpenCutterShell(4), materializedOpening: true,
            mapped: true);
        string path = TempIfcPath("mapped-open-surface-cutter");

        try
        {
            var diagnostics = new StringWriter();
            new IfcExporter(diagnostics).Export(fixture.Info, path);
            string ifc = File.ReadAllText(path);

            Assert.DoesNotContain("=IFCBUILDINGELEMENTPROXY(", ifc);
            Assert.Single(Regex.Matches(ifc, "=IFCOPENINGELEMENT\\("));
            // All map payloads/items were created after the element checkpoint and must be
            // rolled back together with the product.
            Assert.DoesNotContain("=IFCREPRESENTATIONMAP(", ifc);
            Assert.DoesNotContain("=IFCMAPPEDITEM(", ifc);
            Assert.DoesNotContain("=IFCSHELLBASEDSURFACEMODEL(", ifc);
            Assert.Contains(
                "IFC open-surface hosted-cutter proxy products suppressed: 1",
                diagnostics.ToString());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void MappedClosedBodyWithMaterializedOpening_RetainsProxy()
    {
        var fixture = HostedProxyCutter(ClosedCutterBody(), materializedOpening: true,
            mapped: true);
        string path = TempIfcPath("mapped-closed-cutter");

        try
        {
            new IfcExporter().Export(fixture.Info, path);
            string ifc = File.ReadAllText(path);

            Assert.Single(Regex.Matches(ifc, "=IFCBUILDINGELEMENTPROXY\\("));
            Assert.Single(Regex.Matches(ifc, "=IFCREPRESENTATIONMAP\\("));
            Assert.Single(Regex.Matches(ifc, "=IFCMAPPEDITEM\\("));
            Assert.Contains("=IFCEXTRUDEDAREASOLID(", ifc);
            Assert.Single(Regex.Matches(ifc, "=IFCOPENINGELEMENT\\("));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void RolledBackMappedCutter_DoesNotPoisonLaterMapCacheConsumer()
    {
        const int symbolId = 901;
        var fixture = HostedProxyCutter(OpenCutterShell(4), materializedOpening: true,
            mapped: true, symbolId: symbolId);
        var retained = Proxy(302, symbolId, "Later mapped product", OpenCutterShell(4));
        retained.Geometry!.ExactMappedInstances =
        [
            new GeoInstance
            {
                SymbolId = symbolId,
                Transform = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0],
            },
        ];
        fixture.Info.Elements.Add(retained);
        string path = TempIfcPath("mapped-cache-after-rollback");

        try
        {
            new IfcExporter().Export(fixture.Info, path);
            string ifc = File.ReadAllText(path);

            var proxy = Assert.Single(Regex.Matches(
                ifc, @"#\d+=IFCBUILDINGELEMENTPROXY\([^\r\n]*,'302',\$\);"));
            Assert.True(proxy.Success);
            Assert.Single(Regex.Matches(ifc, "=IFCREPRESENTATIONMAP\\("));
            Assert.Single(Regex.Matches(ifc, "=IFCMAPPEDITEM\\("));
            Assert.Single(Regex.Matches(ifc, "=IFCOPENINGELEMENT\\("));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void UnplacedOwnerGate_IsPositiveAndProxyOnly()
    {
        Assert.True(IfcExporter.ShouldSuppressUnplacedOwnerProxyProduct(
            "IFCBUILDINGELEMENTPROXY", 1));
        Assert.True(IfcExporter.ShouldSuppressUnplacedOwnerProxyProduct(
            "IFCBUILDINGELEMENTPROXY", int.MaxValue));

        foreach (int? retainedOwner in new int?[] { null, int.MinValue, -2, -1, 0 })
            Assert.False(IfcExporter.ShouldSuppressUnplacedOwnerProxyProduct(
                "IFCBUILDINGELEMENTPROXY", retainedOwner));

        foreach (string retainedClass in new[]
                 {
                     "IFCFURNISHINGELEMENT", "IFCSLAB", "IFCFLOWTERMINAL",
                 })
            Assert.False(IfcExporter.ShouldSuppressUnplacedOwnerProxyProduct(
                retainedClass, 1));
    }

    [Fact]
    public void PositiveUnplacedOwnerClosedProxy_EmitsNoProductPsetContainmentOrBody()
    {
        var proxy = Proxy(300, 301, "Nested proxy", SolidPrism());
        proxy.SerializedUnplacedOwnerId = 700;
        var info = new RevitFileInfo();
        info.Elements.Add(proxy);
        string path = TempIfcPath("unplaced-owner-suppressed");

        try
        {
            var diagnostics = new StringWriter();
            new IfcExporter(diagnostics).Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.DoesNotContain("=IFCBUILDINGELEMENTPROXY(", ifc);
            Assert.DoesNotContain("Pset_QuantityTakeOff", ifc);
            Assert.DoesNotContain("=IFCFACETEDBREP(", ifc);
            Assert.DoesNotContain("=IFCRELCONTAINEDINSPATIALSTRUCTURE(", ifc);
            Assert.Contains(
                "IFC serialized-unplaced-owner proxy products suppressed: 1",
                diagnostics.ToString());
            Assert.Contains(
                "IFC open-surface hosted-cutter proxy products suppressed: 0",
                diagnostics.ToString());
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void SameTypePositiveAndNegativeOwners_SuppressOnlyPositiveOccurrence()
    {
        var suppressed = Proxy(300, 901, "Shared type", SolidPrism());
        suppressed.SerializedUnplacedOwnerId = 800;
        var retained = Proxy(302, 901, "Shared type", SolidPrism());
        retained.SerializedUnplacedOwnerId = -1;
        var info = new RevitFileInfo();
        info.Elements.Add(suppressed);
        info.Elements.Add(retained);
        string path = TempIfcPath("unplaced-owner-same-type");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            Match product = Assert.Single(Regex.Matches(
                ifc, @"#\d+=IFCBUILDINGELEMENTPROXY\([^\r\n]*,'302',\$\);"));
            Assert.True(product.Success);
            Assert.DoesNotContain("'300',$", ifc);
            Assert.Single(Regex.Matches(ifc, "'Pset_QuantityTakeOff'"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void NullZeroAndNegativeUnplacedOwners_AllRetainTheirProducts()
    {
        var nullOwner = Proxy(300, 901, "Null owner", SolidPrism());
        var zeroOwner = Proxy(302, 901, "Zero owner", SolidPrism());
        zeroOwner.SerializedUnplacedOwnerId = 0;
        var negativeOwner = Proxy(304, 901, "Negative owner", SolidPrism());
        negativeOwner.SerializedUnplacedOwnerId = -2;
        var info = new RevitFileInfo();
        info.Elements.Add(nullOwner);
        info.Elements.Add(zeroOwner);
        info.Elements.Add(negativeOwner);
        string path = TempIfcPath("unplaced-owner-nonpositive");

        try
        {
            new IfcExporter().Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Equal(3, Regex.Matches(ifc, "=IFCBUILDINGELEMENTPROXY\\(").Count);
            Assert.Contains("'300',$", ifc);
            Assert.Contains("'302',$", ifc);
            Assert.Contains("'304',$", ifc);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void PositiveUnplacedOwnerOnNonProxy_RetainsFurnishingProduct()
    {
        var furnishing = Proxy(300, 301, "Nested-looking chair", SolidPrism());
        furnishing.CategoryId = -2000080;
        furnishing.Category = "OST_Furniture";
        furnishing.SerializedUnplacedOwnerId = 700;
        var info = new RevitFileInfo();
        info.Elements.Add(furnishing);
        string path = TempIfcPath("unplaced-owner-nonproxy");

        try
        {
            // The legacy profile only: the unplaced-owner gate is limited to proxies there.
            new IfcExporter { Profile = IfcProfile.Legacy }.Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.Single(Regex.Matches(ifc, "=IFCFURNISHINGELEMENT\\("));
            Assert.Single(Regex.Matches(
                ifc, @"=IFCFURNISHINGELEMENT\([^\r\n]*,'300'"));
            Assert.Contains("Pset_QuantityTakeOff", ifc);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void PositiveUnplacedOwnerOnNonProxy_IsNotAProductInCurrent()
    {
        // NonSpatialCollector keeps only elements with no unplaced owner, whatever their class.
        var furnishing = Proxy(300, 301, "Nested-looking chair", SolidPrism());
        furnishing.CategoryId = -2000080;
        furnishing.Category = "OST_Furniture";
        furnishing.SerializedUnplacedOwnerId = 700;
        var info = new RevitFileInfo();
        info.Elements.Add(furnishing);
        string path = TempIfcPath("unplaced-owner-nonproxy-277");

        try
        {
            new IfcExporter { Profile = IfcProfile.Current }.Export(info, path);
            Assert.DoesNotContain("=IFCFURNISHINGELEMENT(", File.ReadAllText(path));
        }
        finally { Cleanup(path); }
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void PositiveUnplacedOwner_PreservesOnlyMaterializedOpening(
        bool materializedOpening, int expectedOpenings)
    {
        var fixture = HostedProxyCutter(
            ClosedCutterBody(), materializedOpening);
        fixture.Cutter.SerializedUnplacedOwnerId = 700;
        string path = TempIfcPath("unplaced-owner-opening");

        try
        {
            new IfcExporter().Export(fixture.Info, path);
            string ifc = File.ReadAllText(path);

            Assert.DoesNotContain("=IFCBUILDINGELEMENTPROXY(", ifc);
            Assert.Equal(expectedOpenings,
                Regex.Matches(ifc, "=IFCOPENINGELEMENT\\(").Count);
            Assert.Equal(expectedOpenings,
                Regex.Matches(ifc, "=IFCRELVOIDSELEMENT\\(").Count);
            Assert.DoesNotContain("=IFCRELFILLSELEMENT(", ifc);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void SkippedMappedUnplacedOwner_DoesNotCreateOrPoisonMapCache()
    {
        const int symbolId = 901;
        var fixture = HostedProxyCutter(
            OpenCutterShell(4), materializedOpening: false,
            mapped: true, symbolId: symbolId);
        fixture.Cutter.SerializedUnplacedOwnerId = 700;
        var retained = Proxy(302, symbolId, "Later mapped product", OpenCutterShell(4));
        retained.Geometry!.ExactMappedInstances =
        [
            new GeoInstance
            {
                SymbolId = symbolId,
                Transform = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0],
            },
        ];
        fixture.Info.Elements.Add(retained);
        string path = TempIfcPath("unplaced-owner-before-map-cache");

        try
        {
            new IfcExporter().Export(fixture.Info, path);
            string ifc = File.ReadAllText(path);

            Match product = Assert.Single(Regex.Matches(
                ifc, @"#\d+=IFCBUILDINGELEMENTPROXY\([^\r\n]*,'302',\$\);"));
            Assert.True(product.Success);
            Assert.Single(Regex.Matches(ifc, "=IFCREPRESENTATIONMAP\\("));
            Assert.Single(Regex.Matches(ifc, "=IFCMAPPEDITEM\\("));
            Assert.DoesNotContain("=IFCOPENINGELEMENT(", ifc);
        }
        finally { Cleanup(path); }
    }

    private static (RevitFileInfo Info, Element Wall, Element Cutter) HostedProxyCutter(
        GeometryData writerGeometry, bool materializedOpening, bool mapped = false,
        int symbolId = 901)
    {
        const int phaseId = 3;
        const int wallId = 100;
        const int cutterId = 300;
        var wall = new Element
        {
            Id = wallId,
            TypeId = 101,
            CategoryId = -2000011,
            Category = "OST_Walls",
            Name = "Host wall",
            References = [phaseId, cutterId],
            CuttingElementIds = materializedOpening ? [cutterId] : [],
            Parameters =
            [
                PhaseParameter(),
                new Parameter
                {
                    Id = -1001000,
                    Definition = new Definition { Name = "WALL_ATTR_WIDTH_PARAM" },
                    StorageType = StorageType.Double,
                    Value = 1.0,
                },
            ],
            Location = new LocationCurve
            {
                CurveType = "Line",
                StartPoint = [0, 0, 0],
                EndPoint = [4, 0, 0],
            },
            BoundingBox = [0, -0.5, 0, 4, 0.5, 3],
        };
        var instanceGeometry = writerGeometry;
        if (mapped)
            instanceGeometry.ExactMappedInstances =
            [
                new GeoInstance
                {
                    SymbolId = symbolId,
                    Transform = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0],
                },
            ];
        var cutter = new Element
        {
            Id = cutterId,
            TypeId = mapped ? symbolId : 301,
            IsDiscovered = true,
            CategoryId = -2000151,
            Category = "OST_GenericModel",
            Name = "Cutter proxy",
            FamilyName = "Cutter family",
            SerializedHostId = wallId,
            HostId = wallId,
            References = [wallId, phaseId],
            Parameters = [PhaseParameter()],
            BoundingBox = [1, -0.6, 1, 2, 0.6, 2],
            Geometry = instanceGeometry,
        };
        var info = new RevitFileInfo();
        info.Elements.Add(wall);
        if (mapped)
            info.Elements.Add(new Element
            {
                Id = symbolId,
                IsBlockOnly = true,
                Geometry = CloneWriterGeometry(writerGeometry),
            });
        info.Elements.Add(cutter);
        return (info, wall, cutter);
    }

    private static GeometryData CloneWriterGeometry(GeometryData source) => new()
    {
        BoundingBox = source.BoundingBox?.ToArray(),
        Faces = source.Faces.Select(face => new GeoFace
        {
            OuterVertices = face.OuterVertices?.Select(point => point.ToArray()).ToList(),
        }).ToList(),
        BrepSolids = source.BrepSolids?.Select(solid => new GeoBrepSolid
        {
            IsClosed = solid.IsClosed,
            FaceIndices = solid.FaceIndices.ToList(),
        }).ToList(),
    };

    private static GeometryData WriterUnresolvedHostGeometry()
    {
        (double Along, double Z)[] profile =
        [
            (0, 0), (4, 0), (4, 1), (0, 1),
        ];
        var low = profile.Select(point =>
            new[] { point.Along, -0.5, point.Z }).ToList();
        var high = profile.Select(point =>
            new[] { point.Along, 0.5, point.Z }).ToList();
        var faces = new List<GeoFace>
        {
            new() { OuterVertices = low },
            new() { OuterVertices = high.AsEnumerable().Reverse().ToList() },
        };
        for (int index = 0; index < low.Count; index++)
        {
            int next = (index + 1) % low.Count;
            faces.Add(new GeoFace
            {
                OuterVertices =
                [
                    low[index], high[index], high[next], low[next],
                ],
            });
        }
        return new GeometryData
        {
            BoundingBox = [0, -0.5, 0, 4, 0.5, 1],
            Faces = faces,
            BrepSolids =
            [
                new GeoBrepSolid
                {
                    FaceIndices = Enumerable.Range(0, faces.Count).ToList(),
                    IsClosed = true,
                },
            ],
        };
    }

    private static void ShiftGeometryZ(GeometryData geometry, double delta)
    {
        foreach (double[] point in geometry.Faces
                     .SelectMany(face => face.OuterVertices ?? []))
            point[2] += delta;
        if (geometry.BoundingBox is { Length: 6 } bounds)
        {
            bounds[2] += delta;
            bounds[5] += delta;
        }
    }

    private static GeometryData OpenCutterShell(int faceCount)
    {
        if (faceCount is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(faceCount));
        double[][] points =
        [
            [1, -0.6, 1], [2, -0.6, 1], [2, 0.6, 1], [1, 0.6, 1],
            [1, -0.6, 2], [2, -0.6, 2], [2, 0.6, 2], [1, 0.6, 2],
        ];
        int[][] openFaces =
        [
            [0, 1, 5, 4], [1, 2, 6, 5],
            [2, 3, 7, 6], [3, 0, 4, 7],
            [0, 3, 2, 1],
        ];
        var faces = openFaces.Take(faceCount).Select(indices => new GeoFace
        {
            OuterVertices = indices.Select(index => points[index]).ToList(),
        }).ToList();
        return new GeometryData
        {
            BoundingBox = [1, -0.6, 1, 2, 0.6, 2],
            Faces = faces,
            BrepSolids =
            [
                new GeoBrepSolid
                {
                    IsClosed = false,
                    FaceIndices = Enumerable.Range(0, faceCount).ToList(),
                },
            ],
        };
    }

    private static GeometryData ClosedCutterBody()
    {
        GeometryData closed = OpenCutterShell(5);
        closed.Faces.Add(new GeoFace
        {
            OuterVertices =
            [
                [1, -0.6, 2], [2, -0.6, 2], [2, 0.6, 2], [1, 0.6, 2],
            ],
        });
        closed.BrepSolids![0].IsClosed = true;
        closed.BrepSolids[0].FaceIndices.Add(5);
        return closed;
    }

    private static Element Proxy(int id, int typeId, string? name, GeometryData? geometry) =>
        new()
        {
            Id = id,
            TypeId = typeId,
            IsDiscovered = true,
            CategoryId = -2000151, // OST_GenericModel -> IFCBUILDINGELEMENTPROXY
            Category = "OST_GenericModel",
            Name = name,
            FamilyName = "GhostFamily",
            References = [3],
            Parameters = [PhaseParameter()],
            BoundingBox = [0, 0, 0, 4, 3, 2],
            Geometry = geometry,
        };

    private static Element SiteInstance(int id, int typeId) =>
        new()
        {
            Id = id,
            TypeId = typeId,
            IsDiscovered = true,
            CategoryId = -2001260, // OST_Site
            Category = "OST_Site",
            Name = "SiteType",
            FamilyName = "SiteFamily",
            References = [3],
            Parameters = [PhaseParameter()],
            BoundingBox = [0, 0, 0, 4, 3, 2],
        };

    /// <summary>PHASE_CREATED pointing at a referenced phase element - what
    /// HasProjectPhaseReference requires to treat the record as a placed instance.</summary>
    private static Parameter PhaseParameter() => new()
    {
        Id = -1012100,
        StorageType = StorageType.ElementId,
        Value = 3,
    };

    /// <summary>A closed rectangular box: the exporter emits an IfcFacetedBrep for it.</summary>
    private static GeometryData SolidPrism()
    {
        double[][] points =
        [
            [0, 0, 0], [4, 0, 0], [4, 3, 0], [0, 3, 0],
            [0, 0, 2], [4, 0, 2], [4, 3, 2], [0, 3, 2],
        ];
        int[][] faces =
        [
            [0, 3, 2, 1], [4, 5, 6, 7],
            [0, 1, 5, 4], [1, 2, 6, 5],
            [2, 3, 7, 6], [3, 0, 4, 7],
        ];
        return new GeometryData
        {
            BoundingBox = [0, 0, 0, 4, 3, 2],
            Faces = faces.Select(indices => new GeoFace
            {
                OuterVertices = indices.Select(index => points[index]).ToList(),
            }).ToList(),
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
        };
    }

    /// <summary>Four authored faces, three vertices each - so every input-side geometry test
    /// passes - whose vertices are collinear, so the writer emits no face entity and
    /// therefore no shape representation at all.</summary>
    private static GeometryData CollinearFacesWithoutRepresentation() => new()
    {
        BoundingBox = [0, 0, 0, 4, 3, 2],
        Faces = Enumerable.Range(0, 4).Select(index => new GeoFace
        {
            OuterVertices =
            [
                [0, 0, index],
                [1, 0, index],
                [2, 0, index],
            ],
        }).ToList(),
    };

    [Theory]
    [InlineData(IfcProfile.Current)]
    [InlineData(IfcProfile.Legacy)]
    public void WallWithUnresolvedSymbolAndNoFaces_GetsNoSolid_IsNoGeometry_AndIsSuppressed(IfcProfile profile)
    {
        var wall = new Element
        {
            Id = 350,
            TypeId = 351,
            CategoryId = -2000011,
            Category = "OST_Walls",
            Name = "VoidInPlaceWall",
            References = [3],
            Parameters =
            [
                PhaseParameter(),
                new Parameter { Id = -1002050, StorageType = StorageType.ElementId, Value = 351 },
                new Parameter { Id = -1001000, Definition = new Definition { Name = "WALL_ATTR_WIDTH_PARAM" }, StorageType = StorageType.Double, Value = 1.0 },
            ],
            BoundingBox = [0, 0, 0, 10, 4, 8],
            Geometry = new GeometryData
            {
                BoundingBox = [0, 0, 0, 10, 4, 8],
                UnresolvedSymbol = true,
                Instances =
                [
                    new GeoInstance
                    {
                        SymbolId = 351,
                        Transform = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0],
                    },
                ],
            },
        };
        var symbol = new Element
        {
            Id = 351,
            CategoryId = -2000011,
            Category = "OST_Walls",
            Name = "VoidInPlaceWall",
            BoundingBox = [0, 0, 0, 10, 4, 8],
        };
        var info = new RevitFileInfo { Elements = [wall, symbol] };
        ElementBuilder.ComputeWallSolids(info.Elements);

        Assert.Null(wall.Geometry.Solids);

        var mesh = Assert.Single(ElementMeshes.Extract(RvtDocument.FromInfo(info)).Elements);
        Assert.Equal(wall.Id, mesh.ElementId);
        Assert.Equal(MeshCoverage.NoGeometry, mesh.Coverage);

        string path = TempIfcPath($"wall-unresolved-{profile}");
        try
        {
            var diagnostics = new StringWriter();
            var skipLog = new SkipLog();
            new IfcExporter(diagnostics) { Profile = profile, SkipLog = skipLog }.Export(info, path);
            string ifc = File.ReadAllText(path);

            Assert.DoesNotContain("=IFCWALL(", ifc);
            Assert.DoesNotContain("=IFCWALLSTANDARDCASE(", ifc);
            Assert.DoesNotContain("=IFCEXTRUDEDAREASOLID(", ifc);
            Assert.DoesNotContain("Pset_QuantityTakeOff", ifc);
            Assert.Empty(Regex.Matches(ifc, @"#\d+=IFCRELCONTAINEDINSPATIALSTRUCTURE\("));
            Assert.Contains(skipLog.Groups(), group => group.Reason == "geometryless-product" && group.Examples.Contains(wall.Id));
            Assert.Contains("IFC geometry-less element products suppressed: 1 - IFCWALL=1", diagnostics.ToString());
        }
        finally { Cleanup(path); }
    }

    private static string TempIfcPath(string tag) =>
        Path.Combine(Path.GetTempPath(), $"revitreader-{tag}-{Guid.NewGuid():N}.ifc");

    private static void Cleanup(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
