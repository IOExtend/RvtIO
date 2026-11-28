using System.Globalization;
using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;

namespace RvtIO.Parsing;

internal static partial class ElementBuilder
{
    internal static TfBlockDeserializer.ScheduleTableDef ResolveRevisionTextTypes(ParsingState state,
        TfBlockDeserializer.ScheduleTableDef table)
    {
        if (table.CategoryId != (int)BuiltInCategory.OST_Revisions
            || CapHeight(table.TextTypeIds.Header) is not double header
            || CapHeight(table.TextTypeIds.Body) is not double body) return table;
        return table with { RevisionCapHeightsFt = (header, body) };

        double? CapHeight(int id)
        {
            if (id <= 0 || !state.NativeParams.TryGetValue((uint)id, out var type)
                || type.Fields.GetValueOrDefault("625:m_bBold") != 0
                || type.Fields.GetValueOrDefault("625:m_bItalic") != 0
                || type.Fields.GetValueOrDefault("625:m_fontId") is not (> 0 and var fontId)
                || !state.NativeParams.TryGetValue((uint)fontId, out var font)
                || !string.Equals(font.StringFields.GetValueOrDefault("1923:m_name"), "Arial", StringComparison.OrdinalIgnoreCase))
                return null;
            double height = font.DoubleFields.GetValueOrDefault("1923:m_size");
            return height is > 0 and < 1 ? height : null;
        }
    }

    internal static Dictionary<int, List<List<RevitFileInfo.ScheduleCellValue>>> EvaluateScheduleRows(RevitFileInfo info, ParsingState state)
    {
        var result = new Dictionary<int, List<List<RevitFileInfo.ScheduleCellValue>>>();
        foreach (var (id, entry) in state.ScheduleTables)
        {
            if (entry is not { BlockCount: 1, Table: { } table }
                || BuildScheduleRows(table, info.Elements, state) is not { } rows) continue;
            result.Add((int)id, rows);
        }
        return result;
    }

    internal static List<List<RevitFileInfo.ScheduleCellValue>>? BuildScheduleRows(TfBlockDeserializer.ScheduleTableDef table,
        IEnumerable<Element> elements, ParsingState state)
    {
        // shortcut: itemized instance rows with flat sorting and integer filters, until other schedule expression types are evaluated.
        if (table.RowBindings is not { Itemized: true, Unsupported: false } binding
            || table.BodySections is not [{ } section] || binding.FieldIds.Count != binding.FieldObjects.Count)
            return null;
        var fields = new Dictionary<int, (int Parameter, bool Hidden, int Kind)>();
        for (int i = 0; i < binding.FieldIds.Count; i++)
        {
            if (!binding.Fields.TryGetValue(binding.FieldObjects[i], out var field) || field.Source != 0
                || field.Parameter <= 0 || !state.NativeParams.TryGetValue((uint)field.Parameter, out var definition)
                || !definition.Fields.TryGetValue("2870:m_pParamDef.class", out int kind)
                || kind is not (606 or 2756 or 2866)
                || !fields.TryAdd(binding.FieldIds[i], (field.Parameter, field.Hidden, kind))) return null;
        }

        var columns = binding.FieldIds.Select(id => fields[id]).Where(f => !f.Hidden).ToArray();
        if (columns.Length != table.Columns.Count) return null;
        var filters = new List<(int Parameter, int Type, int Value)>();
        foreach (int index in binding.FilterObjects)
        {
            if (!binding.Filters.TryGetValue(index, out var filter) || filter is not { Type: 2 or 3, Storage: 1 }
                || !fields.TryGetValue(filter.Field, out var field) || field.Kind is not (2756 or 2866)) return null;
            filters.Add((field.Parameter, filter.Type, filter.Value));
        }

        var sorts = new List<(int Parameter, int Kind, int Order)>();
        foreach (int index in binding.SortObjects)
        {
            if (!binding.Sorts.TryGetValue(index, out var sort) || sort is not { GroupFlags: 0, Order: 0 or 1 }
                || !fields.TryGetValue(sort.Field, out var field)) return null;
            sorts.Add((field.Parameter, field.Kind, sort.Order));
        }

        var items = new List<(int Id, Dictionary<int, TfBlockDeserializer.NamedParamRecord> Values)>();
        foreach (var element in elements)
        {
            if (element.CategoryId != table.CategoryId || element.RootClassName != "FamilyInstance"
                || !state.NativeParams.TryGetValue((uint)element.Id, out var native) || !native.TailWalkComplete) continue;
            var values = new Dictionary<int, TfBlockDeserializer.NamedParamRecord>();
            foreach (var parameter in native.NamedParams)
                values.TryAdd(parameter.ParamId, parameter);
            if (!filters.All(f => values.TryGetValue(f.Parameter, out var value)
                && (f.Type == 2 ? value.Int == f.Value : value.Int != f.Value))) continue;
            if (columns.Any(c => !values.ContainsKey(c.Parameter)) || sorts.Any(s => !values.ContainsKey(s.Parameter))) continue;
            items.Add((element.Id, values));
        }

        int spacer = binding.InitialSpacer ? 1 : 0;
        if (items.Count == 0 || items.Count + spacer != section.BodyRows) return null;
        items.Sort((left, right) =>
        {
            foreach (var sort in sorts)
            {
                var a = left.Values[sort.Parameter];
                var b = right.Values[sort.Parameter];
                int order = sort.Kind == 606 ? string.CompareOrdinal(a.Str, b.Str) : a.Int.CompareTo(b.Int);
                if (order != 0) return sort.Order == 0 ? order : -order;
            }
            return left.Id.CompareTo(right.Id);
        });

        var rows = new List<List<RevitFileInfo.ScheduleCellValue>>();
        if (spacer > 0) rows.Add(Enumerable.Repeat(new RevitFileInfo.ScheduleCellValue(""), columns.Length).ToList());
        foreach (var item in items)
            rows.Add(columns.Select(column =>
            {
                var value = item.Values[column.Parameter];
                return column.Kind switch
                {
                    606 => new RevitFileInfo.ScheduleCellValue(value.Str ?? ""),
                    2866 => new RevitFileInfo.ScheduleCellValue("", value.Int != 0),
                    _ => new RevitFileInfo.ScheduleCellValue(value.Int.ToString(CultureInfo.InvariantCulture)),
                };
            }).ToList());
        return rows;
    }
}
