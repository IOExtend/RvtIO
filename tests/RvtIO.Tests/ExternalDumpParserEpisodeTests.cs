using RvtIO.Internal;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>
/// Regression coverage for mixed provenance: <c>RvtFileReader.Read()</c> fills <c>ParsingState.EpisodeGuids</c> from the
/// RVT's own history stream before <c>ExternalDumpParser.Parse</c> runs. A dump with element bodies but no episode
/// list of its own must not leave its element indices paired with that GUID table (the indexings differ); otherwise
/// <c>BuildUniqueId</c> would produce a wrong <c>UniqueId</c> instead of none.
/// </summary>
public class ExternalDumpParserEpisodeTests
{
    private const string P = "AbCd";

    [Fact]
    public void EpisodeIndicesWithoutOwnEpisodeList_DoNotPairWithAPreexistingGuidTable()
    {
        var dumpPath = Path.GetTempFileName();
        File.WriteAllLines(dumpPath, new[]
        {
            "Part: Body; Handle: 100; Class: " + P + "Wall",
            "    " + P + "ElemRec ElemRec",
            "    int CreationDate . . . . . . 42",
        });
        try
        {
            var state = new ParsingState
            {
                // Simulates the RVT-sourced table RvtFileReader.Read() already assigned before
                // ExternalDumpParser.Parse runs: real, non-empty, and indexed by a different scheme.
                EpisodeGuids = new List<string>
                {
                    "aaaaaaaa-0000-0000-0000-000000000000",
                    "bbbbbbbb-0000-0000-0000-000000000000",
                },
            };

            ExternalDumpParser.Parse(dumpPath, state);

            // The dump did extract an element-to-index pairing,
            Assert.True(state.ElemEpisodeNumbers.TryGetValue(100, out int idx));
            Assert.Equal(42, idx);
            // but it must not be left paired with a GUID table sourced elsewhere: this dump
            // supplied no EpisodeList of its own, so the pre-existing table is cleared rather
            // than silently reused.
            Assert.Empty(state.EpisodeGuids);
        }
        finally
        {
            File.Delete(dumpPath);
        }
    }

    [Fact]
    public void EpisodeIndicesWithOwnEpisodeList_OverwriteThePreexistingGuidTable()
    {
        var dumpPath = Path.GetTempFileName();
        File.WriteAllLines(dumpPath, new[]
        {
            "VariantArray EpisodeList",
            "  " + P + "Episode item[0]",
            "    GUID GUID . . . . . . 11111111-1111-1111-1111-111111111111",
            "Part: Body; Handle: 100; Class: " + P + "Wall",
            "    " + P + "ElemRec ElemRec",
            "    int CreationDate . . . . . . 0",
        });
        try
        {
            var state = new ParsingState
            {
                EpisodeGuids = new List<string> { "aaaaaaaa-0000-0000-0000-000000000000" },
            };

            ExternalDumpParser.Parse(dumpPath, state);

            // A dump that supplies its own episode list is a self-consistent, single-source
            // pairing (table and indices read from the same dump); this behaviour must be unaffected.
            Assert.Single(state.EpisodeGuids);
            Assert.Equal("11111111-1111-1111-1111-111111111111", state.EpisodeGuids[0]);
            Assert.Equal(0, state.ElemEpisodeNumbers[100]);
        }
        finally
        {
            File.Delete(dumpPath);
        }
    }

    [Fact]
    public void NoEpisodeDataAtAll_LeavesEpisodeGuidsUntouched()
    {
        // Sanity check: a dump that contributes nothing episode-related (no bodies with
        // CreationDate, no EpisodeList) must not clear an unrelated, already-populated table;
        // the clearing is specific to the mixed-provenance case, not a blanket reset.
        var dumpPath = Path.GetTempFileName();
        File.WriteAllLines(dumpPath, new[]
        {
            "Part: Body; Handle: 100; Class: " + P + "BasicWallType",
            "    VariantArray Layers",
            "    double LayerWidth . . . . . . 0.5",
        });
        try
        {
            var state = new ParsingState
            {
                EpisodeGuids = new List<string> { "aaaaaaaa-0000-0000-0000-000000000000" },
            };

            ExternalDumpParser.Parse(dumpPath, state);

            Assert.Empty(state.ElemEpisodeNumbers);
            Assert.Single(state.EpisodeGuids);
        }
        finally
        {
            File.Delete(dumpPath);
        }
    }
}
