# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param([string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$project = Join-Path $PSScriptRoot 'OrdinaryEvmCompletionExtractor.csproj'

& (Join-Path $PSScriptRoot 'Verify-DraftSchemas.ps1')
dotnet build $project -c Release -warnaserror -p:SaveDiskSpace=true -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Static identity auditor build failed.' }
dotnet run --project $project -c Release -p:SaveDiskSpace=true --no-build -- --audit $RepoRoot
if ($LASTEXITCODE -ne 0) { throw 'Static identity audit failed.' }

Push-Location $PSScriptRoot
try {
    lake --wfail build OrdinaryEvmCompletionExtractor.Refinement.OrdinaryEvmCompletion OrdinaryEvmCompletionExtractor.Vectors.OrdinaryEvmCompletionVectors
    if ($LASTEXITCODE -ne 0) { throw 'Draft stage-refinement elaboration failed.' }
    foreach ($file in @('Specification/OrdinaryEvmCompletion.lean', 'Refinement/OrdinaryEvmCompletion.lean', 'Vectors/OrdinaryEvmCompletionVectors.lean')) {
        lake env lean -DwarningAsError=true -DmaxHeartbeats=800000 $file
        if ($LASTEXITCODE -ne 0) { throw "Draft direct elaboration failed: $file" }
        if (Select-String -LiteralPath $file -Pattern '\b(sorry|admit|native_decide)\b|^\s*(axiom|opaque)\b') {
            throw "Draft contains a forbidden proof escape: $file"
        }
    }
}
finally { Pop-Location }

Write-Host 'Handwritten draft checks passed; generated artifacts and executable acceptance are not checked by this gate.'
