using System.Buffers.Binary;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RvtIO.Gltf;

internal sealed class GltfDocumentModel
{
    [JsonPropertyName("asset")]
    public required GltfAsset Asset { get; init; }

    [JsonPropertyName("scene")]
    public int? Scene { get; init; }

    [JsonPropertyName("scenes")]
    public List<GltfScene>? Scenes { get; init; }

    [JsonPropertyName("nodes")]
    public List<GltfNode>? Nodes { get; init; }

    [JsonPropertyName("meshes")]
    public List<GltfMesh>? Meshes { get; init; }

    [JsonPropertyName("materials")]
    public List<GltfMaterial>? Materials { get; init; }

    [JsonPropertyName("buffers")]
    public List<GltfBuffer>? Buffers { get; init; }

    [JsonPropertyName("bufferViews")]
    public List<GltfBufferView>? BufferViews { get; init; }

    [JsonPropertyName("accessors")]
    public List<GltfAccessor>? Accessors { get; init; }
}

internal sealed class GltfAsset
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = "2.0";

    [JsonPropertyName("generator")]
    public string? Generator { get; init; }
}

internal sealed class GltfScene
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("nodes")]
    public required List<int> Nodes { get; init; }
}

internal sealed class GltfNode
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("mesh")]
    public int? Mesh { get; init; }

    [JsonPropertyName("translation")]
    public float[]? Translation { get; init; }

    [JsonPropertyName("matrix")]
    public float[]? Matrix { get; init; }

    [JsonPropertyName("children")]
    public List<int>? Children { get; init; }

    [JsonPropertyName("extras")]
    public GltfNodeExtras? Extras { get; init; }
}

internal sealed class GltfNodeExtras
{
    [JsonPropertyName("elementId")]
    public int ElementId { get; init; }

    [JsonPropertyName("uniqueId")]
    public string? UniqueId { get; init; }

    [JsonPropertyName("ifcGuid")]
    public string? IfcGuid { get; init; }

    [JsonPropertyName("category")]
    public string? Category { get; init; }

    [JsonPropertyName("categoryId")]
    public int? CategoryId { get; init; }

    [JsonPropertyName("typeId")]
    public int? TypeId { get; init; }

    [JsonPropertyName("typeName")]
    public string? TypeName { get; init; }

    [JsonPropertyName("materialIds")]
    public int[]? MaterialIds { get; init; }

    [JsonPropertyName("symbolId")]
    public int? SymbolId { get; init; }

    [JsonPropertyName("coverage")]
    public string? Coverage { get; init; }
}

internal sealed class GltfMesh
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("primitives")]
    public required List<GltfPrimitive> Primitives { get; init; }
}

internal sealed class GltfPrimitive
{
    [JsonPropertyName("attributes")]
    public required Dictionary<string, int> Attributes { get; init; }

    [JsonPropertyName("indices")]
    public int? Indices { get; init; }

    [JsonPropertyName("material")]
    public int? Material { get; init; }

    [JsonPropertyName("mode")]
    public int? Mode { get; init; }
}

internal sealed class GltfMaterial
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("pbrMetallicRoughness")]
    public required GltfPbrMetallicRoughness PbrMetallicRoughness { get; init; }

    [JsonPropertyName("alphaMode")]
    public string? AlphaMode { get; init; }

    [JsonPropertyName("doubleSided")]
    public bool? DoubleSided { get; init; }

    [JsonPropertyName("extras")]
    public GltfMaterialExtras? Extras { get; init; }
}

internal sealed class GltfPbrMetallicRoughness
{
    [JsonPropertyName("baseColorFactor")]
    public required float[] BaseColorFactor { get; init; }

    [JsonPropertyName("metallicFactor")]
    public float MetallicFactor { get; init; }

    [JsonPropertyName("roughnessFactor")]
    public float RoughnessFactor { get; init; }
}

internal sealed class GltfMaterialExtras
{
    [JsonPropertyName("materialId")]
    public int? MaterialId { get; init; }

    [JsonPropertyName("uniqueId")]
    public string? UniqueId { get; init; }

    [JsonPropertyName("colorSource")]
    public required string ColorSource { get; init; }

    [JsonPropertyName("alphaSource")]
    public required string AlphaSource { get; init; }

    [JsonPropertyName("transparencyPercent")]
    public int? TransparencyPercent { get; init; }
}

internal sealed class GltfBuffer
{
    [JsonPropertyName("byteLength")]
    public int ByteLength { get; init; }
}

internal sealed class GltfBufferView
{
    [JsonPropertyName("buffer")]
    public int Buffer { get; init; }

    [JsonPropertyName("byteOffset")]
    public int ByteOffset { get; init; }

    [JsonPropertyName("byteLength")]
    public int ByteLength { get; init; }

    [JsonPropertyName("target")]
    public int Target { get; init; }
}

internal sealed class GltfAccessor
{
    [JsonPropertyName("bufferView")]
    public int BufferView { get; init; }

    [JsonPropertyName("byteOffset")]
    public int? ByteOffset { get; init; }

    [JsonPropertyName("componentType")]
    public int ComponentType { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("min")]
    public float[]? Min { get; init; }

    [JsonPropertyName("max")]
    public float[]? Max { get; init; }
}

internal static class GlbBinaryWriter
{
    private const uint GlbMagic = 0x46546C67;       // "glTF"
    private const uint GlbVersion = 2;
    private const uint JsonChunkType = 0x4E4F534A;  // "JSON"
    private const uint BinChunkType = 0x004E4942;   // "BIN\0"

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Write(GltfDocumentModel document, ReadOnlySpan<byte> binPayload, Stream output)
    {
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        int jsonPad = (4 - (jsonBytes.Length & 3)) & 3;
        int jsonChunkLength = jsonBytes.Length + jsonPad;

        int binPad = binPayload.Length > 0 ? (4 - (binPayload.Length & 3)) & 3 : 0;
        int binChunkLength = binPayload.Length + binPad;

        long totalLength = 12L + 8L + jsonChunkLength + (binChunkLength > 0 ? 8L + binChunkLength : 0L);
        if (totalLength > uint.MaxValue)
            throw new InvalidOperationException("The GLB output exceeds the 4 GiB binary glTF 2.0 size limit.");

        Span<byte> header = stackalloc byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..4], GlbMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], GlbVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], (uint)totalLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..16], (uint)jsonChunkLength);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..20], JsonChunkType);
        output.Write(header);
        output.Write(jsonBytes);
        if (jsonPad > 0)
        {
            Span<byte> spaces = [(byte)' ', (byte)' ', (byte)' '];
            output.Write(spaces[..jsonPad]);
        }

        if (binChunkLength > 0)
        {
            Span<byte> binHeader = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(binHeader[0..4], (uint)binChunkLength);
            BinaryPrimitives.WriteUInt32LittleEndian(binHeader[4..8], BinChunkType);
            output.Write(binHeader);
            output.Write(binPayload);
            if (binPad > 0)
            {
                Span<byte> zeros = [0, 0, 0];
                output.Write(zeros[..binPad]);
            }
        }
    }
}
