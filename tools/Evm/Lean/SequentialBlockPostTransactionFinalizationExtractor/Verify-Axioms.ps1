# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param([string]$Lake = "lake", [switch]$IncludePublication)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
$package = [IO.Path]::GetFullPath($PSScriptRoot)
$inventoryName = if ($IncludePublication) { "PUBLICATION_EXPORTED_THEOREMS.txt" } else { "EXPORTED_THEOREMS.txt" }
$inventoryBytes = [IO.File]::ReadAllBytes((Join-Path $package $inventoryName))
$inventoryCount = if ($IncludePublication) { 1599 } else { 1305 }
$inventoryHash = if ($IncludePublication) { '9af770169f8539bf833aa4519a07b6a52fd0bffad5d513d40546f9ba0bf72312' } else { 'dea78ac75c216204477c271343fedacf453649ca3097f77e92867f8efdbcdfcd' }
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
if ($IncludePublication) { $namespaces += @(
    'SequentialBlockPostTransactionFinalizationExtractor.Generated.ProcessOneValidatedPublication',
    'SequentialBlockPostTransactionFinalizationExtractor.Specification.ProcessOneValidatedPublication',
    'SequentialBlockPostTransactionFinalizationExtractor.Refinement.ProcessOneValidatedPublication',
    'SequentialBlockPostTransactionFinalizationExtractor.Vectors.ProcessOneValidatedPublicationVectors'
) }

function Read-FinalizationInventory([byte[]]$Bytes) {
    $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
    if ($digest -cne $inventoryHash) {
        throw "Finalization exported-theorem inventory changed; explicit review is required."
    }
    $names = @([Text.Encoding]::UTF8.GetString($Bytes).TrimEnd("`n").Split("`n"))
    if ($names.Count -ne $inventoryCount -or [Collections.Generic.HashSet[string]]::new([string[]]$names, [StringComparer]::Ordinal).Count -ne $names.Count) {
        throw "Finalization theorem inventory must contain the complete distinct descendant export roster."
    }
    foreach ($name in $names) {
        if ($name -notmatch '^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+$' -or
            -not @($namespaces | Where-Object { $name.StartsWith($_ + '.', [StringComparison]::Ordinal) }).Count) {
            throw "Invalid finalization theorem name: $name"
        }
    }
    return ,$names
}
$expected = Read-FinalizationInventory $inventoryBytes

$checker = @'
open Lean Elab Command

def checkFinalizationAxioms (name : Name) : CommandElabM (Array Name) := do
  let dependencies ← Lean.collectAxioms name
  let allowed := ["propext", "Classical.choice", "Quot.sound"]
  for dependency in dependencies do
    unless allowed.contains dependency.toString do
      throwError "FINALIZATION_AXIOM_FORBIDDEN {name}: {dependency}"
  return dependencies

elab "audit_finalization_export " target:ident : command => do
  let name := target.getId
  let env ← getEnv
  match env.find? name with
  | some (.thmInfo _) => pure ()
  | _ => throwError "FINALIZATION_AXIOM_MISSING {name}"
  let dependencies ← checkFinalizationAxioms name
  let result := Json.mkObj [("theorem", toJson name.toString),
    ("axioms", toJson (dependencies.map Name.toString))]
  logInfo m!"FINALIZATION_AXIOM_RESULT|{result.compress}"
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
        let _ ← checkFinalizationAxioms name
        unless expected.contains name.toString do
          throwError "FINALIZATION_AXIOM_UNEXPECTED_EXPORT {name}"
        found := found + 1
  unless found == expected.size do
    throwError "FINALIZATION_AXIOM_EXPORT_COUNT {found} != {expected.size}"
'@
$completeness = $completeness.Replace('__NAMESPACES__', (($namespaces | ForEach-Object { '"' + $_ + '"' }) -join ','))
$imports = "import Lean`nimport SequentialBlockPostTransactionFinalizationExtractor.Vectors.SequentialBlockPostTransactionFinalizationVectors`nimport SequentialBlockTransactionFoldExtractor.Vectors.SequentialBlockTransactionFoldVectors`n"
if ($IncludePublication) { $imports += "import SequentialBlockPostTransactionFinalizationExtractor.Vectors.ProcessOneValidatedPublicationVectors`n" }

function Invoke-FinalizationAuditLean([string]$Path) {
    $output = @(& $Lake env lean -DwarningAsError=true -DmaxHeartbeats=800000 $Path 2>&1 | ForEach-Object { $_.ToString() })
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join [Environment]::NewLine) }
}
function Read-FinalizationAuditResults([string]$Output, [string[]]$Names) {
    $records = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($line in ($Output -split '\r?\n')) {
        if ($line -match 'FINALIZATION_AXIOM_RESULT\|(\{.*\})$') {
            $record = $Matches[1] | ConvertFrom-Json
            if (@($record.PSObject.Properties.Name).Count -ne 2 -or
                $record.PSObject.Properties.Name -notcontains 'theorem' -or $record.PSObject.Properties.Name -notcontains 'axioms' -or
                $record.theorem -isnot [string] -or $record.axioms -isnot [array] -or
                $records.ContainsKey($record.theorem) -or $Names -cnotcontains $record.theorem) {
                throw "Malformed, duplicate, or unexpected finalization axiom result."
            }
            foreach ($dependency in $record.axioms) {
                if (@('propext', 'Classical.choice', 'Quot.sound') -cnotcontains $dependency) {
                    throw "Forbidden finalization axiom result: $dependency"
                }
            }
            $records.Add($record.theorem, $record)
        }
    }
    if ($records.Count -ne $Names.Count) { throw "Missing finalization axiom-audit results." }
    return ,$records
}
function Complete-FinalizationExports([string[]]$Names) {
    $quoted = ($Names | ForEach-Object { '"' + $_ + '"' }) -join ','
    return $completeness.Replace('__EXPECTED__', $quoted)
}

$scratch = [IO.Path]::GetFullPath((Join-Path $package (".finalization-axioms-" + [Guid]::NewGuid().ToString("N"))))
if (-not $scratch.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Finalization axiom-audit scratch escaped the package."
}
try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    Push-Location $package
    try {
        $main = $imports + $checker + "`n" + (Complete-FinalizationExports $expected) + "`n" +
            (($expected | ForEach-Object { "audit_finalization_export $_" }) -join "`n")
        $mainPath = Join-Path $scratch "ExportAxioms.lean"
        [IO.File]::WriteAllText($mainPath, $main, [Text.UTF8Encoding]::new($false))
        $result = Invoke-FinalizationAuditLean $mainPath
        if ($result.ExitCode -ne 0) { throw "Finalization exported-theorem axiom baseline failed: $($result.Output)" }
        $records = Read-FinalizationAuditResults $result.Output $expected
        foreach ($name in $expected) { Write-Host ($name + " : [" + ($records[$name].axioms -join ', ') + "]") }

        foreach ($altered in @(
            ($expected[1..($expected.Count - 1)] -join "`n"),
            (($expected + $expected[0]) -join "`n"),
            (($expected + 'SequentialBlockPostTransactionFinalizationExtractor.Refinement.SequentialBlockPostTransactionFinalization.unlisted') -join "`n")
        )) {
            $rejected = $false
            try { $null = Read-FinalizationInventory ([Text.Encoding]::UTF8.GetBytes($altered + "`n")) }
            catch { $rejected = $_.Exception.Message -eq "Finalization exported-theorem inventory changed; explicit review is required." }
            if (-not $rejected) { throw "Finalization inventory negative control survived." }
        }

        $first = 'FINALIZATION_AXIOM_RESULT|{"theorem":"' + $expected[0] + '","axioms":[]}'
        foreach ($test in @(
            @{ Output = ''; Error = 'Missing finalization axiom-audit results.' },
            @{ Output = $result.Output + "`n" + $first; Error = 'Malformed, duplicate, or unexpected finalization axiom result.' },
            @{ Output = 'FINALIZATION_AXIOM_RESULT|{"theorem":"unexpected","axioms":[]}'; Error = 'Malformed, duplicate, or unexpected finalization axiom result.' },
            @{ Output = 'FINALIZATION_AXIOM_RESULT|{"theorem":"' + $expected[0] + '"}'; Error = 'Malformed, duplicate, or unexpected finalization axiom result.' },
            @{ Output = 'FINALIZATION_AXIOM_RESULT|{"theorem":"' + $expected[0] + '","axioms":["injected"]}'; Error = 'Forbidden finalization axiom result: injected' }
        )) {
            $rejected = $false
            try { $null = Read-FinalizationAuditResults $test.Output $expected }
            catch { $rejected = $_.Exception.Message -eq $test.Error }
            if (-not $rejected) { throw "Finalization axiom result negative control survived: $($test.Error)" }
        }
        foreach ($namespaceName in $namespaces) {
            foreach ($forbidden in @($false, $true)) {
                $prefix = $namespaceName + '.Nested.Audit'
                $declarations = if ($forbidden) {
                    "axiom injectedAxiom : False`ntheorem injectedExport : False := injectedAxiom"
                } else { 'theorem injectedExport : True := True.intro' }
                $body = "namespace $prefix`n$declarations`nend $prefix`n" + (Complete-FinalizationExports $expected)
                $testPath = Join-Path $scratch 'LineageNegative.lean'
                [IO.File]::WriteAllText($testPath, $imports + $checker + "`n" + $body, [Text.UTF8Encoding]::new($false))
                $negative = Invoke-FinalizationAuditLean $testPath
                $marker = if ($forbidden) { "FINALIZATION_AXIOM_FORBIDDEN $prefix.injectedExport: $prefix.injectedAxiom" }
                    else { "FINALIZATION_AXIOM_UNEXPECTED_EXPORT $prefix.injectedExport" }
                if ($negative.ExitCode -eq 0 -or -not $negative.Output.Contains($marker, [StringComparison]::Ordinal) -or
                    $negative.Output -match 'unknown (identifier|constant|module)|unexpected token|maximum recursion depth|maximum heartbeats') {
                    throw "Finalization lineage completeness control failed: $prefix forbidden=$forbidden`: $($negative.Output)"
                }
            }
        }

        foreach ($test in @(
            @{ Name = 'Missing'; Imports = "import Lean`n"; Body = 'audit_finalization_export DoesNotExist'; Error = 'FINALIZATION_AXIOM_MISSING DoesNotExist' },
            @{ Name = 'Forbidden'; Imports = "import Lean`n"; Body = "axiom finalizationInjectedAxiom : False`ntheorem finalizationInjectedExport : False := finalizationInjectedAxiom`naudit_finalization_export finalizationInjectedExport"; Error = 'FINALIZATION_AXIOM_FORBIDDEN finalizationInjectedExport: finalizationInjectedAxiom' },
            @{ Name = 'UnexpectedExport'; Imports = $imports; Body = "namespace SequentialBlockPostTransactionFinalizationExtractor.Refinement.SequentialBlockPostTransactionFinalization`ntheorem finalizationUnexpectedExport : True := True.intro`nend SequentialBlockPostTransactionFinalizationExtractor.Refinement.SequentialBlockPostTransactionFinalization`n" + (Complete-FinalizationExports $expected); Error = 'FINALIZATION_AXIOM_UNEXPECTED_EXPORT SequentialBlockPostTransactionFinalizationExtractor.Refinement.SequentialBlockPostTransactionFinalization.finalizationUnexpectedExport' },
            @{ Name = 'NestedExport'; Imports = $imports; Body = "namespace SequentialBlockPostTransactionFinalizationExtractor.Refinement.SequentialBlockPostTransactionFinalization.Nested.Audit`ntheorem injectedNestedExport : True := True.intro`nend SequentialBlockPostTransactionFinalizationExtractor.Refinement.SequentialBlockPostTransactionFinalization.Nested.Audit`n" + (Complete-FinalizationExports $expected); Error = 'FINALIZATION_AXIOM_UNEXPECTED_EXPORT SequentialBlockPostTransactionFinalizationExtractor.Refinement.SequentialBlockPostTransactionFinalization.Nested.Audit.injectedNestedExport' },
            @{ Name = 'NestedForbidden'; Imports = $imports; Body = "namespace SequentialBlockPostTransactionFinalizationExtractor.Refinement.SequentialBlockPostTransactionFinalization.Nested.Audit`naxiom injectedNestedAxiom : False`ntheorem injectedNestedExport : False := injectedNestedAxiom`nend SequentialBlockPostTransactionFinalizationExtractor.Refinement.SequentialBlockPostTransactionFinalization.Nested.Audit`n" + (Complete-FinalizationExports $expected); Error = 'FINALIZATION_AXIOM_FORBIDDEN SequentialBlockPostTransactionFinalizationExtractor.Refinement.SequentialBlockPostTransactionFinalization.Nested.Audit.injectedNestedExport: SequentialBlockPostTransactionFinalizationExtractor.Refinement.SequentialBlockPostTransactionFinalization.Nested.Audit.injectedNestedAxiom' },
            @{ Name = 'OmittedExport'; Imports = $imports; Body = (Complete-FinalizationExports $expected[1..($expected.Count - 1)]); Error = 'FINALIZATION_AXIOM_UNEXPECTED_EXPORT ' + $expected[0] }
        )) {
            $testPath = Join-Path $scratch ($test.Name + ".lean")
            [IO.File]::WriteAllText($testPath, $test.Imports + $checker + "`n" + $test.Body, [Text.UTF8Encoding]::new($false))
            $negative = Invoke-FinalizationAuditLean $testPath
            if ($negative.ExitCode -eq 0 -or -not $negative.Output.Contains($test.Error, [StringComparison]::Ordinal) -or
                $negative.Output -match 'unknown (identifier|constant|module)|unexpected token|maximum recursion depth|maximum heartbeats') {
                throw "Finalization axiom negative/completeness control failed: $($test.Name): $($negative.Output)"
            }
        }
        Write-Host "Verified all frozen finalization theorem exports, standard-only axioms, and negative/completeness controls."
    }
    finally { Pop-Location }
}
finally {
    $resolved = [IO.Path]::GetFullPath($scratch)
    if ($resolved.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
