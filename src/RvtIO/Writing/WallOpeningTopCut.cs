namespace RvtIO.Writing;

internal static partial class WallHeightWriter
{
    private sealed partial class OpeningTransition
    {
        private sealed class BoundaryCutEdges
        {
            internal HashSet<int> Inactive { get; } = [];
            internal Dictionary<int, Frame> Unfaced { get; } = [];
        }

        private sealed record Notch(OpeningInterval Opening, History Edges, int CrossingFace, int CrossingEdgeA, int CrossingEdgeB,
            int OppositeFace, int Left, int Right, int Back, int Front);

        private Dictionary<int, Edge> OpenCut(Dictionary<int, Face> faces, Dictionary<int, Edge> source,
            Dictionary<int, List<int[]>> contours, BoundaryCutEdges topCut, HeightInterval height, Notch notch,
            Func<(int, int, int, int), bool, int> allocate)
        {
            var crossingFace = Find(faces, notch.CrossingFace);
            int back = notch.Back;
            int front = notch.Front;
            bool contact = notch.Opening.Destination is OpeningCut.AtHead or OpeningCut.AtBaseSill;
            double crossing = _throughBottom ? notch.Opening.Bottom : notch.Opening.Top;
            double boundary = _throughBottom ? height.NewBase : height.NewTop;
            if (crossingFace.Loops.Count != (contact ? 0 : 1) || !SameCoordinate(crossingFace.Frame.Origin.Z, crossing)
                || crossingFace.Frame.X.Z != 0 || crossingFace.Frame.Y.Z != 0
                || Find(faces, back).Loops.Count != 0 || Find(faces, front).Loops.Count != 0)
                throw Layout();

            var axisFace = _wall.Target(-1, "VWall.m_pRefFaces@0", "Face");
            var axisPlane = _wall.Target(axisFace, "Face.m_pSurf", "Plane");
            Point Vector(string field) => new(_wall.Real(axisPlane, field + "@0"), _wall.Real(axisPlane, field + "@1"),
                _wall.Real(axisPlane, field + "@2"));
            var origin = Vector("Plane.m_origin");
            var axis = Vector("Plane.m_xVec");
            double Station(Point p) => p.Minus(origin).Dot(axis);

            var edgeA = Find(source, notch.CrossingEdgeA);
            var edgeB = Find(source, notch.CrossingEdgeB);
            var (aLeft, aRight) = OrientForStation(edgeA.Segment);
            var (bRight, bLeft) = OrientForStation(edgeB.Segment);
            if (edgeA.A != _sideA || edgeB.A != _sideB || edgeA.B != notch.CrossingFace || edgeB.B != notch.CrossingFace
                || Math.Abs(Station(aLeft) - notch.Opening.Left) >= 1e-9 || Math.Abs(Station(bLeft) - notch.Opening.Left) >= 1e-9
                || Math.Abs(Station(aRight) - notch.Opening.Right) >= 1e-9 || Math.Abs(Station(bRight) - notch.Opening.Right) >= 1e-9
                || !new[] { aLeft, aRight, bLeft, bRight }.All(p => SameCoordinate(p.Z, crossing)))
                throw Layout();

            int CutterEdge(int kind) => notch.Edges.Get((3, kind, -1, -1));
            var borderEdges = _throughBottom ? new Dictionary<int, (int Face, Segment Segment)>
            {
                [CutterEdge(4)] = (back, new(bLeft, bRight)),
                [CutterEdge(7)] = (notch.Left, new(aLeft, bLeft)),
                [CutterEdge(11)] = (front, new(aRight, aLeft)),
                [CutterEdge(15)] = (notch.Right, new(bRight, aRight)),
            } : new Dictionary<int, (int Face, Segment Segment)>
            {
                [CutterEdge(3)] = (back, new(bRight, bLeft)),
                [CutterEdge(6)] = (notch.Left, new(bLeft, aLeft)),
                [CutterEdge(10)] = (front, new(aLeft, aRight)),
                [CutterEdge(14)] = (notch.Right, new(aRight, bRight)),
            };
            var revealSides = new HashSet<int> { SideEdge(_sideA, notch.Left), SideEdge(_sideA, notch.Right),
                SideEdge(_sideB, notch.Left), SideEdge(_sideB, notch.Right) };
            var edges = new Dictionary<int, Edge>();
            foreach (var (tag, original) in source)
            {
                if (!contact && (tag == notch.CrossingEdgeA || tag == notch.CrossingEdgeB))
                    continue;
                var edge = original;
                if (contact && (edge.A == notch.CrossingFace || edge.B == notch.CrossingFace))
                {
                    if (!borderEdges.Remove(tag) && tag != notch.CrossingEdgeA && tag != notch.CrossingEdgeB)
                        throw Layout();
                    if (_throughBottom && tag == notch.CrossingEdgeA)
                        edge = edge with { Segment = new(aLeft, aRight) };
                }
                else if (borderEdges.Remove(tag, out var border))
                {
                    if (!(edge.A == notch.CrossingFace && edge.B == border.Face || edge.B == notch.CrossingFace && edge.A == border.Face)
                        || !edge.Segment.Matches(border.Segment) && !edge.Segment.Matches(new(border.Segment.Last, border.Segment.First)))
                        throw Layout();
                    edge.Node.SetOpeningBorderActive(false);
                    edge = edge with { A = border.Face, B = -1, Segment = border.Segment };
                    if (!topCut.Unfaced.TryAdd(tag, crossingFace.Frame))
                        throw Layout();
                }
                else if (edge.A == notch.CrossingFace || edge.B == notch.CrossingFace || (edge.A < 0 || edge.B < 0) && !topCut.Unfaced.ContainsKey(tag))
                    throw Layout();
                else if (revealSides.Remove(tag))
                {
                    if (SameCoordinate(edge.Segment.First.Z, crossing) == SameCoordinate(edge.Segment.Last.Z, crossing))
                        throw Layout();
                    edge = edge with { Segment = edge.Segment.MoveZ(crossing, boundary) };
                }
                edges.Add(tag, edge);
            }
            if (borderEdges.Count != 0 || revealSides.Count != 0 || !contact && !faces.Remove(notch.CrossingFace))
                throw Layout();

            var wallBoundary = contours[_boundary];
            int boundaryA = 0, boundaryB = 0, leftEnd = 0, rightEnd = 0, boundaryLoop = -1;
            for (int i = 0; i < wallBoundary.Count; i++)
            {
                int[] loop = wallBoundary[i];
                int a = Only(loop.Where(tag => OppositeBoundaryFace(tag) == _sideA));
                int b = Only(loop.Where(tag => OppositeBoundaryFace(tag) == _sideB));
                var segmentA = Find(edges, a).Segment;
                var segmentB = Find(edges, b).Segment;
                var (aHigh, aLow) = OrientForStation(segmentA);
                var (bLow, bHigh) = OrientForStation(segmentB);
                if (Station(aLow) >= notch.Opening.Left || Station(aHigh) <= notch.Opening.Right
                    || Station(bLow) >= notch.Opening.Left || Station(bHigh) <= notch.Opening.Right)
                    continue;
                if (boundaryLoop != -1 || loop.Length != 4 || topCut.Inactive.Contains(a) || topCut.Inactive.Contains(b))
                    throw Layout();
                boundaryLoop = i;
                boundaryA = a;
                boundaryB = b;
                leftEnd = EndAt(loop, aLow, bLow);
                rightEnd = EndAt(loop, aHigh, bHigh);
            }
            if (boundaryLoop == -1 || leftEnd == rightEnd)
                throw Layout();

            // Both boundary faces split the wall sides left to right, with opposite edge directions.
            var (aFirst, aSecond) = Split(boundaryA, Station, notch.Opening.Left, boundary);
            var (aRest, aStart) = _throughBottom ? (aSecond, aFirst) : (aFirst, aSecond);
            if (!contact)
                _ = Split(CutterEdge(12), p => p.Z, boundary, boundary);
            (aFirst, aSecond) = Split(aRest, Station, notch.Opening.Right, boundary);
            var (aEnd, aGap) = _throughBottom ? (aSecond, aFirst) : (aFirst, aSecond);
            if (!contact)
                _ = Split(CutterEdge(16), p => p.Z, boundary, boundary);
            var (bFirst, bSecond) = Split(boundaryB, Station, notch.Opening.Left, boundary);
            var (bStart, bRest) = _throughBottom ? (bSecond, bFirst) : (bFirst, bSecond);
            if (!contact)
                _ = Split(CutterEdge(8), p => p.Z, boundary, boundary);
            (bFirst, bSecond) = Split(bRest, Station, notch.Opening.Right, boundary);
            var (bGap, bEnd) = _throughBottom ? (bSecond, bFirst) : (bFirst, bSecond);
            if (!contact)
                _ = Split(CutterEdge(17), p => p.Z, boundary, boundary);

            _ = BoundaryEdge(back, bLeft, bRight);
            int leftBoundary = _throughBottom ? BoundaryEdge(notch.Left, bLeft, aLeft) : BoundaryEdge(notch.Left, aLeft, bLeft);
            _ = BoundaryEdge(front, aLeft, aRight);
            int rightBoundary = _throughBottom ? BoundaryEdge(notch.Right, aRight, bRight) : BoundaryEdge(notch.Right, bRight, aRight);
            if (edges.Count != source.Count + (contact ? 12 : 18) || faces.Values.Count(f => f.Loops.Count != 0) < 9)
                throw Layout();

            int al = SideEdge(_sideA, notch.Left), ar = SideEdge(_sideA, notch.Right);
            int bl = SideEdge(_sideB, notch.Left), br = SideEdge(_sideB, notch.Right);
            int oppositeA = SideEdge(_sideA, notch.OppositeFace), oppositeB = SideEdge(_sideB, notch.OppositeFace);
            if (!contact && !contours.Remove(notch.CrossingFace))
                throw Layout();
            contours[_sideA] = SideContours(_sideA, boundaryA,
                _throughBottom ? [aStart, al, oppositeA, ar, aEnd] : [aEnd, ar, oppositeA, al, aStart],
                [notch.CrossingEdgeA, al, ar, oppositeA]);
            contours[_sideB] = SideContours(_sideB, boundaryB,
                _throughBottom ? [bEnd, br, oppositeB, bl, bStart] : [bStart, bl, oppositeB, br, bEnd],
                [notch.CrossingEdgeB, bl, br, oppositeB]);
            wallBoundary[boundaryLoop] = _throughBottom ? [rightEnd, aEnd, rightBoundary, bEnd] : [rightEnd, bEnd, rightBoundary, aEnd];
            wallBoundary.Add(_throughBottom ? [leftEnd, bStart, leftBoundary, aStart] : [leftEnd, aStart, leftBoundary, bStart]);
            contours[notch.Left] = [_throughBottom ? [CutterEdge(6), al, leftBoundary, bl] : [CutterEdge(7), bl, leftBoundary, al]];
            contours[notch.Right] = [_throughBottom ? [CutterEdge(14), br, rightBoundary, ar] : [CutterEdge(15), ar, rightBoundary, br]];
            topCut.Inactive.UnionWith([boundaryA, boundaryB, aRest, aGap, bRest, bGap]);
            return edges;

            Segment OrientForStation(Segment segment) =>
                _throughBottom ? new(segment.Last, segment.First) : segment;

            int SideEdge(int side, int face) => notch.Edges.Get((17, side, face, 0));

            int OppositeBoundaryFace(int tag)
            {
                var edge = Find(edges, tag);
                return edge.Incident(1 - edge.Side(_boundary));
            }

            int EndAt(int[] boundaryEdges, Point a, Point b) => Only(boundaryEdges.Where(tag =>
                Find(edges, tag).Segment.Matches(new(a, b)) || Find(edges, tag).Segment.Matches(new(b, a))));

            List<int[]> SideContours(int side, int parent, int[] insert, HashSet<int> removed)
            {
                if (!contours.TryGetValue(side, out var loops) || loops.Count < 2 || loops[0].Count(tag => tag == parent) != 1)
                    throw Layout();
                var remainder = loops.Skip(1).Where(loop => !removed.SetEquals(loop)).ToArray();
                if (remainder.Length != loops.Count - 2)
                    throw Layout();
                int[] exterior = loops[0].SelectMany(tag => tag == parent ? insert : [tag]).ToArray();
                return [exterior, .. remainder];
            }

            static int Only(IEnumerable<int> entries) => entries.Take(2).ToArray() is [var tag] ? tag : throw Layout();

            (int First, int Second) Split(int parent, Func<Point, double> coordinate, double position, double z)
            {
                var edge = Find(edges, parent);
                var (first, last) = edge.Segment;
                double a = coordinate(first), b = coordinate(last);
                if (position <= Math.Min(a, b) || position >= Math.Max(a, b))
                    throw Layout();
                double ratio = (position - a) / (b - a);
                var middle = first.Plus(last.Minus(first).Scale(ratio)).WithZ(z);
                return (AddEdge((4, parent, 0, -1), edge with { Segment = new(first, middle) }),
                    AddEdge((4, parent, 1, -1), edge with { Segment = new(middle, last) }));
            }

            int BoundaryEdge(int face, Point start, Point end) => AddEdge((17, _boundary, face, 0),
                new(Find(edges, boundaryA).Node, _boundary, face, new(start.WithZ(boundary), end.WithZ(boundary))));

            int AddEdge((int, int, int, int) key, Edge template)
            {
                int tag = allocate(key, key.Item1 == 4 && (template.A == _boundary || template.B == _boundary));
                if (source.ContainsKey(tag) || faces.ContainsKey(tag) || tag == notch.CrossingFace)
                    throw Layout();
                var edge = template.Node.Clone();
                edge.SetTag(tag);
                if (!edges.TryAdd(tag, template with { Node = edge }))
                    throw Layout();
                return tag;
            }
        }
    }
}
