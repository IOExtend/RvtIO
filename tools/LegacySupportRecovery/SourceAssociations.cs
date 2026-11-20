using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using RvtIO.Parsing;

internal static class SourceAssociations
{
    internal static Dictionary<long, HashSet<long>> Read(
        SchemaDocument schema, byte[]? latest, IReadOnlyDictionary<Guid, SchemaField[]?>? entitySchemas)
    {
        const string repoPair = "std::pair< AString, GenericRepo >", itemPair = "std::pair< AString, AString >";
        if (latest is null || latest.Length < sizeof(ushort))
            throw new InvalidDataException("The source analytical association stream is missing.");
        var walker = new WideBlockWalker(schema, (_, _) => null, [repoPair, itemPair])
            { StepBudget = 16L * latest.Length + 65536 };
        if (entitySchemas is not null) walker.UseEntitySchemas(entitySchemas);
        var walk = walker.Run(latest, 0, latest.Length, BinaryPrimitives.ReadUInt16LittleEndian(latest));
        if (!walk.RootRead || walk.Error is not null)
            throw new InvalidDataException("The source analytical associations could not be decoded.");

        var repos = new Dictionary<string, List<(string Key, string Value)>>(StringComparer.Ordinal);
        var items = new List<(string, string)>();
        foreach (var record in walk.Records)
        {
            if (record.QueueIndex < 0 || walker.Class(walk.Queue[record.QueueIndex])?.Name != "AppInfoElementsAssociations" ||
                record.Values.GetValueOrDefault("first") is not string name) continue;
            switch (walker.Class(record.ClassId)?.Name)
            {
                case itemPair when record.Values.GetValueOrDefault("second") is string value:
                    items.Add((name, value));
                    break;
                case repoPair:
                    if (!repos.TryAdd(name, items)) throw new InvalidDataException("Duplicate analytical association repository.");
                    items = new();
                    break;
            }
        }
        return Associations(repos.GetValueOrDefault("LB_Associations"), repos.GetValueOrDefault("LB_References"));
    }

    private static Dictionary<long, HashSet<long>> Associations(
        IReadOnlyList<(string Key, string Value)>? associations, IReadOnlyList<(string Key, string Value)>? references)
    {
        var localIds = new HashSet<long>();
        foreach (var (key, json) in references ?? [])
        {
            if (key != ".") continue;
            using var record = ParseRelation(json);
            if (Number(record.RootElement, "Id") is long id) localIds.Add(id);
        }
        var result = new Dictionary<long, HashSet<long>>();
        foreach (var (_, json) in associations ?? [])
        {
            using var record = ParseRelation(json);
            var fields = record.RootElement;
            if (Number(fields, "ReferenceId") is not long reference || !localIds.Contains(reference) ||
                !fields.TryGetProperty("IsLocalGroup", out var localGroup) || localGroup.ValueKind != JsonValueKind.False ||
                !fields.TryGetProperty("IsExternalGroup", out var externalGroup) || externalGroup.ValueKind != JsonValueKind.False ||
                Number(fields, "ExternalId") is not long analytical || analytical <= 0 ||
                Number(fields, "LocalId") is not long physical || physical <= 0) continue;
            if (!result.TryGetValue(analytical, out var parents)) result[analytical] = parents = [];
            parents.Add(physical);
        }
        return result;
    }

    private static JsonDocument ParseRelation(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException exception) { throw new InvalidDataException("An analytical association record contains invalid JSON.", exception); }
    }

    private static long? Number(JsonElement fields, string name)
    {
        if (fields.ValueKind != JsonValueKind.Object || !fields.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)) return number;
        return value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number) ? number : null;
    }
}
