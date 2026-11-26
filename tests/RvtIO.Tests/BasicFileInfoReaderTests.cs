using System.Text;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

public class BasicFileInfoReaderTests
{
    private static byte[] Stream(string? labelledText, params string[] strings)
    {
        var bytes = new List<byte>(BitConverter.GetBytes(18));
        bytes.AddRange(new byte[8]);
        foreach (string value in strings)
        {
            bytes.AddRange(BitConverter.GetBytes(value.Length));
            bytes.AddRange(Encoding.Unicode.GetBytes(value));
        }
        if (labelledText != null)
        {
            bytes.AddRange(new byte[2]);
            bytes.AddRange(Encoding.Unicode.GetBytes(labelledText));
            bytes.AddRange(new byte[2]);
        }
        return bytes.ToArray();
    }

    [Fact]
    public void LaterFourDigitStringDoesNotReplaceReleaseYear()
    {
        var result = BasicFileInfoReader.Parse(Stream(null,
            "Author", "2022", "20230915_1530(x64)", "RUS", "5170", "Autodesk Revit"));

        Assert.Equal("2022", result["RevitVersion"]);
        Assert.Equal("20230915_1530(x64)", result["Build"]);
    }

    [Fact]
    public void LabelledFormatLineIsTheRelease()
    {
        var result = BasicFileInfoReader.Parse(Stream(
            "Worksharing: Local\r\nFormat: 2022\r\nBuild: 20230915_1530(x64)\r\n"
            + "Locale when saved: RUS\r\n"
            + "Central model's version number corresponding to the last reload latest: 5170\r\n",
            "5170"));

        Assert.Equal("2022", result["RevitVersion"]);
        Assert.Equal("RUS", result["Locale when saved"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void NarrowCrLfStartsTheLabelledBlock(int padding)
    {
        byte[] bytes = [.. Stream(null), .. new byte[padding], .. Encoding.Unicode.GetBytes("Autodesk Revit Application"),
            0x0D, 0x0A, .. Encoding.Unicode.GetBytes("Worksharing: Not enabled\r\nUsername: Before\U0001F600After\r\n")];

        var result = BasicFileInfoReader.Parse(bytes);

        Assert.Equal("Not enabled", result["Worksharing"]);
        Assert.Equal("Before\U0001F600After", result["Username"]);
    }

    [Fact]
    public void SingleYearStringIsUnchanged()
    {
        var result = BasicFileInfoReader.Parse(Stream(null,
            "2022", "20210224_1515(x64)", "RUS", "17"));

        Assert.Equal("2022", result["RevitVersion"]);
    }
    private static byte[] NarrowCloudStream(string value, string before = "Worksharing: Not enabled\r\nFormat: 2025\r\n",
        string after = "\r\nAuthor: Autodesk Revit\r\n")
    {
        var bytes = new List<byte>(BitConverter.GetBytes(14));
        bytes.AddRange(new byte[8]);
        bytes.AddRange(new byte[2]);
        bytes.AddRange(Encoding.Unicode.GetBytes(before + "IsSingleUserCloudModel: "));
        bytes.AddRange(Encoding.ASCII.GetBytes(value));
        bytes.Add(0);
        bytes.AddRange(Encoding.Unicode.GetBytes(after));
        bytes.AddRange(new byte[2]);
        return bytes.ToArray();
    }

    [Theory]
    [InlineData("False")] [InlineData("True")]
    public void NarrowCloudModelBoolean_IsRead_AndLaterLinesSurviveTheShiftedAlignment(string value)
    {
        var result = BasicFileInfoReader.Parse(NarrowCloudStream(value));

        Assert.Equal(value, result["IsSingleUserCloudModel"]);
        Assert.Equal("Autodesk Revit", result["Author"]);
        Assert.Equal("2025", result["RevitVersion"]);
    }

    [Fact]
    public void WideCloudModelBoolean_IsRead_AndAnythingElseIsNot()
    {
        var wide = BasicFileInfoReader.Parse(Stream("Format: 2022\r\nIsSingleUserCloudModel: True\r\nAuthor: x\r\n"));
        Assert.Equal("True", wide["IsSingleUserCloudModel"]);
        Assert.False(BasicFileInfoReader.Parse(NarrowCloudStream("Maybe")).ContainsKey("IsSingleUserCloudModel"));
        Assert.False(BasicFileInfoReader.Parse(NarrowCloudStream("Falsetto")).ContainsKey("IsSingleUserCloudModel"));
    }

    [Fact]
    public void ReleaseWrittenInsideTheBuildText_IsTheRelease()
    {
        var result = BasicFileInfoReader.Parse(Stream(
            "Worksharing: Not enabled\r\nRevit Build: Autodesk Revit MEP 2010 (Build: 20090317_2115)\r\n",
            "Autodesk Revit MEP 2010 (Build: 20090317_2115)"));

        Assert.Equal("2010", result["RevitVersion"]);
    }

    [Theory]
    [InlineData("20070324_1700")] [InlineData("20080126_1900")] [InlineData("20080321_1900")]
    public void BareBuildDateDoesNotDetermineTheRelease(string build)
    {
        var result = BasicFileInfoReader.Parse(Stream($"Worksharing: Not enabled\r\nRevit Build: {build}\r\n", build));

        Assert.False(result.ContainsKey("RevitVersion"));
        Assert.Equal(build, result["Build"]);
    }

    [Fact]
    public void BareBuildOutsideRevit2008And2009_GivesNoRelease()
        => Assert.False(BasicFileInfoReader.Parse(Stream("Revit Build: 20150110_1900\r\n", "20150110_1900")).ContainsKey("RevitVersion"));

    [Fact]
    public void BuildYearDoesNotOverrideAStatedRelease()
    {
        var result = BasicFileInfoReader.Parse(Stream("Format: 2022\r\nBuild: 20230915_1530(x64)\r\n", "2022", "20230915_1530(x64)"));

        Assert.Equal("2022", result["RevitVersion"]);
    }
}
