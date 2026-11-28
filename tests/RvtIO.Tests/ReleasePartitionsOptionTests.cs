using IOScene;
using RvtIO.Model;
using RvtIO.Scene;
using Xunit;

namespace RvtIO.Tests;

/// <summary><see cref="RvtReaderOptions.ReleasePartitions"/>: the reader drops the raw partition streams before it builds the model,
/// and the model it reads is the same.</summary>
public class ReleasePartitionsOptionTests
{
    [Fact]
    public void OptionReleasesThePartitionsOfAFileInTheRevit2022Layout()
    {
        string path = SyntheticModel();
        try
        {
            var keeping = new RvtFileReader();
            keeping.Read(path);
            var releasing = new RvtFileReader(new RvtReaderOptions { ReleasePartitions = true });
            releasing.Read(path);

            // The release applies to a file read in the layout the reader ships, which the synthetic model is.
            Assert.Null(releasing.LastParsingState!.WideSchema);
            Assert.NotEmpty(keeping.LastParsingState!.Chunks);
            Assert.Empty(releasing.LastParsingState.Chunks);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ElementsParametersGeometryAndLocationAreTheSameWithTheOption()
    {
        string path = SyntheticModel();
        try
        {
            var kept = new RvtFileReader().Read(path);
            var released = new RvtFileReader(new RvtReaderOptions { ReleasePartitions = true }).Read(path);

            Assert.NotEmpty(kept.Elements);
            Assert.Contains(kept.Elements, element => element.Geometry != null);
            Assert.Equal(Describe(kept), Describe(released));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SceneReadAsksForTheRelease() =>
        Assert.True(RvtSceneReader.ReaderOptions(new RvtReadDiagnostics(), null).ReleasePartitions);

    [Fact]
    public void SyntheticModel_SceneIsTheSameAsFromADocumentThatKeptItsPartitions()
    {
        string path = SyntheticModel();
        try { AssertSameScene(path); }
        finally { File.Delete(path); }
    }

    /// <summary>The scene of a real file of Revit 2022 has meshes, properties and levels, which the synthetic model has few of.</summary>
    [SkippableFact]
    public void Test2022_SceneIsTheSameAsFromADocumentThatKeptItsPartitions() =>
        Assert.NotEmpty(AssertSameScene(TestFiles.Find("Test_2022.rvt")).Elements);

    /// <summary>Compares the scene of <see cref="RvtSceneReader.Read(string, RvtSceneOptions?)"/>, which releases the partitions, with the
    /// scene mapped from a document that was opened with the same options except the release.</summary>
    private static SceneDocument AssertSameScene(string path)
    {
        var keeping = RvtSceneReader.ReaderOptions(new RvtReadDiagnostics(), null);
        keeping.ReleasePartitions = false;
        var kept = RvtSceneReader.Read(RvtDocument.Open(path, keeping), new RvtSceneOptions { ModelKey = Path.GetFileName(path) });
        var released = RvtSceneReader.Read(path);

        Assert.Equal(SceneJson.Write(kept.Document), SceneJson.Write(released.Document));
        return released.Document;
    }

    private static string SyntheticModel()
    {
        string path = TestFiles.Scratch($"release-partitions-{Guid.NewGuid():N}.rvt");
        SpatialPhaseTests.WriteSyntheticNativeR22ModelWithPhaseZero(path);
        return path;
    }

    // The release moves the reading of the project location ahead of the model, so the location is compared with the elements.
    private static string[] Describe(RevitFileInfo info) =>
    [
        $"transform={Numbers(info.SharedTransform)} active={info.SharedTransformIsActiveLocation} site={info.SiteLatitudeRadians}/"
            + $"{info.SiteLongitudeRadians}/{info.SiteElevationFeet}/{info.SitePlaceName}",
        .. info.Elements.Select(element => string.Join('|', element.Id, element.CategoryId, element.Category, element.Name, element.TypeId,
            element.UniqueId, element.Geometry?.Faces.Count, Numbers(element.Geometry?.BoundingBox ?? element.BoundingBox),
            string.Join(';', element.Parameters.Select(parameter =>
                $"{parameter.Id}={parameter.Value}:{parameter.HasNoRevitValue}:{parameter.HasResolvedZeroElementId}")))),
    ];

    private static string Numbers(double[]? values) => values == null ? "-" : string.Join(',', values);
}
