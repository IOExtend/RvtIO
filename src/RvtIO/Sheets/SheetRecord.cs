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
        IReadOnlyList<int> ownedElementIds,
        IReadOnlyDictionary<string, string>? parameters = null,
        IReadOnlyCollection<string>? yesNoParameters = null)
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
        Parameters = parameters ?? new Dictionary<string, string>();
        YesNoParameters = yesNoParameters ?? [];
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

    /// <summary>The sheet's project and shared parameters as display strings, keyed by display name
    /// (the <c>Definition.Name</c> Revit shows). Only text and whole numbers are included; numbers with
    /// units are left out until their units are read. A Yes/No parameter reads as <c>Yes</c>/<c>No</c>.
    /// The 8 core fields above are not repeated here.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; }

    /// <summary>The display names of the entries of <see cref="Parameters"/> (and the
    /// <c>Appears In Sheet List</c> value) that are Yes/No parameters. Only for these do the
    /// <c>true</c>/<c>false</c>/<c>1</c>/<c>0</c> spellings match <c>Yes</c>/<c>No</c>; every other
    /// field compares literally.</summary>
    public IReadOnlyCollection<string> YesNoParameters { get; }
}
