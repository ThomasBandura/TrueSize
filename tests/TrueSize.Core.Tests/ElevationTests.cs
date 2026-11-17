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
    public void Selector_FallsBackToDirectoryScanner_UntilFastScannersExist()
    {
        var choice = ScannerSelector.Select(Path.GetTempPath());
        Assert.IsType<DirectoryScanner>(choice.Scanner);
        Assert.False(choice.IsFastPath);
        Assert.Equal("NTFS", choice.Volume.FileSystem);
    }
}
