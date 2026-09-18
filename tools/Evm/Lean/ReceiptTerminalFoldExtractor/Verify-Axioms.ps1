# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param(
    [string]$TheoremsPath = (Join-Path $PSScriptRoot "EXPORTED_THEOREMS.txt"),
    [string]$Lake = "lake"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
$expected = @([System.IO.File]::ReadAllLines($TheoremsPath))
if ($expected.Count -ne 234 -or @($expected | Sort-Object -Unique).Count -ne 234) {
    throw "The exported-theorem inventory must contain exactly 234 distinct names."
}
foreach ($name in $expected) {
    if ($name -notmatch '^ReceiptTerminalFoldExtractor\.(Specification|Refinement|Vectors)\.[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)+$') {
        throw "Invalid exported-theorem name: $name"
    }
}
$declared = @([System.IO.File]::ReadAllLines((Join-Path $PSScriptRoot 'DECLARED_THEOREMS.txt')))
if ($declared.Count -ne 12 -or @($declared | Sort-Object -Unique).Count -ne 12 -or
    @($declared | Where-Object { $expected -cnotcontains $_ }).Count -ne 0) {
    throw "The declared-theorem inventory must contain exactly 12 distinct exported names."
}
function Get-DeclarationText([string]$Source) {
    $text = [System.Text.StringBuilder]::new()
    $depth = 0
    $quoted = $false
    $lineComment = $false
    for ($index = 0; $index -lt $Source.Length; $index++) {
        $character = $Source[$index]
        $pair = if ($index + 1 -lt $Source.Length) { $Source.Substring($index, 2) } else { '' }
        if ($lineComment) {
            if ($character -eq "`n") { $lineComment = $false; $null = $text.Append($character) }
            continue
        }
        if ($depth -gt 0) {
            if ($pair -eq '/-') { $depth++; $index++ }
            elseif ($pair -eq '-/') { $depth--; $index++ }
            elseif ($character -eq "`n") { $null = $text.Append($character) }
            continue
        }
        if ($quoted) {
            if ($character -eq '\') { $index++ }
            elseif ($character -eq '"') { $quoted = $false }
            elseif ($character -eq "`n") { $null = $text.Append($character) }
            continue
        }
        if ($pair -eq '--') { $lineComment = $true; $index++; $null = $text.Append(' ') }
        elseif ($pair -eq '/-') { $depth = 1; $index++; $null = $text.Append(' ') }
        elseif ($character -eq '"') { $quoted = $true; $null = $text.Append(' ') }
        else { $null = $text.Append($character) }
    }
    return $text.ToString()
}

$sourceExports = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($sourceFile in Get-ChildItem (Join-Path $PSScriptRoot 'Specification'), (Join-Path $PSScriptRoot 'Refinement'), (Join-Path $PSScriptRoot 'Vectors') -Recurse -File -Filter *.lean) {
    $sourceText = Get-DeclarationText ([System.IO.File]::ReadAllText($sourceFile.FullName))
    $sourceNamespace = [regex]::Match($sourceText, '(?m)^namespace (ReceiptTerminalFoldExtractor\.(?:Specification|Refinement|Vectors)\.[A-Za-z0-9_]+)\s*$').Groups[1].Value
    foreach ($declaration in [regex]::Matches($sourceText, '(?m)^\s*(?:@\[[^\r\n]*\]\s*)*(?:(?:protected|public)\s+)?theorem\s+([A-Za-z0-9_]+)\b')) {
        if (-not $sourceExports.Add($sourceNamespace + '.' + $declaration.Groups[1].Value)) {
            throw "Duplicate named theorem in the source export inventory."
        }
    }
}
if (-not $sourceExports.SetEquals([string[]]$declared)) {
    throw "Named theorem declarations differ from the reviewed export inventory."
}

$scratchBase = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$scratch = [System.IO.Path]::GetFullPath((Join-Path $scratchBase ("receipt-terminal-fold-axioms-" + [Guid]::NewGuid().ToString("N"))))
$scratchPrefix = $scratchBase.TrimEnd([char[]]@('\', '/')) + [System.IO.Path]::DirectorySeparatorChar
if (-not $scratch.StartsWith($scratchPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Axiom-audit scratch escaped the temporary directory."
}

$checker = @'
open Lean Elab Command

elab "audit_export " target:ident : command => do
  let name := target.getId
  let env ← getEnv
  match env.find? name with
  | some (.thmInfo _) => pure ()
  | _ => throwError "AXIOM_AUDIT_MISSING {name}"
  let dependencies ← Lean.collectAxioms name
  let allowed := ["propext", "Classical.choice", "Quot.sound"]
  for dependency in dependencies do
    unless allowed.contains dependency.toString do
      throwError "AXIOM_AUDIT_FORBIDDEN {name}: {dependency}"
  let result := Json.mkObj [("theorem", toJson name.toString),
    ("axioms", toJson (dependencies.map Name.toString))]
  logInfo m!"AXIOM_AUDIT|{result.compress}"
'@

function Invoke-AuditLean([string]$Path) {
    $output = @(& $Lake env lean -DwarningAsError=true -DmaxHeartbeats=800000 $Path 2>&1 | ForEach-Object { $_.ToString() })
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join [Environment]::NewLine) }
}

function Read-AuditResults([string]$Output, [string[]]$ExpectedNames) {
    $records = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($line in ($Output -split '\r?\n')) {
        if ($line -match 'AXIOM_AUDIT\|(\{.*\})$') {
            $record = $Matches[1] | ConvertFrom-Json
            if ($record.PSObject.Properties.Name -notcontains 'theorem' -or
                $record.PSObject.Properties.Name -notcontains 'axioms' -or
                $record.theorem -isnot [string] -or $record.axioms -isnot [array] -or
                $records.ContainsKey($record.theorem) -or $ExpectedNames -cnotcontains $record.theorem) {
                throw "Malformed, duplicate, or unexpected axiom-audit result."
            }
            foreach ($dependency in $record.axioms) {
                if (@('propext', 'Classical.choice', 'Quot.sound') -cnotcontains $dependency) {
                    throw "Unexpected axiom dependency: $dependency"
                }
            }
            $records.Add($record.theorem, $record)
        }
    }
    if ($records.Count -ne $ExpectedNames.Count) { throw "Missing exported-theorem axiom-audit results." }
    foreach ($name in $ExpectedNames) {
        if (-not $records.ContainsKey($name)) { throw "Missing axiom-audit result: $name" }
    }
    return ,$records
}

New-Item -ItemType Directory -Path $scratch | Out-Null
Push-Location $PSScriptRoot
try {
    & $Lake --wfail build
    if ($LASTEXITCODE -ne 0) { throw "Lean build failed before the axiom audit." }
    $inventory = ($expected | ForEach-Object { '"' + $_ + '"' }) -join ","
    $exportCheck = @'
run_cmd do
  let expected : Array String := #[__EXPECTED__]
  let namespaces := ["ReceiptTerminalFoldExtractor.Specification", "ReceiptTerminalFoldExtractor.Refinement", "ReceiptTerminalFoldExtractor.Vectors"]
  let env ← getEnv
  let mut found := 0
  for (name, info) in env.constants do
    if namespaces.any (fun namespaceName => name.toString.startsWith (namespaceName ++ ".")) then
      if let .thmInfo _ := info then
        unless expected.contains name.toString do
          throwError "AXIOM_AUDIT_UNEXPECTED_EXPORT {name}"
        found := found + 1
  unless found == expected.size do
    throwError "AXIOM_AUDIT_EXPORT_COUNT {found} != {expected.size}"
'@
    $main = "import Lean`nimport ReceiptTerminalFoldExtractor.Vectors.ReceiptTerminalFoldVectors" + [Environment]::NewLine +
        $checker + [Environment]::NewLine + $exportCheck.Replace('__EXPECTED__', $inventory) + [Environment]::NewLine +
        (($expected | ForEach-Object { "audit_export $_" }) -join [Environment]::NewLine)
    $mainPath = Join-Path $scratch "ExportAxioms.lean"
    [System.IO.File]::WriteAllText($mainPath, $main)
    $result = Invoke-AuditLean $mainPath
    if ($result.ExitCode -ne 0) { throw "Exported-theorem axiom audit failed: $($result.Output)" }
    $records = Read-AuditResults $result.Output $expected
    foreach ($name in $expected) {
        Write-Host ($name + " : [" + ($records[$name].axioms -join ', ') + "]")
    }

    $missingOutputRejected = $false
    try { $null = Read-AuditResults "" $expected }
    catch { $missingOutputRejected = $_.Exception.Message -eq "Missing exported-theorem axiom-audit results." }
    if (-not $missingOutputRejected) { throw "The axiom audit accepted missing results." }

    foreach ($test in @(
        @{ Name = 'Missing'; Body = 'audit_export DoesNotExist'; Error = 'AXIOM_AUDIT_MISSING' },
        @{ Name = 'Unexpected'; Body = "namespace ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold`ntheorem injectedAuditExtra : True := True.intro`nend ReceiptTerminalFoldExtractor.Refinement.ReceiptTerminalFold`n" + $exportCheck.Replace('__EXPECTED__', $inventory); Error = 'AXIOM_AUDIT_UNEXPECTED_EXPORT' },
        @{ Name = 'Forbidden'; Body = "axiom injectedAuditDependency : False`ntheorem injectedAuditExport : False := injectedAuditDependency`naudit_export injectedAuditExport"; Error = 'AXIOM_AUDIT_FORBIDDEN' }
    )) {
        $testPath = Join-Path $scratch ($test.Name + ".lean")
        [System.IO.File]::WriteAllText($testPath, "import Lean" + [Environment]::NewLine + $checker + [Environment]::NewLine + $test.Body)
        $testResult = Invoke-AuditLean $testPath
        if ($testResult.ExitCode -eq 0 -or -not $testResult.Output.Contains($test.Error, [StringComparison]::Ordinal)) {
            throw "Axiom-audit negative control failed: $($test.Name): $($testResult.Output)"
        }
    }
    Write-Host "Verified all 234 public theorem constants (12 declared claims) and missing/extra/forbidden negative controls."
}
finally {
    Pop-Location
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
