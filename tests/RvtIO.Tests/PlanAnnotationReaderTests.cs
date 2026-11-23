using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The readers that turn the saved parameters of plan annotations (dimensions, text notes, elevation marks, stair
/// paths, room colors, contour labels) into what the plan printer draws. Each reader takes one shape of the annotation
/// and drops the rest, so a test starts from a complete annotation and breaks one fact of it at a time.</summary>
public class PlanAnnotationReaderTests
{
    private const uint View = 20;

    private static TfBlockDeserializer.NativeParams Item(ParsingState state, uint id)
        => state.NativeParams.TryGetValue(id, out var item) ? item : state.NativeParams[id] = new();

    private static void Int(ParsingState state, uint id, string key, int value) => Item(state, id).Fields[key] = value;
    private static void Real(ParsingState state, uint id, string key, double value) => Item(state, id).DoubleFields[key] = value;
    private static void Text(ParsingState state, uint id, string key, string value) => Item(state, id).StringFields[key] = value;

    private static void Parameter(ParsingState state, uint id, BuiltInParameter parameter, double? value)
    {
        var doubles = Item(state, id).Doubles;
        doubles.RemoveAll(entry => entry.ParamId == (int)parameter);
        if (value != null) doubles.Add(((int)parameter, value.Value));
    }

    public static IEnumerable<object[]> Names(IEnumerable<string> names) => names.Select(name => new[] { name });

    // Dimensions

    private const uint Dimension = 10, DimensionStyle = 30, Font = 40, Tick = 50;

    private static ParsingState Dimensions()
    {
        var state = new ParsingState();
        var dimension = Item(state, Dimension);
        dimension.DimensionLine = ([0.5, 0.5, 0], [1, 0, 0]);
        dimension.DimensionReferences = [[0, 0, 0], [1, 0, 0]];
        dimension.DimensionValues = [1];
        Int(state, Dimension, "m_id", (int)Dimension);
        Int(state, Dimension, "m_ownerDBViewId", (int)View);
        Int(state, Dimension, "m_styleSymbolId", (int)DimensionStyle);
        Int(state, Dimension, "160:m_flags", 12);
        Int(state, Dimension, "160:m_useEqualityFormula", 0);
        Int(state, Dimension, "160:m_showLabelGP", 0);
        foreach (var (key, value) in new (string, int)[]
        {
            ("625:m_fontId", (int)Font), ("174:m_arrowHeadStyleId", (int)Tick), ("625:m_categoryId", 60),
            ("174:m_tickCategoryId", 61), ("174:m_dimensionStyleType", 0), ("174:m_linearDimType", 1),
            ("174:m_textAlignment", 1), ("174:m_textLoc", 0), ("174:m_alternateUnits", 0),
            ("174:m_dimWitnCntrlExtBelow", 0), ("174:linearFormatUsesDefault", 1),
            ("174:m_interiorTickMarkStyleId", (int)Tick),
        }) Int(state, DimensionStyle, key, value);
        Parameter(state, DimensionStyle, BuiltInParameter.DIM_LINE_EXTENSION, 0.002);
        Parameter(state, DimensionStyle, BuiltInParameter.WITNS_LINE_EXTENSION, 0.003);
        Parameter(state, DimensionStyle, BuiltInParameter.WITNS_LINE_GAP_TO_ELT, 0.004);
        Parameter(state, DimensionStyle, BuiltInParameter.TEXT_DIST_TO_LINE, 0.005);
        Real(state, Font, "1923:m_size", 0.01);
        Int(state, Tick, "2295:m_tickType", 0);
        Parameter(state, Tick, BuiltInParameter.ARROW_SIZE, 0.006);
        return state;
    }

    private static readonly Dictionary<string, Action<ParsingState>> DimensionDefects = new()
    {
        ["one_reference"] = s => { Item(s, Dimension).DimensionReferences = [[0, 0, 0]]; Item(s, Dimension).DimensionValues = []; },
        ["too_many_values"] = s => Item(s, Dimension).DimensionValues = [1, 1],
        ["no_values"] = s => Item(s, Dimension).DimensionValues = [],
        ["too_few_values"] = s => Item(s, Dimension).DimensionReferences = [[0, 0, 0], [1, 0, 0], [2, 0, 0]],
        ["value_zero"] = s => Item(s, Dimension).DimensionValues = [0],
        ["value_not_a_number"] = s => Item(s, Dimension).DimensionValues = [double.NaN],
        ["other_id"] = s => Int(s, Dimension, "m_id", 11),
        ["no_view"] = s => Int(s, Dimension, "m_ownerDBViewId", 0),
        ["no_style"] = s => Int(s, Dimension, "m_styleSymbolId", 0),
        ["flags"] = s => Int(s, Dimension, "160:m_flags", 13),
        ["equality_formula"] = s => Int(s, Dimension, "160:m_useEqualityFormula", 1),
        ["label"] = s => Int(s, Dimension, "160:m_showLabelGP", 1),
        ["multi_reference"] = s => Int(s, Dimension, "159:m_multiReferenceAnnotationId", 5),
        ["cells"] = s => Item(s, Dimension).CellClasses = [1],
        ["origin_far"] = s => Item(s, Dimension).DimensionLine = ([2_000_000, 0, 0], [1, 0, 0]),
        ["direction_not_a_number"] = s => Item(s, Dimension).DimensionLine = ([0, 0, 0], [double.NaN, 0, 0]),
        ["reference_far"] = s => Item(s, Dimension).DimensionReferences = [[0, 0, 0], [0, 2_000_000, 0]],
        ["style_missing"] = s => s.NativeParams.Remove(DimensionStyle),
        ["style_type"] = s => Int(s, DimensionStyle, "174:m_dimensionStyleType", 1),
        ["style_linear_type"] = s => Int(s, DimensionStyle, "174:m_linearDimType", 2),
        ["style_alignment"] = s => Int(s, DimensionStyle, "174:m_textAlignment", 2),
        ["style_text_location"] = s => Int(s, DimensionStyle, "174:m_textLoc", 1),
        ["style_alternate_units"] = s => Int(s, DimensionStyle, "174:m_alternateUnits", 1),
        ["style_witness_below"] = s => Int(s, DimensionStyle, "174:m_dimWitnCntrlExtBelow", 1),
        ["style_own_format"] = s => Int(s, DimensionStyle, "174:linearFormatUsesDefault", 0),
        ["style_prefix"] = s => Text(s, DimensionStyle, "174:m_prefix", "~"),
        ["style_suffix"] = s => Text(s, DimensionStyle, "174:m_suffix", "mm"),
        ["style_category"] = s => Int(s, DimensionStyle, "625:m_categoryId", 0),
        ["style_tick_category"] = s => Int(s, DimensionStyle, "174:m_tickCategoryId", 0),
        ["style_no_font"] = s => Int(s, DimensionStyle, "625:m_fontId", 0),
        ["style_no_tick"] = s => Int(s, DimensionStyle, "174:m_arrowHeadStyleId", 0),
        ["style_other_interior_tick"] = s => Int(s, DimensionStyle, "174:m_interiorTickMarkStyleId", 51),
        ["font_missing"] = s => s.NativeParams.Remove(Font),
        ["tick_missing"] = s => s.NativeParams.Remove(Tick),
        ["tick_type"] = s => Int(s, Tick, "2295:m_tickType", 1),
        ["text_height_foot"] = s => Real(s, Font, "1923:m_size", 1),
        ["text_height_zero"] = s => Real(s, Font, "1923:m_size", 0),
        ["tick_size_missing"] = s => Parameter(s, Tick, BuiltInParameter.ARROW_SIZE, null),
        ["tick_size_foot"] = s => Parameter(s, Tick, BuiltInParameter.ARROW_SIZE, 1),
        ["line_extension_negative"] = s => Parameter(s, DimensionStyle, BuiltInParameter.DIM_LINE_EXTENSION, -0.001),
        ["witness_extension_missing"] = s => Parameter(s, DimensionStyle, BuiltInParameter.WITNS_LINE_EXTENSION, null),
        ["witness_gap_foot"] = s => Parameter(s, DimensionStyle, BuiltInParameter.WITNS_LINE_GAP_TO_ELT, 1),
        ["text_gap_missing"] = s => Parameter(s, DimensionStyle, BuiltInParameter.TEXT_DIST_TO_LINE, null),
    };

    public static IEnumerable<object[]> DimensionDefectNames => Names(DimensionDefects.Keys);

    [Fact]
    public void Dimension_IsReadWithItsStyle()
    {
        var info = new RevitFileInfo();

        ElementBuilder.ReadPlanDimensions(info, Dimensions());

        var dimension = Assert.Single(Assert.Single(info.PlanDimensions!, entry => entry.Key == (int)View).Value);
        Assert.Equal((int)DimensionStyle, dimension.StyleId);
        Assert.Equal(new[] { 0.5, 0.5, 0 }, dimension.Origin);
        Assert.Equal(new double[] { 1, 0, 0 }, dimension.Direction);
        Assert.Equal(2, dimension.References.Count);
        Assert.Equal(new[] { 1d }, dimension.Values);
        var style = Assert.Single(info.PlanDimensionStyles!).Value;
        Assert.Equal(new RevitFileInfo.PlanDimensionStyle(0.01, 60, 61, 0.002, 0.003, 0.004, 0.005, 0.006), style);
    }

    [Fact]
    public void Dimensions_ShareOneStyleAndKeepTheirViews()
    {
        var state = Dimensions();
        foreach (uint id in new uint[] { 11, 12 })
        {
            var copy = Item(state, id);
            copy.DimensionLine = ([0.5, 0.5, 0], [1, 0, 0]);
            copy.DimensionReferences = [[0, 0, 0], [0, 2, 0], [0, 5, 0]];
            copy.DimensionValues = [2, 3];
            foreach (var (key, value) in Item(state, Dimension).Fields) copy.Fields[key] = value;
            copy.Fields["m_id"] = (int)id;
            if (id == 12) copy.Fields["m_ownerDBViewId"] = 21;
        }
        var info = new RevitFileInfo();

        ElementBuilder.ReadPlanDimensions(info, state);

        Assert.Equal(2, info.PlanDimensions![(int)View].Count);
        Assert.Equal(new[] { 2d, 3d }, info.PlanDimensions[21].Single().Values);
        Assert.Single(info.PlanDimensionStyles!);
    }

    [Fact]
    public void Dimensions_AreEmptyWithoutAnySavedDimension()
    {
        var info = new RevitFileInfo();

        ElementBuilder.ReadPlanDimensions(info, new ParsingState());

        Assert.Empty(info.PlanDimensions!);
        Assert.Empty(info.PlanDimensionStyles!);
    }

    [Theory]
    [MemberData(nameof(DimensionDefectNames))]
    public void Dimension_OutsideTheSupportedShapeIsLeftOut(string defect)
    {
        var state = Dimensions();
        DimensionDefects[defect](state);
        var info = new RevitFileInfo();

        ElementBuilder.ReadPlanDimensions(info, state);

        Assert.Empty(info.PlanDimensions!);
    }

    // Text notes

    private const uint Note = 5, NoteStyle = 70, LeaderArrow = 90;

    private static ParsingState Notes()
    {
        var state = new ParsingState();
        Item(state, Note).PlanNoteText = ("Note", 12);
        Int(state, Note, "m_id", (int)Note);
        Int(state, Note, "m_ownerDBViewId", (int)View);
        Int(state, Note, "3928:m_flags", 8768);
        Int(state, Note, "3928:m_attrId", (int)NoteStyle);
        foreach (var (key, value) in new (string, double)[]
        {
            ("3928:m_baseVec@0", 1), ("3928:m_baseVec@1", 0), ("3928:m_upVec@0", 0), ("3928:m_upVec@1", 1),
            ("3928:m_orig@0", 0.5), ("3928:m_orig@1", 0.6), ("3928:m_lineWidth", 0.4), ("3928:m_height", 0.1),
        }) Real(state, Note, key, value);
        Real(state, NoteStyle, "3931:m_leaderOffsetSheet", 0.01);
        Int(state, NoteStyle, "625:m_categoryId", 80);
        Item(state, NoteStyle).ElementIds.Add(((int)BuiltInParameter.LEADER_ARROWHEAD, (int)LeaderArrow));
        Int(state, LeaderArrow, "2295:m_tickType", 8);
        Parameter(state, LeaderArrow, BuiltInParameter.ARROW_SIZE, 0.007);
        Parameter(state, LeaderArrow, BuiltInParameter.LEADER_ARROW_WIDTH, 0.3);
        return state;
    }

    private static readonly Dictionary<string, Action<ParsingState>> NoteDefects = new()
    {
        ["no_text"] = s => Item(s, Note).PlanNoteText = null,
        ["empty_text"] = s => Item(s, Note).PlanNoteText = ("", 12),
        ["no_font_size"] = s => Item(s, Note).PlanNoteText = ("Note", null),
        ["font_size_zero"] = s => Item(s, Note).PlanNoteText = ("Note", 0),
        ["font_size_huge"] = s => Item(s, Note).PlanNoteText = ("Note", 201),
        ["other_id"] = s => Int(s, Note, "m_id", 6),
        ["no_view"] = s => Int(s, Note, "m_ownerDBViewId", 0),
        ["flags"] = s => Int(s, Note, "3928:m_flags", 8769),
        ["base_vector"] = s => Real(s, Note, "3928:m_baseVec@0", 0),
        ["base_vector_y"] = s => Real(s, Note, "3928:m_baseVec@1", 1),
        ["up_vector"] = s => Real(s, Note, "3928:m_upVec@1", 0),
        ["up_vector_x"] = s => Real(s, Note, "3928:m_upVec@0", 1),
        ["no_style"] = s => Int(s, Note, "3928:m_attrId", 0),
        ["style_missing"] = s => s.NativeParams.Remove(NoteStyle),
        ["position_far"] = s => Real(s, Note, "3928:m_orig@0", 1_000_000),
        ["position_not_a_number"] = s => Real(s, Note, "3928:m_orig@1", double.NaN),
        ["no_position"] = s => Item(s, Note).DoubleFields.Remove("3928:m_orig@0"),
        ["width_zero"] = s => Real(s, Note, "3928:m_lineWidth", 0),
        ["width_wide"] = s => Real(s, Note, "3928:m_lineWidth", 20),
        ["height_zero"] = s => Real(s, Note, "3928:m_height", 0),
        ["height_tall"] = s => Real(s, Note, "3928:m_height", 20),
        ["padding_negative"] = s => Real(s, NoteStyle, "3931:m_leaderOffsetSheet", -0.01),
        ["padding_missing"] = s => Item(s, NoteStyle).DoubleFields.Remove("3931:m_leaderOffsetSheet"),
        ["padding_foot"] = s => Real(s, NoteStyle, "3931:m_leaderOffsetSheet", 1),
        ["no_category"] = s => Int(s, NoteStyle, "625:m_categoryId", 0),
    };

    public static IEnumerable<object[]> NoteDefectNames => Names(NoteDefects.Keys);

    [Fact]
    public void Note_KeepsItsTextPositionAndStyle()
    {
        var state = Notes();
        Item(state, Note).PlanNoteLeaders = [new([1, 1, 0], [0.8, 1, 0], [0.5, 0.6, 0])];
        Int(state, Note, "3971:m_bArcLeaders", 1);
        Int(state, NoteStyle, "3931:m_textBoxVisibility", 1);
        Int(state, NoteStyle, "625:m_background", 0);

        var note = Assert.Single(ElementBuilder.ReadPlanNotes(state)[(int)View]);

        Assert.Equal(("Note", 12d, 0.5, 0.6, 0.4, 0.1), (note.Text, note.FontPoints, note.X, note.Y, note.WidthFeet, note.HeightFeet));
        Assert.True(note.Wrap);
        Assert.True(note.HasBorder);
        Assert.True(note.Opaque);
        Assert.True(note.ArcLeaders);
        Assert.Equal(0.01, note.PaddingFeet);
        Assert.Equal((80, (int)Note), (note.CategoryId, note.ElementId));
        Assert.Equal((0.007, 0.3), (note.ArrowSizeFeet, note.ArrowAngle));
        Assert.Single(note.Leaders);
    }

    [Fact]
    public void Note_WithoutBoxFlagsIsUnwrappedTransparentAndBorderless()
    {
        var state = Notes();
        Int(state, Note, "3928:m_bNoWrapping", 1);
        Int(state, NoteStyle, "3931:m_textBoxVisibility", 0);
        Int(state, NoteStyle, "625:m_background", 1);

        var note = Assert.Single(ElementBuilder.ReadPlanNotes(state)[(int)View]);

        Assert.False(note.Wrap);
        Assert.False(note.HasBorder);
        Assert.False(note.Opaque);
        Assert.False(note.ArcLeaders);
        Assert.Empty(note.Leaders);
    }

    [Fact]
    public void Note_WithoutALeaderArrowKeepsItsTextWithoutAnArrow()
    {
        foreach (var defect in new Action<ParsingState>[]
        {
            s => Item(s, NoteStyle).ElementIds.Clear(),
            s => Int(s, LeaderArrow, "2295:m_tickType", 7),
            s => Parameter(s, LeaderArrow, BuiltInParameter.ARROW_SIZE, 1),
            s => Parameter(s, LeaderArrow, BuiltInParameter.LEADER_ARROW_WIDTH, Math.PI),
            s => Parameter(s, LeaderArrow, BuiltInParameter.LEADER_ARROW_WIDTH, 0),
            s => s.NativeParams.Remove(LeaderArrow),
        })
        {
            var state = Notes();
            defect(state);

            var note = Assert.Single(ElementBuilder.ReadPlanNotes(state)[(int)View]);

            Assert.Equal((0d, 0d), (note.ArrowSizeFeet, note.ArrowAngle));
        }
    }

    [Theory]
    [MemberData(nameof(NoteDefectNames))]
    public void Note_OutsideTheSupportedLayoutIsLeftOut(string defect)
    {
        var state = Notes();
        NoteDefects[defect](state);

        Assert.Empty(ElementBuilder.ReadPlanNotes(state));
    }

    // Elevation marks

    private const uint Marker = 100, MarkerType = 101, MarkerStyle = 102, MarkerBody = 103, MarkerArrow = 110;

    private static ParsingState Marks()
    {
        var state = new ParsingState();
        state.ElemClassIdx[Marker] = 2247;
        Real(state, Marker, "2247:m_pos@0", 1);
        Real(state, Marker, "2247:m_pos@1", 2);
        Int(state, Marker, "2247:m_symbolId", (int)MarkerType);
        Int(state, Marker, "2247:m_arrowIds@2", (int)MarkerArrow);
        Int(state, MarkerType, "1020:m_elevatnAttrId", (int)MarkerStyle);
        Int(state, MarkerStyle, "2251:m_shape", 1);
        Int(state, MarkerStyle, "2251:m_elevationSymbolId", (int)MarkerBody);
        Int(state, MarkerStyle, "2251:m_ahFilled", 1);
        Int(state, MarkerStyle, "2251:m_orderedSubIndices@2", 1);
        Real(state, MarkerStyle, "2251:m_ahAngleDouble", 0.5);
        Int(state, MarkerBody, "1829:m_subInstData@1", 104);
        Int(state, MarkerArrow, "2248:m_parentId", (int)Marker);
        Int(state, MarkerArrow, "621:m_dbViewId", (int)View);
        Real(state, MarkerArrow, "2249:m_coarserScale", 0.5);
        return state;
    }

    private static readonly Dictionary<string, Action<ParsingState>> MarkDefects = new()
    {
        ["other_class"] = s => s.ElemClassIdx[Marker] = 2248,
        ["position_not_a_number"] = s => Real(s, Marker, "2247:m_pos@0", double.NaN),
        ["position_infinite"] = s => Real(s, Marker, "2247:m_pos@1", double.PositiveInfinity),
        ["no_position"] = s => Item(s, Marker).DoubleFields.Remove("2247:m_pos@1"),
        ["no_type"] = s => s.NativeParams.Remove(MarkerType),
        ["no_style"] = s => s.NativeParams.Remove(MarkerStyle),
        ["style_shape"] = s => Int(s, MarkerStyle, "2251:m_shape", 2),
        ["angle_zero"] = s => Real(s, MarkerStyle, "2251:m_ahAngleDouble", 0),
        ["angle_right"] = s => Real(s, MarkerStyle, "2251:m_ahAngleDouble", Math.PI / 2),
        ["no_body"] = s => s.NativeParams.Remove(MarkerBody),
        ["body_not_set"] = s => Int(s, MarkerStyle, "2251:m_elevationSymbolId", 0),
        ["no_arrow"] = s => Item(s, Marker).Fields.Remove("2247:m_arrowIds@2"),
        // A second slot names an arrow too; the first one is complete, so taking it would accept the marker.
        ["two_arrows"] = s => { Int(s, Marker, "2247:m_arrowIds@0", (int)MarkerArrow); Int(s, MarkerStyle, "2251:m_orderedSubIndices@0", 1); },
        ["second_arrow_unknown"] = s => Int(s, Marker, "2247:m_arrowIds@0", 111),
        ["arrow_missing"] = s => s.NativeParams.Remove(MarkerArrow),
        ["arrow_of_another_marker"] = s => Int(s, MarkerArrow, "2248:m_parentId", 999),
        ["arrow_without_view"] = s => Int(s, MarkerArrow, "621:m_dbViewId", 0),
        ["no_scale"] = s => Item(s, MarkerArrow).DoubleFields.Remove("2249:m_coarserScale"),
        ["scale_above_one"] = s => Real(s, MarkerArrow, "2249:m_coarserScale", 1.5),
        ["scale_negative"] = s => Real(s, MarkerArrow, "2249:m_coarserScale", -0.1),
        ["no_sub_index"] = s => Item(s, MarkerStyle).Fields.Remove("2251:m_orderedSubIndices@2"),
        ["no_symbol"] = s => Item(s, MarkerBody).Fields.Remove("1829:m_subInstData@1"),
        ["symbol_not_set"] = s => Int(s, MarkerBody, "1829:m_subInstData@1", 0),
    };

    public static IEnumerable<object[]> MarkDefectNames => Names(MarkDefects.Keys);

    [Fact]
    public void ElevationMark_IsReadWithItsViewAndSymbol()
    {
        var mark = Assert.Single(ElementBuilder.ReadPlanElevationMarks(Marks()));

        Assert.Equal(new RevitFileInfo.PlanElevationMark((int)Marker, 1, 2, (int)MarkerBody, 0.5, true,
            (int)MarkerArrow, (int)View, 104, 0.5), mark);
    }

    [Fact]
    public void ElevationMark_TakesTheArrowAtAnyOfTheFourSlots()
    {
        for (int slot = 0; slot < 4; slot++)
        {
            var state = Marks();
            Item(state, Marker).Fields.Remove("2247:m_arrowIds@2");
            Int(state, Marker, $"2247:m_arrowIds@{slot}", (int)MarkerArrow);
            Item(state, MarkerStyle).Fields.Remove("2251:m_orderedSubIndices@2");
            Int(state, MarkerStyle, $"2251:m_orderedSubIndices@{slot}", 1);

            Assert.Equal(104, Assert.Single(ElementBuilder.ReadPlanElevationMarks(state)).ArrowSymbolId);
        }
    }

    [Fact]
    public void ElevationMark_IsUnfilledWhenTheStyleHasNoFilledHead()
    {
        var state = Marks();
        Int(state, MarkerStyle, "2251:m_ahFilled", 0);

        Assert.False(Assert.Single(ElementBuilder.ReadPlanElevationMarks(state)).Filled);
    }

    [Fact]
    public void ElevationMark_AcceptsTheLimitsOfTheCoarserScale()
    {
        foreach (double scale in new[] { 0d, 1d })
        {
            var state = Marks();
            Real(state, MarkerArrow, "2249:m_coarserScale", scale);

            Assert.Equal(scale, Assert.Single(ElementBuilder.ReadPlanElevationMarks(state)).CoarserScale);
        }
    }

    [Theory]
    [MemberData(nameof(MarkDefectNames))]
    public void ElevationMark_OutsideTheSupportedShapeIsLeftOut(string defect)
    {
        var state = Marks();
        MarkDefects[defect](state);

        Assert.Empty(ElementBuilder.ReadPlanElevationMarks(state));
    }

    // Stair paths

    private const uint Path = 200, PathType = 201, PathArrow = 202, PathTextType = 203, PathFont = 204, Stair = 300;

    private static ParsingState Paths()
    {
        var state = new ParsingState();
        Int(state, Path, "3702:m_stairsLinkElementId", (int)Stair);
        Int(state, Path, "m_id", (int)Path);
        Int(state, Path, "m_ownerDBViewId", (int)View);
        Int(state, Path, "3702:m_typeId", (int)PathType);
        Real(state, Path, "3702:m_stairsPathOffset", 0);
        foreach (var (key, value) in new (string, int)[]
        {
            ("3703:m_stairsPathDirection", 1), ("3703:m_startFromRiser", 1), ("3703:m_endAtRiser", 1),
            ("3703:m_startSymbolTypeId", -1), ("3703:m_fullStepArrow", 0), ("3703:m_arrowheadTypeId", (int)PathArrow),
        }) Int(state, PathType, key, value);
        Real(state, PathType, "3703:m_startExtension", 0);
        Int(state, PathArrow, "2295:m_tickType", 8);
        Int(state, PathArrow, "2295:m_arrowClosed", 0);
        Int(state, PathArrow, "2295:m_arrowFilled", 0);
        Parameter(state, PathArrow, BuiltInParameter.ARROW_SIZE, 0.008);
        Parameter(state, PathArrow, BuiltInParameter.LEADER_ARROW_WIDTH, 0.4);
        return state;
    }

    /// <summary>The stair path with the text it shows near the arrow: "UP" in Arial, set a little off the path.</summary>
    private static ParsingState PathsWithText()
    {
        var state = Paths();
        Int(state, Path, "3702:m_showDownText", 1);
        Int(state, Path, "3702:m_textOrientation", 0);
        Text(state, Path, "3702:m_downText", "UP");
        Real(state, Path, "3702:m_downTextOffset@0", 0.1);
        Real(state, Path, "3702:m_downTextOffset@1", 0.2);
        Int(state, PathType, "3703:m_textTypeId", (int)PathTextType);
        Int(state, PathTextType, "625:m_fontId", (int)PathFont);
        Int(state, PathTextType, "625:m_bBold", 0);
        Int(state, PathTextType, "625:m_bItalic", 0);
        Int(state, PathTextType, "625:m_bUnderline", 0);
        Text(state, PathFont, "1923:m_name", "Arial");
        Real(state, PathFont, "1923:m_size", 0.009);
        return state;
    }

    private static readonly Dictionary<string, Action<ParsingState>> PathDefects = new()
    {
        ["no_stair"] = s => Int(s, Path, "3702:m_stairsLinkElementId", 0),
        ["other_id"] = s => Int(s, Path, "m_id", 201),
        ["no_view"] = s => Int(s, Path, "m_ownerDBViewId", 0),
        ["offset"] = s => Real(s, Path, "3702:m_stairsPathOffset", 0.5),
        ["no_offset"] = s => Item(s, Path).DoubleFields.Remove("3702:m_stairsPathOffset"),
        ["no_type"] = s => Int(s, Path, "3702:m_typeId", 0),
        ["type_missing"] = s => s.NativeParams.Remove(PathType),
        ["direction"] = s => Int(s, PathType, "3703:m_stairsPathDirection", 0),
        ["start_not_at_riser"] = s => Int(s, PathType, "3703:m_startFromRiser", 0),
        ["end_not_at_riser"] = s => Int(s, PathType, "3703:m_endAtRiser", 0),
        ["start_symbol"] = s => Int(s, PathType, "3703:m_startSymbolTypeId", 5),
        ["full_step_arrow"] = s => Int(s, PathType, "3703:m_fullStepArrow", 1),
        ["no_full_step_arrow"] = s => Item(s, PathType).Fields.Remove("3703:m_fullStepArrow"),
        ["start_extension"] = s => Real(s, PathType, "3703:m_startExtension", 0.2),
        ["no_arrow"] = s => Int(s, PathType, "3703:m_arrowheadTypeId", 0),
        ["arrow_missing"] = s => s.NativeParams.Remove(PathArrow),
        ["arrow_tick_type"] = s => Int(s, PathArrow, "2295:m_tickType", 1),
        ["arrow_closed"] = s => Int(s, PathArrow, "2295:m_arrowClosed", 1),
        ["arrow_filled"] = s => Int(s, PathArrow, "2295:m_arrowFilled", 1),
        ["arrow_size_foot"] = s => Parameter(s, PathArrow, BuiltInParameter.ARROW_SIZE, 1),
        ["arrow_size_missing"] = s => Parameter(s, PathArrow, BuiltInParameter.ARROW_SIZE, null),
        ["arrow_angle_flat"] = s => Parameter(s, PathArrow, BuiltInParameter.LEADER_ARROW_WIDTH, Math.PI),
        ["arrow_angle_zero"] = s => Parameter(s, PathArrow, BuiltInParameter.LEADER_ARROW_WIDTH, 0),
    };

    public static IEnumerable<object[]> PathDefectNames => Names(PathDefects.Keys);

    // A stair path whose text cannot be drawn keeps its arrow, so each of these leaves the path without the label.
    private static readonly Dictionary<string, Action<ParsingState>> TextDefects = new()
    {
        ["hidden"] = s => Int(s, Path, "3702:m_showDownText", 0),
        ["orientation"] = s => Int(s, Path, "3702:m_textOrientation", 1),
        ["no_orientation"] = s => Item(s, Path).Fields.Remove("3702:m_textOrientation"),
        ["empty"] = s => Text(s, Path, "3702:m_downText", ""),
        ["no_text_type"] = s => Int(s, PathType, "3703:m_textTypeId", 0),
        ["text_type_missing"] = s => s.NativeParams.Remove(PathTextType),
        ["no_font"] = s => Int(s, PathTextType, "625:m_fontId", 0),
        ["bold"] = s => Int(s, PathTextType, "625:m_bBold", 1),
        ["no_bold_flag"] = s => Item(s, PathTextType).Fields.Remove("625:m_bBold"),
        ["italic"] = s => Int(s, PathTextType, "625:m_bItalic", 1),
        ["no_italic_flag"] = s => Item(s, PathTextType).Fields.Remove("625:m_bItalic"),
        ["underline"] = s => Int(s, PathTextType, "625:m_bUnderline", 1),
        ["font_missing"] = s => s.NativeParams.Remove(PathFont),
        ["font_name"] = s => Text(s, PathFont, "1923:m_name", "Times New Roman"),
        ["no_font_name"] = s => Item(s, PathFont).StringFields.Remove("1923:m_name"),
        ["height_zero"] = s => Real(s, PathFont, "1923:m_size", 0),
        ["height_foot"] = s => Real(s, PathFont, "1923:m_size", 1),
        ["offset_not_a_number"] = s => Real(s, Path, "3702:m_downTextOffset@0", double.NaN),
        ["offset_infinite"] = s => Real(s, Path, "3702:m_downTextOffset@1", double.PositiveInfinity),
        ["no_offset"] = s => Item(s, Path).DoubleFields.Remove("3702:m_downTextOffset@1"),
    };

    public static IEnumerable<object[]> TextDefectNames => Names(TextDefects.Keys);

    [Fact]
    public void StairPath_IsReadWithItsArrow()
    {
        var path = Assert.Single(ElementBuilder.ReadPlanStairPaths(Paths())[(int)View]);

        Assert.Equal(new RevitFileInfo.PlanStairPath((int)Path, (int)Stair, 0.008, 0.4, null, 0, 0, 0), path);
    }

    [Fact]
    public void StairPath_ReadsTheTextAndItsOffsetInArial()
    {
        var path = Assert.Single(ElementBuilder.ReadPlanStairPaths(PathsWithText())[(int)View]);

        Assert.Equal(new RevitFileInfo.PlanStairPath((int)Path, (int)Stair, 0.008, 0.4, "UP", 0.009, 0.1, 0.2), path);
    }

    [Fact]
    public void StairPath_TakesTheFontNameWithoutRegardToCase()
    {
        var state = PathsWithText();
        Text(state, PathFont, "1923:m_name", "ARIAL");

        Assert.Equal("UP", Assert.Single(ElementBuilder.ReadPlanStairPaths(state)[(int)View]).DownText);
    }

    [Fact]
    public void StairPaths_GroupByView()
    {
        var state = Paths();
        foreach (uint id in new uint[] { 210, 211 })
        {
            var copy = Item(state, id);
            foreach (var (key, value) in Item(state, Path).Fields) copy.Fields[key] = value;
            foreach (var (key, value) in Item(state, Path).DoubleFields) copy.DoubleFields[key] = value;
            copy.Fields["m_id"] = (int)id;
            if (id == 211) copy.Fields["m_ownerDBViewId"] = 21;
        }

        var paths = ElementBuilder.ReadPlanStairPaths(state);

        Assert.Equal(new[] { (int)Path, 210 }, paths[(int)View].Select(path => path.Id).Order().ToArray());
        Assert.Equal(211, Assert.Single(paths[21]).Id);
    }

    [Theory]
    [MemberData(nameof(PathDefectNames))]
    public void StairPath_OutsideTheSupportedShapeIsLeftOut(string defect)
    {
        var state = Paths();
        PathDefects[defect](state);

        Assert.Empty(ElementBuilder.ReadPlanStairPaths(state));
    }

    [Theory]
    [MemberData(nameof(TextDefectNames))]
    public void StairPath_WithATextItCannotDrawKeepsItsArrow(string defect)
    {
        var state = PathsWithText();
        TextDefects[defect](state);

        var path = Assert.Single(ElementBuilder.ReadPlanStairPaths(state)[(int)View]);

        // The offset only matters with a text, so a path whose offset is not finite keeps it as read.
        Assert.Null(path.DownText);
        Assert.Equal(0d, path.CapHeight);
        Assert.Equal(0.008, path.ArrowSize);
    }

    // Room colors

    private const uint ColorView = 400, ColorScheme = 401;

    private static TfBlockDeserializer.RoomColorEntry Entry(string name, uint color, int pattern) => new(name, color, pattern);

    private static (RevitFileInfo Info, ParsingState State) Colors(params TfBlockDeserializer.RoomColorEntry[] entries)
    {
        var info = new RevitFileInfo
        {
            FillPatterns = new Dictionary<int, (string Name, IReadOnlyList<TfBlockDeserializer.PatternGrid> Grids)>
            {
                [7] = ("Solid fill", []),
                [8] = ("Hatch", [new(0, 1, 2, 3, 4, [], Target: 1, Orientation: 0)]),
            },
        };
        var state = new ParsingState();
        Int(state, ColorView, "693:m_bBackground", 1);
        Int(state, ColorView, "693:m_roomColorScheme", (int)ColorScheme);
        Int(state, ColorScheme, "690:m_categoryId", (int)BuiltInCategory.OST_Rooms);
        Int(state, ColorScheme, "690:m_paramId", (int)BuiltInParameter.ROOM_NAME);
        Int(state, ColorScheme, "690:m_bByRange", 0);
        Item(state, ColorScheme).RoomColors = [.. entries];
        return (info, state);
    }

    public static IEnumerable<object[]> ColorSchemeDefects => Names(
        ["no_background", "no_scheme", "scheme_missing", "no_entries", "other_category", "other_parameter", "by_range", "no_range_flag"]);

    [Fact]
    public void RoomColors_MapRoomNamesToTheirSolidFills()
    {
        var (info, state) = Colors(Entry("Office", 0xFF0000, 7), Entry("Store", 0x00FF00, 7));

        var colors = Assert.Single(ElementBuilder.BuildViewRoomColors(info, state));

        Assert.Equal((int)ColorView, colors.Key);
        Assert.Equal(0xFF0000u, colors.Value["Office"]);
        Assert.Equal(0x00FF00u, colors.Value["Store"]);
    }

    [Fact]
    public void RoomColors_LeaveOutPatternsThatAreNotSolid()
    {
        var (info, state) = Colors(Entry("Hatched", 0xFF0000, 8), Entry("Unknown", 0x0000FF, 99), Entry("Solid", 0x00FF00, 7));

        var colors = Assert.Single(ElementBuilder.BuildViewRoomColors(info, state)).Value;

        Assert.Equal(new[] { "Solid" }, colors.Keys);
    }

    [Fact]
    public void RoomColors_KeepTheFirstColorOfARepeatedName()
    {
        var (info, state) = Colors(Entry("Office", 0xFF0000, 7), Entry("Office", 0x0000FF, 7));

        Assert.Equal(0xFF0000u, Assert.Single(ElementBuilder.BuildViewRoomColors(info, state)).Value["Office"]);
    }

    [Fact]
    public void RoomColors_MatchRoomNamesExactly()
    {
        var (info, state) = Colors(Entry("Office", 0xFF0000, 7));

        var colors = Assert.Single(ElementBuilder.BuildViewRoomColors(info, state)).Value;

        Assert.False(colors.ContainsKey("office"));
    }

    [Fact]
    public void RoomColors_AreEmptyWhenAViewHasNoSolidEntry()
    {
        var (info, state) = Colors(Entry("Hatched", 0xFF0000, 8));

        Assert.Empty(ElementBuilder.BuildViewRoomColors(info, state));
    }

    [Fact]
    public void RoomColors_AreEmptyWithoutTheFillPatternsOfTheFile()
    {
        var (info, state) = Colors(Entry("Office", 0xFF0000, 7));
        info.FillPatterns = null;

        Assert.Empty(ElementBuilder.BuildViewRoomColors(info, state));
    }

    [Theory]
    [MemberData(nameof(ColorSchemeDefects))]
    public void RoomColors_OfAnotherSchemeAreLeftOut(string defect)
    {
        var (info, state) = Colors(Entry("Office", 0xFF0000, 7));
        switch (defect)
        {
            case "no_background": Int(state, ColorView, "693:m_bBackground", 0); break;
            case "no_scheme": Int(state, ColorView, "693:m_roomColorScheme", 0); break;
            case "scheme_missing": state.NativeParams.Remove(ColorScheme); break;
            case "no_entries": Item(state, ColorScheme).RoomColors = []; break;
            case "other_category": Int(state, ColorScheme, "690:m_categoryId", (int)BuiltInCategory.OST_Walls); break;
            case "other_parameter": Int(state, ColorScheme, "690:m_paramId", (int)BuiltInParameter.ROOM_NUMBER); break;
            case "by_range": Int(state, ColorScheme, "690:m_bByRange", 1); break;
            case "no_range_flag": Item(state, ColorScheme).Fields.Remove("690:m_bByRange"); break;
            default: throw new ArgumentOutOfRangeException(nameof(defect));
        }

        Assert.Empty(ElementBuilder.BuildViewRoomColors(info, state));
    }

    // Contour labels

    private const uint Label = 500, Surface = 501, LabelStyle = 502, LabelFont = 503;

    private static ParsingState Labels()
    {
        var state = new ParsingState();
        Item(state, Label).SiteContourLabels = new(
            (int)Surface, [[1, 2, 0], [3, 4, 0]], [[1, 0, 0], [0, 1, 0]]);
        Int(state, Label, "m_id", (int)Label);
        Int(state, Label, "m_ownerDBViewId", (int)View);
        Int(state, Label, "m_attrId", (int)LabelStyle);
        Item(state, Surface).SiteContours = [];
        Int(state, LabelStyle, "836:m_baseElevation", 0);
        Int(state, LabelStyle, "84:m_bUseDefault", 1);
        Int(state, LabelStyle, "625:m_bBold", 0);
        Int(state, LabelStyle, "625:m_bItalic", 0);
        Int(state, LabelStyle, "625:m_bUnderline", 0);
        Int(state, LabelStyle, "625:m_fontId", (int)LabelFont);
        Real(state, LabelFont, "1923:m_size", 0.01);
        return state;
    }

    private static readonly Dictionary<string, Action<ParsingState>> LabelDefects = new()
    {
        ["other_id"] = s => Int(s, Label, "m_id", 501),
        ["no_view"] = s => Int(s, Label, "m_ownerDBViewId", 0),
        ["no_style_id"] = s => Int(s, Label, "m_attrId", 0),
        ["style_missing"] = s => s.NativeParams.Remove(LabelStyle),
        ["surface_missing"] = s => s.NativeParams.Remove(Surface),
        ["surface_without_contours"] = s => Item(s, Surface).SiteContours = null,
        ["base_elevation"] = s => Int(s, LabelStyle, "836:m_baseElevation", 1),
        ["no_base_elevation"] = s => Item(s, LabelStyle).Fields.Remove("836:m_baseElevation"),
        ["own_format"] = s => Int(s, LabelStyle, "84:m_bUseDefault", 0),
        ["bold"] = s => Int(s, LabelStyle, "625:m_bBold", 1),
        ["italic"] = s => Int(s, LabelStyle, "625:m_bItalic", 1),
        ["underline"] = s => Int(s, LabelStyle, "625:m_bUnderline", 1),
        ["no_font"] = s => Int(s, LabelStyle, "625:m_fontId", 0),
        ["font_missing"] = s => s.NativeParams.Remove(LabelFont),
        ["height_zero"] = s => Real(s, LabelFont, "1923:m_size", 0),
        ["height_foot"] = s => Real(s, LabelFont, "1923:m_size", 1),
    };

    public static IEnumerable<object[]> LabelDefectNames => Names(LabelDefects.Keys);

    [Fact]
    public void ContourLabels_ArePlacedAtEachPositionOfTheElement()
    {
        var labels = Assert.Single(ElementBuilder.BuildSiteContourLabels(Labels()));

        Assert.Equal((int)View, labels.Key);
        Assert.Equal(2, labels.Value.Count);
        Assert.Equal(new double[] { 1, 2, 0 }, labels.Value[0].Position);
        Assert.Equal(new double[] { 0, 1, 0 }, labels.Value[1].Direction);
        Assert.All(labels.Value, label => Assert.Equal(((int)Label, (int)Surface, 0.01), (label.ElementId, label.SurfaceId, label.TextHeightFeet)));
    }

    [Theory]
    [MemberData(nameof(LabelDefectNames))]
    public void ContourLabel_OutsideTheSupportedStyleIsLeftOut(string defect)
    {
        var state = Labels();
        LabelDefects[defect](state);

        Assert.Empty(ElementBuilder.BuildSiteContourLabels(state));
    }
}
