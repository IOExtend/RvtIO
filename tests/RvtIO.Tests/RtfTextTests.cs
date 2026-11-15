using System.Text;
using RvtIO.Parsing;
using Xunit;

namespace RvtIO.Tests;

/// <summary>The RTF reader of the text notes (src/RvtIO/Parsing/RtfText): escaped bytes in Windows-1251 and UTF-8,
/// skipped destinations, control words and the declared font size.</summary>
public class RtfTextTests
{
    private static (string Text, double? FontSizePoints)? Parse(string rtf) =>
        RtfText.Parse(Encoding.ASCII.GetBytes(rtf), 0, Encoding.ASCII.GetByteCount(rtf));

    private static (string Text, double? FontSizePoints)? Parse(byte[] bytes) => RtfText.Parse(bytes, 0, bytes.Length);

    [Fact]
    public void Parse_DecodesEscapedCyrillicWithTheDeclaredCodePage()
    {
        var parsed = Parse(@"{\rtf1\ansi\ansicp1251{\fonttbl{\f0 Arial;}}\f0 \'cf\'f0\'e8\'e2\'e5\'f2}");

        Assert.NotNull(parsed);
        Assert.Equal("Привет", parsed!.Value.Text);
    }

    [Fact]
    public void Parse_DecodesRawUtf8Text()
    {
        byte[] head = Encoding.ASCII.GetBytes(@"{\rtf1\ansi\ansicp1251\f0 ");
        byte[] body = Encoding.UTF8.GetBytes("Привет}");
        var bytes = head.Concat(body).ToArray();

        var parsed = Parse(bytes);

        Assert.Equal("Привет", parsed?.Text);
    }

    [Fact]
    public void Parse_SkipsDestinationsAndKeepsParagraphs()
    {
        var parsed = Parse(@"{\rtf1{\fonttbl{\f0 Arial;}}{\colortbl;\red0\green0\blue0;}\f0 Hello\par World}");

        Assert.Equal("Hello\nWorld", parsed?.Text);
    }

    [Fact]
    public void Parse_ReadsTheFontSizeAndTabs()
    {
        var parsed = Parse(@"{\rtf1\fs24 one\tab two}");

        Assert.Equal("one\ttwo", parsed?.Text);
        Assert.Equal(12, parsed?.FontSizePoints);
    }

    [Fact]
    public void Parse_SkipsAGeneratorGroup()
    {
        var parsed = Parse(@"{\rtf1{\*\generator Riched20 10.0.19041}\f0 Text}");

        Assert.Equal("Text", parsed?.Text);
    }

    [Fact]
    public void Parse_DecodesTheStandardCodePageWord()
    {
        // The standard control word is \ansicpg (a byte of Windows-1252 here); \ansicp is Revit's own spelling.
        var parsed = Parse("{\\rtf1\\ansi\\ansicpg1252 \\'e9}");

        Assert.Equal("é", parsed?.Text);
    }

    [Fact]
    public void Parse_KeepsUnicodeCharactersOutOfTheCodePageBytes()
    {
        // \u1040 is Cyrillic A; the '?' is its fallback character and \'c1 is Cyrillic B in Windows-1251.
        var parsed = Parse("{\\rtf1\\ansi\\ansicpg1251\\uc1 \\u1040?\\'c1}");

        Assert.Equal("АБ", parsed?.Text);
    }

    [Fact]
    public void Parse_RespectsUc0AndSkipsNoFallbackCharacter()
    {
        var parsed = Parse("{\\rtf1\\ansi\\uc0 \\u1040B}");

        Assert.Equal("АB", parsed?.Text);
    }

    [Fact]
    public void Parse_DecodesEachCodePageRunWithItsOwnCodePage()
    {
        var parsed = Parse("{\\rtf1\\ansi\\ansicpg1251 \\'c0\\'c1\\ansicpg1252 \\'e9}");

        Assert.Equal("АБé", parsed?.Text);
    }

    [Fact]
    public void Parse_RestoresTheUcCountOfTheOuterGroup()
    {
        // The inner group sets uc0, so the B is text there; the outer group's uc1 applies again after it.
        var parsed = Parse("{\\rtf1\\ansi\\uc1 \\u1040?{\\uc0 \\u1041B}C}");

        Assert.Equal("АБBC", parsed?.Text);
    }

    [Fact]
    public void Parse_ReturnsNullWhenTheBytesAreNotAnRtfString()
    {
        Assert.Null(Parse("plain text"));
        Assert.Null(Parse(@"{ not rtf }"));
    }
}
