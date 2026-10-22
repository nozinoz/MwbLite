<#
.SYNOPSIS
    Regenerates the app icon (src\App\Logo.ico) and the installer images (installer\Assets) from the drawing code in
    src\App\Lite\Ui\LiteArt.cs. The outputs are committed; run this only after changing the artwork, then build again.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\make-art.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Get-ChildItem (Join-Path $root 'src\App\bin') -Recurse -Filter 'MwbLite.exe' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $exe) {
    throw 'Build first (build.cmd), so src\App\bin contains MwbLite.exe.'
}

$p = Start-Process -FilePath $exe.FullName -ArgumentList '--make-art', "`"$root`"" -Wait -PassThru
if ($p.ExitCode -ne 0) {
    throw "MwbLite.exe --make-art failed with exit code $($p.ExitCode)"
}

Get-Item (Join-Path $root 'src\App\Logo.ico'), (Join-Path $root 'installer\Assets\*.png') | Format-Table Name, Length, LastWriteTime -AutoSize
