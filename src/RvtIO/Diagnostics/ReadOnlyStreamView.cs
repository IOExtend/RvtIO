using System.Runtime.ExceptionServices;

namespace RvtIO;

/// <summary>A read-only, seekable view of a stream the caller owns, which is all the compound file reader gets of it. OpenMcdf trims a
/// writable stream when it closes the root storage, which would shorten the caller's data and, on a damaged file, throw from the
/// close; through this view it has nothing to write. The view never closes the stream, and keeps the first failure of the stream
/// so that a read error is not reported as damage of the file.</summary>
internal sealed class ReadOnlyStreamView(Stream inner) : Stream
{
    private ExceptionDispatchInfo? _failure;

    /// <summary>Throws, as it was thrown, the first exception the stream raised, if it raised one. A stream that ends early is not a
    /// failure: it returns what it has, and that is damage of the file.</summary>
    public void ThrowStreamFailure() => _failure?.Throw();

    // An invalid argument is not a failure of the stream either: a damaged file can name a position that a stream such as a memory
    // buffer cannot take, and a file stream takes it and ends early, which is damage of the file in both cases.
    private void Record(Exception exception)
    {
        if (exception is not (EndOfStreamException or ArgumentException)) _failure ??= ExceptionDispatchInfo.Capture(exception);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            try { return inner.Length; }
            catch (Exception ex) { Record(ex); throw; }
        }
    }

    public override long Position
    {
        get
        {
            try { return inner.Position; }
            catch (Exception ex) { Record(ex); throw; }
        }
        set
        {
            try { inner.Position = value; }
            catch (Exception ex) { Record(ex); throw; }
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        try { return inner.Read(buffer, offset, count); }
        catch (Exception ex) { Record(ex); throw; }
    }

    public override int Read(Span<byte> buffer)
    {
        try { return inner.Read(buffer); }
        catch (Exception ex) { Record(ex); throw; }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        try { return inner.Seek(offset, origin); }
        catch (Exception ex) { Record(ex); throw; }
    }

    // Nothing is buffered here, and the caller's stream is not flushed: it is not ours to change.
    public override void Flush() { }

    public override void SetLength(long value) => throw new NotSupportedException("The Revit input stream is read only.");

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("The Revit input stream is read only.");

    public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException("The Revit input stream is read only.");
}
