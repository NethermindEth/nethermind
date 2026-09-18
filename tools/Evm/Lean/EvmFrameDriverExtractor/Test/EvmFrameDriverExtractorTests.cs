// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.EvmFrameDriverExtractor.Test;

[TestFixture]
public sealed class EvmFrameDriverExtractorTests
{
    private string _root = null!;
    private string _scratch = null!;

    [SetUp]
    public void SetUp()
    {
        _root = FindRoot();
        _scratch = Path.Combine(Path.GetTempPath(), "frame-stage-e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratch);
        CopyClosure(_root, _scratch);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_scratch))
            Directory.Delete(_scratch, recursive: true);
    }

    [Test]
    public void Production_closure_emits_byte_identical_artifacts()
    {
        (IrDocument Ir, byte[] IrBytes, byte[] LeanBytes, byte[] ManifestBytes) first =
            EvmFrameDriverProfile.BuildForTest(_scratch);
        (IrDocument Ir, byte[] IrBytes, byte[] LeanBytes, byte[] ManifestBytes) second =
            EvmFrameDriverProfile.BuildForTest(_scratch);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.IrBytes, Is.EqualTo(second.IrBytes));
            Assert.That(first.LeanBytes, Is.EqualTo(second.LeanBytes));
            Assert.That(first.ManifestBytes, Is.EqualTo(second.ManifestBytes));
            SourceControlIdentity execute = first.Ir.SourceControl.Single(control =>
                control.Member == "ExecuteTransaction");
            Assert.That(execute.Topology.Any(node => node.ConditionIdentity == new ControlConditionIdentity(
                ControlConditionKind.Property, "_currentState", "IsContinuation", true)), Is.True);
            Assert.That(execute.Topology.Any(node => node.ConditionIdentity == new ControlConditionIdentity(
                ControlConditionKind.Property, "_currentState", "IsPrecompile", false)), Is.True);
            Assert.That(execute.Topology.Any(node => node.ConditionIdentity == new ControlConditionIdentity(
                ControlConditionKind.Property, "callResult", "IsReturn", true)), Is.True);
            Assert.That(execute.Topology.Any(node => node.ConditionIdentity == new ControlConditionIdentity(
                ControlConditionKind.Local, string.Empty, "isCreate", true)), Is.True);
            Assert.That(first.IrBytes[^1], Is.EqualTo((byte)'\n'));
            Assert.That(first.LeanBytes[^1], Is.EqualTo((byte)'\n'));
            Assert.That(first.ManifestBytes[^1], Is.EqualTo((byte)'\n'));
        }
    }

    [Test]
    public void Profile_closes_exact_driver_members_and_topology()
    {
        IrDocument document = EvmFrameDriverProfile.BuildForTest(_scratch).Ir;
        SourceControlIdentity[] controls = document.SourceControl;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(document.AcceptanceState, Is.EqualTo(EvmFrameDriverProfile.AcceptanceState));
            Assert.That(document.TargetRoot, Does.Contain("VirtualMachine<TGasPolicy>"));
            Assert.That(document.TargetRoot, Does.Not.Contain("Amsterdam"));
            Assert.That(document.TargetFork, Is.EqualTo("Nethermind.Specs.Forks.Amsterdam"));
            Assert.That(document.TargetGasPolicy, Is.EqualTo("Nethermind.Evm.GasPolicy.EthereumGasPolicy"));
            Assert.That(document.Sources, Has.Length.EqualTo(12));
            Assert.That(document.Members, Has.Length.EqualTo(21));
            Assert.That(document.Dependencies, Has.Length.EqualTo(8));
            Assert.That(document.ControlPlan.Select(static step => step.Stage), Is.EqualTo(
                new[] { "prepare", "dispatch", "classify", "settle", "cleanup" }));
            Assert.That(document.Branches, Has.Length.EqualTo(22));
            Assert.That(controls.Select(static control => control.Member), Is.EqualTo(
                new[] { "ExecuteTransaction", "RunByteCode", "RunDispatchLoop", "Dispose" }));
            Assert.That(controls, Has.All.Property(nameof(SourceControlIdentity.Topology)).Not.Empty);
            Assert.That(controls.SelectMany(static control => control.Topology)
                .Any(static node => node.Kind == "try"), Is.True);
            Assert.That(controls.SelectMany(static control => control.Topology)
                .Any(static node => node.Kind == "using"), Is.True);
            Assert.That(controls.Single(static control => control.Member == "ExecuteTransaction").Topology
                .Any(static node => node.Kind == "label" && node.Condition == "Failure"), Is.True);
            Assert.That(controls.Single(static control => control.Member == "Dispose").Operations,
                Does.Contain("call:DisposeActiveFrames"));
            Assert.That(controls.Single(static control => control.Member == "RunDispatchLoop").Topology
                .Any(static node => node.Kind == "while"), Is.True);
            Assert.That(controls.Single(static control => control.Member == "RunDispatchLoop").Topology
                .Count(static node => node.Kind == "statement" && node.Operations.Length == 1 &&
                    node.Operations[0] == "call:ThrowOperationCanceledException"), Is.EqualTo(2));
            Assert.That(controls.Single(static control => control.Member == "RunByteCode").Topology
                .Any(static node => node.Kind == "statement" &&
                    node.Operations.Contains("call:RunDispatchLoop")), Is.True);
        }
    }

    [Test]
    public void Zero_argument_member_parameter_tokens_are_admitted()
    {
        IrDocument document = EvmFrameDriverProfile.BuildForTest(_scratch).Ir;

        Assert.That(document.Members.Any(static member =>
            member.OwnerPath == "VirtualMachine`1.FrameCleanupScope" &&
            member.Member == "Dispose" && member.ParameterTypes.Length == 0), Is.True);
        Assert.That(document.Members.Any(static member =>
            member.OwnerPath == "VmState`1" && member.MemberKind == "property" &&
            member.Member == "ExecutionType" && member.ParameterTypes.Length == 0), Is.True);
    }

    [Test]
    public void Whitespace_member_parameter_tokens_are_rejected()
    {
        string path = AdmissionPath();
        AssertBuildRejected(path, static value => value.Replace(
            "|VirtualMachine`1.FrameCleanupScope|method|Dispose|0||MethodDeclaration|",
            "|VirtualMachine`1.FrameCleanupScope|method|Dispose|0| |MethodDeclaration|",
            StringComparison.Ordinal), "member parameters must be empty or canonical");
    }

    [Test]
    public void Malformed_member_admission_lines_are_rejected()
    {
        string path = AdmissionPath();
        AssertBuildRejected(path, static value => value.Replace(
            "|VirtualMachine`1.FrameCleanupScope|method|Dispose|0||MethodDeclaration|",
            "|VirtualMachine`1.FrameCleanupScope|method|Dispose|0|MethodDeclaration|",
            StringComparison.Ordinal), "Expected source, member, or dependency admission line");
    }

    [Test]
    public void Every_emitted_branch_binds_to_an_actual_topology_node()
    {
        IrDocument document = EvmFrameDriverProfile.BuildForTest(_scratch).Ir;
        Dictionary<string, SourceControlIdentity> controls = document.SourceControl
            .ToDictionary(static control => control.Member, StringComparer.Ordinal);

        foreach (BranchDescriptor branch in document.Branches)
            foreach (string binding in branch.SourceBindings)
            {
                string[] fields = binding.Split(':');
                Assert.That(fields, Has.Length.EqualTo(6), branch.Name);
                Assert.That(controls.TryGetValue(fields[0], out SourceControlIdentity? control), Is.True,
                    branch.Name);
                Assert.That(control!.Topology.Any(node => node.Id == fields[1] && node.Kind == fields[2] &&
                    node.Arm == fields[3] && node.Sha256 == fields[4]), Is.True, branch.Name);
            }

        foreach (ControlPlanStepIdentity step in document.ControlPlan)
        {
            Assert.That(controls.TryGetValue(step.Member, out SourceControlIdentity? control), Is.True,
                step.Stage);
            Assert.That(control!.Topology.Any(node => node.Id == step.NodeId && node.Kind == step.Kind &&
                node.Arm == step.SourceArm && node.Sha256 == step.SourceSha256), Is.True, step.Stage);
        }

        BranchDescriptor cancelled = document.Branches.Single(static branch => branch.Name == "Cancelled");
        Assert.That(cancelled.SourceBindings, Has.Length.EqualTo(2));
        Assert.That(cancelled.SourceBindings.Select(static binding => binding.Split(':')[1])
            .Distinct(StringComparer.Ordinal).Count(), Is.EqualTo(2));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Cancellation_binding_rejects_wrong_or_extra_poll_site(bool addExtraSite)
    {
        IrDocument document = EvmFrameDriverProfile.BuildForTest(_scratch).Ir;
        SourceControlIdentity control = document.SourceControl
            .Single(static candidate => candidate.Member == "RunDispatchLoop");
        ControlNodeIdentity[] sites = control.Topology
            .Where(static node => node.Kind == "statement" && node.Operations.Length == 1 &&
                node.Operations[0] == "call:ThrowOperationCanceledException")
            .ToArray();
        Assert.That(sites, Has.Length.EqualTo(2));

        ControlNodeIdentity[] mutatedTopology = addExtraSite
            ? [.. control.Topology, sites[0] with { Id = "n999" }]
            : [.. control.Topology.Select(node => ReferenceEquals(node, sites[0])
                ? node with { Operations = ["call:DifferentCancellationCall"] }
                : node)];
        SourceControlIdentity[] mutatedControls = document.SourceControl
            .Select(candidate => candidate.Member == control.Member
                ? candidate with { Topology = mutatedTopology }
                : candidate)
            .ToArray();

        ExtractionException exception = Assert.Throws<ExtractionException>(() =>
            EvmFrameDriverProfile.BindCancellationSites(mutatedControls))!;
        Assert.That(exception.Message, Does.Contain("expected exactly 2"));
    }

    [Test]
    public void Generated_kernel_is_theorem_free_single_step_and_typed_control_driven()
    {
        (IrDocument Ir, byte[] IrBytes, byte[] LeanBytes, byte[] ManifestBytes) artifacts =
            EvmFrameDriverProfile.BuildForTest(_scratch);
        string lean = Encoding.UTF8.GetString(artifacts.LeanBytes);
        string types = File.ReadAllText(Path.Combine(_root,
            "tools/Evm/Lean/EvmFrameDriverExtractor/Specification/Types.lean"));
        IrDocument document = artifacts.Ir;
        int executePlanStart = lean.IndexOf("def executePlan", StringComparison.Ordinal);
        int stepOnceBodyStart = lean.IndexOf("stepOnceBody leaves machine", executePlanStart,
            StringComparison.Ordinal);
        Assert.That(executePlanStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(stepOnceBodyStart, Is.GreaterThan(executePlanStart));
        string executePlan = lean[executePlanStart..stepOnceBodyStart];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lean, Does.Contain("def admittedControlNodes"));
            Assert.That(lean, Does.Contain("def admittedBranchBindings"));
            Assert.That(lean, Does.Contain("def controlTopologyReady"));
            Assert.That(lean, Does.Contain("admittedBranchNames.all"));
            Assert.That(lean, Does.Contain("node.sha256 == binding.sourceSha256"));
            Assert.That(types, Does.Contain("requiredOperations.all"));
            Assert.That(lean, Does.Contain("def executePlan"));
            ControlPlanStepIdentity firstPlan = document.ControlPlan[0];
            Assert.That(lean, Does.Contain(
                $"member := \"{firstPlan.Member}\", id := \"{firstPlan.NodeId}\", kind := \"{firstPlan.Kind}\""));
            foreach (ControlPlanStepIdentity step in document.ControlPlan)
                Assert.That(executePlan, Does.Contain(
                    $"stage := ControlPlanStage.{step.Stage}, member := \"{step.Member}\", nodeId := \"{step.NodeId}\", " +
                    $"kind := \"{step.Kind}\", sourceArm := \"{step.SourceArm}\", " +
                    $"sourceSha256 := \"{step.SourceSha256}\""));
            Assert.That(lean, Does.Contain("node.arm == binding.sourceArm"));
            Assert.That(lean, Does.Contain("&& phaseReady machine"));
            Assert.That(lean, Does.Contain("def stepOnce"));
            Assert.That(lean, Does.Contain("def runOne"));
            Assert.That(executePlan, Does.Not.Contain("nodeId := _"));
            Assert.That(executePlan, Does.Not.Contain("sourceArm := _"));
            Assert.That(executePlan, Does.Not.Contain("sourceSha256 := _"));
            Assert.That(lean, Does.Not.Contain("def runFuel"));
            Assert.That(lean, Does.Not.Contain("def drive"));
            Assert.That(lean, Does.Not.Contain("theorem "));
            Assert.That(lean, Does.Not.Contain("lemma "));
            Assert.That(lean, Does.Not.Contain("axiom "));
            Assert.That(lean, Does.Not.Contain("sorry"));
            Assert.That(lean, Does.Not.Contain("FrameMachineExecution"));
            Assert.That(lean, Does.Not.Contain("Specification.Reference"));
        }
    }

    [Test]
    public void Source_manifest_binds_member_and_control_topology_digests()
    {
        (IrDocument Ir, byte[] IrBytes, byte[] LeanBytes, byte[] ManifestBytes) artifacts =
            EvmFrameDriverProfile.BuildForTest(_scratch);
        using JsonDocument manifest = JsonDocument.Parse(artifacts.ManifestBytes);
        JsonElement root = manifest.RootElement;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(root.GetProperty("ir").GetProperty("sha256").GetString(),
                Is.EqualTo(EvmFrameDriverProfile.Hash(artifacts.IrBytes)));
            Assert.That(root.GetProperty("lean").GetProperty("sha256").GetString(),
                Is.EqualTo(EvmFrameDriverProfile.Hash(artifacts.LeanBytes)));
            Assert.That(root.GetProperty("combinedControlSha256").GetString(),
                Has.Length.EqualTo(64));
            Assert.That(root.GetProperty("sourceControl").GetArrayLength(), Is.EqualTo(4));
            Assert.That(root.GetProperty("members").EnumerateArray()
                .Any(member => member.GetProperty("ownerPath").GetString() == "VirtualMachine`1" &&
                    member.GetProperty("member").GetString() == "ExecuteTransaction"), Is.True);
            Assert.That(root.GetProperty("dependencies").EnumerateArray()
                .Count(dependency => dependency.GetProperty("kind").GetString() == "proof"), Is.EqualTo(4));
            Assert.That(root.GetProperty("dependencies").EnumerateArray()
                .Where(dependency => dependency.GetProperty("kind").GetString() == "proof")
                .Select(dependency => dependency.GetProperty("theorems").EnumerateArray().Single().GetString()),
                Is.EquivalentTo(new[]
                {
                    "EvmFrameMachineExtractor.Refinement.StageARouting.generated_route_lookup_refines_independent_spec",
                    "FrameJournalExtractor.Refinement.FrameJournal.transition_refines",
                    "Eip803x.PrecompileFullFrame.Refinement.source_full_frame_refines_reference",
                    "EvmFrameControlSettlementExtractor.Refinement.hash_pinned_canonical_frame_control_settlement_single_iteration_control_agreement",
                }));
        }
    }

    [Test]
    public void Mutating_an_admitted_source_is_rejected_before_emission()
    {
        string path = Path.Combine(_scratch, "src/Nethermind/Nethermind.Evm/VirtualMachine.cs");
        AssertBuildRejected(path, static value => value + "\n", "source hash changed");
    }

    [Test]
    public void Mutating_an_admitted_control_condition_is_rejected_before_operational_lowering()
    {
        string path = Path.Combine(_scratch, "src/Nethermind/Nethermind.Evm/VirtualMachine.cs");
        AssertBuildRejected(path, static value => value.Replace(
            "if (!_currentState.IsContinuation)", "if (_currentState.IsContinuation)", StringComparison.Ordinal),
            "source hash changed");
    }

    [Test]
    public void Mutating_the_source_dispatch_epoch_is_rejected_before_emission()
    {
        string path = Path.Combine(_scratch,
            "src/Nethermind/Nethermind.Evm/VirtualMachine.Dispatch.cs");
        AssertBuildRejected(path, static value => value.Replace(
            "CancellationCheckMask = 1023", "CancellationCheckMask = 1022", StringComparison.Ordinal),
            "source hash changed");
    }

    [Test]
    public void Mutating_an_admitted_member_selector_is_rejected()
    {
        string path = AdmissionPath();
        AssertBuildRejected(path, static value => value.Replace(
            "|VirtualMachine`1|method|ExecuteTransaction|1|",
            "|VirtualMachine`1|method|ExecuteTransactionMissing|1|", StringComparison.Ordinal),
            "found 0");
    }

    [Test]
    public void Mutating_an_accepted_leaf_dependency_is_rejected()
    {
        string path = Path.Combine(_scratch,
            "tools/Evm/Lean/EvmFrameMachineExtractor/Generated/EvmFrameMachineKernel.source-manifest.json");
        AssertBuildRejected(path, static value => value + "\n", "dependency hash changed");
    }

    [Test]
    public void Mutating_an_accepted_proof_theorem_identity_is_rejected()
    {
        string path = AdmissionPath();
        AssertBuildRejected(path, static value => value.Replace(
            "EvmFrameMachineExtractor.Refinement.StageARouting.generated_route_lookup_refines_independent_spec",
            "EvmFrameMachineExtractor.Refinement.StageARouting.changed_theorem", StringComparison.Ordinal),
            "proof dependency identities are incomplete or changed");
    }

    [Test]
    public void Embedded_admission_identity_is_checked_for_real_extraction()
    {
        string path = AdmissionPath();
        string original = File.ReadAllText(path);
        try
        {
            File.WriteAllText(path, original + "\n# mutation\n", new UTF8Encoding(false));
            ExtractionException exception = Assert.Throws<ExtractionException>(() =>
                EvmFrameDriverProfile.ValidateEmbeddedAdmission(_scratch))!;
            Assert.That(exception.Message, Does.Contain("embedded Stage E admission mismatch"));
        }
        finally
        {
            File.WriteAllText(path, original, new UTF8Encoding(false));
        }
    }

    [Test]
    public void Operational_profile_is_source_bound_and_fail_closed()
    {
        (OperationalIrDocument Ir, byte[] IrBytes, byte[] LeanBytes, byte[] ManifestBytes) artifacts =
            OperationalProfile.BuildForTest(_root);
        OperationalIrDocument document = artifacts.Ir;
        string lean = Encoding.UTF8.GetString(artifacts.LeanBytes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(document.AcceptanceState, Is.EqualTo(OperationalProfile.AcceptanceState));
            Assert.That(document.StageEBoundary, Is.EqualTo(EvmFrameDriverProfile.AcceptanceState));
            Assert.That(document.CompilerReferences, Has.Length.EqualTo(330));
            Assert.That(document.Loop.BatchLimit, Is.EqualTo(1024));
            Assert.That(document.Loop.DispatchModes, Is.EqualTo(
                new[] { "NoTrace", "NoTraceCancelable", "Traced", "TracedCancelable" }));
            Assert.That(document.BranchBindings, Has.Length.EqualTo(OperationalProfile.ExpectedOperationalBranchBindingCount));
            Assert.That(document.BranchBindings.Single(static binding => binding.Branch == "CompletedCleanup"),
                Is.Not.Null);
            Assert.That(document.BranchBindings.Single(static binding => binding.Branch == "ParentResume"),
                Is.Not.Null);
            Assert.That(document.BranchBindings.Single(static binding => binding.Branch == "NestedRegularSuccess").Stages,
                Is.EqualTo(new[] { "classify", "settle" }));
            Assert.That(document.BranchBindings.Single(static binding => binding.Branch == "TopLevelSuccess").Stages,
                Is.EqualTo(new[] { "classify", "settle" }));
            Assert.That(document.BranchBindings.All(static binding => !string.IsNullOrWhiteSpace(binding.Invocation) &&
                binding.Effects.Length > 0 && binding.Effects.All(static effect => !string.IsNullOrWhiteSpace(effect)) &&
                Enum.IsDefined(binding.Predicate) && binding.EffectKinds.Length == binding.Effects.Length &&
                binding.EffectKinds.All(static effect => Enum.IsDefined(effect)) &&
                Enum.IsDefined(binding.SettlementKind) &&
                !string.IsNullOrWhiteSpace(binding.Settlement) && binding.Stages.Length > 0 &&
                binding.Actions.Length == binding.Stages.Length), Is.True);
            Assert.That(document.ControlPlan.All(static step => step.Actions.Length > 0 &&
                step.Actions.Distinct().Count() == step.Actions.Length), Is.True);
            Assert.That(document.Adapters, Has.Length.GreaterThanOrEqualTo(15));
            Assert.That(document.MutationVectors, Has.Length.GreaterThanOrEqualTo(6));
            Assert.That(document.Adapters.All(static adapter =>
                adapter.FailureMode.Contains("none", StringComparison.OrdinalIgnoreCase)), Is.True);
            Assert.That(document.AcceptedDependencies, Has.Length.GreaterThanOrEqualTo(24));
            Assert.That(document.AcceptedDependencies.Any(dependency => dependency.Theorem ==
                "EvmFrameMachineExtractor.Refinement.StageARouting.generated_route_lookup_refines_independent_spec"), Is.True);
            Assert.That(document.AcceptedDependencies.All(static dependency =>
                dependency.SignatureSha256.Length == 64), Is.True);
            Assert.That(document.ExplicitOpenObligations, Has.Length.GreaterThanOrEqualTo(5));
            Assert.That(document.ExplicitOpenObligations, Does.Contain(
                "route PCs/bytes are adapter-supplied dispatch evidence; exact per-op successor/control and variable-width PUSH handling remain unproved"));
            Assert.That(document.SourceControl.Any(static control => control.Member == "ExecuteCall"), Is.False);
            Assert.That(lean, Does.Not.Contain("directInlineStaticPrecompile"));
            Assert.That(lean, Does.Contain("does not infer a C# CFG or PUSH widths"));
            int instructionsStart = lean.IndexOf("def prepareControlInstructions", StringComparison.Ordinal);
            int instructionsEnd = lean.IndexOf("def loopBatchLimit", instructionsStart,
                StringComparison.Ordinal);
            Assert.That(instructionsStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(instructionsEnd, Is.GreaterThan(instructionsStart));
            string instructions = lean[instructionsStart..instructionsEnd];
            foreach (OperationalBranchBindingIdentity binding in document.BranchBindings)
                Assert.That(instructions, Does.Contain(
                    $"branch := \"{binding.Branch}\", member := \"{binding.Member}\", nodeId := \"{binding.NodeId}\""));
            Assert.That(lean, Does.Contain("def branchBindingPresent"));
            Assert.That(lean, Does.Contain("binding.stages.any"));
            Assert.That(lean, Does.Contain("def prepareControlPlanStep"));
            Assert.That(lean, Does.Contain("def stageControlReady"));
            Assert.That(lean, Does.Contain("def controlPlanActionsValid"));
            Assert.That(lean, Does.Contain("def planStepShapeValid"));
            Assert.That(lean, Does.Contain("structure OperationalControlProgram"));
            Assert.That(lean, Does.Contain("def controlProgram : Option OperationalControlProgram"));
            Assert.That(lean, Does.Contain("def controlProgramReady"));
            Assert.That(lean, Does.Contain("List OperationalControlInstruction"));
            Assert.That(lean, Does.Contain("inductive OperationalControlPredicate"));
            Assert.That(lean, Does.Contain("inductive OperationalControlEffect"));
            Assert.That(lean, Does.Contain("inductive OperationalControlSettlement"));
            Assert.That(lean, Does.Contain("def controlInstructionValid"));
            Assert.That(lean, Does.Contain("def controlInstructionBindingPresent"));
            Assert.That(lean, Does.Contain("def controlActionShapeValid"));
            Assert.That(lean, Does.Contain("def executeControlInstructions"));
            Assert.That(lean, Does.Contain("def controlProgramExecution"));
            Assert.That(lean, Does.Contain("def controlPredicateMatches"));
            Assert.That(lean, Does.Contain("def controlEffectsAdmissible"));
            Assert.That(lean, Does.Contain("def controlSettlementMatches"));
            Assert.That(lean, Does.Contain("def selectControlInstruction"));
            Assert.That(lean, Does.Contain("def controlProgramDecision"));
            Assert.That(lean, Does.Contain("def prepareControlInstructions : List OperationalControlInstruction"));
            Assert.That(lean, Does.Contain("predicate := .bytecodeFrame"));
            Assert.That(lean, Does.Contain("effects := [.runBytecode, .runDispatchLoop]"));
            Assert.That(lean, Does.Contain("action := .dispatchBytecode"));
            Assert.That(lean, Does.Contain("actions := [.dispatchBytecode"));
            Assert.That(lean, Does.Contain("settlement := .invocation"));
            Assert.That(lean, Does.Contain("controlProgramDecision .prepare (.machine machine)"));
            Assert.That(lean, Does.Contain("controlProgramDecision .dispatch (.machine machine)"));
            Assert.That(lean, Does.Contain("controlProgramDecision .classify (.machineStep step)"));
            Assert.That(lean, Does.Contain("controlProgramDecision .settle (.frameResult machine result)"));
            Assert.That(lean, Does.Contain("controlProgramDecision .cleanup (.frameResult machine result)"));
            Assert.That(lean, Does.Not.Contain("def controlInstructionOperationValid"));
            Assert.That(lean, Does.Not.Contain("controlProgramHas"));
            Assert.That(lean, Does.Not.Contain("operation :="));
            Assert.That(lean, Does.Contain("createDepositControlReady execution.outcome"));
            Assert.That(lean, Does.Contain("controlProgramReady = true ∧"));
            Assert.That(lean, Does.Contain("stageControlReady program.prepare program.prepareBranches"));
            Assert.That(lean, Does.Contain("stageControlReady program.dispatch program.dispatchBranches"));
            Assert.That(lean, Does.Contain("def precompileRequiredBranches"));
            Assert.That(lean, Does.Contain("def precompileControlReady"));
            Assert.That(lean, Does.Contain("stageControlReady program.classify program.classifyBranches"));
            Assert.That(lean, Does.Contain("stageControlReady program.settle program.settleBranches"));
            Assert.That(lean, Does.Contain("stageControlReady program.cleanup program.cleanupBranches"));
            Assert.That(lean, Does.Not.Contain("expectedControlPlan"));
            Assert.That(lean, Does.Not.Contain("expectedControlNodes"));
            Assert.That(lean, Does.Not.Contain("expectedBranchBindings"));
            Assert.That(lean, Does.Contain("def loopEntryTransitions"));
            Assert.That(lean, Does.Contain("hasString \"fuel decrements once per driver iteration\" loopFuelRules"));
            Assert.That(lean, Does.Contain("hasString \"refund merge/rollback\" loopStateEffects"));
            Assert.That(lean, Does.Contain("loopMeasure = \"gasLeft + remainingCode + parentStackDepth\""));
            Assert.That(lean, Does.Contain("sourceEvidenceValid ∧ controlProgramReady = true ∧ loopControlValid = true"));
            Assert.That(lean, Does.Contain("effects := [.clearReturnData, .prepareFresh]"));
            Assert.That(lean, Does.Contain("effects := [.restoreWorld, .creditParent]"));
            Assert.That(lean, Does.Contain("effects := [.disposeActiveFrames]"));
            Assert.That(lean, Does.Contain("predicate := .resumeParent"));
            Assert.That(lean, Does.Contain("effects := [.resumeParent]"));
            Assert.That(lean, Does.Contain("some .settleResume"));
            Assert.That(lean, Does.Contain("predicate := .topLevelSuccess"));
            Assert.That(lean, Does.Contain("stage := .classify, predicate := .topLevelSuccess"));
            Assert.That(lean, Does.Contain("effects := [.clearReturnData, .prepareFresh]"));
            Assert.That(lean, Does.Contain("effects := [.runBytecode, .runDispatchLoop]"));
            Assert.That(lean, Does.Contain("instruction.effects = [.runBytecode, .runDispatchLoop]"));
            Assert.That(artifacts.IrBytes[^1], Is.EqualTo((byte)'\n'));
            Assert.That(artifacts.LeanBytes[^1], Is.EqualTo((byte)'\n'));
            Assert.That(artifacts.ManifestBytes[^1], Is.EqualTo((byte)'\n'));
        }
    }

    [Test]
    public void Operational_typed_plan_topology_and_routes_are_mutation_gated()
    {
        (OperationalIrDocument Ir, byte[] IrBytes, byte[] LeanBytes, byte[] ManifestBytes) artifacts =
            OperationalProfile.BuildForTest(_root);
        OperationalIrDocument Ir = artifacts.Ir;
        byte[] IrBytes = artifacts.IrBytes;
        string irSha256 = EvmFrameDriverProfile.Hash(IrBytes);

        OperationalControlPlanStepIdentity[] plan = [.. Ir.ControlPlan];
        plan[0] = plan[0] with { NodeId = "mutated-plan-node" };
        OperationalIrDocument mutatedPlan = Ir with { ControlPlan = plan };

        OperationalControlPlanStepIdentity[] planActions = [.. Ir.ControlPlan];
        planActions[0] = planActions[0] with
        {
            Actions = [OperationalControlActionIdentity.DispatchBytecode],
        };
        OperationalIrDocument mutatedPlanActions = Ir with { ControlPlan = planActions };

        OperationalTopologyNodeIdentity[] topology = [.. Ir.Topology];
        topology[0] = topology[0] with { Sha256 = new string('0', 64) };
        OperationalIrDocument mutatedTopology = Ir with { Topology = topology };

        OperationalOpcodeRouteIdentity[] routes = [.. Ir.OpcodeRoutes];
        routes[0] = routes[0] with { Instruction = "mutated-route" };
        OperationalIrDocument mutatedRoutes = Ir with { OpcodeRoutes = routes };

        OperationalPrecompileRouteIdentity[] precompileRoutes = [.. Ir.PrecompileRoutes];
        precompileRoutes[0] = precompileRoutes[0] with { Name = "mutated-precompile" };
        OperationalIrDocument mutatedPrecompileRoutes = Ir with { PrecompileRoutes = precompileRoutes };

        OperationalIrDocument mutatedTarget = Ir with { TargetGasPolicy = "mutated-policy" };

        OperationalBranchBindingIdentity[] branches = [.. Ir.BranchBindings];
        branches[0] = branches[0] with { Branch = "mutated-branch" };
        OperationalIrDocument mutatedBranches = Ir with { BranchBindings = branches };

        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<ExtractionException>(() => OperationalLeanEmitter.Emit(mutatedPlan, irSha256));
            Assert.Throws<ExtractionException>(() => OperationalLeanEmitter.Emit(mutatedPlanActions, irSha256));
            Assert.Throws<ExtractionException>(() => OperationalLeanEmitter.Emit(mutatedTopology, irSha256));
            Assert.Throws<ExtractionException>(() => OperationalLeanEmitter.Emit(mutatedRoutes, irSha256));
            Assert.Throws<ExtractionException>(() => OperationalLeanEmitter.Emit(mutatedPrecompileRoutes, irSha256));
            Assert.Throws<ExtractionException>(() => OperationalLeanEmitter.Emit(mutatedBranches, irSha256));
            Assert.Throws<ExtractionException>(() => OperationalLeanEmitter.Emit(mutatedTarget, irSha256));
        }
    }

    [Test]
    public void Operational_branch_semantics_are_hash_bound_and_fail_closed()
    {
        (OperationalIrDocument Ir, byte[] IrBytes, _, _) = OperationalProfile.BuildForTest(_root);
        string irSha256 = EvmFrameDriverProfile.Hash(IrBytes);
        OperationalBranchBindingIdentity original = Ir.BranchBindings.First(static binding =>
            binding.Branch == "BytecodeDispatch");
        OperationalControlActionIdentity[] actions = [.. original.Actions];
        actions[0] = OperationalControlActionIdentity.DispatchFullPrecompile;
        OperationalBranchBindingIdentity mutated = original with { Actions = actions };
        OperationalBranchBindingIdentity[] bindings = [.. Ir.BranchBindings
            .Select(binding => binding.Branch == original.Branch ? mutated : binding)];
        OperationalIrDocument mutatedIr = Ir with { BranchBindings = bindings };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(OperationalProfile.OperationalEvidenceDigest(mutatedIr),
                Is.Not.EqualTo(OperationalProfile.OperationalEvidenceDigest(Ir)));
            Assert.Throws<ExtractionException>(() => OperationalLeanEmitter.Emit(mutatedIr, irSha256));
        }
    }

    [Test]
    public void Operational_semantic_ir_mutation_changes_the_interpreted_transition()
    {
        (OperationalIrDocument Ir, byte[] IrBytes, byte[] LeanBytes, _) =
            OperationalProfile.BuildForTest(_root);
        OperationalBranchBindingIdentity original = Ir.BranchBindings.First(static binding =>
            binding.Branch == "BytecodeDispatch");
        OperationalControlActionIdentity[] actions = [.. original.Actions];
        actions[0] = OperationalControlActionIdentity.DispatchFullPrecompile;
        OperationalBranchBindingIdentity mutated = original with { Actions = actions };
        OperationalBranchBindingIdentity[] bindings = [.. Ir.BranchBindings
            .Select(binding => binding.Branch == original.Branch ? mutated : binding)];
        OperationalIrDocument mutatedIr = Ir with { BranchBindings = bindings };

        byte[] mutatedLeanBytes = OperationalLeanEmitter.EmitForMutationTest(mutatedIr,
            EvmFrameDriverProfile.Hash(IrBytes));
        string originalLean = Encoding.UTF8.GetString(LeanBytes);
        string mutatedLean = Encoding.UTF8.GetString(mutatedLeanBytes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mutatedLeanBytes, Is.Not.EqualTo(LeanBytes));
            Assert.That(originalLean, Does.Contain(
                "effects := [.runBytecode, .runDispatchLoop]"));
            Assert.That(mutatedLean, Does.Contain("effects := [.runBytecode, .runDispatchLoop]"));
            Assert.That(originalLean, Does.Contain("action := .dispatchBytecode"));
            Assert.That(mutatedLean, Does.Contain("action := .dispatchFullPrecompile"));
            Assert.That(mutatedLean, Does.Not.Contain("controlInstructionAction instruction"));
            Assert.That(mutatedLean, Does.Contain("controlProgramExecution program"));
            Assert.That(mutatedLean, Does.Contain("sourceBranchActionsValid"));
            Assert.That(mutatedLean, Does.Contain("controlPlanActionsValid"));
            Assert.That(mutatedLean, Does.Contain("controlProgramReady = true"));
        }
    }

    [Test]
    public void Operational_loop_ir_mutation_changes_emitted_control_data()
    {
        (OperationalIrDocument Ir, byte[] IrBytes, byte[] LeanBytes, byte[] ManifestBytes) =
            OperationalProfile.BuildForTest(_root);
        string[] entryTransitions = [.. Ir.Loop.EntryTransitions];
        entryTransitions[0] = "PrepareFresh";
        OperationalIrDocument mutated = Ir with
        {
            Loop = Ir.Loop with { EntryTransitions = entryTransitions },
        };

        byte[] mutatedLeanBytes = OperationalLeanEmitter.EmitForMutationTest(mutated,
            EvmFrameDriverProfile.Hash(IrBytes));
        string mutatedLean = Encoding.UTF8.GetString(mutatedLeanBytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mutatedLeanBytes, Is.Not.EqualTo(LeanBytes));
            int loopRulesStart = mutatedLean.IndexOf("def loopRules", StringComparison.Ordinal);
            int loopRulesEnd = mutatedLean.IndexOf("def loopEntryTransitions", loopRulesStart,
                StringComparison.Ordinal);
            Assert.That(loopRulesStart, Is.GreaterThanOrEqualTo(0));
            Assert.That(loopRulesEnd, Is.GreaterThan(loopRulesStart));
            Assert.That(mutatedLean[loopRulesStart..loopRulesEnd],
                Does.Not.Contain(".freshClearsReturnData"));
            Assert.That(mutatedLean, Does.Contain("loopRulePresent .freshClearsReturnData"));
            Assert.That(mutatedLean, Does.Contain("loopControlValid = true"));
            Assert.That(ManifestBytes, Is.Not.Empty);
        }

        string[] stateEffects = [.. Ir.Loop.StateEffects];
        stateEffects[0] = "mutated-state-effect";
        OperationalIrDocument mutatedState = Ir with
        {
            Loop = Ir.Loop with { StateEffects = stateEffects },
        };
        byte[] mutatedStateLeanBytes = OperationalLeanEmitter.EmitForMutationTest(mutatedState,
            EvmFrameDriverProfile.Hash(IrBytes));
        string mutatedStateLean = Encoding.UTF8.GetString(mutatedStateLeanBytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mutatedStateLeanBytes, Is.Not.EqualTo(LeanBytes));
            Assert.That(mutatedStateLean, Does.Contain("mutated-state-effect"));
            Assert.That(mutatedStateLean, Does.Contain("hasString \"refund child gas\" loopStateEffects"));
            Assert.That(mutatedStateLean, Does.Contain("hasString \"refund merge/rollback\" loopStateEffects"));
        }
    }

    [Test]
    public void Operational_reference_and_vectors_are_independent_and_semantic()
    {
        string reference = File.ReadAllText(Path.Combine(_root,
            "tools/Evm/Lean/EvmFrameDriverExtractor/Specification/OperationalReference.lean"));
        string vectors = File.ReadAllText(Path.Combine(_root,
            "tools/Evm/Lean/EvmFrameDriverExtractor/Specification/OperationalVectors.lean"));
        string types = File.ReadAllText(Path.Combine(_root,
            "tools/Evm/Lean/EvmFrameDriverExtractor/Specification/OperationalTypes.lean"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reference, Does.Not.Contain("Generated.Operational"));
            Assert.That(reference, Does.Contain("def runFuel"));
            Assert.That(vectors, Does.Contain("independentMutationVectors"));
            Assert.That(vectors, Does.Contain("mutated_refund_run_is_observable"));
            Assert.That(vectors, Does.Contain("mutated_return_data_run_is_observable"));
            Assert.That(vectors, Does.Contain("mutated_world_rollback_run_is_observable"));
            Assert.That(vectors, Does.Contain("mutated_cleanup_run_is_observable"));
            Assert.That(vectors, Does.Contain("mutated_route_evidence_run_is_rejected"));
            Assert.That(vectors, Does.Contain("mutated_duplicate_route_evidence_run_is_rejected"));
            Assert.That(vectors, Does.Contain("mutated_malformed_control_route_run_is_rejected"));
            Assert.That(vectors, Does.Contain("def concreteMutationWitnesses : List Bool"));
            Assert.That(vectors, Does.Contain("runFixture"));
            Assert.That(types, Does.Contain("startOpcodeCount"));
            Assert.That(types, Does.Contain("routePcs"));
            Assert.That(types, Does.Contain("inductive OperationalControlPredicate"));
            Assert.That(types, Does.Contain("inductive OperationalControlEffect"));
            Assert.That(types, Does.Contain("inductive OperationalControlSettlement"));
            Assert.That(types, Does.Contain("structure OperationalControlInstruction"));
            Assert.That(types, Does.Contain("action : OperationalControlAction"));
            Assert.That(types, Does.Contain("def routeEvidenceAligned"));
            Assert.That(types, Does.Contain("def adapterSuppliedRouteEvidence"));
            Assert.That(types, Does.Contain("adapter-supplied dispatch-evidence stream"));
            Assert.That(types, Does.Contain("per-op successor/control transitions"));
            Assert.That(types, Does.Contain("MachineStepControlValid"));
            Assert.That(types, Does.Contain("FrameResultControlValid"));
            Assert.That(vectors, Does.Contain("List.replicate 1024"));
            Assert.That(vectors, Does.Contain("completedOpcodeCount := 1024"));
            Assert.That(vectors, Does.Contain("cancellationEpoch1TerminalExecution"));
            Assert.That(vectors, Does.Contain("cancellationEpoch1NonterminalExecution"));
            Assert.That(vectors, Does.Contain("cancellationEpoch2TerminalExecution"));
            Assert.That(vectors, Does.Contain("cancellationEpoch2NonterminalExecution"));
            Assert.That(vectors, Does.Contain("afterCompleteBatch 2048"));
            Assert.That(types, Does.Contain("parentStackShape"));
            Assert.That(types, Does.Contain("validLifoStackShape"));
            Assert.That(types, Does.Contain("cleanupFailure semantics machine"));
        }
    }

    [TestCase("../VirtualMachine.cs")]
    [TestCase("src\\Nethermind\\Nethermind.Evm\\VirtualMachine.cs")]
    [TestCase("src//Nethermind")]
    [TestCase("src/../VirtualMachine.cs")]
    public void Noncanonical_paths_fail_closed(string path) =>
        Assert.Throws<ExtractionException>(() => EvmFrameDriverProfile.ResolveCanonical(_scratch, path));

    [Test]
    public void Refinement_exposes_same_leaf_algebra_premises()
    {
        string refinement = File.ReadAllText(Path.Combine(_root,
            "tools/Evm/Lean/EvmFrameDriverExtractor/Refinement/FrameDriver.lean"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(refinement, Does.Contain("structure SameLeafAgreement"));
            Assert.That(refinement, Does.Contain("clearReturnData : production.preparation.clearReturnData"));
            Assert.That(refinement, Does.Contain("runBytecode : production.dispatch.runBytecode"));
            Assert.That(refinement, Does.Contain("classifyNestedCreateDeposit"));
            Assert.That(refinement, Does.Contain("theorem same_leaf_eq"));
            Assert.That(refinement, Does.Contain("phaseReadyInput"));
            Assert.That(refinement, Does.Not.Contain("allLeaves : production = canonical"));
            Assert.That(refinement, Does.Not.Contain("source_step_refines_reference"));
            Assert.That(refinement, Does.Not.Contain("source-to-leaf simulation"));
        }
    }

    private string AdmissionPath() => Path.Combine(_scratch,
        "tools/Evm/Lean/EvmFrameDriverExtractor/Admission/ProductionClosure.txt");

    private void AssertBuildRejected(string path, Func<string, string> mutation, string message)
    {
        string original = File.ReadAllText(path);
        try
        {
            File.WriteAllText(path, mutation(original), new UTF8Encoding(false));
            ExtractionException exception = Assert.Throws<ExtractionException>(() =>
                EvmFrameDriverProfile.BuildForTest(_scratch))!;
            Assert.That(exception.Message, Does.Contain(message));
        }
        finally
        {
            File.WriteAllText(path, original, new UTF8Encoding(false));
        }
    }

    private static void CopyClosure(string root, string destination)
    {
        string admissionPath = Path.Combine(root,
            "tools/Evm/Lean/EvmFrameDriverExtractor/Admission/ProductionClosure.txt");
        string admission = File.ReadAllText(admissionPath);
        Copy(root, destination,
            "tools/Evm/Lean/EvmFrameDriverExtractor/Admission/ProductionClosure.txt");
        foreach (string rawLine in admission.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            string[] fields = line.Split('|');
            if (fields.Length == 4 && fields[0] == "source")
                Copy(root, destination, fields[2]);
            else if (fields.Length == 7 && fields[0] == "dependency")
                Copy(root, destination, fields[3]);
        }
    }

    private static void Copy(string root, string destination, string relative)
    {
        string source = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        string target = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target);
    }

    private static string FindRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "src/Nethermind/Directory.Build.props")))
                return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException("Unable to locate the formal repository root.");
    }
}
