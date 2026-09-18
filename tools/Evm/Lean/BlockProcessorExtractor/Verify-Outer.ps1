# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../../../..")),
    [string]$Configuration = "Release",
    [ValidateSet("finite", "publication", "all")][string]$Slice = "finite",
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
$modes = if ($Slice -eq "all") { @("finite", "publication") } else { @($Slice) }
$names = @{ finite = "NormalFiniteBranchCompletion"; publication = "BlockchainPublication" }

function Invoke-OuterChecked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Outer gate failed: $Command $Arguments" }
}

function Test-OuterSchemas([string]$Mode) {
    $name = $names[$Mode]
    $stem = if ($Mode -eq "finite") { "normal-finite-branch" } else { "blockchain-publication" }
    $dependencyCount = if ($Mode -eq "finite") { 22 } else { 21 }
    $controls = @(
        @{ Name = 'schema'; Change = { param($d) $d.schemaVersion = 2 } },
        @{ Name = 'version'; Change = { param($d) $d.extractorVersion = 'unsupported' } },
        @{ Name = 'name'; Change = { param($d) $d.name = 'other-slice' } },
        @{ Name = 'status'; Change = { param($d) $d.acceptanceState = 'static-draft' } },
        @{ Name = 'missing'; Change = { param($d) $d.Remove('sources') } },
        @{ Name = 'extra'; Change = { param($d) $d.extra = $true } },
        @{ Name = 'null-sources'; Change = { param($d) $d.sources = $null } },
        @{ Name = 'missing-source'; Change = { param($d) $d.sources = @($d.sources | Select-Object -Skip 1) } },
        @{ Name = 'extra-source'; Change = { param($d) $d.sources += $d.sources[0] } },
        @{ Name = 'null-source'; Change = { param($d) $d.sources[0] = $null } },
        @{ Name = 'source-order'; Change = { param($d) $d.sources[0], $d.sources[1] = $d.sources[1], $d.sources[0] } },
        @{ Name = 'source-extra'; Change = { param($d) $d.sources[0].extra = $true } },
        @{ Name = 'null-dependencies'; Change = { param($d) $d.dependencies = $null } },
        @{ Name = 'missing-dependency'; Change = { param($d) $d.dependencies = @($d.dependencies | Select-Object -Skip 1) } },
        @{ Name = 'extra-dependency'; Change = { param($d) $d.dependencies += $d.dependencies[0] } },
        @{ Name = 'null-dependency'; Change = { param($d) $d.dependencies[0] = $null } },
        @{ Name = 'dependency-order'; Change = { param($d) $d.dependencies[0], $d.dependencies[1] = $d.dependencies[1], $d.dependencies[0] } },
        @{ Name = 'dependency-extra'; Change = { param($d) $d.dependencies[0].extra = $true } }
    )
    $checked = 0
    foreach ($kind in @('ir', 'source-manifest')) {
        $schema = Join-Path $package "Schema/$stem.$kind.schema.json"
        $json = Get-Content -Raw -LiteralPath (Join-Path $package "Generated/$name.$kind.json")
        if (-not (Test-Json -Json $json -SchemaFile $schema -ErrorAction Stop)) { throw "$name $kind schema baseline failed." }
        $mutations = @($controls)
        if ($kind -eq 'source-manifest') {
            $mutations += @(
                @{ Name = 'ir-path'; Change = { param($d) $d.ir.path = 'wrong-artifact-name' } },
                @{ Name = 'lean-path'; Change = { param($d) $d.lean.path = 'wrong-artifact-name' } }
            )
        } else {
            $mutations += @(
                @{ Name = 'empty-target'; Change = { param($d) $d.sites[0].target = ' ' } },
                @{ Name = 'null-site'; Change = { param($d) $d.sites[0] = $null } },
                @{ Name = 'extra-site'; Change = { param($d) $d.sites[0].extra = $true } },
                @{ Name = 'missing-cfg'; Change = { param($d) $d.controlFlows = @($d.controlFlows | Select-Object -Skip 1) } },
                @{ Name = 'operation-order'; Change = { param($d) $d.operations[0], $d.operations[1] = $d.operations[1], $d.operations[0] } }
            )
        }
        foreach ($entry in @(
            @{ Field = 'sources'; Count = 23; Hash = $true },
            @{ Field = 'dependencies'; Count = $dependencyCount; Hash = $false }
        )) {
            for ($index = 0; $index -lt $entry.Count; $index++) {
                foreach ($property in $(if ($entry.Hash) { @('path', 'sha256') } else { @('path') })) {
                    $field = $entry.Field
                    $slot = $index
                    $member = $property
                    $change = {
                        param($d)
                        $d[$field][$slot][$member] = if ($member -eq 'path') { 'not/a/source.cs' } else { '0' * 64 }
                    }.GetNewClosure()
                    $mutations += @{ Name = "$field-$slot-$member"; Change = $change }
                }
            }
        }
        for ($index = 0; $index -lt 3; $index++) {
            $slot = $index
            $mutations += @{ Name = "upstream-hash-$slot"; Change = { param($d) $d.dependencies[$slot].sha256 = '0' * 64 }.GetNewClosure() }
        }
        foreach ($control in $mutations) {
            $mutated = $json | ConvertFrom-Json -AsHashtable
            $null = & $control.Change $mutated
            $schemaErrors = @()
            $valid = Test-Json -Json ($mutated | ConvertTo-Json -Depth 100 -Compress) -SchemaFile $schema -ErrorAction SilentlyContinue -ErrorVariable schemaErrors
            if ($valid -or $schemaErrors.Count -eq 0 -or @($schemaErrors | Where-Object {
                $_.FullyQualifiedErrorId -ne 'InvalidJsonAgainstSchemaDetailed,Microsoft.PowerShell.Commands.TestJsonCommand'
            }).Count -ne 0) { throw "$name $kind schema negative control failed: $($control.Name)." }
            $checked++
        }
    }
    $expected = 2 * ($controls.Count + 46 + $dependencyCount + 3) + 7
    if ($checked -ne $expected) { throw "Outer schema control discovery changed." }
    Write-Host "Verified both exact $name schemas and $checked negative controls."
}

try {
    if (-not $SkipBuild) {
        Invoke-OuterChecked dotnet @("build", $project, "-c", $Configuration, "-p:SaveDiskSpace=true", "-warnaserror", "-nr:false", "-m:1")
        Invoke-OuterChecked dotnet @("build", $tests, "-c", $Configuration, "-p:SaveDiskSpace=true", "-warnaserror", "-nr:false", "-m:1")
        $filter = if ($Slice -eq "all") { "FullyQualifiedName~OuterBlockTests" } else {
            "(FullyQualifiedName~OuterBlockTests)&(TestCategory=OuterShared|TestCategory=$($names[$Slice]))"
        }
        $minimumTests = if ($Slice -eq "all") { "104" } else { "72" }
        Invoke-OuterChecked dotnet @("test", "--project", $tests, "-c", $Configuration, "-p:SaveDiskSpace=true", "--no-build", "--",
            "--filter", $filter, "--minimum-expected-tests", $minimumTests)
    }
    Invoke-OuterChecked dotnet @("run", "--project", $project, "-c", $Configuration, "-p:SaveDiskSpace=true", "--no-build", "--",
        "--branch-check", "--repo-root", $repo, "--output", (Join-Path $package "Generated"))
    foreach ($mode in $modes) {
        Invoke-OuterChecked dotnet @("run", "--project", $project, "-c", $Configuration, "-p:SaveDiskSpace=true", "--no-build", "--",
            "--$mode-check", "--repo-root", $repo, "--output", (Join-Path $package "Generated"))
        Test-OuterSchemas $mode
    }

    $scratch = [IO.Path]::GetFullPath((Join-Path $package (".outer-verify-" + [Guid]::NewGuid().ToString("N"))))
    if (-not $scratch.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Outer deterministic-check scratch escaped the package."
    }
    try {
        New-Item -ItemType Directory -Path $scratch | Out-Null
        foreach ($copy in @("first", "second")) {
            foreach ($mode in $modes) {
                Invoke-OuterChecked dotnet @("run", "--project", $project, "-c", $Configuration, "-p:SaveDiskSpace=true", "--no-build", "--",
                    "--$mode-extract", "--repo-root", $repo, "--output", (Join-Path $scratch $copy))
            }
        }
        foreach ($mode in $modes) {
            foreach ($extension in @(".ir.json", ".lean", ".source-manifest.json")) {
                $name = $names[$mode] + $extension
                $hashes = @((Join-Path $scratch "first/$name"), (Join-Path $scratch "second/$name"), (Join-Path $package "Generated/$name")) |
                    ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
                if ($hashes[0] -ne $hashes[1] -or $hashes[1] -ne $hashes[2]) { throw "Outer deterministic/artifact comparison failed: $name" }
            }
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
            $targets = @("BlockProcessorExtractor.Vectors.FiniteBranchVectors", "BlockProcessorExtractor.Vectors.BranchAcceptedIterationVectors",
                "SequentialBlockPostTransactionFinalizationExtractor.Vectors.ProcessOneValidatedPublicationVectors",
                "SequentialBlockPostTransactionFinalizationExtractor.Vectors.SequentialBlockPostTransactionFinalizationVectors",
                "SequentialBlockTransactionFoldExtractor.Vectors.SequentialBlockTransactionFoldVectors")
            $sources = @("Generated/NormalFiniteBranchCompletion.lean", "Specification/NormalFiniteBranchCompletion.lean",
                "Refinement/NormalFiniteBranchCompletion.lean", "Vectors/FiniteBranchVectors.lean")
            if ($Slice -ne "finite") {
                $targets += "BlockProcessorExtractor.Vectors.OuterBlockVectors"
                $sources += @("Generated/BlockchainPublication.lean", "Specification/BlockchainPublication.lean",
                    "Refinement/BlockchainPublication.lean", "Vectors/OuterBlockVectors.lean")
            }
            Invoke-OuterChecked lake (@("--wfail", "build") + $targets)
            foreach ($source in $sources) { Invoke-OuterChecked lake @("env", "lean", "-DwarningAsError=true", $source) }
            & (Join-Path $package "Verify-OuterMutationGates.ps1") -Slice $Slice
            foreach ($mode in $modes) { & (Join-Path $package "Verify-OuterAxioms.ps1") -Slice $mode }
        }
        finally { Pop-Location }
    }
    foreach ($mode in $modes) {
        Invoke-OuterChecked dotnet @("run", "--project", $project, "-c", $Configuration, "-p:SaveDiskSpace=true", "--no-build", "--",
            "--$mode-check", "--repo-root", $repo, "--output", (Join-Path $package "Generated"))
    }
    if ($SkipBuild -or $SkipLean) {
        Write-Host "Verified selected $Slice outer gates only (SkipBuild=$SkipBuild, SkipLean=$SkipLean); omitted gates remain required."
    } else {
        Write-Host "Verified the complete conditional $Slice gate, deterministic artifacts, semantic mutations, and descendant axioms."
    }
}
finally {
    $env:SOURCE_DATE_EPOCH = $previousEpoch
    $env:MSBUILDDISABLENODEREUSE = $previousNodeReuse
}
