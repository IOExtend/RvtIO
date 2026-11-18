using System.Globalization;
using RvtIO.Compact;
using RvtIO.Lookup;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Integrity of the embedded label tables, checked without any external file.</summary>
public class EmbeddedTableIntegrityTests
{
    [Fact]
    public void ParameterTableHasUniqueIdsAndLooksUpEveryRow()
    {
        var rows = EmbeddedTables.Rows("compact_builtin_parameters.tsv", 3);
        Assert.Equal(3342, rows.Count);
        Assert.Equal(rows.Count, rows.Select(r => r[0]).Distinct().Count());
        foreach (var row in rows)
        {
            int id = int.Parse(row[0], CultureInfo.InvariantCulture);
            Assert.True(CompactLabels.TryGetParameterLabel(id, out string label), row[0]);
            Assert.Equal(row[2], label);
            Assert.True(CompactLabels.TryGetParameterGroupId(id, out int group), row[0]);
            Assert.Equal(int.Parse(row[1], CultureInfo.InvariantCulture), group);
            Assert.False(string.IsNullOrWhiteSpace(label), row[0]);
            Assert.Equal(label.Trim(), label);
        }
    }

    [Fact]
    public void ParameterCaptionsAreNoIfcNameOverrideKeys()
    {
        foreach (var row in EmbeddedTables.Rows("compact_builtin_parameters.tsv", 3))
        {
            string key = row[2].Replace(" ", string.Empty, StringComparison.Ordinal);
            Assert.False(StringComparer.InvariantCultureIgnoreCase.Equals(key, "IfcName"), row[0]);
            Assert.False(StringComparer.InvariantCultureIgnoreCase.Equals(key, "NameOverride"), row[0]);
        }
    }

    [Fact]
    public void GroupTableHasUniqueIdsAndEveryGroupOfTheParameterTableIsKnown()
    {
        var groups = EmbeddedTables.Rows("compact_parameter_groups.tsv", 2);
        Assert.Equal(120, groups.Count);
        Assert.Equal(groups.Count, groups.Select(r => r[0]).Distinct().Count());
        foreach (var row in groups)
        {
            Assert.True(ParameterGroupLabels.TryGetGroupLabel(int.Parse(row[0], CultureInfo.InvariantCulture), out string label), row[0]);
            Assert.Equal(row[1], label);
        }
        foreach (int group in EmbeddedTables.Rows("compact_builtin_parameters.tsv", 3).Select(r => int.Parse(r[1], CultureInfo.InvariantCulture)).Distinct())
            Assert.True(group == ParameterGroupLabels.InvalidGroupId || ParameterGroupLabels.TryGetGroupLabel(group, out _), group.ToString());
    }

    [Fact]
    public void OverrideTableHasUniqueIdsAndEveryOverrideIsFound()
    {
        var rows = EmbeddedTables.Rows("compact_label_overrides.tsv", 4);
        Assert.Equal(rows.Count, rows.Select(r => r[0]).Distinct().Count());
        foreach (var row in rows)
        {
            Assert.True(CompactLabels.HasOverride(int.Parse(row[0], CultureInfo.InvariantCulture)), row[0]);
            Assert.True(CompactLabels.TryGetLabels(int.Parse(row[0], CultureInfo.InvariantCulture), out string group, out string label), row[0]);
            Assert.Equal(row[1], group);
            Assert.Equal(row[2], label);
        }
    }

    [Fact]
    public void CategoryNameTableHasUniqueNegativeIds()
    {
        var rows = EmbeddedTables.Rows("english_category_names.tsv", 2);
        Assert.Equal(1075, rows.Count);
        Assert.Equal(rows.Count, rows.Select(r => r[0]).Distinct().Count());
        foreach (var row in rows)
        {
            Assert.True(int.Parse(row[0], CultureInfo.InvariantCulture) < 0, row[0]);
            Assert.True(CompactLabels.TryGetCategoryLabel(int.Parse(row[0], CultureInfo.InvariantCulture), out string label), row[0]);
            Assert.Equal(row[1], label);
        }
    }

    [Theory]
    [InlineData("compact_builtin_parameters.tsv")]
    [InlineData("compact_parameter_groups.tsv")]
    [InlineData("revit_ui_names.tsv")]
    [InlineData("compact_label_overrides.tsv")]
    public void TablesStartWithCommentLinesTheLoadersSkip(string name)
    {
        string first = EmbeddedTables.Text(name).Split('\n')[0];
        Assert.StartsWith("#", first);
        Assert.Equal(first, first.TrimEnd('\r'));
    }
}
