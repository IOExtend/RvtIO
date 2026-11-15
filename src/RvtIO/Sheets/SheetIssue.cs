namespace RvtIO.Sheets;

/// <summary>What a sheet catalog cannot provide in this release. The kind is stable; the message explains the limit.</summary>
public enum SheetIssueKind
{
    /// <summary>The sheet stores no page width, height or orientation.</summary>
    PageSizeNotDecoded,
    /// <summary>A placed view has no position on the sheet.</summary>
    ViewPlacementNotDecoded,
    /// <summary>A viewport does not name the view it displays.</summary>
    DisplayedViewNotDecoded,
    /// <summary>No 2D linework, text, dimension or raster content of a sheet is available.</summary>
    ViewContentNotDecoded,
    /// <summary>The crop region of a drawing view is not available.</summary>
    CropRegionNotDecoded,
}

/// <summary>One limit of <see cref="SheetCatalog"/>: the kind of data that is missing and why. A downstream exporter
/// reads the issues to mark a page incomplete instead of drawing a sheet it cannot fill.</summary>
/// <param name="Kind">The kind of missing data.</param>
/// <param name="Message">A one-sentence explanation of the limit and of the stored data it comes from.</param>
public sealed record SheetIssue(SheetIssueKind Kind, string Message);
