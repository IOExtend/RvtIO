using System.Text;

namespace RvtIO.Parsing;

internal static partial class TfBlockDeserializer
{
    private static readonly UnicodeEncoding DocumentGraphUtf16 = new(false, false, true);
    internal sealed record DocumentGraphField(int Declaring, string Name, int Start, int End, int QueueIndex);
    internal sealed record DocumentGraphObject(ushort ClassId, int QueueIndex, int Start, int End);
    private sealed record DocumentGraphPrefix(int End, DocumentDeferredObject[] DeferredObjects,
        DocumentGraphObject[] Objects, DocumentGraphField[] Fields);
    internal sealed record DocumentUnitsLink(DocumentManagerPrefix Manager, int TrackingStart, int End,
        uint UnitsElemId, DocumentDeferredObject[] DeferredObjects,
        DocumentGraphObject[] Objects, DocumentGraphField[] Fields);

    // Typed constructor/callback bindings correct these recovered references in this reader only.
    // The shared schema and cache are not modified.
    private static readonly (int Class, string Field, byte Type, byte Mods, int? StoredRef, int NativeRef)[] DocumentGraphRefs =
    [
        (2546, "m_appInfoData", 14, 80, null, 2547),
        (2547, "second", 14, 1, 2547, 2541),
        (2546, "m_data", 14, 80, null, 2548),
        (2548, "second", 14, 0, null, 2549),
        (2544, "m_requestToken", 14, 0, null, 2545),
        (696, "m_secondaryId", 14, 0, 696, 30),
        (2546, "m_primary2SecondaryDataIdMap", 14, 80, null, 2552),
        (2552, "second", 14, 0, null, 2553),
        (2553, "m_dataIds", 14, 80, 2552, 2544),
        (1804, "m_arrLoadedFamilyInfo", 14, 80, null, 1805),
        (1805, "m_familyDocGUIDs", 14, 80, 1805, 30),
        (3609, "m_WPRMap", 14, 80, null, 3610),
        (3610, "second", 14, 0, null, 3611),
        (3611, "m_sketchGridId", 14, 0, 3610, 20),
        (1357, "m_symbols", 14, 80, null, 1358),
        (2561, "m_items", 14, 80, null, 2562),
        (2983, "m_topologyMap", 14, 80, null, 2984),
        (2984, "first", 14, 0, null, 2985),
        (2984, "second", 14, 0, 2984, 20),
        (2985, "m_zoneSchemeId", 14, 0, 2985, 20),
        (640, "m_gstyleData", 14, 80, null, 641),
        (641, "m_gstyleId", 14, 0, 641, 20),
        (1359, "m_symbols", 14, 80, null, 1360),
        (1360, "m_elemIdSet", 14, 80, 1360, 20),
        (3399, "m_refTableData", 14, 80, null, 3401),
        (3401, "second", 14, 0, 3401, 3397),
        (3399, "m_refTableKeyData", 14, 80, null, 3402),
        (3402, "first", 14, 0, null, 3403),
        (3402, "second", 14, 0, null, 3404),
        (3403, "m_categoryId", 14, 0, 3403, 20),
        (3404, "m_refTableIds", 14, 80, 3402, 20),
        (24, "second", 14, 0, 24, 20),
        (322, "second", 14, 0, 322, 20),
        (185, "second", 14, 0, null, 186),
        (186, "m_elementIds", 14, 80, 185, 20),
        (1479, "m_keyDataMap", 14, 80, null, 1480),
        (1480, "second", 14, 0, 1480, 1477),
        (561, "m_folderNameToIdMap", 14, 80, null, 562),
        (562, "second", 14, 0, null, 563),
        (561, "m_setExpandedNodes", 14, 80, null, 564),
        (115, "m_mapDuctLevelToOffsets", 14, 80, null, 116),
        (116, "second", 14, 0, null, 117),
        (115, "m_sysNavStateData", 14, 0, null, 118),
        (118, "m_mapColumnSettings", 14, 80, null, 119),
        (119, "second", 14, 0, null, 120),
        (120, "m_sysNavColumnSettings", 14, 0, null, 121),
        (121, "m_columnArray", 14, 80, null, 122),
        (32, "m_addInNames", 14, 80, null, 33),
        (33, "first", 14, 0, null, 34),
        (34, "m_value", 14, 0, 34, 30),
        (1365, "m_elemIdSetMap", 14, 80, null, 1366),
        (1366, "second", 14, 0, null, 1367),
        (1367, "m_idSet", 14, 80, 1366, 20),
        (3752, "second", 14, 0, 3752, 20),
        (1227, "m_modifyingUpdaterInfos", 14, 80, null, 1228),
        (1228, "first", 14, 0, null, 1229),
        (1228, "second", 14, 0, null, 1230),
        (1229, "m_value", 14, 0, 1229, 30),
        (1238, "m_storedForgeSchemas", 14, 80, null, 1239),
        (2633, "m_itemSet", 14, 80, null, 2634),
        (2634, "m_bindingIds", 14, 80, 2634, 20),
        // Nonempty basis maps precede ESSchemaStorage in upgraded projects (including the 2022 structural sample).
        (501, "m_mIdToBasis", 14, 80, null, 502),
        (502, "second", 14, 0, null, 503),
        (503, "m_creationEpisode", 14, 0, null, 504),
        (501, "m_mBasisToIdSet", 14, 80, null, 505),
        (505, "second", 14, 0, null, 506),
        (506, "m_sCommonBasisIds", 14, 80, 505, 20),
    ];

    /// <summary>Follows the exact own FIFO through the manager-owned UnitsTracking and returns its positive UnitsElem id.
    /// Not a complete Global acceptance, a partition-root match, a numeric conversion or Document.GetUnits.</summary>
    internal static DocumentUnitsLink? ExtractOwnDocumentUnitsLink(byte[] data)
        => ExtractOwnDocumentUnitsLink(data, out _);

    internal static DocumentUnitsLink? ExtractOwnDocumentUnitsLink(byte[] data, out string? failure)
    {
        var manager = ExtractOwnDocumentManagerPrefix(data);
        failure = "Unsupported header or manager";
        if (manager == null) return null;
        var graph = ReadOwnDocumentGraphPrefix(data, manager, manager.UnitsTrackingQueueIndex, out failure);
        if (graph == null) return null;
        if (graph.DeferredObjects[manager.UnitsTrackingQueueIndex].ClassId != 4030
            || graph.End > data.Length - 8 || BitConverter.ToUInt32(data, graph.End) != 1)
        { failure = "Malformed UnitsTracking or foreign document backreference"; return null; }
        int id = BitConverter.ToInt32(data, graph.End + 4);
        failure = id > 0 ? null : "Nonpositive UnitsElem ID";
        return id > 0 ? new(manager, graph.End, graph.End + 8, (uint)id,
            graph.DeferredObjects, graph.Objects, graph.Fields) : null;
    }

    private static DocumentGraphPrefix? ReadOwnDocumentGraphPrefix(byte[] data,
        DocumentManagerPrefix manager, int stopQueueIndex, out string? failure)
    {
        failure = "Unsupported recovered alias schema";
        if (!DocumentGraphRefs.All(alias =>
                Schemas.TryGetValue(alias.Class, out var schema) && Schemas.ContainsKey(alias.NativeRef)
                && schema.Fields.Any(field => field.Name == alias.Field && field.Type == alias.Type
                    && field.Mods == alias.Mods && field.Ref == alias.StoredRef))) return null;
        var cursor = new Cursor { D = data, Pos = manager.End, End = data.Length, RootClassId = 28 };
        var queue = manager.Header.DeferredObjects.ToList();
        foreach (var info in manager.OwnedAppInfos)
            queue.Add(new("AppInfoManager.m_appInfoArr", info.ClassId, info.Handle));
        var objects = new List<DocumentGraphObject>();
        var spans = new List<DocumentGraphField>();
        int fieldBudget = 1_000_000;
        string currentField = "before deferred FIFO";
        uint Count(long max = 100_000)
        {
            uint value = ReadU32(cursor);
            if (value == uint.MaxValue) return 0;
            if (value > max) throw Fail;
            return value;
        }
        void String()
        {
            uint length = ReadU32(cursor);
            if (length > 10_000 || length * 2L > cursor.End - cursor.Pos) throw Fail;
            DocumentGraphUtf16.GetString(data, cursor.Pos, (int)length * 2);
            cursor.Pos += (int)length * 2;
        }
        void Pointer(TfField field, bool owned)
        {
            uint handle = ReadU32(cursor);
            if (handle == 0 || !owned && handle != uint.MaxValue) return;
            if (!cursor.Take(2)) throw Fail;
            ushort cls = BitConverter.ToUInt16(data, cursor.Pos - 2);
            if (owned && cls == 0) return;
            if (cls == 4 || !Schemas.ContainsKey(cls) || queue.Count >= 100_000) throw Fail;
            if (field.Ref is int expected && !IsSubclassOf(cls, expected)) throw Fail;
            queue.Add(new(field.Name, cls, handle));
        }
        void Record(int cls, int index, int depth)
        {
            if (depth > 40) throw Fail;
            foreach (var field in SerializationFields(cls) ?? throw Fail) Field(field, index, depth);
        }
        void Scalar(TfField field, int index, int depth)
        {
            switch (field.Type)
            {
                case 1:
                    if (!cursor.Take(1) || data[cursor.Pos - 1] > 1) throw Fail;
                    break;
                case 2: case 3: if (!cursor.Take(1)) throw Fail; break;
                case 4: case 5: case 6: ReadU32(cursor); break;
                case 7: case 10: if (!cursor.Take(8)) throw Fail; break;
                case 8: String(); break;
                case 9: if (!cursor.Take(16)) throw Fail; break;
                case 13:
                    if (field.Cnt is not int n || n < 0 || n > 100_000 || !cursor.Take(n * 8)) throw Fail;
                    break;
                case 14:
                    int lo = field.Mods & 15;
                    if (lo == 3)
                    {
                        uint reference = ReadU32(cursor);
                        if ((field.Name is "m_pADoc" or "m_pDoc") && reference != 1) throw Fail;
                    }
                    else if (lo is 1 or 2) Pointer(field, true);
                    else if (lo != 0) throw Fail;
                    else if (field.Ref == 20) ReadI32(cursor);
                    else if (field.Ref.HasValue) Record(field.Ref.Value, index, depth + 1);
                    else Pointer(field, false);
                    break;
                default: throw Fail;
            }
        }
        void Field(TfField field, int index, int depth)
        {
            currentField = $"q{index}:{field.Declaring}.{field.Name}";
            if (depth > 40 || --fieldBudget < 0) throw Fail;
            int start = cursor.Pos;
            var stored = field;
            foreach (var alias in DocumentGraphRefs)
                if (field.Declaring == alias.Class && field.Name == alias.Field)
                { field = field with { Ref = alias.NativeRef }; break; }
            // The non-empty shape of this container is not bound to a known layout. An
            // empty serialized collection owns no payload; no width is guessed.
            if (field.Declaring == 2546 && field.Name == "m_enqueuedData2primaryParents")
            { if (Count() != 0) throw Fail; }
            else
            {
                int hi = field.Mods >> 4;
                if (hi == 6) String();
                else if (hi == 5 && field.Type is 2 or 3 && field.Ref == null)
                {
                    // a byte array (a stored steel model blob) is bounded by the data left, not by the collection limit
                    uint count = Count(cursor.End - cursor.Pos);
                    if (!cursor.Take((int)count)) throw Fail;
                }
                else if (hi is 1 or 5)
                {
                    uint count = hi == 5 ? Count() : field.Cnt is int n && n >= 0 && n <= 100_000 ? (uint)n : throw Fail;
                    for (uint element = 0; element < count; element++) Scalar(field, index, depth);
                }
                else if (hi == 0) Scalar(field, index, depth);
                else throw Fail;
            }
            spans.Add(new(stored.Declaring, stored.Name, start, cursor.Pos, index));
        }
        try
        {
            for (int index = manager.Header.AppInfoManagerQueueIndex!.Value + 1;
                index < stopQueueIndex; index++)
            {
                if (index >= queue.Count) throw Fail;
                int start = cursor.Pos; ushort cls = queue[index].ClassId;
                Record(cls, index, 0); objects.Add(new(cls, index, start, cursor.Pos));
            }
            failure = null;
            return new(cursor.Pos, queue.ToArray(), objects.ToArray(), spans.ToArray());
        }
        catch (DecodeFail) { failure = $"Unsupported/malformed field {currentField} at {cursor.Pos}"; return null; }
        catch (DecoderFallbackException) { failure = $"Invalid UTF16 {currentField} at {cursor.Pos}"; return null; }
    }
}
