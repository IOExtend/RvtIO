namespace RvtIO.Parsing;

internal static partial class GRepParserV2
{
    /// <summary>Work units per input vertex and per doubling of the vertex count that one trim triangulation may spend
    /// (see <see cref="TrimWorkBudget"/>).</summary>
    private const int TrimTriangulationWorkFactor = 256;

    /// <summary>Work units every trim triangulation may spend on top of the near-linear part, a few seconds, so that loops of a
    /// few thousand vertices that defeat the indexes still triangulate.</summary>
    private const long TrimTriangulationWorkAllowance = 1L << 26;

    /// <summary>The work one trim triangulation may still spend, in orientation tests and visited index entries. The bridges to
    /// inner loops and the ear clipping stay quadratic for adversarial loops; past the budget the triangulation stops instead of
    /// running for hours and says so (<see cref="Exhausted"/>), and the face is left out and counted as untriangulated rather
    /// than drawn without its trim.</summary>
    internal sealed class TrimWorkBudget(long units)
    {
        private long _left = units;

        /// <summary>The budget of a triangulation of <paramref name="vertices"/> loop vertices: near-linear in them, plus the
        /// fixed allowance.</summary>
        internal static TrimWorkBudget ForVertices(int vertices)
        {
            long count = Math.Max(vertices, 16);
            return new TrimWorkBudget(TrimTriangulationWorkFactor * count
                * (1 + System.Numerics.BitOperations.Log2((ulong)count)) + TrimTriangulationWorkAllowance);
        }

        /// <summary>The budget ran out: a triangulation that spent it returned null for that reason.</summary>
        internal bool Exhausted => _left < 0;

        internal void Spend(long units = 1)
        {
            if ((_left -= units) < 0) throw new TrimWorkExceeded();
        }
    }

    private sealed class TrimWorkExceeded : Exception { }

    /// <summary>A widening of a coordinate range that covers the rounding of the trim predicates: each coordinate is rounded
    /// to a decimal of 15 significant digits, and their products to 28 digits and 28 decimal places, so an orientation test
    /// reads zero up to about 1e-28 (relative to large coordinates). Off a segment longer than about 1e-16 that is closer than
    /// the widening; a shorter segment's box, which bounds what an on-segment test accepts, lies within it.</summary>
    private static double TrimMargin(double min, double max) =>
        1e-9 * (Math.Max(Math.Abs(min), Math.Abs(max)) + (max - min)) + 1e-12;

    /// <summary>A k-d tree with tight boxes over points of the UV plane. A triangle query skips a box that lies off the triangle's
    /// box, or beyond one of its edge lines, by more than a margin of the coordinates' size; the decimal predicates accept no point
    /// so far out, so the query misses none of theirs, and a long thin triangle visits only the leaves near it.</summary>
    private sealed class TrimPointTree
    {
        private const int LeafSize = 8;

        private struct Node
        {
            public double MinX, MinY, MaxX, MaxY;
            public int Start, Count, Alive, Parent, Left, Right;
        }

        private readonly IReadOnlyList<double[]> _points;
        private readonly int[] _order;
        private readonly int[] _leafOf;
        private readonly bool[] _gone;
        private Node[] _nodes;
        private int _nodeCount;
        private readonly double _size, _margin;
        private readonly Stack<int> _pending = new();
        private readonly TrimWorkBudget _budget;

        internal TrimPointTree(IReadOnlyList<double[]> points, TrimWorkBudget budget)
        {
            _points = points;
            _budget = budget;
            _order = Enumerable.Range(0, points.Count).ToArray();
            _leafOf = new int[points.Count];
            _gone = new bool[points.Count];
            _nodes = new Node[Math.Max(1, 4 * points.Count / LeafSize + 2)];
            double largest = 0, minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            foreach (var point in points)
            {
                largest = Math.Max(largest, Math.Max(Math.Abs(point[0]), Math.Abs(point[1])));
                minX = Math.Min(minX, point[0]); maxX = Math.Max(maxX, point[0]);
                minY = Math.Min(minY, point[1]); maxY = Math.Max(maxY, point[1]);
            }
            _size = points.Count == 0 ? 0 : largest + Math.Max(maxX - minX, maxY - minY);
            _margin = 3e-9 * _size + 1e-12;
            if (points.Count > 0) Build(0, points.Count, -1);
        }

        private int Build(int start, int count, int parent)
        {
            _budget.Spend(count);
            if (_nodeCount == _nodes.Length) Array.Resize(ref _nodes, 2 * _nodes.Length);
            int node = _nodeCount++;
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            for (int index = start; index < start + count; index++)
            {
                var point = _points[_order[index]];
                minX = Math.Min(minX, point[0]); maxX = Math.Max(maxX, point[0]);
                minY = Math.Min(minY, point[1]); maxY = Math.Max(maxY, point[1]);
            }
            _nodes[node] = new Node
            {
                MinX = minX, MinY = minY, MaxX = maxX, MaxY = maxY,
                Start = start, Count = count, Alive = count, Parent = parent, Left = -1, Right = -1,
            };
            if (count <= LeafSize)
            {
                for (int index = start; index < start + count; index++) _leafOf[_order[index]] = node;
                return node;
            }
            int axis = maxX - minX >= maxY - minY ? 0 : 1;
            _order.AsSpan(start, count).Sort((first, second) =>
                _points[first][axis].CompareTo(_points[second][axis]) is var order and not 0 ? order : first.CompareTo(second));
            int left = Build(start, count / 2, node);
            int right = Build(start + count / 2, count - count / 2, node);
            _nodes[node].Left = left;
            _nodes[node].Right = right;
            return node;
        }

        internal void Remove(int index)
        {
            if (_gone[index]) return;
            _gone[index] = true;
            for (int node = _leafOf[index]; node >= 0; node = _nodes[node].Parent)
            {
                _budget.Spend();
                _nodes[node].Alive--;
            }
        }

        /// <summary>Calls <paramref name="accept"/> for the remaining points that can lie in the closed triangle (a segment when
        /// two corners are equal) until it returns true; the point it accepted then, else -1.</summary>
        internal int FirstInTriangle(double[] a, double[] b, double[] c, Func<int, bool> accept)
        {
            if (_nodeCount == 0) return -1;
            double minX = Math.Min(a[0], Math.Min(b[0], c[0])) - _margin, maxX = Math.Max(a[0], Math.Max(b[0], c[0])) + _margin;
            double minY = Math.Min(a[1], Math.Min(b[1], c[1])) - _margin, maxY = Math.Max(a[1], Math.Max(b[1], c[1])) + _margin;
            // The edge lines a box can be beyond: (start, direction over its length, side of the triangle). A short edge is left
            // out, as its direction is not known closely enough; side 0 marks a triangle within the margin of the line.
            Span<(double X, double Y, double Dx, double Dy, int Side)> lines = stackalloc (double, double, double, double, int)[3];
            int lineCount = 0;
            foreach (var (from, to, opposite) in (ReadOnlySpan<(double[], double[], double[])>)[(a, b, c), (b, c, a), (c, a, b)])
            {
                double dx = to[0] - from[0], dy = to[1] - from[1], length = Math.Sqrt(dx * dx + dy * dy);
                if (!(length >= 1e-6 * _size) || length == 0) continue;
                dx /= length;
                dy /= length;
                double side = dx * (opposite[1] - from[1]) - dy * (opposite[0] - from[0]);
                lines[lineCount++] = (from[0], from[1], dx, dy, side > _margin ? 1 : side < -_margin ? -1 : 0);
            }
            _pending.Clear();
            _pending.Push(0);
            while (_pending.TryPop(out int node))
            {
                _budget.Spend();
                ref var box = ref _nodes[node];
                if (box.Alive == 0 || box.MaxX < minX || box.MinX > maxX || box.MaxY < minY || box.MinY > maxY) continue;
                bool beyond = false;
                for (int index = 0; index < lineCount && !beyond; index++)
                {
                    var line = lines[index];
                    double low = double.PositiveInfinity, high = double.NegativeInfinity;
                    foreach (var (x, y) in (ReadOnlySpan<(double, double)>)
                        [(box.MinX, box.MinY), (box.MaxX, box.MinY), (box.MinX, box.MaxY), (box.MaxX, box.MaxY)])
                    {
                        double distance = line.Dx * (y - line.Y) - line.Dy * (x - line.X);
                        low = Math.Min(low, distance);
                        high = Math.Max(high, distance);
                    }
                    beyond = line.Side >= 0 && high < -_margin || line.Side <= 0 && low > _margin;
                }
                if (beyond) continue;
                if (box.Left < 0)
                {
                    for (int index = box.Start; index < box.Start + box.Count; index++)
                    {
                        int point = _order[index];
                        if (_gone[point]) continue;
                        _budget.Spend();
                        if (accept(point)) return point;
                    }
                    continue;
                }
                _pending.Push(box.Right);
                _pending.Push(box.Left);
            }
            return -1;
        }
    }

    /// <summary>The edges of closed rings bucketed into horizontal bands. A strict winding test of a point over the edges of its
    /// band is the test over every edge: the other edges neither cross the horizontal through the point nor, widened by
    /// <see cref="TrimMargin"/>, hold it. In a band, the steep narrow edges of a ring that cross the whole band are kept sorted by
    /// their lowest x: one well clear of the point to its right adds its direction to the winding number and one to its left
    /// nothing, which is what the decimal orientation test gives them (see <see cref="SpanningEdges"/>), so only those near
    /// the point are tested.</summary>
    private sealed class TrimEdgeBands
    {
        private readonly IReadOnlyList<List<double[]>> _rings;
        private readonly List<(int Ring, int Edge)>?[] _tested;
        private readonly List<SpanningEdges>?[] _spanning;
        private readonly double _minY, _scale, _margin;
        private readonly int[] _winding;
        private readonly bool[] _onRing, _touchedRing;
        private readonly List<int> _touched = new(), _containing = new();
        private readonly Func<double[], double[], double[], decimal> _orientation;
        private readonly Func<double[], double[], double[], bool> _onSegment;
        private readonly TrimWorkBudget _budget;

        /// <summary>The edges of one ring that cross a whole band, sorted by their lowest x, with the sums of their directions
        /// (+1 up, -1 down) from each one on. Each rises by more than 1e-12 times the size of the coordinates, and a point
        /// counts them as right or left of it only when it is more than <c>_margin</c> clear of their x range: then the
        /// exact orientation of the rounded point to the rounded edge is the rise times the horizontal gap, beyond the
        /// rounding of the decimal products, so its sign is the direction (right) or the opposite one (left).</summary>
        private sealed class SpanningEdges(int ring)
        {
            internal readonly int Ring = ring;
            internal readonly List<(double MinX, double MaxX, int Edge, int Direction)> Items = new();
            internal double[] MinX = [], MaxX = [];
            internal int[] Edge = [], DirectionsFrom = [];
            internal double Width;

            internal void Seal()
            {
                Items.Sort((first, second) => first.MinX.CompareTo(second.MinX) is var order and not 0
                    ? order : first.Edge.CompareTo(second.Edge));
                MinX = Items.Select(item => item.MinX).ToArray();
                MaxX = Items.Select(item => item.MaxX).ToArray();
                Edge = Items.Select(item => item.Edge).ToArray();
                DirectionsFrom = new int[Items.Count + 1];
                for (int index = Items.Count - 1; index >= 0; index--)
                    DirectionsFrom[index] = DirectionsFrom[index + 1] + Items[index].Direction;
                Width = Items.Max(item => item.MaxX - item.MinX);
            }
        }

        internal TrimEdgeBands(IReadOnlyList<List<double[]>> rings, TrimWorkBudget budget,
            Func<double[], double[], double[], decimal> orientation, Func<double[], double[], double[], bool> onSegment)
        {
            _rings = rings;
            _budget = budget;
            _orientation = orientation;
            _onSegment = onSegment;
            _winding = new int[rings.Count];
            _onRing = new bool[rings.Count];
            _touchedRing = new bool[rings.Count];
            int edges = rings.Sum(ring => ring.Count);
            budget.Spend(edges);
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity, largest = 0;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            foreach (var ring in rings)
                foreach (var point in ring)
                {
                    largest = Math.Max(largest, Math.Max(Math.Abs(point[0]), Math.Abs(point[1])));
                    minX = Math.Min(minX, point[0]); maxX = Math.Max(maxX, point[0]);
                    minY = Math.Min(minY, point[1]); maxY = Math.Max(maxY, point[1]);
                }
            int count = maxY > minY ? Math.Max(1, (int)Math.Sqrt(edges)) : 1;
            _minY = double.IsFinite(minY) ? minY : 0;
            _scale = maxY > minY ? count / (maxY - minY) : 0;
            _margin = 3e-9 * (edges == 0 ? 0 : largest + Math.Max(maxX - minX, maxY - minY)) + 1e-12;
            _tested = new List<(int, int)>?[count];
            _spanning = new List<SpanningEdges>?[count];
            // A point falls in a band up to the rounding of the band's bounds; an edge spans the band when it reaches beyond
            // both by more than that.
            double slack = _scale > 0 ? 1e-9 * (Math.Abs(_minY) + (maxY - minY)) + 1e-12 : 0;
            double steep = 1e-12 * (1 + largest), narrow = (maxX - minX) / count;
            var groups = new Dictionary<(int Band, int Ring), SpanningEdges>();
            for (int ringIndex = 0; ringIndex < rings.Count; ringIndex++)
            {
                var ring = rings[ringIndex];
                for (int edgeIndex = 0; edgeIndex < ring.Count; edgeIndex++)
                {
                    var first = ring[edgeIndex];
                    var second = ring[(edgeIndex + 1) % ring.Count];
                    double low = Math.Min(first[1], second[1]), high = Math.Max(first[1], second[1]);
                    double left = Math.Min(first[0], second[0]), right = Math.Max(first[0], second[0]);
                    bool spanningCandidate = _scale > 0 && high - low >= steep && right - left <= narrow;
                    double margin = TrimMargin(low, high);
                    int lastBand = Band(high + margin);
                    for (int band = Band(low - margin); band <= lastBand; band++)
                    {
                        budget.Spend();
                        if (spanningCandidate && low < _minY + band / _scale - slack && high > _minY + (band + 1) / _scale + slack)
                        {
                            if (!groups.TryGetValue((band, ringIndex), out var group))
                            {
                                groups[(band, ringIndex)] = group = new SpanningEdges(ringIndex);
                                (_spanning[band] ??= new List<SpanningEdges>()).Add(group);
                            }
                            group.Items.Add((left, right, edgeIndex, first[1] < second[1] ? 1 : -1));
                        }
                        else (_tested[band] ??= new List<(int, int)>()).Add((ringIndex, edgeIndex));
                    }
                }
            }
            foreach (var group in groups.Values)
            {
                budget.Spend(group.Items.Count * (1L + System.Numerics.BitOperations.Log2((uint)group.Items.Count)));
                group.Seal();
            }
        }

        private int Band(double y) => (int)Math.Clamp((y - _minY) * _scale, 0, _tested.Length - 1);

        // The first index whose value is above (or, with orEqual, at least) the bound.
        private static int FirstAbove(double[] values, double bound, bool orEqual)
        {
            int low = 0, high = values.Length;
            while (low < high)
            {
                int middle = (low + high) >>> 1;
                if (values[middle] > bound || orEqual && values[middle] == bound) high = middle;
                else low = middle + 1;
            }
            return low;
        }

        /// <summary>Calls <paramref name="accept"/> with the ring and edge index of every edge that can meet the vertical range
        /// from <paramref name="low"/> to <paramref name="high"/>, until it returns true; true then. An edge in several bands
        /// is offered once per band.</summary>
        internal bool AnyEdge(double low, double high, Func<int, int, bool> accept)
        {
            double margin = TrimMargin(low, high);
            int lastBand = Band(high + margin);
            for (int band = Band(low - margin); band <= lastBand; band++)
            {
                _budget.Spend();
                if (_tested[band] is { } edges)
                {
                    _budget.Spend(edges.Count);
                    foreach (var (ring, edge) in edges)
                        if (accept(ring, edge)) return true;
                }
                if (_spanning[band] is { } groups)
                    foreach (var group in groups)
                    {
                        _budget.Spend(group.Edge.Length);
                        foreach (int edge in group.Edge)
                            if (accept(group.Ring, edge)) return true;
                    }
            }
            return false;
        }

        private void Touch(int ring)
        {
            if (_touchedRing[ring]) return;
            _touchedRing[ring] = true;
            _touched.Add(ring);
        }

        private void Test(int ringIndex, int edgeIndex, double[] point)
        {
            var ring = _rings[ringIndex];
            var first = ring[edgeIndex];
            var second = ring[(edgeIndex + 1) % ring.Count];
            Touch(ringIndex);
            decimal orientation = _orientation(first, second, point);
            if (orientation == 0 && _onSegment(point, first, second)) _onRing[ringIndex] = true;
            else if (first[1] <= point[1] && point[1] < second[1] && orientation > 0) _winding[ringIndex]++;
            else if (second[1] <= point[1] && point[1] < first[1] && orientation < 0) _winding[ringIndex]--;
        }

        /// <summary>The rings that strictly contain the point: it lies on none of their edges and their winding number about it
        /// is not zero. In no particular order; the list is reused by the next call.</summary>
        internal List<int> RingsStrictlyContaining(double[] point)
        {
            foreach (int ring in _touched)
            {
                _winding[ring] = 0;
                _onRing[ring] = false;
                _touchedRing[ring] = false;
            }
            _touched.Clear();
            _containing.Clear();
            int band = Band(point[1]);
            if (_tested[band] is { } edges)
            {
                _budget.Spend(edges.Count);
                foreach (var (ringIndex, edgeIndex) in edges) Test(ringIndex, edgeIndex, point);
            }
            if (_spanning[band] is { } groups)
                foreach (var group in groups)
                {
                    int right = FirstAbove(group.MinX, point[0] + _margin, orEqual: false);
                    int from = FirstAbove(group.MinX, point[0] - _margin - group.Width, orEqual: true);
                    _budget.Spend(2 + 2L * System.Numerics.BitOperations.Log2((uint)group.MinX.Length + 1) + Math.Max(0, right - from));
                    for (int index = from; index < right; index++)
                        if (group.MaxX[index] >= point[0] - _margin) Test(group.Ring, group.Edge[index], point);
                    if (group.DirectionsFrom[right] != 0)
                    {
                        Touch(group.Ring);
                        _winding[group.Ring] += group.DirectionsFrom[right];
                    }
                }
            foreach (int ring in _touched)
                if (!_onRing[ring] && _winding[ring] != 0) _containing.Add(ring);
            return _containing;
        }
    }
}
