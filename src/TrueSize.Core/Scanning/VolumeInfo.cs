using System.ComponentModel;
using System.Runtime.InteropServices;
using TrueSize.Core.Interop;

namespace TrueSize.Core.Scanning;

/// <param name="TotalBytes">Volume capacity (GetDiskFreeSpaceEx), 0 if unknown.</param>
/// <param name="FreeBytes">Free bytes on the volume (GetDiskFreeSpaceEx), 0 if unknown.</param>
public sealed record VolumeInfo(string Root, uint SerialNumber, string FileSystem, long TotalBytes = 0, long FreeBytes = 0)
{
    /// <summary>Bytes in use on the volume, including file system metadata.</summary>
    public long UsedBytes => TotalBytes - FreeBytes;

    /// <summary>ReFS can share clusters between files (block cloning, deduplication); per-file allocation then over-counts.</summary>
    public bool CanShareClusters => string.Equals(FileSystem, "ReFS", StringComparison.OrdinalIgnoreCase);

    public static unsafe VolumeInfo FromPath(string path)
    {
        char* root = stackalloc char[1024];
        if (!Native.GetVolumePathName(path, root, 1024))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"GetVolumePathName failed for '{path}'.");
        }

        string rootPath = new(root);
        char* fsName = stackalloc char[64];
        if (!Native.GetVolumeInformation(rootPath, null, 0, out uint serial, out _, out _, fsName, 64))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"GetVolumeInformation failed for '{rootPath}'.");
        }

        Native.GetDiskFreeSpaceEx(rootPath, out _, out ulong total, out ulong free);
        return new VolumeInfo(rootPath, serial, new string(fsName), (long)total, (long)free);
    }
}
