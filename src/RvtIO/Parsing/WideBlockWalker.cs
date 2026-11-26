using System.Collections.Concurrent;
using System.Text;

namespace RvtIO.Parsing;

/// <summary>
/// Field walk of an element block of a file whose element ids are 64 bits wide (Revit 2025 and later), driven only by
/// the class schema the file carries about itself (<see cref="SchemaDocument"/>), by class and field NAME.
///
/// A block payload is the class id (u16) followed by the fields of the class chain, base class first, in schema order:
/// the "root" fields, and then one object per pointer the root (and every object) queued, in queue order. Field grammar:
///   container (mods high nibble): 0 single, 1 fixed array of Fixed items, 5 u32 count (0xFFFFFFFF = empty) and items, 6 text
///   scalar by type: 1,2 = 1 byte; 3 = 2; 4,5,6 = 4; 7,11 = 8; 10 = 2 (class tag); 8 = u32 length and UTF-16 text; 9 = 16 (GUID);
///     13 = array of Args[0].Fixed items of Args[0].Type; 14 by the low mods nibble:
///       3 back reference: u32;  1, 2, 4 owned pointer: u32 handle, and when it is not 0 a u16 class tag, the object follows later;
///       0 with a class reference: the class's fields inline (an ElementId is Identifier.m_id64, 8 bytes); 0 without: u32 handle,
///       and when it is 0xFFFFFFFF a u16 class tag (queued)
/// The block is consumed exactly when the position after the last queued object equals the end of the payload. The walk
/// stops without a value at a class tag the schema does not declare and at data it cannot interpret; what was read until then
/// is kept. The values of an extensible storage entity are no class of the class schema: they are read by the entity schemas of
/// the file when those are given (<see cref="UseEntitySchemas"/>); without them the entity is passed and the objects that lie
/// behind its values are read out of place.
/// </summary>
internal sealed partial class WideBlockWalker
{
    internal readonly record struct Capture(string Key, CaptureKind Kind, long Integer, double Real, string? Text, bool AtRoot, int QueueIndex,
        string DeclaringClass, string Field, int ValueOffset = -1, int QueuePosition = -1);

    internal readonly record struct ArchiveReference(int Offset, int OwnerQueueIndex, uint Handle, int? QueuedObject = null);

    internal enum CaptureKind { Integer, Real, Text, PointerClass }

    internal sealed partial class Walk
    {
        public bool RootRead;
        public bool Exact;
        public string? Error;
        public int Position;
        public readonly List<int> Queue = [];
        public readonly List<Capture> Captures = [];
        internal List<ArchiveReference>? ArchiveReferences;
        public Dictionary<string, int>? RootPointers;
        /// <summary>Per queued object (same index as <see cref="Queue"/>) the first position of its fields, -1 when not reached.</summary>
        public readonly List<int> QueueStart = [];
        /// <summary>The values of the objects of the requested record classes, in stored order (inline items of arrays and queued objects).</summary>
        public readonly List<(int ClassId, int QueueIndex, Dictionary<string, object?> Values)> Records = [];
        /// <summary>Per queued object the length of the queue after its fields were read (the entries it queued are the ones before).</summary>
        public readonly List<int> QueueEnd = [];
        public readonly List<int> QueueCountAtStart = [];
        /// <summary>Queue index of the first object that held an extensible storage entity: without the entity schemas one without stored values,
        /// which the walk passes; with them any entity whose values it queues. -1 when none. The readers keep what lies behind it out of their
        /// output as before the values were read (<see cref="UseEntitySchemas"/>).</summary>
        public int EmptyEntityAt = -1;
        /// <summary>For a walk that stopped with an error: "Declaring.field" of the field that was being read; null when it stopped before any field.</summary>
        public string? StopField;
        /// <summary>For a walk that stopped with an error: the index of the queued object that was being read, -1 in the root fields.</summary>
        public int StopQueueIndex = -1;
        /// <summary>For a walk that stopped with an error: the name of the class of the object that was being read (the root class at the root).</summary>
        public string? StopClass;

        /// <summary>For a walk that stopped with an error behind an extensible storage entity (<see cref="EmptyEntityAt"/>): the walk cut to the
        /// objects that were queued when that object started. The payloads of its entities lie in the stream before the objects queued
        /// later, so what the walk read of those is misplaced and its error comes from them; the objects before that are read where they are.
        /// Null when the walk has no such entity or stopped inside the cut (its error is then a real one).</summary>
        public Walk? BeforeEntityBlobs()
        {
            if (!RootRead || Error == null || EmptyEntityAt < 0 || EmptyEntityAt >= QueueCountAtStart.Count) return null;
            int bound = QueueCountAtStart[EmptyEntityAt];
            if (QueueStart.Count - 1 < bound || QueueEnd.Count < bound) return null;
            var cut = new Walk { RootRead = true, Position = Position, EmptyEntityAt = EmptyEntityAt };
            cut.Queue.AddRange(Queue.Take(bound));
            cut.QueueStart.AddRange(QueueStart.Take(bound));
            cut.QueueEnd.AddRange(QueueEnd.Take(bound));
            cut.QueueCountAtStart.AddRange(QueueCountAtStart.Take(bound));
            cut.Captures.AddRange(Captures.Where(c => c.QueueIndex < bound));
            cut.Records.AddRange(Records.Where(r => r.QueueIndex < bound));
            return cut;
        }
    }

    /// <summary>Key of a record that names the field ("Declaring.field") of the root object it was read inline from; absent for a queued object.</summary>
    internal const string RootFieldKey = "@rootField";

    private sealed class Fail(string message) : Exception(message);

    /// <summary>The capture key prefix of a counted array whose every item is captured (as key@index).</summary>
    internal const string AllItemsKeyPrefix = "all:";

    private readonly Dictionary<int, SchemaClass> _classes;
    private readonly ConcurrentDictionary<int, StoredField[]> _chains = new();
    private readonly Func<SchemaClass, SchemaField, string?> _keyOf;
    private readonly HashSet<int> _recordClasses;
    private readonly ConcurrentDictionary<(int, string), string?> _keys = new();
    private readonly bool[] _isIdentifier;
    private readonly int _idWidth;
    private readonly bool _captureRootPointers;
    private readonly SchemaClass _entityBlob;
    private Dictionary<Guid, int>? _entityLayoutOf;
    private SchemaField[]?[] _entityLayouts = [];
    private bool _passUnknownEntities;

    /// <summary>The most objects and scalars one walk reads (the error is "step budget" beyond it), so that a misaligned walk does not
    /// loop over millions of items without bytes.</summary>
    internal long StepBudget { get; set; } = long.MaxValue;

    /// <param name="keyOf">The capture key of a scalar field (null: not captured); a fixed array adds "@index".</param>
    /// <param name="recordClassNames">Classes whose objects are returned as <see cref="Walk.Records"/>.</param>
    internal WideBlockWalker(SchemaDocument schema, Func<SchemaClass, SchemaField, string?> keyOf, IEnumerable<string> recordClassNames,
        bool captureRootPointers = false, ElementIdMap? ids = null)
    {
        _classes = schema.Classes.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
        _keyOf = keyOf;
        _captureRootPointers = captureRootPointers;
        _ids = ids;
        var names = recordClassNames.ToHashSet(StringComparer.Ordinal);
        _recordClasses = _classes.Values.Where(c => names.Contains(c.Name)).Select(c => c.Id).ToHashSet();
        int max = _classes.Keys.Max();
        _isIdentifier = new bool[max + 1];
        foreach (var c in _classes.Values) _isIdentifier[c.Id] = c.Name == "ElementId";
        // Identifier.m_id is 4 bytes up to Revit 2023 and Identifier.m_id64 8 bytes from Revit 2024
        _idWidth = _classes.Values.FirstOrDefault(c => c.Name == "Identifier") is { Fields: [{ Type: 4 or 11 } idField] }
            ? idField.Type == 4 ? 4 : 8 : 8;
        _entityBlob = _classes.Values.FirstOrDefault(c => c.Name == "ESBlob") ?? new SchemaClass(-1, "ESBlob", 0, 0, [], []);
    }

    /// <summary>The fields an entity of each extensible storage schema of the file stores, by schema GUID (<see cref="EntitySchemaReader"/>;
    /// null for a schema with a field type that is not read). With them, also with an empty set, an entity is read by its schema or
    /// stops the walk; without this call the walk passes an entity as it did before.</summary>
    /// <param name="passUnknown">An entity whose schema is not given or not read is passed as without the schemas (the readers behind the
    /// output); otherwise it stops the walk (the census).</param>
    internal void UseEntitySchemas(IReadOnlyDictionary<Guid, SchemaField[]?> schemas, bool passUnknown = false,
        IReadOnlyDictionary<Guid, string>? publicEntityNames = null)
    {
        _passUnknownEntities = passUnknown;
        _publicEntityNames = publicEntityNames;
        _entityLayouts = [.. schemas.Values];
        _entityLayoutOf = schemas.Keys.Select((guid, index) => (guid, index)).ToDictionary(pair => pair.guid, pair => pair.index);
    }

    /// <summary>The class of a queue entry; the values of an extensible storage entity (a negative entry) are an ESBlob.</summary>
    internal SchemaClass? Class(int id) => id < 0 ? _entityBlob : _classes.GetValueOrDefault(id);

    private readonly ElementIdMap? _ids;

    private sealed class StoredField(SchemaClass declaring, SchemaField field)
    {
        internal readonly SchemaClass Declaring = declaring;
        internal readonly SchemaField Field = field;
        internal readonly string QualifiedName = declaring.Name + "." + field.Name;
        private string? _key;
        private volatile bool _resolved;

        // Resolve only fields the walk reaches. Different chains can inherit the same field.
        internal string? Key(WideBlockWalker walker)
        {
            if (!_resolved)
            {
                _key = walker.KeyOf(Declaring, Field);
                _resolved = true;
            }
            return _key;
        }
    }

    private StoredField[] Chain(int classId) => _chains.GetOrAdd(classId, static (id, classes) =>
    {
        var order = new List<SchemaClass>();
        for (int guard = 0; id != 0 && guard < 64; guard++)
        {
            if (!classes.TryGetValue(id, out var c)) throw new Fail("class " + id + " has no schema");
            order.Add(c);
            id = c.Base;
        }
        order.Reverse();
        return order.SelectMany(c => c.Fields.Where(f => f.Stored).Select(f => new StoredField(c, f))).ToArray();
    }, _classes);

    /// <summary>True when the class derives from (or is) the named class.</summary>
    internal bool IsA(int classId, string baseName)
    {
        for (int guard = 0; classId != 0 && guard < 64; guard++)
        {
            if (!_classes.TryGetValue(classId, out var c)) return false;
            if (c.Name == baseName) return true;
            classId = c.Base;
        }
        return false;
    }

    /// <summary>Walks the payload <c>[offset, offset + size)</c> of a block of class <paramref name="classId"/>. With
    /// <paramref name="tailValid"/>, a queued object that stops the walk (an extensible storage blob) is skipped when the objects queued
    /// behind it can be read, inside the payload, from exactly one start position and <paramref name="tailValid"/> (walk, index of the first
    /// object read again) accepts what was read there (RebarShape: the constraints of the edges lie behind the ESEntityCell of the shape;
    /// their values tell a real start from a misaligned one).</summary>
    internal Walk Run(byte[] data, int offset, int size, int classId, Func<Walk, int, bool>? tailValid = null, bool rootOnly = false,
        bool traceArchiveReferences = false)
    {
        var walk = new Walk { ArchiveReferences = traceArchiveReferences ? [] : null };
        var ctx = new Context(data, offset + 2, offset + size, walk);
        int failedAt = -1;
        try
        {
            foreach (var field in Chain(classId))
            {
                ctx.RootField = field.QualifiedName;
                ReadField(ctx, field.Declaring, field.Field, field.Key(this), 0, null);
            }
            walk.RootRead = true;
            if (!rootOnly)
            {
                ctx.AtRoot = false;
                RunQueue(ctx, 0, ref failedAt);
                walk.Exact = ctx.Pos == ctx.End;
            }
        }
        catch (Fail failure)
        {
            walk.Error = failure.Message;
            walk.StopField = ctx.Field == null ? null : ctx.Declaring!.Name + "." + ctx.Field.Name;
            walk.StopQueueIndex = ctx.QueueIndex;
            walk.StopClass = Class(ctx.QueueIndex < 0 ? classId : walk.Queue[ctx.QueueIndex])?.Name;
            if (tailValid != null && walk.RootRead && failedAt >= 0 && Resume(ctx, failedAt, tailValid))
            {
                walk.Error = walk.StopField = walk.StopClass = null;
                walk.StopQueueIndex = -1;
            }
        }
        walk.Position = ctx.Pos;
        return walk;
    }

    private void RunQueue(Context ctx, int from, ref int failedAt)
    {
        var walk = ctx.Walk;
        for (int qi = from; qi < walk.Queue.Count; qi++)
        {
            failedAt = qi;
            ctx.QueueIndex = qi;
            ctx.Declaring = null;
            ctx.Field = null;
            walk.QueueStart.Add(ctx.Pos);
            walk.QueueCountAtStart.Add(walk.Queue.Count);
            ReadObject(ctx, walk.Queue[qi], 1);
            walk.QueueEnd.Add(walk.Queue.Count);
        }
        failedAt = -1;
    }

    /// <summary>After a queued object stopped the walk: the position behind it at which the objects queued after
    /// it are read to the end of the payload, when there is exactly one.</summary>
    private const int ResumeScanWindow = 1 << 16;

    private bool Resume(Context ctx, int failed, Func<Walk, int, bool> tailValid)
    {
        var walk = ctx.Walk;
        int first = failed + 1;
        if (first >= walk.Queue.Count) return false;
        int failPos = ctx.Pos;
        int queue = walk.Queue.Count, captures = walk.Captures.Count, records = walk.Records.Count, starts = walk.QueueStart.Count,
            ends = walk.QueueEnd.Count, counts = walk.QueueCountAtStart.Count;
        void Rollback()
        {
            walk.Queue.RemoveRange(queue, walk.Queue.Count - queue);
            walk.Captures.RemoveRange(captures, walk.Captures.Count - captures);
            walk.Records.RemoveRange(records, walk.Records.Count - records);
            walk.QueueStart.RemoveRange(starts, walk.QueueStart.Count - starts);
            walk.QueueEnd.RemoveRange(ends, walk.QueueEnd.Count - ends);
            walk.QueueCountAtStart.RemoveRange(counts, walk.QueueCountAtStart.Count - counts);
        }
        bool TryAt(int position)
        {
            ctx.Pos = position;
            int unused = -1;
            try
            {
                // the failed object keeps its entries so that the queue indexes of the objects behind it stay aligned
                walk.QueueEnd.Add(queue);
                RunQueue(ctx, first, ref unused);
                return ctx.Pos <= ctx.End && tailValid(walk, first);
            }
            catch (Fail)
            {
                return false;
            }
        }
        var found = new List<int>();
        // the objects behind a blob start within one payload of a shape: a bounded window, and the scan stops at the second match (the result is ambiguous then)
        for (int position = failPos; position < ctx.End && position - failPos < ResumeScanWindow && found.Count < 2; position++)
        {
            bool ok = TryAt(position);
            Rollback();
            if (ok) found.Add(position);
        }
        if (found.Count != 1)
        {
            ctx.Pos = failPos;
            return false;
        }
        TryAt(found[0]);
        // what lies behind the last object is not read
        return true;
    }

    private sealed class Context(byte[] data, int pos, int end, Walk walk)
    {
        public readonly byte[] D = data;
        public int Pos = pos;
        public readonly int End = end;
        public readonly Walk Walk = walk;
        public bool AtRoot = true;
        public int QueueIndex = -1;
        public string? RootField;
        public SchemaClass? Declaring;
        public SchemaField? Field;
        public long Steps;
    }

    private static int Take(Context c, int n)
    {
        if (n < 0 || c.Pos + (long)n > c.End) throw new Fail("end of payload");
        int p = c.Pos;
        c.Pos += n;
        return p;
    }

    private static uint U32(Context c) => BitConverter.ToUInt32(c.D, Take(c, 4));

    private void ReadObject(Context c, int classId, int depth, EntityFields? entityFields = null)
    {
        if (depth > 40) throw new Fail("depth");
        if (++c.Steps > StepBudget) throw new Fail("step budget");
        if (classId < 0) { ReadEntityPayload(c, classId, depth); return; }
        bool entityCell = _publicEntityNames is { Count: > 0 } && depth == 1 && _classes[classId].Name == "ESEntityCell";
        Dictionary<string, object?>? record = entityCell ? new EntityFields() : entityFields;
        if (record == null && _recordClasses.Contains(classId)) record = new();
        if (record != null && c.AtRoot) record[RootFieldKey] = c.RootField;
        foreach (var field in Chain(classId)) ReadField(c, field.Declaring, field.Field, field.Key(this), depth, record);
        if (entityCell)
            (c.Walk.EntityCells ??= [])[c.QueueIndex] = record!.GetValueOrDefault("m_entityMap");
        else if (record != null && entityFields == null)
            c.Walk.Records.Add((classId, c.QueueIndex, record));
    }

    // The values of an extensible storage entity are the fields of its schema, which the class schema does not hold: each one stored
    // like a class field of the same type, in the order of ESField.m_entryIndex.
    private void ReadEntityPayload(Context c, int queued, int depth)
    {
        EntityFields? values = c.Walk.EntityPayloads?.GetValueOrDefault(c.QueueIndex);
        foreach (var field in _entityLayouts[-1 - queued]!)
        {
            ReadField(c, _entityBlob, field, KeyOf(_entityBlob, field), depth, values);
            values?.TryAdd(field.Name, UnavailableEntityValue);
        }
    }

    // ESEntity.m_blob is an owned pointer whose class tag is not written: a handle of 0 is no entity and nothing follows; behind
    // 0xFFFFFFFF the 16-byte GUID of the entity's schema stands where the class tag would, and the values are queued like any owned object.
    // False when the entity is to be passed as without the schemas: nothing is read then.
    private bool QueueEntityPayload(Context c, uint handle, EntityFields? values)
    {
        if (values != null) values["m_blob"] = handle == 0 ? null : UnavailableEntityValue;
        if (handle == 0) return true;
        if (_passUnknownEntities && !(handle == 0xFFFFFFFF && c.Pos + 16 <= c.End
            && _entityLayoutOf!.TryGetValue(new Guid(c.D.AsSpan(c.Pos, 16)), out int known) && _entityLayouts[known] != null)) return false;
        if (handle != 0xFFFFFFFF) throw new Fail("entity handle");
        var guid = new Guid(c.D.AsSpan(Take(c, 16), 16));
        if (!_entityLayoutOf!.TryGetValue(guid, out int layout)) throw new Fail("entity schema not stored");
        if (_entityLayouts[layout] == null) throw new Fail("entity field type");
        if (c.Walk.Queue.Count >= 1_000_000) throw new Fail("queue");
        if (values != null && _publicEntityNames?.TryGetValue(guid, out string? name) == true)
        {
            var fields = new EntityFields();
            (c.Walk.EntityPayloads ??= [])[c.Walk.Queue.Count] = fields;
            values["m_blob"] = new Model.ExtensibleStorageEntity(guid, name, fields);
        }
        c.Walk.Queue.Add(-1 - layout);
        if (c.Walk.EmptyEntityAt < 0) c.Walk.EmptyEntityAt = c.QueueIndex;
        return true;
    }

    private void ReadField(Context c, SchemaClass declaring, SchemaField f, string? key, int depth, Dictionary<string, object?>? record)
    {
        int hi = f.Mods >> 4;
        c.Declaring = declaring;
        c.Field = f;
        if (hi == 6)
        {
            Scalar(c, declaring, f, key, depth, record, null);
            return;
        }
        if (hi == 1)
        {
            int n = f.Fixed ?? 0;
            if (record is EntityFields fixedValues) { ReadEntityArray(c, declaring, f, depth, fixedValues, n); return; }
            for (int i = 0; i < n; i++) Scalar(c, declaring, f, key == null ? null : key + "@" + i, depth, record, null);
            return;
        }
        if (hi == 5)
        {
            int countAt = c.Pos;
            uint n = U32(c);
            if (n == 0xFFFFFFFF) n = 0;
            if (n > 5_000_000) throw new Fail("count " + n + " at " + declaring.Name + "." + f.Name);
            if (record is EntityFields countedValues) { ReadEntityArray(c, declaring, f, depth, countedValues, (int)n); return; }
            if (key != null) c.Walk.Captures.Add(new(key + "#", CaptureKind.Integer, n, 0, null, c.AtRoot && depth == 0, c.QueueIndex, declaring.Name, f.Name, countAt));
            // The host type of an empty array is zero (the Revit 2022 walk keeps that zero as the first entry)
            if (key != null && n == 0 && declaring.Name == "Family" && f.Name == "m_refTypeIds")
                c.Walk.Captures.Add(new(key + "@0", CaptureKind.Integer, 0, 0, null, c.AtRoot && depth == 0, c.QueueIndex, declaring.Name, f.Name));
            if (f.Type is 1 or 2 && (f.Mods & 15) == 0)
            {
                int at = Take(c, (int)n);
                // a byte array keyed with AllItemsKeyPrefix is captured whole (RebarInstBase.m_includedBars)
                if (key != null && key.StartsWith(AllItemsKeyPrefix, StringComparison.Ordinal))
                    for (int i = 0; i < n; i++)
                        c.Walk.Captures.Add(new(key + "@" + i, CaptureKind.Integer, c.D[at + i], 0, null, c.AtRoot && depth == 0, c.QueueIndex, declaring.Name, f.Name));
                return;
            }
            // the first item of a counted array is captured; an array keyed with AllItemsKeyPrefix is captured whole
            for (uint i = 0; i < n; i++)
                Scalar(c, declaring, f, key != null && (i == 0 || key.StartsWith(AllItemsKeyPrefix, StringComparison.Ordinal)) ? key + "@" + i : null, depth, record, null);
            return;
        }
        Scalar(c, declaring, f, key, depth, record, null);
    }

    private string? KeyOf(SchemaClass declaring, SchemaField f)
    {
        var key = (declaring.Id, f.Name);
        return _keys.TryGetValue(key, out var value) ? value : _keys.GetOrAdd(key, _keyOf(declaring, f));
    }

    private void Scalar(Context c, SchemaClass declaring, SchemaField f, string? key, int depth, Dictionary<string, object?>? record, object? unused)
    {
        var walk = c.Walk;
        if (++c.Steps > StepBudget) throw new Fail("step budget");
        void Note(CaptureKind kind, long i, double d, string? s, int valueOffset = -1)
        {
            if (key != null) walk.Captures.Add(new(key, kind, i, d, s, c.AtRoot && depth == 0, c.QueueIndex, declaring.Name, f.Name, valueOffset));
        }
        switch (f.Type)
        {
            case 1: case 2:
            {
                int p = Take(c, 1);
                Note(CaptureKind.Integer, c.D[p], 0, null, p);
                if (record != null)
                    record[f.Name] = record is EntityFields && f.Type == 1
                        ? c.D[p] switch { 0 => false, 1 => true, _ => UnavailableEntityValue }
                        : (long)c.D[p];
                return;
            }
            case 3:
            {
                int p = Take(c, 2);
                short v = BitConverter.ToInt16(c.D, p);
                Note(CaptureKind.Integer, v, 0, null);
                if (record != null) record[f.Name] = (long)v;
                return;
            }
            case 4:
            case 5:
            {
                int p = Take(c, 4);
                int v = BitConverter.ToInt32(c.D, p);
                Note(CaptureKind.Integer, v, 0, null, p);
                if (record != null) record[f.Name] = (long)v;
                return;
            }
            case 6:
            {
                int p = Take(c, 4);
                float v = BitConverter.ToSingle(c.D, p);
                Note(CaptureKind.Real, 0, v, null);
                if (record != null) record[f.Name] = (double)v;
                return;
            }
            case 7:
            {
                int p = Take(c, 8);
                double v = BitConverter.ToDouble(c.D, p);
                Note(CaptureKind.Real, 0, v, null, p);
                if (record != null) record[f.Name] = v;
                return;
            }
            case 10:
            {
                // a class reference is the 2-byte class tag (ElementHeader.m_classDef)
                int p = Take(c, 2);
                int v = BitConverter.ToUInt16(c.D, p);
                Note(CaptureKind.Integer, v, 0, null);
                if (record != null) record[f.Name] = (long)v;
                return;
            }
            case 11:
            {
                int p = Take(c, 8);
                long v = BitConverter.ToInt64(c.D, p);
                Note(CaptureKind.Integer, v, 0, null);
                if (record != null) record[f.Name] = record is EntityFields ? ExactEntityInteger(v) : v;
                return;
            }
            case 8:
            {
                uint len = U32(c);
                // a text is as long as its count says (no other limit); twice the count may not fit an int
                if (2L * len > c.End - c.Pos) throw new Fail("end of payload");
                int p = Take(c, 2 * (int)len);
                string s = Encoding.Unicode.GetString(c.D, p, 2 * (int)len);
                Note(CaptureKind.Text, 0, 0, s, p - sizeof(uint));
                if (record != null) record[f.Name] = s;
                return;
            }
            case 9:
                int guidAt = Take(c, 16);
                if (record is EntityFields) record[f.Name] = new Guid(c.D.AsSpan(guidAt, 16));
                return;
            case 13:
            {
                if (f.Args is not { Count: 1 } args || args[0].Fixed is not int count) throw new Fail("template field");
                var item = args[0] with { Mods = (byte)(args[0].Mods & 15) };
                // a keyed template field (Trf.m_3x3) is captured as key/index; the Revit 2022 capture list never keys one
                for (int i = 0; i < count; i++) Scalar(c, declaring, item, key == null ? null : key + "/" + i, depth, null, null);
                return;
            }
            case 14:
                ReadReference(c, declaring, f, key, depth, record);
                return;
            default:
                throw new Fail("type " + f.Type);
        }
    }

    private void ReadReference(Context c, SchemaClass declaring, SchemaField f, string? key, int depth, Dictionary<string, object?>? record)
    {
        var walk = c.Walk;
        int lo = f.Mods & 15;
        if (lo == 3)
        {
            int offset = c.Pos;
            uint back = U32(c);
            walk.ArchiveReferences?.Add(new(offset, c.QueueIndex, back));
            if (key != null) walk.Captures.Add(new(key, CaptureKind.Integer, back, 0, null, c.AtRoot && depth == 0, c.QueueIndex, declaring.Name, f.Name, offset));
            if (record != null) record[f.Name] = (long)back;
            return;
        }
        if (lo is 1 or 2 or 4)
        {
            int handleAt = c.Pos, queuePosition = walk.Queue.Count;
            uint handle = U32(c);
            int tag = 0;
            if (_entityLayoutOf != null && declaring.Name == "ESEntity" && f.Name == "m_blob" && QueueEntityPayload(c, handle, record as EntityFields)) return;
            // Without the entity schemas: an extensible storage entity whose schema GUID behind the handle repeats the pair key in
            // front of it is passed, and nothing is queued for its values.
            if (handle == 0xFFFFFFFF && declaring.Name == "ESEntity" && f.Name == "m_blob" && c.Pos >= 20 && c.Pos + 16 <= c.End
                && c.D.AsSpan(c.Pos - 20, 16).SequenceEqual(c.D.AsSpan(c.Pos, 16)))
            {
                Take(c, 16);
                if (c.Walk.EmptyEntityAt < 0) c.Walk.EmptyEntityAt = c.QueueIndex;
                if (key != null) walk.Captures.Add(new(key, CaptureKind.PointerClass, -1, 0, null, c.AtRoot && depth == 0, c.QueueIndex, declaring.Name, f.Name));
                if (record != null && record is not EntityFields) record[f.Name] = 0L;
                return;
            }
            if (handle != 0)
            {
                tag = BitConverter.ToUInt16(c.D, Take(c, 2));
                if (tag != 0)
                {
                    if (!_classes.ContainsKey(tag)) throw new Fail("class tag " + tag);
                    if (walk.Queue.Count >= 1_000_000) throw new Fail("queue");
                    if (_captureRootPointers && c.AtRoot && depth == 0 && key != null)
                        (walk.RootPointers ??= new())[key] = walk.Queue.Count;
                    if (_publicEntityNames is { Count: > 0 } && c.AtRoot && depth == 0 && declaring.Name == "Element"
                        && f.Name == "m_cellList" && _classes[tag].Name == "CellList")
                        walk.OwnCellList = walk.Queue.Count;
                    walk.Queue.Add(tag);
                }
            }
            if (key != null) walk.Captures.Add(new(key, CaptureKind.PointerClass, handle == 0 ? 0 : tag == 0 ? -1 : tag, 0, null,
                c.AtRoot && depth == 0, c.QueueIndex, declaring.Name, f.Name, handleAt, queuePosition));
            walk.ArchiveReferences?.Add(new(handleAt, c.QueueIndex, handle, tag == 0 ? null : queuePosition));
            if (record != null) record[f.Name] = record is EntityFields ? UnavailableEntityValue : (long)tag;
            return;
        }
        if (f.Ref is int reference)
        {
            if (_isIdentifier[reference])
            {
                // ElementId: Identifier.m_id (4 bytes) or Identifier.m_id64 (8 bytes)
                int p = Take(c, _idWidth);
                long id = _idWidth == 8 ? BitConverter.ToInt64(c.D, p) : BitConverter.ToInt32(c.D, p);
                if (record is EntityFields) { record[f.Name] = ExactEntityInteger(id); return; }
                if (_ids != null) id = _ids.Key(id);
                if (key != null) walk.Captures.Add(new(key, CaptureKind.Integer, id, 0, null, c.AtRoot && depth == 0, c.QueueIndex, declaring.Name, f.Name, p));
                if (record != null) record[f.Name] = id;
                return;
            }
            // an inline structure: the keys of its fields are their own
            int structAt = c.Pos;
            // a stop behind this structure belongs to the field that holds it, not to the last field of the structure
            var (outerClass, outerField) = (c.Declaring, c.Field);
            if (record is EntityFields)
            {
                var values = new EntityFields();
                ReadObject(c, reference, depth + 1, values);
                record[f.Name] = EntityInlineValue(_classes[reference].Name, values);
            }
            else ReadObject(c, reference, depth + 1);
            c.Declaring = outerClass;
            c.Field = outerField;
            // the GUID of an entity schema is kept as its value in the record of the schema
            if (record != null && declaring.Name == "ESSchema" && f.Name == "m_guid" && c.Pos - structAt == 16) record[f.Name] = new Guid(c.D.AsSpan(structAt, 16));
            // a keyed XYZ field (SpatialElementCalculationPointRegenData.m_position) is captured as its three doubles
            if (key != null && _classes[reference].Name == "XYZ" && c.Pos - structAt == 24)
                for (int i = 0; i < 3; i++)
                    walk.Captures.Add(new(key + "@" + i, CaptureKind.Real, 0, BitConverter.ToDouble(c.D, structAt + 8 * i), null, c.AtRoot && depth == 0,
                        c.QueueIndex, declaring.Name, f.Name));
            // a keyed ForgeTypeId field (ParamDef.m_groupTypeId, m_typeId) is captured as the text of its single string
            if (key != null && _classes[reference].Name == "ForgeTypeId" && walk.Records is [.., (var recordClass, _, var forge)] && recordClass == reference
                && forge.GetValueOrDefault("m_typeId") is string forgeText)
                walk.Captures.Add(new(key, CaptureKind.Text, 0, 0, forgeText, c.AtRoot && depth == 0, c.QueueIndex, declaring.Name, f.Name));
            if (key != null && c.Pos - structAt == 16
                && _classes[reference] is { Name: "ExternalParamKey", Fields: [{ Name: "m_guidValue", Type: 14, Mods: 0, Ref: int guidClass }] }
                && Class(guidClass) is { Name: "GUIDvalue", Fields: [{ Name: "m_guid", Type: 9, Mods: 0 }] })
                walk.Captures.Add(new(key, CaptureKind.Text, 0, 0, new Guid(c.D.AsSpan(structAt, 16)).ToString(), c.AtRoot && depth == 0,
                    c.QueueIndex, declaring.Name, f.Name));
            return;
        }
        {
            int handleAt = c.Pos, queuePosition = walk.Queue.Count;
            uint handle = U32(c);
            int tag = 0;
            if (handle == 0xFFFFFFFF)
            {
                tag = BitConverter.ToUInt16(c.D, Take(c, 2));
                if (!_classes.ContainsKey(tag)) throw new Fail("class tag " + tag);
                if (walk.Queue.Count >= 1_000_000) throw new Fail("queue");
                walk.Queue.Add(tag);
            }
            walk.ArchiveReferences?.Add(new(handleAt, c.QueueIndex, handle, tag == 0 ? null : queuePosition));
            if (key != null) walk.Captures.Add(new(key, CaptureKind.PointerClass, handle == 0 ? 0 : tag == 0 ? -1 : tag, 0, null, c.AtRoot && depth == 0, c.QueueIndex, declaring.Name, f.Name));
        }
    }
}
