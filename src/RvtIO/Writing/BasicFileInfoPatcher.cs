using System.Buffers.Binary;
using System.Text;

namespace RvtIO.Writing;

/// <summary>
/// Patches the raw bytes of the <c>BasicFileInfo</c> stream when a file is downgraded: every UTF-16LE
/// occurrence of the source release year becomes the target year, the stream version dword and the build
/// string are taken from the profile file, and the length-prefixed UTF-16 build value is re-written with
/// its own length prefix (so a profile build of another length is accepted and the following values shift).
/// The key-value text block of the stream is free text and is patched in place.
/// </summary>
internal static class BasicFileInfoPatcher
{
    internal static byte[] Patch(byte[] data, int sourceRelease, int targetRelease, byte[]? profileData = null)
    {
        string from = sourceRelease.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string to = targetRelease.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (from.Length != 4 || to.Length != 4) throw new ArgumentException("Releases must be four-digit years.");
        byte[] patched = from == to ? [.. data] : ReplaceAll(data, Encoding.Unicode.GetBytes(from), Encoding.Unicode.GetBytes(to), required: true);

        if (profileData is not null)
        {
            var profileFields = Parsing.BasicFileInfoReader.Parse(profileData);
            // Stream version: the first dword of the stream.
            if (profileFields.TryGetValue("StreamVersion", out string? streamVersion) && int.TryParse(streamVersion, out int streamVersionValue))
            {
                if (patched.Length < 4) throw new InvalidDataException("BasicFileInfo is cut inside its stream version.");
                BinaryPrimitives.WriteInt32LittleEndian(patched.AsSpan(0, 4), streamVersionValue);
            }

            string? sourceBuild = Parsing.BasicFileInfoReader.Parse(data).GetValueOrDefault("Build");
            if (profileFields.TryGetValue("Build", out string? profileBuild) && sourceBuild is not null && profileBuild != sourceBuild)
            {
                // The standalone value: [i32 length][UTF-16 chars]; the length prefix is rewritten with it.
                patched = ReplaceLengthPrefixed(patched, sourceBuild, profileBuild);
                // Any occurrence in the trailing key-value text block.
                patched = ReplaceAll(patched, Encoding.Unicode.GetBytes(sourceBuild), Encoding.Unicode.GetBytes(profileBuild), required: false);
            }
        }

        return patched;
    }

    /// <summary>Replaces the length-prefixed UTF-16 value <paramref name="oldText"/> with <paramref name="newText"/>, updating
    /// its 32-bit character count and moving every later byte. Returns the input when the value is absent.</summary>
    private static byte[] ReplaceLengthPrefixed(byte[] data, string oldText, string newText)
    {
        for (int at = 0; at + 4 <= data.Length; at++)
        {
            int chars = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at, 4));
            if (chars != oldText.Length) continue;
            long end = at + 4L + 2L * chars;
            if (end > data.Length) continue;
            if (Encoding.Unicode.GetString(data, at + 4, 2 * chars) != oldText) continue;

            byte[] value = Encoding.Unicode.GetBytes(newText);
            var result = new byte[data.Length - (2L * chars) + value.Length];
            Buffer.BlockCopy(data, 0, result, 0, at + 4);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(at, 4), newText.Length);
            Buffer.BlockCopy(value, 0, result, at + 4, value.Length);
            Buffer.BlockCopy(data, (int)end, result, at + 4 + value.Length, data.Length - (int)end);
            return result;
        }
        return data;
    }

    private static byte[] ReplaceAll(byte[] data, byte[] oldBytes, byte[] newBytes, bool required)
    {
        var patched = new byte[data.Length];
        Buffer.BlockCopy(data, 0, patched, 0, data.Length);
        int replacements = 0;
        for (int at = IndexOf(patched, oldBytes, 0); at >= 0; at = IndexOf(patched, oldBytes, at + newBytes.Length))
        {
            newBytes.CopyTo(patched, at);
            replacements++;
        }
        if (replacements == 0 && required)
            throw new InvalidDataException($"BasicFileInfo holds no expected '{Encoding.Unicode.GetString(oldBytes)}' value to patch.");
        return patched;
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (int i = start; i + needle.Length <= haystack.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }
}
