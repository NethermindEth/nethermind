// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.TransactionProcessorExtractor.Test;

[TestFixture]
public class ExtractorTests
{
    [Test]
    public void Production_sources_extract_deterministically()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory first = new();
        using TemporaryDirectory second = new();
        ExtractionResult firstResult = Extractor.Extract(
            root,
            first.Path,
            System.IO.Path.Combine(first.Path, "TransactionProcessorLifecycle.lean"));
        ExtractionResult secondResult = Extractor.Extract(
            root,
            second.Path,
            System.IO.Path.Combine(second.Path, "TransactionProcessorLifecycle.lean"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstResult.SourceCount, Is.EqualTo(4));
            Assert.That(File.ReadAllBytes(firstResult.IrPath), Is.EqualTo(File.ReadAllBytes(secondResult.IrPath)));
            Assert.That(File.ReadAllBytes(firstResult.ManifestPath), Is.EqualTo(File.ReadAllBytes(secondResult.ManifestPath)));
            Assert.That(File.ReadAllBytes(firstResult.LeanPath), Is.EqualTo(File.ReadAllBytes(secondResult.LeanPath)));
        }
    }

    [Test]
    public void Checked_artifacts_match_fresh_extraction()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory temporary = new();
        ExtractionResult result = Extractor.Extract(
            root,
            temporary.Path,
            System.IO.Path.Combine(temporary.Path, "TransactionProcessorLifecycle.lean"));
        string checkedDirectory = System.IO.Path.Combine(
            root,
            "tools/Evm/Lean/TransactionProcessorExtractor/Generated");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                File.ReadAllBytes(result.IrPath),
                Is.EqualTo(File.ReadAllBytes(System.IO.Path.Combine(
                    checkedDirectory,
                    "TransactionProcessorLifecycle.ir.json"))));
            Assert.That(
                File.ReadAllBytes(result.ManifestPath),
                Is.EqualTo(File.ReadAllBytes(System.IO.Path.Combine(
                    checkedDirectory,
                    "TransactionProcessorLifecycle.source-manifest.json"))));
            Assert.That(
                File.ReadAllBytes(result.LeanPath),
                Is.EqualTo(File.ReadAllBytes(System.IO.Path.Combine(
                    checkedDirectory,
                    "TransactionProcessorLifecycle.lean"))));
        }
    }

    [Test]
    public void Generated_Lean_contains_definitions_only_and_explicit_external_boundary()
    {
        string root = FindRepoRoot();
        string generated = File.ReadAllText(System.IO.Path.Combine(
            root,
            "tools/Evm/Lean/TransactionProcessorExtractor/Generated/TransactionProcessorLifecycle.lean"));
        string ir = File.ReadAllText(System.IO.Path.Combine(
            root,
            "tools/Evm/Lean/TransactionProcessorExtractor/Generated/TransactionProcessorLifecycle.ir.json"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(generated, Does.Not.Contain("theorem "));
            Assert.That(generated, Does.Contain("def runPrefix"));
            Assert.That(generated, Does.Contain("def finalize"));
            Assert.That(ir, Does.Contain("externalObligations"));
            Assert.That(ir, Does.Contain("snapshot, rollback, commit"));
        }
    }

    [Test]
    public void Serialized_ir_rejects_malformed_unknown_and_nested_nulls()
    {
        byte[] sourceDerived = ReadCheckedIr();

        JsonObject unknownRoot = ParseSerializedIr(sourceDerived);
        unknownRoot["unexpected"] = JsonValue.Create(true);
        JsonObject unknownFinalization = ParseSerializedIr(sourceDerived);
        unknownFinalization["lifecycle"]!.AsObject()["finalization"]!.AsObject()["unexpected"] = JsonValue.Create(true);
        JsonObject casedRoot = ParseSerializedIr(sourceDerived);
        casedRoot["SchemaVersion"] = casedRoot["schemaVersion"]!.DeepClone();
        casedRoot.Remove("schemaVersion");
        JsonObject casedNested = ParseSerializedIr(sourceDerived);
        Rename(casedNested["lifecycle"]!.AsObject(), "restoreFormula", "RestoreFormula");
        JsonObject missingOption = ParseSerializedIr(sourceDerived);
        missingOption["options"]!.AsObject().Remove("none");
        JsonObject nullKernel = ParseSerializedIr(sourceDerived);
        nullKernel["kernel"] = null;
        JsonObject nullOptions = ParseSerializedIr(sourceDerived);
        nullOptions["options"] = null;
        JsonObject nullRegistration = ParseSerializedIr(sourceDerived);
        nullRegistration["reachability"]!.AsObject()["registration"] = null;
        JsonObject nullLifecycle = ParseSerializedIr(sourceDerived);
        nullLifecycle["lifecycle"] = null;
        JsonObject nullAdmissionStage = ParseSerializedIr(sourceDerived);
        nullAdmissionStage["lifecycle"]!.AsObject()["admissionStages"]!.AsArray()[0] = null;
        JsonObject nullStageField = ParseSerializedIr(sourceDerived);
        nullStageField["lifecycle"]!.AsObject()["admissionStages"]!.AsArray()[0]!.AsObject()["stage"] = null;
        JsonObject nullEdgeBefore = ParseSerializedIr(sourceDerived);
        nullEdgeBefore["lifecycle"]!.AsObject()["evmCallPartialOrder"]!.AsArray()[0]!.AsObject()["before"] = null;
        JsonObject nullFinalizationValue = ParseSerializedIr(sourceDerived);
        nullFinalizationValue["lifecycle"]!.AsObject()["finalization"] = null;
        JsonObject nullObligation = ParseSerializedIr(sourceDerived);
        nullObligation["externalObligations"]!.AsArray()[0] = null;

        AssertSerializedIrRejected(sourceDerived, Encoding.UTF8.GetBytes("{"), "not valid JSON");
        AssertSerializedIrRejected(sourceDerived, Encoding.UTF8.GetBytes("null"), "empty");
        AssertSerializedIrRejected(sourceDerived, SerializeSerializedIr(unknownRoot), "not valid JSON");
        AssertSerializedIrRejected(sourceDerived, SerializeSerializedIr(unknownFinalization), "not valid JSON");
        AssertSerializedIrRejected(sourceDerived, SerializeSerializedIr(casedRoot), "not valid JSON");
        AssertSerializedIrRejected(sourceDerived, SerializeSerializedIr(casedNested), "not valid JSON");
        AssertSerializedIrRejected(sourceDerived, SerializeSerializedIr(missingOption), "not valid JSON");
        AssertSerializedIrRejected(sourceDerived, SerializeSerializedIr(nullKernel), "Kernel was null");
        AssertSerializedIrRejected(sourceDerived, SerializeSerializedIr(nullOptions), "Options was null");
        AssertSerializedIrRejected(sourceDerived, SerializeSerializedIr(nullRegistration), "Reachability.Registration was null");
        AssertSerializedIrRejected(sourceDerived, SerializeSerializedIr(nullLifecycle), "Lifecycle was null");
        AssertSerializedIrRejected(sourceDerived, SerializeSerializedIr(nullAdmissionStage), "Lifecycle.AdmissionStages[0] was null");
        AssertSerializedIrRejected(sourceDerived, SerializeSerializedIr(nullStageField), "Lifecycle.AdmissionStages[0].Stage was null");
        AssertSerializedIrRejected(sourceDerived, SerializeSerializedIr(nullEdgeBefore), "Lifecycle.EvmCallPartialOrder[0].Before was null");
        AssertSerializedIrRejected(sourceDerived, SerializeSerializedIr(nullFinalizationValue), "Lifecycle.Finalization was null");
        AssertSerializedIrRejected(sourceDerived, SerializeSerializedIr(nullObligation), "ExternalObligations[0] was null");
        AssertSerializedIrRejected(sourceDerived, null!, "input was null");
    }

    [Test]
    public void Serialized_ir_binds_options_reachability_lifecycle_and_obligation_claims()
    {
        byte[] sourceDerived = ReadCheckedIr();

        AssertSerializedIrMutationRejected(sourceDerived,
            static root => root["options"]!.AsObject()["commit"] = JsonValue.Create(9),
            "Options.Commit");
        AssertSerializedIrMutationRejected(sourceDerived,
            static root => root["reachability"]!.AsObject()["service"] = JsonValue.Create("arbitrary"),
            "Reachability.Service");
        AssertSerializedIrMutationRejected(sourceDerived,
            static root => root["reachability"]!.AsObject()["registration"]!.AsObject()["startLine"] = JsonValue.Create(0),
            "Reachability.Registration.StartLine");
        AssertSerializedIrMutationRejected(sourceDerived,
            static root => root["lifecycle"]!.AsObject()["restoreFormula"] = JsonValue.Create("arbitrary"),
            "Lifecycle.RestoreFormula");
        AssertSerializedIrMutationRejected(sourceDerived,
            static root => root["lifecycle"]!.AsObject()["admissionStages"]!.AsArray()[0]!.AsObject()["canonicalSyntax"] = JsonValue.Create("arbitrary"),
            "Lifecycle.AdmissionStages[0].CanonicalSyntax");
        AssertSerializedIrMutationRejected(sourceDerived,
            static root => root["lifecycle"]!.AsObject()["evmCallPartialOrder"]!.AsArray()[0]!.AsObject()["before"]!.AsObject()["stage"] = JsonValue.Create("arbitrary"),
            "Lifecycle.EvmCallPartialOrder[0].Before.Stage");
        AssertSerializedIrMutationRejected(sourceDerived,
            static root => root["lifecycle"]!.AsObject()["methodFingerprints"]!.AsArray()[0]!.AsObject()["sha256"] = JsonValue.Create("arbitrary"),
            "Lifecycle.MethodFingerprints[0].Sha256");
        AssertSerializedIrMutationRejected(sourceDerived,
            static root => root["lifecycle"]!.AsObject()["methodFingerprints"]!.AsArray()[0]!.AsObject()["key"] = JsonValue.Create("arbitrary"),
            "not path/namespace/owner-qualified");
        AssertSerializedIrMutationRejected(sourceDerived,
            static root => root["lifecycle"]!.AsObject()["methodFingerprints"]!.AsArray()[1] =
                root["lifecycle"]!.AsObject()["methodFingerprints"]!.AsArray()[0]!.DeepClone(),
            "duplicate method identity");
        AssertSerializedIrMutationRejected(sourceDerived,
            static root => root["lifecycle"]!.AsObject()["finalization"]!.AsObject()["warmupWriteGate"] = JsonValue.Create("arbitrary"),
            "Lifecycle.Finalization.WarmupWriteGate");
        AssertSerializedIrMutationRejected(sourceDerived,
            static root => root["lifecycle"]!.AsObject()["finalization"]!.AsObject()["branchPriority"]!.AsArray()[0] = JsonValue.Create("arbitrary"),
            "Lifecycle.Finalization.BranchPriority[0]");
        AssertSerializedIrMutationRejected(sourceDerived,
            static root => root["lifecycle"]!.AsObject()["finalization"]!.AsObject()["anchors"]!.AsArray()[0]!.AsObject()["stage"] = JsonValue.Create("arbitrary"),
            "Lifecycle.Finalization.Anchors[0].Stage");
        AssertSerializedIrMutationRejected(sourceDerived,
            static root => root["externalObligations"]!.AsArray()[0] = JsonValue.Create("arbitrary"),
            "ExternalObligations[0]");
        AssertSerializedIrMutationRejected(sourceDerived,
            static root => root["lifecycle"]!.AsObject()["rejectionGates"] = new JsonArray(),
            "Lifecycle.RejectionGates.Length");
        AssertSerializedIrMutationRejected(sourceDerived,
            static root => root["lifecycle"]!.AsObject()["evmCallPartialOrder"] = new JsonArray(),
            "Lifecycle.EvmCallPartialOrder.Length");
    }

    [Test]
    public void Every_serialized_ir_scalar_coordinate_is_bound()
    {
        byte[] sourceDerived = ReadCheckedIr();
        JsonObject original = ParseSerializedIr(sourceDerived);
        string[] scalarPaths = EnumerateScalarPaths(original).ToArray();

        Assert.That(scalarPaths, Is.Not.Empty);
        foreach (string path in scalarPaths)
        {
            JsonObject candidate = ParseSerializedIr(sourceDerived);
            MutateScalarAtPath(candidate, path);
            AssertSerializedIrRejected(sourceDerived, SerializeSerializedIr(candidate), string.Empty);
        }
    }

    [Test]
    public void Serialized_manifest_strictly_binds_build_sources_admissions_artifacts_and_semantics()
    {
        byte[] sourceDerived = ReadCheckedManifest();
        JsonObject unknownRoot = ParseSerializedIr(sourceDerived);
        unknownRoot["unexpected"] = true;
        JsonObject unknownNested = ParseSerializedIr(sourceDerived);
        unknownNested["sources"]!.AsArray()[0]!.AsObject()["unexpected"] = true;
        JsonObject casedRoot = ParseSerializedIr(sourceDerived);
        Rename(casedRoot, "schemaVersion", "SchemaVersion");
        JsonObject casedNested = ParseSerializedIr(sourceDerived);
        Rename(casedNested["admissions"]!.AsArray()[0]!.AsObject(), "key", "Key");
        JsonObject omittedTop = ParseSerializedIr(sourceDerived);
        omittedTop.Remove("schemaVersion");
        JsonObject omittedNested = ParseSerializedIr(sourceDerived);
        omittedNested["sources"]!.AsArray()[0]!.AsObject().Remove("path");
        JsonObject nullSource = ParseSerializedIr(sourceDerived);
        nullSource["sources"]!.AsArray()[0] = null;
        JsonObject nullAdmission = ParseSerializedIr(sourceDerived);
        nullAdmission["admissions"]!.AsArray()[0] = null;
        JsonObject nullArtifact = ParseSerializedIr(sourceDerived);
        nullArtifact["ir"] = null;
        JsonObject nullBinding = ParseSerializedIr(sourceDerived);
        nullBinding["semanticBindings"]!.AsArray()[0] = null;

        AssertSerializedManifestRejected(sourceDerived, "{"u8.ToArray(), "not valid JSON");
        AssertSerializedManifestRejected(sourceDerived, "null"u8.ToArray(), "empty");
        AssertSerializedManifestRejected(sourceDerived, Encoding.UTF8.GetBytes(unknownRoot.ToJsonString()), "not valid JSON");
        AssertSerializedManifestRejected(sourceDerived, Encoding.UTF8.GetBytes(unknownNested.ToJsonString()), "not valid JSON");
        AssertSerializedManifestRejected(sourceDerived, Encoding.UTF8.GetBytes(casedRoot.ToJsonString()), "not valid JSON");
        AssertSerializedManifestRejected(sourceDerived, Encoding.UTF8.GetBytes(casedNested.ToJsonString()), "not valid JSON");
        AssertSerializedManifestRejected(sourceDerived, Encoding.UTF8.GetBytes(omittedTop.ToJsonString()), "not valid JSON");
        AssertSerializedManifestRejected(sourceDerived, Encoding.UTF8.GetBytes(omittedNested.ToJsonString()), "not valid JSON");
        AssertSerializedManifestRejected(sourceDerived, Encoding.UTF8.GetBytes(nullSource.ToJsonString()), "null collections or entries");
        AssertSerializedManifestRejected(sourceDerived, Encoding.UTF8.GetBytes(nullAdmission.ToJsonString()), "null collections or entries");
        AssertSerializedManifestRejected(sourceDerived, Encoding.UTF8.GetBytes(nullArtifact.ToJsonString()), "null collections or entries");
        AssertSerializedManifestRejected(sourceDerived, Encoding.UTF8.GetBytes(nullBinding.ToJsonString()), "null collections or entries");
        AssertSerializedManifestRejected(sourceDerived, null!, "input was null");

        Action<JsonObject>[] mutations =
        [
            root => root["schemaVersion"] = 2,
            root => root["extractorVersion"] = "changed",
            root => root["compilerVersion"] = "changed",
            root => root["languageVersion"] = "changed",
            root => root["ancestorBaselineCommit"] = "changed",
            root => root["sourceIdentityAuthority"] = "changed",
            root => root["kernel"] = "changed",
            root => root["sources"]!.AsArray()[0]!["path"] = "changed",
            root => root["sources"]!.AsArray()[0]!["sha256"] = new string('0', 64),
            root => root["admissions"]!.AsArray()[0]!["key"] = "changed",
            root => root["admissions"]!.AsArray()[0]!["sourceSha256"] = new string('0', 64),
            root => root["ir"]!["path"] = "changed",
            root => root["ir"]!["sha256"] = new string('0', 64),
            root => root["lean"]!["path"] = "changed",
            root => root["lean"]!["sha256"] = new string('0', 64),
            root => root["combinedSha256"] = new string('0', 64),
            root => root["semanticBindings"]!.AsArray()[0] = "changed",
        ];
        foreach (Action<JsonObject> mutation in mutations)
        {
            JsonObject candidate = ParseSerializedIr(sourceDerived);
            mutation(candidate);
            AssertSerializedManifestRejected(sourceDerived, Encoding.UTF8.GetBytes(candidate.ToJsonString()), string.Empty);
        }

        JsonObject duplicate = ParseSerializedIr(sourceDerived);
        duplicate["admissions"]!.AsArray()[1] = duplicate["admissions"]!.AsArray()[0]!.DeepClone();
        AssertSerializedManifestRejected(sourceDerived, Encoding.UTF8.GetBytes(duplicate.ToJsonString()),
            "Duplicate owner-qualified lifecycle admission identity");
    }

    [Test]
    public void Admission_order_rejects_metrics_moved_before_effective_price()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.TransactionProcessorPath,
            "            UInt256 effectiveGasPrice = CalculateEffectiveGasPrice(tx, spec.IsEip1559Enabled, header.BaseFeePerGas, out UInt256 opcodeGasPrice);\n\n" +
            "            UpdateMetrics(opts, effectiveGasPrice);",
            "            UpdateMetrics(opts, UInt256.Zero);\n\n" +
            "            UInt256 effectiveGasPrice = CalculateEffectiveGasPrice(tx, spec.IsEip1559Enabled, header.BaseFeePerGas, out UInt256 opcodeGasPrice);");

        AssertRejected(fixture, "ordinary Execute admission and route order changed");
    }

    [Test]
    public void Admission_rejects_sender_gate_reordering()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.TransactionProcessorPath,
            "if (!(result = ValidateSender(tx, header, spec, tracer, opts)) ||",
            "if (!(result = IncrementNonce(tx, header, spec, tracer, opts)) ||");

        AssertRejected(fixture, "ValidateSender call");
    }

    [Test]
    public void Admission_rejects_restore_cleanup_mutation()
    {
        using Fixture fixture = new();
        fixture.ReplaceOccurrence(
            Extractor.TransactionProcessorPath,
            "WorldState.Reset(resetBlockChanges: false);",
            "WorldState.Reset(resetBlockChanges: true);",
            0);

        AssertRejected(fixture, "Restore-mode cleanup");
    }

    [Test]
    public void Admission_rejects_commit_formula_mutation()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.TransactionProcessorPath,
            "opts.HasFlag(ExecutionOptions.Commit) || (!opts.HasFlag(ExecutionOptions.SkipValidation) && !spec.IsEip658Enabled)",
            "opts.HasFlag(ExecutionOptions.Commit) || (!opts.HasFlag(ExecutionOptions.SkipValidation) || !spec.IsEip658Enabled)");

        AssertRejected(fixture, "effective commit projection");
    }

    [Test]
    public void Admission_rejects_commit_before_condition_mutation()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.TransactionProcessorPath,
            "commit && (simpleTransferRecipient is null || restore || tracer.IsTracingState)",
            "commit && (simpleTransferRecipient is null || restore) ");

        AssertRejected(fixture, "pre-execution commit condition");
    }

    [Test]
    public void Simple_tail_rejects_missing_refund_anchor()
    {
        using Fixture fixture = new();
        fixture.ReplaceOccurrence(
            Extractor.TransactionProcessorPath,
            "GasConsumed spentGas = Refund(tx, header, spec, opts, in substate, in gasAvailable",
            "GasConsumed spentGas = RefundMutated(tx, header, spec, opts, in substate, in gasAvailable",
            0);

        AssertRejected(fixture, "ExecuteSimpleTransfer must contain exactly one admitted Refund call");
    }

    [Test]
    public void Evm_call_rejects_rollback_removed_after_vm()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.TransactionProcessorPath,
            "                    WorldState.Restore(snapshot);\n                    VirtualMachineStatics.RestoreRipemdTouch",
            "                    WorldState.Reset(resetBlockChanges: false);\n                    VirtualMachineStatics.RestoreRipemdTouch");

        AssertRejected(fixture, "revert-or-error branch must restore the admitted top-level snapshot");
    }

    [Test]
    public void Evm_tail_rejects_uncalled_local_delegation_anchor()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.TransactionProcessorPath,
            "            VirtualMachine.SetTxExecutionContext(new(tx.SenderAddress!, _codeInfoRepository, tx.BlobVersionedHashes, in opcodeGasPrice));",
            "            void ShadowDelegationAnchor() => ProcessDelegations();\n\n" +
            "            VirtualMachine.SetTxExecutionContext(new(tx.SenderAddress!, _codeInfoRepository, tx.BlobVersionedHashes, in opcodeGasPrice));");
        fixture.Replace(
            Extractor.TransactionProcessorPath,
            "if (!ProcessDelegations(tx, spec, accessTracker, ref gasAvailable, ref executionIntrinsicGasStandard, out delegationRefunds))",
            "if (!ProcessDelegationsRemoved(tx, spec, accessTracker, ref gasAvailable, ref executionIntrinsicGasStandard, out delegationRefunds))");

        AssertRejected(fixture, "ProcessDelegations call");
    }

    [Test]
    public void Evm_call_rejects_uncalled_local_rollback_anchor()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.TransactionProcessorPath,
            "                    WorldState.Restore(snapshot);\n                    VirtualMachineStatics.RestoreRipemdTouch",
            "                    void ShadowRollbackAnchor() => WorldState.Restore(snapshot);\n" +
            "                    VirtualMachineStatics.RestoreRipemdTouch");

        AssertRejected(fixture, "revert-or-error branch must restore the admitted top-level snapshot");
    }

    [Test]
    public void Evm_tail_rejects_unadmitted_early_return()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.TransactionProcessorPath,
            "            VirtualMachine.SetTxExecutionContext(new(tx.SenderAddress!, _codeInfoRepository, tx.BlobVersionedHashes, in opcodeGasPrice));",
            "            if (false) return TransactionResult.Ok;\n\n" +
            "            VirtualMachine.SetTxExecutionContext(new(tx.SenderAddress!, _codeInfoRepository, tx.BlobVersionedHashes, in opcodeGasPrice));");

        AssertRejected(fixture, "method-token fingerprints changed");
    }

    [Test]
    public void Finalization_rejects_warmup_write_gate_mutation()
    {
        using Fixture fixture = new();
        fixture.ReplaceOccurrence(
            Extractor.TransactionProcessorPath,
            "if (!opts.HasFlag(ExecutionOptions.Warmup))",
            "if (opts.HasFlag(ExecutionOptions.Warmup))",
            0);

        AssertRejected(fixture, "warmup gas-observation gates");
    }

    [Test]
    public void Finalization_rejects_restore_priority_mutation()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.TransactionProcessorPath,
            "            if (restore)\n            {\n                WorldState.Reset(resetBlockChanges: false);\n                if (deleteCallerAccount)",
            "            if (commit)\n            {\n                WorldState.Reset(resetBlockChanges: false);\n                if (deleteCallerAccount)");

        AssertRejected(fixture, "restore/commit/reset branch priority");
    }

    [Test]
    public void Finalization_rejects_nonce_restore_omission()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.TransactionProcessorPath,
            "                    DecrementNonce(tx);",
            "                    _ = tx;");

        AssertRejected(fixture, "decrement the sender nonce");
    }

    [Test]
    public void Finalization_rejects_receipt_state_root_gate_mutation()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.TransactionProcessorPath,
            "if (!spec.IsEip658Enabled)\n                {\n                    WorldState.RecalculateStateRoot();",
            "if (spec.IsEip658Enabled)\n                {\n                    WorldState.RecalculateStateRoot();");

        AssertRejected(fixture, "Receipt state-root projection");
    }

    [Test]
    public void Finalization_rejects_failure_output_leak()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.TransactionProcessorPath,
            "substate.ShouldRevert ? substate.Output.AsReadOnlyArray() : []",
            "true ? substate.Output.AsReadOnlyArray() : []");

        AssertRejected(fixture, "returndata only for REVERT");
    }

    [Test]
    public void Finalization_rejects_result_status_coupling()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.TransactionProcessorPath,
            "return substate.EvmExceptionType != EvmExceptionType.None",
            "return statusCode == StatusCode.Failure");

        AssertRejected(fixture, "result must remain projected from EvmExceptionType");
    }

    [Test]
    public void Standard_reachability_rejects_alternate_gas_policy()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.TransactionProcessorPath,
            "TransactionProcessorBase<EthereumGasPolicy>(blobBaseFeeCalculator",
            "TransactionProcessorBase<AlternativeGasPolicy>(blobBaseFeeCalculator");

        AssertRejected(fixture, "direct EthereumGasPolicy specialization");
    }

    [Test]
    public void Standard_reachability_rejects_registration_replacement()
    {
        using Fixture fixture = new();
        fixture.Replace(
            Extractor.MainnetDiPath,
            ".AddScoped<ITransactionProcessor, EthereumTransactionProcessor>()",
            ".AddScoped<ITransactionProcessor, SystemTransactionProcessor>()");

        AssertRejected(fixture, "scoped standard ITransactionProcessor registration");
    }

    private static void AssertRejected(Fixture fixture, string expectedMessage)
    {
        ExtractionException exception = Assert.Throws<ExtractionException>(() => fixture.Extract())!;
        Assert.That(exception.Message, Does.Contain(expectedMessage));
    }

    private static void AssertSerializedIrMutationRejected(
        byte[] sourceDerived,
        Action<JsonObject> mutation,
        string expectedMessage)
    {
        JsonObject candidate = ParseSerializedIr(sourceDerived);
        mutation(candidate);
        AssertSerializedIrRejected(sourceDerived, SerializeSerializedIr(candidate), expectedMessage);
    }

    private static void AssertSerializedIrRejected(byte[] sourceDerived, byte[] candidate, string expectedMessage)
    {
        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => Extractor.ValidateSerializedIr(sourceDerived, candidate))!;
        Assert.That(exception.Message, Does.Contain(expectedMessage));
    }

    private static void AssertSerializedManifestRejected(byte[] sourceDerived, byte[] candidate, string expectedMessage)
    {
        ExtractionException exception = Assert.Throws<ExtractionException>(
            () => Extractor.ValidateSerializedManifest(sourceDerived, candidate))!;
        Assert.That(exception.Message, Does.Contain(expectedMessage));
    }

    private static byte[] ReadCheckedIr() => File.ReadAllBytes(System.IO.Path.Combine(
        FindRepoRoot(),
        "tools/Evm/Lean/TransactionProcessorExtractor/Generated/TransactionProcessorLifecycle.ir.json"));

    private static byte[] ReadCheckedManifest() => File.ReadAllBytes(System.IO.Path.Combine(
        FindRepoRoot(),
        "tools/Evm/Lean/TransactionProcessorExtractor/Generated/TransactionProcessorLifecycle.source-manifest.json"));

    private static JsonObject ParseSerializedIr(byte[] bytes) => JsonNode.Parse(Encoding.UTF8.GetString(bytes))!.AsObject();

    private static byte[] SerializeSerializedIr(JsonObject ir) => Encoding.UTF8.GetBytes(ir.ToJsonString(new JsonSerializerOptions
    {
        WriteIndented = true,
    }));

    private static void Rename(JsonObject value, string from, string to)
    {
        value[to] = value[from]!.DeepClone();
        value.Remove(from);
    }

    private static IEnumerable<string> EnumerateScalarPaths(JsonNode node, string prefix = "")
    {
        if (node is JsonObject value)
        {
            foreach ((string property, JsonNode? child) in value)
            {
                if (child is null) continue;
                string path = string.IsNullOrEmpty(prefix) ? property : $"{prefix}/{property}";
                foreach (string nested in EnumerateScalarPaths(child, path)) yield return nested;
            }
        }
        else if (node is JsonArray array)
        {
            for (int index = 0; index < array.Count; index++)
            {
                if (array[index] is not JsonNode child) continue;
                string path = $"{prefix}/{index}";
                foreach (string nested in EnumerateScalarPaths(child, path)) yield return nested;
            }
        }
        else if (node is JsonValue)
        {
            yield return prefix;
        }
    }

    private static void MutateScalarAtPath(JsonObject root, string path)
    {
        string[] segments = path.Split('/');
        JsonNode current = root;
        for (int index = 0; index < segments.Length - 1; index++)
        {
            current = current is JsonObject value
                ? value[segments[index]]!
                : current.AsArray()[int.Parse(segments[index], NumberStyles.None, CultureInfo.InvariantCulture)]!;
        }

        string final = segments[^1];
        JsonValue scalar = current is JsonObject parentObject
            ? parentObject[final]!.AsValue()
            : current.AsArray()[int.Parse(final, NumberStyles.None, CultureInfo.InvariantCulture)]!.AsValue();
        JsonNode replacement;
        if (scalar.TryGetValue<bool>(out bool boolean)) replacement = JsonValue.Create(!boolean);
        else if (scalar.TryGetValue<int>(out int integer)) replacement = JsonValue.Create(integer + 1);
        else replacement = JsonValue.Create(scalar.GetValue<string>() + "#");

        if (current is JsonObject targetObject) targetObject[final] = replacement;
        else current.AsArray()[int.Parse(final, NumberStyles.None, CultureInfo.InvariantCulture)] = replacement;
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, Extractor.TransactionProcessorPath)))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Nethermind repository root.");
    }

    private sealed class Fixture : IDisposable
    {
        private static readonly string[] SourcePaths =
        [
            Extractor.TransactionProcessorPath,
            Extractor.OptionsPath,
            Extractor.GasPolicyPath,
            Extractor.MainnetDiPath,
        ];

        public Fixture()
        {
            Root = System.IO.Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "transaction-processor-extractor-fixtures",
                Guid.NewGuid().ToString("N"));
            Output = System.IO.Path.Combine(Root, "generated");
            string productionRoot = FindRepoRoot();
            foreach (string relativePath in SourcePaths)
            {
                Write(relativePath, File.ReadAllText(System.IO.Path.Combine(productionRoot, relativePath)));
            }
        }

        public string Root { get; }

        public string Output { get; }

        public void Extract() => Extractor.Extract(
            Root,
            Output,
            System.IO.Path.Combine(Output, "TransactionProcessorLifecycle.lean"));

        public void Replace(string relativePath, string original, string replacement)
        {
            string source = Read(relativePath);
            Assert.That(source, Does.Contain(original));
            Write(relativePath, source.Replace(original, replacement, StringComparison.Ordinal));
        }

        public void ReplaceOccurrence(string relativePath, string original, string replacement, int occurrence)
        {
            string source = Read(relativePath);
            int start = -1;
            for (int index = 0; index <= occurrence; index++)
            {
                start = source.IndexOf(original, start + 1, StringComparison.Ordinal);
                Assert.That(start, Is.GreaterThanOrEqualTo(0));
            }
            Write(relativePath, source.Remove(start, original.Length).Insert(start, replacement));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private string Read(string relativePath) => File.ReadAllText(System.IO.Path.Combine(Root, relativePath));

        private void Write(string relativePath, string source)
        {
            string path = System.IO.Path.Combine(Root, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "transaction-processor-extractor-output",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
