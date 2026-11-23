using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using static RvtIO.Writing.UserParameterWriter;
using FormulaValue = RvtIO.Writing.ParameterFormulaEvaluator.Value;

namespace RvtIO.Writing;

internal static class GlobalParameterWriter
{
    private const string Dependency = "global-parameter-dependency";
    private const string AssociatedParameters = "project-global-associations";
    private static readonly string[] AssociationFields = ["m_famParamId", "m_elemPropId", "m_geomTag", "m_bIsSymbol"];

    internal sealed record GlobalRecord(int Partition, int ClassId, byte[] Body);
    internal sealed record ProjectDependencies(
        Dictionary<long, GlobalRecord> Globals, Dictionary<long, HashSet<long>> Formulas,
        Dictionary<long, List<PropertyTarget>> Properties, HashSet<long> OtherConsumers);
    internal readonly record struct PropertyTarget(long ElementId, long ParameterId);
    internal readonly record struct PropertyValue(long GlobalId, FormulaParameterStorage Format, byte[] Previous, byte[] Replacement)
    {
        internal void VerifyFormat(FormulaParameterStorage target)
        {
            if (target.Storage != Format.Storage || target.Unit != Format.Unit || target.Kind != Format.Kind)
                throw ParameterWriter.Unverified("The controlled property and global parameter have different types.");
            _ = target.Decode(Replacement);
        }
    }
    private readonly record struct Association(long GlobalId, long ParameterId, int GlobalOffset, bool Ordinary);
    private readonly record struct FormulaChange(FormulaValue Before, FormulaValue After);

    internal static string? CaptureKey(SchemaClass declaring, SchemaField field) => (declaring.Name, field.Name) switch
    {
        ("ParamIdPair", "m_famParamId") or ("DimSegInfo" or "ParamExprEntry", "m_paramId") => Dependency,
        ("ParametrizedElemParamsCellBase", "m_paramDrivenData") => AssociatedParameters,
        ("ParamIdPair", "m_elemPropId" or "m_geomTag" or "m_bIsSymbol") => field.Name,
        ("ParamElemGlobal", "m_constraintId" or "m_propertyId" or "m_reporting" or "m_oExpression") or
        ("ParamStorage", "m_dbl" or "m_elemId" or "m_int" or "m_storageType") or ("AStringWrapper", "m_str")
            => declaring.Name + "." + field.Name,
        _ => ParameterFormulaEvaluator.CaptureKey(declaring, field),
    };

    internal static void VerifyElementParameter(WideBlockWalker walker, WideBlockWalker.Walk walk, int idWidth,
        IReadOnlySet<long> elementIds, IReadOnlySet<long> globals, long parameterId, long? controllingGlobal = null)
    {
        if (!walk.Exact || (globals.Count != 0 && walk.EmptyEntityAt >= 0))
            throw ParameterWriter.Unverified("The element's parameter associations could not be read exactly.");
        var slots = walk.Queue.Select((tag, index) => (tag, index))
            .Where(item => walker.IsA(item.tag, "ParametrizedElemParamsCellBase")).ToArray();
        Association[] rows = slots.Length == 0 ? [] : slots is [var slot] &&
            walker.Class(slot.tag)?.Name == "ProjectParametrizedElemParamsCell" && walk.EmptyEntityAt < 0
                ? ReadAssociations(walk, slot.index, idWidth, elementIds, globals)
                : throw ParameterWriter.Unverified("The element has an unsupported parameter association.");
        if (rows.Any(row => !row.Ordinary))
            throw ParameterWriter.Unverified("The element has an unverified global parameter association.");
        var association = rows.SingleOrDefault(row => row.ParameterId == parameterId);
        if (controllingGlobal is long global && association.GlobalId != global)
            throw ParameterWriter.Unverified("The element parameter is not associated with the expected global parameter.");
        if (controllingGlobal is null && association.GlobalId > 0)
            throw new RvtWriteRefusedException("read-only-parameter", "A global parameter controls this element parameter.");
    }

    private static Association[] ReadAssociations(WideBlockWalker.Walk walk, int slot, int idWidth,
        IReadOnlySet<long> elementIds, IReadOnlySet<long> globals)
    {
        var fields = walk.Captures.Where(c => c.QueueIndex == slot).ToArray();
        var rows = fields.Where(c => c.DeclaringClass == "ParamIdPair").ToArray();
        if (fields.Where(c => c.Key == AssociatedParameters + "#").ToArray() is not
            [{ Kind: WideBlockWalker.CaptureKind.Integer, ValueOffset: >= 0 } count] || rows.Length != count.Integer * AssociationFields.Length)
            throw ParameterWriter.Unverified("The element's global parameter association list could not be read exactly.");

        var seen = new HashSet<long>();
        var duplicates = new HashSet<long>();
        var associations = new List<Association>();
        int offset = count.ValueOffset + sizeof(uint);
        foreach (var row in rows.Chunk(AssociationFields.Length))
        {
            var global = row[0];
            var property = row[1];
            if (!row.Select(c => c.Field).SequenceEqual(AssociationFields) || row.Any(c => c.Kind != WideBlockWalker.CaptureKind.Integer) ||
                global.ValueOffset != offset || property.ValueOffset != offset + idWidth ||
                row[2].ValueOffset != offset + 2 * idWidth || row[3].ValueOffset != offset + 2 * idWidth + sizeof(int) ||
                global.Integer <= 0 || !globals.Contains(global.Integer) ||
                property.Integer is 0 or -1 or < int.MinValue || (property.Integer > 0 && !elementIds.Contains(property.Integer)))
                throw ParameterWriter.Unverified("The element has an unverified global parameter association.");
            if (!seen.Add(property.Integer))
                duplicates.Add(property.Integer);
            associations.Add(new(global.Integer, property.Integer, global.ValueOffset, row[2].Integer == -1 && row[3].Integer == 0));
            offset += 2 * idWidth + sizeof(int) + sizeof(byte);
        }
        return associations.Select(row => duplicates.Contains(row.ParameterId) ? row with { Ordinary = false } : row).ToArray();
    }

    internal static ParameterWriter.PlanResult? Plan(
        RootStorage root, PartitionFraming.Tags tags, int idWidth, WideBlockWalker walker, IReadOnlySet<long> elementIds, IReadOnlySet<long> globals,
        ParameterWriter.Edit edit, ParameterWriter.Pending pending, int partition, byte[] body, int classId)
    {
        if (edit.Value is null)
            throw new RvtWriteRefusedException("unsupported-parameter-clear", "Global parameters require an explicit value.");

        var (walk, slot) = ReadDefinition(walker, body, edit);
        if (walk.EmptyEntityAt >= 0 || Integer(walk, "ParamElemGlobal.m_reporting") is not (0 or 1) ||
            Integer(walk, "ParamElemGlobal.m_oExpression") is not (>= 0 and long expression))
            throw ParameterWriter.Unverified("The global parameter's formula and reporting state could not be verified.");
        if (expression != 0 || Integer(walk, "ParamElemGlobal.m_reporting") == 1)
            throw new RvtWriteRefusedException("read-only-parameter", "A formula or reporting dimension drives this global parameter.");
        VerifyUnlinked(walk);

        // Supported edits change values only. The same dependency census applies to every global assignment in the batch.
        var dependencies = pending.GlobalParameterDependencies ??= ReadDependencies(root, tags, idWidth, walker, elementIds, globals, pending);
        if (dependencies.OtherConsumers.Contains(edit.ParameterId))
            throw ParameterWriter.Unverified("A dimension or element property depends on this global parameter.");

        bool currency = IsCurrency(walker, walk, slot);
        var (storage, replacement, quantity) = currency
            ? (StorageType.Double, ParameterWriter.EncodeCost(edit.Value), (UnitType?)UnitType.UT_Currency)
            : Encode(walker, walk, slot, edit, idWidth, material: null);
        var result = new List<RvtPartitionRecordEdit>();
        var properties = new List<(PropertyTarget Target, PropertyValue Value)>();
        var location = ValueLocation(walk, storage, idWidth);
        WriteValue(result, edit.ElementId, partition, body, classId, walker, storage, replacement, idWidth, location);
        bool recalculates = dependencies.Formulas.ContainsKey(edit.ParameterId);
        if (currency || ParameterMeasurement.IsAdditionalSpec(quantity) || recalculates || dependencies.Properties.ContainsKey(edit.ParameterId))
        {
            var type = FormulaParameterStorage.Read(walker, walk, slot, includeCurrency: true);
            var previous = body.AsSpan(location.Offset, location.Length);
            var input = new FormulaChange(type.Decode(previous), type.Decode(replacement));
            if (type.Storage == StorageType.Double)
                ParameterFormulaEvaluator.VerifyStableChange(input.Before.Number, input.After.Number);
            CollectProperties(properties, dependencies, new(edit.ParameterId, type, previous.ToArray(), replacement));
            if (recalculates)
                Recalculate(result, properties, dependencies, pending, walker, edit.ParameterId, input, idWidth);
            if (properties.Count != 0)
            {
                // Several globals can update different properties on the same record in this assignment.
                var staged = new ParameterWriter.Pending();
                foreach (var (key, record) in pending.Records)
                    staged.Records.Add(key, record);
                foreach (var (target, value) in properties)
                {
                    var plan = ParameterWriter.Plan(root, tags, idWidth, new(target.ElementId, target.ParameterId, null), staged, value);
                    staged.Apply(plan);
                    if (plan is not null)
                        result.AddRange(plan.Records);
                }
            }
        }
        return result.Count == 0 ? null : new ParameterWriter.PlanResult(result, null);
    }

    private static bool IsCurrency(WideBlockWalker walker, WideBlockWalker.Walk walk, int slot)
        => walker.Class(walk.Queue[slot])?.Name == "ParamDefValue"
            && Integer(walk, "ParamDefValue.m_restriction", slot) == 1
            && Integer(walk, "ParamDefValue.m_boundless", slot) == 0
            && ParameterMeasurement.Spec(walk, slot, includeCurrency: true) == UnitType.UT_Currency;

    private static void CollectProperties(List<(PropertyTarget Target, PropertyValue Value)> properties,
        ProjectDependencies dependencies, PropertyValue value)
    {
        if (dependencies.Properties.TryGetValue(value.GlobalId, out var targets))
            foreach (var target in targets)
            {
                if (value.Format.Unit == UnitType.UT_Currency && target.ParameterId < 0 && target.ParameterId != (int)BuiltInParameter.ALL_MODEL_COST)
                    throw ParameterWriter.Unverified("The Currency global parameter controls an unverified built-in property.");
                properties.Add((target, value));
            }
    }

    private static void Recalculate(List<RvtPartitionRecordEdit> result,
        List<(PropertyTarget Target, PropertyValue Value)> properties, ProjectDependencies dependencies, ParameterWriter.Pending pending,
        WideBlockWalker walker, long inputId, FormulaChange input, int idWidth)
    {
        var affected = new HashSet<long> { inputId };
        var remaining = new Queue<long>();
        remaining.Enqueue(inputId);
        while (remaining.TryDequeue(out long next))
        {
            if (dependencies.OtherConsumers.Contains(next))
                throw ParameterWriter.Unverified("A dimension or element property depends on a value in the global formula chain.");
            if (!dependencies.Formulas.TryGetValue(next, out var consumers))
                continue;
            foreach (long consumer in consumers)
                if (affected.Add(consumer))
                    remaining.Enqueue(consumer);
        }

        var calculated = new Dictionary<long, FormulaChange> { [inputId] = input };
        var evaluating = new HashSet<long>();
        foreach (long parameter in affected)
            Resolve(parameter, ParameterFormulaEvaluator.MaxDepth);

        FormulaChange Resolve(long id, int depth)
        {
            if (calculated.TryGetValue(id, out var value))
                return value;
            if (depth <= 0 || !evaluating.Add(id) || !dependencies.Globals.TryGetValue(id, out var record))
                throw ParameterWriter.Unverified("A global formula reference is missing or its dependency chain cannot be resolved.");

            var key = (record.Partition, 0, 102, (ulong)id);
            byte[] body = pending.Records.TryGetValue(key, out var prior) ? prior.Body.ToArray() : record.Body;
            var (walk, slot) = ReadDefinition(walker, body, new ParameterWriter.Edit(id, id, null));
            var type = FormulaParameterStorage.Read(walker, walk, slot, includeCurrency: true);
            var (offset, length) = ValueLocation(walk, type.Storage, idWidth);
            var stored = body.AsSpan(offset, length);
            var oldValue = type.Decode(stored);
            value = new(oldValue, oldValue);
            if (affected.Contains(id))
            {
                VerifyUnlinked(walk);
                if (Integer(walk, "ParamElemGlobal.m_reporting") != 0)
                    throw ParameterWriter.Unverified("A reporting global parameter occurs in the formula chain.");
                if (walk.Captures.Where(c => c.AtRoot && c.Key == "ParamElemGlobal.m_oExpression").ToArray() is not [var expression])
                    throw ParameterWriter.Unverified("The global formula root could not be verified.");
                var before = ParameterFormulaEvaluator.Evaluate(walker, walk, expression, depth, (reference, budget) => Resolve(reference, budget).Before);
                if (before is { } calculatedBefore && !stored.SequenceEqual(type.Encode(calculatedBefore)))
                    throw ParameterWriter.Unverified("A dependent global formula does not reproduce its stored value exactly.");
                var after = ParameterFormulaEvaluator.Evaluate(walker, walk, expression, depth, (reference, budget) => Resolve(reference, budget).After);
                if (after is { } calculatedAfter)
                {
                    byte[] replacement = type.Encode(calculatedAfter);
                    value = new(oldValue, type.Decode(replacement));
                    if (type.Storage == StorageType.Double)
                        ParameterFormulaEvaluator.VerifyStableChange(value.Before.Number, value.After.Number);
                    WriteValue(result, id, record.Partition, body, record.ClassId, walker, type.Storage, replacement, idWidth, (offset, length));
                    CollectProperties(properties, dependencies, new(id, type, stored.ToArray(), replacement));
                }
            }
            evaluating.Remove(id);
            calculated.Add(id, value);
            return value;
        }
    }

    private static void VerifyUnlinked(WideBlockWalker.Walk walk)
    {
        if (Integer(walk, "ParamElemGlobal.m_constraintId") != -1 || Integer(walk, "ParamElemGlobal.m_propertyId") != -1)
            throw ParameterWriter.Unverified("The global parameter has a dimension or property link that is outside the verified write scope.");
    }

    private static void WriteValue(List<RvtPartitionRecordEdit> result, long id, int partition, byte[] body, int classId,
        WideBlockWalker walker, StorageType storage, byte[] replacement, int idWidth, (int Offset, int Length) location)
    {
        var (offset, length) = location;
        if (body.AsSpan(offset, length).SequenceEqual(replacement))
            return;

        byte[] updated = ParameterWriter.ReplaceValue(body, offset, length, replacement);
        var verified = walker.Run(updated, 0, updated.Length, classId);
        if (!verified.Exact || verified.EmptyEntityAt >= 0 || ValueLocation(verified, storage, idWidth) != (offset, replacement.Length) ||
            !updated.AsSpan(offset, replacement.Length).SequenceEqual(replacement))
            throw ParameterWriter.Unverified("The updated global parameter could not be read back.");
        result.Add(new(partition, 102, (ulong)id, updated));
    }

    private static (int Offset, int Length) ValueLocation(WideBlockWalker.Walk walk, StorageType storage, int idWidth)
    {
        int reporting = Field("ParamElemGlobal.m_reporting", WideBlockWalker.CaptureKind.Integer).ValueOffset;
        var number = Field("ParamStorage.m_dbl", WideBlockWalker.CaptureKind.Real);
        var reference = Field("ParamStorage.m_elemId", WideBlockWalker.CaptureKind.Integer);
        var integer = Field("ParamStorage.m_int", WideBlockWalker.CaptureKind.Integer);
        var type = Field("ParamStorage.m_storageType", WideBlockWalker.CaptureKind.Integer);
        int expression = Field("ParamElemGlobal.m_oExpression", WideBlockWalker.CaptureKind.PointerClass).ValueOffset;
        if (walk.Captures.Where(c => c.QueueIndex == -1 && c.Key == "AStringWrapper.m_str" && c.ValueOffset == reporting + 1).ToArray()
                is not [{ Kind: WideBlockWalker.CaptureKind.Text, Text: not null } text] ||
            reference.Integer != -1 || type.Integer != (int)storage ||
            !double.IsFinite(number.Real) || (storage != StorageType.Double && number.Real != 0) ||
            (storage != StorageType.Integer && integer.Integer != 0) || (storage != StorageType.String && text.Text.Length != 0))
            throw ParameterWriter.Unverified("The global parameter does not have the verified typed value layout.");

        int textLength = checked(sizeof(uint) + sizeof(char) * text.Text.Length);
        if (text.ValueOffset + textLength != number.ValueOffset || number.ValueOffset + sizeof(double) != reference.ValueOffset ||
            reference.ValueOffset + idWidth != integer.ValueOffset || integer.ValueOffset + sizeof(int) != type.ValueOffset ||
            type.ValueOffset + sizeof(int) != expression)
            throw ParameterWriter.Unverified("The global parameter's inline value could not be located exactly.");

        return storage switch
        {
            StorageType.String => (text.ValueOffset, textLength),
            StorageType.Integer => (integer.ValueOffset, sizeof(int)),
            StorageType.Double => (number.ValueOffset, sizeof(double)),
            _ => throw ParameterWriter.Unverified("The global parameter's storage type is outside the verified scope."),
        };

        WideBlockWalker.Capture Field(string key, WideBlockWalker.CaptureKind kind) =>
            walk.Captures.Where(c => c.QueueIndex == -1 && c.Key == key).ToArray() is [{ ValueOffset: >= 0 } field] && field.Kind == kind
                ? field : throw ParameterWriter.Unverified("The global parameter's value fields could not be located exactly.");
    }

    private static ProjectDependencies ReadDependencies(
        RootStorage root, PartitionFraming.Tags tags, int idWidth, WideBlockWalker walker, IReadOnlySet<long> elementIds, IReadOnlySet<long> globalIds,
        ParameterWriter.Pending pending)
    {
        var globals = new Dictionary<long, GlobalRecord>();
        var formulas = new Dictionary<long, HashSet<long>>();
        var properties = new Dictionary<long, List<PropertyTarget>>();
        var otherConsumers = new HashSet<long>();
        var seen = new HashSet<long>();
        ParameterWriter.ScanPartitions(root, tags, idWidth, (partition, record) =>
        {
            long id = (long)record.Owner;
            if (record.Block != 0 || record.Channel != 102 || !elementIds.Contains(id))
                return;
            if (!seen.Add(id) || record.Body.Length < 2)
                throw ParameterWriter.Unverified("A project element has an ambiguous global parameter dependency record.");

            byte[] data = record.Body.ToArray();
            int classId = BinaryPrimitives.ReadUInt16LittleEndian(data);
            var walk = walker.Run(data, 0, data.Length, classId);
            if (!walk.Exact || walk.EmptyEntityAt >= 0 || Integer(walk, "m_id") != id)
                throw ParameterWriter.Unverified("A project element could not be read exactly for global parameter dependencies.");
            bool global = walker.Class(classId)?.Name == "ParamElemGlobal";
            if (global)
                globals.Add(id, new(partition, classId, data));
            var claimed = new HashSet<int>();
            var slots = walk.Queue.Select((tag, index) => (tag, index))
                .Where(item => walker.IsA(item.tag, "ParametrizedElemParamsCellBase")).ToArray();
            if (slots is [var slot] && walker.Class(slot.tag)?.Name == "ProjectParametrizedElemParamsCell")
                foreach (var association in ReadAssociations(walk, slot.index, idWidth, elementIds, globalIds))
                    if (association.Ordinary && (association.ParameterId > 0 || ParameterWriter.IsSupportedBuiltInGlobalProperty(association.ParameterId)))
                    {
                        if (!properties.TryGetValue(association.GlobalId, out var targets))
                            properties.Add(association.GlobalId, targets = []);
                        targets.Add(new(id, association.ParameterId));
                        claimed.Add(association.GlobalOffset);
                    }
            foreach (var field in walk.Captures.Where(c => c.Integer > 0 && c.Key is Dependency or "formula-reference"))
            {
                if (global && field.Key == "formula-reference")
                {
                    if (!formulas.TryGetValue(field.Integer, out var consumers))
                        formulas.Add(field.Integer, consumers = []);
                    consumers.Add(id);
                }
                else if (field.Key != Dependency || !claimed.Contains(field.ValueOffset))
                    otherConsumers.Add(field.Integer);
            }
        }, pending);
        if (!seen.SetEquals(elementIds))
            throw ParameterWriter.Unverified("The project element records are incomplete for global parameter dependency checks.");
        return new(globals, formulas, properties, otherConsumers);
    }
}
