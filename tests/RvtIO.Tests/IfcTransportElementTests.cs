using System.Text.RegularExpressions;
using RvtIO.Geometry;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

public class IfcTransportElementTests
{
    private const int PhaseId = 7;
    private const int VerticalCirculationCategoryId = -2001052;

    private static GeometryData BoxGeo()
    {
        double[][] pts = [[0, 0, 0], [4, 0, 0], [4, 3, 0], [0, 3, 0], [0, 0, 8], [4, 0, 8], [4, 3, 8], [0, 3, 8]];
        int[][] faces = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
        return new GeometryData
        {
            BoundingBox = [0, 0, 0, 4, 3, 8],
            Faces = faces.Select(idx => new GeoFace { OuterVertices = idx.Select(k => pts[k]).ToList() }).ToList(),
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
        };
    }

    private static RevitFileInfo Document(params Element[] extra)
    {
        var info = new RevitFileInfo { SerializedProjectPhaseIds = [PhaseId] };
        info.Elements.Add(new Element { Id = PhaseId, CategoryId = -2000112, Name = "New Construction", NativeRootClassName = "ProjectPhase" });
        info.Elements.Add(new Element
        {
            Id = 10, Name = "Level 1", IsNativeLevel = true,
            SerializedFamilyId = -1, SerializedUnplacedOwnerId = -1, SerializedOwnerDbViewId = -1,
            Parameters =
            [
                new Parameter { Id = -1007102, StorageType = StorageType.Double, Value = 0.0 },
                new Parameter { Id = -1007111, StorageType = StorageType.Integer, Value = 1 },
            ],
        });
        info.Elements.AddRange(extra);
        return info;
    }

    private static Element TypeSymbol(int id, string name, string familyName, Guid? exportId = null) => new()
    {
        Id = id,
        TypeId = -1,
        CategoryId = VerticalCirculationCategoryId,
        Category = "OST_VerticalCirculation",
        Name = name,
        FamilyName = familyName,
        SerializedExportId = exportId,
    };

    private static Element TransportInstance(int id, int typeId, string name, string familyName, Guid? exportId = null) => new()
    {
        Id = id,
        TypeId = typeId,
        CategoryId = VerticalCirculationCategoryId,
        Category = "OST_VerticalCirculation",
        Name = name,
        FamilyName = familyName,
        SerializedExportId = exportId,
        SerializedCreatedPhaseId = PhaseId,
        SerializedDemolishedPhaseId = -1,
        References = [PhaseId],
        Parameters =
        [
            new Parameter { Id = -1002050, Value = typeId, StorageType = StorageType.ElementId, Definition = new Definition { Id = -1002050, Name = "ELEM_TYPE_PARAM" } },
            new Parameter { Id = -1012100, Value = PhaseId, StorageType = StorageType.ElementId },
        ],
        BoundingBox = [0, 0, 0, 4, 3, 8],
        Geometry = BoxGeo(),
    };

    private static string Export(RevitFileInfo info, IfcExporter? exporter = null)
    {
        string path = Path.Combine(Path.GetTempPath(), $"rvtio-transport-{Guid.NewGuid():N}.ifc");
        try
        {
            (exporter ?? new IfcExporter { Profile = IfcProfile.Current }).Export(info, path);
            return File.ReadAllText(path).Replace("\r\n", "\n");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void VerticalCirculation_ExportsAsIfcTransportElementAndTypeWithCommonPsetInCurrent()
    {
        var typeGuid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var inst1Guid = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var inst2Guid = Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff");
        var doc = Document(
            TypeSymbol(200, "Passenger Lift", "Lift Family", typeGuid),
            TransportInstance(101, 200, "Passenger Lift", "Lift Family", inst1Guid),
            TransportInstance(102, 200, "Passenger Lift", "Lift Family", inst2Guid));

        string ifc = Export(doc);

        Assert.DoesNotContain("=IFCBUILDINGELEMENTPROXY(", ifc);
        Assert.DoesNotContain("=IFCBUILDINGELEMENTPROXYTYPE(", ifc);
        Assert.DoesNotContain("Pset_BuildingElementProxyCommon", ifc);

        var elements = Regex.Matches(ifc, @"^#(\d+)=IFCTRANSPORTELEMENT\(([^\r\n]*)\);$", RegexOptions.Multiline);
        Assert.Equal(2, elements.Count);
        foreach (Match match in elements)
            Assert.Matches(@",'10[12]',\$,\$,\$$", match.Groups[2].Value);

        Assert.Contains($"'{IfcGuid.ToIfcGuid(inst1Guid)}'", elements[0].Groups[2].Value);
        Assert.Contains($"'{IfcGuid.ToIfcGuid(inst2Guid)}'", elements[1].Groups[2].Value);

        var typeMatch = Assert.Single(Regex.Matches(ifc, @"^#(\d+)=IFCTRANSPORTELEMENTTYPE\(([^\r\n]*)\);$", RegexOptions.Multiline));
        string typeEntityId = typeMatch.Groups[1].Value;
        string[] typeArgs = IfcExporter.SplitStepArguments(typeMatch.Groups[2].Value);
        Assert.Equal("'200'", typeArgs[7]);
        Assert.Equal("$", typeArgs[8]);
        Assert.Equal(".NOTDEFINED.", typeArgs[9]);

        Assert.Single(Regex.Matches(ifc, $@"^#\d+=IFCRELDEFINESBYTYPE\([^\r\n]*,\(#{elements[0].Groups[1].Value},#{elements[1].Groups[1].Value}\),#{typeEntityId}\);$", RegexOptions.Multiline));

        // Type carries QTO then Pset_TransportElementCommon; both occurrences carry both sets too.
        var psetNames = Regex.Matches(ifc, @"^#(\d+)=IFCPROPERTYSET\([^,]+,[^,]+,'([^']+)',", RegexOptions.Multiline)
            .Cast<Match>()
            .ToDictionary(m => "#" + m.Groups[1].Value, m => m.Groups[2].Value);
        string[] typePsetIds = typeArgs[5].Trim('(', ')').Split(',');
        Assert.Equal(["Pset_QuantityTakeOff", "Pset_TransportElementCommon"], typePsetIds.Select(id => psetNames[id]));

        int occTransportPsets = Regex.Matches(ifc, $@"IFCRELDEFINESBYPROPERTIES\([^\r\n]*,\(#(?:{elements[0].Groups[1].Value}|{elements[1].Groups[1].Value})\),(#\d+)\);", RegexOptions.Multiline)
            .Cast<Match>()
            .Count(m => psetNames[m.Groups[1].Value] == "Pset_TransportElementCommon");
        Assert.Equal(2, occTransportPsets);
    }

    [Fact]
    public void VerticalCirculation_KeepsTheFireRatingTheProxyCarried()
    {
        var type = TypeSymbol(200, "Passenger Lift", "Lift Family");
        type.Parameters = [TextParameter(-1001206, "FIRE_RATING", "EI90")];

        string ifc = Export(Document(type, TransportInstance(101, 200, "Passenger Lift", "Lift Family")));

        string property = Assert.Single(Regex.Matches(ifc, @"^(#\d+)=IFCPROPERTYSINGLEVALUE\('FireRating',[^,]*,IFCLABEL\('EI90'\),\$\);$", RegexOptions.Multiline)).Groups[1].Value;
        var concrete = Regex.Matches(ifc, $@"^(#\d+)=IFCPROPERTYSET\([^,]+,[^,]+,'Pset_ConcreteElementGeneral',[^,]*,\({property}\)\);$", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value).ToList();
        Assert.Equal(2, concrete.Count);
        // one for the occurrence, one for the type
        string occurrence = Assert.Single(Regex.Matches(ifc, @"^(#\d+)=IFCTRANSPORTELEMENT\(", RegexOptions.Multiline)).Groups[1].Value;
        Assert.Single(concrete, id => Regex.IsMatch(ifc, $@"IFCRELDEFINESBYPROPERTIES\([^\r\n]*,\({occurrence}\),{id}\);"));
        var typeArgs = IfcExporter.SplitStepArguments(Assert.Single(Regex.Matches(ifc, @"=IFCTRANSPORTELEMENTTYPE\(([^\r\n]*)\);")).Groups[1].Value);
        Assert.Single(concrete, id => typeArgs[5].Trim('(', ')').Split(',').Contains(id));
    }

    [Fact]
    public void VerticalCirculation_KeepsTheExtrudedBodyAndThePortsTheProxyHad()
    {
        var lift = TransportInstance(101, 200, "Passenger Lift", "Lift Family");
        lift.SerializedConnectorGraph = [(0, 1, [])];
        lift.SerializedMepConnectors = [(0, 1, 16)];

        string ifc = Export(Document(TypeSymbol(200, "Passenger Lift", "Lift Family"), lift));

        Assert.Contains("=IFCEXTRUDEDAREASOLID(", ifc);
        string occurrence = Assert.Single(Regex.Matches(ifc, @"^(#\d+)=IFCTRANSPORTELEMENT\(", RegexOptions.Multiline)).Groups[1].Value;
        string port = Assert.Single(Regex.Matches(ifc, @"^(#\d+)=IFCDISTRIBUTIONPORT\(", RegexOptions.Multiline)).Groups[1].Value;
        Assert.Matches($@"IFCRELCONNECTSPORTTOELEMENT\([^\r\n]*,{port},{occurrence}\);", ifc);
    }

    [Fact]
    public void VerticalCirculation_AnOpenSurfaceHostedCutterIsSuppressedAsAProxyIs()
    {
        var openFourFaceShell = new IfcExporter.BodyRepresentationWriteOutcome(true, false, 1, 4);

        Assert.True(IfcExporter.ShouldSuppressOpenSurfaceHostedCutterProxyProduct("IFCTRANSPORTELEMENT", openFourFaceShell, true));
    }

    [Theory]
    [InlineData(null, "Passenger Lift")]
    [InlineData("LIFT-A", "LIFT-A")]
    public void VerticalCirculation_ReferenceIsTheExplicitParameterOrTheTypeName(string? explicitReference, string expected)
    {
        var type = TypeSymbol(200, "Passenger Lift", "Lift Family");
        if (explicitReference != null) type.Parameters = [TextParameter(500, "Reference", explicitReference)];

        string ifc = Export(Document(type, TransportInstance(101, 200, "Passenger Lift", "Lift Family")));

        var common = Regex.Matches(ifc, @"=IFCPROPERTYSET\([^,]+,[^,]+,'Pset_TransportElementCommon',[^,]*,\((#\d+)\)\);");
        Assert.NotEmpty(common);
        Assert.All(common.Cast<Match>(), set => Assert.Contains(
            $"\n{set.Groups[1].Value}=IFCPROPERTYSINGLEVALUE('Reference','',IFCIDENTIFIER('{expected}'),$);", ifc));
    }

    private static Parameter TextParameter(int id, string name, string value) =>
        new() { Id = id, StorageType = StorageType.String, Value = value, Definition = new Definition { Id = id, Name = name } };

    [Fact]
    public void VerticalCirculation_LegacyProfileStaysProxy()
    {
        var doc = Document(
            TypeSymbol(200, "Passenger Lift", "Lift Family"),
            TransportInstance(101, 200, "Passenger Lift", "Lift Family"));

        string ifc = Export(doc, new IfcExporter { Profile = IfcProfile.Legacy });

        Assert.DoesNotContain("=IFCTRANSPORTELEMENT(", ifc);
        Assert.Contains("=IFCBUILDINGELEMENTPROXY(", ifc);
    }

    [Fact]
    public void VerticalCirculation_Ifc4UsesNineArgumentTransportElementWithPredefinedType()
    {
        var doc = Document(
            TypeSymbol(200, "Escalator Type", "Escalator Family"),
            TransportInstance(101, 200, "Escalator Type", "Escalator Family"));

        string ifc = Export(doc, new IfcExporter(IfcExportConfiguration.FromPreset(IfcExportPresetKind.Ifc4ReferenceView12), null));

        var elemMatch = Assert.Single(Regex.Matches(ifc, @"^#\d+=IFCTRANSPORTELEMENT\(([^\r\n]*)\);$", RegexOptions.Multiline));
        string[] elemArgs = IfcExporter.SplitStepArguments(elemMatch.Groups[1].Value);
        Assert.Equal(9, elemArgs.Length);
        Assert.Equal("'101'", elemArgs[7]);
        Assert.Equal(".NOTDEFINED.", elemArgs[8]);

        var typeMatch = Assert.Single(Regex.Matches(ifc, @"^#\d+=IFCTRANSPORTELEMENTTYPE\(([^\r\n]*)\);$", RegexOptions.Multiline));
        string[] typeArgs = IfcExporter.SplitStepArguments(typeMatch.Groups[1].Value);
        Assert.Equal(10, typeArgs.Length);
        Assert.Equal(".NOTDEFINED.", typeArgs[9]);
    }
}
