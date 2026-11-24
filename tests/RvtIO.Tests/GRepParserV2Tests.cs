using RvtIO.Parsing;
using RvtIO.Internal;
using Xunit;

namespace RvtIO.Tests;

public class GRepParserV2Tests
{
    [Fact]
    public void ThinSamples_KeepsShortListsAndThinsLongOnesKeepingTheEndsAndKnots()
    {
        var short1 = new List<double> { 0, 1, 2 };
        Assert.Same(short1, GRepParserV2.ThinSamples(short1, [], 5));

        var many = Enumerable.Range(0, 1000).Select(i => (double)i).ToList();
        var thinned = GRepParserV2.ThinSamples(many, [250.0, 700.0, -5.0, 2000.0], 20);
        Assert.InRange(thinned.Count, 4, 20);
        Assert.Equal(0, thinned[0]);
        Assert.Equal(999, thinned[^1]);
        Assert.Contains(250.0, thinned);
        Assert.Contains(700.0, thinned);
        Assert.DoesNotContain(-5.0, thinned);
        Assert.Equal(thinned.OrderBy(x => x), thinned);
        Assert.Equal(thinned.Distinct().Count(), thinned.Count);
    }

    [Fact]
    public void FamilyComplexCutter_AcceptsClosedBranchWithoutDummyHostPlane()
    {
        var cutter = new GRepData
        {
            IsFamilyComplexCut = true,
            Faces = [new GRepFace()],
            Solids = [new GRepSolid { IsClosed = true, FaceIndices = [0] }],
        };

        Assert.True(BlockParser.IsAuthoredFamilyComplexCutter(cutter));

        cutter.IsFamilyComplexCut = false;
        Assert.False(BlockParser.IsAuthoredFamilyComplexCutter(cutter));
    }

    [Fact]
    public void FamilyComplexCutter_RequiresClosurePlaneWhenBranchIsOpen()
    {
        var cutter = new GRepData
        {
            IsFamilyComplexCut = true,
            Faces = [new GRepFace()],
            Solids = [new GRepSolid { IsClosed = false, FaceIndices = [0] }],
        };

        Assert.False(BlockParser.IsAuthoredFamilyComplexCutter(cutter));

        cutter.FamilyClosurePlaneOrigin = [0, 0.5, 0];
        cutter.FamilyClosurePlaneNormal = [0, 1, 0];
        Assert.True(BlockParser.IsAuthoredFamilyComplexCutter(cutter));
    }

    [Fact]
    public void ExplicitMappedRepresentation_WinsWithoutMixingSeparateResolvedFaces()
    {
        var resolved = new GRepData();
        resolved.Faces.Add(new GRepFace());
        var mapped = new GRepData();
        mapped.Instances.Add(new GRepInstance
        {
            SymbolElemId = 42,
            Transform = [1, 0, 0, 0, 1, 0, 0, 0, 1, 10, 20, 30],
        });

        foreach (bool mappedFirst in new[] { false, true })
        {
            var state = new ParsingState();
            var job = new BlockParser.PendingGRep([], 7, 0, 0, IsFamilyCut: false);
            BlockParser.ApplyPendingGRep(state, job, mappedFirst ? mapped : resolved);
            BlockParser.ApplyPendingGRep(state, job, mappedFirst ? resolved : mapped);
            Assert.Same(mapped, state.GrepBlocks[7]);
            Assert.Empty(state.GrepBlocks[7].Faces);
        }
    }

    [Theory]
    [InlineData(0x0008C004u, true)]
    [InlineData(0x0008C0E4u, true)]
    [InlineData(0x00088004u, false)]
    [InlineData(0x4008C004u, false)]
    public void GeometryGInfo_RequiresModelControlAndUnsuppressedState(
        uint flags, bool expected)
    {
        Assert.Equal(expected, GRepParserV2.IsPhysicalBodyGeometry(flags));
    }

    [Fact]
    public void GraphicalGeometryState_IsExcludedFromViewDependentBranch()
    {
        var directionOnly = new GRepParserV2.SerializedModelCondition(
            1996, false, 0, null, null);
        var detailLevel = new GRepParserV2.SerializedModelCondition(
            1999, null, 3, 3, 3);

        Assert.False(GRepParserV2.IsPhysicalBodyGeometryState(
            true, false, [directionOnly]));
        Assert.True(GRepParserV2.IsPhysicalBodyGeometryState(
            false, false, [directionOnly]));
        Assert.False(GRepParserV2.IsPhysicalBodyGeometryState(
            false, true, [directionOnly]));
        Assert.False(GRepParserV2.IsPhysicalBodyGeometryState(
            true, false, [directionOnly, detailLevel]));
        Assert.True(GRepParserV2.IsPhysicalBodyGeometryState(
            false, false, [detailLevel]));
    }

    [Fact]
    public void GeometryNodes_OwnTheirFaces_AndANodeReferenceDoesNotTakeIsNegated()
    {
        // queue: 0 GFilter, 1 Geometry (style 36) with faces 2 and 3, 4 Geometry (style 38, visibility bits 0xE0)
        // with face 5, 6 Geometry whose only face 7 carries visibility bits, 8 Face under no Geometry
        (ushort Cls, int V)[] queue =
        [
            (2015, 3), (2092, 4), (1589, 10), (1589, 11), (2092, 5), (1589, 12), (2092, 6), (1589, 13), (1589, 14),
        ];
        (int, string)[] links =
        [
            (-1, "m_subNodes"), (0, "m_subNodes"), (1, "m_pFaces"), (1, "m_pFaces"), (0, "m_subNodes"), (4, "m_pFaces"),
            (0, "m_subNodes"), (6, "m_pFaces"), (0, "m_pFaces"),
        ];
        var gInfo = new Dictionary<int, (int Tag, int Control, int Category, uint Flags)>
        {
            [4] = (-1, 0, 36, 0x8C004), [5] = (-1, 0, 38, 0x8C0E4), [6] = (-1, 0, -1, 0x8C004),
            [10] = (0, 0, -1, 0x88204), [11] = (0, 0, -1, 0x8C204), [12] = (0, 0, -1, 0x88204),
            [13] = (0, 0, -1, 0x882E4), [14] = (0, 0, -1, 0x88204),
        };

        var nodes = GRepParserV2.ResolveGeometryNodes(queue, links, gInfo);

        Assert.Equal((2, 36), nodes[10]);      // both faces of the first node, in one node: queue index 1 + 1
        Assert.Equal((2, 36), nodes[11]);
        Assert.Equal((-5, 38), nodes[12]);     // the node itself has visibility bits
        Assert.Equal((-7, -1), nodes[13]);     // no face of the node is Visible
        Assert.False(nodes.ContainsKey(14));   // a face under no Geometry knows no node
    }

    [Fact]
    public void NodeOrders_WalkTheNodesDepthFirstInTheOrderOfTheChildren_NotInTheOrderOfTheQueue()
    {
        // The queue lists the nodes breadth first: 0 and 1 are the filters A and B, 2 a Geometry under B, 3 and 4
        // Geometry nodes under A, 5 a GInstance under B, 6 a Face (no node). The expected export walks A (3, 4), then B (2, 5).
        (ushort Cls, int V)[] queue = [(2015, 3), (2015, 4), (2092, 5), (2092, 6), (2092, 7), (1976, 8), (1589, 9)];
        (int, string)[] links =
        [
            (-1, "m_subNodes"), (-1, "m_subNodes"), (1, "m_subNodes"), (0, "m_subNodes"), (0, "m_subNodes"),
            (1, "m_subNodes"), (2, "m_pFaces"),
        ];

        var orders = GRepParserV2.ComputeNodeOrders(queue, links, [5]);

        Assert.Equal(4, orders.Count);   // the filter and the face are no slots
        Assert.True(orders[3] < orders[4] && orders[4] < orders[2] && orders[2] < orders[5]);
        Assert.Equal(1L << GRepParserV2.OrderTopShift, orders[3]);
        Assert.False(orders.ContainsKey(1));
    }

    [Fact]
    public void NodeOrders_AGInstanceOwnerIsASlotOnlyWhenItIsListed()
    {
        (ushort Cls, int V)[] queue = [(2015, 3), (1976, 4), (2092, 5)];
        (int, string)[] links = [(-1, "m_subNodes"), (0, "m_subNodes"), (0, "m_subNodes")];

        Assert.Equal([2], GRepParserV2.ComputeNodeOrders(queue, links, []).Keys);
        Assert.Equal([1, 2], GRepParserV2.ComputeNodeOrders(queue, links, [1]).Keys.Order());
    }

    [Fact]
    public void NestedOrderKey_SortsTheSymbolNodesAtTheGInstanceBetweenItsNeighbours()
    {
        long Key(long rank) => rank << GRepParserV2.OrderTopShift;
        long before = Key(3), instance = Key(4), after = Key(5);
        long first = GRepParserV2.NestOrderKey(instance, Key(1)), second = GRepParserV2.NestOrderKey(instance, Key(2));

        Assert.True(before < instance && instance < first && first < second && second < after);
        // a symbol nested twice (at the seventh node of the symbol) stays inside the same neighbours, after its siblings
        long deep = GRepParserV2.NestOrderKey(instance, GRepParserV2.NestOrderKey(Key(7), Key(1)));
        Assert.True(second < deep && deep < after);
    }

    [Fact]
    public void UncutModelBranch_RejectsPositiveOrUndecodedCutAlternative()
    {
        Assert.True(GRepParserV2.AllowsFineUncutModelBranch(
        [
            new(1995, true, null, null, null),
            new(1996, false, null, null, null),
        ]));
        Assert.False(GRepParserV2.AllowsFineUncutModelBranch(
            [new(1995, false, null, null, null)]));
        Assert.False(GRepParserV2.AllowsFineUncutModelBranch(
            [new(1995, null, null, null, null)]));
    }

    [Theory]
    [InlineData(3, 3, true)]
    [InlineData(3, 2, false)]
    [InlineData(0, 3, false)]
    [InlineData(0, 2, true)]
    public void FineModelBranch_EvaluatesSerializedDetailCondition(
        int comparison, int value, bool expected)
    {
        Assert.Equal(expected, GRepParserV2.AllowsFineUncutModelBranch(
            [new(1999, null, comparison, 3, value)]));
    }

    [Theory]
    [InlineData(-1, 3, true)]
    [InlineData(1, 4, true)]
    [InlineData(2, 3, true)]
    [InlineData(4, 3, true)]
    [InlineData(5, 2, true)]
    [InlineData(99, 2, true)]
    public void SerializedIntegerComparison_MatchesRuntimeContract(
        int comparison, int expected, bool result)
    {
        Assert.Equal(result,
            GRepParserV2.CompareSerializedInteger(3, comparison, expected));
    }

    [Fact]
    public void ExternalGInstance_RequiresSymbolExpansion()
    {
        Assert.True(GRepParserV2.ShouldAttachSymbolGeometry(1976));
    }

    [Fact]
    public void ComponentReference_IsNotParentGeometry()
    {
        Assert.False(GRepParserV2.ShouldAttachSymbolGeometry(1991));
    }

    [Theory]
    [InlineData(true, 1, -1, false)]
    [InlineData(false, 1, -1, true)]
    [InlineData(true, 2, -1, true)]
    [InlineData(true, 1, 0, true)]
    public void SoleRootMappedSource_IsNotExpandedBesideCompleteResolvedBody(
        bool hasCompleteResolvedBody, int rootMappedInstanceCount,
        int parentQueueIndex, bool expected)
    {
        Assert.Equal(expected, GRepParserV2.ShouldAttachMappedInstance(
            hasCompleteResolvedBody, rootMappedInstanceCount,
            parentQueueIndex));
    }

    [Fact]
    public void SerializedBounds_AcceptsUvReconstructionRoundoff()
    {
        double oneUlpPastMaximum = Math.BitIncrement(42.0);
        var face = new GRepFace
        {
            Vertices =
            [
                [0.0, 0.0, 0.0],
                [oneUlpPastMaximum, 0.0, 0.0],
                [0.0, 1.0, 0.0],
            ],
        };

        Assert.True(GRepParserV2.IsWithinSerializedBounds(
            face, [0.0, 0.0, 0.0, 42.0, 1.0, 1.0]));
    }

    [Fact]
    public void SerializedBounds_RejectsRealOutOfBoundsGeometry()
    {
        var face = new GRepFace
        {
            Vertices =
            [
                [0.0, 0.0, 0.0],
                [42.001, 0.0, 0.0],
                [0.0, 1.0, 0.0],
            ],
        };

        Assert.False(GRepParserV2.IsWithinSerializedBounds(
            face, [0.0, 0.0, 0.0, 42.0, 1.0, 1.0]));
    }

    [Fact]
    public void SerializedBounds_ClipsCrossingPolygon()
    {
        var face = new GRepFace
        {
            Vertices =
            [
                [0.0, 0.0, 0.0],
                [2.0, 0.0, 0.0],
                [2.0, 1.0, 0.0],
                [0.0, 1.0, 0.0],
            ],
        };

        var clipped = GRepParserV2.ClipToSerializedBounds(
            face, [0.0, 0.0, -1.0, 1.0, 1.0, 1.0]);

        Assert.NotNull(clipped);
        Assert.Equal(4, clipped.Vertices!.Count);
        Assert.All(clipped.Vertices, point => Assert.InRange(point[0], 0.0, 1.0));
        Assert.Contains(clipped.Vertices, point => point[0] == 1.0);
    }

    [Fact]
    public void SerializedBounds_DropsWhollyOutsidePolygon()
    {
        var face = new GRepFace
        {
            Vertices =
            [
                [2.0, 0.0, 0.0],
                [3.0, 0.0, 0.0],
                [3.0, 1.0, 0.0],
            ],
        };

        Assert.Null(GRepParserV2.ClipToSerializedBounds(
            face, [0.0, 0.0, -1.0, 1.0, 1.0, 1.0]));
    }

    [Fact]
    public void PeriodicSurfaceEdges_CloseAcrossEquivalentSeamParameters()
    {
        double negativeSeam = -Math.PI;
        double positiveSeam = Math.PI;
        var edges = new List<(double U1, double V1, double U2, double V2)>
        {
            (negativeSeam, 0, negativeSeam, 1),
            (positiveSeam, 1, 0, 1),
            (0, 1, 0, 0),
            (0, 0, positiveSeam, 0),
        };

        var normalized = GRepParserV2.NormalizePeriodicEdges(edges, 2 * Math.PI);
        var rings = GRepParserV2.OrderRingsW(normalized.Select(edge =>
            (new[] { edge.U1, edge.V1, 0d }, new[] { edge.U2, edge.V2, 0d })).ToList());

        var ring = Assert.Single(rings);
        Assert.Equal(4, ring.Count);
        Assert.Equal(Math.PI, ring.Max(point => point[0]) - ring.Min(point => point[0]), 12);
    }

    [Fact]
    public void RevolutionTrim_ClosesAtSerializedZeroRadiusPole()
    {
        var profile = new GRepParserV2.ArcRec
        {
            HermiteNodes =
            [
                new GRepParserV2.HermiteNode
                {
                    T = 0,
                    Point = [0, 0, 0],
                    Tangent = [1, 0, 1],
                },
                new GRepParserV2.HermiteNode
                {
                    T = 1,
                    Point = [1, 0, 1],
                    Tangent = [1, 0, 1],
                },
            ],
        };
        var open = new List<(double U1, double V1, double U2, double V2)>
        {
            (0, 0, 0, 1),
            (0, 1, Math.PI, 1),
            (Math.PI, 1, Math.PI, 0),
        };

        var closed = GRepParserV2.CloseRevolutionPoleEdges(open, profile);
        var rings = GRepParserV2.OrderRingsW(closed.Select(edge =>
            (new[] { edge.U1, edge.V1, 0d }, new[] { edge.U2, edge.V2, 0d })).ToList());

        Assert.Equal(4, closed.Count);
        Assert.Single(rings);
    }

    [Fact]
    public void TrimTriangulation_PreservesExactInnerLoop()
    {
        var triangles = GRepParserV2.TriangulateTrimRings(
        [
            [[0, 0], [4, 0], [4, 4], [0, 4]],
            [[1, 1], [1, 3], [3, 3], [3, 1]],
        ]);

        Assert.NotNull(triangles);
        Assert.Equal(8, triangles.Count);
        double area = triangles.Sum(triangle => Math.Abs(
            triangle[0][0] * (triangle[1][1] - triangle[2][1])
            + triangle[1][0] * (triangle[2][1] - triangle[0][1])
            + triangle[2][0] * (triangle[0][1] - triangle[1][1])) / 2);
        Assert.Equal(12, area, 12);
        Assert.All(triangles, triangle =>
        {
            double x = triangle.Average(point => point[0]);
            double y = triangle.Average(point => point[1]);
            Assert.False(x > 1 && x < 3 && y > 1 && y < 3);
        });
    }

    [Fact]
    public void TrimTriangulation_PreservesDisconnectedRegionsOfOneFace()
    {
        var triangles = GRepParserV2.TriangulateTrimRegions(
        [
            [[0, 0], [1, 0], [1, 1], [0, 1]],
            [[3, 0], [4, 0], [4, 1], [3, 1]],
        ]);

        Assert.NotNull(triangles);
        Assert.Equal(4, triangles.Count);
        double area = triangles.Sum(triangle => Math.Abs(
            triangle[0][0] * (triangle[1][1] - triangle[2][1])
            + triangle[1][0] * (triangle[2][1] - triangle[0][1])
            + triangle[2][0] * (triangle[0][1] - triangle[1][1])) / 2);
        Assert.Equal(2, area, 12);
        Assert.All(triangles, triangle =>
            Assert.True(triangle.All(point => point[0] <= 1)
                || triangle.All(point => point[0] >= 3)));
    }

    [Fact]
    public void TrimTriangulation_AssignsHolesToTheirContainingRegion()
    {
        var triangles = GRepParserV2.TriangulateTrimRegions(
        [
            [[0, 0], [4, 0], [4, 4], [0, 4]],
            [[1, 1], [1, 3], [3, 3], [3, 1]],
            [[6, 0], [7, 0], [7, 1], [6, 1]],
        ]);

        Assert.NotNull(triangles);
        double area = triangles.Sum(triangle => Math.Abs(
            triangle[0][0] * (triangle[1][1] - triangle[2][1])
            + triangle[1][0] * (triangle[2][1] - triangle[0][1])
            + triangle[2][0] * (triangle[0][1] - triangle[1][1])) / 2);
        Assert.Equal(13, area, 12);
        Assert.Contains(triangles, triangle => triangle.All(point => point[0] >= 6));
    }

    [Fact]
    public void TrimTriangulation_PreservesCurvedSurfaceSamplesAfterUvCollinearCleanup()
    {
        var outer = Enumerable.Range(0, 13)
            .Select(index => new[] { index * Math.PI / 12, 1d }).Concat(
                Enumerable.Range(0, 13).Reverse()
                    .Select(index => new[] { index * Math.PI / 12, 0d }))
            .ToList();
        var hole = Enumerable.Range(0, 18).Select(index =>
        {
            double angle = 2 * Math.PI * index / 18;
            return new[] { Math.PI / 2 + Math.Cos(angle), 0.5 + 0.25 * Math.Sin(angle) };
        }).ToList();

        var triangles = GRepParserV2.TriangulateTrimRings([outer, hole]);

        Assert.NotNull(triangles);
        Assert.Equal(22, triangles.Count);
        foreach (var point in outer.Concat(hole))
            Assert.Contains(triangles.SelectMany(polygon => polygon), candidate =>
                candidate[0] == point[0] && candidate[1] == point[1]);
    }

    [Fact]
    public void TrimTriangulation_KeepsTheTrianglesAndOrderOfTheExhaustiveSearch()
    {
        // Integer samples tie bridge distances and put collinear samples on every outer edge; the expected polygons are those
        // of the earlier triangulation, which tested every candidate and rescanned the ring for every ear.
        var outer = new List<double[]>();
        for (int x = 0; x < 4; x++) outer.Add([x, 0]);
        for (int y = 0; y < 4; y++) outer.Add([4, y]);
        for (int x = 4; x > 0; x--) outer.Add([x, 4]);
        for (int y = 4; y > 0; y--) outer.Add([0, y]);

        var polygons = GRepParserV2.TriangulateTrimRings(
        [
            outer,
            [[1, 1], [1, 2], [2, 2], [2, 1]],
            [[2.5, 2.5], [2.5, 3], [3, 3], [3, 2.5]],
        ]);

        Assert.NotNull(polygons);
        string[] expected =
        [
            "(4, 0) (4, 1) (4, 2) (4, 3) (4, 4) (3, 2.5)",
            "(4, 0) (3, 2.5) (2.5, 2.5)",
            "(4, 0) (2.5, 2.5) (2, 1)",
            "(0, 0) (1, 0) (2, 0) (3, 0) (4, 0) (2, 1)",
            "(0, 0) (2, 1) (1, 1)",
            "(0, 4) (0, 3) (0, 2) (0, 1) (0, 0) (1, 1)",
            "(0, 4) (1, 1) (1, 2)",
            "(0, 4) (1, 2) (2, 2)",
            "(2, 2) (2, 1) (2.5, 2.5)",
            "(0, 4) (2, 2) (2.5, 2.5)",
            "(0, 4) (2.5, 2.5) (2.5, 3)",
            "(0, 4) (2.5, 3) (3, 3)",
            "(3, 3) (3, 2.5) (4, 4)",
            "(3, 3) (4, 4) (3, 4) (2, 4) (1, 4) (0, 4)",
        ];
        Assert.Equal(expected, polygons.Select(polygon => string.Join(" ", polygon.Select(point =>
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"({point[0]}, {point[1]})")))).ToArray());
    }

    // The pattern of a curved face of a private model whose triangulation ran for many minutes: a densely sampled curved
    // outer loop of 1,930 vertices around 20 circular holes of 210 to 258 vertices. The bridges to the holes tested every
    // polygon vertex against every edge, and ear clipping every vertex for every ear.
    private static List<List<double[]>> PerforatedTrimRings()
    {
        const int side = 965;
        var outer = new List<double[]>();
        for (int index = 0; index < side; index++)
            outer.Add([20.0 * index / side, 0.3 * Math.Sin(2.0 * index / side)]);
        for (int index = side; index > 0; index--)
            outer.Add([20.0 * index / side, 4 + 0.3 * Math.Cos(3.0 * index / side)]);
        var rings = new List<List<double[]>> { outer };
        for (int hole = 0; hole < 20; hole++)
        {
            int count = 210 + 2 * hole;
            rings.Add(Enumerable.Range(0, count).Select(index =>
            {
                double angle = -2 * Math.PI * index / count;
                return new[] { 0.5 + hole + 0.4 * Math.Cos(angle), 2 + 0.4 * Math.Sin(angle) };
            }).ToList());
        }
        return rings;
    }

    [Fact]
    public async Task TrimTriangulation_TriangulatesManyFinelySampledHolesInNearLinearWork()
    {
        var rings = PerforatedTrimRings();

        // A regression runs for minutes: fail instead of hanging the test run.
        var triangulation = Task.Run(() => GRepParserV2.TriangulateTrimRegions(rings));
        Assert.True(await Task.WhenAny(triangulation, Task.Delay(TimeSpan.FromSeconds(60))) == triangulation,
            "the trim triangulation did not finish in 60 s");
        var polygons = await triangulation;

        // Not null: the triangulation stayed within the default budget.
        Assert.NotNull(polygons);
        double expected = PolygonArea(rings[0]) - rings.Skip(1).Sum(PolygonArea);
        Assert.Equal(expected, polygons.Sum(PolygonArea), 9);
        var vertices = polygons.SelectMany(polygon => polygon).Select(point => (point[0], point[1])).ToHashSet();
        Assert.All(rings.SelectMany(ring => ring), point => Assert.Contains((point[0], point[1]), vertices));
    }

    [Fact]
    public void TrimTriangulation_ClipsARingWhoseVertexCountSquaredExceedsInt32()
    {
        // The parabola and its closing chord form a convex ring with no collinear vertices to simplify.
        const int count = 46_341;
        var ring = Enumerable.Range(0, count).Select(index => new[] { (double)index, (double)index * index }).ToList();

        var polygons = GRepParserV2.TriangulateTrimRegions([ring], out bool outOfBudget);

        Assert.False(outOfBudget);
        Assert.NotNull(polygons);
        Assert.Equal(count - 2, polygons.Count);
        Assert.Equal(PolygonArea(ring), polygons.Sum(PolygonArea));
    }

    [Fact]
    public void TrimTriangulation_TellsARunOutBudgetApartFromLoopsThatDoNotTriangulate()
    {
        var rings = PerforatedTrimRings();

        Assert.Null(GRepParserV2.TriangulateTrimRegions(rings, out bool outOfBudget, new GRepParserV2.TrimWorkBudget(100_000)));
        Assert.True(outOfBudget);
        Assert.NotNull(GRepParserV2.TriangulateTrimRegions(rings, out outOfBudget));
        Assert.False(outOfBudget);
        // A hole that crosses its outer loop does not triangulate, whatever the budget.
        Assert.Null(GRepParserV2.TriangulateTrimRegions(
        [
            [[0, 0], [4, 0], [4, 4], [0, 4]],
            [[3, 1], [5, 1], [5, 3], [3, 3]],
        ], out outOfBudget));
        Assert.False(outOfBudget);
    }

    [Fact]
    public async Task TrimTriangulation_KeepsTheBoundaryAndHolesOfADenseComb()
    {
        // A comb of 2,400 tall notches around one small hole, 9,608 vertices: every tooth crosses the winding test of every
        // ear, and the ears fan out from the base. Before the indexes the default budget ran out on it.
        const int teeth = 2400;
        List<double[]> outer = [[0, 0], [2 * teeth, 0], [2 * teeth, 10]];
        for (int tooth = teeth - 1; tooth >= 0; tooth--)
        {
            outer.Add([2 * tooth + 1.5, 10]);
            outer.Add([2 * tooth + 1.5, 2]);
            outer.Add([2 * tooth + 0.5, 2]);
            outer.Add([2 * tooth + 0.5, 10]);
        }
        outer.Add([0, 10]);
        List<List<double[]>> rings = [outer, [[0.1, 0.1], [0.2, 0.1], [0.2, 0.2], [0.1, 0.2]]];

        bool outOfBudget = false;
        var triangulation = Task.Run(() => GRepParserV2.TriangulateTrimRegions(rings, out outOfBudget));
        Assert.True(await Task.WhenAny(triangulation, Task.Delay(TimeSpan.FromSeconds(60))) == triangulation,
            "the trim triangulation did not finish in 60 s");
        var polygons = await triangulation;

        Assert.False(outOfBudget);
        Assert.NotNull(polygons);
        Assert.Equal(PolygonArea(outer) - PolygonArea(rings[1]), polygons.Sum(PolygonArea), 6);
        // The edges no two polygons share are the boundary: exactly the edges of the outer loop and of the hole.
        static ((double, double), (double, double)) Key(double[] first, double[] second) =>
            first[0] < second[0] || first[0] == second[0] && first[1] < second[1]
                ? ((first[0], first[1]), (second[0], second[1])) : ((second[0], second[1]), (first[0], first[1]));
        static IEnumerable<((double, double), (double, double))> Edges(List<double[]> ring) =>
            ring.Select((point, index) => Key(point, ring[(index + 1) % ring.Count]));
        var boundary = polygons.SelectMany(Edges).GroupBy(edge => edge).Where(group => group.Count() == 1)
            .Select(group => group.Key).ToHashSet();
        Assert.True(boundary.SetEquals(rings.SelectMany(Edges)), "the boundary differs from the outer loop and the hole");
    }

    [Fact]
    public void CoplanarRingClassification_SeparatesDisjointRegionsFromHoles()
    {
        var outer = new List<double[]>
        {
            new[] { 0d, 0, 0 }, new[] { 4d, 0, 0 },
            new[] { 4d, 4, 0 }, new[] { 0d, 4, 0 },
        };
        var hole = new List<double[]>
        {
            new[] { 1d, 1, 0 }, new[] { 1d, 2, 0 },
            new[] { 2d, 2, 0 }, new[] { 2d, 1, 0 },
        };
        var disjoint = new List<double[]>
        {
            new[] { 5d, 0, 0 }, new[] { 6d, 0, 0 },
            new[] { 6d, 1, 0 }, new[] { 5d, 1, 0 },
        };

        var regions = GRepParserV2.ClassifyCoplanarRings([hole, disjoint, outer]);

        Assert.Equal(2, regions.Count);
        var main = Assert.Single(regions, region => ReferenceEquals(region.Outer, outer));
        Assert.Same(hole, Assert.Single(main.Holes));
        Assert.Contains(regions, region => ReferenceEquals(region.Outer, disjoint)
            && region.Holes.Count == 0);
    }

    [Fact]
    public void ExactClosedShell_RequiresEveryPolygonEdgeTwice()
    {
        double[][] p =
        [
            [0, 0, 0], [1, 0, 0], [1, 1, 0], [0, 1, 0],
            [0, 0, 1], [1, 0, 1], [1, 1, 1], [0, 1, 1],
        ];
        var faces = new List<GRepFace>
        {
            new() { Vertices = [p[0], p[3], p[2], p[1]] },
            new() { Vertices = [p[4], p[5], p[6], p[7]] },
            new() { Vertices = [p[0], p[1], p[5], p[4]] },
            new() { Vertices = [p[1], p[2], p[6], p[5]] },
            new() { Vertices = [p[2], p[3], p[7], p[6]] },
            new() { Vertices = [p[3], p[0], p[4], p[7]] },
        };

        Assert.True(GRepParserV2.IsExactClosedShell(faces, [0, 1, 2, 3, 4, 5]));
        Assert.False(GRepParserV2.IsExactClosedShell(faces, [0, 1, 2, 3, 4]));
    }

    [Fact]
    public void SerializedFaceTopology_ClosesCurvedTessellationAcrossCoordinateRoundoff()
    {
        double next = Math.BitIncrement(1.0);
        var faces = new List<GRepFace>
        {
            new() { Vertices = [[0, 0, 0], [1, 0, 0], [1, 1, 0]] },
            new() { Vertices = [[0, 0, 0], [next, 1, 0], [0, 1, 0]] },
            new() { Vertices = [[0, 0, 0], [1, 0, 0], [0, 0, 1]] },
            new() { Vertices = [[0, 0, 0], [0, 1, 0], [0, 0, 1]] },
        };

        var solids = GRepParserV2.BuildTopologicalSolids(
            faces, [10, 10, 20, 30], [(10u, 20u), (20u, 30u), (30u, 10u)]);

        Assert.True(Assert.Single(solids).IsClosed);
    }

    [Fact]
    public void SerializedFaceTopology_PreservesIndependentSolids()
    {
        var faces = Enumerable.Range(0, 8)
            .Select(index => new GRepFace
            {
                Vertices = [[index, 0, 0], [index, 1, 0], [index, 0, 1]],
            })
            .ToList();

        var solids = GRepParserV2.BuildTopologicalSolids(faces,
            [1, 2, 3, 4, 11, 12, 13, 14],
            [(1u, 2u), (2u, 3u), (3u, 4u), (4u, 1u),
             (11u, 12u), (12u, 13u), (13u, 14u), (14u, 11u)]);

        Assert.Equal(2, solids.Count);
        Assert.All(solids, solid => Assert.True(solid.IsClosed));
    }

    [Theory]
    [InlineData(0x00088204u, true)]
    [InlineData(0x00288204u, false)]
    [InlineData(0x04088004u, false)]
    [InlineData(0x40088204u, false)]
    public void PhysicalBodySurface_UsesSerializedGInfoState(uint flags, bool expected)
    {
        Assert.Equal(expected, GRepParserV2.IsPhysicalBodySurface(flags));
    }

    [Theory]
    [InlineData(2038, false, 0x40088E04u, 0x00088E04u)]
    [InlineData(1589, false, 0x40088204u, 0x40088204u)]
    [InlineData(1589, true, 0x40088204u, 0x00088204u)]
    [InlineData(2038, false, 0x04088E04u, 0x04088E04u)]
    public void NodeSurfaceFlags_ClearsTheModelBitOnlyWhereItIsNotEvidence(int nodeClass, bool spaceBoundary, uint flags, uint expected)
    {
        Assert.Equal(expected, GRepParserV2.NodeSurfaceFlags(nodeClass, spaceBoundary, flags));
    }

    [Fact]
    public void FacetedTopology0_EndsAfterFacetArray()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0); // normalsFlag
            writer.Write(0.0f); writer.Write(0.0f); writer.Write(1.0f); // common normal
            writer.Write(0u); // no per-point normals
            writer.Write(3u);
            writer.Write(0.0f); writer.Write(0.0f); writer.Write(0.0f);
            writer.Write(1.0f); writer.Write(0.0f); writer.Write(0.0f);
            writer.Write(0.0f); writer.Write(1.0f); writer.Write(0.0f);
            writer.Write(1u);
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)2);
        }
        byte[] data = stream.ToArray();

        Assert.True(GRepParserV2.TryDecodeFacetedTopology(data, 0, data.Length,
            hasEdgeVisibility: false, ownerQueueIndex: 7,
            out var mesh, out bool valid, out int consumed));
        Assert.True(valid);
        Assert.Equal(data.Length, consumed);
        Assert.Equal(7, mesh.OwnerQueueIndex);
        Assert.Equal(3, mesh.Points.Count);
        Assert.Equal((0, 1, 2), Assert.Single(mesh.Triangles));

        Assert.False(GRepParserV2.TryDecodeFacetedTopology(data, 0, data.Length,
            hasEdgeVisibility: true, ownerQueueIndex: 7,
            out _, out _, out _));
    }

    [Fact]
    public void NurbSplinePayload_ConsumesInlineHomogeneousControlPoints()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0.0); writer.Write(1.0); // end parameters
            writer.Write(3);                     // degree
            writer.Write((byte)0); writer.Write((byte)0); // closed, rational
            writer.Write(5u);
            for (int point = 0; point < 5; point++)
            {
                writer.Write(1.0); // homogeneous weight
                writer.Write((double)point);
                writer.Write((double)(point * point));
                writer.Write(0.0);
            }
            writer.Write(9u);
            foreach (double knot in new[] { 0d, 0d, 0d, 0d, 0.5, 1d, 1d, 1d, 1d })
                writer.Write(knot);
        }
        byte[] data = stream.ToArray();

        Assert.True(GRepParserV2.TryDecodeNurbSplinePayload(
            data, 0, data.Length, out var curve, out int consumed));
        Assert.Equal(data.Length, consumed);
        Assert.Equal([0, 0, 0], curve.Eval(0));
        Assert.Equal([4, 16, 0], curve.Eval(1));
    }

    [Fact]
    public void NurbSplineEvaluation_AppliesSerializedRationalWeights()
    {
        double middleWeight = Math.Sqrt(0.5);
        var controls = new List<GRepParserV2.NurbControlPoint>
        {
            new() { Weight = 1, Point = [1, 0, 0] },
            new() { Weight = middleWeight, Point = [1, 1, 0] },
            new() { Weight = 1, Point = [0, 1, 0] },
        };

        var point = GRepParserV2.EvaluateNurbSpline(
            0.5, 2, closed: false, rational: true,
            controls, [0, 0, 0, 1, 1, 1]);

        Assert.Equal(Math.Sqrt(0.5), point[0], 12);
        Assert.Equal(Math.Sqrt(0.5), point[1], 12);
        Assert.Equal(0, point[2]);
    }

    [Fact]
    public void RuledSurfaceSampling_RefinesDecodedNurbGuide()
    {
        double middleWeight = Math.Sqrt(0.5);
        var curve = new GRepParserV2.ArcRec
        {
            T0 = 0,
            T1 = 1,
            NurbDegree = 2,
            NurbRational = true,
            NurbControlPoints =
            [
                new() { Weight = 1, Point = [1, 0, 0] },
                new() { Weight = middleWeight, Point = [1, 1, 0] },
                new() { Weight = 1, Point = [0, 1, 0] },
            ],
            NurbKnots = [0, 0, 0, 1, 1, 1],
        };
        var surface = new GRepParserV2.SurfaceRec { ProfileHandle = 42 };

        var samples = GRepParserV2.RuledParameterSamples(
            surface, new Dictionary<uint, GRepParserV2.ArcRec> { [42] = curve },
            [(0, 0, 1, 0)], 0, 1);

        Assert.NotNull(samples);
        Assert.True(samples.Count > 2);
        Assert.Contains(0.5, samples);
    }

    [Fact]
    public void NurbSplinePayload_RejectsKnotCountThatViolatesDegreeContract()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0.0); writer.Write(1.0);
            writer.Write(2);
            writer.Write((byte)0); writer.Write((byte)0);
            writer.Write(3u);
            for (int point = 0; point < 3; point++)
                for (int component = 0; component < 4; component++) writer.Write(1.0);
            writer.Write(5u); // degree 2 with 3 controls requires 6 knots
            for (int knot = 0; knot < 5; knot++) writer.Write((double)knot);
        }
        byte[] data = stream.ToArray();

        Assert.False(GRepParserV2.TryDecodeNurbSplinePayload(
            data, 0, data.Length, out _));
    }

    [Fact]
    public void FaceCycles_KeepExteriorAndBridgeConnectedOpening()
    {
        double[][] outer =
        [
            [0, 0, 0], [4, 0, 0], [4, 4, 0], [0, 4, 0],
        ];
        double[][] hole =
        [
            [1, 1, 0], [2, 1, 0], [2, 2, 0], [1, 2, 0],
        ];
        var edges = RingEdges(outer).Concat(RingEdges(hole)).ToList();
        edges.Add((outer[0], hole[0])); // serialized non-cyclic topology bridge

        var rings = GRepParserV2.OrderRingsW(edges, includeContainingCycles: true);

        Assert.Equal(2, rings.Count);
        Assert.Equal(16, PolygonArea(rings[0]));
        Assert.Equal(1, PolygonArea(rings[1]));
    }

    [Fact]
    public void SectionCycles_SelectCompositeExteriorOverSubdivisionChord()
    {
        double[][] outer =
        [
            [0, 0, 0], [4, 0, 0], [4, 4, 0], [0, 4, 0],
        ];
        var edges = RingEdges(outer).ToList();
        edges.Add((outer[0], outer[2]));

        var rings = GRepParserV2.OrderRingsW(edges,
            preferLargestCyclePerComponent: true);

        Assert.Single(rings);
        Assert.Equal(16, PolygonArea(rings[0]));
    }

    [Fact]
    public void OrderRings_PreservesBoundarySampleInDistinctIfcPrecisionBin()
    {
        const double sampleOffsetFeet = 4e-5;
        double[][] ring =
        [
            [0, 0, 0], [sampleOffsetFeet, 0, 0], [1, 0, 0],
            [1, 1, 0], [0, 1, 0],
        ];

        var rings = GRepParserV2.OrderRingsW(RingEdges(ring).ToList());

        var result = Assert.Single(rings);
        Assert.Equal(5, result.Count);
        Assert.Contains(result, point => point[0] == sampleOffsetFeet && point[1] == 0);
    }

    private static IEnumerable<(double[] A, double[] B)> RingEdges(IReadOnlyList<double[]> ring)
    {
        for (int i = 0; i < ring.Count; i++)
            yield return (ring[i], ring[(i + 1) % ring.Count]);
    }

    private static double PolygonArea(IReadOnlyList<double[]> ring)
    {
        double twiceArea = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            twiceArea += a[0] * b[1] - b[0] * a[1];
        }
        return Math.Abs(twiceArea) / 2;
    }
}
