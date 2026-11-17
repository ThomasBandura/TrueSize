# UI tests for TrueSize.App (winapp ui / UI Automation).
# Usage: .\ui-tests.ps1 -AppPid <pid> [-Elevated]
#   The app must be running. -Elevated additionally runs the fast scan through the helper (UAC prompt to confirm).
param(
    [Parameter(Mandatory)][int]$AppPid,
    [switch]$Elevated,
    [string]$OutDir = (Join-Path $PSScriptRoot 'results')
)

$ErrorActionPreference = 'Continue'
$pass = 0; $fail = 0; $results = @()
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Test-UI {
    param([string]$Name, [scriptblock]$Script)
    try {
        $output = & $Script 2>&1
        if ($LASTEXITCODE -eq 0) {
            $script:pass++; $script:results += @{ name = $Name; status = 'PASS' }
        } else {
            $script:fail++; $script:results += @{ name = $Name; status = 'FAIL'; detail = "$output" }
        }
    } catch {
        $script:fail++; $script:results += @{ name = $Name; status = 'FAIL'; detail = "$_" }
    }
}

function Get-ToggleButtons {
    (winapp ui search 'Auf- oder zuklappen' -a $AppPid --max 500 --json 2>$null | ConvertFrom-Json).matches |
        Where-Object { $_.isEnabled -and -not $_.isOffscreen }
}

# InfoBar title/message are separate text elements; the InfoBar's own value is not its message.
function Wait-Text([string]$Text, [int]$TimeoutMs) {
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    do {
        $found = (winapp ui search $Text -a $AppPid --json 2>$null | ConvertFrom-Json).matchCount
        if ($found -gt 0) { $global:LASTEXITCODE = 0; return }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    throw "'$Text' not shown within $TimeoutMs ms"
}

function Shot([string]$Name) { winapp ui screenshot -a $AppPid -o (Join-Path $OutDir $Name) 2>$null | Out-Null }

# ─── Start state: the app was launched with a path and has scanned it ───
Test-UI 'Toolbar controls exist' {
    foreach ($id in 'PathBox', 'BrowseButton', 'HardlinkModeBox', 'ElevatedScanBox', 'ScanButton', 'CancelButton') {
        winapp ui wait-for $id -a $AppPid -t 5000 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "$id missing" }
    }
}
Test-UI 'Initial scan finished (summary shows totals)' { winapp ui wait-for 'SummaryText' -a $AppPid --value 'Belegt' --contains -t 30000 }
Test-UI 'Cancel disabled while idle' { winapp ui wait-for 'CancelButton' -a $AppPid -p IsEnabled --value 'False' -t 3000 }
Shot '01-result.png'

# ─── Lazy expand / collapse ───
$before = @(Get-ToggleButtons).Count
Test-UI 'Expand first child directory adds rows' {
    $target = @(Get-ToggleButtons)[1]
    if (-not $target) { throw 'no expandable child row' }
    winapp ui invoke $target.selector -a $AppPid | Out-Null
    Start-Sleep -Milliseconds 500
    $after = @(Get-ToggleButtons).Count
    if ($after -le $before) { throw "visible expandable rows $before -> $after" }
    $global:LASTEXITCODE = 0
}
Shot '02-expanded.png'
Test-UI 'Collapse restores rows' {
    $target = @(Get-ToggleButtons)[1]
    winapp ui invoke $target.selector -a $AppPid | Out-Null
    Start-Sleep -Milliseconds 500
    $after = @(Get-ToggleButtons).Count
    if ($after -ne $before) { throw "expected $before, got $after" }
    $global:LASTEXITCODE = 0
}

# ─── Sorting ───
Test-UI 'Sort by name' { winapp ui invoke 'SortName' -a $AppPid }
Start-Sleep -Milliseconds 300
Shot '03-sorted-by-name.png'
Test-UI 'Sort by allocated' { winapp ui invoke 'SortAllocated' -a $AppPid }

# ─── Error display ───
Test-UI 'Unknown folder shows error' {
    winapp ui set-value 'PathBox' 'D:\does\not\exist\truesize' -a $AppPid | Out-Null
    winapp ui invoke 'ScanButton' -a $AppPid | Out-Null
    Wait-Text 'nicht gefunden' 5000
}
Shot '04-error.png'

# ─── Scan without elevation, with progress and cancel ───
Test-UI 'Disable fast scan' {
    $state = (winapp ui get-value 'ElevatedScanBox' -a $AppPid --json 2>$null | ConvertFrom-Json).text
    if ($state -eq 'On') { winapp ui invoke 'ElevatedScanBox' -a $AppPid | Out-Null }
    winapp ui wait-for 'ElevatedScanBox' -a $AppPid --value 'Off' -t 2000
}
Test-UI 'Cancel a running scan' {
    winapp ui set-value 'PathBox' 'C:\Windows' -a $AppPid | Out-Null
    winapp ui invoke 'ScanButton' -a $AppPid | Out-Null
    winapp ui wait-for 'StatusText' -a $AppPid --value 'Dateien' --contains -t 10000 | Out-Null
    winapp ui invoke 'CancelButton' -a $AppPid | Out-Null
    Wait-Text 'Scan abgebrochen' 15000
}
Test-UI 'Directory scan of Program Files\dotnet' {
    winapp ui set-value 'PathBox' 'C:\Program Files\dotnet' -a $AppPid | Out-Null
    winapp ui invoke 'ScanButton' -a $AppPid | Out-Null
    winapp ui wait-for 'SummaryText' -a $AppPid --value 'Verzeichnis-Scan' --contains -t 60000
}
Shot '05-dotnet-directory-scan.png'

# ─── Fast scan via the elevated helper (UAC prompt) ───
if ($Elevated) {
    Test-UI 'Fast scan via helper (confirm UAC)' {
        winapp ui invoke 'ElevatedScanBox' -a $AppPid | Out-Null
        winapp ui invoke 'ScanButton' -a $AppPid | Out-Null
        winapp ui wait-for 'SummaryText' -a $AppPid --value 'MFT-Scan (Admin)' --contains -t 120000
    }
    Shot '06-dotnet-mft-scan.png'
    Test-UI 'Second fast scan reuses the helper (no new UAC)' {
        $helpersBefore = @(Get-Process TrueSize.ScanHelper -ErrorAction SilentlyContinue).Id
        $previous = (winapp ui get-value 'SummaryText' -a $AppPid --json 2>$null | ConvertFrom-Json).text
        winapp ui set-value 'PathBox' 'C:\Windows' -a $AppPid | Out-Null
        winapp ui invoke 'ScanButton' -a $AppPid | Out-Null
        # The old summary already contains "MFT-Scan (Admin)": wait until it actually changes.
        $deadline = (Get-Date).AddSeconds(120)
        do {
            Start-Sleep -Milliseconds 500
            $current = (winapp ui get-value 'SummaryText' -a $AppPid --json 2>$null | ConvertFrom-Json).text
        } while ($current -eq $previous -and (Get-Date) -lt $deadline)
        if ($current -eq $previous -or $current -notmatch 'MFT-Scan \(Admin\)') { throw "summary: $current" }
        $helpersAfter = @(Get-Process TrueSize.ScanHelper -ErrorAction SilentlyContinue).Id
        if (@(Compare-Object $helpersBefore $helpersAfter).Count -ne 0) { throw "helper process changed: $helpersBefore -> $helpersAfter" }
        $global:LASTEXITCODE = 0
    }
    Shot '07-windows-mft-scan.png'
}

# ─── Accessibility: every interactive control outside the virtualized rows has an AutomationId ───
$all = (winapp ui inspect -a $AppPid --interactive --json 2>$null | ConvertFrom-Json).elements
$missing = @($all | Where-Object {
        $_.type -match 'Button|Edit|ComboBox|CheckBox|Hyperlink' -and
        $_.name -notmatch 'Minimize|Maximize|Close|Minimieren|Maximieren|Schließen|System|Auf- oder zuklappen' -and
        $_.className -notmatch 'PickerHost|#32770' -and -not $_.automationId })
if ($missing.Count -eq 0) {
    $pass++; $results += @{ name = 'All app controls have AutomationId'; status = 'PASS' }
} else {
    $fail++; $results += @{ name = 'AutomationId coverage'; status = 'FAIL'; detail = ($missing | ForEach-Object { "$($_.type) '$($_.name)'" }) -join ', ' }
}

Write-Host "`nPassed: $pass | Failed: $fail"
$results | Where-Object { $_.status -eq 'FAIL' } | ForEach-Object { Write-Host "  FAIL: $($_.name) - $($_.detail)" -ForegroundColor Red }
$results | ConvertTo-Json | Out-File (Join-Path $OutDir 'test-results.json')
if ($fail -gt 0) { exit 1 } else { exit 0 }
