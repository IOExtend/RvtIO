using System.Globalization;
using ICSharpCode.SharpZipLib.Checksum;
using OpenMcdf;
using RvtIO.Model;

namespace RvtIO.Parsing;

/// <summary>Reads public entities from legacy project records and embedded family records in their saved document scope.</summary>
internal static class ExtensibleStorageReader
{
    internal static void Apply(RootStorage root, RevitFileInfo info, IReadOnlyList<PartitionFraming.Framed?> framed)
    {
        // An unframed partition may contain a second copy of an owner or family block.
        if (framed.Count == 0 || framed.Any(frame => frame == null)) return;
        var parts = framed.Cast<PartitionFraming.Framed>().ToArray();
        bool legacy = parts[0].IdWidth == 4 && info.ElemTableIsExact;
        var candidates = parts.SelectMany(part => part.FamilyEntityCandidates.Select(item => (Part: part, Record: item))).ToArray();
        if ((!legacy && candidates.Length == 0) || TfSchemaReader.Read(root) is not { } schema
            || schema.Classes.FirstOrDefault(c => c.Name == "ESEntityCell") is not { } cell) return;

        var projectNames = new Dictionary<Guid, string>();
        var project = EntitySchemaReader.Read(root, schema, projectNames);
        var walker = new WideBlockWalker(schema, (declaring, field) =>
            declaring.Name == "Element" && field.Name == "m_id" ? field.Name : null, []);
        if (legacy && projectNames.Count != 0)
        {
            walker.UseEntitySchemas(project, publicEntityNames: projectNames);
            ApplyLegacyProject(info, parts, walker, BitConverter.GetBytes((ushort)cell.Id));
        }

        if (candidates.Length == 0) return;
        var uniqueKeys = parts.Where(part => part.FamilyKeys != null).SelectMany(part => part.FamilyKeys!)
            .GroupBy(key => key, StringComparer.Ordinal).Where(group => group.Count() == 1)
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        uniqueKeys.IntersectWith(candidates.Select(candidate => candidate.Record.ContentKey));
        var names = new Dictionary<string, Dictionary<Guid, string>>(StringComparer.Ordinal);
        var families = uniqueKeys.Count != 0 && SchemaRelease.FromSchema(new FormatsSchema(schema)) is not >= 2025
            ? EntitySchemaReader.ReadContentDocuments(root, schema, uniqueKeys, names) : [];
        var embedded = new List<EmbeddedFamilyElementStorage>();
        foreach (var block in candidates.GroupBy(candidate => candidate.Record.ContentKey, StringComparer.Ordinal))
        {
            if (!uniqueKeys.Contains(block.Key) || projectNames.Count == 0 && !names.ContainsKey(block.Key)) continue;
            ConfigureFamily(walker, project, projectNames, families.GetValueOrDefault(block.Key), names.GetValueOrDefault(block.Key));
            var key = new Guid(Convert.FromHexString(block.Key));
            foreach (var owner in block.GroupBy(candidate => candidate.Record.Item.Owner))
            {
                if (owner.Take(2).ToArray() is not [var candidate]) continue;
                if (ReadOwner(walker, candidate.Part, candidate.Record.Item, []) is { } entities)
                    embedded.Add(new(key, owner.Key.ToString(CultureInfo.InvariantCulture), entities));
            }
        }
        if (embedded.Count > 0) info.EmbeddedFamilyExtensibleStorage = embedded;
    }

    internal static void ConfigureFamily(WideBlockWalker walker, Dictionary<Guid, SchemaField[]?> project,
        Dictionary<Guid, string> projectNames, Dictionary<Guid, SchemaField[]?>? family, Dictionary<Guid, string>? familyNames)
    {
        if (family is not { Count: > 0 })
        {
            walker.UseEntitySchemas(project, publicEntityNames: projectNames);
            return;
        }

        var layouts = new Dictionary<Guid, SchemaField[]?>(project);
        var names = new Dictionary<Guid, string>(projectNames);
        foreach (var (guid, fields) in family)
        {
            layouts[guid] = fields;
            names.Remove(guid);
            if (familyNames?.TryGetValue(guid, out var name) == true) names.Add(guid, name);
        }
        walker.UseEntitySchemas(layouts, publicEntityNames: names);
    }

    private static void ApplyLegacyProject(RevitFileInfo info, PartitionFraming.Framed[] parts, WideBlockWalker walker, byte[] cellTag)
    {
        var recovered = new Dictionary<ulong, IReadOnlyList<ExtensibleStorageEntity>>();
        foreach (var part in parts)
            foreach (var item in part.Items)
                if (item.Channel == 102 && ReadOwner(walker, part, item, cellTag) is { } entities)
                    recovered.TryAdd(item.Owner, entities);
        if (recovered.Count == 0) return;

        // The framing index has already dropped any copies resolved by the increment table.
        var owners = parts.SelectMany(part => part.Items).Where(item => item.Channel == 102 && recovered.ContainsKey(item.Owner))
            .GroupBy(item => item.Owner).Where(group => group.Count() == 1).Select(group => group.Key).ToHashSet();
        foreach (var element in info.Elements)
            if (owners.Contains((ulong)element.ElementId) && recovered.TryGetValue((ulong)element.ElementId, out var entities))
                element.ExtensibleStorage = entities;
    }

    private static IReadOnlyList<ExtensibleStorageEntity>? ReadOwner(WideBlockWalker walker, PartitionFraming.Framed part,
        PartitionFraming.ProjectItem item, ReadOnlySpan<byte> requiredCellTag)
    {
        int length = item.Length - part.IdWidth - 12;
        if (length < 2 || item.Owner > long.MaxValue) return null;
        var (data, start, _) = part.Bytes(item);
        int offset = start + part.IdWidth + 8;
        if (!requiredCellTag.IsEmpty && data.AsSpan(offset, length).IndexOf(requiredCellTag) < 0) return null;
        int tag = BitConverter.ToUInt16(data, offset);
        if (!walker.IsA(tag, "Element")) return null;
        walker.StepBudget = 16L * length + 65536;
        var walk = walker.Run(data, offset, length, tag);
        if (walk.OwnPublicEntities() is not { } entities
            || !walk.Captures.Any(c => c.AtRoot && c.Key == "m_id" && c.Integer == (long)item.Owner)) return null;
        var checksum = new Adler32();
        checksum.Update(new ArraySegment<byte>(data, offset, length));
        return (uint)checksum.Value == BitConverter.ToUInt32(data, start + part.IdWidth) ? entities : null;
    }
}
