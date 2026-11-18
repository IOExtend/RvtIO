namespace RvtIO;

/// <summary>The input cannot be read at all (missing, empty, not a compound file, or a stream ends before its declared length).</summary>
public sealed class RvtInputException : Exception
{
    /// <summary>Stable code: <c>cannot-open</c>, <c>empty-file</c>, <c>not-a-compound-file</c> or <c>truncated-or-damaged</c>.</summary>
    public string Kind { get; }

    /// <summary>Creates the exception with its stable <paramref name="kind"/> code.</summary>
    public RvtInputException(string kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;
}

/// <summary>A stream exceeds a limit of the reader that cannot be read around (see <see cref="RvtReaderOptions.RaiseLimits"/>).</summary>
public sealed class RvtLimitException : Exception
{
    /// <summary>The limit that was exceeded.</summary>
    public ReaderLimitHit Hit { get; }

    /// <summary>Creates the exception for a limit hit.</summary>
    public RvtLimitException(ReaderLimitHit hit) : base(hit.Detail) => Hit = hit;
}

/// <summary>A limit of the reader that cut a stream.</summary>
/// <param name="Stream">The stream, as storage/name.</param>
/// <param name="Limit">Stable limit code, see docs/export-report.md.</param>
/// <param name="Value">The value of the limit in force.</param>
/// <param name="Raisable">True when <see cref="RvtReaderOptions.RaiseLimits"/> raises this limit.</param>
public sealed record ReaderLimitHit(string Stream, string Limit, long Value, bool Raisable, string Detail);

/// <summary>How much of one partition stream decoded. Members are the gzip members the stream is cut into.</summary>
public sealed record StreamRecord(string Name, long RawBytes, int Members, int Decoded, int Failed, int Recovered, int Unverified)
{
    /// <summary>The inflated byte length of the decoded members (0 when nothing decoded).</summary>
    public long UnpackedBytes { get; init; }

    /// <summary>Creates a stream decode record including the inflated byte length of its decoded members.</summary>
    public StreamRecord(string Name, long RawBytes, int Members, int Decoded, int Failed, int Recovered, int Unverified, long UnpackedBytes)
        : this(Name, RawBytes, Members, Decoded, Failed, Recovered, Unverified)
    {
        this.UnpackedBytes = UnpackedBytes;
    }

    /// <summary>Deconstructs the stream decode record including the inflated byte length.</summary>
    public void Deconstruct(out string Name, out long RawBytes, out int Members, out int Decoded, out int Failed, out int Recovered, out int Unverified, out long UnpackedBytes)
    {
        Name = this.Name;
        RawBytes = this.RawBytes;
        Members = this.Members;
        Decoded = this.Decoded;
        Failed = this.Failed;
        Recovered = this.Recovered;
        Unverified = this.Unverified;
        UnpackedBytes = this.UnpackedBytes;
    }
}

/// <summary>What the reader collects while it reads a file (<see cref="RvtReaderOptions.Diagnostics"/>).</summary>
public sealed class RvtReadDiagnostics
{
    /// <summary>The partition streams read, in the order they were read.</summary>
    public List<StreamRecord> Streams { get; } = new();

    /// <summary>The streams cut by a limit of the reader.</summary>
    public List<ReaderLimitHit> LimitHits { get; } = new();
}
