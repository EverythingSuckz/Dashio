# Builds the release folder and the installer.
#
#   pwsh tools\publish.ps1                    # x64
#   pwsh tools\publish.ps1 -Arch arm64
#   pwsh tools\publish.ps1 -SkipInstaller     # only the folder
#
# Output goes to artifacts\ (gitignored). .NET and the Windows App SDK are bundled, so the
# installed app needs nothing else on the PC. Needs Inno Setup 6 for the installer.

param(
    [ValidateSet('x64', 'arm64')]
    [string]$Arch = 'x64',
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props'))).Project.PropertyGroup.Version
$artifacts = Join-Path $repo 'artifacts'
$folder = Join-Path $artifacts "Dashio-$version-$Arch"

if (Get-Process Dashio -ErrorAction SilentlyContinue) {
    Write-Host 'Dashio is running. A Release build does not need it closed, but close it before installing.' -ForegroundColor Yellow
}

if (Test-Path $folder) { [System.IO.Directory]::Delete($folder, $true) }

# --self-contained also reaches the helper, which is built as part of the app's build.
dotnet publish (Join-Path $repo 'src\Dashio.App') -c Release -r "win-$Arch" --self-contained true -o $folder -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

Get-ChildItem $folder -Recurse -Filter *.pdb | ForEach-Object { [System.IO.File]::Delete($_.FullName) }

foreach ($needed in 'Dashio.exe', 'Dashio.pri', 'Dashio.Helper.exe', 'coreclr.dll', 'Microsoft.ui.xaml.dll') {
    if (-not (Test-Path (Join-Path $folder $needed))) { throw "The release folder is missing $needed." }
}
foreach ($config in 'Dashio.runtimeconfig.json', 'Dashio.Helper.runtimeconfig.json') {
    $runtime = (Get-Content (Join-Path $folder $config) -Raw | ConvertFrom-Json).runtimeOptions
    if (-not $runtime.includedFrameworks) { throw "$config still expects .NET to be installed on the PC." }
}

$size = (Get-ChildItem $folder -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Folder:    {0}  ({1:N0} MB)" -f $folder, $size)
if ($SkipInstaller) { return }

$iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    if (-not (Test-Path $iscc)) { throw 'Inno Setup 6 was not found. Install it, or use -SkipInstaller.' }
}

$allowed = if ($Arch -eq 'arm64') { 'arm64' } else { 'x64compatible' }
& $iscc /Qp "/DAppVersion=$version" "/DArch=$Arch" "/DArchAllowed=$allowed" "/DSourceDir=$folder" `
    "/DOutputDir=$artifacts" "/DRepoRoot=$repo" (Join-Path $repo 'installer\Dashio.iss')
if ($LASTEXITCODE -ne 0) { throw 'The installer build failed.' }

$setup = Get-Item (Join-Path $artifacts "Dashio-Setup-$version-$Arch.exe")
Write-Host ("Installer: {0}  ({1:N0} MB)" -f $setup.FullName, ($setup.Length / 1MB))
