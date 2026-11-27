namespace RvtIO.Writing;

internal static partial class WallHeightWriter
{
    private sealed partial class OpeningTransition
    {
        private Dictionary<int, Edge> ActivateCuts(Dictionary<int, Face> faces, Dictionary<int, Edge> edges,
            IReadOnlyList<(OpeningInterval Opening, Archive Geometry)> cutters, HeightInterval height, int idWidth, double baseOffset,
            bool replaysFloor)
        {
            var contours = faces.Where(pair => pair.Value.Contours.Count != 0)
                .ToDictionary(pair => pair.Key, pair => pair.Value.Contours);
            var generators = _generators.ToList();
            var topCut = new BoundaryCutEdges();
            var axisFace = _wall.Target(-1, "VWall.m_pRefFaces@0", "Face");
            var axisPlane = _wall.Target(axisFace, "Face.m_pSurf", "Plane");
            var wallAxis = Vector("Plane.m_xVec");
            var axisOrigin = Vector("Plane.m_origin");
            var fillOrigin = axisOrigin.WithZ(axisOrigin.Z - baseOffset);
            var fillTemplate = Find(faces, _sideA).Node.Target("GFace.m_pGFilling");
            int style = Find(faces, _sideA).Node.Integer("GFace.m_renderStyleId");
            if (style <= 0 || faces.Values.Any(face => face.Node.Integer("GFace.m_renderStyleId") != style)
                || fillTemplate.Integer("GFilling.m_data") != 0 || !double.IsFinite(fillOrigin.Z))
                throw Layout();

            foreach (var (opening, cutter) in cutters)
            {
                int generator = _wall.Int32(opening.Step, "GeomGenerator.m_id");
                if (generator <= 0 || generator == _baseGenerator)
                    throw Layout();

                var faceHistory = new History(_wall, opening.Step, "Face", 3);
                var edgeHistory = new History(_wall, opening.Step, "Edge", 4);
                if (!faceHistory.Tags.Concat(edgeHistory.Tags).All(tag => Generator(tag) == generator))
                    throw Layout();
                int priorCount = generators.Count;
                bool first = !faceHistory.Tags.Any() && !edgeHistory.Tags.Any();
                bool throughTop = opening.Destination == OpeningCut.ThroughTop;
                bool throughBottom = opening.Destination == OpeningCut.ThroughBottom;
                bool atHead = opening.Destination == OpeningCut.AtHead;
                bool atSill = opening.Destination == OpeningCut.AtSill;
                bool atBaseSill = opening.Destination == OpeningCut.AtBaseSill;
                bool atBaseHead = opening.Destination == OpeningCut.AtBaseHead;
                bool dormantContact = atSill || atBaseHead;
                bool missingHead = !faceHistory.Contains((5, 0, -1, 0));
                if (_throughBottom && (first || missingHead || !faceHistory.Contains((5, 1, -1, 0))))
                    throw Layout();
                bool virtualHead = (throughTop || atSill) && missingHead;
                var archive = new GeometryArchive(cutter);
                if (archive.Root.OwnedList("GGroup.m_subNodes") is not [var solid]
                    || solid.Integer("Geometry.m_sharedSurfInfo#") != 0)
                    throw Layout();
                var cutterFaces = solid.OwnedList("GBRep.m_pFaces");
                var cutterEdges = solid.OwnedList("Geometry.m_pEdges");
                // The cutter's head, sill, two guide planes, and jambs have fixed local tags.
                // On first activation each face receives its tag before the edges first encountered on that face.
                int[] localFaces = [0, 1, 2, 5, 9, 13];
                int[][] edgeOrder = virtualHead ? [[], [15, 11, 7, 4], [3, 17, 8], [6, 12], [10, 16], [14]]
                    : [[3, 6, 10, 14], [15, 11, 7, 4], [17, 8], [12], [16], []];
                if (!cutterFaces.Select(CutterTag).Order().SequenceEqual(localFaces)
                    || !cutterEdges.Select(CutterTag).Order().SequenceEqual(edgeOrder.SelectMany(row => row).Order())
                    || generators.Count > int.MaxValue - 64)
                    throw Layout();
                var cutterFrames = cutterFaces.ToDictionary(CutterTag, node => Frame.Read(node.Target("Face.m_pSurf")));

                var faceIds = new Dictionary<int, int>();
                var edgeIds = new Dictionary<int, int>();
                // A cut through the top uses the head to position its edges, then drops it from the solid.
                const int temporaryHead = int.MaxValue - 3;
                for (int i = 0; i < localFaces.Length; i++)
                {
                    int local = localFaces[i];
                    faceIds.Add(local, local == 0 && virtualHead ? temporaryHead
                        : Assign(faceHistory, (5, local, -1, 0), local == 0 && missingHead));
                    foreach (int edge in edgeOrder[i])
                        edgeIds.Add(edge, Assign(edgeHistory, (3, edge, -1, -1)));
                }
                int splitStart = generators.Count;
                int nextSplit = splitStart;
                bool missingSides = !edgeHistory.Contains((17, _sideA, faceIds[1], 0));
                if (_throughBottom && missingSides)
                    throw Layout();
                // An existing notch can allocate new roof splits before it gains permanent head edges.
                bool deferHeadSides = atHead && missingHead && !missingSides;
                int splitCount = missingSides && throughTop ? 16 : missingSides && atHead ? 8 : 0;
                generators.AddRange(Enumerable.Repeat(generator, splitCount));
                var boundaryIds = new Dictionary<(int Side, int Reveal), int>();
                int[] reveals = missingSides && !first ? [1, 5, 13, 0] : [0, 1, 5, 13];
                if (!dormantContact)
                    foreach (int side in new[] { _sideA, _sideB })
                        foreach (int reveal in reveals)
                            boundaryIds.Add((side, reveal), reveal == 0 && (virtualHead || deferHeadSides) ? temporaryHead + (side == _sideA ? 1 : 2)
                                : Assign(edgeHistory, (17, side, faceIds[reveal], 0), missingSides || reveal == 0 && missingHead));

                foreach (var node in cutterFaces)
                {
                    int local = CutterTag(node);
                    bool guide = dormantContact || atHead && local == 0 || atBaseSill && local == 1 || local is 2 or 9;
                    var plane = node.Target("Face.m_pSurf");
                    var frame = cutterFrames[local];
                    var loop = node.Target("GFace.m_pFirstLoop");
                    if (loop.Ref("GEdgeLoop.m_nextLoop") is not null)
                        throw Layout();
                    if (atSill && local == 0 || atBaseHead && local == 1)
                        continue;
                    int tag = faceIds[local];
                    node.SetTag(tag);
                    node.SetIntersectionFace(style, idWidth, guide);
                    if (guide)
                    {
                        node.SetOwned("GFace.m_pFirstLoop", null);
                    }
                    else
                    {
                        var fill = fillTemplate.Clone();
                        var origin = frame.Project(fillOrigin);
                        fill.SetReal("FillPatternPlacer.m_origin@0", origin.U);
                        fill.SetReal("FillPatternPlacer.m_origin@1", origin.V);
                        fill.SetReal("FillPatternPlacer.m_dir@0", local is 0 or 1 ? frame.X.Dot(wallAxis) : 0);
                        fill.SetReal("FillPatternPlacer.m_dir@1", local is 0 or 1 ? frame.Y.Dot(wallAxis) : -1);
                        fill.SetMirroredCutFill();
                        fill.SetWeak("GFilling.m_pGFace", node);
                        node.SetOwned("GFace.m_pGFilling", fill);
                    }
                    if (!faces.TryAdd(tag, new(node, plane, frame) { Loops = guide ? [] : [loop] }))
                        throw Layout();
                }

                var boundary = new Dictionary<(int Side, int Reveal), Segment>();
                foreach (var node in cutterEdges)
                {
                    int local = CutterTag(node);
                    int a = CutterTag(node.Target("GEdge.m_pFace@0"));
                    int b = CutterTag(node.Target("GEdge.m_pFace@1"));
                    if (a == b || !faceIds.TryGetValue(a, out int faceA) || !faceIds.TryGetValue(b, out int faceB)
                        || node.Integer("GEdge.m_interiorEdgePnts#") != 0)
                        throw Layout();
                    var segment = cutterFrames[a].World(node, 0);
                    if (!segment.Matches(cutterFrames[b].World(node, 1))
                        || !Corner(segment.First) || !Corner(segment.Last))
                        throw Layout();
                    if (dormantContact)
                        continue;
                    int tag = edgeIds[local];
                    node.SetTag(tag);
                    node.SetEdgeFlags(local is 3 or 4 or 10 or 11 or 17 || atHead && local is 6 or 14
                        || atBaseSill && local is 7 or 15 ? (byte)7 : (byte)6);
                    if (!atBaseSill && local is 7 or 15 || !atHead && local is 6 or 14)
                        segment = new(segment.Last, segment.First);
                    if (!edges.TryAdd(tag, new(node, faceA, faceB, segment)))
                        throw Layout();
                    if (a is 2 or 9 || b is 2 or 9)
                    {
                        int guide = a is 2 or 9 ? a : b;
                        int reveal = guide == a ? b : a;
                        int side = guide == 9 ? _sideA : _sideB;
                        if (!boundary.TryAdd((side, reveal), segment))
                            throw Layout();
                    }
                }
                if (!dormantContact)
                {
                    if (boundary.Count != 8)
                        throw Layout();
                    AddHole(_sideA, [0, 13, 1, 5]);
                    AddHole(_sideB, [0, 5, 1, 13]);
                    if (!atHead)
                        contours.Add(faceIds[0], [[edgeIds[6], boundaryIds[(_sideB, 0)], edgeIds[14], boundaryIds[(_sideA, 0)]]]);
                    if (!atBaseSill)
                        contours.Add(faceIds[1], [[edgeIds[15], boundaryIds[(_sideB, 1)], edgeIds[7], boundaryIds[(_sideA, 1)]]]);
                    contours.Add(faceIds[5], [[edgeIds[6], boundaryIds[(_sideA, 5)], edgeIds[7], boundaryIds[(_sideB, 5)]]]);
                    contours.Add(faceIds[13], [[edgeIds[14], boundaryIds[(_sideB, 13)], edgeIds[15], boundaryIds[(_sideA, 13)]]]);
                }

                if (throughTop || atHead || throughBottom || atBaseSill)
                {
                    int crossedFace = throughBottom || atBaseSill ? 1 : 0;
                    var notch = new Notch(opening, edgeHistory, faceIds[crossedFace],
                        boundaryIds[(_sideA, crossedFace)], boundaryIds[(_sideB, crossedFace)],
                        faceIds[1 - crossedFace], faceIds[5], faceIds[13], faceIds[2], faceIds[9]);
                    int added = 0, addedRoofSplits = 0, addedRevealSplits = 0;
                    edges = OpenCut(faces, edges, contours, topCut, height, notch, AllocateNotchEdge);
                    int full = atHead || atBaseSill ? 12 : 20;
                    bool partial = added == addedRoofSplits + addedRevealSplits
                        && addedRoofSplits is 0 or 8 && addedRevealSplits is 0 or 8;
                    if (added != 0 && added != full && !partial || splitCount != 0 && (nextSplit != splitStart + splitCount || added != full))
                        throw Layout();
                    if (deferHeadSides)
                    {
                        if (added != addedRoofSplits)
                            throw Layout();
                        var headEdges = new Dictionary<int, int>();
                        foreach (int side in new[] { _sideA, _sideB })
                        {
                            int temporary = boundaryIds[(side, 0)];
                            int tag = Assign(edgeHistory, (17, side, faceIds[0], 0), true);
                            if (edges.ContainsKey(tag))
                                throw Layout();
                            Find(edges, temporary).Node.SetTag(tag);
                            headEdges.Add(temporary, tag);
                        }
                        edges = edges.ToDictionary(pair => headEdges.GetValueOrDefault(pair.Key, pair.Key), pair => pair.Value);
                    }

                    int AllocateNotchEdge((int Kind, int, int, int) key, bool roofSplit)
                    {
                        if (edgeHistory.Contains(key))
                            return edgeHistory.Get(key);
                        added++;
                        if (roofSplit)
                            addedRoofSplits++;
                        else if (key.Kind == 4)
                            addedRevealSplits++;
                        if (splitCount == 0 || key.Kind != 4)
                            return Assign(edgeHistory, key, true);
                        if (nextSplit >= splitStart + splitCount)
                            throw Layout();
                        int tag = nextSplit++;
                        edgeHistory.Add(key, tag);
                        return tag;
                    }
                }

                var flag = _wall.Field(opening.Step, "WallCutoutGStepBase.m_bCutSomething");
                byte cut = CutsHost(opening.Cut) ? (byte)1 : (byte)0;
                byte destination = CutsHost(opening.Destination) ? (byte)1 : (byte)0;
                if (!_wall.Matches(flag.ValueOffset, flag.ValueOffset + 1, [cut]))
                    throw Layout();
                if (cut != destination)
                    _edits.Add((flag.ValueOffset, flag.ValueOffset + 1, [destination]));
                int[] openingSlots = dormantContact ? [-1, -1, -1, -1]
                    : [throughBottom || atBaseSill ? -1 : faceIds[1], throughTop || atHead ? -1 : faceIds[0], faceIds[5], faceIds[13]];
                for (int i = 0; i < openingSlots.Length; i++)
                {
                    var slot = _wall.Field(opening.Step, $"WallRectOpeningGStep.m_OpeningFacesIds@{i}");
                    _edits.Add((slot.ValueOffset, slot.ValueOffset + sizeof(int), IntBytes(openingSlots[i])));
                }

                if (generators.Count != priorCount)
                {
                    _edits.Add(faceHistory.Replacement());
                    _edits.Add(edgeHistory.Replacement());
                }

                int Assign(History history, (int, int, int, int) key, bool allocateHead = false)
                {
                    if (!first && !allocateHead)
                        return history.Get(key);
                    int tag = generators.Count;
                    history.Add(key, tag);
                    generators.Add(generator);
                    return tag;
                }

                bool Corner(Point point)
                {
                    double station = point.Minus(axisOrigin).Dot(wallAxis);
                    return (Math.Abs(station - opening.Left) < 1e-9 || Math.Abs(station - opening.Right) < 1e-9)
                        && (SameCoordinate(point.Z, opening.Bottom) || SameCoordinate(point.Z, opening.Top));
                }

                void AddHole(int side, int[] order)
                {
                    var rim = order.Select(reveal => boundary.TryGetValue((side, reveal), out var segment)
                        ? segment : throw Layout()).ToArray();
                    var loop = new int[order.Length];
                    for (int i = 0; i < order.Length; i++)
                    {
                        var segment = rim[i];
                        var next = rim[(i + 1) % order.Length];
                        if (!next.First.Near(segment.Last) && !next.Last.Near(segment.Last))
                            segment = new(segment.Last, segment.First);
                        if (!next.First.Near(segment.Last) && !next.Last.Near(segment.Last))
                            throw Layout();
                        var edge = Find(edges, BaseSideEdge(2, side == _sideA ? 0 : 1)).Node.Clone();
                        int tag = boundaryIds[(side, order[i])];
                        edge.SetTag(tag);
                        if (!edges.TryAdd(tag, new(edge, side, faceIds[order[i]], segment)))
                            throw Layout();
                        loop[i] = tag;
                    }

                    var start = contours[side][0];
                    contours[side] = [start, .. contours[side].Skip(1).Append(loop).OrderBy(row => row.Min())];
                }
            }

            if (generators.Count != _generators.Length)
                _edits.Add((_tableStart, _tableEnd, Encode([generators.Count, .. generators])));
            if (replaysFloor)
            {
                var floor = contours[_boundary];
                if (floor.Count == 3)
                {
                    // Revit writes the two end contours before the contour between the openings.
                    int leftEnd = _baseEdges.Get((2, 0, 2, -1));
                    int left = floor.FindIndex(loop => loop.Contains(leftEnd));
                    if (left is not (1 or 2) || !floor[0].Contains(_baseEdges.Get((2, 2, 1, -1))))
                        throw Layout();
                    (floor[1], floor[left]) = (floor[left], floor[1]);
                }
                // The saved contour starts at a vertical end edge when the opening interrupts the base.
                if (floor.Count > 1)
                {
                    RotateSideOutline(_sideA, BaseSideEdge(1, 0));
                    RotateSideOutline(_sideB, BaseSideEdge(1, 1));
                }
                else if (contours[_sideA].Count == 1)
                {
                    RotateSideOutline(_sideB, BaseSideEdge(0, 1));
                }
            }
            else if (contours[_roof].Count == 2)
            {
                // Retain the wall outline's established starting edge for a single cut through the top.
                RotateSideOutline(_sideA, BaseSideEdge(2, 0));
                RotateSideOutline(_sideB, BaseSideEdge(2, 1));
            }
            RebuildSolid(faces, edges, contours, topCut);
            return edges;

            void RotateSideOutline(int side, int firstEdge)
            {
                var loops = contours[side];
                int start = Array.IndexOf(loops[0], firstEdge);
                if (start < 0)
                    throw Layout();
                loops[0] = [.. loops[0].AsSpan(start), .. loops[0].AsSpan(0, start)];
            }

            Point Vector(string field) => new(_wall.Real(axisPlane, field + "@0"), _wall.Real(axisPlane, field + "@1"),
                _wall.Real(axisPlane, field + "@2"));

            static int CutterTag(GeometryArchive.Node node) => node.Integer("GInfo.m_tag") is >= 0 and var tag
                ? tag : throw Layout();
        }
    }
}
