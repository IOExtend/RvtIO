using System.Linq;
using RvtIO.BuiltIns;
using RvtIO.Internal;
using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Cli;

/// <summary>
/// Diagnostic-only, gated behind <c>--inspect-batch &lt;ids-file&gt; &lt;out.csv&gt;</c> (see Program.cs). Never referenced from the
/// standard export path.
/// <para>
/// Unlike <c>--inspect &lt;id&gt;</c>, which reopens the file (a full parse) per invocation, this dump parses once
/// (<see cref="RvtFileReader.Read"/>) and looks up every id of the input file in the in-memory element table, writing one CSV row
/// per id. It is deliberately narrower than <c>--inspect</c>: identity, relationship and container fields only (category, type,
/// host chain, workset, phase, design option, geometry counts, bbox extents, raw References).
/// </para>
/// <para>
/// <c>References</c> is the relationship field the exporter uses to find the parent wall of a curtain panel or mullion (it is not
/// <c>HostId</c> or <c>SuperInstanceId</c>, which are populated for hosted inserts and nested family instances).
/// "Partition" and "workset" are the same field: the ElemRec <c>m_partitionId</c> is emitted once, as <c>Workset</c>.
/// </para>
/// <para>
/// Output is plain CSV, one row per requested id; a missing id gets a row with <c>Found=0</c> and every other column blank, so the
/// output does not silently shrink. Never write a .json path here: it would trigger the full JSON export.
/// </para>
/// </summary>
static class InspectBatchDump
{
    public static void Run(RevitFileInfo info, string idsPath, string outPath, ParsingState? state = null)
    {
        var byId = new Dictionary<int, Element>(info.Elements.Count);
        foreach (var e in info.Elements) byId.TryAdd(e.Id, e);

        var ids = new List<int>();
        foreach (var raw in File.ReadLines(idsPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            // Tolerate comma/whitespace-separated ids per line, not just one-per-line.
            foreach (var tok in line.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(tok, out int id)) ids.Add(id);
        }

        using var w = new StreamWriter(outPath, false, System.Text.Encoding.UTF8);
        w.WriteLine(string.Join(",",
            "Id", "Found", "CategoryId", "Category", "TypeId", "FamilyTypeId",
            "HostId", "SerializedHostId", "HostCategoryId", "HostCategory",
            "ExtraHostIds", "ExplicitHostIds",
            "SuperInstanceId", "SuperInstanceCategoryId", "SuperInstanceCategory",
            "Workset", "DesignOptionId", "PhaseCreated", "PhaseDemolished", "Level",
            "IsDiscovered", "IsBlockOnly",
            "FacesCount", "SolidsCount", "InstancesCount",
            "BBoxMinX", "BBoxMinY", "BBoxMinZ", "BBoxMaxX", "BBoxMaxY", "BBoxMaxZ",
            "References",
            "IsNativeLevel", "LevelIsBuildingStory",
            "SerializedFamilyId", "SerializedUnplacedOwnerId", "SerializedOwnerDbViewId",
            // wall candidate columns: Function (FUNCTION_PARAM, -1001006) read from the element and from its FamilyTypeId element (the wall
            // function is stored on the type), Structural Usage (-1001119) and the top/bottom attached flags (-1001117, -1001118)
            "WallFunctionInstance", "WallFunctionType",
            "WallStructuralUsage", "WallTopIsAttached", "WallBottomIsAttached",
            // slab, roof, finish and external-flag candidate columns: FLOOR_PARAM_IS_STRUCTURAL (-1001954), ROOF_SLOPE (-1006016), the
            // shared finish parameter id -1001208 on the occurrence and on its type, the named "IsExternal" lookups (occurrence, type,
            // type bracket; lenient name match), FUNCTION_PARAM of the host's type, and a lenient-named "Slope" double
            "FloorParamIsStructural", "RoofSlopeParam",
            "GenericFinishParam", "GenericFinishParamType",
            "NamedIsExternalOccurrence", "NamedIsExternalType", "NamedIsExternalTypeBracket",
            "HostFunctionParam", "NamedSlopeOccurrence",
            // FinishLayerNames: elem.Layers entries with Function=="Finish1", ordered by LayerIndex, each rendered "MaterialName;" and concatenated
            "FinishLayerNames",
            // symbol-name columns (the `state` overload only; blank when state is null): OwnMasterSymbolId is the element's own decoded
            // NativeParams.Fields["m_masterSymbolId"] (not the TypeId/FamilyTypeId fields, which can be canonicalized or inherited),
            // CurrentName is elem.Name after the full pipeline, the MasterSymbol* columns are the name-table, scan and resolved names of
            // that symbol, OwnNameTableEntry tells whether the instance itself owns a name-table entry (a symbol or type, not a placed instance)
            "OwnMasterSymbolId", "CurrentName", "FamilyName",
            "MasterSymbolNameTableEntry", "MasterSymbolNativeScanName", "MasterSymbolResolvedName",
            "OwnNameTableEntry",
            // RawElemTypeParam* columns: the current ELEM_TYPE_PARAM (-1002050) value read off e.Parameters (not e.TypeId), its category and its name-table entry
            "RawElemTypeParam", "RawElemTypeParamCategoryId", "RawElemTypeParamCategory",
            "RawElemTypeParamNameTableEntry",
            // wall type name columns: the element's own NativeParams.Fields["m_WallAttributesId"], the category of the resolved target and its
            // compound structure type name source (BlockCount, TerminalName)
            "OwnWallAttributesId", "WallAttributesIdCategory",
            "WallAttributesIdSourceBlockCount", "WallAttributesIdSourceTerminalName",
            // __isLevel probe: whether the element's own NativeParams entry, and that of its resolved TypeId target, carry the
            // __isLevel/m_text fields set by a genuine Level's DatumPlane block
            "OwnIsLevelFlag", "OwnNativeMText",
            "TypeIdIsLevelFlag", "TypeIdNativeMText",
            // family name source columns: the name-table chain keyed on the canonical e.TypeId and on e.FamilyTypeId, and the final
            // elem.Name of the elements referenced by the decoded m_famId field (own record and the FamilyTypeId record)
            "FamilyViaTypeIdChain", "FamilyViaFamilyTypeIdChain",
            "SerializedFamilyIdOwn", "SerializedFamilyIdOwnName",
            "SerializedFamilyIdType", "SerializedFamilyIdTypeName"));

        int found = 0, missing = 0;
        foreach (int id in ids)
        {
            if (!byId.TryGetValue(id, out var e))
            {
                w.WriteLine($"{id},0" + new string(',', 75));
                missing++;
                continue;
            }
            found++;

            int? hostCatId = null; string? hostCat = null;
            if (e.HostId is { } hid && byId.TryGetValue(hid, out var hostElem))
            {
                hostCatId = hostElem.CategoryId; hostCat = hostElem.Category;
            }
            int? superCatId = null; string? superCat = null;
            if (e.SuperInstanceId is { } sid && byId.TryGetValue(sid, out var superElem))
            {
                superCatId = superElem.CategoryId; superCat = superElem.Category;
            }

            int? workset = e.WorksetId ?? IntParam(e, BuiltInParameter.ELEM_PARTITION_PARAM);
            int? designOption = IntParam(e, BuiltInParameter.DESIGN_OPTION_ID);
            int? phaseCreated = IntParam(e, BuiltInParameter.PHASE_CREATED);
            int? phaseDemolished = IntParam(e, BuiltInParameter.PHASE_DEMOLISHED);
            int? level = IntParam(e, BuiltInParameter.LEVEL_PARAM);
            int? levelIsBuildingStory = IntParam(e, BuiltInParameter.LEVEL_IS_BUILDING_STORY);

            int? wallFunctionInstance = IntParam(e, BuiltInParameter.FUNCTION_PARAM);
            int? wallFunctionType = e.FamilyTypeId > 0
                && byId.TryGetValue(e.FamilyTypeId, out var typeElem)
                ? IntParam(typeElem, BuiltInParameter.FUNCTION_PARAM)
                : null;
            int? wallStructuralUsage = IntParam(e, BuiltInParameter.WALL_STRUCTURAL_USAGE_PARAM);
            int? wallTopIsAttached = IntParam(e, BuiltInParameter.WALL_TOP_IS_ATTACHED);
            int? wallBottomIsAttached = IntParam(e, BuiltInParameter.WALL_BOTTOM_IS_ATTACHED);

            Element? typeElemForFinish = e.FamilyTypeId > 0 && byId.TryGetValue(e.FamilyTypeId, out var te)
                ? te : null;
            int? floorParamIsStructural = IntParam(e, BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL);
            double? roofSlopeParam = DoubleParam(e, BuiltInParameter.ROOF_SLOPE);
            string? genericFinishParam = StringParam(e, BuiltInParameter.GENERIC_FINISH);
            string? genericFinishParamType = typeElemForFinish != null
                ? StringParam(typeElemForFinish, BuiltInParameter.GENERIC_FINISH) : null;

            int? namedIsExternalOccurrence = NamedIntParam(e, "IsExternal");
            int? namedIsExternalType = typeElemForFinish != null
                ? NamedIntParam(typeElemForFinish, "IsExternal") : null;
            int? namedIsExternalTypeBracket = typeElemForFinish != null
                ? NamedIntParam(typeElemForFinish, "IsExternal[Type]") : null;
            int? hostFunctionParam = null;
            if (e.HostId is { } hostIdForFunction
                && byId.TryGetValue(hostIdForFunction, out var hostElemForFunction)
                && hostElemForFunction.FamilyTypeId > 0
                && byId.TryGetValue(hostElemForFunction.FamilyTypeId, out var hostTypeElem))
            {
                hostFunctionParam = IntParam(hostTypeElem, BuiltInParameter.FUNCTION_PARAM);
            }
            double? namedSlopeOccurrence = NamedDoubleParam(e, "Slope");
            string? finishLayerNames = e.Layers is { Count: > 0 } layers
                ? string.Concat(layers
                    .Where(l => l.Function == "Finish1" && !string.IsNullOrWhiteSpace(l.MaterialName))
                    .OrderBy(l => l.LayerIndex)
                    .Select(l => l.MaterialName + ";"))
                : null;

            int? ownMasterSymbolId = null;
            string? masterSymbolNameTableEntry = null;
            string? masterSymbolNativeScanName = null;
            string? masterSymbolResolvedName = null;
            string? ownNameTableEntry = null;
            if (state != null)
            {
                if (state.NativeParams.TryGetValue((uint)e.Id, out var ownNp)
                    && ownNp.Fields.TryGetValue("m_masterSymbolId", out int sym) && sym > 0)
                {
                    ownMasterSymbolId = sym;
                    if (state.ElementNameTable.TryGetValue((uint)sym, out var tblName))
                        masterSymbolNameTableEntry = tblName;
                    if (state.NativeElementNames.TryGetValue((uint)sym, out var natName))
                        masterSymbolNativeScanName = natName;
                    if (byId.TryGetValue(sym, out var symElem))
                        masterSymbolResolvedName = symElem.Name;
                }
                if (state.ElementNameTable.TryGetValue((uint)e.Id, out var ownTbl))
                    ownNameTableEntry = ownTbl;
            }

            int? rawElemTypeParam = null;
            int? rawElemTypeParamCategoryId = null;
            string? rawElemTypeParamCategory = null;
            string? rawElemTypeParamNameTableEntry = null;
            var elemTypeParamRaw = e.Parameters.FirstOrDefault(p => p.Id == -1002050);
            if (elemTypeParamRaw?.Value is int rawTid && rawTid > 0)
            {
                rawElemTypeParam = rawTid;
                if (byId.TryGetValue(rawTid, out var rawTypeElem))
                {
                    rawElemTypeParamCategoryId = rawTypeElem.CategoryId;
                    rawElemTypeParamCategory = rawTypeElem.Category;
                }
                if (state != null && state.ElementNameTable.TryGetValue((uint)rawTid, out var rawTblName))
                    rawElemTypeParamNameTableEntry = rawTblName;
            }

            int? ownWallAttributesId = null;
            string? wallAttributesIdCategory = null;
            int? wallAttributesIdSourceBlockCount = null;
            string? wallAttributesIdSourceTerminalName = null;
            if (state != null
                && state.NativeParams.TryGetValue((uint)e.Id, out var wallNp)
                && wallNp.Fields.TryGetValue("m_WallAttributesId", out int watId) && watId > 0)
            {
                ownWallAttributesId = watId;
                if (byId.TryGetValue(watId, out var watElem))
                    wallAttributesIdCategory = watElem.Category;
                if (state.CompoundStructureTypeNameSource.TryGetValue((uint)watId, out var watSource))
                {
                    wallAttributesIdSourceBlockCount = watSource.BlockCount;
                    wallAttributesIdSourceTerminalName = watSource.TerminalName;
                }
            }

            int ownIsLevelFlag = 0;
            string? ownNativeMText = null;
            int typeIdIsLevelFlag = 0;
            string? typeIdNativeMText = null;
            if (state != null)
            {
                if (state.NativeParams.TryGetValue((uint)e.Id, out var ownNp2))
                {
                    ownIsLevelFlag = ownNp2.Fields.ContainsKey("__isLevel") ? 1 : 0;
                    ownNp2.StringFields.TryGetValue("m_text", out ownNativeMText);
                }
                if (e.TypeId > 0 && state.NativeParams.TryGetValue((uint)e.TypeId, out var typeNp))
                {
                    typeIdIsLevelFlag = typeNp.Fields.ContainsKey("__isLevel") ? 1 : 0;
                    typeNp.StringFields.TryGetValue("m_text", out typeIdNativeMText);
                }
            }

            string? familyViaTypeIdChain = null;
            string? familyViaFamilyTypeIdChain = null;
            if (state != null)
            {
                if (e.TypeId > 0
                    && state.NameEntryParentHolder.TryGetValue((uint)e.TypeId, out var tidHolder)
                    && state.NameHolderTarget.TryGetValue(tidHolder, out var tidFamId)
                    && state.ElementNameTable.TryGetValue(tidFamId, out var tidFamName))
                    familyViaTypeIdChain = tidFamName;
                if (e.FamilyTypeId > 0
                    && state.NameEntryParentHolder.TryGetValue((uint)e.FamilyTypeId, out var ftidHolder)
                    && state.NameHolderTarget.TryGetValue(ftidHolder, out var ftidFamId)
                    && state.ElementNameTable.TryGetValue(ftidFamId, out var ftidFamName))
                    familyViaFamilyTypeIdChain = ftidFamName;
            }
            int? serializedFamilyIdOwn = e.SerializedFamilyId;
            string? serializedFamilyIdOwnName = e.SerializedFamilyId is int sfio && sfio > 0
                && byId.TryGetValue(sfio, out var sfioElem) ? sfioElem.Name : null;
            int? serializedFamilyIdType = e.FamilyTypeId > 0
                && byId.TryGetValue(e.FamilyTypeId, out var sfitTypeElem)
                ? sfitTypeElem.SerializedFamilyId : null;
            string? serializedFamilyIdTypeName = serializedFamilyIdType is int sfit && sfit > 0
                && byId.TryGetValue(sfit, out var sfitElem) ? sfitElem.Name : null;

            int faces = e.Geometry?.Faces?.Count ?? 0;
            int solids = e.Geometry?.BrepSolids?.Count ?? 0;
            int instances = e.Geometry?.Instances?.Count ?? 0;
            var bbox = e.BoundingBox;

            w.WriteLine(string.Join(",",
                e.Id, 1, Cell(e.CategoryId), Csv(e.Category), Cell(e.TypeId), Cell(e.FamilyTypeId),
                Cell(e.HostId), Cell(e.SerializedHostId), Cell(hostCatId), Csv(hostCat),
                Csv(e.ExtraHostIds is { Count: > 0 } eh ? string.Join(";", eh) : null),
                Csv(e.ExplicitHostIds is { Count: > 0 } xh ? string.Join(";", xh) : null),
                Cell(e.SuperInstanceId), Cell(superCatId), Csv(superCat),
                Cell(workset), Cell(designOption), Cell(phaseCreated), Cell(phaseDemolished), Cell(level),
                e.IsDiscovered ? 1 : 0, e.IsBlockOnly ? 1 : 0,
                faces, solids, instances,
                Cell(bbox is { Length: >= 6 } ? bbox[0] : null),
                Cell(bbox is { Length: >= 6 } ? bbox[1] : null),
                Cell(bbox is { Length: >= 6 } ? bbox[2] : null),
                Cell(bbox is { Length: >= 6 } ? bbox[3] : null),
                Cell(bbox is { Length: >= 6 } ? bbox[4] : null),
                Cell(bbox is { Length: >= 6 } ? bbox[5] : null),
                Csv(e.References is { Count: > 0 } refs ? string.Join(";", refs) : null),
                e.IsNativeLevel ? 1 : 0, Cell(levelIsBuildingStory),
                Cell(e.SerializedFamilyId), Cell(e.SerializedUnplacedOwnerId), Cell(e.SerializedOwnerDbViewId),
                Cell(wallFunctionInstance), Cell(wallFunctionType),
                Cell(wallStructuralUsage), Cell(wallTopIsAttached), Cell(wallBottomIsAttached),
                Cell(floorParamIsStructural), Cell(roofSlopeParam),
                Csv(genericFinishParam), Csv(genericFinishParamType),
                Cell(namedIsExternalOccurrence), Cell(namedIsExternalType), Cell(namedIsExternalTypeBracket),
                Cell(hostFunctionParam), Cell(namedSlopeOccurrence),
                Csv(finishLayerNames),
                Cell(ownMasterSymbolId), Csv(e.Name), Csv(e.FamilyName),
                Csv(masterSymbolNameTableEntry), Csv(masterSymbolNativeScanName), Csv(masterSymbolResolvedName),
                Csv(ownNameTableEntry),
                Cell(rawElemTypeParam), Cell(rawElemTypeParamCategoryId), Csv(rawElemTypeParamCategory),
                Csv(rawElemTypeParamNameTableEntry),
                Cell(ownWallAttributesId), Csv(wallAttributesIdCategory),
                Cell(wallAttributesIdSourceBlockCount), Csv(wallAttributesIdSourceTerminalName),
                ownIsLevelFlag, Csv(ownNativeMText), typeIdIsLevelFlag, Csv(typeIdNativeMText),
                Csv(familyViaTypeIdChain), Csv(familyViaFamilyTypeIdChain),
                Cell(serializedFamilyIdOwn), Csv(serializedFamilyIdOwnName),
                Cell(serializedFamilyIdType), Csv(serializedFamilyIdTypeName)));
        }
        Console.Error.WriteLine($"[inspect-batch] {found} found, {missing} missing -> {outPath}");
    }

    private static int? IntParam(Element e, BuiltInParameter bip) =>
        e.LookupParameter(bip)?.Value is int v ? v : null;

    private static double? DoubleParam(Element e, BuiltInParameter bip) =>
        e.LookupParameter(bip)?.Value is double v ? v : null;

    private static string? StringParam(Element e, BuiltInParameter bip) =>
        e.LookupParameter(bip)?.Value is string v ? v : null;

    /// <summary>Lenient (space-stripped, case-insensitive) named-parameter lookup, matching
    /// IfcExporter's private ParameterNameEquals convention - duplicated here deliberately so
    /// this diagnostic-only tool has no dependency on IfcExporter internals.</summary>
    private static bool NameMatches(RvtIO.Model.Parameter parameter, string name) =>
        parameter.DefinitionName is string parameterName
        && string.Equals(
            parameterName.Replace(" ", null), name.Replace(" ", null),
            StringComparison.InvariantCultureIgnoreCase);

    private static int? NamedIntParam(Element e, string name)
    {
        foreach (var p in e.Parameters)
            if (NameMatches(p, name) && p.Value is int v) return v;
        return null;
    }

    private static double? NamedDoubleParam(Element e, string name)
    {
        foreach (var p in e.Parameters)
            if (NameMatches(p, name) && p.Value is double v) return v;
        return null;
    }

    private static string Cell(int? v) =>
        v.HasValue ? v.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

    private static string Cell(double? v) =>
        v.HasValue ? v.Value.ToString("G17", System.Globalization.CultureInfo.InvariantCulture) : "";

    private static string Csv(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        bool needsQuote = s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r');
        if (!needsQuote) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
