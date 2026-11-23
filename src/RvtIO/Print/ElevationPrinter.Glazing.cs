using RvtIO.BuiltIns;
using RvtIO.Pdf;
using Point = (double X, double Y);

namespace RvtIO.Print;

public static partial class ElevationPrinter
{
    internal sealed class AmbientGlazing(double light, bool detailCallout = false)
    {
        private const double PlaneTolerance = 0.01;
        private readonly Dictionary<List<Point>, Surface> _surfaces = [];
        private readonly Dictionary<int, (double? Gray, double Transmission)> _materials = [];

        private readonly record struct Surface(double Near, double Far, double X0, double Y0, double X1, double Y1,
            double? Gray, double Transmission, bool Pane);

        internal bool HasPanes { get; private set; }

        internal void Add(RvtDocument document, List<Point> points, double near, double far, int? materialId, bool glazing)
        {
            double? gray = null;
            double transmission = 0;
            if (materialId is > 0 and int id)
            {
                if (!_materials.TryGetValue(id, out var appearance))
                {
                    var material = document.GetElement(id);
                    if (material is { HasShadingAppearance: true }
                        && material.LookupParameter(BuiltInParameter.MATERIAL_PARAM_COLOR)?.Value is int rgb
                        && rgb is >= 0 and <= 0xffffff)
                        appearance.Gray = ((rgb & 255) * 0.2126 + ((rgb >> 8) & 255) * 0.7152
                            + ((rgb >> 16) & 255) * 0.0722) / 255;
                    appearance.Transmission = material?.SerializedMaterialTransparency ?? 0;
                    _materials.Add(id, appearance);
                }
                (gray, transmission) = appearance;
            }
            bool pane = glazing && gray != null && transmission is > 0 and <= 1;
            HasPanes |= pane;
            _surfaces.Add(points, new(near, far, points.Min(p => p.X), points.Min(p => p.Y),
                points.Max(p => p.X), points.Max(p => p.Y), gray, transmission, pane));
        }

        internal bool IsPane(List<Point> points) => _surfaces.GetValueOrDefault(points).Pane;

        internal double? AmbientBackgroundGray(List<Point> points)
            => !detailCallout && _surfaces.TryGetValue(points, out var pane) && pane.Pane
                ? (1 - pane.Transmission) * pane.Gray!.Value + pane.Transmission * light : null;

        internal void Draw(PdfPage page, ElevationContent content, List<Point> points,
            IReadOnlyList<IReadOnlyList<Point>> paneRings, Func<double, double> toX, Func<double, double> toY,
            Func<List<Point>, IReadOnlyList<List<Point>>, List<IReadOnlyList<Point>>> faceRings)
        {
            var pane = _surfaces[points];
            double reflection = (1 - pane.Transmission) * pane.Gray!.Value;
            if (detailCallout)
            {
                // shortcut: blend over the opaque page base, until transparent backing is decoded for callouts.
                page.FillPolygonUnion(paneRings, reflection + pane.Transmission);
                return;
            }
            page.FillPolygonUnion(paneRings, reflection + pane.Transmission * light);
            page.BeginPolygonClip(paneRings);
            if (content.AmbientTones?.GetValueOrDefault(points) is { } background)
            {
                double x = toX(background.X0), y = toY(background.Y0);
                page.DrawImage(background.Image, x, y, toX(background.X1) - x, toY(background.Y1) - y);
            }
            foreach (var (back, _, _) in content.Faces)
            {
                // Only planes wholly behind the pane are repainted. The existing painter's ordering
                // and inner rings keep nearer solid infill and empty openings distinct.
                if (!_surfaces.TryGetValue(back, out var surface) || surface.Near < pane.Far + PlaneTolerance
                    || surface.Transmission > 0 || surface.X0 >= pane.X1 || surface.X1 <= pane.X0
                    || surface.Y0 >= pane.Y1 || surface.Y1 <= pane.Y0)
                    continue;
                double fill = surface.Gray is double gray ? reflection + pane.Transmission * light * gray : 1;
                var projected = back.Select(point => (toX(point.X), toY(point.Y))).ToList();
                if (content.FaceOpenings?.GetValueOrDefault(back) is { } holes)
                    page.FillPolygonUnion(faceRings(projected, holes), fill);
                else
                    page.DrawPolyline(projected, 0, true, fill);
            }
            page.EndClip();
        }
    }
}
