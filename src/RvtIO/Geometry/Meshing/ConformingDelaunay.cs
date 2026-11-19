namespace RvtIO.Geometry.Meshing;

internal readonly record struct TriangulationEdge(int A, int B)
{
    internal static TriangulationEdge Of(int a, int b) => a < b ? new(a, b) : new(b, a);
}

internal sealed class ConformingDelaunay
{
    private sealed class Cell
    {
        internal int[] V { get; } = new int[3];
        internal bool Live;
    }

    private readonly record struct Side(Cell Cell, int I)
    {
        internal int A => Cell.V[I];
        internal int B => Cell.V[(I + 1) % 3];
        internal int C => Cell.V[(I + 2) % 3];
        internal Side Next => new(Cell, (I + 1) % 3);
        internal Side Back => new(Cell, (I + 2) % 3);
        internal TriangulationEdge Edge => TriangulationEdge.Of(A, B);
        internal void Set(int a, int b, int c)
        {
            Cell.V[I] = a;
            Cell.V[(I + 1) % 3] = b;
            Cell.V[(I + 2) % 3] = c;
        }
    }

    // Validate by oriented slot and vertex identities. A cancelled site can be followed by a
    // boundary split that reuses those identities, making a previously queued test valid again.
    private readonly record struct Candidate(Side Side, int A, int B, int C)
    {
        internal bool Matches => Side.Cell.Live && A == Side.A && B == Side.B && C == Side.C;
    }

    // Repair competing boundary edges in slot order. New entries can reuse an earlier slot.
    private sealed class BoundaryWork
    {
        private readonly List<TriangulationEdge?> slots = [];
        private readonly Stack<int> free = [];
        internal int Count { get; private set; }

        internal void Add(TriangulationEdge edge)
        {
            if (free.TryPop(out int index)) slots[index] = edge;
            else slots.Add(edge);
            Count++;
        }

        internal IEnumerable<TriangulationEdge> Sweep()
        {
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i] is not TriangulationEdge edge) continue;
                yield return edge;
                slots[i] = null;
                free.Push(i);
                Count--;
            }
        }
    }

    private readonly List<UvPoint> points;
    private readonly List<Cell> cells = [];
    private readonly Stack<Cell> free = [];
    private readonly Dictionary<TriangulationEdge, List<Side>> adjacency = [];
    private readonly List<TriangulationEdge> boundary = [];
    private readonly HashSet<TriangulationEdge> constrained = [];
    private readonly PriorityQueue<Candidate, (int Key, int Order)> bad = new();
    private readonly UvPoint limits;
    private int sequence;
    private bool refining;


    internal UvPoint[] Points => points.ToArray();
    internal int[][] Faces => cells.Where(c => c.Live).Select(c => c.V.ToArray()).ToArray();

    internal ConformingDelaunay(UvPoint[] ring, UvPoint limits)
    {
        this.limits = limits;
        points = [.. ring];
        for (int i = 0; i < ring.Length; i++) RecordBoundary(TriangulationEdge.Of(i, (i + 1) % ring.Length));
        foreach (var tri in DelaunaySeed.Compute(ring)) Create(tri[0], tri[1], tri[2]);
        if (cells.Count != ring.Length - 2 || constrained.Any(e => !adjacency.TryGetValue(e, out var a) || a.Count != 1))
            throw new InvalidDataException("Seed does not cover an isolated simple contour.");

    }

    private void RecordBoundary(TriangulationEdge e) { boundary.Add(e); constrained.Add(e); }

    private static IEnumerable<Side> Around(Cell c)
    {
        for (int i = 0; i < 3; i++) yield return new(c, i);
    }

    private void Attach(Cell cell)
    {
        foreach (var side in Around(cell))
        {
            if (!adjacency.TryGetValue(side.Edge, out var ring)) adjacency.Add(side.Edge, ring = []);
            ring.Add(side);
        }
    }
    private void Detach(Cell cell)
    {
        foreach (var side in Around(cell))
        {
            var ring = adjacency[side.Edge];
            ring.Remove(side);
            if (ring.Count == 0) adjacency.Remove(side.Edge);
        }
    }
    private Side? Across(Side edge)
    {
        if (!adjacency.TryGetValue(edge.Edge, out var ring)) throw new InvalidDataException("Missing facet adjacency.");
        if (ring.Count == 1) return null;
        if (ring.Count != 2) throw new InvalidDataException("Nonmanifold facet.");
        return ring[0].Cell == edge.Cell ? ring[1] : ring[0];
    }

    private Side Create(int a, int b, int c)
    {
        Cell o;
        if (!free.TryPop(out o!)) { o = new(); cells.Add(o); }
        o.Live = true;
        var side = new Side(o, 0);
        side.Set(a, b, c);
        Attach(o);
        return side;
    }
    private void Replace(Side side, int a, int b, int c)
    {
        Detach(side.Cell);
        side.Set(a, b, c);
        Attach(side.Cell);
    }
    private void Deallocate(Cell cell)
    {
        Detach(cell);
        cell.Live = false;
        free.Push(cell);
    }

    private void Flip(Side seam, Side outer)
    {
        int a = seam.A, b = seam.B, p = seam.C, c = outer.C;
        Detach(seam.Cell); Detach(outer.Cell);
        seam.Set(c, p, a);
        outer.Set(p, c, b);
        Attach(seam.Cell); Attach(outer.Cell);
    }

    private double Orientation(int a, int b, UvPoint p) => PlanarPredicates.Orientation(points[a], points[b], p);
    private bool Encroaches(UvPoint p, TriangulationEdge e) => (p - points[e.A]).Dot(p - points[e.B]) < 0;
    private double SideLength(Side h) => (points[h.A] - points[h.B]).LengthSquared;

    private void QueueIfWide(Side side)
    {
        UvPoint a = points[side.A], b = points[side.B], c = points[side.C];
        double u = Math.Max(a.X, Math.Max(b.X, c.X)) - Math.Min(a.X, Math.Min(b.X, c.X));
        double v = Math.Max(a.Y, Math.Max(b.Y, c.Y)) - Math.Min(a.Y, Math.Min(b.Y, c.Y));
        if (u <= limits.X && v <= limits.Y) return;
        var shortEdge = Shortest(side);
        var e = points[shortEdge.A] - points[shortEdge.C];
        var f = points[shortEdge.B] - points[shortEdge.C];
        double dot = e.Dot(f);
        double ratio = constrained.Contains(shortEdge.Next.Edge) && constrained.Contains(shortEdge.Back.Edge)
            ? 0 : dot * dot / (e.LengthSquared * f.LengthSquared);
        int k = Math.Clamp((int)((ratio - 0.6) * 160), 0, 63);
        bad.Enqueue(new(side, side.A, side.B, side.C), (-k, sequence++));
    }

    private Side Shortest(Side e)
    {
        double x = SideLength(e), y = SideLength(e.Next), z = SideLength(e.Back);
        if (y < x && y < z) return e.Next;
        return x < z ? e : e.Back;
    }

    private (Side Seat, bool Outside) Locate(Side start, UvPoint p)
    {
        var side = start;
        for (int remaining = cells.Count; remaining > 0; remaining--)
        {
            int a = side.A, b = side.B, c = side.C;
            double left = Orientation(a, c, p), right = Orientation(c, b, p);
            if (left <= 0 && right <= 0)
                return (left == 0 ? side.Back : right == 0 ? side.Next : side, false);
            bool chooseLeft = left > 0 && (right <= 0 || (points[c] - p).Dot(points[b] - points[a]) > 0);
            var wall = chooseLeft ? side.Back : side.Next;
            var other = Across(wall);
            if (other is null) return (wall, true);
            side = other.Value;
        }
        throw new InvalidDataException("Point location failed.");
    }

    private UvPoint Circumcenter(Side side)
    {
        UvPoint a = points[side.A], b = points[side.B], c = points[side.C], d = b - a, e = c - a;
        double half = 0.5 / PlanarPredicates.Orientation(b, c, a);
        return new(a.X - (d.Y * e.LengthSquared - e.Y * d.LengthSquared) * half,
                   a.Y + (d.X * e.LengthSquared - e.X * d.LengthSquared) * half);
    }

    private Side Insert(Side seat, int index, bool onEdge, BoundaryWork encroached)
    {
        int a = seat.A, b = seat.B, c = seat.C;
        Side walk;
        if (!onEdge)
        {
            Replace(seat, a, b, index);
            Create(b, c, index);
            Create(c, a, index);
            walk = seat;
        }
        else
        {
            var opposite = Across(seat);
            if (opposite is { } other)
            {
                int d = other.C;
                Replace(other.Next, index, d, b);
                Create(a, d, index);
            }
            Replace(seat, index, b, c);
            Create(c, a, index);
            walk = seat.Next;
        }

        int first = walk.A;
        for (int remaining = cells.Count * 8; remaining > 0; remaining--)
        {
            var far = Across(walk);
            if (constrained.Contains(walk.Edge))
            {
                if (Encroaches(points[index], walk.Edge)) encroached.Add(walk.Edge);
            }
            else if (far is { } f &&
                PlanarPredicates.Disk(points[walk.A], points[walk.B], points[index], points[f.C]) > 0)
            {
                Flip(walk, f);
                walk = walk.Back;
                continue;
            }
            if (refining) QueueIfWide(walk);
            var next = Across(walk.Next);
            if (walk.B == first || next == null)
                return walk.Back;
            walk = next.Value.Next;
        }
        throw new InvalidDataException("Edge legalization failed.");
    }

    private void DeleteSite(Side del)
    {
        int center = del.A;
        Side first = Across(del.Back) ?? throw new InvalidDataException("Open cancellation polygon.");
        var fan = new List<Side>();
        Side current = first;
        do
        {
            if (current.A != center) throw new InvalidDataException("Open cancellation polygon.");
            fan.Add(current);
            current = Across(current.Back) ?? throw new InvalidDataException("Open cancellation polygon.");
        } while (current != del && fan.Count <= 256);
        if (current != del || fan.Count is < 2 or > 256) throw new InvalidDataException("Invalid cancellation polygon.");
        var rim = fan.Select(a => a.B).Append(fan[^1].C).ToArray();
        var (right, left, pivot) = Fill(0, rim.Length - 1, false);
        Replace(del, pivot, del.B, del.C);
        Deallocate(right.Cell);
        Deallocate(left.Cell);
        QueueIfWide(del);

        (Side Right, Side Left, int Pivot) Fill(int from, int to, bool flip)
        {
            if (to == from + 1) return (fan[from], default, -1);
            int a = rim[from], b = rim[to], k = from + 1;
            for (int i = k + 1; i < to; i++)
                if (PlanarPredicates.Disk(points[b], points[a], points[rim[k]], points[rim[i]]) > 0) k = i;
            var left = Fill(from, k, true).Right;
            var right = Fill(k, to, true).Right;
            if (!flip) return (right, left, rim[k]);
            Flip(right, left.Back);
            QueueIfWide(left.Back);
            return (right.Back, default, rim[k]);
        }
    }

    private void Split(TriangulationEdge edge, BoundaryWork encroached)
    {
        if (!constrained.Contains(edge)) return;
        if (!adjacency.TryGetValue(edge, out var incident) || incident.Count != 1) throw new InvalidDataException("Invalid boundary edge.");
        var wall = incident[0];
        UvPoint a = points[wall.A], b = points[wall.B];
        bool nearA = constrained.Contains(wall.Back.Edge), nearB = constrained.Contains(wall.Next.Edge);
        double ratio = 0.5;
        if (nearA != nearB)
        {
            double length = Math.Sqrt((b - a).LengthSquared);
            double scale = Math.ScaleB(1.0, (int)Math.Floor(Math.Log2(length) + 0.5) - 1);
            ratio = nearA ? scale / length : 1 - scale / length;
        }
        // Use the boundary direction, opposite the incident triangle.
        double reverse = 1 - ratio;
        var p = b * (1 - reverse) + a * reverse;
        if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || p == a || p == b)
            throw new InvalidDataException("Degenerate boundary split.");
        int n = points.Count, slot = boundary.IndexOf(edge);
        int head = wall.A, tail = wall.B;
        points.Add(p);
        constrained.Remove(edge);
        var unchanged = TriangulationEdge.Of(n, tail);
        constrained.Add(unchanged);
        boundary[slot] = unchanged;
        RecordBoundary(TriangulationEdge.Of(head, n));
        Insert(wall, n, true, encroached);
    }

    internal void Refine()
    {
        var encroached = new BoundaryWork();
        foreach (var edge in boundary)
            if (adjacency[edge].Any(s => Encroaches(points[s.C], edge))) encroached.Add(edge);
        RepairBoundary();
        refining = true;
        foreach (var c in cells)
            if (c.Live) QueueIfWide(new(c, 0));
        int attempts = 0;
        while (bad.TryDequeue(out var candidate, out _))
        {
            if (++attempts > 250000) throw new InvalidDataException("Refinement queue did not converge.");
            if (!candidate.Matches) continue;
            if (points.Count > 12000) throw new InvalidDataException("Too many inserted vertices.");
            var t = candidate.Side;
            var p = Circumcenter(t);
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || p == points[t.A] || p == points[t.B] || p == points[t.C])
                throw new InvalidDataException("Degenerate circumcenter.");
            var (seat, outside) = Locate(Shortest(t), p);
            bool onEdge = Orientation(seat.A, seat.B, p) == 0;
            if ((outside || onEdge) && constrained.Contains(seat.Edge))
            {
                encroached.Add(seat.Edge);
                RepairBoundary();
                continue;
            }
            if (outside) throw new InvalidDataException("Unbounded circumcenter.");
            int count = points.Count;
            points.Add(p);
            var placed = Insert(seat, count, onEdge, encroached);
            bool accepted = encroached.Count == 0;
            if (!accepted)
            {
                DeleteSite(placed);
                points.RemoveAt(count);
                RepairBoundary();
            }
        }
        void RepairBoundary()
        {
            while (encroached.Count > 0)
            {
                foreach (var edge in encroached.Sweep())
                {
                    if (points.Count > 12000) throw new InvalidDataException("Too many boundary vertices.");
                    Split(edge, encroached);
                }
            }
        }
    }
}



internal readonly record struct UvPoint(double X, double Y)
{
    public static UvPoint operator +(UvPoint a, UvPoint b) => new(a.X + b.X, a.Y + b.Y);
    public static UvPoint operator -(UvPoint a, UvPoint b) => new(a.X - b.X, a.Y - b.Y);
    public static UvPoint operator *(UvPoint a, double k) => new(a.X * k, a.Y * k);
    internal double Dot(UvPoint b) => X * b.X + Y * b.Y;
    internal double Cross(UvPoint b) => X * b.Y - Y * b.X;
    internal double LengthSquared => Dot(this);
    internal bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
}
