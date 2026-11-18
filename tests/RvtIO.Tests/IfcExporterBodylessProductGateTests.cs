using RvtIO.Ifc;
using RvtIO.Model;
using System.Text.RegularExpressions;
using Xunit;
using RvtIO.Geometry;

namespace RvtIO.Tests;

/// <summary>In a 64-bit-id file whose bounds are decoded, a product without bounds has no body and is not written, unless its class only groups other products (curtain wall, roof, stair, ramp, element assembly). A file with no bounds at all keeps its products as they are.</summary>
public class IfcExporterBodylessProductGateTests
{
    [Fact]
    public void ProductWithoutBounds_IsNotWritten_WhenOtherProductsHaveBounds()
    {
        var info = new RevitFileInfo { GeometryUndecoded = true };
        info.Elements.Add(Column(id: 100, withBounds: false));
        info.Elements.Add(Column(id: 101, withBounds: true));
        string ifc = Export(info);

        var columns = Regex.Matches(ifc, @"=IFCCOLUMN\(.*,'(\d+)'\);");
        Assert.Equal(["101"], columns.Select(m => m.Groups[1].Value).ToArray());
    }

    [Fact]
    public void ProductWithoutBounds_IsKept_WhenNoProductHasBounds()
    {
        var info = new RevitFileInfo { GeometryUndecoded = true };
        info.Elements.Add(Column(id: 100, withBounds: false));
        Assert.Single(Regex.Matches(Export(info), @"=IFCCOLUMN\("));
    }

    [Fact]
    public void ContainerWithoutBounds_IsKeptWithoutRepresentation()
    {
        var info = new RevitFileInfo { GeometryUndecoded = true };
        info.Elements.Add(Column(id: 101, withBounds: true));
        info.Elements.Add(Roof(id: 102));
        string ifc = Export(info);

        var roof = Assert.Single(Regex.Matches(ifc, @"=IFCROOF\(.*,\$,'102',"));
        Assert.True(roof.Success);
    }

    [Theory]
    [InlineData("IFCBUILDINGELEMENTPROXY", false, true)]
    [InlineData("IFCBEAM", false, true)]
    [InlineData("IFCPLATE", false, true)]
    [InlineData("IFCMEMBER", false, true)]
    [InlineData("IFCFOOTING", false, true)]
    [InlineData("IFCFOOTING", true, false)]
    [InlineData("IFCBUILDINGELEMENTPROXY", true, false)]
    [InlineData("IFCCURTAINWALL", false, false)]
    [InlineData("IFCROOF", false, false)]
    [InlineData("IFCSTAIR", false, false)]
    [InlineData("IFCRAMP", false, false)]
    [InlineData("IFCELEMENTASSEMBLY", false, false)]
    [InlineData("IFCWALL", false, false)]
    [InlineData("IFCWALLSTANDARDCASE", false, false)]
    [InlineData("IFCSITE", false, false)]
    public void Predicate_SkipsOnlyBoundlessNonContainerElements(string ifcType, bool hasBounds, bool expected) =>
        Assert.Equal(expected, IfcExporter.ShouldSkipRepresentationlessProduct(
            ifcType, hasBounds, isEmittedAggregateParent: false));

    [Fact]
    public void Predicate_KeepsAnEmittedAggregateParent() =>
        Assert.False(IfcExporter.ShouldSkipRepresentationlessProduct(
            "IFCBUILDINGELEMENTPROXY", hasBounds: false, isEmittedAggregateParent: true));

    [Fact]
    public void FreestandingDoorWithBounds_IsWritten()
    {
        var info = new RevitFileInfo { GeometryUndecoded = true };
        info.Elements.Add(Instance(id: 200, categoryId: -2000023, "OST_Doors", withBounds: true));
        Assert.Single(Regex.Matches(Export(info), @"=IFCDOOR\("));
    }

    [Fact]
    public void DoorWithoutBounds_IsNotWritten_WhenOtherProductsHaveBounds()
    {
        var info = new RevitFileInfo { GeometryUndecoded = true };
        info.Elements.Add(Instance(id: 200, categoryId: -2000023, "OST_Doors", withBounds: false));
        info.Elements.Add(Column(id: 101, withBounds: true));
        Assert.DoesNotContain("=IFCDOOR(", Export(info));
    }

    [Theory]
    [InlineData("NaN")] [InlineData("Infinity")] [InlineData("inverted")]
    public void DamagedBoundsOfAnotherProduct_DoNotTurnTheGateOn(string damage)
    {
        // Bounds that fail to decode are no decoded bounds: the product that has none is written as before, and the damaged one is rejected
        // by its own bounds, not mistaken for a body of the file.
        var info = new RevitFileInfo { GeometryUndecoded = true };
        info.Elements.Add(Column(id: 100, withBounds: false));
        var damaged = Column(id: 101, withBounds: false);
        damaged.BoundingBox = damage switch
        {
            "NaN" => [double.NaN, 0, 0, 1, 1, 1],
            "Infinity" => [0, 0, 0, double.PositiveInfinity, 1, 1],
            _ => [1, 1, 1, 0, 0, 0],
        };
        info.Elements.Add(damaged);

        var columns = Regex.Matches(Export(info), @"=IFCCOLUMN\(.*,'(\d+)'\);");

        Assert.Equal(["100"], columns.Select(m => m.Groups[1].Value).ToArray());
    }

    [Fact]
    public void ProductWithDamagedBounds_IsRejectedNotSkippedAsBodyless_WhenOtherBoundsAreDecoded()
    {
        var info = new RevitFileInfo { GeometryUndecoded = true };
        var damaged = Column(id: 100, withBounds: false);
        damaged.BoundingBox = [double.NaN, 0, 0, 1, 1, 1];
        info.Elements.Add(damaged);
        info.Elements.Add(Column(id: 101, withBounds: true));

        var columns = Regex.Matches(Export(info), @"=IFCCOLUMN\(.*,'(\d+)'\);");

        Assert.Equal(["101"], columns.Select(m => m.Groups[1].Value).ToArray());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(new double[] { 0, 0, 0, 1, 1 }, false)]
    [InlineData(new double[] { 0, 0, 0, 1, 1, 1 }, true)]
    [InlineData(new double[] { 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new double[] { double.NaN, 0, 0, 1, 1, 1 }, false)]
    [InlineData(new double[] { 0, 0, 0, double.NegativeInfinity, 1, 1 }, false)]
    [InlineData(new double[] { 1, 0, 0, 0, 1, 1 }, false)]
    public void Bounds_AreValidWhenSixFiniteValuesInOrder(double[]? bounds, bool expected) =>
        Assert.Equal(expected, GeometryValidation.BoundsAreValid(bounds));

    [Fact]
    public void FootingWithBounds_IsWrittenAsAnIfcFooting()
    {
        var info = new RevitFileInfo { GeometryUndecoded = true };
        info.Elements.Add(Footing(id: 100, withBounds: true));
        Assert.Single(Regex.Matches(Export(info), @"=IFCFOOTING\("));
    }

    [Fact]
    public void FootingWithoutBounds_IsNotWritten_WhenOtherProductsHaveBounds()
    {
        var info = new RevitFileInfo { GeometryUndecoded = true };
        info.Elements.Add(Footing(id: 100, withBounds: false));
        info.Elements.Add(Column(id: 101, withBounds: true));

        string ifc = Export(info);

        Assert.DoesNotContain("=IFCFOOTING(", ifc);
        Assert.Single(Regex.Matches(ifc, @"=IFCCOLUMN\("));
    }

    [Fact]
    public void FootingWithoutBounds_IsKept_WhenNoProductHasBounds()
    {
        var info = new RevitFileInfo { GeometryUndecoded = true };
        info.Elements.Add(Footing(id: 100, withBounds: false));
        Assert.Single(Regex.Matches(Export(info), @"=IFCFOOTING\("));
    }

    private static Element Footing(int id, bool withBounds)
    {
        var footing = Instance(id, -2001300, "OST_StructuralFoundation", withBounds);
        footing.NativeRootClassName = "ContFooting";
        return footing;
    }

    private static string Export(RevitFileInfo info)
    {
        string path = Path.Combine(Path.GetTempPath(), $"revitreader-bodyless-{Guid.NewGuid():N}.ifc");
        try
        {
            new IfcExporter().Export(info, path);
            return File.ReadAllText(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static Element Column(int id, bool withBounds) => Instance(id, -2001330, "OST_StructuralColumns", withBounds);

    private static Element Roof(int id) => Instance(id, -2000035, "OST_Roofs", withBounds: false);

    private static Element Instance(int id, int categoryId, string category, bool withBounds) => new()
    {
        Id = id,
        TypeId = id + 1000,
        CategoryId = categoryId,
        Category = category,
        Name = "Item",
        BoundingBox = withBounds ? [0, 0, 0, 4, 3, 2] : null,
        Geometry = withBounds ? SolidPrism() : null,
        SerializedCreatedPhaseId = 3,
        Parameters = [new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = 3 }],
    };

    private static GeometryData SolidPrism()
    {
        double[][] points =
        [
            [0, 0, 0], [4, 0, 0], [4, 3, 0], [0, 3, 0],
            [0, 0, 2], [4, 0, 2], [4, 3, 2], [0, 3, 2],
        ];
        int[][] faces =
        [
            [0, 3, 2, 1], [4, 5, 6, 7],
            [0, 1, 5, 4], [1, 2, 6, 5],
            [2, 3, 7, 6], [3, 0, 4, 7],
        ];
        return new GeometryData
        {
            BoundingBox = [0, 0, 0, 4, 3, 2],
            Faces = faces.Select(indices => new GeoFace
            {
                OuterVertices = indices.Select(index => points[index]).ToList(),
            }).ToList(),
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
        };
    }
}
