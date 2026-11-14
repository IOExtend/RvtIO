using RvtIO.Model;
using RvtIO.Parsing;

namespace RvtIO.Internal;

/// <summary>Admission from three independent RVT sources: own helper lists,
/// ElemRec ownership, and uniquely framed semantic Element classes. Never uses the
/// empirical first-frame class table, registered railings, geometry, or IFC output.</summary>
internal static class StairComponentSource
{
    internal sealed record MembershipRoot(int BlockCount, TfBlockDeserializer.StairComponentIds? Value);
    internal sealed record ElementClassRoot(int BlockCount, ushort? ClassId);
    internal readonly record struct CoreComponent(int Id, ushort ClassId);

    internal static void Record(ParsingState state, byte[] data, int start, uint size,
        ushort classId, uint owner)
    {
        bool headerMatches = start >= 0 && start <= data.Length - 4
            && BitConverter.ToUInt32(data, start) == owner;
        if (TfBlockDeserializer.IsSubclassOf(classId, 2870))
        {
            string? name = headerMatches ? TfBlockDeserializer.ExtractOwnStringParameterDefinitionName(data, start, size, classId) : null;
            state.StairNamingDefinitionRoots[owner] = state.StairNamingDefinitionRoots.TryGetValue(owner, out var priorDefinition)
                ? (priorDefinition.BlockCount + 1, null) : (1, name);
        }
        if (classId is 3681 or 3708 or 3686 or 3727)
        {
            var input = headerMatches ? TfBlockDeserializer.ExtractStairNameParameters(data, start, size, classId) : null;
            state.StairNameParameterRoots[owner] = state.StairNameParameterRoots.TryGetValue(owner, out var priorNames)
                ? (priorNames.BlockCount + 1, null) : (1, input);
        }
        if (classId == 3708)
        {
            var input = headerMatches ? TfBlockDeserializer.ExtractStairRunInput(data, start, size, classId) : null;
            state.StairRunInputRoots[owner] = state.StairRunInputRoots.TryGetValue(owner, out var priorInput)
                ? (priorInput.BlockCount + 1, null) : (1, input);
        }
        if (classId == 485)
        {
            int? stair = headerMatches && TfBlockDeserializer.TryExtractRailingStairHost(data, start, size, classId, out int host)
                ? host : null;
            state.RailingStairHostRoots[owner] = state.RailingStairHostRoots.TryGetValue(owner, out var priorHost)
                ? (priorHost.BlockCount + 1, null) : (1, stair);
        }
        if (classId == 3681)
        {
            var value = headerMatches
                ? TfBlockDeserializer.ExtractStairComponentIds(data, start, size, classId) : null;
            state.StairMembershipRoots[owner] = state.StairMembershipRoots.TryGetValue(owner, out var prior)
                ? new(prior.BlockCount + 1, null) : new(1, value);
        }
        if (!CountsClassRoot(state, classId, owner)) return;
        ushort? exactClass = headerMatches
            && TfBlockDeserializer.TryExtractOwnElementClass(data, start, size, classId, out int namedOwner)
            && namedOwner == owner ? classId : null;
        state.OwnedElementClassRoots[owner] = state.OwnedElementClassRoots.TryGetValue(owner, out var existing)
            ? new(existing.BlockCount + 1, null) : new(1, exactClass);
        state.WalkedElementClassRoots[owner] = state.WalkedElementClassRoots.TryGetValue(owner, out var walked)
            ? new(walked.BlockCount + 1, null) : new(1, exactClass);
    }

    /// <summary>Whether <see cref="Record"/> counts a root of this class in <see cref="ParsingState.OwnedElementClassRoots"/>: every
    /// semantic class of table-backed ids, so a railing's non-owned duplicate cannot be hidden by scan order. Separate GElement frames
    /// never count.</summary>
    internal static bool CountsClassRoot(ParsingState state, ushort classId, uint owner) =>
        TfBlockDeserializer.IsSubclassOf(classId, 37)
        && (classId == 3681 || (state.ElemRecOwningElementIds?.ContainsKey(owner) ?? false));

    internal static void Apply(IReadOnlyList<Element> elements, ParsingState state)
    {
        foreach (var element in elements)
        {
            element.SerializedStairCore = null;
            element.SerializedStairCopyLevelIds = null;
            element.SerializedOwnElementClassId = null;
            element.OwnClassRootSeededOnce = false;
            element.SerializedOwningElementId = null;
            element.SerializedRailingStairHostId = null;
            element.SerializedStairRunInput = null;
        }
        if (state.ElemRecOwningElementIds == null) return;
        var unique = elements.GroupBy(element => element.Id)
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single());
        foreach (var element in unique.Values)
        {
            if (element.Id < 0 || element.IsBlockOnly) continue;
            if (state.ElemRecOwningElementIds.TryGetValue((uint)element.Id, out int owner))
                element.SerializedOwningElementId = owner;
            if (state.StairClassRoot((uint)element.Id) is { BlockCount: 1 } root)
                element.SerializedOwnElementClassId = root.ClassId;
            element.OwnClassRootSeededOnce = state.OwnedElementClassRoots.TryGetValue((uint)element.Id, out var seeded)
                && seeded is { BlockCount: 1, ClassId: not null };
            if (element.Id == 0) continue;
            if (state.RailingStairHostRoots.TryGetValue((uint)element.Id, out var railing)
                && railing is { BlockCount: 1, StairId: > 0 }
                && element.SerializedOwnElementClassId == 485 && element.SerializedOwningElementId != null
                && railing.StairId != element.Id)
                element.SerializedRailingStairHostId = railing.StairId;
            if (element.SerializedOwnElementClassId == 3708
                && state.StairRunInputRoots.TryGetValue((uint)element.Id, out var run)
                && run is { BlockCount: 1, Input: { } input } && input.OwnerId == element.Id
                && input.StairsId == element.SerializedOwningElementId && input.StairsId != element.Id)
                element.SerializedStairRunInput = input;
        }
        var inverse = new Dictionary<int, List<uint>>();
        foreach (var (child, owner) in state.ElemRecOwningElementIds)
        {
            if (owner <= 0 || !state.StairMembershipRoots.ContainsKey((uint)owner)) continue;
            if (!inverse.TryGetValue(owner, out var owned)) inverse[owner] = owned = [];
            owned.Add(child);
        }
        foreach (var (id, source) in state.StairMembershipRoots)
        {
            if (id > int.MaxValue || source is not { BlockCount: 1, Value: { } membership }
                || membership.OwnerId != id || !unique.TryGetValue((int)id, out var parent)
                || parent.IsBlockOnly || parent.CategoryId != -2000120
                || parent.SerializedOwnElementClassId != 3681
                || parent.SerializedOwningElementId == null
                || !inverse.TryGetValue(parent.Id, out var owned)) continue;
            var typed = new Dictionary<int, ushort>();
            bool valid = true;
            foreach (uint child in owned)
            {
                // Missing or ambiguous service roots also reject the closure: they must
                // not hide an omitted run/landing/support behind a guessed class.
                if (child > int.MaxValue
                    || state.StairClassRoot(child) is not { BlockCount: 1, ClassId: { } cls }) { valid = false; break; }
                if (cls is 3708 or 3686 or 3727) typed.Add((int)child, cls);
            }
            var listed = membership.RunsAndLandings.Concat(membership.Supports).ToArray();
            if (!valid || listed.Length == 0 || listed.Length != typed.Count
                || listed.Distinct().Count() != listed.Length
                || membership.RunsAndLandings.Any(child => !typed.TryGetValue(child, out var cls) || cls is not (3708 or 3686))
                || membership.Supports.Any(child => !typed.TryGetValue(child, out var cls) || cls != 3727)
                || listed.Any(child => child <= 0 || child == parent.Id
                    || !unique.TryGetValue(child, out var element) || element.IsBlockOnly
                    || element.SerializedOwningElementId != parent.Id
                    || element.SerializedOwnElementClassId != typed[child])) continue;
            parent.SerializedStairCore = listed.Select(child => new CoreComponent(child, typed[child])).ToArray();
            parent.SerializedStairCopyLevelIds = membership.MultistoryCopyLevelIds;
        }
    }

}
