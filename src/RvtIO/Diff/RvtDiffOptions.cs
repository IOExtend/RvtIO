namespace RvtIO.Diff;

/// <summary>Configuration controlling how <see cref="RvtRevisionDiffer"/> matches elements and classifies changes.</summary>
public sealed class RvtDiffOptions
{
    /// <summary>Optional category filter (BuiltInCategory names such as <c>OST_Walls</c>, display names, or integer category IDs).</summary>
    public IReadOnlyList<string>? Categories { get; set; }

    /// <summary>Optional element filter matching <see cref="Model.Element.UniqueId"/> or exact element ID text.</summary>
    public IReadOnlyList<string>? Elements { get; set; }

    /// <summary>Maximum coordinate, bounding-box, length, radius, or layer-thickness delta in Revit internal feet treated as unchanged. Default: <c>1e-4</c> ft (~0.0305 mm).</summary>
    public double LinearToleranceFeet { get; set; } = 1e-4;

    /// <summary>Maximum angular/basis-vector delta (radians / unit-vector component norm) treated as unchanged. Default: <c>1e-5</c>.</summary>
    public double AngleToleranceRadians { get; set; } = 1e-5;

    /// <summary>Maximum face-area delta in square feet treated as unchanged. Default: <c>1e-4</c> sq ft.</summary>
    public double AreaToleranceSquareFeet { get; set; } = 1e-4;

    /// <summary>Maximum numeric double parameter delta in internal units treated as unchanged. Default: <c>1e-6</c>.</summary>
    public double NumericParameterTolerance { get; set; } = 1e-6;

    /// <summary>Compare element parameters (default: true).</summary>
    public bool IncludeParameters { get; set; } = true;

    /// <summary>Compare element types and track type changes back to affected instances (default: true).</summary>
    public bool IncludeTypes { get; set; } = true;

    /// <summary>Compare world-space placement (<c>Location</c>, <c>Transform</c>, <c>TextPosition</c>, <c>ViewportOffset</c>) (default: true).</summary>
    public bool IncludePlacement { get; set; } = true;

    /// <summary>Compare decoded geometry, solids, openings, sketch loops, and compound layers (default: true).</summary>
    public bool IncludeGeometry { get; set; } = true;

    /// <summary>Compare resolved element relationships (host, level, group, room, view, cutters, connectors) (default: true).</summary>
    public bool IncludeRelationships { get; set; } = true;

    /// <summary>Compare document external file links from <c>TransmissionData</c> (default: true).</summary>
    public bool IncludeTransmissionLinks { get; set; } = true;

    /// <summary>Include ambiguous matches and unavailable entries in the report (default: true).</summary>
    public bool IncludeUncertainMatches { get; set; } = true;

    /// <summary>
    /// When false (default), differences limited to display unit formatting or spec presentation where internal values agree
    /// do not mark parameters changed, keeping version/unit-display upgrades separate from semantic changes.
    /// </summary>
    public bool IncludeDisplayFormattingDifferences { get; set; }

    /// <summary>
    /// When true and two documents are verified as revisions of the same project, elements missing a persistent
    /// <see cref="Model.Element.UniqueId"/> may be paired by unique <c>(ElementId, CategoryId)</c> with <see cref="RvtDiffMatchKind.ElementIdFallback"/>.
    /// Default is false so missing persistent identity is surfaced under <c>Unavailable</c> rather than assumed from numeric IDs.
    /// </summary>
    public bool AllowElementIdFallbackWhenRelated { get; set; }

    /// <summary>Scrub personal identifiers, user home segments, and sensitive paths in the resulting report.</summary>
    public bool RedactPersonalData { get; set; }

    /// <summary>Optional reader diagnostics collected when opening the left document externally.</summary>
    public RvtReadDiagnostics? LeftDiagnostics { get; set; }

    /// <summary>Optional reader diagnostics collected when opening the right document externally.</summary>
    public RvtReadDiagnostics? RightDiagnostics { get; set; }

    /// <summary>Optional display label override for the left revision.</summary>
    public string? LeftLabel { get; set; }

    /// <summary>Optional display label override for the right revision.</summary>
    public string? RightLabel { get; set; }

    /// <summary>Optional reader options used when opening the left file path.</summary>
    public RvtReaderOptions? LeftReaderOptions { get; set; }

    /// <summary>Optional reader options used when opening the right file path.</summary>
    public RvtReaderOptions? RightReaderOptions { get; set; }
}
