// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.EvmFrameControlSettlementExtractor.Test;

[TestFixture, NonParallelizable]
public sealed class EvmFrameControlSettlementExtractorTests
{
    private static readonly string[] ExpectedDispatchOrder =
    [
        "clearReturnDataOnFreshOnly",
        "freshOrContinuationPreparation",
        "selectCurrentFrameBytecodeOrFullPrecompile",
        "executeSelectedFrame",
        "bytecodeDispatchCancellationAtEntryOrCompletedBatchWithSuccessor",
        "recordBytecodeDirectInlineStaticPrecompileOutcome",
        "classifyReturnedThrownOrFullPrecompileOutcome",
        "settleNestedCreateDepositAfterInitcodeSuccess",
        "applyVmFrameExitSettlement",
        "cleanupFrameUnwindWhenIterationLeavesVm",
    ];

    private string _root = null!;
    private string _scratch = null!;
    private (IrDocument Ir, byte[] IrBytes, byte[] LeanBytes, byte[] ManifestBytes) _artifacts;

    [OneTimeSetUp]
    public void SetUp()
    {
        _root = FindRoot();
        _scratch = Directory.CreateTempSubdirectory("evm-frame-control-settlement-tests-").FullName;
        CopyClosure(_root, _scratch);
        _artifacts = EvmFrameControlSettlementProfile.BuildForTest(_scratch);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        if (_scratch is null) return;
        string path = Path.GetFullPath(_scratch);
        string temp = Path.GetFullPath(Path.GetTempPath());
        if (!path.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith("evm-frame-control-settlement-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing cleanup outside the owned Stage D test directory.");
        Directory.Delete(path, recursive: true);
    }

    [Test]
    public void Profile_covers_the_reviewed_single_iteration_surface()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_artifacts.Ir.Sources, Has.Length.EqualTo(90));
            Assert.That(_artifacts.Ir.Members, Has.Length.GreaterThanOrEqualTo(77));
            Assert.That(_artifacts.Ir.Dependencies, Has.Length.EqualTo(13));
            Assert.That(_artifacts.Ir.Branches, Has.Length.GreaterThanOrEqualTo(26));
            Assert.That(_artifacts.Ir.Boundary.Adapters, Has.Length.EqualTo(12));
            Assert.That(_artifacts.Ir.Boundary.Oracles, Has.Length.EqualTo(2));
            Assert.That(_artifacts.Ir.ReviewedOperationalBindings, Has.Length.GreaterThanOrEqualTo(14));
            Assert.That(_artifacts.Ir.Exclusions, Has.Length.EqualTo(10));
            Assert.That(_artifacts.Ir.AcceptanceState, Is.EqualTo("stage-d-single-iteration-admitted"));
            Assert.That(_artifacts.Ir.ClosedFork, Is.EqualTo(EvmFrameControlSettlementProfile.ClosedFork));
            Assert.That(_artifacts.Ir.ClosedGasPolicy, Is.EqualTo(EvmFrameControlSettlementProfile.ClosedGasPolicy));
            SourceIdentity props = _artifacts.Ir.Sources.Single(source => source.Path.EndsWith("Directory.Build.props", StringComparison.Ordinal));
            Assert.That(props.SyntaxSha256, Is.EqualTo(props.Sha256));
        }
    }

    [Test]
    public void Emission_is_byte_deterministic()
    {
        (IrDocument Ir, byte[] IrBytes, byte[] LeanBytes, byte[] ManifestBytes) again =
            EvmFrameControlSettlementProfile.BuildForTest(_scratch);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(again.IrBytes, Is.EqualTo(_artifacts.IrBytes));
            Assert.That(again.ManifestBytes, Is.EqualTo(_artifacts.ManifestBytes));
            Assert.That(again.LeanBytes, Is.EqualTo(_artifacts.LeanBytes));
        }
    }

    [Test]
    public void Manifest_binds_emitted_artifacts_and_reviewed_operational_profile()
    {
        string irHash = EvmFrameControlSettlementProfile.Hash(_artifacts.IrBytes);
        string leanHash = EvmFrameControlSettlementProfile.Hash(_artifacts.LeanBytes);
        string manifest = Encoding.UTF8.GetString(_artifacts.ManifestBytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(manifest, Does.Contain($"\"sha256\": \"{irHash}\""));
            Assert.That(manifest, Does.Contain($"\"sha256\": \"{leanHash}\""));
            Assert.That(manifest, Does.Contain("combinedSourceSha256"));
            Assert.That(manifest, Does.Contain("combinedMemberSha256"));
            Assert.That(manifest, Does.Contain("reviewedOperationalBindings"));
            Assert.That(manifest, Does.Not.Contain("semanticBindings"));
        }
    }

    [Test]
    public void Generated_kernel_is_theorem_free_and_limited_to_one_iteration()
    {
        string lean = Encoding.UTF8.GetString(_artifacts.LeanBytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(lean, Does.Contain("CanonicalLeaves"));
            Assert.That(lean, Does.Contain("def driveIteration"));
            Assert.That(lean, Does.Contain("def admittedBranchContracts"));
            Assert.That(lean, Does.Contain("directInlineStaticPrecompile"));
            Assert.That(lean, Does.Contain("fullPrecompileOutOfGasTop"));
            Assert.That(lean, Does.Contain("fullPrecompileManagedExceptionNested"));
            Assert.That(lean, Does.Contain("def settleNestedCreate"));
            Assert.That(lean, Does.Contain("def cleanupTerminal"));
            Assert.That(lean, Does.Not.Contain("def runFuel"));
            Assert.That(lean, Does.Not.Contain("outer.refund"));
            Assert.That(lean, Does.Not.Contain("outer.deployContract"));
            Assert.That(lean, Does.Not.Contain("outer.finalizeTransaction"));
            Assert.That(lean, Does.Not.Contain("FrameMachineExecution"));
            Assert.That(lean, Does.Not.Contain("Specification.Reference"));
            Assert.That(lean, Does.Not.Match(@"(?m)^\s*(theorem|lemma|axiom)\b"));
        }
    }

    [Test]
    public void Generated_kernel_keeps_frame_selection_direct_event_and_cleanup_distinctions()
    {
        string lean = Encoding.UTF8.GetString(_artifacts.LeanBytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(lean, Does.Contain("| .fresh => adapters.preparation.prepareFresh (adapters.preparation.clearReturnData machine)"));
            Assert.That(lean, Does.Contain("| .continuation => adapters.preparation.prepareContinuation"));
            Assert.That(lean, Does.Contain("match subjectOf machine with"));
            Assert.That(lean, Does.Contain("| .bytecode =>"));
            Assert.That(lean, Does.Contain("| .fullPrecompile =>"));
            Assert.That(lean, Does.Contain("| .createSuccessNested => settleNestedCreate"));
            Assert.That(lean, Does.Contain("else if machine.current.executionType.isCreate then .createSuccessNested"));
            Assert.That(lean, Does.Not.Contain("result.frame.executionType.isCreate"));
            Assert.That(lean, Does.Contain("| .completed | .suspended => outcome"));
            Assert.That(lean, Does.Not.Contain("pollBoundary"));
            Assert.That(lean, Does.Not.Contain("boundaryPollDue"));
        }
    }

    [Test]
    public void Specification_exposes_scoped_adapter_bounds_and_exact_cancellation_boundaries()
    {
        string types = File.ReadAllText(Path.Combine(_root,
            "tools/Evm/Lean/EvmFrameControlSettlementExtractor/Specification/Types.lean"));
        string refinement = File.ReadAllText(Path.Combine(_root,
            "tools/Evm/Lean/EvmFrameControlSettlementExtractor/Refinement/FrameControlSettlement.lean"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(types, Does.Contain("inductive OpcodeCancellationBoundary"));
            Assert.That(types, Does.Contain("subjectOf machine"));
            Assert.That(types, Does.Contain("| .precompile _ => .fullPrecompile"));
            Assert.That(types, Does.Contain("def isBytecodeInvocation"));
            Assert.That(types, Does.Contain("def isFullPrecompileInvocation"));
            Assert.That(types, Does.Contain("def isDriverLoopPhase"));
            Assert.That(types, Does.Contain("stage_c_full_theorem_is_constructible"));
            Assert.That(types, Does.Contain("FrontPreservesParents"));
            Assert.That(types, Does.Contain("executionRefund : Int"));
            Assert.That(types, Does.Contain("precompileSuccess : Option Bool"));
            Assert.That(types, Does.Contain("stateGasRefundAdvanced : Nat"));
            Assert.That(types, Does.Contain("previousCallResult : PreviousCallResult"));
            Assert.That(types, Does.Contain("shouldRestoreRipemdTouch : Bool"));
            Assert.That(types, Does.Not.Contain("FuelFacts"));
            Assert.That(types, Does.Not.Contain("OuterTransactionSettlementAdapter"));
            Assert.That(refinement, Does.Contain("structure StepAdmission"));
            Assert.That(refinement, Does.Contain("sourceLoopPhase"));
            Assert.That(refinement, Does.Contain("bytecodeDispatchOutputAdmitted"));
            Assert.That(refinement, Does.Contain("fullPrecompileSettlementOutputAdmitted"));
            Assert.That(refinement, Does.Contain("structure AdmittedAdapterOutputBounds"));
            Assert.That(refinement, Does.Contain("bytecodeCancellationHasExactBoundary"));
            Assert.That(refinement, Does.Contain("fullFrameHasNoDriverCancellation"));
            Assert.That(refinement, Does.Contain("fullPrecompileOutcomeCompletesCurrentFrame"));
            Assert.That(refinement, Does.Contain("topLevelParentShape"));
            Assert.That(types, Does.Contain("0 < completedOpcodeCount"));
            Assert.That(refinement, Does.Contain("hash_pinned_canonical_frame_control_settlement_single_iteration_control_agreement"));
            Assert.That(refinement, Does.Not.Contain("runFuel"));
        }
    }

    [Test]
    public void Control_agreement_uses_shared_leaves_and_scoped_production_obligations()
    {
        string types = File.ReadAllText(Path.Combine(_root,
            "tools/Evm/Lean/EvmFrameControlSettlementExtractor/Specification/Types.lean"));
        string profile = File.ReadAllText(Path.Combine(_root,
            "tools/Evm/Lean/EvmFrameControlSettlementExtractor/Profile.cs"));
        string reference = File.ReadAllText(Path.Combine(_root,
            "tools/Evm/Lean/EvmFrameControlSettlementExtractor/Specification/Reference.lean"));
        string refinement = File.ReadAllText(Path.Combine(_root,
            "tools/Evm/Lean/EvmFrameControlSettlementExtractor/Refinement/FrameControlSettlement.lean"));
        string theorem = Slice(refinement,
            "theorem hash_pinned_canonical_frame_control_settlement_single_iteration_control_agreement",
            "end EvmFrameControlSettlementExtractor.Refinement");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(types, Does.Contain("structure CanonicalLeaves"));
            Assert.That(types, Does.Contain("structure SourceAdapterBindings"));
            Assert.That(types, Does.Contain("explicit assumption interface"));
            Assert.That(types, Does.Contain("does not\ndischarge semantic adapter composition"));
            Assert.That(profile, Does.Contain("does not discharge semantic adapter composition"));
            Assert.That(types, Does.Not.Contain("AdapterCompositionFacts"));
            Assert.That(types, Does.Not.Contain("SettlementSimulation"));
            Assert.That(types, Does.Not.Contain("OracleAssumptions"));
            Assert.That(types, Does.Not.Contain("DependencyBindings"));
            Assert.That(refinement, Does.Contain("structure AdmittedStep"));
            Assert.That(refinement, Does.Contain("invocationDerived"));
            Assert.That(refinement, Does.Contain("routeDerived"));
            Assert.That(refinement, Does.Contain("structure ProductionSettlementLeafSimulation"));
            Assert.That(refinement, Does.Contain("structure ProductionLeafSimulation"));
            Assert.That(refinement, Does.Contain("AdmittedStep production admitted input invocation route"));
            Assert.That(refinement, Does.Not.Contain("cleanup_simulates"));
            Assert.That(theorem, Does.Not.Contain("ProductionLeafSimulation"));
            Assert.That(reference, Does.Contain("inductive EntryPlan"));
            Assert.That(reference, Does.Contain("inductive DispatchPlan"));
            Assert.That(reference, Does.Contain("inductive SettlementAction"));
            Assert.That(reference, Does.Contain("inductive ClosingPlan"));
            Assert.That(reference, Does.Not.Contain("def settleRoute"));
        }
    }

    [Test]
    public void Dispatch_order_is_complete_and_emitted_in_control_flow_order()
    {
        Assert.That(_artifacts.Ir.DispatchOrder, Is.EqualTo(ExpectedDispatchOrder));
        string lean = Encoding.UTF8.GetString(_artifacts.LeanBytes);
        string orderSection = Slice(lean, "def admittedDispatchOrder", "def prepare");
        AssertInOrder(orderSection, ExpectedDispatchOrder);

        string driverSection = Slice(lean, "def driveIteration", "end EvmFrameControlSettlementExtractor.Generated");
        AssertInOrder(driverSection,
            "let prepared := prepare",
            "let dispatched := dispatch",
            "let settled := settleRoute",
            "let observed :=",
            "cleanupTerminal adapters observed");
    }

    [Test]
    public void Every_named_branch_is_emitted_with_its_reviewed_contract()
    {
        string lean = Encoding.UTF8.GetString(_artifacts.LeanBytes);
        foreach (BranchDescriptor branch in _artifacts.Ir.Branches)
        {
            string contract = $"{branch.Name}|{branch.Invocation}|{string.Join(" -> ", branch.Effects)}|{branch.Settlement}";
            using (Assert.EnterMultipleScope())
            {
                Assert.That(branch.Effects, Is.Not.Empty, branch.Name);
                Assert.That(branch.Invocation, Is.Not.Empty, branch.Name);
                Assert.That(branch.Settlement, Is.Not.Empty, branch.Name);
                Assert.That(lean, Does.Contain(contract), branch.Name);
            }
        }
    }

    [Test]
    public void Every_explicit_dependency_has_a_theorem_binding() => Assert.Multiple(() =>
    {
        foreach (DependencyIdentity dependency in _artifacts.Ir.Dependencies)
        {
            Assert.That(dependency.Path, Does.StartWith("tools/Evm/Lean/"));
            Assert.That(dependency.Theorems, Is.Not.Empty, dependency.Name);
            Assert.That(dependency.Binding, Is.Not.Empty, dependency.Name);
        }
    });

    [Test]
    public void Admission_witnesses_cover_all_representative_frame_shapes()
    {
        string witness = File.ReadAllText(Path.Combine(_root,
            "tools/Evm/Lean/EvmFrameControlSettlementExtractor/Specification/AdmissionWitnesses.lean"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(witness, Does.Contain("representative_admission_is_inhabited"));
            Assert.That(witness, Does.Contain("representative_stage_d_assumptions_are_inhabited"));
            Assert.That(witness, Does.Contain("representative_single_iteration_control_agreement_is_inhabited"));
            Assert.That(witness, Does.Contain("bytecode_terminal_route"));
            Assert.That(witness, Does.Contain("bytecode_suspend_route"));
            Assert.That(witness, Does.Contain("direct_inline_is_an_opcode_outcome"));
            Assert.That(witness, Does.Contain("full_precompile_subject"));
            Assert.That(witness, Does.Contain("full_precompile_success_is_current_frame_completion"));
            Assert.That(witness, Does.Contain("full_precompile_success_route"));
            Assert.That(witness, Does.Contain("full_precompile_managed_exception_topology_split"));
            Assert.That(witness, Does.Contain("full_precompile_tracer_cancellation_is_escaped_not_driver_poll"));
            Assert.That(witness, Does.Contain("create_marked_precompile_cannot_enter_full_frame_success_domain"));
            Assert.That(witness, Does.Not.Contain("sorry"));
        }
    }

    [Test]
    public void Stage_c_composition_stays_open_and_component_vectors_execute_proofs()
    {
        string bridge = File.ReadAllText(Path.Combine(_root,
            "tools/Evm/Lean/EvmFrameControlSettlementExtractor/Refinement/StageCFullPrecompileBridge.lean"));
        string vectors = File.ReadAllText(Path.Combine(_root,
            "tools/Evm/Lean/EvmFrameControlSettlementExtractor/Refinement/StageCFullPrecompileBridgeVectors.lean"));
        string gates = File.ReadAllText(Path.Combine(_root,
            "tools/Evm/Lean/EvmFrameControlSettlementExtractor/Verify-BridgeMutationGates.ps1"));
        string componentProof = Slice(bridge, "theorem stage_d_component_evidence", "def invocationOfRaw");
        string[] componentSentinels =
        [
            "admission.stageD admission.oracles admission.bindings admission.fixedWidth",
            "admission.stageD.preparedAdmitted",
            "DriverBoundarySound",
            "boundary.fullPrecompileOutcomeCompletesCurrentFrame",
            "boundary.fullPrecompileFrameBound",
            "checked.2.2.1.prepared",
        ];
        string[] vectorSentinels =
        [
            "vector_input_admitted",
            "vector_admission",
            "stage_c_generated_full_frame_agrees_reference _ (vector_admission",
            "vector_execution_has_expected_kind",
            "vector_execution_has_expected_route",
            "vector_returned_failure_preserves_priced_gas_and_error",
            "vector_success_preserves_output",
            "FullPrecompileRun (vectorBridge",
            "generated_execute_is_raw_admitted",
            "missing_native_is_incomplete",
            "process_adapter_is_incomplete",
            "vector_status_and_substate_error_are_distinct",
        ];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bridge, Does.Contain("structure OpenFullPrecompileCompositionObligation"));
            Assert.That(bridge, Does.Contain("def RepresentedSettlementPreserved"));
            Assert.That(bridge, Does.Contain("driver : Observation"));
            Assert.That(bridge, Does.Contain("substateError := result.result.bind"));
            Assert.That(bridge, Does.Not.Contain("projectSettlement"));
            Assert.That(bridge, Does.Not.Contain("StageDFullPrecompileSeam"));
            Assert.That(bridge, Does.Not.Contain("stage_c_to_stage_d_full_precompile_observational_refinement"));
            Assert.That(bridge, Does.Not.Contain("resumedParentOpcodeBatch"));
            Assert.That(bridge, Does.Not.Contain("sorry"));
            foreach (string sentinel in componentSentinels)
            {
                Assert.That(componentProof, Does.Contain(sentinel), sentinel);
            }
            foreach (string sentinel in vectorSentinels)
            {
                Assert.That(vectors, Does.Contain(sentinel), sentinel);
            }
            Assert.That(vectors, Does.Contain(
                "[1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 256]"));
            Assert.That(vectors, Does.Contain("cases nested <;> cases vector <;>"));
            Assert.That(gates, Does.Contain("substate-error-erased"));
            Assert.That(gates, Does.Contain("missing-native-becomes-success"));
            Assert.That(gates, Does.Contain("pricing-boundary-moved"));
            Assert.That(gates, Does.Contain("fail-closed-becomes-completed"));
            Assert.That(gates, Does.Contain("-DwarningAsError=true"));
        }
    }

    [Test]
    public void Production_closure_names_critical_frame_driver_helpers()
    {
        string closure = File.ReadAllText(Path.Combine(_root,
            "tools/Evm/Lean/EvmFrameControlSettlementExtractor/Admission/ProductionClosure.txt"));
        string[] required =
        [
            "VirtualMachineStatics|ThrowOperationCanceledException|method",
            "VirtualMachine|HandleCreate|method",
            "VirtualMachine|HandleRevert|method",
            "VirtualMachine|ExecutePrecompileCall|method",
            "CallResult|PrecompileSuccess|property",
            "VmState|RentTopLevel|method",
            "ExecutionTypeExtensions|IsAnyCreate|method",
            "EvmPooledMemory|Dispose|method",
            "CancellationTxTracer|ReportAction|method",
            "VirtualMachine|ExecuteJumpIfOpcode|method",
            "EvmInstructions|TryInlineStaticPrecompileCall|method",
        ];

        using (Assert.EnterMultipleScope())
        {
            foreach (string selector in required)
            {
                Assert.That(closure, Does.Contain(selector), selector);
            }
        }
    }

    [TestCase("../VirtualMachine.cs")]
    [TestCase("/tmp/VirtualMachine.cs")]
    [TestCase("src\\Nethermind\\VirtualMachine.cs")]
    [TestCase("src//Nethermind")]
    [TestCase("src/../VirtualMachine.cs")]
    public void Unsafe_paths_fail_closed(string path) =>
        Assert.Throws<ExtractionException>(() => InvokeResolve(path));

    [TestCase("0A00000000000000000000000000000000000000000000000000000000000000")]
    [TestCase("short")]
    [TestCase("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void Invalid_admission_hashes_reach_hash_validation(string hash)
    {
        string admission = AdmissionPath();
        AssertMutatedBuildRejected(admission, text => text.Replace(
            "ad544425af1b0ec60334511abf79896a77843088457ef81b98a06b56d13e4e3c",
            hash,
            StringComparison.Ordinal), "admission hashes must be lowercase SHA-256 values");
    }

    [Test]
    public void Unknown_admission_role_reaches_parser_validation()
    {
        string admission = AdmissionPath();
        AssertMutatedBuildRejected(admission, static text => text.Replace(
            "source|build|",
            "source|unknown-role|",
            StringComparison.Ordinal), "Unknown Stage D source role");
    }

    [Test]
    public void Member_selector_mutation_reaches_member_resolution()
    {
        string admission = AdmissionPath();
        AssertMutatedBuildRejected(admission, static text => text.Replace(
            "|VirtualMachine|ExecuteTransaction|method",
            "|VirtualMachine|ExecuteTransactionMissing|method",
            StringComparison.Ordinal), "member selector did not resolve");
    }

    [Test]
    public void Duplicate_admission_selector_reaches_duplicate_validation()
    {
        string admission = AdmissionPath();
        AssertMutatedBuildRejected(admission, static text => text.Replace(
            "member|src/Nethermind/Nethermind.Evm/VirtualMachine.cs|VirtualMachine|ExecuteTransaction|method",
            "member|src/Nethermind/Nethermind.Evm/VirtualMachine.cs|VirtualMachine|ExecuteTransaction|method\nmember|src/Nethermind/Nethermind.Evm/VirtualMachine.cs|VirtualMachine|ExecuteTransaction|method",
            StringComparison.Ordinal), "Duplicate Stage D member");
    }

    [Test]
    public void Embedded_admission_identity_is_checked_only_for_real_extraction()
    {
        string admission = AdmissionPath();
        string source = File.ReadAllText(admission);
        try
        {
            File.WriteAllText(admission, source + "\n# local test mutation\n", new UTF8Encoding(false));
            ExtractionException exception = Assert.Throws<ExtractionException>(() =>
                EvmFrameControlSettlementProfile.ValidateEmbeddedAdmission(_scratch))!;
            Assert.That(exception.Message, Does.Contain("embedded Stage D admission mismatch"));
        }
        finally
        {
            File.WriteAllText(admission, source, new UTF8Encoding(false));
        }
    }

    [Test]
    public void Source_byte_mutation_is_rejected_before_emission()
    {
        string path = Path.Combine(_scratch, "src/Nethermind/Nethermind.Evm/VirtualMachine.cs");
        AssertMutatedBuildRejected(path, static bytes => bytes + "\n", "exact source profile rejected");
    }

    [Test]
    public void Dependency_byte_mutation_is_rejected_before_emission()
    {
        string path = Path.Combine(_scratch, "tools/Evm/Lean/EvmFrameMachineExtractor/Generated/EvmFrameMachineKernel.lean");
        AssertMutatedBuildRejected(path, static bytes => bytes + "\n", "exact dependency profile rejected");
    }

    [Test]
    public void Missing_source_file_is_rejected()
    {
        string path = Path.Combine(_scratch, "src/Nethermind/Nethermind.Evm/VirtualMachine.cs");
        File.Move(path, path + ".missing");
        try
        {
            ExtractionException exception = Assert.Throws<ExtractionException>(() =>
                EvmFrameControlSettlementProfile.BuildForTest(_scratch))!;
            Assert.That(exception.Message, Does.Contain("path is missing"));
        }
        finally
        {
            File.Move(path + ".missing", path);
        }
    }

    [Test]
    public void Design_profile_has_no_whole_run_or_outer_transaction_shortcut()
    {
        string source = Encoding.UTF8.GetString(_artifacts.LeanBytes);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source, Does.Not.Contain("Generated.run adapters input = Reference.run adapters input"));
            Assert.That(source, Does.Not.Contain("runFuel"));
            Assert.That(source, Does.Not.Contain("DeployContract"));
            Assert.That(source, Does.Not.Contain("FinalizeTransaction"));
            Assert.That(_artifacts.Ir.ReviewedOperationalBindings.Single(binding =>
                binding.StartsWith("Exact source bytes", StringComparison.Ordinal)),
                Does.Contain("not translated into Lean semantics"));
        }
    }

    private string AdmissionPath() => Path.Combine(_scratch,
        "tools/Evm/Lean/EvmFrameControlSettlementExtractor/Admission/ProductionClosure.txt");

    private void InvokeResolve(string path) => _ = EvmFrameControlSettlementProfile.ResolveCanonical(_scratch, path);

    private void AssertMutatedBuildRejected(string path, Func<string, string> mutation, string expectedMessage)
    {
        string source = File.ReadAllText(path);
        try
        {
            File.WriteAllText(path, mutation(source), new UTF8Encoding(false));
            ExtractionException exception = Assert.Throws<ExtractionException>(() =>
                EvmFrameControlSettlementProfile.BuildForTest(_scratch))!;
            Assert.That(exception.Message, Does.Contain(expectedMessage));
        }
        finally
        {
            File.WriteAllText(path, source, new UTF8Encoding(false));
        }
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), startMarker);
        Assert.That(end, Is.GreaterThan(start), endMarker);
        return source[start..end];
    }

    private static void AssertInOrder(string source, params string[] values)
    {
        int previous = -1;
        foreach (string value in values)
        {
            int current = source.IndexOf(value, StringComparison.Ordinal);
            Assert.That(current, Is.GreaterThan(previous), value);
            previous = current;
        }
    }

    private static void CopyClosure(string root, string destination)
    {
        string admissionPath = Path.Combine(root, "tools/Evm/Lean/EvmFrameControlSettlementExtractor/Admission/ProductionClosure.txt");
        string admission = File.ReadAllText(admissionPath);
        Copy(root, destination, "tools/Evm/Lean/EvmFrameControlSettlementExtractor/Admission/ProductionClosure.txt");
        foreach (string line in admission.Split('\n'))
        {
            if (line.StartsWith("source|", StringComparison.Ordinal))
            {
                string[] fields = line.TrimEnd('\r').Split('|');
                Copy(root, destination, fields[2]);
            }
            else if (line.StartsWith("dependency|", StringComparison.Ordinal))
            {
                string[] fields = line.TrimEnd('\r').Split('|');
                Copy(root, destination, fields[3]);
            }
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
