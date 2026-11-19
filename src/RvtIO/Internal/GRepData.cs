namespace RvtIO.Internal;

internal record GRepData
{
    public int GStyleElemId { get; set; }
    public uint Flags { get; set; }
    public double[]? BoundingBox { get; set; }

    /// <summary>The m_bbox of the root representation, as read for the family symbol bounding box: the tight box (<see cref="BoundingBox"/>)
    /// is smaller where non-visible geometry reaches out of the visible one.</summary>
    public double[]? RootBoundingBox { get; set; }
    public List<GRepFace> Faces { get; set; } = new();

    /// <summary>Inactive physical faces with directly decoded surfaces; the simple-wall test looks at them.</summary>
    public List<GRepFace>? InactiveFaces { get; set; }

    /// <summary>The Geometry nodes written as triangle sets (see GRepParserV2.BuildNodes); null when the mesher is off or no node qualifies.</summary>
    public List<RvtIO.Parsing.GeometryNode>? MeshNodes { get; set; }

    /// <summary>The area and signed volume of every Geometry node whose faces are all planes, cylinders and cones (the surface integrals the exporter's
    /// NetVolumeCalculator and NetSurfaceAreaCalculator sum), in the frame of the symbol; a negative volume marks a closed node the exporter takes no
    /// solid from. Null when none is measured.</summary>
    public List<RvtIO.Parsing.MeshNodeMeasure>? NodeMeasures { get; set; }

    /// <summary>The cache is the boundary of a room with a round wall (see GRepParserV2.ParseCurvedSpaceBoundary), not a planar one.</summary>
    public bool CurvedSpaceBoundary { get; set; }

    /// <summary>Connected BRep shells recovered from face adjacency.  A GElement may own
    /// several independent solids; keeping them separate is required for valid IFC
    /// ClosedShell items and for lossless family geometry.</summary>
    public List<GRepSolid> Solids { get; set; } = new();

    /// <summary>Geometry nodes (id = queue index + 1, graphics style, material of their faces) that are exported as solids
    /// but whose faces are all display surfaces (GInfo bit 0x00200000), so no body face of them is decoded. Material
    /// bookkeeping only: they add no polygon to <see cref="Faces"/>.</summary>
    public List<(int Node, int Style, int RenderStyleId)>? DisplayOnlyNodes { get; set; }

    // Exact own primary-body Faces are identities, not coordinate-scan duplicates.
    public bool PreserveSourceFaceIdentity { get; set; }

    /// <summary>The faces form an intentionally open sheet (for example terrain), so
    /// solid-only cleanup must not remove coplanar triangulation facets.</summary>
    public bool IsOpenSurface { get; set; }

    internal (double Volume, double Area, int Count)? LightingBodyMeasures { get; set; }
    internal (double Volume, double Area, int Count)? SourceBodyMeasures { get; set; }
    internal (double Top, double Bottom)? SourceCapAreas { get; set; }
    internal (double Area, double Volume)? TrimmedBodyMeasures { get; set; }
    internal double? FilledArea { get; set; }

    /// <summary>Faces left out because the triangulation of their trim loops ran out of its work budget (see
    /// GRepParserV2.TrimWorkBudget); they have no polygon here rather than one without their trim.</summary>
    public int UntriangulatedFaces { get; set; }

    /// <summary>
    /// 3x4 transform matrix [r00,r01,r02,tx, r10,r11,r12,ty, r20,r21,r22,tz]
    /// mapping from local to world coordinates. Null if identity or not found.
    /// </summary>
    public double[]? Transform { get; set; }

    /// <summary>When the element's GRep is a GInstance (family instances: doors, windows,
    /// furniture...), the FAMILY SYMBOL element id whose GRep holds the actual geometry  - 
    /// this element's shape = symbol shape x InstanceTransform. Mirrors IfcMappedItem.</summary>
    public int SymbolElemId { get; set; }

    /// <summary>GInstance placement: rows = basisX(3), basisY(3), basisZ(3), origin(3)  - 
    /// 12 doubles, local->world (feet).</summary>
    public double[]? InstanceTransform { get; set; }

    /// <summary>Every nested GInstance owned by this representation. Complex family
    /// symbols contain many independent mapped children; retaining only one loses solids.</summary>
    public List<GRepInstance> Instances { get; set; } = new();

    /// <summary>For a FamilySymbol complex cutter, the authored dummy-host closure plane
    /// in family-local coordinates. Replacing the symmetric dummy-host slab delimited by
    /// this plane with the real host slab preserves every authored reveal/protrusion.</summary>
    public double[]? FamilyClosurePlaneOrigin { get; set; }
    public double[]? FamilyClosurePlaneNormal { get; set; }

    /// <summary>True only when the schema walk found the root FamilySymbol
    /// m_complexCutGeoms field and selected faces below that exact branch.</summary>
    public bool IsFamilyComplexCut { get; set; }

    public Dictionary<ushort, int>? QueueClassCounts { get; set; }

    /// <summary>True when the serialized graph of this block holds detail-level alternatives (a GConditionInt on the detail
    /// level below a GFilter), so that the Coarse level can differ from the Fine level this record holds.</summary>
    public bool HasDetailLevelAlternatives { get; set; }

    /// <summary>The same block at Revit's Coarse detail level, kept only when it differs from this (Fine) one. The "Area" of a door
    /// or window is computed from the solids of this level (see IfcExporter.DoorWindowQuantitiesCurrent).</summary>
    public GRepData? Coarse { get; set; }

    private Lazy<GRepData?>? _coarseQuantities;

    /// <summary>Coarse solids including non-visible objects, used only for computed door/window surface quantities.</summary>
    public GRepData? CoarseQuantities
    {
        get => _coarseQuantities?.Value;
        set => _coarseQuantities = value == null ? null : new Lazy<GRepData?>(() => value);
    }
    internal void SetCoarseQuantityFactory(Func<GRepData?> factory) => _coarseQuantities = new(factory);

    /// <summary>The same block at the Medium detail level, kept only when it differs from this (Fine) one.</summary>
    public GRepData? Medium { get; set; }
}

internal record GRepInstance
{
    public int SymbolElemId { get; set; }
    public double[] Transform { get; set; } = new double[12];

    /// <summary>Where the GInstance node stands in the depth-first walk of the nodes (see GRepParserV2.ComputeNodeOrders).</summary>
    public long NodeOrder { get; set; }
}

internal record GRepFace
{
    /// <summary>GFace.m_renderStyleId: the material element of the face; absent when not read.</summary>
    public int? RenderStyleId { get; init; }

    public double[] Envelope { get; set; } = Array.Empty<double>();
    public bool Orient { get; set; }
    public double[] Origin { get; set; } = new double[3];
    public double[] XVector { get; set; } = new double[3];
    public double[] YVector { get; set; } = new double[3];
    public List<double[]> InnerLoops { get; set; } = new();

    /// <summary>Explicit polygon vertices [x,y,z] (feet) from the schema-driven decoder
    /// (GRepParserV2) - real tessellated boundary, not an envelope. When set, downstream
    /// geometry uses these directly instead of reconstructing from Envelope+plane.</summary>
    public List<double[]>? Vertices { get; set; }

    /// <summary>Explicit inner boundary rings (holes) of a schema-decoded face.</summary>
    public List<List<double[]>>? InnerVertices { get; set; }
    public List<double[]>? FirstSourceLoop { get; init; }

    /// <summary>All the loops of the serialized Face in the order the file lists them (the first one is <see cref="FirstSourceLoop"/>),
    /// the same list for every region the Face was split into; null when the loops were not read in order.</summary>
    public IReadOnlyList<List<double[]>>? SourceLoops { get; init; }

    /// <summary>An independently closed containing cycle recovered from the same authored
    /// edge graph. Kept separate so incomplete/notched topology does not replace the primary
    /// decoded face; consumers may use it only after validating the owning solid.</summary>
    public List<double[]>? AlternativeOuterVertices { get; set; }

    /// <summary>Exact analytic-cylinder provenance from the serialized GSurface. These
    /// values describe the source surface in feet and are set only for SurfCylinder,
    /// never for a cone or for a cylinder inferred from polygon coordinates.</summary>
    public double[]? AnalyticCylinderOrigin { get; set; }
    public double[]? AnalyticCylinderAxis { get; set; }
    public double[]? AnalyticCylinderReferenceDirection { get; set; }
    public double AnalyticCylinderRadius { get; set; }

    /// <summary>Bounds of the authored Edge samples evaluated on each Edge's first active Face.</summary>
    public double[]? MeshEdgeBounds { get; init; }
    public double[]? MeshPlaneEnvelopeCorners { get; init; }

    /// <summary>The circular Edges (cylinder meets this plane square to its axis) on the boundary of a planar face, with the
    /// stored parameter span a profile arc is sampled over.</summary>
    public IReadOnlyList<Arc>? MeshArcs { get; init; }

    /// <summary>The area of the serialized planar Face (RvtIO.Geometry.Meshing.FaceArea), the same for every
    /// region of the Face; set only for a Face with a curved Edge, whose quadrature differs from the polygon area.</summary>
    public double? QuadratureArea { get; init; }

    /// <summary>True for a tessellated patch of a non-planar surface (cylinder, cone, revolution, ruled, Hermite).</summary>
    public bool CurvedSurface { get; init; }

    /// <summary>The face lies on a plane surface of the file (GSurface class Plane). When every face of a closed shell lies
    /// on a plane, all its edges are straight lines and the shell is a polyhedron whose volume is exact.
    /// False for faces tessellated from other surfaces (CurvedSurface) and for faces of unknown
    /// surface.</summary>
    public bool PlaneSurface { get; init; }

    /// <summary>Serialized frame axes of the planar surface this face lies on (the plane's u/v directions).</summary>
    public double[]? PlaneX { get; init; }
    public double[]? PlaneY { get; init; }

    /// <summary>Handle of the serialized Face this region came from; one Face can own several coplanar regions.</summary>
    public uint SourceFace { get; init; }

    /// <summary>The Geometry node that owns the Face (queue index + 1 of the nearest Geometry ancestor in the
    /// serialized graph; 0 when not known). Each Geometry node is exported as one solid.</summary>
    public int GeometryNode { get; init; }

    /// <summary>Where the Geometry node that owns the Face stands in the depth-first walk of the nodes (0 when not known).</summary>
    public long NodeOrder { get; init; }

    /// <summary>The graphics style (GStyleElem id) in the GInfo of the Geometry node that owns the Face; -1 for none.</summary>
    public int GeometryStyle { get; init; } = -1;

    /// <summary>GInfo flags of the serialized Face (null when not read).</summary>
    public uint? GInfoFlags { get; init; }

    /// <summary>Face tag used by the owning element's geometry history.</summary>
    public int? GInfoTag { get; init; }
}

/// <summary>A circular Edge as the file stores it: the circle (centre, unit X and Y, radius, in feet) and the parameters of its two
/// ends. A point is <c>Centre + Radius * (cos(t) * X + sin(t) * Y)</c>; a profile sampler takes <c>ceil(|Last - First| /
/// (pi / 16))</c> equal steps over the stored span. <c>Edge</c> is the handle
/// of the Edge it belongs to.</summary>
internal sealed record Arc(double[] Centre, double[] X, double[] Y, double Radius, double First, double Last, uint Edge = 0)
{
    /// <summary>The same arc in another frame: <paramref name="point"/> maps the centre, <paramref name="vector"/> the two axes (a proper
    /// rotation), <paramref name="scale"/> the radius.</summary>
    public Arc Transformed(Func<double[], double[]> point, Func<double[], double[]> vector, double scale) =>
        new(point(Centre), vector(X), vector(Y), Radius * scale, First, Last);

    public double[] UnitAt(double t) =>
    [
        Math.Cos(t) * X[0] + Math.Sin(t) * Y[0],
        Math.Cos(t) * X[1] + Math.Sin(t) * Y[1],
        Math.Cos(t) * X[2] + Math.Sin(t) * Y[2],
    ];

    public double[] At(double t) =>
    [
        Centre[0] + Radius * (Math.Cos(t) * X[0] + Math.Sin(t) * Y[0]),
        Centre[1] + Radius * (Math.Cos(t) * X[1] + Math.Sin(t) * Y[1]),
        Centre[2] + Radius * (Math.Cos(t) * X[2] + Math.Sin(t) * Y[2]),
    ];
}

internal record GRepSolid
{
    public List<int> FaceIndices { get; set; } = new();
    public bool IsClosed { get; set; }
}
