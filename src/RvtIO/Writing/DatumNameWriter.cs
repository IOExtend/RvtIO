using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.BuiltIns;
using RvtIO.Parsing;

namespace RvtIO.Writing;

internal static class DatumNameWriter
{
    private const string PlaneName = "DatumPlane.m_text";
    private const string ChainName = "GridChain.commonText";
    private const string ChainMap = "GridChain.m_curveTagToGridsInChain";
    private const string ChainId = "GridChainMembershipCell.m_gridChainId";
    private const string ProhibitedNameCharacters = "{}[]|;<>?`~\\:\t\r\n\f\v";

    private sealed record Datum(int Partition, string Class, byte[] Body, int Tag,
        WideBlockWalker.Walk Walk, WideBlockWalker.Capture Name, long? Chain);

    internal static ParameterWriter.PlanResult? Plan(RootStorage root, PartitionFraming.Tags tags, int idWidth,
        SchemaDocument schema, IReadOnlySet<long> elementIds, ParameterWriter.Edit edit, ParameterWriter.Pending pending)
    {
        string name = edit.Value!.Trim();
        if (name.Length == 0 || name.AsSpan().IndexOfAny(ProhibitedNameCharacters) >= 0)
            throw new RvtWriteRefusedException("invalid-parameter-value", "A datum name must be non-empty and contain no prohibited characters.");

        var walker = new WideBlockWalker(schema, (type, field) => (type.Name, field.Name) switch
        {
            ("Element", "m_id") => field.Name,
            ("ElementHeader", "m_categroryId" or "m_designOptionId") => field.Name,
            ("DatumPlane", "m_text") => PlaneName,
            ("GridChain", "commonText") => ChainName,
            ("GridChain", "m_curveTagToGridsInChain") => ChainMap,
            ("Grid", "m_pGridChainMembershipCell") => GridAxisReader.MembershipKey,
            ("GridChainMembershipCell", "m_gridChainId") => ChainId,
            _ => GlobalParameterWriter.CaptureKey(type, field),
        }, [GridAxisReader.MemberPair], captureRootPointers: true);
        var datums = new Dictionary<long, Datum>();
        var headers = new Dictionary<long, (int Partition, long? Category, long? Option)>();
        var globals = new HashSet<long>();
        ParameterWriter.ScanPartitions(root, tags, idWidth, (partition, record) =>
        {
            long owner = (long)record.Owner;
            if (record.Block != 0 || !elementIds.Contains(owner) || record.Body.Length < sizeof(ushort))
                return;
            int tag = BinaryPrimitives.ReadUInt16LittleEndian(record.Body.Span);
            string? cls = walker.Class(tag)?.Name;
            if (record.Channel == 101 && cls == "ElementHeader")
            {
                var header = walker.Run(record.Body.ToArray(), 0, record.Body.Length, tag, rootOnly: true);
                long? category = UserParameterWriter.Integer(header, "m_categroryId");
                if (header.RootRead && (owner == edit.ElementId || category is (int)BuiltInCategory.OST_Levels
                        or (int)BuiltInCategory.OST_Grids or (int)BuiltInCategory.OST_GridChains)
                    && !headers.TryAdd(owner, (partition, category, UserParameterWriter.Integer(header, "m_designOptionId"))))
                    throw ParameterWriter.Unverified("A datum has duplicate project headers.");
            }
            if (record.Channel != 102)
                return;
            if (cls == "ParamElemGlobal" && !globals.Add(owner))
                throw ParameterWriter.Unverified("A global parameter has duplicate project records.");
            if (cls is not ("Level" or "Grid" or "GridChain"))
                return;

            byte[] body = record.Body.ToArray();
            var walk = walker.Run(body, 0, body.Length, tag);
            if (!walk.Exact || UserParameterWriter.Integer(walk, "m_id") != owner)
                throw ParameterWriter.Unverified("A datum record could not be read exactly.");
            var label = ReadName(walk, cls);
            long? chain = null;
            if (cls == "Grid")
            {
                if (UserParameterWriter.Integer(walk, GridAxisReader.MembershipKey) is not (>= 0 and long pointer))
                    throw ParameterWriter.Unverified("A grid's chain membership could not be read exactly.");
                if (pointer != 0)
                {
                    if (walk.RootPointers?.TryGetValue(GridAxisReader.MembershipKey, out int slot) != true
                        || UserParameterWriter.Integer(walk, ChainId, slot) is not (> 0 and long chainId))
                        throw ParameterWriter.Unverified("A member grid does not identify its multi-segment grid.");
                    chain = chainId;
                }
            }
            if (!datums.TryAdd(owner, new(partition, cls, body, tag, walk, label, chain)))
                throw ParameterWriter.Unverified("A datum has duplicate project records.");
        }, pending);

        if (!datums.TryGetValue(edit.ElementId, out var target))
            throw new RvtWriteRefusedException("unsupported-parameter-owner", "The element class does not have a verified writable datum name.");
        if (target.Chain is not null)
            throw new RvtWriteRefusedException("read-only-parameter", "A multi-segment grid controls the names of its member grids.");

        var affected = new HashSet<long> { edit.ElementId };
        if (target.Class == "GridChain")
        {
            var listed = GridAxisReader.ReadChainMemberIds(target.Walk);
            var members = datums.Where(pair => pair.Value.Chain == edit.ElementId).Select(pair => pair.Key).ToHashSet();
            if (listed is null || !members.SetEquals(listed) || members.Any(id => datums[id].Name.Text != target.Name.Text))
                throw ParameterWriter.Unverified("The multi-segment grid and its members disagree.");
            affected.UnionWith(listed);
        }
        if (datums.Any(pair => !affected.Contains(pair.Key) && (pair.Value.Class == "Level") == (target.Class == "Level")
            && pair.Value.Name.Text == name))
            throw new RvtWriteRefusedException("invalid-parameter-value", "A datum in this name group already has the requested name.");

        byte[] replacement = ParameterWriter.EncodeText(name);
        var edits = new List<RvtPartitionRecordEdit>();
        foreach (long id in affected)
        {
            var datum = datums[id];
            int category = (int)(datum.Class switch
            {
                "Level" => BuiltInCategory.OST_Levels,
                "Grid" => BuiltInCategory.OST_Grids,
                _ => BuiltInCategory.OST_GridChains,
            });
            if (!headers.TryGetValue(id, out var header) || header != (datum.Partition, (long?)category, -1L)
                || datum.Walk.Queue.Any(tag => walker.IsA(tag, "GroupMembership")))
                throw ParameterWriter.Unverified("The datum is outside a verified main-model context.");
            GlobalParameterWriter.VerifyElementParameter(walker, datum.Walk, idWidth, elementIds, globals, edit.ParameterId);
            if (datum.Name.Text == name)
                continue;
            int length = sizeof(uint) + sizeof(char) * datum.Name.Text!.Length;
            byte[] updated = ParameterWriter.ReplaceValue(datum.Body, datum.Name.ValueOffset, length, replacement);
            if (ReadName(walker.Run(updated, 0, updated.Length, datum.Tag), datum.Class).Text != name)
                throw ParameterWriter.Unverified("The revised datum name could not be read back.");
            edits.Add(new RvtPartitionRecordEdit(datum.Partition, 102, (ulong)id, updated));
        }
        return edits.Count == 0 ? null : new(edits, null);
    }

    private static WideBlockWalker.Capture ReadName(WideBlockWalker.Walk walk, string datumClass)
    {
        string field = datumClass == "GridChain" ? ChainName : PlaneName;
        return walk.Exact && walk.Captures.Where(capture => capture.AtRoot && capture.Key == field).ToArray()
            is [{ Kind: WideBlockWalker.CaptureKind.Text, Text.Length: > 0, ValueOffset: >= 0 } name]
            ? name
            : throw ParameterWriter.Unverified("A datum does not have an exact saved name.");
    }
}
