using RvtIO.Model;

namespace RvtIO.Compact;

/// <summary>
/// The header category of an element, the one stored in the element header and used for the first pseudo object, the Category
/// parameter and every rule that depends on the category. This is the one place the compact export asks for it: the value decoded
/// from the element record (<see cref="Element.NativeHeaderCategoryId"/>). The value is a built-in category id (negative), the id
/// of a category element (positive), or <see cref="NullCategory"/>; null means that the reader has no record for the element.
/// </summary>
internal static class CompactHeaderCategory
{
    /// <summary>The null element id: an element without a header category.</summary>
    internal const int NullCategory = -1;

    internal static int? Of(Element element) =>
        // an electrical system with this stored value has another header category
        element.NativeHeaderCategoryId == ElectricalSystemStored && element.NativeRootClassName == "RbsElectricalSystem"
            ? null : element.NativeHeaderCategoryId;

    private const int ElectricalSystemStored = -2008152;
}
