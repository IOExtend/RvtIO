using RvtIO.Geometry;
using RvtIO.Model;

namespace RvtIO.Ifc;

/// <summary>The extrusion bodies of the current profile walls: the wall frame at the start of the axis, the axis as an
/// IfcTrimmedCurve over an IfcLine, and the body as an IfcExtrudedAreaSolid over a rectangle.</summary>
public partial class IfcExporter
{
    internal sealed class GlobalsCurrent
    {
        private readonly StepWriter _w;
        private int _origin;
        private readonly Dictionary<(int, int, int), int> _directions3D = new();
        private readonly Dictionary<(int, int), int> _directions2D = new();

        public GlobalsCurrent(StepWriter w)
        {
            _w = w;
            for (int i = 0; i < w.EntityCount; i++)
            {
                var (id, type, args) = w.EntityAt(i);
                if (type == "IFCCARTESIANPOINT" && args == "(0.,0.,0.)")
                {
                    if (_origin == 0) _origin = id;
                }
                else if (type == "IFCDIRECTION" && TryAxisKey(args, out var key))
                {
                    if (key.Length == 3) _directions3D.TryAdd((key[0], key[1], key[2]), id);
                    else _directions2D.TryAdd((key[0], key[1]), id);
                }
            }
        }

        /// <summary>The components of a direction written like "(0.,0.,1.)" when it is a unit vector along a global axis.</summary>
        private static bool TryAxisKey(string args, out int[] key)
        {
            key = [];
            if (args.Length < 5 || args[0] != '(' || args[^1] != ')') return false;
            var parts = args[1..^1].Split(',');
            if (parts.Length is not (2 or 3)) return false;
            key = new int[parts.Length];
            int nonZero = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                switch (parts[i])
                {
                    case "0.": break;
                    case "1.": key[i] = 1; nonZero++; break;
                    case "-1.": key[i] = -1; nonZero++; break;
                    default: return false;
                }
            }
            return nonZero == 1;
        }

        /// <summary>A component within 1e-9 of an integer is that integer; a negative zero is a zero.</summary>
        internal static double Snap(double value)
        {
            double rounded = Math.Round(value);
            return (Math.Abs(value - rounded) <= 1e-9 ? rounded : value) + 0.0;
        }

        public int Origin3D() => _origin != 0 ? _origin : _origin = _w.Add("IFCCARTESIANPOINT", "(0.,0.,0.)");

        public int Direction3D(double x, double y, double z)
        {
            (x, y, z) = (Snap(x), Snap(y), Snap(z));
            var key = (x == 0 ? 0 : (int)x, y == 0 ? 0 : (int)y, z == 0 ? 0 : (int)z);
            bool axis = (Math.Abs(x) + Math.Abs(y) + Math.Abs(z)) == 1 && x == key.Item1 && y == key.Item2 && z == key.Item3;
            if (!axis) return _w.Add("IFCDIRECTION", StepWriter.List(StepWriter.Real(x), StepWriter.Real(y), StepWriter.Real(z)));
            if (!_directions3D.TryGetValue(key, out int id))
                _directions3D[key] = id = _w.Add("IFCDIRECTION", StepWriter.List(StepWriter.Real(x), StepWriter.Real(y), StepWriter.Real(z)));
            return id;
        }

        public int Direction2D(double x, double y)
        {
            (x, y) = (Snap(x), Snap(y));
            var key = (x == 0 ? 0 : (int)x, y == 0 ? 0 : (int)y);
            bool axis = (Math.Abs(x) + Math.Abs(y)) == 1 && x == key.Item1 && y == key.Item2;
            if (!axis) return _w.Add("IFCDIRECTION", StepWriter.List(StepWriter.Real(x), StepWriter.Real(y)));
            if (!_directions2D.TryGetValue(key, out int id))
                _directions2D[key] = id = _w.Add("IFCDIRECTION", StepWriter.List(StepWriter.Real(x), StepWriter.Real(y)));
            return id;
        }

        public int Point3D(double x, double y, double z)
        {
            (x, y, z) = (Zero(x), Zero(y), Zero(z));
            return x == 0 && y == 0 && z == 0
                ? Origin3D()
                : _w.Add("IFCCARTESIANPOINT", StepWriter.List(StepWriter.Real(x), StepWriter.Real(y), StepWriter.Real(z)));
        }

        /// <summary>A coordinate that is zero up to the rounding of the arithmetic (1e-12 of the unit) is zero, never a negative zero.</summary>
        internal static double Zero(double value) => Math.Abs(value) < 1e-12 ? 0.0 : value;

        public int Point2D(double x, double y) =>
            _w.Add("IFCCARTESIANPOINT", StepWriter.List(StepWriter.Real(Zero(x)), StepWriter.Real(Zero(y))));

        public int Axis3D(double[] location, double[] zDirection, double[] xDirection)
        {
            int point = Point3D(location[0], location[1], location[2]);
            bool defaults = Snap(zDirection[0]) == 0 && Snap(zDirection[1]) == 0 && Snap(zDirection[2]) == 1
                && Snap(xDirection[0]) == 1 && Snap(xDirection[1]) == 0 && Snap(xDirection[2]) == 0;
            return _w.Add("IFCAXIS2PLACEMENT3D", defaults
                ? $"{StepWriter.Ref(point)},$,$"
                : $"{StepWriter.Ref(point)},{StepWriter.Ref(Direction3D(zDirection[0], zDirection[1], zDirection[2]))},"
                  + StepWriter.Ref(Direction3D(xDirection[0], xDirection[1], xDirection[2])));
        }
    }

    private static bool TryReadPointCurrent(StepWriter w, Dictionary<int, int> indexById, string reference, out double[] point)
    {
        point = [];
        if (!TryEntityId(reference, out int id) || !indexById.TryGetValue(id, out int index)
            || w.EntityAt(index).Type != "IFCCARTESIANPOINT")
            return false;
        string args = w.EntityAt(index).Args;
        if (args.Length < 3 || args[0] != '(' || args[^1] != ')') return false;
        var parts = args[1..^1].Split(',');
        point = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!TryParseStepReal(parts[i], out point[i])) return false;
        return true;
    }

    /// <summary>The entity ids an attribute text refers to.</summary>
    private static IEnumerable<int> ReferencedIdsCurrent(string args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] != '#') continue;
            int j = i + 1, value = 0;
            while (j < args.Length && args[j] is >= '0' and <= '9') value = value * 10 + (args[j++] - '0');
            if (j > i + 1) yield return value;
            i = j - 1;
        }
    }

    /// <summary>Removes the entities that nothing refers to any more, and then those only they referred to.</summary>
    private static void RemoveDetachedCurrent(StepWriter w, IEnumerable<int> candidates)
    {
        int maxId = 0;
        for (int i = 0; i < w.EntityCount; i++) maxId = Math.Max(maxId, w.EntityAt(i).Id);
        var counts = new int[maxId + 1];
        var indexById = new Dictionary<int, int>(w.EntityCount);
        for (int i = 0; i < w.EntityCount; i++)
        {
            var (id, _, args) = w.EntityAt(i);
            indexById[id] = i;
            foreach (int reference in ReferencedIdsCurrent(args))
                if (reference <= maxId) counts[reference]++;
        }
        var remove = new HashSet<int>();
        var queue = new Queue<int>(candidates);
        while (queue.Count > 0)
        {
            int id = queue.Dequeue();
            if (id > maxId || counts[id] != 0 || !indexById.TryGetValue(id, out int index) || !remove.Add(id)) continue;
            foreach (int child in ReferencedIdsCurrent(w.EntityAt(index).Args))
                if (child <= maxId && --counts[child] == 0) queue.Enqueue(child);
        }
        if (remove.Count > 0) w.RemoveEntities(remove);
    }

    /// <summary>The rectangle (min/max along and across the frame, metres) of a wall profile the exporter wrote: an
    /// IfcRectangleProfileDef or a closed polyline of four axis-parallel corners, with the points of the polyline.</summary>
    private static bool TryReadWallRectangleCurrent(StepWriter w, Dictionary<int, int> indexById, string profileReference,
        out double[] extent)
    {
        extent = [];
        if (!TryEntityId(profileReference, out int profileId) || !indexById.TryGetValue(profileId, out int profileIndex)) return false;
        var (_, type, args) = w.EntityAt(profileIndex);
        var attributes = SplitStepArguments(args);
        if (type == "IFCRECTANGLEPROFILEDEF" && attributes.Length == 5)
        {
            if (!TryEntityId(attributes[2], out int placementId) || !indexById.TryGetValue(placementId, out int placementIndex)
                || w.EntityAt(placementIndex).Type != "IFCAXIS2PLACEMENT2D")
                return false;
            var placement = SplitStepArguments(w.EntityAt(placementIndex).Args);
            if (placement.Length != 2 || placement[1] != "$" || !TryReadPointCurrent(w, indexById, placement[0], out var centre)
                || centre.Length != 2 || !TryParseStepReal(attributes[3], out double xDim) || !TryParseStepReal(attributes[4], out double yDim))
                return false;
            extent = [centre[0] - xDim / 2, centre[0] + xDim / 2, centre[1] - yDim / 2, centre[1] + yDim / 2];
            return xDim > 0 && yDim > 0;
        }
        if (type != "IFCARBITRARYCLOSEDPROFILEDEF" || attributes.Length != 3
            || !TryEntityId(attributes[2], out int curveId) || !indexById.TryGetValue(curveId, out int curveIndex)
            || w.EntityAt(curveIndex).Type != "IFCPOLYLINE")
            return false;
        var curve = SplitStepArguments(w.EntityAt(curveIndex).Args);
        if (curve.Length != 1 || curve[0].Length < 3) return false;
        var tokens = SplitStepArguments(curve[0][1..^1]);
        if (tokens.Length != 5 || tokens[0] != tokens[4]) return false;
        var xs = new List<double>();
        var ys = new List<double>();
        for (int i = 0; i < 4; i++)
        {
            if (!TryReadPointCurrent(w, indexById, tokens[i], out var corner) || corner.Length != 2) return false;
            xs.Add(corner[0]); ys.Add(corner[1]);
        }
        double x0 = xs.Min(), x1 = xs.Max(), y0 = ys.Min(), y1 = ys.Max();
        const double tolerance = 1e-9;
        for (int i = 0; i < 4; i++)
            if (Math.Min(Math.Abs(xs[i] - x0), Math.Abs(xs[i] - x1)) > tolerance
                || Math.Min(Math.Abs(ys[i] - y0), Math.Abs(ys[i] - y1)) > tolerance)
                return false;
        extent = [x0, x1, y0, y1];
        return x1 - x0 > tolerance && y1 - y0 > tolerance;
    }

    internal int ApplyCurrentWallExtrusions(StepWriter w, IReadOnlyDictionary<int, Element>? elementsById = null,
        IReadOnlyDictionary<int, int>? productElementIdByEntity = null, int axisSubContextId = 0)
    {
        int count = w.EntityCount;
        var indexById = new Dictionary<int, int>(count);
        for (int i = 0; i < count; i++) indexById[w.EntityAt(i).Id] = i;
        var globals = new GlobalsCurrent(w);
        var detached = new List<int>();
        int[]? referenceCounts = null;
        int converted = 0;

        for (int index = 0; index < count; index++)
        {
            var (id, type, args) = w.EntityAt(index);
            if (type == "IFCWALLSTANDARDCASE")
            {
                Element? wallElement = productElementIdByEntity != null && elementsById != null
                    && productElementIdByEntity.TryGetValue(id, out int wallElementId) && elementsById.TryGetValue(wallElementId, out var found)
                    ? found : null;
                if (TryConvertStandardCaseWallCurrent(w, indexById, globals, args, detached, wallElement)) converted++;
            }
            else if (type == "IFCWALL" && axisSubContextId > 0 && elementsById != null && productElementIdByEntity != null
                && productElementIdByEntity.TryGetValue(id, out int elementId) && elementsById.TryGetValue(elementId, out var element)
                && element.Location is { CurveType: "Line", ArcRadius: null })
            {
                referenceCounts ??= CountReferencesCurrent(w);
                if (TryConvertBrepWallCurrent(w, indexById, referenceCounts, globals, args, element.Location, axisSubContextId, detached, element))
                    converted++;
            }
        }
        if (converted > 0) RemoveDetachedCurrent(w, detached);
        return converted;
    }

    private static List<GeoFace> FirstSolidFacesCurrent(Element element)
    {
        if (element.Geometry?.Faces is not { Count: > 0 } faces) return [];
        var solids = element.Geometry.BrepSolids;
        return solids is { Count: > 0 }
            ? solids[0].FaceIndices.Where(index => (uint)index < (uint)faces.Count).Select(index => faces[index]).ToList()
            : faces;
    }

    /// <summary>How many times each entity id is referred to by the attribute texts of the writer.</summary>
    private static int[] CountReferencesCurrent(StepWriter w)
    {
        int maxId = 0;
        for (int i = 0; i < w.EntityCount; i++) maxId = Math.Max(maxId, w.EntityAt(i).Id);
        var counts = new int[maxId + 1];
        for (int i = 0; i < w.EntityCount; i++)
            foreach (int reference in ReferencedIdsCurrent(w.EntityAt(i).Args))
                if (reference <= maxId) counts[reference]++;
        return counts;
    }

    private static double[] CleanAxisDirectionCurrent(double x, double y)
    {
        double length = Math.Sqrt(x * x + y * y);
        (x, y) = (x / length, y / length);
        return [Math.Abs(x) < 1e-8 ? 0 : x, Math.Abs(y) < 1e-8 ? 0 : y, 0];
    }

    /// <summary>
    /// The points of a faceted Brep as the writer made it: IfcFacetedBrep, IfcClosedShell, IfcFace with outer bounds only,
    /// IfcPolyLoop.
    /// </summary>
    private static bool TryReadBrepRingsCurrent(StepWriter w, Dictionary<int, int> indexById, int brepId,
        List<int[]> rings, Dictionary<int, int> uses)
    {
        if (!indexById.TryGetValue(brepId, out int brepIndex) || w.EntityAt(brepIndex).Type != "IFCFACETEDBREP"
            || !TryEntityId(w.EntityAt(brepIndex).Args, out int shellId) || !indexById.TryGetValue(shellId, out int shellIndex)
            || w.EntityAt(shellIndex).Type != "IFCCLOSEDSHELL")
            return false;
        var shell = SplitStepArguments(w.EntityAt(shellIndex).Args);
        if (shell.Length != 1 || shell[0].Length < 3) return false;
        foreach (string faceToken in SplitStepArguments(shell[0][1..^1]))
        {
            if (!TryEntityId(faceToken, out int faceId) || !indexById.TryGetValue(faceId, out int faceIndex)
                || w.EntityAt(faceIndex).Type != "IFCFACE")
                return false;
            var face = SplitStepArguments(w.EntityAt(faceIndex).Args);
            if (face.Length != 1 || face[0].Length < 3) return false;
            foreach (string boundToken in SplitStepArguments(face[0][1..^1]))
            {
                if (!TryEntityId(boundToken, out int boundId) || !indexById.TryGetValue(boundId, out int boundIndex)
                    || w.EntityAt(boundIndex).Type is not ("IFCFACEOUTERBOUND" or "IFCFACEBOUND")
                    || SplitStepArguments(w.EntityAt(boundIndex).Args) is not { Length: 2 } bound
                    || !TryEntityId(bound[0], out int loopId) || !indexById.TryGetValue(loopId, out int loopIndex)
                    || w.EntityAt(loopIndex).Type != "IFCPOLYLOOP")
                    return false;
                var loop = SplitStepArguments(w.EntityAt(loopIndex).Args);
                if (loop.Length != 1 || loop[0].Length < 3) return false;
                var ring = new List<int>();
                foreach (string pointToken in SplitStepArguments(loop[0][1..^1]))
                {
                    if (!TryEntityId(pointToken, out int pointId)) return false;
                    ring.Add(pointId);
                    uses[pointId] = uses.GetValueOrDefault(pointId) + 1;
                }
                rings.Add(ring.ToArray());
            }
        }
        return rings.Count > 0;
    }

    private static bool TryConvertBrepWallCurrent(StepWriter w, Dictionary<int, int> indexById, int[] referenceCounts, GlobalsCurrent globals,
        string wallArgs, LocationCurve location, int axisSubContextId, List<int> detached, Element? element = null)
    {
        var wall = SplitStepArguments(wallArgs);
        if (wall.Length < 8 || !TryEntityId(wall[5], out int placementId) || !indexById.TryGetValue(placementId, out int placementIndex)
            || !TryEntityId(wall[6], out int shapeId) || !indexById.TryGetValue(shapeId, out int shapeIndex))
            return false;
        var placement = SplitStepArguments(w.EntityAt(placementIndex).Args);
        if (w.EntityAt(placementIndex).Type != "IFCLOCALPLACEMENT" || placement.Length != 2
            || !TryEntityId(placement[1], out int frameId) || !indexById.TryGetValue(frameId, out int frameIndex)
            || w.EntityAt(frameIndex).Type != "IFCAXIS2PLACEMENT3D"
            || SplitStepArguments(w.EntityAt(frameIndex).Args) is not { Length: 3 } frame || frame[1] != "$" || frame[2] != "$"
            || !TryReadPointCurrent(w, indexById, frame[0], out var frameOrigin) || frameOrigin.Any(v => Math.Abs(v) > 1e-12))
            return false;

        var shape = SplitStepArguments(w.EntityAt(shapeIndex).Args);
        if (w.EntityAt(shapeIndex).Type != "IFCPRODUCTDEFINITIONSHAPE" || shape.Length != 3 || shape[2].Length < 3
            || SplitStepArguments(shape[2][1..^1]) is not { Length: 1 } representations
            || !TryEntityId(representations[0], out int bodyId) || !indexById.TryGetValue(bodyId, out int bodyIndex)
            || w.EntityAt(bodyIndex).Type != "IFCSHAPEREPRESENTATION"
            || SplitStepArguments(w.EntityAt(bodyIndex).Args) is not { Length: 4 } body
            || body[1] != "'Body'" || body[2] != "'Brep'" || body[3].Length < 3)
            return false;
        var rings = new List<int[]>();
        var uses = new Dictionary<int, int>();
        foreach (string itemToken in SplitStepArguments(body[3][1..^1]))
            if (!TryEntityId(itemToken, out int brepId) || !TryReadBrepRingsCurrent(w, indexById, brepId, rings, uses))
                return false;

        var points = new Dictionary<int, double[]>();
        foreach (var (pointId, count) in uses)
        {
            if (pointId >= referenceCounts.Length || referenceCounts[pointId] != count
                || !TryReadPointCurrent(w, indexById, StepWriter.Ref(pointId), out var point) || point.Length != 3)
                return false;
            points[pointId] = point;
        }

        double dx = location.EndPoint[0] - location.StartPoint[0], dy = location.EndPoint[1] - location.StartPoint[1];
        if (!(Math.Sqrt(dx * dx + dy * dy) > 1e-9)) return false;
        var u = CleanAxisDirectionCurrent(dx, dy);
        double startX = location.StartPoint[0] * FeetToMetres, startY = location.StartPoint[1] * FeetToMetres;
        double low = double.PositiveInfinity, high = double.NegativeInfinity;
        const double onLine = 1e-9;
        foreach (var ring in rings)
            for (int i = 0; i < ring.Length; i++)
            {
                var a = points[ring[i]];
                var b = points[ring[(i + 1) % ring.Length]];
                double da = -(a[0] - startX) * u[1] + (a[1] - startY) * u[0], db = -(b[0] - startX) * u[1] + (b[1] - startY) * u[0];
                double ta = (a[0] - startX) * u[0] + (a[1] - startY) * u[1], tb = (b[0] - startX) * u[0] + (b[1] - startY) * u[1];
                if (Math.Abs(da) <= onLine) { low = Math.Min(low, ta); high = Math.Max(high, ta); }
                if (Math.Abs(db) <= onLine) { low = Math.Min(low, tb); high = Math.Max(high, tb); }
                if (da * db < 0)
                {
                    double t = ta + (tb - ta) * (da / (da - db));
                    low = Math.Min(low, t); high = Math.Max(high, t);
                }
            }
        if (element?.OwnWallTrimmedAxis is { Length: 2 } trimmedAxis
            && !HasSeparatedFullHeightWallPieces(element))
        {
            double[] along = trimmedAxis.Select(point => (point[0] * FeetToMetres - startX) * u[0]
                + (point[1] * FeetToMetres - startY) * u[1]).ToArray();
            double trimmedLow = Math.Min(along[0], along[1]), trimmedHigh = Math.Max(along[0], along[1]);
            if (trimmedHigh - trimmedLow > 1e-9 && (Math.Abs(trimmedLow - low) > 1e-5 || Math.Abs(trimmedHigh - high) > 1e-5))
            {
                low = trimmedLow;
                high = trimmedHigh;
            }
        }
        if (!(high - low > 1e-9)) return false;
        double minZ = points.Values.Min(p => p[2]);
        double originX = startX + low * u[0], originY = startY + low * u[1];

        var local = new Dictionary<int, double[]>(points.Count);
        foreach (var (pointId, p) in points)
            local[pointId] = [
                (p[0] - originX) * u[0] + (p[1] - originY) * u[1],
                -(p[0] - originX) * u[1] + (p[1] - originY) * u[0],
                p[2] - minZ];
        foreach (var (pointId, p) in local)
            w.ReplaceArgs(indexById[pointId], StepWriter.List(p.Select(v => StepWriter.Real(Math.Abs(v) < 1e-11 ? 0.0 : v))));

        int newFrame = globals.Axis3D([originX, originY, minZ], [0, 0, 1], u);
        w.ReplaceArgs(placementIndex, $"{placement[0]},{StepWriter.Ref(newFrame)}");

        int vector = w.Add("IFCVECTOR", $"{StepWriter.Ref(globals.Direction3D(1, 0, 0))},{StepWriter.Real(FeetToMetres)}");
        int line = w.Add("IFCLINE", $"{StepWriter.Ref(globals.Origin3D())},{StepWriter.Ref(vector)}");
        int endPoint = globals.Point3D(high - low, 0, 0);
        int curve = w.Add("IFCTRIMMEDCURVE",
            $"{StepWriter.Ref(line)},{StepWriter.List(StepWriter.Ref(globals.Origin3D()))},{StepWriter.List(StepWriter.Ref(endPoint))},.T.,.CARTESIAN.");
        int axisRepresentation = w.Add("IFCSHAPEREPRESENTATION",
            $"{StepWriter.Ref(axisSubContextId)},'Axis','Curve2D',{StepWriter.List(StepWriter.Ref(curve))}");
        w.ReplaceArgs(shapeIndex, $"{shape[0]},{shape[1]},{StepWriter.List(StepWriter.Ref(axisRepresentation), representations[0])}");
        return true;
    }

    private static bool TryConvertStandardCaseWallCurrent(
        StepWriter w, Dictionary<int, int> indexById, GlobalsCurrent globals, string wallArgs, List<int> detached, Element? element)
    {
        var wall = SplitStepArguments(wallArgs);
        if (wall.Length < 8 || !TryEntityId(wall[5], out int placementId) || !indexById.TryGetValue(placementId, out int placementIndex)
            || !TryEntityId(wall[6], out int shapeId) || !indexById.TryGetValue(shapeId, out int shapeIndex))
            return false;

        var placement = SplitStepArguments(w.EntityAt(placementIndex).Args);
        if (w.EntityAt(placementIndex).Type != "IFCLOCALPLACEMENT" || placement.Length != 2
            || !TryEntityId(placement[1], out int frameId) || !indexById.TryGetValue(frameId, out int frameIndex)
            || w.EntityAt(frameIndex).Type != "IFCAXIS2PLACEMENT3D")
            return false;
        var frame = SplitStepArguments(w.EntityAt(frameIndex).Args);
        if (frame.Length != 3 || frame[1] != "$" || !TryReadPointCurrent(w, indexById, frame[0], out var origin) || origin.Length != 3)
            return false;
        double[] u = [1, 0, 0];
        if (frame[2] != "$" && !(TryEntityId(frame[2], out int directionId) && indexById.TryGetValue(directionId, out int directionIndex)
                && w.EntityAt(directionIndex).Type == "IFCDIRECTION"
                && SplitStepArguments(w.EntityAt(directionIndex).Args[1..^1]) is { Length: 3 } ratios
                && TryParseStepReal(ratios[0], out u[0]) && TryParseStepReal(ratios[1], out u[1]) && TryParseStepReal(ratios[2], out u[2])))
            return false;
        double uLength = Math.Sqrt(u[0] * u[0] + u[1] * u[1]);
        if (Math.Abs(u[2]) > 1e-9 || uLength < 1e-9) return false;
        u = CleanAxisDirectionCurrent(u[0], u[1]);

        var shape = SplitStepArguments(w.EntityAt(shapeIndex).Args);
        if (w.EntityAt(shapeIndex).Type != "IFCPRODUCTDEFINITIONSHAPE" || shape.Length != 3 || shape[2].Length < 3) return false;
        int axisCurveIndex = -1, solidIndex = -1;
        foreach (string token in SplitStepArguments(shape[2][1..^1]))
        {
            if (!TryEntityId(token, out int representationId) || !indexById.TryGetValue(representationId, out int representationIndex))
                return false;
            var representation = SplitStepArguments(w.EntityAt(representationIndex).Args);
            if (w.EntityAt(representationIndex).Type != "IFCSHAPEREPRESENTATION" || representation.Length != 4
                || representation[3].Length < 3 || SplitStepArguments(representation[3][1..^1]) is not { Length: 1 } items
                || !TryEntityId(items[0], out int itemId) || !indexById.TryGetValue(itemId, out int itemIndex))
                return false;
            if (representation[1] == "'Axis'" && representation[2] == "'Curve2D'") axisCurveIndex = itemIndex;
            else if (representation[1] == "'Body'" && representation[2] == "'SweptSolid'") solidIndex = itemIndex;
            else return false;
        }
        if (axisCurveIndex < 0 || solidIndex < 0 || w.EntityAt(axisCurveIndex).Type != "IFCPOLYLINE"
            || w.EntityAt(solidIndex).Type != "IFCEXTRUDEDAREASOLID")
            return false;

        var axis = SplitStepArguments(w.EntityAt(axisCurveIndex).Args);
        if (axis.Length != 1 || axis[0].Length < 3 || SplitStepArguments(axis[0][1..^1]) is not { Length: 2 } axisPoints
            || !TryReadPointCurrent(w, indexById, axisPoints[0], out var start) || !TryReadPointCurrent(w, indexById, axisPoints[1], out var end)
            || start.Length != 2 || end.Length != 2 || Math.Abs(end[1] - start[1]) > 1e-9 || end[0] - start[0] < 1e-9)
            return false;

        var solid = SplitStepArguments(w.EntityAt(solidIndex).Args);
        if (solid.Length != 4 || !TryParseStepReal(solid[3], out double depth)
            || !TryEntityId(solid[1], out int positionId) || !indexById.TryGetValue(positionId, out int positionIndex)
            || w.EntityAt(positionIndex).Type != "IFCAXIS2PLACEMENT3D"
            || SplitStepArguments(w.EntityAt(positionIndex).Args) is not { Length: 3 } position
            || position[1] != "$" || position[2] != "$"
            || !TryReadPointCurrent(w, indexById, position[0], out var positionOrigin) || positionOrigin.Any(v => Math.Abs(v) > 1e-12)
            || !TryEntityId(solid[2], out int solidDirectionId) || !indexById.TryGetValue(solidDirectionId, out int solidDirectionIndex)
            || w.EntityAt(solidDirectionIndex).Args != "(0.,0.,1.)"
            || !TryEntityId(solid[0], out int profileId) || !indexById.TryGetValue(profileId, out int profileIndex))
            return false;

        // The wall of an arc location curve: the axis is the circle of the arc, the profile the loop of its end face.
        if (element?.Location is { CurveType: "Arc" }
            && TryConvertArcStandardCaseWallCurrent(w, indexById, globals, wall, placementIndex, placement, frameIndex, frameId, origin,
                axisCurveIndex, solidIndex, detached, element))
            return true;

        // A wall that is not a thickened axis is exported by the body exporter: the extrusion of the lower of the two parallel
        // end faces of its body, in the loops of that face.
        WallExtrusion.AxisExtrusion? extrusion = null;
        if (element?.Location is { CurveType: "Line", ArcRadius: null } && PassesSimpleRectangularWallCurrent(element) == false
            && FirstSolidFacesCurrent(element) is { Count: > 0 } solidFaces)
            extrusion = WallExtrusion.FindAxisExtrusion(solidFaces, [0, 0, 1], out _);
        bool hasRectangle = TryReadWallRectangleCurrent(w, indexById, solid[0], out var rectangle);
        if (extrusion == null && !hasRectangle) return false;

        if (element?.OwnWallTrimmedAxis is { Length: 2 } trimmedAxis)
        {
            double[] along = trimmedAxis.Select(point => (point[0] * FeetToMetres - origin[0]) * u[0]
                + (point[1] * FeetToMetres - origin[1]) * u[1]).ToArray();
            double low = Math.Min(along[0], along[1]), high = Math.Max(along[0], along[1]);
            if (high - low > 1e-9 && (Math.Abs(low - start[0]) > 1e-5 || Math.Abs(high - end[0]) > 1e-5))
            {
                start = [low, start[1]];
                end = [high, end[1]];
                if (hasRectangle) rectangle = [low, high, rectangle[2], rectangle[3]];
            }
        }

        double length = end[0] - start[0];
        double[] newOrigin =
        [
            origin[0] + start[0] * u[0] - start[1] * u[1],
            origin[1] + start[0] * u[1] + start[1] * u[0],
            origin[2],
        ];
        detached.AddRange(ReferencedIdsCurrent(w.EntityAt(frameIndex).Args));
        int newFrame = globals.Axis3D(newOrigin, [0, 0, 1], [u[0], u[1], 0]);
        w.ReplaceArgs(placementIndex, $"{placement[0]},{StepWriter.Ref(newFrame)}");
        detached.Add(frameId);

        if (extrusion != null)
        {
            var loops = new List<IReadOnlyList<double[]>>();
            double[] ToFrame(double[] point) =>
            [
                (point[0] * FeetToMetres - newOrigin[0]) * u[0] + (point[1] * FeetToMetres - newOrigin[1]) * u[1],
                -(point[0] * FeetToMetres - newOrigin[0]) * u[1] + (point[1] * FeetToMetres - newOrigin[1]) * u[0],
                point[2] * FeetToMetres - newOrigin[2],
            ];
            var frameArcs = extrusion.Arcs?.Select(arc => arc.Transformed(ToFrame,
                vector => [vector[0] * u[0] + vector[1] * u[1], -vector[0] * u[1] + vector[1] * u[0], vector[2]], FeetToMetres)).ToList();
            for (int i = 0; i < extrusion.Loops.Count; i++)
                loops.Add(CurveLoopCurrent(extrusion.Loops[i].Select(ToFrame).ToList(), [0, 0, 1], outer: i == 0, frameArcs));
            string[] nameParts = wall[2].Trim('\'').Split(':');
            var parts = WriteExtrusionProfileCurrent(w, globals, nameParts.Length >= 3 ? $"'{nameParts[1]}'" : "''", loops, [0, 0, 1], [u[1], u[0], 0]);
            detached.AddRange(ReferencedIdsCurrent(w.EntityAt(profileIndex).Args));
            detached.Add(profileId);
            detached.AddRange(ReferencedIdsCurrent(w.EntityAt(positionIndex).Args));
            detached.Add(positionId);
            detached.Add(solidDirectionId);
            w.ReplaceArgs(solidIndex, $"{StepWriter.Ref(parts.ProfileId)},{StepWriter.Ref(parts.PositionId)},{StepWriter.Ref(parts.DirectionId)},"
                + StepWriter.Real(extrusion.LengthFeet * FeetToMetres));
        }
        else
        {
            double centreX = (rectangle[0] + rectangle[1]) / 2 - start[0], centreY = (rectangle[2] + rectangle[3]) / 2 - start[1];
            int centre = globals.Point2D(centreX, centreY);
            int profilePlacement = w.Add("IFCAXIS2PLACEMENT2D", $"{StepWriter.Ref(centre)},{StepWriter.Ref(globals.Direction2D(-1, 0))}");
            detached.AddRange(ReferencedIdsCurrent(w.EntityAt(profileIndex).Args));
            w.ReplaceEntity(profileIndex, "IFCRECTANGLEPROFILEDEF",
                $".AREA.,'',{StepWriter.Ref(profilePlacement)},{StepWriter.Real(rectangle[1] - rectangle[0])},{StepWriter.Real(rectangle[3] - rectangle[2])}");
            detached.AddRange(ReferencedIdsCurrent(w.EntityAt(positionIndex).Args));
            w.ReplaceArgs(positionIndex, $"{StepWriter.Ref(globals.Origin3D())},$,$");
            string depthText = element != null && OwnSimpleWallHeightCurrent(element) is { } ownHeight
                ? StepWriter.Real(ownHeight) : solid[3];
            w.ReplaceArgs(solidIndex, $"{StepWriter.Ref(profileId)},{StepWriter.Ref(positionId)},{StepWriter.Ref(globals.Direction3D(0, 0, 1))},{depthText}");
            detached.Add(solidDirectionId);
        }

        int vector = w.Add("IFCVECTOR", $"{StepWriter.Ref(globals.Direction3D(1, 0, 0))},{StepWriter.Real(FeetToMetres)}");
        int line = w.Add("IFCLINE", $"{StepWriter.Ref(globals.Origin3D())},{StepWriter.Ref(vector)}");
        int endPoint = globals.Point3D(length, 0, 0);
        detached.AddRange(ReferencedIdsCurrent(w.EntityAt(axisCurveIndex).Args));
        w.ReplaceEntity(axisCurveIndex, "IFCTRIMMEDCURVE",
            $"{StepWriter.Ref(line)},{StepWriter.List(StepWriter.Ref(globals.Origin3D()))},{StepWriter.List(StepWriter.Ref(endPoint))},.T.,.CARTESIAN.");
        return true;
    }
}
