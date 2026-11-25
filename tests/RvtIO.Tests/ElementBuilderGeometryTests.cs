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
    public void DeduplicateExactFaces_MatchesReorderedHolesAndKeepsTheirMultiplicity()
    {
        List<double[]> outline = [[0, 0, 0], [4, 0, 0], [4, 4, 0], [0, 4, 0]];
        List<double[]> first = [[1, 1, 0], [2, 1, 0], [1, 2, 0]];
        List<double[]> second = [[2, 2, 0], [3, 2, 0], [2, 3, 0]];
        var a = new GeoFace { OuterVertices = outline, InnerVertices = [first, second] };
        var b = new GeoFace { OuterVertices = outline, InnerVertices = [second, [first[2], first[0], first[1]]] };
        var c = new GeoFace { OuterVertices = outline, InnerVertices = [first, first] };
        var geometry = new GeometryData { Faces = [a, b, c] };

        ElementBuilder.DeduplicateExactFaces(geometry);

        Assert.Equal([a, c], geometry.Faces);
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

    [Fact]
    public void ClipGeometryToBounds_CopiesAndDeduplicatesAnUncutFace()
    {
        double[] a = [0, 0, 0], b = [1, 0, 0], c = [1, 1, 1];
        var source = new GeometryData { Faces = [new GeoFace { OuterVertices = [a, a, b, c, a] }] };

        var clipped = ElementBuilder.ClipGeometryToBounds(source, [0, 0, 0, 1, 1, 1], out bool changed);

        Assert.False(changed);
        Assert.NotNull(clipped);
        var polygon = Assert.Single(clipped.Faces).OuterVertices!;
        Assert.Equal([a, b, c], polygon);
        Assert.NotSame(a, polygon[0]);
        Assert.Equal(5, source.Faces[0].OuterVertices!.Count);
    }

    [Fact]
    public void ExactFaceDeduplicationKeepsBitwiseDistinctCornersAndTheFirstEquivalentFace()
    {
        double[] a = [0, 0, 0], b = [1, 0, 0], c = [0, 1, 0];
        var first = new GeoFace { OuterVertices = [a, b, c], InnerVertices = [[a], [b, c]] };
        var equivalent = new GeoFace { OuterVertices = [b, a, c], InnerVertices = [[c, b], [a]] };
        var distinct = new GeoFace { OuterVertices = [[-0.0, 0, 0], b, c], InnerVertices = [[a], [b, c]] };
        var noHoles = new GeoFace { OuterVertices = [a, b, c] };
        var emptyHole = new GeoFace { OuterVertices = [c, a, b], InnerVertices = [[]] };
        var data = new GeometryData { Faces = [first, equivalent, distinct, noHoles, emptyHole] };

        ElementBuilder.DeduplicateExactFaces(data);

        Assert.Collection(data.Faces, face => Assert.Same(first, face),
            face => Assert.Same(distinct, face), face => Assert.Same(noHoles, face));
    }

    [Fact]
    public void PlacedSymbol_ReusesLocalFacesAcrossClippedAndUnclippedInstances()
    {
        double[] identity = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0];
        var symbol = TriangleAt(0);
        var cache = new System.Collections.Concurrent.ConcurrentDictionary<GRepData, Lazy<GeometryData?>>(ReferenceEqualityComparer.Instance);
        var state = new ParsingState { SymbolPlacementGeometry = cache };
        state.GrepBlocks[1] = symbol;
        state.GrepBlocks[2] = new GRepData
        {
            BoundingBox = [0.5, 0, 0, 1, 1, 1],
            Instances = [new GRepInstance { SymbolElemId = 1, Transform = identity }],
        };

        var info = new RevitFileInfo();
        ElementBuilder.BuildBlockOnlyElement(2, state, info);
        var clipped = Assert.IsType<GeometryData>(Assert.Single(info.Elements).Geometry);
        var clippedFace = Assert.Single(clipped.Faces);
        Assert.True(clipped.PlacedSymbolClipped);
        Assert.All(clippedFace.OuterVertices!, point => Assert.InRange(point[0], 0.5, 1));
        Assert.Equal(clippedFace.OuterVertices![0], clippedFace.Origin);
        Assert.NotSame(clippedFace.OuterVertices[0], clippedFace.Origin);
        Assert.Null(clippedFace.QuadratureArea);

        var localGeometry = Assert.IsType<GeometryData>(cache[symbol].Value);
        clippedFace.OuterVertices[0][0] = 100;
        var translated = (double[])identity.Clone();
        translated[9] = 10;
        var placed = Assert.IsType<GeometryData>(ElementBuilder.BuildPlacedSymbolGeometry(1, translated, state, []));
        var placedFace = Assert.Single(placed.Faces);

        Assert.Same(localGeometry, Assert.Single(cache).Value.Value);
        Assert.Equal(0, Assert.Single(localGeometry.Faces).OuterVertices![0][0]);
        Assert.Equal([10, 0, 0, 11, 1, 0], placed.BoundingBox!);
        Assert.Equal(7.0, placedFace.QuadratureArea);
        Assert.Equal(42, placedFace.NestedRenderStyleId);
        Assert.NotSame(placedFace.OuterVertices![0], placedFace.Origin);
    }

    [Fact]
    public void PlacedSymbol_CachesEachSelectedDetailRepresentation()
    {
        double[] identity = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0];
        var fine = TriangleAt(0);
        fine.Coarse = TriangleAt(10);
        fine.Medium = TriangleAt(20);
        fine.CoarseQuantities = TriangleAt(30);
        var cache = new System.Collections.Concurrent.ConcurrentDictionary<GRepData, Lazy<GeometryData?>>(ReferenceEqualityComparer.Instance);
        var state = new ParsingState { SymbolPlacementGeometry = cache };
        state.GrepBlocks[1] = fine;

        double Place(ElementBuilder.GeometryDetail detail = ElementBuilder.GeometryDetail.Fine) =>
            ElementBuilder.BuildPlacedSymbolGeometry(1, identity, state, [], detail: detail)!.Faces[0].OuterVertices![0][0];

        Parallel.Invoke(
            () => Assert.Equal(0, Place()),
            () => Assert.Equal(20, Place(ElementBuilder.GeometryDetail.Medium)),
            () => Assert.Equal(10, Place(ElementBuilder.GeometryDetail.Coarse)),
            () => Assert.Equal(30, Place(ElementBuilder.GeometryDetail.CoarseQuantities)));
        Assert.Equal(0, Place());
        Assert.Equal(4, cache.Count);
    }

    private static GRepData TriangleAt(double x) => new()
    {
        Faces = [new GRepFace
        {
            Vertices = [[x, 0, 0], [x + 1, 0, 0], [x, 1, 0]],
            Origin = [x, 0, 0],
            XVector = [0, 0, 1],
            QuadratureArea = 7,
            RenderStyleId = 42,
        }],
    };
}
