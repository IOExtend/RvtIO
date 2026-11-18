using RvtIO.Model;

namespace RvtIO.Compact;

internal static class CompactUniverse
{
    /// <summary>
    /// The row rule IfcExporter applies to its products: a record that only lives in a block is no row, and neither is a record
    /// found outside an ElemTable that was decoded in full.
    /// </summary>
    internal static bool IsTableRow(Element element, bool elemTableIsExact)
        => !element.IsBlockOnly && !(elemTableIsExact && element.IsDiscovered);

    /// <summary>The table rows of <paramref name="info"/> ordered by ElementId; elements with the same id keep their order.</summary>
    internal static List<Element> Select(RevitFileInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        bool exact = info.ElemTableIsExact;
        return info.Elements.Where(element => IsTableRow(element, exact)).OrderBy(element => element.Id).ToList();
    }
}
