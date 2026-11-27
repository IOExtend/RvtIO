using RvtIO.BuiltIns;
using RvtIO.Geometry.Meshing;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Pdf;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The elevation printer (src/RvtIO/Print/ElevationPrinter): the direction from the view name, the projected
/// faces of the model and the composer placing an elevation on a sheet.</summary>
public class ElevationPrinterTests
{
    private const double FeetToMillimetres = 304.8;

    private static void Add(Element element, BuiltInParameter id, StorageType storage, object value) =>
        element.Parameters.Add(BipHelpers.MakeBipParam((int)id, id.ToString(), storage, value));

    [Theory]
    [InlineData("South", "south")]
    [InlineData("North Elevation", "north")]
    [InlineData("East", "east")]
    [InlineData("West", "west")]
    [InlineData("Longitudinal Section", "section")]
    [InlineData("Level 1", null)]
    public void DirectionOf_ReadsTheCompassName(string name, string? expected) =>
        Assert.Equal(expected, ElevationPrinter.DirectionOf(name));

    private static Element ElevationView(int id, string name, int scale)
    {
        var view = new Element { Id = id, CategoryId = (int)BuiltInCategory.OST_Views, NativeRecordClass = 1016 };
        Add(view, BuiltInParameter.VIEW_NAME, StorageType.String, name);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, scale);
        return view;
    }

    private static Element WallWithFace(int id)
    {
        var face = new GeoFace
        {
            Origin = [0, 0, 0],
            XVector = [1, 0, 0],
            YVector = [0, 0, 1],
            Normal = [0, 1, 0],
            OuterVertices =
            [
                [0, 0, 0], [4, 0, 0], [4, 0, 3], [0, 0, 3],
            ],
        };
        return new Element
        {
            Id = id,
            CategoryId = (int)BuiltInCategory.OST_Walls,
            Geometry = new GeometryData { Faces = [face] },
        };
    }

    private static Element QuadWall(int id, double[][] vertices, double[] normal,
        int category = (int)BuiltInCategory.OST_Walls)
    {
        var face = new GeoFace
        {
            Origin = [0, 0, 0],
            XVector = [1, 0, 0],
            YVector = [0, 0, 1],
            Normal = [normal[0], normal[1], normal[2]],
            OuterVertices = vertices.Select(v => new[] { v[0], v[1], v[2] }).ToList(),
        };
        return new Element
        {
            Id = id,
            CategoryId = category,
            Geometry = new GeometryData { Faces = [face] },
        };
    }

    private static Element SectionView(int id, double[] origin)
    {
        return new Element
        {
            Id = id,
            CategoryId = (int)BuiltInCategory.OST_Views,
            ViewOrigin = origin,
            ViewDirection = [-1, 0, 0],
            ViewHorizontalDirection = [0, 1, 0],
            ViewVerticalDirection = [0, 0, 1],
        };
    }

    [Theory]
    [InlineData(true, 10, true, false, false)]
    [InlineData(true, 10, true, true, false)]
    [InlineData(false, 10, false, false, false)]
    [InlineData(true, 6.5, false, false, false)]
    [InlineData(true, 10, true, false, true)]
    [InlineData(true, 10, true, true, true)]
    [InlineData(false, 10, false, false, true)]
    [InlineData(true, 6.5, false, false, true)]
    public void Collect_ProjectsClosedSectionMeshWithoutItsFacetSeams(
        bool closed, double planeX, bool projected, bool covered, bool fineEquipment)
    {
        var view = SectionView(1, [planeX, 0, 0]);
        if (fineEquipment)
        {
            Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 50);
            view.ViewCrop = new ViewCropFrame(planeX, 0, 0, [0, 1, 0], [0, 0, 1], [-1, 0, 0], -3, -3, 5, 5, true);
        }
        var model = QuadWall(2, [[7, 0, 0], [7, 2, 0], [7, 2, 1], [7, 0, 1]], [1, 0, 0],
            (int)(fineEquipment ? BuiltInCategory.OST_SpecialityEquipment : BuiltInCategory.OST_GenericModel));
        if (fineEquipment)
        {
            model.NativeRootClassName = "FamilyInstance";
            model.NativeRootClassChain = ["FamilyInstance"];
        }
        var geometry = model.Geometry!;
        geometry.Faces[0].GeometryNode = 11;
        geometry.Faces[0].MeshMarker = 1;
        geometry.Faces.AddRange([
            new() { OuterVertices = [[7, 0, 1], [7, 2, 1], [7, 2, 2], [7, 0, 2]], Normal = [1, 0, 0], GeometryNode = 11, MeshMarker = 1 },
            new() { OuterVertices = [[6, 0, 0], [6, 0, 2], [6, 2, 2], [6, 2, 0]], Normal = [-1, 0, 0], PlaneSurface = true },
            new() { OuterVertices = [[6, 0, 0], [7, 0, 0], [7, 0, 2], [6, 0, 2]], Normal = [0, -1, 0], PlaneSurface = true },
            new() { OuterVertices = [[6, 2, 0], [6, 2, 2], [7, 2, 2], [7, 2, 0]], Normal = [0, 1, 0], PlaneSurface = true },
            new() { OuterVertices = [[6, 0, 0], [6, 2, 0], [7, 2, 0], [7, 0, 0]], Normal = [0, 0, -1], PlaneSurface = true },
            new() { OuterVertices = [[6, 0, 2], [7, 0, 2], [7, 2, 2], [6, 2, 2]], Normal = [0, 0, 1], PlaneSurface = true },
        ]);
        int bodyFaceCount = geometry.Faces.Count;
        geometry.Faces.AddRange(geometry.Faces.Take(bodyFaceCount).Select(face => new GeoFace
        {
            OuterVertices = face.OuterVertices!.Select(point => new[] { point[0] - 2, point[1], point[2] }).ToList(),
            Normal = face.Normal, PlaneSurface = true,
        }).ToArray());
        geometry.BrepSolids = [
            new() { IsClosed = closed, FaceIndices = Enumerable.Range(0, bodyFaceCount).ToList() },
            new() { IsClosed = true, FaceIndices = Enumerable.Range(bodyFaceCount, bodyFaceCount).ToList() },
        ];
        geometry.MeshNodes = [new GeometryNode(11, 0, [new BrFace { Marker = 1, SurfaceKind = SurfaceKind.Revolved, Loops = [] }], 3)];
        if (fineEquipment)
        {
            var second = geometry.Faces[bodyFaceCount];
            second.GeometryNode = 12;
            second.MeshMarker = 2;
            second.PlaneSurface = false;
            geometry.MeshNodes.Add(new GeometryNode(12, 0,
                [new BrFace { Marker = 2, SurfaceKind = SurfaceKind.Revolved, Loops = [] }], 3));
            geometry.Faces.Add(new GeoFace
            {
                OuterVertices = [[6, 0, 0], [7, 0, 1], [6, 0, 2]], Normal = [0, 1, 0],
            });
        }
        var document = covered
            ? Document(view, model, QuadWall(3, [[8, -1, -1], [8, 3, -1], [8, 3, 3], [8, -1, 3]], [1, 0, 0]))
            : Document(view, model);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };

        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;

        var edges = content.Edges.Where(edge => edge.CategoryId == model.CategoryId).ToList();
        Assert.Equal(projected && !covered, edges.Any(edge => edge.SilhouetteOwnerId == model.Id));
        if (!projected) return;
        if (covered)
        {
            Assert.Empty(edges);
            return;
        }
        Assert.Contains(edges, edge => edge.Y1 == 0 && edge.Y2 == 0);
        Assert.Contains(edges, edge => edge.Y1 == 2 * FeetToMillimetres && edge.Y2 == edge.Y1);
        Assert.DoesNotContain(edges, edge => edge.Y1 == FeetToMillimetres && edge.Y2 == edge.Y1);
    }

    [Theory]
    [InlineData(true, 20)]
    [InlineData(false, 20)]
    [InlineData(true, 100)]
    [InlineData(false, 100)]
    [InlineData(true, 100, "east", true, true)]
    [InlineData(false, 100, "east", true, true)]
    [InlineData(true, 100, "east", true, false)]
    [InlineData(true, 100, "east", false, true)]
    [InlineData(true, 100, "east", true, true, true)]
    [InlineData(false, 100, "east", false, true)]
    [InlineData(true, 100, "east", false, true, true)]
    [InlineData(true, 100, "east", false, true, false, true)]
    [InlineData(true, 100, "east", false, true, false, false, true)]
    [InlineData(true, 100, "east", true, true, false, false, true)]
    [InlineData(true, 100, "east", false, true, false, false, false, true)]
    public void Collect_ProjectsThinRoundRailWithItsVerifiedPlacedFaces(bool matchingFaces, int scale,
        string direction = "section", bool inclined = false, bool surfacePatterns = false, bool occluded = false,
        bool rotate = false, bool rotateAboutZ = false, bool generalPlacement = false)
    {
        const double radius = 1d / 192;
        double rise = inclined ? 0.25 : 0;
        double run = Math.Sqrt(1 - rise * rise);
        // The general case slants the rail along all three axes so that every term of the placement matrix reaches the
        // printed endpoints.
        double[] axis = generalPlacement ? [2d / 11, 6d / 11, 9d / 11] : [0, run, rise];
        double[] ringX = generalPlacement ? [6 / Math.Sqrt(40), -2 / Math.Sqrt(40), 0] : [1, 0, 0];
        double[] ringY =
        [
            ringX[1] * axis[2] - ringX[2] * axis[1], ringX[2] * axis[0] - ringX[0] * axis[2],
            ringX[0] * axis[1] - ringX[1] * axis[0],
        ];
        var view = SectionView(1, [10, 0, 2]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, scale);
        var symbol = new Element { Id = 3, NativeRecordClass = 492, Geometry = new GeometryData() };
        double c = rotate || rotateAboutZ ? 0.8 : 1, s = rotate || rotateAboutZ ? 0.6 : 0;
        // The rows are the placement matrix; the origin is non-zero when the rail turns about Z so a dropped or
        // transposed origin and a transposed matrix each move the printed endpoints. The general rotation has nine
        // distinct non-zero terms (a unit quaternion turned into integers over 57), so a mixed row or a transposed
        // matrix cannot land on the same endpoints.
        double[] transform = generalPlacement
            ? [47d / 57, -32d / 57, -4d / 57, -16d / 57, -17d / 57, -52d / 57, 28d / 57, 44d / 57, -23d / 57, 0.5, 0.25, 0.75]
            : rotateAboutZ ? [c, -s, 0, s, c, 0, 0, 0, 1, 0.5, 0.25, 0.75] : [1, 0, 0, 0, c, -s, 0, s, c, 0, 0, 0];
        double[] Place(double[] point) =>
        [
            transform[9] + transform[0] * point[0] + transform[1] * point[1] + transform[2] * point[2],
            transform[10] + transform[3] * point[0] + transform[4] * point[1] + transform[5] * point[2],
            transform[11] + transform[6] * point[0] + transform[7] * point[1] + transform[8] * point[2],
        ];
        var rail = new Element
        {
            Id = 2, CategoryId = (int)BuiltInCategory.OST_StairsRailing, SerializedBaseRailingSymbolId = symbol.Id,
            Geometry = new GeometryData
            {
                Instances = [new GeoInstance { SymbolId = symbol.Id, Transform = transform }],
            },
        };
        for (int facet = 0; facet < 32; facet++)
        {
            double[] Point(int index, double distance)
            {
                double angle = index * Math.PI / 16;
                double along = radius * Math.Cos(angle), across = radius * Math.Sin(angle);
                return
                [
                    9 + distance * axis[0] + along * ringX[0] + across * ringY[0],
                    distance * axis[1] + along * ringX[1] + across * ringY[1],
                    2 + distance * axis[2] + along * ringX[2] + across * ringY[2],
                ];
            }
            List<double[]> ring = [Point(facet, 0), Point(facet + 1, 0), Point(facet + 1, 11), Point(facet, 11)];
            uint face = facet < 16 ? 10u : 11u;
            symbol.Geometry.Faces.Add(new GeoFace
            {
                CurvedSurface = true, AnalyticCylinderOrigin = [9, 0, 2], AnalyticCylinderAxis = axis,
                AnalyticCylinderRadius = radius, SourceFace = face, OuterVertices = ring,
            });
            rail.Geometry.Faces.Add(new GeoFace
            {
                MeshMarker = !matchingFaces && facet == 0 ? 0 : face,
                OuterVertices = ring.Select(Place).ToList(),
                Normal = [1, 0, 0],
            });
        }

        bool elevation = direction == "east";
        // A rail turned about Z crosses the plane the usual wall stands in, so that wall is moved behind the whole rail.
        double wallDepth = occluded ? 9.5 : generalPlacement ? 4 : rotateAboutZ ? -2 : 8;
        var wall = QuadWall(4, [[wallDepth, -1, 0], [wallDepth, 12, 0], [wallDepth, 12, 7], [wallDepth, -1, 7]],
            [1, 0, 0]);
        var document = elevation ? Document(rail, symbol, wall) : Document(rail, symbol);
        if (surfacePatterns) document.RawData.OpaqueViewsWithSurfacePatterns = new HashSet<int> { view.Id };
        var content = ElevationPrinter.Collect(document, direction, planeView: view, isDetail: scale == 20);
        if (elevation)
        {
            Assert.NotNull(content);
            Assert.Single(content.Faces);
            var tubes = content.Edges.Where(edge => edge.CategoryId == rail.CategoryId
                && Math.Abs(edge.X2 - edge.X1) > 2500).ToList();
            if (!matchingFaces || !surfacePatterns || occluded)
                Assert.Empty(tubes);
            else
            {
                var side = Assert.Single(tubes);
                double[] start = Place([9, 0, 2]), end = Place([9 + 11 * axis[0], 11 * axis[1], 2 + 11 * axis[2]]);
                Assert.Equal(start[1] * FeetToMillimetres, side.X1, 6);
                Assert.Equal(start[2] * FeetToMillimetres, side.Y1, 6);
                Assert.Equal(end[1] * FeetToMillimetres, side.X2, 6);
                Assert.Equal(end[2] * FeetToMillimetres, side.Y2, 6);
            }
            return;
        }

        if (!matchingFaces)
        {
            Assert.Null(content);
            return;
        }
        Assert.NotNull(content);
        var projection = Assert.Single(content.Faces);
        Assert.Equal((2 - radius) * FeetToMillimetres, projection.Points.Min(point => point.Y), 6);
        Assert.Equal((2 + radius) * FeetToMillimetres, projection.Points.Max(point => point.Y), 6);
        var sides = content.Edges.Where(edge => Math.Abs(edge.X2 - edge.X1) > 3000).ToList();
        Assert.Equal(scale == 20 ? 2 : 1, sides.Count);
    }

    [Theory]
    [InlineData(50, true, false, false, false)]
    [InlineData(50, true, true, false, false)]
    [InlineData(50, false, false, false, false)]
    [InlineData(100, true, true, false, false)]
    [InlineData(100, false, false, false, false)]
    [InlineData(100, true, true, true, false)]
    [InlineData(100, false, false, true, false)]
    [InlineData(100, true, false, true, true)]
    [InlineData(100, true, false, true, false, true)]
    [InlineData(100, true, false, true, false, true, true)]
    [InlineData(100, true, false, false, false, false, false, true)]
    [InlineData(100, true, false, true, false, false, false, true)]
    public void Collect_ProjectsAVerifiedRoundShaftWithItsSurfacePattern(int scale, bool matchingPlacement,
        bool backingWall, bool genericModel, bool missingTangent, bool foregroundWall = false, bool opening = false,
        bool placed = false)
    {
        var view = SectionView(900, [0, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, scale);
        view.ViewCrop = new ViewCropFrame(0, 0, 0, [0, 1, 0], [0, 0, 1], [-1, 0, 0], -3, -1, 3, 6, true);
        var material = new Element { Id = 901, HasShadingAppearance = true, SerializedMaterialUsesRenderAppearance = true };
        Add(material, BuiltInParameter.MATERIAL_PARAM_COLOR, StorageType.Integer, 0xc0c0c0);
        var solid = new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] };
        var symbol = new Element
        {
            Id = 902, NativeRootClassName = "FamilySymbol", Geometry = new GeometryData { BrepSolids = [solid] },
        };
        // A placed shaft has its cylinder off the symbol origin and turns about Z, so its centre needs the whole placement.
        double c = placed ? 0.8 : 1, s = placed ? 0.6 : 0;
        double[] axisOrigin = placed ? [0.5, 1, 0] : [0, 0, 0];
        double[] transform = [c, -s, 0, s, c, 0, 0, 0, 1, -4, placed ? 0.5 : 0, 0];
        double[] Place(double[] point) =>
            [transform[9] + c * point[0] - s * point[1], transform[10] + s * point[0] + c * point[1], point[2]];
        double centreAcross = Place(axisOrigin)[1];
        var column = new Element
        {
            Id = 903, CategoryId = (int)(genericModel ? BuiltInCategory.OST_GenericModel : BuiltInCategory.OST_StructuralColumns),
            Geometry = new GeometryData
            {
                Instances = [new() { SymbolId = symbol.Id, Transform = transform }],
                BrepSolids = [solid],
            },
        };
        double half = Math.Sqrt(0.5);
        double[][] bottom = [[axisOrigin[0] + half, axisOrigin[1] + half, 0], [axisOrigin[0] - half, axisOrigin[1] + half, 0],
            [axisOrigin[0] - half, axisOrigin[1] - half, 0], [axisOrigin[0] + half, axisOrigin[1] - half, 0]];
        var top = bottom.Select(point => new[] { point[0], point[1], 4d }).ToArray();
        AddFace([.. bottom], [0, 0, -1], side: false);
        AddFace([.. top], [0, 0, 1], side: false);
        double[][] normals = [[0, 1, 0], [-1, 0, 0], [0, -1, 0], [1, 0, 0]];
        for (int i = 0; i < bottom.Length; i++)
        {
            int next = (i + 1) % bottom.Length;
            if (missingTangent && i == 0) continue;
            AddFace([bottom[i], bottom[next], top[next], top[i]], normals[i], side: true, handle: 277u + (uint)i / 2);
        }
        if (missingTangent) solid.FaceIndices.RemoveAt(solid.FaceIndices.Count - 1);
        if (!matchingPlacement) column.Geometry!.Faces[genericModel ? 2 : 0].OuterVertices![0][0] += 0.1;
        double wallDepth = foregroundWall ? -2 : -8;
        double wallTop = foregroundWall ? 1.5 : 2;
        var wall = QuadWall(905, [[wallDepth, -2, 0], [wallDepth, 2, 0], [wallDepth, 2, wallTop], [wallDepth, -2, wallTop]], [1, 0, 0]);
        wall.Geometry!.Faces[0].PlaneSurface = foregroundWall;
        if (opening)
            wall.Geometry.Faces[0].InnerVertices = [[[wallDepth, -1.2, 0.35], [wallDepth, 1.2, 0.35],
                [wallDepth, 1.2, 0.95], [wallDepth, -1.2, 0.95]]];
        var document = backingWall || foregroundWall ? Document(view, symbol, column, material, wall) : Document(view, symbol, column, material);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        document.RawData.OpaqueViewsWithSurfacePatterns = new HashSet<int> { view.Id };
        var grid = new TfBlockDeserializer.PatternGrid(0, 0, 0, 0, 0.01, [0.001, 0.01], Target: 0, Orientation: 0);
        document.RawData.MaterialPatterns = new Dictionary<int, (int, int, int?)> { [material.Id] = (-1, 904, 0) };
        document.RawData.FillPatterns = new Dictionary<int, (string, IReadOnlyList<TfBlockDeserializer.PatternGrid>)>
            { [904] = ("Surface", [grid]) };

        var content = ElevationPrinter.Collect(document, genericModel ? "east" : "section", planeView: view)!;
        if (matchingPlacement && !missingTangent)
        {
            var face = Assert.Single(content.Faces, face => content.Hatch!.DashedFaces.ContainsKey(face.Points));
            Assert.Equal((centreAcross - 1) * FeetToMillimetres, face.Points.Min(point => point.X), 6);
            Assert.Equal((centreAcross + 1) * FeetToMillimetres, face.Points.Max(point => point.X), 6);
            Assert.Same(grid, Assert.Single(Assert.Single(content.Hatch!.DashedFaces).Value));
            if (genericModel)
            {
                var outline = content.Edges.Where(edge => edge.CategoryId == column.CategoryId
                    && Math.Abs(edge.X1 - edge.X2) < 1e-6).ToList();
                Assert.Contains(outline, edge => Math.Abs(edge.X1 - (centreAcross - 1) * FeetToMillimetres) < 1e-6);
                Assert.Contains(outline, edge => Math.Abs(edge.X1 - (centreAcross + 1) * FeetToMillimetres) < 1e-6);
                if (foregroundWall)
                {
                    Assert.Equal(opening ? 4 : 2, outline.Count);
                    foreach (var edge in outline)
                    {
                        double low = Math.Min(edge.Y1, edge.Y2) / FeetToMillimetres;
                        double high = Math.Max(edge.Y1, edge.Y2) / FeetToMillimetres;
                        if (opening && low < wallTop)
                        {
                            Assert.Equal(0.35, low, 6);
                            Assert.Equal(0.95, high, 6);
                        }
                        else
                        {
                            Assert.Equal(wallTop, low, 6);
                            Assert.Equal(4, high, 6);
                        }
                    }
                }
            }
            if (scale == 50) Assert.Equal(192d / 255, Assert.Single(content.FaceFills!).Value!.Value, 6);
            else Assert.Null(content.FaceFills);
            if (backingWall)
            {
                var topEdges = content.Edges.Where(edge => edge.CategoryId == wall.CategoryId
                    && edge.Y1 == 2 * FeetToMillimetres && edge.Y2 == 2 * FeetToMillimetres).ToArray();
                Assert.Equal(2, topEdges.Length);
                Assert.All(topEdges, edge => Assert.True(Math.Max(edge.X1, edge.X2) <= -FeetToMillimetres + 0.01
                    || Math.Min(edge.X1, edge.X2) >= FeetToMillimetres - 0.01));
            }
        }
        else
        {
            Assert.Null(content.Hatch);
            Assert.All(content.Faces, face => Assert.All(face.Points,
                point => Assert.InRange(point.X, -half * FeetToMillimetres, half * FeetToMillimetres)));
        }

        void AddFace(List<double[]> ring, double[] normal, bool side, uint handle = 0)
        {
            symbol.Geometry!.Faces.Add(new GeoFace
            {
                OuterVertices = ring, Normal = normal, PlaneSurface = !side, CurvedSurface = side, SourceFace = handle,
                AnalyticCylinderOrigin = side ? axisOrigin : null, AnalyticCylinderAxis = side ? [0, 0, 1] : null,
                AnalyticCylinderRadius = side ? 1 : 0, RenderStyleId = material.Id,
            });
            column.Geometry!.Faces.Add(new GeoFace
            {
                OuterVertices = ring.Select(Place).ToList(),
                Normal = normal, PlaneSurface = !side, RenderStyleId = material.Id,
            });
        }
    }

    [Theory]
    [InlineData(false, BuiltInCategory.OST_Casework)]
    [InlineData(true, BuiltInCategory.OST_Casework)]
    [InlineData(false, BuiltInCategory.OST_StructuralColumns)]
    [InlineData(true, BuiltInCategory.OST_StructuralColumns)]
    [InlineData(false, BuiltInCategory.OST_PlumbingFixtures)]
    [InlineData(true, BuiltInCategory.OST_PlumbingFixtures)]
    [InlineData(false, BuiltInCategory.OST_Furniture)]
    [InlineData(true, BuiltInCategory.OST_Furniture)]
    [InlineData(false, BuiltInCategory.OST_StructuralFoundation)]
    [InlineData(true, BuiltInCategory.OST_StructuralFoundation)]
    [InlineData(false, BuiltInCategory.OST_SpecialityEquipment)]
    [InlineData(true, BuiltInCategory.OST_SpecialityEquipment)]
    public void Collect_UsesTheExactElementTableForPlacedFamiliesInOverviewSections(bool exactTable,
        BuiltInCategory category)
    {
        var view = SectionView(900, [0, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 100);
        var placed = QuadWall(901, [[-4, -4, 0], [-4, -1, 0], [-4, -1, 3], [-4, -4, 3]], [1, 0, 0],
            (int)category);
        var recovered = QuadWall(902, [[-4, 1, 0], [-4, 4, 0], [-4, 4, 3], [-4, 1, 3]], [1, 0, 0],
            (int)category);
        if (category != BuiltInCategory.OST_Casework)
        {
            recovered.NativeRootClassName = "FamilySymbol";
            recovered.NativeRootClassChain = ["FamilySymbol"];
        }
        else recovered.IsDiscovered = true;
        var document = Document(view, placed, recovered);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        document.RawData.OpaqueViewsWithSurfacePatterns = new HashSet<int> { view.Id };
        document.RawData.ElemTableIsExact = exactTable;

        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;

        Assert.Contains(content.Edges, edge => edge.X1 < 0 && edge.X2 < 0);
        Assert.Equal(!exactTable, content.Edges.Any(edge => edge.X1 > 0 && edge.X2 > 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Collect_UsesTheSavedJogToChangeSectionDepth(bool reversed)
    {
        const int viewerId = 800;
        var view = new Element
        {
            Id = 900, SectionViewerId = viewerId, ViewOrigin = [0, 0, 0], ViewDirection = [0, 1, 0],
            ViewHorizontalDirection = [1, 0, 0], ViewVerticalDirection = [0, 0, 1],
        };
        var near = QuadWall(200, [[-1, 2, 0], [5, 2, 0], [5, 2, 6], [-1, 2, 6]], [0, -1, 0]);
        var far = QuadWall(201, [[-1, 4, 0], [5, 4, 0], [5, 4, 3], [-1, 4, 3]], [0, -1, 0]);
        var document = Document(view, near, far);
        double[][] frontCut = [[0, 0, 0], [2, 0, 0], [2, 3, 0], [4, 3, 0]];
        if (reversed) Array.Reverse(frontCut);
        document.RawData.SectionViews = new Dictionary<int, (int, double, IReadOnlyList<double[]>?)>
            { [viewerId] = (-1, 0, frontCut) };

        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;
        var bands = content.SectionBands!;

        Assert.Equal(2, bands.Count);
        Assert.Equal(double.NegativeInfinity, bands[0].Left);
        Assert.Equal(2 * FeetToMillimetres, bands[0].Right, 6);
        Assert.Equal(bands[0].Right, bands[1].Left);
        Assert.Equal(double.PositiveInfinity, bands[1].Right);
        Assert.Equal(6 * FeetToMillimetres, bands[0].Content.MaxY, 6);
        Assert.Equal(3 * FeetToMillimetres, bands[1].Content.MaxY, 6);
        var page = new PdfDocument().AddPage(PdfPageSize.A1.Landscape);
        ElevationPrinter.DrawContent(page, content, 100, 0, 0, 0.82);
        Assert.Matches(@"(?m)^(0 51\.84 m 25\.92 51\.84|25\.92 51\.84 m 0 51\.84) l S$", page.Content);
        Assert.Matches(@"(?m)^(25\.92 25\.92 m 51\.84 25\.92|51\.84 25\.92 m 25\.92 25\.92) l S$", page.Content);
        var detail = ElevationPrinter.Collect(document, "section", planeView: view, isDetail: true,
            cropWindow: (0, 0, 500, 2000))!;
        Assert.Null(detail.SectionBands);
    }

    [SkippableFact]
    public void SampleFile_UsesTheNativeCutDepthInBuildingSection()
    {
        string path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.Find("rac_basic_sample_project.rvt");
        var document = RvtDocument.Open(path);
        var view = Assert.Single(document.Elements, element =>
            element.CategoryId == (int)BuiltInCategory.OST_Views && element.Name == "Building Section");
        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;
        var deeperBand = content.SectionBands![1];

        Assert.Equal(13.316265 * FeetToMillimetres, deeperBand.Left, 3);
        Assert.Contains(deeperBand.Content.Edges, edge => edge.IsCut
            && edge.CategoryId == (int)BuiltInCategory.OST_Roofs
            && Math.Max(edge.X1, edge.X2) - Math.Max(deeperBand.Left, Math.Min(edge.X1, edge.X2)) > 10_000);
    }

    [Fact]
    public void TrimProjectedEdgesAtCuts_PreservesTheCutAndUncoveredProjection()
    {
        var projection = new ElevationPrinter.CollectedEdge(0, 0, 1000, 0, 100, 100, 0, -1, 0, 0, 0, 0);
        var cut = projection with { X1 = 250, X2 = 750, Depth = 0, MidDepth = 0, IsCut = true };
        var parallel = projection with { Y1 = 2, Y2 = 2 };
        var symbol = projection with { ViewLineStyleId = 7 };
        var result = ElevationPrinter.TrimProjectedEdgesAtCuts([projection, cut, parallel, symbol]).ToArray();

        Assert.Contains(cut, result);
        Assert.Contains(parallel, result);
        Assert.Contains(symbol, result);
        Assert.Equal(new[] { (0.0, 250.0), (750.0, 1000.0) },
            result.Where(edge => !edge.IsCut && edge.ViewLineStyleId == 0 && edge.Y1 == 0)
                .Select(edge => (edge.X1, edge.X2)));
    }

    [Theory]
    [InlineData(2, 0, 1)]
    [InlineData(3, 0, 2)]
    [InlineData(2, 150, 2)]
    public void DrawContent_TrimsProjectionOnlyBehindACutWithACoveringPen(int cutPen, double cutDepth, int strokeCount)
    {
        int walls = (int)BuiltInCategory.OST_Walls, roofs = (int)BuiltInCategory.OST_Roofs;
        var projection = new ElevationPrinter.CollectedEdge(0, 300, 1000, 300, 100, 100, 0, -1,
            0, 0, 0, 0, CategoryId: roofs);
        var cut = projection with { X1 = 1000, X2 = 0, Depth = cutDepth, MidDepth = cutDepth, IsCut = true };
        var content = new ElevationPrinter.ElevationContent([], [], [], [], [projection, cut], 0, 0, 1000, 600);
        var document = Document();
        document.RawData.GraphicsStylePens = new Dictionary<int, (int, int, int, int, int, int)>
        {
            [1] = (walls, -1, 1, 1, 0, 0),
            [2] = (roofs, -1, 2, cutPen, 0, 0),
        };
        document.RawData.PenWidthTable = (new Dictionary<int, double[]>
        {
            [100] = [0.54 / 864, 1.0 / 864, 0.26 / 864],
        }, []);
        var page = new PdfDocument().AddPage(PdfPageSize.A1.Landscape);

        ElevationPrinter.DrawContent(page, content, 100, 0, 0, 0.82,
            document: document, clipRect: (0, 0, 1000, 600));

        Assert.Equal(strokeCount, System.Text.RegularExpressions.Regex.Matches(page.Content, @"(?m) l S$").Count);
        Assert.Contains(cutPen == 2 ? "1 w" : "0.26 w", page.Content);
    }

    [Fact]
    public void MergeOverlaps_PreservesTheWiderCutPenOnlyWhereItsSourceSpans()
    {
        int walls = (int)BuiltInCategory.OST_Walls;
        int floors = (int)BuiltInCategory.OST_Floors;
        var wall = new ElevationPrinter.CollectedEdge(0, 0, 1000, 0, 0, 0, -1, -1, 1, 0, 0, 0,
            IsCut: true, CategoryId: walls);
        var slab = wall with { X1 = 400, X2 = 850, CategoryId = floors };
        var separateSlab = slab with { X1 = 1200, X2 = 1400 };

        var result = ElevationPrinter.MergeOverlaps([wall, slab, separateSlab],
            edge => edge.CategoryId == floors ? 0.7 : 0.35);

        Assert.Equal(new[] { (0.0, 400.0, walls), (400.0, 850.0, floors), (850.0, 1000.0, walls),
                (1200.0, 1400.0, floors) },
            result.OrderBy(edge => edge.X1).Select(edge => (edge.X1, edge.X2, edge.CategoryId)));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ResolveRoofWallCutJoins_UsesProjectionPenOnOppositeCutFaces(bool opposite, bool reverse)
    {
        var roof = new List<(double X, double Y)> { (0, 0), (10, 0), (10, 8), (0, 8) };
        var wall = new List<(double X, double Y)> { (0, 3), (0, 10), (-5, 10), (-5, 3) };
        if (!opposite)
            wall = [(0, 3), (5, 3), (5, 10), (0, 10)];
        if (reverse)
            wall.Reverse();

        const int roofs = (int)BuiltInCategory.OST_Roofs;
        const int walls = (int)BuiltInCategory.OST_Walls;
        var roofCut = new ElevationPrinter.CollectedEdge(0, 0, 0, 8, 1, 1, -1, -1, 1, 0, 0, 2,
            IsCut: true, CategoryId: roofs);
        var wallCut = roofCut with { Y1 = 3, Y2 = 10, CategoryId = walls };
        var result = ElevationPrinter.ResolveRoofWallCutJoins([roofCut, wallCut], [roof], [wall], 1, (1, 0, 0, 2));

        if (opposite)
        {
            Assert.Equal(new[] { (0.0, 3.0, true), (3.0, 8.0, false), (8.0, 10.0, true) },
                result.OrderBy(edge => edge.Y1).Select(edge => (edge.Y1, edge.Y2, edge.IsCut)));
        }
        else
        {
            Assert.Equal(new[] { roofCut, wallCut }, result);
        }
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_Walls, true)]
    [InlineData(BuiltInCategory.OST_Walls, false)]
    [InlineData(BuiltInCategory.OST_Stairs, true)]
    [InlineData(BuiltInCategory.OST_Stairs, false)]
    public void Collect_CapsFineSectionWallsAndStairsOnlyWhenTheirSolidsAreClosed(BuiltInCategory category, bool closed)
    {
        var view = SectionView(900, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 20);
        view.ViewCrop = new ViewCropFrame(10, 0, 0, [0, 1, 0], [0, 0, 1], [-1, 0, 0], -1, -1, 5, 5, true);
        var element = QuadWall(901, [[11, 0, 0], [11, 2, 0], [11, 2, 2], [11, 0, 2]], [1, 0, 0], (int)category);
        element.BoundingBox = [9, 0, 0, 11, 2, 2];
        var geometry = element.Geometry!;
        geometry.Faces.AddRange([
            Face([[9, 0, 0], [9, 0, 2], [9, 2, 2], [9, 2, 0]], [-1, 0, 0]),
            Face([[9, 0, 0], [11, 0, 0], [11, 0, 2], [9, 0, 2]], [0, -1, 0]),
            Face([[9, 2, 0], [9, 2, 2], [11, 2, 2], [11, 2, 0]], [0, 1, 0]),
            Face([[9, 0, 0], [9, 2, 0], [11, 2, 0], [11, 0, 0]], [0, 0, -1]),
            Face([[9, 0, 2], [11, 0, 2], [11, 2, 2], [9, 2, 2]], [0, 0, 1]),
        ]);
        var material = new Element { Id = 902, CategoryId = (int)BuiltInCategory.OST_Materials };
        foreach (var face in geometry.Faces)
        {
            face.PlaneSurface = true;
            if (category == BuiltInCategory.OST_Stairs) face.RenderStyleId = material.Id;
        }
        geometry.BrepSolids = [new() { IsClosed = closed, FaceIndices = [0, 1, 2, 3, 4, 5] }];
        var document = Document(view, element, material);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };

        var content = ElevationPrinter.Collect(document, "section", planeView: view, viewportScale: 20)!;

        if (closed)
            Assert.Equal(4 * FeetToMillimetres * FeetToMillimetres,
                Math.Abs(PlanPrinter.SignedArea(Assert.Single(content.DetailCutFaces!))), 6);
        else
            Assert.Null(content.DetailCutFaces);
        Assert.Contains(content.Edges, edge => edge.IsCut && edge.CategoryId == (int)category);

        static GeoFace Face(double[][] points, double[] normal) => new() { OuterVertices = [.. points], Normal = normal };
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    public void AmbientOcclusion_IgnoresExitHitsBetweenVerifiedStairSolids(
        bool oneSided, bool exitHit, bool stairReceiver, bool castsShadow)
    {
        var ambient = new ElevationPrinter.AmbientOcclusion(0.5, (0, 0, 10, 10));
        var up = System.Numerics.Vector3.UnitZ;
        ambient.AddOccluder(
            [new(-100, -100, -20), new(100, -100, -20), new(100, 100, -20), new(-100, 100, -20)],
            exitHit ? -up : up, frontFaceOnly: oneSided);
        List<(double X, double Y)> receiver = [(0, 0), (10, 0), (10, 10), (0, 10)];
        ambient.AddReceiver(receiver, null, System.Numerics.Vector3.Zero, -up, ignoreStairExitHits: stairReceiver);

        Assert.Equal(castsShadow, ambient.Calculate()?.ContainsKey(receiver) == true);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void AmbientOcclusion_UsesProjectedOccludersForAnAlignedStairFace(
        bool edgeOn, bool projectOccluders, bool castsShadow)
    {
        var ambient = new ElevationPrinter.AmbientOcclusion(0.5, (0, 0, 10, 10));
        if (edgeOn)
            ambient.AddOccluder(
                [new(-20, -100, -100), new(-20, 100, -100), new(-20, 100, 0), new(-20, -100, 0)],
                System.Numerics.Vector3.UnitX);
        else
            ambient.AddOccluder(
                [new(-100, -100, -20), new(100, -100, -20), new(100, 100, -20), new(-100, 100, -20)],
                System.Numerics.Vector3.UnitZ);
        List<(double X, double Y)> receiver = [(0, 0), (10, 0), (10, 10), (0, 10)];
        ambient.AddReceiver(receiver, null, System.Numerics.Vector3.Zero, -System.Numerics.Vector3.UnitZ,
            ignoreEdgeOnHits: projectOccluders);

        Assert.Equal(castsShadow, ambient.Calculate()?.ContainsKey(receiver) == true);
    }

    [Theory]
    [InlineData(-2, -1, false)]
    [InlineData(-2, 0, false)]
    [InlineData(-2, 1, true)]
    public void StairCutRequiresAFaceCrossingTheSectionPlane(double start, double end, bool crosses)
    {
        List<GeoFace> faces =
        [
            new() { OuterVertices = [[start, 0, 0], [end, 1, 0], [end, 0, 0]] },
            new() { OuterVertices = [[2, 0, 0], [2, 1, 0], [2, 0, 1]] },
        ];
        Assert.Equal(crosses, ElevationPrinter.SpansSectionPlane(faces, [1, 0, 0], 0));
    }

    [Fact]
    public void Collect_CutsAtTheViewPlaneInFeet()
    {
        // Two walls five metres apart along X; a section looking -X with its plane at x = 10 ft keeps
        // the west wall and cuts the east one. The plane origin is file feet, like ViewOrigin documents.
        var view = SectionView(900, [10, 0, 5]);
        var document = Document(
            QuadWall(600, [[3, 0, 0], [3, 4, 0], [3, 4, 3], [3, 0, 3]], [1, 0, 0]),
            QuadWall(601, [[20, 0, 0], [20, 4, 0], [20, 4, 3], [20, 0, 3]], [1, 0, 0]),
            view);

        var content = ElevationPrinter.Collect(document, "section", false, view, null);

        // The west wall (x = 3 ft, in front of the 10 ft plane) stays; the east wall is cut away.
        Assert.NotNull(content);
        Assert.Single(content!.Faces);
        Assert.All(content.Faces[0].Points, point => Assert.True(point.X <= 10 * FeetToMillimetres + 1));
    }

    [SkippableFact]
    public void SampleFile_StairSectionShowsTheStair()
    {
        // The stair railing (world y about -13.7..-6.5 m) projects onto content X in that range
        // (the view looks along -X with right axis +Y). A feet-as-millimetres cut plane would drop
        // everything east of x = 40 mm, leaving none of the stair.
        string? path = TestFiles.TryFind("Test_2022.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var view = document.Elements.FirstOrDefault(e =>
            e.CategoryId == (int)BuiltInCategory.OST_Views && e.Name == "Stair Section");
        Assert.NotNull(view);

        var content = ElevationPrinter.Collect(document, "section", true, view, null);

        Assert.NotNull(content);
        Assert.Contains(content!.Edges, edge => Math.Min(edge.X1, edge.X2) < -6000);
    }

    [SkippableFact]
    public void SampleFile_RoofDetailDrawsItsOwnAnnotationCurves()
    {
        // A detail's annotation lines belong to the view itself: the file stores them as curve elements that
        // name the view as owner (OST_InsulationLines and OST_Lines), in model feet on the view plane, and no
        // solid face reaches them. Without this the roof detail printed only its wall and window.
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        Element? view = null;
        foreach (var e in document.Elements)
            if (e.CategoryId == (int)BuiltInCategory.OST_Views && e.Name == "Typical Wall Roof Connection") view = e;
        Assert.NotNull(view);

        var content = ElevationPrinter.Collect(document, "section", false, view, null, isDetail: true);

        Assert.NotNull(content);
        Assert.Contains(content!.Edges, edge => edge.CategoryId == (int)BuiltInCategory.OST_InsulationLines);
    }

    [SkippableFact]
    public void SampleFile_StairSectionIncludesTreadAndRiserMass()
    {
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var view = Assert.Single(document.Elements, e =>
            e.CategoryId == (int)BuiltInCategory.OST_Views && e.Name == "Stair Section");
        var crop = SheetComposer.CropContentRect(Assert.IsType<ViewCropFrame>(view.ViewCrop));
        var content = Assert.IsType<ElevationPrinter.ElevationContent>(
            ElevationPrinter.Collect(document, "section", planeView: view));
        Assert.Contains(content.Edges, edge => edge is
                { IsCut: true, CategoryId: (int)BuiltInCategory.OST_StairsTrisers }
            && Math.Min(edge.X1, edge.X2) >= crop.X0 && Math.Max(edge.X1, edge.X2) <= crop.X1
            && Math.Min(edge.Y1, edge.Y2) >= crop.Y0 && Math.Max(edge.Y1, edge.Y2) <= crop.Y1);
    }

    [Theory]
    [InlineData(3, false, false, false, true)]
    [InlineData(1, false, false, false, false)]
    [InlineData(3, true, false, false, false)]
    [InlineData(3, false, true, false, false)]
    [InlineData(3, false, false, true, true)]
    [InlineData(3, false, true, true, false)]
    public void Collect_PrintsDirectionalWindowLinesOnlyInTheirVisibleView(int detailLevel, bool sideView,
        bool covered, bool section, bool visible)
    {
        const int styleId = 700;
        var view = SectionView(900, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_DETAIL_LEVEL, StorageType.Integer, detailLevel);
        var window = QuadWall(600, [[9, 0, 0], [9, 4, 0], [9, 4, 3], [9, 0, 3]], [1, 0, 0],
            (int)BuiltInCategory.OST_Windows);
        window.Geometry!.ViewFacingLines = [new([9, 1, 1], [9, 3, 2], sideView ? [0, 1, 0] : [-1, 0, 0],
            styleId, (0, 1))];
        var cover = QuadWall(601, [[9.5, 0, 0], [9.5, 4, 0], [9.5, 4, 3], [9.5, 0, 3]], [1, 0, 0]);
        var document = covered ? Document(view, window, cover) : Document(view, window);
        document.RawData.GraphicsStylePens = new Dictionary<int, (int, int, int, int, int, int)>
        {
            [styleId] = (707, -1, 1, 1, 0, 711),
        };
        document.RawData.LinePatterns = new Dictionary<int, (string, List<(double, int)>)>
        {
            [711] = ("Dash", [(0.01, 0), (0.005, 1)]),
        };
        var content = Assert.IsType<ElevationPrinter.ElevationContent>(
            ElevationPrinter.Collect(document, section ? "section" : "east", planeView: view));
        if (!visible)
        {
            Assert.DoesNotContain(content.Edges, edge => edge.ViewLineStyleId == styleId);
            return;
        }
        var line = Assert.Single(content.Edges, edge => edge.ViewLineStyleId == styleId);
        Assert.Equal(FeetToMillimetres, line.X1, 6);
        Assert.Equal(3 * FeetToMillimetres, line.X2, 6);
        var page = new PdfDocument().AddPage(PdfPageSize.A1.Landscape);
        ElevationPrinter.DrawContent(page, content, 50, 10, 10, document: document);
        Assert.Contains("4.32", page.Content);
        Assert.Contains("0 0 0 RG", page.Content);
    }

    [Theory]
    [InlineData(9.5, 1.75, 2.25, false, true)]
    [InlineData(9.5, 1.25, 1.5, false, true)]
    [InlineData(8.5, 1.75, 2.25, false, false)]
    [InlineData(9.5, 1.25, 1.5, true, false)]
    public void Collect_ClipsWindowSymbolSpansBehindOpaqueFurniture(double coverX,
        double coverStart, double coverEnd, bool transparent, bool clipped)
    {
        const int styleId = 700;
        var view = SectionView(900, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 100);
        var window = QuadWall(600, [[9, 0, 0], [9, 4, 0], [9, 4, 3], [9, 0, 3]], [1, 0, 0],
            (int)BuiltInCategory.OST_Windows);
        window.Geometry!.ViewFacingLines = [new([9, 1, 1], [9, 3, 1], [-1, 0, 0], styleId, null)];
        var cover = QuadWall(601, [[coverX, coverStart, 0], [coverX, coverEnd, 0],
            [coverX, coverEnd, 2], [coverX, coverStart, 2]], [1, 0, 0], (int)BuiltInCategory.OST_Furniture);
        var material = new Element { Id = 602, SerializedMaterialTransparency = transparent ? 0.5 : 0 };
        cover.Geometry!.Faces[0].PlaneSurface = true;
        cover.Geometry.Faces[0].RenderStyleId = material.Id;
        var document = Document(view, window, cover, material);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        document.RawData.GraphicsStylePens = new Dictionary<int, (int, int, int, int, int, int)>
        {
            [styleId] = (707, -1, 1, 1, 0, 711),
        };
        document.RawData.LinePatterns = new Dictionary<int, (string, List<(double, int)>)>
        {
            [711] = ("Dash", [(0.005, 0), (0.0025, 1)]),
        };
        var content = Assert.IsType<ElevationPrinter.ElevationContent>(
            ElevationPrinter.Collect(document, "section", planeView: view));
        var lines = content.Edges.Where(edge => edge.ViewLineStyleId == styleId).OrderBy(edge => edge.X1).ToArray();
        if (!clipped)
        {
            var line = Assert.Single(lines);
            Assert.Equal(FeetToMillimetres, line.X1, 6);
            Assert.Equal(3 * FeetToMillimetres, line.X2, 6);
            Assert.Null(line.UnclippedViewLine);
            return;
        }

        Assert.Equal(2, lines.Length);
        Assert.Equal(FeetToMillimetres, lines[0].X1, 6);
        Assert.Equal(coverStart * FeetToMillimetres, lines[0].X2, 6);
        Assert.Equal(coverEnd * FeetToMillimetres, lines[1].X1, 6);
        Assert.Equal(3 * FeetToMillimetres, lines[1].X2, 6);
        var page = new PdfDocument().AddPage(PdfPageSize.A1.Landscape);
        ElevationPrinter.DrawContent(page, content, 100, 10, 10, document: document);
        var dashCommands = page.Content.Split('\n').Where(command => command.StartsWith('[')
            && !command.StartsWith("[]", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, dashCommands.Length);
        Assert.Equal(dashCommands[0].Split(']')[0], dashCommands[1].Split(']')[0]);
        Assert.EndsWith("] 0 d", dashCommands[0]);
        Assert.EndsWith(coverEnd == 2.25 ? "] 10.8 d" : "] 4.32 d", dashCommands[1]);
    }

    [Theory]
    [InlineData(9.5, 1.25, 2.75, false, 2)]
    [InlineData(9.5, 0.25, 0.75, false, 2)]
    [InlineData(9.5, -0.25, 4.25, false, 0)]
    [InlineData(8.5, 1.25, 2.75, false, 1)]
    [InlineData(9.5, 0.25, 0.75, true, 1)]
    public void Collect_ClipsWindowJambsAtNearerFurniture(double coverX, double bottom, double top,
        bool transparent, int visibleSpans)
    {
        int windowCategory = (int)BuiltInCategory.OST_Windows;
        var view = SectionView(900, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 100);
        var window = QuadWall(600, [[9, 1, 0], [9, 3, 0], [9, 3, 4], [9, 1, 4]], [1, 0, 0], windowCategory);
        var cover = QuadWall(601, [[coverX, 0.5, bottom], [coverX, 1.5, bottom],
            [coverX, 1.5, top], [coverX, 0.5, top]], [1, 0, 0], (int)BuiltInCategory.OST_Furniture);
        var material = new Element { Id = 602, SerializedMaterialTransparency = transparent ? 0.5 : 0 };
        cover.Geometry!.Faces[0].PlaneSurface = true;
        cover.Geometry.Faces[0].RenderStyleId = material.Id;
        var document = Document(view, window, cover, material);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        var content = Assert.IsType<ElevationPrinter.ElevationContent>(
            ElevationPrinter.Collect(document, "section", planeView: view));

        var jambs = content.Edges.Where(edge => edge.CategoryId == windowCategory && edge.X1 == edge.X2).ToArray();
        var rails = content.Edges.Where(edge => edge.CategoryId == windowCategory && edge.Y1 == edge.Y2).ToArray();
        Assert.Equal(2, rails.Length);
        Assert.All(rails, edge =>
        {
            Assert.Equal(FeetToMillimetres, Math.Min(edge.X1, edge.X2), 6);
            Assert.Equal(3 * FeetToMillimetres, Math.Max(edge.X1, edge.X2), 6);
        });
        var right = Assert.Single(jambs, edge => edge.X1 == 3 * FeetToMillimetres);
        Assert.Equal(0, Math.Min(right.Y1, right.Y2), 6);
        Assert.Equal(4 * FeetToMillimetres, Math.Max(right.Y1, right.Y2), 6);

        var left = jambs.Where(edge => edge.X1 == FeetToMillimetres)
            .Select(edge => (Lo: Math.Min(edge.Y1, edge.Y2), Hi: Math.Max(edge.Y1, edge.Y2)))
            .OrderBy(span => span.Lo).ToArray();
        Assert.Equal(visibleSpans, left.Length);
        if (visibleSpans == 0) return;
        Assert.Equal(0, left[0].Lo, 6);
        Assert.Equal(4 * FeetToMillimetres, left[^1].Hi, 6);
        if (visibleSpans == 2)
        {
            Assert.Equal(bottom * FeetToMillimetres, left[0].Hi, 6);
            Assert.Equal(top * FeetToMillimetres, left[1].Lo, 6);
        }
    }

    [Theory]
    [InlineData(9, true, false, 3)]
    [InlineData(9, true, true, 3)]
    [InlineData(7, false, true, 3)]
    [InlineData(9, true, false, 1.5)]
    [InlineData(9, true, true, 1.5)]
    [InlineData(7, false, true, 1.5)]
    public void Collect_ClipsBothRailEdgesOnlyWhereThePostCoversThem(double postDepth, bool inFront,
        bool separateBaluster, double postCenter)
    {
        var view = SectionView(900, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 20);
        int category = (int)BuiltInCategory.OST_StairsRailing;
        const double top = 1 + 3 / FeetToMillimetres;
        var railing = QuadWall(901, [[8, 0, 1], [8, 6, 1], [8, 6, top], [8, 0, top]], [1, 0, 0], category);
        double postLeft = postCenter - 0.1, postRight = postCenter + 0.1;
        var post = QuadWall(902, [[postDepth, postLeft, 0], [postDepth, postRight, 0],
            [postDepth, postRight, 2], [postDepth, postLeft, 2]], [1, 0, 0], (int)BuiltInCategory.OST_StairsRailingBaluster);
        railing.Geometry!.Faces.Add(post.Geometry!.Faces[0]);
        var document = separateBaluster ? Document(railing, post) : Document(railing);

        var content = Assert.IsType<ElevationPrinter.ElevationContent>(ElevationPrinter.Collect(
            document, "section", planeView: view, isDetail: true, viewportScale: 20));

        foreach (double height in new[] { 1, top })
        {
            var spans = content.Edges.Where(edge => edge.CategoryId == category
                    && Math.Abs(edge.Y1 - height * FeetToMillimetres) < 1e-6 && Math.Abs(edge.Y2 - edge.Y1) < 1e-6)
                .Select(edge => (Lo: Math.Min(edge.X1, edge.X2), Hi: Math.Max(edge.X1, edge.X2)))
                .OrderBy(span => span.Lo).ToArray();
            if (inFront)
            {
                Assert.Equal(2, spans.Length);
                Assert.Equal(0, spans[0].Lo, 6);
                Assert.Equal(postLeft * FeetToMillimetres, spans[0].Hi, 6);
                Assert.Equal(postRight * FeetToMillimetres, spans[1].Lo, 6);
                Assert.Equal(6 * FeetToMillimetres, spans[1].Hi, 6);
            }
            else
            {
                var span = Assert.Single(spans);
                Assert.Equal(0, span.Lo, 6);
                Assert.Equal(6 * FeetToMillimetres, span.Hi, 6);
            }
        }
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_StairsRailing, -2, false, true)]
    [InlineData(BuiltInCategory.OST_StairsRailing, -2, true, true)]
    [InlineData(BuiltInCategory.OST_StairsRailing, -4, true, false)]
    [InlineData(BuiltInCategory.OST_StairsRailing, -6, false, false)]
    [InlineData(BuiltInCategory.OST_CurtainWallMullions, -2, true, true)]
    [InlineData(BuiltInCategory.OST_CurtainWallMullions, -4, false, false)]
    public void Collect_KeepsVisibleVerticalProfileSpansInOpaqueDetail(BuiltInCategory profile, double coverX,
        bool repeatNearFace, bool split)
    {
        var view = SectionView(900, [0, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 10);
        int category = (int)profile;
        int duplicateCategory = profile == BuiltInCategory.OST_StairsRailing
            ? (int)BuiltInCategory.OST_StairsRailingBaluster : category;
        Element Profile(int id, double x, int category) => QuadWall(id,
            [[x, 0, 0], [x, 1, 0], [x, 1, 6], [x, 0, 6]], [1, 0, 0], category);
        var far = Profile(901, -5, category);
        var near = Profile(902, -3, category);
        var cover = QuadWall(903, [[coverX, -1, 2], [coverX, 2, 2], [coverX, 2, 4], [coverX, -1, 4]],
            [1, 0, 0], (int)BuiltInCategory.OST_Floors);
        cover.Geometry!.Faces[0].PlaneSurface = true;
        var duplicate = Profile(904, -3, duplicateCategory);
        var document = repeatNearFace ? Document(view, far, near, cover, duplicate) : Document(view, far, near, cover);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };

        var content = Assert.IsType<ElevationPrinter.ElevationContent>(ElevationPrinter.Collect(
            document, "section", planeView: view, isDetail: true, viewportScale: 10,
            cropWindow: (-2 * FeetToMillimetres, -FeetToMillimetres, 3 * FeetToMillimetres, 7 * FeetToMillimetres)));

        foreach (double x in new[] { 0, FeetToMillimetres })
        {
            var spans = content.Edges.Where(edge => (edge.CategoryId == category || edge.CategoryId == duplicateCategory)
                && Math.Abs(edge.X1 - x) < 1e-6 && Math.Abs(edge.X2 - x) < 1e-6)
                .Select(edge => (Lo: Math.Min(edge.Y1, edge.Y2), Hi: Math.Max(edge.Y1, edge.Y2), edge.MidDepth))
                .OrderBy(span => span.Lo).ToArray();
            var expected = split ? new[] { (0, 2), (4, 6) } : new[] { (0, 6) };
            Assert.Equal(expected.Length, spans.Length);
            for (int i = 0; i < spans.Length; i++)
            {
                Assert.Equal(expected[i].Item1 * FeetToMillimetres, spans[i].Lo, 6);
                Assert.Equal(expected[i].Item2 * FeetToMillimetres, spans[i].Hi, 6);
                Assert.Equal(3 * FeetToMillimetres, spans[i].MidDepth, 6);
            }
        }
    }

    [Fact]
    public void Collect_ClipsADoorJambOnlyWhereTheNearRailingCoversIt()
    {
        var view = new Element
        {
            Id = 900, ViewOrigin = [0, 0, 0], ViewDirection = [0, 1, 0],
            ViewHorizontalDirection = [1, 0, 0], ViewVerticalDirection = [0, 0, 1],
        };
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 50);
        int railCategory = (int)BuiltInCategory.OST_StairsRailing;
        var jamb = QuadWall(100, [[0, 3, 0], [2, 3, 0], [2, 3, 4], [0, 3, 4]], [0, -1, 0], (int)BuiltInCategory.OST_Doors);
        var nearRail = QuadWall(101, [[-1, 1, 1.75], [3, 1, 1.75], [3, 1, 2.25], [-1, 1, 2.25]],
            [0, -1, 0], railCategory);
        var farRail = QuadWall(102, [[-1, 4, 0.5], [3, 4, 0.5], [3, 4, 1], [-1, 4, 1]],
            [0, -1, 0], railCategory);

        var content = ElevationPrinter.Collect(Document(view, jamb, nearRail, farRail), "section", planeView: view)!;
        var leftJamb = content.Edges.Where(edge => edge.CategoryId == jamb.CategoryId && edge.X1 == 0 && edge.X2 == 0)
            .Select(edge => (Start: Math.Min(edge.Y1, edge.Y2), End: Math.Max(edge.Y1, edge.Y2)))
            .OrderBy(span => span.Start).ToArray();

        Assert.Equal(2, leftJamb.Length);
        Assert.Equal(0, leftJamb[0].Start, 6);
        Assert.Equal(1.75 * FeetToMillimetres, leftJamb[0].End, 6);
        Assert.Equal(2.25 * FeetToMillimetres, leftJamb[1].Start, 6);
        Assert.Equal(4 * FeetToMillimetres, leftJamb[1].End, 6);
    }

    [SkippableFact]
    public void SampleFile_UsesThePlacedOpeningLinesInTheLongitudinalSection()
    {
        string path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.Find("rac_basic_sample_project.rvt");
        var document = RvtDocument.Open(path);
        var view = Assert.Single(document.Elements, element =>
            element.CategoryId == (int)BuiltInCategory.OST_Views && element.Name == "Longitudinal Section");
        var content = Assert.IsType<ElevationPrinter.ElevationContent>(
            ElevationPrinter.Collect(document, "section", planeView: view));

        var categories = content.Edges.Where(edge => edge.ViewLineStyleId > 0
            && Math.Abs(edge.X2 - edge.X1) > 10 && Math.Abs(edge.Y2 - edge.Y1) > 10
            && document.RawData.GraphicsStylePens?.TryGetValue(edge.ViewLineStyleId, out var pen) == true
            && LinePatterns.TryGetPatternDashes(document, pen.LinePatternId, out _))
            .Select(edge => edge.CategoryId).ToHashSet();
        Assert.Contains((int)BuiltInCategory.OST_Windows, categories);
        Assert.Contains((int)BuiltInCategory.OST_Doors, categories);
    }

    [SkippableFact]
    public void SampleFile_UsesThePlacedWindowOpeningLinesOnTheWestElevation()
    {
        string path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.Find("rac_basic_sample_project.rvt");
        var document = RvtDocument.Open(path);
        var view = Assert.Single(document.Elements, element =>
            element.CategoryId == (int)BuiltInCategory.OST_Views && element.Name == "West");
        var content = Assert.IsType<ElevationPrinter.ElevationContent>(
            ElevationPrinter.Collect(document, "west", planeView: view));

        Assert.Contains(content.Edges, edge => edge.CategoryId == (int)BuiltInCategory.OST_Windows
            && edge.ViewLineStyleId > 0 && Math.Abs(edge.X2 - edge.X1) > 10 && Math.Abs(edge.Y2 - edge.Y1) > 10
            && document.RawData.GraphicsStylePens?.TryGetValue(edge.ViewLineStyleId, out var pen) == true
            && LinePatterns.TryGetPatternDashes(document, pen.LinePatternId, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Collect_LeavesTheNativeWindowFrameOpeningClear(bool opening)
    {
        var view = SectionView(900, [10, 0, 0]);
        var frame = QuadWall(600, [[9, 0, 0], [9, 4, 0], [9, 4, 4], [9, 0, 4]], [1, 0, 0],
            (int)BuiltInCategory.OST_Windows);
        var face = Assert.Single(frame.Geometry!.Faces);
        face.PlaneSurface = true;
        if (opening)
            face.InnerVertices = [[[9, 1, 1], [9, 3, 1], [9, 3, 3], [9, 1, 3]]];
        var behind = QuadWall(601, [[8, 1.25, 1.25], [8, 2.75, 1.25], [8, 2.75, 2.75], [8, 1.25, 2.75]], [1, 0, 0]);
        var content = Assert.IsType<ElevationPrinter.ElevationContent>(
            ElevationPrinter.Collect(Document(view, behind, frame), "east", planeView: view));

        Assert.Equal(opening, content.Edges.Any(edge => edge.CategoryId == behind.CategoryId));
        var page = new PdfDocument().AddPage(PdfPageSize.A1.Landscape);
        ElevationPrinter.DrawContent(page, content, 100, 10, 10);
        Assert.Equal(opening, content.FaceOpenings != null);
        Assert.Equal(opening ? 3 : 2, CountOccurrences(page.Content, "h "));
    }

    [Theory]
    [InlineData((int)BuiltInCategory.OST_StairsRailing, true, 9.7, 6, 4)]
    [InlineData((int)BuiltInCategory.OST_StairsRailing, false, 8.5, 4, 8)]
    [InlineData((int)BuiltInCategory.OST_StairsRailing, true, 8.5, 4, 8)]
    [InlineData((int)BuiltInCategory.OST_Walls, true, 8.5, 4, 8)]
    [InlineData((int)BuiltInCategory.OST_Windows, true, 8.5, 4, 8)]
    [InlineData((int)BuiltInCategory.OST_Doors, true, 8.5, 4, 8)]
    public void Collect_KeepsCoincidentEdgesUntilTheirDepthIsTested(int category, bool farFirst, double plantDepth,
        int edgeCount, double horizontalLength)
    {
        var view = SectionView(900, [10, 0, 0]);
        var near = Component(601, 9);
        var far = Component(602, 8);
        var plant = QuadWall(603, [[plantDepth, 1, 0.5], [plantDepth, 3, 0.5], [plantDepth, 3, 2], [plantDepth, 1, 2]],
            [1, 0, 0], (int)BuiltInCategory.OST_Planting);
        var doc = farFirst ? Document(view, far, near, plant) : Document(view, near, far, plant);

        var content = ElevationPrinter.Collect(doc,
            category == (int)BuiltInCategory.OST_StairsRailing ? "section" : "east", planeView: view)!;
        var edges = content.Edges.Where(edge => edge.CategoryId == category).ToList();
        Assert.Equal(edgeCount, edges.Count);
        Assert.All(edges, edge => Assert.Equal(-9.2 * FeetToMillimetres, edge.MidDepth, 6));
        Assert.Equal(horizontalLength * FeetToMillimetres,
            edges.Where(edge => edge.Y1 == edge.Y2).Sum(edge => Math.Abs(edge.X2 - edge.X1)), 6);

        Element Component(int id, double x)
        {
            var element = QuadWall(id, [[x, 0, 1], [x, 4, 1], [x, 4, 1.5], [x, 0, 1.5]], [-1, 0, 0], category);
            element.Geometry!.Faces.Add(new GeoFace
            {
                OuterVertices = [[x + 0.2, 0, 1], [x + 0.2, 0, 1.5], [x + 0.2, 4, 1.5], [x + 0.2, 4, 1]],
                Normal = [1, 0, 0],
            });
            return element;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Collect_UsesTheSavedPaintRegionForTheWallPattern(bool patterns)
    {
        var view = SectionView(900, [0, 0, 0]);
        var wall = QuadWall(902, [[-4, 0, 0], [-4, 4, 0], [-4, 4, 3], [-4, 0, 3]], [1, 0, 0]);
        var face = wall.Geometry!.Faces[0];
        face.PlaneSurface = true;
        face.RenderStyleId = 199;
        face.SurfaceRegions =
        [
            new(198, [[-4, 2, 0], [-4, 4, 0], [-4, 4, 3], [-4, 2, 3]],
                [[[-4, 2.5, 1], [-4, 3, 1], [-4, 3, 2], [-4, 2.5, 2]]]),
        ];
        var document = Document(view, wall);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        if (patterns) document.RawData.OpaqueViewsWithSurfacePatterns = new HashSet<int> { view.Id };
        var grid = new TfBlockDeserializer.PatternGrid(Math.PI / 2, 0, 0, 0, 0.01, [], Target: 0, Orientation: 0);
        document.RawData.MaterialPatterns = new Dictionary<int, (int, int, int?)>
            { [198] = (-1, 201, 0), [199] = (-1, -1, null) };
        document.RawData.FillPatterns = new Dictionary<int, (string, IReadOnlyList<TfBlockDeserializer.PatternGrid>)>
            { [201] = ("Vertical", [grid]) };

        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;

        var projected = Assert.Single(content.Faces);
        if (patterns)
        {
            var hatches = Assert.IsType<ElevationPrinter.SurfaceHatches>(content.Hatch);
            var (owner, regions) = Assert.Single(hatches.Regions!);
            Assert.Same(projected.Points, owner);
            Assert.Empty(hatches.Faces);
            var region = Assert.Single(regions);
            Assert.Same(grid, region.Grid);
            Assert.Single(region.Holes);
            Assert.Equal(2 * FeetToMillimetres, region.Outer.Min(point => point.X), 6);
            Assert.Equal(4 * FeetToMillimetres, region.Outer.Max(point => point.X), 6);
        }
        else
        {
            Assert.Null(content.Hatch);
        }
    }

    [SkippableFact]
    public void SampleFile_StairDetailWindowFramesTheStair()
    {
        // The Main Stair Detail window (2.6 x 1.9 m around the stair) clips the model to the stair
        // enclosure instead of drawing the whole model.
        string? path = TestFiles.TryFind("Test_2022.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var view = document.Elements.FirstOrDefault(e =>
            e.CategoryId == (int)BuiltInCategory.OST_Views && e.Name == "Main Stair Detail");
        Assert.NotNull(view);

        var content = ElevationPrinter.Collect(document, "section", false, view, (2600, 1900));

        Assert.NotNull(content);
        Assert.True(content!.MaxX - content.MinX < 3000,
            $"detail width is {content.MaxX - content.MinX:0} mm");
        Assert.True(content.MaxY - content.MinY < 2500,
            $"detail height is {content.MaxY - content.MinY:0} mm");
        Assert.Contains(view.Id, document.RawData.OpaqueViewsWithSurfacePatterns!);
        var parent = Assert.Single(document.Elements, element =>
            element.Name == "Stair Section" && element.ViewCrop != null);
        Assert.Equal(parent.Id, view.SectionCallout?.ParentId);
        var a105 = Assert.Single(SheetComposer.Sheets(document), sheet => sheet.Number == "A105");
        var pdfPage = new PdfDocument().AddPage(PdfPageSize.A1.Landscape);
        DetailAnnotations.DrawCallouts(pdfPage, document, document.GetElement(a105.SheetId)!,
            parent, parent.ViewCrop!, 50, 150, 350);
        Assert.Contains("q 1 J 1 j", pdfPage.Content);
        Assert.Contains("(4) Tj", pdfPage.Content);
        Assert.Contains("(A105) Tj", pdfPage.Content);
        var full = Assert.IsType<ElevationPrinter.ElevationContent>(
            ElevationPrinter.Collect(document, "section", planeView: view));
        var hatch = Assert.IsType<ElevationPrinter.SurfaceHatches>(full.Hatch);
        Assert.True(PenWidths.TryGetWidth(document, (int)BuiltInCategory.OST_WallsSurfacePattern,
            false, 20, out double width));
        Assert.Contains(hatch.Faces.Values, pattern =>
            pattern.Grid is { Segments.Length: 0, Target: 0, Orientation: 0 } grid
            && Math.Abs(grid.Offset * FeetToMillimetres - 3) < 1e-6
            && Math.Abs(grid.Angle - Math.PI / 2) < 1e-6 && pattern.Width == width);
        var (x0, y0, x1, y1) = SheetComposer.CropContentRect(view.ViewCrop!);
        Assert.Contains(Assert.IsAssignableFrom<IReadOnlyList<DetailAnnotations.LayerHatch>>(full.LayerHatches),
            layer => layer.Grids is [{ Segments.Length: 0, Target: 0 }]
                && ElevationPrinter.ClipToWindow(layer.Polygon, x0, x1, y0, y1).Count >= 3);
        var printDetail = Assert.IsType<ElevationPrinter.ElevationContent>(ElevationPrinter.Collect(document, "section",
            planeView: view, isDetail: true, cropWindow: SheetComposer.CropContentRect(view.ViewCrop!), viewportScale: 20));
        var slopingTubeEdges = printDetail.Edges.Where(edge => edge.CategoryId == (int)BuiltInCategory.OST_StairsRailing
            && Math.Abs(edge.X2 - edge.X1) > 50
            && Math.Abs((edge.Y2 - edge.Y1) / (edge.X2 - edge.X1) + 0.5468) < 0.01)
            .OrderBy(edge => Math.Abs(edge.X2 - edge.X1)).ToArray();
        // The two longer edges belong to the handrail. Each tube side has three spans between upright posts.
        Assert.Equal(50, slopingTubeEdges.Length);
        var first = slopingTubeEdges[0];
        double slope = (first.Y2 - first.Y1) / (first.X2 - first.X1);
        var sides = slopingTubeEdges[..^2].GroupBy(edge => Math.Round(edge.Y1 - slope * edge.X1, 1)).ToArray();
        Assert.Equal(16, sides.Length);
        Assert.All(sides, side =>
        {
            var spans = side.Select(edge => (Start: Math.Min(edge.X1, edge.X2), End: Math.Max(edge.X1, edge.X2)))
                .OrderBy(span => span.Start).ToArray();
            Assert.Equal(3, spans.Length);
            Assert.Equal(25, spans[1].Start - spans[0].End, 5);
            Assert.Equal(25, spans[2].Start - spans[1].End, 5);
        });
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void Collect_RestoresTheVisibleStairEdgeFromItsNearerFace(bool surfacePatterns, bool blocked, bool expected)
    {
        const int stairs = (int)BuiltInCategory.OST_Stairs;
        var view = SectionView(1, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 100);
        var stair = QuadWall(2, [[2, 0, 0], [2, 3, 0], [2, 3, 2], [2, 0, 2]], [1, 0, 0], stairs);
        stair.Geometry!.Faces.Add(QuadWall(3, [[2, 0, 0], [2, 3, 0], [2, 3, 2], [2, 0, 2]],
            [1, 0, 0], stairs).Geometry!.Faces[0]);
        stair.Geometry.Faces.Add(QuadWall(4, [[5, 0, 0], [5, 3, 0], [5, 3, 2], [5, 0, 2]],
            [1, 0, 0], stairs).Geometry!.Faces[0]);
        var wall = QuadWall(5, [[6, -1, -1], [6, 4, -1], [6, 4, 3], [6, -1, 3]], [1, 0, 0]);
        var document = blocked ? Document(view, stair, wall) : Document(view, stair);
        if (surfacePatterns) document.RawData.OpaqueViewsWithSurfacePatterns = new HashSet<int> { view.Id };

        var content = ElevationPrinter.Collect(document, "east", planeView: view)!;

        var outlines = content.Edges.Where(edge => edge.CategoryId == stairs).ToList();
        Assert.Equal(expected ? 4 : 0, outlines.Count);
        Assert.All(outlines, edge => Assert.Equal(-5 * FeetToMillimetres, edge.MidDepth, 6));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Collect_DistinguishesFineRailFacesWithTheSameProjection(bool crease, bool repeated)
    {
        var view = SectionView(900, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 20);
        const int category = (int)BuiltInCategory.OST_StairsRailing;
        var rail = QuadWall(901, [[9, 0, 0], [9, 4, 0], [9, 4, 4]], [1, 0, 0], category);
        rail.Geometry!.Faces.Add(QuadWall(0, [[9, 0, 0], [9, 4, 4], [crease ? 8.5 : 9, 0, 4]],
            crease ? [8, -1, 1] : [1, 0, 0]).Geometry!.Faces[0]);
        rail.Geometry.Faces.Add(QuadWall(0, [[8, 4, 4], [8, 4, 0], [8, 0, 0]], [-1, 0, 0]).Geometry!.Faces[0]);
        rail.Geometry.Faces.Add(QuadWall(0, [[8, 0, 4], [8, 4, 4], [8, 0, 0]], [-1, 0, 0]).Geometry!.Faces[0]);
        foreach (var face in rail.Geometry.Faces) face.PlaneSurface = true;
        if (repeated) rail.Geometry.Faces.AddRange(rail.Geometry.Faces.ToArray());

        var content = ElevationPrinter.Collect(Document(view, rail), "section", planeView: view)!;

        Assert.Equal(crease ? 1 : 0, content.Edges.Count(edge => edge.X1 != edge.X2 && edge.Y1 != edge.Y2));
        if (!crease) Assert.Equal(4, content.Edges.Count);
    }

    [Theory]
    [InlineData(8.5, true, false)]
    [InlineData(8.5, true, true)]
    [InlineData(9.5, false, false)]
    public void Collect_UsesTheNearTopRailEdgeForOcclusion(double coverDepth, bool visible, bool clipped)
    {
        var view = SectionView(900, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 20);
        const double height = 50 / FeetToMillimetres;
        var railing = QuadWall(600, [[8, 0, 0], [8, 6, 0], [8, 6, height], [8, 0, height]],
            [-1, 0, 0], (int)BuiltInCategory.OST_StairsRailing);
        var topRail = QuadWall(602, [[9, 0, 0], [9, 0, height], [9, 6, height], [9, 6, 0]],
            [1, 0, 0], (int)BuiltInCategory.OST_RailingTopRail);
        topRail.NativeRootClassName = "TopRail";
        var railFaces = topRail.Geometry!.Faces;
        railFaces.Add(railing.Geometry!.Faces[0]);
        railFaces.AddRange([
            Face([[8, 0, 0], [9, 0, 0], [9, 6, 0], [8, 6, 0]], [0, 0, -1]),
            Face([[8, 0, height], [8, 6, height], [9, 6, height], [9, 0, height]], [0, 0, 1]),
            Face([[8, 0, 0], [8, 0, height], [9, 0, height], [9, 0, 0]], [0, -1, 0]),
            Face([[8, 6, 0], [9, 6, 0], [9, 6, height], [8, 6, height]], [0, 1, 0]),
        ]);
        foreach (var face in railFaces) face.PlaneSurface = true;
        var cover = QuadWall(603, [[coverDepth, -1, -1], [coverDepth, 7, -1],
            [coverDepth, 7, 1], [coverDepth, -1, 1]], [1, 0, 0]);

        double cropStart = 2 * FeetToMillimetres;
        var content = ElevationPrinter.Collect(Document(railing, topRail, cover), "section", planeView: view,
            cropWindow: clipped ? (cropStart, -100, 7 * FeetToMillimetres, 100) : null)!;

        var edges = content.Edges.Where(edge => edge.CategoryId == topRail.CategoryId && edge.Y1 == edge.Y2).ToArray();
        Assert.Equal(visible ? 2 : 0, edges.Length);
        Assert.All(edges, edge => Assert.Equal(-9 * FeetToMillimetres, edge.MidDepth, 6));
        if (clipped)
        {
            Assert.All(edges, edge => Assert.Equal(cropStart, Math.Min(edge.X1, edge.X2), 6));
            Assert.DoesNotContain(content.Edges,
                edge => edge.CategoryId == topRail.CategoryId && edge.X1 == cropStart && edge.X2 == cropStart);
        }

        static GeoFace Face(double[][] points, double[] normal) => new() { OuterVertices = [.. points], Normal = normal };
    }

    [Fact]
    public void Collect_MarksCutEdgesOnThePlane()
    {
        // A sloped face straddling the section plane (x = 10 ft, looking -X): the clipped boundary on the
        // plane inks heavy, the natural edges thin.
        var view = SectionView(900, [10, 0, 5]);
        var document = Document(
            QuadWall(600, [[8, 0, 0], [12, 0, 3], [12, 4, 3], [8, 4, 0]], [-0.6, 0, 0.8]),
            view);

        var content = ElevationPrinter.Collect(document, "section", false, view, null)!;

        Assert.NotNull(content);
        var cut = Assert.Single(content!.Edges, e => e.IsCut);
        Assert.Equal(1.5 * FeetToMillimetres, cut.Y1, 0);
        Assert.Equal(1.5 * FeetToMillimetres, cut.Y2, 0);
        Assert.Contains(content.Edges, e => !e.IsCut);
    }

    [Theory]
    [InlineData("section", 100, false, 2)]
    [InlineData("section", 100, true, 2)]
    [InlineData("section", 50, false, 0)]
    [InlineData("east", 100, false, 1)]
    [InlineData("east", 100, true, 2)]
    public void Collect_DrawsOnlyTheUncoveredTerrainCut(string direction, int scale, bool withPad, int edgeCount)
    {
        var view = SectionView(900, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, scale);
        var wall = QuadWall(600, [[9, -1, 0], [9, 1, 0], [9, 1, 3], [9, -1, 3]], [1, 0, 0]);
        var terrain = QuadWall(601, [[9, -4, 0], [11, -4, 0], [11, 4, 2], [9, 4, 2]],
            [0, -0.25, 1], (int)BuiltInCategory.OST_Topography);

        var pad = QuadWall(602, [[9, -2, -1], [11, -2, -1], [11, 2, -1], [9, 2, -1]],
            [0, 0, 1], (int)BuiltInCategory.OST_BuildingPad);
        var document = withPad ? Document(view, wall, terrain, pad) : Document(view, wall, terrain);
        var content = ElevationPrinter.Collect(document, direction, planeView: view)!;
        var grade = content.Edges.Where(e => e.CategoryId == terrain.CategoryId)
            .OrderBy(e => Math.Min(e.X1, e.X2)).ToArray();

        Assert.Equal(edgeCount, grade.Length);
        if (edgeCount == 0) return;
        Assert.All(grade, edge => Assert.True(edge.IsCut));
        Assert.Equal(-4 * FeetToMillimetres, Math.Min(grade[0].X1, grade[0].X2), 6);
        Assert.Equal(4 * FeetToMillimetres, Math.Max(grade[^1].X1, grade[^1].X2), 6);
        if (edgeCount == 2)
        {
            double cutEnd = (withPad ? 2 : 1) * FeetToMillimetres;
            Assert.Equal(-cutEnd, Math.Max(grade[0].X1, grade[0].X2), 6);
            Assert.Equal(cutEnd, Math.Min(grade[1].X1, grade[1].X2), 6);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Collect_UsesTheClosedWallSectionToMaskTheGrade(bool showPatterns, bool detail)
    {
        var view = SectionView(900, [10, 0, 0]);
        if (detail) Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 20);
        var terrain = QuadWall(601, [[9, -4, 1], [11, -4, 1], [11, 4, 1], [9, 4, 1]], [0, 0, 1],
            (int)BuiltInCategory.OST_Topography);
        var behind = QuadWall(602, [[7, -3, 0], [7, 3, 0], [7, 3, 3], [7, -3, 3]], [1, 0, 0]);
        behind.Geometry!.Faces[0].PlaneSurface = true;
        behind.Geometry.Faces[0].RenderStyleId = 200;
        var cutWall = QuadWall(603, [[9, -0.5, 0], [11, -0.5, 0], [11, -0.5, 2], [9, -0.5, 2]], [0, -1, 0]);
        cutWall.TypeId = 604;
        cutWall.BoundingBox = [9, -0.5, 0, 11, 0.5, 2];
        cutWall.Geometry!.Faces.AddRange([
            new() { OuterVertices = [[9, 0.5, 0], [11, 0.5, 0], [11, 0.5, 2], [9, 0.5, 2]], Normal = [0, 1, 0] },
            new() { OuterVertices = [[9, -0.5, 0], [11, -0.5, 0], [11, 0.5, 0], [9, 0.5, 0]], Normal = [0, 0, -1] },
            new() { OuterVertices = [[9, -0.5, 2], [11, -0.5, 2], [11, 0.5, 2], [9, 0.5, 2]], Normal = [0, 0, 1] },
        ]);
        var cutType = new Element
        {
            Id = cutWall.TypeId,
            SerializedHostTypeLayers = [new(BitConverter.DoubleToInt64Bits(1), 1, -1, 200, -1, 0, 1)],
        };
        var document = Document(view, terrain, behind, cutWall, cutType);
        var grid = new TfBlockDeserializer.PatternGrid(0, 0, 0, 0, 0.005, [0.004, 0.001]);
        document.RawData.SiteCutGraphics = (-3, 100);
        var materialGrid = new TfBlockDeserializer.PatternGrid(0, 0, 0, 0, 0.007, [0.002, 0.003], Target: 0, Orientation: 0);
        var cutGrid = materialGrid with { Angle = Math.PI / 4 };
        document.RawData.MaterialPatterns = new Dictionary<int, (int, int, int?)>
        {
            [100] = (101, -1, null), [200] = (103, 102, 0),
        };
        document.RawData.FillPatterns =
            new Dictionary<int, (string, IReadOnlyList<TfBlockDeserializer.PatternGrid>)>
            {
                [101] = ("Earth", [grid]), [102] = ("Sand", [materialGrid]), [103] = ("Concrete", [cutGrid]),
            };
        document.RawData.OpaqueViewsWithSurfacePatterns = showPatterns ? new HashSet<int> { view.Id } : new HashSet<int>();
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        var crop = (-1500.0, -1000.0, 1500.0, 1500.0);
        var content = ElevationPrinter.Collect(document, "section", planeView: view,
            isDetail: detail, cropWindow: detail ? crop : null)!;
        if (showPatterns && !detail) Assert.Equal(materialGrid, Assert.Single(Assert.Single(content.Hatch!.DashedFaces).Value));
        else Assert.Null(content.Hatch);
        var mask = Assert.Single(content.GroundHatch!.SectionMasks!);
        Assert.Equal(4, mask.Count);
        Assert.Equal(2 * FeetToMillimetres * FeetToMillimetres, Math.Abs(PlanPrinter.SignedArea(mask)), 6);
        if (detail)
        {
            Assert.Same(mask, Assert.Single(content.DetailCutFaces!));
            var shaded = new PdfDocument().AddPage(PdfPageSize.A1.Landscape);
            ElevationPrinter.DrawContent(shaded, content, 20, 10, 10, 0.82, clipRect: crop);
            var projected = new PdfDocument().AddPage(PdfPageSize.A1.Landscape);
            ElevationPrinter.DrawContent(projected, content with { DetailCutFaces = null }, 20, 10, 10, 0.82,
                clipRect: crop);
            Assert.Contains("0.82 0.82 0.82 rg", shaded.Content);
            Assert.Equal(CountOccurrences(projected.Content, "\n1 g\n") + 1,
                CountOccurrences(shaded.Content, "\n1 g\n"));
        }
        else
        {
            Assert.Null(content.DetailCutFaces);
        }
        var grade = content.Edges.Where(edge => edge.CategoryId == terrain.CategoryId)
            .OrderBy(edge => Math.Min(edge.X1, edge.X2)).ToArray();
        Assert.Equal(2, grade.Length);
        Assert.Equal(-0.5 * FeetToMillimetres, Math.Max(grade[0].X1, grade[0].X2), 6);
        Assert.Equal(0.5 * FeetToMillimetres, Math.Min(grade[1].X1, grade[1].X2), 6);
        var projectedWall = Assert.Single(content.Edges, edge => edge.CategoryId == behind.CategoryId
            && edge.X1 == 3 * FeetToMillimetres && edge.X2 == edge.X1);
        Assert.Equal(FeetToMillimetres, Math.Min(projectedWall.Y1, projectedWall.Y2), 6);
        Assert.Contains(content.Edges, edge => edge.CategoryId == cutWall.CategoryId && edge.IsCut
            && Math.Min(edge.Y1, edge.Y2) == 0);

        if (detail) return;

        var elevation = ElevationPrinter.Collect(document, "east", planeView: view)!;
        var wallRing = Assert.Single(elevation.GroundHatch!.SectionMasks!);
        Assert.Equal(Math.Abs(PlanPrinter.SignedArea(mask)), Math.Abs(PlanPrinter.SignedArea(wallRing)), 6);
        Assert.Equal(4, elevation.Edges.Count(edge => edge.CategoryId == cutWall.CategoryId && edge.IsCut));
        if (showPatterns)
        {
            var hatch = Assert.Single(elevation.LayerHatches!);
            Assert.Same(wallRing, hatch.Polygon);
            Assert.Same(cutGrid, Assert.Single(hatch.Grids));
            Assert.Same(materialGrid, Assert.Single(Assert.Single(elevation.Hatch!.DashedFaces).Value));
        }
        else
        {
            Assert.Null(elevation.LayerHatches);
            Assert.Null(elevation.Hatch);
        }
        var pdf = new PdfDocument();
        ElevationPrinter.DrawContent(pdf.AddPage(PdfPageSize.A1.Landscape), elevation, 100, 10, 10);
        string drawing = PdfTestContent.Read(pdf);
        Assert.Contains("W* n", drawing);
        Assert.Contains("/Pattern cs", drawing);
        Assert.Equal(showPatterns ? 3 : 1, pdf.LinePatterns.Count);
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(101, false)]
    [InlineData(null, false)]
    public void Collect_TrimsOnlyWallCutSpansInsideAnotherWallOfTheSameMaterial(int? material, bool trimmed)
    {
        var view = SectionView(900, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 20);
        var wall = QuadWall(601, [[9, 0, 0], [11, 0, 0], [11, 0, 2], [9, 0, 2]], [0, -1, 0]);
        wall.BoundingBox = [9, 0, 0, 11, 2, 2];
        wall.Geometry!.Faces.AddRange([
            new() { OuterVertices = [[9, 2, 0], [11, 2, 0], [11, 2, 2], [9, 2, 2]], Normal = [0, 1, 0] },
            new() { OuterVertices = [[9, 0, 0], [11, 0, 0], [11, 2, 0], [9, 2, 0]], Normal = [0, 0, -1] },
            new() { OuterVertices = [[9, 0, 2], [11, 0, 2], [11, 2, 2], [9, 2, 2]], Normal = [0, 0, 1] },
        ]);
        foreach (var face in wall.Geometry.Faces) face.RenderStyleId = 100;

        var crossing = QuadWall(602, [[9, -1, 1], [11, -1, 1], [11, 3, 1], [9, 3, 1]], [0, 0, -1]);
        crossing.Geometry!.Faces[0].RenderStyleId = material;
        var document = Document(view, wall, crossing);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        var content = ElevationPrinter.Collect(document, "section", planeView: view,
            isDetail: true, cropWindow: (-500, -500, 1000, 1000))!;

        var spans = content.Edges.Where(edge => edge.IsCut && edge.Y1 == FeetToMillimetres && edge.Y2 == edge.Y1)
            .Select(edge => (Start: Math.Round(Math.Min(edge.X1, edge.X2) / FeetToMillimetres, 9),
                End: Math.Round(Math.Max(edge.X1, edge.X2) / FeetToMillimetres, 9)))
            .OrderBy(span => span.Start).ToArray();
        if (trimmed)
            Assert.Equal([(-1d, 0d), (2d, 3d)], spans);
        else
            Assert.Equal([(-1d, 3d)], spans);
        Assert.Contains(content.Edges, edge => edge.IsCut && edge.Y1 == 0 && edge.Y2 == 0);
    }

    [Fact]
    public void Collect_ElevationGradeClipsPadsAndCrossingFacesAndIgnoresOffscreenWalls()
    {
        var view = SectionView(900, [10, 0, 0]);
        var terrain = QuadWall(601, [[9, -4, 0], [11, -4, 0], [11, 4, 2], [9, 4, 2]],
            [0, -0.25, 1], (int)BuiltInCategory.OST_Topography);
        var pad = QuadWall(602, [[9, -1, -1], [11, -1, -1], [11, 1, -1], [9, 1, -1]],
            [0, 0, 1], (int)BuiltInCategory.OST_BuildingPad);
        var rear = QuadWall(603, [[9, -3, 0], [9, 3, 0], [9, 3, 4], [9, -3, 4]], [1, 0, 0]);
        var crossing = QuadWall(604, [[9, 1.5, 0], [11, 3.5, 0], [11, 3.5, 4], [9, 1.5, 4]],
            [0.7071067811865476, -0.7071067811865476, 0]);
        var offscreen = QuadWall(605, [[11, -3.5, 0], [11, -2.5, 0], [11, -2.5, 4], [11, -3.5, 4]], [1, 0, 0]);

        var content = ElevationPrinter.Collect(Document(view, terrain, pad, rear, crossing, offscreen), "east",
            planeView: view)!;
        var grade = content.Edges.Where(e => e.CategoryId == terrain.CategoryId)
            .OrderBy(e => Math.Min(e.X1, e.X2)).ToArray();

        Assert.Equal(3, grade.Length);
        AssertSpan(grade[0], -4, -1);
        AssertSpan(grade[1], 1, 2.5);
        AssertSpan(grade[2], 3.5, 4);

        static void AssertSpan(ElevationPrinter.CollectedEdge edge, double from, double to)
        {
            Assert.Equal(from * FeetToMillimetres, Math.Min(edge.X1, edge.X2), 6);
            Assert.Equal(to * FeetToMillimetres, Math.Max(edge.X1, edge.X2), 6);
        }
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_BuildingPad, false, true)]
    [InlineData(BuiltInCategory.OST_Floors, false, true)]
    [InlineData(BuiltInCategory.OST_Floors, true, true)]
    [InlineData(BuiltInCategory.OST_Floors, true, false)]
    public void Collect_UsesAClosedSlabCutToMaskTheProjectionBehindIt(BuiltInCategory category, bool callout, bool closed)
    {
        var view = SectionView(900, [0, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 50);
        view.ViewCrop = new ViewCropFrame(0, 0, 0, [0, 1, 0], [0, 0, 1], [-1, 0, 0], -4, -4, 4, 5, true);
        if (callout) view.SectionViewerId = 905;
        var pad = QuadWall(601, [[-1, -2, -2], [1, -2, -2], [1, 2, -2], [-1, 2, -2]], [0, 0, 1],
            (int)category);
        pad.BoundingBox = [-1, -2, -3, 1, 2, -2];
        pad.Geometry!.Faces.AddRange([
            Face([[-1, 2, -3], [1, 2, -3], [1, -2, -3], [-1, -2, -3]], [0, 0, -1]),
            Face([[-1, -2, -3], [1, -2, -3], [1, -2, -2], [-1, -2, -2]], [0, -1, 0]),
            Face([[-1, 2, -2], [1, 2, -2], [1, 2, -3], [-1, 2, -3]], [0, 1, 0]),
            Face([[-1, -2, -2], [-1, 2, -2], [-1, 2, -3], [-1, -2, -3]], [-1, 0, 0]),
            Face([[1, 2, -2], [1, -2, -2], [1, -2, -3], [1, 2, -3]], [1, 0, 0]),
        ]);
        pad.Geometry.BrepSolids = [new() { IsClosed = closed, FaceIndices = [0, 1, 2, 3, 4, 5] }];
        var wall = QuadWall(602, [[-2, -1, -4], [-2, 1, -4], [-2, 1, 1], [-2, -1, 1]], [1, 0, 0]);
        var document = Document(view, pad, wall);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        document.RawData.SectionViews = new Dictionary<int, (int, double, IReadOnlyList<double[]>?)>
        {
            [905] = (899, 0.02, null),
        };

        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;

        if (!closed)
        {
            Assert.Null(content.DetailCutFaces);
            return;
        }
        var mask = Assert.Single(content.DetailCutFaces!);
        Assert.Equal(4, mask.Count);
        Assert.Equal(-2 * FeetToMillimetres, mask.Max(point => point.Y), 6);
        Assert.Equal(-3 * FeetToMillimetres, mask.Min(point => point.Y), 6);
        Assert.DoesNotContain(content.Edges, edge => edge.CategoryId == (int)BuiltInCategory.OST_Walls
            && Math.Min(edge.Y1, edge.Y2) < -3 * FeetToMillimetres && Math.Max(edge.Y1, edge.Y2) > -2 * FeetToMillimetres);

        static GeoFace Face(double[][] vertices, double[] normal) =>
            new() { OuterVertices = [.. vertices], Normal = normal, PlaneSurface = true };
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_Walls, 9, true, true)]
    [InlineData(BuiltInCategory.OST_StairsRailing, 9, true, true)]
    [InlineData(BuiltInCategory.OST_StairsRailing, 7, true, false)]
    [InlineData(BuiltInCategory.OST_Walls, 9, false, true)]
    [InlineData(BuiltInCategory.OST_StairsRailing, 9, false, true)]
    [InlineData(BuiltInCategory.OST_StairsRailing, 7, false, false)]
    public void Collect_ClipsUprightsAtANearerProjectedSlabInAnOpaqueSection(
        BuiltInCategory category, double slabDepth, bool callout, bool clipped)
    {
        var view = SectionView(900, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 20);
        view.ViewCrop = new ViewCropFrame(10, 0, 0, [0, 1, 0], [0, 0, 1], [-1, 0, 0], -2, -1, 5, 5, true);
        if (callout) view.SectionViewerId = 905;
        var upright = QuadWall(901, [[8, 0, 0], [8, 1, 0], [8, 1, 3], [8, 0, 3]], [1, 0, 0], (int)category);
        var slab = QuadWall(902, [[slabDepth, -1, 1], [slabDepth, 2, 1], [slabDepth, 2, 2], [slabDepth, -1, 2]],
            [1, 0, 0], (int)BuiltInCategory.OST_Floors);
        slab.Geometry!.Faces[0].PlaneSurface = true;
        var document = Document(view, upright, slab);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        document.RawData.SectionViews = new Dictionary<int, (int, double, IReadOnlyList<double[]>?)>
        {
            [905] = (899, 0.02, null),
        };

        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;

        var spans = content.Edges.Where(edge => edge.CategoryId == (int)category && edge.X1 == 0 && edge.X2 == 0)
            .Select(edge => (Math.Round(Math.Min(edge.Y1, edge.Y2) / FeetToMillimetres, 9),
                Math.Round(Math.Max(edge.Y1, edge.Y2) / FeetToMillimetres, 9)))
            .OrderBy(span => span.Item1).ToArray();
        Assert.Equal(clipped ? [(0d, 1d), (2d, 3d)] : [(0d, 3d)], spans);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Collect_ProjectsSmallFurnitureSilhouetteFacetsWhenOpeningsAreEdgeOn(bool facingOpening)
    {
        var view = SectionView(900, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 20);
        view.ViewCrop = new ViewCropFrame(10, 0, 0, [0, 1, 0], [0, 0, 1], [-1, 0, 0], -2, -1, 5, 5, true);
        view.SectionViewerId = 905;
        var furniture = QuadWall(901, [[7, 0, 0], [7, 0.1, 0], [7, 0.1, 0.5], [7, 0, 0.5]], [1, 0, 0],
            (int)BuiltInCategory.OST_Furniture);
        furniture.NativeRootClassName = "FamilyInstance";
        furniture.NativeRootClassChain = ["FamilyInstance"];
        var geometry = furniture.Geometry!;
        geometry.Faces[0].PlaneSurface = true;
        geometry.Faces.AddRange([
            new() { OuterVertices = [[7, 0, 0.5], [7, 0.1, 0.5], [7, 0, 0.6]], Normal = [1, 0, 0], GeometryNode = 11, MeshMarker = 1 },
            new() { OuterVertices = [[6, 0, 0], [6, 0, 0.6], [6, 0.1, 0.5], [6, 0.1, 0]], Normal = [-1, 0, 0], PlaneSurface = true },
            new() { OuterVertices = [[6, 0, 0], [7, 0, 0], [7, 0, 0.6], [6, 0, 0.6]], Normal = [0, -1, 0], PlaneSurface = true },
            new() { OuterVertices = [[6, 0.1, 0], [6, 0.1, 0.5], [7, 0.1, 0.5], [7, 0.1, 0]], Normal = [0, 1, 0], PlaneSurface = true },
            new() { OuterVertices = [[6, 0, 0], [6, 0.1, 0], [7, 0.1, 0], [7, 0, 0]], Normal = [0, 0, -1], PlaneSurface = true },
            new() { OuterVertices = [[6, 0, 0.6], [7, 0, 0.6], [7, 0.1, 0.5], [6, 0.1, 0.5]], Normal = [0, 0.7071067811865476, 0.7071067811865476], PlaneSurface = true },
        ]);
        geometry.Faces[facingOpening ? 2 : 3].InnerLoops = [[0.01, 0.1, 0.08, 0.3]];
        geometry.BrepSolids = [new() { IsClosed = true, FaceIndices = Enumerable.Range(0, geometry.Faces.Count).ToList() }];
        geometry.MeshNodes = [new GeometryNode(11, 0,
            [new BrFace { Marker = 1, SurfaceKind = SurfaceKind.Cylinder, Loops = [] }], 1)];
        var document = Document(view, furniture);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        document.RawData.SectionViews = new Dictionary<int, (int, double, IReadOnlyList<double[]>?)>
        {
            [905] = (899, 0.02, null),
        };

        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;
        var contour = content.Edges.Where(edge => edge.SilhouetteOwnerId == furniture.Id).ToList();
        if (facingOpening)
            Assert.Empty(contour);
        else
            Assert.Contains(contour, edge => Math.Abs(edge.X1 - edge.X2) > 1
                && Math.Abs(Math.Max(edge.Y1, edge.Y2) - 0.6 * FeetToMillimetres) < 1e-6);
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_Walls, false)]
    [InlineData(BuiltInCategory.OST_StructuralFoundation, true)]
    public void Collect_ClipsAGroundTrimmedProjectionAgainstNearerOpaqueFaces(BuiltInCategory category, bool opening)
    {
        var view = SectionView(900, [10, 0, 0]);
        var terrain = QuadWall(601, [[9, -4, 0], [11, -4, 0], [11, 4, 0], [9, 4, 0]],
            [0, 0, 1], (int)BuiltInCategory.OST_Topography);
        var pad = QuadWall(602, [[9, -1, -1], [11, -1, -1], [11, 1, -1], [9, 1, -1]],
            [0, 0, 1], (int)BuiltInCategory.OST_BuildingPad);
        var rear = QuadWall(603, [[7, -3, -0.5], [7, 3, -0.5], [7, 3, 2], [7, -3, 2]],
            [1, 0, 0], (int)category);
        var front = QuadWall(604, [[9, 0.25, -0.75], [9, 1.25, -0.75], [9, 1.25, 0.25], [9, 0.25, 0.25]], [1, 0, 0]);
        var insidePad = QuadWall(605, [[8, 0.8, -0.4], [8, 0.9, -0.4], [8, 0.9, 1], [8, 0.8, 1]], [1, 0, 0]);
        var face = front.Geometry!.Faces[0];
        face.PlaneSurface = true;
        if (opening)
            face.InnerVertices = [[[9, 0.5, -0.65], [9, 0.75, -0.65], [9, 0.75, -0.35], [9, 0.5, -0.35]]];
        var document = Document(view, terrain, pad, rear, front, insidePad);
        document.RawData.OpaqueViewsWithSurfacePatterns = new HashSet<int> { view.Id };
        document.RawData.SiteCutGraphics = (-3, 100);
        document.RawData.MaterialPatterns = new Dictionary<int, (int, int, int?)> { [100] = (101, 102, null) };
        var grid = new TfBlockDeserializer.PatternGrid(0, 0, 0, 0, 0.005, [0.004, 0.001]);
        document.RawData.FillPatterns =
            new Dictionary<int, (string, IReadOnlyList<TfBlockDeserializer.PatternGrid>)> { [101] = ("Earth", [grid]) };

        var content = ElevationPrinter.Collect(document, "east", planeView: view)!;

        var bottom = content.Edges.Where(edge => edge.CategoryId == (int)category
            && Math.Abs(edge.Y1 + 0.5 * FeetToMillimetres) < 1e-6 && Math.Abs(edge.Y2 - edge.Y1) < 1e-6)
            .Select(edge => (Left: Math.Round(Math.Min(edge.X1, edge.X2) / FeetToMillimetres, 9),
                Right: Math.Round(Math.Max(edge.X1, edge.X2) / FeetToMillimetres, 9))).OrderBy(span => span.Left).ToArray();
        Assert.Equal(opening ? [(-1d, 0.25d), (0.5d, 0.75d)] : [(-1d, 0.25d)], bottom);
        var upright = Assert.Single(content.Edges, edge => Math.Abs(edge.X1 - 0.9 * FeetToMillimetres) < 1e-6
            && Math.Abs(edge.X2 - edge.X1) < 1e-6);
        Assert.Equal(0.25 * FeetToMillimetres, Math.Min(upright.Y1, upright.Y2), 6);
        Assert.Equal(FeetToMillimetres, Math.Max(upright.Y1, upright.Y2), 6);
    }

    [Theory]
    [InlineData("east", 1.0, 100, false, false)]
    [InlineData("east", 1.0, 100, true, false)]
    [InlineData("section", 0.82, 100, true, false)]
    [InlineData("section", 0.82, 50, true, false)]
    [InlineData("section", 0.82, 20, true, true)]
    public void DrawContent_UsesTheSiteCutPatternBelowTheGradeAndPad(
        string direction, double faceFill, int viewScale, bool withPad, bool detail)
    {
        var view = SectionView(900, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, viewScale);
        if (viewScale <= 50)
            view.ViewCrop = new ViewCropFrame(10, 0, 0, [0, 1, 0], [0, 0, 1], [-1, 0, 0], -5, -4, 5, 5, true);
        var terrain = QuadWall(601, [[9, -4, 0], [11, -4, 0], [11, 4, 2], [9, 4, 2]],
            [0, -0.25, 1], (int)BuiltInCategory.OST_Topography);
        var pad = QuadWall(602, [[9, -1, -1], [11, -1, -1], [11, 1, -1], [9, 1, -1]],
            [0, 0, 1], (int)BuiltInCategory.OST_BuildingPad);
        var wall = QuadWall(603, [[9, -4, 0], [9, 4, 0], [9, 4, 4], [9, -4, 4]], [1, 0, 0]);
        var foundation = QuadWall(604, [[9, -0.5, -3], [9, 0.5, -3], [9, 0.5, -1], [9, -0.5, -1]], [1, 0, 0]);
        var document = withPad ? Document(view, terrain, pad, wall, foundation) : Document(view, terrain, wall, foundation);
        ElevationPrinter.ElevationContent Collect() => ElevationPrinter.Collect(document, direction,
            planeView: view, isDetail: detail, cropWindow: detail ? SheetComposer.CropContentRect(view.ViewCrop!) : null)!;
        var original = Collect();
        var grid = new TfBlockDeserializer.PatternGrid(0, 0, 0, 0, 0.005, [0.004, 0.001]);
        document.RawData.SiteCutGraphics = (-3, 100);
        document.RawData.MaterialPatterns = new Dictionary<int, (int, int, int?)> { [100] = (101, 102, null) };
        document.RawData.FillPatterns =
            new Dictionary<int, (string, IReadOnlyList<TfBlockDeserializer.PatternGrid>)> { [101] = ("Earth", [grid]) };

        var hatched = Collect();
        Assert.NotNull(hatched.GroundHatch);
        Assert.Equal(-3 * FeetToMillimetres, hatched.GroundHatch.BaseY, 6);
        Assert.Equal(grid, Assert.Single(hatched.GroundHatch.Grids));

        var padCrop = (-300.0, -700.0, 300.0, 0.0);
        Assert.DoesNotContain("/Pattern cs", Render(original, padCrop));
        Assert.Contains("/Pattern cs", Render(hatched, padCrop));
        if (direction == "section")
        {
            Assert.Equal(original.Edges.ToArray(), hatched.Edges.ToArray());
            Assert.Equal(Strokes(original, padCrop), Strokes(hatched, padCrop));
            // These page limits lie inside the projected foundation. Covered hatches must emit no PDF strokes.
            var foundationCrop = (-105.0, -700.0, 105.0, -320.0);
            Assert.Equal(Strokes(original, foundationCrop), Strokes(hatched, foundationCrop));

            string shaded = Render(hatched, null);
            Assert.Contains("0.82 0.82 0.82 rg", shaded);
            Assert.True(shaded.IndexOf("/Pattern cs", StringComparison.Ordinal)
                < shaded.IndexOf("0.82 0.82 0.82 rg", StringComparison.Ordinal));
            Assert.Equal(shaded.Replace("0.82 0.82 0.82 rg", "1 1 1 rg", StringComparison.Ordinal),
                Render(hatched, null, unshadedHiddenLine: true));
            document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
            var opaque = Collect();
            Assert.Equal(Render(hatched, null, unshadedHiddenLine: !detail), Render(opaque, null));
        }
        else
        {
            Assert.Equal(original.Edges.Where(edge => edge.IsCut), hatched.Edges.Where(edge => edge.IsCut));
            var wallBottom = hatched.Edges.Where(edge => edge.CategoryId == wall.CategoryId
                && edge.Y1 == 0 && edge.Y2 == 0);
            if (withPad)
            {
                var exposed = Assert.Single(wallBottom);
                Assert.Equal(-FeetToMillimetres, exposed.X1, 6);
                Assert.Equal(FeetToMillimetres, exposed.X2, 6);
            }
            else Assert.Empty(wallBottom);
            Assert.True(Strokes(original, padCrop) > Strokes(hatched, padCrop));
            Assert.Equal(CountOccurrences(Render(original, padCrop), "1 g\n") + 1,
                CountOccurrences(Render(hatched, padCrop), "1 g\n"));
        }

        document.RawData.GraphicsStylePens = new Dictionary<int, (int, int, int, int, int, int)>
        {
            [1000] = ((int)BuiltInCategory.OST_TopographySurface, -1, 2, 1, 0, -1),
        };
        document.RawData.PenWidthTable = (new Dictionary<int, double[]>
        {
            [20] = [0.18 / FeetToMillimetres],
            [50] = [0.18 / FeetToMillimetres],
            [100] = [0.1 / FeetToMillimetres],
        }, []);
        var styled = new PdfDocument();
        ElevationPrinter.DrawContent(styled.AddPage(PdfPageSize.A1.Landscape), hatched, viewScale, 10, 10,
            faceFill, document, clipRect: padCrop);
        Assert.Equal(viewScale < 100 ? 0.18 : 0.1, Assert.Single(styled.LinePatterns).Width, 6);

        int Strokes(ElevationPrinter.ElevationContent content, (double X0, double Y0, double X1, double Y1) crop)
            => CountOccurrences(Render(content, crop), " l S");

        string Render(ElevationPrinter.ElevationContent content, (double X0, double Y0, double X1, double Y1)? crop,
            bool unshadedHiddenLine = false)
        {
            var pdf = new PdfDocument();
            var page = pdf.AddPage(PdfPageSize.A1.Landscape);
            ElevationPrinter.DrawContent(page, content, viewScale, 10, 10, faceFill,
                clipRect: crop, unshadedHiddenLine: unshadedHiddenLine);
            return page.Content;
        }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void DrawContent_ClipsTheTerrainPenToTheSoilSideOfACutPad(int terrainPen, bool extraInk)
    {
        const int pad = (int)BuiltInCategory.OST_BuildingPad;
        const int terrain = (int)BuiltInCategory.OST_Topography;
        var bottom = new ElevationPrinter.CollectedEdge(0, 300, 1000, 300, 0, 0, -1, -1,
            0, 0, 0, 0, IsCut: true, CategoryId: pad);
        var top = bottom with { Y1 = 500, Y2 = 500 };
        var grid = new TfBlockDeserializer.PatternGrid(0, 0, 0, 0, 0.005, [0.004, 0.001]);
        var hatch = new ElevationPrinter.TerrainHatch(0, 0, 0, [top], [grid],
            [[(0, 300), (1000, 300), (1000, 500), (0, 500)]]);
        var content = new ElevationPrinter.ElevationContent([], [], [], [], [bottom, top], 0, 0, 1000, 600, hatch);
        var document = Document();
        document.RawData.GraphicsStylePens = new Dictionary<int, (int, int, int, int, int, int)>
        {
            [1] = (terrain, -1, 2, terrainPen, 0, 0),
            [2] = (pad, -1, 2, 3, 0, 0),
        };
        document.RawData.PenWidthTable = (new Dictionary<int, double[]>
        {
            [100] = [0.1 / FeetToMillimetres, 0.7 / FeetToMillimetres, 0.18 / FeetToMillimetres],
        }, []);
        var page = new PdfDocument().AddPage(PdfPageSize.A1.Landscape);

        ElevationPrinter.DrawContent(page, content, 100, 0, 0, document: document);

        Assert.Contains("0.5102 w", page.Content);
        const string soilClipAndBand = "W n\n0 0 0 rg\n0 7.5118 m 28.3465 7.5118 l 28.3465 9.4961 l 0 9.4961 l h f";
        if (extraInk) Assert.Contains(soilClipAndBand, page.Content);
        else Assert.DoesNotContain(soilClipAndBand, page.Content);
    }

    [Fact]
    public void Collect_CarvesCutChordsFromTransverseFaces()
    {
        // A face perpendicular to the view direction never outlines, so the cut flag never marks its boundary —
        // yet the section plane slices through it. The plane carves one chord out of the face: a vertical line
        // at the plane crossing spanning the full height. The sliver projection keeps the face itself out of the
        // drawing; the chord still inks.
        var view = SectionView(900, [10, 0, 5]);
        var document = Document(
            QuadWall(600, [[8, 2, 0], [12, 2, 0], [12, 2, 3], [8, 2, 3]], [0, 1, 0]),
            view);

        var content = ElevationPrinter.Collect(document, "section", false, view, null)!;

        Assert.NotNull(content);
        var chord = Assert.Single(content!.Edges, e => e.IsCut);
        Assert.Equal(chord.X1, chord.X2);
        Assert.Equal(2 * FeetToMillimetres, chord.X1, 3);
        Assert.Equal(0, chord.Y1, 0);
        Assert.Equal(3 * FeetToMillimetres, chord.Y2, 0);
    }

    [SkippableFact]
    public void Collect_KeepsCutEdgesWhenTheCropWindowClipsTheFace()
    {
        // A detail view clips its faces to the file crop before the edge pass. Clipping only the projected points
        // broke the one-to-one correspondence with the 3D ring, so `isCut` was never computed and the detail lost
        // its cut profile (the sample's Stair Section had 13 cut edges without a window and 0 with one). The ring
        // is clipped in 3D now and the projected points are rebuilt from it.
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var view = document.Elements.FirstOrDefault(e => e.CategoryId == (int)BuiltInCategory.OST_Views
            && e.Name == "Stair Section");
        Assert.NotNull(view);
        if (view!.ViewCrop is not { Active: true } crop)
            throw new SkipException("The sample's Stair Section has no active crop.");

        var withoutWindow = ElevationPrinter.Collect(document, "section", false, view);
        var withWindow = ElevationPrinter.Collect(document, "section", false, view,
            cropWindow: SheetComposer.CropContentRect(crop));

        Assert.NotNull(withoutWindow);
        Assert.NotNull(withWindow);
        Assert.NotEmpty(withoutWindow!.Edges.Where(edge => edge.IsCut));
        Assert.NotEmpty(withWindow!.Edges.Where(edge => edge.IsCut));
        // The wall's cut face at x = 3982 mm is inside the crop in both runs.
        Assert.Contains(withWindow.Edges, edge => edge.IsCut && Math.Abs(edge.X1 - 3982) < 5 && Math.Abs(edge.X2 - 3982) < 5);
    }

    [SkippableFact]
    public void SampleFile_WallSectionDetailsHaveCutProfiles()
    {
        // The sample's wall-section details previously collected zero cut edges: their planes slice the walls,
        // but only transverse faces cross them, and transverse faces never outlined. The chords carve those
        // cross-sections now (wall strips, slab edges, window and door frames), which is what the reference
        // details draw.
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var view = document.Elements.FirstOrDefault(e => e.CategoryId == (int)BuiltInCategory.OST_Views
            && e.Name == "Typ. Wall Section");
        Assert.NotNull(view);

        var content = ElevationPrinter.Collect(document, "section", false, view);

        Assert.NotNull(content);
        var cut = content!.Edges.Where(edge => edge.IsCut).ToList();
        Assert.True(cut.Count >= 10, $"Typ. Wall Section drew {cut.Count} cut edges");
        Assert.Contains(cut, edge => Math.Abs(edge.X2 - edge.X1) < 0.5);
        Assert.Contains(cut, edge => Math.Abs(edge.Y2 - edge.Y1) < 0.5);
    }

    [Theory]
    [InlineData(true, 10, true)]
    [InlineData(false, 10, false)]
    [InlineData(true, 11, false)]
    public void Collect_UsesTheCutMaterialInAnOrdinaryCroppedSection(bool active, double cropOriginX, bool visible)
    {
        var view = SectionView(900, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 20);
        view.ViewCrop = new ViewCropFrame(cropOriginX, 0, 0, [0, 1, 0], [0, 0, 1], [-1, 0, 0], -1, -1, 5, 2, active);
        var slab = QuadWall(600, [[8, 0, 0], [8, 4, 0], [12, 4, 0], [12, 0, 0]],
            [0, 0, -1], (int)BuiltInCategory.OST_Floors);
        slab.Geometry!.Faces.Add(new GeoFace
        {
            OuterVertices = [[8, 0, 1], [12, 0, 1], [12, 4, 1], [8, 4, 1]],
            Normal = [0, 0, 1],
        });
        slab.TypeId = 601;
        var type = new Element
        {
            Id = slab.TypeId,
            SerializedHostTypeLayers =
            [
                new(BitConverter.DoubleToInt64Bits(0.25), 1, -1, 700, -1, 0, 1),
                new(BitConverter.DoubleToInt64Bits(0.75), 1, -1, 701, -1, 1, 1),
            ],
        };
        var document = Document(view, slab, type);
        var grid = new TfBlockDeserializer.PatternGrid(Math.PI / 4, 0, 0, 0, 0.01, [], 0);
        document.RawData.MaterialPatterns = new Dictionary<int, (int, int, int?)> { [701] = (800, -1, null) };
        document.RawData.FillPatterns = new Dictionary<int, (string, IReadOnlyList<TfBlockDeserializer.PatternGrid>)>
        {
            [800] = ("Cut", [grid]),
        };

        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;

        if (!visible)
        {
            Assert.Null(content.LayerHatches);
            return;
        }
        var layer = Assert.Single(content.LayerHatches!);
        Assert.Same(grid, Assert.Single(layer.Grids));
        Assert.Equal(0, layer.Polygon.Min(p => p.Y), 6);
        Assert.Equal(0.75 * FeetToMillimetres, layer.Polygon.Max(p => p.Y), 6);
    }

    [Theory]
    [InlineData(false, 1.6, 0.75)]
    [InlineData(true, 0.4, 1.25)]
    public void Collect_UsesTheWallMaterialBandsAndKeepsTheSpansBetweenCutEdges(
        bool flipped, double interruptedLayer, double continuousLayer)
    {
        var view = SectionView(900, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 20);
        var wall = QuadWall(901, [[9, 0, 0], [11, 0, 0], [11, 0, 3], [9, 0, 3]], [0, -1, 0]);
        var side = QuadWall(902, [[9, 2, 0], [9, 2, 3], [11, 2, 3], [11, 2, 0]], [0, 1, 0]);
        wall.Geometry!.Faces.Add(side.Geometry!.Faces[0]);
        wall.SerializedWallFlipped = flipped;
        wall.Location = new LocationCurve { StartPoint = [9, 1, 0], EndPoint = [11, 1, 0], Direction = [1, 0, 0] };
        wall.TypeId = 903;
        var type = new Element
        {
            Id = 903,
            SerializedHostTypeLayers = [Layer(0.4), Layer(0.85), Layer(0.75)],
        };
        var cutFace = QuadWall(904, [[9, interruptedLayer, 1], [11, interruptedLayer, 1],
            [11, interruptedLayer, 2], [9, interruptedLayer, 2]], [0, 1, 0], (int)BuiltInCategory.OST_GenericModel);
        var document = Document(view, wall, type, cutFace);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };

        var content = Assert.IsType<ElevationPrinter.ElevationContent>(ElevationPrinter.Collect(document, "section", planeView: view,
            isDetail: true, cropWindow: (-100, -100, 1000, 1000), viewportScale: 20));
        var layers = content.Edges.Where(edge => !edge.IsCut && edge.CategoryId == wall.CategoryId).ToArray();
        Assert.Equal(3, layers.Length);
        Assert.Equal(new[] { (0d, 1d), (2d, 3d) }, Spans(interruptedLayer));
        Assert.Equal(new[] { (0d, 3d) }, Spans(continuousLayer));

        (double Start, double End)[] Spans(double position) => layers
            .Where(edge => Math.Abs(edge.X1 - position * FeetToMillimetres) < 1e-6)
            .Select(edge => (Math.Round(Math.Min(edge.Y1, edge.Y2) / FeetToMillimetres, 9),
                Math.Round(Math.Max(edge.Y1, edge.Y2) / FeetToMillimetres, 9)))
            .OrderBy(span => span.Item1).ToArray();

        static RvtIO.Internal.RawCompoundStructureLayer Layer(double feet) =>
            new(BitConverter.DoubleToInt64Bits(feet), 1, -1, 700, -1, 0, 1);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Collect_OpaqueWallCutKeepsTheOpeningClear(bool opaque, bool isDetail)
    {
        var view = SectionView(910, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 20);
        view.ViewCrop = new ViewCropFrame(10, 0, 0, [0, 1, 0], [0, 0, 1], [-1, 0, 0],
            -4, -1, 4, 4, true);
        var wall = QuadWall(911, [[9, -1, 0], [11, -1, 0], [11, -1, 3], [9, -1, 3]], [0, -1, 0]);
        wall.BoundingBox = [9, -1, 0, 11, 1, 3];
        var faces = wall.Geometry!.Faces;
        faces[0].InnerVertices = [[[9.5, -1, 1], [10.5, -1, 1], [10.5, -1, 2], [9.5, -1, 2]]];
        faces.AddRange([
            new()
            {
                Normal = [0, 1, 0],
                OuterVertices = [[9, 1, 0], [9, 1, 3], [11, 1, 3], [11, 1, 0]],
                InnerVertices = [[[9.5, 1, 1], [9.5, 1, 2], [10.5, 1, 2], [10.5, 1, 1]]],
            },
            new() { Normal = [0, 0, -1], OuterVertices = [[9, -1, 0], [9, 1, 0], [11, 1, 0], [11, -1, 0]] },
            new() { Normal = [0, 0, 1], OuterVertices = [[9, -1, 3], [11, -1, 3], [11, 1, 3], [9, 1, 3]] },
            new() { Normal = [0, 0, 1], OuterVertices = [[9.5, -1, 1], [10.5, -1, 1], [10.5, 1, 1], [9.5, 1, 1]] },
            new() { Normal = [0, 0, -1], OuterVertices = [[9.5, -1, 2], [9.5, 1, 2], [10.5, 1, 2], [10.5, -1, 2]] },
        ]);
        var behind = QuadWall(912, [[7, -2, 0.5], [7, 2, 0.5], [7, 2, 1.5], [7, -2, 1.5]],
            [1, 0, 0], (int)BuiltInCategory.OST_GenericModel);
        wall.TypeId = 913;
        wall.SerializedWallFlipped = false;
        wall.Location = new LocationCurve { StartPoint = [9, 0, 0], EndPoint = [11, 0, 0], Direction = [1, 0, 0] };
        var type = new Element
        {
            Id = 913,
            SerializedHostTypeLayers =
            [
                new(BitConverter.DoubleToInt64Bits(0.75), 1, -1, 100, -1, 0, 1),
                new(BitConverter.DoubleToInt64Bits(1.25), 1, -1, 101, -1, 1, 1),
            ],
        };
        var document = Document(view, wall, type, behind);
        if (opaque) document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        var grid = new TfBlockDeserializer.PatternGrid(0, 0, 0, 0, 0.01, [], 0);
        document.RawData.MaterialPatterns = new Dictionary<int, (int, int, int?)> { [100] = (102, -1, null) };
        document.RawData.FillPatterns = new Dictionary<int, (string, IReadOnlyList<TfBlockDeserializer.PatternGrid>)>
            { [102] = ("Wall finish", [grid]) };

        var content = Assert.IsType<ElevationPrinter.ElevationContent>(ElevationPrinter.Collect(document, "section",
            planeView: view, isDetail: isDetail, cropWindow: isDetail ? (-1000, -100, 1000, 1000) : null, viewportScale: 20));

        bool maskProjection = opaque && isDetail;
        Assert.Equal(maskProjection ? 2 : 0, content.DetailCutFaces?.Count ?? 0);
        Assert.Equal(new[] { (-2d, 2d) }, Spans(1.5));
        Assert.Equal(maskProjection ? new[] { (-2d, -1d), (1d, 2d) } : new[] { (-2d, 2d) }, Spans(0.5));
        if (opaque)
        {
            var fills = content.LayerHatches!.OrderBy(fill => fill.Polygon.Min(point => point.Y)).ToArray();
            Assert.Equal(2, fills.Length);
            Assert.Equal(FeetToMillimetres, fills[0].Polygon.Max(point => point.Y), 6);
            Assert.Equal(2 * FeetToMillimetres, fills[1].Polygon.Min(point => point.Y), 6);
        }

        (double Start, double End)[] Spans(double elevation) => content.Edges
            .Where(edge => edge.CategoryId == behind.CategoryId && !edge.IsCut
                && Math.Abs(edge.Y1 - elevation * FeetToMillimetres) < 1e-6 && edge.Y1 == edge.Y2)
            .Select(edge => (Math.Round(Math.Min(edge.X1, edge.X2) / FeetToMillimetres, 9),
                Math.Round(Math.Max(edge.X1, edge.X2) / FeetToMillimetres, 9)))
            .OrderBy(span => span.Item1).ToArray();
    }

    [Theory]
    [InlineData(true, 1, 2)]
    [InlineData(true, 0.9, 0)]
    [InlineData(false, 1, 0)]
    public void Collect_DrawsRoofLayersOnlyWhenTheCutMatchesTheStoredThickness(
        bool opaque, double stackThickness, int count)
    {
        var view = SectionView(920, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 20);
        double drop = Math.Sqrt(1.25);
        int category = (int)BuiltInCategory.OST_Roofs;
        var roof = QuadWall(921, [[9, -2, 2], [11, -2, 2], [11, 2, 4], [9, 2, 4]],
            [0, -0.5, 1], category);
        roof.BoundingBox = [9, -2, 2 - drop, 11, 2, 4];
        roof.TypeId = 922;
        roof.Geometry!.Faces.AddRange([
            new()
            {
                Normal = [0, 0.5, -1],
                OuterVertices = [[9, -2, 2 - drop], [9, 2, 4 - drop], [11, 2, 4 - drop], [11, -2, 2 - drop]],
            },
            new()
            {
                Normal = [0, -1, 0],
                OuterVertices = [[9, -2, 2 - drop], [11, -2, 2 - drop], [11, -2, 2], [9, -2, 2]],
            },
            new()
            {
                Normal = [0, 1, 0],
                OuterVertices = [[9, 2, 4 - drop], [9, 2, 4], [11, 2, 4], [11, 2, 4 - drop]],
            },
        ]);
        var type = new Element
        {
            Id = roof.TypeId,
            SerializedHostTypeLayers = [Layer(0.25), Layer(0.5), Layer(stackThickness - 0.75)],
        };
        var doc = Document(view, roof, type);
        if (opaque) doc.RawData.OpaquePlanViews = new HashSet<int> { view.Id };

        var content = Assert.IsType<ElevationPrinter.ElevationContent>(ElevationPrinter.Collect(doc, "section",
            planeView: view, isDetail: true, cropWindow: (-500, 0, 500, 1400), viewportScale: 20));

        Assert.Equal(opaque ? 1 : 0, content.DetailCutFaces?.Count ?? 0);
        var lines = content.Edges.Where(edge => edge.CategoryId == category && !edge.IsCut && edge.FaceA < 0)
            .OrderByDescending(edge => edge.Y1 - 0.5 * edge.X1).ToArray();
        Assert.Equal(count, lines.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            var edge = lines[i];
            double intercept = (3 - (0.25 + i * 0.5) * drop) * FeetToMillimetres;
            Assert.Equal(intercept, edge.Y1 - 0.5 * edge.X1, 6);
            Assert.Equal(intercept, edge.Y2 - 0.5 * edge.X2, 6);
            Assert.Equal(-500, Math.Min(edge.X1, edge.X2), 6);
            Assert.Equal(500, Math.Max(edge.X1, edge.X2), 6);
        }

        static RvtIO.Internal.RawCompoundStructureLayer Layer(double feet) =>
            new(BitConverter.DoubleToInt64Bits(feet), 1, -1, 10, -1, 0, 1);
    }

    [Theory]
    [InlineData(true, 1, 50, 3, 4)]
    [InlineData(true, -1, 50, 3, 0)]
    [InlineData(false, 1, 50, 3, 0)]
    [InlineData(true, 1, 100, 3, 4)]
    [InlineData(true, 1, 100, 2, 0)]
    [InlineData(false, 1, 100, 3, 0)]
    public void Collect_JoinsVerifiedRoofLayersAtTheRidgeOfAFineSection(bool opaque, double rise,
        int scale, int detail, int count)
    {
        var view = SectionView(930, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, scale);
        Add(view, BuiltInParameter.VIEW_DETAIL_LEVEL, StorageType.Integer, detail);
        view.ViewCrop = new ViewCropFrame(10, 0, 0, [0, 1, 0], [0, 0, 1], [-1, 0, 0],
            -3, 0, 3, 5, true);
        double drop = Math.Sqrt(1.25);
        double peak = 3 + rise;
        int category = (int)BuiltInCategory.OST_Roofs;
        var roof = QuadWall(931, [[9, -2, 3], [11, -2, 3], [11, 0, peak], [9, 0, peak]],
            [0, -rise / 2, 1], category);
        roof.TypeId = 932;
        roof.BoundingBox = [9, -2, Math.Min(3, peak) - drop, 11, 2, Math.Max(3, peak)];
        roof.Geometry!.Faces.AddRange([
            new()
            {
                Normal = [0, rise / 2, 1],
                OuterVertices = [[9, 0, peak], [11, 0, peak], [11, 2, 3], [9, 2, 3]],
            },
            new()
            {
                Normal = [0, rise / 2, -1],
                OuterVertices = [[9, -2, 3 - drop], [9, 0, peak - drop], [11, 0, peak - drop], [11, -2, 3 - drop]],
            },
            new()
            {
                Normal = [0, -rise / 2, -1],
                OuterVertices = [[9, 0, peak - drop], [9, 2, 3 - drop], [11, 2, 3 - drop], [11, 0, peak - drop]],
            },
            new()
            {
                Normal = [0, -1, 0],
                OuterVertices = [[9, -2, 3 - drop], [11, -2, 3 - drop], [11, -2, 3], [9, -2, 3]],
            },
            new()
            {
                Normal = [0, 1, 0],
                OuterVertices = [[9, 2, 3 - drop], [9, 2, 3], [11, 2, 3], [11, 2, 3 - drop]],
            },
        ]);
        var type = new Element
        {
            Id = roof.TypeId,
            SerializedHostTypeLayers =
            [
                new(BitConverter.DoubleToInt64Bits(0.25), 1, -1, 10, -1, 0, 1),
                new(BitConverter.DoubleToInt64Bits(0.5), 1, -1, 20, -1, 1, 1),
                new(BitConverter.DoubleToInt64Bits(0.25), 1, -1, 30, -1, 2, 1),
            ],
        };
        var doc = Document(view, roof, type);
        if (opaque) doc.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        var grid = new TfBlockDeserializer.PatternGrid(0, 0, 0, 0, 0.01, [], 0);
        doc.RawData.MaterialPatterns = new Dictionary<int, (int, int, int?)> { [20] = (103, -1, null) };
        doc.RawData.FillPatterns = new Dictionary<int, (string, IReadOnlyList<TfBlockDeserializer.PatternGrid>)>
            { [103] = ("Roof core", [grid]) };

        var content = Assert.IsType<ElevationPrinter.ElevationContent>(
            ElevationPrinter.Collect(doc, "section", planeView: view, viewportScale: scale));

        var lines = content.Edges.Where(edge => edge.CategoryId == category && !edge.IsCut && edge.FaceA < 0)
            .OrderByDescending(edge => Math.Max(edge.Y1, edge.Y2)).ToArray();
        Assert.Equal(count, lines.Length);
        Assert.Equal(count > 0 ? 1 : 0, content.DetailCutFaces?.Count ?? 0);
        var bands = content.LayerHatches ?? [];
        Assert.Equal(count > 0 ? 2 : 0, bands.Count);
        if (count > 0)
        {
            var left = Assert.Single(bands, band => band.Polygon.Any(point => point.X < -1e-6));
            var right = Assert.Single(bands, band => band.Polygon.Any(point => point.X > 1e-6));
            Assert.Equal(0, left.Polygon.Max(point => point.X), 6);
            Assert.Equal(0, right.Polygon.Min(point => point.X), 6);
            Assert.All(bands, band =>
            {
                Assert.Same(grid, Assert.Single(band.Grids));
                var depths = band.Polygon.Select(point =>
                    (peak * FeetToMillimetres - 0.5 * Math.Abs(point.X) - point.Y) / drop).ToArray();
                Assert.Equal(0.25 * FeetToMillimetres, depths.Min(), 6);
                Assert.Equal(0.75 * FeetToMillimetres, depths.Max(), 6);
            });
        }
        for (int i = 0; i < lines.Length; i++)
        {
            var edge = lines[i];
            double layerDepth = (i < 2 ? 0.25 : 0.75) * drop;
            var ridge = edge.Y1 > edge.Y2 ? (edge.X1, edge.Y1) : (edge.X2, edge.Y2);
            Assert.Equal(0, ridge.Item1, 6);
            Assert.Equal((peak - layerDepth) * FeetToMillimetres, ridge.Item2, 6);
            Assert.Equal(2 * FeetToMillimetres, Math.Max(Math.Abs(edge.X1), Math.Abs(edge.X2)), 6);
        }
        if (count > 0)
        {
            Assert.Equal(2, lines.Count(edge => Math.Abs(Math.Min(edge.X1, edge.X2) + 2 * FeetToMillimetres) < 1e-6));
            Assert.Equal(2, lines.Count(edge => Math.Abs(Math.Max(edge.X1, edge.X2) - 2 * FeetToMillimetres) < 1e-6));
        }
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void Collect_HatchesAClosedUniformPrecastStairCut(bool closed, bool uniform, bool hatched)
    {
        var view = SectionView(940, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 50);
        view.ViewCrop = new ViewCropFrame(10, 0, 0, [0, 1, 0], [0, 0, 1], [-1, 0, 0],
            -2, 0, 2, 5, true);
        var stair = QuadWall(941, [[9, -1, 1], [11, -1, 1], [11, -1, 2], [9, -1, 2]], [0, -1, 0],
            (int)BuiltInCategory.OST_Stairs);
        var faces = stair.Geometry!.Faces;
        faces[0].RenderStyleId = 100;
        faces.AddRange([
            new() { Normal = [0, 1, 0], RenderStyleId = 100,
                OuterVertices = [[9, 1, 1], [9, 1, 2], [11, 1, 2], [11, 1, 1]] },
            new() { Normal = [0, 0, -1], RenderStyleId = 100,
                OuterVertices = [[9, -1, 1], [9, 1, 1], [11, 1, 1], [11, -1, 1]] },
        ]);
        if (closed)
            faces.Add(new() { Normal = [0, 0, 1], RenderStyleId = uniform ? 100 : 101,
                OuterVertices = [[9, -1, 2], [11, -1, 2], [11, 1, 2], [9, 1, 2]] });

        var doc = Document(view, stair);
        doc.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        var grid = new TfBlockDeserializer.PatternGrid(0, 0, 0, 0, 0.01, [0.001, 0.003], 0);
        doc.RawData.MaterialPatterns = new Dictionary<int, (int, int, int?)> { [100] = (102, -1, null) };
        doc.RawData.FillPatterns = new Dictionary<int, (string, IReadOnlyList<TfBlockDeserializer.PatternGrid>)>
            { [102] = ("Precast", [grid]) };

        var content = Assert.IsType<ElevationPrinter.ElevationContent>(
            ElevationPrinter.Collect(doc, "section", planeView: view, viewportScale: 50));

        if (!hatched)
        {
            Assert.Empty(content.LayerHatches!);
            return;
        }

        var fill = Assert.Single(content.LayerHatches!);
        Assert.Same(grid, Assert.Single(fill.Grids));
        Assert.Equal(-FeetToMillimetres, fill.Polygon.Min(p => p.X), 6);
        Assert.Equal(FeetToMillimetres, fill.Polygon.Max(p => p.X), 6);
        Assert.Equal(FeetToMillimetres, fill.Polygon.Min(p => p.Y), 6);
        Assert.Equal(2 * FeetToMillimetres, fill.Polygon.Max(p => p.Y), 6);
    }

    [Fact]
    public void AddLayerLines_SubdividesAWallPairByItsTypeStack()
    {
        // An asymmetric stack (20/100/10 mm, total 130) between two parallel chords: the source-face normal
        // decides the side — face A looks at face B, so the stack starts at the interior face and runs mirrored,
        // drawing the boundaries at x = 10 and x = 110. Both lines ink thin, like projection edges.
        static RvtIO.Internal.RawCompoundStructureLayer Layer(double millimetres) => new(
            BitConverter.DoubleToInt64Bits(millimetres / 304.8), 1, -1, 700, -1, 0, 1);
        var type = new Element
        {
            Id = 10,
            CategoryId = (int)BuiltInCategory.OST_Walls,
            SerializedHostTypeLayers = [Layer(20), Layer(100), Layer(10)],
        };
        var document = RvtDocument.FromInfo(new RevitFileInfo { Elements = { type } });
        var raw = new List<(double X1, double Y1, double X2, double Y2,
            double Nx, double Ny, double Nz, int TypeId, int CategoryId, double Depth)>
        {
            (0, 0, 0, 1000, -1, 0, 0, 10, (int)BuiltInCategory.OST_Walls, 0),
            (130, 0, 130, 1000, 1, 0, 0, 10, (int)BuiltInCategory.OST_Walls, 0),
        };
        var output = new List<ElevationPrinter.CollectedEdge>();

        ElevationPrinter.AddLayerLines(raw, document, output,
            [0, 1, 0], [1, 0, 0], [0, 0, 1], 0, 0, 0, 0);

        Assert.Equal(2, output.Count);
        var xs = output.Select(edge => (edge.X1 + edge.X2) / 2).OrderBy(x => x).ToList();
        Assert.Equal(10, xs[0], 1);
        Assert.Equal(110, xs[1], 1);
        Assert.All(output, edge => Assert.False(edge.IsCut));
        Assert.All(output, edge => Assert.Equal(0, edge.Y1, 1));
        Assert.All(output, edge => Assert.Equal(1000, edge.Y2, 1));
    }

    [SkippableFact]
    public void SampleFile_WallSectionDetailsHaveLayerLines()
    {
        // The 1:20 wall section draws every layer boundary of its walls, not just the two outer faces: a thin
        // wall edge must stand strictly between two parallel cut wall edges less than 400 mm apart.
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var view = document.Elements.FirstOrDefault(e => e.CategoryId == (int)BuiltInCategory.OST_Views
            && e.Name == "Typ. Wall Section");
        Assert.NotNull(view);

        var content = ElevationPrinter.Collect(document, "section", false, view);

        Assert.NotNull(content);
        var cuts = content!.Edges.Where(edge => edge.IsCut
            && edge.CategoryId == (int)BuiltInCategory.OST_Walls
            && Math.Abs(edge.X2 - edge.X1) < 0.5
            && Math.Abs(edge.Y2 - edge.Y1) > 50).ToList();
        var thins = content.Edges.Where(edge => !edge.IsCut
            && edge.CategoryId == (int)BuiltInCategory.OST_Walls
            && Math.Abs(edge.X2 - edge.X1) < 0.5
            && Math.Abs(edge.Y2 - edge.Y1) > 50).ToList();
        static bool Between(
            ElevationPrinter.CollectedEdge thin,
            List<ElevationPrinter.CollectedEdge> cuts)
        {
            for (int i = 0; i < cuts.Count; i++)
                for (int j = 0; j < cuts.Count; j++)
                {
                    if (i == j) continue;
                    double lo = Math.Min(cuts[i].X1, cuts[j].X1), hi = Math.Max(cuts[i].X1, cuts[j].X1);
                    if (hi - lo > 400 || hi - lo < 5) continue;
                    if (thin.X1 > lo + 1 && thin.X1 < hi - 1) return true;
                }
            return false;
        }
        Assert.Contains(thins, thin => Between(thin, cuts));
    }

    [Fact]
    public void Collect_TagsCutEdgesWithTheirCategory()
    {
        // A railing slat straddling the section plane: the cut edge carries the railing category, so it
        // inks with the railing cut pen (0.25 mm at 1:20) rather than the wall cut pen (0.35 mm).
        var view = SectionView(900, [10, 0, 5]);
        var document = Document(
            QuadWall(601, [[8, 0, 0], [12, 0, 3], [12, 4, 3], [8, 4, 0]], [-0.6, 0, 0.8],
                (int)BuiltInCategory.OST_StairsRailing),
            view);

        var content = ElevationPrinter.Collect(document, "section", false, view, null)!;

        Assert.NotNull(content);
        var cut = Assert.Single(content!.Edges, e => e.IsCut);
        Assert.Equal((int)BuiltInCategory.OST_StairsRailing, cut.CategoryId);
        Assert.All(content.Edges, e => Assert.Equal((int)BuiltInCategory.OST_StairsRailing, e.CategoryId));
    }

    [SkippableFact]
    public void SampleFile_StairDetailKeepsGridsAndInCropLevels()
    {
        // The reference draws grid bubbles across the Main Stair Detail and exactly the three level
        // bands whose height falls in its crop (Level 1, Ceiling, Level 2); DrawContent height-clips
        // the rest, so only the in-crop ones can ink.
        string? path = TestFiles.TryFind("Test_2022.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var view = document.Elements.FirstOrDefault(e =>
            e.CategoryId == (int)BuiltInCategory.OST_Views && e.Name == "Main Stair Detail");
        Assert.NotNull(view);

        var content = ElevationPrinter.Collect(document, "section", false, view, null);

        Assert.NotNull(content);
        Assert.NotEmpty(content!.Grids);
        var crop = view!.ViewCrop;
        Assert.NotNull(crop);
        Assert.True(crop!.Active);
        Assert.True(view.ViewAnnotationCropActive);
        Assert.NotNull(view.ViewAnnotationCrop);
        var (_, right, top, _) = view.ViewAnnotationCrop.Value;
        var (x0, y0, x1, y1) = SheetComposer.CropContentRect(crop);
        const int scale = 20;
        var levels = content.Levels.Where(level => level.Y >= y0 && level.Y <= y1).ToList();
        Assert.Equal(3, levels.Count);
        Assert.All(levels, level => Assert.Equal(x1 + right * FeetToMillimetres * scale,
            content.NativeLevels![level.Y].HeadX, 6));
        var grids = content.Grids.Where(grid => grid.X >= x0 && grid.X <= x1).ToList();
        Assert.Equal(new[] { "C", "B", "A" }, grids.Select(grid => grid.Name));
        Assert.All(grids, grid => Assert.Equal(y1 + top * FeetToMillimetres * scale,
            content.NativeGrids![grid.X].Top, 6));
    }

    [SkippableFact]
    public void SampleFile_StairSectionUsesUncroppedGridEnvelope()
    {
        string? path = TestFiles.TryFind("Test_2022.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var view = Assert.Single(document.Elements, e => e.Name == "Stair Section" && e.ViewCrop != null);
        Assert.False(view.ViewAnnotationCropActive);
        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;
        var grids = content.Grids.Where(grid => grid.Name is "A" or "B" or "C").ToList();
        Assert.Equal(3, grids.Count);
        foreach (var (x, name) in grids)
        {
            var grid = Assert.Contains(x, content.NativeGrids!);
            var plane = Assert.Single(document.Elements, e => e.CategoryId == (int)BuiltInCategory.OST_Grids
                && e.Name == name).GridPlane!;
            double envelopeTop = (plane.Origin[2] + plane.Envelope[3]) * FeetToMillimetres;
            Assert.Equal(envelopeTop, grid.Top + 2 * grid.Radius * 50, 6);
        }
    }

    private static RvtDocument Document(params Element[] elements) =>
        RvtDocument.FromInfo(new RevitFileInfo { Elements = elements.ToList() });

    [Fact]
    public void Views_ListsCompassElevationsWithTheirScale()
    {
        var document = Document(
            ElevationView(400, "South", 100), ElevationView(401, "Longitudinal Section", 100),
            new Element { Id = 402, CategoryId = (int)BuiltInCategory.OST_Views });

        var views = ElevationPrinter.Views(document);

        // The plan view is not an elevation; the section is one (projected along the short axis). The list is sorted by
        // name, so the views are found by id.
        Assert.Equal(2, views.Count);
        ElevationView south = Assert.Single(views.Where(view => view.ViewId == 400));
        Assert.Equal("south", south.Direction);
        Assert.Equal(100, south.Scale);
        ElevationView section = Assert.Single(views.Where(view => view.ViewId == 401));
        Assert.Equal("section", section.Direction);
    }

    [Fact]
    public void Collect_ProjectsTheVerticalFaceOnTheViewPlane()
    {
        var document = Document(WallWithFace(600));

        var content = ElevationPrinter.Collect(document, "south");

        Assert.NotNull(content);
        Assert.Single(content!.Faces);
        Assert.Equal(0, content.MinX, 3);
        Assert.Equal(4 * FeetToMillimetres, content.MaxX, 3);
        Assert.Equal(0, content.MinY, 3);
        Assert.Equal(3 * FeetToMillimetres, content.MaxY, 3);
        Assert.True(content.Faces[0].Outline);
    }

    [Theory]
    [InlineData("south")]
    [InlineData("section")]
    public void Collect_ClipsPlacedPlantingAtTheViewPlaneAndKeepsSmallFacets(string direction)
    {
        var view = new Element
        {
            Id = 900, ViewOrigin = [0, 0, 0], ViewDirection = [0, 1, 0],
            ViewHorizontalDirection = [1, 0, 0], ViewVerticalDirection = [0, 0, 1],
        };
        int planting = (int)BuiltInCategory.OST_Planting;
        var planted = QuadWall(600, [[0, -1, 1], [2, 1, 1], [2, 1, 1.5]], [0, -1, 0], planting);
        var outside = QuadWall(601, [[1, -2, 1], [3, -2, 1], [3, -2, 1.5]], [0, -1, 0], planting);
        var family = QuadWall(602, [[1, 1, 1], [3, 1, 1], [3, 1, 1.5]], [0, -1, 0], planting);
        family.SerializedOwnElementClassId = 1829;
        var document = Document(view, WallWithFace(603), planted, outside, family);

        var content = ElevationPrinter.Collect(document, direction, planeView: view)!;
        var ring = Assert.Single(content.SiteSilhouettes!);
        Assert.Equal(4, ring.Count);
        Assert.Equal(FeetToMillimetres, ring.Min(point => point.X), 6);
        Assert.Equal(2 * FeetToMillimetres, ring.Max(point => point.X), 6);
        Assert.InRange(PlanPrinter.SignedArea(ring), 1, 499_999);
    }

    [Theory]
    [InlineData(true, true, 1, true)]
    [InlineData(true, true, -0.5, false)]
    [InlineData(true, false, 1, false)]
    [InlineData(false, true, 1, false)]
    public void Collect_KeepsAnRpcProxyWhenItsSavedExtentReachesTheView(
        bool rpc, bool savedExtent, double maxDepth, bool visible)
    {
        const int planting = (int)BuiltInCategory.OST_Planting;
        var view = new Element
        {
            Id = 900, ViewOrigin = [0, 0, 0], ViewDirection = [0, 1, 0],
            ViewHorizontalDirection = [1, 0, 0], ViewVerticalDirection = [0, 0, 1],
        };
        var tree = QuadWall(601, [[2, -1, 1], [4, -1, 1], [4, -1, 2]], [0, -1, 0], planting);
        tree.Geometry!.Faces[0].PolygonMesh = true;
        tree.InstanceOriginXY = (2.5, -1);
        tree.BoundingBox = [2, -3, 1, 4, maxDepth, 2];
        tree.BoundingBoxIsHeaderOutline = savedExtent;
        var type = new Element { Id = 903, CategoryId = planting, NativeIsSymbol = true };
        if (rpc) Add(type, BuiltInParameter.RENDER_RPC_FILENAME, StorageType.String, "RPC tree");
        tree.TypeId = type.Id;

        var document = Document(view, WallWithFace(602), tree, type);
        document.RawData.OpaqueViewsWithSurfacePatterns = new HashSet<int> { view.Id };
        var content = ElevationPrinter.Collect(document, "south", planeView: view)!;

        if (visible)
        {
            var ring = Assert.Single(content.SiteSilhouettes!);
            Assert.Equal(3, ring.Count);
            Assert.Equal(FeetToMillimetres * FeetToMillimetres, PlanPrinter.SignedArea(ring), 6);
            Assert.Equal(3, Assert.IsAssignableFrom<IReadOnlyList<ElevationPrinter.CollectedEdge>>(
                content.SiteOutlines).Count(edge => edge.CategoryId == planting));
        }
        else
        {
            Assert.Null(content.SiteSilhouettes);
            Assert.Null(content.SiteOutlines);
        }
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_IOSModelGroups)]
    [InlineData(BuiltInCategory.OST_Furniture)]
    public void Collect_ProjectsGroupTemplateGeometryWithItsInstance(BuiltInCategory category)
    {
        // The reference draws the group template copy as well as the placed instance: skipping templates costs
        // 0.6% raster recall on the sample's stair sheet, so both rings project.
        var view = SectionView(900, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 20);
        view.ViewCrop = new(10, 0, 0, [0, 1, 0], [0, 0, 1], [-1, 0, 0], -2, -1, 5, 5, true);
        var placed = QuadWall(601, [[8, 0, 0], [8, 2, 0], [8, 2, 2], [8, 0, 2]], [1, 0, 0], (int)category);
        var template = QuadWall(602, [[8, 3, 0], [8, 5, 0], [8, 5, 2], [8, 3, 2]], [1, 0, 0], (int)category);
        template.IsGroupTemplate = true;
        var document = Document(view, placed, template);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };

        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;

        Assert.Equal(2, content.Faces.Count);
        Assert.Equal(8, content.Edges.Count(edge => edge.CategoryId == (int)category));
    }

    [Fact]
    public void Collect_KeepsSlopingGroupedEquipmentEdgesThroughStoredGlass()
    {
        var view = new Element
        {
            Id = 900, ViewOrigin = [0, 0, 0], ViewDirection = [0, 1, 0],
            ViewHorizontalDirection = [1, 0, 0], ViewVerticalDirection = [0, 0, 1],
        };
        int equipment = (int)BuiltInCategory.OST_ElectricalEquipment;
        var cell = QuadWall(600, [[1, 2, 1], [3, 2, 1], [3, 4, 1.5], [1, 4, 1.5]], [0, -0.25, 1], equipment);
        cell.SerializedOwnElementClassId = 1794; // FamilyInstance
        cell.SerializedGroupId = 601;
        cell.Geometry!.Faces[0].PlaneSurface = true;
        var glass = QuadWall(601, [[0, 1, 0.85], [4, 1, 0.85], [4, 5, 1.85], [0, 5, 1.85]],
            [0, -0.25, 1], (int)BuiltInCategory.OST_IOSModelGroups);
        glass.Geometry!.Faces[0].PlaneSurface = true;
        glass.Geometry.Faces[0].NestedRenderStyleId = 602;
        glass.Geometry.Faces.Add(new GeoFace
        {
            OuterVertices = [[0, 1, 0.85], [4, 1, 0.85], [4, 5, 1.85], [0, 5, 1.85]],
            InnerVertices = [[[0.5, 1.25, 0.9125], [3.5, 1.25, 0.9125], [3.5, 4.75, 1.7875], [0.5, 4.75, 1.7875]]],
            Normal = [0, -0.25, 1], PlaneSurface = true,
        });
        var material = new Element { Id = 602, CategoryId = (int)BuiltInCategory.OST_Materials };

        Assert.DoesNotContain(ElevationPrinter.Collect(Document(view, cell, glass, material), "south", planeView: view)!.Edges,
            edge => edge.CategoryId == equipment);
        material.SerializedMaterialTransparency = 0.5;

        var content = ElevationPrinter.Collect(Document(view, cell, glass, material), "south", planeView: view)!;
        Assert.Single(content.Faces, face => face.Outline);
        Assert.Equal(4, content.Edges.Count(edge => edge.CategoryId == equipment));
    }

    [Fact]
    public void DrawContent_UsesAuthoredEquipmentShadingColoursThroughTransparentCovers()
    {
        var view = new Element
        {
            Id = 900, ViewOrigin = [0, 0, 0], ViewDirection = [0, 1, 0],
            ViewHorizontalDirection = [1, 0, 0], ViewVerticalDirection = [0, 0, 1],
        };
        var panel = QuadWall(600, [[0, 2, 0], [4, 2, 0], [4, 2, 3], [0, 2, 3]],
            [0, -1, 0], (int)BuiltInCategory.OST_ElectricalEquipment);
        panel.SerializedOwnElementClassId = 1794; // FamilyInstance
        panel.SerializedGroupId = 620;
        panel.Geometry!.Faces[0].PlaneSurface = true;
        panel.Geometry.Faces[0].NestedRenderStyleId = 610;
        panel.Geometry.Faces.Add(new GeoFace
        {
            OuterVertices = [[0, 1, 0], [4, 1, 0], [4, 1, 3], [0, 1, 3]],
            Normal = [0, -1, 0], PlaneSurface = true, NestedRenderStyleId = 611,
        });
        panel.Geometry.Faces.Add(new GeoFace
        {
            OuterVertices = [[4, 1, 0], [5, 1, 0], [5, 1, 3], [4, 1, 3]],
            Normal = [0, -1, 0], PlaneSurface = true, NestedRenderStyleId = 612,
        });
        var cell = new Element { Id = 610, HasShadingAppearance = true, SerializedMaterialUsesRenderAppearance = false };
        Add(cell, BuiltInParameter.MATERIAL_PARAM_COLOR, StorageType.Integer, 0x333333);
        var glass = new Element { Id = 611, HasShadingAppearance = true, SerializedMaterialTransparency = 0.5 };
        var frame = new Element { Id = 612, HasShadingAppearance = true, SerializedMaterialUsesRenderAppearance = true };
        Add(frame, BuiltInParameter.MATERIAL_PARAM_COLOR, StorageType.Integer, 0);
        var content = ElevationPrinter.Collect(Document(view, panel, cell, glass, frame), "south", planeView: view)!;

        Assert.Equal(2, content.FaceFills!.Count);
        Assert.Contains(content.FaceFills.Values, gray => gray.HasValue && Math.Abs(gray.Value - 0.2) < 1e-9);
        Assert.Contains(content.FaceFills.Values, gray => !gray.HasValue);
        var page = new PdfDocument().AddPage(PdfPageSize.A4);
        ElevationPrinter.DrawContent(page, content, 100, 10, 10);
        Assert.Contains("0.2 0.2 0.2 rg", page.Content);
    }


    [Theory]
    [InlineData(0.5, 6)]
    [InlineData(1.5, 8)]
    public void Collect_OutlinesPlacedSiteMeshWithoutItsDiagonalOrCoveredEdges(double coverDepth, double length)
    {
        var view = new Element
        {
            Id = 900, ViewOrigin = [0, 0, 0], ViewDirection = [0, 1, 0],
            ViewHorizontalDirection = [1, 0, 0], ViewVerticalDirection = [0, 0, 1],
        };
        var mesh = QuadWall(600, [[0, 1, 0], [2, 1, 0], [2, 1, 2]],
            [0, -1, 0], (int)BuiltInCategory.OST_Site);
        mesh.SerializedOwnElementClassId = 1794;
        mesh.Geometry!.Faces.Add(new GeoFace
        {
            OuterVertices = [[0, 1, 0], [2, 1, 2], [0, 1, 2]],
            Normal = [0, -1, 0],
        });
        var cover = QuadWall(601, [[1, coverDepth, 1], [3, coverDepth, 1], [3, coverDepth, 3], [1, coverDepth, 3]],
            [0, -1, 0], (int)BuiltInCategory.OST_Planting);
        var back = QuadWall(602, [[5, 3, 0], [6, 3, 0], [6, 3, 2], [5, 3, 2]], [0, -1, 0]);
        var content = ElevationPrinter.Collect(Document(view, mesh, cover, back), "south", planeView: view)!;
        Assert.Equal(3, content.SiteSilhouettes!.Count);
        Assert.Equal(4, content.SiteOutlines!.Count);
        Assert.Equal(length * FeetToMillimetres, content.SiteOutlines.Sum(edge =>
            Math.Abs(edge.X2 - edge.X1) + Math.Abs(edge.Y2 - edge.Y1)), 6);
        Assert.All(content.SiteOutlines, edge => Assert.True(edge.X1 == edge.X2 || edge.Y1 == edge.Y2));
    }

    [Fact]
    public void Collect_OutlinesPlacedSiteObjectsBehindAnOpaqueDetail()
    {
        var view = SectionView(630, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 20);
        var mast = QuadWall(631, [[8, 0, 0], [8, 2, 0], [8, 2, 4], [8, 0, 4]], [1, 0, 0],
            (int)BuiltInCategory.OST_Site);
        mast.SerializedOwnElementClassId = 1794;
        var wall = QuadWall(632, [[9, -1, -1], [9, 3, -1], [9, 3, 2], [9, -1, 2]], [1, 0, 0]);
        var document = Document(view, mast, wall);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };

        var content = ElevationPrinter.Collect(document, "section", planeView: view,
            isDetail: true, cropWindow: (-400, -400, 1000, 1300), viewportScale: 20)!;

        Assert.Single(content.SiteSilhouettes!);
        Assert.Equal(3, content.SiteOutlines!.Count);
        Assert.Equal(2 * FeetToMillimetres, content.SiteOutlines.Min(edge => Math.Min(edge.Y1, edge.Y2)), 6);
    }

    [Theory]
    [InlineData(-0.5, 3)]
    [InlineData(0.5, 0)]
    [InlineData(2.0, 3)]
    public void Collect_PreservesOnlyTheUncoveredEdgesOfAPlacedPolygonMesh(double coverDepth, int edgeCount)
    {
        var view = new Element
        {
            Id = 900, ViewOrigin = [0, 0, 0], ViewDirection = [0, 1, 0],
            ViewHorizontalDirection = [1, 0, 0], ViewVerticalDirection = [0, 0, 1],
        };
        var mesh = QuadWall(600, [[0, 1, 0], [2, 1, 0], [1, 1, 2]],
            [0, -1, 0], (int)BuiltInCategory.OST_Entourage);
        mesh.SerializedOwnElementClassId = 1794;
        mesh.Geometry!.Faces[0].PolygonMesh = true;
        var cover = QuadWall(601, [[-1, coverDepth, -1], [3, coverDepth, -1], [3, coverDepth, 3], [-1, coverDepth, 3]],
            [0, -1, 0]);
        var content = Assert.IsType<ElevationPrinter.ElevationContent>(
            ElevationPrinter.Collect(Document(view, mesh, cover), "south", planeView: view));
        if (coverDepth < 0)
        {
            Assert.Empty(content.Faces);
            Assert.Equal(2 * FeetToMillimetres, content.MaxX - content.MinX, 6);
        }
        Assert.Equal(edgeCount, content.SiteOutlines!.Count);
    }

    [Fact]
    public void Collect_OutlinesBackFacingEntourageWithoutTheMeshDiagonal()
    {
        var view = new Element
        {
            Id = 900, ViewOrigin = [0, 0, 0], ViewDirection = [0, 1, 0],
            ViewHorizontalDirection = [1, 0, 0], ViewVerticalDirection = [0, 0, 1],
        };
        var mesh = QuadWall(600, [[0, 1, 0], [2, 1, 2], [2, 1, 0]], [0, 1, 0],
            (int)BuiltInCategory.OST_Entourage);
        mesh.SerializedOwnElementClassId = 1794;
        mesh.Geometry!.Faces[0].PolygonMesh = true;
        mesh.Geometry.Faces.Add(new GeoFace
        {
            OuterVertices = [[0, 1, 0], [0, 1, 2], [2, 1, 2]],
            Normal = [0, 1, 0], PolygonMesh = true,
        });
        var boundary = ElevationPrinter.Collect(Document(view, mesh), "south", planeView: view)!.SiteOutlines!;

        Assert.Equal(4, boundary.Count);
        Assert.All(boundary, edge => Assert.True(edge.X1 == edge.X2 || edge.Y1 == edge.Y2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Collect_OutlinesOnlyCrossedEntourageBillboards(bool extraPlane)
    {
        var view = new Element
        {
            Id = 900, ViewOrigin = [0, 0, 0], ViewDirection = [0, 1, 0],
            ViewHorizontalDirection = [1, 0, 0], ViewVerticalDirection = [0, 0, 1],
        };
        var mesh = QuadWall(600, [[0, 2, 0], [2, 2, 0], [2, 2, 2]], [0, -1, 0],
            (int)BuiltInCategory.OST_Entourage);
        mesh.SerializedOwnElementClassId = 1794;
        var faces = mesh.Geometry!.Faces;
        faces[0].PolygonMesh = true;
        faces.Add(new GeoFace
        {
            OuterVertices = [[0, 2, 0], [2, 2, 2], [0, 2, 2]], Normal = [0, -1, 0], PolygonMesh = true,
        });
        faces.Add(new GeoFace
        {
            OuterVertices = [[1, 1, 0], [1, 3, 0], [1, 2, 2]], Normal = [1, 0, 0], PolygonMesh = true,
        });
        faces.Add(new GeoFace
        {
            OuterVertices = [[0, 2, 0], [1, 1, 0], [2, 2, 0]], Normal = [0, 0, 1], PolygonMesh = true,
        });
        if (extraPlane)
            faces.Add(new GeoFace
            {
                OuterVertices = [[3, 3, 0], [4, 3, 0], [3, 3, 1]], Normal = [0, -1, 0], PolygonMesh = true,
            });

        var edges = ElevationPrinter.Collect(Document(view, mesh), "south", planeView: view)!.SiteOutlines!;
        double end = 2 * FeetToMillimetres;
        bool hasDiagonal = edges.Any(edge =>
            edge.X1 == 0 && edge.Y1 == 0 && edge.X2 == end && edge.Y2 == end
            || edge.X2 == 0 && edge.Y2 == 0 && edge.X1 == end && edge.Y1 == end);
        Assert.Equal(extraPlane, hasDiagonal);
        if (!extraPlane) Assert.Equal(4, edges.Count);
    }

    [Theory]
    [InlineData(2, true, true)]
    [InlineData(2, false, false)]
    [InlineData(3.1, true, false)]
    public void Collect_ProjectsAnIntersectedCrossedBillboardWholeInAnOpaqueSection(
        double planeY, bool opaque, bool projected)
    {
        var view = new Element
        {
            Id = 900, ViewOrigin = [0, planeY, 0], ViewDirection = [0, 1, 0],
            ViewHorizontalDirection = [1, 0, 0], ViewVerticalDirection = [0, 0, 1],
        };
        var figure = QuadWall(600, [[-2, 0, 0], [1, 3, 0], [1, 3, 2]], [1, -1, 0],
            (int)BuiltInCategory.OST_Entourage);
        figure.SerializedOwnElementClassId = 1794;
        var facets = figure.Geometry!.Faces;
        facets[0].PolygonMesh = true;
        facets.AddRange([
            new() { OuterVertices = [[-2, 0, 0], [1, 3, 2], [-2, 0, 2]], Normal = [1, -1, 0], PolygonMesh = true },
            new() { OuterVertices = [[-1, 3, 0], [1, 1, 0], [1, 1, 2.5]], Normal = [-1, -1, 0], PolygonMesh = true },
            new() { OuterVertices = [[-1, 3, 0], [1, 1, 2.5], [-1, 3, 2.5]], Normal = [-1, -1, 0], PolygonMesh = true },
        ]);
        var rail = QuadWall(601, [[-3, 4, 1], [3, 4, 1], [3, 4, 1.2], [-3, 4, 1.2]],
            [0, -1, 0], (int)BuiltInCategory.OST_StairsRailing);
        var document = Document(view, figure, rail);
        if (opaque) document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };

        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;

        if (!projected)
        {
            Assert.Null(content.SectionBillboards);
            Assert.Contains(content.Edges, edge => edge.Y1 == FeetToMillimetres && edge.Y2 == edge.Y1
                && Math.Abs(edge.X2 - edge.X1) > 5 * FeetToMillimetres);
            return;
        }
        Assert.Equal(-2 * FeetToMillimetres, content.SectionBillboards!.SelectMany(ring => ring).Min(p => p.X), 6);
        Assert.Contains(content.SiteOutlines!, edge => edge.X1 == -2 * FeetToMillimetres && edge.X2 == edge.X1);
        Assert.Contains(content.SiteOutlines!, edge => edge.Y1 == 2 * FeetToMillimetres && edge.Y2 == edge.Y1
            && Math.Min(edge.X1, edge.X2) < -FeetToMillimetres && Math.Abs(Math.Max(edge.X1, edge.X2)) < 1e-6);
        var visibleRail = content.Edges.Where(edge => edge.Y1 == FeetToMillimetres && edge.Y2 == edge.Y1)
            .Select(edge => (Left: Math.Round(Math.Min(edge.X1, edge.X2) / FeetToMillimetres, 6),
                Right: Math.Round(Math.Max(edge.X1, edge.X2) / FeetToMillimetres, 6))).OrderBy(edge => edge.Left).ToArray();
        Assert.Equal(new[] { (-3d, -2d), (1d, 3d) }, visibleRail);
    }

    [Theory]
    [InlineData(8, true)]
    [InlineData(0, false)]
    public void Collect_LightsCrossedPlantPanelsInDepthOrder(int hour, bool shaded)
    {
        var view = new Element
        {
            Id = 900, ViewOrigin = [0, 0, 0], ViewDirection = [0, 1, 0],
            ViewHorizontalDirection = [1, 0, 0], ViewVerticalDirection = [0, 0, 1],
            ViewCrop = new ViewCropFrame(0, 0, 0, [1, 0, 0], [0, 0, 1], [0, 1, 0],
                -10, -5, 10, 10, true),
        };
        var tree = QuadWall(600, [[-4, 2, 0], [4, 8, 0], [0, 5, 4]], [0.6, -0.8, 0],
            (int)BuiltInCategory.OST_Planting);
        tree.Geometry!.Faces[0].PolygonMesh = true;
        tree.Geometry.Faces.Add(new GeoFace
        {
            OuterVertices = [[-3, 9, 0], [3, 1, 0], [0, 5, 4]],
            Normal = [0.8, 0.6, 0],
            PolygonMesh = true,
        });
        var wall = QuadWall(601, [[-4, 10, 0], [4, 10, 0], [4, 10, 4], [-4, 10, 4]], [0, -1, 0]);
        var document = Document(view, tree, wall);
        var raw = document.RawData;
        raw.OpaquePlanViews = new HashSet<int> { view.Id };
        raw.OpaqueViewsWithSurfacePatterns = raw.OpaquePlanViews;
        raw.ViewAmbientFractions = new Dictionary<int, double> { [view.Id] = 0.5 };
        uint time = (uint)(2024 << 20 | 3 << 16 | 20 << 11 | hour << 6);
        raw.ViewStillSunDates = new Dictionary<int, uint> { [view.Id] = time };
        raw.SharedTransformIsActiveLocation = true;
        raw.SharedTransform = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0];
        raw.SiteLatitudeRadians = 0;
        raw.SiteLongitudeRadians = 0;
        raw.SiteTimeZoneHours = 0;

        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;

        Assert.Equal(2, content.SiteSilhouettes!.Count);
        Assert.Null(content.CastShadows);
        if (!shaded)
        {
            Assert.Null(content.PlantTones);
            return;
        }

        var tones = content.PlantTones!.OrderBy(pair => pair.Key).ToArray();
        Assert.Equal(2, tones.Length);
        const double squareFoot = FeetToMillimetres * FeetToMillimetres;
        Assert.Equal(10, tones[0].Value.Sum(PlanPrinter.SignedArea) / squareFoot, 3);
        Assert.Equal(6, tones[1].Value.Sum(PlanPrinter.SignedArea) / squareFoot, 3);
        Assert.All(tones[1].Value.SelectMany(ring => ring), point => Assert.True(point.X >= -0.1));
    }

    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(true, 0.8, false)]
    [InlineData(false, 0, false)]
    public void Collect_ProjectsShadowsFromAPlacedOpaqueStructuralColumn(
        bool placed, double transparency, bool castsShadow)
    {
        var view = new Element
        {
            Id = 900, ViewOrigin = [0, 0, 0], ViewDirection = [0, 1, 0],
            ViewHorizontalDirection = [1, 0, 0], ViewVerticalDirection = [0, 0, 1],
        };
        var wall = QuadWall(600, [[-3, 2, 0], [3, 2, 0], [3, 2, 6], [-3, 2, 6]], [0, -1, 0]);
        Assert.Single(wall.Geometry!.Faces).PlaneSurface = true;
        var column = QuadWall(601, [[0, 0.5, 2], [0, 1.5, 2], [0, 1.5, 5], [0, 0.5, 5]], [1, 0, 0],
            (int)BuiltInCategory.OST_StructuralColumns);
        if (!placed) column.SerializedUnplacedOwnerId = 700;
        var material = new Element { Id = 602, SerializedMaterialTransparency = transparency };
        var leg = Assert.Single(column.Geometry!.Faces);
        leg.PlaneSurface = true;
        leg.RenderStyleId = material.Id;
        var document = Document(view, wall, column, material);
        document.RawData.ViewGroundShadows = new Dictionary<int, (double, double, int, int)>
        {
            [view.Id] = (3 * Math.PI / 4, Math.Atan(Math.Sqrt(0.5)), 1, 60),
        };

        var content = ElevationPrinter.Collect(document, "south", planeView: view)!;
        if (!castsShadow)
        {
            Assert.Null(content.CastShadows);
            return;
        }

        var (receiver, shadow) = Assert.Single(content.CastShadows!);
        Assert.Same(Assert.Single(content.Faces).Points, receiver);
        Assert.Equal(0.4, shadow.Light, 6);
        var ring = Assert.Single(shadow.Rings);
        Assert.Equal(-1.5 * FeetToMillimetres, ring.Min(point => point.X), 6);
        Assert.Equal(-0.5 * FeetToMillimetres, ring.Max(point => point.X), 6);
    }

    [Theory]
    [InlineData(0.5, true)]
    [InlineData(-2, false)]
    [InlineData(0.5, true, true)]
    [InlineData(0.5, false, true, true)]
    [InlineData(0.5, false, true, false, false)]
    [InlineData(-2, true, true)]
    public void Collect_ProjectsShadowsOntoObliqueModelFaces(
        double slope, bool receivesShadow, bool foundationFacet = false, bool warped = false, bool verifiedCaster = true)
    {
        var view = new Element
        {
            Id = 900, ViewOrigin = [0, 0, 0], ViewDirection = [0, 1, 0],
            ViewHorizontalDirection = [1, 0, 0], ViewVerticalDirection = [0, 0, 1],
        };
        const double left = -2, right = 1;
        double normalLength = Math.Sqrt(1 + slope * slope);
        var surface = QuadWall(600,
            [[left, 3 + slope * left, 0], [right, 3 + slope * right, 0],
                [right, 3 + slope * right, 6], [left, 3 + slope * left + (warped ? 0.1 : 0), 6]],
            [slope / normalLength, -1 / normalLength, 0],
            (int)(foundationFacet ? BuiltInCategory.OST_StructuralFoundation : BuiltInCategory.OST_Walls));
        var face = Assert.Single(surface.Geometry!.Faces);
        face.PlaneSurface = !foundationFacet;
        if (foundationFacet) face.MeshMarker = 1;
        double castX = slope < 0 ? 3 : 0;
        var column = QuadWall(601, [[castX, 0.5, 2], [castX, 1.5, 2], [castX, 1.5, 5], [castX, 0.5, 5]], [1, 0, 0],
            (int)BuiltInCategory.OST_StructuralColumns);
        var material = new Element
        {
            Id = 602, HasShadingAppearance = true, SerializedMaterialUsesRenderAppearance = !verifiedCaster,
            SerializedMaterialTransparency = 0,
        };
        var caster = Assert.Single(column.Geometry!.Faces);
        caster.PlaneSurface = true;
        caster.RenderStyleId = material.Id;
        var document = Document(view, surface, column, material);
        document.RawData.ViewGroundShadows = new Dictionary<int, (double, double, int, int)>
        {
            [view.Id] = (3 * Math.PI / 4, Math.Atan(Math.Sqrt(0.5)), 1, 60),
        };

        var content = ElevationPrinter.Collect(document, "south", planeView: view)!;
        if (!receivesShadow)
        {
            Assert.Null(content.CastShadows);
            return;
        }

        var (receiver, shadow) = Assert.Single(content.CastShadows!);
        Assert.Same(Assert.Single(content.Faces).Points, receiver);
        var ring = Assert.Single(shadow.Rings);
        Assert.Equal((slope < 0 ? -1.5 : -5.0 / 3) * FeetToMillimetres, ring.Min(point => point.X), 6);
        Assert.Equal((slope < 0 ? -0.5 : -1) * FeetToMillimetres, ring.Max(point => point.X), 6);
    }

    [Fact]
    public void Collect_ShadesOnlyTheVisiblePlantFacetsFromInViewCasters()
    {
        var view = new Element
        {
            Id = 900, ViewOrigin = [0, 0, 0], ViewDirection = [0, 1, 0],
            ViewHorizontalDirection = [1, 0, 0], ViewVerticalDirection = [0, 0, 1],
        };
        int planting = (int)BuiltInCategory.OST_Planting;
        var back = QuadWall(600, [[0, 2, 0], [3, 2, 0], [0, 2, 3]], [0, -1, 0], planting);
        var front = QuadWall(601, [[0, 1, 0], [1, 1, 0], [0, 1, 1]], [0, -1, 0], planting);
        var caster = QuadWall(602, [[1.5, 0.5, 1.5], [3.5, 0.5, 1.5], [1.5, 0.5, 3.5]], [0, -1, 0], planting);
        var outside = QuadWall(603, [[4, -1, 4], [5, -1, 4], [4, -1, 5]], [0, -1, 0], planting);
        var document = Document(view, back, front, caster, outside, WallWithFace(604));
        document.RawData.ViewGroundShadows = new Dictionary<int, (double, double, int, int)>
        {
            [view.Id] = (3 * Math.PI / 4, Math.Atan(Math.Sqrt(0.5)), 1, 60),
        };

        var content = ElevationPrinter.Collect(document, "south", planeView: view)!;
        Assert.Equal(3, content.SiteSilhouettes!.Count);
        var (receiver, shadow) = Assert.Single(content.CastShadows!);
        Assert.Same(content.SiteSilhouettes[0], receiver);
        Assert.Equal(1.5 * FeetToMillimetres * FeetToMillimetres,
            shadow.Rings.Sum(PlanPrinter.SignedArea), 5);
    }

    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(true, 0.8, false)]
    [InlineData(false, 0, false)]
    public void Collect_ProjectsOpaqueSkylightShadowsOntoTheirSlopedRoof(bool hosted, double transparency, bool castsShadow)
    {
        var view = new Element
        {
            Id = 900, ViewOrigin = [0, 0, 0], ViewDirection = [0, 1, 0],
            ViewHorizontalDirection = [1, 0, 0], ViewVerticalDirection = [0, 0, 1],
        };
        var roof = QuadWall(600, [[0, 2, 1], [6, 2, 1], [6, 6, 3], [0, 6, 3]],
            [0, -1 / Math.Sqrt(5), 2 / Math.Sqrt(5)], (int)BuiltInCategory.OST_Roofs);
        var slope = Assert.Single(roof.Geometry!.Faces);
        slope.PlaneSurface = true;
        slope.InnerVertices = [[[4, 3, 1.5], [5, 3, 1.5], [5, 4, 2], [4, 4, 2]]];
        var material = new Element { Id = 601, SerializedMaterialTransparency = transparency };
        var skylight = QuadWall(602, [[3, 1, 3], [4, 1, 3], [4, 1, 4], [3, 1, 4]],
            [0, -1, 0], (int)BuiltInCategory.OST_Windows);
        if (hosted) skylight.SerializedHostId = roof.Id;
        var frame = Assert.Single(skylight.Geometry!.Faces);
        frame.PlaneSurface = true;
        frame.RenderStyleId = material.Id;
        var document = Document(view, roof, skylight, material);
        document.RawData.ViewGroundShadows = new Dictionary<int, (double, double, int, int)>
        {
            [view.Id] = (3 * Math.PI / 4, Math.Atan(Math.Sqrt(0.5)), 1, 60),
        };

        var content = ElevationPrinter.Collect(document, "south", planeView: view)!;
        if (!castsShadow)
        {
            Assert.Null(content.CastShadows);
            return;
        }

        var shadow = Assert.Single(content.CastShadows!).Value;
        Assert.Single(shadow.Openings);
        var ring = Assert.Single(shadow.Rings);
        Assert.Equal(2d / 3 * FeetToMillimetres, ring.Min(point => point.X), 6);
        Assert.Equal(7d / 3 * FeetToMillimetres, ring.Max(point => point.X), 6);
        Assert.Equal(4d / 3 * FeetToMillimetres, ring.Min(point => point.Y), 6);
        Assert.Equal(5d / 3 * FeetToMillimetres, ring.Max(point => point.Y), 6);
    }

    [Fact]
    public void Collect_SectionClipsPlacedSiteContextAndOmitsSymbolGeometry()
    {
        var view = SectionView(900, [0, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 50);
        int planting = (int)BuiltInCategory.OST_Planting;
        var placed = QuadWall(600, [[1, 0, 0], [-1, 4, 0], [-1, 4, 4], [1, 0, 4]],
            [2, 1, 0], planting);
        var symbol = QuadWall(601, [[-1, 0, 0], [-1, 4, 0], [-1, 4, 4], [-1, 0, 4]],
            [1, 0, 0], planting);
        symbol.NativeIsSymbol = true;
        var wall = QuadWall(602, [[-2, 0, 0], [-2, 4, 0], [-2, 4, 4], [-2, 0, 4]], [1, 0, 0]);
        var document = Document(view, placed, symbol, wall);

        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;
        var context = Assert.Single(content.Context);
        Assert.Equal(2 * FeetToMillimetres, context.Points.Min(p => p.X), 6);
        Assert.Equal(4 * FeetToMillimetres, context.Points.Max(p => p.X), 6);
    }

    [Fact]
    public void Collect_PaintsSmallTerrainFacetOnlyPastTheSectionPlane()
    {
        var view = new Element
        {
            Id = 900, ViewOrigin = [0, 0, 0], ViewDirection = [0, 1, 0],
            ViewHorizontalDirection = [1, 0, 0], ViewVerticalDirection = [0, 0, 1],
            ViewCrop = new ViewCropFrame(0, 0, 0, [1, 0, 0], [0, 0, 1], [0, 1, 0],
                -5, -1, 5, 5, true),
        };
        var wall = QuadWall(601, [[-4, 3, 0], [4, 3, 0], [4, 3, 4], [-4, 3, 4]], [0, -1, 0]);
        int category = (int)BuiltInCategory.OST_Topography;
        var terrain = QuadWall(602, [[0, -1, 0], [2, -1, 0], [0, 1, 2]], [0, -1, 1], category);
        var foreground = QuadWall(603, [[-3, -2, 0], [-1, -2, 0], [-3, -1, 2]], [0, -2, 1], category);
        var document = Document(view, wall, terrain, foreground);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        document.RawData.OpaqueViewsWithSurfacePatterns = document.RawData.OpaquePlanViews;

        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;
        var painted = Assert.Single(content.Context);
        Assert.True(painted.Terrain);
        Assert.Equal(FeetToMillimetres, painted.Points.Min(p => p.Y), 6);
        Assert.Equal(2 * FeetToMillimetres, painted.Points.Max(p => p.Y), 6);
        Assert.Equal(0.5 * FeetToMillimetres * FeetToMillimetres,
            Math.Abs(PlanPrinter.SignedArea(painted.Points)), 6);
    }

    [Fact]
    public void Collect_InLitElevationPlantingShowsThroughNearerOpeningAndInFrontOfBackdrop()
    {
        var view = new Element
        {
            Id = 900, ViewOrigin = [0, 0, 0], ViewDirection = [0, 1, 0],
            ViewHorizontalDirection = [1, 0, 0], ViewVerticalDirection = [0, 0, 1],
        };
        var tree = QuadWall(600, [[0, 2, 0], [4, 2, 0], [0, 2, 4]], [0, -1, 0],
            (int)BuiltInCategory.OST_Planting);
        var screen = QuadWall(601, [[0, 1, 0], [2, 1, 0], [2, 1, 2], [0, 1, 2]], [0, -1, 0]);
        var face = screen.Geometry!.Faces[0];
        face.PlaneSurface = true;
        face.InnerVertices = [[[0.5, 1, 0.5], [1.5, 1, 0.5], [1.5, 1, 1.5], [0.5, 1, 1.5]]];
        var backdrop = QuadWall(602, [[0, 5, 0], [4, 5, 0], [4, 5, 4], [0, 5, 4]], [0, -1, 0]);
        var document = Document(view, tree, screen, backdrop);
        document.RawData.ViewGroundShadows = new Dictionary<int, (double, double, int, int)>
        {
            [view.Id] = (3 * Math.PI / 4, Math.Atan(Math.Sqrt(0.5)), 1, 60),
        };

        var content = ElevationPrinter.Collect(document, "south", planeView: view)!;
        var planting = Assert.IsType<ElevationPrinter.VisibleSiteFacets>(content.ForegroundSite);
        Assert.Same(Assert.Single(content.SiteSilhouettes!), Assert.Single(planting.Sources));
        Assert.Equal(5 * FeetToMillimetres * FeetToMillimetres,
            planting.Rings.Sum(PlanPrinter.SignedArea), 5);
    }

    [Fact]
    public void Collect_ProjectsAnOffscreenTreeShadowOntoSlopingTerrain()
    {
        var view = new Element
        {
            Id = 900, ViewOrigin = [0, 0.5, 0], ViewDirection = [0, 1, 0],
            ViewHorizontalDirection = [1, 0, 0], ViewVerticalDirection = [0, 0, 1],
        };
        var terrain = QuadWall(600, [[-1, 1, 0.5], [4, 1, 0.5], [4, 6, 3], [-1, 6, 3]],
            [0, -0.5, 1], (int)BuiltInCategory.OST_Topography);
        terrain.Geometry!.Faces[0].PlaneSurface = true;
        var tree = QuadWall(601, [[2, 0, 2], [3, 0, 2], [3, 0, 3], [2, 0, 3]],
            [0, -1, 0], (int)BuiltInCategory.OST_Planting);
        tree.Geometry!.Faces[0].PlaneSurface = true;
        var backdrop = QuadWall(602, [[-2, 4, -1], [5, 4, -1], [5, 4, 4], [-2, 4, 4]], [0, -1, 0]);
        var document = Document(view, terrain, tree, backdrop);
        document.RawData.ViewGroundShadows = new Dictionary<int, (double, double, int, int)>
        {
            [view.Id] = (3 * Math.PI / 4, Math.Atan(Math.Sqrt(0.5)), 1, 60),
        };

        var content = ElevationPrinter.Collect(document, "south", planeView: view)!;
        Assert.Null(content.SiteSilhouettes);
        var (receiver, shadow) = Assert.Single(content.CastShadows!);
        Assert.Same(Assert.Single(content.Context).Points, receiver);
        var ring = Assert.Single(shadow.Rings);
        Assert.Equal(0, ring.Min(point => point.X), 6);
        Assert.Equal(5.0 / 3 * FeetToMillimetres, ring.Max(point => point.X), 6);
        Assert.Equal(2.0 / 3 * FeetToMillimetres, ring.Min(point => point.Y), 6);
        Assert.Equal(FeetToMillimetres, ring.Max(point => point.Y), 6);
    }

    [Theory]
    [InlineData(true, -0.5, false)]
    [InlineData(true, 0.5, true)]
    [InlineData(false, -0.5, true)]
    public void Collect_UsesTheRpcInsertionDepthForVisibleShadows(bool rpc, double insertionDepth, bool shadesWall)
    {
        const int planting = (int)BuiltInCategory.OST_Planting;
        var view = new Element
        {
            Id = 900, ViewOrigin = [0, 0, 0], ViewDirection = [0, 1, 0],
            ViewHorizontalDirection = [1, 0, 0], ViewVerticalDirection = [0, 0, 1],
        };
        var terrain = QuadWall(600, [[-0.5, 1, 0], [4.5, 1, 0], [4.5, 6, 3], [-0.5, 6, 3]],
            [0, -0.6, 1], (int)BuiltInCategory.OST_Topography);
        var tree = QuadWall(601, [[2, -1, 2], [3, 1, 2], [3, 1, 3], [2, -1, 3]], [2, -1, 0], planting);
        tree.InstanceOriginXY = (2.5, insertionDepth);
        tree.NativeRootClassName = "FamilyInstance";
        var type = new Element { Id = 903, CategoryId = planting, NativeIsSymbol = true };
        if (rpc) Add(type, BuiltInParameter.RENDER_RPC_FILENAME, StorageType.String, "RPC tree");
        tree.TypeId = type.Id;
        var wall = QuadWall(602, [[0, 2, -1], [4, 2, -1], [4, 2, 4], [0, 2, 4]], [0, -1, 0]);
        foreach (var element in new[] { terrain, tree, wall }) element.Geometry!.Faces[0].PlaneSurface = true;
        var document = Document(view, terrain, tree, wall, type);
        document.RawData.ViewGroundShadows = new Dictionary<int, (double, double, int, int)>
        {
            [view.Id] = (3 * Math.PI / 4, Math.Atan(Math.Sqrt(0.5)), 1, 60),
        };

        var content = ElevationPrinter.Collect(document, "south", planeView: view)!;
        var shadows = Assert.IsType<Dictionary<List<(double X, double Y)>, ElevationPrinter.CastShadow>>(content.CastShadows);
        Assert.Equal(shadesWall, shadows.ContainsKey(Assert.Single(content.Faces).Points));
        Assert.True(shadows.ContainsKey(Assert.Single(content.Context).Points));
        Assert.Single(content.SiteSilhouettes!);
    }

    [Theory]
    [InlineData(-12, true, BuiltInCategory.OST_Walls)]
    [InlineData(-0.5, false, BuiltInCategory.OST_Walls)]
    [InlineData(-12, true, BuiltInCategory.OST_Planting)]
    [InlineData(-0.5, false, BuiltInCategory.OST_Planting)]
    [InlineData(-12, true, BuiltInCategory.OST_StairsRailing)]
    [InlineData(-0.5, false, BuiltInCategory.OST_StairsRailing)]
    [InlineData(-12, true, BuiltInCategory.OST_RailingTopRail)]
    public void Collect_ProjectsTheStoredSunOntoTheFrontWallThroughCasterOpenings(double viewDepth, bool casterVisible,
        BuiltInCategory casterCategory)
    {
        var view = new Element
        {
            Id = 900, ViewOrigin = [0, viewDepth, 0], ViewDirection = [0, 1, 0],
            ViewHorizontalDirection = [1, 0, 0], ViewVerticalDirection = [0, 0, 1],
        };
        var receiver = QuadWall(600, [[0, 0, 0], [4, 0, 0], [4, 0, 3], [0, 0, 3]], [0, -1, 0]);
        var caster = QuadWall(601, [[2, -1, 2], [3, -1, 2], [3, -1, 3], [2, -1, 3]], [0, -1, 0],
            (int)casterCategory);
        var behind = QuadWall(602, [[0, 1, 0], [4, 1, 0], [4, 1, 3], [0, 1, 3]], [0, 1, 0]);
        foreach (var wall in new[] { receiver, caster, behind }) wall.Geometry!.Faces[0].PlaneSurface = true;
        caster.Geometry!.Faces[0].InnerVertices =
            [[[2.25, -1, 2.25], [2.75, -1, 2.25], [2.75, -1, 2.75], [2.25, -1, 2.75]]];
        var document = Document(view, receiver, caster, behind);

        Assert.Null(ElevationPrinter.Collect(document, "south", planeView: view)!.CastShadows);
        document.RawData.ViewGroundShadows = new Dictionary<int, (double, double, int, int)>
        {
            [view.Id] = (3 * Math.PI / 4, Math.Atan(Math.Sqrt(0.5)), 1, 60),
        };
        var content = ElevationPrinter.Collect(document, "south", planeView: view)!;
        if (!casterVisible)
        {
            Assert.Equal(2, content.Faces.Count);
            Assert.Null(content.CastShadows);
            return;
        }
        var shadow = Assert.Single(content.CastShadows!).Value;

        Assert.Equal(0.4, shadow.Light, 8);
        Assert.Equal(2, shadow.Rings.Count);
        Assert.Equal(FeetToMillimetres, shadow.Rings[0].Min(p => p.X), 6);
        Assert.Equal(2 * FeetToMillimetres, shadow.Rings[0].Max(p => p.Y), 6);
        Assert.Equal(1.25 * FeetToMillimetres, shadow.Rings[1].Min(p => p.X), 6);
        Assert.True(PlanPrinter.SignedArea(shadow.Rings[0]) > 0);
        Assert.True(PlanPrinter.SignedArea(shadow.Rings[1]) < 0);

        var terrain = new ElevationPrinter.TerrainHatch(0, 0, -100,
            [new(0, 600, 1200, 700, 0, 0, -1, -1, 0, 0, 0, 0)], []);
        var pdf = new PdfDocument();
        var page = pdf.AddPage(PdfPageSize.A1.Landscape);
        ElevationPrinter.DrawContent(page, content with { GroundHatch = terrain }, 100, 10, 10);
        Assert.Equal(2, CountOccurrences(PdfTestContent.Read(pdf), "W* n"));
    }

    [Theory]
    [InlineData(8, 0.0, 1)]
    [InlineData(6, 0.0, 0)]
    [InlineData(8, 0.8, 0)]
    public void Collect_ClipsSlopingRailingSpansBehindOpaqueOverviewWalls(double wallX, double transparency, double start)
    {
        var view = SectionView(900, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 100);
        view.ViewCrop = new ViewCropFrame(10, 0, 0, [0, 1, 0], [0, 0, 1], [-1, 0, 0], -3, -3, 6, 7, true);
        var wall = QuadWall(901, [[wallX, -1, -1], [wallX, 1, -1], [wallX, 1, 3], [wallX, -1, 3]], [1, 0, 0]);
        wall.TypeId = 902;
        var material = new Element { Id = 903, SerializedMaterialTransparency = transparency };
        var wallFace = Assert.Single(wall.Geometry!.Faces);
        wallFace.PlaneSurface = true;
        wallFace.RenderStyleId = material.Id;
        const int railingCategory = (int)BuiltInCategory.OST_StairsRailing;
        var rail = QuadWall(904, [[7, 0, 0], [7, 4, 4], [7, 4, 4.5], [7, 0, 0.5]], [1, 0, 0], railingCategory);
        var document = Document(view, wall, material, rail);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        document.RawData.OpaqueViewsWithSurfacePatterns = new HashSet<int> { view.Id };

        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;

        var slopes = content.Edges.Where(edge => edge.CategoryId == railingCategory
            && Math.Abs(edge.X1 - edge.X2) > 1e-6 && Math.Abs(edge.Y1 - edge.Y2) > 1e-6).ToArray();
        Assert.Equal(2, slopes.Length);
        Assert.All(slopes, edge =>
        {
            Assert.Equal(start * FeetToMillimetres, Math.Min(edge.X1, edge.X2), 6);
            Assert.Equal(4 * FeetToMillimetres, Math.Max(edge.X1, edge.X2), 6);
        });
    }

    [Theory]
    [InlineData("south", false)]
    [InlineData("section", false)]
    [InlineData("section", true)]
    public void Collect_OnlyOutlinesTheVisibleSpansOfAPlacedPlant(string direction, bool treeCover)
    {
        const int planting = (int)BuiltInCategory.OST_Planting;
        var view = new Element
        {
            Id = 900, ViewOrigin = [0, 0, 0], ViewDirection = [0, 1, 0],
            ViewHorizontalDirection = [1, 0, 0], ViewVerticalDirection = [0, 0, 1],
        };
        var tree = Mesh(901, [[0, 4, 0], [2, 4, 0], [2, 4, 2], [0, 4, 2]], [0, -1, 0], planting);
        var cover = Mesh(902, [[1.5, 2, 0.5], [3, 2, 0.5], [3, 6, 1.5], [1.5, 6, 1.5]],
            [0, -1, 4], treeCover ? planting : (int)BuiltInCategory.OST_Walls);
        var wall = QuadWall(903, [[4, 4, 0], [5, 4, 0], [5, 4, 2], [4, 4, 2]], [0, -1, 0]);
        var sliced = Mesh(904, [[10, -1, 0], [12, 1, 0], [12, 1, 2], [10, -1, 2]], [1, -1, 0], planting);
        var document = Document(view, tree, cover, wall, sliced);
        document.RawData.OpaqueViewsWithSurfacePatterns = new HashSet<int> { view.Id };

        var content = ElevationPrinter.Collect(document, direction, planeView: view)!;
        var right = content.SiteOutlines!.Where(edge => edge.CategoryId == planting
                && Math.Abs(edge.X1 - 2 * FeetToMillimetres) < 1e-6 && Math.Abs(edge.X1 - edge.X2) < 1e-6)
            .Select(edge => (Start: Math.Min(edge.Y1, edge.Y2), End: Math.Max(edge.Y1, edge.Y2)))
            .OrderBy(span => span.Start).ToArray();

        Assert.Collection(right,
            span =>
            {
                Assert.Equal(0, span.Start, 6);
                Assert.Equal(0.5 * FeetToMillimetres, span.End, 6);
            },
            span =>
            {
                Assert.InRange(span.Start, FeetToMillimetres - 0.01, FeetToMillimetres);
                Assert.Equal(2 * FeetToMillimetres, span.End, 6);
            });
        Assert.DoesNotContain(content.SiteOutlines!, edge => edge.CategoryId == planting
            && Math.Min(edge.X1, edge.X2) == 0 && Math.Max(edge.X1, edge.X2) == 2 * FeetToMillimetres
            && edge.Y1 != edge.Y2);
        var slicedOutline = content.SiteOutlines!.Where(edge =>
            Math.Min(edge.X1, edge.X2) > 9 * FeetToMillimetres).ToArray();
        Assert.Equal(3, slicedOutline.Length);
        Assert.All(slicedOutline, edge => Assert.Equal(12 * FeetToMillimetres, Math.Max(edge.X1, edge.X2), 6));

        static Element Mesh(int id, double[][] vertices, double[] normal, int category)
        {
            var first = QuadWall(id, [vertices[0], vertices[1], vertices[2]], normal, category);
            var last = QuadWall(id, [vertices[0], vertices[2], vertices[3]], normal, category);
            first.Geometry!.Faces.Add(last.Geometry!.Faces[0]);
            foreach (var face in first.Geometry.Faces) face.PolygonMesh = true;
            return first;
        }
    }

    [Theory]
    [InlineData(3, true, true)]
    [InlineData(2, true, false)]
    [InlineData(3, false, false)]
    public void Collect_PatternedOverviewSectionDrawsTheClosedFloorLayers(int viewDetail, bool patterns, bool expected)
    {
        var view = SectionView(900, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 100);
        Add(view, BuiltInParameter.VIEW_DETAIL_LEVEL, StorageType.Integer, viewDetail);
        view.ViewCrop = new ViewCropFrame(10, 0, 0, [0, 1, 0], [0, 0, 1], [-1, 0, 0], -3, -1, 3, 2, true);
        const int floorCategory = (int)BuiltInCategory.OST_Floors;
        var floor = QuadWall(901, [[9, -2, 0], [11, -2, 0], [11, -2, 1], [9, -2, 1]], [0, -1, 0], floorCategory);
        floor.BoundingBox = [9, -2, 0, 11, 2, 1];
        floor.TypeId = 902;
        floor.Geometry!.Faces.Add(QuadWall(0, [[11, 2, 0], [9, 2, 0], [9, 2, 1], [11, 2, 1]], [0, 1, 0]).Geometry!.Faces[0]);
        floor.Geometry.Faces.Add(QuadWall(0, [[9, -2, 0], [9, 2, 0], [11, 2, 0], [11, -2, 0]], [0, 0, -1]).Geometry!.Faces[0]);
        floor.Geometry.Faces.Add(QuadWall(0, [[9, -2, 1], [11, -2, 1], [11, 2, 1], [9, 2, 1]], [0, 0, 1]).Geometry!.Faces[0]);
        var type = new Element
        {
            Id = 902,
            SerializedHostTypeLayers = [
                new(BitConverter.DoubleToInt64Bits(0.25), 1, -1, 903, -1, 0, 1),
                new(0, 100, -1, 904, -1, 1, 1),
                new(BitConverter.DoubleToInt64Bits(0.75), 1, -1, 905, -1, 2, 1),
            ],
        };
        var document = Document(view, floor, type);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        if (patterns) document.RawData.OpaqueViewsWithSurfacePatterns = new HashSet<int> { view.Id };
        var top = new TfBlockDeserializer.PatternGrid(0, 0, 0, 0, 0.01, [0.004, 0.01], 0);
        var bottom = top with { Angle = Math.PI / 3, Segments = [] };
        document.RawData.MaterialPatterns = new Dictionary<int, (int, int, int?)> { [903] = (906, -1, 0), [905] = (907, -1, 0) };
        document.RawData.FillPatterns = new Dictionary<int, (string, IReadOnlyList<TfBlockDeserializer.PatternGrid>)>
            { [906] = ("Top", [top]), [907] = ("Bottom", [bottom]) };

        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;
        var fills = content.LayerHatches ?? [];
        const double boundary = 0.75 * FeetToMillimetres;
        bool LayerEdge(ElevationPrinter.CollectedEdge edge) =>
            !edge.IsCut && edge.CategoryId == floorCategory && Math.Abs(edge.Y1 - boundary) < 1e-6
            && Math.Abs(edge.Y2 - boundary) < 1e-6;

        Assert.Equal(expected ? 2 : 0, fills.Count);
        Assert.Equal(expected ? 1 : 0, content.Edges.Count(LayerEdge));
        if (expected)
        {
            Assert.Same(top, Assert.Single(fills[0].Grids));
            Assert.Equal(boundary, fills[0].Polygon.Min(p => p.Y), 6);
            Assert.Same(bottom, Assert.Single(fills[1].Grids));
            Assert.Equal(boundary, fills[1].Polygon.Max(p => p.Y), 6);
            Assert.All(fills, fill => Assert.Equal(0.1, fill.PenWidth));
            var pdf = new PdfDocument();
            ElevationPrinter.DrawContent(pdf.AddPage(PdfPageSize.A1.Landscape), content, 100, 10, 10, 0.82,
                clipRect: (-900, -300, 900, 600));
            Assert.Contains("/Pattern cs", PdfTestContent.Read(pdf));
        }
    }

    [Theory]
    [InlineData("south", BuiltInCategory.OST_Walls, 1, true, 2)]
    [InlineData("south", BuiltInCategory.OST_Walls, 5, true, 0)]
    [InlineData("south", BuiltInCategory.OST_Walls, 1, false, 0)]
    [InlineData("section", BuiltInCategory.OST_BuildingPad, 1, true, 2)]
    [InlineData("section", BuiltInCategory.OST_BuildingPad, 5, true, 0)]
    [InlineData("section", BuiltInCategory.OST_BuildingPad, 1, false, 0)]
    [InlineData("section", BuiltInCategory.OST_Walls, 1, true, 2)]
    [InlineData("section", BuiltInCategory.OST_Walls, 5, true, 0)]
    [InlineData("section", BuiltInCategory.OST_StructuralFoundation, 1, true, 2)]
    [InlineData("section", BuiltInCategory.OST_StructuralFoundation, 1, false, 0)]
    [InlineData("section", BuiltInCategory.OST_Floors, 1, true, 0)]
    public void Collect_CroppedViewClipsSupportedProjectionOnlyBehindPlacedTerrain(string direction,
        BuiltInCategory modelCategory, double terrainDepth, bool placed, double firstVisibleX)
    {
        var view = new Element
        {
            Id = 900, ViewOrigin = [0, 0, 0], ViewDirection = [0, 1, 0],
            ViewHorizontalDirection = [1, 0, 0], ViewVerticalDirection = [0, 0, 1],
            ViewCrop = new ViewCropFrame(0, 0, 0, [1, 0, 0], [0, 0, 1], [0, 1, 0], -2, -1, 6, 6, true),
        };
        var model = QuadWall(601, [[0, 4, 1], [4, 4, 1], [4, 4, 5], [0, 4, 5]], [0, -1, 0], (int)modelCategory);
        var terrain = QuadWall(600,
            [[-1, terrainDepth, 0], [2, terrainDepth, 0], [2, terrainDepth + 2, 3], [-1, terrainDepth + 2, 3]],
            [0, -1.5, 1], (int)BuiltInCategory.OST_Topography);
        terrain.Geometry!.Faces[0].PlaneSurface = true;
        if (!placed) terrain.SerializedUnplacedOwnerId = 700;
        var document = Document(view, model, terrain);
        if (direction == "section")
        {
            document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
            document.RawData.OpaqueViewsWithSurfacePatterns = new HashSet<int> { view.Id };
        }
        else
            document.RawData.ViewGroundShadows = new Dictionary<int, (double, double, int, int)>
            {
                [view.Id] = (3 * Math.PI / 4, Math.Atan(Math.Sqrt(0.5)), 1, 60),
            };

        var content = ElevationPrinter.Collect(document, direction, planeView: view)!;

        var bottom = Assert.Single(content.Edges, edge => edge.CategoryId == (int)modelCategory
            && Math.Abs(edge.Y1 - FeetToMillimetres) < 1e-6 && Math.Abs(edge.Y2 - FeetToMillimetres) < 1e-6);
        Assert.Equal(firstVisibleX * FeetToMillimetres, Math.Min(bottom.X1, bottom.X2), 6);
        Assert.Equal(4 * FeetToMillimetres, Math.Max(bottom.X1, bottom.X2), 6);
        bool clipsFace = firstVisibleX > 0 && (direction != "section"
            || modelCategory is BuiltInCategory.OST_BuildingPad or BuiltInCategory.OST_StructuralFoundation);
        if (!clipsFace) Assert.Null(content.TerrainClippedFaces);
        else
        {
            var (face, visible) = Assert.Single(content.TerrainClippedFaces!);
            Assert.Same(Assert.Single(content.Faces).Points, face);
            Assert.Equal(12 * FeetToMillimetres * FeetToMillimetres, visible.Sum(ring => Math.Abs(PlanPrinter.SignedArea(ring))), 6);
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 60)]
    public void Project_ClipsTerrainShadowBehindNearerTerrain(bool foreground, double firstVisibleX)
    {
        var projector = new ElevationPrinter.ElevationShadows([1, 0, 0], [0, 0, 1], [0, 1, 0], 1, 1, 0.4);
        var face = new GeoFace { Normal = [0, -1, 0.25] };
        List<(double X, double Y)> receiver = [(0, 0), (100, 0), (100, 100), (0, 100)];
        projector.AddTerrainReceiver(receiver, face, [[0, 200, 0], [100, 200, 0], [100, 225, 100], [0, 225, 100]]);
        if (foreground)
            projector.AddTerrainReceiver([(0, 0), (60, 0), (60, 100), (0, 100)], face,
                [[0, 100, 0], [60, 100, 0], [60, 125, 100], [0, 125, 100]]);
        projector.AddCaster(new GeoFace(), [[50, 150, 50], [150, 150, 50], [175, 150, 175], [75, 150, 175]]);

        var shadow = Assert.Single(projector.Project(0)!);
        Assert.Same(receiver, shadow.Key);
        var ring = Assert.Single(shadow.Value.Rings);
        Assert.Equal(firstVisibleX, ring.Min(p => p.X), 6);
        Assert.Equal(100, ring.Max(p => p.X), 6);
        Assert.Equal((100 - firstVisibleX) * 100, PlanPrinter.SignedArea(ring), 6);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void Project_KeepsOffscreenRpcShadowsOnEligiblePlanting(
        bool wholeCasterOutside, bool rpcReceiver, bool shaded)
    {
        var projector = new ElevationPrinter.ElevationShadows([1, 0, 0], [0, 0, 1], [0, 1, 0], 0, 0, 0.4);
        var face = new GeoFace { Normal = [0, -1, 0] };
        List<(double X, double Y)> panel = [(0, 0), (100, 0), (0, 100)];
        projector.AddCaster(face, [[0, -50, 0], [100, -50, 0], [0, -50, 100]],
            planting: true, groundOnly: true, castsOnVisiblePlanting: wholeCasterOutside);
        projector.AddVisibleFacet(panel, face, [[0, 100, 0], [100, 100, 0], [0, 100, 100]],
            planting: true, receivesOffscreenPlantShadows: rpcReceiver);
        projector.AddReceiver([(0, 0), (100, 0), (0, 100)], face, 200);

        var shadows = projector.Project(0);

        if (shaded)
        {
            var result = Assert.Single(shadows!);
            Assert.Same(panel, result.Key);
            Assert.Equal(5000, result.Value.Rings.Sum(PlanPrinter.SignedArea), 6);
        }
        else
            Assert.Null(shadows);
    }

    [Theory]
    [InlineData(2, 1, true, false, 5000)]
    [InlineData(2, -1, true, false, 5000)]
    [InlineData(1, 1, true, false, 5000)]
    [InlineData(2, 1, true, true, 2500)]
    [InlineData(0, 1, true, false, 0)]
    [InlineData(2, 1, false, false, 0)]
    public void Project_ShadesOnlyTheVisibleUnlitBillboardFacet(double shift, int winding, bool billboard,
        bool covered, double area)
    {
        var projector = new ElevationPrinter.ElevationShadows([1, 0, 0], [0, 0, 1], [0, 1, 0], shift, 1, 0.4);
        List<(double X, double Y)> receiver = [(0, 0), (100, 0), (0, 100)];
        var face = new GeoFace { Normal = [-winding, -winding, 0] };
        projector.AddVisibleFacet(receiver, face, [[0, 200, 0], [100, 100, 0], [0, 200, 100]],
            twoSidedBillboard: billboard);
        if (covered)
            projector.AddVisibleFacet([(0, 0), (50, 0), (0, 100)], new GeoFace { Normal = [0, -1, 0] },
                [[0, 60, 0], [50, 60, 0], [0, 60, 100]]);

        var shadows = projector.Project(0);
        if (area == 0)
        {
            Assert.Null(shadows);
            return;
        }
        var shade = Assert.IsType<ElevationPrinter.CastShadow>(shadows?.GetValueOrDefault(receiver));
        Assert.Equal(0.4, shade.Light);
        Assert.Equal(area, shade.Rings.Sum(PlanPrinter.SignedArea), 6);
    }

    [Theory]
    [InlineData(false, 60, 2500)]
    [InlineData(false, 300, 5000)]
    [InlineData(true, 60, 2500)]
    public void ClipSiteFacetsToModel_KeepsBillboardFillInFrontOfModelAndBehindPlanting(bool planting,
        double coverDepth, double area)
    {
        var projector = new ElevationPrinter.ElevationShadows([1, 0, 0], [0, 0, 1], [0, 1, 0], 2, 1, 0.4);
        List<(double X, double Y)> receiver = [(0, 0), (100, 0), (0, 100)];
        var face = new GeoFace { Normal = [-1, -1, 0] };
        projector.AddVisibleFacet(receiver, face, [[0, 200, 0], [100, 100, 0], [0, 200, 100]],
            twoSidedBillboard: true);
        projector.AddModelOccluder(receiver, face.Normal, [0, 200, 0], 200, null);
        List<(double X, double Y)> cover = [(0, 0), (50, 0), (0, 100)];
        var coverFace = new GeoFace { Normal = [0, -1, 0] };
        if (planting)
            projector.AddVisibleFacet(cover, coverFace, [[0, coverDepth, 0], [50, coverDepth, 0], [0, coverDepth, 100]],
                planting: true);
        else
            projector.AddModelOccluder(cover, coverFace.Normal, [0, coverDepth, 0], coverDepth, null);

        var site = Assert.IsType<ElevationPrinter.VisibleSiteFacets>(
            projector.ClipSiteFacetsToModel(projector.Project(0)));
        Assert.Contains(receiver, site.Sources);
        Assert.Equal(area + (planting ? 2500 : 0), site.Rings.Sum(PlanPrinter.SignedArea), 6);
        Assert.Equal(area, site.Shade.Sum(PlanPrinter.SignedArea), 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    public void Project_ShadesTheUnlitSideOfARoundShaft(double shift)
    {
        var projector = new ElevationPrinter.ElevationShadows([1, 0, 0], [0, 0, 1], [0, 1, 0], shift, 1, 0.4);
        List<(double X, double Y)> face = [(-100, 0), (100, 0), (100, 300), (-100, 300)];
        projector.AddReceiver(face, new GeoFace { Normal = [0, -1, 0] }, 50, roundShaft: true);

        var shadows = projector.Project(0);
        if (shift == 0)
        {
            Assert.Null(shadows);
            return;
        }
        var shadow = Assert.Single(shadows!);
        Assert.Same(face, shadow.Key);
        var shade = Assert.Single(shadow.Value.Rings);
        double tangent = 100 / Math.Sqrt(2);
        Assert.Equal(shift > 0 ? -100 : tangent, shade.Min(p => p.X), 6);
        Assert.Equal(shift > 0 ? -tangent : 100, shade.Max(p => p.X), 6);
    }

    [Theory]
    [InlineData(true, -2, -1, true)]
    [InlineData(true, -2, 0, false)]
    [InlineData(true, -2, 1, false)]
    [InlineData(false, -2, -1, false)]
    public void HeaderOutlineIsBeforePlane_UsesTheSavedOutlineOnly(
        bool headerOutline, double minDepth, double maxDepth, bool beforeView)
    {
        var plant = new Element
        {
            BoundingBox = [0, minDepth, 0, 1, maxDepth, 1],
            BoundingBoxIsHeaderOutline = headerOutline,
        };

        Assert.Equal(beforeView, ElevationPrinter.HeaderOutlineIsBeforePlane(plant, [0, 1, 0], 0));
        Assert.Equal(beforeView, ElevationPrinter.HeaderOutlineIsBeforePlane(plant, [0, -1, 0],
            -(minDepth + maxDepth) * FeetToMillimetres));
    }

    [Theory]
    [InlineData(-1, true, false)]
    [InlineData(0, true, true)]
    [InlineData(1, true, true)]
    [InlineData(1, false, false)]
    public void Project_UsesEligiblePlantFacesDirectedIntoTheViewForTheSoilCut(
        int facing, bool castsOnSoilCut, bool shadesSoil)
    {
        var projector = new ElevationPrinter.ElevationShadows([1, 0, 0], [0, 0, 1], [0, 1, 0], 1, 1, 0.4);
        var soil = projector.AddSoilCutReceiver((-500, -500, 500, 500), 0, -200)!;
        List<(double X, double Y)> terrain = [(-500, -500), (500, -500), (500, 500), (-500, 500)];
        projector.AddTerrainReceiver(terrain, new GeoFace { Normal = [0, -1, 0] },
            [[-500, 200, -500], [500, 200, -500], [500, 200, 500], [-500, 200, 500]]);
        var normal = facing == 0 ? new[] { 1d, 0, 0 } : new[] { 0d, facing, 0 };
        List<double[]> corners = facing == 0
            ? [[0, -140, 120], [0, -60, 120], [0, -60, 180], [0, -140, 180]]
            : [[-40, -100, 120], [40, -100, 120], [40, -100, 180], [-40, -100, 180]];
        projector.AddCaster(new GeoFace { Normal = normal }, corners, siteOnly: true, planting: true,
            castsOnSoilCut: castsOnSoilCut);

        var shadows = Assert.IsType<Dictionary<List<(double X, double Y)>, ElevationPrinter.CastShadow>>(projector.Project(0));
        Assert.True(shadows.ContainsKey(terrain));
        Assert.Equal(shadesSoil, shadows.ContainsKey(soil));
    }

    [Fact]
    public void DrawContent_ProjectsForegroundShadowsOntoTheSoilCutUnderTheHatch()
    {
        var projector = ElevationPrinter.ElevationShadows.Create((3 * Math.PI / 4, Math.Atan(Math.Sqrt(0.5)), 1, 60),
            [1, 0, 0], [0, 0, 1], [0, 1, 0])!;
        var crop = (-500d, -200d, 500d, 300d);
        var receiver = projector.AddSoilCutReceiver(crop, 0, -100)!;
        projector.AddCaster(new GeoFace(), [[-40, -100, 120], [40, -100, 120], [40, -100, 180], [-40, -100, 180]],
            planting: true);
        projector.AddCaster(new GeoFace(), [[-40, 100, 120], [40, 100, 120], [40, 100, 180], [-40, 100, 180]],
            planting: true);
        projector.AddCaster(new GeoFace(), [[-80, -100, 120], [80, -100, 120], [80, -100, 180], [-80, -100, 180]]);
        var shadow = Assert.Single(projector.Project(0)!).Value;
        var ring = Assert.Single(shadow.Rings);
        Assert.Equal(-140, ring.Min(p => p.X), 6);
        Assert.Equal(-60, ring.Max(p => p.X), 6);
        Assert.Equal(20, ring.Min(p => p.Y), 6);
        Assert.Equal(80, ring.Max(p => p.Y), 6);
        Assert.Equal(-100, receiver.Min(p => p.Y), 6);
        Assert.Null(projector.AddSoilCutReceiver(crop, 0, 350));

        var terrain = new ElevationPrinter.TerrainHatch(0, 0, -100,
            [new(-300, 100, 300, 100, 0, 0, -1, -1, 0, 0, 0, 0)],
            [new(0, 0, 0, 0, 0.01, [0.01, 0.02])], Shadow: shadow);
        var content = ElevationPrinter.Collect(Document(WallWithFace(600)), "south")!;
        var page = new PdfDocument().AddPage(PdfPageSize.A4);
        ElevationPrinter.DrawContent(page, content with { GroundHatch = terrain }, 100, 10, 10, clipRect: crop);
        int fill = page.Content.IndexOf("0.4 g\n", StringComparison.Ordinal);
        Assert.True(fill > page.Content.IndexOf("W n", StringComparison.Ordinal) && page.Content.Contains("W n"));
        Assert.True(page.Content.IndexOf("/Pattern cs", StringComparison.Ordinal) > fill);
    }


    [Fact]
    public void Collect_ReturnsNullWithoutAFace()
    {
        Assert.Null(ElevationPrinter.Collect(Document(), "south"));
    }

    [Fact]
    public void Collect_IgnoresDiscoveredLightSourceConstructionFaces()
    {
        var helper = WallWithFace(17);
        helper.CategoryId = (int)BuiltInCategory.OST_LightingFixtureSource;
        helper.IsDiscovered = true;

        Assert.Null(ElevationPrinter.Collect(Document(helper), "south"));
    }

    [Theory]
    [InlineData(0, 4, 8)]
    [InlineData(1, 6, 4)]
    [InlineData(2, 0, 0)]
    public void Collect_SectionKeepsTheExposedNearEdgesOfALightingFixture(int cover, int edgeCount,
        double horizontalLength)
    {
        const int fixtureCategory = (int)BuiltInCategory.OST_LightingFixtures;
        var view = SectionView(900, [0, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 20);
        var fixture = QuadWall(601, [[-3, 0, 1], [-3, 4, 1], [-3, 4, 2], [-3, 0, 2]], [-1, 0, 0], fixtureCategory);
        fixture.Geometry!.Faces.Add(QuadWall(602, [[-2, 0, 1], [-2, 4, 1], [-2, 4, 2], [-2, 0, 2]],
            [1, 0, 0]).Geometry!.Faces[0]);
        var column = QuadWall(603, [[-1, 2 - cover, 0], [-1, 2 + cover, 0],
            [-1, 2 + cover, 3], [-1, 2 - cover, 3]], [1, 0, 0], (int)BuiltInCategory.OST_StructuralColumns);
        column.Geometry!.Faces[0].PlaneSurface = true;
        var document = cover == 0 ? Document(view, fixture) : Document(view, fixture, column);

        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;
        var edges = content.Edges.Where(edge => edge.CategoryId == fixtureCategory).ToList();

        Assert.Equal(edgeCount, edges.Count);
        Assert.All(edges, edge => Assert.Equal(2 * FeetToMillimetres, edge.MidDepth, 6));
        Assert.Equal(horizontalLength * FeetToMillimetres,
            edges.Where(edge => edge.Y1 == edge.Y2).Sum(edge => Math.Abs(edge.X2 - edge.X1)), 6);
    }

    [Theory]
    [InlineData(true, false, 0, true)]
    [InlineData(false, false, 0, false)]
    [InlineData(true, true, 0, false)]
    [InlineData(true, false, 0.5, false)]
    public void Collect_UsesTheSavedOpaqueFixtureFinishInAnAmbientFineSection(
        bool ambientShadows, bool useRenderAppearance, double transparency, bool expected)
    {
        var view = SectionView(900, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 50);
        view.ViewCrop = new ViewCropFrame(10, 0, 0, [0, 1, 0], [0, 0, 1], [-1, 0, 0], -1, -1, 5, 5, true);
        var material = new Element
        {
            Id = 901, HasShadingAppearance = true, SerializedMaterialUsesRenderAppearance = useRenderAppearance,
            SerializedMaterialTransparency = transparency,
        };
        Add(material, BuiltInParameter.MATERIAL_PARAM_COLOR, StorageType.Integer, 0x787878);
        var fixture = QuadWall(902, [[8, 1, 1], [8, 3, 1], [8, 3, 2], [8, 1, 2]], [1, 0, 0],
            (int)BuiltInCategory.OST_LightingFixtures);
        var face = Assert.Single(fixture.Geometry!.Faces);
        face.PlaneSurface = true;
        face.NestedRenderStyleId = material.Id;
        var document = Document(view, fixture, material);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        if (ambientShadows) document.RawData.ViewAmbientFractions = new Dictionary<int, double> { [view.Id] = 0.5 };

        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;

        if (expected)
            Assert.Equal(120 / 255d, Assert.Single(content.FaceFills!).Value!.Value, 10);
        else
            Assert.Null(content.FaceFills);
    }

    [Fact]
    public void Collect_FineSectionShowsAThinLightingFixtureSuspension()
    {
        var view = SectionView(900, [0, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 20);
        var fixture = QuadWall(601, [[-2, 0, 1], [-2, 0.01, 1], [-2, 0.01, 2], [-2, 0, 2]],
            [1, 0, 0], (int)BuiltInCategory.OST_LightingFixtures);

        var content = ElevationPrinter.Collect(Document(view, fixture), "section", planeView: view)!;

        Assert.Contains(content.Edges, edge => edge.CategoryId == fixture.CategoryId
            && edge.X1 == edge.X2 && Math.Abs(Math.Abs(edge.Y2 - edge.Y1) - FeetToMillimetres) < 1e-6);
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_Topography, false, false, true)]
    [InlineData(BuiltInCategory.OST_Topography, true, false, false)]
    [InlineData(BuiltInCategory.OST_Floors, false, false, true)]
    [InlineData(BuiltInCategory.OST_Floors, false, true, false)]
    public void Collect_FineSectionUsesPlacedPlanarGeometryToShadeNearbyWalls(BuiltInCategory category,
        bool discovered, bool warped, bool shaded)
    {
        var view = SectionView(900, [0, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 50);
        view.ViewCrop = new ViewCropFrame(0, 0, 0, [0, 1, 0], [0, 0, 1], [-1, 0, 0], -5, -1, 5, 5, true);
        var wall = QuadWall(901, [[-4, -2, 0], [-4, 2, 0], [-4, 2, 2], [-4, -2, 2]], [1, 0, 0]);
        wall.Geometry!.Faces[0].PlaneSurface = true;
        var blocker = QuadWall(902, [[-4, -5, 0], [0, -5, 0], [0, 5, warped ? 0.1 : 0], [-4, 5, 0]],
            [0, 0, 1], (int)category);
        blocker.Geometry!.Faces[0].PlaneSurface = category == BuiltInCategory.OST_Topography;
        blocker.IsDiscovered = discovered;
        var document = Document(view, wall, blocker);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        document.RawData.ViewAmbientFractions = new Dictionary<int, double> { [view.Id] = 0.5 };

        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;

        if (shaded) Assert.True(Assert.Single(content.AmbientTones!).Value.Image.Rgb.Min() < 255);
        else Assert.Null(content.AmbientTones);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AmbientOcclusion_SoftensDistantBlockersOnlyForReceiversWithDistanceFalloff(bool distanceFalloff)
    {
        byte[] Sample(float depth)
        {
            var ambient = new ElevationPrinter.AmbientOcclusion(0.5, (0, 0, 10, 10), binaryVisibility: true);
            ambient.AddReceiver([(0, 0), (10, 0), (10, 10), (0, 10)], null,
                System.Numerics.Vector3.Zero, -System.Numerics.Vector3.UnitZ, distanceFalloff: distanceFalloff);
            ambient.AddOccluder([new(-10000, -10000, depth), new(10000, -10000, depth),
                new(10000, 10000, depth), new(-10000, 10000, depth)], System.Numerics.Vector3.UnitZ);
            return Assert.Single(ambient.Calculate()!).Value.Image.Rgb;
        }

        var near = Sample(-25);
        var far = Sample(-200);
        if (distanceFalloff) Assert.True(far.Min() > near.Max());
        else Assert.Equal(near, far);
    }

    [Theory]
    [InlineData(6000, -3000, true)]
    [InlineData(2000, -3000, false)]
    [InlineData(6000, -2000, false)]
    public void AmbientOcclusion_ExtendsTheReachBelowAnExteriorWallBase(double cropSize, float cutDepth,
        bool shaded)
    {
        var ambient = new ElevationPrinter.AmbientOcclusion(0.5, (0, 0, cropSize, cropSize), binaryVisibility: true);
        ambient.AddReceiver([(0, 0), (10, 0), (10, 10), (0, 10)], null,
            System.Numerics.Vector3.Zero, -System.Numerics.Vector3.UnitZ, exteriorWall: (4, cutDepth));
        ambient.AddOccluder([new(-10000, -10000, -2200), new(10000, -10000, -2200),
            new(10000, 10000, -2200), new(-10000, 10000, -2200)], System.Numerics.Vector3.UnitZ);

        var tones = ambient.Calculate();
        if (shaded)
        {
            var pixels = Assert.Single(tones!).Value.Image.Rgb;
            Assert.All(pixels[..18], sample => Assert.Equal(255, sample));
            Assert.All(pixels[18..], sample => Assert.InRange(sample, (byte)189, (byte)254));
        }
        else
        {
            Assert.Null(tones);
        }
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void AmbientOcclusion_SkipsOnlyTheAttachedRoof(bool attached, bool separateRoof, bool shaded)
    {
        var ambient = new ElevationPrinter.AmbientOcclusion(0.5, (0, 0, 10, 10), binaryVisibility: true);
        ambient.AddReceiver([(0, 0), (10, 0), (10, 10), (0, 10)], null,
            System.Numerics.Vector3.Zero, -System.Numerics.Vector3.UnitZ, attachedRoofIds: attached ? [1] : null);
        AddRoof(1, -100);
        if (separateRoof) AddRoof(2, -200);

        Assert.Equal(shaded, ambient.Calculate() is { Count: 1 });

        void AddRoof(int roofId, float depth) =>
            ambient.AddOccluder([new(-10000, -10000, depth), new(10000, -10000, depth),
                new(10000, 10000, depth), new(-10000, 10000, depth)], System.Numerics.Vector3.UnitZ, roofId: roofId);
    }

    [Fact]
    public void AmbientOcclusion_ModulatesTheSavedSurfaceTone()
    {
        var ambient = new ElevationPrinter.AmbientOcclusion(0.5, (0, 0, 10, 10), binaryVisibility: true);
        ambient.AddReceiver([(0, 0), (10, 0), (10, 10), (0, 10)], null,
            System.Numerics.Vector3.Zero, -System.Numerics.Vector3.UnitZ, gray: 0.6);
        ambient.AddOccluder([new(-10000, -10000, -25), new(10000, -10000, -25),
            new(10000, 10000, -25), new(-10000, 10000, -25)], System.Numerics.Vector3.UnitZ);

        var patch = Assert.Single(ambient.Calculate()!).Value;
        Assert.All(patch.Image.Rgb, sample => Assert.Equal(111, sample));
    }

    [Fact]
    public void AmbientOcclusion_OrientsDirectLightOnWallsOutsideNearerGlazing()
    {
        var ambient = new ElevationPrinter.AmbientOcclusion(0.5, (0, 0, 10, 10));
        var normal = new System.Numerics.Vector3(0.8f, 0, -0.6f);
        List<(double X, double Y)> wall = [(0, 0), (10, 0), (10, 10), (0, 10)];
        ambient.AddReceiver(wall, null, System.Numerics.Vector3.Zero, normal, orientDirectLight: true);
        ambient.AddOccluder([new(0, 0, 0), new(10, 0, 40f / 3), new(10, 10, 40f / 3), new(0, 10, 0)], normal);
        List<(double X, double Y)> nearPane = [(5, 0), (9, 0), (9, 10), (5, 10)];
        ambient.AddGlazing(nearPane, null, -10);
        ambient.AddGlazing(nearPane, null, 30);
        ambient.AddGlazing(nearPane, null, 8);
        ambient.AddGlazing([(11, 0), (20, 0), (20, 10), (11, 10)], null, -10);

        var patch = Assert.Single(ambient.Calculate()!).Value;

        Assert.All(patch.Image.Rgb, sample => Assert.Equal(231, sample));
        Assert.All(patch.ThroughGlass!.Rgb, sample => Assert.Equal(255, sample));
        Assert.Equal(2, patch.Glass!.Count);
        Assert.Equal(nearPane, patch.Glass![0].Polygon);
        Assert.Equal(6.0075, patch.Glass[1].Polygon.Min(point => point.X), 4);
        Assert.Equal(9, patch.Glass[1].Polygon.Max(point => point.X), 6);
    }

    [Fact]
    public void AmbientOcclusion_SamplesCoveredPartsOfAPartlyVisibleFace()
    {
        var ambient = new ElevationPrinter.AmbientOcclusion(0.5, (0, 0, 10, 10));
        ambient.AddReceiver([(0, 0), (10, 0), (10, 10), (0, 10)], null,
            System.Numerics.Vector3.Zero, -System.Numerics.Vector3.UnitZ);
        ambient.AddOccluder([new(-100, -100, -1), new(4, -100, -1), new(4, 100, -1), new(-100, 100, -1)],
            System.Numerics.Vector3.UnitZ);

        var patch = Assert.Single(ambient.Calculate()!).Value.Image;

        Assert.True(patch.Rgb[0] < byte.MaxValue);
    }

    [Theory]
    [InlineData(true, 199)]
    [InlineData(true, 201)]
    [InlineData(false, 199)]
    public void AmbientOcclusion_UsesVisibleSlabSamplesAtNearerCutCaps(bool exclude, double capDepth)
    {
        var normal = System.Numerics.Vector3.UnitZ;
        var ambient = new ElevationPrinter.AmbientOcclusion(0.5, (0, 0, 300, 300));
        ambient.AddReceiver([(0, 0), (300, 0), (300, 300), (0, 300)], null, new(0, 0, 200), -normal,
            excludeSamplesBehindCut: exclude);
        ambient.AddOccluder([new(200, -1000, 199), new(1000, -1000, 199), new(1000, 1000, 199),
            new(200, 1000, 199)], normal);
        ambient.AddOccluder([new(-1500, -1500, -700), new(1500, -1500, -700), new(1500, 1500, -700),
            new(-1500, 1500, -700)], normal);
        List<List<(double X, double Y)>> caps = [[(200, -20), (310, -20), (310, 320), (200, 320)]];

        var baseline = Assert.Single(ambient.Calculate()!).Value.Image.Rgb;
        var result = Assert.Single(ambient.Calculate(caps, capDepth)!).Value.Image.Rgb;

        if (exclude && capDepth < 200)
        {
            Assert.True(baseline[6] < baseline[3]);
            Assert.Equal(baseline[0], result[0]);
            Assert.Equal(baseline[3], result[3]);
            Assert.Equal(baseline[3], result[6]);
        }
        else
            Assert.Equal(baseline, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AmbientOcclusion_SamplesNarrowFramesBesideAnOpening(bool enclosed)
    {
        var ambient = new ElevationPrinter.AmbientOcclusion(0.5, (0, 0, 10, 10));
        List<(double X, double Y)> receiver = enclosed
            ? [(0, 0), (10, 0), (10, 10), (0, 10)]
            : [(0, 0), (1, 0), (1, 9), (9, 9), (9, 0), (10, 0), (10, 10), (0, 10)];
        ambient.AddReceiver(receiver, enclosed ? [[(1, 1), (9, 1), (9, 9), (1, 9)]] : null,
            System.Numerics.Vector3.Zero, -System.Numerics.Vector3.UnitZ);
        ambient.AddOccluder([new(-100, -100, -0.1f), new(4, -100, -0.1f),
            new(4, 100, -0.1f), new(-100, 100, -0.1f)], System.Numerics.Vector3.UnitZ);

        var patch = Assert.Single(ambient.Calculate()!).Value.Image;

        Assert.InRange(patch.Rgb[0], 187, 190);
        Assert.Equal(byte.MaxValue, patch.Rgb[6]);
    }

    [Theory]
    [InlineData(20, true)]
    [InlineData(2000, false)]
    public void AmbientOcclusion_UsesNearbyPhysicalBlockers(float distance, bool shaded)
    {
        var ambient = new ElevationPrinter.AmbientOcclusion(0.5, (0, 0, 10, 10));
        List<(double X, double Y)> receiver = [(0, 0), (10, 0), (10, 10), (0, 10)];
        ambient.AddReceiver(receiver, null, System.Numerics.Vector3.Zero, -System.Numerics.Vector3.UnitZ);
        ambient.AddOccluder([new(distance, -5000, -5000), new(distance, 5000, -5000),
            new(distance, 5000, 0), new(distance, -5000, 0)], System.Numerics.Vector3.UnitX);

        var tones = ambient.Calculate();
        if (shaded) Assert.InRange(Assert.Single(tones!).Value.Image.Rgb.Min(), 188, 254);
        else Assert.Null(tones);
    }

    [Fact]
    public void DrawContent_InksCoincidentCroppedSiteEdgesOnce()
    {
        var edge = new ElevationPrinter.CollectedEdge(-100, 0, 1100, 0, 0, 0, -1, -1, 0, 0, 0, 0,
            false, (int)BuiltInCategory.OST_Entourage);
        var content = new ElevationPrinter.ElevationContent([], [], [], [], [], 0, 0, 1000, 1000,
            SiteOutlines: [edge, edge with { X1 = 1200, X2 = -200 }, edge with { Y2 = 500 }]);
        var page = new PdfDocument().AddPage(PdfPageSize.A1.Landscape);

        ElevationPrinter.DrawContent(page, content, 100, 10, 10, clipRect: (0, 0, 1000, 1000));

        Assert.Equal(2, CountOccurrences(page.Content, " l S\n"));
    }

    [Fact]
    public void PaneScreenShadows_UseVisiblePaneDepthAndRespectOpeningsAndScale()
    {
        List<(double X, double Y)> wall = [(1200, 0), (3300, 0), (3300, 1600), (1200, 1600)];
        List<(double X, double Y)> pane = [(100, 0), (1050, 0), (1050, 1500), (100, 1500)];
        List<(double X, double Y)> hole = [(1450, 700), (1800, 700), (1800, 950), (1450, 950)];
        List<(List<(double X, double Y)> Points, double Depth, bool Outline)> faces =
            [(wall, 6500, true), (pane, 100, true), (pane, 125, true)];
        var openings = new Dictionary<List<(double X, double Y)>, List<List<(double X, double Y)>>>
        {
            [wall] = [hole],
        };
        var receivers = new Dictionary<List<(double X, double Y)>, double> { [wall] = 6500 };
        var crop = (0d, 0d, 3300d, 1600d);
        var fullScale = new ElevationPrinter.PaneScreenShadows(crop, 20, 0.5)
            .Calculate(faces, [1, 2, 2], openings, [2], receivers, null)![wall];
        var halfScale = new ElevationPrinter.PaneScreenShadows(crop, 40, 0.5)
            .Calculate(faces, [1, 2, 2], openings, [2], receivers, null)![wall];

        Assert.True(fullScale.Image.TransparentWhite);
        byte near = ShadeAt(fullScale, 1325, 800);
        Assert.True(near < 255);
        Assert.True(ShadeAt(halfScale, 1325, 800) > near);
        Assert.Equal(255, ShadeAt(fullScale, 1550, 820));

        faces.Add((pane, 0, true));
        Assert.Null(new ElevationPrinter.PaneScreenShadows(crop, 20, 0.5)
            .Calculate(faces, [1, 2, 2, 3], openings, [2], receivers, null));

        static byte ShadeAt(ElevationPrinter.AmbientPatch patch, double x, double y)
        {
            int column = (int)((x - patch.X0) / (patch.X1 - patch.X0) * patch.Image.Width);
            int row = (int)((patch.Y1 - y) / (patch.Y1 - patch.Y0) * patch.Image.Height);
            return patch.Image.Rgb[(row * patch.Image.Width + column) * 3];
        }
    }

    [Theory]
    [InlineData(false, "0.72 0.72 0.72 rg", false, false)]
    [InlineData(false, "0.72 0.72 0.72 rg", true, false)]
    [InlineData(false, "0.72 0.72 0.72 rg", true, true)]
    [InlineData(true, "0.9 g\n", false, false)]
    public void DrawContent_UsesThePaneToneForTheSectionKind(bool detailCallout, string fillOperator, bool ambientPatch,
        bool coversBacking)
    {
        var glass = new Element { Id = 1, HasShadingAppearance = true, SerializedMaterialTransparency = 0.9 };
        var wall = new Element { Id = 2, HasShadingAppearance = true };
        Add(glass, BuiltInParameter.MATERIAL_PARAM_COLOR, StorageType.Integer, 0);
        Add(wall, BuiltInParameter.MATERIAL_PARAM_COLOR, StorageType.Integer, 0xffffff);
        var document = Document(glass, wall);
        var glazing = new ElevationPrinter.AmbientGlazing(0.8, detailCallout);
        List<(double X, double Y)> back = [(0, 0), (500, 0), (500, 1000), (0, 1000)];
        List<(double X, double Y)> pane = [(200, 200), (800, 200), (800, 800), (200, 800)];
        List<(double X, double Y)> front = [(150, 400), (850, 400), (850, 500), (150, 500)];
        glazing.Add(document, back, 240, 240, wall.Id, glazing: false);
        glazing.Add(document, pane, 100, 100, glass.Id, glazing: true, coversBacking: coversBacking);
        glazing.Add(document, front, 50, 50, wall.Id, glazing: false);
        var tones = ambientPatch
            ? new Dictionary<List<(double X, double Y)>, ElevationPrinter.AmbientPatch>
            {
                [pane] = new(new PdfRasterImage(1, 1, [150, 150, 150]), 200, 200, 800, 800),
            }
            : null;
        var content = new ElevationPrinter.ElevationContent([(back, 240, false), (pane, 100, false), (front, 50, false)],
            [], [], [], [], 0, 0, 1000, 1000, Glazing: glazing, AmbientTones: tones);
        var page = new PdfDocument().AddPage(PdfPageSize.A1.Landscape);
        ElevationPrinter.DrawContent(page, content, 50, 10, 10);

        Assert.Equal(coversBacking ? 0 : 1, CountOccurrences(page.Content, fillOperator));
        Assert.Equal(ambientPatch, page.Content.Contains("/Im", StringComparison.Ordinal));
        if (!detailCallout)
        {
            int backdrop = page.Content.IndexOf(coversBacking ? "0.576 g\n" : "0.72 g\n", StringComparison.Ordinal);
            int clip = page.Content.IndexOf("W* n", StringComparison.Ordinal);
            Assert.True(backdrop >= 0 && backdrop < clip);
            if (ambientPatch && !coversBacking)
            {
                int image = page.Content.IndexOf("/Im", StringComparison.Ordinal);
                int backing = page.Content.IndexOf(fillOperator, StringComparison.Ordinal);
                Assert.True(clip < image && image < backing);
            }
        }
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_CurtainWallPanels, true, 0.9, true)]
    [InlineData(BuiltInCategory.OST_CurtainWallPanels, false, 0.9, false)]
    [InlineData(BuiltInCategory.OST_CurtainWallPanels, true, 0.0, false)]
    [InlineData(BuiltInCategory.OST_Mass, true, 0.9, true)]
    [InlineData(BuiltInCategory.OST_Mass, true, 0.0, false)]
    [InlineData(BuiltInCategory.OST_MassForm, true, 0.9, true)]
    public void DrawContent_CompositesASavedTransparentDetailSurfaceOnce(
        BuiltInCategory category, bool ambient, double transparency, bool expected)
    {
        var view = SectionView(900, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 20);
        var material = new Element
        {
            Id = 901, HasShadingAppearance = true, SerializedMaterialTransparency = transparency,
        };
        Add(material, BuiltInParameter.MATERIAL_PARAM_COLOR, StorageType.Integer, 0);
        var pane = QuadWall(902, [[8, 0, 0], [8, 4, 0], [8, 4, 4], [8, 0, 4]],
            [1, 0, 0], (int)category);
        var back = QuadWall(903, [[7.9, 0, 0], [7.9, 0, 4], [7.9, 4, 4], [7.9, 4, 0]], [-1, 0, 0]);
        foreach (var face in pane.Geometry!.Faces.Concat(back.Geometry!.Faces))
        {
            face.PlaneSurface = true;
            if (category == BuiltInCategory.OST_MassForm)
                face.NestedRenderStyleId = material.Id;
            else
                face.RenderStyleId = material.Id;
        }
        pane.Geometry.Faces.Add(back.Geometry.Faces[0]);
        var document = Document(view, material, pane, WallWithFace(904));
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        if (ambient) document.RawData.ViewAmbientFractions = new Dictionary<int, double> { [view.Id] = 0.5 };
        var crop = (-100d, -100d, 1300d, 1300d);
        var content = ElevationPrinter.Collect(document, "section", planeView: view,
            isDetail: true, cropWindow: crop)!;
        var pdf = new PdfDocument();
        var page = pdf.AddPage(PdfPageSize.A1.Landscape);

        ElevationPrinter.DrawContent(page, content, 20, 10, 10, 0.82, document, crop);
        string commands = PdfTestContent.Read(pdf);

        if (expected)
        {
            Assert.Equal(2, content.DetailGlass!.Count);
            Assert.Single(content.DetailGlass.Values, value => value.Opacity == 0);
            Assert.Equal(1, CountOccurrences(page.Content, "q /Gs0 gs\n"));
            Assert.Contains("/Gs0 << /Type /ExtGState /ca 0.1 >>", commands);
        }
        else
        {
            Assert.Null(content.DetailGlass);
            Assert.DoesNotContain("/ExtGState", commands);
        }
    }

    [Fact]
    public void DrawContent_PlacesLevelMarkersPastTheCropFrame()
    {
        // The datum ends at x = 1000 but the crop runs to x = 2000: the marker column stands 10 mm past the
        // crop-mapped right edge, not at the datum end, and the row runs out to it. At scale 100 with origin
        // (10, 10) that puts the marker at x = 40 mm and the label text at x = 44 mm.
        var content = new ElevationPrinter.ElevationContent(
            [],
            [(0, "Level 1", 0, 1000)],
            [],
            [],
            [],
            0, -100, 1000, 100);
        var pdf = new PdfDocument();
        PdfPage page = pdf.AddPage(PdfPageSize.A1.Landscape);

        ElevationPrinter.DrawContent(page, content, 100, 10, 10, 1, null, (0, -500, 2000, 500));
        string text = PdfTestContent.Read(pdf);

        var match = System.Text.RegularExpressions.Regex.Match(text, @"([\d.]+) ([\d.]+) Td \(Level 1\)");
        Assert.True(match.Success, "Level 1 label not drawn.");
        Assert.Equal(44 * 72.0 / 25.4, double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 1);
    }

    [Fact]
    public void DrawContent_InksLevelMarkersSolid()
    {
        // Only the row itself dashes: the marker circle follows a dash reset, so exactly two dash resets
        // are written for one level row (one after the row, one trailing the block).
        var content = new ElevationPrinter.ElevationContent(
            [],
            [(0, "Level 1", 0, 1000)],
            [],
            [],
            [],
            0, -100, 1000, 100);
        var pdf = new PdfDocument();
        PdfPage page = pdf.AddPage(PdfPageSize.A1.Landscape);

        ElevationPrinter.DrawContent(page, content, 100, 10, 10, 1, null, (0, -500, 2000, 500));
        string text = PdfTestContent.Read(pdf);

        Assert.Equal(2, CountOccurrences(text, "[] 0 d"));
    }

    [Theory]
    [InlineData(0x30, -2.0)]
    [InlineData(0x20, -6.0)]
    public void Collect_ProjectsAuthoredLevelEndsAndGridEnvelope(int levelOverrides, double leftFeet)
    {
        var view = SectionView(900, [10, 0, 0]);
        var wall = QuadWall(600, [[9, -4, 0], [9, 4, 0], [9, 4, 4], [9, -4, 4]], [1, 0, 0]);
        var level = new Element
        {
            Id = 601, Name = "Level 1", CategoryId = (int)BuiltInCategory.OST_Levels,
            NativeRootClassName = "Level", TypeId = 100,
            LevelPlane = new GridPlaneData([-4, -4, 4, 4], [0, 1, 0], [1, 0, 0], [0, 1, 0]),
        };
        Add(level, BuiltInParameter.LEVEL_ELEV, StorageType.Double, 0.0);
        var grid = new Element
        {
            Id = 602, Name = "A", CategoryId = (int)BuiltInCategory.OST_Grids, TypeId = 101,
            Location = new LocationCurve { CurveType = "Line", StartPoint = [5, 2, 0], EndPoint = [6, 2, 0] },
            GridPlane = new GridPlaneData([-1, -2, 1, 8], [5, 2, 0], [1, 0, 0], [0, 0, 1]),
        };
        var levelHead = new Element { Id = 110, FamilyName = "M_Level Head - Circle", BoundingBox = [0, -0.01, 0, 0.02, 0.01, 0] };
        var gridHead = new Element { Id = 111, FamilyName = "M_Grid Head - Circle", BoundingBox = [-0.02, -0.02, 0, 0.02, 0.02, 0] };
        var document = Document(view, wall, level, grid, levelHead, gridHead);
        document.RawData.DatumHeads = new Dictionary<int, TfBlockDeserializer.DatumHead>
        {
            [100] = new(110, -1), [101] = new(111, 2),
        };
        document.RawData.DatumExtents = new Dictionary<int, TfBlockDeserializer.ViewDatumExtents>
        {
            [view.Id] = new(view.Id, [-1, 0, 0],
                new()
                {
                    [level.Id] = new(-3, 5, (byte)levelOverrides, new([10, 4, 0], [10, 5, -1], [10, 6, -1]),
                        FreeEnd3d: -7),
                    [grid.Id] = new(-1, 6, 0x30, null, -2, 8),
                }),
        };

        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;

        var levelLine = Assert.Single(content.NativeLevels!).Value;
        Assert.Equal(leftFeet * FeetToMillimetres, levelLine.Left, 6);
        Assert.Equal(6 * FeetToMillimetres, levelLine.HeadX, 6);
        Assert.Equal(-FeetToMillimetres, levelLine.HeadY, 6);
        Assert.Equal(4 * FeetToMillimetres, levelLine.LineEnd, 6);
        Assert.Equal(0.01 * FeetToMillimetres, levelLine.Radius, 6);
        var gridLine = Assert.Single(content.NativeGrids!).Value;
        Assert.Equal(-FeetToMillimetres, gridLine.Bottom, 6);
        Assert.Equal(6 * FeetToMillimetres, gridLine.Top, 6);
        Assert.Equal(0.02 * FeetToMillimetres, gridLine.Radius, 6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Collect_UsesSharedModelExtentLevelHeadsWhenSavedLeadersAgree(bool agree)
    {
        var view = SectionView(900, [10, 0, 0]);
        var wall = QuadWall(600, [[9, 196, 0], [9, 204, 0], [9, 204, 4], [9, 196, 4]], [1, 0, 0]);
        var upper = DatumLevel(601, "Upper", 3, [-4, -4, 4, 4]);
        var lower = DatumLevel(602, "Lower", 1, [-4, -4, 4, 4]);
        var straight = DatumLevel(603, "Straight", 0, [-4, -4, 4, 4]);
        var unrelated = DatumLevel(604, "Other extent", -1, [-4, -4, 4, 6]);
        foreach (var level in new[] { upper, lower, straight, unrelated }) level.TypeId = 100;
        var head = new Element
        {
            Id = 110, FamilyName = "M_Level Head - Circle", BoundingBox = [0, -0.01, 0, 0.02, 0.01, 0],
        };
        var doc = Document(view, wall, upper, lower, straight, unrelated, head);
        doc.RawData.DatumHeads = new Dictionary<int, TfBlockDeserializer.DatumHead> { [100] = new(110, -1) };
        doc.RawData.DatumExtents = new Dictionary<int, TfBlockDeserializer.ViewDatumExtents>
        {
            [view.Id] = new(view.Id, [-1, 0, 0], new()
            {
                [upper.Id] = new(0, 0, 0, new([10, 201, 3], [10, 202, 4], [10, 203, 4]), -4, 4),
                [lower.Id] = new(0, 0, 0, new([10, 201, 1], [10, 202, 2], [10, agree ? 203 : 202, 2]), -4, 4),
            }),
        };

        var content = ElevationPrinter.Collect(doc, "east", planeView: view)!;
        var lines = content.NativeLevels!;
        Assert.Equal(203 * FeetToMillimetres, lines[3 * FeetToMillimetres].HeadX, 6);
        Assert.Equal(4 * FeetToMillimetres, lines[3 * FeetToMillimetres].HeadY, 6);
        Assert.Contains(FeetToMillimetres, lines.Keys);
        Assert.DoesNotContain(-FeetToMillimetres, lines.Keys);
        Assert.Equal(agree, lines.ContainsKey(0));
        if (agree) Assert.Equal(203 * FeetToMillimetres, lines[0].HeadX, 6);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void DrawNativeDatums_UsesSavedLevelLabelStyleAndMaskHeight(bool opaque)
    {
        var font = PdfFonts.TryFind(out _) ?? throw new SkipException("No TrueType font was found on this machine.");
        var doc = Document();
        doc.RawData.TitleBlockLabels = new Dictionary<int, IReadOnlyList<TfBlockDeserializer.TitleBlockLabel>>
        {
            [10] =
            [
                new(-0.012, 0.003, 0, 0, 1, 1, 0, 0.015625, 0.28, 20, 0x900,
                    (int)BuiltInParameter.DATUM_TEXT, "", ""),
                new(-0.012, -0.004, 0, 0, 1, 1, 0, 0.015625, 0.28, 20, 0x300,
                    (int)BuiltInParameter.LEVEL_ELEV, "", ""),
            ],
        };
        doc.RawData.TitleBlockCapHeightsFeet = new Dictionary<int, double> { [20] = 0.009842519685039368 };
        doc.RawData.OpaqueSymbolLabelStyles = opaque ? new HashSet<int> { 20 } : new HashSet<int>();
        doc.RawData.RegularArialSymbolLabelStyles = new HashSet<int> { 20 };
        var content = new ElevationPrinter.ElevationContent([], [(0, "Level 1", 0, 500)], [], [], [],
            0, -500, 500, 500, NativeLevels: new Dictionary<double, ElevationPrinter.NativeLevelLine>
            {
                [0] = new(0, 500, 0, 500, 4, null, SymbolId: 10),
            });
        var pdf = new PdfDocument { Font = font };
        var page = pdf.AddPage(PdfPageSize.A1.Landscape);
        ElevationPrinter.DrawNativeDatums(page, content, 100, 60, 50, doc, null);

        Assert.Equal(opaque ? 2 : 0, CountOccurrences(page.Content, " 13.5 re f\n"));
        Assert.Equal(2, CountOccurrences(page.Content, "BT /F2 "));
        if (opaque && OperatingSystem.IsWindows() && font.Name.Equals("Arial", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Contains(" 39.1875 13.5 re f\n", page.Content);
            Assert.Contains(" 6.75 13.5 re f\n", page.Content);
        }
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, 0x9ba8ad)]
    public void DrawContent_ClipsSiteContextBeforeTheAnnotationStrip(bool gradient, int? groundColor)
    {
        var content = new ElevationPrinter.ElevationContent([], [], [],
            [([(-100, 0), (1200, 0), (1200, 500), (-100, 500)], 0, true)], [], 0, -1000, 1000, 1000,
            SkyGradient: gradient ? (0x0000ff, 0xffffff, groundColor) : null);
        var pdf = new PdfDocument();
        var page = pdf.AddPage(PdfPageSize.A1.Landscape);

        ElevationPrinter.DrawContent(page, content, 100, 10, 0, clipRect: (0, -1000, 1000, 1000));
        string commands = PdfTestContent.Read(pdf);

        Assert.Contains("56.6929", commands);
        Assert.DoesNotContain("62.3622", commands);
        if (gradient)
        {
            Assert.Contains("/ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 28.3465 0 56.6929]", commands);
            Assert.Contains("/C0 [1 1 1] /C1 [1 0 0]", commands);
            const string ground = "0.6784 0.6588 0.6078 rg\n";
            if (groundColor.HasValue)
            {
                Assert.Contains("/Sh0 sh\nQ\n" + ground, commands);
                Assert.Contains("28.3465 28.3465 l h f\n1 1 1 rg\n", commands);
            }
            else
            {
                Assert.Contains("/Sh0 sh\nQ\n1 1 1 rg\n", commands);
                Assert.DoesNotContain(ground, commands);
            }
        }
        else
            Assert.DoesNotContain("/Shading", commands);
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_Windows, 600, 2, false, true)]
    [InlineData(BuiltInCategory.OST_Doors, 600, 2, false, true)]
    [InlineData(BuiltInCategory.OST_Windows, 600, 1, true, false)]
    [InlineData(BuiltInCategory.OST_Windows, -1, 1, false, false)]
    [InlineData(BuiltInCategory.OST_Windows, null, 1, false, false)]
    public void Collect_OnlyTheStoredHostWallLetsTheInsertShowThrough(
        BuiltInCategory category, int? hostId, double depth, bool foregroundWall, bool visible)
    {
        var host = WallWithFace(600);
        var insert = QuadWall(601, [[1, depth, 1], [2, depth, 1], [2, depth, 2], [1, depth, 2]],
            [0, 1, 0], (int)category);
        insert.SerializedHostId = hostId;
        insert.HostId = host.Id;
        var document = foregroundWall
            ? Document(host, insert, QuadWall(602, [[0, -0.25, 0], [4, -0.25, 0], [4, -0.25, 3], [0, -0.25, 3]], [0, 1, 0]))
            : Document(host, insert);

        var content = ElevationPrinter.Collect(document, "south")!;

        Assert.Equal(visible ? 4 : 0, content.Edges.Count(edge => edge.CategoryId == (int)category));
    }

    [Theory]
    [InlineData(2, 600, 100, true)]
    [InlineData(1, 600, 100, false)]
    [InlineData(2, 603, 100, false)]
    [InlineData(2, 600, 20, false)]
    public void Collect_KeepsTheJambBetweenTouchingWindowsOfTheSameHost(
        double adjacentX, int adjacentHost, int scale, bool visible)
    {
        var left = QuadWall(601, [[0, 1, 0], [2, 1, 0], [2, 1, 3], [0, 1, 3]],
            [0, 1, 0], (int)BuiltInCategory.OST_Windows);
        double slope = (4 - adjacentX) / 7.5;
        double normalY = slope / Math.Sqrt(1 + slope * slope);
        var adjacent = QuadWall(602, [[adjacentX, -3, -0.05], [4, 4.5, -0.05], [4, 4.5, 3.05], [adjacentX, -3, 3.05]],
            [-1 / Math.Sqrt(1 + slope * slope), normalY, 0], (int)BuiltInCategory.OST_Windows);
        left.SerializedHostId = 600;
        adjacent.SerializedHostId = adjacentHost;

        var view = ElevationView(900, "South", scale);
        var content = ElevationPrinter.Collect(Document(left, adjacent, view), "south", planeView: view)!;
        double jamb = 2 * FeetToMillimetres;

        Assert.Equal(visible, content.Edges.Any(e => e.CategoryId == left.CategoryId
            && Math.Abs(e.X1 - jamb) < 0.1 && Math.Abs(e.X2 - jamb) < 0.1));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void Collect_PrintsCurtainPanelsThroughNativeWallOpenings(bool complete, bool opening, bool visible)
    {
        var view = new Element
        {
            Id = 900, ViewOrigin = [0, 0, 0], ViewDirection = [0, 1, 0],
            ViewHorizontalDirection = [1, 0, 0], ViewVerticalDirection = [0, 0, 1],
        };
        var wall = QuadWall(601, [[0, 1, 0], [8, 1, 0], [8, 1, 8], [0, 1, 8]], [0, -1, 0]);
        wall.Geometry!.Faces[0].PlaneSurface = true;
        if (opening) wall.Geometry.Faces[0].InnerVertices = [[[1, 1, 1], [7, 1, 1], [7, 1, 7], [1, 1, 7]]];
        var curtain = QuadWall(602, [[1, 2, 1], [7, 2, 1], [7, 2, 7], [1, 2, 7]], [0, -1, 0]);
        var type = new Element { Id = 603, SerializedOwnElementClassId = 2559 }; // NewCurtainWallType
        curtain.TypeId = type.Id;
        int panel = (int)BuiltInCategory.OST_CurtainWallPanels;
        var glass = QuadWall(604, [[2, 3, 2], [6, 3, 2], [6, 3, 6], [2, 3, 6]], [0, -1, 0], panel);
        glass.SerializedHostId = curtain.Id;
        curtain.SerializedCurtainPanelOrder = complete ? [glass.Id] : [glass.Id, 605];
        glass.Geometry!.Faces[0].PlaneSurface = true;
        glass.Geometry.Faces[0].NestedRenderStyleId = 606;
        glass.Geometry.Faces[0].ModelPattern = new(800, [2, 3, 2], [1, 0, 0], [0, 0, 1]);
        var material = new Element { Id = 606, SerializedMaterialTransparency = 0.9 };
        var document = Document(view, wall, curtain, type, glass, material);
        document.RawData.OpaqueViewsWithSurfacePatterns = new HashSet<int> { view.Id };
        document.RawData.MaterialPatterns = new Dictionary<int, (int, int, int?)> { [material.Id] = (-1, 800, 0) };
        document.RawData.FillPatterns = new Dictionary<int, (string, IReadOnlyList<TfBlockDeserializer.PatternGrid>)>
        {
            [800] = ("Glass", [new(Math.PI / 3, 0, 0, 0.5, 2, [1, 2], Target: 1, Orientation: 0)]),
        };

        var content = ElevationPrinter.Collect(document, "south", planeView: view)!;
        Assert.Equal(opening, content.FaceOpenings != null);
        Assert.Equal(complete ? 2 : 3, content.Faces.Count);
        Assert.Equal(visible, content.Edges.Any(edge => edge.CategoryId == panel));
        var grid = Assert.Single(Assert.Single(content.ModelHatches!).Value.Grids);
        Assert.Equal(2 * FeetToMillimetres, grid.Spacing, 6);
        Assert.Equal(0.5 * FeetToMillimetres, grid.Shift, 6);
    }

    [Theory]
    [InlineData(20, true, true)]
    [InlineData(20, false, false)]
    [InlineData(100, true, false)]
    public void Collect_OpensTheCurtainFallbackOnlyForKnownGlazedPanels(int scale, bool transparent, bool opens)
    {
        var view = SectionView(900, [0, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, scale);
        var curtain = QuadWall(600, [[-1, 0, 0], [-1, 8, 0], [-1, 8, 4], [-1, 0, 4]], [1, 0, 0]);
        curtain.Geometry!.Faces[0].PlaneSurface = true;
        var type = new Element { Id = 601, SerializedOwnElementClassId = 2559 }; // NewCurtainWallType
        curtain.TypeId = type.Id;
        int category = (int)BuiltInCategory.OST_CurtainWallPanels;
        var panel = QuadWall(602, [[-2, 1, 1], [-2, 3, 1], [-2, 3, 3], [-2, 1, 3]], [1, 0, 0], category);
        var unresolved = new Element { Id = 603, CategoryId = category, SerializedHostId = curtain.Id };
        var material = new Element { Id = 604, SerializedMaterialTransparency = transparent ? 0.9 : 0 };
        panel.SerializedHostId = curtain.Id;
        panel.Geometry!.Faces[0].PlaneSurface = true;
        panel.Geometry.Faces[0].RenderStyleId = material.Id;
        panel.Geometry.Faces[0].ModelPattern = new(800, [-2, 1, 1], [0, 1, 0], [0, 0, 1]);
        curtain.SerializedCurtainPanelOrder = [panel.Id, unresolved.Id, panel.Id];

        var document = Document(view, curtain, type, panel, unresolved, material);
        document.RawData.OpaqueViewsWithSurfacePatterns = new HashSet<int> { view.Id };
        document.RawData.MaterialPatterns = new Dictionary<int, (int, int, int?)> { [material.Id] = (-1, 800, 0) };
        document.RawData.FillPatterns = new Dictionary<int, (string, IReadOnlyList<TfBlockDeserializer.PatternGrid>)>
        {
            [800] = ("Glass", [new(Math.PI / 3, 0, 0, 0, 2, [1, 2], Target: 1, Orientation: 0)]),
        };

        var result = ElevationPrinter.Collect(document, "section", planeView: view)!;

        Assert.Equal(2, result.Faces.Count);
        Assert.Equal(opens, result.FaceOpenings != null);
        if (opens)
        {
            var opening = Assert.Single(Assert.Single(result.FaceOpenings!).Value);
            Assert.Equal(4, opening.Count);
            Assert.Equal(FeetToMillimetres, opening.Min(point => point.X), 6);
            Assert.Equal(3 * FeetToMillimetres, opening.Max(point => point.X), 6);
            Assert.Single(result.ModelHatches!);
            Assert.Contains(result.Edges, edge => edge.CategoryId == category);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Collect_ProjectsTheElevationWallGridAroundItsOpening(bool showPatterns)
    {
        var view = new Element
        {
            Id = 900, ViewOrigin = [0, -2, 0], ViewDirection = [0, 1, 0],
            ViewHorizontalDirection = [1, 0, 0], ViewVerticalDirection = [0, 0, 1],
        };
        var wall = QuadWall(600, [[0, 0, 0], [4, 0, 0], [4, 0, 3], [0, 0, 3]], [0, -1, 0]);
        var face = Assert.Single(wall.Geometry!.Faces);
        face.PlaneSurface = true;
        face.RenderStyleId = 701;
        face.InnerVertices = [[[1, 0, 1], [2, 0, 1], [2, 0, 2], [1, 0, 2]]];
        var document = Document(view, wall);
        document.RawData.OpaqueViewsWithSurfacePatterns = showPatterns ? new HashSet<int> { view.Id } : [];
        document.RawData.MaterialPatterns = new Dictionary<int, (int, int, int?)> { [701] = (-1, 800, 0) };
        var grid = new TfBlockDeserializer.PatternGrid(Math.PI / 2, 0, 0, 0, 0.01, [], Target: 0, Orientation: 0);
        document.RawData.FillPatterns = new Dictionary<int, (string, IReadOnlyList<TfBlockDeserializer.PatternGrid>)>
        {
            [800] = ("Cladding", [grid]),
        };

        var content = ElevationPrinter.Collect(document, "south", planeView: view)!;
        if (!showPatterns)
        {
            Assert.Null(content.Hatch);
            return;
        }
        Assert.Same(grid, Assert.Single(content.Hatch!.Faces).Value.Grid);
        var page = new PdfDocument().AddPage(PdfPageSize.A1.Landscape);
        ElevationPrinter.DrawContent(page, content, 100, 10, 10);
        string pattern = page.Content.Split("q /Pattern cs /P0 scn\n")[1].Split("f Q\n")[0];
        Assert.Equal(2, CountOccurrences(pattern, "h "));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Collect_ProjectsTheRoofGridOnTheExteriorLayer(bool showPatterns)
    {
        double diagonal = Math.Sqrt(0.5);
        double[][] exterior = [[0, -2, 0], [4, -2, 0], [4, 2, 4], [0, 2, 4]];
        double[][] interior = [[0, -3, -2], [4, -3, -2], [4, 0, 1], [0, 0, 1]];
        var roof = QuadWall(600, exterior, [0, -diagonal, diagonal], (int)BuiltInCategory.OST_Roofs);
        var roofFaces = roof.Geometry!.Faces;
        roofFaces[0].ModelPattern = new(800, exterior[0], [1, 0, 0], [0, diagonal, diagonal]);
        roofFaces[0].InnerVertices = [[[1, -1.5, 0.5], [2, -1.5, 0.5], [2, -1.25, 0.75], [1, -1.25, 0.75]]];
        roofFaces.Add(Face([interior[3], interior[2], interior[1], interior[0]], [0, diagonal, -diagonal]));
        roofFaces.Add(Face([exterior[0], interior[0], interior[1], exterior[1]], [0, -2, 1]));
        roofFaces.Add(Face([exterior[1], interior[1], interior[2], exterior[2]], [1, 0, 0]));
        roofFaces.Add(Face([exterior[2], interior[2], interior[3], exterior[3]], [0, -3, 2]));
        roofFaces.Add(Face([exterior[3], interior[3], interior[0], exterior[0]], [-1, 0, 0]));
        foreach (var face in roofFaces)
        {
            face.PlaneSurface = true;
            face.GeometryNode = 1;
        }
        roof.Geometry.BrepSolids = [new() { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }];
        var view = new Element
        {
            Id = 900, ViewOrigin = [0, -12, 0], ViewDirection = [0, 1, 0],
            ViewHorizontalDirection = [1, 0, 0], ViewVerticalDirection = [0, 0, 1],
        };
        var document = Document(view, roof);
        document.RawData.OpaqueViewsWithSurfacePatterns = showPatterns ? new HashSet<int> { view.Id } : new HashSet<int>();
        document.RawData.FillPatterns = new Dictionary<int, (string, IReadOnlyList<TfBlockDeserializer.PatternGrid>)>
        {
            [800] = ("Seams", [new(Math.PI / 2, 0, 0, 0, 2, [], Target: 1)]),
        };

        var content = ElevationPrinter.Collect(document, "south", planeView: view)!;
        if (!showPatterns)
        {
            Assert.Null(content.ModelHatches);
            return;
        }
        var (polygon, hatch) = Assert.Single(content.ModelHatches!);
        var grid = Assert.Single(hatch.Grids);
        Assert.Equal(2 * FeetToMillimetres, grid.Spacing, 6);
        Assert.Equal(Math.PI / 2, grid.Angle, 6);
        Assert.Single(hatch.Holes);
        int exteriorIndex = content.Faces.FindIndex(face => ReferenceEquals(face.Points, polygon));
        int interiorIndex = content.Faces.FindIndex(face => face.Points.Min(p => p.Y) < 0
            && Math.Abs(face.Points.Max(p => p.Y) - FeetToMillimetres) < 1e-6);
        Assert.True(interiorIndex >= 0 && interiorIndex < exteriorIndex);

        var page = new PdfDocument().AddPage(PdfPageSize.A1.Landscape);
        ElevationPrinter.DrawContent(page, content, 100, 10, 10);
        string pattern = page.Content.Split("q /Pattern cs /P0 scn\n")[1].Split("f Q\n")[0];
        Assert.Equal(2, CountOccurrences(pattern, "h "));

        static GeoFace Face(double[][] vertices, double[] normal) => new() { OuterVertices = [.. vertices], Normal = normal };
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void Collect_ShowsSkylightOnlyThroughItsRoofOpening(bool opening, bool oppositeSlope, bool visible)
    {
        var roof = WallWithFace(600);
        roof.CategoryId = (int)BuiltInCategory.OST_Roofs;
        var faces = roof.Geometry!.Faces!;
        if (opening)
            faces[0].InnerVertices = [[[0.5, 0, 0.5], [2.5, 0, 0.5], [2.5, 0, 2.5], [0.5, 0, 2.5]]];
        if (oppositeSlope)
            faces.Add(QuadWall(602, [[0, -0.25, 0], [4, -0.25, 0], [4, -0.85, 3], [0, -0.85, 3]],
                [0, 1, 0.2]).Geometry!.Faces![0]);
        var skylight = QuadWall(601, [[1, 1, 1], [2, 1, 1], [2, 1, 2], [1, 1, 2]],
            [0, 1, 0], (int)BuiltInCategory.OST_Windows);
        skylight.SerializedHostId = roof.Id;

        var content = ElevationPrinter.Collect(Document(roof, skylight), "south")!;

        Assert.Equal(visible ? 4 : 0, content.Edges.Count(edge => edge.CategoryId == skylight.CategoryId));
    }

    [Theory]
    [InlineData(0.25, false)]
    [InlineData(2.0, true)]
    public void Collect_ComparesSkylightDepthAtTheRoofFace(double x, bool visible)
    {
        var roof = QuadWall(600, [[0, 0, 0], [4, 4, 0], [4, 4, 3], [0, 0, 3]],
            [-1, 1, 0], (int)BuiltInCategory.OST_Roofs);
        var skylight = QuadWall(601, [[x, 1, 1], [x + 0.5, 1, 1], [x + 0.5, 1, 2], [x, 1, 2]],
            [0, 1, 0], (int)BuiltInCategory.OST_Windows);
        skylight.SerializedHostId = roof.Id;

        var content = ElevationPrinter.Collect(Document(roof, skylight), "south")!;

        Assert.Equal(visible ? 4 : 0, content.Edges.Count(edge => edge.CategoryId == skylight.CategoryId));
    }

    [Theory]
    [InlineData(true, (int)BuiltInCategory.OST_Walls)]
    [InlineData(false, (int)BuiltInCategory.OST_Materials)]
    public void Collect_OnlyPlacedGeometryWithMaterialCategoryCanHideAWall(bool discovered, int visibleCategory)
    {
        var view = SectionView(900, [0, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 50);
        var wall = QuadWall(600, [[-2, 0, 0], [-2, 4, 0], [-2, 4, 3], [-2, 0, 3]], [1, 0, 0]);
        var material = QuadWall(601, [[-1, -1, -1], [-1, 5, -1], [-1, 5, 4], [-1, -1, 4]],
            [1, 0, 0], (int)BuiltInCategory.OST_Materials);
        material.IsDiscovered = discovered;

        var content = ElevationPrinter.Collect(Document(view, wall, material), "section", planeView: view)!;
        Assert.Equal(discovered ? 1 : 2, content.Faces.Count);
        Assert.Equal(4, content.Edges.Count);
        Assert.All(content.Edges, edge => Assert.Equal(visibleCategory, edge.CategoryId));
    }

    [Theory]
    [InlineData(2.0, true)]
    [InlineData(3.0, false)]
    public void Collect_SectionCropDoesNotInkArtificialFaceBorders(double wallEnd, bool rightEdgeIsNative)
    {
        var view = SectionView(900, [0, 0, 0]);
        var wall = QuadWall(600, [[-2, 0, 0], [-2, wallEnd, 0], [-2, wallEnd, 3], [-2, 0, 3]], [1, 0, 0]);
        double left = 0.5 * FeetToMillimetres;
        double right = 2 * FeetToMillimetres;

        var content = ElevationPrinter.Collect(Document(view, wall), "section", planeView: view,
            cropWindow: (left, -FeetToMillimetres, right, 4 * FeetToMillimetres))!;

        Assert.Single(content.Faces);
        Assert.Equal(rightEdgeIsNative ? 3 : 2, content.Edges.Count);
        Assert.DoesNotContain(content.Edges, edge => Math.Abs(edge.X1 - left) < 1e-6 && Math.Abs(edge.X2 - left) < 1e-6);
        Assert.Equal(rightEdgeIsNative, content.Edges.Any(edge =>
            Math.Abs(edge.X1 - right) < 1e-6 && Math.Abs(edge.X2 - right) < 1e-6));
        Assert.Equal(2, content.Edges.Count(edge => Math.Abs(edge.Y1 - edge.Y2) < 1e-6));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Collect_SkipsTheSectionViewsHiddenGeometry(bool byCategory, bool detail)
    {
        var view = SectionView(900, [0, 0, 0]);
        var other = SectionView(901, [0, 0, 0]);
        var wall = QuadWall(600, [[-2, 0, 0], [-2, 4, 0], [-2, 4, 3], [-2, 0, 3]], [1, 0, 0]);
        var cover = QuadWall(601, [[-1, -1, -1], [-1, 5, -1], [-1, 5, 4], [-1, -1, 4]],
            [1, 0, 0], (int)BuiltInCategory.OST_Mass);
        var document = Document(view, other, wall, cover);
        foreach (var section in new[] { view, other })
        {
            Add(section, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 50);
            section.ViewCrop = new ViewCropFrame(0, 0, 0, [0, 1, 0], [0, 0, 1], [-1, 0, 0], -5, -5, 5, 5, true);
        }
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id, other.Id };
        if (byCategory)
            document.RawData.ViewHiddenCategories = new Dictionary<int, HashSet<int>> { [view.Id] = [(int)BuiltInCategory.OST_Mass] };
        else
            document.RawData.ViewHiddenElements = new Dictionary<int, HashSet<int>> { [view.Id] = [cover.Id] };

        ElevationPrinter.ElevationContent Collect(Element section) =>
            ElevationPrinter.Collect(document, "section", planeView: section, isDetail: detail,
                cropWindow: detail ? SheetComposer.CropContentRect(section.ViewCrop!) : null)!;

        var content = Collect(view);
        Assert.Single(content.Faces);
        Assert.Contains(content.Edges, edge => edge.CategoryId == wall.CategoryId);
        var both = Collect(other);
        Assert.Equal(2, both.Faces.Count);
        Assert.DoesNotContain(both.Edges, edge => edge.CategoryId == wall.CategoryId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Collect_FineSectionKeepsTheExposedEndsOfASlopingWallEdge(bool detailCallout)
    {
        var view = SectionView(900, [0, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 50);
        view.ViewCrop = new ViewCropFrame(0, 0, 0, [0, 1, 0], [0, 0, 1], [-1, 0, 0], -1, -1, 11, 8, true);
        var wall = QuadWall(600, [[-2, 0, 0], [-2, 10, 0], [-2, 10, 6], [-2, 0, 2]], [1, 0, 0]);
        var cover = QuadWall(601, [[-1, 3, 0], [-1, 7, 0], [-1, 7, 7], [-1, 3, 7]], [1, 0, 0]);
        cover.Geometry!.Faces[0].PlaneSurface = true;
        var document = Document(view, wall, cover);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        if (detailCallout)
        {
            view.SectionViewerId = 901;
            document.RawData.SectionViews = new Dictionary<int, (int, double, IReadOnlyList<double[]>?)>
            {
                [901] = (899, 0.02, null),
            };
        }

        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;
        var slopes = content.Edges.Where(edge => Math.Abs(edge.Y1 - edge.Y2) > 0.1 && Math.Abs(edge.X1 - edge.X2) > 0.1)
            .Select(edge => (Left: Math.Min(edge.X1, edge.X2), Right: Math.Max(edge.X1, edge.X2)))
            .OrderBy(edge => edge.Left).ToArray();

        Assert.Equal(2, slopes.Length);
        Assert.Equal(0, slopes[0].Left, 5);
        Assert.Equal(3 * FeetToMillimetres, slopes[0].Right, 5);
        Assert.Equal(7 * FeetToMillimetres, slopes[1].Left, 5);
        Assert.Equal(10 * FeetToMillimetres, slopes[1].Right, 5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Collect_CalloutClipsSlopingWallSpansWithTheirOriginalFaceDepths(bool reverse)
    {
        var view = SectionView(900, [0, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 20);
        view.ViewCrop = new ViewCropFrame(0, 0, 0, [0, 1, 0], [0, 0, 1], [-1, 0, 0], -1, -1, 11, 8, true);
        view.SectionViewerId = 901;
        var wall = QuadWall(600, [[-4, 0, 0], [-4, 10, 0], [-4, 10, 6], [-4, 0, 2]], [1, 0, 0]);
        var cover = QuadWall(601, [[-1, 3, 0], [-1, 7, 0], [-1, 7, 7], [-1, 3, 7]], [1, 0, 0]);
        var pane = QuadWall(602, [[-2, 0, 0], [-2, 3, 0], [-2, 3, 7], [-2, 0, 7]], [1, 0, 0],
            (int)BuiltInCategory.OST_Windows);
        var glass = new Element { Id = 603, SerializedMaterialTransparency = 0.5 };
        cover.Geometry!.Faces[0].PlaneSurface = true;
        pane.Geometry!.Faces[0].PlaneSurface = true;
        pane.Geometry.Faces[0].RenderStyleId = glass.Id;
        Element[] elements = reverse ? [view, cover, pane, wall, glass] : [view, wall, pane, cover, glass];
        var document = Document(elements);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        document.RawData.SectionViews = new Dictionary<int, (int, double, IReadOnlyList<double[]>?)>
        {
            [901] = (899, 0.02, null),
        };

        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;
        var slope = Assert.Single(content.Edges, edge => Math.Abs(edge.X1 - edge.X2) > 0.1
            && Math.Abs(edge.Y1 - edge.Y2) > 0.1);
        Assert.Equal(7 * FeetToMillimetres, Math.Min(slope.X1, slope.X2), 5);
        Assert.Equal(10 * FeetToMillimetres, Math.Max(slope.X1, slope.X2), 5);
    }

    [Theory]
    [InlineData(true, true, false, 1)]
    [InlineData(false, true, false, 2)]
    [InlineData(true, false, true, 1)]
    [InlineData(true, false, false, 2)]
    public void Collect_FineSectionSkipsNonProjectGeometryWhenItsSourceIsKnown(bool exactTable, bool discovered,
        bool fillPattern, int faceCount)
    {
        var view = SectionView(900, [0, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 50);
        view.ViewCrop = new ViewCropFrame(0, 0, 0, [0, 1, 0], [0, 0, 1], [-1, 0, 0], -5, -5, 5, 5, true);
        var wall = QuadWall(600, [[-2, 0, 0], [-2, 4, 0], [-2, 4, 3], [-2, 0, 3]], [1, 0, 0]);
        var overlay = QuadWall(601, [[-1, -1, -1], [-1, 5, -1], [-1, 5, 4], [-1, -1, 4]], [1, 0, 0],
            fillPattern ? (int)BuiltInCategory.OST_Lines : (int)BuiltInCategory.OST_GenericModel);
        overlay.IsDiscovered = discovered;
        overlay.NativeRootClassName = fillPattern ? "FillPatternElem" : null;
        var document = Document(view, wall, overlay);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        document.RawData.ElemTableIsExact = exactTable;

        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;

        Assert.Equal(faceCount, content.Faces.Count);
        Assert.Equal(faceCount == 1, content.Edges.Any(edge => edge.CategoryId == wall.CategoryId));
    }

    [Theory]
    [InlineData(true, "Family", 1)]
    [InlineData(false, "Family", 2)]
    [InlineData(true, "FamilyInstance", 2)]
    public void Collect_FineDetailUsesPlacedGeometryInsteadOfFamilyDefinition(bool exactTable, string ownerClass, int faces)
    {
        var view = SectionView(900, [0, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 20);
        var placed = QuadWall(600, [[-2, 0, 0], [-2, 4, 0], [-2, 4, 3], [-2, 0, 3]], [1, 0, 0]);
        var nested = QuadWall(601, [[-1, 0, 0], [-1, 4, 0], [-1, 4, 4], [-1, 0, 4]], [1, 0, 0]);
        var owner = new Element { Id = 602, NativeRootClassName = ownerClass, NativeRootClassChain = [ownerClass] };
        nested.SerializedOwningElementId = owner.Id;
        var document = Document(view, placed, nested, owner);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        document.RawData.ElemTableIsExact = exactTable;

        var content = ElevationPrinter.Collect(document, "section", planeView: view, isDetail: true,
            cropWindow: (-500, -500, 1500, 1500), viewportScale: 20)!;

        Assert.Equal(faces, content.Faces.Count);
        Assert.Equal(faces == 1 ? 3 : 4, content.Faces.Max(face => face.Points.Max(point => point.Y)) / FeetToMillimetres, 6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Collect_KeepsTheNearestVisibleCoincidentTreadInOverviewSection(bool covered)
    {
        var view = new Element
        {
            Id = 900, ViewOrigin = [0, 0, 0], ViewDirection = [0, 1, 0],
            ViewHorizontalDirection = [1, 0, 0], ViewVerticalDirection = [0, 0, 1],
        };
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 100);
        int stair = (int)BuiltInCategory.OST_Stairs;
        Element Tread(int id, double depth) => QuadWall(id,
            [[0, depth, 1], [4, depth, 1], [4, depth, 2], [0, depth, 2]], [0, -1, 0], stair);
        var close = Tread(603, 2);
        var middle = Tread(602, 4);
        var far = Tread(601, 6);
        var wall = QuadWall(604, [[-1, 1, 0], [5, 1, 0], [5, 1, 3], [-1, 1, 3]], [0, -1, 0]);
        var document = covered ? Document(view, far, middle, close, wall) : Document(view, far, middle, close);

        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;
        var projected = content.Edges.Where(edge => edge.CategoryId == stair && !edge.IsCut
            && Math.Abs(edge.Y1 - 2 * FeetToMillimetres) < 1e-6 && Math.Abs(edge.Y2 - edge.Y1) < 1e-6);
        if (covered)
        {
            Assert.Empty(projected);
            return;
        }
        var visible = Assert.Single(projected);
        Assert.Equal(2 * FeetToMillimetres, visible.MidDepth, 6);
        Assert.Equal(4 * FeetToMillimetres, Math.Abs(visible.X2 - visible.X1), 6);
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_Walls)]
    [InlineData(BuiltInCategory.OST_Stairs)]
    public void Collect_UsesLocalDepthForAnObliqueTreadOccluder(BuiltInCategory category)
    {
        var view = new Element
        {
            Id = 900, ViewOrigin = [0, 0, 0], ViewDirection = [0, 1, 0],
            ViewHorizontalDirection = [1, 0, 0], ViewVerticalDirection = [0, 0, 1],
        };
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 100);
        var blocker = QuadWall(601, [[0, -1, 0], [4, 3, 0], [4, 3, 3], [0, -1, 3]], [1, -1, 0], (int)category);
        blocker.Geometry!.Faces[0].PlaneSurface = true;
        int stair = (int)(category == BuiltInCategory.OST_Stairs ? BuiltInCategory.OST_StairsRuns : BuiltInCategory.OST_Stairs);
        Element Tread(int id, double left, double right) => QuadWall(id,
            [[left, 1.6, 1], [right, 1.6, 1], [right, 1.6, 2], [left, 1.6, 2]], [0, -1, 0], stair);
        var hidden = Tread(602, 1.2, 2);
        var exposed = Tread(603, 3, 3.8);
        if (category == BuiltInCategory.OST_Stairs)
            hidden.SerializedOwningElementId = exposed.SerializedOwningElementId = blocker.Id;
        var document = Document(view, blocker, hidden, exposed);

        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;
        var top = Assert.Single(content.Edges, edge => edge.CategoryId == stair
            && Math.Abs(edge.Y1 - 2 * FeetToMillimetres) < 1e-6 && Math.Abs(edge.Y2 - edge.Y1) < 1e-6);
        Assert.Equal(3 * FeetToMillimetres, Math.Min(top.X1, top.X2), 6);
        Assert.Equal(3.8 * FeetToMillimetres, Math.Max(top.X1, top.X2), 6);
    }

    [Theory]
    [InlineData(4.0)]
    [InlineData(1.125)]
    public void Collect_ClipsThePortionOfAVisibleSectionEdgeBehindAStair(double edgeEnd)
    {
        var view = SectionView(900, [0, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 20);
        var tread = QuadWall(600, [[-1, 0, 0], [-1, 1, 0], [-1, 1, 3], [-1, 0, 3]], [1, 0, 0],
            (int)BuiltInCategory.OST_StairsTrisers);
        tread.Geometry!.Faces[0].PlaneSurface = true;
        var behind = QuadWall(601, [[-3, 0.5, 1], [-3, edgeEnd, 1], [-3, edgeEnd, 2], [-3, 0.5, 2]], [1, 0, 0],
            (int)BuiltInCategory.OST_Furniture);
        var document = Document(view, tread, behind);

        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;
        var edges = content.Edges.Where(edge => edge.CategoryId == behind.CategoryId).ToList();

        Assert.Equal(3, edges.Count);
        Assert.All(edges, edge => Assert.True(Math.Min(edge.X1, edge.X2) >= FeetToMillimetres - 1e-6));
        Assert.Equal(2, edges.Count(edge => Math.Abs(Math.Min(edge.X1, edge.X2) - FeetToMillimetres) < 1e-6));
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_Viewports, false)]
    [InlineData(BuiltInCategory.OST_Constraints, true)]
    [InlineData(BuiltInCategory.OST_CLines, true)]
    public void Collect_SectionOmitsAnnotationMeshes(BuiltInCategory category, bool discovered)
    {
        var view = SectionView(900, [0, 0, 0]);
        var wall = QuadWall(600, [[-2, 0, 0], [-2, 4, 0], [-2, 4, 3], [-2, 0, 3]], [1, 0, 0]);
        var annotation = QuadWall(601, [[-1, -1, -1], [-1, 5, -1], [-1, 5, 4], [-1, -1, 4]],
            [1, 0, 0], (int)category);
        annotation.IsDiscovered = discovered;

        var content = ElevationPrinter.Collect(Document(view, wall, annotation), "section", planeView: view)!;
        Assert.Single(content.Faces);
        Assert.Equal(4, content.Edges.Count);
        Assert.All(content.Edges, edge => Assert.Equal(wall.CategoryId, edge.CategoryId));
    }

    [Fact]
    public void Collect_SectionKeepsNearerCoincidentSkylightFrame()
    {
        var view = SectionView(900, [0, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 50);
        var roof = QuadWall(600, [[-8, 0, 0], [-8, 4, 0], [-8, 4, 3], [-8, 0, 3]],
            [1, 0, 0], (int)BuiltInCategory.OST_Roofs);
        var skylight = QuadWall(601, [[-5, 1, 1], [-5, 2, 1], [-5, 2, 2], [-5, 1, 2]],
            [1, 0, 0], (int)BuiltInCategory.OST_Windows);
        skylight.SerializedHostId = roof.Id;
        skylight.Geometry!.Faces.Add(QuadWall(602, [[-1, 1, 1], [-1, 2, 1], [-1, 2, 2], [-1, 1, 2]],
            [1, 0, 0]).Geometry!.Faces[0]);

        var content = ElevationPrinter.Collect(Document(view, roof, skylight), "section", planeView: view)!;
        var edges = content.Edges.Where(edge => edge.CategoryId == skylight.CategoryId).ToList();
        Assert.Equal(4, edges.Count);
        Assert.All(edges, edge => Assert.Equal(FeetToMillimetres, edge.MidDepth, 6));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(80, true)]
    public void Collect_SectionShowsSkylightFrameThroughGlazing(double transparency, bool visible)
    {
        var view = SectionView(900, [0, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 50);
        var roof = QuadWall(600, [[-8, 0, 0], [-8, 4, 0], [-8, 4, 4], [-8, 0, 4]],
            [1, 0, 0], (int)BuiltInCategory.OST_Roofs);
        var skylight = QuadWall(601, [[-2, 1, 1], [-2, 2, 1], [-2, 2, 2], [-2, 1, 2]],
            [1, 0, 0], (int)BuiltInCategory.OST_Windows);
        skylight.SerializedHostId = roof.Id;
        var pane = QuadWall(602, [[-1, 0, 0], [-1, 3, 0], [-1, 3, 3], [-1, 0, 3]],
            [1, 0, 0]).Geometry!.Faces[0];
        var material = new Element { Id = 603, SerializedMaterialTransparency = transparency };
        pane.PlaneSurface = true;
        pane.RenderStyleId = material.Id;
        skylight.Geometry!.Faces.Add(pane);

        var content = ElevationPrinter.Collect(Document(view, roof, skylight, material), "section", planeView: view)!;
        Assert.Equal(visible ? 4 : 0, content.Edges.Count(edge => edge.CategoryId == skylight.CategoryId
            && Math.Abs(edge.MidDepth - 2 * FeetToMillimetres) < 1e-6));
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(0.25, 8)]
    public void Collect_ElevationHidesCoveredSkylightFrameWhenGlazingIsEdgeOn(double slope, int visibleEdges)
    {
        var roof = QuadWall(600, [[0, 4, 0], [4, 4, 0], [4, 4, 4], [0, 4, 4]],
            [0, 1, 0], (int)BuiltInCategory.OST_Roofs);
        var skylight = QuadWall(601, [[1, 2, 1], [2, 2, 1], [2, 2, 2], [1, 2, 2]],
            [0, 1, 0], (int)BuiltInCategory.OST_Windows);
        skylight.SerializedHostId = roof.Id;
        var frame = QuadWall(602, [[0, 1, 0], [3, 1, 0], [3, 1, 3], [0, 1, 3]],
            [0, 1, 0]).Geometry!.Faces[0];
        frame.PlaneSurface = true;
        skylight.Geometry!.Faces.Add(frame);
        var glass = new Element { Id = 603, SerializedMaterialTransparency = 80 };
        var normalLength = Math.Sqrt(1.25 + slope * slope);
        var pane = QuadWall(604,
            [[1, 1.2, 1.25], [2, 1.2, 1.75], [2, 1.8, 1.75 + 0.6 * slope], [1, 1.8, 1.25 + 0.6 * slope]],
            [-0.5 / normalLength, -slope / normalLength, 1 / normalLength]).Geometry!.Faces[0];
        pane.PlaneSurface = true;
        pane.RenderStyleId = glass.Id;
        skylight.Geometry.Faces.Add(pane);

        var content = ElevationPrinter.Collect(Document(roof, skylight, glass), "south")!;

        Assert.Equal(visibleEdges, content.Edges.Count(edge => edge.CategoryId == skylight.CategoryId));
    }

    [Fact]
    public void Collect_KeepsWindowFrameBehindItsOwnFace()
    {
        var glazing = QuadWall(601, [[0, 0, 0], [4, 0, 0], [4, 0, 3], [0, 0, 3]],
            [0, 1, 0], (int)BuiltInCategory.OST_Windows);
        var frame = QuadWall(602, [[1, 1, 1], [2, 1, 1], [2, 1, 2], [1, 1, 2]], [0, 1, 0]);
        glazing.Geometry!.Faces!.Add(frame.Geometry!.Faces![0]);

        var content = ElevationPrinter.Collect(Document(glazing), "south")!;

        Assert.Equal(8, content.Edges.Count);
    }

    [SkippableFact]
    public void SampleFile_DetailKeepsItsLevelRows()
    {
        // A crop-drawn detail keeps the levels whose height falls in its crop: the reference details draw
        // their own level rows with markers (Typ. Wall Section shows all six), clipped to the crop at draw
        // time. Window-only details (no file crop) still drop them.
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var view = document.Elements.FirstOrDefault(e => e.CategoryId == (int)BuiltInCategory.OST_Views
            && e.Name == "Typical Wall Roof Connection");
        Assert.NotNull(view);
        if (view!.ViewCrop is not { Active: true } crop)
            throw new SkipException("The sample's detail has no active crop.");

        var content = ElevationPrinter.Collect(document, "section", false, view,
            isDetail: true, cropWindow: SheetComposer.CropContentRect(crop));

        Assert.NotNull(content);
        Assert.Contains(content!.Levels, level => level.Label == "Roof Line" && level.Y == 6000);
    }

    [Fact]
    public void DrawContent_WritesTheProjectedPolygons()
    {
        var document = Document(WallWithFace(600));
        var content = ElevationPrinter.Collect(document, "south")!;
        var pdf = new PdfDocument();
        PdfPage page = pdf.AddPage(PdfPageSize.A1.Landscape);

        ElevationPrinter.DrawContent(page, content, 100, 10, 10);
        using var stream = new MemoryStream();
        pdf.Save(stream);
        string text = PdfTestContent.Read(stream.ToArray());

        // The face fills without stroking its loop ("f"); the shared edge set inks the outline (" l ").
        // A single quad face keeps its four boundary edges.
        Assert.Contains(" l ", text);
        Assert.Contains(" f\n", text);
        Assert.Equal(4, content.Edges.Count);
    }

    [Fact]
    public void Collect_RemovesTheSharedDiagonalOfCoplanarFaces()
    {
        // Two side-by-side quads in one plane: the shared edge is interior triangulation, not inked.
        var document = Document(
            QuadWall(600, [[0, 0, 0], [4, 0, 0], [4, 0, 3], [0, 0, 3]], [0, 1, 0]),
            QuadWall(601, [[4, 0, 0], [8, 0, 0], [8, 0, 3], [4, 0, 3]], [0, 1, 0]));

        var content = ElevationPrinter.Collect(document, "south")!;

        Assert.Equal(2, content.Faces.Count);
        Assert.Equal(6, content.Edges.Count);
    }

    [Theory]
    [InlineData(9, -1)]
    [InlineData(7, 4)]
    public void Collect_KeepsTheUncoveredSpanOfAWallEdgeInAnOpaqueDetail(double coverDepth, double visibleEnd)
    {
        var view = SectionView(610, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 20);
        var wall = QuadWall(611, [[8, -4, 2], [8, 4, 2], [8, 4, 5], [8, -4, 5]], [1, 0, 0]);
        var cover = QuadWall(612,
            [[coverDepth, -1, 3], [coverDepth, 4, 3], [coverDepth, 4, 6], [coverDepth, -1, 6]], [1, 0, 0]);
        var document = Document(view, wall, cover);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };

        var content = Assert.IsType<ElevationPrinter.ElevationContent>(ElevationPrinter.Collect(document, "section",
            planeView: view, isDetail: true, cropWindow: (-1600, 0, 1600, 2300), viewportScale: 20));

        var edge = Assert.Single(content.Edges, edge =>
            Math.Abs(edge.Y1 - 5 * FeetToMillimetres) < 1e-6 && Math.Abs(edge.Y1 - edge.Y2) < 1e-6);
        Assert.Equal(-4 * FeetToMillimetres, Math.Min(edge.X1, edge.X2), 6);
        Assert.Equal(visibleEnd * FeetToMillimetres, Math.Max(edge.X1, edge.X2), 6);
    }

    [Fact]
    public void Collect_KeepsTheExposedEdgeOfOverlappingObliqueWallsInAnOpaqueDetail()
    {
        var view = SectionView(620, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 20);
        var wall = QuadWall(621, [[8.8, -4, 2], [7.2, 4, 2], [7.2, 4, 5], [8.8, -4, 5]], [1, 0.2, 0]);
        var overlap = QuadWall(622, [[8.8, -4, 1], [7.4, 3, 1], [7.4, 3, 5], [8.8, -4, 5]], [1, 0.2, 0]);
        var cover = QuadWall(623, [[9.5, -1, 3], [9.5, 4, 3], [9.5, 4, 6], [9.5, -1, 6]], [1, 0, 0]);
        var document = Document(view, wall, overlap, cover);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };

        var content = Assert.IsType<ElevationPrinter.ElevationContent>(ElevationPrinter.Collect(document, "section",
            planeView: view, isDetail: true, cropWindow: (-1600, 0, 1600, 2300), viewportScale: 20));

        var edge = Assert.Single(content.Edges, edge =>
            Math.Abs(edge.Y1 - 5 * FeetToMillimetres) < 1e-6 && Math.Abs(edge.Y1 - edge.Y2) < 1e-6);
        Assert.Equal(-4 * FeetToMillimetres, Math.Min(edge.X1, edge.X2), 6);
        Assert.Equal(-FeetToMillimetres, Math.Max(edge.X1, edge.X2), 6);
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_Walls, 8, true)]
    [InlineData(BuiltInCategory.OST_Walls, 7.2, false)]
    [InlineData(BuiltInCategory.OST_Floors, 8, true)]
    [InlineData(BuiltInCategory.OST_Floors, 7.2, false)]
    [InlineData(BuiltInCategory.OST_Stairs, 8, true)]
    [InlineData(BuiltInCategory.OST_Stairs, 7.2, false)]
    public void Collect_ResolvesTheDepthOfAnObliqueDetailOccluder(
        BuiltInCategory coverCategory, double frameDepth, bool visible)
    {
        var view = SectionView(630, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 20);
        var cover = QuadWall(631, [[8.8, -4, 0], [7.2, 4, 0], [7.2, 4, 5], [8.8, -4, 5]],
            [1, 0.2, 0], (int)coverCategory);
        var frame = QuadWall(632, [[frameDepth, 1.5, 1], [frameDepth, 2.5, 1],
            [frameDepth, 2.5, 4], [frameDepth, 1.5, 4]], [1, 0, 0],
            (int)BuiltInCategory.OST_CurtainWallMullions);
        var document = Document(view, cover, frame);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };

        var content = ElevationPrinter.Collect(document, "section", planeView: view,
            isDetail: true, cropWindow: (-1400, -100, 1400, 1650), viewportScale: 20)!;

        Assert.Equal(visible ? 4 : 0, content.Edges.Count(edge => edge.CategoryId == frame.CategoryId));
    }

    [Fact]
    public void Collect_KeepsTheVisibleStairOutlineAtCoincidentScreenCoordinates()
    {
        var view = SectionView(640, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 20);
        var far = QuadWall(641, [[7, 0, 1], [7, 2, 1], [7, 2, 3], [7, 0, 3]], [1, 0, 0],
            (int)BuiltInCategory.OST_StairsRuns);
        var near = QuadWall(642, [[8, 0, 1], [8, 2, 1], [8, 2, 3], [8, 0, 3]], [1, 0, 0],
            (int)BuiltInCategory.OST_StairsRuns);
        var wall = QuadWall(643, [[7.5, -1, 0], [7.5, 3, 0], [7.5, 3, 4], [7.5, -1, 4]], [1, 0, 0]);
        var document = Document(view, far, near, wall);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };

        var content = ElevationPrinter.Collect(document, "section", planeView: view,
            isDetail: true, cropWindow: (-400, -100, 1000, 1300), viewportScale: 20)!;

        var outlines = content.Edges.Where(edge => edge.CategoryId == near.CategoryId).ToList();
        Assert.Equal(4, outlines.Count);
        Assert.All(outlines, edge => Assert.Equal(-8 * FeetToMillimetres, edge.MidDepth, 6));
    }

    [Fact]
    public void Collect_KeepsTheCreaseBetweenDifferentPlanes()
    {
        // Two quads sharing an edge at 45 degrees: the crease is drawn once, not twice and not removed --
        // but the leaning-back sides hide on the silhouette of the front wall, leaving its four segments.
        var document = Document(
            QuadWall(600, [[0, 0, 0], [4, 0, 0], [4, 0, 3], [0, 0, 3]], [0, 1, 0]),
            QuadWall(601, [[0, 0, 0], [4, 0, 0], [4, 1, 1], [0, 1, 1]], [0, -0.7071, 0.7071]));

        var content = ElevationPrinter.Collect(document, "south")!;

        Assert.Equal(2, content.Faces.Count);
        Assert.Equal(4, content.Edges.Count);
    }

    private static Element ProjectLevel(int id, string name, double elevationFeet) => Level(id, name, elevationFeet, "Level");

    private static Element FamilyLevel(int id, string name, double elevationFeet) => Level(id, name, elevationFeet, null);

    private static Element Level(int id, string name, double elevationFeet, string? rootClassName)
    {
        var level = new Element
        {
            Id = id,
            CategoryId = (int)BuiltInCategory.OST_Levels,
            NativeRootClassName = rootClassName,
            Name = name,
        };
        Add(level, BuiltInParameter.LEVEL_ELEV, StorageType.Double, elevationFeet);
        return level;
    }

    [Fact]
    public void Collect_ShowsOnlyProjectLevels()
    {
        // The families each carry their own reference levels under the same category; an elevation notes
        // only the level records of the project, not the family ones.
        var document = Document(
            WallWithFace(600),
            ProjectLevel(700, "Level 1", 0),
            FamilyLevel(701, "Ref. Level", 0.5));

        var content = ElevationPrinter.Collect(document, "south")!;

        var level = Assert.Single(content.Levels);
        Assert.Equal("Level 1", level.Label);
    }

    private static ElevationPrinter.CollectedEdge H(double x1, double x2, double y, double depth) =>
        new(x1, y, x2, y, depth, depth, 0, -1, 0, 0, 1, 0);

    [Fact]
    public void Collect_FineSectionKeepsADoorEdgeWhenItsCoincidentWallEdgeIsHidden()
    {
        var view = SectionView(900, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 20);
        var rear = QuadWall(901, [[7, 0, 0], [7, 4, 0], [7, 4, 4], [7, 0, 4]], [1, 0, 0]);
        var cover = QuadWall(902, [[7.5, -1, -1], [7.5, 5, -1], [7.5, 5, 5], [7.5, -1, 5]], [1, 0, 0]);
        int doorCategory = (int)BuiltInCategory.OST_Doors;
        var door = QuadWall(903, [[8, 0, 0], [8, 4, 0], [8, 4, 4], [8, 0, 4]], [1, 0, 0], doorCategory);
        var document = Document(view, rear, cover, door);

        var content = ElevationPrinter.Collect(document, "section", planeView: view, viewportScale: 20)!;

        Assert.Contains(content.Edges, edge => edge.CategoryId == doorCategory
            && edge.X1 == 0 && edge.X2 == 0 && Math.Abs(edge.Y1 - edge.Y2) > 1000);
    }

    [Fact]
    public void DrawContent_InksAnExactlyOverlappingClippedDoorSpanOnce()
    {
        var doorCategory = (int)BuiltInCategory.OST_Doors;
        var edge = new ElevationPrinter.CollectedEdge(50, 20, 50, 200, 100, 100, 0, -1, 0, 0, 0, 0,
            CategoryId: doorCategory);
        var farther = edge with { Y2 = 150, MidDepth = 200, Depth = 200 };
        var content = new ElevationPrinter.ElevationContent([], [], [], [], [edge, farther], 0, 0, 100, 200,
            KeepDoorDepths: true);
        var page = new PdfDocument().AddPage(PdfPageSize.A1.Landscape);

        ElevationPrinter.DrawContent(page, content, 20, 0, 0, 0.82, clipRect: (0, 0, 100, 100));

        Assert.Equal(1, CountOccurrences(page.Content, " l S\n"));
    }

    [Theory]
    [InlineData(true, false, 0.0, false, true)]
    [InlineData(true, false, 0.0, true, false)]
    [InlineData(true, false, 0.8, false, false)]
    [InlineData(true, true, 0.0, false, false)]
    [InlineData(false, false, 0.0, false, false)]
    public void Collect_UsesTheAuthoredOpaqueDoorFinishInAnAmbientFineSection(
        bool showAmbientShadows, bool useRenderAppearance, double transparency, bool glazedDoor, bool expected)
    {
        var view = SectionView(900, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 20);
        view.ViewCrop = new ViewCropFrame(10, 0, 0, [0, 1, 0], [0, 0, 1], [-1, 0, 0], -1, -1, 5, 5, true);
        var finish = new Element
        {
            Id = 901, HasShadingAppearance = true, SerializedMaterialUsesRenderAppearance = useRenderAppearance,
            SerializedMaterialTransparency = transparency,
        };
        Add(finish, BuiltInParameter.MATERIAL_PARAM_COLOR, StorageType.Integer, 0xb4b4b4);
        var door = QuadWall(902, [[8, 0, 0], [8, 4, 0], [8, 4, 4], [8, 0, 4]], [1, 0, 0],
            (int)BuiltInCategory.OST_Doors);
        var panel = Assert.Single(door.Geometry!.Faces);
        panel.PlaneSurface = true;
        panel.NestedRenderStyleId = finish.Id;
        var glass = new Element { Id = 903, HasShadingAppearance = true, SerializedMaterialTransparency = 0.9 };
        if (glazedDoor)
        {
            var pane = Assert.Single(QuadWall(904, [[8, 1, 1], [8, 2, 1], [8, 2, 3], [8, 1, 3]],
                [1, 0, 0]).Geometry!.Faces);
            pane.PlaneSurface = true;
            pane.NestedRenderStyleId = glass.Id;
            door.Geometry!.Faces.Add(pane);
        }
        var document = Document(view, finish, door, glass);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        if (showAmbientShadows) document.RawData.ViewAmbientFractions = new Dictionary<int, double> { [view.Id] = 0.5 };

        var section = ElevationPrinter.Collect(document, "section", planeView: view, viewportScale: 20)!;

        if (expected)
            Assert.Equal(180 / 255d, Assert.Single(section.FaceFills!).Value!.Value, 10);
        else
            Assert.Null(section.FaceFills);
    }

    [Theory]
    [InlineData(true, true, false, 0, 5, true)]
    [InlineData(true, true, false, 0, 4, true)]
    [InlineData(true, true, false, 0, 1, false)]
    [InlineData(false, true, false, 0, 5, false)]
    [InlineData(true, false, false, 0, 5, false)]
    [InlineData(true, true, null, 0, 5, false)]
    [InlineData(true, true, true, 0, 5, false)]
    [InlineData(true, true, false, 0.5, 5, false)]
    public void Collect_ShadesTheSavedWallFaceFinishInAnAmbientFineSection(
        bool callout, bool ambientShadows, bool? useRenderAppearance, double transparency, int layerFunction, bool expected)
    {
        var view = SectionView(900, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 20);
        view.ViewCrop = new ViewCropFrame(10, 0, 0, [0, 1, 0], [0, 0, 1], [-1, 0, 0], -1, -1, 5, 5, true);
        view.SectionViewerId = 903;
        var finish = new Element
        {
            Id = 901, HasShadingAppearance = true, SerializedMaterialUsesRenderAppearance = useRenderAppearance,
            SerializedMaterialTransparency = transparency,
        };
        Add(finish, BuiltInParameter.MATERIAL_PARAM_COLOR, StorageType.Integer, 0xdbdbdb);
        var wall = QuadWall(902, [[8, 0, 0], [8, 4, 0], [8, 4, 4], [8, 0, 4]], [1, 0, 0]);
        var face = Assert.Single(wall.Geometry!.Faces);
        face.PlaneSurface = true;
        face.RenderStyleId = finish.Id;
        var wallType = new Element
        {
            Id = 905,
            SerializedHostTypeLayers = [new(BitConverter.DoubleToInt64Bits(0.05), layerFunction, 0, finish.Id, -1, 0, 0)],
        };
        wall.TypeId = wallType.Id;
        var document = Document(view, wall, finish, wallType);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        if (callout)
            document.RawData.SectionViews = new Dictionary<int, (int, double, IReadOnlyList<double[]>?)>
            {
                [903] = (904, 50, null),
            };
        if (ambientShadows) document.RawData.ViewAmbientFractions = new Dictionary<int, double> { [view.Id] = 0.5 };

        var section = ElevationPrinter.Collect(document, "section", planeView: view, viewportScale: 20)!;

        if (expected)
            Assert.Equal(219 / 255d, Assert.Single(section.FaceFills!).Value!.Value, 10);
        else
            Assert.Null(section.FaceFills);
    }

    [Theory]
    [InlineData(20, 14, true, true, false, 0, true)]
    [InlineData(20, 22, true, true, false, 0, false)]
    [InlineData(10, 14, true, true, false, 0, false)]
    [InlineData(20, 14, true, false, false, 0, false)]
    [InlineData(20, 14, false, true, false, 0, false)]
    [InlineData(20, 14, true, true, true, 0, false)]
    [InlineData(20, 14, true, true, false, 0.5, false)]
    public void Collect_ShadesAWhiteProjectedStructuralSlabSideThroughTheSection(
        double cutX, double blockerX, bool ambientShadows, bool structural, bool useRenderAppearance,
        double transparency, bool expected)
    {
        var view = SectionView(900, [cutX, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 20);
        view.ViewCrop = new ViewCropFrame(cutX, 0, 0, [0, 1, 0], [0, 0, 1], [-1, 0, 0], -1, -1, 5, 5, true);
        var material = new Element
        {
            Id = 901, HasShadingAppearance = true, SerializedMaterialUsesRenderAppearance = useRenderAppearance,
            SerializedMaterialTransparency = transparency,
        };
        Add(material, BuiltInParameter.MATERIAL_PARAM_COLOR, StorageType.Integer, 0xc0c0c0);
        var slab = QuadWall(902, [[8, 0, 0], [8, 4, 0], [8, 4, 1], [8, 0, 1]], [1, 0, 0],
            (int)BuiltInCategory.OST_Floors);
        Assert.Single(slab.Geometry!.Faces).PlaneSurface = true;
        var top = Assert.Single(QuadWall(903, [[6, 0, 1], [8, 0, 1], [8, 4, 1], [6, 4, 1]], [0, 0, 1]).Geometry!.Faces);
        top.PlaneSurface = true;
        slab.Geometry.Faces.Add(top);
        Add(slab, BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL, StorageType.Integer, structural ? 1 : 0);
        Add(slab, BuiltInParameter.STRUCTURAL_MATERIAL_PARAM, StorageType.ElementId, material.Id);
        var blocker = QuadWall(904, [[blockerX, -10, -10], [blockerX, 10, -10],
            [blockerX, 10, 10], [blockerX, -10, 10]], [1, 0, 0]);
        Assert.Single(blocker.Geometry!.Faces).PlaneSurface = true;
        var document = Document(view, slab, material, blocker);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        if (ambientShadows) document.RawData.ViewAmbientFractions = new Dictionary<int, double> { [view.Id] = 0.5 };

        var section = ElevationPrinter.Collect(document, "section", planeView: view, viewportScale: 20)!;

        Assert.Null(section.FaceFills);
        var side = Assert.Single(section.Faces, face => Math.Abs(face.Depth + 8 * FeetToMillimetres) < 1e-6);
        var tone = section.AmbientTones?.GetValueOrDefault(side.Points);
        Assert.Equal(expected, tone != null);
        if (tone != null) Assert.All(tone.Image.Rgb, sample => Assert.InRange(sample, (byte)188, (byte)254));
    }

    [Theory]
    [InlineData(0, true, true, false)]
    [InlineData(1, true, true, true)]
    [InlineData(null, true, true, true)]
    [InlineData(0, false, true, true)]
    [InlineData(0, true, false, true)]
    public void Collect_UsesProjectedAmbientOccludersForTheInsideFinishOfAnUnattachedExteriorWall(
        int? topAttached, bool matchesFinish, bool edgeOn, bool castsShadow)
    {
        var view = SectionView(900, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 50);
        view.ViewCrop = new(10, 0, 0, [0, 1, 0], [0, 0, 1], [-1, 0, 0], -1, -1, 5, 5, true);
        var wall = QuadWall(902, [[8, 0, 0], [8, 2, 0], [8, 2, 2], [8, 0, 2]], [1, 0, 0]);
        var finish = Assert.Single(wall.Geometry!.Faces);
        finish.PlaneSurface = true;
        finish.RenderStyleId = matchesFinish ? 901 : 906;
        var wallType = new Element
        {
            Id = 905,
            SerializedBasicWallTypeFunction = 1,
            SerializedHostTypeLayers =
            [
                new(BitConverter.DoubleToInt64Bits(0.3), 1, 0, 906, -1, 0, 0),
                new(BitConverter.DoubleToInt64Bits(0.05), 4, 0, 901, -1, 0, 0),
            ],
        };
        wall.TypeId = wallType.Id;
        if (topAttached.HasValue)
            Add(wall, BuiltInParameter.WALL_TOP_IS_ATTACHED, StorageType.Integer, topAttached.Value);
        var blocker = edgeOn
            ? QuadWall(903, [[8.05, -0.25, -2], [9.9, -0.25, -2], [9.9, -0.25, 4], [8.05, -0.25, 4]], [0, 1, 0])
            : QuadWall(903, [[9, -2, -2], [9, 4, -2], [9, 4, 4], [9, -2, 4]], [-1, 0, 0]);
        Assert.Single(blocker.Geometry!.Faces).PlaneSurface = true;
        var document = Document(view, wallType, wall, blocker);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        document.RawData.ViewAmbientFractions = new Dictionary<int, double> { [view.Id] = 0.5 };

        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;
        var receiver = Assert.Single(content.Faces, face => Math.Abs(face.Depth + 8 * FeetToMillimetres) < 1e-6);

        Assert.Equal(castsShadow, content.AmbientTones?.ContainsKey(receiver.Points) == true);
    }

    [Theory]
    [InlineData(true, true, 1, 0.5, true)]
    [InlineData(false, true, 1, 0.5, false)]
    [InlineData(true, false, 1, 0.5, false)]
    [InlineData(true, true, 0, 0.5, false)]
    [InlineData(true, true, 1, 2.0, false)]
    public void Collect_ShadesAProjectedSingleLayerExteriorStructuralWallAboveItsBaseLevel(
        bool structural, bool matchesFace, int wallFunction, double baseLevelElevation, bool shaded)
    {
        var view = SectionView(900, [10, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 50);
        view.ViewCrop = new(10, 0, 0, [0, 1, 0], [0, 0, 1], [-1, 0, 0], -1, -1, 5, 5, true);
        var material = new Element { Id = 901, HasShadingAppearance = true, SerializedMaterialTransparency = 0 };
        var type = new Element
        {
            Id = 902, SerializedBasicWallTypeFunction = wallFunction,
            SerializedHostTypeLayers = [new(BitConverter.DoubleToInt64Bits(0.4), 1, -1, material.Id, -1, 0, 1)],
        };
        var wall = QuadWall(903, [[8, 0, 0], [8, 4, 0], [8, 4, 1], [8, 0, 1]], [1, 0, 0]);
        wall.TypeId = type.Id;
        var face = Assert.Single(wall.Geometry!.Faces);
        face.PlaneSurface = true;
        face.RenderStyleId = matchesFace ? material.Id : null;
        Add(wall, BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT, StorageType.Integer, structural ? 1 : 0);
        Add(wall, BuiltInParameter.STRUCTURAL_MATERIAL_PARAM, StorageType.ElementId, material.Id);
        var baseLevel = new Element { Id = 904, CategoryId = (int)BuiltInCategory.OST_Levels };
        Add(baseLevel, BuiltInParameter.LEVEL_ELEV, StorageType.Double, baseLevelElevation);
        Add(wall, BuiltInParameter.WALL_BASE_CONSTRAINT, StorageType.ElementId, baseLevel.Id);
        var document = Document(view, wall, type, material, baseLevel);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        document.RawData.ViewAmbientFractions = new Dictionary<int, double> { [view.Id] = 0.5 };
        document.RawData.ViewAmbientLight = new Dictionary<int, double> { [view.Id] = 0.75 };

        var section = ElevationPrinter.Collect(document, "section", planeView: view, viewportScale: 50)!;

        if (shaded)
        {
            var tone = Assert.Single(section.ExteriorWallTones!).Value;
            Assert.Equal(baseLevelElevation * FeetToMillimetres, tone.BaseY, 10);
            Assert.Equal(0.75, tone.Light, 10);
        }
        else
            Assert.Null(section.ExteriorWallTones);
        Assert.Null(section.FaceFills);
    }

    [Fact]
    public void ConsolidateCoincident_MergesFourfoldRailingBoards()
    {
        // A railing family drawing one 7178 mm board four times at the same depth: the reference inks it once.
        var edges = new List<ElevationPrinter.CollectedEdge>
        {
            H(0, 7178, 2800, -13053), H(0, 7178, 2800.4, -13053),
            H(0, 7178, 2799.7, -13053), H(0.3, 7178, 2800.1, -13053),
        };

        var merged = ElevationPrinter.ConsolidateCoincident(edges);

        Assert.Single(merged);
        Assert.Equal(0, merged[0].X1, 0);
        Assert.Equal(7178, merged[0].X2, 0);
    }

    [Fact]
    public void ConsolidateCoincident_KeepsBothSidesOfFineStairCut()
    {
        var tread = H(0, 342, 700, -12000) with { IsCut = true, CategoryId = (int)BuiltInCategory.OST_StairsTrisers };
        var opposite = tread with { Y1 = 750, Y2 = 750 };
        var duplicate = tread with { CategoryId = (int)BuiltInCategory.OST_StairsRuns };

        var edges = ElevationPrinter.ConsolidateCoincident([tread, opposite, duplicate], keepFineSectionProfiles: true);

        Assert.Equal(new[] { 700.0, 750.0 }, edges.Select(edge => edge.Y1).Order().ToArray());
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_Doors, 40)]
    [InlineData(BuiltInCategory.OST_StairsRailing, 2.75)]
    public void ConsolidateCoincident_RetainsTheTwoProjectedEdgesOfAVisibleProfile(
        BuiltInCategory category, double separation)
    {
        var outer = new ElevationPrinter.CollectedEdge(0, 0, 0, 2000, 100, 100,
            8, 9, 1000, 0, 0, 200, CategoryId: (int)category);
        var inner = outer with { X1 = separation, X2 = separation, Y1 = 40, Y2 = 1960, FaceA = 9, FaceB = 8 };
        var duplicate = inner with { Y1 = inner.Y2, Y2 = inner.Y1 };
        var unrelated = outer with { X1 = 20, X2 = 20, FaceA = 32, FaceB = 33 };

        var fine = ElevationPrinter.ConsolidateCoincident([outer, inner, duplicate, unrelated],
            keepFineSectionProfiles: true);

        Assert.Equal(new[] { outer, inner }, fine);
        Assert.Equal(outer, Assert.Single(ElevationPrinter.ConsolidateCoincident([outer, inner, duplicate, unrelated])));
    }

    [Theory]
    [InlineData(25, true, true)]
    [InlineData(20, true, false)]
    [InlineData(2.75, true, false)]
    [InlineData(25, false, false)]
    public void ConsolidateCoincident_ElevationKeepsRailProfileWiderThanItsPen(
        double separation, bool sharedFace, bool keepOpposite)
    {
        var edge = H(0, 2000, 1000, 200) with
        {
            CategoryId = (int)BuiltInCategory.OST_StairsRailing,
            FaceA = 8,
        };
        var opposite = edge with { Y1 = edge.Y1 + separation, Y2 = edge.Y2 + separation, FaceA = sharedFace ? 8 : 9 };
        var duplicate = opposite with { X1 = opposite.X2, X2 = opposite.X1 };

        var result = ElevationPrinter.ConsolidateCoincident([edge, opposite, duplicate], elevationRailPenWidth: 20);

        Assert.Equal(keepOpposite ? new[] { edge, opposite } : new[] { edge }, result);
    }

    [Fact]
    public void ConsolidateCoincident_KeepsSeparatedAndDeepLines()
    {
        // A 60 mm offset, a 60 mm depth step (a rail in front of its wall) and a short head on a long
        // wall top all stay: only true duplicates collapse, so distinct linework is never lost.
        var wall = H(0, 10000, 0, -13000);
        var apart = H(0, 10000, 60, -13000);
        var deep = H(0, 10000, 0, -13060);
        var head = H(2000, 4000, 0, -13000);

        Assert.Equal(2, ElevationPrinter.ConsolidateCoincident([wall, apart]).Count);
        Assert.Equal(2, ElevationPrinter.ConsolidateCoincident([wall, deep]).Count);
        Assert.Equal(2, ElevationPrinter.ConsolidateCoincident([wall, head]).Count);
    }

    private static Element DatumLevel(int id, string name, double elevationFeet, double[] envelope)
    {
        var level = Level(id, name, elevationFeet, "Level");
        level.LevelPlane = new GridPlaneData(envelope, [100, 200, elevationFeet], [1, 0, 0], [0, 1, 0]);
        return level;
    }

    [Fact]
    public void Collect_SpansLevelLinesByDatumEnvelope()
    {
        // A level line spans its datum envelope projected on the view's right axis (south looks along
        // +Y, so the envelope's U range at the plane origin maps straight to X); without a decoded
        // plane the line spans the content like before.
        var document = Document(
            WallWithFace(600),
            DatumLevel(700, "Wide", 0, [-10, 0, 20, 5]),
            ProjectLevel(701, "Plain", 3.28084));

        var content = ElevationPrinter.Collect(document, "south")!;

        Assert.Equal(2, content.Levels.Count);
        var wide = content.Levels[0];
        Assert.Equal("Wide", wide.Label);
        // Envelope U -10..20 ft about origin X 100 ft, in millimetres.
        Assert.Equal((100 - 10) * FeetToMillimetres, wide.X0, 3);
        Assert.Equal((100 + 20) * FeetToMillimetres, wide.X1, 3);
        var plain = content.Levels[1];
        Assert.Equal("Plain", plain.Label);
        Assert.Equal(content.MinX, plain.X0, 6);
        Assert.Equal(content.MaxX, plain.X1, 6);
    }

    [SkippableFact]
    public void SampleFile_DecodesLevelDatumPlanes()
    {
        string? path = TestFiles.TryFind("Test_2022.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);

        var levels = document.Elements
            .Where(e => e.CategoryId == (int)BuiltInCategory.OST_Levels && e.RootClassName == "Level")
            .ToList();
        Assert.NotEmpty(levels);
        // Every project level decoded its datum plane with a finite envelope, origin and axes.
        foreach (var level in levels)
        {
            Assert.NotNull(level.LevelPlane);
            var plane = level.LevelPlane!;
            Assert.Equal(4, plane.Envelope.Length);
            Assert.Equal(3, plane.Origin.Length);
            Assert.All(plane.Envelope.Concat(plane.Origin).Concat(plane.XVector).Concat(plane.YVector),
                v => Assert.True(double.IsFinite(v)));
        }
        // The sample's Foundation datum runs past the building, the way the reference draws it.
        var widest = levels.MaxBy(e => e.LevelPlane!.Envelope[2] - e.LevelPlane!.Envelope[0])!;
        Assert.True(widest.LevelPlane!.Envelope[2] - widest.LevelPlane!.Envelope[0] > 150,
            $"widest level span is {widest.LevelPlane.Envelope[2] - widest.LevelPlane.Envelope[0]:0.#} ft");
    }

    [SkippableFact]
    public void SampleFile_ResolvesGridCenterDashes()
    {
        string? path = TestFiles.TryFind("Test_2022.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);

        // The grid type's center-segment style decodes to the file's "Grid Line" pattern,
        // the coarse [36 9 9 9] pt dash the reference draws.
        Assert.True(LinePatterns.TryGetGridDashes(document, out double[] dashes));
        Assert.Equal(new[] { 36.0, 9.0, 9.0, 9.0 }, dashes);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Collect_ClipsOnlyTheOverlappedPartOfAMatchingCoplanarWall(
        bool samePlane, bool sameType, bool sameMaterial)
    {
        var view = SectionView(900, [0, 0, 0]);
        Add(view, BuiltInParameter.VIEW_SCALE, StorageType.Integer, 100);
        Add(view, BuiltInParameter.VIEW_DETAIL_LEVEL, StorageType.Integer, 3);
        view.ViewCrop = new ViewCropFrame(0, 0, 0, [0, 1, 0], [0, 0, 1], [-1, 0, 0], -3, -1, 5, 4, true);
        var wall = QuadWall(901, [[-4, -2, 1], [-4, 4, 1], [-4, 4, 2], [-4, -2, 2]], [1, 0, 0]);
        wall.TypeId = 910;
        wall.Geometry!.Faces[0].PlaneSurface = true;
        wall.Geometry.Faces[0].RenderStyleId = 920;
        double x = samePlane ? -4 : -5;
        var covering = QuadWall(902, [[x, 0, 0], [x, 2, 0], [x, 2, 3], [x, 0, 3]], [1, 0, 0]);
        covering.TypeId = sameType ? wall.TypeId : 911;
        covering.Geometry!.Faces[0].PlaneSurface = true;
        covering.Geometry.Faces[0].RenderStyleId = sameMaterial ? 920 : 921;
        var document = Document(view, wall, covering);
        document.RawData.OpaquePlanViews = new HashSet<int> { view.Id };
        document.RawData.OpaqueViewsWithSurfacePatterns = new HashSet<int> { view.Id };

        var content = ElevationPrinter.Collect(document, "section", planeView: view)!;
        var bottom = content.Edges.Where(edge => edge.CategoryId == (int)BuiltInCategory.OST_Walls
                && Math.Abs(edge.Y1 - FeetToMillimetres) < 1e-6 && Math.Abs(edge.Y2 - FeetToMillimetres) < 1e-6)
            .Select(edge => (Math.Round(Math.Min(edge.X1, edge.X2) / FeetToMillimetres, 6),
                Math.Round(Math.Max(edge.X1, edge.X2) / FeetToMillimetres, 6)))
            .OrderBy(span => span.Item1).ToArray();

        var expected = samePlane && sameType && sameMaterial ? new[] { (-2d, 0d), (2d, 4d) } : [(-2d, 4d)];
        Assert.Equal(expected, bottom);
    }

    [Fact]
    public void Collect_MergesOverlappingCoplanarEdges()
    {
        // Two overlapping coplanar quads: the shared stretch is inked once, not twice.
        var document = Document(
            QuadWall(600, [[0, 0, 0], [4, 0, 0], [4, 0, 3], [0, 0, 3]], [0, 1, 0]),
            QuadWall(601, [[2, 0, 0], [6, 0, 0], [6, 0, 3], [2, 0, 3]], [0, 1, 0]));

        var content = ElevationPrinter.Collect(document, "south")!;

        Assert.Equal(2, content.Faces.Count);
        Assert.Equal(6, content.Edges.Count);
    }

    [Fact]
    public void DrawContent_ClipsEdgesToTheCropRect()
    {
        // A second wall far to the right of the crop rectangle contributes fills but no ink.
        var document = Document(
            QuadWall(600, [[0, 0, 0], [4, 0, 0], [4, 0, 3], [0, 0, 3]], [0, 1, 0]),
            QuadWall(601, [[40, 0, 0], [44, 0, 0], [44, 0, 3], [40, 0, 3]], [0, 1, 0]));
        var content = ElevationPrinter.Collect(document, "south")!;
        Assert.Equal(8, content.Edges.Count);
        var pdf = new PdfDocument();
        PdfPage page = pdf.AddPage(PdfPageSize.A1.Landscape);

        ElevationPrinter.DrawContent(page, content, 100, 10, 10, 1, null, (0, 0, 5000, 3000));
        using var stream = new MemoryStream();
        pdf.Save(stream);
        string text = PdfTestContent.Read(stream.ToArray());

        Assert.Equal(4, CountOccurrences(text, " l S"));
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    [Fact]
    public void SheetComposer_DrawsAnElevationViewport()
    {
        var sheet = new Element { Id = 100, CategoryId = (int)BuiltInCategory.OST_Sheets };
        Add(sheet, BuiltInParameter.SHEET_NUMBER, StorageType.String, "A103");
        Add(sheet, BuiltInParameter.SHEET_NAME, StorageType.String, "Elevations");
        var title = new Element
        {
            Id = 301,
            CategoryId = (int)BuiltInCategory.OST_TitleBlocks,
            SerializedOwnerDbViewId = 100,
            TypeId = 300,
        };
        var type = new Element
        {
            Id = 300,
            CategoryId = (int)BuiltInCategory.OST_TitleBlocks,
            BoundingBox = [0, 0, 0, 841 / FeetToMillimetres, 594 / FeetToMillimetres, 0],
        };
        var viewport = new Element
        {
            Id = 700,
            CategoryId = (int)BuiltInCategory.OST_Viewports,
            SerializedOwnerDbViewId = 100,
            ViewportViewId = 400,
            ViewportOffset = [400 / FeetToMillimetres, 300 / FeetToMillimetres, 0],
        };
        var document = Document(sheet, title, type, viewport, ElevationView(400, "South", 100), WallWithFace(600));

        PdfDocument pdf = SheetComposer.Render(document, 100);
        using var stream = new MemoryStream();
        pdf.Save(stream);
        string text = PdfTestContent.Read(stream.ToArray());

        Assert.Contains(" l ", text);
    }
}
