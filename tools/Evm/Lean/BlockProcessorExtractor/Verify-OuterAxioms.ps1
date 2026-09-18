# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param([string]$Lake = "lake", [ValidateSet("finite", "publication")][string]$Slice = "publication")
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
$package = [IO.Path]::GetFullPath($PSScriptRoot)
$finite = $Slice -eq "finite"
$sliceName = if ($finite) { "NormalFiniteBranchCompletion" } else { "BlockchainPublication" }
$inventoryName = if ($finite) { "EXPORTED_FINITE_THEOREMS.txt" } else { "EXPORTED_OUTER_THEOREMS.txt" }
$pinName = if ($finite) { "FINITE_CENSUS_PINS.json" } else { "PUBLICATION_CENSUS_PINS.json" }
$pinDocument = [Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText((Join-Path $package $pinName)))
try {
    $pin = $pinDocument.RootElement
    $fields = @($pin.EnumerateObject() | ForEach-Object Name)
    if (($fields -join ',') -cne 'schemaVersion,name,count,inventory' -or
        $pin.GetProperty('schemaVersion').GetInt32() -ne 1 -or
        $pin.GetProperty('name').GetString() -cne $sliceName) { throw "Outer census pin header changed." }
    $identity = $pin.GetProperty('inventory')
    if ((@($identity.EnumerateObject() | ForEach-Object Name) -join ',') -cne 'path,sha256' -or
        $identity.GetProperty('path').GetString() -cne "tools/Evm/Lean/BlockProcessorExtractor/$inventoryName") {
        throw "Outer census inventory identity changed."
    }
    $inventoryCount = $pin.GetProperty('count').GetInt32()
    $inventoryHash = $identity.GetProperty('sha256').GetString()
    if ($inventoryCount -le 0 -or $inventoryHash -cnotmatch '^[0-9a-f]{64}$') { throw "Outer census pin domain changed." }
}
finally { $pinDocument.Dispose() }
$inventoryBytes = [IO.File]::ReadAllBytes((Join-Path $package $inventoryName))
$namespaces = @(
    'SequentialBlockPostTransactionFinalizationExtractor.Generated.SequentialBlockPostTransactionFinalization',
    'SequentialBlockPostTransactionFinalizationExtractor.Specification.SequentialBlockPostTransactionFinalization',
    'SequentialBlockPostTransactionFinalizationExtractor.Refinement.SequentialBlockPostTransactionFinalization',
    'SequentialBlockPostTransactionFinalizationExtractor.Vectors.SequentialBlockPostTransactionFinalizationVectors',
    'SequentialBlockTransactionFoldExtractor',
    'ReceiptTerminalFoldExtractor',
    'Eip803x.BlockReceiptGas',
    'Eip803x.TransactionGas',
    'Eip803x.Generated.BlockReceiptGasAccountingKernel',
    'Eip803x.Generated.TransactionGasInitializationKernel',
    'Eip803x.Refinement.BlockReceiptGasAccounting',
    'Eip803x.Refinement.TransactionGasInitialization'
)
$namespaces += @(
    'SequentialBlockPostTransactionFinalizationExtractor.Generated.ProcessOneValidatedPublication',
    'SequentialBlockPostTransactionFinalizationExtractor.Specification.ProcessOneValidatedPublication',
    'SequentialBlockPostTransactionFinalizationExtractor.Refinement.ProcessOneValidatedPublication',
    'SequentialBlockPostTransactionFinalizationExtractor.Vectors.ProcessOneValidatedPublicationVectors',
    'BlockProcessorExtractor.Generated.BranchAcceptedIteration',
    'BlockProcessorExtractor.Specification.BranchAcceptedIteration',
    'BlockProcessorExtractor.Refinement.BranchAcceptedIteration',
    'BlockProcessorExtractor.Vectors.BranchAcceptedIterationVectors'
)

$namespaces += @(
    'BlockProcessorExtractor.Generated.NormalFiniteBranchCompletion',
    'BlockProcessorExtractor.Specification.NormalFiniteBranchCompletion',
    'BlockProcessorExtractor.Refinement.NormalFiniteBranchCompletion',
    'BlockProcessorExtractor.Vectors.FiniteBranchVectors'
)
$ownNamespaces = @($namespaces[-4..-1])
if (-not $finite) {
    $ownNamespaces = @(
        'BlockProcessorExtractor.Generated.BlockchainPublication',
        'BlockProcessorExtractor.Specification.BlockchainPublication',
        'BlockProcessorExtractor.Refinement.BlockchainPublication',
        'BlockProcessorExtractor.Vectors.OuterBlockVectors'
    )
    $namespaces += $ownNamespaces
}

function Read-OuterInventory([byte[]]$Bytes) {
    $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
    if ($digest -cne $inventoryHash) {
        throw "Outer exported-theorem inventory changed; explicit review is required."
    }
    $names = @([Text.Encoding]::UTF8.GetString($Bytes).TrimEnd("`n").Split("`n"))
    if ($names.Count -ne $inventoryCount -or [Collections.Generic.HashSet[string]]::new([string[]]$names, [StringComparer]::Ordinal).Count -ne $names.Count) {
        throw "Outer theorem inventory must contain the complete distinct descendant export roster."
    }
    foreach ($name in $names) {
        if ($name -notmatch '^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+$' -or
            -not @($namespaces | Where-Object { $name.StartsWith($_ + '.', [StringComparison]::Ordinal) }).Count) {
            throw "Invalid outer theorem name: $name"
        }
    }
    return ,$names
}
$expected = Read-OuterInventory $inventoryBytes
$upstreamName = if ($finite) { "EXPORTED_BRANCH_THEOREMS.txt" } else { "EXPORTED_FINITE_THEOREMS.txt" }
$upstream = [IO.File]::ReadAllLines((Join-Path $package $upstreamName))
$inherited = @($expected | Where-Object {
    $name = $_
    -not @($ownNamespaces | Where-Object { $name.StartsWith($_ + '.', [StringComparison]::Ordinal) }).Count
})
if (($upstream -join "`n") -cne ($inherited -join "`n")) { throw "Outer census changed the exact accepted upstream export lineage." }

$checker = @'
open Lean Elab Command

def checkOuterAxioms (name : Name) : CommandElabM (Array Name) := do
  let dependencies ← Lean.collectAxioms name
  let allowed := ["propext", "Classical.choice", "Quot.sound"]
  for dependency in dependencies do
    unless allowed.contains dependency.toString do
      throwError "OUTER_AXIOM_FORBIDDEN {name}: {dependency}"
  return dependencies

elab "audit_outer_export " target:ident : command => do
  let name := target.getId
  let env ← getEnv
  match env.find? name with
  | some (.thmInfo _) => pure ()
  | _ => throwError "OUTER_AXIOM_MISSING {name}"
  let dependencies ← checkOuterAxioms name
  let result := Json.mkObj [("theorem", toJson name.toString),
    ("axioms", toJson (dependencies.map Name.toString))]
  logInfo m!"OUTER_AXIOM_RESULT|{result.compress}"
'@
$completeness = @'
run_cmd do
  let expected : Array String := #[__EXPECTED__]
  let namespaces := [__NAMESPACES__]
  let env ← getEnv
  let mut found := 0
  for (name, info) in env.constants do
    if namespaces.any (fun namespaceName => name.toString.startsWith (namespaceName ++ ".")) then
      if let .thmInfo _ := info then
        let _ ← checkOuterAxioms name
        unless expected.contains name.toString do
          throwError "OUTER_AXIOM_UNEXPECTED_EXPORT {name}"
        found := found + 1
  unless found == expected.size do
    throwError "OUTER_AXIOM_EXPORT_COUNT {found} != {expected.size}"
'@
$completeness = $completeness.Replace('__NAMESPACES__', (($namespaces | ForEach-Object { '"' + $_ + '"' }) -join ','))
$imports = "import Lean`nimport SequentialBlockPostTransactionFinalizationExtractor.Vectors.SequentialBlockPostTransactionFinalizationVectors`nimport SequentialBlockTransactionFoldExtractor.Vectors.SequentialBlockTransactionFoldVectors`n"
$imports += "import SequentialBlockPostTransactionFinalizationExtractor.Vectors.ProcessOneValidatedPublicationVectors`n"
$imports += "import BlockProcessorExtractor.Vectors.BranchAcceptedIterationVectors`n"
$imports += "import BlockProcessorExtractor.Vectors.FiniteBranchVectors`n"
if (-not $finite) { $imports += "import BlockProcessorExtractor.Vectors.OuterBlockVectors`n" }

function Invoke-OuterAuditLean([string]$Path) {
    $output = @(& $Lake env lean -DwarningAsError=true -DmaxHeartbeats=800000 $Path 2>&1 | ForEach-Object { $_.ToString() })
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join [Environment]::NewLine) }
}
function Read-OuterAuditResults([string]$Output, [string[]]$Names) {
    $records = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($line in ($Output -split '\r?\n')) {
        if ($line -match 'OUTER_AXIOM_RESULT\|(\{.*\})$') {
            $record = $Matches[1] | ConvertFrom-Json
            if (@($record.PSObject.Properties.Name).Count -ne 2 -or
                $record.PSObject.Properties.Name -notcontains 'theorem' -or $record.PSObject.Properties.Name -notcontains 'axioms' -or
                $record.theorem -isnot [string] -or $record.axioms -isnot [array] -or
                $records.ContainsKey($record.theorem) -or $Names -cnotcontains $record.theorem) {
                throw "Malformed, duplicate, or unexpected outer axiom result."
            }
            foreach ($dependency in $record.axioms) {
                if (@('propext', 'Classical.choice', 'Quot.sound') -cnotcontains $dependency) {
                    throw "Forbidden outer axiom result: $dependency"
                }
            }
            $records.Add($record.theorem, $record)
        }
    }
    if ($records.Count -ne $Names.Count) { throw "Missing outer axiom-audit results." }
    return ,$records
}
function Complete-OuterExports([string[]]$Names) {
    $quoted = ($Names | ForEach-Object { '"' + $_ + '"' }) -join ','
    return $completeness.Replace('__EXPECTED__', $quoted)
}

$scratch = [IO.Path]::GetFullPath((Join-Path $package (".outer-axioms-" + [Guid]::NewGuid().ToString("N"))))
if (-not $scratch.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Outer axiom-audit scratch escaped the package."
}
try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    Push-Location $package
    try {
        $main = $imports + $checker + "`n" + (Complete-OuterExports $expected) + "`n" +
            (($expected | ForEach-Object { "audit_outer_export $_" }) -join "`n")
        $mainPath = Join-Path $scratch "ExportAxioms.lean"
        [IO.File]::WriteAllText($mainPath, $main, [Text.UTF8Encoding]::new($false))
        $result = Invoke-OuterAuditLean $mainPath
        if ($result.ExitCode -ne 0) { throw "Outer exported-theorem axiom baseline failed: $($result.Output)" }
        $records = Read-OuterAuditResults $result.Output $expected
        foreach ($name in $expected) { Write-Host ($name + " : [" + ($records[$name].axioms -join ', ') + "]") }

        foreach ($altered in @(
            ($expected[1..($expected.Count - 1)] -join "`n"),
            (($expected + $expected[0]) -join "`n"),
            (($expected + 'SequentialBlockPostTransactionFinalizationExtractor.Refinement.SequentialBlockPostTransactionFinalization.unlisted') -join "`n")
        )) {
            $rejected = $false
            try { $null = Read-OuterInventory ([Text.Encoding]::UTF8.GetBytes($altered + "`n")) }
            catch { $rejected = $_.Exception.Message -eq "Outer exported-theorem inventory changed; explicit review is required." }
            if (-not $rejected) { throw "Outer inventory negative control survived." }
        }

        $first = 'OUTER_AXIOM_RESULT|{"theorem":"' + $expected[0] + '","axioms":[]}'
        foreach ($test in @(
            @{ Output = ''; Error = 'Missing outer axiom-audit results.' },
            @{ Output = $result.Output + "`n" + $first; Error = 'Malformed, duplicate, or unexpected outer axiom result.' },
            @{ Output = 'OUTER_AXIOM_RESULT|{"theorem":"unexpected","axioms":[]}'; Error = 'Malformed, duplicate, or unexpected outer axiom result.' },
            @{ Output = 'OUTER_AXIOM_RESULT|{"theorem":"' + $expected[0] + '"}'; Error = 'Malformed, duplicate, or unexpected outer axiom result.' },
            @{ Output = 'OUTER_AXIOM_RESULT|{"theorem":"' + $expected[0] + '","axioms":["injected"]}'; Error = 'Forbidden outer axiom result: injected' }
        )) {
            $rejected = $false
            try { $null = Read-OuterAuditResults $test.Output $expected }
            catch { $rejected = $_.Exception.Message -eq $test.Error }
            if (-not $rejected) { throw "Outer axiom result negative control survived: $($test.Error)" }
        }
        foreach ($namespaceName in $namespaces) {
            foreach ($forbidden in @($false, $true)) {
                $prefix = $namespaceName + '.Nested.Audit'
                $declarations = if ($forbidden) {
                    "  axiom injectedAxiom : False`n  theorem injectedExport : False := injectedAxiom"
                } else { '  theorem injectedExport : True := True.intro' }
                $body = "namespace $prefix`n$declarations`nend $prefix`n" + (Complete-OuterExports $expected)
                $testPath = Join-Path $scratch 'LineageNegative.lean'
                [IO.File]::WriteAllText($testPath, $imports + $checker + "`n" + $body, [Text.UTF8Encoding]::new($false))
                $negative = Invoke-OuterAuditLean $testPath
                $marker = if ($forbidden) { "OUTER_AXIOM_FORBIDDEN $prefix.injectedExport: $prefix.injectedAxiom" }
                    else { "OUTER_AXIOM_UNEXPECTED_EXPORT $prefix.injectedExport" }
                if ($negative.ExitCode -eq 0 -or -not $negative.Output.Contains($marker, [StringComparison]::Ordinal) -or
                    $negative.Output -match 'unknown (identifier|constant|module)|unexpected token|maximum recursion depth|maximum heartbeats|maximum number of steps|deterministic timeout') {
                    throw "Outer lineage completeness control failed: $prefix forbidden=$forbidden`: $($negative.Output)"
                }
            }
        }

        foreach ($test in @(
            @{ Name = 'Missing'; Imports = "import Lean`n"; Body = 'audit_outer_export DoesNotExist'; Error = 'OUTER_AXIOM_MISSING DoesNotExist' },
            @{ Name = 'Forbidden'; Imports = "import Lean`n"; Body = "axiom outerInjectedAxiom : False`ntheorem outerInjectedExport : False := outerInjectedAxiom`naudit_outer_export outerInjectedExport"; Error = 'OUTER_AXIOM_FORBIDDEN outerInjectedExport: outerInjectedAxiom' },
            @{ Name = 'UnexpectedExport'; Imports = $imports; Body = "namespace SequentialBlockPostTransactionFinalizationExtractor.Refinement.SequentialBlockPostTransactionFinalization`ntheorem outerUnexpectedExport : True := True.intro`nend SequentialBlockPostTransactionFinalizationExtractor.Refinement.SequentialBlockPostTransactionFinalization`n" + (Complete-OuterExports $expected); Error = 'OUTER_AXIOM_UNEXPECTED_EXPORT SequentialBlockPostTransactionFinalizationExtractor.Refinement.SequentialBlockPostTransactionFinalization.outerUnexpectedExport' },
            @{ Name = 'NestedExport'; Imports = $imports; Body = "namespace SequentialBlockPostTransactionFinalizationExtractor.Refinement.SequentialBlockPostTransactionFinalization.Nested.Audit`ntheorem injectedNestedExport : True := True.intro`nend SequentialBlockPostTransactionFinalizationExtractor.Refinement.SequentialBlockPostTransactionFinalization.Nested.Audit`n" + (Complete-OuterExports $expected); Error = 'OUTER_AXIOM_UNEXPECTED_EXPORT SequentialBlockPostTransactionFinalizationExtractor.Refinement.SequentialBlockPostTransactionFinalization.Nested.Audit.injectedNestedExport' },
            @{ Name = 'NestedForbidden'; Imports = $imports; Body = "namespace SequentialBlockPostTransactionFinalizationExtractor.Refinement.SequentialBlockPostTransactionFinalization.Nested.Audit`naxiom injectedNestedAxiom : False`ntheorem injectedNestedExport : False := injectedNestedAxiom`nend SequentialBlockPostTransactionFinalizationExtractor.Refinement.SequentialBlockPostTransactionFinalization.Nested.Audit`n" + (Complete-OuterExports $expected); Error = 'OUTER_AXIOM_FORBIDDEN SequentialBlockPostTransactionFinalizationExtractor.Refinement.SequentialBlockPostTransactionFinalization.Nested.Audit.injectedNestedExport: SequentialBlockPostTransactionFinalizationExtractor.Refinement.SequentialBlockPostTransactionFinalization.Nested.Audit.injectedNestedAxiom' },
            @{ Name = 'OmittedExport'; Imports = $imports; Body = (Complete-OuterExports $expected[1..($expected.Count - 1)]); Error = 'OUTER_AXIOM_UNEXPECTED_EXPORT ' + $expected[0] }
        )) {
            $testPath = Join-Path $scratch ($test.Name + ".lean")
            [IO.File]::WriteAllText($testPath, $test.Imports + $checker + "`n" + $test.Body, [Text.UTF8Encoding]::new($false))
            $negative = Invoke-OuterAuditLean $testPath
            if ($negative.ExitCode -eq 0 -or -not $negative.Output.Contains($test.Error, [StringComparison]::Ordinal) -or
                $negative.Output -match 'unknown (identifier|constant|module)|unexpected token|maximum recursion depth|maximum heartbeats|maximum number of steps|deterministic timeout') {
                throw "Outer axiom negative/completeness control failed: $($test.Name): $($negative.Output)"
            }
        }
        Write-Host "Verified all frozen outer theorem exports, standard-only axioms, and negative/completeness controls."
    }
    finally { Pop-Location }
}
finally {
    $resolved = [IO.Path]::GetFullPath($scratch)
    if ($resolved.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
