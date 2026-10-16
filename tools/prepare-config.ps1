# CI only: creates RedMoonCappuccino/Configuration.cs from the committed example, filling the
# server addresses from the RMC_WS_SERVER_ADDRESS / RMC_CHAT_WS_SERVER_ADDRESS secrets, and the
# optional RMC_GEMINI_API_KEY secret into the empty GeminiApiKey default, and the optional
# RMC_PLANNER_SERVER_ADDRESS secret into the gear planner server address (without it the planner
# runs locally only, so it's never required).
# Without the secrets (forks, PRs) the placeholders stay; the build still compiles.
#
#   -Require   fail instead (release builds: a placeholder address would ship a broken plugin)

[CmdletBinding()]
param([switch]$Require)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$example = Join-Path $root 'RedMoonCappuccino/Configuration.cs.example'
$target = Join-Path $root 'RedMoonCappuccino/Configuration.cs'

$text = [IO.File]::ReadAllText($example)
$values = @{
    'ws://YOUR_SERVER_ADDRESS:PORT'      = $env:RMC_WS_SERVER_ADDRESS
    'ws://YOUR_SERVER_ADDRESS:CHAT_PORT' = $env:RMC_CHAT_WS_SERVER_ADDRESS
}
foreach ($placeholder in $values.Keys) {
    if ($values[$placeholder]) {
        $text = $text.Replace($placeholder, $values[$placeholder])
    } elseif ($Require) {
        throw "Secret for $placeholder is not set; refusing to build a release with a placeholder server address."
    } else {
        Write-Host "::warning::No secret for $placeholder; the build keeps the placeholder address."
    }
}
if ($env:RMC_PLANNER_SERVER_ADDRESS) {
    $text = $text.Replace('https://YOUR_PLANNER_SERVER_ADDRESS', $env:RMC_PLANNER_SERVER_ADDRESS)
} else {
    Write-Host "::warning::No RMC_PLANNER_SERVER_ADDRESS secret; the gear planner will run locally only."
}
if ($env:RMC_GEMINI_API_KEY) {
    $text = $text.Replace('public string GeminiApiKey { get; set; } = "";', "public string GeminiApiKey { get; set; } = `"$env:RMC_GEMINI_API_KEY`";")
}
[IO.File]::WriteAllText($target, $text, (New-Object System.Text.UTF8Encoding($false)))
