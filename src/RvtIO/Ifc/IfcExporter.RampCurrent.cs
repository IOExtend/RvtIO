using RvtIO.Geometry;
using System.Globalization;
using RvtIO.Model;

namespace RvtIO.Ifc;

public partial class IfcExporter
{
    internal const int RampCategoryId = -2000180;

    /// <summary>The family name of a system ramp, whatever the language of the file.</summary>
    internal const string RampFamilyName = "Ramp";

    /// <summary>The most active faces a solid of a ramp may have to count as a flight or a landing.</summary>
    internal const int RampPartMaxFaces = 8;

    /// <summary>Sub-element of the ExportId that names the IfcRamp holding the body of a ramp.</summary>
    internal const int RampBodySubElementIndex = 2;

    /// <summary>How the current profile writes a ramp.</summary>
    internal enum RampLayout
    {
        /// <summary>One IfcRamp that carries the body.</summary>
        Single,
        /// <summary>An IfcRamp without a body that aggregates one IfcRampFlight or IfcSlab (LANDING) per solid.</summary>
        Components,
        /// <summary>An IfcRamp without a body that aggregates a second IfcRamp holding the body.</summary>
        Monolithic,
    }

    /// <summary>The type of a system ramp: the general type link when it names a ramp type, else the one ramp type the ramp
    /// references. 0 when neither is unique.</summary>
    internal static int RampTypeElementId(Element ramp, IReadOnlyDictionary<int, Element> elemById)
    {
        static bool IsRampType(Element? e) => e is not null && e.RootClassName == "RampAttributes";
        if (ramp.FamilyTypeId > 0 && elemById.TryGetValue(ramp.FamilyTypeId, out var linked) && IsRampType(linked)) return linked.Id;
        int found = 0;
        foreach (int r in ramp.References ?? [])
            if (r != found && elemById.TryGetValue(r, out var e) && IsRampType(e))
            {
                if (found > 0) return 0;
                found = r;
            }
        return found;
    }

    /// <summary>Returns the elements with each system ramp replaced by a copy whose type link names its ramp type (the link is
    /// added when the ramp has none) and, for a nameless ramp, the name of that type. The input elements are not changed.</summary>
    private static List<Element> RelinkRampTypes(List<Element> elements)
    {
        var elemById = new Dictionary<int, Element>();
        foreach (var e in elements) elemById.TryAdd(e.Id, e);
        var result = new List<Element>(elements.Count);
        foreach (var ramp in elements)
        {
            int typeId = IsSystemRamp(ramp) ? RampTypeElementId(ramp, elemById) : 0;
            bool relink = typeId > 0 && typeId != ramp.FamilyTypeId;
            bool rename = typeId > 0 && string.IsNullOrEmpty(ramp.Name);
            if (!relink && !rename) { result.Add(ramp); continue; }
            var copy = ramp.ShallowCopy();
            if (relink)
            {
                var parameters = ramp.Parameters.ToList();
                int index = parameters.FindIndex(p => p.Definition?.Name is "ELEM_TYPE_PARAM" && p.Value is int);
                if (index >= 0) parameters[index] = parameters[index].ShallowCopy(typeId);
                else parameters.Add(new Parameter
                {
                    Id = ElemTypeParamId, Value = typeId, StorageType = StorageType.ElementId,
                    Definition = new Definition { Id = ElemTypeParamId, Name = "ELEM_TYPE_PARAM" },
                });
                copy.Parameters = parameters;
            }
            // A ramp carries the name of its type.
            if (rename) copy.Name = elemById[typeId].Name;
            result.Add(copy);
        }
        return result;
    }

    private const int ElemTypeParamId = -1002050;

    /// <summary>A system ramp is a Ramp element; a ramp placed from a loaded family is a family instance.</summary>
    internal static bool IsSystemRamp(Element element) =>
        element.CategoryId == RampCategoryId && element.RootClassName == "Ramp";

    /// <summary>The layout of a ramp. A ramp placed from a loaded family, or a system ramp without geometry, is one IfcRamp.
    /// A system ramp with railings, with a solid of more than <see cref="RampPartMaxFaces"/> faces, with an open or curved
    /// solid or with a solid without an upward-facing face is monolithic; otherwise it is made of its solids when it has
    /// several, else it is a single flight.</summary>
    internal static RampLayout ClassifyRamp(Element ramp, bool hasRailings)
    {
        if (!IsSystemRamp(ramp) || ramp.Geometry?.Faces is not { Count: > 0 }) return RampLayout.Single;
        if (hasRailings) return RampLayout.Monolithic;
        var solids = ClassifyRampSolids(ramp.Geometry);
        if (solids == null) return RampLayout.Monolithic;
        return solids.Count > 1 ? RampLayout.Components : RampLayout.Single;
    }

    /// <summary>Splits the geometry of a ramp into its closed solids. A solid whose largest upward-facing face is
    /// horizontal is a landing, any other solid a flight. Returns null when the geometry has no separate closed solids,
    /// when a solid has more than <see cref="RampPartMaxFaces"/> active faces, when a face is curved or when a solid has no
    /// upward-facing face.</summary>
    internal static List<(List<int> FaceIndices, bool IsLanding)>? ClassifyRampSolids(GeometryData? geometry)
    {
        if (geometry?.Faces is not { Count: > 0 } faces || geometry.BrepSolids is not { Count: > 0 } solids
            || geometry.HasCurvedFace()) return null;
        var parts = new List<(List<int>, bool)>();
        foreach (var solid in solids)
        {
            var indices = solid.FaceIndices;
            if (!solid.IsClosed || indices.Count == 0 || indices.Count > RampPartMaxFaces
                || indices.Any(index => (uint)index >= (uint)faces.Count)) return null;
            double topArea = 0, topNormalZ = 0;
            bool found = false;
            foreach (int index in indices)
            {
                var face = faces[index];
                if (face.Normal.Length < 3) continue;
                double length = Math.Sqrt(face.Normal.Sum(component => component * component));
                if (length < 1e-12 || face.Normal[2] / length <= 1e-9) continue;
                double area = face.Area ?? WallExtrusion.FaceArea(face);
                if (!found || area > topArea) (found, topArea, topNormalZ) = (true, area, face.Normal[2] / length);
            }
            if (!found) return null;
            parts.Add((indices.ToList(), Math.Abs(topNormalZ - 1) <= 1e-9));
        }
        return parts;
    }

    /// <summary>The ids of the ramps that host a railing.</summary>
    internal static HashSet<int> RampsWithRailings(IEnumerable<Element> elements) =>
        elements.Where(element => element.SerializedRailingStairHostId is > 0 && element.RuleClassName == "BaseRailing")
            .Select(element => element.SerializedRailingStairHostId!.Value).ToHashSet();

    /// <summary>Writes the body of every part of a ramp (see <see cref="ClassifyRampSolids"/>). Writes nothing and returns
    /// false when a part has no body.</summary>
    private bool TryWriteRampPartBodies(StepWriter w, GeometryData geometry, List<(List<int> FaceIndices, bool IsLanding)> parts,
        int subCtxId, out List<(int ShapeId, bool IsLanding)> bodies)
    {
        bodies = new();
        var checkpoint = w.CreateCheckpoint();
        foreach (var (faceIndices, isLanding) in parts)
        {
            var solidGeometry = new GeometryData
            {
                GStyleElemId = geometry.GStyleElemId,
                Flags = geometry.Flags,
                BoundingBox = geometry.BoundingBox,
                Faces = faceIndices.Select(index => geometry.Faces[index]).ToList(),
                BrepSolids = [new GeoBrepSolid { FaceIndices = Enumerable.Range(0, faceIndices.Count).ToList(), IsClosed = true }],
            };
            int shapeId = CreateFacetedBrepWithOutcome(w, solidGeometry, subCtxId).ShapeId;
            if (shapeId <= 0)
            {
                w.Rollback(checkpoint);
                bodies = new();
                return false;
            }
            bodies.Add((shapeId, isLanding));
        }
        return true;
    }

    /// <summary>Writes the flights (IfcRampFlight) and landings (IfcSlab, LANDING) of a ramp and the IfcRelAggregates that
    /// links them to the IfcRamp, which carries no body of its own. Their GlobalIds derive from that of the IfcRamp. Returns the entity ids of the parts.</summary>
    private List<int> WriteRampComponentsCurrent(StepWriter w, int ownerHistId, Element ramp, int rampEntityId, string rampGlobalId, string rampName,
        int rampPlacementId, int axisId, List<(int ShapeId, bool IsLanding)> bodies)
    {
        var parts = new List<int>();
        int flights = 0, landings = 0;
        for (int index = 0; index < bodies.Count; index++)
        {
            var (shapeId, isLanding) = bodies[index];
            int ordinal = isLanding ? ++landings : ++flights;
            string globalId = IfcGuid.FromName($"ramp-part:{rampGlobalId}:{index}");
            int placement = w.Add("IFCLOCALPLACEMENT", $"{R(rampPlacementId)},{R(axisId)}");
            string common = $"{S(globalId)},{R(ownerHistId)},{S($"{rampName} {(isLanding ? "Landing" : "Flight")} {ordinal.ToString(CultureInfo.InvariantCulture)}")},$,$,{R(placement)},{R(shapeId)},$";
            // IFC2X3 gives an IfcRampFlight no PredefinedType; a landing is an IfcSlab of the LANDING type.
            parts.Add(isLanding ? w.Add("IFCSLAB", common + ",.LANDING.") : w.Add("IFCRAMPFLIGHT", common));
        }
        w.Add("IFCRELAGGREGATES",
            $"{S(IfcGuid.FromName($"ramp-aggregate:{rampGlobalId}"))},{R(ownerHistId)},$,$,{R(rampEntityId)},{StepWriter.List(parts.Select(R))}");
        return parts;
    }

    /// <summary>Writes the body of a monolithic ramp with its vertices taken from their lowest corner (the smallest x, y and z of the
    /// written points) and returns the axis that puts the container at that corner; the IfcRamp that holds the body is at identity under
    /// the container. Writes nothing and returns false when the body has no point.</summary>
    private bool TryWriteRampBodyAtMinimumCurrent(StepWriter w, GeometryData geometry, int subCtxId,
        out int shapeId, out BodyRepresentationWriteOutcome outcome, out int axisId)
    {
        axisId = 0;
        var checkpoint = w.CreateCheckpoint();
        (shapeId, outcome) = CreateFacetedBrepWithOutcome(w, geometry, subCtxId);
        double[]? min = shapeId > 0 ? MinimumWrittenPoint(w, checkpoint) : null;
        w.Rollback(checkpoint);
        if (min == null)
        {
            (shapeId, outcome) = (0, default);
            return false;
        }
        (shapeId, outcome) = CreateFacetedBrepWithOutcome(w, geometry, subCtxId,
            -min[0] / FeetToMetres, -min[1] / FeetToMetres, -min[2] / FeetToMetres);
        int origin = w.Add("IFCCARTESIANPOINT", StepWriter.List(D(min[0]), D(min[1]), D(min[2])));
        axisId = w.Add("IFCAXIS2PLACEMENT3D", $"{R(origin)},$,$");
        return shapeId > 0;
    }

    /// <summary>The smallest x, y and z of the IfcCartesianPoint entities written since the checkpoint, or null when there is none
    /// or a coordinate is not a finite number.</summary>
    private static double[]? MinimumWrittenPoint(StepWriter w, StepWriter.Checkpoint checkpoint)
    {
        double[]? min = null;
        for (int index = checkpoint.EntityCount; index < w.EntityCount; index++)
        {
            var entity = w.EntityAt(index);
            if (entity.Type != "IFCCARTESIANPOINT") continue;
            var parts = entity.Args.Trim('(', ')').Split(',');
            if (parts.Length != 3) return null;
            min ??= [double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity];
            for (int axis = 0; axis < 3; axis++)
            {
                if (!double.TryParse(parts[axis], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                    || !double.IsFinite(value)) return null;
                min[axis] = Math.Min(min[axis], value);
            }
        }
        return min;
    }

    /// <summary>Writes the IfcRamp that holds the body of a monolithic ramp, under a copy of the placement of the container,
    /// and the IfcRelAggregates that links it to the container. Returns the entity ids of the contained IfcRamp and of the relation.</summary>
    private (int Body, int Relation) WriteRampBodyCurrent(StepWriter w, int ownerHistId, Element ramp, int rampEntityId, string rampGlobalId, string rampName,
        string objectType, int rampPlacementId, int axisId, int shapeId)
    {
        int placement = w.Add("IFCLOCALPLACEMENT", $"{R(rampPlacementId)},{R(axisId)}");
        string globalId = ResolveExactElementGlobalId(ramp, _usedExportIds, RampBodySubElementIndex, handleBased: true)
            ?? IfcGuid.FromName($"ramp-body:{rampGlobalId}");
        string id = ramp.Id.ToString(CultureInfo.InvariantCulture);
        int body = w.Add("IFCRAMP",
            $"{S(globalId)},{R(ownerHistId)},{S(rampName)},$,{S(objectType)},{R(placement)},{R(shapeId)},{S(id)},.NOTDEFINED.");
        int relation = w.Add("IFCRELAGGREGATES",
            $"{S(IfcGuid.FromName($"ramp-aggregate:{rampGlobalId}"))},{R(ownerHistId)},$,$,{R(rampEntityId)},{StepWriter.List(R(body))}");
        return (body, relation);
    }

    /// <summary>Adds the railings hosted by a monolithic ramp to the IfcRelAggregates of the ramp, after its body, in the order of
    /// the entities, and takes them out of the storey. <paramref name="relations"/> maps the id of each such ramp to its relation.</summary>
    internal static void AggregateRampRailings(StepWriter writer, IReadOnlyList<Element> elements,
        IReadOnlyDictionary<int, (int entityId, int placementId, string ifcType)> products,
        Dictionary<int, List<int>> storeyContents, IReadOnlyDictionary<int, int> relations)
    {
        if (relations.Count == 0) return;
        var counts = new Dictionary<int, int>();
        foreach (var element in elements) counts[element.Id] = counts.GetValueOrDefault(element.Id) + 1;
        var railingsByHost = elements.Where(element => element.Id > 0 && !element.IsBlockOnly && counts[element.Id] == 1
                && element.SerializedRailingStairHostId is > 0 && element.RuleClassName == "BaseRailing"
                && element.SerializedOwningElementId != null)
            .ToLookup(element => element.SerializedRailingStairHostId!.Value);
        var indexById = new Dictionary<int, int>(writer.EntityCount);
        var decomposed = new HashSet<int>();
        for (int index = 0; index < writer.EntityCount; index++)
        {
            var entity = writer.EntityAt(index);
            indexById.Add(entity.Id, index);
            if (entity.Type is not ("IFCRELAGGREGATES" or "IFCRELNESTS")) continue;
            var args = SplitStepArguments(entity.Args);
            if (args.Length == 6) decomposed.UnionWith(ReferenceIds(args[5]));
        }
        foreach (var (rampId, relationId) in relations.OrderBy(pair => pair.Key))
        {
            int relationIndex = indexById[relationId];
            var relationArgs = SplitStepArguments(writer.EntityAt(relationIndex).Args);
            var handles = ReferenceIds(relationArgs[5]);
            int bodies = handles.Count;
            foreach (var railing in railingsByHost[rampId].OrderBy(railing => railing.Id))
            {
                if (!products.TryGetValue(railing.Id, out var product) || product.ifcType != "IFCRAILING"
                    || decomposed.Contains(product.entityId) || !indexById.TryGetValue(product.entityId, out int index)) continue;
                var args = SplitStepArguments(writer.EntityAt(index).Args);
                if (args.Length == 9 && args[6] != "$") handles.Add(product.entityId);
            }
            if (handles.Count == bodies) continue;
            relationArgs[5] = StepWriter.List(handles.Order().Select(R));
            writer.ReplaceArgs(relationIndex, string.Join(",", relationArgs));
            var added = handles.ToHashSet();
            foreach (var contents in storeyContents.Values) contents.RemoveAll(added.Contains);
            decomposed.UnionWith(added);
        }
    }

    /// <summary>Pset_RampCommon: IsExternal, then the Reference shared with Pset_QuantityTakeOff.</summary>
    private static void ExportRampCommonPropertySet(StepWriter w, Element elem, int elemEntityId, int ownerHistId,
        int referenceValueId, Dictionary<(string PropertyName, string ValueExpr), int> valuePool, bool isExternal)
    {
        int external = GetOrAddPropertySingleValue(w, valuePool, "IsExternal", isExternal ? "IFCBOOLEAN(.T.)" : "IFCBOOLEAN(.F.)");
        int psetId = w.Add("IFCPROPERTYSET",
            $"{S(IfcGuid.FromName($"ramp-common-pset:{elem.Id}"))},{R(ownerHistId)},{S("Pset_RampCommon")},$,{StepWriter.List([R(external), R(referenceValueId)])}");
        w.Add("IFCRELDEFINESBYPROPERTIES",
            $"{S(IfcGuid.FromName($"ramp-common-rel:{elem.Id}"))},{R(ownerHistId)},$,$,{StepWriter.List(R(elemEntityId))},{R(psetId)}");
    }
}
