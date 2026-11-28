using RvtIO.Internal;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>GRepParserV2.ReadViewFacingLines and ReadDoorPlanSymbols read the node queue the walker leaves behind: the root
/// filters, the conditions that gate them and the curves they hold. The tests lay that queue out by hand and check what each
/// reader keeps and what it drops, so a changed condition check fails here and not only on a sample. The readers of the plan
/// fixture drawing share the same node queue and are tested in GRepPlanFixtureReaderTests.cs.</summary>
public partial class GRepPlanSymbolReaderTests
{
    private const uint Hidden = 0x20;

    /// <summary>The node queue of one GRep, filled the way the walker fills it for root filters, conditions and curves.</summary>
    private sealed class Frame
    {
        public readonly GRepParserV2.Cur Cur = new();
        private int handle = 500;

        public int Node(ushort cls, int parent, string field)
        {
            int index = Cur.Queue.Count;
            Cur.Queue.Add((cls, handle++));
            Cur.QueueLinks.Add((parent, field));
            if (parent >= 0)
            {
                if (!Cur.QueueChildren.TryGetValue(parent, out var children)) Cur.QueueChildren[parent] = children = [];
                children.Add(index);
            }
            return index;
        }

        public int Filter(uint flags = 0)
        {
            int filter = Node(2015, -1, "m_subNodes");
            Cur.ObjectGInfo[Cur.Queue[filter].V] = (0, 0, 0, flags);
            return filter;
        }

        public int Direction(int filter, int comparison, double[] direction, ushort cls = 1996, byte negate = 0)
        {
            int node = Node(cls, filter, "m_oConditions");
            Cur.QueueInts[node] = new() { ["m_comp"] = comparison };
            Cur.QueueBytes[node] = new() { ["m_negateDirCondition"] = [negate] };
            Cur.QueueDoubles[node] = new() { ["m_dir"] = [.. direction] };
            return node;
        }

        public void Range(int condition, double? low, double? high)
        {
            if (low is double lo) Cur.QueueDoubles[condition]["m_rangeLo"] = [lo];
            if (high is double hi) Cur.QueueDoubles[condition]["m_rangeHi"] = [hi];
        }

        public int Detail(int filter, int parameter, int comparison, int value)
        {
            int node = Node(1999, filter, "m_oConditions");
            Cur.QueueInts[node] = new() { ["m_param"] = parameter, ["m_comp"] = comparison, ["m_value"] = value };
            return node;
        }

        /// <summary>A GLine the view-facing reader may pick up, with the serialized tag of its GInfo.</summary>
        public void ViewLine(int filter, int tag, int style, GRepParserV2.ArcRec curve)
        {
            int index = Node(1736, filter, "m_subNodes");
            (Cur.ViewLineCandidates ??= []).Add((index, tag, style, curve));
        }

        /// <summary>A GLine or GArc the plan symbol reader may pick up; the curve is stored by the handle of its node.</summary>
        public int PlanCurve(int filter, GRepParserV2.ArcRec curve, uint flags = 0, bool filled = false, bool stored = true,
            int style = 3)
        {
            int index = Node(curve.R == 0 ? (ushort)1736 : (ushort)1974, filter, "m_subNodes");
            uint curveHandle = (uint)Cur.Queue[index].V;
            if (stored) Cur.Curves[curveHandle] = curve;
            (Cur.PlanCurveCandidates ??= []).Add((filter, curveHandle, flags, filled, style));
            return index;
        }
    }

    private static GRepParserV2.ArcRec Line(double[] origin, double[] direction, double t0 = 0, double t1 = 2)
    {
        var line = new GRepParserV2.ArcRec { R = 0, T0 = t0, T1 = t1 };
        origin.CopyTo(line.C, 0);
        direction.CopyTo(line.X, 0);
        return line;
    }

    private static GRepParserV2.ArcRec Arc(double radius = 1.5, double t0 = 0, double t1 = Math.PI / 2)
    {
        var arc = new GRepParserV2.ArcRec { R = radius, T0 = t0, T1 = t1 };
        arc.X[0] = 1;
        arc.Y[1] = 1;
        return arc;
    }

    // ---- ReadViewFacingLines ----

    private static readonly double[] Axis = [0, 1, 0];

    /// <summary>The two opposite elevation filters of one drawn line: side 6 holds the line 0.5 along the axis from the line of
    /// side 7. A flaw breaks one rule of the reader.</summary>
    private static List<ViewFacingLine>? ReadPair(string flaw = "", (int Comparison, int Level)? detail = null, double offset = 0.5)
    {
        var frame = new Frame();
        var line6 = Line([0, offset, 0], [1, 0, 0]);
        var line7 = Line([0, 0, 0], [1, 0, 0]);
        int comparison6 = 6, comparison7 = 7, style6 = 4, style7 = 4, tag6 = 11, tag7 = 11;
        double[] axis6 = Axis, axis7 = Axis;
        byte negate6 = 0;
        (int Parameter, int Comparison, int Value)? detail6 = detail is { } given ? (3, given.Comparison, given.Level) : null;
        var detail7 = detail6;
        bool direction6 = true, extra6 = false, lone = false;
        switch (flaw)
        {
            case "": break;
            case "one-side": lone = true; break;
            case "same-side": comparison7 = 6; break;
            case "other-side": comparison6 = 5; break;
            case "style": style7 = 5; break;
            case "tag": tag7 = 12; break;
            case "negated": negate6 = 1; break;
            case "zero-axis": axis6 = [0, 0, 0]; break;
            case "short-axis": axis6 = [0, 1]; break;
            case "other-axis": axis7 = [0, -1, 0]; break;
            case "across": line7 = Line([0.25, 0, 0], [1, 0, 0]); break;
            case "tilted": line7 = Line([0, 0, 0], [1, 0.25, 0]); break;
            // The slope moves the far end of line 7 by twice itself along the axis, so the two spacings differ by 5e-10
            // (inside the 1e-9 they may differ by) or by 2e-9 (outside it).
            case "spacing-within-tolerance": line7 = Line([0, 0, 0], [1, 2.5e-10, 0]); break;
            case "spacing-beyond-tolerance": line7 = Line([0, 0, 0], [1, 1e-9, 0]); break;
            case "across-height": line7 = Line([0, 0, 0.25], [1, 0, 0]); break;
            // Both lines collapse to a point, spaced like the real pair.
            case "point": line6 = Line([0, offset, 0], [1, 0, 0], 0, 0); line7 = Line([0, 0, 0], [1, 0, 0], 0, 0); break;
            case "not-finite": line6.C[0] = double.NaN; break;
            // The side keeps a detail condition, so it has one condition that is not a direction.
            case "no-direction": direction6 = false; detail6 = (3, 3, 2); break;
            case "three-conditions": extra6 = true; detail6 = (3, 3, 2); break;
            case "detail-parameter": detail6 = detail7 = (2, 3, 2); break;
            case "detail-comparison-high": detail6 = detail7 = (3, 6, 2); break;
            case "detail-comparison-low": detail6 = detail7 = (3, -2, 2); break;
            case "detail-level-high": detail6 = detail7 = (3, 3, 4); break;
            case "detail-level-low": detail6 = detail7 = (3, 3, 0); break;
            case "detail-one-side": detail6 = (3, 3, 2); detail7 = null; break;
            case "detail-other-level": detail6 = (3, 3, 2); detail7 = (3, 3, 3); break;
            case "detail-other-comparison": detail6 = (3, 3, 2); detail7 = (3, 4, 2); break;
            default: throw new ArgumentException(flaw);
        }
        Side(comparison6, axis6, tag6, style6, line6, negate6, detail6, direction6, extra6);
        if (!lone) Side(comparison7, axis7, tag7, style7, line7, 0, detail7, true, false);
        return GRepParserV2.ReadViewFacingLines(frame.Cur);

        void Side(int comparison, double[] axis, int tag, int style, GRepParserV2.ArcRec line, byte negate,
            (int Parameter, int Comparison, int Value)? detailCondition, bool withDirection, bool withExtra)
        {
            int filter = frame.Filter();
            if (withDirection) frame.Direction(filter, comparison, axis, negate: negate);
            if (detailCondition is var (parameter, detailComparison, value)) frame.Detail(filter, parameter, detailComparison, value);
            if (withExtra) frame.Detail(filter, 3, 3, 2);
            frame.ViewLine(filter, tag, style, line);
        }
    }

    [Fact]
    public void ViewFacingLines_AreNullWithoutCandidates() =>
        Assert.Null(GRepParserV2.ReadViewFacingLines(new GRepParserV2.Cur()));

    [Theory]
    [InlineData(0.5, true)]
    [InlineData(0, true)]
    [InlineData(-0.5, false)]
    public void ViewFacingLines_PairTheTwoSidesAndGiveEachLineTheViewingDirectionItIsDrawnFor(double offset, bool sideSixIsNearer)
    {
        var lines = ReadPair(offset: offset)!;

        // The line that lies farther along the axis is the one in front when looking back along it.
        Assert.Equal(2, lines.Count);
        Assert.Equal(new[] { 0d, offset, 0 }, lines[0].Start);
        Assert.Equal(new[] { 2d, offset, 0 }, lines[0].End);
        Assert.Equal(new[] { 0d, 0, 0 }, lines[1].Start);
        Assert.Equal(new[] { 2d, 0, 0 }, lines[1].End);
        Assert.Equal(new[] { 0d, sideSixIsNearer ? -1 : 1, 0 }, lines[0].Axis);
        Assert.Equal(new[] { 0d, sideSixIsNearer ? 1 : -1, 0 }, lines[1].Axis);
        Assert.All(lines, line => Assert.Equal(4, line.StyleId));
        Assert.All(lines, line => Assert.Null(line.DetailCondition));
    }

    [Fact]
    public void ViewFacingLines_NormalizeTheSerializedAxis()
    {
        var frame = new Frame();
        foreach (var (comparison, offset) in new[] { (6, 0.5), (7, 0d) })
        {
            int filter = frame.Filter();
            frame.Direction(filter, comparison, [0, 3, 0]);
            frame.ViewLine(filter, 11, 4, Line([0, offset, 0], [1, 0, 0]));
        }

        var lines = GRepParserV2.ReadViewFacingLines(frame.Cur)!;

        Assert.Equal(new[] { 0d, -1, 0 }, lines[0].Axis);
        Assert.Equal(new[] { 0d, 1, 0 }, lines[1].Axis);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(5, 3)]
    [InlineData(2, 2)]
    public void ViewFacingLines_CarryTheDetailConditionOfBothSides(int comparison, int level)
    {
        var lines = ReadPair(detail: (comparison, level))!;

        Assert.Equal(2, lines.Count);
        Assert.All(lines, line => Assert.Equal((comparison, level), line.DetailCondition));
    }

    [Theory]
    [InlineData("one-side")]
    [InlineData("same-side")]
    [InlineData("other-side")]
    [InlineData("style")]
    [InlineData("tag")]
    [InlineData("negated")]
    [InlineData("zero-axis")]
    [InlineData("short-axis")]
    [InlineData("other-axis")]
    [InlineData("across")]
    [InlineData("across-height")]
    [InlineData("tilted")]
    [InlineData("spacing-beyond-tolerance")]
    [InlineData("point")]
    [InlineData("not-finite")]
    [InlineData("no-direction")]
    [InlineData("three-conditions")]
    [InlineData("detail-parameter")]
    [InlineData("detail-comparison-high")]
    [InlineData("detail-comparison-low")]
    [InlineData("detail-level-high")]
    [InlineData("detail-level-low")]
    [InlineData("detail-one-side")]
    [InlineData("detail-other-level")]
    [InlineData("detail-other-comparison")]
    public void ViewFacingLines_DropAPairThatBreaksARule(string flaw) => Assert.Null(ReadPair(flaw));

    [Fact]
    public void ViewFacingLines_ReadEachPairOnItsOwnAndKeepTheGoodOnes()
    {
        var frame = new Frame();
        (int Tag, int Style, bool Broken)[] pairs = [(11, 4, false), (12, 5, true), (13, 6, false)];
        foreach (var (tag, style, broken) in pairs)
            foreach (var (comparison, offset) in new[] { (6, 0.5), (7, 0d) })
            {
                int filter = frame.Filter();
                frame.Direction(filter, comparison, [0, 1, 0]);
                // The broken pair keeps only its first side.
                if (!broken || comparison == 6) frame.ViewLine(filter, tag, style, Line([0, offset, 0], [1, 0, 0]));
            }

        var lines = GRepParserV2.ReadViewFacingLines(frame.Cur)!;

        Assert.Equal(new[] { 4, 4, 6, 6 }, lines.Select(line => line.StyleId));
    }

    [Fact]
    public void ViewFacingLines_PairLinesWhoseSpacingsDifferWithinTheTolerance() =>
        Assert.NotNull(ReadPair("spacing-within-tolerance"));

    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    public void ViewFacingLines_IgnoreACandidateOfAnotherSideInsteadOfLosingThePair(int otherSide)
    {
        var frame = new Frame();
        foreach (var (comparison, offset) in new[] { (6, 0.5), (otherSide, 0.25), (7, 0d) })
        {
            int filter = frame.Filter();
            frame.Direction(filter, comparison, [0, 1, 0]);
            frame.ViewLine(filter, 11, 4, Line([0, offset, 0], [1, 0, 0]));
        }

        var lines = GRepParserV2.ReadViewFacingLines(frame.Cur)!;

        Assert.Equal(new[] { 0.5, 0 }, lines.Select(line => line.Start[1]));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(0.25, false)]
    public void ViewFacingLines_PairLinesOnlyWhenTheirOffsetIsAlongTheAxis(double depthOffset, bool paired)
    {
        // The axis runs along x here, so an offset of the second line along y is across the axis.
        var frame = new Frame();
        foreach (var (comparison, origin) in new[] { (6, new[] { 0.5, 0, 0 }), (7, new[] { 0, depthOffset, 0 }) })
        {
            int filter = frame.Filter();
            frame.Direction(filter, comparison, [1, 0, 0]);
            frame.ViewLine(filter, 11, 4, Line(origin, [0, 1, 0]));
        }

        Assert.Equal(paired, GRepParserV2.ReadViewFacingLines(frame.Cur) is not null);
    }

    [Fact]
    public void ViewFacingLines_DoNotDependOnTheOrderTheSidesAreStored()
    {
        var frame = new Frame();
        foreach (var (comparison, offset) in new[] { (7, 0d), (6, 0.5) })
        {
            int filter = frame.Filter();
            frame.Direction(filter, comparison, [0, 1, 0]);
            frame.ViewLine(filter, 11, 4, Line([0, offset, 0], [1, 0, 0]));
        }

        var lines = GRepParserV2.ReadViewFacingLines(frame.Cur)!;

        // Side 6 comes first and faces back, as when it is stored first.
        Assert.Equal(new[] { 0.5, 0 }, lines.Select(line => line.Start[1]));
        Assert.Equal(new[] { 0d, -1, 0 }, lines[0].Axis);
        Assert.Equal(new[] { 0d, 1, 0 }, lines[1].Axis);
    }

    [Fact]
    public void ViewFacingLines_IgnoreACandidateWhoseFilterHoldsNoCondition()
    {
        var frame = new Frame();
        int filter = frame.Filter();
        frame.ViewLine(filter, 11, 4, Line([0, 0, 0], [1, 0, 0]));

        Assert.Null(GRepParserV2.ReadViewFacingLines(frame.Cur));
    }

    // ---- ReadDoorPlanSymbols ----

    /// <summary>One filter that is shown from above, holding a straight leaf line and a quarter-circle swing. A flaw breaks one
    /// rule of the reader; the flaws on the swing leave the line to be read first, so the reader has to drop the whole group.</summary>
    private static List<DoorPlanSymbol>? ReadSymbol(string flaw = "", Action<Frame>? more = null)
    {
        var frame = new Frame();
        AddSymbol(frame, flaw);
        more?.Invoke(frame);
        return GRepParserV2.ReadDoorPlanSymbols(frame.Cur);
    }

    private static void AddSymbol(Frame frame, string flaw)
    {
        int comparison = 6;
        double[] direction = [0, 0, 1];
        byte negate = 0;
        ushort conditionClass = 1996;
        uint filterFlags = 0, swingFlags = 0;
        var line = Line([0, 0, 0], [1, 0, 0], 0, 3);
        var swing = Arc();
        bool filled = false, stored = true;
        double? low = null, high = null;
        switch (flaw)
        {
            case "": break;
            case "range": conditionClass = 1995; low = 3; high = 5; break;
            case "range-inverted": conditionClass = 1995; low = 5; high = 3; break;
            case "range-empty": conditionClass = 1995; low = 3; high = 3; break;
            case "range-missing-low": conditionClass = 1995; high = 5; break;
            case "range-missing-high": conditionClass = 1995; low = 3; break;
            case "range-low-not-finite": conditionClass = 1995; low = double.NaN; high = 5; break;
            case "range-high-not-finite": conditionClass = 1995; low = 3; high = double.PositiveInfinity; break;
            case "lines-only": swing = Line([0, 0, 0], [0, 1, 0], 0, 1); break;
            case "arcs-only": line = Arc(1); break;
            case "filled": filled = true; break;
            case "hidden-curve": swingFlags = Hidden; break;
            case "hidden-filter": filterFlags = Hidden; break;
            case "other-side": comparison = 7; break;
            case "negated": negate = 1; break;
            case "not-up": direction = [0, 1, 0]; break;
            case "other-class": conditionClass = 1999; break;
            case "missing-curve": stored = false; break;
            case "ellipse": swing.RY = 0.5; break;
            case "tilted": swing.X[2] = 0.5; break;
            case "not-unit": swing.X[0] = 2; break;
            case "empty-sweep": swing.T1 = swing.T0; break;
            case "extra-child" or "third-hidden": break;
            default: throw new ArgumentException(flaw);
        }
        int filter = frame.Filter(filterFlags);
        int condition = frame.Direction(filter, comparison, direction, conditionClass, negate);
        if (conditionClass == 1995) frame.Range(condition, low, high);
        frame.PlanCurve(filter, line);
        frame.PlanCurve(filter, swing, swingFlags, filled, stored);
        if (flaw == "extra-child") frame.Node(1736, filter, "m_subNodes");
        // A group is read whole: a third curve that cannot be used takes the line and the swing with it.
        if (flaw == "third-hidden") frame.PlanCurve(filter, Line([0, 0, 0], [0, 1, 0], 0, 1), Hidden);
    }

    [Fact]
    public void DoorPlanSymbols_AreNullWithoutCandidates() =>
        Assert.Null(GRepParserV2.ReadDoorPlanSymbols(new GRepParserV2.Cur()));

    [Fact]
    public void DoorPlanSymbols_KeepTheLineAndTheSwingOfAFilterShownFromAbove()
    {
        var symbol = Assert.Single(ReadSymbol()!);

        Assert.Null(symbol.CutRange);
        Assert.Equal(2, symbol.Curves.Count);
        Assert.Equal(0, symbol.Curves[0].R);
        Assert.Equal(3, symbol.Curves[0].T1);
        Assert.Equal(1.5, symbol.Curves[1].R);
        Assert.Equal(Math.PI / 2, symbol.Curves[1].T1);
    }

    [Fact]
    public void DoorPlanSymbols_TakeTheCutRangeOfARangeCondition()
    {
        var symbol = Assert.Single(ReadSymbol("range")!);

        Assert.Equal((3d, 5d), symbol.CutRange);
        Assert.Equal(2, symbol.Curves.Count);
    }

    [Theory]
    [InlineData("range-inverted")]
    [InlineData("range-empty")]
    [InlineData("range-missing-low")]
    [InlineData("range-missing-high")]
    [InlineData("range-low-not-finite")]
    [InlineData("range-high-not-finite")]
    [InlineData("lines-only")]
    [InlineData("arcs-only")]
    [InlineData("filled")]
    [InlineData("hidden-curve")]
    [InlineData("hidden-filter")]
    [InlineData("other-side")]
    [InlineData("negated")]
    [InlineData("not-up")]
    [InlineData("other-class")]
    [InlineData("missing-curve")]
    [InlineData("ellipse")]
    [InlineData("tilted")]
    [InlineData("not-unit")]
    [InlineData("empty-sweep")]
    [InlineData("extra-child")]
    [InlineData("third-hidden")]
    public void DoorPlanSymbols_DropAFilterThatBreaksARule(string flaw) => Assert.Null(ReadSymbol(flaw));

    [Fact]
    public void DoorPlanSymbols_DropAFilterWithoutSerializedState()
    {
        var frame = new Frame();
        AddSymbol(frame, "");
        frame.Cur.ObjectGInfo.Clear();

        Assert.Null(GRepParserV2.ReadDoorPlanSymbols(frame.Cur));
    }

    [Fact]
    public void DoorPlanSymbols_DropAFilterWithTwoConditions()
    {
        var symbol = ReadSymbol(more: frame => frame.Direction(0, 6, [0, 0, 1]));

        Assert.Null(symbol);
    }

    [Fact]
    public void DoorPlanSymbols_ReadEachFilterOnItsOwn()
    {
        var frame = new Frame();
        AddSymbol(frame, "negated");
        AddSymbol(frame, "range");
        AddSymbol(frame, "");

        var symbols = GRepParserV2.ReadDoorPlanSymbols(frame.Cur)!;

        Assert.Equal(2, symbols.Count);
        Assert.Equal((3d, 5d), symbols[0].CutRange);
        Assert.Null(symbols[1].CutRange);
    }
}
