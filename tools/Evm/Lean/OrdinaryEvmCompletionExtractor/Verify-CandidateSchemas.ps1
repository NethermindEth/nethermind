# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param([Parameter(Mandatory)][string]$ArtifactDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$controls = 0
foreach ($kind in @('ir', 'source-manifest')) {
    $schema = Join-Path $PSScriptRoot "Schema/ordinary-evm-completion.$kind.schema.json"
    $text = Get-Content -Raw -LiteralPath (Join-Path $ArtifactDirectory "OrdinaryEvmCompletion.$kind.json")
    if (-not (Test-Json -Json $text -SchemaFile $schema -ErrorAction Stop)) { throw "Invalid candidate $kind baseline." }
    $original = $text | ConvertFrom-Json -AsHashtable
    $mutations = @(
        @{ Name = 'version'; Change = { param($d) $d.schemaVersion = 2 } },
        @{ Name = 'acceptance'; Change = { param($d) $d.acceptanceState = 'accepted' } },
        @{ Name = 'extractor'; Change = { param($d) $d.extractorVersion = '9.0.0' } },
        @{ Name = 'null-dependencies'; Change = { param($d) $d.dependencies = $null } },
        @{ Name = 'missing-dependency'; Change = { param($d) $d.dependencies = @($d.dependencies | Select-Object -Skip 1) } },
        @{ Name = 'extra-dependency'; Change = { param($d) $d.dependencies += $d.dependencies[0] } },
        @{ Name = 'reorder'; Change = { param($d) $d.dependencies[0], $d.dependencies[1] = $d.dependencies[1], $d.dependencies[0] } },
        @{ Name = 'extra-field'; Change = { param($d) $d.unexpected = 1 } },
        @{ Name = 'closure-hash'; Change = { param($d) $d.sourceClosure = 'not-a-hash' } }
    )
    for ($index = 0; $index -lt $original.dependencies.Count; $index++) {
        $slot = $index
        $mutations += @{ Name = "dependency-$slot"; Change = { param($d) $d.dependencies[$slot].path = 'wrong-dependency' }.GetNewClosure() }
    }
    for ($index = 0; $index -lt 157; $index++) {
        $slot = $index
        $mutations += @{ Name = "source-$slot"; Change = {
            param($d)
            $sources = if ($kind -eq 'ir') { $d.source.compiler.sources } else { $d.sources }
            $sources[$slot].path = 'wrong-source'
        }.GetNewClosure() }
    }
    if ($kind -eq 'ir') {
        for ($index = 0; $index -lt 226; $index++) {
            $slot = $index
            $mutations += @{ Name = "reference-$slot"; Change = { param($d) $d.source.compiler.references[$slot].mvid = '00000000-0000-0000-0000-000000000000' }.GetNewClosure() }
        }
        for ($index = 0; $index -lt 43; $index++) {
            $slot = $index
            $mutations += @{ Name = "site-$slot"; Change = { param($d) $d.source.plan.expressions[$slot].role = 'wrong-role' }.GetNewClosure() }
        }
        $mutations += @{ Name = 'stage-order'; Change = { param($d) $d.source.plan.stages[0] = 'refund' } }
    } else {
        $mutations += @{ Name = 'ir-name'; Change = { param($d) $d.ir.path = 'other.json' } }
        $mutations += @{ Name = 'lean-name'; Change = { param($d) $d.lean.path = 'Other.lean' } }
    }
    foreach ($mutation in $mutations) {
        $document = $text | ConvertFrom-Json -AsHashtable
        & $mutation.Change $document
        if (Test-Json -Json ($document | ConvertTo-Json -Depth 100 -Compress) -SchemaFile $schema -ErrorAction SilentlyContinue) {
            throw "Candidate schema admitted $kind mutation $($mutation.Name)."
        }
        $controls++
    }
}
Write-Output "Strict conditional-completion schemas passed $controls negative controls."
