using RvtIO.Geometry;
using RvtIO.Internal;
using RvtIO.Model;

namespace RvtIO.Parsing;

internal static partial class ElementBuilder
{
    internal static bool ApplyOwnWallPlanarBody(Element element, ParsingState state)
    {
        element.OwnPrimaryPlanarBody = null;
        element.OwnWallSide0NormalCurrent = null;
        element.OwnCurrentCutPairBodyCurrent = null;
        if (element.Id <= 0 || element.IsBlockOnly || element.CategoryId != -2000011
            || !state.OwnedElementClassRoots.TryGetValue((uint)element.Id, out var root)
            || root.BlockCount != 1 || root.ClassId is not (3524 or 361)
            || !state.OwnWallPlanarBodies.TryGetValue((uint)element.Id, out var planar)
            || planar.BlockCount != 1 || planar.Body is not { PreserveSourceFaceIdentity: true } source
            || source.BoundingBox is not { Length: 6 } bounds || bounds.Any(v => !double.IsFinite(v))
            || Enumerable.Range(0, 3).Any(axis => bounds[axis] >= bounds[axis + 3])
            || source.Solids.Count == 0 || source.Solids.Any(solid => !solid.IsClosed)
            || source.Solids.Sum(solid => solid.FaceIndices.Count) != source.Faces.Count
            || source.Solids.SelectMany(solid => solid.FaceIndices).Any(i => i < 0 || i >= source.Faces.Count)
            || source.Solids.SelectMany(solid => solid.FaceIndices).Distinct().Count() != source.Faces.Count) return false;
        var geometry = BuildGeometryData(source, state.SeparateFacesWithDistinctOpenings);
        if (geometry == null || geometry.Faces.Count != source.Faces.Count
            || geometry.BrepSolids is not { Count: > 0 } solids
            || solids.Sum(solid => solid.FaceIndices.Count) != geometry.Faces.Count
            || solids.SelectMany(solid => solid.FaceIndices).Distinct().Count() != geometry.Faces.Count
            || solids.Any(solid => !solid.IsClosed || !FacetedShell.TryOrientExactFacetedShell(
                geometry.Faces, solid.FaceIndices, out _, triangulateNonPlanarFaces: false))) return false;
        // Body selection does not reinterpret legacy graphics-style/header metadata.
        geometry.GStyleElemId = element.Geometry?.GStyleElemId ?? 0;
        geometry.Flags = element.Geometry?.Flags ?? 0;
        element.OwnPrimaryPlanarBody = geometry;
        if (state.OwnCurrentWallFaceGroups.TryGetValue((uint)element.Id, out var groups)
            && source.Faces.All(face => groups.ContainsKey(face.SourceFace)))
            element.OwnCurrentCutPairBodyCurrent = BuildGeometryData(new()
            {
                PreserveSourceFaceIdentity = true, BoundingBox = source.BoundingBox, Solids = source.Solids,
                Faces = source.Faces.Select(face => face with { SourceFace = groups[face.SourceFace] }).ToList(),
            }, state.SeparateFacesWithDistinctOpenings);
        if (state.OwnWallSide0Normals.TryGetValue((uint)element.Id, out var side)
            && source.Faces.Any(face => face.SourceFace == side.SourceFace))
            element.OwnWallSide0NormalCurrent = side.Normal;
        return true;
    }
}
