# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param([string]$Lake = 'lake')

$ErrorActionPreference = 'Stop'
$temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$scratch = [System.IO.Directory]::CreateTempSubdirectory('stage-c-d-component-mutations-').FullName
$bridge = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Refinement/StageCFullPrecompileBridge.lean'))
$vectors = [System.IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Refinement/StageCFullPrecompileBridgeVectors.lean'))
$vectors = $vectors.Replace('import EvmFrameControlSettlementExtractor.Refinement.StageCFullPrecompileBridge', '')
$vectors = $vectors.Replace('import EvmFrameControlSettlementExtractor.Specification.AdmissionWitnesses', '')
$combined = "import EvmFrameControlSettlementExtractor.Specification.AdmissionWitnesses`n" + $bridge + "`n" + $vectors
$mutations = @(
    @{
        Name = 'substate-error-erased'
        Before = 'substateError := result.result.bind (fun frameResult => frameResult.substateError)'
        After = 'substateError := none'
    },
    @{
        Name = 'missing-native-becomes-success'
        Before = '| .missingNativeDependency => .missingNativeDependency'
        After = '| .missingNativeDependency => .success []'
    },
    @{
        Name = 'pricing-boundary-moved'
        Before = '| .pricingOutOfGas => 41'
        After = '| .pricingOutOfGas => 40'
    },
    @{
        Name = 'fail-closed-becomes-completed'
        Before = '{ termination := .incomplete'
        After = '{ termination := .completed'
    }
)

Push-Location $PSScriptRoot
try {
    $baseline = Join-Path $scratch 'Baseline.lean'
    [System.IO.File]::WriteAllText($baseline, $combined)
    & $Lake env lean -DwarningAsError=true $baseline
    if ($LASTEXITCODE -ne 0) { throw 'Combined Stage C/D component mutation baseline did not compile.' }

    foreach ($mutation in $mutations) {
        if (($combined.Split($mutation.Before, [StringSplitOptions]::None)).Count -ne 2) {
            throw "Expected one mutation anchor for $($mutation.Name)."
        }
        $target = Join-Path $scratch ($mutation.Name + '.lean')
        [System.IO.File]::WriteAllText($target, $combined.Replace($mutation.Before, $mutation.After))
        $diagnostics = & $Lake env lean -DwarningAsError=true $target 2>&1
        if ($LASTEXITCODE -eq 0) { throw "Lean accepted semantic mutation $($mutation.Name)." }
        if (-not (($diagnostics -join "`n") -match 'error:')) {
            throw "Mutation $($mutation.Name) failed without a Lean error: $($diagnostics -join "`n")"
        }
        Write-Output "Lean rejected semantic mutation: $($mutation.Name)"
    }
}
finally {
    Pop-Location
    $resolved = [System.IO.Path]::GetFullPath($scratch)
    if (-not $resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not [System.IO.Path]::GetFileName($resolved).StartsWith('stage-c-d-component-mutations-', [StringComparison]::Ordinal)) {
        throw 'Refusing cleanup outside the owned component mutation directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
