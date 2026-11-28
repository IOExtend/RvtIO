using System.Globalization;
using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Diff;

/// <summary>
/// Compares two revisions of a Revit project and produces a deterministic report of additions, removals,
/// parameter/type/placement/geometry/relationship modifications, ambiguous matches, and partial-decode limits.
/// </summary>
public static partial class RvtRevisionDiffer
{
    /// <summary>Opens <paramref name="leftPath"/> and <paramref name="rightPath"/> and compares them.</summary>
    public static RvtRevisionDiffResult Compare(string leftPath, string rightPath, RvtDiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(leftPath);
        ArgumentNullException.ThrowIfNull(rightPath);
        options ??= new RvtDiffOptions();
        ValidateTolerances(options);

        var leftDiag = options.LeftDiagnostics ?? new RvtReadDiagnostics();
        var rightDiag = options.RightDiagnostics ?? new RvtReadDiagnostics();

        var leftReaderOpts = CloneReaderOptions(options.LeftReaderOptions, leftDiag);
        var rightReaderOpts = CloneReaderOptions(options.RightReaderOptions, rightDiag);

        var leftDoc = RvtDocument.Open(leftPath, leftReaderOpts);
        var rightDoc = RvtDocument.Open(rightPath, rightReaderOpts);

        var effectiveOptions = new RvtDiffOptions
        {
            Categories = options.Categories,
            Elements = options.Elements,
            LinearToleranceFeet = options.LinearToleranceFeet,
            AngleToleranceRadians = options.AngleToleranceRadians,
            AreaToleranceSquareFeet = options.AreaToleranceSquareFeet,
            NumericParameterTolerance = options.NumericParameterTolerance,
            IncludeParameters = options.IncludeParameters,
            IncludeTypes = options.IncludeTypes,
            IncludePlacement = options.IncludePlacement,
            IncludeGeometry = options.IncludeGeometry,
            IncludeRelationships = options.IncludeRelationships,
            IncludeTransmissionLinks = options.IncludeTransmissionLinks,
            IncludeDisplayFormattingDifferences = options.IncludeDisplayFormattingDifferences,
            IncludeUncertainMatches = options.IncludeUncertainMatches,
            AllowElementIdFallbackWhenRelated = options.AllowElementIdFallbackWhenRelated,
            RedactPersonalData = options.RedactPersonalData,
            LeftDiagnostics = leftDiag,
            RightDiagnostics = rightDiag,
            LeftLabel = options.LeftLabel ?? Path.GetFileName(leftPath),
            RightLabel = options.RightLabel ?? Path.GetFileName(rightPath),
        };

        return Compare(leftDoc, rightDoc, effectiveOptions);
    }

    /// <summary>Compares two already-opened <see cref="RvtDocument"/> revisions.</summary>
    public static RvtRevisionDiffResult Compare(RvtDocument left, RvtDocument right, RvtDiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        options ??= new RvtDiffOptions();
        ValidateTolerances(options);

        var warnings = new List<string>();
        bool leftPartial = IsPartialDocument(left.RawData, options.LeftDiagnostics, out int leftLimitHits, out int leftFailedStreams);
        bool rightPartial = IsPartialDocument(right.RawData, options.RightDiagnostics, out int rightLimitHits, out int rightFailedStreams);

        var leftLookup = BuildElementLookup(left);
        var rightLookup = BuildElementLookup(right);

        var leftStandalone = left.Elements.Where(e => !e.IsBlockOnly).ToList();
        var rightStandalone = right.Elements.Where(e => !e.IsBlockOnly).ToList();

        var leftUniqueGroups = GroupByUniqueId(leftStandalone);
        var rightUniqueGroups = GroupByUniqueId(rightStandalone);

        var (relation, relationReason) = AssessRelation(left, right, leftUniqueGroups, rightUniqueGroups);
        if (relation == RvtRevisionRelation.Unrelated)
            warnings.Add($"Documents appear unrelated ({relationReason}); numeric IDs are not matched across unrelated documents.");

        bool versionChanged = !string.IsNullOrEmpty(left.Metadata.RevitVersion)
            && !string.IsNullOrEmpty(right.Metadata.RevitVersion)
            && !string.Equals(left.Metadata.RevitVersion, right.Metadata.RevitVersion, StringComparison.Ordinal);

        if (leftPartial)
            warnings.Add("Left document was only partially decoded; missing items on the right are marked unavailable rather than confirmed additions.");
        if (rightPartial)
            warnings.Add("Right document was only partially decoded; missing items on the left are marked unavailable rather than confirmed deletions.");
        if (left.RawData.WideLayoutNotRewritten || left.RawData.GeometryUndecoded)
            warnings.Add("Left document has undecoded wide-schema geometry; geometry diffs involving missing bodies are marked unavailable.");
        if (right.RawData.WideLayoutNotRewritten || right.RawData.GeometryUndecoded)
            warnings.Add("Right document has undecoded wide-schema geometry; geometry diffs involving missing bodies are marked unavailable.");

        var filter = new ElementFilter(options.Categories, options.Elements);
        var ambiguous = new List<RvtDiffAmbiguousMatch>();
        var unavailable = new List<RvtDiffUnavailableEntry>();
        var added = new List<RvtDiffElementRef>();
        var removed = new List<RvtDiffElementRef>();
        var matchedPairs = new List<(string Key, RvtDiffMatchKind Kind, Element Left, Element Right)>();

        // 1. Resolve duplicate UniqueIds as ambiguous (never matched arbitrarily).
        var duplicateUniqueIds = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (uid, list) in leftUniqueGroups)
            if (list.Count > 1) duplicateUniqueIds.Add(uid);
        foreach (var (uid, list) in rightUniqueGroups)
            if (list.Count > 1) duplicateUniqueIds.Add(uid);

        foreach (string dupUid in duplicateUniqueIds)
        {
            leftUniqueGroups.TryGetValue(dupUid, out var lList);
            rightUniqueGroups.TryGetValue(dupUid, out var rList);
            var lRefs = (lList ?? []).Where(e => filter.Matches(e, left)).Select(e => CreateRef(e, left, leftLookup)).ToList();
            var rRefs = (rList ?? []).Where(e => filter.Matches(e, right)).Select(e => CreateRef(e, right, rightLookup)).ToList();
            if (lRefs.Count == 0 && rRefs.Count == 0) continue;

            ambiguous.Add(new RvtDiffAmbiguousMatch
            {
                IdentityKey = dupUid,
                Reason = "duplicate-unique-id",
                LeftElements = SortRefs(lRefs),
                RightElements = SortRefs(rRefs),
            });
        }

        // 2. Pair unique UniqueIds across documents; classify unmatched UniqueIds against partial-decode state.
        var allUniqueIds = new SortedSet<string>(leftUniqueGroups.Keys, StringComparer.OrdinalIgnoreCase);
        allUniqueIds.UnionWith(rightUniqueGroups.Keys);

        foreach (string uid in allUniqueIds)
        {
            if (duplicateUniqueIds.Contains(uid)) continue;

            leftUniqueGroups.TryGetValue(uid, out var lList);
            rightUniqueGroups.TryGetValue(uid, out var rList);
            Element? le = lList is { Count: 1 } ? lList[0] : null;
            Element? re = rList is { Count: 1 } ? rList[0] : null;

            bool lPass = le != null && filter.Matches(le, left);
            bool rPass = re != null && filter.Matches(re, right);
            if (!lPass && !rPass) continue;

            if (le != null && re != null)
            {
                matchedPairs.Add((uid, RvtDiffMatchKind.UniqueId, le, re));
            }
            else if (le != null)
            {
                if (rightPartial)
                {
                    if (options.IncludeUncertainMatches)
                    {
                        unavailable.Add(new RvtDiffUnavailableEntry
                        {
                            Side = "right",
                            Element = CreateRef(le, left, leftLookup),
                            Aspect = "existence",
                            Reason = "partial-decode-prevents-deletion-confirmation",
                            Detail = "Element is absent from right revision, but the right revision element table or partition streams did not decode completely.",
                        });
                    }
                }
                else
                {
                    removed.Add(CreateRef(le, left, leftLookup));
                }
            }
            else if (re != null)
            {
                if (leftPartial)
                {
                    if (options.IncludeUncertainMatches)
                    {
                        unavailable.Add(new RvtDiffUnavailableEntry
                        {
                            Side = "left",
                            Element = CreateRef(re, right, rightLookup),
                            Aspect = "existence",
                            Reason = "partial-decode-prevents-addition-confirmation",
                            Detail = "Element is absent from left revision, but the left revision element table or partition streams did not decode completely.",
                        });
                    }
                }
                else
                {
                    added.Add(CreateRef(re, right, rightLookup));
                }
            }
        }

        // 3. Elements lacking persistent UniqueId: report under Unavailable unless opted into ElementId+CategoryId fallback for related docs.
        var leftNoUid = leftStandalone.Where(e => string.IsNullOrWhiteSpace(e.UniqueId)).ToList();
        var rightNoUid = rightStandalone.Where(e => string.IsNullOrWhiteSpace(e.UniqueId)).ToList();

        if (options.AllowElementIdFallbackWhenRelated && relation == RvtRevisionRelation.Related)
        {
            var leftByIdCat = GroupByElementIdAndCategory(leftNoUid);
            var rightByIdCat = GroupByElementIdAndCategory(rightNoUid);
            var allKeys = new SortedSet<(long Id, int? CatId)>(leftByIdCat.Keys);
            allKeys.UnionWith(rightByIdCat.Keys);

            foreach (var key in allKeys)
            {
                leftByIdCat.TryGetValue(key, out var lList);
                rightByIdCat.TryGetValue(key, out var rList);
                if (lList is { Count: 1 } && rList is { Count: 1 })
                {
                    Element le = lList[0], re = rList[0];
                    if (!filter.Matches(le, left) && !filter.Matches(re, right)) continue;
                    matchedPairs.Add(($"eid:{key.Id}", RvtDiffMatchKind.ElementIdFallback, le, re));
                }
                else
                {
                    RecordMissingPersistentIdEntries(lList, left, leftLookup, "left", filter, options, unavailable);
                    RecordMissingPersistentIdEntries(rList, right, rightLookup, "right", filter, options, unavailable);
                }
            }
        }
        else if (options.IncludeUncertainMatches)
        {
            RecordMissingPersistentIdEntries(leftNoUid, left, leftLookup, "left", filter, options, unavailable);
            RecordMissingPersistentIdEntries(rightNoUid, right, rightLookup, "right", filter, options, unavailable);
        }

        // 4. Also inspect Type elements even if outside direct element/category filter when needed for impact links,
        // and build Type -> AffectedInstances maps across the full matched set.
        var modified = new List<RvtDiffModifiedElement>();
        var rawModifiedByKey = new Dictionary<string, RvtDiffModifiedElement>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, kind, le, re) in matchedPairs)
        {
            var mod = CompareMatchedElement(key, kind, le, re, left, right, leftLookup, rightLookup, options);
            if (mod != null)
            {
                modified.Add(mod);
                rawModifiedByKey[key] = mod;
            }
        }

        // Compute type-change impact: both reassigned instances and modified type definitions (layers/params/symbol geometry).
        var typeImpacts = options.IncludeTypes
            ? BuildTypeImpacts(matchedPairs, modified, left, right, leftLookup, rightLookup, leftUniqueGroups, rightUniqueGroups, filter, options)
            : [];

        // Link modified instances back to changed type definitions.
        if (typeImpacts.Count > 0)
        {
            var typeImpactByInstance = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var impact in typeImpacts)
            {
                foreach (var inst in impact.AffectedInstances)
                {
                    if (!typeImpactByInstance.TryGetValue(inst.IdentityKey, out var list))
                        typeImpactByInstance[inst.IdentityKey] = list = [];
                    list.Add(impact.TypeElement.IdentityKey);
                }
            }

            for (int i = 0; i < modified.Count; i++)
            {
                var item = modified[i];
                if (!typeImpactByInstance.TryGetValue(item.IdentityKey, out var typeKeys)) continue;
                var distinctKeys = typeKeys.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                modified[i] = new RvtDiffModifiedElement
                {
                    IdentityKey = item.IdentityKey,
                    MatchKind = item.MatchKind,
                    Element = item.Element,
                    TypeChange = item.TypeChange,
                    PlacementChange = item.PlacementChange,
                    GeometryChange = item.GeometryChange,
                    RelationshipChanges = item.RelationshipChanges,
                    ParameterChanges = item.ParameterChanges,
                    AffectedByTypeIdentityKeys = distinctKeys,
                    UnavailableAspects = item.UnavailableAspects,
                };
            }
        }

        var externalLinkChanges = options.IncludeTransmissionLinks
            ? CompareTransmissionLinks(left.TransmissionData, right.TransmissionData)
            : [];

        var sortedAdded = SortRefs(added);
        var sortedRemoved = SortRefs(removed);
        var sortedModified = modified.OrderBy(m => m.IdentityKey, StringComparer.OrdinalIgnoreCase).ToList();
        var sortedTypeImpacts = typeImpacts.OrderBy(t => t.TypeElement.IdentityKey, StringComparer.OrdinalIgnoreCase).ToList();
        var sortedAmbiguous = ambiguous.OrderBy(a => a.IdentityKey, StringComparer.OrdinalIgnoreCase).ToList();
        var sortedUnavailable = unavailable
            .OrderBy(u => u.Side, StringComparer.Ordinal)
            .ThenBy(u => u.Aspect, StringComparer.Ordinal)
            .ThenBy(u => u.Element?.IdentityKey ?? "", StringComparer.OrdinalIgnoreCase)
            .ToList();

        int filteredLeftCount = leftStandalone.Count(e => filter.Matches(e, left));
        int filteredRightCount = rightStandalone.Count(e => filter.Matches(e, right));

        var result = new RvtRevisionDiffResult
        {
            Left = new RvtRevisionSummary
            {
                Label = options.LeftLabel ?? (Path.GetFileName(left.SourcePath ?? "") is { Length: > 0 } ln ? ln : "left"),
                RevitVersion = left.Metadata.RevitVersion,
                Build = left.Metadata.Build,
                DocumentGuid = left.RawData.SerializedDocumentGuid?.ToString("D"),
                ElementCount = leftStandalone.Count,
                FilteredElementCount = filteredLeftCount,
                UniqueIdElementCount = leftStandalone.Count(e => !string.IsNullOrWhiteSpace(e.UniqueId)),
                ElemTableIsExact = left.RawData.ElemTableIsExact,
                GeometryUndecoded = left.RawData.GeometryUndecoded,
                WideLayoutNotRewritten = left.RawData.WideLayoutNotRewritten,
                LimitHitCount = leftLimitHits,
                FailedStreamCount = leftFailedStreams,
                IsPartialRead = leftPartial,
            },
            Right = new RvtRevisionSummary
            {
                Label = options.RightLabel ?? (Path.GetFileName(right.SourcePath ?? "") is { Length: > 0 } rn ? rn : "right"),
                RevitVersion = right.Metadata.RevitVersion,
                Build = right.Metadata.Build,
                DocumentGuid = right.RawData.SerializedDocumentGuid?.ToString("D"),
                ElementCount = rightStandalone.Count,
                FilteredElementCount = filteredRightCount,
                UniqueIdElementCount = rightStandalone.Count(e => !string.IsNullOrWhiteSpace(e.UniqueId)),
                ElemTableIsExact = right.RawData.ElemTableIsExact,
                GeometryUndecoded = right.RawData.GeometryUndecoded,
                WideLayoutNotRewritten = right.RawData.WideLayoutNotRewritten,
                LimitHitCount = rightLimitHits,
                FailedStreamCount = rightFailedStreams,
                IsPartialRead = rightPartial,
            },
            Relation = relation,
            RelationReason = relationReason,
            VersionChanged = versionChanged,
            Tolerances = new RvtDiffToleranceSummary(
                options.LinearToleranceFeet,
                options.AngleToleranceRadians,
                options.AreaToleranceSquareFeet,
                options.NumericParameterTolerance),
            TotalMatchedElements = matchedPairs.Count,
            UnchangedCount = matchedPairs.Count - sortedModified.Count,
            Added = sortedAdded,
            Removed = sortedRemoved,
            Modified = sortedModified,
            TypeImpacts = sortedTypeImpacts,
            AmbiguousMatches = sortedAmbiguous,
            Unavailable = sortedUnavailable,
            ExternalLinkChanges = externalLinkChanges,
            Warnings = warnings,
        };

        return options.RedactPersonalData ? RedactDiffResult(result, left.SourcePath, right.SourcePath) : result;
    }

    private static void ValidateTolerances(RvtDiffOptions options)
    {
        if (!ValidTolerance(options.LinearToleranceFeet) || !ValidTolerance(options.AngleToleranceRadians)
            || !ValidTolerance(options.AreaToleranceSquareFeet) || !ValidTolerance(options.NumericParameterTolerance))
            throw new ArgumentOutOfRangeException(nameof(options), "Diff tolerances must be finite and non-negative.");

        static bool ValidTolerance(double value) => double.IsFinite(value) && value >= 0;
    }

    private static RvtDiffModifiedElement? CompareMatchedElement(
        string identityKey,
        RvtDiffMatchKind matchKind,
        Element left,
        Element right,
        RvtDocument leftDoc,
        RvtDocument rightDoc,
        Dictionary<long, Element> leftLookup,
        Dictionary<long, Element> rightLookup,
        RvtDiffOptions options)
    {
        var unavailableAspects = new List<string>();

        RvtDiffTypeChange? typeChange = options.IncludeTypes
            ? CompareType(left, right, leftDoc, rightDoc, leftLookup, rightLookup)
            : null;

        RvtDiffPlacementChange? placementChange = options.IncludePlacement
            ? ComparePlacement(left, right, options)
            : null;

        RvtDiffGeometryChange? geometryChange = null;
        if (options.IncludeGeometry)
        {
            geometryChange = CompareGeometry(left, right, leftDoc, rightDoc, leftLookup, rightLookup, placementChange, options,
                out bool geometryUnavailable);
            if (geometryUnavailable) unavailableAspects.Add("geometry");
        }

        var relationshipChanges = options.IncludeRelationships
            ? CompareRelationships(left, right, leftLookup, rightLookup)
            : [];

        List<RvtDiffParameterChange> paramChanges = [];
        if (options.IncludeParameters)
        {
            paramChanges = CompareParameters(left, right, leftLookup, rightLookup, options, out bool paramPartial);
            if (paramPartial) unavailableAspects.Add("parameters");
        }

        if (typeChange == null
            && placementChange == null
            && geometryChange == null
            && relationshipChanges.Count == 0
            && paramChanges.Count == 0
            && unavailableAspects.Count == 0)
        {
            return null;
        }

        // Only emit geometryChange if there was an actual confirmed change or partial availability note when desired.
        return new RvtDiffModifiedElement
        {
            IdentityKey = identityKey,
            MatchKind = matchKind,
            Element = CreateRef(right, rightDoc, rightLookup),
            TypeChange = typeChange,
            PlacementChange = placementChange,
            GeometryChange = geometryChange,
            RelationshipChanges = relationshipChanges,
            ParameterChanges = paramChanges,
            UnavailableAspects = unavailableAspects,
        };
    }

    private static RvtDiffTypeChange? CompareType(
        Element left,
        Element right,
        RvtDocument leftDoc,
        RvtDocument rightDoc,
        Dictionary<long, Element> leftLookup,
        Dictionary<long, Element> rightLookup)
    {
        Element? lType = ResolveTypeElement(left, leftDoc, leftLookup);
        Element? rType = ResolveTypeElement(right, rightDoc, rightLookup);

        if (lType == null && rType == null)
        {
            // No resolved type on either side; fall back only if one side had a valid positive type reference and the other didn't.
            bool hasL = left.TypeElementId > 0 || left.FamilyTypeId > 0;
            bool hasR = right.TypeElementId > 0 || right.FamilyTypeId > 0;
            if (!hasL && !hasR) return null;
        }

        string lKey = PersistentKeyOf(lType);
        string rKey = PersistentKeyOf(rType);
        if (!string.IsNullOrEmpty(lKey) && !string.IsNullOrEmpty(rKey))
        {
            if (string.Equals(lKey, rKey, StringComparison.OrdinalIgnoreCase))
                return null;
        }
        else if (lType == null && rType == null)
        {
            return null;
        }
        else if (string.Equals(lType?.Name, rType?.Name, StringComparison.Ordinal)
                 && string.Equals(lType?.FamilyName, rType?.FamilyName, StringComparison.Ordinal)
                 && lType?.CategoryId == rType?.CategoryId
                 && (lType?.ElementId ?? -1) == (rType?.ElementId ?? -1))
        {
            return null;
        }

        return new RvtDiffTypeChange
        {
            OldTypeId = lType?.ElementId ?? (left.FamilyTypeId > 0 ? left.FamilyTypeId : left.TypeElementId),
            OldTypeUniqueId = lType?.UniqueId,
            OldTypeName = lType?.Name,
            OldFamilyName = lType?.FamilyName,
            NewTypeId = rType?.ElementId ?? (right.FamilyTypeId > 0 ? right.FamilyTypeId : right.TypeElementId),
            NewTypeUniqueId = rType?.UniqueId,
            NewTypeName = rType?.Name,
            NewFamilyName = rType?.FamilyName,
        };
    }

    private static RvtDiffPlacementChange? ComparePlacement(Element left, Element right, RvtDiffOptions options)
    {
        var lSnap = CapturePlacement(left);
        var rSnap = CapturePlacement(right);
        if (lSnap == null && rSnap == null) return null;
        if (lSnap == null || rSnap == null)
        {
            // If only one side exposes placement (e.g. geometry undecoded), skip unless coordinates exist on both.
            return null;
        }

        if (string.Equals(lSnap.Kind, rSnap.Kind, StringComparison.Ordinal))
        {
            bool originEqual = PointsEqual(lSnap.Origin, rSnap.Origin, options.LinearToleranceFeet);
            bool endEqual = PointsEqual(lSnap.EndPoint, rSnap.EndPoint, options.LinearToleranceFeet);
            bool centerEqual = PointsEqual(lSnap.ArcCenter, rSnap.ArcCenter, options.LinearToleranceFeet);
            bool radiusEqual = NullableDoublesEqual(lSnap.ArcRadiusFeet, rSnap.ArcRadiusFeet, options.LinearToleranceFeet);
            bool basisEqual = VectorsEqual(lSnap.BasisX, rSnap.BasisX, options.AngleToleranceRadians)
                && VectorsEqual(lSnap.BasisY, rSnap.BasisY, options.AngleToleranceRadians)
                && VectorsEqual(lSnap.BasisZ, rSnap.BasisZ, options.AngleToleranceRadians);
            bool detEqual = NullableDoublesEqual(lSnap.Determinant, rSnap.Determinant, options.AngleToleranceRadians);

            if (originEqual && endEqual && centerEqual && radiusEqual && basisEqual && detEqual)
                return null;
        }

        double dx = rSnap.Origin.X - lSnap.Origin.X;
        double dy = rSnap.Origin.Y - lSnap.Origin.Y;
        double dz = rSnap.Origin.Z - lSnap.Origin.Z;
        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);

        double? rotationDelta = ComputeRotationDeltaDegrees(lSnap, rSnap);
        bool mirroredChanged = lSnap.Determinant.HasValue && rSnap.Determinant.HasValue
            && Math.Sign(lSnap.Determinant.Value) != Math.Sign(rSnap.Determinant.Value);

        return new RvtDiffPlacementChange
        {
            Old = lSnap,
            New = rSnap,
            TranslationFeet = new RvtDiffVector3(dx, dy, dz),
            TranslationDistanceFeet = dist,
            RotationDeltaDegrees = rotationDelta,
            MirroredChanged = mirroredChanged,
        };
    }

    private static RvtDiffPlacementSnapshot? CapturePlacement(Element element)
    {
        if (element.Location is { } loc)
        {
            var start = ToVec(loc.StartPoint);
            var end = ToVec(loc.EndPoint);
            if (start != null)
            {
                return new RvtDiffPlacementSnapshot
                {
                    Kind = string.Equals(loc.CurveType, "Arc", StringComparison.OrdinalIgnoreCase) ? "LocationArc" : "LocationLine",
                    Origin = start,
                    EndPoint = end,
                    LengthFeet = loc.Length,
                    ArcCenter = ToVec(loc.ArcCenter),
                    ArcRadiusFeet = loc.ArcRadius,
                };
            }
        }
        if (element.Transform is { } tr && tr.Origin is { Length: >= 3 })
        {
            var bx = ToVec(tr.BasisX);
            var by = ToVec(tr.BasisY);
            var bz = ToVec(tr.BasisZ);
            double? det = (bx != null && by != null && bz != null) ? Determinant(bx, by, bz) : null;
            return new RvtDiffPlacementSnapshot
            {
                Kind = "Transform",
                Origin = new RvtDiffVector3(tr.Origin[0], tr.Origin[1], tr.Origin[2]),
                BasisX = bx,
                BasisY = by,
                BasisZ = bz,
                Determinant = det,
            };
        }
        if (element.TextPosition is { Length: >= 3 } tp)
        {
            return new RvtDiffPlacementSnapshot
            {
                Kind = "TextPosition",
                Origin = new RvtDiffVector3(tp[0], tp[1], tp[2]),
            };
        }
        if (element.ViewportOffset is { Length: >= 3 } vo)
        {
            return new RvtDiffPlacementSnapshot
            {
                Kind = "ViewportOffset",
                Origin = new RvtDiffVector3(vo[0], vo[1], vo[2]),
            };
        }
        if (element.Geometry?.Transform is { Length: >= 12 } gt)
        {
            var bx = new RvtDiffVector3(gt[0], gt[4], gt[8]);
            var by = new RvtDiffVector3(gt[1], gt[5], gt[9]);
            var bz = new RvtDiffVector3(gt[2], gt[6], gt[10]);
            return new RvtDiffPlacementSnapshot
            {
                Kind = "GeometryTransform",
                Origin = new RvtDiffVector3(gt[3], gt[7], gt[11]),
                BasisX = bx,
                BasisY = by,
                BasisZ = bz,
                Determinant = Determinant(bx, by, bz),
            };
        }
        return null;
    }

    private static RvtDiffGeometryChange? CompareGeometry(
        Element left,
        Element right,
        RvtDocument leftDoc,
        RvtDocument rightDoc,
        Dictionary<long, Element> leftLookup,
        Dictionary<long, Element> rightLookup,
        RvtDiffPlacementChange? placementChange,
        RvtDiffOptions options,
        out bool isUnavailable)
    {
        isUnavailable = false;
        bool leftUndecoded = leftDoc.RawData.WideLayoutNotRewritten || leftDoc.RawData.GeometryUndecoded;
        bool rightUndecoded = rightDoc.RawData.WideLayoutNotRewritten || rightDoc.RawData.GeometryUndecoded;

        if ((left.Geometry == null && leftUndecoded && right.Geometry != null)
            || (right.Geometry == null && rightUndecoded && left.Geometry != null))
        {
            isUnavailable = true;
            return new RvtDiffGeometryChange
            {
                Availability = RvtDiffAvailability.Unavailable,
                Reason = "geometry-undecoded-on-one-revision",
                ChangedAspects = ["unavailable"],
                Old = CaptureGeometrySnapshot(left, leftLookup),
                New = CaptureGeometrySnapshot(right, rightLookup),
            };
        }

        if (left.Geometry == null && right.Geometry == null
            && left.DecodedBox == null && right.DecodedBox == null
            && (left.Layers?.Count ?? 0) == 0 && (right.Layers?.Count ?? 0) == 0
            && (left.SketchLoops?.Count ?? 0) == 0 && (right.SketchLoops?.Count ?? 0) == 0
            && left.Dimension == null && right.Dimension == null)
        {
            return null;
        }

        bool isPartial = (left.Geometry?.UntriangulatedFaces ?? 0) > 0
            || (right.Geometry?.UntriangulatedFaces ?? 0) > 0
            || (left.Geometry?.UnresolvedSymbol ?? false)
            || (right.Geometry?.UnresolvedSymbol ?? false);

        var oldSnap = CaptureGeometrySnapshot(left, leftLookup);
        var newSnap = CaptureGeometrySnapshot(right, rightLookup);
        var changedAspects = new List<string>();
        double? maxBboxDelta = null;

        // If placement only translated cleanly with unchanged orientation/mirroring, compare bounding box shifted back
        // so moved family instances don't falsely emit a bounding-box geometry change alongside PlacementChange.
        double[]? lBox = left.DecodedBox;
        double[]? rBox = right.DecodedBox;
        if (lBox is { Length: >= 6 } && rBox is { Length: >= 6 })
        {
            double shiftX = 0, shiftY = 0, shiftZ = 0;
            if (IsPureTranslation(placementChange, options))
            {
                shiftX = placementChange!.TranslationFeet.X;
                shiftY = placementChange.TranslationFeet.Y;
                shiftZ = placementChange.TranslationFeet.Z;
            }
            double d0 = Math.Abs((lBox[0] + shiftX) - rBox[0]);
            double d1 = Math.Abs((lBox[1] + shiftY) - rBox[1]);
            double d2 = Math.Abs((lBox[2] + shiftZ) - rBox[2]);
            double d3 = Math.Abs((lBox[3] + shiftX) - rBox[3]);
            double d4 = Math.Abs((lBox[4] + shiftY) - rBox[4]);
            double d5 = Math.Abs((lBox[5] + shiftZ) - rBox[5]);
            double maxD = Math.Max(Math.Max(d0, d1), Math.Max(Math.Max(d2, d3), Math.Max(d4, d5)));
            if (maxD > options.LinearToleranceFeet)
            {
                changedAspects.Add("bounding-box");
                maxBboxDelta = maxD;
            }
        }
        else if ((lBox == null) != (rBox == null))
        {
            changedAspects.Add("bounding-box");
        }

        if (oldSnap.FaceCount != newSnap.FaceCount) changedAspects.Add("face-count");
        if (oldSnap.SolidCount != newSnap.SolidCount) changedAspects.Add("solid-count");
        if (oldSnap.BrepSolidCount != newSnap.BrepSolidCount) changedAspects.Add("brep-solid-count");
        if (oldSnap.OpeningCount != newSnap.OpeningCount) changedAspects.Add("opening-count");
        if (oldSnap.InstanceCount != newSnap.InstanceCount) changedAspects.Add("symbol-instance-count");

        if (Math.Abs(oldSnap.TotalFaceAreaSquareFeet - newSnap.TotalFaceAreaSquareFeet) > options.AreaToleranceSquareFeet)
            changedAspects.Add("face-area");

        if (SolidsDiffer(left.Geometry?.Solids, right.Geometry?.Solids, options.LinearToleranceFeet))
            changedAspects.Add("solids");

        if (!SymbolListsEqual(oldSnap.SymbolUniqueIds, newSnap.SymbolUniqueIds))
            changedAspects.Add("symbol-instances");

        if (SketchLoopsDiffer(left.SketchLoops, right.SketchLoops, options.LinearToleranceFeet))
            changedAspects.Add("sketch-loops");

        if (LayersDiffer(left.Layers, right.Layers, leftLookup, rightLookup, options.LinearToleranceFeet))
            changedAspects.Add("layers");

        if (DimensionDiffers(left.Dimension, right.Dimension, options.LinearToleranceFeet))
            changedAspects.Add("dimension");

        if (oldSnap.FaceCount == newSnap.FaceCount
            && oldSnap.FaceCount > 0
            && !changedAspects.Contains("face-area")
            && FacesDiffer(left.Geometry?.Faces, right.Geometry?.Faces, placementChange, options))
        {
            changedAspects.Add("faces");
        }

        if (changedAspects.Count == 0 && !isPartial) return null;
        isUnavailable = isPartial;

        return new RvtDiffGeometryChange
        {
            Availability = isPartial ? RvtDiffAvailability.Partial : RvtDiffAvailability.Confirmed,
            Reason = isPartial ? "partial-geometry-decode" : null,
            ChangedAspects = changedAspects,
            Old = oldSnap,
            New = newSnap,
            MaxBoundingBoxDeltaFeet = maxBboxDelta,
        };
    }

    private static RvtDiffGeometrySnapshot CaptureGeometrySnapshot(Element element, Dictionary<long, Element> lookup)
    {
        var g = element.Geometry;
        double totalArea = 0;
        if (g?.Faces != null)
        {
            foreach (var f in g.Faces)
                totalArea += f.Area ?? PolygonAreaEstimate(f.OuterVertices);
        }
        int openingCount = g?.Solids?.Sum(s => s.Openings?.Count ?? 0) ?? 0;
        var symbols = new List<string>();
        if (g?.Instances != null)
        {
            foreach (var inst in g.Instances)
            {
                if (lookup.TryGetValue(inst.SymbolId, out var sym) && !string.IsNullOrEmpty(sym.UniqueId))
                    symbols.Add(sym.UniqueId);
                else
                    symbols.Add($"id:{inst.SymbolId}");
            }
            symbols.Sort(StringComparer.OrdinalIgnoreCase);
        }

        return new RvtDiffGeometrySnapshot
        {
            BoundingBoxFeet = element.DecodedBox?.ToArray(),
            HasTransform = g?.Transform != null,
            FaceCount = g?.Faces.Count ?? 0,
            EdgeCount = g?.Edges?.Count ?? 0,
            SolidCount = g?.Solids?.Count ?? 0,
            BrepSolidCount = g?.BrepSolids?.Count ?? 0,
            OpeningCount = openingCount,
            InstanceCount = g?.Instances?.Count ?? 0,
            SketchLoopCount = element.SketchLoops?.Count ?? 0,
            LayerCount = element.Layers?.Count ?? 0,
            TotalFaceAreaSquareFeet = totalArea,
            SymbolUniqueIds = symbols,
        };
    }

    private static List<RvtDiffRelationshipChange> CompareRelationships(
        Element left,
        Element right,
        Dictionary<long, Element> leftLookup,
        Dictionary<long, Element> rightLookup)
    {
        var changes = new List<RvtDiffRelationshipChange>();

        void CompareSingle(string relation, long? oldId, long? newId)
        {
            var oldRef = ResolveTargetRef(oldId, leftLookup);
            var newRef = ResolveTargetRef(newId, rightLookup);
            if (TargetRefsEqual(oldRef, newRef)) return;
            changes.Add(new RvtDiffRelationshipChange
            {
                Relation = relation,
                OldTarget = oldRef,
                NewTarget = newRef,
            });
        }

        void CompareSet(string relation, IEnumerable<int>? oldIds, IEnumerable<int>? newIds)
        {
            var oldRefs = SortRefs((oldIds ?? []).Select(id => ResolveTargetRef(id, leftLookup)).OfType<RvtDiffElementRef>().ToList());
            var newRefs = SortRefs((newIds ?? []).Select(id => ResolveTargetRef(id, rightLookup)).OfType<RvtDiffElementRef>().ToList());
            if (oldRefs.Count == newRefs.Count && oldRefs.Zip(newRefs).All(p => TargetRefsEqual(p.First, p.Second)))
                return;
            changes.Add(new RvtDiffRelationshipChange
            {
                Relation = relation,
                OldTargets = oldRefs,
                NewTargets = newRefs,
            });
        }

        CompareSingle("host", ResolveHostId(left), ResolveHostId(right));
        CompareSingle("level", ResolveLevelId(left), ResolveLevelId(right));
        CompareSingle("group", left.SerializedGroupId, right.SerializedGroupId);
        CompareSingle("room", left.SerializedRoomId, right.SerializedRoomId);
        CompareSingle("owner-view", left.OwnerViewId, right.OwnerViewId);
        CompareSingle("viewport-view", left.ViewportViewId, right.ViewportViewId);
        CompareSingle("super-instance", left.SuperInstanceId, right.SuperInstanceId);

        CompareSet("cutters", left.CuttingElementIds, right.CuttingElementIds);
        CompareSet("connectors", ExtractConnectedIds(left), ExtractConnectedIds(right));

        return changes.OrderBy(c => c.Relation, StringComparer.Ordinal).ToList();
    }

    private static List<RvtDiffParameterChange> CompareParameters(
        Element left,
        Element right,
        Dictionary<long, Element> leftLookup,
        Dictionary<long, Element> rightLookup,
        RvtDiffOptions options,
        out bool partial)
    {
        partial = false;
        var lParams = IndexParameters(left);
        var rParams = IndexParameters(right);

        if ((left.MetadataParamPartial() && !right.MetadataParamPartial())
            || (right.MetadataParamPartial() && !left.MetadataParamPartial()))
        {
            partial = true;
        }

        var keys = new SortedSet<string>(lParams.Keys, StringComparer.Ordinal);
        keys.UnionWith(rParams.Keys);

        var changes = new List<RvtDiffParameterChange>();
        foreach (string key in keys)
        {
            lParams.TryGetValue(key, out var lp);
            rParams.TryGetValue(key, out var rp);

            if (lp != null && rp != null)
            {
                if (ParametersEquivalent(lp, rp, leftLookup, rightLookup, options))
                    continue;

                changes.Add(new RvtDiffParameterChange
                {
                    Key = key,
                    Name = rp.DefinitionName ?? lp.DefinitionName ?? key,
                    StorageType = rp.StorageType.ToString(),
                    UnitSymbol = rp.UnitSymbol ?? lp.UnitSymbol,
                    OldValue = CanonicalParamValue(lp, leftLookup),
                    NewValue = CanonicalParamValue(rp, rightLookup),
                    OldDisplayValue = options.IncludeDisplayFormattingDifferences ? lp.AsValueString() : null,
                    NewDisplayValue = options.IncludeDisplayFormattingDifferences ? rp.AsValueString() : null,
                    OldRawElementId = lp.StorageType == StorageType.ElementId && lp.Value is int li ? li : null,
                    NewRawElementId = rp.StorageType == StorageType.ElementId && rp.Value is int ri ? ri : null,
                    ChangeKind = "modified",
                });
            }
            else if (lp != null)
            {
                if (!right.NativeParameterStoresComplete && right.NativeParameterStoreIds != null)
                {
                    partial = true;
                    continue;
                }
                changes.Add(new RvtDiffParameterChange
                {
                    Key = key,
                    Name = lp.DefinitionName ?? key,
                    StorageType = lp.StorageType.ToString(),
                    UnitSymbol = lp.UnitSymbol,
                    OldValue = CanonicalParamValue(lp, leftLookup),
                    NewValue = null,
                    OldRawElementId = lp.StorageType == StorageType.ElementId && lp.Value is int li ? li : null,
                    ChangeKind = "removed",
                });
            }
            else if (rp != null)
            {
                if (!left.NativeParameterStoresComplete && left.NativeParameterStoreIds != null)
                {
                    partial = true;
                    continue;
                }
                changes.Add(new RvtDiffParameterChange
                {
                    Key = key,
                    Name = rp.DefinitionName ?? key,
                    StorageType = rp.StorageType.ToString(),
                    UnitSymbol = rp.UnitSymbol,
                    OldValue = null,
                    NewValue = CanonicalParamValue(rp, rightLookup),
                    NewRawElementId = rp.StorageType == StorageType.ElementId && rp.Value is int ri ? ri : null,
                    ChangeKind = "added",
                });
            }
        }

        return changes;
    }

    private static List<RvtDiffTypeImpact> BuildTypeImpacts(
        IReadOnlyList<(string Key, RvtDiffMatchKind Kind, Element Left, Element Right)> matchedPairs,
        IReadOnlyList<RvtDiffModifiedElement> modified,
        RvtDocument leftDoc,
        RvtDocument rightDoc,
        Dictionary<long, Element> leftLookup,
        Dictionary<long, Element> rightLookup,
        Dictionary<string, List<Element>> leftUniqueGroups,
        Dictionary<string, List<Element>> rightUniqueGroups,
        ElementFilter filter,
        RvtDiffOptions options)
    {
        var impacts = new Dictionary<string, (RvtDiffElementRef TypeRef, HashSet<string> Kinds, List<RvtDiffParameterChange> Params, bool GeomChanged, bool LayerChanged, List<RvtDiffElementRef> Instances, HashSet<string> SeenInstances)>(
            StringComparer.OrdinalIgnoreCase);

        void EnsureEntry(RvtDiffElementRef typeRef)
        {
            if (!impacts.ContainsKey(typeRef.IdentityKey))
                impacts[typeRef.IdentityKey] = (typeRef, new HashSet<string>(StringComparer.Ordinal), [], false, false, [], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        // 1. Type elements that themselves changed (e.g. WallType layer/parameter/symbol edit).
        var modifiedByKey = modified.ToDictionary(m => m.IdentityKey, StringComparer.OrdinalIgnoreCase);
        var matchedKeys = matchedPairs.Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Index instances in rightDoc/leftDoc pointing to each type key in one O(N) pass each.
        var (allLeftInstancesByType, leftReferencedTypeIds) = BuildTypeIndices(leftDoc, leftLookup);
        var (allRightInstancesByType, rightReferencedTypeIds) = BuildTypeIndices(rightDoc, rightLookup);

        foreach (var (uid, rList) in rightUniqueGroups)
        {
            if (rList.Count != 1) continue;
            if (!leftUniqueGroups.TryGetValue(uid, out var lList) || lList.Count != 1) continue;
            Element lType = lList[0], rType = rList[0];
            if (!IsTypeElement(lType, leftReferencedTypeIds) && !IsTypeElement(rType, rightReferencedTypeIds)) continue;

            RvtDiffModifiedElement? typeMod = null;
            if (modifiedByKey.TryGetValue(uid, out var existing))
            {
                typeMod = existing;
            }
            else if (!matchedKeys.Contains(uid))
            {
                // Filtered out from main pair list: only evaluate if any filtered instance references it.
                bool hasFilteredInstance = (allRightInstancesByType.TryGetValue(uid, out var rCand) && rCand.Any(i => filter.Matches(i, rightDoc)))
                    || (allLeftInstancesByType.TryGetValue(uid, out var lCand) && lCand.Any(i => filter.Matches(i, leftDoc)));
                if (hasFilteredInstance)
                    typeMod = CompareMatchedElement(uid, RvtDiffMatchKind.UniqueId, lType, rType, leftDoc, rightDoc, leftLookup, rightLookup, options);
            }
            if (typeMod == null || !typeMod.HasSemanticChange) continue;

            var typeRef = CreateRef(rType, rightDoc, rightLookup);
            var affected = new Dictionary<string, RvtDiffElementRef>(StringComparer.OrdinalIgnoreCase);

            if (allRightInstancesByType.TryGetValue(uid, out var rInsts))
                foreach (var inst in rInsts)
                    if (filter.Matches(inst, rightDoc))
                        affected.TryAdd(PersistentKeyOf(inst) is { Length: > 0 } k ? k : $"id:{inst.ElementId}", CreateRef(inst, rightDoc, rightLookup));

            if (allLeftInstancesByType.TryGetValue(uid, out var lInsts))
                foreach (var inst in lInsts)
                    if (filter.Matches(inst, leftDoc))
                        affected.TryAdd(PersistentKeyOf(inst) is { Length: > 0 } k ? k : $"id:{inst.ElementId}", CreateRef(inst, leftDoc, leftLookup));

            if (affected.Count == 0 && !filter.Matches(rType, rightDoc) && !filter.Matches(lType, leftDoc))
                continue;

            EnsureEntry(typeRef);
            var entry = impacts[typeRef.IdentityKey];
            entry.Kinds.Add("type-modified");
            entry.Params.AddRange(typeMod.ParameterChanges);
            bool layerChanged = typeMod.GeometryChange?.ChangedAspects.Contains("layers") == true;
            bool geomChanged = typeMod.HasGeometryChange;
            var mergedInstances = affected.Values.ToList();
            var seenSet = new HashSet<string>(mergedInstances.Select(x => x.IdentityKey), StringComparer.OrdinalIgnoreCase);
            impacts[typeRef.IdentityKey] = (typeRef, entry.Kinds, entry.Params, geomChanged, layerChanged, mergedInstances, seenSet);
        }

        // 2. Matched instances that switched from OldType -> NewType.
        foreach (var mod in modified)
        {
            if (mod.TypeChange is not { } tc) continue;

            void AddReassignment(long typeId, string? uniqueId, string? typeName, string? familyName, RvtDocument doc, Dictionary<long, Element> lookup, string tag)
            {
                if (typeId <= 0 && string.IsNullOrEmpty(uniqueId)) return;
                Element? typeElem = (typeId > 0 && lookup.TryGetValue(typeId, out var found)) ? found : null;
                var typeRef = typeElem != null
                    ? CreateRef(typeElem, doc, lookup)
                    : new RvtDiffElementRef
                    {
                        IdentityKey = !string.IsNullOrEmpty(uniqueId) ? uniqueId : $"id:{typeId}",
                        UniqueId = uniqueId,
                        ElementId = typeId,
                        Name = typeName,
                        FamilyName = familyName,
                    };
                EnsureEntry(typeRef);
                var current = impacts[typeRef.IdentityKey];
                current.Kinds.Add(tag);
                if (current.SeenInstances.Add(mod.Element.IdentityKey))
                    current.Instances.Add(mod.Element);
            }

            AddReassignment(tc.OldTypeId, tc.OldTypeUniqueId, tc.OldTypeName, tc.OldFamilyName, leftDoc, leftLookup, "instance-reassigned-from");
            AddReassignment(tc.NewTypeId, tc.NewTypeUniqueId, tc.NewTypeName, tc.NewFamilyName, rightDoc, rightLookup, "instance-reassigned-to");
        }

        return impacts.Values
            .Select(v => new RvtDiffTypeImpact
            {
                TypeElement = v.TypeRef,
                ChangeKinds = v.Kinds.OrderBy(k => k, StringComparer.Ordinal).ToList(),
                ParameterChanges = v.Params,
                GeometryChanged = v.GeomChanged,
                LayerChanged = v.LayerChanged,
                AffectedInstances = SortRefs(v.Instances),
            })
            .ToList();
    }

    private static List<RvtDiffExternalLinkChange> CompareTransmissionLinks(TransmissionInfo? left, TransmissionInfo? right)
    {
        var changes = new List<RvtDiffExternalLinkChange>();
        var lRefs = left?.ExternalReferences ?? [];
        var rRefs = right?.ExternalReferences ?? [];
        if (lRefs.Count == 0 && rRefs.Count == 0) return changes;

        static string RefKey(ExternalFileRef r) => $"{r.ElementId}:{r.ReferenceType ?? ""}";
        var lMap = lRefs.GroupBy(RefKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var rMap = rRefs.GroupBy(RefKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var allKeys = new SortedSet<string>(lMap.Keys, StringComparer.Ordinal);
        allKeys.UnionWith(rMap.Keys);
        foreach (string key in allKeys)
        {
            lMap.TryGetValue(key, out var lr);
            rMap.TryGetValue(key, out var rr);
            if (lr != null && rr != null)
            {
                string? lp = lr.Path ?? lr.AbsolutePath;
                string? rp = rr.Path ?? rr.AbsolutePath;
                if (!string.Equals(lp, rp, StringComparison.Ordinal)
                    || !string.Equals(lr.LoadState, rr.LoadState, StringComparison.Ordinal)
                    || !string.Equals(lr.ServerLocation, rr.ServerLocation, StringComparison.Ordinal))
                {
                    changes.Add(new RvtDiffExternalLinkChange
                    {
                        Kind = "modified",
                        ElementId = rr.ElementId,
                        ReferenceType = rr.ReferenceType ?? lr.ReferenceType,
                        OldPath = lp,
                        NewPath = rp,
                        OldLoadState = lr.LoadState,
                        NewLoadState = rr.LoadState,
                    });
                }
            }
            else if (lr != null)
            {
                changes.Add(new RvtDiffExternalLinkChange
                {
                    Kind = "removed",
                    ElementId = lr.ElementId,
                    ReferenceType = lr.ReferenceType,
                    OldPath = lr.Path ?? lr.AbsolutePath,
                    OldLoadState = lr.LoadState,
                });
            }
            else if (rr != null)
            {
                changes.Add(new RvtDiffExternalLinkChange
                {
                    Kind = "added",
                    ElementId = rr.ElementId,
                    ReferenceType = rr.ReferenceType,
                    NewPath = rr.Path ?? rr.AbsolutePath,
                    NewLoadState = rr.LoadState,
                });
            }
        }
        return changes;
    }

    private static RvtReaderOptions CloneReaderOptions(RvtReaderOptions? source, RvtReadDiagnostics diag)
    {
        if (source == null)
        {
            return new RvtReaderOptions
            {
                R22Layout = true,
                ResolveCurrentCopies = true,
                Diagnostics = diag,
            };
        }
        return new RvtReaderOptions
        {
            ExternalDumpPath = source.ExternalDumpPath,
            ExternalDumpClassPrefix = source.ExternalDumpClassPrefix,
            SystemFamilyNameLocale = source.SystemFamilyNameLocale,
            SeparateFacesWithDistinctOpenings = source.SeparateFacesWithDistinctOpenings,
            R22Layout = source.R22Layout,
            PreserveWideLayout = source.PreserveWideLayout,
            ResolveCurrentCopies = source.ResolveCurrentCopies,
            RaiseLimits = source.RaiseLimits,
            ReleasePartitions = source.ReleasePartitions,
            Diagnostics = diag,
            Progress = source.Progress,
            DiagnosticOutput = source.DiagnosticOutput,
        };
    }

    private static bool IsPartialDocument(
        RevitFileInfo info,
        RvtReadDiagnostics? diag,
        out int limitHits,
        out int failedStreams)
    {
        limitHits = diag?.LimitHits.Count ?? 0;
        failedStreams = diag?.Streams.Count(s => s.Failed > 0 || s.Unverified > 0) ?? 0;
        bool openedFromContainer = !string.IsNullOrEmpty(info.Metadata.StreamVersion);
        return limitHits > 0 || failedStreams > 0 || (openedFromContainer && !info.ElemTableIsExact);
    }

    private static (RvtRevisionRelation Relation, string Reason) AssessRelation(
        RvtDocument left,
        RvtDocument right,
        Dictionary<string, List<Element>> leftUnique,
        Dictionary<string, List<Element>> rightUnique)
    {
        int sharedUniqueIds = leftUnique.Keys.Count(k => rightUnique.ContainsKey(k));
        Guid? lGuid = left.RawData.SerializedDocumentGuid;
        Guid? rGuid = right.RawData.SerializedDocumentGuid;

        if (lGuid.HasValue && rGuid.HasValue && lGuid.Value != rGuid.Value && sharedUniqueIds == 0)
            return (RvtRevisionRelation.Unrelated, "distinct-document-guid-and-zero-shared-unique-ids");

        if (sharedUniqueIds > 0)
            return (RvtRevisionRelation.Related, $"shared-{sharedUniqueIds}-persistent-unique-ids");

        if (lGuid.HasValue && rGuid.HasValue && lGuid.Value == rGuid.Value)
            return (RvtRevisionRelation.Related, "matching-document-guid");

        if (leftUnique.Count > 0 && rightUnique.Count > 0 && sharedUniqueIds == 0)
            return (RvtRevisionRelation.Unrelated, "zero-shared-persistent-unique-ids");

        return (RvtRevisionRelation.Indeterminate, "insufficient-persistent-identity-overlap");
    }

    private static Dictionary<long, Element> BuildElementLookup(RvtDocument doc)
    {
        var map = new Dictionary<long, Element>(doc.Elements.Count);
        foreach (var e in doc.Elements)
            map.TryAdd(e.ElementId, e);
        return map;
    }

    private static Dictionary<string, List<Element>> GroupByUniqueId(IEnumerable<Element> elements)
    {
        var groups = new Dictionary<string, List<Element>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in elements)
        {
            if (string.IsNullOrWhiteSpace(e.UniqueId)) continue;
            if (!groups.TryGetValue(e.UniqueId, out var list))
                groups[e.UniqueId] = list = [];
            list.Add(e);
        }
        return groups;
    }

    private static Dictionary<(long Id, int? CatId), List<Element>> GroupByElementIdAndCategory(IEnumerable<Element> elements)
    {
        var groups = new Dictionary<(long Id, int? CatId), List<Element>>();
        foreach (var e in elements)
        {
            var key = (e.ElementId, e.CategoryId);
            if (!groups.TryGetValue(key, out var list))
                groups[key] = list = [];
            list.Add(e);
        }
        return groups;
    }

    private static void RecordMissingPersistentIdEntries(
        IEnumerable<Element>? elements,
        RvtDocument doc,
        Dictionary<long, Element> lookup,
        string side,
        ElementFilter filter,
        RvtDiffOptions options,
        List<RvtDiffUnavailableEntry> unavailable)
    {
        if (elements == null) return;
        foreach (var e in elements)
        {
            if (!filter.Matches(e, doc)) continue;
            unavailable.Add(new RvtDiffUnavailableEntry
            {
                Side = side,
                Element = CreateRef(e, doc, lookup),
                Aspect = "identity",
                Reason = "missing-persistent-identity",
                Detail = "Element lacks a persistent UniqueId; excluded from confirmed change/deletion reporting.",
            });
        }
        _ = options;
    }

    private static (Dictionary<string, List<Element>> InstancesByTypeKey, HashSet<long> ReferencedTypeIds) BuildTypeIndices(
        RvtDocument doc,
        Dictionary<long, Element> lookup)
    {
        var result = new Dictionary<string, List<Element>>(StringComparer.OrdinalIgnoreCase);
        var referencedTypeIds = new HashSet<long>();
        foreach (var e in doc.Elements)
        {
            if (e.IsBlockOnly) continue;
            Element? t = ResolveTypeElement(e, doc, lookup);
            if (t == null) continue;
            referencedTypeIds.Add(t.ElementId);
            referencedTypeIds.Add(t.Id);
            string key = PersistentKeyOf(t);
            if (string.IsNullOrEmpty(key)) continue;
            if (!result.TryGetValue(key, out var list))
                result[key] = list = [];
            list.Add(e);
        }
        return (result, referencedTypeIds);
    }

    private static Element? ResolveTypeElement(Element element, RvtDocument doc, Dictionary<long, Element> lookup)
    {
        int typeId = element.FamilyTypeId;
        if (typeId > 0 && lookup.TryGetValue(typeId, out var byParam)) return byParam;
        if (element.TypeElementId > 0 && lookup.TryGetValue(element.TypeElementId, out var byProp)) return byProp;
        return null;
    }

    private static bool IsTypeElement(Element elem, HashSet<long> referencedTypeIds)
    {
        if (elem.NativeIsSymbol == true) return true;
        if (elem.RootClassName is { } cls && (cls.EndsWith("Type", StringComparison.Ordinal) || cls.EndsWith("Symbol", StringComparison.Ordinal)))
            return true;
        return referencedTypeIds.Contains(elem.ElementId) || referencedTypeIds.Contains(elem.Id);
    }

    private static string PersistentKeyOf(Element? elem)
    {
        if (elem == null) return "";
        return !string.IsNullOrWhiteSpace(elem.UniqueId) ? elem.UniqueId : $"id:{elem.ElementId}";
    }

    private static RvtDiffElementRef CreateRef(Element elem, RvtDocument doc, Dictionary<long, Element> lookup)
    {
        Element? typeElem = ResolveTypeElement(elem, doc, lookup);
        return new RvtDiffElementRef
        {
            IdentityKey = PersistentKeyOf(elem),
            UniqueId = elem.UniqueId,
            ElementId = elem.ElementId,
            CategoryId = elem.CategoryId,
            Category = elem.Category,
            Name = elem.Name,
            FamilyName = elem.FamilyName,
            TypeUniqueId = typeElem?.UniqueId,
            TypeId = typeElem?.ElementId ?? (elem.FamilyTypeId > 0 ? elem.FamilyTypeId : null),
            TypeName = typeElem?.Name,
        };
    }

    private static List<RvtDiffElementRef> SortRefs(List<RvtDiffElementRef> refs) =>
        refs.OrderBy(r => r.IdentityKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.ElementId)
            .ToList();

    private static Dictionary<string, Parameter> IndexParameters(Element elem)
    {
        var map = new Dictionary<string, Parameter>(StringComparer.Ordinal);
        foreach (var p in elem.Parameters)
        {
            if (!p.HasRevitValue) continue;
            string key = ParameterKey(p);
            map.TryAdd(key, p);
        }
        return map;
    }

    private static string ParameterKey(Parameter p)
    {
        if (p.Id < 0)
            return "BIP:" + (p.DefinitionBuiltIn ?? p.Id.ToString(CultureInfo.InvariantCulture));
        if (p.Guid is { } guid)
            return "GUID:" + guid.ToString("D");
        if (!string.IsNullOrEmpty(p.ForgeId))
            return "FORGE:" + p.ForgeId;
        return "NAME:" + (p.DefinitionName ?? p.Id.ToString(CultureInfo.InvariantCulture));
    }

    private static bool ParametersEquivalent(
        Parameter left,
        Parameter right,
        Dictionary<long, Element> leftLookup,
        Dictionary<long, Element> rightLookup,
        RvtDiffOptions options)
    {
        if (left.StorageType != right.StorageType) return false;
        if (options.IncludeDisplayFormattingDifferences
            && !string.Equals(left.AsValueString(), right.AsValueString(), StringComparison.Ordinal))
            return false;

        switch (left.StorageType)
        {
            case StorageType.Double:
                double ld = Convert.ToDouble(left.Value, CultureInfo.InvariantCulture);
                double rd = Convert.ToDouble(right.Value, CultureInfo.InvariantCulture);
                return Math.Abs(ld - rd) <= options.NumericParameterTolerance;
            case StorageType.Integer:
                return Equals(left.Value, right.Value);
            case StorageType.String:
                return string.Equals(left.Value as string, right.Value as string, StringComparison.Ordinal);
            case StorageType.ElementId:
                var lt = ResolveTargetRef(left.Value is int lid ? lid : null, leftLookup);
                var rt = ResolveTargetRef(right.Value is int rid ? rid : null, rightLookup);
                return TargetRefsEqual(lt, rt);
            default:
                return Equals(left.Value, right.Value);
        }
    }

    private static object? CanonicalParamValue(Parameter p, Dictionary<long, Element> lookup)
    {
        if (p.StorageType == StorageType.ElementId && p.Value is int id)
        {
            if (id <= 0 && !p.HasResolvedZeroElementId) return id;
            var target = ResolveTargetRef(id, lookup);
            if (target != null)
                return target.Name != null ? $"{target.IdentityKey} ({target.Name})" : target.IdentityKey;
            return $"id:{id}";
        }
        return p.Value;
    }

    private static RvtDiffElementRef? ResolveTargetRef(long? id, Dictionary<long, Element> lookup)
    {
        if (id is not long valid || valid < 0) return null;
        if (valid == 0 && !lookup.ContainsKey(0)) return null;
        if (lookup.TryGetValue(valid, out var target))
        {
            return new RvtDiffElementRef
            {
                IdentityKey = !string.IsNullOrWhiteSpace(target.UniqueId) ? target.UniqueId : $"id:{valid}",
                UniqueId = target.UniqueId,
                ElementId = target.ElementId,
                CategoryId = target.CategoryId,
                Category = target.Category,
                Name = target.Name,
                FamilyName = target.FamilyName,
            };
        }
        return new RvtDiffElementRef
        {
            IdentityKey = $"id:{valid}",
            ElementId = valid,
        };
    }

    private static bool TargetRefsEqual(RvtDiffElementRef? a, RvtDiffElementRef? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        if (!string.IsNullOrEmpty(a.UniqueId) && !string.IsNullOrEmpty(b.UniqueId))
            return string.Equals(a.UniqueId, b.UniqueId, StringComparison.OrdinalIgnoreCase);
        return string.Equals(a.IdentityKey, b.IdentityKey, StringComparison.OrdinalIgnoreCase);
    }

    private static long? ResolveHostId(Element elem)
    {
        if (elem.SerializedHostId is int sh && sh > 0) return sh;
        if (elem.HostElementId is long h && h > 0) return h;
        return null;
    }

    private static long? ResolveLevelId(Element elem)
    {
        if (elem.SerializedStairBaseLevelId is int sl && sl > 0) return sl;
        foreach (var bip in new[] { BuiltInParameter.LEVEL_PARAM, BuiltInParameter.WALL_BASE_CONSTRAINT, BuiltInParameter.SCHEDULE_LEVEL_PARAM })
        {
            if (elem.LookupParameter(bip)?.Value is int id && id > 0) return id;
        }
        return null;
    }

    private static IEnumerable<int>? ExtractConnectedIds(Element elem) =>
        elem.SerializedConnectorGraph?
            .SelectMany(c => c.Refs)
            .Select(r => r.Id)
            .Where(id => id > 0 && id != elem.Id)
            .Distinct();

    private static bool MetadataParamPartial(this Element elem) =>
        !elem.NativeParameterStoresComplete && elem.NativeParameterStoreIds != null;

    private static RvtDiffVector3? ToVec(double[]? coords) =>
        coords is { Length: >= 3 } ? new RvtDiffVector3(coords[0], coords[1], coords[2]) : null;

    private static bool PointsEqual(RvtDiffVector3? a, RvtDiffVector3? b, double tol)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        return Math.Abs(a.X - b.X) <= tol && Math.Abs(a.Y - b.Y) <= tol && Math.Abs(a.Z - b.Z) <= tol;
    }

    private static bool VectorsEqual(RvtDiffVector3? a, RvtDiffVector3? b, double tol)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        return Math.Abs(a.X - b.X) <= tol && Math.Abs(a.Y - b.Y) <= tol && Math.Abs(a.Z - b.Z) <= tol;
    }

    private static bool NullableDoublesEqual(double? a, double? b, double tol)
    {
        if (!a.HasValue && !b.HasValue) return true;
        if (!a.HasValue || !b.HasValue) return false;
        return Math.Abs(a.Value - b.Value) <= tol;
    }

    private static double Determinant(RvtDiffVector3 bx, RvtDiffVector3 by, RvtDiffVector3 bz) =>
        bx.X * (by.Y * bz.Z - by.Z * bz.Y)
        - by.X * (bx.Y * bz.Z - bx.Z * bz.Y)
        + bz.X * (bx.Y * bz.Y - bx.Z * by.Y);

    private static double? ComputeRotationDeltaDegrees(RvtDiffPlacementSnapshot l, RvtDiffPlacementSnapshot r)
    {
        if (l.BasisX != null && r.BasisX != null && l.BasisY != null && r.BasisY != null && l.BasisZ != null && r.BasisZ != null)
        {
            double trace =
                (l.BasisX.X * r.BasisX.X + l.BasisX.Y * r.BasisX.Y + l.BasisX.Z * r.BasisX.Z)
                + (l.BasisY.X * r.BasisY.X + l.BasisY.Y * r.BasisY.Y + l.BasisY.Z * r.BasisY.Z)
                + (l.BasisZ.X * r.BasisZ.X + l.BasisZ.Y * r.BasisZ.Y + l.BasisZ.Z * r.BasisZ.Z);
            double cos = Math.Clamp((trace - 1.0) / 2.0, -1.0, 1.0);
            return Math.Acos(cos) * (180.0 / Math.PI);
        }
        if (l.EndPoint != null && r.EndPoint != null)
        {
            double ax = l.EndPoint.X - l.Origin.X, ay = l.EndPoint.Y - l.Origin.Y, az = l.EndPoint.Z - l.Origin.Z;
            double bx = r.EndPoint.X - r.Origin.X, by = r.EndPoint.Y - r.Origin.Y, bz = r.EndPoint.Z - r.Origin.Z;
            double la = Math.Sqrt(ax * ax + ay * ay + az * az);
            double lb = Math.Sqrt(bx * bx + by * by + bz * bz);
            if (la > 1e-9 && lb > 1e-9)
            {
                double cos = Math.Clamp((ax * bx + ay * by + az * bz) / (la * lb), -1.0, 1.0);
                return Math.Acos(cos) * (180.0 / Math.PI);
            }
        }
        return null;
    }

    private static double PolygonAreaEstimate(List<double[]>? vertices)
    {
        if (vertices == null || vertices.Count < 3) return 0;
        double ax = 0, ay = 0, az = 0;
        var p0 = vertices[0];
        for (int i = 1; i + 1 < vertices.Count; i++)
        {
            var p1 = vertices[i];
            var p2 = vertices[i + 1];
            double ux = p1[0] - p0[0], uy = p1[1] - p0[1], uz = p1[2] - p0[2];
            double vx = p2[0] - p0[0], vy = p2[1] - p0[1], vz = p2[2] - p0[2];
            ax += uy * vz - uz * vy;
            ay += uz * vx - ux * vz;
            az += ux * vy - uy * vx;
        }
        return 0.5 * Math.Sqrt(ax * ax + ay * ay + az * az);
    }

    private static bool SymbolListsEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static bool SketchLoopsDiffer(List<List<double[]>>? left, List<List<double[]>>? right, double tol)
    {
        int lc = left?.Count ?? 0, rc = right?.Count ?? 0;
        if (lc != rc) return true;
        if (lc == 0) return false;
        for (int i = 0; i < lc; i++)
        {
            if (left![i].Count != right![i].Count) return true;
            for (int j = 0; j < left[i].Count; j++)
            {
                double[] a = left[i][j], b = right[i][j];
                if (Math.Abs(a[0] - b[0]) > tol || Math.Abs(a[1] - b[1]) > tol || Math.Abs(a[2] - b[2]) > tol)
                    return true;
            }
        }
        return false;
    }

    private static bool LayersDiffer(
        List<MaterialLayer>? left,
        List<MaterialLayer>? right,
        Dictionary<long, Element> leftLookup,
        Dictionary<long, Element> rightLookup,
        double tol)
    {
        int lc = left?.Count ?? 0, rc = right?.Count ?? 0;
        if (lc != rc) return true;
        if (lc == 0) return false;
        double tolMm = tol * 304.8;
        for (int i = 0; i < lc; i++)
        {
            var a = left![i];
            var b = right![i];
            if (Math.Abs(a.ThicknessMm - b.ThicknessMm) > tolMm) return true;
            if (!string.Equals(a.Function, b.Function, StringComparison.Ordinal)) return true;
            var am = a.MaterialId > 0 ? ResolveTargetRef(a.MaterialId, leftLookup) : null;
            var bm = b.MaterialId > 0 ? ResolveTargetRef(b.MaterialId, rightLookup) : null;
            if (!TargetRefsEqual(am, bm) && !string.Equals(a.MaterialName, b.MaterialName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool DimensionDiffers(DimensionData? a, DimensionData? b, double tol)
    {
        if (a == null && b == null) return false;
        if (a == null || b == null) return true;
        if (Math.Abs(a.Value - b.Value) > tol) return true;
        if (a.Segments.Count != b.Segments.Count) return true;
        for (int i = 0; i < a.Segments.Count; i++)
            if (Math.Abs(a.Segments[i] - b.Segments[i]) > tol) return true;
        return false;
    }

    private sealed class ElementFilter
    {
        private readonly HashSet<string>? _categories;
        private readonly HashSet<int>? _categoryIds;
        private readonly HashSet<string>? _elements;

        public ElementFilter(IReadOnlyList<string>? categories, IReadOnlyList<string>? elements)
        {
            if (categories is { Count: > 0 })
            {
                _categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _categoryIds = new HashSet<int>();
                foreach (string c in categories)
                {
                    string t = c.Trim();
                    if (t.Length == 0) continue;
                    if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                        _categoryIds.Add(id);
                    else
                        _categories.Add(t);
                }
            }
            if (elements is { Count: > 0 })
            {
                _elements = new HashSet<string>(elements.Select(e => e.Trim()).Where(e => e.Length > 0), StringComparer.OrdinalIgnoreCase);
            }
        }

        public bool Matches(Element elem, RvtDocument doc)
        {
            _ = doc;
            if (_elements != null)
            {
                bool matchId = (!string.IsNullOrWhiteSpace(elem.UniqueId) && _elements.Contains(elem.UniqueId))
                    || _elements.Contains(elem.ElementId.ToString(CultureInfo.InvariantCulture))
                    || _elements.Contains(elem.Id.ToString(CultureInfo.InvariantCulture));
                if (!matchId) return false;
            }
            if (_categories != null && _categoryIds != null)
            {
                bool catIdMatch = elem.CategoryId.HasValue && _categoryIds.Contains(elem.CategoryId.Value);
                bool catNameMatch = !string.IsNullOrWhiteSpace(elem.Category)
                    && (_categories.Contains(elem.Category)
                        || (elem.Category.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) && _categories.Contains(elem.Category[4..])));
                if (!catIdMatch && !catNameMatch) return false;
            }
            return true;
        }
    }

    private static RvtRevisionDiffResult RedactDiffResult(RvtRevisionDiffResult result, string? leftPath, string? rightPath)
    {
        var tokens = RvtPiiRedactor.CollectSensitiveTokens(leftPath, rightPath);
        string? RedactText(string? value) => RvtPiiRedactor.RedactText(value, extraTokens: tokens);

        string RedactStr(string? s) => RedactText(s) ?? "";

        RvtDiffElementRef RedactRef(RvtDiffElementRef r) => new()
        {
            IdentityKey = r.IdentityKey,
            UniqueId = r.UniqueId,
            ElementId = r.ElementId,
            CategoryId = r.CategoryId,
            Category = r.Category,
            Name = RedactText(r.Name),
            FamilyName = RedactText(r.FamilyName),
            TypeUniqueId = r.TypeUniqueId,
            TypeId = r.TypeId,
            TypeName = RedactText(r.TypeName),
        };

        RvtDiffParameterChange RedactParam(RvtDiffParameterChange p) => new()
        {
            Key = RedactText(p.Key) ?? p.Key,
            Name = RedactText(p.Name) ?? p.Name,
            StorageType = p.StorageType,
            UnitSymbol = p.UnitSymbol,
            OldValue = p.OldValue is string os ? RedactText(os) : p.OldValue,
            NewValue = p.NewValue is string ns ? RedactText(ns) : p.NewValue,
            OldDisplayValue = RedactText(p.OldDisplayValue),
            NewDisplayValue = RedactText(p.NewDisplayValue),
            OldRawElementId = p.OldRawElementId,
            NewRawElementId = p.NewRawElementId,
            ChangeKind = p.ChangeKind,
        };

        return new RvtRevisionDiffResult
        {
            Schema = result.Schema,
            Left = new RvtRevisionSummary
            {
                Label = RedactStr(result.Left.Label),
                RevitVersion = result.Left.RevitVersion,
                Build = result.Left.Build,
                DocumentGuid = result.Left.DocumentGuid,
                ElementCount = result.Left.ElementCount,
                FilteredElementCount = result.Left.FilteredElementCount,
                UniqueIdElementCount = result.Left.UniqueIdElementCount,
                ElemTableIsExact = result.Left.ElemTableIsExact,
                GeometryUndecoded = result.Left.GeometryUndecoded,
                WideLayoutNotRewritten = result.Left.WideLayoutNotRewritten,
                LimitHitCount = result.Left.LimitHitCount,
                FailedStreamCount = result.Left.FailedStreamCount,
                IsPartialRead = result.Left.IsPartialRead,
            },
            Right = new RvtRevisionSummary
            {
                Label = RedactStr(result.Right.Label),
                RevitVersion = result.Right.RevitVersion,
                Build = result.Right.Build,
                DocumentGuid = result.Right.DocumentGuid,
                ElementCount = result.Right.ElementCount,
                FilteredElementCount = result.Right.FilteredElementCount,
                UniqueIdElementCount = result.Right.UniqueIdElementCount,
                ElemTableIsExact = result.Right.ElemTableIsExact,
                GeometryUndecoded = result.Right.GeometryUndecoded,
                WideLayoutNotRewritten = result.Right.WideLayoutNotRewritten,
                LimitHitCount = result.Right.LimitHitCount,
                FailedStreamCount = result.Right.FailedStreamCount,
                IsPartialRead = result.Right.IsPartialRead,
            },
            Relation = result.Relation,
            RelationReason = result.RelationReason,
            VersionChanged = result.VersionChanged,
            Tolerances = result.Tolerances,
            TotalMatchedElements = result.TotalMatchedElements,
            UnchangedCount = result.UnchangedCount,
            Added = result.Added.Select(RedactRef).ToList(),
            Removed = result.Removed.Select(RedactRef).ToList(),
            Modified = result.Modified.Select(m => new RvtDiffModifiedElement
            {
                IdentityKey = m.IdentityKey,
                MatchKind = m.MatchKind,
                Element = RedactRef(m.Element),
                TypeChange = m.TypeChange == null ? null : new RvtDiffTypeChange
                {
                    OldTypeId = m.TypeChange.OldTypeId,
                    OldTypeUniqueId = m.TypeChange.OldTypeUniqueId,
                    OldTypeName = RedactText(m.TypeChange.OldTypeName),
                    OldFamilyName = RedactText(m.TypeChange.OldFamilyName),
                    NewTypeId = m.TypeChange.NewTypeId,
                    NewTypeUniqueId = m.TypeChange.NewTypeUniqueId,
                    NewTypeName = RedactText(m.TypeChange.NewTypeName),
                    NewFamilyName = RedactText(m.TypeChange.NewFamilyName),
                },
                PlacementChange = m.PlacementChange,
                GeometryChange = m.GeometryChange,
                RelationshipChanges = m.RelationshipChanges.Select(r => new RvtDiffRelationshipChange
                {
                    Relation = r.Relation,
                    OldTarget = r.OldTarget == null ? null : RedactRef(r.OldTarget),
                    NewTarget = r.NewTarget == null ? null : RedactRef(r.NewTarget),
                    OldTargets = r.OldTargets?.Select(RedactRef).ToList(),
                    NewTargets = r.NewTargets?.Select(RedactRef).ToList(),
                }).ToList(),
                ParameterChanges = m.ParameterChanges.Select(RedactParam).ToList(),
                AffectedByTypeIdentityKeys = m.AffectedByTypeIdentityKeys,
                UnavailableAspects = m.UnavailableAspects,
            }).ToList(),
            TypeImpacts = result.TypeImpacts.Select(t => new RvtDiffTypeImpact
            {
                TypeElement = RedactRef(t.TypeElement),
                ChangeKinds = t.ChangeKinds,
                ParameterChanges = t.ParameterChanges.Select(RedactParam).ToList(),
                GeometryChanged = t.GeometryChanged,
                LayerChanged = t.LayerChanged,
                AffectedInstances = t.AffectedInstances.Select(RedactRef).ToList(),
            }).ToList(),
            AmbiguousMatches = result.AmbiguousMatches.Select(a => new RvtDiffAmbiguousMatch
            {
                IdentityKey = a.IdentityKey,
                Reason = a.Reason,
                LeftElements = a.LeftElements.Select(RedactRef).ToList(),
                RightElements = a.RightElements.Select(RedactRef).ToList(),
            }).ToList(),
            Unavailable = result.Unavailable.Select(u => new RvtDiffUnavailableEntry
            {
                Side = u.Side,
                Element = u.Element == null ? null : RedactRef(u.Element),
                Aspect = u.Aspect,
                Reason = u.Reason,
                Detail = RedactText(u.Detail),
            }).ToList(),
            ExternalLinkChanges = result.ExternalLinkChanges.Select(e => new RvtDiffExternalLinkChange
            {
                Kind = e.Kind,
                ElementId = e.ElementId,
                ReferenceType = e.ReferenceType,
                OldPath = RedactText(e.OldPath),
                NewPath = RedactText(e.NewPath),
                OldLoadState = e.OldLoadState,
                NewLoadState = e.NewLoadState,
            }).ToList(),
            Warnings = result.Warnings.Select(RedactStr).ToList(),
        };
    }
}
