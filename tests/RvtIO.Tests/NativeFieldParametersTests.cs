using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class NativeFieldParametersTests
{
    [Fact]
    public void Table_IsBuiltInOnlyKeyedByDeclaringClassAndUniquePerParameterAndField()
    {
        var seen = new HashSet<(int, string)>();
        foreach (var entry in NativeFieldParameters.All)
        {
            Assert.True(entry.ParamId < 0, "only built-in parameter IDs are file independent");
            Assert.Matches(@"^\d+(#\d+)?:m_\w+(@\d+)?$", entry.Key);
            Assert.True(seen.Add((entry.ParamId, entry.Key)), $"duplicate {entry.ParamId} {entry.Key}");
            Assert.True(entry.Storage is StorageType.Double or StorageType.Integer
                or StorageType.ElementId or StorageType.String);
            Assert.Contains(int.Parse(entry.Key[..entry.Key.IndexOfAny(['#', ':'])]), NativeFieldParameters.DeclaringClasses);
        }
        Assert.NotEmpty(NativeFieldParameters.All);
    }

    [Fact]
    public void PresentRowsUseCapturedStorageAndKeepTableOrder()
    {
        var native = new TfBlockDeserializer.NativeParams();
        native.Fields["2628:m_categoryId"] = 21;
        native.Fields["1064:m_categoryId"] = 42;
        native.DoubleFields["1064:m_categoryId"] = 0.5;
        native.Fields["1492:m_unknown"] = 9;
        var rows = new List<int>();
        var values = new object?[NativeFieldParameters.All.Length];

        NativeFieldParameters.CollectPresentRows(native, rows, values);

        Assert.Equal(
            [(-1140363, "1064:m_categoryId"), (-1140363, "2628:m_categoryId"),
             (-1140362, "1064:m_categoryId"), (-1140362, "2628:m_categoryId")],
            rows.Select(index => (NativeFieldParameters.All[index].ParamId, NativeFieldParameters.All[index].Key)));
        Assert.Equal([42, 21, 42, 21], rows.Select(row => Assert.IsType<int>(values[row])));
        Assert.Same(values[rows[0]], values[rows[2]]);
        NativeFieldParameters.CollectPresentRows(new(), rows, values);
        Assert.Empty(rows);
    }

    [Theory]
    [InlineData(-1001381, "1794:m_structUsage")] // Structural Usage
    [InlineData(-1001552, "308:m_enableAnalyticalModel")] // Enable Analytical Model
    [InlineData(-1010303, "1795:m_hostId")]      // Host Id
    public void KnownApiVerifiedRows_ArePresent(int parameterId, string key) =>
        Assert.Contains(NativeFieldParameters.All, e => e.ParamId == parameterId && e.Key == key);

    [Theory]
    [InlineData(-1150350, "835:m_endExtensionLength")]   // End/Top extension length
    [InlineData(-1150348, "835:m_startExtensionLength")] // Beginning/Bottom extension length
    public void RailExtensionLength_IsOneFieldPerParameter(int parameterId, string key)
    {
        // HandRailType 628127 stores start 1 ft and end 0.25 ft; End/Top prints as 0' - 3" (the table once also mapped each field to the other id)
        var row = Assert.Single(NativeFieldParameters.All, e => e.ParamId == parameterId);
        Assert.Equal(key, row.Key);
        Assert.Equal(["2143", "3990"], row.Domains);
    }

    [Fact]
    public void CrossSectionRotation_IsNotATableRow() // truss members differ; ApplyCrossSectionRotation owns it
        => Assert.DoesNotContain(NativeFieldParameters.All, e => e.Key == "1709:m_rotation");
}

public class NativeNamedParametersTests
{
    [Fact]
    public void VerifiedIds_AreBuiltInAndExcludeComputedLength()
    {
        Assert.All(NativeNamedParameters.VerifiedIds, id => Assert.True(id < 0));
        Assert.Contains(-1005500, NativeNamedParameters.VerifiedIds); // Structural Material
        Assert.DoesNotContain(-1001375, NativeNamedParameters.VerifiedIds); // Length is computed, the stored value is the nominal one
    }
}
