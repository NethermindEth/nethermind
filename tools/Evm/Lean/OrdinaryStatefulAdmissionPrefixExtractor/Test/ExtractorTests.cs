// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.OrdinaryStatefulAdmissionPrefixExtractor.Test;

[TestFixture]
public sealed class ExtractorTests
{
    private const string ScratchRoot = @"D:\tmp";

    [Test]
    public void Production_sources_extract_deterministically()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory first = new();
        using TemporaryDirectory second = new();

        ExtractionResult firstResult = Extractor.Extract(root, first.Location,
            Path.Combine(first.Location, "OrdinaryStatefulAdmissionPrefix.lean"));
        ExtractionResult secondResult = Extractor.Extract(root, second.Location,
            Path.Combine(second.Location, "OrdinaryStatefulAdmissionPrefix.lean"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstResult.SourceCount, Is.EqualTo(15));
            Assert.That(firstResult.BranchCount, Is.EqualTo(19));
            Assert.That(File.ReadAllBytes(firstResult.IrPath), Is.EqualTo(File.ReadAllBytes(secondResult.IrPath)));
            Assert.That(File.ReadAllBytes(firstResult.ManifestPath), Is.EqualTo(File.ReadAllBytes(secondResult.ManifestPath)));
            Assert.That(File.ReadAllBytes(firstResult.LeanPath), Is.EqualTo(File.ReadAllBytes(secondResult.LeanPath)));
        }
    }

    [Test]
    public void Checked_in_artifacts_match_a_fresh_extraction()
    {
        string root = FindRepoRoot();
        string package = Path.Combine(root, "tools", "Evm", "Lean", "OrdinaryStatefulAdmissionPrefixExtractor");
        string generated = Path.Combine(package, "Generated");

        Assert.That(() => Extractor.ValidateExistingArtifacts(root, generated,
            Path.Combine(generated, "OrdinaryStatefulAdmissionPrefix.lean")), Throws.Nothing);
    }

    [Test]
    public void Generated_Lean_is_theorem_free_and_captures_the_bounded_transition()
    {
        string root = FindRepoRoot();
        string path = Path.Combine(root, "tools", "Evm", "Lean", "OrdinaryStatefulAdmissionPrefixExtractor",
            "Generated", "OrdinaryStatefulAdmissionPrefix.lean");
        string lean = File.ReadAllText(path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(lean, Does.Contain("def checkedAdd256"));
            Assert.That(lean, Does.Contain("def checkedMul256"));
            Assert.That(lean, Does.Contain("structure CheckedUInt256"));
            Assert.That(lean, Does.Contain("def finishReservedPayment"));
            Assert.That(lean, Does.Contain("senderReservedGasPayment := reservedValue"));
            Assert.That(lean, Does.Contain("def rejectAfterCombinedGate"));
            Assert.That(lean, Does.Contain("inductive RecoveryResult"));
            Assert.That(lean, Does.Contain("senderAccountDoesNotExist"));
            Assert.That(lean, Does.Contain("def blobGas (tx : Transaction)"));
            Assert.That(lean, Does.Contain("def gasPerBlob"));
            Assert.That(lean, Does.Not.Contain("blobGas : Nat"));
            Assert.That(lean, Does.Contain("def ordinaryStandardInput"));
            Assert.That(lean, Does.Contain("def inputAdapterCoherent"));
            Assert.That(lean, Does.Contain("maxFeePerBlobGas : Option Nat"));
            Assert.That(lean, Does.Contain("blobVersionedHashes : Option (List (Option Nat))"));
            Assert.That(lean, Does.Contain("def sourceLoweredOperations"));
            Assert.That(lean, Does.Contain("def sourceLoweredAdapters"));
            Assert.That(lean, Does.Contain("def run"));
            Assert.That(lean, Does.Contain("def orderedBranchIds"));
            Assert.That(lean, Does.Not.Contain("theorem "));
            Assert.That(lean, Does.Not.Contain("sorry"));
            Assert.That(lean, Does.Not.Match(@"(?m)^\s*admit\b"));
            Assert.That(lean, Does.Not.Contain("axiom "));
        }
    }

    [Test]
    public void Refinement_main_theorem_uses_the_composed_simulation()
    {
        string root = FindRepoRoot();
        string path = Path.Combine(root, "tools", "Evm", "Lean", "OrdinaryStatefulAdmissionPrefixExtractor",
            "Refinement", "OrdinaryStatefulAdmissionPrefix.lean");
        string refinement = File.ReadAllText(path);
        int theoremStart = refinement.IndexOf("theorem generatedRun_mapsReference", StringComparison.Ordinal);
        int theoremEnd = refinement.IndexOf("def observeGenerated", theoremStart, StringComparison.Ordinal);
        int corollaryStart = refinement.IndexOf("/-- Bounded transcription corollary", theoremEnd, StringComparison.Ordinal);

        Assert.That(theoremStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(theoremEnd, Is.GreaterThan(theoremStart));
        Assert.That(corollaryStart, Is.GreaterThan(theoremEnd));
        string theorem = refinement[theoremStart..theoremEnd];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(theorem, Does.Not.Contain("rw [←"));
            Assert.That(theorem.Replace("\r", string.Empty, StringComparison.Ordinal), Does.Not.Contain(":= by\n  rfl"));
            Assert.That(theorem, Does.Contain("finishAfterRecovery_refines"));
            Assert.That(theorem, Does.Contain("recover_refines"));
            Assert.That(theorem, Does.Contain("prepareRecovery_refines"));
            Assert.That(refinement, Does.Contain("private theorem chargeGas_refines"));
            Assert.That(refinement, Does.Contain("private theorem advanceNonce_refines"));
            Assert.That(refinement, Does.Contain("rcases h with"));
            Assert.That(refinement, Does.Contain("Generated.sourceGrammarSemanticsCoherent"));
            string generated = File.ReadAllText(Path.Combine(root, "tools", "Evm", "Lean", "OrdinaryStatefulAdmissionPrefixExtractor",
                "Generated", "OrdinaryStatefulAdmissionPrefix.lean"));
            Assert.That(generated, Does.Contain("grammarSemanticsAuditPassed : Bool"));
            Assert.That(generated, Does.Contain("def sourceGrammarSemanticsCoherent"));
        }
    }

    [Test]
    public void Ir_preserves_the_exact_stage_and_branch_order()
    {
        string root = FindRepoRoot();
        string path = Path.Combine(root, "tools", "Evm", "Lean", "OrdinaryStatefulAdmissionPrefixExtractor",
            "Generated", "OrdinaryStatefulAdmissionPrefix.ir.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        string[] stages = document.RootElement.GetProperty("stages")
            .EnumerateArray().Select(static stage => stage.GetProperty("id").GetString()!).ToArray();
        string[] branches = document.RootElement.GetProperty("branches")
            .EnumerateArray().Select(static branch => branch.GetProperty("id").GetString()!).ToArray();

        Assert.That(stages, Is.EqualTo(new[]
        {
            "validateStatic", "calculateEffectiveGasPrice", "updateMetrics", "recoverSenderIfNeeded",
            "validateSender", "buyGas", "incrementNonce",
        }));
        Assert.That(branches, Is.EqualTo(new[]
        {
            "validateStaticReturn", "recoverSenderCreatesAccount", "recoverSenderThrows",
            "validateSenderContract", "buyGasPremiumBelowBaseFee", "buyGasReservedPaymentOverflow",
            "buyGasMaximumFeeOverflow", "buyGasValueOverflow", "buyGasBlobMaximumFeeOverflow",
            "buyGasBlobFeeCalculationOverflow", "buyGasBlobFeeCapBelowBaseFee", "buyGasBlobPaymentOverflow",
            "buyGasInsufficientBalanceWarmup", "buyGasInsufficientBalanceReturn", "buyGasDebit",
            "incrementNonceMismatch", "incrementNonceSet", "combinedAdmissionFailureRestore",
            "continueAfterAdmissionPrefix",
        }));
    }

    [TestCase(Extractor.TransactionProcessorPath,
        "!(result = ValidateSender(tx, header, spec, tracer, opts)) ||",
        "!(result = ValidateSender(tx, header, spec, tracer, opts)) &&")]
    [TestCase(Extractor.TransactionProcessorPath,
        "WorldState.Reset(resetBlockChanges: false);",
        "WorldState.Reset(resetBlockChanges: true);")]
    [TestCase(Extractor.TransactionProcessorPath,
        "nonce + 1 : 0",
        "nonce + 2 : 0")]
    [TestCase(Extractor.TransactionProcessorPath,
        "UInt256.Min(senderReservedGasPayment, balance)",
        "UInt256.Max(senderReservedGasPayment, balance)")]
    [TestCase(Extractor.TransactionProcessorPath,
        "opts is ExecutionOptions.Commit or ExecutionOptions.None",
        "opts is ExecutionOptions.Commit or ExecutionOptions.BuildUp")]
    [TestCase(Extractor.TransactionExtensionsPath,
        "UInt256.Min(tx.MaxFeePerGas, effectiveFee)",
        "UInt256.Max(tx.MaxFeePerGas, effectiveFee)")]
    [TestCase(Extractor.RoutingKernelPath,
        "options == ExecutionOptions.SkipValidation",
        "options == ExecutionOptions.Commit")]
    [TestCase(Extractor.MainnetDiPath,
        "AddScoped<ITransactionProcessor.IBlobBaseFeeCalculator, BlobBaseFeeCalculator>()",
        "AddScoped<ITransactionProcessor.IBlobBaseFeeCalculator, OtherBlobBaseFeeCalculator>()")]
    [TestCase(Extractor.TransactionProcessorPath,
        "totalBlobBaseFee = UInt256.Zero;",
        "totalBlobBaseFee = UInt256.One;")]
    [TestCase(Extractor.TransactionProcessorPath,
        "TraceLogInvalidTx(tx, $\"SENDER_ACCOUNT_DOES_NOT_EXIST {sender}\");",
        "TraceLogInvalidTx(tx, $\"SENDER_ACCOUNT_MISSING {sender}\");")]
    [TestCase(Extractor.TransactionProcessorPath,
        "WorldState.CreateAccount(sender!, in UInt256.Zero);",
        "WorldState.CreateAccount(sender!, in UInt256.One);")]
    [TestCase(Extractor.TransactionProcessorPath,
        "TraceLogInvalidTx(tx, \"SENDER_NOT_SPECIFIED\");",
        "TraceLogInvalidTx(tx, \"SENDER_MISSING\");")]
    [TestCase(Extractor.OptionsPath,
        "public enum ExecutionOptions",
        "public enum ExecutionOptions : long")]
    [TestCase(Extractor.TransactionPath,
        "public UInt256? MaxFeePerBlobGas { get; set; }",
        "public UInt256 MaxFeePerBlobGas { get; set; }")]
    [TestCase(Extractor.TransactionPath,
        "public byte[]?[]? BlobVersionedHashes { get; set; }",
        "public byte[][] BlobVersionedHashes { get; set; }")]
    [TestCase(Extractor.TransactionProcessorPath,
        "return ExecuteCore(transaction, txTracer, options);",
        "return Execute(transaction, txTracer, options);")]
    [TestCase(Extractor.MainnetDiPath,
        "AddScoped<IWorldState, WorldState>()",
        "AddScoped<IWorldState, OtherWorldState>()")]
    [TestCase(Extractor.StateProviderPath,
        "Account.TotallyEmpty : new Account(nonce, balance)",
        "Account.TotallyEmpty : new Account(nonce + 1, balance)")]
    public void Source_shape_mutations_fail_closed(string relativePath, string original, string replacement)
    {
        using SourceFixture fixture = new();
        fixture.Replace(relativePath, original, replacement);

        Assert.That(() => Extractor.Extract(fixture.Root, fixture.Output,
            Path.Combine(fixture.Output, "OrdinaryStatefulAdmissionPrefix.lean")),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Unreviewed_source_mutation_is_rejected_before_artifact_write()
    {
        using SourceFixture fixture = new();
        fixture.Replace(Extractor.TransactionProcessorPath,
            "WorldState.Reset(resetBlockChanges: false);",
            "WorldState.Reset(resetBlockChanges: true);");

        Assert.That(() => Extractor.Extract(fixture.Root, fixture.Output,
            Path.Combine(fixture.Output, "OrdinaryStatefulAdmissionPrefix.lean")), Throws.TypeOf<ExtractionException>());
        Assert.That(Directory.EnumerateFileSystemEntries(fixture.Output), Is.Empty);
    }

    [TestCase(Extractor.TransactionProcessorPath,
        "!(result = ValidateSender(tx, header, spec, tracer, opts)) ||",
        "!(result = ValidateSender(tx, header, spec, tracer, opts)) &&")]
    [TestCase(Extractor.TransactionProcessorPath,
        "UpdateMetrics(opts, effectiveGasPrice);",
        "UpdateMetrics(opts, effectiveGasPrice); UpdateMetrics(opts, effectiveGasPrice);")]
    [TestCase(Extractor.TransactionProcessorPath,
        "Metrics.UpdateBlockGasPrice(effectiveGasPrice);",
        "Metrics.UpdateBlockGasPrice(effectiveGasPrice); Metrics.UpdateBlockGasPrice(effectiveGasPrice);")]
    [TestCase(Extractor.TransactionProcessorPath,
        "validate && !TryCalculatePremiumPerGas(tx, header.BaseFeePerGas, out premiumPerGas)",
        "validate && !TryCalculatePremiumPerGas(tx, header.BaseFeePerGas, out premiumPerGas) && opts.HasFlag(ExecutionOptions.Warmup)")]
    [TestCase(Extractor.TransactionProcessorPath,
        "protected virtual UInt256 CalculateEffectiveGasPrice",
        "protected virtual UInt256 RenamedCalculateEffectiveGasPrice")]
    [TestCase(Extractor.TransactionPath,
        "public ulong GasLimit { get; set; }",
        "public uint GasLimit { get; set; }")]
    [TestCase(Extractor.TransactionPath,
        "public UInt256? MaxFeePerBlobGas { get; set; }",
        "public UInt256 MaxFeePerBlobGas { get; set; }")]
    [TestCase(Extractor.TransactionExtensionsPath,
        "UInt256.Min(tx.MaxFeePerGas, effectiveFee)",
        "UInt256.Max(tx.MaxFeePerGas, effectiveFee)")]
    [TestCase(Extractor.BlobGasCalculatorPath,
        "blobCount * Eip4844Constants.GasPerBlob",
        "blobCount + Eip4844Constants.GasPerBlob")]
    [TestCase(Extractor.Eip4844ConstantsPath,
        "GasPerBlob = 131072",
        "GasPerBlob = 131073")]
    public void Unreviewed_semantic_source_mutations_fail_the_supported_lowering(
        string relativePath,
        string original,
        string replacement)
    {
        using SourceFixture fixture = new();
        fixture.Replace(relativePath, original, replacement);

        Assert.That(() => Extractor.ExtractWithoutReviewedAdmissionForTest(fixture.Root, fixture.Output,
            Path.Combine(fixture.Output, "OrdinaryStatefulAdmissionPrefix.lean")), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Paired_source_and_semantic_ir_metric_mutation_changes_emitted_lean()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory baseline = new();
        using SourceFixture fixture = new();
        fixture.Replace(Extractor.TransactionProcessorPath,
            "opts is ExecutionOptions.Commit or ExecutionOptions.None",
            "opts is ExecutionOptions.Commit");

        ExtractionResult baselineResult = Extractor.Extract(root, baseline.Location,
            Path.Combine(baseline.Location, "OrdinaryStatefulAdmissionPrefix.lean"));
        ExtractionResult loweredMutation = Extractor.ExtractWithoutReviewedAdmissionForTest(fixture.Root, fixture.Output,
            Path.Combine(fixture.Output, "OrdinaryStatefulAdmissionPrefix.lean"));
        string baselineLean = File.ReadAllText(baselineResult.LeanPath);
        string mutatedLean = File.ReadAllText(loweredMutation.LeanPath);
        using JsonDocument mutatedIr = JsonDocument.Parse(File.ReadAllBytes(loweredMutation.IrPath));
        string formula = mutatedIr.RootElement.GetProperty("semantics").GetProperty("operations")
            .EnumerateArray().Single(operation => operation.GetProperty("id").GetString() == "metricsGate")
            .GetProperty("formula").GetString()!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(formula, Is.EqualTo("metricsCommitOnly"));
        Assert.That(mutatedLean, Does.Contain("if (input.options.raw == executionOptionCommit) then"));
            Assert.That(mutatedLean, Is.Not.EqualTo(baselineLean));
            Assert.That(() => Extractor.Extract(fixture.Root, fixture.Output,
                Path.Combine(fixture.Output, "admitted.lean")), Throws.TypeOf<ExtractionException>());
        }
    }

    [Test]
    public void Unlowered_semantic_ir_effect_is_rejected_by_the_emitter()
    {
        string root = FindRepoRoot();
        string generated = Path.Combine(root, "tools", "Evm", "Lean", "OrdinaryStatefulAdmissionPrefixExtractor", "Generated");
        byte[] ir = File.ReadAllBytes(Path.Combine(generated, "OrdinaryStatefulAdmissionPrefix.ir.json"));
        JsonObject mutation = ParseObject(ir);
        JsonArray operations = mutation["semantics"]!.AsObject()["operations"]!.AsArray();
        JsonObject recovery = operations.Single(operation => operation!.AsObject()["id"]!.GetValue<string>() == "recoveryApplication")!.AsObject();
        recovery["effects"]!.AsArray()[0] = "appendMetric";

        byte[] mutated = Encoding.UTF8.GetBytes(mutation.ToJsonString());
        Assert.That(() => Extractor.EmitUnvalidatedIrForTest(mutated),
            Throws.TypeOf<ExtractionException>());
        Assert.That(() => Extractor.ValidateSerializedIr(ir, mutated),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Every_source_lowered_semantic_field_is_emitted_or_rejected()
    {
        string root = FindRepoRoot();
        string generated = Path.Combine(root, "tools", "Evm", "Lean", "OrdinaryStatefulAdmissionPrefixExtractor", "Generated");
        byte[] ir = File.ReadAllBytes(Path.Combine(generated, "OrdinaryStatefulAdmissionPrefix.ir.json"));

        JsonObject branchTerminal = ParseObject(ir);
        branchTerminal["branches"]!.AsArray()[0]!.AsObject()["terminalKind"] = "throw";

        JsonObject branchConjunct = ParseObject(ir);
        JsonObject branchCondition = branchConjunct["branches"]!.AsArray()[4]!.AsObject()["conditionAst"]!.AsObject();
        branchCondition["symbol"] = "||";
        branchCondition["symbolId"] = "operator:LogicalOrExpression:||";

        JsonObject branchType = ParseObject(ir);
        branchType["branches"]!.AsArray()[4]!.AsObject()["conditionAst"]!.AsObject()["typeName"] = "UInt256";

        JsonObject operationOwner = ParseObject(ir);
        JsonObject operation = operationOwner["semantics"]!.AsObject()["operations"]!.AsArray()[0]!.AsObject();
        operation["binding"]!.AsObject()["member"] = "DifferentSourceMember";

        JsonObject coordinatedOperationOwner = ParseObject(ir);
        JsonObject coordinatedOperation = coordinatedOperationOwner["semantics"]!.AsObject()["operations"]!.AsArray()[0]!.AsObject();
        coordinatedOperation["binding"]!.AsObject()["member"] = "DifferentSourceMember";
        coordinatedOperation["sourceMember"] = "Transaction.DifferentSourceMember";

        JsonObject width = ParseObject(ir);
        width["semantics"]!.AsObject()["widths"]!.AsArray()[0]!.AsObject()["bits"] = 63;

        JsonObject ordinal = ParseObject(ir);
        ordinal["semantics"]!.AsObject()["operations"]!.AsArray()[0]!.AsObject()["ordinal"] = 99;

        foreach (JsonObject mutation in new[]
                 {
                     branchTerminal, branchConjunct, branchType, operationOwner, coordinatedOperationOwner, width, ordinal,
                 })
        {
            Assert.That(() => Extractor.EmitUnvalidatedIrForTest(Encoding.UTF8.GetBytes(mutation.ToJsonString())),
                Throws.TypeOf<ExtractionException>());
        }

        JsonObject adapter = ParseObject(ir);
        adapter["semantics"]!.AsObject()["adapterPremises"]!.AsArray()[0]!.AsObject()["domainPredicate"] = "changedAdapterPremise";
        byte[] mutatedAdapter = Encoding.UTF8.GetBytes(adapter.ToJsonString());
        Assert.That(() => Extractor.EmitUnvalidatedIrForTest(mutatedAdapter), Throws.TypeOf<ExtractionException>());

        JsonObject adapterBinding = ParseObject(ir);
        adapterBinding["semantics"]!.AsObject()["adapterPremises"]!.AsArray()[1]!.AsObject()["bindings"]!
            .AsArray()[0]!.AsObject()["member"] = "DifferentSourceMember";
        Assert.That(() => Extractor.EmitUnvalidatedIrForTest(Encoding.UTF8.GetBytes(adapterBinding.ToJsonString())),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Tampered_ir_and_manifest_are_rejected()
    {
        string root = FindRepoRoot();
        string generated = Path.Combine(root, "tools", "Evm", "Lean", "OrdinaryStatefulAdmissionPrefixExtractor", "Generated");
        byte[] ir = File.ReadAllBytes(Path.Combine(generated, "OrdinaryStatefulAdmissionPrefix.ir.json"));
        byte[] manifest = File.ReadAllBytes(Path.Combine(generated, "OrdinaryStatefulAdmissionPrefix.source-manifest.json"));

        JsonObject irMutation = JsonNode.Parse(ir)!.AsObject();
        irMutation["kernel"] = "mutated";
        JsonObject manifestMutation = JsonNode.Parse(manifest)!.AsObject();
        manifestMutation["semanticIrSha256"] = new string('0', 64);

        Assert.That(() => Extractor.ValidateSerializedIr(ir, Encoding.UTF8.GetBytes(irMutation.ToJsonString())),
            Throws.TypeOf<ExtractionException>());
        Assert.That(() => Extractor.ValidateSerializedManifest(manifest, Encoding.UTF8.GetBytes(manifestMutation.ToJsonString())),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Serialized_artifacts_reject_null_structural_values()
    {
        string root = FindRepoRoot();
        string generated = Path.Combine(root, "tools", "Evm", "Lean", "OrdinaryStatefulAdmissionPrefixExtractor", "Generated");
        byte[] ir = File.ReadAllBytes(Path.Combine(generated, "OrdinaryStatefulAdmissionPrefix.ir.json"));
        byte[] manifest = File.ReadAllBytes(Path.Combine(generated, "OrdinaryStatefulAdmissionPrefix.source-manifest.json"));

        JsonObject nullRoute = ParseObject(ir);
        nullRoute["route"] = null;
        JsonObject nullStage = ParseObject(ir);
        nullStage["stages"]!.AsArray()[0] = null;
        JsonObject nullSource = ParseObject(ir);
        nullSource["sources"]!.AsArray()[0] = null;
        JsonObject nullManifestArtifact = ParseObject(manifest);
        nullManifestArtifact["ir"] = null;
        JsonObject nullManifestBinding = ParseObject(manifest);
        nullManifestBinding["bindings"]!.AsArray()[0] = null;

        foreach (JsonObject mutation in new[] { nullRoute, nullStage, nullSource })
        {
            Assert.That(() => Extractor.ValidateSerializedIr(ir, Encoding.UTF8.GetBytes(mutation.ToJsonString())),
                Throws.TypeOf<ExtractionException>());
        }

        foreach (JsonObject mutation in new[] { nullManifestArtifact, nullManifestBinding })
        {
            Assert.That(() => Extractor.ValidateSerializedManifest(manifest, Encoding.UTF8.GetBytes(mutation.ToJsonString())),
                Throws.TypeOf<ExtractionException>());
        }
    }

    private static JsonObject ParseObject(byte[] bytes) => JsonNode.Parse(bytes)!.AsObject();

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(Environment.CurrentDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, Extractor.TransactionProcessorPath.Replace('/', Path.DirectorySeparatorChar))))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Nethermind source root for extractor tests.");
    }

    private sealed class SourceFixture : IDisposable
    {
        private static readonly string[] Paths =
        [
            Extractor.TransactionProcessorPath,
            Extractor.OptionsPath,
            Extractor.TransactionExtensionsPath,
            Extractor.TransactionPath,
            Extractor.TxTypePath,
            Extractor.TxTypeExtensionsPath,
            Extractor.BlobGasCalculatorPath,
            Extractor.Eip4844ConstantsPath,
            Extractor.RoutingKernelPath,
            Extractor.MainnetDiPath,
            Extractor.SystemProcessorPath,
            Extractor.XdcProcessorPath,
            Extractor.TaikoProcessorPath,
            Extractor.WorldStatePath,
            Extractor.StateProviderPath,
        ];

        private readonly string _temporaryPath;

        public SourceFixture()
        {
            Directory.CreateDirectory(ExtractorTests.ScratchRoot);
            string basePath = ExtractorTests.ScratchRoot;
            _temporaryPath = Path.Combine(basePath, "ordinary-stateful-admission-test-" + Guid.NewGuid().ToString("N"));
            Root = Path.Combine(_temporaryPath, "source");
            Output = Path.Combine(_temporaryPath, "output");
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Output);

            string sourceRoot = FindRepoRoot();
            Copy(sourceRoot, "tools/Evm/Lean/OrdinaryStatefulAdmissionPrefixExtractor/Admission/ProductionClosure.txt");
            foreach (string relativePath in Paths)
            {
                Copy(sourceRoot, relativePath);
            }
        }

        public string Root { get; }

        public string Output { get; }

        public void Replace(string relativePath, string original, string replacement)
        {
            string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            string text = File.ReadAllText(path);
            Assert.That(text, Does.Contain(original), $"Mutation anchor was missing in {relativePath}.");
            File.WriteAllText(path, text.Replace(original, replacement, StringComparison.Ordinal));
        }

        public void Dispose()
        {
            if (Directory.Exists(_temporaryPath))
            {
                Directory.Delete(_temporaryPath, recursive: true);
            }
        }

        private void Copy(string sourceRoot, string relativePath)
        {
            string source = Path.Combine(sourceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            string destination = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path;

        public TemporaryDirectory()
        {
            Directory.CreateDirectory(ExtractorTests.ScratchRoot);
            _path = Path.Combine(ExtractorTests.ScratchRoot, "ordinary-stateful-admission-result-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_path);
        }

        public string Location => _path;

        public void Dispose()
        {
            if (Directory.Exists(_path))
            {
                Directory.Delete(_path, recursive: true);
            }
        }
    }
}
