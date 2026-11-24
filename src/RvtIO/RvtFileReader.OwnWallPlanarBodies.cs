using RvtIO.Geometry;
using RvtIO.Internal;
using RvtIO.Parsing;

namespace RvtIO;

public partial class RvtFileReader
{
    private readonly record struct OwnWallGRep(PartitionChunks.Block Block, uint Size, ushort Cls, uint Owner);

    private static void ReadOwnWallPlanarBodies(ParsingState state, List<OwnWallGRep>? collected,
        Action<int, int>? progress)
    {
        var owners = state.OwnedElementClassRoots.Where(entry => entry.Value.BlockCount == 1
            && entry.Value.ClassId is 3524 or 361).Select(entry => entry.Key).ToHashSet();
        state.OwnWallPlanarBodies.Clear();
        state.OwnWallSide0Normals.Clear();
        state.OwnWallTrimmedRanges.Clear();
        state.OwnCurrentWallFaceGroups.Clear();
        if (owners.Count == 0) return;
        // Filter only after class roots are complete. In-memory reads rediscover the
        // ranges here to avoid keeping their resident segment buffers across recovery.
        var jobs = collected ?? new List<OwnWallGRep>();
        if (collected != null)
            jobs.RemoveAll(job => !owners.Contains(job.Owner));
        else
            TfStreamWalker.WalkLocated(state.Chunks, state.AllKnownIds, (_, _, size, cls, owner, block) =>
            {
                if (cls is 2007 or 2006 && owners.Contains(owner))
                    jobs.Add(new(block, size, cls, owner));
            });
        if (jobs.Count == 0) return;
        progress?.Invoke(0, jobs.Count);
        // Retain replacement wall geometry and the trimmed ranges on both paths.
        // Only IFC needs the separate planar body and its grouping provenance.
        var decoded = state.KeepExportProvenance ? new (GRepData? Body, string? Failure)[jobs.Count] : null;
        var grepCounts = jobs.GroupBy(job => job.Owner).ToDictionary(group => group.Key, group => group.Count());
        var nativeBodies = new GRepData?[jobs.Count];
        var normals = decoded == null ? null : new (uint SourceFace, double[] Normal)?[jobs.Count];
        var groups = decoded == null ? null : new IReadOnlyDictionary<uint, uint>?[jobs.Count];
        var trimmed = new (double Lo, double Hi)?[jobs.Count];
        void Decode(int index)
        {
            var (block, size, cls, owner) = jobs[index];
            if (decoded == null && (cls != 2007 || grepCounts[owner] != 1)) return;
            var (data, start) = block.Read();
            var bodyResult = DecodeOwnWallPlanarBody(data, start, size, cls, owner, out var graph);
            if (decoded != null) decoded[index] = bodyResult;
            if (state.KeepExportProvenance && graph != null && grepCounts[owner] == 1 && bodyResult.Body != null
                && state.OwnCurrentWallRoots.Contains(owner)
                && state.OwnPartitionWallParts?.TryGetValue(owner, out var current) == true
                && OwnPartitionParts.MatchesFrame(data, start, size, current.GeometrySha256))
            {
                if (state.OwnWallSide0Ids.TryGetValue(owner, out int sideId))
                    normals![index] = TfBlockDeserializer.SelectOwnWallSide0Normal(graph, sideId);
                groups![index] = TfBlockDeserializer.SelectOwnPostCutFaceGroups(graph,
                    bodyResult.Body.Faces.Select(face => face.SourceFace).ToArray());
            }
            if (graph != null && grepCounts[owner] == 1 && bodyResult.Body is { } trimmedBody
                && state.OwnWallBaseFaces.TryGetValue(owner, out var baseFaces)
                && state.OwnWallCutoutSteps.TryGetValue(owner, out var location) && location.BlockCount == 1
                && location.Axis is { } locationAxis)
                trimmed[index] = TfBlockDeserializer.SelectOwnWallTrimmedRange(graph, baseFaces, trimmedBody, locationAxis);
            if (bodyResult.Body == null)
                nativeBodies[index] = DecodeOwnNativeWallBody(state, data, start, size, cls, owner, grepCounts[owner]);
        }
        if (OperatingSystem.IsBrowser())
        {
            var clock = progress == null ? null : System.Diagnostics.Stopwatch.StartNew();
            for (int index = 0; index < jobs.Count; index++)
            {
                Decode(index);
                if (clock?.ElapsedMilliseconds >= 500 && index + 1 < jobs.Count)
                {
                    progress!(index + 1, jobs.Count);
                    clock.Restart();
                }
            }
        }
        else
            Parallel.For(0, jobs.Count, Decode);
        for (int index = 0; index < jobs.Count; index++)
        {
            if (decoded != null)
            {
                ApplyOwnWallPlanarBody(state, jobs[index].Owner, decoded[index]);
                if (normals![index] is { } normal) state.OwnWallSide0Normals[jobs[index].Owner] = normal;
                if (groups![index] is { } faces) state.OwnCurrentWallFaceGroups[jobs[index].Owner] = faces;
            }
            if (trimmed[index] is { } range) state.OwnWallTrimmedRanges[jobs[index].Owner] = range;
            if (nativeBodies[index] is { } native)
            {
                var job = jobs[index];
                state.GrepBlocks.Set(job.Owner, native, state.PartitionScratch != null ? job.Block : null,
                    job.Size, nativeWallBody: true);
            }
        }
        progress?.Invoke(jobs.Count, jobs.Count);
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
