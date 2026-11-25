using RvtIO.Internal;

namespace RvtIO.Parsing;

/// <summary>
/// Scans decompressed partition chunks for element-name table entries.
/// Each named element has a companion "name holder" whose block is:
///   [01 00 00 00][holderEid:4][FF x28][00 x0..8][01 00 00 00][targetId:4]
///   [nameLen:4][UTF-16LE name][catId:4][trailing:4...]
/// The name belongs to targetId. For family symbols the trailing field is the holder id of the
/// parent family's name entry (the symbol to family chain); family entries carry a 16-byte GUID there.
/// </summary>
internal static class NameTableScanner
{
    /// <summary>Marker dword check. Clean form: 01 00 00 00. A second on-disk variant
    /// (observed in superseded/journal regions) has high bytes FF-masked: 04 FF FF FF.</summary>
    private static bool IsMarker(byte[] d, int p)
        => (d[p] == 0x01 && d[p + 1] == 0x00 && d[p + 2] == 0x00 && d[p + 3] == 0x00)
        || (d[p] == 0x04 && d[p + 1] == 0xFF && d[p + 2] == 0xFF && d[p + 3] == 0xFF);

    public static void Scan(byte[] d, ParsingState state)
    {
        int n = d.Length;
        // Anchor on the SECOND marker: [marker][targetId:4][nameLen:4][UTF-16 name][catId:4][trailing:4]
        // then validate backwards: a mostly-FF run of 28+ bytes preceded by [marker][holderEid].
        const int maxNameLength = 500;
        int last = n - 16;
        Span<char> chars = stackalloc char[maxNameLength];
        for (int p = 40; p <= last; p++)
        {
            int next = d.AsSpan(p, last - p + 1).IndexOfAny((byte)1, (byte)4);
            if (next < 0) break;
            p += next;
            if (!IsMarker(d, p)) continue;

            uint targetId = BitConverter.ToUInt32(d, p + 4);
            if (targetId == 0 || targetId > 50_000_000) continue;

            // nameLen: clean form [len:u16][00 00]; FF-masked form [len:u8][FF FF FF]
            int nameLen;
            if (d[p + 10] == 0x00 && d[p + 11] == 0x00)
                nameLen = d[p + 8] | (d[p + 9] << 8);
            else if (d[p + 9] == 0xFF && d[p + 10] == 0xFF && d[p + 11] == 0xFF)
                nameLen = d[p + 8];
            else
                continue;
            if (nameLen < 1 || nameLen > maxNameLength || p + 12 + nameLen * 2 + 8 > n) continue;

            // backward validation: an FF run of >=28 bytes (after skipping up to 8 zero
            // bytes; the FF-masked variant has a slightly longer run with isolated
            // non-FF bytes inside), preceded by [marker][holderEid]
            int b = p;
            int zskip = 0;
            while (zskip < 9 && b - 1 >= 0 && d[b - 1] == 0x00) { b--; zskip++; }
            int q = b;
            int run = 0, interlopers = 0;
            while (q - 1 >= 8 && run < 44)
            {
                if (d[q - 1] == 0xFF) { q--; run++; continue; }
                if (interlopers < 2 && run > 0 && q - 2 >= 8 && d[q - 2] == 0xFF)
                { q--; run++; interlopers++; continue; }
                break;
            }
            if (run < 28) continue;
            int holderPos = q - 4;
            uint holderEid = BitConverter.ToUInt32(d, holderPos);
            if (holderEid == 0 || holderEid > 50_000_000) continue;
            if (holderPos - 4 >= 0 && !IsMarker(d, holderPos - 4)) continue;

            // decode name; FF-masked variant corrupts high bytes of BMP-Latin chars
            bool ok = true;
            for (int k = 0; k < nameLen; k++)
            {
                char c = (char)BitConverter.ToUInt16(d, p + 12 + k * 2);
                if ((c & 0xFF00) == 0xFF00) c = (char)(c & 0x00FF);
                if (c == '\0' || (c < 0x20 && c != '\t')) { ok = false; break; }
                chars[k] = c;
            }
            if (!ok) continue;

            int end = p + 12 + nameLen * 2;
            int catId = BitConverter.ToInt32(d, end);
            // category id sanity: BuiltInCategory range (acts as the entry checksum)
            if (catId < -2_200_000 || catId > -2_000_000) continue;
            uint trailing = BitConverter.ToUInt32(d, end + 4);

            // first entry wins (duplicates across incremental-save regions are identical)
            if (!state.ElementNameTable.ContainsKey(targetId))
            {
                state.ElementNameTable[targetId] = new string(chars[..nameLen]);
                state.NameEntryCategory[targetId] = catId;
                if (trailing > 0 && trailing < 50_000_000)
                    state.NameEntryParentHolder[targetId] = trailing;
            }
            if (holderEid > 0 && holderEid < 50_000_000 && !state.NameHolderTarget.ContainsKey(holderEid))
                state.NameHolderTarget[holderEid] = targetId;

            p = end;
        }
    }
}
