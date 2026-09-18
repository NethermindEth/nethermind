# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param([string]$Lake = 'lake', [string]$RepositoryRoot = (Resolve-Path "$PSScriptRoot/../../../..").Path)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'PrecompileFullFrameExtractor.csproj'
$tests = Join-Path $PSScriptRoot 'Test/PrecompileFullFrameExtractor.Test.csproj'
$scratch = [System.IO.Directory]::CreateTempSubdirectory('precompile-full-frame-verify-').FullName
try {
    & dotnet build $tests -c Release -p:SaveDiskSpace=true -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'Stage C warning-as-error build failed.' }
    & dotnet test --project $tests -c Release --no-build -- --filter FullyQualifiedName~PrecompileFullFrameExtractorTests --minimum-expected-tests 104
    if ($LASTEXITCODE -ne 0) { throw 'Stage C mutation suite failed.' }
    foreach ($name in @('first', 'second')) {
        & dotnet run --project $project -c Release --no-build -- extract $RepositoryRoot (Join-Path $scratch $name)
        if ($LASTEXITCODE -ne 0) { throw "Stage C $name emission failed." }
    }
    foreach ($file in @(Get-ChildItem -LiteralPath (Join-Path $scratch 'first') -File)) {
        $other = Join-Path $scratch "second/$($file.Name)"
        if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $other).Hash) {
            throw "Stage C nondeterministic artifact $($file.Name)."
        }
    }
    & dotnet run --project $project -c Release --no-build -- validate $RepositoryRoot (Join-Path $scratch 'second')
    if ($LASTEXITCODE -ne 0) { throw 'Stage C byte validation failed.' }
    & dotnet run --project $project -c Release --no-build -- validate $RepositoryRoot (Join-Path $PSScriptRoot 'Generated')
    if ($LASTEXITCODE -ne 0) { throw 'Stage C checked-in artifacts drifted.' }
    Push-Location $PSScriptRoot
    try {
        & $Lake build -KwarningAsError=true
        if ($LASTEXITCODE -ne 0) { throw 'Stage C Lake warning-as-error gate failed.' }
        foreach ($module in @('Generated/PrecompileFullFrame.lean', 'Specification/Reference.lean', 'Refinement/PrecompileFullFrame.lean')) {
            & $Lake env lean -DwarningAsError=true $module
            if ($LASTEXITCODE -ne 0) { throw "Stage C direct Lean gate failed for $module." }
        }
    }
    finally { Pop-Location }
}
finally {
    $resolved = [System.IO.Path]::GetFullPath($scratch)
    $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if (-not $resolved.StartsWith($temporaryRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [System.IO.Path]::GetFileName($resolved).StartsWith('precompile-full-frame-verify-', [System.StringComparison]::Ordinal)) {
        throw 'Refusing cleanup outside the owned verification directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
