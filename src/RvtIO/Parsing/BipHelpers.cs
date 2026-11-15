using RvtIO.Lookup;
using RvtIO.Model;

namespace RvtIO.Parsing;

internal static class BipHelpers
{
    public static Parameter MakeBipParam(int bipId, string bipName, StorageType storageType, object value, bool hasNoRevitValue = false) => new()
    {
        Id = bipId,
        StorageType = storageType,
        Value = value,
        HasNoRevitValue = hasNoRevitValue,
        IsReadOnly = IsBipReadOnly(bipName),
        Definition = new Definition { Id = bipId, BuiltInParameter = bipName, Name = bipName },
    };

    public static StorageType BipStorageType(string? bipName)
    {
        if (string.IsNullOrEmpty(bipName)) return StorageType.None;
        string n = bipName.ToUpperInvariant();
        if (n.EndsWith("_PARAM") && (n.Contains("_TYPE_") || n.Contains("_FAMILY_") ||
                n.Contains("_SYMBOL_") || n.Contains("_LEVEL_") || n.Contains("_PHASE_") ||
                n.Contains("_VIEW_") || n.Contains("_ROOM_") || n.Contains("_SPACE_") ||
                n.Contains("_MARK_") || n.Contains("_DESIGN_")))
            return StorageType.ElementId;
        if (n.EndsWith("_LEVEL_PARAM") || n.EndsWith("_TYPE_PARAM") ||
            n.EndsWith("_FAMILY_PARAM") || n.EndsWith("_PHASE_PARAM") ||
            n.Contains("_ASSOC_LEVEL") || n.EndsWith("_ID_PARAM"))
            return StorageType.ElementId;
        if ((n.Contains("_HEIGHT") && !n.Contains("_HEIGHT_TYPE")) || n.Contains("_LENGTH") || n.Contains("_WIDTH") ||
            n.Contains("_DEPTH") || n.Contains("_AREA") || n.Contains("_VOLUME") ||
            n.Contains("_WEIGHT") || n.Contains("_ANGLE") || n.Contains("_SLOPE") ||
            n.Contains("_THICKNESS") || n.Contains("_OFFSET") || n.Contains("_RADIUS") ||
            n.Contains("_DISTANCE") || n.Contains("_ELEVATION") || n.Contains("_SPAN") ||
            n.Contains("_PERIMETER") || n.Contains("_COMPUTED") || n.Contains("_COST"))
            return StorageType.Double;
        if (n.Contains("_NAME") || n.Contains("_DESCRIPTION") || n.Contains("_MARK") ||
            n.Contains("_COMMENTS") || n.Contains("_URL") || n.Contains("_CODE") ||
            n.Contains("_LABEL") || n.Contains("_TAG") || n.Contains("_MANUFACTURER") ||
            n.Contains("_MODEL") || n.Contains("_HYPERLINK") || n.Contains("_IMAGE") ||
            n.Contains("_TITLE") || n.Contains("_KEYNOTE") || n.Contains("_NUMBER") ||
            n.StartsWith("ALL_MODEL_"))
            return StorageType.String;
        return StorageType.Integer;
    }

    public static bool IsPossibleBipKey(int key) =>
        key < -1 && key > -1_700_000 && (BipNames.Lookup.ContainsKey(key)
            || (key > -1_100_000 && key < -1_000_000)
            || (key > -1_620_000 && key < -1_600_000));

    public static bool IsBipReadOnly(string? bipName)
    {
        if (string.IsNullOrEmpty(bipName)) return true;
        string n = bipName.ToUpperInvariant();
        if (n.Contains("_COMMENTS") || n.Contains("_MARK") || n == "ALL_MODEL_MARK"
            || n.Contains("_USER_") || n.Contains("_BASE_OFFSET") || n.Contains("_TOP_OFFSET")
            || n == "WALL_USER_HEIGHT_PARAM" || n.Contains("_DESCRIPTION")
            || n.Contains("_URL") || n.Contains("_KEYNOTE") || n.Contains("_MODEL")
            || n.Contains("_MANUFACTURER") || n.Contains("_COST"))
            return false;
        if (n.Contains("_COMPUTED") || n.Contains("_VOLUME") || n.Contains("_AREA")
            || n.Contains("ELEM_TYPE_PARAM") || n.Contains("ELEM_FAMILY_PARAM")
            || n.Contains("SYMBOL_ID_PARAM") || n.Contains("ELEM_FAMILY_AND_TYPE")
            || n.Contains("ELEM_TYPE_LABEL") || n.Contains("_CUT_LENGTH"))
            return true;
        return true;
    }

    public static Guid? ParseGuidFromForgeId(string? forgeId)
    {
        if (forgeId == null) return null;
        int colonIdx = forgeId.LastIndexOf(".-1:");
        if (colonIdx < 0) return null;
        int start = colonIdx + 4;
        if (start >= forgeId.Length) return null;
        if (start + 36 <= forgeId.Length && Guid.TryParse(forgeId.Substring(start, 36), out var g36)) return g36;
        if (start + 32 <= forgeId.Length && Guid.TryParse(forgeId.Substring(start, 32), out var g32)) return g32;
        return null;
    }
}
