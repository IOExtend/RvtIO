using IOScene;
using RvtIO.Meshes;
using RvtIO.Scene;
using Xunit;

namespace RvtIO.Tests;

/// <summary>Reading an RVT document, file or scene from a stream the caller owns: the stream is only read (never written to,
/// shortened or closed, whatever its access), the errors are the typed errors of a path read, and a failure of the stream itself
/// is not reported as a damaged file. The inputs are synthetic.</summary>
public sealed class StreamInputTests
{
    private static readonly RvtSceneOptions SceneOptions = new() { ModelKey = "model" };

    private static byte[] SyntheticRvt()
    {
        string path = Path.Combine(Path.GetTempPath(), "rvtio-stream-input-" + Guid.NewGuid().ToString("N") + ".rvt");
        try
        {
            CliExitCodeTests.WriteSyntheticRvt(path, [1, 2, 3]);
            return File.ReadAllBytes(path);
        }
        finally { File.Delete(path); }
    }

    /// <summary>The first half of a container: its streams are listed but the file ends before they do.</summary>
    private static byte[] Half(byte[] bytes) => bytes[..(bytes.Length / 2)];

    /// <summary>A file of a later release (wide element ids) with one project partition: its geometry is decoded only when the reader
    /// is asked for the Revit 2022 layout, so a read without that option gives another result.</summary>
    private static void WriteLaterRelease(string path)
    {
        byte[] schemaBytes = FormatsSchemaTests.ElemTableSchema(wideIds: true, markerId: 891);
        var schema = RvtIO.Parsing.TfSchemaReader.Parse(schemaBytes);
        ushort Id(string name) => (ushort)schema.Classes.Single(c => c.Name == name).Id;
        byte[] partition = ElementCensusTests.Partition(Id, 8, (false, [[], [], []]));
        CoverageCensusTests.WriteFile(path,
            ("Formats/Latest", RvtIO.Writing.PageCodec.EncodePadded(RvtIO.Writing.GzipMember.Write(schemaBytes))),
            ("Global/ElemTable", RvtIO.Writing.PageCodec.EncodePadded(RvtIO.Writing.GzipMember.Write(
                FormatsSchemaTests.Rows(wideIds: true, (0, 0, 0, 0, -1), (700, 500, 64, 1, 1))))),
            ("Global/History", RvtIO.Writing.PageCodec.EncodePadded(RvtIO.Writing.GzipMember.Write(new byte[64]))),
            ("BasicFileInfo", RvtWriterTests.BuildBasicFileInfoBytes(2024)),
            ("Partitions/1", partition));
    }

    private static object Read(string entry, Stream stream) => entry switch
    {
        "document" => RvtDocument.OpenStream(stream),
        "reader" => new RvtFileReader().ReadStream(stream),
        "scene" => RvtSceneReader.ReadStream(stream, SceneOptions),
        _ => throw new ArgumentOutOfRangeException(nameof(entry)),
    };

    private static readonly string[] Entries = ["document", "reader", "scene"];

    private static readonly string[] WritableKinds = ["fixed", "growing", "file"];

    public static TheoryData<string> EntryPoints()
    {
        var data = new TheoryData<string>();
        foreach (string entry in Entries) data.Add(entry);
        return data;
    }

    public static TheoryData<string, string> EntryPointsAndWritableStreams()
    {
        var data = new TheoryData<string, string>();
        foreach (string entry in Entries)
            foreach (string kind in WritableKinds) data.Add(entry, kind);
        return data;
    }

    public static TheoryData<string, long, string> EntryPointsAndStreamFailures()
    {
        var data = new TheoryData<string, long, string>();
        // 100 bytes in, the header is not read yet; the others fail while the streams of the file are read.
        foreach (string entry in Entries)
            foreach (long limit in new long[] { 100, 20_000, 45_000 })
                foreach (string failure in new[] { "io", "canceled", "disposed" }) data.Add(entry, limit, failure);
        return data;
    }

    /// <summary>A writable stream over the bytes, of the kinds callers pass: a fixed-size buffer (which refuses to be shortened), a
    /// growing buffer, and a file opened for reading and writing.</summary>
    private sealed class Subject : IDisposable
    {
        private readonly string? _path;
        private readonly MemoryStream? _memory;

        public Stream Stream { get; }

        private Subject(Stream stream, MemoryStream? memory, string? path) { Stream = stream; _memory = memory; _path = path; }

        public static Subject Create(string kind, byte[] bytes)
        {
            switch (kind)
            {
                case "fixed":
                    var fixedSize = new MemoryStream(bytes.ToArray());
                    return new Subject(fixedSize, fixedSize, null);
                case "growing":
                    var growing = new MemoryStream();
                    growing.Write(bytes);
                    return new Subject(growing, growing, null);
                case "file":
                    string path = Path.Combine(Path.GetTempPath(), "rvtio-stream-subject-" + Guid.NewGuid().ToString("N") + ".rvt");
                    File.WriteAllBytes(path, bytes);
                    return new Subject(new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite), null, path);
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        /// <summary>The bytes the stream holds now, read without the stream.</summary>
        public byte[] Snapshot()
        {
            if (_memory != null) return _memory.ToArray();
            using var reader = new FileStream(_path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var copy = new MemoryStream();
            reader.CopyTo(copy);
            return copy.ToArray();
        }

        public void Dispose()
        {
            Stream.Dispose();
            if (_path != null) File.Delete(_path);
        }
    }

    /// <summary>A seekable, read-only stream that fails when asked for more than <c>limit</c> bytes in all.</summary>
    private sealed class FailingStream(Stream inner, long limit, Exception failure) : Stream
    {
        private long _served;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_served >= limit) throw failure;
            int read = inner.Read(buffer, offset, count);
            _served += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static Exception Failure(string kind) => kind switch
    {
        "io" => new IOException("network reset"),
        "canceled" => new OperationCanceledException("the read was canceled"),
        "disposed" => new ObjectDisposedException("remote stream"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>A stream that serves at most <c>chunk</c> bytes a read, as a network stream may.</summary>
    private sealed class ShortReadStream(Stream inner, int chunk) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, chunk));
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Reports whether the caller's stream was closed, as a buffer or file stream would be by whatever disposes it.</summary>
    private sealed class DisposeTrackingStream(Stream inner) : Stream
    {
        public bool Disposed { get; private set; }
        public int FlushCalls { get; private set; }
        public override bool CanRead => !Disposed && inner.CanRead;
        public override bool CanSeek => !Disposed && inner.CanSeek;
        public override bool CanWrite => !Disposed && inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => FlushCalls++;
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private static string PathKind(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), "rvtio-stream-kind-" + Guid.NewGuid().ToString("N") + ".rvt");
        try
        {
            File.WriteAllBytes(path, bytes);
            return Assert.Throws<RvtInputException>(() => RvtContainerCheck.Verify(path)).Kind;
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [MemberData(nameof(EntryPointsAndWritableStreams))]
    public void ACutContainerOnAWritableStreamIsATypedInputError(string entry, string kind)
    {
        byte[] cut = Half(SyntheticRvt());
        using var subject = Subject.Create(kind, cut);

        var error = Assert.Throws<RvtInputException>(() => Read(entry, subject.Stream));

        Assert.Equal("truncated-or-damaged", error.Kind);
        Assert.Equal(PathKind(cut), error.Kind);
        Assert.True(subject.Stream.CanRead);
        Assert.Equal(cut, subject.Snapshot());
    }

    [Theory]
    [MemberData(nameof(EntryPointsAndWritableStreams))]
    public void BytesBehindTheContainerOfAWritableStreamAreKept(string entry, string kind)
    {
        byte[] bytes = [.. SyntheticRvt(), .. new byte[4096]];
        using var subject = Subject.Create(kind, bytes);

        Read(entry, subject.Stream);

        Assert.Equal(bytes.Length, subject.Stream.Length);
        Assert.Equal(bytes, subject.Snapshot());
    }

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public void NoStreamIsAnArgumentError(string entry)
    {
        var missing = Assert.Throws<ArgumentNullException>(() => Read(entry, null!));
        Assert.Equal("stream", missing.ParamName);

        using var forward = new ForwardOnlyStream(SyntheticRvt());
        var notSeekable = Assert.Throws<ArgumentException>(() => Read(entry, forward));
        Assert.Equal("stream", notSeekable.ParamName);

        var closed = new MemoryStream(SyntheticRvt());
        closed.Dispose();
        Assert.Equal("stream", Assert.Throws<ArgumentException>(() => Read(entry, closed)).ParamName);

        using var writeOnly = new WriteOnlyStream();
        Assert.Equal("stream", Assert.Throws<ArgumentException>(() => Read(entry, writeOnly)).ParamName);
    }

    private sealed class ForwardOnlyStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class WriteOnlyStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => 1;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }
    }

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public void AnEmptyStreamIsAnEmptyFile(string entry)
    {
        using var stream = new MemoryStream();

        var error = Assert.Throws<RvtInputException>(() => Read(entry, stream));

        Assert.Equal("empty-file", error.Kind);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void ASceneNeedsItsOptionsAndModelKey()
    {
        using var stream = new MemoryStream(SyntheticRvt());

        Assert.Equal("options", Assert.Throws<ArgumentNullException>(() => RvtSceneReader.ReadStream(stream, null!)).ParamName);
        Assert.Throws<ArgumentException>(() => RvtSceneReader.ReadStream(stream, new RvtSceneOptions { ModelKey = "" }));
        Assert.Throws<ArgumentException>(() => RvtSceneReader.ReadStream(stream, new RvtSceneOptions()));
        Assert.Equal(0, stream.Position);
    }

    [Theory]
    [MemberData(nameof(EntryPointsAndStreamFailures))]
    public void AFailureOfTheStreamIsThrownAsItWas(string entry, long limit, string kind)
    {
        var failure = Failure(kind);
        using var stream = new FailingStream(new MemoryStream(SyntheticRvt(), writable: false), limit, failure);

        var thrown = Assert.ThrowsAny<Exception>(() => Read(entry, stream));

        Assert.Same(failure, thrown);
        // The stack is the one of the stream that failed.
        Assert.Contains(nameof(FailingStream), thrown.StackTrace);
    }

    [Theory]
    [InlineData(100L)] [InlineData(20_000L)] [InlineData(45_000L)]
    public void TheEndOfAStreamIsDamageOfTheFileNotAFailureOfTheStream(long limit)
    {
        using var stream = new FailingStream(new MemoryStream(SyntheticRvt(), writable: false), limit, new EndOfStreamException("no more bytes"));

        var error = Assert.Throws<RvtInputException>(() => RvtDocument.OpenStream(stream));

        Assert.Contains(error.Kind, new[] { "not-a-compound-file", "truncated-or-damaged" });
    }

    [Theory]
    [InlineData(1)] [InlineData(7)] [InlineData(511)]
    public void AStreamThatServesFewBytesAReadReadsLikeTheFile(int chunk)
    {
        byte[] bytes = SyntheticRvt();
        using var whole = new MemoryStream(bytes, writable: false);
        var expected = RvtDocument.OpenStream(whole);

        using var trickle = new ShortReadStream(new MemoryStream(bytes, writable: false), chunk);
        var document = RvtDocument.OpenStream(trickle);

        Assert.Equal(expected.Elements.Select(e => e.Id), document.Elements.Select(e => e.Id));
        Assert.NotEmpty(document.Elements);

        // A container that ends early is still damaged when the stream serves it in pieces.
        byte[] cut = Half(bytes);
        using var cutTrickle = new ShortReadStream(new MemoryStream(cut, writable: false), chunk);
        Assert.Equal("truncated-or-damaged", Assert.Throws<RvtInputException>(() => RvtDocument.OpenStream(cutTrickle)).Kind);
    }

    [Theory]
    [InlineData("whole")] [InlineData("cut")] [InlineData("head")]
    public void TheCallersStreamIsNeverClosedOrFlushed(string input)
    {
        byte[] bytes = SyntheticRvt();
        byte[] content = input switch { "whole" => bytes, "cut" => Half(bytes), _ => bytes[..100] };
        foreach (bool writable in new[] { true, false })
        {
            var memory = writable ? new MemoryStream(content.ToArray()) : new MemoryStream(content, writable: false);
            using var stream = new DisposeTrackingStream(memory);

            // The cut file fails while its streams are walked, the head before the compound file is open.
            if (input == "whole") RvtDocument.OpenStream(stream);
            else Assert.Throws<RvtInputException>(() => RvtDocument.OpenStream(stream));

            Assert.False(stream.Disposed);
            Assert.True(stream.CanRead);
            Assert.Equal(0, stream.FlushCalls);
            Assert.Equal(content, memory.ToArray());
        }
    }

    [Fact]
    public void AStreamIsReadFromItsBeginningWhateverItsPosition()
    {
        byte[] bytes = SyntheticRvt();
        using var fresh = new MemoryStream(bytes);
        var expected = RvtDocument.OpenStream(fresh);

        foreach (long position in new long[] { 100, bytes.Length / 2, bytes.Length })
        {
            using var stream = new MemoryStream(bytes) { Position = position };
            var document = RvtDocument.OpenStream(stream);
            Assert.Equal(expected.Elements.Select(e => e.Id), document.Elements.Select(e => e.Id));
        }
    }

    [Fact]
    public void ThePathCheckKeepsReportingAFailingReadAsAnInputError()
    {
        var failure = new IOException("disk error");
        using var stream = new FailingStream(new MemoryStream(SyntheticRvt(), writable: false), 20_000, failure);

        var error = Assert.Throws<RvtInputException>(() => RvtContainerCheck.OpenVerified(stream, "file.rvt", surfaceStreamFailures: false));

        Assert.Equal("truncated-or-damaged", error.Kind);
        Assert.Same(failure, error.InnerException);
    }

    [Fact]
    public void StreamReadsUseTheReaderOptionsOfPathReads()
    {
        string path = Path.Combine(Path.GetTempPath(), "rvtio-stream-options-" + Guid.NewGuid().ToString("N") + ".rvt");
        try
        {
            WriteLaterRelease(path);
            var options = new RvtReaderOptions { R22Layout = true };

            // The input depends on the option: without the Revit 2022 layout the geometry of the file is not decoded.
            Assert.True(ElementMeshes.Extract(RvtDocument.Open(path)).GeometryUndecoded);

            var fromPath = RvtDocument.Open(path, options);
            Assert.NotEmpty(fromPath.Elements);
            Assert.False(ElementMeshes.Extract(fromPath).GeometryUndecoded);
            using var stream = File.OpenRead(path);

            var fromStream = RvtDocument.OpenStream(stream, options);
            Assert.Equal(fromPath.Elements.Select(e => e.Id), fromStream.Elements.Select(e => e.Id));
            Assert.Equal(fromPath.RawData.WideLayoutNotRewritten, fromStream.RawData.WideLayoutNotRewritten);
            Assert.False(ElementMeshes.Extract(fromStream).GeometryUndecoded);
            Assert.Null(fromStream.SourcePath);

            var info = new RvtFileReader(options).ReadStream(stream);
            Assert.Equal(new RvtFileReader(options).Read(path).Elements.Select(e => e.Id), info.Elements.Select(e => e.Id));
            Assert.False(info.WideLayoutNotRewritten);

            var scenePath = RvtSceneReader.Read(path, SceneOptions);
            var sceneStream = RvtSceneReader.ReadStream(stream, SceneOptions);
            Assert.Equal(SceneJson.Write(scenePath.Document), SceneJson.Write(sceneStream.Document));
            Assert.Equal(scenePath.Diagnostics, sceneStream.Diagnostics);
            Assert.DoesNotContain(sceneStream.Diagnostics, d => d.Code == "geometry-undecoded");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ADocumentFromAStreamHasNoSourcePathAndCannotBeSaved()
    {
        using var stream = new MemoryStream(SyntheticRvt());
        var document = RvtDocument.OpenStream(stream);

        Assert.Null(document.SourcePath);
        Assert.Throws<InvalidOperationException>(() => document.SaveAs(Path.Combine(Path.GetTempPath(), "never-written.rvt")));
    }

    [Fact]
    public void TheViewOfTheStreamOnlyReads()
    {
        byte[] bytes = [1, 2, 3, 4, 5, 6];
        var memory = new MemoryStream(bytes.ToArray());
        using var tracked = new DisposeTrackingStream(memory);
        var view = new ReadOnlyStreamView(tracked);

        Assert.True(view.CanRead);
        Assert.True(view.CanSeek);
        Assert.False(view.CanWrite);
        Assert.Equal(6, view.Length);
        view.Position = 2;
        Assert.Equal(2, view.Position);
        Assert.Equal(3, view.ReadByte());
        Assert.Equal(5, view.Seek(-1, SeekOrigin.End));
        var span = new byte[4];
        Assert.Equal(1, view.Read(span.AsSpan()));
        Assert.Equal(6, span[0]);
        Assert.Throws<NotSupportedException>(() => view.Write([9], 0, 1));
        Assert.Throws<NotSupportedException>(() => view.Write(new byte[] { 9 }.AsSpan()));
        Assert.Throws<NotSupportedException>(() => view.SetLength(1));
        view.Flush();
        view.Dispose();

        Assert.False(tracked.Disposed);
        Assert.Equal(0, tracked.FlushCalls);
        Assert.Equal(bytes, memory.ToArray());
    }
}
