using RvtIO.Model;

namespace RvtIO.Ifc;

public partial class IfcExporter
{
    /// <summary>The Reference of a flow fitting, controller, terminal or building element proxy in the current profile when no
    /// Reference parameter exists and the type name could not be validated from the symbol records: the element name of the type,
    /// <c>Family:Type</c> only with the UseFamilyAndTypeNameForReference option. The type name is also the second part of the product name
    /// "Family:Type:Id".
    /// Null when the rule does not apply: another class (pipe curves have their own type name rule), a Reference
    /// parameter, a validated or blocked type name, or a name without a type part.</summary>
    internal static string? ResolveTypeNameReferenceCurrent(
        Element element, string ifcType, string familyAndTypeName, Element? type)
    {
        (string? Validated, bool Blocked) = ifcType switch
        {
            "IFCFLOWFITTING" or "IFCFLOWCONTROLLER" or "IFCFLOWTERMINAL" =>
                (element.ValidatedDistributionTypeReferenceName, element.ValidatedDistributionTypeReferenceIsBlocked),
            "IFCBUILDINGELEMENTPROXY" or "IFCTRANSPORTELEMENT" =>
                (element.ValidatedProxyTypeReferenceName, element.ValidatedProxyTypeReferenceIsBlocked),
            // The element name of a beam can be another name of the instance; the type name of the product name is used, as for the classes above.
            "IFCBEAM" => (null, false),
            _ => ("", true),
        };
        if (Blocked || Validated != null) return null;
        if (ExactIfcStringParameterResolver.ResolveReference(element, type).State != ExactIfcStringParameterState.Absent)
            return null;
        int separator = familyAndTypeName.IndexOf(':');
        return separator < 0 || separator == familyAndTypeName.Length - 1 ? null : familyAndTypeName[(separator + 1)..];
    }
}
