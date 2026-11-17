@{
    RootModule           = 'TrueSize.PowerShell.dll'
    RequiredAssemblies   = @('TrueSize.Core.dll')
    ModuleVersion        = '0.2.0'
    GUID                 = '6f0f5d0e-3b1c-4c3e-9a55-7d2f2b8f4a11'
    Author               = 'Thomas Bandura'
    Description          = 'Real disk usage of directory trees: hard links counted once, reparse points not followed, logical, allocated and freeable size reported separately.'
    PowerShellVersion    = '7.6'
    CompatiblePSEditions = @('Core')
    FormatsToProcess     = @('TrueSize.Format.ps1xml')
    CmdletsToExport      = @('Get-TrueSize')
    FunctionsToExport    = @()
    VariablesToExport    = @()
    AliasesToExport      = @()
    PrivateData          = @{
        PSData = @{
            Tags = @('DiskUsage', 'NTFS', 'ReFS', 'Hardlink', 'Windows')
        }
    }
}
