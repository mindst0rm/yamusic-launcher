[CmdletBinding()]
param(
    [string]$Version = "1.1.5",
    [string]$Configuration = "Release",
    [switch]$SkipInstaller,
    [switch]$PublishGitHubRelease,
    [string]$GitHubRepo = "mindst0rm/yamusic-launcher",
    [string]$ReleaseTarget = "main",
    [switch]$Draft,
    [switch]$PreRelease
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "YaLauncher\YaLauncher.csproj"
$publishDir = Join-Path $repoRoot "artifacts\publish\win-x64"
$installerScript = Join-Path $repoRoot "installer\YaMusicLauncher.iss"
$installerOutDir = Join-Path $repoRoot "installer\output"
$releaseTemplatePath = Join-Path $repoRoot ".github\release-notes-template.md"

if ($SkipInstaller -and $PublishGitHubRelease) {
    throw "The flags -SkipInstaller and -PublishGitHubRelease cannot be used together."
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($Arguments -join ' ')"
    }
}

function Resolve-GhPath {
    $cmd = Get-Command gh.exe -ErrorAction SilentlyContinue
    if ($cmd) {
        return $cmd.Source
    }

    $candidates = @(
        (Join-Path $env:ProgramFiles "GitHub CLI\gh.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "GitHub CLI\gh.exe")
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) {
            return $candidate
        }
    }

    return $null
}

Write-Host "==> Publishing YaLauncher ($Configuration, win-x64)..." -ForegroundColor Cyan
if (-not (Test-Path $publishDir)) {
    New-Item -ItemType Directory -Path $publishDir | Out-Null
}

Invoke-Checked -FilePath "dotnet" -Arguments @(
    "publish",
    $projectPath,
    "-c", $Configuration,
    "-r", "win-x64",
    "--self-contained", "true",
    "-o", $publishDir,
    "/p:Version=$Version"
)

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
Invoke-Checked -FilePath $iscc -Arguments @(
    "/DAppVersion=$Version",
    "/DSourceDir=$publishDir",
    $installerScript
)

if (-not (Test-Path $installerOutDir)) {
    throw "Installer output directory not found after compilation: $installerOutDir"
}

$setupName = "YaMusicLauncher-Setup-$Version.exe"
$latestSetup = Get-ChildItem $installerOutDir -Filter $setupName -File -ErrorAction SilentlyContinue |
    Select-Object -First 1

if (-not $latestSetup) {
    throw "Installer compilation finished, but '$setupName' was not found in $installerOutDir"
}

Write-Host "Installer ready: $($latestSetup.FullName)" -ForegroundColor Green

if (-not $PublishGitHubRelease) {
    exit 0
}

if (-not (Test-Path $releaseTemplatePath)) {
    throw "Release notes template not found: $releaseTemplatePath"
}

$gh = Resolve-GhPath
if (-not $gh) {
    throw "GitHub CLI (gh.exe) not found. Install GitHub CLI or add gh.exe to PATH."
}

$tag = "v$Version"
$title = "Release $tag"

$allTags = @(& git tag --sort=-v:refname 2>$null)
$previousTag = $allTags | Where-Object { $_ -ne $tag } | Select-Object -First 1

if ($previousTag) {
    $commitLines = @(& git log --pretty=format:"- %s" "$previousTag..HEAD" 2>$null)
    $compareLine = "[$previousTag...$tag](https://github.com/$GitHubRepo/compare/$previousTag...$tag)"
}
else {
    $commitLines = @(& git log -n 10 --pretty=format:"- %s" 2>$null)
    $compareLine = "Первая публикуемая версия в репозитории."
}

if (-not $commitLines -or $commitLines.Count -eq 0) {
    $commitLines = @("- Технические изменения и улучшения стабильности.")
}

$commitHighlights = ($commitLines | Select-Object -First 10) -join "`r`n"

$template = Get-Content -Raw -Path $releaseTemplatePath
$notes = $template.Replace("{{TAG}}", $tag)
$notes = $notes.Replace("{{COMMIT_HIGHLIGHTS}}", $commitHighlights)
$notes = $notes.Replace("{{SETUP_FILE}}", $setupName)
$notes = $notes.Replace("{{COMPARE_LINE}}", $compareLine)

$tempNotes = Join-Path $env:TEMP "yamusic-release-notes-$tag.md"
Set-Content -Path $tempNotes -Value $notes -Encoding UTF8

Write-Host "==> Publishing GitHub Release $tag..." -ForegroundColor Cyan
& $gh release view $tag --repo $GitHubRepo *> $null
$releaseExists = $LASTEXITCODE -eq 0

if ($releaseExists) {
    Invoke-Checked -FilePath $gh -Arguments @(
        "release", "edit", $tag,
        "--repo", $GitHubRepo,
        "--title", $title,
        "--notes-file", $tempNotes
    )
    Invoke-Checked -FilePath $gh -Arguments @(
        "release", "upload", $tag, $latestSetup.FullName,
        "--repo", $GitHubRepo,
        "--clobber"
    )
}
else {
    $createArgs = @(
        "release", "create", $tag, $latestSetup.FullName,
        "--repo", $GitHubRepo,
        "--target", $ReleaseTarget,
        "--title", $title,
        "--notes-file", $tempNotes
    )
    if ($Draft) { $createArgs += "--draft" }
    if ($PreRelease) { $createArgs += "--prerelease" }

    Invoke-Checked -FilePath $gh -Arguments $createArgs
}

$releaseUrl = & $gh release view $tag --repo $GitHubRepo --json url --jq .url 2>$null
if ($releaseUrl) {
    Write-Host "Release ready: $releaseUrl" -ForegroundColor Green
}
