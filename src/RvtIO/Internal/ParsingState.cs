using RvtIO.Parsing;

namespace RvtIO.Internal;

internal class ParsingState
{
    public Dictionary<uint, OwnPartitionParts.WallParts>? OwnPartitionWallParts { get; set; }
    public Dictionary<uint, int> OwnWallSide0Ids { get; } = new();
    /// <summary>Face-history table of the first BaseWallGStep of the current own wall root (see
    /// <see cref="TfBlockDeserializer.SelectOwnWallBaseFaceHistories"/>).</summary>
    public Dictionary<uint, IReadOnlyList<TfBlockDeserializer.WallHistoryEntry>> OwnWallBaseFaces { get; } = new();
    /// <summary>Parameters of the trimmed location line of a straight own wall (see
    /// <see cref="TfBlockDeserializer.SelectOwnWallTrimmedRange"/>), on the line of its curve driver.</summary>
    public Dictionary<uint, (double Lo, double Hi)> OwnWallTrimmedRanges { get; } = new();
    public Dictionary<uint, (uint SourceFace, double[] Normal)> OwnWallSide0Normals { get; } = new();
    public HashSet<uint> OwnCurrentWallRoots { get; } = new();
    public Dictionary<uint, IReadOnlyDictionary<uint, uint>> OwnCurrentWallFaceGroups { get; } = new();
    /// <summary>Element-table membership. Values are zero: ElemRec has no type/class column.</summary>
    public Dictionary<uint, uint> ElemTable { get; set; } = new();

    /// <summary>Per-element workset ids from ElemTable offset-20 (workshared files only).</summary>
    public Dictionary<uint, uint> ElemWorksets { get; set; } = new();
    public Dictionary<uint, ElementRecord> Records { get; set; } = new();

    /// <summary>Complete own UnitsElem roots, retaining family/document ownership and
    /// every format option. Duplicates or malformed roots clear the value. This is not
    /// Document.GetUnits: Global/Latest's owned UnitsTracking must choose the owner.</summary>
    public Dictionary<uint, (int BlockCount, TfBlockDeserializer.UnitsElemOptions? Options)>
        OwnUnitOptions { get; } = new();

    /// <summary>Positive owner reached through the CRC-checked Global/Latest root's
    /// named AppInfoManager and its unique owned UnitsTracking. Not a first UnitsElem.</summary>
    public uint? OwnDocumentUnitsElemId { get; set; }

    /// <summary>The unit id of the Length format option of a file with 64-bit ids (see ReadWideDocumentUnits); null when not read.</summary>
    public string? WideLengthUnit { get; set; }

    /// <summary>Why the wide reader found no Length unit; null when it found one or did not run.</summary>
    public string? WideLengthUnitIssue { get; set; }

    /// <summary>Verbatim persisted maps from the unique named-manager ESSchemaStorage.
    /// Not SDK defaults, parsed/evaluated schemas or a winning native unit table.</summary>
    public (TfBlockDeserializer.DocumentSchemaMap Units, TfBlockDeserializer.DocumentSchemaMap Parameters)?
        OwnDocumentSchemas { get; set; }

    /// <summary>SDK numeric components plus own unknown-full-key additions.
    /// SI factors only: not current options, internal-foot conversion, offsets or
    /// native DisplayUnitTypeInfo/legacy lookup. A rejected projection is null.</summary>
    public IReadOnlyDictionary<string, double>? OwnDocumentSiFactors { get; set; }

    /// <summary>Dimension/offset/universal internal-scale components only.
    /// Not display UnitInfo lookup or complete public conversion semantics.</summary>
    public IReadOnlyDictionary<string, UnitSchemaEvaluator.UnitCore>? OwnDocumentUnitCores { get; set; }

    /// <summary>Numeric normal/legacy display lookup and conversion.
    /// Separate from current format options, display strings and IFC unit assignment.</summary>
    public UnitSchemaEvaluator.UnitConversions? OwnDocumentUnitConversions { get; set; }

    /// <summary>Only the unique, complete document-owned partition root selected by
    /// Global/Latest. Computed so a later duplicate or rejected root revokes acceptance.</summary>
    public TfBlockDeserializer.UnitsElemOptions? OwnDocumentUnitOptions
        => OwnDocumentUnitsElemId is uint owner
            && OwnUnitOptions.TryGetValue(owner, out var validation) && validation.BlockCount == 1
            && validation.Options is { } options && options.OwnerId == owner
            && options.FamilyId == -1 && options.DocumentAccess == 1 ? options : null;

    /// <summary>The units element selected by Global/Latest for the length unit: as <see cref="OwnDocumentUnitOptions"/> but also for a
    /// family document, whose selected units element names its family (a project's is -1). The owner, uniqueness and
    /// document access checks are the same; <paramref name="issue"/> says which one failed.</summary>
    public TfBlockDeserializer.UnitsElemOptions? OwnDocumentLengthUnitOptions(out string? issue)
    {
        issue = null;
        if (OwnDocumentUnitsElemId is not uint owner) issue = "Global/Latest names no units element";
        else if (!OwnUnitOptions.TryGetValue(owner, out var proof) || proof.BlockCount == 0) issue = $"no decoded UnitsElem block for id {owner}";
        else if (proof.BlockCount != 1) issue = $"UnitsElem {owner} was found in {proof.BlockCount} blocks";
        else if (proof.Options is not { } options || options.OwnerId != owner) issue = $"the UnitsElem block {owner} is malformed";
        else if (options.DocumentAccess != 1) issue = $"the UnitsElem {owner} is not document accessible";
        else return options;
        return null;
    }

    /// <summary>Numeric length/thermal export plan, not emitted IFC units. Computed
    /// so later duplicate roots or rejected display projections revoke acceptance.</summary>
    public Ifc.IfcNumericUnitPlan? OwnDocumentIfcNumericUnitPlan
        => Ifc.IfcNumericUnitPlan.Create(OwnDocumentUnitOptions?.Value, OwnDocumentUnitConversions, out _);

    /// <summary>First block classId (low16) seen per element in the continuous walk. Used to
    /// assign category from the empirical high-purity classIdx->category table, applied late.</summary>
    public Dictionary<uint, ushort> ElemClassIdx { get; } = new();

    /// <summary>ElementHeader.m_categroryId of the element records the continuous walk accepted (see <see cref="ElementHeaderCategory"/>);
    /// a null value marks an id whose records disagree or whose header could not be read.</summary>
    public Dictionary<uint, int?> HeaderCategories { get; } = new();

    /// <summary>ElementHeader.m_designOptionId per element id (files with 64-bit ids; null: the records of the id disagree).</summary>
    public Dictionary<uint, int?> HeaderDesignOptions { get; } = new();

    /// <summary>The axis-aligned outline the ElementHeader of the element's records stores (see <see cref="Parsing.ElementHeaderCategory"/>):
    /// a verdict when the records agree it is there or agree there is none, null when they disagree or no record was read.</summary>
    public Dictionary<uint, Parsing.ElementHeaderBox?> HeaderBoundingBoxes { get; } = new();

    /// <summary>The file's own class schema when its element ids are 64-bit (Revit 2025 layout, see <see cref="Parsing.WideElementRecords"/>);
    /// null for the 32-bit layouts, which keep the shipped Revit 2022 tables.</summary>
    public Parsing.FileSchema? WideSchema { get; set; }

    /// <summary>The partition chunks of this <see cref="WideSchema"/> file were rewritten into the Revit 2022 layout (<see cref="Parsing.R22Transcoder"/>),
    /// so the 2022 record, block and geometry readers apply to them.</summary>
    public bool R22View { get; set; }

    /// <summary>True when record routing removed the superseded copies of repeated elements from the partition chunks
    /// (<see cref="Parsing.RecordRouting"/>). The per-chunk block walk then no longer stops at a header-like byte run whose element
    /// is not in the ElemTable: with the old copies gone, the current copy of an element can be the first block behind such a run
    /// and the stop would hide it.</summary>
    public bool StaleCopiesRemoved { get; set; }

    /// <summary>Each partition stream read by its framing, or null where the framing does not hold (<see cref="Parsing.PartitionFraming.Framed"/>):
    /// its segments and the records of the project's own block, without the copies record routing marks as not current. In the
    /// <see cref="R22View"/> the segments hold only the records of channels 102 and 103, each rewritten into the Revit 2022 layout.</summary>
    public IReadOnlyList<Parsing.PartitionFraming.Framed?>? FramedPartitions { get; set; }

    /// <summary>For multi-partition files whose partition headers and increment table decode: maps each multi-partition
    /// element id to the partition number that holds its current copy (populated even when stale-copy pruning is disabled).</summary>
    public IReadOnlyDictionary<ulong, int>? IncrementRoutedPartitions { get; set; }

    /// <summary>How many of the first <see cref="Chunks"/> are the chunks of the partition streams (the chunks of Formats and Global follow).</summary>
    public int PartitionChunkCount { get; set; }

    /// <summary>True for a file in the Revit 2022 layout (without its own class schema, or in the <see cref="R22View"/>) whose every
    /// partition stream is read by its framing: the readers take the records of the project's own block (<see cref="FramedPartitions"/>)
    /// instead of scanning the partition chunks.</summary>
    internal bool ReadsFramedRecords => (WideSchema == null || R22View) && FramedPartitions is { Count: > 0 } framed && framed.All(f => f is { IdWidth: 4 });

    /// <summary>The own name of an element of a <see cref="WideSchema"/> file, read from its root block by the schema-driven walk.</summary>
    public Dictionary<uint, string> WideNames { get; } = new();

    /// <summary>The bars of the reinforcement elements (Rebar, RebarInSystem) by element id, read by <see cref="Parsing.RebarReader"/>.</summary>
    public Dictionary<uint, Parsing.RebarBars> RebarBars { get; } = new();

    /// <summary>The beam systems (JoistSystem) by element id, read by <see cref="Parsing.RebarReader"/>.</summary>
    public Dictionary<uint, Parsing.JoistSystemData> JoistSystems { get; } = new();

    /// <summary>Schema name of the Element-derived root class per element id, filled only for <see cref="WideSchema"/> files.</summary>
    public Dictionary<uint, string> NativeRootClassNames { get; } = new();

    /// <summary>Local worksharing username from BasicFileInfo ("Username: ..."); null when not
    /// workshared. Used for the per-category EDITED_BY rule.</summary>
    public string? LocalUsername { get; set; }
    public HashSet<uint> AllKnownIds { get; set; } = new();
    public Dictionary<uint, List<string>> ElemStrings { get; set; } = new();
    public Dictionary<uint, List<string>> ElemScanStrings { get; set; } = new();
    public Dictionary<uint, List<double>> ElemDoubles { get; set; } = new();
    public Dictionary<uint, Dictionary<int, double>> ElemBipDoubles { get; set; } = new();
    public Dictionary<uint, Dictionary<uint, string>> ElemParamStrings { get; set; } = new();
    public Dictionary<uint, Dictionary<int, int>> ElemBipInts { get; set; } = new();
    public Dictionary<uint, BaseElementFields> ElemBaseFields { get; set; } = new();
    public Dictionary<uint, GRepData> GrepBlocks { get; set; } = new();
    internal Dictionary<uint, (int BlockCount, TfBlockDeserializer.OwnRailingQuantityInput? Input)> RailingQuantityInputs { get; } = new();

    /// <summary>While set, the symbols placed from <see cref="GrepBlocks"/> are taken at the Coarse detail level.</summary>
    public bool CoarseView { get; set; }
    public bool IncludeNonVisibleQuantities { get; set; }

    /// <summary>The detail level the geometry is placed at: 0 Fine, 1 Coarse, 2 Medium.</summary>
    public int DetailView { get; set; }
    public Dictionary<uint, (int BlockCount, GRepData? Body, string? Failure)> OwnWallPlanarBodies { get; } = new();

    /// <summary>FamilySymbol.m_complexCutGeoms decoded as an exact family-local BRep.
    /// Kept separate from the symbol's visible GRep because it is a void operand.</summary>
    public Dictionary<uint, GRepData> FamilyCutGreps { get; } = new();
    /// <summary>FamilySymbol InsertableObj cut profiles decoded from m_pCutLoops,
    /// m_cutDirs and m_cutIndices. These are distinct from m_complexCutGeoms.</summary>
    public Dictionary<uint, List<Model.FamilyCutLoopData>> FamilyCutLoops { get; } = new();
    public Dictionary<uint, (int BlockCount, TfBlockDeserializer.OwnFamilyCutSymbol? Symbol,
        List<Model.FamilyCutLoopData>? Loops)> OwnFamilyCutSymbols { get; } = new();
    public Dictionary<uint, (int BlockCount, bool? InPlace)> OwnFamilyInPlace { get; } = new();
    /// <summary>FUNCTION_PARAM of each FamilySymbol root read from its own FamilyParams prefix
    /// (<see cref="TfBlockDeserializer.ExtractFamilySymbolFunctionInput"/>). A repeated root block
    /// clears the value; a root without a decodable stored record has none.</summary>
    public Dictionary<uint, (int BlockCount, int? Function)> FamilySymbolFunctions { get; } = new();
    public Dictionary<uint, (int BlockCount, List<TfBlockDeserializer.OwnWallCutoutStep>? Steps,
        CurveDriverData? Axis, int? CrossSection)>
        OwnWallCutoutSteps { get; } = new();
    /// <summary>Profile helper cell of each own wall root (<see cref="TfBlockDeserializer.ExtractOwnWallHelperInput"/>);
    /// a repeated root block clears the value.</summary>
    public Dictionary<uint, (int BlockCount, TfBlockDeserializer.OwnWallHelperInput? Input)> OwnWallHelpers { get; } = new();
    /// <summary>Number of InsertableObj.m_pCutLoops entries of each FamilySymbol root,
    /// whether or not the loops themselves decode.</summary>
    public Dictionary<uint, int> FamilySymbolCutLoopCounts { get; } = new();
    /// <summary>Exact host-to-cutter topology decoded from GeomStepList cut-out phases and
    /// WallCutoutGStepBase.m_instId.</summary>
    public Dictionary<uint, List<uint>> HostCuttingElementIds { get; } = new();
    /// <summary>Host-to-host pair steps (PairBaseGStep.m_otherId) of the same cut-out phases, in
    /// queue order; present only for hosts whose every tracked step decoded.</summary>
    public Dictionary<uint, List<(ushort ClassId, int ElementId, bool Cutout)>> HostCutOutSteps { get; } = new();

    /// <summary>Hosts whose <see cref="HostCutOutSteps"/> come from the history-layout search of
    /// <c>ExtractCutOutStepsWithLayoutSearch</c>: used only when every step names an element the host references.</summary>
    public HashSet<uint> HostCutOutStepsFromLayoutSearch { get; } = new();
    public HashSet<uint> BlockOnlyIds { get; set; } = new();
    public Dictionary<uint, (string ForgeId, string? DisplayName)> ForgeParamDefs { get; set; } = new();
    public string? DocGuid { get; set; }
    public List<byte[]> Chunks { get; set; } = new();
    public List<byte[]> RecordChunks { get; set; } = new();
    public List<byte[]> NonRecordChunks { get; set; } = new();
    public Dictionary<uint, double> WallTypeWidths { get; set; } = new();

    /// <summary>Wall function enum extracted from classIdx=0x00012100 blocks at offset 0x18.
    /// Values: 0=Interior, 1=Exterior, 2=Foundation, 3=Retaining, 4=Soffit, 5=CoreShaft.</summary>
    public Dictionary<uint, int> WallTypeFunctions { get; set; } = new();

    /// <summary>Exact WallType.m_function values decoded only from class 2559
    /// (NewCurtainWallType) root blocks. Kept separate from compound-structure wall
    /// functions so consumers can require this precise type-side provenance. Every WallType
    /// subclass root counts; duplicate or malformed roots permanently clear Function.</summary>
    public Dictionary<uint, (int BlockCount, int? Function)> NewCurtainWallTypeFunctions { get; } = new();

    /// <summary>Own SymbolInfo names. Acceptance also requires one and only one native
    /// WallType-subclass root in NewCurtainWallTypeFunctions.</summary>
    public Dictionary<uint, string> NewCurtainWallTypeNames { get; } = new();

    /// <summary>Own BasicWallType SymbolInfo names; require unique BasicWallTypeFunctions source.</summary>
    public Dictionary<uint, string> BasicWallTypeNames { get; } = new();

    /// <summary>Member element ids of each group instance, read from its own root block.</summary>
    public Dictionary<uint, int[]> GroupMembers { get; } = new();

    /// <summary>Own WallSweep.m_typeId; every HostObject root counts against uniqueness.</summary>
    public Dictionary<uint, (int BlockCount, int? TypeId)> WallSweepTypeIds { get; } = new();

    /// <summary>Own CorniceAttr SymbolInfo name; every Symbol root counts against uniqueness.</summary>
    public Dictionary<uint, (int BlockCount, string? Name)> CorniceTypeNames { get; } = new();

    /// <summary>Own FamilyInstance symbol link; every FamilyInstance subclass root counts.</summary>
    public Dictionary<uint, (int BlockCount, int? TypeId)> FamilyInstanceSymbolIds { get; } = new();
    /// <summary>Own Instance.m_pInstanceInfo -> InstInfoBase.m_symbolId. All
    /// FamilyInstance subclass roots count, including rejected/duplicate roots.</summary>
    public Dictionary<uint, (int BlockCount, TfBlockDeserializer.ComputedFamilySymbol? Symbol)>
        ComputedFamilySymbols { get; } = new();
    /// <summary>The panel ids of the curtain grids (CurtainGrid.m_panelIds, in stored order) of a wall's own root; every wall
    /// root counts, a wall without a curtain grid or with an undecodable root records no ids.</summary>
    public Dictionary<uint, (int BlockCount, int[]? PanelIds)> CurtainPanelOrders { get; } = new();
    /// <summary>Semantic Element kind/owner prefix, independent of display names
    /// and later subtype fields. Every Element root counts against target uniqueness.</summary>
    public Dictionary<uint, (int BlockCount, ushort? ClassId)> ComputedSymbolTargetClasses { get; } = new();
    /// <summary>Own FamilySymbol SymbolInfo name; every Symbol subclass root counts.</summary>
    public Dictionary<uint, (int BlockCount, string? Name)> FamilySymbolNames { get; } = new();

    /// <summary>Own roof/railing attribute links; all HostObject/BaseRailing roots count.</summary>
    public Dictionary<uint, (int BlockCount, ushort ClassId, int? TypeId)> RoofOrRailingTypeIds { get; } = new();
    /// <summary>Own RoofAttributes/StairsRailingAttr names; all Symbol roots count.</summary>
    public Dictionary<uint, (int BlockCount, ushort ClassId, string? Name)> RoofOrRailingTypeNames { get; } = new();

    /// <summary>Own Symbol-subclass roots: every root counts; only a well-formed own root
    /// records its class and family discriminator. Duplicates clear both.</summary>
    public Dictionary<uint, (int BlockCount, ushort ClassId, int? Discriminator)> SystemTypeRoots { get; } = new();

    /// <summary>Own StairsRunType/StairsLandingType/StairsSupportType SymbolInfo names. SystemTypeRoots
    /// separately counts every Symbol root, including unsupported/conflicting classes.</summary>
    public Dictionary<uint, (int BlockCount, ushort ClassId, string? Name)> StairComponentTypeNames { get; } = new();

    /// <summary>Own circular AbsDuctType SymbolInfo names. SystemTypeRoots separately
    /// counts every Symbol root, including unsupported/conflicting classes.</summary>
    public Dictionary<uint, (int BlockCount, string? Name)> DuctTypeNames { get; } = new();

    /// <summary>Own SymbolInfo names of pipe, flex pipe, pipe insulation and cable tray types (ClassId is the type
    /// root class). SystemTypeRoots separately counts every Symbol root.</summary>
    public Dictionary<uint, (int BlockCount, ushort ClassId, string? Name)> MepTypeNames { get; } = new();

    /// <summary>Own wall/floor/ceiling type link. All HostObj roots count, including
    /// unreadable/unsupported roots; duplicates cannot repair an earlier failure.</summary>
    public Dictionary<uint, (int BlockCount, ushort ClassId, int? TypeId)> SystemOccurrenceTypeIds { get; } = new();

    // Owners whose type came from the unique root read by the file schema.
    internal HashSet<uint> SchemaSystemOccurrenceOwners { get; } = new();

    /// <summary>Own MaterialElem-root Material.m_name source: every root counts; an
    /// unreadable root records null. Duplicates clear the name.</summary>
    public Dictionary<uint, (int BlockCount, string? Name)> MaterialNames { get; } = new();

    /// <summary>GStyleElem (class 2050) -> GStyle.m_materialElemId and the style's category; MaterialId is null when the
    /// block did not decode. A repeated block clears it.</summary>
    public Dictionary<uint, (int BlockCount, int? MaterialId, int CategoryId)> GraphicsStyles { get; } = new();

    /// <summary>CategoryElem (class 632) -> Category.m_name; every root counts, an unreadable root records null and a
    /// repeated block clears the name.</summary>
    public Dictionary<uint, (int BlockCount, string? Name)> CategoryNames { get; } = new();

    /// <summary>Own wall-root (SWall/ArcWall) top-attachment source: every root counts;
    /// an unreadable root records null. Duplicates clear the value.</summary>
    public Dictionary<uint, (int BlockCount, bool? JoinedToTop)> WallJoinedToTop { get; } = new();

    /// <summary>Control joins of each wall's own driver
    /// (<see cref="Parsing.TfBlockDeserializer.ExtractWallEndJoins"/>); null when the block is
    /// not read exactly, and a second block for the same id clears the value.</summary>
    public Dictionary<uint, (int BlockCount, List<Parsing.TfBlockDeserializer.WallEndJoin>? Joins)> WallEndJoins { get; } = new();

    /// <summary>Diagnostics: why wall blocks yielded no end joins, by reason.</summary>
    public Dictionary<string, int> WallEndJoinFailures { get; } = new();

    /// <summary>Own class-512 root source; duplicates and malformed roots clear Function.</summary>
    public Dictionary<uint, (int BlockCount, int? Function)> BasicWallTypeFunctions { get; } = new();

    /// <summary>Own complete StackedWallType roots. Acceptance also requires exactly one
    /// WallType-subclass root in BasicWallTypeFunctions, including malformed duplicates.</summary>
    public HashSet<uint> StackedWallTypeOwners { get; } = new();

    /// <summary>Own FamilyInstance root counts and direct curtain membership. Duplicate
    /// or malformed roots clear membership permanently for this parse.</summary>
    public Dictionary<uint, (int BlockCount, TfBlockDeserializer.CurtainFamilyHost? Host)>
        CurtainFamilyHostSource { get; } = new();

    /// <summary>CompoundStructure layers extracted from 0xFFFF0200/0xFFFF1026 blocks.
    /// Per layer: thickness (ft), material function, material element ID.</summary>
    public Dictionary<uint, List<CompoundLayer>> CompoundLayers { get; set; } = new();

    /// <summary>Exact name source from an element's own 0xFFFF0200 block.
    /// The continuous stream walk records every such block so consumers can require a
    /// unique owner block instead of losing provenance in the aggregate ElemStrings bag.</summary>
    public Dictionary<uint, (int BlockCount, string? TerminalName)>
        CompoundStructureTypeNameSource { get; set; } = new();

    /// <summary>Exact name source from an element's own 0xFFFF077B Floor type block.
    /// Kept separate because its validated name is valid only for existing slab-opening
    /// Reference attachments, not for the main floor product name.</summary>
    public Dictionary<uint, (int BlockCount, string? TerminalName)>
        FloorTypeNameSource { get; set; } = new();

    /// <summary>Exact name source from an element's own 0xFFFF0286 Ceiling type block.</summary>
    public Dictionary<uint, (int BlockCount, string? TerminalName)>
        CeilingTypeNameSource { get; set; } = new();

    /// <summary>Exact named layer records from an element's own unique
    /// 0xFFFF0286 CompoundCeilingType block. A malformed or duplicate owner block
    /// retains its count but clears Layers, so later consumers can fail closed.</summary>
    public Dictionary<uint, (int BlockCount, RawCompoundStructureLayer[]? Layers)>
        CeilingCompoundStructureLayerSource { get; set; } = new();

    /// <summary>Own HostObjAttr CompoundStructure fields and own SymbolInfo name.
    /// Deferred objects after these records are not part of this metadata validation.
    /// Every HostObjAttr root counts, including unsupported/malformed/duplicate roots.</summary>
    public Dictionary<uint, (int BlockCount, ushort ClassId, RawCompoundStructureLayer[]? Layers, string? TypeName)>
        HostTypeCompoundStructures { get; } = new();

    /// <summary>Own bounded class-646 CEILING_THICKNESS getter result in internal feet.
    /// HostTypeCompoundStructures and SystemTypeRoots retain all ambiguity counts.</summary>
    public Dictionary<uint, double?> CompoundCeilingTypeThicknessFeet { get; } = new();

    internal Dictionary<uint, double?> BasicWallTypeThicknessFeet { get; } = new();

    /// <summary>CurveDriver location data extracted from 0xFFFF blocks (wall location curves).
    /// Multiple candidates may be stored per wall - ElementBuilder picks the best one.</summary>
    public Dictionary<uint, List<CurveDriverData>> CurveDriverCandidates { get; set; } = new();

    /// <summary>Drive curves read from the exact root block of a wall that has one record in the partition streams
    /// (<see cref="Parsing.BlockParser.ExtractCurveDriverFromBlock"/> over the block the schema walk found). Used only where record routing
    /// removed superseded copies (<see cref="StaleCopiesRemoved"/>) and the per-chunk block scan found no candidate: the scan
    /// reached the old copy of such a wall, which routing removed, and may miss the current one.</summary>
    internal Dictionary<uint, List<CurveDriverData>> ExactCurveDrivers { get; } = new();

    // Exact owned driver fallback where the legacy block scan found no location candidate.
    internal Dictionary<uint, CurveDriverData> OwnWallLocationLines { get; } = new();

    /// <summary>FamilyInstance transforms extracted from 0x0702 blocks (Trf at offset 344).</summary>
    public Dictionary<uint, FamilyInstanceData> FamilyInstanceTransforms { get; set; } = new();

    /// <summary>Base-point coordinates keyed by element id. The survey point carries
    /// both its project-internal position and its transformed shared-coordinate position.</summary>
    public Dictionary<uint, (double[] Internal, double[] Shared)> BasePointCoordinates { get; } = new();

    /// <summary>Project locations (GeoLocation, class 2070) by element id: row-major Trf matrix,
    /// Trf origin (feet) and the id of the GeoSite symbol. A later block of an element replaces an earlier one.</summary>
    internal Dictionary<uint, (double[] Rotation, double[] Origin, int SymbolId)> GeoLocations { get; } = new();

    /// <summary>GeoSite (class 2071) latitude and longitude in radians by element id; the last block wins.</summary>
    internal Dictionary<uint, (double Latitude, double Longitude)> GeoSites { get; } = new();

    /// <summary>GeoSite (class 2071) m_placeName by element id; the last block wins.</summary>
    internal Dictionary<uint, string> GeoSitePlaceNames { get; } = new();

    /// <summary>ActiveGeoLocationTrackingElement (class 131) id -> id of the active GeoLocation; the last block wins.</summary>
    internal Dictionary<uint, uint> ActiveGeoLocations { get; } = new();

    /// <summary>Dimension / constraint geometry decoded from 0x091F blocks.
    /// Key = element ID; Value = ordered reference points + measured segment lengths.</summary>
    public Dictionary<uint, Model.DimensionData> Dimensions { get; set; } = new();

    /// <summary>Sketch-line curves (Line/Arc) decoded from 0x0346 blocks (OST_SketchLines).
    /// Key = element ID; Value = the curve as a LocationCurve (start/end or center/radius).</summary>
    public Dictionary<uint, Model.LocationCurve> SketchCurves { get; set; } = new();

    /// <summary>Authored datum geometry from Grid (0x083C) / DatumPlane (0x041C):
    /// the serialized datum text, free end, bubble end and cut vector. The element-level
    /// name remains authoritative for the grid mark because datum text may name its head type.</summary>
    public Dictionary<uint, GridDatumData> GridDatums { get; } = new();

    /// <summary>Line-style element id for 0x0346 curves whose GStyleElemId was a real id
    /// (not -1): these are detail/model lines, keyed by owner eid.</summary>
    public Dictionary<uint, int> DetailCurveStyles { get; set; } = new();

    /// <summary>All eids that own a 0x0346 curve block (detail arcs store geometry in a
    /// sub-structure the GInfoBlob scan can't decode, so DetailCurveStyles misses them).</summary>
    public HashSet<uint> CurveBlockOwners { get; set; } = new();

    /// <summary>eids owning a 0x0884 analytical Hub-node block -> OST_AnalyticalNodes.</summary>
    public HashSet<uint> AnalyticalNodeOwners { get; set; } = new();

    /// <summary>Curve line style + length from the full CurveElem schema walk (handles
    /// detail arcs the GInfoBlob pattern scan can't reach).</summary>
    public Dictionary<uint, (int GStyle, double Length)> CurveGeometry { get; set; } = new();

    /// <summary>Native parameter stores per element, deserialized from the Element base
    /// class m_pParamValueSet* containers (schema-driven).</summary>
    public Dictionary<uint, TfBlockDeserializer.NativeParams> NativeParams { get; set; } = new();
    /// <summary>Elements whose NativeParams came from a block of their own ElemTable class (the element root).</summary>
    public HashSet<uint> NativeParamsFromRootClass { get; } = new();

    /// <summary>RoomElem ROOM_NAME from an owned root and complete native stores.
    /// Duplicate or malformed owner blocks permanently clear the retained name.</summary>
    public Dictionary<uint, (int BlockCount, string? Name)> RoomNameSource { get; } = new();

    public Dictionary<uint, int> ExactRoomPhaseIds { get; } = new();
    public Dictionary<uint, bool> ExactRoomLocationless { get; } = new();
    /// <summary>RoomElem.m_cachedCircuitId of rooms with a single valid owner block.</summary>
    public Dictionary<uint, int> ExactRoomCircuitIds { get; } = new();
    /// <summary>The topology of each LevelRoomPlan (class 2306); duplicate or malformed owner blocks clear the plan.</summary>
    public Dictionary<uint, (int BlockCount, TfBlockDeserializer.RoomPlanTopology? Plan)> RoomPlanSource { get; } = new();
    public (int BlockCount, int[]? PhaseIds) ProjectPhaseOrderSource { get; set; }

    // Separate from physical GrepBlocks: suppressed RoomElem boundary caches are
    // spatial geometry, never drawable product geometry. Count every targeted block.
    public Dictionary<uint, (int BlockCount, GRepData? Boundary)> SpaceBoundarySource { get; } = new();

    /// <summary>The same cache read literally (GRepParserV2.ParseLiteralPlanarSpaceBoundary), for an owner with a single block that reads that way.</summary>
    public Dictionary<uint, GRepData> SpaceLiteralBoundaries { get; } = new();

    /// <summary>Exact class-3681 StairsElement root source. Every low-16 class match is
    /// counted; malformed or duplicate owner blocks retain their count and clear Root so
    /// downstream consumers cannot select a block by stream order.</summary>
    public Dictionary<uint, ExactTfRootSource> StairElementRootSource { get; } = new();

    /// <summary>Exact class-3742 StairsType root source used only to validate the named
    /// StairsElement.m_typeId relationship. It never rewrites the public type graph.</summary>
    public Dictionary<uint, ExactTfRootSource> StairTypeRootSource { get; } = new();

    public Dictionary<uint, int>? ElemRecOwningElementIds { get; set; }
    public Dictionary<uint, StairComponentSource.MembershipRoot> StairMembershipRoots { get; } = new();
    public Dictionary<uint, StairComponentSource.ElementClassRoot> OwnedElementClassRoots { get; } = new();

    /// <summary>The same root count as <see cref="OwnedElementClassRoots"/>, taken from the counted roots alone (the records of the project's
    /// block, or the stream walk when a stream is not read by its framing). A file with its own class schema is seeded in
    /// <see cref="OwnedElementClassRoots"/> by the schema index; read by the walk, the walk then counts the same block a second time, so
    /// every id there reads as a duplicate; the stair source reads this table instead.</summary>
    public Dictionary<uint, StairComponentSource.ElementClassRoot> WalkedElementClassRoots { get; } = new();

    /// <summary>The root count of the stair source: the stream walk's own count when the walk saw the element, else the seeded one.</summary>
    internal StairComponentSource.ElementClassRoot? StairClassRoot(uint id) =>
        WalkedElementClassRoots.TryGetValue(id, out var walked) ? walked
        : OwnedElementClassRoots.TryGetValue(id, out var seeded) ? seeded : null;
    internal bool IsParameterElement(uint id) => OwnedElementClassRoots.TryGetValue(id, out var root)
        && root.BlockCount == 1 && root.ClassId is ushort cls && TfBlockDeserializer.IsSubclassOf(cls, 2870);
    public Dictionary<uint, (int BlockCount, int? StairId)> RailingStairHostRoots { get; } = new();
    public Dictionary<uint, (int BlockCount, Parsing.TfBlockDeserializer.StairRunInput? Input)> StairRunInputRoots { get; } = new();
    public Dictionary<uint, (int BlockCount, Parsing.TfBlockDeserializer.StairNameParameters? Input)> StairNameParameterRoots { get; } = new();
    public Dictionary<uint, (int BlockCount, string? Name)> StairNamingDefinitionRoots { get; } = new();

    /// <summary>Material shading appearance decoded from 0x09B6 blocks (OST_Materials).
    /// Key = material element ID; Value = (Name, Revit 0x00BBGGRR Color.IntegerValue,
    /// Shininess, Transparency, Smoothness).</summary>
    public Dictionary<uint, MaterialAppearance> MaterialAppearances { get; set; } = new();

    /// <summary>Transparency and smoothness (integer percent) of the materials whose 0x09B6 block holds them but no
    /// shading entry (the colour of those comes from the holder vote). Key = material element ID.</summary>
    public Dictionary<uint, (int Transparency, int Smoothness)> MaterialFinishes { get; } = new();

    /// <summary>HVAC space-type load parameters decoded from 0x085A blocks (HVACLoadSpaceType).
    /// Key = space-type element ID; Value = (BIP id -> value) for the ~14 heat-gain / load /
    /// air / set-point doubles stored at fixed offsets.</summary>
    public Dictionary<uint, Dictionary<int, double>> HvacSpaceTypeParams { get; set; } = new();

    /// <summary>BIP-keyed strings found in elements' own blocks: signature
    /// [bipId:i32 (negative BIP)][len:u32][UTF-16 value]. Carries per-element string
    /// parameters (marks, descriptions, comments, keynotes...).
    /// Key = element id -> (BIP id -> value), first occurrence wins.</summary>
    public Dictionary<uint, Dictionary<int, string>> BipKeyedStrings { get; set; } = new();

    /// <summary>View display names extracted from the element's own block via the
    /// [FFFFFFFF][tag 0x0BB5][u32=1][holderId][u32=0][len][UTF-16] name-field signature.
    /// Applied to ViewCategories elements as VIEW_NAME.</summary>
    public Dictionary<uint, string> ViewNames { get; set; } = new();

    /// <summary>Level-elevation candidates from the block signature
    /// [elevation:f64][-0.0][-1.0] (the level line record). Applied only to elements whose
    /// category is OST_Levels - the marker itself also occurs in non-level blocks.</summary>
    public Dictionary<uint, double> LevelElevCandidates { get; set; } = new();

    /// <summary>HVAC schedule references from 0x085A/0x0857 blocks at fixed offsets
    /// (0xC3 = power, 0xC7 = occupancy, 0xCB = lighting schedule element id).</summary>
    public Dictionary<uint, (int Power, int Occupancy, int Lighting)> HvacScheduleRefs { get; set; } = new();

    /// <summary>Sheet info decoded from 0x03E3 view-info blocks: element id -> (sheet number,
    /// issue date). Signature: [8 zero bytes][len-prefixed short ASCII string][len-prefixed date].
    /// Applied only to OST_Sheets elements (other view classes share the block layout).</summary>
    public Dictionary<uint, (string Number, string? IssueDate)> SheetInfo { get; set; } = new();

    /// <summary>All shading entries found in chunk data: holder element id -> (color, shininess)
    /// vote counts. Fallback source for materials whose own 0x09B6 block is not decodable  - 
    /// the material's record references contain its shading-holder id.</summary>
    public Dictionary<uint, Dictionary<(int Color, int Shininess), int>> ShadingEntries { get; set; } = new();

    /// <summary>Element names extracted natively from 0xFFFF blocks (SymbolInfo.Name equivalent).</summary>
    public Dictionary<uint, string> NativeElementNames { get; set; } = new();

    /// <summary>Authoritative element names from name-table entries.
    /// Pattern: [01][holderEid][FF x28][01][targetId][len][UTF-16 name][catId][parentHolderId|GUID].
    /// Maps NAMED element id (targetId) -> name.</summary>
    public Dictionary<uint, string> ElementNameTable { get; set; } = new();

    /// <summary>Name-holder element -> named element. The holder is the small companion
    /// element that owns the name-table block (often targetId+1).</summary>
    public Dictionary<uint, uint> NameHolderTarget { get; set; } = new();

    /// <summary>Named element -> trailing id of its name entry. For family SYMBOLS this is
    /// the name holder of the parent FAMILY (symbol -> family chain). For families the
    /// trailing bytes are a GUID, so the value here is garbage - always resolve through
    /// NameHolderTarget to validate.</summary>
    public Dictionary<uint, uint> NameEntryParentHolder { get; set; } = new();

    /// <summary>Category id stored in the name-table entry for the named element.</summary>
    public Dictionary<uint, int> NameEntryCategory { get; set; } = new();

    /// <summary>Element names extracted from an external element dump - verification use only, not production.</summary>
    public Dictionary<uint, string> MeshElementNames { get; set; } = new();

    /// <summary>Episode (CreationDate) numbers, for per-element UniqueId. Populated from an external element dump only
    /// (the element -> episode index is not read from the RVT itself; see
    /// EpisodeGuids for the table this indexes into).</summary>
    public Dictionary<uint, int> ElemEpisodeNumbers { get; set; } = new();

    /// <summary>Episode table: slot index -> GUID string. Used with ElemEpisodeNumbers to
    /// construct UniqueId. Parsed from the RVT's own Global/History stream in the ordinary
    /// parsing path (RvtFileReader.ParseEpisodeGuids), so no dump is required. ExternalDumpParser
    /// overwrites this from a dump's own episode list when one is supplied, and clears it
    /// when the dump instead populated ElemEpisodeNumbers without one - the two indexings
    /// differ, so a table from one source must never be looked up
    /// with indices from the other.</summary>
    public List<string> EpisodeGuids { get; set; } = new();

    /// <summary>X: the count field of the episode list, read as the u32 immediately
    /// preceding the episode-GUID record array in Global/History (null when no valid table was
    /// found). Not wired into ElemEpisodeNumbers; the exact export ids use it together with
    /// <see cref="HistoryEpisodeGuids"/>.</summary>
    public uint? EpisodeTableX { get; set; }

    /// <summary>The episode list read from this file's Global/History. Kept apart from
    /// <see cref="EpisodeGuids"/>, which an external dump may replace.</summary>
    public IReadOnlyList<string> HistoryEpisodeGuids { get; set; } = [];

    /// <summary>DocumentHistory.m_detachGUID of Global/History
    /// (<see cref="RvtFileReader.EpisodeTableParseResult.DetachGuid"/>).</summary>
    public Guid? DocumentDetachGuid { get; set; }

    /// <summary>Exact ElemRec histories of Global/ElemTable
    /// (<c>ElementExportIds.ParseElemTable</c>); null when the stream does not
    /// decode exactly or does not have the R22 record layout.</summary>
    public Dictionary<uint, Parsing.ElemRecHistory>? ElemRecHistories { get; set; }

    // Level elevation (classIdx=0x0E17 blocks at offset 0xF4, the LEVEL_ELEV BIP key -1007102) is
    // injected directly into ElemBipDoubles during block parsing - no separate dictionary needed.

    /// <summary>Tf class schema registry parsed from Formats/Latest.
    /// Maps class name -> field definitions. Used for dynamic Tf block parsing.</summary>
    public Dictionary<string, TfClassRegistry.TfClassDef> TfClassSchemas { get; set; } = new();

    /// <summary>Block classIdx inventory: classIdx -> (count, totalBytes, sampleElemIds, sampleData).</summary>
    public Dictionary<uint, BlockClassStats> BlockClassInventory { get; set; } = new();

    /// <summary>ForgeTypeId Spec Registry extracted from 0x0FBD blocks.
    /// Maps spec URN -> (symbolURN, unitURN, conversionFactor).</summary>
    public Dictionary<string, ForgeSpecEntry> ForgeSpecRegistry { get; set; } = new();

    /// <summary>Maps ForgeId parameter URN -> spec type URN, extracted from 0x0B3A blocks.
    /// E.g., "autodesk.parameter.aec.revit.family:3016976-1.0.0" -> "autodesk.spec.aec:length-2.0.0"</summary>
    public Dictionary<string, string> ForgeParamSpecs { get; set; } = new();

    /// <summary>Binary GUID for shared parameter definition elements.
    /// Extracted from 0x0B39 blocks and Global/Latest GUID tables.</summary>
    public Dictionary<uint, Guid> ParamDefGuids { get; set; } = new();

    /// <summary>Per-family-symbol mapping from binary param key (stored in instance 0x702 blocks)
    /// to the actual project param def ID. Extracted from the family symbol's 0x725 block.
    /// Key = family symbol element ID (a TypeId); Value = (binKey -> projParamId).
    /// The 0x725 block contains a table of 30-byte entries; entries with marker u32 at offset 12 == 1
    /// form a linked list where bin paramKey of entry N maps to bin paramKey of entry N+1.</summary>
    public Dictionary<uint, Dictionary<uint, uint>> FamilyParamMaps { get; set; } = new();

    /// <summary>Per-family-symbol type-level user parameter VALUES extracted from the 0x725
    /// block's chain entries. Some chain entries carry a string (slen&gt;0) or a trailing
    /// double - these are the type-level defaults that apply to all instances of the family.
    /// Key = symbol element ID; Value = (projParamId -> string or double).</summary>
    public Dictionary<uint, Dictionary<uint, object>> FamilyTypeValues { get; set; } = new();

    /// <summary>Per-family-symbol type-level parameter values discovered by scanning 0xFFFF
    /// data blocks for the pattern `[8-byte value][FFFFFFFF sentinel][projParamId:4]`. This is
    /// how Revit family-doc default values for user parameters are stored on the family symbol.
    /// Key = symbol element ID; Value = (projParamId -> double).</summary>
    public Dictionary<uint, Dictionary<uint, double>> FamilySymbolBlockDoubles { get; set; } = new();

    /// <summary>Per-family-symbol type-level string parameter values. Pattern:
    /// `[slen:u32][string:slen*2][14 bytes pad][FFFFFFFF][projParamId:4]`.
    /// Key = symbol element ID; Value = (projParamId -> string).</summary>
    public Dictionary<uint, Dictionary<int, string>> FamilySymbolBlockStrings { get; set; } = new();

    /// <summary>Per-family-symbol type-level integer parameter values. Pattern:
    /// `[FFFFFFFF][projParamId:4][value:i32][marker:u32=1]`.
    /// Key = symbol element ID; Value = (projParamId -> int value).</summary>
    public Dictionary<uint, Dictionary<int, int>> FamilySymbolBlockInts { get; set; } = new();

    /// <summary>Per-family-symbol ElementId-valued type-level parameters scanned from 0x0725.
    /// Pattern: `[zeros>=8][elemId:4][projKey:4][zeros>=4][FFFFFFFF]` - pairs an actual project
    /// element ID (e.g. Material 267879) with the project param def key (e.g. 289055).
    /// Key = symbol element ID; Value = (projKey -> elementIdValue).</summary>
    public Dictionary<uint, Dictionary<int, int>> FamilySymbolBlockElemIds { get; set; } = new();
}

internal sealed record ExactTfRootSource(
    int BlockCount, TfBlockDeserializer.NativeParams? Root);

/// <summary>Material shading appearance: the simple-graphics color and surface finish
/// (distinct from the rendering appearance asset). Color is a 24-bit RGB int
/// (R | G&lt;&lt;8 | B&lt;&lt;16); Transparency/Smoothness are 0-100; Shininess 0-128.</summary>
internal record MaterialAppearance(string? Name, int Color, int Shininess, int Transparency, int Smoothness,
    float TransparencyFraction = -1f);

internal record GridDatumData(string Name, double[] StartPoint, double[] EndPoint, double[] CutVector, double[]? LevelInterval = null,
    double[]? XVector = null, double[]? Envelope = null, double[]? Origin = null, double[]? YVector = null);

internal record ForgeSpecEntry(string Symbol, string Unit, double Factor)
{
    /// <summary>Short symbol name extracted from URN (e.g., "mm" from "autodesk.unit.symbol:mm-1.0.1").</summary>
    public string ShortSymbol => ExtractShortName(Symbol);
    /// <summary>Short unit name extracted from URN (e.g., "millimeters" from "autodesk.unit.unit:millimeters-1.0.1").</summary>
    public string ShortUnit => ExtractShortName(Unit);

    private static string ExtractShortName(string urn)
    {
        int colon = urn.LastIndexOf(':');
        if (colon < 0) return urn;
        string name = urn[(colon + 1)..];
        int dash = name.IndexOf('-');
        return dash > 0 ? name[..dash] : name;
    }
}

internal class BlockClassStats
{
    public int Count;
    public long TotalBytes;
    public int MinSize = int.MaxValue;
    public int MaxSize;
    public List<uint> SampleElemIds = new();
    public byte[]? SampleData;
    /// <summary>Tracks how many blocks of each TypeId exist for this classIdx (typeId -> count).</summary>
    public Dictionary<uint, int> TypeIdCounts = new();
}
