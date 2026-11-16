namespace RvtIO.Parsing;

/// <summary>
/// The Revit release whose class schema a file carries, and the release that decides layouts and row sizes.
///
/// A project saved by a newer release can keep the schema of an older release it passed through, so the year written to
/// BasicFileInfo (the saving release) is not always the release of the stored layouts. The schema release is read from the
/// file's own Formats/Latest: the class id of the framing marker <c>ContentMarker</c> and the number of classes are both fixed per
/// release. When both are known they must agree, otherwise the release is unknown (null) and nothing is guessed.
/// </summary>
internal static class SchemaRelease
{
    // ContentMarker class id -> release (the marker classes are looked up by name, their numbers belong to the release)
    private static readonly Dictionary<int, int> ByMarker = new()
    {
        [779] = 2020, [801] = 2021, [817] = 2022, [869] = 2023, [891] = 2024, [913] = 2025, [931] = 2026, [951] = 2027,
    };

    // class count of Formats/Latest -> release
    private static readonly Dictionary<int, int> ByClassCount = new()
    {
        [4098] = 2019, [4164] = 2020, [4168] = 2021, [4307] = 2022, [4418] = 2023, [4492] = 2024, [4600] = 2025, [4690] = 2026, [4757] = 2027,
    };

    internal static int? FromSchema(FormatsSchema schema) => FromEvidence(schema.IdOf("ContentMarker"), schema.Classes.Count);

    internal static int? FromSchema(FileSchema schema) => FromEvidence(schema.ByName("ContentMarker")?.Id, schema.Classes.Count);

    /// <summary>The release of a schema with this <c>ContentMarker</c> class id and class count, or null when neither is known or
    /// the two name different releases.</summary>
    internal static int? FromEvidence(int? markerId, int classCount)
    {
        int? marker = markerId is int id && ByMarker.TryGetValue(id, out int m) ? m : null;
        int? count = ByClassCount.TryGetValue(classCount, out int c) ? c : null;
        if (marker is not null && count is not null && marker != count) return null;
        return marker ?? count;
    }

    /// <summary>The release saved in BasicFileInfo (a four digit year), or null.</summary>
    internal static int? Saved(string? revitVersion)
        => int.TryParse(revitVersion, out int year) && year is >= 2000 and <= 2100 ? year : null;

    /// <summary>The stored schema decides layouts and row sizes. The saving release is only a fallback when the schema release
    /// is unknown; an older or misread BasicFileInfo year must not override the layout of the bytes.</summary>
    internal static int? Effective(int? saved, int? schema)
        => schema ?? saved;

    /// <summary>The release that decides the layouts of an open file, preferring <paramref name="schemaRelease"/> over the saving
    /// release. Every layout and row-size choice reads this one value.</summary>
    internal static int? Of(OpenMcdf.RootStorage root, int? schemaRelease)
        => Effective(Saved(BasicFileInfoReader.Read(root).GetValueOrDefault("RevitVersion")), schemaRelease);

    /// <summary>Byte width of the element id in the layout of this release: 4 up to Revit 2023, 8 from Revit 2024; null when the
    /// release is not known.</summary>
    internal static int? IdWidth(int? release) => release is int year ? (year >= 2024 ? 8 : 4) : null;

    /// <summary>Byte width of one ElemRec row in the layout of this release: 28 up to Revit 2023, 40 from Revit 2024.</summary>
    internal static int? RowLength(int? release) => IdWidth(release) is int width ? (width == 8 ? 40 : 28) : null;

    /// <summary>True when the release in force is unknown or is <paramref name="year"/>, so the layout shipped for that one release applies.</summary>
    internal static bool AllowsShipped(int? release, int year) => release is null || release == year;

    /// <summary>One diagnostics line naming both releases and the one that is used.</summary>
    internal static string Describe(int? saved, int? schema)
    {
        string Show(int? year) => year?.ToString() ?? "unknown";
        string note = saved is int s && schema is int c && s != c ? $", the layout of release {Effective(s, c)} is used" : "";
        return $"Release: saved by {Show(saved)}, schema of {Show(schema)}{note}";
    }
}
