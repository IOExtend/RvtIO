using RvtIO.Parsing;
using static RvtIO.Writing.UserParameterWriter;

namespace RvtIO.Writing;

internal static class ImportedImageReference
{
    internal const string NoneName = "<None>";

    internal static string? CaptureKey(SchemaClass declaring, SchemaField field) => (declaring.Name, field.Name) switch
    {
        ("ImageSymbol", "m_idImageHolder" or "m_flags" or "m_viewId" or "m_ownerSymbolId" or "m_widthInPixels" or "m_heightInPixels") or
        ("ImageHolder", "m_pRasterImage" or "m_idImageSymbol" or "m_flags") or
        ("ARasterImage", "m_compressedImage" or "m_width" or "m_height") or
        ("Symbol", "m_symbolInfo") or ("SymbolInfo", "m_name") => declaring.Name + "." + field.Name,
        _ => null,
    };

    internal static long ReadId(string value) => value == "-1" ? -1 : ReferenceId(value)
        ?? throw new RvtWriteRefusedException("invalid-parameter-value", "Expected a positive element ID or -1 for no image.");

    internal static string? ReadName(WideBlockWalker walker, IReadOnlyDictionary<long, WideBlockWalker.Walk?> images, long id)
    {
        if (id == -1)
            return NoneName;
        if (images.GetValueOrDefault(id) is not { Exact: true, EmptyEntityAt: < 0 } symbol || Integer(symbol, "m_id") != id
            || Integer(symbol, "ImageSymbol.m_idImageHolder") is not (> 0 and long holderId)
            || Integer(symbol, "ImageSymbol.m_flags") != 0 || Integer(symbol, "ImageSymbol.m_viewId") != -1
            || Integer(symbol, "ImageSymbol.m_ownerSymbolId") != -1
            || Integer(symbol, "ImageSymbol.m_widthInPixels") is not (> 0 and long width)
            || Integer(symbol, "ImageSymbol.m_heightInPixels") is not (> 0 and long height)
            || images.GetValueOrDefault(holderId) is not { Exact: true, EmptyEntityAt: < 0 } holder || Integer(holder, "m_id") != holderId
            || Integer(holder, "ImageHolder.m_idImageSymbol") != id || Integer(holder, "ImageHolder.m_flags") != 0
            || PointerSlot(walker, holder, "ImageHolder.m_pRasterImage", "ARasterImage") is not int raster
            || Integer(holder, "ARasterImage.m_width", raster) != width || Integer(holder, "ARasterImage.m_height", raster) != height
            || Integer(holder, "ARasterImage.m_compressedImage#", raster) is not > 0
            || PointerSlot(walker, symbol, "Symbol.m_symbolInfo", "SymbolInfo") is not int info)
            return null;
        return symbol.Captures.Where(c => c.QueueIndex == info && c.Key == "SymbolInfo.m_name").ToArray()
            is [{ Text: { Length: > 0 } name }] ? name : null;
    }

    internal static int? PointerSlot(WideBlockWalker walker, WideBlockWalker.Walk walk, string field, string type) =>
        walk.Captures.Where(c => c.AtRoot && c.Key == field && c.Kind == WideBlockWalker.CaptureKind.PointerClass).ToArray()
            is [{ Integer: > 0 and long tag, QueuePosition: >= 0 and int slot }]
            && slot < walk.Queue.Count && walk.Queue[slot] == tag && walker.Class((int)tag)?.Name == type ? slot : null;
}
