using System.Globalization;
using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Pdf;
using RvtIO.Print;
using Xunit;
using Xunit.Sdk;

namespace RvtIO.Tests;

/// <summary>The first plan renderer (src/RvtIO/Print): level selection, wall and column drawing, grid clipping, the
/// paper chosen for the drawing, and the errors of a level with nothing to draw.</summary>
public class PlanPrinterTests
{
    [SkippableTheory]
    [InlineData("en-US", "10 m²")]
    [InlineData("ru-RU", "10 м²")]
    public void DrawContent_LocalizesTheFallbackRoomAreaUnit(string cultureName, string expected)
    {
        var font = PdfFonts.TryFind(out _) ?? throw new SkipException("No TrueType font was found on this machine.");
        var content = new PlanPrinter.PlanContent([], [], [], [], [], [], [], [],
            [([(1000, 1000), (9000, 1000), (9000, 9000), (1000, 9000)], "Office", "101", 10 / 0.092903)],
            [], [], 0, 0, 10000, 10000);
        var page = new PdfDocument { Font = font }.AddPage(PdfPageSize.A3);
        var priorCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
            PlanPrinter.DrawContent(page, content, 100, 20, 20);

            string glyphs = string.Concat(expected.Select(character => font.GlyphOf(character).ToString("X4")));
            Assert.Contains($"<{glyphs}> Tj", page.Content);
        }
        finally
        {
            CultureInfo.CurrentUICulture = priorCulture;
        }
    }

    [Theory]
    [InlineData(true, "1 1 1 rg\n")]
    [InlineData(false, "0.72 0.72 0.72 rg\n")]
    public void DrawContent_UsesTheSavedUnshadedWallFill(bool unshaded, string expectedFill)
    {
        var info = new RevitFileInfo
        {
            Elements = { Level(10, "Level 1", 0), Wall(20, 10, 0, 0, 10, 0) },
            UnshadedHiddenLineViews = unshaded ? new HashSet<int> { 30 } : new HashSet<int>(),
        };
        var document = RvtDocument.FromInfo(info);
        var content = Assert.IsType<PlanPrinter.PlanContent>(PlanPrinter.CollectLevel(document, 10));
        var pdf = new PdfDocument();
        PdfPage page = pdf.AddPage(PdfPageSize.A1.Landscape);
        PlanPrinter.DrawContent(page, content, 100, 20, 20, document: document, planViewId: 30);
        Assert.Contains(expectedFill, page.Content);
    }

    [Theory]
    [InlineData(40, false)]
    [InlineData(41, true)]
    public void DrawContent_UsesTheNativeMitreAndKeepsWallTypeBoundaries(int northWallType, bool visibleSeam)
    {
        var eastWall = Wall(20, 10, 0.5, 0.5, 10, 0.5, 1);
        var northWall = Wall(21, 10, 0.5, 0.5, 0.5, 10, 1);
        var branch = Wall(22, 10, 5, 1, 5, 4, 2);
        eastWall.TypeId = branch.TypeId = 40;
        northWall.TypeId = northWallType;
        eastWall.Geometry = Extrude([(0, 0), (10, 0), (10, 1), (1, 1)]);
        northWall.Geometry = Extrude([(0, 0), (1, 1), (1, 10), (0, 10)]);
        branch.Geometry = Extrude([(4, 1), (6, 1), (6, 4), (4, 4)]);
        var info = new RevitFileInfo
        {
            Elements = { Level(10, "Level 1", 0), eastWall, northWall, branch },
            UnshadedHiddenLineViews = new HashSet<int> { 30 },
            PlanViewRanges = new Dictionary<int, (double[] Offsets, int[] LevelIds)> { [30] = ([4], [10]) },
        };
        var document = RvtDocument.FromInfo(info);
        var content = Assert.IsType<PlanPrinter.PlanContent>(PlanPrinter.CollectLevel(document, 10, 30));
        PdfPage page = new PdfDocument().AddPage(PdfPageSize.A1.Landscape);
        PlanPrinter.DrawContent(page, content, 100, 20, 20, document: document, planViewId: 30);

        string outer = Point(0, 0), inner = Point(1, 1), east = Point(10, 0);
        Assert.Contains($"{outer} m {east} l S", page.Content);
        Assert.Equal(visibleSeam, page.Content.Contains($"{outer} m {inner} l S", StringComparison.Ordinal)
            || page.Content.Contains($"{inner} m {outer} l S", StringComparison.Ordinal));
        Assert.Contains($"{Point(10, 1)} m {Point(6, 1)} l S", page.Content);
        Assert.Contains($"{Point(4, 1)} m {inner} l S", page.Content);
        Assert.DoesNotContain($"{Point(10, 1)} m {inner} l S", page.Content);

        string Point(double x, double y) => FormattableString.Invariant(
            $"{(20 + (x * 304.8 - content.MinX) / 100) * 72 / 25.4:0.####} {(20 + (y * 304.8 - content.MinY) / 100) * 72 / 25.4:0.####}");

        static GeometryData Extrude((double X, double Y)[] ring)
        {
            var faces = new List<GeoFace>
            {
                new() { Normal = [0, 0, 1], OuterVertices = ring.Select(p => new[] { p.X, p.Y, 8.0 }).ToList() },
                new() { Normal = [0, 0, -1], OuterVertices = Enumerable.Reverse(ring).Select(p => new[] { p.X, p.Y, 0.0 }).ToList() },
            };
            for (int i = 0; i < ring.Length; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Length];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                faces.Add(new GeoFace
                {
                    Normal = [dy / length, -dx / length, 0],
                    OuterVertices = [[a.X, a.Y, 0], [b.X, b.Y, 0], [b.X, b.Y, 8], [a.X, a.Y, 8]],
                });
            }
            return new GeometryData { Faces = faces };
        }
    }

    [Theory]
    [InlineData(3.0, 0.0, false, true)]
    [InlineData(4.0, 0.0, false, false)]
    [InlineData(3.0, 3.5, false, false)]
    [InlineData(3.0, 0.0, true, false)]
    public void DrawContent_ProjectsTheSavedWallTopBelowTheCut(double top, double bottom, bool overhang, bool projected)
    {
        const int level = 10, view = 30, category = (int)BuiltInCategory.OST_Walls;
        var wall = Wall(20, level, 0, 0.5, 10, 0.5, 1);
        double lowerLeft = overhang ? 0 : 1;
        wall.BoundingBox = [lowerLeft, 0, -1, 9, 1, top];
        wall.Geometry = new GeometryData
        {
            Faces =
            {
                new()
                {
                    PlaneSurface = true, Normal = [0, 0, 1],
                    OuterVertices = [[1, 0, top], [9, 0, top], [9, 1, top], [1, 1, top]],
                },
                new()
                {
                    PlaneSurface = true, Normal = [0, 0, -1],
                    OuterVertices = [[lowerLeft, 0, -1], [lowerLeft, 1, -1], [9, 1, -1], [9, 0, -1]],
                },
            },
        };
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { Level(level, "Level 1", 0), wall },
            UnshadedHiddenLineViews = new HashSet<int> { view },
            PlanViewRanges = new Dictionary<int, (double[], int[])> { [view] = ([4, 8, bottom], [level, level, level]) },
            GraphicsStylePens = new Dictionary<int, (int, int, int, int, int, int)>
            {
                [70] = (category, -1, 1, 1, 0, -1),
                [71] = (category, -1, 2, 2, 0, -1),
            },
            PenWidthTable = (new Dictionary<int, double[]> { [100] = [0.1 / 304.8, 0.25 / 304.8] }, []),
        });

        var content = PlanPrinter.CollectLevel(document, level, view)!;
        Assert.Equal(projected, content.ProjectedWallTops!.ContainsKey(wall.Id));
        if (projected)
        {
            var ring = content.ProjectedWallTops[wall.Id];
            var rotated = PlanPrinter.InViewBasis(content, [0, 1, 0], [-1, 0, 0]);
            Assert.Equal(ring.Select(p => (p.Y, -p.X)), rotated.ProjectedWallTops![wall.Id]);
        }

        PdfPage page = new PdfDocument().AddPage(PdfPageSize.A3);
        PlanPrinter.DrawContent(page, content, 100, 20, 20, document: document,
            clipRect: (-1000, -1000, 10000, 10000), planViewId: view);
        Assert.Contains(projected ? "0.2835 w" : "0.7087 w", page.Content);
        string expectedEdge = projected ? $"{Point(1)} m {Point(9)}" : $"{Point(10)} m {Point(0)}";
        Assert.Contains(expectedEdge + " l S", page.Content);

        string Point(double x) => FormattableString.Invariant(
            $"{(20 + (x * 304.8 - content.MinX) / 100) * 72 / 25.4:0.####} {(20 - content.MinY / 100) * 72 / 25.4:0.####}");
    }

    [Fact]
    public void CollectLevel_DrawsVisibleCurtainProfilesAtTheSavedPlanRange()
    {
        const int level = 10, view = 30;
        const int panels = (int)BuiltInCategory.OST_CurtainWallPanels;
        const int mullions = (int)BuiltInCategory.OST_CurtainWallMullions;
        var host = Wall(20, level, 0, 0, 10, 0);
        var pane = Part(40, panels, 0, 8, true);
        var post = Part(41, mullions, 0, 8, true);
        var above = Part(42, panels, 5, 8, true);
        var open = Part(43, panels, 0, 8, false);
        var sill = Part(44, mullions, 0, 2, true);
        var below = Part(45, mullions, -2, -1, true);
        var info = new RevitFileInfo
        {
            Elements = { Level(level, "Level 1", 0), host, pane, post, above, open, sill, below },
            PlanViewRanges = new Dictionary<int, (double[], int[])> { [view] = ([4, 8, 0], [level, level, level]) },
            GraphicsStylePens = new Dictionary<int, (int, int, int, int, int, int)>
            {
                [70] = (panels, -1, 2, 1, 0, -1),
                [71] = (mullions, -1, 2, 2, 0, -1),
                [72] = (mullions, -1, 1, 3, 0, -1),
            },
            PenWidthTable = (new Dictionary<int, double[]> { [100] = [0.18 / 304.8, 0.25 / 304.8, 0.1 / 304.8] }, []),
        };
        var document = RvtDocument.FromInfo(info);

        var content = PlanPrinter.CollectLevel(document, level, view)!;
        Assert.Equal(3, content.CurtainProfiles!.Count);
        var projection = Assert.Single(content.CurtainProfiles!, profile => !profile.IsCut);
        Assert.Equal(mullions, projection.CategoryId);
        Assert.Equal(44 * 304.8, projection.Ring.Min(p => p.X), 6);
        var glass = Assert.Single(content.CurtainProfiles!, cut => cut.CategoryId == panels);
        Assert.True(glass.IsCut);
        Assert.Equal(4, glass.Ring.Count);
        Assert.All(glass.Ring, p => Assert.InRange(p.X / 304.8, 40, 41));
        var turned = PlanPrinter.InViewBasis(content, [0, 1, 0], [-1, 0, 0]);
        Assert.Equal(glass.Ring.Select(p => (p.Y, -p.X)),
            Assert.Single(turned.CurtainProfiles!, cut => cut.CategoryId == panels).Ring);

        var pdf = new PdfDocument();
        var page = pdf.AddPage(PdfPageSize.A1.Landscape);
        PlanPrinter.DrawContent(page, content, 100, 20, 20, document: document, planViewId: view);
        Assert.Contains("0.5102 w", page.Content);
        Assert.Contains("0.7087 w", page.Content);
        Assert.Contains("0.2835 w", page.Content);
        Assert.Null(PlanPrinter.CollectLevel(document, level)!.CurtainProfiles);

        document.RawData.ViewHiddenCategories = new Dictionary<int, HashSet<int>> { [view] = [mullions] };
        Assert.Equal(panels, Assert.Single(PlanPrinter.CollectLevel(document, level, view)!.CurtainProfiles!).CategoryId);
        document.RawData.ViewHiddenElements = new Dictionary<int, HashSet<int>> { [view] = [pane.Id] };
        Assert.Empty(PlanPrinter.CollectLevel(document, level, view)!.CurtainProfiles!);
        document.RawData.ViewHiddenCategories = null;
        document.RawData.ViewHiddenElements = new Dictionary<int, HashSet<int>> { [view] = [host.Id] };
        Assert.Empty(PlanPrinter.CollectLevel(document, level, view)!.CurtainProfiles!);

        Element Part(int id, int category, double bottom, double top, bool closed)
        {
            List<double[]> Ring(double z) => [[id, 2, z], [id + 1, 2, z], [id + 1, 2.25, z], [id, 2.25, z]];
            var lower = Ring(bottom);
            var upper = Ring(top);
            var faces = new List<GeoFace>
            {
                new() { OuterVertices = lower },
                new() { PlaneSurface = true, Normal = [0, 0, 1], OuterVertices = upper },
            };
            for (int i = 0; i < lower.Count; i++)
            {
                int next = (i + 1) % lower.Count;
                faces.Add(new GeoFace { OuterVertices = [lower[i], lower[next], upper[next], upper[i]] });
            }
            return new Element
            {
                Id = id, CategoryId = category, SerializedHostId = host.Id,
                BoundingBox = [id, 2, bottom, id + 1, 2.25, top],
                Geometry = new GeometryData
                {
                    Faces = faces, BrepSolids = [new() { IsClosed = closed, FaceIndices = [0, 1, 2, 3, 4, 5] }],
                },
            };
        }
    }

    private static (RvtDocument Document, Element Wall, Dictionary<int, (double[] Offsets, int[] LevelIds)> Ranges)
        WallWithHostedOpening()
    {
        var wall = Wall(20, 10, 0.5, 0.5, 11.5, 0.5, 1);
        wall.TypeId = 40;
        var front = Face([[0, 0, 0], [12, 0, 0], [12, 0, 8], [0, 0, 8]]);
        front.InnerVertices = [[[4, 0, 2], [7, 0, 2], [7, 0, 6], [4, 0, 6]]];
        var back = Face([[12, 1, 0], [0, 1, 0], [0, 1, 8], [12, 1, 8]]);
        back.InnerVertices = [[[7, 1, 2], [4, 1, 2], [4, 1, 6], [7, 1, 6]]];
        wall.Geometry = new GeometryData
        {
            Faces =
            [
                front, back,
                Face([[0, 1, 0], [0, 0, 0], [0, 0, 8], [0, 1, 8]]),
                Face([[12, 0, 0], [12, 1, 0], [12, 1, 8], [12, 0, 8]]),
                Face([[0, 0, 0], [0, 1, 0], [12, 1, 0], [12, 0, 0]]),
                Face([[0, 0, 8], [12, 0, 8], [12, 1, 8], [0, 1, 8]]),
                Face([[4, 0, 2], [4, 1, 2], [4, 1, 6], [4, 0, 6]]),
                Face([[7, 1, 2], [7, 0, 2], [7, 0, 6], [7, 1, 6]]),
                Face([[4, 0, 2], [7, 0, 2], [7, 1, 2], [4, 1, 2]]),
                Face([[4, 1, 6], [7, 1, 6], [7, 0, 6], [4, 0, 6]]),
            ],
        };
        var ranges = new Dictionary<int, (double[] Offsets, int[] LevelIds)> { [30] = ([4], [10]) };
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { Level(10, "Level 1", 0), wall },
            UnshadedHiddenLineViews = new HashSet<int> { 30 },
            PlanViewRanges = ranges,
        });
        return (document, wall, ranges);
        static GeoFace Face(List<double[]> vertices) => new() { OuterVertices = vertices, PlaneSurface = true };
    }

    [Fact]
    public void DrawContent_UsesTheModelWallCutWhenThePlaneCrossesTheHostedOpening()
    {
        var (document, wall, ranges) = WallWithHostedOpening();
        var content = Assert.IsType<PlanPrinter.PlanContent>(PlanPrinter.CollectLevel(document, 10, 30));
        content.Gaps[wall.Id] = [(3.5 * 304.8, 6.5 * 304.8)];
        var pdf = new PdfDocument();
        PdfPage section = pdf.AddPage(PdfPageSize.A1.Landscape);
        PlanPrinter.DrawContent(section, content, 100, 20, 20, document: document, planViewId: 30,
            clipRect: (0, -100, 4000, 400));
        Assert.Contains(Line(0, 4), section.Content);
        Assert.Contains(Line(7, 12), section.Content);
        Assert.Equal(2, section.Content.Split('\n').Count(line => line.EndsWith("h f", StringComparison.Ordinal)));

        ranges[30] = ([7], [10]);
        PdfPage above = pdf.AddPage(PdfPageSize.A1.Landscape);
        PlanPrinter.DrawContent(above, content, 100, 20, 20, document: document,
            clipRect: (-100, -100, 10000, 10000), planViewId: 30);
        Assert.Contains(Line(4, 0.5), above.Content);
        Assert.Contains(Line(11.5, 7), above.Content);
        Assert.DoesNotContain(Line(0, 12), above.Content);

        string Line(double a, double b) => $"{Point(a)} m {Point(b)} l S";
        string Point(double x) => FormattableString.Invariant(
            $"{(20 + (x * 304.8 - content.MinX) / 100) * 72 / 25.4:0.####} {(20 - content.MinY / 100) * 72 / 25.4:0.####}");
    }

    [Fact]
    public void DrawContent_ClipsTheModelWallCutToTheCropRectangle()
    {
        var (document, wall, _) = WallWithHostedOpening();
        var content = Assert.IsType<PlanPrinter.PlanContent>(PlanPrinter.CollectLevel(document, 10, 30));
        content.Gaps[wall.Id] = [(3.5 * 304.8, 6.5 * 304.8)];
        var page = new PdfDocument().AddPage(PdfPageSize.A1.Landscape);
        const double cropRight = 3 * 304.8;

        // The crop reaches 3 ft into the first cut piece (0..4 ft) and misses the second (7..12 ft).
        PlanPrinter.DrawContent(page, content, 100, 20, 20, document: document, planViewId: 30,
            clipRect: (0, -100, cropRight, 400));

        var fill = Assert.Single(page.Content.Split('\n'), line => line.EndsWith("h f", StringComparison.Ordinal));
        double[] xs = [.. System.Text.RegularExpressions.Regex.Matches(fill, @"([\d.]+) [\d.]+ [ml]")
            .Select(match => double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))];
        double pageRight = (20 + (cropRight - content.MinX) / 100) * 72 / 25.4;
        Assert.True(xs.Length >= 3);
        Assert.InRange(xs.Max(), pageRight - 0.01, pageRight + 0.01);
    }

    [SkippableFact]
    public void SampleFile_PlanElevationMarksUseTheirSavedViewsAndCrop()
    {
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("The Revit 2022 architectural sample is not available.");
        var document = RvtDocument.Open(path);
        var marks = Assert.IsAssignableFrom<IReadOnlyList<RevitFileInfo.PlanElevationMark>>(document.RawData.PlanElevationMarks);
        Assert.Equal(4, marks.Count);
        Assert.All(marks, mark =>
        {
            Assert.True(mark.BodyId > 0 && mark.ArrowSymbolId > 0);
            Assert.NotNull(document.GetElement(mark.ViewId)?.ViewCrop);
        });

        var sheet = Assert.Single(document.Elements, e =>
            e.LookupParameter(BuiltInParameter.SHEET_NUMBER)?.Value as string == "A102"
            && e.CategoryId == (int)BuiltInCategory.OST_Sheets);
        var views = document.Elements.Where(e => e.OwnerViewId == sheet.Id && e.ViewportViewId.HasValue)
            .Select(e => document.GetElement(e.ViewportViewId!.Value)).OfType<Element>().ToArray();
        var pdf = new PdfDocument { Font = PdfFonts.TryFind(out _) };
        foreach (string name in new[] { "Level 1", "Level 2" })
        {
            var view = Assert.Single(views, e => e.Name == name && e.RootClassName == "DBViewPlan");
            var crop = Assert.IsType<ViewCropFrame>(view.ViewCrop);
            PdfPage page = pdf.AddPage(PdfPageSize.A1.Landscape);
            PlanElevationMarks.Draw(page, document, view, crop, 100, 200, 200);
            Assert.Equal(name == "Level 1" ? 4 : 0, page.Content.Split(" h B\n").Length - 1);
            if (pdf.Font != null)
                Assert.Equal(name == "Level 1" ? 8 : 0, page.Content.Split(" Tj ET\n").Length - 1);
        }
    }

    [Fact]
    public void CollectLevel_ReadsTheRoomFootprints()
    {
        var room = new Element { Id = 700, Name = "Living", CategoryId = (int)BuiltInCategory.OST_Rooms };
        Add(room, BuiltInParameter.ROOM_LEVEL_ID, StorageType.ElementId, 500);
        Add(room, BuiltInParameter.ROOM_AREA, StorageType.Double, 200.0);
        Add(room, BuiltInParameter.ROOM_NUMBER, StorageType.String, "101");
        room.SerializedSpaceBoundary = new GeometryData
        {
            Faces =
            [
                new GeoFace
                {
                    Normal = [0, 0, 1],
                    OuterVertices = [[0, 0, 0], [5, 0, 0], [5, 4, 0], [0, 4, 0]],
                },
            ],
        };
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { Level(500, "Level 1", 0), Wall(600, 500, 0, 0, 10, 0), room },
        });

        var content = PlanPrinter.CollectLevel(document, 500);

        Assert.NotNull(content);
        var footprint = Assert.Single(content!.Rooms);
        Assert.Equal("Living", footprint.Name);
        Assert.Equal("101", footprint.Number);
        Assert.Equal(5 * 304.8 * 4 * 304.8, Math.Abs(Area(footprint.Polygon)), 3);
    }

    [Fact]
    public void CollectLevel_ReadsTheFurnitureOutlines()
    {
        var table = new Element { Id = 800, Name = "Table", CategoryId = (int)BuiltInCategory.OST_Furniture };
        Add(table, BuiltInParameter.LEVEL_PARAM, StorageType.ElementId, 500);
        table.Geometry = new GeometryData
        {
            Faces =
            [
                new GeoFace
                {
                    Normal = [0, 0, 1],
                    OuterVertices = [[0, 0, 2], [1, 0, 2], [1, 1, 2], [0, 1, 2]],
                },
            ],
        };
        var stair = new Element { Id = 801, Name = "Stair", CategoryId = (int)BuiltInCategory.OST_Stairs };
        Add(stair, BuiltInParameter.LEVEL_PARAM, StorageType.ElementId, 500);
        stair.Geometry = new GeometryData
        {
            Faces =
            [
                new GeoFace
                {
                    Normal = [0, 0, 1],
                    OuterVertices = [[3, 0, 1], [4, 0, 1], [4, 1, 1], [3, 1, 1]],
                },
            ],
        };
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { Level(500, "Level 1", 0), Wall(600, 500, 0, 0, 10, 0), table, stair },
        });

        var content = PlanPrinter.CollectLevel(document, 500);

        Assert.NotNull(content);
        // Furniture is a silhouette now (the union of its faces), the stair keeps its face outline.
        Assert.Single(content!.Furniture);
        Assert.NotNull(content.FurnitureLines);
        Assert.NotEmpty(content.FurnitureLines!);
        Assert.All(content.FurnitureLines!, line =>
        {
            Assert.InRange(Math.Min(line.X1, line.X2), 0, 310);
            Assert.InRange(Math.Max(line.X1, line.X2), 0, 310);
            Assert.InRange(Math.Min(line.Y1, line.Y2), 0, 310);
            Assert.InRange(Math.Max(line.Y1, line.Y2), 0, 310);
        });
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void CollectLevel_HiddenStairsDrawNoTreadOutline(bool hidden, bool categoryHidden, bool drawn)
    {
        const int view = 700, stairId = 801;
        var stair = new Element
        {
            Id = stairId, CategoryId = (int)BuiltInCategory.OST_Stairs,
            Geometry = new GeometryData
            {
                Faces = [new GeoFace { Normal = [0, 0, 1], OuterVertices = [[3, 0, 1], [4, 0, 1], [4, 1, 1], [3, 1, 1]] }],
            },
        };
        Add(stair, BuiltInParameter.LEVEL_PARAM, StorageType.ElementId, 500);
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { Level(500, "Level 1", 0), Wall(600, 500, 0, 0, 10, 0), stair },
            ViewHiddenElements = new Dictionary<int, HashSet<int>> { [view] = hidden ? [stairId] : [] },
            ViewHiddenCategories = new Dictionary<int, HashSet<int>>
                { [view] = categoryHidden ? [(int)BuiltInCategory.OST_Stairs] : [] },
        });

        var content = Assert.IsType<PlanPrinter.PlanContent>(PlanPrinter.CollectLevel(document, 500, view));

        Assert.Equal(drawn, content.StairLines is { Count: > 0 });
    }

    [Theory]
    [InlineData(false, false, false, false, true)]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, false, false, true, false)]
    public void CollectLevel_HiddenRailingsDrawNoTopRailOutline(
        bool railHidden, bool railCategoryHidden, bool ownerHidden, bool ownerCategoryHidden, bool drawn)
    {
        const int view = 700, railId = 810, ownerId = 811;
        var rail = new Element
        {
            Id = railId, CategoryId = (int)BuiltInCategory.OST_RailingTopRail, SerializedOwningElementId = ownerId,
            Geometry = new GeometryData
            {
                Faces = [new GeoFace { Normal = [0, 0, 1], OuterVertices = [[3, 0, 3], [5, 0, 3], [5, 0.5, 3], [3, 0.5, 3]] }],
            },
        };
        Add(rail, BuiltInParameter.LEVEL_PARAM, StorageType.ElementId, 500);
        var owner = new Element { Id = ownerId, CategoryId = (int)BuiltInCategory.OST_StairsRailing };
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { Level(500, "Level 1", 0), Wall(600, 500, 0, 0, 10, 0), rail, owner },
            ViewHiddenElements = new Dictionary<int, HashSet<int>>
                { [view] = [.. (railHidden ? [railId] : Array.Empty<int>()), .. (ownerHidden ? [ownerId] : Array.Empty<int>())] },
            ViewHiddenCategories = new Dictionary<int, HashSet<int>>
            {
                [view] =
                [
                    .. railCategoryHidden ? [(int)BuiltInCategory.OST_RailingTopRail] : Array.Empty<int>(),
                    .. ownerCategoryHidden ? [(int)BuiltInCategory.OST_StairsRailing] : Array.Empty<int>(),
                ],
            },
        });

        var content = Assert.IsType<PlanPrinter.PlanContent>(PlanPrinter.CollectLevel(document, 500, view));

        Assert.Equal(drawn, content.RailLines is { Count: > 0 });
    }

    // A flight that arrives on level 500 from below, with a stair path in the view and a top rail on its railing.
    [Theory]
    [InlineData(false, false, false, false, false, true)]
    [InlineData(true, false, false, false, false, false)]
    [InlineData(false, true, false, false, false, false)]
    [InlineData(false, false, true, false, false, false)]
    [InlineData(false, false, false, true, false, false)]
    [InlineData(false, false, false, false, true, false)]
    public void CollectLevel_HiddenRailingDropsItsTopRailFromAProjectedStair(
        bool railHidden, bool railCategoryHidden, bool ownerHidden, bool ownerCategoryHidden, bool stairHidden, bool drawn)
    {
        const int view = 700, stairId = 801, runId = 802, railId = 810, ownerId = 811, railStyle = 73;
        var treads = new[] { new TfBlockDeserializer.StairPlanLine(0, 0, 0, 1, -3), new(3, 0, 3, 1, 3) };
        var plan = new TfBlockDeserializer.StairPlanInput(treads, [new(0, 0, 3, 0, 0)], [new(0, 1, 3, 1, 0)], []);
        var run = new Element
        {
            Id = runId,
            SerializedStairRunInput = new TfBlockDeserializer.StairRunInput(runId, stairId,
                TfBlockDeserializer.StairRunStyle.Sketched, [], new TfBlockDeserializer.StairRunNumbers(-3, 3, 3, 0, 0, 1, 0), plan),
        };
        var stair = new Element
        {
            Id = stairId, CategoryId = (int)BuiltInCategory.OST_Stairs,
            SerializedStairCore = [new StairComponentSource.CoreComponent(runId, 3708)],
        };
        Add(stair, BuiltInParameter.STAIRS_TOP_LEVEL_PARAM, StorageType.ElementId, 500);
        Add(stair, BuiltInParameter.STAIRS_BASE_LEVEL_PARAM, StorageType.ElementId, 499);
        var rail = new Element
        {
            Id = railId, CategoryId = (int)BuiltInCategory.OST_RailingTopRail, NativeRootClassName = "TopRail",
            SerializedOwningElementId = ownerId, BoundingBox = [1, 0, 2.9, 2, 0.5, 3],
            Geometry = new GeometryData
            {
                Faces = [new GeoFace { Normal = [0, 0, 1], OuterVertices = [[1, 0, 3], [2, 0, 3], [2, 0.5, 3], [1, 0.5, 3]] }],
            },
        };
        var owner = new Element { Id = ownerId, CategoryId = (int)BuiltInCategory.OST_StairsRailing, SerializedRailingStairHostId = stairId };
        var info = new RevitFileInfo
        {
            Elements = { Level(500, "Main", 0), Level(499, "Lower", -3), Wall(600, 500, 0, 0, 10, 0), stair, run, rail, owner },
            PlanViewRanges = new Dictionary<int, (double[], int[])> { [view] = ([4, 8, 0], [500, 500, 500]) },
            OpaquePlanViews = new HashSet<int> { view },
            PlanStairPaths = new Dictionary<int, List<RevitFileInfo.PlanStairPath>>
                { [view] = [new(900, stairId, 0.1, 0.5, null, 0, 0, 0)] },
            GraphicsStylePens = new Dictionary<int, (int, int, int, int, int, int)>
            {
                [71] = ((int)BuiltInCategory.OST_StairsOutlines, -1, 1, 1, 0, -1),
                [72] = ((int)BuiltInCategory.OST_StairsNosingLines, -1, 1, 1, 0, -1),
                [railStyle] = ((int)BuiltInCategory.OST_RailingTopRail, -1, 1, 1, 0, -1),
            },
            ViewHiddenElements = new Dictionary<int, HashSet<int>>
            {
                [view] = [.. railHidden ? [railId] : Array.Empty<int>(), .. ownerHidden ? [ownerId] : Array.Empty<int>(),
                    .. stairHidden ? [stairId] : Array.Empty<int>()],
            },
            ViewHiddenCategories = new Dictionary<int, HashSet<int>>
            {
                [view] = [.. railCategoryHidden ? [(int)BuiltInCategory.OST_RailingTopRail] : Array.Empty<int>(),
                    .. ownerCategoryHidden ? [(int)BuiltInCategory.OST_StairsRailing] : Array.Empty<int>()],
            },
        };

        var content = Assert.IsType<PlanPrinter.PlanContent>(PlanPrinter.CollectLevel(RvtDocument.FromInfo(info), 500, view));

        // Without the stair being hidden the flight draws its outlines and nosings either way; the rail of the hidden
        // railing is the part that goes.
        Assert.Equal(!stairHidden, content.FixtureLines is { Count: > 0 });
        Assert.Equal(drawn, content.FixtureLines?.Any(line => line.StyleId == railStyle) == true);
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_Furniture, -4, false, 4, true)]
    [InlineData(BuiltInCategory.OST_Furniture, 500, false, 4, false)]
    [InlineData(BuiltInCategory.OST_Furniture, -4, true, 4, false)]
    [InlineData(BuiltInCategory.OST_Furniture, -4, false, 4, false, true)]
    [InlineData(BuiltInCategory.OST_GenericModel, -4, false, 4, true)]
    [InlineData(BuiltInCategory.OST_GenericModel, -4, false, -1.5, false)]
    [InlineData(BuiltInCategory.OST_GenericModel, -4, true, 4, false)]
    public void CollectLevel_FurnitureFollowsTheSavedViewRangeAndHiddenElements(
        BuiltInCategory category, int bottom, bool hidden, double cut, bool expected, bool categoryHidden = false)
    {
        const int view = 700, fixture = 800;
        var table = new Element
        {
            Id = fixture, CategoryId = (int)category, NativeRootClassName = "FamilyInstance",
            BoundingBox = [0, 0, -2, 1, 1, -1],
            Geometry = new GeometryData
            {
                Faces =
                [
                    new GeoFace
                    {
                        Normal = [0, 0, 1],
                        OuterVertices = [[0, 0, -1], [1, 0, -1], [1, 1, -1], [0, 1, -1]],
                        InnerVertices = [[[0.25, 0.25, -1], [0.75, 0.25, -1], [0.75, 0.75, -1], [0.25, 0.75, -1]]],
                    },
                ],
            },
        };
        Add(table, BuiltInParameter.LEVEL_PARAM, StorageType.ElementId, 501);
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { Level(500, "Main", 0), Level(501, "Lower", -2), Wall(600, 500, 0, 0, 10, 0), table },
            PlanViewRanges = new Dictionary<int, (double[], int[])> { [view] = ([cut, 8, 0, 0, 0], [500, 500, bottom, bottom, -1]) },
            OpaquePlanViews = new HashSet<int> { view },
            ViewHiddenElements = new Dictionary<int, HashSet<int>> { [view] = hidden ? [fixture] : [] },
            ViewHiddenCategories = new Dictionary<int, HashSet<int>> { [view] = categoryHidden ? [(int)category] : [] },
        });

        var content = PlanPrinter.CollectLevel(document, 500, view);

        Assert.NotNull(content);
        Assert.Equal(expected, content.FurnitureLines is { Count: > 0 });
        Assert.Equal(expected ? 2 : 0, content.FurnitureFillRings!.Count);
        if (expected)
        {
            var hole = content.FurnitureFillRings[1];
            Assert.True(hole.Zip(hole.Skip(1).Append(hole[0]), (a, b) => a.X * b.Y - a.Y * b.X).Sum() < 0);
        }
    }

    [Theory]
    [InlineData(-4, true, false, true, true)]
    [InlineData(500, true, false, true, false)]
    [InlineData(-4, false, false, false, false)]
    [InlineData(-4, true, true, false, false)]
    public void CollectLevel_ClipsClosedGenericModelToTheSavedPlanRange(
        int bottom, bool closed, bool hidden, bool expectedCut, bool expectedProjection)
    {
        const int level = 500, view = 700, fixture = 800;
        var geometry = new GeometryData { BrepSolids = [] };
        AddBox(0, 4, -2, -1, true);
        AddBox(1, 3, -1.5, 8, closed);
        var instance = new Element
        {
            Id = fixture, CategoryId = (int)BuiltInCategory.OST_GenericModel, NativeRootClassName = "FamilyInstance",
            BoundingBox = [0, 0, -2, 4, 4, 8], Geometry = geometry,
        };
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { Level(level, "Main", 0), Wall(600, level, 0, 0, 10, 0), instance },
            PlanViewRanges = new Dictionary<int, (double[], int[])> { [view] = ([4, 8, 0], [level, level, bottom]) },
            OpaquePlanViews = new HashSet<int> { view },
            ViewHiddenElements = new Dictionary<int, HashSet<int>> { [view] = hidden ? [fixture] : [] },
        });

        var content = PlanPrinter.CollectLevel(document, level, view)!;
        var lines = content.GenericModelLines ?? [];
        Assert.Equal(expectedCut ? 4 : 0, lines.Count(line => line.Cut));
        Assert.Equal(expectedProjection ? 4 : 0, lines.Count(line => !line.Cut));
        Assert.Equal(expectedProjection, content.FurnitureFillRings!.Any(ring => ring.Any(point => point.X == 0)));
        Assert.Equal(expectedCut, content.FurnitureFillRings!.Count > 0);
        if (expectedCut)
        {
            Assert.All(lines.Where(line => line.Cut), line =>
            {
                Assert.InRange(line.X1, 304.8, 914.4 + 1e-8);
                Assert.InRange(line.Y1, 304.8, 914.4 + 1e-8);
            });
            var rotated = PlanPrinter.InViewBasis(content, [0, 1, 0], [-1, 0, 0]).GenericModelLines!;
            Assert.Equal(lines.Select(line => (line.Y1, -line.X1, line.Y2, -line.X2, line.Cut)), rotated);
        }

        void AddBox(double min, double max, double low, double high, bool isClosed)
        {
            List<double[]> baseRing = [[min, min, low], [max, min, low], [max, max, low], [min, max, low]];
            var topRing = baseRing.Select(point => new[] { point[0], point[1], high }).ToList();
            int first = geometry.Faces.Count;
            geometry.Faces.Add(new GeoFace { OuterVertices = baseRing.AsEnumerable().Reverse().ToList() });
            geometry.Faces.Add(new GeoFace { OuterVertices = topRing });
            for (int i = 0; i < 4; i++)
            {
                int next = (i + 1) % 4;
                geometry.Faces.Add(new GeoFace { OuterVertices = [baseRing[i], baseRing[next], topRing[next], topRing[i]] });
            }
            geometry.BrepSolids!.Add(new GeoBrepSolid { FaceIndices = Enumerable.Range(first, 6).ToList(), IsClosed = isClosed });
        }
    }


    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CollectLevel_RespectsFurnitureTopVisibilityInAnOpaquePlan(bool opaque, bool allExcluded)
    {
        const int view = 700;
        var furniture = new Element
        {
            Id = 800,
            CategoryId = (int)BuiltInCategory.OST_Furniture,
            BoundingBox = [1, 1, 0, 7, 4, 3],
            Geometry = new GeometryData
            {
                Faces =
                [
                    new GeoFace
                    {
                        OuterVertices = [[1, 1, 3], [4, 1, 3], [4, 4, 3], [1, 4, 3]],
                        ExcludedFromTopPlan = allExcluded,
                    },
                    new GeoFace
                    {
                        OuterVertices = [[2, 2, 0], [7, 2, 0], [7, 3, 0], [2, 3, 0]],
                        ExcludedFromTopPlan = true,
                    },
                ],
            },
        };
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { Level(500, "Main", 0), Wall(600, 500, 0, 0, 10, 0), furniture },
            PlanViewRanges = new Dictionary<int, (double[], int[])> { [view] = ([4, 8, 0], [500, 500, 500]) },
            OpaquePlanViews = opaque ? new HashSet<int> { view } : null,
        });

        var content = PlanPrinter.CollectLevel(document, 500, view)!;
        Assert.NotEmpty(content.FurnitureLines!);
        Assert.Equal(!opaque || allExcluded, content.FurnitureLines!.Any(line => line.X1 > 1500 || line.X2 > 1500));
        Assert.Equal(opaque ? (allExcluded ? 2 : 1) : 0, content.FurnitureFillRings!.Count);
        Assert.Equal(2, furniture.Geometry!.Faces.Count);
    }

    [Fact]
    public void CollectLevel_SamplesTheSavedFixtureArcAndClipsItsCoveredLength()
    {
        const int level = 500, view = 700;
        var arc = new Arc([0, 0, 1], [1, 0, 0], [0, 1, 0], 2, 0, Math.PI / 2);
        var adjacentArc = arc with { Centre = [8, 8, 1] };
        var geometry = new GeometryData { BrepSolids = [] };
        AddSolid([[0, 0, 1], arc.At(0), arc.At(Math.PI / 4), arc.At(Math.PI / 2)], 0, [arc, adjacentArc]);
        AddSolid([[1.5, -1, 3], [3, -1, 3], [3, 1.5, 3], [1.5, 1.5, 3]], 2, null);
        var fixture = new Element
        {
            Id = 800, CategoryId = (int)BuiltInCategory.OST_GenericModel, NativeRootClassName = "FamilyInstance",
            BoundingBox = [0, -1, 0, 3, 2, 3], Geometry = geometry,
        };
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { Level(level, "Main", 0), Wall(600, level, 0, 0, 10, 0), fixture },
            PlanViewRanges = new Dictionary<int, (double[], int[])> { [view] = ([4, 8, 0], [level, level, level]) },
            OpaquePlanViews = new HashSet<int> { view },
        });

        var lines = PlanPrinter.CollectLevel(document, level, view)!.FurnitureLines!;
        double[] visible = arc.At(3 * Math.PI / 8), covered = arc.At(Math.PI / 8);
        Assert.Contains(lines, line => HasEndpoint(line, visible));
        Assert.DoesNotContain(lines, line => HasEndpoint(line, covered));

        static bool HasEndpoint((double X1, double Y1, double X2, double Y2) line, double[] p) =>
            Math.Abs(line.X1 - p[0] * 304.8) + Math.Abs(line.Y1 - p[1] * 304.8) < 1e-5
            || Math.Abs(line.X2 - p[0] * 304.8) + Math.Abs(line.Y2 - p[1] * 304.8) < 1e-5;

        void AddSolid(List<double[]> top, double low, IReadOnlyList<Arc>? curves)
        {
            int first = geometry.Faces.Count;
            var lower = top.Select(p => new[] { p[0], p[1], low }).ToList();
            geometry.Faces.Add(new GeoFace
            {
                OuterVertices = top, Normal = [0, 0, 1], PlaneSurface = true, MeshArcs = curves,
            });
            geometry.Faces.Add(new GeoFace { OuterVertices = [.. lower.AsEnumerable().Reverse()], Normal = [0, 0, -1], PlaneSurface = true });
            for (int i = 0; i < top.Count; i++)
            {
                int j = (i + 1) % top.Count;
                geometry.Faces.Add(new GeoFace
                {
                    OuterVertices = [lower[i], lower[j], top[j], top[i]],
                    Normal = [top[j][1] - top[i][1], top[i][0] - top[j][0], 0], PlaneSurface = true,
                });
            }
            geometry.BrepSolids!.Add(new GeoBrepSolid
            {
                IsClosed = true, FaceIndices = Enumerable.Range(first, geometry.Faces.Count - first).ToList(),
            });
        }
    }

    [Fact]
    public void OutlineJoinedRings_PreservesTheBoundaryOfAThinProjectedFacet()
    {
        var edges = ElevationPrinter.OutlineJoinedRings(
            [[(0, 0), (10, 0), (10, 10), (0, 10)], [(10, 0), (10.04, 0), (10.04, 10), (10, 10)]],
            (int)BuiltInCategory.OST_GenericModel, coverOppositeEdges: true, snapEndpoints: false).ToArray();

        Assert.Single(edges, edge => edge.X1 == 10.04 && edge.X2 == 10.04);
        Assert.DoesNotContain(edges, edge => edge.X1 == 10 && edge.X2 == 10);
    }

    [Theory]
    [InlineData(700, false, false, true)]
    [InlineData(701, false, false, false)]
    [InlineData(700, true, false, false)]
    [InlineData(700, false, true, false)]
    public void CollectLevel_DetailCircleFollowsItsOwningViewAndVisibility(
        int owner, bool hiddenElement, bool hiddenCategory, bool visible)
    {
        const int level = 500, view = 700, id = 800;
        var circle = new Element
        {
            Id = id, CategoryId = (int)BuiltInCategory.OST_Lines, NativeRootClassName = "CurveElem",
            SerializedOwnerDbViewId = owner, DetailCircle = (2, 3, 0.5),
        };
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { Level(level, "Main", 0), Wall(600, level, 0, 0, 10, 0), circle },
            ViewHiddenElements = new Dictionary<int, HashSet<int>> { [view] = hiddenElement ? [id] : [] },
            ViewHiddenCategories = new Dictionary<int, HashSet<int>>
                { [view] = hiddenCategory ? [(int)BuiltInCategory.OST_Lines] : [] },
        });

        var content = PlanPrinter.CollectLevel(document, level, view)!;
        if (!visible)
        {
            Assert.Null(content.DetailCircles);
            return;
        }

        var rotated = PlanPrinter.InViewBasis(content, [0, 1, 0], [-1, 0, 0]);
        Assert.Equal((3 * 304.8, -2 * 304.8, 0.5 * 304.8), Assert.Single(rotated.DetailCircles!));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void CollectLevel_KeepsVisibleFixtureComponentsAndClipsTheirHiddenSpans(bool foldedSide, bool sharedFace)
    {
        const int view = 700;
        var geometry = new GeometryData { BrepSolids = [] };
        AddSolid(0, 1, [(1, 1), (7, 1), (7, 7), (1, 7)]);
        AddSolid(1, 2, [(2, 2), (6, 2), (6, 6), (2, 6)], [(3, 3), (3, 5), (5, 5), (5, 3)]);
        AddSolid(2, 3, [(4, 1.75), (5, 1.75), (5, 2.25), (4, 2.25)], folded: foldedSide);
        AddSolid(0.25, 0.75, [(2, 0.75), (3, 0.75), (3, 1.25), (2, 1.25)]);
        if (sharedFace)
        {
            AddSolid(0, 0.5, [(1, 1), (7, 1), (7, 7), (1, 7)]);
            ElementBuilder.DeduplicateExactFaces(geometry);
            Assert.Contains(geometry.BrepSolids![^1].FaceIndices, geometry.BrepSolids[0].FaceIndices.Contains);
        }
        var fixture = new Element
        {
            Id = 800, CategoryId = (int)BuiltInCategory.OST_PlumbingFixtures,
            NativeRootClassName = "FamilyInstance", BoundingBox = [1, 0.75, 0, 7, 7, 3], Geometry = geometry,
        };
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { Level(500, "Main", 0), Wall(600, 500, 0, 0, 10, 0), fixture },
            PlanViewRanges = new Dictionary<int, (double[], int[])> { [view] = ([4, 8, 0], [500, 500, 500]) },
            OpaquePlanViews = new HashSet<int> { view },
        });

        var lines = PlanPrinter.CollectLevel(document, 500, view)!.FurnitureLines!;
        Assert.Contains(lines, line => Horizontal(line, 2, 4, 2));
        Assert.Contains(lines, line => Horizontal(line, 5, 6, 2));
        Assert.DoesNotContain(lines, line => Horizontal(line, 2, 6, 2));
        Assert.Contains(lines, line => Horizontal(line, 3, 5, 3));
        Assert.Contains(lines, line => Horizontal(line, 1, 7, 1));
        Assert.Contains(lines, line => Horizontal(line, 2, 3, 0.75));
        Assert.DoesNotContain(lines, line => Horizontal(line, 2, 3, 1.25));

        static bool Horizontal((double X1, double Y1, double X2, double Y2) line, double first, double last, double y)
            => Near(line.X1, first) && Near(line.X2, last) && Near(line.Y1, y) && Near(line.Y2, y);
        static bool Near(double millimetres, double feet) => Math.Abs(millimetres - 304.8 * feet) < 1e-5;

        void AddSolid(double low, double high, (double X, double Y)[] outer,
            (double X, double Y)[]? hole = null, bool folded = false)
        {
            int start = geometry.Faces.Count;
            List<double[]> At((double X, double Y)[] ring, double z) =>
                ring.Select(p => new[] { p.X, p.Y, z }).ToList();
            var top = At(outer, high);
            var bottom = At(outer, low);
            if (folded)
            {
                bottom[0][1] -= 0.125;
                bottom[1][1] += 0.125;
            }
            geometry.Faces.Add(new GeoFace
            {
                PlaneSurface = true, Normal = [0, 0, -1], OuterVertices = bottom,
                InnerVertices = hole == null ? null : [At(hole, low)],
            });
            geometry.Faces.Add(new GeoFace
            {
                PlaneSurface = true, Normal = [0, 0, 1], OuterVertices = top,
                InnerVertices = hole == null ? null : [At(hole, high)],
            });
            AddSides(bottom, top, folded);
            if (hole != null) AddSides(At(hole, low), At(hole, high), false);
            geometry.BrepSolids!.Add(new GeoBrepSolid
            {
                IsClosed = true, FaceIndices = Enumerable.Range(start, geometry.Faces.Count - start).ToList(),
            });

            void AddSides(List<double[]> lower, List<double[]> upper, bool withFold)
            {
                for (int i = 0; i < lower.Count; i++)
                {
                    int next = (i + 1) % lower.Count;
                    geometry.Faces.Add(new GeoFace
                    {
                        PlaneSurface = !(withFold && i == 0), Normal = [0, -1, 0],
                        OuterVertices = [lower[i], lower[next], upper[next], upper[i]],
                    });
                }
            }
        }
    }

    [Theory]
    [InlineData(2, false, true)]
    [InlineData(1, false, false)]
    [InlineData(3, false, false)]
    [InlineData(2, true, false)]
    public void CollectLevel_UsesMediumFixtureOutlineWhenTheModelBodyIsExcluded(
        int detail, bool hasVisibleBody, bool drawsRegion)
    {
        const int viewId = 700;
        var view = new Element { Id = viewId };
        Add(view, BuiltInParameter.VIEW_DETAIL_LEVEL, StorageType.Integer, detail);
        var fixture = new Element
        {
            Id = 800,
            CategoryId = (int)BuiltInCategory.OST_PlumbingFixtures,
            BoundingBox = [1, 1, 0, 3, 2, 1],
            Geometry = new GeometryData
            {
                Faces =
                [
                    new GeoFace
                    {
                        GeometryNode = 1,
                        ExcludedFromTopPlan = !hasVisibleBody,
                        OuterVertices = [[1, 1, 1], [3, 1, 1], [3, 2, 1], [1, 2, 1]],
                    },
                ],
            },
            PlacedPlanFixtureGraphics = new PlanFixtureGraphics([], [], new PlanFixtureProjection(
                [new StyledModelLine([1, 1, 1], [3, 1, 1], 55)],
                [[[1, 1, 1], [3, 1, 1], [3, 2, 1], [1, 2, 1]]])),
        };
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { Level(500, "Main", 0), Wall(600, 500, 0, 0, 10, 0), view, fixture },
            PlanViewRanges = new Dictionary<int, (double[], int[])> { [viewId] = ([4, 8, 0], [500, 500, 500]) },
            OpaquePlanViews = new HashSet<int> { viewId },
        });

        var plan = PlanPrinter.CollectLevel(document, 500, viewId)!;
        Assert.Equal(drawsRegion, plan.FixtureLines is { Count: 1 });
        Assert.Equal(!drawsRegion, plan.FurnitureLines is { Count: > 0 });
        Assert.Single(plan.FurnitureFillRings!);
    }

    [Theory]
    [InlineData(2, 2.0, 1.5, false, true)]
    [InlineData(2, 3.5, 1.5, false, false)]
    [InlineData(2, 2.0, 2.5, false, false)]
    [InlineData(2, 2.0, 1.5, true, false)]
    [InlineData(3, 2.0, 1.5, false, false)]
    public void CollectLevel_UsesMediumFurnitureSymbolWhenTheMaskCoversTheSurvivingBody(
        int detail, double right, double height, bool hidden, bool drawsSymbol)
    {
        const int viewId = 700, furnitureId = 800;
        var view = new Element { Id = viewId };
        Add(view, BuiltInParameter.VIEW_DETAIL_LEVEL, StorageType.Integer, detail);
        var furniture = new Element
        {
            Id = furnitureId, CategoryId = (int)BuiltInCategory.OST_Furniture,
            BoundingBox = [1, 1, 0, Math.Max(right, 3), 3, Math.Max(height, 2)],
            Geometry = new GeometryData
            {
                Faces =
                [
                    new GeoFace
                    {
                        ExcludedFromTopPlan = true,
                        OuterVertices = [[1, 1, 2], [3, 1, 2], [3, 3, 2], [1, 3, 2]],
                    },
                    new GeoFace
                    {
                        OuterVertices = [[1.5, 1.5, height], [right, 1.5, height], [right, 2, height], [1.5, 2, height]],
                    },
                ],
            },
            PlacedPlanFixtureGraphics = new PlanFixtureGraphics([], [], new PlanFixtureProjection(
                [new StyledModelLine([1, 1, 2], [3, 1, 2], 55)],
                [[[1, 1, 2], [3, 1, 2], [3, 3, 2], [1, 3, 2]]])),
        };
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { Level(500, "Main", 0), Wall(600, 500, 0, 0, 10, 0), view, furniture },
            PlanViewRanges = new Dictionary<int, (double[], int[])> { [viewId] = ([4, 8, 0], [500, 500, 500]) },
            OpaquePlanViews = new HashSet<int> { viewId },
            ViewHiddenElements = new Dictionary<int, HashSet<int>> { [viewId] = hidden ? [furnitureId] : [] },
        });

        var plan = PlanPrinter.CollectLevel(document, 500, viewId)!;
        Assert.Equal(drawsSymbol, plan.FixtureLines is { Count: 1 });
        Assert.Equal(!drawsSymbol && !hidden, plan.FurnitureLines is { Count: > 0 });
        if (hidden) Assert.Empty(plan.FurnitureFillRings!);
        else Assert.Single(plan.FurnitureFillRings!);
    }

    [Theory]
    [InlineData(-4, false, true, true)]
    [InlineData(-4, false, false, true)]
    [InlineData(500, false, true, false)]
    [InlineData(-4, true, true, false)]
    public void CollectLevel_PrintsSavedFixtureLinesAndMasksWithinTheViewRange(int bottom, bool hidden, bool opaque, bool visible)
    {
        const int view = 700, fixtureId = 800, styleId = 55;
        var fixture = new Element
        {
            Id = fixtureId, CategoryId = (int)BuiltInCategory.OST_PlumbingFixtures,
            BoundingBox = [1, 1, -1, 2, 2, -1],
            PlacedPlanFixtureGraphics = new PlanFixtureGraphics(
                [new StyledModelLine([1, 1, -1], [2, 2, -1], styleId)],
                [[[1, 1, -1], [1, 2, -1], [2, 2, -1], [2, 1, -1]]]),
        };
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { Level(500, "Main", 0), Wall(600, 500, 0, 0, 10, 0), fixture },
            PlanViewRanges = new Dictionary<int, (double[], int[])> { [view] = ([4, 8, 0, 0, 0], [500, 500, bottom, bottom, -1]) },
            OpaquePlanViews = opaque ? new HashSet<int> { view } : new HashSet<int>(),
            ViewHiddenElements = new Dictionary<int, HashSet<int>> { [view] = hidden ? [fixtureId] : [] },
            GraphicsStylePens = new Dictionary<int, (int, int, int, int, int, int)>
                { [styleId] = ((int)BuiltInCategory.OST_PlumbingFixtures, -1, 1, 1, 255, -1) },
        });

        var content = Assert.IsType<PlanPrinter.PlanContent>(PlanPrinter.CollectLevel(document, 500, view));
        Assert.Equal(visible, content.FixtureLines is { Count: 1 });
        Assert.Equal(visible && opaque ? 1 : 0, content.FurnitureFillRings!.Count);
        var page = new PdfDocument().AddPage(PdfPageSize.A4.Landscape);
        PlanPrinter.DrawContent(page, content, 100, 10, 10, document: document, planViewId: view);
        Assert.Equal(visible, page.Content.Contains("1 0 0 RG", StringComparison.Ordinal));
    }

    [SkippableFact]
    public void SampleFile_Level1FurnitureIsASilhouette()
    {
        // The plan silhouettes each piece of furniture: the boundary of the union of its faces on a 5 mm grid, so a
        // chair keeps its non-convex plan shape (the back, the gap between legs) that a single top face loses.
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);

        var content = PlanPrinter.CollectLevel(document, 311);

        Assert.NotNull(content);
        Assert.NotNull(content!.FurnitureLines);
        Assert.True(content.FurnitureLines!.Count > 100,
            $"Level 1 furniture silhouette drew {content.FurnitureLines.Count} segments");
        // Every segment is one grid edge: no long straight spans (the old face outlines).
        Assert.All(content.FurnitureLines!, line =>
            Assert.True(Math.Abs(line.X2 - line.X1) + Math.Abs(line.Y2 - line.Y1) <= 5.01));
    }

    [Fact]
    public void CollectLevel_ReadsAStairByItsBaseLevel()
    {
        // A stair names its base level (STAIRS_BASE_LEVEL_PARAM), not a level: the plan of that level
        // draws its treads, the way the reference draws every stair of the level.
        var stair = new Element { Id = 801, Name = "Stair", CategoryId = (int)BuiltInCategory.OST_Stairs };
        Add(stair, BuiltInParameter.STAIRS_BASE_LEVEL_PARAM, StorageType.ElementId, 500);
        stair.Geometry = new GeometryData
        {
            Faces =
            [
                new GeoFace
                {
                    Normal = [0, 0, 1],
                    OuterVertices = [[3, 0, 1], [4, 0, 1], [4, 1, 1], [3, 1, 1]],
                },
            ],
        };
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { Level(500, "Level 1", 0), Wall(600, 500, 0, 0, 10, 0), stair },
        });

        var content = PlanPrinter.CollectLevel(document, 500);

        Assert.NotNull(content);
        Assert.Single(content!.Furniture);
    }

    [SkippableFact]
    public void SampleFile_Level1PlanDrawsTheStairTreads()
    {
        // The file's main stair (665557) names Level 1 (311) as its base level: the Level 1 plan collects
        // its tread outlines (x 11602..13062 mm), which the reference draws.
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);

        var content = PlanPrinter.CollectLevel(document, 311);

        Assert.NotNull(content);
        Assert.Contains(content!.Furniture, outline => outline.All(point => point.X > 9000));
    }

    [SkippableFact]
    public void SampleFile_Level1ViewportUsesSavedStairGraphics()
    {
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var content = PlanPrinter.CollectLevel(document, 311, 312)!;
        var run = document.GetElement(665560)!.SerializedStairRunInput!.Plan!;
        Assert.Equal(17, run.Treads.Length);
        Assert.Equal(2.69936, run.Treads[5].Y1, 5);

        var styles = document.RawData.GraphicsStylePens!;
        var lines = content.FixtureLines!;
        var mark = lines.Where(line => styles[line.StyleId].CategoryId == (int)BuiltInCategory.OST_StairsCutMarks).ToArray();
        Assert.Equal(5, mark.Length);
        Assert.Equal(3.92255 * 304.8, Math.Abs(mark[0].X1 - mark[^1].X2), 2);
        Assert.Equal(Math.Abs(mark[0].X1 - mark[^1].X2), Math.Abs(mark[0].Y1 - mark[^1].Y2), 5);
        Assert.Contains(lines, line => styles[line.StyleId].CategoryId == (int)BuiltInCategory.OST_StairsOutlinesAboveCut);
        double outerRailX = document.GetElement(665842)!.BoundingBox![3] * 304.8;
        Assert.Contains(lines, line => styles[line.StyleId].CategoryId == (int)BuiltInCategory.OST_RailingTopRail
            && Math.Abs(line.X1 - outerRailX) < 0.001 && Math.Abs(line.X2 - outerRailX) < 0.001);
        Assert.Contains(lines, line => styles[line.StyleId].CategoryId == (int)BuiltInCategory.OST_RailingTopRailAboveCut
            && Math.Abs(line.X1 - outerRailX) < 0.001 && Math.Abs(line.X2 - outerRailX) < 0.001);
        Assert.DoesNotContain(content.Furniture, ring => ring.All(point => point.X > 11600 && point.X < 13100));
        var exterior = document.GetElement(513309)!.SerializedStairRunInput!.Plan!.Treads;
        Assert.Equal(4, exterior.Length);
        Assert.Contains(content.FurnitureFillRings!, ring => ring.Count == 4
            && ring.Contains((exterior[0].X1 * 304.8, exterior[0].Y1 * 304.8))
            && ring.Contains((exterior[^1].X2 * 304.8, exterior[^1].Y2 * 304.8)));
        Assert.Contains(lines, line => styles[line.StyleId].CategoryId == (int)BuiltInCategory.OST_StairsNosingLines
            && line.X1 == exterior[1].X1 * 304.8 && line.Y1 == exterior[1].Y1 * 304.8);
    }

    [SkippableFact]
    public void SampleFile_Level2ViewportUsesSavedDescendingStairGraphics()
    {
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        const int level = 245423;
        const int view = 245424;
        var plan = document.GetElement(665560)!.SerializedStairRunInput!.Plan!;
        Assert.True(plan.Treads[0].Elevation < PlanPrinter.GetPlanRange(document, level, view)!.Value.Bottom);
        var pathAnnotation = Assert.Single(document.RawData.PlanStairPaths![view], annotation => annotation.HostId == 665557);
        Assert.Equal(3, plan.Center.Length);
        // The fixture is the same model with different element ids: the saved run centre can differ from the
        // sample project's by a few millimetres, so the literals are checked with a 0.01 ft (3 mm) tolerance.
        Assert.True(Math.Abs(plan.Center[2].X1 - 40.89279) < 0.01,
            $"run centre X {plan.Center[2].X1:F5} ft, expected 40.89279");
        Assert.True(Math.Abs(plan.Center[2].Y1 + 8.54264) < 0.01,
            $"run centre Y {plan.Center[2].Y1:F5} ft, expected -8.54264");

        var content = PlanPrinter.CollectLevel(document, level, view)!;
        var styles = document.RawData.GraphicsStylePens!;
        var lines = content.FixtureLines!;
        var rail = Assert.Single(document.Elements, element => element is
            { RootClassName: "TopRail", SerializedOwningElementId: int ownerId }
            && document.GetElement(ownerId)?.SerializedRailingStairHostId == pathAnnotation.HostId);
        double innerRailX = rail.BoundingBox![3] * 304.8;
        var tread = plan.Treads[6];
        double treadY = tread.Y1 * 304.8;
        double outerNosingX = tread.X1 * 304.8;
        var nosing = Assert.Single(lines, line => styles[line.StyleId].CategoryId == (int)BuiltInCategory.OST_StairsNosingLines
            && Math.Abs(line.Y1 - treadY) < 1e-6 && Math.Abs(line.Y2 - treadY) < 1e-6
            && Math.Abs(Math.Max(line.X1, line.X2) - outerNosingX) < 1e-6);
        Assert.Equal(innerRailX, Math.Min(nosing.X1, nosing.X2), 6);
        Assert.Contains(lines, line => styles[line.StyleId].CategoryId == (int)BuiltInCategory.OST_RailingTopRail
            && Math.Abs(line.X1 - innerRailX) < 1e-6 && Math.Abs(line.X2 - innerRailX) < 1e-6
            && Math.Min(line.Y1, line.Y2) < treadY && treadY < Math.Max(line.Y1, line.Y2));
        foreach (var edge in plan.Left.Concat(plan.Right))
            Assert.Contains(lines, line => styles[line.StyleId].CategoryId == (int)BuiltInCategory.OST_StairsOutlines
                && line.X1 == edge.X1 * 304.8 && line.Y1 == edge.Y1 * 304.8
                && line.X2 == edge.X2 * 304.8 && line.Y2 == edge.Y2 * 304.8);
        var mask = Assert.Single(content.FurnitureFillRings!, ring =>
            ring.Contains((plan.Treads[0].X1 * 304.8, plan.Treads[0].Y1 * 304.8))
            && ring.Contains((plan.Treads[^1].X2 * 304.8, plan.Treads[^1].Y2 * 304.8)));
        Assert.Equal(plan.Left.Length + plan.Right.Length + 2, mask.Count);
        Assert.True(PlanPrinter.SignedArea(mask) > 0);

        var pdf = new PdfDocument();
        var page = pdf.AddPage(PdfPageSize.A1.Landscape);
        PlanPrinter.DrawStairPaths(page, document, view, level, content, 100, 0, 0);
        Assert.Equal(4, page.Content.Split(" l S\n", StringSplitOptions.None).Length - 1);

        document.RawData.ViewHiddenElements = new Dictionary<int, HashSet<int>> { [view] = [rail.Id] };
        var withoutRail = PlanPrinter.CollectLevel(document, level, view)!;
        Assert.Contains(withoutRail.FixtureLines!, line =>
            styles[line.StyleId].CategoryId == (int)BuiltInCategory.OST_StairsNosingLines
            && line.X1 == tread.X1 * 304.8 && line.Y1 == treadY
            && line.X2 == tread.X2 * 304.8 && line.Y2 == treadY);

        document.RawData.ViewHiddenElements = new Dictionary<int, HashSet<int>> { [view] = [pathAnnotation.Id] };
        var hidden = pdf.AddPage(PdfPageSize.A1.Landscape);
        PlanPrinter.DrawStairPaths(hidden, document, view, level, content, 100, 0, 0);
        Assert.Empty(hidden.Content);
    }

    [SkippableFact]
    public void SampleFile_Level1ViewportDrawsItsOwnDescendingStairPath()
    {
        string? path = TestFiles.TryFind("Test_2022.rvt") ?? TestFiles.TryFind("rac_basic_sample_project.rvt");
        if (path == null) throw new SkipException("Private fixture 'Test_2022.rvt' is not available.");
        var document = RvtDocument.Open(path);
        var content = PlanPrinter.CollectLevel(document, 311, 312)!;
        var annotation = Assert.Single(document.RawData.PlanStairPaths![312]);
        Assert.Equal(513460, annotation.Id);
        Assert.Equal(513254, annotation.HostId);
        Assert.Equal("DN", annotation.DownText);
        Assert.Equal(1.25, annotation.ArrowSize * 304.8, 8);
        var pdf = new PdfDocument { Font = PdfFonts.TryFind(out _) };
        var page = pdf.AddPage(PdfPageSize.A1.Landscape);
        PlanPrinter.DrawStairPaths(page, document, 312, 311, content, 100, 0, 0);
        Assert.Equal(4, page.Content.Split(" l S\n", StringSplitOptions.None).Length - 1);
        if (pdf.Font != null) Assert.Contains(" Tj", page.Content);

        var hidden = pdf.AddPage(PdfPageSize.A1.Landscape);
        document.RawData.ViewHiddenElements = new Dictionary<int, HashSet<int>> { [312] = [annotation.Id] };
        PlanPrinter.DrawStairPaths(hidden, document, 312, 311, content, 100, 0, 0);
        Assert.Empty(hidden.Content);
    }

    [Fact]
    public void WrapPlanNoteText_CountsTrailingSpaceBeforeLineBreak()
    {
        var page = new PdfDocument().AddPage(PdfPageSize.A4);
        const double size = 3;
        double width = page.MeasureText("Walking bridge", size) + page.MeasureText(" ", size) / 2;

        Assert.Equal(["Walking", "bridge to", "carport"],
            PlanPrinter.WrapPlanNoteText(page, "Walking bridge to carport", size, width, wrap: true));
        Assert.Equal(["Walking bridge to carport"],
            PlanPrinter.WrapPlanNoteText(page, "Walking bridge to carport", size, width, wrap: false));
    }

    [Fact]
    public void DrawContent_ClipsWallsToTheCropRect()
    {
        // A wall outside the crop rectangle draws nothing (the scorer counts vectors, not the
        // page clip); a wall inside keeps its filled poche and four inked edges.
        var document = Document(Level(500, "Level 1", 0),
            Wall(600, 500, 0, 0, 10, 0), Wall(601, 500, 20, 0, 30, 0));
        var content = PlanPrinter.CollectLevel(document, 500)!;
        var pdf = new PdfDocument();
        var page = pdf.AddPage(new PdfPageSize(1000, 1000));
        PlanPrinter.DrawContent(page, content, 100, 10, 10, 0.9, null, document, null, (0, -10000, 4000, 10000));

        string text = Content(pdf);
        Assert.Equal(0, Count(text, " h B"));
        Assert.Equal(1, Count(text, " f\n"));
        Assert.Equal(4, Count(text, " l S\n"));
    }

    [Fact]
    public void DrawContent_KeepsGridsPastTheCropRect()
    {
        // Grids run past the crop to their bubbles (the reference draws them to the bubble row), so the
        // crop clip spares them while walls clip: the wall ends at the crop edge, the grid runs on.
        var document = Document(Level(500, "Level 1", 0),
            Wall(600, 500, 0, 0, 40, 0), Grid(700, 0, 0, 30, 0));
        var content = PlanPrinter.CollectLevel(document, 500)!;
        var pdf = new PdfDocument();
        var page = pdf.AddPage(new PdfPageSize(1000, 1000));
        PlanPrinter.DrawContent(page, content, 100, 10, 10, 0.9, null, document, null, (0, -1000, 3048, 1000));

        string text = Content(pdf);
        double maxX = 0;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
            text, @"([\d.]+) ([\d.]+) m ([\d.]+) ([\d.]+) l S"))
            maxX = Math.Max(maxX, double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture));
        // The clipped wall ends at content x 3048 mm (~143 pt); the spared grid still runs to 9144 mm (~316 pt).
        Assert.True(maxX > 300, $"Grid line clipped (ends at {maxX} pt).");
    }

    [Fact]
    public void DrawContent_DashesGridsWithTheCenterSegmentPattern()
    {
        // Plan grids dash with the center-segment pattern like elevation grids do (the reference draws
        // dashed centerlines, not solids; a solid fills the dash gaps with unscored extras).
        var document = Document(Level(500, "Level 1", 0),
            Wall(600, 500, 0, 0, 40, 0), Grid(700, 0, 0, 30, 0));
        var content = PlanPrinter.CollectLevel(document, 500)!;
        var pdf = new PdfDocument();
        var page = pdf.AddPage(new PdfPageSize(1000, 1000));
        PlanPrinter.DrawContent(page, content, 100, 10, 10, 0.9, null, document, null, (0, -1000, 3048, 1000));

        string text = Content(pdf);
        Assert.Contains("[36", text);
        Assert.Contains("[] 0 d", text);
    }

    [SkippableFact]
    public void DrawContent_DrawsOneDimensionChainAboveTheWallFace()
    {
        // The reference plan draws a single dimension chain, 780 mm (22.1 pt at 1:100) above the outer
        // wall face; its Level 1 has no bottom, left or right chain. The old rule put the chain 550 mm
        // from the wall's location line and invented three more chains, so every reference segment
        // missed ours by more than the scorer's tolerance.
        var document = Document(Level(500, "Level 1", 0),
            Wall(600, 500, 0, 0, 40, 0),
            Grid(700, 0, -5, 0, 5), Grid(701, 10, -5, 10, 5), Grid(702, 20, -5, 20, 5));
        var content = PlanPrinter.CollectLevel(document, 500)!;
        // A dimension chain is measured with its label, so a plan printed without a font has none.
        var font = PdfFonts.TryFind(out _) ?? throw new SkipException("No TrueType font was found on this machine.");
        var pdf = new PdfDocument { Font = font };
        var page = pdf.AddPage(new PdfPageSize(1000, 1000));
        PlanPrinter.DrawContent(page, content, 100, 10, 10, 0.9, null, document);
        string text = Content(pdf);

        var longHorizontals = new List<double>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
            text, @"([\d.]+) ([\d.]+) m ([\d.]+) ([\d.]+) l"))
        {
            double x0 = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            double y0 = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            double x1 = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            double y1 = double.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (Math.Abs(y1 - y0) < 0.01 && Math.Abs(x1 - x0) > 100) longHorizontals.Add(y0);
        }
        longHorizontals.Sort();

        // Two long horizontals: the wall's outer face and, above it, the one top chain. Its offset beyond
        // the face is 780 mm — the reference's measured 22.1 pt at 1:100 — mapped by the same
        // (content - Min) / scale + origin the renderer uses. Before the fix the list held four chains at
        // the location line + 550 mm instead.
        Assert.Equal(2, longHorizontals.Count);
        double expected = ((100.0 + 780.0 - content.MinY) / 100.0 + 10.0) * 72 / 25.4;
        Assert.Equal(expected, longHorizontals[^1], 1);
    }

    private static double Area(List<(double X, double Y)> points)
    {
        double sum = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var (x1, y1) = points[i];
            var (x2, y2) = points[(i + 1) % points.Count];
            sum += x1 * y2 - x2 * y1;
        }
        return sum / 2;
    }

    private static void Add(Element element, BuiltInParameter id, StorageType storage, object value) =>
        element.Parameters.Add(BipHelpers.MakeBipParam((int)id, id.ToString(), storage, value));

    private static Element Level(int id, string name, double elevationFeet)
    {
        var level = new Element { Id = id, Name = name, CategoryId = (int)BuiltInCategory.OST_Levels };
        Add(level, BuiltInParameter.LEVEL_ELEV, StorageType.Double, elevationFeet);
        return level;
    }

    private static Element Wall(int id, int levelId, double x1, double y1, double x2, double y2, double widthFeet = 0.656168)
    {
        var wall = new Element
        {
            Id = id,
            CategoryId = (int)BuiltInCategory.OST_Walls,
            Location = new LocationCurve
            {
                CurveType = "Line",
                StartPoint = [x1, y1, 0],
                EndPoint = [x2, y2, 0],
            },
        };
        Add(wall, BuiltInParameter.LEVEL_PARAM, StorageType.ElementId, levelId);
        Add(wall, BuiltInParameter.WALL_ATTR_WIDTH_PARAM, StorageType.Double, widthFeet);
        return wall;
    }

    private static Element Column(int id, int levelId, double minX, double minY, double maxX, double maxY)
    {
        var column = new Element
        {
            Id = id,
            CategoryId = (int)BuiltInCategory.OST_Columns,
            BoundingBox = [minX, minY, 0, maxX, maxY, 10],
        };
        Add(column, BuiltInParameter.LEVEL_PARAM, StorageType.ElementId, levelId);
        return column;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(true, true)]
    public void CollectLevel_CutsTheColumnAtThePlanHeightAndUsesOnlySavedCylinders(bool analytic, bool rotate = false)
    {
        var column = Column(20, 10, 8, 18, 12, 22);
        column.CategoryId = (int)BuiltInCategory.OST_StructuralColumns;
        double[][] corners = [[-1, -1], [1, -1], [1, 1], [-1, 1]];
        column.Geometry = new GeometryData();
        for (int i = 0; i < corners.Length; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % corners.Length];
            column.Geometry.Faces.Add(new GeoFace
            {
                OuterVertices = [[10 + a[0], 20 + a[1], 0], [10 + b[0], 20 + b[1], 0],
                    [10 + b[0], 20 + b[1], 8], [10 + a[0], 20 + a[1], 8]],
            });
        }
        var symbol = new Element { Id = 21, Geometry = new GeometryData() };
        if (analytic)
        {
            column.Geometry.Instances = [new GeoInstance
            {
                SymbolId = symbol.Id,
                Transform = rotate ? [0, -1, 0, 1, 0, 0, 0, 0, 1, 10, 18, 0] : [1, 0, 0, 0, 1, 0, 0, 0, 1, 10, 20, 0],
            }];
            symbol.Geometry.Faces.Add(new GeoFace
            {
                AnalyticCylinderOrigin = [rotate ? 2 : 0, 0, 0],
                AnalyticCylinderAxis = [0, 0, 1],
                AnalyticCylinderRadius = Math.Sqrt(2),
                OuterVertices = [[-1, -1, 0], [1, -1, 0], [1, -1, 8], [-1, -1, 8]],
            });
        }
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { Level(10, "First", 0), column, symbol },
            PlanViewRanges = new Dictionary<int, (double[], int[])> { [30] = ([4], [10]) },
            GraphicsStylePens = new Dictionary<int, (int, int, int, int, int, int)>
            {
                [40] = ((int)BuiltInCategory.OST_StructuralColumns, -1, 2, 1, 0, -1),
                [41] = ((int)BuiltInCategory.OST_Columns, -1, 2, 2, 0, -1),
            },
            PenWidthTable = (new Dictionary<int, double[]> { [100] = [0.1 / 304.8, 0.25 / 304.8] }, []),
        });

        var content = Assert.IsType<PlanPrinter.PlanContent>(PlanPrinter.CollectLevel(document, 10, 30));
        var profile = Assert.Single(content.ColumnProfiles!).Value;
        var page = new PdfDocument().AddPage(PdfPageSize.A1.Landscape);
        PlanPrinter.DrawContent(page, content, 100, 10, 10, document: document, drawFallbackDimensions: false);

        Assert.Equal(9 * 304.8, profile.Ring.Min(p => p.X), 6);
        Assert.Equal(11 * 304.8, profile.Ring.Max(p => p.X), 6);
        Assert.Equal(analytic, profile.Circle.HasValue);
        Assert.Equal(analytic ? 4 : 0, System.Text.RegularExpressions.Regex.Matches(page.Content, " c ").Count);
        Assert.Contains("0.2835 w", page.Content);
        Assert.Null(PlanPrinter.CollectLevel(document, 10)!.ColumnProfiles);
    }

    private static Element Grid(int id, double x1, double y1, double x2, double y2) => new()
    {
        Id = id,
        CategoryId = (int)BuiltInCategory.OST_Grids,
        Location = new LocationCurve { CurveType = "Line", StartPoint = [x1, y1, 0], EndPoint = [x2, y2, 0] },
    };

    private static RvtDocument Document(params Element[] elements) =>
        RvtDocument.FromInfo(new RevitFileInfo { Elements = elements.ToList() });

    [Fact]
    public void Levels_ListsOnlyLevelsWithADrawableWallOrColumn()
    {
        var document = Document(
            Level(1, "First", 0), Level(2, "Second", 10), Level(3, "Empty", 20),
            Wall(100, 1, 0, 0, 10, 0), Wall(101, 1, 10, 0, 10, 5), Wall(102, 2, 0, 0, 3, 0),
            new Element { Id = 200, CategoryId = (int)BuiltInCategory.OST_Walls });

        var levels = PlanPrinter.Levels(document);

        Assert.Equal(new[] { 1, 2 }, levels.Select(level => level.LevelId));
        Assert.Equal("First", levels[0].Name);
        Assert.Equal(0d, levels[0].ElevationFeet);
    }

    [Fact]
    public void Render_DrawsTheLevelWithTheMostWallsByDefault()
    {
        var document = Document(
            Level(1, "First", 0), Level(2, "Second", 10),
            Wall(100, 1, 0, 0, 10, 0),
            Wall(101, 2, 0, 0, 10, 0), Wall(102, 2, 10, 0, 10, 5));

        PdfDocument pdf = PlanPrinter.Render(document);

        Assert.Single(pdf.Pages);
        // 10 m at 1:100 is 100 mm, which fits A4 landscape with a 10 mm margin.
        Assert.Equal(PdfPageSize.A4.Landscape, pdf.Pages[0].Size);
    }

    [Fact]
    public void Render_DrawsTwoSidesOfAWallAndItsEndCaps()
    {
        var document = Document(Level(1, "First", 0), Wall(100, 1, 0, 0, 10, 0, widthFeet: 1));

        PdfDocument pdf = PlanPrinter.Render(document, levelId: 1);
        string content = Content(pdf);

        // 10 m long, 1 ft = 304.8 mm wide: the stretch between the two sides is poche, filled and inked as one
        // polygon.
        Assert.Equal(1, Count(content, " h B"));
        Assert.Contains("0.72 0.72 0.72 rg", content);
    }

    // A door in a wall with a plan symbol of its own. Directional: the symbol has no height range, and the door has no
    // saved cutout and is placed by its bounds.
    private static (Element Wall, Element Door) DoorWithPlanSymbol(bool directional)
    {
        var wall = Wall(100, 1, 0, 0, 10, 0, widthFeet: 1);
        var door = new Element
        {
            Id = 200,
            CategoryId = (int)BuiltInCategory.OST_Doors,
            HostId = 100,
            SerializedHostId = 100,
            OwnSingleFamilyCutout = (300, 100, new double[]
                {
                    1, 0, 0,
                    0, 1, 0,
                    0, 0, 1,
                    4.5, 0, 0,
                },
                new FamilyCutLoopData(
                    new[] { new[] { -0.5, 0.0, 0.0 }, new[] { 0.5, 0.0, 0.0 },
                            new[] { 0.5, 0.0, 3.0 }, new[] { -0.5, 0.0, 3.0 } },
                    new[] { 0.0, 1.0, 0.0 }, 0)),
        };
        door.OwnSingleFamilyCutout!.Value.Transform[4] = -1;
        door.OwnDoorPlanSymbols =
        ([
            new DoorPlanSymbol(directional ? null : (0, 3),
            [
                new GRepParserV2.ArcRec { C = [0, 0, 0], X = [0, 1, 0], T0 = 0, T1 = 1 },
                new GRepParserV2.ArcRec { C = [0, 1, 0], X = [1, 0, 0], T0 = 0, T1 = 1 },
                new GRepParserV2.ArcRec { C = [0, 0, 0], X = [1, 0, 0], Y = [0, 1, 0], R = 1, T0 = 0, T1 = Math.PI / 2 },
            ]),
        ], door.OwnSingleFamilyCutout.Value.Transform);
        if (directional)
        {
            door.OwnSingleFamilyCutout = null;
            door.BoundingBox = [4, -1, 0, 5, 1, 3];
        }
        return (wall, door);
    }

    [Theory]
    [InlineData(1.0, false, true)]
    [InlineData(5.0, false, false)]
    [InlineData(1.0, true, true)]
    [InlineData(5.0, true, false)]
    public void Render_UsesTheDoorSymbolAtThePlanCut(double cut, bool directional, bool savedSymbol)
    {
        var (wall, door) = DoorWithPlanSymbol(directional);
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { Level(1, "First", 0), wall, door },
            PlanViewRanges = new Dictionary<int, (double[], int[])> { [30] = ([cut, 0, 0, 0, 0], [1, 1, 1, 1, 1]) },
        });
        var plan = Assert.IsType<PlanPrinter.PlanContent>(PlanPrinter.CollectLevel(document, 1, 30));
        var page = new PdfDocument().AddPage(PdfPageSize.A4.Landscape);
        PlanPrinter.DrawContent(page, plan, 100, plan.MinX / 100, plan.MinY / 100);

        Assert.Equal(directional ? 1 : 2, Count(page.Content, " h B"));
        Assert.Equal(savedSymbol ? 2 : directional ? 0 : 1, Count(page.Content, " l S"));
        Assert.Equal(savedSymbol || !directional, page.Content.Contains(" c ", StringComparison.Ordinal));
        Assert.Equal(!savedSymbol && !directional, page.Content.Contains("0.4 G", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void CollectLevel_HiddenDoorDropsItsSavedPlanSymbolAndKeepsItsWallGap(bool hidden, bool categoryHidden, bool drawn)
    {
        const int view = 30;
        var (wall, door) = DoorWithPlanSymbol(directional: false);
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { Level(1, "First", 0), wall, door },
            PlanViewRanges = new Dictionary<int, (double[], int[])> { [view] = ([1, 0, 0, 0, 0], [1, 1, 1, 1, 1]) },
            ViewHiddenElements = new Dictionary<int, HashSet<int>> { [view] = hidden ? [door.Id] : [] },
            ViewHiddenCategories = new Dictionary<int, HashSet<int>>
                { [view] = categoryHidden ? [(int)BuiltInCategory.OST_Doors] : [] },
        });

        var plan = Assert.IsType<PlanPrinter.PlanContent>(PlanPrinter.CollectLevel(document, 1, view));

        Assert.Equal(drawn, plan.DoorSymbols is { Count: > 0 });
        Assert.Single(Assert.Contains(wall.Id, plan.Gaps));
    }

    [Theory]
    [InlineData(2, false, true, true)]
    [InlineData(2, true, true, false)]
    [InlineData(7, false, true, false)]
    [InlineData(-2, false, true, false)]
    [InlineData(7, false, false, true)]
    public void CollectLevel_UsesTheSavedViewRangeAndVisibilityForRoofOpeningLoops(double height, bool hidden,
        bool savedView, bool expected)
    {
        const int viewId = 30, openingId = 200, roofId = 400;
        var opening = new Element
        {
            Id = openingId,
            CategoryId = (int)BuiltInCategory.OST_Windows,
            OwnSingleFamilyCutout = (300, roofId, [1, 0, 0, 0, 1, 0, 0, 0, 1, 2, 1, height],
                new FamilyCutLoopData([[0, 0, 0], [1, 0, 0], [1, 1, 0], [0, 1, 0]], [0, 0, 1], 0)),
        };
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { Level(1, "First", 0), Wall(100, 1, 0, 0, 10, 0), opening,
                new Element { Id = roofId, CategoryId = (int)BuiltInCategory.OST_Roofs } },
            PlanViewRanges = new Dictionary<int, (double[], int[])> { [viewId] = ([4, 6, 0], [1, 1, 1]) },
            ViewHiddenElements = new Dictionary<int, HashSet<int>> { [viewId] = hidden ? [openingId] : [] },
        });
        var content = Assert.IsType<PlanPrinter.PlanContent>(
            PlanPrinter.CollectLevel(document, 1, savedView ? viewId : null));

        Assert.Equal(expected, content.OpeningLoops.Count > 0);
    }

    private static RvtDocument HostedOpening(BuiltInCategory category, bool hidden, bool categoryHidden,
        out int viewId, out int hostId)
    {
        viewId = 30;
        hostId = 100;
        const int openingId = 200;
        var opening = new Element
        {
            Id = openingId, CategoryId = (int)category,
            OwnSingleFamilyCutout = (300, hostId, [1, 0, 0, 0, 1, 0, 0, 0, 1, 2, 0, 0],
                new FamilyCutLoopData([[0, 0, 0], [2, 0, 0], [2, 0, 4], [0, 0, 4]], [0, 1, 0], 0)),
        };
        Add(opening, BuiltInParameter.ALL_MODEL_MARK, StorageType.String, "1");
        return RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = [Level(1, "First", 0), Wall(hostId, 1, 0, 0, 10, 0), opening],
            ViewHiddenElements = new Dictionary<int, HashSet<int>> { [viewId] = hidden ? [openingId] : [] },
            ViewHiddenCategories = new Dictionary<int, HashSet<int>> { [viewId] = categoryHidden ? [(int)category] : [] },
        });
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_Doors, false, false, true)]
    [InlineData(BuiltInCategory.OST_Doors, true, false, false)]
    [InlineData(BuiltInCategory.OST_Doors, false, true, false)]
    [InlineData(BuiltInCategory.OST_Windows, false, false, true)]
    [InlineData(BuiltInCategory.OST_Windows, true, false, false)]
    [InlineData(BuiltInCategory.OST_Windows, false, true, false)]
    public void CollectLevel_HiddenOpeningKeepsItsWallGapAndDropsItsOwnGraphics(
        BuiltInCategory category, bool hidden, bool categoryHidden, bool visible)
    {
        var document = HostedOpening(category, hidden, categoryHidden, out int viewId, out int hostId);

        var plan = Assert.IsType<PlanPrinter.PlanContent>(PlanPrinter.CollectLevel(document, 1, viewId));

        // The opening belongs to the wall, so it stays when its door or window is hidden; the frame, leaf, glass and
        // tag of the insert itself go.
        var gap = Assert.Single(Assert.Contains(hostId, plan.Gaps));
        Assert.Equal(2 * 304.8, gap.From, 3);
        Assert.Equal(4 * 304.8, gap.To, 3);
        Assert.Equal(visible, plan.OpeningLoops.Count > 0);
        Assert.Equal(visible, plan.Tags.Count > 0);
        Assert.Equal(visible && category == BuiltInCategory.OST_Doors, plan.DoorGaps.Count > 0);
        Assert.Equal(visible && category == BuiltInCategory.OST_Windows, plan.WindowGaps.Count > 0);
    }

    private static Element MarkedOpening(int id, BuiltInCategory category, double from, double to, string mark)
    {
        var opening = new Element
        {
            Id = id, CategoryId = (int)category,
            OwnSingleFamilyCutout = (300 + id, 100, [1, 0, 0, 0, 1, 0, 0, 0, 1, from, 0, 0],
                new FamilyCutLoopData([[0, 0, 0], [to - from, 0, 0], [to - from, 0, 4], [0, 0, 4]], [0, 1, 0], 0)),
        };
        Add(opening, BuiltInParameter.ALL_MODEL_MARK, StorageType.String, mark);
        return opening;
    }

    // A saved tag of the view's own, or none, over a wall with a door D1 and a window W1; counts the texts drawn. The door
    // is hidden through its category or through its own id.
    private static int OpeningTagTexts(bool savedDoorTag, bool doorsHidden, bool windowTagged = false,
        bool doorHiddenById = false)
    {
        const int viewId = 30;
        var tags = new List<RevitFileInfo.PlanOpeningTag>();
        if (savedDoorTag) tags.Add(new(200, 900, 2, 3));
        if (windowTagged) tags.Add(new(201, 900, 6, 3));
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = [Level(1, "First", 0), Wall(100, 1, 0, 0, 10, 0),
                MarkedOpening(200, BuiltInCategory.OST_Doors, 1, 3, "D1"),
                MarkedOpening(201, BuiltInCategory.OST_Windows, 5, 7, "W1")],
            ViewHiddenCategories = new Dictionary<int, HashSet<int>>
                { [viewId] = doorsHidden ? [(int)BuiltInCategory.OST_Doors] : [] },
            ViewHiddenElements = doorHiddenById ? new Dictionary<int, HashSet<int>> { [viewId] = [200] } : null,
            PlanOpeningTags = tags.Count == 0 ? null : new Dictionary<int, List<RevitFileInfo.PlanOpeningTag>> { [viewId] = tags },
            PlanOpeningTagStyles = new Dictionary<int, RevitFileInfo.PlanOpeningTagStyle>
            {
                [900] = new(new TfBlockDeserializer.TitleBlockLabel(0, 0, 0, 0, 1, 1, 0, 0.01, 0.08, 1, 0,
                    (int)BuiltInParameter.ALL_MODEL_MARK, "", ""), 10, [], []),
            },
        });
        var font = PdfFonts.TryFind(out _) ?? throw new SkipException("No TrueType font was found on this machine.");
        var pdf = new PdfDocument { Font = font };
        PlanPrinter.DrawContent(pdf.AddPage(PdfPageSize.A3), PlanPrinter.CollectLevel(document, 1, viewId)!, 100, 20, 20,
            0.9, null, document, planViewId: viewId);
        return Count(Content(pdf), " Tj ET");
    }

    [SkippableFact]
    public void DrawContent_OpeningMarksUseTheViewsSavedTagsAndKeepGeneratedOnesForAViewWithoutAny()
    {
        // No saved tag: the generated marks of both openings.
        Assert.Equal(2, OpeningTagTexts(savedDoorTag: false, doorsHidden: false));
        // A saved tag of the door stands for the view's own tags, so the untagged window gets no generated mark.
        Assert.Equal(1, OpeningTagTexts(savedDoorTag: true, doorsHidden: false));
        Assert.Equal(2, OpeningTagTexts(savedDoorTag: true, doorsHidden: false, windowTagged: true));
    }

    [SkippableFact]
    public void DrawContent_HidingTheTaggedHostsDropsTheirTagsWithoutGeneratingMarksForTheRest()
    {
        // The door tag is not printed with its hidden category, and the visible window does not take a generated mark
        // in its place: the view still has tags of its own.
        Assert.Equal(0, OpeningTagTexts(savedDoorTag: true, doorsHidden: true));
        // Only the window's saved tag remains.
        Assert.Equal(1, OpeningTagTexts(savedDoorTag: true, doorsHidden: true, windowTagged: true));
    }

    [SkippableFact]
    public void DrawContent_HidingTheTaggedDoorByItsIdDropsItsTagLikeHidingItsCategory()
    {
        Assert.Equal(0, OpeningTagTexts(savedDoorTag: true, doorsHidden: false, doorHiddenById: true));
        Assert.Equal(1, OpeningTagTexts(savedDoorTag: true, doorsHidden: false, windowTagged: true, doorHiddenById: true));
    }

    [Theory]
    [InlineData(BuiltInCategory.OST_Doors, true, false)]
    [InlineData(BuiltInCategory.OST_Doors, false, true)]
    [InlineData(BuiltInCategory.OST_Windows, true, false)]
    [InlineData(BuiltInCategory.OST_Windows, false, true)]
    public void DrawContent_HiddenOpeningStillBreaksTheWall(BuiltInCategory category, bool hidden, bool categoryHidden)
    {
        string Draw(bool hide, bool hideCategory)
        {
            var document = HostedOpening(category, hide, hideCategory, out int viewId, out _);
            var content = PlanPrinter.CollectLevel(document, 1, viewId)!;
            var pdf = new PdfDocument();
            PlanPrinter.DrawContent(pdf.AddPage(PdfPageSize.A3), content, 100, 20, 20, 0.9, null, document, planViewId: viewId);
            return Content(pdf);
        }

        string shown = Draw(false, false), hiddenContent = Draw(hidden, categoryHidden);

        // The wall is drawn in two pieces either way, but only the visible insert adds its jambs, leaf or glass.
        Assert.Equal(2, Count(shown, " h B"));
        Assert.Equal(2, Count(hiddenContent, " h B"));
        Assert.True(hiddenContent.Length < shown.Length);
    }

    [Theory]
    [InlineData("A", true)]
    [InlineData("Ж", false)]
    public void DrawPlanGrids_WithoutAFontWritesOnlyANameTheStandardFontHolds(string name, bool drawn)
    {
        var document = Document(Level(1, "First", 0), Wall(100, 1, 0, 0, 10, 0));
        var content = PlanPrinter.CollectLevel(document, 1)!;
        var page = new PdfDocument().AddPage(PdfPageSize.A3);
        var grid = new PlanPrinter.PlanGrid(0, name, 5, (0, 0), (0, 3000), true, false, null, null);

        // The standard PDF font holds printable ASCII only; a grid named outside it keeps its bubble and line.
        PlanPrinter.DrawPlanGrids(page, document, [grid], content, 100, 20, 20);

        Assert.Equal(drawn, page.Content.Contains($"({name}) Tj", StringComparison.Ordinal));
        Assert.Contains(" l S", page.Content);
    }

    [Fact]
    public void Render_DrawsAColumnAsAFilledRectangle()
    {
        var document = Document(Level(1, "First", 0), Wall(100, 1, 0, 0, 10, 0), Column(200, 1, 2, 2, 2.5, 2.5));

        PdfDocument pdf = PlanPrinter.Render(document, levelId: 1);
        string content = Content(pdf);

        Assert.Contains(" re B", content);
    }

    [Fact]
    public void Render_ClipsAGridThatRunsFarBeyondTheLevel()
    {
        var document = Document(
            Level(1, "First", 0), Wall(100, 1, 0, 0, 10, 0),
            Grid(300, -1000, 0, 1000, 0));

        PdfDocument pdf = PlanPrinter.Render(document, levelId: 1);
        string content = Content(pdf);

        // The grid is drawn (one line), clipped to the level's extent; the page stays the one the wall alone would
        // choose and does not grow to the kilometres the raw grid line spans.
        Assert.Equal(1, Count(content, " l S"));
        Assert.Equal(1, Count(content, " h B"));
        Assert.Equal(PdfPageSize.A4.Landscape, pdf.Pages[0].Size);
    }

    [Fact]
    public void Render_UsesTheGivenPaperAndScale()
    {
        var document = Document(Level(1, "First", 0), Wall(100, 1, 0, 0, 10, 0));

        PdfDocument pdf = PlanPrinter.Render(document, 1, scale: 200, new PlanPrintOptions { PaperSize = PdfPageSize.A3 });
        string content = Content(pdf);

        Assert.Equal(PdfPageSize.A3, pdf.Pages[0].Size);
        Assert.Contains("/MediaBox [0 0 841.8898 1190.5512]", content);
    }

    [Fact]
    public void Render_ThrowsWhenTheLevelHasNoDrawableElement()
    {
        var document = Document(Level(1, "First", 0), new Element { Id = 5, CategoryId = (int)BuiltInCategory.OST_Walls });

        var error = Assert.Throws<InvalidOperationException>(() => PlanPrinter.Render(document));

        Assert.Contains("no level", error.Message);
    }

    [Fact]
    public void Render_RejectsANegativeScale()
    {
        var document = Document(Level(1, "First", 0), Wall(100, 1, 0, 0, 10, 0));

        Assert.Throws<ArgumentOutOfRangeException>(() => PlanPrinter.Render(document, 1, scale: 0));
    }

    private static string Content(PdfDocument pdf)
    {
        using var stream = new MemoryStream();
        pdf.Save(stream);
        return PdfTestContent.Read(stream.ToArray());
    }

    private static int Count(string text, string value)
    {
        int count = 0, at = 0;
        while ((at = text.IndexOf(value, at, StringComparison.Ordinal)) >= 0) { count++; at += value.Length; }
        return count;
    }

    [Fact]
    public void InViewBasis_RotatesWallsRoomsAndBounds()
    {
        // The building overlay of a turned site plan uses the same view axes as the site: a quarter turn maps
        // (x, y) to (y, -x), so the wall and the room move with the plan and the bounds swap.
        var plan = new PlanPrinter.PlanContent(
            Walls: [(1, 0, 0, 1000, 0, 200)],
            Columns: [(500, 250, 100, 100)],
            Grids: [],
            Gaps: new Dictionary<int, List<(double From, double To)>> { [1] = [(100, 200)] },
            WindowGaps: [],
            DoorGaps: [],
            FloorLoops: [[(0, 0), (1000, 0), (1000, 500), (0, 500)]],
            OpeningLoops: [],
            Rooms: [([(0, 0), (1000, 0), (1000, 500), (0, 500)], "Living", "101", 500)],
            Furniture: [],
            Tags: [(500, 250, 1, 0, "Tag", false)],
            MinX: 0, MinY: 0, MaxX: 1000, MaxY: 500);

        var turned = PlanPrinter.InViewBasis(plan, [0, 1, 0], [-1, 0, 0]);

        var wall = Assert.Single(turned.Walls);
        Assert.Equal(0, wall.X1, 6);
        Assert.Equal(0, wall.Y1, 6);
        Assert.Equal(0, wall.X2, 6);
        Assert.Equal(-1000, wall.Y2, 6);
        var room = Assert.Single(turned.Rooms);
        Assert.Contains(room.Polygon, point => Math.Abs(point.X - 500) < 1e-6 && Math.Abs(point.Y + 1000) < 1e-6);
        Assert.Equal(-1000, turned.MinY, 6);
        Assert.Equal(500, turned.MaxX, 6);
        // The gaps stay attached to their wall and keep their span along it.
        Assert.Equal([(100.0, 200.0)], turned.Gaps[1]);
    }
}
