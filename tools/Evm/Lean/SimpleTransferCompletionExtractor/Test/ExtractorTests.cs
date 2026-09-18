// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.SimpleTransferCompletionExtractor.Test;

[TestFixture]
public sealed class ExtractorTests
{
    [Test]
    public void Production_sources_extract_deterministically()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory first = new();
        using TemporaryDirectory second = new();
        ExtractionResult a = Extractor.Extract(root, first.Location, Path.Combine(first.Location, "first.lean"));
        ExtractionResult b = Extractor.Extract(root, second.Location, Path.Combine(second.Location, "second.lean"));
        Assert.Multiple(() =>
        {
            Assert.That(a.SourceCount, Is.EqualTo(63));
            Assert.That(a.OperationCount, Is.EqualTo(15));
            Assert.That(a.EffectCount, Is.EqualTo(19));
            Assert.That(File.ReadAllBytes(a.IrPath), Is.EqualTo(File.ReadAllBytes(b.IrPath)));
            Assert.That(File.ReadAllBytes(a.ManifestPath), Is.EqualTo(File.ReadAllBytes(b.ManifestPath)));
            Assert.That(File.ReadAllBytes(a.LeanPath), Is.EqualTo(File.ReadAllBytes(b.LeanPath)));
        });
    }

    [Test]
    public void Checked_in_artifacts_match_fresh_extraction()
    {
        string root = FindRepoRoot();
        string package = Path.Combine(root, "tools", "Evm", "Lean", "SimpleTransferCompletionExtractor");
        string generated = Path.Combine(package, "Generated");
        Assert.That(() => Extractor.ValidateExistingArtifacts(root, generated, Path.Combine(generated, "SimpleTransferCompletion.lean")), Throws.Nothing);
    }

    [Test]
    public void Generated_model_is_theorem_free_and_keeps_receipt_input()
    {
        string root = FindRepoRoot();
        string package = Path.Combine(root, "tools", "Evm", "Lean", "SimpleTransferCompletionExtractor");
        string generated = File.ReadAllText(Path.Combine(package, "Generated", "SimpleTransferCompletion.lean"));
        Assert.Multiple(() =>
        {
            Assert.That(generated, Does.Contain("structure ReceiptContinuationInput"));
            Assert.That(generated, Does.Contain("structure SimpleComplete"));
            Assert.That(generated, Does.Contain("def run (input : CompletionInput)"));
            Assert.That(generated, Does.Contain("Metrics"), "source metadata should retain metric binding");
            Assert.That(generated, Does.Contain("sourceNewAccountPredicate"));
            Assert.That(generated, Does.Contain("sourceRecipientPredicate"));
            Assert.That(generated, Does.Contain("structure SourceOperationSpec"));
            Assert.That(generated, Does.Contain("sourceOperationAvailable"));
            Assert.That(generated, Does.Contain("sourceAcceptanceState"));
            Assert.That(generated, Does.Contain("request-boundary-only"));
            Assert.That(generated, Does.Contain("typedAst="));
            Assert.That(generated, Does.Contain("def transferDataFor"));
            Assert.That(generated, Does.Contain("def addressHashProjection"));
            Assert.That(generated, Does.Contain("topics := [sourceTransferSignature, addressHashProjection input.handoff.tx.sender, addressHashProjection input.handoff.recipient]"));
            Assert.That(generated, Does.Contain("normalReturn : Bool"));
            Assert.That(generated, Does.Not.Contain("accessObservation : AccessObservation"));
            Assert.That(generated, Does.Not.Contain("transferLog : TransferLog"));
            Assert.That(generated, Does.Not.Contain("TransferPayloadAdapter"));
            Assert.That(generated, Does.Not.Contain("fromTopic"));
            Assert.That(generated, Does.Not.Contain("toTopic"));
            Assert.That(generated, Does.Not.Contain("theorem "));
            Assert.That(generated, Does.Not.Contain("axiom "));
            Assert.That(generated, Does.Not.Contain("sorry"));
            Assert.That(generated.Split('\n').Any(line => line.TrimStart().StartsWith("admit ", StringComparison.Ordinal)), Is.False);
        });
    }

    [Test]
    public void Ir_keeps_completion_order_and_adapter_boundary()
    {
        string root = FindRepoRoot();
        string path = Path.Combine(root, "tools", "Evm", "Lean", "SimpleTransferCompletionExtractor", "Generated", "SimpleTransferCompletion.ir.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 1024 });
        JsonElement completion = document.RootElement.GetProperty("completion");
        string[] stages = completion.GetProperty("stages").EnumerateArray().Select(stage => stage.GetProperty("id").GetString()!).ToArray();
        string[] effects = completion.GetProperty("effects").EnumerateArray().Select(effect => effect.GetProperty("id").GetString()!).ToArray();
        JsonElement operations = completion.GetProperty("operations");
        Assert.That(stages, Is.EqualTo(new[] { "metrics", "stateCharge", "valueAndRecipient", "actionStart", "oogForfeit", "transferLog", "substate", "actionEnd", "refundSettlement", "accessReport", "headerFees", "finalize", "receiptContinuation" }));
        Assert.That(document.RootElement.GetProperty("acceptanceState").GetString(), Is.EqualTo("hash-pinned-audited-handwritten-model-to-model-request-boundary-only"));
        Assert.That(effects, Does.Contain("metricsIncrement"));
        Assert.That(effects, Does.Contain("recipientBalance"));
        Assert.That(effects, Does.Contain("receiptContinuation"));
        Assert.That(completion.GetProperty("handoff").GetString(), Is.EqualTo("OrdinaryPostNonceDispatchExtractor.SimpleHandoff"));
        Assert.That(completion.GetProperty("receiptMode").GetString(), Is.EqualTo("receipt-continuation-input-only"));
        Assert.That(completion.GetProperty("adapters").GetArrayLength(), Is.EqualTo(6));
        foreach (JsonElement operation in operations.EnumerateArray())
        {
            JsonElement lowering = operation.GetProperty("lowering");
            Assert.That(lowering.GetProperty("grammar").GetString(), Is.Not.Empty);
            Assert.That(lowering.GetProperty("receiver").GetString(), Is.Not.Empty);
            Assert.That(lowering.GetProperty("targetSymbolIdentity").GetString(), Is.EqualTo(operation.GetProperty("binding").GetProperty("targetSymbolIdentity").GetString()));
            Assert.That(lowering.GetProperty("executionTerm").GetString(), Is.Not.Empty);
            Assert.That(lowering.GetProperty("returnType").GetString(), Is.Not.Empty);
            Assert.That(lowering.GetProperty("argumentNames").GetArrayLength(), Is.EqualTo(lowering.GetProperty("argumentTypes").GetArrayLength()));
            Assert.That(lowering.GetProperty("argumentNames").GetArrayLength(), Is.EqualTo(lowering.GetProperty("argumentKinds").GetArrayLength()));
        }
    }

    [Test]
    public void Public_extraction_rejects_source_drift()
    {
        using SourceFixture fixture = new();
        fixture.Replace(Extractor.TransactionProcessorPath, "!senderIsRecipient", "senderIsRecipient");
        Assert.That(() => Extractor.Extract(fixture.Root, fixture.Output, Path.Combine(fixture.Output, "drift.lean")), Throws.TypeOf<ExtractionException>());
        Assert.That(Directory.EnumerateFileSystemEntries(fixture.Output), Is.Empty);
    }

    [Test]
    public void Unsupported_source_mutation_fails_closed_without_artifacts()
    {
        using SourceFixture fixture = new();
        fixture.Replace(Extractor.TransactionProcessorPath, "if (newAccountOutOfGas)", "while (newAccountOutOfGas)");
        Assert.That(() => Extractor.ExtractWithoutPinnedSourcesForTest(fixture.Root, fixture.Output, Path.Combine(fixture.Output, "unsupported.lean")), Throws.TypeOf<ExtractionException>());
        Assert.That(Directory.EnumerateFileSystemEntries(fixture.Output), Is.Empty);
    }

    [Test]
    public void Typed_source_mutation_changes_rebound_ir_and_live_lean()
    {
        string root = FindRepoRoot();
        using SourceFixture fixture = new();
        fixture.Replace(Extractor.TransactionProcessorPath, "!senderIsRecipient", "senderIsRecipient");
        using TemporaryDirectory baselineDirectory = new();
        ExtractionResult baseline = Extractor.Extract(root, baselineDirectory.Location, Path.Combine(baselineDirectory.Location, "baseline.lean"));
        ExtractionResult mutated = Extractor.ExtractWithoutPinnedSourcesForTest(fixture.Root, fixture.Output, Path.Combine(fixture.Output, "mutated.lean"));
        Assert.That(File.ReadAllBytes(mutated.IrPath), Is.Not.EqualTo(File.ReadAllBytes(baseline.IrPath)));
        Assert.That(File.ReadAllBytes(mutated.LeanPath), Is.Not.EqualTo(File.ReadAllBytes(baseline.LeanPath)));
        Assert.That(File.ReadAllText(mutated.LeanPath), Does.Contain("sourceNewAccountPredicate : SourcePredicate :="));
        Assert.That(File.ReadAllText(mutated.LeanPath), Does.Contain(".selfSend"));
    }

    [Test]
    public void Serialized_tampering_is_rejected()
    {
        string root = FindRepoRoot();
        string generated = Path.Combine(root, "tools", "Evm", "Lean", "SimpleTransferCompletionExtractor", "Generated");
        byte[] ir = File.ReadAllBytes(Path.Combine(generated, "SimpleTransferCompletion.ir.json"));
        byte[] manifest = File.ReadAllBytes(Path.Combine(generated, "SimpleTransferCompletion.source-manifest.json"));
        JsonObject irMutation = JsonNode.Parse(ir, documentOptions: new JsonDocumentOptions { MaxDepth = 1024 })!.AsObject();
        irMutation["completion"]!["effects"]![0]!["id"] = "tampered";
        JsonObject manifestMutation = JsonNode.Parse(manifest, documentOptions: new JsonDocumentOptions { MaxDepth = 1024 })!.AsObject();
        manifestMutation["semanticIrSha256"] = new string('0', 64);
        Assert.That(() => Extractor.ValidateSerializedIr(ir, Encoding.UTF8.GetBytes(irMutation.ToJsonString())), Throws.TypeOf<ExtractionException>());
        Assert.That(() => Extractor.ValidateSerializedManifest(manifest, Encoding.UTF8.GetBytes(manifestMutation.ToJsonString())), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Exact_source_guards_and_max_accounting_fail_closed_when_mutated()
    {
        using SourceFixture buildUpFixture = new();
        buildUpFixture.Replace(Extractor.TransactionProcessorPath, "opts == ExecutionOptions.BuildUp", "opts.HasFlag(ExecutionOptions.BuildUp)");
        Assert.That(() => Extractor.ExtractWithoutPinnedSourcesForTest(buildUpFixture.Root, buildUpFixture.Output, Path.Combine(buildUpFixture.Output, "build-up.lean")), Throws.TypeOf<ExtractionException>());

        using SourceFixture maxFixture = new();
        maxFixture.Replace(Extractor.TransactionGasInitializationKernelPath, "Math.Max(blockExecutionGas, blockStateGas)", "blockExecutionGas + blockStateGas");
        Assert.That(() => Extractor.ExtractWithoutPinnedSourcesForTest(maxFixture.Root, maxFixture.Output, Path.Combine(maxFixture.Output, "max.lean")), Throws.TypeOf<ExtractionException>());

        using SourceFixture effectiveFixture = new();
        effectiveFixture.Replace(Extractor.GasConsumedPath, "BlockGas > 0 || BlockStateGas > 0 ? BlockGas : SpentGas", "BlockGas > 0 ? BlockGas : SpentGas");
        Assert.That(() => Extractor.ExtractWithoutPinnedSourcesForTest(effectiveFixture.Root, effectiveFixture.Output, Path.Combine(effectiveFixture.Output, "effective.lean")), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Access_warmup_source_mutations_fail_closed()
    {
        using SourceFixture fixture = new();
        fixture.Replace(Extractor.TransactionProcessorPath, "if (spec.UseTxAccessLists)", "if (false && spec.UseTxAccessLists)");
        Assert.That(() => Extractor.ExtractWithoutPinnedSourcesForTest(fixture.Root, fixture.Output, Path.Combine(fixture.Output, "access.lean")), Throws.TypeOf<ExtractionException>());

        using SourceFixture orderFixture = new();
        orderFixture.Replace(
            Extractor.TransactionProcessorPath,
            "if (warmUpRecipient)\n                accessTracker.WarmUp(recipient);\n            accessTracker.WarmUp(tx.SenderAddress!);",
            "if (warmUpRecipient)\n                accessTracker.WarmUp(tx.SenderAddress!);\n            accessTracker.WarmUp(recipient);");
        Assert.That(() => Extractor.ExtractWithoutPinnedSourcesForTest(orderFixture.Root, orderFixture.Output, Path.Combine(orderFixture.Output, "access-order.lean")), Throws.TypeOf<ExtractionException>());

        using SourceFixture dedupFixture = new();
        dedupFixture.Replace(Extractor.JournalSetPath, "_set.Add(item)", "_items.Contains(item)");
        Assert.That(() => Extractor.ExtractWithoutPinnedSourcesForTest(dedupFixture.Root, dedupFixture.Output, Path.Combine(dedupFixture.Output, "dedup.lean")), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Source_lowered_guard_cost_and_no_frame_mutations_cannot_hide()
    {
        using SourceFixture guardFixture = new();
        guardFixture.Replace(Extractor.TransactionProcessorPath, "spec.IsEip8037Enabled && hasValueTransfer", "spec.IsEip8037Enabled && !hasValueTransfer");
        using TemporaryDirectory baselineDirectory = new();
        ExtractionResult baseline = Extractor.Extract(FindRepoRoot(), baselineDirectory.Location, Path.Combine(baselineDirectory.Location, "baseline.lean"));
        ExtractionResult guard = Extractor.ExtractWithoutPinnedSourcesForTest(guardFixture.Root, guardFixture.Output, Path.Combine(guardFixture.Output, "guard.lean"));
        Assert.That(File.ReadAllBytes(guard.LeanPath), Is.Not.EqualTo(File.ReadAllBytes(baseline.LeanPath)));
        Assert.That(File.ReadAllText(guard.LeanPath), Does.Contain(".negate (.hasValue)"));

        using SourceFixture deadRecipientFixture = new();
        deadRecipientFixture.Replace(Extractor.TransactionProcessorPath, "WorldState.IsDeadAccount(recipient)", "!WorldState.IsDeadAccount(recipient)");
        ExtractionResult deadRecipient = Extractor.ExtractWithoutPinnedSourcesForTest(
            deadRecipientFixture.Root, deadRecipientFixture.Output, Path.Combine(deadRecipientFixture.Output, "dead-recipient.lean"));
        Assert.That(File.ReadAllText(deadRecipient.LeanPath), Does.Contain(".negate (.recipientDead)"));

        using SourceFixture costFixture = new();
        costFixture.Replace(Extractor.GasCostOfPath, "NewAccountState = StateBytesPerNewAccount * CostPerStateByte", "NewAccountState = 1");
        ExtractionResult cost = Extractor.ExtractWithoutPinnedSourcesForTest(costFixture.Root, costFixture.Output, Path.Combine(costFixture.Output, "cost.lean"));
        Assert.That(File.ReadAllText(cost.LeanPath), Does.Contain("sourceNewAccountStateCost : Nat := 1"));

        using SourceFixture policyCostFixture = new();
        policyCostFixture.Replace(Extractor.GasPolicyInterfacePath, "GasCostOf.NewAccountState", "1");
        Assert.That(() => Extractor.ExtractWithoutPinnedSourcesForTest(policyCostFixture.Root, policyCostFixture.Output, Path.Combine(policyCostFixture.Output, "policy-cost.lean")), Throws.TypeOf<ExtractionException>());

        using SourceFixture noFrameFixture = new();
        noFrameFixture.Replace(Extractor.TransactionProcessorPath, "goto CompleteWithoutFrame;", "goto Complete;");
        Assert.That(() => Extractor.ExtractWithoutPinnedSourcesForTest(noFrameFixture.Root, noFrameFixture.Output, Path.Combine(noFrameFixture.Output, "no-frame.lean")), Throws.TypeOf<ExtractionException>());

        using SourceFixture logFixture = new();
        logFixture.Replace(Extractor.TransferLogPath,
            "[TransferSignature, from.ToHash().ToHash256(), to.ToHash().ToHash256()]",
            "[TransferSignature, to.ToHash().ToHash256(), from.ToHash().ToHash256()]");
        Assert.That(() => Extractor.ExtractWithoutPinnedSourcesForTest(logFixture.Root, logFixture.Output, Path.Combine(logFixture.Output, "log-payload.lean")), Throws.TypeOf<ExtractionException>());

        using SourceFixture dataFixture = new();
        dataFixture.Replace(Extractor.TransferLogPath, "amount.ToBigEndian()", "amount.ToBigEndian().Reverse().ToArray()");
        Assert.That(() => Extractor.ExtractWithoutPinnedSourcesForTest(dataFixture.Root, dataFixture.Output, Path.Combine(dataFixture.Output, "log-data.lean")), Throws.TypeOf<ExtractionException>());

        using SourceFixture counterFixture = new();
        counterFixture.Replace(
            Extractor.TransactionProcessorPath,
            "_blockCumulativeExecutionGas += spentGas.EffectiveBlockGas;",
            "_blockCumulativeExecutionGas += spentGas.BlockGas;");
        Assert.That(() => Extractor.ExtractWithoutPinnedSourcesForTest(counterFixture.Root, counterFixture.Output, Path.Combine(counterFixture.Output, "counter-payload.lean")), Throws.TypeOf<ExtractionException>());

        using SourceFixture cumulativeGateFixture = new();
        cumulativeGateFixture.Replace(
            Extractor.TransactionProcessorPath,
            "if (spec.IsEip8037Enabled)\n                {\n                    _blockCumulativeExecutionGas += spentGas.EffectiveBlockGas;",
            "if (!spec.IsEip8037Enabled)\n                {\n                    _blockCumulativeExecutionGas += spentGas.EffectiveBlockGas;");
        Assert.That(() => Extractor.ExtractWithoutPinnedSourcesForTest(cumulativeGateFixture.Root, cumulativeGateFixture.Output, Path.Combine(cumulativeGateFixture.Output, "cumulative-gate.lean")), Throws.TypeOf<ExtractionException>());

        using SourceFixture receiptFixture = new();
        receiptFixture.Replace(
            Extractor.TransactionProcessorPath,
            "WorldState.RecalculateStateRoot();\n                    stateRoot = WorldState.StateRoot;",
            "stateRoot = WorldState.StateRoot;");
        Assert.That(() => Extractor.ExtractWithoutPinnedSourcesForTest(receiptFixture.Root, receiptFixture.Output, Path.Combine(receiptFixture.Output, "receipt-root.lean")), Throws.TypeOf<ExtractionException>());

        using SourceFixture payValueOrderFixture = new();
        payValueOrderFixture.Replace(
            Extractor.TransactionProcessorPath,
            "if (hasValueTransfer) PayValue(tx, spec, opts);\n                WorldState.AddToBalanceAndCreateIfNotExists(recipient, in hasValueTransfer ? ref value : ref UInt256.Zero, spec);",
            "WorldState.AddToBalanceAndCreateIfNotExists(recipient, in hasValueTransfer ? ref value : ref UInt256.Zero, spec);\n                if (hasValueTransfer) PayValue(tx, spec, opts);");
        Assert.That(() => Extractor.ExtractWithoutPinnedSourcesForTest(payValueOrderFixture.Root, payValueOrderFixture.Output, Path.Combine(payValueOrderFixture.Output, "pay-value-order.lean")), Throws.TypeOf<ExtractionException>());

        using SourceFixture predicateArgumentFixture = new();
        predicateArgumentFixture.Replace(
            Extractor.TransactionProcessorPath,
            "WorldState.IsDeadAccount(recipient)",
            "WorldState.IsDeadAccount(tx.SenderAddress!)");
        Assert.That(() => Extractor.ExtractWithoutPinnedSourcesForTest(predicateArgumentFixture.Root, predicateArgumentFixture.Output, Path.Combine(predicateArgumentFixture.Output, "predicate-argument.lean")), Throws.TypeOf<ExtractionException>());

        using SourceFixture counterArgumentFixture = new();
        counterArgumentFixture.Replace(
            Extractor.TransactionProcessorPath,
            "SystemTransactionRoutingKernel.ParticipatesInNormalBlockCounters(opts, _parallel)",
            "SystemTransactionRoutingKernel.ParticipatesInNormalBlockCounters(opts, false)");
        Assert.That(() => Extractor.ExtractWithoutPinnedSourcesForTest(counterArgumentFixture.Root, counterArgumentFixture.Output, Path.Combine(counterArgumentFixture.Output, "counter-argument.lean")), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Project_metadata_closure_is_content_pinned()
    {
        using SourceFixture fixture = new();
        fixture.MutateReference("Nethermind.Evm.dll");
        Assert.That(() => Extractor.ExtractWithoutPinnedSourcesForTest(fixture.Root, fixture.Output, Path.Combine(fixture.Output, "metadata.lean")), Throws.TypeOf<ExtractionException>());
        Assert.That(Directory.EnumerateFileSystemEntries(fixture.Output), Is.Empty);
    }

    [Test]
    public void Typed_invocation_grammar_rejects_argument_receiver_and_overload_mutations()
    {
        using SourceFixture argumentFixture = new();
        argumentFixture.Replace(
            Extractor.TransactionProcessorPath,
            "TraceSimpleTransferActionStart(tx, recipient, tracer, in value, in gasAvailable)",
            "TraceSimpleTransferActionStart(tx, recipient, tracer, in gasAvailable, in value)");
        Assert.That(() => Extractor.ExtractWithoutPinnedSourcesForTest(argumentFixture.Root, argumentFixture.Output, Path.Combine(argumentFixture.Output, "argument.lean")), Throws.TypeOf<ExtractionException>());

        using SourceFixture sameTypeArgumentFixture = new();
        sameTypeArgumentFixture.Replace(
            Extractor.TransactionProcessorPath,
            "opts, restore, commit, deleteCallerAccount",
            "opts, commit, restore, deleteCallerAccount");
        Assert.That(() => Extractor.ExtractWithoutPinnedSourcesForTest(sameTypeArgumentFixture.Root, sameTypeArgumentFixture.Output, Path.Combine(sameTypeArgumentFixture.Output, "same-type-argument.lean")), Throws.TypeOf<ExtractionException>());

        using SourceFixture receiverFixture = new();
        receiverFixture.Replace(
            Extractor.TransactionProcessorPath,
            "WorldState.AddToBalanceAndCreateIfNotExists(recipient, in hasValueTransfer ? ref value : ref UInt256.Zero, spec)",
            "WorldStateExtensions.AddToBalanceAndCreateIfNotExists(WorldState, recipient, in hasValueTransfer ? ref value : ref UInt256.Zero, spec)");
        Assert.That(() => Extractor.ExtractWithoutPinnedSourcesForTest(receiverFixture.Root, receiverFixture.Output, Path.Combine(receiverFixture.Output, "receiver.lean")), Throws.TypeOf<ExtractionException>());

        using SourceFixture overloadFixture = new();
        overloadFixture.Replace(
            Extractor.TransactionProcessorPath,
            "accessTracker.WarmUp(tx.AccessList)",
            "accessTracker.WarmUp(recipient)");
        Assert.That(() => Extractor.ExtractWithoutPinnedSourcesForTest(overloadFixture.Root, overloadFixture.Output, Path.Combine(overloadFixture.Output, "overload.lean")), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Semantic_closure_rejects_unresolved_source_symbols()
    {
        using SourceFixture fixture = new();
        fixture.Replace(Extractor.TransactionProcessorPath, "bool hasValueTransfer = !value.IsZero;", "bool hasValueTransfer = !value.NoSuchMember;");
        Assert.That(() => Extractor.ExtractWithoutPinnedSourcesForTest(fixture.Root, fixture.Output, Path.Combine(fixture.Output, "unresolved.lean")), Throws.TypeOf<ExtractionException>());
        Assert.That(Directory.EnumerateFileSystemEntries(fixture.Output), Is.Empty);

        using SourceFixture ambiguousFixture = new();
        ambiguousFixture.Replace(Extractor.TransactionProcessorPath, "accessTracker.WarmUp(tx.AccessList)", "accessTracker.WarmUp(default)");
        Assert.That(() => Extractor.ExtractWithoutPinnedSourcesForTest(ambiguousFixture.Root, ambiguousFixture.Output, Path.Combine(ambiguousFixture.Output, "ambiguous.lean")), Throws.TypeOf<ExtractionException>());
        Assert.That(Directory.EnumerateFileSystemEntries(ambiguousFixture.Output), Is.Empty);
    }

    [Test]
    public void Reference_and_refinement_are_independent_files()
    {
        string root = FindRepoRoot();
        string package = Path.Combine(root, "tools", "Evm", "Lean", "SimpleTransferCompletionExtractor");
        string reference = File.ReadAllText(Path.Combine(package, "Reference", "SimpleTransferCompletionReference.lean"));
        string refinement = File.ReadAllText(Path.Combine(package, "Refinement", "SimpleTransferCompletionRefinement.lean"));
        Assert.Multiple(() =>
        {
            Assert.That(reference, Does.Contain("structure Case"));
            Assert.That(reference, Does.Contain("def accessObservation"));
            Assert.That(reference, Does.Contain("def run (case : Case)"));
            Assert.That(reference, Does.Not.Contain("Generated.SimpleTransferCompletion"));
            Assert.That(reference, Does.Not.Contain("OrdinaryPostNonceDispatchExtractor"));
            Assert.That(reference, Does.Not.Contain("transferTopicOracle"));
            Assert.That(reference, Does.Not.Contain("theorem universal_refinement"));
            Assert.That(refinement, Does.Contain("AdapterCoherent"));
            Assert.That(refinement, Does.Contain("uint256Max"));
            Assert.That(refinement, Does.Contain("allUInt64"));
            Assert.That(refinement, Does.Contain("allInt64"));
            Assert.That(refinement, Does.Contain("theorem universal_refinement"));
            Assert.That(refinement, Does.Contain("source_identity_bridge"));
            Assert.That(refinement, Does.Contain("receipt_continuation_preserved"));
            Assert.That(refinement, Does.Contain("mapWorldRequest"));
            Assert.That(refinement, Does.Contain("accessObservationExtensional"));
            Assert.That(refinement, Does.Contain("journalExtensional"));
            Assert.That(refinement, Does.Contain("Generated.addressHashProjection"));
            Assert.That(refinement, Does.Contain("transfer_topic_projection_bridge"));
            Assert.That(refinement, Does.Contain("normalReturnDomain"));
            Assert.That(refinement, Does.Contain("normal_return_domain_excludes_callback_prefix"));
            Assert.That(refinement, Does.Not.Contain("TransferPayloadAdapter"));
            Assert.That(refinement, Does.Not.Contain("transferPayload"));
            Assert.That(refinement, Does.Not.Contain("transferTopicOracle"));
            Assert.That(refinement, Does.Not.Contain("transferLogOracle"));
        });
    }

    [Test, Explicit("Runs the serialized Lake lane; Verify.ps1 is the normal entry point.")]
    public void Mutated_live_lean_typechecks_with_warnings_as_errors()
    {
        string root = FindRepoRoot();
        using SourceFixture fixture = new();
        fixture.Replace(Extractor.TransactionProcessorPath, "!senderIsRecipient", "senderIsRecipient");
        ExtractionResult result = Extractor.ExtractWithoutPinnedSourcesForTest(fixture.Root, fixture.Output, Path.Combine(fixture.Output, "mutated.lean"));
        AssertLeanCompiles(Path.Combine(root, "tools", "Evm", "Lean", "SimpleTransferCompletionExtractor"), result.LeanPath);
    }

    private static void AssertLeanCompiles(string package, string leanPath)
    {
        ProcessStartInfo info = new("lake", $"env lean -DwarningAsError=true -DmaxHeartbeats=800000 \"{leanPath}\"")
        {
            WorkingDirectory = package,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using Process process = Process.Start(info) ?? throw new AssertionException("Could not start Lake.");
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.That(process.ExitCode, Is.EqualTo(0), output);
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json"))) directory = directory.Parent;
        return directory?.FullName ?? throw new AssertionException("Could not locate repository root.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Location = Path.Combine(Path.GetTempPath(), "simple-transfer-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Location);
        }

        internal string Location { get; }
        public void Dispose() => Directory.Delete(Location, recursive: true);
    }

    private sealed class SourceFixture : IDisposable
    {
        private static readonly string[] SourcePaths =
        [
            Extractor.TransactionProcessorPath, Extractor.MetricsPath, Extractor.ExecutionOptionsPath, Extractor.SettlementKernelPath,
            Extractor.RoutingKernelPath, Extractor.MainnetDiPath, Extractor.GasPolicyInterfacePath, Extractor.EthereumGasPolicyPath,
            Extractor.StateGasChargeKernelPath, Extractor.StateGasTransitionKernelPath, Extractor.StateGasTransitionAdapterKernelPath, Extractor.GasCostInterfacePath,
            Extractor.TransactionGasInitializationKernelPath,
            Extractor.WorldStateInterfacePath, Extractor.ReadOnlyStateProviderInterfacePath, Extractor.WorldStateExtensionsPath, Extractor.WorldStatePath, Extractor.StateProviderPath, Extractor.TransactionPath,
            Extractor.TransactionStdPath, Extractor.AddressPath, Extractor.BlockHeaderPath, Extractor.TransactionSubstatePath, Extractor.TxTracerPath,
            Extractor.TxTracerBasePath, Extractor.NullTxTracerPath, Extractor.WorldStateTracerPath, Extractor.StackAccessTrackerPath, Extractor.EvmObjectPoolPath, Extractor.LogEntryPath,
            Extractor.JournalCollectionPath, Extractor.JournalSetPath, Extractor.AccessListPath, Extractor.GasConsumedPath,
            Extractor.StatusCodePath, Extractor.TransferLogPath, Extractor.IReleaseSpecPath, Extractor.EvmExceptionPath,
            Extractor.ExecutionTypePath, Extractor.ITransactionProcessorPath, Extractor.SystemTransactionProcessorPath, Extractor.CodeRepositoryInterfacePath, Extractor.CodeInfoPath,
            Extractor.JumpDestinationAnalyzerPath, Extractor.JumpDestinationAnalyzerStandardPath, Extractor.IntrinsicGasCalculatorPath,
            Extractor.AccountAccessPricingKernelPath, Extractor.PrecompileGasPricingKernelPath, Extractor.SStorePricingKernelPath,
            Extractor.CodeRepositoryPath, Extractor.CacheCodeRepositoryPath, Extractor.VirtualMachineInterfacePath,
            Extractor.VirtualMachineSignaturePath, Extractor.VirtualMachineAdapterPath,
            Extractor.PartialStorageProviderPath, Extractor.PersistentStorageProviderPath, Extractor.PersistentStorageProviderStandardPath, Extractor.TransientStorageProviderPath, Extractor.ChangeTypePath,
            Extractor.DispatchFlagsStandardPath, Extractor.MetricsStandardPath, Extractor.LocalMetricsFlushStandardPath,
            Extractor.GasCostOfPath,
        ];

        private static readonly string[] DependencyPaths =
        [Extractor.HandoffIrPath, Extractor.HandoffManifestPath, Extractor.HandoffLeanPath, Extractor.SettlementLeanPath, Extractor.SettlementRefinementPath, Extractor.StateChargeLeanPath, Extractor.StateChargeRefinementPath, Extractor.RoutingLeanPath, Extractor.RoutingRefinementPath, Extractor.ProjectAssetsRelativePath];

        internal SourceFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "simple-transfer-fixture-" + Guid.NewGuid().ToString("N"));
            Output = Path.Combine(Root, "output");
            string repository = FindRepoRoot();
            foreach (string relative in SourcePaths.Concat(DependencyPaths))
            {
                string target = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(repository, relative.Replace('/', Path.DirectorySeparatorChar)), target);
            }
            string referenceDirectory = Path.Combine(repository, "src", "Nethermind", "artifacts", "bin", "Nethermind.Init", "release");
            if (!File.Exists(Path.Combine(referenceDirectory, "Nethermind.Evm.dll")) ||
                !File.Exists(Path.Combine(referenceDirectory, "Nethermind.Init.dll")) ||
                !File.Exists(Path.Combine(referenceDirectory, "Nethermind.Core.dll")))
                throw new AssertionException("Pinned Nethermind project reference closure is missing.");
            string fixtureReferences = Path.Combine(Root, "src", "Nethermind", "artifacts", "bin", "Nethermind.Init", "release");
            Directory.CreateDirectory(fixtureReferences);
            foreach (string reference in Directory.EnumerateFiles(referenceDirectory, "*.dll"))
                File.Copy(reference, Path.Combine(fixtureReferences, Path.GetFileName(reference)));
            Directory.CreateDirectory(Output);
        }

        internal string Root { get; }
        internal string Output { get; }

        internal void Replace(string relativePath, string original, string replacement)
        {
            string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            string text = File.ReadAllText(path);
            if (!text.Contains(original, StringComparison.Ordinal)) throw new AssertionException($"Mutation anchor not found: {original}");
            File.WriteAllText(path, text.Replace(original, replacement, StringComparison.Ordinal));
        }

        internal void MutateReference(string fileName)
        {
            string path = Path.Combine(Root, "src", "Nethermind", "artifacts", "bin", "Nethermind.Init", "release", fileName);
            byte[] bytes = File.ReadAllBytes(path);
            bytes[^1] ^= 0x01;
            File.WriteAllBytes(path, bytes);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
