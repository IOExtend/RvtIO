using System.Buffers.Binary;
using System.Text;

namespace RvtIO.Pdf;

/// <summary>
/// A TrueType font embedded in a PDF as a Type0 font with Identity-H encoding, so that text in any script the font
/// covers (Cyrillic, for one) is written as real, selectable text. The whole font file is embedded; the widths of the
/// placed glyphs and a ToUnicode map are written for the characters the document uses.
/// </summary>
public sealed class PdfTrueTypeFont
{
    private readonly byte[] _file;
    private readonly Dictionary<int, int> _glyphByCode = new();
    private readonly int[] _advances;
    private readonly int _unitsPerEm;

    /// <summary>Parses a TrueType font file.</summary>
    /// <param name="path">The path of a .ttf file.</param>
    /// <exception cref="NotSupportedException">The file is not a TrueType font with a Unicode cmap.</exception>
    public PdfTrueTypeFont(string path) : this(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path)) { }

    /// <summary>Parses TrueType font bytes.</summary>
    /// <param name="file">The font file.</param>
    /// <param name="name">The PostScript base name written into the PDF.</param>
    /// <exception cref="NotSupportedException">The file is not a TrueType font with a Unicode cmap,
    /// or its tables are truncated or corrupt.</exception>
    public PdfTrueTypeFont(byte[] file, string name)
    {
        ArgumentNullException.ThrowIfNull(file);
        _file = file;
        Name = string.IsNullOrWhiteSpace(name) ? "RvtIOFont" : name;
        try
        {
            if (file.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(file) != 0x00010000)
                throw new NotSupportedException("Only a TrueType font (0x00010000) is supported.");

            var tables = new Dictionary<string, (int Offset, int Length)>(StringComparer.Ordinal);
            int count = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(4));
            for (int i = 0; i < count; i++)
            {
                int at = 12 + i * 16;
                Need(file, at, 16, "table directory");
                string tag = Encoding.ASCII.GetString(file, at, 4);
                int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at + 8));
                int length = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at + 12));
                Need(file, offset, length, tag);
                tables[tag] = (offset, length);
            }
            if (!tables.TryGetValue("head", out var head) || !tables.TryGetValue("hhea", out var hhea)
                || !tables.TryGetValue("maxp", out var maxp) || !tables.TryGetValue("hmtx", out var hmtx)
                || !tables.TryGetValue("cmap", out var cmap))
                throw new NotSupportedException("The font lacks head, hhea, maxp, hmtx or cmap.");
            Need(file, head.Offset, 44, "head");
            Need(file, hhea.Offset, 36, "hhea");
            Need(file, maxp.Offset, 6, "maxp");
            Need(file, cmap.Offset, 4, "cmap");

            _unitsPerEm = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(head.Offset + 18));
            if (_unitsPerEm == 0) throw new NotSupportedException("The font declares no units per em.");
            XMin = BinaryPrimitives.ReadInt16BigEndian(file.AsSpan(head.Offset + 36));
            YMin = BinaryPrimitives.ReadInt16BigEndian(file.AsSpan(head.Offset + 38));
            XMax = BinaryPrimitives.ReadInt16BigEndian(file.AsSpan(head.Offset + 40));
            YMax = BinaryPrimitives.ReadInt16BigEndian(file.AsSpan(head.Offset + 42));

            Ascent = BinaryPrimitives.ReadInt16BigEndian(file.AsSpan(hhea.Offset + 4));
            Descent = BinaryPrimitives.ReadInt16BigEndian(file.AsSpan(hhea.Offset + 6));
            int metrics = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(hhea.Offset + 34));
            int glyphs = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(maxp.Offset + 4));
            if (metrics < 1) throw new NotSupportedException("The font has no horizontal metrics.");
            Need(file, hmtx.Offset, (long)(metrics - 1) * 4 + 2, "hmtx");
            _advances = new int[Math.Max(glyphs, 1)];
            for (int gid = 0; gid < glyphs; gid++)
            {
                int at = hmtx.Offset + Math.Min(gid, metrics - 1) * 4;
                _advances[gid] = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(at));
            }

            ReadCmap(file, cmap);
            if (_glyphByCode.Count == 0) throw new NotSupportedException("The font has no Unicode cmap for the BMP.");
        }
        catch (ArgumentOutOfRangeException exception)
        {
            // Every out-of-range table read is a truncated or corrupt font, reported as
            // unsupported so a caller trying candidates falls through to the next one.
            throw new NotSupportedException("Truncated or corrupt TrueType font.", exception);
        }
    }

    /// <summary>Requires <c>[offset, offset + length)</c> to lie inside the file.</summary>
    /// <exception cref="NotSupportedException">The font is truncated: the table does not fit.</exception>
    private static void Need(byte[] file, long offset, long length, string what)
    {
        if (offset < 0 || length < 0 || offset + length > file.Length)
            throw new NotSupportedException($"Truncated TrueType font: {what} runs past the end of the file.");
    }

    /// <summary>The PostScript base name of the font.</summary>
    public string Name { get; }

    /// <summary>Units per em of the font.</summary>
    public int UnitsPerEm => _unitsPerEm;

    /// <summary>The glyph id of a character, 0 (the notdef glyph) when the font has none.</summary>
    /// <param name="code">A UTF-16 code unit.</param>
    public int GlyphOf(char code) => _glyphByCode.TryGetValue(code, out int gid) ? gid : 0;

    /// <summary>The width of a glyph in the units of the font (1/UnitsPerEm of the em).</summary>
    /// <param name="glyph">The glyph id.</param>
    public int AdvanceOf(int glyph) => glyph >= 0 && glyph < _advances.Length ? _advances[glyph] : 0;

    internal int XMin { get; }
    internal int YMin { get; }
    internal int XMax { get; }
    internal int YMax { get; }
    internal int Ascent { get; }
    internal int Descent { get; }

    /// <summary>The font file, embedded whole.</summary>
    internal byte[] File_ => _file;

    private void ReadCmap(byte[] file, (int Offset, int Length) cmap)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(cmap.Offset + 2));
        int best = -1, bestScore = -1;
        for (int i = 0; i < count; i++)
        {
            int at = cmap.Offset + 4 + i * 8;
            Need(file, at, 8, "cmap encoding record");
            int platform = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(at));
            int encoding = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(at + 2));
            int offset = cmap.Offset + (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at + 4));
            Need(file, offset, 2, "cmap subtable");
            int format = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(offset));
            int score = (platform, encoding, format) switch
            {
                (3, 10, 12) => 5,
                (3, 1, 4) => 4,
                (0, _, 12) => 3,
                (0, _, 4) => 2,
                _ => 0,
            };
            if (score > bestScore) { bestScore = score; best = offset; }
        }
        if (best < 0) return;
        int bestFormat = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(best));
        if (bestFormat == 4) ReadFormat4(file, best);
        else if (bestFormat == 12) ReadFormat12(file, best);
    }

    private void ReadFormat4(byte[] file, int at)
    {
        Need(file, at, 8, "cmap format 4 header");
        int segments = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(at + 6)) / 2;
        Need(file, at, 16L + segments * 8L, "cmap format 4");
        int endCodes = at + 14;
        int startCodes = endCodes + segments * 2 + 2;
        int deltas = startCodes + segments * 2;
        int rangeOffsets = deltas + segments * 2;
        for (int seg = 0; seg < segments; seg++)
        {
            int end = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(endCodes + seg * 2));
            int start = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(startCodes + seg * 2));
            int delta = BinaryPrimitives.ReadInt16BigEndian(file.AsSpan(deltas + seg * 2));
            int rangeOffset = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(rangeOffsets + seg * 2));
            for (int code = start; code <= end && code <= 0xFFFF; code++)
            {
                if (code == 0xFFFF) break;
                int glyph;
                if (rangeOffset == 0)
                {
                    glyph = (code + delta) & 0xFFFF;
                }
                else
                {
                    int address = rangeOffsets + seg * 2 + rangeOffset + (code - start) * 2;
                    if (address + 2 > file.Length) continue;
                    glyph = BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(address));
                    if (glyph != 0) glyph = (glyph + delta) & 0xFFFF;
                }
                if (glyph != 0 && !_glyphByCode.ContainsKey(code)) _glyphByCode[code] = glyph;
            }
        }
    }

    private void ReadFormat12(byte[] file, int at)
    {
        Need(file, at, 16, "cmap format 12 header");
        long groups = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at + 12));
        if (groups < 0 || 16 + groups * 12 > file.Length - (long)at)
            throw new NotSupportedException("Truncated TrueType font: cmap format 12 lists more groups than fit.");
        for (long g = 0; g < groups; g++)
        {
            int entry = at + 16 + (int)(g * 12);
            int start = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(entry));
            int end = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(entry + 4));
            int glyph = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(entry + 8));
            for (int code = start; code <= end && code <= 0xFFFF; code++)
            {
                if (_glyphByCode.ContainsKey(code)) continue;
                _glyphByCode[code] = glyph + (code - start);
            }
        }
    }
}
