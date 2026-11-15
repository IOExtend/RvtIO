namespace RvtIO.Sheets;

/// <summary>A sheet of the document (the Revit API's <c>ViewSheet</c>) with the data this release decodes: its number,
/// name, issue date and by-lines, its scale, its viewports and the elements the sheet owns. Page size, the views the
/// viewports display and all 2D content are not decoded; <see cref="SheetCatalog.Issues"/> lists them.</summary>
public sealed class SheetRecord
{
    internal SheetRecord(
        int elementId,
        string? number,
        string? name,
        string? issueDate,
        int? scale,
        string? drawnBy,
        string? designedBy,
        string? checkedBy,
        string? approvedBy,
        IReadOnlyList<int> viewportIds,
        IReadOnlyList<int> ownedElementIds)
    {
        ElementId = elementId;
        Number = number;
        Name = name;
        IssueDate = issueDate;
        Scale = scale;
        DrawnBy = drawnBy;
        DesignedBy = designedBy;
        CheckedBy = checkedBy;
        ApprovedBy = approvedBy;
        ViewportIds = viewportIds;
        OwnedElementIds = ownedElementIds;
    }

    /// <summary>The element id of the sheet.</summary>
    public int ElementId { get; }

    /// <summary>The sheet number (the <c>Sheet Number</c> parameter) as stored, null when it is empty. "???" is a
    /// value a project stores, not a decode failure.</summary>
    public string? Number { get; }

    /// <summary>The sheet name (the <c>Sheet Name</c> parameter) as stored, null when it is empty. "???" is a value a
    /// project stores, not a decode failure.</summary>
    public string? Name { get; }

    /// <summary>The issue date (the <c>Sheet Issue Date</c> parameter) as stored, null when it is empty.</summary>
    public string? IssueDate { get; }

    /// <summary>The view scale of the sheet as the denominator of 1:N (the <c>View Scale</c> parameter, 1 for a
    /// sheet), null when the file stores none.</summary>
    public int? Scale { get; }

    /// <summary>The <c>Drawn By</c> field of the sheet, null when it is empty.</summary>
    public string? DrawnBy { get; }

    /// <summary>The <c>Designed By</c> field of the sheet, null when it is empty.</summary>
    public string? DesignedBy { get; }

    /// <summary>The <c>Checked By</c> field of the sheet, null when it is empty.</summary>
    public string? CheckedBy { get; }

    /// <summary>The <c>Approved By</c> field of the sheet, null when it is empty.</summary>
    public string? ApprovedBy { get; }

    /// <summary>The element ids of the viewports placed on this sheet, ascending.</summary>
    public IReadOnlyList<int> ViewportIds { get; }

    /// <summary>The element ids of the elements whose <see cref="Model.Element.OwnerViewId"/> is this sheet (annotations,
    /// view-specific graphics and other content placed on the sheet), ascending. Empty when the file decoded no owner
    /// view for its content.</summary>
    public IReadOnlyList<int> OwnedElementIds { get; }
}
