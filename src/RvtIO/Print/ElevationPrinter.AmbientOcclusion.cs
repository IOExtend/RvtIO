using System.Numerics;
using RvtIO.Pdf;
using Point = (double X, double Y);

namespace RvtIO.Print;

public static partial class ElevationPrinter
{
    internal sealed record AmbientPatch(PdfRasterImage Image, double X0, double Y0, double X1, double Y1,
        PdfRasterImage? ThroughGlass = null, IReadOnlyList<AmbientGlass>? Glass = null);

    internal sealed record AmbientGlass(List<Point> Polygon, List<List<Point>>? Holes, double FarDepth);

    internal sealed class AmbientOcclusion(double ambientFraction, (double X0, double Y0, double X1, double Y1) crop,
        bool binaryVisibility = false)
    {
        private const float RayBias = 0.05f;
        private const int RayCount = 24;
        // shortcut: a local model-space falloff, replace it when the display kernel is available.
        private const float AmbientReach = 1500;
        private readonly List<Occluder> _occluders = [];
        private readonly List<(List<Point> Polygon, List<List<Point>>? Holes, Vector3 Origin, Vector3 Normal,
            double Gray, bool OrientDirectLight, bool IgnoreStairExitHits, bool IgnoreEdgeOnHits,
            bool ExcludeSamplesBehindCut, bool DistanceFalloff, int[]? AttachedRoofIds,
            (double Elevation, float Depth)? ExteriorWall, bool ProjectedSlabSide)> _receivers = [];
        private readonly List<AmbientGlass> _glass = [];

        private readonly record struct Occluder(Vector3 Origin, Vector3 Normal, Vector3 Min, Vector3 Max,
            int Axis, List<Point> Outline, List<List<Point>>? Holes, bool FrontFaceOnly, int RoofId);
        private sealed record Node(Vector3 Min, Vector3 Max, int Start, int Count, Node? Left, Node? Right);

        internal void AddOccluder(List<Vector3> ring, Vector3 normal, List<List<Vector3>>? holes = null,
            bool frontFaceOnly = false, int roofId = 0)
        {
            if (ring.Count < 3 || !(normal.LengthSquared() > 0.5f)) return;
            Vector3 min = ring[0], max = ring[0];
            foreach (Vector3 point in ring)
            {
                min = Vector3.Min(min, point);
                max = Vector3.Max(max, point);
            }
            var abs = Vector3.Abs(normal);
            int axis = abs.X > abs.Y && abs.X > abs.Z ? 0 : abs.Y > abs.Z ? 1 : 2;
            List<Point> Flatten(List<Vector3> points) => points.Select(point => Flat(point, axis)).ToList();
            var margin = new Vector3(RayBias);
            _occluders.Add(new(ring[0], normal, min - margin, max + margin, axis, Flatten(ring),
                holes?.Select(Flatten).ToList(), frontFaceOnly, roofId));
        }

        internal void AddReceiver(List<Point> polygon, List<List<Point>>? holes, Vector3 origin, Vector3 normal,
            double gray = 1, bool orientDirectLight = false, bool ignoreStairExitHits = false,
            bool ignoreEdgeOnHits = false,
            bool excludeSamplesBehindCut = false, bool distanceFalloff = false, int[]? attachedRoofIds = null,
            (double Elevation, float Depth)? exteriorWall = null, bool projectedSlabSide = false)
        {
            if (normal.Z < -1e-5f)
                _receivers.Add((polygon, holes, origin, Vector3.Normalize(normal), gray, orientDirectLight,
                    ignoreStairExitHits, ignoreEdgeOnHits, excludeSamplesBehindCut, distanceFalloff, attachedRoofIds,
                    exteriorWall, projectedSlabSide));
        }

        internal void AddGlazing(List<Point> polygon, List<List<Point>>? holes, double farDepth) =>
            _glass.Add(new(polygon, holes, farDepth));

        internal IReadOnlyDictionary<List<Point>, AmbientPatch>? Calculate(
            IReadOnlyList<List<Point>>? cutCaps = null, double cutDepth = 0)
        {
            if (_receivers.Count == 0 || _occluders.Count == 0) return null;
            var occluders = _occluders.ToArray();
            var tree = Build(occluders, 0, occluders.Length);
            var result = new Dictionary<List<Point>, AmbientPatch>();
            Span<Vector3> rays = stackalloc Vector3[RayCount];
            const int gridSize = 3;
            Span<(double X, double Y, bool OnFace)> samples = stackalloc (double, double, bool)[gridSize * gridSize];
            Span<bool> sampled = stackalloc bool[samples.Length];
            Span<bool> behindCut = stackalloc bool[samples.Length];
            float visibleSpan = (float)Math.Min(crop.X1 - crop.X0, crop.Y1 - crop.Y0);
            foreach (var (polygon, holes, planeOrigin, normal, gray, orientDirectLight, ignoreStairExitHits, ignoreEdgeOnHits,
                excludeSamplesBehindCut, distanceFalloff, attachedRoofIds, exteriorWall, projectedSlabSide) in _receivers)
            {
                sampled.Clear();
                behindCut.Clear();
                double left = Math.Max(crop.X0, polygon.Min(p => p.X));
                double right = Math.Min(crop.X1, polygon.Max(p => p.X));
                double bottom = Math.Max(crop.Y0, polygon.Min(p => p.Y));
                double top = Math.Min(crop.Y1, polygon.Max(p => p.Y));
                if (left >= right || bottom >= top) continue;

                var tangent = Vector3.Normalize(Vector3.Cross(normal,
                    Math.Abs(normal.Z) > 0.9f ? Vector3.UnitX : Vector3.UnitZ));
                var bitangent = Vector3.Cross(normal, tangent);
                for (int k = 0; k < rays.Length; k++)
                {
                    float radius = MathF.Sqrt((k + 0.5f) / rays.Length);
                    float angle = k * MathF.PI * (3 - MathF.Sqrt(5));
                    rays[k] = normal * MathF.Sqrt(1 - radius * radius)
                        + radius * (MathF.Cos(angle) * tangent + MathF.Sin(angle) * bitangent);
                }

                bool hasSurfaceSample = false;
                for (int j = 0; j < gridSize; j++)
                for (int i = 0; i < gridSize; i++)
                {
                    double x = left + (right - left) * (i + 0.5) / gridSize;
                    double y = bottom + (top - bottom) * (j + 0.5) / gridSize;
                    bool onFace = PointInPolygon(x, y, polygon)
                        && holes?.Any(hole => PointInPolygon(x, y, hole)) != true;
                    hasSurfaceSample |= onFace;
                    samples[(gridSize - 1 - j) * gridSize + i] = (x, y, onFace);
                }

                var rgb = new byte[samples.Length * 3];
                byte unshaded = (byte)Math.Round(255 * gray);
                double baseLinear = gray <= 0.04045 ? gray / 12.92 : Math.Pow((gray + 0.055) / 1.055, 2.4);
                float Depth(double x, double y) => planeOrigin.Z - (normal.X * ((float)x - planeOrigin.X)
                    + normal.Y * ((float)y - planeOrigin.Y)) / normal.Z;
                double directLightLoss = orientDirectLight ? (1 - ambientFraction) * (1 - Math.Clamp(-normal.Z, 0, 1)) : 0;
                List<AmbientGlass>? glass = null;
                if (directLightLoss > 0 && _glass.Count > 0)
                {
                    glass = [];
                    foreach (var pane in _glass)
                    {
                        if (pane.Polygon.Max(p => p.X) <= left || pane.Polygon.Min(p => p.X) >= right
                            || pane.Polygon.Max(p => p.Y) <= bottom || pane.Polygon.Min(p => p.Y) >= top)
                            continue;

                        // The wall depth varies across an oblique face. Only the portion behind the pane
                        // uses the tone for a transmitted view of the wall.
                        double Gap(Point point) => Depth(point.X, point.Y) - pane.FarDepth - 0.01;
                        List<Point> Clip(List<Point> ring) => ClipHalfPlane(ring, p => Gap(p) > 0, Gap);
                        var mask = Clip(pane.Polygon);
                        if (mask.Count >= 3)
                            glass.Add(pane with { Polygon = mask, Holes = pane.Holes?.Select(Clip).Where(h => h.Count >= 3).ToList() });
                    }
                }
                byte[]? throughGlass = glass is { Count: > 0 } ? new byte[rgb.Length] : null;
                if (throughGlass != null) Array.Fill(throughGlass, unshaded);
                byte unoccluded = directLightLoss > 0 ? ToTone(baseLinear * (1 - directLightLoss)) : unshaded;
                Array.Fill(rgb, unoccluded);
                bool hasShadow = unoccluded < unshaded;
                for (int i = 0; i < samples.Length; i++)
                {
                    var (x, y, onFace) = samples[i];
                    if (!onFace)
                    {
                        if (hasSurfaceSample) continue;
                        // A thin frame may fall between every grid point.
                        var boundary = holes?.Find(hole => PointInPolygon(x, y, hole)) ?? polygon;
                        (x, y, _) = ClosestPolygonPoint(x, y, boundary);
                    }
                    float z = Depth(x, y);
                    if (excludeSamplesBehindCut && z > cutDepth + RayBias
                        && cutCaps?.Any(cap => PointInPolygon(x, y, cap)) == true)
                    {
                        behindCut[i] = true;
                        continue;
                    }
                    sampled[i] = true;
                    var receiver = new Vector3((float)x, (float)y, z);
                    double blocked = 0;
                    double sectionBlocked = 0;
                    // The saved crop and cut plane limit how far a buried wall samples its surroundings.
                    float reach = exteriorWall is { } wall && y < wall.Elevation
                        ? Math.Clamp(visibleSpan, AmbientReach, Math.Max(AmbientReach, z - wall.Depth))
                        : AmbientReach;
                    float sectionDepth = projectedSlabSide ? z - (float)cutDepth : 0;
                    foreach (var ray in rays)
                    {
                        float rayReach = sectionDepth > reach && ray.Z < -1e-6f ? sectionDepth / -ray.Z : reach;
                        float distance = rayReach;
                        if (!FindHit(tree, occluders, receiver + normal * RayBias, ray, ignoreStairExitHits, ignoreEdgeOnHits,
                            attachedRoofIds, ref distance))
                            continue;
                        if (distance < reach)
                        {
                            float relativeDistance = distance / reach;
                            blocked += binaryVisibility && !distanceFalloff && reach == AmbientReach
                                ? 1 : 1 - relativeDistance * relativeDistance;
                        }
                        if (rayReach > reach)
                        {
                            float relativeDistance = distance / rayReach;
                            sectionBlocked += 1 - relativeDistance * relativeDistance;
                        }
                    }
                    double ambientLoss = ambientFraction * blocked / RayCount;
                    // Approximate the reduced light through the section opening separately from contact shadows.
                    double sectionLight = 1 - (1 - ambientFraction) * sectionBlocked / RayCount;
                    byte tone = ToTone(baseLinear * (1 - ambientLoss - directLightLoss) * sectionLight);
                    int pixel = i * 3;
                    rgb[pixel] = rgb[pixel + 1] = rgb[pixel + 2] = tone;
                    if (throughGlass != null)
                        throughGlass[pixel] = throughGlass[pixel + 1] = throughGlass[pixel + 2] =
                            ToTone(baseLinear * (1 - ambientLoss));
                    hasShadow |= tone < unshaded;
                }
                if (hasShadow)
                {
                    // Extend valid tones across the cap in the texture so interpolation uses the visible slab.
                    for (int i = 0; i < behindCut.Length; i++)
                    {
                        if (!behindCut[i]) continue;
                        int nearest = -1;
                        double best = double.PositiveInfinity;
                        for (int j = 0; j < sampled.Length; j++)
                        {
                            if (!sampled[j]) continue;
                            double dx = samples[i].X - samples[j].X;
                            double dy = samples[i].Y - samples[j].Y;
                            double distance = dx * dx + dy * dy;
                            if (distance >= best) continue;
                            nearest = j;
                            best = distance;
                        }
                        if (nearest < 0) continue;
                        Array.Copy(rgb, nearest * 3, rgb, i * 3, 3);
                        if (throughGlass != null)
                            Array.Copy(throughGlass, nearest * 3, throughGlass, i * 3, 3);
                    }
                    result.Add(polygon, new(Raster(rgb), left, bottom, right, top,
                        throughGlass != null ? Raster(throughGlass) : null, glass));
                }

                static PdfRasterImage Raster(byte[] samples) => new(gridSize, gridSize, samples) { Interpolate = true };
            }
            return result.Count > 0 ? result : null;
        }

        internal static byte ToTone(double linear)
        {
            double srgb = linear <= 0.0031308 ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
            return (byte)Math.Round(255 * srgb);
        }

        private static Point Flat(Vector3 p, int axis) => axis switch
        {
            0 => (p.Y, p.Z), 1 => (p.X, p.Z), _ => (p.X, p.Y)
        };

        private static Node Build(Occluder[] faces, int start, int count)
        {
            var min = faces[start].Min;
            var max = faces[start].Max;
            for (int i = start + 1; i < start + count; i++)
            {
                min = Vector3.Min(min, faces[i].Min);
                max = Vector3.Max(max, faces[i].Max);
            }
            if (count <= 8) return new(min, max, start, count, null, null);
            var spread = max - min;
            int axis = spread.X > spread.Y && spread.X > spread.Z ? 0 : spread.Y > spread.Z ? 1 : 2;
            Array.Sort(faces, start, count, Comparer<Occluder>.Create((a, b) =>
                (a.Min[axis] + a.Max[axis]).CompareTo(b.Min[axis] + b.Max[axis])));
            int half = count / 2;
            return new(min, max, start, 0, Build(faces, start, half), Build(faces, start + half, count - half));
        }

        private static bool FindHit(Node node, Occluder[] faces, Vector3 origin, Vector3 direction, bool ignoreStairExitHits,
            bool ignoreEdgeOnHits,
            int[]? attachedRoofIds, ref float limit)
        {
            if (!HitsBounds(node.Min, node.Max, origin, direction, limit)) return false;
            if (node.Left != null)
            {
                bool left = FindHit(node.Left, faces, origin, direction, ignoreStairExitHits, ignoreEdgeOnHits, attachedRoofIds, ref limit);
                bool right = FindHit(node.Right!, faces, origin, direction, ignoreStairExitHits, ignoreEdgeOnHits, attachedRoofIds, ref limit);
                return left || right;
            }
            bool found = false;
            for (int i = node.Start; i < node.Start + node.Count; i++)
            {
                ref readonly var face = ref faces[i];
                if (face.RoofId > 0 && attachedRoofIds != null && Array.IndexOf(attachedRoofIds, face.RoofId) >= 0)
                    continue;
                if (ignoreEdgeOnHits && Math.Abs(face.Normal.Z) < 1e-6f) continue;
                float dot = Vector3.Dot(face.Normal, direction);
                if (Math.Abs(dot) < 1e-6f || ignoreStairExitHits && face.FrontFaceOnly && dot > 0) continue;
                float distance = Vector3.Dot(face.Normal, face.Origin - origin) / dot;
                if (distance <= RayBias || distance >= limit) continue;
                var point = origin + distance * direction;
                if (point.X < face.Min.X || point.X > face.Max.X || point.Y < face.Min.Y || point.Y > face.Max.Y
                    || point.Z < face.Min.Z || point.Z > face.Max.Z) continue;
                var (x, y) = Flat(point, face.Axis);
                if (PointInPolygon(x, y, face.Outline) && face.Holes?.Any(hole => PointInPolygon(x, y, hole)) != true)
                {
                    limit = distance;
                    found = true;
                }
            }
            return found;
        }

        private static bool HitsBounds(Vector3 min, Vector3 max, Vector3 origin, Vector3 direction, float limit)
        {
            float near = 0, far = limit;
            for (int i = 0; i < 3; i++)
            {
                if (Math.Abs(direction[i]) < 1e-8f)
                {
                    if (origin[i] < min[i] || origin[i] > max[i]) return false;
                    continue;
                }
                float a = (min[i] - origin[i]) / direction[i];
                float b = (max[i] - origin[i]) / direction[i];
                near = Math.Max(near, Math.Min(a, b));
                far = Math.Min(far, Math.Max(a, b));
                if (near > far) return false;
            }
            return true;
        }
    }
}
