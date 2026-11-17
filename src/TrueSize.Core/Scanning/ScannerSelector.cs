using TrueSize.Core.Interop;

namespace TrueSize.Core.Scanning;

/// <param name="FastPathPossible">A faster scanner exists for this file system when elevated (NTFS only).</param>
public sealed record ScannerChoice(IVolumeScanner Scanner, VolumeInfo Volume, bool IsElevated, bool IsFastPath, bool FastPathPossible, string Reason);

/// <summary>Picks the scanner per volume based on elevation and file system.</summary>
public static class ScannerSelector
{
    public static bool IsElevated() => Elevation.IsProcessElevated();

    public static ScannerChoice Select(string path)
    {
        var volume = VolumeInfo.FromPath(Path.GetFullPath(path));
        bool elevated = IsElevated();
        bool ntfs = string.Equals(volume.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase);

        if (ntfs && elevated)
        {
            return new ScannerChoice(new NtfsMftScanner(), volume, elevated, IsFastPath: true, FastPathPossible: true, "Elevated NTFS volume: using the MFT scanner.");
        }

        if (ntfs)
        {
            return new ScannerChoice(new DirectoryScanner(), volume, elevated, IsFastPath: false, FastPathPossible: true,
                "Not elevated: using the directory scanner. Run elevated for the fast MFT scanner.");
        }

        // ReFS (phase 4, docs/refs.md): FSCTL_ENUM_USN_DATA returns ERROR_INVALID_FUNCTION even with an active USN journal,
        // and there is no documented on-disk format to read. The directory scanner is the only scanner.
        return new ScannerChoice(new DirectoryScanner(), volume, elevated, IsFastPath: false, FastPathPossible: false,
            $"{volume.FileSystem} volume: using the directory scanner (no faster scanner exists for {volume.FileSystem}).");
    }
}
