// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text;

namespace Nethermind.Evm.Lean.EvmFrameDriverExtractor;

/// <summary>Emits the theorem-free Stage F finite-fuel operational kernel.</summary>
internal static class OperationalLeanEmitter
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] Emit(OperationalIrDocument document, string irSha256)
        => EmitCore(document, irSha256, OperationalProfile.AcceptedOperationalEvidenceSha256);

    // Tests use the document's own digest to inspect fail-closed behavior after
    // a semantic IR mutation. Production extraction always uses the pinned
    // accepted digest through Emit above.
    internal static byte[] EmitForMutationTest(OperationalIrDocument document, string irSha256)
        => EmitCore(document, irSha256, OperationalProfile.OperationalEvidenceDigest(document));

    private static byte[] EmitCore(OperationalIrDocument document, string irSha256,
        string expectedOperationalEvidenceSha256)
    {
        ValidateInput(document, irSha256, expectedOperationalEvidenceSha256);
        StringBuilder sourceMembers = Lines(document.SourceControl.Select(static control => control.Member));
        StringBuilder sourceOperations = Lines(document.SourceControl.SelectMany(static control =>
            control.Operations.Select(operation => control.Member + ":" + operation)));
        StringBuilder sourceDigests = Lines(document.SourceControl.Select(static control =>
            control.Member + "|" + control.Sha256 + "|" + control.TopologySha256));
        StringBuilder controlPlan = RawLines(document.ControlPlan.Select(LeanControlPlan));
        StringBuilder topology = RawLines(document.Topology.Select(LeanTopologyNode));
        StringBuilder branchBindings = RawLines(document.BranchBindings.Select(LeanBranchBinding));
        StringBuilder opcodeRoutes = RawLines(document.OpcodeRoutes.Select(LeanOpcodeRoute));
        StringBuilder precompileRoutes = RawLines(document.PrecompileRoutes.Select(LeanPrecompileRoute));
        StringBuilder theorems = Lines(document.AcceptedDependencies.Select(static dependency => dependency.Theorem));
        StringBuilder adapters = Lines(document.Adapters.Select(static adapter => adapter.Name));
        StringBuilder vectors = Lines(document.MutationVectors);
        string preparePlanStep = LeanControlPlan(PlanStep(document, "prepare"));
        string dispatchPlanStep = LeanControlPlan(PlanStep(document, "dispatch"));
        string classifyPlanStep = LeanControlPlan(PlanStep(document, "classify"));
        string settlePlanStep = LeanControlPlan(PlanStep(document, "settle"));
        string cleanupPlanStep = LeanControlPlan(PlanStep(document, "cleanup"));
        string prepareBranches = LeanStrings(RequiredBranches(document, "prepare"));
        string dispatchBranches = LeanStrings(RequiredBranches(document, "dispatch", precompileOnly: false));
        string precompileBranches = LeanStrings(RequiredBranches(document, "dispatch", precompileOnly: true));
        string classifyBranches = LeanStrings(RequiredBranches(document, "classify"));
        string settleBranches = LeanStrings(RequiredBranches(document, "settle"));
        string cleanupBranches = LeanStrings(RequiredBranches(document, "cleanup"));
        string prepareInstructions = LeanControlInstructions(document, "prepare");
        string dispatchInstructions = LeanControlInstructions(document, "dispatch", precompileOnly: false);
        string precompileInstructions = LeanControlInstructions(document, "dispatch", precompileOnly: true);
        string classifyInstructions = LeanControlInstructions(document, "classify");
        string settleInstructions = LeanControlInstructions(document, "settle");
        string cleanupInstructions = LeanControlInstructions(document, "cleanup");
        string loopBatchLimit = document.Loop.BatchLimit.ToString(CultureInfo.InvariantCulture);
        string loopDispatchModes = LeanDispatchTables(document.Loop.DispatchModes);
        string loopRules = LeanLoopRules(document.Loop.EntryTransitions, document.Loop.FuelRules);
        string loopEntryTransitions = LeanStrings(document.Loop.EntryTransitions);
        string loopBytecodeOutcomes = LeanStrings(document.Loop.BytecodeOutcomes);
        string loopSettlementRoutes = LeanStrings(document.Loop.SettlementRoutes);
        string loopStateEffects = LeanStrings(document.Loop.StateEffects);
        string loopCleanupRoutes = LeanStrings(document.Loop.CleanupRoutes);
        string loopFuelRules = LeanStrings(document.Loop.FuelRules);
        string loopMeasure = Escape(document.Loop.Measure);
        string loopAdequacyStatus = Escape(document.Loop.AdequacyStatus);
        string generated = Template
            .Replace("{{IR_SHA256}}", irSha256, StringComparison.Ordinal)
            .Replace("{{SOURCE_MEMBERS}}", sourceMembers.ToString(), StringComparison.Ordinal)
            .Replace("{{SOURCE_OPERATIONS}}", sourceOperations.ToString(), StringComparison.Ordinal)
            .Replace("{{SOURCE_CONTROL_DIGESTS}}", sourceDigests.ToString(), StringComparison.Ordinal)
            .Replace("{{CONTROL_PLAN}}", controlPlan.ToString(), StringComparison.Ordinal)
            .Replace("{{TOPOLOGY}}", topology.ToString(), StringComparison.Ordinal)
            .Replace("{{BRANCH_BINDINGS}}", branchBindings.ToString(), StringComparison.Ordinal)
            .Replace("{{OPCODE_ROUTES}}", opcodeRoutes.ToString(), StringComparison.Ordinal)
            .Replace("{{PRECOMPILE_ROUTES}}", precompileRoutes.ToString(), StringComparison.Ordinal)
            .Replace("{{PREPARE_PLAN_STEP}}", preparePlanStep, StringComparison.Ordinal)
            .Replace("{{DISPATCH_PLAN_STEP}}", dispatchPlanStep, StringComparison.Ordinal)
            .Replace("{{CLASSIFY_PLAN_STEP}}", classifyPlanStep, StringComparison.Ordinal)
            .Replace("{{SETTLE_PLAN_STEP}}", settlePlanStep, StringComparison.Ordinal)
            .Replace("{{CLEANUP_PLAN_STEP}}", cleanupPlanStep, StringComparison.Ordinal)
            .Replace("{{PREPARE_BRANCHES}}", prepareBranches, StringComparison.Ordinal)
            .Replace("{{DISPATCH_BRANCHES}}", dispatchBranches, StringComparison.Ordinal)
            .Replace("{{PRECOMPILE_BRANCHES}}", precompileBranches, StringComparison.Ordinal)
            .Replace("{{CLASSIFY_BRANCHES}}", classifyBranches, StringComparison.Ordinal)
            .Replace("{{SETTLE_BRANCHES}}", settleBranches, StringComparison.Ordinal)
            .Replace("{{CLEANUP_BRANCHES}}", cleanupBranches, StringComparison.Ordinal)
            .Replace("{{PREPARE_INSTRUCTIONS}}", prepareInstructions, StringComparison.Ordinal)
            .Replace("{{DISPATCH_INSTRUCTIONS}}", dispatchInstructions, StringComparison.Ordinal)
            .Replace("{{PRECOMPILE_INSTRUCTIONS}}", precompileInstructions, StringComparison.Ordinal)
            .Replace("{{CLASSIFY_INSTRUCTIONS}}", classifyInstructions, StringComparison.Ordinal)
            .Replace("{{SETTLE_INSTRUCTIONS}}", settleInstructions, StringComparison.Ordinal)
            .Replace("{{CLEANUP_INSTRUCTIONS}}", cleanupInstructions, StringComparison.Ordinal)
            .Replace("{{LOOP_BATCH_LIMIT}}", loopBatchLimit, StringComparison.Ordinal)
            .Replace("{{LOOP_DISPATCH_MODES}}", loopDispatchModes, StringComparison.Ordinal)
            .Replace("{{LOOP_RULES}}", loopRules, StringComparison.Ordinal)
            .Replace("{{LOOP_ENTRY_TRANSITIONS}}", loopEntryTransitions, StringComparison.Ordinal)
            .Replace("{{LOOP_BYTECODE_OUTCOMES}}", loopBytecodeOutcomes, StringComparison.Ordinal)
            .Replace("{{LOOP_SETTLEMENT_ROUTES}}", loopSettlementRoutes, StringComparison.Ordinal)
            .Replace("{{LOOP_STATE_EFFECTS}}", loopStateEffects, StringComparison.Ordinal)
            .Replace("{{LOOP_CLEANUP_ROUTES}}", loopCleanupRoutes, StringComparison.Ordinal)
            .Replace("{{LOOP_FUEL_RULES}}", loopFuelRules, StringComparison.Ordinal)
            .Replace("{{LOOP_MEASURE}}", loopMeasure, StringComparison.Ordinal)
            .Replace("{{LOOP_ADEQUACY_STATUS}}", loopAdequacyStatus, StringComparison.Ordinal)
            .Replace("{{DEPENDENCY_THEOREMS}}", theorems.ToString(), StringComparison.Ordinal)
            .Replace("{{ADAPTER_NAMES}}", adapters.ToString(), StringComparison.Ordinal)
            .Replace("{{MUTATION_VECTORS}}", vectors.ToString(), StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        if (ContainsProofDeclaration(generated) || generated.Contains("sorry", StringComparison.Ordinal) ||
            ContainsStandaloneAdmission(generated) ||
            generated.Contains("Specification.Reference", StringComparison.Ordinal) ||
            generated.Contains("SameLeafAgreement", StringComparison.Ordinal) ||
            !generated.Contains("def runFuel", StringComparison.Ordinal) ||
            !generated.Contains("def evaluateStep", StringComparison.Ordinal) ||
            !generated.Contains("def execute", StringComparison.Ordinal))
            throw new ExtractionException("Stage F emitter produced a proof-bearing or incomplete operational kernel.");
        return StrictUtf8.GetBytes(generated + "\n");
    }

    private static void ValidateInput(OperationalIrDocument document, string irSha256,
        string expectedOperationalEvidenceSha256)
    {
        EvmFrameDriverLeanEmitter.ValidateSha256(irSha256, "Stage F canonical IR");
        if (document.AcceptanceState != OperationalProfile.AcceptanceState ||
            document.TargetRoot != EvmFrameDriverProfile.TargetRoot ||
            document.TargetFork != EvmFrameDriverProfile.TargetFork ||
            document.TargetGasPolicy != EvmFrameDriverProfile.TargetGasPolicy ||
            document.StageEIrSha256 != OperationalProfile.AcceptedStageEIrSha256 ||
            !OperationalProfile.OperationalEvidenceDigest(document).Equals(
                expectedOperationalEvidenceSha256, StringComparison.Ordinal) ||
            document.Loop.BatchLimit != 1024 || document.Loop.DispatchModes.Length != 4 ||
            document.Sources.Length != OperationalProfile.ExpectedOperationalSourceCount ||
            document.Members.Length != OperationalProfile.ExpectedOperationalMemberCount ||
            document.SourceControl.Length != 4 ||
            !document.SourceControl.Select(static control => control.Member).SequenceEqual(
                ["ExecuteTransaction", "RunByteCode", "RunDispatchLoop", "Dispose"], StringComparer.Ordinal) ||
            document.ControlPlan.Length != 5 ||
            document.ControlPlan.Any(static step => step.Actions is null || step.Actions.Length == 0 ||
                step.Actions.Distinct().Count() != step.Actions.Length ||
                step.Actions.Any(static action => !Enum.IsDefined(action))) ||
            document.CompilerReferences.Length != 330 ||
            document.Topology.Length == 0 || document.BranchBindings.Length != OperationalProfile.ExpectedOperationalBranchBindingCount ||
            document.BranchBindings.Any(static binding => string.IsNullOrWhiteSpace(binding.Invocation) ||
                binding.Effects is null || binding.Effects.Length == 0 ||
                binding.Effects.Any(string.IsNullOrWhiteSpace) ||
                !Enum.IsDefined(binding.Predicate) || binding.EffectKinds is null ||
                binding.EffectKinds.Length != binding.Effects.Length ||
                binding.EffectKinds.Any(static effect => !Enum.IsDefined(effect)) ||
                !Enum.IsDefined(binding.SettlementKind) ||
                string.IsNullOrWhiteSpace(binding.Settlement) || binding.Stages is null ||
                binding.Stages.Length == 0 || binding.Stages.Any(static stage => stage is not (
                    "prepare" or "dispatch" or "classify" or "settle" or "cleanup")) ||
                binding.Actions is null || binding.Actions.Length != binding.Stages.Length ||
                binding.Actions.Any(static action => !Enum.IsDefined(action))) ||
            document.OpcodeRoutes.Length != 1024 || document.PrecompileRoutes.Length != 18 ||
            document.Adapters.Length < 15 || document.AcceptedDependencies.Length < 24 ||
            document.MutationVectors.Length < 6 ||
            !document.Adapters.Any(static adapter => adapter.Name == "createDeposit"))
            throw new ExtractionException("Stage F emitter input is not a complete operational profile.");
        if (document.Adapters.Any(static adapter => string.IsNullOrWhiteSpace(adapter.Name) ||
                string.IsNullOrWhiteSpace(adapter.FailureMode) ||
                !adapter.FailureMode.Contains("none", StringComparison.OrdinalIgnoreCase)))
            throw new ExtractionException("Stage F adapter input is not fail-closed.");
        ValidateInstructionBindingOrder(document);
    }

    private static void ValidateInstructionBindingOrder(OperationalIrDocument document)
    {
        foreach (OperationalControlPlanStepIdentity step in document.ControlPlan)
        {
            OperationalControlActionIdentity[] expected = [.. document.BranchBindings
                .Where(binding => binding.Stages.Contains(step.Stage, StringComparer.Ordinal))
                .SelectMany(binding => binding.Stages.Select((stage, index) =>
                    (stage, action: binding.Actions[index])))
                .Where(pair => pair.stage == step.Stage)
                .Select(pair => pair.action)
                .Distinct()];
            if (!step.Actions.SequenceEqual(expected))
                throw new ExtractionException("Stage F typed control-plan actions are not aligned with branch instructions.");
        }

        List<string> emitted = [];
        foreach (string stage in new[] { "prepare", "dispatch", "classify", "settle", "cleanup" })
        {
            emitted.AddRange(StageBindings(document, stage, precompileOnly: false)
                .Select(binding => InstructionKey(stage, binding)));
            if (stage == "dispatch")
                emitted.AddRange(StageBindings(document, stage, precompileOnly: true)
                    .Select(binding => InstructionKey(stage, binding)));
        }

        int expectedCount = document.BranchBindings.Sum(static binding => binding.Stages.Length);
        if (emitted.Count != expectedCount || emitted.Count != emitted.Distinct(StringComparer.Ordinal).Count())
            throw new ExtractionException("Stage F source branch bindings do not lower one-to-one to ordered instructions.");

        foreach (OperationalBranchBindingIdentity binding in document.BranchBindings)
            foreach (string stage in binding.Stages)
                if (emitted.Count(key => key == InstructionKey(stage, binding)) != 1)
                    throw new ExtractionException("Stage F source branch binding has no unique emitted instruction: " +
                        binding.Branch + " at " + stage + ".");
    }

    private static string InstructionKey(string stage, OperationalBranchBindingIdentity binding) =>
        stage + "|" + binding.Branch + "|" + binding.Member + "|" + binding.NodeId + "|" +
        binding.Kind + "|" + binding.SourceArm + "|" + binding.SourceSha256 + "|" + binding.BindingArm + "|" +
        binding.Invocation + "|" + binding.Predicate + "|" + string.Join(';', binding.Effects) + "|" +
        string.Join(';', binding.EffectKinds) + "|" + binding.Settlement + "|" + binding.SettlementKind + "|" +
        string.Join(';', binding.Actions.Select(static action => action.ToString()));

    private static StringBuilder Lines(IEnumerable<string> values)
    {
        StringBuilder lines = new();
        // Source operation order is part of the accepted Roslyn topology.  Do
        // not canonicalise it away: repeated `if`, `call`, and loop nodes are
        // distinct source events even when their rendered labels coincide.
        foreach (string value in values)
            lines.Append("  \"").Append(Escape(value)).AppendLine("\",");
        return lines;
    }

    private static StringBuilder RawLines(IEnumerable<string> values)
    {
        StringBuilder lines = new();
        foreach (string value in values)
            lines.Append("  ").Append(value).AppendLine(",");
        return lines;
    }

    private static string LeanControlPlan(OperationalControlPlanStepIdentity step) =>
        $"{{ stage := ControlPlanStage.{step.Stage}, member := \"{Escape(step.Member)}\", " +
        $"nodeId := \"{Escape(step.NodeId)}\", kind := \"{Escape(step.Kind)}\", " +
        $"sourceArm := \"{Escape(step.SourceArm)}\", sourceSha256 := \"{Escape(step.SourceSha256)}\", " +
        $"actions := {LeanControlActions(step.Actions)} }}";

    private static OperationalControlPlanStepIdentity PlanStep(OperationalIrDocument document, string stage)
    {
        OperationalControlPlanStepIdentity[] matches = document.ControlPlan.Where(step =>
            step.Stage.Equals(stage, StringComparison.Ordinal)).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new ExtractionException("Stage F typed control plan does not have one " + stage + " step.");
    }

    private static OperationalBranchBindingIdentity[] StageBindings(OperationalIrDocument document, string stage,
        bool precompileOnly = false)
    {
        OperationalBranchBindingIdentity[] matches = [.. document.BranchBindings
            .Where(binding => binding.Stages.Contains(stage, StringComparer.Ordinal) &&
                (IsPrecompilePredicate(binding.Predicate) == precompileOnly))];
        return matches.Length > 0
            ? matches
            : throw new ExtractionException("Stage F typed branch bindings do not have an attached " + stage +
                " control stage.");
    }

    private static string[] RequiredBranches(OperationalIrDocument document, string stage,
        bool precompileOnly = false) =>
        [.. StageBindings(document, stage, precompileOnly).Select(static binding => binding.Branch)];

    private static bool IsPrecompilePredicate(OperationalControlPredicateIdentity predicate) => predicate is
        OperationalControlPredicateIdentity.PrecompileOutOfGasNested or
        OperationalControlPredicateIdentity.PrecompileOutOfGasTop or
        OperationalControlPredicateIdentity.PrecompileReturnedFailure or
        OperationalControlPredicateIdentity.PrecompileManagedException;

    private static string LeanControlInstructions(OperationalIrDocument document, string stage,
        bool precompileOnly = false)
    {
        _ = PlanStep(document, stage);
        return "[" + string.Join(", ", StageBindings(document, stage, precompileOnly)
            .Select(binding => LeanControlInstruction(stage, binding))) + "]";
    }

    private static string LeanControlInstruction(string stage, OperationalBranchBindingIdentity binding) =>
        $"{{ stage := .{LeanControlStage(stage)}, predicate := .{LeanControlPredicate(binding.Predicate)}, " +
        $"effects := {LeanControlEffects(binding.EffectKinds)}, settlement := " +
        $".{LeanControlSettlement(binding.SettlementKind)}, action := " +
        $".{LeanControlAction(stage, binding)}, branch := \"{Escape(binding.Branch)}\", " +
        $"member := \"{Escape(binding.Member)}\", " +
        $"nodeId := \"{Escape(binding.NodeId)}\", kind := \"{Escape(binding.Kind)}\", " +
        $"sourceArm := \"{Escape(binding.SourceArm)}\", bindingArm := \"{Escape(binding.BindingArm)}\", " +
        $"sourceSha256 := \"{Escape(binding.SourceSha256)}\" }}";

    // This is the finite lowering from an admitted source branch record to an
    // executable AST action.  The generated kernel consumes the emitted action
    // field; it does not recover one from a branch/profile label or effect
    // sequence at runtime.  Unknown source combinations fail closed here.
    private static string LeanControlAction(string stage, OperationalBranchBindingIdentity binding)
    {
        int stageIndex = Array.IndexOf(binding.Stages, stage);
        if (stageIndex < 0 || binding.Actions is null || stageIndex >= binding.Actions.Length)
            throw new ExtractionException("Stage F source action is not aligned with " + binding.Branch +
                " at " + stage + ".");
        return LeanActionName(binding.Actions[stageIndex]);
    }

    private static string LeanActionName(OperationalControlActionIdentity action)
    {
        string name = action.ToString();
        return name.Length == 0 ? throw new ExtractionException("Stage F source action is empty.") :
            char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static string LeanControlActions(OperationalControlActionIdentity[] actions) =>
        "[" + string.Join(", ", actions.Select(LeanActionName).Select(static action => "." + action)) + "]";

    private static string LeanControlStage(string stage) => stage switch
    {
        "prepare" => "prepare",
        "dispatch" => "dispatch",
        "classify" => "classify",
        "settle" => "settle",
        "cleanup" => "cleanup",
        _ => throw new ExtractionException("Unknown Stage F control stage " + stage + "."),
    };

    private static string LeanControlPredicate(OperationalControlPredicateIdentity predicate) => predicate switch
    {
        OperationalControlPredicateIdentity.FreshFrame => "freshFrame",
        OperationalControlPredicateIdentity.ContinuationFrame => "continuationFrame",
        OperationalControlPredicateIdentity.BytecodeFrame => "bytecodeFrame",
        OperationalControlPredicateIdentity.FullPrecompileFrame => "fullPrecompileFrame",
        OperationalControlPredicateIdentity.BytecodeContinue => "bytecodeContinue",
        OperationalControlPredicateIdentity.BytecodeSuspend => "bytecodeSuspend",
        OperationalControlPredicateIdentity.NestedRegularSuccess => "nestedRegularSuccess",
        OperationalControlPredicateIdentity.NestedCreateSuccess => "nestedCreateSuccess",
        OperationalControlPredicateIdentity.CreateDepositInvalidCode => "createDepositInvalidCode",
        OperationalControlPredicateIdentity.CreateDepositOutOfGas => "createDepositOutOfGas",
        OperationalControlPredicateIdentity.NestedRevert => "nestedRevert",
        OperationalControlPredicateIdentity.NestedException => "nestedException",
        OperationalControlPredicateIdentity.ResumeParent => "resumeParent",
        OperationalControlPredicateIdentity.TopLevelSuccess => "topLevelSuccess",
        OperationalControlPredicateIdentity.TopLevelRevert => "topLevelRevert",
        OperationalControlPredicateIdentity.TopLevelException => "topLevelException",
        OperationalControlPredicateIdentity.PrecompileOutOfGasNested => "precompileOutOfGasNested",
        OperationalControlPredicateIdentity.PrecompileOutOfGasTop => "precompileOutOfGasTop",
        OperationalControlPredicateIdentity.PrecompileReturnedFailure => "precompileReturnedFailure",
        OperationalControlPredicateIdentity.PrecompileManagedException => "precompileManagedException",
        OperationalControlPredicateIdentity.Cancelled => "cancelled",
        OperationalControlPredicateIdentity.Escaped => "escaped",
        OperationalControlPredicateIdentity.InvalidControl => "invalidControl",
        OperationalControlPredicateIdentity.Completed => "completed",
        _ => throw new ExtractionException("Unknown Stage F control predicate " + predicate + "."),
    };

    private static string LeanControlEffects(IEnumerable<OperationalControlEffectIdentity> effects) =>
        "[" + string.Join(", ", effects.Select(LeanControlEffect).Select(effect => "." + effect)) + "]";

    private static string LeanControlEffect(OperationalControlEffectIdentity effect) => effect switch
    {
        OperationalControlEffectIdentity.ClearReturnData => "clearReturnData",
        OperationalControlEffectIdentity.PrepareFresh => "prepareFresh",
        OperationalControlEffectIdentity.RetainReturnData => "retainReturnData",
        OperationalControlEffectIdentity.PrepareContinuation => "prepareContinuation",
        OperationalControlEffectIdentity.RunBytecode => "runBytecode",
        OperationalControlEffectIdentity.RunDispatchLoop => "runDispatchLoop",
        OperationalControlEffectIdentity.RunFullPrecompile => "runFullPrecompile",
        OperationalControlEffectIdentity.ExecutePrecompile => "executePrecompile",
        OperationalControlEffectIdentity.RetainCurrentFrame => "retainCurrentFrame",
        OperationalControlEffectIdentity.PrepareChildFrame => "prepareChildFrame",
        OperationalControlEffectIdentity.RetainParent => "retainParent",
        OperationalControlEffectIdentity.PopParent => "popParent",
        OperationalControlEffectIdentity.MergeChild => "mergeChild",
        OperationalControlEffectIdentity.RepayStateGasSpill => "repayStateGasSpill",
        OperationalControlEffectIdentity.PrepareCreateData => "prepareCreateData",
        OperationalControlEffectIdentity.HandleCreate => "handleCreate",
        OperationalControlEffectIdentity.RestoreWorld => "restoreWorld",
        OperationalControlEffectIdentity.CreditParent => "creditParent",
        OperationalControlEffectIdentity.BurnDepositGas => "burnDepositGas",
        OperationalControlEffectIdentity.RestoreSnapshot => "restoreSnapshot",
        OperationalControlEffectIdentity.RestoreChildGas => "restoreChildGas",
        OperationalControlEffectIdentity.HandleRevert => "handleRevert",
        OperationalControlEffectIdentity.RestoreFailureControl => "restoreFailureControl",
        OperationalControlEffectIdentity.ResumeParent => "resumeParent",
        OperationalControlEffectIdentity.PrepareTopLevelSubstate => "prepareTopLevelSubstate",
        OperationalControlEffectIdentity.RefundRevertedStateGas => "refundRevertedStateGas",
        OperationalControlEffectIdentity.HandleExceptionOrFailure => "handleExceptionOrFailure",
        OperationalControlEffectIdentity.FailureSettlement => "failureSettlement",
        OperationalControlEffectIdentity.Dispose => "dispose",
        OperationalControlEffectIdentity.FailClosed => "failClosed",
        OperationalControlEffectIdentity.DisposeActiveFrames => "disposeActiveFrames",
        _ => throw new ExtractionException("Unknown Stage F control effect " + effect + "."),
    };

    private static string LeanControlSettlement(OperationalControlSettlementIdentity settlement) => settlement switch
    {
        OperationalControlSettlementIdentity.Preparation => "preparation",
        OperationalControlSettlementIdentity.Invocation => "invocation",
        OperationalControlSettlementIdentity.Continued => "continued",
        OperationalControlSettlementIdentity.Suspended => "suspended",
        OperationalControlSettlementIdentity.ChildSuccess => "childSuccess",
        OperationalControlSettlementIdentity.ChildCreateSuccess => "childCreateSuccess",
        OperationalControlSettlementIdentity.ChildCreateInvalidCode => "childCreateInvalidCode",
        OperationalControlSettlementIdentity.ChildCreateOutOfGas => "childCreateOutOfGas",
        OperationalControlSettlementIdentity.ChildRevert => "childRevert",
        OperationalControlSettlementIdentity.ChildException => "childException",
        OperationalControlSettlementIdentity.TopLevelSuccess => "topLevelSuccess",
        OperationalControlSettlementIdentity.TopLevelRevert => "topLevelRevert",
        OperationalControlSettlementIdentity.TopLevelException => "topLevelException",
        OperationalControlSettlementIdentity.FullPrecompileOutOfGasNested => "fullPrecompileOutOfGasNested",
        OperationalControlSettlementIdentity.FullPrecompileOutOfGasTop => "fullPrecompileOutOfGasTop",
        OperationalControlSettlementIdentity.FullPrecompileReturnedFailure => "fullPrecompileReturnedFailure",
        OperationalControlSettlementIdentity.FullPrecompileManagedException => "fullPrecompileManagedException",
        OperationalControlSettlementIdentity.Cancelled => "cancelled",
        OperationalControlSettlementIdentity.Escaped => "escaped",
        OperationalControlSettlementIdentity.InvalidControl => "invalidControl",
        OperationalControlSettlementIdentity.Completed => "completed",
        _ => throw new ExtractionException("Unknown Stage F control settlement " + settlement + "."),
    };

    private static string LeanTopologyNode(OperationalTopologyNodeIdentity node) =>
        $"{{ member := \"{Escape(node.Member)}\", id := \"{Escape(node.Id)}\", " +
        $"kind := \"{Escape(node.Kind)}\", parentId := \"{Escape(node.ParentId)}\", " +
        $"arm := \"{Escape(node.Arm)}\", condition := \"{Escape(node.Condition)}\", " +
        $"sha256 := \"{Escape(node.Sha256)}\", operations := {LeanStrings(node.Operations)} }}";

    private static string LeanBranchBinding(OperationalBranchBindingIdentity binding) =>
        $"{{ branch := \"{Escape(binding.Branch)}\", stages := {LeanControlStages(binding.Stages)}, " +
        $"member := \"{Escape(binding.Member)}\", " +
        $"nodeId := \"{Escape(binding.NodeId)}\", kind := \"{Escape(binding.Kind)}\", " +
        $"sourceArm := \"{Escape(binding.SourceArm)}\", sourceSha256 := \"{Escape(binding.SourceSha256)}\", " +
        $"bindingArm := \"{Escape(binding.BindingArm)}\", " +
        $"predicate := .{LeanControlPredicate(binding.Predicate)}, " +
        $"effects := {LeanControlEffects(binding.EffectKinds)}, " +
        $"settlement := .{LeanControlSettlement(binding.SettlementKind)}, " +
        $"actions := {LeanControlActions(binding.Actions)} }}";

    private static string LeanControlStages(IEnumerable<string> stages) =>
        "[" + string.Join(", ", stages.Select(stage => "." + LeanControlStage(stage))) + "]";

    private static string LeanOpcodeRoute(OperationalOpcodeRouteIdentity route) =>
        $"{{ dispatchTable := .{LeanDispatchTable(route.DispatchTable)}, byte := {route.Byte}, " +
        $"instruction := \"{Escape(route.Instruction)}\", kind := .{LeanRouteKind(route.RouteKind)}, " +
        $"activationRule := \"{Escape(route.ActivationRule)}\", package := \"{Escape(route.Package)}\", " +
        $"closedHandlerRoot := \"{Escape(route.ClosedHandlerRoot)}\" }}";

    private static string LeanPrecompileRoute(OperationalPrecompileRouteIdentity route) =>
        $"{{ name := \"{Escape(route.Name)}\", address := {route.Address}, " +
        $"activationRule := \"{Escape(route.ActivationRule)}\", providerRoot := \"{Escape(route.ProviderRoot)}\", " +
        $"wrapperManifestPath := \"{Escape(route.WrapperManifestPath)}\", " +
        $"wrapperManifestSha256 := \"{Escape(route.WrapperManifestSha256)}\", " +
        $"leanModule := \"{Escape(route.LeanModule)}\", fullyQualifiedTheorem := {LeanOption(route.FullyQualifiedTheorem)} }}";

    private static string LeanStrings(IEnumerable<string> values) =>
        "[" + string.Join(", ", values.Select(value => "\"" + Escape(value) + "\"")) + "]";

    private static string LeanDispatchTables(IEnumerable<string> values) =>
        "[" + string.Join(", ", values.Select(value => "." + LeanDispatchTable(value))) + "]";

    private static string LeanLoopRules(IEnumerable<string> entryTransitions, IEnumerable<string> fuelRules)
    {
        List<string> rules = [];
        foreach (string transition in entryTransitions)
            if (transition == "clearReturnData iff !IsContinuation") rules.Add(".freshClearsReturnData");
            else if (transition is "PrepareFresh" or "PrepareContinuation") rules.Add(".preparesFreshOrContinuation");
            else throw new ExtractionException("Unknown Stage F loop entry transition " + transition + ".");
        foreach (string rule in fuelRules)
            if (rule == "fuel decrements once per driver iteration") rules.Add(".decrementsFuel");
            else if (rule == "cancelable nonterminal dispatch epochs are exactly 1024 opcodes") rules.Add(".cancelableEpoch1024");
            else if (rule == "a cancelable poll follows every completed nonterminal epoch") rules.Add(".pollsAfterCancelableEpoch");
            else if (rule == "noncancelable tail-call execution reports one exact terminal chain count") rules.Add(".noncancelableTerminalChain");
            else if (rule == "successor PC is <= code length") rules.Add(".successorBounded");
            else if (rule == "adapter-supplied route and PC evidence has one aligned witness per reported completed opcode with cardinality/byte checks") rules.Add(".routeCardinality");
            else if (rule == "route and PC evidence does not prove per-op successor/control transitions") rules.Add(".routeTracePremise");
            else if (rule == "child suspension enters only a fresh frame") rules.Add(".childFreshEntry");
            else if (rule == "LIFO parent push/resume") rules.Add(".lifoResume");
            else throw new ExtractionException("Unknown Stage F loop fuel rule " + rule + ".");
        return "[" + string.Join(", ", rules.Distinct(StringComparer.Ordinal)) + "]";
    }

    private static string LeanOption(string? value) => value is null ? "none" : "some \"" + Escape(value) + "\"";

    private static string LeanDispatchTable(string value) => value switch
    {
        "NoTrace" => "noTrace",
        "NoTraceCancelable" => "noTraceCancelable",
        "Traced" => "traced",
        "TracedCancelable" => "tracedCancelable",
        _ => throw new ExtractionException("Unknown Stage F dispatch table " + value + "."),
    };

    private static string LeanRouteKind(string value) => value switch
    {
        "enabled" => "enabled",
        "disabled" => "disabled",
        "badInstruction" => "badInstruction",
        _ => throw new ExtractionException("Unknown Stage F opcode route kind " + value + "."),
    };

    private static bool ContainsProofDeclaration(string source) => source.Split('\n').Any(static line =>
        line.TrimStart().StartsWith("theorem ", StringComparison.Ordinal) ||
        line.TrimStart().StartsWith("lemma ", StringComparison.Ordinal) ||
        line.TrimStart().StartsWith("axiom ", StringComparison.Ordinal));

    private static bool ContainsStandaloneAdmission(string source) => source.Split('\n').Any(static line =>
        line.TrimStart().StartsWith("admit ", StringComparison.Ordinal) ||
        line.TrimStart().StartsWith("admit\t", StringComparison.Ordinal) ||
        line.TrimStart().Equals("admit", StringComparison.Ordinal));

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal);

    private const string Template = """
-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- Generated Stage F source-derived operational kernel.
-- Source/member/compiler closure and accepted dependency artifacts are checked
-- before this file is emitted. The lists below are evidence, not proofs.
-- Every production effect is an explicit Option adapter; source predicates and
-- effect sequences are interpreted before a callback is invoked.  None is
-- silently replaced by a caller boolean, byte pin, or oracle name.
-- Canonical operational IR SHA-256: {{IR_SHA256}}

import EvmFrameDriverExtractor.Specification.OperationalTypes

namespace Eip803x.Evm.FrameDriver.Operational.Generated

open Eip803x.Evm.FrameMachineState
open Eip803x.Evm.FrameDriver.Operational

def stage : String := "stage-f-source-derived-finite-fuel-operational-refinement"
def kernel : String := "standard-mainnet-amsterdam-evm-frame-driver-operational-stage-f"
def irSha256 : String := "{{IR_SHA256}}"
def sourceMembers : List String := [
{{SOURCE_MEMBERS}}]
def sourceOperations : List String := [
{{SOURCE_OPERATIONS}}]
def sourceControlDigests : List String := [
{{SOURCE_CONTROL_DIGESTS}}]
def admittedControlPlan : List OperationalControlPlanStep := [
{{CONTROL_PLAN}}]
def admittedControlNodes : List OperationalControlNode := [
{{TOPOLOGY}}]
def admittedBranchBindings : List OperationalBranchBinding := [
{{BRANCH_BINDINGS}}]
def sourceOpcodeRoutes : List OpcodeRoute := [
{{OPCODE_ROUTES}}]
def sourcePrecompileRoutes : List PrecompileRoute := [
{{PRECOMPILE_ROUTES}}]
def acceptedDependencyTheorems : List String := [
{{DEPENDENCY_THEOREMS}}]
def adapterNames : List String := [
{{ADAPTER_NAMES}}]
def mutationVectors : List String := [
{{MUTATION_VECTORS}}]

def loopBatchLimit : Nat := {{LOOP_BATCH_LIMIT}}
def loopDispatchModes : List DispatchTable := {{LOOP_DISPATCH_MODES}}
inductive OperationalLoopRule where
  | freshClearsReturnData
  | preparesFreshOrContinuation
  | decrementsFuel
  | cancelableEpoch1024
  | pollsAfterCancelableEpoch
  | noncancelableTerminalChain
  | successorBounded
  | routeCardinality
  | routeTracePremise
  | childFreshEntry
  | lifoResume
  deriving DecidableEq, Repr
def loopRules : List OperationalLoopRule := {{LOOP_RULES}}
def loopEntryTransitions : List String := {{LOOP_ENTRY_TRANSITIONS}}
def loopBytecodeOutcomes : List String := {{LOOP_BYTECODE_OUTCOMES}}
def loopSettlementRoutes : List String := {{LOOP_SETTLEMENT_ROUTES}}
def loopStateEffects : List String := {{LOOP_STATE_EFFECTS}}
def loopCleanupRoutes : List String := {{LOOP_CLEANUP_ROUTES}}
def loopFuelRules : List String := {{LOOP_FUEL_RULES}}
def loopMeasure : String := "{{LOOP_MEASURE}}"
def loopAdequacyStatus : String := "{{LOOP_ADEQUACY_STATUS}}"
def dispatchBatchLimit : Nat := loopBatchLimit
def dispatchModes : List DispatchTable := loopDispatchModes

@[simp] def loopRulePresent (rule : OperationalLoopRule) : Bool :=
  loopRules.any (fun candidate => decide (candidate = rule))

@[simp] def hasString (needle : String) (values : List String) : Bool :=
  values.any (fun value => decide (value = needle))

@[simp] def topologyNodePresent (member nodeId kind arm sourceSha256 : String) : Bool :=
  admittedControlNodes.any (fun node => decide (
    node.member = member ∧ node.id = nodeId ∧ node.kind = kind ∧
      node.arm = arm ∧ node.sha256 = sourceSha256))

@[simp] def branchBindingPresent (stage : ControlPlanStage) (branch : String) : Bool :=
  admittedBranchBindings.any (fun binding =>
    decide (binding.branch = branch) && binding.stages.any (fun admittedStage =>
      decide (admittedStage = stage)) &&
      topologyNodePresent binding.member binding.nodeId binding.kind binding.sourceArm binding.sourceSha256)

@[simp] def branchBindingStageActionPresent (binding : OperationalBranchBinding)
    (stage : ControlPlanStage) (action : OperationalControlAction) : Bool :=
  (binding.stages.zip binding.actions).any (fun pair =>
    decide (pair.1 = stage ∧ pair.2 = action))

@[simp] def dispatchModePresent (table : DispatchTable) : Bool :=
  loopDispatchModes.any (fun mode => decide (mode = table))

/- The stage controls below are lowered from the typed IR rather than
   reconstructed from a fixed template.  Each expected step contains its
   source member, node id, arm, source hash, and typed action set; the required
   branch list is also emitted from the stage sets on the admitted branch
   bindings.  A tampered or incomplete plan therefore changes the transition
   gate, even when the route tables are otherwise unchanged. -/
@[simp] def controlPlanStepPresent (expected : OperationalControlPlanStep) : Bool :=
  admittedControlPlan.any (fun step => decide (step = expected)) &&
    topologyNodePresent expected.member expected.nodeId expected.kind expected.sourceArm
      expected.sourceSha256

@[simp] def controlPlanActionsValid (step : OperationalControlPlanStep) : Bool :=
  step.actions.length > 0 &&
    step.actions.all (fun action =>
      admittedBranchBindings.any (fun binding =>
        branchBindingStageActionPresent binding step.stage action))

@[simp] def stageBranchesPresent (stage : ControlPlanStage) (branches : List String) : Bool :=
  decide (branches.length > 0) && branches.all (branchBindingPresent stage)

/- The source-selected stage node is an admission discriminator.  Executable
   branch selection is performed by the typed instruction interpreter below;
   this generic shape check prevents an unbound or malformed source node from
   being relabeled as a stage while preserving the same instruction stream. -/
@[simp] def planStepShapeValid (step : OperationalControlPlanStep) : Bool :=
  decide (step.member ≠ "" ∧ step.nodeId ≠ "" ∧ step.kind ≠ "" ∧
    step.sourceArm ≠ "" ∧ step.sourceSha256.length = 64) &&
    topologyNodePresent step.member step.nodeId step.kind step.sourceArm step.sourceSha256 &&
    controlPlanActionsValid step

@[simp] def stageControlReady (expected : OperationalControlPlanStep)
    (requiredBranches : List String) : Bool :=
  controlPlanStepPresent expected && stageBranchesPresent expected.stage requiredBranches &&
    planStepShapeValid expected

def prepareControlPlanStep : OperationalControlPlanStep :=
  {{PREPARE_PLAN_STEP}}
def dispatchControlPlanStep : OperationalControlPlanStep :=
  {{DISPATCH_PLAN_STEP}}
def precompileRequiredBranches : List String := {{PRECOMPILE_BRANCHES}}
def classifyControlPlanStep : OperationalControlPlanStep :=
  {{CLASSIFY_PLAN_STEP}}
def settleControlPlanStep : OperationalControlPlanStep :=
  {{SETTLE_PLAN_STEP}}
def cleanupControlPlanStep : OperationalControlPlanStep :=
  {{CLEANUP_PLAN_STEP}}
def prepareRequiredBranches : List String := {{PREPARE_BRANCHES}}
def dispatchRequiredBranches : List String := {{DISPATCH_BRANCHES}}
def classifyRequiredBranches : List String := {{CLASSIFY_BRANCHES}}
def settleRequiredBranches : List String := {{SETTLE_BRANCHES}}
def cleanupRequiredBranches : List String := {{CLEANUP_BRANCHES}}
def prepareControlInstructions : List OperationalControlInstruction := {{PREPARE_INSTRUCTIONS}}
def dispatchControlInstructions : List OperationalControlInstruction := {{DISPATCH_INSTRUCTIONS}}
def precompileControlInstructions : List OperationalControlInstruction := {{PRECOMPILE_INSTRUCTIONS}}
def classifyControlInstructions : List OperationalControlInstruction := {{CLASSIFY_INSTRUCTIONS}}
def settleControlInstructions : List OperationalControlInstruction := {{SETTLE_INSTRUCTIONS}}
def cleanupControlInstructions : List OperationalControlInstruction := {{CLEANUP_INSTRUCTIONS}}

/- The control program is reconstructed by interpreting the typed IR list in
   source order.  The instruction lists below are executable lowering of the
   source branch predicates, effects, settlement labels, and stage sets; they
   are not caller-provided route booleans. -/
structure OperationalControlProgram where
  prepare : OperationalControlPlanStep
  dispatch : OperationalControlPlanStep
  classify : OperationalControlPlanStep
  settle : OperationalControlPlanStep
  cleanup : OperationalControlPlanStep
  prepareBranches : List String
  dispatchBranches : List String
  precompileBranches : List String
  classifyBranches : List String
  settleBranches : List String
  cleanupBranches : List String
  prepareInstructions : List OperationalControlInstruction
  dispatchInstructions : List OperationalControlInstruction
  precompileInstructions : List OperationalControlInstruction
  classifyInstructions : List OperationalControlInstruction
  settleInstructions : List OperationalControlInstruction
  cleanupInstructions : List OperationalControlInstruction
  instructions : List OperationalControlInstruction

def controlProgram : Option OperationalControlProgram :=
  match admittedControlPlan with
  | prepare :: dispatch :: classify :: settle :: cleanup :: [] =>
      some { prepare := prepare, dispatch := dispatch, classify := classify,
        settle := settle, cleanup := cleanup,
        prepareBranches := prepareRequiredBranches, dispatchBranches := dispatchRequiredBranches,
        precompileBranches := precompileRequiredBranches, classifyBranches := classifyRequiredBranches,
        settleBranches := settleRequiredBranches, cleanupBranches := cleanupRequiredBranches,
        prepareInstructions := prepareControlInstructions,
        dispatchInstructions := dispatchControlInstructions,
        precompileInstructions := precompileControlInstructions,
        classifyInstructions := classifyControlInstructions,
        settleInstructions := settleControlInstructions,
        cleanupInstructions := cleanupControlInstructions,
        instructions := prepareControlInstructions ++ dispatchControlInstructions ++
          precompileControlInstructions ++ classifyControlInstructions ++
          settleControlInstructions ++ cleanupControlInstructions }
  | _ => none

def expectedControlInstructionStages : List ControlPlanStage :=
  prepareRequiredBranches.map (fun _ => .prepare) ++
    dispatchRequiredBranches.map (fun _ => .dispatch) ++
    precompileRequiredBranches.map (fun _ => .dispatch) ++
    classifyRequiredBranches.map (fun _ => .classify) ++
    settleRequiredBranches.map (fun _ => .settle) ++
    cleanupRequiredBranches.map (fun _ => .cleanup)

@[simp] def controlInstructionBindingPresent (instruction : OperationalControlInstruction) : Bool :=
  admittedBranchBindings.any (fun binding => decide (
    binding.branch = instruction.branch ∧ binding.member = instruction.member ∧
      binding.nodeId = instruction.nodeId ∧ binding.kind = instruction.kind ∧
      binding.sourceArm = instruction.sourceArm ∧ binding.bindingArm = instruction.bindingArm ∧
      binding.sourceSha256 = instruction.sourceSha256 ∧ binding.predicate = instruction.predicate ∧
      binding.effects = instruction.effects ∧ binding.settlement = instruction.settlement) &&
    branchBindingStageActionPresent binding instruction.stage instruction.action)

/- An action-only IR mutation must not be able to retag an otherwise valid
   predicate/settlement pair.  This is the typed AST well-formedness relation;
   the source bindings carry the action, while this relation keeps the
   executable transition fail-closed when an action is changed in isolation. -/
def controlActionShapeValid : ControlPlanStage → OperationalControlPredicate →
    OperationalControlSettlement → OperationalControlAction → Bool
  | .prepare, .freshFrame, .preparation, .prepareFresh => true
  | .prepare, .continuationFrame, .preparation, .prepareContinuation => true
  | .dispatch, .bytecodeFrame, .invocation, .dispatchBytecode => true
  | .dispatch, .fullPrecompileFrame, .invocation, .dispatchFullPrecompile => true
  | .dispatch, .precompileOutOfGasNested, .fullPrecompileOutOfGasNested, .precompileFailure => true
  | .dispatch, .precompileOutOfGasTop, .fullPrecompileOutOfGasTop, .precompileFailure => true
  | .dispatch, .precompileReturnedFailure, .fullPrecompileReturnedFailure, .precompileFailure => true
  | .dispatch, .precompileManagedException, .fullPrecompileManagedException, .precompileFailure => true
  | .classify, .bytecodeContinue, .continued, .classifyContinue => true
  | .classify, .bytecodeSuspend, .suspended, .classifySuspend => true
  | .classify, .nestedRegularSuccess, .childSuccess, .classifyHalt => true
  | .classify, .nestedCreateSuccess, .childCreateSuccess, .classifyHalt => true
  | .classify, .nestedRevert, .childRevert, .classifyHalt => true
  | .classify, .nestedException, .childException, .classifyHalt => true
  | .classify, .topLevelSuccess, .topLevelSuccess, .classifyHalt => true
  | .classify, .topLevelRevert, .topLevelRevert, .classifyHalt => true
  | .classify, .topLevelException, .topLevelException, .classifyHalt => true
  | .settle, .nestedRegularSuccess, .childSuccess, .settleNestedRegularSuccess => true
  | .settle, .nestedCreateSuccess, .childCreateSuccess, .settleNestedCreateSuccess => true
  | .settle, .createDepositInvalidCode, .childCreateInvalidCode, .settleNestedCreateInvalidCode => true
  | .settle, .createDepositOutOfGas, .childCreateOutOfGas, .settleNestedCreateOutOfGas => true
  | .settle, .nestedRevert, .childRevert, .settleNestedRevert => true
  | .settle, .nestedException, .childException, .settleNestedException => true
  | .settle, .resumeParent, .continued, .settleResume => true
  | .settle, .topLevelSuccess, .topLevelSuccess, .settleTopLevelSuccess => true
  | .settle, .topLevelRevert, .topLevelRevert, .settleTopLevelRevert => true
  | .settle, .topLevelException, .topLevelException, .settleTopLevelException => true
  | .cleanup, .cancelled, .cancelled, .cleanupCancelled => true
  | .cleanup, .escaped, .escaped, .cleanupEscaped => true
  | .cleanup, .invalidControl, .invalidControl, .cleanupInvalidControl => true
  | .cleanup, .completed, .completed, .cleanupCompleted => true
  | _, _, _, _ => false

def sourceBranchActionsValid : Prop :=
  admittedBranchBindings.all (fun binding =>
    (binding.stages.zip binding.actions).all (fun pair =>
      controlActionShapeValid pair.1 binding.predicate binding.settlement pair.2)) = true

@[simp] def controlInstructionValid (instruction : OperationalControlInstruction) : Bool :=
  controlInstructionBindingPresent instruction &&
    decide (instruction.effects.length > 0) &&
    controlActionShapeValid instruction.stage instruction.predicate instruction.settlement instruction.action

def executeControlInstructions : List ControlPlanStage →
    List OperationalControlInstruction → Bool
  | [], [] => true
  | expectedStage :: expectedStages, instruction :: instructions =>
      if decide (instruction.stage = expectedStage) && controlInstructionValid instruction then
        executeControlInstructions expectedStages instructions
      else
        false
  | _, _ => false

def controlProgramExecution (program : OperationalControlProgram) :
    Bool :=
  executeControlInstructions expectedControlInstructionStages program.instructions

def nestedResult (machine : Machine) (result : FrameResult) : Bool :=
  decide (machine.parents ≠ []) && !result.frame.isTopLevel

def nestedRegularResult (machine : Machine) (result : FrameResult) : Bool :=
  nestedResult machine result && !machine.current.executionType.isCreate && decide (result.exit = .success)

def nestedCreateResult (machine : Machine) (result : FrameResult) : Bool :=
  nestedResult machine result && machine.current.executionType.isCreate && decide (result.exit = .success)

def topLevelResult (machine : Machine) (result : FrameResult) : Bool :=
  decide (machine.parents = []) && result.frame.isTopLevel

def controlPredicateMatches : OperationalControlPredicate →
    OperationalControlObservation → Bool
  | .freshFrame, .machine machine => decide (machine.current.phase = .fresh)
  | .continuationFrame, .machine machine => decide (machine.current.phase = .continuation)
  | .bytecodeFrame, .machine machine => decide (machine.current.kind = .bytecode)
  | .fullPrecompileFrame, .machine machine =>
      match machine.current.kind with
      | .precompile _ => true
      | .bytecode => false
  | .bytecodeContinue, .machineStep step =>
      match step.result with
      | .continue _ => true
      | _ => false
  | .bytecodeSuspend, .machineStep step =>
      match step.result with
      | .suspend _ _ => true
      | _ => false
  | .nestedRegularSuccess, .machineStep step =>
      match step.result with
      | .halt result => nestedRegularResult step.machine result
      | _ => false
  | .nestedCreateSuccess, .machineStep step =>
      match step.result with
      | .halt result => nestedCreateResult step.machine result
      | _ => false
  | .nestedRevert, .machineStep step =>
      match step.result with
      | .halt result => nestedResult step.machine result && decide (result.exit = .revert)
      | _ => false
  | .nestedException, .machineStep step =>
      match step.result with
      | .halt result =>
          nestedResult step.machine result &&
            match result.exit with
            | .exception _ => true
            | _ => false
      | _ => false
  | .nestedRegularSuccess, .frameResult machine result => nestedRegularResult machine result
  | .nestedCreateSuccess, .frameResult machine result => nestedCreateResult machine result
  | .nestedCreateSuccess, .createDeposit _ execution => decide (execution.outcome = .deposited)
  | .nestedRevert, .frameResult machine result => nestedResult machine result && decide (result.exit = .revert)
  | .nestedException, .frameResult machine result =>
      nestedResult machine result &&
        match result.exit with
        | .exception _ => true
        | _ => false
  | .resumeParent, .resume _ => true
  | .createDepositInvalidCode, .createDeposit _ execution => decide (execution.outcome = .invalidCode)
  | .createDepositOutOfGas, .createDeposit _ execution => decide (execution.outcome = .outOfGas)
  | .topLevelSuccess, .machineStep step =>
      match step.result with | .halt result => topLevelResult step.machine result && decide (result.exit = .success) | _ => false
  | .topLevelRevert, .machineStep step =>
      match step.result with | .halt result => topLevelResult step.machine result && decide (result.exit = .revert) | _ => false
  | .topLevelException, .machineStep step =>
      match step.result with
      | .halt result =>
          topLevelResult step.machine result &&
            match result.exit with
            | .exception _ => true
            | _ => false
      | _ => false
  | .topLevelSuccess, .frameResult machine result => topLevelResult machine result && decide (result.exit = .success)
  | .topLevelRevert, .frameResult machine result => topLevelResult machine result && decide (result.exit = .revert)
  | .topLevelException, .frameResult machine result =>
      topLevelResult machine result &&
        match result.exit with
        | .exception _ => true
        | _ => false
  | .precompileOutOfGasNested, .precompile machine (.outOfGas _) => decide (machine.parents ≠ [])
  | .precompileOutOfGasTop, .precompile machine (.outOfGas _) => decide (machine.parents = [])
  | .precompileReturnedFailure, .precompile _ (.returnedFailure _ _) => true
  | .precompileManagedException, .precompile _ (.managedException _ _) => true
  | .cancelled, .cancelled _ => true
  | .escaped, .escaped _ => true
  | .invalidControl, .invalidControl _ => true
  | .completed, .frameResult _ _ => true
  | _, _ => false

def controlEffectAdmissible : OperationalControlEffect →
    OperationalControlObservation → Bool
  | .clearReturnData, .machine machine => decide (machine.current.phase = .fresh)
  | .prepareFresh, .machine machine => decide (machine.current.phase = .fresh)
  | .retainReturnData, .machine machine => decide (machine.current.phase = .continuation)
  | .prepareContinuation, .machine machine => decide (machine.current.phase = .continuation)
  | .runBytecode, .machine machine => decide (machine.current.kind = .bytecode)
  | .runDispatchLoop, .machine machine => decide (machine.current.kind = .bytecode)
  | .runFullPrecompile, .machine machine =>
      match machine.current.kind with
      | .precompile _ => true
      | _ => false
  | .executePrecompile, .machine machine =>
      match machine.current.kind with
      | .precompile _ => true
      | _ => false
  | .retainCurrentFrame, .machineStep step =>
      match step.result with
      | .continue _ => true
      | _ => false
  | .popParent, .machineStep step =>
      match step.result with
      | .halt result => nestedResult step.machine result
      | _ => false
  | .mergeChild, .machineStep step =>
      match step.result with
      | .halt result => nestedResult step.machine result
      | _ => false
  | .repayStateGasSpill, .machineStep step =>
      match step.result with
      | .halt result => nestedRegularResult step.machine result
      | _ => false
  | .prepareChildFrame, .machineStep step =>
      match step.result with
      | .suspend _ child => decide (child.phase = .fresh)
      | _ => false
  | .retainParent, .machineStep step =>
      match step.result with
      | .suspend _ _ => true
      | _ => false
  | .popParent, .frameResult machine result => nestedResult machine result
  | .mergeChild, .frameResult machine result => nestedResult machine result
  | .repayStateGasSpill, .frameResult machine result => nestedRegularResult machine result
  | .prepareCreateData, .machineStep step =>
      match step.result with
      | .halt result => nestedCreateResult step.machine result
      | _ => false
  | .prepareCreateData, .createDeposit _ execution => decide (execution.outcome = .deposited)
  | .handleCreate, .machineStep step =>
      match step.result with
      | .halt result => nestedCreateResult step.machine result
      | _ => false
  | .handleCreate, .createDeposit _ execution => decide (execution.outcome = .deposited)
  | .restoreWorld, .createDeposit _ execution => decide (execution.outcome ≠ .deposited)
  | .restoreWorld, .frameResult machine result => nestedResult machine result && decide (result.exit ≠ .success)
  | .creditParent, .createDeposit _ execution => decide (execution.outcome ≠ .deposited)
  | .burnDepositGas, .createDeposit _ execution => decide (execution.outcome = .outOfGas)
  | .restoreSnapshot, .frameResult machine result => nestedResult machine result && decide (result.exit = .revert)
  | .restoreSnapshot, .machineStep step =>
      match step.result with
      | .halt result => nestedResult step.machine result && decide (result.exit = .revert)
      | _ => false
  | .restoreChildGas, .frameResult machine result => nestedResult machine result && decide (result.exit = .revert)
  | .restoreChildGas, .machineStep step =>
      match step.result with
      | .halt result => nestedResult step.machine result && decide (result.exit = .revert)
      | _ => false
  | .handleRevert, .frameResult machine result => nestedResult machine result && decide (result.exit = .revert)
  | .handleRevert, .machineStep step =>
      match step.result with
      | .halt result => nestedResult step.machine result && decide (result.exit = .revert)
      | _ => false
  | .restoreFailureControl, .frameResult machine result => nestedResult machine result &&
      match result.exit with
      | .exception _ => true
      | _ => false
  | .restoreFailureControl, .machineStep step =>
      match step.result with
      | .halt result => nestedResult step.machine result &&
          match result.exit with
          | .exception _ => true
          | _ => false
      | _ => false
  | .resumeParent, .frameResult machine result => nestedResult machine result &&
      match result.exit with
      | .exception _ => true
      | _ => false
  | .resumeParent, .machineStep step =>
      match step.result with
      | .halt result => nestedResult step.machine result &&
          match result.exit with
          | .exception _ => true
          | _ => false
      | _ => false
  | .resumeParent, .resume _ => true
  | .prepareTopLevelSubstate, .frameResult machine result => topLevelResult machine result
  | .prepareTopLevelSubstate, .machineStep step =>
      match step.result with
      | .halt result => topLevelResult step.machine result
      | _ => false
  | .refundRevertedStateGas, .frameResult machine result => topLevelResult machine result && decide (result.exit = .revert)
  | .refundRevertedStateGas, .machineStep step =>
      match step.result with
      | .halt result => topLevelResult step.machine result && decide (result.exit = .revert)
      | _ => false
  | .handleExceptionOrFailure, .frameResult machine result => topLevelResult machine result &&
      match result.exit with
      | .exception _ => true
      | _ => false
  | .handleExceptionOrFailure, .machineStep step =>
      match step.result with
      | .halt result => topLevelResult step.machine result &&
          match result.exit with
          | .exception _ => true
          | _ => false
      | _ => false
  | .failureSettlement, .precompile _ (.outOfGas _ | .returnedFailure _ _ | .managedException _ _) => true
  | .dispose, .cancelled _ | .dispose, .escaped _ | .dispose, .invalidControl _ |
      .dispose, .frameResult _ _ => true
  | .failClosed, .invalidControl _ => true
  | .disposeActiveFrames, .cancelled _ | .disposeActiveFrames, .escaped _ |
      .disposeActiveFrames, .invalidControl _ | .disposeActiveFrames, .frameResult _ _ => true
  | _, _ => false

def controlEffectsAdmissible (effects : List OperationalControlEffect)
    (observation : OperationalControlObservation) : Bool :=
  effects.all (fun effect => controlEffectAdmissible effect observation)

def controlSettlementMatches : OperationalControlSettlement →
    OperationalControlObservation → Bool
  | .preparation, .machine _ | .invocation, .machine _ => true
  | .continued, .machineStep step =>
      match step.result with
      | .continue _ => true
      | _ => false
  | .continued, .resume _ => true
  | .suspended, .machineStep step =>
      match step.result with
      | .suspend _ _ => true
      | _ => false
  | .childSuccess, .machineStep step =>
      match step.result with
      | .halt result => nestedRegularResult step.machine result
      | _ => false
  | .childCreateSuccess, .machineStep step =>
      match step.result with
      | .halt result => nestedCreateResult step.machine result
      | _ => false
  | .childRevert, .machineStep step =>
      match step.result with
      | .halt result => nestedResult step.machine result && decide (result.exit = .revert)
      | _ => false
  | .childException, .machineStep step =>
      match step.result with
      | .halt result =>
          nestedResult step.machine result &&
            match result.exit with
            | .exception _ => true
            | _ => false
      | _ => false
  | .childSuccess, .frameResult machine result => nestedRegularResult machine result
  | .childCreateSuccess, .frameResult machine result => nestedCreateResult machine result
  | .childCreateSuccess, .createDeposit _ execution => decide (execution.outcome = .deposited)
  | .childRevert, .frameResult machine result => nestedResult machine result && decide (result.exit = .revert)
  | .childException, .frameResult machine result =>
      nestedResult machine result &&
        match result.exit with
        | .exception _ => true
        | _ => false
  | .childCreateInvalidCode, .createDeposit _ execution => decide (execution.outcome = .invalidCode)
  | .childCreateOutOfGas, .createDeposit _ execution => decide (execution.outcome = .outOfGas)
  | .topLevelSuccess, .machineStep step =>
      match step.result with
      | .halt result => topLevelResult step.machine result && decide (result.exit = .success)
      | _ => false
  | .topLevelRevert, .machineStep step =>
      match step.result with
      | .halt result => topLevelResult step.machine result && decide (result.exit = .revert)
      | _ => false
  | .topLevelException, .machineStep step =>
      match step.result with
      | .halt result =>
          topLevelResult step.machine result &&
            match result.exit with
            | .exception _ => true
            | _ => false
      | _ => false
  | .topLevelSuccess, .frameResult machine result => topLevelResult machine result && decide (result.exit = .success)
  | .topLevelRevert, .frameResult machine result => topLevelResult machine result && decide (result.exit = .revert)
  | .topLevelException, .frameResult machine result =>
      topLevelResult machine result &&
        match result.exit with
        | .exception _ => true
        | _ => false
  | .fullPrecompileOutOfGasNested, .precompile machine (.outOfGas _) => decide (machine.parents ≠ [])
  | .fullPrecompileOutOfGasTop, .precompile machine (.outOfGas _) => decide (machine.parents = [])
  | .fullPrecompileReturnedFailure, .precompile _ (.returnedFailure _ _) => true
  | .fullPrecompileManagedException, .precompile _ (.managedException _ _) => true
  | .cancelled, .cancelled _ | .escaped, .escaped _ | .invalidControl, .invalidControl _ => true
  | .completed, .frameResult _ _ => true
  | _, _ => false

def selectControlInstruction (stage : ControlPlanStage)
    (observation : OperationalControlObservation) : List OperationalControlInstruction →
      Option OperationalControlAction
  | [] => none
  | instruction :: instructions =>
      if decide (instruction.stage = stage) && controlInstructionValid instruction &&
          controlPredicateMatches instruction.predicate observation &&
          controlEffectsAdmissible instruction.effects observation &&
          controlSettlementMatches instruction.settlement observation then
        some instruction.action
      else
        selectControlInstruction stage observation instructions

def controlProgramDecision (stage : ControlPlanStage)
    (observation : OperationalControlObservation) : Option OperationalControlAction :=
  match controlProgram with
  | some program =>
      let instructions := match stage with
        | .prepare => program.prepareInstructions
        | .dispatch => program.dispatchInstructions ++ program.precompileInstructions
        | .classify => program.classifyInstructions
        | .settle => program.settleInstructions
        | .cleanup => program.cleanupInstructions
      selectControlInstruction stage observation instructions
  | none => none

@[simp] def prepareControlReady : Bool :=
  match controlProgram with
  | some program => stageControlReady program.prepare program.prepareBranches &&
      controlProgramExecution program
  | none => false
@[simp] def dispatchControlReady : Bool :=
  match controlProgram with
  | some program => stageControlReady program.dispatch program.dispatchBranches &&
      controlProgramExecution program
  | none => false
@[simp] def precompileControlReady : Bool :=
  match controlProgram with
  | some program => stageBranchesPresent .dispatch program.precompileBranches && controlProgramExecution program
  | none => false
@[simp] def classifyControlReady : Bool :=
  match controlProgram with
  | some program => stageControlReady program.classify program.classifyBranches &&
      controlProgramExecution program
  | none => false
@[simp] def settleControlReady : Bool :=
  match controlProgram with
  | some program => stageControlReady program.settle program.settleBranches &&
      controlProgramExecution program
  | none => false
@[simp] def cleanupControlReady : Bool :=
  match controlProgram with
  | some program => stageControlReady program.cleanup program.cleanupBranches &&
      controlProgramExecution program
  | none => false

@[simp] def controlProgramReady : Bool :=
  match controlProgram with
  | some program =>
      controlProgramExecution program && prepareControlReady && dispatchControlReady &&
        precompileControlReady && classifyControlReady && settleControlReady && cleanupControlReady
  | none => false

@[simp] def loopControlValid : Bool :=
    decide (loopBatchLimit = 1024) && decide (loopDispatchModes.length = 4) &&
    decide (loopDispatchModes = [.noTrace, .noTraceCancelable, .traced, .tracedCancelable]) &&
    loopRulePresent .freshClearsReturnData && loopRulePresent .preparesFreshOrContinuation &&
    loopRulePresent .decrementsFuel && loopRulePresent .cancelableEpoch1024 &&
    loopRulePresent .pollsAfterCancelableEpoch && loopRulePresent .noncancelableTerminalChain &&
     loopRulePresent .successorBounded && loopRulePresent .routeCardinality &&
     loopRulePresent .routeTracePremise && loopRulePresent .childFreshEntry &&
     loopRulePresent .lifoResume &&
     hasString "a cancelable poll uses cumulative 1024/2048+ counts" loopFuelRules &&
    hasString "None" loopBytecodeOutcomes &&
    hasString "Suspend child" loopBytecodeOutcomes &&
    hasString "Stop/Revert" loopBytecodeOutcomes &&
    hasString "EVM exception" loopBytecodeOutcomes &&
    hasString "Overflow" loopBytecodeOutcomes &&
    hasString "OperationCanceledException" loopBytecodeOutcomes &&
    hasString "escaped" loopBytecodeOutcomes &&
    hasString "success regular" loopSettlementRoutes &&
    hasString "success CREATE deposit" loopSettlementRoutes &&
    hasString "CREATE collision delegated to accepted CALL/CREATE routing" loopSettlementRoutes &&
    hasString "CREATE deposit invalid code" loopSettlementRoutes &&
    hasString "CREATE deposit OOG" loopSettlementRoutes &&
    hasString "revert" loopSettlementRoutes &&
    hasString "exception" loopSettlementRoutes &&
    hasString "top-level success/revert/exception" loopSettlementRoutes &&
    hasString "precompile out-of-gas/returned-failure/managed-exception" loopSettlementRoutes &&
    hasString "refund child gas" loopStateEffects &&
    hasString "state reservoir" loopStateEffects &&
    hasString "state gas used" loopStateEffects &&
    hasString "state gas spill" loopStateEffects &&
    hasString "spill refund" loopStateEffects &&
    hasString "refund merge/rollback" loopStateEffects &&
    hasString "world snapshot" loopStateEffects &&
    hasString "access/log/destroy" loopStateEffects &&
    hasString "RIPEMD latch" loopStateEffects &&
    hasString "return data and bounded parent output copy" loopStateEffects &&
    hasString "trace/substate/status" loopStateEffects &&
    hasString "cancelled cleanup" loopCleanupRoutes &&
    hasString "escaped cleanup" loopCleanupRoutes &&
    hasString "exception cleanup" loopCleanupRoutes &&
    hasString "DisposeActiveFrames" loopCleanupRoutes &&
    hasString "fuel exhausted" loopCleanupRoutes &&
    decide (loopMeasure = "gasLeft + remainingCode + parentStackDepth") &&
    decide (loopAdequacyStatus =
      "blocked: no adequate-fuel proof for all admitted production transitions")

def sourcePlanValid : Prop :=
  admittedControlPlan.length = 5 ∧
    admittedControlPlan.map (fun step => step.stage) =
      [ControlPlanStage.prepare, ControlPlanStage.dispatch, ControlPlanStage.classify,
        ControlPlanStage.settle, ControlPlanStage.cleanup] ∧
    admittedControlPlan.all (fun step =>
      decide (step.member ≠ "" ∧ step.nodeId ≠ "" ∧ step.kind ≠ "" ∧
        step.sourceArm ≠ "" ∧ step.sourceSha256.length = 64) &&
      admittedControlNodes.any (fun node => decide (
        node.member = step.member ∧ node.id = step.nodeId ∧ node.kind = step.kind ∧
        node.arm = step.sourceArm ∧ node.sha256 = step.sourceSha256)) &&
      controlPlanActionsValid step) = true

def sourceTopologyKeysUnique : List OperationalControlNode → Bool
  | [] => true
  | node :: nodes =>
      !nodes.any (fun other => decide (other.member = node.member ∧ other.id = node.id)) &&
        sourceTopologyKeysUnique nodes

def sourceTopologyValid : Prop :=
  admittedControlNodes.length > 0 ∧
    admittedControlNodes.all (fun node => decide (
      node.id ≠ "" ∧ node.member ≠ "" ∧ node.kind ≠ "" ∧ node.parentId ≠ "" ∧
        node.arm ≠ "" ∧ node.sha256.length = 64)) = true ∧
    sourceTopologyKeysUnique admittedControlNodes = true

def sourceBranchesValid : Prop :=
  admittedBranchBindings.length > 0 ∧
    admittedBranchBindings.all (fun binding =>
      decide (binding.branch ≠ "" ∧ binding.bindingArm ≠ "" ∧
        binding.effects.length > 0 ∧ binding.actions.length = binding.stages.length) &&
      admittedControlNodes.any (fun node => decide (
        node.member = binding.member ∧ node.id = binding.nodeId ∧ node.kind = binding.kind ∧
          node.arm = binding.sourceArm ∧ node.sha256 = binding.sourceSha256))) = true

def sourceOpcodeTableComplete (table : DispatchTable) : Bool :=
  (List.range 256).all (fun byte => sourceOpcodeRoutes.any (fun route => decide (
    route.dispatchTable = table ∧ route.byte = byte)))

def sourceOpcodeRoutesValid : Prop :=
  sourceOpcodeRoutes.length = 1024 ∧
    DispatchTable.all.all sourceOpcodeTableComplete = true

def sourcePrecompileAddressesUnique : List PrecompileRoute → Bool
  | [] => true
  | route :: routes =>
      !routes.any (fun other => decide (other.address = route.address)) &&
        sourcePrecompileAddressesUnique routes

def sourcePrecompileRoutesValid : Prop :=
  sourcePrecompileRoutes.length = 18 ∧
    sourcePrecompileAddressesUnique sourcePrecompileRoutes = true

def sourceEvidenceValid : Prop :=
    sourceMembers.length = 4 ∧
    sourceMembers.all (fun member => decide (member ≠ "")) = true ∧
    sourceOperations.length > 0 ∧ sourceControlDigests.length = 4 ∧
    acceptedDependencyTheorems.length > 0 ∧ adapterNames.length > 0 ∧ mutationVectors.length > 0 ∧
    loopControlValid = true ∧
    controlProgramReady = true ∧
    sourcePlanValid ∧ sourceTopologyValid ∧ sourceBranchesValid ∧
    sourceOpcodeRoutesValid ∧ sourcePrecompileRoutesValid
    ∧ sourceBranchActionsValid

def prepareFrame (semantics : OperationalSemantics) (machine : Machine) : Option Machine :=
  match controlProgramDecision .prepare (.machine machine) with
  | some .prepareFresh =>
      match semantics.clearReturnData machine with
      | none => none
      | some cleared => semantics.prepareFresh cleared
  | some .prepareContinuation => semantics.prepareContinuation machine
  | _ => none

def invokeFrame (semantics : OperationalSemantics) (machine : Machine) : Option Invocation :=
  if dispatchModePresent machine.current.dispatchTable then
    match controlProgramDecision .dispatch (.machine machine) with
    | some .dispatchBytecode =>
        match runBytecode semantics machine with
        | some execution => some (.bytecode execution)
        | none => none
    | some .dispatchFullPrecompile =>
        match semantics.runFullPrecompile machine with
        | some execution => some (.fullPrecompile execution)
        | none => none
    | _ => none
  else
    none

def cleanupFailure (semantics : OperationalSemantics) (machine : Machine) : DriverStep :=
  match controlProgramDecision .cleanup (.invalidControl machine) with
  | some .cleanupInvalidControl | some .cleanupCompleted =>
      match semantics.cleanup machine with
      | some cleaned => .incomplete cleaned .invalidControlRoute
      | none => .incomplete machine (.unresolvedAdapter "FrameCleanupScope.Dispose/DisposeActiveFrames")
  | _ => .incomplete machine (.unresolvedAdapter "StageF/cleanup-control-plan")

def invokeFailure (semantics : OperationalSemantics) (machine : Machine)
    (kind : ExceptionKind) : DriverStep :=
  match semantics.failureResult kind machine with
  | some result =>
      if FrameResultControlValid result then .halt machine result
      else cleanupFailure semantics machine
  | none => cleanupFailure semantics machine

def invokePrecompileFailure (semantics : OperationalSemantics) (machine : Machine)
    (outcome : PrecompileRunOutcome) : DriverStep :=
  match semantics.precompileFailureResult outcome machine with
  | some result =>
      if FrameResultControlValid result then .halt machine result
      else cleanupFailure semantics machine
  | none => cleanupFailure semantics machine

/- The source binding below covers only membership in the extracted opcode
   route tables.  `adapterSuppliedRouteEvidence` checks the shape of
   `routePcs` and per-op route evidence; this kernel
   does not infer a C# CFG or PUSH widths from those witnesses. -/
def sourceBoundBytecodeRoutesValid (machine : Machine) (execution : BytecodeExecution) : Prop :=
  execution.routes.all (fun route => decide (
    route ∈ sourceOpcodeRoutes ∧ route.dispatchTable = machine.current.dispatchTable)) = true

def sourceBoundPrecompileRouteValid (machine : Machine) (execution : PrecompileExecution) : Prop :=
  match machine.current.kind, execution.route with
  | .precompile address, some route => route.address = address ∧ route ∈ sourcePrecompileRoutes
  | _, _ => False

def classifyBytecode (machine : Machine) (step : MachineStep) : DriverStep :=
  match controlProgramDecision .classify (.machineStep step) with
  | some .classifyContinue =>
      match step.result with
      | .continue frame => .continue { step.machine with current := frame }
      | _ => .incomplete machine (.invalidControlRoute)
  | some .classifySuspend =>
      match step.result with
      | .suspend parent child => .suspend step.machine parent child
      | _ => .incomplete machine (.invalidControlRoute)
  | some .classifyHalt =>
      match step.result with
      | .halt result => .halt step.machine result
      | _ => .incomplete machine (.invalidControlRoute)
  | _ => .incomplete machine (.unresolvedAdapter "StageF/classification-control-plan")

def interpretBytecode (semantics : OperationalSemantics) (machine : Machine)
    (execution : BytecodeExecution) : DriverStep :=
  if h : sourceEvidenceValid ∧ bytecodeExecutionValid semantics machine execution ∧
      sourceBoundBytecodeRoutesValid machine execution then
    match execution.outcome with
    | .returned step => classifyBytecode machine step
    | .thrownEvm failureMachine kind => invokeFailure semantics failureMachine kind
    | .thrownOverflow failureMachine => invokeFailure semantics failureMachine .other
    | .escaped escapedMachine reason =>
        match controlProgramDecision .cleanup (.escaped escapedMachine) with
        | some .cleanupEscaped => .escaped escapedMachine reason
        | _ => .incomplete machine (.unresolvedAdapter "StageF/escaped-cleanup-control-plan")
    | .cancelled cancelledMachine _ reason =>
        match controlProgramDecision .cleanup (.cancelled cancelledMachine) with
        | some .cleanupCancelled => .cancelled cancelledMachine reason
        | _ => .incomplete machine (.unresolvedAdapter "StageF/cancelled-cleanup-control-plan")
  else
    .incomplete machine (.unresolvedAdapter "RunDispatchLoop/batch-or-cancellation-boundary")

def interpretPrecompile (semantics : OperationalSemantics) (machine : Machine)
    (execution : PrecompileExecution) : DriverStep :=
  if h : sourceEvidenceValid ∧ precompileControlReady = true ∧
      fullPrecompileExecutionValid semantics machine execution ∧
      sourceBoundPrecompileRouteValid machine execution then
    match execution.outcome with
    | .returned step =>
        match controlProgramDecision .classify (.machineStep step), step.result with
        | some .classifyHalt, .halt result => .halt step.machine result
        | _, _ => .incomplete machine .invalidControlRoute
    | .outOfGas failureMachine | .returnedFailure failureMachine _ |
      .managedException failureMachine _ =>
        match controlProgramDecision .dispatch (.precompile failureMachine execution.outcome) with
        | some .precompileFailure => invokePrecompileFailure semantics failureMachine execution.outcome
        | _ => .incomplete machine (.unresolvedAdapter "StageF/precompile-failure-control-plan")
    | .escaped escapedMachine reason =>
        match controlProgramDecision .cleanup (.escaped escapedMachine) with
        | some .cleanupEscaped => .escaped escapedMachine reason
        | _ => .incomplete machine (.unresolvedAdapter "StageF/escaped-cleanup-control-plan")
  else
    .incomplete machine (.unresolvedAdapter "ExecutePrecompile/full-frame-domain")

def evaluateStep (semantics : OperationalSemantics) (machine : Machine) : DriverStep :=
  if h : sourceEvidenceValid ∧ controlProgramReady = true then
    match prepareFrame semantics machine with
    | none =>
        .incomplete machine (.unresolvedAdapter
          "ExecuteTransaction/fresh-or-continuation-preparation")
    | some prepared =>
        match invokeFrame semantics prepared with
        | none =>
            match prepared.current.kind with
            | .bytecode =>
                .incomplete prepared (.unresolvedAdapter "RunDispatchLoop/dispatch-table-mode")
            | .precompile _ =>
                .incomplete prepared (.unresolvedAdapter "ExecutePrecompile")
        | some (.bytecode execution) => interpretBytecode semantics prepared execution
        | some (.fullPrecompile execution) => interpretPrecompile semantics prepared execution
  else
    .incomplete machine (.unresolvedAdapter "StageF/source-plan-topology-route-evidence")

def nestedSettlementAction (machine : Machine) (result : FrameResult) :
    Option OperationalControlAction :=
  controlProgramDecision .settle (.frameResult machine result)

def createDepositAction (machine : Machine) (execution : CreateDepositExecution) :
    Option OperationalControlAction :=
  controlProgramDecision .settle (.createDeposit machine execution)

def topLevelSettlementAction (machine : Machine) (result : FrameResult) :
    Option OperationalControlAction :=
  controlProgramDecision .settle (.frameResult machine result)

def settleSettlement (semantics : OperationalSemantics) (machine : Machine) : Settlement → DriverStep
  | .complete completed =>
      match topLevelSettlementAction machine completed with
      | some .settleTopLevelSuccess | some .settleTopLevelRevert | some .settleTopLevelException =>
        settleComplete semantics machine completed
      | _ => cleanupFailure semantics machine
  | .resume resumed =>
      match controlProgramDecision .settle (.resume resumed) with
      | some .settleResume => settleResume machine resumed
      | _ => cleanupFailure semantics machine
  | .invalidControl invalid => .incomplete invalid .invalidControlRoute

def settleHalt (semantics : OperationalSemantics) (machine : Machine)
    (result : FrameResult) : DriverStep :=
  if h : FrameResultControlValid result then
    if machine.parents ≠ [] then
      if machine.current.executionType.isCreate && result.exit = .success then
        match semantics.createDeposit machine result with
        | some execution =>
            if decide (createDepositOutcomeValid execution) then
              match createDepositAction machine execution with
              | some .settleNestedCreateSuccess | some .settleNestedCreateInvalidCode |
                some .settleNestedCreateOutOfGas => settleSettlement semantics machine execution.settlement
              | _ => cleanupFailure semantics machine
            else
              cleanupFailure semantics machine
        | none => cleanupFailure semantics machine
      else
        match nestedSettlementAction machine result with
        | some .settleNestedRegularSuccess | some .settleNestedRevert | some .settleNestedException =>
            match semantics.settleChild machine result with
            | some settlement => settleSettlement semantics machine settlement
            | none => cleanupFailure semantics machine
        | _ => cleanupFailure semantics machine
    else
      if result.frame.isTopLevel then
        match topLevelSettlementAction machine result with
        | some .settleTopLevelSuccess | some .settleTopLevelRevert | some .settleTopLevelException =>
            match semantics.prepareTopLevelSubstate machine result with
            | some substate =>
                if transactionSubstateFieldsValid result substate then
                  match semantics.settleTopLevel machine result substate with
                  | some settled => .topLevelHalt machine settled substate
                  | none => cleanupFailure semantics machine
                else
                  cleanupFailure semantics machine
            | none => cleanupFailure semantics machine
        | _ => cleanupFailure semantics machine
      else
        .incomplete machine .invalidControlRoute
  else
    cleanupFailure semantics machine

def settleStep (semantics : OperationalSemantics) : DriverStep → DriverStep
  | .halt machine result => settleHalt semantics machine result
  | other => other

def cleanupUnwind (semantics : OperationalSemantics)
    (observation : OperationalControlObservation) (machine : Machine) : DriverStep :=
  match controlProgramDecision .cleanup observation with
  | some .cleanupCancelled | some .cleanupEscaped | some .cleanupInvalidControl =>
      match semantics.cleanup machine with
      | some cleaned => .incomplete cleaned .invalidControlRoute
      | none =>
          .incomplete machine (.unresolvedAdapter
            "FrameCleanupScope.Dispose/DisposeActiveFrames")
  | _ => .incomplete machine (.unresolvedAdapter "StageF/cleanup-control-plan")

def cleanupCompleted (semantics : OperationalSemantics) (machine : Machine)
    (result : FrameResult) : OperationalRunResult :=
  match controlProgramDecision .cleanup (.frameResult machine result) with
  | some .cleanupCompleted =>
      match semantics.cleanup machine with
      | some _ => .completed result
      | none => .incomplete (.unresolvedAdapter "FrameCleanupScope.Dispose/DisposeActiveFrames") machine
  | _ => .incomplete (.unresolvedAdapter "StageF/cleanup-control-plan") machine

def cleanupCompletedTopLevel (semantics : OperationalSemantics) (machine : Machine)
    (result : FrameResult) (substate : TransactionSubstate) : OperationalRunResult :=
  match controlProgramDecision .cleanup (.frameResult machine result) with
  | some .cleanupCompleted =>
      match semantics.cleanup machine with
      | some _ => .completedTopLevel result substate
      | none => .incomplete (.unresolvedAdapter "FrameCleanupScope.Dispose/DisposeActiveFrames") machine
  | _ => .incomplete (.unresolvedAdapter "StageF/cleanup-control-plan") machine

/- The recursive call is on explicit finite fuel. A child suspension pushes
   the captured parent before the next iteration; a settled child resumes only
   the head/call-depth shape of that LIFO stack. Full continuation-state
   equality remains a production adapter obligation. -/
def runFuel : Nat → OperationalSemantics → Machine → OperationalRunResult
  | 0, _, machine => .incomplete .fuelExhausted machine
  | fuel + 1, semantics, machine =>
      if h : sourceEvidenceValid ∧ controlProgramReady = true ∧ loopControlValid = true then
        match settleStep semantics (evaluateStep semantics machine) with
        | .continue next => runFuel fuel semantics next
        | .suspend beforePush parent child =>
            if childEntryValid child then
              runFuel fuel semantics (pushChild beforePush parent child)
            else
              .incomplete .invalidControlRoute beforePush
        | .halt current result => cleanupCompleted semantics current result
        | .topLevelHalt current result substate =>
            cleanupCompletedTopLevel semantics current result substate
        | .cancelled current _ =>
            match cleanupUnwind semantics (.cancelled current) current with
            | .incomplete cleaned reason => .incomplete reason cleaned
            | _ => .incomplete .invalidControlRoute current
        | .escaped current _ =>
            match cleanupUnwind semantics (.escaped current) current with
            | .incomplete cleaned reason => .incomplete reason cleaned
            | _ => .incomplete .invalidControlRoute current
        | .incomplete current reason => .incomplete reason current
      else
        .incomplete (.unresolvedAdapter "StageF/loop-control-plan") machine

def execute (fuel : Nat) (semantics : OperationalSemantics) (machine : Machine) : OperationalRunResult :=
  if h : sourceEvidenceValid ∧ controlProgramReady = true ∧ loopControlValid = true then
    runFuel fuel semantics machine
  else .incomplete (.unresolvedAdapter "StageF/source-plan-topology-route-evidence") machine

end Eip803x.Evm.FrameDriver.Operational.Generated
""";
}
