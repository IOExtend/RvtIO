using System.Collections;
using System.Diagnostics.CodeAnalysis;
using RvtIO.Parsing;

namespace RvtIO.Internal;

/// <summary>Keeps decoded geometry for resident reads and exact block references for scratch-backed reads.</summary>
internal sealed class GRepBlockStore : IEnumerable<KeyValuePair<uint, GRepData>>
{
    private readonly Dictionary<uint, Entry> entries = [];

    private readonly record struct Entry(GRepData? Resident, Replay? Stored)
    {
        internal List<GRepInstance> Instances => Resident?.Instances ?? Stored!.Instances;
        internal int FaceCount => Resident?.Faces.Count ?? Stored!.FaceCount;
        internal Rank Rank => Resident is { } geometry ? Rank.From(geometry) : Stored!.Rank;
        internal GRepData Read() => Resident ?? Stored!.Read();
    }

    private sealed class Replay(PartitionChunks.Block block, uint size, GRepData decoded, bool nativeWallBody = false)
    {
        internal readonly Rank Rank = Rank.From(decoded);
        internal readonly int FaceCount = decoded.Faces.Count;
        internal readonly List<GRepInstance> Instances = decoded.Instances;
        private int symbol = decoded.SymbolElemId;
        private double[]? transform = decoded.InstanceTransform;

        internal void MergeInstances(List<GRepInstance> source)
        {
            BlockParser.MergeGRepInstances(Instances, source);
            if (Instances.Count == 0) return;
            symbol = Instances[0].SymbolElemId;
            transform = Instances[0].Transform;
        }

        internal GRepData Read()
        {
            var (data, start) = block.Read();
            // This block has already contributed its diagnostics on the initial decode.
            using var silenceReplay = DiagnosticSink.Use(TextWriter.Null);
            var geometry = GRepParserV2.Parse(data, start, size, nativeWallBody: nativeWallBody)
                ?? throw new InvalidOperationException("A previously decoded geometry block could not be read from scratch.");
            geometry.Instances = Instances;
            geometry.SymbolElemId = symbol;
            geometry.InstanceTransform = transform;
            return geometry;
        }
    }

    internal readonly record struct Rank(int VertexCount, int FaceCount, bool Transformed, bool Angled, bool Mapped)
    {
        internal static Rank From(GRepData geometry) => new(
            geometry.Faces.Sum(face => face.Vertices?.Count ?? 0),
            geometry.Faces.Count,
            geometry.Transform != null,
            geometry.Faces.Any(face => Math.Abs(face.XVector[0]) > 0.01 && Math.Abs(face.XVector[1]) > 0.01),
            geometry.Instances.Count != 0);
    }

    internal GRepData this[uint id]
    {
        get => entries[id].Read();
        set => Set(id, value);
    }

    internal int Count => entries.Count;
    internal IEnumerable<uint> Keys => entries.Keys;
    internal IEnumerable<GRepData> Values => entries.Values.Select(entry => entry.Read());
    internal bool ContainsKey(uint id) => entries.ContainsKey(id);
    internal IReadOnlyList<GRepInstance> Instances(uint id) => entries[id].Instances;
    internal void Clear() => entries.Clear();

    internal void Set(uint id, GRepData value, PartitionChunks.Block? block = null, uint size = 0,
        bool nativeWallBody = false) =>
        entries[id] = block is { } stored ? new(null, new Replay(stored, size, value, nativeWallBody)) : new(value, null);

    internal bool TryAdd(uint id, GRepData value, PartitionChunks.Block? block, uint size) =>
        entries.TryAdd(id, block is { } stored ? new(null, new Replay(stored, size, value)) : new(value, null));

    internal bool TryGetValue(uint id, [NotNullWhen(true)] out GRepData? value)
    {
        value = entries.TryGetValue(id, out var entry) ? entry.Read() : null;
        return value != null;
    }

    internal bool TryGetRank(uint id, out Rank rank)
    {
        bool found = entries.TryGetValue(id, out var entry);
        rank = found ? entry.Rank : default;
        return found;
    }

    internal void MergeInstances(uint id, List<GRepInstance> source)
    {
        var entry = entries[id];
        if (entry.Resident is { } resident) BlockParser.MergeGRepInstances(resident, source);
        else entry.Stored!.MergeInstances(source);
    }

    internal IEnumerable<uint> ReferencedSymbols()
    {
        foreach (var entry in entries.Values)
            foreach (var instance in entry.Instances)
                if (instance.SymbolElemId > 0) yield return (uint)instance.SymbolElemId;
    }

    internal IEnumerable<uint> BlockOnlyCandidates(HashSet<uint> referencedSymbols)
    {
        foreach (var (id, entry) in entries)
            if (entry.FaceCount >= 4 || referencedSymbols.Contains(id)) yield return id;
    }

    public IEnumerator<KeyValuePair<uint, GRepData>> GetEnumerator()
    {
        foreach (var (id, entry) in entries) yield return new(id, entry.Read());
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
