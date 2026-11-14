namespace RvtIO.Parsing;

internal static partial class GRepParserV2
{
    /// <summary>Work units per input vertex and per doubling of the vertex count that one trim triangulation may spend
    /// (see <see cref="TrimWorkBudget"/>).</summary>
    private const int TrimTriangulationWorkFactor = 256;

    /// <summary>Work units every trim triangulation may spend on top of the near-linear part, a few seconds: loops that defeat
    /// the indexes, such as a comb of tall notches whose every tooth crosses every winding test, still triangulate as before
    /// when they have a few thousand vertices.</summary>
    private const long TrimTriangulationWorkAllowance = 1L << 26;

    /// <summary>The work one trim triangulation may still spend, in orientation tests and visited index entries. The bridges to
    /// inner loops and the ear clipping stay quadratic for adversarial loops; past the budget the triangulation fails, as it
    /// does for a degenerate loop, instead of running for hours.</summary>
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

    /// <summary>A uniform grid of square-ish cells over points of the UV plane. A triangle query visits every point of the
    /// cells the triangle, widened by <see cref="TrimMargin"/>, passes through, so it misses no point that the decimal
    /// predicates of the query could accept.</summary>
    private sealed class TrimPointGrid
    {
        private readonly IReadOnlyList<double[]> _points;
        private readonly List<int>?[] _cells;
        private readonly int _columns, _rows;
        private readonly double _minX, _minY, _scaleX, _scaleY;
        private readonly TrimWorkBudget _budget;

        internal TrimPointGrid(IReadOnlyList<double[]> points, TrimWorkBudget budget)
        {
            _points = points;
            _budget = budget;
            budget.Spend(points.Count);
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            foreach (var point in points)
            {
                minX = Math.Min(minX, point[0]); maxX = Math.Max(maxX, point[0]);
                minY = Math.Min(minY, point[1]); maxY = Math.Max(maxY, point[1]);
            }
            double width = maxX > minX ? maxX - minX : 0, height = maxY > minY ? maxY - minY : 0;
            int count = Math.Max(1, points.Count);
            int Cells(double along, double across) =>
                along == 0 ? 1 : across == 0 ? count : (int)Math.Clamp(Math.Sqrt(count * along / across), 1, count);
            _columns = Cells(width, height);
            _rows = Cells(height, width);
            _minX = double.IsFinite(minX) ? minX : 0;
            _minY = double.IsFinite(minY) ? minY : 0;
            _scaleX = width > 0 ? _columns / width : 0;
            _scaleY = height > 0 ? _rows / height : 0;
            _cells = new List<int>?[_columns * _rows];
            for (int index = 0; index < points.Count; index++)
                (_cells[Cell(points[index])] ??= new List<int>()).Add(index);
        }

        // Monotone in the coordinate, so a point between two coordinates falls in a cell between their cells.
        private int Column(double x) => (int)Math.Clamp((x - _minX) * _scaleX, 0, _columns - 1);
        private int Row(double y) => (int)Math.Clamp((y - _minY) * _scaleY, 0, _rows - 1);
        private int Cell(double[] point) => Row(point[1]) * _columns + Column(point[0]);

        internal void Remove(int index)
        {
            var cell = _cells[Cell(_points[index])]!;
            _budget.Spend(cell.Count);
            cell.Remove(index);
        }

        /// <summary>Calls <paramref name="accept"/> for the points of the cells of the closed triangle (a segment when two
        /// corners are equal) until it returns true; the point it accepted then, else -1.</summary>
        internal int FirstInTriangle(double[] a, double[] b, double[] c, Func<int, bool> accept)
        {
            double minX = Math.Min(a[0], Math.Min(b[0], c[0])), maxX = Math.Max(a[0], Math.Max(b[0], c[0]));
            double minY = Math.Min(a[1], Math.Min(b[1], c[1])), maxY = Math.Max(a[1], Math.Max(b[1], c[1]));
            double marginX = TrimMargin(minX, maxX), marginY = TrimMargin(minY, maxY);
            int firstRow = Row(minY - marginY), lastRow = Row(maxY + marginY);
            for (int row = firstRow; row <= lastRow; row++)
            {
                // The part of the triangle in the horizontal strip of the row: its corners in the strip and the crossings of
                // its edges with the strip's sides bound it.
                double low = minY - marginY, high = maxY + marginY;
                if (_scaleY > 0)
                {
                    // The strip is computed back from the row, so it is widened for the rounding of the grid too.
                    double slack = marginY + 1e-9 * (Math.Abs(_minY) + _rows / _scaleY);
                    low = Math.Max(low, _minY + row / _scaleY - slack);
                    high = Math.Min(high, _minY + (row + 1) / _scaleY + slack);
                }
                double left = double.PositiveInfinity, right = double.NegativeInfinity;
                void Include(double x)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                }
                void Edge(double[] first, double[] second)
                {
                    if (first[1] >= low && first[1] <= high) Include(first[0]);
                    double edgeLow = Math.Min(first[1], second[1]), edgeHigh = Math.Max(first[1], second[1]);
                    if (edgeLow == edgeHigh) return;
                    foreach (double y in (ReadOnlySpan<double>)[low, high])
                        if (y >= edgeLow && y <= edgeHigh)
                            Include(first[0]
                                + Math.Clamp((y - first[1]) / (second[1] - first[1]), 0, 1) * (second[0] - first[0]));
                }
                Edge(a, b);
                Edge(b, c);
                Edge(c, a);
                _budget.Spend();
                if (left > right) continue;
                int firstColumn = Column(left - marginX), lastColumn = Column(right + marginX);
                _budget.Spend(lastColumn - firstColumn + 1);
                for (int column = firstColumn; column <= lastColumn; column++)
                {
                    if (_cells[row * _columns + column] is not { } cell) continue;
                    _budget.Spend(cell.Count);
                    foreach (int index in cell)
                        if (accept(index)) return index;
                }
            }
            return -1;
        }
    }

    /// <summary>The edges of closed rings bucketed into horizontal bands. A strict winding test of a point over the edges of its
    /// band is the test over every edge: the other edges neither cross the horizontal through the point nor, widened by
    /// <see cref="TrimMargin"/>, hold it.</summary>
    private sealed class TrimEdgeBands
    {
        private readonly IReadOnlyList<List<double[]>> _rings;
        private readonly List<(int Ring, int Edge)>?[] _bands;
        private readonly double _minY, _scale;
        private readonly int[] _winding;
        private readonly bool[] _onRing, _touchedRing;
        private readonly List<int> _touched = new(), _containing = new();
        private readonly Func<double[], double[], double[], decimal> _orientation;
        private readonly Func<double[], double[], double[], bool> _onSegment;
        private readonly TrimWorkBudget _budget;

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
            double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
            foreach (var ring in rings)
                foreach (var point in ring)
                {
                    minY = Math.Min(minY, point[1]);
                    maxY = Math.Max(maxY, point[1]);
                }
            int count = maxY > minY ? Math.Max(1, (int)Math.Sqrt(edges)) : 1;
            _minY = double.IsFinite(minY) ? minY : 0;
            _scale = maxY > minY ? count / (maxY - minY) : 0;
            _bands = new List<(int, int)>?[count];
            for (int ringIndex = 0; ringIndex < rings.Count; ringIndex++)
            {
                var ring = rings[ringIndex];
                for (int edgeIndex = 0; edgeIndex < ring.Count; edgeIndex++)
                {
                    double first = ring[edgeIndex][1], second = ring[(edgeIndex + 1) % ring.Count][1];
                    double low = Math.Min(first, second), high = Math.Max(first, second);
                    double margin = TrimMargin(low, high);
                    int lastBand = Band(high + margin);
                    for (int band = Band(low - margin); band <= lastBand; band++)
                    {
                        budget.Spend();
                        (_bands[band] ??= new List<(int, int)>()).Add((ringIndex, edgeIndex));
                    }
                }
            }
        }

        private int Band(double y) => (int)Math.Clamp((y - _minY) * _scale, 0, _bands.Length - 1);

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
                if (_bands[band] is not { } edges) continue;
                _budget.Spend(edges.Count);
                foreach (var (ring, edge) in edges)
                    if (accept(ring, edge)) return true;
            }
            return false;
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
            if (_bands[Band(point[1])] is not { } edges) return _containing;
            _budget.Spend(edges.Count);
            foreach (var (ringIndex, edgeIndex) in edges)
            {
                var ring = _rings[ringIndex];
                var first = ring[edgeIndex];
                var second = ring[(edgeIndex + 1) % ring.Count];
                if (!_touchedRing[ringIndex])
                {
                    _touchedRing[ringIndex] = true;
                    _touched.Add(ringIndex);
                }
                decimal orientation = _orientation(first, second, point);
                if (orientation == 0 && _onSegment(point, first, second)) _onRing[ringIndex] = true;
                else if (first[1] <= point[1] && point[1] < second[1] && orientation > 0) _winding[ringIndex]++;
                else if (second[1] <= point[1] && point[1] < first[1] && orientation < 0) _winding[ringIndex]--;
            }
            foreach (int ring in _touched)
                if (!_onRing[ring] && _winding[ring] != 0) _containing.Add(ring);
            return _containing;
        }
    }
}
