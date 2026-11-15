using RvtIO.BuiltIns;
using RvtIO.Model;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The project information of a document (RvtDocument.ProjectInformation): the fields of the project
/// information element, read when the project information stream does not carry them.
/// </summary>
public class ProjectInfoTests
{
    private static Element ProjectInformation(params (BuiltInParameter Id, string Value)[] fields)
    {
        var element = new Element { Id = 1, CategoryId = (int)BuiltInCategory.OST_ProjectInformation };
        foreach (var (id, value) in fields)
            element.Parameters.Add(new Parameter { Id = (int)id, Value = value });
        return element;
    }

    [Fact]
    public void ProjectInformation_ComesFromTheProjectInformationElement()
    {
        var document = RvtDocument.FromInfo(new RevitFileInfo
        {
            Elements =
            {
                ProjectInformation(
                    (BuiltInParameter.PROJECT_NAME, "Sample House"),
                    (BuiltInParameter.PROJECT_NUMBER, "001-00"),
                    (BuiltInParameter.PROJECT_ADDRESS, "Enter address here")),
            },
        });

        Assert.Equal("Sample House", document.ProjectInformation.Name);
        Assert.Equal("001-00", document.ProjectInformation.Number);
        Assert.Equal("Enter address here", document.ProjectInformation.Address);
    }

    [Fact]
    public void ProjectInformation_KeepsTheValueOfTheStream()
    {
        var info = new RevitFileInfo { ProjectInfo = { Name = "From the stream" } };
        info.Elements.Add(ProjectInformation((BuiltInParameter.PROJECT_NAME, "From the element")));
        var document = RvtDocument.FromInfo(info);

        Assert.Equal("From the stream", document.ProjectInformation.Name);
    }
}
