using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The rows of an itemized schedule (ElementBuilder.BuildScheduleRows, EvaluateScheduleRows) and the revision text
/// sizes of its table. The reader evaluates only the schedules it can reproduce, so each unsupported setting of the
/// definition has to leave the schedule without rows, never with rows that differ from what Revit lists.</summary>
public class ScheduleRowsTests
{
    private const int Mark = 1001, Count = 1002, Flag = 1003, Hidden = 1004;
    private const int Text = 606, Integer = 2756, Boolean = 2866;

    private sealed class Setup
    {
        public TfBlockDeserializer.ScheduleTableDef Table = null!;
        public TfBlockDeserializer.ScheduleRowBindings Bindings = null!;
        public List<Element> Elements = [];
        public ParsingState State = new();
        public List<List<RevitFileInfo.ScheduleCellValue>>? Rows() => ElementBuilder.BuildScheduleRows(Table, Elements, State);
    }

    private static Setup Schedule(int spacer = 0)
    {
        var setup = new Setup { State = new ParsingState() };
        var bindings = new TfBlockDeserializer.ScheduleRowBindings
        {
            Itemized = true,
            InitialSpacer = spacer > 0,
            FieldIds = [10, 11, 12, 13],
            FieldObjects = [100, 101, 102, 103],
        };
        bindings.Fields[100] = (Mark, false, 0);
        bindings.Fields[101] = (Count, false, 0);
        bindings.Fields[102] = (Flag, false, 0);
        bindings.Fields[103] = (Hidden, true, 0);
        setup.Bindings = bindings;
        foreach (var (parameter, kind) in new[] { (Mark, Text), (Count, Integer), (Flag, Boolean), (Hidden, Integer) })
            Define(setup.State, parameter, kind);
        setup.Table = Table(bindings, [new([], 0, 3 + spacer)], 3);
        // Three doors; the middle one has the smallest mark.
        setup.Elements = [Door(11, "B", 2, 1, 1, setup.State), Door(12, "A", 5, 0, 1, setup.State), Door(13, "C", 7, 1, 1, setup.State)];
        return setup;
    }

    private static TfBlockDeserializer.ScheduleTableDef Table(TfBlockDeserializer.ScheduleRowBindings? bindings,
        List<TfBlockDeserializer.ScheduleBodySection> sections, int columns, int category = (int)BuiltInCategory.OST_Doors)
        => new([.. Enumerable.Range(0, columns).Select(i => ($"Column {i}", 0.1))], category, true, true, true, true,
            1, 2, 0.1, [], sections, 0) { RowBindings = bindings };

    private static void Define(ParsingState state, int parameter, int kind)
    {
        var definition = new TfBlockDeserializer.NativeParams();
        definition.Fields["2870:m_pParamDef.class"] = kind;
        state.NativeParams[(uint)parameter] = definition;
    }

    private static Element Door(int id, string mark, int count, int flag, int hidden, ParsingState state,
        int category = (int)BuiltInCategory.OST_Doors, string root = "FamilyInstance", bool complete = true)
    {
        var native = new TfBlockDeserializer.NativeParams { TailWalkComplete = complete };
        native.NamedParams.Add(new(Mark, mark, 0, 0, 0, true, false));
        native.NamedParams.Add(new(Count, null, 0, 0, count, true, false));
        native.NamedParams.Add(new(Flag, null, 0, 0, flag, true, false));
        native.NamedParams.Add(new(Hidden, null, 0, 0, hidden, true, false));
        state.NativeParams[(uint)id] = native;
        return new Element { Id = id, CategoryId = category, NativeRootClassName = root };
    }

    private static string[][] Cells(List<List<RevitFileInfo.ScheduleCellValue>>? rows)
    {
        Assert.NotNull(rows);
        return [.. rows.Select(row => row.Select(cell => cell.Boolean is { } flag ? (flag ? "yes" : "no") : cell.Text).ToArray())];
    }

    [Fact]
    public void Rows_ListTheInstancesWithTheirVisibleColumns()
    {
        var setup = Schedule();
        setup.Bindings.SortObjects = [200];
        setup.Bindings.Sorts[200] = (10, 0, 0);

        Assert.Equal(new[] { new[] { "A", "5", "no" }, new[] { "B", "2", "yes" }, new[] { "C", "7", "yes" } }, Cells(setup.Rows()));
    }

    [Fact]
    public void Rows_AreInOrderOfElementIdWithoutASort()
    {
        var setup = Schedule();

        Assert.Equal(new[] { "B", "A", "C" }, Cells(setup.Rows()).Select(row => row[0]).ToArray());
    }

    [Fact]
    public void Rows_FollowTheSortOrderOfTheField()
    {
        var setup = Schedule();
        setup.Bindings.SortObjects = [200];
        setup.Bindings.Sorts[200] = (10, 0, 1);

        Assert.Equal(new[] { "C", "B", "A" }, Cells(setup.Rows()).Select(row => row[0]).ToArray());
    }

    [Fact]
    public void Rows_SortNumbersAsNumbers()
    {
        var setup = Schedule();
        setup.Bindings.SortObjects = [200];
        setup.Bindings.Sorts[200] = (11, 0, 0);

        Assert.Equal(new[] { "2", "5", "7" }, Cells(setup.Rows()).Select(row => row[1]).ToArray());
    }

    [Fact]
    public void Rows_SortNumbersByValueRatherThanByTheirDigits()
    {
        var setup = Schedule();
        // The element ids list 10, 9, 2: only a numeric sort puts 9 before 10.
        setup.Elements = [Door(11, "B", 10, 1, 1, setup.State), Door(12, "A", 9, 0, 1, setup.State), Door(13, "C", 2, 1, 1, setup.State)];
        setup.Bindings.SortObjects = [200];
        setup.Bindings.Sorts[200] = (11, 0, 0);
        Assert.Equal(new[] { "2", "9", "10" }, Cells(setup.Rows()).Select(row => row[1]).ToArray());

        setup.Bindings.Sorts[200] = (11, 0, 1);
        Assert.Equal(new[] { "10", "9", "2" }, Cells(setup.Rows()).Select(row => row[1]).ToArray());
    }

    [Fact]
    public void Rows_SortByTheFirstFieldAndThenTheNext()
    {
        var setup = Schedule();
        setup.Elements.Add(Door(14, "A", 1, 0, 1, setup.State));
        setup.Table = Table(setup.Bindings, [new([], 0, 4)], 3);
        setup.Bindings.SortObjects = [200, 201];
        setup.Bindings.Sorts[200] = (10, 0, 0);
        setup.Bindings.Sorts[201] = (11, 0, 1);

        Assert.Equal(new[] { "A5", "A1", "B2", "C7" }, Cells(setup.Rows()).Select(row => row[0] + row[1]).ToArray());
    }

    [Fact]
    public void Rows_OfEqualKeysFollowTheElementId()
    {
        var setup = Schedule();
        setup.Elements = [Door(31, "A", 1, 0, 1, setup.State), Door(30, "A", 2, 0, 1, setup.State), Door(32, "A", 3, 0, 1, setup.State)];
        setup.Bindings.SortObjects = [200];
        setup.Bindings.Sorts[200] = (10, 0, 0);

        Assert.Equal(new[] { "2", "1", "3" }, Cells(setup.Rows()).Select(row => row[1]).ToArray());
    }

    [Fact]
    public void Rows_StartWithAnEmptyRowWhenTheScheduleHasAnInitialSpacer()
    {
        var setup = Schedule(spacer: 1);
        setup.Bindings.SortObjects = [200];
        setup.Bindings.Sorts[200] = (10, 0, 0);

        var rows = Cells(setup.Rows());

        Assert.Equal(4, rows.Length);
        Assert.Equal(new[] { "", "", "" }, rows[0]);
        Assert.Equal("A", rows[1][0]);
    }

    [Theory]
    [InlineData(2, new[] { "A" })]            // the filter keeps the doors with the value
    [InlineData(3, new[] { "B", "C" })]       // and the other filter type the doors without it
    public void Rows_KeepTheInstancesTheFilterAdmits(int type, string[] marks)
    {
        var setup = Schedule();
        setup.Bindings.SortObjects = [200];
        setup.Bindings.Sorts[200] = (10, 0, 0);
        setup.Bindings.FilterObjects = [300];
        setup.Bindings.Filters[300] = (12, type, 0, 1);   // the flag column: type 2 lists the doors whose flag is 0
        setup.Table = Table(setup.Bindings, [new([], 0, marks.Length)], 3);

        Assert.Equal(marks, Cells(setup.Rows()).Select(row => row[0]).ToArray());
    }

    [Fact]
    public void Rows_AcceptAFilterOnAHiddenIntegerField()
    {
        var setup = Schedule();
        setup.Elements[1] = Door(12, "A", 5, 0, 9, setup.State);
        setup.Bindings.FilterObjects = [300];
        setup.Bindings.Filters[300] = (13, 3, 9, 1);
        setup.Table = Table(setup.Bindings, [new([], 0, 2)], 3);

        Assert.Equal(new[] { "B", "C" }, Cells(setup.Rows()).Select(row => row[0]).ToArray());
    }

    [Fact]
    public void Rows_LeaveOutInstancesTheScheduleDoesNotList()
    {
        var setup = Schedule();
        setup.Elements.Add(Door(40, "X", 1, 0, 1, setup.State, category: (int)BuiltInCategory.OST_Windows));
        setup.Elements.Add(Door(41, "Y", 1, 0, 1, setup.State, root: "Wall"));
        setup.Elements.Add(Door(42, "Z", 1, 0, 1, setup.State, complete: false));
        setup.Elements.Add(new Element { Id = 43, CategoryId = (int)BuiltInCategory.OST_Doors, NativeRootClassName = "FamilyInstance" });

        Assert.Equal(3, setup.Rows()!.Count);
    }

    [Fact]
    public void Rows_LeaveOutAnInstanceWithoutAParameterOfTheColumnsOrTheSorts()
    {
        var setup = Schedule();
        setup.State.NativeParams[11].NamedParams.RemoveAll(parameter => parameter.ParamId == Count);
        setup.Table = Table(setup.Bindings, [new([], 0, 2)], 3);

        Assert.Equal(new[] { "A", "C" }, Cells(setup.Rows()).Select(row => row[0]).ToArray());

        setup.Bindings.SortObjects = [200];
        setup.Bindings.Sorts[200] = (13, 0, 0);
        setup.State.NativeParams[12].NamedParams.RemoveAll(parameter => parameter.ParamId == Hidden);
        setup.Table = Table(setup.Bindings, [new([], 0, 1)], 3);

        Assert.Equal(new[] { "C" }, Cells(setup.Rows()).Select(row => row[0]).ToArray());
    }

    [Fact]
    public void Rows_TakeTheFirstValueOfARepeatedParameter()
    {
        var setup = Schedule();
        setup.State.NativeParams[11].NamedParams.Add(new(Mark, "Z", 0, 0, 0, true, false));

        Assert.Equal("B", Cells(setup.Rows())[0][0]);
    }

    public static IEnumerable<object[]> UnsupportedDefinitions => ElementBuilderTestNames(
    [
        "no_bindings", "not_itemized", "unsupported", "two_sections", "no_sections", "field_count", "field_source",
        "field_parameter", "field_definition", "field_kind", "field_object", "duplicate_field", "column_count",
        "filter_missing", "filter_type", "filter_storage", "filter_on_text", "filter_field", "sort_missing",
        "sort_group", "sort_order", "sort_field", "no_instances", "row_count_short", "row_count_long",
    ]);

    private static IEnumerable<object[]> ElementBuilderTestNames(IEnumerable<string> names) => names.Select(name => new[] { name });

    [Theory]
    [MemberData(nameof(UnsupportedDefinitions))]
    public void Rows_OfAnUnsupportedDefinitionAreNotListed(string defect)
    {
        var setup = Schedule();
        setup.Bindings.SortObjects = [200];
        setup.Bindings.Sorts[200] = (10, 0, 0);
        var bindings = setup.Bindings;
        var sections = new List<TfBlockDeserializer.ScheduleBodySection> { new([], 0, 3) };
        int columns = 3;
        switch (defect)
        {
            case "no_bindings": setup.Table = Table(null, sections, columns); break;
            case "not_itemized": bindings.Itemized = false; break;
            case "unsupported": bindings.Unsupported = true; break;
            case "two_sections": sections.Add(new([], 0, 1)); setup.Table = Table(bindings, sections, columns); break;
            case "no_sections": setup.Table = Table(bindings, [], columns); break;
            case "field_count": bindings.FieldObjects = [100, 101, 102]; break;
            case "field_source": bindings.Fields[101] = (Count, false, 1); break;
            case "field_parameter": bindings.Fields[101] = (0, false, 0); break;
            case "field_definition": bindings.Fields[101] = (4004, false, 0); break;
            case "field_kind": Define(setup.State, Count, 1234); break;
            case "field_object": bindings.FieldObjects = [100, 101, 102, 199]; break;
            case "duplicate_field": bindings.FieldIds = [10, 11, 12, 12]; break;
            case "column_count": setup.Table = Table(bindings, sections, 2); break;
            case "filter_missing": bindings.FilterObjects = [300]; break;
            case "filter_type": bindings.FilterObjects = [300]; bindings.Filters[300] = (13, 4, 1, 1); break;
            case "filter_storage": bindings.FilterObjects = [300]; bindings.Filters[300] = (13, 2, 1, 0); break;
            case "filter_on_text": bindings.FilterObjects = [300]; bindings.Filters[300] = (10, 2, 1, 1); break;
            case "filter_field": bindings.FilterObjects = [300]; bindings.Filters[300] = (99, 2, 1, 1); break;
            case "sort_missing": bindings.SortObjects = [201]; break;
            case "sort_group": bindings.Sorts[200] = (10, 1, 0); break;
            case "sort_order": bindings.Sorts[200] = (10, 0, 2); break;
            case "sort_field": bindings.Sorts[200] = (99, 0, 0); break;
            case "no_instances": setup.Elements.Clear(); break;
            case "row_count_short": setup.Table = Table(bindings, [new([], 0, 2)], columns); break;
            case "row_count_long": setup.Table = Table(bindings, [new([], 0, 4)], columns); break;
            default: throw new ArgumentOutOfRangeException(nameof(defect));
        }

        Assert.Null(setup.Rows());
    }

    [Fact]
    public void Evaluate_KeepsTheTablesOfASingleBlockThatHaveRows()
    {
        var setup = Schedule();
        var info = new RevitFileInfo { Elements = setup.Elements };
        setup.State.ScheduleTables[50] = (1, setup.Table);
        setup.State.ScheduleTables[51] = (2, setup.Table);                  // the view was saved in two blocks
        setup.State.ScheduleTables[52] = (1, null);                         // no table decoded
        setup.State.ScheduleTables[53] = (1, Table(null, [new([], 0, 3)], 3)); // no bindings

        var rows = ElementBuilder.EvaluateScheduleRows(info, setup.State);

        Assert.Equal(new[] { 50 }, rows.Keys.ToArray());
        Assert.Equal(3, rows[50].Count);
    }

    // Revision text sizes

    private const int HeaderType = 600, BodyType = 601;

    private static (TfBlockDeserializer.ScheduleTableDef Table, ParsingState State) Revisions()
    {
        var state = new ParsingState();
        foreach (var (type, font, size) in new[] { (HeaderType, 610, 0.01), (BodyType, 611, 0.012) })
        {
            var style = new TfBlockDeserializer.NativeParams();
            style.Fields["625:m_bBold"] = 0;
            style.Fields["625:m_bItalic"] = 0;
            style.Fields["625:m_fontId"] = font;
            state.NativeParams[(uint)type] = style;
            var face = new TfBlockDeserializer.NativeParams();
            face.StringFields["1923:m_name"] = "Arial";
            face.DoubleFields["1923:m_size"] = size;
            state.NativeParams[(uint)font] = face;
        }
        var table = Table(null, [], 1, (int)BuiltInCategory.OST_Revisions) with { TextTypeIds = (HeaderType, BodyType) };
        return (table, state);
    }

    public static IEnumerable<object[]> RevisionTextDefects => ElementBuilderTestNames(
        [.. new[] { "header", "body" }.SelectMany(part => new[]
        {
            "no_type", "type_missing", "bold", "italic", "no_font", "font_missing", "font_name", "size_zero", "size_foot",
        }.Select(defect => $"{part}_{defect}"))]);

    [Fact]
    public void RevisionTextTypes_GiveTheCapHeightsOfTheHeaderAndTheBody()
    {
        var (table, state) = Revisions();

        var resolved = ElementBuilder.ResolveRevisionTextTypes(state, table);

        Assert.Equal((0.01, 0.012), resolved.RevisionCapHeightsFt);
    }

    [Fact]
    public void RevisionTextTypes_TakeTheFontNameWithoutRegardToCase()
    {
        var (table, state) = Revisions();
        state.NativeParams[610].StringFields["1923:m_name"] = "arial";

        Assert.Equal((0.01, 0.012), ElementBuilder.ResolveRevisionTextTypes(state, table).RevisionCapHeightsFt);
    }

    [Fact]
    public void RevisionTextTypes_LeaveOtherSchedulesAsTheyAre()
    {
        var (table, state) = Revisions();
        var other = table with { CategoryId = (int)BuiltInCategory.OST_Doors };

        Assert.Same(other, ElementBuilder.ResolveRevisionTextTypes(state, other));
    }

    [Theory]
    [MemberData(nameof(RevisionTextDefects))]
    public void RevisionTextTypes_OfAnotherStyleLeaveTheTableAsItIs(string defect)
    {
        var (table, state) = Revisions();
        bool header = defect.StartsWith("header_", StringComparison.Ordinal);
        uint type = header ? 600u : 601u, font = header ? 610u : 611u;
        switch (defect[(defect.IndexOf('_') + 1)..])
        {
            case "no_type": table = table with { TextTypeIds = header ? (0, BodyType) : (HeaderType, 0) }; break;
            case "type_missing": state.NativeParams.Remove(type); break;
            case "bold": state.NativeParams[type].Fields["625:m_bBold"] = 1; break;
            case "italic": state.NativeParams[type].Fields["625:m_bItalic"] = 1; break;
            case "no_font": state.NativeParams[type].Fields["625:m_fontId"] = 0; break;
            case "font_missing": state.NativeParams.Remove(font); break;
            case "font_name": state.NativeParams[font].StringFields["1923:m_name"] = "Times New Roman"; break;
            case "size_zero": state.NativeParams[font].DoubleFields["1923:m_size"] = 0; break;
            case "size_foot": state.NativeParams[font].DoubleFields["1923:m_size"] = 1; break;
            default: throw new ArgumentOutOfRangeException(nameof(defect));
        }

        Assert.Null(ElementBuilder.ResolveRevisionTextTypes(state, table).RevisionCapHeightsFt);
    }
}
