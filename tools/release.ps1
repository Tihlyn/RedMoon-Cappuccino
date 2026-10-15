# Publishes a stable or testing build by pushing a tag; GitHub Actions does the build, the
# GitHub release and the pluginmaster.json update. Use release.bat at the repo root.
#
#   stable  <a.b.c.d>   bump <Version> in the csproj, commit, tag v<version>, push both atomically
#   testing <a.b.c.d>   tag HEAD as testing-<version> and push the tag (no commit, no version change)
#   sync-config         regenerate Configuration.cs.example from your local Configuration.cs
#
#   -DryRun             run every check, change nothing

[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('stable', 'testing', 'sync-config')]
    [string]$Channel,

    [Parameter(Position = 1)]
    [string]$Version,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Git {
    $output = & git @args
    if ($LASTEXITCODE -ne 0) { throw "git $args failed (exit $LASTEXITCODE)." }
    $output
}

function Invoke-Checked([string]$what, [scriptblock]$command) {
    Write-Host $what
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed; nothing was tagged or pushed." }
}

$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    if ($Channel -eq 'sync-config') {
        & (Join-Path $PSScriptRoot 'sync-config-example.ps1')
        exit $LASTEXITCODE
    }

    if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw 'Version must be major.minor.patch.revision, e.g. 6.3.3.0.'
    }
    if (@($Version.Split('.') | Where-Object { [long]$_ -gt 65534 }).Count) {
        throw 'Version components must fit a .NET assembly version (0..65534).'
    }

    if (Invoke-Git status --porcelain) { throw 'The working tree must be clean, including untracked files.' }
    Invoke-Checked 'Checking Configuration.cs.example is in sync...' { & (Join-Path $PSScriptRoot 'sync-config-example.ps1') -Check }

    Write-Host 'Fetching origin and tags...'
    Invoke-Git fetch origin --tags | Out-Null

    $csproj = Join-Path $root 'RedMoonCappuccino/RedMoonCappuccino.csproj'
    $csprojText = [IO.File]::ReadAllText($csproj)
    $current = ([xml]$csprojText).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if ([version]$Version -le [version]$current) { throw "Version must be greater than the current stable $current." }

    $tag = if ($Channel -eq 'stable') { "v$Version" } else { "testing-$Version" }
    if (Invoke-Git tag --list $tag) { throw "Tag $tag already exists." }

    if ($Channel -eq 'stable') {
        if ((Invoke-Git branch --show-current) -ne 'main') { throw 'Stable releases must be made from main.' }
        if ((Invoke-Git rev-parse HEAD) -ne (Invoke-Git rev-parse origin/main)) {
            throw 'main must exactly match origin/main. Pull or push your changes first.'
        }
    } else {
        # The tag must point at a commit CI can see.
        if (-not (Invoke-Git branch -r --contains HEAD)) { throw 'HEAD is not on any remote branch. Push it first.' }
    }

    Invoke-Checked 'Building the plugin...' { dotnet build RedMoonCappuccino/RedMoonCappuccino.csproj -c Release -nologo -v q "-p:Version=$Version" }
    Invoke-Checked 'Running PvP series checks...' { dotnet run --project tests/PvpSeriesChecks -c Release }
    # Local builds rewrite the tracked bin/ outputs; don't let them leak into the release commit.
    Invoke-Git checkout -- RedMoonCappuccino/bin | Out-Null
    if (Invoke-Git status --porcelain) { throw 'The build changed tracked files other than bin/; inspect them first.' }

    if ($DryRun) {
        Write-Host "Dry run: $tag passed every check. Nothing was committed, tagged or pushed."
        exit 0
    }

    if ($Channel -eq 'stable') {
        $newText = [regex]::Replace($csprojText, '<Version>[^<]*</Version>', "<Version>$Version</Version>", 1)
        [IO.File]::WriteAllText($csproj, $newText, (New-Object System.Text.UTF8Encoding($false)))
        Invoke-Git add -- RedMoonCappuccino/RedMoonCappuccino.csproj | Out-Null
        Invoke-Git commit -m "chore(release): $tag" | Out-Null
        Invoke-Git tag -a $tag -m "RedMoonCappuccino $tag" | Out-Null
        # Publish the commit and tag together so a rejected main update cannot release a tag alone.
        Invoke-Git push --atomic origin main "refs/tags/$tag" | Out-Null
        Write-Host "Pushed main and $tag. CI builds it, publishes the release and updates pluginmaster.json."
        Write-Host 'https://github.com/Tihlyn/RedMoon-Cappuccino/actions/workflows/release.yml'
    } else {
        Invoke-Git tag -a $tag -m "RedMoonCappuccino $tag (testing)" | Out-Null
        Invoke-Git push origin "refs/tags/$tag" | Out-Null
        Write-Host "Pushed $tag. CI builds it, publishes a prerelease and updates the testing channel."
        Write-Host 'https://github.com/Tihlyn/RedMoon-Cappuccino/actions/workflows/testing-release.yml'
    }
} finally {
    Pop-Location
}
