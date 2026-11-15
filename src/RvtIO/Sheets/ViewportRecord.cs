namespace RvtIO.Sheets;

/// <summary>A placed view: a viewport of a sheet (the Revit API's <c>Viewport</c>), as stored in the document. The
/// viewport carries its sheet and its detail number; the view it displays and its position on the sheet are not
/// decoded in this release (see <see cref="SheetIssueKind.DisplayedViewNotDecoded"/> and
/// <see cref="SheetIssueKind.ViewPlacementNotDecoded"/>).</summary>
public sealed class ViewportRecord
{
    internal ViewportRecord(int elementId, int sheetId, string? detailNumber)
    {
        ElementId = elementId;
        SheetId = sheetId;
        DetailNumber = detailNumber;
    }

    /// <summary>The element id of the viewport.</summary>
    public int ElementId { get; }

    /// <summary>The element id of the sheet the viewport is placed on, resolved from the elements the viewport
    /// references (the sheet is one of them).</summary>
    public int SheetId { get; }

    /// <summary>The detail number of the viewport (the <c>Detail Number</c> parameter), null when it is empty.</summary>
    public string? DetailNumber { get; }

    /// <summary>The element id of the view this viewport displays. Null in this release: the link runs through the
    /// sheet's viewer element (schema class 621), which the reader does not decode (see
    /// <see cref="SheetIssueKind.DisplayedViewNotDecoded"/>).</summary>
    public int? DisplayedViewId => null;
}
