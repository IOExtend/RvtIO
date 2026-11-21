using System.Text.RegularExpressions;
using RvtIO.BuiltIns;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The IFCSITE of the current profile: the shared-coordinates placement (axes only for a rotated site), RefLatitude,
/// RefLongitude and RefElevation from the active project location, and the TrueNorth of the geometric context.
/// The legacy profile keeps its historical text.</summary>
public class IfcExporterSiteLocationTests
{
    private const double SampleCos = 0.7986355100472903, SampleSin = 0.6018150231520515;

    private static string Export(IfcProfile version, Action<RevitFileInfo> configure)
    {
        var info = new RevitFileInfo();
        configure(info);
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-site-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter { Profile = version }.Export(info, path);
            return File.ReadAllText(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static Dictionary<int, (string Type, string Args)> Entities(string ifc) =>
        Regex.Matches(ifc, @"^#(\d+)=(\w+)\((.*)\);\r?$", RegexOptions.Multiline)
            .ToDictionary(m => int.Parse(m.Groups[1].Value), m => (m.Groups[2].Value, m.Groups[3].Value));

    private static int Ref(string text) => int.Parse(text.TrimStart('#'));

    /// <summary>The site's origin point text and the axis placement arguments.</summary>
    private static (string Origin, string AxisArgs, string RefDirection) SitePlacement(string ifc)
    {
        var entities = Entities(ifc);
        var site = entities.Values.Single(e => e.Type == "IFCSITE").Args.Split(',');
        var local = entities[Ref(site[5])].Args.Split(',');
        string axisArgs = entities[Ref(local[1])].Args;
        string[] axis = axisArgs.Split(',');
        string refDirection = axis[2] == "$" ? "$" : entities[Ref(axis[2])].Args;
        return (entities[Ref(axis[0])].Args, axisArgs, refDirection);
    }

    private static string SiteArgs(string ifc) => Regex.Match(ifc, @"#\d+=IFCSITE\((.*?)\);").Groups[1].Value;

    [SkippableFact]
    public void Export_TopographyIsTheProjectSiteAndSiteFamilyKeepsItsGlobalIdAsProxy()
    {
        var info = new RvtFileReader().Read(TestFiles.Find("Test_2022.rvt"));
        var family = info.Elements.OrderBy(element => element.Id).First(element =>
            element.CategoryId == (int)BuiltInCategory.OST_Site && element.RootClassName == "FamilyInstance"
            && !element.IsDiscovered && !element.IsBlockOnly && element.Geometry?.Faces.Count > 0);
        string globalId = IfcGuid.ToIfcGuid(Assert.IsType<Guid>(family.SerializedExportId));

        using var output = new StringWriter();
        new IfcExporter { Profile = IfcProfile.Current }.Export(info, output);
        var entities = Entities(output.ToString());

        var proxy = Assert.Single(entities, entity => entity.Value.Args.StartsWith($"'{globalId}',", StringComparison.Ordinal));
        Assert.Equal("IFCBUILDINGELEMENTPROXY", proxy.Value.Type);
        string[] proxyArgs = IfcExporter.SplitStepArguments(proxy.Value.Args);
        Assert.Equal("IFCPRODUCTDEFINITIONSHAPE", entities[Ref(proxyArgs[6])].Type);
        Assert.Contains(entities.Values, entity => entity.Type == "IFCRELCONTAINEDINSPATIALSTRUCTURE"
            && IfcExporter.SplitStepArguments(entity.Args) is var containment
            && IfcExporter.SplitStepArguments(containment[4][1..^1]).Contains($"#{proxy.Key}")
            && entities[Ref(containment[5])].Type == "IFCBUILDINGSTOREY");

        var site = Assert.Single(entities, entity => entity.Value.Type == "IFCSITE");
        string[] siteArgs = IfcExporter.SplitStepArguments(site.Value.Args);
        Assert.Contains(info.Elements, element => element.CategoryId == (int)BuiltInCategory.OST_Topography
            && siteArgs[2].EndsWith($":{element.Id}'", StringComparison.Ordinal));
        Assert.Equal("IFCPRODUCTDEFINITIONSHAPE", entities[Ref(siteArgs[6])].Type);
        int projectId = Assert.Single(entities, entity => entity.Value.Type == "IFCPROJECT").Key;
        Assert.Contains(entities.Values, entity => entity.Type == "IFCRELAGGREGATES"
            && entity.Args.EndsWith($",#{projectId},(#{site.Key})", StringComparison.Ordinal));
    }

    private static readonly double[] CcTransform =
        [1.0, 0, 0, 0, 1.0, 0, 0, 0, 1.0, 343696.5743381642, 283643.7554585116, 11.975065616797893];

    private static void CcLocation(RevitFileInfo info)
    {
        info.SharedTransform = CcTransform;
        info.SiteLatitudeRadians = 0.9730210579868378;
        info.SiteLongitudeRadians = 0.6579891413176677;
        info.SiteElevationFeet = 11.975065616797893;
    }

    [Theory]
    [InlineData(0.9730210579868378, "(55,44,59,999999)")]
    [InlineData(0.6579891413176677, "(37,42,0,2746)")]
    [InlineData(0.7367558371443657, "(42,12,46,799999)")]
    [InlineData(-1.2397684542153908, "(-71,-2,0,-599999)")]
    [InlineData(0.0, "(0,0,0,0)")]
    public void CompoundPlaneAngle_TruncatesEachPartTowardZero(double radians, string expected)
    {
        Assert.Equal(expected, IfcExporter.CompoundPlaneAngle(radians));
    }

    [Fact]
    public void Placement_ShiftOnlySiteKeepsTheDefaultAxesInCurrent()
    {
        string ifc = Export(IfcProfile.Current, CcLocation);

        var (origin, axisArgs, _) = SitePlacement(ifc);

        Assert.Equal("(104758715.85827245,86454616.663754329,3649.9999999999982)", origin);
        Assert.EndsWith(",$,$", axisArgs);
    }

    [Fact]
    public void Placement_ShiftOnlySiteKeepsTheExplicitAxesInLegacy()
    {
        string ifc = Export(IfcProfile.Legacy, CcLocation);

        var (origin, axisArgs, refDirection) = SitePlacement(ifc);

        Assert.Equal("(104758715.858272,86454616.6637543,3650.)", origin);
        Assert.DoesNotContain("$", axisArgs);
        Assert.Equal("(1.,0.,0.)", refDirection);
    }

    [Fact]
    public void Placement_RotatedSiteCarriesTheZAxisAndTheReferenceDirection()
    {
        string ifc = Export(IfcProfile.Current, info =>
            info.SharedTransform = [SampleCos, SampleSin, 0, -SampleSin, SampleCos, 0, 0, 0, 1.0, -65.03805139774867, -61.70701987942478, 0]);

        var (origin, axisArgs, refDirection) = SitePlacement(ifc);

        Assert.StartsWith("(-19823.598066033", origin);
        Assert.DoesNotContain("$", axisArgs);
        Assert.StartsWith("(0.798635510047", refDirection);
        Assert.Contains(",0.601815023152", refDirection);
    }

    [Fact]
    public void Placement_ElevationOnlySiteIsPlacedInCurrentButNotInLegacy()
    {
        void Elevated(RevitFileInfo info) =>
            info.SharedTransform = [1.0, 0, 0, 0, 1.0, 0, 0, 0, 1.0, 0, 0, 5.0];

        var (origin, _, _) = SitePlacement(Export(IfcProfile.Current, Elevated));
        Assert.Equal("(0.,0.,1524.)", origin);

        // The legacy profile keeps its rule: only a rotation or a horizontal shift places the site.
        Assert.Equal("(0.,0.,0.)", SitePlacement(Export(IfcProfile.Legacy, Elevated)).Origin);
    }

    [Fact]
    public void Placement_NoSharedTransformIsTheWorldOrigin()
    {
        var (origin, axisArgs, _) = SitePlacement(Export(IfcProfile.Current, _ => { }));

        Assert.Equal("(0.,0.,0.)", origin);
        Assert.EndsWith(",$,$", axisArgs);
    }

    [Fact]
    public void Site_CurrentWritesLatitudeLongitudeAndProjectBasePointElevation()
    {
        string site = SiteArgs(Export(IfcProfile.Current, CcLocation));

        Assert.EndsWith(".ELEMENT.,(55,44,59,999999),(37,42,0,2746),3649.9999999999982,'',$", site);
    }

    [Fact]
    public void Site_CurrentWithoutAProjectLocationLeavesTheGeoReferenceUnset()
    {
        string site = SiteArgs(Export(IfcProfile.Current, info => info.SharedTransform = CcTransform));

        Assert.EndsWith(".ELEMENT.,$,$,$,'',$", site);
    }

    [Fact]
    public void Site_LegacyNeverWritesTheGeoReference()
    {
        string site = SiteArgs(Export(IfcProfile.Legacy, CcLocation));

        Assert.EndsWith(".ELEMENT.,$,$,$,$,$", site);
    }

    [Fact]
    public void Site_ZeroElevationIsWrittenAsAReal()
    {
        string site = SiteArgs(Export(IfcProfile.Current, info =>
        {
            CcLocation(info);
            info.SiteElevationFeet = 0;
        }));

        Assert.EndsWith(",(37,42,0,2746),0.,'',$", site);
    }

    [Fact]
    public void Context_CurrentTrueNorthIsTheYAxis()
    {
        var entities = Entities(Export(IfcProfile.Current, _ => { }));

        string[] context = entities.Values.Single(e => e.Type == "IFCGEOMETRICREPRESENTATIONCONTEXT").Args.Split(',');

        Assert.Equal("(6.12323399573677E-17,1.)", entities[Ref(context[5])].Args);
    }

    [Fact]
    public void Context_LegacyLeavesTrueNorthUnset()
    {
        var entities = Entities(Export(IfcProfile.Legacy, _ => { }));

        string[] context = entities.Values.Single(e => e.Type == "IFCGEOMETRICREPRESENTATIONCONTEXT").Args.Split(',');

        Assert.Equal("$", context[5]);
    }
}
