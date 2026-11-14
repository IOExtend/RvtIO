using RvtIO.Model;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The SeparateFacesWithDistinctOpenings option belongs to the read it was given to: reads with different options that run
/// at the same time each build the faces their own option asks for. (It was once a static the last read wrote, so concurrent
/// reads of one file gave elements a face more or less, and tests of different classes disturbed each other.)</summary>
public class SeparateFacesPerReadTests
{
    private static Dictionary<int, int> FaceCounts(string path, bool separate)
    {
        var info = new RvtFileReader(new RvtReaderOptions { SeparateFacesWithDistinctOpenings = separate }).Read(path);
        var counts = new Dictionary<int, int>();
        foreach (var element in info.Elements) counts[element.Id] = element.Geometry?.Faces.Count ?? -1;
        return counts;
    }

    [SkippableFact]
    public void ReadsThatRunAtTheSameTimeKeepTheirOwnOption()
    {
        string path = TestFiles.Find("Test_2022.rvt");
        var alone = new Dictionary<bool, Dictionary<int, int>> { [false] = FaceCounts(path, false), [true] = FaceCounts(path, true) };
        // The check means something only where the option changes a face count: elements of Test_2022 do (680524 has 1984 faces
        // without it and 1985 with it).
        Assert.NotEmpty(alone[false].Where(pair => alone[true].TryGetValue(pair.Key, out int other) && other != pair.Value));

        var together = new Dictionary<bool, Dictionary<int, int>>();
        Parallel.ForEach([false, true], new ParallelOptions { MaxDegreeOfParallelism = 2 }, separate =>
        {
            var counts = FaceCounts(path, separate);
            lock (together) together[separate] = counts;
        });

        foreach (bool separate in new[] { false, true })
            Assert.Equal(alone[separate].OrderBy(pair => pair.Key), together[separate].OrderBy(pair => pair.Key));
    }
}
