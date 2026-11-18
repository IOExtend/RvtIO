namespace RvtIO.Model;

public partial class RevitFileInfo
{
    /// <summary>The document's unit formatting, or null when its units element or schemas are incomplete.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    internal Compact.CompactFormatSettings? SerializedFormatSettings { get; set; }

    /// <summary>The empty lookup entry ("&lt;None&gt;") in the file's saved locale, or null without its resources.</summary>
    internal string? SerializedLookupNoneText { get; set; }
}
