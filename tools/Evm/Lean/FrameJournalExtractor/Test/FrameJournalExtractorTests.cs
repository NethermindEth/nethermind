// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.FrameJournalExtractor.Test;

[TestFixture]
public class FrameJournalExtractorTests
{
    [Test]
    public void Extraction_is_deterministic_field_complete_and_theorem_free()
    {
        using Fixture fixture = new();
        string lean = Path.Combine(fixture.Output, "FrameJournalKernel.lean");
        ExtractionResult first = FrameJournalProfile.Extract(fixture.Root, fixture.Output, lean);
        byte[] firstIr = File.ReadAllBytes(first.IrPath);
        byte[] firstManifest = File.ReadAllBytes(first.ManifestPath);
        byte[] firstLean = File.ReadAllBytes(first.LeanPath);
        FrameJournalProfile.ValidateArtifacts(fixture.Root, firstManifest, firstIr, firstLean);
        ExtractionResult second = FrameJournalProfile.Extract(fixture.Root, fixture.Output, lean);
        using JsonDocument ir = JsonDocument.Parse(firstIr);
        using JsonDocument manifest = JsonDocument.Parse(firstManifest);
        string leanText = Encoding.UTF8.GetString(firstLean);
        string irHash = Convert.ToHexStringLower(SHA256.HashData(firstIr));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.SourceCount, Is.EqualTo(20));
            Assert.That(first.MemberCount, Is.EqualTo(53));
            Assert.That(first.DependencyCount, Is.EqualTo(6));
            Assert.That(ir.RootElement.GetProperty("operations").GetArrayLength(), Is.EqualTo(9));
            Assert.That(ir.RootElement.GetProperty("scope").GetProperty("restoredSurfaces").GetArrayLength(), Is.EqualTo(7));
            Assert.That(ir.RootElement.GetProperty("scope").GetProperty("transactionWideSurfaces").GetArrayLength(), Is.EqualTo(3));
            Assert.That(ir.RootElement.GetProperty("dependencyTheorems").GetArrayLength(), Is.EqualTo(13));
            Assert.That(ir.RootElement.GetProperty("importedKernels").GetArrayLength(), Is.EqualTo(1));
            Assert.That(manifest.RootElement.GetProperty("dependencies").GetArrayLength(), Is.EqualTo(6));
            Assert.That(manifest.RootElement.GetProperty("importedKernels").GetArrayLength(), Is.EqualTo(1));
            Assert.That(manifest.RootElement.GetProperty("semanticBindings").GetArrayLength(), Is.EqualTo(18));
            Assert.That(File.ReadAllBytes(second.IrPath), Is.EqualTo(firstIr));
            Assert.That(File.ReadAllBytes(second.ManifestPath), Is.EqualTo(firstManifest));
            Assert.That(File.ReadAllBytes(second.LeanPath), Is.EqualTo(firstLean));
            Assert.That(leanText, Does.Contain($"-- Semantic IR SHA-256: {irHash}"));
            Assert.That(leanText, Does.Contain("WorldJournalExtractor.Generated.WorldJournalKernel"));
            Assert.That(leanText, Does.Contain("def transition"));
            Assert.That(leanText, Does.Contain("def executeTrace"));
            Assert.That(leanText, Does.Not.Match(@"(?m)^\s*(theorem|lemma|axiom|example|sorry|admit)\b"));
            Assert.That(leanText, Does.Not.Match(@"(?m)^\s*import\s+FrameJournalExtractor\.Specification\.FrameJournal\s*$"));
            Assert.That(leanText, Does.Not.Match(@"(?m)^\s*import\s+WorldJournalExtractor\.Specification\.WorldJournal\s*$"));
        }
    }

    [Test]
    public void Ir_operation_order_and_dependency_states_are_closed()
    {
        IrDocument document = FrameJournalProfile.BuildDocument();
        string[] expected =
        [
            "applyWorld", "enterCall", "enterCreate", "preFrameCallFailure",
            "preFrameCreateFailure", "latchRipemdTouch", "exitSuccess", "exitRevert",
            "exitException",
        ];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(document.Operations.Select(operation => operation.Name), Is.EqualTo(expected));
            Assert.That(document.SourceChains, Has.Length.EqualTo(6));
            Assert.That(document.DependencyTheorems, Has.Length.EqualTo(13));
            Assert.That(document.ImportedKernels, Has.Length.EqualTo(1));
            Assert.That(document.Operations.Count(operation => operation.BindingKind == "sourceMember"), Is.EqualTo(8));
            Assert.That(document.Operations.Count(operation => operation.BindingKind == "dependencyTheorem"), Is.EqualTo(1));
            Assert.That(document.Operations.Single(operation => operation.Name == "enterCreate").OrderedEffects,
                Is.EqualTo(new[] { "wasCreated", "takeSnapshot" }));
            Assert.That(document.Operations.Single(operation => operation.Name == "preFrameCallFailure").AdmissionBoundary,
                Does.Contain("warming remains"));
            Assert.That(document.Operations.Single(operation => operation.Name == "applyWorld").Member,
                Is.EqualTo("transition_refines"));
        }
    }

    [Test]
    public void Refinement_checks_and_uses_world_transition_theorem()
    {
        using Fixture fixture = new();
        string refinement = File.ReadAllText(Path.Combine(fixture.Root,
            "tools/Evm/Lean/FrameJournalExtractor/Refinement/FrameJournal.lean"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refinement, Does.Contain(
                "#check @WorldJournalExtractor.Refinement.WorldJournal.transition_refines"));
            Assert.That(Count(refinement,
                "WorldJournalExtractor.Refinement.WorldJournal.transition_refines"), Is.GreaterThanOrEqualTo(4));
        }
    }

    [Test]
    public void Every_operation_name_has_a_mutation_sentinel()
    {
        using Fixture fixture = new();
        ExtractionResult result = FrameJournalProfile.Extract(fixture.Root, fixture.Output);
        byte[] manifest = File.ReadAllBytes(result.ManifestPath);
        byte[] lean = File.ReadAllBytes(result.LeanPath);
        string ir = File.ReadAllText(result.IrPath);

        foreach (OperationDescriptor operation in FrameJournalProfile.BuildDocument().Operations)
        {
            string before = $"\"name\": \"{operation.Name}\"";
            string mutant = ir.Replace(before, $"\"name\": \"mutated-{operation.Name}\"", StringComparison.Ordinal);
            Assert.That(mutant, Is.Not.EqualTo(ir), operation.Name);
            Assert.That(
                () => FrameJournalProfile.ValidateArtifacts(
                    fixture.Root, manifest, Encoding.UTF8.GetBytes(mutant), lean),
                Throws.InstanceOf<ExtractionException>(),
                operation.Name);
        }
    }

    [Test]
    public void Every_admitted_input_file_rejects_a_byte_mutation()
    {
        foreach (string relativePath in FrameJournalProfile.SourceRelativePaths
                     .Concat(FrameJournalProfile.DependencyRelativePaths)
                     .Distinct(StringComparer.Ordinal))
        {
            using RepositoryFixture fixture = new(copyDependencies: true);
            fixture.Append(relativePath, " ");
            Assert.That(
                () => FrameJournalProfile.Extract(fixture.Root, fixture.Output),
                Throws.InstanceOf<ExtractionException>(),
                relativePath);
        }
    }

    [TestCase(FrameJournalProfile.VirtualMachinePath,
        "previousState.CommitToParent(_currentState);", "_ = previousState;",
        TestName = "Rejects_success_commit_removal")]
    [TestCase(FrameJournalProfile.VirtualMachinePath,
        "_worldState.Restore(previousState.Snapshot);", "_ = previousState.Snapshot;",
        TestName = "Rejects_revert_restore_removal")]
    [TestCase(FrameJournalProfile.VirtualMachinePath,
        "PopAndRestoreParentState();", "_currentState = _stateStack.Pop();",
        TestName = "Rejects_exception_dispose_path_bypass")]
    [TestCase(FrameJournalProfile.VmStatePath,
        "_accessTracker.WasCreated(env.ExecutingAccount);\r\n        }\r\n        _accessTracker.TakeSnapshot();",
        "_accessTracker.TakeSnapshot();\r\n        }\r\n        _accessTracker.WasCreated(env.ExecutingAccount);",
        TestName = "Rejects_create_mark_snapshot_order_mutation")]
    [TestCase(FrameJournalProfile.VmStatePath,
        "_canRestore = false; // we can't restore if we committed", "_canRestore = true;",
        TestName = "Rejects_success_restore_latch_mutation")]
    [TestCase(FrameJournalProfile.VmStatePath,
        "if (_canRestore)", "if (!_canRestore)",
        TestName = "Rejects_dispose_restore_gate_mutation")]
    [TestCase(FrameJournalProfile.CallPath,
        "Snapshot snapshot = state.TakeSnapshot();\r\n        // Subtract the transfer value from the caller's balance.\r\n        if (TOpCall.ExecutionType != ExecutionType.DELEGATECALL && !callValue.IsZero) state.SubtractFromBalance(caller, in callValue, vm.Spec);",
        "// Subtract the transfer value from the caller's balance.\r\n        if (TOpCall.ExecutionType != ExecutionType.DELEGATECALL && !callValue.IsZero) state.SubtractFromBalance(caller, in callValue, vm.Spec);\r\n        Snapshot snapshot = state.TakeSnapshot();",
        TestName = "Rejects_call_snapshot_transfer_order_mutation")]
    [TestCase(FrameJournalProfile.CreatePath,
        "state.IncrementNonce(env.ExecutingAccount);", "_ = accountNonce;",
        TestName = "Rejects_create_parent_nonce_boundary_mutation")]
    [TestCase(FrameJournalProfile.AccessTrackerPath,
        "if (!_isTracingAccess)", "if (_isTracingAccess)",
        TestName = "Rejects_normal_access_restore_gate_mutation")]
    [TestCase(FrameJournalProfile.ControlFlowPath,
        "vmState.AccessTracker.ToBeDestroyed(executingAccount);", "vmState.AccessTracker.WarmUp(executingAccount);",
        TestName = "Rejects_selfdestruct_destroy_mutation")]
    [TestCase(FrameJournalProfile.WorldStatePath,
        "_persistentStorageProvider.Restore(snapshot.StorageSnapshot.PersistentStorageSnapshot);\r\n            _transientStorageProvider.Restore(snapshot.StorageSnapshot.TransientStorageSnapshot);",
        "_transientStorageProvider.Restore(snapshot.StorageSnapshot.TransientStorageSnapshot);\r\n            _persistentStorageProvider.Restore(snapshot.StorageSnapshot.PersistentStorageSnapshot);",
        TestName = "Rejects_world_restore_order_mutation")]
    [TestCase(FrameJournalProfile.BuildTargetsPath,
        "<ItemGroup Condition=\"'$(EnableZkEvm)' != 'true'\">",
        "<ItemGroup Condition=\"'$(EnableZkEvm)' == 'true'\">",
        TestName = "Rejects_standard_build_selection_mutation")]
    [TestCase(FrameJournalProfile.SpecFlagsPath,
        "public static void Validate(IReleaseSpec spec) { }",
        "public static void Validate(IReleaseSpec spec) => throw new InvalidOperationException();",
        TestName = "Rejects_standard_spec_flags_mutation")]
    [TestCase(FrameJournalProfile.SpecFlagsPath,
        "public static bool Eip158(IReleaseSpec spec) => spec.ClearEmptyAccountWhenTouched;",
        "public static bool Eip158(IReleaseSpec spec) => false;",
        TestName = "Rejects_standard_eip158_binding_mutation")]
    [TestCase(FrameJournalProfile.VirtualMachineDispatchPath,
        "_executionHandlers = table.GetExecutionHandlers(spec);",
        "_executionHandlers = null;",
        TestName = "Rejects_execution_handler_resolution_bypass")]
    [TestCase(FrameJournalProfile.VirtualMachineExecutionHandlersPath,
        "SpecFlags.Eip158(spec) ? &RunPrecompileCore<OnFlag> : &RunPrecompileCore<OffFlag>;",
        "SpecFlags.Eip158(spec) ? &RunPrecompileCore<OffFlag> : &RunPrecompileCore<OnFlag>;",
        TestName = "Rejects_ripemd_eip158_dispatch_mutation")]
    [TestCase(FrameJournalProfile.VirtualMachineExecutionHandlersPath,
        "vm.RunPrecompile<Eip158>(state);",
        "default;",
        TestName = "Rejects_ripemd_generic_bridge_mutation")]
    [TestCase(FrameJournalProfile.ExecutionTypePath,
        "ExecutionType.CREATE or ExecutionType.CREATE2",
        "ExecutionType.CREATE",
        TestName = "Rejects_is_any_create_mutation")]
    [TestCase(FrameJournalProfile.AmsterdamPath,
        "spec.IsEip8038Enabled = true;",
        "spec.IsEip8038Enabled = false;",
        TestName = "Rejects_amsterdam_rule_mutation")]
    [TestCase(FrameJournalProfile.MainnetSpecProviderPath,
        "[AmsterdamBlockTimestamp] = Amsterdam.Instance,",
        "[AmsterdamBlockTimestamp] = BPO2.Instance,",
        TestName = "Rejects_mainnet_amsterdam_lineage_mutation")]
    [TestCase(FrameJournalProfile.BlockProcessingModulePath,
        ".AddScoped<IWorldState, WorldState>()",
        ".AddScoped<IWorldState, TracedAccessWorldState>()",
        TestName = "Rejects_normal_world_state_root_mutation")]
    [TestCase(FrameJournalProfile.TransactionProcessorPath,
        "using StackAccessTracker accessTracker = new(tracer.IsTracingAccess);",
        "using StackAccessTracker accessTracker = new(true);",
        TestName = "Rejects_access_tracker_mode_construction_mutation")]
    [TestCase(FrameJournalProfile.VirtualMachinePath,
        "_worldState.Restore(previousState.Snapshot);\r\n        RestoreRipemdTouch(_worldState, BlockExecutionContext.Spec, _shouldRestoreRipemdTouch);",
        "RestoreRipemdTouch(_worldState, BlockExecutionContext.Spec, _shouldRestoreRipemdTouch);\r\n        _worldState.Restore(previousState.Snapshot);",
        TestName = "Rejects_ripemd_retouch_order_mutation")]
    [TestCase(FrameJournalProfile.VirtualMachinePath,
        "_shouldRestoreRipemdTouch = false;",
        "_shouldRestoreRipemdTouch = true;",
        TestName = "Rejects_ripemd_transaction_reset_mutation")]
    [TestCase(FrameJournalProfile.AccountPath,
        "public bool IsEmpty => _codeHash is null && Balance.IsZero && Nonce == 0;",
        "public bool IsEmpty => _storageRoot is null && _codeHash is null && Balance.IsZero && Nonce == 0;",
        TestName = "Rejects_ripemd_storage_only_emptiness_mutation")]
    [TestCase(FrameJournalProfile.StateProviderPath,
        "PushTouch(address, touched, releaseSpec, true);",
        "PushUpdate(address, touched);",
        TestName = "Rejects_ripemd_touch_journal_mutation")]
    public void Source_path_mutations_fail_closed(string path, string before, string after)
    {
        using RepositoryFixture fixture = new(copyDependencies: false);
        fixture.Replace(path, before, after);

        Assert.That(() => FrameJournalProfile.ValidateSourceShape(fixture.Root),
            Throws.InstanceOf<ExtractionException>());
    }

    [Test]
    public void Source_overload_mutation_fails_exact_member_admission()
    {
        using RepositoryFixture fixture = new(copyDependencies: false);
        fixture.Replace(FrameJournalProfile.VirtualMachinePath,
            "scoped ref nuint previousCallOutputLength, out bool shouldExit)",
            "ref nuint previousCallOutputLength, out bool shouldExit)");

        Assert.That(() => FrameJournalProfile.ValidateSourceShape(fixture.Root),
            Throws.InstanceOf<ExtractionException>());
    }

    [Test]
    public void Source_owner_mutation_fails_exact_member_admission()
    {
        using RepositoryFixture fixture = new(copyDependencies: false);
        fixture.Replace(FrameJournalProfile.CallResultPath,
            "protected readonly ref struct CallResult",
            "protected readonly ref struct RenamedCallResult");

        Assert.That(() => FrameJournalProfile.ValidateSourceShape(fixture.Root),
            Throws.InstanceOf<ExtractionException>());
    }

    [Test]
    public void Competing_member_declaration_fails_closed()
    {
        using RepositoryFixture fixture = new(copyDependencies: false);
        const string original = "public bool ShouldRevert { get; }";
        const string competing = "public bool ShouldRevert { get; }\n\n        public bool ShouldRevert => false;";
        fixture.Replace(FrameJournalProfile.CallResultPath, original, competing);

        Assert.That(() => FrameJournalProfile.ValidateSourceShape(fixture.Root),
            Throws.InstanceOf<ExtractionException>());
    }

    [Test]
    public void Dependency_theorem_name_mutation_fails_closed()
    {
        using RepositoryFixture fixture = new(copyDependencies: true);
        fixture.Replace("tools/Evm/Lean/WorldJournalExtractor/Refinement/WorldJournal.lean",
            "theorem finite_trace_refines ", "theorem finite_trace_refines_mutated ");

        Assert.That(() => FrameJournalProfile.ValidateDependencyTheoremShape(fixture.Root),
            Throws.InstanceOf<ExtractionException>());
    }

    [Test]
    public void World_journal_transition_theorem_signature_mutation_fails_closed()
    {
        using RepositoryFixture fixture = new(copyDependencies: true);
        fixture.Replace("tools/Evm/Lean/WorldJournalExtractor/Refinement/WorldJournal.lean",
            "theorem transition_refines (operation : Operation) (machine : Machine) :",
            "theorem transition_refines (operation : Operation) (machine : Machine) (_extra : Nat) :");

        Assert.That(() => FrameJournalProfile.ValidateDependencyTheoremShape(fixture.Root),
            Throws.InstanceOf<ExtractionException>());
    }

    [Test]
    public void Mutated_imported_world_journal_kernel_fails_closed()
    {
        using RepositoryFixture fixture = new(copyDependencies: true);
        fixture.Replace("tools/Evm/Lean/WorldJournalExtractor/Generated/WorldJournalKernel.lean",
            "def transition (operation : Operation)", "def transitionMutated (operation : Operation)");

        Assert.That(() => FrameJournalProfile.ValidateImportedKernelShape(fixture.Root),
            Throws.InstanceOf<ExtractionException>());
    }

    [Test]
    public void Stale_imported_world_journal_kernel_digest_fails_closed()
    {
        using RepositoryFixture fixture = new(copyDependencies: true);
        fixture.Replace("tools/Evm/Lean/WorldJournalExtractor/Generated/WorldJournalKernel.source-manifest.json",
            "188c1a3d9a2f6c5cdb1a33b1446381b325e769910e8583470ccc5b5684e843a9",
            new string('0', 64));

        Assert.That(() => FrameJournalProfile.ValidateImportedKernelShape(fixture.Root),
            Throws.InstanceOf<ExtractionException>());
    }

    [Test]
    public void Dependency_theorem_signature_mutation_fails_closed()
    {
        using RepositoryFixture fixture = new(copyDependencies: true);
        fixture.Replace("tools/Evm/Lean/Eip803x/Evm/SelfDestruct.lean",
            "(input : Situation) :\n    (markedEffects input).destroyMarked",
            "(input : Situation) (_extra : Nat) :\n    (markedEffects input).destroyMarked");

        Assert.That(() => FrameJournalProfile.ValidateDependencyTheoremShape(fixture.Root),
            Throws.InstanceOf<ExtractionException>());
    }

    [Test]
    public void Dependency_identity_byte_mutation_fails_closed()
    {
        using RepositoryFixture fixture = new(copyDependencies: true);
        fixture.Append("tools/Evm/Lean/LogOpcodeExtractor/Generated/LogOpcodeKernel.source-manifest.json", " ");

        Assert.That(() => FrameJournalProfile.Extract(fixture.Root, fixture.Output),
            Throws.InstanceOf<ExtractionException>());
    }

    [TestCase("\"exitException\"", "\"exitExceptionMutated\"", TestName = "Rejects_ir_operation_mutation")]
    [TestCase("\"Amsterdam\"", "\"Legacy\"", TestName = "Rejects_ir_fork_mutation")]
    [TestCase("\"persistentOriginals\"", "null", TestName = "Rejects_ir_null_leaf")]
    public void Serialized_ir_mutations_fail_closed(string before, string after)
    {
        using Fixture fixture = new();
        ExtractionResult result = FrameJournalProfile.Extract(fixture.Root, fixture.Output);
        byte[] manifest = File.ReadAllBytes(result.ManifestPath);
        byte[] lean = File.ReadAllBytes(result.LeanPath);
        string ir = File.ReadAllText(result.IrPath).Replace(before, after, StringComparison.Ordinal);

        Assert.That(() => FrameJournalProfile.ValidateArtifacts(fixture.Root, manifest, Encoding.UTF8.GetBytes(ir), lean),
            Throws.InstanceOf<ExtractionException>());
    }

    [Test]
    public void Serialized_ir_rejects_omission_null_case_duplicate_and_leaf_array_mutations()
    {
        using Fixture fixture = new();
        ExtractionResult result = FrameJournalProfile.Extract(fixture.Root, fixture.Output);
        byte[] manifest = File.ReadAllBytes(result.ManifestPath);
        byte[] lean = File.ReadAllBytes(result.LeanPath);
        string ir = File.ReadAllText(result.IrPath);
        string[] mutants =
        [
            ir.Replace("  \"schemaVersion\": 1,\n", string.Empty, StringComparison.Ordinal),
            ReplaceJsonArray(ir, "operations", "null"),
            ir.Replace("\"extractorVersion\"", "\"ExtractorVersion\"", StringComparison.Ordinal),
            ir.Replace("  \"schemaVersion\": 1,", "  \"schemaVersion\": 1,\n  \"schemaVersion\": 1,", StringComparison.Ordinal),
            ReplaceJsonArray(ir, "orderedEffects", "[]"),
            ReplaceJsonArray(ir, "premises", "null"),
            ReplaceJsonArray(ir, "importedKernels", "null"),
        ];

        foreach (string mutant in mutants)
        {
            Assert.That(
                () => FrameJournalProfile.ValidateArtifacts(
                    fixture.Root, manifest, Encoding.UTF8.GetBytes(mutant), lean),
                Throws.InstanceOf<Exception>());
        }
    }

    [Test]
    public void Serialized_manifest_rejects_omission_null_case_duplicate_and_leaf_array_mutations()
    {
        using Fixture fixture = new();
        ExtractionResult result = FrameJournalProfile.Extract(fixture.Root, fixture.Output);
        byte[] ir = File.ReadAllBytes(result.IrPath);
        byte[] lean = File.ReadAllBytes(result.LeanPath);
        string manifest = File.ReadAllText(result.ManifestPath);
        string[] mutants =
        [
            manifest.Replace("  \"schemaVersion\": 1,\n", string.Empty, StringComparison.Ordinal),
            ReplaceJsonArray(manifest, "sources", "null"),
            manifest.Replace("\"extractorVersion\"", "\"ExtractorVersion\"", StringComparison.Ordinal),
            manifest.Replace("  \"schemaVersion\": 1,", "  \"schemaVersion\": 1,\n  \"schemaVersion\": 1,", StringComparison.Ordinal),
            ReplaceJsonArray(manifest, "members", "[]"),
            ReplaceJsonArray(manifest, "semanticBindings", "null"),
            ReplaceJsonArray(manifest, "importedKernels", "null"),
        ];

        foreach (string mutant in mutants)
        {
            Assert.That(
                () => FrameJournalProfile.ValidateArtifacts(
                    fixture.Root, Encoding.UTF8.GetBytes(mutant), ir, lean),
                Throws.InstanceOf<Exception>());
        }
    }

    [Test]
    public void Uppercase_manifest_digest_is_rejected()
    {
        using Fixture fixture = new();
        ExtractionResult result = FrameJournalProfile.Extract(fixture.Root, fixture.Output);
        string manifest = File.ReadAllText(result.ManifestPath);
        using JsonDocument parsed = JsonDocument.Parse(manifest);
        string digest = parsed.RootElement.GetProperty("combinedSourceSha256").GetString()!;
        byte[] mutated = Encoding.UTF8.GetBytes(manifest.Replace(digest, digest.ToUpperInvariant(), StringComparison.Ordinal));

        Assert.That(() => FrameJournalProfile.ValidateArtifacts(fixture.Root, mutated,
            File.ReadAllBytes(result.IrPath), File.ReadAllBytes(result.LeanPath)),
            Throws.InstanceOf<ExtractionException>());
    }

    [Test]
    public void Alternate_self_consistent_manifest_digest_is_rejected_against_sources()
    {
        using Fixture fixture = new();
        ExtractionResult result = FrameJournalProfile.Extract(fixture.Root, fixture.Output);
        byte[] ir = File.ReadAllBytes(result.IrPath);
        byte[] lean = File.ReadAllBytes(result.LeanPath);
        string manifest = File.ReadAllText(result.ManifestPath);
        int hashStart = manifest.IndexOf("\"sha256\": \"", StringComparison.Ordinal) + "\"sha256\": \"".Length;
        string alternate = ReplaceAt(manifest, hashStart, 64, new string('0', 64));
        string originalCombined = ReadStringProperty(manifest, "combinedSourceSha256");
        alternate = alternate.Replace(originalCombined, CalculateCombinedSourceHash(alternate), StringComparison.Ordinal);

        Assert.That(
            () => FrameJournalProfile.ValidateArtifacts(
                fixture.Root, Encoding.UTF8.GetBytes(alternate), ir, lean),
            Throws.InstanceOf<ExtractionException>());
    }

    [Test]
    public void Manifest_member_identities_are_complete_unique_and_lowercase()
    {
        using Fixture fixture = new();
        ExtractionResult result = FrameJournalProfile.Extract(fixture.Root, fixture.Output);
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(result.ManifestPath));
        JsonElement members = manifest.RootElement.GetProperty("members");
        string[] identities =
        [
            .. members.EnumerateArray().Select(member =>
                $"{member.GetProperty("sourcePath").GetString()}:" +
                $"{member.GetProperty("containingType").GetString()}:" +
                member.GetProperty("signature").GetString()),
        ];
        string[] digests =
        [
            .. members.EnumerateArray().Select(member => member.GetProperty("canonicalSha256").GetString()!),
        ];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(identities.Distinct(StringComparer.Ordinal).ToArray(), Has.Length.EqualTo(53));
            Assert.That(digests, Has.All.Match("^[0-9a-f]{64}$"));
            Assert.That(manifest.RootElement.GetProperty("sources").EnumerateArray()
                .Select(source => source.GetProperty("path").GetString()),
                Is.EqualTo(FrameJournalProfile.SourceRelativePaths));
        }
    }

    [TestCase("def transition", "def transitionMutated", TestName = "Rejects_generated_transition_name_mutation")]
    [TestCase("if isBodyOperation worldOperation then", "if !isBodyOperation worldOperation then",
        TestName = "Rejects_generated_body_gate_mutation")]
    [TestCase("| .enterCall => enter .call machine", "| .enterCall => enter (.create 0) machine",
        TestName = "Rejects_generated_call_entry_mutation")]
    [TestCase("| .exitSuccess => retainAndPop machine", "| .exitSuccess => restoreAndPop machine",
        TestName = "Rejects_generated_commit_to_restore_mutation")]
    [TestCase("| .enterCreate address => enter (.create address) machine", "| .enterCreate address => enter .call machine",
        TestName = "Rejects_generated_create_mark_mutation")]
    [TestCase("| .preFrameCallFailure => some machine", "| .preFrameCallFailure => none",
        TestName = "Rejects_generated_call_failure_stutter_mutation")]
    [TestCase("| .preFrameCreateFailure => some machine", "| .preFrameCreateFailure => none",
        TestName = "Rejects_generated_create_failure_stutter_mutation")]
    [TestCase("| .latchRipemdTouch => some { machine with ripemdTouchLatched := true }",
        "| .latchRipemdTouch => some machine",
        TestName = "Rejects_generated_ripemd_latch_mutation")]
    [TestCase("| some world => reapplyRipemdTouch { machine with world, frames }",
        "| some world => some { machine with world, frames }",
        TestName = "Rejects_generated_ripemd_replay_mutation")]
    [TestCase("if isEmptyAccount account then", "if !isEmptyAccount account then",
        TestName = "Rejects_generated_ripemd_empty_gate_mutation")]
    [TestCase("| .exitRevert => restoreAndPop machine", "| .exitRevert => retainAndPop machine",
        TestName = "Rejects_generated_revert_mutation")]
    [TestCase("| .exitException => restoreAndPop machine", "| .exitException => retainAndPop machine",
        TestName = "Rejects_generated_exception_mutation")]
    [TestCase("import FrameJournalExtractor.Specification.FrameJournalState",
        "import FrameJournalExtractor.Specification.FrameJournal",
        TestName = "Rejects_generated_handwritten_frame_import")]
    [TestCase("import WorldJournalExtractor.Generated.WorldJournalKernel",
        "import WorldJournalExtractor.Specification.WorldJournal",
        TestName = "Rejects_generated_handwritten_world_import")]
    public void Generated_semantic_mutations_fail_closed(string before, string after)
    {
        using Fixture fixture = new();
        ExtractionResult result = FrameJournalProfile.Extract(fixture.Root, fixture.Output);
        string lean = File.ReadAllText(result.LeanPath).Replace(before, after, StringComparison.Ordinal);

        Assert.That(() => FrameJournalProfile.ValidateArtifacts(fixture.Root,
            File.ReadAllBytes(result.ManifestPath), File.ReadAllBytes(result.IrPath), Encoding.UTF8.GetBytes(lean)),
            Throws.InstanceOf<ExtractionException>());
    }

    [TestCase("theorem injected : True := by trivial\n", TestName = "Rejects_generated_theorem")]
    [TestCase("lemma injected : True := by trivial\n", TestName = "Rejects_generated_lemma")]
    [TestCase("axiom injected : True\n", TestName = "Rejects_generated_axiom")]
    [TestCase("example : True := by trivial\n", TestName = "Rejects_generated_example")]
    [TestCase("def injected : True := by sorry\n", TestName = "Rejects_generated_sorry")]
    [TestCase("def injected : True := by admit\n", TestName = "Rejects_generated_admit")]
    public void Generated_proof_construct_mutations_fail_closed(string suffix)
    {
        using Fixture fixture = new();
        ExtractionResult result = FrameJournalProfile.Extract(fixture.Root, fixture.Output);
        byte[] mutant = Encoding.UTF8.GetBytes(File.ReadAllText(result.LeanPath) + suffix);

        Assert.That(() => FrameJournalProfile.ValidateArtifacts(fixture.Root,
            File.ReadAllBytes(result.ManifestPath), File.ReadAllBytes(result.IrPath), mutant),
            Throws.InstanceOf<ExtractionException>());
    }

    [Test]
    public void Noncanonical_encodings_are_rejected()
    {
        using Fixture fixture = new();
        ExtractionResult result = FrameJournalProfile.Extract(fixture.Root, fixture.Output);
        byte[] manifest = File.ReadAllBytes(result.ManifestPath);
        byte[] ir = File.ReadAllBytes(result.IrPath);
        byte[] lean = File.ReadAllBytes(result.LeanPath);

        foreach (byte[] mutated in NoncanonicalForms(ir))
        {
            Assert.That(() => FrameJournalProfile.ValidateArtifacts(fixture.Root, manifest, mutated, lean),
                Throws.InstanceOf<ExtractionException>());
        }
        foreach (byte[] mutated in NoncanonicalForms(manifest))
        {
            Assert.That(() => FrameJournalProfile.ValidateArtifacts(fixture.Root, mutated, ir, lean),
                Throws.InstanceOf<ExtractionException>());
        }
        foreach (byte[] mutated in NoncanonicalForms(lean))
        {
            Assert.That(() => FrameJournalProfile.ValidateArtifacts(fixture.Root, manifest, ir, mutated),
                Throws.InstanceOf<ExtractionException>());
        }
    }

    private static byte[][] NoncanonicalForms(byte[] canonical)
    {
        byte[] bom = [0xef, 0xbb, 0xbf, .. canonical];
        string text = Encoding.UTF8.GetString(canonical);
        byte[] crlf = Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\r\n"));
        byte[] missingFinalLf = canonical[..^1];
        return [bom, crlf, missingFinalLf];
    }

    private static int Count(string source, string value)
    {
        int count = 0;
        int cursor = 0;
        while ((cursor = source.IndexOf(value, cursor, StringComparison.Ordinal)) >= 0)
        {
            count++;
            cursor += value.Length;
        }
        return count;
    }

    private static string ReplaceJsonArray(string source, string propertyName, string replacement)
    {
        int property = source.IndexOf($"\"{propertyName}\":", StringComparison.Ordinal);
        if (property < 0) throw new InvalidOperationException($"JSON property {propertyName} was not found.");
        int start = source.IndexOf('[', property);
        if (start < 0) throw new InvalidOperationException($"JSON array {propertyName} was not found.");
        int depth = 0;
        bool quoted = false;
        bool escaped = false;
        for (int index = start; index < source.Length; index++)
        {
            char character = source[index];
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') quoted = false;
                continue;
            }
            if (character == '"') quoted = true;
            else if (character == '[') depth++;
            else if (character == ']' && --depth == 0)
                return source[..start] + replacement + source[(index + 1)..];
        }
        throw new InvalidOperationException($"JSON array {propertyName} was unterminated.");
    }

    private static string ReplaceAt(string source, int start, int length, string replacement) =>
        string.Concat(source.AsSpan(0, start), replacement, source.AsSpan(start + length));

    private static string ReadStringProperty(string json, string propertyName)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(propertyName).GetString()
            ?? throw new InvalidOperationException($"JSON property {propertyName} was null.");
    }

    private static string CalculateCombinedSourceHash(string manifest)
    {
        using JsonDocument document = JsonDocument.Parse(manifest);
        StringBuilder source = new();
        foreach (JsonElement item in document.RootElement.GetProperty("sources").EnumerateArray())
        {
            source.Append(item.GetProperty("path").GetString());
            source.Append('\0');
            source.Append(item.GetProperty("sha256").GetString());
            source.Append('\n');
        }
        if (source.Length != 0) source.Length--;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.ToString())));
    }

    private class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = FindRoot();
            Output = Path.Combine(@"D:\tmp", $"frame-journal-extractor-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Output);
        }

        public string Root { get; protected set; }
        public string Output { get; }

        public void Dispose() => Directory.Delete(Output, recursive: true);

        private static string FindRoot()
        {
            DirectoryInfo? current = new(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, FrameJournalProfile.VirtualMachinePath))) return current.FullName;
                current = current.Parent;
            }
            throw new InvalidOperationException("Could not locate the repository root.");
        }
    }

    private sealed class RepositoryFixture : Fixture
    {
        public RepositoryFixture(bool copyDependencies)
        {
            string sourceRoot = Root;
            Root = Path.Combine(Output, "repo");
            IEnumerable<string> paths = FrameJournalProfile.SourceRelativePaths;
            if (copyDependencies)
                paths = paths.Concat(FrameJournalProfile.DependencyRelativePaths);
            foreach (string relativePath in paths.Distinct(StringComparer.Ordinal))
            {
                string source = Path.Combine(sourceRoot, relativePath);
                string destination = Path.Combine(Root, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }
        }

        public void Replace(string path, string before, string after)
        {
            string fullPath = Path.Combine(Root, path);
            string source = File.ReadAllText(fullPath);
            string normalized = source.ReplaceLineEndings("\n");
            string normalizedBefore = before.ReplaceLineEndings("\n");
            string normalizedAfter = after.ReplaceLineEndings("\n");
            if (!normalized.Contains(normalizedBefore, StringComparison.Ordinal))
                throw new InvalidOperationException($"Fixture token not found in {path}: {before}");
            File.WriteAllText(fullPath, normalized.Replace(normalizedBefore, normalizedAfter, StringComparison.Ordinal),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        public void Append(string path, string value) =>
            File.AppendAllText(Path.Combine(Root, path), value, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
