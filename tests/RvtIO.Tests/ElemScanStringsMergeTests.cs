using System.Text;
using RvtIO.Internal;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class ElemScanStringsMergeTests
{
    private const uint ElementId = 42;
    private const string A = "A candidate";
    private const string B = "B candidate";
    private const string C = "C candidate";
    private const string Same = "same";

    [Fact]
    public void ReverseCompletionOrder_PreservesSourceChunkOrderAndDeduplicates()
    {
        var partialResults = ReverseCompletedResults();
        var merged = new Dictionary<uint, List<string>>();

        RvtFileReader.MergeElemScanStringsInChunkOrder(partialResults, merged);

        Assert.Equal([B, Same, A, C], merged[ElementId]);
    }

    [Fact]
    public void ResolveElementName_AfterReverseCompletion_PicksFirstSourceChunkCandidate()
    {
        var state = new ParsingState();
        RvtFileReader.MergeElemScanStringsInChunkOrder(
            ReverseCompletedResults(), state.ElemScanStrings);

        Assert.Equal(B, ElementBuilder.ResolveElementName(ElementId, state));
    }

    [Fact]
    public void FirstNameOnly_PreservesTheFirstValidNameAcrossChunks()
    {
        byte[][] chunks =
        [
            NameChunk("autodesk.parameter.invalid", B, Same),
            NameChunk(A, Same, C),
        ];
        var all = Scan(firstNameOnly: false);
        var firstOnly = Scan(firstNameOnly: true);
        Assert.Equal([B, Same, A, C], all[ElementId]);
        Assert.Equal([B], firstOnly[ElementId]);

        Dictionary<uint, List<string>> Scan(bool firstNameOnly)
        {
            var partials = new Dictionary<uint, List<string>>[chunks.Length];
            for (int i = chunks.Length - 1; i >= 0; i--)
            {
                partials[i] = new();
                StringScanner.ScanChunk(chunks[i], [ElementId], partials[i], firstNameOnly);
            }

            var names = new Dictionary<uint, List<string>>();
            foreach (var partial in partials)
                RvtFileReader.MergeElemScanStringsInChunkOrder([partial], names, firstNameOnly: firstNameOnly);
            return names;
        }

        static byte[] NameChunk(params string[] names)
        {
            var bytes = new byte[256];
            using var stream = new MemoryStream(bytes);
            using var writer = new BinaryWriter(stream, Encoding.Unicode);
            writer.Write(ElementId);
            foreach (string name in names)
            {
                writer.Write(name.Length);
                writer.Write(Encoding.Unicode.GetBytes(name));
            }
            return bytes;
        }
    }

    private static Dictionary<uint, List<string>>?[] ReverseCompletedResults()
    {
        var partialResults = new Dictionary<uint, List<string>>?[2];

        // Simulate chunk 1 finishing before chunk 0. Indexed storage must still make the
        // merge consume chunk 0 first, preserving the serialized source order.
        partialResults[1] = Partial(A, Same, C);
        partialResults[0] = Partial(B, Same);

        return partialResults;
    }

    private static Dictionary<uint, List<string>> Partial(params string[] strings) =>
        new() { [ElementId] = [.. strings] };
}
