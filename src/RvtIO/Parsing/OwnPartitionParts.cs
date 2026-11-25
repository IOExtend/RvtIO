using System.Buffers.Binary;
using System.Security.Cryptography;

namespace RvtIO.Parsing;

/// <summary>Saved format-9 deposits for walls present only in the initial null-key map.
/// This does not replace the legacy walker or admit geometry from a partial parse.</summary>
internal static class OwnPartitionParts
{
    internal sealed record WallParts(byte[] NodeSha256, byte[] GeometrySha256);

    internal static bool MatchesFrame(byte[] data, int start, uint size, byte[] digest)
        => start >= 0 && size <= int.MaxValue - 16 && (long)start + size + 16 <= data.Length
            && SHA256.HashData(data.AsSpan(start, (int)size + 16)).AsSpan().SequenceEqual(digest);

    /// <summary>The marker and root classes of the Revit 2022 schema, the only release the saved wall deposits were measured on.</summary>
    internal static readonly Classes Revit2022 = new(new PartitionFraming.Tags(817, 816, 3561, 3554, 3580), [3524, 361], 2007);

    /// <summary>The class ids this file's schema gives the framing markers, the wall node roots (SWall, ArcWall) and the wall
    /// geometry root (GElement); the numbers differ per release. <see cref="Revit2022"/> are the measured ones.</summary>
    internal sealed record Classes(PartitionFraming.Tags Tags, int[] NodeKinds, int GeometryKind);

    /// <summary>The classes of a file by NAME in its own schema, or null when the schema lacks one of them.</summary>
    internal static Classes? ClassesOf(FormatsSchema schema)
    {
        if (PartitionFraming.TagsOf(schema) is not { } tags) return null;
        if (schema.IdOf("SWall") is not int straight || schema.IdOf("ArcWall") is not int arc || schema.IdOf("GElement") is not int geometry) return null;
        return new Classes(tags, [straight, arc], geometry);
    }

    internal static Dictionary<uint, WallParts>? Read(byte[] raw, out string? failure) => Read(raw, Revit2022, 4, out failure);

    internal static Dictionary<uint, WallParts>? Read(byte[] raw, Classes classes, int idWidth, out string? failure)
        => Read(raw, null, classes, idWidth, out failure);

    internal static Dictionary<uint, WallParts>? Read(Stream raw, Classes classes, int idWidth, out string? failure)
        => Read(null, raw, classes, idWidth, out failure);

    private static Dictionary<uint, WallParts>? Read(byte[]? raw, Stream? stream, Classes classes, int idWidth, out string? failure)
    {
        var nodes = new Dictionary<uint, byte[]>();
        var geometry = new Dictionary<uint, byte[]>();
        var ambiguous = new HashSet<uint>();
        try
        {
            void Collect(PartitionFraming.Record record)
            {
                ulong terminator = idWidth == 8 ? ulong.MaxValue : uint.MaxValue;
                if (record.Owner == terminator) return;
                if (record.Owner > uint.MaxValue) throw new InvalidDataException("record-owner");
                uint owner = (uint)record.Owner;
                var body = record.Body.Span;
                if (body.Length < 2) throw new InvalidDataException("record-class");
                if (record.Block != 0)
                {
                    if (nodes.ContainsKey(owner)) ambiguous.Add(owner);
                    return;
                }
                ushort kind = BinaryPrimitives.ReadUInt16LittleEndian(body);
                var selected = record.Channel == 102 ? nodes : record.Channel == 103 ? geometry : null;
                if (selected == null) return;
                bool wanted = record.Channel == 102 ? classes.NodeKinds.Contains(kind) : kind == classes.GeometryKind;
                // A repeated deposit replaces the previous one, including a different root class.
                if (wanted) selected[owner] = SHA256.HashData(record.Item.Span);
                else selected.Remove(owner);
            }
            bool walked = stream == null
                ? PartitionFraming.Walk(raw!, classes.Tags, idWidth, Collect, out failure, out _)
                : PartitionFraming.WalkStream(stream, classes.Tags, idWidth, Collect, out failure, out _);
            if (!walked) return null;
        }
        catch (InvalidDataException exception)
        {
            failure = exception.Message;
            return null;
        }
        return nodes.Where(pair => !ambiguous.Contains(pair.Key) && geometry.ContainsKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => new WallParts(pair.Value, geometry[pair.Key]));
    }
}
