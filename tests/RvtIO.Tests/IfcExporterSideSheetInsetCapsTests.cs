using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using RvtIO.Geometry;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

public class IfcExporterSideSheetInsetCapsTests
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
            .TryGetVerifiedGrossWallBoundaryFromTwentyNineFaceInsetCapsAndSideSheets(
                Wall, Wall.BoundingBox!, Rebuilt, Elements, Inverse, Ids, Operands,
                Standalone, out gross, out _);
    }

    [Fact]
    public void SideSheetInsetCaps_RecoversExactCapRectangle()
    {
        var f = Create();
        Assert.True(f.Recover(out var gross));
        Assert.Equal(6, gross.Faces.Count);
        Assert.True(FacetedShell.IsExactFacetedShell(gross.Faces, gross.BrepSolids![0].FaceIndices));
        Assert.True(IfcExporter.TryGetExactVerticalRectangularPrismSweptOpening(gross, out var box));
        Assert.Equal(1800, Math.Max(box.WidthFt, box.DepthFt) * 304.8, 5);
        Assert.Equal(80, Math.Min(box.WidthFt, box.DepthFt) * 304.8, 5);
        Assert.Equal(2840, box.HeightFt * 304.8, 5);
        Assert.NotEqual(f.Wall.BoundingBox![0], gross.BoundingBox![0]);
    }

    [Fact]
    public void SideSheetInsetCaps_WriterRetainsCapBoundsNotAuxiliarySheets()
    {
        var f = Create();
        Assert.True(f.Recover(out var gross));
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
        Assert.Equal(1.8, double.Parse(rectangle.Groups[1].Value, CultureInfo.InvariantCulture), 9);
        Assert.Equal(0.08, double.Parse(rectangle.Groups[2].Value, CultureInfo.InvariantCulture), 9);
        var extrusion = Assert.Single(Regex.Matches(text,
            @"IFCEXTRUDEDAREASOLID\(#\d+,#\d+,#\d+,([^\)]+)\)"));
        Assert.Equal(2.84, double.Parse(extrusion.Groups[1].Value, CultureInfo.InvariantCulture), 9);
    }

    [Fact]
    public void SideSheetInsetCaps_AcceptsReversedCyclesAndPermutedClaims()
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
    public void SideSheetInsetCaps_RejectsEveryChangedSourceAndRebuiltCoordinate()
    {
        var original = Create();
        for (int face = 0; face < original.Wall.Geometry!.Faces.Count; face++)
        for (int point = 0; point < original.Wall.Geometry.Faces[face].OuterVertices!.Count; point++)
        for (int coordinate = 0; coordinate < 3; coordinate++)
        {
            var f = Create();
            f.Wall.Geometry!.Faces[face].OuterVertices![point][coordinate] += 0.1;
            Assert.False(f.Recover(out _), $"source {face}/{point}/{coordinate}");
        }
        for (int point = 0; point < 9; point++)
        for (int coordinate = 0; coordinate < 3; coordinate++)
        {
            var f = Create();
            f.Wall.Geometry!.Faces[3].AlternativeOuterVertices![point][coordinate] += 0.1;
            Assert.False(f.Recover(out _), $"alternative {point}/{coordinate}");
        }
        for (int face = 0; face < original.Rebuilt.Faces.Count; face++)
        for (int point = 0; point < original.Rebuilt.Faces[face].OuterVertices!.Count; point++)
        for (int coordinate = 0; coordinate < 3; coordinate++)
        {
            var f = Create();
            f.Rebuilt.Faces[face].OuterVertices![point][coordinate] += 0.1;
            Assert.False(f.Recover(out _), $"rebuilt {face}/{point}/{coordinate}");
        }
    }

    [Theory]
    [InlineData("missing-alternative")]
    [InlineData("reordered-alternative")]
    [InlineData("inner-ring")]
    [InlineData("solid-roster")]
    [InlineData("solid-closed")]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("axis-inset")]
    [InlineData("axis-slope")]
    [InlineData("bbox")]
    [InlineData("rebuilt-bbox")]
    [InlineData("missing-reference")]
    [InlineData("duplicate-decoded")]
    [InlineData("missing-cutter")]
    [InlineData("reverse-reference")]
    [InlineData("inverse-host")]
    [InlineData("duplicate-emitted")]
    [InlineData("standalone")]
    [InlineData("writer-size")]
    [InlineData("writer-depth")]
    [InlineData("writer-position")]
    [InlineData("writer-rotation")]
    [InlineData("writer-scaled-axis")]
    [InlineData("writer-nan")]
    [InlineData("writer-infinite")]
    [InlineData("duplicate-source-cut")]
    [InlineData("inexact-depth")]
    public void SideSheetInsetCaps_FailsClosed(string mutation)
    {
        var f = Create();
        int id = f.Wall.Id;
        var first = f.Operands[id][0];
        switch (mutation)
        {
            case "missing-alternative": f.Wall.Geometry!.Faces[3].AlternativeOuterVertices = null; break;
            case "reordered-alternative": f.Wall.Geometry!.Faces[3].AlternativeOuterVertices!.Reverse(1, 3); break;
            case "inner-ring": f.Wall.Geometry!.Faces[0].InnerVertices = [f.Wall.Geometry.Faces[0].OuterVertices!]; break;
            case "solid-roster": f.Wall.Geometry!.BrepSolids![0].FaceIndices.Remove(17); break;
            case "solid-closed": f.Wall.Geometry!.BrepSolids![0].IsClosed = true; break;
            case "width": f.Wall.Parameters.Single(p => p.Id == -1001000).Value = 0.5; break;
            case "height": f.Wall.Parameters.Single(p => p.Id == -1001105).Value = 12.0; break;
            case "axis-inset": f.Wall.Location!.StartPoint[0] += 0.1; break;
            case "axis-slope": f.Wall.Location!.EndPoint[2] += 0.1; break;
            case "bbox": f.Wall.BoundingBox![0] += 0.1; break;
            case "rebuilt-bbox": f.Rebuilt.BoundingBox![0] += 0.1; break;
            case "missing-reference": f.Wall.References!.Remove(first.CutterId); break;
            case "duplicate-decoded": f.Wall.CuttingElementIds![1] = first.CutterId; break;
            case "missing-cutter": f.Elements.Remove(first.CutterId); break;
            case "reverse-reference": f.Elements[first.CutterId].References = []; break;
            case "inverse-host": f.Inverse[first.CutterId] = []; break;
            case "duplicate-emitted": f.Ids[id][1] = first.CutterId; break;
            case "standalone": f.Standalone[id] = [99991]; break;
            case "writer-size": first.Operand = first.Operand with { WidthFt = first.Operand.WidthFt + 0.1 }; break;
            case "writer-depth": first.Operand = first.Operand with { DepthFt = first.Operand.DepthFt + 0.1 }; break;
            case "writer-position": first.Operand = first.Operand with { CentreXFt = first.Operand.CentreXFt + 0.1 }; break;
            case "writer-rotation": first.Operand = first.Operand with { AxisX = first.Operand.AxisY, AxisY = -first.Operand.AxisX }; break;
            case "writer-scaled-axis": first.Operand = first.Operand with
                { AxisX = first.Operand.AxisX * 2, AxisY = first.Operand.AxisY * 2,
                    WidthFt = first.Operand.WidthFt / 2, DepthFt = first.Operand.DepthFt / 2 }; break;
            case "writer-nan": first.Operand = first.Operand with { CentreXFt = double.NaN }; break;
            case "writer-infinite": first.Operand = first.Operand with { HeightFt = double.PositiveInfinity }; break;
            case "duplicate-source-cut": first.Operand = f.Operands[id][1].Operand; break;
            case "inexact-depth": first.Operand = first.Operand with { ExactDepth = false }; break;
            default: throw new ArgumentException(mutation);
        }
        f.Operands[id][0] = first;
        Assert.False(f.Recover(out var gross));
        Assert.Empty(gross.Faces);
    }

    private static Fixture Create()
    {
        const string encoded = "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";
        using var stream = new GZipStream(new MemoryStream(Convert.FromBase64String(encoded)), CompressionMode.Decompress);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        static double[] Point(JsonElement e) => e.EnumerateArray().Select(v => v.GetDouble()).ToArray();
        static List<double[]> Ring(JsonElement e) => e.EnumerateArray().Select(Point).ToList();
        var geometry = root.GetProperty("Geometry");
        var wall = new Element
        {
            Id = 98511, CategoryId = -2000011, Category = "OST_Walls",
            Location = JsonSerializer.Deserialize<LocationCurve>(root.GetProperty("Location")),
            BoundingBox = Point(root.GetProperty("BoundingBox")),
            CuttingElementIds = [98512, 98513], References = [98512, 98513],
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
        wall.Geometry.BrepSolids = [new() { IsClosed = false, FaceIndices = Enumerable.Range(0, 29).ToList() }];
        Assert.True(IfcExporter.TryBuildStraightWallBoundary(wall, out var rebuilt));
        var result = new Fixture { Wall = wall, Rebuilt = rebuilt, Elements = new() { [wall.Id] = wall } };
        double dx = wall.Location!.EndPoint[0] - wall.Location.StartPoint[0];
        double dy = wall.Location.EndPoint[1] - wall.Location.StartPoint[1];
        double length = Math.Sqrt(dx * dx + dy * dy);
        dx /= length; dy /= length;
        foreach (var (id, faceIndex) in new[] { (98512, 1), (98513, 3) })
        {
            var points = wall.Geometry.Faces[faceIndex].OuterVertices!;
            double lo = points.Min(p => p[0]*dx+p[1]*dy), hi = points.Max(p => p[0]*dx+p[1]*dy);
            double normal = -wall.Location.StartPoint[0]*dy+wall.Location.StartPoint[1]*dx;
            double centre = (lo + hi) / 2;
            var operand = new IfcExporter.HostedOpeningGeometry(
                centre*dx-normal*dy, centre*dy+normal*dx, dx, dy,
                hi-lo, 80/304.8, points.Min(p=>p[2]), points.Max(p=>p[2])-points.Min(p=>p[2]), ExactDepth: true);
            result.Elements[id] = new Element { Id = id, CategoryId = -2000151, References = [wall.Id] };
            result.Inverse[id] = [wall.Id];
            if (!result.Operands.ContainsKey(wall.Id)) result.Operands[wall.Id] = [];
            result.Operands[wall.Id].Add((id, operand));
        }
        result.Ids[wall.Id] = [98512, 98513];
        return result;
    }
}
