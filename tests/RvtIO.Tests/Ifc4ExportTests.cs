using System.Globalization;
using System.Text.RegularExpressions;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

public class Ifc4ExportTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Regex IfcRootGuidRegex = new(@"^#[0-9]+=IFC[A-Z0-9]+\('([0-3][0-9A-Za-z_$]{21})',", RegexOptions.Compiled);

    private static RevitFileInfo SyntheticArchAndMepDocument()
    {
        const int phaseId = 7;
        double[][] pts = [[0, 0, 0], [4, 0, 0], [4, 3, 0], [0, 3, 0], [0, 0, 1], [4, 0, 1], [4, 3, 1], [0, 3, 1]];
        int[][] faces = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
        GeometryData BoxGeo() => new()
        {
            BoundingBox = [0, 0, 0, 4, 3, 1],
            Faces = faces.Select(idx => new GeoFace { OuterVertices = idx.Select(k => pts[k]).ToList() }).ToList(),
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
        };

        return new RevitFileInfo
        {
            SerializedLengthUnit = "autodesk.unit.unit:millimeters-1.0.1",
            SerializedProjectPhaseIds = [phaseId],
            ProjectInfo = new ProjectInfo { Name = "Synthetic IFC4 Model", Number = "004" },
            Elements =
            [
                new Element { Id = phaseId, CategoryId = -2000112, Name = "New Construction", NativeRootClassName = "ProjectPhase" },
                new Element
                {
                    Id = 10, Name = "Level 1", IsNativeLevel = true,
                    SerializedFamilyId = -1, SerializedUnplacedOwnerId = -1, SerializedOwnerDbViewId = -1,
                    Parameters =
                    [
                        new Parameter { Id = -1007102, Definition = new Definition { Name = "LEVEL_ELEV" }, StorageType = StorageType.Double, Value = 0.0 },
                        new Parameter { Id = -1007111, StorageType = StorageType.Integer, Value = 1 },
                    ],
                },
                new Element
                {
                    Id = 11, CategoryId = -2000160, Category = "OST_Rooms", Name = "Room 101", SerializedRoomPhaseId = phaseId,
                    Parameters =
                    [
                        new Parameter { Id = -1006916, StorageType = StorageType.ElementId, Value = 10 },
                        new Parameter { Id = -1006901, StorageType = StorageType.String, Value = "101" },
                    ],
                },
                new Element { Id = 20, TypeId = -1, CategoryId = -2000032, Name = "Slab Type" },
                new Element
                {
                    Id = 21, CategoryId = -2000032, Category = "OST_Floors", Name = "Slab 1", TypeId = 20,
                    References = [phaseId], SerializedCreatedPhaseId = phaseId, SerializedDemolishedPhaseId = -1,
                    Parameters =
                    [
                        new Parameter { Id = -1002050, Value = 20, StorageType = StorageType.ElementId, Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" } },
                        new Parameter { Id = -1012100, Value = phaseId, StorageType = StorageType.ElementId },
                    ],
                    BoundingBox = [0, 0, 0, 4, 3, 1],
                    Geometry = BoxGeo(),
                },
                new Element { Id = 30, TypeId = -1, CategoryId = -2001040, Name = "Main Distribution Panel" },
                new Element
                {
                    Id = 31, CategoryId = -2001040, Category = "OST_ElectricalEquipment", Name = "Panel 1", TypeId = 30,
                    References = [phaseId], SerializedCreatedPhaseId = phaseId, SerializedDemolishedPhaseId = -1,
                    Parameters =
                    [
                        new Parameter { Id = -1002050, Value = 30, StorageType = StorageType.ElementId, Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" } },
                        new Parameter { Id = -1012100, Value = phaseId, StorageType = StorageType.ElementId },
                    ],
                    Transform = new InstanceTransform { Origin = [1, 1, 0], BasisX = [1, 0, 0], BasisY = [0, 1, 0], BasisZ = [0, 0, 1] },
                    SerializedConnectorGraph = [(0, 2, [])],
                    BoundingBox = [0, 0, 0, 1, 1, 2],
                    Geometry = BoxGeo(),
                },
                new Element { Id = 40, TypeId = -1, CategoryId = -2001060, Name = "Duplex Receptacle" },
                new Element
                {
                    Id = 41, CategoryId = -2001060, Category = "OST_ElectricalFixtures", Name = "Outlet 1", TypeId = 40,
                    References = [phaseId], SerializedCreatedPhaseId = phaseId, SerializedDemolishedPhaseId = -1,
                    Parameters =
                    [
                        new Parameter { Id = -1002050, Value = 40, StorageType = StorageType.ElementId, Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" } },
                        new Parameter { Id = -1012100, Value = phaseId, StorageType = StorageType.ElementId },
                    ],
                    Transform = new InstanceTransform { Origin = [2, 1, 1], BasisX = [1, 0, 0], BasisY = [0, 1, 0], BasisZ = [0, 0, 1] },
                    BoundingBox = [0, 0, 0, 0.5, 0.5, 0.5],
                    Geometry = BoxGeo(),
                },
                new Element { Id = 50, TypeId = -1, CategoryId = -2000151, Name = "Proxy Type" },
                new Element
                {
                    Id = 51, CategoryId = -2000151, Category = "OST_GenericModel", Name = "Proxy With Connector", TypeId = 50,
                    References = [phaseId], SerializedCreatedPhaseId = phaseId, SerializedDemolishedPhaseId = -1,
                    Parameters =
                    [
                        new Parameter { Id = -1002050, Value = 50, StorageType = StorageType.ElementId, Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" } },
                        new Parameter { Id = -1012100, Value = phaseId, StorageType = StorageType.ElementId },
                    ],
                    Transform = new InstanceTransform { Origin = [3, 1, 0], BasisX = [1, 0, 0], BasisY = [0, 1, 0], BasisZ = [0, 0, 1] },
                    SerializedConnectorGraph = [(0, 1, [])],
                    SerializedMepConnectors = [(0, 1, 16)],
                    BoundingBox = [0, 0, 0, 1, 1, 1],
                    Geometry = BoxGeo(),
                },
            ],
        };
    }

    private static string ExportText(RevitFileInfo info, IfcExporter exporter)
    {
        using var sw = new StringWriter();
        exporter.Export(info, sw);
        return sw.ToString();
    }

    [Fact]
    public void FixedTimestampIfc2x3Regression_DefaultMatchesExplicitIfc2x3ByteForByte()
    {
        var doc = SyntheticArchAndMepDocument();
        string defaultOut = ExportText(doc, new IfcExporter { Timestamp = FixedTime });
        string explicit2x3Current = ExportText(doc, new IfcExporter(IfcExportConfiguration.FromPreset(IfcExportPresetKind.Ifc2x3CoordinationView20)) { Timestamp = FixedTime });
        Assert.Equal(defaultOut, explicit2x3Current);
        Assert.Contains("FILE_SCHEMA(('IFC2X3'));", defaultOut);
        Assert.Contains("ViewDefinition [CoordinationView_V2.0, QuantityTakeOffAddOnView]", defaultOut);

        string legacyDefault = ExportText(doc, new IfcExporter { Profile = IfcProfile.Legacy, Timestamp = FixedTime });
        string legacyPreset = ExportText(doc, new IfcExporter(IfcExportConfiguration.FromPreset(IfcExportPresetKind.Ifc2x3CoordinationViewLegacy)) { Timestamp = FixedTime });
        Assert.Equal(legacyDefault, legacyPreset);
        Assert.Contains("FILE_SCHEMA(('IFC2X3'));", legacyDefault);
        Assert.Contains("ViewDefinition [CoordinationView]", legacyDefault);
    }

    [Theory]
    [InlineData(IfcProfile.Current, IfcModelView.ReferenceView12, "ViewDefinition [ReferenceView_V1.2]")]
    [InlineData(IfcProfile.Legacy, IfcModelView.ReferenceView12, "ViewDefinition [ReferenceView_V1.2]")]
    [InlineData(IfcProfile.Current, IfcModelView.DesignTransferView10, "ViewDefinition [DesignTransferView_V1.0]")]
    [InlineData(IfcProfile.Legacy, IfcModelView.DesignTransferView10, "ViewDefinition [DesignTransferView_V1.0]")]
    public void SchemaAndMvdSelection_IsOrthogonalToProfile(IfcProfile profile, IfcModelView view, string expectedDesc)
    {
        var doc = SyntheticArchAndMepDocument();
        var diag = new StringWriter();
        string step = ExportText(doc, new IfcExporter(diag)
        {
            Schema = IfcSchema.Ifc4,
            ModelView = view,
            Profile = profile,
            Timestamp = FixedTime,
        });

        Assert.Contains("FILE_SCHEMA(('IFC4'));", step);
        Assert.Contains(expectedDesc, step);
        Assert.DoesNotContain("IFCELECTRICDISTRIBUTIONPOINT(", step);
        Assert.Contains("IFCELECTRICDISTRIBUTIONBOARD(", step);
        Assert.Contains("IFCELECTRICDISTRIBUTIONBOARDTYPE(", step);
        Assert.Contains("IFCOUTLET(", step);
        Assert.Contains("IFCOUTLETTYPE(", step);

        // Proxy elements are not IfcDistributionElement subtypes in IFC4 and must skip port binding with diagnostics.
        Assert.Contains("IFCBUILDINGELEMENTPROXY(", step);
        Assert.Contains("IfcDistributionElement", diag.ToString());

        // Legacy skips Qto sets while Current emits IFC4 Qto_*BaseQuantities.
        if (profile == IfcProfile.Current)
        {
            Assert.Contains("Qto_SlabBaseQuantities", step);
            Assert.DoesNotContain("'BaseQuantities'", step);
        }
        else
        {
            Assert.DoesNotContain("Qto_SlabBaseQuantities", step);
        }

        // Every IfcRoot GlobalId in IFC4 output must be unique.
        var rootGuids = step.Split('\n')
            .Select(line => IfcRootGuidRegex.Match(line))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToList();
        Assert.NotEmpty(rootGuids);
        Assert.Equal(rootGuids.Count, rootGuids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Ifc4ReferenceViewVsDesignTransferView_EnforcesDistinctMvdGeometryAndWallRules()
    {
        var w = new StepWriter();
        int person = w.Add("IFCPERSON", "$,$,$,$,$,$,$,$");
        int org = w.Add("IFCORGANIZATION", "$,'RvtIO',$,$,$");
        int po = w.Add("IFCPERSONANDORGANIZATION", $"#{person},#{org},$");
        int app = w.Add("IFCAPPLICATION", $"#{org},'1.0','RvtIO','RvtIO'");
        int oh = w.Add("IFCOWNERHISTORY", $"#{po},#{app},$,.ADDED.,$,$,$,1");

        int p0 = w.Add("IFCCARTESIANPOINT", "(0.,0.,0.)");
        int dirX = w.Add("IFCDIRECTION", "(1.,0.,0.)");
        int dirZ = w.Add("IFCDIRECTION", "(0.,0.,1.)");
        int axBad = w.Add("IFCAXIS2PLACEMENT3D", $"#{p0},$,#{dirX}");
        int ctx = w.Add("IFCGEOMETRICREPRESENTATIONCONTEXT", $"$,'Model',3,1.E-5,#{axBad},$");
        int subCtx = w.Add("IFCGEOMETRICREPRESENTATIONSUBCONTEXT", $"$,'Body',*,*,*,*,#{ctx},0.,.MODEL_VIEW.,$");

        // Mesh/BRep box + voided face + surface model
        int pt1 = w.Add("IFCCARTESIANPOINT", "(1.,0.,0.)");
        int pt2 = w.Add("IFCCARTESIANPOINT", "(1.,1.,0.)");
        int pt3 = w.Add("IFCCARTESIANPOINT", "(0.,1.,0.)");
        int pt4 = w.Add("IFCCARTESIANPOINT", "(0.2,0.2,0.)");
        int pt5 = w.Add("IFCCARTESIANPOINT", "(0.8,0.2,0.)");
        int pt6 = w.Add("IFCCARTESIANPOINT", "(0.5,0.8,0.)");
        int outerLoop = w.Add("IFCPOLYLOOP", $"(#{p0},#{pt1},#{pt2},#{pt3})");
        int innerLoop = w.Add("IFCPOLYLOOP", $"(#{pt4},#{pt5},#{pt6})");
        int outerBound = w.Add("IFCFACEOUTERBOUND", $"#{outerLoop},.T.");
        int innerBound = w.Add("IFCFACEBOUND", $"#{innerLoop},.F.");
        int polyFace = w.Add("IFCFACE", $"(#{outerBound},#{innerBound})");
        int triLoop = w.Add("IFCPOLYLOOP", $"(#{p0},#{pt1},#{pt2})");
        int triBound = w.Add("IFCFACEOUTERBOUND", $"#{triLoop},.T.");
        int triFace = w.Add("IFCFACE", $"(#{triBound})");

        int closedShell = w.Add("IFCCLOSEDSHELL", $"(#{polyFace})");
        int brep = w.Add("IFCFACETEDBREP", $"#{closedShell}");
        int openShell = w.Add("IFCOPENSHELL", $"(#{triFace})");
        int surfModel = w.Add("IFCFACEBASEDSURFACEMODEL", $"(#{openShell})");

        int surfStyle = w.Add("IFCSURFACESTYLE", "'WallMat',.BOTH.,()");
        int styleAssign = w.Add("IFCPRESENTATIONSTYLEASSIGNMENT", $"(#{surfStyle})");
        w.Add("IFCSTYLEDITEM", $"#{brep},(#{styleAssign}),$");

        int repBrep = w.Add("IFCSHAPEREPRESENTATION", $"#{subCtx},'Body','Brep',(#{brep})");
        int repSurf = w.Add("IFCSHAPEREPRESENTATION", $"#{subCtx},'Body','SurfaceModel',(#{surfModel})");
        int wallShape = w.Add("IFCPRODUCTDEFINITIONSHAPE", $"$,$,(#{repBrep},#{repSurf})");
        w.Add("IFCPRODUCTDEFINITIONSHAPE", $"$,$,(#{repBrep})"); // Intentional orphan to verify pruning.

        int lp = w.Add("IFCLOCALPLACEMENT", $"$,#{axBad}");
        int wallStdWithUsage = w.Add("IFCWALLSTANDARDCASE", $"'0001000100010001000101',#{oh},'WallStd',$,$,#{lp},#{wallShape},'W1'");
        int wallStdNoUsage = w.Add("IFCWALLSTANDARDCASE", $"'0001000100010001000102',#{oh},'WallNoUsage',$,$,#{lp},$,'W2'");
        int mat = w.Add("IFCMATERIAL", "'Concrete'");
        int layer = w.Add("IFCMATERIALLAYER", $"#{mat},0.2,.F.");
        int layerSet = w.Add("IFCMATERIALLAYERSET", $"(#{layer}),'Conc200'");
        int layerUsage = w.Add("IFCMATERIALLAYERSETUSAGE", $"#{layerSet},.AXIS2.,.POSITIVE.,-0.1");
        w.Add("IFCRELASSOCIATESMATERIAL", $"'0001000100010001000103',#{oh},$,$,(#{wallStdWithUsage}),#{layerUsage}");

        // Legacy entities needing IFC4 upgrades and space/material compliance checks
        int space = w.Add("IFCSPACE", $"'0001000100010001000104',#{oh},'Sp1',$,$,#{lp},$,'Room',.ELEMENT.,.INTERNAL.,0.");
        int storey = w.Add("IFCBUILDINGSTOREY", $"'0001000100010001000105',#{oh},'Level 1',$,$,#{lp},$,$,.ELEMENT.,0.");
        w.Add("IFCRELCONTAINEDINSPATIALSTRUCTURE", $"'0001000100010001000106',#{oh},$,$,(#{space},#{wallStdWithUsage}),#{storey}");
        w.Add("IFCRELASSOCIATESMATERIAL", $"'0001000100010001000107',#{oh},$,$,(#{space}),#{mat}");

        w.Add("IFCDOOR", $"'0001000100010001000108',#{oh},'Door1',$,$,#{lp},$,'D1',2.1,0.9");
        w.Add("IFCWINDOW", $"'0001000100010001000109',#{oh},'Win1',$,$,#{lp},$,'W1',1.2,1.5");
        w.Add("IFCDOORSTYLE", $"'000100010001000100010A',#{oh},'DS',$,$,$,$,'10',.SINGLE_SWING_LEFT.,.NOTDEFINED.,.T.,.F.");
        w.Add("IFCWINDOWSTYLE", $"'000100010001000100010B',#{oh},'WS',$,$,$,$,'11',.NOTDEFINED.,.SINGLE_PANEL.,.T.,.F.");
        w.Add("IFCSTAIRFLIGHT", $"'000100010001000100010C',#{oh},'SF',$,$,#{lp},$,'12',10,10,0.18,0.28");
        w.Add("IFCREINFORCINGMESH", $"'000100010001000100010D',#{oh},'RM',$,$,#{lp},$,'13',$,$,2.0,1.0,0.01,0.01,0.2,0.2,'MESH'");
        w.Add("IFCZONE", $"'000100010001000100010E',#{oh},'Zone1',$,$");
        w.Add("IFCCLASSIFICATION", "'UniFormat',$,$,'UF'");
        w.Add("IFCCLASSIFICATIONREFERENCE", "$,'A10','Foundations',$");
        w.Add("IFCFURNITURETYPE", $"'000100010001000100010F',#{oh},'Desk',$,$,$,$,'14',$,$");
        w.Add("IFCDISTRIBUTIONCONTROLELEMENT", $"'0001000100010001000110',#{oh},'Sensor',$,$,#{lp},$,'15','CTRL'");
        w.Add("IFCDISTRIBUTIONPORT", $"'0001000100010001000111',#{oh},'Port1',$,$,#{lp},$,.SOURCE.");
        int pset1 = w.Add("IFCPROPERTYSET", $"'0001000100010001000112',#{oh},'Pset_BeamCommon',$,()");
        int pset2 = w.Add("IFCPROPERTYSET", $"'0001000100010001000113',#{oh},'Pset_ReinforcementBarPitchOfBeam',$,()");
        int pset3 = w.Add("IFCPROPERTYSET", $"'0001000100010001000114',#{oh},'Pset_ReinforcementBarPitchOfBeam',$,()");
        w.Add("IFCBEAMTYPE", $"'0001000100010001000115',#{oh},'BeamT',$,$,(#{pset1},#{pset2},#{pset3}),$,'16',$,.NOTDEFINED.");

        // 2D Grid axis + degenerate XY direction (0,0,1)
        int gridVec = w.Add("IFCVECTOR", $"#{dirZ},1.");
        int gridLine = w.Add("IFCLINE", $"#{p0},#{gridVec}");
        int gridTrim = w.Add("IFCTRIMMEDCURVE", $"#{gridLine},(#{p0}),(#{pt1}),.T.,.CARTESIAN.");
        int gridAxis = w.Add("IFCGRIDAXIS", $"'A',#{gridTrim},.T.");
        w.Add("IFCGRID", $"'0001000100010001000116',#{oh},'Grid',$,$,#{lp},$,'17',(#{gridAxis}),(#{gridAxis})");

        // Quantity conversion + 5-arg IfcQuantity
        int qLen = w.Add("IFCQUANTITYLENGTH", "'Length',$,$,4.0");
        int qto = w.Add("IFCELEMENTQUANTITY", $"'0001000100010001000117',#{oh},'BaseQuantities',$,$,(#{qLen})");
        w.Add("IFCRELDEFINESBYPROPERTIES", $"'0001000100010001000118',#{oh},$,$,(#{wallStdWithUsage}),#{qto}");

        var exporter = new IfcExporter();

        // Clone writer twice by re-running via Export snapshot? Build via helper delegate twice or test RV and DTV sequentially via separate writers.
        static StepWriter CloneWriter(StepWriter src)
        {
            var copy = new StepWriter();
            for (int i = 0; i < src.EntityCount; i++)
            {
                var (_, type, args) = src.EntityAt(i);
                copy.Add(type, args);
            }
            return copy;
        }

        // 1. ReferenceView_V1.2
        var rvWriter = CloneWriter(w);
        var rvConfig = IfcExportConfiguration.FromPreset(IfcExportPresetKind.Ifc4ReferenceView12).Resolve();
        exporter.ApplyIfc4SchemaAndMvdCompliance(rvWriter, rvConfig, out var rvSummary);
        using var rvSw = new StringWriter();
        rvWriter.WriteTo(rvSw, "rv.ifc", "IFC4", "ViewDefinition [ReferenceView_V1.2]", "RvtIO", "IO", FixedTime);
        string rvStep = rvSw.ToString();

        Assert.Equal(2, rvSummary.DowngradedWalls);
        Assert.Equal(1, rvSummary.SuppressedSpaceMaterials);
        Assert.Equal(1, rvSummary.PrunedOrphanShapes);
        Assert.DoesNotContain("IFCWALLSTANDARDCASE(", rvStep);
        Assert.Equal(2, Regex.Matches(rvStep, @"=IFCWALL\(").Count);
        Assert.DoesNotContain("IFCFACETEDBREP(", rvStep);
        Assert.DoesNotContain("IFCFACEBASEDSURFACEMODEL(", rvStep);
        Assert.Contains("IFCPOLYGONALFACESET(", rvStep);
        Assert.Contains("IFCINDEXEDPOLYGONALFACEWITHVOIDS(", rvStep);
        Assert.Contains("IFCTRIANGULATEDFACESET(", rvStep);
        Assert.DoesNotContain("IFCPRESENTATIONSTYLEASSIGNMENT(", rvStep);
        Assert.DoesNotContain("IFCDOORSTYLE(", rvStep);
        Assert.DoesNotContain("IFCWINDOWSTYLE(", rvStep);
        Assert.Contains("IFCDOORTYPE(", rvStep);
        Assert.Contains("IFCWINDOWTYPE(", rvStep);
        Assert.Contains("Qto_WallBaseQuantities", rvStep);
        Assert.Contains("IFCQUANTITYLENGTH('Length',$,$,4.0,$)", rvStep);
        Assert.Contains("IFCPERSON('',$,$,$,$,$,$,$)", rvStep);
        Assert.DoesNotContain(",0.,.MODEL_VIEW.", rvStep);
        Assert.DoesNotContain($"#{p0},$,#{dirX}", rvStep); // AxisAndRefDirProvision filled!
        Assert.Contains("IFCFURNITURETYPE('000100010001000100010F'", rvStep);
        Assert.Contains(",.NOTDEFINED.,.NOTDEFINED.);", rvStep);
        Assert.Contains("IFCDISTRIBUTIONCONTROLELEMENT('0001000100010001000110'", rvStep);
        Assert.Contains("IFCDISTRIBUTIONPORT('0001000100010001000111'", rvStep);
        Assert.Contains("IFCPRODUCTDEFINITIONSHAPE", rvStep);
        Assert.Single(Regex.Matches(rvStep, @"=IFCPRODUCTDEFINITIONSHAPE\(")); // Orphan pruned!
        Assert.DoesNotContain($"(#{pset1},#{pset2},#{pset3})", rvStep); // Duplicate pset name pruned!
        Assert.Contains($"(#{pset1},#{pset2})", rvStep);
        Assert.Contains("IFCRELAGGREGATES(", rvStep); // IfcSpace moved out of spatial containment!
        Assert.DoesNotContain($"IFCRELCONTAINEDINSPATIALSTRUCTURE('0001000100010001000106',#{oh},$,$,(#{space},", rvStep);
        Assert.DoesNotContain("IFCDIRECTION((0.,0.))", rvStep); // Non-zero magnitude enforced!
        Assert.Contains("IFCDIRECTION((1.,0.))", rvStep);

        // 2. DesignTransferView_V1.0
        var dtvWriter = CloneWriter(w);
        var dtvConfig = IfcExportConfiguration.FromPreset(IfcExportPresetKind.Ifc4DesignTransferView10).Resolve();
        exporter.ApplyIfc4SchemaAndMvdCompliance(dtvWriter, dtvConfig, out var dtvSummary);
        using var dtvSw = new StringWriter();
        dtvWriter.WriteTo(dtvSw, "dtv.ifc", "IFC4", "ViewDefinition [DesignTransferView_V1.0]", "RvtIO", "IO", FixedTime);
        string dtvStep = dtvSw.ToString();

        Assert.Equal(1, dtvSummary.DowngradedWalls); // Only wall without IfcMaterialLayerSetUsage downgraded!
        Assert.Contains("IFCWALLSTANDARDCASE('0001000100010001000101'", dtvStep);
        Assert.Contains("IFCWALL('0001000100010001000102'", dtvStep);
        Assert.Contains("IFCFACETEDBREP(", dtvStep);
        Assert.Contains("IFCFACEBASEDSURFACEMODEL(", dtvStep);
        Assert.DoesNotContain("IFCPOLYGONALFACESET(", dtvStep);
        Assert.DoesNotContain("IFCTRIANGULATEDFACESET(", dtvStep);
        Assert.DoesNotContain("IFCPRESENTATIONSTYLEASSIGNMENT(", dtvStep);
    }

    [Fact]
    public void Ifc4UnitsAndCoordinates_ScaleCartesianPointList3DAndQuantitiesConsistently()
    {
        var doc = SyntheticArchAndMepDocument();
        string mmIfc = ExportText(doc, new IfcExporter { Schema = IfcSchema.Ifc4, ModelView = IfcModelView.ReferenceView12, LengthUnit = IfcUnitFamily.Millimetre, Timestamp = FixedTime });
        string mIfc = ExportText(doc, new IfcExporter { Schema = IfcSchema.Ifc4, ModelView = IfcModelView.ReferenceView12, LengthUnit = IfcUnitFamily.Metre, Timestamp = FixedTime });
        string ftIfc = ExportText(doc, new IfcExporter { Schema = IfcSchema.Ifc4, ModelView = IfcModelView.ReferenceView12, LengthUnit = IfcUnitFamily.Foot, Timestamp = FixedTime });

        Assert.Contains("IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.)", mmIfc);
        Assert.Contains("IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.)", mIfc);
        Assert.Contains("IFCCONVERSIONBASEDUNIT(#", ftIfc);
        Assert.Contains("'FOOT'", ftIfc);

        static double MaxPointCoord(string step)
        {
            var m = Regex.Match(step, @"IFCCARTESIANPOINTLIST3D\(\(([^;]+)\)\);");
            Assert.True(m.Success, "Expected IFCCARTESIANPOINTLIST3D in ReferenceView_V1.2 output.");
            return Regex.Matches(m.Groups[1].Value, @"-?\d+(?:\.\d+)?(?:[Ee][+-]?\d+)?")
                .Select(x => Math.Abs(double.Parse(x.Value, CultureInfo.InvariantCulture)))
                .Max();
        }

        double maxMm = MaxPointCoord(mmIfc);
        double maxM = MaxPointCoord(mIfc);
        double maxFt = MaxPointCoord(ftIfc);

        Assert.InRange(maxMm / maxM, 999.9, 1000.1);
        Assert.InRange(maxM / maxFt, 0.3047, 0.3049);
    }

    [SkippableFact]
    public void SampleRvtFilesFromTestRoots_ExportValidIfc4ReferenceViewAndDesignTransferViewWhenPresent()
    {
        string? firstRvt = TestFiles.Roots()
            .SelectMany(root => Directory.EnumerateFiles(root, "*.rvt", SearchOption.AllDirectories))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (firstRvt is null)
            throw new SkipException("No .rvt fixture files found under RVTIO_TEST_FILES.");

        var info = new RvtFileReader(new RvtReaderOptions { R22Layout = true }).Read(firstRvt);
        string rvStep = ExportText(info, new IfcExporter(IfcExportConfiguration.FromPreset(IfcExportPresetKind.Ifc4ReferenceView12)) { Timestamp = FixedTime });
        Assert.Contains("FILE_SCHEMA(('IFC4'));", rvStep);
        Assert.Contains("ViewDefinition [ReferenceView_V1.2]", rvStep);
        Assert.DoesNotContain("IFCFACETEDBREP(", rvStep);
        Assert.DoesNotContain("IFCELECTRICDISTRIBUTIONPOINT(", rvStep);
        Assert.DoesNotContain("IFCDOORSTYLE(", rvStep);

        string dtvStep = ExportText(info, new IfcExporter(IfcExportConfiguration.FromPreset(IfcExportPresetKind.Ifc4DesignTransferView10)) { Timestamp = FixedTime });
        Assert.Contains("FILE_SCHEMA(('IFC4'));", dtvStep);
        Assert.Contains("ViewDefinition [DesignTransferView_V1.0]", dtvStep);
        Assert.DoesNotContain("IFCPOLYGONALFACESET(", dtvStep);
        Assert.DoesNotContain("IFCTRIANGULATEDFACESET(", dtvStep);
    }
}
