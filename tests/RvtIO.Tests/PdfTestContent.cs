using System.IO.Compression;
using System.Text;
using RvtIO.Pdf;

namespace RvtIO.Tests;

/// <summary>Reads a written PDF back for assertions: the raw object dictionaries plus every stream, Flate-decoded when
/// the writer compressed it.</summary>
internal static class PdfTestContent
{
    public static string Read(PdfDocument document)
    {
        using var stream = new MemoryStream();
        document.Save(stream);
        return Read(stream.ToArray());
    }

    public static string Read(byte[] pdf)
    {
        string raw = Encoding.Latin1.GetString(pdf);
        var text = new StringBuilder(raw);
        int at = 0;
        while ((at = raw.IndexOf("stream\n", at, StringComparison.Ordinal)) >= 0)
        {
            int start = at + 7;
            int end = raw.IndexOf("\nendstream", start, StringComparison.Ordinal);
            if (end < 0) break;
            byte[] data = pdf[start..end];
            try
            {
                using var input = new MemoryStream(data);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                zlib.CopyTo(output);
                text.Append('\n').Append(Encoding.Latin1.GetString(output.ToArray()));
            }
            catch (InvalidDataException)
            {
                // Not a Flate stream: its raw bytes are already part of the raw text.
            }
            at = end + 10;
        }
        return text.ToString();
    }
}
