<#
.SYNOPSIS
    Builds MwbLite from a clean clone.

.DESCRIPTION
    1. Installs the .NET SDK pinned in global.json into .\.dotnet (portable; nothing is installed
       system-wide) if it is not there yet. The installer script is downloaded from Microsoft and its
       Authenticode signature is verified before it runs.
    2. Builds and runs the unit tests (skip with -SkipTests).
    3. Publishes to .\dist:
         MwbLite-Setup-<version>.exe   installer (wizard, Start menu, Apps entry, service, firewall rule)
         MwbLite-<version>-x64.msi     the same package for silent installs (msiexec /i ... /qn)
         portable\                     two single-file exes that run without installing
    4. Optionally signs everything (-CertThumbprint or -SignToolArgs).

    NuGet packages are cached in .\.nuget, the CLI home is .\.dotnet\home, and .NET CLI telemetry is off.
    The installer is built with the WiX Toolset 5 (from NuGet; nothing to install).

.PARAMETER CertThumbprint
    SHA-1 thumbprint of a code-signing certificate in the Windows certificate store (works with
    certificates on hardware tokens). Signs with SHA-256 and an RFC 3161 timestamp.

.PARAMETER SignToolArgs
    Full argument string for "signtool sign" instead of -CertThumbprint, e.g. for Azure Trusted Signing:
    "/fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 /dlib <path>\Azure.CodeSigning.Dlib.dll /dmdf <path>\metadata.json"

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
.EXAMPLE
    .\build.cmd -CertThumbprint 0123456789ABCDEF0123456789ABCDEF01234567
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [string]$CertThumbprint,
    [string]$SignToolArgs,
    [string]$SignToolPath,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$root = $PSScriptRoot
$dotnetDir = Join-Path $root '.dotnet'
$dotnet = Join-Path $dotnetDir 'dotnet.exe'
$dist = Join-Path $root 'dist'
$payload = Join-Path $root 'out\payload'
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
$running = Get-Process -Name 'MwbLite', 'MwbLite.Helper', 'MwbLite-Setup*' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($dist, [StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    throw "MwbLite or its Setup is running from $dist (PID $($running.Id -join ', ')). Exit it (tray menu > Exit, or Cancel in Setup), then run the build again."
}

foreach ($dir in $dist, $payload) {
    if (Test-Path $dir) {
        Remove-Item -Recurse -Force $dir
    }
}

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$portable = Join-Path $dist 'portable'
$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--nologo', '-v', 'minimal', '-p:DebugType=none')

# a) Portable: two single-file exes. No compression: compressed bundles are inflated into private
#    memory at startup (~3x the RAM).
$singleFile = $common + @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=false', '-o', $portable)
Invoke-Checked $dotnet (@('publish', (Join-Path $root 'src\App\MwbLite.csproj')) + $singleFile)
Invoke-Checked $dotnet (@('publish', (Join-Path $root 'src\App\Helper\MwbLite.Helper.csproj')) + $singleFile)

# b) Installer payload: both apps in one folder sharing one .NET runtime.
$folder = $common + @('-p:PublishSingleFile=false', '-o', $payload)
Invoke-Checked $dotnet (@('publish', (Join-Path $root 'src\App\MwbLite.csproj')) + $folder)
Invoke-Checked $dotnet (@('publish', (Join-Path $root 'src\App\Helper\MwbLite.Helper.csproj')) + $folder)

# Licence and docs ship in the install folder too (MIT requires the licence to travel with the software).
foreach ($doc in 'LICENSE', 'NOTICE.md', 'README.md') {
    Copy-Item (Join-Path $root $doc) $payload
}

# --- 4. Optional code signing -----------------------------------------------------------------
$signToolArgsResolved = $null
if ($CertThumbprint -or $SignToolArgs) {
    if (-not $SignToolPath) {
        # signtool.exe from Microsoft's Windows SDK build tools package on nuget.org (no SDK install).
        $stVersion = '10.0.26100.9169'
        $stDir = Join-Path $root ".nuget\tools\microsoft.windows.sdk.buildtools.$stVersion"
        $SignToolPath = Join-Path $stDir 'bin\10.0.26100.0\x64\signtool.exe'
        if (-not (Test-Path $SignToolPath)) {
            Write-Host "Downloading signtool ($stVersion) from nuget.org ..."
            New-Item -ItemType Directory -Force -Path $stDir | Out-Null
            $zip = Join-Path $stDir 'pkg.zip'
            Invoke-WebRequest -UseBasicParsing -OutFile $zip -Uri "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$stVersion/microsoft.windows.sdk.buildtools.$stVersion.nupkg"
            Expand-Archive -Path $zip -DestinationPath $stDir -Force
            Remove-Item $zip
        }

        $stSig = Get-AuthenticodeSignature -FilePath $SignToolPath
        if ($stSig.Status -ne 'Valid' -or $stSig.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
            throw "signtool.exe signature check failed ($($stSig.Status))."
        }
    }

    $signToolArgsResolved = if ($SignToolArgs) { $SignToolArgs } else { "/sha1 $CertThumbprint /fd sha256 /tr $TimestampUrl /td sha256" }
    Write-Host "Signing with: signtool sign $signToolArgsResolved"

    $toSign = @(
        (Join-Path $portable 'MwbLite.exe'), (Join-Path $portable 'MwbLite.Helper.exe'),
        (Join-Path $payload 'MwbLite.exe'), (Join-Path $payload 'MwbLite.dll'),
        (Join-Path $payload 'MwbLite.Helper.exe'), (Join-Path $payload 'MwbLite.Helper.dll'))
    foreach ($file in $toSign) {
        Invoke-Checked $SignToolPath (@('sign') + ($signToolArgsResolved -split ' ' | Where-Object { $_ }) + @($file))
    }
}

# --- 5. Installer: MwbLite-Setup.exe + MSI ---------------------------------------------------
if (-not $SkipInstaller) {
    $wixArgs = @('build', (Join-Path $root 'installer\MwbLite.Setup.wixproj'), '-c', 'Release', '--nologo', '-v', 'minimal',
        "-p:ProductVersion=$version", "-p:PayloadDir=$payload")
    if ($signToolArgsResolved) {
        $wixArgs += @('-p:SignOutput=true', "-p:SignToolPath=$SignToolPath", "-p:SignToolArgs=$signToolArgsResolved")
    }

    Invoke-Checked $dotnet $wixArgs
    $wixOut = Join-Path $root 'installer\bin\Release'
    Copy-Item (Join-Path $wixOut 'MwbLite-Setup.exe') (Join-Path $dist "MwbLite-Setup-$version.exe")
    Copy-Item (Join-Path $wixOut 'MwbLite-x64.msi') (Join-Path $dist "MwbLite-$version-x64.msi")
}

foreach ($doc in 'LICENSE', 'NOTICE.md', 'README.md') {
    if (Test-Path (Join-Path $root $doc)) {
        Copy-Item (Join-Path $root $doc) $portable
    }
}

$sums = Get-ChildItem $dist -Recurse -Include *.exe, *.msi | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.FullName.Substring($dist.Length + 1)
}
$sums | Set-Content -Encoding ASCII (Join-Path $dist 'SHA256SUMS.txt')

Write-Host ''
Write-Host "Published MwbLite $version to $dist"
Get-ChildItem $dist -Recurse -File | Format-Table @{ Name = 'File'; Expression = { $_.FullName.Substring($dist.Length + 1) } },
    @{ Name = 'Size (MB)'; Expression = { '{0:N1}' -f ($_.Length / 1MB) } } -AutoSize
