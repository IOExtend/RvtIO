namespace RvtIO.Writing;

internal static partial class WallHeightWriter
{
    private sealed partial class OpeningTransition
    {
        private sealed class TopCutEdges
        {
            internal HashSet<int> Inactive { get; } = [];
            internal Dictionary<int, Frame> Unfaced { get; } = [];
        }

        private sealed record Notch(OpeningInterval Opening, History Edges, int Head, int HeadEdgeA, int HeadEdgeB,
            int Sill, int Left, int Right, int Back, int Front);

        private Dictionary<int, Edge> OpenCut(Dictionary<int, Face> faces, Dictionary<int, Edge> source,
            Dictionary<int, List<int[]>> contours, TopCutEdges topCut, HeightInterval height, Notch notch,
            Func<(int, int, int, int), bool, int> allocate)
        {
            var head = Find(faces, notch.Head);
            int back = notch.Back;
            int front = notch.Front;
            bool atHead = notch.Opening.Destination == OpeningCut.AtHead;
            if (head.Loops.Count != (atHead ? 0 : 1) || !SameCoordinate(head.Frame.Origin.Z, notch.Opening.Top)
                || head.Frame.X.Z != 0 || head.Frame.Y.Z != 0
                || Find(faces, back).Loops.Count != 0 || Find(faces, front).Loops.Count != 0)
                throw Layout();

            var axisFace = _wall.Target(-1, "VWall.m_pRefFaces@0", "Face");
            var axisPlane = _wall.Target(axisFace, "Face.m_pSurf", "Plane");
            Point Vector(string field) => new(_wall.Real(axisPlane, field + "@0"), _wall.Real(axisPlane, field + "@1"),
                _wall.Real(axisPlane, field + "@2"));
            var origin = Vector("Plane.m_origin");
            var axis = Vector("Plane.m_xVec");
            double Station(Point p) => p.Minus(origin).Dot(axis);

            var edgeA = Find(source, notch.HeadEdgeA);
            var edgeB = Find(source, notch.HeadEdgeB);
            var (aLeft, aRight) = edgeA.Segment;
            var (bRight, bLeft) = edgeB.Segment;
            if (edgeA.A != _sideA || edgeB.A != _sideB || edgeA.B != notch.Head || edgeB.B != notch.Head
                || Math.Abs(Station(aLeft) - notch.Opening.Left) >= 1e-9 || Math.Abs(Station(bLeft) - notch.Opening.Left) >= 1e-9
                || Math.Abs(Station(aRight) - notch.Opening.Right) >= 1e-9 || Math.Abs(Station(bRight) - notch.Opening.Right) >= 1e-9
                || !new[] { aLeft, aRight, bLeft, bRight }.All(p => SameCoordinate(p.Z, notch.Opening.Top)))
                throw Layout();

            int AcrossHead(int kind) => notch.Edges.Get((3, kind, -1, -1));
            var topBorders = new Dictionary<int, (int Face, Segment Segment)>
            {
                [AcrossHead(3)] = (back, new(bRight, bLeft)),
                [AcrossHead(6)] = (notch.Left, new(bLeft, aLeft)),
                [AcrossHead(10)] = (front, new(aLeft, aRight)),
                [AcrossHead(14)] = (notch.Right, new(aRight, bRight)),
            };
            var revealSides = new HashSet<int> { SideEdge(_sideA, notch.Left), SideEdge(_sideA, notch.Right),
                SideEdge(_sideB, notch.Left), SideEdge(_sideB, notch.Right) };
            var edges = new Dictionary<int, Edge>();
            foreach (var (tag, original) in source)
            {
                if (!atHead && (tag == notch.HeadEdgeA || tag == notch.HeadEdgeB))
                    continue;
                var edge = original;
                if (atHead && (edge.A == notch.Head || edge.B == notch.Head))
                {
                    if (!topBorders.Remove(tag) && tag != notch.HeadEdgeA && tag != notch.HeadEdgeB)
                        throw Layout();
                }
                else if (topBorders.Remove(tag, out var border))
                {
                    if (!(edge.A == notch.Head && edge.B == border.Face || edge.B == notch.Head && edge.A == border.Face)
                        || !edge.Segment.Matches(border.Segment) && !edge.Segment.Matches(new(border.Segment.Last, border.Segment.First)))
                        throw Layout();
                    edge.Node.SetHeadEdgeActive(false);
                    edge = edge with { A = border.Face, B = -1, Segment = border.Segment };
                    if (!topCut.Unfaced.TryAdd(tag, head.Frame))
                        throw Layout();
                }
                else if (edge.A == notch.Head || edge.B == notch.Head || (edge.A < 0 || edge.B < 0) && !topCut.Unfaced.ContainsKey(tag))
                    throw Layout();
                else if (revealSides.Remove(tag))
                {
                    if (SameCoordinate(edge.Segment.First.Z, notch.Opening.Top) == SameCoordinate(edge.Segment.Last.Z, notch.Opening.Top))
                        throw Layout();
                    edge = edge with { Segment = edge.Segment.MoveZ(notch.Opening.Top, height.NewTop) };
                }
                edges.Add(tag, edge);
            }
            if (topBorders.Count != 0 || revealSides.Count != 0 || !atHead && !faces.Remove(notch.Head))
                throw Layout();

            var roof = contours[_roof];
            int roofA = 0, roofB = 0, leftEnd = 0, rightEnd = 0, roofLoop = -1;
            for (int i = 0; i < roof.Count; i++)
            {
                int[] loop = roof[i];
                int a = Only(loop.Where(tag => OppositeRoofFace(tag) == _sideA));
                int b = Only(loop.Where(tag => OppositeRoofFace(tag) == _sideB));
                var (aHigh, aLow) = Find(edges, a).Segment;
                var (bLow, bHigh) = Find(edges, b).Segment;
                if (Station(aLow) >= notch.Opening.Left || Station(aHigh) <= notch.Opening.Right
                    || Station(bLow) >= notch.Opening.Left || Station(bHigh) <= notch.Opening.Right)
                    continue;
                if (roofLoop != -1 || loop.Length != 4 || topCut.Inactive.Contains(a) || topCut.Inactive.Contains(b))
                    throw Layout();
                roofLoop = i;
                roofA = a;
                roofB = b;
                leftEnd = EndAt(loop, aLow, bLow);
                rightEnd = EndAt(loop, aHigh, bHigh);
            }
            if (roofLoop == -1 || leftEnd == rightEnd)
                throw Layout();

            // The two wall sides allocate their splits in order, then the roof creates its four intersections.
            var (aRest, aStart) = Split(roofA, Station, notch.Opening.Left, height.NewTop);
            if (!atHead)
                _ = Split(AcrossHead(12), p => p.Z, height.NewTop, height.NewTop);
            var (aEnd, aGap) = Split(aRest, Station, notch.Opening.Right, height.NewTop);
            if (!atHead)
                _ = Split(AcrossHead(16), p => p.Z, height.NewTop, height.NewTop);
            var (bStart, bRest) = Split(roofB, Station, notch.Opening.Left, height.NewTop);
            if (!atHead)
                _ = Split(AcrossHead(8), p => p.Z, height.NewTop, height.NewTop);
            var (bGap, bEnd) = Split(bRest, Station, notch.Opening.Right, height.NewTop);
            if (!atHead)
                _ = Split(AcrossHead(17), p => p.Z, height.NewTop, height.NewTop);

            _ = RoofEdge(back, bLeft, bRight);
            int leftRoof = RoofEdge(notch.Left, aLeft, bLeft);
            _ = RoofEdge(front, aLeft, aRight);
            int rightRoof = RoofEdge(notch.Right, bRight, aRight);
            if (edges.Count != source.Count + (atHead ? 12 : 18) || faces.Values.Count(f => f.Loops.Count != 0) < 9)
                throw Layout();

            int al = SideEdge(_sideA, notch.Left), ar = SideEdge(_sideA, notch.Right);
            int bl = SideEdge(_sideB, notch.Left), br = SideEdge(_sideB, notch.Right);
            int sillA = SideEdge(_sideA, notch.Sill), sillB = SideEdge(_sideB, notch.Sill);
            if (!atHead && !contours.Remove(notch.Head))
                throw Layout();
            contours[_sideA] = SideContours(_sideA, roofA, [aEnd, ar, sillA, al, aStart],
                [notch.HeadEdgeA, al, ar, sillA]);
            contours[_sideB] = SideContours(_sideB, roofB, [bStart, bl, sillB, br, bEnd],
                [notch.HeadEdgeB, bl, br, sillB]);
            roof[roofLoop] = [rightEnd, bEnd, rightRoof, aEnd];
            roof.Add([leftEnd, aStart, leftRoof, bStart]);
            contours[notch.Left] = [[AcrossHead(7), bl, leftRoof, al]];
            contours[notch.Right] = [[AcrossHead(15), ar, rightRoof, br]];
            topCut.Inactive.UnionWith([roofA, roofB, aRest, aGap, bRest, bGap]);
            return edges;

            int SideEdge(int side, int face) => notch.Edges.Get((17, side, face, 0));

            int OppositeRoofFace(int tag)
            {
                var edge = Find(edges, tag);
                return edge.Incident(1 - edge.Side(_roof));
            }

            int EndAt(int[] roofEdges, Point a, Point b) => Only(roofEdges.Where(tag =>
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

            int RoofEdge(int face, Point start, Point end) => AddEdge((17, _roof, face, 0),
                new(Find(edges, roofA).Node, _roof, face, new(start.WithZ(height.NewTop), end.WithZ(height.NewTop))));

            int AddEdge((int, int, int, int) key, Edge template)
            {
                int tag = allocate(key, key.Item1 == 4 && (template.A == _roof || template.B == _roof));
                if (source.ContainsKey(tag) || faces.ContainsKey(tag) || tag == notch.Head)
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
