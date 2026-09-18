// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.OrdinaryPostNonceDispatchExtractor.Test;

[TestFixture]
public sealed class ExtractorTests
{
    [Test]
    public void Production_sources_extract_deterministically()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory first = new();
        using TemporaryDirectory second = new();
        ExtractionResult a = Extractor.Extract(root, first.Location, Path.Combine(first.Location, "OrdinaryPostNonceDispatch.lean"));
        ExtractionResult b = Extractor.Extract(root, second.Location, Path.Combine(second.Location, "OrdinaryPostNonceDispatch.lean"));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(a.SourceCount, Is.EqualTo(40));
            Assert.That(a.BranchCount, Is.EqualTo(9));
            Assert.That(a.EffectCount, Is.EqualTo(6));
            Assert.That(File.ReadAllBytes(a.IrPath), Is.EqualTo(File.ReadAllBytes(b.IrPath)));
            Assert.That(File.ReadAllBytes(a.ManifestPath), Is.EqualTo(File.ReadAllBytes(b.ManifestPath)));
            Assert.That(File.ReadAllBytes(a.LeanPath), Is.EqualTo(File.ReadAllBytes(b.LeanPath)));
        }
    }

    [Test]
    public void Checked_in_artifacts_match_fresh_extraction()
    {
        string root = FindRepoRoot();
        string package = Path.Combine(root, "tools", "Evm", "Lean", "OrdinaryPostNonceDispatchExtractor");
        string generated = Path.Combine(package, "Generated");
        Assert.That(() => Extractor.ValidateExistingArtifacts(root, generated, Path.Combine(generated, "OrdinaryPostNonceDispatch.lean")), Throws.Nothing);
    }

    [Test]
    public void Generated_Lean_is_theorem_free_and_contains_typed_dispatch()
    {
        string root = FindRepoRoot();
        string path = Path.Combine(root, "tools", "Evm", "Lean", "OrdinaryPostNonceDispatchExtractor", "Generated", "OrdinaryPostNonceDispatch.lean");
        string lean = File.ReadAllText(path);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(lean, Does.Contain("inductive Preload"));
            Assert.That(lean, Does.Contain("codeHandle"));
            Assert.That(lean, Does.Contain("delegation"));
            Assert.That(lean, Does.Contain("EscapedLookup"));
            Assert.That(lean, Does.Contain("EscapedPrecommit"));
            Assert.That(lean, Does.Contain("GasRejected"));
            Assert.That(lean, Does.Contain("SimpleHandoff"));
            Assert.That(lean, Does.Contain("EvmHandoff"));
            Assert.That(lean, Does.Contain("commitRoots"));
            Assert.That(lean, Does.Contain("structure CommitRequest"));
            Assert.That(lean, Does.Contain("tracer := if input.tracer.isTracingState then .tracing input.tracer else .null"));
            Assert.That(lean, Does.Contain("followDelegation := !input.spec.eip8037Enabled"));
            Assert.That(lean, Does.Contain("def classifyAvailableGas (condition : Bool) : String :="));
            Assert.That(lean, Does.Contain("failure := some (classifyAvailableGas input.availableGas.accepted)"));
            Assert.That(lean, Does.Contain("else \"GasLimitBelowIntrinsicGas\""));
            Assert.That(lean, Does.Contain("zeroGas"));
            Assert.That(lean, Does.Not.Contain("theorem "));
            Assert.That(lean, Does.Not.Contain("sorry"));
            Assert.That(lean, Does.Not.Contain("admit"));
            Assert.That(lean, Does.Not.Contain("axiom "));
        }
    }

    [Test]
    public void Refinement_contains_required_universal_and_erasure_names()
    {
        string root = FindRepoRoot();
        string path = Path.Combine(root, "tools", "Evm", "Lean", "OrdinaryPostNonceDispatchExtractor", "Refinement", "OrdinaryPostNonceDispatch.lean");
        string refinement = File.ReadAllText(path);
        Assert.That(refinement, Does.Contain("generated_postNonceDispatch_refines_reference"));
        Assert.That(refinement, Does.Contain("erases_to_lifecycleControl"));
        Assert.That(refinement, Does.Contain("futureEvmHandoff_bridge_obligation"));
        Assert.That(refinement, Does.Contain("Generated.TransactionProcessorLifecycle"));
    }

    [Test]
    public void Ir_preserves_exact_stage_branch_effect_and_terminal_order()
    {
        string root = FindRepoRoot();
        string path = Path.Combine(root, "tools", "Evm", "Lean", "OrdinaryPostNonceDispatchExtractor", "Generated", "OrdinaryPostNonceDispatch.ir.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        string[] stages = document.RootElement.GetProperty("dispatch").GetProperty("stages").EnumerateArray().Select(stage => stage.GetProperty("id").GetString()!).ToArray();
        string[] branches = document.RootElement.GetProperty("dispatch").GetProperty("branches").EnumerateArray().Select(branch => branch.GetProperty("id").GetString()!).ToArray();
        string[] effects = document.RootElement.GetProperty("dispatch").GetProperty("effects").EnumerateArray().Select(effect => effect.GetProperty("id").GetString()!).ToArray();
        Assert.That(stages, Is.EqualTo(new[] { "prepareSimpleTransferFastPath", "commitBeforeExecution", "calculateAvailableGas", "dispatchSimpleTransfer", "dispatchEvm" }));
        Assert.That(branches, Is.EqualTo(new[] { "prepareInitializesPreloadedOutputs", "prepareNoRecipient", "prepareNotCandidate", "lookupEscapes", "lookupReturns", "commitEscapes", "gasRejected", "simpleHandoff", "evmHandoff" }));
        Assert.That(effects, Is.EqualTo(new[] { "preloadOutputs", "codeLookup", "precommit", "gasFailure", "simpleHandoff", "evmHandoff" }));
    }

    [Test]
    public void Compiler_reference_provenance_is_carried_by_ir_and_manifest()
    {
        string root = FindRepoRoot();
        string generated = Path.Combine(root, "tools", "Evm", "Lean", "OrdinaryPostNonceDispatchExtractor", "Generated");
        using JsonDocument ir = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(generated, "OrdinaryPostNonceDispatch.ir.json")));
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(generated, "OrdinaryPostNonceDispatch.source-manifest.json")));
        JsonElement[] irReferences = ir.RootElement.GetProperty("compilerReferences").EnumerateArray().ToArray();
        JsonElement[] manifestReferences = manifest.RootElement.GetProperty("compilerReferences").EnumerateArray().ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(irReferences, Is.Not.Empty);
            Assert.That(manifestReferences.Length, Is.EqualTo(irReferences.Length));
            Assert.That(irReferences.Any(reference =>
                reference.GetProperty("path").GetString() == "src/Nethermind/artifacts/bin/Nethermind.Init/release/Nethermind.Evm.dll" &&
                reference.GetProperty("selected").GetBoolean()), Is.True);
            Assert.That(irReferences.All(reference => reference.GetProperty("path").GetString()!.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)), Is.True);
            Assert.That(irReferences.All(reference => reference.GetProperty("sha256").GetString()!.Length == 64), Is.True);
            Assert.That(irReferences.All(reference => reference.GetProperty("mvid").GetString()!.Length == 36), Is.True);
            Assert.That(irReferences.All(reference => reference.TryGetProperty("dependencies", out JsonElement dependencies) && dependencies.ValueKind == JsonValueKind.Array), Is.True);
            Assert.That(manifestReferences.All(reference => reference.TryGetProperty("dependencies", out JsonElement dependencies) && dependencies.ValueKind == JsonValueKind.Array), Is.True);

            JsonElement boundary = ir.RootElement.GetProperty("dispatch").GetProperty("postNonceBoundary");
            Assert.That(boundary.GetProperty("successPredicate").GetString(), Is.EqualTo("!(result=ValidateSender(tx,header,spec,tracer,opts))||!(result=BuyGas(tx,spec,tracer,opts,effectiveGasPrice,outUInt256premiumPerGas,outUInt256senderReservedGasPayment,outUInt256blobBaseFee))||!(result=IncrementNonce(tx,header,spec,tracer,opts))"));
            Assert.That(boundary.GetProperty("closedNormalExitBlocks").GetArrayLength(), Is.GreaterThan(0));
            Assert.That(boundary.GetProperty("successPredicateBinding").GetProperty("member").GetString(), Is.EqualTo("postNonceSuccessPredicate"));
        }
    }

    [TestCase(Extractor.TransactionProcessorPath, "preloadedCodeInfo = null;", "preloadedCodeInfo = CodeInfo.Empty;", "Missing exact assignment preloadedCodeInfo = null")]
    [TestCase(Extractor.TransactionProcessorPath, "preloadedDelegationAddress = null;", "preloadedDelegationAddress = Address.Zero;", "Missing exact assignment preloadedDelegationAddress = null")]
    [TestCase(Extractor.TransactionProcessorPath, "Address? recipient = tx.To;", "Address? recipient = tx.SenderAddress;", "fast-path recipient must be initialized from tx.To")]
    [TestCase(Extractor.TransactionProcessorPath, "!isCodeOverridable && tx.AuthorizationList is null && !ForceSimpleTransferDisabled", "isCodeOverridable && tx.AuthorizationList is null && !ForceSimpleTransferDisabled", "The simple-transfer candidate guard changed.")]
    [TestCase(Extractor.TransactionProcessorPath, "delegationAddress is null && codeInfo.IsEmpty", "codeInfo.IsEmpty && delegationAddress is null", "HasNoExecutableCode no longer has delegation-first short-circuit semantics")]
    [TestCase(Extractor.TransactionProcessorPath, "followDelegation: !spec.IsEip8037Enabled", "followDelegation: spec.IsEip8037Enabled", "Missing admitted GetCachedCodeInfo invocation")]
    [TestCase(Extractor.TransactionProcessorPath, "commitRoots: false", "commitRoots: true", "Missing direct Commit invocation")]
    [TestCase(Extractor.TransactionProcessorPath, "bool restore = opts.HasFlag(ExecutionOptions.Restore);", "bool restore = opts.HasFlag(ExecutionOptions.Restore) || opts.HasFlag(ExecutionOptions.Commit);", "Restore must retain the exact HasFlag")]
    [TestCase(Extractor.TransactionProcessorPath, "bool commit = opts.HasFlag(ExecutionOptions.Commit) || (!opts.HasFlag(ExecutionOptions.SkipValidation) && !spec.IsEip658Enabled);", "bool commit = opts.HasFlag(ExecutionOptions.Commit) || (!opts.HasFlag(ExecutionOptions.SkipValidation) && !spec.IsEip658Enabled) || opts.HasFlag(ExecutionOptions.Restore);", "Commit must retain the exact effective-commit initializer AST")]
    [TestCase(Extractor.TransactionProcessorPath, "CalculateAvailableGas(tx, spec, in intrinsicGas, out TGasPolicy gasAvailable)", "CalculateAvailableGas(tx, spec, in intrinsicGas, out _)", "error CS0103: The name 'gasAvailable' does not exist")]
    [TestCase(Extractor.TransactionProcessorPath, "TGasPolicy.TryCreateAvailableFromIntrinsic(tx.GasLimit, intrinsicGas.Standard, spec, out gasAvailable)", "TGasPolicy.TryCreateAvailableFromIntrinsic(tx.GasLimit, intrinsicGas.Standard, spec, out gasAvailable) || true", "CalculateAvailableGas must retain its exact source-bound condition")]
    [TestCase(Extractor.TransactionProcessorPath, "? TransactionResult.Ok", "? TransactionResult.GasLimitBelowIntrinsicGas", "CalculateAvailableGas must retain its exact source-bound condition")]
    [TestCase(Extractor.TransactionProcessorPath, ": TransactionResult.GasLimitBelowIntrinsicGas;", ": TransactionResult.Ok;", "CalculateAvailableGas must retain its exact source-bound condition")]
    [TestCase(Extractor.TransactionProcessorPath, "Address? simpleTransferRecipient = PrepareSimpleTransferFastPath(tx, spec, out CodeInfo? preloadedCodeInfo, out Address? preloadedDelegationAddress);", "Address? simpleTransferRecipient = tx.To;", "error CS0103: The name 'preloadedCodeInfo' does not exist")]
    [TestCase(Extractor.TransactionProcessorPath, "ExecuteSimpleTransfer(tx, header, spec, tracer, opts, restore, commit, deleteCallerAccount, simpleTransferRecipient", "ExecuteSimpleTransfer(tx, spec, header, tracer, opts, restore, commit, deleteCallerAccount, simpleTransferRecipient", "error CS1503: Argument 2: cannot convert from 'Nethermind.Core.Specs.IReleaseSpec' to 'Nethermind.Core.BlockHeader'")]
    [TestCase(Extractor.TransactionProcessorPath, "ExecuteEvmTransaction(tx, header, spec, tracer, opts, restore, commit, deleteCallerAccount", "ExecuteEvmTransaction(tx, spec, header, tracer, opts, restore, commit, deleteCallerAccount", "error CS1503: Argument 2: cannot convert from 'Nethermind.Core.Specs.IReleaseSpec' to 'Nethermind.Core.BlockHeader'")]
    [TestCase(Extractor.TransactionProcessorPath, "!(result = IncrementNonce(tx, header, spec, tracer, opts))", "(result = IncrementNonce(tx, header, spec, tracer, opts))", "The post-IncrementNonce success edge must retain the exact admitted validation/buy-gas/nonce predicate.")]
    [TestCase(Extractor.TransactionProcessorPath, "if (!(result = CalculateAvailableGas(tx, spec, in intrinsicGas, out TGasPolicy gasAvailable)))", "if (!(result = CalculateAvailableGas(tx, spec, in intrinsicGas, out TGasPolicy gasAvailable)) && opts.HasFlag(ExecutionOptions.Warmup))", "The CalculateAvailableGas failure guard must retain the exact admitted predicate.")]
    [TestCase(Extractor.TransactionProcessorPath, "Address? simpleTransferRecipient = PrepareSimpleTransferFastPath(tx, spec, out CodeInfo? preloadedCodeInfo, out Address? preloadedDelegationAddress);", "return result;\n\n            Address? simpleTransferRecipient = PrepareSimpleTransferFastPath(tx, spec, out CodeInfo? preloadedCodeInfo, out Address? preloadedDelegationAddress);", "The post-IncrementNonce success edge has normal exits before CalculateAvailableGas")]
    [TestCase(Extractor.EthereumGasPolicyPath, "available = default;", "available = new EthereumGasPolicy();", "EthereumGasPolicy must preserve the default available-gas out value on rejection.")]
    [TestCase(Extractor.CodeInfoPath, "public bool IsEmpty => ReferenceEquals(_analyzer, _emptyAnalyzer);", "public bool IsEmpty => false;", "CodeInfo.IsEmpty must retain the analyzer-sentinel predicate.")]
    [TestCase(Extractor.TransactionPath, "public AuthorizationTuple[]? AuthorizationList { get; set; }", "public AuthorizationTuple[] AuthorizationList { get; set; }", "Transaction.To and Transaction.AuthorizationList must retain their admitted nullable types")]
    [TestCase(Extractor.WorldStatePath, "_stateProvider.Commit(releaseSpec, tracer, commitRoots, isGenesis);", "_stateProvider.Commit(releaseSpec, tracer, isGenesis, commitRoots);", "Missing admitted Commit invocation in Commit.")]
    [TestCase(Extractor.VirtualMachineSignaturePath, "namespace Nethermind.Evm;", "namespace Nethermind.Other;", "VirtualMachineStatics.RestoreRipemdTouch source must contain exactly one Nethermind.Evm.VirtualMachineStatics declaration.")]
    [TestCase(Extractor.VirtualMachineSignaturePath, "public static class VirtualMachineStatics", "public static class VirtualMachineStatics<TGasPolicy>", "VirtualMachineStatics.RestoreRipemdTouch source must contain exactly one Nethermind.Evm.VirtualMachineStatics declaration.")]
    [TestCase(Extractor.VirtualMachineSignaturePath, "internal static void RestoreRipemdTouch", "internal static int RestoreRipemdTouch", "VirtualMachineStatics.RestoreRipemdTouch source declaration must remain internal static void")]
    [TestCase(Extractor.VirtualMachineSignaturePath, "internal static void RestoreRipemdTouch", "internal void RestoreRipemdTouch", "VirtualMachineStatics.RestoreRipemdTouch source declaration must remain internal static void")]
    [TestCase(Extractor.VirtualMachineSignaturePath, "internal static void RestoreRipemdTouch", "public static void RestoreRipemdTouch", "VirtualMachineStatics.RestoreRipemdTouch source declaration must remain internal static void")]
    [TestCase(Extractor.VirtualMachineSignaturePath, "IWorldState worldState, IReleaseSpec spec, bool shouldRestore", "IWorldState worldState, IReleaseSpec spec, int shouldRestore", "VirtualMachineStatics.RestoreRipemdTouch source declaration must remain internal static void")]
    [TestCase(Extractor.VirtualMachineAdapterPath, "internal static void RestoreRipemdTouch", "internal static int RestoreRipemdTouch", "VirtualMachineStatics compiler adapter must remain public static with an empty internal static void RestoreRipemdTouch")]
    public void Source_shape_mutations_fail_closed(string relativePath, string original, string replacement, string expectedFailure)
    {
        using SourceFixture fixture = new();
        fixture.AssertUnchangedExtractionSucceeds();
        fixture.Replace(relativePath, original, replacement);
        ExtractionException exception = Assert.Throws<ExtractionException>(() =>
            Extractor.ExtractWithoutReviewedAdmissionForTest(fixture.Root, fixture.Output, Path.Combine(fixture.Output, "mutated.lean"), fixture.ReferenceRoot))!;
        Assert.That(exception.Message, Does.Contain(expectedFailure), relativePath);
        Assert.That(Directory.EnumerateFileSystemEntries(fixture.Output), Is.Empty);
    }

    [Test]
    public void Metrics_source_shape_mutation_reports_its_exact_diagnostic_and_path()
    {
        using SourceFixture fixture = new();
        fixture.AssertUnchangedExtractionSucceeds();
        fixture.Replace(Extractor.MetricsPath, "internal static void IncrementCodeReads()", "internal static void IncrementCodeReads(int count)");

        ExtractionException exception = Assert.Throws<ExtractionException>(() =>
            Extractor.ExtractWithoutReviewedAdmissionForTest(fixture.Root, fixture.Output, Path.Combine(fixture.Output, "mutated.lean"), fixture.ReferenceRoot))!;
        string expectedDiagnosticPath = Extractor.CodeRepositoryPath + "(133,";
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exception.Message, Does.Contain(expectedDiagnosticPath));
            Assert.That(exception.Message, Does.Contain("error CS7036"));
        }

        Assert.That(Directory.EnumerateFileSystemEntries(fixture.Output), Is.Empty);
    }

    [Test]
    public void Compiler_reference_inventory_rejects_stale_missing_and_unexpected_binaries()
    {
        string root = FindRepoRoot();
        using CompilerReferenceFixture fixture = new(root);
        string productionEvm = fixture.Path("src/Nethermind/artifacts/bin/Nethermind.Init/release/Nethermind.Evm.dll");
        string toolEvm = fixture.Path("tools/artifacts/bin/Evm/release/Nethermind.Evm.dll");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Sha256File(productionEvm), Is.Not.EqualTo(Sha256File(toolEvm)), "The stale-reference mutation must use a different binary.");
            Assert.That(new FileInfo(productionEvm).Length, Is.EqualTo(new FileInfo(toolEvm).Length));
            Assert.That(AssemblyName.GetAssemblyName(productionEvm).Name, Is.EqualTo("Nethermind.Evm"));
            Assert.That(AssemblyName.GetAssemblyName(toolEvm).Name, Is.EqualTo("Nethermind.Evm"));
            Assert.That(ReadVirtualMachineStaticSignature(productionEvm), Is.EqualTo(ReadVirtualMachineStaticSignature(toolEvm)),
                "The stale-reference mutation must remain signature-compatible with the production VM boundary.");
        }
        File.Copy(toolEvm, productionEvm, overwrite: true);
        AssertReferenceRejected(fixture, "The compiler reference inventory detected byte drift in 'src/Nethermind/artifacts/bin/Nethermind.Init/release/Nethermind.Evm.dll'");
        File.Copy(Path.Combine(root, "src/Nethermind/artifacts/bin/Nethermind.Init/release/Nethermind.Evm.dll"), productionEvm, overwrite: true);

        string missing = fixture.Path("tools/artifacts/bin/Evm/release/Autofac.dll");
        File.Delete(missing);
        AssertReferenceRejected(fixture, "The compiler reference inventory path set changed. Additions: []. Removals: [tools/artifacts/bin/Evm/release/Autofac.dll].");
        File.Copy(Path.Combine(root, "tools/artifacts/bin/Evm/release/Autofac.dll"), missing, overwrite: true);

        string unexpected = fixture.Path("tools/artifacts/bin/Evm/release/Unexpected.dll");
        File.Copy(missing, unexpected, overwrite: false);
        AssertReferenceRejected(fixture, "The compiler reference inventory path set changed. Additions: [tools/artifacts/bin/Evm/release/Unexpected.dll]. Removals: [].");
    }

    private static void AssertReferenceRejected(CompilerReferenceFixture fixture, string expectedMessage)
    {
        if (Directory.Exists(fixture.Output)) Directory.Delete(fixture.Output, recursive: true);
        Directory.CreateDirectory(fixture.Output);
        ExtractionException exception = Assert.Throws<ExtractionException>(() =>
            Extractor.ExtractWithoutReviewedAdmissionForTest(
                fixture.SourceRoot,
                fixture.Output,
                Path.Combine(fixture.Output, "mutated.lean"),
                fixture.Root))!;
        Assert.That(exception.Message, Does.Contain(expectedMessage));
        Assert.That(Directory.EnumerateFileSystemEntries(fixture.Output), Is.Empty);
    }

    [Test]
    public void Semantic_binding_records_member_receiver_and_cfg_metadata()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory output = new();
        ExtractionResult result = Extractor.Extract(root, output.Location, Path.Combine(output.Location, "out.lean"));
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(result.ManifestPath));
        JsonElement[] bindings = manifest.RootElement.GetProperty("bindings").EnumerateArray().ToArray();
        Assert.That(bindings, Is.Not.Empty);
        Assert.That(bindings.Any(binding => binding.GetProperty("targetSymbol").GetString()!.Contains("GetCachedCodeInfo", StringComparison.Ordinal)), Is.True);
        Assert.That(bindings.Any(binding => binding.GetProperty("receiver").GetString()!.Contains("_codeInfoRepository", StringComparison.Ordinal)), Is.True);
        Assert.That(bindings.Any(binding => binding.GetProperty("controlFlowBlock").GetInt32() >= 0), Is.True);
        Assert.That(bindings.Any(binding => binding.GetProperty("containingMember").GetString()!.Contains("Execute", StringComparison.Ordinal)), Is.True);
        Assert.That(bindings.All(binding => binding.GetProperty("candidateReason").GetString() == "None"), Is.True);
        Assert.That(bindings.Any(binding => binding.GetProperty("targetSymbolKind").GetString() == "Method"), Is.True);
        Assert.That(bindings.All(binding => !binding.GetProperty("isErrorSymbol").GetBoolean() && !binding.GetProperty("hasCandidateSymbols").GetBoolean()), Is.True);
        Assert.That(bindings.All(binding => binding.TryGetProperty("sourceSyntax", out JsonElement sourceSyntax) &&
            !string.IsNullOrWhiteSpace(sourceSyntax.GetString()) &&
            binding.TryGetProperty("operationKind", out JsonElement operationKind) &&
            !string.IsNullOrWhiteSpace(operationKind.GetString()) &&
            binding.TryGetProperty("readInside", out JsonElement readInside) && readInside.ValueKind == JsonValueKind.Array &&
            binding.TryGetProperty("writtenInside", out JsonElement writtenInside) && writtenInside.ValueKind == JsonValueKind.Array), Is.True);
    }

    [Test]
    public void Tampered_ir_and_manifest_are_rejected()
    {
        string root = FindRepoRoot();
        string generated = Path.Combine(root, "tools", "Evm", "Lean", "OrdinaryPostNonceDispatchExtractor", "Generated");
        byte[] ir = File.ReadAllBytes(Path.Combine(generated, "OrdinaryPostNonceDispatch.ir.json"));
        byte[] manifest = File.ReadAllBytes(Path.Combine(generated, "OrdinaryPostNonceDispatch.source-manifest.json"));
        JsonObject irMutation = ParseObject(ir);
        irMutation["dispatch"]!.AsObject()["branches"]!.AsArray()[0]!.AsObject()["terminalKind"] = "EvmHandoff";
        JsonObject manifestMutation = ParseObject(manifest);
        manifestMutation["semanticIrSha256"] = new string('0', 64);
        Assert.That(() => Extractor.ValidateSerializedIr(ir, Encoding.UTF8.GetBytes(irMutation.ToJsonString())), Throws.TypeOf<ExtractionException>());
        Assert.That(() => Extractor.ValidateSerializedManifest(manifest, Encoding.UTF8.GetBytes(manifestMutation.ToJsonString())), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Emitter_rejects_unlowered_branch_effect_and_preload_tampering()
    {
        string root = FindRepoRoot();
        string generated = Path.Combine(root, "tools", "Evm", "Lean", "OrdinaryPostNonceDispatchExtractor", "Generated");
        byte[] ir = File.ReadAllBytes(Path.Combine(generated, "OrdinaryPostNonceDispatch.ir.json"));
        foreach ((string path, string value) in new[]
        {
            ("dispatch.branches.0.terminalKind", "EvmHandoff"),
            ("dispatch.effects.0.effect", "HandoffEvm"),
            ("dispatch.candidateFormula", "recipientisnull"),
            ("dispatch.semantics.operations.4.expression", "tampered"),
        })
        {
            JsonObject mutation = ParseObject(ir);
            SetPath(mutation, path, value);
            Assert.That(() => Extractor.EmitUnvalidatedIrForTest(Encoding.UTF8.GetBytes(mutation.ToJsonString())), Throws.TypeOf<ExtractionException>(), path);
        }
    }

    [Test]
    public void Typed_source_trees_and_result_arms_reject_semantic_mutations()
    {
        string root = FindRepoRoot();
        string generated = Path.Combine(root, "tools", "Evm", "Lean", "OrdinaryPostNonceDispatchExtractor", "Generated");
        byte[] ir = File.ReadAllBytes(Path.Combine(generated, "OrdinaryPostNonceDispatch.ir.json"));
        foreach (string path in new[]
        {
            "dispatch.semantics.operations.0.expressionAst.symbol",
            "dispatch.semantics.operations.1.expressionAst.children.1.symbol",
            "dispatch.semantics.gasClassification.successAst.symbol",
            "dispatch.semantics.gasClassification.failureAst.symbol",
            "dispatch.branches.6.conditionAst.symbol",
            "dispatch.semantics.adapterPremises.0.domainAst.symbol",
            "dispatch.semantics.operations.0.binding.sourceSyntax",
        })
        {
            JsonObject mutation = ParseObject(ir);
            SetPath(mutation, path, "tampered");
            Assert.That(() => Extractor.EmitUnvalidatedIrForTest(Encoding.UTF8.GetBytes(mutation.ToJsonString())),
                Throws.TypeOf<ExtractionException>(), path);
        }

    }

    [Test]
    public void Serialized_shape_rejects_branch_effect_order_member_receiver_and_nullable_tampering()
    {
        string root = FindRepoRoot();
        string generated = Path.Combine(root, "tools", "Evm", "Lean", "OrdinaryPostNonceDispatchExtractor", "Generated");
        byte[] ir = File.ReadAllBytes(Path.Combine(generated, "OrdinaryPostNonceDispatch.ir.json"));
        (string path, string? value, bool setNull)[] mutations =
        [
            ("dispatch.branches.4.effects.0", "HandoffEvm", false),
            ("dispatch.branches.6.ordinal", "1", false),
            ("dispatch.semantics.operations.4.binding.member", "wrongMember", false),
            ("dispatch.semantics.operations.4.binding.receiver", "WorldState :: Nethermind.Evm.State.IWorldState", false),
            ("dispatch.semantics.operations.4.binding.targetSymbol", null, true),
            ("dispatch.semantics.gasClassification.failureBinding.isErrorSymbol", "true", false),
            ("dispatch.semantics.gasClassification.failureBinding.hasCandidateSymbols", "true", false),
            ("dispatch.semantics.gasClassification.failureBinding.candidateReason", "OverloadResolutionFailure", false),
            ("dispatch.preloadStates.1", "None", false),
        ];

        foreach ((string path, string? value, bool setNull) in mutations)
        {
            JsonObject mutation = ParseObject(ir);
            if (setNull) SetNullPath(mutation, path);
            else SetPath(mutation, path, value!);
            Assert.That(() => Extractor.EmitUnvalidatedIrForTest(Encoding.UTF8.GetBytes(mutation.ToJsonString())),
                Throws.TypeOf<ExtractionException>(), path);
        }
    }

    [Test]
    public void Preload_is_emitted_as_one_coherent_algebraic_option()
    {
        string root = FindRepoRoot();
        string generated = Path.Combine(root, "tools", "Evm", "Lean", "OrdinaryPostNonceDispatchExtractor", "Generated");
        string ir = File.ReadAllText(Path.Combine(generated, "OrdinaryPostNonceDispatch.ir.json"));
        string lean = File.ReadAllText(Path.Combine(generated, "OrdinaryPostNonceDispatch.lean"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ir, Does.Contain("coherent code/delegation"));
            Assert.That(lean, Does.Contain("inductive Preload"));
            Assert.That(lean, Does.Contain("| loaded (value : LoadedPreload)"));
            Assert.That(lean, Does.Contain("delegation.isSome"));
        }
    }

    private static JsonObject ParseObject(byte[] bytes) => JsonNode.Parse(bytes)?.AsObject() ?? throw new AssertionException("JSON was empty.");

    private static void SetPath(JsonObject root, string path, string value)
    {
        string[] parts = path.Split('.');
        JsonNode node = root;
        for (int index = 0; index < parts.Length - 1; index++)
        {
            node = int.TryParse(parts[index], out int item) ? node.AsArray()[item]! : node.AsObject()[parts[index]]!;
        }

        if (int.TryParse(parts[^1], out int last)) node.AsArray()[last] = value;
        else node.AsObject()[parts[^1]] = value;
    }

    private static void SetNullPath(JsonObject root, string path)
    {
        string[] parts = path.Split('.');
        JsonNode node = root;
        for (int index = 0; index < parts.Length - 1; index++)
        {
            node = int.TryParse(parts[index], out int item) ? node.AsArray()[item]! : node.AsObject()[parts[index]]!;
        }

        if (int.TryParse(parts[^1], out int last)) node.AsArray()[last] = null;
        else node.AsObject()[parts[^1]] = null;
    }

    private static void AppendPath(JsonObject root, string path, string value)
    {
        string[] parts = path.Split('.');
        JsonNode node = root;
        for (int index = 0; index < parts.Length; index++)
        {
            node = int.TryParse(parts[index], out int item) ? node.AsArray()[item]! : node.AsObject()[parts[index]]!;
        }

        node.AsArray().Add(value);
    }

    private static void SetCanonicalSyntax(JsonObject root, string shapePath, string projectedProperty, string canonicalSyntax)
    {
        SetPath(root, shapePath + "." + projectedProperty, canonicalSyntax);
        SetPath(root, shapePath + ".binding.canonicalSyntax", canonicalSyntax);
        SetPath(root, shapePath + ".binding.canonicalSyntaxSha256", Sha256(canonicalSyntax));
    }

    private static string Sha256(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Sha256File(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static string ReadVirtualMachineStaticSignature(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using PEReader peReader = new(stream);
        using MetadataReaderProvider provider = MetadataReaderProvider.FromMetadataImage(peReader.GetMetadata().GetContent());
        MetadataReader reader = provider.GetMetadataReader();
        TypeDefinitionHandle typeHandle = reader.TypeDefinitions.Single(handle =>
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);
            return reader.GetString(type.Namespace) == "Nethermind.Evm" && reader.GetString(type.Name) == "VirtualMachineStatics";
        });
        TypeDefinition typeDefinition = reader.GetTypeDefinition(typeHandle);
        MethodDefinitionHandle methodHandle = typeDefinition.GetMethods().Single(handle =>
            reader.GetString(reader.GetMethodDefinition(handle).Name) == "RestoreRipemdTouch");
        MethodDefinition method = reader.GetMethodDefinition(methodHandle);
        string parameterNames = string.Join(",", method.GetParameters()
            .Select(handle => reader.GetString(reader.GetParameter(handle).Name)));
        return $"{reader.GetString(typeDefinition.Namespace)}.{reader.GetString(typeDefinition.Name)}|" +
            $"{reader.GetString(method.Name)}|{method.Attributes}|{Convert.ToHexString(reader.GetBlobBytes(method.Signature))}|{parameterNames}";
    }

    private static string ReplaceTargetMember(string target, string oldMember, string newMember)
    {
        int offset = target.LastIndexOf(oldMember, StringComparison.Ordinal);
        Assert.That(offset, Is.GreaterThanOrEqualTo(0), $"The binding target did not end in {oldMember}.");
        return target[..offset] + newMember + target[(offset + oldMember.Length)..];
    }

    private static void AssertLeanCompiles(string root, string name, byte[] source)
    {
        string package = Path.Combine(root, "tools", "Evm", "Lean", "OrdinaryPostNonceDispatchExtractor");
        using TemporaryDirectory directory = new();
        string path = Path.Combine(directory.Location, "mutated-" + name + ".lean");
        File.WriteAllBytes(path, source);
        ProcessStartInfo startInfo = new("lake")
        {
            WorkingDirectory = package,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("env");
        startInfo.ArgumentList.Add("lean");
        startInfo.ArgumentList.Add("-DwarningAsError=true");
        startInfo.ArgumentList.Add("-DmaxHeartbeats=800000");
        startInfo.ArgumentList.Add(path);
        using Process process = Process.Start(startInfo) ?? throw new AssertionException("Could not start Lake for semantic mutation compilation.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            Assert.Fail($"Timed out compiling the {name} semantic mutation.");
        }

        Assert.That(process.ExitCode, Is.EqualTo(0), $"The {name} semantic mutation did not typecheck.\n{output}\n{error}");
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) directory = directory.Parent;
        return directory?.FullName ?? throw new AssertionException("Repository root was not found.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Location = Path.Combine(Path.GetTempPath(), "ordinary-post-nonce-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Location);
        }

        internal string Location { get; }

        public void Dispose()
        {
            if (Directory.Exists(Location)) Directory.Delete(Location, recursive: true);
        }
    }

    private sealed class CompilerReferenceFixture : IDisposable
    {
        private static readonly string[] ClosurePaths =
        [
            "src/Nethermind/artifacts/bin/Nethermind.Init/release",
            "tools/artifacts/bin/Evm/release",
            "src/Nethermind/artifacts/bin/Nethermind.Network.Enr.Test/release",
        ];

        internal CompilerReferenceFixture(string sourceRoot)
        {
            SourceRoot = sourceRoot;
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ordinary-post-nonce-reference-" + Guid.NewGuid().ToString("N"));
            Output = System.IO.Path.Combine(Root, "output");
            Directory.CreateDirectory(Root);
            foreach (string relativePath in ClosurePaths)
            {
                string source = System.IO.Path.Combine(SourceRoot, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
                string target = System.IO.Path.Combine(Root, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
                Directory.CreateDirectory(target);
                foreach (string file in Directory.EnumerateFiles(source, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    File.Copy(file, System.IO.Path.Combine(target, System.IO.Path.GetFileName(file)));
                }
            }

            string inventory = System.IO.Path.Combine(SourceRoot, Extractor.CompilerReferenceInventoryPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            string inventoryCopy = System.IO.Path.Combine(Root, Extractor.CompilerReferenceInventoryPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(inventoryCopy)!);
            File.Copy(inventory, inventoryCopy);
        }

        internal string SourceRoot { get; }
        internal string Root { get; }
        internal string Output { get; }

        internal string Path(string relativePath) =>
            System.IO.Path.Combine(Root, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class SourceFixture : IDisposable
    {
        private static readonly string[] SourcePaths =
        [
            Extractor.TransactionProcessorPath, Extractor.ExecutionOptionsPath, Extractor.RoutingKernelPath, Extractor.MainnetDiPath,
            Extractor.CodeRepositoryInterfacePath, Extractor.CodeInfoPath, Extractor.JumpDestinationAnalyzerPath, Extractor.JumpDestinationAnalyzerStandardPath,
            Extractor.CodeRepositoryPath, Extractor.CacheCodeRepositoryPath,
            Extractor.WorldStateInterfacePath, Extractor.WorldStatePath, Extractor.StateProviderPath, Extractor.GasPolicyInterfacePath,
            Extractor.EthereumGasPolicyPath, Extractor.TransactionPath, Extractor.TransactionStdPath,
            Extractor.TransactionProcessorInterfacePath, Extractor.SystemTransactionProcessorPath, Extractor.DispatchFlagsStandardPath,
            Extractor.MetricsPath, Extractor.MetricsStandardPath, Extractor.TransactionSubstatePath, Extractor.TransactionSettlementPath,
            Extractor.VirtualMachineInterfacePath, Extractor.VirtualMachineSignaturePath,
            Extractor.IntrinsicGasCalculatorPath, Extractor.AccountAccessPricingKernelPath, Extractor.PrecompileGasPricingKernelPath,
            Extractor.SStorePricingKernelPath, Extractor.StateGasChargeKernelPath, Extractor.StateGasTransitionAdapterKernelPath,
            Extractor.StateGasTransitionKernelPath, Extractor.TransactionGasInitializationKernelPath,
            Extractor.PartialStorageProviderPath, Extractor.PersistentStorageProviderPath, Extractor.PersistentStorageProviderStandardPath,
            Extractor.TransientStorageProviderPath, Extractor.LocalMetricsFlushStandardPath,
            Extractor.StateChangeTypePath,
        ];

        private static readonly string[] DependencyPaths =
        [Extractor.OrdinaryStatefulIrPath, Extractor.OrdinaryStatefulManifestPath, Extractor.OrdinaryStatefulLeanPath, Extractor.LifecycleLeanPath];

        internal SourceFixture()
        {
            string sourceRoot = FindRepoRoot();
            Root = Path.Combine(Path.GetTempPath(), "ordinary-post-nonce-fixture-" + Guid.NewGuid().ToString("N"));
            ReferenceRoot = sourceRoot;
            Output = Path.Combine(Root, "fixture-output");
            Directory.CreateDirectory(Output);
            foreach (string relativePath in SourcePaths.Concat(DependencyPaths)
                         .Append("tools/Evm/Lean/OrdinaryPostNonceDispatchExtractor/Admission/ProductionClosure.txt")
                         .Append(Extractor.VirtualMachineAdapterPath))
            {
                string source = Path.Combine(sourceRoot, relativePath);
                string target = Path.Combine(Root, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }
        }

        internal string Root { get; }
        internal string ReferenceRoot { get; }
        internal string Output { get; }

        internal void AssertUnchangedExtractionSucceeds()
        {
            string baselineOutput = Path.Combine(Root, "baseline-output");
            Directory.CreateDirectory(baselineOutput);
            try
            {
                ExtractionResult result = Extractor.ExtractWithoutReviewedAdmissionForTest(
                    Root,
                    baselineOutput,
                    Path.Combine(baselineOutput, "baseline.lean"),
                    ReferenceRoot);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(result.SourceCount, Is.EqualTo(40));
                    Assert.That(result.BranchCount, Is.EqualTo(9));
                    Assert.That(result.EffectCount, Is.EqualTo(6));
                    Assert.That(File.Exists(result.IrPath), Is.True);
                    Assert.That(File.Exists(result.ManifestPath), Is.True);
                    Assert.That(File.Exists(result.LeanPath), Is.True);
                }
            }
            finally
            {
                if (Directory.Exists(baselineOutput)) Directory.Delete(baselineOutput, recursive: true);
            }
        }

        internal void Replace(string relativePath, string original, string replacement)
        {
            string path = Path.Combine(Root, relativePath);
            string text = File.ReadAllText(path);
            if (!text.Contains(original, StringComparison.Ordinal)) throw new AssertionException($"Mutation text was not found: {original}");
            File.WriteAllText(path, text.Replace(original, replacement, StringComparison.Ordinal), new UTF8Encoding(false));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
