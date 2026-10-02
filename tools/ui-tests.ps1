# Drives the real Dashio window through its main flows with `winapp ui`.
#
#   pwsh tools\ui-tests.ps1                 # builds nothing; run `dotnet build src\Dashio.App` first
#                                           # (a solution build goes to bin\x64, which this does not run)
#   pwsh tools\ui-tests.ps1 -Configuration Release
#   pwsh tools\ui-tests.ps1 -Exe artifacts\Dashio-0.1.0-x64\Dashio.exe
#
# The script starts its own copy of the app with DASHIO_DATA_DIR pointing at a temp folder, so
# your settings and change log are not touched. It creates one throwaway startup entry under
# HKCU ("DashioUiTest", pointing at a file that does not exist) and removes it at the end.
# Nothing here needs administrator rights.
#
# The test window opens in the background and is driven through UI Automation, not the real
# mouse or keyboard, so you can keep working while it runs (about a minute). Just do not click
# or type in the test window itself.

param(
    [string]$Configuration = 'Debug',
    # Test this Dashio.exe instead of a build, for example the one in a release folder.
    [string]$Exe,
    [string]$ShotFolder = (Join-Path ([System.IO.Path]::GetTempPath()) 'dashio-ui-tests')
)

$ErrorActionPreference = 'Continue'
$repo = Split-Path $PSScriptRoot -Parent
$exe = if ($Exe) { $Exe } else { Join-Path $repo "src\Dashio.App\bin\$Configuration\net10.0-windows10.0.26100.0\win-x64\Dashio.exe" }
if (-not (Test-Path $exe)) { Write-Host "Not built: $exe" -ForegroundColor Red; exit 2 }

$testName = 'DashioUiTest'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$approvedKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
$dataFolder = Join-Path ([System.IO.Path]::GetTempPath()) "dashio-ui-data-$([guid]::NewGuid().ToString('N'))"

$pass = 0; $fail = 0; $results = @()

function Test-UI {
    param([string]$Name, [scriptblock]$Script)
    # Inside $Script use 'throw' to fail; a non-zero exit code from winapp also fails.
    try {
        $global:LASTEXITCODE = 0
        $output = & $Script 2>&1
        if ($LASTEXITCODE -eq 0) {
            $script:pass++; $script:results += @{ name = $Name; status = 'PASS' }
            Write-Host "  PASS  $Name"
        }
        else {
            $script:fail++; $script:results += @{ name = $Name; status = 'FAIL'; detail = "$output" }
            Write-Host "  FAIL  $Name" -ForegroundColor Red
        }
    }
    catch {
        $script:fail++; $script:results += @{ name = $Name; status = 'FAIL'; detail = "$_" }
        Write-Host "  FAIL  $Name`: $_" -ForegroundColor Red
    }
}

# The selector of the first element whose name and type match.
function Find-Selector([string]$Text, [string]$Type, [string]$ExactName) {
    $found = winapp ui search $Text -a $AppPid -w $hwnd --json 2>$null | ConvertFrom-Json
    $match = $found.matches | Where-Object {
        $_.type -eq $Type -and (-not $ExactName -or $_.name -eq $ExactName)
    } | Select-Object -First 1
    if (-not $match) { throw "No $Type matching '$Text' was found." }
    return $match.selector
}

function Get-ApprovedFlag {
    $value = (Get-ItemProperty $approvedKey -ErrorAction SilentlyContinue).$testName
    if ($null -eq $value) { return $null }
    return [int]$value[0]
}

function Save-Shot([string]$Name) {
    winapp ui screenshot -a $AppPid -w $hwnd -o (Join-Path $ShotFolder "$Name.png") 2>$null | Out-Null
}

# ─── The helper, run without elevation ───
# The elevated helper is a separate program copied next to the app. If it is built differently
# from the app it cannot list services, and every change fails with "The item no longer exists".
# Run through `dotnet` it skips its admin prompt, so this checks the lookup and changes nothing:
# the request asks for the state the service is already in, and without admin rights it is refused.
Test-UI 'The helper next to the app can look up a service' {
    $helper = Join-Path (Split-Path $exe -Parent) 'Dashio.Helper.dll'
    $service = Get-CimInstance Win32_Service |
        Where-Object { $_.StartMode -eq 'Manual' -and $_.PathName -notmatch 'svchost' -and $_.PathName -notmatch '\\Windows\\' } |
        Select-Object -First 1
    if (-not $service) { throw 'No third-party manual service to test with.' }

    $folder = Join-Path ([System.IO.Path]::GetTempPath()) "dashio-helper-test-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    try {
        $request = Join-Path $folder 'a.request.json'
        $response = Join-Path $folder 'a.response.json'
        @{ SchemaVersion = 1; Changes = @(@{ ItemId = "service:machine:$($service.Name)"; Target = @{ Enabled = $true; StartType = 'Manual' } }) } |
            ConvertTo-Json -Depth 5 | Set-Content $request
        dotnet $helper --request $request --response $response | Out-Null
        if (-not (Test-Path $response)) { throw 'The helper wrote no response.' }
        $result = (Get-Content $response -Raw | ConvertFrom-Json).Results[0]
        if ($result.Error -match 'no longer exists') { throw "The helper could not find $($service.Name)." }
        $global:LASTEXITCODE = 0
    }
    finally {
        [System.IO.Directory]::Delete($folder, $true)
    }
}

# ─── Arrange ───
New-Item -ItemType Directory -Force -Path $ShotFolder, $dataFolder | Out-Null
Set-ItemProperty $runKey -Name $testName -Value 'C:\DashioUiTest\does-not-exist.exe'
Remove-ItemProperty $approvedKey -Name $testName -ErrorAction SilentlyContinue

$env:DASHIO_DATA_DIR = $dataFolder
# Open the test window without taking focus, so it does not interrupt whoever is using the PC.
$env:DASHIO_NO_ACTIVATE = '1'
$process = Start-Process $exe -PassThru
$AppPid = $process.Id
Remove-Item Env:\DASHIO_DATA_DIR, Env:\DASHIO_NO_ACTIVATE

try {
    Test-UI 'Window appears' { winapp ui wait-for 'NavApps' -a $AppPid -t 20000 }
    $windows = winapp ui list-windows -a $AppPid --json 2>$null | ConvertFrom-Json
    $hwnd = ($windows | Where-Object { $_.title -eq 'Dashio' } | Select-Object -First 1).hwnd

    # ─── First scan and shell ───
    Test-UI 'First scan finishes with a summary' {
        # The scanning message is on screen from the start and goes when the first scan is done.
        winapp ui wait-for 'ScanStatus' -a $AppPid -w $hwnd --gone -t 60000 | Out-Null
        winapp ui wait-for 'AppsSummary' -a $AppPid -w $hwnd --value 'page per app' --contains -t 5000
    }
    foreach ($id in 'NavApps', 'NavAllItems', 'NavHistory', 'SearchBox', 'RefreshButton', 'ViewMenu',
        'HomeButton', 'LayoutToggle', 'FilterAll', 'FilterAtStartup', 'FilterNotInTaskManager') {
        Test-UI "$id exists" { winapp ui wait-for $id -a $AppPid -w $hwnd -t 3000 }
    }
    Test-UI 'The app list has app cards' {
        $tree = winapp ui inspect -a $AppPid -w $hwnd --interactive --json --depth 14 2>$null | ConvertFrom-Json
        $cards = @($tree.windows[0].elements | Where-Object { $_.name -match 'at startup' })
        if ($cards.Count -lt 3) { throw "Only $($cards.Count) app cards were found." }
    }
    Save-Shot '01-apps'

    # ─── Accessibility: everything a user can act on has a name ───
    Test-UI 'Every interactive element on the Apps page has a name' {
        $tree = winapp ui inspect -a $AppPid -w $hwnd --interactive --json --depth 14 2>$null | ConvertFrom-Json
        $unnamed = @($tree.windows[0].elements | Where-Object { $_.isEnabled -and -not $_.name })
        if ($unnamed.Count -gt 0) {
            throw "Unnamed: $(($unnamed | ForEach-Object { "$($_.type) $($_.selector)" }) -join ', ')"
        }
    }

    # ─── Grid view ───
    Test-UI 'The layout button switches to tiles and back' {
        winapp ui invoke 'LayoutToggle' -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'Show as a list' -a $AppPid -w $hwnd -t 3000 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'The button did not offer the list after switching to the grid.' }
        Start-Sleep -Milliseconds 600
        $tree = winapp ui inspect -a $AppPid -w $hwnd --interactive --json --depth 14 2>$null | ConvertFrom-Json
        $tiles = @($tree.windows[0].elements | Where-Object { $_.name -match 'at startup' })
        if ($tiles.Count -lt 3) { throw "Only $($tiles.Count) app tiles were found." }
        Save-Shot '01-apps-grid'
        winapp ui invoke 'LayoutToggle' -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'Show as a grid' -a $AppPid -w $hwnd -t 3000
    }

    # ─── Search ───
    Test-UI 'Search narrows the list to the test entry' {
        winapp ui set-value 'TextBox' $testName -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'AppsSummary' -a $AppPid -w $hwnd --value '1 app matches' --contains -t 5000
    }

    # ─── Detail page ───
    Test-UI 'Opening the app shows its detail page' {
        $card = Find-Selector $testName 'Button'
        winapp ui invoke $card -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'DetailTitle' -a $AppPid -w $hwnd --value $testName -t 5000
    }
    Test-UI 'The detail page offers Turn off all' { winapp ui wait-for 'TurnAllButton' -a $AppPid -w $hwnd -t 3000 }

    # ─── Queue a change, review it, cancel ───
    Test-UI 'Flipping a switch shows the pending bar' {
        $switch = Find-Selector $testName 'Button' $testName
        winapp ui invoke $switch -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'PendingTitle' -a $AppPid -w $hwnd --value '1 change' --contains -t 3000
    }
    Save-Shot '02-pending'
    Test-UI 'Review and apply opens a confirmation dialog' {
        winapp ui invoke 'ApplyButton' -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'PrimaryButton' -a $AppPid -w $hwnd -t 3000
    }
    Save-Shot '03-review-dialog'
    Test-UI 'Cancelling the dialog changes nothing' {
        winapp ui invoke 'CloseButton' -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'PrimaryButton' -a $AppPid -w $hwnd --gone -t 3000 | Out-Null
        if ($null -ne (Get-ApprovedFlag)) { throw 'The startup entry was changed although the dialog was cancelled.' }
        winapp ui wait-for 'PendingTitle' -a $AppPid -w $hwnd -t 2000
    }

    # ─── Apply for real (user-level entry: no admin prompt) ───
    Test-UI 'Applying turns the entry off in Windows' {
        winapp ui invoke 'ApplyButton' -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'PrimaryButton' -a $AppPid -w $hwnd -t 3000 | Out-Null
        winapp ui invoke 'PrimaryButton' -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'NoticeUndoButton' -a $AppPid -w $hwnd -t 15000 | Out-Null
        $flag = Get-ApprovedFlag
        if ($null -eq $flag -or ($flag -band 1) -ne 1) { throw "Expected a 'disabled' flag, found '$flag'." }
        if ((Get-ItemProperty $runKey).$testName -notlike '*does-not-exist.exe') { throw 'The command itself was changed.' }
    }
    Test-UI 'The pending bar is gone after applying' {
        winapp ui wait-for 'PendingTitle' -a $AppPid -w $hwnd --gone -t 3000
    }
    Save-Shot '04-applied'

    # ─── Undo from the banner ───
    Test-UI 'Undo turns the entry back on in Windows' {
        winapp ui invoke 'NoticeUndoButton' -a $AppPid -w $hwnd | Out-Null
        Start-Sleep -Seconds 3
        $flag = Get-ApprovedFlag
        if ($null -eq $flag -or ($flag -band 1) -ne 0) { throw "Expected an 'enabled' flag, found '$flag'." }
    }

    # ─── Discard ───
    Test-UI 'Discard drops queued changes' {
        $switch = Find-Selector $testName 'Button' $testName
        winapp ui invoke $switch -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'PendingTitle' -a $AppPid -w $hwnd -t 3000 | Out-Null
        winapp ui invoke 'DiscardButton' -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'PendingTitle' -a $AppPid -w $hwnd --gone -t 3000
    }

    # ─── History ───
    Test-UI 'History lists the change and its undo' {
        winapp ui invoke 'NavHistory' -a $AppPid -w $hwnd | Out-Null
        Start-Sleep -Milliseconds 1200
        foreach ($text in "Turned off $testName", "Turned on $testName") {
            $found = winapp ui search $text -a $AppPid -w $hwnd --json 2>$null | ConvertFrom-Json
            if ($found.matchCount -lt 1) { throw "History does not show '$text'." }
        }
        $global:LASTEXITCODE = 0
    }
    Save-Shot '05-history'

    # ─── All items ───
    Test-UI 'All items lists the entry in a table' {
        winapp ui invoke 'NavAllItems' -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'ItemsSummary' -a $AppPid -w $hwnd --value '1 item across 1 app' --contains -t 5000
    }
    Test-UI 'Clearing the search shows everything again' {
        winapp ui set-value 'TextBox' '' -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'ItemsSummary' -a $AppPid -w $hwnd --value 'apps' --contains -t 5000
    }
    Save-Shot '06-all-items'

    # ─── Settings and themes ───
    Test-UI 'Settings opens with the Windows theme selected' {
        winapp ui invoke 'SettingsItem' -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'ThemeBox' -a $AppPid -w $hwnd --value 'Use Windows setting' -t 5000
    }
    Test-UI 'Settings has the admin scan' { winapp ui wait-for 'AdminScanButton' -a $AppPid -w $hwnd -t 3000 }
    foreach ($theme in 'Light', 'Dark') {
        Test-UI "Theme can be set to $theme" {
            winapp ui invoke 'ThemeBox' -a $AppPid -w $hwnd | Out-Null
            Start-Sleep -Milliseconds 600
            $item = (winapp ui search $theme -a $AppPid --json 2>$null | ConvertFrom-Json).matches |
                Where-Object { $_.type -eq 'ListItem' -and $_.name -eq $theme } | Select-Object -First 1
            if (-not $item) { throw "The $theme option was not found." }
            winapp ui invoke $item.selector -a $AppPid | Out-Null
            winapp ui wait-for 'ThemeBox' -a $AppPid -w $hwnd --value $theme -t 3000
        }
        winapp ui invoke 'NavApps' -a $AppPid -w $hwnd 2>$null | Out-Null
        Start-Sleep -Milliseconds 900
        Save-Shot "07-apps-$($theme.ToLower())"
        winapp ui invoke 'SettingsItem' -a $AppPid -w $hwnd 2>$null | Out-Null
        Start-Sleep -Milliseconds 500
    }
    Save-Shot '08-settings'

    # ─── Home ───
    Test-UI 'Pressing the app name goes back to Apps' {
        winapp ui invoke 'HomeButton' -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'AppsSummary' -a $AppPid -w $hwnd --value 'page per app' --contains -t 5000
    }
}
finally {
    # ─── Clean up ───
    if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
    Remove-ItemProperty $runKey -Name $testName -ErrorAction SilentlyContinue
    Remove-ItemProperty $approvedKey -Name $testName -ErrorAction SilentlyContinue
    if (Test-Path $dataFolder) { [System.IO.Directory]::Delete($dataFolder, $true) }
}

Write-Host "`nPassed: $pass | Failed: $fail"
$results | Where-Object { $_.status -eq 'FAIL' } | ForEach-Object {
    Write-Host "  FAIL: $($_.name): $($_.detail)" -ForegroundColor Red
}
$results | ConvertTo-Json | Out-File (Join-Path $ShotFolder 'test-results.json')
Write-Host "Screenshots and results: $ShotFolder"
if ($fail -gt 0) { exit 1 } else { exit 0 }
