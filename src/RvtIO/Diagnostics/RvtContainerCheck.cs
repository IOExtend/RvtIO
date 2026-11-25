using System.Globalization;
using OpenMcdf;

namespace RvtIO;

/// <summary>Reads every stream of the compound file to its declared end, so that a cut or damaged file is reported as one input
/// error before the decoders run. Without it the metadata readers swallow a stream that ends early and the run goes on with less data.</summary>
public static class RvtContainerCheck
{
    /// <exception cref="RvtInputException">The file is missing, empty, not a compound file, or a stream ends before its declared length.</exception>
    /// <exception cref="RvtLimitException">A stream is longer than the platform allows in one array.</exception>
    public static void Verify(string path)
    {
        FileInfo info;
        try { info = new FileInfo(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new RvtInputException("cannot-open", $"'{path}' is not a usable file path.", ex); }
        if (!info.Exists) throw new RvtInputException("cannot-open", $"'{info.Name}' does not exist.");
        if (info.Length == 0) throw new RvtInputException("empty-file", $"'{info.Name}' is empty.");

        // An operating system refusal (sharing, permission) is "cannot-open"; what OpenMcdf rejects after this is the format.
        // The stream is opened here, and closed here whatever OpenMcdf does: OpenRead(path) leaves its file open when it throws.
        FileStream file;
        try { file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new RvtInputException("cannot-open", $"'{info.Name}' cannot be opened ({ex.GetType().Name}); the file may be in use or not readable.", ex); }
        using var opened = file;
        using var root = OpenVerified(file, info.Name, out _, surfaceStreamFailures: false);
    }

    internal static RootStorage OpenVerified(Stream stream, string name, out ReadOnlyStreamView view,
        bool surfaceStreamFailures = true, bool streamPartitions = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException("The Revit input stream must be readable and seekable.", nameof(stream));
        if (stream.Length == 0)
            throw new RvtInputException("empty-file", $"'{name}' is empty.");
        stream.Position = 0;

        // OpenMcdf trims a writable stream when it closes the root, so it gets a read-only view: the caller's stream is neither
        // changed nor closed, whatever its access. A failure of the stream itself is not damage of the file and is thrown as it was
        // (when asked to: the file path keeps reporting it as an input error).
        view = new ReadOnlyStreamView(stream);
        RootStorage root;
        try { root = RootStorage.Open(view, StorageModeFlags.LeaveOpen); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (surfaceStreamFailures) view.ThrowStreamFailure();
            throw new RvtInputException("not-a-compound-file", $"'{name}' is not a Revit file: its compound file header or directory cannot be read ({ex.Message}). The file may be cut or damaged.", ex);
        }

        try
        {
            var buffer = new byte[1 << 20];
            int visited = 0;
            Walk(root, "", buffer, ref visited, streamPartitions);
            return root;
        }
        catch (Exception ex)
        {
            root.Dispose();
            if (ex is OutOfMemoryException or RvtLimitException) throw;
            if (surfaceStreamFailures) view.ThrowStreamFailure();
            string where = ex is EndOfStreamException eos && eos.Data["stream"] is string entryName ? $" in stream '{entryName}'" : "";
            throw new RvtInputException("truncated-or-damaged",
                $"'{name}' is cut or damaged: the compound file ends early{where} ({ex.Message}).", ex);
        }
    }

    // A Revit file nests storages only a few levels deep; a damaged directory can loop back on itself, which would overflow the stack.
    private const int MaxDepth = 64;
    // Bounds the total work when a damaged directory points several entries at the same children.
    private const int MaxEntries = 200_000;

    internal static RvtLimitException ArrayLengthLimit(string name, long length) =>
        new(new ReaderLimitHit(name, "array-length", Array.MaxLength, Raisable: false,
            $"stream '{name}' is {length.ToString("N0", CultureInfo.InvariantCulture)} bytes, more than the {Array.MaxLength.ToString("N0", CultureInfo.InvariantCulture)} bytes the platform holds in one array"));

    private static void Walk(Storage storage, string prefix, byte[] buffer, ref int visited, bool streamPartitions, int depth = 0)
    {
        if (depth > MaxDepth) throw new InvalidDataException($"storage nesting deeper than {MaxDepth} levels at '{prefix}'");
        foreach (var entry in storage.EnumerateEntries())
        {
            if (++visited > MaxEntries) throw new InvalidDataException($"more than {MaxEntries} directory entries");
            string name = prefix + entry.Name;
            if (entry.Type == EntryType.Storage)
            {
                Walk(storage.OpenStorage(entry.Name), name + "/", buffer, ref visited, streamPartitions, depth + 1);
                continue;
            }
            if (entry.Type != EntryType.Stream) continue;
            if (entry.Length > Array.MaxLength
                && !(streamPartitions && depth == 1 && prefix.Equals("Partitions/", StringComparison.OrdinalIgnoreCase)))
                throw ArrayLengthLimit(name, entry.Length);
            using var stream = storage.OpenStream(entry.Name);
            long total = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) total += read;
            if (total != entry.Length)
            {
                var eos = new EndOfStreamException($"stream '{name}' holds {total.ToString("N0", CultureInfo.InvariantCulture)} of its {entry.Length.ToString("N0", CultureInfo.InvariantCulture)} bytes");
                eos.Data["stream"] = name;
                throw eos;
            }
        }
    }
}
