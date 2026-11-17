using System.Buffers.Binary;

namespace RvtIO.Parsing;

/// <summary>
/// Rewrites the partition streams of a file of a later release (Revit 2023 and later, class schema read from the file) into the
/// Revit 2022 layout the geometry and element readers were written against: class tags renumbered by class NAME, ids narrowed to
/// 32 bits, fields compared by declaring class and field NAME (a field the 2022 class does not have is cut out, a 2022 field the
/// file does not have is written empty), and objects of classes the 2022 table does not know are left out. Item framing is the
/// one of <see cref="WideElementRecords"/>:
///   section = record x n, SEP, root block x n, SEP', dummy/geometry block x n, SEP'   (SEP = ff*W 00*8, SEP' = ff*W [u32 1] 00*8)
///   record  = [id:W][size:4][payload][size:4]            block = [id:W][adler32(payload):4][size:4][payload][size:4]
/// The payload walk is the field grammar of <see cref="WideBlockWalker"/> (breadth-first queue of owned objects); the edits it
/// finds (tag renumbering, id narrowing, cut and inserted fields, left-out objects with the count of their array) are applied
/// to the payload bytes, and the queued objects are written in the order of the target pointer fields (Revit 2024 and later
/// store many classes in another order). A field renamed between the releases is matched by an explicit map; an
/// <c>ESEntity</c> blob is read by the schema it names and left empty, since the target release keeps the schemas in another
/// field. <see cref="Stream"/> and <see cref="Framed"/> keep the block bytes with the root class tag <c>0xFFFE</c> when a block
/// cannot be rewritten; the downgrade writer leaves such an element out instead.
/// </summary>
internal sealed class R22Transcoder
{
    internal const ushort UnknownTag = 0xFFFE;

    private sealed class Fail(string message) : Exception(message);

    private readonly record struct Edit(int Start, int End, byte[] Replacement);

    private readonly Dictionary<int, SchemaClass> _source;
    private readonly Dictionary<int, SchemaClass> _target;
    private readonly SchemaDocument _targetDocument;
    private readonly Dictionary<string, int> _targetByName;
    private readonly Dictionary<int, int> _tagMap = new();
    private readonly int _sourceWidth;
    private readonly int _sourceIdentifier;
    private readonly int _targetIdentifierWidth;
    private readonly bool _tagMapping;
    private HashSet<uint> _presentElements = [];
    private uint _maxElementId;
    private readonly SchemaField[]?[]? _entityFields;
    private readonly Dictionary<Guid, int>? _entityIndexOf;
    private readonly Dictionary<Guid, SchemaField[]?>? _entityTargetSchemas;
    private readonly Dictionary<int, Plan> _plans = new();

    /// <summary>Source fields in serialization order, the target class of the same name and the target field positions by (declaring class, field).</summary>
    private sealed record Plan((SchemaClass Declaring, SchemaField Field)[] Source, (SchemaClass Declaring, SchemaField Field)[] Target,
        int[] TargetIndex, bool[] TargetPresent, bool Reordered, bool[] Narrow);

    internal R22Transcoder(SchemaDocument source, SchemaDocument target, int sourceIdWidth,
        IReadOnlyDictionary<Guid, SchemaField[]?>? entitySchemas = null, bool tagMapping = false)
    {
        _tagMapping = tagMapping;
        _source = source.Classes.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        _target = target.Classes.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        _targetDocument = target;
        _targetByName = new(StringComparer.Ordinal);
        foreach (var c in target.Classes.OrderBy(c => c.Id)) _targetByName.TryAdd(c.Name, c.Id);
        foreach (var c in source.Classes)
        {
            if (_targetByName.TryGetValue(c.Name, out int id)
                || (RenamedClasses.TryGetValue(c.Name, out string? renamedClass) && _targetByName.TryGetValue(renamedClass, out id))
                || (_tagMapping && TagRenamedClasses.TryGetValue(c.Name, out string? renamed) && _targetByName.TryGetValue(renamed, out id))
                || (ProjectedClasses.TryGetValue(c.Name, out var projection) && _targetByName.TryGetValue(projection.TargetClass, out id)))
                _tagMap[c.Id] = id;
        }
        _sourceWidth = sourceIdWidth;
        _sourceIdentifier = source.Classes.FirstOrDefault(c => c.Name == "ElementId")?.Id ?? -1;
        // the width of an ElementId value in the target file: 8 bytes when the identifier class stores m_id64, else 4
        _targetIdentifierWidth = target.Classes.FirstOrDefault(c => c.Name == "Identifier") is { } targetIdentifier
            && targetIdentifier.Fields.Any(f => f.Stored && f.Name == "m_id64" && f.Type == 11) ? 8 : 4;
        if (entitySchemas is { Count: > 0 })
        {
            _entityFields = [.. entitySchemas.Values];
            _entityIndexOf = entitySchemas.Keys.Select((guid, index) => (guid, index)).ToDictionary(pair => pair.guid, pair => pair.index);
            // the same schemas with the class references of the target table: the check walk reads the
            // written entity values with them, exactly as the older Revit will
            _entityTargetSchemas = entitySchemas.ToDictionary(
                pair => pair.Key,
                pair => pair.Value is null ? (SchemaField[]?)null : [.. pair.Value.Select(MapToTarget)]);
        }
    }

    /// <summary>The class references of a field through the source-to-target map (template arguments too).</summary>
    private SchemaField MapToTarget(SchemaField field) => field with
    {
        Ref = field.Ref is int reference ? _tagMap.GetValueOrDefault(reference) is int target && target != 0 ? target : null : null,
        Ref2 = field.Ref2 is int reference2 ? _tagMap.GetValueOrDefault(reference2) is int target2 && target2 != 0 ? target2 : null : null,
        Args = field.Args is null ? null : [.. field.Args.Select(MapToTarget)],
    };

    /// <summary>The class number of the 2022 table for a class of the file, 0 when it has none.</summary>
    internal int TargetId(int sourceClass) => _tagMap.GetValueOrDefault(sourceClass);

    /// <summary>True when the file's own class table declares the class.</summary>
    internal bool HasSourceClass(int sourceClass) => _source.ContainsKey(sourceClass);

    /// <summary>
    /// The element ids the written file holds (<paramref name="present"/>) and the highest one:
    /// a reference to an id inside that range which is not present is dangling and is written as
    /// invalid (<c>-1</c>), so the older Revit does not follow it into an element that is gone.
    /// Ids above the highest present one are left alone: the upgrade may create them.
    /// </summary>
    internal void SetElementUniverse(IReadOnlySet<uint> present, uint maxElementId)
    {
        _presentElements = [.. present];
        _maxElementId = maxElementId;
    }


    /// <summary>The chunks of one partition stream in the 2022 layout, cut at item boundaries; the input is the same stream
    /// concatenated. Bytes outside the section grammar are copied as they are.</summary>
    internal List<byte[]> Stream(IReadOnlyList<byte[]> chunks, out Stats stats)
    {
        long total = 0;
        foreach (var c in chunks) total += c.Length;
        var buf = new byte[total];
        long off = 0;
        foreach (var c in chunks) { Buffer.BlockCopy(c, 0, buf, (int)off, c.Length); off += c.Length; }
        var result = new List<byte[]>();
        var output = new MemoryStream();
        stats = new Stats();
        int w = _sourceWidth;
        int n = buf.Length, p = 0;
        void Flush(bool force)
        {
            if (output.Length > 0 && (force || output.Length >= 1 << 20)) { result.Add(output.ToArray()); output.SetLength(0); }
        }
        void Separator(int length, bool counted)
        {
            // ff*W [u32 k]? 00*8 in the source width, ff*4 ... in the target width
            var sep = new byte[(counted ? 4 : 0) + 4 + 8];
            sep.AsSpan(0, 4).Fill(0xFF);
            if (counted) BinaryPrimitives.WriteUInt32LittleEndian(sep.AsSpan(4), BitConverter.ToUInt32(buf, p + w));
            output.Write(sep);
            p += length;
        }
        bool IsSeparator(int at) => at + w <= n && buf.AsSpan(at, w).IndexOfAnyExcept((byte)0xFF) < 0;
        bool HasRecordlessSectionAhead(int at)
        {
            if (at + w + 8 > n || !IsSeparator(at) || buf.AsSpan(at + w, 8).IndexOfAnyExcept((byte)0) >= 0) return false;
            int q = at + w + 8;
            while (q + w + 12 <= n && !IsSeparator(q) && TryBlock(buf, q, w, out int len)) q += len;
            if (q + w + 12 > n || !IsSeparator(q) || buf.AsSpan(q + w + 4, 8).IndexOfAnyExcept((byte)0) >= 0) return false;
            q += w + 12;
            while (q + w + 12 <= n && !IsSeparator(q) && TryBlock(buf, q, w, out int len)) q += len;
            return q + w + 12 <= n && IsSeparator(q) && buf.AsSpan(q + w + 4, 8).IndexOfAnyExcept((byte)0) < 0;
        }
        while (p < n)
        {
            // section: records
            int recordsAt = p;
            while (p + w + 8 <= n && !IsSeparator(p) && TryRecord(buf, p, w, out int length))
            {
                WriteRecord(buf, p, output, stats);
                p += length;
                Flush(false);
            }
            if (!IsSeparator(p) || p + w + 8 > n || (p == recordsAt && !HasRecordlessSectionAhead(p))) break;
            Separator(w + 8, false);
            // root blocks
            while (p + w + 12 <= n && !IsSeparator(p) && TryBlock(buf, p, w, out int length))
            {
                WriteBlock(buf, p, output, stats, root: true);
                p += length;
                Flush(false);
            }
            if (!IsSeparator(p) || p + w + 12 > n) break;
            Separator(w + 12, true);
            // second blocks (one per record: placeholders and geometry)
            while (p + w + 12 <= n && !IsSeparator(p) && TryBlock(buf, p, w, out int length))
            {
                WriteBlock(buf, p, output, stats, root: false);
                p += length;
                Flush(false);
            }
            if (!IsSeparator(p) || p + w + 12 > n) break;
            Separator(w + 12, true);
            Flush(true);
        }
        if (p < n) output.Write(buf, p, n - p);
        Flush(true);
        stats.UnparsedBytes = n - p;
        stats.StoppedAt = p;
        if (p < n) stats.StoppedBytes = Convert.ToHexString(buf, p, Math.Min(48, n - p));
        return result;
    }

    /// <summary>The channel-102 and 103 records of the project's own block of one framed stream in the 2022 layout, each rewritten as
    /// <see cref="Stream"/> rewrites a block and none running into the next segment; channel 101 is left out. Null when the frame's id
    /// width is not the one of the file's schema.</summary>
    internal PartitionFraming.Framed? Framed(PartitionFraming.Framed source, out Stats stats)
    {
        stats = new Stats();
        if (source.IdWidth != _sourceWidth) return null;
        var segments = new List<byte[]>();
        var items = new List<PartitionFraming.ProjectItem>();
        var output = new MemoryStream();
        long flushed = 0;
        foreach (var item in source.Items)
        {
            if (item.Channel == 101 || item.Length < _sourceWidth + 14) continue;
            var (data, start, _) = source.Bytes(item);
            long at = flushed + output.Length;
            WriteBlock(data, start, output, stats, root: item.Channel == 102);
            items.Add(new(item.Channel, item.Owner, at, (int)(flushed + output.Length - at)));
            if (output.Length >= 1 << 20) { flushed += output.Length; segments.Add(output.ToArray()); output.SetLength(0); }
        }
        if (output.Length > 0) segments.Add(output.ToArray());
        return new(segments, items, 4, source.Partition);
    }

    internal sealed class Stats
    {
        public int Records, RecordsRewritten, RootBlocks, RootBlocksRewritten, OtherBlocks, OtherBlocksRewritten;
        public long UnparsedBytes;
        public long StoppedAt;
        public string? StoppedBytes;
        public readonly Dictionary<string, int> Failures = new();
    }

    private static bool TryRecord(byte[] b, int p, int w, out int length)
    {
        length = 0;
        if (p + w + 8 > b.Length) return false;
        uint size = BitConverter.ToUInt32(b, p + w);
        if (size < 2 || size > 8_000_000 || p + (long)w + 8 + size > b.Length || BitConverter.ToUInt32(b, p + w + 4 + (int)size) != size) return false;
        length = w + 8 + (int)size;
        return true;
    }

    private static bool TryBlock(byte[] b, int p, int w, out int length)
    {
        length = 0;
        if (p + w + 12 > b.Length) return false;
        uint size = BitConverter.ToUInt32(b, p + w + 4);
        if (size < 2 || size > 64_000_000 || p + (long)w + 12 + size > b.Length || BitConverter.ToUInt32(b, p + w + 8 + (int)size) != size) return false;
        length = w + 12 + (int)size;
        return true;
    }

    private void WriteId(byte[] b, int p, MemoryStream output)
    {
        Span<byte> id = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(id, BitConverter.ToUInt32(b, p));
        output.Write(id);
    }

    private void WriteRecord(byte[] b, int p, MemoryStream output, Stats stats)
    {
        int w = _sourceWidth;
        int size = (int)BitConverter.ToUInt32(b, p + w);
        stats.Records++;
        var payload = Payload(b, p + w + 4, size, out var failure);
        if (payload != null) stats.RecordsRewritten++;
        else
        {
            string reason = "record " + (failure ?? "unknown");
            stats.Failures[reason] = stats.Failures.GetValueOrDefault(reason) + 1;
        }
        payload ??= Unknown(b, p + w + 4, size);
        WriteId(b, p, output);
        WriteFramed(output, payload, adler: false);
    }

    private void WriteBlock(byte[] b, int p, MemoryStream output, Stats stats, bool root)
    {
        int w = _sourceWidth;
        int size = (int)BitConverter.ToUInt32(b, p + w + 4);
        var payload = Payload(b, p + w + 8, size, out var failure);
        if (payload == null)
        {
            string reason = failure ?? "unknown";
            stats.Failures[reason] = stats.Failures.GetValueOrDefault(reason) + 1;
        }
        if (root) { stats.RootBlocks++; if (payload != null) stats.RootBlocksRewritten++; }
        else { stats.OtherBlocks++; if (payload != null) stats.OtherBlocksRewritten++; }
        payload ??= Unknown(b, p + w + 8, size);
        WriteId(b, p, output);
        WriteFramed(output, payload, adler: true);
    }

    private static byte[] Unknown(byte[] b, int offset, int size)
    {
        var copy = b.AsSpan(offset, size).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(copy, UnknownTag);
        return copy;
    }

    private static void WriteFramed(MemoryStream output, byte[] payload, bool adler)
    {
        Span<byte> word = stackalloc byte[4];
        if (adler)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(word, Adler32(payload));
            output.Write(word);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(word, (uint)payload.Length);
        output.Write(word);
        output.Write(payload);
        output.Write(word);
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (byte x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }


    private sealed class Context(byte[] data, int pos, int end)
    {
        public readonly byte[] D = data;
        public int Pos = pos;
        public readonly int End = end;
        public readonly List<(int Tag, bool Dropped)> Queue = [];
        public readonly List<Edit> Edits = [];
        /// <summary>The source offset at which each queued object starts, in queue (storage) order.</summary>
        public readonly List<int> ObjectStarts = [];
        /// <summary>Per object slot (root = -1, queued object i = i) the queue ranges its pointer fields created, by target field index.</summary>
        public readonly Dictionary<int, List<(int Key, int Start, int End, int Seq)>> Groups = [];
        /// <summary>The object whose fields are being read: -1 at the root.</summary>
        public int Slot = -1;
        /// <summary>The target index of the top-level field being read, inherited by inline structures.</summary>
        public int FieldKey = int.MaxValue;
        public int GroupSeq;
        /// <summary>The payload reorders pointer fields or class fields, so its structure is checked against the target schema.</summary>
        public bool NeedsValidation;
        public bool Cutting;
        public int Dropped;
        public bool InArray;
        /// <summary>Inside a counted array, also through inline structures (its items must keep their stored order).</summary>
        public bool DeepInArray;
        /// <summary>The 64-bit scalars being read take the place of 32-bit 2022 fields.</summary>
        public bool Narrow;
        /// <summary>The field being read ("Declaring.field"), for failure messages.</summary>
        public string? Field;
        /// <summary>The last ForgeTypeId text read (a parameter group type id).</summary>
        public string? LastForgeText;
        /// <summary>The built-in parameter group element id the current ParamDef names (its source has a type id instead).</summary>
        public int? GroupElementId;
    }

    /// <summary>The payload (class tag first) in the 2022 layout, or null when it cannot be rewritten.</summary>
    internal byte[]? Payload(byte[] data, int offset, int size) => Payload(data, offset, size, out _);

    /// <summary>The payload in the 2022 layout with every byte after the object copied unchanged (a stream that holds one
    /// class object followed by opaque data), or null when the object cannot be rewritten.</summary>
    internal byte[]? PayloadWithTail(byte[] data, int offset, int size, out string? failure)
    {
        byte[]? prefix = PayloadObject(data, offset, size, out int objectEnd, out failure);
        if (prefix is null) return null;
        if (objectEnd == offset + size) return prefix;
        var result = new byte[prefix.Length + (offset + size - objectEnd)];
        prefix.CopyTo(result, 0);
        Buffer.BlockCopy(data, objectEnd, result, prefix.Length, offset + size - objectEnd);
        return result;
    }

    /// <param name="failure">Why the payload cannot be rewritten (the root class and the first rule that failed), null when it can.</param>
    internal byte[]? Payload(byte[] data, int offset, int size, out string? failure)
    {
        byte[]? rewritten = PayloadObject(data, offset, size, out int objectEnd, out failure);
        if (rewritten is null) return null;
        if (objectEnd != offset + size)
        {
            failure = (_source.TryGetValue(BitConverter.ToUInt16(data, offset), out var root) ? root.Name : "class")
                + ": bytes after the end of the object";
            return null;
        }
        return rewritten;
    }

    private byte[]? PayloadObject(byte[] data, int offset, int size, out int objectEnd, out string? failure)
    {
        failure = null;
        objectEnd = offset;
        if (size < 2 || offset + (long)size > data.Length) { failure = "short payload"; return null; }
        int rootClass = BitConverter.ToUInt16(data, offset);
        if (!_tagMap.ContainsKey(rootClass))
        {
            failure = (_source.TryGetValue(rootClass, out var unknown) ? unknown.Name : "tag " + rootClass) + ": root class without a 2022 counterpart";
            return null;
        }
        var c = new Context(data, offset + 2, offset + size);
        try
        {
            c.Edits.Add(new(offset, offset + 2, Tag(_tagMap[rootClass])));
            ReadObject(c, rootClass);
            for (int i = 0; i < c.Queue.Count; i++)
            {
                c.ObjectStarts.Add(c.Pos);
                c.Slot = i;
                var (tag, dropped) = c.Queue[i];
                if (tag < 0)
                {
                    // the values of an extensible-storage entity: no class of the table, read by the fields of its schema
                    ReadObject(c, tag);
                    c.Slot = -1;
                    continue;
                }
                if (dropped || !_tagMap.ContainsKey(tag))
                {
                    // an object of a class the 2022 table does not know: read it with the source grammar and cut it out.
                    // Only edits strictly inside the cut region are discarded: a zero-width insertion exactly at its
                    // start belongs to the previous object (for example a default field value appended to it) and stays.
                    int start = c.Pos, mark = c.Queue.Count;
                    c.Cutting = true;
                    ReadObject(c, tag);
                    c.Cutting = false;
                    c.Edits.RemoveAll(e => e.Start > start || (e.Start == start && e.End > start));
                    c.Edits.Add(new(start, c.Pos, []));
                    for (int j = mark; j < c.Queue.Count; j++) c.Queue[j] = (c.Queue[j].Tag, true);
                    c.Slot = -1;
                    continue;
                }
                ReadObject(c, tag);
                c.Slot = -1;
            }
            objectEnd = c.Pos;
        }
        catch (Fail fail)
        {
            failure = _source[rootClass].Name + ": " + fail.Message + (c.Field is null ? "" : " at " + c.Field);
            return null;
        }
        try
        {
            byte[] assembled = Assemble(c, offset, c.Pos);
            if (c.NeedsValidation)
            {
                var checkWalker = new WideBlockWalker(_targetDocument, (_, _) => null, []);
                if (_entityTargetSchemas is not null) checkWalker.UseEntitySchemas(_entityTargetSchemas, passUnknown: true);
                var walk = checkWalker.Run(assembled, 0, assembled.Length, BitConverter.ToUInt16(assembled, 0));
                if (!walk.Exact || walk.Error != null)
                {
                    failure = _source[rootClass].Name + ": the rewritten object does not read to its end"
                        + (walk.Error is null ? "" : " (" + walk.Error + ")")
                        + (walk.StopField is null ? "" : " at " + walk.StopField);
                    return null;
                }
            }
            return assembled;
        }
        catch (Fail fail)
        {
            failure = _source[rootClass].Name + ": " + fail.Message + (c.Field is null ? "" : " at " + c.Field);
            return null;
        }
    }

    /// <summary>
    /// The rewritten payload: the root fields, then the queued objects in the order of the target
    /// pointer fields (breadth-first over the recorded field groups), with the objects of dropped
    /// classes left out. Each object's bytes are its own source range with its own edits.
    /// </summary>
    private static byte[] Assemble(Context c, int offset, int objectEnd)
    {
        int n = c.ObjectStarts.Count;
        int rootEnd = n > 0 ? c.ObjectStarts[0] : objectEnd;
        var bySlot = new List<Edit>[n + 1];
        for (int i = 0; i <= n; i++) bySlot[i] = [];
        foreach (var edit in c.Edits)
        {
            int slot = 0;
            if (n > 0)
            {
                // an insertion exactly at an object start belongs to the object before it (its
                // trailing default fields), so it is looked up with a strict comparison
                bool insertion = edit.Start == edit.End;
                int lo = 0, hi = n - 1, found = -1;
                while (lo <= hi)
                {
                    int mid = (lo + hi) / 2;
                    bool atOrBefore = insertion ? c.ObjectStarts[mid] < edit.Start : c.ObjectStarts[mid] <= edit.Start;
                    if (atOrBefore) { found = mid; lo = mid + 1; }
                    else hi = mid - 1;
                }
                slot = found + 1;
            }
            bySlot[slot].Add(edit);
        }

        var order = new List<int>();
        var emitted = new bool[n];
        void Children(int slot)
        {
            if (!c.Groups.TryGetValue(slot, out var groups)) return;
            foreach (var group in groups.OrderBy(g => g.Key).ThenBy(g => g.Seq))
                for (int i = group.Start; i < group.End; i++)
                    if (!c.Queue[i].Dropped && !emitted[i])
                    {
                        emitted[i] = true;
                        order.Add(i);
                    }
        }
        Children(-1);
        for (int i = 0; i < order.Count; i++) Children(order[i]);

        using var output = new MemoryStream();
        output.Write(Apply(c.D, offset, rootEnd, bySlot[0]));
        foreach (int q in order)
        {
            int start = c.ObjectStarts[q];
            int end = q + 1 < n ? c.ObjectStarts[q + 1] : objectEnd;
            output.Write(Apply(c.D, start, end, bySlot[q + 1]));
        }
        return output.ToArray();
    }

    /// <summary>The bytes <c>[start, end)</c> with the edits applied (insertions sort before the replacement that starts at the same place).</summary>
    private static byte[] Apply(byte[] data, int start, int end, List<Edit> edits)
    {
        var ordered = edits.OrderBy(e => e.Start).ThenBy(e => e.End > e.Start ? 1 : 0).ToList();
        var output = new byte[end - start + ordered.Sum(e => e.Replacement.Length - (e.End - e.Start))];
        int at = 0, source = start;
        foreach (var e in ordered)
        {
            int gap = e.Start - source;
            if (gap < 0 || e.End > end) throw new Fail("overlapping edits");
            Buffer.BlockCopy(data, source, output, at, gap);
            at += gap;
            Buffer.BlockCopy(e.Replacement, 0, output, at, e.Replacement.Length);
            at += e.Replacement.Length;
            source = e.End;
        }
        Buffer.BlockCopy(data, source, output, at, end - source);
        return output;
    }

    private static byte[] Tag(int id)
    {
        var tag = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(tag, (ushort)id);
        return tag;
    }

    private Plan PlanOf(int sourceClass)
    {
        if (_plans.TryGetValue(sourceClass, out var plan)) return plan;
        if (!_tagMap.TryGetValue(sourceClass, out int targetClass))
            throw new Fail("class " + (_source.GetValueOrDefault(sourceClass)?.Name ?? sourceClass.ToString()) + " without a 2022 counterpart");
        var source = Chain(_source, sourceClass);
        var target = Chain(_target, targetClass);
        var index = new Dictionary<(string, string), int>();
        for (int i = 0; i < target.Length; i++) index[(target[i].Declaring.Name, target[i].Field.Name)] = i;
        string TargetName(string declaring, string field) =>
            TryRenamed(declaring, field, out var rename) ? rename.Name : field;
        var sourceKeys = source.Select(s => (s.Declaring.Name, TargetName(s.Declaring.Name, s.Field.Name))).ToHashSet();
        var targetIndex = source.Select(s => index.GetValueOrDefault((s.Declaring.Name, TargetName(s.Declaring.Name, s.Field.Name)), -1)).ToArray();
        // Revit 2024 widened some 32-bit fields to 64 bits under a new name ("m_id" became "m_id64", "m_otherInt" "m_otherInt64"):
        // a 64-bit source field takes the place of the 32-bit 2022 field of the name without the "64", when the file has no field of that name
        var narrow = new bool[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            var (declaring, field) = source[i];
            int at = field.Name.LastIndexOf("64", StringComparison.Ordinal);
            if (targetIndex[i] >= 0 || field.Type != 11 || at < 0) continue;
            string legacy = field.Name.Remove(at, 2);
            if (sourceKeys.Contains((declaring.Name, legacy)) || !index.TryGetValue((declaring.Name, legacy), out int k)
                || target[k].Field.Type is not (4 or 5) || target[k].Field.Mods != field.Mods) continue;
            targetIndex[i] = k;
            narrow[i] = true;
        }
        // A renamed source field takes the place of its target name when the source has no field of that name.
        for (int i = 0; i < source.Length; i++)
        {
            var (declaring, field) = source[i];
            if (targetIndex[i] < 0 || !TryRenamed(declaring.Name, field.Name, out var rename) || !rename.Narrow) continue;
            var t = target[targetIndex[i]];
            if (field.Type != 11 || t.Field.Type is not (4 or 5) || t.Field.Mods != field.Mods)
                throw new Fail($"renamed field {declaring.Name}.{field.Name} does not fit {t.Declaring.Name}.{t.Field.Name}");
            for (int other = 0; other < i; other++)
                if (targetIndex[other] == targetIndex[i]) throw new Fail($"renamed field {declaring.Name}.{field.Name} is claimed twice");
            narrow[i] = true;
        }
        // A field the target declares in another class of the chain (a base class renamed between
        // the releases): match it by its own name when the type and modifiers agree and it is the
        // only free target field of that name.
        {
            var claimed = new HashSet<int>(targetIndex.Where(k => k >= 0));
            var unmatched = Enumerable.Range(0, source.Length).Where(i => targetIndex[i] < 0).ToList();
            foreach (int i in unmatched)
            {
                var field = source[i].Field;
                string targetName = TargetName(source[i].Declaring.Name, field.Name);
                if (unmatched.Count(j => TargetName(source[j].Declaring.Name, source[j].Field.Name) == targetName) != 1) continue;
                int candidate = -1;
                for (int t = 0; t < target.Length; t++)
                {
                    if (claimed.Contains(t) || target[t].Field.Name != targetName) continue;
                    if (target[t].Field.Type != field.Type || target[t].Field.Mods != field.Mods) continue;
                    if (candidate >= 0) { candidate = -2; break; }
                    candidate = t;
                }
                if (candidate < 0) continue;
                targetIndex[i] = candidate;
                claimed.Add(candidate);
            }
        }
        var present = target.Select(t => sourceKeys.Contains((t.Declaring.Name, t.Field.Name))).ToArray();
        for (int i = 0; i < source.Length; i++)
            if (targetIndex[i] >= 0) present[targetIndex[i]] = true;
        // the fields both classes have are stored in another order (Revit 2024 and later store many classes in a changed order)
        var kept = targetIndex.Where(k => k >= 0).ToArray();
        bool reordered = kept.Zip(kept.Skip(1), (a, b) => b < a).Any(x => x);
        return _plans[sourceClass] = new Plan(source, target, targetIndex, present, reordered, narrow);
    }

    private static (SchemaClass Declaring, SchemaField Field)[] Chain(Dictionary<int, SchemaClass> classes, int id)
    {
        var order = new List<SchemaClass>();
        for (int guard = 0; id != 0 && guard < 64; guard++)
        {
            if (!classes.TryGetValue(id, out var c)) throw new Fail("class " + id + " has no schema");
            order.Add(c);
            id = c.Base;
        }
        order.Reverse();
        return order.SelectMany(c => c.Fields.Where(f => f.Stored).Select(f => (c, f))).ToArray();
    }

    private void ReadObject(Context c, int sourceClass)
    {
        if (sourceClass < 0)
        {
            int layout = -1 - sourceClass;
            if (_entityFields is null || layout >= _entityFields.Length || _entityFields[layout] is not { } entityFields)
                throw new Fail("entity schema fields are not known");
            foreach (var field in entityFields) ReadField(c, "ESBlob", field, 0);
            return;
        }
        if (c.Cutting)
        {
            foreach (var (declaring, field) in Chain(_source, sourceClass)) ReadField(c, declaring.Name, field, 0);
            return;
        }
        ReadFields(c, sourceClass, 0);
    }

    /// <summary>The fields of an object or inline structure compared with the 2022 class: cut, inserted, or (when the common fields are stored
    /// in another order) rewritten in the order of the 2022 class.</summary>
    private void ReadFields(Context c, int sourceClass, int depth)
    {
        if (ProjectedClasses.TryGetValue(_source[sourceClass].Name, out var projected))
        {
            ReadProjectedClass(c, sourceClass, projected.SubField, depth);
            return;
        }
        var plan = PlanOf(sourceClass);
        if (plan.Reordered) c.NeedsValidation = true;
        int regionStart = c.Pos, regionEdits = c.Edits.Count;
        var spans = new (int Start, int End, int EditFrom, int EditTo)[plan.Source.Length];
        int last = -1, lastQueuing = -1;
        for (int i = 0; i < plan.Source.Length; i++)
        {
            int k = plan.TargetIndex[i];
            int start = c.Pos, editFrom = c.Edits.Count, queued = c.Queue.Count;
            if (k < 0)
            {
                // a field the 2022 class does not have: read with the source grammar and cut out
                c.Cutting = true;
                ReadField(c, plan.Source[i].Declaring.Name, plan.Source[i].Field, depth);
                c.Cutting = false;
                if (plan.Source[i].Declaring.Name == "ParamDef" && plan.Source[i].Field.Name == "m_groupTypeId" && c.LastForgeText is { } cutGroup)
                    c.GroupElementId = ParameterGroupElementIds.GetValueOrDefault(cutGroup, -1);
                c.Edits.RemoveRange(editFrom, c.Edits.Count - editFrom);
                c.Edits.Add(new(start, c.Pos, []));
                spans[i] = (-1, -1, 0, 0);
                continue;
            }
            if (!plan.Reordered)
            {
                for (int j = last + 1; j < k; j++)
                    if (!plan.TargetPresent[j]) c.Edits.Add(new(start, start, DefaultFor(c, plan.Target[j])));
                last = Math.Max(last, k);
                editFrom = c.Edits.Count;
            }
            c.Narrow = plan.Narrow[i];
            if (depth == 0) c.FieldKey = k;
            if (TryRenamed(plan.Source[i].Declaring.Name, plan.Source[i].Field.Name, out var projection)
                && projection.SubField is { } subField && k >= 0)
                ReadProjectedField(c, plan.Source[i].Field, subField, depth);
            else
                ReadField(c, plan.Source[i].Declaring.Name, plan.Source[i].Field, depth);
            c.Narrow = false;
            if (depth == 0 && plan.Source[i].Declaring.Name == "ParamDef" && plan.Source[i].Field.Name == "m_groupTypeId" && c.LastForgeText is { } group)
                c.GroupElementId = ParameterGroupElementIds.GetValueOrDefault(group, -1);
            spans[i] = (start, c.Pos, editFrom, c.Edits.Count);
            if (c.Queue.Count != queued)
            {
                // the objects of the pointers follow in the order of the fields; at the top level
                // the queue is put into the order of the target fields when the payload is assembled
                if (depth > 0 && k < lastQueuing) throw new Fail("pointer fields change their order");
                if (depth == 0 && k < lastQueuing) c.NeedsValidation = true;
                if (k >= 0)
                {
                    if (!c.Groups.TryGetValue(c.Slot, out var groups)) c.Groups[c.Slot] = groups = [];
                    groups.Add((c.FieldKey, queued, c.Queue.Count, c.GroupSeq++));
                }
                lastQueuing = k;
            }
        }
        if (!plan.Reordered)
        {
            for (int j = last + 1; j < plan.Target.Length; j++)
                if (!plan.TargetPresent[j]) c.Edits.Add(new(c.Pos, c.Pos, DefaultFor(c, plan.Target[j])));
            return;
        }
        var bySource = new int[plan.Target.Length];
        Array.Fill(bySource, -1);
        for (int i = 0; i < plan.Source.Length; i++)
            if (plan.TargetIndex[i] >= 0) bySource[plan.TargetIndex[i]] = i;
        var composed = new MemoryStream();
        for (int j = 0; j < plan.Target.Length; j++)
        {
            if (bySource[j] < 0) { composed.Write(DefaultFor(c, plan.Target[j])); continue; }
            var (start, end, from, to) = spans[bySource[j]];
            composed.Write(Apply(c.D, start, end, c.Edits.GetRange(from, to - from)));
        }
        c.Edits.RemoveRange(regionEdits, c.Edits.Count - regionEdits);
        c.Edits.Add(new(regionStart, c.Pos, composed.ToArray()));
    }


    /// <summary>Reads one field of an inline structure as the target field: the other fields of the
    /// source structure are read only to keep the grammar and cut (the 2027 reference id key holds
    /// the 2023 tagged element as its first field, the rest has no counterpart).</summary>
    /// <summary>Reads a source class through one of its sub-objects as the target class (the 2027
    /// schema usage entry is the 2023 stored schema plus the data around it).</summary>
    private void ReadProjectedClass(Context c, int sourceClass, string subField, int depth)
    {
        foreach (var (declaring, field) in Chain(_source, sourceClass))
        {
            if (field.Name == subField)
            {
                ReadField(c, declaring.Name, field, depth);
                continue;
            }
            int start = c.Pos;
            c.Cutting = true;
            ReadField(c, declaring.Name, field, depth);
            c.Cutting = false;
            c.Edits.RemoveAll(e => e.Start >= start);
            c.Edits.Add(new(start, c.Pos, []));
        }
    }

    private void ReadProjectedField(Context c, SchemaField field, string subField, int depth)
    {
        if (field.Ref is not int inlineClass) throw new Fail("projected field without a class");
        foreach (var (declaring, nested) in Chain(_source, inlineClass))
        {
            if (nested.Name == subField)
            {
                ReadField(c, declaring.Name, nested, depth);
                continue;
            }
            int start = c.Pos;
            c.Cutting = true;
            ReadField(c, declaring.Name, nested, depth);
            c.Cutting = false;
            c.Edits.RemoveAll(e => e.Start >= start);
            c.Edits.Add(new(start, c.Pos, []));
        }
    }

    private static int Take(Context c, int n)
    {
        if (n < 0 || c.Pos + (long)n > c.End) throw new Fail("end of payload");
        int p = c.Pos;
        c.Pos += n;
        return p;
    }

    private static uint U32(Context c) => BitConverter.ToUInt32(c.D, Take(c, 4));

    private void ReadField(Context c, string? declaring, SchemaField f, int depth)
    {
        c.Field = (declaring ?? "?") + "." + f.Name;
        int hi = f.Mods >> 4;
        if (hi == 6) { ReadScalar(c, declaring, f, depth); return; }
        if (hi == 1)
        {
            for (int i = 0; i < (f.Fixed ?? 0); i++) ReadScalar(c, declaring, f, depth);
            return;
        }
        if (hi == 5)
        {
            int countAt = Take(c, 4);
            uint n = BitConverter.ToUInt32(c.D, countAt);
            if (n == 0xFFFFFFFF) n = 0;
            if (n > 5_000_000) throw new Fail("count");
            if (f.Type is 1 or 2 && (f.Mods & 15) == 0) { Take(c, (int)n); return; }
            int before = c.Dropped;
            bool outer = c.InArray, outerDeep = c.DeepInArray;
            c.InArray = true;
            c.DeepInArray = true;
            for (uint i = 0; i < n; i++) ReadScalar(c, declaring, f, depth);
            c.InArray = outer;
            c.DeepInArray = outerDeep;
            if (c.Dropped > before) c.Edits.Add(new(countAt, countAt + 4, BitConverter.GetBytes(n - (uint)(c.Dropped - before))));
            return;
        }
        ReadScalar(c, declaring, f, depth);
    }

    private void ReadScalar(Context c, string? declaring, SchemaField f, int depth)
    {
        switch (f.Type)
        {
            case 1: case 2: Take(c, 1); return;
            case 3: Take(c, 2); return;
            case 4: case 5: case 6: Take(c, 4); return;
            case 7: Take(c, 8); return;
            case 11:
            {
                int p = Take(c, 8);
                if (c.Narrow && !c.Cutting)
                {
                    long value = BitConverter.ToInt64(c.D, p);
                    if (value < int.MinValue || value > uint.MaxValue) throw new Fail("value beyond 32 bits");
                    c.Edits.Add(new(p, p + 8, BitConverter.GetBytes(unchecked((int)value))));
                }
                return;
            }
            case 10:
            {
                // ARuntimeClassRef: a u16 class tag
                int p = Take(c, 2);
                if (!c.Cutting)
                {
                    int tag = BitConverter.ToUInt16(c.D, p);
                    if (tag != 0) c.Edits.Add(new(p, p + 2, Tag(_tagMap.GetValueOrDefault(tag, UnknownTag))));
                }
                return;
            }
            case 8:
            {
                uint len = U32(c);
                if (2L * len > c.End - c.Pos) throw new Fail("text length");
                int p = Take(c, 2 * (int)len);
                if (declaring == "ForgeTypeId" && f.Name == "m_typeId")
                    c.LastForgeText = System.Text.Encoding.Unicode.GetString(c.D, p, 2 * (int)len);
                return;
            }
            case 9: Take(c, 16); return;
            case 13:
            {
                if (f.Args is not { Count: 1 } args || args[0].Fixed is not int count) throw new Fail("template field");
                var item = args[0] with { Mods = (byte)(args[0].Mods & 15) };
                for (int i = 0; i < count; i++) ReadScalar(c, declaring, item, depth);
                return;
            }
            case 14: ReadReference(c, declaring, f, depth); return;
            default: throw new Fail("type " + f.Type);
        }
    }

    private void ReadReference(Context c, string? declaring, SchemaField f, int depth)
    {
        int lo = f.Mods & 15;
        if (lo == 3) { Take(c, 4); return; }
        if (lo is 1 or 2 or 4)
        {
            if (declaring == "ESEntity" && f.Name == "m_blob")
            {
                ReadEntityBlob(c);
                return;
            }
            int handleAt = Take(c, 4);
            if (BitConverter.ToUInt32(c.D, handleAt) == 0) return;
            Pointer(c, handleAt);
            return;
        }
        if (f.Ref is int reference)
        {
            if (reference == _sourceIdentifier)
            {
                int p = Take(c, _sourceWidth);
                if (_sourceWidth == 8 && _targetIdentifierWidth == 4 && !c.Cutting)
                {
                    long id = BitConverter.ToInt64(c.D, p);
                    if (!c.DeepInArray && id is > 0 and <= uint.MaxValue && _presentElements.Count > 0
                        && id <= _maxElementId && !_presentElements.Contains((uint)id))
                        id = -1;
                    if (id < int.MinValue || id > uint.MaxValue) throw new Fail("id beyond 32 bits: " + id);
                    c.Edits.Add(new(p, p + 8, BitConverter.GetBytes(unchecked((int)id))));
                }
                return;
            }
            if (depth > 40) throw new Fail("depth");
            if (c.Cutting) { foreach (var (d, field) in Chain(_source, reference)) ReadField(c, d.Name, field, depth + 1); }
            else
            {
                bool outer = c.InArray;
                c.InArray = false;
                ReadInline(c, reference, depth);
                c.InArray = outer;
            }
            return;
        }
        {
            int handleAt = Take(c, 4);
            if (BitConverter.ToUInt32(c.D, handleAt) == 0xFFFFFFFF) Pointer(c, handleAt);
        }
    }

    private void ReadInline(Context c, int sourceClass, int depth) => ReadFields(c, sourceClass, depth + 1);

    /// <summary>
    /// <c>ESEntity.m_blob</c> is an owned pointer whose class tag is not written: a null handle
    /// is no entity and nothing follows, behind <c>0xFFFFFFFF</c> stands the 16-byte GUID of the
    /// entity's schema and its values. The values are read by the fields of that schema (with the
    /// target widths for the classes they name) and queued like an owned object, so the value of
    /// the entity survives the downgrade; the handle and the GUID are kept as they are.
    /// </summary>
    private void ReadEntityBlob(Context c)
    {
        uint handle = U32(c);
        if (handle == 0) return;
        if (handle != uint.MaxValue || _entityFields is null || _entityIndexOf is null) throw new Fail("entity handle");
        var guid = new Guid(c.D.AsSpan(Take(c, 16), 16));
        if (!_entityIndexOf.TryGetValue(guid, out int layout) || layout < 0 || layout >= _entityFields.Length || _entityFields[layout] is null)
            throw new Fail("entity schema not stored");
        if (c.Queue.Count >= 1_000_000) throw new Fail("queue");
        c.Queue.Add((-1 - layout, false));
    }

    /// <summary>A pointer whose handle is not null: the u16 class tag follows, the object is queued.</summary>
    private void Pointer(Context c, int handleAt)
    {
        int tagAt = Take(c, 2);
        int tag = BitConverter.ToUInt16(c.D, tagAt);
        if (tag == 0) return;
        if (!_source.ContainsKey(tag)) throw new Fail("class tag " + tag);
        if (c.Queue.Count >= 1_000_000) throw new Fail("queue");
        if (c.Cutting) { c.Queue.Add((tag, true)); return; }
        if (_tagMap.TryGetValue(tag, out int target))
        {
            c.Queue.Add((tag, false));
            c.Edits.Add(new(tagAt, tagAt + 2, Tag(target)));
            return;
        }
        c.Queue.Add((tag, true));
        c.Dropped++;
        // the element leaves its array, a single pointer becomes null
        c.Edits.Add(c.InArray ? new(handleAt, tagAt + 2, []) : new(handleAt, tagAt + 2, new byte[4]));
    }

    /// <summary>The shipped 2022 table keeps the legacy type reference of these fields (the class itself, or none); the class the 2022 stream
    /// names is the one given here (from the Formats/Latest stream), which is what an empty value of the field is made of.</summary>
    private static readonly Dictionary<(string, string), string> ExactReferences = new()
    {
        [("GroupAndParamsPair", "m_groupId")] = "ElementId",
        [("PlanViewRange2", "m_levelIds")] = "ElementId",
        [("IndependentTag", "m_taggedEntitiesCell")] = "TaggedEntitiesCell",
    };

    /// <summary>A target field the source release does not have takes this value instead of zero (the 2023 save writes -1).</summary>
    private static readonly Dictionary<(string Class, string Field), byte[]> DefaultValues = new()
    {
        [("GeomTable", "m_maxSafeTag")] = BitConverter.GetBytes(-1),
        [("GeomTable", "m_lastCheckedKingsUserModificationDate")] = BitConverter.GetBytes(-1),
        [("MEPAnalyticalModelCell", "m_baseElementId")] = BitConverter.GetBytes(-1),
    };

    /// <summary>A field the target release renamed, and whether the 64-bit source scalar takes the place of a 32-bit target one.</summary>
    private readonly record struct RenamedField(string Name, bool Narrow, string? SubField = null);

    /// <summary>Fields renamed between releases; a name cannot be matched by the plain (declaring class, field name) rule.</summary>
    private static readonly Dictionary<(string Declaring, string Field), RenamedField> RenamedFields = new()
    {
        [("TreeItemData", "m_identifier")] = new("m_id", Narrow: true),
        [("TreeItemData", "m_parentOrOtherDataId")] = new("m_parentIdOrOtherData", Narrow: true),
        [("ESSchemaStorage", "m_schemaUsageMap")] = new("m_storedSchemas", Narrow: false),
    };

    /// <summary>The tag cells of 2027 became the tagged-entities cell of 2023 (only in the tag rewriter).</summary>
    private static readonly Dictionary<(string Declaring, string Field), RenamedField> TagRenamedFields = new()
    {
        [("ReferenceIdsCellBase", "m_references")] = new("m_taggedEntities", Narrow: false),
        [("ReferenceIdBase", "m_referenceIdKey")] = new("m_taggedId", Narrow: false, SubField: "m_hostOrLinkElemId"),
    };

    private static readonly Dictionary<string, string> TagRenamedClasses = new(StringComparer.Ordinal)
    {
        ["TagReferencesWithLeaderCell"] = "TaggedEntitiesCell",
        ["TagReferenceWithLeader"] = "TaggedElem",
    };

    /// <summary>Classes the target release renamed under the same shape (the ES schema pair).</summary>
    private static readonly Dictionary<string, string> RenamedClasses = new(StringComparer.Ordinal)
    {
        ["std::pair< GUIDvalue, SchemaUsageInfo >"] = "std::pair< GUIDvalue, ESSchema >",
    };

    /// <summary>A source class the target release has under another name and shape, read from one of its sub-objects.</summary>
    private sealed record ClassProjection(string TargetClass, string SubField);

    /// <summary>The 2027 schema usage entry carries the 2023 stored schema as its m_schema sub-object.</summary>
    private static readonly Dictionary<string, ClassProjection> ProjectedClasses = new(StringComparer.Ordinal)
    {
        ["SchemaUsageInfo"] = new("ESSchema", "m_schema"),
    };

    private bool TryRenamed(string declaring, string field, out RenamedField rename) =>
        RenamedFields.TryGetValue((declaring, field), out rename)
        || (_tagMapping && TagRenamedFields.TryGetValue((declaring, field), out rename));

    /// <summary>The built-in parameter group element ids of the group type ids a 2027 file stores.</summary>
    private static readonly Dictionary<string, int> ParameterGroupElementIds = new(StringComparer.Ordinal)
    {
        [""] = -1,
        ["autodesk.parameter.group:adskModelProperties-1.0.0"] = -5000165,
        ["autodesk.parameter.group:analysisResults-1.0.0"] = -5000161,
        ["autodesk.parameter.group:constraints-1.0.0"] = -5000119,
        ["autodesk.parameter.group:construction-1.0.0"] = -5000103,
        ["autodesk.parameter.group:data-1.0.0"] = -5000175,
        ["autodesk.parameter.group:dimensions-1.0.0"] = -5000101,
        ["autodesk.parameter.group:electricalEngineering-1.0.0"] = -5000110,
        ["autodesk.parameter.group:electricalLoads-1.0.0"] = -5000125,
        ["autodesk.parameter.group:general-1.0.0"] = -5000173,
        ["autodesk.parameter.group:graphics-1.0.0"] = -5000104,
        ["autodesk.parameter.group:identityData-1.0.0"] = -5000100,
        ["autodesk.parameter.group:ifc-1.0.0"] = -5000131,
        ["autodesk.parameter.group:materials-1.0.0"] = -5000105,
        ["autodesk.parameter.group:mechanical-1.0.0"] = -5000113,
        ["autodesk.parameter.group:overallLegend-1.0.0"] = -5000187,
        ["autodesk.parameter.group:primaryEnd-1.0.0"] = -5000213,
        ["autodesk.parameter.group:rebarSystemLayers-1.0.0"] = -5000143,
        ["autodesk.parameter.group:secondaryEnd-1.0.0"] = -5000214,
        ["autodesk.parameter.group:segmentsFittings-1.0.0"] = -5000205,
        ["autodesk.parameter.group:structural-1.0.0"] = -5000112,
        ["autodesk.parameter.group:structuralAnalysis-1.0.0"] = -5000128,
        ["autodesk.parameter.group:text-1.0.0"] = -5000123,
        ["autodesk.parameter.group:title-1.0.0"] = -5000149,
        ["autodesk.parameter.group:visibility-1.0.0"] = -5000186,
        ["autodesk.revit.group:structuralSectionGeometry-1.0.0"] = -5000225,
    };

    /// <summary>A default that depends on the object being read: the 2027 ParamDef names its
    /// parameter group by a group type id, the 2023 field wants the group element id.</summary>
    private byte[] DefaultFor(Context c, (SchemaClass Declaring, SchemaField Field) target)
    {
        if (target.Declaring.Name == "ParamDef" && target.Field.Name == "m_groupElemId" && c.GroupElementId is int group)
            return BitConverter.GetBytes(group);
        return Default(target);
    }

    /// <summary>The empty encoding of a 2022 field: zero counts, null pointers, zero scalars.</summary>
    private byte[] Default((SchemaClass Declaring, SchemaField Field) target, int depth = 0)
    {
        if (DefaultValues.TryGetValue((target.Declaring.Name, target.Field.Name), out var value)) return value;
        var f = target.Field;
        int hi = f.Mods >> 4;
        if (hi is 5 or 6) return new byte[4];
        int one = DefaultScalarLength(target.Declaring, f, depth);
        return new byte[hi == 1 ? one * (f.Fixed ?? 0) : one];
    }

    private int DefaultScalarLength(SchemaClass declaring, SchemaField f, int depth)
    {
        if (depth > 12) throw new Fail("recursive default of " + f.Name);
        switch (f.Type)
        {
            case 1: case 2: return 1;
            case 3: return 2;
            case 4: case 5: case 6: case 8: return 4;
            case 7: case 11: return 8;
            case 10: return 2;
            case 9: return 16;
            case 13:
            {
                if (f.Args is not { Count: 1 } args || args[0].Fixed is not int count) throw new Fail("template field");
                return count * DefaultScalarLength(declaring, args[0] with { Mods = (byte)(args[0].Mods & 15) }, depth + 1);
            }
            case 14:
            {
                int lo = f.Mods & 15;
                if (lo != 0 || f.Ref is not int reference) return 4;
                if (ExactReferences.TryGetValue((declaring.Name, f.Name), out string? exact) && _targetByName.TryGetValue(exact, out int exactId))
                    reference = exactId;
                if (_target.TryGetValue(reference, out var c) && c.Name == "ElementId") return 4;
                int length = 0;
                foreach (var entry in Chain(_target, reference)) length += Default(entry, depth + 1).Length;
                return length;
            }
            default: throw new Fail("type " + f.Type);
        }
    }
}
