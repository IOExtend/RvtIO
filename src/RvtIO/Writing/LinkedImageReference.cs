using RvtIO.Parsing;
using static RvtIO.Writing.UserParameterWriter;

namespace RvtIO.Writing;

internal static class LinkedImageReference
{
    internal sealed record Location(string Path, string PathType);

    private const string TextPair = "std::pair< AString, AString >";
    private const string ResourceCell = "ExternalResourceReferenceCell";

    internal static string? CaptureKey(SchemaClass declaring, SchemaField field) => (declaring.Name, field.Name) switch
    {
        (ResourceCell, "m_externalResourceReferences" or "m_externalResourceReferencesExpanded") or
        ("ExternalRefIdMap", "m_refs") or ("ExternalResourceRef", "m_referenceInformation") or
        (TextPair, "first" or "second") => WideBlockWalker.AllItemsKeyPrefix + declaring.Name + "." + field.Name,
        _ => null,
    };

    internal static bool IsVerifiedTarget(WideBlockWalker walker, IReadOnlyDictionary<long, WideBlockWalker.Walk?> images, long id)
    {
        return images.GetValueOrDefault(id) is { } symbol && Integer(symbol, "m_id") == id
            && Integer(symbol, "ImageSymbol.m_viewId") == -1 && Integer(symbol, "ImageSymbol.m_ownerSymbolId") == -1
            && Integer(symbol, "ImageSymbol.m_idImageHolder") is > 0 and long holderId
            && images.GetValueOrDefault(holderId) is { Exact: true, EmptyEntityAt: < 0 } holder
            && Integer(holder, "m_id") == holderId && Integer(holder, "ImageHolder.m_idImageSymbol") == id
            && Integer(holder, "ImageHolder.m_flags") == 1 && Integer(holder, "ImageHolder.m_pRasterImage") == 0
            && ReadLocation(walker, symbol) is not null;
    }

    internal static Location? ReadLocation(WideBlockWalker walker, WideBlockWalker.Walk symbol)
    {
        if (!symbol.Exact || symbol.EmptyEntityAt >= 0 || Integer(symbol, "ImageSymbol.m_flags") != 4
            || symbol.Queue.Select((tag, index) => (tag, index)).Where(x => walker.Class(x.tag)?.Name == ResourceCell)
                .ToArray() is not [{ index: var slot }])
            return null;

        const string prefix = WideBlockWalker.AllItemsKeyPrefix;
        var maps = (Integer(symbol, prefix + ResourceCell + ".m_externalResourceReferences#", slot),
            Integer(symbol, prefix + ResourceCell + ".m_externalResourceReferencesExpanded#", slot),
            Integer(symbol, prefix + "ExternalRefIdMap.m_refs#", slot));
        if (maps is not ((1, null, null) or (0, 1, 1))
            || Integer(symbol, prefix + "ExternalResourceRef.m_referenceInformation#", slot) is not (> 0 and long count))
            return null;

        var fields = symbol.Captures.Where(x => x.QueueIndex == slot && x.DeclaringClass == TextPair).ToArray();
        if (fields.Length % 2 != 0 || fields.Length / 2 != count)
            return null;

        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < fields.Length; i += 2)
        {
            if (fields[i] is not { Field: "first", Text: { Length: > 0 } key }
                || fields[i + 1] is not { Field: "second", Text: { } value } || !entries.TryAdd(key, value))
                return null;
        }
        if (!entries.TryGetValue("Path", out string? path) || string.IsNullOrWhiteSpace(path) || path.Contains('\0')
            || !entries.TryGetValue("PathType", out string? pathType))
            return null;
        return pathType switch
        {
            "Absolute" when IsAbsoluteFilePath(path) => new(path, pathType),
            "Relative" or "Relative to Central Model" when !Path.IsPathRooted(path)
                && !path.StartsWith('\\') && !path.StartsWith('/') && !path.Contains(':') => new(path, pathType),
            _ => null,
        };
    }

    private static bool IsAbsoluteFilePath(string path) => Path.IsPathFullyQualified(path)
        || path.StartsWith("\\\\", StringComparison.Ordinal)
        || path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';
}
