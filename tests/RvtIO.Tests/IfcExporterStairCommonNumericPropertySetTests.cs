using RvtIO.Ifc;
using RvtIO.Model;
using System.Text.RegularExpressions;
using Xunit;

namespace RvtIO.Tests;

public class IfcExporterStairCommonNumericPropertySetTests
{
    [Theory]
    [InlineData(513254, 3, 0.5923738699329402, 0.984251968503937,
        "IFCCOUNTMEASURE(3.)",
        "IFCPOSITIVELENGTHMEASURE(180.55555555556018)",
        "IFCPOSITIVELENGTHMEASURE(300.)")]
    [InlineData(665557, 16, 0.6151574803149606, 0.8202099737532808,
        "IFCCOUNTMEASURE(16.)",
        "IFCPOSITIVELENGTHMEASURE(187.5)",
        "IFCPOSITIVELENGTHMEASURE(249.99999999999997)")]
    [InlineData(1190472, 6, 0.5304024496937879, 0.8530183727034121,
        "IFCCOUNTMEASURE(6.)",
        "IFCPOSITIVELENGTHMEASURE(161.66666666666657)",
        "IFCPOSITIVELENGTHMEASURE(260.)")]
    [InlineData(1317181, 17, 0.679326848849776, 0.8530183727034121,
        "IFCCOUNTMEASURE(17.)",
        "IFCPOSITIVELENGTHMEASURE(207.05882352941174)",
        "IFCPOSITIVELENGTHMEASURE(260.)")]
    public void ValidatedStair_WritesSharedFalseFirstThenReferenceAndG17NumericLiterals(
        int id, int numberOfRisers, double riserHeightFeet,
        double treadDepthFeet, string countExpression,
        string riserExpression, string treadExpression)
    {
        Element stair = Stair(id, id + 100_000, "Exact stair reference");
        Confirm(stair, numberOfRisers, riserHeightFeet, treadDepthFeet);

        string ifc = ExportToText(stair, StairType(stair.SerializedStairTypeId!.Value));

        Pset qto = SolePset(ifc, "Pset_QuantityTakeOff");
        Pset common = SolePset(ifc, "Pset_StairCommon");
        Assert.Equal(5, common.ValueIds.Count);
        Assert.Equal(Assert.Single(qto.ValueIds), common.ValueIds[1]);
        Dictionary<string, PropertyValue> properties = Properties(ifc, common);
        Assert.Equal(
            new[] { "IsExternal", "Reference", "NumberOfRiser", "RiserHeight", "TreadLength" },
            common.ValueIds.Select(id => properties.Single(pair => pair.Value.Id == id).Key));
        Assert.Equal("IFCBOOLEAN(.F.)", properties["IsExternal"].Expression);
        Assert.Equal(countExpression, properties["NumberOfRiser"].Expression);
        Assert.Equal(riserExpression, properties["RiserHeight"].Expression);
        Assert.Equal(treadExpression, properties["TreadLength"].Expression);
        Assert.DoesNotContain("'NumberOfTreads'", ifc);
    }

    [Theory]
    [InlineData(IfcProfile.Current)]
    [InlineData(IfcProfile.Legacy)]
    public void ValidatedStair_WritesNumberOfRiserInBothProfiles(IfcProfile profile)
    {
        // Revit's exports write the stored riser count of a stair; the current profile no longer omits it.
        Element stair = Stair(513254, 613254, "Exact stair reference");
        Confirm(stair, 3, 0.5923738699329402, 0.984251968503937);

        string ifc = ExportToText(profile, stair, StairType(stair.SerializedStairTypeId!.Value));

        Assert.Equal("IFCCOUNTMEASURE(3.)", Properties(ifc, SolePset(ifc, "Pset_StairCommon"))["NumberOfRiser"].Expression);
    }

    [Fact]
    public void EqualValues_KeepThreePrivatePropertyEntitiesPerOccurrence()
    {
        Element first = Stair(4100, 5100, "Shared stair reference", 0);
        Element second = Stair(4101, 5100, "Shared stair reference", 10);
        Confirm(first, 3, 0.5923738699329402, 0.984251968503937);
        Confirm(second, 3, 0.5923738699329402, 0.984251968503937);

        string ifc = ExportToText(first, second, StairType(5100));

        List<Pset> commons = Psets(ifc, "Pset_StairCommon");
        Assert.Equal(2, commons.Count);
        Assert.Equal(5, commons[0].ValueIds.Count);
        Assert.Equal(5, commons[1].ValueIds.Count);
        Assert.Equal(commons[0].ValueIds[0], commons[1].ValueIds[0]);
        Assert.Equal(commons[0].ValueIds[1], commons[1].ValueIds[1]);
        Assert.Empty(commons[0].ValueIds.Skip(2).Intersect(commons[1].ValueIds.Skip(2)));
        Assert.Single(Regex.Matches(ifc,
            @"IFCPROPERTYSINGLEVALUE\('IsExternal',(?:\$|''),IFCBOOLEAN\(\.F\.\),\$\)"));
        Assert.Equal(2, Regex.Matches(ifc,
            @"IFCPROPERTYSINGLEVALUE\('NumberOfRiser',(?:\$|''),IFCCOUNTMEASURE\(3\.\),\$\)").Count);
        Assert.Equal(2, Regex.Matches(ifc,
            @"IFCPROPERTYSINGLEVALUE\('RiserHeight',(?:\$|''),IFCPOSITIVELENGTHMEASURE\(180\.55555555556018\),\$\)").Count);
        Assert.Equal(2, Regex.Matches(ifc,
            @"IFCPROPERTYSINGLEVALUE\('TreadLength',(?:\$|''),IFCPOSITIVELENGTHMEASURE\(300\.\),\$\)").Count);
    }

    [Theory]
    [InlineData("NumberOfRiser", "occurrence")]
    [InlineData("NumberOfRiser", "type")]
    [InlineData("NumberOfRiser[Type]", "type")]
    [InlineData("RiserHeight", "occurrence")]
    [InlineData("RiserHeight", "type")]
    [InlineData("RiserHeight[Type]", "type")]
    [InlineData("TreadLength", "occurrence")]
    [InlineData("TreadLength", "type")]
    [InlineData("TreadLength[Type]", "type")]
    [InlineData("Pset_StairCommon.NumberOfRiser", "occurrence")]
    [InlineData("Pset_StairCommon.RiserHeight[Type]", "type")]
    public void ExactNamedOverrideCandidate_BlocksOnlyItsProperty(
        string propertyName, string scope)
    {
        Element stair = Stair(4200, 5200, "Override blocker");
        Element type = StairType(5200);
        Confirm(stair, 7, 0.5, 1.0);
        Element target = scope == "occurrence" ? stair : type;
        target.Parameters.Add(Param(7000, propertyName, StorageType.Double, 12.5));

        string ifc = ExportToText(stair, type);

        Dictionary<string, PropertyValue> properties = Properties(
            ifc, SolePset(ifc, "Pset_StairCommon"));
        string blocked = propertyName.Replace("[Type]", string.Empty).Replace("Pset_StairCommon.", string.Empty);
        Assert.DoesNotContain(blocked, properties.Keys);
        Assert.Contains("Reference", properties.Keys);
        foreach (string name in new[] { "NumberOfRiser", "RiserHeight", "TreadLength" })
            if (name != blocked) Assert.Contains(name, properties.Keys);
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("approximate")]
    [InlineData("conflicting")]
    public void UnreproducibleNamedOverrideCandidate_StillBlocksRawValue(
        string mismatch)
    {
        Element stair = Stair(4250, 5250, "Override provenance blocker");
        Element type = StairType(5250);
        Confirm(stair, 7, 0.5, 1.0);
        Parameter candidate = Param(
            7100, "Riser Height",
            mismatch == "unsupported" ? StorageType.String : StorageType.Double,
            mismatch == "unsupported" ? "not numeric" : 12.5);
        candidate.IsApproximate = mismatch == "approximate";
        type.Parameters.Add(candidate);
        if (mismatch == "conflicting")
            type.Parameters.Add(Param(
                7101, "riserheight", StorageType.Double, 13.5));

        string ifc = ExportToText(stair, type);

        Dictionary<string, PropertyValue> properties = Properties(
            ifc, SolePset(ifc, "Pset_StairCommon"));
        Assert.DoesNotContain("RiserHeight", properties.Keys);
        Assert.Contains("NumberOfRiser", properties.Keys);
        Assert.Contains("TreadLength", properties.Keys);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("missing_target")]
    [InlineData("wrong_class")]
    public void UnresolvedSerializedTypeLink_KeepsReferenceOnly(string mismatch)
    {
        Element stair = Stair(4300, 5300, "Blocked type link");
        Confirm(stair, 7, 0.5, 1.0);
        stair.SerializedStairTypeLinkIsResolved = mismatch != "missing";
        Element type = StairType(5300);
        if (mismatch == "wrong_class")
            type.SerializedStairsTypeClassId = 3718;

        string ifc = mismatch == "missing_target"
            ? ExportToText(stair)
            : ExportToText(stair, type);

        Dictionary<string, PropertyValue> properties = Properties(
            ifc, SolePset(ifc, "Pset_StairCommon"));
        Assert.Equal(new[] { "Reference" }, properties.Keys);
    }

    [Theory]
    [InlineData(0, 1, "IFCBOOLEAN(.F.)")]
    [InlineData(1, 0, "IFCBOOLEAN(.T.)")]
    public void SetQualifiedIsExternal_WinsOverThePropertyName(int qualified, int plain, string expression)
    {
        Element stair = Stair(4330, 5330, "Qualified external stair");
        Element type = StairType(5330);
        Confirm(stair, 7, 0.5, 1.0);
        stair.Parameters.Add(Param(7310, "IsExternal", StorageType.Integer, plain));
        stair.Parameters.Add(Param(7311, "Pset_StairCommon.IsExternal", StorageType.Integer, qualified));

        string ifc = ExportToText(stair, type);

        Assert.Equal(expression, Properties(ifc, SolePset(ifc, "Pset_StairCommon"))["IsExternal"].Expression);
    }

    [Theory]
    [InlineData("occurrence", 1, "IFCBOOLEAN(.T.)")]
    [InlineData("occurrence", 0, "IFCBOOLEAN(.F.)")]
    [InlineData("type", 1, "IFCBOOLEAN(.T.)")]
    [InlineData("type[Type]", 0, "IFCBOOLEAN(.F.)")]
    public void ExactNamedIsExternal_PrecedesTypeFunction(
        string scope, int value, string expression)
    {
        Element stair = Stair(4320, 5320, "Explicit external stair");
        Element type = StairType(5320);
        Confirm(stair, 7, 0.5, 1.0);
        Element target = scope == "occurrence" ? stair : type;
        string name = scope == "type[Type]" ? "IsExternal[Type]" : "IsExternal";
        target.Parameters.Add(Param(7300, name, StorageType.Integer, value));

        string ifc = ExportToText(stair, type);
        Dictionary<string, PropertyValue> properties = Properties(
            ifc, SolePset(ifc, "Pset_StairCommon"));

        Assert.Equal(expression, properties["IsExternal"].Expression);
    }

    [Fact]
    public void ExactTypeIsExternal_PrecedesDifferingTypeSuffixAndFunction()
    {
        Element stair = Stair(4325, 5325, "Exact external precedence");
        Element type = StairType(5325);
        Confirm(stair, 7, 0.5, 1.0);
        type.Parameters.Add(Param(
            7310, "IsExternal", StorageType.Integer, 1));
        type.Parameters.Add(Param(
            7311, "IsExternal[Type]", StorageType.Integer, 0));

        string ifc = ExportToText(stair, type);
        Dictionary<string, PropertyValue> properties = Properties(
            ifc, SolePset(ifc, "Pset_StairCommon"));

        Assert.Equal("IFCBOOLEAN(.T.)", properties["IsExternal"].Expression);
    }

    [Theory]
    [InlineData("occurrence", "unsupported")]
    [InlineData("occurrence", "approximate")]
    [InlineData("occurrence", "conflicting")]
    [InlineData("type", "unsupported")]
    [InlineData("type", "approximate")]
    [InlineData("type", "conflicting")]
    public void BlockedExplicitIsExternal_DoesNotFallThrough(
        string scope, string mismatch)
    {
        Element stair = Stair(4326, 5326, "Blocked external precedence");
        Element type = StairType(5326);
        Confirm(stair, 7, 0.5, 1.0);
        Element target = scope == "occurrence" ? stair : type;
        Parameter blocker = Param(
            7320, "IsExternal",
            mismatch == "unsupported" ? StorageType.String : StorageType.Integer,
            mismatch == "unsupported" ? "{unresolved}" : 0);
        blocker.IsApproximate = mismatch == "approximate";
        target.Parameters.Add(blocker);
        if (mismatch == "conflicting")
            target.Parameters.Add(Param(
                7321, "isexternal", StorageType.Integer, 1));
        if (scope == "type")
            type.Parameters.Add(Param(
                7322, "IsExternal[Type]", StorageType.Integer, 1));

        string ifc = ExportToText(stair, type);
        Dictionary<string, PropertyValue> properties = Properties(
            ifc, SolePset(ifc, "Pset_StairCommon"));

        Assert.DoesNotContain("IsExternal", properties.Keys);
        Assert.Contains("Reference", properties.Keys);
        Assert.Contains("NumberOfRiser", properties.Keys);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CurtainWallSerializedFunction_IsNeverAStairAuthority(
        bool hasExactStairFunction)
    {
        Element stair = Stair(4327, 5327, "Unrelated wall function");
        Element type = StairType(5327);
        Confirm(stair, 7, 0.5, 1.0);
        type.SerializedCurtainWallFunction = 1;
        if (!hasExactStairFunction)
            type.Parameters.Clear();

        string ifc = ExportToText(stair, type);
        Dictionary<string, PropertyValue> properties = Properties(
            ifc, SolePset(ifc, "Pset_StairCommon"));

        if (hasExactStairFunction)
            Assert.Equal("IFCBOOLEAN(.F.)", properties["IsExternal"].Expression);
        else
            Assert.DoesNotContain("IsExternal", properties.Keys);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("blocked")]
    [InlineData("conflicting")]
    public void UnvalidatedTypeFunction_OmitsOnlyIsExternal(string mismatch)
    {
        Element stair = Stair(4330, 5330, "Unvalidated external stair");
        Element type = StairType(5330);
        Confirm(stair, 7, 0.5, 1.0);
        type.Parameters.Clear();
        if (mismatch != "missing")
            type.Parameters.Add(Param(
                -1001006, "FUNCTION_PARAM",
                mismatch == "blocked" ? StorageType.String : StorageType.Integer,
                mismatch == "blocked" ? "unresolved" : 0));
        if (mismatch == "conflicting")
            type.Parameters.Add(Param(
                -1001006, "FUNCTION_PARAM", StorageType.Integer, 1));

        string ifc = ExportToText(stair, type);
        Dictionary<string, PropertyValue> properties = Properties(
            ifc, SolePset(ifc, "Pset_StairCommon"));

        Assert.DoesNotContain("IsExternal", properties.Keys);
        Assert.Contains("Reference", properties.Keys);
        Assert.Contains("NumberOfRiser", properties.Keys);
    }

    [Fact]
    public void AmbiguousMaterializedTypeId_FailsNarrowResolverClosed()
    {
        Element stair = Stair(4350, 5350, "Ambiguous type id");
        Element type = StairType(5350);
        Confirm(stair, 7, 0.5, 1.0);

        Assert.Null(IfcExporter.ResolveExactSerializedStairType(
            stair,
            new Dictionary<int, Element>
            {
                [stair.Id] = stair,
                [type.Id] = type,
            },
            new Dictionary<int, int>
            {
                [stair.Id] = 1,
                [type.Id] = 2,
            }));
    }

    [Fact]
    public void InvalidOneRawField_OmitsOnlyThatProperty()
    {
        Element stair = Stair(4400, 5400, "Partial raw fields");
        Confirm(stair, 7, 0.5, 1.0);
        stair.SerializedActualRiserHeightFeet = double.NaN;

        string ifc = ExportToText(stair, StairType(5400));

        Dictionary<string, PropertyValue> properties = Properties(
            ifc, SolePset(ifc, "Pset_StairCommon"));
        Assert.Equal(
            new[] { "IsExternal", "Reference", "NumberOfRiser", "TreadLength" },
            properties.Keys);
    }

    [Theory]
    [InlineData(-2000919, "OST_StairsRuns")]
    [InlineData(-2000126, "OST_StairsRailing")]
    public void NonStairOrFlight_NeverReceivesStairNumericProperties(
        int categoryId, string category)
    {
        Element element = Stair(4500, 5500, "Adjacent class");
        element.CategoryId = categoryId;
        element.Category = category;
        Confirm(element, 7, 0.5, 1.0);

        string ifc = ExportToText(element, StairType(5500));

        Assert.DoesNotContain("'NumberOfRiser'", ifc);
        Assert.DoesNotContain("'RiserHeight'", ifc);
        Assert.DoesNotContain("'TreadLength'", ifc);
    }

    [Theory]
    [InlineData("qto-less")]
    [InlineData("geometry-less")]
    public void MissingQtoOrProduct_CreatesNoNumericOrphan(string mismatch)
    {
        Element stair = Stair(
            4600, 5600,
            mismatch == "qto-less" ? null : "Geometryless stair");
        Confirm(stair, 7, 0.5, 1.0);
        if (mismatch == "geometry-less") stair.Geometry = null;

        string ifc = ExportToText(stair, StairType(5600));

        Assert.DoesNotContain("'NumberOfRiser'", ifc);
        Assert.DoesNotContain("'RiserHeight'", ifc);
        Assert.DoesNotContain("'TreadLength'", ifc);
        if (mismatch == "geometry-less")
            Assert.DoesNotContain("Pset_StairCommon", ifc);
    }

    private static void Confirm(
        Element stair, int numberOfRisers,
        double riserHeightFeet, double treadDepthFeet)
    {
        stair.SerializedStairTypeId = stair.FamilyTypeId;
        stair.SerializedStairTypeLinkIsResolved = true;
        stair.SerializedActualNumberOfRisers = numberOfRisers;
        stair.SerializedActualRiserHeightFeet = riserHeightFeet;
        stair.SerializedActualTreadDepthFeet = treadDepthFeet;
    }

    private sealed record Pset(int Id, IReadOnlyList<int> ValueIds);
    private sealed record PropertyValue(int Id, string Expression);

    private static Dictionary<string, PropertyValue> Properties(string ifc, Pset pset)
    {
        var result = new Dictionary<string, PropertyValue>();
        foreach (int valueId in pset.ValueIds)
        {
            Match match = Assert.Single(Regex.Matches(
                ifc,
                $@"#{valueId}=IFCPROPERTYSINGLEVALUE\('((?:''|[^'])*)',(?:\$|''),(.*),\$\);"));
            result.Add(
                match.Groups[1].Value.Replace("''", "'"),
                new PropertyValue(valueId, match.Groups[2].Value));
        }
        return result;
    }

    private static Pset SolePset(string ifc, string psetName) =>
        Assert.Single(Psets(ifc, psetName));

    private static List<Pset> Psets(string ifc, string psetName) =>
        Regex.Matches(
                ifc,
                $@"#(\d+)=IFCPROPERTYSET\('[^']*',#\d+,'{Regex.Escape(psetName)}',(?:\$|''),\((#[0-9]+(?:,#[0-9]+)*)\)\);" )
            .Select(match => new Pset(
                int.Parse(match.Groups[1].Value),
                match.Groups[2].Value.Split(',')
                    .Select(value => int.Parse(value[1..])).ToList()))
            .ToList();

    private static Element Stair(
        int id, int typeId, string? name, double centreX = 0) =>
        new()
        {
            Id = id,
            TypeId = typeId,
            SuperInstanceId = 999,
            IsDiscovered = true,
            CategoryId = -2000120,
            Category = "OST_Stairs",
            Name = name,
            FamilyName = "Stair family",
            References = [3],
            Parameters =
            [
                Param(-1002050, "ELEM_TYPE_PARAM", StorageType.ElementId, typeId),
                Param(-1012100, "PHASE_CREATED", StorageType.ElementId, 3),
            ],
            BoundingBox = [centreX, 0, 0, centreX + 4, 3, 2],
            Geometry = SolidPrism(centreX),
        };

    private static Element StairType(int id) => new()
    {
        Id = id,
        TypeId = 3742,
        IsDiscovered = true,
        CategoryId = -2000120,
        Category = "OST_Stairs",
        Name = "Stair type",
        SerializedStairsTypeClassId = 3742,
        Parameters =
        [
            Param(-1001006, "FUNCTION_PARAM", StorageType.Integer, 0),
        ],
    };

    private static Parameter Param(
        int id, string name, StorageType storageType, object value) => new()
    {
        Id = id,
        Definition = new Definition { Id = id, Name = name },
        StorageType = storageType,
        Value = value,
    };

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
                new GeoBrepSolid
                {
                    IsClosed = true,
                    FaceIndices = [0, 1, 2, 3, 4, 5],
                },
            ],
        };
    }

    private static string ExportToText(params Element[] elements) => ExportToText(IfcProfile.Legacy, elements);

    private static string ExportToText(IfcProfile profile, params Element[] elements)
    {
        var info = new RevitFileInfo();
        info.Elements.AddRange(elements);
        string path = Path.Combine(
            Path.GetTempPath(),
            $"revitreader-stair-numeric-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter() { Profile = profile }.Export(info, path);
            return File.ReadAllText(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
