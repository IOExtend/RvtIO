namespace RvtIO;

/// <summary>Configuration for RVT file reading.</summary>
public class RvtReaderOptions
{
    /// <summary>Path to an optional text dump of the model for CompoundStructure layer extraction. Null to skip.</summary>
    public string? ExternalDumpPath { get; set; }

    /// <summary>Regular expression for the prefix that class names in the external dump carry in front of the class name proper.
    /// Null uses the default shape (capital, lowercase, capital, lowercase, followed by a capital).</summary>
    public string? ExternalDumpClassPrefix { get; set; }

    /// <summary>Ignored: the localized system family names are built into the library. Kept so that existing callers still compile.</summary>
    [Obsolete("Ignored: texts are built in.")]
    public string? RevitLocalizationDirectory { get; set; }

    /// <summary>Locale code (such as "ENU" or "RUS") of the built-in texts that name system families, instead
    /// of the locale the file was saved in. The current profile names them in English whatever the file's locale. Null keeps
    /// the saved locale.</summary>
    public string? SystemFamilyNameLocale { get; set; }

    /// <summary>Keep two faces that share their outline but not their openings as two faces (the decoded geometry merges faces by the
    /// corners of their outline only). The current profile has them as two faces: the cap of a window frame and the cap of the glazing bar behind it
    /// are one rectangle with different holes. Off by default, which keeps the geometry the legacy profile export is built on.</summary>
    public bool SeparateFacesWithDistinctOpenings { get; set; }

    /// <summary>Rewrite the partition streams of a file that is read by its own class schema into the Revit 2022 layout, so that the
    /// geometry and element readers written for 2022 see them (see <see cref="Parsing.R22Transcoder"/>). The reader keeps the layout it
    /// ships only for a file of Revit 2022 (or of an unknown release); any other file whose own schema it can read is read by that
    /// schema, and its element geometry is decoded only with this option (measured on samples of Revit 2020 and 2025; the Revit 2022
    /// samples decode the same either way). Off by default; the command line tool turns it on for an IFC export.</summary>
    public bool R22Layout { get; set; }

    /// <summary>When true and reading with R22Layout on a 64-bit wide-schema file, also decodes and preserves
    /// the wide-schema model in <see cref="RvtFileReader.WideModel"/> for multi-output exports without a second file read.</summary>
    public bool PreserveWideLayout { get; set; }

    /// <summary>When several partition streams hold a copy of the same element, keep the current copy named by the file's document
    /// increment table and drop the superseded ones before decoding (see <see cref="Parsing.RecordRouting"/>). On by default; set it to false
    /// to decode every copy, as releases before this default did. A file with one partition stream is read the same either way.</summary>
    public bool ResolveCurrentCopies { get; set; } = true;

    /// <summary>Raise the limits that cut a stream while it is recovered from damage: the output of the element table recovery
    /// (256 MiB) and its loop guard (4096 iterations) become the platform array length and unbounded.
    /// For very large models. Off by default.</summary>
    public bool RaiseLimits { get; set; }

    /// <summary>Collects the streams read, how much of each decoded, and the limits that cut a stream. Null to collect nothing.</summary>
    public RvtReadDiagnostics? Diagnostics { get; set; }

    /// <summary>Receives phase and work-count updates from <see cref="RvtFileReader.Read"/> in the order the phases run.
    /// The reader calls <see cref="IProgress{T}.Report"/> synchronously on the thread that calls <c>Read</c>, one call at a time, and
    /// does not catch an exception the observer throws: it ends the read. <see cref="Progress{T}"/> instead posts each update to the
    /// <see cref="System.Threading.SynchronizationContext"/> it captured when it was created, or to the thread pool when there is none, so
    /// its handler can run after <c>Read</c> has returned and, on the thread pool, out of order. Implement
    /// <see cref="IProgress{T}"/> to see the updates in order on the reading thread.</summary>
    public IProgress<RvtReadProgress>? Progress { get; set; }

    /// <summary>Write diagnostic/progress messages to this TextWriter. Null for silent operation.</summary>
    public TextWriter? DiagnosticOutput { get; set; }
}
