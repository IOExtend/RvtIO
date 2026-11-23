namespace RvtIO;

/// <summary>A progress update from a Revit file read.</summary>
/// <param name="Phase">The machine-readable name of the current reader phase.</param>
/// <param name="Completed">Completed units of work within the phase.</param>
/// <param name="Total">Units of work in that phase, or zero when no count is available.</param>
public readonly record struct RvtReadProgress(string Phase, long Completed = 0, long Total = 0);
