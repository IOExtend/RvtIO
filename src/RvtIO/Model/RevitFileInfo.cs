namespace RvtIO.Model
{
    /// <summary>Everything the reader decodes from one Revit file.</summary>
    public class RevitFileInfo
    {
        /// <summary>A copy that shares all members with this instance; the caller replaces what it changes.</summary>
        internal RevitFileInfo ShallowCopy() => (RevitFileInfo)MemberwiseClone();

        /// <summary>The basic file information.</summary>
        public FileMetadata Metadata { get; set; } = new();
        /// <summary>The project information.</summary>
        public ProjectInfo ProjectInfo { get; set; } = new();
        /// <summary>The transmission data, null when the file has none.</summary>
        public TransmissionInfo? TransmissionData { get; set; }

        /// <summary>
        /// Shared parameter-definition table, keyed by parameter definition id
        /// (negative for built-in params, positive for user/shared params).
        /// Every <see cref="Parameter"/> on an element references an entry here by its
        /// <see cref="Parameter.Id"/>; only the per-element <see cref="Parameter.Value"/>
        /// is stored inline. Populated by <see cref="BuildParameterDefinitionTable"/>
        /// before serialization to avoid repeating definitions across millions of params.
        /// </summary>
        public Dictionary<int, ParameterDefinition>? ParameterDefinitions { get; set; }

        /// <summary>All elements of the file.</summary>
        public List<Element> Elements { get; set; } = new();

        /// <summary>The whole ElemTable was decoded and verified, so a parsed record without a row is a
        /// record the exporter never iterates (a deleted or embedded element), not an element lost to a damaged stream.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        internal bool ElemTableIsExact { get; set; }

        /// <summary>The file is of the 64-bit-id layout, whose element geometry is not decoded: the IFC exporter writes the products
        /// that have no bounding box as they are, without a representation.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        internal bool GeometryUndecoded { get; set; }

        /// <summary>The file is of the 64-bit-id layout and was read without <see cref="RvtReaderOptions.R22Layout"/>, so no element
        /// geometry was decoded. (<see cref="GeometryUndecoded"/> stays true for such a file read with the rewrite.)</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        internal bool WideLayoutNotRewritten { get; set; }

        /// <summary>GStyleElem id -> the ElementId of its GStyle material (-1 or 0 stand for no material) and the
        /// ElementId of its category, for the graphics styles whose block decoded exactly once. The material
        /// of a solid whose faces carry none comes from the style of the solid,
        /// and the solid is named after the category of that style.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        internal IReadOnlyDictionary<int, (int Material, int Category)>? GraphicsStyles { get; set; }

        /// <summary>GStyleElem id -> the Object Styles row (category, owner, projection/cut type, pen number,
        /// color 0xBBGGRR, line pattern id), for the styles whose block decoded exactly once.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        internal IReadOnlyDictionary<int, (int CategoryId, int OwnerId, int GStyleType, int PenNumber, int Color, int LinePatternId)>? GraphicsStylePens { get; set; }

        /// <summary>The document's Line Weights table (the lowest-id PenWidthTableElem whose block decoded exactly
        /// once): model rows by inverted scale and the annotation pens, all widths in feet. Null when no table decoded.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        internal (Dictionary<int, double[]> ModelRows, double[] DraftPens)? PenWidthTable { get; set; }

        /// <summary>LinePatternElem id -> the line pattern (name and segments: data in feet, type 0 dash / 1 space /
        /// 2 dot), for the patterns whose block decoded exactly once.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        internal IReadOnlyDictionary<int, (string Name, List<(double Data, int Type)> Segs)>? LinePatterns { get; set; }

        /// <summary>FamilySymbol id -> the linework of a title block family symbol in symbol feet, for the symbols
        /// whose block decoded exactly once.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        internal IReadOnlyDictionary<int, IReadOnlyList<(double X0, double Y0, double X1, double Y1, int StyleId)>>? TitleBlockLines { get; set; }

        /// <summary>CategoryElem id -> its name, for the category elements whose block decoded exactly once.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        internal IReadOnlyDictionary<int, string>? CategoryNames { get; set; }

        /// <summary>Project, shared and family parameters of the file: definitions, bindings and per-element values
        /// (<see cref="Parsing.UserParameterCatalog"/>). Null for a file that did not go through the full reader.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        internal Parsing.UserParameterCatalog? UserParameters { get; set; }

        /// <summary>Last authored project phase, when the unique ordered source is known.
        /// The phase of the command line option <c>--ifc-phase last</c>; the exporter's own default is the last phase that is a phase element (<see cref="Ifc.IfcExporter.ExportPhaseId"/>).</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        internal int? SerializedDefaultExportPhaseId { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        internal int[]? SerializedProjectPhaseIds { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        internal bool SerializedHasZeroProjectPhase { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        internal bool SerializedHasZeroDimensionStyle { get; set; }

        /// <summary>DocumentHistory.m_detachGUID of Global/History: the document GUID that
        /// is combined with project-level ids and level ids to form export ids.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        internal Guid? SerializedDocumentGuid { get; set; }

        /// <summary>The document's unit format options per spec with the decimal and digit grouping
        /// symbols, the numeric unit conversions and the unit symbol labels: what the display-value
        /// formatter (<c>Compact.CompactValueFormatter</c>) needs. Null when the document's own units element or
        /// persisted unit schemas could not be read completely.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        internal Compact.CompactFormatSettings? SerializedFormatSettings { get; set; }

        /// <summary>The unit id of the document's stored Length format option (for example "autodesk.unit.unit:feetFractionalInches-1.0.1");
        /// null when the units element has no single, non-default Length option.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        internal string? SerializedLengthUnit { get; set; }

        /// <summary>Why <see cref="SerializedLengthUnit"/> is null; null when it is set.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        internal string? SerializedLengthUnitIssue { get; set; }

        /// <summary>Whether the document is workshared, from the BasicFileInfo "Worksharing" line ("Not enabled" is false,
        /// "Local" and "Central" are true); null when the line is missing or says something else.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        internal bool? SerializedIsWorkshared { get; set; }

        /// <summary>Tf class schema registry from Formats/Latest. Maps class name -> field list.</summary>
        public Dictionary<string, ClassSchema>? ClassSchemas { get; set; }

        /// <summary>Shared-coordinates / true-north placement, when the project is rotated
        /// or offset from its internal origin. 12 doubles [basisX(3), basisY(3), basisZ(3),
        /// origin(3)] in feet - maps project-internal coords to shared coords, exactly like
        /// a GInstance transform. Applied to the IFCSITE placement so every element inherits
        /// it. Null = axis-aligned.</summary>
        public double[]? SharedTransform { get; set; }

        /// <summary>True when <see cref="SharedTransform"/> is the exactly resolved active project location (not the older fallback).</summary>
        internal bool SharedTransformIsActiveLocation { get; set; }

        /// <summary>The text of the empty lookup table entry ("&lt;None&gt;") in the locale the file was saved in; null when the
        /// Revit resources of that locale are not available.</summary>
        internal string? SerializedLookupNoneText { get; set; }

        /// <summary>Latitude and longitude (radians) of the GeoSite of the active project location;
        /// null when the file has no readable active location.</summary>
        public double? SiteLatitudeRadians { get; set; }

        /// <summary>See <see cref="SiteLatitudeRadians"/>.</summary>
        public double? SiteLongitudeRadians { get; set; }

        /// <summary>Elevation (feet) of the project base point in shared coordinates; 0 when unknown.</summary>
        public double SiteElevationFeet { get; set; }

        /// <summary>The place name of the GeoSite of the active project location (GeoSite.m_placeName, such as
        /// "Boston, MA"); null when the file has no readable active location or the site names no place.</summary>
        public string? SitePlaceName { get; set; }

        /// <summary>
        /// Build the shared <see cref="ParameterDefinitions"/> table from all element
        /// parameters. The invariant fields of each parameter (name, storage type,
        /// read-only flag, ForgeId, unit symbol, GUID) are de-duplicated by definition id.
        /// </summary>
        public void BuildParameterDefinitionTable()
        {
            var table = new Dictionary<int, ParameterDefinition>();
            foreach (var e in Elements)
            {
                if (e.Parameters == null) continue;
                foreach (var p in e.Parameters)
                {
                    if (!table.TryGetValue(p.Id, out var def))
                    {
                        table[p.Id] = new ParameterDefinition
                        {
                            Name = p.Definition?.Name,
                            BuiltInParameter = p.Definition?.BuiltInParameter,
                            StorageType = p.StorageType,
                            IsReadOnly = p.IsReadOnly,
                            ForgeId = p.ForgeId,
                            UnitSymbol = p.UnitSymbol,
                            Guid = p.Guid,
                        };
                    }
                    else
                    {
                        // Fill nullable fields that a later instance resolved but an earlier one missed.
                        def.Name ??= p.Definition?.Name;
                        def.BuiltInParameter ??= p.Definition?.BuiltInParameter;
                        def.ForgeId ??= p.ForgeId;
                        def.UnitSymbol ??= p.UnitSymbol;
                        def.Guid ??= p.Guid;
                    }
                }
            }
            ParameterDefinitions = table;
        }
    }

    /// <summary>
    /// A de-duplicated parameter definition shared across all element instances that
    /// carry this parameter. Referenced by <see cref="Parameter.Id"/>.
    /// </summary>
    public class ParameterDefinition
    {
        /// <summary>Display name. For BIPs this equals <see cref="BuiltInParameter"/>.</summary>
        public string? Name { get; set; }

        /// <summary>BuiltInParameter enum name (e.g. "WALL_USER_HEIGHT_PARAM"); null for user/shared params.</summary>
        public string? BuiltInParameter { get; set; }

        /// <summary>How the value of the parameter is stored.</summary>
        public StorageType StorageType { get; set; }

        /// <summary>Whether the parameter cannot be changed.</summary>
        public bool IsReadOnly { get; set; }

        /// <summary>Forge TypeId string for user/shared params.</summary>
        public string? ForgeId { get; set; }

        /// <summary>Display unit symbol (e.g. "mm", "m^2").</summary>
        public string? UnitSymbol { get; set; }

        /// <summary>GUID for shared/external parameters.</summary>
        public System.Guid? Guid { get; set; }
    }

    /// <summary>A class definition from the Formats/Latest schema table.</summary>
    public class ClassSchema
    {
        /// <summary>The class name.</summary>
        public string Name { get; set; } = "";
        /// <summary>The fields of the class.</summary>
        public List<ClassField> Fields { get; set; } = new();
    }

    /// <summary>A field in a class schema.</summary>
    public class ClassField
    {
        /// <summary>The field name.</summary>
        public string Name { get; set; } = "";
        /// <summary>The stored type of the field.</summary>
        public string Type { get; set; } = "";
    }

    /// <summary>The transmission data of a file that was sent for exchange.</summary>
    public class TransmissionInfo
    {
        /// <summary>Whether the file is transmitted.</summary>
        public bool IsTransmitted { get; set; }
        /// <summary>The user data of the transmission.</summary>
        public string? UserData { get; set; }
        /// <summary>The version of the transmission data.</summary>
        public int Version { get; set; }
        /// <summary>The external files the document refers to.</summary>
        public List<ExternalFileRef> ExternalReferences { get; set; } = new();
    }

    /// <summary>A reference to an external file.</summary>
    public class ExternalFileRef
    {
        /// <summary>The id of the element that holds the reference.</summary>
        public int ElementId { get; set; }
        /// <summary>The kind of the reference.</summary>
        public string? ReferenceType { get; set; }
        /// <summary>The stored path.</summary>
        public string? Path { get; set; }
        /// <summary>The absolute path.</summary>
        public string? AbsolutePath { get; set; }
        /// <summary>The kind of the path.</summary>
        public string? PathType { get; set; }
        /// <summary>The load state of the reference.</summary>
        public string? LoadState { get; set; }
        /// <summary>The server location.</summary>
        public string? ServerLocation { get; set; }
    }
}
