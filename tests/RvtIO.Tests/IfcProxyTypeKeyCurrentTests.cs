using System.Reflection;
using System.Text.RegularExpressions;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

/// <summary>A proxy in the current profile: one that holds its geometry itself, with no GInstance of a symbol, is compared with its type element
/// (also by the number of stored solids, the geometry summary), and a proxy with a GInstance of a symbol gets a type of its own
/// whenever it stores geometry.</summary>
public class IfcProxyTypeKeyCurrentTests
{
    [Theory]
    [InlineData(7, 15, 3, 7, 15, 2, true)]    // one solid more, as many faces and edges
    [InlineData(7, 15, 2, 7, 15, 2, false)]   // the same summary
    [InlineData(7, 16, 2, 7, 15, 2, true)]    // one edge more
    [InlineData(0, 0, 0, 7, 15, 2, false)]    // nothing but a GInstance
    [InlineData(0, 0, 1, 7, 15, 2, true)]     // a solid and no face
    public void ProxyWithoutAGInstance_IsComparedWithItsTypeElement(
        int faces, int edges, int solids, int typeFaces, int typeEdges, int typeSolids, bool ownType)
    {
        var elements = Elements(mapped: false, (faces, edges, solids), (typeFaces, typeEdges, typeSolids));
        string newer = Write(elements, currentProfile: true);
        Assert.Equal(ownType ? 2 : 1, Regex.Matches(newer, "=IFCBUILDINGELEMENTPROXYTYPE\\(").Count);
        // The legacy profile keeps one type for the proxies of a type element.
        Assert.Single(Regex.Matches(Write(elements, currentProfile: false), "=IFCBUILDINGELEMENTPROXYTYPE\\("));
    }

    [Fact]
    public void ProxyWithoutAGInstance_AndATypeElementWithoutGeometry_KeepsTheSharedType()
    {
        // Proxies with stored solids of type elements 7422650 and 16032, which have no block, share their types.
        var elements = Elements(mapped: false, (7, 15, 3), null);
        Assert.Single(Regex.Matches(Write(elements, currentProfile: true), "=IFCBUILDINGELEMENTPROXYTYPE\\("));
    }

    [Theory]
    [InlineData(true, 2)]   // a column of the family "Ventilation block" with a type of its own
    [InlineData(false, 1)]
    public void ColumnWithoutAGInstance_IsComparedWithItsTypeElementInTheNewerReleaseOnly(bool currentProfile, int types)
    {
        var elements = Elements(mapped: false, (62, 176, 10), (94, 256, 24));
        Assert.Equal(types, Regex.Matches(Write(elements, currentProfile, "IFCCOLUMN"), "=IFCCOLUMNTYPE\\(").Count);
    }

    [Theory]
    [InlineData(true, 2)]   // a type of its own
    [InlineData(false, 1)]
    public void FurnishingElementWithoutAGInstance_IsComparedWithItsTypeElementInTheNewerReleaseOnly(bool currentProfile, int types)
    {
        var elements = Elements(mapped: false, (280, 356, 19), (110, 228, 18));
        Assert.Equal(types, Regex.Matches(Write(elements, currentProfile, "IFCFURNISHINGELEMENT"), "=IFCFURNITURETYPE\\(").Count);
    }

    [Fact]
    public void ProxyWithoutAGInstance_AndATypeElementOfUnknownSummary_KeepsTheSharedType()
    {
        var elements = Elements(mapped: false, (7, 15, 3), (-1, -1, -1));
        Assert.Single(Regex.Matches(Write(elements, currentProfile: true), "=IFCBUILDINGELEMENTPROXYTYPE\\("));
    }

    [Theory]
    [InlineData(6, 12, 2, 6, 12, 1, true, false)]   // as many faces and edges as the symbol, one solid more
    [InlineData(6, 12, 1, 6, 12, 1, true, false)]   // the same summary
    [InlineData(6, 13, 1, 6, 12, 1, true, true)]    // one edge more: decided in both releases
    [InlineData(0, 0, 0, 6, 12, 1, false, false)]   // nothing but a GInstance
    public void MappedProxy_WithStoredGeometry_GetsATypeOfItsOwnInTheNewerReleaseWhateverTheSymbolHolds(
        int faces, int edges, int solids, int symbolFaces, int symbolEdges, int symbolSolids, bool newerOwn, bool olderOwn)
    {
        var elements = Elements(mapped: true, (faces, edges, solids), (symbolFaces, symbolEdges, symbolSolids));
        Assert.Equal(newerOwn ? 2 : 1, Regex.Matches(Write(elements, currentProfile: true), "=IFCBUILDINGELEMENTPROXYTYPE\\(").Count);
        Assert.Equal(olderOwn ? 2 : 1, Regex.Matches(Write(elements, currentProfile: false), "=IFCBUILDINGELEMENTPROXYTYPE\\(").Count);
    }

    [Fact]
    public void MappedProxy_AndASymbolOfUnknownSummary_KeepsTheSharedType()
    {
        var elements = Elements(mapped: true, (6, 12, 5), (-1, -1, -1));
        Assert.Single(Regex.Matches(Write(elements, currentProfile: true), "=IFCBUILDINGELEMENTPROXYTYPE\\("));
    }

    [Fact]
    public void MappedTransportElements_AreKeyedByTheirSymbolsAndOwnGeometryAsProxiesAre()
    {
        static int Types(Dictionary<int, Element> elements) =>
            Regex.Matches(Write(elements, currentProfile: true, "IFCTRANSPORTELEMENT"), "=IFCTRANSPORTELEMENTTYPE\\(").Count;

        // one symbol and no geometry of their own: one type
        Assert.Equal(1, Types(Elements(mapped: true, (0, 0, 0), (6, 12, 1))));
        // stored geometry of its own: a type of its own
        Assert.Equal(2, Types(Elements(mapped: true, (6, 12, 1), (6, 12, 1))));
        // different symbols under one type element: a type per symbol
        var elements = Elements(mapped: true, (0, 0, 0), (6, 12, 1));
        elements[2].Geometry!.ExactMappedInstances = [new GeoInstance { SymbolId = 602 }];
        elements[602] = new() { Id = 602, Name = "Other symbol", Geometry = new GeometryData() };
        Assert.Equal(2, Types(elements));
    }

    /// <summary>Two proxies of one type element 500; with <paramref name="mapped"/> each has a GInstance of symbol 601.
    /// The first carries <paramref name="instance"/> as its stored summary, the second only a GInstance. A summary of
    /// (-1, -1, -1) leaves the node summary of the block unknown, null leaves the block without geometry.</summary>
    private static Dictionary<int, Element> Elements(bool mapped, (int Faces, int Edges, int Solids) instance,
        (int Faces, int Edges, int Solids)? baseline)
    {
        static GeometryData Block((int Faces, int Edges, int Solids) summary) => summary.Faces < 0
            ? new GeometryData()
            : new GeometryData { StoredNodeSummary = (summary.Faces, summary.Edges), StoredSolids = summary.Solids };
        Element Proxy(int id, (int Faces, int Edges, int Solids) summary)
        {
            var geometry = Block(summary);
            if (mapped) geometry.ExactMappedInstances = [new GeoInstance { SymbolId = 601 }];
            return new Element
            {
                Id = id, NativeMasterSymbolId = 500,
                Parameters = [new Parameter
                {
                    Id = -1002050, Definition = new Definition { Name = "ELEM_TYPE_PARAM" },
                    StorageType = StorageType.ElementId, Value = 500,
                }],
                Geometry = geometry,
            };
        }
        var elements = new Dictionary<int, Element>
        {
            [1] = Proxy(1, instance),
            [2] = Proxy(2, (0, 0, 0)),
            [500] = new() { Id = 500, ValidatedIfcTypeObjectName = "Own family:Own type", Geometry = mapped || baseline == null ? null : Block(baseline.Value) },
        };
        if (mapped) elements[601] = new() { Id = 601, Name = "Symbol", Geometry = baseline == null ? null : Block(baseline.Value) };
        return elements;
    }

    private static string Write(Dictionary<int, Element> elements, bool currentProfile, string product = "IFCBUILDINGELEMENTPROXY")
    {
        var products = new Dictionary<int, (int entityId, int placementId, string ifcType)>();
        foreach (int id in new[] { 1, 2 })
            products.Add(id, (1000 + id, 0, product));
        var writer = new StepWriter();
        var method = typeof(IfcExporter).GetMethod("WriteTypeObjectsAndRelDefinesByTypeCore",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method.Invoke(null, [writer, 900, elements, products,
            new Dictionary<(string PropertyName, string ValueExpr), int>(), new Dictionary<int, bool?>(), currentProfile]);
        using var text = new StringWriter();
        writer.WriteTo(text, "test.ifc", "IFC2X3", "test", "test");
        return text.ToString();
    }
}
