using System.Text.Json;
using System.Text.Json.Serialization;
using RvtIO;
using RvtIO.Compact;
using RvtIO.BuiltIns;
using RvtIO.Ifc;
using RvtIO.Model;

internal static partial class Program
{
    private static int Inspect(string rvtPath, string? externalDumpPath, string? externalDumpClassPrefix, string inspectIds)
    {
        var doc = RvtDocument.Open(rvtPath, new RvtReaderOptions { DiagnosticOutput = Console.Error, ExternalDumpPath = externalDumpPath, ExternalDumpClassPrefix = externalDumpClassPrefix });
        foreach (var idStr in inspectIds.Split(','))
        {
            int id = int.Parse(idStr.Trim());
            var elem = doc.GetElement(id);
            if (elem == null) { Console.WriteLine($"Element {id}: NOT FOUND"); continue; }
            Console.WriteLine($"\n=== Element {id} ===");
            Console.WriteLine($"  Name: {elem.Name ?? "(null)"}");
            Console.WriteLine($"  FamilyName: {elem.FamilyName ?? "(null)"}");
            Console.WriteLine($"  Category: {elem.Category ?? "(null)"} ({elem.CategoryId})");
            Console.WriteLine($"  UniqueId: {elem.UniqueId ?? "(null)"}");
            Console.WriteLine($"  TypeId: {elem.TypeId}");
            Console.WriteLine($"  FamilyTypeId: {elem.FamilyTypeId}, HostId: {elem.HostId?.ToString() ?? "(null)"}, "
                + $"BlockOnly: {elem.IsBlockOnly}, Discovered: {elem.IsDiscovered}");
            Console.WriteLine($"  SerializedHostId: {elem.SerializedHostId?.ToString() ?? "(absent)"}, "
                + $"ExtraHosts: [{string.Join(", ", elem.ExtraHostIds ?? [])}], "
                + $"ExplicitHosts: [{string.Join(", ", elem.ExplicitHostIds ?? [])}], "
                + $"SuperInstanceId: {elem.SuperInstanceId?.ToString() ?? "(null)"}");
            Console.WriteLine($"  FamilySymbolFunction: {elem.SerializedFamilySymbolFunction?.ToString() ?? "(none)"}, "
                + $"StoredParameterIds: {(elem.NativeStoredParameterIds == null ? "(none)" : string.Join(",", elem.NativeStoredParameterIds.OrderBy(x => x)))}");
            if (elem.Location != null)
                Console.WriteLine($"  Location: {elem.Location.CurveType} "
                    + $"({string.Join(", ", elem.Location.StartPoint.Select(v => v.ToString("F9")))}) -> "
                    + $"({string.Join(", ", elem.Location.EndPoint.Select(v => v.ToString("F9")))})");
            if (elem.Layers is { Count: > 0 })
                Console.WriteLine($"  Layers ({elem.Layers.Count}): "
                    + string.Join(" | ", elem.Layers.OrderBy(x => x.LayerIndex)
                        .Select(x => $"{x.LayerIndex}:{x.ThicknessMm:F3}mm:{x.MaterialId}:{x.MaterialName}")));
            Console.WriteLine($"  BBox: {(elem.BoundingBox != null ? string.Join(", ", elem.BoundingBox.Select(v => v.ToString("F3"))) : "(null)")}");
            Console.WriteLine($"  Geometry: faces={elem.Geometry?.Faces?.Count ?? 0}, solids={elem.Geometry?.BrepSolids?.Count ?? 0}, instances={elem.Geometry?.Instances?.Count ?? 0}");
            var geometryVertices = elem.Geometry?.Faces?
                .SelectMany(f => f.OuterVertices ?? Enumerable.Empty<double[]>())
                .Where(v => v.Length >= 3).ToList();
            if (geometryVertices is { Count: > 0 })
                Console.WriteLine($"  Geometry bounds: {geometryVertices.Min(v => v[0]):F3}, {geometryVertices.Min(v => v[1]):F3}, {geometryVertices.Min(v => v[2]):F3}, "
                    + $"{geometryVertices.Max(v => v[0]):F3}, {geometryVertices.Max(v => v[1]):F3}, {geometryVertices.Max(v => v[2]):F3}");
            if (elem.Geometry?.BrepSolids is { Count: > 0 } geometrySolids)
                for (int solidIndex = 0; solidIndex < geometrySolids.Count; solidIndex++)
                {
                    var solid = geometrySolids[solidIndex];
                    var vertices = solid.FaceIndices
                        .Where(i => i >= 0 && i < elem.Geometry.Faces.Count)
                        .SelectMany(i => elem.Geometry.Faces[i].OuterVertices ?? Enumerable.Empty<double[]>())
                        .Where(v => v.Length >= 3).ToList();
                    if (vertices.Count == 0) continue;
                    Console.WriteLine($"    Solid {solidIndex}: faces={solid.FaceIndices.Count}, closed={solid.IsClosed}, "
                        + $"bounds=[{vertices.Min(v => v[0]):F6},{vertices.Min(v => v[1]):F6},{vertices.Min(v => v[2]):F6},"
                        + $"{vertices.Max(v => v[0]):F6},{vertices.Max(v => v[1]):F6},{vertices.Max(v => v[2]):F6}]");
                }
            if (elem.CategoryId == -2000011
                && IfcExporter.TryGetSerializedWallWidthFeet(elem, out double inspectWallWidthFt))
            {
                Console.WriteLine($"  Wall width: {inspectWallWidthFt:F12} ft");
                if (IfcExporter.TryGetAuthoredStraightWallPlan(
                        elem, inspectWallWidthFt, out var inspectWallPlan))
                    Console.WriteLine("  Authored wall plan: " + string.Join("; ",
                        inspectWallPlan.Select(point =>
                            $"({string.Join(',', point.Select(value => value.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)))})")));
                else
                    Console.WriteLine("  Authored wall plan: unavailable");

                if (IfcExporter.TryBuildStraightWallBoundary(elem, out var inspectWallBoundary))
                {
                    Console.WriteLine($"  Rebuilt wall boundary: faces={inspectWallBoundary.Faces.Count}");
                    for (int faceIndex = 0; faceIndex < inspectWallBoundary.Faces.Count; faceIndex++)
                        if (inspectWallBoundary.Faces[faceIndex].OuterVertices is { Count: > 0 } ring)
                            Console.WriteLine($"    Boundary face {faceIndex}: " + string.Join("; ",
                                ring.Select(point =>
                                    $"({string.Join(',', point.Select(value => value.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)))})")));
                }
                else
                    Console.WriteLine("  Rebuilt wall boundary: unavailable");
            }
            if (elem.SketchLoops is { Count: > 0 } sketchLoops)
                for (int loopIndex = 0; loopIndex < sketchLoops.Count; loopIndex++)
                    Console.WriteLine($"    Sketch loop {loopIndex}: "
                        + string.Join("; ", sketchLoops[loopIndex]
                            .Select(point => $"({string.Join(',', point.Select(v => v.ToString("F6")))})")));
            if (elem.Geometry?.Faces is { Count: > 0 } inspectFaces)
                for (int faceIndex = 0; faceIndex < inspectFaces.Count; faceIndex++)
                {
                    var vertices = inspectFaces[faceIndex].OuterVertices;
                    if (vertices is not { Count: > 0 }) continue;
                    Console.WriteLine($"    Face {faceIndex}: vertices={vertices.Count}, inner={inspectFaces[faceIndex].InnerVertices?.Count ?? 0}, "
                        + $"bounds=[{vertices.Min(v => v[0]):F6},{vertices.Min(v => v[1]):F6},{vertices.Min(v => v[2]):F6},"
                        + $"{vertices.Max(v => v[0]):F6},{vertices.Max(v => v[1]):F6},{vertices.Max(v => v[2]):F6}]");
                    if (inspectFaces[faceIndex].AlternativeOuterVertices is { Count: > 0 } alternative)
                        Console.WriteLine($"      Alternative: vertices={alternative.Count}, "
                            + $"bounds=[{alternative.Min(v => v[0]):F6},{alternative.Min(v => v[1]):F6},{alternative.Min(v => v[2]):F6},"
                            + $"{alternative.Max(v => v[0]):F6},{alternative.Max(v => v[1]):F6},{alternative.Max(v => v[2]):F6}]");
                    if (inspectFaces[faceIndex].InnerVertices is { Count: > 0 } innerRings)
                        for (int innerIndex = 0; innerIndex < innerRings.Count; innerIndex++)
                            Console.WriteLine($"      Inner {innerIndex}: "
                                + string.Join("; ", innerRings[innerIndex]
                                    .Select(point => $"({string.Join(',', point.Select(v => v.ToString("F6")))})")));
                }
            if (elem.Geometry?.Instances is { Count: > 0 } geometryInstances)
                foreach (var instance in geometryInstances)
                    Console.WriteLine($"    Instance symbol={instance.SymbolId} transform=[{string.Join(", ", instance.Transform.Select(v => v.ToString("F6")))}]");
            Console.WriteLine($"  Params: {elem.Parameters.Count}");
            // RVTIO_INSPECT_ALL=1 lists every parameter, not the first twenty.
            int listedParameters = Environment.GetEnvironmentVariable("RVTIO_INSPECT_ALL") == "1" ? int.MaxValue : 20;
            foreach (var p in elem.Parameters.Take(listedParameters))
                Console.WriteLine($"    [{p.Id}] {p.DefinitionName ?? "?"} = {p.Value} ({p.StorageType})");
            if (elem.Parameters.Count > listedParameters)
                Console.WriteLine($"    ... +{elem.Parameters.Count - listedParameters} more");
            foreach (var p in elem.Parameters.Skip(listedParameters).Where(parameter =>
                parameter.DefinitionName?.Contains("WIDTH", StringComparison.OrdinalIgnoreCase) == true
                || parameter.DefinitionName?.Contains("HEIGHT", StringComparison.OrdinalIgnoreCase) == true
                || parameter.DefinitionName?.Contains("ROUGH", StringComparison.OrdinalIgnoreCase) == true))
                Console.WriteLine($"    size [{p.Id}] {p.DefinitionName ?? "?"} = {p.Value} ({p.StorageType})");
            if (elem.References != null)
                Console.WriteLine($"  References ({elem.References.Count}): {string.Join(", ", elem.References)}");
            if (elem.Dimension != null)
            {
                var dm = elem.Dimension;
                Console.WriteLine($"  Dimension: {dm.Points.Count} pts, value={dm.Value * 304.8:F1}mm, segments=[{string.Join(", ", dm.Segments.Select(s => $"{s * 304.8:F1}"))}]mm");
                foreach (var p in dm.Points.Take(6))
                    Console.WriteLine($"      pt ({p[0]:F3}, {p[1]:F3}, {p[2]:F3})");
            }
        }
        return 0;
    }

    private static int DiagDumpIds(string rvtPath, string diagDumpIds)
    {
        var ids = new HashSet<uint>(diagDumpIds.Split(',').Select(s => uint.Parse(s.Trim())));
        RvtIO.Cli.DiagDump.Run(rvtPath, ids);
        return 0;
    }

    // API demo mode
    private static int ApiDemo(string rvtPath)
    {
        var doc = RvtDocument.Open(rvtPath, new RvtReaderOptions { DiagnosticOutput = Console.Error });

        Console.WriteLine($"=== RvtDocument API Demo ===");
        Console.WriteLine($"File: {doc.Metadata.LastSavePath}");
        Console.WriteLine($"Revit: {doc.Metadata.RevitVersion}");
        Console.WriteLine($"Project: {doc.ProjectInformation.Name}");
        Console.WriteLine($"Elements: {doc.ElementCount}");
        Console.WriteLine();

        Console.WriteLine("--- Categories ---");
        foreach (var (catId, name, count) in doc.GetCategorySummary().Take(15))
            Console.WriteLine($"  {name ?? catId.ToString()}: {count} elements");
        Console.WriteLine();

        var walls = doc.Collector()
            .OfCategory(BuiltInCategory.OST_Walls)
            .WhereElementIsNotElementType()
            .ToElements();
        Console.WriteLine($"--- Walls (instances): {walls.Count} ---");
        foreach (var wall in walls.Take(5))
        {
            Console.WriteLine($"  {wall}");

            var heightParam = wall.LookupParameter("WALL_USER_HEIGHT_PARAM");
            if (heightParam != null && heightParam.HasValue)
                Console.WriteLine($"    Height: {heightParam.AsValueString()}");

            var widthParam = wall.LookupParameter("WALL_ATTR_WIDTH_PARAM");
            if (widthParam != null && widthParam.HasValue)
                Console.WriteLine($"    Width: {widthParam.AsValueString()}");

            var typeElem = doc.GetTypeElement(wall);
            if (typeElem != null)
                Console.WriteLine($"    Type: {typeElem.Name} (id={typeElem.Id})");

            if (wall.BoundingBox != null)
            {
                var bb = wall.BoundingBox;
                Console.WriteLine($"    BBox: ({bb[0]:F2}, {bb[1]:F2}, {bb[2]:F2}) -> ({bb[3]:F2}, {bb[4]:F2}, {bb[5]:F2}) ft");
            }
        }
        Console.WriteLine();

        Console.WriteLine("--- GetElement by ID ---");
        var elem = doc.GetElement(263584);
        if (elem != null)
        {
            Console.WriteLine($"  {elem}");
            Console.WriteLine($"  Category: {elem.Category}");
            Console.WriteLine($"  FamilyName: {elem.FamilyName}");
            Console.WriteLine($"  Parameters: {elem.Parameters.Count}");
            Console.WriteLine($"  HasGeometry: {elem.Geometry != null}");
            Console.WriteLine($"  Faces: {elem.Geometry?.Faces?.Count ?? 0}");

            var p = elem["WALL_USER_HEIGHT_PARAM"];
            if (p != null) Console.WriteLine($"  Height (indexer): {p.AsDouble()} ft = {p.AsValueString()}");
        }
        Console.WriteLine();

        var elemsWithGeom = doc.Collector()
            .WhereHasFaces()
            .GetElementCount();
        Console.WriteLine($"Elements with face geometry: {elemsWithGeom}");

        // Filter by category string (works when category name differs from BuiltInCategory enum)
        var proxyInstances = doc.Collector()
            .OfCategory("OST_GenericModel")
            .WhereElementIsNotElementType()
            .GetElementCount();
        Console.WriteLine($"GenericModel instances: {proxyInstances}");

        var wallTypes = doc.Collector()
            .OfCategory(BuiltInCategory.OST_Walls)
            .WhereElementIsElementType()
            .GetElementCount();
        Console.WriteLine($"Wall types: {wallTypes}");

        return 0;
    }

    // --sketch-owner-dump <path.csv> : for every sketch-owning element (slab/wall openings and
    // boundaries alike), dump owner id/category and each closed loop's member curve element ids
    // in stored order (owner.References order, not the HashSet-derived order
    // ElementBuilder.BuildSketchTopology uses internally for SketchLoops). Diagnostic only:
    // SketchOwnerDump.Run is never called from the standard export path. One full RvtFileReader.Read() pass, then one CSV write.
    // Do not pass a .json path here or to the CLI's own output argument: a .json path triggers the full JSON export.
    private static int SketchOwnerDump(string rvtPath, string sketchOwnerDumpPath)
    {
        if (sketchOwnerDumpPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("--sketch-owner-dump refuses a .json path: write .csv or .txt instead.");
            return 1;
        }
        var readerSo = new RvtFileReader(new RvtReaderOptions { DiagnosticOutput = Console.Error });
        var rvtInfoSo = readerSo.Read(rvtPath);
        RvtIO.Cli.SketchOwnerDump.Run(rvtInfoSo, sketchOwnerDumpPath);
        return 0;
    }

    // --inspect-batch <ids-file> <out.csv> : per-id identity/relationship/geometry-count dump for
    // a large batch of ids in ONE parse (--inspect reopens the file for every id). <ids-file> is plain text:
    // one id per line, blank lines and #-comments ignored, commas/whitespace also accepted as separators.
    // Diagnostic only: InspectBatchDump.Run is never called from the standard export path.
    // Do not pass a .json path for <out.csv> or for the CLI's own output argument: a .json path triggers the full JSON export.
    private static int InspectBatch(string rvtPath, string inspectBatchIdsFile, string inspectBatchOutPath)
    {
        if (inspectBatchOutPath!.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("--inspect-batch refuses a .json output path: write .csv instead.");
            return 1;
        }
        var readerIb = new RvtFileReader(new RvtReaderOptions { DiagnosticOutput = Console.Error });
        var rvtInfoIb = readerIb.Read(rvtPath);
        RvtIO.Cli.InspectBatchDump.Run(
            rvtInfoIb, inspectBatchIdsFile, inspectBatchOutPath, readerIb.LastParsingState);
        return 0;
    }

    // --list-category <categoryId> <out.csv> : lists every element whose CategoryId matches, with
    // identity fields (Name, a lenient "Number" parameter, resolved level). One RvtFileReader.Read
    // pass. Diagnostic only: ListCategoryDump.Run is never called from the standard export path.
    // Do not pass a .json path here: a .json path triggers the full JSON export.
    private static int ListCategory(string rvtPath, int? listCategoryId, string listCategoryOutPath)
    {
        if (listCategoryOutPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("--list-category refuses a .json output path: write .csv instead.");
            return 1;
        }
        var readerLc = new RvtFileReader(new RvtReaderOptions { DiagnosticOutput = Console.Error });
        var rvtInfoLc = readerLc.Read(rvtPath);
        RvtIO.Cli.ListCategoryDump.Run(rvtInfoLc, listCategoryId!.Value, listCategoryOutPath);
        return 0;
    }
}
