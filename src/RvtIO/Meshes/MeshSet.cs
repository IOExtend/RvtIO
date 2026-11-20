namespace RvtIO.Meshes;

/// <summary>The triangle meshes of the drawable model elements of a document (see <see cref="ElementMeshes.Extract"/>). All
/// coordinates are in feet.</summary>
public sealed class MeshSet
{
    /// <summary>One entry per drawable element, in the order of <see cref="RvtDocument.Elements"/>.</summary>
    public IReadOnlyList<ElementMesh> Elements { get; init; } = [];

    /// <summary>The triangles of every symbol that an element places (<see cref="ElementMesh.Placements"/>), by symbol element id,
    /// in the frame of the family. Only placed symbols are listed.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<TriangleMesh>> Symbols { get; init; } = new Dictionary<int, IReadOnlyList<TriangleMesh>>();

    /// <summary>The document was read without decoding its geometry: a file read by its own class schema (a release other than Revit
    /// 2022, such as the Revit 2020 and 2025 samples) opened without <see cref="RvtReaderOptions.R22Layout"/>. Its elements are listed
    /// with <see cref="MeshCoverage.Undecoded"/>.</summary>
    public bool GeometryUndecoded { get; init; }
}

/// <summary>The geometry of one element: its own triangles in the coordinates of the document, plus symbols placed whole.</summary>
public sealed class ElementMesh
{
    /// <summary>The id of the element.</summary>
    public int ElementId { get; init; }

    /// <summary>The triangles of the element itself, one mesh per material, in the coordinates of the document.</summary>
    public IReadOnlyList<TriangleMesh> Meshes { get; init; } = [];

    /// <summary>Symbols (<see cref="MeshSet.Symbols"/>) that make up the rest of the element, each drawn by its transform. A placement
    /// is given only when the element's geometry is exactly its symbols placed rigidly; any other element has its own triangles.</summary>
    public IReadOnlyList<SymbolPlacement> Placements { get; init; } = [];

    /// <summary>Drawable component IDs that make up all the geometry of this container.</summary>
    public IReadOnlyList<int> ComponentIds { get; init; } = [];

    /// <summary>The endpoints of a straight model line in document coordinates (feet), if present.</summary>
    public (MeshVector Start, MeshVector End)? Line { get; init; }

    /// <summary>The points of a tessellated model arc in document coordinates (feet), if present.</summary>
    public IReadOnlyList<MeshVector>? Arc { get; init; }

    /// <summary>Independent line segments listed as consecutive endpoint pairs, in document feet.</summary>
    public IReadOnlyList<MeshVector>? LinePairs { get; init; }

    /// <summary>Whether the line pairs are datum references that are hidden by default.</summary>
    public bool IsDatum { get; init; }

    /// <summary>How completely <see cref="Meshes"/> and <see cref="Placements"/> represent the element.</summary>
    public MeshCoverage Coverage { get; init; }

    /// <summary>Faces of the element (or of its placed symbols, once per placement) that could not be triangulated.</summary>
    public int UntriangulatedFaces { get; init; }
}

/// <summary>How completely an <see cref="ElementMesh"/> represents its element. When several apply, the first in the order
/// <see cref="Undecoded"/>, <see cref="NoGeometry"/>, <see cref="BoxSubstitute"/>, <see cref="Partial"/> is reported.</summary>
public enum MeshCoverage
{
    /// <summary>Every decoded face is triangulated.</summary>
    Complete,

    /// <summary>Some faces could not be triangulated, a symbol the element places (or a symbol nested in one) has no geometry, or a
    /// Geometry node that Revit exports has no decoded polygon (display surfaces, or a duplicate of another node), so it may be
    /// missing.</summary>
    Partial,

    /// <summary>A symbol placed at a rescaled placement was replaced by its bounding box.</summary>
    BoxSubstitute,

    /// <summary>The element has a geometry record but nothing in it could be drawn.</summary>
    NoGeometry,

    /// <summary>The geometry of the document was not decoded (<see cref="MeshSet.GeometryUndecoded"/>).</summary>
    Undecoded,
}

/// <summary>Triangles that share one material.</summary>
public sealed class TriangleMesh
{
    /// <summary>Vertex coordinates x, y, z in feet, three values per vertex.</summary>
    public double[] Positions { get; init; } = [];

    /// <summary>Three vertex indices per triangle, counter-clockwise seen from the side the normals point to.</summary>
    public int[] Indices { get; init; } = [];

    /// <summary>Unit normal x, y, z per vertex. Vertices are not shared between faces, so a face keeps its own normal and the
    /// edges between faces stay sharp.</summary>
    public double[] Normals { get; init; } = [];

    /// <summary>The material element id of the triangles, 0 for none.</summary>
    public int MaterialId { get; init; }
}

/// <summary>A symbol drawn by a transform.</summary>
/// <param name="SymbolId">The key of the symbol in <see cref="MeshSet.Symbols"/>.</param>
/// <param name="Transform">The transform from the frame of the family to the coordinates of the document.</param>
public readonly record struct SymbolPlacement(int SymbolId, MeshTransform Transform);

/// <summary>A point or a direction in feet.</summary>
/// <param name="X">The X coordinate, in feet.</param>
/// <param name="Y">The Y coordinate, in feet.</param>
/// <param name="Z">The Z coordinate (up), in feet.</param>
public readonly record struct MeshVector(double X, double Y, double Z);

/// <summary>
/// A transform from the frame of a family to the coordinates of the document: a point p maps to
/// <c>Origin + BasisX * p.X + BasisY * p.Y + BasisZ * p.Z</c>. The basis vectors are the images of the family axes.
/// A negative <see cref="Determinant"/> is a mirror image: a renderer that culls back faces reverses the winding of the
/// symbol's triangles for it.
/// </summary>
/// <remarks>A <see cref="Model.GeoInstance.Transform"/> <c>t</c> holds the 3x3 matrix row by row and then the translation, so the
/// basis vectors are its columns: BasisX = (t[0], t[3], t[6]), BasisY = (t[1], t[4], t[7]), BasisZ = (t[2], t[5], t[8]) and
/// Origin = (t[9], t[10], t[11]) (see <see cref="FromRowMajor"/>).</remarks>
/// <param name="Origin">The image of the family origin, in feet: (t[9], t[10], t[11]).</param>
/// <param name="BasisX">The image of the family X axis: the column (t[0], t[3], t[6]).</param>
/// <param name="BasisY">The image of the family Y axis: the column (t[1], t[4], t[7]).</param>
/// <param name="BasisZ">The image of the family Z axis: the column (t[2], t[5], t[8]).</param>
public readonly record struct MeshTransform(MeshVector Origin, MeshVector BasisX, MeshVector BasisY, MeshVector BasisZ)
{
    /// <summary>The transform of the 12 values of a <see cref="Model.GeoInstance.Transform"/>.</summary>
    /// <exception cref="ArgumentException">Fewer than 12 values.</exception>
    public static MeshTransform FromRowMajor(IReadOnlyList<double> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (transform.Count < 12) throw new ArgumentException("A transform has 12 values.", nameof(transform));
        return new(new(transform[9], transform[10], transform[11]),
            new(transform[0], transform[3], transform[6]),
            new(transform[1], transform[4], transform[7]),
            new(transform[2], transform[5], transform[8]));
    }

    /// <summary>The determinant of the basis: 1 for a rotation, -1 for a mirror image.</summary>
    public double Determinant =>
        BasisX.X * (BasisY.Y * BasisZ.Z - BasisZ.Y * BasisY.Z)
        - BasisY.X * (BasisX.Y * BasisZ.Z - BasisZ.Y * BasisX.Z)
        + BasisZ.X * (BasisX.Y * BasisY.Z - BasisY.Y * BasisX.Z);

    /// <summary>The image of a point of the family frame.</summary>
    public MeshVector Apply(MeshVector point) => new(
        Origin.X + BasisX.X * point.X + BasisY.X * point.Y + BasisZ.X * point.Z,
        Origin.Y + BasisX.Y * point.X + BasisY.Y * point.Y + BasisZ.Y * point.Z,
        Origin.Z + BasisX.Z * point.X + BasisY.Z * point.Y + BasisZ.Z * point.Z);
}
