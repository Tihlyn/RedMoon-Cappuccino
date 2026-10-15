# Regenerates RedMoonCappuccino/Configuration.cs.example from the local (gitignored)
# Configuration.cs, swapping the server address defaults for placeholders and blanking the
# Gemini API key (never commit it; CI fills it from a secret).
# CI builds from the example, so it must carry every field the real file has.
#
#   -Check   only report whether the example is in sync (exit 1 when it isn't)

[CmdletBinding()]
param([switch]$Check)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $root 'RedMoonCappuccino/Configuration.cs'
$example = Join-Path $root 'RedMoonCappuccino/Configuration.cs.example'

if (!(Test-Path $source)) { throw "No $source to generate the example from." }

$text = [IO.File]::ReadAllText($source).Replace("`r`n", "`n")
$placeholders = [ordered]@{
    'WsServerAddress'     = 'ws://YOUR_SERVER_ADDRESS:PORT'
    'ChatWsServerAddress' = 'ws://YOUR_SERVER_ADDRESS:CHAT_PORT'
    'GeminiApiKey'        = ''
}
foreach ($name in $placeholders.Keys) {
    $pattern = "(public string $name \{ get; set; \} = )`"[^`"]*`""
    if ($text -notmatch $pattern) { throw "Configuration.cs has no '$name' default to replace." }
    $text = [regex]::Replace($text, $pattern, "`$1`"$($placeholders[$name])`"")
}

if ($text -match 'AIza[0-9A-Za-z_-]{20,}') { throw 'An API key is still in the generated example; refusing to write it.' }

$current = if (Test-Path $example) { [IO.File]::ReadAllText($example).Replace("`r`n", "`n") } else { '' }
if ($current -eq $text) {
    Write-Host 'Configuration.cs.example is in sync.'
    exit 0
}
if ($Check) {
    Write-Host 'Configuration.cs.example is out of date. Run: release.bat sync-config, then commit it.'
    exit 1
}
[IO.File]::WriteAllText($example, $text, (New-Object System.Text.UTF8Encoding($false)))
Write-Host 'Updated Configuration.cs.example; commit it.'
