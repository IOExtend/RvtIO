using System.Runtime.InteropServices;
using RvtIO.Model;

namespace RvtIO.Geometry;

/// <summary>Validates and orients faceted shells on the metric lattice used for IFC serialization.</summary>
internal static class FacetedShell
{
    private const double FeetToMetres = 0.3048;
    internal const double IfcGeometryPrecisionMetres = 1.0e-5;

    internal static (long X, long Y, long Z) IfcPrecisionKeyFeet(double[] point) =>
        (IfcPrecisionBinMetres(point[0] * FeetToMetres),
            IfcPrecisionBinMetres(point[1] * FeetToMetres),
            IfcPrecisionBinMetres(point[2] * FeetToMetres));

    internal static (long X, long Y, long Z) IfcPrecisionKeyMetres(
        double x, double y, double z) =>
        (IfcPrecisionBinMetres(x), IfcPrecisionBinMetres(y), IfcPrecisionBinMetres(z));

    internal static long IfcPrecisionBinMetres(double value) => checked((long)Math.Round(
        value / IfcGeometryPrecisionMetres, MidpointRounding.ToEven));

    internal static bool FaceCollapsesAtIfcPrecision(GeoFace face)
    {
        if (face.OuterVertices is not { Count: >= 3 } source
            || source.Any(point => point.Length < 3
                || !double.IsFinite(point[0]) || !double.IsFinite(point[1])
                || !double.IsFinite(point[2])))
            return false;
        var vertices = new List<(long X, long Y, long Z)>();
        foreach (var point in source)
        {
            var key = IfcPrecisionKeyFeet(point);
            if (vertices.Count == 0 || vertices[^1] != key) vertices.Add(key);
        }
        if (vertices.Count > 1 && vertices[0] == vertices[^1]) vertices.RemoveAt(vertices.Count - 1);
        if (vertices.Count < 3) return true;
        var first = vertices[0];
        for (int index = 1; index + 1 < vertices.Count; index++)
        {
            var second = vertices[index];
            var third = vertices[index + 1];
            var ux = (System.Numerics.BigInteger)second.X - first.X;
            var uy = (System.Numerics.BigInteger)second.Y - first.Y;
            var uz = (System.Numerics.BigInteger)second.Z - first.Z;
            var vx = (System.Numerics.BigInteger)third.X - first.X;
            var vy = (System.Numerics.BigInteger)third.Y - first.Y;
            var vz = (System.Numerics.BigInteger)third.Z - first.Z;
            if (!(uy * vz - uz * vy).IsZero
                || !(uz * vx - ux * vz).IsZero
                || !(ux * vy - uy * vx).IsZero)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Orients every face of one closed component consistently and then chooses the outward global
    /// orientation from its signed volume.
    /// </summary>
    internal static bool TryOrientExactFacetedShell(IReadOnlyList<GeoFace> faces,
        IReadOnlyCollection<int> faceIndices, out List<GeoFace> orientedFaces,
        bool triangulateNonPlanarFaces, HashSet<(long X, long Y, long Z)>? splitVertices = null) =>
        TryOrientExactFacetedShell(faces, faceIndices, out orientedFaces, out _, triangulateNonPlanarFaces, splitVertices);

    internal static bool TryOrientExactFacetedShell(IReadOnlyList<GeoFace> faces,
        IReadOnlyCollection<int> faceIndices, out List<GeoFace> orientedFaces, out double volume,
        bool triangulateNonPlanarFaces, HashSet<(long X, long Y, long Z)>? splitVertices = null)
    {
        orientedFaces = [];
        volume = 0;
        var indices = faceIndices.Distinct().Order().ToList();
        if (indices.Count < 4 || indices.Any(index => (uint)index >= faces.Count))
            return false;
        var normalizedFaces = new Dictionary<int, GeoFace>();
        long edgeUseCount = 0;
        foreach (int faceIndex in indices)
        {
            var normalized = CloneFace(faces[faceIndex]);
            if (normalized.OuterVertices is not { Count: >= 3 } outer
                || outer.Any(point => point.Length < 3
                    || !double.IsFinite(point[0]) || !double.IsFinite(point[1])
                    || !double.IsFinite(point[2])))
                return false;
            normalized.OuterVertices = CleanPrecisionLoop(outer);
            if (normalized.OuterVertices.Count < 3
                || FaceCollapsesAtIfcPrecision(normalized))
                return false;
            normalized.InnerVertices = normalized.InnerVertices?
                .Where(ring => ring.Count >= 3 && ring.All(point => point.Length >= 3
                    && double.IsFinite(point[0]) && double.IsFinite(point[1])
                    && double.IsFinite(point[2])))
                .Select(CleanPrecisionLoop)
                .Where(ring => ring.Count >= 3
                    && !FaceCollapsesAtIfcPrecision(new GeoFace
                        { OuterVertices = ring }))
                .ToList();
            if (!TryNormalizeInnerBounds(normalized))
                return false;
            edgeUseCount += normalized.OuterVertices.Count;
            if (normalized.InnerVertices is { } holes)
                foreach (var ring in holes)
                    edgeUseCount += ring.Count;
            normalizedFaces[faceIndex] = normalized;
        }
        // In a closed shell with matched loops, two incidences share each edge.
        long closedEdgeCount = edgeUseCount / 2;
        int initialCapacity = closedEdgeCount <= int.MaxValue ? (int)closedEdgeCount : 0;
        if (!TryGetFacetedAtomicEdgeUses(normalizedFaces, indices, out var edgeUses, initialCapacity, splitVertices))
            return false;

        var constraints = indices.ToDictionary(index => index,
            _ => new List<(int Other, bool OppositeFlip)>());
        foreach (var uses in edgeUses.Values)
        {
            bool oppositeFlip = uses.First.Direction == uses.Second.Direction;
            constraints[uses.First.FaceIndex].Add((uses.Second.FaceIndex, oppositeFlip));
            constraints[uses.Second.FaceIndex].Add((uses.First.FaceIndex, oppositeFlip));
        }

        var flips = new Dictionary<int, bool>();
        foreach (int seed in indices)
        {
            if (flips.ContainsKey(seed)) continue;
            flips[seed] = false;
            var queue = new Queue<int>();
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (var (other, oppositeFlip) in constraints[current])
                {
                    bool required = flips[current] ^ oppositeFlip;
                    if (flips.TryGetValue(other, out bool existing))
                    {
                        if (existing != required)
                            return false;
                    }
                    else
                    {
                        flips[other] = required;
                        queue.Enqueue(other);
                    }
                }
            }
        }

        orientedFaces = new List<GeoFace>(indices.Count);
        foreach (int index in indices)
        {
            var face = normalizedFaces[index];
            if (flips[index]) ReverseFace(face, detachVertices: false);
            orientedFaces.Add(face);
        }
        SplitFacetedLoopsAtSharedVertices(orientedFaces);
        var compactIndices = Enumerable.Range(0, orientedFaces.Count).ToList();
        if (!IsExactFacetedShell(orientedFaces, compactIndices, edgeUses, splitVertices))
        {
            orientedFaces = [];
            return false;
        }

        var points = orientedFaces.SelectMany(face =>
                (face.OuterVertices ?? []).Concat((face.InnerVertices ?? []).SelectMany(ring => ring)))
            .ToList();
        double[] origin =
        [
            points.Average(point => point[0]),
            points.Average(point => point[1]),
            points.Average(point => point[2]),
        ];
        double signedVolume = orientedFaces.Sum(face =>
            SignedFaceVolume(face.OuterVertices!, origin)
            + (face.InnerVertices?.Sum(ring => SignedFaceVolume(ring, origin)) ?? 0));
        if (!double.IsFinite(signedVolume) || signedVolume == 0)
        {
            orientedFaces = [];
            return false;
        }
        if (signedVolume < 0)
            foreach (var face in orientedFaces)
                ReverseFace(face, detachVertices: true);
        volume = Math.Abs(signedVolume);
        if (!triangulateNonPlanarFaces)
            return true;
        var triangulatedFaces = TriangulateNonPlanarFacetedFaces(orientedFaces);
        SplitFacetedLoopsAtSharedVertices(triangulatedFaces);
        if (IsExactFacetedShell(triangulatedFaces,
                Enumerable.Range(0, triangulatedFaces.Count).ToList(), edgeUses, splitVertices))
            orientedFaces = triangulatedFaces;
        return true;

        static double SignedFaceVolume(IReadOnlyList<double[]> ring, double[] origin)
        {
            double volume = 0;
            var first = ring[0];
            for (int index = 1; index + 1 < ring.Count; index++)
            {
                double ax = first[0] - origin[0], ay = first[1] - origin[1], az = first[2] - origin[2];
                double bx = ring[index][0] - origin[0], by = ring[index][1] - origin[1], bz = ring[index][2] - origin[2];
                double cx = ring[index + 1][0] - origin[0], cy = ring[index + 1][1] - origin[1], cz = ring[index + 1][2] - origin[2];
                volume += (ax * (by * cz - bz * cy) - ay * (bx * cz - bz * cx)
                    + az * (bx * cy - by * cx)) / 6;
            }
            return volume;
        }

        static bool TryNormalizeInnerBounds(GeoFace face)
        {
            if (face.InnerVertices is not { Count: > 0 }) return true;
            if (face.OuterVertices is not { Count: >= 3 } outer
                || !TryNewellNormal(outer, out var outerNormal))
                return false;
            foreach (var inner in face.InnerVertices)
            {
                if (inner.Count < 3 || !TryNewellNormal(inner, out var innerNormal))
                    return false;
                double dot = outerNormal.X * innerNormal.X
                    + outerNormal.Y * innerNormal.Y + outerNormal.Z * innerNormal.Z;
                if (!double.IsFinite(dot) || dot == 0) return false;
                bool isStrictlyContained = IsStrictlyContainedAtIfcPrecision(
                    inner, outer, outerNormal);
                bool hasSameDirection = dot > 0;
                if (isStrictlyContained == hasSameDirection) inner.Reverse();
            }
            return true;
        }

        static bool IsStrictlyContainedAtIfcPrecision(IReadOnlyList<double[]> inner,
            IReadOnlyList<double[]> outer, (double X, double Y, double Z) outerNormal)
        {
            int droppedAxis = Math.Abs(outerNormal.X) >= Math.Abs(outerNormal.Y)
                && Math.Abs(outerNormal.X) >= Math.Abs(outerNormal.Z) ? 0
                : Math.Abs(outerNormal.Y) >= Math.Abs(outerNormal.Z) ? 1 : 2;
            static (long U, long V) Project(double[] point, int droppedAxis)
            {
                var key = IfcPrecisionKeyFeet(point);
                return droppedAxis == 0 ? (key.Y, key.Z)
                    : droppedAxis == 1 ? (key.X, key.Z) : (key.X, key.Y);
            }
            static Int128 Orientation((long U, long V) first, (long U, long V) second,
                (long U, long V) point) =>
                ((Int128)second.U - first.U) * ((Int128)point.V - first.V)
                - ((Int128)second.V - first.V) * ((Int128)point.U - first.U);
            static bool OnSegment((long U, long V) point, (long U, long V) first,
                (long U, long V) second) => Orientation(first, second, point) == 0
                && point.U >= Math.Min(first.U, second.U)
                && point.U <= Math.Max(first.U, second.U)
                && point.V >= Math.Min(first.V, second.V)
                && point.V <= Math.Max(first.V, second.V);
            static bool StrictlyInside((long U, long V) point,
                IReadOnlyList<(long U, long V)> polygon)
            {
                int winding = 0;
                for (int index = 0; index < polygon.Count; index++)
                {
                    var first = polygon[index];
                    var second = polygon[(index + 1) % polygon.Count];
                    Int128 orientation = Orientation(first, second, point);
                    if (orientation == 0 && OnSegment(point, first, second)) return false;
                    if (first.V <= point.V && point.V < second.V && orientation > 0) winding++;
                    else if (second.V <= point.V && point.V < first.V && orientation < 0) winding--;
                }
                return winding != 0;
            }
            static bool Intersects((long U, long V) firstA, (long U, long V) secondA,
                (long U, long V) firstB, (long U, long V) secondB)
            {
                Int128 ab1 = Orientation(firstA, secondA, firstB);
                Int128 ab2 = Orientation(firstA, secondA, secondB);
                Int128 ba1 = Orientation(firstB, secondB, firstA);
                Int128 ba2 = Orientation(firstB, secondB, secondA);
                if (ab1 == 0 && OnSegment(firstB, firstA, secondA)) return true;
                if (ab2 == 0 && OnSegment(secondB, firstA, secondA)) return true;
                if (ba1 == 0 && OnSegment(firstA, firstB, secondB)) return true;
                if (ba2 == 0 && OnSegment(secondA, firstB, secondB)) return true;
                return (ab1 < 0) != (ab2 < 0) && (ba1 < 0) != (ba2 < 0);
            }

            var outer2d = outer.Select(point => Project(point, droppedAxis)).ToList();
            var inner2d = inner.Select(point => Project(point, droppedAxis)).ToList();
            if (outer2d.Distinct().Count() < 3 || inner2d.Distinct().Count() < 3
                || inner2d.Any(point => !StrictlyInside(point, outer2d)))
                return false;
            for (int innerIndex = 0; innerIndex < inner2d.Count; innerIndex++)
                for (int outerIndex = 0; outerIndex < outer2d.Count; outerIndex++)
                    if (Intersects(inner2d[innerIndex], inner2d[(innerIndex + 1) % inner2d.Count],
                            outer2d[outerIndex], outer2d[(outerIndex + 1) % outer2d.Count]))
                        return false;
            return true;
        }

        static bool TryNewellNormal(IReadOnlyList<double[]> ring,
            out (double X, double Y, double Z) normal)
        {
            double x = 0, y = 0, z = 0;
            for (int index = 0; index < ring.Count; index++)
            {
                var current = ring[index];
                var next = ring[(index + 1) % ring.Count];
                if (current.Length < 3 || next.Length < 3
                    || !double.IsFinite(current[0]) || !double.IsFinite(current[1])
                    || !double.IsFinite(current[2]) || !double.IsFinite(next[0])
                    || !double.IsFinite(next[1]) || !double.IsFinite(next[2]))
                {
                    normal = default;
                    return false;
                }
                x += (current[1] - next[1]) * (current[2] + next[2]);
                y += (current[2] - next[2]) * (current[0] + next[0]);
                z += (current[0] - next[0]) * (current[1] + next[1]);
            }
            normal = (x, y, z);
            return double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z)
                && (x != 0 || y != 0 || z != 0);
        }

        static List<double[]> CleanPrecisionLoop(List<double[]> ring)
        {
            var first = IfcPrecisionKeyFeet(ring[0]);
            var last = first;
            int count = 1;
            for (int index = 1; index < ring.Count; index++)
            {
                var point = ring[index];
                var key = IfcPrecisionKeyFeet(point);
                if (last == key) continue;
                if (count != index) ring[count] = point;
                count++;
                last = key;
            }
            if (count > 1 && first == last) count--;
            ring.RemoveRange(count, ring.Count - count);
            return ring;
        }

        static void ReverseFace(GeoFace face, bool detachVertices)
        {
            ReverseRing(face.OuterVertices!);
            if (face.InnerVertices is { } holes)
                foreach (var ring in holes)
                    ReverseRing(ring);
            var normal = face.Normal;
            for (int i = 0; i < normal.Length; i++)
                normal[i] = -normal[i];

            void ReverseRing(List<double[]> ring)
            {
                ring.Reverse();
                // Splitting can insert a vertex array owned by a different face.
                if (detachVertices)
                    for (int i = 0; i < ring.Count; i++)
                        ring[i] = (double[])ring[i].Clone();
            }
        }

        static GeoFace CloneFace(GeoFace source)
        {
            static List<double[]> CopyRing(List<double[]> ring) =>
                ring.Select(point => (double[])point.Clone()).ToList();

            return new GeoFace
            {
                Origin = (double[])source.Origin.Clone(),
                XVector = (double[])source.XVector.Clone(),
                YVector = (double[])source.YVector.Clone(),
                Normal = (double[])source.Normal.Clone(),
                Envelope = source.Envelope is null ? null : (double[])source.Envelope.Clone(),
                Area = source.Area,
                InnerLoops = source.InnerLoops?.Select(loop => (double[])loop.Clone()).ToList(),
                OuterVertices = source.OuterVertices is null ? null : CopyRing(source.OuterVertices),
                InnerVertices = source.InnerVertices?.Select(CopyRing).ToList(),
                AlternativeOuterVertices = source.AlternativeOuterVertices is null ? null
                    : source.AlternativeOuterVertices.Select(point => (double[])point.Clone()).ToList(),
                EdgeIndices = source.EdgeIndices?.ToList(),
            };
        }
    }

    /// <summary>
    /// Converts precision-nonplanar polygon faces to triangles using exact projected ear clipping.
    /// </summary>
    private static List<GeoFace> TriangulateNonPlanarFacetedFaces(
        IReadOnlyList<GeoFace> faces)
    {

        static GeoFace Triangle(GeoFace source, params double[][] vertices) => new()
        {
            Origin = (double[])source.Origin.Clone(),
            XVector = (double[])source.XVector.Clone(),
            YVector = (double[])source.YVector.Clone(),
            Normal = (double[])source.Normal.Clone(),
            Envelope = source.Envelope is null ? null : (double[])source.Envelope.Clone(),
            Area = source.Area,
            InnerLoops = source.InnerLoops?.Select(loop => (double[])loop.Clone()).ToList(),
            OuterVertices = vertices.Select(point => (double[])point.Clone()).ToList(),
            EdgeIndices = source.EdgeIndices?.ToList(),
        };
        static bool TryProjectedShape(IReadOnlyList<double[]> ring,
            out bool planar, out bool convex)
        {
            planar = false;
            convex = false;
            var points = ring.Select(IfcPrecisionKeyFeet).ToList();
            if (points.Count < 4 || points.Distinct().Count() != points.Count) return false;
            System.Numerics.BigInteger nx = 0, ny = 0, nz = 0;
            for (int index = 0; index < points.Count; index++)
            {
                var current = points[index];
                var next = points[(index + 1) % points.Count];
                nx += ((System.Numerics.BigInteger)current.Y - next.Y) * (current.Z + next.Z);
                ny += ((System.Numerics.BigInteger)current.Z - next.Z) * (current.X + next.X);
                nz += ((System.Numerics.BigInteger)current.X - next.X) * (current.Y + next.Y);
            }
            if (nx.IsZero && ny.IsZero && nz.IsZero) return false;
            var absX = System.Numerics.BigInteger.Abs(nx);
            var absY = System.Numerics.BigInteger.Abs(ny);
            var absZ = System.Numerics.BigInteger.Abs(nz);
            int droppedAxis = absX >= absY && absX >= absZ ? 0 : absY >= absZ ? 1 : 2;
            static (long U, long V) Project((long X, long Y, long Z) point, int axis) =>
                axis == 0 ? (point.Y, point.Z)
                    : axis == 1 ? (point.X, point.Z) : (point.X, point.Y);
            int sign = 0;
            for (int index = 0; index < points.Count; index++)
            {
                var first = Project(points[index], droppedAxis);
                var second = Project(points[(index + 1) % points.Count], droppedAxis);
                var third = Project(points[(index + 2) % points.Count], droppedAxis);
                var turn = ((System.Numerics.BigInteger)second.U - first.U)
                    * (third.V - second.V)
                    - ((System.Numerics.BigInteger)second.V - first.V)
                    * (third.U - second.U);
                if (turn.IsZero) continue;
                int currentSign = turn.Sign;
                if (sign != 0 && currentSign != sign) return true;
                sign = currentSign;
            }
            convex = sign != 0;

            var origin = points[0];
            (System.Numerics.BigInteger X, System.Numerics.BigInteger Y,
                System.Numerics.BigInteger Z)? normal = null;
            for (int index = 1; index + 1 < points.Count && normal == null; index++)
            {
                var first = points[index];
                var second = points[index + 1];
                var ux = (System.Numerics.BigInteger)first.X - origin.X;
                var uy = (System.Numerics.BigInteger)first.Y - origin.Y;
                var uz = (System.Numerics.BigInteger)first.Z - origin.Z;
                var vx = (System.Numerics.BigInteger)second.X - origin.X;
                var vy = (System.Numerics.BigInteger)second.Y - origin.Y;
                var vz = (System.Numerics.BigInteger)second.Z - origin.Z;
                var cross = (X: uy * vz - uz * vy, Y: uz * vx - ux * vz,
                    Z: ux * vy - uy * vx);
                if (!cross.X.IsZero || !cross.Y.IsZero || !cross.Z.IsZero)
                    normal = cross;
            }
            if (normal == null) return false;
            planar = points.All(point =>
                normal.Value.X * ((System.Numerics.BigInteger)point.X - origin.X)
                + normal.Value.Y * ((System.Numerics.BigInteger)point.Y - origin.Y)
                + normal.Value.Z * ((System.Numerics.BigInteger)point.Z - origin.Z) == 0);
            return true;
        }

        static List<GeoFace>? EarClip(GeoFace face, IReadOnlyList<double[]> ring)
        {
            var points = ring.Select(IfcPrecisionKeyFeet).ToList();
            System.Numerics.BigInteger nx = 0, ny = 0, nz = 0;
            for (int index = 0; index < points.Count; index++)
            {
                var current = points[index];
                var next = points[(index + 1) % points.Count];
                nx += ((System.Numerics.BigInteger)current.Y - next.Y) * (current.Z + next.Z);
                ny += ((System.Numerics.BigInteger)current.Z - next.Z) * (current.X + next.X);
                nz += ((System.Numerics.BigInteger)current.X - next.X) * (current.Y + next.Y);
            }
            var absX = System.Numerics.BigInteger.Abs(nx);
            var absY = System.Numerics.BigInteger.Abs(ny);
            var absZ = System.Numerics.BigInteger.Abs(nz);
            if (absX.IsZero && absY.IsZero && absZ.IsZero) return null;
            int droppedAxis = absX >= absY && absX >= absZ ? 0 : absY >= absZ ? 1 : 2;
            static (long U, long V) Project((long X, long Y, long Z) point, int axis) =>
                axis == 0 ? (point.Y, point.Z)
                    : axis == 1 ? (point.X, point.Z) : (point.X, point.Y);
            var projected = points.Select(point => Project(point, droppedAxis)).ToList();
            static System.Numerics.BigInteger Turn((long U, long V) first,
                (long U, long V) second, (long U, long V) third) =>
                ((System.Numerics.BigInteger)second.U - first.U) * (third.V - first.V)
                - ((System.Numerics.BigInteger)second.V - first.V) * (third.U - first.U);
            System.Numerics.BigInteger twiceArea = 0;
            for (int index = 0; index < projected.Count; index++)
            {
                var current = projected[index];
                var next = projected[(index + 1) % projected.Count];
                twiceArea += (System.Numerics.BigInteger)current.U * next.V
                    - (System.Numerics.BigInteger)next.U * current.V;
            }
            if (twiceArea.IsZero) return null;
            int orientation = twiceArea.Sign;
            var remaining = Enumerable.Range(0, ring.Count).ToList();
            var result = new List<GeoFace>();
            int guard = ring.Count * ring.Count;
            while (remaining.Count > 3 && guard-- > 0)
            {
                bool removed = false;
                for (int offset = 0; offset < remaining.Count; offset++)
                {
                    int previous = remaining[(offset - 1 + remaining.Count) % remaining.Count];
                    int current = remaining[offset];
                    int next = remaining[(offset + 1) % remaining.Count];
                    var turn = Turn(projected[previous], projected[current], projected[next]);
                    if (turn.Sign != orientation) continue;
                    bool contains = remaining.Any(candidate => candidate != previous
                        && candidate != current && candidate != next
                        && new[]
                        {
                            Turn(projected[previous], projected[current], projected[candidate]),
                            Turn(projected[current], projected[next], projected[candidate]),
                            Turn(projected[next], projected[previous], projected[candidate]),
                        }.All(value => value.IsZero || value.Sign == orientation));
                    if (contains) continue;
                    var triangle = Triangle(face,
                        ring[previous], ring[current], ring[next]);
                    if (FaceCollapsesAtIfcPrecision(triangle)) continue;
                    result.Add(triangle);
                    remaining.RemoveAt(offset);
                    removed = true;
                    break;
                }
                if (!removed) return null;
            }
            if (remaining.Count != 3) return null;
            var finalTriangle = Triangle(face,
                ring[remaining[0]], ring[remaining[1]], ring[remaining[2]]);
            if (FaceCollapsesAtIfcPrecision(finalTriangle)) return null;
            result.Add(finalTriangle);
            return result;
        }

        var result = new List<GeoFace>();
        foreach (var face in faces)
        {
            if (face.OuterVertices is not { Count: > 3 } ring
                || face.InnerVertices is { Count: > 0 }
                || !TryProjectedShape(ring, out bool planar, out _)
                || planar)
            {
                result.Add(face);
                continue;
            }
            result.AddRange(EarClip(face, ring) ?? [face]);
        }
        return result;
    }

    internal static (long X, long Y, long Z)[][] BuildVertexAxisIndex(
        ICollection<(long X, long Y, long Z)> vertices)
    {
        var byX = vertices.ToArray();
        var byY = ((long X, long Y, long Z)[])byX.Clone();
        var byZ = ((long X, long Y, long Z)[])byX.Clone();
        Array.Sort(byX);
        Array.Sort(byY, static (a, b) => (a.Y, a.X, a.Z).CompareTo((b.Y, b.X, b.Z)));
        Array.Sort(byZ, static (a, b) => (a.Z, a.X, a.Y).CompareTo((b.Z, b.X, b.Y)));
        return [byX, byY, byZ];
    }

    private static long Axis((long X, long Y, long Z) point, int axis) =>
        axis == 0 ? point.X : axis == 1 ? point.Y : point.Z;

    private static int AxisBound((long X, long Y, long Z)[] points, int axis, long value, bool afterEqual)
    {
        int low = 0, high = points.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            long coordinate = Axis(points[middle], axis);
            if (coordinate < value || (afterEqual && coordinate == value)) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    internal readonly struct LatticeSegment
    {
        private readonly (long X, long Y, long Z) _first;
        private readonly (long X, long Y, long Z) _min;
        private readonly (long X, long Y, long Z) _max;
        private readonly Int128 _dx, _dy, _dz;
        private readonly bool _axisAligned;
        private readonly bool _fitsLongProducts;

        internal int DominantAxis { get; }

        internal LatticeSegment((long X, long Y, long Z) first, (long X, long Y, long Z) second)
        {
            _first = first;
            _min = (Math.Min(first.X, second.X), Math.Min(first.Y, second.Y), Math.Min(first.Z, second.Z));
            _max = (Math.Max(first.X, second.X), Math.Max(first.Y, second.Y), Math.Max(first.Z, second.Z));
            _dx = (Int128)second.X - first.X;
            _dy = (Int128)second.Y - first.Y;
            _dz = (Int128)second.Z - first.Z;
            Int128 absX = Int128.Abs(_dx), absY = Int128.Abs(_dy), absZ = Int128.Abs(_dz);
            DominantAxis = absX >= absY && absX >= absZ ? 0 : absY >= absZ ? 1 : 2;
            _axisAligned = first.X == second.X
                ? first.Y == second.Y || first.Z == second.Z
                : first.Y == second.Y && first.Z == second.Z;
            _fitsLongProducts = absX <= int.MaxValue && absY <= int.MaxValue && absZ <= int.MaxValue;
        }

        internal bool OnSegment((long X, long Y, long Z) point)
        {
            if (point.X < _min.X || point.X > _max.X || point.Y < _min.Y || point.Y > _max.Y
                || point.Z < _min.Z || point.Z > _max.Z)
                return false;
            if (_axisAligned) return true;
            if (_fitsLongProducts)
            {
                // The bounds also limit each point offset to the signed 32-bit span.
                long x = point.X - _first.X, y = point.Y - _first.Y, z = point.Z - _first.Z;
                return (long)_dy * z == (long)_dz * y
                    && (long)_dz * x == (long)_dx * z
                    && (long)_dx * y == (long)_dy * x;
            }
            Int128 px = (Int128)point.X - _first.X;
            Int128 py = (Int128)point.Y - _first.Y;
            Int128 pz = (Int128)point.Z - _first.Z;
            return _dy * pz == _dz * py && _dz * px == _dx * pz && _dx * py == _dy * px;
        }
    }

    /// <summary>
    /// Materializes the same atomic edge partition used by shell validation in the actual polygon
    /// loops written to IFC.
    /// </summary>
    private static void SplitFacetedLoopsAtSharedVertices(IReadOnlyList<GeoFace> faces)
    {
        var representative = new Dictionary<(long X, long Y, long Z), double[]>();
        foreach (var face in faces)
            foreach (var ring in (face.OuterVertices is { Count: > 0 } outer
                    ? new[] { outer }
                    : Enumerable.Empty<List<double[]>>())
                .Concat(face.InnerVertices ?? []))
                foreach (var point in ring)
                    representative.TryAdd(IfcPrecisionKeyFeet(point), point);
        var verticesByAxis = BuildVertexAxisIndex(representative.Keys);

        List<double[]> Split(IReadOnlyList<double[]> ring)
        {
            var result = new List<double[]>(ring.Count);
            if (ring.Count == 0) return result;
            var start = IfcPrecisionKeyFeet(ring[0]);
            var second = start;
            var last = start;
            for (int edgeIndex = 0; edgeIndex < ring.Count; edgeIndex++)
            {
                var first = second;
                int nextIndex = edgeIndex + 1;
                second = nextIndex < ring.Count ? IfcPrecisionKeyFeet(ring[nextIndex]) : start;
                Append(ring[edgeIndex], first);
                if (first == second) continue;
                var segment = new LatticeSegment(first, second);
                int axis = segment.DominantAxis;
                long from = Axis(first, axis), to = Axis(second, axis);
                bool forward = from < to;
                var sorted = verticesByAxis[axis];
                int index = AxisBound(sorted, axis, from, afterEqual: !forward) - (forward ? 0 : 1);
                int step = forward ? 1 : -1;
                // Along a segment the dominant coordinate uniquely orders its collinear vertices.
                for (; (uint)index < (uint)sorted.Length; index += step)
                {
                    var point = sorted[index];
                    long coordinate = Axis(point, axis);
                    if (forward ? coordinate > to : coordinate < to) break;
                    if (point != first && point != second && segment.OnSegment(point))
                        Append(representative[point], point);
                }
            }
            if (result.Count > 1 && start == last) result.RemoveAt(result.Count - 1);
            return result;

            void Append(double[] point, (long X, long Y, long Z) key)
            {
                if (result.Count != 0 && last == key) return;
                result.Add(point);
                last = key;
            }
        }

        foreach (var face in faces)
        {
            if (face.OuterVertices is { Count: >= 3 } outer)
                face.OuterVertices = Split(outer);
            if (face.InnerVertices is { Count: > 0 } inner)
                face.InnerVertices = inner.Select(Split).ToList();
        }
    }

    internal struct EdgeUses
    {
        private List<(int FaceIndex, int Direction)>? _overflow;
        internal (int FaceIndex, int Direction) First { get; private set; }
        internal (int FaceIndex, int Direction) Second { get; private set; }

        // Valid directions are ±1, leaving zero for an unused slot.
        internal readonly int Count => Second.Direction != 0
            ? 2 + (_overflow?.Count ?? 0)
            : First.Direction != 0 ? 1 : 0;

        internal readonly (int FaceIndex, int Direction) this[int index] => index switch
        {
            0 when First.Direction != 0 => First,
            1 when Second.Direction != 0 => Second,
            >= 2 when _overflow != null => _overflow[index - 2],
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

        internal void Add((int FaceIndex, int Direction) use)
        {
            if (First.Direction == 0) First = use;
            else if (Second.Direction == 0) Second = use;
            else (_overflow ??= []).Add(use);
        }
    }

    /// <summary>Builds exact shared-edge uses on the serialized IFC coordinate lattice.</summary>
    internal static bool TryGetFacetedAtomicEdgeUses(IReadOnlyDictionary<int, GeoFace> faces,
        IReadOnlyCollection<int> faceIndices,
        out Dictionary<((long X, long Y, long Z) A, (long X, long Y, long Z) B), EdgeUses> edgeUses,
        int initialCapacity = 0, HashSet<(long X, long Y, long Z)>? splitVertices = null)
    {
        edgeUses = new(initialCapacity);
        return TryGetFacetedAtomicEdgeUses(faces, faceIndices, edgeUses, splitVertices);
    }

    private static bool TryGetFacetedAtomicEdgeUses(IReadOnlyDictionary<int, GeoFace> faces,
        IReadOnlyCollection<int> faceIndices,
        Dictionary<((long X, long Y, long Z) A, (long X, long Y, long Z) B), EdgeUses> usesByEdge,
        HashSet<(long X, long Y, long Z)>? splitVertices)
    {
        usesByEdge.Clear();
        (long X, long Y, long Z)[][]? verticesByAxis = null;
        if (!BuildEdgeUses())
        {
            usesByEdge.Clear();
            return false;
        }
        if (usesByEdge.Count == 0) return false;
        if (HasClosedMultiplicity()) return true;

        var vertices = splitVertices ?? new HashSet<(long X, long Y, long Z)>();
        vertices.Clear();
        foreach (var (first, second) in usesByEdge.Keys)
        {
            vertices.Add(first);
            vertices.Add(second);
        }
        verticesByAxis = BuildVertexAxisIndex(vertices);
        usesByEdge.Clear();
        return BuildEdgeUses() && HasClosedMultiplicity();

        void AddUse(int faceIndex, (long X, long Y, long Z) first,
            (long X, long Y, long Z) second)
        {
            bool forward = first.CompareTo(second) <= 0;
            var edge = forward ? (first, second) : (second, first);
            ref var uses = ref CollectionsMarshal.GetValueRefOrAddDefault(usesByEdge, edge, out _);
            uses.Add((faceIndex, forward ? 1 : -1));
        }
        bool HasClosedMultiplicity() => usesByEdge.Count > 0
            && usesByEdge.Values.All(uses => uses.Count == 2
                && uses.First.FaceIndex != uses.Second.FaceIndex);

        bool BuildEdgeUses()
        {
            foreach (int faceIndex in faceIndices)
            {
                if (!faces.TryGetValue(faceIndex, out var face)
                    || face.OuterVertices is not { } outer || !AddRingUses(outer, faceIndex))
                    return false;
                if (face.InnerVertices is { } holes)
                    foreach (var ring in holes)
                        if (!AddRingUses(ring, faceIndex)) return false;
            }
            return true;
        }

        bool AddRingUses(List<double[]> ring, int faceIndex)
        {
            if (ring.Count < 3 || verticesByAxis is null && ring.Any(point => point.Length < 3
                    || !double.IsFinite(point[0]) || !double.IsFinite(point[1])
                    || !double.IsFinite(point[2])))
                return false;
            var start = IfcPrecisionKeyFeet(ring[0]);
            var first = start;
            for (int index = 1; index <= ring.Count; index++)
            {
                var second = index < ring.Count ? IfcPrecisionKeyFeet(ring[index]) : start;
                if (first == second) return false;
                if (verticesByAxis is null) AddUse(faceIndex, first, second);
                else AddSplitUses(faceIndex, first, second, verticesByAxis);
                first = second;
            }
            return true;
        }

        void AddSplitUses(int faceIndex, (long X, long Y, long Z) first,
            (long X, long Y, long Z) second, (long X, long Y, long Z)[][] byAxis)
        {
            var segment = new LatticeSegment(first, second);
            int axis = segment.DominantAxis;
            long from = Axis(first, axis), to = Axis(second, axis);
            bool forward = from < to;
            var sorted = byAxis[axis];
            int index = AxisBound(sorted, axis, from, afterEqual: forward) - (forward ? 0 : 1);
            int step = forward ? 1 : -1;
            var previous = first;
            // Both endpoints are in the index from the validated first pass.
            for (; (uint)index < (uint)sorted.Length; index += step)
            {
                var point = sorted[index];
                long coordinate = Axis(point, axis);
                if (forward ? coordinate >= to : coordinate <= to) break;
                if (!segment.OnSegment(point)) continue;
                AddUse(faceIndex, previous, point);
                previous = point;
            }
            AddUse(faceIndex, previous, second);
        }
    }

    /// <summary>
    /// Checks whether the emitted polygon loops satisfy IFC closed-shell topology using the same
    /// declared-precision identity as the IFC point cache.
    /// </summary>
    internal static bool IsExactFacetedShell(
        IReadOnlyList<GeoFace> faces, IReadOnlyCollection<int> faceIndices) =>
        IsExactFacetedShell(faces, faceIndices, new());

    private static bool IsExactFacetedShell(
        IReadOnlyList<GeoFace> faces, IReadOnlyCollection<int> faceIndices,
        Dictionary<((long X, long Y, long Z) A, (long X, long Y, long Z) B), EdgeUses> edgeUses,
        HashSet<(long X, long Y, long Z)>? splitVertices = null)
    {
        var faceMap = faceIndices.Distinct().ToDictionary(index => index,
            index => (uint)index < faces.Count ? faces[index] : null);
        if (faceMap.Values.Any(face => face == null)) return false;
        return TryGetFacetedAtomicEdgeUses(
                faceMap.ToDictionary(pair => pair.Key, pair => pair.Value!),
                faceIndices, edgeUses, splitVertices)
            && edgeUses.Values.All(uses => uses.First.Direction + uses.Second.Direction == 0);
    }
}
