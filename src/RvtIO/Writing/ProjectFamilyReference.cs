using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.Parsing;
using static RvtIO.Writing.UserParameterWriter;

namespace RvtIO.Writing;

internal sealed record ProjectFamilyReference(long FamilyId, long Category, IReadOnlySet<long> Dependencies, long? SharedNestedOwner)
{
    private const string ExternalIds = "FamilyDocument.m_big2SmallMap2";
    private const string OwnedIds = "ElementIdIndexPairSet.m_data";

    internal static ProjectFamilyReference Read(RootStorage root, PartitionFraming.Tags tags, int idWidth,
        SchemaDocument schema, IReadOnlySet<long> elementIds, long id, long familyId, ParameterWriter.Pending pending,
        long? referringType = null, bool inspectDependencies = true)
    {
        var walker = new WideBlockWalker(schema, (c, f) => (c.Name, f.Name) switch
        {
            ("Element", "m_id") or ("ElementHeader", "m_designOptionId" or "m_categroryId") => f.Name,
            ("FamilyBase", "m_oFamDoc" or "m_familyIds" or "m_bShared") or ("FamilyDocument", "m_big2SmallMap2") or
            ("ElementIdIndexPairSet", "m_data") or ("std::pair< ElementId, ForeignElemRef >", "first") or
            ("ElementIdPlusIndex", "m_elementId") => c.Name + "." + f.Name,
            _ => LoadedFamilyParameterWriter.CaptureKey(c, f) ?? CaptureKey(c, f),
        }, ["ForgeTypeId"]);
        var classes = new Dictionary<long, string?>();
        var familyRecords = new Dictionary<long, byte[]>();
        var nestedTypeRecords = new Dictionary<long, byte[]>();
        var familySymbols = new Dictionary<long, List<long>>();
        var symbolFamilies = new Dictionary<long, long>();
        var dependencies = new HashSet<long>();
        var familiesToRead = new Queue<long>();
        var mappedProxies = new HashSet<long>();
        HashSet<long>? ownedProxies = null;
        long? sharedNestedOwner = null;
        long? category = null;
        bool sawHeader = false;

        ParameterWriter.ScanPartitions(root, tags, idWidth, (_, record) =>
        {
            long owner = (long)record.Owner;
            if (record.Block != 0 || !elementIds.Contains(owner))
                return;
            int tag = Tag(record);
            if (record.Channel == 101 && owner == id)
            {
                if (sawHeader || walker.Class(tag)?.Name != "ElementHeader")
                    throw UnverifiedTarget();
                sawHeader = true;
                var header = Walk(record, tag, rootOnly: true);
                if (!header.RootRead || Integer(header, "m_designOptionId") != -1)
                    throw UnverifiedTarget();
                category = Integer(header, "m_categroryId");
            }
            if (record.Channel != 102 || (!inspectDependencies && owner != id && owner != familyId))
                return;
            string? name = walker.Class(tag)?.Name;
            if (!classes.TryAdd(owner, name))
                classes[owner] = null;
            if (name == "Family" && classes[owner] is not null)
                familyRecords.Add(owner, record.Body.ToArray());
            if (referringType is not null && name == "NestedFamSymRefElem" && classes[owner] is not null)
                nestedTypeRecords.Add(owner, record.Body.ToArray());
            if (!walker.IsA(tag, "FamilySymbol"))
                return;
            var symbol = Walk(record, tag, rootOnly: true);
            if (!symbol.RootRead || Integer(symbol, "m_id") != owner
                || Integer(symbol, "FamilySymbol.m_familyId") is not (> 0 and long owningFamily))
                throw UnverifiedTarget();
            if (!familySymbols.TryGetValue(owningFamily, out var symbols))
                familySymbols.Add(owningFamily, symbols = []);
            symbols.Add(owner);
            symbolFamilies.TryAdd(owner, owningFamily);
        }, pending);

        if (classes.GetValueOrDefault(id) != "FamilySymbol" || symbolFamilies.GetValueOrDefault(id) != familyId
            || category is not (< -1 and long targetCategory))
            throw UnverifiedTarget();

        // A verified shared project binding stores the ID on the element, outside the family declaration.
        if (!inspectDependencies)
        {
            _ = ReadFamily(familyId);
            return new(familyId, targetCategory, dependencies, null);
        }

        EnqueueFamily(familyId);
        while (familiesToRead.Count != 0)
        {
            // Following mapped families first keeps their parameter stores in the same partition scan.
            var symbolsToRead = new HashSet<long>();
            while (familiesToRead.TryDequeue(out long dependency))
            {
                var (family, symbols) = ReadFamily(dependency);
                var external = ReadMappedReferences(family, "FamilyBase.m_oFamDoc", "FamilyDocument", ExternalIds, "std::pair< ElementId, ForeignElemRef >.first");
                var owned = ReadMappedReferences(family, "FamilyBase.m_familyIds", "ElementIdIndexPairSet", OwnedIds, "ElementIdPlusIndex.m_elementId");
                if (dependency == familyId && referringType is long type && classes.GetValueOrDefault(type) == "FamilySymbol"
                    && symbolFamilies.TryGetValue(type, out long owner) && owner != familyId && external.Contains(owner) && external.Contains(type))
                    sharedNestedOwner = owner;
                if (dependency == sharedNestedOwner)
                {
                    if (Integer(family, "FamilyBase.m_bShared") != 1)
                        throw UnverifiedTarget();
                    // The container also maps the shared nested family's own private type proxies.
                    ownedProxies = owned.Where(reference => external.Contains(reference) && classes.GetValueOrDefault(reference) == "NestedFamSymRefElem")
                        .ToHashSet();
                }
                foreach (long reference in external)
                {
                    if (sharedNestedOwner is not null && (dependency == familyId || dependency == sharedNestedOwner)
                        && classes.GetValueOrDefault(reference) == "NestedFamSymRefElem")
                        mappedProxies.Add(reference);
                    else
                        FollowReference(reference);
                }
                foreach (long reference in owned)
                    if (dependency != sharedNestedOwner || ownedProxies?.Contains(reference) != true)
                        FollowReference(reference);
                AddValueReferences(family, dependency, dependency);
                symbolsToRead.UnionWith(symbols);
            }

            ParameterWriter.ScanPartitions(root, tags, idWidth, (_, record) =>
            {
                long owner = (long)record.Owner;
                if (record.Block != 0 || record.Channel != 102 || !elementIds.Contains(owner))
                    return;
                int tag = Tag(record);
                if (symbolsToRead.Contains(owner))
                {
                    AddValueReferences(Walk(record, tag), owner, symbolFamilies[owner]);
                    return;
                }
                if (!walker.IsA(tag, "FamilyInstance"))
                    return;
                var instance = Walk(record, tag, rootOnly: true);
                if (!instance.RootRead || Integer(instance, "m_id") != owner
                    || Integer(instance, "FamilyInstance.m_masterSymbolId") is not long master)
                    throw UnverifiedTarget();
                if (symbolsToRead.Contains(master))
                {
                    if (classes.GetValueOrDefault(owner) is null)
                        throw UnverifiedTarget();
                    AddValueReferences(Walk(record, tag), owner, symbolFamilies[master]);
                }
            }, pending);
        }
        if (sharedNestedOwner is not null && (ownedProxies is null || !mappedProxies.IsSubsetOf(ownedProxies)
            || mappedProxies.Any(proxy => !VerifyNestedProxy(proxy))))
            throw UnverifiedTarget();
        dependencies.Remove(familyId);
        return new(familyId, targetCategory, dependencies, sharedNestedOwner);

        static int Tag(PartitionFraming.Record record) => record.Body.Length >= 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(record.Body.Span) : -1;

        WideBlockWalker.Walk Walk(PartitionFraming.Record record, int tag, bool rootOnly = false)
        {
            byte[] bytes = record.Body.ToArray();
            return walker.Run(bytes, 0, bytes.Length, tag, rootOnly: rootOnly);
        }

        (WideBlockWalker.Walk Walk, List<long> Symbols) ReadFamily(long owner)
        {
            if (classes.GetValueOrDefault(owner) != "Family" || !familyRecords.TryGetValue(owner, out byte[]? bytes)
                || !familySymbols.TryGetValue(owner, out var symbols)
                || symbols.Any(symbol => classes.GetValueOrDefault(symbol) is null))
                throw UnverifiedTarget();
            var family = walker.Run(bytes, 0, bytes.Length, BinaryPrimitives.ReadUInt16LittleEndian(bytes));
            if (!family.Exact || family.EmptyEntityAt >= 0 || Integer(family, "m_id") != owner
                || Integer(family, "FamilyBase.m_inPlace") != 0 || Integer(family, "FamilyBase.m_categoryId") is not (< -1 and long familyCategory)
                || (owner == familyId && familyCategory != targetCategory))
                throw UnverifiedTarget();
            return (family, symbols);
        }

        void EnqueueFamily(long family)
        {
            if (dependencies.Add(family))
                familiesToRead.Enqueue(family);
        }

        void FollowReference(long reference)
        {
            string? name = classes.GetValueOrDefault(reference);
            if (name is "CategoryElem" or "GStyleElem" or "FontElem" or "LeaderStyle" or "DimensionStyle"
                or "TextNoteAttributes" or "TagNoteAttributes" or "MaterialElem" or "CoverType" or "ParamElemFamily" or "ParamElemExternal")
                return;
            if (name == "Family")
                EnqueueFamily(reference);
            else if (name is not null && symbolFamilies.TryGetValue(reference, out long owningFamily))
                EnqueueFamily(owningFamily);
            else
                throw UnverifiedTarget();
        }

        void AddValueReferences(WideBlockWalker.Walk walk, long owner, long owningFamily)
        {
            if (!walk.Exact || walk.EmptyEntityAt >= 0 || Integer(walk, "m_id") != owner)
                throw UnverifiedTarget();
            foreach (var capture in walk.Captures.Where(c => (c.DeclaringClass, c.Field) is
                ("NamedParam", "m_elemId") or ("ParamValueElementId", "m_value")))
            {
                if (capture.Kind != WideBlockWalker.CaptureKind.Integer)
                    throw UnverifiedTarget();
                if (capture.Integer > 0 && (owningFamily != sharedNestedOwner || ownedProxies?.Contains(capture.Integer) != true))
                    FollowReference(capture.Integer);
            }
        }

        HashSet<long> ReadMappedReferences(WideBlockWalker.Walk walk, string pointer, string targetClass, string array, string field)
        {
            if (walk.Captures.Where(c => c.AtRoot && c.Key == pointer).ToArray() is not
                    [{ Kind: WideBlockWalker.CaptureKind.PointerClass, Integer: > 0 and var tag, QueuePosition: >= 0 and var slot }]
                || slot >= walk.Queue.Count || walk.Queue[slot] != tag
                || walker.Class(walk.Queue[slot])?.Name != targetClass)
                throw UnverifiedTarget();
            var entries = walk.Captures.Where(c => c.QueueIndex == slot && c.Key == field).ToArray();
            var ids = entries.Select(c => c.Integer).ToHashSet();
            if (Integer(walk, array + "#", slot) != entries.Length
                || entries.Any(c => c.Kind != WideBlockWalker.CaptureKind.Integer || c.Integer <= 0)
                || ids.Count != entries.Length)
                throw UnverifiedTarget();
            return ids;
        }

        bool VerifyNestedProxy(long reference)
        {
            if (!nestedTypeRecords.TryGetValue(reference, out byte[]? record))
                return false;
            var proxy = walker.Run(record, 0, record.Length, BinaryPrimitives.ReadUInt16LittleEndian(record));
            return proxy is { Exact: true, EmptyEntityAt: < 0 } && Integer(proxy, "m_id") == reference
                && Integer(proxy, "NestedFamSymRefElem.m_categoryId") is < -1
                && RootName(proxy, "NestedFamSymRefElem.m_famName") is not null
                && RootName(proxy, "NestedFamSymRefElem.m_symName") is not null;
        }
    }

    private static RvtWriteRefusedException UnverifiedTarget() =>
        ParameterWriter.Unverified("The project family type target or its family dependencies could not be verified.");
}
