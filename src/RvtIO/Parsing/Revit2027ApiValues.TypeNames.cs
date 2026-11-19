using RvtIO.BuiltIns;
using RvtIO.Internal;

namespace RvtIO.Parsing;

internal static partial class Revit2027ApiValues
{
    private static string? TypeName(ParsingState state, string? kind, TfBlockDeserializer.NativeParams root) => kind switch
    {
        "TilePatternType" => root.StringFields.GetValueOrDefault(WideNativeParams.TilePatternName),
        "GeoSite" => "Location Data",
        "StairsTriserSymbol" when root.Fields.TryGetValue(TriserHost, out int host) =>
            RootIs(state, host, "StairsRun") ? "Run(Internal)" : RootIs(state, host, "StairsLanding") ? "Landing(Internal)" : null,
        "ConceptualConstructionType" when root.Fields.TryGetValue(ConstructionCategory, out int category)
            && root.Fields.TryGetValue(ConstructionKind, out int index) =>
            ConstructionNames.TryGetValue((BuiltInCategory)category, out var names) && (uint)index < names.Length ? names[index] : null,
        _ => null,
    };

    // The same ordinal labels different constructions in each mass subcategory.
    private static readonly IReadOnlyDictionary<BuiltInCategory, string[]> ConstructionNames =
        new Dictionary<BuiltInCategory, string[]>
        {
            [BuiltInCategory.OST_MassWallsAll] =
            [
                "Lightweight Construction - High Insulation",
                "Lightweight Construction - Typical Cold Climate Insulation",
                "Lightweight Construction - Typical Mild Climate Insulation",
                "Lightweight Construction - Low Insulation",
                "Lightweight Construction - No Insulation",
                "High Mass Construction - High Insulation",
                "High Mass Construction - Typical Cold Climate Insulation",
                "High Mass Construction - Typical Mild Climate Insulation",
                "High Mass Construction - No Insulation",
            ],
            [BuiltInCategory.OST_MassRoof] =
            [
                "High Insulation - Cool Roof", "High Insulation - Dark Roof",
                "Typical Insulation - Cool Roof", "Typical Insulation - Dark Roof",
                "Low Insulation - Cool Roof", "Low Insulation - Dark Roof", "No Insulation - Dark Roof",
            ],
            [BuiltInCategory.OST_MassFloorsAll] =
            [
                "Lightweight Construction - High Insulation", "Lightweight Construction - Typical Insulation",
                "Lightweight Construction - Low Insulation", "Lightweight Construction - No Insulation",
                "High Mass Construction - Frigid Climate Slab Insulation", "High Mass Construction - Cold Climate Slab Insulation",
                "High Mass Construction - No Insulation",
            ],
            [BuiltInCategory.OST_MassShade] = ["Basic Shade"],
            [BuiltInCategory.OST_MassGlazingAll] =
            [
                "Single Pane Clear - No Coating", "Single Pane - Tinted", "Single Pane - Reflective",
                "Double Pane Clear - No Coating", "Double Pane - Tinted", "Double Pane - Reflective",
                "Double Pane Clear - LowE Cold Climate, High SHGC", "Double Pane Clear - LowE Hot Climate, Low SHGC",
                "Double Pane Clear - High Performance, LowE, High Tvis, Low SHGC",
                "Triple Pane Clear - LowE Hot or Cold Climate", "Quad Pane Clear - LowE Hot or Cold Climate",
            ],
            [BuiltInCategory.OST_MassOpening] = ["Air"],
        };
}
