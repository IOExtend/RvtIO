using RvtIO.Geometry;
using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class NonVisibleQuantityGeometryTests
{
    [Fact]
    public void QuantityView_IsDecodedOnlyOnDemandAndCached()
    {
        int reads = 0;
        var block = new GRepData();
        var quantities = new GRepData();
        block.SetCoarseQuantityFactory(() => { reads++; return quantities; });

        Assert.Equal(0, reads);
        Assert.Same(quantities, block.CoarseQuantities);
        Assert.Same(quantities, block.CoarseQuantities);
        Assert.Equal(1, reads);
    }

    private static GRepFace Quad(double z) => new()
    {
        Vertices = [[0, 0, z], [1, 0, z], [1, 1, z], [0, 1, z]],
        Origin = [0, 0, z], XVector = [0, 0, 1],
    };

    [Theory]
    [InlineData(0x000880e4u, true)]
    [InlineData(0x400880e4u, false)]
    [InlineData(0x00088004u, false)]
    public void QuantityView_IncludesHiddenBodiesButRejectsSuppressedAndIntermediateStates(uint flags, bool expected)
    {
        Assert.False(GRepParserV2.IsPhysicalBodyGeometry(flags));
        Assert.Equal(expected, GRepParserV2.IsPhysicalBodyGeometry(flags, includeNonVisible: true));
    }

    [Fact]
    public void QuantityView_ActivatesHiddenNodesWithoutChangingDefaultVisibility()
    {
        (ushort, int)[] queue = [(2092, 10), (1589, 11)];
        (int, string)[] links = [(-1, "m_subNodes"), (0, "m_pFaces")];
        var flags = new Dictionary<int, (int, int, int, uint)>
        {
            [10] = (-1, 0, -1, 0x880e4), [11] = (0, 0, -1, 0x88204),
        };
        Assert.Equal(-1, GRepParserV2.ResolveGeometryNodes(queue, links, flags)[11].Node);
        Assert.Equal(1, GRepParserV2.ResolveGeometryNodes(queue, links, flags, includeNonVisible: true)[11].Node);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DoorArea_UsesQuantityViewForOwnOrNestedBodiesAndKeepsVisibleGeometry(bool nested)
    {
        var visible = new GRepData { Faces = [Quad(0)] };
        visible.Coarse = new GRepData { Faces = [Quad(0), Quad(1)] };
        visible.CoarseQuantities = new GRepData { IsOpenSurface = true, Faces = [Quad(0), Quad(1), Quad(2)] };
        var state = new ParsingState();
        state.GrepBlocks[20] = visible;
        state.GrepBlocks[10] = nested ? new GRepData
        {
            BoundingBox = [4, 5, 6, 5, 6, 6.5],
            Instances = [new GRepInstance { SymbolElemId = 20, Transform = [1, 0, 0, 0, 1, 0, 0, 0, 1, 4, 5, 6] }],
        } : visible;
        var body = ElementBuilder.BuildGeometryData(visible);
        var door = new Element { Id = 10, CategoryId = -2000023, Geometry = body };
        var info = new RevitFileInfo();
        info.Elements.Add(door);

        ElementBuilder.ApplyCoarseDoorWindowSurfaces(info, state);

        Assert.Equal(3, door.CoarseSurfaceArea!.Value, 12);
        Assert.Same(body, door.Geometry);
        Assert.Single(door.Geometry!.Faces);
        Assert.False(state.CoarseView);
        Assert.False(state.IncludeNonVisibleQuantities);
    }

    [Fact]
    public void SurfaceArea_CountsSourceSurfaceOnceAcrossFacetsAndSeparatelyAcrossNodes()
    {
        var geometry = new GeometryData
        {
            Faces =
            [
                new GeoFace { SourceFace = 7, GeometryNode = 1, QuadratureArea = 2, OuterVertices = Quad(0).Vertices },
                new GeoFace { SourceFace = 7, GeometryNode = 1, QuadratureArea = 2, OuterVertices = Quad(1).Vertices },
                new GeoFace { SourceFace = 7, GeometryNode = 2, QuadratureArea = 3, OuterVertices = Quad(2).Vertices },
                new GeoFace { OuterVertices = Quad(3).Vertices },
            ],
        };

        Assert.Equal(6, BrepMeasures.SurfaceAreaCurrent(geometry), 12);
    }
}
