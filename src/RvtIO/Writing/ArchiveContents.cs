using System.Buffers.Binary;

namespace RvtIO.Writing;

/// <summary>
/// The <c>Contents</c> stream is the archive header: a magic word, the archive version at
/// offset 4 and a directory of the streams. An older Revit refuses a file whose version is
/// above its own before it reads any stream, and the header layout itself changes between
/// releases (Revit 2027 writes a shorter header than 2023), so the written file takes the
/// header of the target profile when the profile's version is the older one. The profile is
/// a file of the target release, so its header is exactly what that release writes; the
/// stream directory it carries names its own streams, which is not read for the element data.
/// </summary>
internal static class ArchiveContents
{
    internal const uint Magic = 0x05221962;

    private const int VersionOffset = 4;

    /// <summary>True when the bytes are an archive header and its version is read.</summary>
    internal static bool TryReadVersion(byte[]? contents, out uint version)
    {
        version = 0;
        if (contents is null || contents.Length < VersionOffset + 4) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(contents) != Magic) return false;
        version = BinaryPrimitives.ReadUInt32LittleEndian(contents.AsSpan(VersionOffset, 4));
        return true;
    }

    /// <summary>
    /// The header the written file carries: the target profile's header when its archive
    /// version is below the source one, the source header otherwise (the source is already
    /// acceptable to the target release), or null when neither stream is an archive header.
    /// </summary>
    internal static byte[]? ForTarget(byte[]? sourceContents, byte[]? profileContents)
    {
        if (!TryReadVersion(profileContents, out uint target)) return sourceContents;
        if (!TryReadVersion(sourceContents, out uint source)) return null;
        return target < source ? profileContents : sourceContents;
    }
}
