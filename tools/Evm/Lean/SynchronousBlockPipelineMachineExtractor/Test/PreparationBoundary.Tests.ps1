# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param([string] $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../../../..')).Path)

$ErrorActionPreference = 'Stop'
$assemblyDirectory = Join-Path $RepositoryRoot 'tools/artifacts/bin/SynchronousBlockPipelineMachineExtractor/release'
$loadContext = [System.Runtime.Loader.AssemblyLoadContext]::new('PreparationBoundaryTests', $true)
foreach ($name in @('Microsoft.CodeAnalysis', 'Microsoft.CodeAnalysis.CSharp', 'Nethermind.Evm.Lean.SynchronousBlockPipelineMachineExtractor')) {
    $assembly = $loadContext.LoadFromAssemblyPath((Join-Path $assemblyDirectory "$name.dll"))
    if ($name -eq 'Microsoft.CodeAnalysis.CSharp') { $csharpAssembly = $assembly }
}
$auditType = $assembly.GetType('Nethermind.Evm.Lean.SynchronousBlockPipelineMachineExtractor.SourceAudit', $true)
$routeType = $auditType.Assembly.GetType('Nethermind.Evm.Lean.SynchronousBlockPipelineMachineExtractor.ProductionRoute', $true)
$flags = [Reflection.BindingFlags]'Static, NonPublic'
$guard = $auditType.GetMethod('RequirePreparationOutsideRetry', $flags)
$prepare = '_balManager.PrepareForProcessing(suggestedBlock, spec, options);'

function Assert-PreparationBoundary([string] $Body, [string] $ExpectedError) {
    $source = "class BlockProcessor { void ProcessOne() { $Body } }"
    $parse = $csharpAssembly.GetType('Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree').GetMethods() |
        Where-Object { $_.Name -eq 'ParseText' -and $_.GetParameters().Count -eq 5 -and $_.GetParameters()[0].ParameterType -eq [string] }
    $tree = $parse.Invoke($null, @($source, $null, '', $null, [Threading.CancellationToken]::None))
    $nodes = $tree.GetRoot().DescendantNodes()
    $method = @($nodes | Where-Object { $_.GetType().Name -eq 'MethodDeclarationSyntax' })[0]
    $processingTry = @($nodes | Where-Object { $_.GetType().Name -eq 'TryStatementSyntax' })[0]
    $failure = $null
    try {
        $null = $guard.Invoke($null, @($method.PSObject.BaseObject, $processingTry.PSObject.BaseObject, 'regression.cs'))
    }
    catch {
        $failure = $_.Exception.GetBaseException().Message
    }
    if ($ExpectedError) {
        if (!$failure -or !$failure.StartsWith($ExpectedError, [StringComparison]::Ordinal)) {
            throw "Expected '$ExpectedError'; received '$failure'."
        }
    }
    elseif ($failure) {
        throw "Valid preparation boundary rejected: $failure"
    }
}

$valid = "$prepare try { ProcessBlock(); } finally { Cleanup(); }"
Assert-PreparationBoundary $valid ''
foreach ($body in @(
    "try { $prepare ProcessBlock(); } finally { Cleanup(); }",
    "try { ProcessBlock(); } finally { Cleanup(); } $prepare",
    "$prepare $prepare try { ProcessBlock(); } finally { Cleanup(); }",
    "if (enabled) { $prepare } try { ProcessBlock(); } finally { Cleanup(); }"
)) {
    Assert-PreparationBoundary $body 'BAL preparation must be a direct statement before the processing retry boundary'
}

$hook = $routeType.GetField('HookDependencies', $flags).GetValue($null) |
    Where-Object { $_.Hook.ToString() -eq 'PrepareBal' }
$property = $hook.GetType().GetProperty('FailureDisposition')
$original = $property.GetValue($hook)
try {
    $property.SetValue($hook, [Enum]::Parse($property.PropertyType, 'RetrySequential'))
    Assert-PreparationBoundary $valid 'BAL preparation failures escape the processing retry boundary.'
}
finally {
    $property.SetValue($hook, $original)
}
Assert-PreparationBoundary $valid ''
Write-Output 'PASS: original disposition rejected; four placement mutations rejected; valid preparation accepted before and after mutation.'
