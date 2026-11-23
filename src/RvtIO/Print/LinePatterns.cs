using RvtIO.BuiltIns;
using RvtIO.Model;

namespace RvtIO.Print;

/// <summary>Line patterns of the document's Object Styles: the dash lengths a category draws with,
/// in PDF points, the way rvt-prt pens.py maps LinePatternElem segments (paper feet, 864 points
/// per foot; a dot prints as a 0.1 pt dash).</summary>
internal static class LinePatterns
{
    private const double PtPerFoot = 864.0;

    /// <summary>Dash lengths in PDF points for the center lines of the document's grids: the grid
    /// type's center-segment category (GridAttributes) names a projection graphics style whose line
    /// pattern supplies the dashes. False when any link did not decode.</summary>
    internal static bool TryGetGridDashes(RvtDocument document, out double[] dashes)
    {
        dashes = [];
        var info = document.RawData;
        if (info.GraphicsStylePens == null || info.LinePatterns == null || info.GridCenterCategories == null)
            return false;
        int? centerCategory = null;
        foreach (var element in document.Elements)
        {
            if (element.CategoryId != (int)BuiltInCategory.OST_Grids) continue;
            if (element.TypeId > 0 && info.GridCenterCategories.TryGetValue(element.TypeId, out int category))
            {
                centerCategory = category;
                break;
            }
        }
        return centerCategory is int categoryId && TryGetDashes(document, categoryId, out dashes);
    }

    internal static bool TryGetDashes(RvtDocument document, int categoryId, out double[] dashes)
    {
        dashes = [];
        var info = document.RawData;
        if (info.GraphicsStylePens == null || info.LinePatterns == null) return false;
        int patternId = 0;
        bool found = false;
        foreach (var style in info.GraphicsStylePens.Values)
        {
            if (style.CategoryId == categoryId && style.OwnerId == -1 && style.GStyleType == 1)
            {
                patternId = style.LinePatternId;
                found = true;
                break;
            }
        }
        return found && TryGetPatternDashes(document, patternId, out dashes);
    }

    internal static bool TryGetPatternDashes(RvtDocument document, int patternId, out double[] dashes)
    {
        dashes = [];
        if (patternId <= 0 || document.RawData.LinePatterns?.TryGetValue(patternId, out var pattern) != true)
            return false;
        var points = new List<double>();
        var ink = new List<bool>();
        foreach (var seg in pattern.Segs)
        {
            points.Add(Math.Max(seg.Data * PtPerFoot, 0.1));
            ink.Add(seg.Type != 1);
        }
        if (points.Count == 0) return false;
        if (!ink[0])
        {
            points.Add(points[0]);
            points.RemoveAt(0);
        }
        dashes = [.. points.Select(v => Math.Round(v, 3))];
        return true;
    }

    internal static double[] FitEndpointDashes(double lengthPoints, double[] dashes)
    {
        if (dashes.Length < 2 || dashes.Length % 2 != 0 || !double.IsFinite(lengthPoints)
            || dashes.Any(value => !(value > 0 && double.IsFinite(value)))) return dashes;
        double period = dashes.Sum();
        double repeats = Math.Floor(lengthPoints / period);
        if (repeats < 1) return [];
        if (repeats * dashes.Length + 2 > 4096) return dashes;

        // Keep the interior sequence and split the remaining length equally between the terminal dashes.
        double terminal = (lengthPoints - repeats * period + dashes[0]) / 2;
        var fitted = new double[(int)repeats * dashes.Length + 2];
        for (int i = 0; i < fitted.Length - 2; i += dashes.Length)
            Array.Copy(dashes, 0, fitted, i, dashes.Length);
        fitted[0] = fitted[^2] = terminal;
        fitted[^1] = dashes[1];
        return fitted;
    }
}
