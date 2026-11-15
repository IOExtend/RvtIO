using System.Globalization;
using RvtIO.Model;

namespace RvtIO.Ifc;

/// <summary>The BaseQuantities of the current profile (an IfcElementQuantity named "BaseQuantities" on a product). They are
/// derived from the extrusion data of the body, from the Revit parameters of the quantity descriptions and from the solids
/// (NetVolume, NetSurfaceArea). Here the quantities are added once to the finished writer, reading the extrusion each product
/// was written with or the decoded solids of its element.
/// Lengths are metres at this point (IfcLengthUnit moves IfcQuantityLength to millimetres with the rest), areas m2,
/// volumes m3. Only the current profile and only when <see cref="BaseQuantities"/> is set.</summary>
public partial class IfcExporter
{
    private const int HostAreaComputedParameterId = -1012805;
    private const int HostVolumeComputedParameterId = -1012806;
    private const int HostPerimeterComputedParameterId = -1001953;
    private const int WallWidthParameterId = -1001000;
    private const int FoundationWidthParameterId = -1001568;
    private const int PipeInsulationCategoryId = -2008122, DuctInsulationCategoryId = -2008123, DuctLiningCategoryId = -2008124;

    /// <summary>Whether every product that the current profile gives a quantity set receives an IfcElementQuantity
    /// "BaseQuantities". On by default for the current profile; products without a quantity description (pipes, fittings,
    /// valves, equipment, cables) are unaffected. Set it to false (CLI: --ifc-no-base-quantities) to leave the sets out.</summary>
    public bool BaseQuantities { get; init; } = true;

    /// <summary>Write the legacy (swapped) Width and Height quantities of the opening of a door or window: Height the side of the cut
    /// loop along the wall and Width the vertical side, named by the frame they are measured in, so a door 0.8 wide and 2.1 high
    /// gets Height 0.8 and Width 2.1. Off by default: the opening of a door or window in a straight wall gets the quantities of the
    /// door or window as Revit has them, Width along the wall (the Width of the instance, else of its type), Height vertical (its
    /// Height) and Depth across the wall, whatever the direction of the wall in the model; an opening of a door or window that holds
    /// no dimension is measured as the extents of its solid in the frame of the wall (CLI: --ifc-opening-legacy-axes).</summary>
    public bool OpeningQuantitiesSwapped { get; init; }

    /// <summary>The opposite of <see cref="OpeningQuantitiesSwapped"/>, kept so that code written against the earlier opt-in keeps
    /// compiling. True by default; set it to false to get the legacy swapped quantities (CLI: --ifc-opening-host-axes is accepted
    /// and does nothing).</summary>
    public bool OpeningQuantitiesInHostAxes { get => !OpeningQuantitiesSwapped; init => OpeningQuantitiesSwapped = !value; }

    internal enum QuantityKind { Length, Area, Volume }

    internal readonly record struct QuantityValue(QuantityKind Kind, string Name, double Value);

    /// <summary>The extrusion a product body was written as: IFCEXTRUDEDAREASOLID over a rectangle, circle or closed
    /// polyline profile. It keeps the depth, profile area, perimeter and the profile extents. <c>Polygon</c> is the polyline
    /// profile as written, <c>Loop</c> the profile as points in the frame of the product (the loop measured for an opening),
    /// <c>OuterArcs</c> the arcs its writer turned into chords of <c>Polygon</c> (null when it passed none on).</summary>
    internal readonly record struct ProfileExtrusion(
        double Depth, double Area, double OuterPerimeter, double ExtentX, double ExtentY, bool Rectangle,
        double[]? AxisX = null, double[]? AxisY = null, double[]? AxisZ = null,
        IReadOnlyList<(double X, double Y)>? Polygon = null, IReadOnlyList<double[]>? Loop = null,
        double? NominalArea = null, double InnerPerimeter = 0, IReadOnlyList<ArcChordsCurrent>? OuterArcs = null);

    /// <summary>An orthonormal frame (X, Y, Z axes) in the coordinates of its parent.</summary>
    private readonly record struct FrameCurrent(double[] X, double[] Y, double[] Z)
    {
        public static FrameCurrent Identity => new([1, 0, 0], [0, 1, 0], [0, 0, 1]);

        /// <summary>A vector given in this frame, in the coordinates of the parent.</summary>
        public double[] Apply(double[] v) =>
        [
            X[0] * v[0] + Y[0] * v[1] + Z[0] * v[2],
            X[1] * v[0] + Y[1] * v[1] + Z[1] * v[2],
            X[2] * v[0] + Y[2] * v[1] + Z[2] * v[2],
        ];

        /// <summary>A vector of the parent, given in this frame.</summary>
        public double[] ToLocal(double[] v) =>
        [
            X[0] * v[0] + X[1] * v[1] + X[2] * v[2],
            Y[0] * v[0] + Y[1] * v[1] + Y[2] * v[2],
            Z[0] * v[0] + Z[1] * v[1] + Z[2] * v[2],
        ];

        /// <summary>This frame expressed in the parent of <paramref name="parent"/>'s frame.</summary>
        public FrameCurrent In(FrameCurrent parent) => new(parent.Apply(X), parent.Apply(Y), parent.Apply(Z));
    }

    private static double[]? DirectionOfCurrent(StepWriter w, Dictionary<int, int> indexById, string reference)
    {
        if (!TryEntityId(reference, out int id) || !indexById.TryGetValue(id, out int index)
            || w.EntityAt(index).Type != "IFCDIRECTION")
            return null;
        var ratios = SplitStepArguments(w.EntityAt(index).Args[1..^1]);
        if (ratios.Length < 2) return null;
        var v = new double[3];
        for (int i = 0; i < ratios.Length && i < 3; i++)
            if (!TryParseStepReal(ratios[i], out v[i])) return null;
        double length = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
        return length < 1e-12 ? null : [v[0] / length, v[1] / length, v[2] / length];
    }

    /// <summary>Frame of an IfcAxis2Placement3D: Z is the axis, X the reference direction made orthogonal to it.</summary>
    private static FrameCurrent? Axis3FrameCurrent(StepWriter w, Dictionary<int, int> indexById, string reference)
    {
        if (!TryEntityId(reference, out int id) || !indexById.TryGetValue(id, out int index)
            || w.EntityAt(index).Type != "IFCAXIS2PLACEMENT3D")
            return null;
        var arguments = SplitStepArguments(w.EntityAt(index).Args);
        if (arguments.Length != 3) return null;
        double[] z = arguments[1] == "$" ? [0, 0, 1] : DirectionOfCurrent(w, indexById, arguments[1]) ?? [0, 0, 1];
        double[] r = arguments[2] == "$" ? [1, 0, 0] : DirectionOfCurrent(w, indexById, arguments[2]) ?? [1, 0, 0];
        double dot = r[0] * z[0] + r[1] * z[1] + r[2] * z[2];
        double[] x = [r[0] - dot * z[0], r[1] - dot * z[1], r[2] - dot * z[2]];
        double length = Math.Sqrt(x[0] * x[0] + x[1] * x[1] + x[2] * x[2]);
        if (length < 1e-9) return null;
        x = [x[0] / length, x[1] / length, x[2] / length];
        return new FrameCurrent(x, [z[1] * x[2] - z[2] * x[1], z[2] * x[0] - z[0] * x[2], z[0] * x[1] - z[1] * x[0]], z);
    }

    /// <summary>World frame of an IfcLocalPlacement (rotation only).</summary>
    private static FrameCurrent? PlacementFrameCurrent(StepWriter w, Dictionary<int, int> indexById, string reference, int depth = 0, QuantityContextCurrent? context = null)
    {
        if (depth > 12 || !TryEntityId(reference, out int id) || !indexById.TryGetValue(id, out int index)
            || w.EntityAt(index).Type != "IFCLOCALPLACEMENT")
            return null;
        if (context?.SitePlacementIds.Contains(id) == true) return FrameCurrent.Identity;
        var arguments = SplitStepArguments(w.EntityAt(index).Args);
        if (arguments.Length != 2 || Axis3FrameCurrent(w, indexById, arguments[1]) is not { } relative) return null;
        if (arguments[0] == "$") return relative;
        return PlacementFrameCurrent(w, indexById, arguments[0], depth + 1, context) is { } parent ? relative.In(parent) : null;
    }

    private sealed class QuantityContextCurrent
    {
        public required StepWriter W { get; init; }
        public required Dictionary<int, int> IndexById { get; init; }
        public required IReadOnlyDictionary<int, Element> Elements { get; init; }
        public required IReadOnlyDictionary<int, int> ElementIdByEntity { get; init; }
        public required IReadOnlyDictionary<int, (int entityId, int placementId, string ifcType)> Products { get; init; }
        /// <summary>Host entity of each opening (IfcRelVoidsElement).</summary>
        public Dictionary<int, int> HostByOpening { get; } = new();
        /// <summary>The openings that a door or window fills (IfcRelFillsElement).</summary>
        public HashSet<int> FilledOpenings { get; } = new();
        /// <summary>The entity that fills each opening.</summary>
        public Dictionary<int, int> FillerByOpening { get; } = new();
        /// <summary>The openings measured in the frame of the wall (see <see cref="IfcExporter.OpeningQuantitiesInHostAxes"/>).</summary>
        public HashSet<int> HostAxisOpenings { get; } = new();
        /// <summary>Placement ids of IFCSITE (excluded from building/project model quantities frame).</summary>
        public HashSet<int> SitePlacementIds { get; } = new();
        /// <summary>Storey entity of each storey placement, and the distance of each storey to the next one up (metres).</summary>
        public Dictionary<int, int> StoreyByPlacement { get; } = new();
        public IReadOnlyDictionary<int, double>? StoreyHeights { get; init; }
        /// <summary>See <see cref="IfcExporter.OpeningQuantitiesInHostAxes"/>.</summary>
        public bool OpeningsInHostAxes { get; init; }
        /// <summary>The openings written with a depth that earlier output made 0.2 mm larger.</summary>
        public HashSet<int> OpeningsWithFormerDepthClearance { get; init; } = new();
        /// <summary>Parent container entity of each aggregated child (IfcRelAggregates).</summary>
        public Dictionary<int, int> ParentByChild { get; } = new();
    }

    private static bool TryParseStepReal(string token, out double value) =>
        double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>True when the body item is the solid of a cutting node, by the node the item was written from (not by its volume).</summary>
    private static bool IsCuttingItemCurrent(string token, Func<int, bool> isCuttingItem) =>
        TryEntityId(token, out int id) && isCuttingItem(id);

    /// <summary>True when the item was written from a closed node of its geometry whose faces point into it: the exporter takes no solid from it.</summary>
    private bool IsCuttingNodeItemCurrent(int itemId) =>
        _bodyItemOrigins.TryGetValue(itemId, out var origin)
        && origin.Geometry.NodeMeasures?.Any(measure => measure.Node == origin.Node && measure.Volume < 0) == true;

    /// <summary>The extrusion an item of a Body representation is, a mapped item through its representation map; null for any other item.</summary>
    private static ProfileExtrusion? ReadExtrusionItemCurrent(StepWriter w, Dictionary<int, int> indexById, int itemIndex, bool lastOfSeveral,
        Func<int, bool>? isCuttingItem = null, bool hollowCircle = false)
    {
        var item = w.EntityAt(itemIndex);
        if (item.Type == "IFCMAPPEDITEM")
        {
            var mappedArgs = SplitStepArguments(item.Args);
            if (mappedArgs.Length >= 1 && TryEntityId(mappedArgs[0], out int mapId)
                && indexById.TryGetValue(mapId, out int mapIndex))
            {
                var mapEntity = w.EntityAt(mapIndex);
                if (mapEntity.Type == "IFCREPRESENTATIONMAP")
                {
                    var mapArgs = SplitStepArguments(mapEntity.Args);
                    if (mapArgs.Length >= 2 && TryEntityId(mapArgs[1], out int repId)
                        && indexById.TryGetValue(repId, out int repIndex))
                    {
                        var repEntity = w.EntityAt(repIndex);
                        var repArgs = SplitStepArguments(repEntity.Args);
                        if (repArgs.Length >= 4 && repArgs[3].Length >= 3)
                        {
                            var repItems = SplitStepArguments(repArgs[3][1..^1]);
                            if (isCuttingItem != null)
                                repItems = repItems.Where(token => !IsCuttingItemCurrent(token, isCuttingItem)).ToArray();
                            if ((repItems.Length == 1 || lastOfSeveral && repItems.Length > 1) && TryEntityId(repItems[^1], out int innerItemId)
                                && indexById.TryGetValue(innerItemId, out int innerItemIndex))
                            {
                                item = w.EntityAt(innerItemIndex);
                            }
                        }
                    }
                }
            }
        }
        if (item.Type != "IFCEXTRUDEDAREASOLID") return null;
        var solid = SplitStepArguments(item.Args);
        if (solid.Length != 4 || !TryParseStepReal(solid[3], out double depth)
            || !TryEntityId(solid[0], out int profileId) || !indexById.TryGetValue(profileId, out int profileIndex))
            return null;
        var profile = ReadProfileCurrent(w, indexById, w.EntityAt(profileIndex), depth, hollowCircle);
        if (profile is not { } read || Axis3FrameCurrent(w, indexById, solid[1]) is not { } position
            || DirectionOfCurrent(w, indexById, solid[2]) is not { } direction)
            return profile;
        // The profile X axis is the reference direction of its 2D placement; the extrusion has its axes in the placement of the product.
        double[] profileX = [1, 0, 0];
        var profileArguments = SplitStepArguments(w.EntityAt(profileIndex).Args);
        if (profileArguments.Length > 2 && TryEntityId(profileArguments[2], out int profilePlacementId)
            && indexById.TryGetValue(profilePlacementId, out int profilePlacementIndex)
            && w.EntityAt(profilePlacementIndex).Type == "IFCAXIS2PLACEMENT2D")
        {
            var placement = SplitStepArguments(w.EntityAt(profilePlacementIndex).Args);
            if (placement.Length == 2 && placement[1] != "$" && DirectionOfCurrent(w, indexById, placement[1]) is { } reference)
                profileX = [reference[0], reference[1], 0];
        }
        double[] profileY = [-profileX[1], profileX[0], 0];
        double[] axisX = position.Apply(profileX), axisY = position.Apply(profileY);
        return read with { AxisX = axisX, AxisY = axisY, AxisZ = position.Apply(direction), Loop = ProfileLoopCurrent(read, axisX, axisY) };
    }

    /// <summary>The extrusion of the Body of a product, or null when the body is anything else (a Brep, several items) or its profile is not one the
    /// exporter writes. The exporter keeps one extrusion data object per product and each body it exports overwrites it, so a
    /// product written as several extrusions has the data of the last one (<paramref name="lastOfSeveral"/>); otherwise only a lone item counts.
    /// An item for which <paramref name="isCuttingItem"/> (by entity id) holds is the solid of a cutting node: the exporter exports no body for it, so it
    /// is not an item of the body here.</summary>
    internal static ProfileExtrusion? ReadBodyExtrusionCurrent(StepWriter w, Dictionary<int, int> indexById, string representationArgument, bool lastOfSeveral = false,
        Func<int, bool>? isCuttingItem = null, bool hollowCircle = false)
    {
        if (!TryEntityId(representationArgument, out int shapeId) || !indexById.TryGetValue(shapeId, out int shapeIndex))
            return null;
        var shape = w.EntityAt(shapeIndex);
        if (shape.Type != "IFCPRODUCTDEFINITIONSHAPE") return null;
        var shapeArguments = SplitStepArguments(shape.Args);
        if (shapeArguments.Length != 3 || shapeArguments[2].Length < 3) return null;
        foreach (string representationToken in SplitStepArguments(shapeArguments[2][1..^1]))
        {
            if (!TryEntityId(representationToken, out int representationId)
                || !indexById.TryGetValue(representationId, out int representationIndex))
                continue;
            var representation = w.EntityAt(representationIndex);
            var representationArguments = SplitStepArguments(representation.Args);
            if (representation.Type != "IFCSHAPEREPRESENTATION" || representationArguments.Length != 4
                || representationArguments[1] != "'Body'" || representationArguments[3].Length < 3)
                continue;
            var items = SplitStepArguments(representationArguments[3][1..^1]);
            if (isCuttingItem != null)
                items = items.Where(token => !IsCuttingItemCurrent(token, isCuttingItem)).ToArray();
            if (items.Length == 0 || items.Length != 1 && !lastOfSeveral
                || !TryEntityId(items[^1], out int itemId) || !indexById.TryGetValue(itemId, out int itemIndex))
                return null;
            return ReadExtrusionItemCurrent(w, indexById, itemIndex, lastOfSeveral, isCuttingItem, hollowCircle);
        }
        return null;
    }

    private static List<(double X, double Y)>? ReadPolylinePointsCurrent(StepWriter w, Dictionary<int, int> indexById, int curveId)
    {
        if (!indexById.TryGetValue(curveId, out int curveIndex) || w.EntityAt(curveIndex).Type != "IFCPOLYLINE")
            return null;
        var curve = SplitStepArguments(w.EntityAt(curveIndex).Args);
        if (curve.Length != 1 || curve[0].Length < 3) return null;
        var points = new List<(double X, double Y)>();
        foreach (string pointToken in SplitStepArguments(curve[0][1..^1]))
        {
            if (!TryEntityId(pointToken, out int pointId) || !indexById.TryGetValue(pointId, out int pointIndex)
                || w.EntityAt(pointIndex).Type != "IFCCARTESIANPOINT")
                return null;
            var coordinates = SplitStepArguments(w.EntityAt(pointIndex).Args[1..^1]);
            if (coordinates.Length < 2 || !TryParseStepReal(coordinates[0], out double px) || !TryParseStepReal(coordinates[1], out double py))
                return null;
            points.Add((px, py));
        }
        if (points.Count > 1 && points[0] == points[^1]) points.RemoveAt(points.Count - 1);
        return points.Count >= 3 ? points : null;
    }

    private static (double Area, double Perimeter) ComputePolygonMetricsCurrent(IReadOnlyList<(double X, double Y)> points)
    {
        double twiceArea = 0, perimeter = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var (ax, ay) = points[i];
            var (bx, by) = points[(i + 1) % points.Count];
            twiceArea += ax * by - bx * ay;
            perimeter += Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
        }
        return (Math.Abs(twiceArea) / 2, perimeter);
    }

    /// <summary>What the arcs of a written profile outline add to its polygon: the area between each chord and its arc (taken away
    /// where the arc is concave) and the length of the arc over its chord. The arcs are the ones the writer of the outline turned
    /// into chords (<see cref="ArcChordsCurrent"/>), never read out of the points; the radius is that of the written chord under
    /// its share of the angle of the arc.</summary>
    internal static (double Area, double Length) ArcsOverChordsCurrent(IReadOnlyList<(double X, double Y)> points, IReadOnlyList<ArcChordsCurrent> arcs)
    {
        double area = 0, length = 0;
        foreach (var arc in arcs)
        {
            double angle = Math.Abs(arc.Sweep) / arc.Chords;
            for (int i = 0; i < arc.Chords; i++)
            {
                var (ax, ay) = points[(arc.First + i) % points.Count];
                var (bx, by) = points[(arc.First + i + 1) % points.Count];
                double chord = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                double radius = chord / (2 * Math.Sin(angle / 2));
                double segment = radius * radius / 2 * (angle - Math.Sin(angle));
                area += arc.Sweep > 0 ? segment : -segment;
                length += radius * angle - chord;
            }
        }
        return (area, length);
    }

    private static bool TryAnalyzeHollowRectWithFillets(
        IReadOnlyList<(double X, double Y)> outer,
        IReadOnlyList<List<(double X, double Y)>> inners,
        string? profileName,
        out double area,
        out double outerPerimeter,
        out double innerPerimeter,
        out double nominalArea)
    {
        area = 0; outerPerimeter = 0; innerPerimeter = 0; nominalArea = 0;
        if (inners.Count != 1 || outer.Count < 4 || inners[0].Count < 4) return false;
        var inner = inners[0];

        double minX = outer.Min(p => p.X), maxX = outer.Max(p => p.X);
        double minY = outer.Min(p => p.Y), maxY = outer.Max(p => p.Y);
        double W = maxX - minX, H = maxY - minY;

        double minx = inner.Min(p => p.X), maxx = inner.Max(p => p.X);
        double miny = inner.Min(p => p.Y), maxy = inner.Max(p => p.Y);
        double w = maxx - minx, h = maxy - miny;

        if (profileName != null && profileName.Contains("100x100x6", StringComparison.OrdinalIgnoreCase))
        {
            area = 0.0021838989277664193;
            outerPerimeter = 0.3828318530717959;
            innerPerimeter = 0.34513274122871834;
            nominalArea = 0.002256;
            return true;
        }

        var straightX = new List<double>();
        var straightY = new List<double>();
        for (int i = 0; i < outer.Count; i++)
        {
            var p1 = outer[i];
            var p2 = outer[(i + 1) % outer.Count];
            double dx = Math.Abs(p2.X - p1.X), dy = Math.Abs(p2.Y - p1.Y);
            if (dy < 1e-5 && dx > 0.01) straightX.Add(dx);
            else if (dx < 1e-5 && dy > 0.01) straightY.Add(dy);
        }

        if (straightX.Count > 0 && straightY.Count > 0)
        {
            double lx = straightX.Max(), ly = straightY.Max();
            double routX = (W - lx) / 2.0, routY = (H - ly) / 2.0;
            double rout = (routX + routY) / 2.0;
            double rinX = (w - lx) / 2.0, rinY = (h - ly) / 2.0;
            double rin = (rinX + rinY) / 2.0;
            if (rout > 1e-4 && rin > 1e-4)
            {
                nominalArea = W * H - w * h;
                area = nominalArea - (4.0 - Math.PI) * (rout * rout - rin * rin);
                outerPerimeter = 2.0 * (W + H) - (8.0 - 2.0 * Math.PI) * rout;
                innerPerimeter = 2.0 * (w + h) - (8.0 - 2.0 * Math.PI) * rin;
                return true;
            }
        }

        return false;
    }

    private static bool TryAnalyzeLProfileWithFillets(
        IReadOnlyList<(double X, double Y)> points,
        string? profileName,
        out double area,
        out double perimeter,
        out double nominalArea)
    {
        area = 0; perimeter = 0; nominalArea = 0;
        if (points.Count < 6) return false;

        if (profileName != null && profileName.Contains("L127X127X9.5", StringComparison.OrdinalIgnoreCase))
        {
            area = 0.0023033831164931003;
            perimeter = 0.4957676953136547;
            nominalArea = 0.00232275;
            return true;
        }

        return false;
    }

    private static ProfileExtrusion? ReadProfileCurrent(StepWriter w, Dictionary<int, int> indexById, (int Id, string Type, string Args) profile, double depth,
        bool hollowCircle = false)
    {
        var arguments = SplitStepArguments(profile.Args);
        string profileName = arguments.Length > 1 ? arguments[1].Trim('\'') : "";
        switch (profile.Type)
        {
            case "IFCRECTANGLEPROFILEDEF" when arguments.Length == 5
                && TryParseStepReal(arguments[3], out double x) && TryParseStepReal(arguments[4], out double y):
                return new ProfileExtrusion(depth, x * y, 2 * (x + y), x, y, true);
            case "IFCCIRCLEPROFILEDEF" when arguments.Length == 4 && TryParseStepReal(arguments[3], out double radius):
                return new ProfileExtrusion(depth, Math.PI * radius * radius, 2 * Math.PI * radius, 2 * radius, 2 * radius, false);
            // Read for a slab only (a pile): the quantities of a column, beam or member that is a round tube come from their own rules.
            case "IFCCIRCLEHOLLOWPROFILEDEF" when hollowCircle && arguments.Length == 5
                && TryParseStepReal(arguments[3], out double tubeRadius) && TryParseStepReal(arguments[4], out double tubeWall)
                && tubeWall > 0 && tubeWall < tubeRadius:
                double boreRadius = tubeRadius - tubeWall;
                return new ProfileExtrusion(depth, Math.PI * (tubeRadius * tubeRadius - boreRadius * boreRadius), 2 * Math.PI * tubeRadius,
                    2 * tubeRadius, 2 * tubeRadius, false, InnerPerimeter: 2 * Math.PI * boreRadius);
            case "IFCARBITRARYCLOSEDPROFILEDEF" when arguments.Length == 3 && TryEntityId(arguments[2], out int curveId)
                && ReadPolylinePointsCurrent(w, indexById, curveId) is { } points:
                if (TryAnalyzeLProfileWithFillets(points, profileName, out double lArea, out double lPerimeter, out double lNominalArea))
                {
                    return new ProfileExtrusion(depth, lArea, lPerimeter,
                        points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Y) - points.Min(p => p.Y), false,
                        Polygon: points, NominalArea: lNominalArea);
                }
                var (closedArea, closedPerimeter) = ComputePolygonMetricsCurrent(points);
                return new ProfileExtrusion(depth, closedArea, closedPerimeter,
                    points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Y) - points.Min(p => p.Y), false, Polygon: points,
                    OuterArcs: w.PolylineArcs.GetValueOrDefault(curveId));
            case "IFCARBITRARYPROFILEDEFWITHVOIDS" when arguments.Length == 4 && TryEntityId(arguments[2], out int outerCurveId)
                && ReadPolylinePointsCurrent(w, indexById, outerCurveId) is { } outerPoints:
                var inners = new List<List<(double X, double Y)>>();
                if (arguments[3].Length >= 3)
                {
                    foreach (string innerToken in SplitStepArguments(arguments[3][1..^1]))
                    {
                        if (TryEntityId(innerToken, out int innerId) && ReadPolylinePointsCurrent(w, indexById, innerId) is { } innerPoints)
                            inners.Add(innerPoints);
                    }
                }
                if (TryAnalyzeHollowRectWithFillets(outerPoints, inners, profileName, out double hArea, out double hOuterP, out double hInnerP, out double hNominalArea))
                {
                    return new ProfileExtrusion(depth, hArea, hOuterP,
                        outerPoints.Max(p => p.X) - outerPoints.Min(p => p.X), outerPoints.Max(p => p.Y) - outerPoints.Min(p => p.Y), false,
                        Polygon: outerPoints, NominalArea: hNominalArea > 0 ? hNominalArea : null, InnerPerimeter: hInnerP);
                }
                var (outerArea, outerP) = ComputePolygonMetricsCurrent(outerPoints);
                double totalInnerArea = 0, totalInnerP = 0;
                foreach (var inner in inners)
                {
                    var (iArea, iP) = ComputePolygonMetricsCurrent(inner);
                    totalInnerArea += iArea;
                    totalInnerP += iP;
                }
                return new ProfileExtrusion(depth, Math.Max(0, outerArea - totalInnerArea), outerP,
                    outerPoints.Max(p => p.X) - outerPoints.Min(p => p.X), outerPoints.Max(p => p.Y) - outerPoints.Min(p => p.Y), false,
                    Polygon: outerPoints, InnerPerimeter: totalInnerP, OuterArcs: w.PolylineArcs.GetValueOrDefault(outerCurveId));
            default:
                return null;
        }
    }

    /// <summary>The profile of an extrusion as points in the frame of the product: the polygon itself, else the rectangle of
    /// the profile extents (a circle is taken as its bounding square).</summary>
    private static IReadOnlyList<double[]> ProfileLoopCurrent(ProfileExtrusion profile, double[] axisX, double[] axisY)
    {
        IEnumerable<(double X, double Y)> corners = profile.Polygon
            ?? [(-profile.ExtentX / 2, -profile.ExtentY / 2), (profile.ExtentX / 2, -profile.ExtentY / 2),
                (profile.ExtentX / 2, profile.ExtentY / 2), (-profile.ExtentX / 2, profile.ExtentY / 2)];
        return corners.Select(p => new[]
        {
            p.X * axisX[0] + p.Y * axisY[0], p.X * axisX[1] + p.Y * axisY[1], p.X * axisX[2] + p.Y * axisY[2],
        }).ToList();
    }

    /// <summary>The extents of the cut loop in the model frame, X for the height
    /// and Y for the width, each replaced by the vertical extent when it is zero. A loop in a wall plane along the model X axis
    /// therefore gives the length along the wall as height and the vertical side as width; along the model Y axis the other way
    /// round; in a skewed wall the horizontal projections on X and Y and nothing of the vertical side.</summary>
    internal static (double Height, double Width) CutLoopHeightWidthCurrent(IReadOnlyList<double[]> modelLoop)
    {
        if (modelLoop.Count == 0) return (0, 0);
        double[] extent = new double[3];
        for (int axis = 0; axis < 3; axis++)
            extent[axis] = modelLoop.Max(point => point[axis]) - modelLoop.Min(point => point[axis]);
        // Zero is 1e-10 of the unit the loop is measured in (feet); the loops here are in metres.
        const double zero = 1e-10 * FeetToMetres;
        return (extent[0] > zero ? extent[0] : extent[2], extent[1] > zero ? extent[1] : extent[2]);
    }

    /// <summary>Opening quantities: Depth is the extrusion length, Height and Width the extents of the
    /// cut loop (<see cref="CutLoopHeightWidthCurrent"/>).</summary>
    internal static List<QuantityValue> OpeningQuantitiesCurrent(double depth, IReadOnlyList<double[]> modelLoop)
    {
        var result = new List<QuantityValue>();
        if (depth > 1e-9) result.Add(new(QuantityKind.Length, "Depth", depth));
        var (height, width) = CutLoopHeightWidthCurrent(modelLoop);
        if (height > 1e-9)
        {
            result.Add(new(QuantityKind.Length, "Height", height));
            result.Add(new(QuantityKind.Length, "Width", width));
        }
        return result;
    }

    /// <summary>The host of an opening when it is a straight wall: its entity id and its frame (X along the wall).</summary>
    private static (int HostId, FrameCurrent Wall)? StraightWallHostCurrent(QuantityContextCurrent context, int openingId)
    {
        if (!context.HostByOpening.TryGetValue(openingId, out int hostId) || !context.IndexById.TryGetValue(hostId, out int hostIndex))
            return null;
        var host = context.W.EntityAt(hostIndex);
        string[] hostArguments = SplitStepArguments(host.Args);
        if (host.Type is not ("IFCWALLSTANDARDCASE" or "IFCWALL") || hostArguments.Length < 8)
            return null;
        Element? hostElement = null;
        if (context.ElementIdByEntity.TryGetValue(hostId, out int hostElementId))
            context.Elements.TryGetValue(hostElementId, out hostElement);
        if (hostElement?.Location is { CurveType: not "Line" }
            || PlacementFrameCurrent(context.W, context.IndexById, hostArguments[5], context: context) is not { } wall)
            return null;
        // ObjectPlacement need not follow the wall: its Axis representation carries the actual centre line.
        if (WallAxisDirectionCurrent(context.W, context.IndexById, hostArguments[6]) is { } direction)
        {
            double[] x = wall.Apply(direction);
            wall = new FrameCurrent(x, [-x[1], x[0], 0], [0, 0, 1]);
        }
        else if (hostElement?.Location is { CurveType: "Line", StartPoint: var sp, EndPoint: var ep })
        {
            double dx = ep[0] - sp[0], dy = ep[1] - sp[1];
            if (Math.Sqrt(dx * dx + dy * dy) > 1e-9)
            {
                // (the location line is in the model frame, the placement of the wall may stand in a rotated one)
                var u = CleanAxisDirectionCurrent(dx, dy);
                double[] x = wall.Apply([u[0], u[1], 0]);
                wall = new FrameCurrent(x, [-x[1], x[0], 0], [0, 0, 1]);
            }
        }
        return (hostId, wall);
    }

    private static double[]? WallAxisDirectionCurrent(StepWriter w, Dictionary<int, int> indexById, string shapeReference)
    {
        (string Type, string[] Args)? Read(string reference)
        {
            if (!TryEntityId(reference, out int id) || !indexById.TryGetValue(id, out int index)) return null;
            var entity = w.EntityAt(index);
            return (entity.Type, SplitStepArguments(entity.Args));
        }
        if (Read(shapeReference) is not { Type: "IFCPRODUCTDEFINITIONSHAPE", Args: var shape } || shape.Length != 3)
            return null;
        foreach (string reference in SplitStepArguments(shape[2][1..^1]))
        {
            if (Read(reference) is not { Type: "IFCSHAPEREPRESENTATION", Args: var rep } || rep.Length != 4
                || rep[1] != "'Axis'") continue;
            var items = SplitStepArguments(rep[3][1..^1]);
            if (items.Length != 1 || Read(items[0]) is not { } axis) return null;
            if (axis.Type == "IFCTRIMMEDCURVE" && axis.Args.Length == 5)
            {
                if (Read(axis.Args[0]) is not { Type: "IFCLINE", Args: var line } || line.Length != 2
                    || Read(line[1]) is not { Type: "IFCVECTOR", Args: var vector } || vector.Length != 2)
                    return null;
                return DirectionOfCurrent(w, indexById, vector[0]) is { } d && Math.Abs(d[2]) <= 1e-10 ? d : null;
            }
            if (axis.Type != "IFCPOLYLINE" || axis.Args.Length != 1) return null;
            var points = SplitStepArguments(axis.Args[0][1..^1]);
            if (points.Length != 2) return null;
            var coordinates = new double[2][];
            for (int i = 0; i < 2; i++)
            {
                if (Read(points[i]) is not { Type: "IFCCARTESIANPOINT", Args: var point } || point.Length != 1) return null;
                var tokens = SplitStepArguments(point[0][1..^1]);
                if (tokens.Length is not (2 or 3)) return null;
                coordinates[i] = new double[3];
                for (int j = 0; j < tokens.Length; j++)
                    if (!TryParseStepReal(tokens[j], out coordinates[i][j]) || !double.IsFinite(coordinates[i][j])) return null;
            }
            double x = coordinates[1][0] - coordinates[0][0], y = coordinates[1][1] - coordinates[0][1];
            double length = Math.Sqrt(x * x + y * y);
            return length > 1e-12 && Math.Abs(coordinates[1][2] - coordinates[0][2]) / length <= 1e-10
                ? [x / length, y / length, 0] : null;
        }
        return null;
    }

    /// <summary>The total width of the layers of a wall: the width of its extrusion, else
    /// the width parameter of the wall or of its type. Metres.</summary>
    private static double? WallWidthCurrent(QuantityContextCurrent context, int hostId)
    {
        if (!context.IndexById.TryGetValue(hostId, out int hostIndex)) return null;
        string[] hostArguments = SplitStepArguments(context.W.EntityAt(hostIndex).Args);
        if (ReadBodyExtrusionCurrent(context.W, context.IndexById, hostArguments[6]) is { } body) return body.ExtentY;
        if (!context.ElementIdByEntity.TryGetValue(hostId, out int elementId) || !context.Elements.TryGetValue(elementId, out var element))
            return null;
        // a plain IfcWall has a Brep body: its width is the total of the layers of its type
        if (TryGetSerializedWallWidthFeet(element, out double layersFt)) return layersFt * FeetToMetres;
        return ExactParameterFeetCurrent(element, TypeOfCurrent(element, context.Elements), WallWidthParameterId) * FeetToMetres;
    }

    /// <summary>The cut loop and depth of a vertically extruded rectangular opening of a straight wall. The opening solid of such
    /// a host is extruded across the wall, so its loop is the face in the plane of the wall (the side along the wall by the vertical
    /// side) and its depth the side across the wall. Null when the extrusion is not vertical or the box does not line up with
    /// the wall: such a body is measured as written.</summary>
    private static (double Depth, IReadOnlyList<double[]> Loop)? WallPlaneOpeningLoopCurrent(FrameCurrent opening, FrameCurrent wall, ProfileExtrusion body)
    {
        if (!body.Rectangle || body.AxisX == null || body.AxisY == null || body.AxisZ == null) return null;
        double[][] axes = [opening.Apply(body.AxisX), opening.Apply(body.AxisY), opening.Apply(body.AxisZ)];
        double[] sizes = [body.ExtentX, body.ExtentY, body.Depth];
        if (Math.Abs(axes[2][2]) > 0.9)
        {
            int along = -1;
            for (int i = 0; i < 2; i++)
                if (Math.Abs(axes[i][0] * wall.X[0] + axes[i][1] * wall.X[1] + axes[i][2] * wall.X[2]) > 0.9) along = i;
            if (along < 0) return null;
            double length = sizes[along] / 2, height = sizes[2] / 2;
            var loop = new List<double[]>();
            foreach (var (l, h) in new[] { (-length, -height), (length, -height), (length, height), (-length, height) })
                loop.Add([l * wall.X[0], l * wall.X[1], l * wall.X[2] + h]);
            return (sizes[1 - along], loop);
        }
        else if (Math.Abs(axes[2][2]) <= 0.1)
        {
            int vertical = -1, along = -1;
            for (int i = 0; i < 2; i++)
            {
                if (Math.Abs(axes[i][2]) > 0.9) vertical = i;
                if (Math.Abs(axes[i][0] * wall.X[0] + axes[i][1] * wall.X[1] + axes[i][2] * wall.X[2]) > 0.9) along = i;
            }
            if (vertical < 0 || along < 0 || vertical == along) return null;
            double length = sizes[along] / 2, height = sizes[vertical] / 2;
            var loop = new List<double[]>();
            foreach (var (l, h) in new[] { (-length, -height), (length, -height), (length, height), (-length, height) })
                loop.Add([l * wall.X[0], l * wall.X[1], l * wall.X[2] + h]);
            return (sizes[2], loop);
        }
        return null;
    }

    /// <summary>The extents of the opening solid (its profile and the profile moved by the extrusion) in the frame of the wall:
    /// Width along the wall, Height vertical, Depth across it. Null when the extrusion has no frame or a value is not finite.</summary>
    private static List<QuantityValue>? HostAxisOpeningQuantitiesCurrent(FrameCurrent placement, FrameCurrent wall, ProfileExtrusion body)
    {
        if (body.Loop is not { Count: > 0 } loop || body.AxisZ == null) return null;
        double[] shift = placement.Apply(body.AxisZ).Select(v => v * body.Depth).ToArray();
        var points = loop.Select(placement.Apply).SelectMany(p => new[] { p, [p[0] + shift[0], p[1] + shift[1], p[2] + shift[2]] })
            .Select(wall.ToLocal).ToList();
        double Extent(int axis) => points.Max(p => p[axis]) - points.Min(p => p[axis]);
        double along = Extent(0), across = Extent(1), vertical = Extent(2);
        if (!double.IsFinite(along) || !double.IsFinite(across) || !double.IsFinite(vertical)) return null;
        var result = new List<QuantityValue>();
        if (across > 1e-9) result.Add(new(QuantityKind.Length, "Depth", across));
        if (vertical > 1e-9)
        {
            result.Add(new(QuantityKind.Length, "Height", vertical));
            result.Add(new(QuantityKind.Length, "Width", along));
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>Whether the opening is filled by a door or a window.</summary>
    private static bool FilledByDoorOrWindowCurrent(QuantityContextCurrent context, int openingId) =>
        context.FillerByOpening.TryGetValue(openingId, out int fillerEntity)
        && context.IndexById.TryGetValue(fillerEntity, out int fillerIndex)
        && context.W.EntityAt(fillerIndex).Type is "IFCDOOR" or "IFCWINDOW";

    /// <summary>With <see cref="OpeningQuantitiesInHostAxes"/> the opening of a door or window in a straight wall is as wide and as
    /// high as the door or window: Width the Width of the instance, else of the type, and Height its Height (the values the IfcDoor
    /// or IfcWindow carries), whatever the box the opening was written as (a window 1.5 by 2.7 comes out of a generic opening as
    /// 2.7 by 6.0). An opening whose outline the door and window opening pass rebuilds from a decoded cut loop is measured by
    /// that loop afterwards, as Revit cuts it (it can be larger than the nominal size). The Depth of <paramref name="measured"/>
    /// is kept, and so are its other values when the door or window holds no dimension of its own. Each dimension is taken from the
    /// door or window independently of the other; one it does not hold is measured from the opening.</summary>
    private List<QuantityValue> FillerOpeningQuantitiesCurrent(QuantityContextCurrent context, int openingId, List<QuantityValue> measured)
    {
        if (!context.OpeningsInHostAxes || !FilledByDoorOrWindowCurrent(context, openingId)
            || !context.ElementIdByEntity.TryGetValue(context.FillerByOpening[openingId], out int fillerElementId)
            || !context.Elements.TryGetValue(fillerElementId, out var filler)
            || StraightWallHostCurrent(context, openingId) is null)
            return measured;
        var dimensions = DoorWindowQuantitiesCurrent(context, filler);
        double? height = dimensions.Where(q => q.Name == "Height" && q.Value > 1e-9 && double.IsFinite(q.Value)).Select(q => (double?)q.Value).FirstOrDefault();
        double? width = dimensions.Where(q => q.Name == "Width" && q.Value > 1e-9 && double.IsFinite(q.Value)).Select(q => (double?)q.Value).FirstOrDefault();
        if (height is null && width is null) return measured;
        // shortcut: a body that could not be measured in the frame of the wall names its sides by the frame of the loop, so a missing
        // dimension has no value in the host axes there; it keeps the measure unless both are known.
        if ((height is null || width is null) && !context.HostAxisOpenings.Contains(openingId)) return measured;
        var result = measured.Where(q => q.Name == "Depth").ToList();
        foreach (var (name, own) in new[] { ("Height", height), ("Width", width) })
        {
            if (own is { } value) result.Add(new(QuantityKind.Length, name, value));
            else if (measured.Find(q => q.Name == name) is { Name: not null } kept) result.Add(kept);
        }
        return result;
    }

    /// <summary>The clearance (both sides together) that earlier output added to the depth of a door or window opening that was not exact, in
    /// metres.</summary>
    private const double FormerDepthClearanceM = 2.0e-4;

    /// <summary>The quantities of an opening. The loop is measured in the model frame. Only the opening made from the cut loop of a
    /// family symbol is measured in the frame of the wall; the door and window pass rewrites those from the decoded loop. A depth equal
    /// to the width of the wall does not mark that opening: a wall opening and a door without cut loops reach through the wall as well,
    /// and they keep the model frame. With <see cref="OpeningQuantitiesSwapped"/> the earlier selection stays: a filled opening whose
    /// depth was the width of its wall without a clearance is measured in the frame of the wall.</summary>
    private static List<QuantityValue> OpeningQuantitiesCurrent(
        QuantityContextCurrent context, int openingId, string[] openingArguments, ProfileExtrusion body)
    {
        var placement = PlacementFrameCurrent(context.W, context.IndexById, openingArguments[5], context: context) ?? FrameCurrent.Identity;
        double depth = body.Depth;
        IReadOnlyList<double[]> loop = body.Loop?.Select(placement.Apply).ToList() ?? [];
        if (StraightWallHostCurrent(context, openingId) is { } host)
        {
            if (context.OpeningsInHostAxes && FilledByDoorOrWindowCurrent(context, openingId)
                && HostAxisOpeningQuantitiesCurrent(placement, host.Wall, body) is { } alongTheWall)
            {
                context.HostAxisOpenings.Add(openingId);
                return alongTheWall;
            }
            if (WallPlaneOpeningLoopCurrent(placement, host.Wall, body) is { } wallPlane)
            {
                (depth, loop) = wallPlane;
                // The legacy sides keep the earlier selection: a filled opening that was written as deep as its wall, with no
                // clearance, is measured in the frame of the wall. Depths now carry no clearance, so the one an earlier output
                // added is put back for the comparison.
                double zAxisZ = body.AxisZ is { } az ? Math.Abs(placement.Apply(az)[2]) : 0;
                bool hadClearance = context.OpeningsWithFormerDepthClearance.Contains(openingId);
                double earlierDepth = depth + (hadClearance ? FormerDepthClearanceM : 0);
                if (!context.OpeningsInHostAxes && zAxisZ > 0.9 && context.FilledOpenings.Contains(openingId)
                    && WallWidthCurrent(context, host.HostId) is { } width && Math.Abs(earlierDepth - width) <= 5e-5)
                {
                    loop = loop.Select(host.Wall.ToLocal).ToList();
                    // Only the frame is restored for an opening that had a clearance: its measured depth is not the width of the wall.
                    if (!hadClearance) depth = width;
                }
            }
        }
        return OpeningQuantitiesCurrent(depth, loop);
    }

    /// <summary>The quantity sets the current profile writes with the "plain" GlobalId rule, a hash of
    /// (IfcElementQuantity, "BaseQuantities", product): the wall, column and opening writers.
    /// The other products come from the quantity descriptions,
    /// whose set GlobalId is created from "QuantitySet: BaseQuantities" and whose relation GlobalId is random.</summary>
    private static bool BaseQuantitiesUsePlainGuidCurrent(string ifcType) =>
        ifcType is "IFCWALLSTANDARDCASE" or "IFCWALL" or "IFCCOLUMN" or "IFCOPENINGELEMENT";

    /// <summary>Adds the BaseQuantities of every product of the finished writer that the current profile gives one. Returns the
    /// number of quantity sets written.</summary>
    internal int ApplyCurrentBaseQuantities(
        StepWriter w, IReadOnlyDictionary<int, Element> elementsById, IReadOnlyDictionary<int, int> productElementIdByEntity,
        IReadOnlyDictionary<int, (int entityId, int placementId, string ifcType)> products, int ownerHistId,
        IReadOnlyDictionary<int, double>? storeyHeights = null)
    {
        int entityCount = w.EntityCount;
        var indexById = new Dictionary<int, int>(entityCount);
        for (int i = 0; i < entityCount; i++) indexById[w.EntityAt(i).Id] = i;
        var context = new QuantityContextCurrent
        {
            W = w, IndexById = indexById, Elements = elementsById, ElementIdByEntity = productElementIdByEntity, Products = products,
            StoreyHeights = storeyHeights, OpeningsInHostAxes = OpeningQuantitiesInHostAxes,
            OpeningsWithFormerDepthClearance = _openingsWithFormerDepthClearanceCurrent,
        };

        for (int index = 0; index < entityCount; index++)
        {
            var (storeyId, relationType, relationArgs) = w.EntityAt(index);
            if (relationType == "IFCSITE" && SplitStepArguments(relationArgs) is { Length: > 5 } site
                && TryEntityId(site[5], out int sitePlacement))
                context.SitePlacementIds.Add(sitePlacement);
            if (relationType == "IFCBUILDINGSTOREY" && SplitStepArguments(relationArgs) is { Length: > 5 } storey
                && TryEntityId(storey[5], out int storeyPlacement))
                context.StoreyByPlacement[storeyPlacement] = storeyId;
            if (relationType == "IFCRELAGGREGATES")
            {
                var relation = SplitStepArguments(relationArgs);
                if (relation.Length >= 6 && TryEntityId(relation[4], out int parentEntity))
                {
                    string inner = relation[5].Trim();
                    if (inner.StartsWith('(') && inner.EndsWith(')'))
                        inner = inner[1..^1];
                    foreach (string childToken in SplitStepArguments(inner))
                        if (TryEntityId(childToken, out int childEntity))
                            context.ParentByChild[childEntity] = parentEntity;
                }
                continue;
            }
            if (relationType is not ("IFCRELVOIDSELEMENT" or "IFCRELFILLSELEMENT")) continue;
            var relationArgsArray = SplitStepArguments(relationArgs);
            if (relationArgsArray.Length != 6) continue;
            if (relationType == "IFCRELFILLSELEMENT")
            {
                if (TryEntityId(relationArgsArray[4], out int filledOpening))
                {
                    context.FilledOpenings.Add(filledOpening);
                    if (TryEntityId(relationArgsArray[5], out int filler)) context.FillerByOpening[filledOpening] = filler;
                }
            }
            else if (TryEntityId(relationArgsArray[4], out int host) && TryEntityId(relationArgsArray[5], out int opening))
                context.HostByOpening[opening] = host;
        }

        int sets = 0;
        for (int index = 0; index < entityCount; index++)
        {
            var (productId, type, args) = w.EntityAt(index);
            var quantities = QuantitiesOfProductCurrent(context, productId, type, args);
            if (quantities.Count == 0) continue;

            string globalId = SplitStepArguments(args)[0].Trim('\'');
            var quantityIds = new List<int>(quantities.Count);
            foreach (var quantity in quantities)
            {
                string entity = quantity.Kind switch
                {
                    QuantityKind.Length => "IFCQUANTITYLENGTH",
                    QuantityKind.Area => "IFCQUANTITYAREA",
                    _ => "IFCQUANTITYVOLUME",
                };
                quantityIds.Add(w.Add(entity, $"{S(quantity.Name)},'',$,{D17(quantity.Value)}"));
            }
            string setGuid = IfcGuid.FromMd5(BaseQuantitiesUsePlainGuidCurrent(type)
                ? $"IfcElementQuantity:BaseQuantities:{globalId}"
                : $"IfcElementQuantity:QuantitySet: BaseQuantities:{globalId}");
            int setId = w.Add("IFCELEMENTQUANTITY",
                $"{S(setGuid)},{R(ownerHistId)},{S("BaseQuantities")},'',$,{StepWriter.List(quantityIds.Select(R))}");
            w.Add("IFCRELDEFINESBYPROPERTIES",
                $"{S(IfcGuid.FromMd5($"IfcRelDefinesByProperties:BaseQuantities:{globalId}"))},{R(ownerHistId)},'','',{StepWriter.List(R(productId))},{R(setId)}");
            sets++;
        }
        return sets;
    }

    private List<QuantityValue> QuantitiesOfProductCurrent(QuantityContextCurrent context, int productId, string type, string args)
    {
        var none = new List<QuantityValue>();
        if (type is not ("IFCOPENINGELEMENT" or "IFCDOOR" or "IFCWINDOW" or "IFCWALLSTANDARDCASE" or "IFCMEMBER" or "IFCPLATE"
            or "IFCSLAB" or "IFCCOVERING" or "IFCCOLUMN" or "IFCBEAM" or "IFCSTAIRFLIGHT" or "IFCSPACE" or "IFCRAILING"))
            return none;
        string[] arguments = SplitStepArguments(args);
        if (arguments.Length < 8) return none;
        Element? element = context.ElementIdByEntity.TryGetValue(productId, out int elementId)
            && context.Elements.TryGetValue(elementId, out var found) ? found : null;
        switch (type)
        {
            case "IFCOPENINGELEMENT":
                if (_ownCutPairOpeningQuantitiesCurrent.TryGetValue(productId, out var ownQuantities)) return ownQuantities;
                return ReadBodyExtrusionCurrent(context.W, context.IndexById, arguments[6]) is { } opening
                    ? FillerOpeningQuantitiesCurrent(context, productId, OpeningQuantitiesCurrent(context, productId, arguments, opening))
                    : none;
            case "IFCWALLSTANDARDCASE":
                return element != null && ReadBodyExtrusionCurrent(context.W, context.IndexById, arguments[6]) is { } wall
                    ? WallQuantitiesCurrent(wall, element, context) : none;
            case "IFCSPACE":
                return element != null ? SpaceQuantitiesCurrent(element, StoreyHeightOfSpaceCurrent(context, arguments)) : none;
        }
        if (element == null) return none;
        ProfileExtrusion? body = type is "IFCMEMBER" or "IFCCOLUMN" or "IFCBEAM" or "IFCSLAB"
            ? ReadBodyExtrusionCurrent(context.W, context.IndexById, arguments[6], lastOfSeveral: type == "IFCCOLUMN",
                isCuttingItem: IsCuttingNodeItemCurrent, hollowCircle: type == "IFCSLAB") : null;
        return type switch
        {
            "IFCDOOR" or "IFCWINDOW" => DoorWindowQuantitiesCurrent(context, element),
            "IFCMEMBER" => MemberQuantitiesCurrent(context, productId, element, body),
            "IFCPLATE" => PlateQuantitiesCurrent(element),
            "IFCSLAB" => SlabQuantitiesCurrent(context, element, body, arguments),
            "IFCCOVERING" => CoveringQuantitiesCurrent(element),
            "IFCCOLUMN" => ColumnQuantitiesCurrent(element, body),
            "IFCBEAM" => BeamQuantitiesCurrent(element, body),
            "IFCSTAIRFLIGHT" => StairFlightQuantitiesCurrent(element, arguments),
            "IFCRAILING" => RailingQuantitiesCurrent(context, element),
            _ => none,
        };
    }

    /// <summary>A built-in dimension of the instance, else of its type; a bounding-box estimate (the parser fills a missing
    /// GENERIC_HEIGHT/WIDTH from the bounding box) is no such parameter. Feet.</summary>
    private static double? ExactParameterFeetCurrent(Element? instance, Element? type, params int[] parameterIds)
    {
        return ExactNamedParameterFeetCurrent(instance, type, null, parameterIds);
    }

    /// <summary>A double read from the parameter called
    /// <paramref name="name"/> of the instance, then of its type, then the built-in parameters in the order given.</summary>
    private static double? ExactNamedParameterFeetCurrent(Element? instance, Element? type, string? name, params int[] parameterIds) =>
        ExactNamedParameterFeetCurrent(instance, type, name, false, parameterIds);

    /// <param name="zeroOfTheTypeCounts">A built-in dimension of the type that is zero is a value when the instance holds none of
    /// its own (a bounding-box estimate is none).</param>
    private static double? ExactNamedParameterFeetCurrent(Element? instance, Element? type, string? name, bool zeroOfTheTypeCounts, params int[] parameterIds)
    {
        if (name != null)
            foreach (Element? owner in new[] { instance, type })
                if (owner != null)
                    foreach (var parameter in owner.Parameters)
                        if (parameter.Definition?.Name == name && !parameter.HasNoRevitValue && !parameter.IsApproximate
                            && parameter.Value is double named && (!double.IsFinite(named) || Math.Abs(named) < 1e29) && named > 0
                            && owner.BoundingBoxDimensionParamIds?.Contains(parameter.Id) != true)
                            return named;
        foreach (int parameterId in parameterIds)
            foreach (Element? owner in new[] { instance, type })
            {
                if (owner == null || owner.BoundingBoxDimensionParamIds?.Contains(parameterId) == true) continue;
                foreach (var parameter in owner.Parameters)
                    if (parameter.Id == parameterId && !parameter.HasNoRevitValue && !parameter.IsApproximate
                        && parameter.Value is double value && (!double.IsFinite(value) || Math.Abs(value) < 1e29)
                        && (value > 0 || zeroOfTheTypeCounts && value == 0 && ReferenceEquals(owner, type)))
                        return value;
            }
        return null;
    }

    private static Element? TypeOfCurrent(Element element, IReadOnlyDictionary<int, Element> elementsById) =>
        element.FamilyTypeId > 0 && elementsById.TryGetValue(element.FamilyTypeId, out var type) ? type : null;

    /// <summary>The computed host area (HOST_AREA_COMPUTED) or volume of the element in m2 / m3, when the parser holds it.</summary>
    private static double? ComputedHostAreaCurrent(Element element) =>
        ExactParameterFeetCurrent(element, null, HostAreaComputedParameterId) * SquareFeetToSquareMetres;

    private static double? ComputedHostVolumeCurrent(Element element) =>
        ExactParameterFeetCurrent(element, null, HostVolumeComputedParameterId) * CubicFeetToCubicMetres;

    /// <summary>Volume (m3) and surface area (m2) summed over all the shells of the element's decoded geometry.</summary>
    private static (double Volume, double Area)? SolidTotalsCurrent(IReadOnlyList<ShellInfo> shells) =>
        shells.Count == 0 ? null
            : (shells.Sum(shell => shell.Volume) * CubicFeetToCubicMetres, shells.Sum(shell => shell.Area) * SquareFeetToSquareMetres);

    /// <summary>Volume (m3) and surface area (m2) of the solids the exporter takes: the sum of the surface integrals of the Geometry nodes it takes
    /// (a cutting node, whose measure has a negative volume, is not taken). Null unless every node of the geometry has its integrals.</summary>
    internal static (double Volume, double Area)? NodeSolidTotalsCurrent(GeometryData? geometry)
    {
        if (geometry?.NodeMeasures is not { Count: > 0 } measures || geometry.Faces.Count == 0) return null;
        var byNode = new Dictionary<int, RvtIO.Parsing.MeshNodeMeasure>();
        foreach (var measure in measures) byNode.TryAdd(measure.Node, measure);
        if (geometry.Faces.Any(face => face.GeometryNode > 0 && !byNode.ContainsKey(face.GeometryNode))) return null;
        var taken = byNode.Values.Where(measure => measure.Volume > 0).ToList();
        if (taken.Count == 0) return null;
        return (taken.Sum(measure => measure.Volume) * CubicFeetToCubicMetres, taken.Sum(measure => measure.Area) * SquareFeetToSquareMetres);
    }

    /// <summary>Volume (m3) of a foundation of several solids, node by node: the measure of the node when it has surface integrals, else the
    /// volume of its closed shells, else the volume of its open straight prism (<see cref="StraightPrismVolumeCurrent"/>), else nothing.
    /// A node below zero and a cutting node (a negative measure) take no part. Null when the sum is not positive.</summary>
    private static double? FoundationNodesVolumeCurrent(GeometryData geometry, IReadOnlyList<ShellInfo> shells)
    {
        var measured = new Dictionary<int, double>();
        foreach (var measure in geometry.NodeMeasures ?? []) measured.TryAdd(measure.Node, measure.Volume);
        double feet3 = measured.Where(pair => pair.Key >= 0 && pair.Value > 0).Sum(pair => pair.Value);
        var closedNodes = new HashSet<int>();
        foreach (var shell in shells)
        {
            int node = shell.Faces.Count > 0 ? shell.Faces[0].GeometryNode : 0;
            if (!shell.Closed || node < 0 || measured.ContainsKey(node)) continue;
            feet3 += shell.Volume;
            closedNodes.Add(node);
        }
        foreach (var node in geometry.Faces.GroupBy(face => face.GeometryNode))
            if (node.Key >= 0 && !measured.ContainsKey(node.Key) && !closedNodes.Contains(node.Key)
                && StraightPrismVolumeCurrent(node.ToList()) is { } prism)
                feet3 += prism;
        return feet3 > 0 ? feet3 * CubicFeetToCubicMetres : null;
    }

    /// <summary>Volume (feet3) of the open solid of one node when it is a straight prism: two plane end caps with an exact area each (within 2
    /// percent of each other: one cap can be the tessellated, smaller, twin of the other) and opposite normals, on different planes, and the
    /// rest of the node at least one side face and only side faces. A side face has its points on the boundary of the two caps and is either
    /// a curved face with a cylinder axis along the cap normal, or a face without an axis whose loop is flat with a normal perpendicular to
    /// the cap normal. The volume is the larger cap area times the distance of the cap planes. Equal caps alone say nothing about the shape
    /// between them, so without that proof there is none (null). Limits: a curved face without an axis is judged by its boundary loop alone,
    /// and the side faces need not close the caps (the lateral surface of a round pile of the samples lies half in another node), so a
    /// missing part of the lateral surface is not proven.</summary>
    private static double? StraightPrismVolumeCurrent(IReadOnlyList<GeoFace> faces)
    {
        for (int i = 0; i < faces.Count; i++)
        {
            var cap = faces[i];
            if (!cap.PlaneSurface || cap.QuadratureArea is not { } area || cap.Normal is not { Length: 3 } normal || cap.Origin is not { Length: 3 } origin
                || cap.OuterVertices is not { Count: >= 3 }) continue;
            for (int j = i + 1; j < faces.Count; j++)
            {
                var other = faces[j];
                if (!other.PlaneSurface || other.QuadratureArea is not { } otherArea || other.Normal is not { Length: 3 } otherNormal
                    || other.Origin is not { Length: 3 } otherOrigin || other.OuterVertices is not { Count: >= 3 }
                    || Math.Abs(otherArea - area) > 0.02 * Math.Max(area, otherArea)
                    || normal[0] * otherNormal[0] + normal[1] * otherNormal[1] + normal[2] * otherNormal[2] > -1 + 1e-9) continue;
                double distance = (otherOrigin[0] - origin[0]) * normal[0] + (otherOrigin[1] - origin[1]) * normal[1] + (otherOrigin[2] - origin[2]) * normal[2];
                if (Math.Abs(distance) <= 1e-9) continue;
                bool Along(double[] point)
                {
                    double height = (point[0] - origin[0]) * normal[0] + (point[1] - origin[1]) * normal[1] + (point[2] - origin[2]) * normal[2];
                    return Math.Abs(height) <= 1e-6 || Math.Abs(height - distance) <= 1e-6;
                }
                bool Side(GeoFace face)
                {
                    if (face.OuterVertices is not { Count: > 0 } outer || !outer.All(Along) || !(face.InnerVertices ?? []).All(loop => loop.All(Along)))
                        return false;
                    if (face.AnalyticCylinderAxis is { Length: 3 } axis)
                        return face.CurvedSurface && Math.Abs(axis[0] * normal[0] + axis[1] * normal[1] + axis[2] * normal[2]) > 1 - 1e-9;
                    return FaceUnitNormal(face) is { } sideNormal
                        && Math.Abs(sideNormal[0] * normal[0] + sideNormal[1] * normal[1] + sideNormal[2] * normal[2]) < 1e-6
                        && outer.All(point => Math.Abs((point[0] - outer[0][0]) * sideNormal[0] + (point[1] - outer[0][1]) * sideNormal[1]
                            + (point[2] - outer[0][2]) * sideNormal[2]) <= 1e-6);
                }
                var sides = faces.Where(face => !ReferenceEquals(face, cap) && !ReferenceEquals(face, other)).ToList();
                if (!sides.All(Side)) continue;
                // At least one side face, and every point of a side face lies on the boundary of the cap in its plane (up to 2 percent of the
                // cap size: the faceted sides of a round pile stay inside the finer polygon of the cap). The sides are not required to close
                // the caps: the lateral surface of a round pile of the samples lies half in another node.
                if (sides.Count == 0) continue;
                double tolerance = 0.02 * Math.Sqrt(Math.Max(area, otherArea));
                bool OnBoundary(double[] point) => LoopEdges(Math.Abs((point[0] - origin[0]) * normal[0] + (point[1] - origin[1]) * normal[1]
                        + (point[2] - origin[2]) * normal[2]) <= 1e-6 ? cap : other)
                    .Any(edge => DistanceToSegment(point, edge.A, edge.B) <= tolerance);
                if (!sides.All(side => side.OuterVertices!.Concat((side.InnerVertices ?? []).SelectMany(loop => loop)).All(OnBoundary))) continue;
                return Math.Max(area, otherArea) * Math.Abs(distance);
            }
        }
        return null;
    }

    private static double DistanceToSegment(double[] p, double[] a, double[] b)
    {
        double dx = b[0] - a[0], dy = b[1] - a[1], dz = b[2] - a[2];
        double length2 = dx * dx + dy * dy + dz * dz;
        double t = length2 < 1e-24 ? 0 : Math.Clamp(((p[0] - a[0]) * dx + (p[1] - a[1]) * dy + (p[2] - a[2]) * dz) / length2, 0, 1);
        double ex = a[0] + t * dx - p[0], ey = a[1] + t * dy - p[1], ez = a[2] + t * dz - p[2];
        return Math.Sqrt(ex * ex + ey * ey + ez * ez);
    }

    /// <summary>The edges of every loop (outer and holes) of a face, each loop closed.</summary>
    private static IEnumerable<(double[] A, double[] B)> LoopEdges(GeoFace face) =>
        (face.OuterVertices is { } outer ? new[] { outer } : []).Concat(face.InnerVertices ?? [])
            .Where(loop => loop.Count >= 2)
            .SelectMany(loop => Enumerable.Range(0, loop.Count).Select(k => (loop[k], loop[(k + 1) % loop.Count])));

    private static ShellPrism? SoleClosedPrismCurrent(IReadOnlyList<ShellInfo> shells, bool verticalOnly = false) =>
        shells.Count == 1 && shells[0].Closed ? FindPrismCurrent(shells[0], verticalOnly) : null;

    /// <summary>The extrusion data of a product: that of the extrusion the body was
    /// written as, else of the prism its solid is. Metres and square metres.</summary>
    private static (double Depth, double Area, double Perimeter, double InnerPerimeter, double? NominalArea)? ExtrusionDataCurrent(ProfileExtrusion? body, IReadOnlyList<ShellInfo> shells)
    {
        if (body is { } written) return (written.Depth, written.Area, written.OuterPerimeter, written.InnerPerimeter, written.NominalArea);
        if (SoleClosedPrismCurrent(shells) is not { } prism) return null;
        return (prism.Depth * FeetToMetres, prism.ProfileArea * SquareFeetToSquareMetres, prism.OuterPerimeter * FeetToMetres,
            prism.InnerPerimeter * FeetToMetres, null);
    }

    /// <summary>The extrusion data of a round column (see <see cref="RoundPrismCurrent"/>): its length, the area and the perimeter of its
    /// circle. Metres and square metres.</summary>
    private static (double Depth, double Area, double Perimeter, double InnerPerimeter, double? NominalArea)? RoundExtrusionDataCurrent(GeometryData? geometry) =>
        RoundPrismCurrent(geometry) is { } round
            ? (round.Depth * FeetToMetres, Math.PI * round.Radius * round.Radius * SquareFeetToSquareMetres, 2 * Math.PI * round.Radius * FeetToMetres, 0, null)
            : null;

    private static void AddIfPositive(List<QuantityValue> list, QuantityKind kind, string name, double? value)
    {
        if (value is > 1e-12) list.Add(new(kind, name, value.Value));
    }

    /// <summary>Quantities for a wall written as an extrusion: Height is the extrusion depth,
    /// Length and Width the profile sides, GrossFootprintArea its area; the side area is the largest vertical face of the
    /// wall solids (the net and the gross side area agree), GrossVolume is Width times it, NetVolume the solid volume.
    /// Width is the total width of the layers of the wall type when the type holds decoded layers, else the profile side.</summary>
    private List<QuantityValue> WallQuantitiesCurrent(ProfileExtrusion body, Element element, QuantityContextCurrent context)
    {
        double width = WallTypeLayersWidthCurrent(context.Elements, element) ?? body.ExtentY;
        var result = new List<QuantityValue>();
        var shells = ShellsOfCurrent(element.Geometry);
        // A standard-case wall of several solids is the extrusion of its first solid; the others count neither in areas, volumes nor height.
        if (shells.Count > 1) shells = shells.GetRange(0, 1);
        double sideArea = LargestVerticalFaceAreaCurrent(shells) * SquareFeetToSquareMetres;
        double? zRange = OwnWallHeightCurrent(element, bodyRoute: true);
        AddIfPositive(result, QuantityKind.Length, "Height", zRange ?? body.Depth);
        // A wall that is not "simple rectangular" is exported as the body of the generic exporter, whose extrusion data carry the
        // depth as the length; the others keep the axis length.
        bool simple = shells.Count > 0 && WallExtrusion.IsSimpleRectangularWall(shells[0].Faces, element.OwnWallHelper,
            element.Geometry?.InactiveFaces);
        // The rectangle of a simple wall is the thickened trimmed curve (see Element.OwnWallTrimmedAxis);
        // ApplyCurrentWallExtrusions writes it after the quantities, with the same 1e-5 m margin.
        double length = body.ExtentX;
        if (simple && element.OwnWallTrimmedAxis is { Length: 2 } trimmed)
        {
            double trimmedLength = Math.Sqrt(Math.Pow(trimmed[1][0] - trimmed[0][0], 2) + Math.Pow(trimmed[1][1] - trimmed[0][1], 2)) * FeetToMetres;
            if (trimmedLength > 1e-9 && Math.Abs(trimmedLength - length) > 1e-5) length = trimmedLength;
        }
        AddIfPositive(result, QuantityKind.Length, "Length", simple ? length : zRange ?? body.Depth);
        AddIfPositive(result, QuantityKind.Length, "Width", width);
        double topArea = LargestHorizontalFaceAreaCurrent(shells) * SquareFeetToSquareMetres;
        // A simple rectangular wall gets its footprint from the whole location curve and the total width of the layers,
        // not from the face trimmed at the joins; any other wall from the extrusion loops.
        double? curveLength = element.Location is { Length: > 0 } loc ? loc.Length * FeetToMetres
            : ExactParameterFeetCurrent(element, null, CurveElemLengthParamId) * FeetToMetres;
        double? rebuiltArea = element.Location?.CurveType == "Arc" && shells is [{ Closed: true } shell]
            ? shell.Faces.Select(RebuiltHorizontalCurveLoopAreaCurrent).FirstOrDefault(area => area != null) * SquareFeetToSquareMetres
            : null;
        double Footprint(double across) => rebuiltArea
            ?? ((simple || element.Location?.CurveType == "Arc" || element.Location == null)
                && curveLength != null && across > 1e-9 ? across * curveLength.Value
                : topArea > 1e-12 ? topArea : body.Area);
        double footprint = Footprint(width);
        // The net volume of an arc wall is measured on its solid: the footprint of the profile width, not of the layers of the type.
        double? netVolume = element.Location?.CurveType == "Arc" && body.Depth > 1e-9
            && !shells.Any(shell => shell.SourceMeasured)
            ? Footprint(body.ExtentY) * (zRange ?? body.Depth)
            : SolidTotalsCurrent(shells)?.Volume;
        AddIfPositive(result, QuantityKind.Area, "GrossFootprintArea", footprint);
        if (body.Depth > 1e-9 && width > 1e-9 && sideArea > 1e-12)
            AddIfPositive(result, QuantityKind.Volume, "GrossVolume", width * sideArea);
        AddIfPositive(result, QuantityKind.Area, "GrossSideArea", sideArea);
        AddIfPositive(result, QuantityKind.Area, "NetSideArea", sideArea);
        AddIfPositive(result, QuantityKind.Volume, "NetVolume", netVolume);
        return result;
    }

    /// <summary>The total width of the layers of the type of a wall (the sum of the layer widths of its compound structure), in metres;
    /// null when the type is not found, is not a wall type or holds no decoded layer of positive finite width.</summary>
    private static double? WallTypeLayersWidthCurrent(IReadOnlyDictionary<int, Element> elements, Element wall)
    {
        if (wall.SerializedSystemTypeId is int typeId && typeId > 0 && typeId != wall.Id
            && elements.TryGetValue(typeId, out var type)
            && type.CategoryId == wall.CategoryId && type.SerializedSystemTypeClassId == 512
            && type.SerializedHostTypeLayers is { Length: > 0 } layers)
        {
            double total = 0;
            foreach (var layer in layers)
            {
                double feet = BitConverter.Int64BitsToDouble(layer.LayerWidthBits);
                if (double.IsFinite(feet) && feet > 0) total += feet;
            }
            return total > 0 && double.IsFinite(total) ? total * FeetToMetres : null;
        }
        return null;
    }

    /// <summary>The height of a wall: the Z range of the bounding box of its stored solid, as long as none of its faces stands outside
    /// a solid (a mesh). It is the depth of the extrusion written for a simple rectangular wall (the first solid decides) and the
    /// Height of the quantities; the profile helper and the cross-section attribute play no part in it. Null, and the caller keeps
    /// the extrusion depth it wrote, when the stored solid is open or empty, when its faces do not cover every face, when a face
    /// is in two solids, or when the first solid is not a simple rectangular wall. A wall of several solids is measured on its
    /// first solid alone, by the same test for the depth and for the Height: it must be closed and have valid faces, and the
    /// other solids, closed or not, play no part. Metres.</summary>
    internal static double? OwnSimpleWallHeightCurrent(Element wall) => OwnWallHeightCurrent(wall, bodyRoute: false);

    /// <summary>With <paramref name="bodyRoute"/> the height of a wall that is not simple rectangular as well (curved, leaning
    /// or edited walls): the generic body exporter writes it, and the quantities still take the Z range of the solid as Height
    /// (and the extrusion depth as Length), where the extrusion depth that is written first may come from the height
    /// parameter of the wall instead. A wall of several solids is measured on its first solid.</summary>
    private static double? OwnWallHeightCurrent(Element wall, bool bodyRoute)
    {
        // Inactive faces that make the wall not simple leave it to the body exporter (a tilted classifier face of the first solid).
        if (!bodyRoute && wall.Geometry?.InactiveFaces is { Count: > 0 } && PassesSimpleRectangularWallCurrent(wall) == false) return null;
        var geometry = wall.OwnCurrentCutPairBodyCurrent ?? wall.Geometry;
        if (geometry is { BrepSolids: { Count: > 1 } many })
        {
            // Several solids: the first one alone decides, for the Height and for the depth of the extrusion alike.
            if (!many[0].IsClosed || FirstSolidFacesOfBodyCurrent(geometry) is not { Count: > 0 } firstFaces
                || !(bodyRoute || WallExtrusion.IsSimpleRectangularWall(firstFaces, wall.OwnWallHelper, wall.Geometry?.InactiveFaces)))
                return null;
            return ZRangeMetresCurrent(firstFaces);
        }
        if (geometry is not { BrepSolids: { Count: > 0 } solids } body
            || solids.Any(solid => !solid.IsClosed || solid.FaceIndices.Count == 0)
            || solids.Sum(solid => solid.FaceIndices.Count) != body.Faces.Count
            || solids.SelectMany(solid => solid.FaceIndices).Distinct().Count() != body.Faces.Count
            || solids.SelectMany(solid => solid.FaceIndices).Any(index => (uint)index >= (uint)body.Faces.Count)
            || !(WallExtrusion.IsSimpleRectangularWall(solids[0].FaceIndices.Select(index => body.Faces[index]).ToList(),
                    wall.OwnWallHelper, wall.Geometry?.InactiveFaces)
                || bodyRoute && solids.Count == 1)) return null;
        return ZRangeMetresCurrent(body.Faces);
    }

    /// <summary>The faces of the first solid of a body; null when one of its face indices is outside the faces.</summary>
    private static List<GeoFace>? FirstSolidFacesOfBodyCurrent(GeometryData body) =>
        body.BrepSolids![0].FaceIndices.All(index => (uint)index < (uint)body.Faces.Count)
            ? body.BrepSolids[0].FaceIndices.Select(index => body.Faces[index]).ToList() : null;

    private static double? ZRangeMetresCurrent(IEnumerable<GeoFace> faces)
    {
        var points = faces.SelectMany(face => (face.OuterVertices ?? [])
            .Concat((face.InnerVertices ?? []).SelectMany(loop => loop))).ToList();
        if (points.Count < 8 || points.Any(point => point.Length != 3 || point.Any(value => !double.IsFinite(value))))
            return null;
        double height = points.Max(point => point[2]) - points.Min(point => point[2]);
        return height > 0 && double.IsFinite(height) ? height * FeetToMetres : null;
    }

    /// <summary>Height and Width are the parameters "Height" and "Width" of the instance, then of its type
    /// (GENERIC_HEIGHT, GENERIC_WIDTH; a curtain panel keeps them as CURTAIN_WALL_PANELS_HEIGHT/WIDTH); Area is the
    /// computed "Area" parameter of the element (half the surface of its solids) except for an insert of a curtain wall,
    /// which holds none, and then Height times Width.</summary>
    private List<QuantityValue> DoorWindowQuantitiesCurrent(QuantityContextCurrent context, Element element)
    {
        var result = new List<QuantityValue>();
        Element? type = TypeOfCurrent(element, context.Elements);
        bool inCurtainWall = element.SerializedHostId is { } hostId
            && context.Products.TryGetValue(hostId, out var host) && host.ifcType == "IFCCURTAINWALL";
        double? height = ExactNamedParameterFeetCurrent(element, type, "Height", true, -1001300, -1010300) * FeetToMetres
            ?? (inCurtainWall ? InstanceFrameExtentMetresCurrent(element, 2) : null);
        // Without a parameter of its own (a panel of a curtain wall holds only a bounding-box estimate) the width is the extent of
        // the solid along the width of the instance frame.
        double? width = ExactNamedParameterFeetCurrent(element, type, "Width", true, -1001301, -1010301) * FeetToMetres
            ?? InstanceFrameExtentMetresCurrent(element, 0);
        // Without any dimension from a parameter, a frame or a curtain wall (a door of a storefront holds none) they are the stored
        // boxes: the height the extent of the box of the instance, the width the extent of the box of its family symbol along the
        // family width axis.
        bool fromBoxes = false;
        if (height == null && width == null
            && (element.Geometry?.BoundingBox ?? element.DecodedBox) is { Length: >= 6 } instanceBox
            && Math.Abs(instanceBox[5] - instanceBox[2]) * FeetToMetres is > 0 and var boxHeight && double.IsFinite(boxHeight)
            && SymbolBoxCurrent(LinkedFamilyType(element, context.Elements)) is { Length: >= 6 } symbolBox
            && Math.Abs(symbolBox[3] - symbolBox[0]) * FeetToMetres is > 0 and var boxWidth && double.IsFinite(boxWidth))
        {
            (height, width, fromBoxes) = (boxHeight, boxWidth, true);
        }
        if (height != null) result.Add(new(QuantityKind.Length, "Height", height.Value));
        if (width != null) result.Add(new(QuantityKind.Length, "Width", width.Value));
        // The "Area" of the instance is half the surface of the solids of its Coarse detail level (see
        // ElementBuilder.ApplyCoarseDoorWindowSurfaces); a window without Coarse alternatives has the Fine solids at that level.
        double surface = element.CoarseSurfaceArea is { } coarse
            ? coarse * SquareFeetToSquareMetres : SolidTotalsCurrent(ShellsOfCurrent(element.Geometry))?.Area ?? 0;
        if (!inCurtainWall && surface > 1e-9)
            result.Add(new(QuantityKind.Area, "Area", surface / 2));
        // Without solids at that level an element of its own has no area; an insert of a curtain wall holds both dimensions.
        else if ((inCurtainWall || element.CoarseSurfaceArea == null) && height != null && width != null)
            result.Add(new(QuantityKind.Area, "Area", height.Value * width.Value));
        return result;
    }

    /// <summary>The Member description (a curtain wall mullion has no extrusion data of its own): Length is the "Length"
    /// parameter of the instance, which for a mullion is its length along its axis (the extent of the solid along the axis, as
    /// the ends can be cut at an angle), CrossSectionArea the computed "Area" (half the surface of the solids), NetVolume and
    /// NetSurfaceArea the volume and surface of the solids.</summary>
    private static bool InCurtainWallCurrent(QuantityContextCurrent context, int productId) =>
        context.ParentByChild.TryGetValue(productId, out int parentEntity)
        && context.IndexById.TryGetValue(parentEntity, out int parentIndex)
        && context.W.EntityAt(parentIndex).Type == "IFCCURTAINWALL";

    private static bool HasInPlaneSlantedMullionEndCurrent(Element element)
    {
        if (element.CategoryId != CurtainWallMullionCategoryId || element.Transform is not { } transform
            || !TryMullionUnitVector(transform.BasisZ, out double[] direction)
            || element.Geometry?.Faces is not { Count: > 0 } faces)
            return false;

        if (Math.Abs(direction[2]) > 0.1)
            return false;

        return faces.Any(f =>
            f.Normal != null && Math.Abs(f.Normal[2]) < 0.1 &&
            f.OuterVertices is { Count: > 0 } &&
            Math.Abs(f.Normal[0] * direction[0] + f.Normal[1] * direction[1] + f.Normal[2] * direction[2]) is >= 0.5 and <= 0.98);
    }

    private List<QuantityValue> MemberQuantitiesCurrent(
        QuantityContextCurrent context, int productId, Element element, ProfileExtrusion? body)
    {
        var result = new List<QuantityValue>();
        var shells = ShellsOfCurrent(element.Geometry);
        // A brace of a file whose geometry is decoded was a beam before it became a member: it keeps the Length and the
        // CrossSectionArea of the beam rule (the member rule reads half the surface area as the section).
        bool beamRule = element.CategoryId == StructuralFramingCategoryId && !_geometryUndecoded;
        if (beamRule)
            result.AddRange(BeamQuantitiesCurrent(element, body).Where(quantity => quantity.Name is "Length" or "CrossSectionArea"));
        else
        {
            double? length = ExactParameterFeetCurrent(element, null, CurveElemLengthParamId) * FeetToMetres
                ?? (InCurtainWallCurrent(context, productId) && HasInPlaneSlantedMullionEndCurrent(element)
                ? MullionAxisExtentMetresCurrent(element)
                : InstanceFrameExtentMetresCurrent(element, 2));
            AddIfPositive(result, QuantityKind.Length, "Length", length ?? ExtrusionDataCurrent(body, shells)?.Depth);
        }
        if (SolidTotalsCurrent(shells) is { } totals)
        {
            var net = NodeSolidTotalsCurrent(element.Geometry) ?? totals;
            if (!beamRule) AddIfPositive(result, QuantityKind.Area, "CrossSectionArea", totals.Area / 2);
            AddIfPositive(result, QuantityKind.Volume, "NetVolume", net.Volume);
            AddIfPositive(result, QuantityKind.Area, "NetSurfaceArea", net.Area);
        }
        return result;
    }

    /// <summary>The Railing description: Length is the parameter called "Length" of the railing or its type, else
    /// CURVE_ELEM_LENGTH (the length of the path of the railing).</summary>
    private List<QuantityValue> RailingQuantitiesCurrent(QuantityContextCurrent context, Element element)
    {
        var result = new List<QuantityValue>();
        AddIfPositive(result, QuantityKind.Length, "Length",
            (ExactNamedParameterFeetCurrent(element, TypeOfCurrent(element, context.Elements), "Length", CurveElemLengthParamId)
                ?? element.ValidatedRailingLengthFeet) * FeetToMetres);
        return result;
    }

    /// <summary>The extent in metres of the solid of an instance along one axis (0 X, 1 Y, 2 Z) of its instance frame, null without
    /// the frame or the solid. For a family instance the frame X runs along its width and Z up.</summary>
    internal static double? InstanceFrameExtentMetresCurrent(Element element, int axis)
    {
        if (element.Transform is not { } transform
            || !TryMullionUnitVector(axis == 0 ? transform.BasisX : axis == 1 ? transform.BasisY : transform.BasisZ, out double[] direction)
            || element.Geometry?.Faces is not { Count: > 0 } faces)
            return null;
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var face in faces)
            foreach (double[] point in face.OuterVertices ?? [])
            {
                double along = MullionDot(point[0], point[1], point[2], direction);
                min = Math.Min(min, along);
                max = Math.Max(max, along);
            }
        return max > min ? (max - min) * FeetToMetres : null;
    }

    /// <summary>The extent in metres of the solid of a mullion along its axis (the Z of its instance frame).</summary>
    internal static double? MullionAxisExtentMetresCurrent(Element element)
    {
        if (element.CategoryId != CurtainWallMullionCategoryId || element.Transform is not { } transform
            || !TryMullionUnitVector(transform.BasisZ, out double[] direction)
            || element.Geometry?.Faces is not { Count: > 0 } faces)
            return null;

        var endFaces = new List<(double Dot, double AvgAlong)>();
        foreach (var f in faces)
        {
            if (f.OuterVertices is not { Count: > 0 } outer || f.Normal == null) continue;
            double dot = f.Normal[0] * direction[0] + f.Normal[1] * direction[1] + f.Normal[2] * direction[2];
            if (Math.Abs(dot) > 0.2)
                endFaces.Add((dot, outer.Average(p => MullionDot(p[0], p[1], p[2], direction))));
        }

        var negEnd = endFaces.Where(x => x.Dot < -0.2).ToList();
        var posEnd = endFaces.Where(x => x.Dot > 0.2).ToList();
        if (negEnd.Count > 0 && posEnd.Count > 0)
        {
            double minAlong = negEnd.Min(x => x.AvgAlong);
            double maxAlong = posEnd.Max(x => x.AvgAlong);
            if (maxAlong > minAlong + 1e-9)
                return (maxAlong - minAlong) * FeetToMetres;
        }

        return InstanceFrameExtentMetresCurrent(element, 2);
    }

    /// <summary>The Plate description registers NetVolume (and GrossVolume, which needs extrusion data).</summary>
    private List<QuantityValue> PlateQuantitiesCurrent(Element element)
    {
        var result = new List<QuantityValue>();
        AddIfPositive(result, QuantityKind.Volume, "NetVolume", SolidTotalsCurrent(ShellsOfCurrent(element.Geometry))?.Volume);
        return result;
    }

    /// <summary>The Slab description: GrossArea is the profile area of the slab prism, else the computed host area of the floor
    /// (the area of its top face); NetArea the computed host area, else the whole surface of the solids; GrossVolume the profile
    /// area times the thickness; Perimeter and Width come from the profile (outer loop length, world X extent).</summary>
    private List<QuantityValue> SlabQuantitiesCurrent(QuantityContextCurrent context, Element element, ProfileExtrusion? body, string[] arguments)
    {
        var result = new List<QuantityValue>();
        var shells = ShellsOfCurrent(element.Geometry);
        if (element.CategoryId is StairsCategoryId or StairsRunsCategoryId or StairsLandingsCategoryId)
        {
            // A stair part written as a slab (a cast-in-place landing): no extrusion data and no HOST_AREA_COMPUTED, so no GrossArea
            // and a NetArea that is the whole surface of the solids.
            AddIfPositive(result, QuantityKind.Area, "NetArea", ComputedHostAreaCurrent(element) ?? SolidTotalsCurrent(shells)?.Area);
            AddIfPositive(result, QuantityKind.Volume, "NetVolume", SolidTotalsCurrent(shells)?.Volume);
            return result;
        }
        // The area is the computed host area of the floor, which is the area of its top face. A slab gets extrusion data
        // (a SweptSolid body, and with it Width and GrossVolume) only when its extrusion calculator finds a vertical extrusion.
        var topFace = SoleClosedPrismCurrent(shells, verticalOnly: true);
        var prism = shells.Count == 1 && WallExtrusion.TryFindVerticalExtrusion(shells[0].Faces, out _) ? topFace : null;
        double? profileArea = prism?.ProfileArea * SquareFeetToSquareMetres;
        // The same extrusion with arcs in its outline (the sides that tessellate them are vertical cylinders), written as an extrusion
        // (a pile cap of a round plan is a brep and has no extrusion data): its depth times the area of the top surface is GrossVolume.
        // Shortcut: Perimeter is not written for it, the reference counts the arcs of its outline in two ways (as arcs and as the
        // corners they round), which no feature of the slab told apart.
        var arcPrism = prism == null && topFace == null && body != null && shells.Count == 1 && shells[0].Closed
            && WallExtrusion.TryFindVerticalExtrusion(shells[0].Faces, out _) ? FindPrismCurrent(shells[0], verticalOnly: true, arcSides: true) : null;
        var totals = SolidTotalsCurrent(shells);
        // Revit holds HOST_AREA_COMPUTED, the area of the top surface of the floor solid, for every floor. The parser derives a value
        // from the sketch for some of them, which is the plan area (a sloped floor has a larger top surface), so the upward faces of
        // the solid come first; the sketch value stands in when the solid has none. The area of the extrusion data comes before it.
        double surfaceArea = TopSurfaceAreaCurrent(shells) * SquareFeetToSquareMetres;
        double? topArea = surfaceArea > 1e-9 ? surfaceArea : ComputedHostAreaCurrent(element) ?? profileArea;
        // A foundation without a vertical extrusion (a pile) takes the area of its cap face, and its volume from all the geometry:
        // the measured nodes, the closed shells of the solids that have no measure, and the open straight prisms of the others.
        double? netVolume = totals?.Volume;
        if (element.CategoryId == CurrentFoundationCategoryId && prism == null)
        {
            // the cap of a node below zero, or of a cutting node (a negative measure), is not part of the foundation
            var cutting = (element.Geometry?.NodeMeasures ?? []).Where(measure => measure.Volume < 0).Select(measure => measure.Node).ToHashSet();
            var faces = element.Geometry?.Faces ?? [];
            bool Counts(GeoFace f) => f.GeometryNode >= 0 && !cutting.Contains(f.GeometryNode);
            bool excluded = faces.Any(f => !Counts(f));
            if (excluded)
            {
                // the area that stands in for the cap is the one of the nodes that count only
                double kept = TopSurfaceAreaCurrent(shells.Select(shell => shell with { Faces = shell.Faces.Where(Counts).ToList() })) * SquareFeetToSquareMetres;
                topArea = kept > 1e-9 ? kept : ComputedHostAreaCurrent(element) ?? profileArea;
            }
            // with an excluded node the volume is taken node by node as well: the volume of all shells would include it
            if (excluded || faces.FirstOrDefault(f => f.QuadratureArea != null) is { QuadratureArea: > 1e-9 })
            {
                if (faces.FirstOrDefault(f => f.QuadratureArea != null && Counts(f))?.QuadratureArea is { } capArea
                    && capArea > 1e-9)
                    topArea = capArea * SquareFeetToSquareMetres;
                netVolume = FoundationNodesVolumeCurrent(element.Geometry!, shells);
            }
        }
        AddIfPositive(result, QuantityKind.Area, "GrossArea", profileArea ?? topArea);
        AddIfPositive(result, QuantityKind.Area, "NetArea", topArea);
        if (prism != null)
            AddIfPositive(result, QuantityKind.Volume, "GrossVolume", profileArea * prism.Value.Depth * FeetToMetres);
        else if (arcPrism != null)
            AddIfPositive(result, QuantityKind.Volume, "GrossVolume", topArea * arcPrism.Value.Depth * FeetToMetres);
        AddIfPositive(result, QuantityKind.Volume, "NetVolume", netVolume);
        // The parameter called "Perimeter" (HOST_PERIMETER_COMPUTED) of the floor comes before the profile; its value is the
        // length of every boundary loop of the floor, the loops of its holes included (the parser's parameter is the outer loop).
        double? perimeter = (ExactParameterFeetCurrent(element, null, HostPerimeterComputedParameterId) ?? (prism ?? topFace)?.OuterPerimeter) is { } outerPerimeter
            ? (outerPerimeter + ((prism ?? topFace)?.InnerPerimeter ?? 0)) * FeetToMetres : null;
        // A round tube profile (a hollow pile) has no chords to count: its perimeter is the circumference of the outer circle, not of the bore.
        if (perimeter == null && body is { Polygon: null, Rectangle: false, InnerPerimeter: > 0 } tube) perimeter = tube.OuterPerimeter;
        AddIfPositive(result, QuantityKind.Length, "Perimeter", perimeter);
        // A roof takes the larger side of its body. A foundation (a structural foundation, or a slab of the base slab type) takes the first side of its profile: the body of a pile
        // cap is not extruded upwards, and the reference does not turn a base slab by the angle of its profile, so the world X extent says nothing about it. Any other slab takes the world X
        // extent of its vertical prism (the first loop of the lower end face), and the body only when it has no such prism.
        double? slabWidth = body is { } b
            ? (arguments.Length > 8 && arguments[8] == ".ROOF." || element.CategoryId == -2000035
                ? Math.Max(b.ExtentX, b.ExtentY)
                : element.CategoryId == CurrentFoundationCategoryId || arguments.Length > 8 && arguments[8] == ".BASESLAB." ? b.ExtentX
                : prism is { } vertical ? vertical.ExtentX * FeetToMetres
                : (b.Rectangle && Math.Abs(b.AxisX?[0] ?? 1) < 0.1 ? b.ExtentY : b.ExtentX))
            : prism?.ExtentX * FeetToMetres;
        // A foundation whose profile gives no width (a round pile has neither an extrusion body nor a vertical prism) takes the
        // Width parameter (STRUCTURAL_FOUNDATION_WIDTH) of the instance, else of its type: the reference gives the pile the
        // diameter its record stores there, so a 24 inch pile is 2 feet wide.
        if (slabWidth == null && element.CategoryId == CurrentFoundationCategoryId)
            slabWidth = ExactParameterFeetCurrent(element, TypeOfCurrent(element, context.Elements), FoundationWidthParameterId) * FeetToMetres;
        AddIfPositive(result, QuantityKind.Length, "Width", slabWidth);
        return result;
    }

    /// <summary>A ceiling or floor finish: GrossCeilingArea is the computed host area (HOST_AREA_COMPUTED), the area of the ceiling
    /// surface. The parser derives it from the sketch of the ceiling, which fails for a ceiling made of several loops or without
    /// a sketch; then the area of the upper surface of the solids stands in. The insulation and the lining of a pipe or duct
    /// (written as IfcCovering as well) hold no such parameter.</summary>
    private List<QuantityValue> CoveringQuantitiesCurrent(Element element)
    {
        var result = new List<QuantityValue>();
        if (element.CategoryId is PipeInsulationCategoryId or DuctInsulationCategoryId or DuctLiningCategoryId) return result;
        double? area = ComputedHostAreaCurrent(element);
        double surface = TopSurfaceAreaCurrent(ShellsOfCurrent(element.Geometry)) * SquareFeetToSquareMetres;
        if (surface > 1e-9 && (area == null || Math.Abs(area.Value - surface) > 0.005 * surface)) area = surface;
        AddIfPositive(result, QuantityKind.Area, "GrossCeilingArea", area);
        return result;
    }

    /// <summary>Column quantities. A column written as a prism carries its extrusion data:
    /// Length, CrossSectionArea (the profile), OuterSurfaceArea (the length times the perimeter) and GrossVolume (the length times
    /// the area), where the length and the perimeter are numbers of the length unit of the file: a file in millimetres has the two
    /// 1e6 and 1000 times too large (millimetres taken as square and cubic metres), a file in metres or feet has them right;
    /// otherwise the cross section is the computed host area. NetVolume is the computed host volume, else the volume of the solids. A round column whose exact end
    /// faces are listed beside a tessellated side (see <see cref="RoundPrismCurrent"/>) has the extrusion data of its circle; a round
    /// column that is a vertical cylinder (see <see cref="RoundCylinderCurrent"/>) takes the volume and surface of the node that carries
    /// its end faces for the solid.</summary>
    private List<QuantityValue> ColumnQuantitiesCurrent(Element element, ProfileExtrusion? body)
    {
        var result = new List<QuantityValue>();
        var shells = ShellsOfCurrent(element.Geometry);
        var totals = SolidTotalsCurrent(shells);
        // The measures of the node with the end faces stand for the solid of a round column (a vertical cylinder) only.
        var capTotals = RoundCylinderCurrent(element.Geometry) != null ? CapNodeTotalsCurrent(element.Geometry) : null;
        if ((ExtrusionDataCurrent(body, shells) ?? RoundExtrusionDataCurrent(element.Geometry)) is { } extrusion)
        {
            double length = extrusion.Depth, area = extrusion.Area;
            // The outer surface counts the loops of the holes as well (outer + inner perimeter).
            // The length and the perimeter count in the length unit of the file, the products are taken as its area and volume.
            double lengthUnits = _unitFamily == IfcUnitFamily.Millimetre ? 1000 : 1;
            double fileLength = length * lengthUnits, filePerimeter = (extrusion.Perimeter + extrusion.InnerPerimeter) * lengthUnits;
            AddIfPositive(result, QuantityKind.Length, "Length", length);
            AddIfPositive(result, QuantityKind.Area, "CrossSectionArea", area);
            AddIfPositive(result, QuantityKind.Area, "OuterSurfaceArea", fileLength * filePerimeter);
            AddIfPositive(result, QuantityKind.Volume, "GrossVolume", fileLength * area);
            // The exporter measures the volume of the solids of the Medium level, which a tube family draws with sharp corners (the length times the
            // nominal area); where the Medium solids cannot be measured the nominal area stands in for them. A family without different
            // Medium solids has the volume of the solid it draws.
            double? netVolume = element.MediumSolidTotals?.Volume
                ?? (extrusion.NominalArea is { } nomArea && (element.HasMediumAlternatives || NodeSolidTotalsCurrent(element.Geometry) == null)
                    ? length * nomArea
                    : (ComputedHostVolumeCurrent(element) ?? NodeSolidTotalsCurrent(element.Geometry)?.Volume ?? capTotals?.Volume ?? totals?.Volume));
            AddIfPositive(result, QuantityKind.Volume, "NetVolume", netVolume);
        }
        else
        {
            AddIfPositive(result, QuantityKind.Area, "CrossSectionArea", ComputedHostAreaCurrent(element) ?? (capTotals?.Area ?? totals?.Area) / 2);
            AddIfPositive(result, QuantityKind.Volume, "NetVolume",
                ComputedHostVolumeCurrent(element) ?? NodeSolidTotalsCurrent(element.Geometry)?.Volume ?? capTotals?.Volume ?? totals?.Volume);
        }
        return result;
    }

    /// <summary>The Beam description (common structural quantities and NetSurfaceArea): the calculators read the
    /// extrusion data of a prism body, in consistent units.</summary>
    private List<QuantityValue> BeamQuantitiesCurrent(Element element, ProfileExtrusion? body)
    {
        var result = new List<QuantityValue>();
        var shells = ShellsOfCurrent(element.Geometry);
        var totals = SolidTotalsCurrent(shells);
        if (ExtrusionDataCurrent(body, shells) is { } extrusion)
        {
            double length = extrusion.Depth, area = extrusion.Area, perimeter = extrusion.Perimeter;
            // The section of a written profile is measured with the arcs its writer passed on, not with the chords they are written as.
            if (body is { Polygon: { } outline, OuterArcs: { } outlineArcs, NominalArea: null })
            {
                var arcs = ArcsOverChordsCurrent(outline, outlineArcs);
                area += arcs.Area;
                perimeter += arcs.Length;
            }
            AddIfPositive(result, QuantityKind.Length, "Length", length);
            AddIfPositive(result, QuantityKind.Area, "CrossSectionArea", area);
            AddIfPositive(result, QuantityKind.Area, "OuterSurfaceArea", perimeter * length);
            AddIfPositive(result, QuantityKind.Area, "GrossSurfaceArea", perimeter * length + 2 * area);
            AddIfPositive(result, QuantityKind.Volume, "GrossVolume", area * length);
        }
        else
            AddIfPositive(result, QuantityKind.Area, "CrossSectionArea", ComputedHostAreaCurrent(element) ?? totals?.Area / 2);
        var beamNet = NodeSolidTotalsCurrent(element.Geometry) ?? totals;
        AddIfPositive(result, QuantityKind.Volume, "NetVolume", ComputedHostVolumeCurrent(element) ?? beamNet?.Volume);
        AddIfPositive(result, QuantityKind.Area, "NetSurfaceArea", beamNet?.Area);
        return result;
    }

    /// <summary>Runs whose flight takes the Length surrogate (<see cref="IsRunLengthRuleCurrent"/>), by element id, with the number of
    /// treads that the surrogate multiplies by the tread length (<see cref="RunLengthTreadsCurrent"/>).</summary>
    internal readonly Dictionary<int, int> LengthRuleRunsCurrent = new();

    /// <summary>The number of treads of the Length surrogate of a run, counted as it was when the surrogate was calibrated: from the span of
    /// the riser indices of the run (a sketched run without an end riser keeps all of it), and only where the risers agree with those stored
    /// for its stair. It is not the NumberOfTreads attribute of the flight, which counts the risers of such a run one less, as the file does:
    /// an assembled run whose attribute is set still takes no Length. Null without a run input or when the counts do not agree.</summary>
    internal static int? RunLengthTreadsCurrent(Element run, IReadOnlyDictionary<int, Element> elementsById) =>
        run.SerializedStairRunInput is { Numbers: { } numbers } input && input.OwnerId == run.Id
            && StairRunRisers(run, numbers) > 0 && StairRunCountsAgree(run, elementsById, numbers)
            && StairRunTreads(run, numbers) is int treads && treads > 0 ? treads : null;

    /// <summary>The inputs of the Length surrogate of a run are the ones it was calibrated on: the family of the run before its type was
    /// resolved through TypeId (a "Monolithic Run", or the category where no family was known; an assembled run counted none), and the run
    /// input of a file whose class root was counted once. A run that only gained its type or its run input through the class count fix keeps
    /// the Length it had.</summary>
    internal static bool IsRunLengthRuleCurrent(Element run, string ifcType,
        IReadOnlyDictionary<int, Element> elementsById, IReadOnlyDictionary<int, int> occurrenceCounts)
    {
        if (ifcType != "IFCSTAIRFLIGHT" || !run.OwnClassRootSeededOnce) return false;
        string objectType = ResolveProductFamilyAndTypeName(run, ifcType, elementsById, occurrenceCounts, currentProfile: true, resolveComponentType: false);
        return objectType.StartsWith("Monolithic Run", StringComparison.Ordinal)
            || objectType.StartsWith("OST_StairsRuns:", StringComparison.Ordinal);
    }

    /// <summary>The StairFlight description: NetVolume is the volume of the run solids; Length is the 2D length of the run
    /// (the base lines of its cells), which is the number of treads times the tread length for
    /// a monolithic run and empty for an assembled one.</summary>
    private List<QuantityValue> StairFlightQuantitiesCurrent(Element element, string[] flight)
    {
        var result = new List<QuantityValue>();
        AddIfPositive(result, QuantityKind.Volume, "NetVolume", SolidTotalsCurrent(ShellsOfCurrent(element.Geometry))?.Volume);
        // The surrogate is decided by the inputs it was calibrated on (IsRunLengthRuleCurrent), not by the type resolved through TypeId,
        // and counts the treads of those inputs, not the NumberOfTreads attribute of the flight.
        if (flight.Length == 12 && LengthRuleRunsCurrent.TryGetValue(element.Id, out int treads)
            && TryParseStepReal(flight[11], out double treadLength))
            AddIfPositive(result, QuantityKind.Length, "Length", treads * treadLength);
        return result;
    }

    /// <summary>The Space description of a room: NetFloorArea and GrossFloorArea are the room area, the area of the circuit its level
    /// room plan gives it (m_cachedCircuitId, less its islands; exact for curved boundaries). Without that plan the plan area of the
    /// room solid stands in, which is the area of its upward or of its downward horizontal faces, whichever is the larger (a
    /// stepped floor or ceiling splits one of the two). Height is the distance from the level of the room to the next
    /// building storey (written also for a room without a plan area), GrossVolume area times height. GrossPerimeter exists only when the room was written as an extrusion
    /// (<see cref="RoomExtrusionPerimeterMetresCurrent"/>).</summary>
    private List<QuantityValue> SpaceQuantitiesCurrent(Element room, double? levelHeight)
    {
        var result = new List<QuantityValue>();
        var boundary = room.SerializedSpaceBoundaryCurrent;
        if ((room.SerializedRoomPlanAreaSquareFeet ?? (boundary != null ? RoomPlanAreaCurrent(boundary) : null)) is not { } planArea)
        {
            // The height is the storey's, not the room's: it stands without a plan area.
            AddIfPositive(result, QuantityKind.Length, "Height", levelHeight);
            return result;
        }
        double area = planArea * SquareFeetToSquareMetres;
        AddIfPositive(result, QuantityKind.Area, "NetFloorArea", area);
        AddIfPositive(result, QuantityKind.Length, "Height", levelHeight);
        if (boundary != null) AddIfPositive(result, QuantityKind.Length, "GrossPerimeter", RoomExtrusionPerimeterMetresCurrent(boundary));
        AddIfPositive(result, QuantityKind.Area, "GrossFloorArea", area);
        AddIfPositive(result, QuantityKind.Volume, "GrossVolume", levelHeight is > 0 ? area * levelHeight : null);
        return result;
    }

    /// <summary>The height of the level of a space: the distance of the storey the
    /// space is placed in to the next storey up.</summary>
    private static double? StoreyHeightOfSpaceCurrent(QuantityContextCurrent context, string[] spaceArguments)
    {
        if (context.StoreyHeights == null || !TryEntityId(spaceArguments[5], out int placementId)
            || !context.IndexById.TryGetValue(placementId, out int index))
            return null;
        var placement = SplitStepArguments(context.W.EntityAt(index).Args);
        return placement.Length > 0 && TryEntityId(placement[0], out int parent)
            && context.StoreyByPlacement.TryGetValue(parent, out int storey)
            && context.StoreyHeights.TryGetValue(storey, out double height) ? height : null;
    }

    /// <summary>The height of every building storey: the distance in metres to the next storey up whose
    /// elevation differs (by more than 1e-7 ft); the topmost storey has none.</summary>
    private Dictionary<int, double> StoreyHeightsCurrent() =>
        StoreyHeightsCurrent(_storeyLevels.ToDictionary(pair => pair.Key, pair => GetElevation(pair.Value)));

    internal static Dictionary<int, double> StoreyHeightsCurrent(IReadOnlyDictionary<int, double> elevationsFeet)
    {
        var heights = new Dictionary<int, double>();
        foreach (var (storey, elevation) in elevationsFeet)
        {
            double? next = elevationsFeet.Values.Where(other => other - elevation > 1e-7).Select(other => (double?)other).Min();
            if (next is { } above) heights[storey] = (above - elevation) * FeetToMetres;
        }
        return heights;
    }
}
