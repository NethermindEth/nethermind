# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param([string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')), [switch]$SkipBuild)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repo = [IO.Path]::GetFullPath($RepoRoot)
$verificationManifest = Get-Content -Raw -LiteralPath (Join-Path $repo 'tools/Evm/Lean/verification-manifest.json') | ConvertFrom-Json
$sourceRevisionId = [string] $verificationManifest.pins.nethermindCommit
if ($verificationManifest.schemaVersion -ne 1 -or $sourceRevisionId -notmatch '^[0-9a-f]{40}$') { throw 'Invalid verification manifest Nethermind pin.' }
$pathMapRoot = $repo.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
if ($pathMapRoot.IndexOfAny([char[]] @(',', ';', '=')) -ge 0) { throw "Repository path cannot be represented by PathMap: $pathMapRoot" }
$deterministicBuildProperties = @(
    "-p:SourceRevisionId=$sourceRevisionId", '-p:SourceDateEpoch=1789035784', '-p:EnableSourceLink=false',
    '-p:EmbedUntrackedSources=false', '-p:ContinuousIntegrationBuild=false', '-p:BuildingInsideVisualStudio=false', '-p:Deterministic=true',
    '-p:DeterministicSourcePaths=true', "-p:PathMap=$pathMapRoot=/_/"
)
$previousSourceDateEpoch = $env:SOURCE_DATE_EPOCH
$previousNodeReuse = $env:MSBUILDDISABLENODEREUSE
$project = Join-Path $PSScriptRoot 'OrdinaryEvmCompletionExtractor.csproj'
$tests = Join-Path $PSScriptRoot 'Test/OrdinaryEvmCompletionExtractor.Test.csproj'
$generated = Join-Path $PSScriptRoot 'Generated'
function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Candidate gate failed: $Command $Arguments" }
}
function Assert-TestSummary([string]$Output) {
    $counts = @{}
    foreach ($field in @('total', 'failed', 'succeeded', 'skipped')) {
        $matches = [regex]::Matches($Output, "(?m)^\s+$($field):\s+(\d+)\s*$")
        if ($matches.Count -ne 1) { throw 'Missing or duplicate candidate test summary.' }
        $counts[$field] = [int]$matches[0].Groups[1].Value
    }
    if ($counts.total -lt 46 -or $counts.failed -ne 0 -or $counts.skipped -ne 0 -or $counts.succeeded -ne $counts.total) {
        throw 'Candidate tests must all succeed without skips, at or above the 46-test floor.'
    }
}
try {
$env:SOURCE_DATE_EPOCH = '1789035784'
$env:MSBUILDDISABLENODEREUSE = '1'
$validSummary = "  total: 46`n  failed: 0`n  succeeded: 46`n  skipped: 0`n"
Assert-TestSummary $validSummary
foreach ($invalidSummary in @(
    $validSummary.Replace('succeeded: 46', 'succeeded: 45').Replace('skipped: 0', 'skipped: 1'),
    $validSummary.Replace('succeeded: 46', 'succeeded: 45').Replace('failed: 0', 'failed: 1'),
    ($validSummary + "  total: 46`n"),
    $validSummary.Replace('46', '45')
)) {
    $rejected = $false
    try { Assert-TestSummary $invalidSummary }
    catch { $rejected = $_.Exception.Message -in @('Missing or duplicate candidate test summary.', 'Candidate tests must all succeed without skips, at or above the 46-test floor.') }
    if (-not $rejected) { throw 'Candidate test-summary negative control survived.' }
}
if (-not $SkipBuild) {
    Invoke-Checked dotnet (@('build', (Join-Path $repo 'tools/Evm/Evm.csproj'), '-c', 'Release', '-warnaserror', '-p:SaveDiskSpace=true', '-nr:false', '-m:1') + $deterministicBuildProperties)
    Invoke-Checked dotnet (@('build', $tests, '-c', 'Release', '-warnaserror', '-p:SaveDiskSpace=true', '-nr:false', '-m:1') + $deterministicBuildProperties)
}
$testOutput = & dotnet run --project $tests -c Release --no-build -- 2>&1
$testExit = $LASTEXITCODE
$testOutput | Write-Output
if ($testExit -ne 0) { throw 'Candidate test executable failed.' }
Assert-TestSummary ($testOutput -join "`n")
Invoke-Checked dotnet @('run', '--project', $project, '-c', 'Release', '--no-build', '--', '--check', $RepoRoot, $generated)
& (Join-Path $PSScriptRoot 'Verify-DraftSchemas.ps1')
& (Join-Path $PSScriptRoot 'Verify-CandidateSchemas.ps1') -ArtifactDirectory $generated
Push-Location $PSScriptRoot
try {
    Invoke-Checked lake @('--wfail', 'build', 'OrdinaryEvmCompletionExtractor.Generated.OrdinaryEvmCompletion',
        'OrdinaryEvmCompletionExtractor.Refinement.OrdinaryEvmCompletion',
        'OrdinaryEvmCompletionExtractor.Refinement.SourceAttachedOrdinaryEvmCompletion',
        'OrdinaryEvmCompletionExtractor.Vectors.OrdinaryEvmCompletionVectors')
} finally { Pop-Location }
& (Join-Path $PSScriptRoot 'Verify-SemanticMutations.ps1')
& (Join-Path $PSScriptRoot 'Verify-ClosureAxioms.ps1')
Invoke-Checked dotnet @('run', '--project', $project, '-c', 'Release', '--no-build', '--', '--check', $RepoRoot, $generated)
Write-Output 'Conditional ordinary-completion checks passed; complete production preparation and VM execution remain outside this boundary.'
}
finally {
    $env:SOURCE_DATE_EPOCH = $previousSourceDateEpoch
    $env:MSBUILDDISABLENODEREUSE = $previousNodeReuse
}
