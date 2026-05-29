<#
.SYNOPSIS
    Sync canonical agent skills into tool-specific directories (Windows).

.DESCRIPTION
    `.agents/skills/` is the single source of truth. Claude Code reads
    `.claude/skills/` and Cursor reads `.cursor/skills/`; neither reads
    `.agents/skills/` directly, so this script materializes real copies there.
    GitHub Copilot and other AGENTS.md-aware tools read `.agents/skills/`
    natively and need no copy.

    Real copies (not symlinks) are used deliberately: Git symlinks are not
    reliably checked out on Windows without core.symlinks=true and OS-level
    symlink privilege, which silently degrades the link to a text stub.

    Pass -Check to fail when copies drift from the source (used by CI).
#>
[CmdletBinding()]
param(
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path "$PSScriptRoot/..").Path
$src = Join-Path $repoRoot '.agents/skills'
$targets = @('.claude/skills', '.cursor/skills')

if (-not (Test-Path $src)) {
    Write-Error "source skills directory not found: $src"
}

function Populate([string]$dst) {
    if (Test-Path $dst) { Remove-Item -Recurse -Force $dst }
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Get-ChildItem -Directory $src | ForEach-Object {
        Copy-Item -Recurse -Force $_.FullName (Join-Path $dst $_.Name)
    }
}

if ($Check) {
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ([System.IO.Path]::GetRandomFileName())
    try {
        $inSync = $true
        foreach ($target in $targets) {
            $expected = Join-Path $tmp $target
            Populate $expected
            $actual = Join-Path $repoRoot $target
            $a = Get-ChildItem -Recurse -File $expected | ForEach-Object {
                $rel = $_.FullName.Substring($expected.Length)
                "$rel`:$((Get-FileHash $_.FullName -Algorithm SHA256).Hash)"
            } | Sort-Object
            $b = if (Test-Path $actual) {
                Get-ChildItem -Recurse -File $actual | ForEach-Object {
                    $rel = $_.FullName.Substring($actual.Length)
                    "$rel`:$((Get-FileHash $_.FullName -Algorithm SHA256).Hash)"
                } | Sort-Object
            } else { @() }
            if (Compare-Object $a $b) {
                Write-Host "error: $target is out of sync with .agents/skills/." -ForegroundColor Red
                $inSync = $false
            }
        }
    } finally {
        if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
    }
    if (-not $inSync) {
        Write-Error 'tool skill copies are out of sync with .agents/skills/. Run scripts/sync-skills.ps1 and commit the result.'
    }
    Write-Output 'Skills are in sync.'
} else {
    foreach ($target in $targets) {
        Populate (Join-Path $repoRoot $target)
    }
    Write-Output "Synced .agents/skills/ -> $($targets -join ', ')"
}
