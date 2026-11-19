namespace RvtIO.Geometry.Meshing;

// Divide and conquer on the planar input. Each hull has a fan to a virtual
// vertex. Slots stay fixed as a merge consumes a hull edge.
internal sealed class DelaunaySeed
{
    private const int Infinity = -1;
    private readonly UvPoint[] points;
    private readonly List<Triangle> slots = [];

    private sealed class Triangle(int a, int b, int c)
    {
        internal int[] Vertices { get; } = [a, b, c];
        internal EdgeRef[] Flat { get; } = new EdgeRef[3];
    }

    private readonly record struct EdgeRef(Triangle Cell, int Side)
    {
        internal EdgeRef Next => new(Cell, (Side + 1) % 3);
        internal EdgeRef Back => new(Cell, (Side + 2) % 3);
        internal EdgeRef Across => Cell.Flat[Side];
        internal int A => Cell.Vertices[Side];
        internal int B => Cell.Vertices[(Side + 1) % 3];
        internal int C => Cell.Vertices[(Side + 2) % 3];
        internal void Write(int a, int b, int c)
        {
            Cell.Vertices[Side] = a;
            Cell.Vertices[(Side + 1) % 3] = b;
            Cell.Vertices[(Side + 2) % 3] = c;
        }
        internal void Pair(EdgeRef b)
        {
            Cell.Flat[Side] = b;
            b.Cell.Flat[b.Side] = this;
        }
    }

    private DelaunaySeed(UvPoint[] points) => this.points = points;
    internal static int[][] Compute(UvPoint[] points)
    {
        var plan = new DelaunaySeed(points);
        var hull = Enumerable.Range(0, points.Length).ToArray();
        plan.Partition(hull, 0);
        var seed = plan.slots.Where(t => t.Vertices.All(v => v >= 0) && plan.Inside(t.Vertices))
            .Select(t => t.Vertices).ToArray();
        return seed;
    }

    private static IEnumerable<EdgeRef> HalfEdges(Triangle t) =>
        Enumerable.Range(0, 3).Select(i => new EdgeRef(t, i));

    private double Turn(int a, int b, int c) =>
        (points[b] - points[a]).Cross(points[c] - points[a]);
    private double Disk(int a, int b, int c, int d)
    {
        var x = points[a] - points[d]; var y = points[b] - points[d]; var z = points[c] - points[d];
        return x.LengthSquared * y.Cross(z) + y.LengthSquared * z.Cross(x) + z.LengthSquared * x.Cross(y);
    }

    private EdgeRef Slot(int a = Infinity, int b = Infinity, int c = Infinity)
    {
        var triangle = new Triangle(a, b, c);
        slots.Add(triangle);
        return new(triangle, 0);
    }

    private void PairLeaf(int first)
    {
        var sides = slots.Skip(first).SelectMany(HalfEdges).ToArray();
        foreach (var arc in sides)
        {
            if (arc.Across.Cell != null) continue;
            var otherSides = sides.Where(other => other.Cell != arc.Cell && other.A == arc.B && other.B == arc.A).ToArray();
            if (otherSides.Length != 1) throw new InvalidDataException("Invalid seed hull.");
            var inverse = otherSides[0];
            arc.Pair(inverse);
        }
    }

    private void Partition(int[] p, int axis)
    {
        int start = slots.Count;
        if (p.Length <= 3)
        {
            p = p.OrderBy(i => points[i].X).ThenBy(i => points[i].Y).ToArray();
            int a = p[0], b = p[1];
            if (p.Length == 2)
            {
                Slot(a, b);
                Slot(b, a);
            }
            else
            {
                int c = p[2];
                double winding = Turn(a, b, c);
                if (winding == 0)
                {
                    Slot(a, b);
                    Slot(b, a);
                    Slot(c, b);
                    Slot(b, c);
                    PairLeaf(start);
                    return;
                }
                if (winding < 0) (b, c) = (c, b);
                Slot(a, b, c);
                Slot(b, a); Slot(c, b); Slot(a, c);
            }
            PairLeaf(start);
            return;
        }
        var order = axis == 0 ? p.OrderBy(i => points[i].X).ThenBy(i => points[i].Y).ToArray()
                          : p.OrderBy(i => points[i].Y).ThenBy(i => points[i].X).ToArray();
        int mid = order.Length / 2;
        var left = order[..mid]; var right = order[mid..];
        Partition(left, 1 - axis);
        int split = slots.Count;
        Partition(right, 1 - axis);
        Stitch(left, right, start, split, slots.Count);
    }

    private EdgeRef FindGhost(int lo, int hi, int a, int b)
    {
        var matches = slots.Skip(lo).Take(hi - lo).Where(f => f.Vertices.Contains(Infinity))
    .SelectMany(HalfEdges).Where(e => e.A == a && e.B == b).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("Ambiguous seed hull.");
        return matches[0];
    }

    private void Stitch(int[] left, int[] right, int begin, int split, int end)
    {
        var tangents = (from a in left
                        from b in right
                        where left.All(p => Turn(a, b, p) >= 0) && right.All(p => Turn(a, b, p) >= 0)
                        select (a, b)).Take(2).ToArray();
        if (tangents.Length != 1) throw new InvalidDataException("Ambiguous seed tangent.");
        var tangent = tangents[0];
        int lowerL = tangent.a, lowerR = tangent.b;
        var leftHull = FindGhost(begin, split, Infinity, lowerL);
        var rightHull = FindGhost(split, end, lowerR, Infinity);
        var l = leftHull.Across;
        var r = rightHull.Across;
        var bottom = Slot(lowerL, Infinity, lowerR);
        bottom.Pair(leftHull);
        bottom.Next.Pair(rightHull);
        var seam = bottom.Back;
        int remaining = (left.Length + right.Length) * 4;
        while (remaining-- > 0)
        {
            bool canL = l.C >= 0 && Turn(lowerL, lowerR, l.C) > 0;
            bool canR = r.C >= 0 && Turn(lowerL, lowerR, r.C) > 0;
            if (!canL && !canR)
            {
                var upper = Slot(lowerL, lowerR);
                upper.Pair(seam);
                upper.Next.Pair(r);
                upper.Back.Pair(l);
                return;
            }

            if (canL) RollLeft(ref l, lowerL, lowerR);
            if (canR) RollRight(ref r, lowerL, lowerR);
            if (!canL || canR && Disk(l.C, lowerL, lowerR, r.C) > 0)
            {
                seam.Pair(r);
                int top = r.C;
                seam = r.Back;
                seam.Write(top, lowerL, lowerR);
                lowerR = top;
                r = seam.Across;
            }
            else
            {
                seam.Pair(l);
                int top = l.C;
                seam = l.Next;
                seam.Write(lowerR, top, lowerL);
                lowerL = top;
                l = seam.Across;
            }
        }
        throw new InvalidDataException("Delaunay seam failed to close.");
    }

    private void RollLeft(ref EdgeRef choice, int left, int right)
    {
        var peel = choice.Back.Across;
        int remaining = slots.Count;
        while (peel.C >= 0 && Disk(left, right, choice.C, peel.C) > 0)
        {
            if (--remaining == 0) throw new InvalidDataException("Invalid left seed hull.");
            int top = choice.C, replacement = peel.C;
            var far = peel.Next.Across;
            var upcoming = peel.Back.Across;
            peel.Back.Pair(far);
            choice.Pair(upcoming);
            choice = choice.Next;
            peel.Next.Pair(choice.Across);
            choice.Write(left, Infinity, replacement);
            peel.Next.Write(Infinity, top, replacement);
            peel = upcoming;
        }
    }

    private void RollRight(ref EdgeRef choice, int left, int right)
    {
        var peel = choice.Next.Across;
        int remaining = slots.Count;
        while (peel.C >= 0 && Disk(left, right, choice.C, peel.C) > 0)
        {
            if (--remaining == 0) throw new InvalidDataException("Invalid right seed hull.");
            int top = choice.C, replacement = peel.C;
            var far = peel.Back.Across;
            var upcoming = peel.Next.Across;
            peel.Next.Pair(far);
            choice.Pair(upcoming);
            choice = choice.Back;
            peel.Back.Pair(choice.Across);
            choice.Write(Infinity, right, replacement);
            peel.Back.Write(top, Infinity, replacement);
            peel = upcoming;
        }
    }

    private bool Inside(int[] triangle)
    {
        var p = (points[triangle[0]] + points[triangle[1]] + points[triangle[2]]) * (1.0 / 3);
        bool contained = false;
        for (int i = 0; i < points.Length; i++)
        {
            var a = points[i]; var b = points[(i + 1) % points.Length];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) contained = !contained;
        }
        return contained;
    }
}
