using System.Buffers.Binary;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using OpenMcdf;
using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using static RvtIO.Writing.ImportedImageReference;
using static RvtIO.Writing.LoadedFamilyParameterWriter;
using static RvtIO.Writing.ParameterWriter;
using static RvtIO.Writing.UserParameterWriter;

namespace RvtIO.Writing;

internal static class StandaloneFamilyParameterWriter
{
    internal sealed record Edit(string TypeName, long ParameterId, string? Value);
    private sealed record FamilyType(string Name, FamilyStore Values);
    private sealed record FamilyDefinition(WideBlockWalker.Walk Walk, int Slot, int Mode, Guid? SharedGuid);
    private sealed record BuiltInField(string Name, string Type, string? DisplayName = null, UnitType? Quantity = null)
    {
        internal StorageType Storage => Quantity.HasValue ? StorageType.Double : Type == "Image" ? StorageType.ElementId : StorageType.String;
        internal bool IsIdentity => Storage == StorageType.String || Quantity == UnitType.UT_Currency;
    }
    private static readonly Dictionary<long, BuiltInField> BuiltInFields = new()
    {
        [(int)BuiltInParameter.ALL_MODEL_TYPE_COMMENTS] = new("Type_Comments", "Text", "Type Comments"),
        [(int)BuiltInParameter.ALL_MODEL_DESCRIPTION] = new("Description", "Text"),
        [(int)BuiltInParameter.ALL_MODEL_MANUFACTURER] = new("Manufacturer", "Text"),
        [(int)BuiltInParameter.ALL_MODEL_MODEL] = new("Model", "Text"),
        [(int)BuiltInParameter.ALL_MODEL_URL] = new("URL", "URL"),
        [(int)BuiltInParameter.ALL_MODEL_COST] = new("Cost", "Currency", Quantity: UnitType.UT_Currency),
        [(int)BuiltInParameter.FIRE_RATING] = new("Fire_Rating", "Text", "Fire Rating"),
        [(int)BuiltInParameter.FAMILY_FREEINST_DEFAULT_ELEVATION] = new("Default_Elevation", "Length", "Default Elevation", UnitType.UT_Length),
        [(int)BuiltInParameter.ALL_MODEL_TYPE_IMAGE] = new("Type_Image", "Image", "Type Image"),
    };
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace PartAtom = "urn:schemas-autodesk-com:partatom";
    private static readonly string[] ConstraintFields =
        ["m_paramExprs", "m_drivenDimSegs", "m_additionalParamExprs", "m_dimSegDataMap"];

    internal static void Validate(long parameterId, string? value)
    {
        if (parameterId < 0)
        {
            if (!BuiltInFields.ContainsKey(parameterId))
                throw new RvtWriteRefusedException("unsupported-parameter", "This built-in family parameter has not been verified for writing.");
            if (value is null)
                throw new RvtWriteRefusedException("unsupported-parameter-clear", "Built-in family parameters require an explicit assignment.");
            ValidateText(value);
            return;
        }
        ParameterWriter.Validate(parameterId, value);
    }

    private static string? CaptureKey(SchemaClass declaring, SchemaField field) => (declaring.Name, field.Name) switch
    {
        ("AUnits", "m_formatOptionsArr") => ParameterMeasurement.LegacyFormats,
        ("ADocument", "m_ownerFamilyId" or "m_ownerFamilyContainingGroupId") or
        ("FamilyBase", "m_pFamilyTypes" or "m_bHasLabeledDimensions" or "m_contentProtected" or "m_name") or
        ("Family", "m_oFamDimConstrMgr") or
        ("FamilyTypeTable", "m_idx" or "m_pairs") or
        ("FamilyTypePair", "name") or
        ("ParamIdPair", "m_famParamId") or
        ("DimSegInfo" or "ParamExprEntry", "m_paramId") or
        ("ParamExpr", "m_entries") or
        ("ParamDef", "m_caption" or "m_typeId" or "m_userVisible") or
        ("ParamDefFamType", "m_categoryId") or
        ("ParamElemExternal", "m_externalParamKey") => declaring.Name + "." + field.Name,
        ("FamDimConstrMgrImpl", _) when ConstraintFields.Contains(field.Name) => declaring.Name + "." + field.Name,
        ("Element", "m_id") => field.Name,
        _ => ImportedImageReference.CaptureKey(declaring, field) ?? UserParameterWriter.CaptureKey(declaring, field)
            ?? LoadedFamilyParameterWriter.CaptureKey(declaring, field)
            ?? ParameterFormulaEvaluator.CaptureKey(declaring, field),
    };

    internal static PlanResult? Plan(RootStorage root, PartitionFraming.Tags tags, int idWidth, Edit edit, Pending pending)
    {
        BuiltInField? builtIn = BuiltInFields.GetValueOrDefault(edit.ParameterId);
        if (TfSchemaReader.Read(root) is not { } schema)
            throw Unverified("The family schema could not be read exactly.");
        var formats = new FormatsSchema(schema);
        var walker = new WideBlockWalker(schema, CaptureKey, ["ForgeTypeId", "FormatOptions", "AUnits"]);
        if (PagedGzipStream.Read(root, "Global", "Latest") is not { Length: >= 2 } latest
            || BinaryPrimitives.ReadUInt16LittleEndian(latest) != formats.IdOf("ADocument"))
            throw Unverified("The family document header could not be located.");
        var header = walker.Run(latest, 0, latest.Length, BinaryPrimitives.ReadUInt16LittleEndian(latest), rootOnly: true);
        if (!header.RootRead || Integer(header, "ADocument.m_ownerFamilyId") is not long familyId || familyId <= 0
            || Integer(header, "ADocument.m_ownerFamilyContainingGroupId") != -1)
            throw Unverified("The family document's owner could not be verified.");

        if (ElemTableLayout.IdWidth(formats) != idWidth || PagedGzipStream.Read(root, "Global", "ElemTable") is not { } table
            || ElemTableLayout.TryParse(table, formats) is not { } elements)
            throw Unverified("Global/ElemTable could not be read exactly.");
        var active = elements.Select(row => (ulong)row.Id).ToHashSet();
        if (!active.Contains((ulong)familyId))
            throw Unverified("The document's owner family is absent from the element table.");
        int definitionIndex = elements.FindIndex(element => element.Id == edit.ParameterId);
        if (builtIn is null && definitionIndex < 0)
            throw new RvtWriteRefusedException("missing-parameter-definition", $"Parameter definition {edit.ParameterId} is absent from the family.");

        var seen = new HashSet<ulong>();
        var materials = new Dictionary<long, string>();
        var images = new Dictionary<long, WideBlockWalker.Walk?>();
        var nestedTypes = new Dictionary<long, WideBlockWalker.Walk>();
        var nestedFamilies = new Dictionary<long, WideBlockWalker.Walk>();
        var units = new Dictionary<ulong, WideBlockWalker.Walk>();
        var definitions = new Dictionary<long, byte[]>();
        var externalReferences = new HashSet<long>();
        byte[]? body = null;
        WideBlockWalker.Walk? family = null;
        int partition = -1, familyClass = -1;
        ScanPartitions(root, tags, idWidth, (number, record) =>
        {
            if (record.Block != 0 || record.Channel != 102 || !active.Contains(record.Owner))
                return;
            if (!seen.Add(record.Owner) || record.Body.Length < 2)
                throw Unverified($"Family element {record.Owner} has an ambiguous record.");
            int cls = BinaryPrimitives.ReadUInt16LittleEndian(record.Body.Span);
            byte[] data = record.Body.ToArray();
            var walk = walker.Run(data, 0, data.Length, cls);
            if (!walk.Exact || walk.EmptyEntityAt >= 0 || Integer(walk, "m_id") != (long)record.Owner)
                throw Unverified($"Family element {record.Owner} could not be read exactly for dependency checks.");
            string? className = walker.Class(cls)?.Name;
            if (className == "MaterialElem" && MaterialName(walker, walk, (long)record.Owner) is { } name)
                materials.Add((long)record.Owner, name);
            if ((builtIn is null || builtIn.Storage == StorageType.ElementId) && className is ("ImageSymbol" or "ImageHolder"))
                images.Add((long)record.Owner, walk);
            if (className == "UnitsElem")
                units.Add(record.Owner, walk);
            bool owner = record.Owner == (ulong)familyId;
            if (!owner && builtIn is null && className == "Family")
                nestedFamilies.Add((long)record.Owner, walk);
            if (builtIn is null && className == "FamilySymbol")
                nestedTypes.Add((long)record.Owner, walk);
            if (walker.IsA(cls, "ParamElemGlobal"))
                throw Unverified("The parameter may participate in a global, element, or dimension association.");
            foreach (var capture in walk.Captures)
                if (capture.Key is "ParamIdPair.m_famParamId" or "DimSegInfo.m_paramId" or "ParamExprEntry.m_paramId" ||
                    !owner && capture.Key == "formula-reference")
                    externalReferences.Add(capture.Integer);
            if (builtIn is not null)
                CheckFormulaDependencies(walk, edit.ParameterId);
            if (owner)
            {
                if (className != "Family")
                    throw Unverified("The owner element is outside the verified family scope.");
                (body, family, partition, familyClass) = (data, walk, number, cls);
            }
            if (className is "ParamElemFamily" or "ParamElemExternal")
                definitions.Add((long)record.Owner, data);
        }, pending);
        if (!seen.SetEquals(active) || family is null || body is null)
            throw Unverified("The family element records are incomplete.");
        if (externalReferences.Contains(edit.ParameterId))
            throw Unverified("The parameter is used by an external formula or may participate in an element or dimension association.");
        long? category = Integer(family, "FamilyBase.m_categoryId");
        bool independentOfDoorConstraints = builtIn?.IsIdentity == true && category == (int)BuiltInCategory.OST_Doors;
        if (Integer(family, "FamilyBase.m_contentProtected") != 0 || Integer(family, "FamilyBase.m_bHasLabeledDimensions") != 0
            || Integer(family, "Family.m_oFamDimConstrMgr") is not long manager
            || family.Queue.Select((tag, index) => (tag, index)).Where(p => p.tag == manager).ToArray() is not [{ index: var constraintSlot }]
            || ConstraintFields.Any(name => Integer(family, "FamDimConstrMgrImpl." + name + "#", constraintSlot) is not (>= 0 and long count)
                || (!independentOfDoorConstraints && count != 0))
            || (independentOfDoorConstraints && !HasExactConstraintReferences(family, constraintSlot)))
            throw Unverified("The family has protection or dimension constraints outside the verified write scope.");

        if (builtIn is not null)
            return PlanBuiltIn(builtIn, family, body);

        var (types, current) = ReadTypes(walker, family);
        var selected = FindType(types, edit.TypeName);
        var rootStore = ReadStore(walker, family, "FamilyBase.m_familyParams");
        if (rootStore.Rows.Where(r => r.Id == edit.ParameterId).ToArray() is [var requested] && requested.Fields[1].Integer != 0)
            throw new RvtWriteRefusedException("read-only-parameter", "A formula drives the family parameter.");
        var parsed = new Dictionary<long, FamilyDefinition>();
        var input = Definition(edit.ParameterId);
        var valueEdit = new ParameterWriter.Edit(familyId, edit.ParameterId, edit.Value);
        string? definitionClass = walker.Class(input.Walk.Queue[input.Slot])?.Name;
        bool familyTypeReference = definitionClass == "ParamDefFamType";
        bool imageReference = definitionClass == "ParamDefImageSymbolBrowseEdit";
        long? reference = ReferenceId(edit.Value);
        long? material = reference.HasValue && materials.ContainsKey(reference.Value) ? reference : null;
        string? nestedReferenceName = null;
        string? imageReferenceName = null;
        (StorageType storage, byte[] replacement, UnitType? quantity) =
            familyTypeReference ? EncodeFamilyTypeReference() :
            imageReference ? EncodeImageReference() :
            Encode(walker, input.Walk, input.Slot, valueEdit, idWidth, material, familyReset: true, includeCurrency: true);
        var calculated = FamilyFormulaPropagation.Recalculate(walker, family, body, types.Select(type => type.Values).Append(rootStore),
            selected.Values, selected == current ? rootStore : null, edit.ParameterId, replacement, idWidth, id => !externalReferences.Contains(id),
            id =>
            {
                var definition = Definition(id);
                return FormulaParameterStorage.Read(walker, definition.Walk, definition.Slot, includeCurrency: true);
            });

        var metadata = ReadPartAtom(root, pending);
        var part = FindPart(metadata, edit.TypeName);
        bool metadataChanged = false;
        var locations = new List<(int Offset, int Length, byte[] Replacement)>();
        var staged = new List<(long Id, StorageType Storage, byte[] Replacement)>();
        Stage(edit.ParameterId, storage, replacement, quantity, edit.Value, computed: false);
        foreach (var result in calculated)
        {
            var format = result.Format;
            Stage(result.Id, format.Storage, result.Replacement,
                format.Storage == StorageType.Double ? format.Unit : null,
                format.Storage == StorageType.String ? format.Decode(result.Replacement).Text : null, computed: true);
        }

        byte[]? atom = metadataChanged ? SerializePartAtom(metadata) : null;
        if (locations.Count == 0)
            return atom is null ? null : new PlanResult([], ("PartAtom", atom));

        byte[] updated = body;
        foreach (var location in locations.OrderByDescending(location => location.Offset))
            updated = ReplaceValue(updated, location.Offset, location.Length, location.Replacement);
        var verified = walker.Run(updated, 0, updated.Length, familyClass);
        var (checkedTypes, checkedCurrent) = ReadTypes(walker, verified);
        if (checkedCurrent.Name != current.Name || checkedTypes.Where(type => type.Name == edit.TypeName).ToArray() is not [var checkedType])
            throw Unverified("The edited record changed the current type or lost the requested type.");
        Verify(checkedType.Values);
        if (checkedType == checkedCurrent)
            Verify(ReadStore(walker, verified, "FamilyBase.m_familyParams"));

        return new PlanResult([new RvtPartitionRecordEdit(partition, 102, (ulong)familyId, updated)],
            atom is null ? null : ("PartAtom", atom));

        void Verify(FamilyStore store)
        {
            foreach (var item in staged)
            {
                var row = FamilyFormulaPropagation.FindFormulaRow(store, item.Id);
                var location = Location(row, item.Storage, idWidth);
                if (row.Instance != Definition(item.Id).Mode ||
                    !updated.AsSpan(location.Offset, location.Length).SequenceEqual(item.Replacement))
                    throw Unverified("The updated family type value could not be read back.");
            }
        }

        FamilyDefinition Definition(long id)
        {
            if (parsed.TryGetValue(id, out var saved))
                return saved;
            if (!definitions.TryGetValue(id, out var data))
                throw new RvtWriteRefusedException("unsupported-parameter", "The parameter must have a family or shared definition.");
            if (rootStore.Rows.Where(row => row.Id == id).ToArray() is not [{ Instance: 0 or 1 } declaration])
                throw Unverified("The parameter is not declared by the owner family.");
            int mode = (int)declaration.Instance;
            var (def, slot) = ReadDefinition(walker, data, new ParameterWriter.Edit(familyId, id, null));
            bool shared = walker.Class(BinaryPrimitives.ReadUInt16LittleEndian(data))?.Name == "ParamElemExternal";
            if (shared ? Integer(def, WideBlockWalker.AllItemsKeyPrefix + "bindings#") != 0 :
                Integer(def, "ParamElemFamily.m_instanceParam") != mode)
                throw Unverified("The parameter definition does not match the family declaration.");
            Guid? guid = shared
                ? ReadSharedGuid(root, schema, elements[elements.FindIndex(element => element.Id == id)], def, slot)
                : null;
            var definition = new FamilyDefinition(def, slot, mode, guid);
            parsed.Add(id, definition);
            return definition;
        }

        (StorageType, byte[], UnitType?) EncodeFamilyTypeReference()
        {
            if (Integer(input.Walk, "ParamDefFamType.m_categoryId", input.Slot) is not < 0)
                throw Unverified("The family type parameter definition is outside the verified layout.");
            if (edit.Value is null)
                throw new RvtWriteRefusedException("unsupported-parameter-clear", "Family type references require an explicit assignment.");
            if (edit.Value == "-1")
                reference = -1;
            if (reference is not long target || target != -1 && !nestedTypes.ContainsKey(target) && !materials.ContainsKey(target))
                throw new RvtWriteRefusedException("invalid-parameter-value",
                    "Expected -1 or the ID of an existing nested family type or material in the family file.");
            nestedReferenceName = ReferencePartAtomName(target);
            return (StorageType.ElementId, EncodeElementId(target, idWidth), null);
        }

        (StorageType, byte[], UnitType?) EncodeImageReference()
        {
            if (input.SharedGuid is not null || input.Mode != 0 || Integer(input.Walk, "ParamDef.m_userVisible", input.Slot) != 1)
                throw Unverified("The image parameter is outside the verified custom type scope.");
            if (edit.Value is null)
                throw new RvtWriteRefusedException("unsupported-parameter-clear", "Image references require an explicit assignment.");
            var (id, name) = ReadImageAssignment(edit.Value);
            imageReferenceName = name;
            return (StorageType.ElementId, EncodeElementId(id, idWidth), null);
        }

        (long Id, string Name) ReadImageAssignment(string value)
        {
            long id = ReadId(value);
            string name = ReadName(walker, images, id)
                ?? throw new RvtWriteRefusedException("invalid-parameter-value", "The image ID does not identify a verified image imported into this family.");
            ValidateText(name);
            return (id, name);
        }

        string? ReferencePartAtomName(long target)
        {
            if (target == -1 || materials.ContainsKey(target))
                return null;
            if (!nestedTypes.TryGetValue(target, out var symbol) || Integer(symbol, "FamilySymbol.m_masterId") != -1
                || Integer(symbol, "FamilySymbol.m_ownerInstanceId") != -1
                || Integer(symbol, "FamilySymbol.m_familyId") is not long ownerId
                || !nestedFamilies.TryGetValue(ownerId, out var owner) || Integer(owner, "FamilyBase.m_inPlace") != 0
                || PointerSlot(walker, symbol, "Symbol.m_symbolInfo", "SymbolInfo") is not int info
                || symbol.Captures.Where(c => c.QueueIndex == info && c.Key == "SymbolInfo.m_name").ToArray()
                    is not [{ Text: { Length: > 0 } typeName }]
                || owner.Captures.Where(c => c.AtRoot && c.Key == "FamilyBase.m_name").ToArray()
                    is not [{ Text: { Length: > 0 } familyName }])
                throw Unverified("The nested family type reference or its name could not be verified.");
            return familyName + " : " + typeName;
        }

        void Stage(long id, StorageType storage, byte[] replacement, UnitType? quantity, string? text, bool computed)
        {
            var definition = Definition(id);
            var row = ReadRow(selected.Values);
            var value = Location(row, storage, idWidth);
            var old = body.AsSpan(value.Offset, value.Length);
            if (!computed && (quantity == UnitType.UT_Currency || ParameterMeasurement.IsAdditionalSpec(quantity)))
                ParameterFormulaEvaluator.VerifyStableChange(BinaryPrimitives.ReadDoubleLittleEndian(old),
                    BinaryPrimitives.ReadDoubleLittleEndian(replacement));
            if (selected == current)
            {
                var copy = Location(ReadRow(rootStore), storage, idWidth);
                if (copy.Offset == value.Offset || !body.AsSpan(copy.Offset, copy.Length).SequenceEqual(old))
                    throw Unverified("The active type and the current family parameter value disagree.");
                if (!old.SequenceEqual(replacement))
                    locations.Add((copy.Offset, copy.Length, replacement));
            }
            if (!old.SequenceEqual(replacement))
                locations.Add((value.Offset, value.Length, replacement));
            staged.Add((id, storage, replacement));

            var original = row.Value(storage);
            if (familyTypeReference && !computed)
            {
                metadataChanged |= UpdateFamilyTypePartAtom(metadata, part, definition, category,
                    ReferencePartAtomName(original.Integer), nestedReferenceName);
                return;
            }
            ParameterMeasurement.DisplayValue? display = definition.Mode == 0 && quantity is UnitType measured && measured != UnitType.UT_Number
                && WideDocument.Read(latest, schema) is { } document && units.TryGetValue(document.UnitsElemId, out var displayUnits)
                ? ParameterMeasurement.Display(measured, walker, displayUnits, original.Real, BinaryPrimitives.ReadDoubleLittleEndian(replacement))
                : null;
            (string? Type, string? Before, string? After) referenceNames = default;
            if (storage == StorageType.ElementId)
            {
                referenceNames = imageReference && !computed
                    ? ("Image", ReadName(walker, images, original.Integer)
                        ?? throw Unverified("The stored image does not identify a verified image imported into this family."), imageReferenceName)
                    : ("Material", original.Integer == -1 ? "<By Category>" : materials.GetValueOrDefault(original.Integer),
                        edit.Value is null ? "<By Category>" : materials.GetValueOrDefault(material!.Value));
            }
            metadataChanged |= UpdatePartAtom(part, definition.Walk, definition.Slot, definition.SharedGuid, definition.Mode,
                storage, original, replacement, text, display, referenceNames);

            NamedValue ReadRow(FamilyStore store)
            {
                var row = computed ? FamilyFormulaPropagation.FindFormulaRow(store, id) : FindRow(store, id, definition.Mode)!;
                if (row.Instance != definition.Mode)
                    throw Unverified("The family formula result has a different instance or type scope.");
                return row;
            }
        }

        PlanResult? PlanBuiltIn(BuiltInField field, WideBlockWalker.Walk owner, byte[] data)
        {
            bool supportedCategory = edit.ParameterId == (int)BuiltInParameter.FIRE_RATING
                ? category == (int)BuiltInCategory.OST_Doors
                : category == (int)BuiltInCategory.OST_GenericModel || (field.IsIdentity
                    && category is ((int)BuiltInCategory.OST_Doors or (int)BuiltInCategory.OST_Windows or (int)BuiltInCategory.OST_Furniture));
            if (!supportedCategory || Integer(owner, "FamilyBase.m_inPlace") != 0)
                throw Unverified("This built-in field has not been verified for the family category.");

            var (allTypes, activeType) = ReadTypes(walker, owner);
            var target = FindType(allTypes, edit.TypeName);
            // Revit also copies the last assigned identity value to the unnamed door or window type.
            var unnamedType = field.IsIdentity && category is ((int)BuiltInCategory.OST_Doors or (int)BuiltInCategory.OST_Windows)
                ? allTypes.SingleOrDefault(type => type.Name == " ") : null;
            var currentValues = ReadStore(walker, owner, "FamilyBase.m_familyParams");
            var activeCopy = FindRow(currentValues, edit.ParameterId, 0, required: false);
            var rows = allTypes.ToDictionary(t => t, t => FindRow(t.Values, edit.ParameterId, 0, required: false));
            bool first = activeCopy is null;
            if ((first && field.Quantity == UnitType.UT_Length) || rows.Values.Any(row => (row is null) != first))
                throw Unverified("The built-in parameter's family value stores could not be verified.");

            var storageType = field.Storage;
            if (!first)
            {
                if (rows.Values.Append(activeCopy).Any(row => row is null || row.Fields[5].Integer != 0
                    || (storageType != StorageType.ElementId && row.Fields[3].Integer != -1)
                    || (storageType != StorageType.Double && row.Fields[2].Real != 0)
                    || (storageType != StorageType.String && row.Fields[0].Text != "")))
                    throw Unverified("The built-in family parameter has an unverified value layout.");
                var copy = Location(activeCopy!, storageType, idWidth);
                var saved = Location(rows[activeType]!, storageType, idWidth);
                if (copy.Offset == saved.Offset || !data.AsSpan(copy.Offset, copy.Length).SequenceEqual(data.AsSpan(saved.Offset, saved.Length)))
                    throw Unverified("The active type and the current family parameter value disagree.");
            }

            var old = rows[target]?.Value(storageType);
            string newText = edit.Value!;
            byte[] encoded;
            if (storageType == StorageType.ElementId)
            {
                var (imageId, name) = ReadImageAssignment(newText);
                newText = name;
                encoded = EncodeElementId(imageId, idWidth);
            }
            else if (field.Quantity == UnitType.UT_Length)
            {
                if (ParameterMeasurement.Parse(UnitType.UT_Length, newText) is not double value)
                    throw new RvtWriteRefusedException("invalid-parameter-value", "Default Elevation requires a finite length with a unit, such as 300 mm.");
                encoded = EncodeDouble(value);
            }
            else
                encoded = storageType == StorageType.Double ? EncodeCost(newText) : EncodeText(newText);
            var format = field.Quantity is UnitType quantity && WideDocument.Read(latest, schema) is { } document
                && units.TryGetValue(document.UnitsElemId, out var displayUnits)
                ? ParameterMeasurement.Display(quantity, walker, displayUnits, old?.Real ?? 0,
                    BinaryPrimitives.ReadDoubleLittleEndian(encoded))
                : null;
            if (storageType == StorageType.Double && format is null)
                throw Unverified("The family display format for this built-in parameter could not be verified.");

            string? oldText = storageType == StorageType.ElementId && old is { } storedImage
                ? ReadName(walker, images, storedImage.Integer)
                    ?? throw Unverified("The family references an image whose imported value could not be verified.")
                : old?.Text;
            var (defaultBytes, defaultText) = storageType switch
            {
                StorageType.ElementId => (EncodeElementId(-1, idWidth), NoneName),
                StorageType.Double => (new byte[sizeof(double)], "0.00"),
                _ => (new byte[sizeof(uint)], ""),
            };
            byte[]? partAtom = EditBuiltInPartAtom(root, pending, field, allTypes, target, unnamedType, first, oldText, newText, defaultText, format);
            byte[] revised = data;
            if (first)
            {
                VerifyNamedParamLayout(formats, walker);
                byte[] defaultRow = EncodeNamedParam(edit.ParameterId, storageType, idWidth, defaultBytes);
                byte[] assignedRow = EncodeNamedParam(edit.ParameterId, storageType, idWidth, encoded);
                var stores = allTypes.Select(t => (Store: t.Values, Assigned: Updates(t)))
                    .Append((Store: currentValues, Assigned: Updates(activeType)));
                foreach (var (store, assigned) in stores.OrderByDescending(s => s.Store.CountOffset))
                    revised = InsertNamedParam(revised, store, assigned ? assignedRow : defaultRow);
            }
            else
            {
                var locationsToEdit = allTypes.Where(Updates).Select(t => Location(rows[t]!, storageType, idWidth)).ToList();
                if (Updates(activeType))
                    locationsToEdit.Add(Location(activeCopy!, storageType, idWidth));
                foreach (var location in locationsToEdit.OrderByDescending(l => l.Offset))
                    if (!revised.AsSpan(location.Offset, location.Length).SequenceEqual(encoded))
                        revised = ReplaceValue(revised, location.Offset, location.Length, encoded);
            }

            var readBack = walker.Run(revised, 0, revised.Length, familyClass);
            var (verifiedTypes, verifiedCurrent) = ReadTypes(walker, readBack);
            if (verifiedCurrent.Name != activeType.Name || !verifiedTypes.Select(t => t.Name).SequenceEqual(allTypes.Select(t => t.Name)))
                throw Unverified("The edited family record changed the type table.");
            foreach (var type in verifiedTypes.Where(t => first || Updates(t)))
                VerifyValue(type.Values, Updates(type));
            if (first || Updates(activeType))
                VerifyValue(ReadStore(walker, readBack, "FamilyBase.m_familyParams"), Updates(activeType));

            return ReferenceEquals(data, revised) && partAtom is null ? null
                : new PlanResult(ReferenceEquals(data, revised) ? [] : [new(partition, 102, (ulong)familyId, revised)],
                    partAtom is null ? null : ("PartAtom", partAtom));

            bool Updates(FamilyType type) => type.Name == target.Name || type.Name == unnamedType?.Name;

            void VerifyValue(FamilyStore store, bool assigned)
            {
                var location = Location(FindRow(store, edit.ParameterId, 0)!, storageType, idWidth);
                var bytes = revised.AsSpan(location.Offset, location.Length);
                if (!bytes.SequenceEqual(assigned ? encoded : defaultBytes))
                    throw Unverified("The built-in family parameter could not be read back.");
            }
        }
    }

    private static FamilyType FindType(FamilyType[] types, string name) =>
        types.SingleOrDefault(t => t.Name == name)
        ?? throw new RvtWriteRefusedException("missing-family-type", $"The family has no unique type named '{name}'.");

    private static bool HasExactConstraintReferences(WideBlockWalker.Walk family, int slot)
    {
        long? expressionCount = Integer(family, "FamDimConstrMgrImpl.m_paramExprs#", slot)
            + Integer(family, "FamDimConstrMgrImpl.m_additionalParamExprs#", slot);
        var counts = family.Captures.Where(c => c.QueueIndex == slot && c.Key == "ParamExpr.m_entries#").ToArray();
        return expressionCount == counts.Length && counts.Sum(c => c.Integer) == family.Captures.Count(c =>
            c.QueueIndex == slot && c.Key == "ParamExprEntry.m_paramId" && c.Kind == WideBlockWalker.CaptureKind.Integer);
    }

    private static void VerifyNamedParamLayout(FormatsSchema formats, WideBlockWalker walker)
    {
        if (formats.IdOf("NamedParam") is not int tag || walker.Class(tag) is not { Base: 0 } row
            || row.Fields.Where(f => f.Stored).ToArray() is not
            [
                { Name: "m_str", Type: 8, Mods: 0 or 0x60 },
                { Name: "m_oExpression", Type: 14, Mods: 1 },
                { Name: "m_value", Type: 7, Mods: 0 },
                { Name: "m_elemId", Type: 14, Mods: 0, Ref: int idTag },
                { Name: "m_paramId", Type: 14, Mods: 0, Ref: int paramTag },
                { Name: "m_int", Type: 4, Mods: 0 },
                { Name: "m_instance", Type: 1, Mods: 0 },
                { Name: "m_reporting", Type: 1, Mods: 0 },
            ] || idTag != paramTag || walker.Class(idTag)?.Name != "ElementId")
            throw Unverified("The built-in family parameter row layout could not be verified.");
    }

    private static byte[] EncodeNamedParam(long id, StorageType storage, int idWidth, byte[] value)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        if (storage == StorageType.String)
            writer.Write(value);
        else
            writer.Write(0u);
        writer.Write(0u);
        if (storage == StorageType.Double)
            writer.Write(value);
        else
            writer.Write(0d);
        writer.Write(storage == StorageType.ElementId ? value : EncodeElementId(-1, idWidth));
        writer.Write(EncodeElementId(id, idWidth));
        writer.Write(0);
        writer.Write(false);
        writer.Write(false);
        return output.ToArray();
    }

    private static (FamilyType[] Types, FamilyType Current) ReadTypes(WideBlockWalker walker, WideBlockWalker.Walk family)
    {
        if (!family.Exact || Integer(family, "FamilyBase.m_pFamilyTypes") is not long tag
            || walker.Class((int)tag)?.Name != "FamilyTypeTable"
            || family.Queue.Select((cls, index) => (cls, index)).Where(p => p.cls == tag).ToArray() is not [{ index: var slot }])
            throw Unverified("The family type table could not be read exactly.");
        var fields = family.Captures.Where(c => c.QueueIndex == slot).ToArray();
        var names = fields.Select((capture, index) => (capture, index)).Where(p => p.capture.Key == "FamilyTypePair.name").ToArray();
        if (Integer(family, "FamilyTypeTable.m_pairs#", slot) != names.Length
            || Integer(family, "FamilyTypeTable.m_idx", slot) is not long current || current < 0 || current >= names.Length
            || names.Any(p => p.capture.Text is null)
            || names.Select(p => p.capture.Text).Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw Unverified("The family type names or current type could not be verified.");
        var types = new FamilyType[names.Length];
        for (int i = 0; i < types.Length; i++)
        {
            int end = i + 1 == types.Length ? fields.Length : names[i + 1].index;
            types[i] = new FamilyType(names[i].capture.Text!, ReadStore(fields[(names[i].index + 1)..end]));
        }
        return (types, types[current]);
    }

    private static byte[]? EditBuiltInPartAtom(RootStorage root, Pending pending, BuiltInField field, FamilyType[] types, FamilyType selected,
        FamilyType? unnamedType, bool first, string? before, string text, string defaultText, ParameterMeasurement.DisplayValue? display)
    {
        var document = ReadPartAtom(root, pending);
        if (unnamedType is not null && TypeParts(document).Any(part => (string?)part.Element(Atom + "title") == unnamedType.Name))
            throw Unverified("The unnamed family type has an unverified PartAtom entry.");
        bool changed = false;
        foreach (var type in types.Where(t => t == selected || (first && t != unnamedType)))
        {
            var part = FindPart(document, type.Name);
            var matches = part.Elements(Atom + field.Name).Where(node => (string?)node.Attribute("type") == "system").ToArray();
            if (matches.Length > 1)
                throw Unverified("The built-in family metadata entry is ambiguous.");
            var entry = matches.SingleOrDefault();
            bool expected = !first && (display is not null || !string.IsNullOrEmpty(before));
            if (expected != (entry is not null)
                || (entry is not null && (entry.HasElements || (string?)entry.Attribute("typeOfParameter") != field.Type
                    || (string?)entry.Attribute("displayName") != field.DisplayName || (string?)entry.Attribute("units") != display?.Unit
                    || !(display?.Matches(entry.Value) ?? entry.Value == before))))
                throw Unverified("The stored built-in value and family metadata disagree.");

            string after = type == selected ? display?.After ?? text : defaultText;
            if (entry is null)
            {
                if (after.Length == 0)
                    continue;
                entry = new XElement(Atom + field.Name);
                if (field.DisplayName is not null)
                    entry.SetAttributeValue("displayName", field.DisplayName);
                entry.SetAttributeValue("type", "system");
                entry.SetAttributeValue("typeOfParameter", field.Type);
                if (display is { } units)
                    entry.SetAttributeValue("units", units.Unit);
                entry.Value = after;
                part.Add(entry);
            }
            else if (after.Length == 0)
                entry.Remove();
            else if (entry.Value != after)
                entry.Value = after;
            else
                continue;
            changed = true;
        }
        return changed ? SerializePartAtom(document) : null;
    }

    private static XElement FindPart(XDocument document, string typeName)
    {
        var parts = TypeParts(document)
            .Where(part => (string?)part.Element(Atom + "title") == typeName).ToArray();
        return parts is [var part] ? part : throw Unverified("The family metadata does not identify the requested type uniquely.");
    }

    private static IEnumerable<XElement> TypeParts(XDocument document) =>
        document.Elements(Atom + "entry").Elements(PartAtom + "family").Elements(PartAtom + "part");

    private static XDocument ReadPartAtom(RootStorage root, Pending pending)
    {
        using var stream = pending.OpenMetadata(root, "PartAtom")
            ?? throw Unverified("The family metadata stream is missing.");
        try
        {
            using var input = XmlReader.Create(stream, new XmlReaderSettings { MaxCharactersInDocument = 16 * 1024 * 1024 });
            return XDocument.Load(input, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw new RvtWriteRefusedException("unverified-parameter-storage", "The family metadata stream could not be decoded.", ex);
        }
    }

    private static bool UpdateFamilyTypePartAtom(XDocument metadata, XElement part, FamilyDefinition definition,
        long? ownerCategory, string? before, string? after)
    {
        if (definition.Walk.Captures.Where(c => c.QueueIndex == definition.Slot && c.Key == "ParamDef.m_caption").ToArray()
            is not [{ Text: { Length: > 0 } caption }]
            || Integer(definition.Walk, "ParamDef.m_userVisible", definition.Slot) is not long visible || visible is not (0 or 1))
            throw Unverified("The family type parameter name or visibility could not be verified.");

        var matches = Entries(part).ToArray();
        if (definition.Mode != 0 || visible == 0)
        {
            if (matches.Length > 0)
                throw Unverified("The family type parameter has an unexpected metadata entry.");
            return false;
        }

        const string prefix = "Family type: ";
        var labels = TypeParts(metadata).SelectMany(Entries).Select(e => (string?)e.Attribute("typeOfParameter"))
            .Distinct(StringComparer.Ordinal).ToArray();
        string? type = labels is [{ } label] && label.StartsWith(prefix, StringComparison.Ordinal) && label.Length > prefix.Length
            ? label : null;
        if (labels.Length > 0 && type is null)
            throw Unverified("The family type parameter metadata uses an unverified category label.");

        if (before is not null)
        {
            if (matches is not [var target] || target.HasElements || target.Attribute("units") is not null || target.Value != before)
                throw Unverified("The stored family type reference and family metadata disagree.");
            if (before == after)
                return false;
            if (after is null)
                target.Remove();
            else
                target.Value = after;
            return true;
        }

        if (matches.Length > 0)
            throw Unverified("The unset family type reference has an unexpected metadata entry.");
        if (after is null)
            return false;
        if (type is null && ownerCategory is >= int.MinValue and < 0
            && ((BuiltInCategory)ownerCategory.Value).TryGetCategoryName(out var ownerLabel)
            && metadata.Root?.Elements(Atom + "category").Where(e => (string?)e.Element(Atom + "scheme") == "adsk:revit:grouping")
                .Select(e => (string?)e.Element(Atom + "term")).ToArray() is [var grouping] && grouping == ownerLabel
            && Integer(definition.Walk, "ParamDefFamType.m_categoryId", definition.Slot) is >= int.MinValue and < 0 and long targetCategory
            && ((BuiltInCategory)targetCategory).TryGetCategoryName(out var targetLabel))
            type = prefix + targetLabel;
        if (type is null)
            throw Unverified("The family type parameter metadata category label could not be verified.");

        part.Add(CreatePartAtomEntry(caption, definition.SharedGuid, type, after));
        return true;

        IEnumerable<XElement> Entries(XElement entry) => entry.Elements().Where(node =>
            MatchesPartAtomParameter(node, caption, definition.SharedGuid));
    }

    private static bool UpdatePartAtom(
        XElement part, WideBlockWalker.Walk definition, int slot, Guid? sharedGuid, int mode,
        StorageType storage, WideBlockWalker.Capture original, byte[] replacement, string? text,
        ParameterMeasurement.DisplayValue? display, (string? Type, string? Before, string? After) reference)
    {
        if (definition.Captures.Where(c => c.QueueIndex == slot && c.Key == "ParamDef.m_caption").ToArray()
            is not [{ Text: { Length: > 0 } caption }])
            throw Unverified("The parameter name could not be verified.");
        var matches = part.Elements().Where(node => MatchesPartAtomParameter(node, caption, sharedGuid)).ToArray();
        if (matches.Length == 0)
        {
            if (reference.Type == "Image")
                throw Unverified("The custom image has no verified family metadata entry.");
            if (mode != 0 || storage != StorageType.String || original.Text != "" || string.IsNullOrEmpty(text)
                || Integer(definition, "ParamDef.m_userVisible", slot) != 1)
                return false;

            part.Add(CreatePartAtomEntry(caption, sharedGuid, "Text", text));
            return true;
        }
        if (mode != 0 || matches is not [var target] || target.HasElements || (string?)target.Attribute("units") != display?.Unit)
            throw Unverified("The parameter's family metadata is outside the verified layout.");

        string before = target.Value;
        string after;
        bool matchesValue;
        switch (storage, (string?)target.Attribute("typeOfParameter"))
        {
            case (StorageType.String, "Text"):
                matchesValue = before == original.Text;
                after = text ?? "";
                break;
            case (StorageType.Integer, "Integer"):
                matchesValue = int.TryParse(before, NumberStyles.Integer, CultureInfo.InvariantCulture, out int integer) && integer == original.Integer;
                after = BinaryPrimitives.ReadInt32LittleEndian(replacement).ToString(CultureInfo.InvariantCulture);
                break;
            case (StorageType.Integer, "Yes/No"):
                matchesValue = before is "Yes" or "No" && (before == "Yes" ? 1 : 0) == original.Integer;
                after = BinaryPrimitives.ReadInt32LittleEndian(replacement) == 0 ? "No" : "Yes";
                break;
            case (StorageType.Double, "Number") when display is null:
                matchesValue = double.TryParse(before, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && number == original.Real;
                after = BinaryPrimitives.ReadDoubleLittleEndian(replacement).ToString("R", CultureInfo.InvariantCulture);
                break;
            case (StorageType.Double, string type) when display is { } formatted && type == formatted.Type:
                matchesValue = formatted.Matches(before);
                after = formatted.After;
                break;
            case (StorageType.ElementId, string type) when reference.Type == type && reference.After is not null:
                matchesValue = before == reference.Before;
                after = reference.After;
                break;
            default:
                throw Unverified("The parameter's family metadata uses an unverified value format.");
        }
        if (!matchesValue)
            throw Unverified("The stored parameter value and family metadata disagree.");
        if (storage == StorageType.String && after.Length == 0)
            target.Remove();
        else
        {
            if (after == before)
                return false;
            target.Value = after;
        }
        return true;
    }

    private static bool MatchesPartAtomParameter(XElement entry, string caption, Guid? sharedGuid) =>
        sharedGuid is Guid guid
            ? (string?)entry.Attribute("type") == "shared" && Guid.TryParse((string?)entry.Attribute("id"), out var id) && id == guid
            : (string?)entry.Attribute("type") == "custom" && (string?)entry.Attribute("displayName") == caption;

    private static XElement CreatePartAtomEntry(string caption, Guid? sharedGuid, string type, string value)
    {
        var entry = new XElement(Atom + PartAtomParameterName(caption), new XAttribute("displayName", caption),
            new XAttribute("type", sharedGuid.HasValue ? "shared" : "custom"));
        if (sharedGuid is Guid guid)
            entry.Add(new XAttribute("id", guid));
        entry.Add(new XAttribute("typeOfParameter", type), value);
        return entry;
    }

    private static string PartAtomParameterName(string caption)
    {
        string name = new(caption.Select(c => char.IsLetterOrDigit(c) || c is '_' or '.' ? c : '_').ToArray());
        return name[0] != '_' && !char.IsLetter(name[0]) ? "_" + name : name;
    }

    private const string LocalSharedPrefix = "revit.local.shared:";

    private static Guid ReadSharedGuid(RootStorage root, SchemaDocument schema, ElemTableLayout.Row parameter,
        WideBlockWalker.Walk definition, int slot)
    {
        var key = definition.Captures.Where(c => c.AtRoot && c.Key == "ParamElemExternal.m_externalParamKey").ToArray();
        var forge = definition.Captures.Where(c => c.QueueIndex == slot && c.Key == "ParamDef.m_typeId").ToArray();
        string? typeId = forge is [{ Text: var text }] ? text : null;
        Guid? forgeGuid = SharedTypeGuid(typeId);
        if (key is [{ Text: var guidText }] && Guid.TryParse(guidText, out Guid guid) && guid != Guid.Empty)
        {
            if (forge.Length != 0 && forgeGuid != guid && !MatchesLocalSharedElement(root, schema, parameter, typeId))
                throw Unverified("The shared family parameter GUID and Forge type ID disagree.");
            return guid;
        }
        if (key.Length == 0 && forgeGuid is Guid known && known != Guid.Empty)
            return known;
        throw Unverified("The shared family parameter GUID could not be verified.");
    }

    private static Guid? SharedTypeGuid(string? typeId)
    {
        if (typeId?.StartsWith(LocalSharedPrefix, StringComparison.Ordinal) != true)
            return BipHelpers.ParseGuidFromForgeId(typeId);
        var value = typeId.AsSpan(LocalSharedPrefix.Length);
        return value.Length == 38 && (value[32..].SequenceEqual("-1.0.0") || value[32..].SequenceEqual("-2.0.0"))
            && Guid.TryParseExact(value[..32], "N", out Guid guid) ? guid : null;
    }

    private static bool MatchesLocalSharedElement(RootStorage root, SchemaDocument schema, ElemTableLayout.Row parameter, string? typeId)
    {
        // Revit 2023 uses the definition element's compact UniqueId here, while PartAtom still uses its shared GUID.
        if (typeId?.StartsWith(LocalSharedPrefix, StringComparison.Ordinal) != true
            || typeId.Length != LocalSharedPrefix.Length + 46 || !typeId.EndsWith("-1.0.0", StringComparison.Ordinal)
            || parameter.OriginalId is < 0 or > uint.MaxValue
            || PagedGzipStream.Read(root, "Global", "History") is not { } history)
            return false;
        var episodes = RvtFileReader.ParseEpisodeGuids([history], null, new FileSchema(schema));
        var original = new ElemRecHistory((uint)parameter.OriginalId, parameter.CreationDate);
        return ElementExportIds.EpisodeGuid(original, episodes.Guids, episodes.X) is Guid episode
            && typeId.AsSpan(LocalSharedPrefix.Length, 40).Equals($"{episode:N}{original.OriginalElementId:x8}", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] SerializePartAtom(XDocument document)
    {
        using var buffer = new MemoryStream();
        using (var output = XmlWriter.Create(buffer, new XmlWriterSettings { NewLineHandling = NewLineHandling.Entitize }))
            document.Save(output);
        return buffer.ToArray();
    }
}
