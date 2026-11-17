using System.Security.Principal;
using TrueSize.Core.Scanning;

namespace TrueSize.Core.Tests;

public sealed class ElevationTests
{
    [Fact]
    public void TokenElevation_MatchesAdministratorRoleOfCurrentToken()
    {
        // With UAC, the filtered (non-elevated) token is not in the Administrators role; the elevated one is.
        using var identity = WindowsIdentity.GetCurrent();
        bool inAdminRole = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        Assert.Equal(inAdminRole, ScannerSelector.IsElevated());
    }

    [Fact]
    public void Selector_PicksMftScannerOnlyWhenElevatedOnNtfs()
    {
        var choice = ScannerSelector.Select(Path.GetTempPath());
        Assert.Equal("NTFS", choice.Volume.FileSystem);
        if (ScannerSelector.IsElevated())
        {
            Assert.IsType<NtfsMftScanner>(choice.Scanner);
            Assert.True(choice.IsFastPath);
        }
        else
        {
            Assert.IsType<DirectoryScanner>(choice.Scanner);
            Assert.False(choice.IsFastPath);
        }
    }

    [Fact]
    public void Selector_UsesDirectoryScannerOnReFS_WithoutFastPathHint()
    {
        string refsPath = AppContext.BaseDirectory;
        if (VolumeInfo.FromPath(refsPath).FileSystem != "ReFS") return;

        var choice = ScannerSelector.Select(refsPath);
        Assert.IsType<DirectoryScanner>(choice.Scanner);
        Assert.False(choice.IsFastPath);
        Assert.False(choice.FastPathPossible);
        Assert.True(choice.Volume.CanShareClusters);
    }

    [Fact]
    public void VolumeInfo_ReportsCapacity()
    {
        var volume = VolumeInfo.FromPath(Path.GetTempPath());
        Assert.True(volume.TotalBytes > 0);
        Assert.InRange(volume.FreeBytes, 0, volume.TotalBytes);
        Assert.False(volume.CanShareClusters); // %TEMP% is on NTFS here
    }
}
