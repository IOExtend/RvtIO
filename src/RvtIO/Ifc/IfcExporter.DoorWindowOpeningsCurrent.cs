using RvtIO.Model;

namespace RvtIO.Ifc;

/// <summary>
/// The opening of a door or window in a straight wall the way the current profile makes it: from the cut loop the file stores
/// with the family symbol (<c>InsertableObj.m_pCutLoops</c>), placed in the frame of the wall and extruded across its whole
/// width.
/// </summary>
public partial class IfcExporter
{
    /// <summary>A placement with a position: the point and the axes of the frame, in the coordinates of its parent.</summary>
    private readonly record struct PlacementCurrent(double[] Origin, FrameCurrent Axes)
    {
        public double[] ToWorld(double[] p) => Sum(Origin, Axes.Apply(p));
        public double[] ToLocal(double[] q) => Axes.ToLocal([q[0] - Origin[0], q[1] - Origin[1], q[2] - Origin[2]]);
        private static double[] Sum(double[] a, double[] b) => [a[0] + b[0], a[1] + b[1], a[2] + b[2]];
    }

    private static PlacementCurrent? ModelPlacementCurrent(StepWriter w, Dictionary<int, int> indexById, string reference, int depth = 0)
    {
        if (depth > 12 || !TryEntityId(reference, out int id) || !indexById.TryGetValue(id, out int index)
            || w.EntityAt(index).Type != "IFCLOCALPLACEMENT")
            return null;
        var arguments = SplitStepArguments(w.EntityAt(index).Args);
        if (arguments.Length != 2) return null;
        if (arguments[0] == "$") return new PlacementCurrent([0, 0, 0], FrameCurrent.Identity);
        if (!TryEntityId(arguments[1], out int axisId) || !indexById.TryGetValue(axisId, out int axisIndex)
            || SplitStepArguments(w.EntityAt(axisIndex).Args) is not { Length: 3 } axis
            || !TryReadPointCurrent(w, indexById, axis[0], out var origin) || origin.Length != 3
            || Axis3FrameCurrent(w, indexById, arguments[1]) is not { } relative
            || ModelPlacementCurrent(w, indexById, arguments[0], depth + 1) is not { } parent)
            return null;
        return new PlacementCurrent(parent.ToWorld(origin), relative.In(parent.Axes));
    }

    private static double[] Cross3(double[] a, double[] b) => CrossCurrent(a, b);

    /// <summary>
    /// Re-expresses the openings of the doors and windows of straight walls from the cut loops of their family symbols. Returns
    /// the number of openings written again.
    /// </summary>
    internal int ApplyCurrentDoorWindowOpenings(StepWriter w, IReadOnlyDictionary<int, Element> elementsById,
        IReadOnlyDictionary<int, int> productElementIdByEntity)
    {
        var indexById = new Dictionary<int, int>(w.EntityCount);
        for (int i = 0; i < w.EntityCount; i++) indexById[w.EntityAt(i).Id] = i;
        var fillingByOpening = new Dictionary<int, int>();
        var hostByOpening = new Dictionary<int, int>();
        var psetByProduct = new Dictionary<int, int>();
        for (int i = 0; i < w.EntityCount; i++)
        {
            var (_, type, args) = w.EntityAt(i);
            if (type is not ("IFCRELFILLSELEMENT" or "IFCRELVOIDSELEMENT" or "IFCRELDEFINESBYPROPERTIES")) continue;
            var attributes = SplitStepArguments(args);
            if (attributes.Length < 6) continue;
            if (type == "IFCRELFILLSELEMENT" && TryEntityId(attributes[4], out int first) && TryEntityId(attributes[5], out int second))
                fillingByOpening[first] = second;
            else if (type == "IFCRELVOIDSELEMENT" && TryEntityId(attributes[4], out int voidHost) && TryEntityId(attributes[5], out int voidOp))
                hostByOpening[voidOp] = voidHost;
            else if (type == "IFCRELDEFINESBYPROPERTIES" && TryEntityId(attributes[5], out int psetId))
            {
                string prods = attributes[4].Trim();
                if (prods.StartsWith('(') && prods.EndsWith(')')) prods = prods[1..^1];
                foreach (string prodToken in SplitStepArguments(prods))
                    if (TryEntityId(prodToken, out int prodId)) psetByProduct[prodId] = psetId;
            }
        }

        var globals = new GlobalsCurrent(w);
        var detached = new List<int>();
        int rewritten = 0, fromParameter = 0, deepened = 0;
        var reasons = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Skip(string reason) => reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
        // The length quantities of the BaseQuantities set of an opening; a value returned for a quantity name replaces it.
        void RewriteLengthQuantities(int openingId, Func<string, string, double?> newValue)
        {
            if (!psetByProduct.TryGetValue(openingId, out int psetId)
                || !indexById.TryGetValue(psetId, out int psetIndex)
                || w.EntityAt(psetIndex).Type != "IFCELEMENTQUANTITY")
                return;
            var psetArgs = SplitStepArguments(w.EntityAt(psetIndex).Args);
            if (psetArgs.Length < 6 || psetArgs[2].Trim('\'') is not ("BaseQuantities" or "Qto_OpeningElementBaseQuantities")) return;
            string inner = psetArgs[5].Trim();
            if (inner.StartsWith('(') && inner.EndsWith(')')) inner = inner[1..^1];
            foreach (string qToken in SplitStepArguments(inner))
            {
                if (!TryEntityId(qToken, out int qId) || !indexById.TryGetValue(qId, out int qIndex)
                    || w.EntityAt(qIndex).Type != "IFCQUANTITYLENGTH")
                    continue;
                var qArgs = SplitStepArguments(w.EntityAt(qIndex).Args);
                if (qArgs.Length < 4) continue;
                string qName = qArgs[0].Trim('\'');
                if (newValue(qName, qArgs[3]) is { } value)
                {
                    string suffix = qArgs.Length >= 5 ? $",{qArgs[4]}" : "";
                    w.ReplaceArgs(qIndex, $"'{qName}','',$,{D17(value)}{suffix}");
                }
            }
        }
        foreach (var (openingId, fillingId) in fillingByOpening)
        {
            if (!hostByOpening.TryGetValue(openingId, out int hostId)
                || !indexById.TryGetValue(openingId, out int openingIndex) || !indexById.TryGetValue(hostId, out int hostIndex)
                || !productElementIdByEntity.TryGetValue(fillingId, out int hostedElementId)
                || !productElementIdByEntity.TryGetValue(hostId, out int wallElementId)
                || !elementsById.TryGetValue(hostedElementId, out var hosted) || !elementsById.TryGetValue(wallElementId, out var wall)
                || wall.Location is not { CurveType: "Line", ArcRadius: null })
            {
                Skip(hostByOpening.ContainsKey(openingId) && productElementIdByEntity.TryGetValue(hostByOpening[openingId], out int skippedWall)
                    && elementsById.TryGetValue(skippedWall, out var skippedHost) && skippedHost.Location is { CurveType: "Arc" } ? "host-arc-wall" : "host-or-hosted-unresolved");
                continue;
            }
            var opening = w.EntityAt(openingIndex);
            var wallEntity = w.EntityAt(hostIndex);
            if (opening.Type != "IFCOPENINGELEMENT" || wallEntity.Type is not ("IFCWALLSTANDARDCASE" or "IFCWALL")) { Skip("host-not-a-wall"); continue; }
            var openingAttributes = SplitStepArguments(opening.Args);
            var wallAttributes = SplitStepArguments(wallEntity.Args);
            if (openingAttributes.Length < 8 || wallAttributes.Length < 8) { Skip("short-attributes"); continue; }

            if (!TryDoorWindowOpeningCurrent(w, indexById, elementsById, hosted, wall, wallAttributes, out var built, out string failure))
            {
                Skip(failure);
                // (the box of the opening stays, but a door or window that Revit cuts with a profile of its family reaches through the whole
                // wall; one without a cut profile is cut by a void body of its own, whose depth can exceed the wall and is kept as it is)
                if (LinkedFamilyType(hosted, elementsById)?.SerializedCutLoopCount is > 0
                    && TryThroughDepthCurrent(w, indexById, elementsById, wall, wallAttributes, openingAttributes, globals, detached) is { } throughDepth)
                {
                    RewriteLengthQuantities(openingId, (qName, _) => qName == "Depth" ? throughDepth : null);
                    deepened++;
                }
                continue;
            }

            if (!TryEntityId(openingAttributes[6], out int shapeId) || !indexById.TryGetValue(shapeId, out int shapeIndex)
                || SplitStepArguments(w.EntityAt(shapeIndex).Args) is not { Length: 3 } shape
                || !TryEntityId(shape[2].Trim('(', ')').Split(',')[0], out int oldRepresentationId)
                || !indexById.TryGetValue(oldRepresentationId, out int oldRepresentationIndex)
                || SplitStepArguments(w.EntityAt(oldRepresentationIndex).Args) is not { Length: 4 } oldRepresentation
                || oldRepresentation[1] != "'Body'")
            {
                Skip("opening-without-a-body");
                continue;
            }
            int placementAxis = globals.Axis3D(built.RelativeOrigin, [0, 0, 1], [1, 0, 0]);
            int placement = w.Add("IFCLOCALPLACEMENT", $"{wallAttributes[5]},{StepWriter.Ref(placementAxis)}");
            int profile;
            if (built.IsRectangle)
            {
                var p = built.ProfileLoop;
                double dx = p[0][0] - p[3][0], dy = p[0][1] - p[3][1];
                double xDim = Math.Sqrt(dx * dx + dy * dy);
                double yx = p[2][0] - p[3][0], yy = p[2][1] - p[3][1];
                double yDim = Math.Sqrt(yx * yx + yy * yy);
                int centre = globals.Point2D((p[3][0] + p[1][0]) / 2, (p[3][1] + p[1][1]) / 2);
                int profilePlacement = w.Add("IFCAXIS2PLACEMENT2D", $"{StepWriter.Ref(centre)},{StepWriter.Ref(globals.Direction2D(dx / xDim, dy / xDim))}");
                profile = w.Add("IFCRECTANGLEPROFILEDEF",
                    $".AREA.,'',{StepWriter.Ref(profilePlacement)},{StepWriter.Real(xDim)},{StepWriter.Real(yDim)}");
            }
            else
            {
                var points = built.ProfileLoop.Select(p => globals.Point2D(p[0], p[1])).ToList();
                int curve = w.Add("IFCPOLYLINE", StepWriter.List(points.Append(points[0]).Select(StepWriter.Ref)));
                profile = w.Add("IFCARBITRARYCLOSEDPROFILEDEF", $".AREA.,'',{StepWriter.Ref(curve)}");
            }
            int position = globals.Axis3D([0, 0, 0], built.Axis, [0, 0, 1]);
            int solid = w.Add("IFCEXTRUDEDAREASOLID",
                $"{StepWriter.Ref(profile)},{StepWriter.Ref(position)},{StepWriter.Ref(globals.Direction3D(0, 0, 1))},{StepWriter.Real(built.Depth)}");
            int representation = w.Add("IFCSHAPEREPRESENTATION",
                $"{oldRepresentation[0]},'Body','SweptSolid',{StepWriter.List(StepWriter.Ref(solid))}");
            int newShape = w.Add("IFCPRODUCTDEFINITIONSHAPE", $"$,$,{StepWriter.List(StepWriter.Ref(representation))}");

            detached.Add(shapeId);
            if (TryEntityId(openingAttributes[5], out int oldPlacementId)) detached.Add(oldPlacementId);
            openingAttributes[5] = StepWriter.Ref(placement);
            openingAttributes[6] = StepWriter.Ref(newShape);
            w.ReplaceArgs(openingIndex, string.Join(",", openingAttributes));

            // (only the opening of a door or window is measured along the host axes, as in the quantity pass)
            bool hostAxes = OpeningQuantitiesInHostAxes && indexById.TryGetValue(fillingId, out int fillerIndex)
                && w.EntityAt(fillerIndex).Type is "IFCDOOR" or "IFCWINDOW";
            RewriteLengthQuantities(openingId, (qName, written) =>
            {
                // (along the host axes Height is the vertical and Width the side along the wall of the new outline. The decoded
                // cut loop is what Revit cuts, so it replaces the nominal size that the quantity pass took from the door or
                // window; where the two are the same size the nominal value stays, see OpeningQuantitiesInHostAxes)
                bool own(double cut) => hostAxes && SameSizeCurrent(written, cut);
                if (qName == "Height" && (hostAxes ? built.HostHeight : built.Height) is { } newHeight && !own(newHeight)) return newHeight;
                if (qName == "Width" && (hostAxes ? built.HostWidth : built.Width) is { } newWidth && !own(newWidth)) return newWidth;
                return qName == "Depth" ? built.Depth : null;
            });

            // OverallHeight and OverallWidth (millimetres) of the door or window are the vertical and the along-the-wall extent of the
            // cut loop, which can be larger than the nominal size; the openings that are not rewritten here keep the nominal size.
            if (indexById.TryGetValue(fillingId, out int fillerAttributeIndex) && w.EntityAt(fillerAttributeIndex).Type is "IFCDOOR" or "IFCWINDOW"
                && SplitStepArguments(w.EntityAt(fillerAttributeIndex).Args) is { Length: 10 } fillerAttributes)
            {
                // (a nominal size that is the cut size up to rounding noise keeps its written text)
                bool changed = false;
                foreach (var (slot, cutMetres) in new[] { (8, built.HostHeight), (9, built.HostWidth) })
                    if (cutMetres is { } cut && !SameSizeCurrent(fillerAttributes[slot], cut * 1000))
                    {
                        fillerAttributes[slot] = StepWriter.Real(cut * 1000);
                        changed = true;
                    }
                if (changed) w.ReplaceArgs(fillerAttributeIndex, string.Join(",", fillerAttributes));
            }

            rewritten++;
            if (built.WidthFromParameter) fromParameter++;
        }
        if (detached.Count > 0) RemoveDetachedCurrent(w, detached);
        _diag?.WriteLine($"The current profile door and window openings of {fillingByOpening.Count} filled: {rewritten} rewritten ({fromParameter} with the wall width parameter, its layers or body being stale: wall-width-conflict), {deepened} of the others given the depth of the wall; left as they were: "
            + string.Join(", ", reasons.Select(pair => $"{pair.Key}={pair.Value}")));
        return rewritten;
    }

    /// <summary>The opening placement, authored profile in its plane, extrusion axis and depth.</summary>
    private readonly record struct DoorWindowOpeningCurrent(double[] RelativeOrigin, List<double[]> ProfileLoop,
        bool IsRectangle, double[] Axis, double Depth, double Height, double Width,
        double? HostHeight, double? HostWidth, bool WidthFromParameter = false);

    private static bool TryDoorWindowOpeningCurrent(StepWriter w, Dictionary<int, int> indexById,
        IReadOnlyDictionary<int, Element> elementsById, Element hosted, Element wall, string[] wallAttributes, out DoorWindowOpeningCurrent opening,
        out string failure)
    {
        opening = default;
        failure = "";
        FamilyCutLoopData cut;
        double[] direction;
        InstanceTransform transform;
        double[]? box;
        if (hosted.OwnSingleFamilyCutout is { Transform.Length: 12 } own && own.Transform.All(double.IsFinite))
        {
            cut = own.Loop;
            direction = own.Loop.Direction;
            transform = new InstanceTransform
            {
                SymbolId = own.SymbolId, Origin = [own.Transform[9], own.Transform[10], own.Transform[11]],
                BasisX = [own.Transform[0], own.Transform[3], own.Transform[6]],
                BasisY = [own.Transform[1], own.Transform[4], own.Transform[7]],
                BasisZ = [own.Transform[2], own.Transform[5], own.Transform[8]],
            };
            box = SymbolBoxCurrent(elementsById.GetValueOrDefault(own.SymbolId));
        }
        else
        {
            var cutSymbol = LinkedFamilyCutTypes(hosted, elementsById).FirstOrDefault(type => type.FamilyCutLoops is { Count: > 0 });
            if (cutSymbol?.FamilyCutLoops is not { Count: > 0 } loops || !TryGetSerializedInstanceTransform(hosted, out var found))
            {
                // (the symbol stores cut loops that the decoder did not read, or none at all)
                failure = LinkedFamilyType(hosted, elementsById)?.SerializedCutLoopCount is > 0 ? "cut-loops-not-decoded" : "symbol-without-cut-loops";
                return false;
            }
            cut = loops.OrderByDescending(loop => LoopAreaOf(loop.Vertices)).First();
            direction = loops[0].Direction;
            transform = found;
            box = SymbolBoxCurrent(LinkedFamilyType(hosted, elementsById)) ?? SymbolBoxCurrent(cutSymbol);
        }
        if (box is not { Length: 6 } symbolBox) { failure = "no-symbol-box"; return false; }
        if (ModelPlacementCurrent(w, indexById, wallAttributes[5]) is not { } wallPlacement) { failure = "no-wall-placement"; return false; }
        if (WallTotalWidthCurrent(w, indexById, wallAttributes, wall, elementsById, out bool widthConflict) is not { } width || !(width > 0))
        {
            failure = widthConflict ? "wall-width-conflict" : "no-wall-width";
            return false;
        }
        if (cut.Vertices.Count < 3 || direction.Length < 3 || cut.Vertices.Any(v => v.Length < 3 || v.Any(c => !double.IsFinite(c)))
            || direction.Any(c => !double.IsFinite(c)))
        {
            failure = "invalid-loop-vertices";
            return false;
        }

        double[] ToModel(double[] p) =>
        [
            transform.Origin[0] * FeetToMetres + (p[0] * transform.BasisX[0] + p[1] * transform.BasisY[0] + p[2] * transform.BasisZ[0]) * FeetToMetres,
            transform.Origin[1] * FeetToMetres + (p[0] * transform.BasisX[1] + p[1] * transform.BasisY[1] + p[2] * transform.BasisZ[1]) * FeetToMetres,
            transform.Origin[2] * FeetToMetres + (p[0] * transform.BasisX[2] + p[1] * transform.BasisY[2] + p[2] * transform.BasisZ[2]) * FeetToMetres,
        ];
        double[] DirectionToModel(double[] v) =>
        [
            v[0] * transform.BasisX[0] + v[1] * transform.BasisY[0] + v[2] * transform.BasisZ[0],
            v[0] * transform.BasisX[1] + v[1] * transform.BasisY[1] + v[2] * transform.BasisZ[1],
            v[0] * transform.BasisX[2] + v[1] * transform.BasisY[2] + v[2] * transform.BasisZ[2],
        ];

        var local = cut.Vertices.Select(v => wallPlacement.ToLocal(ToModel(v))).ToList();
        double[] axis = wallPlacement.Axes.ToLocal(DirectionToModel(direction));
        double axisLength = Math.Sqrt(axis[0] * axis[0] + axis[1] * axis[1] + axis[2] * axis[2]);
        if (!(axisLength > 1e-12)) { failure = "no-axis"; return false; }
        axis = [axis[0] / axisLength, axis[1] / axisLength, axis[2] / axisLength];

        var centre = wallPlacement.ToLocal(ToModel([(symbolBox[0] + symbolBox[3]) / 2, (symbolBox[1] + symbolBox[4]) / 2, (symbolBox[2] + symbolBox[5]) / 2]));
        bool hinge = centre[1] > -1e-9;

        double plane = local[0][1];
        if (local.Any(v => Math.Abs(v[1] - plane) > 1e-6)) { failure = "loop-not-across-wall"; return false; }
        double shift = (hinge ? -width / 2 : width / 2) - plane;
        local = local.Select(v => new[] { v[0], v[1] + shift, v[2] }).ToList();
        if ((axis[1] > 0) != hinge) axis = [-axis[0], -axis[1], -axis[2]];

        double[] relative = [local.Min(v => v[0]), local.Min(v => v[1]), local.Min(v => v[2])];
        local = local.Select(v => new[] { v[0] - relative[0], v[1] - relative[1], v[2] - relative[2] }).ToList();
        bool rectangle = OthersCurrentIsRectangle(local);
        if (!rectangle) local = OthersCurrentOrient(local, axis, true);

        // the plane of the loop: X vertical, Z the direction of the cut
        double[] x = [0, 0, 1];
        double[] y = Cross3(axis, x);
        double yLength = Math.Sqrt(y[0] * y[0] + y[1] * y[1] + y[2] * y[2]);
        if (!(yLength > 1e-12)) { failure = "cut-along-vertical"; return false; }
        y = [y[0] / yLength, y[1] / yLength, y[2] / yLength];
        var projected = local.Select(v => new[] { v[0] * x[0] + v[1] * x[1] + v[2] * x[2], v[0] * y[0] + v[1] * y[1] + v[2] * y[2] }).ToList();
        double area = Enumerable.Range(0, projected.Count).Sum(i =>
            projected[i][0] * projected[(i + 1) % projected.Count][1] - projected[(i + 1) % projected.Count][0] * projected[i][1]);
        if (area == 0 || !double.IsFinite(area)) { failure = "degenerate-loop"; return false; }

        var wallLocalLoop = cut.Vertices.Select(v => wallPlacement.ToLocal(ToModel(v))).ToList();
        var (loopHeight, loopWidth) = CutLoopHeightWidthCurrent(wallLocalLoop);

        // the loop in the frame of the wall: Width along the wall, Height vertical (null for a loop without that extent)
        double? Extent(int axis)
        {
            double extent = wallLocalLoop.Max(p => p[axis]) - wallLocalLoop.Min(p => p[axis]);
            return extent > 1e-9 ? extent : null;
        }
        opening = new DoorWindowOpeningCurrent(relative, projected, rectangle, axis, width, loopHeight, loopWidth, Extent(2), Extent(0), widthConflict);
        return true;

        static double LoopAreaOf(IReadOnlyList<double[]> vertices)
        {
            double[] total = [0, 0, 0];
            for (int i = 0; i < vertices.Count; i++)
            {
                var cross = CrossCurrent(vertices[i], vertices[(i + 1) % vertices.Count]);
                total[0] += cross[0]; total[1] += cross[1]; total[2] += cross[2];
            }
            return 0.5 * Math.Sqrt(total[0] * total[0] + total[1] * total[1] + total[2] * total[2]);
        }
    }

    /// <summary>Whether a written quantity (the text of the STEP real) is the same size as <paramref name="cut"/>, up to the rounding noise
    /// of the cut loop (a relative 1e-9; a cut that is larger or smaller than the nominal size differs by far more than that).</summary>
    private static bool SameSizeCurrent(string written, double cut) =>
        double.TryParse(written, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
        && Math.Abs(value - cut) <= 1e-9 * Math.Max(Math.Abs(value), Math.Abs(cut));

    private static double[]? SymbolBoxCurrent(Element? symbol) =>
        symbol?.Geometry is { } geometry ? geometry.RootBoundingBox ?? geometry.BoundingBox : null;

    /// <summary>
    /// The total width of the layers of the host wall, metres: the layers of its type, else the width of the extrusion that is its
    /// Body (a wall with banding or other parts has a body narrower than its layers), else its width parameter. A wall whose type
    /// thickness is not validated (the type root and its compound structure read once) and whose layers (or body) disagree with its
    /// own width parameter has a stale copy among them (a file saved in several increments), so none of the two
    /// is confirmed: <paramref name="conflict"/> is set and no width is returned.
    /// </summary>
    private static double? WallTotalWidthCurrent(StepWriter w, Dictionary<int, int> indexById, string[] wallAttributes, Element wall,
        IReadOnlyDictionary<int, Element> elementsById, out bool conflict)
    {
        conflict = false;
        double? declared = ExactParameterFeetCurrent(wall, TypeOfCurrent(wall, elementsById), WallWidthParameterId) * FeetToMetres;
        double? used = (wall.ValidatedWallTypeThicknessFeet is > 0 || wall.Layers is { Count: > 0 }) && TryGetSerializedWallWidthFeet(wall, out double layersFeet)
            ? layersFeet * FeetToMetres
            : ReadBodyExtrusionCurrent(w, indexById, wallAttributes[6]) is { } body ? body.ExtentY : declared;
        if (wall.ValidatedWallTypeThicknessFeet is not > 0 && used is { } value && declared is { } stated && Math.Abs(value - stated) > WallWidthAgreementMetres)
        {
            // (the width parameter is read exactly from the wall or its type and is positive; it is the width Revit cuts through)
            conflict = true;
            return double.IsFinite(stated) ? stated : null;
        }
        return used;
    }

    /// <summary>Largest difference between the width of the layers or body of a wall and its width parameter that still counts as the same width.</summary>
    private const double WallWidthAgreementMetres = 1e-4;
}
