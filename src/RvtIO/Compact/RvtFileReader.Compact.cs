using RvtIO.Internal;
using RvtIO.Lookup;
using RvtIO.Model;

namespace RvtIO;

public partial class RvtFileReader
{
    // Use the locale of the stored lookup entries even when export selects a different locale for names.
    static partial void ReadCompactLookupText(RevitFileInfo info) =>
        info.SerializedLookupNoneText = RevitSystemFamilyNames.Load(
            info.Metadata.RevitVersion, info.Metadata.SavedLocale)?.Get(RevitUiText.LookupNone);

    static partial void ReadCompactFormatSettings(RevitFileInfo info, ParsingState state) =>
        info.SerializedFormatSettings = Compact.CompactFormatSettings.Create(
            state.OwnDocumentUnitOptions, state.OwnDocumentUnitConversions, state.OwnDocumentSchemas);
}
