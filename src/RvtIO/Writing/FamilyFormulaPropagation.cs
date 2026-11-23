using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using static RvtIO.Writing.LoadedFamilyParameterWriter;
using static RvtIO.Writing.ParameterWriter;
using FormulaValue = RvtIO.Writing.ParameterFormulaEvaluator.Value;

namespace RvtIO.Writing;

internal static class FamilyFormulaPropagation
{
    private const string FormulaReference = "formula-reference";
    internal sealed record Result(long Id, FormulaParameterStorage Format, byte[] Replacement);
    internal sealed record InstanceValues(byte[] Body, FamilyStore Store);
    private readonly record struct Change(FormulaValue Before, FormulaValue After);

    internal static Dictionary<long, HashSet<long>> Dependencies(WideBlockWalker.Walk family, FamilyStore store)
    {
        var dependents = new Dictionary<long, HashSet<long>>();
        IndexDependencies(family, store, References(family), [], dependents);
        return dependents;
    }

    internal static List<Result> Recalculate(WideBlockWalker walker, WideBlockWalker.Walk family, byte[] body,
        IEnumerable<FamilyStore> stores, FamilyStore selected, FamilyStore? activeMirror,
        long inputId, byte[] inputValue, int idWidth, Func<long, bool> canRecalculate,
        Func<long, FormulaParameterStorage> formatOf, Func<long, IReadOnlySet<long>?>? declaredUses = null,
        InstanceValues? instance = null)
    {
        var references = References(family);
        if (!references.Any(group => group.Any(c => c.Integer == inputId)) && declaredUses?.Invoke(inputId) is null)
            return [];

        var formulaSlots = new HashSet<int>();
        var dependents = new Dictionary<long, HashSet<long>>();
        var mirrorDependents = new Dictionary<long, HashSet<long>>();
        foreach (var store in stores)
            IndexDependencies(family, store, references, formulaSlots,
                store == selected ? dependents : store == activeMirror ? mirrorDependents : null);

        var affected = new HashSet<long> { inputId };
        var remaining = new Queue<long>();
        remaining.Enqueue(inputId);
        while (remaining.TryDequeue(out long id))
        {
            if (!canRecalculate(id))
                throw Unverified("A parameter in the family formula chain also drives geometry or another element.");
            var uses = dependents.GetValueOrDefault(id);
            if (activeMirror is not null && !SameUses(uses, mirrorDependents.GetValueOrDefault(id)))
                throw Unverified("The active type and current family formula dependencies disagree.");
            if (declaredUses is not null && !SameUses(uses, declaredUses(id)))
                throw Unverified("The loaded type and family formula dependencies disagree.");
            if (uses is null)
                continue;
            foreach (long dependent in uses)
                if (affected.Add(dependent))
                    remaining.Enqueue(dependent);
        }
        if (references.Any(group => !formulaSlots.Contains(group.Key) && group.Any(c => affected.Contains(c.Integer))))
            throw Unverified("A parameter in the family formula chain is referenced outside the family type values.");
        if (affected.Count == 1)
            return [];

        bool editsInstanceInput = instance is not null && FindFormulaRow(selected, inputId).Instance == 1;
        var result = new List<Result>();
        var resolved = new Dictionary<long, Change>();
        var evaluating = new HashSet<long>();
        foreach (long id in affected)
            Resolve(id, ParameterFormulaEvaluator.MaxDepth);
        return result;

        Change Resolve(long id, int depth)
        {
            if (resolved.TryGetValue(id, out var change))
                return change;
            if (depth <= 0 || !evaluating.Add(id))
                throw Unverified("The family formula dependency chain cannot be resolved.");

            var row = FindFormulaRow(selected, id);
            var format = formatOf(id);
            NamedValue? instanceRow = instance is not null && row.Instance == 1
                ? FindRow(instance.Store, id, 0, required: affected.Contains(id))
                : null;
            var location = Location(instanceRow ?? row, format.Storage, idWidth);
            var stored = (instanceRow is null ? body : instance!.Body).AsSpan(location.Offset, location.Length);
            var oldValue = format.Decode(stored);
            NamedValue? mirror = activeMirror is null ? null : FindFormulaRow(activeMirror, id);
            if (mirror is not null)
            {
                var copy = Location(mirror, format.Storage, idWidth);
                if (copy.Offset == location.Offset || !stored.SequenceEqual(body.AsSpan(copy.Offset, copy.Length)) ||
                    mirror.Instance != row.Instance || mirror.Fields[1].Integer != row.Fields[1].Integer)
                    throw Unverified("The active type and current family formula values disagree.");
            }

            if (id == inputId)
            {
                change = new(oldValue, format.Decode(inputValue));
                if (format.Storage == StorageType.Double)
                    ParameterFormulaEvaluator.VerifyStableChange(change.Before.Number, change.After.Number);
            }
            else if (affected.Contains(id))
            {
                // A retained intermediate result would need separate verification of downstream evaluation.
                bool retainNearValue = editsInstanceInput && instanceRow is not null && format.Unit == UnitType.UT_Currency &&
                    !dependents.ContainsKey(id);
                var replacement = Evaluate(row, format, depth, stored, retainNearValue);
                if (mirror is not null && !replacement.AsSpan().SequenceEqual(Evaluate(mirror, format, depth, stored, retainNearValue)))
                    throw Unverified("The two active family formula copies produced different values.");
                change = new(oldValue, format.Decode(replacement));
                if (format.Storage == StorageType.Double)
                    ParameterFormulaEvaluator.VerifyStableChange(change.Before.Number, change.After.Number);
                if (instance is null || instanceRow is not null)
                    result.Add(new(id, format, replacement));
            }
            else
            {
                change = new(oldValue, oldValue);
            }

            evaluating.Remove(id);
            resolved.Add(id, change);
            return change;
        }

        byte[] Evaluate(NamedValue row, FormulaParameterStorage format, int depth, ReadOnlySpan<byte> stored, bool retainNearValue)
        {
            var expression = row.Fields[1];
            double savedNumber = retainNearValue ? format.Decode(stored).Number : 0;
            if (ParameterFormulaEvaluator.Evaluate(walker, family, expression, depth, (id, remaining) => Resolve(id, remaining).Before)
                    is not { } before || (!stored.SequenceEqual(format.Encode(before)) && !ShouldRetain(before)))
                throw Unverified("A family formula does not reproduce its stored value exactly.");
            if (ParameterFormulaEvaluator.Evaluate(walker, family, expression, depth, (id, remaining) => Resolve(id, remaining).After)
                    is not { } after)
                throw new RvtWriteRefusedException("invalid-parameter-value", "The family formula did not produce a value for this input.");
            byte[] replacement = format.Encode(after);
            return ShouldRetain(after) ? stored.ToArray() : replacement;

            bool ShouldRetain(FormulaValue calculated) => retainNearValue &&
                ParameterFormulaEvaluator.IsWithinVerifiedRelativeTolerance(savedNumber, calculated.Number);
        }
    }

    private static bool SameUses(HashSet<long>? actual, IReadOnlySet<long>? expected)
        => actual is null ? expected is null : expected is not null && actual.SetEquals(expected);

    private static ILookup<int, WideBlockWalker.Capture> References(WideBlockWalker.Walk family)
        => family.Captures.Where(c => c.Key == FormulaReference && c.Integer > 0).ToLookup(c => c.QueueIndex);

    private static void IndexDependencies(WideBlockWalker.Walk family, FamilyStore store,
        ILookup<int, WideBlockWalker.Capture> references, HashSet<int> owned,
        Dictionary<long, HashSet<long>>? dependents)
    {
        foreach (var row in store.Rows.Where(r => r.Fields[1].Integer != 0))
        {
            var pending = new Queue<int>();
            pending.Enqueue(ParameterFormulaEvaluator.ChildSlot(row.Fields[1], family));
            while (pending.TryDequeue(out int slot))
            {
                if (!owned.Add(slot))
                    throw Unverified("The family formula objects do not have unique ownership.");
                if (dependents is not null)
                    foreach (var reference in references[slot])
                    {
                        if (!dependents.TryGetValue(reference.Integer, out var uses))
                            dependents.Add(reference.Integer, uses = []);
                        uses.Add(row.Id);
                    }
                for (int child = family.QueueCountAtStart[slot]; child < family.QueueEnd[slot]; child++)
                    pending.Enqueue(child);
            }
        }
    }

    internal static NamedValue FindFormulaRow(FamilyStore store, long id)
    {
        if (store.Rows.Where(row => row.Id == id).ToArray() is not [var item] ||
            item.Instance is not (0 or 1) || item.Fields[7].Integer != 0)
            throw Unverified("A formula parameter has no unique stored value or is reporting.");
        return item;
    }
}
