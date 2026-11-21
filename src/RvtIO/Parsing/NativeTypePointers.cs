namespace RvtIO.Parsing;

/// <summary>Element classes that keep the id of their type in a field of their own.
/// On every sampled element of the class where the field is set, the four identity parameters of the public API
/// (Type, Type Id, Family and Type, Family) equal the field.</summary>
internal static class NativeTypePointers
{
    /// <summary>Root class -> "Declaring:Name" of the field holding the type id.</summary>
    internal static readonly IReadOnlyDictionary<ushort, string> ByClass = new Dictionary<ushort, string>
    {
        [157] = "160:m_styleSymbolId",
        [332] = "160:m_styleSymbolId",
        [380] = "381:m_symbolId",
        [485] = "485:m_stairsRailingAttrId",
        [527] = "527:m_symbolId",
        [565] = "565:m_buildingPadTypeId",
        [598] = "602:m_idType",
        [642] = "642:m_ceilingAttributesId",
        [689] = "689:m_symbolId",
        [754] = "604:m_typeId",
        [804] = "804:m_typeId",
        [838] = "3430:m_symbolId",
        [986] = "108:m_dbViewTypeId",
        [993] = "108:m_dbViewTypeId",
        [995] = "108:m_dbViewTypeId",
        [1004] = "108:m_dbViewTypeId",
        [1005] = "108:m_dbViewTypeId",
        [1011] = "108:m_dbViewTypeId",
        [1016] = "108:m_dbViewTypeId",
        [1322] = "422:m_groupId",
        [1794] = "1794:m_masterSymbolId",
        [1860] = "1860:m_attrId",
        [1913] = "1913:m_floorAttributesId",
        [1989] = "1990:m_attrId",
        [1990] = "1990:m_attrId",
        [2070] = "2237:m_symbolId",
        [2108] = "2108:m_attrId",
        [2198] = "2196:m_symbolId",
        [2205] = "2237:m_symbolId",
        [2247] = "2247:m_symbolId",
        [2264] = "2264:m_symbolId",
        [2299] = "2299:m_attrId",
        [2320] = "381:m_symbolId",
        [2335] = "160:m_styleSymbolId",
        [2534] = "2534:m_typeId",
        [3054] = "1605:m_roofAttributesId",
        [3095] = "160:m_styleSymbolId",
        [3111] = "3111:m_stairsAttrId",
        [3134] = "602:m_idType",
        [3148] = "602:m_idType",
        [3177] = "602:m_idType",
        [3188] = "3170:m_typeId",
        [3210] = "602:m_idType",
        [3228] = "3170:m_typeId",
        [3253] = "602:m_idType",
        [3366] = "2937:m_symbolId",
        [3408] = "3408:m_attrId",
        [3475] = "3475:m_symbolId",
        [3502] = "2237:m_symbolId",
        [3524] = "362:m_WallAttributesId",
        [3657] = "160:m_styleSymbolId",
        [3681] = "3681:m_typeId",
        [3702] = "3702:m_typeId",
        [3727] = "3727:m_typeId",
        [3892] = "3892:m_typeId",
        [3971] = "3928:m_attrId",
        [3989] = "823:m_typeId",
        [4157] = "4157:m_typeId",
    };

    internal static readonly HashSet<string> Keys = ByClass.Values.ToHashSet(StringComparer.Ordinal);

    /// <summary>The type id the class keeps in its field, when the field is set.</summary>
    internal static int? Of(ushort rootClass, TfBlockDeserializer.NativeParams parameters)
        => ByClass.TryGetValue(rootClass, out string? key) && parameters.Fields.TryGetValue(key, out int id) && id > 0 ? id : null;
}
