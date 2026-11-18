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
        bool triangulateNonPlanarFaces)
    {
        orientedFaces = [];
        var indices = faceIndices.Distinct().Order().ToList();
        if (indices.Count < 4 || indices.Any(index => (uint)index >= faces.Count))
            return false;
        var normalizedFaces = new Dictionary<int, GeoFace>();
        foreach (int faceIndex in indices)
        {
            var normalized = CloneFace(faces[faceIndex], false);
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
            normalizedFaces[faceIndex] = normalized;
        }
        if (!TryGetFacetedAtomicEdgeUses(normalizedFaces, indices, out var edgeUses))
            return false;

        var constraints = indices.ToDictionary(index => index,
            _ => new List<(int Other, bool OppositeFlip)>());
        foreach (var uses in edgeUses.Values)
        {
            bool oppositeFlip = uses[0].Direction == uses[1].Direction;
            constraints[uses[0].FaceIndex].Add((uses[1].FaceIndex, oppositeFlip));
            constraints[uses[1].FaceIndex].Add((uses[0].FaceIndex, oppositeFlip));
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

        orientedFaces = indices.Select(index =>
            CloneFace(normalizedFaces[index], flips[index])).ToList();
        SplitFacetedLoopsAtSharedVertices(orientedFaces);
        var compactIndices = Enumerable.Range(0, orientedFaces.Count).ToList();
        if (!IsExactFacetedShell(orientedFaces, compactIndices))
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
            orientedFaces = orientedFaces.Select(face => CloneFace(face, true)).ToList();
        if (!triangulateNonPlanarFaces)
            return true;
        var triangulatedFaces = TriangulateNonPlanarFacetedFaces(orientedFaces);
        SplitFacetedLoopsAtSharedVertices(triangulatedFaces);
        if (IsExactFacetedShell(triangulatedFaces,
                Enumerable.Range(0, triangulatedFaces.Count).ToList()))
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

        static List<double[]> CleanPrecisionLoop(IEnumerable<double[]> source)
        {
            var clean = new List<double[]>();
            foreach (var point in source)
                if (clean.Count == 0
                    || IfcPrecisionKeyFeet(clean[^1]) != IfcPrecisionKeyFeet(point))
                    clean.Add((double[])point.Clone());
            if (clean.Count > 1
                && IfcPrecisionKeyFeet(clean[0]) == IfcPrecisionKeyFeet(clean[^1]))
                clean.RemoveAt(clean.Count - 1);
            return clean;
        }

        static GeoFace CloneFace(GeoFace source, bool reverse)
        {
            List<double[]> CopyRing(IEnumerable<double[]> ring)
            {
                var copy = ring.Select(point => (double[])point.Clone()).ToList();
                if (reverse) copy.Reverse();
                return copy;
            }
            return new GeoFace
            {
                Origin = (double[])source.Origin.Clone(),
                XVector = (double[])source.XVector.Clone(),
                YVector = (double[])source.YVector.Clone(),
                Normal = reverse ? source.Normal.Select(value => -value).ToArray()
                    : (double[])source.Normal.Clone(),
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

    /// <summary>
    /// Materializes the same atomic edge partition used by shell validation in the actual polygon
    /// loops written to IFC.
    /// </summary>
    private static void SplitFacetedLoopsAtSharedVertices(IReadOnlyList<GeoFace> faces)
    {
        static long Axis((long X, long Y, long Z) point, int axis) =>
            axis == 0 ? point.X : axis == 1 ? point.Y : point.Z;
        static int LowerBound(List<(long X, long Y, long Z)> points, int axis, long value)
        {
            int low = 0, high = points.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (Axis(points[middle], axis) < value) low = middle + 1;
                else high = middle;
            }
            return low;
        }
        static bool OnSegment((long X, long Y, long Z) point,
            (long X, long Y, long Z) first, (long X, long Y, long Z) second)
        {
            if (point.X < Math.Min(first.X, second.X) || point.X > Math.Max(first.X, second.X)
                || point.Y < Math.Min(first.Y, second.Y) || point.Y > Math.Max(first.Y, second.Y)
                || point.Z < Math.Min(first.Z, second.Z) || point.Z > Math.Max(first.Z, second.Z))
                return false;
            Int128 dx = (Int128)second.X - first.X;
            Int128 dy = (Int128)second.Y - first.Y;
            Int128 dz = (Int128)second.Z - first.Z;
            Int128 px = (Int128)point.X - first.X;
            Int128 py = (Int128)point.Y - first.Y;
            Int128 pz = (Int128)point.Z - first.Z;
            return dy * pz - dz * py == 0
                && dz * px - dx * pz == 0
                && dx * py - dy * px == 0;
        }

        var representative = new Dictionary<(long X, long Y, long Z), double[]>();
        foreach (var face in faces)
            foreach (var ring in (face.OuterVertices is { Count: > 0 } outer
                    ? new[] { outer }
                    : Enumerable.Empty<List<double[]>>())
                .Concat(face.InnerVertices ?? []))
                foreach (var point in ring)
                    representative.TryAdd(IfcPrecisionKeyFeet(point), point);
        var vertices = representative.Keys.ToList();
        var verticesByAxis = Enumerable.Range(0, 3)
            .Select(axis => vertices.OrderBy(point => Axis(point, axis))
                .ThenBy(point => point.X).ThenBy(point => point.Y).ThenBy(point => point.Z)
                .ToList()).ToArray();

        List<double[]> Split(IReadOnlyList<double[]> ring)
        {
            var result = new List<double[]>();
            for (int edgeIndex = 0; edgeIndex < ring.Count; edgeIndex++)
            {
                var sourceFirst = ring[edgeIndex];
                var sourceSecond = ring[(edgeIndex + 1) % ring.Count];
                var first = IfcPrecisionKeyFeet(sourceFirst);
                var second = IfcPrecisionKeyFeet(sourceSecond);
                result.Add(sourceFirst);
                if (first == second) continue;
                Int128 dx = (Int128)second.X - first.X;
                Int128 dy = (Int128)second.Y - first.Y;
                Int128 dz = (Int128)second.Z - first.Z;
                Int128 absX = Int128.Abs(dx), absY = Int128.Abs(dy), absZ = Int128.Abs(dz);
                int axis = absX >= absY && absX >= absZ ? 0 : absY >= absZ ? 1 : 2;
                long low = Math.Min(Axis(first, axis), Axis(second, axis));
                long high = Math.Max(Axis(first, axis), Axis(second, axis));
                var split = new List<(long X, long Y, long Z)>();
                var sorted = verticesByAxis[axis];
                for (int index = LowerBound(sorted, axis, low);
                     index < sorted.Count && Axis(sorted[index], axis) <= high; index++)
                    if (sorted[index] != first && sorted[index] != second
                        && OnSegment(sorted[index], first, second))
                        split.Add(sorted[index]);
                split.Sort((left, right) => Axis(left, axis).CompareTo(Axis(right, axis)));
                if (Axis(first, axis) > Axis(second, axis)) split.Reverse();
                result.AddRange(split.Select(point => representative[point]));
            }
            var clean = new List<double[]>();
            foreach (var point in result)
                if (clean.Count == 0
                    || IfcPrecisionKeyFeet(clean[^1]) != IfcPrecisionKeyFeet(point))
                    clean.Add(point);
            if (clean.Count > 1
                && IfcPrecisionKeyFeet(clean[0]) == IfcPrecisionKeyFeet(clean[^1]))
                clean.RemoveAt(clean.Count - 1);
            return clean;
        }

        foreach (var face in faces)
        {
            if (face.OuterVertices is { Count: >= 3 } outer)
                face.OuterVertices = Split(outer);
            if (face.InnerVertices is { Count: > 0 } inner)
                face.InnerVertices = inner.Select(Split).ToList();
        }
    }

    /// <summary>Builds exact shared-edge uses on the serialized IFC coordinate lattice.</summary>
    internal static bool TryGetFacetedAtomicEdgeUses(IReadOnlyDictionary<int, GeoFace> faces,
        IReadOnlyCollection<int> faceIndices,
        out Dictionary<((long X, long Y, long Z) A, (long X, long Y, long Z) B),
            List<(int FaceIndex, int Direction)>> edgeUses)
    {
        static ((long X, long Y, long Z) A, (long X, long Y, long Z) B) EdgeKey(
            (long X, long Y, long Z) first, (long X, long Y, long Z) second)
            => first.CompareTo(second) <= 0 ? (first, second) : (second, first);
        static long Axis((long X, long Y, long Z) point, int axis) =>
            axis == 0 ? point.X : axis == 1 ? point.Y : point.Z;
        static int LowerBound(List<(long X, long Y, long Z)> points, int axis, long value)
        {
            int low = 0, high = points.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (Axis(points[middle], axis) < value) low = middle + 1;
                else high = middle;
            }
            return low;
        }
        static bool OnSegment((long X, long Y, long Z) point,
            (long X, long Y, long Z) first, (long X, long Y, long Z) second)
        {
            if (point.X < Math.Min(first.X, second.X) || point.X > Math.Max(first.X, second.X)
                || point.Y < Math.Min(first.Y, second.Y) || point.Y > Math.Max(first.Y, second.Y)
                || point.Z < Math.Min(first.Z, second.Z) || point.Z > Math.Max(first.Z, second.Z))
                return false;
            Int128 dx = (Int128)second.X - first.X;
            Int128 dy = (Int128)second.Y - first.Y;
            Int128 dz = (Int128)second.Z - first.Z;
            Int128 px = (Int128)point.X - first.X;
            Int128 py = (Int128)point.Y - first.Y;
            Int128 pz = (Int128)point.Z - first.Z;
            return dy * pz - dz * py == 0
                && dz * px - dx * pz == 0
                && dx * py - dy * px == 0;
        }

        var usesByEdge = new Dictionary<
            ((long X, long Y, long Z) A, (long X, long Y, long Z) B),
            List<(int FaceIndex, int Direction)>>();
        edgeUses = usesByEdge;
        var segments = new List<(int FaceIndex, (long X, long Y, long Z) First,
            (long X, long Y, long Z) Second)>();
        foreach (int faceIndex in faceIndices)
        {
            if (!faces.TryGetValue(faceIndex, out var face)
                || face.OuterVertices is not { Count: >= 3 } outer)
                return false;
            IEnumerable<IReadOnlyList<double[]>> rings =
                new[] { (IReadOnlyList<double[]>)outer }
                    .Concat(face.InnerVertices ?? Enumerable.Empty<List<double[]>>());
            foreach (var ring in rings)
            {
                if (ring.Count < 3 || ring.Any(point => point.Length < 3
                        || !double.IsFinite(point[0]) || !double.IsFinite(point[1])
                        || !double.IsFinite(point[2])))
                    return false;
                for (int index = 0; index < ring.Count; index++)
                {
                    var first = IfcPrecisionKeyFeet(ring[index]);
                    var second = IfcPrecisionKeyFeet(ring[(index + 1) % ring.Count]);
                    if (first == second) return false;
                    segments.Add((faceIndex, first, second));
                }
            }
        }
        if (segments.Count == 0) return false;

        void AddUse(int faceIndex, (long X, long Y, long Z) first,
            (long X, long Y, long Z) second)
        {
            var edge = EdgeKey(first, second);
            if (!usesByEdge.TryGetValue(edge, out var uses)) usesByEdge[edge] = uses = [];
            uses.Add((faceIndex, first.CompareTo(second) <= 0 ? 1 : -1));
        }
        bool HasClosedMultiplicity() => usesByEdge.Count > 0
            && usesByEdge.Values.All(uses => uses.Count == 2
                && uses[0].FaceIndex != uses[1].FaceIndex);

        foreach (var segment in segments)
            AddUse(segment.FaceIndex, segment.First, segment.Second);
        if (HasClosedMultiplicity()) return true;

        usesByEdge.Clear();
        var vertices = segments.SelectMany(segment => new[] { segment.First, segment.Second })
            .Distinct().ToList();
        var verticesByAxis = Enumerable.Range(0, 3)
            .Select(axis => vertices.OrderBy(point => Axis(point, axis))
                .ThenBy(point => point.X).ThenBy(point => point.Y).ThenBy(point => point.Z)
                .ToList())
            .ToArray();
        foreach (var segment in segments)
        {
            Int128 dx = (Int128)segment.Second.X - segment.First.X;
            Int128 dy = (Int128)segment.Second.Y - segment.First.Y;
            Int128 dz = (Int128)segment.Second.Z - segment.First.Z;
            Int128 absX = Int128.Abs(dx), absY = Int128.Abs(dy), absZ = Int128.Abs(dz);
            int axis = absX >= absY && absX >= absZ ? 0 : absY >= absZ ? 1 : 2;
            long low = Math.Min(Axis(segment.First, axis), Axis(segment.Second, axis));
            long high = Math.Max(Axis(segment.First, axis), Axis(segment.Second, axis));
            var sorted = verticesByAxis[axis];
            var split = new List<(long X, long Y, long Z)>();
            for (int index = LowerBound(sorted, axis, low);
                 index < sorted.Count && Axis(sorted[index], axis) <= high; index++)
                if (OnSegment(sorted[index], segment.First, segment.Second))
                    split.Add(sorted[index]);
            if (Axis(segment.First, axis) > Axis(segment.Second, axis)) split.Reverse();
            if (split.Count < 2 || split[0] != segment.First || split[^1] != segment.Second)
                return false;
            for (int index = 0; index + 1 < split.Count; index++)
                AddUse(segment.FaceIndex, split[index], split[index + 1]);
        }
        return HasClosedMultiplicity();
    }

    /// <summary>
    /// Checks whether the emitted polygon loops satisfy IFC closed-shell topology using the same
    /// declared-precision identity as the IFC point cache.
    /// </summary>
    internal static bool IsExactFacetedShell(
        IReadOnlyList<GeoFace> faces, IReadOnlyCollection<int> faceIndices)
    {
        var faceMap = faceIndices.Distinct().ToDictionary(index => index,
            index => (uint)index < faces.Count ? faces[index] : null);
        if (faceMap.Values.Any(face => face == null)) return false;
        return TryGetFacetedAtomicEdgeUses(
                faceMap.ToDictionary(pair => pair.Key, pair => pair.Value!),
                faceIndices, out var edgeUses)
            && edgeUses.Values.All(uses => uses[0].Direction + uses[1].Direction == 0);
    }
}
