// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.SequentialBlockTransactionFoldExtractor.Test;

[TestFixture]
[NonParallelizable]
public sealed class SequentialBlockTransactionFoldExtractorTests
{
    [OneTimeSetUp]
    public void Admit_unmutated_source_and_dependency_baseline() => Extractor.ValidateCheckedIn(FindRepoRoot(), null);

    [TestCase(false)]
    [TestCase(true)]
    public void Unmutated_and_no_subscriber_source_reach_every_later_admission_gate(bool clearSubscribers)
    {
        string root = FindRepoRoot();
        Dictionary<string, byte[]> overrides = new(StringComparer.Ordinal);
        if (clearSubscribers)
        {
            string source = File.ReadAllText(Path.Combine(root, Extractor.BlockProcessorPath));
            overrides.Add(Extractor.BlockProcessorPath, Encoding.UTF8.GetBytes(ReplaceOnce(source,
                "TransactionsExecuted?.Invoke();", "TransactionsExecuted = null;\n        TransactionsExecuted?.Invoke();")));
        }

        Assert.DoesNotThrow(() => Extractor.ValidateCompilationForTest(root, overrides));
        using TemporaryDirectory output = new();
        ExtractionResult extracted = Extractor.ExtractForTest(root, output.Path, overrides);
        IrDocument document = Extractor.LoadIrForTest(extracted.IrPath);
        ConditionalSignalIdentity signal = document.TransactionsExecuted;
        AnchorIdentity invocation = document.Anchors.Single(static anchor => anchor.Id == "block.transactions-executed-signal");
        ControlFlowIdentity flow = document.ControlFlows.Single(static flow => flow.Id == "block.processBlock");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(signal.Evaluation.ControlFlowBlock, Is.Not.EqualTo(invocation.Binding.ControlFlowBlock));
            Assert.That(signal.WhenNotNullBlock, Is.EqualTo(invocation.Binding.ControlFlowBlock));
            Assert.That(signal.WhenNullBlock, Is.Not.EqualTo(signal.WhenNotNullBlock));
            Assert.That(flow.Edges, Does.Contain($"{signal.Evaluation.ControlFlowBlock}->{signal.WhenNullBlock}"));
            Assert.That(flow.Edges, Does.Contain($"{signal.Evaluation.ControlFlowBlock}->{signal.WhenNotNullBlock}"));
            Assert.That(flow.Edges, Does.Contain($"{signal.WhenNotNullBlock}->{signal.WhenNullBlock}"));
            Assert.That(invocation.Binding.SymbolId, Is.EqualTo("M:System.Action.Invoke"));
            Assert.That(invocation.Binding.ReceiverSymbol,
                Is.EqualTo("E:Nethermind.Consensus.Processing.BlockProcessor.TransactionsExecuted"));
            Assert.That(document.AuxiliaryAnchors, Has.Length.EqualTo(23));
            Assert.That(document.AuxiliaryAnchors[^1].Id, Is.EqualTo("di.bal-manager"));
            Assert.That(document.SourceRoute.ExecutorIsNonVirtual, Is.True);
        }
    }

    [TestCase("invocation-as-evaluation")]
    [TestCase("event-symbol")]
    [TestCase("reversed-branches")]
    [TestCase("missing-null-edge")]
    [TestCase("missing-call-edge")]
    [TestCase("missing-join-edge")]
    [TestCase("null-loop")]
    public void Conditional_signal_topology_ir_mutations_fail_at_the_named_gate(string mutation)
    {
        IrDocument document = ReadCheckedIr();
        ConditionalSignalIdentity signal = document.TransactionsExecuted;
        ConditionalSignalIdentity alteredSignal = mutation switch
        {
            "invocation-as-evaluation" => signal with
            {
                Evaluation = signal.Evaluation with { ControlFlowBlock = signal.WhenNotNullBlock },
            },
            "event-symbol" => signal with { Evaluation = signal.Evaluation with { SymbolId = "E:Shadow.TransactionsExecuted" } },
            "reversed-branches" => signal with { WhenNullBlock = signal.WhenNotNullBlock, WhenNotNullBlock = signal.WhenNullBlock },
            "null-loop" => signal with { WhenNullBlock = signal.Evaluation.ControlFlowBlock },
            _ => signal,
        };
        string removedEdge = mutation switch
        {
            "missing-null-edge" => $"{signal.Evaluation.ControlFlowBlock}->{signal.WhenNullBlock}",
            "missing-call-edge" => $"{signal.Evaluation.ControlFlowBlock}->{signal.WhenNotNullBlock}",
            "missing-join-edge" => $"{signal.WhenNotNullBlock}->{signal.WhenNullBlock}",
            _ => string.Empty,
        };
        IrDocument altered = document with
        {
            TransactionsExecuted = alteredSignal,
            ControlFlows = document.ControlFlows.Select(flow => flow.Id == "block.processBlock"
                ? flow with { Edges = flow.Edges.Where(edge => edge != removedEdge).ToArray() } : flow).ToArray(),
        };

        ExtractionException? exception = Assert.Throws<ExtractionException>(() => Extractor.ValidateIrForTest(altered));
        Assert.That(exception!.Message, Does.StartWith("fold.signal.topology:"));
    }

    [Test]
    public void Checked_ir_admits_only_the_sequential_commit_boundary()
    {
        IrDocument document = ReadCheckedIr();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(document.Sources.Select(static source => source.Path), Is.EqualTo(new[]
            {
                Extractor.ExecutorPath,
                Extractor.BlockProcessorPath,
                Extractor.BlockProcessorStandardPath,
                Extractor.BlockProcessorInterfacePath,
                Extractor.ProcessingOptionsPath,
            }));
            Assert.That(document.Members.Select(static member => member.Id), Is.EqualTo(new[]
            {
                "executor.processTransactions",
                "executor.processTransaction",
                "block.processBlock",
            }));
            Assert.That(document.Anchors.Select(static anchor => anchor.Id), Is.EqualTo(new[]
            {
                "executor.metrics-setup",
                "executor.validation-mode",
                "executor.process-transaction-call",
                "executor.gas-limit-guard",
                "executor.invalid-result-throw",
                "executor.transaction-adapter-call",
                "executor.processed-event",
                "block.transaction-fold-call",
                "block.pre-transaction-commit",
                "block.post-transaction-commit",
                "block.transactions-executed-signal",
            }));
            Assert.That(document.AuxiliarySources.Select(static source => source.Path), Is.EqualTo(new[]
            {
                Extractor.TransactionProcessorAdapterPath,
                Extractor.BlockReceiptsTracerPath,
                Extractor.BlockAccessListManagerPath,
                Extractor.BlockAccessListInterfacePath,
                Extractor.ParallelExecutorPath,
                Extractor.BlockProcessingModulePath,
                Extractor.TransactionProcessorAdapterInterfacePath,
            }));
            Assert.That(document.AuxiliaryAnchors.Select(static anchor => anchor.Id), Is.EqualTo(new[]
            {
                "block.receipts-tracer-start",
                "adapter.tx-trace-start",
                "adapter.execute",
                "adapter.tx-trace-end",
                "tracer.reset-index",
                "tracer.reset-receipts",
                "tracer.reset-block-gas",
                "tracer.reset-receipt-gas",
                "tracer.success-append",
                "tracer.failure-append",
                "tracer.receipt-index",
                "tracer.tx-start-current",
                "tracer.tx-start-delegate",
                "tracer.tx-end-delegate",
                "tracer.tx-end-index",
                "bal.enabled-spec",
                "bal.enabled-derived",
                "parallel.bal-disabled-gate",
                "parallel.inner-sequential",
                "parallel.selection",
                "di.base-executor",
                "di.parallel-decorator",
                "di.bal-manager",
            }));
            Assert.That(document.CompilerReferences.InventoryPath,
                Is.EqualTo(Extractor.CompilerReferenceInventoryPath));
            Assert.That(document.CompilerReferences.Count, Is.EqualTo(434));
            Assert.That(document.ControlFlows, Has.Length.EqualTo(3));
            Assert.That(document.Excluded, Does.Contain(
                "parallel worker execution and parallel BlockReceiptsTracer semantics"));
            Assert.That(document.Excluded, Does.Contain(
                "EVM execution, gas production, world-state transitions, roots, trie, RLP, hashes, rewards, withdrawals, requests"));
            Assert.That(document.OpenObligations, Has.Length.EqualTo(13));
            Assert.That(document.SourceRoute.ExecutorIsNonVirtual, Is.True);
            Assert.That(document.SourceRoute.BaseRegistration,
                Is.EqualTo("IBlockProcessor.IBlockTransactionsExecutor -> BlockProcessor.BlockValidationTransactionsExecutor"));
            Assert.That(document.AuxiliaryAnchors.Single(static anchor => anchor.Id == "di.base-executor").CanonicalSyntax,
                Is.EqualTo("builder.AddScoped<IBlockProcessor.IBlockTransactionsExecutor,BlockProcessor.BlockValidationTransactionsExecutor>()"));
            Assert.That(document.AuxiliaryAnchors.Single(static anchor => anchor.Id == "di.parallel-decorator").CanonicalSyntax,
                Is.EqualTo("builder.AddScoped<IBlockProcessor.IBlockTransactionsExecutor,BlockProcessor.BlockValidationTransactionsExecutor>()" +
                    ".AddDecorator<IBlockProcessor.IBlockTransactionsExecutor,BlockProcessor.ParallelBlockValidationTransactionsExecutor>()"));
            Assert.That(document.Composition.ReceiptTerminalSourceClosure.SourcePinsPath,
                Is.EqualTo("tools/Evm/Lean/ReceiptTerminalFoldExtractor/SOURCE_PINS.json"));
            Assert.That(document.Composition.ReceiptTerminalSourceClosure.Sources, Has.Length.EqualTo(12));
            Assert.That(document.Composition.ReceiptTerminalSourceClosure.BindingSources, Has.Length.EqualTo(38));
        }
    }

    [TestCase("changed")]
    [TestCase("missing")]
    [TestCase("ambiguous")]
    [TestCase("null")]
    public void Compiler_reference_mutations_fail_closed(string mutation)
    {
        CompilerReferenceIdentity[] references = Extractor.LoadCompilerReferencesForTest(FindRepoRoot());
        CompilerReferenceIdentity target = references[0];
        CompilerReferenceIdentity[] altered = mutation switch
        {
            "changed" => references.Select(reference => reference.Path == target.Path
                ? reference with { Sha256 = new string('0', 64) }
                : reference).ToArray(),
            "missing" => references[1..],
            "ambiguous" => references.Append(target with
            {
                Path = "tools/artifacts/bin/Evm/release/" + target.AssemblyName + ".dll",
            }).OrderBy(reference => reference.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            "null" => references.Select((reference, index) => index == 0 ? null! : reference).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        Assert.That(() => Extractor.ValidateCompilerReferencesForTest(altered),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Missing_compiler_reference_inventory_fails_closed()
    {
        using TemporaryDirectory root = new();

        Assert.That(() => Extractor.ValidateCompilerInventoryForTest(root.Path),
            Throws.TypeOf<ExtractionException>());
    }

    [TestCase("candidate")]
    [TestCase("operation")]
    [TestCase("symbol")]
    [TestCase("position")]
    [TestCase("source")]
    [TestCase("wrong-target")]
    [TestCase("wrong-receiver-type")]
    [TestCase("wrong-receiver-symbol")]
    [TestCase("wrong-argument")]
    public void Typed_ir_mutations_fail_closed(string mutation)
    {
        IrDocument document = ReadCheckedIr();
        AnchorIdentity target = document.Anchors.Single(static anchor =>
            anchor.Id == "block.transaction-fold-call");

        TypedBinding binding = mutation switch
        {
            "candidate" => target.Binding with
            {
                HasCandidateSymbols = true,
                CandidateReason = "OverloadResolutionFailure",
            },
            "operation" => target.Binding with { OperationKind = "None" },
            "symbol" => target.Binding with { SymbolId = string.Empty },
            "position" => target.Binding with { Position = 0 },
            "source" => target.Binding with { Path = string.Empty },
            "wrong-target" => target.Binding with { SymbolId = "M:Shadow.ProcessTransactions" },
            "wrong-receiver-type" => target.Binding with { ReceiverType = "Shadow.IBlockTransactionsExecutor" },
            "wrong-receiver-symbol" => target.Binding with { ReceiverSymbol = "F:Shadow.executor" },
            "wrong-argument" => target.Binding with { ArgumentBindings = ["otherBlock", "options", "ReceiptsTracer", "token"] },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        AnchorIdentity changed = target with { Binding = binding };
        IrDocument altered = document with
        {
            Anchors = document.Anchors.Select(anchor => anchor.Id == target.Id ? changed : anchor).ToArray(),
        };

        Assert.That(() => Extractor.ValidateIrForTest(altered),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Canonical_anchor_mutation_fails_closed()
    {
        IrDocument document = ReadCheckedIr();
        AnchorIdentity target = document.Anchors.Single(static anchor =>
            anchor.Id == "block.post-transaction-commit");
        IrDocument altered = document with
        {
            Anchors = document.Anchors.Select(anchor => anchor.Id == target.Id
                ? target with { CanonicalSyntax = "CommitState(_specProvider.GetSpec(block.Header))" }
                : anchor).ToArray(),
        };

        Assert.That(() => Extractor.ValidateIrForTest(altered),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Direct_inner_BAL_boundary_mutation_fails_closed()
    {
        IrDocument document = ReadCheckedIr();
        IrDocument altered = document with
        {
            Kernel = document.Kernel.Replace("BAL disabled premise", "BAL decorator enabled", StringComparison.Ordinal),
        };

        Assert.That(() => Extractor.ValidateIrForTest(altered),
            Throws.TypeOf<ExtractionException>());
    }

    [TestCase("signature")]
    [TestCase("base-registration")]
    [TestCase("decorator-registration")]
    public void Exact_base_route_identity_mutations_fail_closed(string mutation)
    {
        IrDocument document = ReadCheckedIr();
        SourceRouteIdentity route = document.SourceRoute;
        SourceRouteIdentity altered = mutation switch
        {
            "signature" => route with { ExecutorSignature = route.ExecutorSignature + ".Changed" },
            "base-registration" => route with { BaseRegistration = "IBlockProcessor.IBlockTransactionsExecutor -> changed" },
            "decorator-registration" => route with { DecoratorRegistration = "IBlockProcessor.IBlockTransactionsExecutor -> changed" },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        Assert.That(() => Extractor.ValidateIrForTest(document with { SourceRoute = altered }),
            Throws.TypeOf<ExtractionException>());
    }

    [TestCase("operation")]
    [TestCase("cfg-block")]
    [TestCase("unreachable")]
    [TestCase("owner")]
    public void Auxiliary_typed_operation_and_cfg_mutations_fail_closed(string mutation)
    {
        IrDocument document = ReadCheckedIr();
        AuxiliaryAnchorIdentity target = document.AuxiliaryAnchors.Single(static anchor =>
            anchor.Id == "adapter.execute");
        AuxiliaryAnchorIdentity changed = mutation switch
        {
            "operation" => target with { OperationKind = "None" },
            "cfg-block" => target with { ControlFlowBlock = -1 },
            "unreachable" => target with { IsReachable = false },
            "owner" => target with { ContainingMember = "Other" },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        IrDocument altered = document with
        {
            AuxiliaryAnchors = document.AuxiliaryAnchors.Select(anchor => anchor.Id == target.Id
                ? changed
                : anchor).ToArray(),
        };

        Assert.That(() => Extractor.ValidateIrForTest(altered),
            Throws.TypeOf<ExtractionException>());
    }


    [TestCase("tracer.reset-index", "SimpleAssignment")]
    [TestCase("tracer.tx-end-index", "Increment")]
    public void Current_index_uses_the_live_property_and_Roslyn_operation(string id, string operationKind)
    {
        AuxiliaryAnchorIdentity anchor = ReadCheckedIr().AuxiliaryAnchors.Single(anchor => anchor.Id == id);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(anchor.TargetSymbol, Is.EqualTo("P:Nethermind.Blockchain.Tracing.BlockReceiptsTracer._currentIndex"));
            Assert.That(anchor.OperationKind, Is.EqualTo(operationKind));
            Assert.That(anchor.RightHandSideBindings, Is.Empty);
        }
    }

    [TestCase("tracer.reset-index", "field")]
    [TestCase("tracer.tx-end-index", "field")]
    [TestCase("tracer.tx-end-index", "old-operation")]
    public void Current_index_identity_drift_is_rejected(string id, string mutation)
    {
        IrDocument document = ReadCheckedIr();
        AuxiliaryAnchorIdentity target = document.AuxiliaryAnchors.Single(anchor => anchor.Id == id);
        AuxiliaryAnchorIdentity altered = mutation == "field"
            ? target with { TargetSymbol = "F:Nethermind.Blockchain.Tracing.BlockReceiptsTracer._currentIndex" }
            : target with { OperationKind = "IncrementOrDecrement" };
        IrDocument changed = document with
        {
            AuxiliaryAnchors = document.AuxiliaryAnchors.Select(anchor => anchor.Id == id ? altered : anchor).ToArray(),
        };
        ExtractionException? exception = Assert.Throws<ExtractionException>(() => Extractor.ValidateIrForTest(changed));
        Assert.That(exception!.Message, Is.EqualTo($"The typed auxiliary anchor '{id}' is incomplete or changed."));
    }

    [TestCase("target")]
    [TestCase("receiver-symbol")]
    [TestCase("receiver-type")]
    [TestCase("field-symbol")]
    [TestCase("field-receiver")]
    [TestCase("parameter-symbol")]
    [TestCase("parameter-ordinal")]
    [TestCase("missing")]
    [TestCase("extra")]
    [TestCase("null-array")]
    [TestCase("null-entry")]
    public void Assignment_rhs_binding_drift_reaches_the_exact_gate(string mutation)
    {
        IrDocument document = ReadCheckedIr();
        AuxiliaryAnchorIdentity target = document.AuxiliaryAnchors.Single(static anchor => anchor.Id == "tracer.tx-start-delegate");
        AssignmentValueBinding[] values = [.. target.RightHandSideBindings];
        Assert.That(values, Has.Length.EqualTo(3));
        switch (mutation)
        {
            case "target": values[0] = values[0] with { Symbol = "M:Shadow.StartNewTxTrace(Nethermind.Core.Transaction)" }; break;
            case "receiver-symbol": values[0] = values[0] with { ReceiverSymbol = "F:Shadow._otherTracer" }; break;
            case "receiver-type": values[0] = values[0] with { ReceiverType = "Shadow.IBlockTracer" }; break;
            case "field-symbol": values[1] = values[1] with { Symbol = "F:Shadow._otherTracer" }; break;
            case "field-receiver": values[1] = values[1] with { ReceiverSymbol = "this:Shadow.Tracer" }; break;
            case "parameter-symbol": values[2] = values[2] with { Symbol = "M:Shadow.StartNewTxTrace/parameter:0:tx" }; break;
            case "parameter-ordinal": values[2] = values[2] with { ParameterOrdinal = 1 }; break;
            case "missing": values = values[1..]; break;
            case "extra": values = [.. values, values[0]]; break;
            case "null-array": values = null!; break;
            case "null-entry": values[1] = null!; break;
        }
        IrDocument changed = document with
        {
            AuxiliaryAnchors = document.AuxiliaryAnchors.Select(anchor => anchor.Id == target.Id
                ? anchor with { RightHandSideBindings = values } : anchor).ToArray(),
        };
        ExtractionException? exception = Assert.Throws<ExtractionException>(() => Extractor.ValidateIrForTest(changed));
        Assert.That(exception!.Message, Is.EqualTo("fold.assignment.tracer.tx-start-delegate: exact right-hand-side bindings changed."));
    }

    [Test]
    public void Commit_order_mutation_fails_closed()
    {
        IrDocument document = ReadCheckedIr();
        AnchorIdentity pre = document.Anchors.Single(static anchor =>
            anchor.Id == "block.pre-transaction-commit");
        AnchorIdentity post = document.Anchors.Single(static anchor =>
            anchor.Id == "block.post-transaction-commit");
        int postPosition = post.Binding.Position;
        AnchorIdentity changed = pre with { Binding = pre.Binding with
        {
            Position = postPosition + 1,
        } };
        IrDocument altered = document with
        {
            Anchors = document.Anchors.Select(anchor => anchor.Id == pre.Id ? changed : anchor).ToArray(),
        };

        Assert.That(() => Extractor.ValidateIrForTest(altered),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Source_mutation_rejects_missing_post_commit_anchor_without_writing_partial_artifacts()
    {
        string root = FindRepoRoot();
        string path = Path.Combine(root, Extractor.BlockProcessorPath);
        byte[] original = File.ReadAllBytes(path);
        string source = Encoding.UTF8.GetString(original).Replace("\r\n", "\n", StringComparison.Ordinal);
        string mutated = source.Replace("CommitState(spec);", "CommitState(_specProvider.GetSpec(block.Header));", StringComparison.Ordinal);
        Assert.That(mutated, Is.Not.EqualTo(source));

        AssertSourceRejected(root, Extractor.BlockProcessorPath, mutated,
            "ProcessBlock must retain exactly one pre-fold and two post-fold CommitState(spec) calls.");
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
    }

    [Test]
    public void Source_mutation_rejects_rebinding_to_the_later_excluded_commit()
    {
        string root = FindRepoRoot();
        string path = Path.Combine(root, Extractor.BlockProcessorPath);
        byte[] original = File.ReadAllBytes(path);
        string source = Encoding.UTF8.GetString(original).Replace("\r\n", "\n", StringComparison.Ordinal);
        const string expected = "CommitState(spec);";
        int first = source.IndexOf(expected, StringComparison.Ordinal);
        int second = first < 0 ? -1 : source.IndexOf(expected, first + expected.Length, StringComparison.Ordinal);
        Assert.That(second, Is.GreaterThanOrEqualTo(0));

        string mutated = source[..second] + "CommitState(_specProvider.GetSpec(block.Header));" + source[(second + expected.Length)..];
        AssertSourceRejected(root, Extractor.BlockProcessorPath, mutated,
            "ProcessBlock must retain exactly one pre-fold and two post-fold CommitState(spec) calls.");
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
    }

    [Test]
    public void Source_mutation_rejects_non_indexed_transaction_loop_without_writing_partial_artifacts()
    {
        string root = FindRepoRoot();
        string path = Path.Combine(root, Extractor.ExecutorPath);
        byte[] original = File.ReadAllBytes(path);
        string source = Encoding.UTF8.GetString(original).Replace("\r\n", "\n", StringComparison.Ordinal);
        const string expected = "i < block.Transactions.Length";
        string mutated = source.Replace(expected, "i <= block.Transactions.Length", StringComparison.Ordinal);
        Assert.That(mutated, Is.Not.EqualTo(source));

        AssertSourceRejected(root, Extractor.ExecutorPath, mutated,
            "ProcessTransactions must retain the indexed block transaction loop shape.");
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
    }

    [Test]
    public void Source_mutation_rejects_callback_after_post_commit_without_writing_partial_artifacts()
    {
        string root = FindRepoRoot();
        string path = Path.Combine(root, Extractor.BlockProcessorPath);
        byte[] original = File.ReadAllBytes(path);
        string source = Encoding.UTF8.GetString(original).Replace("\r\n", "\n", StringComparison.Ordinal);
        const string expected = "TransactionsExecuted?.Invoke();\n\n        CommitState(spec);";
        string mutated = source.Replace(expected,
            "CommitState(spec);\n        TransactionsExecuted?.Invoke();", StringComparison.Ordinal);
        Assert.That(mutated, Is.Not.EqualTo(source));

        AssertSourceRejected(root, Extractor.BlockProcessorPath, mutated,
            "ProcessBlock must signal TransactionsExecuted after a normal transaction fold return and before the post-fold commit.");
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
    }

    [Test]
    public void Source_mutation_rejects_conditional_transactions_executed_callback()
    {
        string root = FindRepoRoot();
        string path = Path.Combine(root, Extractor.BlockProcessorPath);
        byte[] original = File.ReadAllBytes(path);
        string source = Encoding.UTF8.GetString(original).Replace("\r\n", "\n", StringComparison.Ordinal);
        const string expected = "TransactionsExecuted?.Invoke();";
        string mutated = source.Replace(expected,
            "if (block.Transactions.Length > 0)\n        {\n            TransactionsExecuted?.Invoke();\n        }", StringComparison.Ordinal);
        Assert.That(mutated, Is.Not.EqualTo(source));

        AssertSourceRejected(root, Extractor.BlockProcessorPath, mutated,
            "The first post-fold CommitState(spec) must remain on the normal path after TransactionsExecuted.");
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(original));
    }

    [TestCase("executor.metrics-setup")]
    [TestCase("executor.process-transaction-call")]
    [TestCase("executor.process-transaction-local")]
    [TestCase("executor.transaction-adapter-call")]
    [TestCase("executor.processed-event")]
    [TestCase("executor.gas-limit-guard")]
    [TestCase("block.transaction-fold-call")]
    [TestCase("block.pre-transaction-commit")]
    [TestCase("block.post-transaction-commit")]
    public void Main_anchors_reject_unused_lambda_or_local_function_owners(string anchor)
    {
        string root = FindRepoRoot();
        string path = anchor.StartsWith("block.", StringComparison.Ordinal)
            ? Extractor.BlockProcessorPath : Extractor.ExecutorPath;
        string source = File.ReadAllText(Path.Combine(root, path)).Replace("\r\n", "\n", StringComparison.Ordinal);
        (string expected, string replacement) = anchor switch
        {
            "executor.metrics-setup" => ("SetupTxTimingMetrics(block);", "System.Action skipped = () => SetupTxTimingMetrics(block);"),
            "executor.process-transaction-call" => ("ProcessTransaction(block, currentTx, i, receiptsTracer, processingOptions);",
                "System.Action skipped = () => ProcessTransaction(block, currentTx, i, receiptsTracer, processingOptions);"),
            "executor.process-transaction-local" => ("ProcessTransaction(block, currentTx, i, receiptsTracer, processingOptions);",
                "void Skipped() => ProcessTransaction(block, currentTx, i, receiptsTracer, processingOptions);"),
            "executor.transaction-adapter-call" => ("result = transactionProcessor.ProcessTransaction(currentTx, receiptsTracer, processingOptions, _stateProvider);",
                "result = TransactionResult.Ok; System.Action skipped = () => transactionProcessor.ProcessTransaction(currentTx, receiptsTracer, processingOptions, _stateProvider);"),
            "executor.processed-event" => ("_transactionProcessedEventHandler?.OnTransactionProcessed(new TxProcessedEventArgs(index, currentTx, block.Header, receiptsTracer.TxReceipts[index]));",
                "System.Action skipped = () => _transactionProcessedEventHandler?.OnTransactionProcessed(new TxProcessedEventArgs(index, currentTx, block.Header, receiptsTracer.TxReceipts[index]));"),
            "executor.gas-limit-guard" => ("if (shouldValidate && block.Header.GasUsed > block.Header.GasLimit)\n                {\n                    ThrowInvalidBlockForGasLimit(block);\n                }",
                "System.Action skipped = () => { if (shouldValidate && block.Header.GasUsed > block.Header.GasLimit) { ThrowInvalidBlockForGasLimit(block); } };"),
            "block.transaction-fold-call" => ("TxReceipt[] receipts = _blockTransactionsExecutor.ProcessTransactions(block, options, ReceiptsTracer, token);",
                "TxReceipt[] receipts = []; System.Action skipped = () => _blockTransactionsExecutor.ProcessTransactions(block, options, ReceiptsTracer, token);"),
            "block.pre-transaction-commit" => ("CommitState(spec);", "System.Action skipped = () => CommitState(spec);"),
            "block.post-transaction-commit" => ("TransactionsExecuted?.Invoke();\n\n        CommitState(spec);",
                "TransactionsExecuted?.Invoke();\n\n        System.Action skipped = () => CommitState(spec);"),
            _ => throw new ArgumentOutOfRangeException(nameof(anchor)),
        };
        string id = anchor == "executor.process-transaction-local" ? "executor.process-transaction-call" : anchor;
        AssertSourceRejected(root, path, ReplaceOnce(source, expected, replacement),
            $"Auxiliary anchor '{id}' is owned by a local function or lambda.");
    }

    [TestCase("conditional")]
    [TestCase("continue")]
    [TestCase("break")]
    [TestCase("return")]
    [TestCase("while")]
    [TestCase("do")]
    [TestCase("extra-statement")]
    [TestCase("outer-if")]
    [TestCase("outer-while")]
    [TestCase("else-break")]
    [TestCase("else-continue")]
    [TestCase("else-return")]
    [TestCase("else-repeat-call")]
    [TestCase("else-while")]
    [TestCase("else-do")]
    [TestCase("else-empty")]
    public void Iteration_rejects_compile_valid_skips_repetitions_and_extra_statements(string mutation)
    {
        string root = FindRepoRoot();
        string source = File.ReadAllText(Path.Combine(root, Extractor.ExecutorPath)).Replace("\r\n", "\n", StringComparison.Ordinal);
        const string action = "ProcessTransaction(block, currentTx, i, receiptsTracer, processingOptions);\n\n" +
            "                if (shouldValidate && block.Header.GasUsed > block.Header.GasLimit)\n" +
            "                {\n                    ThrowInvalidBlockForGasLimit(block);\n                }";
        const string loop = "for (int i = 0; i < block.Transactions.Length; i++)";
        (string expected, string replacement) = mutation switch
        {
            "conditional" => (action, "if (i > 0) { " + action + " }"),
            "continue" => (action, "if (i == 0) continue;\n                " + action),
            "break" => (action, "if (i == 0) break;\n                " + action),
            "return" => (action, "if (i == 0) return [];\n                " + action),
            "while" => (action, "while (i > 0) { " + action + " }"),
            "do" => (action, "do { " + action + " } while (i > 0);"),
            "extra-statement" => (action, "System.GC.KeepAlive(currentTx);\n                " + action),
            "outer-if" => (loop, "if (block.Header.Number > 0) " + loop),
            "outer-while" => (loop, "while (block.Header.Number > 0) " + loop),
            "else-break" => (action, action + " else { break; }"),
            "else-continue" => (action, action + " else { continue; }"),
            "else-return" => (action, action + " else { return []; }"),
            "else-repeat-call" => (action, action + " else { this.ProcessTransaction(block, currentTx, i, receiptsTracer, processingOptions); }"),
            "else-while" => (action, action + " else { while (i > 0) { this.ProcessTransaction(block, currentTx, i, receiptsTracer, processingOptions); } }"),
            "else-do" => (action, action + " else { do { this.ProcessTransaction(block, currentTx, i, receiptsTracer, processingOptions); } while (i > 0); }"),
            "else-empty" => (action, action + " else { }"),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        string diagnostic = mutation switch
        {
            _ when mutation.StartsWith("outer-", StringComparison.Ordinal) =>
                "fold.iteration: transaction loop must be a direct ProcessTransactions statement.",
            _ when mutation.StartsWith("else-", StringComparison.Ordinal) =>
                "fold.iteration: gas-limit guard must not have an else arm.",
            _ => "fold.iteration: body must contain only the ordered transaction projection, helper call, and gas-limit guard.",
        };
        AssertSourceRejected(root, Extractor.ExecutorPath, ReplaceOnce(source, expected, replacement), diagnostic);
    }

    [Test]
    public void Iteration_topology_ir_mutations_fail_at_the_named_gate(
        [Values("missing-false-edge", "skip-increment", "repeat-call", "changed-back-edge", "extra-exit", "extra-block")] string mutation)
    {
        IrDocument document = ReadCheckedIr();
        ControlFlowIdentity flow = document.ControlFlows.Single(static flow => flow.Id == "executor.processTransactions");
        ControlFlowIdentity changed = mutation switch
        {
            "missing-false-edge" => flow with { Edges = flow.Edges.Where(static edge => edge != "4->7").ToArray() },
            "skip-increment" => flow with { Edges = flow.Edges.Select(static edge => edge == "5->7" ? "5->8" : edge).ToArray() },
            "repeat-call" => flow with { Edges = [.. flow.Edges, "5->4"] },
            "changed-back-edge" => flow with { BackEdges = ["7->4"] },
            "extra-exit" => flow with { NormalExitBlocks = [5, .. flow.NormalExitBlocks] },
            "extra-block" => flow with { ReachableBlocks = [.. flow.ReachableBlocks, 10] },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        IrDocument altered = document with
        {
            ControlFlows = document.ControlFlows.Select(candidate => candidate.Id == flow.Id ? changed : candidate).ToArray(),
        };

        ExtractionException? exception = Assert.Throws<ExtractionException>(() => Extractor.ValidateIrForTest(altered));
        Assert.That(exception!.Message, Does.StartWith("fold.iteration.topology:"));
    }

    [Test]
    public void Projection_rejects_compile_valid_alias_conversions([Values("transaction", "result")] string mutation)
    {
        string root = FindRepoRoot();
        string source = File.ReadAllText(Path.Combine(root, Extractor.ExecutorPath));
        bool transaction = mutation == "transaction";
        string type = transaction ? "Transaction" : "TransactionResult";
        string metadataType = transaction ? "Nethermind.Core.Transaction" : "Nethermind.Evm.TransactionProcessing.TransactionResult";
        string shadow = transaction ? "ShadowTransaction" : "ShadowResult";
        source = ReplaceOnce(source, "using System.Diagnostics;",
            $"using {type} = Nethermind.Consensus.Processing.{shadow};\nusing System.Diagnostics;");
        source = source.Replace(transaction ? "Transaction currentTx," : "TransactionResult result,",
            transaction ? "Nethermind.Core.Transaction currentTx," : "Nethermind.Evm.TransactionProcessing.TransactionResult result,",
            StringComparison.Ordinal);
        source += $$"""

            internal sealed class {{shadow}}
            {
                public static implicit operator {{shadow}}({{metadataType}} value) => new();
                public static implicit operator {{metadataType}}({{shadow}} value) => {{(transaction ? "new Nethermind.Core.Transaction()" : "Nethermind.Evm.TransactionProcessing.TransactionResult.Ok")}};
                {{(transaction ? string.Empty : "public static bool operator !(ShadowResult value) => false;")}}
            }
            """;
        AssertSourceRejected(root, Extractor.ExecutorPath, source,
            transaction ? "fold.projection.binding:" : "fold.argument.conversion:");
    }

    [Test]
    public void Projection_ir_mutations_fail_at_the_named_gate(
        [Values("type", "assembly", "index", "argument", "initializer-conversion", "argument-conversion", "operator", "coordinated-shadow", "missing")] string mutation)
    {
        IrDocument document = ReadCheckedIr();
        TransactionProjectionIdentity projection = document.TransactionProjection;
        TransactionProjectionIdentity changed = mutation switch
        {
            "type" => projection with { TransactionType = "Nethermind.Consensus.Processing.ShadowTransaction" },
            "assembly" => projection with { TransactionAssembly = "Nethermind.Consensus" },
            "index" => projection with { IndexLocal = projection.IndexLocal + "Shadow" },
            "argument" => projection with { HelperArgumentLocal = projection.HelperArgumentLocal + "Shadow" },
            "initializer-conversion" => projection with { InitializerIdentityConversion = false },
            "argument-conversion" => projection with { HelperArgumentIdentityConversion = false },
            "operator" => projection with { HelperArgumentOperatorMethod = "M:ShadowTransaction.op_Implicit" },
            "coordinated-shadow" => projection with
            {
                LocalSymbol = projection.LocalSymbol + "Shadow",
                HelperArgumentLocal = projection.HelperArgumentLocal + "Shadow",
                TransactionType = "Nethermind.Consensus.Processing.ShadowTransaction",
                TransactionAssembly = "Nethermind.Consensus",
            },
            "missing" => null!,
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        ExtractionException? exception = Assert.Throws<ExtractionException>(() =>
            Extractor.ValidateIrForTest(document with { TransactionProjection = changed }));
        Assert.That(exception!.Message, Does.StartWith("fold.projection.binding:"));
    }

    [TestCase("gas-noop")]
    [TestCase("gas-exception")]
    [TestCase("gas-message")]
    [TestCase("gas-block")]
    [TestCase("invalid-noop")]
    [TestCase("invalid-exception")]
    public void Throw_helpers_reject_compile_valid_body_mutations(string mutation)
    {
        string root = FindRepoRoot();
        string source = File.ReadAllText(Path.Combine(root, Extractor.ExecutorPath));
        const string gasThrow = "=> throw new InvalidBlockException(block, Core.Messages.BlockErrorMessages.ExceededGasLimit);";
        const string invalidThrow = "=> throw new InvalidTransactionException(header, $\"Transaction {currentTx.Hash} at index {index} failed with error {result.ErrorDescription}\", result);";
        (string expected, string replacement) = mutation switch
        {
            "gas-noop" => (gasThrow, "{ }"),
            "gas-exception" => (gasThrow, "=> throw new System.InvalidOperationException();"),
            "gas-message" => (gasThrow, "=> throw new InvalidBlockException(block, \"changed\");"),
            "gas-block" => (gasThrow, "=> throw new InvalidBlockException((Block)null!, Core.Messages.BlockErrorMessages.ExceededGasLimit);"),
            "invalid-noop" => (invalidThrow, "{ }"),
            "invalid-exception" => (invalidThrow, "=> throw new System.InvalidOperationException();"),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        string helper = mutation.StartsWith("gas-", StringComparison.Ordinal) ? "gas-limit" : "invalid-result";
        AssertSourceRejected(root, Extractor.ExecutorPath, ReplaceOnce(source, expected, replacement),
            $"fold.throw.executor.{helper}-helper: helper must directly throw the exact admitted exception.");
    }

    [Test]
    public void Synchronous_callables_reject_compile_valid_async_void(
        [Values("executor.processTransaction", "executor.gas-limit-helper", "executor.invalid-result-helper")] string id)
    {
        string root = FindRepoRoot();
        string source = File.ReadAllText(Path.Combine(root, Extractor.ExecutorPath));
        string declaration = SynchronousDeclaration(id);
        string changed = ReplaceOnce(source, declaration, declaration.Replace("void", "async void", StringComparison.Ordinal));
        AssertSourceRejected(root, Extractor.ExecutorPath, changed, $"fold.synchronous.{id}:");
    }

    [Test]
    public void Synchronous_attributes_reject_compile_valid_call_omission_or_execution_changes(
        [Values("executor.processTransaction", "executor.gas-limit-helper", "executor.invalid-result-helper")] string id,
        [Values("conditional", "alias", "synchronized")] string mutation)
    {
        string root = FindRepoRoot();
        string source = File.ReadAllText(Path.Combine(root, Extractor.ExecutorPath));
        if (mutation == "alias") source = "using FoldConditional = System.Diagnostics.ConditionalAttribute;\n" + source;
        string attribute = mutation switch
        {
            "conditional" => "[System.Diagnostics.Conditional(\"FOLD_NEVER_DEFINED\")]",
            "alias" => "[FoldConditional(\"FOLD_NEVER_DEFINED\")]",
            "synchronized" => "[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        string declaration = SynchronousDeclaration(id);
        string changed = ReplaceOnce(source, declaration, attribute + "\n        " + declaration);
        string diagnostic = mutation == "synchronized" ? "exact attribute inventory changed." : "ConditionalAttribute is not admitted.";
        AssertSourceRejected(root, Extractor.ExecutorPath, changed, $"fold.synchronous.{id}.attributes: {diagnostic}");
    }

    private static string SynchronousDeclaration(string id) => id switch
    {
        "executor.processTransaction" => "protected virtual void ProcessTransaction(",
        "executor.gas-limit-helper" => "static void ThrowInvalidBlockForGasLimit(",
        "executor.invalid-result-helper" => "internal static void ThrowInvalidTransactionException(",
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    [Test]
    public void Synchronous_lineage_rejects_compile_valid_inherited_or_changed_routes(
        [Values("conditional", "alias", "indirect", "synchronized", "plain", "base-only", "interface")] string mutation)
    {
        string root = FindRepoRoot();
        string source = File.ReadAllText(Path.Combine(root, Extractor.ExecutorPath));
        if (mutation == "interface")
        {
            source = ReplaceOnce(source, ": IBlockProcessor.IBlockTransactionsExecutor",
                ": IBlockProcessor.IBlockTransactionsExecutor, IAdditionalExecutor");
            source += "\npublic interface IAdditionalExecutor { }";
        }
        else
        {
            source = ReplaceOnce(source, ": IBlockProcessor.IBlockTransactionsExecutor",
                ": ConditionalExecutorBase, IBlockProcessor.IBlockTransactionsExecutor");
            if (mutation == "base-only") source += "\npublic class ConditionalExecutorBase { }";
            else
            {
                source = ReplaceOnce(source, "protected virtual void ProcessTransaction(", "protected override void ProcessTransaction(");
                if (mutation == "alias") source = "using FoldConditional = System.Diagnostics.ConditionalAttribute;\n" + source;
                string attribute = mutation switch
                {
                    "conditional" or "indirect" => "[System.Diagnostics.Conditional(\"FOLD_NEVER_DEFINED\")]",
                    "alias" => "[FoldConditional(\"FOLD_NEVER_DEFINED\")]",
                    "synchronized" => "[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.Synchronized)]",
                    _ => "",
                };
                string baseType = mutation == "indirect" ? "ConditionalExecutorRoot" : "ConditionalExecutorBase";
                source += $$"""

                    public class {{baseType}}
                    {
                        {{attribute}}
                        protected virtual void ProcessTransaction(Block block, Transaction currentTx, int index,
                            BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions) { }
                    }
                    """;
                if (mutation == "indirect") source += """

                    public class ConditionalExecutorBase : ConditionalExecutorRoot
                    {
                        protected override void ProcessTransaction(Block block, Transaction currentTx, int index,
                            BlockReceiptsTracer receiptsTracer, ProcessingOptions processingOptions) { }
                    }
                    """;
            }
        }
        string diagnostic = mutation is "conditional" or "alias" or "indirect"
            ? "inherited ConditionalAttribute is not admitted." : "exact callable lineage changed.";
        AssertSourceRejected(root, Extractor.ExecutorPath, source,
            $"fold.synchronous.executor.processTransaction.lineage: {diagnostic}");
    }

    [Test]
    public void Synchronous_lineage_ir_mutations_fail_at_the_named_gate(
        [Values("executor.processTransaction", "executor.gas-limit-helper", "executor.invalid-result-helper")] string id,
        [Values("override", "overridden-method", "inherited-conditional", "inherited-attributes", "declaring-type",
            "base", "interface", "implemented-interface", "static", "virtual", "missing")] string mutation)
    {
        IrDocument document = ReadCheckedIr();
        SynchronousCallableIdentity callable = document.SynchronousCallables.Single(candidate => candidate.Id == id);
        CallableLineageIdentity lineage = callable.Lineage;
        CallableLineageIdentity changed = mutation switch
        {
            "override" => lineage with { IsOverride = true },
            "overridden-method" => lineage with { OverriddenMethods = ["M:ConditionalExecutorBase.ProcessTransaction"] },
            "inherited-conditional" => lineage with { HasInheritedConditionalAttribute = true },
            "inherited-attributes" => lineage with
            {
                InheritedAttributes = [new("System.Runtime.CompilerServices.MethodImplAttribute", "System.Private.CoreLib", true)],
            },
            "declaring-type" => lineage with { DeclaringType = lineage.DeclaringType with { Assembly = "ShadowAssembly" } },
            "base" => lineage with { BaseTypes = [new("T:ConditionalExecutorBase", "Nethermind.Consensus")] },
            "interface" => lineage with { Interfaces = [] },
            "implemented-interface" => lineage with { ImplementedInterfaceMethods = ["M:IAdditionalExecutor.ProcessTransaction"] },
            "static" => lineage with { IsStatic = !lineage.IsStatic },
            "virtual" => lineage with { IsVirtual = !lineage.IsVirtual },
            "missing" => null!,
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        IrDocument altered = document with
        {
            SynchronousCallables = document.SynchronousCallables.Select(candidate =>
                candidate.Id == id ? candidate with { Lineage = changed } : candidate).ToArray(),
        };
        ExtractionException? exception = Assert.Throws<ExtractionException>(() => Extractor.ValidateIrForTest(altered));
        string diagnostic = mutation == "inherited-conditional"
            ? "inherited ConditionalAttribute is not admitted." : "exact callable lineage changed.";
        Assert.That(exception!.Message, Is.EqualTo($"fold.synchronous.{id}.lineage: {diagnostic}"));
    }

    [Test]
    public void Synchronous_attributes_ir_mutations_fail_at_the_named_gate(
        [Values("executor.processTransaction", "executor.gas-limit-helper", "executor.invalid-result-helper")] string id,
        [Values("conditional", "missing", "duplicate", "arguments", "assembly")] string mutation)
    {
        IrDocument document = ReadCheckedIr();
        SynchronousCallableIdentity callable = document.SynchronousCallables.Single(candidate => candidate.Id == id);
        CallableAttributeIdentity attribute = callable.Attributes.FirstOrDefault() ??
            new("System.Diagnostics.CodeAnalysis.DoesNotReturnAttribute", "Microsoft.TestPlatform.Utilities", false);
        SynchronousCallableIdentity changed = mutation switch
        {
            "conditional" => callable with { HasConditionalAttribute = true },
            "missing" => callable with { Attributes = null! },
            "duplicate" => callable with { Attributes = [.. callable.Attributes, attribute, attribute] },
            "arguments" => callable with { Attributes = [attribute with { HasArguments = true }, .. callable.Attributes.Skip(1)] },
            "assembly" => callable with { Attributes = [attribute with { Assembly = "ShadowAssembly" }, .. callable.Attributes.Skip(1)] },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        IrDocument altered = document with
        {
            SynchronousCallables = document.SynchronousCallables.Select(candidate => candidate.Id == id ? changed : candidate).ToArray(),
        };
        ExtractionException? exception = Assert.Throws<ExtractionException>(() => Extractor.ValidateIrForTest(altered));
        string diagnostic = mutation == "conditional" ? "ConditionalAttribute is not admitted." : "exact attribute inventory changed.";
        Assert.That(exception!.Message, Is.EqualTo($"fold.synchronous.{id}.attributes: {diagnostic}"));
    }

    [Test]
    public void Synchronous_attributes_required_json_flag_cannot_be_omitted_or_duplicated(
        [Values("hasConditionalAttribute", "isOverride", "hasInheritedConditionalAttribute")] string propertyName,
        [Values("missing", "duplicate")] string mutation)
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(),
            "tools/Evm/Lean/SequentialBlockTransactionFoldExtractor/Generated/SequentialBlockTransactionFold.ir.json"));
        string property = $"\"{propertyName}\": false,";
        string changed = ReplaceOnce(source, property, mutation == "missing" ? "" : property + property);
        ExtractionException? exception = Assert.Throws<ExtractionException>(() =>
            ReceiptDependencyAudit.ReadStrict<IrDocument>(Encoding.UTF8.GetBytes(changed)));
        Assert.That(exception!.Message, Does.StartWith("fold.json:"));
    }

    [Test]
    public void Synchronous_callables_reject_compile_valid_deferred_or_bodyless_helper([Values("extern", "iterator")] string mutation)
    {
        string root = FindRepoRoot();
        string source = File.ReadAllText(Path.Combine(root, Extractor.ExecutorPath));
        const string declaration = "protected virtual void ProcessTransaction(";
        int start = source.IndexOf(declaration, StringComparison.Ordinal);
        int body = source.IndexOf('{', start);
        int next = source.IndexOf("        public void SetupTxTimingMetrics(", body, StringComparison.Ordinal);
        Assert.That(start >= 0 && body > start && next > body, Is.True);
        string changed = mutation == "extern"
            ? source[..start] + source[start..body].TrimEnd().Replace("virtual", "extern", StringComparison.Ordinal) + ";\n\n" + source[next..]
            : source[..start] + source[start..body].Replace("void", "System.Collections.Generic.IEnumerable<int>", StringComparison.Ordinal) +
                "{ yield return 0;" + source[(body + 1)..];
        AssertSourceRejected(root, Extractor.ExecutorPath, changed, "fold.synchronous.executor.processTransaction:");
    }

    [Test]
    public void Synchronous_callable_ir_mutations_fail_at_the_named_gate(
        [Values("executor.processTransaction", "executor.gas-limit-helper", "executor.invalid-result-helper")] string id,
        [Values("async", "iterator", "partial", "extern", "abstract", "bodyless")] string mutation)
    {
        IrDocument document = ReadCheckedIr();
        SynchronousCallableIdentity callable = document.SynchronousCallables.Single(candidate => candidate.Id == id);
        SynchronousCallableIdentity changed = mutation switch
        {
            "async" => callable with { IsAsync = true },
            "iterator" => callable with { IsIterator = true },
            "partial" => callable with { IsPartial = true },
            "extern" => callable with { IsExtern = true },
            "abstract" => callable with { IsAbstract = true },
            "bodyless" => callable with { BodyKind = "none" },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        IrDocument altered = document with
        {
            SynchronousCallables = document.SynchronousCallables.Select(candidate => candidate.Id == id ? changed : candidate).ToArray(),
        };
        ExtractionException? exception = Assert.Throws<ExtractionException>(() => Extractor.ValidateIrForTest(altered));
        Assert.That(exception!.Message, Does.StartWith($"fold.synchronous.{id}:"));
    }

    [Test]
    public void Synchronous_callable_inventory_mutations_fail_at_the_named_gate([Values("missing", "duplicate")] string mutation)
    {
        IrDocument document = ReadCheckedIr();
        SynchronousCallableIdentity[] changed = mutation == "missing"
            ? document.SynchronousCallables[1..]
            : [.. document.SynchronousCallables, document.SynchronousCallables[0]];
        ExtractionException? exception = Assert.Throws<ExtractionException>(() =>
            Extractor.ValidateIrForTest(document with { SynchronousCallables = changed }));
        Assert.That(exception!.Message, Does.StartWith("fold.synchronous.inventory:"));
    }

    [TestCase("adapter-order")]
    [TestCase("adapter-early-return")]
    [TestCase("adapter-local-owner")]
    [TestCase("adapter-lambda-owner")]
    [TestCase("tracer-reset-index")]
    [TestCase("tracer-reset-index-conditional")]
    [TestCase("tracer-reset-receipts")]
    [TestCase("tracer-reset-block-gas")]
    [TestCase("tracer-reset-receipt-gas")]
    [TestCase("tracer-success-append")]
    [TestCase("tracer-success-shadow-target")]
    [TestCase("tracer-success-conditional")]
    [TestCase("tracer-failure-append")]
    [TestCase("tracer-failure-conditional")]
    [TestCase("tracer-receipt-index")]
    [TestCase("tracer-start-order")]
    [TestCase("tracer-start-shadow-receiver")]
    [TestCase("tracer-start-parameter-alias")]
    [TestCase("tracer-receipt-index-shadow")]
    [TestCase("bal-spec-parameter-alias")]
    [TestCase("bal-block-parameter-alias")]
    [TestCase("tracer-end-order")]
    [TestCase("tracer-end-index")]
    [TestCase("tracer-end-index-conditional")]
    [TestCase("bal-enabled-spec")]
    [TestCase("bal-enabled-derived")]
    [TestCase("bal-disabled-guard")]
    [TestCase("bal-disabled-inner")]
    [TestCase("bal-inner-else")]
    [TestCase("bal-inner-noop")]
    [TestCase("parallel-selection")]
    [TestCase("di-base")]
    [TestCase("di-decorator")]
    [TestCase("di-manager")]
    [TestCase("di-order")]
    [TestCase("exact-base-virtual")]
    [TestCase("conditional-start")]
    [TestCase("conditional-precommit")]
    [TestCase("early-return-after-fold-before-callback")]
    [TestCase("early-return-after-callback-before-postcommit")]
    [TestCase("gas-throw-conditional")]
    [TestCase("gas-throw-noop")]
    [TestCase("gas-throw-bypass-in-true-arm")]
    [TestCase("invalid-result-conditional")]
    [TestCase("invalid-result-noop")]
    [TestCase("invalid-result-throw-bypass-in-true-arm")]
    [TestCase("bal-inner-return-bypass-in-true-arm")]
    [TestCase("no-subscriber-bypass")]
    [TestCase("subscriber-only-commit")]
    [TestCase("callback-else-arm")]
    [TestCase("callback-local-owner")]
    [TestCase("callback-lambda-owner")]
    [TestCase("callback-shadow-receiver")]
    public void Auxiliary_source_mutations_fail_closed_without_writing_partial_artifacts(string mutation)
    {
        string root = FindRepoRoot();
        (string relativePath, string mutated) = MutateAuxiliarySource(root, mutation);
        AssertSourceRejected(root, relativePath, mutated, ExpectedDiagnostic(mutation));
    }

    [TestCase("manifest")]
    [TestCase("source-pins")]
    [TestCase("receipt-inventory")]
    [TestCase("source-entry")]
    [TestCase("binding-entry")]
    public void Transitive_receipt_closure_mutations_fail_closed(string mutation)
    {
        IrDocument document = ReadCheckedIr();
        ReceiptTerminalSourceClosureIdentity closure = document.Composition.ReceiptTerminalSourceClosure;
        ReceiptTerminalSourceClosureIdentity altered = mutation switch
        {
            "manifest" => closure with { SourceManifestSha256 = new string('0', 64) },
            "source-pins" => closure with { SourcePinsSha256 = new string('0', 64) },
            "receipt-inventory" => closure with { CompilerReferenceInventorySha256 = new string('0', 64) },
            "source-entry" => closure with
            {
                Sources = closure.Sources.Select((source, index) => index == 0
                    ? source with { Sha256 = new string('0', 64) }
                    : source).ToArray(),
            },
            "binding-entry" => closure with
            {
                BindingSources = closure.BindingSources.Select((source, index) => index == 0
                    ? source with { Role = "mutated" }
                    : source).ToArray(),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        IrDocument changed = document with
        {
            Composition = document.Composition with { ReceiptTerminalSourceClosure = altered },
        };
        Assert.That(() => Extractor.ValidateIrForTest(changed), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Generated_lean_is_theorem_free_and_uses_terminal_observation_fields()
    {
        string path = Path.Combine(FindRepoRoot(), Extractor.DefaultLeanPath);
        string source = File.ReadAllText(path);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source, Does.Not.Match(@"(?m)^\s*(theorem|axiom)\s"));
            Assert.That(source, Does.Not.Match(@"\b(sorry|admit|example)\b"));
            Assert.That(source, Does.Contain("ReceiptTerminalFoldExtractor.Generated.ReceiptTerminalFoldKernel"));
            Assert.That(source, Does.Contain("observation.trace.state"));
            Assert.That(source, Does.Contain("observation.result"));
            Assert.That(source, Does.Contain("observation.result == .ok"));
            Assert.That(source, Does.Contain("preTransactionCommit"));
            Assert.That(source, Does.Contain("postTransactionCommit"));
            Assert.That(source, Does.Contain("parallelExecution"));
            Assert.That(source, Does.Contain("indexOrderMismatch"));
            Assert.That(source, Does.Contain("shouldValidate"));
            Assert.That(source, Does.Contain("blockGasLimitExceeded"));
            Assert.That(source, Does.Contain("txTraceStarted"));
            Assert.That(source, Does.Contain("transactionsExecuted"));
            Assert.That(source, Does.Contain("malformedTerminalObservation"));
            Assert.That(source, Does.Contain("balDecoratorActive"));
        }
    }

    [Test]
    public void Emitter_reproduces_checked_in_generated_lean()
    {
        string root = FindRepoRoot();
        IrDocument document = ReadCheckedIr();
        byte[] expected = File.ReadAllBytes(Path.Combine(root, Extractor.DefaultLeanPath));

        Assert.That(Extractor.EmitLeanForTest(document), Is.EqualTo(expected));
    }

    private static IrDocument ReadCheckedIr() =>
        Extractor.LoadIrForTest(Path.Combine(FindRepoRoot(), Extractor.DefaultOutputPath,
            Extractor.ArtifactName + ".ir.json"));

    private static (string RelativePath, string Mutated) MutateAuxiliarySource(string root, string mutation)
    {
        string relativePath;
        string source;
        switch (mutation)
        {
            case "adapter-order":
                relativePath = Extractor.TransactionProcessorAdapterPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "TransactionResult result = transactionProcessor.Execute(currentTx, receiptsTracer);\n        receiptsTracer.EndTxTrace();",
                    "receiptsTracer.EndTxTrace();\n        TransactionResult result = transactionProcessor.Execute(currentTx, receiptsTracer);"));
            case "adapter-early-return":
                relativePath = Extractor.TransactionProcessorAdapterPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "receiptsTracer.EndTxTrace();\n        return result;",
                    "if (result) return result;\n        receiptsTracer.EndTxTrace();\n        return result;"));
            case "adapter-local-owner":
                relativePath = Extractor.TransactionProcessorAdapterPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "using ITxTracer tracer = receiptsTracer.StartNewTxTrace(currentTx);\n        TransactionResult result = transactionProcessor.Execute(currentTx, receiptsTracer);\n        receiptsTracer.EndTxTrace();\n        return result;",
                    "TransactionResult Local()\n        {\n            using ITxTracer localTracer = receiptsTracer.StartNewTxTrace(currentTx);\n            TransactionResult localResult = transactionProcessor.Execute(currentTx, receiptsTracer);\n            receiptsTracer.EndTxTrace();\n            return localResult;\n        }\n\n        return Local();"));
            case "adapter-lambda-owner":
                relativePath = Extractor.TransactionProcessorAdapterPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "using ITxTracer tracer = receiptsTracer.StartNewTxTrace(currentTx);\n        TransactionResult result = transactionProcessor.Execute(currentTx, receiptsTracer);\n        receiptsTracer.EndTxTrace();\n        return result;",
                    "System.Func<TransactionResult> local = () =>\n        {\n            using ITxTracer localTracer = receiptsTracer.StartNewTxTrace(currentTx);\n            TransactionResult localResult = transactionProcessor.Execute(currentTx, receiptsTracer);\n            receiptsTracer.EndTxTrace();\n            return localResult;\n        };\n\n        return local();"));
            case "tracer-reset-index":
                relativePath = Extractor.BlockReceiptsTracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source, "_currentIndex = 0;", "_currentIndex = 1;"));
            case "tracer-reset-index-conditional":
                relativePath = Extractor.BlockReceiptsTracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "_currentIndex = 0;",
                    "if (block.Transactions.Length >= 0)\n        {\n            _currentIndex = 0;\n        }"));
            case "tracer-reset-receipts":
                relativePath = Extractor.BlockReceiptsTracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source, "_txReceipts.Clear();", "_txReceipts.TrimExcess();"));
            case "tracer-reset-block-gas":
                relativePath = Extractor.BlockReceiptsTracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source, "_cumulativeBlockGasPerTx.Clear();", "_cumulativeBlockGasPerTx.TrimExcess();"));
            case "tracer-reset-receipt-gas":
                relativePath = Extractor.BlockReceiptsTracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source, "_cumulativeReceiptGas = 0;", "_cumulativeReceiptGas = 1;"));
            case "tracer-success-append":
                relativePath = Extractor.BlockReceiptsTracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "_txReceipts.Add(BuildReceipt(recipient, gasSpent, StatusCode.Success, logs, stateRoot));",
                    "_txReceipts.Add(BuildFailedReceipt(recipient, gasSpent, null, stateRoot));"));
            case "tracer-success-shadow-target":
                relativePath = Extractor.BlockReceiptsTracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "_txReceipts.Add(BuildReceipt(recipient, gasSpent, StatusCode.Success, logs, stateRoot));",
                    "static TxReceipt BuildReceipt(Address recipient, in GasConsumed gasSpent, byte status, LogEntry[] logs, Hash256? root) => new();\n        _txReceipts.Add(BuildReceipt(recipient, gasSpent, StatusCode.Success, logs, stateRoot));"));
            case "tracer-success-conditional":
                relativePath = Extractor.BlockReceiptsTracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "_txReceipts.Add(BuildReceipt(recipient, gasSpent, StatusCode.Success, logs, stateRoot));",
                    "if (logs.Length >= 0)\n        {\n            _txReceipts.Add(BuildReceipt(recipient, gasSpent, StatusCode.Success, logs, stateRoot));\n        }"));
            case "tracer-failure-append":
                relativePath = Extractor.BlockReceiptsTracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "_txReceipts.Add(BuildFailedReceipt(recipient, gasSpent, error, stateRoot));",
                    "_txReceipts.Add(BuildReceipt(recipient, gasSpent, StatusCode.Failure, [], stateRoot));"));
            case "tracer-failure-conditional":
                relativePath = Extractor.BlockReceiptsTracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "_txReceipts.Add(BuildFailedReceipt(recipient, gasSpent, error, stateRoot));",
                    "if (output.Length >= 0)\n        {\n            _txReceipts.Add(BuildFailedReceipt(recipient, gasSpent, error, stateRoot));\n        }"));
            case "tracer-receipt-index":
                relativePath = Extractor.BlockReceiptsTracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source, "Index = _currentIndex,", "Index = _currentIndex + 1,"));
            case "tracer-start-shadow-receiver":
                relativePath = Extractor.BlockReceiptsTracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "        _currentTxTracer = _otherTracer.StartNewTxTrace(tx);",
                    "        IBlockTracer _otherTracer = NullBlockTracer.Instance;\n        _currentTxTracer = _otherTracer.StartNewTxTrace(tx);"));
            case "tracer-start-parameter-alias":
                relativePath = Extractor.BlockReceiptsTracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "public ITxTracer StartNewTxTrace(Transaction? tx)\n    {\n        CurrentTx = tx;",
                    "public ITxTracer StartNewTxTrace(Transaction? sourceTx)\n    {\n        Transaction? tx = sourceTx;\n        CurrentTx = tx;"));
            case "tracer-receipt-index-shadow":
                relativePath = Extractor.BlockReceiptsTracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "        ulong cumulativeReceiptGas = UpdateCumulativeGasTracking(gasConsumed);",
                    "        int _currentIndex = 0;\n        ulong cumulativeReceiptGas = UpdateCumulativeGasTracking(gasConsumed);"));
            case "bal-spec-parameter-alias":
                relativePath = Extractor.BlockAccessListManagerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "public void PrepareForProcessing(Block suggestedBlock, IReleaseSpec spec, ProcessingOptions options)\n    {",
                    "public void PrepareForProcessing(Block suggestedBlock, IReleaseSpec sourceSpec, ProcessingOptions options)\n    {\n        IReleaseSpec spec = sourceSpec;"));
            case "bal-block-parameter-alias":
                relativePath = Extractor.BlockAccessListManagerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "public void PrepareForProcessing(Block suggestedBlock, IReleaseSpec spec, ProcessingOptions options)\n    {",
                    "public void PrepareForProcessing(Block sourceBlock, IReleaseSpec spec, ProcessingOptions options)\n    {\n        Block suggestedBlock = sourceBlock;"));
            case "tracer-start-order":
                relativePath = Extractor.BlockReceiptsTracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "CurrentTx = tx;\n        _currentTxTracer = _otherTracer.StartNewTxTrace(tx);",
                    "_currentTxTracer = _otherTracer.StartNewTxTrace(tx);\n        CurrentTx = tx;"));
            case "tracer-end-order":
                relativePath = Extractor.BlockReceiptsTracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "_otherTracer.EndTxTrace();\n        _currentIndex++;",
                    "_currentIndex++;\n        _otherTracer.EndTxTrace();"));
            case "tracer-end-index":
                relativePath = Extractor.BlockReceiptsTracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source, "_currentIndex++;", "_currentIndex += 2;"));
            case "tracer-end-index-conditional":
                relativePath = Extractor.BlockReceiptsTracerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "_currentIndex++;",
                    "if (_currentIndex >= 0)\n        {\n            _currentIndex++;\n        }"));
            case "bal-enabled-spec":
                relativePath = Extractor.BlockAccessListManagerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "_blockAccessListsEnabled = spec.BlockLevelAccessListsEnabled;",
                    "_blockAccessListsEnabled = false;"));
            case "bal-enabled-derived":
                relativePath = Extractor.BlockAccessListManagerPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "Enabled = _blockAccessListsEnabled && !suggestedBlock.IsGenesis;",
                    "Enabled = true;"));
            case "bal-disabled-guard":
                relativePath = Extractor.ParallelExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source, "if (!balManager.Enabled)", "if (balManager.Enabled)"));
            case "bal-disabled-inner":
                relativePath = Extractor.ParallelExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "inner.ProcessTransactions(block, processingOptions, receiptsTracer, token)",
                    "inner.ProcessTransactions(block, processingOptions, receiptsTracer, default)"));
            case "bal-inner-else":
                relativePath = Extractor.ParallelExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "if (!balManager.Enabled)\n            {\n                return inner.ProcessTransactions(block, processingOptions, receiptsTracer, token);\n            }",
                    "if (!balManager.Enabled)\n            {\n            }\n            else\n            {\n                return inner.ProcessTransactions(block, processingOptions, receiptsTracer, token);\n            }"));
            case "bal-inner-noop":
                relativePath = Extractor.ParallelExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "return inner.ProcessTransactions(block, processingOptions, receiptsTracer, token);",
                    "inner.ProcessTransactions(block, processingOptions, receiptsTracer, token);"));
            case "parallel-selection":
                relativePath = Extractor.ParallelExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "&& balManager.ParallelExecutionEnabled",
                    "&& !balManager.ParallelExecutionEnabled"));
            case "di-base":
                relativePath = Extractor.BlockProcessingModulePath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "BlockProcessor.BlockValidationTransactionsExecutor>()",
                    "BlockProcessor.ParallelBlockValidationTransactionsExecutor>()"));
            case "di-decorator":
                relativePath = Extractor.BlockProcessingModulePath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    ".AddDecorator<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.ParallelBlockValidationTransactionsExecutor>();",
                    ".AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.ParallelBlockValidationTransactionsExecutor>();"));
            case "di-manager":
                relativePath = Extractor.BlockProcessingModulePath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "AddScoped<IBlockAccessListManager, BlockAccessListManager>()",
                    "AddScoped<IBlockAccessListManager, NullBlockAccessListManager>()"));
            case "di-order":
                relativePath = Extractor.BlockProcessingModulePath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    ".AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockValidationTransactionsExecutor>()\n            .AddDecorator<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.ParallelBlockValidationTransactionsExecutor>();",
                    ".AddDecorator<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.ParallelBlockValidationTransactionsExecutor>()\n            .AddScoped<IBlockProcessor.IBlockTransactionsExecutor, BlockProcessor.BlockValidationTransactionsExecutor>();"));
            case "exact-base-virtual":
                relativePath = Extractor.ExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "public TxReceipt[] ProcessTransactions(",
                    "public virtual TxReceipt[] ProcessTransactions("));
            case "conditional-start":
                relativePath = Extractor.BlockProcessorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "        ReceiptsTracer.StartNewBlockTrace(block);",
                    "        if (block.Transactions.Length > 0)\n        {\n            ReceiptsTracer.StartNewBlockTrace(block);\n        }"));
            case "conditional-precommit":
                relativePath = Extractor.BlockProcessorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "        CommitState(spec);\n\n        TxReceipt[] receipts = _blockTransactionsExecutor.ProcessTransactions(block, options, ReceiptsTracer, token);",
                    "        if (block.Transactions.Length > 0)\n        {\n            CommitState(spec);\n        }\n\n        TxReceipt[] receipts = _blockTransactionsExecutor.ProcessTransactions(block, options, ReceiptsTracer, token);"));
            case "early-return-after-fold-before-callback":
                relativePath = Extractor.BlockProcessorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "        TxReceipt[] receipts = _blockTransactionsExecutor.ProcessTransactions(block, options, ReceiptsTracer, token);\n\n        // Signal that transactions are done — subscribers can cancel background work (e.g. prewarmer)",
                    "        TxReceipt[] receipts = _blockTransactionsExecutor.ProcessTransactions(block, options, ReceiptsTracer, token);\n\n        if (block.Transactions.Length > 0)\n        {\n            return receipts;\n        }\n\n        // Signal that transactions are done — subscribers can cancel background work (e.g. prewarmer)"));
            case "early-return-after-callback-before-postcommit":
                relativePath = Extractor.BlockProcessorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "        TransactionsExecuted?.Invoke();\n\n        CommitState(spec);",
                    "        TransactionsExecuted?.Invoke();\n\n        if (block.Transactions.Length > 0)\n        {\n            return receipts;\n        }\n\n        CommitState(spec);"));
            case "gas-throw-conditional":
                relativePath = Extractor.ExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "if (shouldValidate && block.Header.GasUsed > block.Header.GasLimit)\n                {\n                    ThrowInvalidBlockForGasLimit(block);\n                }",
                    "if (shouldValidate && block.Header.GasUsed > block.Header.GasLimit)\n                {\n                    if (true) ThrowInvalidBlockForGasLimit(block);\n                }"));
            case "gas-throw-noop":
                relativePath = Extractor.ExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "ThrowInvalidBlockForGasLimit(block);",
                    "return [.. receiptsTracer.TxReceipts];"));
            case "gas-throw-bypass-in-true-arm":
                relativePath = Extractor.ExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "if (shouldValidate && block.Header.GasUsed > block.Header.GasLimit)\n                {\n                    ThrowInvalidBlockForGasLimit(block);\n                }",
                    "if (shouldValidate && block.Header.GasUsed > block.Header.GasLimit)\n                {\n                    if (block.Transactions.Length > 0)\n                    {\n                        return [.. receiptsTracer.TxReceipts];\n                    }\n\n                    ThrowInvalidBlockForGasLimit(block);\n                }"));
            case "invalid-result-conditional":
                relativePath = Extractor.ExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "if (!result) ThrowInvalidTransactionException(result, block.Header, currentTx, index);",
                    "if (!result)\n            {\n                if (true) ThrowInvalidTransactionException(result, block.Header, currentTx, index);\n            }"));
            case "invalid-result-noop":
                relativePath = Extractor.ExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "if (!result) ThrowInvalidTransactionException(result, block.Header, currentTx, index);",
                    "if (!result) return;"));
            case "invalid-result-throw-bypass-in-true-arm":
                relativePath = Extractor.ExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "if (!result) ThrowInvalidTransactionException(result, block.Header, currentTx, index);",
                    "if (!result)\n            {\n                if (index > 0) return;\n                ThrowInvalidTransactionException(result, block.Header, currentTx, index);\n            }"));
            case "bal-inner-return-bypass-in-true-arm":
                relativePath = Extractor.ParallelExecutorPath;
                source = ReadSource(root, relativePath);
                return (relativePath, ReplaceOnce(source,
                    "if (!balManager.Enabled)\n            {\n                return inner.ProcessTransactions(block, processingOptions, receiptsTracer, token);\n            }",
                    "if (!balManager.Enabled)\n            {\n                if (block.Transactions.Length > 0) return [];\n                return inner.ProcessTransactions(block, processingOptions, receiptsTracer, token);\n            }"));
            case "no-subscriber-bypass":
            case "subscriber-only-commit":
            case "callback-else-arm":
            case "callback-local-owner":
            case "callback-lambda-owner":
            case "callback-shadow-receiver":
                relativePath = Extractor.BlockProcessorPath;
                source = ReadSource(root, relativePath);
                string signalReplacement = mutation switch
                {
                    "no-subscriber-bypass" => "if (TransactionsExecuted is null) return receipts;\n        TransactionsExecuted?.Invoke();",
                    "subscriber-only-commit" => "TransactionsExecuted?.Invoke();\n\n        if (TransactionsExecuted is not null) { CommitState(spec); }",
                    "callback-else-arm" => "if (block.Transactions.Length > 0) { } else { TransactionsExecuted?.Invoke(); }",
                    "callback-local-owner" => "void SignalTransactions() { TransactionsExecuted?.Invoke(); }\n        SignalTransactions();",
                    "callback-lambda-owner" => "System.Action signalTransactions = () => { TransactionsExecuted?.Invoke(); };\n        signalTransactions();",
                    "callback-shadow-receiver" => "System.Action? TransactionsExecuted = null;\n        TransactionsExecuted?.Invoke();",
                    _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
                };
                return (relativePath, ReplaceOnce(source, mutation == "subscriber-only-commit"
                    ? "TransactionsExecuted?.Invoke();\n\n        CommitState(spec);" : "TransactionsExecuted?.Invoke();", signalReplacement));
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        static string ReadSource(string root, string relativePath) =>
            Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, relativePath))).Replace("\r\n", "\n", StringComparison.Ordinal);
    }



    [TestCase(Extractor.TransactionProcessorAdapterPath)]
    [TestCase(Extractor.BlockReceiptsTracerPath)]
    [TestCase(Extractor.BlockAccessListManagerPath)]
    [TestCase(Extractor.BlockAccessListInterfacePath)]
    [TestCase(Extractor.ParallelExecutorPath)]
    [TestCase(Extractor.BlockProcessingModulePath)]
    [TestCase(Extractor.TransactionProcessorAdapterInterfacePath)]
    [TestCase("src/Nethermind/Nethermind.Consensus/Processing/ExecutionFlags.std.cs")]
    [TestCase("src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListValidationIndex.cs")]
    [TestCase("src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListValidationIndex.LaneStore.cs")]
    [TestCase("src/Nethermind/Nethermind.Consensus/Processing/BlockCachePreWarmer.cs")]
    [TestCase("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.SystemContractHandler.cs")]
    [TestCase("src/Nethermind/Nethermind.Consensus/Processing/BlockProcessor.BlockAccessListSystemContractHandler.cs")]
    [TestCase("src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.Validation.cs")]
    [TestCase("src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.StateChanges.cs")]
    [TestCase("src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.SystemContracts.cs")]
    [TestCase("src/Nethermind/Nethermind.Consensus/Processing/BlockAccessListManager.TxProcessorPool.cs")]
    [TestCase("src/Nethermind/Nethermind.Blockchain/Tracing/BlockReceiptGasAccountingKernel.cs")]
    public void Complete_auxiliary_compilation_rejects_errors_outside_admitted_members(string relativePath)
    {
        string root = FindRepoRoot();
        string source = File.ReadAllText(Path.Combine(root, relativePath));
        Dictionary<string, byte[]> overrides = new(StringComparer.Ordinal)
        {
            [relativePath] = Encoding.UTF8.GetBytes(source +
                "\ninternal static class FoldUnboundProbe { static void Probe() => MissingFoldSymbol(); }\n"),
        };
        ExtractionException? exception = Assert.Throws<ExtractionException>(() =>
            Extractor.ValidateCompilationForTest(root, overrides));
        Assert.That(exception!.Message, Does.StartWith("fold.compiler.compilation:"));
        Assert.That(exception.Message, Does.Contain("CS0103").And.Contain("MissingFoldSymbol"));
    }

    [TestCase("target")]
    [TestCase("alias")]
    [TestCase("condition")]
    [TestCase("duplicate")]
    public void Global_alias_binding_rejects_changed_msbuild_inputs(string mutation)
    {
        const string path = "src/Nethermind/Directory.Build.props";
        const string declaration = "<Using Include=\"System.Runtime.Intrinsics.Vector256&lt;byte&gt;\" Alias=\"EvmWord\" />";
        string root = FindRepoRoot();
        string source = File.ReadAllText(Path.Combine(root, path));
        string changed = mutation switch
        {
            "target" => ReplaceOnce(source, "Vector256&lt;byte&gt;", "Vector128&lt;byte&gt;"),
            "alias" => ReplaceOnce(source, "Alias=\"EvmWord\"", "Alias=\"OtherWord\""),
            "condition" => ReplaceOnce(source, "!$(TargetFramework.StartsWith('netstandard'))", "false"),
            "duplicate" => ReplaceOnce(source, declaration, declaration + declaration),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        Dictionary<string, byte[]> overrides = new(StringComparer.Ordinal)
        {
            [path] = Encoding.UTF8.GetBytes(changed),
        };
        ExtractionException? exception = Assert.Throws<ExtractionException>(() =>
            Extractor.ValidateCompilationForTest(root, overrides));
        Assert.That(exception!.Message, Does.StartWith("fold.compiler.global-alias:"));
    }

    private static string ExpectedDiagnostic(string mutation) => mutation switch
    {
        "adapter-order" => "TransactionProcessorAdapterExtensions must order StartNewTxTrace, Execute, and EndTxTrace.",
        "adapter-early-return" => "TransactionProcessorAdapterExtensions EndTxTrace must postdominate Execute on the normal path.",
        "adapter-local-owner" => "Auxiliary anchor 'adapter.tx-trace-start' is owned by a local function or lambda.",
        "adapter-lambda-owner" => "Auxiliary anchor 'adapter.tx-trace-start' is owned by a local function or lambda.",
        "tracer-reset-index" => "Auxiliary anchor 'tracer.reset-index' expected one canonical node or invocation",
        "tracer-reset-index-conditional" => "BlockReceiptsTracer currentIndex reset must dominate every normal exit.",
        "tracer-reset-receipts" => "Auxiliary anchor 'tracer.reset-receipts' expected one typed invocation",
        "tracer-reset-block-gas" => "Auxiliary anchor 'tracer.reset-block-gas' expected one typed invocation",
        "tracer-reset-receipt-gas" => "Auxiliary anchor 'tracer.reset-receipt-gas' expected one canonical node or invocation",
        "tracer-success-shadow-target" => "fold.argument.binding: unexpected exact symbol",
        "tracer-success-append" => "Auxiliary anchor 'tracer.success-append' expected one typed invocation",
        "tracer-success-conditional" => "Auxiliary anchor 'tracer.success-append' must be a direct statement in its admitted method body.",
        "tracer-failure-append" => "Auxiliary anchor 'tracer.failure-append' expected one typed invocation",
        "tracer-failure-conditional" => "Auxiliary anchor 'tracer.failure-append' must be a direct statement in its admitted method body.",
        "tracer-receipt-index" => "Auxiliary anchor 'tracer.receipt-index' expected one canonical node or invocation",
        "tracer-start-shadow-receiver" => "fold.assignment.tracer.tx-start-delegate: exact right-hand-side bindings changed.",
        "tracer-start-parameter-alias" => "fold.assignment.tracer.tx-start-current: exact right-hand-side bindings changed.",
        "tracer-receipt-index-shadow" => "fold.assignment.tracer.receipt-index: exact right-hand-side bindings changed.",
        "bal-spec-parameter-alias" => "fold.assignment.bal.enabled-spec: exact right-hand-side bindings changed.",
        "bal-block-parameter-alias" => "fold.assignment.bal.enabled-derived: exact right-hand-side bindings changed.",
        "tracer-start-order" => "BlockReceiptsTracer.StartNewTxTrace must record CurrentTx before delegating.",
        "tracer-end-order" => "BlockReceiptsTracer.EndTxTrace must delegate before advancing currentIndex.",
        "tracer-end-index" => "Auxiliary anchor 'tracer.tx-end-index' expected one canonical node or invocation",
        "tracer-end-index-conditional" => "BlockReceiptsTracer currentIndex increment must postdominate delegation on the normal path.",
        "bal-enabled-spec" => "Auxiliary anchor 'bal.enabled-spec' expected one canonical node or invocation",
        "bal-enabled-derived" => "Auxiliary anchor 'bal.enabled-derived' expected one canonical node or invocation",
        "bal-disabled-guard" => "Auxiliary anchor 'parallel.bal-disabled-gate' expected one canonical node or invocation",
        "bal-disabled-inner" => "Auxiliary anchor 'parallel.inner-sequential' expected one typed invocation",
        "bal-inner-else" => "The BAL-disabled decorator branch must return the inner executor from the guard's true arm.",
        "bal-inner-noop" => "The BAL-disabled decorator branch must return the inner executor from the guard's true arm.",
        "parallel-selection" => "Auxiliary anchor 'parallel.selection' expected one canonical node or invocation",
        "di-base" => "Auxiliary anchor 'di.base-executor' expected one typed invocation",
        "di-decorator" => "fold.binding.di.parallel-decorator: exact target, receiver or arguments changed.",
        "di-manager" => "Auxiliary anchor 'di.bal-manager' expected one typed invocation",
        "di-order" => "standard validation must register the base executor before its decorator.",
        "exact-base-virtual" => "The admitted BlockValidationTransactionsExecutor.ProcessTransactions route is virtual or abstract.",
        "conditional-start" => "StartNewBlockTrace must dominate the normal pre-transaction CommitState(spec).",
        "conditional-precommit" => "The normal pre-transaction CommitState(spec) must dominate the transaction fold.",
        "early-return-after-fold-before-callback" => "TransactionsExecuted must postdominate the normal transaction fold return.",
        "early-return-after-callback-before-postcommit" => "The normal post-transaction CommitState(spec) must postdominate TransactionsExecuted.",
        "gas-throw-conditional" => "Guard 'executor.gas-limit-throw' must contain exactly one direct 'ThrowInvalidBlockForGasLimit(block)' invocation.",
        "gas-throw-noop" => "Guard 'executor.gas-limit-throw' must contain exactly one direct 'ThrowInvalidBlockForGasLimit(block)' invocation.",
        "gas-throw-bypass-in-true-arm" => "Guard 'executor.gas-limit-throw' must contain exactly one direct 'ThrowInvalidBlockForGasLimit(block)' invocation.",
        "invalid-result-conditional" => "ProcessTransaction invalid-result throw must be the direct '!result' guard body.",
        "invalid-result-noop" => "ProcessTransaction invalid-result throw is missing or ambiguous.",
        "invalid-result-throw-bypass-in-true-arm" => "ProcessTransaction invalid-result throw must be the direct '!result' guard body.",
        "bal-inner-return-bypass-in-true-arm" => "The BAL-disabled decorator branch must return the inner executor from the guard's true arm.",
        "no-subscriber-bypass" => "TransactionsExecuted must postdominate the normal transaction fold return.",
        "subscriber-only-commit" => "The normal post-transaction CommitState(spec) must postdominate TransactionsExecuted.",
        "callback-else-arm" => "The first post-fold CommitState(spec) must remain on the normal path after TransactionsExecuted.",
        "callback-local-owner" or "callback-lambda-owner" => "Auxiliary anchor 'block.transactions-executed-signal' is owned by a local function or lambda.",
        "callback-shadow-receiver" => "fold.signal.topology:",
        _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
    };

    private static void AssertSourceRejected(string root, string relativePath, string mutated, string diagnostic)
    {
        Dictionary<string, byte[]> overrides = new(StringComparer.Ordinal)
        {
            [relativePath] = Encoding.UTF8.GetBytes(mutated),
        };
        Assert.DoesNotThrow(() => Extractor.ValidateCompilationForTest(root, overrides),
            "The mutation must compile before source admission is exercised.");
        using TemporaryDirectory output = new();
        ExtractionException? exception = Assert.Throws<ExtractionException>(() =>
            Extractor.ExtractForTest(root, output.Path, overrides));
        Assert.That(exception!.Message, Does.StartWith(diagnostic));
        Assert.That(exception.Message, Does.Not.Contain("fold.receipt").And.Not.Contain("fold.compiler"));
        Assert.That(Directory.EnumerateFileSystemEntries(output.Path), Is.Empty);
    }

    private static string ReplaceOnce(string source, string expected, string replacement)
    {
        int position = source.IndexOf(expected, StringComparison.Ordinal);
        Assert.That(position, Is.GreaterThanOrEqualTo(0));
        return source[..position] + replacement + source[(position + expected.Length)..];
    }

    internal static string FindRepoRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, Extractor.ExecutorPath)))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Cannot find repository root.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "sequential-block-fold-" + Guid.NewGuid().ToString("N"));
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
}
