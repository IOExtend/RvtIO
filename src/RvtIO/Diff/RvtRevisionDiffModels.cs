using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RvtIO.Diff;

/// <summary>Assessed relationship between the two compared Revit documents.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RvtRevisionRelation
{
    /// <summary>The documents share persistent project identity and/or persistent element <c>UniqueId</c>s.</summary>
    Related = 0,
    /// <summary>The documents carry conflicting identity signals and zero shared <c>UniqueId</c>s.</summary>
    Unrelated = 1,
    /// <summary>Neither document carried enough identity metadata or elements to confirm relatedness.</summary>
    Indeterminate = 2,
}

/// <summary>How a matched element pair was linked across revisions.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RvtDiffMatchKind
{
    /// <summary>Paired by unique persistent Revit <c>UniqueId</c>.</summary>
    UniqueId = 0,
    /// <summary>Paired by unique <c>(ElementId, CategoryId)</c> inside related revisions when <c>UniqueId</c> was absent.</summary>
    ElementIdFallback = 1,
}

/// <summary>Completeness of data underlying a comparison aspect.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RvtDiffAvailability
{
    /// <summary>Data was decoded on both revisions.</summary>
    Confirmed = 0,
    /// <summary>One or both revisions held partial geometry or incomplete parameter containers.</summary>
    Partial = 1,
    /// <summary>Data on one or both revisions was undecoded or cut by reader limits, so no change or deletion is claimed.</summary>
    Unavailable = 2,
}

/// <summary>Document-level metadata and decode completeness summary for one revision.</summary>
public sealed class RvtRevisionSummary
{
    /// <summary>Display label or file name for the revision.</summary>
    public string Label { get; init; } = "";
    /// <summary>Revit saving release from <c>BasicFileInfo</c>.</summary>
    public string? RevitVersion { get; init; }
    /// <summary>Revit build string from <c>BasicFileInfo</c>.</summary>
    public string? Build { get; init; }
    /// <summary>Decoded persistent document detach GUID, when available.</summary>
    public string? DocumentGuid { get; init; }
    /// <summary>Total standalone elements in the document.</summary>
    public int ElementCount { get; init; }
    /// <summary>Number of standalone elements matching the active category/element filter.</summary>
    public int FilteredElementCount { get; init; }
    /// <summary>Number of standalone elements carrying a persistent <c>UniqueId</c>.</summary>
    public int UniqueIdElementCount { get; init; }
    /// <summary>True when the whole element table decoded with verified CRC and length.</summary>
    public bool ElemTableIsExact { get; init; }
    /// <summary>True when the source carried a wide-schema layout.</summary>
    public bool GeometryUndecoded { get; init; }
    /// <summary>True when wide-schema geometry was not rewritten to R22 layout on read.</summary>
    public bool WideLayoutNotRewritten { get; init; }
    /// <summary>Number of reader limit hits reported while reading the revision.</summary>
    public int LimitHitCount { get; init; }
    /// <summary>Number of failed or unverified partition streams encountered while reading the revision.</summary>
    public int FailedStreamCount { get; init; }
    /// <summary>True when incomplete streams or element tables prevent confirming deletions.</summary>
    public bool IsPartialRead { get; init; }
}

/// <summary>Compact identifying reference to a document element.</summary>
public sealed class RvtDiffElementRef
{
    /// <summary>Canonical match key (<c>UniqueId</c> or <c>id:&lt;ElementId&gt;</c>).</summary>
    public required string IdentityKey { get; init; }
    /// <summary>Persistent Revit <c>UniqueId</c>, when available.</summary>
    public string? UniqueId { get; init; }
    /// <summary>Numeric element ID stored in this revision.</summary>
    public long ElementId { get; init; }
    /// <summary>BuiltInCategory integer identifier.</summary>
    public int? CategoryId { get; init; }
    /// <summary>Category name.</summary>
    public string? Category { get; init; }
    /// <summary>Element name.</summary>
    public string? Name { get; init; }
    /// <summary>Family name, when resolved.</summary>
    public string? FamilyName { get; init; }
    /// <summary>Persistent <c>UniqueId</c> of the element's resolved type, when available.</summary>
    public string? TypeUniqueId { get; init; }
    /// <summary>Numeric type ID in this revision.</summary>
    public long? TypeId { get; init; }
    /// <summary>Resolved type name, when available.</summary>
    public string? TypeName { get; init; }
}

/// <summary>Added, removed, or modified parameter with old/new canonical values.</summary>
public sealed class RvtDiffParameterChange
{
    /// <summary>Canonical matching key (BuiltInParameter, Forge GUID, ForgeId, or name).</summary>
    public required string Key { get; init; }
    /// <summary>Parameter display/definition name.</summary>
    public required string Name { get; init; }
    /// <summary>Parameter storage type (<c>Double</c>, <c>Integer</c>, <c>String</c>, <c>ElementId</c>).</summary>
    public required string StorageType { get; init; }
    /// <summary>Display unit symbol, when known.</summary>
    public string? UnitSymbol { get; init; }
    /// <summary>Canonical previous value (internal units for numeric values, resolved target identity for ElementIds).</summary>
    public object? OldValue { get; init; }
    /// <summary>Canonical new value (internal units for numeric values, resolved target identity for ElementIds).</summary>
    public object? NewValue { get; init; }
    /// <summary>Formatted previous value when <see cref="RvtDiffOptions.IncludeDisplayFormattingDifferences"/> is enabled.</summary>
    public string? OldDisplayValue { get; init; }
    /// <summary>Formatted new value when <see cref="RvtDiffOptions.IncludeDisplayFormattingDifferences"/> is enabled.</summary>
    public string? NewDisplayValue { get; init; }
    /// <summary>Raw numeric <c>ElementId</c> in the left revision for ElementId parameters.</summary>
    public int? OldRawElementId { get; init; }
    /// <summary>Raw numeric <c>ElementId</c> in the right revision for ElementId parameters.</summary>
    public int? NewRawElementId { get; init; }
    /// <summary>Kind of parameter change (<c>added</c>, <c>removed</c>, or <c>modified</c>).</summary>
    public string ChangeKind { get; init; } = "modified";
}

/// <summary>Reassigned type or family symbol on an instance.</summary>
public sealed class RvtDiffTypeChange
{
    /// <summary>Numeric type ID in the left revision.</summary>
    public long OldTypeId { get; init; }
    /// <summary>Persistent <c>UniqueId</c> of the previous type.</summary>
    public string? OldTypeUniqueId { get; init; }
    /// <summary>Name of the previous type.</summary>
    public string? OldTypeName { get; init; }
    /// <summary>Family name of the previous type.</summary>
    public string? OldFamilyName { get; init; }
    /// <summary>Numeric type ID in the right revision.</summary>
    public long NewTypeId { get; init; }
    /// <summary>Persistent <c>UniqueId</c> of the new type.</summary>
    public string? NewTypeUniqueId { get; init; }
    /// <summary>Name of the new type.</summary>
    public string? NewTypeName { get; init; }
    /// <summary>Family name of the new type.</summary>
    public string? NewFamilyName { get; init; }
}

/// <summary>3D coordinate or vector in Revit internal feet.</summary>
/// <param name="X">X coordinate in internal feet.</param>
/// <param name="Y">Y coordinate in internal feet.</param>
/// <param name="Z">Z coordinate in internal feet.</param>
public sealed record RvtDiffVector3(double X, double Y, double Z);

/// <summary>Comparable snapshot of an element's world-space placement in internal feet.</summary>
public sealed class RvtDiffPlacementSnapshot
{
    /// <summary>Placement representation kind (<c>LocationLine</c>, <c>LocationArc</c>, <c>Transform</c>, <c>TextPosition</c>, <c>ViewportOffset</c>, <c>GeometryTransform</c>).</summary>
    public required string Kind { get; init; }
    /// <summary>Origin or start point in world coordinates (feet).</summary>
    public required RvtDiffVector3 Origin { get; init; }
    /// <summary>Curve end point for location curves (feet).</summary>
    public RvtDiffVector3? EndPoint { get; init; }
    /// <summary>Curve length in feet for location curves.</summary>
    public double? LengthFeet { get; init; }
    /// <summary>Arc center in world feet for arc location curves.</summary>
    public RvtDiffVector3? ArcCenter { get; init; }
    /// <summary>Arc radius in feet for arc location curves.</summary>
    public double? ArcRadiusFeet { get; init; }
    /// <summary>Transform X basis vector.</summary>
    public RvtDiffVector3? BasisX { get; init; }
    /// <summary>Transform Y basis vector.</summary>
    public RvtDiffVector3? BasisY { get; init; }
    /// <summary>Transform Z basis vector.</summary>
    public RvtDiffVector3? BasisZ { get; init; }
    /// <summary>Orientation determinant (negative indicates mirrored transform).</summary>
    public double? Determinant { get; init; }
}

/// <summary>World-space movement, rotation, or mirroring change of an element.</summary>
public sealed class RvtDiffPlacementChange
{
    /// <summary>Previous placement snapshot.</summary>
    public required RvtDiffPlacementSnapshot Old { get; init; }
    /// <summary>New placement snapshot.</summary>
    public required RvtDiffPlacementSnapshot New { get; init; }
    /// <summary>Origin translation vector from old to new in feet.</summary>
    public required RvtDiffVector3 TranslationFeet { get; init; }
    /// <summary>Euclidean origin translation distance in feet.</summary>
    public double TranslationDistanceFeet { get; init; }
    /// <summary>Estimated orientation rotation angle change in degrees, when basis or curve direction changed.</summary>
    public double? RotationDeltaDegrees { get; init; }
    /// <summary>True when mirror handedness determinant sign changed.</summary>
    public bool MirroredChanged { get; init; }
}

/// <summary>Comparable structural and dimensional geometry snapshot in internal feet.</summary>
public sealed class RvtDiffGeometrySnapshot
{
    /// <summary>World bounding box <c>[minX, minY, minZ, maxX, maxY, maxZ]</c> in feet.</summary>
    public double[]? BoundingBoxFeet { get; init; }
    /// <summary>True when the geometry carries a local-to-world transform.</summary>
    public bool HasTransform { get; init; }
    /// <summary>Decoded face count.</summary>
    public int FaceCount { get; init; }
    /// <summary>Decoded edge count.</summary>
    public int EdgeCount { get; init; }
    /// <summary>Extruded solid count.</summary>
    public int SolidCount { get; init; }
    /// <summary>Topological BRep solid count.</summary>
    public int BrepSolidCount { get; init; }
    /// <summary>Opening void count across solids.</summary>
    public int OpeningCount { get; init; }
    /// <summary>Nested symbol instance count.</summary>
    public int InstanceCount { get; init; }
    /// <summary>Closed sketch loop count.</summary>
    public int SketchLoopCount { get; init; }
    /// <summary>Compound layer count (host types).</summary>
    public int LayerCount { get; init; }
    /// <summary>Total face surface area in square feet.</summary>
    public double TotalFaceAreaSquareFeet { get; init; }
    /// <summary>Sorted persistent identities of referenced family symbols.</summary>
    public IReadOnlyList<string> SymbolUniqueIds { get; init; } = [];
}

/// <summary>Decoded geometry, solid, sketch, or layer modification with old/new metrics.</summary>
public sealed class RvtDiffGeometryChange
{
    /// <summary>Completeness of the underlying geometry comparison.</summary>
    public required RvtDiffAvailability Availability { get; init; }
    /// <summary>Reason code when availability is <see cref="RvtDiffAvailability.Partial"/> or <see cref="RvtDiffAvailability.Unavailable"/>.</summary>
    public string? Reason { get; init; }
    /// <summary>List of changed geometry aspects (<c>bounding-box</c>, <c>face-count</c>, <c>face-area</c>, <c>solids</c>, <c>layers</c>, etc.).</summary>
    public required IReadOnlyList<string> ChangedAspects { get; init; }
    /// <summary>Left geometry snapshot.</summary>
    public RvtDiffGeometrySnapshot? Old { get; init; }
    /// <summary>Right geometry snapshot.</summary>
    public RvtDiffGeometrySnapshot? New { get; init; }
    /// <summary>Maximum bounding-box coordinate delta in feet, when bounding box changed.</summary>
    public double? MaxBoundingBoxDeltaFeet { get; init; }
}

/// <summary>Changed structural or semantic relationship on an element (targets resolved by persistent identity).</summary>
public sealed class RvtDiffRelationshipChange
{
    /// <summary>Relationship role (<c>host</c>, <c>level</c>, <c>group</c>, <c>room</c>, <c>owner-view</c>, <c>viewport-view</c>, <c>super-instance</c>, <c>cutters</c>, <c>connectors</c>).</summary>
    public required string Relation { get; init; }
    /// <summary>Previous target reference for single-target relations.</summary>
    public RvtDiffElementRef? OldTarget { get; init; }
    /// <summary>New target reference for single-target relations.</summary>
    public RvtDiffElementRef? NewTarget { get; init; }
    /// <summary>Previous target references for set relations.</summary>
    public IReadOnlyList<RvtDiffElementRef>? OldTargets { get; init; }
    /// <summary>New target references for set relations.</summary>
    public IReadOnlyList<RvtDiffElementRef>? NewTargets { get; init; }
}

/// <summary>Full breakdown of confirmed modifications on a matched element pair.</summary>
public sealed class RvtDiffModifiedElement
{
    /// <summary>Canonical identity key linking the pair.</summary>
    public required string IdentityKey { get; init; }
    /// <summary>Mechanism used to pair the element across revisions.</summary>
    public required RvtDiffMatchKind MatchKind { get; init; }
    /// <summary>Element summary in the right revision.</summary>
    public required RvtDiffElementRef Element { get; init; }
    /// <summary>Type assignment change, if the element changed types.</summary>
    public RvtDiffTypeChange? TypeChange { get; init; }
    /// <summary>Placement/transform change, if moved/rotated/mirrored.</summary>
    public RvtDiffPlacementChange? PlacementChange { get; init; }
    /// <summary>Geometry, layer, or sketch change, or unavailable geometry status.</summary>
    public RvtDiffGeometryChange? GeometryChange { get; init; }
    /// <summary>Modified structural/containment/connectivity relationships.</summary>
    public IReadOnlyList<RvtDiffRelationshipChange> RelationshipChanges { get; init; } = [];
    /// <summary>Added, removed, or modified parameters.</summary>
    public IReadOnlyList<RvtDiffParameterChange> ParameterChanges { get; init; } = [];
    /// <summary>Identity keys of changed type elements that affect this instance.</summary>
    public IReadOnlyList<string> AffectedByTypeIdentityKeys { get; init; } = [];
    /// <summary>Aspects marked unavailable due to partial decoding.</summary>
    public IReadOnlyList<string> UnavailableAspects { get; init; } = [];
}

/// <summary>Type modification or type reassignment linked to every affected instance in the compared scope.</summary>
public sealed class RvtDiffTypeImpact
{
    /// <summary>Type or family symbol element reference.</summary>
    public required RvtDiffElementRef TypeElement { get; init; }
    /// <summary>Kinds of impact (<c>type-modified</c>, <c>instance-reassigned-from</c>, <c>instance-reassigned-to</c>).</summary>
    public required IReadOnlyList<string> ChangeKinds { get; init; }
    /// <summary>Changed parameters on the type element itself.</summary>
    public IReadOnlyList<RvtDiffParameterChange> ParameterChanges { get; init; } = [];
    /// <summary>True when shared geometry on the type/symbol changed.</summary>
    public bool GeometryChanged { get; init; }
    /// <summary>True when compound structure layers on the type changed.</summary>
    public bool LayerChanged { get; init; }
    /// <summary>Instances affected by this type modification or reassignment.</summary>
    public required IReadOnlyList<RvtDiffElementRef> AffectedInstances { get; init; }
}

/// <summary>Elements sharing duplicate persistent identity within one or both revisions, set aside without guessing.</summary>
public sealed class RvtDiffAmbiguousMatch
{
    /// <summary>Duplicate persistent key.</summary>
    public required string IdentityKey { get; init; }
    /// <summary>Ambiguity reason code.</summary>
    public required string Reason { get; init; }
    /// <summary>Left revision elements sharing <see cref="IdentityKey"/>.</summary>
    public required IReadOnlyList<RvtDiffElementRef> LeftElements { get; init; }
    /// <summary>Right revision elements sharing <see cref="IdentityKey"/>.</summary>
    public required IReadOnlyList<RvtDiffElementRef> RightElements { get; init; }
}

/// <summary>Aspect or element whose comparison state could not be confirmed due to missing identity or partial decoding.</summary>
public sealed class RvtDiffUnavailableEntry
{
    /// <summary>Revision side missing complete data (<c>left</c>, <c>right</c>, or <c>both</c>).</summary>
    public required string Side { get; init; }
    /// <summary>Element involved, when attributable to a specific element.</summary>
    public RvtDiffElementRef? Element { get; init; }
    /// <summary>Comparison aspect (<c>identity</c>, <c>existence</c>, <c>geometry</c>, <c>parameters</c>).</summary>
    public required string Aspect { get; init; }
    /// <summary>Stable reason code explaining why confirmation was withheld.</summary>
    public required string Reason { get; init; }
    /// <summary>Human-readable diagnostic detail.</summary>
    public string? Detail { get; init; }
}

/// <summary>Change in external file links recorded in <c>TransmissionData</c>.</summary>
public sealed class RvtDiffExternalLinkChange
{
    /// <summary>Link change kind (<c>added</c>, <c>removed</c>, or <c>modified</c>).</summary>
    public required string Kind { get; init; }
    /// <summary>External reference element ID.</summary>
    public int ElementId { get; init; }
    /// <summary>Reference type (e.g. RevitLink).</summary>
    public string? ReferenceType { get; init; }
    /// <summary>Previous path.</summary>
    public string? OldPath { get; init; }
    /// <summary>New path.</summary>
    public string? NewPath { get; init; }
    /// <summary>Previous load state.</summary>
    public string? OldLoadState { get; init; }
    /// <summary>New load state.</summary>
    public string? NewLoadState { get; init; }
}

/// <summary>Tolerances active during a revision comparison.</summary>
/// <param name="LinearToleranceFeet">Linear coordinate and dimension tolerance in feet.</param>
/// <param name="AngleToleranceRadians">Angular tolerance in radians.</param>
/// <param name="AreaToleranceSquareFeet">Face area tolerance in square feet.</param>
/// <param name="NumericParameterTolerance">Numeric parameter value tolerance in internal units.</param>
public sealed record RvtDiffToleranceSummary(
    double LinearToleranceFeet,
    double AngleToleranceRadians,
    double AreaToleranceSquareFeet,
    double NumericParameterTolerance);

/// <summary>Deterministic machine- and human-readable comparison report across two Revit revisions.</summary>
public sealed class RvtRevisionDiffResult
{
    private static readonly JsonSerializerOptions JsonIndented = CreateJsonOptions(true);
    private static readonly JsonSerializerOptions JsonCompact = CreateJsonOptions(false);

    /// <summary>Report schema identifier.</summary>
    public string Schema { get; init; } = "rvtio-diff/1";
    /// <summary>Left revision summary.</summary>
    public required RvtRevisionSummary Left { get; init; }
    /// <summary>Right revision summary.</summary>
    public required RvtRevisionSummary Right { get; init; }
    /// <summary>Assessed relatedness between the two documents.</summary>
    public required RvtRevisionRelation Relation { get; init; }
    /// <summary>Reason code explaining <see cref="Relation"/>.</summary>
    public required string RelationReason { get; init; }
    /// <summary>True when <c>Left.RevitVersion</c> differs from <c>Right.RevitVersion</c>.</summary>
    public bool VersionChanged { get; init; }
    /// <summary>Tolerances applied during comparison.</summary>
    public required RvtDiffToleranceSummary Tolerances { get; init; }
    /// <summary>Total paired elements across both revisions.</summary>
    public int TotalMatchedElements { get; init; }
    /// <summary>Paired elements without any detected changes or unavailable aspects.</summary>
    public int UnchangedCount { get; init; }
    /// <summary>Confirmed elements present only in the right revision.</summary>
    public required IReadOnlyList<RvtDiffElementRef> Added { get; init; }
    /// <summary>Confirmed elements present only in the left revision.</summary>
    public required IReadOnlyList<RvtDiffElementRef> Removed { get; init; }
    /// <summary>Paired elements with detected parameter, type, placement, geometry, relationship, or availability entries.</summary>
    public required IReadOnlyList<RvtDiffModifiedElement> Modified { get; init; }
    /// <summary>Changed or reassigned types mapped to affected instances.</summary>
    public required IReadOnlyList<RvtDiffTypeImpact> TypeImpacts { get; init; }
    /// <summary>Duplicate persistent identities set aside without guessing.</summary>
    public required IReadOnlyList<RvtDiffAmbiguousMatch> AmbiguousMatches { get; init; }
    /// <summary>Missing/undecoded/identity-less entries reported as unavailable rather than deletions.</summary>
    public required IReadOnlyList<RvtDiffUnavailableEntry> Unavailable { get; init; }
    /// <summary>Changes in <c>TransmissionData</c> external file links.</summary>
    public required IReadOnlyList<RvtDiffExternalLinkChange> ExternalLinkChanges { get; init; }
    /// <summary>Non-fatal warnings regarding relatedness, partial decoding, or undecoded geometry.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>True when any confirmed additions, removals, modifications, type impacts, or external link changes exist.</summary>
    public bool HasSemanticChanges =>
        Added.Count > 0
        || Removed.Count > 0
        || Modified.Count > 0
        || TypeImpacts.Count > 0
        || ExternalLinkChanges.Count > 0;

    /// <summary>Serializes the diff report to deterministic JSON.</summary>
    public string ToJson(bool indented = true) =>
        JsonSerializer.Serialize(this, indented ? JsonIndented : JsonCompact);

    private static JsonSerializerOptions CreateJsonOptions(bool indented) => new()
    {
        WriteIndented = indented,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
