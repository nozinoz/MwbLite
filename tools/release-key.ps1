<#
.SYNOPSIS
    The release key: creates it, backs it up, restores it and signs a release with it.

.DESCRIPTION
    Installed copies of MwbLite install an update only if the release's SHA256SUMS.txt is signed with the release key,
    whose public half is built into the app (src\App\Lite\LiteReleaseKey.cs). The private half exists only on the
    maintainer's PC: %USERPROFILE%\.mwblite-release\release-key.dpapi, encrypted with Windows DPAPI for your account
    (it can't be read by another user, or on another PC). Algorithm: ECDSA P-256 with SHA-256.

    The tool is tools\ReleaseSigner, built and run with the repository's portable SDK (.\.dotnet, installed by build.cmd).

    -New       Makes the key once (refuses if one exists), writes the public key into LiteReleaseKey.cs and prints it.
               Commit that change and build the release from it. Then make a backup.
    -Backup    Writes the private key to <file> as a passphrase-protected PKCS#8 PEM (AES-256, PBKDF2 600,000 rounds).
               Asks for the passphrase twice. Keep the file and the passphrase apart and off this PC.
    -Restore   Reads such a backup into the DPAPI file (new PC, new Windows install). Refuses if a key exists.
    -Sign      Signs a SHA256SUMS.txt, writing <file>.sig, and checks the signature against the public key in
               LiteReleaseKey.cs (it fails if they don't match). tools\publish-release.ps1 does this for you.

    If the key is lost, installed copies can no longer be updated by themselves; everyone installs the next version
    by hand once, and that version carries a new key.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\release-key.ps1 -New
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\release-key.ps1 -Backup D:\safe\mwblite-release-key.pem
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\release-key.ps1 -Sign .\dist\SHA256SUMS.txt
#>
[CmdletBinding()]
param(
    [switch]$New,
    [string]$Backup,
    [string]$Restore,
    [string]$Sign,

    # For tests only: another place for the private key, another LiteReleaseKey.cs, and the passphrase given up front.
    [Parameter(DontShow)][string]$KeyPath,
    [Parameter(DontShow)][string]$SourcePath,
    [Parameter(DontShow)][string]$PassphraseForTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $PSScriptRoot 'ReleaseSigner\ReleaseSigner.csproj'

$actions = @(
    if ($New) { 'new' }
    if ($Backup) { 'backup' }
    if ($Restore) { 'restore' }
    if ($Sign) { 'sign' }
)
if ($actions.Count -ne 1) {
    throw 'Give exactly one of -New, -Backup <file>, -Restore <file>, -Sign <SHA256SUMS.txt>.'
}

# The repository's portable SDK (build.cmd installs it); a copy of the repository without one can borrow another via DOTNET_ROOT.
$dotnetDir = Join-Path $root '.dotnet'
$nugetDir = Join-Path $root '.nuget\packages'
if (-not (Test-Path (Join-Path $dotnetDir 'dotnet.exe')) -and $env:DOTNET_ROOT -and (Test-Path (Join-Path $env:DOTNET_ROOT 'dotnet.exe'))) {
    $dotnetDir = $env:DOTNET_ROOT
    if ($env:NUGET_PACKAGES) {
        $nugetDir = $env:NUGET_PACKAGES
    }
}

$dotnet = Join-Path $dotnetDir 'dotnet.exe'
if (-not (Test-Path $dotnet)) {
    throw "The portable .NET SDK isn't at $dotnetDir. Run build.cmd once to install it."
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_MULTILEVEL_LOOKUP = '0'
$env:DOTNET_ROOT = $dotnetDir
$env:DOTNET_CLI_HOME = Join-Path $dotnetDir 'home'
$env:NUGET_PACKAGES = $nugetDir

& $dotnet build $project -c Release -v quiet --nologo -clp:NoSummary
if ($LASTEXITCODE -ne 0) {
    throw "Building tools\ReleaseSigner failed ($LASTEXITCODE)."
}

$dll = Join-Path $PSScriptRoot 'ReleaseSigner\bin\Release\net10.0-windows\ReleaseSigner.dll'
$toolArgs = @($dll)
switch ($actions[0]) {
    'new' { $toolArgs += 'new' }
    'backup' { $toolArgs += @('backup', $Backup) }
    'restore' { $toolArgs += @('restore', $Restore) }
    'sign' { $toolArgs += @('sign', $Sign) }
}

$toolArgs += @('--source', $(if ($SourcePath) { $SourcePath } else { Join-Path $root 'src\App\Lite\LiteReleaseKey.cs' }))
if ($KeyPath) {
    $toolArgs += @('--key', $KeyPath)
}

# The passphrase goes to the tool in an environment variable, not on a command line.
$passphraseVariable = 'MWBLITE_RELEASE_PASSPHRASE'
try {
    if ($actions[0] -in 'backup', 'restore') {
        if ($PassphraseForTests) {
            $plain = $PassphraseForTests
        } else {
            $first = Read-Host -AsSecureString -Prompt 'Passphrase for the backup'
            $second = if ($actions[0] -eq 'backup') { Read-Host -AsSecureString -Prompt 'Passphrase again' } else { $first }
            $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($first))
            $plain2 = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($second))
            if ($plain -cne $plain2) {
                throw "The two passphrases don't match."
            }
        }

        Set-Item -Path "Env:$passphraseVariable" -Value $plain
    }

    & $dotnet @toolArgs
    if ($LASTEXITCODE -ne 0) {
        throw "release-key.ps1 -$($actions[0]) failed ($LASTEXITCODE)."
    }
} finally {
    Remove-Item -Path "Env:$passphraseVariable" -ErrorAction SilentlyContinue
}
