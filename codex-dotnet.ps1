# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $Command
)

$repoRoot = Split-Path -Parent $PSCommandPath

$env:DOTNET_CLI_HOME = Join-Path $repoRoot ".dotnet-cli"
$env:NUGET_PACKAGES = Join-Path $repoRoot ".nuget\packages"
$env:TMP = Join-Path $repoRoot ".tmp"
$env:TEMP = Join-Path $repoRoot ".tmp"
$env:MSBUILDDISABLENODEREUSE = "1"

New-Item -ItemType Directory -Force $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES, $env:TMP | Out-Null

if ($Command.Count -eq 0)
{
    Write-Host "Configured repo-local .NET state:"
    Write-Host "  DOTNET_CLI_HOME=$env:DOTNET_CLI_HOME"
    Write-Host "  NUGET_PACKAGES=$env:NUGET_PACKAGES"
    Write-Host "  TMP=$env:TMP"
    Write-Host "  TEMP=$env:TEMP"
    Write-Host "  MSBUILDDISABLENODEREUSE=$env:MSBUILDDISABLENODEREUSE"
    Write-Host ""
    Write-Host "Dot-source this script to persist the environment in the current shell:"
    Write-Host "  . .\codex-dotnet.ps1"
    Write-Host ""
    Write-Host "Or run a command under the configured environment:"
    Write-Host "  .\codex-dotnet.ps1 dotnet build .\src\Nethermind\Nethermind.slnx -m:1"
    exit 0
}

if ($Command.Count -gt 1)
{
    [string[]] $arguments = $Command[1..($Command.Count - 1)]
}
else
{
    [string[]] $arguments = @()
}

& $Command[0] @arguments
$exitCode = $LASTEXITCODE

if ($null -eq $exitCode)
{
    $exitCode = 0
}

exit $exitCode
