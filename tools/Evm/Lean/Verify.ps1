# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:MSBUILDDISABLENODEREUSE = '1'
$env:SOURCE_DATE_EPOCH = '1789035784'

function Invoke-CheckedNative
{
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,
        [Parameter(Mandatory)]
        [string[]] $Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "$FilePath exited with code $LASTEXITCODE."
    }
}

function Assert-NativeRefusalWithoutOutput
{
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,
        [Parameter(Mandatory)]
        [string[]] $Arguments,
        [Parameter(Mandatory)]
        [string] $ExpectedMessage,
        [Parameter(Mandatory)]
        [string] $ForbiddenOutputPath
    )

    if (Test-Path -LiteralPath $ForbiddenOutputPath)
    {
        throw "Refusal output path already exists: $ForbiddenOutputPath"
    }

    $ErrorActionPreference = 'Continue'
    $PSNativeCommandUseErrorActionPreference = $false
    [string[]] $commandOutput = @(& $FilePath @Arguments 2>&1 | ForEach-Object { $_.ToString() })
    [int] $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0)
    {
        throw "$FilePath unexpectedly accepted a refused operation."
    }

    [string] $outputText = [string]::Join("`n", $commandOutput)
    if ($outputText.IndexOf($ExpectedMessage, [StringComparison]::Ordinal) -lt 0)
    {
        throw "$FilePath did not report the expected refusal: $ExpectedMessage"
    }

    if (Test-Path -LiteralPath $ForbiddenOutputPath)
    {
        throw "Refused operation created output: $ForbiddenOutputPath"
    }
}

function Assert-FileBytesEqual
{
    param(
        [Parameter(Mandatory)]
        [string] $ExpectedPath,
        [Parameter(Mandatory)]
        [string] $ActualPath
    )

    if (-not (Test-Path -LiteralPath $ExpectedPath -PathType Leaf))
    {
        throw "Expected file is missing: $ExpectedPath"
    }

    if (-not (Test-Path -LiteralPath $ActualPath -PathType Leaf))
    {
        throw "Actual file is missing: $ActualPath"
    }

    [byte[]] $expectedBytes = [System.IO.File]::ReadAllBytes($ExpectedPath)
    [byte[]] $actualBytes = [System.IO.File]::ReadAllBytes($ActualPath)
    if ($expectedBytes.Length -ne $actualBytes.Length)
    {
        throw "File length differs: $ActualPath"
    }

    for ([int] $index = 0; $index -lt $expectedBytes.Length; $index++)
    {
        if ($expectedBytes[$index] -ne $actualBytes[$index])
        {
            throw "File differs at byte ${index}: $ActualPath"
        }
    }
}

function Assert-FileSha256
{
    param(
        [Parameter(Mandatory)]
        [string] $Path,
        [Parameter(Mandatory)]
        [string] $ExpectedSha256
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        throw "Hash-pinned file is missing: $Path"
    }

    [string] $actualSha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha256 -cne $ExpectedSha256)
    {
        throw "SHA-256 differs for ${Path}: expected $ExpectedSha256; found $actualSha256."
    }
}

function Assert-BlsFp2ToG2Fixtures
{
    param(
        [Parameter(Mandatory)]
        [string] $RepositoryRoot,
        [Parameter(Mandatory)]
        [string] $LeanDirectory
    )

    [string] $successPath = Join-Path $RepositoryRoot 'src\Nethermind\Nethermind.Evm.Test\PrecompileVectors\Bls\map_fp2_to_G2_bls.json'
    [string] $failurePath = Join-Path $RepositoryRoot 'src\Nethermind\Nethermind.Evm.Test\PrecompileVectors\Bls\fail-map_fp2_to_G2_bls.json'
    [string] $vectorsPath = Join-Path $LeanDirectory 'Eip803x\Precompiles\Bls12381Fp2ToG2Vectors.lean'

    [hashtable] $expectedHashes = @{
        $successPath = '00f39be8b922fa22a0d0f20b23dd270b9fe0882c5066873228492eea8eb520c8'
        $failurePath = '101e4e994c1c265a8f49b1ea220f7ee9e9b208a63473fcd86a55a0d5e4a2bd37'
    }
    foreach ($path in $expectedHashes.Keys)
    {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf))
        {
            throw "BLS12_MAP_FP2_TO_G2 fixture is missing: $path"
        }

        [string] $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -cne $expectedHashes[$path])
        {
            throw "BLS12_MAP_FP2_TO_G2 fixture hash differs: $path"
        }
    }

    if (-not (Test-Path -LiteralPath $vectorsPath -PathType Leaf))
    {
        throw "BLS12_MAP_FP2_TO_G2 Lean vectors are missing: $vectorsPath"
    }

    [object[]] $successVectors = @(Get-Content -LiteralPath $successPath -Raw | ConvertFrom-Json)
    [object[]] $failureVectors = @(Get-Content -LiteralPath $failurePath -Raw | ConvertFrom-Json)
    if ($successVectors.Count -ne 5 -or $failureVectors.Count -ne 5)
    {
        throw 'BLS12_MAP_FP2_TO_G2 fixture counts differ from the pinned five-success/five-failure inventory.'
    }

    [string] $leanSource = [System.IO.File]::ReadAllText($vectorsPath)
    [string[]] $successInputDefinitions = @('inputOneHex', 'inputTwoHex', 'inputThreeHex', 'inputFourHex', 'inputFiveHex')
    [string[]] $successExpectedDefinitions = @('expectedOneHex', 'expectedTwoHex', 'expectedThreeHex', 'expectedFourHex', 'expectedFiveHex')
    [string[]] $successOracleDefinitions = @('oracleOneHex', 'oracleTwoHex', 'oracleThreeHex', 'oracleFourHex', 'oracleFiveHex')
    [string[]] $failureInputDefinitions = @('emptyInputHex', 'shortInputHex', 'longInputHex', 'topBytesInputHex', 'invalidFieldInputHex')
    [string[]] $successBindingDefinitions = @('successAssetOne', 'successAssetTwo', 'successAssetThree', 'successAssetFour', 'successAssetFive')
    [string[]] $failureBindingDefinitions = @('failureAssetEmpty', 'failureAssetShort', 'failureAssetLong', 'failureAssetTopBytes', 'failureAssetInvalidField')

    [hashtable] $literalValues = @{}
    foreach ($definitionName in @($successInputDefinitions + $successExpectedDefinitions + $successOracleDefinitions + $failureInputDefinitions))
    {
        [string] $pattern = '(?ms)^\s*def\s+' + [regex]::Escape($definitionName) + '\s*:\s*String\s*:=\s*"([0-9a-f]*)"\s*$'
        [System.Text.RegularExpressions.MatchCollection] $literalMatches = [regex]::Matches($leanSource, $pattern)
        if ($literalMatches.Count -ne 1)
        {
            throw "BLS12_MAP_FP2_TO_G2 fixture literal '$definitionName' is missing, malformed, aliased, or duplicated."
        }
        $literalValues[$definitionName] = $literalMatches[0].Groups[1].Value
    }

    for ([int] $index = 0; $index -lt $successVectors.Count; $index++)
    {
        if ($literalValues[$successInputDefinitions[$index]] -cne [string] $successVectors[$index].Input -or
            $literalValues[$successExpectedDefinitions[$index]] -cne [string] $successVectors[$index].Expected -or
            $literalValues[$successOracleDefinitions[$index]] -cne [string] $successVectors[$index].Expected)
        {
            throw "BLS12_MAP_FP2_TO_G2 success fixture transcription differs at index $index."
        }

        [string] $bindingPattern = '(?ms)^\s*def\s+' + [regex]::Escape($successBindingDefinitions[$index]) +
            '\s*:\s*SuccessAssetBinding\s*:=\s*\{(?<body>.*?)\}\s*(?=^\s*def\s+)'
        [System.Text.RegularExpressions.MatchCollection] $bindingMatches = [regex]::Matches($leanSource, $bindingPattern)
        if ($bindingMatches.Count -ne 1)
        {
            throw "BLS12_MAP_FP2_TO_G2 success binding '$($successBindingDefinitions[$index])' is missing or duplicated."
        }
        [string] $bindingBody = $bindingMatches[0].Groups['body'].Value
        [string] $expectedNamePattern = '\bname\s*:=\s*"' + [regex]::Escape([string] $successVectors[$index].Name) + '"'
        [string] $expectedInputPattern = '\binputHex\s*:=\s*' + [regex]::Escape($successInputDefinitions[$index]) + '\b'
        [string] $expectedOutputPattern = '\boutputHex\s*:=\s*' + [regex]::Escape($successExpectedDefinitions[$index]) + '\b'
        [string] $expectedGasPattern = '\bgas\s*:=\s*' + [regex]::Escape([string] $successVectors[$index].Gas) + '\b'
        if ($bindingBody -notmatch $expectedNamePattern -or $bindingBody -notmatch $expectedInputPattern -or
            $bindingBody -notmatch $expectedOutputPattern -or $bindingBody -notmatch $expectedGasPattern)
        {
            throw "BLS12_MAP_FP2_TO_G2 success record association differs at index $index."
        }
    }

    [hashtable] $modeledFailureErrors = @{
        'invalid input length' = '.invalidInputLength'
        'invalid field element top bytes' = '.invalidFieldElementTopBytes'
        'invalid fp.Element encoding' = '.invalidFieldElement'
    }
    for ([int] $index = 0; $index -lt $failureVectors.Count; $index++)
    {
        if ($literalValues[$failureInputDefinitions[$index]] -cne [string] $failureVectors[$index].Input)
        {
            throw "BLS12_MAP_FP2_TO_G2 failure fixture transcription differs at index $index."
        }

        [string] $expectedErrorText = [string] $failureVectors[$index].ExpectedError
        if (-not $modeledFailureErrors.ContainsKey($expectedErrorText))
        {
            throw "BLS12_MAP_FP2_TO_G2 fixture carries an unmapped failure error at index $index."
        }
        [string] $bindingPattern = '(?ms)^\s*def\s+' + [regex]::Escape($failureBindingDefinitions[$index]) +
            '\s*:\s*FailureAssetBinding\s*:=\s*\{(?<body>.*?)\}\s*(?=^\s*def\s+)'
        [System.Text.RegularExpressions.MatchCollection] $bindingMatches = [regex]::Matches($leanSource, $bindingPattern)
        if ($bindingMatches.Count -ne 1)
        {
            throw "BLS12_MAP_FP2_TO_G2 failure binding '$($failureBindingDefinitions[$index])' is missing or duplicated."
        }
        [string] $bindingBody = $bindingMatches[0].Groups['body'].Value
        [string] $expectedNamePattern = '\bname\s*:=\s*"' + [regex]::Escape([string] $failureVectors[$index].Name) + '"'
        [string] $expectedInputPattern = '\binputHex\s*:=\s*' + [regex]::Escape($failureInputDefinitions[$index]) + '\b'
        [string] $expectedTextPattern = '\bexpectedErrorText\s*:=\s*"' + [regex]::Escape($expectedErrorText) + '"'
        [string] $expectedModelPattern = '\bexpectedError\s*:=\s*' + [regex]::Escape($modeledFailureErrors[$expectedErrorText]) + '\b'
        if ($bindingBody -notmatch $expectedNamePattern -or $bindingBody -notmatch $expectedInputPattern -or
            $bindingBody -notmatch $expectedTextPattern -or $bindingBody -notmatch $expectedModelPattern)
        {
            throw "BLS12_MAP_FP2_TO_G2 failure record association or error mapping differs at index $index."
        }
    }

    [string[]] $expectedNames = @($successVectors | ForEach-Object { [string] $_.Name }) +
        @($failureVectors | ForEach-Object { [string] $_.Name })
    foreach ($expectedName in $expectedNames)
    {
        [string] $namePattern = '(?m)\bname\s*:=\s*"' + [regex]::Escape($expectedName) + '"'
        if ([regex]::Matches($leanSource, $namePattern).Count -ne 1)
        {
            throw "BLS12_MAP_FP2_TO_G2 fixture name '$expectedName' is missing or duplicated."
        }
    }
}

function Assert-ProcessingCoverageReapBeforePersistentCommit
{
    param(
        [Parameter(Mandatory)]
        [string] $ProcessingCoveragePath
    )

    try
    {
        $processingCoverage = Get-Content -LiteralPath $ProcessingCoveragePath -Raw | ConvertFrom-Json
    }
    catch
    {
        throw "Unable to parse processing coverage '$ProcessingCoveragePath': $($_.Exception.Message)"
    }

    [object[]] $phases = @($processingCoverage.block.phases | Where-Object { $_.name -ceq 'storage and state roots' })
    if ($phases.Count -ne 1)
    {
        throw 'Processing coverage does not contain exactly one storage and state roots phase.'
    }

    [object[]] $sources = @($phases[0].steps | ForEach-Object { $_.sources })
    [object[]] $reapAnchors = @($sources | Where-Object { $_.id -ceq 'eip161-reap-empty-accounts' })
    [object[]] $persistentCommitAnchors = @($sources | Where-Object { $_.id -ceq 'persistent-storage-commit' })
    if ($reapAnchors.Count -ne 1 -or $persistentCommitAnchors.Count -ne 1)
    {
        throw 'Processing coverage does not contain exactly one EIP-161 reap and persistent-storage commit anchor.'
    }

    $reapAnchor = $reapAnchors[0]
    $persistentCommitAnchor = $persistentCommitAnchors[0]
    if ($reapAnchor.path -cne 'src/Nethermind/Nethermind.State/WorldState.cs' -or
        $persistentCommitAnchor.path -cne 'src/Nethermind/Nethermind.State/WorldState.cs' -or
        [int] $reapAnchor.line -ge [int] $persistentCommitAnchor.line)
    {
        throw 'Processing coverage does not prove EIP-161 reaping precedes persistent storage commit in WorldState.Commit.'
    }
}

function Assert-SystemTransactionRoutingSourceManifest
{
    param(
        [Parameter(Mandatory)]
        [string] $ManifestPath,
        [Parameter(Mandatory)]
        [string] $RepositoryRoot,
        [Parameter(Mandatory)]
        [string] $PinnedCommit
    )

    try
    {
        $routingManifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    }
    catch
    {
        throw "Unable to parse system-transaction routing source manifest '$ManifestPath': $($_.Exception.Message)"
    }

    if ($routingManifest.schemaVersion -ne 1 -or $routingManifest.ancestorBaselineCommit -cne $PinnedCommit)
    {
        throw "System-transaction routing manifest does not carry the verification manifest's ancestor baseline pin."
    }

    if ($routingManifest.sourceIdentityAuthority -cne
        'The source-manifest SHA-256 identities are authoritative for the admitted current source; the ancestor baseline records lineage only.')
    {
        throw "System-transaction routing manifest has changed source-identity semantics."
    }

    [string[]] $expectedSourcePaths = @(
        'src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionRoutingKernel.cs',
        'src/Nethermind/Nethermind.Evm/TransactionProcessing/ExecutionOptions.cs',
        'src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs',
        'src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionProcessor.cs',
        'src/Nethermind/Nethermind.Core/TransactionExtensions.cs',
        'src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs',
        'src/Nethermind/Nethermind.Core/ContainerBuilderExtensions.cs'
    )
    if (@($routingManifest.sources).Count -ne $expectedSourcePaths.Count)
    {
        throw "System-transaction routing source manifest has an unexpected source count."
    }

    [object[]] $actualSources = @($routingManifest.sources)
    for ([int] $index = 0; $index -lt $expectedSourcePaths.Count; $index++)
    {
        if (([string] $actualSources[$index].path).Replace('\', '/') -cne $expectedSourcePaths[$index])
        {
            throw "System-transaction routing source manifest has an unexpected source set or order."
        }
    }

    foreach ($source in @($routingManifest.sources))
    {
        [string] $relativePath = $source.path
        [string] $absolutePath = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot $relativePath))
        [string] $relativeCheck = [System.IO.Path]::GetRelativePath($RepositoryRoot, $absolutePath)
        if ([System.IO.Path]::IsPathRooted($relativePath) -or
            $relativeCheck -eq '..' -or
            $relativeCheck.StartsWith("..$([System.IO.Path]::DirectorySeparatorChar)", [System.StringComparison]::Ordinal))
        {
            throw "System-transaction routing source manifest escapes the repository root: $relativePath"
        }

        if (-not (Test-Path -LiteralPath $absolutePath -PathType Leaf))
        {
            throw "System-transaction routing source manifest names a missing source: $relativePath"
        }

        [string] $actualHash = (Get-FileHash -LiteralPath $absolutePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -cne ([string] $source.sha256))
        {
            throw "System-transaction routing source identity differs for $relativePath."
        }
    }
}

function Assert-NoLeanPlaceholders
{
    param(
        [Parameter(Mandatory)]
        [string] $LeanDirectory
    )

    [System.IO.FileInfo[]] $leanSources = Get-ChildItem -LiteralPath $LeanDirectory -Filter '*.lean' -File -Recurse |
        Where-Object { $_.FullName -notmatch '[\\/]\.lake[\\/]' }

    foreach ($source in $leanSources)
    {
        [Microsoft.PowerShell.Commands.MatchInfo] $match = Select-String -LiteralPath $source.FullName -Pattern '\b(?:sorry|admit|axiom)\b' -CaseSensitive |
            Select-Object -First 1
        if ($null -ne $match)
        {
            throw "Forbidden Lean placeholder at $($source.FullName):$($match.LineNumber)."
        }
    }
}

function Assert-ExactLines
{
    param(
        [Parameter(Mandatory)]
        [string[]] $Expected,
        [Parameter(Mandatory)]
        [string[]] $Actual
    )

    if ($Actual.Count -ne $Expected.Count)
    {
        throw "Expected $($Expected.Count) formal command responses, received $($Actual.Count)."
    }

    for ([int] $index = 0; $index -lt $Expected.Count; $index++)
    {
        if ($Actual[$index] -cne $Expected[$index])
        {
            throw "Formal command response $($index + 1) does not match the pinned vector."
        }
    }
}

function Invoke-CanonicalNdjson
{
    param(
        [Parameter(Mandatory)]
        [string] $FilePath,
        [Parameter(Mandatory)]
        [string[]] $Arguments,
        [Parameter(Mandatory)]
        [string] $OutputPath,
        [string[]] $InputLines
    )

    [string[]] $lines = if ($null -eq $InputLines)
    {
        @(& $FilePath @Arguments)
    }
    else
    {
        @($InputLines | & $FilePath @Arguments)
    }

    if ($LASTEXITCODE -ne 0)
    {
        throw "$FilePath exited with code $LASTEXITCODE."
    }

    # Canonical LF framing makes the byte gate independent of host console newline conventions.
    [string] $content = if ($lines.Count -eq 0)
    {
        [string]::Empty
    }
    else
    {
        [string]::Join("`n", $lines) + "`n"
    }
    [System.IO.File]::WriteAllText($OutputPath, $content, [System.Text.UTF8Encoding]::new($false))
}

function Get-SystemTemporaryDirectory
{
    [string] $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([char[]] @('\', '/'))
    [string] $temporaryDirectory = [System.IO.Path]::GetFullPath((Join-Path $temporaryRoot ("nethermind-eip803x-verify-" + [System.Guid]::NewGuid().ToString('N'))))
    if (-not [string]::Equals(
        [System.IO.Path]::GetDirectoryName($temporaryDirectory),
        $temporaryRoot,
        [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Refusing to create a verification directory outside the system temporary directory."
    }

    New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
    return $temporaryDirectory
}

function Remove-SystemTemporaryDirectory
{
    param(
        [Parameter(Mandatory)]
        [string] $TemporaryDirectory,
        [Parameter(Mandatory)]
        [string] $TemporaryRoot
    )

    if (-not (Test-Path -LiteralPath $TemporaryDirectory))
    {
        return
    }

    [string] $fullTemporaryDirectory = [System.IO.Path]::GetFullPath($TemporaryDirectory)
    if (-not [string]::Equals(
        [System.IO.Path]::GetDirectoryName($fullTemporaryDirectory),
        $TemporaryRoot,
        [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Refusing to remove a verification directory outside the system temporary directory."
    }

    Remove-Item -LiteralPath $fullTemporaryDirectory -Recurse -Force
}

[string] $repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
[string] $leanDirectory = $PSScriptRoot
[string] $manifestPath = Join-Path $leanDirectory 'verification-manifest.json'
[string] $extractorProject = Join-Path $leanDirectory 'Extractor\Extractor.csproj'
[string] $extractorTestProject = Join-Path $leanDirectory 'Extractor\Test\Extractor.Test.csproj'
[string] $generatedDirectory = Join-Path $leanDirectory 'Extractor\Generated'
[string] $systemTransactionRoutingExtractorProject = Join-Path $leanDirectory 'SystemTransactionRoutingExtractor\SystemTransactionRoutingExtractor.csproj'
[string] $systemTransactionRoutingExtractorTestProject = Join-Path $leanDirectory 'SystemTransactionRoutingExtractor\Test\SystemTransactionRoutingExtractor.Test.csproj'
[string] $systemTransactionRoutingGeneratedDirectory = Join-Path $leanDirectory 'SystemTransactionRoutingExtractor\Generated'
[string] $environmentOpcodeExtractorProject = Join-Path $leanDirectory 'EnvironmentOpcodeExtractor\EnvironmentOpcodeExtractor.csproj'
[string] $environmentOpcodeExtractorTestProject = Join-Path $leanDirectory 'EnvironmentOpcodeExtractor\Test\EnvironmentOpcodeExtractor.Test.csproj'
[string] $environmentOpcodeGeneratedDirectory = Join-Path $leanDirectory 'EnvironmentOpcodeExtractor\Generated'
[string] $extendedStackOpcodeExtractorProject = Join-Path $leanDirectory 'ExtendedStackOpcodeExtractor\ExtendedStackOpcodeExtractor.csproj'
[string] $extendedStackOpcodeExtractorTestProject = Join-Path $leanDirectory 'ExtendedStackOpcodeExtractor\Test\ExtendedStackOpcodeExtractor.Test.csproj'
[string] $extendedStackOpcodeGeneratedDirectory = Join-Path $leanDirectory 'ExtendedStackOpcodeExtractor\Generated'
[string] $keccak256OpcodeExtractorProject = Join-Path $leanDirectory 'Keccak256OpcodeExtractor\Keccak256OpcodeExtractor.csproj'
[string] $keccak256OpcodeExtractorTestProject = Join-Path $leanDirectory 'Keccak256OpcodeExtractor\Test\Keccak256OpcodeExtractor.Test.csproj'
[string] $keccak256OpcodeGeneratedDirectory = Join-Path $leanDirectory 'Keccak256OpcodeExtractor\Generated'
[string] $accountReadOpcodeExtractorProject = Join-Path $leanDirectory 'AccountReadOpcodeExtractor\AccountReadOpcodeExtractor.csproj'
[string] $accountReadOpcodeExtractorTestProject = Join-Path $leanDirectory 'AccountReadOpcodeExtractor\Test\AccountReadOpcodeExtractor.Test.csproj'
[string] $accountReadOpcodeGeneratedDirectory = Join-Path $leanDirectory 'AccountReadOpcodeExtractor\Generated'
[string] $logOpcodeExtractorProject = Join-Path $leanDirectory 'LogOpcodeExtractor\LogOpcodeExtractor.csproj'
[string] $logOpcodeExtractorTestProject = Join-Path $leanDirectory 'LogOpcodeExtractor\Test\LogOpcodeExtractor.Test.csproj'
[string] $logOpcodeGeneratedDirectory = Join-Path $leanDirectory 'LogOpcodeExtractor\Generated'
[string] $memoryControlOpcodeExtractorProject = Join-Path $leanDirectory 'MemoryControlOpcodeExtractor\MemoryControlOpcodeExtractor.csproj'
[string] $memoryControlOpcodeExtractorTestProject = Join-Path $leanDirectory 'MemoryControlOpcodeExtractor\Test\MemoryControlOpcodeExtractor.Test.csproj'
[string] $memoryControlOpcodeGeneratedDirectory = Join-Path $leanDirectory 'MemoryControlOpcodeExtractor\Generated'
[string] $memoryCopyOpcodeExtractorProject = Join-Path $leanDirectory 'MemoryCopyOpcodeExtractor\MemoryCopyOpcodeExtractor.csproj'
[string] $memoryCopyOpcodeExtractorTestProject = Join-Path $leanDirectory 'MemoryCopyOpcodeExtractor\Test\MemoryCopyOpcodeExtractor.Test.csproj'
[string] $memoryCopyOpcodeGeneratedDirectory = Join-Path $leanDirectory 'MemoryCopyOpcodeExtractor\Generated'
[string] $controlFlowOpcodeExtractorProject = Join-Path $leanDirectory 'ControlFlowOpcodeExtractor\ControlFlowOpcodeExtractor.csproj'
[string] $controlFlowOpcodeExtractorTestProject = Join-Path $leanDirectory 'ControlFlowOpcodeExtractor\Test\ControlFlowOpcodeExtractor.Test.csproj'
[string] $controlFlowOpcodeGeneratedDirectory = Join-Path $leanDirectory 'ControlFlowOpcodeExtractor\Generated'
[string] $callDataLoadOpcodeExtractorProject = Join-Path $leanDirectory 'CallDataLoadOpcodeExtractor\CallDataLoadOpcodeExtractor.csproj'
[string] $callDataLoadOpcodeExtractorTestProject = Join-Path $leanDirectory 'CallDataLoadOpcodeExtractor\Test\CallDataLoadOpcodeExtractor.Test.csproj'
[string] $callDataLoadOpcodeGeneratedDirectory = Join-Path $leanDirectory 'CallDataLoadOpcodeExtractor\Generated'
[string] $evmFrameMachineExtractorProject = Join-Path $leanDirectory 'EvmFrameMachineExtractor\EvmFrameMachineExtractor.csproj'
[string] $evmFrameMachineExtractorTestProject = Join-Path $leanDirectory 'EvmFrameMachineExtractor\Test\EvmFrameMachineExtractor.Test.csproj'
[string] $evmFrameMachineGeneratedDirectory = Join-Path $leanDirectory 'EvmFrameMachineExtractor\Generated'
[string] $evmFrameControlSettlementExtractorProject = Join-Path $leanDirectory 'EvmFrameControlSettlementExtractor\EvmFrameControlSettlementExtractor.csproj'
[string] $evmFrameControlSettlementExtractorTestProject = Join-Path $leanDirectory 'EvmFrameControlSettlementExtractor\Test\EvmFrameControlSettlementExtractor.Test.csproj'
[string] $evmFrameControlSettlementGeneratedDirectory = Join-Path $leanDirectory 'EvmFrameControlSettlementExtractor\Generated'
[string] $evmFrameDriverExtractorProject = Join-Path $leanDirectory 'EvmFrameDriverExtractor\EvmFrameDriverExtractor.csproj'
[string] $evmFrameDriverExtractorTestProject = Join-Path $leanDirectory 'EvmFrameDriverExtractor\Test\EvmFrameDriverExtractor.Test.csproj'
[string] $evmFrameDriverGeneratedDirectory = Join-Path $leanDirectory 'EvmFrameDriverExtractor\Generated'
[string] $callCreateOpcodeExtractorProject = Join-Path $leanDirectory 'CallCreateOpcodeExtractor\CallCreateOpcodeExtractor.csproj'
[string] $callCreateOpcodeExtractorTestProject = Join-Path $leanDirectory 'CallCreateOpcodeExtractor\Test\CallCreateOpcodeExtractor.Test.csproj'
[string] $callCreateOpcodeGeneratedDirectory = Join-Path $leanDirectory 'CallCreateOpcodeExtractor\Generated'
[string] $worldJournalExtractorProject = Join-Path $leanDirectory 'WorldJournalExtractor\WorldJournalExtractor.csproj'
[string] $worldJournalExtractorTestProject = Join-Path $leanDirectory 'WorldJournalExtractor\Test\WorldJournalExtractor.Test.csproj'
[string] $worldJournalGeneratedDirectory = Join-Path $leanDirectory 'WorldJournalExtractor\Generated'
[string] $frameJournalExtractorProject = Join-Path $leanDirectory 'FrameJournalExtractor\FrameJournalExtractor.csproj'
[string] $frameJournalExtractorTestProject = Join-Path $leanDirectory 'FrameJournalExtractor\Test\FrameJournalExtractor.Test.csproj'
[string] $frameJournalGeneratedDirectory = Join-Path $leanDirectory 'FrameJournalExtractor\Generated'
[string] $precompileFrameExtractorProject = Join-Path $leanDirectory 'PrecompileFrameExtractor\PrecompileFrameExtractor.csproj'
[string] $precompileFrameExtractorTestProject = Join-Path $leanDirectory 'PrecompileFrameExtractor\Test\PrecompileFrameExtractor.Test.csproj'
[string] $precompileFrameGeneratedDirectory = Join-Path $leanDirectory 'PrecompileFrameExtractor\Generated'
[string] $precompileFullFrameExtractorProject = Join-Path $leanDirectory 'PrecompileFullFrameExtractor\PrecompileFullFrameExtractor.csproj'
[string] $precompileFullFrameExtractorTestProject = Join-Path $leanDirectory 'PrecompileFullFrameExtractor\Test\PrecompileFullFrameExtractor.Test.csproj'
[string] $precompileFullFrameGeneratedDirectory = Join-Path $leanDirectory 'PrecompileFullFrameExtractor\Generated'
[string] $pureWordOpcodeExtractorProject = Join-Path $leanDirectory 'PureWordOpcodeExtractor\PureWordOpcodeExtractor.csproj'
[string] $pureWordOpcodeExtractorTestProject = Join-Path $leanDirectory 'PureWordOpcodeExtractor\Test\PureWordOpcodeExtractor.Test.csproj'
[string] $pureWordOpcodeGeneratedDirectory = Join-Path $leanDirectory 'PureWordOpcodeExtractor\Generated'
[string] $pushOpcodeExtractorProject = Join-Path $leanDirectory 'PushOpcodeExtractor\PushOpcodeExtractor.csproj'
[string] $pushOpcodeExtractorTestProject = Join-Path $leanDirectory 'PushOpcodeExtractor\Test\PushOpcodeExtractor.Test.csproj'
[string] $pushOpcodeGeneratedDirectory = Join-Path $leanDirectory 'PushOpcodeExtractor\Generated'
[string] $stackRearrangementOpcodeExtractorProject = Join-Path $leanDirectory 'StackRearrangementOpcodeExtractor\StackRearrangementOpcodeExtractor.csproj'
[string] $stackRearrangementOpcodeExtractorTestProject = Join-Path $leanDirectory 'StackRearrangementOpcodeExtractor\StackRearrangementOpcodeExtractor.Test\StackRearrangementOpcodeExtractor.Test.csproj'
[string] $stackRearrangementOpcodeGeneratedDirectory = Join-Path $leanDirectory 'StackRearrangementOpcodeExtractor\Generated'
[string] $persistentStorageOpcodeExtractorProject = Join-Path $leanDirectory 'PersistentStorageOpcodeExtractor\PersistentStorageOpcodeExtractor.csproj'
[string] $persistentStorageOpcodeExtractorTestProject = Join-Path $leanDirectory 'PersistentStorageOpcodeExtractor\Test\PersistentStorageOpcodeExtractor.Test.csproj'
[string] $persistentStorageOpcodeGeneratedDirectory = Join-Path $leanDirectory 'PersistentStorageOpcodeExtractor\Generated'
[string] $transientStorageOpcodeExtractorProject = Join-Path $leanDirectory 'TransientStorageOpcodeExtractor\TransientStorageOpcodeExtractor.csproj'
[string] $transientStorageOpcodeExtractorTestProject = Join-Path $leanDirectory 'TransientStorageOpcodeExtractor\Test\TransientStorageOpcodeExtractor.Test.csproj'
[string] $transientStorageOpcodeGeneratedDirectory = Join-Path $leanDirectory 'TransientStorageOpcodeExtractor\Generated'
[string] $transactionProcessorExtractorProject = Join-Path $leanDirectory 'TransactionProcessorExtractor\TransactionProcessorExtractor.csproj'
[string] $transactionProcessorExtractorTestProject = Join-Path $leanDirectory 'TransactionProcessorExtractor\Test\TransactionProcessorExtractor.Test.csproj'
[string] $transactionProcessorGeneratedDirectory = Join-Path $leanDirectory 'TransactionProcessorExtractor\Generated'
[string] $ordinaryStaticAdmissionExtractorProject = Join-Path $leanDirectory 'OrdinaryStaticAdmissionExtractor\OrdinaryStaticAdmissionExtractor.csproj'
[string] $ordinaryStaticAdmissionExtractorTestProject = Join-Path $leanDirectory 'OrdinaryStaticAdmissionExtractor\Test\OrdinaryStaticAdmissionExtractor.Test.csproj'
[string] $ordinaryStaticAdmissionGeneratedDirectory = Join-Path $leanDirectory 'OrdinaryStaticAdmissionExtractor\Generated'
[string] $ordinaryStatefulAdmissionPrefixExtractorProject = Join-Path $leanDirectory 'OrdinaryStatefulAdmissionPrefixExtractor\OrdinaryStatefulAdmissionPrefixExtractor.csproj'
[string] $ordinaryStatefulAdmissionPrefixExtractorTestProject = Join-Path $leanDirectory 'OrdinaryStatefulAdmissionPrefixExtractor\Test\OrdinaryStatefulAdmissionPrefixExtractor.Test.csproj'
[string] $ordinaryStatefulAdmissionPrefixGeneratedDirectory = Join-Path $leanDirectory 'OrdinaryStatefulAdmissionPrefixExtractor\Generated'
[string] $blockProcessorExtractorProject = Join-Path $leanDirectory 'BlockProcessorExtractor\BlockProcessorExtractor.csproj'
[string] $blockProcessorExtractorTestProject = Join-Path $leanDirectory 'BlockProcessorExtractor\Test\BlockProcessorExtractor.Test.csproj'
[string] $blockProcessorGeneratedDirectory = Join-Path $leanDirectory 'BlockProcessorExtractor\Generated'
[string] $authorizationStateGasFoldExtractorProject = Join-Path $leanDirectory 'AuthorizationStateGasFoldExtractor\AuthorizationStateGasFoldExtractor.csproj'
[string] $authorizationStateGasFoldExtractorTestProject = Join-Path $leanDirectory 'AuthorizationStateGasFoldExtractor\Test\AuthorizationStateGasFoldExtractor.Test.csproj'
[string] $authorizationStateGasFoldGeneratedDirectory = Join-Path $leanDirectory 'AuthorizationStateGasFoldExtractor\Generated'
[string] $authorizationStateGasFoldRefinement = Join-Path $leanDirectory 'AuthorizationStateGasFoldExtractor\Refinement\AuthorizationStateGasFold.lean'
[string] $synchronousBlockPipelineMachineExtractorProject = Join-Path $leanDirectory 'SynchronousBlockPipelineMachineExtractor\SynchronousBlockPipelineMachineExtractor.csproj'
[string] $synchronousBlockPipelinePreparationBoundaryTest = Join-Path $leanDirectory 'SynchronousBlockPipelineMachineExtractor\Test\PreparationBoundary.Tests.ps1'
[string] $generatedChargeLeanArtifact = Join-Path $leanDirectory 'Eip803x\Generated\StateGasChargeKernel.lean'
[string] $generatedTransitionLeanArtifact = Join-Path $leanDirectory 'Eip803x\Generated\StateGasTransitionKernel.lean'
[string] $generatedStateGasTransitionAdapterLeanArtifact = Join-Path $leanDirectory 'Eip803x\Generated\StateGasTransitionAdapterKernel.lean'
[string] $generatedTransactionGasInitializationLeanArtifact = Join-Path $leanDirectory 'Eip803x\Generated\TransactionGasInitializationKernel.lean'
[string] $generatedTransactionSettlementLeanArtifact = Join-Path $leanDirectory 'Eip803x\Generated\TransactionSettlementKernel.lean'
[string] $generatedBlockGasInclusionLeanArtifact = Join-Path $leanDirectory 'Eip803x\Generated\Eip8037BlockGasInclusionCheck.lean'
[string] $generatedBlockReceiptGasAccountingLeanArtifact = Join-Path $leanDirectory 'Eip803x\Generated\BlockReceiptGasAccountingKernel.lean'
[string] $generatedSStorePricingLeanArtifact = Join-Path $leanDirectory 'Eip803x\Generated\SStorePricingKernel.lean'
[string] $generatedAccountAccessPricingLeanArtifact = Join-Path $leanDirectory 'Eip803x\Generated\AccountAccessPricingKernel.lean'
[string] $generatedExtendedStackDecoderLeanArtifact = Join-Path $leanDirectory 'Eip803x\Generated\ExtendedStackDecoderKernel.lean'
[string] $generatedIdentityPrecompileLeanArtifact = Join-Path $leanDirectory 'Eip803x\Generated\IdentityPrecompileKernel.lean'
[string] $generatedPrecompileGasPricingLeanArtifact = Join-Path $leanDirectory 'Eip803x\Generated\PrecompileGasPricingKernel.lean'
[string] $generatedSystemTransactionRoutingLeanArtifact = Join-Path $leanDirectory 'Eip803x\Generated\SystemTransactionRoutingKernel.lean'
[string] $generatedEnvironmentOpcodeLeanArtifact = Join-Path $leanDirectory 'Eip803x\Generated\EnvironmentOpcodeKernel.lean'
[string] $generatedAccountReadOpcodeLeanArtifact = Join-Path $leanDirectory 'Eip803x\Generated\AccountReadOpcodeKernel.lean'
[string] $generatedLogOpcodeLeanArtifact = Join-Path $leanDirectory 'Eip803x\Generated\LogOpcodeKernel.lean'
[string] $generatedMemoryControlOpcodeLeanArtifact = Join-Path $leanDirectory 'Eip803x\Generated\MemoryControlOpcodeKernel.lean'
[string] $generatedPureWordOpcodeLeanArtifact = Join-Path $leanDirectory 'Eip803x\Generated\PureWordOpcodeKernel.lean'
[string] $generatedStackRearrangementOpcodeLeanArtifact = Join-Path $leanDirectory 'Eip803x\Generated\StackRearrangementOpcodeKernel.lean'
[string] $coverageArtifact = Join-Path $leanDirectory 'standard-mainnet-coverage.json'
[string] $processingCoverageArtifact = Join-Path $leanDirectory 'standard-mainnet-processing-coverage.json'
[string] $solutionPath = Join-Path $repositoryRoot 'tools\Evm\Evm.slnx'
[string] $testProject = Join-Path $repositoryRoot 'src\Nethermind\Nethermind.Evm.Test\Nethermind.Evm.Test.csproj'
[string] $stateTestProject = Join-Path $repositoryRoot 'src\Nethermind\Nethermind.State.Test\Nethermind.State.Test.csproj'
[string] $blockchainTestProject = Join-Path $repositoryRoot 'src\Nethermind\Nethermind.Blockchain.Test\Nethermind.Blockchain.Test.csproj'
[string] $evmAssembly = Join-Path $repositoryRoot 'tools\artifacts\bin\Evm\release\Evm.dll'
[string] $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([char[]] @('\', '/'))
$temporaryDirectory = $null

try
{
    foreach ($command in @('git', 'dotnet', 'lake', 'pwsh'))
    {
        if ($null -eq (Get-Command $command -ErrorAction SilentlyContinue))
        {
            throw "Required command is not available on PATH: $command"
        }
    }

    try
    {
        $verificationManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    }
    catch
    {
        throw "Unable to parse verification manifest '$manifestPath': $($_.Exception.Message)"
    }

    if ($verificationManifest.schemaVersion -ne 1)
    {
        throw "Unsupported verification manifest schema version: $($verificationManifest.schemaVersion)"
    }

    Assert-BlsFp2ToG2Fixtures $repositoryRoot $leanDirectory

    [string] $pinnedCommit = $verificationManifest.pins.nethermindCommit
    if ($pinnedCommit -notmatch '^[0-9a-f]{40}$')
    {
        throw "Verification manifest has an invalid Nethermind commit pin."
    }

    [string] $currentHead = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0)
    {
        throw "Unable to resolve the repository HEAD."
    }

    & git -C $repositoryRoot cat-file -e ($pinnedCommit + '^{commit}')
    if ($LASTEXITCODE -ne 0)
    {
        throw "Pinned Nethermind commit $pinnedCommit is not available in the repository."
    }

    & git -C $repositoryRoot merge-base --is-ancestor $pinnedCommit $currentHead
    if ($LASTEXITCODE -ne 0)
    {
        throw "Pinned Nethermind commit $pinnedCommit is not an ancestor of repository HEAD $currentHead."
    }

    [string] $pinnedLeanToolchain = $verificationManifest.pins.leanToolchain
    [string] $declaredLeanToolchain = (Get-Content -LiteralPath (Join-Path $leanDirectory 'lean-toolchain') -Raw).Trim()
    if ($declaredLeanToolchain -cne $pinnedLeanToolchain)
    {
        throw "Lean toolchain file does not match the verification manifest pin."
    }

    Invoke-CheckedNative 'dotnet' @(
        'build',
        $solutionPath,
        '-c', 'Release',
        '--warnaserror',
        '-p:SaveDiskSpace=true'
    )

    $temporaryDirectory = Get-SystemTemporaryDirectory
    [string] $temporaryCoverageArtifact = Join-Path $temporaryDirectory 'standard-mainnet-coverage.json'
    [string[]] $coverageLines = @(& 'dotnet' $evmAssembly formal coverage --repo-root $repositoryRoot)
    if ($LASTEXITCODE -ne 0)
    {
        throw "formal coverage exited with code $LASTEXITCODE."
    }

    [string] $coverageContent = if ($coverageLines.Count -eq 0)
    {
        [string]::Empty
    }
    else
    {
        [string]::Join("`n", $coverageLines) + "`n"
    }
    [System.IO.File]::WriteAllText($temporaryCoverageArtifact, $coverageContent, [System.Text.UTF8Encoding]::new($false))
    Assert-FileBytesEqual $coverageArtifact $temporaryCoverageArtifact

    [string] $temporaryProcessingCoverageArtifact = Join-Path $temporaryDirectory 'standard-mainnet-processing-coverage.json'
    [string[]] $processingCoverageLines = @(& 'dotnet' $evmAssembly formal processing-coverage --repo-root $repositoryRoot)
    if ($LASTEXITCODE -ne 0)
    {
        throw "formal processing-coverage exited with code $LASTEXITCODE."
    }

    [string] $processingCoverageContent = if ($processingCoverageLines.Count -eq 0)
    {
        [string]::Empty
    }
    else
    {
        [string]::Join("`n", $processingCoverageLines) + "`n"
    }
    [System.IO.File]::WriteAllText($temporaryProcessingCoverageArtifact, $processingCoverageContent, [System.Text.UTF8Encoding]::new($false))
    Assert-FileBytesEqual $processingCoverageArtifact $temporaryProcessingCoverageArtifact
    Assert-ProcessingCoverageReapBeforePersistentCommit $temporaryProcessingCoverageArtifact

    [string] $temporaryChargeLeanArtifact = Join-Path $temporaryDirectory 'StateGasChargeKernel.lean'
    [string] $temporaryTransitionLeanArtifact = Join-Path $temporaryDirectory 'StateGasTransitionKernel.lean'
    [string] $temporaryStateGasTransitionAdapterLeanArtifact = Join-Path $temporaryDirectory 'StateGasTransitionAdapterKernel.lean'
    [string] $temporaryTransactionGasInitializationLeanArtifact = Join-Path $temporaryDirectory 'TransactionGasInitializationKernel.lean'
    [string] $temporaryTransactionSettlementLeanArtifact = Join-Path $temporaryDirectory 'TransactionSettlementKernel.lean'
    [string] $temporaryBlockGasInclusionLeanArtifact = Join-Path $temporaryDirectory 'Eip8037BlockGasInclusionCheck.lean'
    [string] $temporaryBlockReceiptGasAccountingLeanArtifact = Join-Path $temporaryDirectory 'BlockReceiptGasAccountingKernel.lean'
    [string] $temporarySStorePricingLeanArtifact = Join-Path $temporaryDirectory 'SStorePricingKernel.lean'
    [string] $temporaryAccountAccessPricingLeanArtifact = Join-Path $temporaryDirectory 'AccountAccessPricingKernel.lean'
    [string] $temporaryExtendedStackDecoderLeanArtifact = Join-Path $temporaryDirectory 'ExtendedStackDecoderKernel.lean'
    [string] $temporaryIdentityPrecompileLeanArtifact = Join-Path $temporaryDirectory 'IdentityPrecompileKernel.lean'
    [string] $temporaryPrecompileGasPricingLeanArtifact = Join-Path $temporaryDirectory 'PrecompileGasPricingKernel.lean'
    [string] $temporarySystemTransactionRoutingDirectory = Join-Path $temporaryDirectory 'system-transaction-routing'
    [string] $temporarySystemTransactionRoutingLeanArtifact = Join-Path $temporarySystemTransactionRoutingDirectory 'SystemTransactionRoutingKernel.lean'
    [string] $temporaryEnvironmentOpcodeDirectory = Join-Path $temporaryDirectory 'environment-opcode'
    [string] $temporaryEnvironmentOpcodeLeanArtifact = Join-Path $temporaryEnvironmentOpcodeDirectory 'EnvironmentOpcodeKernel.lean'
    [string] $temporaryExtendedStackOpcodeDirectory = Join-Path $temporaryDirectory 'extended-stack-opcode'
    [string] $temporaryExtendedStackOpcodeLeanArtifact = Join-Path $temporaryExtendedStackOpcodeDirectory 'ExtendedStackOpcodeKernel.lean'
    [string] $temporaryKeccak256OpcodeDirectory = Join-Path $temporaryDirectory 'keccak256-opcode'
    [string] $temporaryKeccak256OpcodeLeanArtifact = Join-Path $temporaryKeccak256OpcodeDirectory 'Keccak256OpcodeKernel.lean'
    [string] $temporaryAccountReadOpcodeDirectory = Join-Path $temporaryDirectory 'account-read-opcode'
    [string] $temporaryAccountReadOpcodeLeanArtifact = Join-Path $temporaryAccountReadOpcodeDirectory 'AccountReadOpcodeKernel.lean'
    [string] $temporaryLogOpcodeDirectory = Join-Path $temporaryDirectory 'log-opcode'
    [string] $temporaryLogOpcodeLeanArtifact = Join-Path $temporaryLogOpcodeDirectory 'LogOpcodeKernel.lean'
    [string] $temporaryMemoryControlOpcodeDirectory = Join-Path $temporaryDirectory 'memory-control-opcode'
    [string] $temporaryMemoryControlOpcodeLeanArtifact = Join-Path $temporaryMemoryControlOpcodeDirectory 'MemoryControlOpcodeKernel.lean'
    [string] $temporaryMemoryCopyOpcodeDirectory = Join-Path $temporaryDirectory 'memory-copy-opcode'
    [string] $temporaryMemoryCopyOpcodeLeanArtifact = Join-Path $temporaryMemoryCopyOpcodeDirectory 'MemoryCopyOpcodeKernel.lean'
    [string] $temporaryControlFlowOpcodeDirectory = Join-Path $temporaryDirectory 'control-flow-opcode'
    [string] $temporaryControlFlowOpcodeLeanArtifact = Join-Path $temporaryControlFlowOpcodeDirectory 'ControlFlowOpcodeKernel.lean'
    [string] $temporaryCallDataLoadOpcodeDirectory = Join-Path $temporaryDirectory 'call-data-load-opcode'
    [string] $temporaryCallDataLoadOpcodeLeanArtifact = Join-Path $temporaryCallDataLoadOpcodeDirectory 'CallDataLoadOpcodeKernel.lean'
    [string] $temporaryEvmFrameMachineDirectory = Join-Path $temporaryDirectory 'evm-frame-machine'
    [string] $temporaryEvmFrameMachineLeanArtifact = Join-Path $temporaryEvmFrameMachineDirectory 'EvmFrameMachineKernel.lean'
    [string] $temporaryEvmFrameControlSettlementDirectory = Join-Path $temporaryDirectory 'evm-frame-control-settlement'
    [string] $temporaryEvmFrameControlSettlementLeanArtifact = Join-Path $temporaryEvmFrameControlSettlementDirectory 'EvmFrameControlSettlementKernel.lean'
    [string] $temporaryEvmFrameDriverDirectory = Join-Path $temporaryDirectory 'evm-frame-driver'
    [string] $temporaryEvmFrameDriverLeanArtifact = Join-Path $temporaryEvmFrameDriverDirectory 'EvmFrameDriverKernel.lean'
    [string] $temporaryCallCreateOpcodeDirectory = Join-Path $temporaryDirectory 'call-create-opcode'
    [string] $temporaryCallCreateOpcodeLeanArtifact = Join-Path $temporaryCallCreateOpcodeDirectory 'CallCreateOpcodeKernel.lean'
    [string] $temporaryWorldJournalDirectory = Join-Path $temporaryDirectory 'world-journal'
    [string] $temporaryWorldJournalLeanArtifact = Join-Path $temporaryWorldJournalDirectory 'WorldJournalKernel.lean'
    [string] $temporaryFrameJournalDirectory = Join-Path $temporaryDirectory 'frame-journal'
    [string] $temporaryFrameJournalLeanArtifact = Join-Path $temporaryFrameJournalDirectory 'FrameJournalKernel.lean'
    [string] $temporaryPrecompileFrameDirectory = Join-Path $temporaryDirectory 'precompile-frame'
    [string] $temporaryPrecompileFrameLeanArtifact = Join-Path $temporaryPrecompileFrameDirectory 'PrecompileFrameStageA.lean'
    [string] $temporaryPrecompileFullFrameDirectory = Join-Path $temporaryDirectory 'precompile-full-frame'
    [string] $temporaryPureWordOpcodeDirectory = Join-Path $temporaryDirectory 'pure-word-opcode'
    [string] $temporaryPureWordOpcodeLeanArtifact = Join-Path $temporaryPureWordOpcodeDirectory 'PureWordOpcodeKernel.lean'
    [string] $temporaryPushOpcodeDirectory = Join-Path $temporaryDirectory 'push-opcode'
    [string] $temporaryPushOpcodeLeanArtifact = Join-Path $temporaryPushOpcodeDirectory 'PushOpcodeKernel.lean'
    [string] $temporaryStackRearrangementOpcodeDirectory = Join-Path $temporaryDirectory 'stack-rearrangement-opcode'
    [string] $temporaryStackRearrangementOpcodeLeanArtifact = Join-Path $temporaryStackRearrangementOpcodeDirectory 'StackRearrangementOpcodeKernel.lean'
    [string] $temporaryPersistentStorageOpcodeDirectory = Join-Path $temporaryDirectory 'persistent-storage-opcode'
    [string] $temporaryPersistentStorageOpcodeLeanArtifact = Join-Path $temporaryPersistentStorageOpcodeDirectory 'PersistentStorageOpcodeKernel.lean'
    [string] $temporaryTransientStorageOpcodeDirectory = Join-Path $temporaryDirectory 'transient-storage-opcode'
    [string] $temporaryTransientStorageOpcodeLeanArtifact = Join-Path $temporaryTransientStorageOpcodeDirectory 'TransientStorageOpcodeKernel.lean'
    [string] $temporaryTransactionProcessorDirectory = Join-Path $temporaryDirectory 'transaction-processor'
    [string] $temporaryTransactionProcessorLeanArtifact = Join-Path $temporaryTransactionProcessorDirectory 'TransactionProcessorLifecycle.lean'
    [string] $temporaryOrdinaryStaticAdmissionDirectory = Join-Path $temporaryDirectory 'ordinary-static-admission'
    [string] $temporaryOrdinaryStaticAdmissionLeanArtifact = Join-Path $temporaryOrdinaryStaticAdmissionDirectory 'OrdinaryStaticAdmissionKernel.lean'
    [string] $temporaryOrdinaryStatefulAdmissionPrefixDirectory = Join-Path $temporaryDirectory 'ordinary-stateful-admission-prefix'
    [string] $temporaryOrdinaryStatefulAdmissionPrefixLeanArtifact = Join-Path $temporaryOrdinaryStatefulAdmissionPrefixDirectory 'OrdinaryStatefulAdmissionPrefix.lean'
    [string] $temporaryBlockProcessorDirectory = Join-Path $temporaryDirectory 'block-processor'
    [string] $temporaryAuthorizationStateGasFoldDirectory = Join-Path $temporaryDirectory 'authorization-state-gas-fold'
    [string] $temporaryAuthorizationStateGasFoldLeanArtifact = Join-Path $temporaryAuthorizationStateGasFoldDirectory 'AuthorizationStateGasFold.lean'
    [string] $temporarySynchronousBlockPipelineExtractionDirectory = Join-Path $temporaryDirectory 'synchronous-block-pipeline-extraction'
    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $extractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryDirectory,
        '--lean-output', $temporaryChargeLeanArtifact,
        '--transition-lean-output', $temporaryTransitionLeanArtifact,
        '--transaction-gas-initialization-lean-output', $temporaryTransactionGasInitializationLeanArtifact,
        '--block-gas-inclusion-lean-output', $temporaryBlockGasInclusionLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $extractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryDirectory,
        '--profile', 'transaction-settlement',
        '--transaction-settlement-lean-output', $temporaryTransactionSettlementLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $extractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryDirectory,
        '--profile', 'state-gas-transition-adapter',
        '--state-gas-transition-adapter-lean-output', $temporaryStateGasTransitionAdapterLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $extractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryDirectory,
        '--profile', 'block-receipt-gas-accounting',
        '--block-receipt-gas-accounting-lean-output', $temporaryBlockReceiptGasAccountingLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $extractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryDirectory,
        '--profile', 'sstore-pricing',
        '--sstore-pricing-lean-output', $temporarySStorePricingLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $extractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryDirectory,
        '--profile', 'account-access-pricing',
        '--account-access-pricing-lean-output', $temporaryAccountAccessPricingLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $extractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryDirectory,
        '--profile', 'extended-stack-decoder',
        '--extended-stack-decoder-lean-output', $temporaryExtendedStackDecoderLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $extractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryDirectory,
        '--profile', 'identity-precompile',
        '--identity-precompile-lean-output', $temporaryIdentityPrecompileLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $extractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryDirectory,
        '--profile', 'precompile-gas-pricing',
        '--precompile-gas-pricing-lean-output', $temporaryPrecompileGasPricingLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $systemTransactionRoutingExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporarySystemTransactionRoutingDirectory,
        '--lean-output', $temporarySystemTransactionRoutingLeanArtifact
    )
    Assert-SystemTransactionRoutingSourceManifest `
        (Join-Path $temporarySystemTransactionRoutingDirectory 'SystemTransactionRoutingKernel.source-manifest.json') `
        $repositoryRoot `
        $pinnedCommit

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $environmentOpcodeExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryEnvironmentOpcodeDirectory,
        '--lean-output', $temporaryEnvironmentOpcodeLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $extendedStackOpcodeExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryExtendedStackOpcodeDirectory,
        '--lean-output', $temporaryExtendedStackOpcodeLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $keccak256OpcodeExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryKeccak256OpcodeDirectory,
        '--lean-output', $temporaryKeccak256OpcodeLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $accountReadOpcodeExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryAccountReadOpcodeDirectory,
        '--lean-output', $temporaryAccountReadOpcodeLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $logOpcodeExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryLogOpcodeDirectory,
        '--lean-output', $temporaryLogOpcodeLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $memoryControlOpcodeExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryMemoryControlOpcodeDirectory,
        '--lean-output', $temporaryMemoryControlOpcodeLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $memoryCopyOpcodeExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryMemoryCopyOpcodeDirectory,
        '--lean-output', $temporaryMemoryCopyOpcodeLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $controlFlowOpcodeExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryControlFlowOpcodeDirectory,
        '--lean-output', $temporaryControlFlowOpcodeLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $callDataLoadOpcodeExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryCallDataLoadOpcodeDirectory,
        '--lean-output', $temporaryCallDataLoadOpcodeLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $evmFrameMachineExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryEvmFrameMachineDirectory,
        '--lean-output', $temporaryEvmFrameMachineLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $evmFrameControlSettlementExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryEvmFrameControlSettlementDirectory,
        '--lean-output', $temporaryEvmFrameControlSettlementLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $evmFrameDriverExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryEvmFrameDriverDirectory,
        '--lean-output', $temporaryEvmFrameDriverLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $callCreateOpcodeExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryCallCreateOpcodeDirectory,
        '--lean-output', $temporaryCallCreateOpcodeLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $worldJournalExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryWorldJournalDirectory,
        '--lean-output', $temporaryWorldJournalLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $frameJournalExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryFrameJournalDirectory,
        '--lean-output', $temporaryFrameJournalLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $precompileFrameExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryPrecompileFrameDirectory,
        '--lean-output', $temporaryPrecompileFrameLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $precompileFullFrameExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        'extract',
        $repositoryRoot,
        $temporaryPrecompileFullFrameDirectory
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $pureWordOpcodeExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryPureWordOpcodeDirectory,
        '--lean-output', $temporaryPureWordOpcodeLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $pushOpcodeExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        $repositoryRoot,
        $temporaryPushOpcodeDirectory,
        $temporaryPushOpcodeLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $stackRearrangementOpcodeExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryStackRearrangementOpcodeDirectory,
        '--lean-output', $temporaryStackRearrangementOpcodeLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $persistentStorageOpcodeExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        $repositoryRoot,
        $temporaryPersistentStorageOpcodeDirectory,
        $temporaryPersistentStorageOpcodeLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $transientStorageOpcodeExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        $repositoryRoot,
        $temporaryTransientStorageOpcodeDirectory,
        $temporaryTransientStorageOpcodeLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $transactionProcessorExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryTransactionProcessorDirectory,
        '--lean-output', $temporaryTransactionProcessorLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $ordinaryStaticAdmissionExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryOrdinaryStaticAdmissionDirectory,
        '--lean-output', $temporaryOrdinaryStaticAdmissionLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $ordinaryStatefulAdmissionPrefixExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryOrdinaryStatefulAdmissionPrefixDirectory,
        '--lean-output', $temporaryOrdinaryStatefulAdmissionPrefixLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $blockProcessorExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryBlockProcessorDirectory
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $authorizationStateGasFoldExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--repo-root', $repositoryRoot,
        '--output', $temporaryAuthorizationStateGasFoldDirectory,
        '--lean-output', $temporaryAuthorizationStateGasFoldLeanArtifact
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $synchronousBlockPipelineMachineExtractorProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '--',
        '--audit-pins',
        '--repo-root', $repositoryRoot
    )

    Invoke-CheckedNative 'pwsh' @(
        '-NoProfile',
        '-File', $synchronousBlockPipelinePreparationBoundaryTest,
        '-RepositoryRoot', $repositoryRoot
    )

    Assert-NativeRefusalWithoutOutput `
        'dotnet' `
        @(
            'run',
            '--project', $synchronousBlockPipelineMachineExtractorProject,
            '-c', 'Release',
            '--no-build',
            '--no-restore',
            '--',
            '--extract',
            '--repo-root', $repositoryRoot,
            '--output', $temporarySynchronousBlockPipelineExtractionDirectory
        ) `
        'Operational extraction is intentionally disabled: the source route is an audit scaffold, not an admitted semantic kernel.' `
        $temporarySynchronousBlockPipelineExtractionDirectory

    foreach ($artifactName in @(
        'StateGasChargeKernel.ir.json',
        'StateGasChargeKernel.source-manifest.json',
        'StateGasTransitionKernel.ir.json',
        'StateGasTransitionKernel.source-manifest.json',
        'StateGasTransitionAdapterKernel.ir.json',
        'StateGasTransitionAdapterKernel.source-manifest.json',
        'TransactionGasInitializationKernel.ir.json',
        'TransactionGasInitializationKernel.source-manifest.json',
        'TransactionSettlementKernel.ir.json',
        'TransactionSettlementKernel.source-manifest.json',
        'Eip8037BlockGasInclusionCheck.ir.json',
        'Eip8037BlockGasInclusionCheck.source-manifest.json',
        'BlockReceiptGasAccountingKernel.ir.json',
        'BlockReceiptGasAccountingKernel.source-manifest.json',
        'SStorePricingKernel.ir.json',
        'SStorePricingKernel.source-manifest.json',
        'AccountAccessPricingKernel.ir.json',
        'AccountAccessPricingKernel.source-manifest.json',
        'ExtendedStackDecoderKernel.ir.json',
        'ExtendedStackDecoderKernel.source-manifest.json',
        'IdentityPrecompileKernel.ir.json',
        'IdentityPrecompileKernel.source-manifest.json',
        'PrecompileGasPricingKernel.ir.json',
        'PrecompileGasPricingKernel.source-manifest.json'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $generatedDirectory $artifactName) `
            (Join-Path $temporaryDirectory $artifactName)
    }
    Assert-FileBytesEqual $generatedChargeLeanArtifact $temporaryChargeLeanArtifact
    Assert-FileBytesEqual $generatedTransitionLeanArtifact $temporaryTransitionLeanArtifact
    Assert-FileBytesEqual $generatedStateGasTransitionAdapterLeanArtifact $temporaryStateGasTransitionAdapterLeanArtifact
    Assert-FileBytesEqual $generatedTransactionGasInitializationLeanArtifact $temporaryTransactionGasInitializationLeanArtifact
    Assert-FileBytesEqual $generatedTransactionSettlementLeanArtifact $temporaryTransactionSettlementLeanArtifact
    Assert-FileBytesEqual $generatedBlockGasInclusionLeanArtifact $temporaryBlockGasInclusionLeanArtifact
    Assert-FileBytesEqual $generatedBlockReceiptGasAccountingLeanArtifact $temporaryBlockReceiptGasAccountingLeanArtifact
    Assert-FileBytesEqual $generatedSStorePricingLeanArtifact $temporarySStorePricingLeanArtifact
    Assert-FileBytesEqual $generatedAccountAccessPricingLeanArtifact $temporaryAccountAccessPricingLeanArtifact
    Assert-FileBytesEqual $generatedExtendedStackDecoderLeanArtifact $temporaryExtendedStackDecoderLeanArtifact
    Assert-FileBytesEqual $generatedIdentityPrecompileLeanArtifact $temporaryIdentityPrecompileLeanArtifact
    Assert-FileBytesEqual $generatedPrecompileGasPricingLeanArtifact $temporaryPrecompileGasPricingLeanArtifact
    foreach ($artifactName in @(
        'SystemTransactionRoutingKernel.ir.json',
        'SystemTransactionRoutingKernel.source-manifest.json'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $systemTransactionRoutingGeneratedDirectory $artifactName) `
            (Join-Path $temporarySystemTransactionRoutingDirectory $artifactName)
    }
    Assert-FileBytesEqual $generatedSystemTransactionRoutingLeanArtifact $temporarySystemTransactionRoutingLeanArtifact
    foreach ($artifactName in @(
        'EnvironmentOpcodeKernel.ir.json',
        'EnvironmentOpcodeKernel.source-manifest.json'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $environmentOpcodeGeneratedDirectory $artifactName) `
            (Join-Path $temporaryEnvironmentOpcodeDirectory $artifactName)
    }
    Assert-FileBytesEqual $generatedEnvironmentOpcodeLeanArtifact $temporaryEnvironmentOpcodeLeanArtifact
    foreach ($artifactName in @(
        'ExtendedStackOpcodeKernel.ir.json',
        'ExtendedStackOpcodeKernel.source-manifest.json',
        'ExtendedStackOpcodeKernel.lean'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $extendedStackOpcodeGeneratedDirectory $artifactName) `
            (Join-Path $temporaryExtendedStackOpcodeDirectory $artifactName)
    }
    foreach ($artifactName in @(
        'Keccak256OpcodeKernel.ir.json',
        'Keccak256OpcodeKernel.source-manifest.json',
        'Keccak256OpcodeKernel.lean'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $keccak256OpcodeGeneratedDirectory $artifactName) `
            (Join-Path $temporaryKeccak256OpcodeDirectory $artifactName)
    }
    foreach ($artifactName in @(
        'AccountReadOpcodeKernel.ir.json',
        'AccountReadOpcodeKernel.source-manifest.json'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $accountReadOpcodeGeneratedDirectory $artifactName) `
            (Join-Path $temporaryAccountReadOpcodeDirectory $artifactName)
    }
    Assert-FileBytesEqual $generatedAccountReadOpcodeLeanArtifact $temporaryAccountReadOpcodeLeanArtifact
    foreach ($artifactName in @(
        'LogOpcodeKernel.ir.json',
        'LogOpcodeKernel.source-manifest.json'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $logOpcodeGeneratedDirectory $artifactName) `
            (Join-Path $temporaryLogOpcodeDirectory $artifactName)
    }
    Assert-FileBytesEqual $generatedLogOpcodeLeanArtifact $temporaryLogOpcodeLeanArtifact
    foreach ($artifactName in @(
        'MemoryControlOpcodeKernel.ir.json',
        'MemoryControlOpcodeKernel.source-manifest.json'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $memoryControlOpcodeGeneratedDirectory $artifactName) `
            (Join-Path $temporaryMemoryControlOpcodeDirectory $artifactName)
    }
    Assert-FileBytesEqual $generatedMemoryControlOpcodeLeanArtifact $temporaryMemoryControlOpcodeLeanArtifact
    foreach ($artifactName in @(
        'MemoryCopyOpcodeKernel.ir.json',
        'MemoryCopyOpcodeKernel.source-manifest.json',
        'MemoryCopyOpcodeKernel.lean'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $memoryCopyOpcodeGeneratedDirectory $artifactName) `
            (Join-Path $temporaryMemoryCopyOpcodeDirectory $artifactName)
    }
    foreach ($artifactName in @(
        'ControlFlowOpcodeKernel.ir.json',
        'ControlFlowOpcodeKernel.source-manifest.json',
        'ControlFlowOpcodeKernel.lean'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $controlFlowOpcodeGeneratedDirectory $artifactName) `
            (Join-Path $temporaryControlFlowOpcodeDirectory $artifactName)
    }
    foreach ($artifactName in @(
        'CallDataLoadOpcodeKernel.ir.json',
        'CallDataLoadOpcodeKernel.source-manifest.json',
        'CallDataLoadOpcodeKernel.lean'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $callDataLoadOpcodeGeneratedDirectory $artifactName) `
            (Join-Path $temporaryCallDataLoadOpcodeDirectory $artifactName)
    }
    foreach ($artifactName in @(
        'EvmFrameMachineKernel.ir.json',
        'EvmFrameMachineKernel.source-manifest.json',
        'EvmFrameMachineKernel.lean'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $evmFrameMachineGeneratedDirectory $artifactName) `
            (Join-Path $temporaryEvmFrameMachineDirectory $artifactName)
    }
    foreach ($artifactName in @(
        'EvmFrameControlSettlementKernel.ir.json',
        'EvmFrameControlSettlementKernel.source-manifest.json',
        'EvmFrameControlSettlementKernel.lean'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $evmFrameControlSettlementGeneratedDirectory $artifactName) `
            (Join-Path $temporaryEvmFrameControlSettlementDirectory $artifactName)
    }
    foreach ($artifactName in @(
        'EvmFrameDriverKernel.ir.json',
        'EvmFrameDriverKernel.source-manifest.json',
        'EvmFrameDriverKernel.lean'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $evmFrameDriverGeneratedDirectory $artifactName) `
            (Join-Path $temporaryEvmFrameDriverDirectory $artifactName)
    }
    foreach ($artifactName in @(
        'CallCreateOpcodeKernel.ir.json',
        'CallCreateOpcodeKernel.source-manifest.json',
        'CallCreateOpcodeKernel.lean'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $callCreateOpcodeGeneratedDirectory $artifactName) `
            (Join-Path $temporaryCallCreateOpcodeDirectory $artifactName)
    }
    foreach ($artifactName in @(
        'WorldJournalKernel.ir.json',
        'WorldJournalKernel.source-manifest.json',
        'WorldJournalKernel.lean'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $worldJournalGeneratedDirectory $artifactName) `
            (Join-Path $temporaryWorldJournalDirectory $artifactName)
    }
    foreach ($artifactName in @(
        'FrameJournalKernel.ir.json',
        'FrameJournalKernel.source-manifest.json',
        'FrameJournalKernel.lean'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $frameJournalGeneratedDirectory $artifactName) `
            (Join-Path $temporaryFrameJournalDirectory $artifactName)
    }
    foreach ($artifactName in @(
        'precompile-frame-stage-a.ir.json',
        'precompile-frame-stage-a.source-manifest.json',
        'PrecompileFrameStageA.lean',
        'precompile-frame-stage-b.ir.json',
        'precompile-frame-stage-b.source-manifest.json',
        'PrecompileFrameStageB.lean'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $precompileFrameGeneratedDirectory $artifactName) `
            (Join-Path $temporaryPrecompileFrameDirectory $artifactName)
    }
    foreach ($artifactName in @(
        'precompile-full-frame-stage-c.ir.json',
        'precompile-full-frame-stage-c.source-manifest.json',
        'PrecompileFullFrame.lean'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $precompileFullFrameGeneratedDirectory $artifactName) `
            (Join-Path $temporaryPrecompileFullFrameDirectory $artifactName)
    }
    foreach ($artifactName in @(
        'PureWordOpcodeKernel.ir.json',
        'PureWordOpcodeKernel.source-manifest.json'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $pureWordOpcodeGeneratedDirectory $artifactName) `
            (Join-Path $temporaryPureWordOpcodeDirectory $artifactName)
    }
    Assert-FileBytesEqual $generatedPureWordOpcodeLeanArtifact $temporaryPureWordOpcodeLeanArtifact
    foreach ($artifactName in @(
        'PushOpcodeKernel.ir.json',
        'PushOpcodeKernel.source-manifest.json',
        'PushOpcodeKernel.lean'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $pushOpcodeGeneratedDirectory $artifactName) `
            (Join-Path $temporaryPushOpcodeDirectory $artifactName)
    }
    foreach ($artifactName in @(
        'StackRearrangementOpcodeKernel.ir.json',
        'StackRearrangementOpcodeKernel.source-manifest.json'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $stackRearrangementOpcodeGeneratedDirectory $artifactName) `
            (Join-Path $temporaryStackRearrangementOpcodeDirectory $artifactName)
    }
    Assert-FileBytesEqual $generatedStackRearrangementOpcodeLeanArtifact $temporaryStackRearrangementOpcodeLeanArtifact
    foreach ($artifactName in @(
        'PersistentStorageOpcodeKernel.ir.json',
        'PersistentStorageOpcodeKernel.source-manifest.json',
        'PersistentStorageOpcodeKernel.lean'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $persistentStorageOpcodeGeneratedDirectory $artifactName) `
            (Join-Path $temporaryPersistentStorageOpcodeDirectory $artifactName)
    }
    foreach ($artifactName in @(
        'TransientStorageOpcodeKernel.ir.json',
        'TransientStorageOpcodeKernel.source-manifest.json',
        'TransientStorageOpcodeKernel.lean'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $transientStorageOpcodeGeneratedDirectory $artifactName) `
            (Join-Path $temporaryTransientStorageOpcodeDirectory $artifactName)
    }
    foreach ($artifactName in @(
        'TransactionProcessorLifecycle.ir.json',
        'TransactionProcessorLifecycle.source-manifest.json',
        'TransactionProcessorLifecycle.lean'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $transactionProcessorGeneratedDirectory $artifactName) `
            (Join-Path $temporaryTransactionProcessorDirectory $artifactName)
    }
    foreach ($artifactName in @(
        'BlockProcessorControl.ir.json',
        'BlockProcessorControl.source-manifest.json',
        'BlockProcessorControl.lean'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $blockProcessorGeneratedDirectory $artifactName) `
            (Join-Path $temporaryBlockProcessorDirectory $artifactName)
    }
    foreach ($artifactName in @(
        'OrdinaryStaticAdmissionKernel.ir.json',
        'OrdinaryStaticAdmissionKernel.source-manifest.json',
        'OrdinaryStaticAdmissionKernel.lean'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $ordinaryStaticAdmissionGeneratedDirectory $artifactName) `
            (Join-Path $temporaryOrdinaryStaticAdmissionDirectory $artifactName)
    }
    foreach ($artifactName in @(
        'OrdinaryStatefulAdmissionPrefix.ir.json',
        'OrdinaryStatefulAdmissionPrefix.source-manifest.json',
        'OrdinaryStatefulAdmissionPrefix.lean'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $ordinaryStatefulAdmissionPrefixGeneratedDirectory $artifactName) `
            (Join-Path $temporaryOrdinaryStatefulAdmissionPrefixDirectory $artifactName)
    }
    foreach ($artifactName in @(
        'AuthorizationStateGasFold.ir.json',
        'AuthorizationStateGasFold.source-manifest.json',
        'AuthorizationStateGasFold.lean'
    ))
    {
        Assert-FileBytesEqual `
            (Join-Path $authorizationStateGasFoldGeneratedDirectory $artifactName) `
            (Join-Path $temporaryAuthorizationStateGasFoldDirectory $artifactName)
    }
    Assert-FileSha256 `
        $authorizationStateGasFoldRefinement `
        '24e11c978c1e960c5f6961b4ee136a8880d6e78528a983a5b210e78818fec315'

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $extractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--minimum-expected-tests', '151',
        '--no-ansi',
        '--progress', 'off'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $systemTransactionRoutingExtractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--minimum-expected-tests', '107',
        '--no-ansi',
        '--progress', 'off'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $environmentOpcodeExtractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--minimum-expected-tests', '50',
        '--no-ansi',
        '--progress', 'off'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $extendedStackOpcodeExtractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--minimum-expected-tests', '11',
        '--no-ansi',
        '--progress', 'off'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $keccak256OpcodeExtractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--minimum-expected-tests', '23',
        '--no-ansi',
        '--progress', 'off'
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $accountReadOpcodeExtractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--minimum-expected-tests', '50',
        '--no-ansi',
        '--progress', 'off'
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $logOpcodeExtractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--minimum-expected-tests', '41',
        '--no-ansi',
        '--progress', 'off'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $memoryControlOpcodeExtractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--minimum-expected-tests', '46',
        '--no-ansi',
        '--progress', 'off'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $memoryCopyOpcodeExtractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--minimum-expected-tests', '22',
        '--no-ansi',
        '--progress', 'off'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $controlFlowOpcodeExtractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--minimum-expected-tests', '11',
        '--no-ansi',
        '--progress', 'off'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $callDataLoadOpcodeExtractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--minimum-expected-tests', '31',
        '--no-ansi',
        '--progress', 'off'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $evmFrameMachineExtractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--minimum-expected-tests', '28',
        '--no-ansi',
        '--progress', 'off'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $evmFrameControlSettlementExtractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--minimum-expected-tests', '28',
        '--no-ansi',
        '--no-progress'
    )

    $frameDriverTestOutput = @(Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $evmFrameDriverExtractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--filter', 'FullyQualifiedName~EvmFrameDriverExtractorTests&FullyQualifiedName!~.Operational_',
        '--minimum-expected-tests', '22',
        '--no-ansi',
        '--no-progress'
    ))
    $frameDriverTestOutput | Write-Output
    $frameDriverTestText = $frameDriverTestOutput -join "`n"
    $frameDriverTestCounts = @{}
    foreach ($name in @('total', 'failed', 'succeeded', 'skipped'))
    {
        $counts = [regex]::Matches($frameDriverTestText, "(?m)^\s*${name}:\s*(\d+)\s*$")
        if ($counts.Count -ne 1) { throw "Stage E requires exactly one $name test count." }
        $frameDriverTestCounts[$name] = [int]$counts[0].Groups[1].Value
    }
    if ($frameDriverTestCounts.total -lt 22 -or $frameDriverTestCounts.failed -ne 0 -or
        $frameDriverTestCounts.skipped -ne 0 -or
        $frameDriverTestCounts.succeeded -ne $frameDriverTestCounts.total)
    {
        throw 'Stage E requires at least 22 passing tests and no failures or skips.'
    }

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $callCreateOpcodeExtractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--minimum-expected-tests', '41',
        '--no-ansi',
        '--progress', 'off'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $worldJournalExtractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--minimum-expected-tests', '35',
        '--no-ansi',
        '--progress', 'off'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $frameJournalExtractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--minimum-expected-tests', '69',
        '--no-ansi',
        '--progress', 'off'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $precompileFrameExtractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--minimum-expected-tests', '95',
        '--no-ansi',
        '--no-progress'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $precompileFullFrameExtractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--filter', 'FullyQualifiedName~PrecompileFullFrameExtractorTests',
        '--minimum-expected-tests', '104',
        '--no-ansi',
        '--no-progress'
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $pureWordOpcodeExtractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--minimum-expected-tests', '47',
        '--no-ansi',
        '--progress', 'off'
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $pushOpcodeExtractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--minimum-expected-tests', '23',
        '--no-ansi',
        '--progress', 'off'
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $stackRearrangementOpcodeExtractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--minimum-expected-tests', '45',
        '--no-ansi',
        '--progress', 'off'
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $persistentStorageOpcodeExtractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--minimum-expected-tests', '53',
        '--no-ansi',
        '--progress', 'off'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $transientStorageOpcodeExtractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--minimum-expected-tests', '29',
        '--no-ansi',
        '--progress', 'off'
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $transactionProcessorExtractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--minimum-expected-tests', '25',
        '--no-ansi',
        '--progress', 'off'
    )

    Invoke-CheckedNative 'dotnet' @(
        'run',
        '--project', $blockProcessorExtractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--filter', 'FullyQualifiedName~Nethermind.Evm.Lean.BlockProcessorExtractor.Test.ExtractorTests',
        '--minimum-expected-tests', '43',
        '--no-ansi',
        '--progress', 'off'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $authorizationStateGasFoldExtractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--minimum-expected-tests', '77',
        '--no-ansi',
        '--progress', 'off'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $ordinaryStaticAdmissionExtractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--minimum-expected-tests', '96',
        '--no-ansi',
        '--progress', 'off'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $ordinaryStatefulAdmissionPrefixExtractorTestProject,
        '-c', 'Release',
        '--no-build',
        '--no-restore',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--minimum-expected-tests', '39',
        '--no-ansi',
        '--progress', 'off'
    )

    Assert-NoLeanPlaceholders $leanDirectory

    [string] $leanRequestOutput = Join-Path $temporaryDirectory 'eip803x-charge-state-requests.ndjson'
    [string] $leanVectorOutput = Join-Path $temporaryDirectory 'eip803x-charge-state.ndjson'
    [string] $evmVectorOutput = Join-Path $temporaryDirectory 'evm-charge-state.ndjson'
    [string] $leanTransitionRequestOutput = Join-Path $temporaryDirectory 'eip803x-state-gas-transition-requests.ndjson'
    [string] $leanTransitionVectorOutput = Join-Path $temporaryDirectory 'eip803x-state-gas-transition.ndjson'
    [string] $evmTransitionVectorOutput = Join-Path $temporaryDirectory 'evm-state-gas-transition.ndjson'

    Push-Location $leanDirectory
    try
    {
        Invoke-CheckedNative 'lake' @('build')
        Invoke-CanonicalNdjson 'lake' @('exe', 'eip803x-charge-state', 'requests') $leanRequestOutput
        Invoke-CanonicalNdjson 'lake' @('exe', 'eip803x-charge-state') $leanVectorOutput
        Invoke-CanonicalNdjson 'lake' @('exe', 'eip803x-state-gas-transition', 'requests') $leanTransitionRequestOutput
        Invoke-CanonicalNdjson 'lake' @('exe', 'eip803x-state-gas-transition') $leanTransitionVectorOutput
    }
    finally
    {
        Pop-Location
    }

    Push-Location (Join-Path $leanDirectory 'TransactionProcessorExtractor')
    try
    {
        Invoke-CheckedNative 'lake' @('build')
    }
    finally
    {
        Pop-Location
    }

    Push-Location (Join-Path $leanDirectory 'ExtendedStackOpcodeExtractor')
    try
    {
        Invoke-CheckedNative 'lake' @('build')
    }
    finally
    {
        Pop-Location
    }

    Push-Location (Join-Path $leanDirectory 'MemoryControlOpcodeExtractor')
    try
    {
        Invoke-CheckedNative 'lake' @('build')
    }
    finally
    {
        Pop-Location
    }

    Push-Location (Join-Path $leanDirectory 'MemoryCopyOpcodeExtractor')
    try
    {
        Invoke-CheckedNative 'lake' @('build')
    }
    finally
    {
        Pop-Location
    }

    Push-Location (Join-Path $leanDirectory 'ControlFlowOpcodeExtractor')
    try
    {
        Invoke-CheckedNative 'lake' @('build')
    }
    finally
    {
        Pop-Location
    }

    Push-Location (Join-Path $leanDirectory 'CallCreateOpcodeExtractor')
    try
    {
        Invoke-CheckedNative 'lake' @('build')
    }
    finally
    {
        Pop-Location
    }

    Push-Location (Join-Path $leanDirectory 'CallDataLoadOpcodeExtractor')
    try
    {
        Invoke-CheckedNative 'lake' @('build')
    }
    finally
    {
        Pop-Location
    }

    Push-Location (Join-Path $leanDirectory 'EvmFrameMachineExtractor')
    try
    {
        Invoke-CheckedNative 'lake' @('build')
    }
    finally
    {
        Pop-Location
    }

    Push-Location (Join-Path $leanDirectory 'EvmFrameControlSettlementExtractor')
    try
    {
        Invoke-CheckedNative 'lake' @('build', '-KwarningAsError=true')
        foreach ($source in @(
            '.\Generated\EvmFrameControlSettlementKernel.lean',
            '.\Specification\Types.lean',
            '.\Specification\Reference.lean',
            '.\Specification\AdmissionWitnesses.lean',
            '.\Refinement\FrameControlSettlement.lean',
            '.\Refinement\StageCFullPrecompileBridge.lean',
            '.\Refinement\StageCFullPrecompileBridgeVectors.lean'
        ))
        {
            Invoke-CheckedNative 'lake' @('env', 'lean', '-DwarningAsError=true', $source)
        }
        & (Join-Path $leanDirectory 'EvmFrameControlSettlementExtractor\Verify-BridgeMutationGates.ps1')
    }
    finally
    {
        Pop-Location
    }

    Push-Location (Join-Path $leanDirectory 'EvmFrameDriverExtractor')
    try
    {
        Invoke-CheckedNative 'lake' @('build', '-KwarningAsError=true',
            'EvmFrameDriverExtractor.Generated.EvmFrameDriverKernel',
            'EvmFrameDriverExtractor.Specification.Types',
            'EvmFrameDriverExtractor.Specification.Reference',
            'EvmFrameDriverExtractor.Specification.AdmissionWitnesses',
            'EvmFrameDriverExtractor.Refinement.FrameDriver')
        foreach ($source in @(
            '.\Generated\EvmFrameDriverKernel.lean',
            '.\Specification\Types.lean',
            '.\Specification\Reference.lean',
            '.\Specification\AdmissionWitnesses.lean',
            '.\Refinement\FrameDriver.lean'
        ))
        {
            Invoke-CheckedNative 'lake' @('env', 'lean', '-DwarningAsError=true', $source)
        }
    }
    finally
    {
        Pop-Location
    }

    Push-Location (Join-Path $leanDirectory 'OrdinaryStaticAdmissionExtractor')
    try
    {
        Invoke-CheckedNative 'lake' @('build', '-KwarningAsError=true')
        foreach ($source in @(
            '.\Generated\OrdinaryStaticAdmissionKernel.lean',
            '.\Reference\OrdinaryStaticAdmissionReference.lean',
            '.\Reference\OrdinaryStaticAdmissionVectors.lean',
            '.\Refinement\OrdinaryStaticAdmission.lean'
        ))
        {
            Invoke-CheckedNative 'lake' @('env', 'lean', '-DwarningAsError=true', $source)
        }
    }
    finally
    {
        Pop-Location
    }

    Push-Location (Join-Path $leanDirectory 'OrdinaryStatefulAdmissionPrefixExtractor')
    try
    {
        Invoke-CheckedNative 'lake' @('build', '-KwarningAsError=true')
        foreach ($source in @(
            '.\Generated\OrdinaryStatefulAdmissionPrefix.lean',
            '.\Reference\OrdinaryStatefulAdmissionPrefixReference.lean',
            '.\Refinement\OrdinaryStatefulAdmissionPrefix.lean',
            '.\Reference\OrdinaryStatefulAdmissionPrefixVectors.lean'
        ))
        {
            Invoke-CheckedNative 'lake' @('env', 'lean', '-DwarningAsError=true', '-DmaxHeartbeats=800000', $source)
        }
    }
    finally
    {
        Pop-Location
    }

    Push-Location (Join-Path $leanDirectory 'WorldJournalExtractor')
    try
    {
        Invoke-CheckedNative 'lake' @('build')
    }
    finally
    {
        Pop-Location
    }

    Push-Location (Join-Path $leanDirectory 'FrameJournalExtractor')
    try
    {
        Invoke-CheckedNative 'lake' @('build')
    }
    finally
    {
        Pop-Location
    }

    Push-Location (Join-Path $leanDirectory 'PrecompileFrameExtractor')
    try
    {
        Invoke-CheckedNative 'lake' @('build')
    }
    finally
    {
        Pop-Location
    }

    Push-Location (Join-Path $leanDirectory 'PrecompileFullFrameExtractor')
    try
    {
        Invoke-CheckedNative 'lake' @('build')
    }
    finally
    {
        Pop-Location
    }

    Push-Location (Join-Path $leanDirectory 'AuthorizationStateGasFoldExtractor')
    try
    {
        Invoke-CheckedNative 'lake' @('build')
    }
    finally
    {
        Pop-Location
    }

    Push-Location (Join-Path $leanDirectory 'Keccak256OpcodeExtractor')
    try
    {
        Invoke-CheckedNative 'lake' @('build')
    }
    finally
    {
        Pop-Location
    }

    Push-Location (Join-Path $leanDirectory 'PushOpcodeExtractor')
    try
    {
        Invoke-CheckedNative 'lake' @('build')
    }
    finally
    {
        Pop-Location
    }

    Push-Location (Join-Path $leanDirectory 'LogOpcodeExtractor')
    try
    {
        Invoke-CheckedNative 'lake' @('build')
    }
    finally
    {
        Pop-Location
    }

    Push-Location (Join-Path $leanDirectory 'PersistentStorageOpcodeExtractor')
    try
    {
        Invoke-CheckedNative 'lake' @('build')
    }
    finally
    {
        Pop-Location
    }

    Push-Location (Join-Path $leanDirectory 'TransientStorageOpcodeExtractor')
    try
    {
        Invoke-CheckedNative 'lake' @('build')
    }
    finally
    {
        Pop-Location
    }

    Push-Location (Join-Path $leanDirectory 'BlockProcessorExtractor')
    try
    {
        Invoke-CheckedNative 'lake' @('--wfail', 'build', 'BlockProcessorExtractor.Refinement.BlockProcessorControl')
        Invoke-CheckedNative 'lake' @('env', 'lean', '-DwarningAsError=true', '.\Refinement\BlockProcessorControl.lean')
    }
    finally
    {
        Pop-Location
    }

    Push-Location (Join-Path $leanDirectory 'SynchronousBlockPipelineMachineExtractor')
    try
    {
        Invoke-CheckedNative 'lake' @('build')
        foreach ($source in @(
            '.\Specification\BlockPipelineState.lean',
            '.\Specification\BlockPipelinePlan.lean',
            '.\Specification\CompositionBoundary.lean',
            '.\Specification\MetadataMirror.lean'
        ))
        {
            Invoke-CheckedNative 'lake' @('env', 'lean', '-DwarningAsError=true', $source)
        }
    }
    finally
    {
        Pop-Location
    }

    Push-Location $leanDirectory
    try
    {
        Invoke-CheckedNative 'lake' @('build', 'Eip803x')
        Invoke-CheckedNative 'lake' @('env', 'lean', '-DwarningAsError=true', 'Eip803x.lean')
    }
    finally
    {
        Pop-Location
    }

    [string] $stateGasChargeTestFilter = [string]::Join('|', @(
        'FullyQualifiedName=Nethermind.Evm.Test.EthereumGasPolicyTests.zero',
        'FullyQualifiedName=Nethermind.Evm.Test.EthereumGasPolicyTests.exact_reservoir',
        'FullyQualifiedName=Nethermind.Evm.Test.EthereumGasPolicyTests.partial_spill',
        'FullyQualifiedName=Nethermind.Evm.Test.EthereumGasPolicyTests.exact_execution_gas',
        'FullyQualifiedName=Nethermind.Evm.Test.EthereumGasPolicyTests.one_short_out_of_gas',
        'FullyQualifiedName=Nethermind.Evm.Test.EthereumGasPolicyTests.negative_reservoir',
        'FullyQualifiedName=Nethermind.Evm.Test.EthereumGasPolicyTests.maximum_reservoir',
        'FullyQualifiedName=Nethermind.Evm.Test.EthereumGasPolicyTests.maximum_spill',
        'FullyQualifiedName=Nethermind.Evm.Test.EthereumGasPolicyTests.minimum_reservoir',
        'FullyQualifiedName=Nethermind.Evm.Test.EthereumGasPolicyTests.negative_cost',
        'FullyQualifiedName~Nethermind.Evm.Test.EthereumGasPolicyTests.Specialized_account_access_matches_dynamic_policy_without_reading_fork_flags',
        'FullyQualifiedName~Nethermind.Evm.Test.EthereumGasPolicyTests.Specialized_eip8038_cold_account_access_honors_exact_boundaries',
        'FullyQualifiedName~Nethermind.Evm.Test.EthereumGasPolicyTests.Selfdestruct_beneficiary_access_follows_eip8038',
        'FullyQualifiedName~Nethermind.Evm.Test.Eip8038Tests.Selfdestruct_charges_beneficiary_access_and_creation',
        'FullyQualifiedName~Nethermind.Evm.Test.AccountAccessPricingKernelTests',
        'FullyQualifiedName~Nethermind.Evm.Test.EthereumGasPolicyTests.Precompile_pricing_charges_execution_gas_without_mutating_state_gas',
        'FullyQualifiedName~Nethermind.Evm.Test.EthereumGasPolicyTests.Full_frame_local_copy_and_inline_by_ref_dispatch_to_standard_precompile_pricing'
    ))
    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $testProject,
        '-c', 'Release',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--filter', $stateGasChargeTestFilter
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $testProject,
        '-c', 'Release',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--filter', 'FullyQualifiedName~Nethermind.Evm.Test.TransactionGasInitializationKernelTests',
        '--minimum-expected-tests', '23'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $testProject,
        '-c', 'Release',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--filter', 'FullyQualifiedName~Nethermind.Evm.Test.SystemTransactionRoutingKernelTests'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $testProject,
        '-c', 'Release',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--filter', 'FullyQualifiedName~Nethermind.Evm.Test.SStorePricingKernelTests'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $testProject,
        '-c', 'Release',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--filter', 'FullyQualifiedName~Nethermind.Evm.Test.ExtendedStackDecoderKernelTests'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $testProject,
        '-c', 'Release',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--filter', 'FullyQualifiedName~Nethermind.Evm.Test.Eip1153Tests'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $testProject,
        '-c', 'Release',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--filter', 'FullyQualifiedName~Nethermind.Evm.Test.Eip8037RegressionTests'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $testProject,
        '-c', 'Release',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--filter', 'FullyQualifiedName~Nethermind.Evm.Test.Eip1014Tests'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $stateTestProject,
        '-c', 'Release',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--filter', 'FullyQualifiedName~Nethermind.Store.Test.StorageProviderTests'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $stateTestProject,
        '-c', 'Release',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--filter', 'FullyQualifiedName~Nethermind.Store.Test.BlockAccessListBasedWorldStateTests'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $stateTestProject,
        '-c', 'Release',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--filter', 'FullyQualifiedName~Nethermind.Store.Test.TracedAccessWorldStateTests',
        '--minimum-expected-tests', '1'
    )

    [string] $pureWordOpcodeReferenceTestFilter = [string]::Join('|', @(
        'FullyQualifiedName~Nethermind.Evm.Test.VirtualMachineTests.Fixed_cost_opcode_gas_status_preserves_failure_precedence',
        'FullyQualifiedName~Nethermind.Evm.Test.VirtualMachineTests.Modular_arithmetic_preserves_full_width_operands',
        'FullyQualifiedName~Nethermind.Evm.Test.VirtualMachineTests.Exp_',
        'FullyQualifiedName~Nethermind.Evm.Test.VirtualMachineTests.Pure_word_opcode_',
        'FullyQualifiedName~Nethermind.Evm.Test.VirtualMachineTests.Continuable_opcode_at_end_of_code_reports_one_instruction_boundary',
        'FullyQualifiedName~Nethermind.Evm.Test.VirtualMachineTests.Create_paths_preserve_pre_child_trace_without_duplicate_boundary',
        'FullyQualifiedName~Nethermind.Evm.Test.EthereumGasPolicyTests.Specialized_exp_price_matches_price_book',
        'FullyQualifiedName~Nethermind.Evm.Test.AddTests',
        'FullyQualifiedName~Nethermind.Evm.Test.SubTests',
        'FullyQualifiedName~Nethermind.Evm.Test.SDivTests',
        'FullyQualifiedName~Nethermind.Evm.Test.SModTests',
        'FullyQualifiedName~Nethermind.Evm.Test.GtTests',
        'FullyQualifiedName~Nethermind.Evm.Test.SgtTests',
        'FullyQualifiedName~Nethermind.Evm.Test.SltTests',
        'FullyQualifiedName~Nethermind.Evm.Test.Eip145Tests',
        'FullyQualifiedName~Nethermind.Evm.Test.Eip7939Tests',
        'FullyQualifiedName~Nethermind.Evm.Test.ByteTests',
        'FullyQualifiedName~Nethermind.Evm.Test.SignExtTests'
    ))
    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $testProject,
        '-c', 'Release',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--filter', $pureWordOpcodeReferenceTestFilter,
        '--minimum-expected-tests', '1'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $testProject,
        '-c', 'Release',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--filter', 'FullyQualifiedName~Nethermind.Evm.Test.IdentityPrecompileTests'
    )

    [string] $referencePrecompileTestFilter = [string]::Join('|', @(
        'FullyQualifiedName~Nethermind.Evm.Test.ECRecoverPrecompileTests',
        'FullyQualifiedName~Nethermind.Evm.Test.Sha256PrecompileTests',
        'FullyQualifiedName~Nethermind.Evm.Test.Ripemd160PrecompileTests',
        'FullyQualifiedName~Nethermind.Evm.Test.ModExpPrecompileTests',
        'FullyQualifiedName~Nethermind.Evm.Test.Eip2565Tests',
        'FullyQualifiedName~Nethermind.Evm.Test.Eip7823Tests',
        'FullyQualifiedName~Nethermind.Evm.Test.Eip7883Tests',
        'FullyQualifiedName~Nethermind.Evm.Test.BN254AddPrecompileTests',
        'FullyQualifiedName~Nethermind.Evm.Test.BN254MulPrecompileTests',
        'FullyQualifiedName~Nethermind.Evm.Test.BN254PairingCheckPrecompileTests',
        'FullyQualifiedName~Nethermind.Evm.Test.Blake2FPrecompileTests',
        'FullyQualifiedName~Nethermind.Evm.Test.KzgPointEvaluationPrecompileTests',
        'FullyQualifiedName~Nethermind.Evm.Test.Bls12381G1AddPrecompileTests',
        'FullyQualifiedName~Nethermind.Evm.Test.Bls12381G1MsmPrecompileTests',
        'FullyQualifiedName~Nethermind.Evm.Test.Bls12381G2AddPrecompileTests',
        'FullyQualifiedName~Nethermind.Evm.Test.Bls12381G2MsmPrecompileTests',
        'FullyQualifiedName~Nethermind.Evm.Test.Bls12381PairingCheckPrecompileTests',
        'FullyQualifiedName~Nethermind.Evm.Test.Bls12381FpToG1PrecompileTests',
        'FullyQualifiedName~Nethermind.Evm.Test.Bls12381Fp2ToG2PrecompileTests',
        'FullyQualifiedName~Nethermind.Evm.Test.SecP256r1PrecompileTests'
    ))
    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $testProject,
        '-c', 'Release',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--filter', $referencePrecompileTestFilter,
        '--minimum-expected-tests', '1'
    )

    [string] $selfDestructReferenceTestFilter = [string]::Join('|', @(
        'FullyQualifiedName~Nethermind.Evm.Test.Eip6780Tests',
        'FullyQualifiedName~Nethermind.Evm.Test.Eip8246Tests',
        'FullyQualifiedName~Nethermind.Evm.Test.Eip7708Tests',
        'FullyQualifiedName~Nethermind.Evm.Test.Eip8038Tests.Selfdestruct',
        'FullyQualifiedName~Nethermind.Evm.Test.Eip8037RegressionTests&FullyQualifiedName~selfdestruct'
    ))
    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $testProject,
        '-c', 'Release',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--filter', $selfDestructReferenceTestFilter,
        '--minimum-expected-tests', '1'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $testProject,
        '-c', 'Release',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--filter', 'FullyQualifiedName~Nethermind.Evm.Test.LogOpcodeTests'
    )

    [string] $transactionSettlementTestFilter = [string]::Join('|', @(
        'FullyQualifiedName~Success_applies_signed_refund_and_cap',
        'FullyQualifiedName~Revert_and_legacy_error_ignore_substate_and_destroy_refunds',
        'FullyQualifiedName~Eip8037_projects_transaction_and_block_gas_dimensions',
        'FullyQualifiedName~PreEip8037_projection_preserves_Eip7778_branch',
        'FullyQualifiedName~Maximum_values_preserve_fixed_width_refund_semantics',
        'FullyQualifiedName~Signed_refund_machine_boundaries_are_unchecked'
    ))
    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $testProject,
        '-c', 'Release',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--filter', $transactionSettlementTestFilter
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $testProject,
        '-c', 'Release',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--filter', 'FullyQualifiedName~Nethermind.Evm.Test.Eip8037BlockGasInclusionCheckTests'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $blockchainTestProject,
        '-c', 'Release',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--filter', 'FullyQualifiedName~Nethermind.Blockchain.Test.BlockProcessorTests'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $blockchainTestProject,
        '-c', 'Release',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--filter', 'FullyQualifiedName~Nethermind.Blockchain.Test.BlockAccessListSequentialValidationTests',
        '--minimum-expected-tests', '1'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $blockchainTestProject,
        '-c', 'Release',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--filter', 'FullyQualifiedName~Nethermind.Blockchain.Test.BlockchainProcessorTests'
    )

    Invoke-CheckedNative 'dotnet' @(
        'test',
        '--project', $blockchainTestProject,
        '-c', 'Release',
        '-p:TreatWarningsAsErrors=true',
        '-p:SaveDiskSpace=true',
        '--',
        '--filter', 'FullyQualifiedName~Nethermind.Blockchain.Test.BlockReceiptGasAccountingKernelTests'
    )

    if (-not (Test-Path -LiteralPath $evmAssembly -PathType Leaf))
    {
        throw "Built EVM command assembly is missing: $evmAssembly"
    }

    [string[]] $expectedResponses = @(
        '{"schemaVersion":"1","operation":"charge-state","outcome":"success","state":{"gasLeft":"10","stateReservoir":"7","stateGasUsed":"11","stateGasSpill":"13","stateGasSpillRefunded":"5"}}',
        '{"schemaVersion":"1","operation":"charge-state","outcome":"success","state":{"gasLeft":"10","stateReservoir":"0","stateGasUsed":"18","stateGasSpill":"13","stateGasSpillRefunded":"5"}}',
        '{"schemaVersion":"1","operation":"charge-state","outcome":"success","state":{"gasLeft":"6","stateReservoir":"0","stateGasUsed":"17","stateGasSpill":"6","stateGasSpillRefunded":"1"}}',
        '{"schemaVersion":"1","operation":"charge-state","outcome":"success","state":{"gasLeft":"0","stateReservoir":"0","stateGasUsed":"8","stateGasSpill":"5","stateGasSpillRefunded":"1"}}',
        '{"schemaVersion":"1","operation":"charge-state","outcome":"outOfGas","state":{"gasLeft":"2","stateReservoir":"0","stateGasUsed":"5","stateGasSpill":"2","stateGasSpillRefunded":"1"}}',
        '{"schemaVersion":"1","operation":"charge-state","outcome":"success","state":{"gasLeft":"3","stateReservoir":"-2","stateGasUsed":"14","stateGasSpill":"9","stateGasSpillRefunded":"2"}}',
        '{"schemaVersion":"1","operation":"charge-state","outcome":"success","state":{"gasLeft":"18446744073709551614","stateReservoir":"0","stateGasUsed":"1","stateGasSpill":"1","stateGasSpillRefunded":"0"}}',
        '{"schemaVersion":"1","operation":"charge-state","outcome":"success","state":{"gasLeft":"0","stateReservoir":"0","stateGasUsed":"9223372036854775807","stateGasSpill":"0","stateGasSpillRefunded":"0"}}',
        '{"schemaVersion":"1","operation":"charge-state","outcome":"success","state":{"gasLeft":"0","stateReservoir":"0","stateGasUsed":"1","stateGasSpill":"9223372036854775807","stateGasSpillRefunded":"0"}}',
        '{"schemaVersion":"1","operation":"charge-state","outcome":"success","state":{"gasLeft":"0","stateReservoir":"0","stateGasUsed":"9223372036854775807","stateGasSpill":"0","stateGasSpillRefunded":"0"}}'
    )
    [string[]] $leanRequests = [System.IO.File]::ReadAllLines($leanRequestOutput)
    Invoke-CanonicalNdjson 'dotnet' @($evmAssembly, 'formal', 'charge-state') $evmVectorOutput $leanRequests

    [string[]] $leanResponses = [System.IO.File]::ReadAllLines($leanVectorOutput)
    [string[]] $actualResponses = [System.IO.File]::ReadAllLines($evmVectorOutput)
    Assert-ExactLines $expectedResponses $leanResponses
    Assert-ExactLines $expectedResponses $actualResponses
    Assert-FileBytesEqual $leanVectorOutput $evmVectorOutput

    [string[]] $leanTransitionRequests = [System.IO.File]::ReadAllLines($leanTransitionRequestOutput)
    if ($leanTransitionRequests.Count -ne 28)
    {
        throw "Expected 28 state-gas transition requests, received $($leanTransitionRequests.Count)."
    }

    Invoke-CanonicalNdjson 'dotnet' @($evmAssembly, 'formal', 'state-transition') $evmTransitionVectorOutput $leanTransitionRequests
    [string[]] $leanTransitionResponses = [System.IO.File]::ReadAllLines($leanTransitionVectorOutput)
    [string[]] $evmTransitionResponses = [System.IO.File]::ReadAllLines($evmTransitionVectorOutput)
    if ($leanTransitionResponses.Count -ne 28 -or $evmTransitionResponses.Count -ne 28)
    {
        throw "Expected 28 state-gas transition responses from each implementation."
    }

    Assert-ExactLines $leanTransitionResponses $evmTransitionResponses
    Assert-FileBytesEqual $leanTransitionVectorOutput $evmTransitionVectorOutput
    & (Join-Path $leanDirectory 'ReceiptTerminalFoldExtractor/Verify.ps1') -RepoRoot $repositoryRoot
    & (Join-Path $leanDirectory 'SequentialBlockTransactionFoldExtractor/Verify.ps1') -RepoRoot $repositoryRoot
    & (Join-Path $leanDirectory 'SequentialBlockPostTransactionFinalizationExtractor/Verify-Publication.ps1') -RepoRoot $repositoryRoot
    & (Join-Path $leanDirectory 'BlockProcessorExtractor/Verify-Branch.ps1') -RepoRoot $repositoryRoot
    & (Join-Path $leanDirectory 'BlockProcessorExtractor/Verify-Outer.ps1') -RepoRoot $repositoryRoot -Slice all
    & (Join-Path $leanDirectory 'OrdinaryTransactionRefundAdapterExtractor/Verify.ps1') -RepoRoot $repositoryRoot
    & (Join-Path $leanDirectory 'OrdinaryEvmCompletionExtractor/Verify-Candidate.ps1') -RepoRoot $repositoryRoot -SkipBuild
    Write-Output 'Verification passed.'
}
finally
{
    if ($null -ne $temporaryDirectory)
    {
        Remove-SystemTemporaryDirectory $temporaryDirectory $temporaryRoot
    }
}
