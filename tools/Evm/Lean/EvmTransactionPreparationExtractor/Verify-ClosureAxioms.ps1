# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
$package = [IO.Path]::GetFullPath($PSScriptRoot)
$leanRoot = [IO.Path]::GetFullPath((Join-Path $package '..')).Replace('\', '/')
$inventoryHash = '8c64b43a0debfcd071a31b9b4d8772f021d60ff369f59a3dfebc31105eb79bc3'

function Read-ClosureInventory([byte[]]$Bytes) {
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
    if ($hash -cne $inventoryHash) { throw "Preparation closure inventory changed; review is required." }
    $rows = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($line in [Text.Encoding]::UTF8.GetString($Bytes).TrimEnd("`n").Split("`n")) {
        if ($line -cnotmatch '^([A-Za-z_][A-Za-z0-9_.]*)\|([0-9]+)\|([0-9]+)\|([0-9a-f]{64})$' -or $rows.ContainsKey($Matches[1])) {
            throw "Malformed or duplicate preparation closure inventory row."
        }
        $rows.Add($Matches[1], [pscustomobject]@{ Public = [int]$Matches[2]; Private = [int]$Matches[3]; Hash = $Matches[4] })
    }
    if ($rows.Count -ne 22) { throw "Preparation closure must contain exactly 22 repository modules." }
    return ,$rows
}
$inventoryBytes = [IO.File]::ReadAllBytes((Join-Path $package 'IMPORTED_THEOREMS.txt'))
$inventory = Read-ClosureInventory $inventoryBytes
$modules = @($inventory.Keys | Sort-Object -CaseSensitive)
$imports = "import EvmTransactionPreparationExtractor.Refinement.EvmTransactionPreparation`nimport Lean`n"
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
      unless sourcePresent do throwError "PREP_CLOSURE_SOURCE_MISSING {name}"
      unless expected.contains name.toString do throwError "PREP_CLOSURE_MODULE_UNEXPECTED {name}"
      modules := modules.push name
      logInfo m!"PREP_CLOSURE_MODULE|{name}"
  for expectedName in expected do
    unless modules.any (fun name => name.toString == expectedName) do
      throwError "PREP_CLOSURE_MODULE_MISSING {expectedName}"
  for (name, info) in env.constants do
    if let .thmInfo _ := info then
      let owner := (env.getModuleIdxFor? name).map (fun idx => env.header.moduleNames[idx.toNat]!)
      if owner.isNone || owner.any modules.contains then
        let axioms ← Lean.collectAxioms name
        for axiomName in axioms do
          unless ["propext", "Classical.choice", "Quot.sound"].contains axiomName.toString do
            throwError "PREP_CLOSURE_AXIOM_FORBIDDEN {name}: {axiomName}"
        match owner with
        | none => throwError "PREP_CLOSURE_LOCAL_UNEXPECTED {name}"
        | some moduleName =>
          let row := Json.mkObj [("module", toJson moduleName.toString),
            ("theorem", toJson name.toString), ("axioms", toJson (axioms.map Name.toString))]
          logInfo m!"PREP_CLOSURE_THEOREM|{row.compress}"
'@
$checker = $checker.Replace('__LEAN_ROOT__', $leanRoot)
function Get-ClosureChecker([string[]]$Modules) {
    return $checker.Replace('__MODULES__', (($Modules | ForEach-Object { '"' + $_ + '"' }) -join ','))
}
function Invoke-ClosureLean([string]$Source) {
    $output = @($Source | & lake env lean -DwarningAsError=true -DmaxHeartbeats=800000 --stdin 2>&1 | ForEach-Object { $_.ToString() })
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = [string]::Join("`n", $output) }
}
function Read-ClosureResults([string]$Output) {
    $seenModules = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $records = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($line in $Output -split '\r?\n') {
        if ($line.StartsWith('PREP_CLOSURE_MODULE|', [StringComparison]::Ordinal)) {
            $moduleName = $line.Substring(20)
            if (-not $inventory.ContainsKey($moduleName) -or -not $seenModules.Add($moduleName)) {
                throw "Malformed or duplicate preparation closure module."
            }
        }
        elseif ($line.StartsWith('PREP_CLOSURE_THEOREM|', [StringComparison]::Ordinal)) {
            $row = $line.Substring(21) | ConvertFrom-Json
            $propertyNames = @($row.PSObject.Properties | ForEach-Object { $_.Name })
            if ($propertyNames.Count -ne 3 -or
                @($propertyNames | Where-Object { $_ -cnotin @('module', 'theorem', 'axioms') }).Count -ne 0 -or
                $row.module -isnot [string] -or -not $inventory.ContainsKey($row.module) -or
                $row.theorem -isnot [string] -or $row.theorem.Length -eq 0 -or
                $row.axioms -isnot [array] -or $records.ContainsKey($row.theorem)) {
                throw "Malformed, duplicate, or unexpected preparation closure theorem."
            }
            foreach ($axiomName in $row.axioms) {
                if ($axiomName -isnot [string] -or $axiomName -cnotin @('propext', 'Classical.choice', 'Quot.sound')) {
                    throw "Forbidden preparation closure axiom result."
                }
            }
            $records.Add($row.theorem, $row)
        }
    }
    if (-not $seenModules.SetEquals([string[]]$modules)) { throw "Missing preparation closure modules." }
    foreach ($moduleName in $modules) {
        $names = [Collections.Generic.List[string]]::new()
        $privateCount = 0
        foreach ($row in $records.Values) {
            if ($row.module -ceq $moduleName) {
                $names.Add($row.theorem)
                if ($row.theorem.StartsWith('_private.', [StringComparison]::Ordinal)) { $privateCount++ }
            }
        }
        $names.Sort([StringComparer]::Ordinal)
        $bytes = [Text.Encoding]::UTF8.GetBytes([string]::Join("`n", $names) + "`n")
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        $expected = $inventory[$moduleName]
        if ($names.Count - $privateCount -ne $expected.Public -or $privateCount -ne $expected.Private -or $hash -cne $expected.Hash) {
            throw "Preparation theorem census mismatch: $moduleName"
        }
    }
    return ,$records
}
function Assert-ClosureNegative([string]$Source, [string]$Diagnostic) {
    $result = Invoke-ClosureLean $Source
    if ($result.ExitCode -eq 0 -or -not $result.Output.Contains($Diagnostic, [StringComparison]::Ordinal) -or
        $result.Output -match 'unknown (identifier|constant|module)|unexpected token|maximum recursion depth|maximum heartbeats|maximum number of steps|deterministic timeout') {
        throw "Preparation closure negative control failed: $Diagnostic`n$($result.Output)"
    }
}

Push-Location $package
try {
    lake --wfail build
    if ($LASTEXITCODE -ne 0) { throw "Preparation closure build failed." }
    $completeChecker = Get-ClosureChecker $modules
    $baseline = Invoke-ClosureLean ($imports + $completeChecker)
    if ($baseline.ExitCode -ne 0) { throw "Preparation closure audit failed: $($baseline.Output)" }
    $records = Read-ClosureResults $baseline.Output
    foreach ($moduleName in $modules) {
        $row = $inventory[$moduleName]
        Write-Host "$moduleName : $($row.Public) public + $($row.Private) private, SHA256 $($row.Hash)"
    }

    $inventoryRejected = $false
    try { $null = Read-ClosureInventory ([Text.Encoding]::UTF8.GetBytes('altered')) }
    catch { $inventoryRejected = $_.Exception.Message -eq "Preparation closure inventory changed; review is required." }
    if (-not $inventoryRejected) { throw "Preparation closure accepted an altered inventory." }
    $firstRecord = @($baseline.Output -split '\r?\n' | Where-Object { $_.StartsWith('PREP_CLOSURE_THEOREM|') })[0]
    $firstModule = @($baseline.Output -split '\r?\n' | Where-Object { $_.StartsWith('PREP_CLOSURE_MODULE|') })[0]
    $firstPrivate = @($baseline.Output -split '\r?\n' | Where-Object { $_.StartsWith('PREP_CLOSURE_THEOREM|') -and $_.Contains('"_private.') })[0]
    foreach ($test in @(
        @{ Output = ''; Error = 'Missing preparation closure modules.' },
        @{ Output = $baseline.Output.Replace($firstRecord, ''); Error = 'Preparation theorem census mismatch:' },
        @{ Output = $baseline.Output.Replace($firstPrivate, ''); Error = 'Preparation theorem census mismatch:' },
        @{ Output = $baseline.Output + "`n" + $firstRecord; Error = 'Malformed, duplicate, or unexpected preparation closure theorem.' },
        @{ Output = $baseline.Output + "`n" + $firstModule; Error = 'Malformed or duplicate preparation closure module.' },
        @{ Output = $baseline.Output + "`nPREP_CLOSURE_THEOREM|{}"; Error = 'Malformed, duplicate, or unexpected preparation closure theorem.' },
        @{ Output = $baseline.Output.Replace('"axioms":[]', '"axioms":["injected"]'); Error = 'Forbidden preparation closure axiom result.' }
    )) {
        $rejected = $false
        try { $null = Read-ClosureResults $test.Output }
        catch { $rejected = $_.Exception.Message.StartsWith($test.Error, [StringComparison]::Ordinal) }
        if (-not $rejected) { throw "Preparation closure parser control survived: $($test.Error)" }
    }
    Assert-ClosureNegative ($imports + (Get-ClosureChecker $modules[1..($modules.Count - 1)])) ('PREP_CLOSURE_MODULE_UNEXPECTED ' + $modules[0])
    Assert-ClosureNegative ($imports + (Get-ClosureChecker ($modules + 'Missing.Repository.Module'))) 'PREP_CLOSURE_MODULE_MISSING Missing.Repository.Module'
    foreach ($test in @(
        @{ Declaration = 'theorem injected : True := True.intro'; Error = 'PREP_CLOSURE_LOCAL_UNEXPECTED' },
        @{ Declaration = 'private theorem injected : True := True.intro'; Error = 'PREP_CLOSURE_LOCAL_UNEXPECTED' },
        @{ Declaration = 'namespace List.Nested`ntheorem injected : True := True.intro`nend List.Nested'; Error = 'PREP_CLOSURE_LOCAL_UNEXPECTED' },
        @{ Declaration = 'axiom injectedAxiom : False`ntheorem injected : False := injectedAxiom'; Error = 'PREP_CLOSURE_AXIOM_FORBIDDEN' },
        @{ Declaration = 'theorem injected : (1 + 1 : Nat) = 2 := by native_decide'; Error = 'PREP_CLOSURE_AXIOM_FORBIDDEN' }
    )) {
        $declaration = $test.Declaration.Replace('`n', "`n")
        Assert-ClosureNegative ($imports + $declaration + "`n" + $completeChecker) $test.Error
    }
    Write-Host "Verified 22 imported repository modules and all 3565 theorem declarations (3273 public, 292 private), standard-only axioms, and 15 negative controls."
}
finally { Pop-Location }
