using System.Buffers.Binary;
using OpenMcdf;
using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using static RvtIO.Writing.UserParameterWriter;

namespace RvtIO.Writing;

internal static class LoadedFamilyParameterWriter
{
    private const string TrivialIds = WideBlockWalker.AllItemsKeyPrefix + "trivial";
    private static readonly string[] NamedFields =
        ["m_str", "m_oExpression", "m_value", "m_elemId", "m_paramId", "m_int", "m_instance", "m_reporting"];
    private sealed record ProjectRecord(int Partition, long Id, byte[] Body, int ClassId, WideBlockWalker.Walk Walk);
    private sealed record FormulaSymbol(ProjectRecord Record, FamilyStore Values);
    private sealed record ValueEdit(long Id, StorageType Storage, byte[] Bytes, int Mode);
    internal sealed record NamedValue(WideBlockWalker.Capture[] Fields)
    {
        internal long Id => Fields[4].Integer;
        internal long Instance => Fields[6].Integer;
        internal bool IsDriven => Fields[1].Integer != 0 || Fields[7].Integer != 0;
        internal WideBlockWalker.Capture Value(StorageType storage) => Fields[storage switch
        {
            StorageType.String => 0,
            StorageType.Double => 2,
            StorageType.ElementId => 3,
            _ => 5,
        }];
    }
    internal sealed record FamilyStore(int CountOffset, NamedValue[] Rows);

    internal static string? CaptureKey(SchemaClass declaring, SchemaField field) => (declaring.Name, field.Name) switch
    {
        ("FamilyBase", "m_trivialParamIds") => TrivialIds,
        ("FamilyBase", "m_familyParams" or "m_inPlace" or "m_categoryId" or "m_name") or
        ("FamilyInstance", "m_pInstParams" or "m_masterSymbolId" or "m_superInstanceId") or
        ("FamilySymbol", "m_pParams" or "m_familyId" or "m_masterId" or "m_ownerInstanceId") or
        ("FamilyParams", "m_params") or
        ("ParamElemFamily", "m_instanceParam") or
        ("InstInfoBase", "m_symbolId") => declaring.Name + "." + field.Name,
        ("ParameterExpression" or "CalculatedParameterExpression" or "FormulaTokenParameter", "m_paramId") => "formula-reference",
        ("NamedParam", _) when NamedFields.Contains(field.Name) => field.Name,
        _ => null,
    };

    internal static ParameterWriter.PlanResult? Plan(
        RootStorage root, PartitionFraming.Tags tags, int idWidth, WideBlockWalker walker, IReadOnlySet<long> elementIds,
        IReadOnlySet<long> globals, int? category,
        byte[]? definition, ParameterWriter.Edit edit, long? material, WideBlockWalker.Walk? reference, ProjectFamilyReference? projectReference,
        int partition, byte[] body, WideBlockWalker.Walk walk,
        ParameterWriter.Pending pending, bool instance, GlobalParameterWriter.PropertyValue? driven = null)
    {
        if (definition is not { Length: >= 2 })
            throw ParameterWriter.Unverified("The family parameter definition record was not found.");
        string? definitionClass = walker.Class(BinaryPrimitives.ReadUInt16LittleEndian(definition))?.Name;
        if (definitionClass is not ("ParamElemFamily" or "ParamElemExternal"))
            throw new RvtWriteRefusedException("unsupported-parameter", "The definition is outside the loaded family parameter scope.");
        var (def, slot) = ReadDefinition(walker, definition, edit);
        int mode = instance ? 1 : 0;
        if ((definitionClass == "ParamElemFamily" && Integer(def, "ParamElemFamily.m_instanceParam") != mode)
            || (definitionClass == "ParamElemExternal" && Integer(def, WideBlockWalker.AllItemsKeyPrefix + "bindings#") != 0))
            throw ParameterWriter.Unverified("The definition belongs to a different parameter scope.");
        bool familyTypeReference = walker.Class(def.Queue[slot])?.Name == "ParamDefFamType";
        (StorageType Storage, byte[] Replacement, UnitType? Quantity)? encoded = driven is { } property
            ? PrepareDriven(property)
            : familyTypeReference ? null : Encode(walker, def, slot, edit, idWidth, material, includeCurrency: true);
        if (!instance && Integer(walk, "FamilySymbol.m_masterId") != -1)
            throw ParameterWriter.Unverified("The family element's master and effective symbols could not be verified.");
        var (familyWalk, display, symbols, masterIds) = ReadOwnership(root, tags, idWidth, walker, elementIds,
            category, edit.ElementId, walk, pending, instance);
        var owner = new ProjectRecord(partition, edit.ElementId, body, BinaryPrimitives.ReadUInt16LittleEndian(body), walk);
        var declaration = ReadStore(walker, familyWalk, "FamilyBase.m_familyParams");
        var declared = FamilyFormulaPropagation.FindFormulaRow(declaration, edit.ParameterId);
        bool inverse = !instance && declared.Fields[1].Integer != 0;
        if (inverse && driven is not null)
            throw ParameterWriter.Unverified("A global parameter cannot update a calculated family property directly.");
        if (!inverse)
            _ = FindRow(declaration, edit.ParameterId, mode);
        var trivial = familyWalk.Captures.Where(c => c.AtRoot &&
            c.Key.StartsWith(TrivialIds + "@", StringComparison.Ordinal)).ToArray();
        if (Integer(familyWalk, TrivialIds + "#") != trivial.Length
            || trivial.Count(c => c.Integer == edit.ParameterId) != 1)
            throw ParameterWriter.Unverified("The family does not classify this parameter as independent of geometry.");

        if (familyTypeReference)
        {
            if (Integer(display.Walk, "FamilySymbol.m_ownerInstanceId") != -1)
                throw ParameterWriter.Unverified("The effective family symbol is outside the verified family type reference scope.");
            bool independentType = !instance && definitionClass == "ParamElemFamily" && !inverse
                && !UsesFormula(familyWalk) && !UsesFormula(walk);
            VerifyFamilyReferenceOwner(familyWalk, reference, projectReference, edit, independentType);
        }
        var (storage, replacement, quantity) = encoded ?? EncodeFamilyTypeReference(def, slot, edit, idWidth, material, reference, projectReference);
        var records = new List<RvtPartitionRecordEdit>();
        string pointer = instance ? "FamilyInstance.m_pInstParams" : "FamilySymbol.m_pParams";
        // Revit writes the instance flag on the family declaration, but clears it in the instance override store.
        var store = ReadStore(walker, walk, pointer);
        var storedRow = inverse ? null : FindRow(store, edit.ParameterId, 0, required: !instance || driven is not null);
        if (driven is { } expected)
        {
            var saved = Location(storedRow!, storage, idWidth);
            if (!body.AsSpan(saved.Offset, saved.Length).SequenceEqual(expected.Previous))
                throw ParameterWriter.Unverified("The controlled family property disagrees with the saved global value.");
            VerifySeparateNamedValue(walk, edit.ParameterId);
        }
        var displayWalk = instance ? walker.Run(display.Body, 0, display.Body.Length, display.ClassId) : walk;
        if (!inverse && (quantity == UnitType.UT_Currency || ParameterMeasurement.IsAdditionalSpec(quantity)))
        {
            var effectiveRow = storedRow ?? FindRow(ReadStore(walker, displayWalk, "FamilySymbol.m_pParams"), edit.ParameterId, 1)!;
            var effectiveBody = storedRow is null ? display.Body : body;
            var effectiveValue = Location(effectiveRow, storage, idWidth);
            ParameterFormulaEvaluator.VerifyStableChange(BinaryPrimitives.ReadDoubleLittleEndian(effectiveBody.AsSpan(effectiveValue.Offset)),
                BinaryPrimitives.ReadDoubleLittleEndian(replacement));
        }
        if (familyTypeReference)
        {
            VerifySeparateNamedValue(walk, edit.ParameterId);
            if (displayWalk != walk)
                VerifySeparateNamedValue(displayWalk, edit.ParameterId);
        }
        if (instance)
        {
            GlobalParameterWriter.VerifyElementParameter(walker, displayWalk, idWidth, elementIds, globals, edit.ParameterId);
            CheckFormulaDependencies(walk, edit.ParameterId);
        }
        if (inverse || UsesFormula(familyWalk) || UsesFormula(displayWalk))
            return PlanFormula();
        if (storedRow is null)
        {
            var inherited = FindRow(ReadStore(walker, displayWalk, "FamilySymbol.m_pParams"), edit.ParameterId, 1)!;
            var inheritedValue = Location(inherited, storage, idWidth);
            if (display.Body.AsSpan(inheritedValue.Offset, inheritedValue.Length).SequenceEqual(replacement))
                return null;

            // An undriven NamedParam has no deferred payload. Its first and last fields delimit the inline row.
            int rowStart = inherited.Fields[0].ValueOffset;
            int rowEnd = inherited.Fields[7].ValueOffset + 1;
            int flag = inherited.Fields[6].ValueOffset;
            if (rowStart < 0 || flag < rowStart || flag >= rowEnd || inheritedValue.Offset + inheritedValue.Length > flag)
                throw ParameterWriter.Unverified("The inherited parameter does not have a verified inline layout.");
            byte[] row = display.Body[rowStart..rowEnd];
            row[flag - rowStart] = 0;
            row = ParameterWriter.ReplaceValue(row, inheritedValue.Offset - rowStart, inheritedValue.Length, replacement);
            byte[] updated = InsertNamedParam(body, store, row);
            VerifyUpdated(updated, owner.ClassId);
            return new ParameterWriter.PlanResult([new(partition, 102, (ulong)edit.ElementId, updated)], null);
        }
        var original = Location(storedRow, storage, idWidth);
        ReadOnlySpan<byte> originalBytes = body.AsSpan(original.Offset, original.Length);
        AddReplacement(owner, original);
        if (!instance)
        {
            long? familyId = Integer(familyWalk, "m_id");
            foreach (var (id, symbol) in symbols)
            {
                if (id == edit.ElementId)
                    continue;
                if (symbol is null || masterIds.Contains(id) || Integer(symbol.Walk, "FamilySymbol.m_familyId") != familyId
                    || (familyTypeReference && Integer(symbol.Walk, "FamilySymbol.m_ownerInstanceId") != -1))
                    throw ParameterWriter.Unverified("A derived family symbol could not be verified.");
                var full = walker.Run(symbol.Body, 0, symbol.Body.Length, symbol.ClassId);
                GlobalParameterWriter.VerifyElementParameter(walker, full, idWidth, elementIds, globals, edit.ParameterId);
                if (driven is not null || familyTypeReference)
                    VerifySeparateNamedValue(full, edit.ParameterId);
                CheckFormulaDependencies(full, edit.ParameterId);
                if (driven is not null)
                    continue;
                var stored = Location(FindRow(ReadStore(walker, full, pointer), edit.ParameterId, 0)!, storage, idWidth);
                if (!symbol.Body.AsSpan(stored.Offset, stored.Length).SequenceEqual(originalBytes))
                    throw ParameterWriter.Unverified("The parameter value of a derived symbol disagrees with its master.");
                AddReplacement(symbol, stored);
            }
        }
        return records.Count == 0 ? null : new ParameterWriter.PlanResult(records, null);

        (StorageType Storage, byte[] Replacement, UnitType? Quantity) PrepareDriven(GlobalParameterWriter.PropertyValue value)
        {
            var format = FormulaParameterStorage.Read(walker, def, slot, includeCurrency: true);
            value.VerifyFormat(format, allowSlopeGlobalToNumber: true);
            return (format.Storage, value.Replacement, format.Unit);
        }

        static void VerifySeparateNamedValue(WideBlockWalker.Walk record, long id)
        {
            if (record.Captures.Any(field => field.Field == "m_paramId" && field.Integer == id &&
                field.DeclaringClass.StartsWith("ParamValue", StringComparison.Ordinal)))
                throw ParameterWriter.Unverified("The named family property has a competing typed store.");
        }

        void AddReplacement(ProjectRecord record, (int Offset, int Length) value)
        {
            if (record.Body.AsSpan(value.Offset, value.Length).SequenceEqual(replacement))
                return;
            byte[] updated = ParameterWriter.ReplaceValue(record.Body, value.Offset, value.Length, replacement);
            VerifyUpdated(updated, record.ClassId);
            records.Add(new(record.Partition, 102, (ulong)record.Id, updated));
        }

        void VerifyUpdated(byte[] updated, int classId)
        {
            var readBack = Location(FindRow(ReadStore(walker, walker.Run(updated, 0, updated.Length, classId), pointer),
                edit.ParameterId, 0)!, storage, idWidth);
            if (!updated.AsSpan(readBack.Offset, readBack.Length).SequenceEqual(replacement))
                throw ParameterWriter.Unverified("The updated family parameter could not be read back.");
        }

        bool UsesFormula(WideBlockWalker.Walk value) =>
            value.Captures.Any(c => c.Key == "formula-reference" && c.Integer == edit.ParameterId);

        ParameterWriter.PlanResult? PlanFormula()
        {
            var familyId = Integer(familyWalk, "m_id");
            var declaredDependencies = FamilyFormulaPropagation.Dependencies(familyWalk, declaration);
            if (!inverse && UsesFormula(familyWalk) && !declaredDependencies.ContainsKey(edit.ParameterId))
                throw ParameterWriter.Unverified("The loaded family formula input is referenced outside its declared formulas.");
            var independent = trivial.Select(c => c.Integer).ToHashSet();
            var ids = declaration.Rows.Where(row => row.Id > 0).Select(row => row.Id).ToHashSet();
            var definitions = new Dictionary<long, byte[]>();
            ParameterWriter.ScanPartitions(root, tags, idWidth, (number, record) =>
            {
                if (record.Block == 0 && record.Channel == 102 && ids.Contains((long)record.Owner) &&
                    !definitions.TryAdd((long)record.Owner, record.Body.ToArray()))
                    throw ParameterWriter.Unverified("A formula parameter has more than one project definition.");
            }, pending);
            var formats = new Dictionary<long, FormulaParameterStorage>();
            var input = new ValueEdit(edit.ParameterId, storage, replacement, mode);
            ParameterFormulaEvaluator.Linear? equation = null;
            if (inverse)
            {
                equation = ReadEquation(familyWalk, declaration);
                if (ReadEquation(walk, store) != equation)
                    throw ParameterWriter.Unverified("The loaded type formula disagrees with its family declaration.");
                var resultFormat = ReadFormat(edit.ParameterId);
                var requested = resultFormat.Decode(replacement);
                var formula = equation.Value;
                var inputFormat = ReadFormat(formula.InputId);
                if (formula.Scale == 0 || requested.Kind != ParameterFormulaEvaluator.ValueKind.Number || requested.Unit != formula.Offset.Unit)
                    throw ParameterWriter.Unverified("The requested formula result does not have a verified inverse.");
                _ = FindRow(declaration, formula.InputId, 0);
                var outputLocation = Location(FamilyFormulaPropagation.FindFormulaRow(store, edit.ParameterId), resultFormat.Storage, idWidth);
                var inputLocation = Location(FindRow(store, formula.InputId, 0)!, inputFormat.Storage, idWidth);
                bool unchangedResult = body.AsSpan(outputLocation.Offset, outputLocation.Length).SequenceEqual(replacement);
                double solved = unchangedResult ? inputFormat.Decode(body.AsSpan(inputLocation.Offset, inputLocation.Length)).Number
                    : (requested.Number - formula.Offset.Number) / formula.Scale;
                if (inputFormat.Storage == StorageType.Double && (!formula.ExactScaling || inputFormat.Unit != resultFormat.Unit ||
                    solved == 0 && requested.Number != 0 || double.IsSubnormal(solved) || double.IsSubnormal(requested.Number)))
                    throw ParameterWriter.Unverified("The real-valued inverse does not have verified exact scaling.");
                if (!double.IsFinite(solved))
                    throw new RvtWriteRefusedException("invalid-parameter-value", "The formula inverse does not produce a finite input.");
                var encoded = inputFormat.Encode(new(solved, inputFormat.FormulaUnit));
                input = new(formula.InputId, inputFormat.Storage, encoded, 0);
            }
            if (instance)
            {
                VerifyFormulaRecord(walk, edit.ElementId);
                VerifyFormulaRecord(displayWalk, display.Id);
                if (Integer(displayWalk, "FamilySymbol.m_ownerInstanceId") is not long displayOwner ||
                    displayOwner != -1 && displayOwner != edit.ElementId)
                    throw ParameterWriter.Unverified("The effective formula symbol belongs to another instance.");
                var symbolRows = ReadStore(walker, displayWalk, "FamilySymbol.m_pParams");
                _ = FindRow(symbolRows, edit.ParameterId, 1);
                var source = new FormulaSymbol(display with { Walk = displayWalk }, symbolRows);
                var updates = Recalculate(source, new(body, store));
                foreach (var update in updates)
                    CheckFormulaDependencies(walk, update.Id);
                updates.Add(new(edit.ParameterId, storage, replacement, 1));
                WriteFormulaValues(owner, pointer, store, updates.Select(update => update with { Mode = 0 }));
                return records.Count == 0 ? null : new ParameterWriter.PlanResult(records, null);
            }

            var sources = new Dictionary<long, FormulaSymbol>();
            var seenInstances = new HashSet<ulong>();
            bool hasInstanceResults = false;
            var original = Location(FindRow(store, input.Id, 0)!, input.Storage, idWidth);

            foreach (var (id, record) in symbols)
            {
                if (record is null || id != edit.ElementId && (masterIds.Contains(id) ||
                    Integer(record.Walk, "FamilySymbol.m_familyId") != familyId ||
                    Integer(record.Walk, "FamilySymbol.m_ownerInstanceId") != -1))
                    throw ParameterWriter.Unverified("A derived formula symbol could not be verified.");
                // Global edits write the associated primary type, even when placements use derived copies.
                if (driven is not null && id != edit.ElementId)
                    continue;
                var full = id == edit.ElementId ? walk : walker.Run(record.Body, 0, record.Body.Length, record.ClassId);
                VerifyFormulaRecord(full, id);
                var rows = id == edit.ElementId ? store : ReadStore(walker, full, pointer);
                if (equation is not null && id != edit.ElementId && ReadEquation(full, rows) != equation)
                    throw ParameterWriter.Unverified("A derived symbol has a different formula inverse from its master.");
                var before = Location(FindRow(rows, input.Id, 0)!, input.Storage, idWidth);
                if (!record.Body.AsSpan(before.Offset, before.Length).SequenceEqual(body.AsSpan(original.Offset, original.Length)))
                    throw ParameterWriter.Unverified("The derived formula input disagrees with its master.");

                var source = new FormulaSymbol(record with { Walk = full }, rows);
                sources.Add(id, source);
                var results = Recalculate(source);
                if (inverse && !results.Any(result => result.Id == edit.ParameterId &&
                    (input.Storage == StorageType.Integer || result.Bytes.AsSpan().SequenceEqual(replacement))))
                    throw ParameterWriter.Unverified("The verified forward calculation does not reproduce the formula inverse.");
                hasInstanceResults |= results.Any(result => result.Mode == 1);
                results.Add(input);
                WriteFormulaValues(source.Record, pointer, rows, results);
            }

            if (hasInstanceResults)
                ParameterWriter.ScanPartitions(root, tags, idWidth, ReadInstance, pending);
            return records.Count == 0 ? null : new ParameterWriter.PlanResult(records, null);

            ParameterFormulaEvaluator.Linear ReadEquation(WideBlockWalker.Walk record, FamilyStore rows)
            {
                var linear = Resolve(edit.ParameterId, ParameterFormulaEvaluator.MaxDepth);
                if (linear.InputId <= 0)
                    throw ParameterWriter.Unverified("The loaded type formula does not have one writable input.");
                return linear;

                ParameterFormulaEvaluator.Linear Resolve(long id, int depth)
                {
                    var row = FamilyFormulaPropagation.FindFormulaRow(rows, id);
                    var format = ReadFormat(id);
                    if (row.Instance != 0 || format.Kind != ParameterFormulaEvaluator.ValueKind.Number || !independent.Contains(id))
                        throw ParameterWriter.Unverified("The formula inverse contains an unverified type parameter.");
                    if (row.Fields[1].Integer == 0)
                        return ParameterFormulaEvaluator.Linear.Input(id, format.FormulaUnit);
                    var value = ParameterFormulaEvaluator.ReadLinear(walker, record, row.Fields[1], depth, Resolve);
                    if (value.Offset.Unit != format.FormulaUnit)
                        throw ParameterWriter.Unverified("A formula inverse has incompatible units.");
                    if (id != edit.ParameterId && format.Storage == StorageType.Integer &&
                        (value.InputId <= 0 || ReadFormat(value.InputId).Storage != StorageType.Integer ||
                         value.Scale != Math.Truncate(value.Scale) || value.Offset.Number != Math.Truncate(value.Offset.Number)))
                        throw ParameterWriter.Unverified("A formula inverse would have to reverse an intermediate rounding step.");
                    return value;
                }
            }

            void ReadInstance(int number, PartitionFraming.Record record)
            {
                if (record.Block != 0 || record.Channel != 102 || record.Body.Length < 2 || !elementIds.Contains((long)record.Owner))
                    return;
                int id = BinaryPrimitives.ReadUInt16LittleEndian(record.Body.Span);
                if (walker.Class(id)?.Name != "FamilyInstance")
                    return;
                var data = record.Body.ToArray();
                var header = walker.Run(data, 0, data.Length, id, rootOnly: true);
                if (!header.RootRead || Integer(header, "FamilyInstance.m_masterSymbolId") is not long master)
                    throw ParameterWriter.Unverified("A family instance's master symbol link could not be verified.");
                if (master != edit.ElementId)
                    return;
                if (!seenInstances.Add(record.Owner))
                    throw ParameterWriter.Unverified("A dependent family instance has more than one project record.");
                var full = walker.Run(data, 0, data.Length, id);
                VerifyFormulaRecord(full, (long)record.Owner);
                long effective = InstanceSymbol(walker, full);
                if (Integer(full, "FamilyInstance.m_superInstanceId") != -1 || !symbols.ContainsKey(effective) ||
                    !sources.TryGetValue(driven is null ? effective : edit.ElementId, out var source))
                    throw ParameterWriter.Unverified("A dependent family instance is nested or has an unverified effective type.");
                const string instancePointer = "FamilyInstance.m_pInstParams";
                var rows = ReadStore(walker, full, instancePointer);
                var results = Recalculate(source, new(data, rows));
                WriteFormulaValues(new(number, (long)record.Owner, data, id, full), instancePointer, rows,
                    results.Select(result => result with { Mode = 0 }));
            }

            List<ValueEdit> Recalculate(FormulaSymbol source, FamilyFormulaPropagation.InstanceValues? values = null)
            {
                return FamilyFormulaPropagation.Recalculate(walker, source.Record.Walk, source.Record.Body, [source.Values],
                    source.Values, null, input.Id, input.Bytes, idWidth,
                    id => independent.Contains(id) && (!instance || FamilyFormulaPropagation.FindFormulaRow(declaration, id).Instance == 1), ReadSymbolFormat,
                    id => declaredDependencies.GetValueOrDefault(id), values)
                    .Select(result => new ValueEdit(result.Id, result.Format.Storage, result.Replacement,
                        (int)FamilyFormulaPropagation.FindFormulaRow(source.Values, result.Id).Instance)).ToList();

                FormulaParameterStorage ReadSymbolFormat(long id)
                {
                    if (FamilyFormulaPropagation.FindFormulaRow(source.Values, id).Instance !=
                        FamilyFormulaPropagation.FindFormulaRow(declaration, id).Instance)
                        throw ParameterWriter.Unverified("A loaded formula value's scope disagrees with its family declaration.");
                    return ReadFormat(id);
                }
            }

            FormulaParameterStorage ReadFormat(long id)
            {
                if (formats.TryGetValue(id, out var format))
                    return format;
                if (!definitions.TryGetValue(id, out var data) || data.Length < sizeof(ushort))
                    throw ParameterWriter.Unverified("A loaded formula parameter definition was not found.");
                var type = walker.Class(BinaryPrimitives.ReadUInt16LittleEndian(data))?.Name;
                if (type is not ("ParamElemFamily" or "ParamElemExternal"))
                    throw ParameterWriter.Unverified("A formula references a parameter outside the loaded family definition.");
                var (decoded, index) = ReadDefinition(walker, data, new ParameterWriter.Edit(edit.ElementId, id, null));
                long mode = FamilyFormulaPropagation.FindFormulaRow(declaration, id).Instance;
                if (type == "ParamElemFamily" ? Integer(decoded, "ParamElemFamily.m_instanceParam") != mode :
                    Integer(decoded, WideBlockWalker.AllItemsKeyPrefix + "bindings#") != 0)
                    throw ParameterWriter.Unverified("A loaded formula parameter's definition disagrees with its family declaration.");
                format = FormulaParameterStorage.Read(walker, decoded, index, includeCurrency: true);
                formats.Add(id, format);
                return format;
            }

            void WriteFormulaValues(ProjectRecord target, string storePointer, FamilyStore rows, IEnumerable<ValueEdit> updates)
            {
                var locations = updates.Select(update => (Update: update, Location: Location(Row(rows, update), update.Storage, idWidth)))
                    .OrderByDescending(item => item.Location.Offset).ToArray();
                byte[] data = target.Body;
                foreach (var (update, location) in locations)
                {
                    long? controlling = target.Id == edit.ElementId && update.Id == edit.ParameterId ? driven?.GlobalId : null;
                    GlobalParameterWriter.VerifyElementParameter(walker, target.Walk, idWidth, elementIds, globals, update.Id, controlling);
                    if (driven is not null)
                        VerifySeparateNamedValue(target.Walk, update.Id);
                    if (!target.Body.AsSpan(location.Offset, location.Length).SequenceEqual(update.Bytes))
                        data = ParameterWriter.ReplaceValue(data, location.Offset, location.Length, update.Bytes);
                }
                if (ReferenceEquals(data, target.Body))
                    return;
                var readBack = ReadStore(walker, walker.Run(data, 0, data.Length, target.ClassId), storePointer);
                foreach (var (update, _) in locations)
                {
                    var location = Location(Row(readBack, update), update.Storage, idWidth);
                    if (!data.AsSpan(location.Offset, location.Length).SequenceEqual(update.Bytes))
                        throw ParameterWriter.Unverified("An updated loaded formula value could not be read back.");
                }
                records.Add(new(target.Partition, 102, (ulong)target.Id, data));

                static NamedValue Row(FamilyStore rows, ValueEdit update)
                {
                    var value = FamilyFormulaPropagation.FindFormulaRow(rows, update.Id);
                    if (value.Instance != update.Mode)
                        throw ParameterWriter.Unverified("A loaded formula value's instance or type scope disagrees with its definition.");
                    return value;
                }
            }

            void VerifyFormulaRecord(WideBlockWalker.Walk record, long id)
            {
                if (!record.Exact || record.EmptyEntityAt >= 0 || Integer(record, "m_id") != id ||
                    record.Queue.Any(tag => walker.IsA(tag, "GroupMembership")))
                    throw ParameterWriter.Unverified("A loaded formula record could not be verified or belongs to a group.");
            }
        }
    }

    internal static bool TryPlanTypeIdentity(
        RootStorage root, PartitionFraming.Tags tags, int idWidth, WideBlockWalker walker, IReadOnlySet<long> elementIds, IReadOnlySet<long> globals,
        int? category, ParameterWriter.Edit edit, WideBlockWalker.Walk walk, StorageType storage, byte[] replacement,
        ParameterWriter.Pending pending, GlobalParameterWriter.PropertyValue? driven, out ParameterWriter.PlanResult? plan)
    {
        plan = null;
        var (family, target, symbols, masterIds) = ReadOwnership(root, tags, idWidth, walker, elementIds, category, edit.ElementId, walk, pending, instance: false);
        bool derivedTarget = Integer(target.Walk, "FamilySymbol.m_masterId") is > 0;
        if ((symbols.Count != 1 && category != (int)BuiltInCategory.OST_GenericModel)
            || (derivedTarget && (driven is null || masterIds.Contains(target.Id)))
            || family.EmptyEntityAt >= 0 || walk.EmptyEntityAt >= 0)
            throw ParameterWriter.Unverified("The loaded identity field requires a verified primary symbol and supported derived copies.");
        CheckFormulaDependencies(family, edit.ParameterId);
        CheckFormulaDependencies(walk, edit.ParameterId);

        var declaration = FindRow(ReadStore(walker, family, "FamilyBase.m_familyParams"), edit.ParameterId, 0, required: false);
        const string pointer = "FamilySymbol.m_pParams";
        var row = FindRow(ReadStore(walker, walk, pointer), edit.ParameterId, 0, required: false);
        if ((declaration is null) != (row is null)
            || CountLocalReferences(walk) != (row is null ? 0 : 1))
            throw ParameterWriter.Unverified("The loaded identity field has an ambiguous parameter store.");
        if (row is null)
        {
            if (symbols.Count != 1)
                throw ParameterWriter.Unverified("The loaded identity field has no verified family value to mirror.");
            return false;
        }

        if (!HasIdentityStorage(declaration!) || !HasIdentityStorage(row) || ParameterWriter.FindValue(walker, walk, edit, storage) is not null)
            throw ParameterWriter.Unverified("The loaded identity field has an unverified value or competing typed store.");

        var records = new List<RvtPartitionRecordEdit>();
        var original = Location(row, storage, idWidth);
        var originalValue = target.Body.AsSpan(original.Offset, original.Length);
        if (driven is { } expected && !originalValue.SequenceEqual(expected.Previous))
            throw ParameterWriter.Unverified("The controlled family identity value disagrees with the saved global value.");
        ReplaceNamedValue(target, original);
        foreach (var (id, symbol) in symbols)
        {
            if (derivedTarget || id == target.Id)
                continue;
            if (symbol is null || masterIds.Contains(id)
                || Integer(symbol.Walk, "FamilySymbol.m_familyId") != Integer(family, "m_id")
                || Integer(symbol.Walk, "FamilySymbol.m_ownerInstanceId") != -1)
                throw ParameterWriter.Unverified("A derived family symbol could not be verified for the identity edit.");
            var full = walker.Run(symbol.Body, 0, symbol.Body.Length, symbol.ClassId);
            if (driven is null)
                GlobalParameterWriter.VerifyElementParameter(walker, full, idWidth, elementIds, globals, edit.ParameterId);
            CheckFormulaDependencies(full, edit.ParameterId);
            var copy = FindRow(ReadStore(walker, full, pointer), edit.ParameterId, 0)!;
            var position = Location(copy, storage, idWidth);
            if (full.EmptyEntityAt >= 0 || CountLocalReferences(full) != 1 || !HasIdentityStorage(copy)
                || ParameterWriter.FindValue(walker, full, edit, storage) is not null
                || full.Queue.Any(tag => walker.IsA(tag, "GroupMembership")))
                throw ParameterWriter.Unverified("A stored identity copy has an unverified layout.");
            if (driven is not null)
                continue;
            if (!symbol.Body.AsSpan(position.Offset, position.Length).SequenceEqual(originalValue))
                throw ParameterWriter.Unverified("A stored identity copy cannot be verified against its primary symbol.");
            ReplaceNamedValue(symbol, position);
        }
        if (records.Count != 0)
            plan = new ParameterWriter.PlanResult(records, null);
        return true;

        void ReplaceNamedValue(ProjectRecord owner, (int Offset, int Length) location)
        {
            if (owner.Body.AsSpan(location.Offset, location.Length).SequenceEqual(replacement))
                return;
            byte[] updated = ParameterWriter.ReplaceValue(owner.Body, location.Offset, location.Length, replacement);
            var verified = walker.Run(updated, 0, updated.Length, owner.ClassId);
            var readBack = Location(FindRow(ReadStore(walker, verified, pointer), edit.ParameterId, 0)!, storage, idWidth);
            if (readBack.Length != replacement.Length || CountLocalReferences(verified) != 1
                || !updated.AsSpan(readBack.Offset, readBack.Length).SequenceEqual(replacement))
                throw ParameterWriter.Unverified("The loaded identity value could not be read back.");
            records.Add(new(owner.Partition, 102, (ulong)owner.Id, updated));
        }

        int CountLocalReferences(WideBlockWalker.Walk owner) => owner.Captures.Count(c => c.Field == "m_paramId"
            && c.Integer == edit.ParameterId && !c.DeclaringClass.StartsWith("ParamValue", StringComparison.Ordinal));

        bool HasIdentityStorage(NamedValue value) => value.Fields[3].Integer == -1 && value.Fields[5].Integer == 0
            && (storage == StorageType.String ? value.Fields[2].Real == 0 : value.Fields[0].Text == "");
    }

    internal static void VerifyLocalValueEdit(
        RootStorage root, PartitionFraming.Tags tags, int idWidth, WideBlockWalker walker, IReadOnlySet<long> elementIds,
        int? category, ParameterWriter.Edit edit, WideBlockWalker.Walk walk, StorageType storage, WideBlockWalker.Walk? reference,
        ProjectFamilyReference? projectReference, ParameterWriter.Pending pending, bool instance)
    {
        var (family, display, symbols, masterIds) = ReadOwnership(root, tags, idWidth, walker, elementIds, category, edit.ElementId, walk, pending, instance);
        if (family.EmptyEntityAt >= 0)
            throw ParameterWriter.Unverified("The loaded family declarations could not be read exactly.");
        if (storage == StorageType.ElementId)
            VerifyFamilyReferenceOwner(family, reference, projectReference, edit);
        if (ReadStore(walker, family, "FamilyBase.m_familyParams").Rows.Any(row => row.Id == edit.ParameterId))
        {
            if (!edit.UserDefined)
                throw ParameterWriter.Unverified("The built-in parameter also has a value in the family store.");
            throw new RvtWriteRefusedException(edit.Value is null ? "unsupported-parameter-clear" : "unsupported-parameter",
                "This edit requires a parameter value separate from the loaded family declaration.");
        }
        if (instance)
        {
            VerifyStore(walk);
            return;
        }

        // Revit edits the addressed symbol's own value in the element store.
        long masterId = Integer(display.Walk, "FamilySymbol.m_masterId") is > 0 and long master ? master : display.Id;
        long? familyId = Integer(family, "m_id");
        foreach (var (id, symbol) in symbols)
        {
            bool primary = id == masterId;
            if (primary && id != edit.ElementId)
                continue;
            if (symbol is null || (!primary && (masterIds.Contains(id) || Integer(symbol.Walk, "FamilySymbol.m_familyId") != familyId
                || Integer(symbol.Walk, "FamilySymbol.m_ownerInstanceId") != -1)))
                throw ParameterWriter.Unverified("A derived family symbol could not be verified for editing.");
            var symbolWalk = id == edit.ElementId ? walk : walker.Run(symbol.Body, 0, symbol.Body.Length, symbol.ClassId);
            VerifyStore(symbolWalk);
            _ = ParameterWriter.FindValue(walker, symbolWalk, edit, storage);
        }

        void VerifyStore(WideBlockWalker.Walk owner)
        {
            if (!owner.Exact || owner.EmptyEntityAt >= 0
                || owner.Captures.Any(c => c.Field == "m_paramId" && c.Integer == edit.ParameterId
                    && !c.DeclaringClass.StartsWith("ParamValue", StringComparison.Ordinal)))
                throw ParameterWriter.Unverified("A family element has an unverified parameter store.");
        }
    }

    private static (WideBlockWalker.Walk Family, ProjectRecord Display, Dictionary<long, ProjectRecord?> Symbols, HashSet<long> MasterIds)
        ReadOwnership(RootStorage root, PartitionFraming.Tags tags, int idWidth, WideBlockWalker walker, IReadOnlySet<long> elementIds,
            int? category, long elementId, WideBlockWalker.Walk walk, ParameterWriter.Pending pending, bool instance)
    {
        if (category is not < -1 || (instance && Integer(walk, "FamilyInstance.m_superInstanceId") != -1))
            throw ParameterWriter.Unverified("A main-model, top-level family element could not be verified.");

        long masterId = instance ? Integer(walk, "FamilyInstance.m_masterSymbolId") ?? -1
            : Integer(walk, "FamilySymbol.m_masterId") switch { -1 => elementId, > 0 and long linked => linked, _ => -1 };
        long displayId = instance ? InstanceSymbol(walker, walk) : elementId;
        if (masterId <= 0 || displayId <= 0 || !elementIds.Contains(masterId) || !elementIds.Contains(displayId)
            || (!instance && Integer(walk, "FamilySymbol.m_ownerInstanceId") != -1))
            throw ParameterWriter.Unverified("The family element's master and effective symbols could not be verified.");

        var families = new Dictionary<long, byte[]?>();
        var symbols = new Dictionary<long, ProjectRecord?>();
        var masterIds = new HashSet<long>();
        ParameterWriter.ScanPartitions(root, tags, idWidth, (number, record) =>
        {
            if (record.Block != 0 || record.Channel != 102 || record.Body.Length < 2 || !elementIds.Contains((long)record.Owner))
                return;
            int cls = BinaryPrimitives.ReadUInt16LittleEndian(record.Body.Span);
            string? name = walker.Class(cls)?.Name;
            long id = (long)record.Owner;
            if (name == "Family")
            {
                if (!families.TryAdd(id, record.Body.ToArray()))
                    families[id] = null;
                return;
            }
            if (name != "FamilySymbol" || (instance && id != masterId && id != displayId))
                return;
            byte[] data = record.Body.ToArray();
            var symbol = walker.Run(data, 0, data.Length, cls, rootOnly: true);
            if (!symbol.RootRead || Integer(symbol, "m_id") != id
                || Integer(symbol, "FamilySymbol.m_masterId") is not long master)
                throw ParameterWriter.Unverified("A family symbol's master link could not be read exactly.");
            if (master > 0)
                masterIds.Add(master);
            if (id == masterId || id == displayId || master == masterId)
            {
                if (!symbols.TryAdd(id, new(number, id, data, cls, symbol)))
                    symbols[id] = null;
            }
        }, pending);
        if (!symbols.TryGetValue(masterId, out var masterSymbol) || masterSymbol is null
            || Integer(masterSymbol.Walk, "FamilySymbol.m_masterId") != -1
            || Integer(masterSymbol.Walk, "FamilySymbol.m_ownerInstanceId") != -1
            || Integer(masterSymbol.Walk, "FamilySymbol.m_familyId") is not long familyId
            || !families.TryGetValue(familyId, out var family) || family is null
            || !symbols.TryGetValue(displayId, out var display) || display is null
            || Integer(display.Walk, "FamilySymbol.m_familyId") != familyId
            || (displayId != masterId && Integer(display.Walk, "FamilySymbol.m_masterId") != masterId))
            throw ParameterWriter.Unverified("The active family and its symbols could not be identified uniquely.");

        var familyWalk = walker.Run(family, 0, family.Length, BinaryPrimitives.ReadUInt16LittleEndian(family));
        if (!familyWalk.Exact || Integer(familyWalk, "m_id") != familyId || Integer(familyWalk, "FamilyBase.m_inPlace") != 0
            || Integer(familyWalk, "FamilyBase.m_categoryId") != category)
            throw ParameterWriter.Unverified("The loaded family's ownership or category could not be verified.");
        return (familyWalk, display, symbols, masterIds);
    }

    private static long InstanceSymbol(WideBlockWalker walker, WideBlockWalker.Walk walk)
    {
        var instanceInfo = walk.Captures.Where(c => c.Key == "InstInfoBase.m_symbolId" && c.QueueIndex >= 0
            && walker.IsA(walk.Queue[c.QueueIndex], "InstanceInfo")).ToArray();
        return instanceInfo is [var value] ? value.Integer : -1;
    }

    internal static void CheckFormulaDependencies(WideBlockWalker.Walk walk, long parameterId)
    {
        if (walk.Captures.Any(c => c.Key == "formula-reference" && c.Integer == parameterId))
            throw ParameterWriter.Unverified("The parameter is used by a saved family formula.");
    }

    internal static FamilyStore ReadStore(WideBlockWalker walker, WideBlockWalker.Walk walk, string pointer)
    {
        if (!walk.Exact || Integer(walk, pointer) is not long tag
            || walker.Class((int)tag)?.Name != "FamilyParams"
            || walk.Queue.Select((cls, index) => (cls, index)).Where(p => p.cls == tag).ToArray() is not [{ index: var slot }])
            throw ParameterWriter.Unverified("The element's own FamilyParams store could not be read exactly.");
        return ReadStore(walk.Captures.Where(c => c.QueueIndex == slot));
    }

    internal static FamilyStore ReadStore(IEnumerable<WideBlockWalker.Capture> captures)
    {
        var items = captures.ToArray();
        if (items.Where(c => c.Key == "FamilyParams.m_params#").ToArray() is not [{ ValueOffset: >= 0 } count])
            throw ParameterWriter.Unverified("The FamilyParams array count could not be read exactly.");
        var fields = items.Where(c => c.DeclaringClass == "NamedParam").ToArray();
        if (fields.Length != count.Integer * NamedFields.Length)
            throw ParameterWriter.Unverified("The NamedParam records could not be identified exactly.");
        var rows = fields.Chunk(NamedFields.Length).ToArray();
        if (rows.Any(group => !group.Select(c => c.Field).SequenceEqual(NamedFields)))
            throw ParameterWriter.Unverified("The NamedParam layout could not be verified.");
        return new FamilyStore(count.ValueOffset, rows.Select(row => new NamedValue(row)).ToArray());
    }

    internal static byte[] InsertNamedParam(byte[] body, FamilyStore store, ReadOnlySpan<byte> row)
    {
        int minimum = store.CountOffset + sizeof(uint);
        int insertion = store.Rows.Length == 0 ? minimum : store.Rows[^1].Fields[7].ValueOffset + 1;
        if (insertion < minimum || insertion > body.Length
            || BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(store.CountOffset)) != store.Rows.Length)
            throw ParameterWriter.Unverified("The family parameter array does not have a verified inline layout.");
        byte[] updated = ParameterWriter.ReplaceValue(body, insertion, 0, row);
        BinaryPrimitives.WriteInt32LittleEndian(updated.AsSpan(store.CountOffset), checked(store.Rows.Length + 1));
        return updated;
    }

    internal static NamedValue? FindRow(FamilyStore store, long parameterId, int mode, bool required = true)
    {
        var matches = store.Rows.Where(row => row.Id == parameterId).ToArray();
        if (matches.Length == 0 && !required)
            return null;
        if (matches.Length == 0)
            throw ParameterWriter.Unverified("The element has no unique locally stored family parameter value.");
        if (matches is not [var named] || named.Instance != mode)
            throw ParameterWriter.Unverified("The stored family parameter does not match its instance or type scope.");
        if (named.IsDriven)
            throw ParameterWriter.Unverified("The family parameter has a saved expression or is reporting.");
        return named;
    }

    internal static (int Offset, int Length) Location(NamedValue row, StorageType storage, int idWidth)
    {
        var value = row.Value(storage);
        int length = storage switch
        {
            StorageType.String when value.Kind == WideBlockWalker.CaptureKind.Text && value.Text is not null
                => sizeof(uint) + sizeof(char) * value.Text.Length,
            StorageType.Integer when value.Kind == WideBlockWalker.CaptureKind.Integer => sizeof(int),
            StorageType.Double when value.Kind == WideBlockWalker.CaptureKind.Real => sizeof(double),
            StorageType.ElementId when value.Kind == WideBlockWalker.CaptureKind.Integer
                && row.Fields[4].ValueOffset - value.ValueOffset == idWidth => idWidth,
            _ => 0,
        };
        if (value.ValueOffset < 0 || length == 0)
            throw ParameterWriter.Unverified("The family parameter value does not match its definition.");
        return (value.ValueOffset, length);
    }
}
