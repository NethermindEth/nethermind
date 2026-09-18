# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$controls = 0
foreach ($stem in @('source-map', 'upstream-artifacts', 'stage-dependencies')) {
    $document = Get-Content -LiteralPath (Join-Path $PSScriptRoot "$stem.json") -Raw
    $schema = Get-Content -LiteralPath (Join-Path $PSScriptRoot "$stem.schema.json") -Raw
    if (-not (Test-Json -Json $document -Schema $schema -ErrorAction Stop)) {
        throw "Invalid draft identity document: $stem"
    }

    $mutants = [System.Collections.Generic.List[string]]::new()
    $mutant = $document | ConvertFrom-Json -AsHashtable
    $mutant.acceptanceState = 'accepted'
    $mutants.Add(($mutant | ConvertTo-Json -Depth 30))
    $mutant = $document | ConvertFrom-Json -AsHashtable
    $mutant.files = @($mutant.files | Select-Object -Skip 1)
    $mutants.Add(($mutant | ConvertTo-Json -Depth 30))
    $mutant = $document | ConvertFrom-Json -AsHashtable
    $mutant.files[0].path = 'not/a/source.cs'
    $mutants.Add(($mutant | ConvertTo-Json -Depth 30))
    $mutant = $document | ConvertFrom-Json -AsHashtable
    $mutant.files[0].sha256 = '0' * 64
    $mutants.Add(($mutant | ConvertTo-Json -Depth 30))
    $mutant = $document | ConvertFrom-Json -AsHashtable
    $mutant.files[0].role = 'unrelated'
    $mutants.Add(($mutant | ConvertTo-Json -Depth 30))
    $mutant = $document | ConvertFrom-Json -AsHashtable
    $mutant.files = @($mutant.files) + @($mutant.files[0])
    $mutants.Add(($mutant | ConvertTo-Json -Depth 30))
    $mutant = $document | ConvertFrom-Json -AsHashtable
    $mutant.files[0] = $null
    $mutants.Add(($mutant | ConvertTo-Json -Depth 30))
    $mutant = $document | ConvertFrom-Json -AsHashtable
    $mutant.unexpected = $true
    $mutants.Add(($mutant | ConvertTo-Json -Depth 30))
    $mutant = $document | ConvertFrom-Json -AsHashtable
    $mutant.Remove('package')
    $mutants.Add(($mutant | ConvertTo-Json -Depth 30))
    $mutant = $document | ConvertFrom-Json -AsHashtable
    $first = $mutant.files[0]
    $mutant.files[0] = $mutant.files[1]
    $mutant.files[1] = $first
    $mutants.Add(($mutant | ConvertTo-Json -Depth 30))

    foreach ($json in $mutants) {
        if (Test-Json -Json $json -Schema $schema -ErrorAction SilentlyContinue) {
            throw "Draft schema accepted a negative control: $stem/$controls"
        }
        $controls++
    }
}
Write-Host "Draft identity schemas valid; $controls negative controls rejected. No source semantics admitted."
