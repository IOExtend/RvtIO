using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using OpenMcdf;
using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Writing;

internal static class ParameterWriter
{
    internal sealed record Edit(long ElementId, long ParameterId, string? Value)
    {
        internal BuiltInParameter Parameter => (BuiltInParameter)ParameterId;
        internal bool UserDefined => ParameterId > 0;
    }
    internal sealed record PlanResult(IReadOnlyList<RvtPartitionRecordEdit> Records, (string Path, byte[] Bytes)? Metadata);

    internal sealed class Pending
    {
        internal readonly Dictionary<(int Partition, int Block, int Channel, ulong Owner), RvtPartitionRecordEdit> Records = new();
        internal readonly Dictionary<string, byte[]> Metadata = new(StringComparer.OrdinalIgnoreCase);
        internal readonly List<PartitionOwnerAddition> AddedProjectOwners = [];
        internal readonly HashSet<(int Partition, ulong Owner)> RemovedProjectOwners = [];
        internal GlobalParameterWriter.ProjectDependencies? GlobalParameterDependencies;

        internal void Apply(PlanResult? plan)
        {
            if (plan is null)
                return;
            foreach (var edit in plan.Records)
                Records[(edit.PartitionNumber, edit.Block, edit.Channel, edit.OwnerId)] = edit;
            if (plan.Metadata is { } metadata)
                Metadata[metadata.Path] = metadata.Bytes;
        }

        internal Stream? OpenMetadata(RootStorage root, string path) =>
            Metadata.TryGetValue(path, out byte[]? bytes) ? new MemoryStream(bytes, writable: false)
                : root.TryOpenStream(path, out var source) ? source : null;
    }

    // These ProjectInfo fields are writable in Revit and have a second representation in the ProjectInformation stream.
    private static readonly Dictionary<BuiltInParameter, string> Fields = new()
    {
        [BuiltInParameter.CLIENT_NAME] = "Client_Name",
        [BuiltInParameter.PROJECT_ADDRESS] = "Project_Address",
        [BuiltInParameter.PROJECT_ISSUE_DATE] = "Project_Issue_Date",
        [BuiltInParameter.PROJECT_NAME] = "Project_Name",
        [BuiltInParameter.PROJECT_NUMBER] = "Project_Number",
        [BuiltInParameter.PROJECT_STATUS] = "Project_Status",
    };

    private static bool IsTypeIdentity(BuiltInParameter parameter) => parameter is BuiltInParameter.ALL_MODEL_TYPE_MARK
        or BuiltInParameter.ALL_MODEL_TYPE_COMMENTS or BuiltInParameter.ALL_MODEL_DESCRIPTION
        or BuiltInParameter.ALL_MODEL_MANUFACTURER or BuiltInParameter.ALL_MODEL_MODEL
        or BuiltInParameter.ALL_MODEL_URL or BuiltInParameter.FIRE_RATING or BuiltInParameter.ALL_MODEL_COST;

    private static bool IsImage(BuiltInParameter parameter) => parameter is BuiltInParameter.ALL_MODEL_IMAGE
        or BuiltInParameter.ALL_MODEL_TYPE_IMAGE;

    internal static bool IsSupportedBuiltInGlobalProperty(long id) => id is
        (int)BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS or (int)BuiltInParameter.ALL_MODEL_TYPE_COMMENTS
        or (int)BuiltInParameter.ALL_MODEL_DESCRIPTION or (int)BuiltInParameter.ALL_MODEL_MANUFACTURER
        or (int)BuiltInParameter.ALL_MODEL_MODEL or (int)BuiltInParameter.ALL_MODEL_COST
        or (int)BuiltInParameter.FIRE_RATING;

    internal static void Validate(long parameterId, string? value)
    {
        if (parameterId < int.MinValue)
            throw new RvtWriteRefusedException("unsupported-parameter", $"Writing parameter {parameterId} has not been verified.");
        var parameter = (BuiltInParameter)parameterId;
        if (parameterId == (int)BuiltInParameter.HOST_AREA_COMPUTED)
            throw new RvtWriteRefusedException("read-only-parameter", "Revit computes HOST_AREA_COMPUTED and exposes it as read-only.");
        if (parameterId <= 0 && parameter is not (BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS or BuiltInParameter.ALL_MODEL_MARK)
            && !WallHeightWriter.IsWallDimension(parameter) && !IsTypeIdentity(parameter) && !IsImage(parameter)
            && !Fields.ContainsKey(parameter))
            throw new RvtWriteRefusedException("unsupported-parameter", $"Writing {parameter} has not been verified.");
        if (value is null)
        {
            if (parameterId <= 0)
                throw new RvtWriteRefusedException("unsupported-parameter-clear", "Revit ClearValue requires a shared parameter with HideWhenNoValue.");
            return;
        }
        ValidateText(value);
    }

    internal static void ValidateText(string value)
    {
        try { XmlConvert.VerifyXmlChars(value); }
        catch (XmlException ex)
        {
            throw new RvtWriteRefusedException("invalid-parameter-value", "The text contains a character that the supported parameter values cannot represent.", ex);
        }
    }

    internal static PlanResult? Plan(RootStorage root, PartitionFraming.Tags tags, int idWidth, Edit edit, Pending pending,
        GlobalParameterWriter.PropertyValue? driven = null)
    {
        if (driven is not null && (edit.ElementId == edit.ParameterId
            || (!edit.UserDefined && !IsSupportedBuiltInGlobalProperty(edit.ParameterId))))
            throw Unverified("The global parameter does not control a verified project property.");
        bool clearing = edit.Value is null && driven is null;
        bool comments = !edit.UserDefined && edit.Parameter == BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS;
        bool mark = !edit.UserDefined && edit.Parameter is BuiltInParameter.ALL_MODEL_MARK or BuiltInParameter.ALL_MODEL_TYPE_MARK;
        bool typeIdentity = !edit.UserDefined && IsTypeIdentity(edit.Parameter);
        bool image = !edit.UserDefined && IsImage(edit.Parameter);
        bool projectInfo = !edit.UserDefined && !comments && !mark && !typeIdentity && !image;
        bool possibleGlobalEdit = edit.UserDefined && edit.ElementId == edit.ParameterId;
        if (TfSchemaReader.Read(root) is not { } schema)
            throw Unverified("The source schema could not be read exactly.");

        var formats = new FormatsSchema(schema);
        if (ElemTableLayout.IdWidth(formats) != idWidth || PagedGzipStream.Read(root, "Global", "ElemTable") is not { } table
            || ElemTableLayout.TryParse(table, formats) is not { } elements)
            throw Unverified("Global/ElemTable could not be read exactly.");
        var elementIds = elements.Select(row => row.Id).ToHashSet();
        if (!elementIds.Contains(edit.ElementId))
            throw new RvtWriteRefusedException("missing-element", $"Element {edit.ElementId} was not found in the element table.");
        if (!edit.UserDefined && WallHeightWriter.IsWallDimension(edit.Parameter))
            return WallHeightWriter.Plan(root, tags, idWidth, schema, elementIds, edit, pending);
        if (edit.UserDefined && !elementIds.Contains(edit.ParameterId))
            throw new RvtWriteRefusedException("missing-parameter-definition", $"Parameter definition {edit.ParameterId} was not found in the element table.");

        var walker = new WideBlockWalker(schema, (c, f) => (c.Name, f.Name) switch
        {
            ("Element", "m_id" or "m_pParamValueSetAString") => f.Name,
            ("FamilySymbol", "m_active") when edit.UserDefined => "FamilySymbol.m_active",
            ("ElementHeader", "m_categroryId" or "m_designOptionId") => f.Name,
            ("ParamValueDouble" or "ParamValueInt" or "ParamValueAString" or "ParamValueElementId", "m_paramId") => f.Name,
            ("ParamValueAString", "m_value") => f.Name,
            ("Element", "m_pParamValueSetDouble") or ("ParamValueDouble", "m_value")
                when edit.ParameterId == (int)BuiltInParameter.ALL_MODEL_COST => f.Name,
            _ => (edit.UserDefined || image ? UserParameterWriter.CaptureKey(c, f) : null)
                ?? (image ? ImportedImageReference.CaptureKey(c, f) ?? LinkedImageReference.CaptureKey(c, f) : null)
                ?? (!projectInfo ? LoadedFamilyParameterWriter.CaptureKey(c, f) : null)
                ?? (edit.UserDefined ? ParameterFormulaEvaluator.CaptureKey(c, f) : null)
                ?? GlobalParameterWriter.CaptureKey(c, f),
        }, edit.UserDefined ? ["ForgeTypeId"] : []);
        var bindings = new Dictionary<long, byte[]?>();
        long imageId = image ? ImportedImageReference.ReadId(edit.Value!) : -1;
        var imageRecords = imageId > 0 ? new Dictionary<long, WideBlockWalker.Walk?>() : null;
        var seenImageRecords = imageRecords is not null ? new HashSet<long>() : null;
        bool imageTargetsLevel = false;
        long? referenceId = edit.UserDefined ? UserParameterWriter.ReferenceId(edit.Value) : imageId > 0 ? imageId : null;
        byte[]? reference = null;
        bool sawReference = false;
        bool sawReferenceHeader = false;
        bool mainModelReference = false;
        ulong terminator = idWidth == 8 ? ulong.MaxValue : uint.MaxValue;
        byte[]? definition = null;
        byte[]? body = null;
        int? category = null;
        bool sawHeader = false;
        int partition = -1;
        var globals = new HashSet<long>();
        ScanPartitions(root, tags, idWidth, (number, record) =>
        {
            if (record.Block != 0 || record.Owner == terminator)
                return;
            if (!projectInfo && record.Channel == 101)
            {
                if (referenceId is long targetId && record.Owner == (ulong)targetId)
                {
                    mainModelReference = !sawReferenceHeader && ReadMainModelHeader(record) is not null;
                    sawReferenceHeader = true;
                }
                if (record.Owner != (ulong)edit.ElementId)
                    return;
                if (sawHeader)
                {
                    if (edit.UserDefined)
                        throw Unverified($"Element {edit.ElementId} has more than one project header.");
                    category = null;
                    return;
                }
                sawHeader = true;
                if (ReadMainModelHeader(record) is { } fields
                    && UserParameterWriter.Integer(fields, "m_categroryId") is >= int.MinValue and <= int.MaxValue and long id)
                    category = (int)id;
            }
            if (record.Channel != 102)
                return;
            if (!projectInfo && record.Body.Length >= 2
                && walker.Class(BinaryPrimitives.ReadUInt16LittleEndian(record.Body.Span))?.Name == "ParamElemGlobal"
                && (!elementIds.Contains((long)record.Owner) || !globals.Add((long)record.Owner)))
                throw Unverified("A global parameter does not have a unique project record.");
            if (imageRecords is not null && elementIds.Contains((long)record.Owner))
            {
                long id = (long)record.Owner;
                int tag = record.Body.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(record.Body.Span) : -1;
                string? name = walker.Class(tag)?.Name;
                if (!seenImageRecords!.Add(id))
                    imageRecords[id] = null;
                else if (name == "ImageHolder" || id == imageId && (name is "ImageSymbol" or "Level"))
                {
                    imageRecords.Add(id, walker.Run(record.Body.ToArray(), 0, record.Body.Length, tag));
                    imageTargetsLevel |= name == "Level";
                }
            }
            if (edit.UserDefined)
            {
                if (referenceId is long target && record.Owner == (ulong)target && elementIds.Contains(target))
                {
                    string? referenceClass = record.Body.Length >= 2
                        ? walker.Class(BinaryPrimitives.ReadUInt16LittleEndian(record.Body.Span))?.Name : null;
                    reference = !sawReference && (referenceClass is "MaterialElem" or "NestedFamSymRefElem" or "FamilySymbol"
                        || IsProjectElementReferenceClass(referenceClass))
                        ? record.Body.ToArray() : null;
                    sawReference = true;
                }
                if (record.Owner == (ulong)edit.ParameterId)
                {
                    if (definition is not null)
                        throw Unverified($"Parameter definition {edit.ParameterId} has more than one project record.");
                    definition = record.Body.ToArray();
                }
                if (record.Body.Length >= 2 && elementIds.Contains((long)record.Owner)
                    && walker.Class(BinaryPrimitives.ReadUInt16LittleEndian(record.Body.Span))?.Name == "ParamBinding"
                    && !bindings.TryAdd((long)record.Owner, record.Body.ToArray()))
                    bindings[(long)record.Owner] = null;
            }
            if (record.Owner != (ulong)edit.ElementId)
                return;
            if (body is not null)
                throw Unverified($"Element {edit.ElementId} has more than one project record.");
            body = record.Body.ToArray();
            partition = number;
        }, pending);

        WideBlockWalker.Walk? ReadMainModelHeader(PartitionFraming.Record record)
        {
            int tag = record.Body.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(record.Body.Span) : -1;
            if (walker.Class(tag)?.Name != "ElementHeader")
                return null;
            var fields = walker.Run(record.Body.ToArray(), 0, record.Body.Length, tag, rootOnly: true);
            return fields.RootRead && UserParameterWriter.Integer(fields, "m_designOptionId") == -1 ? fields : null;
        }

        if (body is null)
            throw Unverified($"No project record was found for element {edit.ElementId}.");
        int classId = body.Length < 2 ? -1 : BinaryPrimitives.ReadUInt16LittleEndian(body);
        string? ownerClass = walker.Class(classId)?.Name;
        if (ownerClass == "ParamElemGlobal")
        {
            if (!possibleGlobalEdit)
                throw new RvtWriteRefusedException("unsupported-parameter-owner", "Use the global parameter's element ID for both IDs.");
            return GlobalParameterWriter.Plan(root, tags, idWidth, walker, elementIds, globals, edit, pending, partition, body, classId);
        }
        bool loadedFamily = ownerClass is "FamilyInstance" or "FamilySymbol";
        bool doorWindowFurniture = category is (int)BuiltInCategory.OST_Doors or (int)BuiltInCategory.OST_Windows or (int)BuiltInCategory.OST_Furniture;
        bool loadedCategoryIdentity = typeIdentity && !mark && ownerClass == "FamilySymbol"
            && doorWindowFurniture;
        bool loadedCategoryInstance = (comments || image) && ownerClass == "FamilyInstance" && doorWindowFurniture;
        if (!edit.UserDefined && !(edit.Parameter switch
            {
                BuiltInParameter.ALL_MODEL_MARK => ownerClass is "SWall" or "FamilyInstance",
                BuiltInParameter.ALL_MODEL_TYPE_MARK => ownerClass is "BasicWallType" or "FamilySymbol",
                BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS => ownerClass is "SWall" or "FamilyInstance",
                BuiltInParameter.ALL_MODEL_IMAGE => ownerClass is "SWall" or "RoomElem" or "FamilyInstance",
                BuiltInParameter.ALL_MODEL_TYPE_IMAGE => ownerClass == "BasicWallType",
                BuiltInParameter.FIRE_RATING => ownerClass == "BasicWallType"
                    || (ownerClass == "FamilySymbol" && category == (int)BuiltInCategory.OST_Doors),
                _ when typeIdentity => ownerClass is "BasicWallType" or "FamilySymbol",
                _ => ownerClass == "ProjectInfo",
            }))
            throw new RvtWriteRefusedException("unsupported-parameter-owner", $"{edit.Parameter} cannot be written on this element class.");
        if ((mark || typeIdentity || image) && (ownerClass is "SWall" or "BasicWallType") && category != (int)BuiltInCategory.OST_Walls)
            throw Unverified("The wall's category or design option could not be verified for writing.");
        if (image && ownerClass == "RoomElem" && category != (int)BuiltInCategory.OST_Rooms)
            throw Unverified("The room's category or design option could not be verified for writing.");
        if (!edit.UserDefined && !mark && loadedFamily && !loadedCategoryIdentity && !loadedCategoryInstance
            && category != (int)BuiltInCategory.OST_GenericModel)
            throw Unverified("The built-in parameter's availability and writability have not been verified for this family category.");
        var walk = walker.Run(body, 0, body.Length, classId);
        if (!walk.Exact || !walk.Captures.Any(c => c.AtRoot && c.DeclaringClass == "Element" && c.Field == "m_id" && c.Integer == edit.ElementId))
            throw Unverified("The element record could not be read exactly.");
        if (driven is not null && !edit.UserDefined)
        {
            long? master = UserParameterWriter.Integer(walk, "FamilySymbol.m_masterId");
            bool supportedOwner = comments
                ? SchemaRelease.FromSchema(formats) is >= 2022 && (ownerClass == "SWall" && category == (int)BuiltInCategory.OST_Walls
                    || ownerClass == "FamilyInstance" && category == (int)BuiltInCategory.OST_GenericModel)
                : ownerClass == "FamilySymbol" && (doorWindowFurniture || category == (int)BuiltInCategory.OST_GenericModel)
                    && master is -1 or > 0;
            if (!supportedOwner)
                throw Unverified("This built-in global association has not been verified for the element class, category, and release.");
        }
        if (!projectInfo)
        {
            if (walk.Queue.Any(tag => walker.IsA(tag, "GroupMembership")))
                throw Unverified("The element has a group membership that has not been verified for writing.");
            GlobalParameterWriter.VerifyElementParameter(walker, walk, idWidth, elementIds, globals, edit.ParameterId, driven?.GlobalId);
        }

        int targetTag = reference is null ? -1 : BinaryPrimitives.ReadUInt16LittleEndian(reference);
        string? targetClass = walker.Class(targetTag)?.Name;
        var target = reference is null ? null : walker.Run(reference, 0, reference.Length, targetTag);
        long? material = target is not null && referenceId is long materialId
            && UserParameterWriter.MaterialName(walker, target, materialId) is not null
            ? materialId : null;
        long? localElementReference = mainModelReference && IsProjectElementReferenceClass(targetClass)
            && target is { Exact: true, EmptyEntityAt: < 0 } && UserParameterWriter.Integer(target, "m_id") == referenceId
            && (targetClass != "Family" || UserParameterWriter.Integer(target, "FamilyBase.m_inPlace") == 0)
            && (targetClass != "FamilyInstance" || UserParameterWriter.Integer(target, "FamilyInstance.m_superInstanceId") == -1)
            ? referenceId : null;
        bool hasProjectBinding = edit.UserDefined && UserParameterWriter.HasProjectBinding(walker, definition);
        bool sharedProjectReference = false;
        ProjectFamilyReference? projectReference = null;
        if (ownerClass is ("FamilySymbol" or "FamilyInstance" or "SWall" or "BasicWallType" or "RoomElem")
            && targetClass == "FamilySymbol"
            && UserParameterWriter.IsFamilyTypeDefinition(walker, definition))
        {
            string? definitionClass = walker.Class(BinaryPrimitives.ReadUInt16LittleEndian(definition!))?.Name;
            sharedProjectReference = driven is null && hasProjectBinding && definitionClass == "ParamElemExternal";
            if (sharedProjectReference)
                _ = UserParameterWriter.ReadBoundDefinition(walker, ownerClass, category, definition, bindings, edit, clearing: false);
            else
                _ = UserParameterWriter.ReadDefinition(walker, definition!, edit);
            if (target is not { Exact: true, EmptyEntityAt: < 0 } || referenceId is not long id
                || UserParameterWriter.Integer(target, "m_id") != id
                || UserParameterWriter.Integer(target, "FamilySymbol.m_masterId") != -1
                || UserParameterWriter.Integer(target, "FamilySymbol.m_ownerInstanceId") != -1
                || UserParameterWriter.Integer(target, "FamilySymbol.m_familyId") is not (> 0 and long familyId))
                throw Unverified("The reference does not identify a primary project family type.");
            if (UserParameterWriter.Integer(target, "FamilySymbol.m_active") != 1)
                throw Unverified("Assigning this reference requires activating and regenerating the target family type.");
            long? referringType = driven is null && ownerClass == "FamilySymbol"
                && definitionClass == "ParamElemFamily"
                && UserParameterWriter.Integer(walk, "FamilySymbol.m_masterId") == -1
                && UserParameterWriter.Integer(walk, "FamilySymbol.m_ownerInstanceId") == -1
                && UserParameterWriter.Integer(walk, "FamilySymbol.m_active") == 1 ? edit.ElementId : null;
            projectReference = ProjectFamilyReference.Read(root, tags, idWidth, schema, elementIds, id, familyId, pending,
                referringType, inspectDependencies: !sharedProjectReference);
        }
        bool projectBoundAssignment = edit.UserDefined && loadedFamily && !clearing
            && (ownerClass == "FamilySymbol" && UserParameterWriter.Integer(walk, "FamilySymbol.m_masterId") is > 0
                || hasProjectBinding);
        if (edit.UserDefined && loadedFamily && !clearing && !projectBoundAssignment)
            return LoadedFamilyParameterWriter.Plan(root, tags, idWidth, walker, elementIds, globals, category, definition, edit, material,
                target, projectReference, partition, body, walk, pending, ownerClass == "FamilyInstance", driven);

        if (imageRecords is not null && (!mainModelReference || !IsVerifiedImageTarget(imageRecords)))
            throw new RvtWriteRefusedException("invalid-parameter-value", "The image ID does not identify a verified local image or level in this project.");
        var (storage, replacement, quantity) = driven is { } property ? PrepareDriven(property) : edit.UserDefined
            ? UserParameterWriter.Prepare(walker, ownerClass, category, definition, bindings, edit, idWidth, material, target,
                projectReference, localElementReference)
            : edit.Parameter switch
            {
                BuiltInParameter.ALL_MODEL_COST => (StorageType.Double, EncodeCost(edit.Value!), (UnitType?)UnitType.UT_Currency),
                _ when image => (StorageType.ElementId, UserParameterWriter.EncodeElementId(imageId, idWidth), null),
                _ => (StorageType.String, EncodeText(edit.Value!), null),
            };
        bool genericNamedIdentity = typeIdentity && !mark && ownerClass == "FamilySymbol" && category == (int)BuiltInCategory.OST_GenericModel
            && walk.Captures.Any(c => c.DeclaringClass == "NamedParam" && c.Field == "m_paramId" && c.Integer == edit.ParameterId);
        bool checkTypeIdentity = loadedCategoryIdentity || genericNamedIdentity;
        if (checkTypeIdentity && LoadedFamilyParameterWriter.TryPlanTypeIdentity(root, tags, idWidth, walker, elementIds, globals, category,
            edit, walk, storage, replacement, pending, driven, out var identityPlan))
            return identityPlan;
        if (loadedFamily && !checkTypeIdentity)
            LoadedFamilyParameterWriter.VerifyLocalValueEdit(root, tags, idWidth, walker, elementIds, category, edit, walk, storage,
                target, sharedProjectReference ? null : projectReference, pending, ownerClass == "FamilyInstance");
        var original = FindValue(walker, walk, edit, storage);
        if (clearing)
        {
            if (walk.EmptyEntityAt >= 0)
                throw Unverified("The parameter store could not be read exactly for clearing.");
            if (original is not { } row)
                return null;
            byte[] cleared = RemoveValue(walk, body, idWidth, storage, row);
            var clearedWalk = walker.Run(cleared, 0, cleared.Length, classId);
            if (clearedWalk.EmptyEntityAt >= 0 || FindValue(walker, clearedWalk, edit, storage) is not null)
                throw Unverified("The cleared parameter store could not be read back exactly.");
            return new PlanResult([new RvtPartitionRecordEdit(partition, 102, (ulong)edit.ElementId, cleared)], null);
        }
        byte[]? xml = null;
        byte[] updated;
        if (original?.Value is { } stored)
        {
            if (projectInfo)
                xml = EditProjectInformation(root, pending, Fields[edit.Parameter], stored.Text!, edit.Value!);
            int length = storage == StorageType.String ? sizeof(uint) + sizeof(char) * stored.Text!.Length : replacement.Length;
            if (driven is { } expected && !body.AsSpan(stored.ValueOffset, length).SequenceEqual(expected.Previous))
                throw Unverified("The controlled property disagrees with the saved global value.");
            if (quantity == UnitType.UT_Currency || ParameterMeasurement.IsAdditionalSpec(quantity))
                ParameterFormulaEvaluator.VerifyStableChange(stored.Real, BinaryPrimitives.ReadDoubleLittleEndian(replacement));
            if (body.AsSpan(stored.ValueOffset, length).SequenceEqual(replacement))
                return null;
            updated = ReplaceValue(body, stored.ValueOffset, length, replacement);
        }
        else
        {
            if (driven is not null)
                throw Unverified("The controlled property has no verified saved value.");
            if (walk.EmptyEntityAt >= 0 || projectInfo || (comments && ownerClass == "SWall" && category != (int)BuiltInCategory.OST_Walls))
                throw Unverified("The first value requires a verified main-model element and parameter store.");
            if (storage == StorageType.String && edit.Value!.Length == 0 && edit.UserDefined && !projectBoundAssignment)
                throw new RvtWriteRefusedException("invalid-parameter-value", "The first text value must be non-empty.");
            updated = InsertValue(formats, walker, walk, body, idWidth, edit, storage, replacement);
        }

        var verified = walker.Run(updated, 0, updated.Length, classId);
        if (FindValue(walker, verified, edit, storage)?.Value is not { } readBack
            || (original?.Value is { } previous && readBack.ValueOffset != previous.ValueOffset)
            || readBack.ValueOffset > updated.Length - replacement.Length
            || !updated.AsSpan(readBack.ValueOffset, replacement.Length).SequenceEqual(replacement))
            throw Unverified("The updated parameter could not be read back.");
        return new PlanResult([new RvtPartitionRecordEdit(partition, 102, (ulong)edit.ElementId, updated)],
            xml is null ? null : ("ProjectInformation", xml));

        bool IsVerifiedImageTarget(IReadOnlyDictionary<long, WideBlockWalker.Walk?> records) => imageTargetsLevel
            ? records.GetValueOrDefault(imageId) is { Exact: true, EmptyEntityAt: < 0 } level
                && UserParameterWriter.Integer(level, "m_id") == imageId
            : ImportedImageReference.ReadName(walker, records, imageId) is not null
                || LinkedImageReference.IsVerifiedTarget(walker, records, imageId);

        (StorageType Storage, byte[] Replacement, UnitType? Quantity) PrepareDriven(GlobalParameterWriter.PropertyValue value)
        {
            if (!edit.UserDefined)
            {
                var format = edit.Parameter == BuiltInParameter.ALL_MODEL_COST
                    ? new FormulaParameterStorage(StorageType.Double, UnitType.UT_Currency, ParameterFormulaEvaluator.ValueKind.Number)
                    : new FormulaParameterStorage(StorageType.String, UnitType.UT_Number, ParameterFormulaEvaluator.ValueKind.Text);
                value.VerifyFormat(format);
                return (format.Storage, value.Replacement, null);
            }
            var (definitionWalk, slot) = UserParameterWriter.ReadBoundDefinition(walker, ownerClass, category, definition, bindings,
                edit, clearing: false);
            var target = FormulaParameterStorage.Read(walker, definitionWalk, slot, includeCurrency: true);
            // Revit 2020–2021 refuse Slope globals on Number parameters bound in the project.
            value.VerifyFormat(target, allowSlopeGlobalToNumber: SchemaRelease.FromSchema(formats) is >= 2022);
            return (target.Storage, value.Replacement, target.Unit);
        }
    }

    internal static byte[] ReplaceValue(byte[] body, int start, int length, ReadOnlySpan<byte> replacement)
    {
        byte[] updated = new byte[checked(body.Length - length + replacement.Length)];
        body.AsSpan(0, start).CopyTo(updated);
        replacement.CopyTo(updated.AsSpan(start));
        body.AsSpan(start + length).CopyTo(updated.AsSpan(start + replacement.Length));
        return updated;
    }

    private static bool IsProjectElementReferenceClass(string? name) => name is
        "BasicWallType" or "CoverType" or "Level" or "Family" or "FamilyInstance";

    internal static void ScanPartitions(RootStorage root, PartitionFraming.Tags tags, int idWidth,
        Action<int, PartitionFraming.Record> visit, Pending pending)
    {
        var partitions = root.OpenStorage("Partitions");
        foreach (var entry in partitions.EnumerateEntries())
        {
            if (entry.Type != EntryType.Stream || !int.TryParse(entry.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
                continue;
            using var stream = partitions.OpenStream(entry.Name);
            var raw = new byte[stream.Length];
            stream.ReadExactly(raw);
            if (!PartitionFraming.Walk(raw, tags, idWidth, record =>
                {
                    if (record.Block == 0 && pending.RemovedProjectOwners.Contains((number, record.Owner)))
                        return;
                    if (pending.Records.TryGetValue((number, record.Block, record.Channel, record.Owner), out var edit))
                        record = record with { Body = edit.Body };
                    visit(number, record);
                }, out string? failure, out _))
                throw Unverified($"Partition {entry.Name} could not be read exactly ({failure}).");
        }
    }

    internal static byte[] EncodeText(string value)
    {
        byte[] result = new byte[checked(sizeof(uint) + Encoding.Unicode.GetByteCount(value))];
        BinaryPrimitives.WriteInt32LittleEndian(result, value.Length);
        Encoding.Unicode.GetBytes(value, result.AsSpan(sizeof(uint)));
        return result;
    }

    internal static byte[] EncodeCost(string value)
    {
        if (ParameterMeasurement.Parse(UnitType.UT_Number, value) is not double cost)
            throw new RvtWriteRefusedException("invalid-parameter-value", "Currency and Cost require a finite invariant number.");
        return EncodeDouble(cost);
    }

    internal static byte[] EncodeDouble(double value)
    {
        var result = new byte[sizeof(double)];
        BinaryPrimitives.WriteDoubleLittleEndian(result, value);
        return result;
    }

    private static string ValueSuffix(StorageType storage) => storage switch
    {
        StorageType.String => "AString",
        StorageType.Integer => "Int",
        StorageType.ElementId => "ElementId",
        _ => "Double",
    };

    private static string ValueClass(StorageType storage) => "ParamValue" + ValueSuffix(storage);
    private static string StoreClass(StorageType storage) => "ParamValueSet" + ValueSuffix(storage);

    private static WideBlockWalker.Capture StorePointer(WideBlockWalker.Walk walk, StorageType storage)
    {
        string field = "m_p" + StoreClass(storage);
        if (!walk.Exact || walk.Captures.Where(c => c.AtRoot && c.DeclaringClass == "Element" && c.Field == field
            && c.Kind == WideBlockWalker.CaptureKind.PointerClass).ToArray() is not [var store])
            throw Unverified("The root parameter store could not be read exactly.");
        return store;
    }

    private static SchemaField[] StoreRow(WideBlockWalker walker, int tag, StorageType storage)
    {
        if (walker.Class(tag) is not { Base: 0 } store || store.Name != StoreClass(storage)
            || store.Fields.Where(f => f.Stored).ToArray() is not [{ Name: "m_paramSet", Type: 14, Mods: 0x50, Ref: int rowTag }]
            || walker.Class(rowTag) is not { Base: 0 } row || row.Name != ValueClass(storage))
            throw Unverified("The parameter store layout could not be verified.");
        var fields = row.Fields.Where(f => f.Stored).ToArray();
        if (fields is not ([{ Name: "m_paramId" }, { Name: "m_value" }]
            or [{ Name: "m_value" }, { Name: "m_paramId" }]))
            throw Unverified("The parameter row layout could not be verified.");
        return fields;
    }

    internal static (WideBlockWalker.Capture Id, WideBlockWalker.Capture Value)? FindValue(
        WideBlockWalker walker, WideBlockWalker.Walk walk, Edit edit, StorageType storage)
    {
        var store = StorePointer(walk, storage);
        string valueClass = ValueClass(storage);
        var matches = walk.Captures
            .Select((capture, index) => (capture, index))
            .Where(p => p.capture.DeclaringClass.StartsWith("ParamValue", StringComparison.Ordinal)
                && p.capture.Field == "m_paramId" && p.capture.Integer == edit.ParameterId)
            .ToArray();
        if (matches.Length == 0)
            return null;
        if (matches is not [{ capture: { QueueIndex: >= 0 } id, index: var index }]
            || id.DeclaringClass != valueClass
            || id.QueueIndex != StoreSlot(walk, store))
            throw Unverified($"No unique stored value was found for parameter {edit.ParameterId}.");
        var row = StoreRow(walker, (int)store.Integer, storage);
        bool idFirst = row[0].Name == "m_paramId";
        if (storage == StorageType.ElementId && (!idFirst || !IsElementId(walker, row[1])))
            throw Unverified("The reference parameter's stored ID layout could not be verified.");
        int valueIndex = index + (idFirst ? 1 : -1);
        if ((uint)valueIndex >= walk.Captures.Count
            || walk.Captures[valueIndex] is not { Field: "m_value", ValueOffset: >= 0 } value
            || value.DeclaringClass != valueClass || value.QueueIndex != id.QueueIndex
            || value.Kind != (storage == StorageType.String ? WideBlockWalker.CaptureKind.Text
                : storage == StorageType.Double ? WideBlockWalker.CaptureKind.Real : WideBlockWalker.CaptureKind.Integer))
            throw Unverified($"The stored value for parameter {edit.ParameterId} does not match its definition.");
        return (id, value);
    }

    private static byte[] RemoveValue(WideBlockWalker.Walk walk, byte[] body, int idWidth, StorageType storage,
        (WideBlockWalker.Capture Id, WideBlockWalker.Capture Value) row)
    {
        int valueLength = storage switch
        {
            StorageType.String => checked(sizeof(uint) + sizeof(char) * row.Value.Text!.Length),
            StorageType.Integer => sizeof(int),
            StorageType.Double => sizeof(double),
            _ => idWidth,
        };
        int slot = row.Id.QueueIndex;
        int countAt = walk.QueueStart[slot];
        int storeEnd = slot + 1 == walk.Queue.Count ? body.Length : walk.QueueStart[slot + 1];
        int start = Math.Min(row.Id.ValueOffset, row.Value.ValueOffset);
        int end = Math.Max(row.Id.ValueOffset + idWidth, row.Value.ValueOffset + valueLength);
        int count = walk.Captures.Count(c => c.QueueIndex == slot && c.DeclaringClass == ValueClass(storage) && c.Field == "m_paramId");
        if (start < countAt + sizeof(uint) || end > storeEnd || end - start != idWidth + valueLength
            || count == 0 || BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(countAt)) != count)
            throw Unverified("The parameter row could not be located exactly for clearing.");
        byte[] updated = ReplaceValue(body, start, end - start, []);
        BinaryPrimitives.WriteUInt32LittleEndian(updated.AsSpan(countAt), (uint)count - 1);
        return updated;
    }

    private static byte[] InsertValue(FormatsSchema schema, WideBlockWalker walker, WideBlockWalker.Walk walk,
        byte[] body, int idWidth, Edit edit, StorageType storage, byte[] replacement)
    {
        var store = StorePointer(walk, storage);
        if (schema.IdOf(StoreClass(storage)) is not int tag || store.Integer != tag && store.Integer != 0)
            throw Unverified("The parameter store class could not be verified.");
        var fields = StoreRow(walker, tag, storage);
        bool idFirst = fields[0].Name == "m_paramId";
        var idField = fields[idFirst ? 0 : 1];
        var valueField = fields[idFirst ? 1 : 0];
        if (!(IsElementId(walker, idField) || idField is { Type: 4, Mods: 0 } && idWidth == 4)
            || (storage switch
            {
                StorageType.String => !idFirst || valueField is not { Type: 8, Mods: 0 or 0x60 },
                StorageType.Integer => !idFirst || valueField is not { Type: 4, Mods: 0 },
                StorageType.ElementId => !idFirst || !IsElementId(walker, valueField),
                _ => valueField is not { Type: 7, Mods: 0 },
            }))
            throw Unverified("The parameter row encoding could not be verified.");

        byte[] row = new byte[checked(idWidth + replacement.Length)];
        var idBytes = row.AsSpan(idFirst ? 0 : replacement.Length, idWidth);
        if (idWidth == 8)
            BinaryPrimitives.WriteInt64LittleEndian(idBytes, edit.ParameterId);
        else
            BinaryPrimitives.WriteInt32LittleEndian(idBytes, checked((int)edit.ParameterId));
        replacement.CopyTo(row.AsSpan(idFirst ? idWidth : 0));

        if (store.Integer == 0)
        {
            if (store.ValueOffset < 0 || (uint)store.QueuePosition > walk.Queue.Count
                || BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(store.ValueOffset)) != 0)
                throw Unverified("The empty parameter-store pointer could not be verified.");
            byte[] payload = new byte[checked(sizeof(uint) + row.Length)];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, 1);
            row.CopyTo(payload.AsSpan(sizeof(uint)));
            int start = store.QueuePosition == walk.Queue.Count ? body.Length : walk.QueueStart[store.QueuePosition];
            Span<byte> pointer = stackalloc byte[sizeof(uint) + sizeof(ushort)];
            BinaryPrimitives.WriteUInt32LittleEndian(pointer, uint.MaxValue);
            BinaryPrimitives.WriteUInt16LittleEndian(pointer[sizeof(uint)..], checked((ushort)tag));
            return ReplaceValue(ReplaceValue(body, start, 0, payload), store.ValueOffset, sizeof(uint), pointer);
        }

        int slot = StoreSlot(walk, store);
        var ids = walk.Captures.Where(c => c.QueueIndex == slot && c.DeclaringClass == ValueClass(storage) && c.Field == "m_paramId").ToArray();
        int countAt = walk.QueueStart[slot];
        int endAt = slot + 1 == walk.Queue.Count ? body.Length : walk.QueueStart[slot + 1];
        if (BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(countAt)) != ids.Length
            || ids.Any(c => c.ValueOffset < countAt + sizeof(uint) || c.ValueOffset >= endAt)
            || ids.Zip(ids.Skip(1)).Any(pair => pair.First.Integer >= pair.Second.Integer))
            throw Unverified("The existing parameter rows are not uniquely sorted by ID.");
        int next = Array.FindIndex(ids, id => id.Integer > edit.ParameterId);
        int insertAt = next < 0 ? endAt : ids[next].ValueOffset - (idFirst ? 0 : sizeof(double));
        byte[] updated = ReplaceValue(body, insertAt, 0, row);
        BinaryPrimitives.WriteUInt32LittleEndian(updated.AsSpan(countAt), (uint)ids.Length + 1);
        return updated;
    }

    private static bool IsElementId(WideBlockWalker walker, SchemaField field) =>
        field is { Type: 14, Mods: 0, Ref: int tag } && walker.Class(tag)?.Name == "ElementId";

    private static int StoreSlot(WideBlockWalker.Walk walk, WideBlockWalker.Capture pointer)
    {
        int slot = pointer.QueuePosition;
        if (slot < 0)
        {
            slot = walk.Queue.IndexOf((int)pointer.Integer);
            if (slot != walk.Queue.LastIndexOf((int)pointer.Integer))
                slot = -1;
        }
        if (pointer.Integer <= 0 || (uint)slot >= walk.Queue.Count || walk.Queue[slot] != pointer.Integer)
            throw Unverified("The root parameter store is ambiguous.");
        return slot;
    }

    private static byte[]? EditProjectInformation(RootStorage root, Pending pending, string field, string oldValue, string newValue)
    {
        if (pending.OpenMetadata(root, "ProjectInformation") is not { } source)
            throw Unverified("The ProjectInformation stream is missing.");
        using (source)
        {
            try
            {
                using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
                if (zip.Entries.Count != 1 || !zip.Entries[0].Name.EndsWith(".project.xml", StringComparison.Ordinal))
                    throw Unverified("The ProjectInformation archive layout is unsupported.");
                var entry = zip.Entries[0];
                using var xml = XmlReader.Create(entry.Open(), new XmlReaderSettings { CloseInput = true, MaxCharactersInDocument = 16 * 1024 * 1024 });
                var doc = XDocument.Load(xml, LoadOptions.PreserveWhitespace);
                XNamespace atom = "http://www.w3.org/2005/Atom";
                XNamespace partAtom = "urn:schemas-autodesk-com:partatom";
                var values = doc.Elements(atom + "entry").Elements(partAtom + "features").Elements(partAtom + "feature")
                    .Elements(partAtom + "group").Elements(atom + field).Where(e => (string?)e.Attribute("type") == "system").ToArray();
                if (values is not [var target] || target.HasElements || NormalizeXmlText(target.Value) != NormalizeXmlText(oldValue))
                    throw Unverified($"The stored parameter and its ProjectInformation XML field '{field}' do not match.");
                if (oldValue == newValue)
                    return null;

                target.Value = newValue;
                using var buffer = new MemoryStream();
                using (var output = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var outputEntry = output.CreateEntry(entry.FullName);
                    outputEntry.LastWriteTime = entry.LastWriteTime;
                    using var destination = outputEntry.Open();
                    using var writer = XmlWriter.Create(destination, new XmlWriterSettings { NewLineHandling = NewLineHandling.Entitize });
                    doc.Save(writer);
                }
                return buffer.ToArray();
            }
            catch (Exception ex) when (ex is InvalidDataException or XmlException)
            {
                throw new RvtWriteRefusedException("unverified-parameter-storage", "The ProjectInformation stream could not be decoded.", ex);
            }
        }
    }

    private static string NormalizeXmlText(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    internal static RvtWriteRefusedException Unverified(string message) => new("unverified-parameter-storage", message);
}
