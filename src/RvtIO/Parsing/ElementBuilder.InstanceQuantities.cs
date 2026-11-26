using RvtIO.Model;

namespace RvtIO.Parsing;

internal static partial class ElementBuilder
{
    /// <summary>
    /// Categories whose Volume (HOST_VOLUME_COMPUTED) is the volume of the solids: structural
    /// columns, architectural columns, framing and foundations, casework, windows, electrical
    /// fixtures, lighting devices and fixtures, mechanical, plumbing and speciality equipment,
    /// parking, baluster instances, generic models.
    /// </summary>
    private static readonly HashSet<int> SolidVolumeCategories =
    [
        -2001330, -2001320, -2001300, -2001000, -2000014, -2001060, -2008087, -2001120, -2001140, -2001180, -2001350, -2000127,
        -2000038, -2000151, -2000100, -2001040,
    ];

    /// <summary>
    /// Categories whose Area (HOST_AREA_COMPUTED) is half the surface of the solids and whose
    /// instances nearly all have it: electrical fixtures, lighting devices and parking.
    /// </summary>
    private static readonly HashSet<int> SolidAreaCategories = [-2001060, -2008087, -2001180];

    private static readonly HashSet<int> SolidSystemCategories = [-2000038];

    private const ushort FamilyInstanceRootClassId = 1794;

    /// <summary>
    /// At the first detail level, Coarse then Medium then Fine, that has solids, the sum of the
    /// volumes of the solids, and half the sum of the areas of their faces.
    /// </summary>
    internal static void ApplyInstanceQuantities(RevitFileInfo info, IReadOnlyDictionary<uint, ushort>? classByElement)
    {
        if (classByElement == null) return;
        foreach (var e in info.Elements)
        {
            if (e.CategoryId is not int category) continue;
            bool volume = SolidVolumeCategories.Contains(category), area = SolidAreaCategories.Contains(category);
            if (!volume && !area) continue;
            if (e.NativeMasterSymbolId == null && !SolidSystemCategories.Contains(category)) continue;
            // A family symbol holds solids too but has no Volume, and a void form (all faces
            // unfilled) is not a body.
            if (e.NativeMasterSymbolId != null && (!classByElement.TryGetValue((uint)e.Id, out var rootClass) || rootClass != FamilyInstanceRootClassId)) continue;
            if (ProductRules.HasOnlyUnfilledFaces(e)) continue;
            if (category == -2001320 && e.FramingCopingDistance is not < 0) continue;
            var counts = e.LevelSolidCounts;
            int level = 0;
            while (level < counts.Count && counts[level] <= 0) level++;
            if (level == counts.Count || e.LevelShells[level] is not { } shell) continue;
            if (volume) SetParam(e, -1012806, "HOST_VOLUME_COMPUTED", StorageType.Double, shell.Volume);
            if (area) SetParam(e, -1012805, "HOST_AREA_COMPUTED", StorageType.Double, shell.SurfaceArea / 2);
        }
    }
}
