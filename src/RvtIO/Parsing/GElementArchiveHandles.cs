namespace RvtIO.Parsing;

internal sealed class GElementArchiveHandles
{
    // Null, the document, and the root geometry already occupy handles 0 through 2.
    private const uint FirstObject = 3;
    private readonly Dictionary<uint, uint> _handles = new();

    internal uint Assign(uint identity)
    {
        if (identity < FirstObject)
            return identity;
        if (!_handles.TryGetValue(identity, out uint handle))
        {
            handle = FirstObject + (uint)_handles.Count;
            _handles.Add(identity, handle);
        }
        return handle;
    }

    internal static bool IsCanonical(WideBlockWalker.Walk walk)
    {
        if (!walk.Exact || walk.ArchiveReferences is not { } references)
            return false;
        var handles = new GElementArchiveHandles();
        var owners = new HashSet<uint>();
        foreach (var reference in references)
        {
            // An edge can reference a loop before the owning pointer to that loop is stored.
            if (handles.Assign(reference.Handle) != reference.Handle)
                return false;
            if (reference.QueuedObject is not int queued)
                continue;
            if (reference.Handle < FirstObject || queued != owners.Count || !owners.Add(reference.Handle))
                return false;
        }
        return owners.Count == handles._handles.Count && owners.Count == walk.Queue.Count;
    }
}
