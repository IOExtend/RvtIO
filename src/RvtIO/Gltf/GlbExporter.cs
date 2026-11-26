using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using RvtIO.BuiltIns;
using RvtIO.Ifc;
using RvtIO.Internal;
using RvtIO.Meshes;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Gltf;

/// <summary>
/// Exports decoded Revit geometry directly to binary glTF 2.0 (.glb) using triangle meshes
/// (<see cref="ElementMeshes"/>), converting coordinates from Revit Z-up feet into glTF Y-up metres, reusing shared family
/// symbol meshes and materials, and attaching element and material metadata in glTF <c>extras</c>.
/// </summary>
/// <remarks>A standalone family with no exportable triangles is refused with <see cref="NotSupportedException"/>.</remarks>
public static class GlbExporter
{
    internal sealed class FamilyGeometryUnavailableException()
        : NotSupportedException("The standalone Revit family has no decoded triangle geometry to export to GLB.");

    private const double MetresPerFoot = 0.3048;
    private const int ArrayBufferTarget = 34962;
    private const int ElementArrayBufferTarget = 34963;
    private const int ComponentTypeUnsignedShort = 5123;
    private const int ComponentTypeUnsignedInt = 5125;
    private const int ComponentTypeFloat = 5126;

    private static string DefaultGenerator { get; } = "RvtIO " + (typeof(GlbExporter).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(GlbExporter).Assembly.GetName().Version?.ToString(3)
        ?? "0.4.0");

    /// <summary>Reads the Revit file at <paramref name="rvtPath"/> once with geometry decoding enabled and writes a binary
    /// glTF 2.0 (.glb) file to <paramref name="glbPath"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="rvtPath"/> or <paramref name="glbPath"/> is null or empty, or both paths name the same file.</exception>
    public static GlbExportResult Export(string rvtPath, string glbPath, GlbExportOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(rvtPath);
        ArgumentException.ThrowIfNullOrEmpty(glbPath);
        if (FileIdentity.SameFile(glbPath, rvtPath))
            throw new ArgumentException($"The output path '{glbPath}' must not name the input file.", nameof(glbPath));

        var document = OpenDocument(rvtPath, options);
        return Export(document, glbPath, options);
    }

    /// <summary>Reads the Revit file at <paramref name="rvtPath"/> once with geometry decoding enabled and writes binary
    /// glTF 2.0 (.glb) data to <paramref name="output"/> (leaving <paramref name="output"/> open).</summary>
    public static GlbExportResult Export(string rvtPath, Stream output, GlbExportOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(rvtPath);
        ArgumentNullException.ThrowIfNull(output);

        var document = OpenDocument(rvtPath, options);
        return Export(document, output, options);
    }

    /// <summary>Extracts triangle meshes from an opened <paramref name="document"/> and writes a binary glTF 2.0 (.glb)
    /// file to <paramref name="glbPath"/>.</summary>
    public static GlbExportResult Export(RvtDocument document, string glbPath, GlbExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(glbPath);
        var meshes = ExtractMeshes(document, options);
        return Export(document, meshes, glbPath, options);
    }

    /// <summary>Extracts triangle meshes from an opened <paramref name="document"/> and writes binary glTF 2.0 (.glb) data
    /// to <paramref name="output"/> (leaving <paramref name="output"/> open).</summary>
    public static GlbExportResult Export(RvtDocument document, Stream output, GlbExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);
        var meshes = ExtractMeshes(document, options);
        return Export(document, meshes, output, options);
    }

    /// <summary>Writes a binary glTF 2.0 (.glb) file to <paramref name="glbPath"/> from an opened <paramref name="document"/>
    /// and pre-extracted <paramref name="meshes"/>, replacing any existing target atomically.</summary>
    public static GlbExportResult Export(RvtDocument document, MeshSet meshes, string glbPath, GlbExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(meshes);
        ArgumentException.ThrowIfNullOrEmpty(glbPath);

        string fullPath = Path.GetFullPath(glbPath);
        if (document.SourcePath is { } sourcePath && FileIdentity.SameFile(fullPath, sourcePath))
            throw new ArgumentException($"The output path '{glbPath}' must not name the input file.", nameof(glbPath));

        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            GlbExportResult result;
            using (var fs = File.Create(temporary))
                result = Export(document, meshes, fs, options);
            File.Move(temporary, fullPath, overwrite: true);
            return result;
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    /// <summary>Writes binary glTF 2.0 (.glb) data to <paramref name="output"/> (leaving <paramref name="output"/> open)
    /// from an opened <paramref name="document"/> and pre-extracted <paramref name="meshes"/>.</summary>
    public static GlbExportResult Export(RvtDocument document, MeshSet meshes, Stream output, GlbExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(meshes);
        ArgumentNullException.ThrowIfNull(output);

        var builder = new Builder(document, meshes, options ?? new GlbExportOptions());
        return builder.BuildAndWrite(output);
    }

    /// <summary>Exports an opened <paramref name="document"/> (optionally reusing pre-extracted <paramref name="meshes"/>)
    /// to an in-memory binary glTF 2.0 (.glb) byte array.</summary>
    public static byte[] ExportToBytes(RvtDocument document, MeshSet? meshes = null, GlbExportOptions? options = null)
        => ExportToBytes(document, out _, meshes, options);

    /// <summary>Exports an opened <paramref name="document"/> (optionally reusing pre-extracted <paramref name="meshes"/>)
    /// to an in-memory binary glTF 2.0 (.glb) byte array and outputs summary metrics.</summary>
    public static byte[] ExportToBytes(RvtDocument document, out GlbExportResult result, MeshSet? meshes = null, GlbExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var resolvedMeshes = meshes ?? ExtractMeshes(document, options);
        using var ms = new MemoryStream();
        result = Export(document, resolvedMeshes, ms, options);
        return ms.ToArray();
    }

    private static RvtDocument OpenDocument(string rvtPath, GlbExportOptions? options) =>
        RvtDocument.Open(rvtPath, new RvtReaderOptions
        {
            R22Layout = true,
            SystemFamilyNameLocale = "ENU",
            SeparateFacesWithDistinctOpenings = true,
            DiagnosticOutput = options?.DiagnosticOutput,
        });

    private static MeshSet ExtractMeshes(RvtDocument document, GlbExportOptions? options) =>
        ElementMeshes.Extract(document, new ElementMeshOptions
        {
            UseSymbolPlacements = options?.UseSymbolPlacements ?? true,
        });

    private sealed class Builder
    {
        private readonly RvtDocument _document;
        private readonly MeshSet _meshes;
        private readonly GlbExportOptions _options;
        private readonly TextWriter? _diag;

        private readonly MemoryStream _bin = new();
        private readonly List<GltfBufferView> _bufferViews = [];
        private readonly List<GltfAccessor> _accessors = [];
        private readonly List<GltfMaterial> _materials = [];
        private readonly Dictionary<int, int> _materialIndexById = [];
        private readonly List<GltfMesh> _glMeshes = [];
        private readonly Dictionary<int, (int MeshIndex, int[] MaterialIds)?> _symbolMeshes = [];
        private readonly List<GltfNode> _nodes = [];
        private readonly List<int> _sceneRootNodes = [];
        private readonly Dictionary<MeshCoverage, int> _coverageCounts = [];
        private readonly List<ElementGeometryQuality>? _reportElements;

        private int _elementCount;
        private int _elementsWithGeometry;
        private int _sharedSymbolMeshCount;
        private int _placementCount;

        public Builder(RvtDocument document, MeshSet meshes, GlbExportOptions options)
        {
            _document = document;
            _meshes = meshes;
            _options = options;
            _diag = options.DiagnosticOutput;
            if (options.CollectExportReport)
                _reportElements = new List<ElementGeometryQuality>(meshes.Elements.Count);
        }

        public GlbExportResult BuildAndWrite(Stream output)
        {
            if (_meshes.GeometryUndecoded)
                _diag?.WriteLine("warning: document geometry was not decoded (open with R22Layout = true); exporting element nodes without geometry.");

            foreach (var elemMesh in _meshes.Elements)
            {
                _coverageCounts[elemMesh.Coverage] = _coverageCounts.GetValueOrDefault(elemMesh.Coverage) + 1;
                ProcessElement(elemMesh);
            }

            if (_document.RawData.IsStandaloneFamily && _elementsWithGeometry == 0)
                throw new FamilyGeometryUnavailableException();

            if (_bin.Length > 0)
                PadBinTo4();

            var doc = new GltfDocumentModel
            {
                Asset = new GltfAsset
                {
                    Version = "2.0",
                    Generator = string.IsNullOrWhiteSpace(_options.Generator) ? DefaultGenerator : _options.Generator,
                },
                Scene = _sceneRootNodes.Count > 0 ? 0 : null,
                Scenes = _sceneRootNodes.Count > 0 ? [new GltfScene { Nodes = _sceneRootNodes }] : null,
                Nodes = _nodes.Count > 0 ? _nodes : null,
                Meshes = _glMeshes.Count > 0 ? _glMeshes : null,
                Materials = _materials.Count > 0 ? _materials : null,
                Buffers = _bin.Length > 0 ? [new GltfBuffer { ByteLength = checked((int)_bin.Length) }] : null,
                BufferViews = _bufferViews.Count > 0 ? _bufferViews : null,
                Accessors = _accessors.Count > 0 ? _accessors : null,
            };

            ReadOnlySpan<byte> payload = _bin.Length > 0 ? _bin.GetBuffer().AsSpan(0, checked((int)_bin.Length)) : [];
            GlbBinaryWriter.Write(doc, payload, output);

            return new GlbExportResult
            {
                ElementCount = _elementCount,
                ElementsWithGeometry = _elementsWithGeometry,
                NodeCount = _nodes.Count,
                MeshCount = _glMeshes.Count,
                SharedSymbolMeshCount = _sharedSymbolMeshCount,
                PlacementCount = _placementCount,
                MaterialCount = _materials.Count,
                GeometryUndecoded = _meshes.GeometryUndecoded,
                Coverage = _coverageCounts,
                ReportElements = _reportElements,
            };
        }

        private void ProcessElement(ElementMesh elemMesh)
        {
            var element = _document.GetElement(elemMesh.ElementId);
            string? elementName = string.IsNullOrEmpty(element?.Name) ? null : element.Name;
            string? category = CategoryName(element);
            int? categoryId = element?.CategoryId is > int.MinValue and int cid && cid != 0 ? cid : null;
            int? typeId = element?.TypeId is > 0 and int tid ? tid : null;
            string? typeName = element != null ? NullIfEmpty(_document.GetTypeElement(element)?.Name) : null;
            string? uniqueId = NullIfEmpty(element?.UniqueId);
            string? ifcGuid = element != null ? IfcGuidOf(element) : null;
            string coverage = elemMesh.Coverage.ToString();

            // Resolve placements (shared family symbol meshes).
            var resolvedPlacements = new List<(int MeshIndex, int SymbolId, int[] MaterialIds, float[]? Translation, float[]? Matrix)>();
            foreach (var placement in elemMesh.Placements)
            {
                if (!TryConvertTransform(placement.Transform, out float[]? translation, out float[]? matrix))
                {
                    _diag?.WriteLine($"warning: element {elemMesh.ElementId} placement of symbol {placement.SymbolId} has an invalid or degenerate transform and was skipped.");
                    continue;
                }
                if (GetOrBuildSymbolMesh(placement.SymbolId) is not { } symbol)
                    continue;
                resolvedPlacements.Add((symbol.MeshIndex, placement.SymbolId, symbol.MaterialIds, translation, matrix));
            }

            // Resolve element's own meshes (vertices relative to LowestCorner in metres Y-up).
            int? ownMeshIndex = null;
            int[] ownMaterialIds = [];
            float[]? ownTranslation = null;
            if (elemMesh.Meshes.Count > 0)
            {
                var originFeet = LowestCorner(elemMesh.Meshes);
                var primitives = new List<GltfPrimitive>();
                var matIds = new SortedSet<int>();
                foreach (var triMesh in elemMesh.Meshes)
                {
                    if (PushTriangleMesh(triMesh, originFeet, out var primitive, out int positiveMaterialId))
                    {
                        primitives.Add(primitive);
                        if (positiveMaterialId > 0) matIds.Add(positiveMaterialId);
                    }
                }
                if (primitives.Count > 0)
                {
                    ownMeshIndex = _glMeshes.Count;
                    _glMeshes.Add(new GltfMesh { Name = elementName, Primitives = primitives });
                    ownMaterialIds = [.. matIds];
                    ownTranslation = ToGltfTranslation(originFeet);
                }
            }

            bool hasAnyGeometry = resolvedPlacements.Count > 0 || ownMeshIndex.HasValue;
            if (!hasAnyGeometry && !_options.IncludeElementsWithoutGeometry)
                return;

            _elementCount++;
            if (hasAnyGeometry) _elementsWithGeometry++;
            _placementCount += resolvedPlacements.Count;
            if (_reportElements != null)
            {
                int ownPrimitiveCount = ownMeshIndex is int meshIndex ? _glMeshes[meshIndex].Primitives.Count : 0;
                bool lostGeometry = ownPrimitiveCount < elemMesh.Meshes.Count || resolvedPlacements.Count < elemMesh.Placements.Count
                    || resolvedPlacements.Any(p => _glMeshes[p.MeshIndex].Primitives.Count < _meshes.Symbols[p.SymbolId].Count);
                var quality = !hasAnyGeometry ? GeometryQualityClass.None
                    : elemMesh.Coverage == MeshCoverage.BoxSubstitute ? GeometryQualityClass.BoxOnly
                    : lostGeometry || elemMesh.Coverage != MeshCoverage.Complete ? GeometryQualityClass.Approximated
                    : GeometryQualityClass.Exact;
                _reportElements.Add(new ElementGeometryQuality(elemMesh.ElementId,
                    element == null ? "unknown" : SkipLog.ClassOf(element), quality));
            }

            GltfNodeExtras MakeExtras(int[]? matIds, int? symbolId) => new()
            {
                ElementId = elemMesh.ElementId,
                UniqueId = uniqueId,
                IfcGuid = ifcGuid,
                Category = NullIfEmpty(category),
                CategoryId = categoryId,
                TypeId = typeId,
                TypeName = typeName,
                MaterialIds = matIds is { Length: > 0 } ? matIds : null,
                SymbolId = symbolId,
                Coverage = coverage,
            };

            // Single placement with no own meshes: emit one compact element node directly referencing the shared symbol mesh.
            if (resolvedPlacements.Count == 1 && !ownMeshIndex.HasValue)
            {
                var p = resolvedPlacements[0];
                int nodeIndex = _nodes.Count;
                _nodes.Add(new GltfNode
                {
                    Name = elementName,
                    Mesh = p.MeshIndex,
                    Translation = p.Translation,
                    Matrix = p.Matrix,
                    Extras = MakeExtras(p.MaterialIds, p.SymbolId),
                });
                _sceneRootNodes.Add(nodeIndex);
                return;
            }

            // Own meshes only or geometry-less element: emit one element node.
            if (resolvedPlacements.Count == 0)
            {
                int nodeIndex = _nodes.Count;
                _nodes.Add(new GltfNode
                {
                    Name = elementName,
                    Mesh = ownMeshIndex,
                    Translation = ownMeshIndex.HasValue ? ownTranslation : null,
                    Extras = MakeExtras(ownMaterialIds, null),
                });
                _sceneRootNodes.Add(nodeIndex);
                return;
            }

            // Multiple placements (or combined own + placements): emit an element parent node with placement/own children.
            var allMaterialIds = new SortedSet<int>(ownMaterialIds);
            foreach (var p in resolvedPlacements)
                foreach (int mid in p.MaterialIds)
                    allMaterialIds.Add(mid);

            var childIndices = new List<int>(resolvedPlacements.Count + (ownMeshIndex.HasValue ? 1 : 0));
            if (ownMeshIndex.HasValue)
            {
                int ownChildIndex = _nodes.Count;
                _nodes.Add(new GltfNode
                {
                    Name = elementName,
                    Mesh = ownMeshIndex,
                    Translation = ownTranslation,
                    Extras = MakeExtras(ownMaterialIds, null),
                });
                childIndices.Add(ownChildIndex);
            }

            foreach (var p in resolvedPlacements)
            {
                int childIndex = _nodes.Count;
                _nodes.Add(new GltfNode
                {
                    Name = elementName,
                    Mesh = p.MeshIndex,
                    Translation = p.Translation,
                    Matrix = p.Matrix,
                    Extras = MakeExtras(p.MaterialIds, p.SymbolId),
                });
                childIndices.Add(childIndex);
            }

            int parentIndex = _nodes.Count;
            _nodes.Add(new GltfNode
            {
                Name = elementName,
                Children = childIndices,
                Extras = MakeExtras([.. allMaterialIds], null),
            });
            _sceneRootNodes.Add(parentIndex);
        }

        private (int MeshIndex, int[] MaterialIds)? GetOrBuildSymbolMesh(int symbolId)
        {
            if (_symbolMeshes.TryGetValue(symbolId, out var cached))
                return cached;

            if (!_meshes.Symbols.TryGetValue(symbolId, out var symbolMeshes) || symbolMeshes.Count == 0)
            {
                _symbolMeshes[symbolId] = null;
                return null;
            }

            var primitives = new List<GltfPrimitive>(symbolMeshes.Count);
            var materialIds = new SortedSet<int>();
            foreach (var triMesh in symbolMeshes)
            {
                if (PushTriangleMesh(triMesh, default, out var primitive, out int positiveMaterialId))
                {
                    primitives.Add(primitive);
                    if (positiveMaterialId > 0) materialIds.Add(positiveMaterialId);
                }
            }

            if (primitives.Count == 0)
            {
                _symbolMeshes[symbolId] = null;
                return null;
            }

            string? symbolName = NullIfEmpty(_document.GetElement(symbolId)?.Name);
            int meshIndex = _glMeshes.Count;
            _glMeshes.Add(new GltfMesh
            {
                Name = symbolName ?? $"Symbol {symbolId.ToString(CultureInfo.InvariantCulture)}",
                Primitives = primitives,
            });
            _sharedSymbolMeshCount++;

            var built = (meshIndex, materialIds.ToArray());
            _symbolMeshes[symbolId] = built;
            return built;
        }

        private bool PushTriangleMesh(TriangleMesh mesh, MeshVector originFeet, out GltfPrimitive primitive, out int positiveMaterialId)
        {
            primitive = null!;
            positiveMaterialId = mesh.MaterialId > 0 ? mesh.MaterialId : 0;

            int vertexCount = mesh.Positions.Length / 3;
            if (vertexCount <= 0 || mesh.Positions.Length % 3 != 0 || mesh.Indices.Length < 3 || mesh.Indices.Length % 3 != 0)
                return false;

            float[] positions = new float[vertexCount * 3];
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity, minZ = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity, maxZ = float.NegativeInfinity;

            for (int v = 0; v < vertexCount; v++)
            {
                double rx = (mesh.Positions[3 * v] - originFeet.X) * MetresPerFoot;
                double ry = (mesh.Positions[3 * v + 1] - originFeet.Y) * MetresPerFoot;
                double rz = (mesh.Positions[3 * v + 2] - originFeet.Z) * MetresPerFoot;
                if (!double.IsFinite(rx) || !double.IsFinite(ry) || !double.IsFinite(rz))
                {
                    _diag?.WriteLine("warning: skipped triangle mesh with non-finite vertex coordinates.");
                    return false;
                }

                // Z-up (Revit) -> Y-up (glTF): (x, y, z) -> (x, z, -y)
                float gx = CleanZero((float)rx);
                float gy = CleanZero((float)rz);
                float gz = CleanZero((float)(-ry));
                if (!float.IsFinite(gx) || !float.IsFinite(gy) || !float.IsFinite(gz))
                    return false;

                positions[3 * v] = gx;
                positions[3 * v + 1] = gy;
                positions[3 * v + 2] = gz;

                if (gx < minX) minX = gx;
                if (gy < minY) minY = gy;
                if (gz < minZ) minZ = gz;
                if (gx > maxX) maxX = gx;
                if (gy > maxY) maxY = gy;
                if (gz > maxZ) maxZ = gz;
            }

            foreach (int idx in mesh.Indices)
            {
                if ((uint)idx >= (uint)vertexCount)
                {
                    _diag?.WriteLine("warning: skipped triangle mesh with out-of-range vertex index.");
                    return false;
                }
            }

            bool hasNormals = mesh.Normals.Length == mesh.Positions.Length;
            float[]? normals = hasNormals ? new float[vertexCount * 3] : null;
            if (normals != null)
            {
                for (int v = 0; v < vertexCount; v++)
                {
                    double nx = mesh.Normals[3 * v];
                    double ny = mesh.Normals[3 * v + 1];
                    double nz = mesh.Normals[3 * v + 2];
                    float gx = (float)nx;
                    float gy = (float)nz;
                    float gz = (float)(-ny);
                    float lenSq = gx * gx + gy * gy + gz * gz;
                    if (lenSq > 0f && float.IsFinite(lenSq))
                    {
                        float inv = 1f / MathF.Sqrt(lenSq);
                        normals[3 * v] = CleanZero(gx * inv);
                        normals[3 * v + 1] = CleanZero(gy * inv);
                        normals[3 * v + 2] = CleanZero(gz * inv);
                    }
                    else
                    {
                        normals[3 * v] = 0f;
                        normals[3 * v + 1] = 1f;
                        normals[3 * v + 2] = 0f;
                    }
                }
            }

            int posAccessor = WriteFloatVec3Accessor(positions, min: [minX, minY, minZ], max: [maxX, maxY, maxZ]);
            var attributes = new Dictionary<string, int>(2) { ["POSITION"] = posAccessor };
            if (normals != null)
                attributes["NORMAL"] = WriteFloatVec3Accessor(normals, min: null, max: null);

            int idxAccessor = WriteIndexAccessor(mesh.Indices, vertexCount);
            int materialIndex = GetOrCreateMaterial(positiveMaterialId);

            primitive = new GltfPrimitive
            {
                Attributes = attributes,
                Indices = idxAccessor,
                Material = materialIndex,
            };
            return true;
        }

        private int WriteFloatVec3Accessor(float[] values, float[]? min, float[]? max)
        {
            PadBinTo4();
            int offset = checked((int)_bin.Length);
            Span<byte> buf = stackalloc byte[4];
            foreach (float val in values)
            {
                BinaryPrimitives.WriteSingleLittleEndian(buf, val);
                _bin.Write(buf);
            }
            int byteLength = checked((int)_bin.Length) - offset;

            int viewIndex = _bufferViews.Count;
            _bufferViews.Add(new GltfBufferView
            {
                Buffer = 0,
                ByteOffset = offset,
                ByteLength = byteLength,
                Target = ArrayBufferTarget,
            });

            int accessorIndex = _accessors.Count;
            _accessors.Add(new GltfAccessor
            {
                BufferView = viewIndex,
                ComponentType = ComponentTypeFloat,
                Count = values.Length / 3,
                Type = "VEC3",
                Min = min,
                Max = max,
            });
            return accessorIndex;
        }

        private int WriteIndexAccessor(int[] indices, int vertexCount)
        {
            PadBinTo4();
            int offset = checked((int)_bin.Length);
            // 0xFFFF is disallowed as a vertex index in glTF UNSIGNED_SHORT (primitive restart sentinel).
            bool useU16 = vertexCount <= 65534;

            if (useU16)
            {
                Span<byte> buf = stackalloc byte[2];
                foreach (int idx in indices)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)idx);
                    _bin.Write(buf);
                }
            }
            else
            {
                Span<byte> buf = stackalloc byte[4];
                foreach (int idx in indices)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)idx);
                    _bin.Write(buf);
                }
            }

            int byteLength = checked((int)_bin.Length) - offset;
            PadBinTo4();

            int viewIndex = _bufferViews.Count;
            _bufferViews.Add(new GltfBufferView
            {
                Buffer = 0,
                ByteOffset = offset,
                ByteLength = byteLength,
                Target = ElementArrayBufferTarget,
            });

            int accessorIndex = _accessors.Count;
            _accessors.Add(new GltfAccessor
            {
                BufferView = viewIndex,
                ComponentType = useU16 ? ComponentTypeUnsignedShort : ComponentTypeUnsignedInt,
                Count = indices.Length,
                Type = "SCALAR",
            });
            return accessorIndex;
        }

        private void PadBinTo4()
        {
            int rem = (int)(_bin.Length & 3);
            if (rem == 0) return;
            for (int i = rem; i < 4; i++)
                _bin.WriteByte(0);
        }

        private int GetOrCreateMaterial(int materialId)
        {
            if (_materialIndexById.TryGetValue(materialId, out int existing))
                return existing;

            byte r = 180, g = 180, b = 180;
            string colorSource = "default";
            float alpha = 1f;
            string alphaSource = "default";
            int? transparencyPercent = null;
            string? name = "Default";
            string? uniqueId = null;

            if (materialId > 0)
            {
                var matElem = _document.GetElement(materialId);
                name = NullIfEmpty(matElem?.Name) ?? $"Material {materialId.ToString(CultureInfo.InvariantCulture)}";
                uniqueId = NullIfEmpty(matElem?.UniqueId);

                if (matElem?.LookupParameter(BuiltInParameter.MATERIAL_PARAM_COLOR)?.Value is int packed)
                {
                    r = (byte)packed;
                    g = (byte)(packed >> 8);
                    b = (byte)(packed >> 16);
                    colorSource = "file";
                }
                if (matElem?.LookupParameter(BuiltInParameter.MATERIAL_PARAM_TRANSPARENCY)?.Value is int rawTransparency)
                {
                    int clamped = Math.Clamp(rawTransparency, 0, 100);
                    transparencyPercent = clamped;
                    alpha = (100 - clamped) / 100f;
                    alphaSource = "file";
                }
            }

            float lr = SrgbToLinear(r);
            float lg = SrgbToLinear(g);
            float lb = SrgbToLinear(b);
            bool isTransparent = alpha < 1f;

            var gltfMat = new GltfMaterial
            {
                Name = name,
                PbrMetallicRoughness = new GltfPbrMetallicRoughness
                {
                    BaseColorFactor = [lr, lg, lb, alpha],
                    MetallicFactor = 0f,
                    RoughnessFactor = 0.8f,
                },
                AlphaMode = isTransparent ? "BLEND" : null,
                DoubleSided = isTransparent ? true : null,
                Extras = new GltfMaterialExtras
                {
                    MaterialId = materialId > 0 ? materialId : null,
                    UniqueId = uniqueId,
                    ColorSource = colorSource,
                    AlphaSource = alphaSource,
                    TransparencyPercent = transparencyPercent,
                },
            };

            int idx = _materials.Count;
            _materials.Add(gltfMat);
            _materialIndexById[materialId] = idx;
            return idx;
        }

        private static float SrgbToLinear(byte channel)
        {
            double s = channel / 255.0;
            double lin = s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            return CleanZero((float)lin);
        }

        private static bool TryConvertTransform(MeshTransform t, out float[]? translation, out float[]? matrix)
        {
            translation = null;
            matrix = null;

            if (!IsFinite(t.Origin) || !IsFinite(t.BasisX) || !IsFinite(t.BasisY) || !IsFinite(t.BasisZ))
                return false;

            double det = t.Determinant;
            if (!double.IsFinite(det) || Math.Abs(det) <= 1e-12)
                return false;

            // Z-up feet -> Y-up metres: C(x, y, z) = (x, z, -y), C^-1(x, y, z) = (x, -z, y)
            // Matrix M' = C * M * C^-1 has columns: [C(BasisX), C(BasisZ), -C(BasisY)]
            float c0x = CleanZero((float)t.BasisX.X), c0y = CleanZero((float)t.BasisX.Z), c0z = CleanZero((float)(-t.BasisX.Y));
            float c1x = CleanZero((float)t.BasisZ.X), c1y = CleanZero((float)t.BasisZ.Z), c1z = CleanZero((float)(-t.BasisZ.Y));
            float c2x = CleanZero((float)(-t.BasisY.X)), c2y = CleanZero((float)(-t.BasisY.Z)), c2z = CleanZero((float)t.BasisY.Y);

            float tx = CleanZero((float)(t.Origin.X * MetresPerFoot));
            float ty = CleanZero((float)(t.Origin.Z * MetresPerFoot));
            float tz = CleanZero((float)(-t.Origin.Y * MetresPerFoot));

            if (!float.IsFinite(c0x) || !float.IsFinite(c0y) || !float.IsFinite(c0z)
                || !float.IsFinite(c1x) || !float.IsFinite(c1y) || !float.IsFinite(c1z)
                || !float.IsFinite(c2x) || !float.IsFinite(c2y) || !float.IsFinite(c2z)
                || !float.IsFinite(tx) || !float.IsFinite(ty) || !float.IsFinite(tz))
                return false;

            bool isIdentityBasis =
                c0x == 1f && c0y == 0f && c0z == 0f &&
                c1x == 0f && c1y == 1f && c1z == 0f &&
                c2x == 0f && c2y == 0f && c2z == 1f;

            if (isIdentityBasis)
            {
                if (tx != 0f || ty != 0f || tz != 0f)
                    translation = [tx, ty, tz];
                return true;
            }

            matrix =
            [
                c0x, c0y, c0z, 0f,
                c1x, c1y, c1z, 0f,
                c2x, c2y, c2z, 0f,
                tx, ty, tz, 1f,
            ];
            return true;
        }

        private static float[]? ToGltfTranslation(MeshVector originFeet)
        {
            float tx = CleanZero((float)(originFeet.X * MetresPerFoot));
            float ty = CleanZero((float)(originFeet.Z * MetresPerFoot));
            float tz = CleanZero((float)(-originFeet.Y * MetresPerFoot));
            if (tx == 0f && ty == 0f && tz == 0f)
                return null;
            return [tx, ty, tz];
        }

        private static MeshVector LowestCorner(IReadOnlyList<TriangleMesh> meshes)
        {
            double x = double.PositiveInfinity, y = double.PositiveInfinity, z = double.PositiveInfinity;
            foreach (var mesh in meshes)
            {
                for (int i = 0; i + 2 < mesh.Positions.Length; i += 3)
                {
                    if (mesh.Positions[i] < x) x = mesh.Positions[i];
                    if (mesh.Positions[i + 1] < y) y = mesh.Positions[i + 1];
                    if (mesh.Positions[i + 2] < z) z = mesh.Positions[i + 2];
                }
            }
            return double.IsFinite(x) ? new MeshVector(x, y, z) : default;
        }

        private static string? CategoryName(Element? element) =>
            element?.CategoryId is int id && ((BuiltInCategory)id).TryGetCategoryName(out string? name) ? name : element?.Category;

        private static string? IfcGuidOf(Element element)
        {
            foreach (var parameter in element.Parameters)
            {
                if (parameter.Id == (int)BuiltInParameter.IFC_GUID
                    && parameter.Value is string stored
                    && IfcGuid.Parse(stored) != null)
                    return stored;
            }

            if (element.UniqueId is { Length: 45 } uniqueId && uniqueId[36] == '-'
                && Guid.TryParseExact(uniqueId.AsSpan(0, 36), "D", out var episode)
                && uint.TryParse(uniqueId.AsSpan(37), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint originalId))
            {
                return IfcGuid.ToIfcGuid(ElementExportIds.ExportId(episode, originalId));
            }
            return null;
        }

        private static bool IsFinite(MeshVector v) =>
            double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);

        private static float CleanZero(float value) => value == 0f ? 0f : value;

        private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
    }
}
