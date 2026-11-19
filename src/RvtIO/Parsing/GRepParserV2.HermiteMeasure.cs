using RvtIO.Geometry.Meshing;

namespace RvtIO.Parsing;

internal static partial class GRepParserV2
{
    internal static (double Area, double Volume)? MeasureHermiteFace(SurfaceRec surface, double[] reference,
        IReadOnlyList<FaceArea.Coedge> coedges)
    {
        const double tolerance = 1e-9;
        if (surface.Kind != SurfKind.Hermite || surface.Orient != 1 || surface.Env is not { Length: 4 } env
            || surface.UParams is not { Length: >= 2 } u || surface.VParams is not { Length: >= 2 } v
            || surface.HermiteNodes is not { Count: <= 256 } nodes || nodes.Count != (long)u.Length * v.Length
            || reference is not { Length: 3 } || !reference.All(double.IsFinite)
            || !env.All(double.IsFinite) || env[2] <= env[0] || env[3] <= env[1]
            || !Near(env[0], u[0]) || !Near(env[2], u[^1]) || !Near(env[1], v[0]) || !Near(env[3], v[^1])
            || coedges.Count != 4)
            return null;

        // The reconstructed mapping covers a single, positively oriented full knot domain.
        UvPoint[] corners = [new(env[0], env[1]), new(env[2], env[1]), new(env[2], env[3]), new(env[0], env[3])];
        var contour = new List<UvPoint>();
        int start = Array.FindIndex(corners, corner => Matches(Read(coedges[0], 0), corner));
        if (start < 0) return null;
        for (int i = 0; i < 4; i++)
        {
            var edge = coedges[i];
            var a = corners[(start + i) % 4];
            var b = corners[(start + i + 1) % 4];
            int last = edge.Points.Count - 1;
            if (last < 1 || last > 64 - contour.Count || !Matches(Read(edge, 0), a) || !Matches(Read(edge, last), b))
                return null;

            bool alongU = a.Y == b.Y;
            double direction = Math.Sign(alongU ? b.X - a.X : b.Y - a.Y);
            double previous = double.NegativeInfinity;
            double end = (alongU ? b.X : b.Y) * direction;
            for (int j = 0; j < last; j++)
            {
                var point = Read(edge, j);
                double parameter = (alongU ? point.X : point.Y) * direction;
                if (!Near(alongU ? point.Y : point.X, alongU ? a.Y : a.X)
                    || !double.IsFinite(parameter) || parameter <= previous || parameter >= end) return null;
                contour.Add(point);
                previous = parameter;
            }
        }

        try
        {
            var face = new PreparedFace(new PatchGrid(u, v, nodes), env, contour.ToArray(), Pt3.From(reference));
            if (!double.IsFinite(face.Step.X) || !double.IsFinite(face.Step.Y) || face.Step.X <= 0 || face.Step.Y <= 0
                || face.Contour.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y))) return null;
            var mesh = new ConformingDelaunay(face.Contour, face.Step);
            mesh.Refine();
            var measure = face.Measure(mesh.Points, mesh.Faces);
            return double.IsFinite(measure.Area) && measure.Area > 0 && double.IsFinite(measure.Volume) ? measure : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }

        static UvPoint Read(FaceArea.Coedge edge, int i)
        {
            if (edge.Points.Count == 0) return new(double.NaN, double.NaN);
            var (u, v) = edge.Points[edge.Reversed ? edge.Points.Count - 1 - i : i];
            return new(u, v);
        }

        static bool Near(double a, double b) => Math.Abs(a - b) <= tolerance;
        static bool Matches(UvPoint a, UvPoint b) => Near(a.X, b.X) && Near(a.Y, b.Y);
    }

    private readonly record struct SurfaceJet(Pt3 Position, Pt3 Du, Pt3 Dv, Pt3 Duu, Pt3 Duv, Pt3 Dvv);

    private sealed class PatchGrid
    {
        private readonly Pt3[][] nets;
        internal readonly double[] UKnots;
        internal readonly double[] VKnots;

        internal PatchGrid(double[] uKnots, double[] vKnots, IReadOnlyList<HermiteSurfaceNode> nodes)
        {
            UKnots = uKnots;
            VKnots = vKnots;
            if (UKnots.Length < 2 || VKnots.Length < 2 || nodes.Count != UKnots.Length * VKnots.Length ||
                !OrderedFinite(UKnots) || !OrderedFinite(VKnots))
                throw new InvalidDataException("Invalid Hermite knot grid.");
            nets = new Pt3[(UKnots.Length - 1) * (VKnots.Length - 1)][];
            for (int i = 0; i + 1 < UKnots.Length; i++)
                for (int j = 0; j + 1 < VKnots.Length; j++)
                {
                    var net = new Pt3[16];
                    double h = UKnots[i + 1] - UKnots[i], k = VKnots[j + 1] - VKnots[j];
                    for (int a = 0; a < 2; a++)
                        for (int b = 0; b < 2; b++)
                        {
                            var node = nodes[(j + b) * UKnots.Length + i + a];
                            net[a * 4 + b] = Pt3.From(node.Point);
                            net[(a + 2) * 4 + b] = Pt3.From(node.Du) * h;
                            net[a * 4 + b + 2] = Pt3.From(node.Dv) * k;
                            net[(a + 2) * 4 + b + 2] = Pt3.From(node.Duv) * h * k;
                        }
                    nets[j * (UKnots.Length - 1) + i] = net;
                }
        }

        private static bool OrderedFinite(double[] values) =>
            values.All(double.IsFinite) && Enumerable.Range(1, values.Length - 1).All(i => values[i] > values[i - 1]);

        private static int Interval(double[] knots, double t)
        {
            int pos = Array.BinarySearch(knots, t);
            return Math.Clamp(pos >= 0 ? pos : ~pos - 1, 0, knots.Length - 2);
        }

        private static void Basis(double s, double h, Span<double> b)
        {
            double sq = s * s, hh = h * h;
            b[0] = (2 * s - 3) * sq + 1; b[1] = (3 - 2 * s) * sq;
            b[2] = (s - 2) * sq + s; b[3] = (s - 1) * sq;
            b[4] = (6 * sq - 6 * s) / h; b[5] = (6 * s - 6 * sq) / h;
            b[6] = (3 * sq - 4 * s + 1) / h; b[7] = (3 * sq - 2 * s) / h;
            b[8] = (12 * s - 6) / hh; b[9] = (6 - 12 * s) / hh;
            b[10] = (6 * s - 4) / hh; b[11] = (6 * s - 2) / hh;
        }

        internal SurfaceJet Evaluate(double u, double v)
        {
            int i = Interval(UKnots, u), j = Interval(VKnots, v);
            var net = nets[j * (UKnots.Length - 1) + i];
            double h = UKnots[i + 1] - UKnots[i], k = VKnots[j + 1] - VKnots[j];
            Span<double> b = stackalloc double[12], c = stackalloc double[12];
            Basis((u - UKnots[i]) / h, h, b);
            Basis((v - VKnots[j]) / k, k, c);
            Pt3 p = default, pu = default, pv = default, puu = default, puv = default, pvv = default;
            for (int a = 0; a < 4; a++)
                for (int d = 0; d < 4; d++)
                {
                    var entry = net[a * 4 + d];
                    p += entry * (b[a] * c[d]);
                    pu += entry * (b[4 + a] * c[d]);
                    pv += entry * (b[a] * c[4 + d]);
                    puu += entry * (b[8 + a] * c[d]);
                    puv += entry * (b[4 + a] * c[4 + d]);
                    pvv += entry * (b[a] * c[8 + d]);
                }
            return new(p, pu, pv, puu, puv, pvv);
        }
    }

    private sealed class PreparedFace
    {
        private readonly PatchGrid grid;
        private readonly UvPoint midpoint;
        private readonly UvPoint perturbationCenter;
        private readonly Pt3 fluxOrigin;
        private readonly double curvature;
        private readonly double slope;
        private readonly double width;
        private readonly double scaleU;
        private readonly double scaleV;
        internal UvPoint[] Contour { get; }
        internal UvPoint Step { get; }
        private readonly bool linearScaling;

        internal PreparedFace(PatchGrid grid, double[] envelope, UvPoint[] contour, Pt3 origin)
        {
            this.grid = grid;
            double u0 = envelope[0], u1 = envelope[2];
            double v0 = envelope[1], v1 = envelope[3];
            midpoint = new((u0 + u1) * 0.5, (v0 + v1) * 0.5);
            perturbationCenter = new(u0 * 0.500043219876 + u1 * 0.499956780124, v0 * 0.500067894321 + v1 * 0.499932105679);
            fluxOrigin = origin;
            var mid = grid.Evaluate(midpoint.X, midpoint.Y);
            Span<double> radii = stackalloc double[5];
            radii[0] = CurvatureRadius(mid);
            int at = 1;
            foreach (double u in new[] { u0, u1 })
                foreach (double v in new[] { v0, v1 })
                    radii[at++] = CurvatureRadius(grid.Evaluate(u * 0.9 + midpoint.X * 0.1, v * 0.9 + midpoint.Y * 0.1));
            double radiusMax = 0, radiusMin = double.PositiveInfinity;
            foreach (var r in radii)
            {
                radiusMin = Math.Min(radiusMin, r);
                radiusMax = Math.Max(radiusMax, r);
            }
            bool fine = radiusMax < 1.0 / 12 && radiusMin / 3000 >= 1e-7;
            double chordTolerance = fine ? radiusMin / 3000 : 1.0 / 24;
            var rawStep = DeriveRawStep(grid, chordTolerance);
            double speedU = mid.Du.Norm, speedV = mid.Dv.Norm;
            double accelerationU = mid.Duu.Norm, accelerationV = mid.Dvv.Norm;
            if (Math.Min(speedU, speedV) <= 1e-9)
                throw new InvalidDataException("Singular parameter center.");
            double minAngle = (fine ? 1 : 8) * Math.PI / 180;
            double bendAngle = (fine ? 5 : 20) * Math.PI / 180;
            double distance = Math.Min(rawStep.X * speedU, rawStep.Y * speedV) / Math.Tan(minAngle);
            double du = LimitAngle(Math.Min(rawStep.X, distance / speedU), speedU, accelerationU, bendAngle);
            double dv = LimitAngle(Math.Min(rawStep.Y, distance / speedV), speedV, accelerationV, bendAngle);
            double physicalU = du * speedU, physicalV = dv * speedV;
            double stretchU = Math.Max(1, physicalV / physicalU), stretchV = Math.Max(1, physicalU / physicalV);
            double lowerWidth = grid.Evaluate(midpoint.X, v0).Du.Norm / speedV;
            double midWidth = speedU / speedV;
            double upperWidth = grid.Evaluate(midpoint.X, v1).Du.Norm / speedV;
            double half = (v1 - v0) * 0.5;
            double quadratic = (lowerWidth + upperWidth - 2 * midWidth) / (2 * half * half);
            double linear = (upperWidth - lowerWidth) / (2 * half);
            linearScaling = quadratic > 1e-9;
            double mean = Math.Sqrt(speedU * speedV);
            scaleU = linearScaling ? speedU / mean * stretchU : stretchU;
            scaleV = linearScaling ? speedV / mean * stretchV : stretchV;
            // Scale the global UV polynomial before evaluating it. Rounding changes the order of tied insertions.
            width = (midWidth + midpoint.Y * (quadratic * midpoint.Y - linear)) * scaleU;
            slope = (linear - 2 * quadratic * midpoint.Y) * scaleU;
            curvature = quadratic * scaleU;
            Step = new(du * scaleU * (linearScaling ? 1 : midWidth), dv * scaleV);
            Contour = contour.Select(p => Move(Project(p), -1)).ToArray();
        }

        private static double LimitAngle(double step, double speed, double acceleration, double angle) =>
            acceleration > 0 && speed * speed > acceleration / 24 ? Math.Min(step, angle * speed / acceleration) : step;

        private static double CurvatureRadius(SurfaceJet e)
        {
            var normal = Pt3.Cross(e.Du, e.Dv);
            double n = normal.Norm;
            if (!(n > 0)) throw new InvalidDataException("Singular surface sample.");
            normal *= 1 / n;
            double E = e.Du.Dot(e.Du), F = e.Du.Dot(e.Dv), G = e.Dv.Dot(e.Dv);
            double L = normal.Dot(e.Duu), M = normal.Dot(e.Duv), N = normal.Dot(e.Dvv);
            double det = E * G - F * F;
            if (!(det > 0)) throw new InvalidDataException("Singular metric.");
            double trace = (G * L + E * N - 2 * F * M) / det;
            double gaussian = (L * N - M * M) / det;
            double maxCurvature = (Math.Abs(trace) + Math.Sqrt(Math.Max(0, trace * trace - 4 * gaussian))) * 0.5;
            return 1 / maxCurvature;
        }

        private static UvPoint DeriveRawStep(PatchGrid grid, double tolerance)
        {
            double uMax = 0, vMax = 0;
            void Sample(double u, double v)
            {
                var e = grid.Evaluate(u, v);
                double mixed = e.Duv.Norm;
                uMax = Math.Max(uMax, Math.Max(e.Duu.Norm, mixed));
                vMax = Math.Max(vMax, Math.Max(e.Dvv.Norm, mixed));
            }
            for (int i = 0; i < grid.UKnots.Length; i++)
                for (int j = 0; j < grid.VKnots.Length; j++)
                {
                    Sample(grid.UKnots[i], grid.VKnots[j]);
                    if (i + 1 < grid.UKnots.Length && j + 1 < grid.VKnots.Length)
                        Sample((grid.UKnots[i] + grid.UKnots[i + 1]) * 0.5, (grid.VKnots[j] + grid.VKnots[j + 1]) * 0.5);
                }
            double spanU = grid.UKnots[^1] - grid.UKnots[0], spanV = grid.VKnots[^1] - grid.VKnots[0];
            double Divide(double span, double bound) =>
                span / Math.Clamp(Math.Ceiling(span * Math.Sqrt(bound / (8 * tolerance))) + 1, 2, 60);
            return new(Divide(spanU, uMax), Divide(spanV, vMax));
        }

        private UvPoint Move(UvPoint p, double sign)
        {
            var diff = p - perturbationCenter;
            double norm = Math.Sqrt(diff.LengthSquared);
            return norm < 1e-9 ? p : p + diff * (sign * 1e-7 / norm);
        }

        private double WidthAt(double v) => (curvature * v + slope) * v + width;

        private UvPoint Project(UvPoint p) => linearScaling
            ? new(p.X * scaleU, p.Y * scaleV) : new((p.X - midpoint.X) * WidthAt(p.Y), p.Y * scaleV);

        private Pt3 Reconstruct(UvPoint p)
        {
            p = Move(p, 1);
            double v = p.Y / scaleV;
            double u = linearScaling ? p.X / scaleU : p.X / WidthAt(v) + midpoint.X;
            var xyz = grid.Evaluate(u, v).Position;
            return new((float)xyz.X, (float)xyz.Y, (float)xyz.Z);
        }

        internal (double Area, double Volume) Measure(UvPoint[] points, int[][] faces)
        {
            var p = points.Select(Reconstruct).ToArray();
            double area = 0, volume = 0;
            foreach (var f in faces)
            {
                var cross = Pt3.Cross(p[f[1]] - p[f[0]], p[f[2]] - p[f[0]]);
                area += cross.Norm * 0.5;
                volume += (p[f[0]] - fluxOrigin).Dot(cross) / 6;
            }
            return (area, volume);
        }
    }
}
