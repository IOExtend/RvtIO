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
/// to the payload bytes. A block that cannot be rewritten (unknown class at the root, an extensible-storage blob, a field of a
/// class the 2022 table lacks) keeps its bytes with the root class tag 0xFFFE, which no reader recognises.
/// </summary>
internal sealed class R22Transcoder
{
    internal const ushort UnknownTag = 0xFFFE;

    private sealed class Fail(string message) : Exception(message);

    private readonly record struct Edit(int Start, int End, byte[] Replacement);

    private readonly Dictionary<int, SchemaClass> _source;
    private readonly Dictionary<int, SchemaClass> _target;
    private readonly Dictionary<string, int> _targetByName;
    private readonly Dictionary<int, int> _tagMap = new();
    private readonly int _sourceWidth;
    private readonly int _sourceIdentifier;
    private readonly Dictionary<int, Plan> _plans = new();

    /// <summary>Source fields in serialization order, the target class of the same name and the target field positions by (declaring class, field).</summary>
    private sealed record Plan((SchemaClass Declaring, SchemaField Field)[] Source, (SchemaClass Declaring, SchemaField Field)[] Target,
        int[] TargetIndex, bool[] TargetPresent, bool Reordered, bool[] Narrow);

    internal R22Transcoder(SchemaDocument source, SchemaDocument target, int sourceIdWidth)
    {
        _source = source.Classes.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        _target = target.Classes.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        _targetByName = new(StringComparer.Ordinal);
        foreach (var c in target.Classes.OrderBy(c => c.Id)) _targetByName.TryAdd(c.Name, c.Id);
        foreach (var c in source.Classes)
            if (_targetByName.TryGetValue(c.Name, out int id)) _tagMap[c.Id] = id;
        _sourceWidth = sourceIdWidth;
        _sourceIdentifier = source.Classes.FirstOrDefault(c => c.Name == "ElementId")?.Id ?? -1;
    }

    /// <summary>Class number of the 2022 table for a class of the file, 0 when it has none.</summary>
    internal int TargetId(int sourceClass) => _tagMap.GetValueOrDefault(sourceClass);


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
            if (p == recordsAt || !IsSeparator(p) || p + w + 8 > n) break;
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
        else stats.Failures["record " + failure] = stats.Failures.GetValueOrDefault("record " + failure) + 1;
        payload ??= Unknown(b, p + w + 4, size);
        WriteId(b, p, output);
        WriteFramed(output, payload, adler: false);
    }

    private void WriteBlock(byte[] b, int p, MemoryStream output, Stats stats, bool root)
    {
        int w = _sourceWidth;
        int size = (int)BitConverter.ToUInt32(b, p + w + 4);
        var payload = Payload(b, p + w + 8, size, out var failure);
        if (payload == null) stats.Failures[failure!] = stats.Failures.GetValueOrDefault(failure!) + 1;
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
        public bool Cutting;
        public int Dropped;
        public bool InArray;
        /// <summary>The 64-bit scalars being read take the place of 32-bit 2022 fields.</summary>
        public bool Narrow;
    }

    /// <summary>The payload (class tag first) in the 2022 layout, or null when it cannot be rewritten.</summary>
    internal byte[]? Payload(byte[] data, int offset, int size) => Payload(data, offset, size, out _);

    /// <param name="failure">Why the payload cannot be rewritten (the root class and the first rule that failed), null when it can.</param>
    internal byte[]? Payload(byte[] data, int offset, int size, out string? failure)
    {
        failure = null;
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
                var (tag, dropped) = c.Queue[i];
                if (dropped || !_tagMap.ContainsKey(tag))
                {
                    // an object of a class the 2022 table does not know: read it with the source grammar and cut it out
                    int start = c.Pos, mark = c.Queue.Count;
                    c.Cutting = true;
                    ReadObject(c, tag);
                    c.Cutting = false;
                    c.Edits.RemoveAll(e => e.Start >= start);
                    c.Edits.Add(new(start, c.Pos, []));
                    for (int j = mark; j < c.Queue.Count; j++) c.Queue[j] = (c.Queue[j].Tag, true);
                    continue;
                }
                ReadObject(c, tag);
            }
            if (c.Pos != c.End) { failure = _source[rootClass].Name + ": bytes after the end of the object"; return null; }
        }
        catch (Fail fail)
        {
            failure = _source[rootClass].Name + ": " + fail.Message;
            return null;
        }
        try { return Apply(data, offset, offset + size, c.Edits); }
        catch (Fail fail)
        {
            failure = _source[rootClass].Name + ": " + fail.Message;
            return null;
        }
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
        if (!_tagMap.TryGetValue(sourceClass, out int targetClass)) throw new Fail("class without a 2022 counterpart");
        var source = Chain(_source, sourceClass);
        var target = Chain(_target, targetClass);
        var index = new Dictionary<(string, string), int>();
        for (int i = 0; i < target.Length; i++) index[(target[i].Declaring.Name, target[i].Field.Name)] = i;
        var sourceKeys = source.Select(s => (s.Declaring.Name, s.Field.Name)).ToHashSet();
        var targetIndex = source.Select(s => index.GetValueOrDefault((s.Declaring.Name, s.Field.Name), -1)).ToArray();
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
        var present = target.Select(t => sourceKeys.Contains((t.Declaring.Name, t.Field.Name))).ToArray();
        for (int i = 0; i < source.Length; i++)
            if (narrow[i]) present[targetIndex[i]] = true;
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
        if (c.Cutting)
        {
            foreach (var (_, field) in Chain(_source, sourceClass)) ReadField(c, field, 0);
            return;
        }
        ReadFields(c, sourceClass, 0);
    }

    /// <summary>The fields of an object or inline structure compared with the 2022 class: cut, inserted, or (when the common fields are stored
    /// in another order) rewritten in the order of the 2022 class.</summary>
    private void ReadFields(Context c, int sourceClass, int depth)
    {
        var plan = PlanOf(sourceClass);
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
                ReadField(c, plan.Source[i].Field, depth);
                c.Cutting = false;
                c.Edits.RemoveRange(editFrom, c.Edits.Count - editFrom);
                c.Edits.Add(new(start, c.Pos, []));
                spans[i] = (-1, -1, 0, 0);
                continue;
            }
            if (!plan.Reordered)
            {
                for (int j = last + 1; j < k; j++)
                    if (!plan.TargetPresent[j]) c.Edits.Add(new(start, start, Default(plan.Target[j])));
                last = Math.Max(last, k);
                editFrom = c.Edits.Count;
            }
            c.Narrow = plan.Narrow[i];
            ReadField(c, plan.Source[i].Field, depth);
            c.Narrow = false;
            spans[i] = (start, c.Pos, editFrom, c.Edits.Count);
            if (c.Queue.Count != queued)
            {
                // the objects of the pointers follow in the order of the fields: they cannot change places
                if (k < lastQueuing) throw new Fail("pointer fields change their order");
                lastQueuing = k;
            }
        }
        if (!plan.Reordered)
        {
            for (int j = last + 1; j < plan.Target.Length; j++)
                if (!plan.TargetPresent[j]) c.Edits.Add(new(c.Pos, c.Pos, Default(plan.Target[j])));
            return;
        }
        var bySource = new int[plan.Target.Length];
        Array.Fill(bySource, -1);
        for (int i = 0; i < plan.Source.Length; i++)
            if (plan.TargetIndex[i] >= 0) bySource[plan.TargetIndex[i]] = i;
        var composed = new MemoryStream();
        for (int j = 0; j < plan.Target.Length; j++)
        {
            if (bySource[j] < 0) { composed.Write(Default(plan.Target[j])); continue; }
            var (start, end, from, to) = spans[bySource[j]];
            composed.Write(Apply(c.D, start, end, c.Edits.GetRange(from, to - from)));
        }
        c.Edits.RemoveRange(regionEdits, c.Edits.Count - regionEdits);
        c.Edits.Add(new(regionStart, c.Pos, composed.ToArray()));
    }


    private static int Take(Context c, int n)
    {
        if (n < 0 || c.Pos + (long)n > c.End) throw new Fail("end of payload");
        int p = c.Pos;
        c.Pos += n;
        return p;
    }

    private static uint U32(Context c) => BitConverter.ToUInt32(c.D, Take(c, 4));

    private void ReadField(Context c, SchemaField f, int depth)
    {
        int hi = f.Mods >> 4;
        if (hi == 6) { ReadScalar(c, f, depth); return; }
        if (hi == 1)
        {
            for (int i = 0; i < (f.Fixed ?? 0); i++) ReadScalar(c, f, depth);
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
            bool outer = c.InArray;
            c.InArray = true;
            for (uint i = 0; i < n; i++) ReadScalar(c, f, depth);
            c.InArray = outer;
            if (c.Dropped > before) c.Edits.Add(new(countAt, countAt + 4, BitConverter.GetBytes(n - (uint)(c.Dropped - before))));
            return;
        }
        ReadScalar(c, f, depth);
    }

    private void ReadScalar(Context c, SchemaField f, int depth)
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
                    if (value < int.MinValue || value > int.MaxValue) throw new Fail("value beyond 32 bits");
                    c.Edits.Add(new(p, p + 8, BitConverter.GetBytes((int)value)));
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
                if (len > 100_000) throw new Fail("text length");
                Take(c, 2 * (int)len);
                return;
            }
            case 9: Take(c, 16); return;
            case 13:
            {
                if (f.Args is not { Count: 1 } args || args[0].Fixed is not int count) throw new Fail("template field");
                var item = args[0] with { Mods = (byte)(args[0].Mods & 15) };
                for (int i = 0; i < count; i++) ReadScalar(c, item, depth);
                return;
            }
            case 14: ReadReference(c, f, depth); return;
            default: throw new Fail("type " + f.Type);
        }
    }

    private void ReadReference(Context c, SchemaField f, int depth)
    {
        int lo = f.Mods & 15;
        if (lo == 3) { Take(c, 4); return; }
        if (lo is 1 or 2 or 4)
        {
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
                if (_sourceWidth == 8 && !c.Cutting)
                {
                    long id = BitConverter.ToInt64(c.D, p);
                    if (id < int.MinValue || id > int.MaxValue) throw new Fail("id beyond 32 bits");
                    c.Edits.Add(new(p, p + 8, BitConverter.GetBytes((int)id)));
                }
                return;
            }
            if (depth > 40) throw new Fail("depth");
            if (c.Cutting) { foreach (var (_, field) in Chain(_source, reference)) ReadField(c, field, depth + 1); }
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

    /// <summary>The empty encoding of a 2022 field: zero counts, null pointers, zero scalars.</summary>
    private byte[] Default((SchemaClass Declaring, SchemaField Field) target, int depth = 0)
    {
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
