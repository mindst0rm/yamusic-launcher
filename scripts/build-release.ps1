[CmdletBinding()]
param(
    [string]$Version = "1.1.3",
    [string]$Configuration = "Release",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "YaLauncher\YaLauncher.csproj"
$publishDir = Join-Path $repoRoot "artifacts\publish\win-x64"
$installerScript = Join-Path $repoRoot "installer\YaMusicLauncher.iss"
$installerOutDir = Join-Path $repoRoot "installer\output"

Write-Host "==> Publishing YaLauncher ($Configuration, win-x64)..." -ForegroundColor Cyan
if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

dotnet publish $projectPath `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -o $publishDir `
    /p:Version=$Version

Write-Host "Publish output: $publishDir" -ForegroundColor Green

if ($SkipInstaller) {
    Write-Host "Installer build skipped by -SkipInstaller flag." -ForegroundColor Yellow
    exit 0
}

if (-not (Test-Path $installerScript)) {
    throw "Installer script not found: $installerScript"
}

function Resolve-IsccPath {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) {
            return $candidate
        }
    }

    return $null
}

$iscc = Resolve-IsccPath
if (-not $iscc) {
    throw "ISCC.exe not found. Install Inno Setup 6 or add ISCC.exe to PATH."
}

Write-Host "==> Compiling installer via Inno Setup..." -ForegroundColor Cyan
& $iscc "/DAppVersion=$Version" "/DSourceDir=$publishDir" $installerScript

if (-not (Test-Path $installerOutDir)) {
    throw "Installer output directory not found after compilation: $installerOutDir"
}

$latestSetup = Get-ChildItem $installerOutDir -Filter "YaMusicLauncher-Setup-*.exe" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $latestSetup) {
    throw "Installer compilation finished, but Setup.exe was not found in $installerOutDir"
}

Write-Host "Installer ready: $($latestSetup.FullName)" -ForegroundColor Green
