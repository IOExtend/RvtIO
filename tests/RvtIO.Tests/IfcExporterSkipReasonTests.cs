using RvtIO.Ifc;
using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Every element the IFC export leaves out for its bounds, as a container without written parts or for its design
/// option is listed in the skip log with a reason (docs/export-report.md), so the exported and the skipped elements add up to
/// the elements of the file.</summary>
public class IfcExporterSkipReasonTests
{
    internal static GeometryData Box(double x0, double x1, double y0, double y1, double z0, double z1)
    {
        double[][] p =
        [
            [x0, y0, z0], [x1, y0, z0], [x1, y1, z0], [x0, y1, z0],
            [x0, y0, z1], [x1, y0, z1], [x1, y1, z1], [x0, y1, z1],
        ];
        int[][] faces = [[0, 3, 2, 1], [4, 5, 6, 7], [0, 1, 5, 4], [1, 2, 6, 5], [2, 3, 7, 6], [3, 0, 4, 7]];
        return new GeometryData
        {
            BoundingBox = [x0, y0, z0, x1, y1, z1],
            Faces = faces.Select(indices => new GeoFace { OuterVertices = indices.Select(vertex => p[vertex]).ToList(), PlaneSurface = true }).ToList(),
            BrepSolids = [new GeoBrepSolid { IsClosed = true, FaceIndices = [0, 1, 2, 3, 4, 5] }],
        };
    }

    private static Element Level() => new()
    {
        Id = 10, Name = "Level 1", CategoryId = -2000240, Category = "OST_Levels", IsNativeLevel = true,
        SerializedFamilyId = -1, SerializedUnplacedOwnerId = -1, SerializedOwnerDbViewId = -1,
        Parameters =
        [
            new Parameter { Id = -1007102, Definition = new Definition { Name = "LEVEL_ELEV" }, StorageType = StorageType.Double, Value = 0.0 },
            new Parameter { Id = -1007111, StorageType = StorageType.Integer, Value = 1 },
        ],
    };

    /// <summary>A generic model on the level; <paramref name="geometry"/> null leaves it without bounds.</summary>
    private static Element Product(int id, GeometryData? geometry) => new()
    {
        Id = id, IsDiscovered = true, CategoryId = -2000151, Category = "OST_GenericModel", Name = $"Product {id}",
        References = [3],
        Parameters = [new Parameter { Id = -1012100, StorageType = StorageType.ElementId, Value = 3 }],
        BoundingBox = geometry?.BoundingBox is { } box ? (double[])box.Clone() : null,
        Geometry = geometry,
    };

    [Theory]
    [InlineData(IfcProfile.Current)]
    [InlineData(IfcProfile.Legacy)]
    public void ProductsLeftOutForTheirBounds_AreInTheSkipLogWithAReason(IfcProfile profile)
    {
        var unbounded = Box(0, 1, 0, 1, 0, 1);
        unbounded.BoundingBox = null;
        var broken = Box(0, 1, 0, 1, 0, 1);
        broken.BoundingBox = [0, 0, 0, double.NaN, 1, 1];
        var info = new RevitFileInfo();
        info.Elements.AddRange(
        [
            Level(),
            Product(100, Box(0, 4, 0, 3, 0, 2)),
            Product(101, unbounded),
            Product(102, broken),
            Product(103, Box(0, 0.02, 0, 0.02, 0, 0.02)),   // 6 mm
        ]);
        string path = Path.Combine(Path.GetTempPath(), $"rvtio-skip-reasons-{Guid.NewGuid():N}.ifc");
        var skipped = new SkipLog();
        var exporter = new IfcExporter { Profile = profile, SkipLog = skipped };
        try
        {
            exporter.Export(info, path);
            var reasons = skipped.Groups().SelectMany(group => group.Examples.Select(id => (id, group.Reason))).ToDictionary(pair => pair.id, pair => pair.Reason);
            Assert.DoesNotContain(100, reasons.Keys);
            Assert.Equal("product-without-bounds", reasons.GetValueOrDefault(101));
            Assert.Equal("invalid-bounds", reasons.GetValueOrDefault(102));
            Assert.Equal("bounds-under-one-centimetre", reasons.GetValueOrDefault(103));
            Assert.Equal(info.Elements.Count, exporter.ExportedProducts + skipped.Total);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>A roof without a body of its own that hosts a curtain panel: the roof is written after its parts.</summary>
    private static (Element Roof, Element Panel) RoofWithPanel(GeometryData? panelGeometry)
    {
        var roof = Product(200, null);
        roof.CategoryId = -2000035;
        roof.Category = "OST_Roofs";
        roof.TypeId = 500;
        var panel = Product(201, panelGeometry);
        panel.CategoryId = -2000170;
        panel.Category = "OST_CurtainWallPanels";
        panel.SerializedHostId = 200;
        return (roof, panel);
    }

    [Theory]
    [InlineData(true)]    // the panel is written: so is the roof that holds it
    [InlineData(false)]   // the panel has no bounds and is left out: the roof has no written part
    public void ContainerWrittenAfterItsParts_IsListedOnlyWhenNoPartIsWritten(bool panelWritten)
    {
        var unbounded = Box(0, 1, 0, 1, 0, 1);
        unbounded.BoundingBox = null;
        var (roof, panel) = RoofWithPanel(panelWritten ? Box(0, 4, 0, 3, 3, 3.1) : unbounded);
        var info = new RevitFileInfo();
        info.Elements.AddRange([Level(), roof, panel]);
        string path = Path.Combine(Path.GetTempPath(), $"rvtio-skip-reasons-{Guid.NewGuid():N}.ifc");
        var skipped = new SkipLog();
        var exporter = new IfcExporter { Profile = IfcProfile.Current, SkipLog = skipped };
        try
        {
            exporter.Export(info, path);
            var reasons = skipped.Groups().SelectMany(group => group.Examples.Select(id => (id, group.Reason))).ToDictionary(pair => pair.id, pair => pair.Reason);
            if (panelWritten)
            {
                Assert.DoesNotContain(200, reasons.Keys);
                Assert.DoesNotContain(201, reasons.Keys);
                Assert.Contains("=IFCROOF(", File.ReadAllText(path));
            }
            else
            {
                Assert.Equal("container-without-written-parts", reasons.GetValueOrDefault(200));
                Assert.Contains(201, reasons.Keys);
                Assert.DoesNotContain("=IFCROOF(", File.ReadAllText(path));
            }
            Assert.Equal(info.Elements.Count, exporter.ExportedProducts + skipped.Total);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ElementOfANonPrimaryDesignOption_IsListedInTheSkipLog()
    {
        var inPrimary = Product(100, Box(0, 4, 0, 3, 0, 2));
        inPrimary.NativeHeaderDesignOptionId = 875635;
        var inOther = Product(101, Box(5, 9, 0, 3, 0, 2));
        inOther.NativeHeaderDesignOptionId = 875638;
        var info = new RevitFileInfo();
        info.Elements.AddRange(
        [
            Level(), inPrimary, inOther,
            new Element { Id = 875634, NativePrimaryDesignOptionId = 875635, NativeDesignOptionName = "Option Set 1" },
            new Element { Id = 875635, NativeDesignOptionSetId = 875634, NativeDesignOptionName = "Option 1" },
            new Element { Id = 875638, NativeDesignOptionSetId = 875634, NativeDesignOptionName = "Option 2" },
        ]);
        int elements = info.Elements.Count;
        string path = Path.Combine(Path.GetTempPath(), $"rvtio-skip-reasons-{Guid.NewGuid():N}.ifc");
        var skipped = new SkipLog();
        var exporter = new IfcExporter { Profile = IfcProfile.Current, SkipLog = skipped };
        try
        {
            exporter.Export(info, path);
            var group = Assert.Single(skipped.Groups(), entry => entry.Reason == "non-primary-design-option");
            Assert.Equal([101], group.Examples);
            Assert.DoesNotContain(skipped.Groups(), entry => entry.Examples.Contains(100));
            Assert.Equal(elements, exporter.ExportedProducts + skipped.Total);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>On a real file every element is either written or listed: none leaves the export unseen.</summary>
    [SkippableFact]
    public void SampleExport_AccountsForEveryElement()
    {
        var info = new RvtFileReader().Read(TestFiles.Find("TESTFILE.rvt"));
        int elements = info.Elements.Count;
        string path = Path.Combine(Path.GetTempPath(), $"rvtio-skip-reasons-{Guid.NewGuid():N}.ifc");
        var skipped = new SkipLog();
        var exporter = new IfcExporter { Profile = IfcProfile.Current, SkipLog = skipped };
        try
        {
            exporter.Export(info, path);
            Assert.Equal(elements, exporter.ExportedProducts + skipped.Total);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>Every reason the sources pass to the skip log is in the list of docs/export-report.md, which calls the codes
    /// stable.</summary>
    [SkippableFact]
    public void EveryReasonOfTheSourcesIsInTheDocumentedList()
    {
        string? root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "docs", "export-report.md"))) root = Path.GetDirectoryName(root);
        if (root == null) throw new SkipException("The repository root with docs/export-report.md is not above the test binary.");
        string documented = File.ReadAllText(Path.Combine(root!, "docs", "export-report.md"));
        var reasons = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root!, "src", "RvtIO"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
            // the calls that add to the skip log: Skip(element, "reason") of the IFC export, SkipLog.Add and skipped.Add(id, class, "a" or "b")
            foreach (System.Text.RegularExpressions.Match call in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file),
                         @"(?<![.\w])Skip\(\s*\w+\s*,[^;]*;|\b(?:skipped|SkipLog\??)\.Add\([^;]*;"))
                foreach (System.Text.RegularExpressions.Match reason in System.Text.RegularExpressions.Regex.Matches(call.Value, "\"([a-z0-9]+(?:-[a-z0-9]+)+)\""))
                    reasons.Add(reason.Groups[1].Value);
        }
        Assert.Contains("product-without-bounds", reasons);          // the pattern still finds the three kinds of call
        Assert.Contains("record-outside-exact-element-table", reasons);
        Assert.Contains("outside-export-phase", reasons);
        Assert.Empty(reasons.Where(reason => !documented.Contains("`" + reason + "`", StringComparison.Ordinal)));
    }
}
