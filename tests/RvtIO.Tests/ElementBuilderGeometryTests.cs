using RvtIO.Model;
using RvtIO.Internal;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class ElementBuilderGeometryTests
{
    [Fact]
    public void SelectWallLocationCurveCandidate_RejectsVerticalExtrusionGenerator()
    {
        var locationCurve = new CurveDriverData
        {
            OriginPoint = [22.837701828863633, 7.868786159508579, 0],
            DirectionVector = [-1.67e-15, -1, 0],
            EndParams = [18.37270341207317, 68.248031496063],
        };
        var extrusionGenerator = new CurveDriverData
        {
            OriginPoint = [22.837701828863633, -60.37924533655442, -3.937],
            DirectionVector = [0, 0, 1],
            EndParams = [0, 12.795275590551181],
        };

        CurveDriverData selected = ElementBuilder.SelectWallLocationCurveCandidate(
            [locationCurve, extrusionGenerator], [0, 0, 1]);

        Assert.Same(locationCurve, selected);
    }

    [Fact]
    public void DeduplicateExactFaces_RemovesReorderedBitIdenticalFace()
    {
        var a = new[] { 0.0, 0.0, 0.0 };
        var b = new[] { 1.0, 0.0, 0.0 };
        var c = new[] { 0.0, 1.0, 0.0 };
        var geometry = new GeometryData
        {
            Faces =
            [
                new GeoFace { OuterVertices = [a, b, c] },
                new GeoFace { OuterVertices = [c, a, b] },
            ],
        };

        ElementBuilder.DeduplicateExactFaces(geometry);

        Assert.Single(geometry.Faces);
    }

    [Fact]
    public void DeduplicateExactFaces_PreservesOneBitCoordinateDifference()
    {
        double adjacent = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(1.0) + 1);
        var geometry = new GeometryData
        {
            Faces =
            [
                new GeoFace { OuterVertices = [[0.0, 0.0, 0.0], [1.0, 0.0, 0.0], [0.0, 1.0, 0.0]] },
                new GeoFace { OuterVertices = [[0.0, 0.0, 0.0], [adjacent, 0.0, 0.0], [0.0, 1.0, 0.0]] },
            ],
        };

        ElementBuilder.DeduplicateExactFaces(geometry);

        Assert.Equal(2, geometry.Faces.Count);
    }

    [Fact]
    public void AddCurveLengths_LocationCurvePath_IsNotApproximate()
    {
        // A mullion with a decoded location curve takes the exact path (Location.Length),
        // never the bounding-box fallback, even though its category qualifies for that
        // fallback too.
        var elem = new Element
        {
            Id = 1,
            CategoryId = -2000171, // OST_CurtainWallMullions
            Location = new LocationCurve { Length = 10.0 },
            BoundingBox = [0, 0, 0, 1, 1, 100], // dominant dimension (99) deliberately
                                                 // disagrees with Location.Length (10) so a
                                                 // regression that prefers bbox would be caught
                                                 // by the length assertion below too.
        };
        var info = new RevitFileInfo();
        info.Elements.Add(elem);

        ElementBuilder.AddCurveLengths(info);

        var param = Assert.Single(elem.Parameters, p => p.Id == -1004005);
        Assert.Equal(10.0, (double)param.Value!, precision: 9);
        Assert.False(param.IsApproximate);
    }

    [Fact]
    public void AddCurveLengths_MullionBoundingBoxFallback_IsApproximate()
    {
        // No location curve: a curtain wall mullion falls back to the dominant bounding-box
        // dimension, which AddCurveLengths must flag as approximate so a consumer that
        // requires exactness (e.g. the IFC exporter's Pset_MemberCommon.Span) can tell it
        // apart from the exact Location.Length path above.
        var elem = new Element
        {
            Id = 2,
            CategoryId = -2000171, // OST_CurtainWallMullions
            BoundingBox = [0, 0, 0, 1, 1, 12], // dominant dimension = 12
        };
        var info = new RevitFileInfo();
        info.Elements.Add(elem);

        ElementBuilder.AddCurveLengths(info);

        var param = Assert.Single(elem.Parameters, p => p.Id == -1004005);
        Assert.Equal(12.0, (double)param.Value!, precision: 9);
        Assert.True(param.IsApproximate);
    }

    [Theory]
    [InlineData(1.0, true)]
    [InlineData(-1.0, true)]
    [InlineData(2.0, false)]
    public void TransformGeometry_PreservesStoredArcsThroughRigidPlacements(double axis, bool rigid)
    {
        var arc = new Arc([1, 2, 0], [1, 0, 0], [0, 1, 0], 2, 0, Math.PI / 2, Edge: 12);
        var geometry = new GeometryData
        {
            Faces = [new GeoFace { OuterVertices = [[1, 2, 0], arc.At(arc.First), arc.At(arc.Last)], MeshArcs = [arc] }],
        };
        var placed = ElementBuilder.TransformGeometry(geometry, [0, -1, 0, axis, 0, 0, 0, 0, 1, 10, 20, 3]);
        var face = Assert.Single(placed!.Faces);
        if (rigid)
        {
            var transformed = Assert.Single(face.MeshArcs!);
            Assert.Equal(arc.Edge, transformed.Edge);
            Assert.Equal(arc.Radius, transformed.Radius);
            Assert.Equal(new[] { 8, 20 + axis, 3 }, transformed.Centre);
            Assert.Equal(face.OuterVertices![1], transformed.At(transformed.First));
            Assert.Equal(face.OuterVertices[2], transformed.At(transformed.Last));

            var copied = ElementBuilder.ClipGeometryToBounds(placed, [0, 10, 2, 12, 25, 4], out bool wasClipped);
            Assert.False(wasClipped);
            Assert.Same(transformed, Assert.Single(Assert.Single(copied!.Faces).MeshArcs!));

            var clipped = ElementBuilder.ClipGeometryToBounds(placed, [7, 10, 2, 12, 25, 4], out wasClipped);
            Assert.True(wasClipped);
            Assert.Null(Assert.Single(clipped!.Faces).MeshArcs);
        }
        else Assert.Null(face.MeshArcs);
    }

    [Fact]
    public void ClipGeometryToBounds_UsesExactSerializedPlane()
    {
        var geometry = new GeometryData
        {
            Faces =
            [
                new GeoFace
                {
                    OuterVertices =
                    [
                        [0.0, 0.0, 0.0], [1.0, 0.0, 0.0],
                        [1.0, 0.0, 2.0], [0.0, 0.0, 2.0],
                    ],
                },
            ],
        };

        var clipped = ElementBuilder.ClipGeometryToBounds(geometry, [0, -1, 0, 1, 1, 1.25]);

        Assert.NotNull(clipped);
        Assert.All(clipped!.Faces.SelectMany(face => face.OuterVertices!),
            point => Assert.InRange(point[2], 0, 1.25));
        Assert.Contains(clipped.Faces.SelectMany(face => face.OuterVertices!),
            point => BitConverter.DoubleToInt64Bits(point[2]) == BitConverter.DoubleToInt64Bits(1.25));
    }

    private static ViewFacingLine ProjectedLine(double x) => new([x, 0, 0], [x + 1, 0, 0], [0, 0, 1], 7);

    private static GeometryData SquareWithLines(params ViewFacingLine[] lines) => new()
    {
        Faces = [new GeoFace { OuterVertices = [[0, 0, 0], [1, 0, 0], [1, 1, 0], [0, 1, 0]] }],
        ViewFacingLines = lines.ToList(),
    };

    [Fact]
    public void ClipGeometryToBounds_GivesTheResultItsOwnViewFacingLines()
    {
        var source = SquareWithLines(ProjectedLine(0));

        var clipped = ElementBuilder.ClipGeometryToBounds(source, [-5, -5, -5, 5, 5, 5])!;
        // Appending a symbol to the clipped geometry must not reach back into the source it was cut from.
        ElementBuilder.AppendGeometry(clipped, SquareWithLines(ProjectedLine(10)));

        Assert.NotSame(source.ViewFacingLines, clipped.ViewFacingLines);
        Assert.Equal(2, clipped.ViewFacingLines!.Count);
        Assert.Single(source.ViewFacingLines!);
    }

    [Fact]
    public void BuildGeometryData_GivesTheResultItsOwnViewFacingLines()
    {
        var grep = new GRepData { BoundingBox = [0, 0, 0, 1, 1, 0], ViewFacingLines = [ProjectedLine(0)] };

        var built = ElementBuilder.BuildGeometryData(grep)!;
        ElementBuilder.AppendGeometry(built, SquareWithLines(ProjectedLine(10)));

        Assert.NotSame(grep.ViewFacingLines, built.ViewFacingLines);
        Assert.Equal(2, built.ViewFacingLines!.Count);
        Assert.Single(grep.ViewFacingLines);
    }
}
