using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Sheets;

/// <summary>
/// The sheets of a document and the viewports placed on them, extracted from the data a read already decoded. This is
/// the first slice of the sheet presentation model: identity (number, name, issue date, by-lines, scale), the viewports
/// of each sheet and the elements each sheet owns. Page size, view placement, the displayed view, crop regions and all
/// 2D content are not decoded; <see cref="Issues"/> names each limit so a downstream exporter can mark a page
/// incomplete instead of drawing it wrongly.
/// </summary>
/// <remarks>
/// A sheet is an element that carries the <c>Sheet Number</c> or <c>Sheet Name</c> parameter; the reader creates those
/// parameters only for the sheet class of the file (class 995 of the Revit 2022 schema), not for the elements whose
/// category merely says <c>OST_Sheets</c>, of which a project holds thousands (every block whose class word is 0x03E3
/// takes that category). A viewport is an element that carries the <c>Detail Number</c> parameter and references a
/// sheet; its sheet is the first referenced element that is a sheet. Ownership comes from the stored
/// <c>m_ownerDBViewId</c> (<see cref="Element.OwnerViewId"/>).
/// Coordinates, sizes and units are not exposed: no sheet or viewport coordinate is decoded in this release.
/// </remarks>
public sealed class SheetCatalog
{
    /// <summary>The limits of this release, one entry per kind of missing data.</summary>
    private static readonly SheetIssue[] ReleaseIssues =
    [
        new(SheetIssueKind.PageSizeNotDecoded,
            "A sheet stores no page width, height or orientation: Revit keeps them in the title block family, which the "
            + "reader does not decode."),
        new(SheetIssueKind.ViewPlacementNotDecoded,
            "A viewport stores only the origin of its view title label; the position and rotation of the view on the "
            + "sheet are not decoded, so no placed view has a sheet coordinate."),
        new(SheetIssueKind.DisplayedViewNotDecoded,
            "The view a viewport displays is reached through the sheet's viewer element (schema class 621), which the "
            + "reader does not decode; ViewportRecord.DisplayedViewId is null."),
        new(SheetIssueKind.ViewContentNotDecoded,
            "No 2D linework, text, dimension, tag, image or schedule content of a sheet or drawing view is decoded; an "
            + "exporter cannot draw page content from this release."),
        new(SheetIssueKind.CropRegionNotDecoded,
            "The crop region of a drawing view is not decoded (no stored crop field is read)."),
    ];

    private SheetCatalog(IReadOnlyList<SheetRecord> sheets, IReadOnlyList<ViewportRecord> viewports)
    {
        Sheets = sheets;
        Viewports = viewports;
    }

    /// <summary>Create a catalog from sheets and viewports (used by <c>--where</c> filtering).</summary>
    public static SheetCatalog FromFiltered(IReadOnlyList<SheetRecord> sheets, IReadOnlyList<ViewportRecord> viewports) =>
        new(sheets, viewports);

    /// <summary>The sheets of the document, ordered by number (empty numbers last) and then by element id.</summary>
    public IReadOnlyList<SheetRecord> Sheets { get; }

    /// <summary>The viewports placed on the sheets of <see cref="Sheets"/>, ordered by element id.</summary>
    public IReadOnlyList<ViewportRecord> Viewports { get; }

    /// <summary>What this release does not decode, one entry per kind. Never empty.</summary>
    public IReadOnlyList<SheetIssue> Issues { get; } = ReleaseIssues;

    /// <summary>Gets the sheet catalog of an opened document. Nothing is read from the file again.</summary>
    /// <param name="document">The document to read the sheets from.</param>
    public static SheetCatalog Extract(RvtDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Extract(document.RawData);
    }

    /// <summary>Gets the sheet catalog of a read file.</summary>
    /// <param name="info">The file information a read returned.</param>
    public static SheetCatalog Extract(RevitFileInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var sheetElements = new List<Element>();
        foreach (var element in info.Elements)
        {
            if (!Compact.CompactUniverse.IsTableRow(element, info.ElemTableIsExact)) continue;
            if (element.LookupParameter(BuiltInParameter.SHEET_NUMBER) == null
                && element.LookupParameter(BuiltInParameter.SHEET_NAME) == null) continue;
            sheetElements.Add(element);
        }

        // The sheet's own number orders the pages; a sheet without one keeps document order behind the numbered sheets.
        sheetElements.Sort((left, right) =>
        {
            string? leftNumber = Text(left, BuiltInParameter.SHEET_NUMBER);
            string? rightNumber = Text(right, BuiltInParameter.SHEET_NUMBER);
            if (leftNumber == null || rightNumber == null)
            {
                if (leftNumber != null) return -1;
                if (rightNumber != null) return 1;
            }
            else
            {
                int order = StringComparer.OrdinalIgnoreCase.Compare(leftNumber, rightNumber);
                if (order != 0) return order;
            }
            return left.Id.CompareTo(right.Id);
        });

        var sheetIds = new HashSet<int>();
        foreach (var sheet in sheetElements) sheetIds.Add(sheet.Id);

        var owned = new Dictionary<int, List<int>>();
        foreach (var element in info.Elements)
        {
            if (!Compact.CompactUniverse.IsTableRow(element, info.ElemTableIsExact)) continue;
            if (element.OwnerViewId is not int owner || !sheetIds.Contains(owner) || owner == element.Id) continue;
            if (!owned.TryGetValue(owner, out var ids)) owned[owner] = ids = new List<int>();
            ids.Add(element.Id);
        }

        var viewports = new List<ViewportRecord>();
        var viewportsBySheet = new Dictionary<int, List<int>>();
        foreach (var element in info.Elements)
        {
            if (!Compact.CompactUniverse.IsTableRow(element, info.ElemTableIsExact)) continue;
            if (element.LookupParameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER) == null) continue;
            if (element.References is not { Count: > 0 } references) continue;
            int sheetId = 0;
            foreach (int reference in references)
            {
                if (reference != element.Id && sheetIds.Contains(reference)) { sheetId = reference; break; }
            }
            if (sheetId == 0) continue;
            viewports.Add(new ViewportRecord(element.Id, sheetId, Text(element, BuiltInParameter.VIEWPORT_DETAIL_NUMBER)));
            if (!viewportsBySheet.TryGetValue(sheetId, out var ids)) viewportsBySheet[sheetId] = ids = new List<int>();
            ids.Add(element.Id);
        }
        viewports.Sort((left, right) => left.ElementId.CompareTo(right.ElementId));

        var sheets = new List<SheetRecord>(sheetElements.Count);
        foreach (var element in sheetElements)
        {
            var (parameters, yesNo) = ExtraParameters(element, info.UserParameters);
            sheets.Add(new SheetRecord(
                element.Id,
                Text(element, BuiltInParameter.SHEET_NUMBER),
                Text(element, BuiltInParameter.SHEET_NAME),
                Text(element, BuiltInParameter.SHEET_ISSUE_DATE),
                Integer(element, BuiltInParameter.VIEW_SCALE),
                Text(element, BuiltInParameter.SHEET_DRAWN_BY),
                Text(element, BuiltInParameter.SHEET_DESIGNED_BY),
                Text(element, BuiltInParameter.SHEET_CHECKED_BY),
                Text(element, BuiltInParameter.SHEET_APPROVED_BY),
                Ordered(viewportsBySheet, element.Id),
                Ordered(owned, element.Id),
                parameters,
                yesNo));
        }

        return new SheetCatalog(sheets, viewports);
    }

    /// <summary>
    /// The sheet's project and shared parameters as display strings (rvt-prt's <c>list --params</c>):
    /// text and whole-number user parameters (positive definition ids) with a value; numbers with units
    /// are left out until their units are read. Unset sentinels (<c>HasRevitValue == false</c>) are skipped.
    /// Built-in parameters other than the sheet's own fields (such as category or partition ids) are not
    /// sheet parameters and stay out. Also returns the display names of the Yes/No parameters among them:
    /// a user parameter whose definition is a Yes/No kind, whose Forge spec is boolean, or whose stored
    /// value is boolean, reads as <c>Yes</c>/<c>No</c>, as does <c>Appears In Sheet List</c>.
    /// </summary>
    private static (IReadOnlyDictionary<string, string> Parameters, IReadOnlyCollection<string> YesNo)
        ExtraParameters(Element element, UserParameterCatalog? catalog)
    {
        var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var yesNo = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in element.Parameters)
        {
            if (parameter.Definition == null || !parameter.HasRevitValue) continue;
            int id = parameter.Definition.Id;
            if (id == (int)BuiltInParameter.SHEET_NUMBER
                || id == (int)BuiltInParameter.SHEET_NAME
                || id == (int)BuiltInParameter.SHEET_ISSUE_DATE
                || id == (int)BuiltInParameter.VIEW_SCALE
                || id == (int)BuiltInParameter.SHEET_DRAWN_BY
                || id == (int)BuiltInParameter.SHEET_DESIGNED_BY
                || id == (int)BuiltInParameter.SHEET_CHECKED_BY
                || id == (int)BuiltInParameter.SHEET_APPROVED_BY)
                continue;
            if (id < 0) continue;
            string? key = parameter.Definition.Name;
            if (string.IsNullOrWhiteSpace(key)) continue;
            bool knownYesNo = IsYesNoParameter(parameter, id, catalog);
            string? value = DisplayParameterValue(parameter, knownYesNo);
            if (value == null) continue;
            // First occurrence wins, matching Element.LookupParameter semantics.
            if (result.TryAdd(key, value) && knownYesNo) yesNo.Add(key);
        }
        // "Appears In Sheet List" under its Revit display name for --where compatibility;
        // the BIP enum name still resolves through SheetFilter.Lookup.
        if (element.LookupParameter(BuiltInParameter.SHEET_SCHEDULED) is { } scheduled
            && scheduled.HasRevitValue)
        {
            string? scheduledText = scheduled.Value switch
            {
                int scheduledInt => scheduledInt != 0 ? "Yes" : "No",
                bool scheduledBool => scheduledBool ? "Yes" : "No",
                _ => null,
            };
            if (scheduledText != null
                && result.TryAdd(SheetFilter.DisplayName(BuiltInParameter.SHEET_SCHEDULED), scheduledText))
                yesNo.Add(SheetFilter.DisplayName(BuiltInParameter.SHEET_SCHEDULED));
        }
        return (result, yesNo);
    }

    /// <summary>True when the parameter is known to be Yes/No: its definition element is a Yes/No kind,
    /// its Forge spec is boolean, or its stored value is boolean.</summary>
    private static bool IsYesNoParameter(Parameter parameter, int id, Parsing.UserParameterCatalog? catalog)
    {
        if (parameter.Value is bool) return true;
        if (parameter.SpecType?.Contains("bool", StringComparison.OrdinalIgnoreCase) == true) return true;
        return catalog?.Definitions.TryGetValue(id, out var definition) == true && definition.IsYesNo;
    }

    /// <summary>Display text for a sheet parameter: strings as stored, integers as numbers,
    /// Yes/No parameters as <c>Yes</c>/<c>No</c>.</summary>
    private static string? DisplayParameterValue(Parameter parameter, bool knownYesNo)
    {
        if (parameter.Value == null) return null;
        if (parameter.Value is bool boolean) return boolean ? "Yes" : "No";
        return parameter.StorageType switch
        {
            StorageType.String => parameter.Value as string,
            StorageType.Integer => parameter.Value is int i
                ? knownYesNo ? (i != 0 ? "Yes" : "No") : i.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null,
            _ => null,
        };
    }

    private static IReadOnlyList<int> Ordered(Dictionary<int, List<int>> byOwner, int owner)
    {
        if (!byOwner.TryGetValue(owner, out var ids) || ids.Count == 0) return [];
        ids.Sort();
        return ids;
    }

    /// <summary>A string parameter as stored; null when the element holds none or the value is empty.</summary>
    private static string? Text(Element element, BuiltInParameter parameter) =>
        element.LookupParameter(parameter)?.Value is string value && value.Length > 0 ? value : null;

    /// <summary>An integer parameter as stored; null when the element holds none or the value is not an integer.</summary>
    private static int? Integer(Element element, BuiltInParameter parameter) =>
        element.LookupParameter(parameter)?.Value is int value ? value : null;
}
