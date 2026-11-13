using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class NativeFieldDomainTests
{
    private static Parameter P(int id, StorageType storage, object value) => BipHelpers.MakeBipParam(id, id.ToString(), storage, value);
    private static object? Value(Element e, int id) => e.Parameters.FirstOrDefault(p => p.Id == id)?.Value;

    private static Dictionary<uint, ushort> Classes(params (uint Id, ushort Class)[] items) => items.ToDictionary(i => i.Id, i => i.Class);

    [Fact]
    public void ParameterCreatedByTheTable_IsRemovedOutsideItsDomains_AndKeptInsideThem()
    {
        var inside = new Element { Id = 1, CategoryId = -2000011 };
        inside.Parameters.Add(P(-1006000, StorageType.Integer, 5));
        inside.NativeTableAddedParams = [(-1006000, ["10/-2000011"], StorageType.Integer, 5, false)];
        inside.NativeTableCreatedParams = [-1006000];
        var wrongCategory = new Element { Id = 2, CategoryId = -2000023 };
        wrongCategory.Parameters.Add(P(-1006000, StorageType.Integer, 5));
        wrongCategory.NativeTableAddedParams = [(-1006000, ["10/-2000011"], StorageType.Integer, 5, false)];
        wrongCategory.NativeTableCreatedParams = [-1006000];
        var classWide = new Element { Id = 3, CategoryId = -2000023 };
        classWide.Parameters.Add(P(-1006000, StorageType.Integer, 5));
        classWide.NativeTableAddedParams = [(-1006000, ["10"], StorageType.Integer, 5, false)];
        classWide.NativeTableCreatedParams = [-1006000];
        var info = new RevitFileInfo { Elements = [inside, wrongCategory, classWide] };

        ElementBuilder.PruneNativeFieldTableOutsideDomains(info, Classes((1, 10), (2, 10), (3, 10)));

        Assert.Equal(5, Value(inside, -1006000));
        Assert.Null(Value(wrongCategory, -1006000));
        Assert.Equal(5, Value(classWide, -1006000));
        Assert.Null(inside.NativeTableAddedParams);
    }

    [Fact]
    public void OverwrittenValue_IsGivenBack_WhenNoRowAppliesToTheFinalDomain()
    {
        var element = new Element { Id = 1, CategoryId = -2000023 };
        element.Parameters.Add(P(-1006001, StorageType.String, "wrong"));
        element.NativeTableAddedParams = [(-1006001, ["10/-2000011"], StorageType.String, "wrong", false)];
        element.NativeTablePrevious = new() { [-1006001] = ("original", StorageType.String, false) };

        ElementBuilder.PruneNativeFieldTableOutsideDomains(new RevitFileInfo { Elements = [element] }, Classes((1, 10)));

        Assert.Equal("original", Value(element, -1006001));
    }

    [Fact]
    public void LaterPassValue_IsNotTouched()
    {
        var element = new Element { Id = 1, CategoryId = -2000023 };
        element.Parameters.Add(P(-1006001, StorageType.String, "decided later"));
        element.NativeTableAddedParams = [(-1006001, ["10/-2000011"], StorageType.String, "table", false)];
        element.NativeTablePrevious = new() { [-1006001] = ("original", StorageType.String, false) };

        ElementBuilder.PruneNativeFieldTableOutsideDomains(new RevitFileInfo { Elements = [element] }, Classes((1, 10)));

        Assert.Equal("decided later", Value(element, -1006001));
    }

    [Fact]
    public void InDomainRow_WinsOverALaterOutOfDomainRow()
    {
        var element = new Element { Id = 1, CategoryId = -2000011 };
        element.Parameters.Add(P(-1006002, StorageType.String, ""));
        element.NativeTableAddedParams =
        [
            (-1006002, ["10/-2000011"], StorageType.String, "450x200", false),
            (-1006002, ["10/-2000016"], StorageType.String, "", false),
        ];

        ElementBuilder.PruneNativeFieldTableOutsideDomains(new RevitFileInfo { Elements = [element] }, Classes((1, 10)));

        Assert.Equal("450x200", Value(element, -1006002));
    }
}
