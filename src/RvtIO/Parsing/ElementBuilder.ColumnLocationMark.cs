using System.Globalization;
using RvtIO.Model;

namespace RvtIO.Parsing;

internal static partial class ElementBuilder
{
    private const int GridsCategoryId = -2000220, StructuralColumnsCategoryId = -2001330, ColumnLocationMarkParamId = -1002563;

    /// <summary>
    /// Root class of a grid occurrence (the grid types of the same category have other root
    /// classes).
    /// </summary>
    private const ushort GridOccurrenceClassId = 2108;

    /// <summary>
    /// Feet; a grid line closer than this to the plan of a column's bounding box counts as touching
    /// it.
    /// </summary>
    private const double PlanTouchTolerance = 1e-6;

    /// <summary>
    /// Feet; intersections whose distances from the column differ by less than this are a tie,
    /// which goes to the first grid pair in element id order.
    /// </summary>
    private const double NearestTieTolerance = 0.02;

    private const double OriginInsideBoxMargin = 0.01;

    /// <summary>
    /// Column Location Mark (COLUMN_LOCATION_MARK) of a structural column (one with
    /// StructuralUserData, which also carries its connection types): the names of the two straight
    /// grids whose intersection is nearest to the column's location point.
    /// </summary>
    internal static void ApplyColumnLocationMarks(RevitFileInfo info, TfBlockDeserializer.UnitOptions? units,
        IReadOnlyDictionary<uint, ushort>? classByElement)
    {
        var lengthOptions = units?.Options
            .Where(option => UnitSchemaEvaluator.DisplayNameKey(option.Spec) == ProjectLengthUnits.LengthSpecName).ToArray();
        if (units == null || lengthOptions is not { Length: 1 } || units.DecimalSymbol != 0) return;
        var length = lengthOptions[0];
        if (length.UseDefault != 0 || length.Accuracy <= 0) return;
        double scale;
        switch (UnitSchemaEvaluator.DisplayNameKey(length.Unit))
        {
            case "autodesk.unit.unit:millimeters": scale = 304.8; break;
            case "autodesk.unit.unit:centimeters": scale = 30.48; break;
            case "autodesk.unit.unit:meters": scale = 0.3048; break;
            default: return;
        }

        // A grid occurrence that is not a straight line read (an arc grid) could be one of the two
        // nearest to a column.
        if (classByElement == null || info.Elements.Any(e => e.CategoryId == GridsCategoryId && e.Location is not { CurveType: "Line" }
                && classByElement.TryGetValue((uint)e.Id, out var rootClass) && rootClass == GridOccurrenceClassId)) return;
        var grids = info.Elements
            .Where(e => e.CategoryId == GridsCategoryId && e.Location is { CurveType: "Line" } && !string.IsNullOrEmpty(e.Name))
            .OrderBy(e => e.Id)
            .Select(e => new Grid(e.Name!, e.Location!.StartPoint[0], e.Location.StartPoint[1], e.Location.EndPoint[0], e.Location.EndPoint[1]))
            .ToList();
        if (grids.Count < 2) return;
        foreach (var column in info.Elements)
        {
            if (column.CategoryId != StructuralColumnsCategoryId || column.NativeMasterSymbolId == null
                || column.StructuralConnectionTypes == null || column.InstanceOriginXY is not { } origin
                || ElementBuilder.GeometryBox(column) is not { Length: >= 6 } box
                || origin.X < box[0] - OriginInsideBoxMargin || origin.X > box[3] + OriginInsideBoxMargin
                || origin.Y < box[1] - OriginInsideBoxMargin || origin.Y > box[4] + OriginInsideBoxMargin
                || NearestGridIntersection(grids, origin.X, origin.Y) is not var (first, second))
                continue;
            var named = new[] { first, second }.OrderBy(g => NameClass(g.Name)).ThenBy(g => g.Name, StringComparer.Ordinal).ToArray();
            string text = string.Join('-', named.Select(grid => GridMark(grid, origin, box, scale, length)));
            SetParam(column, ColumnLocationMarkParamId, "COLUMN_LOCATION_MARK", StorageType.String, text);
        }
    }

    private sealed record Grid(string Name, double X1, double Y1, double X2, double Y2)
    {
        private double Dx => X2 - X1;
        private double Dy => Y2 - Y1;
        private double Span => Math.Sqrt(Dx * Dx + Dy * Dy);

        /// <summary>
        /// Signed distance (feet) of a point from the grid line along the normal with a positive x
        /// component.
        /// </summary>
        public double SignedDistance(double x, double y)
        {
            double nx = -Dy / Span, ny = Dx / Span;
            if (nx < -1e-9 || (Math.Abs(nx) <= 1e-9 && ny < 0)) { nx = -nx; ny = -ny; }
            return (x - X1) * nx + (y - Y1) * ny;
        }

        public bool TouchesPlan(double[] box)
        {
            double min = double.MaxValue, max = double.MinValue;
            foreach (double x in new[] { box[0], box[3] })
                foreach (double y in new[] { box[1], box[4] })
                {
                    double side = ((x - X1) * Dy - (y - Y1) * Dx) / Span;
                    min = Math.Min(min, side);
                    max = Math.Max(max, side);
                }
            return min <= PlanTouchTolerance && max >= -PlanTouchTolerance;
        }

        public (double X, double Y)? Intersection(Grid other)
        {
            double denominator = Dx * other.Dy - Dy * other.Dx;
            if (Math.Abs(denominator) < 1e-9) return null;
            double t = ((other.X1 - X1) * other.Dy - (other.Y1 - Y1) * other.Dx) / denominator;
            return (X1 + t * Dx, Y1 + t * Dy);
        }
    }

    private static int NameClass(string name) => name[0] == '.' ? 0 : char.IsLetter(name[0]) ? 1 : char.IsDigit(name[0]) ? 2 : 3;

    private static (Grid First, Grid Second)? NearestGridIntersection(List<Grid> grids, double x, double y)
    {
        var candidates = new List<(Grid First, Grid Second, double Distance)>();
        for (int i = 0; i < grids.Count; i++)
            for (int j = i + 1; j < grids.Count; j++)
                if (grids[i].Intersection(grids[j]) is var (ix, iy))
                    candidates.Add((grids[i], grids[j], Math.Sqrt((x - ix) * (x - ix) + (y - iy) * (y - iy))));
        if (candidates.Count == 0) return null;
        double nearest = candidates.Min(c => c.Distance);
        var chosen = candidates.First(c => c.Distance <= nearest + NearestTieTolerance);
        return (chosen.First, chosen.Second);
    }

    private static string GridMark(Grid grid, (double X, double Y) origin, double[] box, double unitsPerFoot,
        TfBlockDeserializer.UnitFormatOption format)
    {
        if (grid.TouchesPlan(box)) return grid.Name;
        double accuracy = format.Accuracy;
        double value = Math.Round(grid.SignedDistance(origin.X, origin.Y) * unitsPerFoot / accuracy, MidpointRounding.AwayFromZero) * accuracy;
        if (value == 0) return grid.Name;
        int decimals = Math.Max(0, (int)Math.Ceiling(-Math.Log10(accuracy) - 1e-9));
        string number = value.ToString("F" + decimals, CultureInfo.InvariantCulture);
        if (format.SuppressTrailingZeros != 0 && number.Contains('.')) number = number.TrimEnd('0').TrimEnd('.');
        return grid.Name + "(" + number + ")";
    }
}
