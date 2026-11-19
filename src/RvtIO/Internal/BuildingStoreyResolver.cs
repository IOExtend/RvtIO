using RvtIO.Model;

namespace RvtIO.Internal;

/// <summary>Building-storey selection and the assignment rules shared by the scene and IFC exporters.</summary>
internal sealed class BuildingStoreyResolver
{
    private readonly Dictionary<int, int> _storeyIds;
    private readonly IReadOnlyDictionary<int, Element> _elemById;
    private readonly int _defaultStoreyId;
    private readonly int _buildingId;
    private readonly bool _currentProfile;
    private readonly Lazy<((int StoreyId, double Start, double Height)[] Ranges, int Lowest)> _bottomRanges;

    internal BuildingStoreyResolver(Dictionary<int, int> storeyIds, IReadOnlyDictionary<int, Element> elemById,
        int defaultStoreyId, int buildingId, bool currentProfile)
    {
        _storeyIds = storeyIds;
        _elemById = elemById;
        _defaultStoreyId = defaultStoreyId;
        _buildingId = buildingId;
        _currentProfile = currentProfile;
        _bottomRanges = new(() => BuildBottomRanges(_storeyIds, _elemById));
    }

    /// <summary>
    /// The storey of an element, replaying the storey rule: the base-level
    /// parameter of its kind (a nested family instance reads its top-level instance), then any
    /// stored level reference. <paramref name="outline"/> is the box of an element that has no body of its own.
    /// </summary>
    internal int Resolve(Element elem, out bool hasOffStoreyLevel, double[]? outline = null)
    {
        hasOffStoreyLevel = false;
        // The current profile reads STAIRS_BASE_LEVEL_PARAM, which is StairsElement.m_baseLevelId.
        if (_currentProfile && elem.CategoryId == -2000120 && elem.SerializedStairBaseLevelId is int stairBaseLevelId
            && _storeyIds.TryGetValue(stairBaseLevelId, out int stairBaseStoreyId))
            return stairBaseStoreyId;
        Element parameterSource = elem.SuperInstanceId is > 0 && _elemById.TryGetValue(elem.SuperInstanceId.Value, out var top)
            ? top : elem;
        int[] baseLevelParameters = elem.CategoryId switch
        {
            -2000011 => [-1001107],                 // WALL_BASE_CONSTRAINT
            -2000035 => [-1001651],                 // ROOF_CONSTRAINT_LEVEL_PARAM
            -2000120 => [-1007200],                 // STAIRS_BASE_LEVEL_PARAM
            _ when elem.NativeMasterSymbolId is > 0 => [-1001350, -1001365, -1001383],
            _ => [],
        };
        // An element whose level is a Level that is not a project storey stands in the highest storey at or below
        // that level, in the building where no storey is that low.
        Element? offStoreyLevel = null;
        foreach (int parameterId in baseLevelParameters)
            if (parameterSource.Parameters.FirstOrDefault(parameter => parameter.Id == parameterId)?.Value is int levelId)
            {
                if (_storeyIds.TryGetValue(levelId, out int storeyId)) return storeyId;
                if (_currentProfile && offStoreyLevel == null
                    && _elemById.TryGetValue(levelId, out var baseLevel) && baseLevel.CategoryId == -2000240)
                    offStoreyLevel = baseLevel;
            }
        // A base level that is not a storey outranks the element's own level parameters below, as one that is a
        // storey does. Among the base-level parameters one on a storey still outranks an earlier one that is not.
        if (offStoreyLevel != null)
        {
            hasOffStoreyLevel = true;
            return HighestStoreyAtOrBelow(offStoreyLevel, _storeyIds, _elemById, _buildingId);
        }

        // A column whose base level is a Level that is not a project storey stands in the highest storey at or
        // below that level, and in the building where no storey is that low.
        if (_currentProfile && elem.CategoryId is -2001330 or -2000100
            && parameterSource.Parameters.FirstOrDefault(parameter => parameter.Id == -1001350)?.Value is int columnBaseLevelId
            && _elemById.TryGetValue(columnBaseLevelId, out var columnBaseLevel) && columnBaseLevel.CategoryId == -2000240)
            return _storeyIds.TryGetValue(columnBaseLevelId, out int baseStoreyId)
                ? baseStoreyId
                : HighestStoreyAtOrBelow(columnBaseLevel, _storeyIds, _elemById, _buildingId);

        if (_currentProfile)
        {
            // LEVEL_PARAM, SCHEDULE_LEVEL_PARAM, RBS_START_LEVEL_PARAM, FAMILY_BASE_LEVEL_PARAM,
            // INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM, INSTANCE_REFERENCE_LEVEL_PARAM,
            // SCHEDULE_BASE_LEVEL_PARAM.
            foreach (int parameterId in elem.CategoryId is -2000035 or -2000120
                         ? new[] { -1001952, -1002062, -1114000, -1001350, -1001365, -1001383, -1002063, -1001352 }
                         : new[] { -1001952, -1002062, -1114000, -1001350, -1001365, -1001383, -1002063 })
                if (elem.Parameters.FirstOrDefault(parameter => parameter.Id == parameterId)?.Value is int assocLevelId)
                {
                    if (_storeyIds.TryGetValue(assocLevelId, out int assocStoreyId)) return assocStoreyId;
                    if (offStoreyLevel == null
                        && _elemById.TryGetValue(assocLevelId, out var assocLevel) && assocLevel.CategoryId == -2000240)
                        offStoreyLevel = assocLevel;
                }
            if (offStoreyLevel != null)
            {
                hasOffStoreyLevel = true;
                return HighestStoreyAtOrBelow(offStoreyLevel, _storeyIds, _elemById, _buildingId);
            }
        }
        // Try to find LEVEL_PARAM or BASE_LEVEL_PARAM in element's parameters
        else foreach (var p in elem.Parameters)
        {
            if (p.StorageType == StorageType.ElementId && p.Value is int refId && refId > 0)
            {
                string? pName = p.Definition?.Name;
                if (pName != null && (pName.Contains("LEVEL") || pName.Contains("CONSTRAINT") || pName.Contains("Level")))
                {
                    if (_storeyIds.TryGetValue(refId, out int sid))
                        return sid;
                }
            }
        }

        // No stored level: the highest storey at or below the bottom of the element.
        if (_currentProfile)
            return ResolveByBottom(
                (outline ?? elem.DecodedBox ?? elem.Geometry?.BoundingBox) is { Length: >= 6 } box ? box[2] : null);
        if ((elem.DecodedBox ?? elem.Geometry?.BoundingBox) is { Length: >= 6 } bounds && _storeyIds.Count > 0)
        {
            const double Tolerance = 1e-6;
            int best = _defaultStoreyId;
            double bestElevation = double.NegativeInfinity;
            foreach (var (levelId, storeyId) in _storeyIds)
                if (_elemById.TryGetValue(levelId, out var level))
                {
                    double elevation = GetElevation(level);
                    if (elevation <= bounds[2] + Tolerance && elevation > bestElevation)
                    {
                        best = storeyId;
                        bestElevation = elevation;
                    }
                }
            return best;
        }

        return _defaultStoreyId;
    }

    /// <summary>The storey of the highest project level at or below this level (the later level on a tie), the building where no storey is that low.</summary>
    internal static int HighestStoreyAtOrBelow(Element level, Dictionary<int, int> storeyIds,
        IReadOnlyDictionary<int, Element> elemById, int buildingId)
    {
        const double Tolerance = 1e-6;
        double baseElevation = GetElevation(level);
        int chosen = buildingId;
        double chosenElevation = double.NegativeInfinity;
        int chosenLevelId = 0;
        foreach (var (levelId, storeyId) in storeyIds)
            if (elemById.TryGetValue(levelId, out var storeyLevel))
            {
                double elevation = GetElevation(storeyLevel);
                if (elevation <= baseElevation + Tolerance
                    && (elevation > chosenElevation || elevation == chosenElevation && levelId > chosenLevelId))
                {
                    chosen = storeyId;
                    chosenElevation = elevation;
                    chosenLevelId = levelId;
                }
            }
        return chosen;
    }

    /// <summary>10 cm in feet: the storey rule, how far below a level its storey starts.</summary>
    private const double StoreyExtensionFeet = 10.0 / (12.0 * 2.54);

    private static ((int StoreyId, double Start, double Height)[] Ranges, int Lowest)
        BuildBottomRanges(IReadOnlyDictionary<int, int> storeyIds, IReadOnlyDictionary<int, Element> elemById)
    {
        var levels = storeyIds
            .Where(pair => elemById.ContainsKey(pair.Key))
            .Select(pair => (LevelId: pair.Key, StoreyId: pair.Value, Elevation: GetElevation(elemById[pair.Key])))
            .OrderBy(level => level.Elevation).ThenBy(level => level.LevelId).ToList();
        var ranges = new List<(int LevelId, int StoreyId, double Start, double Height)>();
        for (int i = 0; i < levels.Count; i++)
        {
            double height = 0;
            for (int j = i + 1; j < levels.Count; j++)
                if (Math.Abs(levels[j].Elevation - levels[i].Elevation) > 1e-7)
                {
                    height = levels[j].Elevation - levels[i].Elevation;
                    break;
                }
            ranges.Add((levels[i].LevelId, levels[i].StoreyId, levels[i].Elevation - StoreyExtensionFeet, height));
        }
        ranges.Sort((a, b) => a.LevelId.CompareTo(b.LevelId));
        var storeyRanges = ranges.Select(range => (range.StoreyId, range.Start, range.Height)).ToArray();
        int storeyRangeBottom = 0;
        double lowest = double.MaxValue;
        foreach (var range in storeyRanges)
            if (range.Start < lowest + 1e-8)
            {
                storeyRangeBottom = range.StoreyId;
                lowest = range.Start;
            }
        return (storeyRanges, storeyRangeBottom);
    }

    /// <summary>The placement rule for an element without a stored level.</summary>
    internal int ResolveByBottom(double? bottom)
    {
        int id = _storeyIds.Count > 0 ? ResolveStoreyByBottomCurrent(bottom) : 0;
        return id != 0 ? id : _defaultStoreyId;
    }

    private int ResolveStoreyByBottomCurrent(double? bottom)
    {
        var (ranges, lowest) = _bottomRanges.Value;
        int chosen = 0;
        if (bottom is double z)
            foreach (var (storeyId, start, height) in ranges)
                // Both bounds move by the same tolerance, so no bottom falls between two ranges.
                if (z > start + 1e-9 && (Math.Abs(height) < 1e-10 || z <= start + height + 1e-9))
                    chosen = storeyId;
        return chosen != 0 ? chosen : lowest;
    }

    // A project Level (building story or not) has no owning family, unplaced instance or DB view;
    // loaded-family reference levels carry an exact owner in one of these serialized fields.
    internal static bool IsProjectLevel(Element level) => level.IsNativeLevel
        && level.SerializedFamilyId == -1
        && level.SerializedUnplacedOwnerId == -1
        && level.SerializedOwnerDbViewId == -1;

    internal static List<Element> GetBuildingStoreyLevels(IEnumerable<Element> elements)
    {
        var candidates = elements
            .Where(element => IsProjectLevel(element)
                && !string.IsNullOrWhiteSpace(element.Name)
                && element.Parameters.Any(parameter => parameter.Id == -1007102
                    && parameter.StorageType == StorageType.Double
                    && parameter.Value is double elevation
                    && double.IsFinite(elevation))
                && element.Parameters.Any(parameter => parameter.Id == -1007111
                    && parameter.StorageType == StorageType.Integer
                    && parameter.Value is int isBuildingStory
                    && isBuildingStory == 1))
            .ToList();

        return candidates
            .OrderBy(GetElevation)
            .ThenBy(element => element.Id)
            .ToList();
    }

    internal static double GetElevation(Element level)
    {
        // Look for LEVEL_ELEV parameter
        foreach (var p in level.Parameters)
        {
            string? pName = p.Definition?.Name;
            if (pName != null && pName.Contains("LEVEL_ELEV") && p.StorageType == StorageType.Double && p.Value is double d)
                return d;
        }
        // Fallback: bbox Z
        if (level.DecodedBox != null && level.DecodedBox.Length >= 3)
            return level.DecodedBox[2];
        return 0;
    }
}
