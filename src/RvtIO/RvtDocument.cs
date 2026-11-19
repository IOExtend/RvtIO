using RvtIO.BuiltIns;
using RvtIO.Model;

namespace RvtIO;

/// <summary>
/// Main entry point for reading Revit files, mirrors the Revit API Document class.
/// Provides indexed element access, filtering, and metadata queries.
/// </summary>
public class RvtDocument
{
    private readonly RevitFileInfo _info;
    private readonly string? _sourcePath;
    private readonly Dictionary<int, Element> _elemById;
    private readonly Dictionary<long, Element>? _elementsBySourceId;
    private readonly Dictionary<string, Element>? _elemByUniqueId;
    private readonly HashSet<int> _typeElementIds;

    /// <summary>All elements in the document.</summary>
    public IReadOnlyList<Element> Elements => _info.Elements;

    /// <summary>Project metadata (Revit version, build, file path).</summary>
    public FileMetadata Metadata => _info.Metadata;

    /// <summary>Full local file path when opened via <see cref="Open"/>; null when created via <see cref="FromInfo"/>.</summary>
    public string? SourcePath => _sourcePath;

    /// <summary>Project information (name, number, client, address, etc.).</summary>
    public ProjectInfo ProjectInformation => _projectInfo ??= FillProjectInformation(_info.ProjectInfo);

    private ProjectInfo? _projectInfo;

    /// <summary>The project information the stream did not carry is read from the project information element, whose
    /// parameters hold the same fields.</summary>
    private ProjectInfo FillProjectInformation(ProjectInfo info)
    {
        if (info.Name is { Length: > 0 } && info.Number is { Length: > 0 }) return info;
        foreach (var element in _info.Elements)
        {
            if (element.CategoryId != (int)BuiltInCategory.OST_ProjectInformation) continue;
            info.Name = FirstNonEmpty(info.Name, ValueOf(element, BuiltInParameter.PROJECT_NAME));
            info.Number = FirstNonEmpty(info.Number,
                ValueOf(element, BuiltInParameter.PROJECT_NUMBER));
            info.Client = FirstNonEmpty(info.Client, ValueOf(element, BuiltInParameter.CLIENT_NAME));
            info.Address = FirstNonEmpty(info.Address,
                ValueOf(element, BuiltInParameter.PROJECT_ADDRESS));
            info.Status = FirstNonEmpty(info.Status, ValueOf(element, BuiltInParameter.PROJECT_STATUS));
            info.IssueDate = FirstNonEmpty(info.IssueDate,
                ValueOf(element, BuiltInParameter.PROJECT_ISSUE_DATE));
            info.OrganizationName = FirstNonEmpty(info.OrganizationName,
                ValueOf(element, BuiltInParameter.PROJECT_ORGANIZATION_NAME));
            break;
        }
        return info;
    }

    /// <summary>The value of a parameter, found by the identifier of the built-in parameter whether the parameter
    /// carries a definition or only its id.</summary>
    private static string? ValueOf(Element element, BuiltInParameter parameter)
    {
        int id = (int)parameter;
        foreach (var p in element.Parameters)
        {
            int parameterId = p.Definition?.Id is int definitionId && definitionId != 0 ? definitionId : p.Id;
            if (parameterId != id) continue;
            if (p.Value is string text && text.Length > 0) return text;
        }
        return null;
    }

    private static string? FirstNonEmpty(string? current, string? candidate) =>
        current is { Length: > 0 } ? current : candidate is { Length: > 0 } ? candidate : current;

    /// <summary>Transmission data with external file references.</summary>
    public TransmissionInfo? TransmissionData => _info.TransmissionData;

    /// <summary>The underlying raw data model (for JSON serialization or advanced access).</summary>
    public RevitFileInfo RawData => _info;

    /// <summary>Number of elements in the document.</summary>
    public int ElementCount => _info.Elements.Count;

    private RvtDocument(RevitFileInfo info, string? sourcePath = null)
    {
        _info = info;
        _sourcePath = sourcePath;

        _elemById = new Dictionary<int, Element>(info.Elements.Count);
        foreach (var e in info.Elements)
        {
            _elemById.TryAdd(e.Id, e);
            if (e.NativeId is long sourceId)
                (_elementsBySourceId ??= []).TryAdd(sourceId, e);
        }

        // Build type element ID set: elements referenced via ELEM_TYPE_PARAM by other elements
        _typeElementIds = new HashSet<int>();
        foreach (var e in info.Elements)
        {
            foreach (var p in e.Parameters)
            {
                if (p.Definition?.Name is "ELEM_TYPE_PARAM" or "ELEM_FAMILY_AND_TYPE_PARAM"
                    && p.Value is int typeId && typeId > 0)
                {
                    _typeElementIds.Add(typeId);
                    break;
                }
            }
        }

        SynthesizeMissingParams();
        info.AttachSourceElementIds();
        Parameter.ResolveZeroElementIds(_info);

        ResolveElementNames();

        // Build UniqueId index (only if UniqueIds are present)
        if (info.Elements.Any(e => e.UniqueId != null))
        {
            _elemByUniqueId = new Dictionary<string, Element>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in info.Elements)
            {
                if (e.UniqueId != null)
                    _elemByUniqueId.TryAdd(e.UniqueId, e);
            }
        }
    }

    /// <summary>
    /// Synthesize standard Revit BIPs that the binary parser may not have extracted.
    /// Runs once during construction to ensure API compatibility with Revit.
    /// </summary>
    private void SynthesizeMissingParams()
    {
        foreach (var elem in _info.Elements)
        {
            // ELEM_CATEGORY_PARAM is always available in the Revit API; synthesized from CategoryId.
            if (elem.CategoryId.HasValue && elem.LookupParameter("ELEM_CATEGORY_PARAM") == null)
            {
                elem.Parameters.Add(new Parameter
                {
                    Id = (int)BuiltInParameter.ELEM_CATEGORY_PARAM,
                    Definition = new Definition
                    {
                        Id = (int)BuiltInParameter.ELEM_CATEGORY_PARAM,
                        BuiltInParameter = "ELEM_CATEGORY_PARAM",
                        Name = "ELEM_CATEGORY_PARAM"
                    },
                    StorageType = StorageType.Integer,
                    Value = elem.CategoryId.Value,
                    IsReadOnly = true
                });
            }

            // ELEM_TYPE_PARAM for elements that don't have it but reference a type element:
            // walls often don't get it from the binary, but their References include the type ID.
            if (elem.FamilyTypeId <= 0 && elem.References != null && elem.References.Count > 0)
            {
                // Look for a referenced element with same category that:
                // a) is already known as a type (in _typeElementIds), OR
                // b) has the same CategoryId and no geometry/bbox (likely a type definition)
                int? candidateTypeId = null;
                foreach (var refId in elem.References)
                {
                    if (refId <= 0 || refId == elem.Id) continue;
                    if (!_elemById.TryGetValue(refId, out var refElem)) continue;
                    if (refElem.CategoryId != elem.CategoryId) continue;

                    // Known type element: best match
                    if (_typeElementIds.Contains(refId))
                    {
                        candidateTypeId = refId;
                        break;
                    }
                    // Same category, no bbox, no own type reference: likely a type definition
                    if (candidateTypeId == null
                        && (refElem.BoundingBox == null || refElem.BoundingBox.Length == 0)
                        && refElem.FamilyTypeId <= 0)
                    {
                        candidateTypeId = refId;
                    }
                }

                if (candidateTypeId.HasValue)
                {
                    elem.Parameters.Add(new Parameter
                    {
                        Id = (int)BuiltInParameter.ELEM_TYPE_PARAM,
                        Definition = new Definition
                        {
                            Id = (int)BuiltInParameter.ELEM_TYPE_PARAM,
                            BuiltInParameter = "ELEM_TYPE_PARAM",
                            Name = "ELEM_TYPE_PARAM"
                        },
                        StorageType = StorageType.ElementId,
                        Value = candidateTypeId.Value,
                        IsReadOnly = true
                    });
                    _typeElementIds.Add(candidateTypeId.Value);
                }
            }

            // Fix SYMBOL_ID_PARAM and ELEM_FAMILY_PARAM: copy value from ELEM_TYPE_PARAM if empty
            int familyTypeId = elem.FamilyTypeId; // re-check after possible synthesis above
            if (familyTypeId > 0)
            {
                FixEmptyElementIdParam(elem, "SYMBOL_ID_PARAM", (int)BuiltInParameter.SYMBOL_ID_PARAM, familyTypeId);
                FixEmptyElementIdParam(elem, "ELEM_FAMILY_PARAM", (int)BuiltInParameter.ELEM_FAMILY_PARAM, familyTypeId);
            }
        }
    }

    /// <summary>
    /// In Revit API, element.Name for instances returns the type name.
    /// Fill in empty element names from type elements where possible.
    /// Also propagate FamilyName from type elements to instances.
    /// </summary>
    private void ResolveElementNames()
    {
        foreach (var elem in _info.Elements)
        {
            // A level, a view, a grid, a room or a sheet carries its own name: a type lookup must not replace it (a
            // level whose ELEM_TYPE_PARAM resolves to a wall type was renamed to that type, e.g. "8mm Head").
            if (elem.Category is "OST_Levels" or "OST_Views" or "OST_Grids" or "OST_Rooms" or "OST_Sheets"
                or "OST_Areas" or "OST_TextNotes") continue;
            int typeId = elem.FamilyTypeId;
            if (typeId <= 0) continue;
            if (!_elemById.TryGetValue(typeId, out var typeElem)) continue;

            // In Revit API, instance.Name always returns the type name.
            // Override scanned name with the type name when available.
            if (!string.IsNullOrEmpty(typeElem.Name) && !_typeElementIds.Contains(elem.Id))
            {
                // Always prefer the type name for instances (Revit API behavior).
                // But if type name looks like a font/rendering artifact, keep instance name.
                if (!IsLikelyArtifactName(typeElem.Name))
                    elem.Name = typeElem.Name;
                else if (string.IsNullOrEmpty(elem.Name))
                    elem.Name = typeElem.Name;
            }

            // If instance has no FamilyName but type has one, inherit it
            if (string.IsNullOrEmpty(elem.FamilyName) && !string.IsNullOrEmpty(typeElem.FamilyName))
            {
                elem.FamilyName = typeElem.FamilyName;
            }
        }

        // Parts (OST_Parts): resolve FamilyName, Name and Category from DPART_ORIGINAL_* parameters when empty
        foreach (var elem in _info.Elements)
        {
            if (elem.CategoryId == (int)BuiltIns.BuiltInCategory.OST_Parts || elem.Category == "Parts" || elem.Category == "OST_Parts")
            {
                if (string.IsNullOrEmpty(elem.FamilyName))
                {
                    var paramFamily = elem.LookupParameter(BuiltIns.BuiltInParameter.DPART_ORIGINAL_FAMILY)
                        ?? elem.LookupParameter("DPART_ORIGINAL_FAMILY");
                    if (paramFamily?.Value is string family && !string.IsNullOrWhiteSpace(family))
                    {
                        elem.FamilyName = family;
                    }
                }

                if (string.IsNullOrEmpty(elem.Name))
                {
                    var paramType = elem.LookupParameter(BuiltIns.BuiltInParameter.DPART_ORIGINAL_TYPE)
                        ?? elem.LookupParameter("DPART_ORIGINAL_TYPE");
                    if (paramType?.Value is string typeName && !string.IsNullOrWhiteSpace(typeName))
                    {
                        elem.Name = typeName;
                    }
                }

                if (string.IsNullOrEmpty(elem.Category))
                {
                    var paramCat = elem.LookupParameter(BuiltIns.BuiltInParameter.DPART_ORIGINAL_CATEGORY)
                        ?? elem.LookupParameter("DPART_ORIGINAL_CATEGORY");
                    if (paramCat?.Value is string cat && !string.IsNullOrWhiteSpace(cat))
                    {
                        elem.Category = cat;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Detect names that are likely rendering/font artifacts rather than real element names.
    /// These come from scanned strings in binary data near the element.
    /// </summary>
    private static bool IsLikelyArtifactName(string name)
    {
        // Common font names that appear in rendering data
        if (name is "Arial" or "Times New Roman" or "Courier New" or "Verdana"
            or "Tahoma" or "Calibri" or "Segoe UI" or "Consolas")
            return true;
        // Rendering/sky objects
        if (name.StartsWith("SunAndSky") || name.StartsWith("assetlibrary_base"))
            return true;
        // File extensions
        if (name.EndsWith(".fbx") || name.EndsWith(".png") || name.EndsWith(".jpg")
            || name.EndsWith(".bmp") || name.EndsWith(".dll"))
            return true;
        return false;
    }

    private static void FixEmptyElementIdParam(Element elem, string paramName, int bipId, int value)
    {
        var p = elem.LookupParameter(paramName);
        if (p != null && !p.HasValue)
        {
            p.Value = value;
            p.StorageType = StorageType.ElementId;
        }
        else if (p == null)
        {
            // Don't synthesize if the param wasn't extracted at all: only fix empty values
        }
    }


    /// <summary>
    /// Open a Revit file and return a queryable Document.
    /// <code>var doc = RvtDocument.Open("project.rvt");</code>
    /// </summary>
    public static RvtDocument Open(string rvtPath, RvtReaderOptions? options = null)
    {
        var reader = new RvtFileReader(options);
        var info = reader.Read(rvtPath);
        return new RvtDocument(info, Path.GetFullPath(rvtPath));
    }

    /// <summary>
    /// Wrap an existing RevitFileInfo as a queryable Document.
    /// Useful when you already have the data from RvtFileReader.Read().
    /// Creates an isolated view of elements so that synthesizing document-level
    /// parameters and names does not mutate the input model in-place.
    /// </summary>
    public static RvtDocument FromInfo(RevitFileInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return new RvtDocument(CloneForDocument(info));
    }

    private static RevitFileInfo CloneForDocument(RevitFileInfo info)
    {
        var cloned = info.ShallowCopy();
        cloned.Elements = new List<Element>(info.Elements.Count);
        foreach (var elem in info.Elements)
        {
            var copy = elem.ShallowCopy();
            copy.Parameters = new List<Parameter>(elem.Parameters.Count);
            foreach (var p in elem.Parameters)
            {
                copy.Parameters.Add(p.ShallowCopy(p.Value));
            }
            cloned.Elements.Add(copy);
        }
        return cloned;
    }

    /// <summary>Writes a verified lossless copy of this document's backing file to <paramref name="outputPath"/>.</summary>
    /// <exception cref="InvalidOperationException">This document was constructed via <see cref="FromInfo"/> and has no backing file path.</exception>
    /// <exception cref="RvtWriteRefusedException">The backing file is outside the supported write-capability matrix.</exception>
    public RvtWriteResult SaveAs(string outputPath)
    {
        if (string.IsNullOrEmpty(_sourcePath))
            throw new InvalidOperationException("This document has no backing source file path; use RvtWriter.SaveAs(sourcePath, outputPath).");
        return RvtWriter.SaveAs(_sourcePath, outputPath);
    }


    /// <summary>Finds an element by its document-local key. Use the long overload for a source ID.</summary>
    public Element? GetElement(int elementId)
    {
        _elemById.TryGetValue(elementId, out var elem);
        return elem;
    }

    /// <summary>Finds an element by the exact source ID stored in the RVT.</summary>
    public Element? GetElement(long elementId)
    {
        if (_elementsBySourceId?.TryGetValue(elementId, out var element) == true) return element;
        if (elementId is < int.MinValue or > int.MaxValue) return null;
        element = GetElement((int)elementId);
        return element?.ElementId == elementId ? element : null;
    }

    /// <summary>Get element by UniqueId string. Returns null if not found.</summary>
    public Element? GetElement(string uniqueId)
    {
        if (_elemByUniqueId != null)
        {
            _elemByUniqueId.TryGetValue(uniqueId, out var elem);
            return elem;
        }
        // Fallback: linear scan
        foreach (var e in _info.Elements)
        {
            if (string.Equals(e.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase))
                return e;
        }
        return null;
    }

    /// <summary>
    /// Get the type element for a given instance.
    /// Looks up ELEM_TYPE_PARAM to find the type element ID, then resolves it.
    /// </summary>
    public Element? GetTypeElement(Element instance)
    {
        int typeId = instance.FamilyTypeId;
        if (typeId <= 0) return null;
        return GetElement(typeId);
    }


    /// <summary>
    /// Create a filtered element collector for all elements.
    /// Mirrors Revit API: new FilteredElementCollector(doc).
    /// <code>
    /// var walls = doc.Collector()
    ///     .OfCategory(BuiltInCategory.OST_Walls)
    ///     .WhereElementIsNotElementType()
    ///     .ToElements();
    /// </code>
    /// </summary>
    public FilteredElementCollector Collector()
        => new(_info.Elements, _typeElementIds);

    /// <summary>
    /// Create a filtered element collector for a subset of element IDs.
    /// Mirrors Revit API: new FilteredElementCollector(doc, elementIds).
    /// </summary>
    public FilteredElementCollector Collector(IEnumerable<int> elementIds)
    {
        var idSet = new HashSet<int>(elementIds);
        return new(_info.Elements.Where(e => idSet.Contains(e.Id)), _typeElementIds);
    }

    /// <summary>Set of element IDs that are known type definitions (referenced by ELEM_TYPE_PARAM).</summary>
    public IReadOnlySet<int> TypeElementIds => _typeElementIds;


    /// <summary>Get all elements of a given BuiltInCategory.</summary>
    public IList<Element> GetElements(BuiltInCategory category)
        => Collector().OfCategory(category).ToElements();

    /// <summary>Get all elements of a given category ID.</summary>
    public IList<Element> GetElements(int categoryId)
        => Collector().OfCategory(categoryId).ToElements();

    /// <summary>Gets the project building storeys and resolves element assignments with the current IFC storey rules.</summary>
    public BuildingStoreyCatalog GetBuildingStoreyCatalog() => new(_elemById);

    /// <summary>Gets project grid axes in Revit ID order. Multi-segment grids are one axis with ordered curves.</summary>
    public IReadOnlyList<GridAxis> GetGridAxes()
    {
        var axes = new List<GridAxis>(_info.CurvedGridAxes);
        foreach (var element in Collector().OfCategory(BuiltInCategory.OST_Grids).WhereElementIsNotElementType())
        {
            if (element.Location is { CurveType: "Line" } line && _info.GridChainMemberIds?.Contains(element.Id) != true)
                axes.Add(new GridAxis(element.Id, element.Name ?? string.Empty, [line]));
        }
        axes.Sort((a, b) => a.Id.CompareTo(b.Id));
        return axes;
    }

    /// <summary>Get all distinct category IDs present in the document.</summary>
    public IReadOnlyList<int> GetCategories()
    {
        var cats = new HashSet<int>();
        foreach (var e in _info.Elements)
            if (e.CategoryId.HasValue) cats.Add(e.CategoryId.Value);
        return cats.OrderBy(c => c).ToList();
    }

    /// <summary>
    /// Get a summary of categories and their element counts.
    /// Returns list of (CategoryId, CategoryName, Count).
    /// </summary>
    public IReadOnlyList<(int CategoryId, string? Name, int Count)> GetCategorySummary()
    {
        var groups = _info.Elements
            .Where(e => e.CategoryId.HasValue)
            .GroupBy(e => e.CategoryId!.Value)
            .Select(g => (CategoryId: g.Key, Name: g.First().Category, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .ToList();
        return groups;
    }
}
