using RvtIO.Geometry;
using System.Globalization;
using RvtIO.Model;

namespace RvtIO.Ifc;

public partial class IfcExporter
{
    /// <summary>Pset_ColumnCommon.Slope and Pset_MemberCommon.Slope of the current profile.
    /// A column receives the extrusion slope of the body export of its body, which runs for the first instance of every mapped
    /// body only (the other instances reuse the mapped representation and carry no extrusion data) and for every instance with a
    /// body of its own: asin(|z|) of the extrusion direction, 90 degrees for a vertical extrusion; the Slope sits on the lowest
    /// element id of the members of a type that share a mapped body.
    /// The extrusion is found by <see cref="WallExtrusion.TryFindVerticalExtrusion"/> on a solid of the instance. A
    /// member that holds solids of its own (a railing support: its body is no mapped item) takes the last attempt of
    /// the calculation, the slope of the largest face whose normal has a positive Z. Beams, slanted columns and
    /// members whose body is mapped are not covered.</summary>
    internal int ApplySlopePropertySetsCurrent(
        StepWriter w, IReadOnlyDictionary<int, Element> elementsById,
        IReadOnlyDictionary<int, (int entityId, int placementId, string ifcType)> products,
        Dictionary<(string PropertyName, string ValueExpr), int> pool)
    {
        var elementIdByEntity = new Dictionary<int, int>();
        foreach (var (elementId, product) in products)
            if (product.ifcType is "IFCCOLUMN" or "IFCMEMBER") elementIdByEntity[product.entityId] = elementId;
        if (elementIdByEntity.Count == 0) return 0;

        var columnTypes = new HashSet<int>();
        var typeMembers = new List<List<int>>();
        var psetIndexById = new Dictionary<int, int>();
        var psetByObject = new Dictionary<int, int>();
        int count = w.EntityCount;
        for (int i = 0; i < count; i++)
        {
            var (id, type, args) = w.EntityAt(i);
            switch (type)
            {
                case "IFCCOLUMNTYPE": columnTypes.Add(id); break;
                case "IFCRELDEFINESBYTYPE":
                    int last = args.LastIndexOf('#');
                    if (last > 0 && int.TryParse(args.AsSpan(last + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int relating)
                        && columnTypes.Contains(relating))
                        typeMembers.Add(EntityReference.Matches(args[..last]).Select(match =>
                            int.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture)).Where(elementIdByEntity.ContainsKey).ToList());
                    break;
                case "IFCPROPERTYSET":
                    if (PropertySetName.Match(args) is { Success: true } name
                        && StepWriter.Unescape(name.Groups[1].Value) is "Pset_ColumnCommon" or "Pset_MemberCommon")
                        psetIndexById[id] = i;
                    break;
                case "IFCRELDEFINESBYPROPERTIES":
                    if (SingleObjectPropertyRelation.Match(args) is { Success: true } relation
                        && int.Parse(relation.Groups[1].ValueSpan, CultureInfo.InvariantCulture) is int objectId
                        && elementIdByEntity.ContainsKey(objectId)
                        && int.Parse(relation.Groups[2].ValueSpan, CultureInfo.InvariantCulture) is int psetId
                        && psetIndexById.ContainsKey(psetId))
                        psetByObject[objectId] = psetId;
                    break;
            }
        }

        var targets = new List<(int EntityId, double Degrees)>();
        string? diagPath = Environment.GetEnvironmentVariable("RVTIO_PSET_DIAG");
        var indexById = new Dictionary<int, int>();
        for (int i = 0; i < count; i++) indexById.TryAdd(w.EntityAt(i).Id, i); // a repeated id keeps its first entity
        foreach (var members in typeMembers)
        {
            // The members that share one mapped body are the extrusion of its first member (the lowest element id); a member with a
            // body of its own is an extrusion of its own.
            var usedMaps = new HashSet<int>();
            bool first = true;
            foreach (int entityId in members.OrderBy(entityId => elementIdByEntity[entityId]))
            {
                // A body whose form is not recognised is not guessed to be a body of its own: only the first member of the type is judged then.
                bool recognised = TryMappedBodySourceCurrent(w, indexById, entityId, out int? map);
                bool shared = map is { } source && !usedMaps.Add(source);
                if (shared || (!first && !recognised)) continue;
                first = false;
                if (!elementsById.TryGetValue(elementIdByEntity[entityId], out Element? column) || column.Geometry is not { } geometry)
                    continue;
                bool vertical = HasVerticalExtrusionCurrent(geometry, out string detail);
                if (vertical) targets.Add((entityId, 90.0));
                if (!string.IsNullOrEmpty(diagPath))
                    File.AppendAllText(diagPath, $"Slope,{column.Id},{members.Count},{vertical},{detail}\n");
            }
        }
        foreach (var (elementId, product) in products)
            if (product.ifcType == "IFCMEMBER" && elementsById.TryGetValue(elementId, out Element? member)
                // A brace with a straight drive curve takes the slope of that curve (ApplyBeamSlopePropertySetsCurrent), as a beam does.
                && !(member.CategoryId == StructuralFramingCategoryId && ResolveBeamSlopeDegreesCurrent(member) != null)
                && member.Geometry is { ExactMappedInstances: null or { Count: 0 } } memberGeometry
                && LargestUpwardFaceSlopeCurrent(memberGeometry) is double degrees)
                targets.Add((product.entityId, degrees));

        int added = 0;
        foreach (var (entityId, degrees) in targets)
        {
            if (!psetByObject.TryGetValue(entityId, out int psetId) || !psetIndexById.TryGetValue(psetId, out int index)) continue;
            string psetArgs = w.EntityAt(index).Args;
            if (!psetArgs.EndsWith(')')) continue;
            int propertyId = GetOrAddPropertySingleValue(w, pool, "Slope", $"IFCPLANEANGLEMEASURE({D17(degrees)})");
            w.ReplaceArgs(index, $"{psetArgs[..^1]},#{propertyId.ToString(CultureInfo.InvariantCulture)})");
            added++;
        }
        return added;
    }

    /// <summary>Pset_BeamCommon.Slope of every beam, and Pset_MemberCommon.Slope of every brace (a structural framing IfcMember), with a
    /// straight drive curve: the angle of the curve to the horizontal in degrees.
    /// It does not depend on the BaseQuantities option or on the body of the beam. A beam without a straight drive curve has none.
    /// Returns the number of properties added.</summary>
    internal static int ApplyBeamSlopePropertySetsCurrent(
        StepWriter w, IReadOnlyDictionary<int, Element> elementsById,
        IReadOnlyDictionary<int, (int entityId, int placementId, string ifcType)> products,
        Dictionary<(string PropertyName, string ValueExpr), int> pool)
    {
        var elementIdByEntity = new Dictionary<int, int>();
        foreach (var (elementId, product) in products)
            if (product.ifcType == "IFCBEAM"
                || product.ifcType == "IFCMEMBER" && elementsById.TryGetValue(elementId, out Element? framing) && framing.CategoryId == StructuralFramingCategoryId)
                elementIdByEntity[product.entityId] = elementId;
        var slopes = new Dictionary<int, double>();
        foreach (var (entityId, elementId) in elementIdByEntity)
            if (elementsById.TryGetValue(elementId, out Element? beam) && ResolveBeamSlopeDegreesCurrent(beam) is double slope)
                slopes[entityId] = slope;
        return AddBeamSlopesCurrent(w, slopes, pool);
    }

    private static int AddBeamSlopesCurrent(
        StepWriter w, IReadOnlyDictionary<int, double> slopes, Dictionary<(string PropertyName, string ValueExpr), int> pool)
    {
        if (slopes.Count == 0) return 0;
        var psetIndexById = new Dictionary<int, int>();
        var psetByObject = new Dictionary<int, int>();
        int count = w.EntityCount;
        for (int i = 0; i < count; i++)
        {
            var (id, type, args) = w.EntityAt(i);
            if (type == "IFCPROPERTYSET" && PropertySetName.Match(args) is { Success: true } name
                && StepWriter.Unescape(name.Groups[1].Value) is "Pset_BeamCommon" or "Pset_MemberCommon")
                psetIndexById[id] = i;
            else if (type == "IFCRELDEFINESBYPROPERTIES" && SingleObjectPropertyRelation.Match(args) is { Success: true } relation
                && int.Parse(relation.Groups[1].ValueSpan, CultureInfo.InvariantCulture) is int objectId && slopes.ContainsKey(objectId)
                && int.Parse(relation.Groups[2].ValueSpan, CultureInfo.InvariantCulture) is int psetId && psetIndexById.ContainsKey(psetId))
                psetByObject[objectId] = psetId;
        }
        int added = 0;
        foreach (var (entityId, degrees) in slopes)
        {
            if (!psetByObject.TryGetValue(entityId, out int psetId)) continue;
            int index = psetIndexById[psetId];
            string psetArgs = w.EntityAt(index).Args;
            if (!psetArgs.EndsWith(')')) continue;
            int propertyId = GetOrAddPropertySingleValue(w, pool, "Slope", $"IFCPLANEANGLEMEASURE({D17(degrees)})");
            w.ReplaceArgs(index, $"{psetArgs[..^1]},#{propertyId.ToString(CultureInfo.InvariantCulture)})");
            added++;
        }
        return added;
    }

    /// <summary>How the Body of a product is written. True when its form is recognised: <paramref name="map"/> is then the representation
    /// map of a mapped item, or null for a body of its own (any Body representation that is not a mapped one). False when the product,
    /// its shape or its representations do not have the expected form, or no Body is found: the caller must not guess.</summary>
    internal static bool TryMappedBodySourceCurrent(StepWriter w, Dictionary<int, int> indexById, int productEntity, out int? map)
    {
        map = null;
        if (!indexById.TryGetValue(productEntity, out int productIndex)) return false;
        var product = SplitStepArguments(w.EntityAt(productIndex).Args);
        if (product.Length < 7 || !TryEntityId(product[6], out int shapeId) || !indexById.TryGetValue(shapeId, out int shapeIndex)) return false;
        var shape = SplitStepArguments(w.EntityAt(shapeIndex).Args);
        if (shape.Length != 3 || shape[2].Length < 3) return false;
        foreach (string token in SplitStepArguments(shape[2][1..^1]))
        {
            if (!TryEntityId(token, out int representationId) || !indexById.TryGetValue(representationId, out int representationIndex)) continue;
            var representation = SplitStepArguments(w.EntityAt(representationIndex).Args);
            if (representation.Length != 4 || representation[1] != "'Body'") continue;
            if (representation[2] != "'MappedRepresentation'") return true;
            if (representation[3].Length < 3) return false;
            var items = SplitStepArguments(representation[3][1..^1]);
            if (items.Length != 1 || !TryEntityId(items[0], out int itemId) || !indexById.TryGetValue(itemId, out int itemIndex)
                || w.EntityAt(itemIndex).Type != "IFCMAPPEDITEM"
                || SplitStepArguments(w.EntityAt(itemIndex).Args) is not { Length: > 0 } itemArguments || !TryEntityId(itemArguments[0], out int source))
                return false;
            map = source;
            return true;
        }
        return false;
    }

    /// <summary>Every solid of the instance is a vertical extrusion for the body export. An instance of three solids, one of
    /// which fails, is exported as three B-Reps, none as an extrusion, so one failing solid takes the extrusion of the others.</summary>
    internal static bool HasVerticalExtrusionCurrent(GeometryData geometry, out string detail)
    {
        detail = "no-faces";
        if (geometry.Faces is not { Count: > 0 } faces || faces.Any(face => face.OuterVertices is not { Count: >= 3 })) return false;
        var solids = geometry.BrepSolids is { Count: > 0 } shells
            ? shells.Select(shell => shell.FaceIndices.Where(index => (uint)index < (uint)faces.Count).Select(index => faces[index]).ToList()).ToList()
            : [faces];
        // A round column (see RoundPrismCurrent) lists its exact end faces outside the tessellated shell of its side: its solid is all the faces then.
        if (geometry.BrepSolids is [var only] && RoundPrismCurrent(geometry) != null)
        {
            var inShell = only.FaceIndices.ToHashSet();
            if (faces.Where((face, index) => !inShell.Contains(index)).Any(IsExactCap)) solids = [faces];
        }
        bool all = true;
        var parts = new List<string>();
        foreach (var solid in solids)
        {
            bool ok = WallExtrusion.TryFindVerticalExtrusion(solid, out string reason, exactCylinders: true);
            all &= ok;
            parts.Add($"{solid.Count}f/{solid.Count(face => face.CurvedSurface)}c/{reason}");
        }
        detail = string.Join(';', parts);
        return all;
    }

    /// <summary>Last attempt of the slope calculation: the angle of the largest face of the last solid whose normal has a positive Z,
    /// pi/2 - acos(n . unit(nx, ny, 0)) in degrees; none when that face is horizontal (the slope of a vertical line is undefined).
    /// Null when the solid has a curved face with a positive Z, whose normal depends on the evaluation point.</summary>
    internal static double? LargestUpwardFaceSlopeCurrent(GeometryData geometry)
    {
        if (geometry.Faces is not { Count: > 0 } faces || geometry.BrepSolids is not { Count: > 0 } shells) return null;
        double bestArea = 0;
        double[]? best = null;
        foreach (int index in shells[^1].FaceIndices)
        {
            if ((uint)index >= (uint)faces.Count) continue;
            var face = faces[index];
            // The normal is evaluated on the analytic surface, not on its polygon.
            bool sourcePlane = face.PlaneXAxis is { Length: 3 } && face.PlaneYAxis is { Length: 3 };
            var x = sourcePlane ? face.PlaneXAxis! : face.XVector;
            var y = sourcePlane ? face.PlaneYAxis! : face.YVector;
            double[] normal = x.Length == 3 && y.Length == 3 && x.Any(v => v != 0) && y.Any(v => v != 0)
                ? [x[1] * y[2] - x[2] * y[1], x[2] * y[0] - x[0] * y[2], x[0] * y[1] - x[1] * y[0]]
                : face.Normal;
            if (normal.Length != 3 || !(normal[2] > 0)) continue;
            if (face.CurvedSurface) return null;
            double area = face.Area ?? WallExtrusion.FaceArea(face) - (face.InnerVertices ?? []).Sum(ring => WallExtrusion.LoopArea(ring));
            if (area > bestArea) { bestArea = area; best = normal; }
        }
        if (best == null) return null;
        double length = Math.Sqrt(best[0] * best[0] + best[1] * best[1] + best[2] * best[2]);
        double horizontal = Math.Sqrt(best[0] * best[0] + best[1] * best[1]);
        if (length == 0 || horizontal == 0) return null;
        // acos of the dot product of the unit normal with the unit horizontal direction.
        double dot = (best[0] * best[0] + best[1] * best[1]) / (length * horizontal);
        return (0.5 * Math.PI - Math.Acos(Math.Min(1.0, dot))) * 180.0 / Math.PI;
    }
}
