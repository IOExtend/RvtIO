using System.Globalization;
using System.Text;
using RvtIO.Pdf;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The objects of a PDF written by <see cref="PdfDocument"/>, found through its cross-reference table. A test
/// asserts on one object instead of on a substring of the whole file, and the lookup fails when an offset of the table
/// does not point at the object it names.</summary>
internal sealed class PdfObjects
{
    private readonly Dictionary<int, string> _objects = [];

    public static PdfObjects Write(PdfDocument document)
    {
        using var stream = new MemoryStream();
        document.Save(stream);
        // Latin-1 maps every byte to one character, so offsets of the table index the string directly.
        string text = Encoding.Latin1.GetString(stream.ToArray());

        int startXref = text.LastIndexOf("\nstartxref\n", StringComparison.Ordinal) + "\nstartxref\n".Length;
        int xrefOffset = int.Parse(text[startXref..text.IndexOf('\n', startXref)], CultureInfo.InvariantCulture);
        Assert.StartsWith("xref\n", text[xrefOffset..]);
        string[] lines = text[(xrefOffset + "xref\n".Length)..].Split('\n');
        int count = int.Parse(lines[0].Split(' ')[1], CultureInfo.InvariantCulture);
        Assert.Equal("0000000000 65535 f ", lines[1]);

        var offsets = new List<(int Id, int Offset)>();
        for (int id = 1; id < count; id++)
        {
            int offset = int.Parse(lines[1 + id][..10], CultureInfo.InvariantCulture);
            Assert.EndsWith(" 00000 n ", lines[1 + id]);
            Assert.StartsWith($"{id} 0 obj\n", text[offset..]);
            offsets.Add((id, offset));
        }
        Assert.Contains($"/Size {count} ", text[xrefOffset..]);

        var result = new PdfObjects();
        var ordered = offsets.OrderBy(item => item.Offset).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            int end = i + 1 < ordered.Count ? ordered[i + 1].Offset : xrefOffset;
            string body = text[ordered[i].Offset..end];
            Assert.EndsWith("endobj\n", body);
            result._objects[ordered[i].Id] = body[(body.IndexOf('\n') + 1)..];
        }
        return result;
    }

    public int Count => _objects.Count;

    /// <summary>The text of the object, from its dictionary to its stream.</summary>
    public string this[int id] => _objects[id];

    /// <summary>The id of the one object whose text contains <paramref name="marker"/>.</summary>
    public int IdOf(string marker) => _objects.Single(item => item.Value.Contains(marker, StringComparison.Ordinal)).Key;

    /// <summary>The object an indirect reference in the text of another object names, read after <paramref name="key"/>.</summary>
    public int Reference(string text, string key)
    {
        int at = text.IndexOf(key, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{key} is not in {text}");
        string rest = text[(at + key.Length)..].TrimStart(' ', '[');
        return int.Parse(rest[..rest.IndexOf(' ')], CultureInfo.InvariantCulture);
    }
}
