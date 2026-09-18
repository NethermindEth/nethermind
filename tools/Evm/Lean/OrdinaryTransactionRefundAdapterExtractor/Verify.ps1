# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')),
    [string]$Configuration = 'Release',
    [switch]$SkipBuild,
    [switch]$SkipLean
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$package = [IO.Path]::GetFullPath($PSScriptRoot)
$repo = [IO.Path]::GetFullPath($RepoRoot)
$project = Join-Path $package 'OrdinaryTransactionRefundAdapterExtractor.csproj'
$tests = Join-Path $package 'Test/OrdinaryTransactionRefundAdapterExtractor.Test.csproj'
$previousEpoch = $env:SOURCE_DATE_EPOCH
$previousNodeReuse = $env:MSBUILDDISABLENODEREUSE
$env:SOURCE_DATE_EPOCH = '1789035784'
$env:MSBUILDDISABLENODEREUSE = '1'

function Invoke-RefundChecked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Refund gate failed: $Command $Arguments" }
}

function Test-RefundArtifactSchemas {
    $checked = 0
    foreach ($kind in @('ir', 'source-manifest')) {
        $schema = Join-Path $package "Schema/ordinary-transaction-refund.$kind.schema.json"
        $json = Get-Content -Raw -LiteralPath (Join-Path $package "Generated/OrdinaryTransactionRefund.$kind.json")
        if (-not (Test-Json -Json $json -SchemaFile $schema -ErrorAction Stop)) { throw "Refund $kind schema baseline failed." }
        $mutations = @(
            @{ Name = 'schema-version'; Change = { param($d, $s) $d.schemaVersion = 0 } },
            @{ Name = 'extractor-version'; Change = { param($d, $s) $d.extractorVersion = 'unreviewed' } },
            @{ Name = 'acceptance-state'; Change = { param($d, $s) $d.acceptanceState = 'static-draft' } },
            @{ Name = 'unknown-field'; Change = { param($d, $s) $d.extra = $true } },
            @{ Name = 'missing-dependencies'; Change = { param($d, $s) $d.Remove('dependencies') } },
            @{ Name = 'null-source'; Change = { param($d, $s) $s[0] = $null } },
            @{ Name = 'source-order'; Change = { param($d, $s) $s[0], $s[1] = $s[1], $s[0] } },
            @{ Name = 'dependency-order'; Change = { param($d, $s) $d.dependencies[0], $d.dependencies[1] = $d.dependencies[1], $d.dependencies[0] } },
            @{ Name = 'source-hash'; Change = { param($d, $s) $s[0].sha256 = '0' * 64 } },
            @{ Name = 'source-role'; Change = { param($d, $s) $s[0].role = 'compiler-support' } }
        )
        for ($index = 0; $index -lt 157; $index++) {
            $slot = $index
            $mutations += @{ Name = "source-$slot"; Change = { param($d, $s) $s[$slot].path = 'wrong/source.cs' }.GetNewClosure() }
        }
        for ($index = 0; $index -lt 52; $index++) {
            $slot = $index
            $mutations += @{ Name = "dependency-$slot"; Change = { param($d, $s) $d.dependencies[$slot].path = 'wrong/dependency' }.GetNewClosure() }
        }
        if ($kind -eq 'ir') {
            for ($index = 0; $index -lt 226; $index++) {
                $slot = $index
                $mutations += @{ Name = "reference-$slot"; Change = { param($d, $s) $d.admission.admission.references[$slot].path = 'wrong/reference.dll' }.GetNewClosure() }
            }
            for ($index = 0; $index -lt 50; $index++) {
                $slot = $index
                $mutations += @{ Name = "expression-$slot"; Change = { param($d, $s) $d.admission.plan.expressions[$slot].role = 'wrong-role' }.GetNewClosure() }
            }
            $mutations += @(
                @{ Name = 'halt-order'; Change = { param($d, $s) $d.admission.plan.haltStages[0] = 'clearExecution' } },
                @{ Name = 'null-reference'; Change = { param($d, $s) $d.admission.admission.references[0] = $null } },
                @{ Name = 'reference-hash'; Change = { param($d, $s) $d.admission.admission.references[0].sha256 = 'invalid' } },
                @{ Name = 'reference-selection'; Change = { param($d, $s) $d.admission.admission.references[0].selected = -not $d.admission.admission.references[0].selected } }
            )
        } else {
            $mutations += @(
                @{ Name = 'ir-name'; Change = { param($d, $s) $d.ir.path = 'wrong-ir.json' } },
                @{ Name = 'lean-name'; Change = { param($d, $s) $d.lean.path = 'wrong-module.lean' } }
            )
        }
        foreach ($control in $mutations) {
            $document = $json | ConvertFrom-Json -AsHashtable
            $sources = if ($kind -eq 'ir') { $document.admission.admission.sources } else { $document.sources }
            $null = & $control.Change $document $sources
            if ($kind -eq 'ir') { $document.admission.admission.sources = $sources } else { $document.sources = $sources }
            $schemaErrors = @()
            $valid = Test-Json -Json ($document | ConvertTo-Json -Depth 100 -Compress) -SchemaFile $schema -ErrorAction SilentlyContinue -ErrorVariable schemaErrors
            if ($valid -or $schemaErrors.Count -eq 0 -or @($schemaErrors | Where-Object {
                $_.FullyQualifiedErrorId -ne 'InvalidJsonAgainstSchemaDetailed,Microsoft.PowerShell.Commands.TestJsonCommand'
            }).Count -ne 0) { throw "Refund $kind schema control failed: $($control.Name)." }
            $checked++
        }
    }
    if ($checked -ne 720) { throw "Refund schema-control discovery changed: $checked." }
    Write-Host "Verified both refund artifact schemas and all $checked schema negative controls."
}

try {
    if (-not $SkipBuild) {
        Invoke-RefundChecked dotnet @('build', $project, '-c', $Configuration, '-p:SaveDiskSpace=true', '-warnaserror', '-nr:false', '-m:1')
        Invoke-RefundChecked dotnet @('build', $tests, '-c', $Configuration, '-p:SaveDiskSpace=true', '-warnaserror', '-nr:false', '-m:1')
    }
    Invoke-RefundChecked dotnet @('run', '--project', $project, '-c', $Configuration, '--no-build', '--', '--check', $repo)
    if (-not $SkipBuild) {
        Invoke-RefundChecked dotnet @('test', '--project', $tests, '-c', $Configuration, '--no-build', '--', '--minimum-expected-tests', '104')
    }
    Test-RefundArtifactSchemas
    $scratch = [IO.Path]::GetFullPath((Join-Path $package ('.refund-determinism-' + [Guid]::NewGuid().ToString('N'))))
    if (-not $scratch.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refund scratch escaped package.' }
    try {
        foreach ($copy in @('first', 'second')) {
            Invoke-RefundChecked dotnet @('run', '--project', $project, '-c', $Configuration, '--no-build', '--', '--extract', $repo, (Join-Path $scratch $copy))
        }
        foreach ($suffix in @('.ir.json', '.lean', '.source-manifest.json')) {
            $hashes = @((Join-Path $scratch "first/OrdinaryTransactionRefund$suffix"), (Join-Path $scratch "second/OrdinaryTransactionRefund$suffix"),
                (Join-Path $package "Generated/OrdinaryTransactionRefund$suffix")) | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
            if ($hashes[0] -cne $hashes[1] -or $hashes[1] -cne $hashes[2]) { throw "Refund deterministic artifact mismatch: $suffix" }
        }
    }
    finally {
        $resolved = [IO.Path]::GetFullPath($scratch)
        if ($resolved.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolved)) {
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
    }
    if (-not $SkipLean) {
        Push-Location $package
        try {
            Invoke-RefundChecked lake @('--wfail', 'build')
            foreach ($source in @('Generated/OrdinaryTransactionRefund.lean', 'Specification/OrdinaryTransactionRefund.lean',
                'Refinement/OrdinaryTransactionRefund.lean', 'Vectors/OrdinaryTransactionRefundVectors.lean')) {
                Invoke-RefundChecked lake @('env', 'lean', '-DwarningAsError=true', $source)
            }
            & (Join-Path $package 'Verify-MutationGates.ps1')
            & (Join-Path $package 'Verify-Axioms.ps1')
        }
        finally { Pop-Location }
    }
    Invoke-RefundChecked dotnet @('run', '--project', $project, '-c', $Configuration, '--no-build', '--', '--check', $repo)
    if ($SkipBuild -or $SkipLean) {
        Write-Host "Only selected refund gates ran (SkipBuild=$SkipBuild, SkipLean=$SkipLean); no full verification claimed."
    } else {
        Write-Host 'Verified refund source admission, deterministic artifacts, tests, schemas, proofs, mutations and complete standard-only descendant census.'
    }
}
finally {
    $env:SOURCE_DATE_EPOCH = $previousEpoch
    $env:MSBUILDDISABLENODEREUSE = $previousNodeReuse
}
