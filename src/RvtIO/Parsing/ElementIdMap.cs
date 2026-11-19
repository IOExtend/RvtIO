namespace RvtIO.Parsing;

/// <summary>Document-local keys for the geometry readers whose records use signed 32-bit IDs.</summary>
internal sealed class ElementIdMap
{
    private readonly HashSet<int> _occupied = [];
    private readonly Dictionary<long, int> _keys = [];
    private readonly Dictionary<int, long> _sourceIds = [];
    private int _nextKey = int.MaxValue;

    internal ElementIdMap(IReadOnlyList<ElemTableLayout.Row> rows)
    {
        foreach (var row in rows)
            if (row.Id <= int.MaxValue) _occupied.Add((int)row.Id);
        foreach (var row in rows)
            if (row.OwningId is > 0 and <= int.MaxValue) _occupied.Add((int)row.OwningId);
        foreach (var row in rows)
            if (row.Id > int.MaxValue) Key(row.Id);
    }

    internal long SourceId(int key) => _sourceIds.TryGetValue(key, out long id) ? id : key;

    internal IReadOnlyDictionary<int, string>? SourceIdsAsText() => _sourceIds.Count == 0 ? null
        : _sourceIds.ToDictionary(pair => pair.Key, pair => pair.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    internal bool TryGetKey(long sourceId, out int key)
    {
        if (_keys.TryGetValue(sourceId, out key)) return true;
        if (sourceId is < int.MinValue or > int.MaxValue) return false;
        key = (int)sourceId;
        return !_sourceIds.ContainsKey(key);
    }

    internal int Key(long sourceId)
    {
        if (sourceId is >= int.MinValue and <= 0) return (int)sourceId;
        if (_keys.TryGetValue(sourceId, out int key)) return key;
        if (sourceId <= int.MaxValue && sourceId > 0 && !_sourceIds.ContainsKey((int)sourceId))
        {
            _occupied.Add((int)sourceId);
            return (int)sourceId;
        }
        while (_nextKey > 0 && !_occupied.Add(_nextKey)) _nextKey--;
        if (_nextKey == 0) throw new InvalidOperationException("The document has exhausted the local element key range.");
        key = _nextKey--;
        _keys.Add(sourceId, key);
        _sourceIds.Add(key, sourceId);
        return key;
    }
}
