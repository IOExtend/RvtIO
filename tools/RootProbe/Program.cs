using System.Text.Json;
using OpenMcdf;
using RvtIO.Parsing;

// Diagnostic probe: reads selected roots from a model or from raw block captures and writes
// their exact decoded values as JSON, so a large model can be checked without a full export.
// The first argument selects the mode (the mode names are matched below; usage lines are printed on a bad command line).
// refs.json lists the element ids to look at, as produced by an external helper script.
bool modelExport = args.Length > 0 && args[0] is "model-type-names-export" or "model-computed-family-symbols-export"
    || args.Length == 5 && args[0] == "model-elements";
if (args.Length != (modelExport ? 5 : 4))
{
    Console.Error.WriteLine("RootProbe stream-tail <rvt-or-rfa> <storage/stream> <out.json>");
    Console.Error.WriteLine("RootProbe document-manager-prefix <rvt> - <out.json>: named manager prefix, not current units");
    Console.Error.WriteLine("RootProbe document-units-link|model-current-units|model-current-units-full <rvt> - <out.json>: own current units, no numeric conversion");
    Console.Error.WriteLine("RootProbe document-schemas|model-document-schemas|model-document-schemas-full <rvt> - <out.json>: own verbatim schema maps, no numeric evaluation");
    Console.Error.WriteLine("RootProbe document-si-factors|model-document-si-factors|model-document-si-factors-full <rvt> - <out.json>: SI projection, not internal conversion");
    Console.Error.WriteLine("RootProbe document-unit-cores|model-document-unit-cores|model-document-unit-cores-full <rvt> - <out.json>: numeric components, not display/public conversion");
    Console.Error.WriteLine("RootProbe document-unit-conversions|model-document-unit-conversions[-full] <rvt> - <out.json>: numeric display lookup/conversion, not formatting or IFC assignment");
    Console.Error.WriteLine("RootProbe model-ifc-numeric-units[-full] <rvt> - <out.json>: owned length/thermal unit plan, not IFC assignment or export");
    Console.Error.WriteLine("usage: RootProbe walls-ceilings|types|stairs|guids|wall-joins|panels|door-cuts|host-cuts|roots|raw-roots|class-roots|storage-entries|stream-pages|stream-exact|unit-options|own-unit-options|model-unit-options|document-root-prefix|system-type-links|host-type-names|own-element-classes|model-elements|trace-blocks|trace-tracked-blocks|trace-family-fields|trace-host-history-spans|host-vertical-regions|ceiling-type-thickness|trace-stair-compatible|trace-stair-layout|trace-stair-current|stair-run-inputs|stair-name-parameters|stair-name-definitions|stair-component-ids|railing-stair-hosts|project-info|layer-groups <rvt-or-dash> <refs.json-or-ids> <out.json>");
    return 2;
}
string mode = args[0];
if (mode == "stream-tail")
{
    File.WriteAllText(args[3], JsonSerializer.Serialize(PageTailProbe.Read(args[1], args[2])));
    return 0;
}
if (mode == "model-family-cutout-inputs")
{
    var reader = new RvtIO.RvtFileReader(new() { DiagnosticOutput = Console.Error });
    var model = reader.Read(args[1]);
    var state = reader.LastParsingState!;
    var cutoutInputs = new
    {
        instances = model.Elements.Where(element => state.ComputedFamilySymbols.ContainsKey((uint)element.Id))
            .Select(element => new
            {
                element.Id, element.ValidatedOriginalFamilySymbolId, element.SerializedHostId,
                own = state.ComputedFamilySymbols[(uint)element.Id],
                cut = element.OwnSingleFamilyCutout is { } cut
                    ? new { cut.SymbolId, cut.HostId, cut.Transform, cut.Loop } : null,
            }).ToArray(),
        symbols = state.OwnFamilyCutSymbols.ToDictionary(pair => pair.Key, pair => new
            { pair.Value.BlockCount, pair.Value.Symbol, loopCount = pair.Value.Loops?.Count }),
        families = state.OwnFamilyInPlace.ToDictionary(pair => pair.Key, pair => new
            { pair.Value.BlockCount, pair.Value.InPlace }),
        walls = state.OwnWallCutoutSteps.ToDictionary(pair => pair.Key, pair => new
            { pair.Value.BlockCount, pair.Value.Steps, pair.Value.Axis, pair.Value.CrossSection }),
    };
    using var output = File.Create(args[3]);
    JsonSerializer.Serialize(output, cutoutInputs, new JsonSerializerOptions { IncludeFields = true });
    return 0;
}
bool inMemoryClassRoots = mode == "class-roots-in-memory";
if (mode == "source-element-grep-lookup")
{
    using var input = JsonDocument.Parse(File.ReadAllText(args[2]));
    using var output = File.Create(args[3]); using var writer = new Utf8JsonWriter(output);
    writer.WriteStartObject();
    foreach (var row in input.RootElement.EnumerateObject())
    {
        var cells = row.Value.GetProperty("Cells").Deserialize<List<TfBlockDeserializer.SourceElementCell>>();
        var cache = row.Value.GetProperty("Cache").Deserialize<TfBlockDeserializer.SourceElementGRepCache>();
        var plan = TfBlockDeserializer.PlanSourceElementGRepLookup(row.Value.GetProperty("ElementId").GetInt32(),
            row.Value.GetProperty("AlternateBackend").GetBoolean(), cells, cache, out string? failure);
        writer.WritePropertyName(row.Name); JsonSerializer.Serialize(writer, new { plan, failure });
    }
    writer.WriteEndObject(); return 0;
}
if (mode == "source-refined-winding-integrals")
{
    using var input = JsonDocument.Parse(File.ReadAllText(args[2]));
    using var output = File.Create(args[3]); using var writer = new Utf8JsonWriter(output);
    writer.WriteStartObject();
    foreach (var row in input.RootElement.EnumerateObject())
    {
        var segment = row.Value.Deserialize<TfBlockDeserializer.SourceRefinedWindingInput>()
            ?? throw new InvalidDataException("Missing refined winding input");
        var refined = TfBlockDeserializer.IntegrateSourceRefinedWindingSegment(segment, 1e-6, out string? failure);
        writer.WritePropertyName(row.Name); JsonSerializer.Serialize(writer, new { refined, failure });
    }
    writer.WriteEndObject(); return 0;
}
if (mode == "source-constraint-plane")
{
    using var input = JsonDocument.Parse(File.ReadAllText(args[2]));
    using var output = File.Create(args[3]); using var writer = new Utf8JsonWriter(output);
    writer.WriteStartObject();
    foreach (var row in input.RootElement.EnumerateObject())
    {
        long[] Bits(string name) => row.Value.GetProperty(name).EnumerateArray().Select(v => v.GetInt64()).ToArray();
        var plane = TfBlockDeserializer.PrepareSourceConstraintPlane(Bits("vectorBits"), Bits("leftBits"), Bits("rightBits"), out string? failure);
        writer.WritePropertyName(row.Name); JsonSerializer.Serialize(writer, new { plane, failure });
    }
    writer.WriteEndObject(); return 0;
}
if (mode == "source-vector3")
{
    using var input = JsonDocument.Parse(File.ReadAllText(args[2]));
    using var output = File.Create(args[3]); using var writer = new Utf8JsonWriter(output);
    writer.WriteStartObject();
    foreach (var row in input.RootElement.EnumerateObject())
    {
        long[] Bits(string name) => row.Value.GetProperty(name).EnumerateArray().Select(v => v.GetInt64()).ToArray();
        var length = TfBlockDeserializer.EvaluateSourceVectorLength(Bits("vectorBits"), out string? lengthFailure);
        var unit = TfBlockDeserializer.EvaluateSourceVectorUnit(Bits("vectorBits"), out string? unitFailure);
        var distance = TfBlockDeserializer.EvaluateSourceVectorDistance(Bits("leftBits"), Bits("rightBits"), out string? distanceFailure);
        writer.WritePropertyName(row.Name); JsonSerializer.Serialize(writer, new { length, lengthFailure, unit, unitFailure, distance, distanceFailure });
    }
    writer.WriteEndObject(); return 0;
}
if (mode == "source-winding-segment-integrals")
{
    using var input = JsonDocument.Parse(File.ReadAllText(args[2]));
    using var output = File.Create(args[3]); using var writer = new Utf8JsonWriter(output);
    writer.WriteStartObject();
    foreach (var row in input.RootElement.EnumerateObject())
    {
        long[] Bits(string name) => row.Value.GetProperty(name).EnumerateArray().Select(v => v.GetInt64()).ToArray();
        var integral = TfBlockDeserializer.IntegrateSourceWindingSegment(Bits("firstBits"), Bits("lastBits"), Bits("queryBits"),
            row.Value.GetProperty("orientation").GetBoolean(), row.Value.GetProperty("reverse").GetBoolean(), 1e-6, out string? failure);
        writer.WritePropertyName(row.Name); JsonSerializer.Serialize(writer, new { integral, failure });
    }
    writer.WriteEndObject(); return 0;
}
if (mode == "polygon-crossings")
{
    using var input = JsonDocument.Parse(File.ReadAllText(args[2]));
    using var output = File.Create(args[3]);
    using var writer = new Utf8JsonWriter(output);
    writer.WriteStartObject();
    foreach (var row in input.RootElement.EnumerateObject())
    {
        var points = row.Value.GetProperty("orderedUvBits").EnumerateArray()
            .Select(p => (IReadOnlyList<long>)p.EnumerateArray().Select(v => v.GetInt64()).ToArray()).ToArray();
        var query = row.Value.GetProperty("queryUvBits").EnumerateArray().Select(v => v.GetInt64()).ToArray();
        var crossings = TfBlockDeserializer.EvaluateSourcePolygonCrossings(points, query, out string? failure);
        writer.WritePropertyName(row.Name); JsonSerializer.Serialize(writer, new { crossings, failure });
    }
    writer.WriteEndObject(); return 0;
}
if (mode is "wall-physical-table-prefix" or "wall-debug-off-cleared-table" or "wall-debug-off-active-histories" or "wall-debug-off-cut-history-chains" or "wall-debug-off-face-planarity")
{
    using var walls = JsonDocument.Parse(File.ReadAllText(args[1]));
    using var greps = JsonDocument.Parse(File.ReadAllText(args[2]));
    using var output = File.Create(args[3]);
    using var writer = new Utf8JsonWriter(output);
    writer.WriteStartObject();
    foreach (var owner in walls.RootElement.EnumerateObject())
    {
        writer.WritePropertyName(owner.Name); writer.WriteStartArray();
        foreach (var block in owner.Value.EnumerateArray())
        {
            ushort cls = block.GetProperty("classId").GetUInt16();
            var bytes = Convert.FromBase64String(block.GetProperty("bytes").GetString()!);
            var nonBRep = TfBlockDeserializer.ExtractOwnWallNonBRepTablePrefix(bytes, 0,
                block.GetProperty("size").GetUInt32(), cls, out string? failure);
            TfBlockDeserializer.OwnWallPhysicalTablePrefix? prefix = null;
            TfBlockDeserializer.OwnGElementGeometryGraph? capturedGraph = null;
            if (!int.TryParse(owner.Name, out int id) || nonBRep != null && nonBRep.OwnerId != id)
            { nonBRep = null; failure = "capture-owner"; }
            if (nonBRep != null)
            {
                if (!greps.RootElement.TryGetProperty(owner.Name, out var frames) || frames.GetArrayLength() == 0)
                    failure = "capture-grep-missing"; // not an absent-geometry conclusion
                else if (frames.GetArrayLength() != 1) failure = "capture-grep-ambiguous";
                else
                {
                    var frame = frames[0]; var data = Convert.FromBase64String(frame.GetProperty("bytes").GetString()!);
                    var graph = TfBlockDeserializer.ExtractOwnGElementGeometryGraph(data, 0,
                        frame.GetProperty("size").GetUInt32(), frame.GetProperty("classId").GetUInt16(), out failure);
                    if (graph != null && graph.OwnerId != id) { graph = null; failure = "capture-grep-owner"; }
                    if (graph != null) { capturedGraph = graph; prefix = TfBlockDeserializer.ApplyOwnWallPhysicalTablePrefix(cls, nonBRep, graph, out failure); }
                }
            }
            if (mode is "wall-debug-off-cleared-table" or "wall-debug-off-active-histories" or "wall-debug-off-cut-history-chains" or "wall-debug-off-face-planarity")
            {
                TfBlockDeserializer.OwnWallDebugOffClearIds? clearIds = null;
                TfBlockDeserializer.OwnWallDebugOffClearedTable? table = null;
                TfBlockDeserializer.OwnWallDebugOffActiveFaceHistories? active = null;
                TfBlockDeserializer.OwnWallDebugOffCutFaceHistoryChains? chains = null;
                TfBlockDeserializer.OwnWallDebugOffFacePlanarity? planarity = null;
                if (prefix != null)
                {
                    var input = TfBlockDeserializer.ExtractOwnWallCompleteInputs(bytes, 0,
                        block.GetProperty("size").GetUInt32(), cls, out failure);
                    var references = input == null ? null : TfBlockDeserializer.ExtractOwnWallReferenceGeometryInputs(bytes, 0,
                        block.GetProperty("size").GetUInt32(), cls, out failure);
                    if (input != null && references != null) clearIds = TfBlockDeserializer.SelectOwnWallDebugOffClearIds(
                        input, references, prefix.GeometryCount, out failure);
                    if (clearIds != null) table = TfBlockDeserializer.ApplyOwnWallDebugOffClearing(prefix, clearIds, out failure);
                    if (mode is "wall-debug-off-active-histories" or "wall-debug-off-cut-history-chains" or "wall-debug-off-face-planarity" && input != null && table != null)
                        active = TfBlockDeserializer.SelectOwnWallDebugOffActiveFaceHistories(input, table, out failure);
                    if (mode == "wall-debug-off-cut-history-chains" && active != null)
                        chains = TfBlockDeserializer.ResolveOwnWallDebugOffCutFaceHistoryChains(active, out failure);
                    if (mode == "wall-debug-off-face-planarity" && input != null && references != null && capturedGraph != null && active != null)
                        planarity = TfBlockDeserializer.SelectOwnWallDebugOffFacePlanarity(input, references, capturedGraph, active, out failure);
                }
                if (mode == "wall-debug-off-face-planarity") JsonSerializer.Serialize(writer, new { classId = cls, planarity, failure });
                else if (mode == "wall-debug-off-cut-history-chains") JsonSerializer.Serialize(writer, new { classId = cls, chains, failure });
                else if (mode == "wall-debug-off-active-histories") JsonSerializer.Serialize(writer, new { classId = cls, active, failure });
                else JsonSerializer.Serialize(writer, new { classId = cls, clearIds, table, failure });
            }
            else JsonSerializer.Serialize(writer, new { classId = cls, prefix, failure });
        }
        writer.WriteEndArray(); writer.Flush();
    }
    writer.WriteEndObject(); writer.Flush();
    return 0;
}
if (inMemoryClassRoots) mode = "class-roots";
if (modelExport)
{
    if (!args[4].EndsWith(".ifc", StringComparison.OrdinalIgnoreCase) || File.Exists(args[4])
        || Path.GetFullPath(args[4]).Equals(Path.GetFullPath(args[3]), StringComparison.OrdinalIgnoreCase))
    { Console.Error.WriteLine("Fresh distinct IFC output path required."); return 2; }
    // The optional IFC output runs the default writer on this very model.
    if (mode != "model-elements") mode = "model-type-names";
}
JsonElement refs = mode is "roots" or "raw-roots" or "class-roots" or "elem-table" or "storage-entries" or "stream-pages" or "stream-exact" or "model-elements" or "model-type-names" or "model-unit-options" or "model-current-units" or "model-current-units-full" or "model-document-schemas" or "model-document-schemas-full" or "document-schemas" or "document-si-factors" or "model-document-si-factors" or "model-document-si-factors-full" or "document-unit-cores" or "model-document-unit-cores" or "model-document-unit-cores-full" or "document-unit-conversions" or "model-document-unit-conversions" or "model-document-unit-conversions-full" or "model-ifc-numeric-units" or "model-ifc-numeric-units-full" or "document-root-prefix" or "document-manager-prefix" or "document-units-link" or "project-info" ? default : JsonDocument.Parse(File.ReadAllText(args[2])).RootElement;
if (mode is "gelement-planar-source-regions" or "gelement-geometry-graph" or "gelement-archive-references" or "gelement-loop-topology" or "gelement-loop-nesting-inputs" or "gelement-loop-envelope-first-pass" or "gelement-loop-two-point-projections" or "gelement-point-within-first-sample" or "gelement-chain-first-sample" or "gelement-plane-winding-fallback" or "gelement-runtime-winding" or "gelement-runtime-winding-eps0" or "gelement-eps-independent-samples" or "wall-local-geometry" or "wall-reference-geometry" or "wall-archive-owners" or "wall-complete-inputs" or "wall-non-brep-table")
{
    // Keep one complete graph live, not the multi-million-node model census.
    using var output = File.Create(args[3]);
    using var writer = new Utf8JsonWriter(output);
    writer.WriteStartObject();
    foreach (var owner in refs.EnumerateObject())
    {
        writer.WritePropertyName(owner.Name); writer.WriteStartArray();
        foreach (var block in owner.Value.EnumerateArray())
        {
            ushort cls = block.GetProperty("classId").GetUInt16();
            byte[] bytes = Convert.FromBase64String(block.GetProperty("bytes").GetString()!);
            if (mode == "wall-non-brep-table")
            {
                var prefix = TfBlockDeserializer.ExtractOwnWallNonBRepTablePrefix(bytes, 0,
                    block.GetProperty("size").GetUInt32(), cls, out string? why);
                if (!int.TryParse(owner.Name, out int capturedId) || prefix != null && prefix.OwnerId != capturedId)
                { prefix = null; why = "capture-owner"; }
                JsonSerializer.Serialize(writer, new { classId = cls, prefix, failure = why });
                continue;
            }
            if (mode == "wall-table-addressing")
            {
                var input = TfBlockDeserializer.ExtractOwnWallCompleteInputs(bytes, 0,
                    block.GetProperty("size").GetUInt32(), cls, out string? why);
                var table = input == null ? null : TfBlockDeserializer.SelectOwnWallGeometryTableAddressing(input, out why);
                if (!int.TryParse(owner.Name, out int capturedId) || table != null && table.OwnerId != capturedId)
                { table = null; why = "capture-owner"; }
                JsonSerializer.Serialize(writer, new { classId = cls, table, failure = why });
                continue;
            }
            if (mode == "wall-complete-inputs")
            {
                var inputs = TfBlockDeserializer.ExtractOwnWallCompleteInputs(bytes, 0,
                    block.GetProperty("size").GetUInt32(), cls, out string? why);
                if (!int.TryParse(owner.Name, out int capturedId)
                    || inputs != null && inputs.Histories.Histories.List.OwnerId != capturedId)
                { inputs = null; why = "capture-owner"; }
                JsonSerializer.Serialize(writer, new { classId = cls, inputs, failure = why });
                continue;
            }
            if (mode == "wall-archive-owners")
            {
                var links = TfBlockDeserializer.ExtractOwnWallArchiveOwnerLinks(bytes, 0,
                    block.GetProperty("size").GetUInt32(), cls, out string? why);
                if (!int.TryParse(owner.Name, out int capturedId) || links != null && links.OwnerId != capturedId)
                { links = null; why = "capture-owner"; }
                JsonSerializer.Serialize(writer, new { classId = cls, links, failure = why });
                continue;
            }
            if (mode == "wall-reference-geometry")
            {
                var inputs = TfBlockDeserializer.ExtractOwnWallReferenceGeometryInputs(bytes, 0,
                    block.GetProperty("size").GetUInt32(), cls, out string? why);
                if (!int.TryParse(owner.Name, out int capturedId) || inputs != null && inputs.OwnerId != capturedId)
                { inputs = null; why = "capture-owner"; }
                JsonSerializer.Serialize(writer, new { classId = cls, inputs, failure = why });
                continue;
            }
            var graph = TfBlockDeserializer.ExtractOwnGElementGeometryGraph(bytes, 0,
                block.GetProperty("size").GetUInt32(), cls, out string? failure);
            if (!int.TryParse(owner.Name, out int id) || graph != null && graph.OwnerId != id)
            { graph = null; failure = "capture-owner"; }
            if (mode == "gelement-planar-source-regions")
            {
                var regions = graph == null ? null : GRepParserV2.SelectOwnPlanarFaceRegions(graph, out failure);
                string? localFailure = null;
                var local = graph == null ? null : TfBlockDeserializer.SelectOwnWallLocalGeometryInputs(graph, out localFailure);
                object? firstDirectLocalGeometry = null;
                if (regions != null && local?.Geometries is { Count: 1 } geometries
                    && local.FirstDirectGeometryQueueIndex == geometries[0].QueueIndex)
                {
                    var faces = geometries[0].Faces.SelectMany(face => regions.GetValueOrDefault(face.FaceQueueIndex) ?? []).ToList();
                    var faceKeys = geometries[0].Faces.SelectMany(face => Enumerable.Repeat(
                        (uint)face.FaceQueueIndex + 1, regions.GetValueOrDefault(face.FaceQueueIndex)?.Count ?? 0)).ToArray();
                    var bodyKeys = geometries[0].Faces.Select(face => face.FaceQueueIndex).ToHashSet();
                    var topology = TfBlockDeserializer.SelectOwnGElementLoopTopology(graph!, out _);
                    var visits = new Dictionary<int, List<(int Face, int? Side)>>();
                    foreach (var face in topology?.Faces.Where(face => bodyKeys.Contains(face.FaceQueueIndex)) ?? [])
                        foreach (var loop in face.Loops)
                            foreach (var edge in loop.Edges)
                            {
                                if (!visits.TryGetValue(edge.EdgeQueueIndex, out var uses))
                                    visits.Add(edge.EdgeQueueIndex, uses = []);
                                uses.Add((face.FaceQueueIndex, edge.FaceSide));
                            }
                    bool paired = visits.Count > 0 && visits.Values.All(uses => uses.Count == 2
                        && (uses[0].Side == 0 && uses[1].Side == 1 || uses[0].Side == 1 && uses[1].Side == 0));
                    // Queue tokens are frame-local identities; repeated raw handles must not alias Faces.
                    var solids = paired ? GRepParserV2.BuildTopologicalSolids(faces, faceKeys,
                        visits.Values.Select(uses => ((uint)uses[0].Face + 1, (uint)uses[1].Face + 1))) : [];
                    bool topologyCoverage = solids.SelectMany(solid => solid.FaceIndices).Distinct().Count() == faces.Count
                        && solids.Sum(solid => solid.FaceIndices.Count) == faces.Count;
                    var withTopology = solids.Count == 0 ? null : ElementBuilder.BuildGeometryData(
                        new RvtIO.Internal.GRepData { Faces = faces, Solids = solids });
                    bool closedWithTopology = withTopology != null && RvtIO.Ifc.IfcExporter.TryOrientExactFacetedShell(
                        withTopology.Faces, Enumerable.Range(0, withTopology.Faces.Count).ToArray(), out _,
                        triangulateNonPlanarFaces: false);
                    var legacyCleanup = ElementBuilder.BuildGeometryData(new RvtIO.Internal.GRepData { Faces = faces });
                    // Unknown source closure is a surface control, not permission to remove interior planes.
                    var materialized = ElementBuilder.BuildGeometryData(new RvtIO.Internal.GRepData
                        { Faces = faces, IsOpenSurface = true });
                    bool closed = materialized != null && RvtIO.Ifc.IfcExporter.TryOrientExactFacetedShell(
                        materialized.Faces, Enumerable.Range(0, materialized.Faces.Count).ToArray(), out _,
                        triangulateNonPlanarFaces: false);
                    firstDirectLocalGeometry = new { queueIndex = geometries[0].QueueIndex,
                        sourceRegionCount = faces.Count, materializedFaceCount = materialized?.Faces.Count,
                        withoutSourceTopologyFaceCount = legacyCleanup?.Faces.Count,
                        twoSidedIncidence = paired, topologicalSolidCount = solids.Count, topologyCoverage,
                        topologicallyClosed = solids.Count > 0 && solids.All(solid => solid.IsClosed),
                        materializedTopologyFaceCount = withTopology?.Faces.Count,
                        writerLatticeClosedWithTopology = closedWithTopology,
                        writerLatticeClosed = closed };
                }
                string? primaryFailure = null;
                var primary = graph == null ? null : GRepParserV2.SelectOwnFirstDirectPlanarBody(graph, out primaryFailure);
                JsonSerializer.Serialize(writer, new { classId = cls, regions, failure,
                    primaryBody = primary == null ? null : new { faceCount = primary.Faces.Count,
                        primary.BoundingBox, primary.Solids, primary.PreserveSourceFaceIdentity }, primaryFailure,
                    firstDirectLocalGeometry, localFailure,
                    currentGeometryAuthorityClosed = false, defaultReaderIntegration = true,
                    defaultReaderScope = "unique closed planar candidate; IFCWALL without emitted void operands" });
            }
            else if (mode == "gelement-eps-independent-samples")
            {
                var independent = graph == null ? null : TfBlockDeserializer.SelectOwnGElementEpsIndependentSamples(graph, out failure);
                JsonSerializer.Serialize(writer, new { classId = cls, independent, failure });
            }
            else if (mode is "gelement-runtime-winding" or "gelement-runtime-winding-eps0")
            {
                var runtime = graph == null ? null : TfBlockDeserializer.SelectOwnGElementRuntimeWinding(graph,
                    mode == "gelement-runtime-winding-eps0" ? 0 : 1, out failure);
                JsonSerializer.Serialize(writer, new { classId = cls, runtime, failure });
            }
            else if (mode == "gelement-plane-winding-fallback")
            {
                var winding = graph == null ? null : TfBlockDeserializer.SelectOwnGElementPlaneWindingFallback(graph, 1, out failure);
                JsonSerializer.Serialize(writer, new { classId = cls, winding, failure });
            }
            else if (mode == "gelement-chain-first-sample")
            {
                var chains = graph == null ? null : TfBlockDeserializer.SelectOwnGElementChainFirstSample(graph, 1, out failure);
                JsonSerializer.Serialize(writer, new { classId = cls, chains, failure });
            }
            else if (mode == "gelement-point-within-first-sample")
            {
                var containment = graph == null ? null : TfBlockDeserializer.SelectOwnGElementPointWithinFirstSample(graph, 1, out failure);
                JsonSerializer.Serialize(writer, new { classId = cls, containment, failure });
            }
            else if (mode == "gelement-loop-two-point-projections")
            {
                var projections = graph == null ? null : TfBlockDeserializer.SelectOwnGElementLoopTwoPointProjections(graph, 1, out failure);
                JsonSerializer.Serialize(writer, new { classId = cls, projections, failure });
            }
            else if (mode == "gelement-loop-envelope-first-pass")
            {
                var envelopes = graph == null ? null : TfBlockDeserializer.SelectOwnGElementLoopEnvelopeFirstPass(graph, 1, out failure);
                JsonSerializer.Serialize(writer, new { classId = cls, envelopes, failure });
            }
            else if (mode == "gelement-loop-nesting-inputs")
            {
                var nesting = graph == null ? null : TfBlockDeserializer.SelectOwnGElementLoopNestingInputs(graph, 1, out failure);
                JsonSerializer.Serialize(writer, new { classId = cls, nesting, failure });
            }
            else if (mode == "gelement-loop-topology")
            {
                var topology = graph == null ? null : TfBlockDeserializer.SelectOwnGElementLoopTopology(graph, out failure);
                JsonSerializer.Serialize(writer, new { classId = cls, topology, failure });
            }
            else if (mode == "gelement-archive-references")
            {
                var references = graph == null ? null : TfBlockDeserializer.SelectOwnGElementFreshArchiveReferences(graph, out failure);
                JsonSerializer.Serialize(writer, new { classId = cls, references, failure });
            }
            else if (mode == "gelement-geometry-graph") JsonSerializer.Serialize(writer, new { classId = cls, graph, failure });
            else
            {
                var inputs = graph == null ? null : TfBlockDeserializer.SelectOwnWallLocalGeometryInputs(graph, out failure);
                JsonSerializer.Serialize(writer, new { classId = cls, inputs, failure });
            }
        }
        writer.WriteEndArray(); writer.Flush();
    }
    writer.WriteEndObject(); writer.Flush();
    return 0;
}

object result = mode switch
{
    "walls-ceilings" => WallsAndCeilings(ReadChunks(args[1]), refs),
    "types" => Types(ReadChunks(args[1]), refs, LoadNames(args[1])),
    "stairs" => Stairs(ReadChunks(args[1]), refs, LoadNames(args[1])),
    "guids" => Guids(args[1], refs),
    "wall-joins" => WallJoins(ReadChunks(args[1]), refs),
    "panels" => Panels(ReadChunks(args[1]), refs),
    "door-cuts" => DoorCuts(ReadChunks(args[1]), refs),
    "host-cuts" => HostCuts(ReadChunks(args[1]), refs),
    "layer-groups" => LayerGroups(args[1], refs),
    "project-info" => RvtIO.Parsing.ProjectInfoReader.Read(RootStorage.OpenRead(args[1])),
    "roots" => Roots(ReadChunks(args[1]), args[2].Split(',').Select(uint.Parse).ToHashSet()),
    "raw-roots" => RawRoots(ReadChunks(args[1]), args[2].Split(',').Select(uint.Parse).ToHashSet()),
    "class-roots" => inMemoryClassRoots
        ? ClassRootsFromChunks(ReadChunks(args[1]), args[2].Split(',').Select(ushort.Parse).ToHashSet(), true)
        : ClassRoots(args[1], args[2].Split(',').Select(ushort.Parse).ToHashSet()),
    "storage-entries" => StorageEntries(args[1]),
    "stream-pages" => StreamPages(args[1], args[2]),
    "stream-exact" => StreamPages(args[1], args[2], exact: true),
    "elem-table" => ReadElementTable(args[1]),
    "document-root-prefix" => ReadDocumentRootPrefix(args[1]),
    "document-manager-prefix" => ReadDocumentManagerPrefix(args[1]),
    "document-units-link" => ReadDocumentUnitsLink(args[1]),
    "model-current-units" => ReadCurrentDocumentUnits(args[1], false),
    "model-current-units-full" => ReadCurrentDocumentUnits(args[1], true),
    "document-schemas" => ReadDocumentSchemas(args[1]),
    "model-document-schemas" => ReadCurrentDocumentUnits(args[1], false, true),
    "model-document-schemas-full" => ReadCurrentDocumentUnits(args[1], true, true),
    "document-si-factors" => ReadDocumentSiFactors(args[1]),
    "model-document-si-factors" => ReadCurrentDocumentUnits(args[1], false, siFactors: true),
    "model-document-si-factors-full" => ReadCurrentDocumentUnits(args[1], true, siFactors: true),
    "document-unit-cores" => ReadDocumentSiFactors(args[1], cores: true),
    "model-document-unit-cores" => ReadCurrentDocumentUnits(args[1], false, cores: true),
    "model-document-unit-cores-full" => ReadCurrentDocumentUnits(args[1], true, cores: true),
    "document-unit-conversions" => ReadDocumentSiFactors(args[1], conversions: true),
    "model-document-unit-conversions" => ReadCurrentDocumentUnits(args[1], false, conversions: true),
    "model-document-unit-conversions-full" => ReadCurrentDocumentUnits(args[1], true, conversions: true),
    "model-ifc-numeric-units" => ReadCurrentDocumentUnits(args[1], false, ifcPlan: true),
    "model-ifc-numeric-units-full" => ReadCurrentDocumentUnits(args[1], true, ifcPlan: true),
    "unit-options" => refs.EnumerateObject().ToDictionary(owner => owner.Name,
        owner => owner.Value.EnumerateArray().Select(block => UnitOptionsProbe.ReadOwnBlock(
            Convert.FromBase64String(block.GetProperty("bytes").GetString()!),
            block.GetProperty("size").GetUInt32(), block.GetProperty("classId").GetUInt16())).ToArray()),
    "own-unit-options" => refs.EnumerateObject().ToDictionary(owner => owner.Name, owner =>
    {
        if (!uint.TryParse(owner.Name, out uint id) || id == 0 || id > int.MaxValue)
            throw new InvalidDataException("Invalid own UnitsElem owner");
        var state = new RvtIO.Internal.ParsingState();
        foreach (var block in owner.Value.EnumerateArray())
            RvtIO.RvtFileReader.RecordOwnUnitOptions(state,
                Convert.FromBase64String(block.GetProperty("bytes").GetString()!), 0,
                block.GetProperty("size").GetUInt32(), block.GetProperty("classId").GetUInt16(), id);
        var validation = state.OwnUnitOptions.GetValueOrDefault(id);
        return new { validation.BlockCount, validation.Options };
    }),
    "model-unit-options" => new RvtIO.RvtFileReader(new()
        { DiagnosticOutput = Console.Error }).ReadWithState(args[1]).State.OwnUnitOptions
        .ToDictionary(item => item.Key.ToString(), item => new { item.Value.BlockCount, item.Value.Options }),
    "own-element-classes" => refs.EnumerateObject().ToDictionary(owner => owner.Name,
        owner => owner.Value.EnumerateArray().Select(block =>
        {
            ushort classId = block.GetProperty("classId").GetUInt16();
            bool exact = TfBlockDeserializer.TryExtractOwnElementClass(
                Convert.FromBase64String(block.GetProperty("bytes").GetString()!), 0,
                block.GetProperty("size").GetUInt32(), classId, out int id);
            return new { classId, exactElementClass = exact, ownerId = id };
        }).ToArray()),
    "system-type-links" => refs.EnumerateObject().ToDictionary(owner => owner.Name,
        owner => owner.Value.EnumerateArray().Select(block =>
        {
            byte[] bytes = Convert.FromBase64String(block.GetProperty("bytes").GetString()!);
            ushort cls = block.GetProperty("classId").GetUInt16();
            uint size = block.GetProperty("size").GetUInt32();
            bool owned = bytes.Length >= 4 && uint.TryParse(owner.Name, out uint id)
                && BitConverter.ToUInt32(bytes, 0) == id;
            int? typeId = owned ? TfBlockDeserializer.ExtractSystemOccurrenceTypeId(bytes, 0, size, cls) : null;
            int? discriminator = null;
            bool typeRoot = owned && TfBlockDeserializer.TryExtractSystemTypeRoot(bytes, 0, size, cls, out discriminator);
            return new { classId = cls, typeId, typeRoot, discriminator };
        }).ToArray()),
    "host-type-names" => refs.EnumerateObject().ToDictionary(owner => owner.Name,
        owner => owner.Value.EnumerateArray().Select(block =>
        {
            byte[] bytes = Convert.FromBase64String(block.GetProperty("bytes").GetString()!);
            ushort cls = block.GetProperty("classId").GetUInt16();
            uint size = block.GetProperty("size").GetUInt32();
            bool owned = bytes.Length >= 4 && uint.TryParse(owner.Name, out uint id)
                && BitConverter.ToUInt32(bytes, 0) == id;
            string? name = owned ? TfBlockDeserializer.ExtractHostTypeOwnName(bytes, 0, size, cls) : null;
            var state = new RvtIO.Internal.ParsingState();
            if (uint.TryParse(owner.Name, out uint ownerId))
                RvtIO.RvtFileReader.RecordHostTypeCompoundStructure(state, bytes, 0, size, cls, ownerId);
            var validation = state.HostTypeCompoundStructures.GetValueOrDefault(ownerId);
            return new { classId = cls, ownName = name, recordedName = validation.TypeName,
                validation.BlockCount, hostClass = validation.ClassId, layersDecoded = validation.Layers != null };
        }).ToArray()),
    "wall-location-line" => refs.EnumerateObject().ToDictionary(owner => owner.Name,
        owner => owner.Value.EnumerateArray().Select(block =>
        {
            byte[] bytes = Convert.FromBase64String(block.GetProperty("bytes").GetString()!);
            var line = TfBlockDeserializer.ExtractOwnWallLocationLine(bytes, 0,
                block.GetProperty("size").GetUInt32(), block.GetProperty("classId").GetUInt16(), out string? failure);
            if (!uint.TryParse(owner.Name, out uint id) || bytes.Length < 4 || BitConverter.ToUInt32(bytes) != id)
            { line = null; failure = "capture-owner"; }
            return new { line, failure };
        }).ToArray()),
    "model-elements" => ModelElements(args[1], args[2].Split(',').Select(int.Parse).ToHashSet(),
        args.Length > 4 ? args[4] : null),
    "model-type-names" => args[0] == "model-computed-family-symbols-export"
        ? ModelComputedFamilySymbols(args[1], args[4])
        : ModelTypeNames(args[1], args.Length == 5 ? args[4] : null),
    "trace-blocks" => TraceBlocks(refs),
    "computed-family-symbols" => ComputedFamilySymbols(refs),
    "wall-geometry-phases" => refs.EnumerateObject().ToDictionary(owner => owner.Name,
        owner => owner.Value.EnumerateArray().Select(block =>
        {
            ushort cls = block.GetProperty("classId").GetUInt16();
            byte[] bytes = Convert.FromBase64String(block.GetProperty("bytes").GetString()!);
            var phases = TfBlockDeserializer.ExtractOwnWallGeometryPhases(bytes, 0,
                block.GetProperty("size").GetUInt32(), cls, out string? failure);
            if (!int.TryParse(owner.Name, out int id) || phases != null && phases.OwnerId != id)
            { phases = null; failure = "capture-owner"; }
            return new { classId = cls, phases, failure };
        }).ToArray()),
    "wall-cutout-steps" => refs.EnumerateObject().ToDictionary(owner => owner.Name,
        owner => owner.Value.EnumerateArray().Select(block =>
        {
            ushort cls = block.GetProperty("classId").GetUInt16();
            byte[] bytes = Convert.FromBase64String(block.GetProperty("bytes").GetString()!);
            var steps = TfBlockDeserializer.ExtractOwnWallCutoutSteps(bytes, 0,
                block.GetProperty("size").GetUInt32(), cls, out string? failure);
            if (!int.TryParse(owner.Name, out int id) || bytes.Length < 4 || BitConverter.ToInt32(bytes) != id)
            { steps = null; failure = "capture-owner"; }
            return new { classId = cls, steps, failure };
        }).ToArray()),
    "wall-geometry-histories" => refs.EnumerateObject().ToDictionary(owner => owner.Name,
        owner => owner.Value.EnumerateArray().Select(block =>
        {
            ushort cls = block.GetProperty("classId").GetUInt16();
            byte[] bytes = Convert.FromBase64String(block.GetProperty("bytes").GetString()!);
            var histories = TfBlockDeserializer.ExtractOwnWallGeometryHistories(bytes, 0,
                block.GetProperty("size").GetUInt32(), cls, out string? failure);
            if (!int.TryParse(owner.Name, out int id) || histories != null && histories.List.OwnerId != id)
            { histories = null; failure = "capture-owner"; }
            return new { classId = cls, histories, failure };
        }).ToArray()),
    "gelement-geometry-prefix" => refs.EnumerateObject().ToDictionary(owner => owner.Name,
        owner => owner.Value.EnumerateArray().Select(block =>
        {
            ushort cls = block.GetProperty("classId").GetUInt16();
            byte[] bytes = Convert.FromBase64String(block.GetProperty("bytes").GetString()!);
            var prefix = TfBlockDeserializer.ExtractOwnGElementGeometryPrefix(bytes, 0,
                block.GetProperty("size").GetUInt32(), cls, out string? failure);
            if (!int.TryParse(owner.Name, out int id) || prefix != null && prefix.OwnerId != id)
            { prefix = null; failure = "capture-owner"; }
            return new { classId = cls, prefix, failure };
        }).ToArray()),
    "wall-six-phase-histories" => refs.EnumerateObject().ToDictionary(owner => owner.Name,
        owner => owner.Value.EnumerateArray().Select(block =>
        {
            ushort cls = block.GetProperty("classId").GetUInt16();
            byte[] bytes = Convert.FromBase64String(block.GetProperty("bytes").GetString()!);
            var histories = TfBlockDeserializer.ExtractOwnWallSixPhaseGeometryHistories(bytes, 0,
                block.GetProperty("size").GetUInt32(), cls, out string? failure);
            if (!int.TryParse(owner.Name, out int id) || histories != null && histories.Histories.List.OwnerId != id)
            { histories = null; failure = "capture-owner"; }
            return new { classId = cls, histories, failure };
        }).ToArray()),
    "trace-tracked-blocks" => TraceBlocks(refs, trackedHistories: true),
    "trace-family-fields" => TraceBlocks(refs, familyFields: true),
    "family-cut-loops" => refs.EnumerateObject().ToDictionary(owner => owner.Name,
        owner => owner.Value.EnumerateArray().Select(block =>
        {
            byte[] bytes = Convert.FromBase64String(block.GetProperty("bytes").GetString()!);
            ushort cls = block.GetProperty("classId").GetUInt16();
            uint size = block.GetProperty("size").GetUInt32();
            bool own = int.TryParse(owner.Name, out int id)
                && DeferredFieldProbe.HasOwnFamilyFrame(bytes, size, cls, id);
            int? rootCount = null;
            var loops = own ? TfBlockDeserializer.ExtractOwnFamilySymbolCutLoops(
                bytes, 0, size, cls, out rootCount) : null;
            return new { classId = cls, own, rootCount, loops };
        }).ToArray()),
    "trace-family-function-fields" => TraceBlocks(refs, familyFields: true, familyFunctionFields: true),
    "family-function-inputs" => refs.EnumerateObject().ToDictionary(owner => owner.Name,
        owner => owner.Value.EnumerateArray().Select(block =>
        {
            byte[] bytes = Convert.FromBase64String(block.GetProperty("bytes").GetString()!);
            ushort cls = block.GetProperty("classId").GetUInt16();
            var input = TfBlockDeserializer.ExtractFamilySymbolFunctionInput(bytes, 0,
                block.GetProperty("size").GetUInt32(), cls);
            if (!int.TryParse(owner.Name, out int id) || input?.OwnerId != id) input = null;
            return new { classId = cls, input };
        }).ToArray()),
    "trace-host-history-spans" => TraceBlocks(refs, trackedHistories: true, coalescedHostHistories: true),
    "host-vertical-regions" => HostVerticalRegions(refs),
    "ceiling-type-thickness" => refs.EnumerateObject().ToDictionary(owner => owner.Name,
        owner => owner.Value.EnumerateArray().Select(block =>
        {
            ushort cls = block.GetProperty("classId").GetUInt16();
            byte[] bytes = Convert.FromBase64String(block.GetProperty("bytes").GetString()!);
            double value = 0;
            bool validated = uint.TryParse(owner.Name, out uint id) && bytes.Length >= 4
                && BitConverter.ToUInt32(bytes, 0) == id
                && TfBlockDeserializer.TryExtractCompoundCeilingTypeThicknessFeet(bytes, 0,
                    block.GetProperty("size").GetUInt32(), cls, out value);
            double? thicknessFeet = validated ? value : null;
            return new { classId = cls, validated, thicknessFeet };
        }).ToArray()),
    "trace-stair-compatible" => TraceBlocks(refs, stairCompatibleCurves: true),
    "trace-stair-layout" => TraceBlocks(refs, stairLayout: true),
    "trace-stair-current" => TraceBlocks(refs, stairLayout: true, stairCountedHistories: true),
    "trace-stair-parent-current" => TraceBlocks(refs, stairLayout: true, stairCountedHistories: true, stairParent: true),
    "stair-run-inputs" => refs.EnumerateObject().ToDictionary(owner => owner.Name,
        owner => owner.Value.EnumerateArray().Select(block =>
        {
            ushort cls = block.GetProperty("classId").GetUInt16();
            var input = TfBlockDeserializer.ExtractStairRunInput(Convert.FromBase64String(block.GetProperty("bytes").GetString()!),
                0, block.GetProperty("size").GetUInt32(), cls);
            if (!int.TryParse(owner.Name, out int id) || input?.OwnerId != id) input = null;
            return new { classId = cls, supported = input != null, input };
        }).ToArray()),
    "stair-component-ids" => StairComponentIds(refs),
    "stair-name-parameters" => refs.EnumerateObject().ToDictionary(owner => owner.Name,
        owner => owner.Value.EnumerateArray().Select(block =>
        {
            ushort cls = block.GetProperty("classId").GetUInt16();
            var input = TfBlockDeserializer.ExtractStairNameParameters(Convert.FromBase64String(block.GetProperty("bytes").GetString()!),
                0, block.GetProperty("size").GetUInt32(), cls);
            if (!int.TryParse(owner.Name, out int id) || input?.OwnerId != id) input = null;
            return new { classId = cls, supported = input != null, ownerId = input?.OwnerId,
                doubles = input?.Values.Doubles.Select(row => new { id = row.ParamId, value = row.Value }),
                ints = input?.Values.Ints.Select(row => new { id = row.ParamId, value = row.Value }),
                strings = input?.Values.Strings.Select(row => new { id = row.ParamId, value = row.Value }),
                elementIds = input?.Values.ElementIds.Select(row => new { id = row.ParamId, value = row.Value }) };
        }).ToArray()),
    "stair-name-definitions" => refs.EnumerateObject().ToDictionary(owner => owner.Name,
        owner => owner.Value.EnumerateArray().Select(block =>
        {
            ushort cls = block.GetProperty("classId").GetUInt16();
            byte[] bytes = Convert.FromBase64String(block.GetProperty("bytes").GetString()!);
            string? name = int.TryParse(owner.Name, out int id) && BitConverter.ToUInt32(bytes, 0) == id
                ? TfBlockDeserializer.ExtractOwnStringParameterDefinitionName(bytes, 0, block.GetProperty("size").GetUInt32(), cls) : null;
            return new { classId = cls, supported = name != null, name };
        }).ToArray()),
    "railing-stair-hosts" => refs.EnumerateObject().ToDictionary(owner => owner.Name,
        owner => owner.Value.EnumerateArray().Select(block =>
        {
            ushort cls = block.GetProperty("classId").GetUInt16();
            bool exact = TfBlockDeserializer.TryExtractRailingStairHost(Convert.FromBase64String(block.GetProperty("bytes").GetString()!),
                0, block.GetProperty("size").GetUInt32(), cls, out int host);
            return new { classId = cls, exactHost = exact, stairId = host };
        }).ToArray()),
    _ => throw new ArgumentException($"unknown mode {mode}"),
};
using (var output = File.Create(args[3])) JsonSerializer.Serialize(output, result);
return 0;

static object HostVerticalRegions(JsonElement owners)
{
    return owners.EnumerateObject().ToDictionary(owner => owner.Name, owner =>
        owner.Value.EnumerateArray().Select(block =>
        {
            ushort cls = block.GetProperty("classId").GetUInt16();
            byte[] bytes = Convert.FromBase64String(block.GetProperty("bytes").GetString()!);
            RvtIO.Internal.RawHostVerticalRegions? regions = null;
            bool complete = uint.TryParse(owner.Name, out uint id)
                && BitConverter.ToUInt32(bytes, 0) == id
                && TfBlockDeserializer.TryExtractHostTypeVerticalRegions(bytes, 0,
                    block.GetProperty("size").GetUInt32(), cls, out regions);
            RvtIO.Internal.HostVerticalRegionTopology.ParallelLayerRegion[] parallelLayers = [];
            bool parallelLayersValidated = complete && TfBlockDeserializer.TryExtractHostTypeLayers(bytes, 0,
                block.GetProperty("size").GetUInt32(), cls, out var layers)
                && RvtIO.Internal.HostVerticalRegionTopology.TryConfirmParallelRectangularLayers(
                    regions, layers, out parallelLayers);
            return new
            {
                classId = cls, complete, regions, parallelLayersValidated, parallelLayers,
                basicWallThicknessFeet = TfBlockDeserializer.TryExtractBasicWallTypeThicknessFeet(bytes, 0,
                    block.GetProperty("size").GetUInt32(), cls, out double ownWidth) ? (double?)ownWidth : null,
            };
        }).ToArray());
}

static object StairComponentIds(JsonElement blocks)
    => blocks.EnumerateObject().ToDictionary(element => element.Name,
        element => element.Value.EnumerateArray().Where(block => block.GetProperty("classId").GetUInt16() == 3681)
            .Select(block =>
            {
                byte[] bytes = Convert.FromBase64String(block.GetProperty("bytes").GetString()!);
                return TfBlockDeserializer.ExtractStairComponentIds(bytes, 0, block.GetProperty("size").GetUInt32(), 3681);
            }).ToArray());

static object ModelElements(string path, HashSet<int> ids, string? outputIfc = null)
{
    var reader = new RvtIO.RvtFileReader(new RvtIO.RvtReaderOptions
    {
        DiagnosticOutput = Console.Error,
    });
    var model = reader.Read(path);
    var state = reader.LastParsingState!;
    var selected = model.Elements.Where(element => ids.Contains(element.Id)).Select(element => new
    {
        element.Id, element.Name, element.CategoryId, element.Category, element.TypeId, element.FamilyTypeId,
        element.IsBlockOnly, element.IsDiscovered, element.NativeMasterSymbolId, element.SerializedSystemTypeId,
        element.SerializedSystemTypeClassId, element.FamilyName, element.ValidatedSystemFamilyName,
        element.ValidatedStairComponentTypeName, element.ValidatedCeilingTypeThicknessFeet, element.ValidatedWallTypeThicknessFeet,
        element.Location, element.BoundingBox,
        element.Layers, element.Geometry,
        ownPrimaryPlanarBody = element.OwnPrimaryPlanarBody,
        element.FamilyCutGeometry, element.FamilyCutLoops, element.SerializedCutLoopCount,
        element.FamilyCutClosurePlaneOrigin, element.FamilyCutClosurePlaneNormal,
        faceInputs = element.Geometry?.Faces.Select((face, index) => new
        {
            index, face.AlternativeOuterVertices,
        }).ToArray(),
        occurrenceTypeInput = state.SystemOccurrenceTypeIds.TryGetValue((uint)element.Id, out var occurrence)
            ? new { occurrence.BlockCount, occurrence.ClassId, occurrence.TypeId } : null,
        typeInputs = element.SerializedSystemTypeId is int exactTypeId
            ? model.Elements.Where(type => type.Id == exactTypeId).Select(type => new
            {
                type.Id, type.CategoryId, type.TypeId, type.Layers, type.SerializedOwnElementClassId,
                type.SerializedHostTypeLayers,
                root = state.SystemTypeRoots.TryGetValue((uint)type.Id, out var typeRoot)
                    ? new { typeRoot.BlockCount, typeRoot.ClassId, typeRoot.Discriminator } : null,
                host = state.HostTypeCompoundStructures.TryGetValue((uint)type.Id, out var hostRoot)
                    ? new { hostRoot.BlockCount, hostRoot.ClassId, hostRoot.Layers, hostRoot.TypeName } : null,
            }).ToArray() : null,
        ownWallLocation = state.OwnWallLocationLines.GetValueOrDefault((uint)element.Id),
        element.SerializedOwnElementClassId, element.SerializedOwningElementId,
        BaseFields = state.ElemBaseFields.GetValueOrDefault((uint)element.Id),
        Native = RvtIO.RootProbe.ParameterInputProbe.OwnRoot(element.SerializedOwnElementClassId ?? 0,
            state.NativeParams.GetValueOrDefault((uint)element.Id)),
        UserStrings = state.ElemParamStrings.GetValueOrDefault((uint)element.Id),
        Parameters = element.Parameters.Select(RvtIO.RootProbe.ParameterInputProbe.Materialized).ToArray(),
    }).ToArray();
    if (outputIfc != null)
    {
        new RvtIO.Ifc.IfcExporter(Console.Error).Export(model, outputIfc);
        Console.Error.WriteLine($"IFC written to {outputIfc}");
    }
    return outputIfc == null ? (object)selected : new
    {
        selected, ownWallLocationCount = state.OwnWallLocationLines.Count,
        ownWallPlanarBodies = state.OwnWallPlanarBodies.OrderBy(p => p.Key).Select(p => new {
            id = p.Key, p.Value.BlockCount, p.Value.Failure, faceCount = p.Value.Body?.Faces.Count,
            candidate = model.Elements.Any(e => e.Id == p.Key && e.OwnPrimaryPlanarBody != null),
        }).ToArray(),
        ownWallWidths = model.Elements.Where(e => e.ValidatedWallTypeThicknessFeet != null).Select(e =>
        {
            double? validated = e.ValidatedWallTypeThicknessFeet;
            try
            {
                e.ValidatedWallTypeThicknessFeet = null;
                bool oldAvailable = RvtIO.Ifc.IfcExporter.TryGetSerializedWallWidthFeet(e, out double oldWidth);
                return new { e.Id, e.SerializedSystemTypeId, feet = validated, oldAvailable, oldWidth };
            }
            finally { e.ValidatedWallTypeThicknessFeet = validated; }
        }).ToArray(),
        ownWallLocations = state.OwnWallLocationLines.OrderBy(pair => pair.Key).Select(pair => new
        {
            id = pair.Key, line = pair.Value,
            root = state.OwnedElementClassRoots.GetValueOrDefault(pair.Key),
        }).ToArray(),
    };
}

static object ModelTypeNames(string path, string? outputIfc = null)
{
    var reader = new RvtIO.RvtFileReader(new RvtIO.RvtReaderOptions
    {
        DiagnosticOutput = Console.Error,
    });
    var model = reader.Read(path);
    var state = reader.LastParsingState!;
    var index = model.Elements.GroupBy(element => element.Id).ToDictionary(group => group.Key, group => group.ToArray());
    var result = state.SystemTypeRoots.Where(pair => pair.Value.ClassId is 1915 or 646 or 2559 or 873 or 3736)
        .OrderBy(pair => pair.Key).Select(pair => new
        {
            id = pair.Key, pair.Value.BlockCount, pair.Value.ClassId, pair.Value.Discriminator,
            host = state.HostTypeCompoundStructures.TryGetValue(pair.Key, out var host) ? new
            {
                host.BlockCount, host.ClassId, host.TypeName, layersDecoded = host.Layers != null,
            } : null,
            curtainName = state.NewCurtainWallTypeNames.GetValueOrDefault(pair.Key),
            cornice = state.CorniceTypeNames.TryGetValue(pair.Key, out var cornice) ? new
            { cornice.BlockCount, cornice.Name } : null,
            component = state.StairComponentTypeNames.TryGetValue(pair.Key, out var component) ? new
            { component.BlockCount, component.ClassId, component.Name } : null,
            curtainFunction = state.NewCurtainWallTypeFunctions.TryGetValue(pair.Key, out var curtain) ? new
            {
                curtain.BlockCount, curtain.Function,
            } : null,
            materialized = index.GetValueOrDefault((int)pair.Key)?.Select(element => new
            {
                element.Id, element.Name, element.FamilyName, element.CategoryId, element.IsBlockOnly,
                element.SerializedSystemTypeClassId, element.ValidatedSystemFamilyName, element.ValidatedIfcTypeObjectName,
                element.ValidatedHostTypeLayerSetName, element.SerializedCurtainWallTypeName,
                element.SerializedCurtainWallFunction, element.SerializedBasicWallTypeFunction,
                element.ValidatedCorniceTypeName, element.ValidatedStairComponentTypeName,
            }).ToArray(),
        }).ToArray();
    if (outputIfc != null)
    {
        new RvtIO.Ifc.IfcExporter(Console.Error).Export(model, outputIfc);
        Console.Error.WriteLine($"IFC written to {outputIfc}");
    }
    return result;
}

static object LayerGroups(string path, JsonElement refs)
{
    var reader = new RvtIO.RvtFileReader(new RvtIO.RvtReaderOptions
    {
        DiagnosticOutput = Console.Error,
    });
    var model = reader.Read(path);
    var counts = model.Elements.GroupBy(element => element.Id).ToDictionary(group => group.Key, group => group.Count());
    var elements = model.Elements.Where(element => counts[element.Id] == 1).ToDictionary(element => element.Id);
    var products = new List<(RvtIO.Model.Element Element, string IfcType)>();
    foreach (var property in refs.GetProperty("products").EnumerateObject())
    {
        if (!int.TryParse(property.Name, out int id) || !elements.TryGetValue(id, out var element)
            || element.Layers is not { Count: > 0 }) continue;
        foreach (var product in property.Value.EnumerateArray())
            products.Add((element, product[0].GetString()!));
    }
    var groups = products.GroupBy(item =>
        (item.IfcType is "IFCWALL" or "IFCWALLSTANDARDCASE" ? "AXIS2" : "AXIS3") + ":" +
        string.Join(";", item.Element.Layers!.OrderBy(layer => layer.LayerIndex)
            .Select(layer => $"{layer.MaterialId}:{layer.ThicknessMm:R}")));
    return new
    {
        groups = groups.Select(group => new
        {
            key = group.Key,
            name = RvtIO.Ifc.IfcExporter.ResolveValidatedLayerSetName(group.ToList(), elements, counts),
            members = group.Select(item => new
            {
                item.Element.Id, item.IfcType, item.Element.Name, item.Element.CategoryId,
                item.Element.SerializedSystemTypeId, item.Element.Layers,
            }),
        }).ToArray(),
        types = reader.LastParsingState!.HostTypeCompoundStructures.Select(pair => new
        {
            id = pair.Key, pair.Value.BlockCount, pair.Value.ClassId, pair.Value.TypeName,
            layers = pair.Value.Layers?.Select(layer => new
            {
                widthFeet = BitConverter.Int64BitsToDouble(layer.LayerWidthBits),
                layer.RawMaterialId, layer.RawLayerId,
            }),
            root = reader.LastParsingState.SystemTypeRoots.GetValueOrDefault(pair.Key),
            materialized = elements.TryGetValue((int)pair.Key, out var type) ? new
            {
                type.CategoryId, type.IsBlockOnly, type.SerializedSystemTypeClassId,
                type.ValidatedSystemFamilyName, type.ValidatedHostTypeLayerSetName,
            } : null,
        }).ToArray(),
    };
}

static List<byte[]> ReadChunks(string path)
{
    var chunks = new List<byte[]>();
    using var root = RootStorage.OpenRead(path);
    var partitions = root.OpenStorage("Partitions");
    var decompressor = new ChunkDecompressor();
    foreach (var entry in partitions.EnumerateEntries()
                 .Where(entry => entry.Type == EntryType.Stream)
                 .OrderByDescending(entry => entry.Length))
    {
        using var stream = partitions.OpenStream(entry.Name);
        var raw = new byte[stream.Length];
        stream.ReadExactly(raw);
        chunks.AddRange(decompressor.DecompressAll(raw));
    }
    return chunks;
}

static Dictionary<uint, List<object>> ClassRoots(string path, HashSet<ushort> classes)
{
    return ClassRootsFromChunks(SequentialChunks(), classes);

    IEnumerable<byte[]> SequentialChunks()
    {
        using var root = RootStorage.OpenRead(path);
        var partitions = root.OpenStorage("Partitions");
        var decompressor = new ChunkDecompressor();
        foreach (var entry in partitions.EnumerateEntries()
                     .Where(entry => entry.Type == EntryType.Stream)
                     .OrderByDescending(entry => entry.Length))
        {
            using var stream = partitions.OpenStream(entry.Name);
            var raw = new byte[stream.Length];
            stream.ReadExactly(raw);
            var chunks = decompressor.DecompressAll(raw);
            Console.Error.WriteLine($"Class-root partition {entry.Name}: {chunks.Count} chunks, {chunks.Sum(chunk => (long)chunk.Length)} decompressed bytes");
            foreach (var chunk in chunks) yield return chunk;
        }
    }
}

static Dictionary<uint, List<object>> ClassRootsFromChunks(IEnumerable<byte[]> chunks, HashSet<ushort> classes,
    bool inMemory = false)
{
    var roots = new Dictionary<uint, List<object>>();
    // No guessed element ids: every walker anchor must carry the native size footer.
    TfStreamWalker.BlockVisitor visit = (segment, start, size, classId, id) =>
    {
        if (!classes.Contains(classId) || !Owned(segment, start, id) || size < 8
            || BitConverter.ToUInt32(segment, start + 16 + (int)size - 4) != size) return;
        Add(roots, id, (object)new
        {
            classId, size,
            bytes = Convert.ToBase64String(segment, start, checked(16 + (int)size)),
        });
    };
    if (inMemory) TfStreamWalker.Walk(chunks.ToList(), [], visit);
    else TfStreamWalker.WalkStreaming(chunks, [], visit);
    return roots;
}

static object StorageEntries(string path)
{
    using var root = RootStorage.OpenRead(path);
    var entries = new List<object>();
    void Walk(Storage storage, string prefix)
    {
        foreach (var entry in storage.EnumerateEntries())
        {
            string name = prefix + entry.Name;
            entries.Add(new { path = name, type = entry.Type.ToString(), length = entry.Length });
            if (entry.Type == EntryType.Storage) Walk(storage.OpenStorage(entry.Name), name + "/");
        }
    }
    Walk(root, "");
    return entries;
}

static object StreamPages(string path, string streamPath, bool exact = false)
{
    using var root = RootStorage.OpenRead(path);
    var names = streamPath.Split('/');
    Storage storage = root;
    foreach (var name in names[..^1]) storage = storage.OpenStorage(name);
    using var stream = storage.OpenStream(names[^1]);
    var raw = new byte[stream.Length];
    stream.ReadExactly(raw);
    IEnumerable<byte[]> chunks = exact
        ? [ExactStreamProbe.Decode(raw, streamPath)]
        : new ChunkDecompressor().DecompressAll(raw);
    return new
    {
        path = streamPath, raw = Convert.ToBase64String(raw),
        validation = exact ? "gzip-crc32-isize" : "not-checked",
        chunks = chunks
            .Select(bytes => new { length = bytes.Length, bytes = Convert.ToBase64String(bytes) }),
    };
}

static object ReadDocumentRootPrefix(string path)
{
    using var root = RootStorage.OpenRead(path);
    byte[] data = PagedGzipStream.Read(root, "Global", "Latest")
        ?? throw new InvalidDataException("Global/Latest is missing or failed exact CRC/length decoding");
    var prefix = TfBlockDeserializer.ExtractOwnDocumentRootPrefix(data)
        ?? throw new InvalidDataException("Unsupported or malformed own ADocument header");
    return new { path = "Global/Latest", validation = "gzip-crc32-isize", length = data.Length,
        sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant(), prefix };
}

static object ReadElementTable(string path)
{
    using var root = RootStorage.OpenRead(path);
    byte[] exact = PagedGzipStream.Read(root, "Global", "ElemTable")
        ?? throw new InvalidDataException("ElemTable is missing or failed exact CRC/length decoding");
    var membership = ElemTableReader.Read(root, out var worksets, out var creationDates);
    return new { validation = "gzip-crc32-isize", bytes = Convert.ToBase64String(exact), membership,
        worksets, creationDates };
}

static object ReadDocumentManagerPrefix(string path)
{
    using var root = RootStorage.OpenRead(path);
    byte[] data = PagedGzipStream.Read(root, "Global", "Latest")
        ?? throw new InvalidDataException("Global/Latest is missing or failed exact CRC/length decoding");
    var prefix = TfBlockDeserializer.ExtractOwnDocumentManagerPrefix(data)
        ?? throw new InvalidDataException("Unsupported or malformed own AppInfoManager prefix");
    return new { path = "Global/Latest", validation = "gzip-crc32-isize", length = data.Length,
        sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant(), prefix };
}

static object ReadDocumentSchemas(string path)
{
    using var root = RootStorage.OpenRead(path);
    byte[] data = PagedGzipStream.Read(root, "Global", "Latest")
        ?? throw new InvalidDataException("Global/Latest missing or rejected");
    var prefix = TfBlockDeserializer.ExtractOwnDocumentSchemas(data, out string? failure)
        ?? throw new InvalidDataException("Own document schemas rejected: " + failure);
    return new { validation = "gzip-crc32-isize", length = data.Length,
        sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant(), prefix };
}

static object ReadDocumentSiFactors(string path, bool cores = false, bool conversions = false)
{
    using var root = RootStorage.OpenRead(path);
    byte[] data = PagedGzipStream.Read(root, "Global", "Latest")
        ?? throw new InvalidDataException("Global/Latest missing or rejected");
    var prefix = TfBlockDeserializer.ExtractOwnDocumentSchemas(data, out string? failure)
        ?? throw new InvalidDataException("Own document schemas rejected: " + failure);
    var factors = UnitSchemaEvaluator.EvaluateOwn(prefix.Units, out failure)
        ?? throw new InvalidDataException("Own document SI projection rejected: " + failure);
    if (conversions)
    {
        var result = UnitSchemaEvaluator.EvaluateDisplay(prefix.Units, out failure)
            ?? throw new InvalidDataException("Own numeric display rejected: " + failure);
        var components = UnitSchemaEvaluator.EvaluateCore(prefix.Units, out failure)
            ?? throw new InvalidDataException("Own unit core rejected: " + failure);
        return new { validation = "gzip-crc32-isize", length = data.Length,
            sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant(),
            conversions = ConversionBits(result, components), ownSchemaEntries = prefix.Units.Entries.Length };
    }
    if (cores)
    {
        var result = UnitSchemaEvaluator.EvaluateCore(prefix.Units, out failure)
            ?? throw new InvalidDataException("Own unit core rejected: " + failure);
        return new { validation = "gzip-crc32-isize", length = data.Length,
            sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant(),
            unitCores = CoreBits(result), ownSchemaEntries = prefix.Units.Entries.Length };
    }
    return new { validation = "gzip-crc32-isize", length = data.Length,
        sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant(),
        siFactorBits = SiBits(factors), ownSchemaEntries = prefix.Units.Entries.Length };
}

static Dictionary<string,string> SiBits(IReadOnlyDictionary<string,double> factors)
    => factors.ToDictionary(x => x.Key, x => BitConverter.DoubleToUInt64Bits(x.Value).ToString("X16"), StringComparer.Ordinal);

static object CoreBits(IReadOnlyDictionary<string,UnitSchemaEvaluator.UnitCore> cores)
    => cores.ToDictionary(x => x.Key, x => new { x.Value.Dimensions, x.Value.Absolute,
        siFactorBits = BitConverter.DoubleToUInt64Bits(x.Value.SiFactor).ToString("X16"),
        offsetBits = BitConverter.DoubleToUInt64Bits(x.Value.Offset).ToString("X16"),
        internalScaleBits = BitConverter.DoubleToUInt64Bits(x.Value.InternalScale).ToString("X16"),
        linearFromOneBits = BitConverter.DoubleToUInt64Bits(x.Value.LinearFromInternal(1.0)).ToString("X16"),
        linearToOneBits = BitConverter.DoubleToUInt64Bits(x.Value.LinearToInternal(1.0)).ToString("X16") }, StringComparer.Ordinal);

static object ConversionBits(UnitSchemaEvaluator.UnitConversions units, IReadOnlyDictionary<string,UnitSchemaEvaluator.UnitCore> cores)
{
    static string Hex(double value) => BitConverter.DoubleToUInt64Bits(value).ToString("X16");
    static object? Info(UnitSchemaEvaluator.NumericUnitInfo? info) => info == null ? null : new
    {
        info.FullId, info.Core.Dimensions, info.Core.Absolute, siFactorBits = Hex(info.Core.SiFactor),
        offsetBits = Hex(info.Core.Offset), internalScaleBits = Hex(info.Core.InternalScale),
    };
    var keys = cores.Keys.Concat(units.NormalByName.Values.Select(x => x.FullId)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
    ulong[] common = [0xC00A000000000000,0xBFF0000000000000,0x8000000000000000,0,
        0x3F50624DD2F1A9FC,0x3FF0000000000000,0x3FA5D657595D6575,0x3FE3F28FCA3F28FC,0x3FEA3F28FCA3F290];
    ulong[] ratio = [0x3E112E0BE826D694,0x3E112E0BE826D695,0x3E112E0BE826D696,
        0xBE112E0BE826D694,0xBE112E0BE826D695,0xBE112E0BE826D696,0x0010000000000000,0x8010000000000000];
    ulong[] slope = [0x40567FFFFFFFFFFF,0x4056800000000000,0x4056800000000001,
        0xC0567FFFFFFFFFFF,0xC056800000000000,0xC056800000000001,
        0x4070DFFFFFFFFFFF,0x4070E00000000000,0x4070E00000000001,
        0xC070DFFFFFFFFFFF,0xC070E00000000000,0xC070E00000000001,
        0x4076800000000000,0xC076800000000000,0x4086800000000000,0xC086800000000000];
    object Samples(string id)
    {
        if (units.GetLegacy(id) == null) return Array.Empty<object>();
        var inputs = common.Concat(id == "autodesk.unit.unit:1ToRatio-1.0.1" ? ratio
            : id == "autodesk.unit.unit:slopeDegrees-1.0.0" ? slope : []);
        return inputs.Select(raw =>
        {
            double input = BitConverter.UInt64BitsToDouble(raw);
            if (!units.TryFromInternal(input,id,out double from) || !units.TryToInternal(input,id,out double to))
                throw new InvalidDataException("Numeric display sample rejected: " + id);
            return new { inputBits = raw.ToString("X16"), fromBits = Hex(from), toBits = Hex(to) };
        }).ToArray();
    }
    return new { normal = units.NormalByName.Values.ToDictionary(x => x.FullId, x => Info(x),StringComparer.Ordinal),
        legacyExact = units.LegacyExact.ToDictionary(x => x.Key, x => Info(x.Value),StringComparer.Ordinal),
        lookups = keys.ToDictionary(id => id, id => new { normal = Info(units.GetNormal(id)),
            legacy = Info(units.GetLegacy(id)), samples = Samples(id) },StringComparer.Ordinal) };
}

static object ReadCurrentDocumentUnits(string path, bool full, bool schemaMaps = false, bool siFactors = false, bool cores = false, bool conversions = false, bool ifcPlan = false)
{
    var reader = new RvtIO.RvtFileReader(new() { DiagnosticOutput = Console.Error });
    RvtIO.Internal.ParsingState state;
    if (full)
    {
        reader.Read(path);
        state = reader.LastParsingState ?? throw new InvalidDataException("Full Read did not retain diagnostic state");
    }
    else state = reader.ReadWithState(path).State;
    if (ifcPlan)
    {
        var current = state.OwnDocumentUnitOptions ?? throw new InvalidDataException("Own current document options unavailable");
        var values = RvtIO.Ifc.IfcNumericUnitPlan.Create(current.Value, state.OwnDocumentUnitConversions, out var failure)
            ?? throw new InvalidDataException(failure);
        return new { pipeline = full ? "public Read" : "ReadWithState (shortened)",
            ownerId = state.OwnDocumentUnitsElemId, options = current, numericIfcUnitPlan = IfcUnitPlanBits(values),
            ownSchemaEntries = state.OwnDocumentSchemas?.Units.Entries.Length };
    }
    if (conversions)
    {
        var values = state.OwnDocumentUnitConversions ?? throw new InvalidDataException("Own numeric display unavailable");
        var components = state.OwnDocumentUnitCores ?? throw new InvalidDataException("Own unit cores unavailable");
        var current = state.OwnDocumentUnitOptions ?? throw new InvalidDataException("Own current document options unavailable");
        return new { pipeline = full ? "public Read" : "ReadWithState (shortened)", conversions = ConversionBits(values, components),
            ownerId = state.OwnDocumentUnitsElemId, options = current,
            ownSchemaEntries = state.OwnDocumentSchemas?.Units.Entries.Length };
    }
    if (cores)
    {
        var values = state.OwnDocumentUnitCores ?? throw new InvalidDataException("Own document unit cores unavailable");
        var current = state.OwnDocumentUnitOptions ?? throw new InvalidDataException("Own current document options unavailable");
        return new { pipeline = full ? "public Read" : "ReadWithState (shortened)", unitCores = CoreBits(values),
            ownerId = state.OwnDocumentUnitsElemId, options = current,
            ownSchemaEntries = state.OwnDocumentSchemas?.Units.Entries.Length };
    }
    if (siFactors)
    {
        var factors = state.OwnDocumentSiFactors ?? throw new InvalidDataException("Own document SI projection unavailable");
        var current = state.OwnDocumentUnitOptions ?? throw new InvalidDataException("Own current document options unavailable");
        return new { pipeline = full ? "public Read" : "ReadWithState (shortened)", siFactorBits = SiBits(factors),
            ownerId = state.OwnDocumentUnitsElemId, options = current,
            ownSchemaEntries = state.OwnDocumentSchemas?.Units.Entries.Length };
    }
    if (schemaMaps)
    {
        var maps = state.OwnDocumentSchemas ?? throw new InvalidDataException("Own document schemas unavailable");
        return new { pipeline = full ? "public Read" : "ReadWithState (shortened)", maps.Units, maps.Parameters };
    }
    var options = state.OwnDocumentUnitOptions
        ?? throw new InvalidDataException("Current document UnitsElem lacks a unique complete own partition root");
    return new { pipeline = full ? "public Read" : "ReadWithState (shortened)",
        ownerId = state.OwnDocumentUnitsElemId, blockCount = state.OwnUnitOptions[options.OwnerId].BlockCount, options };
}

static object IfcUnitPlanBits(RvtIO.Ifc.IfcNumericUnitPlan plan)
{
    static string Hex(double value) => BitConverter.DoubleToUInt64Bits(value).ToString("X16");
    ulong[] inputs = [0xC00A000000000000,0xBFF0000000000000,0x8000000000000000,0,
        0x3F50624DD2F1A9FC,0x3FF0000000000000,0x3FA5D657595D6575,0x3FE3F28FCA3F28FC,0x3FEA3F28FCA3F290,
        0x7FEFFFFFFFFFFFFF,0xFFEFFFFFFFFFFFFF,1,0x8000000000000001,0x7FF0000000000000,0x7FF8000000000000];
    object Unit(RvtIO.Ifc.IfcNumericUnitPlan.CachedUnit value) => new
    {
        value.RequestedUnitId, value.ResolvedUnitId, value.IfcUnitType, value.SiPrefix, value.ConversionName,
        metresPerUnitBits = value.MetresPerUnit is double ratio ? Hex(ratio) : null,
        scaleBits = Hex(value.Scale), offsetBits = Hex(value.Offset),
        samples = inputs.Select(raw =>
        {
            double input = BitConverter.UInt64BitsToDouble(raw);
            bool scaled = value.TryScale(input, out double scale), unscaled = value.TryUnscale(input, out double unscale);
            return new { inputBits = raw.ToString("X16"), scaled, scaledBits = scaled ? Hex(scale) : null,
                unscaled, unscaledBits = unscaled ? Hex(unscale) : null };
        }).ToArray(),
    };
    return new { plan.LengthOptionSpec, plan.LengthOptionUnit,
        length = Unit(plan.Length), thermalTransmittance = Unit(plan.ThermalTransmittance) };
}

static object ReadDocumentUnitsLink(string path)
{
    using var root = RootStorage.OpenRead(path);
    byte[] data = PagedGzipStream.Read(root, "Global", "Latest")
        ?? throw new InvalidDataException("Global/Latest is missing or failed exact CRC/length decoding");
    var link = TfBlockDeserializer.ExtractOwnDocumentUnitsLink(data, out string? failure)
        ?? throw new InvalidDataException("Unsupported or malformed own document UnitsTracking FIFO: " + failure);
    return new { path = "Global/Latest", validation = "gzip-crc32-isize", length = data.Length,
        sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant(), link };
}


// The embedded names are used; version and locale come from the file's own BasicFileInfo.
static RvtIO.Lookup.RevitSystemFamilyNames? LoadNames(string path)
{
    using var root = RootStorage.OpenRead(path);
    var info = BasicFileInfoReader.Read(root);
    return RvtIO.Lookup.RevitSystemFamilyNames.Load(
        info.GetValueOrDefault("RevitVersion"), info.GetValueOrDefault("Locale when saved"));
}

// The category that agrees with each kind's root class, so the probe reports the name the
// exporter would use once the element's own category matches.
static string? KindName(RvtIO.Lookup.RevitSystemFamilyNames? names, ushort classId, int? discriminator)
{
    foreach (int category in new[] { -2000011, -2000032, -2001300, -2000035, -2000038, -2001263, -2000126, -2000181, -2000120 })
        if (RvtIO.Lookup.RevitSystemFamilyNames.KindOf(classId, discriminator, category) is { } kind)
            return names?.Get(kind);
    return null;
}

static HashSet<uint> Ids(JsonElement refs, string key) =>
    refs.GetProperty(key).EnumerateObject().Select(property => uint.Parse(property.Name)).ToHashSet();

static void Add<T>(Dictionary<uint, List<T>> map, uint id, T value)
{
    if (!map.TryGetValue(id, out var list)) map[id] = list = new();
    list.Add(value);
}

static bool Owned(byte[] segment, int start, uint id) => BitConverter.ToUInt32(segment, start) == id;

static object WallsAndCeilings(List<byte[]> chunks, JsonElement refs)
{
    HashSet<uint> wallIds = Ids(refs, "walls"), ceilingIds = Ids(refs, "ceilings");
    var walls = new Dictionary<uint, List<string>>();
    var ceilings = new Dictionary<uint, List<int>>();
    TfStreamWalker.Walk(chunks, wallIds.Concat(ceilingIds).ToHashSet(), (segment, start, size, classId, id) =>
    {
        if (!Owned(segment, start, id)) return;
        if (wallIds.Contains(id) && classId is 3524 or 361)
        {
            bool? top = TfBlockDeserializer.ExtractWallJoinedToTop(segment, start, size, classId, out string? why);
            Add(walls, id, top switch { true => "T", false => "F", null => "null:" + why });
        }
        if (ceilingIds.Contains(id) && classId == 642)
        {
            var root = TfBlockDeserializer.ExtractParams(segment, start, size, classId);
            Add(ceilings, id, root != null && root.Fields.TryGetValue("m_id", out int owner) && owner == (int)id
                && root.Fields.TryGetValue("m_ceilingAttributesId", out int typeId) ? typeId : -1);
        }
    });

    var typeIds = ceilings.Values.Where(list => list.Count == 1 && list[0] > 0).Select(list => (uint)list[0]).ToHashSet();
    var types = new Dictionary<uint, List<int[][]?>>();
    TfStreamWalker.Walk(chunks, typeIds, (segment, start, size, classId, id) =>
    {
        if (!typeIds.Contains(id) || classId != 646 || !Owned(segment, start, id)
            || BitConverter.ToUInt32(segment, start + 12) != 0xFFFF0286) return;
        Add(types, id, TfBlockDeserializer.TryExtractCompoundCeilingLayers(segment, start, size, classId, out var layers)
            ? layers.Select(layer => new[] { layer.RawFunction, layer.RawMaterialId }).ToArray() : null);
    });

    var materialIds = types.Values.Where(list => list.Count == 1 && list[0] != null)
        .SelectMany(list => list[0]!).Where(layer => layer[0] is 4 or 5 && layer[1] > 0)
        .Select(layer => (uint)layer[1]).ToHashSet();
    var materials = new Dictionary<uint, List<string?>>();
    TfStreamWalker.Walk(chunks, materialIds, (segment, start, size, classId, id) =>
    {
        if (!materialIds.Contains(id) || classId != 2486 || !Owned(segment, start, id)) return;
        Add(materials, id, TfBlockDeserializer.ExtractMaterialName(segment, start, size, classId));
    });
    return new { walls, ceilings, types, materials };
}

static object Types(List<byte[]> chunks, JsonElement refs, RvtIO.Lookup.RevitSystemFamilyNames? names)
{
    HashSet<uint> typeIds = Ids(refs, "types");
    var roots = new Dictionary<uint, List<string?>>();
    TfStreamWalker.Walk(chunks, typeIds, (segment, start, size, classId, id) =>
    {
        if (!typeIds.Contains(id) || !Owned(segment, start, id) || !TfBlockDeserializer.IsSubclassOf(classId, 106)) return;
        bool exact = TfBlockDeserializer.TryExtractSystemTypeRoot(segment, start, size, classId, out int? discriminator);
        Add(roots, id, exact ? KindName(names, classId, discriminator) ?? "" : null);
    });
    return new { roots };
}

static object Stairs(List<byte[]> chunks, JsonElement refs, RvtIO.Lookup.RevitSystemFamilyNames? names)
{
    HashSet<uint> stairIds = Ids(refs, "stairs");
    var stairs = new Dictionary<uint, List<int>>();
    TfStreamWalker.Walk(chunks, stairIds, (segment, start, size, classId, id) =>
    {
        if (!stairIds.Contains(id) || classId != 3681 || !Owned(segment, start, id)) return;
        Add(stairs, id, TfBlockDeserializer.TryExtractExactStairElementRoot(segment, start, size, classId, out var root)
            && root.Fields.TryGetValue("m_typeId", out int typeId) ? typeId : -1);
    });
    var typeIds = stairs.Values.Where(list => list.Count == 1 && list[0] > 0).Select(list => (uint)list[0]).ToHashSet();
    var types = new Dictionary<uint, List<string?>>();
    TfStreamWalker.Walk(chunks, typeIds, (segment, start, size, classId, id) =>
    {
        if (!typeIds.Contains(id) || classId != 3742 || !Owned(segment, start, id)) return;
        bool exact = TfBlockDeserializer.TryExtractSystemTypeRoot(segment, start, size, classId, out int? discriminator);
        Add(types, id, exact ? KindName(names, classId, discriminator) ?? "" : null);
    });
    return new { stairs, types, stairOccurrenceName = names?.StairOccurrenceName };
}

// The IFC GlobalId of every listed tag, and the element whose export id (or InstanceAsType
// sub-element id) is each listed IfcSpace/IfcSpaceType GlobalId, from the same exact tables
// and formulas the reader and exporter use.
static object Guids(string path, JsonElement refs)
{
    using var root = RootStorage.OpenRead(path);
    var history = PagedGzipStream.Read(root, "Global", "History");
    var episodes = history != null
        ? RvtIO.RvtFileReader.ParseEpisodeGuids(new List<byte[]> { history }, Console.Error)
        : new RvtIO.RvtFileReader.EpisodeTableParseResult();
    var elemTable = PagedGzipStream.Read(root, "Global", "ElemTable");
    var records = elemTable != null ? ElementExportIds.ParseElemTable(elemTable) : null;
    Console.Error.WriteLine($"episodes={episodes.Guids.Count}/{episodes.X}, records={records?.Count.ToString() ?? "unavailable"}");
    var exportIds = new Dictionary<uint, Guid>();
    foreach (var (id, record) in records ?? new())
        if (record.OriginalElementId >= 0 && ElementExportIds.EpisodeGuid(record, episodes.Guids, episodes.X) is Guid episode)
            exportIds[id] = ElementExportIds.ExportId(episode, (ulong)record.OriginalElementId);
    var byIfcGuid = new Dictionary<string, (uint Id, int Index)>();
    foreach (var (id, exportId) in exportIds)
    {
        byIfcGuid[RvtIO.Ifc.IfcGuid.ToIfcGuid(exportId)] = (id, 0);
        byIfcGuid[RvtIO.Ifc.IfcGuid.ToIfcGuid(ElementExportIds.SubElementId(
            exportId, RvtIO.Ifc.IfcExporter.InstanceAsTypeSubElementIndex))] = (id, RvtIO.Ifc.IfcExporter.InstanceAsTypeSubElementIndex);
    }
    var tags = new Dictionary<string, string?>();
    foreach (var product in refs.GetProperty("products").EnumerateObject())
        tags[product.Name] = uint.TryParse(product.Name, out uint id) && exportIds.TryGetValue(id, out var exportId)
            ? RvtIO.Ifc.IfcGuid.ToIfcGuid(exportId) : null;
    var spaces = new Dictionary<string, object?>();
    foreach (var guid in refs.GetProperty("spaces").EnumerateArray().Select(item => item.GetString()!))
        spaces[guid] = byIfcGuid.TryGetValue(guid, out var match) ? new[] { (long)match.Id, match.Index } : null;

    // Document-level GlobalIds: the project-level ids, and the element id each listed
    // storey GlobalId resolves to under the same document GUID.
    var document = new Dictionary<string, string?>();
    var storeys = new Dictionary<string, long?>();
    if (episodes.DetachGuid is Guid documentGuid)
    {
        foreach (var type in Enum.GetValues<RvtIO.Ifc.IfcExporter.ProjectLevelGuidType>())
            document[type.ToString()] = RvtIO.Ifc.IfcGuid.ToIfcGuid(
                ElementExportIds.ExportId(documentGuid, unchecked((uint)(int)type)));
        foreach (var guid in refs.GetProperty("storeys").EnumerateArray().Select(item => item.GetString()!))
        {
            var candidate = records?.Keys.FirstOrDefault(id => RvtIO.Ifc.IfcGuid.ToIfcGuid(
                ElementExportIds.ExportId(documentGuid, id)) == guid);
            storeys[guid] = candidate is uint id && id != 0 ? id : null;
        }
    }
    // Every element's export GlobalId, for ids that are not products (opening cutters).
    var exportGlobalIds = exportIds.ToDictionary(
        pair => pair.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
        pair => RvtIO.Ifc.IfcGuid.ToIfcGuid(pair.Value));
    return new { tags, spaces, document, storeys, exportGlobalIds };
}

// Control joins of the driver of every listed wall and curtain wall, from its own root;
// "null:<reason>" when the root is not read exactly.
static object WallJoins(List<byte[]> chunks, JsonElement refs)
{
    var wallIds = refs.GetProperty("products").EnumerateObject()
        .Where(product => product.Value.EnumerateArray().Any(item =>
            item[0].GetString() is "IFCWALL" or "IFCWALLSTANDARDCASE" or "IFCCURTAINWALL"))
        .Select(product => uint.Parse(product.Name)).ToHashSet();
    var joins = new Dictionary<uint, List<object>>();
    TfStreamWalker.Walk(chunks, wallIds, (segment, start, size, classId, id) =>
    {
        if (!wallIds.Contains(id) || classId is not (3524 or 361) || !Owned(segment, start, id)) return;
        var read = TfBlockDeserializer.ExtractWallEndJoins(segment, start, size, classId, out string? why);
        Add(joins, id, read != null
            ? read.Select(join => new[] { join.End, join.ElementId, join.JoinedEnd }).ToArray()
            : (object)("null:" + why));
    });
    return new { joins };
}

// Every listed IFCPLATE/IFCMEMBER root: its m_masterSymbolId and m_hostId; then every
// referenced symbol root: its FUNCTION_PARAM (-1001006) and whether it carries a parameter
// store at all. Used to replay the category rule in the listed order.
static object Roots(List<byte[]> chunks, HashSet<uint> ids)
{
    var roots = new Dictionary<uint, List<object>>();
    TfStreamWalker.Walk(chunks, ids, (segment, start, size, classId, id) =>
    {
        if (!ids.Contains(id) || !Owned(segment, start, id)) return;
        var root = TfBlockDeserializer.ExtractParams(segment, start, size, classId);
        if (!roots.TryGetValue(id, out var list)) roots[id] = list = new();
        list.Add(RvtIO.RootProbe.ParameterInputProbe.OwnRoot(classId, root));
    });
    return roots;
}

// Keeps every matching own block, including duplicates and undecodable roots, so ambiguity stays visible.
static object RawRoots(List<byte[]> chunks, HashSet<uint> ids)
{
    var roots = new Dictionary<uint, List<object>>();
    TfStreamWalker.Walk(chunks, ids, (segment, start, size, classId, id) =>
    {
        if (!ids.Contains(id) || !Owned(segment, start, id)) return;
        long end = (long)start + 16 + size;
        if (start < 0 || end > segment.Length || size > int.MaxValue - 16)
            throw new InvalidDataException($"Out-of-bounds own block: {id}/{classId}");
        bool exactType = TfBlockDeserializer.TryExtractSystemTypeRoot(
            segment, start, size, classId, out int? discriminator);
        Add(roots, id, new
        {
            classId, size, exactType, discriminator,
            bytes = Convert.ToBase64String(segment, start, checked((int)size + 16)),
        });
    });
    return roots;
}

// Diagnostic use of the exact library reader, including its local overrides.
// A failed field is reported as failed, with its position; no resynchronization.
static object ModelComputedFamilySymbols(string path, string outputIfc)
{
    var reader = new RvtIO.RvtFileReader(new RvtIO.RvtReaderOptions
    {
        DiagnosticOutput = Console.Error,
    });
    var model = reader.Read(path);
    var state = reader.LastParsingState!;
    var index = model.Elements.GroupBy(element => element.Id).ToDictionary(group => group.Key, group => group.ToArray());
    var source = state.ComputedFamilySymbols.OrderBy(pair => pair.Key).Select(pair =>
    {
        int symbolId = pair.Value.Symbol?.SymbolId ?? 0;
        var displayLink = state.FamilyInstanceSymbolIds.GetValueOrDefault(pair.Key);
        var displayName = displayLink.TypeId is int displayId && displayId > 0
            ? state.FamilySymbolNames.GetValueOrDefault((uint)displayId) : default;
        return new
        {
            id = pair.Key, pair.Value.BlockCount, pair.Value.Symbol,
            displayLink = new { displayLink.BlockCount, displayLink.TypeId },
            displayName = new { displayName.BlockCount, displayName.Name },
            originalRoot = state.ComputedSymbolTargetClasses.TryGetValue((uint)symbolId, out var root)
                ? new { root.BlockCount, root.ClassId } : null,
            materialized = index.GetValueOrDefault((int)pair.Key)?.Select(element => new
            {
                element.Id, element.Name, element.CategoryId, element.TypeId, element.FamilyTypeId,
                element.IsBlockOnly, element.ValidatedOriginalFamilySymbolId,
            }).ToArray(),
            originalMaterialized = index.GetValueOrDefault(symbolId)?.Select(element => new
            {
                element.Id, element.CategoryId, element.TypeId, element.IsBlockOnly,
            }).ToArray(),
        };
    }).ToArray();
    new RvtIO.Ifc.IfcExporter(Console.Error).Export(model, outputIfc);
    Console.Error.WriteLine($"IFC written to {outputIfc}");
    return source;
}

static object ComputedFamilySymbols(JsonElement blocks)
{
    return blocks.EnumerateObject().ToDictionary(element => element.Name,
        element => element.Value.EnumerateArray().Select(block =>
        {
            byte[] bytes = Convert.FromBase64String(block.GetProperty("bytes").GetString()!);
            uint size = block.GetProperty("size").GetUInt32();
            ushort cls = block.GetProperty("classId").GetUInt16();
            bool own = uint.TryParse(element.Name, out uint owner) && bytes.Length >= 4
                && BitConverter.ToUInt32(bytes) == owner;
            return new { classId = cls, size,
                computedSymbol = own ? TfBlockDeserializer.ExtractComputedFamilySymbol(bytes, 0, size, cls) : null,
                displaySymbolId = own ? TfBlockDeserializer.ExtractFamilyInstanceSymbolId(bytes, 0, size, cls) : null };
        }).ToArray());
}

static object TraceBlocks(JsonElement blocks, bool trackedHistories = false,
    bool coalescedHostHistories = false, bool stairCompatibleCurves = false, bool stairLayout = false,
    bool stairCountedHistories = false, bool stairParent = false, bool familyFields = false,
    bool familyFunctionFields = false)
{
    var output = new Dictionary<string, List<object>>();
    Type type = typeof(TfBlockDeserializer);
    Type cursorType = type.GetNestedType("Cursor", System.Reflection.BindingFlags.NonPublic)!;
    var read = type.GetMethod("ReadField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    var readDeferred = type.GetMethod("ReadDeferredObject", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    foreach (var element in blocks.EnumerateObject())
    {
        var traces = new List<object>(); output[element.Name] = traces;
        foreach (var block in element.Value.EnumerateArray())
        {
            ushort cls = block.GetProperty("classId").GetUInt16();
            byte[] bytes = Convert.FromBase64String(block.GetProperty("bytes").GetString()!);
            if (familyFields && (!int.TryParse(element.Name, out int familyOwner)
                || !DeferredFieldProbe.HasOwnFamilyFrame(bytes, block.GetProperty("size").GetUInt32(), cls, familyOwner)))
            {
                traces.Add(new { classId = cls, complete = false, failure = "Invalid own family frame/identity",
                    positions = Array.Empty<object>(), deferred = Array.Empty<object>(), end = 14,
                    expectedEnd = bytes.Length - 4, queue = Array.Empty<object>() });
                continue;
            }
            if (stairLayout && ((stairParent ? cls != 3681 : cls is not (3708 or 3686))
                || !int.TryParse(element.Name, out int expectedOwner)
                || !TfBlockDeserializer.TryExtractOwnElementClass(bytes, 0,
                    block.GetProperty("size").GetUInt32(), cls, out int ownId) || ownId != expectedOwner))
            {
                traces.Add(new { classId = cls, complete = false, failure = "Invalid own stair component frame/identity",
                    positions = Array.Empty<object>(), deferred = Array.Empty<object>(), end = 14,
                    expectedEnd = bytes.Length - 4, queue = Array.Empty<object>(), nativeStairLayout = (object?)null });
                continue;
            }
            object cursor = Activator.CreateInstance(cursorType)!;
            cursorType.GetField("D")!.SetValue(cursor, bytes);
            cursorType.GetField("Pos")!.SetValue(cursor, 14);
            cursorType.GetField("End")!.SetValue(cursor, bytes.Length - 4);
            cursorType.GetField("RootClassId")!.SetValue(cursor, cls);
            if (familyFields && cls == 1829)
            {
                cursorType.GetField("FamilyCutLoopQueueIndices")!.SetValue(cursor, new List<int>());
                cursorType.GetField("ExactFamilyCutInputs")!.SetValue(cursor, true);
            }
            if (familyFunctionFields && cls == 1829)
                cursorType.GetField("ExactFamilyFunctionExtraction")!.SetValue(cursor, true);
            var queue = new List<(ushort, uint)>();
            var historyTrace = new List<string>();
            if (trackedHistories) cursorType.GetField("CutTopologyTrace")!.SetValue(cursor, historyTrace);
            var positions = new List<object>();
            StairBoundaryProbe.Layout? nativeStairLayout = null;
            string? failure = null;
            var fields = TfBlockDeserializer.SerializationFields(cls);
            if (fields == null) failure = "Missing root schema";
            foreach (var field in fields ?? [])
            {
                int start = (int)cursorType.GetField("Pos")!.GetValue(cursor)!;
                try
                {
                    if (stairLayout && field is
                        { Declaring: 3687, Name: "m_compatibleCrvInfo", Type: 14, Mods: 0, Ref: null })
                    {
                        if (!StairBoundaryProbe.TryLayoutInfo(bytes, start, bytes.Length - 4,
                                out int layoutEnd, out nativeStairLayout, out var layoutDeferred))
                            throw new InvalidOperationException("Unsupported inline stair layout");
                        cursorType.GetField("Pos")!.SetValue(cursor, layoutEnd);
                        queue.AddRange(layoutDeferred);
                    }
                    else if (stairLayout && field is
                        { Declaring: 3687, Name: "m_logicJoinInfo", Type: 14, Mods: 0, Ref: null })
                    {
                        // Inline StairsLogicJoinInfo(3689): a vector of 3690 records.
                        // The original descriptor omits the concrete field reference.
                        if (start > bytes.Length - 8) throw new InvalidOperationException("Truncated stair joins");
                        uint count = BitConverter.ToUInt32(bytes, start);
                        if (count == uint.MaxValue) count = 0;
                        long joinEnd = (long)start + 4 + count * 16L;
                        if (count > 100_000 || joinEnd > bytes.Length - 4)
                            throw new InvalidOperationException("Unbounded stair joins");
                        cursorType.GetField("Pos")!.SetValue(cursor, (int)joinEnd);
                    }
                    else if (stairLayout && field is
                        { Declaring: 3686, Name: "m_landingRisersMap", Type: 14, Mods: 80, Ref: 867 })
                    {
                        // The exact pair's second member is two i32s, not a
                        // recursive 867 object. Keep the schema correction local.
                        if (start > bytes.Length - 8) throw new InvalidOperationException("Truncated landing risers");
                        uint count = BitConverter.ToUInt32(bytes, start);
                        if (count == uint.MaxValue) count = 0;
                        long mapEnd = (long)start + 4 + count * 12L;
                        if (count > 100_000 || mapEnd > bytes.Length - 4)
                            throw new InvalidOperationException("Unbounded landing risers");
                        cursorType.GetField("Pos")!.SetValue(cursor, (int)mapEnd);
                    }
                    else if (stairCompatibleCurves && field is
                        { Declaring: 3687, Name: "m_compatibleCrvInfo", Type: 14, Mods: 0, Ref: null })
                    {
                        if (!StairBoundaryProbe.TryCompatibleCurveInfo(bytes, start, bytes.Length - 4,
                                out int compatibleEnd, out _))
                            throw new InvalidOperationException("Unsupported compatible-curve record-vector hypothesis");
                        cursorType.GetField("Pos")!.SetValue(cursor, compatibleEnd);
                    }
                    else read.Invoke(null, [cursor, field, queue, 0, null]);
                    if (stairLayout && queue.Count > 100_000)
                        throw new InvalidOperationException("Unbounded stair deferred graph");
                }
                catch (System.Reflection.TargetInvocationException exception)
                {
                    failure = exception.InnerException?.GetType().Name + ":" + exception.InnerException?.Message;
                }
                catch (InvalidOperationException exception)
                {
                    failure = exception.GetType().Name + ":" + exception.Message;
                }
                int end = (int)cursorType.GetField("Pos")!.GetValue(cursor)!;
                positions.Add(new
                {
                    field.Declaring, field.Name, field.Type, field.Mods, field.Ref,
                    start, end, failure,
                    prefix = Convert.ToHexString(bytes.AsSpan(start, Math.Min(48, Math.Max(0, bytes.Length - 4 - start)))),
                });
                if (failure != null) break;
            }
            var deferred = new List<object>();
            if (failure == null)
                for (int index = 0; index < queue.Count; index++)
                {
                    if (stairLayout && queue.Count > 100_000)
                    {
                        failure = "Unbounded stair deferred graph";
                        break;
                    }
                    int start = (int)cursorType.GetField("Pos")!.GetValue(cursor)!;
                    int countBefore = queue.Count;
                    var fieldPositions = new List<object>();
                    List<HistoryBoundaryProbe.Candidate>? historyCandidates = null;
                    if (trackedHistories)
                    {
                        cursorType.GetField("CurrentGeomStepQueueIndex")!.SetValue(cursor,
                            TfBlockDeserializer.IsSubclassOf(queue[index].Item1, 96) ? index : -1);
                        cursorType.GetField("CurrentDeferredClassId")!.SetValue(cursor, queue[index].Item1);
                        cursorType.GetField("CurrentDeferredQueueIndex")!.SetValue(cursor, index);
                    }
                    try
                    {
                        if (familyFields && (queue[index].Item1 is 1285 or 2238 or 714 or 653
                            || cls == 1772 && queue[index].Item1 is 1740 or 775 or 90 or 1812 or 1809))
                        {
                            var trace = DeferredFieldProbe.Read(bytes, start, bytes.Length - 4, queue[index].Item1, cls);
                            cursorType.GetField("Pos")!.SetValue(cursor, trace.End);
                            queue.AddRange(trace.Queued);
                            fieldPositions.AddRange(trace.Fields);
                            failure = trace.Failure;
                        }
                        else if (stairLayout && TfBlockDeserializer.IsSubclassOf(queue[index].Item1, 96))
                        {
                            int historyEnd;
                            if (stairCountedHistories)
                            {
                                if (!StairBoundaryProbe.TryCurrentHistoryEnd(bytes, start, bytes.Length - 4, out historyEnd))
                                    throw new InvalidOperationException("Unsupported current stair history");
                            }
                            else
                            {
                                historyCandidates = HistoryBoundaryProbe.StairStepCandidates(bytes, start, bytes.Length - 4);
                                int[] tails = historyCandidates.Select(candidate => candidate.TailStart).Distinct().ToArray();
                                if (tails.Length != 1)
                                    throw new InvalidOperationException($"Expected one stair history tail, found {tails.Length}");
                                historyEnd = tails.Single();
                            }
                            cursorType.GetField("Pos")!.SetValue(cursor, historyEnd);
                            foreach (var field in TfBlockDeserializer.SerializationFields(queue[index].Item1)
                                         ?? throw new InvalidOperationException("Missing stair step schema"))
                            {
                                if (field.Declaring == 97 && field.Name is "m_faceHistTable"
                                        or "m_curveHistTableSet" or "m_edgeHistTable" or "m_edgeHistTableReverse")
                                    continue;
                                int fieldStart = (int)cursorType.GetField("Pos")!.GetValue(cursor)!;
                                if (field is { Declaring: 677, Name: "m_affectedEdgesTags", Type: 14, Mods: 80, Ref: null })
                                {
                                    if (!StairBoundaryProbe.TryAffectedTags(bytes, fieldStart, bytes.Length - 4,
                                            out int affectedEnd, out _))
                                        throw new InvalidOperationException("Unsupported affected-edge pairs");
                                    cursorType.GetField("Pos")!.SetValue(cursor, affectedEnd);
                                }
                                else read.Invoke(null, [cursor, field, queue, 1, null]);
                                int fieldEnd = (int)cursorType.GetField("Pos")!.GetValue(cursor)!;
                                fieldPositions.Add(new { field.Declaring, field.Name, field.Type, field.Mods, field.Ref,
                                    start = fieldStart, end = fieldEnd, bytes = Convert.ToHexString(bytes.AsSpan(fieldStart, fieldEnd - fieldStart)) });
                            }
                        }
                        else if (coalescedHostHistories && queue[index].Item1 == 4096)
                        {
                            historyCandidates = HistoryBoundaryProbe.VertCompoundStepCandidates(
                                bytes, start, bytes.Length - 4);
                            int[] tails = historyCandidates.Select(candidate => candidate.TailStart).Distinct().ToArray();
                            if (tails.Length != 1)
                                throw new InvalidOperationException($"Expected one complete history span, found {tails.Length}");
                            cursorType.GetField("Pos")!.SetValue(cursor, tails.Single());
                            foreach (var field in TfBlockDeserializer.SerializationFields(4096)
                                         ?? throw new InvalidOperationException("Missing VertCompoundStructureGStep schema"))
                            {
                                if (field.Declaring == 97 && field.Name is "m_faceHistTable"
                                        or "m_curveHistTableSet" or "m_edgeHistTable" or "m_edgeHistTableReverse")
                                    continue;
                                read.Invoke(null, [cursor, field, queue, 1, null]);
                            }
                        }
                        else if (trackedHistories && queue[index].Item1 == 4099
                                 || coalescedHostHistories && queue[index].Item1 == 737
                                 || (stairCompatibleCurves || stairLayout) && queue[index].Item1 is 653 or 3682
                                 || stairLayout && (queue[index].Item1 == 3712
                                     || TfBlockDeserializer.IsSubclassOf(queue[index].Item1, 1246)
                                     || TfBlockDeserializer.IsSubclassOf(queue[index].Item1, 523)
                                     || TfBlockDeserializer.IsSubclassOf(queue[index].Item1, 96)
                                     || TfBlockDeserializer.IsSubclassOf(queue[index].Item1, 70)))
                        {
                            foreach (var field in TfBlockDeserializer.SerializationFields(queue[index].Item1)
                                         ?? throw new InvalidOperationException("Missing queued host schema"))
                            {
                                int fieldStart = (int)cursorType.GetField("Pos")!.GetValue(cursor)!;
                                if (field is { Declaring: 737, Name: "m_layers", Type: 14, Mods: 80 })
                                {
                                    if (fieldStart + 4 > bytes.Length - 4)
                                        throw new InvalidOperationException("Missing native layer count");
                                    uint count = BitConverter.ToUInt32(bytes, fieldStart);
                                    long layerEnd = (long)fieldStart + 4 + count * 29L;
                                    if (count > 100_000 || layerEnd > bytes.Length - 4)
                                        throw new InvalidOperationException("Truncated native layers");
                                    cursorType.GetField("Pos")!.SetValue(cursor, (int)layerEnd);
                                }
                                else if (coalescedHostHistories
                                         && field is { Declaring: 737, Name: "m_segRefFaceKeys", Type: 14, Mods: 80, Ref: null })
                                {
                                    if (!HistoryBoundaryProbe.TryTripletContainerEnd(bytes, fieldStart,
                                            bytes.Length - 4, out int tripletEnd))
                                        throw new InvalidOperationException("Truncated research segment-reference triplets");
                                    cursorType.GetField("Pos")!.SetValue(cursor, tripletEnd);
                                }
                                else if (stairLayout && field is
                                         { Declaring: 1246, Name: "m_GInfo", Type: 14, Mods: 0, Ref: null })
                                {
                                    // No empirical id range: GInfo has an exact
                                    // descriptor, independent of observed ids.
                                    if (fieldStart > bytes.Length - 20)
                                        throw new InvalidOperationException("Invalid own GInfo prefix");
                                    read.Invoke(null, [cursor, field with { Name = "research_GInfo", Declaring = 0, Ref = 1247 }, queue, 1, null]);
                                }
                                else if (stairLayout && field is
                                         { Declaring: 523, Name: "m_Envelope", Type: 14, Mods: 0, Ref: null })
                                    read.Invoke(null, [cursor, field with { Ref = 524 }, queue, 1, null]);
                                else if (stairLayout && field is { Type: 13, Mods: 16, Cnt: not null }
                                         && field.Ref is not (524 or 1248))
                                    // Same fixed XYZ POD contract as GRepParserV2;
                                    // Envelope/uv PODs remain two doubles per corner.
                                    read.Invoke(null, [cursor, field with { Type = 7, Cnt = checked(field.Cnt.Value * 3) }, queue, 1, null]);
                                else read.Invoke(null, [cursor, field, queue, 1, null]);
                                int fieldEnd = (int)cursorType.GetField("Pos")!.GetValue(cursor)!;
                                fieldPositions.Add(new
                                {
                                    field.Declaring, field.Name, field.Type, field.Mods, field.Ref,
                                    start = fieldStart, end = fieldEnd,
                                    bytes = Convert.ToHexString(bytes.AsSpan(fieldStart, fieldEnd - fieldStart)),
                                });
                            }
                        }
                        else readDeferred.Invoke(null, [cursor, queue[index].Item1, queue]);
                    }
                    catch (System.Reflection.TargetInvocationException exception)
                    {
                        failure = exception.InnerException?.GetType().Name + ":" + exception.InnerException?.Message;
                    }
                    catch (InvalidOperationException exception)
                    {
                        failure = exception.GetType().Name + ":" + exception.Message;
                    }
                    int end = (int)cursorType.GetField("Pos")!.GetValue(cursor)!;
                    deferred.Add(new
                    {
                        index, classId = queue[index].Item1, start, end, countBefore, countAfter = queue.Count, failure,
                        fieldPositions,
                        historyCandidates,
                        prefix = Convert.ToHexString(bytes.AsSpan(start, Math.Min(48, Math.Max(0, bytes.Length - 4 - start)))),
                    });
                    if (failure != null) break;
                }
            if ((coalescedHostHistories || stairCompatibleCurves || stairLayout || familyFields) && failure == null
                && (int)cursorType.GetField("Pos")!.GetValue(cursor)! != bytes.Length - 4)
                failure = "Deferred graph does not finish at the block footer";
            traces.Add(new
            {
                classId = cls, complete = failure == null, failure, positions, deferred,
                historyTrace, nativeStairLayout,
                nativeLayers = coalescedHostHistories
                    && TfBlockDeserializer.TryExtractHostTypeLayers(bytes, 0,
                        block.GetProperty("size").GetUInt32(), cls, out var layers)
                    ? layers.Select(layer => new
                    {
                        layer.LayerWidthBits, layer.RawFunction, layer.RawEmbeddingType,
                        layer.RawMaterialId, layer.RawProfileId, layer.RawLayerId,
                        layer.RawCapFlag,
                    }).ToArray() : null,
                end = (int)cursorType.GetField("Pos")!.GetValue(cursor)!, expectedEnd = bytes.Length - 4,
                queue = queue.Select(item => new { classId = item.Item1, handle = item.Item2 }),
            });
        }
    }
    return output;
}

static object HostCuts(List<byte[]> chunks, JsonElement refs)
{
    var hostIds = refs.GetProperty("products").EnumerateObject()
        .Select(product => uint.TryParse(product.Name, out uint id) ? id : 0)
        .Where(id => id != 0).ToHashSet();
    var hosts = new Dictionary<uint, List<uint>?>();
    TfStreamWalker.Walk(chunks, hostIds, (segment, start, size, classId, id) =>
    {
        if (!hostIds.Contains(id) || !Owned(segment, start, id)
            || !TfBlockDeserializer.IsSubclassOf(classId, 363) || hosts.ContainsKey(id)) return;
        hosts[id] = TfBlockDeserializer.ExtractCuttingElementIds(segment, start, (uint)size, classId);
    });
    // Root class and category of every cutter and every listed opening Tag.
    var cutterIds = hosts.Values.Where(list => list != null).SelectMany(list => list!).ToHashSet();
    foreach (var product in refs.GetProperty("products").EnumerateObject())
        if (uint.TryParse(product.Name, out uint tagId)) cutterIds.Add(tagId);
    var cutters = new Dictionary<uint, int[]>();
    TfStreamWalker.Walk(chunks, cutterIds, (segment, start, size, classId, id) =>
    {
        if (!cutterIds.Contains(id) || !Owned(segment, start, id) || cutters.ContainsKey(id)) return;
        var root = TfBlockDeserializer.ExtractParams(segment, start, size, classId);
        cutters[id] = new[]
        {
            classId,
            root?.Fields.GetValueOrDefault("m_masterSymbolId", -1) ?? -1,
            root != null && root.Fields.TryGetValue("m_hostId", out int host) ? host : -1,
        };
    });
    return new { hosts, cutters };
}

static object DoorCuts(List<byte[]> chunks, JsonElement refs)
{
    var insertIds = refs.GetProperty("products").EnumerateObject()
        .Where(product => product.Value.EnumerateArray().Any(item =>
            item[0].GetString() is "IFCDOOR" or "IFCWINDOW" or "IFCOPENINGELEMENT"))
        .Select(product => uint.TryParse(product.Name, out uint id) ? id : 0)
        .Where(id => id != 0).ToHashSet();
    var inserts = new Dictionary<uint, List<int[]>>();
    TfStreamWalker.Walk(chunks, insertIds, (segment, start, size, classId, id) =>
    {
        if (!insertIds.Contains(id) || !Owned(segment, start, id)) return;
        var root = TfBlockDeserializer.ExtractParams(segment, start, size, classId);
        if (root == null) return;
        Add(inserts, id, new[]
        {
            root.Fields.GetValueOrDefault("m_masterSymbolId", -1),
            root.Fields.TryGetValue("m_hostId", out int host) ? host : -1,
            classId,
        });
    });
    var symbolIds = inserts.Values.SelectMany(list => list).Select(item => item[0])
        .Where(id => id > 0).Select(id => (uint)id).ToHashSet();
    var symbols = new Dictionary<uint, List<int[]>>();
    TfStreamWalker.Walk(chunks, symbolIds, (segment, start, size, classId, id) =>
    {
        if (!symbolIds.Contains(id) || !Owned(segment, start, id)) return;
        var loops = TfBlockDeserializer.ExtractFamilySymbolCutLoops(segment, start, (uint)size, classId, out int? rootCount);
        Add(symbols, id, new[] { loops?.Count ?? -1, loops?.Sum(loop => loop.Vertices.Count) ?? -1, rootCount ?? -1, classId });
    });
    return new { inserts, symbols };
}

static object Panels(List<byte[]> chunks, JsonElement refs)
{
    var componentIds = refs.GetProperty("products").EnumerateObject()
        .Where(product => product.Value.EnumerateArray().Any(item =>
            item[0].GetString() is "IFCPLATE" or "IFCMEMBER"))
        .Select(product => uint.Parse(product.Name)).ToHashSet();
    var components = new Dictionary<uint, List<int[]>>();
    TfStreamWalker.Walk(chunks, componentIds, (segment, start, size, classId, id) =>
    {
        if (!componentIds.Contains(id) || !Owned(segment, start, id)) return;
        var root = TfBlockDeserializer.ExtractParams(segment, start, size, classId);
        if (root == null) return;
        Add(components, id, new[]
        {
            root.Fields.GetValueOrDefault("m_masterSymbolId", -1),
            root.Fields.TryGetValue("m_hostId", out int host) ? host : -1,
            root.Ints.Count(item => item.ParamId == -1001006),
            classId,
        });
    });
    var symbolIds = components.Values.SelectMany(list => list).Select(item => item[0])
        .Where(id => id > 0).Select(id => (uint)id).ToHashSet();
    var symbols = new Dictionary<uint, List<int[]>>();
    TfStreamWalker.Walk(chunks, symbolIds, (segment, start, size, classId, id) =>
    {
        if (!symbolIds.Contains(id) || !Owned(segment, start, id)) return;
        var root = TfBlockDeserializer.ExtractParams(segment, start, size, classId);
        if (root == null) return;
        var function = root.Ints.Where(item => item.ParamId == -1001006).Select(item => item.Value).ToList();
        Add(symbols, id, new[] { function.Count == 1 ? function[0] : function.Count == 0 ? -100 : -200, classId });
    });
    return new { components, symbols };
}
