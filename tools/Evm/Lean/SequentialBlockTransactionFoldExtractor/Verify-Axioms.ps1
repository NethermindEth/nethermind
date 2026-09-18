# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param([string]$Lake = "lake")
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
$package = [IO.Path]::GetFullPath($PSScriptRoot)
$inventoryBytes = [IO.File]::ReadAllBytes((Join-Path $package "EXPORTED_THEOREMS.txt"))

function Read-FoldInventory([byte[]]$Bytes) {
    $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
    if ($digest -cne '3f3de4d7350098b07bd112c51358dd249938f1b861c86e85bb7f02a0b71495d2') {
        throw "Fold exported-theorem inventory changed; explicit review is required."
    }
    $names = @([Text.Encoding]::UTF8.GetString($Bytes).TrimEnd("`n").Split("`n"))
    if ($names.Count -ne 49 -or @($names | Sort-Object -Unique -CaseSensitive).Count -ne 49) {
        throw "Fold theorem inventory must contain exactly 49 distinct exports."
    }
    foreach ($name in $names) {
        if ($name -notmatch '^SequentialBlockTransactionFoldExtractor\.(Specification|Refinement|Vectors)(\.[A-Za-z_][A-Za-z0-9_]*)+$') {
            throw "Invalid fold theorem name: $name"
        }
    }
    return ,$names
}
$expected = Read-FoldInventory $inventoryBytes

$checker = @'
open Lean Elab Command

def checkFoldAxioms (name : Name) : CommandElabM (Array Name) := do
  let dependencies ← Lean.collectAxioms name
  let allowed := ["propext", "Classical.choice", "Quot.sound"]
  for dependency in dependencies do
    unless allowed.contains dependency.toString do
      throwError "FOLD_AXIOM_FORBIDDEN {name}: {dependency}"
  return dependencies

elab "audit_fold_export " target:ident : command => do
  let name := target.getId
  let env ← getEnv
  match env.find? name with
  | some (.thmInfo _) => pure ()
  | _ => throwError "FOLD_AXIOM_MISSING {name}"
  let dependencies ← checkFoldAxioms name
  let result := Json.mkObj [("theorem", toJson name.toString),
    ("axioms", toJson (dependencies.map Name.toString))]
  logInfo m!"FOLD_AXIOM_RESULT|{result.compress}"
'@
$completeness = @'
run_cmd do
  let expected : Array String := #[__EXPECTED__]
  let namespaces := ["SequentialBlockTransactionFoldExtractor.Specification",
    "SequentialBlockTransactionFoldExtractor.Refinement", "SequentialBlockTransactionFoldExtractor.Vectors"]
  let env ← getEnv
  let mut found := 0
  for (name, info) in env.constants do
    if namespaces.any (fun namespaceName => name.toString.startsWith (namespaceName ++ ".")) then
      if let .thmInfo _ := info then
        let _ ← checkFoldAxioms name
        unless expected.contains name.toString do
          throwError "FOLD_AXIOM_UNEXPECTED_EXPORT {name}"
        found := found + 1
  unless found == expected.size do
    throwError "FOLD_AXIOM_EXPORT_COUNT {found} != {expected.size}"
'@
$imports = "import Lean`nimport SequentialBlockTransactionFoldExtractor.Refinement.SequentialBlockTransactionFold`nimport SequentialBlockTransactionFoldExtractor.Vectors.SequentialBlockTransactionFoldVectors`n"

function Invoke-FoldAuditLean([string]$Path) {
    $output = @(& $Lake env lean -DwarningAsError=true -DmaxHeartbeats=800000 $Path 2>&1 | ForEach-Object { $_.ToString() })
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join [Environment]::NewLine) }
}
function Read-FoldAuditResults([string]$Output, [string[]]$Names) {
    $records = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($line in ($Output -split '\r?\n')) {
        if ($line -match 'FOLD_AXIOM_RESULT\|(\{.*\})$') {
            $record = $Matches[1] | ConvertFrom-Json
            if (@($record.PSObject.Properties.Name).Count -ne 2 -or
                $record.PSObject.Properties.Name -notcontains 'theorem' -or $record.PSObject.Properties.Name -notcontains 'axioms' -or
                $record.theorem -isnot [string] -or $record.axioms -isnot [array] -or
                $records.ContainsKey($record.theorem) -or $Names -cnotcontains $record.theorem) {
                throw "Malformed, duplicate, or unexpected fold axiom result."
            }
            foreach ($dependency in $record.axioms) {
                if (@('propext', 'Classical.choice', 'Quot.sound') -cnotcontains $dependency) {
                    throw "Forbidden fold axiom result: $dependency"
                }
            }
            $records.Add($record.theorem, $record)
        }
    }
    if ($records.Count -ne $Names.Count) { throw "Missing fold axiom-audit results." }
    return ,$records
}
function Complete-FoldExports([string[]]$Names) {
    $quoted = ($Names | ForEach-Object { '"' + $_ + '"' }) -join ','
    return $completeness.Replace('__EXPECTED__', $quoted)
}

$scratch = [IO.Path]::GetFullPath((Join-Path $package (".fold-axioms-" + [Guid]::NewGuid().ToString("N"))))
if (-not $scratch.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Fold axiom-audit scratch escaped the package."
}
try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    Push-Location $package
    try {
        $main = $imports + $checker + "`n" + (Complete-FoldExports $expected) + "`n" +
            (($expected | ForEach-Object { "audit_fold_export $_" }) -join "`n")
        $mainPath = Join-Path $scratch "ExportAxioms.lean"
        [IO.File]::WriteAllText($mainPath, $main, [Text.UTF8Encoding]::new($false))
        $result = Invoke-FoldAuditLean $mainPath
        if ($result.ExitCode -ne 0) { throw "Fold exported-theorem axiom baseline failed: $($result.Output)" }
        $records = Read-FoldAuditResults $result.Output $expected
        foreach ($name in $expected) { Write-Host ($name + " : [" + ($records[$name].axioms -join ', ') + "]") }

        foreach ($altered in @(
            ($expected[1..($expected.Count - 1)] -join "`n"),
            (($expected + $expected[0]) -join "`n"),
            (($expected + 'SequentialBlockTransactionFoldExtractor.Refinement.SequentialBlockTransactionFold.unlisted') -join "`n")
        )) {
            $rejected = $false
            try { $null = Read-FoldInventory ([Text.Encoding]::UTF8.GetBytes($altered + "`n")) }
            catch { $rejected = $_.Exception.Message -eq "Fold exported-theorem inventory changed; explicit review is required." }
            if (-not $rejected) { throw "Fold inventory negative control survived." }
        }

        $first = 'FOLD_AXIOM_RESULT|{"theorem":"' + $expected[0] + '","axioms":[]}'
        foreach ($test in @(
            @{ Output = ''; Error = 'Missing fold axiom-audit results.' },
            @{ Output = $result.Output + "`n" + $first; Error = 'Malformed, duplicate, or unexpected fold axiom result.' },
            @{ Output = 'FOLD_AXIOM_RESULT|{"theorem":"unexpected","axioms":[]}'; Error = 'Malformed, duplicate, or unexpected fold axiom result.' },
            @{ Output = 'FOLD_AXIOM_RESULT|{"theorem":"' + $expected[0] + '"}'; Error = 'Malformed, duplicate, or unexpected fold axiom result.' },
            @{ Output = 'FOLD_AXIOM_RESULT|{"theorem":"' + $expected[0] + '","axioms":["injected"]}'; Error = 'Forbidden fold axiom result: injected' }
        )) {
            $rejected = $false
            try { $null = Read-FoldAuditResults $test.Output $expected }
            catch { $rejected = $_.Exception.Message -eq $test.Error }
            if (-not $rejected) { throw "Fold axiom result negative control survived: $($test.Error)" }
        }

        foreach ($test in @(
            @{ Name = 'Missing'; Imports = "import Lean`n"; Body = 'audit_fold_export DoesNotExist'; Error = 'FOLD_AXIOM_MISSING DoesNotExist' },
            @{ Name = 'Forbidden'; Imports = "import Lean`n"; Body = "axiom foldInjectedAxiom : False`ntheorem foldInjectedExport : False := foldInjectedAxiom`naudit_fold_export foldInjectedExport"; Error = 'FOLD_AXIOM_FORBIDDEN foldInjectedExport: foldInjectedAxiom' },
            @{ Name = 'UnexpectedExport'; Imports = $imports; Body = "namespace SequentialBlockTransactionFoldExtractor.Refinement.SequentialBlockTransactionFold`ntheorem foldUnexpectedExport : True := True.intro`nend SequentialBlockTransactionFoldExtractor.Refinement.SequentialBlockTransactionFold`n" + (Complete-FoldExports $expected); Error = 'FOLD_AXIOM_UNEXPECTED_EXPORT SequentialBlockTransactionFoldExtractor.Refinement.SequentialBlockTransactionFold.foldUnexpectedExport' },
            @{ Name = 'NestedExport'; Imports = $imports; Body = "namespace SequentialBlockTransactionFoldExtractor.Refinement.SequentialBlockTransactionFold.Nested.Audit`ntheorem injectedNestedExport : True := True.intro`nend SequentialBlockTransactionFoldExtractor.Refinement.SequentialBlockTransactionFold.Nested.Audit`n" + (Complete-FoldExports $expected); Error = 'FOLD_AXIOM_UNEXPECTED_EXPORT SequentialBlockTransactionFoldExtractor.Refinement.SequentialBlockTransactionFold.Nested.Audit.injectedNestedExport' },
            @{ Name = 'NestedForbidden'; Imports = $imports; Body = "namespace SequentialBlockTransactionFoldExtractor.Refinement.SequentialBlockTransactionFold.Nested.Audit`naxiom injectedNestedAxiom : False`ntheorem injectedNestedExport : False := injectedNestedAxiom`nend SequentialBlockTransactionFoldExtractor.Refinement.SequentialBlockTransactionFold.Nested.Audit`n" + (Complete-FoldExports $expected); Error = 'FOLD_AXIOM_FORBIDDEN SequentialBlockTransactionFoldExtractor.Refinement.SequentialBlockTransactionFold.Nested.Audit.injectedNestedExport: SequentialBlockTransactionFoldExtractor.Refinement.SequentialBlockTransactionFold.Nested.Audit.injectedNestedAxiom' },
            @{ Name = 'OmittedExport'; Imports = $imports; Body = (Complete-FoldExports $expected[1..($expected.Count - 1)]); Error = 'FOLD_AXIOM_UNEXPECTED_EXPORT ' + $expected[0] }
        )) {
            $testPath = Join-Path $scratch ($test.Name + ".lean")
            [IO.File]::WriteAllText($testPath, $test.Imports + $checker + "`n" + $test.Body, [Text.UTF8Encoding]::new($false))
            $negative = Invoke-FoldAuditLean $testPath
            if ($negative.ExitCode -eq 0 -or -not $negative.Output.Contains($test.Error, [StringComparison]::Ordinal) -or
                $negative.Output -match 'unknown (identifier|constant|module)|unexpected token|maximum recursion depth|maximum heartbeats') {
                throw "Fold axiom negative/completeness control failed: $($test.Name): $($negative.Output)"
            }
        }
        Write-Host "Verified all 49 frozen fold theorem exports, standard-only axioms, and negative/completeness controls."
    }
    finally { Pop-Location }
}
finally {
    $resolved = [IO.Path]::GetFullPath($scratch)
    if ($resolved.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolved)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
