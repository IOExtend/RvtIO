using RvtIO.Geometry.Meshing;
using RvtIO.Parsing;

namespace RvtIO.Meshes;

internal static class ReinforcementMeshes
{
    private const int Sides = 24;
    private const double ChordToleranceFeet = 0.0005;
    private const double JoinToleranceFeet = 1e-6;

    internal static TriangleMesh? Build(RebarBars element)
    {
        var mesh = new ElementMeshes.MeshBuilder(0);
        foreach (var item in element.Items ?? [element])
        {
            double radius = item.ModelDiameter / 2;
            if (!double.IsFinite(radius) || radius <= 0) return null;
            foreach (var path in item.ModelPaths ?? item.Bars)
                if (!AppendBar(mesh, path, radius)) return null;
        }
        return mesh.Indices.Count > 0 ? mesh.ToMesh() : null;
    }

    private static bool AppendBar(ElementMeshes.MeshBuilder mesh, IReadOnlyList<RebarSegment> path, double radius)
    {
        int firstRing = mesh.VertexCount;
        Pt3 firstPoint = default, firstTangent = default, point = default, tangent = default, normal = default;
        foreach (var piece in path)
        {
            double sweep = piece.End - piece.Start;
            if (!double.IsFinite(sweep) || sweep == 0 || piece.Origin.Length != 3 || piece.Axis.Length != 3
                || (piece.IsArc && (piece.Axis2.Length != 3 || !double.IsFinite(piece.Radius)
                    || piece.Radius <= 0 || Math.Abs(sweep) > 2 * Math.PI + 1e-9)))
                return false;
            var origin = Pt3.From(piece.Origin);
            var x = Pt3.From(piece.Axis);
            var y = piece.IsArc ? Pt3.From(piece.Axis2) : default;
            double maxStep = piece.IsArc
                ? Math.Min(Math.PI / 12, 2 * Math.Acos(Math.Max(0, 1 - ChordToleranceFeet / (piece.Radius + radius))))
                : Math.Abs(sweep);
            double steps = Math.Ceiling(Math.Abs(sweep) / maxStep);
            if (!(steps is >= 1 and <= 4096)) return false;

            for (int i = 0; i <= (int)steps; i++)
            {
                double t = piece.Start + sweep * i / steps;
                double cos = Math.Cos(t), sin = Math.Sin(t);
                var center = piece.IsArc ? origin + piece.Radius * (cos * x + sin * y) : origin + t * x;
                var direction = Math.Sign(sweep) * (piece.IsArc ? -sin * x + cos * y : x);
                double length = direction.Norm;
                if (!center.IsFinite || !double.IsFinite(length) || length <= 0) return false;
                direction /= length;

                int ring = mesh.VertexCount;
                if (ring == firstRing)
                {
                    firstPoint = center;
                    firstTangent = direction;
                    var seed = Math.Abs(direction.Z) < 0.9 ? new Pt3(0, 0, 1) : new Pt3(1, 0, 0);
                    normal = Pt3.Cross(seed, direction);
                }
                else if (i == 0)
                {
                    // Adjacent analytic pieces share the end ring.
                    if ((point - center).Norm > JoinToleranceFeet || tangent.Dot(direction) < 0.999)
                        return false;
                    continue;
                }
                normal -= normal.Dot(direction) * direction;
                double normalLength = normal.Norm;
                if (!(normalLength > 0)) return false;
                normal /= normalLength;
                var binormal = Pt3.Cross(direction, normal);

                for (int side = 0; side < Sides; side++)
                {
                    double angle = 2 * Math.PI * side / Sides;
                    var outward = Math.Cos(angle) * normal + Math.Sin(angle) * binormal;
                    var vertex = center + radius * outward;
                    if (!vertex.IsFinite) return false;
                    mesh.AddVertex(vertex.ToArray(), outward.X, outward.Y, outward.Z);
                    if (ring == firstRing) continue;
                    int previous = ring - Sides;
                    int next = (side + 1) % Sides;
                    mesh.AddTriangle(previous + side, previous + next, ring + side);
                    mesh.AddTriangle(previous + next, ring + next, ring + side);
                }
                point = center;
                tangent = direction;
            }
        }

        if (mesh.VertexCount - firstRing < 2 * Sides) return false;
        int lastRing = mesh.VertexCount - Sides;
        AddCap(firstRing, firstPoint, -1 * firstTangent, reverse: true);
        AddCap(lastRing, point, tangent, reverse: false);
        return true;

        void AddCap(int ring, Pt3 center, Pt3 outward, bool reverse)
        {
            int hub = mesh.VertexCount;
            mesh.AddVertex(center.ToArray(), outward.X, outward.Y, outward.Z);
            for (int side = 0; side < Sides; side++)
            {
                int a = ring + side, b = ring + (side + 1) % Sides;
                if (reverse) mesh.AddTriangle(hub, b, a);
                else mesh.AddTriangle(hub, a, b);
            }
        }
    }
}
