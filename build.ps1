<#
.SYNOPSIS
    Builds MwbLite from a clean clone.

.DESCRIPTION
    1. Installs the .NET SDK pinned in global.json into .\.dotnet (portable; nothing is installed
       system-wide) if it is not there yet. The installer script is downloaded from Microsoft and its
       Authenticode signature is verified before it runs.
    2. Builds and runs the unit tests (skip with -SkipTests).
    3. Publishes self-contained, single-file x64 executables to .\dist:
         MwbLite.exe         main app (tray), service host, install/uninstall commands
         MwbLite.Helper.exe  per-user clipboard/drag-drop helper (started by MwbLite.exe)

    NuGet packages are cached in .\.nuget, the CLI home is .\.dotnet\home, and .NET CLI telemetry is off.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
#>
[CmdletBinding()]
param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$root = $PSScriptRoot
$dotnetDir = Join-Path $root '.dotnet'
$dotnet = Join-Path $dotnetDir 'dotnet.exe'
$dist = Join-Path $root 'dist'
$sdkVersion = (Get-Content (Join-Path $root 'global.json') -Raw | ConvertFrom-Json).sdk.version

# Keep the SDK, its state and the package cache inside the repository; no CLI telemetry.
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:DOTNET_ROOT = $dotnetDir
$env:DOTNET_CLI_HOME = Join-Path $dotnetDir 'home'
$env:NUGET_PACKAGES = Join-Path $root '.nuget\packages'

function Invoke-Checked {
    param([string]$Exe, [string[]]$Arguments)
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$([IO.Path]::GetFileName($Exe)) $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

# --- 1. SDK ------------------------------------------------------------------------------------
$haveSdk = (Test-Path $dotnet) -and ((& $dotnet --list-sdks 2>$null) -match "^$([regex]::Escape($sdkVersion)) ")
if (-not $haveSdk) {
    Write-Host "Installing .NET SDK $sdkVersion into $dotnetDir ..."
    New-Item -ItemType Directory -Force -Path $dotnetDir | Out-Null
    $installer = Join-Path $dotnetDir 'dotnet-install.ps1'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer

    $sig = Get-AuthenticodeSignature -FilePath $installer
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
        throw "dotnet-install.ps1 signature check failed ($($sig.Status))."
    }

    Invoke-Checked 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $installer,
        '-Version', $sdkVersion, '-InstallDir', $dotnetDir, '-NoPath')
}

Write-Host "Using .NET SDK $(& $dotnet --version)"

# --- 2. Tests ----------------------------------------------------------------------------------
if (-not $SkipTests) {
    Invoke-Checked $dotnet @('build', (Join-Path $root 'src\UnitTests\MwbLite.UnitTests.csproj'), '-c', 'Release', '--nologo', '-v', 'minimal')
    $testExe = Get-ChildItem -Path (Join-Path $root 'src\UnitTests\bin') -Recurse -Filter 'MouseWithoutBorders.UnitTests.exe' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    Invoke-Checked $testExe.FullName @('--results-directory', (Join-Path $root 'src\UnitTests\bin\TestResults'))
}

# --- 3. Publish --------------------------------------------------------------------------------
$running = Get-Process -Name 'MwbLite', 'MwbLite.Helper' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($dist, [StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    throw "MwbLite is running from $dist (PID $($running.Id -join ', ')). Exit it from the tray menu, then run the build again."
}

if (Test-Path $dist) {
    Remove-Item -Recurse -Force $dist
}

$publishArgs = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--nologo', '-v', 'minimal',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    # No compression: compressed bundles are inflated into private memory at startup (~3x the RAM).
    '-p:EnableCompressionInSingleFile=false', '-p:DebugType=none', '-o', $dist)

Invoke-Checked $dotnet (@('publish', (Join-Path $root 'src\App\MwbLite.csproj')) + $publishArgs)
Invoke-Checked $dotnet (@('publish', (Join-Path $root 'src\App\Helper\MwbLite.Helper.csproj')) + $publishArgs)

foreach ($doc in 'LICENSE', 'NOTICE.md', 'README.md') {
    if (Test-Path (Join-Path $root $doc)) {
        Copy-Item (Join-Path $root $doc) $dist
    }
}

$sums = Get-ChildItem $dist -Filter *.exe | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
$sums | Set-Content -Encoding ASCII (Join-Path $dist 'SHA256SUMS.txt')

Write-Host ''
Write-Host "Published to $dist"
Get-ChildItem $dist | Format-Table Name, @{ Name = 'Size (MB)'; Expression = { '{0:N1}' -f ($_.Length / 1MB) } } -AutoSize
