using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RvtIO.Meshes;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Scene;

/// <summary>Stores triangle buffers in seekable scratch while preserving the scene's list contracts.</summary>
internal sealed class SceneMeshScratch
{
    private readonly PartitionChunks.Backing storage;
    private readonly GeometryFaceStore faces;

    internal SceneMeshScratch(Stream stream)
    {
        storage = new(stream);
        faces = new(storage);
    }

    internal void ArchiveGeometry(Element element) => faces.Archive(element);

    internal IReadOnlyList<TriangleMesh> Store(IReadOnlyList<TriangleMesh> meshes)
    {
        var stored = new StoredMesh[meshes.Count];
        for (int i = 0; i < meshes.Count; i++)
        {
            var mesh = meshes[i];
            stored[i] = new(mesh.MaterialId, Store(mesh.Positions), Store(mesh.Normals), Store(mesh.Indices));
        }
        return new MeshList(stored);
    }

    internal StoredArray<T> Store<T>(T[] values) where T : unmanaged
    {
        int pageSize = (64 * 1024) / Unsafe.SizeOf<T>();
        var pages = new List<PartitionChunks.Chunk>();
        for (int start = 0; start < values.Length;)
        {
            int length = Math.Min(values.Length - start, pageSize);
            pages.Add(storage.Add(MemoryMarshal.AsBytes(values.AsSpan(start, length))));
            start += length;
        }
        return new StoredArray<T>(storage, pages.ToArray(), values.Length, pageSize);
    }

    internal sealed class StoredArray<T>(PartitionChunks.Backing backing, PartitionChunks.Chunk[] pages,
        int count, int pageSize) : IReadOnlyList<T> where T : unmanaged
    {
        public int Count => count;

        public T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                return MemoryMarshal.Cast<byte, T>(backing.Read(pages[index / pageSize]))[index % pageSize];
            }
        }

        internal T[] ReadAll()
        {
            var result = new T[Count];
            for (int page = 0; page < pages.Length; page++)
                MemoryMarshal.Cast<byte, T>(backing.Read(pages[page])).CopyTo(result.AsSpan(page * pageSize));
            return result;
        }

        public IEnumerator<T> GetEnumerator()
        {
            foreach (var page in pages)
            {
                byte[] bytes = backing.Read(page);
                int length = bytes.Length / Unsafe.SizeOf<T>();
                for (int index = 0; index < length; index++)
                    yield return MemoryMarshal.Cast<byte, T>(bytes)[index];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private readonly record struct StoredMesh(int Material, StoredArray<double> Positions, StoredArray<double> Normals,
        StoredArray<int> Indices)
    {
        internal TriangleMesh Read() => new()
        {
            MaterialId = Material,
            Positions = Positions.ReadAll(),
            Normals = Normals.ReadAll(),
            Indices = Indices.ReadAll(),
        };
    }

    private sealed class MeshList(StoredMesh[] meshes) : IReadOnlyList<TriangleMesh>
    {
        public int Count => meshes.Length;
        public TriangleMesh this[int index] => meshes[index].Read();
        public IEnumerator<TriangleMesh> GetEnumerator() => meshes.Select(mesh => mesh.Read()).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
