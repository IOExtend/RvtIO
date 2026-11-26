using RvtIO.Parsing;

namespace RvtIO.Writing;

internal static partial class WallHeightWriter
{
    private sealed partial class OpeningTransition
    {
        private readonly Archive _wall;
        private readonly OpeningInterval _opening;
        private readonly int[] _generators;
        private readonly int _baseGenerator, _openingGenerator;
        private readonly int _tableStart, _tableEnd;
        private readonly History _baseFaces, _baseEdges, _openingFaces, _openingEdges;
        private readonly int _sideA, _sideB, _roof, _sill, _left, _right, _head;
        private readonly int _headEdgeA, _headEdgeB;
        private readonly bool _createdHead;
        private int _addedTags;
        private readonly List<(int Start, int End, byte[] Bytes)> _edits = [];

        internal OpeningTransition(Archive wall, OpeningInterval opening, bool removesCuts, bool activatesCuts)
        {
            _wall = wall;
            _opening = opening;
            if (wall.Nodes("GeomTable").ToArray() is not [var table] || wall.Nodes("BaseWallGStep").ToArray() is not [var baseStep]
                || wall.Integer(table, "GeomTable.m_bigTableOwner") != 0
                || wall.Integer(table, "GeomTable.m_materialMarkers#") != 0
                || wall.Integer(table, "GeomTable.m_faceTypeMarkers#") != 0)
                throw Layout();
            _baseGenerator = wall.Int32(baseStep, "GeomGenerator.m_id");
            _openingGenerator = wall.Int32(opening.Step, "GeomGenerator.m_id");
            _generators = wall.Values(table, "GeomTabEntry.m_geomGeneratorId").Select(Integer).ToArray();
            var count = wall.Field(table, "GeomTable.m_table#");
            _tableStart = count.ValueOffset;
            _tableEnd = wall.Field(table, "GeomTable.m_bigTableOwner").ValueOffset;
            if (count.Integer != _generators.Length || _baseGenerator <= 0 || _openingGenerator <= 0 || _baseGenerator == _openingGenerator
                || !wall.Matches(_tableStart, _tableEnd, Encode([_generators.Length, .. _generators])))
                throw Layout();

            _baseFaces = new(wall, baseStep, "Face", 3);
            _baseEdges = new(wall, baseStep, "Edge", 4);
            _openingFaces = new(wall, opening.Step, "Face", 3);
            _openingEdges = new(wall, opening.Step, "Edge", 4);
            _sideA = _baseFaces.Get((2, -1, -1, 0));
            _sideB = _baseFaces.Get((1, -1, -1, 0));
            _roof = _baseFaces.Get((3, 3, -1, 0));
            if (!_baseFaces.Tags.Concat(_baseEdges.Tags).All(t => Generator(t) == _baseGenerator))
                throw Layout();
            if (activatesCuts)
                return;
            _sill = _openingFaces.Get((5, 1, -1, 0));
            _left = _openingFaces.Get((5, 5, -1, 0));
            _right = _openingFaces.Get((5, 13, -1, 0));
            int OpeningFace(int slot) => wall.Int32(opening.Step, $"WallRectOpeningGStep.m_OpeningFacesIds@{slot}");
            if (OpeningFace(0) != _sill || OpeningFace(2) != _left || OpeningFace(3) != _right
                || !_openingFaces.Tags.Concat(_openingEdges.Tags).All(t => Generator(t) == _openingGenerator))
                throw Layout();

            // Removing a cut keeps all assigned tags and never needs to allocate the missing head of a notch.
            if (removesCuts)
                return;

            var headKey = (5, 0, -1, 0);
            if (_openingFaces.Contains(headKey))
            {
                _head = _openingFaces.Get(headKey);
                _headEdgeA = _openingEdges.Get((17, _sideA, _head, 0));
                _headEdgeB = _openingEdges.Get((17, _sideB, _head, 0));
            }
            else
            {
                if (opening.Cut != OpeningCut.ThroughTop)
                    throw Layout();
                _head = _generators.Length;
                _headEdgeA = _head + 1;
                _headEdgeB = _head + 2;
                _openingFaces.Add(headKey, _head);
                _openingEdges.Add((17, _sideA, _head, 0), _headEdgeA);
                _openingEdges.Add((17, _sideB, _head, 0), _headEdgeB);
                _addedTags = 3;
                _createdHead = true;
            }

            var headFace = wall.Field(opening.Step, "WallRectOpeningGStep.m_OpeningFacesIds@1");
            bool openingUp = opening.Cut == OpeningCut.Interior;
            if (!wall.Matches(headFace.ValueOffset, headFace.ValueOffset + 4, IntBytes(openingUp ? _head : -1)))
                throw Layout();
            _edits.Add((headFace.ValueOffset, headFace.ValueOffset + 4, IntBytes(openingUp ? -1 : _head)));
        }

        internal void ClearCutSteps(IEnumerable<OpeningInterval> openings)
        {
            foreach (var opening in openings)
            {
                var cut = _wall.Field(opening.Step, "WallCutoutGStepBase.m_bCutSomething");
                if (!_wall.Matches(cut.ValueOffset, cut.ValueOffset + 1, [1]))
                    throw Layout();
                _edits.Add((cut.ValueOffset, cut.ValueOffset + 1, [0]));
                for (int i = 0; i < 4; i++)
                {
                    var face = _wall.Field(opening.Step, $"WallRectOpeningGStep.m_OpeningFacesIds@{i}");
                    _edits.Add((face.ValueOffset, face.ValueOffset + 4, IntBytes(-1)));
                }
            }
        }

        internal byte[] Apply(byte[] topology, IEnumerable<(int Start, int End)> topologyTables)
        {
            if (_addedTags != 0)
            {
                _edits.Add((_tableStart, _tableEnd,
                    Encode([_generators.Length + _addedTags, .. _generators, .. Enumerable.Repeat(_openingGenerator, _addedTags)])));
                if (_createdHead)
                    _edits.Add(_openingFaces.Replacement());
                _edits.Add(_openingEdges.Replacement());
            }
            byte[] body = _wall.Body;
            int end = body.Length;
            foreach (var edit in _edits.Concat(topologyTables.Select(t => (t.Start, t.End, Bytes: topology))).OrderByDescending(e => e.Start))
            {
                if (edit.Start < sizeof(ushort) || edit.Start >= edit.End || edit.End > end)
                    throw Layout();
                body = ParameterWriter.ReplaceValue(body, edit.Start, edit.End - edit.Start, edit.Bytes);
                end = edit.Start;
            }
            return body;
        }

        private int Generator(int tag) => (uint)tag < _generators.Length ? _generators[tag] : throw Layout();

        private int AllocateEdge((int A, int B, int C, int D) key)
        {
            if (_openingEdges.Contains(key))
                return _openingEdges.Get(key);
            int tag = _generators.Length + _addedTags++;
            _openingEdges.Add(key, tag);
            return tag;
        }

        private static int Integer(WideBlockWalker.Capture capture) =>
            capture is { Kind: WideBlockWalker.CaptureKind.Integer, Integer: >= int.MinValue and <= int.MaxValue and var value }
                ? (int)value : throw Layout();

        private static byte[] Encode(IEnumerable<int> values)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            foreach (int value in values)
                writer.Write(value);
            return stream.ToArray();
        }

        private sealed class History
        {
            private readonly SortedDictionary<(int A, int B, int C, int D), int> _rows = [];
            private readonly int _start, _end, _width;

            internal History(Archive archive, int node, string kind, int width)
            {
                _width = width;
                var count = archive.Field(node, $"GeomGenerator.m_{kind.ToLowerInvariant()}HistTable#");
                var ids = archive.Values(node, $"{kind}HistEntry.m_id").Select(Integer).ToArray();
                int[][] keys = Enumerable.Range(0, width).Select(i => archive.Values(node, $"{kind}Hist.m_keys@{i}").Select(Integer).ToArray()).ToArray();
                if (ids.Length != count.Integer || keys.Any(k => k.Length != ids.Length) || ids.Distinct().Count() != ids.Length)
                    throw Layout();
                for (int i = 0; i < ids.Length; i++)
                    Add((keys[0][i], keys[1][i], keys[2][i], width == 4 ? keys[3][i] : 0), ids[i]);
                _start = count.ValueOffset;
                byte[] data = Bytes();
                _end = _start + data.Length;
                if (!archive.Matches(_start, _end, data))
                    throw Layout();
            }

            internal IEnumerable<int> Tags => _rows.Values;
            internal IEnumerable<int> SelectTags(Func<(int A, int B, int C, int D), bool> match) =>
                _rows.Where(entry => match(entry.Key)).Select(entry => entry.Value);
            internal bool Contains((int, int, int, int) key) => _rows.ContainsKey(key);
            internal int Get((int, int, int, int) key) => _rows.TryGetValue(key, out int tag) ? tag : throw Layout();
            internal void Add((int, int, int, int) key, int tag)
            {
                if (tag <= 0 || !_rows.TryAdd(key, tag))
                    throw Layout();
            }
            internal (int Start, int End, byte[] Bytes) Replacement() => (_start, _end, Bytes());

            private byte[] Bytes()
            {
                using var buffer = new MemoryStream();
                using var writer = new BinaryWriter(buffer);
                writer.Write(_rows.Count);
                foreach (var (key, value) in _rows)
                {
                    writer.Write(value);
                    writer.Write(key.A);
                    writer.Write(key.B);
                    writer.Write(key.C);
                    if (_width == 4)
                        writer.Write(key.D);
                }
                return buffer.ToArray();
            }
        }
    }
}
