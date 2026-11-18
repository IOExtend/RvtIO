using System.IO.Compression;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using RvtIO.Geometry;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

public class IfcExporterInsetCapsTests
{
    private sealed class Fixture
    {
        public required Element Wall;
        public required GeometryData Rebuilt;
        public required Dictionary<int, Element> Elements;
        public Dictionary<int, List<int>> Inverse = [];
        public Dictionary<int, List<int>> Ids = [];
        public Dictionary<int, List<(int CutterId, IfcExporter.HostedOpeningGeometry Operand)>> Operands = [];
        public Dictionary<int, List<int>> Standalone = [];
        public bool Recover(out GeometryData gross) => IfcExporter
            .TryGetVerifiedGrossWallBoundaryFromFourteenFaceDualAlternativeInsetCaps(
                Wall, Wall.BoundingBox!, Rebuilt, Elements, Inverse, Ids, Operands,
                Standalone, out gross, out _);
    }

    [Fact]
    public void DualAlternativeInsetCaps_RecoversSourceRectangle()
    {
        var f = Create();
        Assert.True(f.Recover(out var gross));
        Assert.Equal(6, gross.Faces.Count);
        Assert.True(FacetedShell.IsExactFacetedShell(gross.Faces, gross.BrepSolids![0].FaceIndices));
        Assert.True(IfcExporter.TryGetExactVerticalRectangularPrismSweptOpening(gross, out var box));
        Assert.Equal(1690, Math.Max(box.WidthFt, box.DepthFt) * 304.8, 5);
        Assert.Equal(80, Math.Min(box.WidthFt, box.DepthFt) * 304.8, 5);
        Assert.Equal(2840, box.HeightFt * 304.8, 5);
    }

    [Fact]
    public void DualAlternativeInsetCaps_InteriorDimensionsAreOwnedBySixSourceFaces()
    {
        var f = Create();
        f.Elements[f.Operands[f.Wall.Id][0].CutterId].Parameters.Clear();
        Assert.True(f.Recover(out _));
    }

    [Fact]
    public void DualAlternativeInsetCaps_WriterUsesConfirmedCapsInsteadOfBacktrackingJoinedPlan()
    {
        var f = Create();
        Assert.True(f.Recover(out var gross));
        Assert.True(IfcExporter.TryGetAuthoredStraightWallPlan(f.Wall, 80 / 304.8, out _));
        var method = typeof(IfcExporter).GetMethod("CreateWallSweptSolid",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var writer = new StepWriter();
        var values = new Dictionary<string, object?>
        {
            ["w"] = writer, ["elem"] = f.Wall, ["bbox"] = f.Wall.BoundingBox,
            ["subCtxId"] = 900, ["axisSubCtxId"] = 901, ["parentPlacementId"] = 902,
            ["forceSerializedDefinition"] = true, ["verifiedBoundary"] = gross,
            ["allowBoundaryOwnedVerticalDefinition"] = true,
            ["useVerifiedBoundaryBoundsForVerticalDefinition"] = true,
        };
        method.Invoke(new IfcExporter(), method.GetParameters().Select(p =>
            values.TryGetValue(p.Name!, out var value) ? value : p.DefaultValue).ToArray());
        using var output = new StringWriter();
        writer.WriteTo(output, "test.ifc", "IFC2X3", "test", "test");
        string text = output.ToString();
        Assert.DoesNotContain("IFCARBITRARYCLOSEDPROFILEDEF", text);
        var rectangle = Assert.Single(Regex.Matches(text,
            @"IFCRECTANGLEPROFILEDEF\(\.AREA\.,\$,#\d+,([^,]+),([^\)]+)\)"));
        Assert.Equal(1.69, double.Parse(rectangle.Groups[1].Value, CultureInfo.InvariantCulture), 9);
        Assert.Equal(0.08, double.Parse(rectangle.Groups[2].Value, CultureInfo.InvariantCulture), 9);
        var extrusion = Assert.Single(Regex.Matches(text,
            @"IFCEXTRUDEDAREASOLID\(#\d+,#\d+,#\d+,([^\)]+)\)"));
        Assert.Equal(2.84, double.Parse(extrusion.Groups[1].Value, CultureInfo.InvariantCulture), 9);
    }

    [Fact]
    public void DualAlternativeInsetCaps_AcceptsReversedCyclesAndPermutedClaims()
    {
        var f = Create();
        foreach (var face in f.Wall.Geometry!.Faces.Concat(f.Rebuilt.Faces))
        {
            face.OuterVertices!.Reverse();
            face.AlternativeOuterVertices?.Reverse();
        }
        f.Wall.CuttingElementIds!.Reverse();
        f.Ids[f.Wall.Id].Reverse();
        f.Operands[f.Wall.Id].Reverse();
        Assert.True(f.Recover(out _));
    }

    [Fact]
    public void DualAlternativeInsetCaps_RejectsEveryChangedSourceAndRebuiltVertex()
    {
        var original = Create();
        for (int i = 0; i < original.Wall.Geometry!.Faces.Count; i++)
        for (int point = 0; point < original.Wall.Geometry.Faces[i].OuterVertices!.Count; point++)
        for (int coordinate = 0; coordinate < 3; coordinate++)
        {
            var f = Create();
            f.Wall.Geometry!.Faces[i].OuterVertices![point][coordinate] += 0.1;
            Assert.False(f.Recover(out _), $"source face {i}, point {point}, coordinate {coordinate}");
        }
        foreach (int i in new[] { 3, 7 })
        for (int point = 0; point < original.Wall.Geometry.Faces[i].AlternativeOuterVertices!.Count; point++)
        for (int coordinate = 0; coordinate < 3; coordinate++)
        {
            var f = Create();
            f.Wall.Geometry!.Faces[i].AlternativeOuterVertices![point][coordinate] += 0.1;
            Assert.False(f.Recover(out _), $"alternative face {i}, point {point}, coordinate {coordinate}");
        }
        for (int i = 0; i < original.Rebuilt.Faces.Count; i++)
        for (int point = 0; point < original.Rebuilt.Faces[i].OuterVertices!.Count; point++)
        for (int coordinate = 0; coordinate < 3; coordinate++)
        {
            var f = Create();
            f.Rebuilt.Faces[i].OuterVertices![point][coordinate] += 0.1;
            Assert.False(f.Recover(out _), $"rebuilt face {i}, point {point}, coordinate {coordinate}");
        }
    }

    [Theory]
    [InlineData("missing-alternative")]
    [InlineData("reordered-alternative")]
    [InlineData("extra-inner")]
    [InlineData("solid-roster")]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("axis-inset")]
    [InlineData("axis-slope")]
    [InlineData("missing-reference")]
    [InlineData("duplicate-decoded")]
    [InlineData("missing-cutter")]
    [InlineData("reverse-reference")]
    [InlineData("inverse-host")]
    [InlineData("duplicate-emitted")]
    [InlineData("standalone")]
    [InlineData("source-writer-size")]
    [InlineData("source-writer-depth")]
    [InlineData("source-writer-position")]
    [InlineData("source-writer-rotation")]
    [InlineData("source-writer-scaled-axis")]
    [InlineData("source-writer-nan")]
    [InlineData("source-writer-infinite")]
    [InlineData("duplicate-source-cut")]
    [InlineData("inexact-depth")]
    [InlineData("own-width")]
    [InlineData("duplicate-own-width")]
    public void DualAlternativeInsetCaps_FailsClosed(string mutation)
    {
        var f = Create();
        int id = f.Wall.Id;
        var first = f.Operands[id][0];
        switch (mutation)
        {
            case "missing-alternative": f.Wall.Geometry!.Faces[3].AlternativeOuterVertices = null; break;
            case "reordered-alternative": f.Wall.Geometry!.Faces[3].AlternativeOuterVertices!.Reverse(1, 3); break;
            case "extra-inner": f.Wall.Geometry!.Faces[0].InnerVertices = [f.Wall.Geometry.Faces[0].OuterVertices!]; break;
            case "solid-roster": f.Wall.Geometry!.BrepSolids![0].FaceIndices.Add(13); break;
            case "width": f.Wall.Parameters.Single(p => p.Id == -1001000).Value = 0.5; break;
            case "height": f.Wall.Parameters.Single(p => p.Id == -1001105).Value = 12.0; break;
            case "axis-inset": f.Wall.Location!.StartPoint[0] += 0.1; break;
            case "axis-slope": f.Wall.Location!.EndPoint[2] += 0.1; break;
            case "missing-reference": f.Wall.References!.Remove(first.CutterId); break;
            case "duplicate-decoded": f.Wall.CuttingElementIds![1] = first.CutterId; break;
            case "missing-cutter": f.Elements.Remove(first.CutterId); break;
            case "reverse-reference": f.Elements[first.CutterId].References = []; break;
            case "inverse-host": f.Inverse[first.CutterId] = []; break;
            case "duplicate-emitted": f.Ids[id][1] = first.CutterId; break;
            case "standalone": f.Standalone[id] = [99991]; break;
            case "source-writer-size": first.Operand = first.Operand with { WidthFt = first.Operand.WidthFt + 0.1 }; break;
            case "source-writer-depth": first.Operand = first.Operand with { DepthFt = first.Operand.DepthFt + 0.1 }; break;
            case "source-writer-position": first.Operand = first.Operand with { CentreXFt = first.Operand.CentreXFt + 0.1 }; break;
            case "source-writer-rotation": first.Operand = first.Operand with { AxisX = first.Operand.AxisY, AxisY = -first.Operand.AxisX }; break;
            case "source-writer-scaled-axis": first.Operand = first.Operand with
                { AxisX = first.Operand.AxisX * 2, AxisY = first.Operand.AxisY * 2,
                    WidthFt = first.Operand.WidthFt / 2, DepthFt = first.Operand.DepthFt / 2 }; break;
            case "source-writer-nan": first.Operand = first.Operand with { CentreXFt = double.NaN }; break;
            case "source-writer-infinite": first.Operand = first.Operand with { HeightFt = double.PositiveInfinity }; break;
            case "duplicate-source-cut": first.Operand = f.Operands[id][1].Operand; break;
            case "inexact-depth": first.Operand = first.Operand with { ExactDepth = false }; break;
            case "own-width": f.Elements[f.Operands[id][1].CutterId].Parameters[0].Value = 1.0; break;
            case "duplicate-own-width":
                var endCutter = f.Elements[f.Operands[id][1].CutterId];
                endCutter.Parameters.Add(endCutter.Parameters[0]); break;
            default: throw new ArgumentException(mutation);
        }
        f.Operands[id][0] = first;
        Assert.False(f.Recover(out var gross));
        Assert.Empty(gross.Faces);
    }

    private static Fixture Create()
    {
        // Complete captured fourteen-face source, including internal alternatives.
        // IDs are deliberately unrelated to the RVT/IFC reference.
        const string encoded = "H4sIAAAAAAACCs1Y227iSBD9Fz83VvWl+sIbyZBJJHaDgJl5iCLEhp5ZJGKvwGQ3ivj3LYfMDMY2aWz28ujL6T51+nRVd71Eg/Rhli3SJOq+RJeb1ZOfPP/ho240WCQ+YtE4m62yYbpIsqh7BzFobq0AoYRTIBzninWUi6V1RklUDoRhXJpYguTKadDCOaHvWdRP5t9H6WCsuXQGnFNcGwGlMbRz1aN8WKz8w47tXYczESNII/NZhQKlXb/DkQH9OPDJt+z3qEtTGbQogVuluZByy6KLdJPMF8m3i/SvejYowBnDlbLCoNBER8fOIGjU9JczTrJOLoeTyNFw+qAcavGKFTQjDYdSCK0IqnQsiD+941ZLFIIIfvTpo89Wz7nsw5Vf+1yabLXx/xN+V7MHf0lEiBWtcfSVHkfpn2si9BLdJHNP1IBFt5vMr+jdXXkuY8tzWaJpKRxUFlErCYqmqsDWQAuGsE7tsJYWWDpLnqSfTVWIbbGHlO+37IcIfF8EjIFGE9JpozRaWxGHRtIbY05rp5HcLomWzslgrADopUR03EjZHCoc1oJRWkBjlDCWW7kDh3IuYAsiiKNOqFnNQ79WOuFtSd53+y6Ud3cKD4TWyF/G7ssgizIUzWVMTSSVGyIQW+3qUA3D0BjIm5JGb0nRJ1ROnvzbfmi8IO2ccGYNbDU4z5vGIT2hA0Ru2glYBTaufuqDlB0qd0toKeDARS5V8/2do45lUVfNpTqZFZMo/wegVfk3kPABdF8BLCrQMBudNwm28E9d+Sph9zXQDTQ490YIzhwnoQ+L33EdTEA5bcinVSaztbG0wR7oUK4i/4oCYWWkzVEmVIET5g0/SweU7z0D2uJGfDcxQquTaWtso2QOlQfifRXcMRVE88rS4nBuWmBDy2jhdgNHNGizmie6qOqK0gyrAykXVDh6yTMn3dSanRRYy1NRQxcVRBAn325aXLtNS2zTS9V7t/2DKx4HVAosB8q4mk4cFUPKyk5WWFunFbSmLodRLoPvt1Sa1+lyMT9sAt2sL5fp2s+j7tfZcu13vSP6vHjw+a/AqFHHJFMMmWaGWUbNPXpJbs0bTa/drvV/1eja0upGw9lqRs04v3qLjCLpcADOgUrAr/SJ2qFfeoPBdHI7nN5eXY37E+qMfp4tN/Ql99Vrf1EaYUAqLnguycjP5rfJ8vmHKJ+SRTZ+fvwtXdJoj480wGWaPNGc1M0kwbKUPEV+i+2r3X5SsEUKF71xv4qDFBZyczsELqnRiWclgUUSn8b90fS6f/PxejId9ka9X35SoT4u/95xJUWsPCMPACjy6E0mo+mXmw+T60MaEAstlDbccU1A5SjpFYjsWq3hPO63fwNBLvMFKxcAAA==";
        using var stream = new GZipStream(new MemoryStream(Convert.FromBase64String(encoded)), CompressionMode.Decompress);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        static double[] Point(JsonElement e) => e.EnumerateArray().Select(v => v.GetDouble()).ToArray();
        static List<double[]> Ring(JsonElement e) => e.EnumerateArray().Select(Point).ToList();
        var geometry = root.GetProperty("Geometry");
        var wall = new Element
        {
            Id = 98411, CategoryId = -2000011, Category = "OST_Walls",
            Location = JsonSerializer.Deserialize<LocationCurve>(root.GetProperty("Location")),
            BoundingBox = Point(root.GetProperty("BoundingBox")),
            CuttingElementIds = [98412, 98413], References = [98412, 98413],
            Parameters = root.GetProperty("Parameters").EnumerateArray().Select(p => new Parameter
            {
                Id = p.GetProperty("Id").GetInt32(), StorageType = StorageType.Double,
                Value = p.GetProperty("Value").GetDouble(),
            }).ToList(),
            Geometry = new GeometryData { BoundingBox = Point(geometry.GetProperty("BoundingBox")) },
        };
        foreach (var face in geometry.GetProperty("faceRows").EnumerateArray())
            wall.Geometry.Faces.Add(new GeoFace
            {
                OuterVertices = Ring(face.GetProperty("Outer")),
                AlternativeOuterVertices = face.TryGetProperty("Alternative", out var alt)
                    && alt.ValueKind == JsonValueKind.Array ? Ring(alt) : null,
            });
        wall.Geometry.BrepSolids = [new() { IsClosed = false, FaceIndices = Enumerable.Range(0, 13).ToList() }];
        Assert.True(IfcExporter.TryBuildStraightWallBoundary(wall, out var rebuilt));
        var result = new Fixture { Wall = wall, Rebuilt = rebuilt, Elements = new() { [wall.Id] = wall } };
        double dx = wall.Location!.EndPoint[0] - wall.Location.StartPoint[0];
        double dy = wall.Location.EndPoint[1] - wall.Location.StartPoint[1];
        double length = Math.Sqrt(dx * dx + dy * dy);
        dx /= length; dy /= length;
        foreach (var (id, faceIndex) in new[] { (98412, 1), (98413, 0) })
        {
            var points = wall.Geometry.Faces[faceIndex].OuterVertices!;
            double lo = points.Min(p => p[0]*dx+p[1]*dy), hi = points.Max(p => p[0]*dx+p[1]*dy);
            double normal = -wall.Location.StartPoint[0]*dy+wall.Location.StartPoint[1]*dx;
            double fullWidth = faceIndex == 0 ? 250 / 304.8 : hi - lo;
            double centre = hi - fullWidth / 2;
            double height = points.Max(p=>p[2])-points.Min(p=>p[2]);
            var operand = new IfcExporter.HostedOpeningGeometry(
                centre*dx-normal*dy, centre*dy+normal*dx, dx, dy,
                fullWidth, 80/304.8, points.Min(p=>p[2]), height, ExactDepth: true);
            static Parameter Length(int parameterId, string guid, double value) => new()
            {
                Id = parameterId, Guid = new Guid(guid), StorageType = StorageType.Double, Value = value,
                ForgeId = "autodesk.parameter.aec.revit.external.-1:" + guid.Replace("-", "") + "-1.0.0",
            };
            result.Elements[id] = new Element
            {
                Id = id, CategoryId = -2000151, References = [wall.Id], Parameters =
                [
                    Length(99001, "cef807ab-6704-4013-8d86-96c5a31b2d1e", fullWidth),
                    Length(99002, "e140a591-7a59-4036-b7e4-074fbd8222ff", 80/304.8),
                    Length(99003, "e31864ac-8135-4bea-a7d3-f2a96b81a6ea", height),
                ],
            };
            result.Inverse[id] = [wall.Id];
            if (!result.Operands.ContainsKey(wall.Id)) result.Operands[wall.Id] = [];
            result.Operands[wall.Id].Add((id, operand));
        }
        result.Ids[wall.Id] = [98412, 98413];
        return result;
    }
}
