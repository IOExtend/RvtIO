using RvtIO.Parsing;
using RvtIO.Writing;
using Xunit;
using Cls = RvtIO.Tests.SchemaStreamEditorTests.Cls;
using Fld = RvtIO.Tests.SchemaStreamEditorTests.Fld;
using NoRef = RvtIO.Tests.SchemaStreamEditorTests.NoRef;

namespace RvtIO.Tests;

/// <summary>Transcoding a Global/History episode table across releases remaps only its
/// opening DocumentHistory class tag; every other byte is preserved verbatim.</summary>
public sealed class DowngradeHistoryRewriterTests
{
    private static SchemaDocument Doc(params Cls[] classes) =>
        TfSchemaReader.Parse(SchemaStreamEditorTests.Write([.. classes]).Data);

    private static byte[] Table(ushort tag, params byte[] rest)
    {
        var payload = new byte[2 + rest.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(payload, tag);
        rest.CopyTo(payload, 2);
        return payload;
    }

    [Fact]
    public void Rewrite_SwapsOnlyTheOpeningTag()
    {
        // Different DocumentHistory ids: filler pushes the target class to id 13.
        var source = Doc(new Cls("DocumentHistory", new NoRef(), 1, [new Fld("m_x", 0x04)], []));
        var target = Doc(
            new Cls("Filler", new NoRef(), 1, [], []),
            new Cls("DocumentHistory", new NoRef(), 1, [new Fld("m_x", 0x04)], []));
        byte[] rest = [1, 0, 0, 0, 0, 0, 0, 0, 0xA2, 0xB3, 0xC4];
        byte[] result = DowngradeHistoryRewriter.Rewrite(Table(12, rest), source, target);
        Assert.Equal(Table(13, rest), result);
    }

    [Fact]
    public void Rewrite_EqualIds_IsAVerbatimCopy()
    {
        var source = Doc(new Cls("DocumentHistory", new NoRef(), 1, [], []));
        var target = Doc(new Cls("DocumentHistory", new NoRef(), 1, [], []));
        byte[] payload = Table(12, [1, 2, 3, 4]);
        Assert.Equal(payload, DowngradeHistoryRewriter.Rewrite(payload, source, target));
    }

    [Fact]
    public void Rewrite_WrongOpeningTag_IsRejected()
    {
        var source = Doc(new Cls("DocumentHistory", new NoRef(), 1, [], []));
        var target = Doc(new Cls("DocumentHistory", new NoRef(), 1, [], []));
        Assert.Throws<InvalidDataException>(() => DowngradeHistoryRewriter.Rewrite(Table(99, [1, 2]), source, target));
        Assert.Throws<InvalidDataException>(() => DowngradeHistoryRewriter.Rewrite([0x12], source, target));
    }

    [Fact]
    public void Rewrite_MissingDocumentHistoryClass_IsRejected()
    {
        var with = Doc(new Cls("DocumentHistory", new NoRef(), 1, [], []));
        var without = Doc(new Cls("Other", new NoRef(), 1, [], []));
        Assert.Throws<InvalidDataException>(() => DowngradeHistoryRewriter.Rewrite(Table(12, [1, 2]), without, with));
        Assert.Throws<InvalidDataException>(() => DowngradeHistoryRewriter.Rewrite(Table(12, [1, 2]), with, without));
    }
}
