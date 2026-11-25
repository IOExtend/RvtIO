namespace RvtIO.Parsing;

internal static partial class TfBlockDeserializer
{
    // Literal r22 descriptor layout, checked against the named and private entry
    // initializers and the unsigned offset sort. Not an inferred field map.
    private static readonly IReadOnlyDictionary<ushort, TfClass> GElementGraphSchemas =
        new Dictionary<ushort, TfClass>
        {
            [20] = new("ElementId", null,
                [new("m_id", 14, 0, null, null)]),
            [78] = new("Trf", null,
                [new("m_3x3", 13, 16, null, 3), new("m_or", 7, 16, 77, 3)]),
            [413] = new("ControlledConstDocAccess", null,
                [new("m_pDoc", 14, 3, 413, null)]),
            [522] = new("Plane", 523,
                [new("m_origin", 7, 16, null, 3), new("m_xVec", 7, 16, null, 3), new("m_yVec", 7, 16, 522, 3)]),
            [523] = new("Surface", null,
                [new("m_Envelope", 14, 0, null, null), new("m_orientFlag", 1, 0, null, null)]),
            [524] = new("Envelope", null,
                [new("m_corners", 13, 16, 524, 2)]),
            [983] = new("CylSurf", 523,
                [new("m_center", 7, 16, null, 3), new("m_xVec", 7, 16, null, 3), new("m_yVec", 7, 16, null, 3), new("m_zVec", 7, 16, null, 3), new("m_radius", 7, 0, null, null)]),
            [1243] = new("Edge", 1244,
                []),
            [1244] = new("GEdge", 1245,
                [new("m_pFace", 14, 19, null, 2), new("m_next", 14, 19, null, 2), new("m_prev", 14, 19, null, 2), new("m_interiorEdgePnts", 14, 80, null, null), new("m_firstAndLastEdgePnts", 14, 16, 1248, 2), new("m_flags", 2, 0, null, null)]),
            [1245] = new("GEdgeBase", 1246,
                []),
            [1246] = new("GNode", null,
                [new("m_GInfo", 14, 0, null, null)]),
            [1248] = new("EdgePnt", null,
                [new("uv", 13, 16, 1248, 2)]),
            [1253] = new("EdgeChainWithEnvelope", null,
                [new("m_pStartEdge", 14, 3, null, null), new("m_envelope", 14, 0, 524, null)]),
            [1254] = new("EdgeLoop", 1255,
                []),
            [1255] = new("GEdgeLoop", 1245,
                [new("m_nextLoop", 14, 1, null, null), new("m_pFace", 14, 3, null, null), new("m_next", 14, 3, null, null), new("m_prev", 14, 3, null, null), new("m_Envelope", 14, 0, 524, null), new("m_open", 1, 0, null, null)]),
            [1257] = new("EdgeLoopWithChainEnvelopes", 1254,
                [new("m_chainsWithEnvelopes", 14, 80, 1253, null)]),
            [1589] = new("Face", 1590,
                [new("m_pSurf", 14, 1, null, null)]),
            [1590] = new("GFace", 1246,
                [new("m_pFirstLoop", 14, 1, null, null), new("m_faceRegions", 14, 81, null, null), new("m_pGFilling", 14, 1, null, null), new("m_oBackgroundFilling", 14, 1, null, null), new("m_cutType", 4, 0, null, null), new("m_faceFlags_v9", 5, 0, null, null), new("m_renderStyleId", 14, 0, 20, null)]),
            [1736] = new("GLine", 1737,
                [new("m_origin", 7, 16, null, 3), new("m_dirVec", 7, 16, 1736, 3)]),
            [1737] = new("GCurve", 1246,
                [new("m_endParams", 7, 16, null, 2)]),
            [1761] = new("UV", null,
                [new("m_uv", 7, 16, 1761, 2)]),
            [1848] = new("FillGrid", null,
                [new("m_angle", 7, 0, null, null), new("m_origin", 7, 16, null, 2), new("m_deltas", 7, 16, null, 2), new("m_segs", 7, 80, null, null)]),
            [1850] = new("FillPatternData", null,
                [new("m_windowSize", 7, 0, null, null), new("m_lengthPerArea", 7, 0, null, null), new("m_strokesPerArea", 7, 0, null, null), new("m_linesPerLength", 7, 0, null, null), new("m_fillGrids", 14, 81, null, null)]),
            [1855] = new("FillPatternPlacer", null,
                [new("m_scale", 7, 0, null, null), new("m_origin", 7, 16, null, 2), new("m_dir", 7, 16, null, 2), new("m_uvScale", 14, 0, 1761, null), new("m_isMirrored", 1, 0, null, null), new("m_placedDraft", 1, 0, null, null)]),
            [1969] = new("TessEpsCntrl", null,
                [new("m_type", 4, 0, null, null), new("m_TessEpsCntrlVersion", 4, 0, 1969, null)]),
            [1974] = new("GArc", 1737,
                [new("m_xVec", 7, 16, null, 3), new("m_yVec", 7, 16, null, 3), new("m_radius", 7, 0, null, null), new("m_center", 7, 16, null, 3), new("m_bFilled", 1, 0, null, null)]),
            [1978] = new("GBRep", 1246,
                [new("m_pFaces", 14, 81, null, null)]),
            [1982] = new("GBitmap", 1246,
                [new("m_point", 7, 16, null, 3), new("m_size", 5, 16, null, 2), new("m_bitmapType", 4, 0, null, null), new("m_alignment", 4, 0, null, null)]),
            [1991] = new("GComponentRef", 1246,
                [new("m_instanceInfo", 14, 1, null, null)]),
            [1993] = new("GConditionBase", null,
                [new("m_comp", 4, 0, null, null)]),
            [1996] = new("GConditionDir", 1993,
                [new("m_dir", 7, 16, null, 3), new("m_negateDirCondition", 1, 0, null, null)]),
            [1999] = new("GConditionInt", 1993,
                [new("m_param", 4, 0, null, null), new("m_value", 4, 0, null, null)]),
            [2007] = new("GElement", 2008,
                []),
            [2008] = new("GRep", 2009,
                [new("m_bBox", 13, 16, null, 2), new("m_tightbBox", 13, 16, null, 2), new("m_gElemType", 4, 0, null, null), new("m_flags", 5, 0, null, null)]),
            [2009] = new("GGroup", 1246,
                [new("m_subNodes", 14, 81, null, null)]),
            [2014] = new("GFilling", 1246,
                [new("m_pGFace", 14, 3, null, null), new("m_placer", 14, 0, 1855, null), new("m_data", 14, 1, null, null), new("m_patternId", 14, 0, 20, null), new("m_fillColor", 5, 0, null, null), new("m_flags", 4, 0, null, null)]),
            [2015] = new("GFilter", 2009,
                [new("m_oConditions", 14, 81, null, null)]),
            [2017] = new("GGTag", 2009,
                [new("m_modelTestPoint", 7, 16, null, 3), new("m_bUseModelTestPoint", 1, 0, null, null), new("m_selectByModelTestPoint", 1, 0, null, null)]),
            [2032] = new("GPoint", 1246,
                [new("m_coord", 7, 16, null, 3), new("m_size", 4, 0, null, null), new("m_pointFlags", 4, 0, null, null)]),
            [2092] = new("Geometry", 1978,
                [new("m_flags", 4, 0, null, null), new("m_geometryTag", 4, 0, null, null), new("m_tessEpsCntrl", 14, 0, 1969, null), new("m_pEdges", 14, 81, null, null), new("m_sharedSurfInfo", 14, 81, null, null)]),
            [2237] = new("InstInfoBase", null,
                [new("m_Trf", 14, 0, 78, null), new("m_symbolId", 14, 0, 20, null)]),
            [2238] = new("InstanceInfo", 2237,
                [new("m_cda", 14, 0, 413, null)]),
        };

    private static readonly Dictionary<ushort, TfField[]> GElementGraphFieldsByClass =
        GElementGraphSchemas.Keys.ToDictionary(id => id, id => GElementGraphFields(id).ToArray());

    private static bool HasGElementGraphSchemas() => GElementGraphSchemas.All(pair =>
        Schemas.TryGetValue(pair.Key, out var actual) && actual.Name == pair.Value.Name
        && actual.Base == pair.Value.Base && actual.Fields.SequenceEqual(pair.Value.Fields));

    private static IEnumerable<TfField> GElementGraphFields(ushort classId)
    {
        var schema = GElementGraphSchemas[classId];
        if (schema.Base is int parent)
            foreach (var field in GElementGraphFields((ushort)parent)) yield return field;
        foreach (var field in schema.Fields) yield return field with { Declaring = classId };
    }
}
