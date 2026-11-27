using System.Text.Json.Serialization;

namespace RvtIO.Sheets;

/// <summary>A placed view: a viewport of a sheet (the Revit API's <c>Viewport</c>), as stored in the document. The
/// viewport carries its sheet and its detail number; the view it displays and its position on the sheet are not
/// decoded in this release (see <see cref="SheetIssueKind.DisplayedViewNotDecoded"/> and
/// <see cref="SheetIssueKind.ViewPlacementNotDecoded"/>).</summary>
public sealed class ViewportRecord
{
    internal ViewportRecord(int elementId, int sheetId, string? detailNumber,
        string? sourceElementId = null, string? sourceSheetId = null)
    {
        ElementId = elementId;
        SheetId = sheetId;
        DetailNumber = detailNumber;
        SourceElementId = sourceElementId;
        SourceSheetId = sourceSheetId;
    }

    /// <summary>The document-local element id of the viewport.</summary>
    public int ElementId { get; }

    /// <summary>Lossless decimal text of the original 64-bit Revit element ID when the file was renumbered to fit <see cref="ElementId"/>; null when unchanged.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceElementId { get; }

    /// <summary>The document-local element id of the sheet the viewport is placed on, resolved from the elements the viewport
    /// references (the sheet is one of them).</summary>
    public int SheetId { get; }

    /// <summary>Lossless decimal text of the sheet's original 64-bit Revit element ID when renumbered to fit <see cref="SheetId"/>; null when unchanged.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceSheetId { get; }

    /// <summary>The detail number of the viewport (the <c>Detail Number</c> parameter), null when it is empty.</summary>
    public string? DetailNumber { get; }

    /// <summary>The element id of the view this viewport displays. Null in this release: the link runs through the
    /// sheet's viewer element (schema class 621), which the reader does not decode (see
    /// <see cref="SheetIssueKind.DisplayedViewNotDecoded"/>).</summary>
    public int? DisplayedViewId => null;
}
