#Requires -Version 7.6
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }

# Run: Invoke-Pester ./tests/TrueSize.PowerShell.Tests
# Module path override: $env:TRUESIZE_MODULE = '<path to TrueSize.psd1>'

BeforeAll {
    $modulePath = $env:TRUESIZE_MODULE
    if (-not $modulePath) {
        $modulePath = Join-Path $PSScriptRoot '..\..\src\TrueSize.PowerShell\bin\Debug\net10.0-windows\TrueSize.psd1'
    }

    Import-Module (Resolve-Path $modulePath) -Force

    # Fixture (NTFS, under %TEMP%):
    #   big\data.bin          300 KB
    #   big\shared.bin        100 KB, hard link partner in other\shared-link.bin
    #   small\data.bin         10 KB
    #   other\shared-link.bin  hard link to big\shared.bin
    #   link                   junction to big
    $script:root = Join-Path ([IO.Path]::GetTempPath()) ("ts-pester-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path "$root\big", "$root\small", "$root\other" | Out-Null
    [IO.File]::WriteAllBytes("$root\big\data.bin", [byte[]]::new(300KB))
    [IO.File]::WriteAllBytes("$root\big\shared.bin", [byte[]]::new(100KB))
    [IO.File]::WriteAllBytes("$root\small\data.bin", [byte[]]::new(10KB))
    New-Item -ItemType HardLink -Path "$root\other\shared-link.bin" -Target "$root\big\shared.bin" | Out-Null
    New-Item -ItemType Junction -Path "$root\link" -Target "$root\big" | Out-Null
}

AfterAll {
    if ($script:root -and (Test-Path $script:root)) {
        # Remove the junction first so deletion never follows it.
        [IO.Directory]::Delete("$script:root\link")
        Remove-Item $script:root -Recurse -Force
    }
}

Describe 'Module' {
    It 'exports Get-TrueSize' {
        (Get-Command Get-TrueSize).ModuleName | Should -Be 'TrueSize'
    }
}

Describe 'Get-TrueSize output shape' {
    It 'returns the direct children sorted by allocated size by default' {
        $items = @(Get-TrueSize $root -WarningAction SilentlyContinue)
        $items.Name | Should -Be @('big', 'small', 'link', 'other') -Because 'big > small > zero-size entries ordered by name'
        $items | ForEach-Object { $_.Depth | Should -Be 1 }
    }

    It 'returns only the root with -Depth 0' {
        $items = @(Get-TrueSize $root -Depth 0 -WarningAction SilentlyContinue)
        $items.Count | Should -Be 1
        $items[0].FullName | Should -Be $root
        $items[0].Files | Should -Be 4
    }

    It 'limits the output with -Top' {
        @(Get-TrueSize $root -Top 1 -WarningAction SilentlyContinue).Name | Should -Be 'big'
    }

    It 'filters with -MinSize' {
        $items = @(Get-TrueSize $root -Depth 5 -MinSize 50KB -WarningAction SilentlyContinue)
        $items | ForEach-Object { $_.Allocated | Should -BeGreaterOrEqual 50KB }
        $items.Name | Should -Not -Contain 'small'
    }

    It 'emits tree order with indented names for -AsTree' {
        $items = @(Get-TrueSize $root -AsTree -Depth 2 -WarningAction SilentlyContinue)
        $items[0].Depth | Should -Be 0
        $items[1].Name | Should -Be 'big'
        $items[2].Depth | Should -Be 2
        $items[2].DisplayName | Should -BeLike '    *'
    }

    It 'returns a single ScanResult with -Raw' {
        $result = Get-TrueSize $root -Raw -WarningAction SilentlyContinue
        $result | Should -BeOfType ([TrueSize.Core.Scanning.ScanResult])
        $result.Tree.Info.FileSystem | Should -Be 'NTFS'
    }

    It 'accepts directories from the pipeline' {
        $items = @(Get-Item $root | Get-TrueSize -Depth 0 -WarningAction SilentlyContinue)
        $items[0].FullName | Should -Be $root
    }
}

Describe 'Get-TrueSize semantics' {
    It 'counts the hard-linked file once (FirstFound)' {
        $rootItem = Get-TrueSize $root -Depth 0 -WarningAction SilentlyContinue
        $rootItem.Size | Should -Be (300KB + 100KB + 10KB)
        (Get-TrueSize $root -WarningAction SilentlyContinue | Where-Object Name -eq 'other').Size | Should -Be 0
    }

    It 'does not count a file as freeable when another link remains' {
        $items = Get-TrueSize $root -Depth 2 -WarningAction SilentlyContinue
        $big = $items | Where-Object FullName -eq "$root\big"
        $big.Freeable | Should -BeLessThan $big.Allocated
        $shared = $items | Where-Object FullName -eq "$root\big\shared.bin"
        $shared.Markers | Should -Match 'H'
        $shared.LinkCount | Should -Be 2
        $shared.Freeable | Should -Be 0
    }

    It 'reports hard links separately with -HardlinkMode Separate' {
        $big = Get-TrueSize $root -HardlinkMode Separate -WarningAction SilentlyContinue | Where-Object Name -eq 'big'
        $big.Size | Should -Be 300KB
        $big.HardlinkAllocated | Should -BeGreaterThan 0
    }

    It 'marks the junction and does not follow it' {
        $link = Get-TrueSize $root -WarningAction SilentlyContinue | Where-Object Name -eq 'link'
        $link.Markers | Should -Match 'J'
        $link.Allocated | Should -Be 0
        $link.Files | Should -Be 0
    }
}

Describe 'Get-TrueSize errors and environment' {
    It 'warns when not elevated' {
        if ([TrueSize.Core.Scanning.ScannerSelector]::IsElevated()) {
            Set-ItResult -Skipped -Because 'the session is elevated'
            return
        }

        Get-TrueSize $root -Depth 0 -WarningVariable warnings -WarningAction SilentlyContinue | Out-Null
        $warnings | Should -Not -BeNullOrEmpty
        "$warnings" | Should -Match 'Not elevated'
    }

    It 'rejects non-file-system paths' {
        { Get-TrueSize 'HKCU:\Software' -ErrorAction Stop } | Should -Throw -ErrorId 'NotFileSystemPath,TrueSize.PowerShell.GetTrueSizeCommand'
    }

    It 'rejects files' {
        { Get-TrueSize "$root\big\data.bin" -ErrorAction Stop } | Should -Throw -ErrorId 'NotADirectory,TrueSize.PowerShell.GetTrueSizeCommand'
    }

    It 'stops promptly when the pipeline is stopped' {
        $ps = [powershell]::Create()
        try {
            $null = $ps.AddCommand('Import-Module').AddArgument((Get-Module TrueSize).Path).AddStatement()
            $null = $ps.AddCommand('Get-TrueSize').AddParameter('Path', $env:SystemRoot).AddParameter('WarningAction', 'SilentlyContinue')
            $async = $ps.BeginInvoke()
            Start-Sleep -Milliseconds 500
            $watch = [Diagnostics.Stopwatch]::StartNew()
            $ps.Stop()
            $watch.Elapsed.TotalSeconds | Should -BeLessThan 5
            $async.IsCompleted | Should -BeTrue
        }
        finally {
            $ps.Dispose()
        }
    }
}
