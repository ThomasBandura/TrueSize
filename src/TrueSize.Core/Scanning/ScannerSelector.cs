using TrueSize.Core.Interop;

namespace TrueSize.Core.Scanning;

public sealed record ScannerChoice(IVolumeScanner Scanner, VolumeInfo Volume, bool IsElevated, bool IsFastPath, string Reason);

/// <summary>Picks the scanner per volume based on elevation and file system.</summary>
public static class ScannerSelector
{
    public static bool IsElevated() => Elevation.IsProcessElevated();

    public static ScannerChoice Select(string path)
    {
        var volume = VolumeInfo.FromPath(Path.GetFullPath(path));
        bool elevated = IsElevated();

        // NtfsMftScanner (phase 3) and the ReFS evaluation (phase 4) are not implemented yet.
        string reason = elevated
            ? $"Elevated, but no fast scanner is available for {volume.FileSystem} yet; using the directory scanner."
            : "Not elevated: using the directory scanner. Run elevated for the fast scanner.";
        return new ScannerChoice(new DirectoryScanner(), volume, elevated, IsFastPath: false, reason);
    }
}
