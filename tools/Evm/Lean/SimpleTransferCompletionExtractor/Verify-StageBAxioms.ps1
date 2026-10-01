# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

param([string]$Lake = "lake")

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
$package = [IO.Path]::GetFullPath($PSScriptRoot)
$roots = @(
    "SimpleTransferCompletionExtractor.StageB.Dispatch.Refinement.generated_resolution_refines",
    "SimpleTransferCompletionExtractor.StageB.Dispatch.Refinement.boundary_resolves_standard_base",
    "SimpleTransferCompletionExtractor.StageB.RefundBridge.standard_mainnet_boundary_refines",
    "SimpleTransferCompletionExtractor.StageB.Runtime.generated_execute_simpleTransfer_block28_false_to_standard_mainnet_refund_boundary_refines",
    "SimpleTransferCompletionExtractor.StageB.Runtime.generated_execute_simpleTransfer_block28_false_to_refund_preCall_exact",
    "SimpleTransferCompletionExtractor.StageB.Runtime.generated_execute_simpleTransfer_refund_continuation_retained_exact",
    "SimpleTransferCompletionExtractor.StageB.Runtime.generated_execute_simpleTransfer_refund_result_install_exact",
    "SimpleTransferCompletionExtractor.StageB.Runtime.generated_execute_simpleTransfer_refund_postInstall_source_admitted",
    "SimpleTransferCompletionExtractor.StageB.Runtime.generated_execute_simpleTransfer_refund_installed_still_suspends",
    "SimpleTransferCompletionExtractor.StageB.Runtime.generated_execute_simpleTransfer_refund_rearmed_to_block33_exact",
    "SimpleTransferCompletionExtractor.StageB.Runtime.generated_execute_simpleTransfer_refund_resultInstalled_rearmed_to_block33_exact",
    "SimpleTransferCompletionExtractor.StageB.Runtime.generated_execute_simpleTransfer_refund_postInstall_status_source_admitted",
    "SimpleTransferCompletionExtractor.StageB.Runtime.generated_execute_simpleTransfer_postRefund_guard_exact",
    "SimpleTransferCompletionExtractor.StageB.Runtime.generated_execute_simpleTransfer_postRefund_status_selection_exact",
    "SimpleTransferCompletionExtractor.StageB.Runtime.generated_execute_simpleTransfer_postRefund_block33_to_status_exact",
    "SimpleTransferCompletionExtractor.StageB.Runtime.generated_execute_simpleTransfer_refund_rearmed_to_status_exact",
    "SimpleTransferCompletionExtractor.StageB.Runtime.generated_execute_simpleTransfer_refund_resultInstalled_rearmed_to_status_exact",
    "SimpleTransferCompletionExtractor.StageB.Runtime.conversionValue_byte_to_int_exact",
    "SimpleTransferCompletionExtractor.StageB.Runtime.defaultValue_int_exact",
    "SimpleTransferCompletionExtractor.StageB.Runtime.generated_execute_simpleTransfer_refund_statusCode_source_admitted",
    "SimpleTransferCompletionExtractor.StageB.Runtime.generated_execute_simpleTransfer_postRefund_statusCode_exact",
    "SimpleTransferCompletionExtractor.StageB.Runtime.generated_execute_simpleTransfer_refund_rearmed_to_statusCode_exact",
    "SimpleTransferCompletionExtractor.StageB.Runtime.generated_execute_simpleTransfer_refund_resultInstalled_rearmed_to_statusCode_exact",
    "SimpleTransferCompletionExtractor.StageB.Runtime.generated_execute_simpleTransfer_refund_accessGuard_source_admitted",
    "SimpleTransferCompletionExtractor.StageB.Runtime.generated_execute_simpleTransfer_postRefund_accessGuard_exact",
    "SimpleTransferCompletionExtractor.StageB.Runtime.generated_execute_simpleTransfer_postRefund_call_frontiers_source_admitted",
    "SimpleTransferCompletionExtractor.StageB.Runtime.generated_execute_simpleTransfer_postRefund_callFrontier_exact",
    "SimpleTransferCompletionExtractor.StageB.Runtime.generated_execute_simpleTransfer_postRefund_accessGuard_to_callFrontier_exact"
)
$allowed = @("propext", "Classical.choice", "Quot.sound")

if ([Collections.Generic.HashSet[string]]::new([string[]]$roots, [StringComparer]::Ordinal).Count -ne $roots.Count)
{
    throw "Stage-B axiom root roster contains duplicates."
}

$checker = @'
open Lean Elab Command

def checkStageBAxioms (name : Name) : CommandElabM (Array Name) := do
  let dependencies ← Lean.collectAxioms name
  let allowed := ["propext", "Classical.choice", "Quot.sound"]
  for dependency in dependencies do
    unless allowed.contains dependency.toString do
      throwError "STAGE_B_AXIOM_FORBIDDEN {name}: {dependency}"
  return dependencies

elab "audit_stage_b_axiom " target:ident : command => do
  let name := target.getId
  let env ← getEnv
  match env.find? name with
  | some (.thmInfo _) => pure ()
  | _ => throwError "STAGE_B_AXIOM_MISSING {name}"
  let dependencies ← checkStageBAxioms name
  let result := Json.mkObj [
    ("theorem", toJson name.toString),
    ("axioms", toJson (dependencies.map Name.toString))]
  logInfo m!"STAGE_B_AXIOM_RESULT|{result.compress}"
'@

function Invoke-StageBAxiomLean([string]$Path)
{
    $output = @(& $Lake env lean -DwarningAsError=true $Path 2>&1 | ForEach-Object { $_.ToString() })
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join [Environment]::NewLine) }
}

function Read-StageBAxiomResults([string]$Output, [string[]]$Names)
{
    $records = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $marker = "STAGE_B_AXIOM_RESULT|"
    foreach ($line in ($Output -split '\r?\n'))
    {
        if (-not $line.Contains($marker, [StringComparison]::Ordinal)) { continue }
        if (-not $line.StartsWith($marker, [StringComparison]::Ordinal))
        {
            throw "Malformed, duplicate, or unexpected Stage-B axiom result."
        }

        [string]$json = $line.Substring($marker.Length)
        [System.Text.Json.JsonDocument]$document = $null
        try
        {
            $document = [System.Text.Json.JsonDocument]::Parse($json)
            if ($document.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object)
            {
                throw "Malformed, duplicate, or unexpected Stage-B axiom result."
            }

            $properties = @($document.RootElement.EnumerateObject())
            $theoremProperties = @($properties | Where-Object { $_.Name -ceq "theorem" })
            $axiomProperties = @($properties | Where-Object { $_.Name -ceq "axioms" })
            if ($properties.Count -ne 2 -or
                $theoremProperties.Count -ne 1 -or
                $axiomProperties.Count -ne 1 -or
                $theoremProperties[0].Value.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
                $axiomProperties[0].Value.ValueKind -ne [System.Text.Json.JsonValueKind]::Array)
            {
                throw "Malformed, duplicate, or unexpected Stage-B axiom result."
            }

            [string]$theorem = $theoremProperties[0].Value.GetString()
            [Collections.Generic.List[string]]$dependencies = @()
            foreach ($dependency in $axiomProperties[0].Value.EnumerateArray())
            {
                if ($dependency.ValueKind -ne [System.Text.Json.JsonValueKind]::String)
                {
                    throw "Malformed, duplicate, or unexpected Stage-B axiom result."
                }
                $dependencies.Add($dependency.GetString())
            }

            if ([Collections.Generic.HashSet[string]]::new($dependencies, [StringComparer]::Ordinal).Count -ne $dependencies.Count -or
                $records.ContainsKey($theorem) -or
                $Names -cnotcontains $theorem)
            {
                throw "Malformed, duplicate, or unexpected Stage-B axiom result."
            }

            foreach ($dependency in $dependencies)
            {
                if ($allowed -cnotcontains $dependency) { throw "Forbidden Stage-B axiom result: $dependency" }
            }
            $records.Add($theorem, [pscustomobject]@{ theorem = $theorem; axioms = $dependencies.ToArray() })
        }
        catch [System.Text.Json.JsonException]
        {
            throw "Malformed, duplicate, or unexpected Stage-B axiom result."
        }
        finally
        {
            if ($null -ne $document) { $document.Dispose() }
        }
    }

    if ($records.Count -ne $Names.Count) { throw "Missing Stage-B axiom-audit results." }
    return ,$records
}

$scratch = [IO.Path]::GetFullPath((Join-Path $package (".stage-b-axioms-" + [Guid]::NewGuid().ToString("N"))))
if (-not $scratch.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))
{
    throw "Stage-B axiom-audit scratch escaped the package."
}

try
{
    New-Item -ItemType Directory -Path $scratch | Out-Null
    Push-Location $package
    try
    {
        $imports = "import Lean`nimport SimpleTransferCompletionExtractor.StageB.Semantics`n"
        $main = $imports + $checker + "`n" + (($roots | ForEach-Object { "audit_stage_b_axiom $_" }) -join "`n")
        $mainPath = Join-Path $scratch "StageBAxioms.lean"
        [IO.File]::WriteAllText($mainPath, $main, [Text.UTF8Encoding]::new($false))
        $result = Invoke-StageBAxiomLean $mainPath
        if ($result.ExitCode -ne 0) { throw "Stage-B axiom baseline failed: $($result.Output)" }
        $records = Read-StageBAxiomResults $result.Output $roots
        foreach ($name in $roots)
        {
            Write-Host ($name + " : [" + ($records[$name].axioms -join ", ") + "]")
        }

        foreach ($test in @(
            @{ Name = "Missing"; Body = "audit_stage_b_axiom DoesNotExist"; Marker = "STAGE_B_AXIOM_MISSING DoesNotExist" },
            @{ Name = "Forbidden"; Body = "axiom stageBInjectedAxiom : False`ntheorem stageBInjectedExport : False := stageBInjectedAxiom`naudit_stage_b_axiom stageBInjectedExport"; Marker = "STAGE_B_AXIOM_FORBIDDEN stageBInjectedExport: stageBInjectedAxiom" }
        ))
        {
            $testPath = Join-Path $scratch ($test.Name + ".lean")
            [IO.File]::WriteAllText($testPath, $imports + $checker + "`n" + $test.Body,
                [Text.UTF8Encoding]::new($false))
            $negative = Invoke-StageBAxiomLean $testPath
            if ($negative.ExitCode -eq 0 -or
                -not $negative.Output.Contains($test.Marker, [StringComparison]::Ordinal) -or
                $negative.Output -match 'unknown (identifier|constant|module)|unexpected token|maximum recursion depth|maximum heartbeats|maximum number of steps|deterministic timeout')
            {
                throw "Stage-B axiom negative control failed: $($test.Name): $($negative.Output)"
            }
        }

        $validParserOutput = ($roots | ForEach-Object {
            'STAGE_B_AXIOM_RESULT|{"axioms":[],"theorem":"' + $_ + '"}'
        }) -join [Environment]::NewLine
        foreach ($test in @(
            @{ Output = ""; Error = "Missing Stage-B axiom-audit results." },
            @{ Output = 'STAGE_B_AXIOM_RESULT|{"theorem":"unexpected","axioms":[]}'; Error = "Malformed, duplicate, or unexpected Stage-B axiom result." },
            @{ Output = 'STAGE_B_AXIOM_RESULT|{"theorem":"' + $roots[0] + '","axioms":["injected"]}'; Error = "Forbidden Stage-B axiom result: injected" },
            @{ Output = $validParserOutput + [Environment]::NewLine + 'STAGE_B_AXIOM_RESULT|not-json'; Error = "Malformed, duplicate, or unexpected Stage-B axiom result." },
            @{ Output = 'STAGE_B_AXIOM_RESULT|{"theorem":"ignored","theorem":"' + $roots[0] + '","axioms":[]}'; Error = "Malformed, duplicate, or unexpected Stage-B axiom result." },
            @{ Output = 'prefix STAGE_B_AXIOM_RESULT|{"theorem":"' + $roots[0] + '","axioms":[]}'; Error = "Malformed, duplicate, or unexpected Stage-B axiom result." },
            @{ Output = 'STAGE_B_AXIOM_RESULT|{"theorem":"' + $roots[0] + '","axioms":["propext","propext"]}'; Error = "Malformed, duplicate, or unexpected Stage-B axiom result." },
            @{ Output = 'STAGE_B_AXIOM_RESULT|{"theorem":"' + $roots[0] + '","axioms":[null]}'; Error = "Malformed, duplicate, or unexpected Stage-B axiom result." }
        ))
        {
            $rejected = $false
            try { $null = Read-StageBAxiomResults $test.Output $roots }
            catch { $rejected = $_.Exception.Message -eq $test.Error }
            if (-not $rejected) { throw "Stage-B axiom parser negative control survived: $($test.Error)" }
        }

        Write-Host "Verified the frozen Stage-B refinement roots and standard-only axiom dependencies."
    }
    finally { Pop-Location }
}
finally
{
    $resolved = [IO.Path]::GetFullPath($scratch)
    if ($resolved.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolved))
    {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
