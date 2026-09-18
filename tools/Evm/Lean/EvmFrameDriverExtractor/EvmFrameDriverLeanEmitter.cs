// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Evm.Lean.EvmFrameDriverExtractor;

/// <summary>Emits the theorem-free source-attached algebra for one frame-driver audit step.</summary>
internal static class EvmFrameDriverLeanEmitter
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] Emit(IrDocument document, string irSha256)
    {
        EvmFrameDriverProfile.ValidateEmitterInput(document, irSha256);

        StringBuilder branches = new();
        StringBuilder contracts = new();
        foreach (BranchDescriptor branch in document.Branches)
        {
            branches.Append("  \"").Append(Escape(branch.Name)).AppendLine("\",");
            contracts.Append("  \"").Append(Escape($"{branch.Name}|{branch.Invocation}|{string.Join(" -> ", branch.Effects)}|{branch.Settlement}"))
                .AppendLine("\",");
        }

        StringBuilder order = new();
        foreach (string operation in document.DispatchOrder)
            order.Append("  \"").Append(Escape(operation)).AppendLine("\",");

        StringBuilder controlMembers = new();
        StringBuilder controlOperations = new();
        StringBuilder controlTopology = new();
        StringBuilder controlPlan = new();
        StringBuilder controlPlanPattern = new();
        foreach (SourceControlIdentity control in document.SourceControl)
        {
            controlMembers.Append("  \"").Append(Escape(control.Member)).AppendLine("\",");
            foreach (string operation in control.Operations)
                controlOperations.Append("  \"").Append(Escape($"{control.Member}:{operation}")).AppendLine("\",");
            foreach (ControlNodeIdentity node in control.Topology)
            {
                controlTopology.Append("  { member := \"").Append(Escape(control.Member))
                    .Append("\", id := \"").Append(Escape(node.Id))
                    .Append("\", kind := \"").Append(Escape(node.Kind))
                    .Append("\", parentId := \"").Append(Escape(node.ParentId))
                    .Append("\", arm := \"").Append(Escape(node.Arm))
                    .Append("\", condition := \"").Append(Escape(node.Condition))
                    .Append("\", sha256 := \"").Append(Escape(node.Sha256))
                    .Append("\", operations := [");
                foreach (string operation in node.Operations)
                    controlTopology.Append('"').Append(Escape(operation)).Append("\", ");
                controlTopology.AppendLine("] },");
            }
        }

        for (int index = 0; index < document.ControlPlan.Length; index++)
        {
            ControlPlanStepIdentity step = document.ControlPlan[index];
            string stage = step.Stage switch
            {
                "prepare" => "ControlPlanStage.prepare",
                "dispatch" => "ControlPlanStage.dispatch",
                "classify" => "ControlPlanStage.classify",
                "settle" => "ControlPlanStage.settle",
                "cleanup" => "ControlPlanStage.cleanup",
                _ => throw new ExtractionException("Stage E control plan stage is not admitted: " + step.Stage + "."),
            };
            controlPlan.Append("  { stage := ").Append(stage)
                .Append(", member := \"").Append(Escape(step.Member))
                .Append("\", nodeId := \"").Append(Escape(step.NodeId))
                .Append("\", kind := \"").Append(Escape(step.Kind))
                .Append("\", sourceArm := \"").Append(Escape(step.SourceArm))
                .Append("\", sourceSha256 := \"").Append(Escape(step.SourceSha256)).AppendLine("\" },");

            controlPlanPattern.Append(index == 0 ? "  | " : "      ")
                .Append("{ stage := ").Append(stage)
                .Append(", member := \"").Append(Escape(step.Member))
                .Append("\", nodeId := \"").Append(Escape(step.NodeId))
                .Append("\", kind := \"").Append(Escape(step.Kind))
                .Append("\", sourceArm := \"").Append(Escape(step.SourceArm))
                .Append("\", sourceSha256 := \"").Append(Escape(step.SourceSha256))
                .Append("\" } ::");
            if (index == document.ControlPlan.Length - 1)
                controlPlanPattern.AppendLine(" [] =>");
            else
                controlPlanPattern.AppendLine();
        }

        StringBuilder branchBindings = new();
        foreach (BranchDescriptor branch in document.Branches)
            foreach (string binding in branch.SourceBindings)
            {
                string[] fields = binding.Split(':');
                if (fields.Length != 6)
                    throw new ExtractionException("Stage E branch binding is not canonical: " + binding + ".");
                ValidateSha256(fields[4], "Branch binding source node");
                branchBindings.Append("  { branch := \"").Append(Escape(branch.Name))
                    .Append("\", member := \"").Append(Escape(fields[0]))
                    .Append("\", nodeId := \"").Append(Escape(fields[1]))
                    .Append("\", kind := \"").Append(Escape(fields[2]))
                    .Append("\", sourceArm := \"").Append(Escape(fields[3]))
                    .Append("\", sourceSha256 := \"").Append(Escape(fields[4]))
                    .Append("\", arm := \"").Append(Escape(fields[5])).AppendLine("\" },");
            }

        string generated = Template
            .Replace("{{IR_SHA256}}", irSha256, StringComparison.Ordinal)
            .Replace("{{BRANCH_NAMES}}", branches.ToString(), StringComparison.Ordinal)
            .Replace("{{BRANCH_CONTRACTS}}", contracts.ToString(), StringComparison.Ordinal)
            .Replace("{{DISPATCH_ORDER}}", order.ToString(), StringComparison.Ordinal)
            .Replace("{{CONTROL_MEMBERS}}", controlMembers.ToString(), StringComparison.Ordinal)
            .Replace("{{CONTROL_OPERATIONS}}", controlOperations.ToString(), StringComparison.Ordinal)
            .Replace("{{CONTROL_TOPOLOGY}}", controlTopology.ToString(), StringComparison.Ordinal)
            .Replace("{{BRANCH_BINDINGS}}", branchBindings.ToString(), StringComparison.Ordinal)
            .Replace("{{CONTROL_PLAN}}", controlPlan.ToString(), StringComparison.Ordinal)
            .Replace("{{CONTROL_PLAN_PATTERN}}", controlPlanPattern.ToString(), StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        if (ContainsProofDeclaration(generated) || generated.Contains("sorry", StringComparison.Ordinal) ||
            generated.Contains("FrameMachineExecution", StringComparison.Ordinal) ||
            generated.Contains("Specification.Reference", StringComparison.Ordinal) ||
            generated.Contains("def runFuel", StringComparison.Ordinal) ||
            generated.Contains("def drive", StringComparison.Ordinal) ||
            !generated.Contains("def stepOnce", StringComparison.Ordinal) ||
            !generated.Contains("def runOne", StringComparison.Ordinal))
            throw new ExtractionException("Stage E emitter produced a proof-bearing or whole-run generated kernel.");

        return StrictUtf8.GetBytes(generated + "\n");
    }

    internal static void ValidateSha256(string? value, string description)
    {
        if (value is not { Length: 64 } || value.Any(static character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ExtractionException($"{description} SHA-256 must be lowercase hexadecimal.");
        if (value.All(static character => character == '0'))
            throw new ExtractionException($"{description} SHA-256 may not be the all-zero placeholder.");
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

-- Generated from the exact Stage E source/member admission profile.
-- This theorem-free kernel describes one source-attached ExecuteTransaction
-- frame-driver algebra step. It never calls itself recursively and therefore
-- does not claim a whole run or a production C# refinement.
-- Bytecode, full-precompile, journal, gas/refund, and settlement behavior remain
-- explicit accepted leaf adapters.
-- Canonical IR SHA-256: {{IR_SHA256}}

import EvmFrameMachineExtractor.Specification.FrameMachineState
import EvmFrameDriverExtractor.Specification.Types

namespace Eip803x.Evm.FrameDriver.Generated

open FrameMachineState
def irSha256 : String := "{{IR_SHA256}}"

def admittedBranchNames : List String := [
{{BRANCH_NAMES}}]

def admittedBranchContracts : List String := [
{{BRANCH_CONTRACTS}}]

def admittedDispatchOrder : List String := [
{{DISPATCH_ORDER}}]

def admittedSourceControl : SourceControl :=
  { admittedMembers := [
{{CONTROL_MEMBERS}}]
    admittedOperations := [
{{CONTROL_OPERATIONS}}] }

def admittedControlNodes : List ControlNodeEvidence := [
{{CONTROL_TOPOLOGY}}]

def admittedBranchBindings : List BranchBinding := [
{{BRANCH_BINDINGS}}]

def admittedControlPlan : List ControlPlanStep := [
{{CONTROL_PLAN}}]

def controlTopologyReady : Bool :=
  admittedControlNodes.length > 0 &&
  admittedBranchBindings.length > 0 &&
  admittedBranchNames.length == admittedBranchContracts.length &&
  admittedBranchNames.all (fun branch =>
    admittedBranchBindings.any (fun binding => binding.branch == branch)) &&
  admittedBranchBindings.all (fun binding =>
    admittedControlNodes.any (fun node =>
      node.member == binding.member && node.id == binding.nodeId && node.kind == binding.kind &&
      node.arm == binding.sourceArm && node.sha256 == binding.sourceSha256))

def controlPlanNodeReady (step : ControlPlanStep) : Bool :=
  admittedControlNodes.any (fun node =>
    node.member == step.member && node.id == step.nodeId && node.kind == step.kind &&
    node.arm == step.sourceArm && node.sha256 == step.sourceSha256)

def controlPlanStagesReady : Bool :=
  match admittedControlPlan with
  | { stage := ControlPlanStage.prepare, .. } :: { stage := ControlPlanStage.dispatch, .. } ::
      { stage := ControlPlanStage.classify, .. } :: { stage := ControlPlanStage.settle, .. } ::
      { stage := ControlPlanStage.cleanup, .. } :: [] => true
  | _ => false

def controlPlanReady : Bool :=
  controlPlanStagesReady && admittedControlPlan.all controlPlanNodeReady

def sourceControlReady : Bool :=
  Eip803x.Evm.FrameDriver.sourceControlReady admittedSourceControl

def prepare (leaves : DriverLeaves) (machine : Machine) : Machine :=
  match machine.current.phase with
  | .fresh => leaves.preparation.prepareFresh (leaves.preparation.clearReturnData machine)
  | .continuation => leaves.preparation.prepareContinuation machine
  | .running => machine

def dispatch (leaves : DriverLeaves) (machine : Machine) : Invocation :=
  match subjectOf machine with
  | .bytecode =>
      let outcome := leaves.dispatch.runBytecode machine
      .bytecode outcome.outcome outcome.directInlineStaticPrecompile
  | .fullPrecompile =>
      .fullPrecompile (leaves.dispatch.runFullPrecompile machine)

def classifyReturnedHalt (machine : Machine) (result : FrameResult) : SettlementRoute :=
  returnedRoute machine result

def classifyBytecodeStep (machine : Machine) (step : MachineStep) : SettlementRoute :=
  match step.result with
  | .continue _ => .continued
  | .suspend _ _ => .suspended
  | .halt result => classifyReturnedHalt machine result

def classifyPrecompileStep (machine : Machine) (step : MachineStep) : SettlementRoute :=
  match step.result with
  | .continue _ | .suspend _ _ => .invalidControl
  | .halt result =>
      match result.precompileSuccess with
      | some false =>
          if topLevel machine then .fullPrecompileReturnedFailureTop
          else .fullPrecompileReturnedFailureNested
      | _ => classifyReturnedHalt machine result

def classifyInvocation (machine : Machine) : Invocation → SettlementRoute
  | .bytecode (.returned step) _ => classifyBytecodeStep machine step
  | .bytecode (.thrownEvm _) _ =>
      if topLevel machine then .topLevelException else .childException
  | .bytecode .thrownOverflow _ =>
      if topLevel machine then .topLevelException else .childException
  | .bytecode (.escaped _) _ => .escaped
  | .bytecode (.cancelled _ _) _ => .cancelled
  | .fullPrecompile (.returned step) => classifyPrecompileStep machine step
  | .fullPrecompile .outOfGas =>
      if topLevel machine then .fullPrecompileOutOfGasTop
      else .fullPrecompileOutOfGasNested
  | .fullPrecompile (.returnedFailure _) =>
      if topLevel machine then .fullPrecompileReturnedFailureTop
      else .fullPrecompileReturnedFailureNested
  | .fullPrecompile (.managedException _) =>
      if topLevel machine then .fullPrecompileManagedExceptionTop
      else .fullPrecompileManagedExceptionNested
  | .fullPrecompile (.escaped _) => .escaped

def settleInvocation (leaves : DriverLeaves) (route : SettlementRoute)
    (invocation : Invocation) (machine : Machine) : DriverResult :=
  settle leaves route invocation machine

def cleanup (leaves : DriverLeaves) (result : DriverResult) : DriverResult :=
  cleanupTerminal leaves result

def stepOnceBody (leaves : DriverLeaves) (machine : Machine) : DriverResult :=
  let prepared := prepare leaves machine
  let invocation := dispatch leaves prepared
  let route := classifyInvocation prepared invocation
  let settled := settleInvocation leaves route invocation prepared
  let observed := { settled with directInlineStaticPrecompile := invocation.directInline }
  cleanup leaves observed

def executePlan (leaves : DriverLeaves) (machine : Machine)
    (plan : List ControlPlanStep) : DriverResult :=
  match plan with
{{CONTROL_PLAN_PATTERN}}
      stepOnceBody leaves machine
  | _ => DriverResult.incomplete machine

def stepOnce (leaves : DriverLeaves) (machine : Machine) : DriverResult :=
  if sourceControlReady && controlTopologyReady && controlPlanReady && phaseReady machine then
    executePlan leaves machine admittedControlPlan
  else DriverResult.incomplete machine

def runOne (fuel : Nat) (leaves : DriverLeaves) (machine : Machine) : FuelResult :=
  match fuel with
  | 0 => .exhausted machine
  | _ + 1 => .stepped (stepOnce leaves machine)

end Eip803x.Evm.FrameDriver.Generated
""";
}
