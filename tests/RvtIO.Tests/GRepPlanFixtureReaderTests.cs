using RvtIO.Internal;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>GRepParserV2.ReadPlanFixtureMasks, ReadTopFixtureProjection and ReadPlanFixtureGraphics read the plan drawing stored with
/// a family symbol from the node queue the walker leaves behind: root filters, their conditions, the faces with their loops and
/// edges, and the curves. The tests lay that queue out by hand and check what each reader draws and which rule it refuses on, so a
/// changed check fails here and not only on a sample.</summary>
public partial class GRepPlanSymbolReaderTests
{
    private const uint DisplaySurface = GRepParserV2.DisplaySurfaceBit;
    private const uint OtherFace = 88888;

    /// <summary>One root filter shown from above, with the face and the curve it holds. Each property breaks one rule of the readers;
    /// the defaults satisfy all of them.</summary>
    private sealed record Spec
    {
        public ushort FilterClass { get; init; } = 2015;
        public bool Nested { get; init; }
        public string FilterField { get; init; } = "m_subNodes";
        public uint FilterFlags { get; init; }
        public bool FilterInfo { get; init; } = true;
        public bool NoCondition { get; init; }
        public ushort ConditionClass { get; init; } = 1996;
        public int Comparison { get; init; } = 6;
        public byte Negate { get; init; }
        public double[] Direction { get; init; } = [0, 0, 1];
        public bool SecondDirection { get; init; }
        public (int Parameter, int Comparison, int Value)? Detail { get; init; }
        /// <summary>The key a detail condition lacks; "values" drops all of them.</summary>
        public string? DetailMissing { get; init; }
        public bool ExtraDetail { get; init; }
        public bool ExtraChild { get; init; }
        public bool NoFace { get; init; }
        public bool NoCurve { get; init; }
        public ushort GeometryClass { get; init; } = 2092;
        public string GeometryField { get; init; } = "m_subNodes";
        public bool GeometryRoot { get; init; }
        public bool GeometryInfo { get; init; } = true;
        public uint GeometryFlags { get; init; }
        public string FaceField { get; init; } = "m_pFaces";
        public bool FaceRoot { get; init; }
        public bool FaceInfo { get; init; } = true;
        public uint FaceFlags { get; init; } = DisplaySurface;
        public int FaceCandidates { get; init; } = 1;
        public bool Surface { get; init; } = true;
        public GRepParserV2.SurfKind Kind { get; init; } = GRepParserV2.SurfKind.Plane;
        public double SurfaceXz { get; init; }
        public double SurfaceYz { get; init; }
        public bool FirstLoop { get; init; } = true;
        public bool OpenLoop { get; init; }
        public bool BrokenChain { get; init; }
        public bool Hole { get; init; }
        public string Edges { get; init; } = "";
        public GRepParserV2.ArcRec? Curve { get; init; }
        public uint CurveFlags { get; init; }
        public bool CurveFilled { get; init; }
        public int? CurveStyle { get; init; }
        public bool CurveStored { get; init; } = true;
    }

    private static GRepParserV2.ArcRec Ellipse(double radiusY)
    {
        var arc = Arc(1.5, 0, Math.PI);
        arc.RY = radiusY;
        return arc;
    }

    private static Spec Flaw(string name) => name switch
    {
        "" => new(),
        "hidden-filter" => new() { FilterFlags = Hidden },
        "no-filter-info" => new() { FilterInfo = false },
        "filter-class" => new() { FilterClass = 2016 },
        "nested-filter" => new() { Nested = true },
        "filter-field" => new() { FilterField = "m_pFaces" },
        "no-condition" => new() { NoCondition = true },
        "range-condition" => new() { ConditionClass = 1995 },
        "other-side" => new() { Comparison = 7 },
        "negated" => new() { Negate = 1 },
        "down" => new() { Direction = [0, 0, -1] },
        "two-directions" => new() { SecondDirection = true },
        // A valid detail condition: the projection reads it, the mask reader wants the one condition alone.
        "two-conditions" => new() { Detail = (3, 3, 2) },
        "three-conditions" => new() { Detail = (3, 3, 2), ExtraDetail = true },
        "detail-parameter" => new() { Detail = (2, 3, 2) },
        "detail-comparison-high" => new() { Detail = (3, 6, 2) },
        "detail-comparison-low" => new() { Detail = (3, -2, 2) },
        "detail-level-high" => new() { Detail = (3, 3, 4) },
        "detail-level-low" => new() { Detail = (3, 3, 0) },
        "detail-no-comparison" => new() { Detail = (3, 3, 2), DetailMissing = "m_comp" },
        "detail-no-level" => new() { Detail = (3, 3, 2), DetailMissing = "m_value" },
        "detail-no-values" => new() { Detail = (3, 3, 2), DetailMissing = "values" },
        "hidden-geometry" => new() { GeometryFlags = Hidden },
        "no-geometry-info" => new() { GeometryInfo = false },
        "geometry-class" => new() { GeometryClass = 2093 },
        "geometry-field" => new() { GeometryField = "m_pEdges" },
        "geometry-root" => new() { GeometryRoot = true },
        "extra-child" => new() { ExtraChild = true },
        "face-field" => new() { FaceField = "m_pEdges" },
        "face-root" => new() { FaceRoot = true },
        "no-candidate" => new() { FaceCandidates = 0 },
        "two-candidates" => new() { FaceCandidates = 2 },
        "no-face-info" => new() { FaceInfo = false },
        "not-display-face" => new() { FaceFlags = 0 },
        "hidden-face" => new() { FaceFlags = DisplaySurface | Hidden },
        "no-surface" => new() { Surface = false },
        "cylinder" => new() { Kind = GRepParserV2.SurfKind.Cylinder },
        "tilted-x" => new() { SurfaceXz = 0.5 },
        "tilted-y" => new() { SurfaceYz = 0.5 },
        "no-first-loop" => new() { FirstLoop = false },
        "open-loop" => new() { OpenLoop = true },
        "broken-chain" => new() { BrokenChain = true },
        "hole" => new() { Hole = true },
        "edges-mixed" => new() { Edges = "mixed" },
        "edges-two-styles" => new() { Edges = "two-styles" },
        "edges-unstyled" => new() { Edges = "unstyled" },
        "edges-no-info" => new() { Edges = "no-info" },
        // A hidden curve is skipped before its other fields are looked at.
        "curve-hidden" => new() { CurveFlags = Hidden, CurveStyle = 0, CurveFilled = true },
        "curve-filled" => new() { CurveFilled = true },
        "curve-unstyled" => new() { CurveStyle = 0 },
        "curve-missing" => new() { CurveStored = false },
        "curve-backwards" => new() { Curve = Arc(1.5, 2, 1) },
        // Both ends round to the same point, so there is no segment to draw.
        "curve-degenerate" => new() { Curve = Line([5e6, 0, 0], [1e-12, 0, 0], 0, 1) },
        // The reader keeps points below 1e7 only; the start is out of range, the end is not.
        "curve-start-far" => new() { Curve = Line([-6e6, 0, 0], [1, 0, 0], -9e6, 5e6) },
        "curve-end-far" => new() { Curve = Line([0, 0, 0], [2, 0, 0], 0, 9e6) },
        "ellipse" => new() { Curve = Ellipse(0.5) },
        "ellipse-negative" => new() { Curve = Ellipse(-1) },
        // A second radius out of range, on an arc short enough that every sampled point is in range.
        "ellipse-huge" => new() { Curve = Ellipse(2e7) },
        _ => throw new ArgumentException(name),
    };

    /// <summary>A square face of one loop at the given shift along x, over the plane through (shift, 3, 0). Its edges carry the outline
    /// style, or are hidden when there is none, which makes the face a mask alone.</summary>
    private static void AddFace(Frame frame, int filter, Spec spec, double shift, int? outline)
    {
        var cur = frame.Cur;
        int geometry = frame.Node(spec.GeometryClass, spec.GeometryRoot ? -1 : filter, spec.GeometryField);
        if (spec.GeometryInfo) cur.ObjectGInfo[cur.Queue[geometry].V] = (0, 0, 0, spec.GeometryFlags);
        int face = frame.Node(1589, spec.FaceRoot ? -1 : geometry, spec.FaceField);
        uint handle = (uint)cur.Queue[face].V;
        if (spec.FaceInfo) cur.ObjectGInfo[(int)handle] = (0, 0, 0, spec.FaceFlags);
        for (int i = 0; i < spec.FaceCandidates; i++) (cur.PlanMaskCandidates ??= []).Add((face, handle));
        if (spec.Surface)
        {
            var surface = new GRepParserV2.SurfaceRec { Kind = spec.Kind };
            surface.O[0] = shift;
            surface.O[1] = 3;
            surface.X[0] = 1;
            surface.X[2] = spec.SurfaceXz;
            surface.Y[1] = 1;
            surface.Y[2] = spec.SurfaceYz;
            cur.DirectSurfaceByFace[handle] = surface;
        }
        cur.FaceHasFirstLoop[handle] = spec.FirstLoop;

        (double U, double V)[][] rings = spec.Hole
            ? [[(0, 0), (1, 0), (1, 1), (0, 1)], [(0.25, 0.25), (0.25, 0.75), (0.75, 0.75), (0.75, 0.25)]]
            : [[(0, 0), (1, 0), (1, 1), (0, 1)]];
        int parent = face;
        string field = "m_pFirstLoop";
        foreach (var ring in rings)
        {
            int loop = frame.Node(1254, parent, field);
            uint sentinel = (uint)cur.Queue[loop].V, first = (uint)(9000 + cur.Edges.Count);
            for (int i = 0; i < ring.Length; i++)
            {
                var (u0, v0) = ring[i];
                var (u1, v1) = ring[(i + 1) % ring.Length];
                uint edge = first + (uint)i;
                cur.Edges.Add(new GRepParserV2.EdgeRec
                {
                    Handle = edge, FaceA = handle, FaceB = OtherFace,
                    PrevA = i == 0 ? sentinel : edge - 1, NextA = i == ring.Length - 1 ? sentinel : edge + 1,
                    Start = new() { UA = u0, VA = v0, UB = u0, VB = v0 },
                    EndP = new() { UA = u1, VA = v1, UB = u1, VB = v1 },
                });
                if (EdgeInfo(i) is var (category, flags)) cur.ObjectGInfo[(int)edge] = (0, 0, category, flags);
            }
            // The loop names its first and last edge; a broken chain ends on the wrong one.
            cur.SpaceEdgeLoops[loop] = new()
            {
                Face = handle, Next = first, Prev = spec.BrokenChain ? first : first + (uint)ring.Length - 1,
                Open = (byte)(spec.OpenLoop ? 1 : 0),
            };
            parent = loop;
            field = "m_nextLoop";
        }
        cur.LastLoops.Add(parent);

        (int Category, uint Flags)? EdgeInfo(int index) => spec.Edges switch
        {
            "mixed" => (7, index == 0 ? 0u : Hidden),
            "two-styles" => (index % 2 == 0 ? 7 : 8, 0u),
            "unstyled" => (0, 0u),
            "no-info" => null,
            _ => outline is { } style ? (style, 0u) : (0, Hidden),
        };
    }

    /// <summary>A root filter shown from above: its conditions, a face, a curve and what its spec breaks.</summary>
    private static void AddTop(Frame frame, Spec spec, double shift, int? outline, int curveStyle, GRepParserV2.ArcRec curve)
    {
        var cur = frame.Cur;
        int outer = spec.Nested ? frame.Filter() : -1;
        int filter = frame.Node(spec.FilterClass, outer, spec.FilterField);
        if (spec.FilterInfo) cur.ObjectGInfo[cur.Queue[filter].V] = (0, 0, 0, spec.FilterFlags);
        if (!spec.NoCondition)
            frame.Direction(filter, spec.Comparison, spec.Direction, spec.ConditionClass, spec.Negate);
        if (spec.SecondDirection) frame.Direction(filter, 6, [0, 0, 1]);
        if (spec.Detail is var (parameter, comparison, value))
        {
            int detail = frame.Detail(filter, parameter, comparison, value);
            if (spec.DetailMissing == "values") cur.QueueInts.Remove(detail);
            else if (spec.DetailMissing is { } key) cur.QueueInts[detail].Remove(key);
            if (spec.ExtraDetail) frame.Detail(filter, 3, 3, 2);
        }
        if (!spec.NoFace) AddFace(frame, filter, spec, shift, outline);
        if (spec.ExtraChild) frame.Node(1736, filter, "m_subNodes");
        if (!spec.NoCurve)
            frame.PlanCurve(filter, spec.Curve ?? curve, spec.CurveFlags, spec.CurveFilled, spec.CurveStored, spec.CurveStyle ?? curveStyle);
    }

    /// <summary>A half circle about (2, 10, 0).</summary>
    private static GRepParserV2.ArcRec HalfCircle()
    {
        var arc = Arc(1.5, 0, Math.PI);
        arc.C[0] = 2;
        arc.C[1] = 10;
        return arc;
    }

    /// <summary>The filter the flaw breaks, with a face at x 2 (outlined in style 7) and the half circle in style 6, then a sound
    /// filter at x 20 whose face is a mask alone and whose line has style 5. What the second one draws shows that the reader went
    /// on past the first.</summary>
    private static Frame TopFrame(string flaw = "")
    {
        var frame = new Frame();
        AddTop(frame, Flaw(flaw), 2, 7, 6, HalfCircle());
        AddTop(frame, new Spec(), 20, null, 5, Line([20, 30, 0], [1, 0, 0]));
        return frame;
    }

    /// <summary>The square of the first face, moved to the given x.</summary>
    private static double[][] Square(double x) => [[x, 3, 0], [x + 1, 3, 0], [x + 1, 4, 0], [x, 4, 0]];

    private static void AssertLine(StyledModelLine line, double[] start, double[] end, int style)
    {
        Assert.Equal(start, line.Start);
        Assert.Equal(end, line.End);
        Assert.Equal(style, line.StyleId);
    }

    private static PlanFixtureProjection? ReadTop(Frame frame, int level = 3) =>
        GRepParserV2.ReadTopFixtureProjection(frame.Cur, level);

    // ---- ReadPlanFixtureMasks ----

    [Fact]
    public void PlanFixtureMasks_AreEmptyWithoutCandidates() =>
        Assert.Empty(GRepParserV2.ReadPlanFixtureMasks(new GRepParserV2.Cur()));

    [Fact]
    public void PlanFixtureMasks_TakeTheRingOfEveryFaceInPlaceOfItsSurface()
    {
        var masks = GRepParserV2.ReadPlanFixtureMasks(TopFrame().Cur);

        Assert.Equal(2, masks.Count);
        Assert.Equal(Square(2), masks[0]);
        Assert.Equal(Square(20), masks[1]);
    }

    [Theory]
    [InlineData("hidden-filter")]
    [InlineData("no-filter-info")]
    [InlineData("filter-class")]
    [InlineData("nested-filter")]
    [InlineData("filter-field")]
    [InlineData("no-condition")]
    [InlineData("range-condition")]
    [InlineData("other-side")]
    [InlineData("negated")]
    [InlineData("down")]
    [InlineData("two-directions")]
    [InlineData("two-conditions")]
    [InlineData("three-conditions")]
    [InlineData("hidden-geometry")]
    [InlineData("no-geometry-info")]
    [InlineData("geometry-class")]
    [InlineData("geometry-field")]
    [InlineData("geometry-root")]
    [InlineData("face-field")]
    [InlineData("face-root")]
    [InlineData("no-surface")]
    [InlineData("cylinder")]
    [InlineData("tilted-x")]
    [InlineData("tilted-y")]
    [InlineData("no-first-loop")]
    [InlineData("open-loop")]
    [InlineData("broken-chain")]
    [InlineData("hole")]
    public void PlanFixtureMasks_DropAFaceThatBreaksARuleAndKeepTheNextOne(string flaw)
    {
        var masks = GRepParserV2.ReadPlanFixtureMasks(TopFrame(flaw).Cur);

        Assert.Equal(Square(20), Assert.Single(masks));
    }

    // ---- ReadTopFixtureProjection ----

    [Fact]
    public void TopFixtureProjection_IsNullWithoutCandidates() =>
        Assert.Null(GRepParserV2.ReadTopFixtureProjection(new GRepParserV2.Cur(), 3));

    [Fact]
    public void TopFixtureProjection_DrawsTheFacesAndCurvesOfEveryFilterShownFromAbove()
    {
        var projection = ReadTop(TopFrame())!;

        // The edges of the face come in the order of their loop, then the sampled half circle, then the line of the second filter.
        var lines = projection.Lines;
        var corners = Square(2);
        for (int i = 0; i < 4; i++) AssertLine(lines[i], corners[i], corners[(i + 1) % 4], 7);
        AssertSampled(lines.Skip(4).SkipLast(1).ToArray(), HalfCircle(), 6);
        AssertLine(lines[^1], [20, 30, 0], [22, 30, 0], 5);
        Assert.Equal(2, projection.Masks.Count);
        Assert.Equal(Square(2), projection.Masks[0]);
        Assert.Equal(Square(20), projection.Masks[1]);
    }

    /// <summary>The segments follow the curve from its start to its end, each beginning where the one before ended.</summary>
    private static void AssertSampled(StyledModelLine[] segments, GRepParserV2.ArcRec curve, int style)
    {
        Assert.True(segments.Length >= 2);
        Assert.All(segments, segment => Assert.Equal(style, segment.StyleId));
        void Near(double[] expected, double[] actual) =>
            Assert.All(expected.Zip(actual), pair => Assert.Equal(pair.First, pair.Second, 9));
        Near(curve.Eval(curve.T0), segments[0].Start);
        Near(curve.Eval(curve.T1), segments[^1].End);
        for (int i = 1; i < segments.Length; i++) Near(segments[i - 1].End, segments[i].Start);
    }

    [Fact]
    public void TopFixtureProjection_SamplesAnEllipseThroughItsSecondRadius()
    {
        var projection = ReadTop(TopFrame("ellipse"))!;

        AssertSampled(projection.Lines.Skip(4).SkipLast(1).ToArray(), Ellipse(0.5), 6);
    }

    [Fact]
    public void TopFixtureProjection_DrawsTheEdgesOfAFaceWithAHoleButMakesNoMaskOfIt()
    {
        var projection = ReadTop(TopFrame("hole"))!;

        Assert.Equal(8, projection.Lines.Count(line => line.StyleId == 7));
        AssertLine(projection.Lines[4], [2.25, 3.25, 0], [2.25, 3.75, 0], 7);
        Assert.Equal(Square(20), Assert.Single(projection.Masks));
    }

    [Fact]
    public void TopFixtureProjection_DrawsCurvesWithoutAnyFace()
    {
        var frame = new Frame();
        AddTop(frame, new Spec { NoFace = true }, 2, 7, 6, HalfCircle());

        var projection = ReadTop(frame)!;

        AssertSampled([.. projection.Lines], HalfCircle(), 6);
        Assert.Empty(projection.Masks);
    }

    [Fact]
    public void TopFixtureProjection_DrawsTheOutlineOfAFaceWithoutAnyCurve()
    {
        var frame = new Frame();
        AddTop(frame, new Spec { NoCurve = true }, 2, 7, 6, HalfCircle());

        var projection = ReadTop(frame)!;

        Assert.Equal(new[] { 7, 7, 7, 7 }, projection.Lines.Select(line => line.StyleId));
        Assert.Equal(Square(2), Assert.Single(projection.Masks));
    }

    [Fact]
    public void TopFixtureProjection_IsNullForAMaskThatHasNoLines()
    {
        var frame = new Frame();
        AddTop(frame, new Spec { NoCurve = true }, 2, null, 6, HalfCircle());

        Assert.Null(ReadTop(frame));
    }

    [Theory]
    [InlineData("hidden-filter")]
    [InlineData("no-filter-info")]
    [InlineData("filter-class")]
    [InlineData("nested-filter")]
    [InlineData("filter-field")]
    [InlineData("no-condition")]
    [InlineData("range-condition")]
    [InlineData("other-side")]
    [InlineData("negated")]
    [InlineData("down")]
    public void TopFixtureProjection_SkipsAFilterThatIsNotShownFromAboveAndDrawsTheNext(string flaw)
    {
        var projection = ReadTop(TopFrame(flaw))!;

        AssertLine(Assert.Single(projection.Lines), [20, 30, 0], [22, 30, 0], 5);
        Assert.Equal(Square(20), Assert.Single(projection.Masks));
    }

    [Theory]
    [InlineData("curve-hidden")]
    [InlineData("curve-degenerate")]
    public void TopFixtureProjection_DrawsTheFaceOfAFilterWhoseCurveHasNothingToDraw(string flaw)
    {
        var projection = ReadTop(TopFrame(flaw))!;

        Assert.Equal(5, projection.Lines.Count);
        Assert.Equal(new[] { 7, 7, 7, 7, 5 }, projection.Lines.Select(line => line.StyleId));
        Assert.Equal(2, projection.Masks.Count);
    }

    [Theory]
    [InlineData("three-conditions")]
    [InlineData("two-directions")]
    [InlineData("detail-parameter")]
    [InlineData("detail-comparison-high")]
    [InlineData("detail-comparison-low")]
    [InlineData("detail-level-high")]
    [InlineData("detail-level-low")]
    [InlineData("detail-no-comparison")]
    [InlineData("detail-no-level")]
    [InlineData("detail-no-values")]
    [InlineData("geometry-class")]
    [InlineData("extra-child")]
    [InlineData("hidden-geometry")]
    [InlineData("no-geometry-info")]
    [InlineData("no-candidate")]
    [InlineData("two-candidates")]
    [InlineData("face-field")]
    [InlineData("no-face-info")]
    [InlineData("not-display-face")]
    [InlineData("hidden-face")]
    [InlineData("no-surface")]
    [InlineData("cylinder")]
    [InlineData("tilted-x")]
    [InlineData("tilted-y")]
    [InlineData("no-first-loop")]
    [InlineData("open-loop")]
    [InlineData("broken-chain")]
    [InlineData("edges-mixed")]
    [InlineData("edges-two-styles")]
    [InlineData("edges-unstyled")]
    [InlineData("edges-no-info")]
    [InlineData("curve-filled")]
    [InlineData("curve-unstyled")]
    [InlineData("curve-missing")]
    [InlineData("curve-backwards")]
    [InlineData("curve-start-far")]
    [InlineData("curve-end-far")]
    [InlineData("ellipse-negative")]
    [InlineData("ellipse-huge")]
    public void TopFixtureProjection_DrawsNothingWhenAFilterShownFromAboveIsMisread(string flaw) =>
        Assert.Null(ReadTop(TopFrame(flaw)));

    [Theory]
    [InlineData(-1, 2, 2, true)]
    [InlineData(-1, 2, 1, false)]
    [InlineData(3, 2, 2, true)]
    [InlineData(3, 3, 2, false)]
    [InlineData(3, 3, 3, true)]
    [InlineData(3, 1, 1, true)]
    [InlineData(0, 2, 2, false)]
    [InlineData(0, 2, 1, true)]
    [InlineData(1, 2, 1, true)]
    [InlineData(1, 2, 2, false)]
    [InlineData(2, 2, 2, true)]
    [InlineData(2, 3, 2, true)]
    [InlineData(2, 1, 2, false)]
    [InlineData(4, 2, 2, true)]
    [InlineData(4, 3, 2, false)]
    [InlineData(5, 1, 2, true)]
    [InlineData(5, 2, 2, false)]
    public void TopFixtureProjection_DrawsAFilterOnlyAtTheDetailLevelItsConditionAccepts(
        int comparison, int value, int level, bool drawn)
    {
        var frame = new Frame();
        AddTop(frame, new Spec { Detail = (3, comparison, value) }, 2, 7, 6, HalfCircle());
        AddTop(frame, new Spec(), 20, null, 5, Line([20, 30, 0], [1, 0, 0]));

        var projection = ReadTop(frame, level)!;

        // The filter that the level does not accept is left out and the next one is still read.
        Assert.Equal(drawn ? 4 : 0, projection.Lines.Count(line => line.StyleId == 7));
        Assert.Equal(drawn ? 2 : 1, projection.Masks.Count);
        AssertLine(projection.Lines[^1], [20, 30, 0], [22, 30, 0], 5);
    }

    // ---- ReadPlanFixtureGraphics ----

    /// <summary>One filter without conditions that holds two lines, in styles 4 and 5, from the given y; the flaw breaks a rule of the
    /// reader for it.</summary>
    private static void AddBranch(Frame frame, string flaw, double y)
    {
        var cur = frame.Cur;
        var first = Line([0, y, 0], [1, 0, 0]);
        var second = Line([0, y + 1, 0], [1, 0, 0]);
        uint filterFlags = 0, secondFlags = 0;
        int secondStyle = 5;
        bool stored = true;
        switch (flaw)
        {
            case "" or "no-info" or "extra-child" or "other-condition" or "wrong-field": break;
            case "hidden-filter": filterFlags = Hidden; break;
            case "arc-child": second = Arc(); break;
            // A hidden line is skipped before its style is looked at.
            case "hidden-item": secondFlags = Hidden; secondStyle = 0; break;
            case "unstyled": secondStyle = 0; break;
            case "missing": stored = false; break;
            case "backwards": second = Line([0, y + 1, 0], [1, 0, 0], 2, 0); break;
            case "degenerate": second = Line([5e6, y + 1, 0], [1e-12, 0, 0], 0, 1); break;
            case "start-far": second = Line([-6e6, y + 1, 0], [1, 0, 0], -9e6, 5e6); break;
            case "end-far": second = Line([0, y + 1, 0], [2, 0, 0], 0, 9e6); break;
            default: throw new ArgumentException(flaw);
        }
        int filter = frame.Filter(filterFlags);
        frame.PlanCurve(filter, first, style: 4);
        frame.PlanCurve(filter, second, secondFlags, stored: stored, style: secondStyle);
        switch (flaw)
        {
            case "no-info": cur.ObjectGInfo.Remove(cur.Queue[filter].V); break;
            case "extra-child": frame.Node(1736, filter, "m_subNodes"); break;
            case "other-condition": frame.Direction(filter, 7, [0, 0, 1]); break;
            case "wrong-field":
                // The capture only takes children of the m_subNodes field, so this is a second line of defence.
                int index = frame.Node(1736, filter, "m_oConditions");
                uint handle = (uint)cur.Queue[index].V;
                cur.Curves[handle] = Line([0, y + 2, 0], [1, 0, 0]);
                cur.PlanCurveCandidates!.Add((filter, handle, 0, false, 4));
                break;
        }
    }

    private static Frame BranchFrame(string flaw = "")
    {
        var frame = new Frame();
        AddBranch(frame, flaw, 0);
        AddBranch(frame, "", 10);
        return frame;
    }

    [Fact]
    public void PlanFixtureGraphics_AreNullWithoutCandidates() =>
        Assert.Null(GRepParserV2.ReadPlanFixtureGraphics(new GRepParserV2.Cur()));

    [Fact]
    public void PlanFixtureGraphics_KeepTheLinesOfEveryCompleteBranchWithoutConditions()
    {
        var graphics = GRepParserV2.ReadPlanFixtureGraphics(BranchFrame().Cur)!;

        Assert.Equal(4, graphics.Lines.Count);
        AssertLine(graphics.Lines[0], [0, 0, 0], [2, 0, 0], 4);
        AssertLine(graphics.Lines[1], [0, 1, 0], [2, 1, 0], 5);
        AssertLine(graphics.Lines[2], [0, 10, 0], [2, 10, 0], 4);
        AssertLine(graphics.Lines[3], [0, 11, 0], [2, 11, 0], 5);
        Assert.Empty(graphics.Masks);
        Assert.Null(graphics.MediumProjection);
        Assert.Null(graphics.CoarseProjection);
    }

    [Theory]
    [InlineData("hidden-filter")]
    [InlineData("no-info")]
    [InlineData("extra-child")]
    [InlineData("other-condition")]
    [InlineData("arc-child")]
    [InlineData("wrong-field")]
    [InlineData("unstyled")]
    [InlineData("missing")]
    [InlineData("backwards")]
    [InlineData("degenerate")]
    [InlineData("start-far")]
    [InlineData("end-far")]
    public void PlanFixtureGraphics_DropABranchThatIsNotCompleteWholeAndKeepTheNextOne(string flaw)
    {
        var graphics = GRepParserV2.ReadPlanFixtureGraphics(BranchFrame(flaw).Cur)!;

        Assert.Equal(new[] { 4, 5 }, graphics.Lines.Select(line => line.StyleId));
        Assert.Equal(new[] { 0d, 10, 0 }, graphics.Lines[0].Start);
    }

    [Fact]
    public void PlanFixtureGraphics_SkipAHiddenLineAndKeepTheRestOfItsBranch()
    {
        var graphics = GRepParserV2.ReadPlanFixtureGraphics(BranchFrame("hidden-item").Cur)!;

        Assert.Equal(new[] { 4, 4, 5 }, graphics.Lines.Select(line => line.StyleId));
        Assert.Equal(new[] { 0d, 0, 0 }, graphics.Lines[0].Start);
    }

    [Fact]
    public void PlanFixtureGraphics_AreNullWhenNothingIsDrawn()
    {
        var frame = new Frame();
        AddTop(frame, Flaw("hidden-filter"), 2, 7, 6, HalfCircle());
        AddBranch(frame, "hidden-filter", 0);

        Assert.Null(GRepParserV2.ReadPlanFixtureGraphics(frame.Cur));
    }

    [Fact]
    public void PlanFixtureGraphics_TakeTheMasksAndBothProjectionsOfTheFiltersShownFromAbove()
    {
        var frame = BranchFrame();
        AddTop(frame, new Spec(), 2, 7, 6, HalfCircle());

        var graphics = GRepParserV2.ReadPlanFixtureGraphics(frame.Cur)!;

        Assert.Equal(4, graphics.Lines.Count);
        Assert.Equal(Square(2), Assert.Single(graphics.Masks));
        foreach (var projection in new[] { graphics.MediumProjection, graphics.CoarseProjection })
        {
            Assert.Equal(4, projection!.Lines.Count(line => line.StyleId == 7));
            Assert.Equal(Square(2), Assert.Single(projection.Masks));
        }
    }

    [Fact]
    public void PlanFixtureGraphics_LeaveTheMasksOutWhenThereAreNoLines()
    {
        var frame = new Frame();
        AddTop(frame, new Spec(), 2, 7, 6, HalfCircle());

        var graphics = GRepParserV2.ReadPlanFixtureGraphics(frame.Cur)!;

        Assert.Empty(graphics.Lines);
        Assert.Empty(graphics.Masks);
        Assert.Equal(Square(2), Assert.Single(graphics.MediumProjection!.Masks));
    }

    [Fact]
    public void PlanFixtureGraphics_ReadTheMediumProjectionAtLevelTwoAndTheCoarseOneAtLevelOne()
    {
        var frame = new Frame();
        AddTop(frame, new Spec { Detail = (3, 3, 1) }, 2, 7, 21, HalfCircle());
        AddTop(frame, new Spec { Detail = (3, 3, 2) }, 20, null, 22, Line([20, 30, 0], [1, 0, 0]));

        var graphics = GRepParserV2.ReadPlanFixtureGraphics(frame.Cur)!;

        Assert.Equal(new[] { 22 }, graphics.MediumProjection!.Lines.Select(line => line.StyleId).Distinct());
        Assert.Equal(new[] { 7, 21 }, graphics.CoarseProjection!.Lines.Select(line => line.StyleId).Distinct());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void PlanFixtureGraphics_KeepAProjectionOfOneDetailLevelAlone(int level)
    {
        var frame = new Frame();
        AddTop(frame, new Spec { Detail = (3, 3, level) }, 20, null, 22, Line([20, 30, 0], [1, 0, 0]));

        var graphics = GRepParserV2.ReadPlanFixtureGraphics(frame.Cur)!;

        Assert.Empty(graphics.Lines);
        Assert.Equal(level == 2, graphics.MediumProjection != null);
        Assert.Equal(level == 1, graphics.CoarseProjection != null);
    }
}
