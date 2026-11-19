using System.Globalization;
using RvtIO.Model;

namespace RvtIO.Parsing;

internal sealed partial class WideBlockWalker
{
    private static readonly object UnavailableEntityValue = new();
    private IReadOnlyDictionary<Guid, string>? _publicEntityNames;

    internal sealed class EntityFields : Dictionary<string, object?> { }

    internal sealed partial class Walk
    {
        internal Dictionary<int, EntityFields>? EntityPayloads;
        internal Dictionary<int, object?>? EntityCells;
        internal int OwnCellList = -1;

        internal IReadOnlyList<ExtensibleStorageEntity>? OwnPublicEntities()
        {
            if (!Exact || OwnCellList < 0 || EntityCells == null) return null;
            var entities = new Dictionary<Guid, ExtensibleStorageEntity>();
            var duplicates = new HashSet<Guid>();
            for (int i = QueueCountAtStart[OwnCellList]; i < QueueEnd[OwnCellList]; i++)
            {
                if (EntityCells.GetValueOrDefault(i) is not List<object?> entries) continue;
                foreach (var entry in entries)
                {
                    if (entry is not KeyValuePair<object?, object?> { Key: Guid id, Value: ExtensibleStorageEntity entity }
                        || id != entity.SchemaId || !IsComplete(entity, 0)) continue;
                    if (!entities.TryAdd(id, entity)) duplicates.Add(id);
                }
            }
            foreach (Guid id in duplicates) entities.Remove(id);
            return entities.Count == 0 ? null : entities.Values.ToArray();
        }
    }

    private void ReadEntityArray(Context c, SchemaClass declaring, SchemaField field, int depth, EntityFields values, int count)
    {
        var items = new List<object?>();
        var item = new EntityFields();
        for (int index = 0; index < count; index++)
        {
            item.Clear();
            Scalar(c, declaring, field, null, depth, item, null);
            items.Add(item.TryGetValue(field.Name, out var value) ? value : UnavailableEntityValue);
        }
        values[field.Name] = items;
    }

    private static object? EntityInlineValue(string className, EntityFields values) => className switch
    {
        "ESEntity" when values.TryGetValue("m_blob", out var entity) => entity,
        "AStringWrapper" when values.GetValueOrDefault("m_str") is string text => text,
        "GUIDvalue" when values.GetValueOrDefault("m_guid") is Guid guid => guid,
        "XYZ" when values.GetValueOrDefault("m_xyz") is List<object?> { Count: 3 } xyz => xyz,
        _ when className.StartsWith("std::pair< ", StringComparison.Ordinal)
            && values.TryGetValue("first", out var first) && values.TryGetValue("second", out var second)
            => new KeyValuePair<object?, object?>(first, second),
        _ => UnavailableEntityValue,
    };

    private static object ExactEntityInteger(long value) =>
        value is < -9_007_199_254_740_991L or > 9_007_199_254_740_991L ? value.ToString(CultureInfo.InvariantCulture) : value;

    private static bool IsComplete(object? value, int depth) => depth <= 32 && value switch
    {
        ExtensibleStorageEntity entity => entity.Fields.Values.All(v => IsComplete(v, depth + 1)),
        List<object?> items => items.All(v => IsComplete(v, depth + 1)),
        KeyValuePair<object?, object?> entry => IsComplete(entry.Key, depth + 1) && IsComplete(entry.Value, depth + 1),
        double real => double.IsFinite(real),
        _ => !ReferenceEquals(value, UnavailableEntityValue),
    };
}
