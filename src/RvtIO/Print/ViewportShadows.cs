using System.Numerics;
using RvtIO.Model;

namespace RvtIO.Print;

internal sealed class ViewportShadows
{
    private const int GridSize = 64;
    private readonly (double X, double Y, double Z) _direction, _right, _up;
    private readonly ViewCropFrame _camera;
    private readonly List<Caster> _casters = [];
    private readonly double _shade;
    private readonly double? _ground;
    private double _left = double.PositiveInfinity, _bottom = double.PositiveInfinity;
    private double _rightEdge = double.NegativeInfinity, _top = double.NegativeInfinity;

    private sealed record Caster((double X, double Y)[][] Rings, double Z, double SlopeX, double SlopeY,
        double MinX, double MinY, double MaxX, double MaxY);

    internal ViewportShadows(ViewCropFrame camera, Vector3 direction, int intensity, double? groundElevation = null)
    {
        _camera = camera;
        double x = direction.X, y = direction.Y, z = direction.Z;
        double horizontal = Math.Sqrt(x * x + y * y), magnitude = Math.Sqrt(horizontal * horizontal + z * z);
        _direction = (x / magnitude, y / magnitude, z / magnitude);
        _right = horizontal > 0 ? (-y / horizontal, x / horizontal, 0) : (1, 0, 0);
        _up = (-_direction.Z * _right.Y, _direction.Z * _right.X,
            _direction.X * _right.Y - _direction.Y * _right.X);
        _shade = 1 - intensity / 100d;
        if (groundElevation is double elevation && double.IsFinite(elevation) && _direction.Z > 0
            && Math.Abs(camera.Look[2]) > 1e-8)
            _ground = elevation - camera.OriginZ;
    }

    internal (double Left, double Bottom, double Right, double Top)? GroundShadowBounds =>
        _left < _rightEdge && _bottom < _top ? (_left, _bottom, _rightEdge, _top) : null;

    internal bool FacesLight(double[] normal) => Dot(normal[0], normal[1], normal[2], _direction) > 1e-8;

    internal void AddCaster(IReadOnlyList<double[][]> rings, double[] normal)
    {
        double facing = Dot(normal[0], normal[1], normal[2], _direction);
        if (!(facing > 1e-8)) return;
        double[] first = rings[0][0];
        double z = Dot(first[0] - _camera.OriginX, first[1] - _camera.OriginY, first[2] - _camera.OriginZ, _direction);
        var projected = rings.Select(ring => ring.Select(point =>
        {
            double x = point[0] - _camera.OriginX, y = point[1] - _camera.OriginY, h = point[2] - _camera.OriginZ;
            return (X: Dot(x, y, h, _right), Y: Dot(x, y, h, _up));
        }).ToArray()).ToArray();
        var outer = projected[0];
        if (!double.IsFinite(z) || outer.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y))) return;
        _casters.Add(new(projected, z,
            -Dot(normal[0], normal[1], normal[2], _right) / facing,
            -Dot(normal[0], normal[1], normal[2], _up) / facing,
            outer.Min(p => p.X), outer.Min(p => p.Y), outer.Max(p => p.X), outer.Max(p => p.Y)));
        if (_ground is not double ground || rings[0].All(point => point[2] <= ground + _camera.OriginZ)) return;
        foreach (var point in rings[0])
        {
            double t = (point[2] - _camera.OriginZ - ground) / _direction.Z;
            double x = point[0] - _camera.OriginX - t * _direction.X;
            double y = point[1] - _camera.OriginY - t * _direction.Y;
            double u = x * _camera.Right[0] + y * _camera.Right[1] + ground * _camera.Right[2];
            double v = x * _camera.Up[0] + y * _camera.Up[1] + ground * _camera.Up[2];
            _left = Math.Min(_left, u);
            _rightEdge = Math.Max(_rightEdge, u);
            _bottom = Math.Min(_bottom, v);
            _top = Math.Max(_top, v);
        }
    }

    internal void Apply(byte[] rgb, double[] depth, bool[] facesLight, int columns, int rows,
        double left, double top, double stepX, double stepY, double anchorX, double anchorY, double scale)
    {
        // Approximate the tone of surfaces facing away from the active sun with the saved shadow strength.
        for (int pixel = 0; pixel < depth.Length; pixel++)
            if (!facesLight[pixel] && double.IsFinite(depth[pixel]))
                Shade(pixel);
        if (_casters.Count == 0) return;
        double minX = _casters.Min(p => p.MinX), maxX = _casters.Max(p => p.MaxX);
        double minY = _casters.Min(p => p.MinY), maxY = _casters.Max(p => p.MaxY);
        if (!(maxX > minX && maxY > minY)) return;
        double cellX = (maxX - minX) / GridSize, cellY = (maxY - minY) / GridSize;
        int GridX(double x) => Math.Clamp((int)((x - minX) / cellX), 0, GridSize - 1);
        int GridY(double y) => Math.Clamp((int)((y - minY) / cellY), 0, GridSize - 1);
        var grid = new List<Caster>?[GridSize * GridSize];
        foreach (var caster in _casters)
        {
            int lastX = GridX(caster.MaxX), lastY = GridY(caster.MaxY);
            for (int y = GridY(caster.MinY); y <= lastY; y++)
                for (int x = GridX(caster.MinX); x <= lastX; x++)
                    (grid[y * GridSize + x] ??= []).Add(caster);
        }

        // The grid indexes polygons. The actual hit still uses their plane and opening loops.
        for (int row = 0; row < rows; row++)
        {
            double y = (top - (row + 0.5) * stepY - anchorY) / scale;
            for (int column = 0; column < columns; column++)
            {
                int pixel = row * columns + column;
                double z = depth[pixel];
                bool groundPixel = double.IsPositiveInfinity(z) && _ground.HasValue;
                if (!facesLight[pixel] && !groundPixel) continue;
                double x = (left + (column + 0.5) * stepX - anchorX) / scale;
                if (groundPixel)
                {
                    if (x < _left || x > _rightEdge || y < _bottom || y > _top) continue;
                    z = (_ground!.Value - x * _camera.Right[2] - y * _camera.Up[2]) / _camera.Look[2];
                }
                double wx = x * _camera.Right[0] + y * _camera.Up[0] + z * _camera.Look[0];
                double wy = x * _camera.Right[1] + y * _camera.Up[1] + z * _camera.Look[1];
                double wz = x * _camera.Right[2] + y * _camera.Up[2] + z * _camera.Look[2];
                double lx = Dot(wx, wy, wz, _right), ly = Dot(wx, wy, wz, _up);
                if (lx < minX || lx > maxX || ly < minY || ly > maxY
                    || grid[GridY(ly) * GridSize + GridX(lx)] is not { } candidates) continue;
                double receiver = Dot(wx, wy, wz, _direction);
                foreach (var caster in candidates)
                {
                    if (lx < caster.MinX || lx > caster.MaxX || ly < caster.MinY || ly > caster.MaxY) continue;
                    var first = caster.Rings[0][0];
                    double blocker = caster.Z + (lx - first.X) * caster.SlopeX + (ly - first.Y) * caster.SlopeY;
                    if (!(blocker - receiver > 1e-6)) continue;
                    bool hit = false;
                    foreach (var polygon in caster.Rings)
                        if (PlanPrinter.PointInPolygon(lx, ly, polygon)) hit = !hit;
                    if (!hit) continue;
                    Shade(pixel);
                    break;
                }
            }
        }

        void Shade(int pixel)
        {
            for (int channel = 0; channel < 3; channel++)
                rgb[3 * pixel + channel] = (byte)Math.Round(rgb[3 * pixel + channel] * _shade);
        }
    }

    private static double Dot(double x, double y, double z, (double X, double Y, double Z) axis) =>
        x * axis.X + y * axis.Y + z * axis.Z;
}
