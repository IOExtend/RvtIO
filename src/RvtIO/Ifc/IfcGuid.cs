using System.Security.Cryptography;
using System.Text;

namespace RvtIO.Ifc;

/// <summary>
/// IFC GlobalId encoding: 128-bit GUID to 22-character base64-like string.
/// Uses IFC-specific alphabet (0-9, A-Z, a-z, _, $).
/// Algorithm from buildingSMART IFC specification.
/// </summary>
internal static class IfcGuid
{
    private const string Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$";

    /// <summary>Convert a .NET Guid to IFC GlobalId (22 chars).</summary>
    public static string ToIfcGuid(Guid guid)
    {
        var b = guid.ToByteArray();
        // Rearrange .NET byte order to UUID big-endian
        var u = new byte[16];
        u[0] = b[3]; u[1] = b[2]; u[2] = b[1]; u[3] = b[0];
        u[4] = b[5]; u[5] = b[4];
        u[6] = b[7]; u[7] = b[6];
        Array.Copy(b, 8, u, 8, 8);

        // 128 bits -> 22 base-64 chars (22*6 = 132, so 4 padding bits)
        // Split into two 64-bit halves
        ulong hi = 0, lo = 0;
        for (int i = 0; i < 8; i++) hi = (hi << 8) | u[i];
        for (int i = 8; i < 16; i++) lo = (lo << 8) | u[i];

        var result = new char[22];
        // Char 0: top 2 bits (padded to 6)
        result[0] = Chars[(int)(hi >> 62) & 0x3F];
        // Chars 1-10: next 60 bits from hi
        result[1] = Chars[(int)(hi >> 56) & 0x3F];
        result[2] = Chars[(int)(hi >> 50) & 0x3F];
        result[3] = Chars[(int)(hi >> 44) & 0x3F];
        result[4] = Chars[(int)(hi >> 38) & 0x3F];
        result[5] = Chars[(int)(hi >> 32) & 0x3F];
        result[6] = Chars[(int)(hi >> 26) & 0x3F];
        result[7] = Chars[(int)(hi >> 20) & 0x3F];
        result[8] = Chars[(int)(hi >> 14) & 0x3F];
        result[9] = Chars[(int)(hi >> 8) & 0x3F];
        result[10] = Chars[(int)(hi >> 2) & 0x3F];
        // Char 11: 2 bits from hi + 4 bits from lo
        result[11] = Chars[(int)(((hi & 0x03) << 4) | ((lo >> 60) & 0x0F))];
        // Chars 12-21: remaining 60 bits from lo
        result[12] = Chars[(int)(lo >> 54) & 0x3F];
        result[13] = Chars[(int)(lo >> 48) & 0x3F];
        result[14] = Chars[(int)(lo >> 42) & 0x3F];
        result[15] = Chars[(int)(lo >> 36) & 0x3F];
        result[16] = Chars[(int)(lo >> 30) & 0x3F];
        result[17] = Chars[(int)(lo >> 24) & 0x3F];
        result[18] = Chars[(int)(lo >> 18) & 0x3F];
        result[19] = Chars[(int)(lo >> 12) & 0x3F];
        result[20] = Chars[(int)(lo >> 6) & 0x3F];
        result[21] = Chars[(int)(lo) & 0x3F];

        return new string(result);
    }

    /// <summary>The .NET Guid of a 22-character IFC GlobalId (inverse of
    /// <see cref="ToIfcGuid"/>); null when the text is not one.</summary>
    public static Guid? Parse(string globalId)
    {
        if (globalId.Length != 22) return null;
        var value = System.Numerics.BigInteger.Zero;
        foreach (char c in globalId)
        {
            int digit = Chars.IndexOf(c);
            if (digit < 0) return null;
            value = value * 64 + digit;
        }
        if (value >> 128 != 0) return null;
        var u = new byte[16];
        for (int i = 15; i >= 0; i--)
        {
            u[i] = (byte)(value & 0xFF);
            value >>= 8;
        }
        var b = new byte[16];
        b[0] = u[3]; b[1] = u[2]; b[2] = u[1]; b[3] = u[0];
        b[4] = u[5]; b[5] = u[4];
        b[6] = u[7]; b[7] = u[6];
        Array.Copy(u, 8, b, 8, 8);
        return new Guid(b);
    }

    /// <summary>Generate a deterministic IFC GUID from element ID and a namespace string.</summary>
    // The default namespace seed is fixed: it keeps the generated GUIDs stable across releases.
    public static string FromElementId(int elemId, string ns = "RevitReader")
        => FromElementId((long)elemId, ns);

    /// <summary>Generate a deterministic fallback IFC GUID from the complete source element ID.</summary>
    public static string FromElementId(long elemId, string ns = "RevitReader")
    {
        var bytes = Encoding.UTF8.GetBytes(FormattableString.Invariant($"{ns}:{elemId}"));
        var hash = MD5.HashData(bytes);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return ToIfcGuid(new Guid(hash));
    }

    /// <summary>A deterministic IFC GUID for a hash key: the MD5 of the key's
    /// UTF-8 bytes read as a .NET Guid, in IFC form.</summary>
    public static string FromMd5(string key) =>
        ToIfcGuid(new Guid(MD5.HashData(Encoding.UTF8.GetBytes(key))));

    /// <summary>Generate a deterministic IFC GUID from a name string.</summary>
    public static string FromName(string name)
    {
        var bytes = Encoding.UTF8.GetBytes($"RevitReader:name:{name}");
        var hash = MD5.HashData(bytes);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return ToIfcGuid(new Guid(hash));
    }
}
