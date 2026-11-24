using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using IOScene;
using RvtIO.BuiltIns;
using RvtIO.Meshes;
using RvtIO.Model;

namespace RvtIO.Scene;

/// <summary>
/// Turns a Revit document into a scene (<see cref="SceneDocument"/>): one model, one element per drawable element of the document
/// with its triangle meshes (<see cref="ElementMeshes"/>), its names and its parameters, plus building storeys and decoded grid axes.
/// </summary>
/// <remarks>
/// <para>Lengths are converted from feet to metres (x 0.3048) into the internal coordinates of the document, Z up. A symbol that
/// elements place whole (<see cref="MeshSet.Symbols"/>) is one geometry per mesh, in the frame of its family, and each placement
/// is one instance per mesh with the placement's transform; a mirrored placement keeps its transform, so the winding of the
/// symbol's triangles is not changed. A mesh of an element's own is a geometry used once: its vertices are relative to the
/// lowest corner of the element's own meshes, which is the translation of its instances. Normals and indices are those of
/// the meshes. A mesh with a material has the instance material of that material element: its name and, where the file
/// gives it, its colour (else a neutral grey).</para>
/// <para>An element's <c>SourceId</c> is its Revit UniqueId, <c>IfcGuid</c> the GlobalId Revit's IFC export gives it (its valid
/// built-in IfcGUID parameter, else the export id of its UniqueId), <c>Category</c> the English name of its built-in category (else the
/// category the reader gives), <c>TypeName</c> the name of its type element. Its parameters are the property set
/// <c>revit:instance</c>; the parameters of its type are the property set <c>revit:type</c>, one set shared by every element of
/// the type. A parameter whose value the reader only estimates (<see cref="Parameter.IsApproximate"/>) is left out. The key of
/// a built-in parameter is <c>revit:bip:&lt;id&gt;</c>, other parameters have no key.</para>
/// <para>Values are text: the display string of <see cref="Parameter.AsValueString()"/>, with the invariant culture, and only
/// where it is the text Revit shows. A string is the stored text; a double is its value in the display unit and the unit symbol
/// (<c>3200.4 mm</c>) when the unit comes from the parameter's measurement spec and is mm, m², m³, ° or A, the units whose
/// factor converts Revit's internal unit, and is left out otherwise (no spec, as for every built-in double, whose unit the
/// reader only guesses from the parameter name, or a unit the reader converts with a factor that is not Revit's); an integer of a shared, project or family
/// parameter (a plain integer or a Yes/No, shown as 1 or 0) is the number in decimal; a built-in integer, which may be an
/// enumeration, and an element id are left out. Typed property values are not written.</para>
/// </remarks>
public static class RvtSceneReader
{
    private const double MetresPerFoot = 0.3048;
    private const int ModelId = 1;

    /// <summary>Reads the Revit file at <paramref name="path"/> with the options the RvtIO command line tool reads it with for an
    /// IFC export (the Revit 2022 layout, English system family names, faces with distinct openings kept apart) and turns it
    /// into a scene. The diagnostics also report the read: a stream a reader limit cut (<c>read-cut</c>) and the members of a
    /// stream that failed to decode, were rebuilt by structural recovery or decoded without a matching checksum
    /// (<c>failed-members</c>, <c>recovered-members</c>, <c>unverified-members</c>).</summary>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty, or <see cref="RvtSceneOptions.ModelKey"/> is empty.</exception>
    public static RvtSceneResult Read(string path, RvtSceneOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string key = options?.ModelKey ?? Path.GetFileName(path);
        if (key.Length == 0) throw new ArgumentException("The model key is empty.", nameof(options));
        var readDiagnostics = new RvtReadDiagnostics();
        var document = RvtDocument.Open(path, ReaderOptions(readDiagnostics, options));
        return Map(document, ElementMeshes.Extract(document, MeshOptions(options)), key, ReadDiagnostics(readDiagnostics));
    }

    /// <summary>Reads a scene and its diagnostics from a readable, seekable RVT stream with the same
    /// reader options as <see cref="Read(string, RvtSceneOptions?)"/>. The stream is read from its beginning, whatever its position,
    /// and only read: it is never written to, shortened or closed, also when it is writable.</summary>
    /// <remarks>A failure of the stream itself, such as an <see cref="IOException"/> or a cancellation, is thrown as the stream threw
    /// it; it is not reported as a file that is cut or damaged.</remarks>
    /// <param name="stream">The RVT input stream, which the caller keeps.</param>
    /// <param name="options">Scene options with a nonempty <see cref="RvtSceneOptions.ModelKey"/>: a stream has no file name to take it from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="stream"/> is not readable or not seekable, or is closed, or
    /// <see cref="RvtSceneOptions.ModelKey"/> is null or empty.</exception>
    /// <exception cref="RvtInputException">The stream is empty, is not a compound file, or is cut or damaged.</exception>
    /// <exception cref="RvtLimitException">A stream that requires an array is longer than one array can hold, or a file
    /// with no project partitions contains a source element ID above the range of document-local keys.</exception>
    public static RvtSceneResult ReadStream(Stream stream, RvtSceneOptions options) =>
        ReadStreamCore(stream, options, null);

    /// <summary>Reads a scene and keeps its triangle buffers, intermediate faces and element parameters in a separate temporary stream.
    /// The caller owns that stream and keeps it open until the scene is no longer needed.</summary>
    /// <param name="stream">The readable, seekable RVT input.</param>
    /// <param name="options">The options of the read, including the required model key.</param>
    /// <param name="triangleScratch">An empty, readable, writable, seekable stream distinct from the input
    /// and any partition scratch stream. Its contents are written during conversion and read while rendering or exporting the scene.</param>
    /// <exception cref="ArgumentException">The triangle scratch stream is not empty or separate, or does not support
    /// reading, writing and seeking.</exception>
    public static RvtSceneResult ReadStream(Stream stream, RvtSceneOptions options, Stream triangleScratch)
    {
        ArgumentNullException.ThrowIfNull(triangleScratch);
        if (ReferenceEquals(stream, triangleScratch) || !triangleScratch.CanRead || !triangleScratch.CanWrite
            || !triangleScratch.CanSeek || triangleScratch.Length != 0)
            throw new ArgumentException("Use a separate, empty, readable, writable, seekable triangle scratch stream.", nameof(triangleScratch));
        return ReadStreamCore(stream, options, triangleScratch);
    }

    private static RvtSceneResult ReadStreamCore(Stream stream, RvtSceneOptions options, Stream? triangleScratch)
    {
        ArgumentNullException.ThrowIfNull(options);
        string key = RequiredKey(options);
        var readDiagnostics = new RvtReadDiagnostics();
        var readerOptions = ReaderOptions(readDiagnostics, options);
        if (triangleScratch != null && readerOptions.CreatePartitionScratch is { } createPartitionScratch)
            readerOptions.CreatePartitionScratch = () =>
            {
                var partitionScratch = createPartitionScratch();
                if (ReferenceEquals(partitionScratch, triangleScratch))
                    throw new ArgumentException("Partition scratch must be separate from triangle scratch.", nameof(options));
                return partitionScratch;
            };
        var scratch = triangleScratch == null ? null : new SceneMeshScratch(triangleScratch);
        if (scratch != null)
        {
            readerOptions.ArchiveSceneGeometry = scratch.ArchiveGeometry;
            readerOptions.ArchiveSceneParameters = scratch.ArchiveParameters;
        }
        var document = RvtDocument.OpenStream(stream, readerOptions);
        var meshes = ElementMeshes.ExtractCore(document, MeshOptions(options), scratch == null ? null : scratch.Store);
        return Map(document, meshes, key, ReadDiagnostics(readDiagnostics), scratch);
    }

    private static RvtReaderOptions ReaderOptions(RvtReadDiagnostics diagnostics, RvtSceneOptions? options)
        => new()
        {
            R22Layout = true,
            SystemFamilyNameLocale = "ENU",
            SeparateFacesWithDistinctOpenings = true,
            Diagnostics = diagnostics,
            Progress = options?.ReadProgress,
            CreatePartitionScratch = options?.CreatePartitionScratch,
        };

    /// <summary>The scene diagnostics of a read: <c>read-cut</c> for each stream a reader limit cut, then for each stream read, in
    /// the order read, the members that failed to decode, were rebuilt by structural recovery or decoded without a matching
    /// checksum, by the rules of the issues of the export report.</summary>
    internal static IEnumerable<SceneDiagnostic> ReadDiagnostics(RvtReadDiagnostics diagnostics)
    {
        // A cut read is what the command line tool refuses to export; here the scene is made and the cut is an error.
        foreach (var hit in diagnostics.LimitHits)
            yield return new(SceneSeverity.Error, "read-cut",
                $"The stream {hit.Stream} was cut by the reader limit {hit.Limit}: {hit.Detail}. Elements may be missing.");
        foreach (var stream in diagnostics.Streams)
        {
            // A member that failed to decode is lost, as a cut is: an error.
            if (stream.Failed > 0)
                yield return new(SceneSeverity.Error, "failed-members",
                    $"The stream {stream.Name}: {stream.Failed} of {stream.Members} member(s) failed to decode. Elements may be missing.");
            if (stream.Recovered > 0)
                yield return new(SceneSeverity.Warning, "recovered-members",
                    $"The stream {stream.Name}: {stream.Recovered} of {stream.Members} member(s) were rebuilt by structural recovery. Elements may be incomplete or wrong.");
            // A rebuilt member is also counted unverified; it is reported once, as rebuilt.
            int unverified = stream.Unverified - stream.Recovered;
            if (unverified > 0)
                yield return new(SceneSeverity.Warning, "unverified-members",
                    $"The stream {stream.Name}: {unverified} of {stream.Members} member(s) decoded without a matching checksum. Elements may be incomplete or wrong.");
        }
    }

    /// <summary>Turns an opened document into a scene.</summary>
    /// <remarks>The path and stream reader methods report input diagnostics. A caller who opens the document reads them from
    /// <see cref="RvtReaderOptions.Diagnostics"/>.</remarks>
    /// <param name="document">The document, opened by the caller; open a file of a release other than Revit 2022 with
    /// <see cref="RvtReaderOptions.R22Layout"/>, or its elements have no geometry.</param>
    /// <param name="options">The options; <see cref="RvtSceneOptions.ModelKey"/> is required, as a document has no file name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    /// <exception cref="ArgumentException"><see cref="RvtSceneOptions.ModelKey"/> is null or empty.</exception>
    public static RvtSceneResult Read(RvtDocument document, RvtSceneOptions? options)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Map(document, ElementMeshes.Extract(document, MeshOptions(options)), RequiredKey(options));
    }

    /// <summary>Turns an opened document into a scene with meshes the caller extracted from it, for example with
    /// <see cref="ElementMeshes.Extract(RvtDocument, ElementMeshOptions?)"/> and options of its own.</summary>
    /// <remarks>The diagnostics of the read are reported only by <see cref="Read(string, RvtSceneOptions?)"/> and
    /// <see cref="ReadStream(Stream, RvtSceneOptions)"/>, which open the file; a caller who opens the document reads them from
    /// <see cref="RvtReaderOptions.Diagnostics"/>.</remarks>
    /// <param name="document">The document the meshes come from; the element facts are read from it.</param>
    /// <param name="meshes">The meshes of the drawable elements of <paramref name="document"/>.</param>
    /// <param name="options">The options; <see cref="RvtSceneOptions.ModelKey"/> is required, as a document has no file name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="meshes"/> is null.</exception>
    /// <exception cref="ArgumentException"><see cref="RvtSceneOptions.ModelKey"/> is null or empty.</exception>
    public static RvtSceneResult Read(RvtDocument document, MeshSet meshes, RvtSceneOptions? options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(meshes);
        return Map(document, meshes, RequiredKey(options));
    }

    private static ElementMeshOptions? MeshOptions(RvtSceneOptions? options) =>
        options is null ? null : new()
        {
            IncludeModelLinesAndReinforcement = options.IncludeModelLinesAndReinforcement,
            IncludeDatumGeometry = options.IncludeDatumGeometry,
        };

    private static string RequiredKey(RvtSceneOptions? options) => options?.ModelKey is { Length: > 0 } key ? key
        : throw new ArgumentException("A document has no file name: give the model key in RvtSceneOptions.ModelKey.", nameof(options));

    /// <summary>The scene of the meshes of <paramref name="document"/>; the element facts come from the document.</summary>
    internal static RvtSceneResult Map(RvtDocument document, MeshSet meshes, string modelKey,
        IEnumerable<SceneDiagnostic>? diagnostics = null, SceneMeshScratch? scratch = null)
    {
        var scene = new SceneDocument { Title = modelKey };
        scene.Models.Add(new SceneModel
        {
            Id = ModelId, Key = modelKey, Name = modelKey, SourceFormat = "rvt", SourceLengthUnit = "ft", Producer = Producer,
        });
        AddGrids(scene, document);
        var storeys = document.GetBuildingStoreyCatalog();
        foreach (var storey in storeys.Storeys)
            scene.Levels.Add(new SceneLevel
            {
                Id = storey.Id,
                ModelId = ModelId,
                Name = storey.Name,
                Elevation = double.IsFinite(storey.ElevationFeet) ? storey.ElevationFeet * MetresPerFoot : null,
            });
        var report = new List<SceneDiagnostic>(diagnostics ?? []);
        if (meshes.GeometryUndecoded)
            report.Add(new(SceneSeverity.Error, "geometry-undecoded",
                "The geometry of the file was not decoded: open it with RvtReaderOptions.R22Layout = true. Its elements have no geometry in the scene."));

        var materials = new HashSet<int>();
        int? MaterialOf(int id)
        {
            if (id <= 0) return null;
            if (materials.Add(id)) scene.Materials.Add(Material(document, id));
            return id;
        }

        var symbols = new Dictionary<int, List<(int GeometryId, int? MaterialId)>>();
        foreach (var (symbolId, symbolMeshes) in meshes.Symbols.OrderBy(symbol => symbol.Key))
            symbols[symbolId] = symbolMeshes.Select(mesh => (AddGeometry(scene, mesh, default, scratch), MaterialOf(mesh.MaterialId))).ToList();

        var typeSets = new Dictionary<int, int?>();
        var coverage = new Dictionary<MeshCoverage, int>();
        int withGeometry = 0, placements = 0, withoutUniqueId = 0;
        foreach (var mesh in meshes.Elements)
        {
            var element = document.GetElement(mesh.ElementId);
            var sceneElement = new SceneElement
            {
                Id = mesh.ElementId,
                ModelId = ModelId,
                LevelId = mesh.IsDatum ? null : storeys.GetStoreyId(mesh.ElementId),
                SourceId = element?.UniqueId,
                IfcGuid = element != null ? IfcGuidOf(element) : null,
                Kind = SceneNodeKind.Element,
                Name = (mesh.Line != null || mesh.Arc != null) && string.IsNullOrWhiteSpace(element?.Name) ? "Model Lines" : element?.Name ?? string.Empty,
                Category = CategoryName(element),
                TypeName = element != null ? document.GetTypeElement(element)?.Name ?? string.Empty : string.Empty,
            };
            if (sceneElement.SourceId == null) withoutUniqueId++;
            if (element != null)
            {
                if (AddPropertySet(scene, "Instance", "revit:instance", element.ReadParameters, element) is int instanceSet)
                    sceneElement.PropertySetIds.Add(instanceSet);
                if (document.GetTypeElement(element) is { } type)
                {
                    if (!typeSets.TryGetValue(type.Id, out int? typeSet))
                        typeSets[type.Id] = typeSet = AddPropertySet(scene, "Type", "revit:type", type.ReadParameters);
                    if (typeSet is int set) sceneElement.PropertySetIds.Add(set);
                }
            }
            scene.Elements.Add(sceneElement);

            int instancesBefore = scene.Instances.Count;
            if (mesh.Line is { } line)
            {
                var start = Vector(line.Start) * MetresPerFoot;
                var end = Vector(line.End) * MetresPerFoot;
                int geometryId = scene.Geometries.Count + 1;
                scene.Geometries.Add(new SceneGeometry
                {
                    Id = geometryId, Topology = SceneTopology.Lines, Vertices = [Vector3d.Zero, end - start], Indices = [0, 1],
                });
                scene.Instances.Add(new SceneInstance { ElementId = mesh.ElementId, GeometryId = geometryId, Transform = Transform3d.Translate(start) });
            }
            if (mesh.Arc is { } arc)
            {
                var start = Vector(arc[0]) * MetresPerFoot;
                var vertices = arc.Select(point => Vector(point) * MetresPerFoot - start).ToArray();
                int[] indices = new int[2 * (vertices.Length - 1)];
                for (int i = 0; i < vertices.Length - 1; i++)
                {
                    indices[2 * i] = i;
                    indices[2 * i + 1] = i + 1;
                }
                int geometryId = scene.Geometries.Count + 1;
                scene.Geometries.Add(new SceneGeometry
                {
                    Id = geometryId, Topology = SceneTopology.Lines, Vertices = vertices, Indices = indices,
                });
                scene.Instances.Add(new SceneInstance { ElementId = mesh.ElementId, GeometryId = geometryId, Transform = Transform3d.Translate(start) });
            }
            if (mesh.LinePairs is { } pairs)
            {
                var origin = Vector(pairs[0]) * MetresPerFoot;
                int geometryId = scene.Geometries.Count + 1;
                scene.Geometries.Add(new SceneGeometry
                {
                    Id = geometryId, Topology = SceneTopology.Lines,
                    Vertices = pairs.Select(point => Vector(point) * MetresPerFoot - origin).ToArray(),
                    Indices = Enumerable.Range(0, pairs.Count).ToArray(),
                });
                scene.Instances.Add(new SceneInstance { ElementId = mesh.ElementId, GeometryId = geometryId, Transform = Transform3d.Translate(origin) });
            }
            foreach (var placement in mesh.Placements)
            {
                placements++;
                var t = placement.Transform;
                var transform = new Transform3d(Vector(t.BasisX), Vector(t.BasisY), Vector(t.BasisZ), Vector(t.Origin) * MetresPerFoot);
                foreach (var (geometryId, materialId) in symbols[placement.SymbolId])
                    scene.Instances.Add(new SceneInstance { ElementId = mesh.ElementId, GeometryId = geometryId, Transform = transform, MaterialId = materialId });
            }
            if (mesh.Meshes.Count > 0)
            {
                var origin = LowestCorner(mesh.Meshes);
                var transform = Transform3d.Translate(Vector(origin) * MetresPerFoot);
                foreach (var own in mesh.Meshes)
                    scene.Instances.Add(new SceneInstance
                    {
                        ElementId = mesh.ElementId, GeometryId = AddGeometry(scene, own, origin, scratch), Transform = transform, MaterialId = MaterialOf(own.MaterialId),
                    });
            }
            if (scene.Instances.Count > instancesBefore) withGeometry++;

            coverage[mesh.Coverage] = coverage.GetValueOrDefault(mesh.Coverage) + 1;
            if (CoverageDiagnostic(mesh, sceneElement.SourceId) is { } diagnostic) report.Add(diagnostic);
        }
        Dictionary<long, SceneElement>? sceneElements = null;
        foreach (var mesh in meshes.Elements)
        {
            if (mesh.ComponentIds.Count == 0) continue;
            sceneElements ??= scene.Elements.ToDictionary(element => element.Id);
            foreach (int component in mesh.ComponentIds)
                if (sceneElements.TryGetValue(component, out var element))
                    element.ParentId = mesh.ElementId;
        }
        if (withoutUniqueId > 0)
            report.Add(new(SceneSeverity.Warning, "no-source-id",
                $"{withoutUniqueId} elements have no Revit UniqueId: their SourceId is empty and they cannot be identified outside this scene."));

        return new RvtSceneResult
        {
            Document = scene,
            Diagnostics = report,
            ElementCount = scene.Elements.Count,
            ElementsWithGeometry = withGeometry,
            HiddenDatumIds = meshes.Elements.Where(mesh => mesh.IsDatum).Select(mesh => (long)mesh.ElementId).ToArray(),
            Coverage = coverage,
            GeometryCount = scene.Geometries.Count,
            InstanceCount = scene.Instances.Count,
            PlacementCount = placements,
        };
    }

    private static void AddGrids(SceneDocument scene, RvtDocument document)
    {
        const double GridChordErrorMetres = 0.001;
        var grid = new SceneGridSystem { ModelId = ModelId };
        foreach (var axis in document.GetGridAxes())
        {
            if (axis.Segments is [{ CurveType: "Line" } line])
            {
                if (ReadPoint(line.StartPoint) is not { } start || ReadPoint(line.EndPoint) is not { } end || start == end)
                    continue;
                grid.Lines.Add(new SceneGridLine { Name = axis.Name, Start = start, End = end, Elevation = start.Z });
                continue;
            }

            if (axis.Tessellate(GridChordErrorMetres / MetresPerFoot) is not { } samples) continue;
            var points = new List<Vector3d>(samples.Count);
            foreach (var sample in samples)
            {
                if (ReadPoint(sample) is not { } point) break;
                points.Add(point);
            }
            if (points.Count == samples.Count)
                (grid.Paths ??= []).Add(new SceneGridPath { Name = axis.Name, Points = points });
        }

        if (grid.Lines.Count > 0 || grid.Paths is { Count: > 0 }) scene.Grids.Add(grid);

        static Vector3d? ReadPoint(double[] point) =>
            point is [var x, var y, var z] && double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z)
                ? new Vector3d(x, y, z) * MetresPerFoot : null;
    }

    /// <summary>"RvtIO.Scene" and the version of this assembly without the source revision.</summary>
    private static string Producer { get; } = "RvtIO.Scene " + (typeof(RvtSceneReader).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(RvtSceneReader).Assembly.GetName().Version?.ToString(3));

    private static SceneDiagnostic? CoverageDiagnostic(ElementMesh mesh, string? sourceId)
    {
        var (code, text) = mesh.Coverage switch
        {
            MeshCoverage.Partial => ("partial-geometry",
                $"may miss parts of its geometry ({mesh.UntriangulatedFaces} faces not triangulated; a placed symbol or a Geometry node may have no decoded polygon)"),
            MeshCoverage.BoxSubstitute => ("box-substitute", "places a rescaled symbol, which is drawn as its bounding box"),
            MeshCoverage.NoGeometry => ("no-geometry", "has a geometry record with nothing that could be drawn; it has no geometry in the scene"),
            MeshCoverage.Undecoded => ("undecoded", "has no geometry in the scene: the geometry of the file was not decoded"),
            _ => (null, null),
        };
        return code == null ? null : new SceneDiagnostic(SceneSeverity.Warning, code, $"Element {mesh.ElementId} {text}.", sourceId);
    }

    private static string CategoryName(Element? element) =>
        element?.CategoryId is int id && ((BuiltInCategory)id).TryGetCategoryName(out string? name) ? name : element?.Category ?? string.Empty;

    /// <summary>Adds the set of the parameters that have a value the file stores; null when none has.</summary>
    private static int? AddPropertySet(SceneDocument scene, string name, string key, IReadOnlyList<Parameter> parameters,
        Element? source = null)
    {
        var properties = new List<SceneProperty>();
        AddSourceId("Revit Element ID", source?.SourceElementId);
        AddSourceId("Revit Type ID", source?.SourceTypeId);
        AddSourceId("Revit Host ID", source?.SourceHostId);
        foreach (var parameter in parameters)
        {
            // An approximate or unset parameter is a reader estimate or placeholder, not a value stored in Revit.
            if (!parameter.HasRevitValue) continue;
            if (DisplayText(parameter) is not { } text) continue;
            properties.Add(new SceneProperty
            {
                Name = parameter.DefinitionName ?? parameter.Id.ToString(CultureInfo.InvariantCulture),
                Key = parameter.Id < 0 ? "revit:bip:" + parameter.Id.ToString(CultureInfo.InvariantCulture) : null,
                Value = SceneValue.FromText(text),
            });
        }
        if (properties.Count == 0) return null;
        int id = scene.PropertySets.Count + 1;
        scene.PropertySets.Add(new ScenePropertySet { Id = id, Name = name, Key = key, Properties = properties });
        return id;

        void AddSourceId(string label, string? id)
        {
            if (id != null)
                properties.Add(new SceneProperty { Name = label, Value = SceneValue.FromText(id) });
        }
    }

    /// <summary>The display string of the value as <see cref="Parameter.AsValueString()"/> gives it, with the invariant culture;
    /// null when the public API gives no display string that is exactly the one Revit shows.</summary>
    private static string? DisplayText(Parameter parameter) => parameter.StorageType switch
    {
        StorageType.String => parameter.Value as string,
        // The unit form of AsValueString, with a unit resolved from the parameter's measurement spec. Without a spec (every
        // built-in double) the reader guesses the unit from the parameter name, as for the factor TEXT_WIDTH_SCALE: left out.
        StorageType.Double when parameter.Value is double value && parameter.SpecType is { } spec && parameter.UnitSymbol is { } unit
            && ExactUnits.TryGetValue(unit, out double exact) && parameter.ConversionFactor == exact
            && !spec.Contains(":surfaceAreaPerUnitLength-", StringComparison.Ordinal)
            => string.Create(CultureInfo.InvariantCulture, $"{value * exact:G6} {unit}"),
        // A user parameter that stores an integer is an Integer or a Yes/No, both shown by AsValueString as the number. A
        // built-in one may be an enumeration, which the public API cannot tell from a number, so it is left out.
        StorageType.Integer when parameter.Id > 0 && parameter.Value is int value => value.ToString(CultureInfo.InvariantCulture),
        StorageType.ElementId => parameter.SourceElementId,
        // An ElementId: the public API does not give the name Revit shows for the referenced element.
        _ => null,
    };

    /// <summary>The display units whose factor converts Revit's internal unit (foot, square foot, cubic foot, radian, ampere) to
    /// them (the cubic one to ten digits, beyond the six the text shows). The reader gives other units (W, V, °C, Pa, ...) a
    /// factor of 1, which is not Revit's conversion, and gives an area per unit length the unit m²: such values are left out.</summary>
    private static readonly Dictionary<string, double> ExactUnits = new(StringComparer.Ordinal)
    {
        ["mm"] = 304.8, ["m²"] = 0.09290304, ["m³"] = 0.0283168466, ["°"] = 180.0 / Math.PI, ["A"] = 1.0,
    };

    private const string IfcGuidAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$";

    /// <summary>The IFC GlobalId Revit's IFC export gives the element: the value of its built-in IfcGUID parameter (IFC_GUID)
    /// when it is a valid GlobalId, else the export id of its UniqueId (the episode GUID with its last 64 bits XORed with the
    /// element id the UniqueId ends with) in the IFC base-64 form; null without either.</summary>
    private static string? IfcGuidOf(Element element)
    {
        if (element.ReadParameters.Where(parameter => parameter.Id == (int)BuiltInParameter.IFC_GUID)
            .Select(parameter => parameter.Value as string).FirstOrDefault(IsIfcGuid) is { } stored) return stored;

        // The original element id has a minimum width of eight hex digits.
        if (element.UniqueId is not { Length: >= 45 and <= 53 } uniqueId || uniqueId[36] != '-'
            || !Guid.TryParseExact(uniqueId.AsSpan(0, 36), "D", out var episode)
            || !long.TryParse(uniqueId.AsSpan(37), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long originalId)
            || originalId < 0)
            return null;
        UInt128 bits = BinaryPrimitives.ReadUInt128BigEndian(episode.ToByteArray(bigEndian: true)) ^ (ulong)originalId;
        var text = new char[22];
        for (int index = 21; index >= 0; index--, bits >>= 6) text[index] = IfcGuidAlphabet[(int)(bits & 63)];
        return new string(text);
    }

    // 22 characters of the IFC base-64 alphabet; the first one carries two bits, so it is 0 to 3.
    private static bool IsIfcGuid(string? value) =>
        value is { Length: 22 } && value[0] is >= '0' and <= '3' && value.All(c => IfcGuidAlphabet.Contains(c));

    private static SceneMaterial Material(RvtDocument document, int id)
    {
        var element = document.GetElement(id);
        var color = RgbaColor.Gray;
        if (element?.LookupParameter(BuiltInParameter.MATERIAL_PARAM_COLOR)?.Value is int packed)
        {
            int transparency = element.LookupParameter(BuiltInParameter.MATERIAL_PARAM_TRANSPARENCY)?.Value is int percent ? Math.Clamp(percent, 0, 100) : 0;
            // Red is the low byte, as the IFC export of RvtIO reads the parameter.
            color = new RgbaColor((byte)packed, (byte)(packed >> 8), (byte)(packed >> 16), (byte)Math.Round(255 * (100 - transparency) / 100.0));
        }
        return new SceneMaterial { Id = id, Name = element?.Name ?? $"Material {id}", Color = color };
    }

    /// <summary>Adds the mesh as a geometry in metres, its vertices relative to <paramref name="origin"/> (feet).</summary>
    private static int AddGeometry(SceneDocument scene, TriangleMesh mesh, MeshVector origin, SceneMeshScratch? scratch)
    {
        var vertices = new Vector3d[mesh.Positions.Length / 3];
        for (int vertex = 0; vertex < vertices.Length; vertex++)
            vertices[vertex] = new Vector3d(
                (mesh.Positions[3 * vertex] - origin.X) * MetresPerFoot,
                (mesh.Positions[3 * vertex + 1] - origin.Y) * MetresPerFoot,
                (mesh.Positions[3 * vertex + 2] - origin.Z) * MetresPerFoot);
        var normals = new Vector3d[mesh.Normals.Length / 3];
        for (int vertex = 0; vertex < normals.Length; vertex++)
            normals[vertex] = new Vector3d(mesh.Normals[3 * vertex], mesh.Normals[3 * vertex + 1], mesh.Normals[3 * vertex + 2]);
        int id = scene.Geometries.Count + 1;
        scene.Geometries.Add(new SceneGeometry
        {
            Id = id, Topology = SceneTopology.Triangles,
            Vertices = scratch == null ? vertices : scratch.Store(vertices),
            Indices = scratch == null ? mesh.Indices : scratch.Store(mesh.Indices),
            Normals = scratch == null ? normals : scratch.Store(normals),
        });
        return id;
    }

    /// <summary>The lowest x, y and z of the vertices of <paramref name="meshes"/>, in feet.</summary>
    private static MeshVector LowestCorner(IReadOnlyList<TriangleMesh> meshes)
    {
        double x = double.PositiveInfinity, y = double.PositiveInfinity, z = double.PositiveInfinity;
        foreach (var mesh in meshes)
            for (int index = 0; index + 2 < mesh.Positions.Length; index += 3)
            {
                x = Math.Min(x, mesh.Positions[index]);
                y = Math.Min(y, mesh.Positions[index + 1]);
                z = Math.Min(z, mesh.Positions[index + 2]);
            }
        return double.IsFinite(x) ? new MeshVector(x, y, z) : default;
    }

    private static Vector3d Vector(MeshVector vector) => new(vector.X, vector.Y, vector.Z);
}
