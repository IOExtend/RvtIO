using RvtIO.Internal;
using RvtIO.Parsing;

namespace RvtIO;

public partial class RvtFileReader
{
    /// <summary>Native parameter decoding is independent across owners; replay results in stream order.</summary>
    private sealed class TfNativeBlocks(ParsingState state)
    {
        private const int Capacity = 32_768;
        private readonly List<Block> _blocks = [];
        private readonly Dictionary<uint, int> _first = new();
        private int[]? _next;
        private Update[]? _updates;

        private readonly record struct Block(byte[] Data, int Start, uint Size, ushort Class, uint Owner, bool Root);
        private readonly record struct Update(TfBlockDeserializer.NativeParams? Values, GridDatumData? Grid,
            bool Root, bool MergeDrive = false, Model.GridPlaneData? Level = null);

        internal void Add(byte[] data, int start, uint size, ushort cls, uint owner, bool root)
        {
            if (cls is not (2299 or 0x083C) && state.NativeParams.ContainsKey(owner)
                && (!root || (state.NativeParamsFromRootClass.Contains(owner)
                    && !(state.R22View && TfBlockDeserializer.IsSubclassOf(cls, 1794)))))
                return;

            var block = new Block(data, start, size, cls, owner, root);
            if (TfBlockDeserializer.CaptureAllFields || OperatingSystem.IsBrowser())
            {
                bool hasNative = state.NativeParams.ContainsKey(owner);
                bool hasRoot = state.NativeParamsFromRootClass.Contains(owner);
                Apply(owner, Decode(block, ref hasNative, ref hasRoot));
                return;
            }

            _blocks.Add(block);
            if (_blocks.Count == Capacity) Flush();
        }

        internal void Flush()
        {
            if (_blocks.Count == 0) return;
            if (_next == null || _next.Length < _blocks.Count)
            {
                _next = new int[_blocks.Count];
                _updates = new Update[_blocks.Count];
            }
            var next = _next;
            var updates = _updates!;
            for (int i = _blocks.Count - 1; i >= 0; i--)
            {
                uint owner = _blocks[i].Owner;
                next[i] = _first.GetValueOrDefault(owner, -1);
                _first[owner] = i;
            }
            Parallel.ForEach(_first, group =>
            {
                bool hasNative = state.NativeParams.ContainsKey(group.Key);
                bool hasRoot = state.NativeParamsFromRootClass.Contains(group.Key);
                for (int i = group.Value; i >= 0; i = next[i])
                    updates[i] = Decode(_blocks[i], ref hasNative, ref hasRoot);
            });
            for (int i = 0; i < _blocks.Count; i++)
                Apply(_blocks[i].Owner, updates[i]);
            Array.Clear(updates, 0, _blocks.Count);
            _blocks.Clear();
            _first.Clear();
        }

        private Update Decode(Block block, ref bool hasNative, ref bool hasRoot)
        {
            var (data, start, size, cls, _, root) = block;
            // Datum subclasses provide their exact names and geometry even when an earlier block supplied parameters.
            if (!hasNative || cls is 2299 or 0x083C || (root && !hasRoot))
            {
                var values = TfBlockDeserializer.ExtractParams(data, start, size, cls);
                bool decodedRoot = root && values is { Count: > 0 };
                hasRoot |= decodedRoot;
                Model.GridPlaneData? level = null;
                if (cls == 2299)
                {
                    values ??= new TfBlockDeserializer.NativeParams();
                    values.Fields["__isLevel"] = 1;
                    var name = TfBlockDeserializer.ExtractLevelName(data, start, size, cls);
                    if (!string.IsNullOrWhiteSpace(name)) values.StringFields["m_text"] = name;
                    level = TfBlockDeserializer.ExtractLevelPlane(data, start, size, cls);
                    if (level is { } plane && double.IsFinite(plane.Origin[2]))
                        values.DoubleFields["__levelElevation"] = plane.Origin[2];
                }
                GridDatumData? grid = null;
                if (cls == 0x083C && TfBlockDeserializer.ExtractGridDatum(data, start, size, cls) is { } datum)
                    grid = new GridDatumData(datum.Name, datum.StartPoint, datum.EndPoint, datum.CutVector, datum.LevelInterval,
                        datum.XVector, datum.Envelope, datum.Origin, datum.YVector);
                if (values is not { Count: > 0 }) values = null;
                hasNative |= values != null;
                return new Update(values, grid, decodedRoot, Level: level);
            }

            if (state.R22View && root && TfBlockDeserializer.IsSubclassOf(cls, 1794))
                return new Update(TfBlockDeserializer.ExtractParams(data, start, size, cls), null, false, MergeDrive: true);
            return default;
        }

        private void Apply(uint owner, Update update)
        {
            if (update.Root) state.NativeParamsFromRootClass.Add(owner);
            if (update.MergeDrive)
                AddDriveCurveFromRewrittenBlock(state.NativeParams[owner], update.Values);
            else if (update.Values != null)
                state.NativeParams[owner] = update.Values;
            if (update.Grid is { } grid) state.GridDatums[owner] = grid;
            if (update.Level is { } level) state.LevelPlanes[owner] = level;
        }
    }
}
