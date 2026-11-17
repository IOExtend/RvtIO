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
        if (centerCategory == null) return false;
        int patternId = 0;
        bool found = false;
        foreach (var style in info.GraphicsStylePens.Values)
        {
            if (style.CategoryId == centerCategory && style.OwnerId == -1 && style.GStyleType == 1)
            {
                patternId = style.LinePatternId;
                found = true;
                break;
            }
        }
        if (!found || patternId <= 0 || !info.LinePatterns.TryGetValue(patternId, out var pattern))
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
}
