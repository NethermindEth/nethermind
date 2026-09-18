# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param([switch]$Discover)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$leanRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).Replace('\', '/')
$inventoryHash = '103c5bb4aa37510df26581e0ca0fd3b329a7ca496ea467f464c30f08419767bd'
$modules = @(
    'AuthorizationStateGasFoldExtractor.Generated.AuthorizationStateGasFold',
    'AuthorizationStateGasFoldExtractor.Refinement.AuthorizationStateGasFold',
    'Eip803x.BlockReceiptGas',
    'Eip803x.Gas',
    'Eip803x.Generated.BlockReceiptGasAccountingKernel',
    'Eip803x.Generated.StateGasChargeKernel',
    'Eip803x.Generated.StateGasTransitionAdapterKernel',
    'Eip803x.Generated.StateGasTransitionKernel',
    'Eip803x.Generated.TransactionGasInitializationKernel',
    'Eip803x.Generated.TransactionSettlementKernel',
    'Eip803x.Production',
    'Eip803x.Refinement.BlockReceiptGasAccounting',
    'Eip803x.Refinement.StateGasCharge',
    'Eip803x.Refinement.StateGasTransition',
    'Eip803x.Refinement.StateGasTransitionAdapter',
    'Eip803x.Refinement.StateGasTransitionAdapterKernel',
    'Eip803x.Refinement.TransactionGasInitialization',
    'Eip803x.Refinement.TransactionSettlement',
    'Eip803x.Schedule',
    'Eip803x.TransactionGas',
    'Eip803x.TransactionSettlement',
    'EvmTransactionPreparationExtractor.Generated.EvmTransactionPreparation',
    'EvmTransactionPreparationExtractor.Reference.EvmTransactionPreparationReference',
    'EvmTransactionPreparationExtractor.Refinement.Admission',
    'EvmTransactionPreparationExtractor.Refinement.EvmTransactionPreparation',
    'EvmTransactionPreparationExtractor.Refinement.Maps',
    'OrdinaryEvmCompletionExtractor.Generated.OrdinaryEvmCompletion',
    'OrdinaryEvmCompletionExtractor.Refinement.OrdinaryEvmCompletion',
    'OrdinaryEvmCompletionExtractor.Refinement.SourceAttachedOrdinaryEvmCompletion',
    'OrdinaryEvmCompletionExtractor.Specification.OrdinaryEvmCompletion',
    'OrdinaryEvmCompletionExtractor.Vectors.OrdinaryEvmCompletionVectors',
    'OrdinaryPostNonceDispatchExtractor.Generated.OrdinaryPostNonceDispatch',
    'OrdinaryPostNonceDispatchExtractor.Reference.OrdinaryPostNonceDispatchReference',
    'OrdinaryPostNonceDispatchExtractor.Refinement.OrdinaryPostNonceDispatch',
    'OrdinaryStatefulAdmissionPrefixExtractor.Generated.OrdinaryStatefulAdmissionPrefix',
    'OrdinaryTransactionRefundAdapterExtractor.Generated.OrdinaryTransactionRefund',
    'OrdinaryTransactionRefundAdapterExtractor.Refinement.OrdinaryTransactionRefund',
    'OrdinaryTransactionRefundAdapterExtractor.Specification.OrdinaryTransactionRefund',
    'ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel',
    'ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold',
    'ReceiptTerminalFoldExtractor.Specification.ReceiptTerminalFold',
    'TransactionProcessorExtractor.Generated.TransactionProcessorLifecycle'
)
$imports = @'
import OrdinaryEvmCompletionExtractor.Refinement.SourceAttachedOrdinaryEvmCompletion
import OrdinaryEvmCompletionExtractor.Vectors.OrdinaryEvmCompletionVectors
import Lean
'@
$checker = @'
open Lean Elab Command
run_cmd do
  let expected : Array String := #[__MODULES__]
  let env ← getEnv
  let mut modules : Array Name := #[]
  for name in env.header.moduleNames do
    let path := System.FilePath.mk ("__LEAN_ROOT__/" ++ name.toString.replace "." "/" ++ ".lean")
    let sourcePresent ← path.pathExists
    let standard := ["Init", "Lean", "Std", "Lake"].contains (name.components.head!).toString
    if sourcePresent || expected.contains name.toString || !standard then
      unless sourcePresent do throwError "COMPLETION_SOURCE_MISSING {name}"
      unless expected.contains name.toString do throwError "COMPLETION_MODULE_UNEXPECTED {name}"
      modules := modules.push name
      logInfo m!"COMPLETION_MODULE|{name}"
  for expectedName in expected do
    unless modules.any (fun name => name.toString == expectedName) do
      throwError "COMPLETION_MODULE_MISSING {expectedName}"
  for (name, info) in env.constants do
    let owner := (env.getModuleIdxFor? name).map (fun idx => env.header.moduleNames[idx.toNat]!)
    if owner.isNone || owner.any modules.contains then
      let kind := match info with
        | .axiomInfo _ => "axiom"
        | .defnInfo _ => "definition"
        | .thmInfo _ => "theorem"
        | .opaqueInfo _ => "opaque"
        | .quotInfo _ => "quotient"
        | .inductInfo _ => "inductive"
        | .ctorInfo _ => "constructor"
        | .recInfo _ => "recursor"
      let axioms ← Lean.collectAxioms name
      for axiomName in axioms do
        unless ["propext", "Classical.choice", "Quot.sound"].contains axiomName.toString do
          throwError "COMPLETION_AXIOM_FORBIDDEN {name}: {axiomName}"
      if kind == "opaque" then throwError "COMPLETION_OPAQUE_FORBIDDEN {name}"
      match owner with
      | none => throwError "COMPLETION_LOCAL_UNEXPECTED {name}"
      | some moduleName =>
        let row := Json.mkObj [("module", toJson moduleName.toString),
          ("declaration", toJson name.toString), ("kind", toJson kind),
          ("axioms", toJson (axioms.map Name.toString))]
        logInfo m!"COMPLETION_DECLARATION|{row.compress}"
'@
$checker = $checker.Replace('__LEAN_ROOT__', $leanRoot)
function Checker([string[]]$Names) {
    return $checker.Replace('__MODULES__', (($Names | ForEach-Object { '"' + $_ + '"' }) -join ','))
}
function Invoke-Lean([string]$Source) {
    $output = @($Source | & lake env lean -DwarningAsError=true -DmaxHeartbeats=800000 --stdin 2>&1 | ForEach-Object { $_.ToString() })
    return [pscustomobject]@{ Exit = $LASTEXITCODE; Output = $output -join "`n" }
}
function Read-Records([string]$Output) {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $records = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($line in $Output -split '\r?\n') {
        if ($line.StartsWith('COMPLETION_MODULE|', [StringComparison]::Ordinal)) {
            $name = $line.Substring('COMPLETION_MODULE|'.Length)
            if ($name -cnotin $modules -or -not $seen.Add($name)) { throw 'Unexpected or duplicate census module.' }
        } elseif ($line.StartsWith('COMPLETION_DECLARATION|', [StringComparison]::Ordinal)) {
            $row = $line.Substring('COMPLETION_DECLARATION|'.Length) | ConvertFrom-Json
            $properties = @($row.PSObject.Properties | ForEach-Object { $_.Name })
            if ($properties.Count -ne 4 -or @($properties | Where-Object { $_ -cnotin @('module', 'declaration', 'kind', 'axioms') }).Count -ne 0 -or
                $row.module -isnot [string] -or $row.module -cnotin $modules -or
                $row.declaration -isnot [string] -or $row.declaration.Length -eq 0 -or
                $row.kind -cnotin @('definition', 'theorem', 'inductive', 'constructor', 'recursor', 'quotient') -or
                $row.axioms -isnot [array] -or $records.ContainsKey($row.declaration)) { throw 'Malformed or duplicate census declaration.' }
            foreach ($axiomName in $row.axioms) {
                if ($axiomName -isnot [string] -or $axiomName -cnotin @('propext', 'Classical.choice', 'Quot.sound')) { throw 'Forbidden census axiom.' }
            }
            $records.Add($row.declaration, $row)
        }
    }
    if (-not $seen.SetEquals([string[]]$modules)) { throw 'Missing census module.' }
    return ,$records
}
function Inventory([object]$Records) {
    $lines = [Collections.Generic.List[string]]::new()
    foreach ($module in $modules) {
        $names = [Collections.Generic.List[string]]::new()
        $private = 0
        foreach ($row in $Records.Values) {
            if ($row.module -ceq $module) {
                $names.Add($row.declaration + '|' + $row.kind)
                if ($row.declaration.StartsWith('_private.', [StringComparison]::Ordinal)) { $private++ }
            }
        }
        $names.Sort([StringComparer]::Ordinal)
        $bytes = [Text.Encoding]::UTF8.GetBytes([string]::Join("`n", $names) + "`n")
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        $lines.Add("$module|$($names.Count - $private)|$private|$hash")
    }
    return [string]::Join("`n", $lines) + "`n"
}
function Assert-Inventory([string]$Output, [string]$Expected) {
    $records = Read-Records $Output
    if ((Inventory $records) -cne $Expected) { throw 'Declaration census differs from the frozen inventory.' }
}
function Negative([string]$Source, [string]$Diagnostic) {
    $result = Invoke-Lean $Source
    if ($result.Exit -eq 0 -or -not $result.Output.Contains($Diagnostic, [StringComparison]::Ordinal) -or
        $result.Output -match 'unknown (identifier|constant|module)|unexpected token|failed to synthesize|maximum recursion depth|maximum heartbeats|maximum number of steps|deterministic timeout') {
        throw "Census negative control failed: $Diagnostic`n$($result.Output)"
    }
}
Push-Location $PSScriptRoot
try {
    $complete = Checker $modules
    $baseline = Invoke-Lean ($imports + "`n" + $complete)
    if ($baseline.Exit -ne 0) { throw "Complete module-origin census failed:`n$($baseline.Output)" }
    $records = Read-Records $baseline.Output
    $actual = Inventory $records
    if ($Discover) {
        Write-Output $baseline.Output
        Write-Output 'CANDIDATE_INVENTORY_BEGIN'
        Write-Output $actual
        Write-Output 'CANDIDATE_INVENTORY_END'
        Write-Output "Unfrozen discovery only: $($modules.Count) modules, $($records.Count) declarations."
        return
    }
    $bytes = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'IMPORTED_DECLARATIONS.txt'))
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    if ($hash -cne $inventoryHash) { throw 'Frozen declaration inventory changed; review is required.' }
    $expected = [Text.Encoding]::UTF8.GetString($bytes).Replace("`r`n", "`n")
    Assert-Inventory $baseline.Output $expected
    $firstRow = @($baseline.Output -split '\r?\n' | Where-Object { $_.StartsWith('COMPLETION_DECLARATION|') })[0]
    $privateRow = @($baseline.Output -split '\r?\n' | Where-Object { $_.StartsWith('COMPLETION_DECLARATION|') -and $_.Contains('"_private.') })[0]
    $equationRow = @($baseline.Output -split '\r?\n' | Where-Object { $_.StartsWith('COMPLETION_DECLARATION|') -and $_.Contains('.eq_1"') })[0]
    $firstModule = @($baseline.Output -split '\r?\n' | Where-Object { $_.StartsWith('COMPLETION_MODULE|') })[0]
    foreach ($test in @(
        @{ Output = ''; Error = 'Missing census module.' },
        @{ Output = $baseline.Output.Replace($firstRow, ''); Error = 'Declaration census differs' },
        @{ Output = $baseline.Output.Replace($privateRow, ''); Error = 'Declaration census differs' },
        @{ Output = $baseline.Output.Replace($equationRow, ''); Error = 'Declaration census differs' },
        @{ Output = $baseline.Output + "`n" + $firstRow; Error = 'Malformed or duplicate census declaration.' },
        @{ Output = $baseline.Output + "`n" + $firstModule; Error = 'Unexpected or duplicate census module.' },
        @{ Output = $baseline.Output + "`nCOMPLETION_DECLARATION|{}"; Error = 'Malformed or duplicate census declaration.' },
        @{ Output = $baseline.Output.Replace('"axioms":[]', '"axioms":["injected"]'); Error = 'Forbidden census axiom.' },
        @{ Output = $baseline.Output + "`nCOMPLETION_DECLARATION|" + (@{ module = $modules[0]; declaration = 'Unexpected.Descendant'; kind = 'theorem'; axioms = @() } | ConvertTo-Json -Compress); Error = 'Declaration census differs' }
    )) {
        $rejected = $false
        try { Assert-Inventory $test.Output $expected }
        catch { $rejected = $_.Exception.Message.StartsWith($test.Error, [StringComparison]::Ordinal) }
        if (-not $rejected) { throw "Census parser negative survived: $($test.Error)" }
    }
    Negative ($imports + "`n" + (Checker $modules[1..($modules.Count - 1)])) ('COMPLETION_MODULE_UNEXPECTED ' + $modules[0])
    Negative ($imports + "`n" + (Checker ($modules + 'Missing.Repository.Module'))) 'COMPLETION_MODULE_MISSING Missing.Repository.Module'
    foreach ($test in @(
        @{ Declaration = 'theorem injected : True := True.intro'; Error = 'COMPLETION_LOCAL_UNEXPECTED' },
        @{ Declaration = 'private theorem injected : True := True.intro'; Error = 'COMPLETION_LOCAL_UNEXPECTED' },
        @{ Declaration = 'namespace List.Nested`n  theorem injected : True := True.intro`nend List.Nested'; Error = 'COMPLETION_LOCAL_UNEXPECTED' },
        @{ Declaration = 'axiom injectedAxiom : False`ntheorem injected : False := injectedAxiom'; Error = 'COMPLETION_AXIOM_FORBIDDEN' },
        @{ Declaration = 'theorem injected : (1 + 1 : Nat) = 2 := by native_decide'; Error = 'COMPLETION_AXIOM_FORBIDDEN' },
        @{ Declaration = 'def injectedEquation : Bool → Nat | true => 1 | false => 0`n#check injectedEquation.eq_1'; Error = 'COMPLETION_LOCAL_UNEXPECTED' }
    )) {
        Negative ($imports + "`n" + $test.Declaration.Replace('`n', "`n") + "`n" + $complete) $test.Error
    }
    Write-Output $baseline.Output
    Write-Output "Frozen candidate census passed: $($modules.Count) modules, $($records.Count) declarations; standard-only axioms and 17 negative controls."
} finally { Pop-Location }
