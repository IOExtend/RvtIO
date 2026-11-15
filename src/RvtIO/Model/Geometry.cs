namespace RvtIO.Model
{
    /// <summary>
    /// Parsed geometry representation from GRep blocks (classIdx &amp; 0xFFFF == 0x07D7).
    /// Mirrors the Geometry/Face/Edge hierarchy of Revit's geometry model.
    /// </summary>
    public class GeometryData
    {
        /// <summary>GStyle element ID (material/graphics style reference).</summary>
        public int GStyleElemId { get; set; }

        /// <summary>Flags from GInfoBlob header.</summary>
        public uint Flags { get; set; }

        /// <summary>Outer bounding box [minX, minY, minZ, maxX, maxY, maxZ] in feet.</summary>
        public double[]? BoundingBox { get; set; }

        /// <summary>The m_bbox of the root representation (feet, in the frame of the geometry), which may exceed the tight box.</summary>
        internal double[]? RootBoundingBox { get; set; }

        /// <summary>List of geometry nodes (GGroups containing faces/edges).</summary>
        public List<GeoFace> Faces { get; set; } = new();

        /// <summary>Classifier-only faces: no boundary and no rendered geometry.</summary>
        internal List<GeoFace>? InactiveFaces { get; set; }

        /// <summary>The Geometry nodes written as triangle sets, in the frame of <see cref="Faces"/> (see GRepParserV2.BuildNodes).</summary>
        internal List<RvtIO.Parsing.GeometryNode>? MeshNodes { get; set; }

        /// <summary>The surface integrals of the Geometry nodes (see GRepData.NodeMeasures), under the node ids of <see cref="Faces"/>.</summary>
        internal List<RvtIO.Parsing.MeshNodeMeasure>? NodeMeasures { get; set; }

        /// <summary>A Geometry node written as a triangle set has a face that is not a plane (a round end, a hole): the body is the face meshes,
        /// whatever the dense polygons of the planar faces close into.</summary>
        internal bool HasCurvedFace(IReadOnlyCollection<int>? nodes = null) => MeshNodes?.Any(node => (nodes == null || nodes.Contains(node.Node))
            && node.Faces.Any(face => face.SurfaceKind != RvtIO.Geometry.Meshing.SurfaceKind.Plane)) == true;

        /// <summary>List of edges with 3D coordinates.</summary>
        public List<GeoEdge>? Edges { get; set; }

        /// <summary>Unique vertices [x, y, z] in feet, deduplicated across all faces.</summary>
        public List<double[]>? Vertices { get; set; }

        /// <summary>Extruded solids (e.g. wall extrusions). Computed from bbox + width param.</summary>
        public List<GeoSolid>? Solids { get; set; }

        /// <summary>Topological BRep shells decoded directly from Revit geometry. Face
        /// indices refer to <see cref="Faces"/>; separate shells are separate solids.</summary>
        public List<GeoBrepSolid>? BrepSolids { get; set; }

        /// <summary>
        /// 3x4 transform matrix [r00,r01,r02,tx, r10,r11,r12,ty, r20,r21,r22,tz]
        /// from local to world coordinates. Null if identity or not found.
        /// </summary>
        public double[]? Transform { get; set; }

        /// <summary>Nested family-symbol geometry references retained from GInstance
        /// nodes. The transform maps the referenced symbol's local coordinates into this
        /// representation's coordinate system.</summary>
        public List<GeoInstance>? Instances { get; set; }

        /// <summary>Top-level GInstance nodes whose transformed symbol faces make up this
        /// element's complete face list. Used by the IFC writer to retain the native
        /// representation-map structure instead of duplicating the expanded mesh.</summary>
        internal List<GeoInstance>? ExactMappedInstances { get; set; }

        /// <summary>A symbol placed into this geometry was cut by the tight box of the owning GRep, or left out because it lay
        /// outside it (ElementBuilder.ClipGeometryToBounds): the faces are not the placed symbols as they are.</summary>
        internal bool PlacedSymbolClipped { get; set; }

        /// <summary>A component placed at a rescaled placement was replaced by its tight box (ElementBuilder.ScaledComponentFaceBudget).</summary>
        internal bool PlacedSymbolBox { get; set; }

        /// <summary>A GInstance of this geometry, or of a symbol it places at any depth, placed nothing: its symbol has no
        /// geometry.</summary>
        internal bool UnresolvedSymbol { get; set; }

        /// <summary>Faces of this geometry, or of the symbols placed into it, that are left out because the triangulation of their
        /// trim loops ran out of its work budget: they have no polygon in <see cref="Faces"/>, so the geometry is partial.</summary>
        internal int UntriangulatedFaces { get; set; }

        /// <summary>Number of stored GFace and Edge objects in the GRep block this geometry was
        /// built from (null when the block's object graph was not walked). Such node counts of a family instance's
        /// geometry are compared with its symbol's to decide whether the
        /// instance carries a geometry of its own.</summary>
        internal (int Faces, int Edges)? StoredNodeSummary { get; set; }

        /// <summary>Number of stored Geometry (solid) objects of the same block; null when the object graph was not walked.
        /// The solids of a family instance's geometry and of its symbol's are counted as well.</summary>
        internal int? StoredSolids { get; set; }

        /// <summary>Geometry nodes of the block that are exported as solids and that have no polygon in <see cref="Faces"/>: a
        /// node of display surfaces only, or a node whose polygons all duplicate those of another node (identical solids
        /// in two nodes). Each has the graphics style of the node and the dominant material of its faces (0 for none);
        /// they only count as pieces of the product's material list.</summary>
        internal List<(int Node, int Style, int RenderStyleId)>? GhostNodes { get; set; }

        /// <summary>Object count per serialized class id of the same block; kept only while RVTIO_TYPEKEY_DIAG is set.</summary>
        internal Dictionary<ushort, int>? StoredClassCounts { get; set; }
    }

    /// <summary>A placed copy of a symbol geometry.</summary>
    public class GeoInstance
    {
        /// <summary>The id of the symbol that is placed.</summary>
        public int SymbolId { get; set; }
        /// <summary>The 12 values of the placement transform: the 3x3 matrix row by row, then the translation, so a point p of the
        /// symbol maps to (t[9] + p.X t[0] + p.Y t[1] + p.Z t[2], t[10] + p.X t[3] + ..., t[11] + p.X t[6] + ...) and the basis vectors are the
        /// columns (see <see cref="RvtIO.Meshes.MeshTransform.FromRowMajor"/>).</summary>
        public double[] Transform { get; set; } = new double[12];

        /// <summary>Where the GInstance stands among the nodes of its GRep in the depth-first walk (internal order key).</summary>
        internal long NodeOrder { get; set; }
    }

    /// <summary>A planar face defined by origin + two direction vectors.</summary>
    public class GeoFace
    {
        /// <summary>GFace.m_renderStyleId: the material element id of the face (internal; not serialized).</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        internal int? RenderStyleId { get; set; }

        /// <summary>Plane origin [x, y, z] in feet.</summary>
        public double[] Origin { get; set; } = new double[3];

        /// <summary>Plane X direction vector [x, y, z].</summary>
        public double[] XVector { get; set; } = new double[3];

        /// <summary>Plane Y direction vector [x, y, z].</summary>
        public double[] YVector { get; set; } = new double[3];

        /// <summary>Normal = XVector x YVector (computed).</summary>
        public double[] Normal { get; set; } = new double[3];

        /// <summary>2D envelope of the face in parametric space [minU, minV, maxU, maxV].</summary>
        public double[]? Envelope { get; set; }

        /// <summary>Net face area in ft^2 = envelope area minus inner-loop (opening) areas.</summary>
        public double? Area { get; set; }

        /// <summary>Inner loop envelopes (openings/holes). Each = [minU, minV, maxU, maxV].</summary>
        public List<double[]>? InnerLoops { get; set; }

        /// <summary>Outer boundary vertices [x,y,z] computed from envelope + plane. 4 corners CCW.</summary>
        public List<double[]>? OuterVertices { get; set; }

        /// <summary>Inner loop (opening) vertices. Each list = 4 corners CCW for one opening.</summary>
        public List<List<double[]>>? InnerVertices { get; set; }

        /// <summary>Alternative containing cycle from the same exact GRep edge graph.
        /// Internal because it is validation input, not an additional rendered face.</summary>
        internal List<double[]>? AlternativeOuterVertices { get; set; }

        /// <summary>Serialized analytic-cylinder source for this tessellated patch.
        /// Internal provenance used to recover semantic IFC swept solids without
        /// classifying regular polygon meshes as circles.</summary>
        internal double[]? AnalyticCylinderOrigin { get; set; }
        internal double[]? AnalyticCylinderAxis { get; set; }
        internal double[]? AnalyticCylinderReferenceDirection { get; set; }
        internal double AnalyticCylinderRadius { get; set; }

        /// <summary>Bounds of this source Face's authored Edge points in the same frame as the vertices.</summary>
        internal double[]? MeshEdgeBounds { get; set; }
        internal double[]? MeshPlaneEnvelopeCorners { get; set; }

        /// <summary>The circular edges of a planar face with their stored parameter spans (see <c>Internal.Arc</c>).</summary>
        internal IReadOnlyList<RvtIO.Internal.Arc>? MeshArcs { get; set; }

        /// <summary>The area of the serialized Face of this region (see GRepFace.QuadratureArea); null: the polygon area.</summary>
        internal double? QuadratureArea { get; set; }

        /// <summary>True for a tessellated patch of a non-planar surface.</summary>
        internal bool CurvedSurface { get; set; }

        /// <summary>The face lies on a plane surface of the file (GRepFace.PlaneSurface); kept through placing and copying of
        /// the geometry.</summary>
        internal bool PlaneSurface { get; set; }

        /// <summary>The u/v frame axes of the serialized planar surface (the face envelope is evaluated in this frame).</summary>
        internal double[]? PlaneXAxis { get; set; }
        internal double[]? PlaneYAxis { get; set; }

        /// <summary>Handle of the serialized Face this region came from (0 when unknown); one face per handle.</summary>
        internal uint SourceFace { get; set; }

        /// <summary>The handle of the serialized Face of a face that was placed from a symbol (the placed copies drop <see cref="SourceFace"/>): the
        /// key of the face meshes of the node the face belongs to.</summary>
        internal uint MeshMarker { get; set; }
        internal IReadOnlyList<double[]>? FirstSourceLoop { get; set; }

        /// <summary>All the loops of the serialized Face this region came from, in the order of the file (shared by the regions of
        /// one Face); the bounds of an IfcFace are written in that order.</summary>
        internal IReadOnlyList<List<double[]>>? SourceLoops { get; set; }

        /// <summary>The Geometry node that owns the face in its GRep (0 when not known). Each Geometry
        /// node is exported as one solid, however many disjoint shells it holds; ids are unique within one GeometryData.</summary>
        internal int GeometryNode { get; set; }

        /// <summary>Where the Geometry node of the face stands in the depth-first walk of the nodes, nested symbols
        /// inline at their GInstance (0 when not known): the order of the Body items. Not unique across nodes of other
        /// GRep levels beyond the third.</summary>
        internal long NodeOrder { get; set; }

        /// <summary>The graphics style (GStyleElem id) of the Geometry node that owns the face; -1 for none.</summary>
        internal int GeometryStyle { get; set; } = -1;

        /// <summary>GInfo flags of the serialized Face this polygon came from; null for a face placed from a nested symbol
        /// or not read (see <see cref="ProductRules.HasOnlyUnfilledFaces"/>).</summary>
        internal uint? GInfoFlags { get; set; }

        /// <summary>The material of a face that was placed here from a nested symbol (GInstance); RenderStyleId is
        /// left unset for such faces. The solids of nested instances are collected like the instance's own.</summary>
        internal int? NestedRenderStyleId { get; set; }

        /// <summary>Edge indices belonging to this face.</summary>
        public List<int>? EdgeIndices { get; set; }
    }

    /// <summary>An edge connecting two 3D points.</summary>
    public class GeoEdge
    {
        /// <summary>Start point [x, y, z] in feet.</summary>
        public double[] Start { get; set; } = new double[3];

        /// <summary>End point [x, y, z] in feet.</summary>
        public double[] End { get; set; } = new double[3];
    }

    /// <summary>
    /// Extruded solid: a 2D profile swept along a direction.
    /// Used for walls (rectangle profile extruded vertically).
    /// All dimensions in feet.
    /// </summary>
    public class GeoSolid
    {
        /// <summary>Position of the solid base center [x, y, z] in feet.</summary>
        public double[] Position { get; set; } = new double[3];

        /// <summary>Direction of profile X-axis [x, y, z] (wall length direction for walls).</summary>
        public double[] RefDirection { get; set; } = new double[3];

        /// <summary>Extrusion direction [x, y, z] (typically [0,0,1] for walls).</summary>
        public double[] ExtrusionDirection { get; set; } = [0, 0, 1];

        /// <summary>Profile width in feet (along RefDirection - wall length for walls).</summary>
        public double ProfileWidth { get; set; }

        /// <summary>Profile depth in feet (perpendicular to RefDirection - wall thickness for walls).</summary>
        public double ProfileDepth { get; set; }

        /// <summary>Extrusion height in feet.</summary>
        public double ExtrusionHeight { get; set; }

        /// <summary>Rectangular openings cut through this solid.</summary>
        public List<GeoOpening>? Openings { get; set; }
    }

    /// <summary>A connected boundary-representation shell recovered from Revit's GRep.</summary>
    public class GeoBrepSolid
    {
        /// <summary>The indices of the faces of the solid.</summary>
        public List<int> FaceIndices { get; set; } = new();
        /// <summary>Whether the faces close the solid.</summary>
        public bool IsClosed { get; set; }
    }

    /// <summary>
    /// A rectangular opening (void) in a solid element.
    /// Position is relative to the parent solid's local coordinate system.
    /// All dimensions in feet.
    /// </summary>
    public class GeoOpening
    {
        /// <summary>Center position [x, y, z] in feet relative to parent solid origin.</summary>
        public double[] Position { get; set; } = new double[3];

        /// <summary>Opening width in feet (along parent solid RefDirection).</summary>
        public double Width { get; set; }

        /// <summary>Opening height in feet (vertical).</summary>
        public double Height { get; set; }
    }

    /// <summary>
    /// Location curve for wall elements (equivalent to Revit LocationCurve).
    /// Coordinates are in LOCAL space - apply GRep Transform to get world coordinates.
    /// </summary>
    public class LocationCurve
    {
        /// <summary>"Line" or "Arc".</summary>
        public string CurveType { get; set; } = "Line";

        /// <summary>Start point [x, y, z] in feet (world coordinates).</summary>
        public double[] StartPoint { get; set; } = new double[3];

        /// <summary>End point [x, y, z] in feet (world coordinates).</summary>
        public double[] EndPoint { get; set; } = new double[3];

        /// <summary>Direction vector [x, y, z] (unit, world coordinates). For lines only.</summary>
        public double[]? Direction { get; set; }

        /// <summary>Curve length in feet.</summary>
        public double Length { get; set; }

        /// <summary>Lines only: origin, direction (not unit) and end parameters of the curve driver the line was built from.</summary>
        internal double[]? DriverOrigin { get; set; }
        internal double[]? DriverDirection { get; set; }
        internal double[]? DriverEndParams { get; set; }

        /// <summary>For arcs: center point [x, y, z] in feet (world coordinates).</summary>
        public double[]? ArcCenter { get; set; }

        /// <summary>For arcs: radius in feet.</summary>
        public double? ArcRadius { get; set; }

        /// <summary>For arcs: the unit X and Y axes of the stored arc and the parameters of its start and end
        /// (<c>Centre + R * (cos t * X + sin t * Y)</c>); the normal of the arc is X x Y.</summary>
        internal double[]? ArcXDirection { get; set; }
        internal double[]? ArcYDirection { get; set; }
        internal double ArcStartParameter { get; set; }
        internal double ArcEndParameter { get; set; }
    }

    /// <summary>
    /// Dimension / constraint geometry decoded from 0x091F blocks.
    /// Revit stores dimensions and dimension-like constraints (OST_Dimensions,
    /// OST_WeakDims, OST_Constraints) as an ordered list of reference points
    /// (the witness-line endpoints). Consecutive points form measured segments;
    /// the distance between them is the dimension value.
    /// </summary>
    public class DimensionData
    {
        /// <summary>Ordered reference points [x, y, z] in feet (world coordinates).</summary>
        public List<double[]> Points { get; set; } = new();

        /// <summary>Measured segment lengths in feet (distance between consecutive points).</summary>
        public List<double> Segments { get; set; } = new();

        /// <summary>
        /// Primary measured value in feet = distance between the first two reference points.
        /// For a single-segment linear dimension this is the dimension value.
        /// </summary>
        public double Value { get; set; }
    }

    /// <summary>
    /// FamilyInstance placement transform in world coordinates.
    /// Equivalent to Revit FamilyInstance.GetTransform().
    /// </summary>
    public class InstanceTransform
    {
        /// <summary>Origin point [x, y, z] in feet (translation component of transform).</summary>
        public double[] Origin { get; set; } = new double[3];

        /// <summary>X basis vector [x, y, z] (first column of rotation matrix).</summary>
        public double[] BasisX { get; set; } = new double[3];

        /// <summary>Y basis vector [x, y, z] (second column of rotation matrix).</summary>
        public double[] BasisY { get; set; } = new double[3];

        /// <summary>Z basis vector [x, y, z] (third column of rotation matrix).</summary>
        public double[] BasisZ { get; set; } = new double[3];

        /// <summary>Family symbol (type) element ID.</summary>
        public int SymbolId { get; set; }
    }
}
