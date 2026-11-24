using System.Collections;
using System.Runtime.InteropServices;
using RvtIO.Internal;
using RvtIO.Model;

namespace RvtIO.Parsing;

/// <summary>Stores the expanded face lists of a scene read in the caller's geometry scratch stream.</summary>
internal sealed class GeometryFaceStore(PartitionChunks.Backing storage)
{
    private const int PageBytes = 64 * 1024;
    private readonly MemoryStream output = new(PageBytes);
    private readonly object pageLock = new();
    private Page? cachedPage;
    private GeoFace[]? cachedFaces;

    internal void Archive(Element element)
    {
        ArchiveBody(element.Geometry);
        ArchiveBody(element.SerializedComputedSymbolGeometry);
    }

    private void ArchiveBody(GeometryData? body)
    {
        if (body == null) return;
        body.ArchiveFaces(Store);
        body.KeepSceneMeshNodeFacts();
    }

    private IReadOnlyList<GeoFace> Store(List<GeoFace> faces)
    {
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        var pages = new List<Page>();
        int first = 0;
        for (int i = 0; i < faces.Count; i++)
        {
            WriteFace(writer, faces[i]);
            if (output.Length < PageBytes && i + 1 < faces.Count) continue;
            var block = storage.Add(output.GetBuffer().AsSpan(0, checked((int)output.Length)));
            pages.Add(new Page(first, i + 1, block));
            first = i + 1;
            output.SetLength(0);
            if (output.Capacity > 2 * PageBytes) output.Capacity = PageBytes;
        }
        return new FaceList(this, pages.ToArray(), faces.Count);
    }

    private GeoFace[] ReadPage(Page page)
    {
        lock (pageLock)
        {
            if (ReferenceEquals(cachedPage, page)) return cachedFaces!;
            using var reader = new BinaryReader(new MemoryStream(storage.Read(page.Block), writable: false));
            var faces = new GeoFace[page.End - page.First];
            for (int i = 0; i < faces.Length; i++) faces[i] = ReadFace(reader);
            if (page.Block.Length <= 2 * PageBytes)
            {
                cachedPage = page;
                cachedFaces = faces;
            }
            return faces;
        }
    }

    private sealed record Page(int First, int End, PartitionChunks.Chunk Block);

    private sealed class FaceList(GeometryFaceStore store, Page[] pages, int count) : IReadOnlyList<GeoFace>
    {
        public int Count => count;
        public GeoFace this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                int lo = 0, hi = pages.Length - 1;
                while (lo < hi)
                {
                    int mid = lo + (hi - lo) / 2;
                    if (index < pages[mid].End) hi = mid;
                    else lo = mid + 1;
                }
                return store.ReadPage(pages[lo])[index - pages[lo].First];
            }
        }

        public IEnumerator<GeoFace> GetEnumerator()
        {
            foreach (var page in pages)
                foreach (var face in store.ReadPage(page))
                    yield return face;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static void WriteFace(BinaryWriter writer, GeoFace face)
    {
        WriteOptional(writer, face.RenderStyleId, static (output, value) => output.Write(value));
        WriteArray(writer, face.Origin);
        WriteArray(writer, face.XVector);
        WriteArray(writer, face.YVector);
        WriteArray(writer, face.Normal);
        WriteArray(writer, face.Envelope);
        WriteOptional(writer, face.Area, static (output, value) => output.Write(value));
        WriteRing(writer, face.InnerLoops);
        WriteRing(writer, face.OuterVertices);
        WriteRings(writer, face.InnerVertices);
        WriteRing(writer, face.AlternativeOuterVertices);
        WriteArray(writer, face.AnalyticCylinderOrigin);
        WriteArray(writer, face.AnalyticCylinderAxis);
        WriteArray(writer, face.AnalyticCylinderReferenceDirection);
        writer.Write(face.AnalyticCylinderRadius);
        WriteArray(writer, face.MeshEdgeBounds);
        WriteArray(writer, face.MeshPlaneEnvelopeCorners);
        WriteArcs(writer, face.MeshArcs);
        WriteOptional(writer, face.QuadratureArea, static (output, value) => output.Write(value));
        writer.Write(face.CurvedSurface);
        writer.Write(face.PlaneSurface);
        WriteArray(writer, face.PlaneXAxis);
        WriteArray(writer, face.PlaneYAxis);
        writer.Write(face.SourceFace);
        writer.Write(face.MeshMarker);
        WriteRing(writer, face.FirstSourceLoop);
        WriteRings(writer, face.SourceLoops);
        writer.Write(face.GeometryNode);
        writer.Write(face.NodeOrder);
        writer.Write(face.GeometryStyle);
        WriteOptional(writer, face.GInfoFlags, static (output, value) => output.Write(value));
        WriteOptional(writer, face.NestedRenderStyleId, static (output, value) => output.Write(value));
        WriteArray(writer, face.EdgeIndices);
    }

    private static GeoFace ReadFace(BinaryReader reader) => new()
    {
        RenderStyleId = ReadOptional(reader, static input => input.ReadInt32()),
        Origin = ReadArray<double>(reader)!,
        XVector = ReadArray<double>(reader)!,
        YVector = ReadArray<double>(reader)!,
        Normal = ReadArray<double>(reader)!,
        Envelope = ReadArray<double>(reader),
        Area = ReadOptional(reader, static input => input.ReadDouble()),
        InnerLoops = ReadRing(reader),
        OuterVertices = ReadRing(reader),
        InnerVertices = ReadRings(reader),
        AlternativeOuterVertices = ReadRing(reader),
        AnalyticCylinderOrigin = ReadArray<double>(reader),
        AnalyticCylinderAxis = ReadArray<double>(reader),
        AnalyticCylinderReferenceDirection = ReadArray<double>(reader),
        AnalyticCylinderRadius = reader.ReadDouble(),
        MeshEdgeBounds = ReadArray<double>(reader),
        MeshPlaneEnvelopeCorners = ReadArray<double>(reader),
        MeshArcs = ReadArcs(reader),
        QuadratureArea = ReadOptional(reader, static input => input.ReadDouble()),
        CurvedSurface = reader.ReadBoolean(),
        PlaneSurface = reader.ReadBoolean(),
        PlaneXAxis = ReadArray<double>(reader),
        PlaneYAxis = ReadArray<double>(reader),
        SourceFace = reader.ReadUInt32(),
        MeshMarker = reader.ReadUInt32(),
        FirstSourceLoop = ReadRing(reader),
        SourceLoops = ReadRings(reader),
        GeometryNode = reader.ReadInt32(),
        NodeOrder = reader.ReadInt64(),
        GeometryStyle = reader.ReadInt32(),
        GInfoFlags = ReadOptional(reader, static input => input.ReadUInt32()),
        NestedRenderStyleId = ReadOptional(reader, static input => input.ReadInt32()),
        EdgeIndices = ReadArray<int>(reader)?.ToList(),
    };

    private static void WriteOptional<T>(BinaryWriter writer, T? value, Action<BinaryWriter, T> write) where T : struct
    {
        writer.Write(value.HasValue);
        if (value is T item) write(writer, item);
    }

    private static T? ReadOptional<T>(BinaryReader reader, Func<BinaryReader, T> read) where T : struct =>
        reader.ReadBoolean() ? read(reader) : null;

    private static void WriteArray<T>(BinaryWriter writer, T[]? values) where T : unmanaged
    {
        writer.Write(values?.Length ?? -1);
        if (values != null) writer.Write(MemoryMarshal.AsBytes(values.AsSpan()));
    }

    private static T[]? ReadArray<T>(BinaryReader reader) where T : unmanaged
    {
        int count = reader.ReadInt32();
        if (count == -1) return null;
        var values = new T[count];
        reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan()));
        return values;
    }

    private static void WriteRing(BinaryWriter writer, IReadOnlyList<double[]>? ring)
    {
        writer.Write(ring?.Count ?? -1);
        if (ring != null)
            foreach (var point in ring) WriteArray(writer, point);
    }

    private static List<double[]>? ReadRing(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count == -1) return null;
        var points = new List<double[]>(count);
        for (int i = 0; i < count; i++) points.Add(ReadArray<double>(reader)!);
        return points;
    }

    private static void WriteRings(BinaryWriter writer, IReadOnlyList<List<double[]>>? rings)
    {
        writer.Write(rings?.Count ?? -1);
        if (rings != null)
            foreach (var ring in rings) WriteRing(writer, ring);
    }

    private static List<List<double[]>>? ReadRings(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count == -1) return null;
        var rings = new List<List<double[]>>(count);
        for (int i = 0; i < count; i++) rings.Add(ReadRing(reader)!);
        return rings;
    }

    private static void WriteArcs(BinaryWriter writer, IReadOnlyList<Arc>? arcs)
    {
        writer.Write(arcs?.Count ?? -1);
        if (arcs == null) return;
        foreach (var arc in arcs)
        {
            WriteArray(writer, arc.Centre);
            WriteArray(writer, arc.X);
            WriteArray(writer, arc.Y);
            writer.Write(arc.Radius);
            writer.Write(arc.First);
            writer.Write(arc.Last);
            writer.Write(arc.Edge);
        }
    }

    private static List<Arc>? ReadArcs(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count == -1) return null;
        var arcs = new List<Arc>(count);
        for (int i = 0; i < count; i++)
            arcs.Add(new Arc(ReadArray<double>(reader)!, ReadArray<double>(reader)!, ReadArray<double>(reader)!,
                reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadUInt32()));
        return arcs;
    }

    private static void WriteArray(BinaryWriter writer, List<int>? values)
    {
        writer.Write(values?.Count ?? -1);
        if (values != null) writer.Write(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(values)));
    }
}
