<#
.SYNOPSIS
    Publishes the current build as a GitHub Release, which is where MwbLite's update check looks.

.DESCRIPTION
    Uses the version in Directory.Build.props and the files build.cmd put in dist\:
      MwbLite-Setup-<version>.exe, MwbLite-<version>-x64.msi, SHA256SUMS.txt
    The release notes are that version's section of CHANGELOG.md. The release is tagged v<version> on the current
    commit, which must be pushed already. Needs the GitHub CLI (gh), signed in with access to the repository.

.EXAMPLE
    .\build.cmd
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\publish-release.ps1
#>
[CmdletBinding()]
param(
    [string]$Repo = 'nozinoz/MwbLite',
    [switch]$Draft
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$tag = "v$version"
$dist = Join-Path $root 'dist'
$files = @("MwbLite-Setup-$version.exe", "MwbLite-$version-x64.msi", 'SHA256SUMS.txt') | ForEach-Object { Join-Path $dist $_ }
foreach ($f in $files) {
    if (-not (Test-Path $f)) {
        throw "$f is missing. Run build.cmd first."
    }
}

if (git -C $root status --porcelain) {
    throw 'The working tree has uncommitted changes. Commit (and push) them first.'
}

$head = (git -C $root rev-parse HEAD).Trim()
$remoteHead = (git -C $root ls-remote origin refs/heads/main).Split("`t")[0]
if ($head -ne $remoteHead) {
    throw 'The current commit is not on origin/main. Push it first.'
}

# This version's section of CHANGELOG.md.
$changelog = Get-Content (Join-Path $root 'CHANGELOG.md') -Raw
$match = [regex]::Match($changelog, "(?ms)^## $([regex]::Escape($version))\s*$(.*?)(?=^## |\z)")
if (-not $match.Success) {
    throw "CHANGELOG.md has no section for $version."
}

$notes = Join-Path $env:TEMP "MwbLite-release-notes-$version.md"
$body = $match.Groups[1].Value.Trim() + "`n`n**Install:** download ``MwbLite-Setup-$version.exe`` and run it (it upgrades an existing install). Already on 0.8 or later? Settings > About > Update now.`n"
[IO.File]::WriteAllText($notes, $body)

$ghArgs = @('release', 'create', $tag) + $files + @('--repo', $Repo, '--target', $head, '--title', "MwbLite $version", '--notes-file', $notes)
if ($Draft) {
    $ghArgs += '--draft'
}

& gh @ghArgs
if ($LASTEXITCODE -ne 0) {
    throw "gh release create failed ($LASTEXITCODE)."
}

Remove-Item $notes
Write-Host "Published $tag."
