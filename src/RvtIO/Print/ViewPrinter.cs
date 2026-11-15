using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Pdf;

namespace RvtIO.Print;

/// <summary>
/// Prints views of every kind to one PDF document: a plan at its level and scale, a compass elevation or a section from
/// the projected model faces, a 3D view as an isometric massing and a rendering as the image Revit stored for it. One
/// page per view, in the order given.
/// </summary>
public static class ViewPrinter
{
    /// <summary>Prints several views, one page each, to one PDF document.</summary>
    /// <param name="document">The opened document.</param>
    /// <param name="viewIds">The element ids of the views; each must be a printable plan (<see cref="PlanPrinter.Views"/>),
    /// a printable elevation (<see cref="ElevationPrinter.Views"/>), a 3D view or a rendering view.</param>
    /// <param name="options">The paper and margin; null uses the defaults.</param>
    /// <exception cref="InvalidOperationException">A view id is none of those.</exception>
    public static PdfDocument Render(RvtDocument document, IReadOnlyList<int> viewIds, PlanPrintOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(viewIds);
        options ??= new PlanPrintOptions();
        var plans = new Dictionary<int, PlanView>();
        foreach (var view in PlanPrinter.Views(document)) plans[view.ViewId] = view;
        var elevations = new Dictionary<int, ElevationView>();
        foreach (var view in ElevationPrinter.Views(document)) elevations[view.ViewId] = view;
        var pdf = new PdfDocument { Font = PdfFonts.TryFind(out _), Compress = true };
        foreach (int viewId in viewIds)
        {
            if (plans.TryGetValue(viewId, out var plan))
            {
                PlanPrinter.AddPlanPage(pdf, document, plan.LevelId, plan.Scale, plan.Name, options);
                continue;
            }
            if (elevations.TryGetValue(viewId, out var elevation))
            {
                ElevationPrinter.AddElevationPage(pdf, document, elevation, options);
                continue;
            }
            Element? view = document.GetElement(viewId);
            if (view?.RootClassName == "DBViewRendering")
            {
                AddRenderingPage(pdf, document, view, options);
                continue;
            }
            if (view?.RootClassName == "DBView3d")
            {
                AddThreeDimensionalPage(pdf, document, view, options);
                continue;
            }
            throw new InvalidOperationException(
                $"View {viewId} is not a printable plan, elevation, 3D view or rendering "
                + "(see PlanPrinter.Views and ElevationPrinter.Views).");
        }
        if (pdf.Pages.Count == 0) throw new InvalidOperationException("No view was given to print.");
        return pdf;
    }

    /// <summary>The rendering view and the 3D views of a document, in name order: the views this printer prints beyond
    /// the plans and the elevations.</summary>
    /// <param name="document">The opened document.</param>
    public static IReadOnlyList<Element> OtherViews(RvtDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Elements
            .Where(element => element.RootClassName is "DBViewRendering" or "DBView3d")
            .OrderBy(element => element.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>A rendering view prints its image, fitted on a page of its own; when the image cannot be read, the
    /// isometric massing of the model stands in for it.</summary>
    private static void AddRenderingPage(PdfDocument pdf, RvtDocument document, Element view, PlanPrintOptions options)
    {
        PdfJpegImage? image = document.SourcePath is { Length: > 0 } path
            ? FindRendering(path, document, view.Id)
            : null;
        if (image is null)
        {
            AddThreeDimensionalPage(pdf, document, view, options);
            return;
        }
        double margin = options.MarginMillimetres;
        double aspect = (double)image.Width / image.Height;
        PdfPageSize paper = PdfPageSize.A3.Landscape;
        double width = paper.WidthMillimetres - 2 * margin;
        double height = width / aspect;
        if (height > paper.HeightMillimetres - 2 * margin)
        {
            height = paper.HeightMillimetres - 2 * margin;
            width = height * aspect;
        }
        PdfPage page = pdf.AddPage(paper);
        page.DrawImage(image, (paper.WidthMillimetres - width) / 2, (paper.HeightMillimetres - height) / 2, width,
            height);
        if (pdf.Font != null)
            page.DrawText(view.Name ?? "", margin, paper.HeightMillimetres - margin - 6, 4);
    }

    /// <summary>The image a rendering view stores: the image symbol the view owns carries its type.</summary>
    private static PdfJpegImage? FindRendering(string path, RvtDocument document, int viewId)
    {
        foreach (var element in document.Elements)
        {
            if (element.OwnerViewId != viewId || element.RootClassName != "ImageSymbol") continue;
            if (element.TypeId <= 0) continue;
            var image = RvtRaster.FindJpeg(path, element.TypeId);
            if (image != null) return image;
        }
        return null;
    }

    /// <summary>A 3D view prints the isometric massing of the model.</summary>
    private static void AddThreeDimensionalPage(PdfDocument pdf, RvtDocument document, Element view,
        PlanPrintOptions options)
    {
        ElevationPrinter.AddElevationPage(pdf, document,
            new ElevationView(view.Id, view.Name ?? "", "iso", view.LookupParameter(BuiltInParameter.VIEW_SCALE)?.Value
                is int scale && scale > 1 ? scale : 200), options);
    }
}
