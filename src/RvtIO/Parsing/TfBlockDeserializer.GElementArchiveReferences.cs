namespace RvtIO.Parsing;

internal static partial class TfBlockDeserializer
{
    internal sealed record GElementArchiveObjectRegistration(uint Key, int QueueIndex, ushort ClassId,
        uint RawHandle, int Start, int End);
    internal sealed record GElementArchiveReference(int SourceQueueIndex, int Declaring, string Name,
        int ItemIndex, uint Key, int Start, int End, int? TargetQueueIndex, ushort? TargetClassId, string Outcome);
    internal sealed record OwnGElementFreshArchiveReferences(int OwnerId, uint ExternalDocumentKey, uint RootKey,
        IReadOnlyList<GElementArchiveObjectRegistration> Registrations,
        IReadOnlyList<GElementArchiveReference> References, bool Complete);

    /// <summary>Serialized-key lookup (reference kind 3) in a fresh archive context with a selected non-null document.
    /// Positive owned pointer keys bind their own object records; 0xFFFFFFFF owners are untracked. Keys are used only
    /// within this one archive, never compared as tags or handles across archives. Every weak field, item, span and
    /// missing-key outcome is preserved; an incomplete resolution is not a null or absent geometry.</summary>
    internal static OwnGElementFreshArchiveReferences? SelectOwnGElementFreshArchiveReferences(
        OwnGElementGeometryGraph graph, out string? failure)
    {
        var analysis = AnalyzeGElement(graph).Archive.Value;
        failure = analysis.Failure;
        return analysis.Value;
    }

    private static OwnGElementFreshArchiveReferences? ReadOwnGElementFreshArchiveReferences(
        OwnGElementGeometryGraph graph, out string? failure)
    {
        failure = "archive-reference-graph";
        if (graph.OwnerId <= 0 || !HasGElementGraphSchemas()) return null;
        var roots = graph.Nodes.Where(n => n.QueueIndex == -1).ToArray();
        if (roots.Length != 1 || roots[0].ClassId != 2007 || roots[0].Handle != (uint)graph.OwnerId
            || graph.Nodes.Select(n => n.QueueIndex).Distinct().Count() != graph.Nodes.Count) return null;
        var root = roots[0];
        var registrations = new List<GElementArchiveObjectRegistration> { new(2, -1, 2007, root.Handle, root.Start, root.End) };
        var map = new Dictionary<uint, GElementArchiveObjectRegistration> { [2] = registrations[0] };
        failure = "archive-reference-registration";
        foreach (var node in graph.Nodes.Where(n => n.QueueIndex != -1))
        {
            if (node.QueueIndex < 0 || node.Handle == 0) return null;
            if (node.Handle == uint.MaxValue) continue; // native untracked-owner path, not an ID
            var registration = new GElementArchiveObjectRegistration(node.Handle, node.QueueIndex, node.ClassId, node.Handle, node.Start, node.End);
            if (node.Handle <= 2 || !map.TryAdd(node.Handle, registration)) return null;
            registrations.Add(registration);
        }
        var links = new List<GElementArchiveReference>();
        int contextSource = 0;
        GElementGraphField? contextField = null;
        try
        {
            foreach (var node in graph.Nodes) Walk(node.QueueIndex, node.Fields);
            failure = null;
            return new(graph.OwnerId, 1, 2, registrations, links, links.All(l => l.Outcome != "missing-key"));
        }
        catch (DecodeFail)
        {
            failure = contextField == null ? "archive-reference-field"
                : $"archive-reference-field:{contextSource}:{contextField.Declaring}:{contextField.Name}";
            return null;
        }

        void Walk(int source, IReadOnlyList<GElementGraphField> fields)
        {
            foreach (var field in fields)
            {
                contextSource = source;
                contextField = field;
                if (!GElementGraphFieldDescriptors.TryGetValue(((ushort)field.Declaring, field.Name), out var descriptor))
                    throw Fail;
                for (int i = 0; i < field.Items.Count; i++)
                {
                    var item = field.Items[i];
                    if (descriptor.Type == 14 && (descriptor.Mods & 15) == 3)
                    {
                        if (item.Values.Count != 1 || item.Values[0] < 0 || item.Values[0] > uint.MaxValue
                            || item.End - item.Start != 4 || item.Pointer != null || item.Fields.Count != 0) throw Fail;
                        uint key = (uint)item.Values[0]; int? target = null; ushort? cls = null; string outcome;
                        if (key == 0) outcome = "null";
                        else if (key == 1) outcome = "external-document";
                        else if (map.TryGetValue(key, out var registration))
                        { target = registration.QueueIndex; cls = registration.ClassId; outcome = key == 2 ? "own-root" : "own-node"; }
                        else outcome = "missing-key";
                        links.Add(new(source, field.Declaring, field.Name, i, key, item.Start, item.End, target, cls, outcome));
                    }
                    else if (item.Fields.Count != 0) Walk(source, item.Fields);
                }
            }
        }
    }
}
