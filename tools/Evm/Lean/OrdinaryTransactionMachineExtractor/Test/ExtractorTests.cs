// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.OrdinaryTransactionMachineExtractor.Test;

[TestFixture]
[NonParallelizable]
public sealed class ExtractorTests
{
    [Test]
    public void Generated_model_is_only_ok_terminal_and_fresh_sequential()
    {
        string lean = Read("Generated", "OrdinaryTransactionMachine.lean");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(lean, Does.Contain("def onlyOkTerminal"));
            Assert.That(lean, Does.Contain("def freshSequentialTracer"));
            Assert.That(lean, Does.Contain("freshSequentialTracer input.tracerSeed input.block.headerGasUsed"));
            Assert.That(lean, Does.Contain("state.currentIndex := 0"));
            Assert.That(lean, Does.Contain("gasHistory := []"));
            Assert.That(lean, Does.Contain("cumulativeReceiptGas := 0"));
            Assert.That(lean, Does.Contain("def receiptObservationMatches"));
            Assert.That(lean, Does.Contain("def gasTotalsObservationMatches"));
            Assert.That(lean, Does.Contain("def sequentialIndexInvariant"));
            Assert.That(lean, Does.Contain("def semanticOperationIds"));
            Assert.That(lean, Does.Contain("def fieldwiseSeamIds"));
            Assert.That(lean, Does.Contain("def NormalReturnPremises.validFor"));
            Assert.That(lean, Does.Contain("!input.isCodeOverridable"));
            Assert.That(lean, Does.Contain("!input.hasAuthorizationList"));
            Assert.That(lean, Does.Contain("!input.forceSimpleTransferDisabled"));
            Assert.That(lean, Does.Contain("def terminalForwardingEvents"));
            Assert.That(lean, Does.Contain("ReceiptTerminalFoldKernel.forwardingEvents"));
            Assert.That(lean, Does.Contain("entry.nestedTracer entry.currentTxTracerIsTracingReceipt"));
            Assert.That(lean, Does.Not.Contain("TransactionResult.Equals"));
            Assert.That(lean, Does.Not.Contain("theorem "));
            Assert.That(lean, Does.Not.Contain("lemma "));
            Assert.That(lean, Does.Not.Contain("example "));
            Assert.That(lean, Does.Not.Contain("axiom "));
            Assert.That(lean, Does.Not.Contain("sorry"));
            Assert.That(lean, Does.Not.Contain("admit"));
        }
    }

    [TestCase("malformed-first", "!entry.wellFormed", "malformedSettled")]
    [TestCase("invalid-first", "!resultIsOk entry.result", "exceptionResult")]
    [TestCase("receipt-index", "actual.index == expected.index", "receiptObservationMatches")]
    [TestCase("start-reset", "currentIndex := 0", "freshSequentialTracer")]
    [TestCase("mark-append", "markAsSuccess", "receiptAppend")]
    [TestCase("end-increment", "currentIndex := state.currentIndex + 1", "currentIndexIncrement")]
    [TestCase("adapter-order", "startNewTxTrace, Event.execute, Event.transactionCommit", "endTxTrace")]
    public void Adversarial_vectors_remain_explicit(string vector, string required, string secondRequired)
    {
        string lean = Read("Generated", "OrdinaryTransactionMachine.lean");
        Assert.That(lean, Does.Contain(required), vector);
        Assert.That(lean, Does.Contain(secondRequired), vector);
    }

    [Test]
    public void Empty_input_has_a_normal_completion_after_block_boundaries()
    {
        string lean = Read("Generated", "OrdinaryTransactionMachine.lean");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(lean, Does.Contain("| [] => .completed state events processed"));
            Assert.That(lean, Does.Contain("Event.foldReturn, Event.transactionsExecuted"));
            Assert.That(lean, Does.Contain("Event.postTransactionCommit"));
        }
    }

    [Test]
    public void BAL_disabled_direct_inner_and_exact_DI_route_are_guarded()
    {
        string source = Read("Extractor.cs");
        string lean = Read("Generated", "OrdinaryTransactionMachine.lean");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source, Does.Contain("RequireSoleReturnInTrueArm"));
            Assert.That(source, Does.Contain("IsDirectReturnInTrueArm"));
            Assert.That(source, Does.Contain("IsDirectGuardStatement"));
            Assert.That(source, Does.Contain("bal.directInnerGuard"));
            Assert.That(source, Does.Contain("IBlockAccessListManager.Enabled"));
            Assert.That(source, Does.Contain("BlockValidationTransactionsExecutor"));
            Assert.That(source, Does.Contain("ParallelBlockValidationTransactionsExecutor"));
            Assert.That(source, Does.Contain("RequireExactBaseRoute"));
            Assert.That(source, Does.Contain("RequireExactDirectExecutorRoute"));
            Assert.That(source, Does.Contain("RequireExactDirectExecutorRegistration"));
            Assert.That(lean, Does.Contain("!input.balEnabled"));
            Assert.That(lean, Does.Contain("!input.parallel"));
        }
    }

    [Test]
    public void ProcessBlock_callback_and_commit_order_are_source_attached()
    {
        string source = Read("Extractor.cs");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source, Does.Contain("block.startTrace"));
            Assert.That(source, Does.Contain("block.preCommit"));
            Assert.That(source, Does.Contain("block.fold"));
            Assert.That(source, Does.Contain("block.transactionsExecuted"));
            Assert.That(source, Does.Contain("block.postCommit"));
            Assert.That(source, Does.Contain("RequirePostDominates(processBlock, transactionsExecuted, blockFold"));
            Assert.That(source, Does.Contain("RequirePostDominates(processBlock, blockPostCommit, transactionsExecuted"));
        }
    }

    [Test]
    public void Exact_commit_context_fast_path_and_forwarding_routes_are_source_attached()
    {
        string source = Read("Extractor.cs");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source, Does.Contain("adapter.commitExecute"));
            Assert.That(source, Does.Contain("adapter.commitProcess"));
            Assert.That(source, Does.Contain("di.adapterFactory"));
            Assert.That(source, Does.Contain("di.createExecuteAdapter"));
            Assert.That(source, Does.Contain("di.validationModule"));
            Assert.That(source, Does.Contain("di.blockProcessorExecutorStore"));
            Assert.That(source, Does.Contain("di.decoratorInnerParameter"));
            Assert.That(source, Does.Contain("di.directAdapterParameter"));
            Assert.That(source, Does.Contain("di.executeProcessorParameter"));
            Assert.That(source, Does.Contain("BindFluentInvocation"));
            Assert.That(source, Does.Contain("RequireFluentOrder"));
            Assert.That(source, Does.Contain("context.executeAdapter"));
            Assert.That(source, Does.Contain("context.directExecutor"));
            Assert.That(source, Does.Contain("context.decoratorBal"));
            Assert.That(source, Does.Contain("context.decoratorInner"));
            Assert.That(source, Does.Contain("context.balStore"));
            Assert.That(source, Does.Contain("dispatch.fastPathCandidate"));
            Assert.That(source, Does.Contain("dispatch.noExecutableCode"));
            Assert.That(source, Does.Contain("tracer.nestedForwardGuard"));
            Assert.That(source, Does.Contain("tracer.currentForwardGuard"));
            Assert.That(source, Does.Contain("EnsureWithin(leanOutputPath is null ? root : outputDirectory, leanPath)"));
            Assert.That(source, Does.Contain("new ArtifactIdentity(Normalize(DefaultLeanPath), Sha256(leanBytes))"));
        }
    }

    [Test]
    public void Every_semantic_binder_uses_the_hard_coded_symbol_identity_ledgers()
    {
        string source = Read("Extractor.cs");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source, Does.Contain("SourceMemberLedger"));
            Assert.That(source, Does.Contain("SourceOwnerLedger"));
            Assert.That(source, Does.Contain("InvocationTargetLedger"));
            Assert.That(source, Does.Contain("DeclarationLedger"));
            Assert.That(source, Does.Contain("AssignmentTargetLedger"));
            Assert.That(source, Does.Contain("MemberTargetLedger"));
            Assert.That(source, Does.Contain("ResolveExactType"));
            Assert.That(source, Does.Contain("SymbolEqualityComparer.Default.Equals"));
            Assert.That(source, Does.Contain("Source-member identity rejected an unledgered competing declaration"));
            Assert.That(source, Does.Contain("Invocation receiver identity mismatch"));
            Assert.That(source, Does.Contain("Invocation target signature identity"));
            Assert.That(source, Does.Contain("ParameterTypeMetadataNames"));
            Assert.That(source, Does.Contain("ConstructorSignatureMatches"));
            Assert.That(source, Does.Contain("Method(SourceSpecs[12].Path, \"Nethermind.Evm.State.IWorldState\", \"Commit\", \"voidCommit(IReleaseSpecreleaseSpec,IWorldStateTracertracer,boolisGenesis=false,boolcommitRoots=true)\""));
            Assert.That(source, Does.Contain("Nethermind.Evm.State.IWorldState\", \"Commit\", 4"));
            Assert.That(source, Does.Not.Contain("Target(\"dispatch.precommit\", \"Nethermind.Evm.State.WorldStateExtensions\""));
            Assert.That(source, Does.Not.Contain("Target(\"finalize.commit\", \"Nethermind.Evm.State.WorldStateExtensions\""));
            Assert.That(source, Does.Contain("ContainerBuilderExtensionsSourcePath"));
            Assert.That(source, Does.Contain("Base-type identity mismatch for route.ethereumGasPolicyBase"));
        }
    }

    [Test]
    public void Verification_runs_two_extractions_and_all_lean_surfaces()
    {
        string verify = Read("Verify.ps1");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(verify, Does.Contain("$scratchGeneratedSecond"));
            Assert.That(verify, Does.Contain("Two-run generated artifact mismatch"));
            Assert.That(verify, Does.Contain("Assert-ByteIdentical $freshPath $freshSecondPath"));
            Assert.That(verify, Does.Contain("--minimum-expected-tests 115"));
            Assert.That(verify, Does.Contain("Specification\\TransactionState.lean"));
            Assert.That(verify, Does.Contain("Specification\\Economics.lean"));
            Assert.That(verify, Does.Contain("Specification\\ExecutionBoundary.lean"));
            Assert.That(verify, Does.Contain("Vectors\\OrdinaryTransactionMachineVectors.lean"));
            Assert.That(verify, Does.Contain("theorem|lemma|example|axiom"));
        }
    }

    [Test]
    public void Compiler_closure_mutations_fail_closed_by_identity()
    {
        string source = Read("Extractor.cs");
        string pins = Read("MACHINE_SOURCE_PINS.json");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source, Does.Contain("Compiler/reference byte drift"));
            Assert.That(source, Does.Contain("Missing compiler/reference binary"));
            Assert.That(source, Does.Contain("duplicate or malformed identity"));
            Assert.That(source, Does.Contain("compiler/reference aggregate changed"));
            Assert.That(source, Does.Contain("Compiler/reference manifest drift"));
            Assert.That(source, Does.Contain("Compiler/reference metadata drift"));
            Assert.That(source, Does.Contain("ValidateCompilerIdentities(root, ir.CompilerReferences)"));
            Assert.That(source, Does.Contain("MachinePinsSha256"));
            Assert.That(source, Does.Contain("ReceiptSourcePinsSha256"));
            Assert.That(source, Does.Contain("semantic operation"));
            Assert.That(source, Does.Contain("fieldwise seam"));
            Assert.That(source, Does.Contain("TransactionResult.Equals"));
            Assert.That(pins, Does.Contain("\"count\": 329"));
            Assert.That(pins, Does.Contain("aggregateSha256"));
        }
    }

    [TestCase("sha")]
    [TestCase("mvid")]
    [TestCase("dependencies")]
    [TestCase("missing")]
    [TestCase("ambiguity")]
    [TestCase("order")]
    public void Compiler_reference_mutations_reach_identity_gate(string mutation)
    {
        string root = FindRepoRoot();
        CompilerReferenceIdentity[] references = Extractor.LoadCompilerReferencesForTest(root);
        CompilerReferenceIdentity[] altered = mutation switch
        {
            "sha" => references.Select((reference, index) => index == 0
                ? reference with { Sha256 = new string('0', 64) }
                : reference).ToArray(),
            "mvid" => references.Select((reference, index) => index == 0
                ? reference with { Mvid = Guid.Empty.ToString("D") }
                : reference).ToArray(),
            "dependencies" => references.Select((reference, index) => index == 0
                ? reference with { Dependencies = ["Changed.Dependency"] }
                : reference).ToArray(),
            "missing" => references.Skip(1).ToArray(),
            "ambiguity" => references.Select((reference, index) => index == 1
                ? reference with { Path = references[0].Path }
                : reference).ToArray(),
            "order" => references.Select((reference, index) => index switch
            {
                0 => references[1],
                1 => references[0],
                _ => reference,
            }).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        ExtractionException exception = Assert.Throws<ExtractionException>(() =>
            Extractor.ValidateCompilerReferencesForTest(root, altered))!;
        Assert.That(exception.Message, Does.Contain("compiler/reference"), mutation);
    }

    [Test]
    public void Partial_and_receipt_terminal_closures_are_explicit()
    {
        string source = Read("Extractor.cs");
        string pins = Read("MACHINE_SOURCE_PINS.json");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source, Does.Contain("IsBlockProcessorPartial"));
            Assert.That(source, Does.Contain("IsBlockAccessListManagerPartial"));
            Assert.That(source, Does.Contain("ReceiptTerminalFoldExtractor/SOURCE_PINS.json"));
            Assert.That(source, Does.Contain("ValidateReceiptTerminalSourcePins"));
            Assert.That(source, Does.Contain("ReadPinnedReceiptBytes"));
            Assert.That(source, Does.Contain("ReceiptSourceManifestSha256"));
            Assert.That(source, Does.Contain("ValidateReceiptSourceFiles"));
            Assert.That(source, Does.Contain("ValidateReceiptManifestCompilerReferences"));
            Assert.That(source, Does.Contain("ValidateReceiptManifestArtifacts"));
            Assert.That(source, Does.Contain("ReceiptTerminalSourceClosureIdentity"));
            Assert.That(source, Does.Contain("ReceiptTerminalSourceClosureMatches"));
            Assert.That(source, Does.Contain("TransactionProcessorAdapterExtensions"), "adapter source identity");
            Assert.That(source, Does.Contain("ProcessTransaction"), "adapter binding");
            Assert.That(source, Does.Contain("Unit(units, AdapterExtensionsSourcePath)"));
            Assert.That(source, Does.Contain("Unit(units, ExecuteAdapterSourcePath)"));
            Assert.That(source, Does.Contain("Unit(units, ProcessorInterfaceSourcePath)"));
            Assert.That(source, Does.Not.Contain("SemanticUnit executeAdapter = Unit(units, SourceSpecs[6].Path)"));
            Assert.That(source, Does.Contain("BindMethod(adapterExtensions, AdapterExtensionsType, \"ProcessTransaction\", 5)"));
            Assert.That(source, Does.Contain("block.balPrepareCall"));
            Assert.That(source, Does.Contain("block.processBlockCall"));
            Assert.That(source, Does.Not.Contain("Boundary(\"executor.invalidThrow\""));
            Assert.That(source, Does.Not.Contain("Boundary(\"executor.gasLimitThrow\""));
            Assert.That(pins, Does.Contain("ReceiptTerminalFoldExtractor/SOURCE_PINS.json"));
            Assert.That(pins, Does.Contain("BlockAccessListManager.TxProcessorPool.cs"));
            Assert.That(pins, Does.Contain("BlockProcessor.SystemContractHandler.cs"));
        }
    }

    [Test]
    public void Receipt_terminal_manifest_drift_remains_fail_closed()
    {
        string source = Read("Extractor.cs");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source, Does.Contain("ReceiptPinsMatch(sources, manifestSources)"));
            Assert.That(source, Does.Contain("ReceiptPinsMatch(bindingSources, manifestBindingSources)"));
            Assert.That(source, Does.Contain("receipt source manifest does not match"));
            Assert.That(source, Does.Contain("ReceiptAccountingManifestSha256"));
            Assert.That(source, Does.Contain("The settled receipt accounting-kernel closure changed"));
        }
    }

    [Test]
    public void Independent_spec_and_fieldwise_bridge_do_not_use_whole_result_equality()
    {
        string specification = Read("Specification", "OrdinaryTransactionMachine.lean");
        string refinement = Read("Refinement", "OrdinaryTransactionMachine.lean");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(specification, Does.Contain("def step"));
            Assert.That(specification, Does.Contain("def fold"));
            Assert.That(specification, Does.Contain("finishCompleted"));
            Assert.That(specification, Does.Contain("[12, 13, 14]"));
            Assert.That(specification, Does.Contain("def terminalForwardingEvents"));
            Assert.That(specification, Does.Contain("if entry.nestedTracer then [8]"));
            Assert.That(specification, Does.Contain("if entry.currentTxTracerIsTracingReceipt then [9]"));
            Assert.That(specification, Does.Contain("!input.isCodeOverridable"));
            Assert.That(specification, Does.Contain("!input.hasAuthorizationList"));
            Assert.That(specification, Does.Contain("!input.forceSimpleTransferDisabled"));
            Assert.That(refinement, Does.Contain("receiptFieldsExtensional"));
            Assert.That(refinement, Does.Contain("gasFieldsExtensional"));
            Assert.That(refinement, Does.Contain("resultFieldsExtensional"));
            Assert.That(refinement, Does.Contain("generatedRun_refines_independentSpec"));
            Assert.That(refinement, Does.Contain("SourceAdapterPremises"));
            Assert.That(refinement, Does.Contain("def FreshSequentialTracer"));
            Assert.That(refinement, Does.Contain("fresh_sequential_tracer_is_explicit"));
            Assert.That(refinement, Does.Contain("ReceiptTerminalObservation"));
            Assert.That(refinement, Does.Contain("ReceiptTerminalChain"));
            Assert.That(refinement, Does.Contain("startNewTxTraceNormalReturn"));
            Assert.That(refinement, Does.Contain("transactionProcessorNormalReturn"));
            Assert.That(refinement, Does.Contain("NormalReturnObserved"));
            Assert.That(refinement, Does.Contain("EntrySourceNormalityChain"));
            Assert.That(refinement, Does.Contain("settledReturnObservation : settledEntryInvariant entry"));
            Assert.That(refinement, Does.Contain("hAdapter.perEntry.settled"));
            Assert.That(refinement, Does.Contain("adapterSuppliedFoldDomain"));
            Assert.That(refinement, Does.Contain("isCodeOverridable := input.isCodeOverridable"));
            Assert.That(refinement, Does.Contain("nestedTracer := entry.nestedTracer"));
            Assert.That(refinement, Does.Contain("executorNormalReturn"));
            Assert.That(refinement, Does.Contain("executorNormalReturn : NormalReturnObserved premises.executorReturn"));
            Assert.That(refinement, Does.Contain("transactionsExecutedSubscriberNormalReturn"));
            Assert.That(refinement, Does.Contain("postTransactionCommitStateNormalReturn"));
            Assert.That(refinement, Does.Contain("SourceAdapterPremises.validFor"));
            Assert.That(refinement, Does.Not.Contain("sourceCorrespondence"));
            Assert.That(refinement, Does.Not.Contain("startNewTxTraceNormalReturn : Prop"));
            Assert.That(refinement, Does.Not.Contain("executorNormalReturn : Prop"));
            Assert.That(refinement, Does.Not.Contain("transactionProcessorNormalReturn : Prop"));
            Assert.That(refinement, Does.Not.Contain("normalReturnGate : G.NormalReturnPremises.validFor"));
            Assert.That(refinement, Does.Contain("foldEntries_completed_fields"));
            Assert.That(refinement, Does.Contain("malformed_settled_entry_rejects_without_prefix_mutation"));
            Assert.That(refinement, Does.Contain("non_ok_result_rejects_without_prefix_mutation"));
            Assert.That(refinement, Does.Contain("induction entries"));
            Assert.That(refinement, Does.Not.Contain("hObservation"));
            Assert.That(refinement, Does.Not.Contain("TransactionResult.Equals"));
            Assert.That(refinement, Does.Not.Contain("generated = specification"));
        }
    }

    [Test]
    public void Source_pin_and_manifest_drafts_are_valid_json_and_mark_reemit_boundary()
    {
        using JsonDocument pins = JsonDocument.Parse(File.ReadAllBytes(PathFrom("MACHINE_SOURCE_PINS.json")));
        string manifest = Read("Generated", "OrdinaryTransactionMachine.source-manifest.json");
        string project = Read("OrdinaryTransactionMachineExtractor.csproj");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pins.RootElement.GetProperty("status").GetString(), Is.EqualTo("ordinary-machine-static-draft"));
            Assert.That(pins.RootElement.GetProperty("sources").GetArrayLength(), Is.EqualTo(50));
            Assert.That(manifest, Does.Contain("static-draft"));
            Assert.That(manifest, Does.Contain("re-emit"));
            Assert.That(File.Exists(PathFrom("SOURCE_PINS.json")), Is.False,
                "MACHINE_SOURCE_PINS.json must remain the sole local source authority");
            Assert.That(project, Does.Not.Contain("SOURCE_PINS.json"));
        }
    }

    [Test]
    public void Required_mutation_vectors_are_named()
    {
        string vectors = Read("Vectors", "OrdinaryTransactionMachineVectors.lean");
        string[] required =
        [
            "conditional-start",
            "conditional-precommit",
            "early-return-before-callback",
            "early-return-before-postcommit",
            "malformed-second-after-valid-prefix",
            "two-entry-indices",
            "gas-total",
            "gas-throw-bypass-in-true-arm",
            "invalid-result-throw-bypass-in-true-arm",
            "bal-inner-return-bypass-in-true-arm",
            "bal-enabled-derivation",
            "di-decorator-route",
            "exact-base-executor",
            "compiler-reference-missing",
            "compiler-reference-ambiguity",
            "receipt-source-pin-drift",
            "receipt-manifest-pin-drift",
            "receipt-accounting-artifact-missing",
            "receipt-compiler-manifest-ambiguity",
            "normal-return-field-false",
            "normality-missing-entry",
            "settled-proof-mismatch",
            "adapter-local-owner",
            "tracer-reset-receipt-gas",
            "tracer-reset-current",
            "tracer-reset-tracer",
            "tracer-reset-receipts-conditional",
            "tracer-reset-block-gas-conditional",
            "tracer-success-conditional",
            "tracer-success-status",
            "tracer-success-forward-order",
            "tracer-forward-none",
            "tracer-forward-nested-only",
            "tracer-forward-current-only",
            "tracer-forward-both",
            "tracer-gas-update-conditional",
            "tracer-end-order",
            "bal-enabled-spec",
            "bal-enabled-guard",
            "parallel-selection",
            "di-manager-route",
            "di-validation-module",
            "fast-path-code-overridable",
            "fast-path-authorization-list",
            "fast-path-force-disabled",
            "fast-path-entry-guard",
            "fast-path-delegation",
            "fast-path-no-code-call",
            "simple-recipient-dead-check",
            "exact-commit-adapter",
            "exact-commit-option",
            "adapter-execute-tracer",
            "executor-adapter-tracer",
            "di-adapter-factory",
            "di-adapter-route",
            "di-adapter-construction",
            "block-executor-capture",
            "decorator-inner-parameter",
            "decorator-bal-parameter",
            "direct-adapter-parameter",
            "execute-processor-parameter",
            "block-context-route",
            "execute-adapter-context",
            "direct-executor-context",
            "decorator-context-bal",
            "decorator-context-inner",
            "decorator-context-order",
            "bal-context-store",
            "nested-forward-guard",
            "current-forward-guard",
            "simple-value-order",
            "simple-access-order",
        ];
        Assert.That(required.All(vector => vectors.Contains(vector, StringComparison.Ordinal)), Is.True);
    }

    [TestCase("adapter-order", "must end tracing after Execute")]
    [TestCase("adapter-early-return", "EndTxTrace must postdominate Execute")]
    [TestCase("adapter-local-owner", "owned by a local function or lambda")]
    [TestCase("adapter-lambda-owner", "owned by a local function or lambda")]
    [TestCase("tracer-reset-index", "set the receipt index to zero")]
    [TestCase("tracer-reset-index-conditional", "index reset must be unconditional")]
    [TestCase("tracer-reset-current", "clear the current transaction")]
    [TestCase("tracer-reset-current-conditional", "current transaction reset must be unconditional")]
    [TestCase("tracer-reset-tracer", "install the null transaction tracer")]
    [TestCase("tracer-reset-receipts", "Expected invocation")]
    [TestCase("tracer-reset-receipts-conditional", "receipt-history reset must be unconditional")]
    [TestCase("tracer-reset-block-gas", "Expected invocation")]
    [TestCase("tracer-reset-block-gas-conditional", "block-gas-history reset must be unconditional")]
    [TestCase("tracer-reset-receipt-gas", "clear cumulative receipt gas")]
    [TestCase("tracer-success-append", "Expected invocation")]
    [TestCase("tracer-success-status", "construct a success receipt")]
    [TestCase("tracer-success-conditional", "Successful receipt append must be unconditional")]
    [TestCase("tracer-success-forward-order", "Nested-tracer forwarding must precede current-tracer forwarding")]
    [TestCase("tracer-gas-update-conditional", "cumulative gas update must be unconditional")]
    [TestCase("tracer-receipt-index", "current transaction index")]
    [TestCase("tracer-start-order", "set CurrentTx before the current tracer")]
    [TestCase("tracer-end-order", "forward before incrementing the receipt index")]
    [TestCase("tracer-end-index", "Expected one post-increment")]
    [TestCase("tracer-end-index-conditional", "increment the receipt index unconditionally")]
    [TestCase("bal-enabled-spec", "derive from the release spec capability")]
    [TestCase("bal-enabled-derived", "exclude genesis blocks after spec derivation")]
    [TestCase("bal-disabled-guard", "Expected one guard containing")]
    [TestCase("bal-disabled-inner", "typed argument order")]
    [TestCase("bal-inner-else", "BAL-disabled direct-inner dispatch must be the sole return")]
    [TestCase("bal-inner-noop", "BAL-disabled direct-inner dispatch must be the sole return")]
    [TestCase("parallel-selection", "parallel-selection guard changed")]
    [TestCase("di-base", "Expected invocation")]
    [TestCase("di-decorator", "Expected invocation")]
    [TestCase("di-manager", "Expected invocation")]
    [TestCase("di-order", "direct executor registration must precede")]
    [TestCase("di-validation-module", "standard block-validation module registration changed")]
    [TestCase("fast-path-code-overridable", "simple-transfer candidate guards changed")]
    [TestCase("fast-path-authorization-list", "simple-transfer candidate guards changed")]
    [TestCase("fast-path-force-disabled", "simple-transfer candidate guards changed")]
    [TestCase("fast-path-entry-guard", "simple-transfer preparation entry guard changed")]
    [TestCase("fast-path-delegation", "no-executable-code/delegation predicate changed")]
    [TestCase("fast-path-no-code-call", "Expected invocation")]
    [TestCase("simple-recipient-dead-check", "Expected invocation")]
    [TestCase("exact-commit-adapter", "Expected invocation")]
    [TestCase("exact-commit-option", "ordinary adapter extension no longer selects exact")]
    [TestCase("adapter-execute-tracer", "no longer forwards the exact transaction and base receipts tracer")]
    [TestCase("executor-adapter-tracer", "direct executor no longer forwards the exact transaction")]
    [TestCase("di-adapter-factory", "Expected invocation")]
    [TestCase("di-adapter-route", "standard transaction adapter registration changed")]
    [TestCase("di-adapter-construction", "Expected one ExecuteTransactionProcessorAdapter object creation")]
    [TestCase("block-executor-capture", "Field initializer _blockTransactionsExecutor")]
    [TestCase("decorator-inner-parameter", "parameter inner")]
    [TestCase("decorator-bal-parameter", "parameter balManager")]
    [TestCase("direct-adapter-parameter", "parameter transactionProcessor")]
    [TestCase("execute-processor-parameter", "parameter transactionProcessor")]
    [TestCase("block-context-route", "BlockProcessor no longer forwards the exact constructed block execution context")]
    [TestCase("execute-adapter-context", "execute adapter no longer forwards the exact block execution context")]
    [TestCase("direct-executor-context", "direct executor no longer forwards the exact block execution context")]
    [TestCase("decorator-context-bal", "Expected invocation")]
    [TestCase("decorator-context-inner", "Expected invocation")]
    [TestCase("decorator-context-order", "BAL context installation must precede inner-executor")]
    [TestCase("bal-context-store", "BAL manager no longer retains the exact block execution context")]
    [TestCase("nested-forward-guard", "nested receipt-forwarding guard changed")]
    [TestCase("current-forward-guard", "current transaction receipt-forwarding guard changed")]
    [TestCase("exact-base-virtual", "Source-member identity mismatch")]
    [TestCase("conditional-start", "StartNewBlockTrace must dominate")]
    [TestCase("conditional-precommit", "pre-transaction CommitState boundary must dominate")]
    [TestCase("early-return-after-fold-before-callback", "TransactionsExecuted must postdominate")]
    [TestCase("early-return-after-callback-before-postcommit", "post-transaction CommitState boundary must postdominate")]
    [TestCase("simple-value-order", "PayValue must precede the recipient balance write")]
    [TestCase("simple-access-order", "Simple-transfer access reporting must precede header gas and fee accounting")]
    [TestCase("gas-throw-conditional", "block-gas guard must directly throw")]
    [TestCase("gas-throw-noop", "Expected invocation")]
    [TestCase("gas-throw-bypass-in-true-arm", "block-gas guard must directly throw")]
    [TestCase("invalid-result-conditional", "invalid-result guard must directly throw")]
    [TestCase("invalid-result-noop", "Expected invocation")]
    [TestCase("invalid-result-throw-bypass-in-true-arm", "invalid-result guard must directly throw")]
    [TestCase("bal-inner-return-bypass-in-true-arm", "BAL-disabled direct-inner dispatch must be the sole return")]
    public void Compile_valid_source_mutations_reach_local_fail_closed_diagnostics(string mutation, string expectedMessage) =>
        AssertRejectedMutation(mutation, expectedMessage, exactDiagnostic: false);

    [Test]
    public void Unmutated_extract_for_test_passes_all_identity_binders()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory output = new(root);
        string leanPath = Path.Combine(output.Path, "baseline.lean");

        ExtractionResult result = Extractor.ExtractForTest(
            root,
            output.Path,
            new Dictionary<string, byte[]>(StringComparer.Ordinal),
            leanOutputPath: leanPath);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.SourceCount, Is.GreaterThan(0));
            Assert.That(result.BindingCount, Is.GreaterThan(0));
            Assert.That(File.Exists(result.IrPath), Is.True);
            Assert.That(File.Exists(result.ManifestPath), Is.True);
            Assert.That(File.Exists(result.LeanPath), Is.True);
            Assert.That(result.LeanPath, Is.EqualTo(leanPath));
        }
    }

    [TestCase("identity-method-moved", "Source-owner identity Nethermind.Evm.TransactionProcessing.SystemTransactionRoutingKernel is not declared by src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionRoutingKernel.cs.")]
    [TestCase("identity-method-competing", "Source-member identity ledger does not exactly cover src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionRoutingKernel.cs:Nethermind.Evm.TransactionProcessing.SystemTransactionRoutingKernel.UseSystemProcessor/2.")]
    [TestCase("identity-method-simple-shadow", "Source-member identity ledger rejected 1 simple-name shadow(s) for Nethermind.Evm.TransactionProcessing.SystemTransactionRoutingKernel.UseSystemProcessor.")]
    [TestCase("identity-constructor-alias-shadow", "Constructor identity mismatch for ExecuteTransactionProcessorAdapter in CreateExecuteAdapter for di.createExecuteAdapter; found 0.")]
    [TestCase("identity-field-local-shadow", "Assignment target identity for context.reset.executionGas is not a field or property.")]
    [TestCase("identity-enum-simple-shadow", "Enum-member identity rejected 1 simple-name shadow(s) for options.commit.")]
    [TestCase("identity-base-type-argument", "Base-type identity mismatch for route.ethereumGasPolicyBase: expected TransactionProcessorBase<EthereumGasPolicy>.")]
    [TestCase("identity-fluent-target-shadow", "Invocation target identity mismatch for di.directExecutor: expected Nethermind.Core.ContainerBuilderExtensions.AddScoped/1.")]
    [TestCase("identity-fluent-type-argument-alias", "Invocation generic type-argument identity mismatch for di.worldState at position 1.")]
    [TestCase("identity-invocation-target-shadow", "Invocation target identity mismatch for route.systemGuard: expected Nethermind.Evm.TransactionProcessing.SystemTransactionRoutingKernel.UseSystemProcessor/2.")]
    public void Compile_valid_identity_mutations_reach_their_unique_anchor_diagnostic(
        string mutation,
        string expectedDiagnostic) =>
        AssertRejectedMutation(mutation, expectedDiagnostic, exactDiagnostic: true);

    private static void AssertRejectedMutation(string mutation, string expectedDiagnostic, bool exactDiagnostic)
    {
        string root = FindRepoRoot();
        (string relativePath, string mutated) = MutateSource(root, mutation);
        string sourcePath = Path.Combine(root, relativePath);
        byte[] original = File.ReadAllBytes(sourcePath);
        Assert.That(Encoding.UTF8.GetString(original), Is.Not.EqualTo(mutated), mutation);
        using TemporaryDirectory output = new(root);

        ExtractionException exception = Assert.Throws<ExtractionException>(() => Extractor.ExtractForTest(root, output.Path,
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [relativePath] = Encoding.UTF8.GetBytes(mutated),
            },
            leanOutputPath: Path.Combine(output.Path, "mutated.lean")))!;

        using (Assert.EnterMultipleScope())
        {
            if (exactDiagnostic)
            {
                Assert.That(exception.Message, Is.EqualTo(expectedDiagnostic), mutation);
            }
            else
            {
                Assert.That(exception.Message, Does.Contain(expectedDiagnostic), mutation);
            }
            Assert.That(exception.Message, Does.Not.Contain("settled receipt-terminal"), mutation);
            Assert.That(exception.Message, Does.Not.Contain("settled receipt source"), mutation);
            Assert.That(exception.Message, Does.Not.Contain("transitive receipt"), mutation);
            Assert.That(exception.Message, Does.Not.Contain("compiler/reference"), mutation);
            Assert.That(Directory.Exists(output.Path) && Directory.EnumerateFileSystemEntries(output.Path).Any(), Is.False, mutation);
            Assert.That(File.ReadAllBytes(sourcePath), Is.EqualTo(original), mutation);
        }
    }

    private const string ProcessorPath = "src/Nethermind/Nethermind.Evm/TransactionProcessing/TransactionProcessor.cs";
    private const string RoutingPath = "src/Nethermind/Nethermind.Evm/TransactionProcessing/SystemTransactionRoutingKernel.cs";
    private const string OptionsPath = "src/Nethermind/Nethermind.Evm/TransactionProcessing/ExecutionOptions.cs";
    private const string ExecuteAdapterPath = "src/Nethermind/Nethermind.Evm/TransactionProcessing/ExecuteTransactionProcessorAdapter.cs";
    private const string ProcessorInterfacePath = "src/Nethermind/Nethermind.Evm/TransactionProcessing/ITransactionProcessor.cs";
    private const string AdapterPath = "src/Nethermind/Nethermind.Consensus/Processing/TransactionProcessorAdapterExtensions.cs";
    private const string ExecutorPath = "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockValidationTransactionsExecutor.cs";
    private const string ParallelExecutorPath = "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.ParallelBlockValidationTransactionsExecutor.cs";
    private const string BalManagerPath = "src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.cs";
    private const string DiPath = "src/Nethermind/Nethermind.Init/Modules/BlockProcessingModule.cs";
    private const string BlockProcessorPath = "src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.cs";
    private const string TracerPath = "src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptsTracer.cs";

    private static (string RelativePath, string Mutated) MutateSource(string root, string mutation)
    {
        string relativePath;
        string source;
        switch (mutation)
        {
            case "adapter-order":
                relativePath = AdapterPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "TransactionResult result = transactionProcessor.Execute(currentTx, receiptsTracer);\n        receiptsTracer.EndTxTrace();",
                    "receiptsTracer.EndTxTrace();\n        TransactionResult result = transactionProcessor.Execute(currentTx, receiptsTracer);"));
            case "adapter-early-return":
                relativePath = AdapterPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "receiptsTracer.EndTxTrace();\n        return result;",
                    "if (result) return result;\n        receiptsTracer.EndTxTrace();\n        return result;"));
            case "adapter-local-owner":
                relativePath = AdapterPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "using ITxTracer tracer = receiptsTracer.StartNewTxTrace(currentTx);\n        TransactionResult result = transactionProcessor.Execute(currentTx, receiptsTracer);\n        receiptsTracer.EndTxTrace();\n        return result;",
                    "TransactionResult Local()\n        {\n            using ITxTracer localTracer = receiptsTracer.StartNewTxTrace(currentTx);\n            TransactionResult localResult = transactionProcessor.Execute(currentTx, receiptsTracer);\n            receiptsTracer.EndTxTrace();\n            return localResult;\n        }\n\n        return Local();"));
            case "adapter-lambda-owner":
                relativePath = AdapterPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "using ITxTracer tracer = receiptsTracer.StartNewTxTrace(currentTx);\n        TransactionResult result = transactionProcessor.Execute(currentTx, receiptsTracer);\n        receiptsTracer.EndTxTrace();\n        return result;",
                    "System.Func<TransactionResult> local = () =>\n        {\n            using ITxTracer localTracer = receiptsTracer.StartNewTxTrace(currentTx);\n            TransactionResult localResult = transactionProcessor.Execute(currentTx, receiptsTracer);\n            receiptsTracer.EndTxTrace();\n            return localResult;\n        };\n\n        return local();"));
            case "tracer-reset-index":
                relativePath = TracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source, "_currentIndex = 0;", "_currentIndex = 1;"));
            case "tracer-reset-index-conditional":
                relativePath = TracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "_currentIndex = 0;",
                    "if (block.Transactions.Length >= 0)\n        {\n            _currentIndex = 0;\n        }"));
            case "tracer-reset-current":
                relativePath = TracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source, "CurrentTx = null;", "CurrentTx = block.Transactions[0];"));
            case "tracer-reset-current-conditional":
                relativePath = TracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "CurrentTx = null;",
                    "if (block.Transactions.Length >= 0)\n        {\n            CurrentTx = null;\n        }"));
            case "tracer-reset-tracer":
                relativePath = TracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "        _currentTxTracer = NullTxTracer.Instance;",
                    "        _currentTxTracer = _currentTxTracer;"));
            case "tracer-reset-receipts":
                relativePath = TracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source, "_txReceipts.Clear();", "_txReceipts.TrimExcess();"));
            case "tracer-reset-receipts-conditional":
                relativePath = TracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "_txReceipts.Clear();",
                    "if (block.Transactions.Length >= 0)\n        {\n            _txReceipts.Clear();\n        }"));
            case "tracer-reset-block-gas":
                relativePath = TracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source, "_cumulativeBlockGasPerTx.Clear();", "_cumulativeBlockGasPerTx.TrimExcess();"));
            case "tracer-reset-block-gas-conditional":
                relativePath = TracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "_cumulativeBlockGasPerTx.Clear();",
                    "if (block.Transactions.Length >= 0)\n        {\n            _cumulativeBlockGasPerTx.Clear();\n        }"));
            case "tracer-reset-receipt-gas":
                relativePath = TracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source, "_cumulativeReceiptGas = 0;", "_cumulativeReceiptGas = 1;"));
            case "tracer-success-append":
                relativePath = TracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "_txReceipts.Add(BuildReceipt(recipient, gasSpent, StatusCode.Success, logs, stateRoot));",
                    "_txReceipts.Add(BuildFailedReceipt(recipient, gasSpent, \"mutated\", stateRoot));"));
            case "tracer-success-status":
                relativePath = TracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "_txReceipts.Add(BuildReceipt(recipient, gasSpent, StatusCode.Success, logs, stateRoot));",
                    "_txReceipts.Add(BuildReceipt(recipient, gasSpent, StatusCode.Failure, logs, stateRoot));"));
            case "tracer-success-conditional":
                relativePath = TracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "_txReceipts.Add(BuildReceipt(recipient, gasSpent, StatusCode.Success, logs, stateRoot));",
                    "if (logs.Length >= 0)\n        {\n            _txReceipts.Add(BuildReceipt(recipient, gasSpent, StatusCode.Success, logs, stateRoot));\n        }"));
            case "tracer-success-forward-order":
                relativePath = TracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "        if (_otherTracer is ITxTracer otherTxTracer)\n        {\n            otherTxTracer.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);\n        }\n\n        if (_currentTxTracer.IsTracingReceipt)\n        {\n            _currentTxTracer.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);\n        }",
                    "        if (_currentTxTracer.IsTracingReceipt)\n        {\n            _currentTxTracer.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);\n        }\n\n        if (_otherTracer is ITxTracer otherTxTracer)\n        {\n            otherTxTracer.MarkAsSuccess(recipient, gasSpent, output, logs, stateRoot);\n        }"));
            case "tracer-gas-update-conditional":
                relativePath = TracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "ulong cumulativeReceiptGas = UpdateCumulativeGasTracking(gasConsumed);",
                    "ulong cumulativeReceiptGas;\n        if (gasConsumed.SpentGas > 0)\n        {\n            cumulativeReceiptGas = UpdateCumulativeGasTracking(gasConsumed);\n        }\n        else\n        {\n            cumulativeReceiptGas = 0;\n        }"));
            case "tracer-receipt-index":
                relativePath = TracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source, "Index = _currentIndex,", "Index = _currentIndex + 1,"));
            case "tracer-start-order":
                relativePath = TracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "CurrentTx = tx;\n        _currentTxTracer = _otherTracer.StartNewTxTrace(tx);",
                    "_currentTxTracer = _otherTracer.StartNewTxTrace(tx);\n        CurrentTx = tx;"));
            case "tracer-end-order":
                relativePath = TracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "_otherTracer.EndTxTrace();\n        _currentIndex++;",
                    "_currentIndex++;\n        _otherTracer.EndTxTrace();"));
            case "tracer-end-index":
                relativePath = TracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source, "_currentIndex++;", "_currentIndex += 2;"));
            case "tracer-end-index-conditional":
                relativePath = TracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "_currentIndex++;",
                    "if (_currentIndex >= 0)\n        {\n            _currentIndex++;\n        }"));
            case "bal-enabled-spec":
                relativePath = BalManagerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "_blockAccessListsEnabled = spec.BlockLevelAccessListsEnabled;",
                    "_blockAccessListsEnabled = false;"));
            case "bal-enabled-derived":
                relativePath = BalManagerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "Enabled = _blockAccessListsEnabled && !suggestedBlock.IsGenesis;",
                    "Enabled = true;"));
            case "bal-disabled-guard":
                relativePath = ParallelExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source, "if (!balManager.Enabled)", "if (balManager.Enabled)"));
            case "bal-disabled-inner":
                relativePath = ParallelExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "inner.ProcessTransactions(block, processingOptions, receiptsTracer, token)",
                    "inner.ProcessTransactions(block, processingOptions, new BlockReceiptsTracer(false), token)"));
            case "bal-inner-else":
                relativePath = ParallelExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "if (!balManager.Enabled)\n            {\n                return inner.ProcessTransactions(block, processingOptions, receiptsTracer, token);\n            }",
                    "if (!balManager.Enabled)\n            {\n            }\n            else\n            {\n                return inner.ProcessTransactions(block, processingOptions, receiptsTracer, token);\n            }"));
            case "bal-inner-noop":
                relativePath = ParallelExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "return inner.ProcessTransactions(block, processingOptions, receiptsTracer, token);",
                    "inner.ProcessTransactions(block, processingOptions, receiptsTracer, token);"));
            case "parallel-selection":
                relativePath = ParallelExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "&& balManager.ParallelExecutionEnabled",
                    "&& !balManager.ParallelExecutionEnabled"));
            case "di-base":
                relativePath = DiPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "BlockProcessor.BlockValidationTransactionsExecutor>()",
                    "BlockProcessor.ParallelBlockValidationTransactionsExecutor>()"));
            case "di-decorator":
                relativePath = DiPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "BlockProcessor.ParallelBlockValidationTransactionsExecutor>();",
                    "BlockProcessor.BlockValidationTransactionsExecutor>();"));
            case "di-manager":
                relativePath = DiPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "AddScoped<IBlockAccessListManager, BlockAccessListManager>()",
                    "AddSingleton<IBlockAccessListManager, BlockAccessListManager>()"));
            case "di-order":
                relativePath = DiPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    ".AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockValidationTransactionsExecutor>()\n            .AddDecorator<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.ParallelBlockValidationTransactionsExecutor>();",
                    ".AddDecorator<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.ParallelBlockValidationTransactionsExecutor>()\n            .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockValidationTransactionsExecutor>();"));
            case "di-validation-module":
                relativePath = DiPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    ".AddSingleton<IBlockValidationModule, StandardBlockValidationModule>()",
                    ".AddScoped<IBlockValidationModule, StandardBlockValidationModule>()"));
            case "fast-path-code-overridable":
                relativePath = ProcessorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "=> !isCodeOverridable && tx.AuthorizationList is null && !ForceSimpleTransferDisabled;",
                    "=> isCodeOverridable && tx.AuthorizationList is null && !ForceSimpleTransferDisabled;"));
            case "fast-path-authorization-list":
                relativePath = ProcessorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "=> !isCodeOverridable && tx.AuthorizationList is null && !ForceSimpleTransferDisabled;",
                    "=> !isCodeOverridable && tx.AuthorizationList is not null && !ForceSimpleTransferDisabled;"));
            case "fast-path-force-disabled":
                relativePath = ProcessorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "=> !isCodeOverridable && tx.AuthorizationList is null && !ForceSimpleTransferDisabled;",
                    "=> !isCodeOverridable && tx.AuthorizationList is null && ForceSimpleTransferDisabled;"));
            case "fast-path-entry-guard":
                relativePath = ProcessorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "if (recipient is null || !IsSimpleTransferFastPathCandidate(tx, _isCodeOverridable)) return null;",
                    "if (recipient is null && !IsSimpleTransferFastPathCandidate(tx, _isCodeOverridable)) return null;"));
            case "fast-path-delegation":
                relativePath = ProcessorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "=> delegationAddress is null && codeInfo.IsEmpty;",
                    "=> delegationAddress is not null && codeInfo.IsEmpty;"));
            case "fast-path-no-code-call":
                relativePath = ProcessorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "return HasNoExecutableCode(preloadedCodeInfo, preloadedDelegationAddress) ? recipient : null;",
                    "return HasNoExecutableCode(preloadedCodeInfo, null) ? recipient : null;"));
            case "simple-recipient-dead-check":
                relativePath = ProcessorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "&& WorldState.IsDeadAccount(recipient))",
                    "&& WorldState.AccountExists(recipient))"));
            case "exact-commit-adapter":
                relativePath = ExecuteAdapterPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "transactionProcessor.Execute(transaction, txTracer);",
                    "transactionProcessor.CallAndRestore(transaction, txTracer);"));
            case "exact-commit-option":
                relativePath = ProcessorInterfacePath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "transactionProcessor.Process(transaction, txTracer, ExecutionOptions.Commit);",
                    "transactionProcessor.Process(transaction, txTracer, ExecutionOptions.CommitAndRestore);"));
            case "adapter-execute-tracer":
                relativePath = AdapterPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "transactionProcessor.Execute(currentTx, receiptsTracer);",
                    "transactionProcessor.Execute(currentTx, tracer);"));
            case "executor-adapter-tracer":
                relativePath = ExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "transactionProcessor.ProcessTransaction(currentTx, receiptsTracer, processingOptions, _stateProvider);",
                    "transactionProcessor.ProcessTransaction(currentTx, new BlockReceiptsTracer(false), processingOptions, _stateProvider);"));
            case "di-adapter-factory":
                relativePath = DiPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    ".AddScoped<TransactionProcessorAdapterFactory>(CreateExecuteAdapter)",
                    ".AddSingleton<TransactionProcessorAdapterFactory>(CreateExecuteAdapter)"));
            case "di-adapter-route":
                relativePath = DiPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "static (transactionProcessor, adapterFactory) => adapterFactory(transactionProcessor)",
                    "static (transactionProcessor, adapterFactory) => new ExecuteTransactionProcessorAdapter(transactionProcessor)"));
            case "di-adapter-construction":
                relativePath = DiPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "new ExecuteTransactionProcessorAdapter(transactionProcessor)",
                    "new BuildUpTransactionProcessorAdapter(transactionProcessor)"));
            case "block-executor-capture":
                relativePath = BlockProcessorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "_blockTransactionsExecutor = blockTransactionsExecutor;",
                    "_blockTransactionsExecutor = blockTransactionsExecutor ?? throw new ArgumentNullException(nameof(blockTransactionsExecutor));"));
            case "decorator-inner-parameter":
                relativePath = ParallelExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "IBlockProcessor.IBlockTransactionsExecutor inner,",
                    "BlockValidationTransactionsExecutor inner,"));
            case "decorator-bal-parameter":
                relativePath = ParallelExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "IBlockAccessListManager balManager,",
                    "BlockAccessListManager balManager,"));
            case "direct-adapter-parameter":
                relativePath = ExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "ITransactionProcessorAdapter transactionProcessor,",
                    "ExecuteTransactionProcessorAdapter transactionProcessor,"));
            case "execute-processor-parameter":
                relativePath = ExecuteAdapterPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "ExecuteTransactionProcessorAdapter(ITransactionProcessor transactionProcessor)",
                    "ExecuteTransactionProcessorAdapter(EthereumTransactionProcessor transactionProcessor)"));
            case "block-context-route":
                relativePath = BlockProcessorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "_blockTransactionsExecutor.SetBlockExecutionContext(CreateBlockExecutionContext(block.Header, spec));",
                    "_blockTransactionsExecutor.SetBlockExecutionContext(default);"));
            case "execute-adapter-context":
                relativePath = ExecuteAdapterPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);",
                    "transactionProcessor.SetBlockExecutionContext(blockExecutionContext.Header);"));
            case "direct-executor-context":
                relativePath = ExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "transactionProcessor.SetBlockExecutionContext(in blockExecutionContext);",
                    "transactionProcessor.SetBlockExecutionContext(blockExecutionContext.Header);"));
            case "decorator-context-bal":
                relativePath = ParallelExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "balManager.SetBlockExecutionContext(blockExecutionContext);",
                    "inner.SetBlockExecutionContext(blockExecutionContext);"));
            case "decorator-context-inner":
                relativePath = ParallelExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "inner.SetBlockExecutionContext(blockExecutionContext);",
                    "balManager.SetBlockExecutionContext(blockExecutionContext);"));
            case "decorator-context-order":
                relativePath = ParallelExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "balManager.SetBlockExecutionContext(blockExecutionContext);\n            inner.SetBlockExecutionContext(blockExecutionContext);",
                    "inner.SetBlockExecutionContext(blockExecutionContext);\n            balManager.SetBlockExecutionContext(blockExecutionContext);"));
            case "bal-context-store":
                relativePath = BalManagerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "=> _blockExecutionContext = blockExecutionContext;",
                    "=> _blockExecutionContext = default;"));
            case "nested-forward-guard":
                relativePath = TracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "if (_otherTracer is ITxTracer otherTxTracer)",
                    "if (_otherTracer is ITxTracer otherTxTracer && false)"));
            case "current-forward-guard":
                relativePath = TracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "if (_currentTxTracer.IsTracingReceipt)",
                    "if (_currentTxTracer.IsTracingReceipt && false)"));
            case "exact-base-virtual":
                relativePath = ExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "public TxReceipt[] ProcessTransactions(",
                    "public virtual TxReceipt[] ProcessTransactions("));
            case "conditional-start":
                relativePath = BlockProcessorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "        ReceiptsTracer.StartNewBlockTrace(block);",
                    "        if (block.Transactions.Length > 0)\n        {\n            ReceiptsTracer.StartNewBlockTrace(block);\n        }"));
            case "conditional-precommit":
                relativePath = BlockProcessorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "        CommitState(spec);\n\n        TxReceipt[] receipts = _blockTransactionsExecutor.ProcessTransactions(block, options, ReceiptsTracer, token);",
                    "        if (block.Transactions.Length > 0)\n        {\n            CommitState(spec);\n        }\n\n        TxReceipt[] receipts = _blockTransactionsExecutor.ProcessTransactions(block, options, ReceiptsTracer, token);"));
            case "early-return-after-fold-before-callback":
                relativePath = BlockProcessorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "        TxReceipt[] receipts = _blockTransactionsExecutor.ProcessTransactions(block, options, ReceiptsTracer, token);\n\n        // Signal that transactions are done — subscribers can cancel background work (e.g. prewarmer)",
                    "        TxReceipt[] receipts = _blockTransactionsExecutor.ProcessTransactions(block, options, ReceiptsTracer, token);\n\n        if (block.Transactions.Length > 0)\n        {\n            return receipts;\n        }\n\n        // Signal that transactions are done — subscribers can cancel background work (e.g. prewarmer)"));
            case "early-return-after-callback-before-postcommit":
                relativePath = BlockProcessorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "        TransactionsExecuted?.Invoke();\n\n        CommitState(spec);",
                    "        TransactionsExecuted?.Invoke();\n\n        if (block.Transactions.Length > 0)\n        {\n            return receipts;\n        }\n\n        CommitState(spec);"));
            case "simple-value-order":
                relativePath = ProcessorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "                if (hasValueTransfer) PayValue(tx, spec, opts);\n                WorldState.AddToBalanceAndCreateIfNotExists(recipient, in hasValueTransfer ? ref value : ref UInt256.Zero, spec);",
                    "                WorldState.AddToBalanceAndCreateIfNotExists(recipient, in hasValueTransfer ? ref value : ref UInt256.Zero, spec);\n                if (hasValueTransfer) PayValue(tx, spec, opts);"));
            case "simple-access-order":
                relativePath = ProcessorPath;
                source = ReadSource(root, relativePath);
                const string accessAndHeader = "            if (tracer.IsTracingAccess)\n            {\n                ReportSimpleTransferAccess(tx, spec, tracer, recipient);\n            }\n\n            UpdateHeaderGasUsedAndPayFees(tx, header, spec, tracer, opts, in substate, in spentGas, premiumPerGas, in opcodeGasPrice, blobBaseFee, statusCode);";
                const string headerAndAccess = "            UpdateHeaderGasUsedAndPayFees(tx, header, spec, tracer, opts, in substate, in spentGas, premiumPerGas, in opcodeGasPrice, blobBaseFee, statusCode);\n\n            if (tracer.IsTracingAccess)\n            {\n                ReportSimpleTransferAccess(tx, spec, tracer, recipient);\n            }";
                return (relativePath, ReplaceOnce(source, accessAndHeader, headerAndAccess));
            case "gas-throw-conditional":
                relativePath = ExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "if (shouldValidate && block.Header.GasUsed > block.Header.GasLimit)\n                {\n                    ThrowInvalidBlockForGasLimit(block);\n                }",
                    "if (shouldValidate && block.Header.GasUsed > block.Header.GasLimit)\n                {\n                    if (true) ThrowInvalidBlockForGasLimit(block);\n                }"));
            case "gas-throw-noop":
                relativePath = ExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source, "ThrowInvalidBlockForGasLimit(block);", "return [.. receiptsTracer.TxReceipts];"));
            case "gas-throw-bypass-in-true-arm":
                relativePath = ExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "if (shouldValidate && block.Header.GasUsed > block.Header.GasLimit)\n                {\n                    ThrowInvalidBlockForGasLimit(block);\n                }",
                    "if (shouldValidate && block.Header.GasUsed > block.Header.GasLimit)\n                {\n                    if (block.Transactions.Length > 0)\n                    {\n                        return [.. receiptsTracer.TxReceipts];\n                    }\n\n                    ThrowInvalidBlockForGasLimit(block);\n                }"));
            case "invalid-result-conditional":
                relativePath = ExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "if (!result) ThrowInvalidTransactionException(result, block.Header, currentTx, index);",
                    "if (!result)\n            {\n                if (true) ThrowInvalidTransactionException(result, block.Header, currentTx, index);\n            }"));
            case "invalid-result-noop":
                relativePath = ExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "if (!result) ThrowInvalidTransactionException(result, block.Header, currentTx, index);",
                    "if (!result) return;"));
            case "invalid-result-throw-bypass-in-true-arm":
                relativePath = ExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "if (!result) ThrowInvalidTransactionException(result, block.Header, currentTx, index);",
                    "if (!result)\n            {\n                if (index > 0) return;\n                ThrowInvalidTransactionException(result, block.Header, currentTx, index);\n            }"));
            case "bal-inner-return-bypass-in-true-arm":
                relativePath = ParallelExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "if (!balManager.Enabled)\n            {\n                return inner.ProcessTransactions(block, processingOptions, receiptsTracer, token);\n            }",
                    "if (!balManager.Enabled)\n            {\n                if (block.Transactions.Length > 0) return [];\n                return inner.ProcessTransactions(block, processingOptions, receiptsTracer, token);\n            }"));
            case "identity-method-moved":
                relativePath = RoutingPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "namespace Nethermind.Evm.TransactionProcessing;",
                    "using Nethermind.Evm.TransactionProcessing;\n\nnamespace IdentityMoved;"));
            case "identity-method-competing":
                relativePath = RoutingPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "internal static class SystemTransactionRoutingKernel\n{",
                    "internal static class SystemTransactionRoutingKernel\n{\n    internal static bool UseSystemProcessor(bool isSystemTransaction, int options) => isSystemTransaction || options != 0;"));
            case "identity-method-simple-shadow":
                relativePath = RoutingPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "    internal static bool ParticipatesInNormalBlockCounters(ExecutionOptions options, bool parallel) =>\n        (options & ExecutionOptions.SkipValidation) != ExecutionOptions.SkipValidation && !parallel;\n}",
                    "    internal static bool ParticipatesInNormalBlockCounters(ExecutionOptions options, bool parallel) =>\n        (options & ExecutionOptions.SkipValidation) != ExecutionOptions.SkipValidation && !parallel;\n\n    private static class IdentityShadow\n    {\n        internal static class SystemTransactionRoutingKernel\n        {\n            internal static bool UseSystemProcessor(bool isSystemTransaction, ExecutionOptions options) => false;\n        }\n    }\n}"));
            case "identity-constructor-alias-shadow":
                relativePath = DiPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "using Nethermind.Evm.TransactionProcessing;",
                    "using Nethermind.Evm.TransactionProcessing;\nusing ExecuteTransactionProcessorAdapter = Nethermind.Evm.TransactionProcessing.BuildUpTransactionProcessorAdapter;"));
            case "identity-field-local-shadow":
                relativePath = ProcessorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)\n        {\n            _blockCumulativeExecutionGas = 0;",
                    "public void SetBlockExecutionContext(in BlockExecutionContext blockExecutionContext)\n        {\n            ulong _blockCumulativeExecutionGas = 1;\n            _blockCumulativeExecutionGas = 0;"));
            case "identity-enum-simple-shadow":
                relativePath = OptionsPath;
                source = ReadSource(root, relativePath);
                return (relativePath, source +
                    "\ninternal static class IdentityEnumShadow\n{\n    internal enum ExecutionOptions\n    {\n        Commit = 1,\n    }\n}\n");
            case "identity-base-type-argument":
                relativePath = ProcessorPath;
                source = ReadSource(root, relativePath);
                source = ReplaceOnce(source,
                    "using Nethermind.Evm.GasPolicy;",
                    "using Nethermind.Evm.GasPolicy;\nusing IdentityEthereumGasPolicy = Nethermind.Evm.GasPolicy.EthereumGasPolicy;");
                return (relativePath, ReplaceOnce(source,
                    "TransactionProcessorBase<EthereumGasPolicy>(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager, parallel);",
                    "TransactionProcessorBase<IdentityEthereumGasPolicy>(blobBaseFeeCalculator, specProvider, worldState, virtualMachine, codeInfoRepository, logManager, parallel);"));
            case "identity-fluent-target-shadow":
                relativePath = DiPath;
                source = ReadSource(root, relativePath);
                source = ReplaceOnce(source,
                    "namespace Nethermind.Init.Modules;",
                    "namespace Nethermind.Init.Modules;\n\ninternal static class IdentityContainerBuilderShadow\n{\n    internal static ContainerBuilder AddScoped<T, TImpl>(ContainerBuilder builder) => builder;\n}");
                return (relativePath, ReplaceOnce(source,
                    "protected override void Load(ContainerBuilder builder) => builder\n            .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockValidationTransactionsExecutor>()",
                    "protected override void Load(ContainerBuilder builder) => IdentityContainerBuilderShadow.AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockValidationTransactionsExecutor>(builder)"));
            case "identity-fluent-type-argument-alias":
                relativePath = DiPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "using Nethermind.State;",
                    "using Nethermind.State;\nusing WorldState = Nethermind.State.WorldStateDecorator;"));
            case "identity-invocation-target-shadow":
                relativePath = ProcessorPath;
                source = ReadSource(root, relativePath);
                source = ReplaceOnce(source,
                    "        private TransactionResult ExecuteCore(Transaction tx, ITxTracer tracer, ExecutionOptions opts)",
                    "        private static bool UseSystemProcessor(bool isSystemTransaction, ExecutionOptions options) => isSystemTransaction || options == ExecutionOptions.SkipValidation;\n\n        private TransactionResult ExecuteCore(Transaction tx, ITxTracer tracer, ExecutionOptions opts)");
                return (relativePath, ReplaceOnce(source,
                    "SystemTransactionRoutingKernel.UseSystemProcessor(tx.IsSystem(), opts)",
                    "UseSystemProcessor(tx.IsSystem(), opts)"));
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
    }

    private static string ReadSource(string root, string relativePath) =>
        Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, relativePath)));

    private static string ReplaceOnce(string source, string expected, string replacement)
    {
        int position = source.IndexOf(expected, StringComparison.Ordinal);
        Assert.That(position, Is.GreaterThanOrEqualTo(0), $"Mutation target was not found: {expected}");
        return source[..position] + replacement + source[(position + expected.Length)..];
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory(string parent)
        {
            Path = System.IO.Path.Combine(parent, ".ordinary-transaction-machine-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private static string Read(params string[] parts) => File.ReadAllText(PathFrom(parts));

    private static string PathFrom(params string[] parts)
    {
        string root = FindRepoRoot();
        return Path.Combine([root, "tools", "Evm", "Lean", "OrdinaryTransactionMachineExtractor", .. parts]);
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not find repository root.");
    }
}
