using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Pdf;

namespace RvtIO.Print;

public static partial class SitePrinter
{
    // The shared stock RPC plan symbol, measured in model axes on the Revit export.
    private static readonly double[] RpcSpokeAngles =
        [15, 30.75, 61, 71.75, 83.25, 104.75, 110, 160.5, 184.25, 190.25, 225, 265.75, 275, 302.75, 329.75, 342.25];

    private static double? RpcPlanRadius(RvtDocument document, Element element)
    {
        var type = document.GetElement(element.TypeId);
        if (type?.LookupParameter(BuiltInParameter.RENDER_RPC_FILENAME)?.Value is not string { Length: > 0 })
            return null;
        var height = element.LookupParameter(BuiltInParameter.RENDER_PLANT_HEIGHT)?.Value
            ?? type.LookupParameter(BuiltInParameter.RENDER_PLANT_HEIGHT)?.Value;
        // The common plan mark scales with the saved RPC height, independently of the mesh bounds.
        return height is double feet && feet > 0 && double.IsFinite(feet) ? feet * 0.275 * FeetToMillimetres : null;
    }

    private static void DrawRpcPlant(PdfPage page, double x, double y, double radius, ViewCropFrame? basis,
        double width, PdfColor color)
    {
        const int perimeterSegments = 64;
        var previous = (X: x + radius, Y: y);
        for (int i = 1; i <= perimeterSegments; i++)
        {
            double angle = i * (2 * Math.PI / perimeterSegments);
            var point = (X: x + Math.Cos(angle) * radius, Y: y + Math.Sin(angle) * radius);
            page.DrawLine(previous.X, previous.Y, point.X, point.Y, width, color);
            previous = point;
        }
        foreach (double angle in RpcSpokeAngles)
        {
            double radians = angle * Math.PI / 180;
            double dx = Math.Cos(radians), dy = Math.Sin(radians);
            double projectedX = basis == null ? dx : dx * basis.Right[0] + dy * basis.Right[1];
            double projectedY = basis == null ? dy : dx * basis.Up[0] + dy * basis.Up[1];
            page.DrawLine(x, y, x + projectedX * radius, y + projectedY * radius, width, color);
        }
    }
}
