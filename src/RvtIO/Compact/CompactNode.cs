namespace RvtIO.Compact;

/// <summary>One node of the compact JSON properties collection: the element object and, when both
/// <paramref name="CategoryLabel"/> and <paramref name="FamilyName"/> are non-empty, the two objects that precede it.</summary>
/// <param name="ElementId">Printed as the decimal "object" string.</param>
/// <param name="ClassName">"name" of the element object (the class name without any prefix), empty when the class cannot be resolved.</param>
/// <param name="UniqueId">"externalId" of the element object.</param>
/// <param name="CategoryLabel">Name of the first of the two preceding objects.</param>
/// <param name="FamilyName">Name of the second of the two preceding objects.</param>
/// <param name="Properties">Insertion-ordered properties; the writer sorts them.</param>
internal readonly record struct CompactNode(
    long ElementId,
    string ClassName,
    string UniqueId,
    string? CategoryLabel,
    string? FamilyName,
    IReadOnlyList<CompactProperty> Properties);
