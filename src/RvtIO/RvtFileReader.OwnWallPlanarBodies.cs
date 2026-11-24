using RvtIO.Geometry;
using RvtIO.Internal;
using RvtIO.Parsing;

namespace RvtIO;

public partial class RvtFileReader
{
    private static void ReadOwnWallPlanarBodies(ParsingState state)
    {
        var owners = state.OwnedElementClassRoots.Where(entry => entry.Value.BlockCount == 1
            && entry.Value.ClassId is 3524 or 361).Select(entry => entry.Key).ToHashSet();
        state.OwnWallPlanarBodies.Clear();
        state.OwnWallSide0Normals.Clear();
        state.OwnWallTrimmedRanges.Clear();
        state.OwnCurrentWallFaceGroups.Clear();
        if (owners.Count == 0) return;
        // Class roots are complete now; a second anchored walk avoids stream-order admission. The walk only collects the
        // owner blocks; the per-block geometry decode is independent, so it runs in parallel and the results are applied in
        // walk order.
        var jobs = new List<(PartitionChunks.Block Block, uint Size, ushort Cls, uint Owner)>();
        TfStreamWalker.Walk(state.Chunks, state.AllKnownIds, (data, start, size, cls, owner) =>
        {
            if (cls is 2007 or 2006 && owners.Contains(owner))
                jobs.Add((new(data, start, checked((int)size + 16), state.PartitionScratch), size, cls, owner));
        });
        var decoded = new (GRepData? Body, string? Failure)[jobs.Count];
        var grepCounts = jobs.GroupBy(job => job.Owner).ToDictionary(group => group.Key, group => group.Count());
        var nativeBodies = new GRepData?[jobs.Count];
        var normals = new (uint SourceFace, double[] Normal)?[jobs.Count];
        var groups = new IReadOnlyDictionary<uint, uint>?[jobs.Count];
        var trimmed = new (double Lo, double Hi)?[jobs.Count];
        Parallel.For(0, jobs.Count, index =>
        {
            var (block, size, cls, owner) = jobs[index];
            var (data, start) = block.Read();
            decoded[index] = DecodeOwnWallPlanarBody(data, start, size, cls, owner, out var graph);
            if (graph != null && grepCounts[owner] == 1 && decoded[index].Body != null
                && state.OwnCurrentWallRoots.Contains(owner)
                && state.OwnPartitionWallParts?.TryGetValue(owner, out var current) == true
                && OwnPartitionParts.MatchesFrame(data, start, size, current.GeometrySha256))
            {
                if (state.OwnWallSide0Ids.TryGetValue(owner, out int sideId))
                    normals[index] = TfBlockDeserializer.SelectOwnWallSide0Normal(graph, sideId);
                groups[index] = TfBlockDeserializer.SelectOwnPostCutFaceGroups(graph,
                    decoded[index].Body!.Faces.Select(face => face.SourceFace).ToArray());
            }
            if (graph != null && grepCounts[owner] == 1 && decoded[index].Body is { } trimmedBody
                && state.OwnWallBaseFaces.TryGetValue(owner, out var baseFaces)
                && state.OwnWallCutoutSteps.TryGetValue(owner, out var location) && location.BlockCount == 1
                && location.Axis is { } locationAxis)
                trimmed[index] = TfBlockDeserializer.SelectOwnWallTrimmedRange(graph, baseFaces, trimmedBody, locationAxis);
            if (decoded[index].Body == null)
                nativeBodies[index] = DecodeOwnNativeWallBody(state, data, start, size, cls, owner, grepCounts[owner]);
        });
        for (int index = 0; index < jobs.Count; index++)
        {
            ApplyOwnWallPlanarBody(state, jobs[index].Owner, decoded[index]);
            if (normals[index] is { } normal) state.OwnWallSide0Normals[jobs[index].Owner] = normal;
            if (trimmed[index] is { } range) state.OwnWallTrimmedRanges[jobs[index].Owner] = range;
            if (groups[index] is { } faces) state.OwnCurrentWallFaceGroups[jobs[index].Owner] = faces;
            if (nativeBodies[index] is { } native) state.GrepBlocks[jobs[index].Owner] = native;
        }
    }

    internal static GRepData? DecodeOwnNativeWallBody(ParsingState state, byte[] data, int start,
        uint size, ushort cls, uint owner, int grepCount)
    {
        if (grepCount != 1 || cls != 2007 || start < 0 || start > data.Length - 16
            || BitConverter.ToUInt32(data, start) != owner
            || BitConverter.ToUInt16(data, start + 12) != cls
            || !state.OwnedElementClassRoots.TryGetValue(owner, out var root)
            || root.BlockCount != 1 || root.ClassId is not (3524 or 361)) return null;
        var body = GRepParserV2.Parse(data, start, size, nativeWallBody: true);
        if (!(body is { Faces.Count: > 0, Instances.Count: 0 }
            && body.Faces.All(face => face.MeshEdgeBounds is { Length: 6 })
            && body.Faces.Any(face => face.AnalyticCylinderRadius > 0))) return null;
        if (!state.GrepBlocks.TryGetValue(owner, out var original)) return null;
        var nativeGeometry = ElementBuilder.BuildGeometryData(body, state.SeparateFacesWithDistinctOpenings);
        var originalGeometry = ElementBuilder.BuildGeometryData(original, state.SeparateFacesWithDistinctOpenings);
        var helper = state.OwnWallHelpers.TryGetValue(owner, out var source) && source.BlockCount == 1
            ? source.Input : null;
        // shortcut: only correct the StandardCase-to-Wall bounds defect; extend after other native boundaries are confirmed.
        if (nativeGeometry?.BrepSolids is not [{ IsClosed: true } solid]
            || nativeGeometry.Faces.Where(face => face.CurvedSurface).Any(face => !WallExtrusion.IsVerticalCylinderPatch(face))
            || WallExtrusion.IsStandardCaseBody(originalGeometry, helper) != true
            || WallExtrusion.IsStandardCaseBody(nativeGeometry, helper) != false
            || WallExtrusion.TryFindVerticalExtrusion(nativeGeometry.Faces, out string reason, true,
                nativeGeometry.Faces.Select(face => face.MeshEdgeBounds!).ToList()) || reason != "bbox"
            || !FacetedShell.TryOrientExactFacetedShell(nativeGeometry.Faces, solid.FaceIndices, out _, triangulateNonPlanarFaces: false)) return null;
        return body;
    }

    private static (GRepData? Body, string? Failure) DecodeOwnWallPlanarBody(
        byte[] data, int start, uint size, ushort cls, uint owner,
        out TfBlockDeserializer.OwnGElementGeometryGraph? graph)
    {
        string? failure = "unsupported-grep";
        GRepData? body = null;
        graph = null;
        if (cls == 2007)
        {
            graph = TfBlockDeserializer.ExtractOwnGElementGeometryGraph(data, start, size, cls, out failure);
            if (graph != null && graph.OwnerId != owner) { graph = null; failure = "capture-owner"; }
            if (graph != null) body = GRepParserV2.SelectOwnFirstDirectPlanarBody(graph, out failure);
        }
        return (body, failure);
    }

    private static void ApplyOwnWallPlanarBody(ParsingState state, uint owner, (GRepData? Body, string? Failure) decoded)
    {
        if (state.OwnWallPlanarBodies.TryGetValue(owner, out var prior))
        {
            state.OwnWallPlanarBodies[owner] = (prior.BlockCount + 1, null, "duplicate-grep");
            return;
        }
        state.OwnWallPlanarBodies[owner] = (1, decoded.Body, decoded.Failure);
    }

    internal static void RecordOwnWallPlanarBody(ParsingState state, byte[] data, int start,
        uint size, ushort cls, uint owner)
    {
        if (cls is not (2007 or 2006)) return;
        ApplyOwnWallPlanarBody(state, owner, DecodeOwnWallPlanarBody(data, start, size, cls, owner, out _));
    }
}
