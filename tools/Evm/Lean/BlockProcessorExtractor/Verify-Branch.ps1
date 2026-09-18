# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../../../..")),
    [string]$Configuration = "Release",
    [switch]$SkipBuild,
    [switch]$SkipLean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
$previousEpoch = $env:SOURCE_DATE_EPOCH
$previousNodeReuse = $env:MSBUILDDISABLENODEREUSE
$env:SOURCE_DATE_EPOCH = "1789035784"
$env:MSBUILDDISABLENODEREUSE = "1"
$package = [IO.Path]::GetFullPath($PSScriptRoot)
$repo = [IO.Path]::GetFullPath($RepoRoot)
$project = Join-Path $package "BlockProcessorExtractor.csproj"
$tests = Join-Path $package "Test/BlockProcessorExtractor.Test.csproj"
$upstream = Join-Path $repo "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor/SequentialBlockPostTransactionFinalizationExtractor.csproj"

function Invoke-BranchChecked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Branch gate failed: $Command $Arguments" }
}

function Test-BranchArtifactSchemas {
    $controls = @(
        @{ Name = 'schema-version'; Change = { param($d) $d.schemaVersion = 1 } },
        @{ Name = 'extractor-version'; Change = { param($d) $d.extractorVersion = 'unsupported' } },
        @{ Name = 'missing-field'; Change = { param($d) $d.Remove('sources') } },
        @{ Name = 'extra-field'; Change = { param($d) $d.extra = $true } },
        @{ Name = 'null-sources'; Change = { param($d) $d.sources = $null } },
        @{ Name = 'missing-source'; Change = { param($d) $d.sources = @($d.sources | Select-Object -Skip 1) } },
        @{ Name = 'extra-source'; Change = { param($d) $d.sources += $d.sources[0] } },
        @{ Name = 'null-source'; Change = { param($d) $d.sources[0] = $null } },
        @{ Name = 'source-order'; Change = { param($d) $d.sources[0], $d.sources[1] = $d.sources[1], $d.sources[0] } },
        @{ Name = 'source-extra-property'; Change = { param($d) $d.sources[0].extra = $true } },
        @{ Name = 'missing-publication-manifest'; Change = { param($d) $d.dependencies = @($d.dependencies | Select-Object -Skip 1) } },
        @{ Name = 'extra-dependency'; Change = { param($d) $d.dependencies += $d.dependencies[0] } },
        @{ Name = 'null-dependency'; Change = { param($d) $d.dependencies[0] = $null } },
        @{ Name = 'dependency-order'; Change = { param($d) $d.dependencies[0], $d.dependencies[1] = $d.dependencies[1], $d.dependencies[0] } },
        @{ Name = 'dependency-hash'; Change = { param($d) $d.dependencies[0].sha256 = '0' * 64 } },
        @{ Name = 'dependency-extra-property'; Change = { param($d) $d.dependencies[0].extra = $true } },
        @{ Name = 'null-compiler'; Change = { param($d) $d.compilerClosure = $null } },
        @{ Name = 'missing-support'; Change = { param($d) $d.compilerClosure.supportSources = @($d.compilerClosure.supportSources | Select-Object -Skip 1) } },
        @{ Name = 'extra-support'; Change = { param($d) $d.compilerClosure.supportSources += $d.compilerClosure.supportSources[0] } },
        @{ Name = 'support-order'; Change = { param($d) $d.compilerClosure.supportSources[0], $d.compilerClosure.supportSources[1] = $d.compilerClosure.supportSources[1], $d.compilerClosure.supportSources[0] } },
        @{ Name = 'source-role-swap'; Change = { param($d) $d.sources[0], $d.compilerClosure.supportSources[0] = $d.compilerClosure.supportSources[0], $d.sources[0] } },
        @{ Name = 'compiler-inventory'; Change = { param($d) $d.compilerClosure.inventory.path = 'not/an/inventory.json' } },
        @{ Name = 'compiler-aggregate'; Change = { param($d) $d.compilerClosure.aggregateSha256 = '0' * 64 } },
        @{ Name = 'missing-reference'; Change = { param($d) $d.compilerClosure.references = @($d.compilerClosure.references | Select-Object -Skip 1) } },
        @{ Name = 'null-reference'; Change = { param($d) $d.compilerClosure.references[0] = $null } },
        @{ Name = 'reference-mvid'; Change = { param($d) $d.compilerClosure.references[0].mvid = 'not-a-guid' } },
        @{ Name = 'reference-selection-count'; Change = { param($d) $d.compilerClosure.references[0].selected = -not $d.compilerClosure.references[0].selected } },
        @{ Name = 'reference-extra-property'; Change = { param($d) $d.compilerClosure.references[0].extra = $true } }
    )
    $checked = 0
    foreach ($kind in @('ir', 'source-manifest')) {
        $schema = Join-Path $package "Schema/branch-accepted-iteration.$kind.schema.json"
        $json = Get-Content -Raw -LiteralPath (Join-Path $package "Generated/BranchAcceptedIteration.$kind.json")
        if (-not (Test-Json -Json $json -SchemaFile $schema -ErrorAction Stop)) { throw "Branch $kind schema baseline failed." }
        $mutations = @($controls)
        if ($kind -eq 'source-manifest') {
            $mutations += @(
                @{ Name = 'ir-artifact-path'; Change = { param($d) $d.ir.path = 'wrong-artifact-name' } },
                @{ Name = 'lean-artifact-path'; Change = { param($d) $d.lean.path = 'wrong-artifact-name' } }
            )
        }
        foreach ($entry in @(
            @{ Field = 'dependencies'; Count = 82; Hash = $false },
            @{ Field = 'sources'; Count = 13; Hash = $true },
            @{ Field = 'supportSources'; Count = 4; Hash = $true }
        )) {
            for ($index = 0; $index -lt $entry.Count; $index++) {
                foreach ($property in $(if ($entry.Hash) { @('path', 'sha256') } else { @('path') })) {
                    $field = $entry.Field
                    $slot = $index
                    $member = $property
                    $change = {
                        param($d)
                        $array = if ($field -eq 'supportSources') { $d.compilerClosure.supportSources } else { $d[$field] }
                        $array[$slot][$member] = if ($member -eq 'path') { 'not/a/source.cs' } else { '0' * 64 }
                    }.GetNewClosure()
                    $mutations += @{ Name = "$field-$slot-$member"; Change = $change }
                }
            }
        }
        foreach ($control in $mutations) {
            $mutated = $json | ConvertFrom-Json -AsHashtable
            $null = & $control.Change $mutated
            $schemaErrors = @()
            $valid = Test-Json -Json ($mutated | ConvertTo-Json -Depth 100 -Compress) -SchemaFile $schema -ErrorAction SilentlyContinue -ErrorVariable schemaErrors
            if ($valid -or $schemaErrors.Count -eq 0 -or @($schemaErrors | Where-Object {
                $_.FullyQualifiedErrorId -ne 'InvalidJsonAgainstSchemaDetailed,Microsoft.PowerShell.Commands.TestJsonCommand'
            }).Count -ne 0) { throw "Branch $kind schema negative control failed: $($control.Name)." }
            $checked++
        }
    }
    if ($checked -ne 290) { throw "Branch schema negative-control discovery changed: $checked." }
    Write-Host "Verified both exact Branch artifact schemas and $checked schema negative controls."
}

try {
if (-not $SkipBuild) {
    Invoke-BranchChecked dotnet @("build", $project, "-c", $Configuration, "-p:SaveDiskSpace=true", "-warnaserror", "-nr:false", "-m:1")
    Invoke-BranchChecked dotnet @("build", $tests, "-c", $Configuration, "-p:SaveDiskSpace=true", "-warnaserror", "-nr:false", "-m:1")
    Invoke-BranchChecked dotnet @("test", "--project", $tests, "-c", $Configuration, "-p:SaveDiskSpace=true", "--no-build", "--",
        "--filter", "FullyQualifiedName~BranchAcceptedIterationTests", "--minimum-expected-tests", "106")
}

# This invokes the upstream package's own source/dependency check, not just a manifest-status test.
Invoke-BranchChecked dotnet @("run", "--project", $upstream, "-c", $Configuration, "-p:SaveDiskSpace=true", "--no-build", "--",
    "--publication-check", "--repo-root", $repo)
Invoke-BranchChecked dotnet @("run", "--project", $project, "-c", $Configuration, "-p:SaveDiskSpace=true", "--no-build", "--",
    "--branch-check", "--repo-root", $repo, "--output", (Join-Path $package "Generated"))
Test-BranchArtifactSchemas

$scratch = [IO.Path]::GetFullPath((Join-Path $package (".branch-verify-" + [Guid]::NewGuid().ToString("N"))))
if (-not $scratch.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Branch deterministic-check scratch escaped the package."
}
try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    foreach ($copy in @("first", "second")) {
        Invoke-BranchChecked dotnet @("run", "--project", $project, "-c", $Configuration, "-p:SaveDiskSpace=true", "--no-build", "--",
            "--branch-extract", "--repo-root", $repo, "--output", (Join-Path $scratch $copy))
    }
    foreach ($extension in @(".ir.json", ".lean", ".source-manifest.json")) {
        $name = "BranchAcceptedIteration" + $extension
        $hashes = @((Join-Path $scratch "first/$name"), (Join-Path $scratch "second/$name"), (Join-Path $package "Generated/$name")) |
            ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
        if ($hashes[0] -ne $hashes[1] -or $hashes[1] -ne $hashes[2]) { throw "Branch deterministic/artifact comparison failed: $name" }
    }
}
finally {
    $resolved = [IO.Path]::GetFullPath($scratch)
    if ($resolved.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}

if (-not $SkipLean) {
    Push-Location $package
    try {
        Invoke-BranchChecked lake @("--wfail", "build", "BlockProcessorExtractor.Vectors.BranchAcceptedIterationVectors",
            "SequentialBlockPostTransactionFinalizationExtractor.Vectors.ProcessOneValidatedPublicationVectors",
            "SequentialBlockPostTransactionFinalizationExtractor.Vectors.SequentialBlockPostTransactionFinalizationVectors",
            "SequentialBlockTransactionFoldExtractor.Vectors.SequentialBlockTransactionFoldVectors")
        foreach ($source in @("Generated/BranchAcceptedIteration.lean", "Specification/BranchAcceptedIteration.lean",
            "Refinement/BranchAcceptedIteration.lean", "Vectors/BranchAcceptedIterationVectors.lean")) {
            Invoke-BranchChecked lake @("env", "lean", "-DwarningAsError=true", $source)
        }
        & (Join-Path $package "Verify-BranchMutationGates.ps1")
        & (Join-Path $package "Verify-BranchAxioms.ps1")
    }
    finally { Pop-Location }
}
Invoke-BranchChecked dotnet @("run", "--project", $project, "-c", $Configuration, "-p:SaveDiskSpace=true", "--no-build", "--",
    "--branch-check", "--repo-root", $repo, "--output", (Join-Path $package "Generated"))
if ($SkipBuild -or $SkipLean) {
    Write-Host "Verified selected branch gates only (SkipBuild=$SkipBuild, SkipLean=$SkipLean); omitted gates remain required."
} else {
    Write-Host "Verified the conditional branch iteration, deterministic artifacts, semantic mutations, and complete descendant axioms."
}
}
finally {
    $env:SOURCE_DATE_EPOCH = $previousEpoch
    $env:MSBUILDDISABLENODEREUSE = $previousNodeReuse
}
