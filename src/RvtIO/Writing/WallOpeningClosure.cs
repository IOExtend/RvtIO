namespace RvtIO.Writing;

internal static partial class WallHeightWriter
{
    private sealed partial class OpeningTransition
    {
        internal byte[] Rebuild(Archive geometry, HeightInterval height, IReadOnlyList<OpeningInterval> openings, bool removesCuts,
            IReadOnlyList<(OpeningInterval Opening, Archive Geometry)> cutters, int idWidth, double baseOffset, bool replay)
        {
            var archive = new GeometryArchive(geometry);
            var solid = archive.Root.Target("GGroup.m_subNodes@4");
            var faces = new Dictionary<int, Face>();
            var sourceEdges = new Dictionary<int, GeometryArchive.Node>();
            foreach (var node in solid.OwnedList("GBRep.m_pFaces"))
            {
                var plane = node.Target("Face.m_pSurf");
                if (!faces.TryAdd(Tag(node), new(node, plane, Frame.Read(plane))))
                    throw Layout();
            }
            foreach (var node in solid.OwnedList("Geometry.m_pEdges"))
                if (!sourceEdges.TryAdd(Tag(node), node))
                    throw Layout();

            if (sourceEdges.Keys.Any(faces.ContainsKey)
                || !replay && !removesCuts && cutters.Count == 0 && (_head == _headEdgeA || _head == _headEdgeB))
                throw Layout();
            foreach (var face in faces.Values)
                ReadLoops(face, sourceEdges);
            var byNode = faces.ToDictionary(p => p.Value.Node, p => p.Key);
            var edges = new Dictionary<int, Edge>();
            foreach (var (tag, node) in sourceEdges)
            {
                int a = FaceTag(node, 0);
                int b = FaceTag(node, 1);
                if (a == b || node.Integer("GEdge.m_interiorEdgePnts#") != 0)
                    throw Layout();
                Segment segment = a >= 0 ? faces[a].Frame.World(node, 0) : faces[b].Frame.World(node, 1);
                if (a >= 0 && b >= 0 && !segment.Matches(faces[b].Frame.World(node, 1)))
                    throw Layout();
                edges.Add(tag, new(node, a, b, segment));
            }

            if (replay || cutters.Count != 0)
            {
                if (replay)
                {
                    var cutGenerators = openings.Select(o => _wall.Int32(o.Step, "GeomGenerator.m_id")).ToHashSet();
                    if (cutGenerators.Count != openings.Count || cutGenerators.Contains(_baseGenerator) || cutGenerators.Any(g => g <= 0))
                        throw Layout();
                    faces = faces.Where(p => !cutGenerators.Contains(Generator(p.Key))).ToDictionary();
                    edges = edges.Where(p => !cutGenerators.Contains(Generator(p.Key))).ToDictionary();
                    var contours = UncutContours(faces, edges, true);
                    if (faces.Count != 6 || edges.Count != 12 || contours.Count != 6)
                        throw Layout();
                    foreach (var (tag, loops) in contours)
                    {
                        var face = Find(faces, tag);
                        face.Contours.Clear();
                        face.Contours.AddRange(loops);
                    }
                }
                edges = ActivateCuts(faces, edges, cutters, height, idWidth, baseOffset);
            }
            else if (removesCuts)
            {
                var removed = openings.Where(o => o.Cut != o.Destination)
                    .Select(o => _wall.Int32(o.Step, "GeomGenerator.m_id")).ToHashSet();
                if (removed.Contains(_baseGenerator) || removed.Contains(0) || removed.Count != openings.Count(o => o.Cut != o.Destination))
                    throw Layout();
                faces = faces.Where(p => !removed.Contains(Generator(p.Key))).ToDictionary();
                edges = edges.Where(p => !removed.Contains(Generator(p.Key))).ToDictionary();
                RebuildSolid(faces, edges, UncutContours(faces, edges, openings.Any(o => o.Cut == OpeningCut.ThroughTop)));
            }
            else if (_opening.Cut == OpeningCut.Interior)
            {
                var contours = faces.Where(pair => pair.Value.Loops.Count != 0).ToDictionary(pair => pair.Key, pair => pair.Value.Contours);
                var notch = new Notch(_opening, _openingEdges, _head, _headEdgeA, _headEdgeB, _sill, _left, _right,
                    _openingFaces.Get((5, 2, -1, 0)), _openingFaces.Get((5, 9, -1, 0)));
                var topCut = new TopCutEdges();
                edges = OpenCut(faces, edges, contours, topCut, height, notch, (key, _) => AllocateEdge(key));
                if (_addedTags is not (0 or 20))
                    throw Layout();
                RebuildSolid(faces, edges, contours, topCut);
            }
            else
            {
                edges = CloseCut(faces, edges, height);
                RebuildSolid(faces, edges, NewClosedContours(faces, openings.Count));
            }
            solid.ReplaceOwnedList("GBRep.m_pFaces", faces.Values.Select(f => f.Node).ToArray());
            solid.ReplaceOwnedList("Geometry.m_pEdges", edges.Values.Select(e => e.Node).ToArray());
            return archive.Build();

            int FaceTag(GeometryArchive.Node edge, int side) => edge.Ref($"GEdge.m_pFace@{side}") is not { } node
                ? -1 : byNode.TryGetValue(node, out int tag) ? tag : throw Layout();
        }

        private Dictionary<int, List<int[]>> UncutContours(Dictionary<int, Face> faces, Dictionary<int, Edge> edges, bool restoreRoof)
        {
            if (!faces.Keys.Where(tag => Generator(tag) == _baseGenerator).ToHashSet().SetEquals(_baseFaces.Tags)
                || !edges.Keys.Where(tag => Generator(tag) == _baseGenerator).ToHashSet().SetEquals(_baseEdges.Tags)
                || _baseFaces.Tags.Count() != 6 || _baseEdges.Tags.Count() != 12)
                throw Layout();

            var contours = new Dictionary<int, List<int[]>>();
            foreach (var (tag, face) in faces)
            {
                if (face.Contours.Count == 0)
                {
                    if (Generator(tag) == _baseGenerator)
                        throw Layout();
                    continue;
                }

                if (restoreRoof && tag == _roof)
                {
                    contours.Add(tag, [BaseRoofOutline()]);
                    continue;
                }

                bool restoreSide = restoreRoof && (tag == _sideA || tag == _sideB);
                var kept = new List<int[]>();
                if (restoreSide)
                    kept.Add(BaseSideOutline(tag == _sideA ? 0 : 1));
                foreach (var loop in face.Contours.Skip(restoreSide ? 1 : 0))
                {
                    int retained = loop.Count(edges.ContainsKey);
                    if (retained == loop.Length)
                        kept.Add(loop);
                    else if (retained != 0)
                        throw Layout();
                }
                if (kept.Count == 0)
                    throw Layout();
                contours.Add(tag, kept);
            }
            return contours;
        }

        private Dictionary<int, Edge> CloseCut(Dictionary<int, Face> faces, Dictionary<int, Edge> source, HeightInterval height)
        {
            if (new[] { _head, _headEdgeA, _headEdgeB }.Any(tag => faces.ContainsKey(tag) || source.ContainsKey(tag)))
                throw Layout();
            var sill = Find(faces, _sill);
            if (sill.Loops is not [var sillLoop] || !SameCoordinate(sill.Frame.Origin.Z, _opening.Bottom)
                || sill.Frame.X.Z != 0 || sill.Frame.Y.Z != 0)
                throw Layout();
            faces.Add(_head, CreateHead(sill, sillLoop));

            // Retain split edges in history so a later regeneration can reuse their tags.
            var separated = _openingEdges.SelectTags(k => k.A == 4 || k.A == 17 && k.B == _roof).ToHashSet();
            var upperBorder = new[] { 3, 6, 10, 14 }.Select(k => _openingEdges.Get((3, k, -1, -1))).ToHashSet();
            var revealSides = RevealSides();
            var edges = new Dictionary<int, Edge>();
            foreach (var (tag, original) in source)
            {
                if (separated.Contains(tag))
                    continue;
                var edge = original;
                if (edge.A < 0 || edge.B < 0)
                {
                    if (!upperBorder.Remove(tag))
                        throw Layout();
                    edge.Node.SetHeadEdgeActive(true);
                    edge = edge with { A = _head, B = edge.A >= 0 ? edge.A : edge.B };
                }
                else if (revealSides.Remove(tag))
                {
                    if (SameCoordinate(edge.Segment.First.Z, height.NewTop) == SameCoordinate(edge.Segment.Last.Z, height.NewTop))
                        throw Layout();
                    edge = edge with { Segment = edge.Segment.MoveZ(height.NewTop, _opening.Top) };
                }
                else if (Generator(tag) != _baseGenerator &&
                    (SameCoordinate(edge.Segment.First.Z, height.NewTop) || SameCoordinate(edge.Segment.Last.Z, height.NewTop)))
                    throw Layout();
                edges.Add(tag, edge);
            }
            if (upperBorder.Count != 0 || revealSides.Count != 0)
                throw Layout();
            InsertHeadEdge(_sideA, _headEdgeA);
            InsertHeadEdge(_sideB, _headEdgeB);
            return edges;

            void InsertHeadEdge(int face, int tag)
            {
                var template = Find(edges, SideEdge(face, _sill));
                if (template.A != face || template.B != _sill)
                    throw Layout();
                var node = template.Node.Clone();
                node.SetTag(tag);
                edges.Add(tag, new(node, face, _head,
                    new(template.Segment.Last.WithZ(_opening.Top), template.Segment.First.WithZ(_opening.Top))));
            }
        }

        private void RebuildSolid(Dictionary<int, Face> faces, Dictionary<int, Edge> edges,
            Dictionary<int, List<int[]>> loops, TopCutEdges? topCut = null)
        {
            var physicalEdges = new HashSet<(int Tag, int Side)>();
            foreach (var (tag, edge) in edges)
            {
                for (int side = 0; side < 2; side++)
                {
                    int faceTag = edge.Incident(side);
                    Face? face = faceTag < 0 ? null : Find(faces, faceTag);
                    var frame = face?.Frame ?? (topCut is not null && topCut.Unfaced.TryGetValue(tag, out var head) ? head : throw Layout());
                    edge.Node.SetWeak($"GEdge.m_pFace@{side}", face?.Node);
                    edge.Node.SetWeak($"GEdge.m_prev@{side}", null);
                    edge.Node.SetWeak($"GEdge.m_next@{side}", null);
                    var (first, last) = edge.Segment;
                    var uv1 = frame.Project(first);
                    var uv2 = frame.Project(last);
                    if (!frame.World(uv1).Near(first) || !frame.World(uv2).Near(last) || first.Near(last))
                        throw Layout();
                    edge.Node.SetPair($"EdgePnt.uv@{side}/0", uv1.U, uv2.U);
                    edge.Node.SetPair($"EdgePnt.uv@{side}/1", uv1.V, uv2.V);
                    if (topCut?.Inactive.Contains(tag) != true && loops.ContainsKey(faceTag) && loops.ContainsKey(edge.Incident(1 - side)))
                        physicalEdges.Add((tag, side));
                }
            }

            int sideLength = 4 * loops[_roof].Count;
            foreach (var (tag, contours) in loops)
            {
                var face = Find(faces, tag);
                var nodes = contours.Select((_, i) => i < face.Loops.Count ? face.Loops[i] : face.Loops[0].Clone()).ToArray();
                face.Node.SetOwned("GFace.m_pFirstLoop", nodes[0]);
                var allPoints = new List<Uv>();
                for (int i = 0; i < contours.Count; i++)
                {
                    var contour = contours[i];
                    var loop = nodes[i];
                    int expectedLength = i == 0 && (tag == _sideA || tag == _sideB) ? sideLength : 4;
                    if (contour.Length != expectedLength)
                        throw Layout();
                    loop.SetOwned("GEdgeLoop.m_nextLoop", i + 1 == nodes.Length ? null : nodes[i + 1]);
                    loop.SetWeak("GEdgeLoop.m_pFace", face.Node);
                    loop.SetWeak("GEdgeLoop.m_next", Find(edges, contour[0]).Node);
                    loop.SetWeak("GEdgeLoop.m_prev", Find(edges, contour[^1]).Node);
                    var points = new List<Uv>(2 * contour.Length);
                    for (int j = 0; j < contour.Length; j++)
                    {
                        int edgeTag = contour[j];
                        var edge = Find(edges, edgeTag);
                        int side = edge.Side(tag);
                        var previous = Find(edges, contour[(j + contour.Length - 1) % contour.Length]);
                        var next = Find(edges, contour[(j + 1) % contour.Length]);
                        if (!physicalEdges.Remove((edgeTag, side)) || !edge.Segment.Connects(previous.Segment, next.Segment))
                            throw Layout();
                        edge.Node.SetWeak($"GEdge.m_prev@{side}", j == 0 ? loop : previous.Node);
                        edge.Node.SetWeak($"GEdge.m_next@{side}", j + 1 == contour.Length ? loop : next.Node);
                        points.Add(face.Frame.Project(edge.Segment.First));
                        points.Add(face.Frame.Project(edge.Segment.Last));
                    }
                    SetEnvelope(loop, points);
                    allPoints.AddRange(points);
                }
                SetEnvelope(face.Plane, allPoints);
            }
            if (physicalEdges.Count != 0)
                throw Layout();
        }

        private static T Find<T>(IReadOnlyDictionary<int, T> entries, int tag) where T : class =>
            entries.TryGetValue(tag, out var result) ? result : throw Layout();

        private HashSet<int> RevealSides() =>
            [SideEdge(_sideA, _left), SideEdge(_sideA, _right), SideEdge(_sideB, _left), SideEdge(_sideB, _right)];

        private Face CreateHead(Face sill, GeometryArchive.Node sillLoop)
        {
            var node = sill.Node.Clone();
            var plane = sill.Plane.Clone();
            var filling = sill.Node.Target("GFace.m_pGFilling").Clone();
            var loop = sillLoop.Clone();
            node.SetTag(_head);
            node.SetOwned("Face.m_pSurf", plane);
            node.SetOwned("GFace.m_pGFilling", filling);
            node.SetOwned("GFace.m_pFirstLoop", loop);
            filling.SetWeak("GFilling.m_pGFace", node);
            filling.SetReal("FillPatternPlacer.m_origin@0", -filling.Real("FillPatternPlacer.m_origin@0"));
            filling.SetReal("FillPatternPlacer.m_dir@0", -filling.Real("FillPatternPlacer.m_dir@0"));
            var frame = sill.Frame with { Origin = sill.Frame.Origin.WithZ(_opening.Top), X = sill.Frame.X.Scale(-1) };
            plane.SetReal("Plane.m_origin@2", _opening.Top);
            plane.SetReal("Plane.m_xVec@0", frame.X.X);
            plane.SetReal("Plane.m_xVec@1", frame.X.Y);
            return new(node, plane, frame) { Loops = [loop] };
        }

        private static void ReadLoops(Face face, IReadOnlyDictionary<int, GeometryArchive.Node> sourceEdges)
        {
            var loop = face.Node.Ref("GFace.m_pFirstLoop");
            while (loop is not null)
            {
                if (face.Loops.Contains(loop) || loop.Ref("GEdgeLoop.m_pFace") != face.Node)
                    throw Layout();
                face.Loops.Add(loop);
                var edge = loop.Target("GEdgeLoop.m_next");
                var contour = new List<int>();
                while (edge != loop)
                {
                    int edgeTag = Tag(edge);
                    if (contour.Contains(edgeTag) || !sourceEdges.TryGetValue(edgeTag, out var verified) || verified != edge)
                        throw Layout();
                    contour.Add(edgeTag);
                    int side = edge.Ref("GEdge.m_pFace@0") == face.Node ? 0 : 1;
                    if (edge.Ref($"GEdge.m_pFace@{side}") != face.Node)
                        throw Layout();
                    edge = edge.Target($"GEdge.m_next@{side}");
                }
                face.Contours.Add(contour.ToArray());
                loop = loop.Ref("GEdgeLoop.m_nextLoop");
            }
        }

        private Dictionary<int, List<int[]>> NewClosedContours(Dictionary<int, Face> faces, int openingCount)
        {
            // Base wall and opening history keys name the edges, including currently untrimmed edges.
            int CutEdge(int kind) => _openingEdges.Get((3, kind, -1, -1));
            int leftHead = CutEdge(6), rightHead = CutEdge(14);
            int aLeft = SideEdge(_sideA, _left), bLeft = SideEdge(_sideB, _left);
            int aRight = SideEdge(_sideA, _right), bRight = SideEdge(_sideB, _right);
            var contours = faces.Where(p => p.Value.Loops.Count != 0).ToDictionary(p => p.Key, p => p.Value.Contours);
            if (!new[] { _sideA, _sideB, _roof, _left, _right, _head }.All(contours.ContainsKey))
                throw Layout();
            contours[_sideA] = SideContours(_sideA, BaseSideOutline(0),
                [SideEdge(_sideA, _sill), aLeft, _headEdgeA, aRight]);
            contours[_sideB] = SideContours(_sideB, BaseSideOutline(1),
                [SideEdge(_sideB, _sill), bRight, _headEdgeB, bLeft]);
            contours[_roof] = [BaseRoofOutline()];
            contours[_left] = [[leftHead, aLeft, CutEdge(7), bLeft]];
            contours[_right] = [[rightHead, bRight, CutEdge(15), aRight]];
            contours[_head] = [[leftHead, _headEdgeB, rightHead, _headEdgeA]];
            foreach (var (tag, loops) in contours)
                if (loops.Count != (tag == _sideA || tag == _sideB ? openingCount + 1 : 1))
                    throw Layout();
            return contours;

            List<int[]> SideContours(int side, int[] outside, int[] hole) =>
                [outside, .. contours[side].Skip(1).Append(hole).OrderBy(loop => loop.Min())];
        }

        private int[] BaseSideOutline(int side) =>
            [BaseSideEdge(2, side), BaseSideEdge(1 - side, side), BaseSideEdge(3, side), BaseSideEdge(side, side)];

        private int[] BaseRoofOutline() =>
            [BaseSideEdge(3, 0), _baseEdges.Get((2, 1, 3, -1)), BaseSideEdge(3, 1), _baseEdges.Get((2, 3, 0, -1))];

        private int BaseSideEdge(int end, int side) => _baseEdges.Get((1, end, side, -1));

        private int SideEdge(int side, int face) => _openingEdges.Get((17, side, face, 0));
        private static int Tag(GeometryArchive.Node node) => node.Integer("GInfo.m_tag") is > 0 and < int.MaxValue and var tag
            ? tag : throw Layout();

        private static void SetEnvelope(GeometryArchive.Node node, List<Uv> points)
        {
            node.SetReal("Envelope.m_corners@0/0", points.Min(p => p.U));
            node.SetReal("Envelope.m_corners@0/1", points.Min(p => p.V));
            node.SetReal("Envelope.m_corners@1/0", points.Max(p => p.U));
            node.SetReal("Envelope.m_corners@1/1", points.Max(p => p.V));
        }

        private sealed record Face(GeometryArchive.Node Node, GeometryArchive.Node Plane, Frame Frame)
        {
            internal List<GeometryArchive.Node> Loops { get; init; } = [];
            internal List<int[]> Contours { get; } = [];
        }

        private sealed record Edge(GeometryArchive.Node Node, int A, int B, Segment Segment)
        {
            internal int Incident(int side) => side == 0 ? A : B;
            internal int Side(int face) => A == face ? 0 : B == face ? 1 : throw Layout();
        }

        private readonly record struct Uv(double U, double V);

        private readonly record struct Point(double X, double Y, double Z)
        {
            internal double Dot(Point p) => X * p.X + Y * p.Y + Z * p.Z;
            internal Point Plus(Point p) => new(X + p.X, Y + p.Y, Z + p.Z);
            internal Point Minus(Point p) => new(X - p.X, Y - p.Y, Z - p.Z);
            internal Point Scale(double s) => new(X * s, Y * s, Z * s);
            internal Point WithZ(double z) => this with { Z = z };
            // The topology is determined by semantic tags. This bound checks the floating-point plane projections.
            internal bool Near(Point p) => Math.Abs(X - p.X) < 1e-9 && Math.Abs(Y - p.Y) < 1e-9 && Math.Abs(Z - p.Z) < 1e-9;
        }

        private readonly record struct Segment(Point First, Point Last)
        {
            internal bool Matches(Segment other) => First.Near(other.First) && Last.Near(other.Last);
            internal Segment MoveZ(double from, double to) => new(SameCoordinate(First.Z, from) ? First.WithZ(to) : First,
                SameCoordinate(Last.Z, from) ? Last.WithZ(to) : Last);
            internal bool Connects(Segment previous, Segment next) =>
                previous.Contains(First) && next.Contains(Last) && !previous.Contains(Last) && !next.Contains(First)
                || previous.Contains(Last) && next.Contains(First) && !previous.Contains(First) && !next.Contains(Last);
            private bool Contains(Point p) => First.Near(p) || Last.Near(p);
        }

        private readonly record struct Frame(Point Origin, Point X, Point Y)
        {
            internal static Frame Read(GeometryArchive.Node node)
            {
                Point ReadPoint(string field) => new(node.Real(field + "@0"), node.Real(field + "@1"), node.Real(field + "@2"));
                return new(ReadPoint("Plane.m_origin"), ReadPoint("Plane.m_xVec"), ReadPoint("Plane.m_yVec"));
            }

            internal Point World(Uv uv) => new(Origin.X + X.X * uv.U + Y.X * uv.V,
                Origin.Y + X.Y * uv.U + Y.Y * uv.V, Origin.Z + X.Z * uv.U + Y.Z * uv.V);

            internal Segment World(GeometryArchive.Node edge, int side)
            {
                var u = edge.Pair($"EdgePnt.uv@{side}/0");
                var v = edge.Pair($"EdgePnt.uv@{side}/1");
                return new(World(new Uv(u.First, v.First)), World(new Uv(u.Last, v.Last)));
            }

            internal Uv Project(Point point)
            {
                var relative = point.Minus(Origin);
                return new(relative.Dot(X), relative.Dot(Y));
            }
        }
    }
}
