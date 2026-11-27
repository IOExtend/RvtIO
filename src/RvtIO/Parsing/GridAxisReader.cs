using RvtIO.Model;

namespace RvtIO.Parsing;

/// <summary>Reads the authoritative surface of a Grid and the ordered members of a GridChain.</summary>
internal sealed class GridAxisReader
{
    private const string SurfaceKey = "DatumPlane.m_pSurface";
    internal const string MembershipKey = "Grid.m_pGridChainMembershipCell";
    private const string ChainMap = "GridChain.m_curveTagToGridsInChain";
    internal const string MemberPair = "std::pair< int, ElementId >";
    private readonly WideBlockWalker _walker;
    private readonly Dictionary<int, (string? Name, int? Owner, LocationCurve? Curve)> _segments = [];
    private readonly Dictionary<int, (string Name, int[] Members)> _chains = [];

    internal GridAxisReader(SchemaDocument schema, IReadOnlyDictionary<Guid, SchemaField[]?> entities, ElementIdMap? ids = null)
    {
        _walker = new(schema, static (type, field) => type.Name switch
        {
            "Element" when field.Name == "m_id" => "Element.m_id",
            "DatumPlane" when field.Name is "m_text" or "m_pSurface" => type.Name + "." + field.Name,
            "Grid" when field.Name == "m_pGridChainMembershipCell" => MembershipKey,
            "GridChain" when field.Name is "commonText" or "m_curveTagToGridsInChain" => type.Name + "." + field.Name,
            "GridChainMembershipCell" when field.Name == "m_gridChainId" => type.Name + "." + field.Name,
            "Envelope" when field.Name == "m_corners" => type.Name + "." + field.Name,
            "Plane" or "CylSurf" => type.Name + "." + field.Name,
            _ => null,
        }, [MemberPair], captureRootPointers: true, ids: ids);
        _walker.UseEntitySchemas(entities, passUnknown: true);
    }

    internal bool IsGrid(int classId) => _walker.Class(classId)?.Name is "Grid" or "GridChain";

    internal void Read(uint id, byte[] data, int offset, int size, int classId)
    {
        if (id is 0 or > int.MaxValue || offset < 0 || size < 2 || (long)offset + size > data.Length) return;
        var walk = _walker.Run(data, offset, size, classId);
        if (!walk.RootRead || Field("Element.m_id", -1).Integer != id) return;
        if (_walker.Class(classId)?.Name == "GridChain")
        {
            if (!walk.Exact || Field("GridChain.commonText", -1).Text is not { } label || string.IsNullOrWhiteSpace(label))
                return;
            if (ReadChainMemberIds(walk) is { } members && members.All(member => member <= int.MaxValue))
                _chains.Add((int)id, (label, members.Select(member => (int)member).ToArray()));
            return;
        }

        int? chainId = null;
        if (walk.RootPointers?.TryGetValue(MembershipKey, out int membership) == true)
        {
            long owner = Field("GridChainMembershipCell.m_gridChainId", membership).Integer;
            // An unfinished membership object still identifies the element as part of a chain.
            chainId = owner is > 0 and <= int.MaxValue ? (int)owner : 0;
        }
        var name = Field("DatumPlane.m_text", -1).Text;
        LocationCurve? curve = null;
        if (walk.RootPointers?.TryGetValue(SurfaceKey, out int surface) == true && surface < walk.QueueEnd.Count)
        {
            double start = Real("Envelope.m_corners@0/0", surface), end = Real("Envelope.m_corners@1/0", surface);
            string? type = _walker.Class(walk.Queue[surface])?.Name;
            if (double.IsFinite(start) && double.IsFinite(end) && end > start)
            {
                if (type == "CylSurf")
                    curve = Arc(Vector("CylSurf.m_center", surface), Vector("CylSurf.m_xVec", surface),
                        Vector("CylSurf.m_yVec", surface), Vector("CylSurf.m_zVec", surface),
                        Real("CylSurf.m_radius", surface), start, end);
                else if (type == "Plane" && chainId != null)
                {
                    var origin = Vector("Plane.m_origin", surface);
                    var direction = Vector("Plane.m_xVec", surface);
                    if (double.IsFinite(origin[2]) && Math.Abs(direction[2]) <= 1e-8)
                    {
                        // Like the arcs, chain members use Grid.Curve's model plan even when the sketch is elevated.
                        direction[2] = 0;
                        double[] Point(double t) => [origin[0] + t * direction[0], origin[1] + t * direction[1], 0];
                        var a = Point(start);
                        var b = Point(end);
                        double length = Math.Sqrt(DistanceSquared(a, b));
                        if (double.IsFinite(length) && length > 0)
                            curve = new LocationCurve { StartPoint = a, EndPoint = b, Direction = direction, Length = length };
                    }
                }
            }
        }
        if (curve != null || chainId != null) _segments.Add((int)id, (name, chainId, curve));

        WideBlockWalker.Capture Field(string key, int queue) =>
            walk.Captures.FirstOrDefault(field => field.Key == key && field.QueueIndex == queue);
        double Real(string key, int queue) => Field(key, queue) is { Kind: WideBlockWalker.CaptureKind.Real } field ? field.Real : double.NaN;
        double[] Vector(string key, int queue) => [Real(key + "@0", queue), Real(key + "@1", queue), Real(key + "@2", queue)];
    }

    internal static long[]? ReadChainMemberIds(WideBlockWalker.Walk walk)
    {
        if (!walk.Exact || walk.Captures.Where(field => field.AtRoot && field.Key == ChainMap + "#").ToArray()
            is not [{ Kind: WideBlockWalker.CaptureKind.Integer } count] || count.Integer < 2)
            return null;
        var order = new SortedDictionary<int, long>();
        foreach (var pair in walk.Records)
        {
            if (pair.Values.GetValueOrDefault(WideBlockWalker.RootFieldKey) is not ChainMap) continue;
            if (pair.Values.GetValueOrDefault("first") is not long tag || tag is < 0 or > int.MaxValue
                || pair.Values.GetValueOrDefault("second") is not long member || member <= 0 || !order.TryAdd((int)tag, member))
                return null;
        }
        return order.Count == count.Integer && order.Values.Distinct().Count() == order.Count ? order.Values.ToArray() : null;
    }

    internal (List<GridAxis> Paths, HashSet<int> Members) Build()
    {
        var paths = new List<GridAxis>();
        var members = _segments.Where(pair => pair.Value.Owner.HasValue).Select(pair => pair.Key).ToHashSet();
        foreach (var (id, segment) in _segments)
            if (segment is { Owner: null, Curve: { } curve, Name: { } name } && !string.IsNullOrWhiteSpace(name))
                paths.Add(new GridAxis(id, name, [curve]));
        foreach (var (id, chain) in _chains)
        {
            members.UnionWith(chain.Members);
            var curves = new List<LocationCurve>(chain.Members.Length);
            foreach (int member in chain.Members)
            {
                if (!_segments.TryGetValue(member, out var segment) || segment.Owner != id || segment.Curve == null) break;
                curves.Add(segment.Curve);
            }
            if (curves.Count == chain.Members.Length && Orient(curves))
                paths.Add(new GridAxis(id, chain.Name, curves.ToArray()));
        }
        return (paths, members);
    }

    private static LocationCurve? Arc(double[] center, double[] x, double[] y, double[] axis, double radius, double start, double end)
    {
        const double Epsilon = 1e-8;
        double sweep = end - start;
        if (!center.Concat(x).Concat(y).Concat(axis).All(double.IsFinite) || !double.IsFinite(radius) || radius <= 0
            || sweep > 2 * Math.PI + Epsilon || Math.Abs(axis[0]) > Epsilon || Math.Abs(axis[1]) > Epsilon
            || Math.Abs(Math.Abs(axis[2]) - 1) > Epsilon || Math.Abs(x[2]) > Epsilon || Math.Abs(y[2]) > Epsilon
            || Math.Abs(x[0] * y[0] + x[1] * y[1]) > Epsilon
            || Math.Abs(x[0] * x[0] + x[1] * x[1] - 1) > Epsilon
            || Math.Abs(y[0] * y[0] + y[1] * y[1] - 1) > Epsilon)
            return null;
        // Grid.Curve is the horizontal datum curve at model Z=0, even when the cylinder's origin is offset.
        center[2] = x[2] = y[2] = 0;
        var first = GridAxis.PointOnArc(center, radius, x, y, start);
        var last = GridAxis.PointOnArc(center, radius, x, y, end);
        if (!first.Concat(last).All(double.IsFinite) || !double.IsFinite(radius * sweep)) return null;
        return new LocationCurve
        {
            CurveType = "Arc", StartPoint = first, EndPoint = last, Length = radius * sweep,
            ArcCenter = center, ArcRadius = radius, ArcXDirection = x, ArcYDirection = y,
            ArcStartParameter = start, ArcEndParameter = end,
        };
    }

    internal static bool Orient(List<LocationCurve> curves)
    {
        // Analytic endpoints of consecutive stored segments can differ by floating-point rounding.
        static bool Meets(double[] a, double[] b) => DistanceSquared(a, b) <= 1e-12;
        static bool Touches(double[] point, LocationCurve curve) => Meets(point, curve.StartPoint) || Meets(point, curve.EndPoint);
        bool start = Touches(curves[0].StartPoint, curves[1]), end = Touches(curves[0].EndPoint, curves[1]);
        if (start == end) return false;
        if (start) Reverse(curves[0]);
        for (int i = 1; i < curves.Count; i++)
        {
            start = Meets(curves[i - 1].EndPoint, curves[i].StartPoint);
            end = Meets(curves[i - 1].EndPoint, curves[i].EndPoint);
            if (start == end) return false;
            if (end) Reverse(curves[i]);
        }
        return true;
    }

    private static void Reverse(LocationCurve curve)
    {
        (curve.StartPoint, curve.EndPoint) = (curve.EndPoint, curve.StartPoint);
        (curve.ArcStartParameter, curve.ArcEndParameter) = (curve.ArcEndParameter, curve.ArcStartParameter);
        if (curve.Direction is [var x, var y, var z]) curve.Direction = [-x, -y, -z];
    }

    private static double DistanceSquared(double[] a, double[] b)
    {
        double x = a[0] - b[0], y = a[1] - b[1], z = a[2] - b[2];
        return x * x + y * y + z * z;
    }
}
