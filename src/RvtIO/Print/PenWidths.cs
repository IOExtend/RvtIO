using RvtIO.Model;

namespace RvtIO.Print;

/// <summary>Line weights of the document's Object Styles: which pen a category draws with and how wide a pen
/// prints at a view's scale, the way Revit's Line Weights dialog defines them (see rvt-prt pens.py).</summary>
internal static class PenWidths
{
    private const double PtPerFoot = 864.0;
    private const double MinPt = 0.24;
    private const double MmPerPt = 25.4 / 72.0;

    /// <summary>Width in millimetres a category's cut (isCut) or projection lines print at for a view scale
    /// denominator (100 for 1:100). False when the category has no style for that kind or the table did not decode.</summary>
    internal static bool TryGetWidth(RvtDocument document, int categoryId, bool isCut, int scaleDenominator, out double widthMillimetres)
    {
        widthMillimetres = 0;
        var info = document.RawData;
        if (info.GraphicsStylePens == null || info.PenWidthTable == null) return false;
        if (scaleDenominator <= 0) return false;
        int kind = isCut ? 2 : 1;
        int pen = 0;
        bool found = false;
        foreach (var style in info.GraphicsStylePens.Values)
        {
            if (style.CategoryId == categoryId && style.OwnerId == -1 && style.GStyleType == kind)
            {
                pen = style.PenNumber;
                found = true;
                break;
            }
        }
        if (!found) return false;
        return TryPenWidth(info.PenWidthTable.Value, pen, scaleDenominator, out widthMillimetres);
    }

    /// <summary>Width in millimetres a model pen prints at for a view scale denominator. False when the pen is out
    /// of range or the table did not decode.</summary>
    internal static bool TryPenWidth((Dictionary<int, double[]> ModelRows, double[] DraftPens) table, int pen, int scaleDenominator, out double widthMillimetres)
    {
        widthMillimetres = 0;
        if (pen < 1 || scaleDenominator <= 0 || table.ModelRows.Count == 0) return false;
        int nearest = 0;
        double best = double.MaxValue;
        foreach (int inverted in table.ModelRows.Keys)
        {
            double rel = Math.Abs(inverted - scaleDenominator) / (double)Math.Max(inverted, 1);
            if (rel < best) { best = rel; nearest = inverted; }
        }
        if (nearest == 0) return false;
        double[] row = table.ModelRows[nearest];
        if (pen > row.Length) return false;
        double pt = Math.Max(row[pen - 1] * PtPerFoot, MinPt);
        if (!double.IsFinite(pt) || pt <= 0) return false;
        widthMillimetres = pt * MmPerPt;
        return true;
    }

    /// <summary>Width in millimetres an annotation pen prints at (the draft pens of the table). False when out of range.</summary>
    internal static bool TryAnnotationWidth(RvtDocument document, int pen, out double widthMillimetres)
    {
        widthMillimetres = 0;
        var table = document.RawData.PenWidthTable;
        if (table == null || pen < 1 || pen > table.Value.DraftPens.Length) return false;
        double pt = Math.Max(table.Value.DraftPens[pen - 1] * PtPerFoot, MinPt);
        if (!double.IsFinite(pt) || pt <= 0) return false;
        widthMillimetres = pt * MmPerPt;
        return true;
    }
}
