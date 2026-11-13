using System.Text.Json.Serialization;

namespace RvtIO.Model
{
    internal sealed record FamilyCutLoopData(
        IReadOnlyList<double[]> Vertices,
        double[] Direction,
        int CutIndex);

    /// <summary>The datum plane of a grid (DatumPlane.m_pSurface): its parameter envelope [u0, v0, u1, v1], world origin
    /// and X / Y vectors, feet.</summary>
    internal sealed record GridPlaneData(double[] Envelope, double[] Origin, double[] XVector, double[] YVector);

    /// <summary>A Revit element: an instance or a type, with its identity, parameters and geometry.</summary>
    public class Element
    {
        /// <summary>A copy that shares all members with this element; the caller replaces what it changes.</summary>
        internal Element ShallowCopy() => (Element)MemberwiseClone();

        /// <summary>The element id.</summary>
        public int Id { get; set; }

        /// <summary>Resolved type element id; nonpositive when unresolved.</summary>
        public int TypeId { get; set; }

        /// <summary>
        /// True when the element was reconstructed from an orphan binary block and has no
        /// corresponding element-table record. Such blocks are useful for resolving nested
        /// family geometry, but are not standalone document instances.
        /// </summary>
        [JsonIgnore]
        internal bool IsBlockOnly { get; set; }

        /// <summary>True when the element record was recovered outside ElemTable.</summary>
        [JsonIgnore]
        internal bool IsDiscovered { get; set; }

        /// <summary>True when this element owns an exact serialized Level root block.</summary>
        [JsonIgnore]
        internal bool IsNativeLevel { get; set; }
        /// <summary>SCHEDULE_LEVEL_PARAM was decided from the stored association/schedule level fields; later inferred levels must not replace it.</summary>
        internal bool ScheduleLevelFromStoredFields { get; set; }

        /// <summary>Start/end z of the FamInstCurveDriver drive curve (CurveDriver.m_pCrv GLine), project coordinates (feet).</summary>
        [JsonIgnore]
        internal (double StartZ, double EndZ)? CurveDriverLineZ { get; set; }

        /// <summary>Unit direction of the straight drive curve (CurveDriver.m_pCrv GLine dirVec); absent for an arc driver.</summary>
        [JsonIgnore]
        internal (double X, double Y, double Z)? CurveDriverLineDirection { get; set; }

        /// <summary>Length of the straight drive curve (CurveDriver.m_pCrv GLine: the end-parameter span times the stored direction vector), feet.</summary>
        [JsonIgnore]
        internal double? CurveDriverLineLength { get; set; }

        /// <summary>StructuralUserData.m_copingDistance as stored (-1 when the member has no coped end).</summary>
        [JsonIgnore]
        internal double? FramingCopingDistance { get; set; }

        /// <summary>FamInstCurveDriver.m_refLevelId as stored (-1 when the driver has no reference level of its own).</summary>
        [JsonIgnore]
        internal int? CurveDriverRefLevelId { get; set; }

        /// <summary>Material whose appearance came from a shading (0x09B6) payload or its shading holder.</summary>
        [JsonIgnore]
        internal bool HasShadingAppearance { get; set; }

        /// <summary>FamilyInstance.m_instOrigin z as stored (the placement height relative to the instance's host/level).</summary>
        [JsonIgnore]
        internal double? InstanceOriginZ { get; set; }

        /// <summary>FamilyInstance.m_refs FamilyLevelRef pair as stored: base (first) and top (second) level and offset.</summary>
        [JsonIgnore]
        internal (int BaseLevel, double BaseOffset, int TopLevel, double TopOffset)? FamilyLevelRefs { get; set; }

        /// <summary>Parameters set by the NativeFieldParameters table with their validated presence domains, the
        /// parameter's state before the table set it (null: it did not exist) and the value the table left.</summary>
        [JsonIgnore]
        internal List<(int ParamId, string[] Domains, StorageType Storage, object Value, bool HasNoRevitValue)>? NativeTableAddedParams { get; set; }

        /// <summary>Parameter ids the NativeFieldParameters table created (they did not exist before it).</summary>
        [JsonIgnore]
        internal HashSet<int>? NativeTableCreatedParams { get; set; }

        /// <summary>Values the table's domain rows replaced (first touch per parameter id).</summary>
        [JsonIgnore]
        internal Dictionary<int, (object? Value, StorageType Storage, bool HasNoRevitValue)>? NativeTablePrevious { get; set; }

        /// <summary>StructCurveDriverBase.m_rotation as stored.</summary>
        [JsonIgnore]
        internal double? CurveDriverRotation { get; set; }

        /// <summary>Element.m_createdPhaseId / m_demolishedPhaseId as stored.</summary>
        [JsonIgnore]
        internal int? SerializedCreatedPhaseId { get; set; }
        [JsonIgnore]
        internal int? SerializedDemolishedPhaseId { get; set; }

        /// <summary>Structural connection type ids of the two ends as stored (StructFramUserData / StructColumnUserData
        /// m_connectionType: start/base, end/top).</summary>
        [JsonIgnore]
        internal (int First, int Second)? StructuralConnectionTypes { get; set; }

        /// <summary>Parameter ids whose value is a bounding-box estimate (AddBboxDimensionsIfMissing).</summary>
        [JsonIgnore]
        internal HashSet<int>? BoundingBoxDimensionParamIds { get; set; }

        /// <summary>The total surface (feet squared) of the solids of a door or window at the Coarse detail level; null when the
        /// element has no detail-level alternatives, so its Coarse solids are the Fine ones.</summary>
        [JsonIgnore]
        internal double? CoarseSurfaceArea { get; set; }

        /// <summary>The geometry of a column has Medium detail-level alternatives that differ from the Fine solids (a tube drawn with sharp
        /// corners at the Medium level and with round ones at the Fine level); without them the Medium solids are the Fine ones.</summary>
        [JsonIgnore]
        internal bool HasMediumAlternatives { get; set; }

        /// <summary>The volume (cubic metres) and surface (square metres) of the taken solids of a column at the Medium level when it
        /// differs from the Fine one; null when the element has no alternatives or its Medium solids have no surface integrals.</summary>
        [JsonIgnore]
        internal (double Volume, double Area)? MediumSolidTotals { get; set; }

        /// <summary>EnergyDataSettings.m_buildingTypeId as stored (the project's HVAC load building type).</summary>
        [JsonIgnore]
        internal int? EnergyBuildingTypeId { get; set; }

        /// <summary>The instance's own geometry symbol (InstanceInfo m_symbolId) as stored.</summary>
        [JsonIgnore]
        internal int? InstanceSymbolId { get; set; }

        /// <summary>Framing length regenerated into an instance symbol: StructSetbackSymFamRef.m_Length, equal at both ends.</summary>
        [JsonIgnore]
        internal double? SymbolFramingLength { get; set; }

        /// <summary>The attachment record of a column end (AttachInfo 699): <c>IsTop</c> is m_baseOrTop = 1.</summary>
        [JsonIgnore]
        internal (bool IsTop, double Offset, int Justification)? ColumnAttachment { get; set; }

        /// <summary>FamilyInstance.m_useOffsetPos as stored: whether the instance keeps its offset when its grid moves.</summary>
        [JsonIgnore]
        internal bool? InstanceUsesOffsetPosition { get; set; }

        /// <summary>FamilyInstance.m_instOrigin x and y as stored (project coordinates, feet).</summary>
        [JsonIgnore]
        internal (double X, double Y)? InstanceOriginXY { get; set; }

        /// <summary>FamilyInstance.m_bVertical as stored.</summary>
        [JsonIgnore]
        internal bool? InstanceIsVertical { get; set; }

        /// <summary>FamilyInstance.m_assocSketchPlaneId as stored.</summary>
        [JsonIgnore]
        internal int? AssocSketchPlaneId { get; set; }

        /// <summary>The instance is a curtain cell or mullion (CurtainCellFamRef / MullionFamRef with its own m_hostId).</summary>
        [JsonIgnore]
        internal bool IsCurtainMemberInstance { get; set; }

        /// <summary>FamilySymbol.m_familyId as stored.</summary>
        [JsonIgnore]
        internal int? SymbolFamilyId { get; set; }

        /// <summary>Family.m_bIsHostBased as stored (wall/ceiling/floor/roof-hosted family templates).</summary>
        [JsonIgnore]
        internal bool? FamilyIsHostBased { get; set; }

        /// <summary>The raw host is permitted for curtain/mullion families,
        /// curve-driven structural framing, or a nonzero Family.m_refTypeIds[0]. SketchPlane hosts are resolved separately.</summary>
        [JsonIgnore]
        internal bool? FamilyAllowsRawHost { get; set; }

        /// <summary>SketchPlane datum reference (PlaneRef m_datumPlaneId) as stored.</summary>
        [JsonIgnore]
        internal int? SketchPlaneDatumId { get; set; }

        /// <summary>Complete planar boundary cache of this unique OST_Rooms owner, in
        /// project coordinates (feet). Kept out of physical geometry and public JSON.</summary>
        [JsonIgnore]
        internal GeometryData? SerializedSpaceBoundary { get; set; }

        /// <summary>The cache of a room whose boundary also holds analytic cylinders (a round wall): the current profile body of the room is
        /// written from the face meshes of its Geometry node. Kept apart from <see cref="SerializedSpaceBoundary"/> (planar boundaries
        /// only), which the legacy profile target and the room area read.</summary>
        internal GeometryData? SerializedCurvedSpaceBoundary { get; set; }

        /// <summary>The planar cache of a room read literally (loops of the coedges as they are, no welding, no closed-shell validation): the
        /// body of a room whose welded cache is refused or is no manifold shell. See GRepParserV2.ParseLiteralPlanarSpaceBoundary.</summary>
        internal GeometryData? SerializedLiteralSpaceBoundary { get; set; }

        /// <summary>The outline (feet, plan coordinates) of a room or area that has no geometry block, from its circuit in the level room plan: the profile
        /// The current profile extrudes for it. Null when there is none or the circuit cannot be read exactly (see Parsing.RoomPlanBoundary).</summary>
        internal IReadOnlyList<(double X, double Y)>? SerializedPlanBoundary { get; set; }

        /// <summary>The planar boundary of a room, else its curved one: what the current profile export reads.</summary>
        internal GeometryData? SerializedSpaceBoundaryCurrent => SerializedSpaceBoundary ?? SerializedCurvedSpaceBoundary;

        /// <summary>RoomElem.m_phaseId from a unique, validated room owner root.</summary>
        [JsonIgnore]
        internal int? SerializedRoomPhaseId { get; set; }

        /// <summary>The area (square feet) of the circuit RoomElem.m_cachedCircuitId names in the single level room plan
        /// that holds this room, less its islands.</summary>
        [JsonIgnore]
        internal double? SerializedRoomPlanAreaSquareFeet { get; set; }

        /// <summary>Exact serialized Element.m_docAccess handle.</summary>
        [JsonIgnore]
        internal int? DocumentAccessHandle { get; set; }

        /// <summary>Exact serialized Element.m_famId value.</summary>
        [JsonIgnore]
        internal int? SerializedFamilyId { get; set; }

        [JsonIgnore]
        internal double? ValidatedProfileRoofAreaFeet { get; set; }

        [JsonIgnore]
        internal double? ValidatedRailingLengthFeet { get; set; }

        /// <summary>Exact serialized Element.m_unplacedOwnerId value.</summary>
        [JsonIgnore]
        internal int? SerializedUnplacedOwnerId { get; set; }

        /// <summary>Exact serialized Element.m_ownerDBViewId value.</summary>
        [JsonIgnore]
        internal int? SerializedOwnerDbViewId { get; set; }

        /// <summary>Exact BaseRailing.m_baseRailingSymbolId stored by this occurrence.</summary>
        [JsonIgnore]
        internal int? SerializedBaseRailingSymbolId { get; set; }

        /// <summary>Own unique InstanceInfo.m_symbolId resolving to a FamilySymbol
        /// with known semantic kind/owner prefix. The original symbol,
        /// separate from the display FamilyTypeId and geometry inference.</summary>
        [JsonIgnore]
        internal int? ValidatedOriginalFamilySymbolId { get; set; }

        /// <summary>Hand flip differs from facing flip in the one own FamilyInstance root (a mirrored insert); null when
        /// the root is not unique or the flags are not read.</summary>
        [JsonIgnore]
        internal bool? SerializedFlippedSymbol { get; set; }

        /// <summary>One active own computed-symbol cut with paired InstanceInfo transform;
        /// only a unique, non-inplace family with no nested instances is admitted.</summary>
        [JsonIgnore]
        internal (int SymbolId, int HostId, double[] Transform, FamilyCutLoopData Loop)? OwnSingleFamilyCutout { get; set; }

        /// <summary>Own phase-4 cutout instance IDs in source order, not inferred inverse hosts.</summary>
        [JsonIgnore]
        internal IReadOnlyList<int>? OwnWallCutoutInstanceIds { get; set; }

        [JsonIgnore]
        internal IReadOnlyList<Parsing.TfBlockDeserializer.OwnWallCutoutStep>? OwnWallCutoutSteps { get; set; }

        [JsonIgnore]
        internal double[][]? OwnWallAxisLine { get; set; }

        /// <summary>The location line of a straight own wall, bounded by its trimmed curve: start and end
        /// point, feet. Null when the side faces of the wall are not both known.</summary>
        [JsonIgnore]
        internal double[][]? OwnWallTrimmedAxis { get; set; }

        [JsonIgnore]
        internal int? OwnWallCrossSection { get; set; }

        /// <summary>Profile helper of the own wall root; null when the root is not readable or not unique.</summary>
        [JsonIgnore]
        internal Parsing.TfBlockDeserializer.OwnWallHelperInput? OwnWallHelper { get; set; }

        /// <summary>Exact BaseRailing.m_stairsRailingAttrId stored by this occurrence.</summary>
        [JsonIgnore]
        internal int? SerializedStairsRailingAttrId { get; set; }

        /// <summary>Exact BaseRailingAttr.m_topRailHeight reached through
        /// <see cref="SerializedStairsRailingAttrId"/>, in Revit internal feet.</summary>
        [JsonIgnore]
        internal double? SerializedRailingTopHeightFeet { get; set; }

        /// <summary>Exact StairsElement.m_typeId from this occurrence's unique complete
        /// class-3681 root, after resolving one unique class-3742 StairsType target.</summary>
        [JsonIgnore]
        internal int? SerializedStairTypeId { get; set; }

        /// <summary>Exact StairsElement.m_baseLevelId of the same class-3681 root (STAIRS_BASE_LEVEL_PARAM).</summary>
        [JsonIgnore]
        internal int? SerializedStairBaseLevelId { get; set; }

        /// <summary>True only while <see cref="SerializedStairTypeId"/> resolves to the
        /// unique materialized stair type validated by its own exact class-3742 root.</summary>
        [JsonIgnore]
        internal bool SerializedStairTypeLinkIsResolved { get; set; }

        /// <summary>Exact raw Tf class of this materialized StairsType target. Kept after
        /// public TypeId normalization so the exporter can revalidate the raw boundary.</summary>
        [JsonIgnore]
        internal ushort? SerializedStairsTypeClassId { get; set; }

        /// <summary>Own semantic class from a single validated Element base prefix;
        /// not the first geometry frame or the public normalized TypeId.</summary>
        [JsonIgnore]
        internal ushort? SerializedOwnElementClassId { get; set; }

        /// <summary>True when the element class root was counted once by the schema-seeded table alone (<c>OwnedElementClassRoots</c>), the
        /// count the stair source read before the walked count replaced it. The Length surrogate of a stair run was calibrated on the files
        /// where this holds, so only those runs get it.</summary>
        [JsonIgnore]
        internal bool OwnClassRootSeededOnce { get; set; }

        /// <summary>ElementHeader.m_categroryId stored in the element's record
        /// (<see cref="Parsing.ElementHeaderCategory"/>; -1 is the null id). Unlike <see cref="CategoryId"/> it is never derived from
        /// other data. Null when the walk accepted no record of this id or its records disagree.</summary>
        [JsonIgnore]
        internal int? NativeHeaderCategoryId { get; set; }

        /// <summary>FamilyInstance.m_structType as stored (StructuralType: 0 non-structural, 1 beam, 2 brace, 3 column, 4 footing, 5 unknown
        /// framing); null when the walk did not read it.</summary>
        [JsonIgnore]
        internal int? NativeStructuralType { get; set; }

        /// <summary>ElementHeader.m_designOptionId of a file with 64-bit ids; null when not decoded.</summary>
        [JsonIgnore]
        internal int? NativeHeaderDesignOptionId { get; set; }

        /// <summary>DesignOptionSet.m_mainDesignOption of a design option set (class 1080): the primary option; null when not decoded.</summary>
        [JsonIgnore]
        internal int? NativePrimaryDesignOptionId { get; set; }

        /// <summary>DesignOption.m_DesignOptionSetId of a design option (class 1074); null when not decoded.</summary>
        [JsonIgnore]
        internal int? NativeDesignOptionSetId { get; set; }

        /// <summary>DesignOption.m_strName or DesignOptionSet.m_strName; null when not decoded.</summary>
        [JsonIgnore]
        internal string? NativeDesignOptionName { get; set; }

        /// <summary>Whether the root class derives from Symbol, by the class schema of a file with 64-bit ids; null for the other files.</summary>
        [JsonIgnore]
        internal bool? NativeIsSymbol { get; set; }

        /// <summary>Name, in the file's own class schema, of the Element-derived class of this element's root block (Revit 2025 layout;
        /// null for the other layouts, whose root class is <see cref="SerializedOwnElementClassId"/>).</summary>
        [JsonIgnore]
        internal string? NativeRootClassName { get; set; }

        /// <summary>The bars of a reinforcement element (Rebar, RebarInSystem), null for every other element.</summary>
        [JsonIgnore]
        internal Parsing.RebarBars? RebarBars { get; set; }

        /// <summary>The type and the beams of a beam system (JoistSystem), null for every other element.</summary>
        [JsonIgnore]
        internal Parsing.JoistSystemData? JoistSystem { get; set; }

        /// <summary>For a file with a <see cref="NativeRootClassName"/>: that class followed by its base classes in the file's schema.</summary>
        [JsonIgnore]
        internal IReadOnlyList<string>? NativeRootClassChain { get; set; }

        /// <summary>Name of the element's own root class in every layout: the schema name for a file of its own class numbering
        /// (<see cref="NativeRootClassName"/>), else the name of <see cref="SerializedOwnElementClassId"/> in the Revit 2022 table;
        /// null when the reader decoded no root class.</summary>
        [JsonIgnore]
        internal string? RootClassName => NativeRootClassName
            ?? (SerializedOwnElementClassId is ushort id ? Parsing.TfClassNames.NameOf(id) : null);

        /// <summary><see cref="RootClassName"/> for the property rules. For a file that numbers its classes itself
        /// (<see cref="NativeRootClassName"/>) the rules run too: their unvalidated cases are guarded where they are decided (the
        /// identity block, the parameter provenance filter).</summary>
        [JsonIgnore]
        internal string? RuleClassName => RootClassName;

        /// <summary><see cref="RootClassIs"/> for the same rules as <see cref="RuleClassName"/>.</summary>
        internal bool RuleClassIs(string className) => RootClassIs(className);

        /// <summary>True when the root class is <paramref name="className"/> or derives from it; false when the class is unknown.</summary>
        internal bool RootClassIs(string className) => NativeRootClassName != null
            ? NativeRootClassChain?.Contains(className) == true
            : SerializedOwnElementClassId is ushort id && Parsing.TfClassNames.IsSubclassOf(Parsing.TfClassNames.NameOf(id) ?? "", className);

        /// <summary>Independent CRC-validated Global/ElemTable owning element id.</summary>
        [JsonIgnore]
        internal int? SerializedOwningElementId { get; set; }

        /// <summary>Own helper core, reciprocally owned and equal to the native typed
        /// inverse ownership set. Ordering is not an API naming ordinal.</summary>
        [JsonIgnore]
        internal Internal.StairComponentSource.CoreComponent[]? SerializedStairCore { get; set; }
        /// <summary>Levels a multistory stair is repeated on, in stored order.</summary>
        [JsonIgnore]
        internal int[]? SerializedStairCopyLevelIds { get; set; }

        /// <summary>Single complete own run graph's native style and cached API path
        /// directions, with independent semantic class and reciprocal ElemRec owner.</summary>
        [JsonIgnore]
        internal Parsing.TfBlockDeserializer.StairRunInput? SerializedStairRunInput { get; set; }

        /// <summary>Native IDS_STAIR instance name from the saved locale's resource,
        /// admitted only for a complete own native stair/type/core. Not the type Name.</summary>
        [JsonIgnore]
        internal string? ValidatedStairOccurrenceName { get; set; }

        /// <summary>Own complete parameter-store scope for native instance naming;
        /// null is unvalidated, an empty list is validated absence. Never public heuristics.</summary>
        [JsonIgnore]
        internal List<Parameter>? SerializedStairNameParameters { get; set; }

        /// <summary>Own single class-485 BaseRailing.m_stairsId, separately from
        /// ElemRec ownership, registered lists, and the adjustment start component.</summary>
        [JsonIgnore]
        internal int? SerializedRailingStairHostId { get; set; }

        /// <summary>Material.m_name from this material's own single MaterialElem root.</summary>
        [JsonIgnore]
        internal string? SerializedMaterialName { get; set; }

        /// <summary>The transparency of the material as the file stores it (a float fraction, 0.975 where the Revit parameter
        /// is the integer percent 97); null when the block did not give it.</summary>
        [JsonIgnore]
        internal double? SerializedMaterialTransparency { get; set; }

        /// <summary>The lowest and highest elevation (feet) at which a Grid is shown in plan views
        /// (the V envelope of its DatumPlane surface).</summary>
        [JsonIgnore]
        internal double[]? SerializedGridLevelInterval { get; set; }

        /// <summary>Ordered layers of this CompoundCeilingType's own CompoundStructure,
        /// from its single exact class-646 root.</summary>
        [JsonIgnore]
        internal Internal.RawCompoundStructureLayer[]? SerializedCeilingTypeLayers { get; set; }

        /// <summary>Own ordered HostObjAttr CompoundStructure layer field records, separate from
        /// legacy public Layers so source cannot change geometry or material contents.</summary>
        [JsonIgnore]
        internal Internal.RawCompoundStructureLayer[]? SerializedHostTypeLayers { get; set; }

        /// <summary>Own thick CompoundCeilingType's original getter result, in internal feet.
        /// Only the validated null-vertical-structure branch; not a public parameter override.</summary>
        [JsonIgnore]
        internal double? ValidatedCeilingTypeThicknessFeet { get; set; }

        /// <summary>Own basic-wall type getter, bound through the exact occurrence type link.
        /// Separate from legacy Layers and propagated parameters; internal feet.</summary>
        [JsonIgnore]
        internal double? ValidatedWallTypeThicknessFeet { get; set; }

        // Kept separate until the writer has completed the host/void contract.
        [JsonIgnore]
        internal GeometryData? OwnPrimaryPlanarBody { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        internal double[]? OwnWallSide0NormalCurrent { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        internal GeometryData? OwnCurrentCutPairBodyCurrent { get; set; }

        /// <summary>VWall.m_isFlipped read from the wall's own native record; null when not decoded.</summary>
        [JsonIgnore]
        internal bool? SerializedWallFlipped { get; set; }

        /// <summary>GroupMembership.m_groupId of the element: the model group instance it belongs to.</summary>
        [JsonIgnore]
        internal int? SerializedGroupId { get; set; }

        /// <summary>FamInstDesignPropertyManager.m_idRoom of a family instance: the room it is in.</summary>
        [JsonIgnore]
        internal int? SerializedRoomId { get; set; }

        /// <summary>AreaMeasureElem.m_areaElemName and m_areaElemDescription of an area scheme.</summary>
        [JsonIgnore]
        internal (string Name, string Description)? SerializedAreaScheme { get; set; }

        /// <summary>RoomElem.m_bIsLocationless: the room or area is not placed in a plan.</summary>
        [JsonIgnore]
        internal bool? SerializedRoomIsLocationless { get; set; }

        /// <summary>True for the defining (template) instance of a group type and for its members: they are stored
        /// in the file but are not elements of the model.</summary>
        [JsonIgnore]
        internal bool IsGroupTemplate { get; set; }

        /// <summary>The localized "Model Group" family label of a model group instance.</summary>
        [JsonIgnore]
        internal string? SerializedGroupFamilyLabel { get; set; }

        /// <summary>The family label Revit puts before the name of a level ("Level") or a topography surface
        /// ("Surface") in its "Family:Type" names, in the locale of the system family names.</summary>
        [JsonIgnore]
        internal string? SerializedUiFamilyLabel { get; set; }

        /// <summary>StairsType.m_minTreadWidthOnInsideBoundary in feet (own stairs type root).</summary>
        [JsonIgnore]
        internal double? SerializedStairsTypeMinInsideTreadFeet { get; set; }

        /// <summary>StairsRunType.m_nosingLength in feet (own stairs run type root).</summary>
        [JsonIgnore]
        internal double? SerializedStairRunTypeNosingFeet { get; set; }

        /// <summary>Family instance connectors in stored order: Connector.m_nIndex, the stored hvac (2381) or
        /// piping (2382) connector calculation direction and its system type (null when the domains are mixed
        /// or the connector objects do not match the calculations one to one).</summary>
        [JsonIgnore]
        internal IReadOnlyList<(int Index, int Direction, int SystemType)>? SerializedMepConnectors { get; set; }

        /// <summary>Every connector of the element in stored order: Connector.m_nIndex, the stored flow direction
        /// (SegmentConnectorCalculation.m_eDirection, 0 when none) and Connector.m_arrRefs, the connected
        /// (element id, connector index, connection type) entries. The element's own other connectors are among them.</summary>
        [JsonIgnore]
        internal IReadOnlyList<(int Index, int Direction, IReadOnlyList<(int Id, int Index, int Type)> Refs)>? SerializedConnectorGraph { get; set; }

        /// <summary>The MEP system element each connector of <see cref="SerializedConnectorGraph"/> belongs to, in the
        /// same order (the connector's m_idSystem from the segment calculation of a curve or the hvac/piping
        /// calculation of a family instance; 0 or negative for none).</summary>
        [JsonIgnore]
        internal IReadOnlyList<int>? SerializedConnectorSystems { get; set; }

        /// <summary>The mode of each connector of <see cref="SerializedConnectorGraph"/>, in the same order; null when the walk of
        /// the file does not know the mode (Revit 2022 layout).</summary>
        [JsonIgnore]
        internal IReadOnlyList<int>? SerializedConnectorModes { get; set; }

        /// <summary>The vertical extent (feet) of a Grid's finite surface: the interval of levels the grid belongs to.
        /// Null for a grid whose surface was not decoded.</summary>
        [JsonIgnore]
        internal (double Min, double Max)? SerializedGridZInterval
        {
            get => SerializedGridLevelInterval is { Length: 2 } interval ? (interval[0], interval[1]) : null;
            set => SerializedGridLevelInterval = value is { } v ? [v.Min, v.Max] : null;
        }

        /// <summary>MEPCurve.m_idSystemType, the system type an MEP curve (duct, flex duct, pipe) stores itself, also when it
        /// belongs to no system (its System Type parameter is then -1); null when the element stores none.</summary>
        [JsonIgnore]
        internal int? SerializedCurveSystemTypeId { get; set; }

        /// <summary>ZoneElement.m_bDefault of an HVAC zone (whether it is the default zone); null for other elements.</summary>
        [JsonIgnore]
        internal bool? SerializedZoneIsDefault { get; set; }

        /// <summary>RoomElem.m_zoneElementId of a space: the HVAC zone it belongs to; null when it belongs to none.</summary>
        [JsonIgnore]
        internal int? SerializedSpaceZoneId { get; set; }

        /// <summary>RbsSystem.m_strName and m_typeId of an MEP system element (piping or duct system); null for other elements.</summary>
        [JsonIgnore]
        internal (string Name, int TypeId)? SerializedMepSystem { get; set; }

        /// <summary>The base equipment (panel) of an electrical circuit, from its connector manager; null for other elements and for a circuit with none.</summary>
        [JsonIgnore]
        internal int? SerializedCircuitBaseEquipmentId { get; set; }

        /// <summary>Own SymbolInfo type name with the original localized system family,
        /// used for LayerSet metadata and IFC floor/ceiling Type.Name; the name remains
        /// separate from the public Name/FamilyName projection.</summary>
        [JsonIgnore]
        internal string? ValidatedHostTypeLayerSetName { get; set; }

        /// <summary>Whether the wall's own root carries a top attachment
        /// (JoinToRoofGStep with m_baseOrTop = 1); null when unvalidated.</summary>
        [JsonIgnore]
        internal bool? SerializedWallJoinedToTop { get; set; }

        /// <summary>The export id of the element: its episode GUID with the last
        /// 32 bits XORed with the original element id (Global/History and Global/ElemTable);
        /// null when either table is not read exactly.</summary>
        [JsonIgnore]
        internal Guid? SerializedExportId { get; set; }

        /// <summary>The episode GUID with its last 32 bits XORed with the element's current id (not its original id):
        /// the base the current profile gives a sub-element GUID.</summary>
        [JsonIgnore]
        internal Guid? SerializedHandleExportId { get; set; }

        /// <summary>Own wall/floor/ceiling type link, validated against one unique
        /// materialized type's exact root and category. Never a public type guess.</summary>
        [JsonIgnore]
        internal int? SerializedSystemTypeId { get; set; }

        /// <summary>Exact own native root class of that wall/floor/ceiling type.
        /// Kept independently of localization and the public TypeId normalization.</summary>
        [JsonIgnore]
        internal ushort? SerializedSystemTypeClassId { get; set; }

        /// <summary>Control joins of the wall's own driver: per end (0 start, 1 end) the joined
        /// element and its joined end; null when the wall block is not read exactly.</summary>
        [JsonIgnore]
        internal IReadOnlyList<Parsing.TfBlockDeserializer.WallEndJoin>? SerializedWallEndJoins { get; set; }

        /// <summary>Exact StairsElement.m_actualNumberOfRisers occurrence value.</summary>
        [JsonIgnore]
        internal int? SerializedActualNumberOfRisers { get; set; }

        /// <summary>Exact StairsElement.m_actualRiserHeight in Revit internal feet.</summary>
        [JsonIgnore]
        internal double? SerializedActualRiserHeightFeet { get; set; }

        /// <summary>Exact StairsElement.m_actualTreadDepth in Revit internal feet.</summary>
        [JsonIgnore]
        internal double? SerializedActualTreadDepthFeet { get; set; }

        /// <summary>Exact WallType.m_function serialized by this element's own
        /// NewCurtainWallType root block (0=Interior, 1=Exterior, ...).</summary>
        [JsonIgnore]
        internal int? SerializedCurtainWallFunction { get; set; }

        /// <summary>FUNCTION_PARAM stored by this FamilySymbol's own FamilyParams prefix (the first
        /// matching NamedParam, its separate integer member), accepted only for a unique root block
        /// and a value from 0 to 5. A symbol without such a record has no value here, which is not
        /// validation that the parameter is absent.</summary>
        [JsonIgnore]
        internal int? SerializedFamilySymbolFunction { get; set; }

        /// <summary>Ids of the built-in parameters the element stores in its own parameter value sets
        /// (double, integer, text and element id containers) and as negative NamedParams. Exactly
        /// these parameters of an element are listed, so a stored parameter with an empty text value is
        /// still "held" for a property that reads it by name.</summary>
        [JsonIgnore]
        internal HashSet<int>? NativeStoredParameterIds { get; set; }

        /// <summary>Ids of the parameters stored in the element's four root parameter stores (double, integer,
        /// string, element id value sets); null when the element's native record was not decoded.
        /// <see cref="NativeParameterStoresComplete"/> tells whether every store of the record was read.</summary>
        internal HashSet<int>? NativeParameterStoreIds { get; set; }

        internal bool NativeParameterStoresComplete { get; set; }

        /// <summary>Element.m_cellList of the decoded record: 0 when the element has no cell list, otherwise the class
        /// tag of the owned CellList object (or -1 for a plain handle); null when the record was not decoded.</summary>
        internal int? NativeCellListTag { get; set; }

        /// <summary>Class tags of the cells held by <see cref="NativeCellListTag"/>'s CellList; null when the deferred
        /// walk of the record did not reach the CellList object.</summary>
        internal int[]? NativeCellClasses { get; set; }

        /// <summary>The deferred walk of the record consumed every queued object: <see cref="NativeCellClasses"/> and
        /// <see cref="NativeNamedParameters"/> are complete.</summary>
        internal bool NativeTailWalkComplete { get; set; }

        /// <summary>Negative (built-in) ids of the NamedParams the record's FamilyParams hold, with their instance flag.</summary>
        internal (int Id, bool Instance)[]? NativeNamedParameters { get; set; }

        /// <summary>Values of the schema fields the parameter-list rules read (<see cref="RvtIO.Parsing.TfBlockDeserializer.ListFactFieldKeys"/>),
        /// keyed Declaring:Name; a field the record did not hold is absent.</summary>
        internal Dictionary<string, int>? NativeListFields { get; set; }

        /// <summary>Values of the schema fields the room of a family instance is found from
        /// (<see cref="RvtIO.Parsing.TfBlockDeserializer.RoomRuleFieldKeys"/>), keyed Declaring:Name; a field the record did not hold is
        /// absent.</summary>
        internal Dictionary<string, double>? NativeRoomFields { get; set; }

        /// <summary>Tf class tag of the element's record (the class the record is an instance of, by its schema name); null when the
        /// record was not walked.</summary>
        internal int? NativeRecordClass { get; set; }

        /// <summary>The vertical extent of a Grid's datum plane in feet, [lowest, highest]: the V interval of
        /// <c>DatumPlane.m_pSurface</c> placed at its origin. The grid is put on the levels inside it.
        /// Null for other elements and planes that are not vertical.</summary>
        [JsonIgnore]
        internal double[]? GridElevationRangeFeet { get => SerializedGridLevelInterval; set => SerializedGridLevelInterval = value; }

        /// <summary>Own unique NewCurtainWallType SymbolInfo name, not a mark or an
        /// occurrence-name fallback. Kept separate from public identity/name fields.</summary>
        [JsonIgnore]
        internal string? SerializedCurtainWallTypeName { get; set; }

        /// <summary>Own unique BasicWallType SymbolInfo name, independent of public name fallbacks.</summary>
        [JsonIgnore]
        internal string? SerializedBasicWallTypeName { get; set; }

        /// <summary>Own unique StackedWallType root. Such a type's placed parent wall is
        /// a container, not a separate IFC wall product; its component walls remain sources.</summary>
        [JsonIgnore]
        internal bool IsSerializedStackedWallType { get; set; }

        /// <summary>Element name (first useful string found near this element in the binary stream).</summary>
        public string? Name { get; set; }

        /// <summary>Exact floor Reference source reached through the instance's own
        /// serialized m_floorAttributesId. An explicit instance/type Reference has priority;
        /// otherwise the fallback is admitted only from one unique type-owned 0xFFFF077B
        /// block. This is isolated from <see cref="Name"/>: it may override only floor/slab-
        /// opening Reference properties, never IFC identity fields.</summary>
        [JsonIgnore]
        internal string? ValidatedFloorTypeReferenceName { get; set; }

        /// <summary>The own name of the floor type (its unique HostObjAttr root, validated), set when no Reference parameter
        /// decides the floor Reference. The current IFC profile prefers it to <see cref="ValidatedFloorTypeReferenceName"/>, which
        /// ends in a text parameter of a type that carries some.</summary>
        [JsonIgnore]
        internal string? ValidatedFloorOwnTypeName { get; set; }

        /// <summary>True when <see cref="ValidatedFloorTypeReferenceName"/> came from an
        /// explicit instance/type Reference parameter rather than the terminal type name.
        /// Such a value can create the main product Pset even when the parsed element name
        /// is missing.</summary>
        [JsonIgnore]
        internal bool ValidatedFloorTypeReferenceIsExplicit { get; set; }

        /// <summary>An exact floor/type relationship was validated, but a higher-priority
        /// Reference parameter could not be reproduced deterministically. Lower-priority
        /// name source must not leak past that parameter.</summary>
        [JsonIgnore]
        internal bool ValidatedFloorTypeReferenceIsBlocked { get; set; }

        /// <summary>Exact Ceiling.m_ceilingAttributesId carried from this occurrence's
        /// own class-642 root block after owner, class, uniqueness and category validation.
        /// It is isolated from the public type graph and must never rewrite
        /// <see cref="TypeId"/> or type-related parameters.</summary>
        [JsonIgnore]
        internal int? SerializedCeilingTypeId { get; set; }

        /// <summary>Exact ceiling QTO Reference source reached through
        /// <see cref="SerializedCeilingTypeId"/>. It is isolated from product identity,
        /// public names, the structural type graph and material resolution.</summary>
        [JsonIgnore]
        internal string? ValidatedCeilingTypeReferenceName { get; set; }

        /// <summary>True when the validated ceiling Reference came from an explicit
        /// instance/serialized-type parameter rather than terminal type-name source.</summary>
        [JsonIgnore]
        internal bool ValidatedCeilingTypeReferenceIsExplicit { get; set; }

        /// <summary>The exact ceiling relationship was validated, but a higher-priority
        /// Reference parameter could not be reproduced deterministically.</summary>
        [JsonIgnore]
        internal bool ValidatedCeilingTypeReferenceIsBlocked { get; set; }

        /// <summary>Exact window Reference source reached through the instance's own
        /// directly-decoded FamilyInstance.m_masterSymbolId (never the general TypeId/
        /// FamilyTypeId fan-out, which can be canonicalized from this same field but can
        /// also be inherited from an earlier, unreliable heuristic). The terminal name comes
        /// from the resolved symbol's ElementNameTable entry - the name-holder record
        /// of the name table scanner for FamilySymbol ids. Isolated
        /// from <see cref="Name"/>: it may override only Pset_QuantityTakeOff/Pset_WindowCommon
        /// Reference, never IFC identity fields.</summary>
        [JsonIgnore]
        internal string? ValidatedWindowTypeReferenceName { get; set; }

        /// <summary>True when <see cref="ValidatedWindowTypeReferenceName"/> came from an
        /// explicit instance/type Reference parameter rather than the resolved symbol's
        /// name-table terminal name.</summary>
        [JsonIgnore]
        internal bool ValidatedWindowTypeReferenceIsExplicit { get; set; }

        /// <summary>An exact window/symbol relationship was validated, but a higher-priority
        /// Reference parameter could not be reproduced deterministically. Lower-priority
        /// name-table source must not leak past that parameter.</summary>
        [JsonIgnore]
        internal bool ValidatedWindowTypeReferenceIsBlocked { get; set; }

        /// <summary>Exact IfcBuildingElementProxy Reference source, same mechanism and
        /// precedence as <see cref="ValidatedWindowTypeReferenceName"/> - the instance's own
        /// directly-decoded FamilyInstance.m_masterSymbolId, resolved through
        /// ElementNameTable, never the general TypeId/FamilyTypeId fan-out (that fan-out
        /// runs before ApplyNativeParams canonicalizes TypeId from this same field).
        /// Unlike Window, the proxy population spans
        /// many BuiltInCategory ids (generic model, specialty/mechanical/electrical equipment,
        /// planting, mass, ...), so the gate here is a category set, not a single constant.
        /// Isolated from <see cref="Name"/>: it may override only Pset_QuantityTakeOff/
        /// Pset_BuildingElementProxyCommon Reference, never IFC identity fields.</summary>
        [JsonIgnore]
        internal string? ValidatedProxyTypeReferenceName { get; set; }

        /// <summary>True when <see cref="ValidatedProxyTypeReferenceName"/> came from an
        /// explicit instance/type Reference parameter rather than the resolved symbol's
        /// name-table terminal name.</summary>
        [JsonIgnore]
        internal bool ValidatedProxyTypeReferenceIsExplicit { get; set; }

        /// <summary>An exact proxy/symbol relationship was validated, but a higher-priority
        /// Reference parameter could not be reproduced deterministically. Lower-priority
        /// name-table source must not leak past that parameter.</summary>
        [JsonIgnore]
        internal bool ValidatedProxyTypeReferenceIsBlocked { get; set; }

        /// <summary>Own FamilyInstance -> FamilySymbol -> SymbolInfo validation, confined to
        /// distribution-flow QTO/Common Reference. Never changes product/type identity.</summary>
        [JsonIgnore]
        internal string? ValidatedDistributionTypeReferenceName { get; set; }
        [JsonIgnore]
        internal bool ValidatedDistributionTypeReferenceIsExplicit { get; set; }
        [JsonIgnore]
        internal bool ValidatedDistributionTypeReferenceIsBlocked { get; set; }

        /// <summary>Own RoofAttributes/StairsRailingAttr name for QTO Reference only.</summary>
        [JsonIgnore]
        internal string? ValidatedRoofOrRailingTypeReferenceName { get; set; }
        [JsonIgnore]
        internal bool ValidatedRoofOrRailingTypeReferenceIsExplicit { get; set; }
        [JsonIgnore]
        internal bool ValidatedRoofOrRailingTypeReferenceIsBlocked { get; set; }

        /// <summary>Localized system family name of THIS type element (Revit's own resource
        /// string for its exact root class). Set only on type elements; occurrences read it
        /// through their type link. Never derived from names or marks.</summary>
        [JsonIgnore]
        internal string? ValidatedSystemFamilyName { get; set; }

        /// <summary>THIS stair component type's own SymbolInfo name, independent of
        /// the public Name/TypeId propagation. Never a borrowed level or occurrence name.</summary>
        [JsonIgnore]
        internal string? ValidatedStairComponentTypeName { get; set; }

        /// <summary>THIS CorniceAttr type's own SymbolInfo name. Never a borrowed
        /// public Name; used only with its original system family for IFC type naming.</summary>
        [JsonIgnore]
        internal string? ValidatedCorniceTypeName { get; set; }

        /// <summary>THIS circular duct type's own SymbolInfo name, independent of
        /// general Name propagation; used only for IFC type-object naming.</summary>
        [JsonIgnore]
        internal string? ValidatedDuctTypeName { get; set; }

        /// <summary>THIS pipe, flex pipe, pipe insulation or cable tray type's own SymbolInfo name; the name table can hold a
        /// different string for these types.</summary>
        [JsonIgnore]
        internal string? ValidatedMepTypeName { get; set; }

        /// <summary>Localized system family name of THIS pipe, flex pipe, pipe insulation or cable tray type. Kept apart
        /// from <see cref="ValidatedSystemFamilyName"/>: only the current profile naming reads it (the legacy profile has no such
        /// products).</summary>
        [JsonIgnore]
        internal string? ValidatedMepFamilyName { get; set; }

        /// <summary>The type of THIS MEP curve occurrence (pipe, flex pipe, pipe insulation, duct, cable tray) read
        /// from its own RbsCurve.m_idType and checked against the type's exact root class. The general
        /// FamilyTypeId can name another element's symbol for these occurrences.</summary>
        [JsonIgnore]
        internal int? SerializedMepCurveTypeId { get; set; }

        /// <summary>Exact FamilyInstance.m_masterSymbolId decoded for THIS occurrence
        /// (schema field presence, not an identity/name value). Set only when the native
        /// parameter store actually carries the field (see ApplyNativeParams). Used to gate
        /// IfcRailingType emission: only component-family railings have an own m_masterSymbolId
        /// and are typed; system-family railings have no such field and are never typed.
        /// This is a schema-presence fact, not ElementId/Tag/family/type-name branching - the same kind
        /// of signal <see cref="ValidatedProxyTypeReferenceName"/> already reads directly from
        /// NativeParams.Fields["m_masterSymbolId"].</summary>
        [JsonIgnore]
        internal int? NativeMasterSymbolId { get; set; }

        /// <summary>The element's type id came from the field its root class keeps it in
        /// (<see cref="RvtIO.Parsing.NativeTypePointers"/>), so type guesses must not replace it.</summary>
        internal bool TypeFromNativePointer { get; set; }

        /// <summary>Closed-shell quantities of the solids at the Coarse, Medium and Fine detail levels (index 0, 1, 2); null at a level whose
        /// solids are not all closed shells of plane faces. See <c>ElementBuilder.ApplyInstanceQuantities</c>.</summary>
        [JsonIgnore]
        internal Parsing.SolidQuantities.Shell?[] LevelShells { get; set; } = new Parsing.SolidQuantities.Shell?[3];

        /// <summary>Number of solids at Coarse, Medium and Fine.</summary>
        [JsonIgnore]
        internal int[] LevelSolidCounts { get; set; } = new int[3];

        /// <summary>Revit BuiltInCategory ID (e.g. -2000011 = OST_Walls).</summary>
        public int? CategoryId { get; set; }

        /// <summary>BuiltInCategory name (e.g. "OST_Walls").</summary>
        public string? Category { get; set; }

        /// <summary>The parameters of the element.</summary>
        public List<Parameter> Parameters { get; set; } = new();

        /// <summary>The axis-aligned outline the element header stores: [minX, minY, minZ, maxX, maxY, maxZ] in Revit internal
        /// units (feet, project coordinates) - the box the application reports. Null when the header stores no outline; the
        /// tighter box of the decoded geometry stays on the geometry.</summary>
        public double[]? BoundingBox { get; set; }

        /// <summary>True when <see cref="BoundingBox"/> is the outline the element header stores (the box the application reports), which
        /// the reader sets for every element whose header stores one, and not a box taken from decoded geometry. An element that has no decoded
        /// geometry at all can have an outline, so the outline says nothing about the geometry: see <see cref="DecodedBox"/>.</summary>
        internal bool BoundingBoxIsHeaderOutline { get; set; }

        /// <summary>The box the element carries apart from the outline of its header, the one the IFC export measures bodies by and takes the presence
        /// of geometry from: <see cref="BoundingBox"/> of an element that was not read from a file (its box is its own), and the tight box of the
        /// decoded <see cref="Geometry"/> of an element whose box is the outline of its header, none when it has no geometry (the application
        /// reports an outline for a roof that only holds curtain panels). Everything that reads the box of an element in the IFC export reads this one.</summary>
        internal double[]? DecodedBox => BoundingBoxIsHeaderOutline ? Geometry?.BoundingBox : BoundingBox;

        /// <summary>
        /// Revit UniqueId: "{document-GUID}-{elemId:08x}".
        /// Constructed from the document GUID extracted from binary data.
        /// </summary>
        public string? UniqueId { get; set; }

        /// <summary>Family/type name resolved from FamId or ELEM_TYPE_PARAM reference.</summary>
        public string? FamilyName { get; set; }

        /// <summary>Diagnostic (RVTIO_ALL_FIELDS=1): all named scalar schema fields, "Declaring:Name" -> value.</summary>
        public Dictionary<string, object>? DebugNativeFields { get; set; }

        /// <summary>Export-only Family:Type display name from this element's own name-table
        /// entry and parent-family chain. Never inherited from its TypeId or occurrences.</summary>
        [JsonIgnore]
        internal string? ValidatedIfcTypeObjectName { get; set; }

        /// <summary>The type-name part of <see cref="ValidatedIfcTypeObjectName"/>, without the family.</summary>
        [JsonIgnore]
        internal string? ValidatedIfcTypeOwnName { get; set; }

        /// <summary>WallType.m_function from a unique validated own BasicWallType root.</summary>
        [JsonIgnore]
        internal int? SerializedBasicWallTypeFunction { get; set; }

        /// <summary>IDs of directly referenced elements.</summary>
        public List<int>? References { get; set; }

        /// <summary>Strings found near referenced elements, grouped by their TypeId.</summary>
        public Dictionary<string, List<string>>? RefStrings { get; set; }

        /// <summary>Parsed geometry from GRep block (faces, edges, vertices).</summary>
        public GeometryData? Geometry { get; set; }

        /// <summary>Exact FamilySymbol complex-cut BRep in family-local coordinates.</summary>
        [JsonIgnore]
        internal GeometryData? FamilyCutGeometry { get; set; }

        /// <summary>Dummy-host closure plane used to substitute the real host thickness.</summary>
        [JsonIgnore]
        internal double[]? FamilyCutClosurePlaneOrigin { get; set; }

        [JsonIgnore]
        internal double[]? FamilyCutClosurePlaneNormal { get; set; }

        /// <summary>Exact InsertableObj.m_pCutLoops profiles and their serialized cut
        /// directions from the FamilySymbol block, in family-local coordinates.</summary>
        [JsonIgnore]
        internal List<FamilyCutLoopData>? FamilyCutLoops { get; set; }

        /// <summary>Number of InsertableObj.m_pCutLoops entries of a FamilySymbol, whether or
        /// not they decode: Revit can cut a wall out with the instance's profile
        /// only when there is one.</summary>
        [JsonIgnore]
        internal int? SerializedCutLoopCount { get; set; }

        /// <summary>Elements recorded by this host's serialized cut-out geometry steps.</summary>
        [JsonIgnore]
        internal List<int>? CuttingElementIds { get; set; }

        /// <summary>Every element named by this host's cut-out geometry steps, in step order: the cutter
        /// of each inserted-element step (<c>Cutout</c>, as in <see cref="CuttingElementIds"/>) and the other
        /// host of each host-to-host pair step (CutPairGStep and its siblings). Null when not every tracked
        /// step decoded.</summary>
        [JsonIgnore]
        internal List<(ushort ClassId, int ElementId, bool Cutout)>? CutOutSteps { get; set; }

        /// <summary>Location curve for wall elements (line/arc with start/end/direction).</summary>
        public LocationCurve? Location { get; set; }

        /// <summary>For a grid: its datum plane.</summary>
        [JsonIgnore]
        internal GridPlaneData? GridPlane { get; set; }

        /// <summary>CompoundStructure layers (wall/floor/ceiling types). Each layer has thickness, function, material.</summary>
        public List<MaterialLayer>? Layers { get; set; }

        /// <summary>Dimension / constraint geometry (reference points + measured value), decoded from 0x091F blocks.</summary>
        public DimensionData? Dimension { get; set; }

        /// <summary>
        /// For a sketch-owning element (the element that references its OST_SketchLines):
        /// the closed sketch loops, each an ordered list of [x, y, z] boundary points (feet).
        /// Reconstructed from the member sketch lines by shared-endpoint connectivity.
        /// </summary>
        public List<List<double[]>>? SketchLoops { get; set; }

        /// <summary>For a sketch line in a simple closed loop of its owner's sketch (every
        /// endpoint shared by exactly two of the owner's lines): the smallest line id of that
        /// loop. Revit numbers the openings of a host's sketch in this order.</summary>
        [JsonIgnore]
        internal int? SketchLoopKey { get; set; }

        /// <summary>Host element ID for inserts (doors/windows -> host wall).</summary>
        public int? HostId { get; set; }

        /// <summary>
        /// Exact InsertableInst.m_hostId stored in the RVT stream. A value of -1 is
        /// significant: the instance is not a host-cutting insert, even when its generic
        /// reference graph contains a wall.
        /// </summary>
        public int? SerializedHostId { get; set; }

        /// <summary>Own direct CurtainCellFamRef/MullionFamRef membership, distinct from
        /// the host which this instance can cut. Attached only from a unique native root.</summary>
        [JsonIgnore]
        internal int? SerializedCurtainHostId { get; set; }

        /// <summary>The geometry of an instance whose own Faces are all unfilled (a void or cut helper, see
        /// <see cref="ProductRules.HasOnlyUnfilledFaces"/>) while its computed symbol has filled ones: the symbol's geometry placed by the
        /// instance's own transform, with that symbol as its one mapped instance. The current profile exports such an instance with the body of the
        /// symbol (for example louvers whose own geometry is the box they fill). Null for any other element.</summary>
        [JsonIgnore]
        internal GeometryData? SerializedComputedSymbolGeometry { get; set; }

        /// <summary>The panels of a curtain wall in the order its CurtainGrid stores them (m_panelIds, the grids of its
        /// drivers one after the other): the order they are exported in. Null for any other element and for a wall whose
        /// root did not decode exactly.</summary>
        [JsonIgnore]
        internal int[]? SerializedCurtainPanelOrder { get; set; }

        /// <summary>Exact host-of-host m_hostId reached through this element's own
        /// serialized m_hostId and the raw NativeParams record of that direct host.
        /// Unlike <see cref="HostId"/>, this remains available when the intermediary
        /// host has no materialized <see cref="Element"/>.</summary>
        [JsonIgnore]
        internal int? SerializedHostParentId { get; set; }

        /// <summary>Additional hosts stored by InsertableInst.m_extraHostIds.</summary>
        public List<int>? ExtraHostIds { get; set; }

        /// <summary>Explicit hosts stored by InsertableInst.m_explicitHostIds.</summary>
        public List<int>? ExplicitHostIds { get; set; }

        /// <summary>Owning nested FamilyInstance stored by FamilyInstance.m_superInstanceId.</summary>
        public int? SuperInstanceId { get; set; }

        /// <summary>
        /// FamilyInstance placement transform: position + orientation in world coordinates.
        /// Origin = [x, y, z] in feet. BasisX/Y/Z = rotation axes (unit vectors).
        /// </summary>
        public InstanceTransform? Transform { get; set; }

        /// <summary>
        /// Find the first parameter with the given name.
        /// Matches against Definition.Name (display name).
        /// </summary>
        public Parameter? LookupParameter(string name)
        {
            foreach (var p in Parameters)
                if (p.Definition?.Name == name) return p;
            return null;
        }

        /// <summary>
        /// Find the first parameter by BuiltInParameter enum value.
        /// </summary>
        public Parameter? LookupParameter(BuiltIns.BuiltInParameter bip)
        {
            int bipId = (int)bip;
            foreach (var p in Parameters)
                if (p.Definition?.Id == bipId) return p;
            return null;
        }

        /// <summary>
        /// Find all parameters matching the given name.
        /// </summary>
        public IReadOnlyList<Parameter> GetParameters(string name)
        {
            var result = new List<Parameter>();
            foreach (var p in Parameters)
                if (p.Definition?.Name == name) result.Add(p);
            return result;
        }

        /// <summary>
        /// Get the type element ID (from ELEM_TYPE_PARAM).
        /// Returns -1 if no type reference found.
        /// </summary>
        [JsonIgnore]
        public int FamilyTypeId
        {
            get
            {
                foreach (var p in Parameters)
                {
                    if (p.Definition?.Name is "ELEM_TYPE_PARAM" && p.Value is int id && id > 0)
                        return id;
                }
                return -1;
            }
        }

        /// <summary>
        /// Get parameter value by name, with typed access.
        /// Returns null if parameter not found.
        /// </summary>
        public Parameter? this[string paramName] => LookupParameter(paramName);

        /// <summary>
        /// Get parameter value by BuiltInParameter enum.
        /// Returns null if parameter not found.
        /// </summary>
        public Parameter? this[BuiltIns.BuiltInParameter bip] => LookupParameter(bip);

        /// <inheritdoc />
        public override string ToString()
        {
            var name = Name ?? $"#{Id}";
            var cat = Category ?? CategoryId?.ToString() ?? "?";
            return $"Element({Id}: {name} [{cat}])";
        }
    }
}
