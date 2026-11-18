using RvtIO.Geometry;
using RvtIO.Ifc;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The current profile writes a plain IfcWall as the Brep of its decoded body: the holes and reveals of its doors and windows are faces of the body, and a round face is part of it as the triangles of its face mesh.</summary>
public class IfcExporterPlainWallBodyCurrentTests
{
    private static GeometryData Decode(string name)
    {
        using var stream = TestFiles.OpenBlock($"{name}.bin");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        byte[] data = memory.ToArray();
        return ElementBuilder.BuildGeometryData(GRepParserV2.Parse(data, 0, BitConverter.ToUInt32(data, 8))!)!;
    }

    private static Dictionary<string, int> Written(StepWriter w)
    {
        var counts = new Dictionary<string, int>();
        for (int index = 0; index < w.EntityCount; index++)
        {
            string type = w.EntityAt(index).Type;
            counts[type] = counts.GetValueOrDefault(type) + 1;
        }
        return counts;
    }

    [SkippableFact]
    public void ACutWall_KeepsItsReveals()
    {
        var w = new StepWriter();
        var wall = new Element { Id = 638404, Geometry = Decode("sample-a-wall-638404-cut") };
        Assert.True(new IfcExporter().TryWriteDecodedPlainWallBodyCurrent(w, wall, 1, out int shape));
        Assert.True(shape > 0);
        var counts = Written(w);
        Assert.Equal(1, counts["IFCFACETEDBREP"]);
        Assert.Equal(10, counts["IFCFACE"]);
        Assert.Equal(2, counts["IFCFACEBOUND"]);
    }

    [SkippableFact]
    public void ARoundWall_IsWrittenAsTheTrianglesOfItsFaceMeshes()
    {
        var w = new StepWriter();
        var wall = new Element { Id = 9751957, Geometry = Decode("sample-a-wall-9751957-round") };
        Assert.True(new IfcExporter().TryWriteDecodedPlainWallBodyCurrent(w, wall, 1, out _));
        var counts = Written(w);
        Assert.Equal(1, counts["IFCFACETEDBREP"]);
        Assert.Equal(28, counts["IFCFACE"]);
        Assert.False(counts.ContainsKey("IFCFACEBOUND"));
    }

    [SkippableFact]
    public void WithoutAClosedSolid_NothingIsWritten()
    {
        var w = new StepWriter();
        var geometry = Decode("sample-a-wall-638404-cut");
        geometry.BrepSolids = [];
        Assert.False(new IfcExporter().TryWriteDecodedPlainWallBodyCurrent(w, new Element { Id = 1, Geometry = geometry }, 1, out int shape));
        Assert.Equal(0, shape);
        Assert.Equal(0, w.EntityCount);
    }

    [SkippableFact]
    public void AWallWithARoundHoleFace_IsNoStandardCaseWall_AndIsWrittenAsItsFaceMeshes()
    {
        var geometry = Decode("sample-a-wall-2520908-hole");
        Assert.False(geometry.BrepSolids![0].IsClosed);
        Assert.Contains(geometry.MeshNodes!.Single().Faces, face => face.SurfaceKind != RvtIO.Geometry.Meshing.SurfaceKind.Plane);
        Assert.False(WallExtrusion.IsStandardCaseBody(geometry));

        var w = new StepWriter();
        Assert.True(new IfcExporter().TryWriteDecodedPlainWallBodyCurrent(w, new Element { Id = 2520908, Geometry = geometry }, 1, out _));
        var counts = Written(w);
        Assert.Equal(1, counts["IFCFACETEDBREP"]);
        Assert.Equal(68, counts["IFCFACE"]);
    }
}
