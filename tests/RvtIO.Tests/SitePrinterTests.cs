using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using RvtIO.Print;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The site printer (src/RvtIO/Print/SitePrinter): the topography faces and the planting of a site view.
/// </summary>
public class SitePrinterTests
{
    private const double FeetToMillimetres = 304.8;

    [Theory]
    [InlineData(3, 4, false, "N", "36° 52' 12\"", "E")]
    [InlineData(-3, -4, true, "Ю", "36° 52' 12\"", "З")]
    [InlineData(1, 0, false, "N", "90° 00' 00\"", "E")]
    public void PropertyBearings_UseTheQuadrantAndRoundToASecond(double east, double north, bool russian,
        string ns, string angle, string ew)
    {
        Assert.Equal((ns, angle, ew), SitePrinter.PropertyBearing(east, north, russian));
    }

    [Fact]
    public void PropertyDashes_FitBothEndsWithoutRescalingTheInterior()
    {
        double[] pattern = [45, 13.5, 18, 13.5, 18, 13.5];
        var fitted = LinePatterns.FitEndpointDashes(155.6, pattern);
        Assert.Equal(39.55, fitted[0], 8);
        Assert.Equal(fitted[0], fitted[^2]);
        Assert.Equal(pattern[1..], fitted[1..pattern.Length]);
        Assert.Equal(155.6, fitted[..^1].Sum(), 8);
        Assert.Empty(LinePatterns.FitEndpointDashes(100, pattern));
    }

    private static Element Topography(int id, params double[][] vertices) => new()
    {
        Id = id,
        CategoryId = (int)BuiltInCategory.OST_Topography,
        Geometry = new GeometryData
        {
            Faces = [new GeoFace { Normal = [0, 0, 1], OuterVertices = vertices.ToList() }],
        },
    };

    private static Element Plant(int id, double x, double y, double size)
    {
        var plant = new Element { Id = id, CategoryId = (int)BuiltInCategory.OST_Planting };
        plant.BoundingBox =
        [
            x - size / 2, y - size / 2, 0,
            x + size / 2, y + size / 2, 10,
        ];
        return plant;
    }

    // A boundary segment carries the category of the segment, while a view hides the boundary as "Property Lines".
    private static Element PropertySegment(int id) => new()
    {
        Id = id, CategoryId = (int)BuiltInCategory.OST_SitePropertyLineSegment, SitePropertyCurve = [(0, 0), (5, 0)],
    };

    [Theory]
    [InlineData(false, null, false, true)]
    [InlineData(true, null, false, false)]
    [InlineData(false, BuiltInCategory.OST_SitePropertyLineSegment, false, false)]
    [InlineData(false, BuiltInCategory.OST_SiteProperty, false, false)]
    [InlineData(false, BuiltInCategory.OST_Topography, false, true)]
    [InlineData(false, null, true, false)]
    public void Collect_UsesOnlyPlacedVisiblePropertyLines(bool hidden, BuiltInCategory? hiddenCategory, bool unplaced,
        bool visible)
    {
        const int view = 10;
        var property = PropertySegment(20);
        if (unplaced) property.SerializedUnplacedOwnerId = 30;
        var doc = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = [property],
            ViewHiddenElements = new Dictionary<int, HashSet<int>> { [view] = hidden ? [property.Id] : [] },
            ViewHiddenCategories = new Dictionary<int, HashSet<int>>
                { [view] = hiddenCategory is { } category ? [(int)category] : [] },
        });

        var site = SitePrinter.Collect(doc, view);
        if (visible)
            Assert.Single(site!.PropertyLines!);
        else
            Assert.Null(site);
        // A view that hides nothing keeps the boundary, unless it is not placed at all.
        Assert.Equal(!unplaced, SitePrinter.Collect(doc, view + 1) != null);
    }

    [SkippableTheory]
    [InlineData(null, null, true)]
    [InlineData(null, BuiltInCategory.OST_SiteProperty, false)]
    [InlineData(null, BuiltInCategory.OST_SitePropertyLineSegment, false)]
    [InlineData(20, null, false)]
    public void DrawPropertyTags_FollowTheBoundaryTheViewShows(int? hiddenElement, BuiltInCategory? hiddenCategory,
        bool drawn)
    {
        const int view = 10, symbol = 500;
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = [PropertySegment(20)],
            ViewHiddenElements = new Dictionary<int, HashSet<int>>
                { [view] = hiddenElement is { } element ? [element] : [] },
            ViewHiddenCategories = new Dictionary<int, HashSet<int>>
                { [view] = hiddenCategory is { } category ? [(int)category] : [] },
            SitePropertyTags = new Dictionary<int, List<RevitFileInfo.PlanOpeningTag>>
                { [view] = [new(20, symbol, 2, 0.1)] },
            SitePropertyTagStyles = new Dictionary<int, IReadOnlyList<(TfBlockDeserializer.TitleBlockLabel Label, double FontPoints)>>
            {
                [symbol] = [(new(0, 0, 0, 0, 1, 1, 0, 0.01, 0.08, 1, 0, (int)BuiltInParameter.PROPERTY_SEGMENT_N_S, "", ""), 10)],
            },
            PropertyBearingFormat = (1, 1),
            SharedTransform = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0],
            SharedTransformIsActiveLocation = true,
            SerializedFormatSettings = new Compact.CompactFormatSettings(new TfBlockDeserializer.UnitOptions([], 1, 0, 1, 1),
                new UnitSchemaEvaluator.UnitConversions([], []), new Dictionary<string, Compact.CompactSymbolLabel>()),
        });
        var site = SitePrinter.Collect(document)!;
        var font = RvtIO.Pdf.PdfFonts.TryFind(out _) ?? throw new SkipException("No TrueType font was found on this machine.");
        var page = new RvtIO.Pdf.PdfDocument { Font = font }.AddPage(RvtIO.Pdf.PdfPageSize.A3);

        SitePrinter.DrawPropertyTags(page, document, view, null, site, 100, 20, 20);

        Assert.Equal(drawn, page.Content.Contains(" Tj", StringComparison.Ordinal));
    }

    [Fact]
    public void Collect_ProjectsTheTopographyAndThePlanting()
    {
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements =
            {
                Topography(100, [-5, -5, 0], [5, -5, 0], [5, 5, 0], [-5, 5, 0]),
                Plant(200, 2, 2, 4),
            },
        });

        var site = SitePrinter.Collect(document);

        Assert.NotNull(site);
        Assert.Single(site!.Topography);
        var plant = Assert.Single(site.Plants);
        Assert.Equal(2 * FeetToMillimetres, plant.X, 3);
        Assert.Equal(2 * FeetToMillimetres, plant.Radius, 3);
        Assert.Equal(-5 * FeetToMillimetres, site.MinX, 3);
    }

    [Fact]
    public void Collect_UsesTheSavedRpcHeightForThePlanSymbol()
    {
        var type = new Element { Id = 201, CategoryId = (int)BuiltInCategory.OST_Planting, NativeIsSymbol = true };
        type.Parameters.Add(BipHelpers.MakeBipParam((int)BuiltInParameter.RENDER_RPC_FILENAME,
            "RENDER_RPC_FILENAME", StorageType.String, "RPC tree"));
        type.Parameters.Add(BipHelpers.MakeBipParam((int)BuiltInParameter.RENDER_PLANT_HEIGHT,
            "RENDER_PLANT_HEIGHT", StorageType.Double, 42.0));
        var plant = Plant(200, 2, 2, 4);
        plant.TypeId = type.Id;
        plant.InstanceOriginXY = (3, 4);
        var document = RvtDocument.FromInfo(new RevitFileInfo { Elements = { type, plant } });

        var site = SitePrinter.Collect(document)!;
        var symbol = Assert.Single(site.Plants);
        Assert.True(symbol.Rpc);
        Assert.Equal(11.55 * FeetToMillimetres, symbol.Radius, 6);
        Assert.Equal(3 * FeetToMillimetres, symbol.X, 6);
        Assert.Equal(4 * FeetToMillimetres, symbol.Y, 6);

        var page = new RvtIO.Pdf.PdfDocument().AddPage(RvtIO.Pdf.PdfPageSize.A4);
        SitePrinter.DrawContent(page, site, 200, 0, 0, document, drawFallbackTags: false, drawFallbackNorth: false);
        Assert.Equal(80, page.Content.Split(" l S\n", StringSplitOptions.None).Length - 1);

        var ordinaryPlant = RvtDocument.FromInfo(new RevitFileInfo { Elements = { Plant(200, 2, 2, 4) } });
        var fallback = Assert.Single(SitePrinter.Collect(ordinaryPlant)!.Plants);
        Assert.False(fallback.Rpc);
        Assert.Equal(2 * FeetToMillimetres, fallback.Radius, 6);
    }

    [Fact]
    public void Collect_UsesTheSiteViewsSavedCategoryVisibility()
    {
        const int siteView = 17;
        const int entourageCategory = (int)BuiltInCategory.OST_Entourage;
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements =
            {
                Plant(200, 2, 2, 4),
                new Element { Id = 300, CategoryId = entourageCategory, BoundingBox = [9, 9, 0, 12, 13, 5] },
            },
            ViewHiddenCategories = new Dictionary<int, HashSet<int>> { [siteView] = [entourageCategory] },
        });

        var site = Assert.IsType<SitePrinter.SiteContent>(SitePrinter.Collect(document, siteView));
        Assert.Empty(site.Entourage);
        Assert.Single(site.Plants);
        Assert.Single(SitePrinter.Collect(document, siteView + 1)!.Entourage);
        Assert.Single(SitePrinter.Collect(document)!.Entourage);
    }

    [Fact]
    public void Collect_UsesStoredContoursAndPlacedPlantOrigins()
    {
        var placed = Topography(100, [0, 0, 0], [5, 0, 5], [5, 5, 5], [0, 5, 0]);
        var unplaced = Topography(101, [8, 8, 0], [9, 8, 4], [9, 9, 4]);
        unplaced.SerializedUnplacedOwnerId = 20;
        var plant = Plant(200, 2, 2, 4);
        plant.InstanceOriginXY = (3, 4);
        var symbol = Plant(201, 0, 0, 20);
        symbol.NativeIsSymbol = true;
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { placed, unplaced, plant, symbol },
            SiteContours = new Dictionary<int, List<TfBlockDeserializer.SiteContour>>
            {
                [100] = [new(2, true, [[(2, 1), (2, 4)]])],
                [101] = [new(2, false, [[(8, 8), (9, 9)]])],
            },
        });

        var site = Assert.IsType<SitePrinter.SiteContent>(SitePrinter.Collect(document));
        Assert.Single(site.Topography);
        var contour = Assert.Single(site.Contours);
        Assert.Equal((2 * FeetToMillimetres, 1 * FeetToMillimetres, 2 * FeetToMillimetres, 4 * FeetToMillimetres,
            2 * FeetToMillimetres), contour);
        Assert.Contains(contour.Z, site.NativePrimaryLevels!);
        var entry = Assert.Single(site.Plants);
        Assert.Equal((3 * FeetToMillimetres, 4 * FeetToMillimetres), (entry.X, entry.Y));
    }

    [Fact]
    public void RoofProjection_UsesViewRangeAndRelativeLight()
    {
        var level = new Element { Id = 10, CategoryId = (int)BuiltInCategory.OST_Levels };
        level.Parameters.Add(BipHelpers.MakeBipParam((int)BuiltInParameter.LEVEL_ELEV,
            "LEVEL_ELEV", StorageType.Double, 0d));
        var active = Topography(1, [0, 0, 5], [4, 0, 5], [4, 4, 5], [0, 4, 5]);
        var aboveCut = Topography(2, [0, 0, 25], [4, 0, 25], [4, 4, 25], [0, 4, 25]);
        active.CategoryId = aboveCut.CategoryId = (int)BuiltInCategory.OST_Roofs;
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { level, active, aboveCut },
            PlanViewRanges = new Dictionary<int, (double[], int[])>
            {
                [20] = ([20, 20, 0], [10, 10, 10]),
            },
            ViewGroundShadows = new Dictionary<int, (double, double, int, int)>
            {
                [20] = (Math.PI / 2, Math.PI / 4, 10, 60),
            },
        });

        var roof = Assert.IsType<SitePrinter.SiteGraphics>(SitePrinter.CollectOpaqueGraphics(document, 20, null));
        Assert.Single(roof.Surfaces);
        Assert.Equal(0.4, roof.ShadowGray, 8);
        var shadow = Assert.Single(roof.GroundShadows);
        Assert.Equal(-5 * FeetToMillimetres, shadow[0].X, 7);
        Assert.Equal(0, shadow[0].Y, 7);
        Assert.Null(SitePrinter.CollectOpaqueGraphics(document, 21, null));
    }

    [Theory]
    [InlineData(true, false, 7, true)]
    [InlineData(false, false, 7, false)]
    [InlineData(true, true, 7, false)]
    [InlineData(true, false, 21, false)]
    public void RoofProjection_ClipsVisibleHostedGlazingToTheOpening(bool hosted, bool hidden,
        double height, bool expected)
    {
        var level = new Element { Id = 10, CategoryId = (int)BuiltInCategory.OST_Levels };
        level.Parameters.Add(BipHelpers.MakeBipParam((int)BuiltInParameter.LEVEL_ELEV,
            "LEVEL_ELEV", StorageType.Double, 0d));
        var roof = Topography(1, [0, 0, 6], [8, 0, 6], [8, 8, 6], [0, 8, 6]);
        roof.CategoryId = (int)BuiltInCategory.OST_Roofs;
        roof.Geometry!.Faces[0].InnerVertices = [[[2, 2, 6], [5, 2, 6], [5, 5, 6], [2, 5, 6]]];
        var window = Topography(2, [1, 1, height], [6, 1, height], [6, 6, height], [1, 6, height]);
        window.CategoryId = (int)BuiltInCategory.OST_Windows;
        window.SerializedHostId = hosted ? roof.Id : 999;
        var pane = window.Geometry!.Faces[0];
        pane.PlaneSurface = true;
        pane.NestedRenderStyleId = 3;
        var glass = new Element { Id = 3, SerializedMaterialTransparency = 0.9 };
        var floor = Topography(4, [2, 2, 4], [5, 2, 4], [5, 5, 4], [2, 5, 4]);
        floor.CategoryId = (int)BuiltInCategory.OST_Floors;
        floor.NativeRootClassName = "Floor";
        floor.NativeRootClassChain = ["Floor"];
        floor.Geometry!.Faces[0].PlaneSurface = true;
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { level, roof, window, glass, floor },
            PlanViewRanges = new Dictionary<int, (double[], int[])> { [20] = ([20, 20, 0], [10, 10, 10]) },
            ViewHiddenElements = hidden ? new Dictionary<int, HashSet<int>> { [20] = [window.Id] } : null,
        });

        var graphics = Assert.IsType<SitePrinter.SiteGraphics>(SitePrinter.CollectOpaqueGraphics(document, 20, null));
        var surface = Assert.Single(graphics.Surfaces, surface => surface.Category == BuiltInCategory.OST_Roofs);
        Assert.Equal(expected, surface.Glazing is { Count: 1 });
        Assert.Equal(2, surface.Rings.Count);
        var slab = Assert.Single(graphics.Surfaces, surface => surface.Category == BuiltInCategory.OST_Floors);
        Assert.Equal(expected, slab.Visible is { Count: 0 });

        var page = new RvtIO.Pdf.PdfDocument().AddPage(RvtIO.Pdf.PdfPageSize.A4);
        SitePrinter.DrawSurfaces(page, document, graphics, new SitePrinter.SiteContent([], [], [], [],
            0, 0, 8 * FeetToMillimetres, 8 * FeetToMillimetres), 200, 0, 0);
        Assert.Equal(expected, page.Content.Contains("W* n\n", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0.0, false, true)]
    [InlineData(0.9, false, false)]
    [InlineData(0.0, true, false)]
    public void RoofProjection_PaintsTheVisibleFrameShadowOnTheSlopingRoof(double transparency, bool hidden,
        bool expected)
    {
        var level = new Element { Id = 10, CategoryId = (int)BuiltInCategory.OST_Levels };
        level.Parameters.Add(BipHelpers.MakeBipParam((int)BuiltInParameter.LEVEL_ELEV,
            "LEVEL_ELEV", StorageType.Double, 0d));
        var roof = Topography(1, [0, 0, 4], [8, 0, 8], [8, 8, 8], [0, 8, 4]);
        roof.CategoryId = (int)BuiltInCategory.OST_Roofs;
        var slope = Assert.Single(roof.Geometry!.Faces);
        slope.Normal = [-0.5, 0, 1];
        slope.InnerVertices = [[[5, 1, 6.5], [6, 1, 7], [6, 2, 7], [5, 2, 6.5]]];
        var window = Topography(2, [5, 1, 7.5], [6, 1, 7.5], [6, 2, 7.5], [5, 2, 7.5]);
        window.CategoryId = (int)BuiltInCategory.OST_Windows;
        window.SerializedHostId = roof.Id;
        var face = Assert.Single(window.Geometry!.Faces);
        face.PlaneSurface = true;
        face.NestedRenderStyleId = 3;
        var material = new Element { Id = 3, SerializedMaterialTransparency = transparency };
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { level, roof, window, material },
            PlanViewRanges = new Dictionary<int, (double[], int[])> { [20] = ([20, 20, 0], [10, 10, 10]) },
            ViewGroundShadows = new Dictionary<int, (double, double, int, int)>
            {
                [20] = (Math.PI / 2, Math.PI / 4, 10, 60),
            },
            ViewHiddenElements = hidden ? new Dictionary<int, HashSet<int>> { [20] = [window.Id] } : null,
        });

        var graphics = Assert.IsType<SitePrinter.SiteGraphics>(SitePrinter.CollectOpaqueGraphics(document, 20, null));
        var surface = Assert.Single(graphics.Surfaces);
        if (expected)
        {
            var points = Assert.IsType<List<List<(double X, double Y)>>>(surface.Shadows).SelectMany(ring => ring);
            Assert.Equal(3 * FeetToMillimetres, points.Min(p => p.X), 4);
            Assert.Equal(5 * FeetToMillimetres, points.Max(p => p.X), 4);
        }
        else
            Assert.Null(surface.Shadows);

        var page = new RvtIO.Pdf.PdfDocument().AddPage(RvtIO.Pdf.PdfPageSize.A4);
        SitePrinter.DrawSurfaces(page, document, graphics, new SitePrinter.SiteContent([], [], [], [],
            0, 0, 8 * FeetToMillimetres, 8 * FeetToMillimetres), 200, 0, 0);
        Assert.Equal(expected, page.Content.Contains("0.4 g\n", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StairProjection_ClipsHorizontalFacesAgainstTheActualRoofPlane(bool lighting)
    {
        var level = new Element { Id = 10, CategoryId = (int)BuiltInCategory.OST_Levels };
        level.Parameters.Add(BipHelpers.MakeBipParam((int)BuiltInParameter.LEVEL_ELEV,
            "LEVEL_ELEV", StorageType.Double, 0d));
        var roof = Topography(1, [0, 0, 2], [8, 0, 6], [8, 8, 6], [0, 8, 2]);
        roof.CategoryId = (int)BuiltInCategory.OST_Roofs;
        roof.Geometry!.Faces[0].Normal = [-0.5, 0, 1];
        var overhead = Topography(3, [4.5, 2, 7], [5.5, 2, 7], [5.5, 3, 7], [4.5, 3, 7]);
        overhead.CategoryId = (int)BuiltInCategory.OST_Floors;
        var stair = new Element
        {
            Id = 2,
            CategoryId = (int)BuiltInCategory.OST_Stairs,
            NativeRootClassName = "StairsElement",
            NativeRootClassChain = ["StairsElement"],
            Geometry = new GeometryData { Faces = [Tread(0, 1), Tread(2, 4), Tread(4, 7), Tread(6, 21)] },
        };
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { level, roof, stair, overhead },
            PlanViewRanges = new Dictionary<int, (double[], int[])>
            {
                [20] = ([20, 20, 0], [10, 10, 10]),
                [21] = ([20, 20, 0], [10, 10, 10]),
            },
            ViewHiddenCategories = new Dictionary<int, HashSet<int>>
            {
                [21] = [(int)BuiltInCategory.OST_Stairs],
            },
            ViewGroundShadows = lighting ? new Dictionary<int, (double, double, int, int)>
            {
                [20] = (Math.PI / 2, Math.PI / 4, 10, 60),
                [21] = (Math.PI / 2, Math.PI / 4, 10, 60),
            } : null,
        });

        var graphics = SitePrinter.CollectOpaqueGraphics(document, 20, null)!;
        var steps = graphics.Surfaces.Where(surface => surface.Category == BuiltInCategory.OST_Stairs).ToArray();
        Assert.Equal(3, steps.Length);
        Assert.Empty(steps[0].Visible!);
        var visiblePoints = steps[1].Visible!.SelectMany(ring => ring);
        Assert.Equal(0, visiblePoints.Min(point => point.X), 6);
        Assert.InRange(visiblePoints.Max(point => point.X), 4 * FeetToMillimetres, 4 * FeetToMillimetres + 0.04);
        Assert.Equal(lighting, steps[1].Shadows is { Count: > 0 });
        Assert.Null(steps[2].Visible);
        Assert.Single(SitePrinter.CollectOpaqueGraphics(document, 21, null)!.Surfaces);

        static GeoFace Tread(double y, double z) => new()
        {
            Normal = [0, 0, 1], PlaneSurface = true,
            OuterVertices = [[0, y, z], [8, y, z], [8, y + 1, z], [0, y + 1, z]],
        };
    }

    [Theory]
    [InlineData("visible", true)]
    [InlineData("hidden owner", false)]
    [InlineData("hidden category", false)]
    [InlineData("symbol", false)]
    public void SiteRailProjection_UsesThePlacedTopRailForBothProjectionAndShadow(string state, bool expected)
    {
        var level = new Element { Id = 10, CategoryId = (int)BuiltInCategory.OST_Levels };
        level.Parameters.Add(BipHelpers.MakeBipParam((int)BuiltInParameter.LEVEL_ELEV,
            "LEVEL_ELEV", StorageType.Double, 0d));
        var roof = Topography(1, [0, 0, 5], [8, 0, 5], [8, 4, 5], [0, 4, 5]);
        roof.CategoryId = (int)BuiltInCategory.OST_Roofs;
        var owner = new Element { Id = 2, CategoryId = (int)BuiltInCategory.OST_StairsRailing,
            NativeRootClassName = "BaseRailing" };
        var rail = Topography(3, [4, 1, 7], [6, 1, 7], [6, 2, 7], [4, 2, 7]);
        rail.CategoryId = (int)BuiltInCategory.OST_RailingTopRail;
        rail.NativeRootClassName = state == "symbol" ? "ContinuousRailSymbol" : "TopRail";
        rail.SerializedOwningElementId = owner.Id;
        rail.Geometry!.Faces[0].PlaneSurface = true;
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { level, roof, owner, rail },
            PlanViewRanges = new Dictionary<int, (double[], int[])> { [20] = ([20, 20, 0], [10, 10, 10]) },
            ViewGroundShadows = new Dictionary<int, (double, double, int, int)>
            {
                [20] = (Math.PI / 2, Math.PI / 4, 10, 60),
            },
            ViewHiddenElements = state == "hidden owner"
                ? new Dictionary<int, HashSet<int>> { [20] = [owner.Id] } : null,
            ViewHiddenCategories = state == "hidden category"
                ? new Dictionary<int, HashSet<int>> { [20] = [owner.CategoryId!.Value] } : null,
        });

        var graphics = SitePrinter.CollectOpaqueGraphics(document, 20, null)!;
        var receivingRoof = Assert.Single(graphics.Surfaces, surface => surface.Category == BuiltInCategory.OST_Roofs);
        Assert.Equal(expected, graphics.Surfaces.Any(surface => surface.RailId == rail.Id));
        Assert.Equal(expected, receivingRoof.Shadows is { Count: > 0 });
        if (expected)
        {
            var points = receivingRoof.Shadows!.SelectMany(ring => ring);
            Assert.Equal(2 * FeetToMillimetres, points.Min(point => point.X), 6);
            Assert.Equal(4 * FeetToMillimetres, points.Max(point => point.X), 6);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SiteFloorProjection_ClipsAtTheTerrainPlaneAndRetainsCastShadows(bool lighting)
    {
        var level = new Element { Id = 10, CategoryId = (int)BuiltInCategory.OST_Levels };
        level.Parameters.Add(BipHelpers.MakeBipParam((int)BuiltInParameter.LEVEL_ELEV,
            "LEVEL_ELEV", StorageType.Double, 0d));
        var floor = Topography(1, [0, 0, 0], [8, 0, 0], [8, 4, 0], [0, 4, 0]);
        floor.CategoryId = (int)BuiltInCategory.OST_Floors;
        floor.NativeRootClassName = "Floor";
        floor.NativeRootClassChain = ["Floor"];
        floor.Geometry!.Faces[0].PlaneSurface = true;
        var terrain = Topography(2, [0, 0, -2], [8, 0, 2], [8, 4, 2], [0, 4, -2]);
        terrain.Geometry!.Faces[0].Normal = [-0.5, 0, 1];
        var wall = Topography(3, [5, 1, 1], [5, 3, 1], [5, 3, 3], [5, 1, 3]);
        wall.CategoryId = (int)BuiltInCategory.OST_Walls;
        wall.Geometry!.Faces[0].Normal = [-1, 0, 0];
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { level, floor, terrain, wall },
            PlanViewRanges = new Dictionary<int, (double[], int[])>
            {
                [20] = ([20, 20, 0], [10, 10, 10]),
                [21] = ([20, 20, 0], [10, 10, 10]),
            },
            ViewGroundShadows = lighting ? new Dictionary<int, (double, double, int, int)>
            {
                [20] = (Math.PI / 2, Math.PI / 4, 10, 60),
            } : null,
            ViewHiddenCategories = new Dictionary<int, HashSet<int>>
            {
                [21] = [(int)BuiltInCategory.OST_Floors],
            },
        });

        var content = SitePrinter.CollectOpaqueGraphics(document, 20, null)!;
        var slab = Assert.Single(content.Surfaces);
        Assert.Equal(BuiltInCategory.OST_Floors, slab.Category);
        var visible = slab.Visible!.SelectMany(ring => ring);
        Assert.Equal(0, visible.Min(point => point.X), 6);
        Assert.InRange(visible.Max(point => point.X), 4 * FeetToMillimetres, 4 * FeetToMillimetres + 0.04);
        Assert.Equal(lighting, slab.Shadows is { Count: > 0 });
        Assert.Null(SitePrinter.CollectOpaqueGraphics(document, 21, null));
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(2, 4)]
    public void SiteEquipment_UsesTheSavedPlanDetailAndOnlyVisiblePlacements(int detail, int expectedEndX)
    {
        const int viewId = 20;
        int equipmentCategory = (int)BuiltInCategory.OST_ElectricalEquipment;
        int siteCategory = (int)BuiltInCategory.OST_Site;
        var view = new Element { Id = viewId };
        view.Parameters.Add(BipHelpers.MakeBipParam((int)BuiltInParameter.VIEW_DETAIL_LEVEL,
            "VIEW_DETAIL_LEVEL", StorageType.Integer, detail));
        var level = new Element { Id = 10 };
        level.Parameters.Add(BipHelpers.MakeBipParam((int)BuiltInParameter.LEVEL_ELEV, "LEVEL_ELEV", StorageType.Double, 0d));
        var visible = Equipment(101, equipmentCategory, 2);
        var hidden = Equipment(102, equipmentCategory, 2);
        var aboveCut = Equipment(103, equipmentCategory, 21);
        var unplaced = Equipment(104, equipmentCategory, 2);
        unplaced.SerializedUnplacedOwnerId = 11;
        var hiddenCategory = Equipment(105, siteCategory, 2);
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { level, view, visible, hidden, aboveCut, unplaced, hiddenCategory },
            PlanViewRanges = new Dictionary<int, (double[], int[])> { [viewId] = ([20, 20, 0], [10, 10, 10]) },
            ViewHiddenCategories = new Dictionary<int, HashSet<int>> { [viewId] = [siteCategory] },
            ViewHiddenElements = new Dictionary<int, HashSet<int>> { [viewId] = [hidden.Id] },
        });

        var graphics = Assert.IsType<SitePrinter.SiteGraphics>(SitePrinter.CollectOpaqueGraphics(document, viewId, null));
        var projection = Assert.IsType<RvtIO.Internal.PlanFixtureProjection>(graphics.Equipment);
        Assert.Equal((double)expectedEndX, Assert.Single(projection.Lines).End[0]);
        Assert.Single(projection.Masks);

        static Element Equipment(int id, int category, double elevation)
        {
            RvtIO.Internal.PlanFixtureProjection Symbol(double width) => new(
                [new([1, 1, elevation], [width, 1, elevation], 57)],
                [[[1, 1, elevation], [width, 1, elevation], [width, 2, elevation], [1, 2, elevation]]]);

            return new Element
            {
                Id = id, CategoryId = category,
                BoundingBox = [1, 1, elevation, 4, 2, elevation + 1],
                PlacedPlanFixtureGraphics = new([], [], MediumProjection: Symbol(4), CoarseProjection: Symbol(3)),
            };
        }
    }

    [Fact]
    public void SiteShadows_ProjectVisibleEquipmentOntoTheSavedGroundPlane()
    {
        var level = new Element { Id = 10 };
        level.Parameters.Add(BipHelpers.MakeBipParam((int)BuiltInParameter.LEVEL_ELEV, "LEVEL_ELEV", StorageType.Double, 0d));
        var views = new[] { new Element { Id = 20 }, new Element { Id = 21 } };
        foreach (var view in views)
            view.Parameters.Add(BipHelpers.MakeBipParam((int)BuiltInParameter.VIEW_DETAIL_LEVEL,
                "VIEW_DETAIL_LEVEL", StorageType.Integer, 1));
        var equipment = Topography(11, [0, 0, 5], [4, 0, 5], [4, 4, 5], [0, 4, 5]);
        equipment.CategoryId = (int)BuiltInCategory.OST_Site;
        equipment.BoundingBox = [0, 0, 5, 4, 4, 5];
        equipment.PlacedPlanFixtureGraphics = new([], [],
            CoarseProjection: new([new([0, 0, 5], [4, 0, 5], 57)], []));
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { level, views[0], views[1], equipment },
            PlanViewRanges = views.ToDictionary(view => view.Id, _ => (new[] { 20d, 20d, 0d }, new[] { 10, 10, 10 })),
            ViewGroundShadows = views.ToDictionary(view => view.Id, _ => (Math.PI / 2, Math.PI / 4, 10, 60)),
            ViewHiddenElements = new Dictionary<int, HashSet<int>> { [views[1].Id] = [equipment.Id] },
        });

        var graphics = Assert.IsType<SitePrinter.SiteGraphics>(SitePrinter.CollectOpaqueGraphics(document, views[0].Id, null));
        var shadow = Assert.Single(graphics.GroundShadows);
        Assert.Equal(-5 * FeetToMillimetres, shadow.Min(point => point.X), 7);
        Assert.Equal(-FeetToMillimetres, shadow.Max(point => point.X), 7);
        Assert.Null(SitePrinter.CollectOpaqueGraphics(document, views[1].Id, null));
    }

    [Fact]
    public void SiteShadows_UseRaisedTerrainAsBothReceiverAndCaster()
    {
        var level = new Element { Id = 10, CategoryId = (int)BuiltInCategory.OST_Levels };
        level.Parameters.Add(BipHelpers.MakeBipParam((int)BuiltInParameter.LEVEL_ELEV,
            "LEVEL_ELEV", StorageType.Double, 0d));
        var terrain = Topography(1, [0, 0, 2], [5, 0, 2], [5, 4, 2], [0, 4, 2]);
        var wall = Topography(2, [5, 0, 2], [5, 4, 2], [5, 4, 5], [5, 0, 5]);
        wall.CategoryId = (int)BuiltInCategory.OST_Walls;
        wall.Geometry!.Faces[0].Normal = [1, 0, 0];
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements = { level, terrain, wall },
            PlanViewRanges = new Dictionary<int, (double[], int[])>
            {
                [20] = ([20, 20, 0], [10, 10, 10]),
                [21] = ([20, 20, 0], [10, 10, 10]),
            },
            ViewGroundShadows = new Dictionary<int, (double, double, int, int)>
            {
                [20] = (Math.PI / 2, Math.PI / 4, 10, 60),
                [21] = (Math.PI / 2, Math.PI / 4, 10, 60),
            },
            ViewHiddenElements = new Dictionary<int, HashSet<int>> { [21] = [2] },
        });

        var view = Assert.IsType<SitePrinter.SiteGraphics>(SitePrinter.CollectOpaqueGraphics(document, 20, null));
        Assert.True(InShadow(view, -1));
        Assert.False(InShadow(view, -3));
        Assert.False(InShadow(view, 1));
        Assert.True(InShadow(view, 3));

        var hiddenWall = Assert.IsType<SitePrinter.SiteGraphics>(SitePrinter.CollectOpaqueGraphics(document, 21, null));
        Assert.True(InShadow(hiddenWall, -1));
        Assert.False(InShadow(hiddenWall, 3));

        static bool InShadow(SitePrinter.SiteGraphics view, double x) =>
            view.GroundShadows.Sum(ring =>
            {
                int winding = 0;
                double y = 1.5 * FeetToMillimetres;
                for (int i = 0; i < ring.Count; i++)
                {
                    var a = ring[i];
                    var b = ring[(i + 1) % ring.Count];
                    if ((a.Y > y) != (b.Y > y) && a.X + (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) > x * FeetToMillimetres)
                        winding += Math.Sign(b.Y - a.Y);
                }
                return winding;
            }) > 0;
    }

    [Fact]
    public void Collect_ReturnsNullWithoutSiteEntities()
    {
        Assert.Null(SitePrinter.Collect(RvtDocument.FromInfo(new RevitFileInfo())));
    }

    [Fact]
    public void InViewBasis_RotatesThePlanOntoTheViewAxes()
    {
        // A plan whose crop basis is turned (the Site view of the sample: right = (0.7986, -0.6018)) prints turned
        // with the view. A quarter turn maps (x, y) to (y, -x), so the square's bounds swap and its corners move.
        var site = new SitePrinter.SiteContent(
            Topography: [[(0, 0), (1000, 0), (1000, 500), (0, 500)]],
            Contours: [(0, 0, 500, 0, 100)],
            Plants: [new(200, 300, 150, "Maple", "T1")],
            Entourage: [[(0, 0), (100, 0), (100, 100)]],
            MinX: 0, MinY: 0, MaxX: 1000, MaxY: 500);

        var turned = SitePrinter.InViewBasis(site, [0, 1, 0], [-1, 0, 0]);

        var polygon = Assert.Single(turned.Topography);
        Assert.Contains(polygon, point => Math.Abs(point.X - 0) < 1e-6 && Math.Abs(point.Y - 0) < 1e-6);
        Assert.Contains(polygon, point => Math.Abs(point.X - 500) < 1e-6 && Math.Abs(point.Y + 1000) < 1e-6);
        var plant = Assert.Single(turned.Plants);
        Assert.Equal(300, plant.X, 6);
        Assert.Equal(-200, plant.Y, 6);
        Assert.Equal(-1000, turned.MinY, 6);
        Assert.Equal(500, turned.MaxX, 6);
    }
}
