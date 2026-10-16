# CI: fails when the plugin package holds anything from the gear planner server (server/):
# its assemblies, game data snapshot or settings. The server is deployed on its own and must
# never ship inside the plugin zip.

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$zip = Join-Path $root 'RedMoonCappuccino/bin/Release/RedMoonCappuccino/latest.zip'
if (-not (Test-Path $zip)) { throw "No plugin package at $zip; build the plugin first." }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $entries = $archive.Entries | ForEach-Object { $_.FullName }
} finally {
    $archive.Dispose()
}
$leaks = $entries | Where-Object { $_ -match '(^|/)(Rmc\.|rmc-|appsettings)|(^|/)server/' }
if ($leaks) {
    throw "Plugin package contains gear planner server files:`n$($leaks -join "`n")"
}
Write-Host "Plugin package clean: $($entries.Count) entries, no server files."
