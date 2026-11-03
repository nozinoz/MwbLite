<#
.SYNOPSIS
    Fills the winget manifest templates in packaging\winget for the current version.

.DESCRIPTION
    Reads the version from Directory.Build.props and the SHA-256 of MwbLite-Setup-<version>.exe from
    dist\SHA256SUMS.txt (written by build.cmd), fills the templates in packaging\winget and writes the three manifest
    files to out\winget\<version>\. If winget is installed, the result is checked with "winget validate".
    Nothing is submitted anywhere; see packaging\winget\README.md for how to send it to the winget community
    repository.

    The installer URL points at the GitHub Release (v<version>), so publish the release first
    (tools\publish-release.ps1). The hash must be that of the file attached to the release; if you rebuilt after
    publishing, use -Download to hash the published file instead.

.PARAMETER Version
    Defaults to the version in Directory.Build.props.

.PARAMETER Sha256
    The installer's SHA-256, instead of looking it up.

.PARAMETER SumsFile
    The hash list to read. Defaults to dist\SHA256SUMS.txt.

.PARAMETER Download
    Download the Setup from the GitHub Release and hash it.

.PARAMETER ReleaseDate
    The release's date (yyyy-MM-dd). Defaults to today.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\make-winget-manifest.ps1
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Sha256,
    [string]$SumsFile,
    [switch]$Download,
    [string]$ReleaseDate = (Get-Date -Format 'yyyy-MM-dd'),
    [string]$Repo = 'nozinoz/MwbLite'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$root = Split-Path $PSScriptRoot -Parent
if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}

if ($Version -notmatch '^\d+(\.\d+){1,3}$') {
    throw "'$Version' is not a version number."
}

if ($ReleaseDate -notmatch '^\d{4}-\d{2}-\d{2}$') {
    throw "ReleaseDate must look like 2026-01-31."
}

$setupName = "MwbLite-Setup-$Version.exe"

# --- the installer's hash ------------------------------------------------------------------------
if (-not $Sha256) {
    if ($Download) {
        $url = "https://github.com/$Repo/releases/download/v$Version/$setupName"
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ("MwbLite-winget-" + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $tmp | Out-Null
        try {
            Write-Host "Downloading $url ..."
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile (Join-Path $tmp $setupName)
            $Sha256 = (Get-FileHash (Join-Path $tmp $setupName) -Algorithm SHA256).Hash
        }
        finally {
            Remove-Item -Recurse -Force $tmp
        }
    }
    else {
        if (-not $SumsFile) {
            $SumsFile = Join-Path $root 'dist\SHA256SUMS.txt'
        }

        if (-not (Test-Path $SumsFile)) {
            throw "$SumsFile is missing. Run build.cmd first, or pass -Sha256, or -Download to hash the published release."
        }

        # Lines look like "<hash>  MwbLite-Setup-0.9.0.exe".
        $pattern = '^\s*([0-9a-fA-F]{64})\s+\*?(.*[\\/])?' + [regex]::Escape($setupName) + '\s*$'
        $line = Get-Content $SumsFile | Where-Object { $_ -match $pattern } | Select-Object -First 1
        if (-not $line) {
            throw "$SumsFile has no entry for $setupName."
        }

        $null = $line -match $pattern
        $Sha256 = $Matches[1]
    }
}

if ($Sha256 -notmatch '^[0-9a-fA-F]{64}$') {
    throw "'$Sha256' is not a SHA-256 hash."
}

$Sha256 = $Sha256.ToUpperInvariant()

# --- fill the templates --------------------------------------------------------------------------
$templates = Join-Path $root 'packaging\winget'
$out = Join-Path $root "out\winget\$Version"
if (Test-Path $out) {
    Remove-Item -Recurse -Force $out
}

New-Item -ItemType Directory -Path $out | Out-Null
$values = @{ '__VERSION__' = $Version; '__SHA256__' = $Sha256; '__RELEASE_DATE__' = $ReleaseDate; '__REPO__' = $Repo }
$utf8 = New-Object System.Text.UTF8Encoding($false)
foreach ($file in Get-ChildItem $templates -Filter 'nozinoz.MwbLite*.yaml') {
    $text = [IO.File]::ReadAllText($file.FullName) -replace '(?m)^# Template:.*\r?\n', '' # the note about the template isn't part of the manifest
    foreach ($key in $values.Keys) {
        $text = $text.Replace($key, $values[$key])
    }

    if ($text -match '__[A-Z_]+__') {
        throw "$($file.Name) still has an unfilled token: $($Matches[0])"
    }

    [IO.File]::WriteAllText((Join-Path $out $file.Name), $text.Replace("`r`n", "`n"), $utf8)
}

Write-Host "Wrote the winget manifest for MwbLite $Version to $out"
Write-Host "  $setupName  SHA-256 $Sha256"

# --- validate (only when winget is there) --------------------------------------------------------
if (Get-Command winget -ErrorAction SilentlyContinue) {
    & winget validate --manifest $out
    if ($LASTEXITCODE -ne 0) {
        throw "winget validate failed (exit code $LASTEXITCODE)."
    }
}
else {
    Write-Host 'winget is not installed here, so the manifest was not validated.'
}
