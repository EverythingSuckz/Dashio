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
# The test window never comes to the front and sits off the screen. It is driven through UI
# Automation, not the real mouse or keyboard, so you can keep working while it runs (about two
# minutes). The last test fails if the window took the front or showed on the screen at any point.

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

# Watches the whole run for the test window coming to the front or appearing on the screen.
Add-Type -Path (Join-Path $PSScriptRoot 'FrontWatch.cs')
[FrontWatch]::Start()

function Test-UI {
    param([string]$Name, [scriptblock]$Script)
    # Inside $Script use 'throw' to fail; a non-zero exit code from winapp also fails.
    try {
        [FrontWatch]::Step = $Name
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

# Types into a filter or search box. The box is a group; the text goes into the edit field inside it.
function Set-Box([string]$Name, [string]$Text) {
    $selector = Find-Selector $Name 'Edit'
    winapp ui set-value $selector $Text -a $AppPid -w $hwnd | Out-Null
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
        @{ SchemaVersion = 2; Changes = @(@{ ItemId = "service:machine:$($service.Name)"; Target = @{ Enabled = $true; StartType = 'Manual' } }) } |
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
# The test window refuses activation and opens off the screen, so it does not interrupt whoever is using the PC.
$env:DASHIO_NO_ACTIVATE = '1'
$process = Start-Process $exe -PassThru
$AppPid = $process.Id
[FrontWatch]::ProcessId = $AppPid
Remove-Item Env:\DASHIO_DATA_DIR, Env:\DASHIO_NO_ACTIVATE

try {
    Test-UI 'Window appears' { winapp ui wait-for 'NavApps' -a $AppPid -t 20000 }
    $windows = winapp ui list-windows -a $AppPid --json 2>$null | ConvertFrom-Json
    $hwnd = ($windows | Where-Object { $_.title -eq 'Dashio' } | Select-Object -First 1).hwnd

    # ─── Overview: live figures ───
    Test-UI 'The app opens on Overview' { winapp ui wait-for 'OverviewTitle' -a $AppPid -w $hwnd -t 5000 }
    Test-UI 'Overview shows memory and processor use' {
        winapp ui wait-for 'OverviewMemory' -a $AppPid -w $hwnd --value 'B' --contains -t 10000 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'No memory figure appeared.' }
        winapp ui wait-for 'OverviewCpu' -a $AppPid -w $hwnd --value '%' --contains -t 10000
    }
    Test-UI 'Overview lists the apps using the most memory' {
        # The lists fill in once the first scan has matched processes to apps.
        $deadline = (Get-Date).AddSeconds(60)
        do {
            Start-Sleep -Milliseconds 1000
            $found = winapp ui search 'processes' -a $AppPid -w $hwnd --json 2>$null | ConvertFrom-Json
            $script:usageRows = @($found.matches | Where-Object { $_.type -eq 'Button' })
        } while ($script:usageRows.Count -lt 3 -and (Get-Date) -lt $deadline)
        if ($script:usageRows.Count -lt 3) { throw "Only $($script:usageRows.Count) apps were listed." }
        $global:LASTEXITCODE = 0
    }
    Save-Shot '00-overview'
    Test-UI 'An app opened from Overview shows what it is running' {
        # A row's name carries its live figure, so a row found a moment ago may already read differently.
        foreach ($attempt in 1..4) {
            $found = winapp ui search 'processes' -a $AppPid -w $hwnd --json 2>$null | ConvertFrom-Json
            $row = $found.matches | Where-Object { $_.type -eq 'Button' -and $_.name -notmatch '^(Windows|Dashio),' } | Select-Object -First 1
            winapp ui invoke $row.selector -a $AppPid -w $hwnd 2>$null | Out-Null
            winapp ui wait-for 'DetailUsage' -a $AppPid -w $hwnd --value 'process' --contains -t 3000 2>$null | Out-Null
            if ($LASTEXITCODE -eq 0) { break }
        }
        if ($LASTEXITCODE -ne 0) { throw 'The app page did not show a running summary.' }
    }
    Save-Shot '00-running-now'
    Test-UI 'End app asks first and can be cancelled' {
        winapp ui invoke 'EndAppButton' -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'PrimaryButton' -a $AppPid -w $hwnd --value 'End' -t 4000 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'No confirmation appeared.' }
        Save-Shot '00-end-confirm'
        winapp ui invoke 'CloseButton' -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'PrimaryButton' -a $AppPid -w $hwnd --gone -t 3000 | Out-Null
        # Nothing was ended, so the app is still listed as running.
        winapp ui wait-for 'DetailUsage' -a $AppPid -w $hwnd --value 'process' --contains -t 3000
    }
    Test-UI 'A startup tile opens the Apps page on its tab' {
        winapp ui invoke 'NavOverview' -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'TileAtStartup' -a $AppPid -w $hwnd -t 5000 | Out-Null
        winapp ui invoke 'TileAtStartup' -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'AppsSummary' -a $AppPid -w $hwnd -t 5000 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'The Apps page did not open.' }
        # Back to the full list for the tests below.
        winapp ui invoke 'FilterAll' -a $AppPid -w $hwnd
    }

    # ─── Processes: everything running, grouped by app ───
    Test-UI 'Processes lists the running apps' {
        winapp ui invoke 'NavProcesses' -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'ProcessesLoading' -a $AppPid -w $hwnd --gone -t 60000 | Out-Null
        winapp ui wait-for 'ProcessesMemory' -a $AppPid -w $hwnd --value 'B' --contains -t 10000 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'No memory figure appeared.' }
        $found = winapp ui search 'processor' -a $AppPid -w $hwnd --json 2>$null | ConvertFrom-Json
        $rows = @($found.matches | Where-Object { $_.type -eq 'ListItem' -and $_.name -match 'collapsed' })
        if ($rows.Count -lt 3) { throw "Only $($rows.Count) running apps were listed." }
        $global:LASTEXITCODE = 0
    }
    Test-UI 'Pressing a running app shows its processes' {
        # A row's name carries its live figures, so a row found a moment ago may already read differently.
        foreach ($attempt in 1..4) {
            $found = winapp ui search 'collapsed' -a $AppPid -w $hwnd --json 2>$null | ConvertFrom-Json
            $row = $found.matches | Where-Object { $_.type -eq 'ListItem' -and $_.name -notmatch '^(Windows|Dashio),' } | Select-Object -First 1
            winapp ui invoke $row.selector -a $AppPid -w $hwnd 2>$null | Out-Null
            Start-Sleep -Milliseconds 800
            $children = winapp ui search 'Process ' -a $AppPid -w $hwnd --json 2>$null | ConvertFrom-Json
            $shown = @($children.matches | Where-Object { $_.type -eq 'ListItem' -and $_.name -match 'Process \d+' })
            if ($shown.Count -gt 0) { break }
        }
        if ($shown.Count -eq 0) { throw 'No process rows appeared under the app.' }
        $global:LASTEXITCODE = 0
    }
    Save-Shot '00-processes'
    Test-UI 'The process filter narrows the list' {
        Set-Box 'Filter processes' 'no-such-program-anywhere'
        winapp ui wait-for 'ProcessesSummary' -a $AppPid -w $hwnd --value 'no-such-program-anywhere' --contains -t 5000 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'The summary did not mention the filter.' }
        Set-Box 'Filter processes' ''
        winapp ui wait-for 'ProcessesSummary' -a $AppPid -w $hwnd --value 'grouped by the app' --contains -t 5000
    }

    # ─── Storage: sizes ───
    Test-UI 'Storage shows the drives and offers to scan one' {
        winapp ui invoke 'NavStorage' -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'StorageStartScan' -a $AppPid -w $hwnd -t 5000 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'The offer to scan the drive is missing.' }
        $found = winapp ui search ' free' -a $AppPid -w $hwnd --json 2>$null | ConvertFrom-Json
        $drives = @($found.matches | Where-Object { $_.type -eq 'Button' -and $_.name -match 'used of' })
        if ($drives.Count -lt 1) { throw 'No drive is shown.' }
        winapp ui wait-for 'StorageAdminCheck' -a $AppPid -w $hwnd -t 3000 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'The choice to scan as administrator is missing.' }
        $global:LASTEXITCODE = 0
    }
    Save-Shot '00-storage-folders'
    Test-UI 'Storage leads to the apps, largest first' {
        winapp ui invoke 'StorageAppsBySize' -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'AppsSummary' -a $AppPid -w $hwnd -t 5000 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'The Apps page did not open.' }
        # Sizes arrive once the installed list has been built and its folders measured.
        $deadline = (Get-Date).AddSeconds(60)
        do {
            Start-Sleep -Milliseconds 1000
            $found = winapp ui search 'B' -a $AppPid -w $hwnd --json 2>$null | ConvertFrom-Json
            $script:sizedRows = @($found.matches | Where-Object { $_.type -eq 'ListItem' -and $_.name -match '\d (MB|GB)' })
        } while ($script:sizedRows.Count -lt 3 -and (Get-Date) -lt $deadline)
        if ($script:sizedRows.Count -lt 3) { throw "Only $($script:sizedRows.Count) apps show a size." }
        $global:LASTEXITCODE = 0
    }
    Save-Shot '00-apps-by-size'
    Test-UI 'An app opened from the list shows where its files are' {
        $found = winapp ui search 'GB' -a $AppPid -w $hwnd --json 2>$null | ConvertFrom-Json
        # "about 75 GB" is a size Windows recorded for an app with no folder to show.
        $row = $found.matches | Where-Object { $_.type -eq 'ListItem' -and $_.name -match '\d GB' -and $_.name -notmatch 'about' } | Select-Object -First 1
        winapp ui invoke $row.selector -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'DetailStorage' -a $AppPid -w $hwnd --value 'B' --contains -t 8000
    }
    Save-Shot '00-storage-detail'
    Test-UI 'Uninstall asks first and can be cancelled' {
        winapp ui invoke 'UninstallButton' -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'PrimaryButton' -a $AppPid -w $hwnd --value 'Uninstall' -t 4000 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'No confirmation appeared.' }
        Save-Shot '00-uninstall-confirm'
        # Cancel: nothing is started.
        winapp ui invoke 'CloseButton' -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'PrimaryButton' -a $AppPid -w $hwnd --gone -t 3000 | Out-Null
        winapp ui wait-for 'UninstallButton' -a $AppPid -w $hwnd -t 3000
    }

    # ─── First scan and shell ───
    Test-UI 'First scan finishes with a summary' {
        winapp ui invoke 'NavApps' -a $AppPid -w $hwnd | Out-Null
        # The scanning message is on screen from the start and goes when the first scan is done.
        winapp ui wait-for 'ScanStatus' -a $AppPid -w $hwnd --gone -t 60000 | Out-Null
        winapp ui wait-for 'AppsSummary' -a $AppPid -w $hwnd --value 'apps on this PC' --contains -t 5000
    }
    Test-UI 'Components are left out of the list until asked for' {
        $summary = { (winapp ui get-value 'AppsSummary' -a $AppPid -w $hwnd 2>$null | Select-Object -First 1) -replace '\D.*$' }
        $link = {
            param($text)
            (winapp ui search $text -a $AppPid -w $hwnd --json 2>$null | ConvertFrom-Json).matches |
                Where-Object { $_.type -eq 'Hyperlink' } | Select-Object -First 1
        }
        $before = [int](& $summary)
        $show = & $link 'Show them'
        if (-not $show) { throw 'Nothing says that components are left out.' }
        winapp ui invoke $show.selector -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'AppsComponents' -a $AppPid -w $hwnd --value 'Including' --contains -t 5000 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'The components did not appear.' }
        $with = [int](& $summary)
        if ($with -le $before) { throw "Showing the components did not add any: $before, then $with." }

        winapp ui invoke (& $link 'Leave them out').selector -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'AppsComponents' -a $AppPid -w $hwnd --value 'left out' --contains -t 5000
    }
    foreach ($id in 'NavProcesses', 'NavApps', 'NavStorage', 'NavStartup', 'NavHistory', 'SearchBox', 'ViewMenu',
        'HomeButton', 'LayoutToggle', 'AppsFilter', 'FilterAll', 'FilterRunning', 'FilterAtStartup', 'FilterNotInTaskManager', 'FilterUnused') {
        Test-UI "$id exists" { winapp ui wait-for $id -a $AppPid -w $hwnd -t 3000 }
    }
    Test-UI 'The Not opened lately tab says what it rests on' {
        winapp ui invoke 'FilterUnused' -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'AppsEvidence' -a $AppPid -w $hwnd -t 5000 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'No explanation was shown.' }
        Save-Shot '00-not-opened'
        winapp ui invoke 'FilterAll' -a $AppPid -w $hwnd
    }
    Test-UI 'The app list has a row per app' {
        $found = winapp ui search 'start with Windows' -a $AppPid -w $hwnd --json 2>$null | ConvertFrom-Json
        $rows = @($found.matches | Where-Object { $_.type -eq 'ListItem' })
        if ($rows.Count -lt 3) { throw "Only $($rows.Count) app rows were found." }
        $global:LASTEXITCODE = 0
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
        $found = winapp ui search 'start with Windows' -a $AppPid -w $hwnd --json 2>$null | ConvertFrom-Json
        $tiles = @($found.matches | Where-Object { $_.type -eq 'Button' })
        if ($tiles.Count -lt 3) { throw "Only $($tiles.Count) app tiles were found." }
        Save-Shot '01-apps-grid'
        winapp ui invoke 'LayoutToggle' -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'Show as a grid' -a $AppPid -w $hwnd -t 3000
    }

    # ─── Search: jump to something by name ───
    Test-UI 'Search suggests the test entry and opens its app' {
        Set-Box 'Search Dashio' $testName
        Start-Sleep -Milliseconds 1200
        # The suggestions open in a window of their own.
        $found = winapp ui search $testName -a $AppPid --json 2>$null | ConvertFrom-Json
        $suggestion = $found.matches | Where-Object { $_.type -eq 'ListItem' -and $_.name -match 'App' } | Select-Object -First 1
        if (-not $suggestion) { throw 'The app was not suggested.' }
        winapp ui invoke $suggestion.selector -a $AppPid | Out-Null
        winapp ui wait-for 'DetailTitle' -a $AppPid -w $hwnd --value $testName -t 5000
    }

    # ─── The filter of the Apps page ───
    Test-UI 'The filter narrows the list to the test entry' {
        winapp ui invoke 'NavApps' -a $AppPid -w $hwnd | Out-Null
        Set-Box 'Filter apps' $testName
        winapp ui wait-for 'AppsSummary' -a $AppPid -w $hwnd --value '1 app matches' --contains -t 5000
    }

    # ─── Detail page ───
    Test-UI 'Opening the app shows its detail page' {
        $row = Find-Selector $testName 'ListItem'
        winapp ui invoke $row -a $AppPid -w $hwnd | Out-Null
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

    # ─── Startup ───
    Test-UI 'Startup lists the entry in a table' {
        winapp ui invoke 'NavStartup' -a $AppPid -w $hwnd | Out-Null
        Set-Box 'Filter startup items' $testName
        winapp ui wait-for 'StartupSummary' -a $AppPid -w $hwnd --value '1 item across 1 app' --contains -t 5000
    }
    Save-Shot '06-startup'
    Test-UI 'Startup says what an item is using now' {
        winapp ui wait-for 'SortMemory' -a $AppPid -w $hwnd -t 3000 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'The Memory column is missing.' }
        # The test entry points at a file that does not exist, so nothing of it can be running.
        $row = (winapp ui search $testName -a $AppPid -w $hwnd --json 2>$null | ConvertFrom-Json).matches |
            Where-Object { $_.type -eq 'ListItem' } | Select-Object -First 1
        if (-not $row) { throw 'The test entry is not listed.' }
        $found = winapp ui search 'Not running' -a $AppPid -w $hwnd --json 2>$null | ConvertFrom-Json
        if ($found.matchCount -lt 1) { throw 'The entry does not say it is not running.' }
        $global:LASTEXITCODE = 0
    }
    Test-UI 'The switch on a Startup row queues a change' {
        $switch = Find-Selector $testName 'Button' $testName
        winapp ui invoke $switch -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'PendingTitle' -a $AppPid -w $hwnd --value '1 change' --contains -t 3000 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'The pending bar did not appear.' }
        winapp ui invoke 'DiscardButton' -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'PendingTitle' -a $AppPid -w $hwnd --gone -t 3000
    }
    Test-UI 'Clearing the filters shows everything again' {
        Set-Box 'Filter startup items' ''
        winapp ui wait-for 'StartupSummary' -a $AppPid -w $hwnd --value 'apps' --contains -t 5000 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'The Startup filter did not clear.' }
        foreach ($id in 'KindAll', 'KindService', 'KindScheduledTask', 'KindRunKey') {
            winapp ui wait-for $id -a $AppPid -w $hwnd -t 3000 | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "The $id tab is missing." }
        }
        winapp ui invoke 'NavApps' -a $AppPid -w $hwnd | Out-Null
        Set-Box 'Filter apps' ''
        winapp ui wait-for 'AppsSummary' -a $AppPid -w $hwnd --value 'apps on this PC' --contains -t 5000
    }

    # ─── Settings and themes ───
    Test-UI 'Settings opens with the Windows theme selected' {
        winapp ui invoke 'SettingsItem' -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'ThemeBox' -a $AppPid -w $hwnd --value 'Use Windows setting' -t 5000
    }
    Test-UI 'Settings can scan again' { winapp ui wait-for 'RescanButton' -a $AppPid -w $hwnd -t 3000 }
    Test-UI 'Settings has the admin scan' { winapp ui wait-for 'AdminScanButton' -a $AppPid -w $hwnd -t 3000 }
    Test-UI 'Settings has the last-opened check' { winapp ui wait-for 'UsageCheckButton' -a $AppPid -w $hwnd -t 3000 }
    Test-UI 'Settings has the refresh interval' {
        winapp ui wait-for 'RefreshBox' -a $AppPid -w $hwnd --value '2 seconds' -t 3000
    }
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
    Test-UI 'Pressing the app name goes back to Overview' {
        winapp ui invoke 'HomeButton' -a $AppPid -w $hwnd | Out-Null
        winapp ui wait-for 'OverviewTitle' -a $AppPid -w $hwnd -t 5000
    }

    # ─── Not interrupting ───
    Test-UI 'The test window never came to the front or onto the screen' {
        [FrontWatch]::Stop()
        if ([FrontWatch]::Problems.Count -gt 0) {
            throw "In front for $([FrontWatch]::MillisecondsInFront) ms, on screen for $([FrontWatch]::MillisecondsOnScreen) ms: $([FrontWatch]::Problems -join '; ')"
        }
        $global:LASTEXITCODE = 0
    }
}
finally {
    [FrontWatch]::Stop()
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
