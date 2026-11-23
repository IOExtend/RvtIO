namespace RvtIO.Model;

/// <summary>The crop frame of a view's viewer (the crop region Revit prints), in view feet: the origin and the
/// right/up axes of the view, and the crop rectangle. A sheet point is the viewport offset plus the view scale
/// times the view coordinates; the crop rectangle is what the viewport draws, at the view's own scale.</summary>
/// <param name="OriginX">The X of the view origin in model feet.</param>
/// <param name="OriginY">The Y of the view origin in model feet.</param>
/// <param name="OriginZ">The Z of the view origin in model feet.</param>
/// <param name="Right">The right axis of the view, a unit vector.</param>
/// <param name="Up">The up axis of the view, a unit vector.</param>
/// <param name="Look">The looking direction of the view, a unit vector.</param>
/// <param name="X0">The left side of the crop rectangle in view feet.</param>
/// <param name="Y0">The bottom side of the crop rectangle in view feet.</param>
/// <param name="X1">The right side of the crop rectangle in view feet.</param>
/// <param name="Y1">The top side of the crop rectangle in view feet.</param>
/// <param name="Active">False when the crop region is off (Revit prints the whole model) or the rectangle runs
/// past ±10000 feet: the viewport then draws without a crop.</param>
public sealed record ViewCropFrame(
    double OriginX,
    double OriginY,
    double OriginZ,
    double[] Right,
    double[] Up,
    double[] Look,
    double X0,
    double Y0,
    double X1,
    double Y1,
    bool Active);

internal sealed record ViewModelBounds(double[] Origin, double[][] Axes, double[] Offsets, bool[] Active);

internal sealed record ViewSolarStudy(double[] Outline, int SizePercent, int GroundLevelId, int AnnotationId,
    uint Start, uint End, int Interval, double ActiveFrame, int ShadowIntensity = 0,
    bool ShowSurfacePatterns = false, bool ShowEdges = false);
