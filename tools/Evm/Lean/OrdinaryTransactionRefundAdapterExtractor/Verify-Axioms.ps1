# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param([string]$Lake = "lake")
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
$package = [IO.Path]::GetFullPath($PSScriptRoot)
$inventoryName = "EXPORTED_THEOREMS.txt"
$inventoryBytes = [IO.File]::ReadAllBytes((Join-Path $package $inventoryName))
$inventoryCount = 1005
$inventoryHash = 'b6cf921d99722cc15ddcac3f54b32ae07c9035b18692d96091fa266763805396'
$namespaces = @(
    'OrdinaryTransactionRefundAdapterExtractor.Generated.OrdinaryTransactionRefund',
    'OrdinaryTransactionRefundAdapterExtractor.Specification.OrdinaryTransactionRefund',
    'OrdinaryTransactionRefundAdapterExtractor.Refinement.OrdinaryTransactionRefund',
    'OrdinaryTransactionRefundAdapterExtractor.Vectors.OrdinaryTransactionRefundVectors',
    'Eip803x'
)

function Read-RefundInventory([byte[]]$Bytes) {
    $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
    if ($digest -cne $inventoryHash) {
        throw "Refund exported-theorem inventory changed; explicit review is required."
    }
    $names = @([Text.Encoding]::UTF8.GetString($Bytes).TrimEnd("`n").Split("`n"))
    if ($names.Count -ne $inventoryCount -or [Collections.Generic.HashSet[string]]::new([string[]]$names, [StringComparer]::Ordinal).Count -ne $names.Count) {
        throw "Refund theorem inventory must contain the complete distinct descendant export roster."
    }
    foreach ($name in $names) {
        if ($name -notmatch '^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+$' -or
            -not @($namespaces | Where-Object { $name.StartsWith($_ + '.', [StringComparison]::Ordinal) }).Count) {
            throw "Invalid refund theorem name: $name"
        }
    }
    return ,$names
}
$expected = Read-RefundInventory $inventoryBytes

$checker = @'
open Lean Elab Command

def checkRefundAxioms (name : Name) : CommandElabM (Array Name) := do
  let dependencies ← Lean.collectAxioms name
  let allowed := ["propext", "Classical.choice", "Quot.sound"]
  for dependency in dependencies do
    unless allowed.contains dependency.toString do
      throwError "REFUND_AXIOM_FORBIDDEN {name}: {dependency}"
  return dependencies

elab "audit_refund_export " target:ident : command => do
  let name := target.getId
  let env ← getEnv
  match env.find? name with
  | some (.thmInfo _) => pure ()
  | _ => throwError "REFUND_AXIOM_MISSING {name}"
  let dependencies ← checkRefundAxioms name
  let result := Json.mkObj [("theorem", toJson name.toString),
    ("axioms", toJson (dependencies.map Name.toString))]
  logInfo m!"REFUND_AXIOM_RESULT|{result.compress}"
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
        let _ ← checkRefundAxioms name
        unless expected.contains name.toString do
          throwError "REFUND_AXIOM_UNEXPECTED_EXPORT {name}"
        found := found + 1
  unless found == expected.size do
    throwError "REFUND_AXIOM_EXPORT_COUNT {found} != {expected.size}"
'@
$completeness = $completeness.Replace('__NAMESPACES__', (($namespaces | ForEach-Object { '"' + $_ + '"' }) -join ','))
$imports = "import Lean`nimport OrdinaryTransactionRefundAdapterExtractor.Refinement.OrdinaryTransactionRefund`n"
$imports += "import OrdinaryTransactionRefundAdapterExtractor.Vectors.OrdinaryTransactionRefundVectors`n"
$imports += "import Eip803x.TransactionSettlementVectors`nimport Eip803x.StateGasTransitionVectors`nimport Eip803x.StateGasTransitionAdapterVectors`n"

function Invoke-RefundAuditLean([string]$Path) {
    $output = @(& $Lake env lean -DwarningAsError=true -DmaxHeartbeats=800000 $Path 2>&1 | ForEach-Object { $_.ToString() })
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join [Environment]::NewLine) }
}
function Read-RefundAuditResults([string]$Output, [string[]]$Names) {
    $records = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($line in ($Output -split '\r?\n')) {
        if ($line -match 'REFUND_AXIOM_RESULT\|(\{.*\})$') {
            $record = $Matches[1] | ConvertFrom-Json
            if (@($record.PSObject.Properties.Name).Count -ne 2 -or
                $record.PSObject.Properties.Name -notcontains 'theorem' -or $record.PSObject.Properties.Name -notcontains 'axioms' -or
                $record.theorem -isnot [string] -or $record.axioms -isnot [array] -or
                $records.ContainsKey($record.theorem) -or $Names -cnotcontains $record.theorem) {
                throw "Malformed, duplicate, or unexpected refund axiom result."
            }
            foreach ($dependency in $record.axioms) {
                if (@('propext', 'Classical.choice', 'Quot.sound') -cnotcontains $dependency) {
                    throw "Forbidden refund axiom result: $dependency"
                }
            }
            $records.Add($record.theorem, $record)
        }
    }
    if ($records.Count -ne $Names.Count) { throw "Missing refund axiom-audit results." }
    return ,$records
}
function Complete-RefundExports([string[]]$Names) {
    $quoted = ($Names | ForEach-Object { '"' + $_ + '"' }) -join ','
    return $completeness.Replace('__EXPECTED__', $quoted)
}

$scratch = [IO.Path]::GetFullPath((Join-Path $package (".refund-axioms-" + [Guid]::NewGuid().ToString("N"))))
if (-not $scratch.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refund axiom-audit scratch escaped the package."
}
try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    Push-Location $package
    try {
        $main = $imports + $checker + "`n" + (Complete-RefundExports $expected) + "`n" +
            (($expected | ForEach-Object { "audit_refund_export $_" }) -join "`n")
        $mainPath = Join-Path $scratch "ExportAxioms.lean"
        [IO.File]::WriteAllText($mainPath, $main, [Text.UTF8Encoding]::new($false))
        $result = Invoke-RefundAuditLean $mainPath
        if ($result.ExitCode -ne 0) { throw "Refund exported-theorem axiom baseline failed: $($result.Output)" }
        $records = Read-RefundAuditResults $result.Output $expected
        foreach ($name in $expected) { Write-Host ($name + " : [" + ($records[$name].axioms -join ', ') + "]") }

        foreach ($altered in @(
            ($expected[1..($expected.Count - 1)] -join "`n"),
            (($expected + $expected[0]) -join "`n"),
            (($expected + 'OrdinaryTransactionRefundAdapterExtractor.Refinement.OrdinaryTransactionRefund.unlisted') -join "`n")
        )) {
            $rejected = $false
            try { $null = Read-RefundInventory ([Text.Encoding]::UTF8.GetBytes($altered + "`n")) }
            catch { $rejected = $_.Exception.Message -eq "Refund exported-theorem inventory changed; explicit review is required." }
            if (-not $rejected) { throw "Refund inventory negative control survived." }
        }

        $first = 'REFUND_AXIOM_RESULT|{"theorem":"' + $expected[0] + '","axioms":[]}'
        foreach ($test in @(
            @{ Output = ''; Error = 'Missing refund axiom-audit results.' },
            @{ Output = $result.Output + "`n" + $first; Error = 'Malformed, duplicate, or unexpected refund axiom result.' },
            @{ Output = 'REFUND_AXIOM_RESULT|{"theorem":"unexpected","axioms":[]}'; Error = 'Malformed, duplicate, or unexpected refund axiom result.' },
            @{ Output = 'REFUND_AXIOM_RESULT|{"theorem":"' + $expected[0] + '"}'; Error = 'Malformed, duplicate, or unexpected refund axiom result.' },
            @{ Output = 'REFUND_AXIOM_RESULT|{"theorem":"' + $expected[0] + '","axioms":["injected"]}'; Error = 'Forbidden refund axiom result: injected' }
        )) {
            $rejected = $false
            try { $null = Read-RefundAuditResults $test.Output $expected }
            catch { $rejected = $_.Exception.Message -eq $test.Error }
            if (-not $rejected) { throw "Refund axiom result negative control survived: $($test.Error)" }
        }
        foreach ($namespaceName in $namespaces) {
            foreach ($forbidden in @($false, $true)) {
                $prefix = $namespaceName + '.Nested.Audit'
                $declarations = if ($forbidden) {
                    "axiom injectedAxiom : False`ntheorem injectedExport : False := injectedAxiom"
                } else { 'theorem injectedExport : True := True.intro' }
                $body = "namespace $prefix`n$declarations`nend $prefix`n" + (Complete-RefundExports $expected)
                $testPath = Join-Path $scratch 'LineageNegative.lean'
                [IO.File]::WriteAllText($testPath, $imports + $checker + "`n" + $body, [Text.UTF8Encoding]::new($false))
                $negative = Invoke-RefundAuditLean $testPath
                $marker = if ($forbidden) { "REFUND_AXIOM_FORBIDDEN $prefix.injectedExport: $prefix.injectedAxiom" }
                    else { "REFUND_AXIOM_UNEXPECTED_EXPORT $prefix.injectedExport" }
                if ($negative.ExitCode -eq 0 -or -not $negative.Output.Contains($marker, [StringComparison]::Ordinal) -or
                    $negative.Output -match 'unknown (identifier|constant|module)|unexpected token|maximum recursion depth|maximum heartbeats|maximum number of steps|deterministic timeout') {
                    throw "Refund lineage completeness control failed: $prefix forbidden=$forbidden`: $($negative.Output)"
                }
            }
        }

        foreach ($test in @(
            @{ Name = 'Missing'; Imports = "import Lean`n"; Body = 'audit_refund_export DoesNotExist'; Error = 'REFUND_AXIOM_MISSING DoesNotExist' },
            @{ Name = 'Forbidden'; Imports = "import Lean`n"; Body = "axiom refundInjectedAxiom : False`ntheorem refundInjectedExport : False := refundInjectedAxiom`naudit_refund_export refundInjectedExport"; Error = 'REFUND_AXIOM_FORBIDDEN refundInjectedExport: refundInjectedAxiom' },
            @{ Name = 'UnexpectedExport'; Imports = $imports; Body = "namespace OrdinaryTransactionRefundAdapterExtractor.Refinement.OrdinaryTransactionRefund`ntheorem refundUnexpectedExport : True := True.intro`nend OrdinaryTransactionRefundAdapterExtractor.Refinement.OrdinaryTransactionRefund`n" + (Complete-RefundExports $expected); Error = 'REFUND_AXIOM_UNEXPECTED_EXPORT OrdinaryTransactionRefundAdapterExtractor.Refinement.OrdinaryTransactionRefund.refundUnexpectedExport' },
            @{ Name = 'NestedExport'; Imports = $imports; Body = "namespace OrdinaryTransactionRefundAdapterExtractor.Refinement.OrdinaryTransactionRefund.Nested.Audit`ntheorem injectedNestedExport : True := True.intro`nend OrdinaryTransactionRefundAdapterExtractor.Refinement.OrdinaryTransactionRefund.Nested.Audit`n" + (Complete-RefundExports $expected); Error = 'REFUND_AXIOM_UNEXPECTED_EXPORT OrdinaryTransactionRefundAdapterExtractor.Refinement.OrdinaryTransactionRefund.Nested.Audit.injectedNestedExport' },
            @{ Name = 'NestedForbidden'; Imports = $imports; Body = "namespace OrdinaryTransactionRefundAdapterExtractor.Refinement.OrdinaryTransactionRefund.Nested.Audit`naxiom injectedNestedAxiom : False`ntheorem injectedNestedExport : False := injectedNestedAxiom`nend OrdinaryTransactionRefundAdapterExtractor.Refinement.OrdinaryTransactionRefund.Nested.Audit`n" + (Complete-RefundExports $expected); Error = 'REFUND_AXIOM_FORBIDDEN OrdinaryTransactionRefundAdapterExtractor.Refinement.OrdinaryTransactionRefund.Nested.Audit.injectedNestedExport: OrdinaryTransactionRefundAdapterExtractor.Refinement.OrdinaryTransactionRefund.Nested.Audit.injectedNestedAxiom' },
            @{ Name = 'OmittedExport'; Imports = $imports; Body = (Complete-RefundExports $expected[1..($expected.Count - 1)]); Error = 'REFUND_AXIOM_UNEXPECTED_EXPORT ' + $expected[0] }
        )) {
            $testPath = Join-Path $scratch ($test.Name + ".lean")
            [IO.File]::WriteAllText($testPath, $test.Imports + $checker + "`n" + $test.Body, [Text.UTF8Encoding]::new($false))
            $negative = Invoke-RefundAuditLean $testPath
            if ($negative.ExitCode -eq 0 -or -not $negative.Output.Contains($test.Error, [StringComparison]::Ordinal) -or
                $negative.Output -match 'unknown (identifier|constant|module)|unexpected token|maximum recursion depth|maximum heartbeats|maximum number of steps|deterministic timeout') {
                throw "Refund axiom negative/completeness control failed: $($test.Name): $($negative.Output)"
            }
        }
        Write-Host "Verified all frozen refund theorem exports, standard-only axioms, and negative/completeness controls."
    }
    finally { Pop-Location }
}
finally {
    $resolved = [IO.Path]::GetFullPath($scratch)
    if ($resolved.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
