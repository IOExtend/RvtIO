using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using RvtIO.Geometry;
using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

public class IfcExporterJoinNotchTests
{
    private sealed class Fixture
    {
        public required Element Wall;
        public required Element Neighbour;
        public required Element Cutter;
        public required GeometryData Rebuilt;
        public required Dictionary<int,Element> Elements;
        public Dictionary<int,List<int>> Ids=[];
        public Dictionary<int,List<(int CutterId,IfcExporter.HostedOpeningGeometry Operand)>> Operands=[];
        public Dictionary<int,List<int>> Standalone=[];
        public bool Recover(out GeometryData gross) => IfcExporter.TryGetVerifiedGrossWallBoundaryFromPerpendicularJoinNotchAndDoor(
            Wall,Wall.BoundingBox!,Rebuilt,Elements,Ids,Operands,Standalone,out gross,out _);
    }

    [Fact]
    public void JoinNotch_RecoversRectangleWithoutChangingDoor()
    {
        var f=Create();
        var original=f.Operands[f.Wall.Id][0];
        Assert.True(f.Recover(out var gross));
        Assert.Equal(original,f.Operands[f.Wall.Id][0]);
        Assert.True(FacetedShell.IsExactFacetedShell(gross.Faces,gross.BrepSolids![0].FaceIndices));
        Assert.True(IfcExporter.TryGetExactVerticalRectangularPrismSweptOpening(gross,out var box));
        Assert.Equal(2500,Math.Max(box.WidthFt,box.DepthFt)*304.8,5);
        Assert.Equal(200,Math.Min(box.WidthFt,box.DepthFt)*304.8,5);
        Assert.Equal(2850,box.HeightFt*304.8,5);
    }

    [Fact]
    public void JoinNotch_WriterUsesValidatedGrossBounds()
    {
        var f=Create(); Assert.True(f.Recover(out var gross));
        var method=typeof(IfcExporter).GetMethod("CreateWallSweptSolid",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var writer=new StepWriter();
        var values=new Dictionary<string,object?>
        {
            ["w"]=writer,["elem"]=f.Wall,["bbox"]=f.Wall.BoundingBox,["subCtxId"]=900,
            ["axisSubCtxId"]=901,["parentPlacementId"]=902,["forceSerializedDefinition"]=true,
            ["verifiedBoundary"]=gross,["allowBoundaryOwnedVerticalDefinition"]=true,
            ["useVerifiedBoundaryBoundsForVerticalDefinition"]=true,
        };
        method.Invoke(new IfcExporter(),method.GetParameters().Select(p=>values.TryGetValue(p.Name!,out var v)?v:p.DefaultValue).ToArray());
        using var output=new StringWriter(); writer.WriteTo(output,"test.ifc","IFC2X3","test","test");
        string text=output.ToString();
        Assert.DoesNotContain("IFCARBITRARYCLOSEDPROFILEDEF",text);
        var rectangle=Assert.Single(Regex.Matches(text,@"IFCRECTANGLEPROFILEDEF\(\.AREA\.,\$,#\d+,([^,]+),([^\)]+)\)"));
        Assert.Equal(2.5,double.Parse(rectangle.Groups[1].Value,CultureInfo.InvariantCulture),9);
        Assert.Equal(0.2,double.Parse(rectangle.Groups[2].Value,CultureInfo.InvariantCulture),9);
        var extrusion=Assert.Single(Regex.Matches(text,@"IFCEXTRUDEDAREASOLID\(#\d+,#\d+,#\d+,([^\)]+)\)"));
        Assert.Equal(2.85,double.Parse(extrusion.Groups[1].Value,CultureInfo.InvariantCulture),9);
    }

    [Fact]
    public void JoinNotch_AcceptsReversalAndRebuiltFacePermutation()
    {
        var f=Create();
        foreach(var geometry in new[] {f.Wall.Geometry!,f.Neighbour.Geometry!,f.Rebuilt})
        foreach(var face in geometry.Faces)
        {
            face.OuterVertices!.Reverse(); face.AlternativeOuterVertices?.Reverse();
            foreach(var ring in face.InnerVertices??[]) ring.Reverse();
        }
        f.Rebuilt.Faces.Reverse(); f.Wall.References!.Reverse();
        Assert.True(f.Recover(out _));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void JoinNotch_AcceptsDifferentThroughDepth(double factor)
    {
        var f=Create(); var pair=f.Operands[f.Wall.Id][0];
        f.Operands[f.Wall.Id][0]=(pair.CutterId,pair.Operand with {DepthFt=pair.Operand.DepthFt*factor});
        Assert.True(f.Recover(out _));
    }

    [Theory]
    [InlineData(0.37)]
    [InlineData(1.5707963267948966)]
    public void JoinNotch_AcceptsRigidWorldTransform(double angle)
    {
        var f=Create(); double c=Math.Cos(angle),s=Math.Sin(angle);
        var transformed=new HashSet<double[]>(ReferenceEqualityComparer.Instance);
        void Transform(double[] p)
        {
            double x=p[0],y=p[1]; p[0]=c*x-s*y+32; p[1]=s*x+c*y-17; p[2]+=4;
        }
        foreach(var geometry in new[] {f.Wall.Geometry!,f.Neighbour.Geometry!,f.Rebuilt})
        {
            var points=geometry.Faces.SelectMany(face=>face.OuterVertices!
                .Concat((face.InnerVertices??[]).SelectMany(r=>r)).Concat(face.AlternativeOuterVertices??[])).ToArray();
            foreach(var point in points) if(transformed.Add(point)) Transform(point);
            geometry.BoundingBox=[points.Min(p=>p[0]),points.Min(p=>p[1]),points.Min(p=>p[2]),
                points.Max(p=>p[0]),points.Max(p=>p[1]),points.Max(p=>p[2])];
        }
        foreach(var wall in new[] {f.Wall,f.Neighbour})
        {
            Transform(wall.Location!.StartPoint); Transform(wall.Location.EndPoint);
            wall.BoundingBox=(double[])wall.Geometry!.BoundingBox!.Clone();
        }
        var pair=f.Operands[f.Wall.Id][0]; var op=pair.Operand;
        f.Operands[f.Wall.Id][0]=(pair.CutterId,op with
        {
            CentreXFt=c*op.CentreXFt-s*op.CentreYFt+32,CentreYFt=s*op.CentreXFt+c*op.CentreYFt-17,
            AxisX=c*op.AxisX-s*op.AxisY,AxisY=s*op.AxisX+c*op.AxisY,BaseZFt=op.BaseZFt+4,
        });
        Assert.True(f.Recover(out _));
    }

    [Fact]
    public void JoinNotch_RejectsEveryIndividualGeometryCoordinateMutation()
    {
        var original=Create(); int checkedCount=0;
        for(int which=0;which<3;which++)
        {
            var g=which==0?original.Wall.Geometry!:which==1?original.Neighbour.Geometry!:original.Rebuilt;
            for(int face=0;face<g.Faces.Count;face++)
            {
                var rings=new List<List<double[]>> {g.Faces[face].OuterVertices!};
                rings.AddRange(g.Faces[face].InnerVertices??[]);
                if(g.Faces[face].AlternativeOuterVertices is { } alt) rings.Add(alt);
                for(int ring=0;ring<rings.Count;ring++)
                for(int point=0;point<rings[ring].Count;point++)
                for(int coordinate=0;coordinate<3;coordinate++)
                {
                    var f=Create(); var actual=which==0?f.Wall.Geometry!:which==1?f.Neighbour.Geometry!:f.Rebuilt;
                    var actualRings=new List<List<double[]>> {actual.Faces[face].OuterVertices!};
                    actualRings.AddRange(actual.Faces[face].InnerVertices??[]);
                    if(actual.Faces[face].AlternativeOuterVertices is { } other) actualRings.Add(other);
                    actualRings[ring][point][coordinate]+=0.1;
                    Assert.False(f.Recover(out _),$"geometry {which} face {face} ring {ring} point {point} coordinate {coordinate}");
                    checkedCount++;
                }
            }
        }
        Assert.True(checkedCount>500);
    }

    [Theory]
    [InlineData("wall-roster")]
    [InlineData("wall-closed")]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("axis")]
    [InlineData("bbox")]
    [InlineData("rebuilt-bbox")]
    [InlineData("rebuilt-hole")]
    [InlineData("extra-inner")]
    [InlineData("source-alternative")]
    [InlineData("join-roster")]
    [InlineData("join-closed")]
    [InlineData("join-width")]
    [InlineData("join-height")]
    [InlineData("join-end")]
    [InlineData("join-bbox")]
    [InlineData("join-cut")]
    [InlineData("join-alternative")]
    [InlineData("join-reference")]
    [InlineData("join-reverse")]
    [InlineData("join-duplicate")]
    [InlineData("ambiguous")]
    [InlineData("decoded")]
    [InlineData("door-host")]
    [InlineData("door-reference")]
    [InlineData("door-reverse")]
    [InlineData("door-super")]
    [InlineData("emitted")]
    [InlineData("operand-id")]
    [InlineData("standalone")]
    [InlineData("operand-width")]
    [InlineData("operand-height")]
    [InlineData("operand-shallow")]
    [InlineData("operand-shift")]
    [InlineData("operand-angle")]
    [InlineData("operand-scale")]
    [InlineData("operand-nan")]
    public void JoinNotch_FailsClosed(string mutation)
    {
        var f=Create(); var pair=f.Operands[f.Wall.Id][0]; var op=pair.Operand;
        switch(mutation)
        {
            case "wall-roster": f.Wall.Geometry!.BrepSolids![0].FaceIndices.Add(12); break;
            case "wall-closed": f.Wall.Geometry!.BrepSolids![0].IsClosed=true; break;
            case "width": f.Wall.Parameters.Single(p=>p.Id==-1001000).Value=1.0; break;
            case "height": f.Wall.Parameters.Single(p=>p.Id==-1001105).Value=10.0; break;
            case "axis": f.Wall.Location!.EndPoint[2]+=0.1; break;
            case "bbox": f.Wall.BoundingBox![0]+=0.1; break;
            case "rebuilt-bbox": f.Rebuilt.BoundingBox![0]+=0.1; break;
            case "rebuilt-hole": f.Rebuilt.Faces[0].InnerVertices=null; break;
            case "extra-inner": f.Wall.Geometry!.Faces[1].InnerVertices=[f.Wall.Geometry.Faces[1].OuterVertices!]; break;
            case "source-alternative": f.Wall.Geometry!.Faces[0].AlternativeOuterVertices=f.Wall.Geometry.Faces[0].OuterVertices; break;
            case "join-roster": f.Neighbour.Geometry!.BrepSolids![0].FaceIndices.Add(6); break;
            case "join-closed": f.Neighbour.Geometry!.BrepSolids![0].IsClosed=true; break;
            case "join-width": f.Neighbour.Parameters.Single(p=>p.Id==-1001000).Value=1.0; break;
            case "join-height": f.Neighbour.Parameters.Single(p=>p.Id==-1001105).Value=10.0; break;
            case "join-end": f.Neighbour.Location!.EndPoint[0]+=0.1; break;
            case "join-bbox": f.Neighbour.BoundingBox![0]+=0.1; break;
            case "join-cut": f.Neighbour.CuttingElementIds=[f.Cutter.Id]; break;
            case "join-alternative": f.Neighbour.Geometry!.Faces[5].AlternativeOuterVertices=null; break;
            case "join-reference": f.Wall.References!.Remove(f.Neighbour.Id); break;
            case "join-reverse": f.Neighbour.References=[]; break;
            case "join-duplicate": f.Wall.References!.Add(f.Neighbour.Id); break;
            case "ambiguous":
                var other=new Element {Id=99004,CategoryId=-2000011,Location=f.Neighbour.Location};
                f.Elements.Add(other.Id,other); f.Wall.References!.Add(other.Id); break;
            case "decoded": f.Wall.CuttingElementIds!.Add(f.Cutter.Id); break;
            case "door-host": f.Cutter.SerializedHostId=99099; break;
            case "door-reference": f.Wall.References!.Remove(f.Cutter.Id); break;
            case "door-reverse": f.Cutter.References=[]; break;
            case "door-super": f.Cutter.SuperInstanceId=99099; break;
            case "emitted": f.Ids[f.Wall.Id].Add(f.Cutter.Id); break;
            case "operand-id": f.Operands[f.Wall.Id][0]=(99099,op); break;
            case "standalone": f.Standalone[f.Wall.Id]=[99099]; break;
            case "operand-width": op=op with {WidthFt=op.WidthFt+0.1}; break;
            case "operand-height": op=op with {HeightFt=op.HeightFt+0.1}; break;
            case "operand-shallow": op=op with {DepthFt=0.1}; break;
            case "operand-shift": op=op with {CentreYFt=op.CentreYFt+0.1}; break;
            case "operand-angle": op=op with {AxisX=0.6,AxisY=0.8}; break;
            case "operand-scale": op=op with {AxisX=op.AxisX*2,AxisY=op.AxisY*2,WidthFt=op.WidthFt/2,DepthFt=op.DepthFt/2}; break;
            case "operand-nan": op=op with {BaseZFt=double.NaN}; break;
        }
        if(mutation.StartsWith("operand-") && mutation!="operand-id") f.Operands[f.Wall.Id][0]=(pair.CutterId,op);
        Assert.False(f.Recover(out _));
    }

    // Captured RVT source neighbourhood; IDs remapped. No IFC/reference dimensions.
    private static Fixture Create()
    {
        const string captured="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";
        using var stream=new GZipStream(new MemoryStream(Convert.FromBase64String(captured)),CompressionMode.Decompress);
        using var doc=JsonDocument.Parse(stream);
        static double[] Point(JsonElement p)=>p.EnumerateArray().Select(v=>v.GetDouble()).ToArray();
        static List<double[]> Ring(JsonElement r)=>r.EnumerateArray().Select(Point).ToList();
        int Map(int id)=>id==2366849?99001:id==2373235?99002:id==2391019?99003:id;
        var elements=new Dictionary<int,Element>();
        foreach(var row in doc.RootElement.EnumerateArray())
        {
            var element=new Element
            {
                Id=Map(row.GetProperty("Id").GetInt32()),CategoryId=row.GetProperty("CategoryId").GetInt32(),
                Location=JsonSerializer.Deserialize<LocationCurve>(row.GetProperty("Location")),
                BoundingBox=Point(row.GetProperty("BoundingBox")),
                References=row.GetProperty("References").EnumerateArray().Select(p=>Map(p.GetInt32())).ToList(),
                CuttingElementIds=row.GetProperty("CuttingElementIds").ValueKind==JsonValueKind.Array
                    ?row.GetProperty("CuttingElementIds").EnumerateArray().Select(p=>Map(p.GetInt32())).ToList():null,
                SerializedHostId=row.GetProperty("SerializedHostId").ValueKind==JsonValueKind.Number
                    ?Map(row.GetProperty("SerializedHostId").GetInt32()):null,
                Parameters=row.GetProperty("Parameters").EnumerateArray().Where(p=>p.GetProperty("StorageType").GetString()=="Double")
                    .Select(p=>new Parameter {Id=p.GetProperty("Id").GetInt32(),StorageType=StorageType.Double,Value=p.GetProperty("Value").GetDouble()}).ToList(),
            };
            var g=row.GetProperty("Geometry");
            if(g.ValueKind==JsonValueKind.Object)
            {
                element.Geometry=new GeometryData {BoundingBox=Point(g.GetProperty("BoundingBox"))};
                foreach(var face in g.GetProperty("Faces").EnumerateArray()) element.Geometry.Faces.Add(new GeoFace
                {
                    OuterVertices=Ring(face.GetProperty("Outer")),
                    InnerVertices=face.GetProperty("Inner").ValueKind==JsonValueKind.Array?face.GetProperty("Inner").EnumerateArray().Select(Ring).ToList():null,
                    AlternativeOuterVertices=face.GetProperty("Alternative").ValueKind==JsonValueKind.Array?Ring(face.GetProperty("Alternative")):null,
                });
                if(g.GetProperty("Solids").ValueKind==JsonValueKind.Array)
                    element.Geometry.BrepSolids=JsonSerializer.Deserialize<List<GeoBrepSolid>>(g.GetProperty("Solids"));
            }
            elements.Add(element.Id,element);
        }
        var wall=elements[99001];
        Assert.True(IfcExporter.TryBuildStraightWallBoundary(wall,out var rebuilt));
        var f=new Fixture {Wall=wall,Neighbour=elements[99002],Cutter=elements[99003],Rebuilt=rebuilt,Elements=elements};
        var hole=wall.Geometry!.Faces[0].InnerVertices![0];
        double dx=wall.Location!.EndPoint[0]-wall.Location.StartPoint[0],dy=wall.Location.EndPoint[1]-wall.Location.StartPoint[1];
        double length=Math.Sqrt(dx*dx+dy*dy); dx/=length;dy/=length;
        double lo=hole.Min(p=>p[0]*dx+p[1]*dy),hi=hole.Max(p=>p[0]*dx+p[1]*dy);
        double normal=-wall.Location.StartPoint[0]*dy+wall.Location.StartPoint[1]*dx,centre=(lo+hi)/2;
        f.Ids[wall.Id]=[f.Cutter.Id];
        f.Operands[wall.Id]=[(f.Cutter.Id,new IfcExporter.HostedOpeningGeometry(
            centre*dx-normal*dy,centre*dy+normal*dx,dx,dy,hi-lo,10,hole.Min(p=>p[2]),hole.Max(p=>p[2])-hole.Min(p=>p[2])))];
        return f;
    }
}
