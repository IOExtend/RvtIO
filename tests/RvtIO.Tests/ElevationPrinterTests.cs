using RvtIO.BuiltIns;
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

    [Fact]
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

    [Fact]
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

    [Fact]
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
        double oyv = (crop.OriginX * crop.Up[0] + crop.OriginY * crop.Up[1] + crop.OriginZ * crop.Up[2]) * 304.8;
        double cy0 = oyv + Math.Min(crop.Y0, crop.Y1) * 304.8, cy1 = oyv + Math.Max(crop.Y0, crop.Y1) * 304.8;
        int inCrop = content.Levels.Count(l => l.Y >= cy0 - 1 && l.Y <= cy1 + 1);
        Assert.Equal(3, inCrop);
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

    [Fact]
    public void Collect_ReturnsNullWithoutAFace()
    {
        Assert.Null(ElevationPrinter.Collect(Document(), "south"));
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

    [Fact]
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

    [Fact]
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
