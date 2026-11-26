using System.Buffers.Binary;
using System.Globalization;
using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Writing;

internal static class UserParameterWriter
{
    internal static string? CaptureKey(SchemaClass declaring, SchemaField field) => (declaring.Name, field.Name) switch
    {
        ("Element", "m_pParamValueSetInt" or "m_pParamValueSetDouble" or "m_pParamValueSetElementId") => field.Name,
        ("ParamValueInt" or "ParamValueDouble" or "ParamValueElementId", "m_value") => field.Name,
        ("ParamElemProject" or "ParamElemExternal", "m_bindingIds") => WideBlockWalker.AllItemsKeyPrefix + "bindings",
        ("ParamElemExternal", "m_hideWhenNoValue") or
        ("ParamElem", "m_pParamDef") or
        ("ParamDef", "m_paramElemId" or "m_readOnly") or
        ("ParamDefValue", "m_specTypeId" or "m_unitType" or "m_restriction" or "m_boundless") or
        ("ParamDefInt", "m_lowBound" or "m_upBound") or
        ("ParamDefFamType", "m_categoryId") or
        ("NestedFamSymRefElem", "m_famName" or "m_symName" or "m_categoryId") or
        ("ParamDefMaterialBrowse", "m_unassignedString" or "m_extraButtonFlag" or "m_includeParams") or
        ("MaterialElem", "m_pMaterial") or ("Material", "m_name") or
        ("ParamBinding", "m_paramId" or "m_categoryId" or "m_elemOrSymbol") => declaring.Name + "." + field.Name,
        _ => null,
    };

    internal static bool HasProjectBinding(WideBlockWalker walker, byte[]? definition)
    {
        if (definition is not { Length: >= 2 })
            return false;
        int tag = BinaryPrimitives.ReadUInt16LittleEndian(definition);
        return walker.Class(tag)?.Name is "ParamElemExternal" or "ParamElemProject"
            && walker.Run(definition, 0, definition.Length, tag, rootOnly: true) is { RootRead: true } root
            && Integer(root, WideBlockWalker.AllItemsKeyPrefix + "bindings#") is > 0;
    }

    internal static bool IsFamilyTypeDefinition(WideBlockWalker walker, byte[]? definition)
    {
        if (definition is not { Length: >= 2 })
            return false;
        int tag = BinaryPrimitives.ReadUInt16LittleEndian(definition);
        var root = walker.Run(definition, 0, definition.Length, tag, rootOnly: true);
        return root.RootRead && Integer(root, "ParamElem.m_pParamDef") is (> 0 and long kind)
            && walker.Class((int)kind)?.Name == "ParamDefFamType";
    }

    internal static (StorageType Storage, byte[] Replacement, UnitType? Quantity) Prepare(
        WideBlockWalker walker, string? ownerClass, int? category, byte[]? definition,
        IReadOnlyDictionary<long, byte[]?> bindings, ParameterWriter.Edit edit, int idWidth, long? material,
        WideBlockWalker.Walk? reference, ProjectFamilyReference? projectReference, long? localElementReference)
    {
        var (def, slot) = ReadBoundDefinition(walker, ownerClass, category, definition, bindings, edit, edit.Value is null);
        string? definitionClass = walker.Class(BinaryPrimitives.ReadUInt16LittleEndian(definition))?.Name;
        if (walker.Class(def.Queue[slot])?.Name == "ParamDefFamType")
        {
            return EncodeFamilyTypeReference(def, slot, edit, idWidth, material, reference, projectReference,
                definitionClass == "ParamElemExternal", localElementReference);
        }
        if (ownerClass == "FamilySymbol" && edit.Value == "-1"
            && definitionClass == "ParamElemProject")
            material = -1;
        return Encode(walker, def, slot, edit, idWidth, material, includeCurrency: true);
    }

    internal static (WideBlockWalker.Walk Walk, int Slot) ReadBoundDefinition(
        WideBlockWalker walker, string? ownerClass, int? category, byte[]? definition,
        IReadOnlyDictionary<long, byte[]?> bindings, ParameterWriter.Edit edit, bool clearing)
    {
        bool loadedFamily = ownerClass is "FamilyInstance" or "FamilySymbol";
        (int? expectedCategory, int bindingKind) = ownerClass switch
        {
            "SWall" => ((int)BuiltInCategory.OST_Walls, 1),
            "BasicWallType" => ((int)BuiltInCategory.OST_Walls, 2),
            "RoomElem" => ((int)BuiltInCategory.OST_Rooms, 1),
            "Floor" => ((int)BuiltInCategory.OST_Floors, 1),
            "FloorAttributes" => ((int)BuiltInCategory.OST_Floors, 2),
            "ProfileRoof" => ((int)BuiltInCategory.OST_Roofs, 1),
            "RoofAttributes" => ((int)BuiltInCategory.OST_Roofs, 2),
            "Ceiling" => ((int)BuiltInCategory.OST_Ceilings, 1),
            "CompoundCeilingType" => ((int)BuiltInCategory.OST_Ceilings, 2),
            "Level" => ((int)BuiltInCategory.OST_Levels, 1),
            "LevelAttributes" => ((int)BuiltInCategory.OST_Levels, 2),
            "Grid" => ((int)BuiltInCategory.OST_Grids, 1),
            "GridAttributes" => ((int)BuiltInCategory.OST_Grids, 2),
            "GridChain" => ((int)BuiltInCategory.OST_GridChains, 1),
            "FamilyInstance" => (category, 1),
            "FamilySymbol" => (category, 2),
            _ => throw new RvtWriteRefusedException("unsupported-parameter-owner", "Writing user parameters on this element class has not been verified."),
        };
        if (category is not < -1 || category != expectedCategory)
            throw ParameterWriter.Unverified("The element's category or design option could not be verified for writing.");

        if (definition is not { Length: >= 2 })
            throw ParameterWriter.Unverified("The parameter definition record was not found.");
        int definitionId = BinaryPrimitives.ReadUInt16LittleEndian(definition);
        string? definitionClass = walker.Class(definitionId)?.Name;
        string refusal = clearing ? "unsupported-parameter-clear" : "unsupported-parameter";
        if (loadedFamily && definitionClass != "ParamElemExternal" && (clearing || definitionClass != "ParamElemProject"))
            throw new RvtWriteRefusedException(refusal, clearing
                ? "Clearing requires a shared parameter bound in the project."
                : "This edit requires a parameter bound in the project.");
        if (definitionClass is not ("ParamElemExternal" or "ParamElemProject"))
            throw new RvtWriteRefusedException("unsupported-parameter", "The parameter must have a project or shared definition bound in this project.");

        var (def, slot) = ReadDefinition(walker, definition, edit);

        if (clearing && (definitionClass != "ParamElemExternal" || Integer(def, "ParamElemExternal.m_hideWhenNoValue") != 1))
            throw new RvtWriteRefusedException(refusal, "This edit requires a shared parameter with HideWhenNoValue.");
        if (loadedFamily && Integer(def, WideBlockWalker.AllItemsKeyPrefix + "bindings#") == 0)
            throw new RvtWriteRefusedException(refusal, "This edit requires a project-bound parameter declared outside the loaded family.");

        var bindingIds = def.Captures.Where(c => c.AtRoot && c.Key.StartsWith(WideBlockWalker.AllItemsKeyPrefix + "bindings@", StringComparison.Ordinal))
            .Select(c => c.Integer).ToArray();
        if (bindingIds.Length == 0 || Integer(def, WideBlockWalker.AllItemsKeyPrefix + "bindings#") != bindingIds.Length
            || bindingIds.Distinct().Count() != bindingIds.Length)
            throw ParameterWriter.Unverified("The parameter has no verified category bindings.");
        int matches = 0;
        foreach (long id in bindingIds)
        {
            if (!bindings.TryGetValue(id, out byte[]? binding) || binding is null)
                throw ParameterWriter.Unverified("A declared parameter binding could not be found exactly.");
            var resolved = walker.Run(binding, 0, binding.Length, BinaryPrimitives.ReadUInt16LittleEndian(binding));
            if (!resolved.Exact || Integer(resolved, "m_id") != id || Integer(resolved, "ParamBinding.m_paramId") != edit.ParameterId
                || Integer(resolved, "ParamBinding.m_categoryId") is not long boundCategory
                || Integer(resolved, "ParamBinding.m_elemOrSymbol") is not (1 or 2))
                throw ParameterWriter.Unverified("A declared parameter binding could not be read exactly.");
            if (boundCategory == category)
            {
                if (Integer(resolved, "ParamBinding.m_elemOrSymbol") != bindingKind)
                    throw ParameterWriter.Unverified("The parameter binding targets a different instance or type.");
                matches++;
            }
        }
        if (matches != 1)
            throw ParameterWriter.Unverified("The category has no unique applicable parameter binding.");

        return (def, slot);
    }

    internal static (WideBlockWalker.Walk Walk, int Slot) ReadDefinition(
        WideBlockWalker walker, byte[] definition, ParameterWriter.Edit edit)
    {
        var def = walker.Run(definition, 0, definition.Length, BinaryPrimitives.ReadUInt16LittleEndian(definition));
        if (!def.Exact || Integer(def, "m_id") != edit.ParameterId
            || Integer(def, "ParamElem.m_pParamDef") is not long paramDefClass
            || def.Queue.Select((tag, index) => (tag, index)).Where(p => p.tag == paramDefClass).ToArray() is not [{ index: var slot }]
            || Integer(def, "ParamDef.m_paramElemId", slot) != edit.ParameterId
            || Integer(def, "ParamDef.m_readOnly", slot) is not long readOnly)
            throw ParameterWriter.Unverified("The parameter definition could not be read exactly.");
        if (readOnly != 0)
            throw new RvtWriteRefusedException("read-only-parameter", $"Parameter {edit.ParameterId} is marked read-only in its definition.");
        return (def, slot);
    }

    internal static (StorageType Storage, byte[] Replacement, UnitType? Quantity) Encode(
        WideBlockWalker walker, WideBlockWalker.Walk def, int slot, ParameterWriter.Edit edit, int idWidth, long? material,
        bool familyReset = false, bool includeCurrency = false)
    {
        string? defClassName = walker.Class(def.Queue[slot])?.Name;
        if (defClassName == "ParamDefString")
            return (StorageType.String, edit.Value is null && !familyReset ? [] : ParameterWriter.EncodeText(edit.Value ?? ""), null);
        if (defClassName is "ParamDefInt" or "ParamDefYesNo")
        {
            long? min = defClassName == "ParamDefYesNo" ? 0 : Integer(def, "ParamDefInt.m_lowBound", slot);
            long? max = defClassName == "ParamDefYesNo" ? 1 : Integer(def, "ParamDefInt.m_upBound", slot);
            if (min is null || max is null || min > max)
                throw ParameterWriter.Unverified("The integer parameter's bounds could not be read exactly.");
            if (edit.Value is null)
            {
                if (familyReset) throw UnsupportedFamilyReset();
                return (StorageType.Integer, [], null);
            }
            if (!int.TryParse(edit.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < min || value > max)
                throw InvalidValue(edit, $"An integer between {min} and {max} is required.");
            byte[] bytes = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            return (StorageType.Integer, bytes, null);
        }
        if (defClassName == "ParamDefValue" && Integer(def, "ParamDefValue.m_restriction", slot) == 1
            && Integer(def, "ParamDefValue.m_boundless", slot) == 0
            && ParameterMeasurement.Spec(def, slot, includeCurrency: includeCurrency, includeAdditional: true) is UnitType quantity)
        {
            if (edit.Value is null)
            {
                if (familyReset) throw UnsupportedFamilyReset();
                return (StorageType.Double, [], quantity);
            }
            if (ParameterMeasurement.Parse(quantity, edit.Value) is not double value)
                throw InvalidValue(edit, quantity is UnitType.UT_Number or UnitType.UT_Currency ? "A finite number in invariant format is required."
                    : "A finite number and a matching unit are required, separated by a space.");
            return (StorageType.Double, ParameterWriter.EncodeDouble(value), quantity);
        }
        if (defClassName == "ParamDefMaterialBrowse" && Integer(def, "ParamDefMaterialBrowse.m_unassignedString", slot) == 0
            && Integer(def, "ParamDefMaterialBrowse.m_extraButtonFlag", slot) == -1
            && Integer(def, "ParamDefMaterialBrowse.m_includeParams", slot) == 0)
        {
            if (edit.Value is null && !familyReset)
                return (StorageType.ElementId, [], null);
            if (edit.Value is null)
                material = -1;
            if (material is not long id)
                throw InvalidValue(edit, "Expected the positive ID of an existing material in the source file.");
            return (StorageType.ElementId, EncodeElementId(id, idWidth), null);
        }
        throw new RvtWriteRefusedException("unsupported-parameter", $"The data type of parameter {edit.ParameterId} has not been verified for writing.");
    }

    private static RvtWriteRefusedException UnsupportedFamilyReset() =>
        new("unsupported-parameter-clear", "Family parameters can be reset to empty text or By Category for materials. Numeric values require an explicit assignment.");

    internal static long? ReferenceId(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0 ? id : null;

    internal static (StorageType, byte[], UnitType?) EncodeFamilyTypeReference(WideBlockWalker.Walk definition, int slot,
        ParameterWriter.Edit edit, int idWidth, long? material, WideBlockWalker.Walk? reference, ProjectFamilyReference? projectReference,
        bool sharedProjectBinding = false, long? localElementReference = null)
    {
        if (Integer(definition, "ParamDefFamType.m_categoryId", slot) is not (< 0 and long referenceCategory))
            throw ParameterWriter.Unverified("The family type reference category could not be verified.");
        if (edit.Value is null)
            return (StorageType.ElementId, [], null);
        long? target = edit.Value == "-1" ? -1 : ReferenceId(edit.Value);
        if (target is not long id)
            throw InvalidValue(edit, "Expected -1 or a project element ID.");
        bool verifiedProjectType = projectReference is not null && (sharedProjectBinding || projectReference.Category == referenceCategory);
        if (id != -1 && id != material && !verifiedProjectType && !(sharedProjectBinding && id == localElementReference)
            && (reference is not { Exact: true, EmptyEntityAt: < 0 }
                || Integer(reference, "m_id") != id
                || Integer(reference, "NestedFamSymRefElem.m_categoryId") != referenceCategory
                || RootName(reference, "NestedFamSymRefElem.m_symName") is null
                || RootName(reference, "NestedFamSymRefElem.m_famName") is null))
            throw InvalidValue(edit, sharedProjectBinding ? "Expected a verified local element or nested family type reference."
                : "Expected a verified material or family type reference in the parameter's category.");
        return (StorageType.ElementId, EncodeElementId(id, idWidth), null);
    }

    internal static void VerifyFamilyReferenceOwner(WideBlockWalker.Walk family, WideBlockWalker.Walk? reference,
        ProjectFamilyReference? projectReference, ParameterWriter.Edit edit, bool independentType = false)
    {
        if (projectReference is not null)
        {
            if (Integer(family, "m_id") is not long owner)
                throw ParameterWriter.Unverified("The referring family could not be identified.");
            if (owner == projectReference.FamilyId)
                throw InvalidValue(edit, "The referenced type belongs to the family being edited.");
            if (projectReference.Dependencies.Contains(owner) && (!independentType || projectReference.SharedNestedOwner != owner))
                throw ParameterWriter.Unverified("The referenced type has a dependency on the family being edited.");
        }
        if (reference is not null && RootName(reference, "NestedFamSymRefElem.m_famName") is string targetFamily
            && (RootName(family, "FamilyBase.m_name") is not string ownerFamily
                || targetFamily.Equals(ownerFamily, StringComparison.OrdinalIgnoreCase)))
            throw InvalidValue(edit, "The nested type does not identify a different family from the owner.");
    }

    internal static string? RootName(WideBlockWalker.Walk record, string key) =>
        record.Captures.Where(field => field.AtRoot && field.Key == key).ToArray() is [{ Text: { Length: > 0 } name }]
            && !string.IsNullOrWhiteSpace(name) ? name : null;

    internal static byte[] EncodeElementId(long id, int idWidth)
    {
        var bytes = new byte[idWidth];
        if (idWidth == sizeof(long))
            BinaryPrimitives.WriteInt64LittleEndian(bytes, id);
        else
            BinaryPrimitives.WriteInt32LittleEndian(bytes, checked((int)id));
        return bytes;
    }

    internal static string? MaterialName(WideBlockWalker walker, WideBlockWalker.Walk walk, long id)
    {
        if (!walk.Exact || walk.EmptyEntityAt >= 0 || Integer(walk, "m_id") != id
            || Integer(walk, "MaterialElem.m_pMaterial") is not long tag || walker.Class((int)tag)?.Name != "Material"
            || walk.Queue.Select((cls, index) => (cls, index)).Where(p => p.cls == tag).ToArray() is not [{ index: var slot }])
            return null;
        return walk.Captures.Where(c => c.QueueIndex == slot && c.Key == "Material.m_name").ToArray()
            is [{ Text: { Length: > 0 } name }] ? name : null;
    }

    internal static long? Integer(WideBlockWalker.Walk walk, string key, int queueIndex = -1)
        => walk.Captures.Where(c => c.QueueIndex == queueIndex && c.Key == key).ToArray() is [var value]
            && value.Kind is WideBlockWalker.CaptureKind.Integer or WideBlockWalker.CaptureKind.PointerClass ? value.Integer : null;

    private static RvtWriteRefusedException InvalidValue(ParameterWriter.Edit edit, string message)
        => new("invalid-parameter-value", $"Parameter {edit.ParameterId}: {message}");
}
