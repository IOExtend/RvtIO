using System.IO.Compression;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Nested components placed at a uniform scale: the six components of the ball valve of sample project P
/// (Revit 2022) sit at scale 0.0395 inside each of the five type symbols.</summary>
public class ScaledInstanceComponentTests
{
    // Complete 17,510-byte GElement of a family symbol (sample project P element 9516707, the 3/4" type): a 14-face solid that
    // is not part of the exported body plus six GInstance size variants of nested symbols 9988501-9988506, each placed
    // by a 3x3 matrix of 0.0395 * identity; only 9988502 is visible for this type. Stored as gzip.
    private const string SymbolFixture = "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";

    private static byte[] SymbolBlock()
    {
        using var input = new MemoryStream(Convert.FromBase64String(SymbolFixture));
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    [Fact]
    public void SizeVariants_KeepOnlyTheComponentOfTheType()
    {
        var block = SymbolBlock();
        Assert.Equal(17510, block.Length);

        var geometry = Assert.IsType<GRepData>(
            GRepParserV2.Parse(block, 0, BitConverter.ToUInt32(block, 8)));

        // Six GInstances of nested symbols 9988501-9988506; five carry GInfo.flags 0x000880E4 (hidden variants),
        // the 3/4" type uses 9988502 (flags 0x00088004), placed at the scale 0.0395.
        Assert.Empty(geometry.Faces);
        var instance = Assert.Single(geometry.Instances);
        Assert.Equal(9988502, instance.SymbolElemId);
        Assert.Equal(0.0395, instance.Transform[0]);
        Assert.Equal(0.0395, instance.Transform[4]);
        Assert.Equal(0.0395, instance.Transform[8]);
        // Its tight box, placed, is the tight box of the type symbol (checked on all five types of the family).
        Assert.Equal(-0.3251925602617762, geometry.BoundingBox![0], 12);
    }

    [Theory]
    [InlineData(0x000880E4u, true)]
    [InlineData(0x000000E0u, true)]
    [InlineData(0x00088004u, false)]
    [InlineData(0x00088024u, false)]   // one visibility bit: Test_2022 has ten such instances
    [InlineData(0x000880C4u, false)]
    [InlineData(0u, false)]
    public void HiddenInstanceVariant_NeedsAllThreeVisibilityBits(uint flags, bool expected)
    {
        Assert.Equal(expected, GRepParserV2.IsHiddenInstanceVariant(flags));
    }

    [Fact]
    public void ScratchGRep_ReplaysTheWinningBlockWithItsMergedInstances()
    {
        const uint owner = 9516707;
        var bytes = SymbolBlock();
        uint size = BitConverter.ToUInt32(bytes, 8);
        var backing = new PartitionChunks.Backing(new MemoryStream());
        var scratch = new PartitionChunks(backing);
        var stored = new PartitionChunks.Block(bytes, 0, bytes.Length, backing);
        var state = new ParsingState { Chunks = scratch };
        var job = new BlockParser.PendingGRep([], owner, 0, size, IsFamilyCut: false) { Stored = stored };
        var decoded = Assert.IsType<GRepData>(GRepParserV2.Parse(bytes, 0, size));
        BlockParser.ApplyPendingGRep(state, job, decoded);
        BlockParser.ApplyPendingGRep(state, job, new GRepData
        {
            Instances = [new GRepInstance { SymbolElemId = 42 }],
        });

        var restored = state.GrepBlocks[owner];
        Assert.NotSame(decoded, restored);
        Assert.Equal(decoded.BoundingBox, restored.BoundingBox);
        Assert.Equal(9988502, restored.SymbolElemId);
        Assert.Equal(decoded.InstanceTransform, restored.InstanceTransform);
        Assert.Equal(new uint[] { 9988502, 42 }, state.GrepBlocks.ReferencedSymbols());
        Assert.Equal(new[] { 9988502, 42 }, restored.Instances.Select(instance => instance.SymbolElemId));
    }

    [Theory]
    [InlineData(new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, true)]
    [InlineData(new double[] { 0, -1, 0, 1, 0, 0, 0, 0, 1 }, true)]
    [InlineData(new double[] { 0.0395, 0, 0, 0, 0.0395, 0, 0, 0, 0.0395 }, true)]
    [InlineData(new double[] { 0, 2.5, 0, -2.5, 0, 0, 0, 0, 2.5 }, true)]
    [InlineData(new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 0 }, true)]            // near-unit first two rows: accepted as before
    [InlineData(new double[] { 0.0395, 0, 0, 0, 0.04, 0, 0, 0, 0.0395 }, false)] // non-uniform scale
    [InlineData(new double[] { 0.0395, 0.0395, 0, 0, 0.0395, 0, 0, 0, 0.0395 }, false)] // shear
    [InlineData(new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    [InlineData(new double[] { double.NaN, 0, 0, 0, 1, 0, 0, 0, 1 }, false)]
    [InlineData(new double[] { 1, 0, 0 }, false)]
    public void InstanceBasis_AcceptsRigidAndUniformlyScaledPlacementsOnly(double[] basis, bool expected)
    {
        Assert.Equal(expected, GRepParserV2.IsPlausibleInstanceBasis(basis));
    }

    private static readonly double[] Identity = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0];

    private static double SignedVolume(GeometryData geometry)
    {
        double volume = 0;
        foreach (var face in geometry.Faces)
        {
            var ring = face.OuterVertices!;
            for (int index = 1; index + 1 < ring.Count; index++)
            {
                double[] a = ring[0], b = ring[index], c = ring[index + 1];
                volume += (a[0] * (b[1] * c[2] - b[2] * c[1]) - a[1] * (b[0] * c[2] - b[2] * c[0])
                    + a[2] * (b[0] * c[1] - b[1] * c[0])) / 6;
            }
        }
        return volume;
    }

    [Fact]
    public void PlacedBox_IsAClosedOutwardSolidAtTheComponentScale()
    {
        double[] scaled = [0.5, 0, 0, 0, 0.5, 0, 0, 0, 0.5, 10, 20, 30];
        var box = Assert.IsType<GeometryData>(
            ElementBuilder.BuildPlacedBox([-2, -1, 0, 2, 3, 4], scaled));

        Assert.Equal(6, box.Faces.Count);
        var solid = Assert.Single(box.BrepSolids!);
        Assert.True(solid.IsClosed);
        Assert.Equal(6, solid.FaceIndices.Count);
        Assert.Equal(4 * 4 * 4 * 0.125, SignedVolume(box), 12);   // 4 x 4 x 4 box at scale 0.5
        Assert.Equal([9, 19.5, 30, 11, 21.5, 32], box.BoundingBox!);
        Assert.All(box.Faces, face => Assert.Equal(1, Math.Sqrt(
            face.Normal.Sum(value => value * value)), 12));
    }

    [Fact]
    public void PlacedBox_KeepsOutwardOrientationForAMirroredPlacement()
    {
        double[] mirrored = [-1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0];
        var box = Assert.IsType<GeometryData>(ElementBuilder.BuildPlacedBox([0, 0, 0, 1, 2, 3], mirrored));

        Assert.Equal(6.0, SignedVolume(box), 12);
    }

    [Theory]
    [InlineData(new double[] { 0, 0, 0, 0, 1, 1 })]            // zero extent
    [InlineData(new double[] { 0, 0, 0, double.NaN, 1, 1 })]
    [InlineData(new double[] { 0, 0, 0 })]
    public void PlacedBox_RejectsMissingOrDegenerateBounds(double[] bounds)
    {
        Assert.Null(ElementBuilder.BuildPlacedBox(bounds, Identity));
    }

    [Theory]
    [InlineData(new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0 }, false)]
    [InlineData(new double[] { 1.2, 0, 0, 0, 1.2, 0, 0, 0, 1.2, 0, 0, 0 }, false)] // accepted before: stays as it was
    [InlineData(new double[] { 0.0395, 0, 0, 0, 0.0395, 0, 0, 0, 0.0395, 0, 0, 0 }, true)]
    [InlineData(new double[] { 3, 0, 0, 0, 3, 0, 0, 0, 3, 0, 0, 0 }, true)]
    public void RescaledPlacement_IsOnlyWhatWasRejectedBefore(double[] transform, bool expected)
    {
        Assert.Equal(expected, ElementBuilder.IsRescaledPlacement(transform));
    }

    private static GRepData Component(int faceCount, double[] box) => new()
    {
        BoundingBox = box,
        Faces = Enumerable.Range(0, faceCount).Select(index => new GRepFace
        {
            Vertices = [[index, 0, 0], [index + 1, 0, 0], [index, 1, 0]],
            Origin = [index, 0, 0],
            XVector = [0, 0, 1],
        }).ToList(),
    };

    private static ParsingState StateWithSymbol(GRepData component, double[] placement)
    {
        var state = new ParsingState();
        state.GrepBlocks[1] = new GRepData
        {
            Instances = [new GRepInstance { SymbolElemId = 2, Transform = placement }],
        };
        state.GrepBlocks[2] = component;
        return state;
    }

    [Fact]
    public void OversizeScaledComponent_IsPlacedAsItsTightBox()
    {
        double[] scaled = [0.5, 0, 0, 0, 0.5, 0, 0, 0, 0.5, 1, 0, 0];
        var state = StateWithSymbol(Component(30, [0, 0, 0, 4, 2, 2]), scaled);

        var placed = ElementBuilder.BuildPlacedSymbolGeometry(
            1, Identity, state, new HashSet<int>(), scaledComponentFaceBudget: 10);

        Assert.NotNull(placed);
        Assert.Equal(6, placed.Faces.Count);
        Assert.Equal([1, 0, 0, 3, 1, 1], placed.BoundingBox!);
    }

    [Fact]
    public void OversizeScaledComponent_KeepsItsMostFrequentFaceMaterialAndGraphicsStyle()
    {
        // The box stands for the component, so it takes the component's dominant material (ball valve: "Default")
        // and graphics style; the smaller id wins a tie.
        var component = Component(30, [0, 0, 0, 4, 2, 2]);
        var faces = component.Faces.Select((face, index) => face with
        {
            RenderStyleId = index < 12 ? 400 : index < 24 ? 500 : null,
            GeometryStyle = index < 20 ? 90 : index < 25 ? 80 : -1,
        }).ToList();
        component.Faces = faces;
        double[] scaled = [0.5, 0, 0, 0, 0.5, 0, 0, 0, 0.5, 1, 0, 0];
        var placed = ElementBuilder.BuildPlacedSymbolGeometry(
            1, Identity, StateWithSymbol(component, scaled), new HashSet<int>(), scaledComponentFaceBudget: 10);

        Assert.NotNull(placed);
        Assert.All(placed.Faces, face =>
        {
            Assert.Equal(400, face.NestedRenderStyleId);   // 12 faces each of 400 and 500: the smaller id
            Assert.Null(face.RenderStyleId);
            Assert.Equal(90, face.GeometryStyle);
        });
    }

    [Fact]
    public void OversizeScaledComponentWithoutMaterials_StaysWithoutThem()
    {
        double[] scaled = [0.5, 0, 0, 0, 0.5, 0, 0, 0, 0.5, 1, 0, 0];
        var placed = ElementBuilder.BuildPlacedSymbolGeometry(
            1, Identity, StateWithSymbol(Component(30, [0, 0, 0, 4, 2, 2]), scaled), new HashSet<int>(), scaledComponentFaceBudget: 10);

        Assert.NotNull(placed);
        Assert.All(placed.Faces, face =>
        {
            Assert.Null(face.NestedRenderStyleId);
            Assert.Equal(-1, face.GeometryStyle);
        });
    }

    [Fact]
    public void ScaledComponentWithinBudget_IsExpandedFace_ByFace()
    {
        double[] scaled = [0.5, 0, 0, 0, 0.5, 0, 0, 0, 0.5, 1, 0, 0];
        var state = StateWithSymbol(Component(30, [0, 0, 0, 4, 2, 2]), scaled);

        var placed = ElementBuilder.BuildPlacedSymbolGeometry(
            1, Identity, state, new HashSet<int>(), scaledComponentFaceBudget: 30);

        Assert.NotNull(placed);
        Assert.Equal(30, placed.Faces.Count);
    }

    [Fact]
    public void OversizeComponentAtUnitScale_IsStillExpandedFace_ByFace()
    {
        var state = StateWithSymbol(Component(30, [0, 0, 0, 4, 2, 2]), Identity);

        var placed = ElementBuilder.BuildPlacedSymbolGeometry(
            1, Identity, state, new HashSet<int>(), scaledComponentFaceBudget: 10);

        Assert.NotNull(placed);
        Assert.Equal(30, placed.Faces.Count);
    }
}
