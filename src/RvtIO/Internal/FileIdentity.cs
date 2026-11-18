namespace RvtIO.Internal;

internal static class FileIdentity
{
    /// <summary>True when both paths name the same file. Existing files are compared by file identity (volume and file index on Windows,
    /// device and inode on Linux and macOS), which catches hard links, file links and directory links (junctions, directory symbolic links)
    /// anywhere in the path. Otherwise, or when a file does not exist yet, the real paths are compared: every existing component is resolved
    /// through its links, so a path below a linked directory is recognised before the file is created.</summary>
    internal static bool SameFile(string first, string second)
    {
        string a = Path.GetFullPath(first), b = Path.GetFullPath(second);
        if (File.Exists(a) && File.Exists(b) && TryGetIdentity(a) is { } ia && TryGetIdentity(b) is { } ib) return ia == ib;
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(RealPath(a), RealPath(b), comparison);
    }

    /// <summary>The path with every existing component resolved through its symbolic links and junctions; components that do not exist are kept.</summary>
    internal static string RealPath(string fullPath, int depth = 0)
    {
        string root = Path.GetPathRoot(fullPath) ?? "";
        string current = root;
        foreach (string part in fullPath[root.Length..].Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            // a link loop has no real path: stop resolving and compare what is left
            if (depth < 32 && info.Exists && info.LinkTarget != null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                current = RealPath(target.FullName, depth + 1);
        }
        return current;
    }

    // Volume serial number and file index on Windows, or device and inode on Linux and macOS; null where the platform call is not available.
    private static (ulong Device, ulong Inode)? TryGetIdentity(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (OperatingSystem.IsWindows())
                return GetFileInformationByHandle(handle, out var info)
                    ? (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow)
                    : null;
            if (OperatingSystem.IsLinux())
                return LinuxFileIdentity(handle);
            if (OperatingSystem.IsMacOS())
                return DarwinFileIdentity(handle);
            return null;
        }
        // a file that cannot be opened or queried is compared by path
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    private static (ulong Device, ulong Inode)? LinuxFileIdentity(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        const int AtEmptyPath = 0x1000;
        const uint StatxIno = 0x00000100u;
        byte emptyPath = 0;
        byte[] buffer = new byte[256];
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            if (LinuxStatx((int)handle.DangerousGetHandle(), ref emptyPath, AtEmptyPath, StatxIno, ref buffer[0]) != 0)
                return null;
        }
        finally
        {
            if (added) handle.DangerousRelease();
        }
        uint mask = BitConverter.ToUInt32(buffer, 0);
        if ((mask & StatxIno) == 0) return null;
        ulong inode = BitConverter.ToUInt64(buffer, 32);
        uint devMajor = BitConverter.ToUInt32(buffer, 136);
        uint devMinor = BitConverter.ToUInt32(buffer, 140);
        return (((ulong)devMajor << 32) | devMinor, inode);
    }

    private static (ulong Device, ulong Inode)? DarwinFileIdentity(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        byte[] buffer = new byte[256];
        bool added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            int fd = (int)handle.DangerousGetHandle();
            int rc = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.Arm64 => DarwinFstatArm64(fd, ref buffer[0]),
                System.Runtime.InteropServices.Architecture.X64 => DarwinFstatInode64(fd, ref buffer[0]),
                _ => -1,
            };
            if (rc != 0) return null;
        }
        finally
        {
            if (added) handle.DangerousRelease();
        }
        // __DARWIN_STRUCT_STAT64: int32 st_dev at 0, uint16 st_mode at 4, uint16 st_nlink at 6, uint64 st_ino at 8.
        uint device = BitConverter.ToUInt32(buffer, 0);
        ulong inode = BitConverter.ToUInt64(buffer, 8);
        return (device, inode);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out ByHandleFileInformation information);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int LinuxStatx(int dirfd, ref byte pathname, int flags, uint mask, ref byte statxbuf);

    [System.Runtime.InteropServices.DllImport("libSystem.dylib", EntryPoint = "fstat", SetLastError = true)]
    private static extern int DarwinFstatArm64(int fd, ref byte statbuf);

    [System.Runtime.InteropServices.DllImport("libSystem.dylib", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int DarwinFstatInode64(int fd, ref byte statbuf);
}
