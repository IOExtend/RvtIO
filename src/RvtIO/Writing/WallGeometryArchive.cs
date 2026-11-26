using System.Buffers.Binary;
using RvtIO.Parsing;

namespace RvtIO.Writing;

internal static partial class WallHeightWriter
{
    // Rebuilding the object list can move an owning pointer after its first back reference.
    private sealed class GeometryArchive
    {
        internal GeometryArchive(Archive source)
        {
            var walk = source.Walk;
            Root = new Node(source, -1, 0, walk.QueueStart[0], 0);
            var objects = walk.Queue.Select((tag, index) => new Node(source, index, walk.QueueStart[index],
                index + 1 == walk.Queue.Count ? source.Body.Length : walk.QueueStart[index + 1], checked((ushort)tag))).ToArray();
            var byHandle = walk.ArchiveReferences!.Where(r => r.QueuedObject.HasValue)
                .ToDictionary(r => r.Handle, r => objects[r.QueuedObject!.Value]);
            foreach (var r in walk.ArchiveReferences!)
            {
                if (r.Handle < 3)
                    continue;
                if (!byHandle.TryGetValue(r.Handle, out var target))
                    throw Layout();
                var owner = r.OwnerQueueIndex < 0 ? Root : objects[r.OwnerQueueIndex];
                bool owns = r.QueuedObject.HasValue;
                owner.Replace(r.Offset - owner.Start, owns ? 6 : 4, new Reference(target, owns));
            }
            if (!Build().AsSpan().SequenceEqual(source.Body))
                throw Layout();
        }

        internal Node Root { get; }

        internal byte[] Build()
        {
            var handles = new GElementArchiveHandles();
            var identities = new Dictionary<Node, uint>();
            var owned = new HashSet<Node> { Root };
            var order = new List<Node> { Root };
            using var buffer = new MemoryStream();
            using var writer = new BinaryWriter(buffer);
            for (int i = 0; i < order.Count; i++)
            {
                Node node = order[i];
                int pos = 0;
                foreach (var (offset, change) in node.Edits)
                {
                    if (offset < pos || offset > node.Length - change.Length)
                        throw Layout();
                    writer.Write(node.Bytes.Slice(pos, offset - pos));
                    writer.Write(change.Prefix);
                    foreach (var reference in change.References)
                    {
                        if (reference.Target is not { } target)
                        {
                            writer.Write(0u);
                            continue;
                        }
                        if (!identities.TryGetValue(target, out uint identity))
                            identities.Add(target, identity = (uint)identities.Count + 3);
                        writer.Write(handles.Assign(identity));
                        if (!reference.Owns)
                            continue;
                        if (!owned.Add(target))
                            throw Layout();
                        writer.Write(target.ClassTag);
                        order.Add(target);
                    }
                    pos = offset + change.Length;
                }
                writer.Write(node.Bytes[pos..]);
            }
            if (owned.Count != identities.Count + 1)
                throw Layout();
            return buffer.ToArray();
        }

        internal readonly record struct Reference(Node? Target, bool Owns = false);
        internal sealed record Change(int Length, byte[] Prefix, IReadOnlyList<Reference> References);

        internal sealed class Node(Archive source, int index, int start, int end, ushort classTag)
        {
            internal readonly SortedDictionary<int, Change> Edits = [];
            internal int Start => start;
            internal int Length => end - start;
            internal ushort ClassTag => classTag;
            internal ReadOnlySpan<byte> Bytes => source.Body.AsSpan(start, Length);

            internal Node Clone()
            {
                var clone = new Node(source, index, start, end, classTag);
                foreach (var (offset, change) in Edits)
                    clone.Edits.Add(offset, change);
                return clone;
            }

            internal int Integer(string field) => source.Int32(index, field);
            private int Offset(string field) => source.Field(index, field).ValueOffset - start;

            internal double Real(string field) => ReadReal(source.Field(index, field));

            internal (double First, double Last) Pair(string field) => source.Values(index, field).ToArray() is [var a, var b]
                ? (ReadReal(a), ReadReal(b)) : throw Layout();

            private double ReadReal(WideBlockWalker.Capture capture)
            {
                int offset = capture.ValueOffset - start;
                if (capture.Kind != WideBlockWalker.CaptureKind.Real || offset < 0 || offset > Length - sizeof(double))
                    throw Layout();
                double value = BinaryPrimitives.ReadDoubleLittleEndian(Bytes[offset..]);
                return double.IsFinite(value) ? value : throw Layout();
            }

            internal Node? Ref(string field) => Edits.TryGetValue(Offset(field), out var change)
                && change is { Prefix.Length: 0, References.Count: 1 } ? change.References[0].Target : null;

            internal Node Target(string field) => Ref(field) ?? throw Layout();

            internal Node[] OwnedList(string field) => Enumerable.Range(0, Integer(field + "#"))
                .Select(i => Target($"{field}@{i}")).ToArray();

            internal void Replace(int offset, int length, Reference reference) => Edits[offset] = new(length, [], [reference]);

            internal void SetWeak(string field, Node? target) => Replace(Offset(field), 4, new(target));

            internal void SetOwned(string field, Node? target)
            {
                int offset = Offset(field);
                int length = BinaryPrimitives.ReadUInt32LittleEndian(Bytes[offset..]) == 0 ? 4 : 6;
                Replace(offset, length, new(target, true));
            }

            internal void ReplaceOwnedList(string field, IReadOnlyList<Node> targets)
            {
                int startOffset = Offset(field + "#");
                int oldCount = Integer(field + "#");
                for (int i = 0; i < oldCount; i++)
                    if (Offset($"{field}@{i}") != startOffset + 4 + 6 * i || !Edits.Remove(startOffset + 4 + 6 * i))
                        throw Layout();
                Edits[startOffset] = new(4 + 6 * oldCount, IntBytes(targets.Count), targets.Select(n => new Reference(n, true)).ToArray());
            }

            internal void SetTag(int tag) => Edits[Offset("GInfo.m_tag")] = new(4, IntBytes(tag), []);

            internal void SetIntersectionFace(int style, int idWidth, bool guide)
            {
                int flagsAt = Offset("GFace.m_pFirstLoop") - sizeof(int);
                int sideFlagsAt = Offset("GFace.m_cutType") + sizeof(int);
                int styleAt = Offset("GFace.m_renderStyleId");
                if (flagsAt < 0 || flagsAt > Length - sizeof(int) || sideFlagsAt < 0 || sideFlagsAt > Length - sizeof(int)
                    || styleAt < 0 || styleAt > Length - idWidth
                    || BinaryPrimitives.ReadInt32LittleEndian(Bytes[flagsAt..]) != Integer("GInfo.m_flags")
                    || BinaryPrimitives.ReadInt32LittleEndian(Bytes[sideFlagsAt..]) != 4
                    || Integer("GFace.m_faceFlags_v9") != 4 || Integer("GFace.m_renderStyleId") != -1
                    || Integer("GFace.m_oBackgroundFilling") != 0 || Ref("GFace.m_pGFilling") is not null)
                    throw Layout();
                // The two guide planes retain geometry tags but have no loops or fill in the wall solid.
                Edits[flagsAt] = new(sizeof(int), IntBytes(guide ? 0x8204 : 0x88004), []);
                Edits[sideFlagsAt] = new(sizeof(int), IntBytes(6), []);
                byte[] identifier = new byte[idWidth];
                if (idWidth == sizeof(int)) BinaryPrimitives.WriteInt32LittleEndian(identifier, style);
                else BinaryPrimitives.WriteInt64LittleEndian(identifier, style);
                Edits[styleAt] = new(idWidth, identifier, []);
            }

            internal void SetHeadEdgeActive(bool active)
            {
                int offset = Offset("GEdge.m_flags");
                int flags = Integer("GEdge.m_flags");
                if (offset != Length - 1 || flags is not (6 or 7) || active && flags != 6)
                    throw Layout();
                SetEdgeFlags(active ? (byte)7 : (byte)6);
            }

            internal void SetEdgeFlags(byte flags)
            {
                int offset = Offset("GEdge.m_flags");
                if (offset != Length - 1 || Integer("GEdge.m_flags") is not (6 or 7) || flags is not (6 or 7))
                    throw Layout();
                Edits[offset] = new(1, [flags], []);
            }

            internal void SetMirroredCutFill()
            {
                int offset = Offset("FillPatternPlacer.m_isMirrored");
                if (Bytes[offset] is not (0 or 1))
                    throw Layout();
                Edits[offset] = new(1, [1], []);
            }

            internal void SetReal(string field, double value) => SetReal(source.Field(index, field), value);

            internal void SetPair(string field, double first, double last)
            {
                if (source.Values(index, field).ToArray() is not [var a, var b])
                    throw Layout();
                SetReal(a, first);
                SetReal(b, last);
            }

            private void SetReal(WideBlockWalker.Capture capture, double value)
            {
                if (capture.Kind != WideBlockWalker.CaptureKind.Real || !double.IsFinite(value))
                    throw Layout();
                byte[] bytes = new byte[sizeof(double)];
                BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
                Edits[capture.ValueOffset - start] = new(bytes.Length, bytes, []);
            }
        }
    }

    private static byte[] IntBytes(int value)
    {
        byte[] bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }
}
