using System.Globalization;
using System.Text.Json;
using RvtIO.Compact;
using RvtIO.BuiltIns;
using RvtIO.Lookup;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The parameter, group and category labels of the compact JSON export (CompactLabels).</summary>
public class CompactLabelsTests
{
    private static string RepoFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(Path.Combine(parts));
    }

    /// <summary>The Definition.Name and ParameterGroup of the embedded API definitions are what the table
    /// says, for all 2050 listed parameters (but one with a blank at the end of its name).</summary>
    [Fact]
    public void ParameterTableEqualsTheEmbeddedApiDefinitions()
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(RepoFile("src", "RvtIO", "Resources", "api_parameter_definitions_2022.json")));
        int checkedIds = 0, notListed = 0;
        foreach (var entry in json.RootElement.EnumerateObject())
        {
            int id = int.Parse(entry.Name, CultureInfo.InvariantCulture);
            string name = entry.Value[0].GetString()!;
            if (name != name.Trim())
            {
                Assert.False(CompactLabels.TryGetParameterLabel(id, out _), entry.Name); // not a plain label: not listed
                notListed++;
                continue;
            }
            Assert.True(CompactLabels.TryGetParameterLabel(id, out string label), entry.Name);
            Assert.Equal(name, label);
            Assert.True(CompactLabels.TryGetParameterGroupId(id, out int group), entry.Name);
            Assert.Equal(entry.Value[1].GetInt32(), group);
            checkedIds++;
        }
        Assert.Equal(2049, checkedIds);
        Assert.Equal(1, notListed);
    }

    /// <summary>The identity block every node starts with (Type Name, Design Option, Workset, Edited by, IfcGUID) and the
    /// parameters whose definition has no group.</summary>
    [Theory]
    [InlineData(-1002001, "Type Name", "Identity Data")]
    [InlineData(-1013201, "Design Option", "Identity Data")]
    [InlineData(-1002053, "Workset", "Identity Data")]
    [InlineData(-1002067, "Edited by", "Identity Data")]
    [InlineData(-1019000, "IfcGUID", "IFC Parameters")]
    [InlineData(-1140362, "Category", "Invalid group")]
    [InlineData(-1140363, "Category", "Invalid group")]
    [InlineData(-1002051, "Family", "Invalid group")]
    [InlineData(-1002052, "Family and Type", "Invalid group")]
    [InlineData(-1002050, "Type", "Invalid group")]
    [InlineData(-1002000, "Type Id", "Invalid group")]
    [InlineData(-1002002, "Family Name", "Invalid group")]
    [InlineData(-1002108, "Host Id", "Invalid group")]
    public void IdentityBlockParameters(int id, string label, string group)
    {
        Assert.True(CompactLabels.TryGetParameterLabel(id, out string actualLabel));
        Assert.Equal(label, actualLabel);
        Assert.True(CompactLabels.TryGetParameterGroupLabel(id, out string actualGroup));
        Assert.Equal(group, actualGroup);
    }

    [Theory]
    [InlineData(-1, "Invalid group")]
    [InlineData(-5000100, "Identity Data")]
    [InlineData(-5000119, "Constraints")]
    [InlineData(-5000110, "Electrical Engineering")]
    [InlineData(-5000130, "Electrical")]
    [InlineData(-5000232, "Invalid group")] // a group of later versions: unknown here, the expected export's rule for an unknown group
    [InlineData(0, "Invalid group")]
    public void GroupLabelOfAGroupId(int group, string expected) => Assert.Equal(expected, CompactLabels.GroupLabel(group));

    [Fact]
    public void InvalidGroupOfAParameterIsLabelledInvalidGroup()
    {
        // Category (ELEM_CATEGORY_PARAM): its definition has no group.
        Assert.True(CompactLabels.TryGetParameterGroupId(-1140362, out int group));
        Assert.Equal(ParameterGroupLabels.InvalidGroupId, group);
        Assert.True(CompactLabels.TryGetParameterGroupLabel(-1140362, out string label));
        Assert.Equal("Invalid group", label);
    }

    [Theory]
    [InlineData(-1019012)] // Export to IFC: not in the Revit 2022 table
    [InlineData(-1016014)]
    [InlineData(-1006703)] // its Revit 2022 caption ends with a line break: not a plain label
    [InlineData(-1006012)] // its Revit 2022 caption ends with a blank: not a plain label
    [InlineData(0)]
    [InlineData(1234567)]
    public void AParameterNoSourceListsIsNotLabelled(int id)
    {
        Assert.False(CompactLabels.TryGetParameterLabel(id, out string label));
        Assert.Equal("", label);
        Assert.False(CompactLabels.TryGetParameterGroupId(id, out _));
        Assert.False(CompactLabels.TryGetParameterGroupLabel(id, out _));
    }

    [Theory]
    [InlineData(-2000011, "Walls")]
    [InlineData(-2000023, "Doors")]
    [InlineData(-2000032, "Floors")]
    public void CategoryLabelOfABuiltinCategory(int id, string expected)
    {
        Assert.True(CompactLabels.TryGetCategoryLabel(id, out string label));
        Assert.Equal(expected, label);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(288495)]
    public void ACategoryOfTheFileHasNoBuiltinLabel(int id) => Assert.False(CompactLabels.TryGetCategoryLabel(id, out _));

    /// <summary>The group labels of the table agree with the labels the repository holds for the enum.</summary>
    [Fact]
    public void GroupLabelsAgreeWithTheEnumExtension()
    {
        int known = 0;
        for (int group = -5000300; group <= -5000001; group++)
            if (ParameterGroupLabels.TryGetGroupLabel(group, out _)) known++;
        Assert.Equal(120, known);
        foreach (var group in Enum.GetValues<BuiltInParameterGroup>().Where(g => g != BuiltInParameterGroup.INVALID))
            if (group.ToEnglishLabel(out string enumLabel))
            {
                Assert.True(ParameterGroupLabels.TryGetGroupLabel((int)group, out string label), group.ToString());
                Assert.Equal(enumLabel, label);
            }
    }

    [Theory]
    [InlineData(-1155224, "Model Bar Diameter", -5000103, "Construction")]
    [InlineData(-1155222, "Show Grids", -5000104, "Graphics")]
    public void ParameterLabelAndGroupOfAnId(int id, string label, int groupId, string group)
    {
        Assert.True(CompactLabels.TryGetParameterLabel(id, out string actualLabel));
        Assert.Equal(label, actualLabel);
        Assert.True(CompactLabels.TryGetParameterGroupId(id, out int actualGroupId));
        Assert.Equal(groupId, actualGroupId);
        Assert.Equal(group, CompactLabels.GroupLabel(actualGroupId));
    }

    [Fact]
    public void AnOverrideReplacesTheTableLabelAndGroup()
    {
        Assert.True(CompactLabels.HasOverride(-1001375));
        Assert.True(CompactLabels.TryGetLabels(-1001375, out string group, out string label));
        Assert.Equal("Dimensions", group);
        Assert.Equal("System Length", label);
    }
}
