using RvtIO.Model;
using RvtIO.Pdf;

namespace RvtIO.Print;

/// <summary>
/// Prints views of both kinds to one PDF document: a plan at its level and scale, a compass elevation from the projected
/// model faces. One page per view, in the order given.
/// </summary>
public static class ViewPrinter
{
    /// <summary>Prints several views, one page each, to one PDF document.</summary>
    /// <param name="document">The opened document.</param>
    /// <param name="viewIds">The element ids of the views; each must be a printable plan (<see cref="PlanPrinter.Views"/>)
    /// or a printable elevation (<see cref="ElevationPrinter.Views"/>).</param>
    /// <param name="options">The paper and margin; null uses the defaults.</param>
    /// <exception cref="InvalidOperationException">A view id is neither a printable plan nor a printable elevation.</exception>
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
            throw new InvalidOperationException(
                $"View {viewId} is not a printable plan or elevation (see PlanPrinter.Views and ElevationPrinter.Views).");
        }
        if (pdf.Pages.Count == 0) throw new InvalidOperationException("No view was given to print.");
        return pdf;
    }
}
