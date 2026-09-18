// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Evm.Lean.EvmFrameControlSettlementExtractor;

/// <summary>Emits a theorem-free, admission-profiled one-iteration frame driver.</summary>
internal static class EvmFrameControlSettlementLeanEmitter
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] Emit(IrDocument document, string irSha256)
    {
        EvmFrameControlSettlementProfile.ValidateEmitterInput(document, irSha256);
        StringBuilder imports = new();
        HashSet<string> modules = new(StringComparer.Ordinal);
        foreach (DependencyIdentity dependency in document.Dependencies)
        {
            string module = ModuleName(dependency.Path);
            if (modules.Add(module)) imports.Append("import ").AppendLine(module);
        }

        StringBuilder theoremChecks = new();
        foreach (DependencyIdentity dependency in document.Dependencies)
            foreach (string theorem in dependency.Theorems)
                theoremChecks.Append("#check @").AppendLine(theorem);

        StringBuilder branchNames = new();
        StringBuilder branchContracts = new();
        foreach (BranchDescriptor branch in document.Branches)
        {
            branchNames.Append("  \"").Append(Escape(branch.Name)).AppendLine("\",");
            branchContracts.Append("  \"").Append(Escape($"{branch.Name}|{branch.Invocation}|{string.Join(" -> ", branch.Effects)}|{branch.Settlement}"))
                .AppendLine("\",");
        }

        StringBuilder dispatchOrder = new();
        foreach (string phase in document.DispatchOrder)
            dispatchOrder.Append("  \"").Append(Escape(phase)).AppendLine("\",");

        string generated = Template
            .Replace("{{IMPORTS}}", imports.ToString(), StringComparison.Ordinal)
            .Replace("{{THEOREM_CHECKS}}", theoremChecks.ToString(), StringComparison.Ordinal)
            .Replace("{{IR_SHA256}}", irSha256, StringComparison.Ordinal)
            .Replace("{{BRANCH_NAMES}}", branchNames.ToString(), StringComparison.Ordinal)
            .Replace("{{BRANCH_CONTRACTS}}", branchContracts.ToString(), StringComparison.Ordinal)
            .Replace("{{DISPATCH_ORDER}}", dispatchOrder.ToString(), StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        if (ContainsProofDeclaration(generated) || generated.Contains("sorry", StringComparison.Ordinal) ||
            generated.Contains("FrameMachineExecution", StringComparison.Ordinal) ||
            generated.Contains("Specification.Reference", StringComparison.Ordinal) ||
            !generated.Contains("def driveIteration", StringComparison.Ordinal) ||
            generated.Contains("def runFuel", StringComparison.Ordinal) ||
            !generated.Contains("CanonicalLeaves", StringComparison.Ordinal))
            throw new ExtractionException("Stage D emitter produced a proof-bearing, whole-run, or non-independent generated kernel.");
        return StrictUtf8.GetBytes(generated);
    }

    internal static void ValidateSha256(string? value, string description)
    {
        if (value is not { Length: 64 } || value.Any(static character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ExtractionException($"{description} SHA-256 must be lowercase hexadecimal.");
    }

    private static string ModuleName(string path)
    {
        const string prefix = "tools/Evm/Lean/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(".lean", StringComparison.Ordinal))
            throw new ExtractionException($"Stage D dependency is outside the Lean source root: {path}.");
        return path[prefix.Length..^5].Replace('/', '.');
    }

    private static bool ContainsProofDeclaration(string source) => source.Split('\n').Any(static line =>
        line.TrimStart().StartsWith("theorem ", StringComparison.Ordinal) ||
        line.TrimStart().StartsWith("lemma ", StringComparison.Ordinal) ||
        line.TrimStart().StartsWith("axiom ", StringComparison.Ordinal));

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal);

    private const string Template = """
-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- Generated from the exact Stage D source/member admission profile.
-- This theorem-free kernel is a handwritten operational transcription of one admitted
-- ExecuteTransaction loop iteration; source-body semantics are not extracted here.
-- Opcode bodies, STATICCALL direct-inline details, full-precompile leaves, journals,
-- settlement arithmetic, tracing, and lifecycle effects remain named adapter boundaries.
-- Canonical IR SHA-256: {{IR_SHA256}}

{{IMPORTS}}

import EvmFrameControlSettlementExtractor.Specification.Types

{{THEOREM_CHECKS}}

namespace EvmFrameControlSettlementExtractor.Generated

open Eip803x.Evm.FrameMachineState
open EvmFrameControlSettlementExtractor

def irSha256 : String := "{{IR_SHA256}}"

def admittedBranchNames : List String := [
{{BRANCH_NAMES}}]

def admittedBranchContracts : List String := [
{{BRANCH_CONTRACTS}}]

def admittedDispatchOrder : List String := [
{{DISPATCH_ORDER}}]

def prepare (adapters : CanonicalLeaves) (machine : Machine) : Machine :=
  match machine.current.phase with
  | .fresh => adapters.preparation.prepareFresh (adapters.preparation.clearReturnData machine)
  | .continuation => adapters.preparation.prepareContinuation machine
  | .running => machine

def dispatch (adapters : CanonicalLeaves) (machine : Machine) : DriverInvocation :=
  match subjectOf machine with
  | .bytecode =>
      let bytecode := adapters.dispatch.executeBytecodeFrame machine
      { invocation := bytecode.invocation
        directInlineStaticPrecompile := bytecode.directInlineStaticPrecompile }
  | .fullPrecompile =>
      { invocation := adapters.dispatch.executeFullPrecompileFrame machine
        directInlineStaticPrecompile := none }

def classifyHalt (machine : Machine) (result : FrameResult) : SettlementRoute :=
  match result.exit with
  | .success =>
      if topLevel machine then .topLevelSuccess
      else if machine.current.executionType.isCreate then .createSuccessNested
      else .regularSuccessNested
  | .revert => if topLevel machine then .revertTop else .revertNested
  | .exception _ => if topLevel machine then .exceptionTop else .exceptionNested

def classifyReturned (machine : Machine) (step : MachineStep) : SettlementRoute :=
  match step.result with
  | .continue _ => .continued
  | .suspend _ _ => .suspend
  | .halt result => classifyHalt machine result

def classify (machine : Machine) : InvocationResult → SettlementRoute
  | .returned step => classifyReturned machine step
  | .fullPrecompileSuccess step => classifyReturned machine step
  | .fullPrecompileOutOfGas =>
      if topLevel machine then .fullPrecompileOutOfGasTop else .fullPrecompileOutOfGasNested
  | .fullPrecompileReturnedFailure _ =>
      if topLevel machine then .fullPrecompileReturnedFailureTop else .fullPrecompileReturnedFailureNested
  | .fullPrecompileManagedException _ =>
      if topLevel machine then .fullPrecompileManagedExceptionTop else .fullPrecompileManagedExceptionNested
  | .thrownEvm _ | .thrownOverflow =>
      if topLevel machine then .exceptionTop else .exceptionNested
  | .escapedInvocation _ => .escaped
  | .cancelled _ _ => .cancelled

def tagged (route : SettlementRoute) (outcome : ShellResult) : ShellResult :=
  { outcome with route := route }

def settleNestedCreate (adapters : CanonicalLeaves) (invocation : InvocationResult)
    (machine : Machine) : ShellResult :=
  match adapters.settlement.classifyNestedCreateCodeDeposit invocation machine with
  | .deposited => tagged .createSuccessNested
      (adapters.settlement.createSuccessNested invocation machine)
  | .invalidCode => tagged .codeDepositInvalidNested
      (adapters.settlement.codeDepositInvalidNested invocation machine)
  | .outOfGas => tagged .codeDepositOutOfGasNested
      (adapters.settlement.codeDepositOutOfGasNested invocation machine)

def settleRoute (adapters : CanonicalLeaves) (route : SettlementRoute)
    (invocation : InvocationResult) (machine : Machine) : ShellResult :=
  match route with
  | .continued => tagged .continued (adapters.settlement.continueStep invocation machine)
  | .suspend => tagged .suspend (adapters.settlement.suspendStep invocation machine)
  | .regularSuccessNested => tagged .regularSuccessNested
      (adapters.settlement.regularSuccessNested invocation machine)
  | .topLevelSuccess => tagged .topLevelSuccess
      (adapters.settlement.topLevelSuccess invocation machine)
  | .createSuccessNested => settleNestedCreate adapters invocation machine
  | .revertNested => tagged .revertNested (adapters.settlement.revertNested invocation machine)
  | .revertTop => tagged .revertTop (adapters.settlement.revertTop invocation machine)
  | .exceptionTop => tagged .exceptionTop (adapters.settlement.exceptionTop invocation machine)
  | .exceptionNested => tagged .exceptionNested (adapters.settlement.exceptionNested invocation machine)
  | .codeDepositInvalidNested => tagged .codeDepositInvalidNested
      (adapters.settlement.codeDepositInvalidNested invocation machine)
  | .codeDepositOutOfGasNested => tagged .codeDepositOutOfGasNested
      (adapters.settlement.codeDepositOutOfGasNested invocation machine)
  | .fullPrecompileOutOfGasNested => tagged .fullPrecompileOutOfGasNested
      (adapters.settlement.fullPrecompileOutOfGasNested invocation machine)
  | .fullPrecompileOutOfGasTop => tagged .fullPrecompileOutOfGasTop
      (adapters.settlement.fullPrecompileOutOfGasTop invocation machine)
  | .fullPrecompileReturnedFailureNested => tagged .fullPrecompileReturnedFailureNested
      (adapters.settlement.fullPrecompileReturnedFailureNested invocation machine)
  | .fullPrecompileReturnedFailureTop => tagged .fullPrecompileReturnedFailureTop
      (adapters.settlement.fullPrecompileReturnedFailureTop invocation machine)
  | .fullPrecompileManagedExceptionNested => tagged .fullPrecompileManagedExceptionNested
      (adapters.settlement.fullPrecompileManagedExceptionNested invocation machine)
  | .fullPrecompileManagedExceptionTop => tagged .fullPrecompileManagedExceptionTop
      (adapters.settlement.fullPrecompileManagedExceptionTop invocation machine)
  | .cancelled =>
      { termination := .cancelled
        route := .cancelled
        machine := machine
        result := none
        reason := some (cancellationReasonOf invocation)
        status := none
        directInlineStaticPrecompile := none
        disposal := DisposalObservation.notObserved }
  | .escaped =>
      { termination := .escaped
        route := .escaped
        machine := machine
        result := none
        reason := some (escapedReasonOf invocation)
        status := none
        directInlineStaticPrecompile := none
        disposal := DisposalObservation.notObserved }
  | .noSettlement =>
      { termination := .incomplete
        route := .noSettlement
        machine := machine
        result := none
        reason := some "unsettledRoute"
        status := none
        directInlineStaticPrecompile := none
        disposal := DisposalObservation.notObserved }

def cleanupTerminal (adapters : CanonicalLeaves) (outcome : ShellResult) : ShellResult :=
  match outcome.termination with
  | .completed | .suspended => outcome
  | .cancelled | .escaped | .incomplete =>
      let cleanup := adapters.cleanup outcome
      { outcome with machine := cleanup.2, disposal := cleanup.1 }

def driveIteration (adapters : CanonicalLeaves) (machine : Machine) : ShellResult :=
  let prepared := prepare adapters machine
  let dispatched := dispatch adapters prepared
  let settled := settleRoute adapters (classify prepared dispatched.invocation)
    dispatched.invocation prepared
  let observed := { settled with directInlineStaticPrecompile := dispatched.directInlineStaticPrecompile }
  cleanupTerminal adapters observed

end EvmFrameControlSettlementExtractor.Generated
""";
}
