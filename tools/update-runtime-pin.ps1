<#
.SYNOPSIS
    Points installer\DotNetRuntime.props at the latest .NET 10 Desktop Runtime (x64) installer.

.DESCRIPTION
    Reads Microsoft's release metadata for the .NET 10.0 channel, takes the newest Windows Desktop runtime
    installer for win-x64, and writes its version, download URL, SHA-512 (from the metadata) and size (from an
    HTTP HEAD request) into installer\DotNetRuntime.props. Run it when Microsoft ships a .NET 10 patch, then build.
    Nothing is downloaded except the metadata.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\update-runtime-pin.ps1
#>
[CmdletBinding()]
param(
    [string]$Channel = '10.0'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$metadataUrl = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/$Channel/releases.json"
$metadata = Invoke-RestMethod -UseBasicParsing -Uri $metadataUrl
$release = $metadata.releases | Where-Object { $_.windowsdesktop } | Select-Object -First 1
$file = $release.windowsdesktop.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.exe' } | Select-Object -First 1
if (-not $file) {
    throw "No win-x64 Windows Desktop runtime installer in $metadataUrl"
}

$version = $release.windowsdesktop.version
$url = [string]$file.url
if (-not $url.StartsWith('https://builds.dotnet.microsoft.com/', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unexpected download host: $url"
}

$head = Invoke-WebRequest -UseBasicParsing -Method Head -Uri $url
$size = [long]($head.Headers['Content-Length'] | Select-Object -First 1)
if ($size -le 0) {
    throw "No size for $url"
}

$props = Join-Path $PSScriptRoot '..\installer\DotNetRuntime.props'
$text = Get-Content $props -Raw
$text = $text -replace '<DotNetRuntimeVersion>[^<]*</DotNetRuntimeVersion>', "<DotNetRuntimeVersion>$version</DotNetRuntimeVersion>"
$text = $text -replace '<DotNetRuntimeUrl>[^<]*</DotNetRuntimeUrl>', "<DotNetRuntimeUrl>$url</DotNetRuntimeUrl>"
$text = $text -replace '<DotNetRuntimeSha512>[^<]*</DotNetRuntimeSha512>', "<DotNetRuntimeSha512>$($file.hash.ToLowerInvariant())</DotNetRuntimeSha512>"
$text = $text -replace '<DotNetRuntimeSize>[^<]*</DotNetRuntimeSize>', "<DotNetRuntimeSize>$size</DotNetRuntimeSize>"
[IO.File]::WriteAllText((Resolve-Path $props), $text)

Write-Host "Pinned .NET Desktop Runtime $version ($([math]::Round($size / 1MB, 1)) MB, released $($release.'release-date'))"
