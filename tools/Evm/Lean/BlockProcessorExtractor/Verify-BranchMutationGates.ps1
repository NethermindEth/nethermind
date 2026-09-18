# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param([string]$Lake = "lake")
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
$package = [IO.Path]::GetFullPath($PSScriptRoot)
$scratch = [IO.Path]::GetFullPath((Join-Path $package (".branch-mutations-" + [Guid]::NewGuid().ToString("N"))))
if (-not $scratch.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Branch mutation scratch escaped the package."
}

$generated = Get-Content -Raw -LiteralPath (Join-Path $package "Generated/BranchAcceptedIteration.lean")
$support = @("Specification/BranchAcceptedIteration.lean", "Refinement/BranchAcceptedIteration.lean",
    "Vectors/BranchAcceptedIterationVectors.lean") | ForEach-Object { Get-Content -Raw -LiteralPath (Join-Path $package $_) }
$imports = @(
    "import SequentialBlockPostTransactionFinalizationExtractor.Generated.ProcessOneValidatedPublication",
    "import SequentialBlockPostTransactionFinalizationExtractor.Specification.ProcessOneValidatedPublication",
    "import SequentialBlockPostTransactionFinalizationExtractor.Refinement.ProcessOneValidatedPublication"
) -join "`n"

function Combined([string]$Kernel, [bool]$Proofs = $true) {
    $parts = @($Kernel)
    if ($Proofs) { $parts += $support }
    return $imports + "`n" + (($parts | ForEach-Object { [regex]::Replace($_, '(?m)^import [^\r\n]+\r?\n', '') }) -join "`n")
}

$mutations = @(
    @{ Name = "count-before-completion"; Before = "{ s with count := i.index + 1 }"; After = "{ s with count := i.index }" },
    @{ Name = "wrong-commit-number"; Before = ".commitTreeInvoked s.scope (suggestedNumber i)"; After = ".commitTreeInvoked s.scope (i.header s.processedBlock)" },
    @{ Name = "omit-suggested-signal"; Before = "suggestedSignal := some signal"; After = "suggestedSignal := none" },
    @{ Name = "wrong-checkpoint"; Before = "i.index % 64 == 0"; After = "i.index % 32 == 0" },
    @{ Name = "wrong-next-base"; Before = "nextBase := some (i.header s.processedBlock)"; After = "nextBase := some (suggestedHeader i)" },
    @{ Name = "skip-final-disposal"; Before = ".unsubscribe, .disposeFinally, .completionEvent"; After = ".unsubscribe, .clearPrefetch, .completionEvent" },
    @{ Name = "escape-becomes-return"; Before = "| _, _ => r"; After = "| _, _ => { r with outcome := .returnedBranch }" }
)

try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    Push-Location $package
    try {
        $baseline = Join-Path $scratch "Baseline.lean"
        [IO.File]::WriteAllText($baseline, (Combined $generated), [Text.UTF8Encoding]::new($false))
        & $Lake env lean -DwarningAsError=true -DmaxRecDepth=4096 -DmaxHeartbeats=800000 $baseline
        if ($LASTEXITCODE -ne 0) { throw "Branch proof-executing mutation baseline failed." }
        foreach ($mutation in $mutations) {
            $count = ([regex]::Matches($generated, [regex]::Escape($mutation.Before))).Count
            if ($count -ne 1) { throw "Mutation $($mutation.Name) must match one generated semantic anchor; got $count." }
            $changed = $generated.Replace($mutation.Before, $mutation.After)
            $kernelPath = Join-Path $scratch ($mutation.Name + "-kernel.lean")
            [IO.File]::WriteAllText($kernelPath, (Combined $changed $false), [Text.UTF8Encoding]::new($false))
            $kernelOutput = (& $Lake env lean -DwarningAsError=true $kernelPath 2>&1 | Out-String)
            if ($LASTEXITCODE -ne 0) { throw "Branch semantic mutation is not compile-valid: $($mutation.Name): $kernelOutput" }
            $path = Join-Path $scratch ($mutation.Name + ".lean")
            [IO.File]::WriteAllText($path, (Combined $changed), [Text.UTF8Encoding]::new($false))
            $output = (& $Lake env lean -DwarningAsError=true -DmaxRecDepth=4096 -DmaxHeartbeats=800000 $path 2>&1 | Out-String)
            if ($LASTEXITCODE -eq 0 -or $output -notmatch 'error:') { throw "Semantic mutation survived: $($mutation.Name). $output" }
            if ($output -match 'unknown (identifier|constant|module)|unexpected token|failed to synthesize|maximum recursion depth|maxRecDepth|maximum heartbeats|maximum number of steps|deterministic timeout' -or
                $output -notmatch 'unsolved goals|[Tt]ype mismatch|[Tt]actic .*failed|[Tt]actic .*evaluated|[Tt]actic .*proved that the[\s\S]*is false|application type mismatch') {
                throw "Mutation $($mutation.Name) failed outside the intended proof gate: $output"
            }
            Write-Host "Branch semantic proof mutation rejected: $($mutation.Name)"
        }
    }
    finally { Pop-Location }
}
finally {
    $resolved = [IO.Path]::GetFullPath($scratch)
    if ($resolved.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
