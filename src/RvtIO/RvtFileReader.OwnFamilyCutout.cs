using RvtIO.Internal;
using RvtIO.Parsing;

namespace RvtIO;

public partial class RvtFileReader
{
    internal static void RecordOwnFamilyCutoutInputs(ParsingState state, byte[] data,
        int start, uint size, ushort classId, uint owner, TfBlockDeserializer.WallCompleteInputRead? complete = null)
    {
        bool own = start >= 0 && start <= data.Length - 4 && BitConverter.ToUInt32(data, start) == owner;
        if (classId == 1772)
        {
            state.OwnFamilyInPlace[owner] = state.OwnFamilyInPlace.TryGetValue(owner, out var old)
                ? (old.BlockCount + 1, null)
                : (1, own ? TfBlockDeserializer.ExtractOwnFamilyInPlace(data, start, size, classId) : null);
        }
        if (classId == 1829)
        {
            // FUNCTION_PARAM of the symbol: a duplicate root block clears it, a value outside the WallFunction
            // range is not a function.
            int? function = own && TfBlockDeserializer.ExtractFamilySymbolFunctionInput(data, start, size, classId)
                is { Value: >= 0 and <= 5 } input && input.OwnerId == owner ? input.Value : null;
            state.FamilySymbolFunctions[owner] = state.FamilySymbolFunctions.TryGetValue(owner, out var oldFunction)
                ? (oldFunction.BlockCount + 1, null) : (1, function);
            if (state.OwnFamilyCutSymbols.TryGetValue(owner, out var old))
                state.OwnFamilyCutSymbols[owner] = (old.BlockCount + 1, null, null);
            else
            {
                var symbol = own ? TfBlockDeserializer.ExtractOwnFamilyCutSymbol(data, start, size, classId) : null;
                var loops = symbol is { Active: true }
                    ? TfBlockDeserializer.ExtractOwnFamilySymbolCutLoops(data, start, size, classId, out _) : null;
                state.OwnFamilyCutSymbols[owner] = (1, symbol, loops);
            }
        }
        if (classId is 3524 or 361)
        {
            if (own) complete ??= new(data, start, size, classId);
            state.OwnWallCutoutSteps[owner] = state.OwnWallCutoutSteps.TryGetValue(owner, out var old)
                ? (old.BlockCount + 1, null, null, null)
                : (1, own ? TfBlockDeserializer.ExtractOwnWallCutoutSteps(data, start, size, classId, out _) : null,
                    own ? TfBlockDeserializer.ExtractOwnWallLocationLine(data, start, size, classId, out _, complete) : null,
                    own ? TfBlockDeserializer.ExtractOwnWallCrossSection(data, start, size, classId) : null);
            state.OwnWallHelpers[owner] = state.OwnWallHelpers.TryGetValue(owner, out var oldHelper)
                ? (oldHelper.BlockCount + 1, null)
                : (1, own ? TfBlockDeserializer.ExtractOwnWallHelperInput(data, start, size, classId, complete) : null);
            state.OwnWallSide0Ids.Remove(owner);
            state.OwnWallBaseFaces.Remove(owner);
            state.OwnCurrentWallRoots.Remove(owner);
            bool currentRoot = own && state.OwnWallCutoutSteps[owner].BlockCount == 1
                && state.OwnPartitionWallParts?.TryGetValue(owner, out var current) == true
                && OwnPartitionParts.MatchesFrame(data, start, size, current.NodeSha256);
            if (currentRoot) state.OwnCurrentWallRoots.Add(owner);
            bool single = own && state.OwnWallCutoutSteps[owner].BlockCount == 1;
            if ((currentRoot || single) && complete?.Read(out _) is { } input)
            {
                if (currentRoot && state.OwnWallCutoutSteps[owner].Steps is { Count: > 0 }
                    && TfBlockDeserializer.SelectOwnWallSide0Id(input) is int sideId)
                    state.OwnWallSide0Ids[owner] = sideId;
                if (TfBlockDeserializer.SelectOwnWallBaseFaceHistories(input) is { } faces)
                    state.OwnWallBaseFaces[owner] = faces;
            }
        }
    }
}
