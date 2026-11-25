using System.Collections;
using RvtIO.BuiltIns;
using RvtIO.Model;

namespace RvtIO;

/// <summary>
/// Provides Revit-API-like element filtering and querying.
/// Chainable filters applied lazily - enumeration triggers evaluation.
/// </summary>
public class FilteredElementCollector : IEnumerable<Element>
{
    private IEnumerable<Element> _source;
    private readonly HashSet<int>? _typeElementIds;

    internal FilteredElementCollector(IEnumerable<Element> elements, HashSet<int>? typeElementIds = null)
    {
        _source = elements;
        _typeElementIds = typeElementIds;
    }

    /// <summary>Filter by BuiltInCategory enum.</summary>
    public FilteredElementCollector OfCategory(BuiltInCategory category)
    {
        int catId = (int)category;
        _source = _source.Where(e => e.CategoryId == catId);
        return this;
    }

    /// <summary>Filter by category ID (int).</summary>
    public FilteredElementCollector OfCategory(int categoryId)
    {
        _source = _source.Where(e => e.CategoryId == categoryId);
        return this;
    }

    /// <summary>Filter by category name string (e.g. "OST_Walls").</summary>
    public FilteredElementCollector OfCategory(string categoryName)
    {
        _source = _source.Where(e =>
            string.Equals(e.Category, categoryName, StringComparison.OrdinalIgnoreCase));
        return this;
    }

    /// <summary>
    /// Keep only instance elements (not type definitions). Excludes elements whose ID is referenced as a type by other
    /// elements.
    /// </summary>
    public FilteredElementCollector WhereElementIsNotElementType()
    {
        if (_typeElementIds != null)
        {
            // Precise: exclude elements known to be types (referenced by ELEM_TYPE_PARAM of other elements)
            _source = _source.Where(e => !_typeElementIds.Contains(e.Id));
        }
        else
        {
            // Fallback: keep only elements that have ELEM_TYPE_PARAM (family instances)
            _source = _source.Where(e =>
                e.Parameters.Any(p =>
                    p.DefinitionName is "ELEM_TYPE_PARAM" or "ELEM_FAMILY_AND_TYPE_PARAM"));
        }
        return this;
    }

    /// <summary>
    /// Keep only type/symbol elements (not instances).
    /// Keeps elements whose ID is referenced as a type by other elements.
    /// </summary>
    public FilteredElementCollector WhereElementIsElementType()
    {
        if (_typeElementIds != null)
        {
            _source = _source.Where(e => _typeElementIds.Contains(e.Id));
        }
        else
        {
            _source = _source.Where(e =>
                !e.Parameters.Any(p =>
                    p.DefinitionName is "ELEM_TYPE_PARAM" or "ELEM_FAMILY_AND_TYPE_PARAM"));
        }
        return this;
    }

    /// <summary>Keep only elements that have a bounding box.</summary>
    public FilteredElementCollector WhereBoundingBoxIsNotNull()
    {
        _source = _source.Where(e =>
            e.BoundingBox != null || e.Geometry?.BoundingBox != null);
        return this;
    }

    /// <summary>Keep only elements that have GRep geometry (faces).</summary>
    public FilteredElementCollector WhereHasGeometry()
    {
        _source = _source.Where(e => e.Geometry != null);
        return this;
    }

    /// <summary>Keep only elements that have face geometry.</summary>
    public FilteredElementCollector WhereHasFaces()
    {
        _source = _source.Where(e =>
            e.Geometry?.Faces != null && e.Geometry.Faces.Count > 0);
        return this;
    }

    /// <summary>Keep only elements that have parameters.</summary>
    public FilteredElementCollector WhereHasParameters()
    {
        _source = _source.Where(e => e.Parameters.Count > 0);
        return this;
    }

    /// <summary>Keep only elements with a specific parameter name.</summary>
    public FilteredElementCollector WhereHasParameter(string paramName)
    {
        _source = _source.Where(e =>
            e.Parameters.Any(p => p.DefinitionName == paramName));
        return this;
    }

    /// <summary>Keep only elements with a specific BuiltInParameter.</summary>
    public FilteredElementCollector WhereHasParameter(BuiltInParameter bip)
    {
        int bipId = (int)bip;
        _source = _source.Where(e =>
            e.Parameters.Any(p => p.DefinitionId == bipId));
        return this;
    }

    /// <summary>Filter by family name.</summary>
    public FilteredElementCollector OfFamilyName(string familyName)
    {
        _source = _source.Where(e =>
            string.Equals(e.FamilyName, familyName, StringComparison.OrdinalIgnoreCase));
        return this;
    }

    /// <summary>Custom predicate filter.</summary>
    public FilteredElementCollector Where(Func<Element, bool> predicate)
    {
        _source = _source.Where(predicate);
        return this;
    }

    /// <summary>Exclude specific element IDs.</summary>
    public FilteredElementCollector Excluding(IEnumerable<int> elementIds)
    {
        var idSet = new HashSet<int>(elementIds);
        _source = _source.Where(e => !idSet.Contains(e.Id));
        return this;
    }

    /// <summary>Materialize all matching elements into a list.</summary>
    public IList<Element> ToElements() => _source.ToList();

    /// <summary>Get all matching element IDs.</summary>
    public IList<int> ToElementIds() => _source.Select(e => e.Id).ToList();

    /// <summary>Get the first matching element, or null.</summary>
    public Element? FirstElement() => _source.FirstOrDefault();

    /// <summary>Count matching elements without materializing.</summary>
    public int GetElementCount() => _source.Count();

    /// <inheritdoc />
    public IEnumerator<Element> GetEnumerator() => _source.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
