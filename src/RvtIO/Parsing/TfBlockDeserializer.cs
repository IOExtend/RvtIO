using System.Text.Json;
using RvtIO.Internal;
using RvtIO.Model;

namespace RvtIO.Parsing;

/// <summary>
/// Schema-driven deserializer for Tf class-payload blocks (value-encoding grammar).
///
/// Framing: BLOCK = [eid:u32][hash:u32][dataSize:u32][classId:u16][payload], where
/// payload spans [pos+14, pos+16+dataSize) (len == dataSize+2) and its last 4 bytes
/// are a u32 == dataSize (self-check footer).
///
/// Field order: root base class first, then subclasses, fields in schema order.
/// Scalars: 01/02/03 = u8, 04 i32, 05 u32, 06 f32, 07 f64, 08 [len:u32][UTF-16],
/// 09 GUID, 0A i64, 0D = cnt x f64 POD. Mods high nibble: 0 scalar, 1 fixed array
/// (cnt, no count written), 5 dynamic [count:u32], 6 string. 0E refs: ref==20 is a
/// bare i32 ElementId; ref!=20 (m=0) is an inline struct; pointer flavors are a u32 v
/// (0=null, else u16 classId tag, payload DEFERRED to block tail in queue order;
/// tag 4 = hand-serialized GInfo); poly scalar (m=0, no ref) is a u32 (0 null,
/// FFFFFFFF anonymous+tag, else plain handle).
///
/// The four Element param stores (tags 0xB47 Double / 0xB49 Int / 0xB46 AString /
/// 0xB48 ElementId) are the FIRST deferred objects: [count:u32] then records:
/// Double [f64 value][i32 paramId] (value first!), Int/ElemId [i32 id][i32 val],
/// AString [i32 id][u32 len][UTF-16]. Some Double params are runtime-computed and not stored in the file.
/// </summary>
internal static partial class TfBlockDeserializer
{
    internal sealed record TfField(string Name, byte Type, byte Mods, int? Ref, int? Cnt, int Declaring = 0);
    internal sealed record TfClass(string Name, int? Base, List<TfField> Fields);

    internal sealed class NativeParams
    {
        /// <summary>All root-queued native parameter stores were decoded before the
        /// non-parameter deferred tail. False also covers a partially decoded store.</summary>
        public bool RootParameterStoresComplete { get; set; }
        /// <summary>Class tags of the cells of the element's CellList, when the deferred walk reached that object.</summary>
        public List<int>? CellClasses { get; set; }
        /// <summary>The deferred walk consumed every queued object of the record (so the NamedParams and cells are complete).</summary>
        public bool TailWalkComplete { get; set; }
        public List<(int ParamId, double Value)> Doubles { get; } = new();
        public List<(int ParamId, int Value)> Ints { get; } = new();
        public List<(int ParamId, string Value)> Strings { get; } = new();
        public List<(int ParamId, int Value)> ElementIds { get; } = new();

        /// <summary>Named schema fields captured during the walk (interesting ElementId
        /// refs like m_masterSymbolId / m_scheduleOnlyLevelId and small enums).</summary>
        public Dictionary<string, int> Fields { get; } = new();
        public Dictionary<string, double> DoubleFields { get; } = new();
        public Dictionary<string, string> StringFields { get; } = new();

        /// <summary>FamilyParams.m_params items in stored order (family parameters of the element).</summary>
        public List<NamedParamRecord> NamedParams { get; } = new();
        public int Count => Doubles.Count + Ints.Count + Strings.Count + ElementIds.Count
            + Fields.Count + DoubleFields.Count + StringFields.Count;
    }

    /// <summary>One NamedParam of FamilyParams.m_params. Which of Value/ElemId/Int/Str is meaningful depends on the
    /// parameter's storage type.</summary>
    internal sealed record NamedParamRecord(int ParamId, string? Str, double Value, int ElemId, int Int,
        bool Instance, bool Reporting);

    /// <param name="LevelInterval">The elevations (feet) between which the grid is shown in plan views: the vertical
    /// extent of the grid plane, null when the plane has no vertical axis.</param>
    internal sealed record GridDatum(string Name, double[] StartPoint,
        double[] EndPoint, double[] CutVector, double[]? LevelInterval = null, double[]? XVector = null,
        double[]? Envelope = null, double[]? Origin = null, double[]? YVector = null);

    internal sealed record CurtainFamilyHost(int HostId, ushort ReferenceClass, int? ReplacementElementId = null);

    /// <summary>Own FamilyInstance.m_refs containing one complete CurtainCellFamRef or
    /// MullionFamRef. This is curtain membership, NOT InsertableInst's cutting host.
    /// A panel replacement is admitted only through its owned HostPanelRefData object.
    /// Unrelated deferred geometry after the expected export is outside this scope.</summary>
    internal static CurtainFamilyHost? ExtractCurtainFamilyHost(
        byte[] data, int start, uint size, ushort classId)
    {
        long end = (long)start + 16 + size;
        if (classId != 1794 || start < 0 || size < 4 || start > data.Length - 16
            || end > data.Length || BitConverter.ToUInt32(data, start + 8) != size
            || BitConverter.ToUInt16(data, start + 12) != classId
            || BitConverter.ToUInt32(data, (int)end - 4) != size)
            return null;
        var fields = SerializationFields(classId);
        if (fields == null) return null;
        var cursor = new Cursor { D = data, Pos = start + 14, End = (int)end - 4, RootClassId = classId };
        var queue = new List<(ushort Cls, uint Id)>();
        int referenceIndex = -1;
        var root = new NativeParams();
        try
        {
            foreach (var field in fields)
            {
                if (field.Declaring == 1794 && field.Name == "m_refs")
                {
                    if (field.Type != 14 || field.Mods != 81 || ReadU32(cursor) != 1)
                        return null;
                    int before = queue.Count;
                    ReadPtr(cursor, queue);
                    if (queue.Count != before + 1 || queue[before].Cls is not (897 or 2518))
                        return null;
                    referenceIndex = before;
                }
                else ReadField(cursor, field, queue, 0, root);
            }
            uint owner = BitConverter.ToUInt32(data, start);
            if (referenceIndex < 0 || !root.Fields.TryGetValue("m_id", out int rootOwner)
                || rootOwner <= 0 || owner != (uint)rootOwner)
                return null;
            for (int index = 0; index < referenceIndex; index++)
                ReadDeferredObject(cursor, queue[index].Cls, queue);

            ushort referenceClass = queue[referenceIndex].Cls;
            var referenceFields = SerializationFields(referenceClass);
            if (referenceFields == null) return null;
            int? host = null, driver = null, replacement = null;
            int replacementIndex = -1;
            foreach (var field in referenceFields)
            {
                if (field.Declaring == referenceClass && field.Name == "m_hostId"
                    && field.Type == 14 && field.Ref == 20 && field.Mods == 0)
                    host = ReadI32(cursor);
                else if (field.Declaring == referenceClass && field.Name == "m_driverId"
                    && field.Type == 4 && field.Mods == 0)
                    driver = ReadI32(cursor);
                else if (referenceClass == 897 && field.Name == "m_hostPanelRefData")
                {
                    if (field.Type != 14 || field.Mods != 1) return null;
                    int pointerStart = cursor.Pos, before = queue.Count;
                    ReadPtr(cursor, queue);
                    if (BitConverter.ToUInt32(data, pointerStart) == 0) continue;
                    if (queue.Count != before + 1 || queue[before].Cls != 2175)
                        return null;
                    replacementIndex = before;
                }
                else if (referenceClass == 2518 && field.Name == "m_wasSuspendedOnFail")
                {
                    if (field.Type != 1 || field.Mods != 0 || !cursor.Take(1)
                        || data[cursor.Pos - 1] != 0)
                        return null;
                }
                else ReadField(cursor, field, queue, 1, null);
            }
            if (replacementIndex >= 0)
            {
                for (int index = referenceIndex + 1; index < replacementIndex; index++)
                    ReadDeferredObject(cursor, queue[index].Cls, queue);
                var replacementFields = SerializationFields(2175);
                if (replacementFields is not { Count: 1 }) return null;
                var field = replacementFields[0];
                if (field.Declaring != 2175 || field.Name != "m_panelHostObjId"
                    || field.Type != 14 || field.Mods != 0 || field.Ref != 20)
                    return null;
                replacement = ReadI32(cursor);
                if (replacement <= 0 || replacement == rootOwner || replacement == host) return null;
            }
            return host is > 0 && host != rootOwner && driver == -1
                ? new CurtainFamilyHost(host.Value, referenceClass, replacement) : null;
        }
        catch (DecodeFail) { return null; }
    }

    /// <summary>Schema field names whose values are captured into NativeParams.Fields.</summary>
    /// <summary>Diagnostic (RVTIO_ALL_FIELDS=1): capture every named scalar field of the root object chain as
    /// "Declaring:Name" (output via Element.DebugNativeFields). Used to map API parameters to schema fields.</summary>
    internal static readonly bool CaptureAllFields = Environment.GetEnvironmentVariable("RVTIO_ALL_FIELDS") is "1" or "2";
    internal static readonly bool DeferredAllFields = Environment.GetEnvironmentVariable("RVTIO_ALL_FIELDS") == "2";
    /// <summary>Diagnostic: RVTIO_WALK_HEX="858,746" adds the raw bytes at the start of those queued classes to "walk:".</summary>
    private static readonly HashSet<int> WalkHexClasses = (Environment.GetEnvironmentVariable("RVTIO_WALK_HEX") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();

    /// <summary>True when the named scalar field feeds a NativeFieldParameters row (or diagnostic capture is on).</summary>
    [ThreadStatic] private static bool capturingNamedParam;
    [ThreadStatic] private static string? lastReadField;
    [ThreadStatic] private static List<string>? readTrail;
    // Diagnostic (RVTIO_ALL_FIELDS): the field that queued each deferred object of the root queue.
    [ThreadStatic] private static string? diagCurrentField;
    [ThreadStatic] private static List<(ushort, uint)>? diagRootQueue;
    [ThreadStatic] private static List<string>? diagQueueOrigins;
    [ThreadStatic] private static uint? diagLastOwnerReference; // diagnostic: GeomStep.m_pElem of the object just walked

    private static void DiagNoteQueued(List<(ushort, uint)> queue)
    {
        if (!CaptureAllFields || !ReferenceEquals(queue, diagRootQueue) || diagQueueOrigins == null) return;
        while (diagQueueOrigins.Count < queue.Count) diagQueueOrigins.Add(diagCurrentField ?? "");
    }

    /// <summary>Scalar schema fields the parameter-list rules (Compact/CompactParameterLists) read, as Declaring:Name keys: the dimension and its
    /// style first, then the fields of the "field:" facts of <c>CompactParameterListRules.Facts</c>. Kept in
    /// <see cref="Element.NativeListFields"/>.</summary>
    internal static readonly string[] ListFactFieldKeys =
    [
        "160:m_styleSymbolId", "160:m_dimSketchPlaneId", "160:m_showLabelGP", "160:m_flags", "174:m_dimensionStyleType",
        "174:m_linearDimType", "480:m_locationType", "896:m_cGDrivTag", "896:m_cSystemId", "896:m_dividedSurfaceId",
        "2202:m_createdStatus", "3475:m_symbolId", "3669:m_stairsAttrVersion", "3718:m_hasMonolithicSupport",
        "3742:m_constructionMethod", "3928:m_attrId", "3471:m_areaSchemeId", "3471:m_zoneSchemeId",
        // Family names of system types (CompactIdentity): the field that decides which family name a type reports.
        "1016:m_sectionViewType", "743:m_massSurfaceSubCategoryId", "105:m_pCompoundStructure", "3697:m_hasMonolithicSupport",
        "3736:m_topsideSurfaceType", "3703:m_stairsPathDirection", "3130:m_bWithFitting", "3135:m_bWithFitting", "104:m_Profile",
        "560:m_type", "1005:m_planViewType", "108:m_dbViewTypeId", "1333:m_placementHelper", "1990:m_attrId", "1860:m_attrId",
        // Family instance (FamilyInstance.getListParams): the instance, its symbol and its family.
        "37:m_assocLevelId", "37:m_dummy", "1794:m_masterSymbolId", "1794:m_useOffsetPos", "1794:m_workPlaneBased",
        "1794:m_assocSketchPlaneId", "1794:m_scheduleOnlyLevelId", "1794:m_pCurveDriver", "1794:m_pInstParams",
        "1794:m_oFamInstUserData", "1794:m_oFamInstSpec", "1794:m_pDesignPropManager", "1794:m_structType", "1794:m_refs#",
        "1794:m_refs@0", "1829:m_familyId", "1829:m_pParams", "1773:m_is2d", "1773:m_inPlace", "1773:m_isModel",
        "1773:m_isCurtainPanel", "1773:m_isWorkPlaneBased", "1773:m_eStructMaterialType", "1773:m_lightSourceDefBits",
        "1773:m_isCrvDriven", "1773:m_categoryId", "1772:m_elevationFixed", "1772:m_refTypeIds@0", "1772:m_bHasAnyDummyRefs",
        "1773:m_eSymbolicRepType", "1773:m_partType", "877:m_active", "699:present@0", "699:present@1", "699:dormant@0", "699:dormant@1",
        // the reference level of the curve driver of a family instance (no longer read by the framing orientation rule, kept for the fact dumps)
        "1708:m_refLevelId",
        // the curve of the curve driver of a family instance (class of the pointee: GLine, GArc): an arc member lists no Orientation
        "87:m_pCrv",
        // the end attachments of a framing member (FamInstStructuralAttachment, one entry per end)
        "705:m_attachmentType@0", "705:m_attachmentType@1",
        // Rule-less classes: the fields of CurveElem, RbsCurve, IndependentTag and Viewport.
        "838:m_detail", "838:m_showCenterMark", "838:m_useOffsetPos", "838:m_sketchPlaneId", "838:m_referenceType", "838:m_areaSeparation",
        "838:m_bMEPSpaceSeparation", "838:m_bMEPLoadAreaSeparation", "838:m_isAreaMeasurementScheme", "838:m_categoryId",
        "838:m_pCurveDriver", "838:m_oArcCntr", "838:m_oEllipseElemData", "838:m_famElemVisibility", "838:m_data",
        "602:m_idSystemCategory", "602:m_iEndReference", "602:m_idLevelStart", "602:m_pCurveDriver", "602:m_pConnectorManager",
        "527:m_leader", "527:m_leaderEndCondition", "527:m_tagRelOffsetCondition", "527:m_isRelPosValid", "527:m_hideTagIn3DView",
        "527:m_oTagOrientationCell", "527:m_symbolId", "527:m_taggedEntitiesCell", "527:m_multiReferenceAnnotationId",
        "1990:m_dbViewId", "1990:m_dbDrawingId", "1990:m_viewportOrientation", "1990:m_clipping", "1990:m_detailNumber",
    ];

    /// <summary>Inputs of the room of a family instance (IfcExporter.InstanceRoomsCurrent): the Y flip of the instance, the origin
    /// of its symbol, the room calculation point of the symbol and the flag of its family that turns it on, the level,
    /// vertical extent and phase of a room and the computation offset of a level. Kept in
    /// <see cref="Element.NativeRoomFields"/>.</summary>
    internal static readonly string[] RoomRuleFieldKeys =
    [
        "1794:m_flippedY", "1829:m_origin@0", "1829:m_origin@1", "1829:m_origin@2",
        "3639:m_position@0", "3639:m_position@1", "3639:m_position@2", "1773:m_hasRoomCalculationPoint",
        "3471:m_upperOffset", "3471:m_height", "3471:m_lowerOffset", "3471:m_upperLevelId", "3471:m_levelId", "3471:m_phaseId",
        "2299:m_roomComputationElevationOffset",
    ];

    /// <summary>Schema fields read by derived parameter rules (ElementBuilder.ApplyCurveDriverLevelParams).</summary>
    private static readonly string[] DerivedInputRuleKeys =
    [
        "1794:m_assocSketchPlaneId", "2604:m_datumPlaneId", "140:m_elemId",
        "897:m_hostId", "2518:m_hostId", "1829:m_familyId", "1773:m_bIsHostBased",
        "1773:m_categoryId", "1773:m_isCurtainPanel", "1773:m_isCrvDriven",
        "1794:m_instOrigin@0", "1794:m_instOrigin@1", "1794:m_instOrigin@2", "1794:m_bVertical", "1794:m_useOffsetPos",
        // VWall orientation flip: sense and offset of IfcMaterialLayerSetUsage follow it.
        "362:m_isFlipped",
        "2237:m_symbolId", "1419:m_buildingTypeId", "1709:m_rotation",
        "3795:m_SetBack", "3795:m_Length", "3795:m_StickSetBack", "3795:m_JoinCutback", "3795:m_Extension", "3795:m_endIdx",
        "369:m_angle", "369:m_origin", "369:m_belt", "369:m_fixedNum", "369:m_justify", "369:m_beltSet",
        "3789:m_connectionType@0", "3789:m_connectionType@1", "3775:m_connectionType@0", "3775:m_connectionType@1",
        // MEP: connector system data (SegmentConnectorCalculation / Position per connector, in stored order), the
        // duct taps' results and the system objects the connectors refer to.
        "3556:m_idSystem", "3556:m_nSection", "3556:m_eSystemType", "3556:m_eDirection", "3558:m_paramOnCurve",
        "3950:m_pressureDrop", "3950:m_coefficient", "3950:m_flow",
        // The same data of family instances (terminals, equipment, fixtures): hvac and piping connector calculations.
        "2381:m_idSystem", "2381:m_nSection", "2381:m_eSystemType", "2381:m_eDirection",
        "2382:m_idSystem", "2382:m_nSection", "2382:m_eSystemType", "2382:m_eDirection",
        // Connector index (port names) of family instances.
        "771:m_nIndex", "2380:m_idDownstreamCircuit",
        // Stair type lengths used by the stair flight properties.
        "3742:m_minTreadWidthOnInsideBoundary", "3718:m_nosingLength",
        // Group membership of an element (ElementGroupMembership).
        "422:m_groupId",
        // The defining (template) group instance of a group type: neither it nor its members are model elements.
        "1333:m_definingGroupInstId",
        // The room of a family instance (FamInstDesignPropertyManager).
        "1286:m_idRoom",
        "388:m_areaElemName", "388:m_areaElemDescription",
        // Design options: the primary option of a set, the name of an option or set and the set of an option (DesignOptionSet, DesignOption).
        "1080:m_mainDesignOption", "1080:m_strName", "1074:m_strName", "1074:m_DesignOptionSetId",
        // A room that is not placed in the plan (RoomElem.m_bIsLocationless) is no IfcSpace.
        "3471:m_bIsLocationless",
        "3170:m_strName", "3170:m_typeId", "3190:m_basicSystemType", "3190:m_abbreviation",
        // MEP curve geometry rules (ElementBuilder.ApplyMepCurveGeometry).
        "602:m_dOffsetStart", "602:m_dOffsetEnd", "602:m_dWidthOrDiameter", "602:m_dHeight", "1219:m_dEqDiameter",
        "2964:m_dOuterDiameter", "2964:m_dInnerDiameter", "749:m_dOuterDiameter", "749:m_dInnerDiameter",
        "1219:m_dPressureDrop", "1219:m_dLossCoefficient",
        // Pipe segment (602:m_idSegment): material, schedule/type, roughness, description.
        "602:m_idSegment", "2968:m_materialId", "2967:m_scheduleTypeId", "2968:m_roughness", "2968:m_description",
        // Type of an MEP curve occurrence (RbsCurve.getTypeId_), see ExtractSystemOccurrenceTypeId.
        "602:m_idType",
        // Coped end of a structural framing member (ElementBuilder.ApplyFramingCutLength).
        "3789:m_copingDistance",
        // Parameter definitions (UserParameterCatalog): the owned ParamDef of every ParamElem, the instance flag of a
        // family parameter and the binding records of project and shared parameters. The two ForgeTypeId strings are
        // captured as "301:m_typeId" / "612:m_specTypeId" by the field override.
        "301:m_caption", "301:m_groupElemId", "301:m_paramElemId", "301:m_readOnly", "301:m_userVisible",
        "301:m_typeId", "612:m_specTypeId", "2874:m_instanceParam", "2632:m_paramId", "2632:m_categoryId", "2632:m_elemOrSymbol",
        // Attachment of a column end (AttachInfo 699): which end, offset, justification (ElementBuilder.ApplyColumnAttachments).
        "699:m_baseOrTop", "699:m_attachOffset", "699:m_justification",
        // Parameter-list facts (Compact/CompactParameterLists): presence / class tag of owned pointers of the element base and
        // of the family instance.
        "37:m_cellList",
        // HVAC zones: the default zone flag of a ZoneElement and the zone of a space (RoomElem).
        "4215:m_bDefault", "3471:m_zoneElementId",
        .. RoomRuleFieldKeys,
        .. ListFactFieldKeys,
    ];

    /// <summary>The rule inputs above and the type pointers (<see cref="NativeTypePointers"/>).</summary>
    private static readonly HashSet<string> DerivedInputFieldKeys =
        DerivedInputRuleKeys.Concat(NativeTypePointers.Keys).ToHashSet(StringComparer.Ordinal);

    private static readonly HashSet<int> DerivedInputDeclaringClasses = DerivedInputFieldKeys
        .Select(key => int.Parse(key[..key.IndexOf(':')], System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();

    private static bool WantField(TfField f) =>
        (capturingNamedParam && f.Declaring == 715)
        || CaptureAllFields
        || (DerivedInputDeclaringClasses.Contains(f.Declaring) && DerivedInputFieldKeys.Contains(FieldKey(f)))
        || (NativeFieldParameters.DeclaringClasses.Contains(f.Declaring)
            && NativeFieldParameters.Keys.Contains(FieldKey(f)));

    /// <summary>Every "Declaring:Name" key a production parse keeps, and the bare field names kept from the root.</summary>
    internal static IEnumerable<string> CapturedKeys => DerivedInputFieldKeys.Concat(NativeFieldParameters.Keys);
    internal static HashSet<string> CapturedFieldNames => CapturedFields;

    /// <summary>Whether a "Declaring:Name" field is kept by a production (not RVTIO_ALL_FIELDS) parse.</summary>
    internal static bool IsCapturedInProduction(string key) =>
        NativeFieldParameters.Keys.Contains(key) || DerivedInputFieldKeys.Contains(key);

    private static string FieldKey(TfField f) => f.Declaring.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + f.Name;

    /// <summary>Keyed (Declaring:Name) entries of a scratch capture are the only ones merged into the real result.</summary>
    private static void MergeKeyed(NativeParams from, NativeParams into)
    {
        foreach (var (key, value) in from.Fields) if (key.Contains(':')) into.Fields[key] = value;
        foreach (var (key, value) in from.DoubleFields) if (key.Contains(':')) into.DoubleFields[key] = value;
        foreach (var (key, value) in from.StringFields) if (key.Contains(':')) into.StringFields[key] = value;
    }

    private static readonly HashSet<string> CapturedFields = new()
    {
        "m_id", "m_famId", "m_assocLevelId", "m_unplacedOwnerId", "m_ownerDBViewId",
        "m_createdPhaseId", "m_demolishedPhaseId",
        "m_masterSymbolId", "m_ownerElemId", "m_scheduleOnlyLevelId", "m_docAccess",
        // InsertableInst / FamilyInstance host topology
        "m_hostId", "m_superInstanceId",
        "m_structType", "m_structUsage",
        // VWall subclass
        "m_WallAttributesId", "m_upToLevelId", "m_wallStructuralUsage",
        "m_wallKeyRef", "m_wallCrossSection",
        // CurveElem subclass: stored category + flags
        "m_categoryId", "m_detail", "m_showCenterMark", "m_sketchPlaneId",
        // Floor / Ceiling / Roof / Level type-attribute ids and flags
        "m_floorAttributesId", "m_ceilingAttributesId", "m_roofAttributesId", "m_attrId", "m_buildingPadTypeId",
        // BaseRailing occurrence -> generated symbol and authored railing attributes.
        "m_baseRailingSymbolId", "m_stairsRailingAttrId",
        "m_analyzesAs", "m_isBuildingStory",
        // DBView
        "m_dbViewTypeId", "m_defaultTemplateId", "m_partsVisibility",
        // Dimension / DBViewPlan / SpotElevation
        "m_styleSymbolId", "m_dimSketchPlaneId", "m_paramId",
        "m_useEqualityFormula", "m_displayElevation",
        "m_underlayOrientation", "m_idUnderlayLevel", "m_idUnderlayTopLevel",
        "m_fixedSketchPlaneId", "m_bUseTrueNorth",
        // Connector / ConnectorDomain fields (ports): Connector.m_nIndex,
        // ConnectorElem.m_index, Connector.m_mode, ConnectorDomainHvac/Piping.m_eDirection,
        // ConnectorElemDomainHvac/Piping.m_eFlowDirection. Distinct names, no known collision
        // with any other captured class.
        "m_nIndex", "m_index", "m_mode", "m_eDirection", "m_eFlowDirection",
        "m_eSystemType", "m_nSection", "m_idSystem",
        // ActiveGeoLocationTrackingElement: the GeoLocation that is the active project location.
        "m_activeGeoLocationId",
    };

    /// <summary>Container fields whose COUNT is captured (into Fields as name+"#").</summary>
    private static readonly HashSet<string> CapturedCountFields = new() { "m_refPnts", "m_controlJoinsSet", "m_sideJoins", "m_midParams", "m_sideAlignments" };

    /// <summary>ElementId containers whose members describe exact instance host topology.</summary>
    private static readonly HashSet<string> CapturedElementIdContainers = new()
    {
        "m_extraHostIds", "m_explicitHostIds",
    };

    /// <summary>f64 schema fields captured into DoubleFields.</summary>
    private static readonly HashSet<string> CapturedDoubleFields = new()
    {
        "m_scale", "m_topRailHeight",
        // GeoSite (project location symbol): latitude / longitude in radians.
        "m_dLatitude", "m_dLongitude",
    };

    /// <summary>String schema fields captured into StringFields.</summary>
    private static readonly HashSet<string> CapturedStringFields = new() { "m_viewName", "m_text", "m_placeName" };

    private static readonly Lazy<Dictionary<int, TfClass>> _schemas = new(LoadSchemas);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, List<TfField>> _chainCache = new();

    public static Dictionary<int, TfClass> Schemas => _schemas.Value;

    private static Dictionary<int, TfClass> LoadSchemas()
    {
        var result = new Dictionary<int, TfClass>();
        using var s = typeof(TfBlockDeserializer).Assembly
            .GetManifestResourceStream("RvtIO.Resources.r22_schemas.json");
        if (s == null) return result;
        foreach (var c in TfSchemaReader.ReadShipped2022(s).Classes)
            result[c.Id] = new TfClass(c.Name, c.Base == 0 ? null : c.Base,
                [.. c.Fields.Select(f => new TfField(f.Name, f.Type, f.Mods, f.Ref, f.Fixed))]);
        return result;
    }

    /// <summary>Field list for a class in serialization order (root base first). Cached.</summary>
    public static List<TfField>? SerializationFields(int classId)
    {
        if (_chainCache.TryGetValue(classId, out var cached)) return cached;
        var schemas = Schemas;
        if (!schemas.ContainsKey(classId)) return null;
        var chain = new List<TfClass>();
        int? cur = classId;
        int guard = 0;
        while (cur.HasValue && schemas.TryGetValue(cur.Value, out var c) && guard++ < 32)
        {
            chain.Add(c);
            cur = c.Base;
        }
        chain.Reverse();
        var fields = new List<TfField>();
        var idByClass = new Dictionary<TfClass, int>();
        foreach (var kv in schemas) idByClass[kv.Value] = kv.Key;
        foreach (var c in chain)
        {
            int decl = idByClass.TryGetValue(c, out int d0) ? d0 : 0;
            foreach (var fl in c.Fields) fields.Add(fl with { Declaring = decl });
        }
        return _chainCache.GetOrAdd(classId, fields);
    }

    public static bool IsSubclassOf(int classId, int baseClassId)
    {
        int guard = 0;
        int? current = classId;
        while (current.HasValue && guard++ < 32)
        {
            if (current.Value == baseClassId) return true;
            current = Schemas.TryGetValue(current.Value, out var schema)
                ? schema.Base : null;
        }
        return false;
    }

    private sealed class Cursor
    {
        public byte[] D = [];
        public int Pos, End;
        public ushort RootClassId;
        public bool ExactRoomNameExtraction;
        public bool ExactFamilySymbolNameExtraction;
        public bool ExactFamilyFunctionExtraction;
        /// <summary>The ExtractParams walk of a FamilySymbol root: its int[2] face-tag records are 8 bytes each (as in
        /// the exact function walk), so a symbol with such records no longer loses every field after them.</summary>
        public bool FamilySymbolParamsWalk;
        public bool ExactWallCompleteInputs;
        // The ExtractParams deferred-tail walk: layouts (and, for GNode.m_GInfo, four inline words)
        // that the other readers' scoped contracts do not take.
        public bool TailWalk;
        public bool ExactFamilyCutInputs;
        public bool Take(int n) { if (Pos + n > End) return false; Pos += n; return true; }

        /// <summary>m_baseOrTop of the ColumnAttachment cell being read (its m_dormant follows in the same record); -1 outside.</summary>
        public int AttachBaseOrTop = -1;

        // Wall-join capture context: end index of the pair<int, CDJoinInfo> currently
        // being walked (-1 outside), so m_wallJoinType lands as "wallJoinType@<end>".
        public int JoinEnd = -1;

        // Exact wall control-join capture (ExtractWallEndJoins): when non-null, every
        // EndInfo of CurveDriver.m_controlJoinsSet lands here with its end and join type.
        public List<WallEndJoin>? ControlJoins;

        // Wall location-line capture (SWall tail): when non-null, GLine field values
        // (m_origin/m_dirVec/m_endParams) are collected here.
        public Dictionary<string, double>? WallLineCapture;

        // ExtractParams tail walk: the root deferred queue, the class of the queued object being
        // walked and the queue index of the FamInstCurveDriver's CurveDriver.m_pCrv curve.
        public List<(ushort, uint)>? RootQueue;
        public ushort TailClassId;
        public int CurveDriverCurveQueueIndex = -1;
        public int MullionReferenceQueueIndex = -1;
        public int MullionCurveQueueIndex = -1;
        /// <summary>The nodes (parameter, point, tangent) of the drive curve when it is a GHermiteSpline (flexible curves).</summary>
        public List<double[]>? HermiteNodes;

        // Curve-geometry accumulation (ExtractCurveGeometry only). GStyle from the first
        // GInfo/famElemVisibility; Curves collects each GArc/GLine/GEllipse's geometry.
        public int GStyle = int.MinValue;
        public double Ep0, Ep1, Radius, XLen, YLen;
        public List<(int Kind, double Ep0, double Ep1, double R, double XL, double YL)>? Curves;

        // FamilySymbol / InsertableObj exact cut-loop extraction.
        public List<int>? FamilyCutLoopQueueIndices;
        public List<double[]>? FamilyCutDirections;
        public List<int>? FamilyCutIndices;
        public Dictionary<string, double[]>? FamilyLineCapture;

        // Exact host cut topology. GeomStepList owns phase-specific step arrays;
        // every GeomStep stores the element that authored it in m_pElem.
        public HashSet<int>? CutOutGeomStepQueueIndices;
        public Dictionary<int, uint>? GeomStepOwnerElementIds;
        public Dictionary<int, int>? GeomStepCuttingElementIds;
        public Dictionary<int, int>? GeomStepPairOtherIds;
        public int CurrentGeomStepQueueIndex = -1;
        /// <summary>The current VerticalExtensionOfLayersGStep persists its two m_otherId members as counted containers.</summary>
        public bool CountedOtherIds;
        /// <summary>A JoinEnd step (2256) reads its history tables with the counted layout, like the other steps.</summary>
        public bool CountedHistory;
        /// <summary>A JoinEnd step (2256) persists m_realJoined and m_exposedFaceMap although its reverse history is empty.</summary>
        public bool JoinEndFullTail;
        /// <summary>The block is read by the layout search (a block the exact reading could not decode): the step layouts that
        /// only that search knows apply.</summary>
        public bool LayoutSearch;
        public int CurrentDeferredQueueIndex = -1;
        public ushort CurrentDeferredClassId;
        public bool CurrentJoinEndHasReverseHistory;
        public bool SawJoinEndReverseHistory;
        public bool CurrentTrackedReverseHistoryPresent;
        public int CurrentTrackedReverseHistoryEnd = -1;
        public int CurrentTrackedCurveRecordSize = 20;
        public List<string>? CutTopologyTrace;

        // BasePoint capture: m_transformedPosition is the point in shared coordinates;
        // the deferred GPoint.m_coord is the same marker in project-internal coordinates.
        public double[]? BasePointShared;
        public double[]? BasePointInternal;

        // GeoLocation (project location) capture: the deferred InstanceInfo owns the Trf
        // (row-major 3x3 + origin) of the location and the id of its GeoSite symbol.
        public bool GeoLocationExtraction;
        public double[]? GeoRotation;
        public double[]? GeoOrigin;
        public int? GeoSymbolId;

        // Grid DatumPlane capture. The root m_pSurface queue index identifies the exact
        // authored datum Plane even when its Face queues another Plane later.
        public bool GridDatumExtraction;
        public int GridSurfaceQueueIndex = -1;
        public bool CaptureGridPlane;
        public double[]? GridEnvelope;
        public double[]? GridOrigin;
        public double[]? GridXVector;
        public double[]? GridYVector;

    }

    private sealed class DecodeFail : Exception { }
    private static readonly DecodeFail Fail = new();

    // Param-store deferred tags -> record class ids (record schemas drive field order).
    private static readonly Dictionary<int, (char Kind, int RecordClass)> ParamSetTags = new()
    {
        [0xB47] = ('D', 0xB43), [0xB49] = ('I', 0xB45),
        [0xB46] = ('A', 0xB42), [0xB48] = ('E', 0xB44),
    };

    /// <summary>
    /// Extract the element's native parameter stores from a block. blockStart points at
    /// the [eid] field of the block header. Returns null when the walk fails before
    /// reaching the deferred region or the class has no schema.
    /// </summary>
    public static NativeParams? ExtractParams(byte[] d, int blockStart, uint dataSize, ushort classId)
    {
        var fields = SerializationFields(classId);
        if (fields == null) return null;
        int payloadStart = blockStart + 14;
        int payloadEnd = blockStart + 16 + (int)dataSize;
        if (payloadEnd > d.Length || payloadEnd - payloadStart < 6) return null;
        // Footer self-check: last 4 payload bytes == dataSize.
        if (BitConverter.ToUInt32(d, payloadEnd - 4) != dataSize) return null;

        var cur = new Cursor
        {
            D = d,
            Pos = payloadStart,
            End = payloadEnd - 4,
            RootClassId = classId,
            TailWalk = true,
            CutTopologyTrace = CaptureAllFields ? new List<string>() : null, // diagnostic: candidate selection of the history tables
            FamilySymbolParamsWalk = classId == 1829,
        };
        var queue = new List<(ushort Cls, uint Id)>();
        cur.RootQueue = queue;
        var res = new NativeParams();
        if (CaptureAllFields)
        {
            diagRootQueue = queue;
            diagQueueOrigins = new List<string>();
        }
        try
        {
            foreach (var f in fields) ReadField(cur, f, queue, 0, res);
        }
        catch (DecodeFail)
        {
            if (CaptureAllFields && Environment.GetEnvironmentVariable("RVTIO_ROOTFAIL") is { } watched
                && watched.Split(',').Contains(BitConverter.ToUInt32(d, blockStart).ToString(System.Globalization.CultureInfo.InvariantCulture)))
                DiagnosticSink.WriteLine($"rootfail eid={BitConverter.ToUInt32(d, blockStart)} cls={classId} at {lastReadField} trail={string.Join(">", readTrail ?? new())}");
            return null;
        }

        try
        {
            int qi = 0;
            for (; qi < queue.Count; qi++)
            {
                var (cls, _) = queue[qi];
                // A PropertySetElement (an asset of a material) keeps its stores in m_oParamSet: the ParamSet object
                // has no payload of its own, it queues the four stores in the usual order.
                if (cls == 2880 && classId == 3073 && qi == 0)
                {
                    foreach (var paramSetField in SerializationFields(2880) ?? throw Fail)
                        ReadField(cur, paramSetField, queue, 1, new NativeParams());
                    continue;
                }
                if (!ParamSetTags.TryGetValue(cls, out var tag)) break; // geometry tail begins
                uint n = ReadU32(cur);
                if (n > 100_000) return res;
                var recFields = SerializationFields(tag.RecordClass);
                if (recFields == null) return res;
                for (uint i = 0; i < n; i++)
                {
                    int paramId = 0; double dv = 0; int iv = 0; string sv = "";
                    foreach (var rf in recFields)
                    {
                        switch (rf.Name)
                        {
                            case "m_paramId": paramId = ReadI32(cur); break;
                            case "m_value" when tag.Kind == 'D': dv = ReadF64(cur); break;
                            case "m_value" when tag.Kind == 'A': sv = ReadString(cur); break;
                            case "m_value": iv = ReadI32(cur); break;
                            default: ReadField(cur, rf, new List<(ushort, uint)>(), 0, null); break;
                        }
                    }
                    switch (tag.Kind)
                    {
                        case 'D': res.Doubles.Add((paramId, dv)); break;
                        case 'I': res.Ints.Add((paramId, iv)); break;
                        case 'A': res.Strings.Add((paramId, sv)); break;
                        case 'E': res.ElementIds.Add((paramId, iv)); break;
                    }
                }
            }

            res.RootParameterStoresComplete = !queue.Skip(qi)
                .Any(entry => ParamSetTags.ContainsKey(entry.Cls));

            // Walk the remaining deferred objects in queue order for NativeFieldParameters keys
            // (everything with RVTIO_ALL_FIELDS=2). Name-based captures are kept out via a scratch result.
            if (classId != 3524 && (DeferredAllFields || !CaptureAllFields))
            {
                var tail = new NativeParams();
                var walked = CaptureAllFields ? new System.Text.StringBuilder() : null;
                string lastField = "";
                var refOccurrences = new Dictionary<ushort, int>();
                bool tailDone = false;
                try
                {
                    for (; qi < queue.Count; qi++)
                    {
                        var qf = SerializationFields(queue[qi].Cls);
                        string origin = walked != null && diagQueueOrigins != null && qi < diagQueueOrigins.Count ? diagQueueOrigins[qi] : "";
                        walked?.Append(queue[qi].Cls).Append("<").Append(origin).Append("@").Append(cur.Pos).Append(qf == null ? "!," : ",");
                        if (walked != null && WalkHexClasses.Contains(queue[qi].Cls)) walked.Append("[").Append(Convert.ToHexString(cur.D, cur.Pos, Math.Min(800, cur.End - cur.Pos))).Append("]");
                        if (qf == null) break;
                        if (queue[qi].Cls == 715)
                        {
                            var item = new NativeParams();
                            capturingNamedParam = true;
                            try { foreach (var sf in qf) ReadField(cur, sf, queue, 1, item); }
                            finally { capturingNamedParam = false; }
                            tail.NamedParams.Add(NamedParamOf(item));
                            continue;
                        }
                        cur.TailClassId = queue[qi].Cls;
                        // Geometry steps carry persisted history tables: read them with the tracked layouts of the
                        // host cut-topology walk (count / terminated / prefixed, validated against the step tail).
                        cur.CurrentDeferredClassId = queue[qi].Cls;
                        cur.CurrentDeferredQueueIndex = qi;
                        cur.CurrentGeomStepQueueIndex = IsSubclassOf(queue[qi].Cls, 96) ? qi : -1;
                        // The FamInstCurveDriver drive curve, when it is a GLine: origin, direction, end params.
                        bool mullionLine = qi == cur.MullionCurveQueueIndex && queue[qi].Cls == 1736;
                        bool driveLine = (qi == cur.CurveDriverCurveQueueIndex || mullionLine) && queue[qi].Cls == 1736;
                        // the class of that curve is a list fact (CompactFamilyInstanceFacts, getMemberIndexToTagMap); the root queue only,
                        // as for the capture of the line itself
                        if (qi == cur.CurveDriverCurveQueueIndex && ReferenceEquals(queue, cur.RootQueue)) tail.Fields["87:m_pCrv"] = (int)queue[qi].Cls;
                        if (driveLine)
                        {
                            cur.FamilyLineCapture = new Dictionary<string, double[]>();
                            cur.Ep0 = cur.Ep1 = double.NaN;
                        }
                        diagLastOwnerReference = null;
                        int attachedTopBefore = AttachTopBefore(tail, queue[qi].Cls);
                        // The drive curve of a flexible pipe or duct is a GHermiteSpline: keep its nodes.
                        bool driveSpline = qi == cur.CurveDriverCurveQueueIndex && queue[qi].Cls == 2020;
                        if (driveSpline) cur.HermiteNodes = new List<double[]>();
                        int cellsBefore = queue[qi].Cls == 653 ? queue.Count : -1;
                        foreach (var sf in qf) { lastField = FieldKey(sf) + "/" + sf.Type + "/" + sf.Mods + "/" + sf.Ref; ReadField(cur, sf, queue, 1, tail); }
                        if (cellsBefore >= 0 && ReferenceEquals(queue, cur.RootQueue))
                            tail.CellClasses = queue.Skip(cellsBefore).Select(entry => (int)entry.Item1).ToList();
                        KeepAnyAttachTop(tail, queue[qi].Cls, attachedTopBefore);
                        if (driveSpline)
                        {
                            for (int node = 0; node < cur.HermiteNodes!.Count; node++)
                                for (int k = 0; k < 7; k++)
                                    tail.DoubleFields[$"87:m_pCrv.node{node}@{k}"] = cur.HermiteNodes[node][k];
                            tail.DoubleFields["87:m_pCrv.nodes"] = cur.HermiteNodes.Count;
                            cur.HermiteNodes = null;
                        }
                        if (walked != null && diagLastOwnerReference is { } ownerReference)
                            walked.Append("{pe=").Append(ownerReference)
                                .Append(",id=").Append(tail.Fields.GetValueOrDefault("97:m_id"))
                                .Append(",v=").Append(tail.Fields.GetValueOrDefault("96:m_version"))
                                .Append(",f=").Append(tail.Fields.GetValueOrDefault("96:m_flags")).Append("},");
                        // Repeated reference objects in stored order: FamilyInstance.m_refs FamilyLevelRef (column
                        // base, then top) and FamilySymbol.m_refs StructSetbackSymFamRef (framing start, then end).
                        if (ReferenceEquals(queue, cur.RootQueue)) CopyOccurrence(tail, qf, queue[qi].Cls, refOccurrences);
                        if (driveLine)
                        {
                            string curveKey = mullionLine ? "2520:m_pCurve" : "87:m_pCrv";
                            if (cur.FamilyLineCapture!.TryGetValue("m_origin", out var lineOrigin)
                                && cur.FamilyLineCapture.TryGetValue("m_dirVec", out var lineDirection))
                            {
                                for (int i = 0; i < 3; i++)
                                {
                                    tail.DoubleFields[curveKey + ".origin@" + i] = lineOrigin[i];
                                    tail.DoubleFields[curveKey + ".dirVec@" + i] = lineDirection[i];
                                }
                                tail.DoubleFields[curveKey + ".endParams@0"] = cur.Ep0;
                                tail.DoubleFields[curveKey + ".endParams@1"] = cur.Ep1;
                            }
                            cur.FamilyLineCapture = null;
                        }
                    }
                    tailDone = qi >= queue.Count;
                }
                catch (DecodeFail)
                {
                    walked?.Append("FAIL@").Append(lastField).Append(" inner=").Append(lastReadField).Append(" trail=").Append(string.Join(">", readTrail ?? new()));
                    if (walked != null && cur.CutTopologyTrace is { Count: > 0 } trace) walked.Append(" cand=").Append(string.Join("|", trace.TakeLast(6)));
                }
                if (walked != null) tail.StringFields["walk:"] = walked.ToString() + " queue=" + queue.Count + " rest=" + (cur.End - cur.Pos);
                res.TailWalkComplete = tailDone;
                MergeKeyed(tail, res);
                res.NamedParams.AddRange(tail.NamedParams);
                if (tail.CellClasses != null) res.CellClasses = tail.CellClasses;
            }

            // SWall (3524): keep walking the deferred tail; the wall's VWallDriver
            // (0xFC8, base CurveDriver) is deferred after GeomStepList/GeomTable/CellList
            // and carries the wall-join records: m_controlJoinsSet queues
            // pair<int endIdx, CDJoinInfo{.., m_wallJoinType}> objects, and VWallDriver
            // itself has m_noJoinAtEnd[2]. A DecodeFail keeps whatever was captured.
            if (classId == 3524)
            {
                bool sawDriver = false, gotLine = false;
                var wallOccurrences = new Dictionary<ushort, int>();
                var walkedWall = CaptureAllFields ? new System.Text.StringBuilder() : null;
                // The wall's own fields were read from its root; the deferred objects below carry fields of the same
                // names (GeomGenerator.m_id of every step, ...) that must not replace them.
                var rootNamedFields = res.Fields.Where(pair => !pair.Key.Contains(':')).ToList();
                var rootNamedDoubles = res.DoubleFields.Where(pair => !pair.Key.Contains(':')).ToList();
                var rootNamedStrings = res.StringFields.Where(pair => !pair.Key.Contains(':')).ToList();
                try
                {
                for (; qi < queue.Count; qi++)
                {
                    var (cls, _) = queue[qi];
                    if (walkedWall != null)
                    {
                        string origin = diagQueueOrigins != null && qi < diagQueueOrigins.Count ? diagQueueOrigins[qi] : "";
                        walkedWall.Append(cls).Append('<').Append(origin).Append('@').Append(cur.Pos).Append(',');
                    }
                    if (cls == 4293) // pair<int, CDJoinInfo>
                    {
                        cur.JoinEnd = ReadI32(cur);
                        var jf = SerializationFields(89) ?? throw Fail;
                        foreach (var sf in jf) ReadField(cur, sf, queue, 1, res);
                        cur.JoinEnd = -1;
                        continue;
                    }
                    if (cls == 4040) sawDriver = true;
                    // First GLine after VWallDriver = the driver's m_pCrv = the wall's
                    // LOCATION LINE (world coords): capture origin/dir/endParams.
                    bool captureLine = sawDriver && !gotLine && cls == 0x6C8;
                    if (captureLine) cur.WallLineCapture = new Dictionary<string, double>();
                    var qf = SerializationFields(cls);
                    if (qf == null) break;
                    // Same geometry-step context as the generic tail walk: the persisted history tables of steps use
                    // the tracked layouts.
                    cur.TailClassId = cls;
                    cur.CurrentDeferredClassId = cls;
                    cur.CurrentDeferredQueueIndex = qi;
                    cur.CurrentGeomStepQueueIndex = IsSubclassOf(cls, 96) ? qi : -1;
                    int attachedTopBefore = AttachTopBefore(res, cls);
                    int wallCellsBefore = cls == 653 ? queue.Count : -1;
                    foreach (var sf in qf) ReadField(cur, sf, queue, 1, res);
                    if (wallCellsBefore >= 0 && ReferenceEquals(queue, cur.RootQueue))
                        res.CellClasses = queue.Skip(wallCellsBefore).Select(entry => (int)entry.Item1).ToList();
                    KeepAnyAttachTop(res, cls, attachedTopBefore);
                    CopyOccurrence(res, qf, cls, wallOccurrences);
                    if (captureLine)
                    {
                        if (cur.WallLineCapture!.Count >= 8)
                        {
                            gotLine = true;
                            foreach (var kv in cur.WallLineCapture)
                                res.DoubleFields["wallLn_" + kv.Key] = kv.Value;
                        }
                        cur.WallLineCapture = null;
                    }
                }
                }
                catch (DecodeFail)
                {
                    walkedWall?.Append("FAIL@").Append(lastReadField).Append(" trail=").Append(string.Join(">", readTrail ?? new()));
                    throw;
                }
                finally
                {
                    foreach (var (key, value) in rootNamedFields) res.Fields[key] = value;
                    foreach (var (key, value) in rootNamedDoubles) res.DoubleFields[key] = value;
                    foreach (var (key, value) in rootNamedStrings) res.StringFields[key] = value;
                    if (walkedWall != null) res.StringFields["walk:"] = walkedWall + " queue=" + queue.Count + " rest=" + (cur.End - cur.Pos);
                }
            }
        }
        catch (DecodeFail) { /* keep what we have */ }
        return res;
    }

    /// <summary>The attachment steps of a column or wall: CutPairGStep (977) is written once per attachment (base and top)
    /// and tells the end by <c>m_isTop</c>; JoinToRoofGStep (2262) does the same by <c>m_baseOrTop</c> (1 = top).</summary>
    private static string? AttachTopKey(ushort cls) => cls switch { 977 => "977:m_isTop", 2262 => "2262:m_baseOrTop", _ => null };

    private static int AttachTopBefore(NativeParams target, ushort cls)
        => AttachTopKey(cls) is { } key ? target.Fields.GetValueOrDefault(key) : 0;

    /// <summary>"Top is Attached" is the field of any such step, not of the last one read.</summary>
    internal static void KeepAnyAttachTop(NativeParams target, ushort cls, int before)
    {
        if (AttachTopKey(cls) is { } key && target.Fields.TryGetValue(key, out int now))
            target.Fields[key] = Math.Max(before, now);
    }

    /// <summary>Key of the running count of connector calculations in the occurrence table of one walk.</summary>
    private const ushort CalculationCounterKey = ushort.MaxValue;

    /// <summary>Repeated deferred objects keep their stored order under "Class#k:field" keys: FamilyInstance.m_refs
    /// FamilyLevelRef (column base, then top), FamilySymbol.m_refs StructSetbackSymFamRef (framing start, then end)
    /// and the curtain wall SpacingRuleInst pair (horizontal, then vertical grid). The connector calculations (2380,
    /// 2381, 2382, 3556) are also numbered across the classes, as "calc#n:class" and "calc#n:occurrence".</summary>
    private static void CopyOccurrence(NativeParams target, List<TfField> fields, ushort cls, Dictionary<ushort, int> occurrences)
    {
        if (cls is not (1801 or 3795 or 369 or 3556 or 3558 or 3950 or 2381 or 2382 or 2380 or 771)) return;
        int occurrence = occurrences.TryGetValue(cls, out int seen) ? seen : 0;
        occurrences[cls] = occurrence + 1;
        if (cls is 2380 or 2381 or 2382 or 3556)
        {
            // Each connector owns exactly one calculation among its modifiers, and the modifiers of the connectors are
            // queued in connector order: the n-th calculation of any domain belongs to the n-th connector.
            int calculation = occurrences.TryGetValue(CalculationCounterKey, out int calculations) ? calculations : 0;
            occurrences[CalculationCounterKey] = calculation + 1;
            string order = "calc#" + calculation.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":";
            target.Fields[order + "class"] = cls;
            target.Fields[order + "occurrence"] = occurrence;
        }
        string prefix = cls.ToString(System.Globalization.CultureInfo.InvariantCulture) + "#"
            + occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":";
        foreach (var field in fields)
        {
            string key = FieldKey(field);
            if (target.DoubleFields.TryGetValue(key, out double doubleValue)) target.DoubleFields[prefix + field.Name] = doubleValue;
            else if (target.Fields.TryGetValue(key, out int intValue)) target.Fields[prefix + field.Name] = intValue;
        }
        if (cls == 771 && target.Fields.Remove("771:m_arrRefs#", out int refCount))
        {
            target.Fields[prefix + "m_arrRefs#"] = refCount;
            for (int i = 0; i < refCount; i++)
                foreach (string part in new[] { "id", "index", "type" })
                    if (target.Fields.Remove($"771:m_arrRefs@{i}:{part}", out int refValue))
                        target.Fields[$"{prefix}m_arrRefs@{i}:{part}"] = refValue;
        }
    }

    /// <summary>Reads ROOM_NAME only from an owned RoomElem root and its complete four
    /// parameter stores. Geometry after the stores is outside this scope. The dedicated
    /// cursor keeps the RoomBoundingItem correction out of the legacy partial decoder.</summary>
    internal static bool TryExtractExactRoomName(
        byte[] data, int start, uint size, ushort classId, out string? name)
        => TryExtractExactRoomMetadata(data, start, size, classId, out name, out _);

    internal static bool TryExtractExactRoomMetadata(
        byte[] data, int start, uint size, ushort classId, out string? name, out int? phaseId)
        => TryExtractExactRoomMetadata(data, start, size, classId, out name, out phaseId, out _);

    internal static bool TryExtractExactRoomMetadata(
        byte[] data, int start, uint size, ushort classId, out string? name, out int? phaseId, out bool? isLocationless)
        => TryExtractExactRoomMetadata(data, start, size, classId, out name, out phaseId, out isLocationless, out _);

    /// <param name="cachedCircuitId">RoomElem.m_cachedCircuitId: the circuit of the level room plan the room occupies.</param>
    internal static bool TryExtractExactRoomMetadata(
        byte[] data, int start, uint size, ushort classId, out string? name, out int? phaseId, out bool? isLocationless,
        out int? cachedCircuitId)
    {
        name = null;
        phaseId = null;
        isLocationless = null;
        cachedCircuitId = null;
        long end = (long)start + 16 + size;
        if (classId != 3471 || start < 0 || size < 4 || end > data.Length
            || start > data.Length - 16
            || BitConverter.ToUInt32(data, start + 8) != size
            || BitConverter.ToUInt16(data, start + 12) != classId
            || BitConverter.ToUInt32(data, (int)end - 4) != size)
            return false;
        var fields = SerializationFields(classId);
        if (fields == null || fields.Count < 4) return false;
        string[] storeNames = ["m_pParamValueSetDouble", "m_pParamValueSetInt",
            "m_pParamValueSetAString", "m_pParamValueSetElementId"];
        ushort[] storeTags = [2887, 2889, 2886, 2888];
        var cursor = new Cursor
        {
            D = data, Pos = start + 14, End = (int)end - 4,
            RootClassId = classId, ExactRoomNameExtraction = true,
        };
        var queue = new List<(ushort Cls, uint Id)>();
        var stores = new List<int>();
        var captured = new NativeParams();
        int? roomPhase = null;
        int? roomCircuit = null;
        try
        {
            for (int i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                int before = queue.Count;
                if (i < 4 && (field.Declaring != 37 || field.Name != storeNames[i]
                    || field.Type != 14 || field.Mods != 1 || field.Ref != null))
                    return false;
                if (field is { Declaring: 3471, Name: "m_phaseId", Type: 14, Mods: 0, Ref: 20 })
                    roomPhase = ReadI32(cursor);
                // CircuitId is one inline int (the recovered reference of its m_id is not its layout).
                else if (field is { Declaring: 3471, Name: "m_cachedCircuitId", Type: 14, Mods: 0, Ref: 182 }
                    && SchemaIs(182, "CircuitId", false, ("m_id", 4, 0, AnyReference)))
                    roomCircuit = ReadI32(cursor);
                else
                    ReadField(cursor, field, queue, 0, captured);
                if (i < 4 && queue.Count != before)
                {
                    if (queue.Count != before + 1 || queue[^1].Cls != storeTags[i])
                        return false;
                    stores.Add(i);
                }
            }
            if (!captured.Fields.TryGetValue("m_id", out int owner)
                || owner <= 0 || (uint)owner != BitConverter.ToUInt32(data, start))
                return false;
            // The circuit is a field of the walked root and validated by the owner alone; a room without a stored name keeps it.
            cachedCircuitId = roomCircuit;
            int nameCount = 0;
            string? candidate = null;
            foreach (int store in stores)
            {
                uint count = ReadU32(cursor);
                if (count > 100_000) return false;
                for (uint i = 0; i < count; i++)
                {
                    if (store == 0)
                    {
                        ReadF64(cursor);
                        if (ReadI32(cursor) == -1006900) return false;
                    }
                    else
                    {
                        int parameterId = ReadI32(cursor);
                        if (store == 2)
                        {
                            string value = ReadString(cursor);
                            if (parameterId == -1006900) { nameCount++; candidate = value; }
                        }
                        else
                        {
                            if (parameterId == -1006900) return false;
                            ReadI32(cursor);
                        }
                    }
                }
            }
            if (nameCount != 1 || string.IsNullOrEmpty(candidate)) return false;
            name = candidate;
            phaseId = roomPhase;
            if (captured.Fields.TryGetValue("3471:m_bIsLocationless", out int locationless))
                isLocationless = locationless != 0;
            return true;
        }
        catch (DecodeFail) { return false; }
    }

    /// <summary>Read the authored phase order, never a sorted or voted list of IDs.
    /// The complete AllProjectPhases block, including its deferred tail, must decode.</summary>
    internal static bool TryExtractExactProjectPhaseOrder(
        byte[] data, int start, uint size, ushort classId, out int[]? phaseIds)
    {
        phaseIds = null;
        long end = (long)start + 16 + size;
        if (classId != 187 || start < 0 || size < 4 || end > data.Length
            || start > data.Length - 16
            || BitConverter.ToUInt32(data, start + 8) != size
            || BitConverter.ToUInt16(data, start + 12) != classId
            || BitConverter.ToUInt32(data, (int)end - 4) != size)
            return false;
        var fields = SerializationFields(classId);
        if (fields == null) return false;
        var cursor = new Cursor { D = data, Pos = start + 14, End = (int)end - 4, RootClassId = classId };
        var queue = new List<(ushort Cls, uint Id)>();
        var captured = new NativeParams();
        int[]? candidate = null;
        try
        {
            foreach (var field in fields)
            {
                if (field is { Declaring: 187, Name: "m_phaseIds", Type: 14, Mods: 80, Ref: 20 })
                {
                    uint count = ReadU32(cursor);
                    if (count == 0 || count > 100_000 || count > (cursor.End - cursor.Pos) / 4)
                        return false;
                    candidate = new int[count];
                    for (int i = 0; i < candidate.Length; i++) candidate[i] = ReadI32(cursor);
                    // Id 0 is a real phase id in older projects (phases 0 and 2).
                    if (candidate.Any(id => id < 0) || candidate.Distinct().Count() != candidate.Length)
                        return false;
                }
                else ReadField(cursor, field, queue, 0, captured);
            }
            if (candidate == null || !captured.Fields.TryGetValue("m_id", out int owner)
                || owner <= 0 || (uint)owner != BitConverter.ToUInt32(data, start))
                return false;
            for (int i = 0; i < queue.Count; i++)
            {
                if (queue.Count > 100_000) return false;
                ReadDeferredObject(cursor, queue[i].Cls, queue);
            }
            if (cursor.Pos != cursor.End) return false;
            phaseIds = candidate;
            return true;
        }
        catch (DecodeFail) { return false; }
    }

    /// <summary>Decodes only an exact class-3681 StairsElement owner block. The complete
    /// schema root must walk successfully, the four named source fields must retain their
    /// schema root must walk successfully, the four named source fields must retain their
    /// r22 declarations, and the serialized Element.m_id must agree with the block
    /// header. Deferred geometry is intentionally outside this occurrence-value scope.</summary>
    internal static bool TryExtractExactStairElementRoot(
        byte[] data, int blockStart, uint dataSize, ushort classId,
        out NativeParams root)
    {
        const ushort stairElementClassId = 3681;
        root = null!;
        if (classId != stairElementClassId
            || blockStart < 0
            || blockStart > data.Length - 16
            || dataSize > int.MaxValue
            || (long)blockStart + 16 + dataSize > data.Length
            || BitConverter.ToUInt32(data, blockStart + 8) != dataSize
            || BitConverter.ToUInt32(data, blockStart + 12) != stairElementClassId
            || !Schemas.TryGetValue(stairElementClassId, out TfClass? schema)
            || schema.Name != "StairsElement"
            || schema.Base != 37)
            return false;

        List<TfField>? fields = SerializationFields(stairElementClassId);
        if (fields == null
            || !HasExactSchemaField(fields, 37, "m_id", 0x0E, 0, 20)
            || !HasExactSchemaField(
                fields, stairElementClassId, "m_typeId", 0x0E, 0, 20)
            || !HasExactSchemaField(
                fields, stairElementClassId,
                "m_actualNumberOfRisers", 0x04, 0, null)
            || !HasExactSchemaField(
                fields, stairElementClassId,
                "m_actualRiserHeight", 0x07, 0, null)
            || !HasExactSchemaField(
                fields, stairElementClassId,
                "m_actualTreadDepth", 0x07, 0, null))
            return false;

        NativeParams? decoded = ExtractParams(
            data, blockStart, dataSize, stairElementClassId);
        int headerOwnerId = unchecked((int)BitConverter.ToUInt32(data, blockStart));
        if (decoded == null
            || !decoded.Fields.TryGetValue("m_id", out int serializedOwnerId)
            || serializedOwnerId != headerOwnerId
            || !decoded.Fields.ContainsKey("m_typeId")
            || !decoded.Fields.ContainsKey("m_actualNumberOfRisers")
            || !decoded.DoubleFields.ContainsKey("m_actualRiserHeight")
            || !decoded.DoubleFields.ContainsKey("m_actualTreadDepth"))
            return false;

        root = decoded;
        return true;
    }

    /// <summary>Validates the exact class-3742 StairsType root reached by
    /// StairsElement.m_typeId. Only class, framing, a complete schema-root walk and owner
    /// agreement are carried; no type dimensions are consumed by this slice.</summary>
    internal static bool TryExtractExactStairsTypeRoot(
        byte[] data, int blockStart, uint dataSize, ushort classId,
        out NativeParams root)
    {
        const ushort stairsTypeClassId = 3742;
        root = null!;
        if (classId != stairsTypeClassId
            || blockStart < 0
            || blockStart > data.Length - 16
            || dataSize > int.MaxValue
            || (long)blockStart + 16 + dataSize > data.Length
            || BitConverter.ToUInt32(data, blockStart + 8) != dataSize
            || BitConverter.ToUInt32(data, blockStart + 12) != stairsTypeClassId
            || !Schemas.TryGetValue(stairsTypeClassId, out TfClass? schema)
            || schema.Name != "StairsType"
            || schema.Base != 106)
            return false;

        NativeParams? decoded = ExtractParams(
            data, blockStart, dataSize, stairsTypeClassId);
        int headerOwnerId = unchecked((int)BitConverter.ToUInt32(data, blockStart));
        if (decoded == null
            || !decoded.Fields.TryGetValue("m_id", out int serializedOwnerId)
            || serializedOwnerId != headerOwnerId)
            return false;

        root = decoded;
        return true;
    }

    /// <summary>Own semantic Element class/identity prefix only, not a complete subtype or
    /// geometry graph. Header, size/footer, project document handle and named Element.m_id
    /// must agree. GElement frames cannot supply an Element's semantic class.</summary>
    internal static bool TryExtractOwnElementClass(
        byte[] data, int start, uint size, ushort classId, out int ownerId)
    {
        ownerId = -1;
        long end = (long)start + 16 + size;
        if (start < 0 || start > data.Length - 16 || size < 4 || size > int.MaxValue
            || end > data.Length || !IsSubclassOf(classId, 37)
            || BitConverter.ToUInt32(data, start + 8) != size
            || BitConverter.ToUInt16(data, start + 12) != classId
            || BitConverter.ToUInt32(data, (int)end - 4) != size)
            return false;
        uint headerOwner = BitConverter.ToUInt32(data, start);
        var fields = SerializationFields(37);
        if ((headerOwner == 0 && classId != 3065) || headerOwner > int.MaxValue || fields == null
            || !HasExactSchemaField(fields, 37, "m_id", 14, 0, 20)
            || !HasExactSchemaField(fields, 37, "m_docAccess", 14, 0, null))
            return false;
        var cursor = new Cursor { D = data, Pos = start + 14, End = (int)end - 4, RootClassId = classId };
        var queue = new List<(ushort, uint)>();
        var root = new NativeParams();
        try
        {
            foreach (var field in fields)
            {
                if (field.Name == "m_docAccess")
                {
                    // Only the explicit project document handle, not an anonymous or foreign document.
                    if (ReadU32(cursor) != 1) return false;
                }
                else ReadField(cursor, field, queue, 0, root);
                if (queue.Count > 100_000) return false;
            }
            if (!root.Fields.TryGetValue("m_id", out int namedOwner) || namedOwner != headerOwner)
                return false;
            ownerId = namedOwner;
            return true;
        }
        catch (DecodeFail) { return false; }
    }

    internal sealed record StairComponentIds(int OwnerId, int[] RunsAndLandings,
        int[] Supports, int[] RegisteredRailings, int MultistoryUpToLevelId,
        int[]? MultistoryCopyLevelIds = null);

    /// <summary>Own BaseRailing prefix through its named stair host. Later flags,
    /// parameter stores and geometry are outside this host-field scope.</summary>
    internal static bool TryExtractRailingStairHost(byte[] data, int start, uint size,
        ushort classId, out int stairId)
    {
        stairId = -1;
        if (classId != 485 || !TryExtractOwnElementClass(data, start, size, classId, out _)) return false;
        var fields = SerializationFields(485);
        if (fields == null || !HasExactSchemaField(fields, 485, "m_stairsId", 14, 0, 20)) return false;
        var cursor = new Cursor { D = data, Pos = start + 14, End = checked(start + 12 + (int)size), RootClassId = classId };
        // A railing of the legacy format (m_version 1) keeps its stair in m_stairsComponentId and has no m_stairsId (0).
        bool hasComponentField = HasExactSchemaField(fields, 485, "m_stairsComponentId", 14, 0, 20);
        bool idRead = false;
        var queue = new List<(ushort, uint)>();
        try
        {
            foreach (var field in fields)
            {
                if (field.Declaring == 485 && field.Name == "m_stairsId")
                {
                    stairId = ReadI32(cursor);
                    if (stairId > 0 || !hasComponentField) return true;
                    idRead = true;
                    continue;
                }
                if (idRead && field.Declaring == 485 && field.Name == "m_stairsComponentId")
                {
                    int component = ReadI32(cursor);
                    if (component > 0) stairId = component;
                    return true;
                }
                ReadField(cursor, field, queue, 0, null);
                if (queue.Count > 100_000) return false;
            }
        }
        // a prefix that ends after m_stairsId keeps the id
        catch (DecodeFail) { return idRead; }
        return false;
    }

    /// <summary>Own StairsElement.m_cellList -> CellList.m_cells -> one
    /// StairsElementGroupHelper. Reads the complete root and FIFO prefix through that
    /// helper, not later unrelated geometry/cells. Native list order is retained but
    /// is NOT claimed to be GetStairsRuns/GetStairsLandings API enumeration order.
    /// When the same cell list owns a MultistoryStairsDataCell, the walk continues to it
    /// for the levels the stair is repeated on, in stored order; a failure there keeps
    /// the helper result without levels.</summary>
    internal static StairComponentIds? ExtractStairComponentIds(
        byte[] data, int start, uint size, ushort classId)
    {
        long end = (long)start + 16 + size;
        if (classId != 3681 || start < 0 || size < 4 || start > data.Length - 16
            || end > data.Length || BitConverter.ToUInt32(data, start + 8) != size
            || BitConverter.ToUInt16(data, start + 12) != classId
            || BitConverter.ToUInt32(data, (int)end - 4) != size
            || !Schemas.TryGetValue(3681, out var stair) || stair.Name != "StairsElement" || stair.Base != 37
            || !Schemas.TryGetValue(653, out var cells) || cells.Name != "CellList" || cells.Base != null
            || !Schemas.TryGetValue(3682, out var helper) || helper.Name != "StairsElementGroupHelper" || helper.Base != 348
            || !Schemas.TryGetValue(348, out var group) || group.Name != "GroupHelper" || group.Base != 70)
            return null;
        if (!TryExtractOwnElementClass(data, start, size, classId, out _)) return null;
        var fields = SerializationFields(3681);
        var cellFields = SerializationFields(653);
        var helperFields = SerializationFields(3682);
        if (fields == null || cellFields is not { Count: 1 }
            || cellFields[0] is not { Declaring: 653, Name: "m_cells", Type: 14, Mods: 81, Ref: null }
            || helperFields is not { Count: 4 }
            || helperFields[0] is not { Declaring: 348, Name: "m_pElemIdsPairSet", Type: 14, Mods: 1, Ref: null }
            || helperFields[1] is not { Declaring: 348, Name: "m_nextIndex", Type: 4, Mods: 0, Ref: null }
            || helperFields[2] is not { Declaring: 3682, Name: "m_runAndLandings", Type: 14, Mods: 80, Ref: 20 }
            || helperFields[3] is not { Declaring: 3682, Name: "m_supports", Type: 14, Mods: 80, Ref: 20 }
            || !HasExactSchemaField(fields, 37, "m_id", 14, 0, 20)
            || !HasExactSchemaField(fields, 37, "m_cellList", 14, 1, null)
            || !HasExactSchemaField(fields, 3681, "m_runsAndLandings", 14, 80, 20)
            || !HasExactSchemaField(fields, 3681, "m_supports", 14, 80, 20)
            || !HasExactSchemaField(fields, 3681, "m_registeredRailings", 14, 80, 20)
            || !HasExactSchemaField(fields, 3681, "m_multistoryUpToLevelId", 14, 0, 20))
            return null;
        var cursor = new Cursor { D = data, Pos = start + 14, End = (int)end - 4, RootClassId = classId };
        var queue = new List<(ushort Cls, uint Id)>();
        var root = new NativeParams();
        int cellIndex = -1, helperIndex = -1, dataCellIndex = -1, multistoryLevel = 0;
        int[]? registeredRailings = null;
        StairComponentIds? result = null;
        bool dataCellSchema = Schemas.TryGetValue(2536, out var dataCell) && dataCell.Name == "MultistoryStairsDataCell"
            && SerializationFields(2536) is { Count: 3 } dataFields
            && dataFields[0] is { Declaring: 2536, Name: "m_levelIdIndexPairSet", Type: 14, Mods: 0, Ref: 90 }
            && dataFields[1] is { Declaring: 2536, Name: "m_instancesUpToLevelId", Type: 14, Mods: 0, Ref: 20 }
            && dataFields[2] is { Declaring: 2536, Name: "m_nextIndex", Type: 4, Mods: 0, Ref: null };
        try
        {
            foreach (var field in fields)
            {
                if (field.Declaring == 3681 && field.Name is "m_runsAndLandings" or "m_supports")
                {
                    // The current layout stores these in the owned helper.
                    // Do not reconcile two potentially different authorities here.
                    if (ReadIds(cursor).Length != 0) return null;
                }
                else if (field.Declaring == 3681 && field.Name == "m_registeredRailings")
                    registeredRailings = ReadIds(cursor);
                else if (field.Declaring == 3681 && field.Name == "m_multistoryUpToLevelId")
                    multistoryLevel = ReadI32(cursor);
                else
                {
                    int before = queue.Count;
                    ReadField(cursor, field, queue, 0, root);
                    if (field.Declaring == 37 && field.Name == "m_cellList")
                    {
                        if (queue.Count != before + 1 || queue[before].Cls != 653) return null;
                        cellIndex = before;
                    }
                }
            }
            uint headerOwner = BitConverter.ToUInt32(data, start);
            if (cellIndex < 0 || registeredRailings == null
                || !root.Fields.TryGetValue("m_id", out int owner) || owner <= 0 || headerOwner != (uint)owner)
                return null;
            for (int index = 0; index < queue.Count; index++)
            {
                if (queue.Count > 100_000) return result;
                if (index == cellIndex)
                {
                    uint count = ReadU32(cursor);
                    if (count == uint.MaxValue) count = 0;
                    if (count > 100_000 || count * 4L > cursor.End - cursor.Pos) return null;
                    for (uint item = 0; item < count; item++)
                    {
                        int before = queue.Count;
                        ReadPtr(cursor, queue);
                        if (queue.Count == before + 1 && queue[before].Cls == 3682)
                        {
                            if (helperIndex >= 0) return null;
                            helperIndex = before;
                        }
                        else if (queue.Count == before + 1 && queue[before].Cls == 2536)
                            dataCellIndex = helperIndex >= 0 && dataCellIndex < 0 && dataCellSchema ? before : int.MaxValue;
                    }
                    if (helperIndex < 0) return null;
                }
                else if (index == helperIndex)
                {
                    ReadField(cursor, helperFields[0], queue, 1, null);
                    ReadField(cursor, helperFields[1], queue, 1, null);
                    int[] runs = ReadIds(cursor), supports = ReadIds(cursor);
                    result = new(owner, runs, supports, registeredRailings, multistoryLevel);
                    if (dataCellIndex < index || dataCellIndex == int.MaxValue) return result;
                }
                else if (index == dataCellIndex)
                {
                    // Inline pair set: count, then (level id, index) per repeated level.
                    uint count = ReadU32(cursor);
                    if (count == uint.MaxValue) count = 0;
                    if (count > 100_000 || count * 8L + 8 > cursor.End - cursor.Pos) return result;
                    var levels = new int[(int)count];
                    for (int item = 0; item < levels.Length; item++)
                    {
                        levels[item] = ReadI32(cursor);
                        ReadI32(cursor);
                    }
                    return levels.Any(level => level <= 0) || levels.Distinct().Count() != levels.Length
                        ? result : result! with { MultistoryCopyLevelIds = levels };
                }
                else if (queue[index].Cls is 3678 or 3685 or 677)
                {
                    // Current native StairsCopyGeomGStep / StairsElementRefGStep
                    // before the owned helper. Select the exact counted-history
                    // grammar explicitly, not the generic earliest legacy layout.
                    ushort step = queue[index].Cls;
                    string name = step switch { 3678 => "StairsCopyGeomGStep", 3685 => "StairsElementRefGStep", _ => "CleanPairGStep" };
                    var stepFields = SerializationFields(step);
                    if (!Schemas.TryGetValue(step, out var schema) || schema.Name != name
                        || (step == 677 ? schema.Base != 678 : schema.Base != 96 || schema.Fields.Count != 0)
                        || !HasExactCurrentStepFields(step, stepFields)
                        || !StairGraphLayout.TryCurrentHistoryEnd(data, cursor.Pos, cursor.End, out int finish)) return result;
                    cursor.Pos = finish;
                    foreach (var field in stepFields!)
                    {
                        if (field.Declaring == 97 && field.Name is "m_faceHistTable"
                            or "m_curveHistTableSet" or "m_edgeHistTable" or "m_edgeHistTableReverse") continue;
                        if (field is { Declaring: 677, Name: "m_affectedEdgesTags", Type: 14, Mods: 80, Ref: null })
                        {
                            if (!StairGraphLayout.TryAffectedTags(data, cursor.Pos, cursor.End, out int tagsEnd, out _)) return result;
                            cursor.Pos = tagsEnd;
                        }
                        else ReadField(cursor, field, queue, 1, null);
                    }
                }
                else ReadDeferredObject(cursor, queue[index].Cls, queue);
            }
        }
        catch (DecodeFail) { return result; }
        return result;

        static int[] ReadIds(Cursor cursor)
        {
            uint count = ReadU32(cursor);
            if (count == uint.MaxValue) count = 0;
            if (count > 100_000 || count * 4L > cursor.End - cursor.Pos) throw Fail;
            var ids = new int[(int)count];
            for (int index = 0; index < ids.Length; index++) ids[index] = ReadI32(cursor);
            return ids;
        }

        static bool HasExactCurrentStepFields(ushort step, IReadOnlyList<TfField>? fields)
        {
            if (fields == null || fields.Count != (step == 677 ? 16 : 8)
                || fields[0] is not { Declaring: 97, Name: "m_faceHistTable", Type: 14, Mods: 80, Ref: null }
                || fields[1] is not { Declaring: 97, Name: "m_curveHistTableSet", Type: 14, Mods: 80, Ref: null }
                || fields[2] is not { Declaring: 97, Name: "m_edgeHistTable", Type: 14, Mods: 80, Ref: null }
                || fields[3] is not { Declaring: 97, Name: "m_edgeHistTableReverse", Type: 14, Mods: 80, Ref: 102 }
                || fields[4] is not { Declaring: 97, Name: "m_id", Type: 4, Mods: 0, Ref: null }
                || fields[5] is not { Declaring: 96, Name: "m_version", Type: 4, Mods: 0, Ref: null }
                || fields[6] is not { Declaring: 96, Name: "m_flags", Type: 4, Mods: 0, Ref: null }
                || fields[7] is not { Declaring: 96, Name: "m_pElem", Type: 14, Mods: 3, Ref: null }) return false;
            if (step != 677) return true;
            return Schemas.TryGetValue(678, out var pair) && pair.Name == "PairBaseGStep" && pair.Base == 96
                && fields[8] is { Declaring: 678, Name: "m_otherId", Type: 14, Mods: 0, Ref: 20 }
                && fields[9] is { Declaring: 678, Name: "m_userSet", Type: 1, Mods: 0, Ref: null }
                && fields[10] is { Declaring: 678, Name: "m_allowUserUnjoin", Type: 1, Mods: 0, Ref: null }
                && fields[11] is { Declaring: 678, Name: "m_isAssociatedWithColJoinStep", Type: 1, Mods: 0, Ref: null }
                && fields[12] is { Declaring: 677, Name: "m_affectedEdgesTags", Type: 14, Mods: 80, Ref: null }
                && fields[13] is { Declaring: 677, Name: "m_healingParents", Type: 4, Mods: 80, Ref: null }
                && fields[14] is { Declaring: 677, Name: "m_hasCoincFaces", Type: 1, Mods: 0, Ref: null }
                && fields[15] is { Declaring: 677, Name: "m_doNothing", Type: 1, Mods: 0, Ref: null };
        }
    }

    private static bool HasExactSchemaField(
        IReadOnlyList<TfField> fields, int declaring, string name,
        byte type, byte mods, int? reference) =>
        fields.Count(field =>
            field.Declaring == declaring
            && field.Name == name
            && field.Type == type
            && field.Mods == mods
            && field.Ref == reference) == 1;

    /// <summary>Decode the exact named layer vector owned by a CompoundCeilingType.
    /// The HostObjAttr.m_pCompoundStructure queue position is recorded while walking
    /// the root schema; no class-tag or byte-marker search is used. Every deferred
    /// object is then consumed in FIFO order and the complete payload must end exactly
    /// at the footer.</summary>
    internal static bool TryExtractCompoundCeilingLayers(
        byte[] d, int blockStart, uint dataSize, ushort classId,
        out RawCompoundStructureLayer[] layers)
    {
        layers = [];
        return classId == 646 && TryExtractHostTypeLayersCore(d, blockStart, dataSize, classId,
            requireCompleteDeferredGraph: true, out layers);
    }

    /// <summary>Own HostObjAttr/CompoundStructure field records for BasicWallType,
    /// FloorAttributes, CompoundCeilingType, RoofAttributes and BuildingPadType (the last two add nothing to
    /// HostObjAttr, so they read like the first three). The complete root and FIFO prefix through
    /// its own CompoundStructure are decoded. Later deferred geometry/cells are outside
    /// this layer-field scope; no public layer normalization is applied.</summary>
    internal static bool TryExtractHostTypeLayers(
        byte[] d, int blockStart, uint dataSize, ushort classId,
        out RawCompoundStructureLayer[] layers)
        => TryExtractHostTypeLayersCore(d, blockStart, dataSize, classId,
            requireCompleteDeferredGraph: false, out layers);

    private static bool TryExtractHostTypeLayersCore(
        byte[] d, int blockStart, uint dataSize, ushort classId,
        bool requireCompleteDeferredGraph, out RawCompoundStructureLayer[] layers)
    {
        const ushort compoundStructureClassId = 737;
        layers = [];
        if (classId is not (512 or 1915 or 646 or 3452 or 571)
            || blockStart < 0
            || blockStart > d.Length - 14
            || (long)blockStart + 16 + dataSize > d.Length
            || BitConverter.ToUInt32(d, blockStart + 8) != dataSize
            || BitConverter.ToUInt16(d, blockStart + 12) != classId)
            return false;

        int payloadStart = blockStart + 14;
        int payloadEnd = blockStart + 16 + checked((int)dataSize);
        if (payloadEnd - payloadStart < 6
            || BitConverter.ToUInt32(d, payloadEnd - 4) != dataSize)
            return false;

        var rootFields = SerializationFields(classId);
        if (rootFields == null) return false;
        var cur = new Cursor
        {
            D = d,
            Pos = payloadStart,
            End = payloadEnd - 4,
            RootClassId = classId,
        };
        var queue = new List<(ushort Cls, uint Id)>();
        var capture = new NativeParams();
        int compoundPointerCount = 0;
        int compoundQueueIndex = -1;
        try
        {
            foreach (var field in rootFields)
            {
                int queueCount = queue.Count;
                ReadField(cur, field, queue, 0, capture);
                if (field.Declaring != 105
                    || field.Name != "m_pCompoundStructure")
                    continue;

                compoundPointerCount++;
                if (field.Type != 0x0E || field.Mods != 1
                    || queue.Count != queueCount + 1
                    || queue[queueCount].Cls != compoundStructureClassId)
                    return false;
                compoundQueueIndex = queueCount;
            }

            uint headerElementId = BitConverter.ToUInt32(d, blockStart);
            if (compoundPointerCount != 1 || compoundQueueIndex < 0
                || !capture.Fields.TryGetValue("m_id", out int serializedOwnerId)
                || serializedOwnerId <= 0 || unchecked((uint)serializedOwnerId) != headerElementId)
                return false;

            RawCompoundStructureLayer[]? decodedLayers = null;
            for (int queueIndex = 0; queueIndex < queue.Count; queueIndex++)
            {
                var (queuedClass, _) = queue[queueIndex];
                if (queueIndex == compoundQueueIndex)
                {
                    if (queuedClass != compoundStructureClassId
                        || !TryReadNamedCompoundStructure(
                            cur, queue, out decodedLayers))
                        return false;
                    if (!requireCompleteDeferredGraph && queueIndex + 1 < queue.Count)
                    {
                        layers = decodedLayers!;
                        return true;
                    }
                }
                else
                {
                    ReadDeferredObject(cur, queuedClass, queue);
                }
            }

            if (decodedLayers == null || cur.Pos != cur.End)
                return false;
            layers = decodedLayers;
            return true;
        }
        catch (DecodeFail)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    internal static string? ExtractHostTypeOwnName(byte[] data, int start, uint size, ushort classId)
        => classId is 512 or 1915 or 646 or 3452 or 571 && HasExactOwnElementRoot(data, start, size, classId)
            ? ExtractOwnSymbolInfoName(data, start, size, classId) : null;

    private static bool TryReadNamedCompoundStructure(
        Cursor cur, List<(ushort Cls, uint Id)> queue,
        out RawCompoundStructureLayer[]? layers)
        => TryReadNamedCompoundStructure(cur, queue, out layers, out _);

    private static bool TryReadNamedCompoundStructure(
        Cursor cur, List<(ushort Cls, uint Id)> queue,
        out RawCompoundStructureLayer[]? layers, out int verticalQueueIndex)
        => TryReadNamedCompoundStructure(cur, queue, out layers, out verticalQueueIndex, out _);

    private static bool TryReadNamedCompoundStructure(
        Cursor cur, List<(ushort Cls, uint Id)> queue,
        out RawCompoundStructureLayer[]? layers, out int verticalQueueIndex,
        out bool? verticalStructureIsNull, bool allowNullLayerContainer = false)
    {
        layers = null;
        verticalQueueIndex = -1;
        verticalStructureIsNull = null;
        var fields = SerializationFields(737);
        var layerFields = SerializationFields(738);
        if (fields == null || layerFields == null
            || layerFields.Count != 7
            || layerFields[0] is not
                { Declaring: 738, Name: "m_layerWidth", Type: 0x07, Mods: 0 }
            || layerFields[1] is not
                { Declaring: 738, Name: "m_layerFunction", Type: 0x04, Mods: 0 }
            || layerFields[2] is not
                { Declaring: 738, Name: "m_embeddingType", Type: 0x04, Mods: 0 }
            || layerFields[3] is not
                { Declaring: 738, Name: "m_materialId", Type: 0x0E, Mods: 0, Ref: 20 }
            || layerFields[4] is not
                { Declaring: 738, Name: "m_profileId", Type: 0x0E, Mods: 0, Ref: 20 }
            || layerFields[5] is not
                { Declaring: 738, Name: "m_layerId", Type: 0x04, Mods: 0 }
            || layerFields[6] is not
                { Declaring: 738, Name: "m_layerCapFlag", Type: 0x01, Mods: 0 })
            return false;

        int namedLayerFieldCount = 0;
        int verticalFieldCount = 0;
        foreach (var field in fields)
        {
            if (field.Declaring != 737 || field.Name != "m_layers")
            {
                int before = queue.Count;
                int position = cur.Pos;
                ReadField(cur, field, queue, 1, null);
                // Preserve the existing layer/vertical-region reader's queue-index contract.
                if (field.Declaring == 737 && field.Name == "m_oVertRegStructure"
                    && field.Type == 0x0E && field.Mods == 1
                    && queue.Count == before + 1 && queue[before].Cls == 4099)
                    verticalQueueIndex = before;
                if (field.Declaring == 737 && field.Name == "m_oVertRegStructure")
                {
                    verticalFieldCount++;
                    if (verticalFieldCount == 1 && field.Type == 0x0E && field.Mods == 1 && field.Ref == null)
                    {
                        verticalStructureIsNull = BitConverter.ToUInt32(cur.D, position) == 0;
                    }
                    else verticalStructureIsNull = null;
                }
                continue;
            }

            namedLayerFieldCount++;
            if (field.Type != 0x0E || field.Mods != 0x50
                || namedLayerFieldCount != 1)
                return false;
            uint count = ReadU32(cur);
            if (count == uint.MaxValue && allowNullLayerContainer) count = 0;
            if (count > int.MaxValue
                || (long)count * 29 > cur.End - cur.Pos)
                return false;

            var decoded = new RawCompoundStructureLayer[checked((int)count)];
            for (int index = 0; index < decoded.Length; index++)
            {
                if (cur.Pos + 29 > cur.End) throw Fail;
                long widthBits = BitConverter.ToInt64(cur.D, cur.Pos);
                cur.Pos += 8;
                int function = ReadI32(cur);
                int embeddingType = ReadI32(cur);
                int materialId = ReadI32(cur);
                int profileId = ReadI32(cur);
                int layerId = ReadI32(cur);
                byte capFlag = cur.D[cur.Pos++];
                decoded[index] = new RawCompoundStructureLayer(
                    widthBits, function, embeddingType, materialId,
                    profileId, layerId, capFlag);
            }
            layers = decoded;
        }
        return namedLayerFieldCount == 1 && layers != null;
    }

    /// <summary>Read the grid reached by the own HostObjAttr -> CompoundStructure ->
    /// m_oVertRegStructure pointer chain. Full FIFO/footer validation is mandatory;
    /// this does not classify homogeneity or compute API layer widths. Not yet consumed
    /// by RvtFileReader/ElementBuilder/IfcExporter.</summary>
    internal static bool TryExtractHostTypeVerticalRegions(
        byte[] data, int start, uint size, ushort classId,
        out RawHostVerticalRegions regions)
    {
        regions = null!;
        // Reuse the existing exact root/frame/owner/layer-prefix checks. A separate
        // complete walk avoids changing the established ceiling Finish contract.
        if (!TryExtractHostTypeLayers(data, start, size, classId, out var ownLayers)
            || !HasExactSchemaField(SerializationFields(737)!, 737,
                "m_oVertRegStructure", 0x0E, 1, null)
            || !HasVerticalRegionSchemas()) return false;
        var cur = new Cursor
        {
            D = data, Pos = start + 14, End = start + 12 + checked((int)size),
            RootClassId = classId,
        };
        var queue = new List<(ushort Cls, uint Id)>();
        int compoundIndex = -1, verticalIndex = -1;
        RawHostVerticalRegions? decoded = null;
        try
        {
            foreach (var field in SerializationFields(classId)!)
            {
                int before = queue.Count;
                ReadField(cur, field, queue, 0, null);
                if (field.Declaring == 105 && field.Name == "m_pCompoundStructure")
                {
                    if (compoundIndex >= 0 || queue.Count != before + 1
                        || queue[before].Cls != 737) return false;
                    compoundIndex = before;
                }
            }
            for (int index = 0; index < queue.Count; index++)
            {
                ushort cls = queue[index].Cls;
                cur.CurrentDeferredClassId = cls;
                cur.CurrentDeferredQueueIndex = index;
                cur.CurrentGeomStepQueueIndex = IsSubclassOf(cls, 96) ? index : -1;
                if (index == compoundIndex)
                {
                    if (!TryReadNamedCompoundStructure(cur, queue, out var layers, out verticalIndex)
                        || verticalIndex < 0 || !ownLayers.SequenceEqual(layers!)) return false;
                }
                else if (index == verticalIndex)
                {
                    if (cls != 4099 || decoded != null) return false;
                    decoded = ReadNamedVerticalRegions(cur, queue);
                }
                else ReadDeferredObject(cur, cls, queue);
            }
            if (decoded == null || cur.Pos != cur.End) return false;
            regions = decoded;
            return true;
        }
        catch (DecodeFail) { return false; }
        catch (OverflowException) { return false; }
    }

    private static bool HasVerticalRegionSchemas()
    {
        bool Matches(int cls, string name, params (string Name, byte Type, byte Mods, int? Ref)[] expected)
        {
            var fields = SerializationFields(cls);
            return Schemas.TryGetValue(cls, out var schema) && schema.Name == name
                && fields != null && fields.Count == expected.Length
                && fields.Select((field, index) => field.Declaring == cls
                    && field.Name == expected[index].Name && field.Type == expected[index].Type
                    && field.Mods == expected[index].Mods && field.Ref == expected[index].Ref).All(value => value);
        }
        return Matches(4099, "VerticalRegionsStructure",
                ("m_extendableRegionIdsTop", 4, 80, null),
                ("m_extendableRegionIdsBottom", 4, 80, null),
                ("m_regionToLayerMap", 14, 80, 221), ("m_grid", 14, 0, 3394),
                ("m_sampleHeight", 7, 0, null), ("m_cutOffHeight", 7, 0, null),
                ("m_bPreviewDimsAreValid", 1, 0, null),
                ("m_wallSweeps", 14, 80, null), ("m_reveals", 14, 80, 4100))
            && Matches(221, "std::pair< int, int >", ("first", 4, 0, null), ("second", 4, 0, 221))
            && Matches(3394, "RectangularGrid", ("m_segments", 14, 80, 2118),
                ("m_regions", 14, 80, 3388), ("m_tol", 7, 0, null))
            && Matches(2118, "GridSegment", ("m_coordinate", 7, 0, null),
                ("m_id", 4, 0, null), ("m_orientation", 4, 0, null), ("m_regionId", 4, 16, null))
            && SerializationFields(2118)!.Single(field => field.Name == "m_regionId").Cnt == 2
            && Matches(3388, "RectGridRegion", ("m_id", 4, 0, null), ("m_segmentIds", 4, 80, null));
    }

    private static RawHostVerticalRegions ReadNamedVerticalRegions(
        Cursor cur, List<(ushort Cls, uint Id)> queue)
    {
        int[] top = [], bottom = [];
        RawRegionLayerPair[] map = [];
        RawGridSegment[] segments = [];
        RawGridRegion[] regions = [];
        double tolerance = 0, sampleHeight = 0, cutOffHeight = 0;
        byte preview = 0;
        foreach (var field in SerializationFields(4099)!)
        {
            switch (field.Name)
            {
                case "m_extendableRegionIdsTop": top = IntArray(); break;
                case "m_extendableRegionIdsBottom": bottom = IntArray(); break;
                case "m_regionToLayerMap":
                    map = new RawRegionLayerPair[Count(8)];
                    for (int i = 0; i < map.Length; i++) map[i] = new(ReadI32(cur), ReadI32(cur));
                    break;
                case "m_grid":
                    foreach (var gridField in SerializationFields(3394)!)
                    {
                        switch (gridField.Name)
                        {
                            case "m_segments":
                                segments = new RawGridSegment[Count(24)];
                                for (int i = 0; i < segments.Length; i++)
                                    segments[i] = new(Finite(), ReadI32(cur), ReadI32(cur), ReadI32(cur), ReadI32(cur));
                                break;
                            case "m_regions":
                                regions = new RawGridRegion[Count(8)];
                                for (int i = 0; i < regions.Length; i++) regions[i] = new(ReadI32(cur), IntArray());
                                break;
                            case "m_tol": tolerance = Finite(); if (tolerance < 0) throw Fail; break;
                        }
                    }
                    break;
                case "m_sampleHeight": sampleHeight = Finite(); break;
                case "m_cutOffHeight": cutOffHeight = Finite(); break;
                case "m_bPreviewDimsAreValid":
                    if (cur.Pos >= cur.End) throw Fail;
                    preview = cur.D[cur.Pos++];
                    if (preview > 1) throw Fail;
                    break;
                default: ReadField(cur, field, queue, 1, null); break;
            }
        }
        return new(top, bottom, map, segments, regions, tolerance, sampleHeight, cutOffHeight, preview);

        int Count(int minimumRecordSize)
        {
            uint count = ReadU32(cur);
            if (count == uint.MaxValue) return 0;
            if (count > 100_000 || count * (long)minimumRecordSize > cur.End - cur.Pos) throw Fail;
            return (int)count;
        }
        int[] IntArray()
        {
            var values = new int[Count(4)];
            for (int i = 0; i < values.Length; i++) values[i] = ReadI32(cur);
            return values;
        }
        double Finite()
        {
            double value = ReadF64(cur);
            if (!double.IsFinite(value)) throw Fail;
            return value;
        }
    }

    private static void ReadDeferredObject(
        Cursor cur, ushort queuedClass, List<(ushort Cls, uint Id)> queue)
    {
        if (queuedClass == 737)
        {
            if (!TryReadNamedCompoundStructure(cur, queue, out _)) throw Fail;
            return;
        }
        if (ParamSetTags.TryGetValue(queuedClass, out var parameterStore))
        {
            uint count = ReadU32(cur);
            if (count > 100_000) throw Fail;
            var recordFields = SerializationFields(parameterStore.RecordClass)
                ?? throw Fail;
            for (uint index = 0; index < count; index++)
                foreach (var recordField in recordFields)
                    ReadField(cur, recordField, queue, 1, null);
            return;
        }

        var fields = SerializationFields(queuedClass) ?? throw Fail;
        foreach (var field in fields)
            ReadField(cur, field, queue, 1, null);
    }

    /// <summary>Diagnostic-only: full deferred-queue trace of an owner block.
    /// Walks the owner's root schema fields (queuing every owned/polymorphic
    /// pointer), then resumes the FIFO, generically decoding every queued item via
    /// <see cref="ReadDeferredObject"/> (which itself queues that item's own nested
    /// pointers), until the queue is exhausted or a DecodeFail stops the walk. Returns the
    /// full ordered list of (classId, handle) pairs actually queued.
    /// Not used by production decode paths.</summary>
    internal static List<(ushort Cls, uint Id)> TraceQueue(
        byte[] d, int blockStart, uint dataSize, ushort classId)
    {
        var rootFields = SerializationFields(classId) ?? throw Fail;
        int payloadStart = blockStart + 14;
        int payloadEnd = blockStart + 16 + (int)dataSize;
        var cur = new Cursor { D = d, Pos = payloadStart, End = payloadEnd - 4, RootClassId = classId };
        var queue = new List<(ushort Cls, uint Id)>();
        try
        {
            foreach (var f in rootFields) ReadField(cur, f, queue, 0, null);
            for (int qi = 0; qi < queue.Count; qi++)
                ReadDeferredObject(cur, queue[qi].Cls, queue);
        }
        catch (DecodeFail) { /* keep whatever was captured */ }
        return queue;
    }

    /// <summary>Record for one Connector-family object reached through an
    /// owner's FamilyInstance.m_pConnectorManager / RbsCurve.m_pConnectorManager chain.
    /// <see cref="Index"/> is Connector.m_nIndex or ConnectorElem.m_index
    /// (whichever the resolved class declares). <see cref="FlowDirection"/> is read off
    /// whichever reachable object actually carries m_eDirection/m_eFlowDirection: either the
    /// FamilyConnector(1775)-subclass's own m_pDomain (ConnectorDomainHvac/Piping), or, for plumbing-fixture
    /// connectors that serialize as the PLAIN Connector(771) base with no m_pDomain field at all,
    /// one of the per-connector m_modifiers entries (MEPFamilyConnectorCalculationHvac/Piping).
    /// Both paths funnel into the same capture bucket since the field names are shared.</summary>
    internal sealed record ConnectorRecord(
        ushort ConnectorClassId, uint Handle, int? Index, int? Mode, int? FlowDirection);

    /// <summary>Decodes the exact ConnectorManager.m_connPtrArray population reached through
    /// a single owner block's m_pConnectorManager pointer.
    /// Walks the owner's OWN root schema fields to find and queue m_pConnectorManager, then
    /// resumes the deferred FIFO: every other queued item is consumed generically
    /// (<see cref="ReadDeferredObject"/>) to keep cursor alignment; the connector manager's
    /// own fields, each connector's own fields (m_nIndex/m_mode, m_pDomain, every m_modifiers entry)
    /// and each such auxiliary object's fields are walked directly into the SAME per-connector
    /// capture bucket, so m_eDirection/m_eFlowDirection lands regardless of which specific object
    /// carries it. Returns null when the owner has no schema, its footer doesn't validate, or
    /// m_pConnectorManager never resolves to a non-null connector manager (no connectors on
    /// this element). A <see cref="DecodeFail"/> mid-walk stops the walk and returns whatever
    /// was captured so far (same discipline as ExtractParams).</summary>
    internal static List<ConnectorRecord>? TryExtractConnectors(
        byte[] d, int blockStart, uint dataSize, ushort classId,
        List<NativeParams>? rawCapturesOut = null)
    {
        var rootFields = SerializationFields(classId);
        if (rootFields == null) return null;
        int payloadStart = blockStart + 14;
        int payloadEnd = blockStart + 16 + (int)dataSize;
        if (payloadEnd > d.Length || payloadEnd - payloadStart < 6) return null;
        if (BitConverter.ToUInt32(d, payloadEnd - 4) != dataSize) return null;

        var cur = new Cursor { D = d, Pos = payloadStart, End = payloadEnd - 4, RootClassId = classId };
        var queue = new List<(ushort Cls, uint Id)>();
        int connectorManagerQueueIndex = -1;
        try
        {
            foreach (var f in rootFields)
            {
                int before = queue.Count;
                ReadField(cur, f, queue, 0, null);
                if (f.Name == "m_pConnectorManager" && queue.Count == before + 1)
                    connectorManagerQueueIndex = before;
            }
        }
        catch (DecodeFail) { return null; }
        if (connectorManagerQueueIndex < 0) return null;

        var connectorQueueIndices = new HashSet<int>();
        var auxQueueIndexToResultIndex = new Dictionary<int, int>();
        var connectorClassIds = new List<ushort>();
        var connectorHandles = new List<uint>();
        var resultCaptures = new List<NativeParams>();
        try
        {
            for (int qi = 0; qi < queue.Count; qi++)
            {
                var (cls, id) = queue[qi];
                if (qi == connectorManagerQueueIndex)
                {
                    var cmFields = SerializationFields(cls) ?? throw Fail;
                    foreach (var f in cmFields)
                    {
                        if (f.Name != "m_connPtrArray") { ReadField(cur, f, queue, 1, null); continue; }
                        int before = queue.Count;
                        ReadField(cur, f, queue, 1, null);
                        for (int k = before; k < queue.Count; k++) connectorQueueIndices.Add(k);
                    }
                    continue;
                }
                if (connectorQueueIndices.Contains(qi))
                {
                    var connFields = SerializationFields(cls) ?? throw Fail;
                    var capture = new NativeParams();
                    int resultIndex = resultCaptures.Count;
                    resultCaptures.Add(capture);
                    connectorClassIds.Add(cls);
                    connectorHandles.Add(id);
                    foreach (var f in connFields)
                    {
                        if (f.Name is "m_pDomain" or "m_modifiers")
                        {
                            int before = queue.Count;
                            ReadField(cur, f, queue, 1, capture);
                            for (int k = before; k < queue.Count; k++)
                                auxQueueIndexToResultIndex[k] = resultIndex;
                            continue;
                        }
                        ReadField(cur, f, queue, 1, capture);
                    }
                    continue;
                }
                if (auxQueueIndexToResultIndex.TryGetValue(qi, out int owningResultIndex))
                {
                    var auxFields = SerializationFields(cls) ?? throw Fail;
                    var capture = resultCaptures[owningResultIndex];
                    foreach (var f in auxFields) ReadField(cur, f, queue, 1, capture);
                    continue;
                }
                ReadDeferredObject(cur, cls, queue);
            }
        }
        catch (DecodeFail) { /* keep whatever was captured */ }

        if (rawCapturesOut != null) rawCapturesOut.AddRange(resultCaptures);
        var results = new List<ConnectorRecord>(resultCaptures.Count);
        for (int i = 0; i < resultCaptures.Count; i++)
        {
            var capture = resultCaptures[i];
            results.Add(new ConnectorRecord(
                connectorClassIds[i], connectorHandles[i],
                capture.Fields.TryGetValue("m_nIndex", out int nIdx) ? nIdx
                    : capture.Fields.TryGetValue("m_index", out int idx) ? idx : null,
                capture.Fields.TryGetValue("m_mode", out int mode) ? mode : null,
                capture.Fields.TryGetValue("m_eDirection", out int dir) ? dir
                    : capture.Fields.TryGetValue("m_eFlowDirection", out int fdir) ? fdir : null));
        }
        return results;
    }

    /// <summary>Reads the own BasicWallType root and WallType.m_function. The complete
    /// root schema, header/footer and serialized owner must agree. Deferred geometry is
    /// does not determine either the native type class or its root-level function.</summary>
    internal static bool TryExtractBasicWallTypeFunction(
        byte[] data, int start, uint size, ushort classId, out int function)
    {
        function = default;
        if (classId != 512 || start < 0 || start > data.Length - 16
            || size > int.MaxValue || (long)start + 16 + size > data.Length
            || BitConverter.ToUInt32(data, start + 8) != size
            || BitConverter.ToUInt16(data, start + 12) != classId
            || !Schemas.TryGetValue(512, out TfClass? schema)
            || schema.Name != "BasicWallType" || schema.Base != 513)
            return false;
        var fields = SerializationFields(512);
        if (fields == null || !HasExactSchemaField(fields, 513, "m_function", 4, 0, null))
            return false;
        var root = ExtractParams(data, start, size, classId);
        if (root == null || !root.Fields.TryGetValue("m_id", out int owner)
            || owner <= 0 || (uint)owner != BitConverter.ToUInt32(data, start)
            || !root.Fields.TryGetValue("m_function", out int value) || value is < 0 or > 5)
            return false;
        function = value;
        return true;
    }

    /// <summary>Confirms a StackedWallType from its complete own schema root and owner,
    /// not from a category, type name, child reference, or inferred function.</summary>
    internal static bool IsExactStackedWallTypeRoot(byte[] data, int start, uint size, ushort classId)
    {
        if (classId != 3666 || start < 0 || start > data.Length - 16
            || size > int.MaxValue || (long)start + 16 + size > data.Length
            || BitConverter.ToUInt32(data, start + 8) != size
            || BitConverter.ToUInt16(data, start + 12) != classId
            || !Schemas.TryGetValue(classId, out TfClass? schema)
            || schema.Name != "StackedWallType" || schema.Base != 513)
            return false;
        var root = ExtractParams(data, start, size, classId);
        return root != null && root.Fields.TryGetValue("m_id", out int owner)
            && owner > 0 && (uint)owner == BitConverter.ToUInt32(data, start);
    }

    /// <summary>Extracts the authored WallType.m_function only from an exact
    /// NewCurtainWallType root block. Other WallType subclasses deliberately fail closed:
    /// their persisted function does not establish this type relation.</summary>
    public static bool TryExtractNewCurtainWallTypeFunction(
        byte[] d, int blockStart, uint dataSize, ushort classId, out int function)
    {
        const ushort NewCurtainWallTypeClassId = 2559;
        function = default;
        if (classId != NewCurtainWallTypeClassId
            || blockStart < 0
            || blockStart > d.Length - 14
            || (long)blockStart + 16 + dataSize > d.Length
            || BitConverter.ToUInt32(d, blockStart + 8) != dataSize
            || BitConverter.ToUInt16(d, blockStart + 12) != classId)
            return false;

        NativeParams? decoded = ExtractParams(d, blockStart, dataSize, classId);
        if (decoded == null
            || !decoded.Fields.TryGetValue("m_id", out int serializedOwnerId)
            || serializedOwnerId != unchecked((int)BitConverter.ToUInt32(d, blockStart))
            || !decoded.Fields.TryGetValue("m_function", out int value)
            || value is < 0 or > 5)
            return false;

        function = value;
        return true;
    }

    /// <summary>Own NewCurtainWallType name through Symbol.m_symbolInfo -> SymbolInfo.m_name.
    /// Parameter strings (including type marks), other symbols and later geometry cannot
    /// provide this name. The root and all deferred objects preceding SymbolInfo must decode.</summary>
    internal static string? ExtractNewCurtainWallTypeName(
        byte[] data, int start, uint size, ushort classId)
    {
        if (!TryExtractNewCurtainWallTypeFunction(data, start, size, classId, out _)
            || BitConverter.ToInt32(data, start) <= 0)
            return null;
        return ExtractOwnSymbolInfoName(data, start, size, classId);
    }

    /// <summary>Member element ids of a group instance (ElementGroup root, class 1322). The block ends with the
    /// array of its member pairs (element id, running index; 8 bytes each) preceded by their u32 count and followed
    /// by the block footer; the longest pair array that agrees with its count, with positive ids and strictly
    /// increasing indices, is the member list. Null when the block is not a group root or no array agrees.</summary>
    internal static int[]? ExtractGroupMembers(byte[] data, int start, uint size, ushort classId)
    {
        if (classId != 1322 || start < 0 || size < 8) return null;
        long end = (long)start + 16 + size;
        if (end > data.Length || BitConverter.ToUInt32(data, (int)end - 4) != size) return null;
        int payloadStart = start + 14, tail = (int)end - 4;
        for (int count = (tail - payloadStart - 4) / 8; count >= 1; count--)
        {
            int first = tail - 8 * count;
            if (first - 4 < payloadStart || BitConverter.ToInt32(data, first - 4) != count) continue;
            var ids = new int[count];
            bool valid = true;
            int previousIndex = int.MinValue;
            for (int pair = 0; pair < count && valid; pair++)
            {
                ids[pair] = BitConverter.ToInt32(data, first + 8 * pair);
                int index = BitConverter.ToInt32(data, first + 8 * pair + 4);
                valid = ids[pair] > 0 && index > previousIndex;
                previousIndex = index;
            }
            if (valid) return ids;
        }
        return [];
    }

    /// <summary>Same owned SymbolInfo path, independently gated by an exact BasicWallType root.</summary>
    internal static string? ExtractBasicWallTypeName(byte[] data, int start, uint size, ushort classId)
    {
        if (!TryExtractBasicWallTypeFunction(data, start, size, classId, out _)) return null;
        return ExtractOwnSymbolInfoName(data, start, size, classId);
    }

    internal static string? ExtractCorniceTypeName(byte[] data, int start, uint size, ushort classId)
    {
        if (classId != 873 || !HasExactOwnElementRoot(data, start, size, classId)) return null;
        return ExtractOwnSymbolInfoName(data, start, size, classId);
    }

    internal static int? ExtractWallSweepTypeId(byte[] data, int start, uint size, ushort classId)
    {
        if (classId != 4157 || !HasExactOwnElementRoot(data, start, size, classId)) return null;
        var fields = SerializationFields(classId);
        if (fields == null || !HasExactSchemaField(fields, 4157, "m_typeId", 14, 0, 20)) return null;
        var cursor = new Cursor { D = data, Pos = start + 14, End = start + 12 + (int)size, RootClassId = classId };
        var queue = new List<(ushort Cls, uint Id)>();
        int? typeId = null;
        try
        {
            foreach (var field in fields)
            {
                if (field.Declaring == 4157 && field.Name == "m_typeId") typeId = ReadI32(cursor);
                else ReadField(cursor, field, queue, 0, null);
            }
        }
        catch (DecodeFail) { return null; }
        return typeId > 0 && typeId != BitConverter.ToInt32(data, start) ? typeId : null;
    }

    private static bool HasExactOwnElementRoot(byte[] data, int start, uint size, ushort classId)
    {
        if (start < 0 || start > data.Length - 16 || size > int.MaxValue
            || (long)start + 16 + size > data.Length
            || BitConverter.ToUInt32(data, start + 8) != size
            || BitConverter.ToUInt16(data, start + 12) != classId)
            return false;
        var root = ExtractParams(data, start, size, classId);
        return root != null && root.Fields.TryGetValue("m_id", out int owner)
            && owner > 0 && (uint)owner == BitConverter.ToUInt32(data, start);
    }

    /// <summary>The field that names the type of a system occurrence (declaring class and field name), null for another class.</summary>
    internal static (int Declaring, string Name)? SystemOccurrenceTypeField(ushort classId) => classId switch
    {
        3524 or 361 => (362, "m_WallAttributesId"),
        1913 => (1913, "m_floorAttributesId"),
        642 => (642, "m_ceilingAttributesId"),
        // Roof (footprint), ProfileRoof (extrusion), FaceRoof and BuildingPad name their type the same way.
        3451 or 3054 or 1604 => (1605, "m_roofAttributesId"),
        565 => (565, "m_buildingPadTypeId"),
        // Pipes, ducts, cable trays, conduits and their insulation/lining: RbsCurve.getTypeId_.
        _ when IsSubclassOf(classId, 602) => (602, "m_idType"),
        _ => null,
    };

    /// <summary>Own GetTypeId source for SWall/ArcWall, Floor, Ceiling, Roof and BuildingPad. Validate
    /// the complete root and exact ElementId field, not a reference-voting type guess.</summary>
    internal static int? ExtractSystemOccurrenceTypeId(byte[] data, int start, uint size, ushort classId)
    {
        var field = SystemOccurrenceTypeField(classId);
        if (field is not var (declaring, name) || !HasExactOwnElementRoot(data, start, size, classId)) return null;
        var fields = SerializationFields(classId);
        if (fields == null || !HasExactSchemaField(fields, declaring, name, 14, 0, 20)) return null;
        var root = ExtractParams(data, start, size, classId)!;
        // RbsCurve.m_idType is captured under its declaring-class key, as a name shared with other classes would be ambiguous.
        return root.Fields.TryGetValue(declaring == 602 ? "602:m_idType" : name, out int typeId)
            && typeId > 0 && typeId != BitConverter.ToInt32(data, start) ? typeId : null;
    }

    internal static int? ExtractFamilyInstanceSymbolId(byte[] data, int start, uint size, ushort classId)
    {
        if (classId != 1794 || !HasExactOwnElementRoot(data, start, size, classId)) return null;
        var fields = SerializationFields(classId);
        if (fields == null || !HasExactSchemaField(fields, 1794, "m_masterSymbolId", 14, 0, 20)) return null;
        var root = ExtractParams(data, start, size, classId)!;
        return root.Fields.TryGetValue("m_masterSymbolId", out int symbolId)
            && symbolId > 0 && symbolId != BitConverter.ToInt32(data, start) ? symbolId : null;
    }

    /// <summary>Own FamilySymbol name only. The pair descriptor correction is confined to
    /// this read; ordinary parameter, geometry and type-identity decoding is unchanged.</summary>
    internal static string? ExtractFamilySymbolName(byte[] data, int start, uint size, ushort classId)
        => classId == 1829 ? ExtractOwnSymbolInfoName(data, start, size, classId, true) : null;

    /// <summary>An exact own Symbol-subclass root (header, m_id and complete root fields agree).
    /// Classes whose system family depends on one own field also yield that discriminator:
    /// FloorAttributes.m_footingType (Floor / Foundation Slab) and StairsType.m_constructionMethod
    /// (Cast-In-Place / Precast / Assembled Stair), and StairsRunType/StairsLandingType's
    /// own m_hasMonolithicSupport boolean, and StairsSupportType.m_topsideSurfaceType int.
    /// An undecodable discriminator rejects the root.</summary>
    internal static bool TryExtractSystemTypeRoot(
        byte[] data, int start, uint size, ushort classId, out int? discriminator)
    {
        discriminator = null;
        if (!IsSubclassOf(classId, 106) || !HasExactOwnElementRoot(data, start, size, classId)) return false;
        if (classId is 110 or 114) return TryReadOwnDuctProfileClass(data, start, size, classId, out discriminator);
        (int Declaring, string Name, byte Type)? field = classId switch
        {
            1915 => (1915, "m_footingType", 4),
            3742 => (3742, "m_constructionMethod", 4),
            3718 => (3718, "m_hasMonolithicSupport", 1),
            3697 => (3697, "m_hasMonolithicSupport", 1),
            3736 => (3736, "m_topsideSurfaceType", 4),
            // Cable Tray with / without Fittings.
            3130 => (3130, "m_bWithFitting", 1),
            // Conduit with / without Fittings.
            3135 => (3135, "m_bWithFitting", 1),
            _ => null,
        };
        if (field is not var (declaring, name, type)) return true;
        if (!TryReadOwnRootDiscriminator(data, start, size, classId, declaring, name, type, out int value))
            return false;
        discriminator = value;
        return true;
    }

    /// <summary>Own stored FUNCTION_PARAM input, not a public parameter/default-absence
    /// reading. Walk the root and its deferred FIFO to m_pParams; never search/resync bytes.
    /// Later unrelated geometry is outside this bounded FamilyParams prefix.</summary>
    internal sealed record FamilyFunctionInput(int OwnerId, int Value, int RecordIndex, int RecordCount,
        int ParamsStart, int ParamsEnd);

    internal static FamilyFunctionInput? ExtractFamilySymbolFunctionInput(
        byte[] data, int start, uint size, ushort classId)
    {
        if (classId != 1829 || start < 0 || size < 4 || size > int.MaxValue
            || start > data.Length - 16 || (long)start + 16 + size > data.Length
            || BitConverter.ToUInt32(data, start + 8) != size
            || BitConverter.ToUInt16(data, start + 12) != classId
            || BitConverter.ToUInt32(data, start + 12 + (int)size) != size)
            return null;
        var fields = SerializationFields(classId);
        var paramsFields = SerializationFields(714);
        var namedFields = SerializationFields(715);
        if (fields == null || !HasExactSchemaField(fields, 37, "m_id", 14, 0, 20)
            || !HasExactSchemaField(fields, 1829, "m_pParams", 14, 2, null)
            || paramsFields is not { Count: 2 }
            || !HasExactSchemaField(paramsFields, 714, "m_params", 14, 80, null)
            || !HasExactSchemaField(paramsFields, 714, "m_geomRefHandles", 14, 0, null)
            || paramsFields[0].Name != "m_params" || paramsFields[1].Name != "m_geomRefHandles"
            || namedFields is not { Count: 8 }) return null;
        (string Name, int Type, int Mods, int? Ref)[] expected =
        [ ("m_str", 8, 96, null), ("m_oExpression", 14, 1, null),
          ("m_value", 7, 0, null), ("m_elemId", 14, 0, 20),
          ("m_paramId", 14, 0, 20), ("m_int", 4, 0, null),
          ("m_instance", 1, 0, null), ("m_reporting", 1, 0, 715) ];
        for (int i = 0; i < expected.Length; i++)
        {
            var field = namedFields[i]; var wanted = expected[i];
            if (field.Declaring != 715 || field.Name != wanted.Name || field.Type != wanted.Type
                || field.Mods != wanted.Mods || field.Ref != wanted.Ref || field.Cnt != null)
                return null;
        }
        var cursor = new Cursor { D = data, Pos = start + 14, End = start + 12 + (int)size,
            RootClassId = classId, ExactFamilyFunctionExtraction = true };
        var queue = new List<(ushort Cls, uint Id)>();
        int target = -1, owner = 0;
        try
        {
            foreach (var field in fields)
            {
                if (field.Declaring == 37 && field.Name == "m_id")
                { owner = ReadI32(cursor); continue; }
                if (field.Declaring == 1829 && field.Name == "m_pParams")
                {
                    int before = queue.Count, pointerStart = cursor.Pos;
                    ReadPtr(cursor, queue);
                    if (BitConverter.ToUInt32(data, pointerStart) != uint.MaxValue
                        || queue.Count != before + 1 || queue[before].Cls != 714) return null;
                    target = before;
                }
                else ReadField(cursor, field, queue, 0, null);
            }
            if (owner <= 0 || owner != BitConverter.ToInt32(data, start) || target < 0) return null;
            for (int i = 0; i < target; i++) ReadDeferredObject(cursor, queue[i].Cls, queue);
            int paramsStart = cursor.Pos - start;
            uint count = ReadU32(cursor);
            if (count == uint.MaxValue || count > 100_000) return null;
            FamilyFunctionInput? result = null;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                ReadString(cursor);
                int expressionStart = cursor.Pos;
                ReadPtr(cursor, queue);
                bool expression = BitConverter.ToUInt32(data, expressionStart) != 0;
                ReadF64(cursor); ReadI32(cursor);
                int pid = ReadI32(cursor), value = ReadI32(cursor);
                if (!cursor.Take(2)) throw Fail;
                byte instance = data[cursor.Pos - 2], reporting = data[cursor.Pos - 1];
                // The native getter selects the FIRST PID and the distinct +20 integer
                // member. A blocked first record is never replaced by a later duplicate.
                if (pid == -1001006 && !found)
                {
                    found = true;
                    if (!expression && instance == 0 && reporting == 0)
                        result = new(owner, value, i, (int)count, paramsStart, 0);
                }
            }
            // This bounded input currently accepts the exact empty own geometry map only.
            // Nonempty geometry/expressions are not fabricated as a complete API scope.
            return ReadU32(cursor) == 0 && result != null
                ? result with { ParamsEnd = cursor.Pos - start } : null;
        }
        catch (DecodeFail) { return null; }
    }

    /// <summary>AbsDuctType's family depends on AbsCurveType.m_Profile, not dimensions.
    /// Require the own project root and complete deferred graph. Only the circular, oval
    /// and rectangular sweep profile classes are supported here; other runtime shapes cannot borrow their label.</summary>
    private static bool TryReadOwnDuctProfileClass(byte[] data, int start, uint size, ushort classId, out int? profileClass)
    {
        profileClass = null;
        if (!TryExtractOwnElementClass(data, start, size, classId, out _)) return false;
        var fields = SerializationFields(classId);
        if (fields == null || !HasExactSchemaField(fields, 104, "m_Profile", 14, 1, null)) return false;
        var cursor = new Cursor { D = data, Pos = start + 14, End = checked((int)((long)start + 12 + size)), RootClassId = classId };
        var queue = new List<(ushort Cls, uint Id)>();
        int profile = -1;
        try
        {
            foreach (var field in fields)
            {
                int before = queue.Count;
                ReadField(cursor, field, queue, 0, null);
                if (field is { Declaring: 104, Name: "m_Profile" })
                {
                    // A null or external persistent pointer establishes no own profile class.
                    if (queue.Count != before + 1 || queue[before].Cls is not (124 or 126 or 127)) throw Fail;
                    profile = before;
                }
                if (queue.Count > 100_000) throw Fail;
            }
            if (profile < 0) throw Fail;
            for (int index = 0; index < queue.Count; index++)
            {
                if (queue.Count > 100_000) throw Fail;
                ReadDeferredObject(cursor, queue[index].Cls, queue);
            }
            if (cursor.Pos != cursor.End) throw Fail;
            profileClass = queue[profile].Cls;
            return true;
        }
        catch (DecodeFail) { return false; }
        catch (OverflowException) { return false; }
    }

    /// <summary>Reads one named int32/boolean field of a root by walking every root field in schema
    /// order; any decode failure or schema mismatch yields false.</summary>
    private static bool TryReadOwnRootDiscriminator(byte[] data, int start, uint size, ushort classId,
        int declaring, string name, byte type, out int value)
    {
        value = 0;
        var fields = SerializationFields(classId);
        if (type is not (1 or 4) || fields == null
            || !HasExactSchemaField(fields, declaring, name, type, 0, null)) return false;
        var cursor = new Cursor { D = data, Pos = start + 14, End = start + 12 + (int)size, RootClassId = classId };
        var queue = new List<(ushort Cls, uint Id)>();
        bool found = false;
        try
        {
            foreach (var field in fields)
            {
                if (field.Declaring == declaring && field.Name == name)
                {
                    if (type == 1)
                    {
                        if (!cursor.Take(1)) throw Fail;
                        value = data[cursor.Pos - 1];
                        if (value is not (0 or 1)) return false;
                    }
                    else value = ReadI32(cursor);
                    found = true;
                }
                else ReadField(cursor, field, queue, 0, null);
            }
        }
        catch (DecodeFail) { return false; }
        return found;
    }

    internal static string? ExtractRoofOrRailingTypeName(byte[] data, int start, uint size, ushort classId)
        => classId is 908 or 3452 or 3705 && HasExactOwnElementRoot(data, start, size, classId)
            ? ExtractOwnSymbolInfoName(data, start, size, classId) : null;

    internal static string? ExtractStairComponentTypeName(byte[] data, int start, uint size, ushort classId)
        => classId is 3718 or 3697 or 3736 && HasExactOwnElementRoot(data, start, size, classId)
            ? ExtractOwnSymbolInfoName(data, start, size, classId) : null;

    /// <summary>Own SymbolInfo name of a pipe, flex pipe, pipe insulation or cable tray type: the type name Revit reads
    /// with getElementName, where the name table can hold another string.</summary>
    internal static string? ExtractMepTypeName(byte[] data, int start, uint size, ushort classId)
        => classId is 3227 or 3181 or 3217 or 3130 or 3135 && HasExactOwnElementRoot(data, start, size, classId)
            ? ExtractOwnSymbolInfoName(data, start, size, classId) : null;

    internal static string? ExtractDuctTypeName(byte[] data, int start, uint size, ushort classId)
        => classId == 110 && TryExtractSystemTypeRoot(data, start, size, classId, out int? profile)
            && profile is 124 or 126 or 127 ? ExtractOwnSymbolInfoName(data, start, size, classId) : null;

    internal static int? ExtractRoofOrRailingTypeId(byte[] data, int start, uint size, ushort classId)
    {
        if (classId is not (3451 or 3054 or 485) || !HasExactOwnElementRoot(data, start, size, classId)) return null;
        int declaring = classId == 485 ? 485 : 1605;
        string name = classId == 485 ? "m_stairsRailingAttrId" : "m_roofAttributesId";
        var fields = SerializationFields(classId);
        if (fields == null || !HasExactSchemaField(fields, declaring, name, 14, 0, 20)) return null;
        var root = ExtractParams(data, start, size, classId)!;
        return root.Fields.TryGetValue(name, out int typeId)
            && typeId > 0 && typeId != BitConverter.ToInt32(data, start) ? typeId : null;
    }

    private static string? ExtractOwnSymbolInfoName(byte[] data, int start, uint size, ushort classId,
        bool exactFamilySymbol = false)
    {
        if (start < 0 || start > data.Length - 16 || size > int.MaxValue
            || (long)start + 16 + size > data.Length
            || BitConverter.ToUInt32(data, start + 8) != size
            || BitConverter.ToUInt16(data, start + 12) != classId
            || BitConverter.ToUInt32(data, start + 12 + (int)size) != size)
            return null;
        var fields = SerializationFields(classId);
        if (fields == null || !HasExactSchemaField(fields, 37, "m_id", 14, 0, 20)) return null;
        var cursor = new Cursor { D = data, Pos = start + 14, End = start + 12 + (int)size,
            RootClassId = classId, ExactFamilySymbolNameExtraction = exactFamilySymbol };
        var queue = new List<(ushort Cls, uint Id)>();
        int symbolIndex = -1;
        int? owner = null;
        try
        {
            foreach (var field in fields)
            {
                if (field.Declaring == 37 && field.Name == "m_id")
                {
                    owner = ReadI32(cursor);
                    continue;
                }
                int before = queue.Count;
                ReadField(cursor, field, queue, 0, null);
                if (field.Declaring == 106 && field.Name == "m_symbolInfo"
                    && field.Type == 14 && field.Mods == 2
                    && queue.Count == before + 1 && queue[before].Cls == 3905)
                    symbolIndex = before;
            }
            if (symbolIndex < 0 || owner is not > 0 || owner != BitConverter.ToInt32(data, start)) return null;
            for (int index = 0; index < symbolIndex; index++)
                ReadDeferredObject(cursor, queue[index].Cls, queue);
            var symbolFields = SerializationFields(3905);
            if (symbolFields is not { Count: 1 }) return null;
            var nameField = symbolFields[0];
            if (nameField.Declaring != 3905 || nameField.Name != "m_name"
                || nameField.Type != 8 || nameField.Mods != 96)
                return null;
            uint length = ReadU32(cursor);
            if (length == 0 || length > 10_000 || cursor.Pos + length * 2 > cursor.End)
                return null;
            string name = new System.Text.UnicodeEncoding(false, false, true)
                .GetString(data, cursor.Pos, (int)length * 2);
            return string.IsNullOrWhiteSpace(name) || name.Contains('\0') ? null : name;
        }
        catch (DecodeFail) { return null; }
        catch (System.Text.DecoderFallbackException) { return null; }
    }

    /// <summary>Decode the exact planar cut profiles stored by InsertableObj.m_pCutLoops.
    /// The corresponding m_cutDirs and m_cutIndices fields are read from the same root
    /// object; only fully decoded line loops are returned. Curved or incomplete profiles
    /// deliberately stay on the existing BRep/reveal path.</summary>
    public static List<FamilyCutLoopData>? ExtractFamilySymbolCutLoops(
        byte[] d, int blockStart, uint dataSize, ushort classId)
        => ExtractFamilySymbolCutLoops(d, blockStart, dataSize, classId, out _);

    /// <summary>As <see cref="ExtractFamilySymbolCutLoops(byte[], int, uint, ushort)"/>, and
    /// also the number of InsertableObj.m_pCutLoops entries, read with InsertableObj's own
    /// fields whether or not the loops' curves or the later FamilySymbol fields decode (null
    /// when InsertableObj's fields do not read exactly).</summary>
    public static List<FamilyCutLoopData>? ExtractFamilySymbolCutLoops(
        byte[] d, int blockStart, uint dataSize, ushort classId, out int? rootCutLoopCount)
        => ExtractFamilySymbolCutLoopsCore(d, blockStart, dataSize, classId, false, out rootCutLoopCount);

    // Diagnostic input only: activating legacy IFC loop consumers regresses void/fill relations.
    internal static List<FamilyCutLoopData>? ExtractOwnFamilySymbolCutLoops(
        byte[] d, int blockStart, uint dataSize, ushort classId, out int? rootCutLoopCount)
        => ExtractFamilySymbolCutLoopsCore(d, blockStart, dataSize, classId, true, out rootCutLoopCount);

    private static List<FamilyCutLoopData>? ExtractFamilySymbolCutLoopsCore(
        byte[] d, int blockStart, uint dataSize, ushort classId, bool exact, out int? rootCutLoopCount)
    {
        rootCutLoopCount = null;
        if (classId != 1829) return null;
        if (exact && !TryExtractOwnElementClass(d, blockStart, dataSize, classId, out _)) return null;
        var fields = SerializationFields(classId);
        if (fields == null) return null;
        int payloadStart = blockStart + 14;
        int payloadEnd = blockStart + 16 + (int)dataSize;
        if (payloadEnd > d.Length || payloadEnd - payloadStart < 6
            || BitConverter.ToUInt32(d, payloadEnd - 4) != dataSize)
            return null;

        var cur = new Cursor
        {
            D = d,
            Pos = payloadStart,
            End = payloadEnd - 4,
            ExactFamilyCutInputs = exact,
            FamilyCutLoopQueueIndices = new(),
            FamilyCutDirections = new(),
            FamilyCutIndices = new(),
        };
        var queue = new List<(ushort Cls, uint Id)>();
        var curveOwnerByQueueIndex = new Dictionary<int, int>();
        var lineCountByLoop = new Dictionary<int, int>();
        var curvesProcessedByLoop = new Dictionary<int, int>();
        var linesByLoop = new Dictionary<int, List<(double[] Start, double[] End)>>();
        var unsupportedLoops = new HashSet<int>();
        bool rootComplete = false;
        int deferredPayloadStart = 0;

        try
        {
            foreach (var field in fields)
            {
                ReadField(cur, field, queue, 0, null);
                if (exact && queue.Count > 100_000) throw Fail;
                // InsertableObj's fields come first; the count stays exact when a later
                // FamilySymbol field (m_pretaggingGNodes) does not decode.
                if (field.Declaring == 1830 && field.Name == "m_cutIndices")
                    rootCutLoopCount = cur.FamilyCutLoopQueueIndices.Count;
            }
            if (cur.FamilyCutLoopQueueIndices.Count == 0
                || cur.FamilyCutDirections.Count == 0
                || cur.FamilyCutDirections.Count != cur.FamilyCutIndices.Count)
                return null;
            rootComplete = true;
            deferredPayloadStart = cur.Pos;

            var loopOrdinalByQueueIndex = cur.FamilyCutLoopQueueIndices
                .Select((queueIndex, ordinal) => (queueIndex, ordinal))
                .ToDictionary(pair => pair.queueIndex, pair => pair.ordinal);

            for (int queueIndex = 0; queueIndex < queue.Count; queueIndex++)
            {
                if (exact && queue.Count > 100_000) throw Fail;
                var (queuedClass, _) = queue[queueIndex];
                if (ParamSetTags.TryGetValue(queuedClass, out var parameterStore))
                {
                    uint count = ReadU32(cur);
                    if (count > 100_000) throw Fail;
                    var recordFields = SerializationFields(parameterStore.RecordClass)
                        ?? throw Fail;
                    for (uint index = 0; index < count; index++)
                        foreach (var recordField in recordFields)
                            ReadField(cur, recordField, new List<(ushort, uint)>(), 0, null);
                    continue;
                }

                if (loopOrdinalByQueueIndex.TryGetValue(queueIndex, out int loopOrdinal))
                {
                    if (queuedClass != 938 || cur.Pos >= cur.End) throw Fail;
                    bool open = cur.D[cur.Pos++] != 0;
                    uint curveCount = ReadU32(cur);
                    if (open || curveCount < 3 || curveCount > 100_000) throw Fail;
                    lineCountByLoop[loopOrdinal] = (int)curveCount;
                    for (uint curveIndex = 0; curveIndex < curveCount; curveIndex++)
                    {
                        int before = queue.Count;
                        ReadPtr(cur, queue);
                        if (queue.Count != before + 1) throw Fail;
                        curveOwnerByQueueIndex[before] = loopOrdinal;
                        if (queue[before].Cls != 1736 && queue[before].Cls != 1974)
                            unsupportedLoops.Add(loopOrdinal);
                    }
                    continue;
                }

                var queuedFields = SerializationFields(queuedClass) ?? throw Fail;
                bool ownedCurve = curveOwnerByQueueIndex.TryGetValue(
                    queueIndex, out int ownerLoop);
                bool line = ownedCurve && queuedClass == 1736;
                bool arc = ownedCurve && queuedClass == 1974;
                if (ownedCurve && !line && !arc) unsupportedLoops.Add(ownerLoop);
                if (line || arc)
                {
                    cur.Ep0 = cur.Ep1 = 0;
                    cur.Radius = 0;
                    cur.FamilyLineCapture = new Dictionary<string, double[]>();
                }
                foreach (var queuedField in queuedFields)
                    ReadField(cur, queuedField, queue, 1, null);
                if (!line && !arc) continue;

                if (line)
                {
                    if (!cur.FamilyLineCapture!.TryGetValue("m_origin", out var origin)
                        || !cur.FamilyLineCapture.TryGetValue("m_dirVec", out var direction)
                        || origin.Length != 3 || direction.Length != 3
                        || origin.Concat(direction).Any(value => !double.IsFinite(value))
                        || !double.IsFinite(cur.Ep0) || !double.IsFinite(cur.Ep1))
                        throw Fail;
                    double[] start =
                    [
                        origin[0] + direction[0] * cur.Ep0,
                        origin[1] + direction[1] * cur.Ep0,
                        origin[2] + direction[2] * cur.Ep0,
                    ];
                    double[] end =
                    [
                        origin[0] + direction[0] * cur.Ep1,
                        origin[1] + direction[1] * cur.Ep1,
                        origin[2] + direction[2] * cur.Ep1,
                    ];
                    if (exact && start.Concat(end).Any(value => !double.IsFinite(value))) throw Fail;
                    if (!linesByLoop.TryGetValue(ownerLoop, out var lines))
                        linesByLoop[ownerLoop] = lines = new();
                    lines.Add((start, end));
                    curvesProcessedByLoop[ownerLoop] = curvesProcessedByLoop.GetValueOrDefault(ownerLoop) + 1;
                    cur.FamilyLineCapture = null;
                }
                else if (arc)
                {
                    if (!cur.FamilyLineCapture!.TryGetValue("m_center", out var center)
                        || !cur.FamilyLineCapture.TryGetValue("m_xVec", out var xVec)
                        || !cur.FamilyLineCapture.TryGetValue("m_yVec", out var yVec)
                        || center.Length != 3 || xVec.Length != 3 || yVec.Length != 3
                        || center.Concat(xVec).Concat(yVec).Any(value => !double.IsFinite(value))
                        || !double.IsFinite(cur.Radius) || cur.Radius <= 0
                        || !double.IsFinite(cur.Ep0) || !double.IsFinite(cur.Ep1))
                        throw Fail;

                    double xLen = Math.Sqrt(xVec[0] * xVec[0] + xVec[1] * xVec[1] + xVec[2] * xVec[2]);
                    double yLen = Math.Sqrt(yVec[0] * yVec[0] + yVec[1] * yVec[1] + yVec[2] * yVec[2]);
                    if (xLen > 1e-12) xVec = [xVec[0] / xLen, xVec[1] / xLen, xVec[2] / xLen];
                    if (yLen > 1e-12) yVec = [yVec[0] / yLen, yVec[1] / yLen, yVec[2] / yLen];

                    double sweep = cur.Ep1 - cur.Ep0;
                    double span = Math.Abs(sweep);
                    if (span < 1e-12) span = 2 * Math.PI;
                    int n = Math.Max(1, (int)Math.Ceiling(span / (Math.PI / 16.0) - 1e-9));

                    double[] PtAt(double t)
                    {
                        double c = Math.Cos(t), s = Math.Sin(t);
                        return
                        [
                            center[0] + cur.Radius * (c * xVec[0] + s * yVec[0]),
                            center[1] + cur.Radius * (c * xVec[1] + s * yVec[1]),
                            center[2] + cur.Radius * (c * xVec[2] + s * yVec[2]),
                        ];
                    }

                    if (!linesByLoop.TryGetValue(ownerLoop, out var lines))
                        linesByLoop[ownerLoop] = lines = new();

                    double[] prev = PtAt(cur.Ep0);
                    for (int k = 1; k <= n; k++)
                    {
                        double t = cur.Ep0 + sweep * k / n;
                        double[] next = PtAt(t);
                        if (exact && prev.Concat(next).Any(value => !double.IsFinite(value))) throw Fail;
                        lines.Add((prev, next));
                        prev = next;
                    }
                    curvesProcessedByLoop[ownerLoop] = curvesProcessedByLoop.GetValueOrDefault(ownerLoop) + 1;
                    cur.FamilyLineCapture = null;
                }
            }
            if (exact && cur.Pos != cur.End) return null;
        }
        catch (DecodeFail)
        {
            if (exact || !rootComplete || lineCountByLoop.Count == 0
                || unsupportedLoops.Count > 0
                || !TryRecoverSerializedLineLoops(cur.D, deferredPayloadStart, cur.End,
                    lineCountByLoop, out linesByLoop))
                return null;
        }

        if (cur.FamilyCutIndices.Count != cur.FamilyCutLoopQueueIndices.Count)
            return null;
        var result = new List<FamilyCutLoopData>();
        var usedLoopIndices = new HashSet<int>();
        for (int directionIndex = 0; directionIndex < cur.FamilyCutDirections.Count;
             directionIndex++)
        {
            // Native InsertableObj.getCut indexes all three arrays by the requested
            // ordinal; m_cutIndices is a host-reference index, not a loop permutation.
            int loopIndex = exact ? directionIndex : cur.FamilyCutIndices[directionIndex];
            if (loopIndex < 0 || loopIndex >= cur.FamilyCutLoopQueueIndices.Count
                || !usedLoopIndices.Add(loopIndex) || unsupportedLoops.Contains(loopIndex)
                || !linesByLoop.TryGetValue(loopIndex, out var lines)
                || !lineCountByLoop.TryGetValue(loopIndex, out int expectedLineCount)
                || !curvesProcessedByLoop.TryGetValue(loopIndex, out int processedCount)
                || processedCount != expectedLineCount
                || lines.Count < expectedLineCount
                || !TryOrderClosedLineLoop(lines, out var vertices))
                return null;
            result.Add(new FamilyCutLoopData(
                vertices, cur.FamilyCutDirections[directionIndex],
                exact ? cur.FamilyCutIndices[directionIndex] : loopIndex));
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>Decode the exact elements recorded as owners of a host's cut-out
    /// geometry steps. The result comes exclusively from GeomStepList phase arrays and
    /// GeomStep.m_pElem back-references; a null result means that the serialized queue
    /// could not be decoded completely up to the relevant steps.</summary>
    public static List<uint>? ExtractCuttingElementIds(
        byte[] d, int blockStart, uint dataSize, ushort classId)
        => ExtractCuttingElementIdsCore(
            d, blockStart, dataSize, classId, collectDiagnostic: false, out _);

    internal static List<uint>? ExtractCuttingElementIds(
        byte[] d, int blockStart, uint dataSize, ushort classId,
        out string? diagnostic)
        => ExtractCuttingElementIdsCore(
            d, blockStart, dataSize, classId, collectDiagnostic: true, out diagnostic);

    /// <summary>The cutters of <see cref="ExtractCuttingElementIds(byte[], int, uint, ushort)"/> plus every
    /// element the cut-out phases name, in step order: the cutter of each WallCutoutGStepBase
    /// (<c>Cutout</c>, m_instId) and the other host of each PairBaseGStep (CutPairGStep, CleanPairGStep, ...,
    /// m_otherId). Every tracked step must decode; otherwise null.</summary>
    public static List<uint>? ExtractCuttingElementIdsAndSteps(
        byte[] d, int blockStart, uint dataSize, ushort classId,
        out List<(ushort ClassId, int ElementId, bool Cutout)>? steps)
    {
        steps = new List<(ushort ClassId, int ElementId, bool Cutout)>();
        var cutters = ExtractCuttingElementIdsCore(
            d, blockStart, dataSize, classId, collectDiagnostic: false, out _, steps);
        if (cutters == null) steps = null;
        return cutters;
    }

    /// <summary>The steps of <see cref="ExtractCuttingElementIdsAndSteps"/> for a block it cannot decode. A step that is
    /// not a cut-out one is first read with the counted history layout; the cut-out steps are read with the exact
    /// layouts (counted, compact or zero-id terminated), and the older steps of a wall persist either. When a reading
    /// fails, the steps before the failure are read again, one at a time and then in combinations, with the exact
    /// layouts (breadth first, a bounded number of attempts); the first reading that decodes every cut-out step wins.
    /// The caller validates the result against the host's references.</summary>
    public static List<(ushort ClassId, int ElementId, bool Cutout)>? ExtractCutOutStepsWithLayoutSearch(
        byte[] d, int blockStart, uint dataSize, ushort classId)
    {
        var pending = new Queue<(int[] Mode, int LastFlipped)>();
        pending.Enqueue(([], -1));
        for (int attempt = 0; attempt < MaxStepLayoutAttempts && pending.Count > 0; attempt++)
        {
            var (mode, lastFlipped) = pending.Dequeue();
            var search = new StepLayoutSearch { Mode = mode };
            var steps = new List<(ushort ClassId, int ElementId, bool Cutout)>();
            if (ExtractCuttingElementIdsCore(
                    d, blockStart, dataSize, classId, collectDiagnostic: false, out _, steps, search) != null)
                return steps;
            for (int step = lastFlipped + 1; step <= search.FailedOrdinal; step++)
            {
                // VerticalExtensionOfLayersGStep (4098) has a second tail layout; the other steps only the history one.
                foreach (int flip in StepLayoutFlips(search.ClassAt[step]))
                {
                    var next = new int[step + 1];
                    Array.Copy(mode, next, Math.Min(mode.Length, next.Length));
                    next[step] = flip;
                    pending.Enqueue((next, step));
                }
            }
        }
        return null;
    }

    /// <summary>The layouts a step of this class is tried with after the default: exact history tables (bit 0), counted
    /// m_otherId containers of a VerticalExtensionOfLayersGStep (bit 1), counted history of a JoinEndGStep (bit 2).</summary>
    private static int[] StepLayoutFlips(ushort stepClass) => stepClass switch
    {
        4098 => [1, 2, 3],
        2256 => [4, 8, 12],
        _ => [1],
    };

    private const int MaxStepLayoutAttempts = 96;

    /// <summary>Which of the not cut-out steps of the GeomStepList (in queue order) read their history tables with the
    /// exact layouts, and where the reading failed.</summary>
    private sealed class StepLayoutSearch
    {
        /// <summary>Per step: bit 0 = history tables in the exact layouts, bit 1 = counted m_otherId containers (4098).</summary>
        public int[] Mode = [];
        public int Ordinal;
        public int Current = -1;
        public int FailedOrdinal = -1;
        public List<ushort> ClassAt = new();
    }

    private static List<uint>? ExtractCuttingElementIdsCore(
        byte[] d, int blockStart, uint dataSize, ushort classId,
        bool collectDiagnostic, out string? diagnostic,
        List<(ushort ClassId, int ElementId, bool Cutout)>? steps = null,
        StepLayoutSearch? search = null)
    {
        diagnostic = null;
        if (!IsSubclassOf(classId, 363))
        {
            diagnostic = $"class {classId} is not a HostObj";
            return null;
        }
        var fields = SerializationFields(classId);
        if (fields == null)
        {
            diagnostic = $"schema {classId} is unavailable";
            return null;
        }
        int payloadStart = blockStart + 14;
        int payloadEnd = blockStart + 16 + (int)dataSize;
        if (payloadEnd > d.Length || payloadEnd - payloadStart < 6
            || BitConverter.ToUInt32(d, payloadEnd - 4) != dataSize)
        {
            diagnostic = "invalid block bounds or footer";
            return null;
        }

        var cur = new Cursor
        {
            D = d,
            Pos = payloadStart,
            End = payloadEnd - 4,
            CutOutGeomStepQueueIndices = new(),
            GeomStepOwnerElementIds = new(),
            GeomStepCuttingElementIds = new(),
            GeomStepPairOtherIds = steps != null ? new() : null,
            CutTopologyTrace = collectDiagnostic ? new() : null,
            LayoutSearch = search != null,
        };
        var queue = new List<(ushort Cls, uint Id)>();
        bool sawGeomStepList = false;
        string context = "root";
        int diagnosticQueueIndex = -1;

        try
        {
            foreach (var field in fields)
            {
                if (collectDiagnostic)
                    context = $"root {field.Declaring}.{field.Name}";
                ReadField(cur, field, queue, 0, null);
            }

            for (int queueIndex = 0; queueIndex < queue.Count; queueIndex++)
            {
                diagnosticQueueIndex = queueIndex;
                var (queuedClass, queuedId) = queue[queueIndex];
                cur.CurrentDeferredQueueIndex = queueIndex;
                cur.CurrentDeferredClassId = queuedClass;
                cur.CurrentJoinEndHasReverseHistory = false;
                cur.CurrentTrackedReverseHistoryPresent = false;
                cur.CurrentTrackedReverseHistoryEnd = -1;
                cur.CurrentTrackedCurveRecordSize = 20;
                if (collectDiagnostic)
                    context = $"queue[{queueIndex}] class {queuedClass}";
                if (ParamSetTags.TryGetValue(queuedClass, out var parameterStore))
                {
                    uint count = ReadU32(cur);
                    if (count > 100_000) throw Fail;
                    var recordFields = SerializationFields(parameterStore.RecordClass)
                        ?? throw Fail;
                    for (uint index = 0; index < count; index++)
                        foreach (var recordField in recordFields)
                        {
                            if (collectDiagnostic)
                                context = $"queue[{queueIndex}] param[{index}] "
                                    + $"{recordField.Declaring}.{recordField.Name}";
                            ReadField(cur, recordField, new List<(ushort, uint)>(), 0, null);
                        }
                    continue;
                }

                var queuedFields = SerializationFields(queuedClass) ?? throw Fail;
                if (queuedClass == 2086)
                {
                    sawGeomStepList = true;
                    foreach (var queuedField in queuedFields)
                    {
                        if (collectDiagnostic)
                            context = $"queue[{queueIndex}] class {queuedClass} "
                                + $"{queuedField.Declaring}.{queuedField.Name}";
                        int phaseQueueStart = queue.Count;
                        if (queuedField.Declaring == 2086
                            && queuedField.Name is "m_bRepCutOutGList"
                                or "m_bRepPostCutOutGList")
                        {
                            ReadTrackedPointerContainer(cur, queue,
                                cur.CutOutGeomStepQueueIndices);
                        }
                        else
                        {
                            ReadField(cur, queuedField, queue, 1, null);
                        }
                        if (queuedField.Declaring == 2086 && queue.Count > phaseQueueStart)
                            cur.CutTopologyTrace?.Add($"phase={queuedField.Name}:"
                                + string.Join(",", Enumerable.Range(phaseQueueStart,
                                        queue.Count - phaseQueueStart)
                                    .Select(index =>
                                        $"{index}:{queue[index].Cls}:{queue[index].Id}")));
                    }

                    // A cut-out phase containing only PairBase operations represents
                    // host-to-host joins. It is authoritative for opening topology too:
                    // without a WallCutoutGStepBase there is no inserted-element cutter,
                    // so falling back to the host's broad dependency list would invent
                    // void relationships for unrelated references.
                    if (cur.CutOutGeomStepQueueIndices.Count == 0
                        || steps == null && !cur.CutOutGeomStepQueueIndices.Any(index =>
                            IsSubclassOf(queue[index].Cls, 1793)))
                    {
                        if (collectDiagnostic)
                            diagnostic = string.Join(" | ", cur.CutTopologyTrace ?? []);
                        return new List<uint>();
                    }
                    cur.CutTopologyTrace?.Add("cut-indices="
                        + string.Join(",", cur.CutOutGeomStepQueueIndices.OrderBy(index => index)
                            .Select(index => $"{index}:{queue[index].Cls}:{queue[index].Id}")));
                    continue;
                }

                bool trackedStep = cur.CutOutGeomStepQueueIndices.Contains(queueIndex);
                cur.CutTopologyTrace?.Add($"q{queueIndex}:{queuedClass}:{queuedId}:"
                    + $"tracked={trackedStep}");
                bool isGeomStep = sawGeomStepList && IsSubclassOf(queuedClass, 96);
                bool exactHistory = false;
                cur.CountedOtherIds = false;
                cur.CountedHistory = false;
                cur.JoinEndFullTail = false;
                if (search != null)
                {
                    search.Current = -1;
                    if (isGeomStep && !trackedStep)
                    {
                        search.Current = search.Ordinal++;
                        search.ClassAt.Add(queuedClass);
                        int mode = search.Current < search.Mode.Length ? search.Mode[search.Current] : 0;
                        exactHistory = (mode & 1) != 0;
                        cur.CountedOtherIds = (mode & 2) != 0;
                        cur.CountedHistory = (mode & 4) != 0;
                        cur.JoinEndFullTail = (mode & 8) != 0;
                    }
                }
                cur.CurrentGeomStepQueueIndex = trackedStep || exactHistory ? queueIndex : -1;
                foreach (var queuedField in queuedFields)
                {
                    if (collectDiagnostic)
                        context = $"queue[{queueIndex}] class {queuedClass} "
                            + $"{queuedField.Declaring}.{queuedField.Name}";
                    int fieldStart = cur.Pos;
                    ReadField(cur, queuedField, queue, 1, null);
                    if (queuedClass is 2256 or 4098)
                        cur.CutTopologyTrace?.Add($"q{queueIndex}:{queuedClass}:"
                            + $"{queuedField.Declaring}.{queuedField.Name}"
                            + $"@{fieldStart:X}->{cur.Pos:X}");
                }
                cur.CurrentGeomStepQueueIndex = -1;
                cur.CutTopologyTrace?.Add($"decoded={cur.GeomStepOwnerElementIds.Count}/"
                    + $"{cur.CutOutGeomStepQueueIndices.Count};cutters="
                    + cur.GeomStepCuttingElementIds.Count);

                if (steps != null)
                {
                    // Step mode: every tracked step is decoded, then the steps are listed.
                    var tracked = cur.CutOutGeomStepQueueIndices.OrderBy(index => index).ToList();
                    if (sawGeomStepList && tracked.All(index =>
                            cur.GeomStepOwnerElementIds.ContainsKey(index)
                            && (!IsSubclassOf(queue[index].Cls, 1793)
                                || cur.GeomStepCuttingElementIds.ContainsKey(index))
                            && (!IsSubclassOf(queue[index].Cls, 678)
                                || cur.GeomStepPairOtherIds!.ContainsKey(index))))
                    {
                        foreach (int index in tracked)
                        {
                            bool cutout = IsSubclassOf(queue[index].Cls, 1793);
                            if (cutout || IsSubclassOf(queue[index].Cls, 678))
                                steps.Add((queue[index].Cls, cutout
                                    ? cur.GeomStepCuttingElementIds[index] : cur.GeomStepPairOtherIds![index], cutout));
                        }
                        return tracked.Where(index => IsSubclassOf(queue[index].Cls, 1793))
                            .Select(index => cur.GeomStepCuttingElementIds[index])
                            .Where(elementId => elementId > 0)
                            .Select(elementId => (uint)elementId)
                            .Distinct()
                            .ToList();
                    }
                    continue;
                }

                // Only WallCutoutGStepBase.m_instId identifies an inserted element that
                // authors an opening. CutPairGStep is also stored in the cut-out phase,
                // but PairBaseGStep.m_otherId is the other host participating in a
                // wall/solid join and must not be indexed as a hosted cutter.
                var cutOperationIndices = cur.CutOutGeomStepQueueIndices
                    .Where(index => IsSubclassOf(queue[index].Cls, 1793))
                    .OrderBy(index => index)
                    .ToList();
                if (sawGeomStepList && cutOperationIndices.Count > 0
                    && cutOperationIndices.All(index =>
                        cur.GeomStepOwnerElementIds.ContainsKey(index)
                        && cur.GeomStepCuttingElementIds.ContainsKey(index)))
                {
                    var result = cutOperationIndices
                        .Select(index => cur.GeomStepCuttingElementIds[index])
                        .Where(elementId => elementId > 0)
                        .Select(elementId => (uint)elementId)
                        .Distinct()
                        .ToList();
                    if (collectDiagnostic)
                        diagnostic = string.Join(" | ", cur.CutTopologyTrace ?? []);
                    return result;
                }
            }
        }
        catch (DecodeFail)
        {
            // A failure inside a cut-out step (read with the exact layouts anyway) points at the steps read before it.
            if (search != null) search.FailedOrdinal = search.Current >= 0 ? search.Current : search.Ordinal - 1;
            if (!collectDiagnostic) return null;
            int byteStart = Math.Max(payloadStart, cur.Pos - 16);
            int byteCount = Math.Min(32, Math.Max(0, cur.End - byteStart));
            string bytes = Convert.ToHexString(d, byteStart, byteCount);
            string queueTail = string.Join(",",
                queue.TakeWhile((_, index) => index <= diagnosticQueueIndex)
                    .TakeLast(6).Select(item => item.Cls));
            string trace = string.Join(" | ", cur.CutTopologyTrace?.TakeLast(120)
                ?? Enumerable.Empty<string>());
            diagnostic = $"{context} at payload offset 0x{cur.Pos - payloadStart:X} "
                + $"of 0x{cur.End - payloadStart:X}; queue tail [{queueTail}]; "
                + $"bytes@0x{byteStart - payloadStart:X}={bytes}; trace {trace}";
            return null;
        }

        if (sawGeomStepList && cur.CutOutGeomStepQueueIndices.Count == 0)
            return new List<uint>();
        diagnostic = sawGeomStepList
            ? "deferred queue ended before every cut-out step was decoded"
            : "GeomStepList was not present in the deferred queue";
        return null;
    }

    /// <summary>One joined neighbour of a wall end: a record of the wall driver's
    /// m_controlJoinsSet, <c>pair&lt;int end, CDJoinInfo&gt;</c>, whose m_info lists
    /// <c>EndInfo(m_elemId, m_joinedEnd, m_bTangent)</c>.</summary>
    internal readonly record struct WallEndJoin(int End, int ElementId, int JoinedEnd, int JoinType);

    /// <summary>The control joins of a wall's own driver, read from the wall block. The root
    /// and the deferred queue are walked in order as in <c>ExtractWallJoinedToTop</c>:
    /// the single VWallDriver (class 4040) records CurveDriver.m_controlJoinsSet - per entry
    /// the EndInfo records of the inline CDJoinInfo, its m_wallJoinType and the wall end it
    /// belongs to. Every object queued after it up to the first geometry step must decode,
    /// and that step must be read with its exact layout (<see cref="TryReadExactWallGeomStep"/>):
    /// its history tables, version and back-reference land in place only if the driver was
    /// read in alignment. Null otherwise.</summary>
    internal static List<WallEndJoin>? ExtractWallEndJoins(
        byte[] d, int blockStart, uint dataSize, ushort classId, out string? failure)
    {
        failure = "framing";
        if (classId is not (3524 or 361)
            || blockStart < 0
            || dataSize > int.MaxValue
            || (long)blockStart + 16 + dataSize > d.Length
            || dataSize < 6
            || BitConverter.ToUInt32(d, blockStart + 8) != dataSize
            || BitConverter.ToUInt16(d, blockStart + 12) != classId)
            return null;
        var fields = SerializationFields(classId);
        if (fields == null) return null;
        int payloadStart = blockStart + 14;
        int payloadEnd = blockStart + 16 + (int)dataSize;
        if (BitConverter.ToUInt32(d, payloadEnd - 4) != dataSize) return null;

        var cur = new Cursor
        {
            D = d,
            Pos = payloadStart,
            End = payloadEnd - 4,
            CutOutGeomStepQueueIndices = new(),
        };
        var queue = new List<(ushort Cls, uint Id)>();
        HashSet<int>? stepIndices = null;
        var decodedSteps = new HashSet<int>();
        uint? owner = null;
        List<WallEndJoin>? joins = null;
        try
        {
            failure = "root";
            foreach (var field in fields)
                ReadField(cur, field, queue, 0, null);

            for (int queueIndex = 0; queueIndex < queue.Count; queueIndex++)
            {
                ushort queuedClass = queue[queueIndex].Cls;
                cur.CurrentDeferredQueueIndex = queueIndex;
                cur.CurrentDeferredClassId = queuedClass;
                cur.CurrentJoinEndHasReverseHistory = false;
                cur.CurrentTrackedReverseHistoryPresent = false;
                cur.CurrentTrackedReverseHistoryEnd = -1;
                cur.CurrentTrackedCurveRecordSize = 20;
                if (ParamSetTags.TryGetValue(queuedClass, out var parameterStore))
                {
                    failure = "parameters";
                    uint count = ReadU32(cur);
                    if (count > 100_000) return null;
                    var recordFields = SerializationFields(parameterStore.RecordClass);
                    if (recordFields == null) return null;
                    for (uint index = 0; index < count; index++)
                        foreach (var recordField in recordFields)
                            ReadField(cur, recordField, new List<(ushort, uint)>(), 0, null);
                    continue;
                }

                if (queuedClass == 2086)
                {
                    failure = "step list";
                    if (stepIndices != null) return null;
                    stepIndices = new HashSet<int>();
                    foreach (var listField in SerializationFields(2086) ?? throw Fail)
                    {
                        int before = queue.Count;
                        ReadField(cur, listField, queue, 1, null);
                        if (listField.Declaring != 2086
                            || listField.Name is not ("m_nonBRepGList" or "m_bRepFormGList"
                                or "m_bRepAdjustGList" or "m_bRepCutOutGList"
                                or "m_bRepPostCutOutGList" or "m_bRepTweakGList"))
                            continue;
                        for (int index = before; index < queue.Count; index++)
                        {
                            if (!IsSubclassOf(queue[index].Cls, 96)) return null;
                            stepIndices.Add(index);
                        }
                    }
                    continue;
                }

                if (IsSubclassOf(queuedClass, 96))
                {
                    failure = $"step {queuedClass}";
                    if (!TryReadExactWallGeomStep(cur, queuedClass,
                            out uint stepOwner, out _, out string? stepFailure))
                    {
                        failure = $"step {queuedClass} {stepFailure}";
                        return null;
                    }
                    if (owner is uint expected && expected != stepOwner)
                    {
                        failure = "owner";
                        return null;
                    }
                    owner = stepOwner;
                    decodedSteps.Add(queueIndex);
                }
                else
                {
                    failure = $"object {queuedClass}";
                    if (queuedClass == 4040)
                    {
                        if (joins != null) return null;
                        joins = cur.ControlJoins = new List<WallEndJoin>();
                    }
                    foreach (var queuedField in SerializationFields(queuedClass) ?? throw Fail)
                        ReadField(cur, queuedField, queue, 1, null);
                    cur.ControlJoins = null;
                }

                if (joins != null && decodedSteps.Count > 0)
                {
                    failure = null;
                    return joins;
                }
            }
            failure = joins == null ? "driver not reached" : "no step after the driver";
        }
        catch (DecodeFail) { return null; }
        return null;
    }

    /// <summary>Decides from a wall's own block whether it carries a top attachment, the
    /// serialized form of Revit's "Attach Top": a JoinToRoofGStep (class 2262) with
    /// <c>m_baseOrTop = 1</c> and at least one target id. The deferred queue is walked in
    /// order; every geometry step listed by the wall's GeomStepList is read with the exact
    /// per-class layout of <see cref="TryReadExactWallGeomStep"/> (any m_version of a class it reads), and all of them must share
    /// the wall's own back-reference. Returns null when the block, a step class or version,
    /// or any earlier deferred object cannot be read exactly; true/false otherwise.</summary>
    internal static bool? ExtractWallJoinedToTop(
        byte[] d, int blockStart, uint dataSize, ushort classId)
        => ExtractWallJoinedToTop(d, blockStart, dataSize, classId, out _);

    /// <inheritdoc cref="ExtractWallJoinedToTop(byte[], int, uint, ushort)"/>
    /// <param name="failure">Why no value was produced, for diagnostics.</param>
    internal static bool? ExtractWallJoinedToTop(
        byte[] d, int blockStart, uint dataSize, ushort classId, out string? failure)
    {
        failure = "framing";
        if (classId is not (3524 or 361)
            || blockStart < 0
            || dataSize > int.MaxValue
            || (long)blockStart + 16 + dataSize > d.Length
            || dataSize < 6
            || BitConverter.ToUInt32(d, blockStart + 8) != dataSize
            || BitConverter.ToUInt16(d, blockStart + 12) != classId)
            return null;
        var fields = SerializationFields(classId);
        if (fields == null) return null;
        int payloadStart = blockStart + 14;
        int payloadEnd = blockStart + 16 + (int)dataSize;
        if (BitConverter.ToUInt32(d, payloadEnd - 4) != dataSize) return null;

        var cur = new Cursor
        {
            D = d,
            Pos = payloadStart,
            End = payloadEnd - 4,
            CutOutGeomStepQueueIndices = new(),
        };
        var queue = new List<(ushort Cls, uint Id)>();
        HashSet<int>? stepIndices = null;
        var decodedSteps = new HashSet<int>();
        uint? owner = null;
        bool joinedToTop = false;
        try
        {
            failure = "root";
            foreach (var field in fields)
                ReadField(cur, field, queue, 0, null);

            for (int queueIndex = 0; queueIndex < queue.Count; queueIndex++)
            {
                ushort queuedClass = queue[queueIndex].Cls;
                cur.CurrentDeferredQueueIndex = queueIndex;
                cur.CurrentDeferredClassId = queuedClass;
                cur.CurrentJoinEndHasReverseHistory = false;
                cur.CurrentTrackedReverseHistoryPresent = false;
                cur.CurrentTrackedReverseHistoryEnd = -1;
                cur.CurrentTrackedCurveRecordSize = 20;
                if (ParamSetTags.TryGetValue(queuedClass, out var parameterStore))
                {
                    failure = "parameters";
                    uint count = ReadU32(cur);
                    if (count > 100_000) return null;
                    var recordFields = SerializationFields(parameterStore.RecordClass);
                    if (recordFields == null) return null;
                    for (uint index = 0; index < count; index++)
                        foreach (var recordField in recordFields)
                            ReadField(cur, recordField, new List<(ushort, uint)>(), 0, null);
                    continue;
                }

                if (queuedClass == 2086)
                {
                    failure = "step list";
                    if (stepIndices != null) return null;
                    stepIndices = new HashSet<int>();
                    foreach (var listField in SerializationFields(2086) ?? throw Fail)
                    {
                        int before = queue.Count;
                        ReadField(cur, listField, queue, 1, null);
                        if (listField.Declaring != 2086
                            || listField.Name is not ("m_nonBRepGList" or "m_bRepFormGList"
                                or "m_bRepAdjustGList" or "m_bRepCutOutGList"
                                or "m_bRepPostCutOutGList" or "m_bRepTweakGList"))
                            continue;
                        for (int index = before; index < queue.Count; index++)
                        {
                            if (!IsSubclassOf(queue[index].Cls, 96)) return null;
                            stepIndices.Add(index);
                        }
                    }
                    if (stepIndices.Count == 0) { failure = null; return false; }
                    continue;
                }

                if (IsSubclassOf(queuedClass, 96))
                {
                    int stepStart = cur.Pos - blockStart;
                    failure = $"step {queuedClass} @{stepStart}";
                    if (!TryReadExactWallGeomStep(cur, queuedClass,
                            out uint stepOwner, out bool stepJoinsTop, out string? stepFailure, anyVersion: true))
                    {
                        failure = $"step {queuedClass} {stepFailure} @{stepStart}";
                        return null;
                    }
                    if (owner is uint expected && expected != stepOwner)
                    {
                        failure = "owner";
                        return null;
                    }
                    owner = stepOwner;
                    joinedToTop |= stepJoinsTop;
                    decodedSteps.Add(queueIndex);
                    if (stepIndices != null && stepIndices.IsSubsetOf(decodedSteps))
                    {
                        failure = null;
                        return joinedToTop;
                    }
                    continue;
                }

                failure = $"object {queuedClass}";
                foreach (var queuedField in SerializationFields(queuedClass) ?? throw Fail)
                    ReadField(cur, queuedField, queue, 1, null);
            }
            failure = "steps not reached";
        }
        catch (DecodeFail) { return null; }
        return null;
    }

    /// <summary>Reads a material's own name: the MaterialElem root (class 2486) owns exactly
    /// one deferred Material (class 2479) through m_pMaterial, and Material.m_name is that
    /// object's first serialized field. The root and every deferred object queued before the
    /// Material are walked in order; the root's m_materialPathMap is read as its persisted
    /// map&lt;int, pair&lt;AString, int&gt;&gt;. Any failure yields null.</summary>
    internal static string? ExtractMaterialName(
        byte[] d, int blockStart, uint dataSize, ushort classId)
        => ExtractMaterialName(d, blockStart, dataSize, classId, out _);

    /// <inheritdoc cref="ExtractMaterialName(byte[], int, uint, ushort)"/>
    /// <param name="failure">Why no name was produced, for diagnostics.</param>
    internal static string? ExtractMaterialName(
        byte[] d, int blockStart, uint dataSize, ushort classId, out string? failure)
    {
        failure = "framing";
        if (classId != 2486
            || blockStart < 0
            || dataSize > int.MaxValue
            || (long)blockStart + 16 + dataSize > d.Length
            || dataSize < 6
            || BitConverter.ToUInt32(d, blockStart + 8) != dataSize
            || BitConverter.ToUInt16(d, blockStart + 12) != classId)
            return null;
        var fields = SerializationFields(classId);
        var materialFields = SerializationFields(2479);
        if (fields == null || materialFields == null || materialFields.Count == 0
            || materialFields[0] is not { Declaring: 2479, Name: "m_name", Type: 8, Mods: 96 })
            return null;
        int payloadEnd = blockStart + 16 + (int)dataSize;
        if (BitConverter.ToUInt32(d, payloadEnd - 4) != dataSize) return null;

        var cur = new Cursor { D = d, Pos = blockStart + 14, End = payloadEnd - 4 };
        var queue = new List<(ushort Cls, uint Id)>();
        int materialIndex = -1;
        bool ownOwnerRead = false;
        try
        {
            foreach (var field in fields)
            {
                failure = $"root {field.Declaring}.{field.Name}";
                if (field is { Declaring: 37, Name: "m_id" })
                {
                    if (ownOwnerRead || field is not { Type: 14, Mods: 0, Ref: 20 }) return null;
                    int owner = ReadI32(cur);
                    if (owner <= 0 || (uint)owner != BitConverter.ToUInt32(d, blockStart)) return null;
                    ownOwnerRead = true;
                    continue;
                }
                // MaterialElem.m_materialPathMap is a map<int, pair<AString, int>>
                // (classes 345/346); the field carries no item class, so read it here.
                if (field is { Declaring: 2486, Name: "m_materialPathMap", Type: 14, Mods: 80 })
                {
                    uint entries = ReadU32(cur);
                    if (entries == 0xFFFFFFFF) continue;
                    if (entries > 10_000) return null;
                    for (uint index = 0; index < entries; index++)
                    {
                        ReadI32(cur);
                        ReadString(cur);
                        ReadI32(cur);
                    }
                    continue;
                }
                int before = queue.Count;
                ReadField(cur, field, queue, 0, null);
                if (field is not { Declaring: 2486, Name: "m_pMaterial" }) continue;
                if (queue.Count != before + 1 || queue[before].Cls != 2479) return null;
                materialIndex = before;
            }
            if (materialIndex < 0 || !ownOwnerRead) return null;

            for (int queueIndex = 0; queueIndex < materialIndex; queueIndex++)
            {
                ushort queuedClass = queue[queueIndex].Cls;
                failure = $"object {queuedClass} @{cur.Pos - blockStart}";
                if (ParamSetTags.TryGetValue(queuedClass, out var parameterStore))
                {
                    uint count = ReadU32(cur);
                    if (count > 100_000) return null;
                    var recordFields = SerializationFields(parameterStore.RecordClass);
                    if (recordFields == null) return null;
                    for (uint index = 0; index < count; index++)
                        foreach (var recordField in recordFields)
                            ReadField(cur, recordField, new List<(ushort, uint)>(), 0, null);
                    continue;
                }
                foreach (var queuedField in SerializationFields(queuedClass) ?? throw Fail)
                    ReadField(cur, queuedField, queue, 1, null);
            }
            failure = "name";
            string name = ReadString(cur);
            if (string.IsNullOrEmpty(name)) return null;
            failure = null;
            return name;
        }
        catch (DecodeFail) { return null; }
    }

    /// <summary>Reads a graphics style: the GStyleElem root (class 2050) owns exactly one deferred GStyle (class 2046)
    /// through m_pGStyle, and GStyle.m_materialElemId is one of its serialized fields;
    /// the root names the category of the style in m_categoryId. The root and
    /// every deferred object queued before the GStyle are walked in order, as for
    /// <see cref="ExtractMaterialName(byte[], int, uint, ushort)"/>. <paramref name="materialId"/> is the stored
    /// ElementId (-1 or 0 when the style has no material); false when anything fails to decode.</summary>
    internal static bool TryExtractGraphicsStyle(
        byte[] d, int blockStart, uint dataSize, ushort classId, out int categoryId, out int materialId)
    {
        categoryId = 0;
        materialId = 0;
        if (classId != 2050
            || blockStart < 0
            || dataSize > int.MaxValue
            || (long)blockStart + 16 + dataSize > d.Length
            || dataSize < 6
            || BitConverter.ToUInt32(d, blockStart + 8) != dataSize
            || BitConverter.ToUInt16(d, blockStart + 12) != classId)
            return false;
        var fields = SerializationFields(classId);
        var styleFields = SerializationFields(2046);
        if (fields == null || styleFields == null
            || !styleFields.Any(field => field is { Declaring: 2046, Name: "m_materialElemId", Type: 14, Mods: 0, Ref: 20 })
            || fields.Count(field => field is { Declaring: 2050, Name: "m_pGStyle", Type: 14, Mods: 1 }) != 1)
            return false;
        int payloadEnd = blockStart + 16 + (int)dataSize;
        if (BitConverter.ToUInt32(d, payloadEnd - 4) != dataSize) return false;

        var cur = new Cursor { D = d, Pos = blockStart + 14, End = payloadEnd - 4 };
        var queue = new List<(ushort Cls, uint Id)>();
        int styleIndex = -1;
        try
        {
            bool categoryRead = false;
            foreach (var field in fields)
            {
                if (field is { Declaring: 2050, Name: "m_categoryId" })
                {
                    if (field is not { Type: 14, Mods: 0, Ref: 20 }) return false;
                    categoryId = ReadI32(cur);
                    categoryRead = true;
                    continue;
                }
                int before = queue.Count;
                ReadField(cur, field, queue, 0, null);
                if (field is not { Declaring: 2050, Name: "m_pGStyle" }) continue;
                if (queue.Count != before + 1 || queue[before].Cls != 2046) return false;
                styleIndex = before;
            }
            if (styleIndex < 0 || !categoryRead) return false;

            for (int queueIndex = 0; queueIndex < styleIndex; queueIndex++)
            {
                ushort queuedClass = queue[queueIndex].Cls;
                if (ParamSetTags.TryGetValue(queuedClass, out var parameterStore))
                {
                    uint count = ReadU32(cur);
                    if (count > 100_000) return false;
                    var recordFields = SerializationFields(parameterStore.RecordClass);
                    if (recordFields == null) return false;
                    for (uint index = 0; index < count; index++)
                        foreach (var recordField in recordFields)
                            ReadField(cur, recordField, new List<(ushort, uint)>(), 0, null);
                    continue;
                }
                foreach (var queuedField in SerializationFields(queuedClass) ?? throw Fail)
                    ReadField(cur, queuedField, queue, 1, null);
            }

            bool read = false;
            foreach (var styleField in styleFields)
            {
                if (styleField is { Declaring: 2046, Name: "m_materialElemId" })
                {
                    materialId = ReadI32(cur);
                    read = true;
                }
                else ReadField(cur, styleField, queue, 1, null);
            }
            return read;
        }
        catch (DecodeFail) { return false; }
    }

    /// <summary>Reads the name of a category element: the CategoryElem root (class
    /// 632) owns exactly one deferred Category (class 629) through m_pCategory, and Category.m_name is that object's
    /// first serialized field. The root and every deferred object queued before the Category are walked in order, as for
    /// <see cref="ExtractMaterialName(byte[], int, uint, ushort)"/>. Null when anything fails to decode.</summary>
    internal static string? ExtractCategoryName(byte[] d, int blockStart, uint dataSize, ushort classId)
    {
        if (classId != 632
            || blockStart < 0
            || dataSize > int.MaxValue
            || (long)blockStart + 16 + dataSize > d.Length
            || dataSize < 6
            || BitConverter.ToUInt32(d, blockStart + 8) != dataSize
            || BitConverter.ToUInt16(d, blockStart + 12) != classId)
            return null;
        var fields = SerializationFields(classId);
        var categoryFields = SerializationFields(629);
        if (fields == null || categoryFields == null || categoryFields.Count == 0
            || categoryFields[0] is not { Declaring: 629, Name: "m_name", Type: 8, Mods: 96 }
            || fields.Count(field => field is { Declaring: 632, Name: "m_pCategory", Type: 14, Mods: 1 }) != 1)
            return null;
        int payloadEnd = blockStart + 16 + (int)dataSize;
        if (BitConverter.ToUInt32(d, payloadEnd - 4) != dataSize) return null;

        var cur = new Cursor { D = d, Pos = blockStart + 14, End = payloadEnd - 4 };
        var queue = new List<(ushort Cls, uint Id)>();
        int categoryIndex = -1;
        try
        {
            foreach (var field in fields)
            {
                int before = queue.Count;
                ReadField(cur, field, queue, 0, null);
                if (field is not { Declaring: 632, Name: "m_pCategory" }) continue;
                if (queue.Count != before + 1 || queue[before].Cls != 629) return null;
                categoryIndex = before;
            }
            if (categoryIndex < 0) return null;

            for (int queueIndex = 0; queueIndex < categoryIndex; queueIndex++)
            {
                ushort queuedClass = queue[queueIndex].Cls;
                if (ParamSetTags.TryGetValue(queuedClass, out var parameterStore))
                {
                    uint count = ReadU32(cur);
                    if (count > 100_000) return null;
                    var recordFields = SerializationFields(parameterStore.RecordClass);
                    if (recordFields == null) return null;
                    for (uint index = 0; index < count; index++)
                        foreach (var recordField in recordFields)
                            ReadField(cur, recordField, new List<(ushort, uint)>(), 0, null);
                    continue;
                }
                foreach (var queuedField in SerializationFields(queuedClass) ?? throw Fail)
                    ReadField(cur, queuedField, queue, 1, null);
            }
            return ReadString(cur);
        }
        catch (DecodeFail) { return null; }
    }

    // (class, m_version) pairs whose persisted own-field layout is read below. A step of any
    // other version is not guessed.
    private static readonly HashSet<(ushort Class, int Version)> ExactWallGeomStepVersions = new()
    {
        (365, 0), (500, 7), (596, 0), (677, 9), (677, 12), (977, 9), (977, 12), (1502, 0),
        (1621, 2), (2256, 7), (2262, 16), (4036, 2), (4098, 3), (4137, 133), (4138, 1),
        (4147, 1), (4151, 3), (4153, 0), (4153, 3), (4153, 4), (4155, 1), (4155, 33),
    };

    // The classes of those pairs. The top-attachment walk accepts any m_version of them: the layout read below follows
    // the class schema, and on Autodesk's samples of Revit 2020 to 2025 every wall the strict pairs read keeps its value,
    // each wall reads the same in every save of its project, and rac_advanced 2020 equals Revit's IFC2X3 export of it
    // on all 146 walls (ExtendToStructure; the strict pairs read none of them).
    private static readonly HashSet<ushort> ExactWallGeomStepClasses =
        ExactWallGeomStepVersions.Select(pair => pair.Class).ToHashSet();

    /// <summary>Reads one wall geometry step. GeomGenerator persists its face, curve and
    /// edge history tables as counted FaceHistEntry (16 bytes), CurveHistEntry (24) and
    /// EdgeHistEntry (20) records, followed by an empty reverse table, m_id, then GeomStep's
    /// m_version, m_flags and m_pElem back-reference. Own fields follow in schema order;
    /// containers are counted: EndInfo 9 bytes, the exposed-face map 12, pair&lt;int,
    /// ElementId&gt; 8, an affected-edge entry an ElementId plus a counted tag set, a merged
    /// face entry an int key plus a counted int set.</summary>
    private static bool TryReadExactWallGeomStep(
        Cursor c, ushort classId, out uint owner, out bool joinsTop, out string? failure, bool anyVersion = false)
    {
        owner = 0;
        joinsTop = false;
        failure = null;
        uint Count()
        {
            uint n = ReadU32(c);
            if (n == 0xFFFFFFFF) return 0;
            if (n > 100_000) throw Fail;
            return n;
        }
        void Skip(long bytes)
        {
            if (bytes < 0 || c.Pos + bytes > c.End) throw Fail;
            c.Pos += (int)bytes;
        }
        byte U8()
        {
            if (c.Pos + 1 > c.End) throw Fail;
            return c.D[c.Pos++];
        }

        Skip(16L * Count());
        Skip(24L * Count());
        Skip(20L * Count());
        uint reverse = ReadU32(c);
        if (reverse is not (0 or 0xFFFFFFFF)) { failure = "reverse"; return false; }
        ReadI32(c);
        int version = ReadI32(c);
        ReadI32(c);
        owner = ReadU32(c);
        if (anyVersion ? !ExactWallGeomStepClasses.Contains(classId) : !ExactWallGeomStepVersions.Contains((classId, version)))
        {
            failure = $"v{version}";
            return false;
        }

        switch (classId)
        {
            case 365 or 596 or 1502 or 4036 or 4153:
                break;
            case 500:
                ReadI32(c);
                break;
            case 1621:
                ReadI32(c);
                U8();
                break;
            case 2256:
                ReadI32(c);
                ReadI32(c);
                U8();
                Skip(9L * Count());
                Skip(9L * Count());
                Skip(12L * Count());
                break;
            case 2262:
            {
                uint targets = Count();
                Skip(4L * targets);
                int baseOrTop = ReadI32(c);
                ReadI32(c);
                if (targets == 0 || baseOrTop is not (0 or 1)) { failure = "join to roof"; return false; }
                joinsTop = baseOrTop == 1;
                break;
            }
            case 4098:
                Skip(8L * Count());
                Skip(8L * Count());
                break;
            case 4147:
            {
                uint affected = Count();
                for (uint index = 0; index < affected; index++)
                {
                    ReadI32(c);
                    Skip(4L * Count());
                }
                Skip(4L * Count());
                Skip(4L * Count());
                break;
            }
            case 677 or 977:
            {
                ReadI32(c);
                U8();
                U8();
                U8();
                if (classId == 977)
                {
                    uint merged = Count();
                    for (uint index = 0; index < merged; index++)
                    {
                        ReadI32(c);
                        Skip(4L * Count());
                    }
                    for (int index = 0; index < 5; index++) U8();
                }
                else
                {
                    uint affected = Count();
                    for (uint index = 0; index < affected; index++)
                    {
                        ReadI32(c);
                        Skip(4L * Count());
                    }
                    Skip(4L * Count());
                    U8();
                    U8();
                }
                break;
            }
            case 4137 or 4138 or 4151 or 4155:
                ReadI32(c);
                ReadI32(c);
                ReadI32(c);
                ReadI32(c);
                U8();
                U8();
                U8();
                Skip(Count());
                if (classId == 4151)
                {
                    Skip(4L * 2 + 4L * 4);
                    ReadI32(c);
                }
                else if (classId == 4138)
                {
                    ReadI32(c);
                    U8();
                }
                else if (classId == 4155)
                {
                    ReadI32(c);
                    ReadI32(c);
                }
                break;
            default:
                failure = "class";
                return false;
        }
        return true;
    }

    private static void ReadTrackedPointerContainer(Cursor cur,
        List<(ushort Cls, uint Id)> queue, HashSet<int> trackedQueueIndices)
    {
        uint count = ReadU32(cur);
        if (count == 0xFFFFFFFF) return;
        if (count > 1_000_000) throw Fail;
        for (uint index = 0; index < count; index++)
        {
            int queueIndex = queue.Count;
            ReadPtr(cur, queue);
            if (queue.Count != queueIndex + 1) throw Fail;
            trackedQueueIndices.Add(queueIndex);
        }
    }

    private static bool TryRecoverSerializedLineLoops(byte[] data, int start, int end,
        IReadOnlyDictionary<int, int> lineCounts,
        out Dictionary<int, List<(double[] Start, double[] End)>> recovered)
    {
        recovered = new();
        var orderedCounts = lineCounts.OrderBy(pair => pair.Key).ToList();
        if (orderedCounts.Count == 0 || orderedCounts.Any(pair => pair.Value < 3))
            return false;

        Dictionary<int, List<(double[] Start, double[] End)>>? unique = null;
        for (int candidateStart = start; candidateStart < end; candidateStart++)
        {
            int position = candidateStart;
            var candidate = new Dictionary<int, List<(double[] Start, double[] End)>>();
            bool valid = true;
            foreach (var (loopIndex, lineCount) in orderedCounts)
            {
                var lines = new List<(double[] Start, double[] End)>(lineCount);
                for (int lineIndex = 0; lineIndex < lineCount; lineIndex++)
                {
                    if (!TryReadSerializedGLine(data, ref position, end, out var line))
                    {
                        valid = false;
                        break;
                    }
                    lines.Add(line);
                }
                if (!valid || !TryOrderClosedLineLoop(lines, out _))
                {
                    valid = false;
                    break;
                }
                candidate[loopIndex] = lines;
            }
            if (!valid) continue;
            // The serialized pointer classes, line grammar, exact line count and closed
            // topology must identify one and only one deferred sequence. Ambiguity is a
            // hard failure; no nearest-offset or geometric ranking is used.
            if (unique != null) return false;
            unique = candidate;
        }
        if (unique == null) return false;
        recovered = unique;
        return true;
    }

    private static bool TryReadSerializedGLine(byte[] data, ref int position, int end,
        out (double[] Start, double[] End) line)
    {
        line = default;
        if (position + 4 > end) return false;
        uint inlineFlags = position + 16 <= end
            ? BitConverter.ToUInt32(data, position + 12) : 0;
        if ((inlineFlags & 0xFF00FF) == 0x080004)
        {
            position += 16;
        }
        else if (BitConverter.ToUInt32(data, position) == 0)
            position += 4;
        else return false;
        if (position + 8 * 8 > end) return false;
        double ep0 = BitConverter.ToDouble(data, position);
        double ep1 = BitConverter.ToDouble(data, position + 8);
        var origin = new[]
        {
            BitConverter.ToDouble(data, position + 16),
            BitConverter.ToDouble(data, position + 24),
            BitConverter.ToDouble(data, position + 32),
        };
        var direction = new[]
        {
            BitConverter.ToDouble(data, position + 40),
            BitConverter.ToDouble(data, position + 48),
            BitConverter.ToDouble(data, position + 56),
        };
        position += 64;
        if (!double.IsFinite(ep0) || !double.IsFinite(ep1)
            || origin.Concat(direction).Any(value => !double.IsFinite(value)))
            return false;
        double directionLength = Math.Sqrt(direction.Sum(value => value * value));
        double roundoff = 4096 * 2.2204460492503131e-16
            * Math.Max(1.0, directionLength);
        if (Math.Abs(directionLength - 1) > roundoff || ep0 == ep1) return false;
        line =
        (
            [origin[0] + direction[0] * ep0,
                origin[1] + direction[1] * ep0,
                origin[2] + direction[2] * ep0],
            [origin[0] + direction[0] * ep1,
                origin[1] + direction[1] * ep1,
                origin[2] + direction[2] * ep1]
        );
        return true;
    }

    internal static bool TryOrderClosedLineLoop(
        IReadOnlyList<(double[] Start, double[] End)> lines,
        out IReadOnlyList<double[]> vertices)
    {
        vertices = [];
        if (lines.Count < 3) return false;
        double scale = Math.Max(1.0, lines
            .SelectMany(line => line.Start.Concat(line.End)).Select(Math.Abs).Max());
        double roundoff = 4096 * 2.2204460492503131e-16 * scale;
        static double Distance(double[] first, double[] second)
        {
            double dx = first[0] - second[0];
            double dy = first[1] - second[1];
            double dz = first[2] - second[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        bool TryOrientation(bool reverseFirst, out List<double[]> ordered)
        {
            ordered = new();
            double[] start = reverseFirst ? lines[0].End : lines[0].Start;
            double[] current = reverseFirst ? lines[0].Start : lines[0].End;
            ordered.Add(start);
            for (int index = 1; index < lines.Count; index++)
            {
                var line = lines[index];
                if (Distance(current, line.Start) <= roundoff)
                {
                    ordered.Add(current);
                    current = line.End;
                }
                else if (Distance(current, line.End) <= roundoff)
                {
                    ordered.Add(current);
                    current = line.Start;
                }
                else return false;
            }
            return Distance(current, start) <= roundoff;
        }

        if (!TryOrientation(false, out var result)
            && !TryOrientation(true, out result))
            return false;
        vertices = result;
        return true;
    }

    /// <summary>Decode a CurveElem (0x346) block fully (root + all deferred objects) and
    /// return the curve's line style (BUILDING_CURVE_GSTYLE) and length (CURVE_ELEM_LENGTH).
    /// Detail arcs store geometry as deferred GArc/GLine/GEllipse objects the pattern scan
    /// couldn't reach; the full schema walk with rules R1-R7 recovers them.</summary>
    public static (int GStyle, double Length)? ExtractCurveGeometry(byte[] d, int blockStart, uint dataSize)
    {
        var fields = SerializationFields(0x346);
        if (fields == null) return null;
        int payloadStart = blockStart + 14;
        int payloadEnd = blockStart + 16 + (int)dataSize;
        if (payloadEnd > d.Length || payloadEnd - payloadStart < 6) return null;
        if (BitConverter.ToUInt32(d, payloadEnd - 4) != dataSize) return null;

        var cur = new Cursor { D = d, Pos = payloadStart, End = payloadEnd - 4, Curves = new() };
        var queue = new List<(ushort Cls, uint Id)>();
        try
        {
            foreach (var f in fields) ReadField(cur, f, queue, 0, null);
            // Process the FULL deferred queue (not just param stores), capturing each
            // GArc/GLine/GEllipse object's geometry.
            for (int i = 0; i < queue.Count; i++)
            {
                var (cls, _) = queue[i];
                if (ParamSetTags.ContainsKey(cls))
                {
                    // Param store: [count] x record - skip its bytes to stay aligned.
                    uint n = ReadU32(cur);
                    if (n > 100_000) throw Fail;
                    var rf = SerializationFields(ParamSetTags[cls].RecordClass) ?? throw Fail;
                    for (uint k = 0; k < n; k++)
                        foreach (var f2 in rf) ReadField(cur, f2, new List<(ushort, uint)>(), 0, null);
                    continue;
                }
                var qf = SerializationFields(cls);
                if (qf == null) throw Fail;
                bool isCurve = cls is 0x7B6 or 0x7DA or 0x6C8;
                if (isCurve) { cur.Ep0 = cur.Ep1 = cur.Radius = cur.XLen = cur.YLen = 0; }
                foreach (var sf in qf) ReadField(cur, sf, queue, 1, null);
                if (isCurve)
                    cur.Curves!.Add((cls, cur.Ep0, cur.Ep1, cur.Radius, cur.XLen, cur.YLen));
            }
        }
        catch (DecodeFail) { } // Expected: an unsupported or truncated field ends the scan; fall through to the no-value result

        double? length = null;
        // Prefer a GArc/GEllipse over a GLine (a detail arc may carry a helper line).
        foreach (var pref in new[] { 0x7B6, 0x7DA, 0x6C8 })
        {
            foreach (var g in cur.Curves!)
            {
                if (g.Kind != pref) continue;
                double sweep = Math.Abs(g.Ep1 - g.Ep0);
                if (g.Kind == 0x7B6) // GArc: params are angles
                {
                    if (sweep < 1e-12) sweep = 2 * Math.PI;
                    length = sweep * g.R;
                }
                else if (g.Kind == 0x6C8) // GLine: params are distances
                    length = sweep;
                else // GEllipse: numeric arc length
                {
                    double a = g.XL, b = g.YL, ep0 = g.Ep0, ep1 = g.Ep1;
                    if (sweep < 1e-12) { ep0 = 0; ep1 = 2 * Math.PI; }
                    const int N = 512;
                    double prevx = 0, prevy = 0, L = 0;
                    for (int k = 0; k < N; k++)
                    {
                        double t = ep0 + (ep1 - ep0) * k / (N - 1);
                        double x = a * Math.Cos(t), y = b * Math.Sin(t);
                        if (k > 0) L += Math.Sqrt((x - prevx) * (x - prevx) + (y - prevy) * (y - prevy));
                        prevx = x; prevy = y;
                    }
                    length = L;
                }
                break;
            }
            if (length != null) break;
        }

        int gs = cur.GStyle == int.MinValue ? -1 : cur.GStyle;
        if (gs < 0 && length == null) return null;
        return (gs, length ?? 0);
    }

    /// <summary>Decode the two coordinate representations stored by a BasePoint block.
    /// For the survey point their relation is Shared = ProjectLocation(Internal), which
    /// lets the reader recover the translation part of the active shared transform.</summary>
    public static (double[] Internal, double[] Shared)? ExtractBasePointCoordinates(
        byte[] d, int blockStart, uint dataSize, ushort classId)
    {
        if (classId != 480) return null;
        var fields = SerializationFields(classId);
        if (fields == null) return null;
        int payloadStart = blockStart + 14;
        int payloadEnd = blockStart + 16 + (int)dataSize;
        if (payloadEnd > d.Length || payloadEnd - payloadStart < 6) return null;
        if (BitConverter.ToUInt32(d, payloadEnd - 4) != dataSize) return null;

        var cur = new Cursor
        {
            D = d,
            Pos = payloadStart,
            End = payloadEnd - 4,
        };
        var queue = new List<(ushort Cls, uint Id)>();
        try
        {
            foreach (var f in fields) ReadField(cur, f, queue, 0, null);
            for (int i = 0; i < queue.Count; i++)
            {
                var (cls, _) = queue[i];
                if (ParamSetTags.TryGetValue(cls, out var tag))
                {
                    uint n = ReadU32(cur);
                    if (n > 100_000) throw Fail;
                    var rf = SerializationFields(tag.RecordClass) ?? throw Fail;
                    for (uint k = 0; k < n; k++)
                        foreach (var f2 in rf) ReadField(cur, f2, new List<(ushort, uint)>(), 0, null);
                    continue;
                }
                var qf = SerializationFields(cls);
                if (qf == null) throw Fail;
                foreach (var sf in qf) ReadField(cur, sf, queue, 1, null);
                if (cur.BasePointInternal != null && cur.BasePointShared != null)
                    return (cur.BasePointInternal, cur.BasePointShared);
            }
        }
        catch (DecodeFail) { } // Expected: an unsupported or truncated field ends the scan; fall through to the no-value result
        return cur.BasePointInternal != null && cur.BasePointShared != null
            ? (cur.BasePointInternal, cur.BasePointShared)
            : null;
    }

    /// <summary>Decode a GeoLocation (Revit ProjectLocation): the Trf its InstanceInfo stores
    /// (nine row-major matrix doubles and the origin, feet) and the id of its GeoSite symbol.
    /// Null when the block does not reach the InstanceInfo or the Trf is not fully read.</summary>
    public static (double[] Rotation, double[] Origin, int SymbolId)? ExtractGeoLocation(
        byte[] d, int blockStart, uint dataSize, ushort classId)
    {
        if (classId != 2070) return null;
        var fields = SerializationFields(classId);
        if (fields == null) return null;
        int payloadStart = blockStart + 14;
        int payloadEnd = blockStart + 16 + (int)dataSize;
        if (payloadEnd > d.Length || payloadEnd - payloadStart < 6) return null;
        if (BitConverter.ToUInt32(d, payloadEnd - 4) != dataSize) return null;

        var cur = new Cursor
        {
            D = d,
            Pos = payloadStart,
            End = payloadEnd - 4,
            GeoLocationExtraction = true,
        };
        var queue = new List<(ushort Cls, uint Id)>();
        try
        {
            foreach (var f in fields) ReadField(cur, f, queue, 0, null);
            for (int i = 0; i < queue.Count; i++)
            {
                var (cls, _) = queue[i];
                if (ParamSetTags.TryGetValue(cls, out var tag))
                {
                    uint n = ReadU32(cur);
                    if (n > 100_000) throw Fail;
                    var rf = SerializationFields(tag.RecordClass) ?? throw Fail;
                    for (uint k = 0; k < n; k++)
                        foreach (var f2 in rf) ReadField(cur, f2, new List<(ushort, uint)>(), 0, null);
                    continue;
                }
                var qf = SerializationFields(cls) ?? throw Fail;
                foreach (var sf in qf) ReadField(cur, sf, queue, 1, null);
                if (cur.GeoRotation != null && cur.GeoOrigin != null && cur.GeoSymbolId != null)
                    return (cur.GeoRotation, cur.GeoOrigin, cur.GeoSymbolId.Value);
            }
        }
        catch (DecodeFail) { } // Expected: an unsupported or truncated field ends the scan; fall through to the no-value result
        return cur.GeoRotation != null && cur.GeoOrigin != null && cur.GeoSymbolId != null
            ? (cur.GeoRotation, cur.GeoOrigin, cur.GeoSymbolId.Value)
            : null;
    }

    /// <summary>Decode the exact finite Grid curve from DatumPlane.m_pSurface. Its Plane
    /// stores the local U interval in Surface.m_Envelope and the world origin/X vector;
    /// the authored endpoints are P(Umin) and P(Umax). No model bounds are involved.</summary>
    public static GridDatum? ExtractGridDatum(byte[] d, int blockStart, uint dataSize,
        ushort classId)
    {
        if (classId != 0x083C) return null;
        var parameters = ExtractParams(d, blockStart, dataSize, classId);
        if (parameters == null
            || !parameters.StringFields.TryGetValue("m_text", out string? name)
            || string.IsNullOrWhiteSpace(name))
            return null;

        var surface = ExtractDatumPlaneSurface(d, blockStart, dataSize, classId);
        if (surface == null) return null;
        var (envelope, origin, xVector, yVector) = surface.Value;

        double[] PointAt(double parameter) =>
        [
            origin[0] + parameter * xVector[0],
            origin[1] + parameter * xVector[1],
            origin[2] + parameter * xVector[2],
        ];
        double[] start = PointAt(envelope[0]);
        double[] end = PointAt(envelope[2]);
        if (!start.Concat(end).All(double.IsFinite) || start.SequenceEqual(end))
            return null;

        double[] cut = new double[3];
        for (int coordinate = 0; coordinate < 3; coordinate++)
            parameters.DoubleFields.TryGetValue($"m_cutVec@{coordinate}",
                out cut[coordinate]);
        // The vertical extent of the finite surface: the height of the four corners of the plane envelope.
        double zMin = double.PositiveInfinity, zMax = double.NegativeInfinity;
        foreach (double u in new[] { envelope[0], envelope[2] })
            foreach (double v in new[] { envelope[1], envelope[3] })
            {
                double z = origin[2] + u * xVector[2] + v * yVector[2];
                zMin = Math.Min(zMin, z);
                zMax = Math.Max(zMax, z);
            }
        double[]? levelInterval = double.IsFinite(zMin) && double.IsFinite(zMax) ? [zMin, zMax] : null;
        return new GridDatum(name, start, end, cut, levelInterval, [xVector[0], xVector[1], xVector[2]], envelope.ToArray(),
            origin.ToArray(), yVector.ToArray());
    }

    /// <summary>Decode the exact Level elevation from its authored datum Plane origin.</summary>
    public static double? ExtractLevelElevation(byte[] d, int blockStart, uint dataSize,
        ushort classId)
    {
        if (classId != 2299) return null;
        var surface = ExtractDatumPlaneSurface(d, blockStart, dataSize, classId);
        if (surface == null) return null;
        double elevation = surface.Value.Origin[2];
        return double.IsFinite(elevation) ? elevation : null;
    }

    private static (double[] Envelope, double[] Origin, double[] XVector, double[] YVector)?
        ExtractDatumPlaneSurface(byte[] d, int blockStart, uint dataSize, ushort classId)
    {
        if (classId is not (0x083C or 2299)) return null;

        var fields = SerializationFields(classId);
        if (fields == null) return null;
        int payloadStart = blockStart + 14;
        int payloadEnd = blockStart + 16 + (int)dataSize;
        if (payloadEnd > d.Length || payloadEnd - payloadStart < 6
            || BitConverter.ToUInt32(d, payloadEnd - 4) != dataSize)
            return null;

        var cur = new Cursor
        {
            D = d,
            Pos = payloadStart,
            End = payloadEnd - 4,
            GridDatumExtraction = true,
        };
        var queue = new List<(ushort Cls, uint Id)>();
        try
        {
            foreach (var field in fields) ReadField(cur, field, queue, 0, null);
            if (cur.GridSurfaceQueueIndex < 0) return null;

            for (int queueIndex = 0; queueIndex < queue.Count; queueIndex++)
            {
                var (queuedClass, _) = queue[queueIndex];
                if (ParamSetTags.TryGetValue(queuedClass, out var tag))
                {
                    uint count = ReadU32(cur);
                    if (count > 100_000) return null;
                    var recordFields = SerializationFields(tag.RecordClass) ?? throw Fail;
                    for (uint item = 0; item < count; item++)
                        foreach (var recordField in recordFields)
                            ReadField(cur, recordField, new List<(ushort, uint)>(), 0, null);
                    continue;
                }

                var queuedFields = SerializationFields(queuedClass) ?? throw Fail;
                bool targetSurface = queueIndex == cur.GridSurfaceQueueIndex
                    && queuedClass == 522;
                cur.CaptureGridPlane = targetSurface;
                if (targetSurface)
                {
                    cur.GridEnvelope = null;
                    cur.GridOrigin = null;
                    cur.GridXVector = null;
                    cur.GridYVector = null;
                }
                foreach (var field in queuedFields)
                    ReadField(cur, field, queue, 1, null);
                cur.CaptureGridPlane = false;

                if (targetSurface && cur.GridEnvelope is { Length: 4 } envelope
                    && cur.GridOrigin is { Length: 3 } origin
                    && cur.GridXVector is { Length: 3 } xVector
                    && cur.GridYVector is { Length: 3 } yVector)
                {
                    return (envelope, origin, xVector, yVector);
                }
            }
        }
        catch (DecodeFail) { } // Expected: an unsupported or truncated field ends the scan; fall through to the no-value result
        return null;
    }

    /// <summary>Read DatumPlane.m_text from a Level block without depending on the
    /// complete schema walk. Some recovered chunks preserve the small Level block but
    /// not every deferred parameter-store object required by ExtractParams.</summary>
    public static string? ExtractLevelName(byte[] d, int blockStart, uint dataSize, ushort classId)
    {
        if (classId != 2299) return null;
        int start = blockStart + 16;
        int end = Math.Min(d.Length, start + (int)dataSize);
        int scanEnd = Math.Min(end, start + 160);
        for (int p = start; p + 6 <= scanEnd; p++)
        {
            uint len = BitConverter.ToUInt32(d, p);
            if (len is 0 or > 200 || p + 4L + len * 2L > end) continue;
            string value = System.Text.Encoding.Unicode.GetString(d, p + 4, (int)len * 2);
            if (!StringScanner.IsValidElementName(value)
                || value.Any(ch => ch == '\0' || char.IsControl(ch))) continue;
            return value;
        }
        return null;
    }

    private static uint ReadU32(Cursor c)
    {
        if (c.Pos + 4 > c.End) throw Fail;
        uint v = BitConverter.ToUInt32(c.D, c.Pos); c.Pos += 4; return v;
    }
    private static int ReadI32(Cursor c)
    {
        if (c.Pos + 4 > c.End) throw Fail;
        int v = BitConverter.ToInt32(c.D, c.Pos); c.Pos += 4; return v;
    }
    private static double ReadF64(Cursor c)
    {
        if (c.Pos + 8 > c.End) throw Fail;
        double v = BitConverter.ToDouble(c.D, c.Pos); c.Pos += 8; return v;
    }
    private static string ReadString(Cursor c)
    {
        uint len = ReadU32(c);
        if (len > 10_000 || c.Pos + len * 2 > c.End) throw Fail;
        string s = System.Text.Encoding.Unicode.GetString(c.D, c.Pos, (int)len * 2);
        c.Pos += (int)len * 2;
        return s;
    }

    private static void ReadField(Cursor c, TfField f, List<(ushort, uint)> queue, int depth,
        NativeParams? capture)
    {
        if (CaptureAllFields)
        {
            diagCurrentField = FieldKey(f);
            (readTrail ??= new()).Add(FieldKey(f) + "/" + f.Type + "/" + f.Mods + "/" + f.Ref + "/" + f.Cnt + "@" + c.Pos);
            if (readTrail.Count > 14) readTrail.RemoveAt(0);
            lastReadField = FieldKey(f) + "/" + f.Type + "/" + f.Mods + "/" + f.Ref + "@" + c.Pos + " bytes=" + Convert.ToHexString(c.D, Math.Max(0, c.Pos - 8), Math.Min(48, c.D.Length - Math.Max(0, c.Pos - 8)));
        }
        // Every derived step binds the same inline GeomGenerator history entries.
        if (c.ExactFamilyCutInputs && f.Type == 14 && f.Ref == null && f.Cnt == null)
        {
            int historyTarget = (f.Declaring, f.Name, f.Mods) switch
            {
                (97, "m_faceHistTable", 80) => 98,
                (97, "m_curveHistTableSet", 80) => 100,
                (97, "m_edgeHistTable", 80) => 102,
                (98, "m_faceHist", 0) => 99,
                (100, "m_curveHist", 0) => 101,
                (102, "m_edgeHist", 0) => 103,
                _ => 0
            };
            if (historyTarget != 0)
            {
                if (!HasCountedWallHistorySchema()) throw Fail;
                f = f with { Ref = historyTarget };
            }
        }
        // Matrix.m_rows is inline MatrixRow, missing from the recovered schema.
        if (c.ExactFamilyCutInputs
            && f is { Declaring: 1722, Name: "m_rows", Type: 14, Mods: 80, Ref: null, Cnt: null })
        {
            if (!Schemas.TryGetValue(1722, out var matrix) || matrix.Name != "Matrix"
                || !Schemas.TryGetValue(1723, out var row) || row.Name != "MatrixRow"
                || row.Fields.Count != 1
                || row.Fields[0] is not { Name: "m_arr", Type: 7, Mods: 80, Ref: 1721, Cnt: null }) throw Fail;
            f = f with { Ref = 1723 };
        }
        // ElectricalFamInstDesignPropertyManager.m_LargestMotorMap is a pair<ElementId, pair<double, double>>
        // that the recovered schema leaves without a layout: 4 + 8 + 8 bytes (family instance roots only).
        if (c.RootClassId == 1794 && f is { Declaring: 1285, Name: "m_LargestMotorMap", Type: 14, Mods: 0, Ref: null, Cnt: null })
        { if (!c.Take(20)) throw Fail; return; }
        if (f is { Declaring: 1234, Name: "m_entityMap", Type: 14, Mods: 80, Ref: null, Cnt: null }
            && SkipEmptyExtensibleStorageEntities(c.D, c.Pos, c.End) is int afterEntities)
        { c.Pos = afterEntities; return; }
        if (f is { Declaring: 2367, Name: "m_connectorData", Type: 14, Mods: 81 })
            c.Pos = SkipExtraFittingDataString(c.D, c.Pos, c.End);
        // ConnectorManager.m_setDeletedConnectors is serialized only by some versions: a counted int list that must
        // be followed by a valid pointer array (count, then a pointer with a known class tag). Otherwise the
        // connector pointer array starts here.
        if (f is { Declaring: 793, Name: "m_setDeletedConnectors", Type: 4, Mods: 80 })
        {
            int listStart = c.Pos;
            uint deleted = ReadU32(c);
            if (deleted == 0xFFFFFFFF) deleted = 0;
            if (deleted <= 64 && c.Pos + 4L * deleted <= c.End && LooksLikePointerArray(c, c.Pos + 4 * (int)deleted))
            { c.Pos += 4 * (int)deleted; return; }
            c.Pos = listStart;
            if (!LooksLikePointerArray(c, listStart)) throw Fail;
            return;
        }
        if (c.ExactWallCompleteInputs && TryReadExactCompleteWallField(c, f, queue, depth)) return;
        if (TryReadOriginalWallAuxiliaryField(c, f, queue, depth, capture)) return;
        if (TryReadOriginalWallBackupField(c, f, queue, depth, capture)) return;
        if (TryReadOriginalCurtainAuxiliaryField(c, f, queue, depth, capture)) return;
        if (TryReadOriginalCornerMullionField(c, f, queue, depth, capture)) return;
        // The native declaration names GraveyardRec/ElementHistory/EpisodeId instead of the recovered missing/self refs.
        if (f is { Declaring: 1312, Name: "m_graveyardRecs", Type: 14, Mods: 80, Ref: null, Cnt: null }
            or { Declaring: 1313, Name: "m_history", Type: 14, Mods: 0, Ref: 1313, Cnt: null }
            or { Declaring: 819, Name: "m_lastUserModificationDate", Type: 14, Mods: 0, Ref: 819, Cnt: null })
        {
            if (!Schemas.TryGetValue(1312, out var table) || table.Name != "ElemTable" || table.Base != null
                || table.Fields.Count != 4
                || table.Fields[0] is not { Name: "m_elemArr", Type: 14, Mods: 80, Ref: 1308, Cnt: null }
                || table.Fields[1] is not { Name: "m_graveyardRecs", Type: 14, Mods: 80, Ref: null, Cnt: null }
                || table.Fields[2] is not { Name: "m_pSource", Type: 14, Mods: 2, Ref: null, Cnt: null }
                || table.Fields[3] is not { Name: "m_bExpandAllOnLoad", Type: 1, Mods: 0, Ref: null, Cnt: null }
                || !Schemas.TryGetValue(1313, out var graveyard) || graveyard.Name != "GraveyardRec" || graveyard.Base != null
                || graveyard.Fields.Count != 3
                || graveyard.Fields[0] is not { Name: "m_id", Type: 14, Mods: 0, Ref: 20, Cnt: null }
                || graveyard.Fields[1] is not { Name: "m_partitionId", Type: 14, Mods: 0, Ref: 1270, Cnt: null }
                || graveyard.Fields[2] is not { Name: "m_history", Type: 14, Mods: 0, Ref: 1313, Cnt: null }
                || !Schemas.TryGetValue(819, out var history) || history.Name != "ElementHistory" || history.Base != null
                || history.Fields.Count != 4
                || history.Fields[0] is not { Name: "m_originalElementId", Type: 14, Mods: 0, Ref: 546, Cnt: null }
                || history.Fields[1] is not { Name: "m_creationDate", Type: 14, Mods: 0, Ref: 504, Cnt: null }
                || history.Fields[2] is not { Name: "m_lastModificationDate", Type: 14, Mods: 0, Ref: 504, Cnt: null }
                || history.Fields[3] is not { Name: "m_lastUserModificationDate", Type: 14, Mods: 0, Ref: 819, Cnt: null }
                || !Schemas.TryGetValue(504, out var date) || date.Name != "EpisodeId" || date.Base != null
                || date.Fields.Count != 1
                || date.Fields[0] is not { Name: "m_id", Type: 4, Mods: 0, Ref: 504, Cnt: null }) throw Fail;
            f = f with { Ref = f.Declaring == 1312 ? 1313 : f.Declaring == 1313 ? 819 : 504 };
        }
        // The native declaration has two inline int32 pieces, not a polymorphic handle.
        if (f.Declaring == 814 && f.Type == 14 && f.Mods == 0 && f.Cnt == null
            && f.Name is "m_compactElement" or "m_compactGRep" or "m_elemId" or "m_epKingId")
        {
            if (depth > 40 || !Schemas.TryGetValue(814, out var content) || content.Name != "ContentElemRecPointers"
                || content.Base != null || content.Fields.Count != 5
                || content.Fields[0] is not { Name: "m_oElementHeader", Type: 14, Mods: 1, Ref: null, Cnt: null }
                || content.Fields[1] is not { Name: "m_compactElement", Type: 14, Mods: 0, Ref: null, Cnt: null }
                || content.Fields[2] is not { Name: "m_compactGRep", Type: 14, Mods: 0, Ref: 815, Cnt: null }
                || content.Fields[3] is not { Name: "m_elemId", Type: 14, Mods: 0, Ref: 20, Cnt: null }
                || content.Fields[4] is not { Name: "m_epKingId", Type: 14, Mods: 0, Ref: 504, Cnt: null }
                || !Schemas.TryGetValue(815, out var compact) || compact.Name != "SerializableCompactMemoryPointer"
                || compact.Base != null || compact.Fields.Count != 2
                || compact.Fields[0] is not { Name: "m_pointerPart1", Type: 4, Mods: 0, Ref: null, Cnt: null }
                || compact.Fields[1] is not { Name: "m_pointerPart2", Type: 4, Mods: 0, Ref: 815, Cnt: null }
                || !Schemas.TryGetValue(504, out var episode) || episode.Name != "EpisodeId"
                || episode.Base != null || episode.Fields.Count != 1
                || episode.Fields[0] is not { Name: "m_id", Type: 4, Mods: 0, Ref: 504, Cnt: null }) throw Fail;
            int first = ReadI32(c);
            bool pointer = f.Name is "m_compactElement" or "m_compactGRep";
            int second = pointer ? ReadI32(c) : 0;
            if (capture != null && c.RootClassId == 814)
            {
                capture.Fields[pointer ? f.Name + "@0" : f.Name] = first;
                if (pointer) capture.Fields[f.Name + "@1"] = second;
            }
            return;
        }
        // The native declaration has inline ElementAndGRep; the recovered R22 ref is missing.
        if (f is { Declaring: 1309, Name: "m_eag", Type: 14, Mods: 0, Ref: null, Cnt: null })
        {
            if (!Schemas.TryGetValue(1309, out var pointers) || pointers.Name != "ElemRecPointers"
                || pointers.Base != null || pointers.Fields.Count != 1
                || !Schemas.TryGetValue(1310, out var value) || value.Name != "ElementAndGRep"
                || value.Base != null || value.Fields.Count != 3
                || value.Fields[0] is not { Name: "m_id", Type: 14, Mods: 0, Ref: 20, Cnt: null }
                || value.Fields[1] is not { Name: "m_pElement", Type: 14, Mods: 1, Ref: null, Cnt: null }
                || value.Fields[2] is not { Name: "m_pGRep", Type: 14, Mods: 1, Ref: 1310, Cnt: null })
                throw Fail;
            f = f with { Ref = 1310 };
        }
        // The AddInId/UpdaterId ADDEntry managers both name GUIDvalue for
        // m_value. R22 metadata instead points back to the wrapper itself.
        // Correct only these exact inline contracts, without mutating the schema
        // or treating arbitrary self-references as GUIDs/polymorphic pointers.
        if (f.Name == "m_value" && f.Declaring is 34 or 1229
            && f.Type == 14 && f.Mods == 0 && f.Ref == f.Declaring && f.Cnt == null)
        {
            if (!Schemas.TryGetValue(f.Declaring, out var wrapper)
                || wrapper.Name != (f.Declaring == 34 ? "AddInId" : "UpdaterId")
                || !Schemas.TryGetValue(30, out var guid) || guid.Name != "GUIDvalue" || guid.Base != null
                || guid.Fields is not { Count: 1 }
                || guid.Fields[0] is not { Name: "m_guid", Type: 9, Mods: 0, Ref: 30, Cnt: null })
                throw Fail;
            f = f with { Ref = 30 };
        }
        // The R22 schema names this pair's second value FamSubInstDescriptor but its
        // reference points back to the pair itself. The correction applies to every walk (the own-name walk
        // confirmed it; the FamilySymbol parameter walk needs it to reach its stores); the shared schema/cache is
        // never mutated.
        if (f is { Declaring: 1763, Name: "second", Type: 14, Mods: 0, Ref: 1763 })
        {
            if (!Schemas.TryGetValue(1763, out var pair) || pair.Name != "std::pair< int, FamSubInstDescriptor >"
                || !Schemas.TryGetValue(1724, out var descriptor) || descriptor.Name != "FamSubInstDescriptor")
                throw Fail;
            f = f with { Ref = 1724 };
        }
        // Connector.m_arrRefs holds ConnectorId entries [element id, connector index, connection type] (12 bytes);
        // the recovered metadata leaves its element type out, and the generic reader takes 4-byte handles. The
        // connection type is an int32 although the metadata makes it a self reference (see TryFieldOverride).
        if (f is { Declaring: 771, Name: "m_arrRefs", Type: 14, Mods: 80, Ref: null, Cnt: null })
        {
            if (!Schemas.TryGetValue(771, out var connector) || connector.Name != "Connector"
                || !Schemas.TryGetValue(772, out var connectorId) || connectorId.Name != "ConnectorId"
                || connectorId.Fields.Count != 3
                || connectorId.Fields[0] is not { Name: "m_id", Type: 14, Mods: 0, Ref: 20 }
                || connectorId.Fields[1] is not { Name: "m_nIndex", Type: 4, Mods: 0 }
                || connectorId.Fields[2] is not { Name: "m_connType", Type: 4 })
                throw Fail;
            f = f with { Ref = 772 };
        }
        if (c.GridDatumExtraction && f.Declaring == 1052 && f.Name == "m_pSurface"
            && f.Type == 0x0E && (f.Mods & 0x0F) is 1 or 2)
        {
            int queueCount = queue.Count;
            ReadPtr(c, queue);
            if (queue.Count == queueCount + 1 && queue[^1].Item1 == 522)
                c.GridSurfaceQueueIndex = queue.Count - 1;
            return;
        }
        if (f.Declaring == 480 && f.Name == "m_transformedPosition"
            && f.Type == 0x0E && f.Ref == 171)
        {
            c.BasePointShared = [ReadF64(c), ReadF64(c), ReadF64(c)];
            return;
        }
        if (f.Declaring == 0x07F0 && f.Name == "m_coord"
            && f.Type == 0x07 && (f.Cnt ?? 0) == 3)
        {
            c.BasePointInternal = [ReadF64(c), ReadF64(c), ReadF64(c)];
            return;
        }
        if (c.GeoLocationExtraction)
        {
            if (f is { Declaring: 78, Name: "m_3x3", Type: 0x0D, Mods: 0x10, Cnt: 3 })
            {
                var rows = new double[9];
                for (int i = 0; i < rows.Length; i++) rows[i] = ReadF64(c);
                c.GeoRotation = rows;
                return;
            }
            if (f is { Declaring: 78, Name: "m_or", Type: 0x07, Cnt: 3 })
            {
                c.GeoOrigin = [ReadF64(c), ReadF64(c), ReadF64(c)];
                return;
            }
            if (f is { Declaring: 2237, Name: "m_symbolId", Type: 0x0E, Ref: 20 })
            {
                c.GeoSymbolId = ReadI32(c);
                return;
            }
        }
        if (depth > 40) throw Fail;
        if (!c.ExactWallCompleteInputs && TryFieldOverride(c, f, queue, depth, capture)) return;
        int hi = f.Mods >> 4;
        if (hi == 6)
        {
            string sv = ReadString(c);
            if (capture != null && CapturedStringFields.Contains(f.Name)) capture.StringFields[f.Name] = sv;
            if (capture != null && WantField(f)) capture.StringFields[FieldKey(f)] = sv;
            return;
        }
        if (hi == 1)
        {
            if (c.FamilyLineCapture != null && (f.Declaring == 1736 || f.Declaring == 1974)
                && f.Type == 0x07 && (f.Cnt ?? 0) == 3
                && f.Name is "m_origin" or "m_dirVec" or "m_center" or "m_xVec" or "m_yVec")
            {
                c.FamilyLineCapture[f.Name] =
                    [ReadF64(c), ReadF64(c), ReadF64(c)];
                return;
            }
            if (c.CaptureGridPlane && f.Declaring == 522 && f.Type == 0x07
                && (f.Cnt ?? 0) == 3
                && f.Name is "m_origin" or "m_xVec" or "m_yVec")
            {
                double[] vector = [ReadF64(c), ReadF64(c), ReadF64(c)];
                if (f.Name == "m_origin") c.GridOrigin = vector;
                else if (f.Name == "m_xVec") c.GridXVector = vector;
                else c.GridYVector = vector;
                return;
            }
            // DatumPlane owns the exact authored endpoints of Grid elements. Keep all
            // three vectors distinct; the IFC grid curve uses the span from freeEnd to bubbleEnd while
            // cutVec remains available for datum orientation.
            if (capture != null && f.Declaring == 1052 && f.Type == 0x07
                && (f.Cnt ?? 0) == 3
                && f.Name is "m_freeEnd" or "m_bubbleEnd" or "m_cutVec")
            {
                for (int i = 0; i < 3; i++)
                    capture.DoubleFields[$"{f.Name}@{i}"] = ReadF64(c);
                return;
            }
            // VWallDriver.m_noJoinAtEnd: u8[2] per-end allow-join flags.
            if (f.Name == "m_noJoinAtEnd" && f.Type == 0x01 && (f.Cnt ?? 0) == 2 && capture != null)
            {
                if (c.Pos + 2 > c.End) throw Fail;
                capture.Fields["noJoinAtEnd@0"] = c.D[c.Pos];
                capture.Fields["noJoinAtEnd@1"] = c.D[c.Pos + 1];
                c.Pos += 2;
                return;
            }
            // Wall location line (the SWall tail's m_pCrv GLine): origin/direction/params.
            if (c.WallLineCapture != null && f.Type == 0x07
                && f.Name is "m_origin" or "m_dirVec" or "m_endParams")
            {
                int nn = f.Cnt ?? 0;
                for (int i = 0; i < nn; i++) c.WallLineCapture[f.Name + i] = ReadF64(c);
                return;
            }
            int n = f.Cnt ?? 0;
            if (capture != null && (f.Type is 0x01 or 0x04 or 0x07 || (f.Type == 0x0E && f.Ref == 20)) && f.Mods == 0x10
                && (CaptureAllFields || NativeFieldParameters.DeclaringClasses.Contains(f.Declaring)
                    || DerivedInputDeclaringClasses.Contains(f.Declaring)))
            {
                for (int i = 0; i < n; i++)
                {
                    int before = c.Pos;
                    ReadScalar(c, f, queue, depth, null);
                    string key = FieldKey(f) + "@" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (!CaptureAllFields && !NativeFieldParameters.Keys.Contains(key) && !DerivedInputFieldKeys.Contains(key)) continue;
                    if (f.Type == 0x07 && c.Pos - before == 8) capture.DoubleFields[key] = BitConverter.ToDouble(c.D, before);
                    else if (f.Type is 0x04 or 0x0E && c.Pos - before == 4) capture.Fields[key] = BitConverter.ToInt32(c.D, before);
                    else if (f.Type == 0x01 && c.Pos - before == 1) capture.Fields[key] = c.D[before];
                }
                return;
            }
            for (int i = 0; i < n; i++) ReadScalar(c, f, queue, depth, null);
            return;
        }
        if (hi == 5)
        {
            uint n = ReadU32(c);
            if (capture != null && c.RootClassId == 1772
                && f is { Declaring: 1772, Name: "m_refTypeIds", Type: 4, Mods: 80, Ref: null, Cnt: null })
            {
                // The host type of an empty array is zero.
                if (n != uint.MaxValue && n > 1_000_000) throw Fail;
                int first = 0;
                for (uint index = 0; n != uint.MaxValue && index < n; index++)
                {
                    int value = ReadI32(c);
                    if (index == 0) first = value;
                }
                capture.Fields["1772:m_refTypeIds@0"] = first;
                return;
            }
            if (capture != null && f is { Declaring: 1794, Name: "m_refs", Type: 14, Mods: 81 })
            {
                // Parameter-list facts: the number of references of a family instance and the class tag of the first one.
                if (n != uint.MaxValue && n > 1_000_000) throw Fail;
                capture.Fields["1794:m_refs#"] = n == uint.MaxValue ? 0 : (int)n;
                for (uint i = 0; n != uint.MaxValue && i < n; i++)
                {
                    ReadScalar(c, f, queue, depth, null);
                    if (i == 0) capture.Fields["1794:m_refs@0"] = lastPolyClass;
                }
                return;
            }
            bool connectorRefs = capture != null && f is { Declaring: 771, Name: "m_arrRefs", Ref: 772 };
            if (connectorRefs) capture!.Fields["771:m_arrRefs#"] = 0;
            if (n == 0xFFFFFFFF) return;
            if (n > 1_000_000) throw Fail;
            if (connectorRefs)
            {
                // ConnectorId entries: element id, connector index, connection type (12 bytes each).
                capture!.Fields["771:m_arrRefs#"] = (int)n;
                for (uint i = 0; i < n; i++)
                {
                    capture.Fields[$"771:m_arrRefs@{i}:id"] = ReadI32(c);
                    capture.Fields[$"771:m_arrRefs@{i}:index"] = ReadI32(c);
                    capture.Fields[$"771:m_arrRefs@{i}:type"] = ReadI32(c);
                }
                return;
            }
            if (capture != null && CapturedCountFields.Contains(f.Name)) capture.Fields[f.Name + "#"] = (int)n;
            if (capture != null && CapturedElementIdContainers.Contains(f.Name)
                && f.Type == 0x0E && f.Ref == 20 && (f.Mods & 0x0F) == 0)
            {
                for (uint index = 0; index < n; index++)
                    capture.Fields[$"{f.Name}@{index}"] = ReadI32(c);
                return;
            }
            for (uint i = 0; i < n; i++) ReadScalar(c, f, queue, depth, null);
            return;
        }
        ReadScalar(c, f, queue, depth, capture);
    }

    private static void ReadScalar(Cursor c, TfField f, List<(ushort, uint)> queue, int depth,
        NativeParams? capture)
    {
        switch (f.Type)
        {
            case 0x01:
            {
                if (c.Pos + 1 > c.End) throw Fail;
                byte bv = c.D[c.Pos]; c.Pos += 1;
                if (c.CurrentGeomStepQueueIndex >= 0 && f.Declaring == 1793)
                    c.CutTopologyTrace?.Add($"q{c.CurrentDeferredQueueIndex}:"
                        + $"{c.CurrentDeferredClassId}:{f.Name}={bv}");
                if (capture != null && CapturedFields.Contains(f.Name)) capture.Fields[f.Name] = bv;
                if (capture != null && WantField(f)) capture.Fields[FieldKey(f)] = bv;
                if (capture != null && f.Declaring == 699 && f.Name == "m_dormant" && c.AttachBaseOrTop is 0 or 1)
                    capture.Fields[$"699:dormant@{c.AttachBaseOrTop}"] = bv;
                return;
            }
            case 0x02: case 0x03:
                if (capture != null && c.Pos < c.End && WantField(f)) capture.Fields[FieldKey(f)] = c.D[c.Pos];
                if (!c.Take(1)) throw Fail;
                return;
            case 0x04:
            {
                int v = ReadI32(c);
                if (f.Declaring == 699 && f.Name == "m_baseOrTop")
                {
                    // ColumnAttachment cell (one per column end): which end it belongs to, for the parameter-list facts.
                    c.AttachBaseOrTop = v;
                    if (capture != null && v is 0 or 1) capture.Fields[$"699:present@{v}"] = 1;
                }
                if (c.CutOutGeomStepQueueIndices != null
                    && f.Declaring == 96 && f.Name == "m_version")
                {
                    c.CutTopologyTrace?.Add($"q{c.CurrentDeferredQueueIndex}:"
                        + $"{c.CurrentDeferredClassId}:GeomStepVersion={v}");
                }
                if (c.CurrentGeomStepQueueIndex >= 0 && f.Declaring == 1793)
                    c.CutTopologyTrace?.Add($"q{c.CurrentDeferredQueueIndex}:"
                        + $"{c.CurrentDeferredClassId}:{f.Name}={v}");
                bool captureNewCurtainWallFunction = c.RootClassId is 512 or 2559
                    && f.Declaring == 513
                    && f.Name == "m_function"
                    && f.Mods == 0;
                bool captureStairNumberOfRisers = c.RootClassId == 3681
                    && f.Declaring == 3681
                    && f.Name == "m_actualNumberOfRisers"
                    && f.Mods == 0;
                if (capture != null
                    && (CapturedFields.Contains(f.Name)
                        || captureNewCurtainWallFunction
                        || captureStairNumberOfRisers))
                {
                    // m_paramId (dim label) repeats per segment: first real value wins.
                    if (f.Name != "m_paramId" || (v != -1 && !capture.Fields.ContainsKey("m_paramId")))
                        capture.Fields[f.Name] = v;
                }
                // Wall-join type: one per joined end, keyed by the pair's end index.
                if (capture != null && f.Name == "m_wallJoinType" && c.JoinEnd >= 0 && c.JoinEnd < 64)
                    capture.Fields[$"wallJoinType@{c.JoinEnd}"] = v;
                if (capture != null && WantField(f)) capture.Fields[FieldKey(f)] = v;
                return;
            }
            case 0x05: case 0x06:
                if (capture != null && c.Pos + 4 <= c.End && WantField(f))
                {
                    if (f.Type == 0x05) capture.Fields[FieldKey(f)] = BitConverter.ToInt32(c.D, c.Pos);
                    else capture.DoubleFields[FieldKey(f)] = BitConverter.ToSingle(c.D, c.Pos);
                }
                if (!c.Take(4)) throw Fail;
                return;
            case 0x07:
            {
                double dv = ReadF64(c);
                bool captureStairLength = c.RootClassId == 3681
                    && f.Declaring == 3681
                    && f.Name is "m_actualRiserHeight" or "m_actualTreadDepth"
                    && f.Mods == 0;
                if (capture != null
                    && (CapturedDoubleFields.Contains(f.Name)
                        || captureStairLength))
                    capture.DoubleFields[f.Name] = dv;
                if (capture != null && WantField(f)) capture.DoubleFields[FieldKey(f)] = dv;
                return;
            }
            case 0x0A:
                if (!c.Take(8)) throw Fail;
                return;
            case 0x08: ReadString(c); return;
            case 0x09:
                if (!c.Take(16)) throw Fail;
                return;
            case 0x0D:
            {
                int n = f.Cnt ?? 3;
                if (!c.Take(8 * n)) throw Fail;
                return;
            }
            case 0x0E:
            {
                // Element form by low nibble (orthogonal to the container high nibble):
                //   lo 3 = non-owning back-reference: bare u32, never a tag
                //   lo 1/2 = owned pointer: u32 v; v!=0 -> u16 class tag (deferred payload)
                //   lo 4 = the shared expression pointers of the variational sketch classes (VarFunction.m_args,
                //          VarSketchDimObj.m_dimValue/m_refPts, VarSketchObj.m_params): the same [handle][class tag] form
                //   lo 0: ref==20 -> bare i32 ElementId; concrete ref -> inline struct;
                //         no ref -> polymorphic handle
                int lo = f.Mods & 0x0F;
                if (lo == 3)
                {
                    uint backReference = ReadU32(c);
                    if (CaptureAllFields && f.Declaring == 96 && f.Name == "m_pElem") diagLastOwnerReference = backReference;
                    if (c.CurrentGeomStepQueueIndex >= 0
                        && f.Declaring == 96 && f.Name == "m_pElem"
                        && c.GeomStepOwnerElementIds != null)
                    {
                        c.GeomStepOwnerElementIds[c.CurrentGeomStepQueueIndex]
                            = backReference;
                        c.CutTopologyTrace?.Add($"q{c.CurrentDeferredQueueIndex}:"
                            + $"{c.CurrentDeferredClassId}:owner={backReference}");
                    }
                    return;
                }
                if (lo is 1 or 2 || (lo == 4 && c.TailWalk))
                {
                    ReadPtr(c, queue);
                    // Keyed capture of an owned pointer: 0 null, -1 external/untagged, otherwise the class tag.
                    if (capture != null && WantField(f)) capture.Fields[FieldKey(f)] = lastPolyClass;
                    return;
                }
                if (f.Ref == 20) // bare ElementId
                {
                    int v = ReadI32(c);
                    if (c.CurrentGeomStepQueueIndex >= 0
                        && (f.Declaring, f.Name) is (1793, "m_instId")
                        && c.GeomStepCuttingElementIds != null)
                    {
                        c.GeomStepCuttingElementIds[c.CurrentGeomStepQueueIndex] = v;
                        c.CutTopologyTrace?.Add($"q{c.CurrentDeferredQueueIndex}:"
                            + $"{c.CurrentDeferredClassId}:cutter={v}");
                    }
                    if (c.CurrentGeomStepQueueIndex >= 0
                        && (f.Declaring, f.Name) is (678, "m_otherId")
                        && c.GeomStepPairOtherIds != null)
                        c.GeomStepPairOtherIds[c.CurrentGeomStepQueueIndex] = v;
                    bool captureStairTypeId = c.RootClassId == 3681
                        && f.Declaring == 3681
                        && f.Name is "m_typeId" or "m_baseLevelId"
                        && f.Mods == 0;
                    if (capture != null
                        && (CapturedFields.Contains(f.Name)
                            || captureStairTypeId))
                        capture.Fields[f.Name] = v;
                    if (capture != null && WantField(f)) capture.Fields[FieldKey(f)] = v;
                    return;
                }
                if (f.Ref.HasValue) // inline struct
                {
                    var sub = SerializationFields(f.Ref.Value) ?? throw Fail;
                    // Name-based captures stay off inside inline structs; only keyed fields are merged.
                    var sink = capture != null && (CaptureAllFields || NativeFieldParameters.DeclaringClasses.Contains(f.Ref.Value)
                            || DerivedInputDeclaringClasses.Contains(f.Ref.Value))
                        ? new NativeParams() : null;
                    foreach (var sf in sub) ReadField(c, sf, queue, depth + 1, sink);
                    if (sink != null) MergeKeyed(sink, capture!);
                    return;
                }
                uint handle = ReadPoly(c, queue);
                if (capture != null && CapturedFields.Contains(f.Name))
                    capture.Fields[f.Name] = unchecked((int)handle);
                // Keyed capture of a pointer field: 0 null, -1 plain handle, otherwise the class tag of the owned object.
                if (capture != null && WantField(f)) capture.Fields[FieldKey(f)] = lastPolyClass;
                return;
            }
            default: throw Fail;
        }
    }

    /// <summary>Dimension-chain field overrides (rules D1-D6):
    /// several Dimension/DimSegInfo fields deviate from the schema-generic encoding.</summary>
    private static bool TryFieldOverride(Cursor c, TfField f, List<(ushort, uint)> queue, int depth,
        NativeParams? capture)
    {
        switch (f.Declaring, f.Name)
        {
            // A mullion's nominal axis belongs to the first reference of its own symbol, not its trimmed solid.
            case (1829, "m_refs") when IsSubclassOf(c.RootClassId, 1829) && f.Type == 14 && f.Mods == 81
                && ReferenceEquals(queue, c.RootQueue):
            {
                uint count = ReadU32(c);
                if (count > 100_000) throw Fail;
                for (uint i = 0; i < count; i++)
                {
                    int before = queue.Count;
                    ReadPtr(c, queue);
                    if (i == 0 && queue.Count == before + 1 && queue[before].Item1 == 2520)
                        c.MullionReferenceQueueIndex = before;
                }
                return true;
            }
            case (2520, "m_pCurve") when f.Type == 14 && f.Mods == 1
                && c.CurrentDeferredQueueIndex == c.MullionReferenceQueueIndex
                && ReferenceEquals(queue, c.RootQueue):
            {
                int before = queue.Count;
                ReadPtr(c, queue);
                if (queue.Count == before + 1) c.MullionCurveQueueIndex = before;
                return true;
            }
            // FamInstCurveDriver (1708) and RbsCurveDriver (3139, duct/pipe/conduit/cable tray; both base CurveDriver
            // 87): remember which queued object is the drive curve m_pCrv. Consumption is the generic owned-pointer
            // grammar.
            case (87, "m_pCrv") when f.Type == 14 && f.Mods == 1 && c.TailClassId is 1708 or 3139
                && ReferenceEquals(queue, c.RootQueue):
            {
                int before = queue.Count;
                ReadPtr(c, queue);
                if (queue.Count == before + 1) c.CurveDriverCurveQueueIndex = before;
                return true;
            }
            // GRep bounding boxes are two XYZ points (48 bytes), as the GElement graph decoder reads them;
            // the generic type-13 element size (Cnt x 8 per element) under-reads them.
            case (2008, "m_bBox" or "m_tightbBox") when f.Type == 13 && f.Mods == 16 && f.Cnt == 2:
                if (!c.Take(48)) throw Fail;
                return true;
            // BeamMiterGStep.m_miterPlane names no type; the payload is an inline Plane (Surface envelope,
            // orientation flag, origin, x and y vectors). Read as polymorphic handle it misaligned every
            // following cell of mitred beams.
            case (521, "m_miterPlane") when f.Type == 14 && f.Mods == 0 && f.Ref == null && f.Cnt == null:
            {
                if (!Schemas.TryGetValue(522, out var plane) || plane.Name != "Plane") throw Fail;
                ReadInlineStruct(c, 522, queue, depth, null);
                return true;
            }
            // BeamSystem HeldBeam: the two end points and the two end offsets are XYZ pairs as well.
            case (2147, "m_ends" or "m_offsets") when f.Type == 13 && f.Mods == 16 && f.Cnt == 2:
                if (!c.Take(48)) throw Fail;
                return true;
            // JoinGStep (677).m_affectedEdgesTags: the schema names no item type; the payload is a map of
            // [ElementId key][count][count x int edge tag] entries (the following m_healingParents/bools
            // and the next step then align exactly).
            case (677, "m_affectedEdgesTags") when f.Type == 14 && f.Mods == 80 && f.Ref == null && f.Cnt == null:
            {
                uint entries = ReadU32(c);
                if (entries == 0xFFFFFFFF) return true;
                if (entries > 100_000) throw Fail;
                for (uint entry = 0; entry < entries; entry++)
                {
                    ReadI32(c);
                    uint tags = ReadU32(c);
                    if (tags > 100_000 || !c.Take(checked((int)tags * 4))) throw Fail;
                }
                return true;
            }
            // ParamDef.m_typeId and ParamDefValue.m_specTypeId are ForgeTypeId structs (one string each); kept under
            // their own key so that the generic "45:m_typeId" stays uncaptured and the two cannot overwrite each other.
            case (301, "m_typeId") or (612, "m_specTypeId") when f.Type == 14 && f.Mods == 0 && f.Ref == 45 && capture != null:
            {
                if (SerializationFields(45) is not [{ Name: "m_typeId", Type: 8, Mods: 96 }]) throw Fail;
                string forgeId = ReadString(c);
                if (WantField(f)) capture.StringFields[FieldKey(f)] = forgeId;
                return true;
            }
            // SpatialElementCalculationPointRegenData.m_position is an XYZ struct (three doubles); read here so that they are
            // kept under the key of the field (the key of the struct's own field, "171:m_xyz", is not a captured one).
            case (3639, "m_position") when f.Type == 14 && f.Mods == 0 && f.Ref == 171 && capture != null:
            {
                if (SerializationFields(171) is not [{ Name: "m_xyz", Type: 7, Mods: 16, Cnt: 3 }]) throw Fail;
                for (int i = 0; i < 3; i++) capture.DoubleFields[FieldKey(f) + "@" + i] = ReadF64(c);
                return true;
            }
            // ParamElem.m_pParamDef: the class of the owned ParamDef object is the parameter's kind
            // (ParamDefYesNo, ParamDefMaterialBrowse, ...). Consumption is the generic owned-pointer grammar.
            case (2870, "m_pParamDef") when f.Type == 14 && (f.Mods & 0x0F) is 1 or 2 && capture != null
                && ReferenceEquals(queue, c.RootQueue):
            {
                int before = queue.Count;
                ReadPtr(c, queue);
                if (queue.Count == before + 1) capture.Fields["2870:m_pParamDef.class"] = queue[before].Item1;
                return true;
            }
            // FamilyDB: Arr<int[2]> outer manager (stride 8), nested
            // fixed-size manager (two 4-byte items), SerialSimple<int>.
            // Type13 here is an opaque POD, not XYZ. The 24-byte generic reading of a non-empty list shifted every
            // later field of the symbol by 16 bytes per entry and failed its whole parameter walk,
            // so it applies to every walk.
            case (1829, "m_dummyHostInsertionFaceTags") when c.RootClassId == 1829 && f.Type == 13 && f.Mods == 80 && f.Ref == null && f.Cnt == null:
            {
                uint count = ReadU32(c);
                if (count == uint.MaxValue) return true;
                if (count > 1_000_000 || count > (c.End - c.Pos) / 8) throw Fail;
                if (!c.Take((int)count * 8)) throw Fail;
                return true;
            }
            // The r22 schema's self-reference is not a recursive inline object:
            // RoomBoundingItem stores (host/link instance ElementId, linked ElementId).
            // Keep this 4-byte leaf local to exact RoomElem name extraction and the ExtractParams walk
            // (spaces, room tags and stair paths stopped on it in their root fields).
            case (180, "m_linkRef") when (c.ExactRoomNameExtraction || c.TailWalk)
                && f.Type == 14 && f.Mods == 0 && f.Ref == 180:
                ReadI32(c);
                return true;
            // BaseRailing persists an empty override slot as a tagged null
            // [handle:u32=0][classTag:u16=0]. The generic polymorphic-container grammar
            // consumes only the handle for null values, shifting every following field by
            // two bytes. Non-null entries retain the existing ReadPoly grammar exactly.
            case (485, "m_continuousRailOverrides"):
            {
                uint count = ReadU32(c);
                if (count == 0xFFFFFFFF) return true;
                if (count > 100_000) throw Fail;
                for (uint index = 0; index < count; index++)
                {
                    int itemStart = c.Pos;
                    uint handle = ReadU32(c);
                    if (handle == 0)
                    {
                        if (c.Pos + 2 > c.End) throw Fail;
                        ushort classTag = BitConverter.ToUInt16(c.D, c.Pos);
                        c.Pos += 2;
                        if (classTag != 0) throw Fail;
                        continue;
                    }

                    c.Pos = itemStart;
                    ReadPoly(c, queue);
                }
                return true;
            }
            // VerticalExtensionOfLayersGStep persists exactly one (int, ElementId)
            // pair for each side. The R22 Formats metadata marks both members as
            // dynamic containers, but the payload contains the two class-322 records
            // inline and has no count prefix.
            case (4098, "m_otherId0" or "m_otherId1")
                when c.CutOutGeomStepQueueIndices != null:
            {
                if (!c.CountedOtherIds)
                {
                    ReadInlineStruct(c, 322, queue, depth, capture);
                    return true;
                }
                uint pairs = ReadU32(c);
                if (pairs != 0xFFFFFFFF && (pairs > 100_000 || !c.Take(checked((int)pairs * 8)))) throw Fail;
                return true;
            }
            // JoinEndGStep payloads with an empty reverse history end after
            // m_geomJoined. The current Formats schema also describes m_realJoined and
            // m_exposedFaceMap, but those members are absent from that persisted variant.
            // A non-empty reverse history selects the later payload that includes them.
            case (2256, "m_realJoined" or "m_exposedFaceMap")
                when c.CutOutGeomStepQueueIndices != null
                    && c.CurrentDeferredClassId == 2256
                    && !c.CurrentJoinEndHasReverseHistory
                    && !c.JoinEndFullTail:
                return true;
            // When the exposed face map is persisted it is [count][count x (face key, ElementId, int)]: 12-byte entries
            // that the Formats metadata leaves out (read as 4-byte ones the cursor lands inside the next table).
            case (2256, "m_exposedFaceMap") or (977, "m_mergedFaceInfo")
                when c.LayoutSearch && c.CutOutGeomStepQueueIndices != null && c.CurrentDeferredClassId == f.Declaring:
            {
                uint count = ReadU32(c);
                if (count != 0xFFFFFFFF && (count > 100_000 || !c.Take(checked((int)count * 12)))) throw Fail;
                return true;
            }
            // RbsCurveGStep (the single non-BRep step of duct/pipe/conduit/cable tray curves) persists the four
            // history tables counted, with the inline entry types the Formats metadata leaves out: face 16 bytes,
            // curve 24, edge 20 and reverse edge 20 (m_id plus the 3/5/4 history ints). The generic reader takes
            // the entries for polymorphic handles and fails on the first FFFFFFFF. RbsFlexCurveGStep (3176), the
            // second non-BRep step of a flexible pipe curve, has the same prefix and no fields of its own.
            case (97, "m_faceHistTable" or "m_curveHistTableSet" or "m_edgeHistTable" or "m_edgeHistTableReverse")
                when c.CurrentDeferredClassId is 3140 or 3176:
            {
                uint count = ReadU32(c);
                if (count == 0xFFFFFFFF) return true;
                int recordSize = f.Name switch
                {
                    "m_faceHistTable" => 16,
                    "m_curveHistTableSet" => 24,
                    _ => 20,
                };
                if (count > 100_000 || !c.Take(checked((int)count * recordSize))) throw Fail;
                return true;
            }
            // GHermiteSpline.m_NodeArray (the drive curve of a flexible pipe): the Formats metadata names no element type;
            // each node is seven doubles - the parameter (the chord length from the first node), the point and the
            // tangent (node 1 parameter = distance between the two points).
            case (2020, "m_NodeArray") when f.Type == 14 && f.Mods == 80 && f.Ref == null && f.Cnt == null:
            {
                uint nodes = ReadU32(c);
                if (nodes == 0xFFFFFFFF) return true;
                if (nodes > 100_000) throw Fail;
                for (uint node = 0; node < nodes; node++)
                {
                    if (c.HermiteNodes == null) { if (!c.Take(56)) throw Fail; continue; }
                    var values = new double[7];
                    for (int k = 0; k < values.Length; k++) values[k] = ReadF64(c);
                    c.HermiteNodes.Add(values);
                }
                return true;
            }
            // Cached JoinEnd regeneration steps store the reverse edge history as
            // [high-water id][reserved=0][entry count][EdgeHistEntry...]. An empty
            // table is the single zero/FFFFFFFF value. Formats exposes only the inner
            // entry type, so treating the high-water id as the count overreads the
            // following GeomStep fields.
            case (97, "m_edgeHistTableReverse")
                when c.CutOutGeomStepQueueIndices != null
                    && c.CurrentDeferredClassId == 2256:
            {
                int fieldStart = c.Pos;
                if (c.CurrentTrackedReverseHistoryEnd >= fieldStart)
                {
                    c.CurrentJoinEndHasReverseHistory =
                        c.CurrentTrackedReverseHistoryEnd > fieldStart;
                    c.SawJoinEndReverseHistory |= c.CurrentJoinEndHasReverseHistory;
                    c.Pos = c.CurrentTrackedReverseHistoryEnd;
                    c.CutTopologyTrace?.Add($"q{c.CurrentDeferredQueueIndex}:"
                        + $"{c.CurrentDeferredClassId}:m_edgeHistTableReverse"
                        + $"@{fieldStart:X}->tracked-{c.Pos:X}");
                    return true;
                }
                uint highWaterId = ReadU32(c);
                c.CutTopologyTrace?.Add($"q{c.CurrentDeferredQueueIndex}:"
                    + $"{c.CurrentDeferredClassId}:m_edgeHistTableReverse"
                    + $"@{fieldStart:X}=id{highWaterId}");
                if (highWaterId is 0 or 0xFFFFFFFF) return true;
                uint reserved = ReadU32(c);
                uint count = ReadU32(c);
                if (reserved != 0 || count > 100_000
                    || !c.Take(checked((int)count * 20)))
                    throw Fail;
                c.CurrentJoinEndHasReverseHistory = true;
                c.SawJoinEndReverseHistory = true;
                return true;
            }
            // Cached cut-phase steps use three exact persisted history layouts. Legacy
            // face tables are zero-id terminated, with or without a leading high-water
            // id, and have 20-byte curve records. Counted FaceHistEntry tables retain
            // the current 24-byte CurveHistEntry records. Edge histories independently
            // use either counted EdgeHistEntry records or the terminated legacy order;
            // the reverse table is either omitted or an explicit empty marker.
            case (97, "m_faceHistTable")
                when c.CurrentGeomStepQueueIndex >= 0
                    || c.CurrentDeferredClassId == 2256 && !c.CountedHistory:
                ReadTrackedFaceHistoryTable(c);
                return true;
            case (97, "m_curveHistTableSet")
                when c.CurrentGeomStepQueueIndex >= 0
                    || c.CurrentDeferredClassId == 2256 && !c.CountedHistory:
            {
                uint count = ReadU32(c);
                if (count == 0xFFFFFFFF) return true;
                if (count > 100_000
                    || !c.Take(checked((int)count * c.CurrentTrackedCurveRecordSize)))
                    throw Fail;
                return true;
            }
            case (97, "m_edgeHistTable")
                when c.CurrentGeomStepQueueIndex >= 0
                    || c.CurrentDeferredClassId == 2256 && !c.CountedHistory:
                ReadTrackedEdgeHistoryTable(c);
                return true;
            case (97, "m_edgeHistTableReverse")
                when c.CurrentGeomStepQueueIndex >= 0:
                if (c.CurrentTrackedReverseHistoryPresent)
                {
                    if (c.CurrentTrackedReverseHistoryEnd < c.Pos
                        || c.CurrentTrackedReverseHistoryEnd > c.End)
                        throw Fail;
                    c.Pos = c.CurrentTrackedReverseHistoryEnd;
                }
                return true;
            case (97, var historyField) when c.CutOutGeomStepQueueIndices != null
                && historyField is "m_faceHistTable" or "m_curveHistTableSet"
                    or "m_edgeHistTable":
            {
                int fieldStart = c.Pos;
                uint count = ReadU32(c);
                c.CutTopologyTrace?.Add($"q{c.CurrentDeferredQueueIndex}:"
                    + $"{c.CurrentDeferredClassId}:{historyField}"
                    + $"@{fieldStart:X}=n{count}");
                if (count == 0xFFFFFFFF) return true;
                if (count > 100_000) throw Fail;
                int recordSize = historyField switch
                {
                    "m_faceHistTable" => 16,
                    "m_curveHistTableSet" => 24,
                    _ => 20,
                };
                if (!c.Take(recordSize * (int)count)) throw Fail;
                return true;
            }
            case (1830, "m_pCutLoops"):
            {
                uint count = ReadU32(c);
                if (count > 100_000) throw Fail;
                for (uint index = 0; index < count; index++)
                {
                    int before = queue.Count;
                    ReadPtr(c, queue);
                    if (c.FamilyCutLoopQueueIndices != null)
                    {
                        if (queue.Count != before + 1) throw Fail;
                        c.FamilyCutLoopQueueIndices.Add(before);
                    }
                }
                return true;
            }
            case (1830, "m_cutDirs"):
            {
                uint count = ReadU32(c);
                if (count > 100_000) throw Fail;
                for (uint index = 0; index < count; index++)
                {
                    double[] direction = [ReadF64(c), ReadF64(c), ReadF64(c)];
                    if (c.ExactFamilyCutInputs && direction.Any(value => !double.IsFinite(value))) throw Fail;
                    c.FamilyCutDirections?.Add(direction);
                }
                return true;
            }
            case (1830, "m_cutIndices"):
            {
                uint count = ReadU32(c);
                if (count > 100_000) throw Fail;
                for (uint index = 0; index < count; index++)
                {
                    int cutIndex = ReadI32(c);
                    c.FamilyCutIndices?.Add(cutIndex);
                }
                return true;
            }
            // Surface serializes its finite UV envelope inline as Umin,Vmin,Umax,Vmax;
            // retain it when the current operation is extracting the Grid datum Plane.
            case (523, "m_Envelope"):
            {
                double[] envelope = [ReadF64(c), ReadF64(c), ReadF64(c), ReadF64(c)];
                if (c.CaptureGridPlane) c.GridEnvelope = envelope;
                return true;
            }
            // GeomPositioningCell.m_locationLineOffsetData is an inline LocationLineOffsetData the schema does not
            // reference: owner back-reference u32, two XYZ offsets, three int[2], an ElementId[2] and a byte[2]
            // (86 bytes).
            case (2080, "m_locationLineOffsetData") when f.Type == 14 && f.Mods == 0 && f.Ref == null && f.Cnt == null:
                if (!HasLocationLineOffsetDataSchema() || !c.Take(86)) throw Fail;
                return true;
            // Pick.m_elemPicks stores ElemPick items inline (the schema omits the item reference); ElemPick's
            // m_elemId and m_foreignRef are bare signed ElementIds like GeomRef.m_foreignElemIdRef.
            case (264, "m_elemPicks") when f.Type == 14 && f.Mods == 80 && f.Ref == null && f.Cnt == null:
            {
                if (c.Pos + 4 <= c.End && BitConverter.ToUInt32(c.D, c.Pos) == 0xFFFFFFFF)
                {
                    c.Pos += 4; // empty container
                    return true;
                }
                ReadInlineContainer(c, 265, queue, depth, capture);
                return true;
            }
            case (265, "m_elemId" or "m_foreignRef") when f.Type == 14 && f.Mods == 0 && (f.Ref == null || f.Ref == 265):
                if (!c.Take(4)) throw Fail;
                return true;
            // Pick.m_ownerDBViewId is a bare signed ElementId; the R22 schema points it back at Pick itself, so the
            // generic reading would recurse into nested Picks.
            case (264, "m_ownerDBViewId") when f.Type == 14 && f.Mods == 0 && (f.Ref == null || f.Ref == 264):
                if (!c.Take(4)) throw Fail;
                return true;
            // PointElementReference.m_alternateHostGRef is an inline GeomRef the R22 schema leaves untyped. Read as a
            // polymorphic handle it consumed 4 of its 33 bytes (empty tag list) and shifted the rest of PointOnSketchData.
            case (139, "m_alternateHostGRef") when c.TailWalk && f.Type == 14 && f.Mods == 0 && f.Ref == null && f.Cnt == null:
                if (!HasGeomRefSchema()) throw Fail;
                ReadInlineStruct(c, 140, queue, depth, null);
                return true;
            // StairsSupport.m_hostRef is an inline StairsSupportGeomRef: four f64 (extensions and vertical offsets), then the
            // GeomRef its m_geomRef names (the schema points that field back at 3728). 65 bytes with an empty tag list, not a
            // four-byte handle: the deferred objects of every stair stringer started 61 bytes too early.
            case (3727, "m_hostRef") when c.TailWalk && f.Type == 14 && f.Mods == 0 && f.Ref == null && f.Cnt == null:
                if (!HasStairsSupportGeomRefSchema() || !HasGeomRefSchema() || !c.Take(32)) throw Fail;
                ReadInlineStruct(c, 140, queue, depth, null);
                return true;
            // Connector.m_arrRefs holds ConnectorId entries inline (ElementId, index, connector type = 12 bytes, as in
            // MEPAnalyticalNode.m_connectorIds); the schema names no item class.
            case (771, "m_arrRefs") when c.TailWalk && f.Type == 14 && f.Mods == 80 && f.Ref == null && f.Cnt == null:
                if (!HasConnectorIdSchema()) throw Fail;
                if (c.Pos + 4 <= c.End && BitConverter.ToUInt32(c.D, c.Pos) == 0xFFFFFFFF)
                {
                    c.Pos += 4; // empty container
                    return true;
                }
                ReadInlineContainer(c, 772, queue, depth, null);
                return true;
            // CGridDivideFaceGStep.m_cellMap holds pair<CGCellCoords, CGCellContents> entries inline: the cell's two grid
            // coordinates, then edge tags, face tag and five ElementIds; the schema leaves the container untyped and
            // points pair.second back at the pair.
            case (590, "m_cellMap") when c.TailWalk && f.Type == 14 && f.Mods == 80 && f.Ref == null && f.Cnt == null:
                if (!HasCellMapSchema()) throw Fail;
                if (c.Pos + 4 <= c.End && BitConverter.ToUInt32(c.D, c.Pos) == 0xFFFFFFFF)
                {
                    c.Pos += 4; // empty container
                    return true;
                }
                ReadInlineContainer(c, 591, queue, depth, null);
                return true;
            case (591, "second") when c.TailWalk && f.Type == 14 && f.Mods == 0 && f.Ref == 591:
                ReadInlineStruct(c, 583, queue, depth, null);
                return true;
            // The same step's m_originalToEdgeSegs is a pair<int, CEdgeSegSetWrapper> container (an int key and a counted
            // list of CEdgeSeg: start, end, edge tag = 2 f64 + i32).
            case (590, "m_originalToEdgeSegs") when c.TailWalk && f.Type == 14 && f.Mods == 80 && f.Ref == null && f.Cnt == null:
                if (!HasEdgeSegMapSchema()) throw Fail;
                if (c.Pos + 4 <= c.End && BitConverter.ToUInt32(c.D, c.Pos) == 0xFFFFFFFF)
                {
                    c.Pos += 4; // empty container
                    return true;
                }
                ReadInlineContainer(c, 592, queue, depth, null);
                return true;
            case (592, "second") when c.TailWalk && f.Type == 14 && f.Mods == 0 && f.Ref == 592:
                ReadInlineStruct(c, 582, queue, depth, null);
                return true;
            // WallJoinTweakGStep.m_affectedEdgesTags is typed as pair<ElementId, TweakEffectedTagSetWrapper>, whose second half
            // the schema leaves untyped; the wrapper is an int[] (count + ints), the layout JoinGStep's untyped map also has.
            case (679, "second") when c.TailWalk && f.Type == 14 && f.Mods == 0 && f.Ref == null && f.Cnt == null:
                if (!HasTweakEffectedTagSetSchema()) throw Fail;
                ReadInlineStruct(c, 680, queue, depth, null);
                return true;
            // RoomCreate3dGeometryGStep.m_faceOffsetMap holds pair<int, RoomFaceOffsets> entries inline (int + double[4] =
            // 36 bytes); the schema names neither the item class nor the type of pair.second.
            case (3468, "m_faceOffsetMap") when c.TailWalk && f.Type == 14 && f.Mods == 80 && f.Ref == null && f.Cnt == null:
                if (!HasRoomFaceOffsetSchema()) throw Fail;
                if (c.Pos + 4 <= c.End && BitConverter.ToUInt32(c.D, c.Pos) == 0xFFFFFFFF)
                {
                    c.Pos += 4; // empty container
                    return true;
                }
                ReadInlineContainer(c, 3469, queue, depth, null);
                return true;
            case (3469, "second") when c.TailWalk && f.Type == 14 && f.Mods == 0 && f.Ref == null && f.Cnt == null:
                ReadInlineStruct(c, 3470, queue, depth, null);
                return true;
            // ElectricalFamInstDesignPropertyManager.m_LargestMotorMap is an inline pair<ElementId, pair<ElementId, double>>:
            // i32, i32, f64. The schema names no type and 1289.second points
            // back at 1289, so the generic reading takes a polymorphic handle and stops the walk of every electrical instance.
            case (1285, "m_LargestMotorMap") when c.TailWalk && f.Type == 14 && f.Mods == 0 && f.Ref == null && f.Cnt == null:
                if (!HasLargestMotorPairSchema() || !c.Take(16)) throw Fail;
                return true;
            // pair<ElementId, ElementId>.second is a bare ElementId; the schema points it back at the pair, which the
            // generic reading would recurse into (DocumentUnitsLink applies the same correction to its own walk).
            case (24, "second") when c.TailWalk && f.Type == 14 && f.Mods == 0 && f.Ref == 24:
                if (!HasElementIdPairSchema() || !c.Take(4)) throw Fail;
                return true;
            // GeomRef stores the foreign element reference as a bare signed ElementId.
            // The schema lacks its ElementId ref annotation, so treating -1 as the
            // polymorphic-object sentinel would consume the following geom tag.
            case (140, "m_foreignElemIdRef"):
                if (!c.Take(4)) throw Fail;
                return true;
            // FamSymHandleMgr's map entries are inline pair<GeomRef,
            // FamSymHandleData> records (class 1730); the schema omits that ref.
            case (1729, "m_handleMap"):
                ReadInlineContainer(c, 1730, queue, depth, capture);
                return true;
            case (1730, "second"):
                ReadInlineStruct(c, 1727, queue, depth, capture);
                return true;
            // GTagDirFlagsPair encodes its compact per-axis direction flags as 3*u8.
            case (1758, "m_flags"):
                if (!c.Take(3)) throw Fail;
                return true;
            // pair<int, ElementId>.second is the bare ElementId value.
            case (322, "second"):
                if (!c.Take(4)) throw Fail;
                return true;
            // FamilyParams stores NamedParam values and its geometry-reference table
            // inline; the schema omits both concrete item references.
            case (714, "m_params"):
                ReadInlineContainer(c, 715, queue, depth, capture);
                return true;
            case (714, "m_geomRefHandles"):
                ReadInlineStruct(c, 716, queue, depth, capture);
                return true;
            case (716, "m_offsetGeomMap"):
                ReadInlineContainer(c, 717, queue, depth, capture);
                return true;
            // Outline.m_minmax is a fixed array of two XYZ POD values.
            case (297, "m_minmax"):
                if (!c.Take(2 * 3 * 8)) throw Fail;
                return true;
            // DatumPlane stores two reference points, each as an XYZ POD (2 x 3 f64).
            // The generic fixed-array path otherwise applies Cnt twice to type 0x0D and
            // consumes only four doubles, desynchronizing every Grid deferred object.
            case (1052, "m_refPointsForNewViews"):
                if (!c.Take(2 * 3 * 8)) throw Fail;
                return true;
            // WallType/CurtainRoofAttributes.m_bordMullions is a 16-byte t13 POD with Cnt=2. The generic
            // fixed-array path applies Cnt both as the array length and as the POD width,
            // consuming 32 bytes and skipping past the authored m_function field.
            case (513, "m_bordMullions") when f.Type == 0x0D
                && f.Mods == 0x10 && f.Cnt == 2:
            case (908, "m_bordMullions") when f.Type == 0x0D
                && f.Mods == 0x10 && f.Cnt == 2:
                if (!c.Take(2 * 8)) throw Fail;
                return true;
            // R22 leaves the current CompoundStructure segment-reference vector's
            // record type unnamed. It persists the same three i32 fields described
            // by SegmentIdAndRefFaceKey, not polymorphic object handles. Consuming
            // one u32 per entry shifts the following Pre40 vector and deferred FIFO.
            case (737, "m_segRefFaceKeys") when f.Type == 0x0E
                && f.Mods == 0x50 && f.Ref == null:
            {
                var records = SerializationFields(739);
                if (!Schemas.TryGetValue(739, out var recordClass)
                    || recordClass.Name != "SegmentIdAndRefFaceKey"
                    || records is not { Count: 3 }
                    || !HasExactSchemaField(records, 739, "m_id", 0x04, 0, null)
                    || !HasExactSchemaField(records, 739, "m_key0", 0x04, 0, null)
                    || !HasExactSchemaField(records, 739, "m_key1", 0x04, 0, 739))
                    throw Fail;
                uint count = ReadU32(c);
                if (count == uint.MaxValue) return true;
                if (count > 100_000 || count * 12L > c.End - c.Pos) throw Fail;
                for (uint index = 0; index < count; index++)
                    foreach (var record in records)
                        ReadField(c, record, queue, depth + 1, null);
                return true;
            }
            case (0xA0, "m_ArrSegInfo"):                      // D1: [count] x inline DimSegInfo
                ReadInlineContainer(c, 0xA1, queue, depth, capture);
                return true;
            case (0xA0, "m_ArrEqualityFormulaInfo_DimEqSegInfoArr"): // D3: inline DimEqSegInfoArr
                ReadInlineStruct(c, 0xA5, queue, depth);
                return true;
            case (0x9F, "m_witnessRefs"):                     // D4
                ReadInlineContainer(c, 0xA7, queue, depth);
                return true;
            case (0x162, "m_witnessRefs"):                    // D4 (radial chain)
                ReadInlineContainer(c, 0x163, queue, depth);
                return true;
            case (0xA1, "m_values"):                          // D2: u32 hdr + 3 x inline DimSegValue
            {
                uint hdr = ReadU32(c);
                // First DimSegValue.m_value = the segment's stored length.
                // Peek before the generic struct walk.
                if (capture != null && hdr != 0xFFFFFFFF && c.Pos + 8 <= c.End)
                {
                    double segVal = BitConverter.ToDouble(c.D, c.Pos);
                    if (!double.IsNaN(segVal) && Math.Abs(segVal) < 1e8)
                    {
                        capture.DoubleFields.TryGetValue("dimSegSum", out double acc);
                        capture.DoubleFields["dimSegSum"] = acc + segVal;
                        capture.Fields.TryGetValue("dimSegN", out int n0);
                        capture.Fields["dimSegN"] = n0 + 1;
                    }
                }
                if (hdr == 0xFFFFFFFF)
                {
                    if (c.Pos + 2 > c.End) throw Fail;
                    ushort tag = BitConverter.ToUInt16(c.D, c.Pos); c.Pos += 2;
                    if (!Schemas.ContainsKey(tag)) throw Fail;
                    queue.Add((tag, 0xFFFFFFFF));
                }
                else if (hdr > 1000) throw Fail;
                for (int i = 0; i < 3; i++) ReadInlineStruct(c, 0xA3, queue, depth);
                return true;
            }
            case (0xA1, "m_id"):                              // D6: plain i32 (may be -1)
            case (0x9F, "m_lastUsedId"):
                if (!c.Take(4)) throw Fail;
                return true;
            case (0xA7, "m_oldRefSegEnds"):                   // D4: cnt x XYZ (24 bytes)
            case (0x105F, "m_oldRefSegEnds"):
                if (!c.Take(24 * (f.Cnt ?? 2))) throw Fail;
                return true;
            case (_, "m_OwningDimension"):                    // D2c: bare u32 self-ref
                if (!c.Take(4)) throw Fail;
                return true;

            // CurveElem / arc geometry (rules R1-R7)
            case (0x346, "m_referenceType"):                 // R1: field i32 + a hidden extra i32
                if (!c.Take(8)) throw Fail;
                return true;
            case (0x346, "m_famElemVisibility"):             // inline {m_flags} = BUILDING_CURVE_GSTYLE
            {
                int flags = ReadI32(c);
                if (c.GStyle == int.MinValue) c.GStyle = flags;
                return true;
            }
            case (0x4DE, "m_GInfo"):                          // R2: null or inline GInfo
            {
                // The GNode layout is four raw32 fields, also in the own FamilySymbol FIFO.
                if (c.ExactFamilyCutInputs || c.TailWalk)
                {
                    if (!c.Take(16)) throw Fail;
                    return true;
                }
                if (c.CutOutGeomStepQueueIndices != null)
                {
                    if (c.Pos + 16 > c.End) throw Fail;
                    int categoryId = BitConverter.ToInt32(c.D, c.Pos + 8);
                    if (categoryId < -3_000_000) throw Fail;
                    c.Pos += 16;
                    return true;
                }
                if (c.Pos + 4 > c.End) throw Fail;
                uint inlineFlags = c.Pos + 16 <= c.End
                    ? BitConverter.ToUInt32(c.D, c.Pos + 12) : 0;
                if ((inlineFlags & 0xFF00FF) == 0x080004)
                {
                    if (!c.Take(8)) throw Fail;               // tag, control
                    int gstyle = ReadI32(c);
                    uint gflags = ReadU32(c);
                    if (c.GStyle == int.MinValue) c.GStyle = gstyle;
                    return true;
                }
                if (BitConverter.ToUInt32(c.D, c.Pos) != 0) throw Fail;
                c.Pos += 4;
                return true;
            }
            case (0x61, "m_curveHistTableSet"):              // R3: [count] x 6 i32
            {
                uint n = ReadU32(c);
                if (n > 4096 || !c.Take((int)n * 24)) throw Fail;
                return true;
            }
            case (0x59, "m_info"):                            // R4: [count] x (i32,i32,u8)
            {
                uint n = ReadU32(c);
                if (n > 4096 || !c.Take((int)n * 9)) throw Fail;
                return true;
            }
            // CDJoinInfo uses inline ElementIdIndexPairSet, not a
            // polymorphic object handle. Its m_data is a counted ElementIdPlusIndex
            // collection; an empty count hides the missing concrete refs.
            case (89, "m_cleanData"):
                ReadCDJoinInfoCleanData(c, queue, depth);
                return true;
            case (0x57, "m_controlJoinsSet"):                // R5: [count] x (inline 0x59 + u32)
            {
                // Inline CDJoinInfo (0x59) = m_cleanData (inline counted id/index pairs) + m_info ([cnt] x EndInfo
                // records (m_elemId:i32, m_joinedEnd:i32, m_bTangent:u8), the JOINED
                // NEIGHBOR ids) + m_wallJoinType (i32); the trailing u32 is the wall-end
                // control index the join belongs to. Captured for wall-join analytics.
                uint n = ReadU32(c);
                if (n > 4096) throw Fail;
                Span<(int Elem, int JEnd)> infoBuffer = stackalloc (int, int)[8];
                for (uint i = 0; i < n; i++)
                {
                    ReadCDJoinInfoCleanData(c, queue, depth);  // m_cleanData: ElementIdIndexPairSet
                    uint ni = ReadU32(c);                     // m_info count (R4 layout)
                    if (ni > 4096) throw Fail;
                    Span<(int Elem, int JEnd)> infos = ni <= 8 ? infoBuffer[..(int)ni] : new (int, int)[(int)ni];
                    for (uint k = 0; k < ni; k++)
                    {
                        int elem = ReadI32(c);
                        int jend = ReadI32(c);
                        if (!c.Take(1)) throw Fail;           // m_bTangent
                        infos[(int)k] = (elem, jend);
                    }
                    int jt = ReadI32(c);                      // m_wallJoinType
                    int end = ReadI32(c);                     // pair key: wall-end index
                    if (c.ControlJoins != null)
                        foreach (var (elem, jend) in infos)
                            c.ControlJoins.Add(new WallEndJoin(end, elem, jend, jt));
                    if (capture != null && end >= 0 && end < 64)
                    {
                        capture.Fields[$"wallJoinType@{end}"] = jt;
                        for (int k = 0; k < infos.Length && k < 4; k++)
                        {
                            capture.Fields[$"joinElem@{end}_{k}"] = infos[k].Elem;
                            capture.Fields[$"joinEnd@{end}_{k}"] = infos[k].JEnd;
                        }
                    }
                }
                return true;
            }
            case (0x57, "m_sideAlignments"):                 // R6: [count] x 20 bytes
            {
                uint n = ReadU32(c);
                if (n > 4096 || !c.Take((int)n * 20)) throw Fail;
                return true;
            }
            case (0x6C9, "m_endParams"):                      // GCurve: 2 f64 end params
                c.Ep0 = ReadF64(c); c.Ep1 = ReadF64(c);
                return true;
            case (0x7B6, "m_radius"):
                c.Radius = ReadF64(c);
                return true;
            case (0x7DA, "m_xLen"):
                c.XLen = ReadF64(c);
                return true;
            case (0x7DA, "m_yLen"):
                c.YLen = ReadF64(c);
                return true;
        }
        return false;
    }

    private static void ReadTrackedFaceHistoryTable(Cursor c)
    {
        int fieldStart = c.Pos;
        uint value = ReadU32(c);
        if (value == 0 && TrySelectZeroPrefixedFaceTable(c)) return;
        if (value is 0 or 0xFFFFFFFF) return;

        int countEnd = value <= 100_000
            ? checked(c.Pos + (int)value * 16)
            : -1;
        int prefixedTerminatedEnd = FindTerminatedHistoryTableEnd(c, c.Pos, 16);
        int unprefixedTerminatedEnd = FindTerminatedHistoryTableEnd(c, fieldStart, 16);
        var candidates = new Dictionary<(int End, int CurveSize),
            (string Layout, int TailStart)>();
        bool countTailValid = TryGetTrackedHistoryTail(c, countEnd, 24,
            out int countTailStart);
        AddTrackedFaceCandidate(c, candidates, countEnd, 24, "count",
            IsCountHistoryTable(c, c.Pos, value, 16)
                && countTailValid, countTailStart);
        bool prefixedTailValid = TryGetTrackedHistoryTail(c,
            prefixedTerminatedEnd, 20, out int prefixedTailStart);
        AddTrackedFaceCandidate(c, candidates, prefixedTerminatedEnd, 20,
            "prefixed-terminated",
            prefixedTailValid, prefixedTailStart);
        bool unprefixedTailValid = TryGetTrackedHistoryTail(c,
            unprefixedTerminatedEnd, 20, out int unprefixedTailStart);
        AddTrackedFaceCandidate(c, candidates, unprefixedTerminatedEnd, 20,
            "terminated",
            unprefixedTailValid, unprefixedTailStart);
        if (candidates.Count > 1)
        {
            int earliestTail = candidates.Min(candidate => candidate.Value.TailStart);
            foreach (var key in candidates
                         .Where(candidate => candidate.Value.TailStart != earliestTail)
                         .Select(candidate => candidate.Key).ToList())
                candidates.Remove(key);
        }
        // Counted and legacy-terminated carvings that reach the same step tail consume the same bytes; as for the edge
        // table, the current counted format wins.
        if (c.TailWalk && candidates.Count > 1
            && candidates.Count(candidate => candidate.Value.Layout.StartsWith("count", StringComparison.Ordinal)) == 1)
            foreach (var key in candidates
                         .Where(candidate => !candidate.Value.Layout.StartsWith("count", StringComparison.Ordinal))
                         .Select(candidate => candidate.Key).ToList())
                candidates.Remove(key);
        if (candidates.Count > 1 && c.LayoutSearch)
        {
            // Layouts that segment the same bytes differently and reach the same step tail: a counted table is the one
            // written (a terminated 20-byte reading of 16-byte records only coincides with it).
            var counted = candidates.Where(candidate => candidate.Value.Layout.StartsWith("count", StringComparison.Ordinal))
                .Select(candidate => candidate.Key).ToList();
            if (counted.Count == 1)
                foreach (var key in candidates.Keys.Where(key => key != counted[0]).ToList())
                    candidates.Remove(key);
        }
        c.CutTopologyTrace?.Add($"q{c.CurrentDeferredQueueIndex}:"
            + $"{c.CurrentDeferredClassId}:tracked-face-candidates@{fieldStart:X}:"
            + string.Join(",", candidates.Select(candidate =>
                $"{candidate.Value.Layout}@{candidate.Key.End:X}/c{candidate.Key.CurveSize}"
                + $"/tail{candidate.Value.TailStart:X}")));
        if (candidates.Count != 1) throw Fail;
        var selected = candidates.Single();
        c.Pos = selected.Key.End;
        c.CurrentTrackedCurveRecordSize = selected.Key.CurveSize;
        c.CutTopologyTrace?.Add($"q{c.CurrentDeferredQueueIndex}:"
            + $"{c.CurrentDeferredClassId}:tracked-face@{fieldStart:X}="
            + selected.Value.Layout);
    }

    /// <summary>A face history table persisted as [0][count][FaceHistEntry...]: after the zero a count and counted entries
    /// follow where an empty table would be followed by the curve table. It is taken only when the tail after the entries
    /// is a valid step tail and the tail right after the zero is not.</summary>
    private static bool TrySelectZeroPrefixedFaceTable(Cursor c)
    {
        if (c.Pos + 4 > c.End) return false;
        uint count = BitConverter.ToUInt32(c.D, c.Pos);
        if (count is 0 or > 100_000 || !IsCountHistoryTable(c, c.Pos + 4, count, 16)) return false;
        if (TryGetTrackedHistoryTail(c, c.Pos, 20, out _)) return false;
        int end = c.Pos + 4 + (int)count * 16;
        foreach (int curveSize in new[] { 24, 20 })
            if (TryGetTrackedHistoryTail(c, end, curveSize, out _))
            {
                c.Pos = end;
                c.CurrentTrackedCurveRecordSize = curveSize;
                c.CutTopologyTrace?.Add($"q{c.CurrentDeferredQueueIndex}:{c.CurrentDeferredClassId}:tracked-face=zero-prefixed-count/c{curveSize}");
                return true;
            }
        return false;
    }

    private static void AddTrackedFaceCandidate(
        Cursor c,
        Dictionary<(int End, int CurveSize), (string Layout, int TailStart)> candidates,
        int end, int curveSize, string layout, bool valid, int tailStart)
    {
        if (!valid) return;
        // Empty curve histories consume only their count/sentinel, independently
        // of the 20/24-byte record layout. Coincident face ends and step tails
        // therefore represent the same skipped span, not competing boundaries.
        if (BitConverter.ToUInt32(c.D, end) is 0 or 0xFFFFFFFF)
            curveSize = 20;
        var key = (end, curveSize);
        if (candidates.TryGetValue(key, out var existing))
        {
            if (existing.TailStart != tailStart) throw Fail;
            candidates[key] = (existing.Layout + "+" + layout, existing.TailStart);
        }
        else
            candidates[key] = (layout, tailStart);
    }

    private static int FindTerminatedHistoryTableEnd(Cursor c, int start, int recordSize)
    {
        int position = start;
        var ids = new HashSet<int>();
        for (int count = 0; count < 100_000; count++)
        {
            if (position + recordSize > c.End) return -1;
            int resultId = BitConverter.ToInt32(c.D, position + recordSize - 4);
            position += recordSize;
            if (resultId == 0) return position;
            if (resultId < 0 || !ids.Add(resultId)) return -1;
        }
        return -1;
    }

    private static bool IsCountHistoryTable(Cursor c, int start, uint count,
        int recordSize)
    {
        if (count > 100_000) return false;
        long end = (long)start + count * (long)recordSize;
        if (start < 0 || end > c.End) return false;
        var ids = new HashSet<int>();
        for (uint index = 0; index < count; index++)
        {
            int id = BitConverter.ToInt32(c.D,
                checked(start + (int)index * recordSize));
            if (id < 0 || !ids.Add(id)) return false;
        }
        return true;
    }

    private static bool TryGetTrackedHistoryTail(Cursor c, int faceEnd,
        int curveRecordSize, out int tailStart)
    {
        tailStart = -1;
        if (faceEnd < 0 || faceEnd + 4 > c.End) return false;
        uint curveCount = BitConverter.ToUInt32(c.D, faceEnd);
        int curveEnd;
        if (curveCount == 0xFFFFFFFF)
            curveEnd = faceEnd + 4;
        else
        {
            if (curveCount > 100_000) return false;
            long end = (long)faceEnd + 4 + curveCount * (long)curveRecordSize;
            if (end > c.End
                || (curveRecordSize == 24
                    && !IsCountHistoryTable(c, faceEnd + 4, curveCount, 24)))
                return false;
            curveEnd = (int)end;
        }

        if (!TrySelectTrackedEdgeHistoryEnd(c, curveEnd,
                out int edgeEnd, out int reverseEnd, out string edgeLayout))
        {
            c.CutTopologyTrace?.Add($"q{c.CurrentDeferredQueueIndex}:"
                + $"{c.CurrentDeferredClassId}:tracked-tail@{curveEnd:X}=invalid");
            return false;
        }
        c.CutTopologyTrace?.Add($"q{c.CurrentDeferredQueueIndex}:"
            + $"{c.CurrentDeferredClassId}:tracked-tail@{curveEnd:X}="
            + $"{edgeLayout}@{edgeEnd:X}->{reverseEnd:X}");
        tailStart = reverseEnd;
        return true;
    }

    private static void ReadTrackedEdgeHistoryTable(Cursor c)
    {
        int fieldStart = c.Pos;
        if (!TrySelectTrackedEdgeHistoryEnd(c, fieldStart,
                out int selectedEnd, out int reverseEnd, out string layout))
            throw Fail;
        c.Pos = selectedEnd;
        c.CurrentTrackedReverseHistoryPresent = reverseEnd > selectedEnd;
        c.CurrentTrackedReverseHistoryEnd = reverseEnd;
        c.CutTopologyTrace?.Add($"q{c.CurrentDeferredQueueIndex}:"
            + $"{c.CurrentDeferredClassId}:tracked-edge@{fieldStart:X}={layout}"
            + (reverseEnd > selectedEnd ? $"+reverse->{reverseEnd:X}" : ""));
    }

    private static bool TrySelectTrackedEdgeHistoryEnd(Cursor c, int fieldStart,
        out int selectedEnd, out int reverseEnd, out string layout)
    {
        selectedEnd = -1;
        reverseEnd = -1;
        layout = "";
        if (fieldStart < 0 || fieldStart + 4 > c.End) return false;

        uint value = BitConverter.ToUInt32(c.D, fieldStart);
        if (value is 0 or 0xFFFFFFFF)
        {
            selectedEnd = fieldStart + 4;
            layout = "empty";
            return TryValidateTrackedEdgeEnd(c, selectedEnd,
                out reverseEnd, out string reverseLayout)
                && AppendReverseLayout(ref layout, reverseLayout);
        }

        var candidates = new Dictionary<(int End, int ReverseEnd), string>();
        if (value <= 100_000)
        {
            long compactCountEnd64 = (long)fieldStart + 4 + value * 16L;
            if (compactCountEnd64 + 4 <= c.End
                && IsCountHistoryTable(c, fieldStart + 4, value, 16)
                && BitConverter.ToUInt32(c.D, (int)compactCountEnd64) == 0)
            {
                int compactTerminatedEnd = (int)compactCountEnd64 + 4;
                if (TryValidateTrackedEdgeEnd(c, compactTerminatedEnd,
                        out int compactReverseEnd, out string compactReverseLayout))
                    candidates[(compactTerminatedEnd, compactReverseEnd)] =
                        "compact-count-terminated"
                        + ReverseLayoutSuffix(compactReverseLayout);
            }

            long countEnd64 = (long)fieldStart + 4 + value * 20L;
            if (countEnd64 <= c.End
                && IsCountHistoryTable(c, fieldStart + 4, value, 20))
            {
                int countEnd = (int)countEnd64;
                if (TryValidateTrackedEdgeEnd(c, countEnd,
                        out int countReverseEnd, out string countReverseLayout))
                    candidates[(countEnd, countReverseEnd)] = "count"
                        + ReverseLayoutSuffix(countReverseLayout);
            }
        }

        int terminatedEnd = FindTerminatedHistoryTableEnd(c, fieldStart + 4, 20);
        if (TryValidateTrackedEdgeEnd(c, terminatedEnd,
                out int terminatedReverseEnd, out string terminatedReverseLayout))
        {
            var key = (terminatedEnd, terminatedReverseEnd);
            string terminatedName = "terminated"
                + ReverseLayoutSuffix(terminatedReverseLayout);
            if (candidates.TryGetValue(key, out string? existing))
                candidates[key] = existing + "+" + terminatedName;
            else
                candidates[key] = terminatedName;
        }

        if (candidates.Count > 1)
        {
            var compactKeys = candidates
                .Where(candidate => candidate.Value.StartsWith("compact-count",
                    StringComparison.Ordinal))
                .Select(candidate => candidate.Key)
                .ToHashSet();
            var preferredKeys = compactKeys.Count > 0
                ? compactKeys
                : candidates
                    .Where(candidate => candidate.Value.StartsWith("count",
                        StringComparison.Ordinal))
                    .Select(candidate => candidate.Key)
                    .ToHashSet();
            if (preferredKeys.Count > 0)
            {
                foreach (var key in candidates.Keys
                             .Where(key => !preferredKeys.Contains(key)).ToList())
                    candidates.Remove(key);
            }
        }
        if (candidates.Count > 1)
        {
            int earliestTail = candidates.Min(candidate => candidate.Key.ReverseEnd);
            foreach (var key in candidates.Keys
                         .Where(key => key.ReverseEnd != earliestTail)
                         .ToList())
                candidates.Remove(key);
        }
        c.CutTopologyTrace?.Add($"q{c.CurrentDeferredQueueIndex}:"
            + $"{c.CurrentDeferredClassId}:tracked-edge-candidates@{fieldStart:X}:"
            + string.Join(",", candidates.Select(candidate =>
                $"{candidate.Value}@{candidate.Key.End:X}->{candidate.Key.ReverseEnd:X}")));
        if (candidates.Count != 1) return false;
        var selected = candidates.Single();
        selectedEnd = selected.Key.End;
        reverseEnd = selected.Key.ReverseEnd;
        layout = selected.Value;
        return true;
    }

    private static bool TryValidateTrackedEdgeEnd(Cursor c, int edgeEnd,
        out int reverseEnd, out string reverseLayout)
    {
        reverseEnd = -1;
        reverseLayout = "";
        if (edgeEnd < 0 || edgeEnd > c.End) return false;
        var candidates = new Dictionary<int, string>();
        if (IsTrackedStepTail(c, edgeEnd))
            candidates[edgeEnd] = "omitted";
        if (edgeEnd + 4 <= c.End)
        {
            uint value = BitConverter.ToUInt32(c.D, edgeEnd);
            if (value is 0 or 0xFFFFFFFF)
            {
                // An empty reverse table is a run of empty-marker words (0 / FFFFFFFF): steps of the curve,
                // curtain-grid and sketch-regeneration classes write [0][0], [FFFFFFFF][0][0] or longer runs.
                for (int words = 1; words <= (c.TailWalk ? 6 : 1); words++)
                {
                    int emptyEnd = edgeEnd + 4 * words;
                    if (emptyEnd > c.End) break;
                    if (IsTrackedStepTail(c, emptyEnd))
                        candidates[emptyEnd] = words == 1 ? "empty" : "empty" + words;
                    if (words < 6 && (emptyEnd + 4 > c.End || BitConverter.ToUInt32(c.D, emptyEnd) is not (0 or 0xFFFFFFFF))) break;
                }
            }
            else if (value <= 100_000)
            {
                if (edgeEnd + 12 <= c.End
                    && BitConverter.ToUInt32(c.D, edgeEnd + 4) == 0)
                {
                    uint nestedCount = BitConverter.ToUInt32(c.D, edgeEnd + 8);
                    long nestedEnd64 = (long)edgeEnd + 12 + nestedCount * 20L;
                    if (nestedCount <= 100_000 && nestedEnd64 <= c.End
                        && IsCountHistoryTable(c, edgeEnd + 12, nestedCount, 20)
                        && IsTrackedStepTail(c, (int)nestedEnd64))
                        candidates[(int)nestedEnd64] = "high-water-count";
                }

                long countEnd64 = (long)edgeEnd + 4 + value * 20L;
                if (countEnd64 <= c.End
                    && IsCountHistoryTable(c, edgeEnd + 4, value, 20)
                    && IsTrackedStepTail(c, (int)countEnd64))
                    candidates[(int)countEnd64] = "count";

                if (countEnd64 + 4 <= c.End
                    && IsCountHistoryTable(c, edgeEnd + 4, value, 20)
                    && BitConverter.ToUInt32(c.D, (int)countEnd64) == 0
                    && IsTrackedStepTail(c, (int)countEnd64 + 4))
                    candidates[(int)countEnd64 + 4] = "count-terminated";
            }

            int terminatedEnd = FindTerminatedHistoryTableEnd(c, edgeEnd + 4, 20);
            if (IsTrackedStepTail(c, terminatedEnd))
            {
                if (candidates.TryGetValue(terminatedEnd, out string? existing))
                    candidates[terminatedEnd] = existing + "+terminated";
                else
                    candidates[terminatedEnd] = "terminated";
            }
        }
        if (candidates.Count > 1)
        {
            int earliestTail = candidates.Keys.Min();
            foreach (int end in candidates.Keys.Where(end => end != earliestTail).ToList())
                candidates.Remove(end);
        }
        if (candidates.Count != 1) return false;
        var selected = candidates.Single();
        reverseEnd = selected.Key;
        reverseLayout = selected.Value;
        return true;
    }

    private static string ReverseLayoutSuffix(string reverseLayout)
        => reverseLayout == "omitted" ? "" : "+reverse-" + reverseLayout;

    private static bool AppendReverseLayout(ref string layout, string reverseLayout)
    {
        layout += ReverseLayoutSuffix(reverseLayout);
        return true;
    }

    private static bool IsTrackedStepTail(Cursor c, int tailStart)
    {
        bool wallCut = IsSubclassOf(c.CurrentDeferredClassId, 1793);
        bool pairCut = IsSubclassOf(c.CurrentDeferredClassId, 678);
        int requiredSize = wallCut ? 24 : pairCut ? 20 : 16;
        if (tailStart < 0 || tailStart + requiredSize > c.End) return false;

        uint ownerReference = BitConverter.ToUInt32(c.D, tailStart + 12);
        // Deferred cut steps refer back to the owning root HostObj by queue handle 2. Generator ids start at 1: every
        // step of an exactly consumed block has m_id >= 1, while chance matches of the owner word did not.
        if (ownerReference != 2 || (c.TailWalk && BitConverter.ToInt32(c.D, tailStart) <= 0)) return false;
        return wallCut
            ? BitConverter.ToInt32(c.D, tailStart + 20) > 0
            : !pairCut || BitConverter.ToInt32(c.D, tailStart + 16) > 0;
    }

    private static NamedParamRecord NamedParamOf(NativeParams item) => new(
        item.Fields.GetValueOrDefault("715:m_paramId", -1),
        item.StringFields.GetValueOrDefault("715:m_str"),
        item.DoubleFields.GetValueOrDefault("715:m_value"),
        item.Fields.GetValueOrDefault("715:m_elemId", -1),
        item.Fields.GetValueOrDefault("715:m_int"),
        item.Fields.GetValueOrDefault("715:m_instance") != 0,
        item.Fields.GetValueOrDefault("715:m_reporting") != 0);

    private static void ReadInlineContainer(Cursor c, int cls, List<(ushort, uint)> queue, int depth,
        NativeParams? capture = null)
    {
        uint n = ReadU32(c);
        if (n > 100_000) throw Fail;
        if (cls == 715 && capture != null)
        {
            for (uint i = 0; i < n; i++)
            {
                var item = new NativeParams();
                capturingNamedParam = true;
                try { ReadInlineStruct(c, cls, queue, depth, item); }
                finally { capturingNamedParam = false; }
                capture.NamedParams.Add(NamedParamOf(item));
            }
            return;
        }
        for (uint i = 0; i < n; i++) ReadInlineStruct(c, cls, queue, depth, capture);
    }

    private static void ReadCDJoinInfoCleanData(Cursor c, List<(ushort, uint)> queue, int depth)
    {
        // Correct only this named inline contract; unrelated class90 walks and
        // owner-scoped diagnostic aliases keep their existing behavior/descriptors.
        var set = SerializationFields(90);
        var item = SerializationFields(91);
        if (set is not { Count: 1 } || set[0] is not { Declaring: 90, Name: "m_data", Type: 14,
                Mods: 80, Ref: null, Cnt: null }
            || item is not { Count: 2 }
            || item[0] is not { Declaring: 91, Name: "m_elementId", Type: 14, Mods: 0, Ref: 20 }
            || item[1] is not { Declaring: 91, Name: "m_index", Type: 4, Mods: 0, Ref: 90 })
            throw Fail;
        ReadField(c, set[0] with { Ref = 91 }, queue, depth + 1, null);
    }

    private static bool HasGeomRefSchema() =>
        Schemas.TryGetValue(140, out var geomRef) && geomRef.Name == "GeomRef" && geomRef.Fields.Count == 9
        && geomRef.Fields[0] is { Name: "m_intermediateTags", Type: 4, Mods: 80, Ref: null }
        && geomRef.Fields[1] is { Name: "m_oNextRef", Type: 14, Mods: 1, Ref: null }
        && geomRef.Fields[2] is { Name: "m_elemId", Type: 14, Mods: 0, Ref: 20 }
        && geomRef.Fields[3] is { Name: "m_foreignElemIdRef", Type: 14, Mods: 0, Ref: null }
        && geomRef.Fields[4] is { Name: "m_geomTag", Type: 4, Mods: 0 }
        && geomRef.Fields[5] is { Name: "m_subTag", Type: 4, Mods: 0 }
        && geomRef.Fields[6] is { Name: "m_flags", Type: 4, Mods: 0 }
        && geomRef.Fields[7] is { Name: "m_ownerDBViewId", Type: 14, Mods: 0, Ref: 20 }
        && geomRef.Fields[8] is { Name: "m_isLazyRef", Type: 1, Mods: 0 };

    private static bool HasStairsSupportGeomRefSchema() =>
        Schemas.TryGetValue(3728, out var reference) && reference.Name == "StairsSupportGeomRef" && reference.Fields.Count == 5
        && reference.Fields.Take(4).All(field => field is { Type: 7, Mods: 0 })
        && reference.Fields[4] is { Name: "m_geomRef", Type: 14, Mods: 0, Ref: 3728 };

    private static bool HasConnectorIdSchema() =>
        Schemas.TryGetValue(772, out var id) && id.Name == "ConnectorId" && id.Fields.Count == 3
        && id.Fields[0] is { Name: "m_id", Type: 14, Mods: 0, Ref: 20 }
        && id.Fields[1] is { Name: "m_nIndex", Type: 4, Mods: 0 }
        && id.Fields[2] is { Name: "m_connType", Type: 4, Mods: 0 };

    private static bool HasCellMapSchema() =>
        Schemas.TryGetValue(591, out var pair) && pair.Name == "std::pair< CGCellCoords, CGCellContents >" && pair.Fields.Count == 2
        && pair.Fields[0] is { Name: "first", Type: 14, Mods: 0, Ref: 584 }
        && pair.Fields[1] is { Name: "second", Type: 14, Mods: 0, Ref: 591 }
        && Schemas.TryGetValue(584, out var coords) && coords.Fields.Count == 2
        && coords.Fields[0] is { Name: "m_u", Type: 4, Mods: 0 } && coords.Fields[1] is { Name: "m_v", Type: 4, Mods: 0 }
        && Schemas.TryGetValue(583, out var contents) && contents.Name == "CGCellContents" && contents.Fields.Count == 7
        && contents.Fields[0] is { Name: "m_edgeTags", Type: 4, Mods: 80 }
        && contents.Fields[1] is { Name: "m_faceTag", Type: 4, Mods: 0 }
        && contents.Fields.Skip(2).All(field => field is { Type: 14, Mods: 0, Ref: 20 });

    private static bool HasEdgeSegMapSchema() =>
        Schemas.TryGetValue(592, out var pair) && pair.Name == "std::pair< int, CEdgeSegSetWrapper >" && pair.Fields.Count == 2
        && pair.Fields[0] is { Name: "first", Type: 4, Mods: 0 }
        && pair.Fields[1] is { Name: "second", Type: 14, Mods: 0, Ref: 592 }
        && Schemas.TryGetValue(582, out var wrapper) && wrapper.Name == "CEdgeSegSetWrapper" && wrapper.Fields.Count == 1
        && wrapper.Fields[0] is { Name: "m_edgeSegs", Type: 14, Mods: 80, Ref: 581 }
        && Schemas.TryGetValue(581, out var seg) && seg.Name == "CEdgeSeg" && seg.Fields.Count == 3
        && seg.Fields[0] is { Name: "m_start", Type: 7, Mods: 0 } && seg.Fields[1] is { Name: "m_end", Type: 7, Mods: 0 }
        && seg.Fields[2] is { Name: "m_edgeTag", Type: 4, Mods: 0 };

    private static bool HasTweakEffectedTagSetSchema() =>
        Schemas.TryGetValue(679, out var pair) && pair.Name == "std::pair< ElementId, TweakEffectedTagSetWrapper >" && pair.Fields.Count == 2
        && pair.Fields[0] is { Name: "first", Type: 14, Mods: 0, Ref: 20 }
        && pair.Fields[1] is { Name: "second", Type: 14, Mods: 0, Ref: null }
        && Schemas.TryGetValue(680, out var wrapper) && wrapper.Name == "TweakEffectedTagSetWrapper" && wrapper.Fields.Count == 1
        && wrapper.Fields[0] is { Name: "m_tags", Type: 4, Mods: 80 };

    private static bool HasRoomFaceOffsetSchema() =>
        Schemas.TryGetValue(3469, out var pair) && pair.Name == "std::pair< int, RoomFaceOffsets >" && pair.Fields.Count == 2
        && pair.Fields[0] is { Name: "first", Type: 4, Mods: 0 }
        && pair.Fields[1] is { Name: "second", Type: 14, Mods: 0, Ref: null }
        && Schemas.TryGetValue(3470, out var offsets) && offsets.Name == "RoomFaceOffsets" && offsets.Fields.Count == 1
        && offsets.Fields[0] is { Name: "m_offsets", Type: 7, Mods: 16, Cnt: 4 };

    private static bool HasLargestMotorPairSchema() =>
        Schemas.TryGetValue(1289, out var pair) && pair.Fields.Count == 2
        && pair.Fields[0] is { Name: "first", Type: 14, Mods: 0, Ref: 20 }
        && pair.Fields[1] is { Name: "second", Type: 14, Mods: 0, Ref: 1289 }
        && Schemas.TryGetValue(22, out var scalar) && scalar.Fields.Count == 2
        && scalar.Fields[0] is { Name: "first", Type: 14, Mods: 0, Ref: 20 }
        && scalar.Fields[1] is { Name: "second", Type: 7, Mods: 0 };

    private static bool HasElementIdPairSchema() =>
        Schemas.TryGetValue(24, out var pair) && pair.Name == "std::pair< ElementId, ElementId >" && pair.Fields.Count == 2
        && pair.Fields[0] is { Name: "first", Type: 14, Mods: 0, Ref: 20 }
        && pair.Fields[1] is { Name: "second", Type: 14, Mods: 0, Ref: 24 };

    private static bool HasLocationLineOffsetDataSchema() =>
        Schemas.TryGetValue(2081, out var data) && data.Name == "LocationLineOffsetData" && data.Fields.Count == 7
        && data.Fields[0] is { Name: "m_pOwnerCell", Type: 14, Mods: 3 }
        && data.Fields[1] is { Name: "m_offset", Type: 14, Mods: 16 }
        && data.Fields[2] is { Name: "m_offsetType", Type: 4, Mods: 16 }
        && data.Fields[3] is { Name: "m_offsetYRefType", Type: 4, Mods: 16 }
        && data.Fields[4] is { Name: "m_offsetZRefType", Type: 4, Mods: 16 }
        && data.Fields[5] is { Name: "m_refElemId", Type: 14, Mods: 16 }
        && data.Fields[6] is { Name: "m_isOffsettingEnabled", Type: 1, Mods: 16 };

    private static void ReadInlineStruct(Cursor c, int cls, List<(ushort, uint)> queue, int depth,
        NativeParams? capture = null)
    {
        var sub = SerializationFields(cls) ?? throw Fail;
        if (depth > 40) throw Fail;
        foreach (var sf in sub) ReadField(c, sf, queue, depth + 1, capture);
    }

    private static bool LooksLikePointerArray(Cursor c, int position)
    {
        if (position + 4 > c.End) return false;
        uint count = BitConverter.ToUInt32(c.D, position);
        if (count == 0xFFFFFFFF || count == 0) return true;
        if (count > 4096 || position + 4 + 6 > c.End) return false;
        uint first = BitConverter.ToUInt32(c.D, position + 4);
        if (first == 0) return true;
        return Schemas.ContainsKey(BitConverter.ToUInt16(c.D, position + 8));
    }

    private static void ReadPtr(Cursor c, List<(ushort, uint)> queue)
    {
        uint v = ReadU32(c);
        lastPolyClass = 0;
        if (v == 0) return;
        if (c.Pos + 2 > c.End) throw Fail;
        ushort cls = BitConverter.ToUInt16(c.D, c.Pos); c.Pos += 2;
        lastPolyClass = cls == 0 ? -1 : cls;
        // Class tag zero is an unresolved/external persistent pointer. It owns no
        // deferred payload in this block, but the explicit tag is still serialized.
        if (cls == 0) return;
        if (cls == 4) { ReadGInfo(c); return; }
        if (!Schemas.ContainsKey(cls)) throw Fail;
        queue.Add((cls, v));
        DiagNoteQueued(queue);
    }

    /// <summary>Class tag of the pointer read last by <see cref="ReadPoly"/>: 0 null, -1 plain handle, else the u16 tag.</summary>
    [ThreadStatic] private static int lastPolyClass;

    private static uint ReadPoly(Cursor c, List<(ushort, uint)> queue)
    {
        uint v = ReadU32(c);
        lastPolyClass = v == 0 ? 0 : -1;
        if (v == 0 || v != 0xFFFFFFFF) return v; // null or plain handle
        if (c.Pos + 2 > c.End) throw Fail;
        ushort cls = BitConverter.ToUInt16(c.D, c.Pos); c.Pos += 2;
        lastPolyClass = cls;
        if (cls == 4) { ReadGInfo(c); return v; }
        if (!Schemas.ContainsKey(cls)) throw Fail;
        queue.Add((cls, v));
        DiagNoteQueued(queue);
        return v;
    }

    private static void ReadGInfo(Cursor c)
    {
        if (c.Pos + 2 > c.End) throw Fail;
        ushort tk = BitConverter.ToUInt16(c.D, c.Pos); c.Pos += 2;
        if ((tk & 0xFF) != 8) throw Fail;
        int kind = tk >> 8;
        if (kind == 0) return;
        if (kind == 1) { if (!c.Take(6 * 8 + 2 * 4)) throw Fail; return; }
        throw Fail;
    }
}
