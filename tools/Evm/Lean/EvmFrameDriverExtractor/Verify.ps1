# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param(
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "../../../..")),
    [string]$Configuration = "Release",
    [string]$Dotnet = "dotnet",
    [string]$Lake = "lake",
    [switch]$Operational
)

$ErrorActionPreference = "Stop"
$package = [System.IO.Path]::GetFullPath($PSScriptRoot)
$project = Join-Path $package "EvmFrameDriverExtractor.csproj"
$tests = Join-Path $package "Test/EvmFrameDriverExtractor.Test.csproj"
$checkedIn = Join-Path $package "Generated"
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
$scratch = Join-Path $tempRoot ("evm-frame-driver-verify-" + [Guid]::NewGuid().ToString("N"))

function Assert-OperationalHashClosure {
    param(
        [Parameter(Mandatory)] [string]$IrPath,
        [Parameter(Mandatory)] [string]$ManifestPath,
        [Parameter(Mandatory)] [string]$LeanPath
    )
    $ir = Get-Content -LiteralPath $IrPath -Raw | ConvertFrom-Json
    $irHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $IrPath).Hash.ToLowerInvariant()
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    if ($manifest.ir.sha256 -ne $irHash -or $manifest.ir.sha256 -match '^0+$') {
        throw "Stage F manifest does not carry the nonzero canonical IR hash."
    }
    $lean = Get-Content -LiteralPath $LeanPath -Raw
    $matches = [regex]::Matches($lean, 'def irSha256 : String := "([0-9a-f]{64})"')
    if ($matches.Count -ne 1 -or $matches[0].Groups[1].Value -ne $irHash -or
        $matches[0].Groups[1].Value -match '^0+$') {
        throw "Stage F Lean does not carry the nonzero canonical IR hash."
    }
    if ($ir.compilerReferences.Count -ne 330 -or $manifest.compilerReferences.Count -ne 330) {
        throw "Stage F compiler-reference identity closure is not the reviewed 330-entry projection."
    }
    if ($ir.loop.batchLimit -ne 1024 -or $manifest.loop.batchLimit -ne 1024 -or
        ($ir.loop.dispatchModes -join ',') -ne 'NoTrace,NoTraceCancelable,Traced,TracedCancelable' -or
        ($manifest.loop.dispatchModes -join ',') -ne 'NoTrace,NoTraceCancelable,Traced,TracedCancelable') {
        throw "Stage F manifest/IR loop identity is not the exact source-derived four-mode closure."
    }
}

if (-not $scratch.StartsWith($tempRoot + [System.IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Scratch directory escaped the temporary root."
}

try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    & $Dotnet build $tests -c $Configuration -p:SaveDiskSpace=true -warnaserror
    if ($LASTEXITCODE -ne 0) { throw "Stage E warning-as-error extractor build failed." }

    $testFilter = 'FullyQualifiedName~EvmFrameDriverExtractorTests&FullyQualifiedName!~.Operational_'
    $minimumTests = 22
    if ($Operational) {
        $testFilter = 'FullyQualifiedName~EvmFrameDriverExtractorTests'
        $minimumTests = 28
    }
    $testOutput = @(& $Dotnet test --project $tests -c $Configuration --no-build -- `
        --filter $testFilter --minimum-expected-tests $minimumTests --no-ansi --no-progress)
    $testOutput | Write-Output
    if ($LASTEXITCODE -ne 0) { throw "Frame driver focused mutation/determinism suite failed." }
    $testText = $testOutput -join "`n"
    $testCounts = @{}
    foreach ($name in @('total', 'failed', 'succeeded', 'skipped')) {
        $counts = [regex]::Matches($testText, "(?m)^\s*${name}:\s*(\d+)\s*$")
        if ($counts.Count -ne 1) { throw "Frame driver requires exactly one $name test count." }
        $testCounts[$name] = [int]$counts[0].Groups[1].Value
    }
    if ($testCounts.total -lt $minimumTests -or $testCounts.failed -ne 0 -or
        $testCounts.skipped -ne 0 -or $testCounts.succeeded -ne $testCounts.total) {
        throw 'Frame driver tests did not all pass without skips.'
    }

    foreach ($name in @("first", "second")) {
        $output = Join-Path $scratch $name
        New-Item -ItemType Directory -Path $output | Out-Null
        & $Dotnet run --project $project -c $Configuration --no-build -- `
            --repo-root $RepoRoot --output $output `
            --lean-output (Join-Path $output "EvmFrameDriverKernel.lean")
        if ($LASTEXITCODE -ne 0) { throw "Stage E $name extraction failed." }
    }

    foreach ($name in @(
        "EvmFrameDriverKernel.ir.json",
        "EvmFrameDriverKernel.source-manifest.json",
        "EvmFrameDriverKernel.lean"
    )) {
        $first = Join-Path $scratch "first/$name"
        $second = Join-Path $scratch "second/$name"
        if ((Get-FileHash -Algorithm SHA256 -LiteralPath $first).Hash -ne
            (Get-FileHash -Algorithm SHA256 -LiteralPath $second).Hash) {
            throw "Stage E nondeterministic artifact: $name."
        }
        $pinned = Join-Path $checkedIn $name
        if (-not (Test-Path -LiteralPath $pinned)) { throw "Missing checked-in Stage E artifact: $pinned." }
        if ((Get-FileHash -Algorithm SHA256 -LiteralPath $pinned).Hash -ne
            (Get-FileHash -Algorithm SHA256 -LiteralPath $second).Hash) {
            throw "Stage E checked-in artifact drift: $name."
        }
    }

    if ($Operational) {
        foreach ($name in @("first", "second")) {
            $output = Join-Path $scratch ("operational/" + $name)
            New-Item -ItemType Directory -Path $output | Out-Null
            & $Dotnet run --project $project -c $Configuration --no-build -- `
                --stage operational --repo-root $RepoRoot --output $output `
                --lean-output (Join-Path $output "EvmFrameDriverOperationalKernel.lean")
            if ($LASTEXITCODE -ne 0) { throw "Stage F $name extraction failed." }
        }

        foreach ($name in @(
            "EvmFrameDriverOperationalKernel.ir.json",
            "EvmFrameDriverOperationalKernel.source-manifest.json",
            "EvmFrameDriverOperationalKernel.lean"
        )) {
            $first = Join-Path $scratch ("operational/first/" + $name)
            $second = Join-Path $scratch ("operational/second/" + $name)
            if ((Get-FileHash -Algorithm SHA256 -LiteralPath $first).Hash -ne
                (Get-FileHash -Algorithm SHA256 -LiteralPath $second).Hash) {
                throw "Stage F nondeterministic artifact: $name."
            }
            $pinned = Join-Path $package ("Generated/Operational/" + $name)
            if (-not (Test-Path -LiteralPath $pinned)) { throw "Missing checked-in Stage F artifact: $pinned." }
            if ((Get-FileHash -Algorithm SHA256 -LiteralPath $pinned).Hash -ne
                (Get-FileHash -Algorithm SHA256 -LiteralPath $second).Hash) {
                throw "Stage F checked-in artifact drift: $name."
            }
        }

        Assert-OperationalHashClosure `
            (Join-Path $scratch "operational/second/EvmFrameDriverOperationalKernel.ir.json") `
            (Join-Path $scratch "operational/second/EvmFrameDriverOperationalKernel.source-manifest.json") `
            (Join-Path $scratch "operational/second/EvmFrameDriverOperationalKernel.lean")

        Push-Location $package
        try {
            foreach ($module in @(
                "Generated/Operational/EvmFrameDriverOperationalKernel.lean",
                "Specification/OperationalTypes.lean",
                "Specification/OperationalAdapters.lean",
                "Specification/OperationalReference.lean",
                "Specification/OperationalVectors.lean",
                "Specification/OperationalAdmissionWitnesses.lean",
                "Refinement/OperationalFrameDriver.lean"
            )) {
                & $Lake env lean -DwarningAsError=true $module
                if ($LASTEXITCODE -ne 0) { throw "Stage F direct Lean gate failed for $module." }
            }
        }
        finally { Pop-Location }
    }

    Push-Location $package
    try {
        & $Lake build -KwarningAsError=true `
            EvmFrameDriverExtractor.Generated.EvmFrameDriverKernel `
            EvmFrameDriverExtractor.Specification.Types `
            EvmFrameDriverExtractor.Specification.Reference `
            EvmFrameDriverExtractor.Specification.AdmissionWitnesses `
            EvmFrameDriverExtractor.Refinement.FrameDriver
        if ($LASTEXITCODE -ne 0) { throw "Stage E Lake warning-as-error gate failed." }
        foreach ($module in @(
            "Generated/EvmFrameDriverKernel.lean",
            "Specification/Types.lean",
            "Specification/Reference.lean",
            "Specification/AdmissionWitnesses.lean",
            "Refinement/FrameDriver.lean"
        )) {
            & $Lake env lean -DwarningAsError=true $module
            if ($LASTEXITCODE -ne 0) { throw "Stage E direct Lean gate failed for $module." }
        }
    }
    finally { Pop-Location }
}
finally {
    if (Test-Path -LiteralPath $scratch) {
        $resolved = [System.IO.Path]::GetFullPath($scratch)
        if (-not $resolved.StartsWith($tempRoot + [System.IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not [System.IO.Path]::GetFileName($resolved).StartsWith("evm-frame-driver-verify-",
                [StringComparison]::Ordinal)) {
            throw "Refusing cleanup outside the owned Stage E verification directory."
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
