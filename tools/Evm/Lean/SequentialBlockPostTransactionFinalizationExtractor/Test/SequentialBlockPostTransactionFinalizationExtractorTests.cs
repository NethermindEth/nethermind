// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace Nethermind.Evm.Lean.SequentialBlockPostTransactionFinalizationExtractor.Test;

[TestFixture]
[NonParallelizable]
public sealed partial class SequentialBlockPostTransactionFinalizationExtractorTests
{
    [OneTimeSetUp]
    public void Unchanged_source_baseline_passes_before_any_mutation()
    {
        string root = FindRepoRoot();
        Extractor.ValidateCheckedIn(root, null);
        using TemporaryDirectory output = new();
        Extractor.RequireCompilationForTest(root, new Dictionary<string, byte[]>());
        Extractor.ExtractForTest(root, output.Path, new Dictionary<string, byte[]>());
        ProcessOneValidatedPublicationExtractor.RequireCompilationForTest(root, new Dictionary<string, byte[]>());
        ProcessOneValidatedPublicationExtractor.AuditForTest(root);
    }

    [Test]
    public void Checked_ir_contains_the_bounded_ordered_tail()
    {
        IrDocument document = ReadCheckedIr();
        Assert.That(document.Anchors.Select(static anchor => anchor.Id), Is.EqualTo(new[]
        {
            "block.post-transaction-commit", "block.blob-gas-guard", "block.blob-gas-calculation",
            "block.background-task-null", "block.receipts-background-guard", "block.sync-blooms",
            "block.sync-receipts-root", "block.rewards", "block.withdrawals", "block.finalization-commit",
            "block.execution-requests", "block.end-block-trace", "block.storage-roots-commit",
            "block.main-thread-guard", "block.account-changes", "block.state-root-guard", "block.state-root",
            "block.background-result-guard", "block.bal-finalization", "block.background-finally-guard",
            "block.hash", "block.return-receipts",
        }));
        Assert.That(document.Commits.Select(static commit => commit.Id), Is.EqualTo(new[]
        {
            "post-transaction-no-roots", "finalization-no-roots", "storage-roots",
        }));
        Assert.That(document.Commits.Select(static commit => commit.Event), Is.EqualTo(new[]
        {
            "commitNoRoots(0)", "commitNoRoots(1)", "commitRoots",
        }));
        Assert.That(document.Commits.Select(static commit => commit.CommitRoots), Is.EqualTo(new[] { false, false, true }));
        Assert.That(document.Commits.Select(static commit => commit.InvocationBinding.CanonicalSyntax), Is.EqualTo(new[]
        {
            "CommitState(spec)", "CommitState(spec)", "CommitStateAndStorageRoots(spec)",
        }));
        Assert.That(document.HeaderAssignments.Select(static assignment => assignment.Id), Is.EqualTo(new[]
        {
            "header.blob-gas-used", "header.receipts-root",
        }));
        Assert.That(document.HeaderAssignments.Select(static assignment => assignment.SelectedArm), Is.EqualTo(new[]
        {
            "true-arm", "false-arm",
        }));
        Assert.That(document.HeaderAssignments[1].ExcludedBindings, Has.Length.EqualTo(1));
        Assert.That(document.HeaderAssignments[1].ExcludedWriteCanonicals, Is.EqualTo(new[]
        {
            "(header.Bloom,header.ReceiptsRoot)=bloomsAndReceiptsRootTask.GetAwaiter().GetResult()",
        }));
        Assert.That(document.Steps.Select(static step => step.Ordinal), Is.EqualTo(Enumerable.Range(0, 16)));
        Assert.That(document.Bridge.FoldArtifact, Does.Contain("SequentialBlockTransactionFoldExtractor/Generated/SequentialBlockTransactionFold.lean"));
        Assert.That(document.Bridge.RequiredTerminalFields, Is.EqualTo(new[]
        {
            "outcome", "committed", "state.receipts[*].index/logs", "terminalResults[*]",
            "completedIndices", "events.postTransactionCommit",
        }));
        Assert.That(document.SourceEntryAdapters.Select(static adapter => adapter.Id), Is.EqualTo(new[]
        {
            "block.processBlock.standard-exact-base-adapter",
        }));
        Assert.That(document.SourceEntryAdapters.Single().ReceiptsReferenceCount, Is.EqualTo(8));
        Assert.That(document.SourceEntryAdapters.Single().ReceiptsWriteCount, Is.EqualTo(1));
        Assert.That(document.SourceEntryAdapters.Single().ReceiptsBinding.SymbolKind, Is.EqualTo("Local"));
        Assert.That(document.Members.Select(static member => member.Id), Is.EqualTo(new[]
        {
            "block.processBlock", "block.commit-no-roots", "block.commit-roots", "block.compute-state-root",
            "block.set-account-changes",
        }));
        Assert.That(document.ControlFlows.Select(static flow => flow.Id), Is.EqualTo(new[]
        {
            "block.processBlock", "block.commit-no-roots", "block.commit-roots", "block.compute-state-root",
            "block.set-account-changes",
        }));
        Assert.That(document.SourceEntryAdapters.Single().StateRootWriteBinding.CanonicalSyntax,
            Is.EqualTo("header.StateRoot=_stateProvider.StateRoot"));
        Assert.That(document.SourceEntryAdapters.Single().StateRootValueBinding.SymbolId,
            Is.EqualTo("global::Nethermind.Evm.State.IReadOnlyStateProvider.StateRoot"));
        Assert.That(document.SourceEntryAdapters.Single().AccountChangesWriteBinding.CanonicalSyntax,
            Is.EqualTo("block.AccountChanges=_stateProvider.GetAccountChanges()"));
        Assert.That(document.SourceEntryAdapters.Single().AccountChangesValueBinding.SymbolId,
            Is.EqualTo("global::Nethermind.Evm.State.IWorldState.GetAccountChanges()"));
        Assert.That(document.SourceEntryAdapters.Single().TransactionsExecutedNormalReturnPremise,
            Is.EqualTo("runtime premise: TransactionsExecuted subscribers return normally"));
        Assert.That(document.SourceEntryAdapters.Single().PostTransactionCommitNormalReturnPremise,
            Is.EqualTo("runtime premise: post-transaction CommitState(spec) returns normally"));
        Assert.That(document.SourceEntryAdapters.Single().TransactionsExecutedEventSymbol,
            Is.EqualTo("global::Nethermind.Consensus.Processing.BlockProcessor.TransactionsExecuted"));
        Assert.That(document.SourceEntryAdapters.Single().TransactionsExecutedBinding.CanonicalSyntax,
            Is.EqualTo("TransactionsExecuted?.Invoke()"));
        Assert.That(document.SourceEntryAdapters.Single().TransactionsExecutedBinding.SymbolId,
            Is.EqualTo("global::System.Action.Invoke()"));
        ConditionalSignalIdentity signal = document.SourceEntryAdapters.Single().ConditionalSignal;
        Assert.That(signal.Evaluation.ControlFlowBlock, Is.Not.EqualTo(signal.WhenNotNullBlock));
        Assert.That(signal.WhenNullBlock, Is.Not.EqualTo(signal.WhenNotNullBlock));
        Assert.That(document.ControlFlows.Single(flow => flow.Id == "block.processBlock").ExceptionHandlerEntries,
            Has.Length.EqualTo(1));
        Assert.That(document.SourceEntryAdapters.Single().ExecutorBinding.ReadInside,
            Is.EqualTo(document.SourceEntryAdapters.Single().EntryBinding.ReadInside));
        Assert.That(document.SourceEntryAdapters.Single().ExecutorBinding.WrittenInside,
            Is.EqualTo(document.SourceEntryAdapters.Single().EntryBinding.WrittenInside));
        Assert.That(document.SourceEntryAdapters.Single().PostTransactionCommitBinding.CanonicalSyntax,
            Is.EqualTo("CommitState(spec)"));
        Assert.That(document.TaskFlow.SynchronousAssignments, Is.EqualTo(0));
        Assert.That(document.TaskFlow.LocalSymbol, Is.EqualTo(
            "global::Nethermind.Consensus.Processing.BlockProcessor.ProcessBlock.bloomsAndReceiptsRootTask"));
        Assert.That(document.TaskFlow.NullPreservedInSynchronousArm, Is.True);
        Assert.That(document.Bridge.Relation, Does.Contain("projection"));
        Assert.That(document.Bridge.Relation, Does.Not.Contain("result ="));
        Assert.That(document.Bridge.Relation, Does.Contain("not a whole-result equality premise"));
        Assert.That(document.Bridge.RequiredFoldPremises, Does.Contain(
            "projectFoldReceipts(fold.state.receipts)=input.receipts (elementwise)"));
        Assert.That(document.Bridge.RequiredFoldPremises, Does.Contain(
            "fold.completedIndices=input.fold.completedIndices (elementwise)"));
        Assert.That(document.Bridge.RequiredFoldPremises, Does.Contain(
            "fold.outcome=.completed -> fold.terminalResults are all .ok (upstream fold theorem)"));
    }

    [TestCase("operation")]
    [TestCase("symbol")]
    [TestCase("position")]
    [TestCase("source")]
    [TestCase("dataflow")]
    public void Typed_ir_mutations_fail_closed(string mutation)
    {
        IrDocument document = ReadCheckedIr();
        AnchorIdentity target = document.Anchors.Single(static anchor => anchor.Id == "block.end-block-trace");
        TypedBinding binding = mutation switch
        {
            "operation" => target.Binding with { OperationKind = string.Empty },
            "symbol" => target.Binding with { SymbolId = string.Empty },
            "position" => target.Binding with { Position = 0 },
            "source" => target.Binding with { Path = string.Empty },
            "dataflow" => target.Binding with { DataFlowSucceeded = false },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        IrDocument altered = document with
        {
            Anchors = document.Anchors.Select(anchor => anchor.Id == target.Id
                ? target with { Binding = binding }
                : anchor).ToArray(),
        };
        Assert.That(() => Extractor.ValidateIrForTest(altered), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Tracer_namespace_shadow_compiles_but_fails_source_admission(
        [Values("normal-tail", "publication")] string boundary)
    {
        string root = FindRepoRoot();
        const string processorInterfacePath = "src/Nethermind/Nethermind.Consensus/Processing/IBlockProcessor.cs";
        Dictionary<string, byte[]> overrides = new(StringComparer.Ordinal);
        foreach (string path in new[] { Extractor.BlockProcessorPath, processorInterfacePath })
        {
            string source = File.ReadAllText(Path.Combine(root, path));
            string changed = System.Text.RegularExpressions.Regex.Replace(source, @"\bIBlockTracer\b",
                "global::Nethermind.Blockchain.Tracing.IBlockTracer");
            Assert.That(changed, Is.Not.EqualTo(source), "The explicit parameter type must change before admission is tested.");
            if (path == processorInterfacePath)
                changed += "\nnamespace Nethermind.Blockchain.Tracing\n{\n" +
                    "    public interface IBlockTracer : global::Nethermind.Evm.Tracing.IBlockTracer { }\n}\n";
            overrides.Add(path, Encoding.UTF8.GetBytes(changed));
        }

        ProcessOneValidatedPublicationExtractor.RequireCompilationForTest(root, overrides);
        using TemporaryDirectory output = new();
        if (boundary == "publication")
            Assert.That(() => ProcessOneValidatedPublicationExtractor.AuditForTest(root, overrides),
                Throws.TypeOf<ExtractionException>().With.Message.Contains(
                    "processOne.processBlock-return' is attached to the wrong method"));
        else
            Assert.That(() => Extractor.ExtractForTest(root, output.Path, overrides),
                Throws.TypeOf<ExtractionException>().With.Message.Contains(
                    "ProcessBlock did not bind to the pinned BlockProcessor source method."));
        Assert.That(Directory.EnumerateFileSystemEntries(output.Path), Is.Empty);
    }

    [Test]
    public void Tracer_namespace_ir_identity_mutations_fail_closed(
        [Values("block.rewards", "block.return-receipts")] string anchorId)
    {
        IrDocument document = ReadCheckedIr();
        AnchorIdentity target = document.Anchors.Single(anchor => anchor.Id == anchorId);
        Assert.That(target.Binding.SymbolId, Does.Contain("Nethermind.Evm.Tracing.IBlockTracer"));
        TypedBinding changed = target.Binding with
        {
            SymbolId = target.Binding.SymbolId.Replace("Nethermind.Evm.Tracing.IBlockTracer",
                "Nethermind.Blockchain.Tracing.IBlockTracer", StringComparison.Ordinal),
        };
        IrDocument altered = document with
        {
            Anchors = document.Anchors.Select(anchor => anchor.Id == anchorId
                ? anchor with { Binding = changed } : anchor).ToArray(),
        };
        Assert.That(() => Extractor.ValidateIrForTest(altered), Throws.TypeOf<ExtractionException>());
    }

    [TestCase("assignment")]
    [TestCase("value")]
    [TestCase("guard")]
    [TestCase("excluded-write")]
    [TestCase("excluded-canonical")]
    public void Header_assignment_typed_mutations_fail_closed(string mutation)
    {
        IrDocument document = ReadCheckedIr();
        HeaderAssignmentIdentity target = document.HeaderAssignments[1];
        HeaderAssignmentIdentity alteredTarget = mutation switch
        {
            "assignment" => target with
            {
                AssignmentBinding = target.AssignmentBinding with { OperationKind = string.Empty },
            },
            "value" => target with
            {
                ValueBinding = document.Anchors.Single(anchor => anchor.Id == "block.blob-gas-calculation").Binding,
            },
            "guard" => target with
            {
                GuardBinding = document.Anchors.Single(anchor => anchor.Id == "block.blob-gas-guard").Binding,
            },
            "excluded-write" => target with { ExcludedBindings = [] },
            "excluded-canonical" => target with { ExcludedWriteCanonicals = ["header.ReceiptsRoot=other"] },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        IrDocument altered = document with
        {
            HeaderAssignments = document.HeaderAssignments.Select(assignment => assignment.Id == target.Id
                ? alteredTarget
                : assignment).ToArray(),
        };
        Assert.That(() => Extractor.ValidateIrForTest(altered), Throws.TypeOf<ExtractionException>());
    }

    [TestCase("duplicate-anchor")]
    [TestCase("duplicate-hook")]
    [TestCase("redirect-hook")]
    [TestCase("redirect-guard-binding")]
    [TestCase("redirect-step-binding")]
    [TestCase("malformed-edge")]
    [TestCase("duplicate-edge")]
    [TestCase("missing-edge")]
    [TestCase("disconnected-block")]
    [TestCase("duplicate-flow")]
    [TestCase("unreachable-edge")]
    [TestCase("unsorted-edge")]
    [TestCase("dominance-edge")]
    [TestCase("redirect-back-edge")]
    [TestCase("redirect-normal-exit")]
    [TestCase("missing-handler-entry")]
    [TestCase("redirect-handler-entry")]
    [TestCase("duplicate-handler-entry")]
    [TestCase("binding-unreachable")]
    [TestCase("binding-redirect-reachable")]
    [TestCase("duplicate-membership")]
    [TestCase("graph-shape-digest")]
    [TestCase("commit-event")]
    [TestCase("commit-anchor")]
    [TestCase("commit-method")]
    [TestCase("commit-invocation-binding")]
    public void CFG_and_exact_mapping_mutations_fail_closed(string mutation)
    {
        IrDocument document = ReadCheckedIr();
        IrDocument altered = mutation switch
        {
            "duplicate-anchor" => document with
            {
                Anchors = document.Anchors.Select((anchor, index) => index == 1
                    ? anchor with { Id = document.Anchors[0].Id }
                    : anchor).ToArray(),
            },
            "duplicate-hook" => document with
            {
                OpaqueDelegates = document.OpaqueDelegates.Select((item, index) => index == 1
                    ? item with { Id = document.OpaqueDelegates[0].Id }
                    : item).ToArray(),
            },
            "redirect-hook" => document with
            {
                OpaqueDelegates = document.OpaqueDelegates.Select((item, index) => index == 1
                    ? item with { AnchorId = document.OpaqueDelegates[0].AnchorId }
                    : item).ToArray(),
            },
            "redirect-guard-binding" => document with
            {
                Guards = document.Guards.Select(guard => guard.Id == "background-receipts"
                    ? guard with { Binding = document.Anchors.Single(anchor => anchor.Id == "block.sync-blooms").Binding }
                    : guard).ToArray(),
            },
            "redirect-step-binding" => document with
            {
                Steps = document.Steps.Select(step => step.Id == "rewards"
                    ? step with { Binding = document.Anchors.Single(anchor => anchor.Id == "block.withdrawals").Binding }
                    : step).ToArray(),
            },
            "malformed-edge" => document with
            {
                ControlFlows = document.ControlFlows.Select(flow => flow.Id == "block.processBlock"
                    ? flow with { Edges = ["not-an-edge"] }
                    : flow).ToArray(),
            },
            "duplicate-edge" => document with
            {
                ControlFlows = document.ControlFlows.Select(flow => flow.Id == "block.processBlock"
                    ? flow with { Edges = ["0->1", "0->1"] }
                    : flow).ToArray(),
            },
            "missing-edge" => document with
            {
                ControlFlows = document.ControlFlows.Select(flow => flow.Id == "block.processBlock"
                    ? flow with { Edges = flow.Edges.Skip(1).ToArray() }
                    : flow).ToArray(),
            },
            "disconnected-block" => document with
            {
                ControlFlows = document.ControlFlows.Select(flow => flow.Id == "block.processBlock"
                    ? flow with
                    {
                        Edges = flow.Edges.Select(edge => edge == "0->1" ? "0->2" : edge).ToArray(),
                    }
                    : flow).ToArray(),
            },
            "duplicate-flow" => document with
            {
                ControlFlows = document.ControlFlows.Select((flow, index) => index == 1
                    ? flow with { Id = document.ControlFlows[0].Id }
                    : flow).ToArray(),
            },
            "unreachable-edge" => document with
            {
                ControlFlows = document.ControlFlows.Select(flow => flow.Id == "block.processBlock"
                    ? flow with { Edges = ["0->999"] }
                    : flow).ToArray(),
            },
            "unsorted-edge" => document with
            {
                ControlFlows = document.ControlFlows.Select(flow => flow.Id == "block.processBlock"
                    ? flow with { Edges = ["1->2", "0->1"] }
                    : flow).ToArray(),
            },
            "dominance-edge" => AddDominanceBypass(document),
            "redirect-back-edge" => document with
            {
                ControlFlows = document.ControlFlows.Select(flow => flow.Id == "block.processBlock"
                    ? flow with { BackEdges = ["0->1"] }
                    : flow).ToArray(),
            },
            "redirect-normal-exit" => document with
            {
                ControlFlows = document.ControlFlows.Select(flow => flow.Id == "block.processBlock"
                    ? flow with { NormalExitBlocks = [999] }
                    : flow).ToArray(),
            },
            "missing-handler-entry" or "redirect-handler-entry" or "duplicate-handler-entry" => document with
            {
                ControlFlows = document.ControlFlows.Select(flow => flow.Id == "block.processBlock"
                    ? flow with
                    {
                        ExceptionHandlerEntries = mutation switch
                        {
                            "missing-handler-entry" => [],
                            "redirect-handler-entry" => [999],
                            _ => [flow.ExceptionHandlerEntries[0], flow.ExceptionHandlerEntries[0]],
                        },
                    }
                    : flow).ToArray(),
            },
            "binding-unreachable" => document with
            {
                Anchors = document.Anchors.Select(anchor => anchor.Id == "block.end-block-trace"
                    ? anchor with { Binding = anchor.Binding with { ControlFlowBlock = 999 } }
                    : anchor).ToArray(),
            },
            "binding-redirect-reachable" => document with
            {
                Anchors = document.Anchors.Select(anchor => anchor.Id == "block.end-block-trace"
                    ? anchor with
                    {
                        Binding = anchor.Binding with
                        {
                            ControlFlowBlock = document.ControlFlows.Single(flow => flow.Id == "block.processBlock")
                                .ReachableBlocks[0],
                        },
                    }
                    : anchor).ToArray(),
            },
            "duplicate-membership" => DuplicateMembership(document),
            "graph-shape-digest" => document with
            {
                ControlFlows = document.ControlFlows.Select(flow => flow.Id == "block.processBlock"
                    ? flow with { ShapeSha256 = new string('0', 64) }
                    : flow).ToArray(),
            },
            "commit-event" => document with
            {
                Commits = document.Commits.Select(commit => commit.Id == "storage-roots"
                    ? commit with { Event = "commitNoRoots(1)" }
                    : commit).ToArray(),
            },
            "commit-anchor" => document with
            {
                Commits = document.Commits.Select(commit => commit.Id == "storage-roots"
                    ? commit with { InvocationAnchorId = "block.finalization-commit" }
                    : commit).ToArray(),
            },
            "commit-method" => document with
            {
                Commits = document.Commits.Select(commit => commit.Id == "finalization-no-roots"
                    ? commit with { MethodId = "block.commit-roots" }
                    : commit).ToArray(),
            },
            "commit-invocation-binding" => document with
            {
                Commits = document.Commits.Select(commit => commit.Id == "finalization-no-roots"
                    ? commit with
                    {
                        InvocationBinding = document.Anchors.Single(anchor => anchor.Id == "block.storage-roots-commit")
                            .Binding,
                    }
                    : commit).ToArray(),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        Assert.That(() => Extractor.ValidateIrForTest(altered), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Commit_flag_mutation_fails_closed()
    {
        IrDocument document = ReadCheckedIr();
        IrDocument altered = document with
        {
            Commits = document.Commits.Select(commit => commit.Id == "storage-roots"
                ? commit with { CommitRoots = false }
                : commit).ToArray(),
        };
        Assert.That(() => Extractor.ValidateIrForTest(altered), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Guard_polarity_mutation_fails_closed()
    {
        IrDocument document = ReadCheckedIr();
        IrDocument altered = document with
        {
            Guards = document.Guards.Select(guard => guard.Id == "background-receipts"
                ? guard with { ExpectedPolarity = "true-arm" }
                : guard).ToArray(),
        };
        Assert.That(() => Extractor.ValidateIrForTest(altered), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Step_reorder_mutation_fails_closed()
    {
        IrDocument document = ReadCheckedIr();
        StepIdentity first = document.Steps[5];
        StepIdentity second = document.Steps[6];
        IrDocument altered = document with
        {
            Steps = document.Steps.Select(step => step.Id == first.Id ? second : step.Id == second.Id ? first : step).ToArray(),
        };
        Assert.That(() => Extractor.ValidateIrForTest(altered), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Bridge_result_equality_mutation_fails_closed()
    {
        IrDocument document = ReadCheckedIr();
        IrDocument altered = document with
        {
            Bridge = document.Bridge with { Relation = "result = generated result" },
        };
        Assert.That(() => Extractor.ValidateIrForTest(altered), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void Result_observable_mutation_fails_closed()
    {
        IrDocument document = ReadCheckedIr();
        IrDocument altered = document with
        {
            Steps = document.Steps.Select(step => step.Id == "header-hash"
                ? step with { Observable = string.Empty }
                : step).ToArray(),
        };
        Assert.That(() => Extractor.ValidateIrForTest(altered), Throws.TypeOf<ExtractionException>());
    }

    [TestCase("claim")]
    [TestCase("transactions-executed-normal-return")]
    [TestCase("post-transaction-commit-normal-return")]
    [TestCase("transactions-executed-event")]
    [TestCase("transactions-executed-symbol")]
    [TestCase("transactions-executed-binding")]
    [TestCase("post-transaction-commit-binding")]
    public void Source_entry_premise_mutations_fail_closed(string mutation)
    {
        IrDocument document = ReadCheckedIr();
        SourceEntryAdapterIdentity adapter = document.SourceEntryAdapters.Single();
        TypedBinding transactionsExecutedBinding = adapter.TransactionsExecutedBinding;
        TypedBinding postTransactionCommitBinding = adapter.PostTransactionCommitBinding;
        IrDocument altered = document with
        {
            SourceEntryAdapters = [mutation switch
            {
                "claim" => adapter with { Claim = "executable runtime dispatch proof" },
                "transactions-executed-normal-return" => adapter with
                {
                    TransactionsExecutedNormalReturnPremise = "unconditional callback return"
                },
                "post-transaction-commit-normal-return" => adapter with
                {
                    PostTransactionCommitNormalReturnPremise = "unconditional commit return"
                },
                "transactions-executed-event" => adapter with
                {
                    TransactionsExecutedEventSymbol = "global::System.Action.Invoke"
                },
                "transactions-executed-symbol" => adapter with
                {
                    TransactionsExecutedBinding = transactionsExecutedBinding with
                    {
                        SymbolId = "global::System.Action.Invoke(System.Object)"
                    }
                },
                "transactions-executed-binding" => adapter with
                {
                    TransactionsExecutedBinding = transactionsExecutedBinding with
                    {
                        CanonicalSyntax = "TransactionsExecuted.Invoke()"
                    }
                },
                "post-transaction-commit-binding" => adapter with
                {
                    PostTransactionCommitBinding = postTransactionCommitBinding with
                    {
                        CanonicalSyntax = "CommitState(otherSpec)"
                    }
                },
                _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
            }],
        };
        Assert.That(() => Extractor.ValidateIrForTest(altered), Throws.TypeOf<ExtractionException>());
    }

    [TestCase("null-flag")]
    [TestCase("local-symbol")]
    [TestCase("background-predicate")]
    [TestCase("finally-predicate")]
    public void Null_task_dataflow_mutations_fail_closed(string mutation)
    {
        IrDocument document = ReadCheckedIr();
        IrDocument altered = document with
        {
            TaskFlow = mutation switch
            {
                "null-flag" => document.TaskFlow with { NullPreservedInSynchronousArm = false },
                "local-symbol" => document.TaskFlow with { LocalSymbol = "receipts" },
                "background-predicate" => document.TaskFlow with { BackgroundResultPredicate = "task != null" },
                "finally-predicate" => document.TaskFlow with { FinallyPredicate = "task.IsCompletedSuccessfully" },
                _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
            },
        };
        Assert.That(() => Extractor.ValidateIrForTest(altered), Throws.TypeOf<ExtractionException>());
    }

    [TestCase("delete-commit")]
    [TestCase("duplicate-commit")]
    [TestCase("reorder-commit")]
    [TestCase("commit-root-flags")]
    [TestCase("commit-roots-disabled")]
    [TestCase("invert-eip4844")]
    [TestCase("swap-blob-arguments")]
    [TestCase("invert-background")]
    [TestCase("invert-main-thread")]
    [TestCase("invert-state-root")]
    [TestCase("wrong-receipt-root-input")]
    [TestCase("swap-end-trace-argument")]
    [TestCase("task-reassignment")]
    [TestCase("bal-after-hash")]
    [TestCase("early-return")]
    [TestCase("using-alias")]
    [TestCase("ref-alias")]
    [TestCase("ref-out")]
    [TestCase("task-capture")]
    [TestCase("local-function-capture")]
    [TestCase("indirect-task-use")]
    [TestCase("task-helper-assignment")]
             [TestCase("callback-conditional")]
             [TestCase("callback-lambda")]
             [TestCase("callback-duplicate")]
             [TestCase("callback-try-finally")]
             [TestCase("opaque-bound-helper")]
             [TestCase("opaque-helper-write")]
             [TestCase("opaque-local-function")]
             [TestCase("opaque-task-run")]
             [TestCase("opaque-callback")]
    [TestCase("receipts-ignored-result")]
    [TestCase("receipts-reassigned-result")]
    [TestCase("receipts-capture")]
    [TestCase("receipts-local-reassignment")]
    [TestCase("competing-blob-write")]
    [TestCase("competing-root-write")]
     [TestCase("competing-background-root-write")]
     [TestCase("duplicate-roots-post-finally")]
     [TestCase("direct-commit")]
      [TestCase("direct-commit-post-finally")]
      [TestCase("helper-equivalent-commit")]
      [TestCase("flush-state")]
      [TestCase("rewrite-header")]
      [TestCase("nested-flush-state")]
      [TestCase("nested-rewrite-header")]
      [TestCase("delegate-header-write")]
      [TestCase("property-accessor-header-write")]
      [TestCase("constructor-header-write")]
      [TestCase("interface-dispatch-header-write")]
      [TestCase("operator-header-write")]
      [TestCase("conversion-header-write")]
      [TestCase("dynamic-member-sentinel")]
      [TestCase("lazy-initializer-header-write")]
      [TestCase("external-interface-getter-header-write")]
      [TestCase("event-accessor-header-write")]
      [TestCase("idisposable-disposal-header-write")]
      [TestCase("metrics-timer-sink-header-write")]
      [TestCase("reflection-reentry-header-write")]
      [TestCase("duplicate-account-false-arm")]
     [TestCase("duplicate-state-root-false-arm")]
     [TestCase("direct-state-root-write")]
     [TestCase("direct-account-changes-write")]
     [TestCase("deconstruction-state-root-write")]
     [TestCase("deconstruction-account-changes-write")]
     [TestCase("compound-state-root-write")]
     [TestCase("compound-account-changes-write")]
    [TestCase("local-state-root-write")]
    [TestCase("local-account-changes-write")]
     [TestCase("duplicate-state-root-helper-write")]
     [TestCase("duplicate-account-helper-write")]
     [TestCase("redirect-state-root-helper-value")]
     [TestCase("redirect-account-helper-value")]
     [TestCase("compound-state-root-helper-write")]
     [TestCase("compound-account-helper-write")]
     [TestCase("later-header-overwrite")]
     [TestCase("later-hash-overwrite")]
    [TestCase("background-result-predicate")]
    [TestCase("finally-predicate")]
    [TestCase("wrap-post-transaction-commit")]
    [TestCase("wrap-task-initializer")]
    [TestCase("wrap-rewards")]
    [TestCase("wrap-withdrawals")]
    [TestCase("wrap-finalization-commit")]
    [TestCase("wrap-execution-requests")]
    [TestCase("wrap-end-trace")]
    [TestCase("wrap-storage-roots")]
    [TestCase("wrap-bal")]
    [TestCase("wrap-hash")]
    [TestCase("wrap-return")]
    public void Source_mutations_fail_closed(string mutation)
    {
        string root = FindRepoRoot();
        string path = Path.Combine(root, Extractor.BlockProcessorPath);
        byte[] originalBytes = File.ReadAllBytes(path);
        string source = Encoding.UTF8.GetString(originalBytes);
        string mutated = mutation switch
        {
            "delete-commit" => ReplaceOnce(source, "CommitState(spec);", "", 2),
            "duplicate-commit" => ReplaceOnce(source, "CommitState(spec);", "CommitState(spec); CommitState(spec);", 2),
            "reorder-commit" => ReplaceOnce(source, "CommitStateAndStorageRoots(spec);", "CommitState(spec);", 1),
            "commit-root-flags" => source.Replace("commitRoots: false", "commitRoots: true", StringComparison.Ordinal),
            "commit-roots-disabled" => source.Replace("commitRoots: true", "commitRoots: false", StringComparison.Ordinal),
            "invert-eip4844" => source.Replace("if (spec.IsEip4844Enabled)", "if (!spec.IsEip4844Enabled)", StringComparison.Ordinal),
            "swap-blob-arguments" => source.Replace("CalculateBlobGas(block.Transactions)", "CalculateBlobGas(block.Body.Transactions)", StringComparison.Ordinal),
            "invert-background" => source.Replace("if (ShouldCalculateReceiptsInBackground(receipts))", "if (!ShouldCalculateReceiptsInBackground(receipts))", StringComparison.Ordinal),
            "invert-main-thread" => source.Replace("if (BlockchainProcessor.IsMainProcessingThread)", "if (!BlockchainProcessor.IsMainProcessingThread)", StringComparison.Ordinal),
            "invert-state-root" => source.Replace("if (ShouldComputeStateRoot(header))", "if (!ShouldComputeStateRoot(header))", StringComparison.Ordinal),
            "wrong-receipt-root-input" => source.Replace("CalculateReceiptsRoot(receipts, spec, block)", "CalculateReceiptsRoot(Array.Empty<TxReceipt>(), spec, block)", StringComparison.Ordinal),
            "swap-end-trace-argument" => source.Replace("accumulateBlockBloom: bloomsAndReceiptsRootTask is null", "accumulateBlockBloom: false", StringComparison.Ordinal),
            "task-reassignment" => AddTaskReassignment(source),
            "bal-after-hash" => MoveBalAfterHash(source),
            "early-return" => source.Replace("header.Hash = header.CalculateHash();", "return receipts;\n\n        header.Hash = header.CalculateHash();", StringComparison.Ordinal),
            "using-alias" => "using Alias = System.String;\n" + source,
            "ref-alias" => AddAfterTaskInitializer(source,
                "        ref var taskAlias = ref bloomsAndReceiptsRootTask;"),
            "ref-out" => AddAfterTaskInitializer(source,
                "        ref var taskRefAlias = ref bloomsAndReceiptsRootTask;"),
            "task-capture" => source.Replace(
                "bloomsAndReceiptsRootTask = Task.Run(() =>\n            {",
                "bloomsAndReceiptsRootTask = Task.Run(() =>\n            {\n                _ = bloomsAndReceiptsRootTask;",
                StringComparison.Ordinal),
            "local-function-capture" => AddAfterTaskInitializer(source,
                "        void ObserveTask() => GC.KeepAlive(bloomsAndReceiptsRootTask);"),
            "indirect-task-use" => AddAfterTaskInitializer(source,
                "        GC.KeepAlive(bloomsAndReceiptsRootTask);"),
            "task-helper-assignment" => source.Replace("bloomsAndReceiptsRootTask = Task.Run(() =>",
                "bloomsAndReceiptsRootTask = Task.Factory.StartNew(() =>", StringComparison.Ordinal),
            "callback-conditional" => source.Replace(
                "        TransactionsExecuted?.Invoke();",
                "        if (token.CanBeCanceled)\n        {\n            TransactionsExecuted?.Invoke();\n        }",
                StringComparison.Ordinal),
            "callback-lambda" => source.Replace(
                "        TransactionsExecuted?.Invoke();",
                "        Task.Run(() => TransactionsExecuted?.Invoke());",
                StringComparison.Ordinal),
            "callback-duplicate" => source.Replace(
                "        TransactionsExecuted?.Invoke();",
                "        TransactionsExecuted?.Invoke();\n        TransactionsExecuted?.Invoke();",
                StringComparison.Ordinal),
            "callback-try-finally" => source.Replace(
                "        TransactionsExecuted?.Invoke();",
                "        try\n        {\n            TransactionsExecuted?.Invoke();\n        }\n        finally\n        {\n        }",
                StringComparison.Ordinal),
            "opaque-bound-helper" => source.Replace(
                "        if (_logger.IsTrace) _logger.Trace(\"Applying miner rewards:\");",
                "        SetAccountChanges(block);\n        if (_logger.IsTrace) _logger.Trace(\"Applying miner rewards:\");",
                StringComparison.Ordinal),
            "opaque-helper-write" => source.Replace(
                "        if (_logger.IsTrace) _logger.Trace(\"Applying miner rewards:\");",
                "        block.Header.BlobGasUsed = 0UL;\n        if (_logger.IsTrace) _logger.Trace(\"Applying miner rewards:\");",
                StringComparison.Ordinal),
            "opaque-local-function" => source.Replace(
                "        if (_logger.IsTrace) _logger.Trace(\"Applying miner rewards:\");",
                "        void ObserveOpaque(Block ignored) { }\n        if (_logger.IsTrace) _logger.Trace(\"Applying miner rewards:\");",
                StringComparison.Ordinal),
            "opaque-task-run" => source.Replace(
                "        if (_logger.IsTrace) _logger.Trace(\"Applying miner rewards:\");",
                "        _ = Task.Run(() => { });\n        if (_logger.IsTrace) _logger.Trace(\"Applying miner rewards:\");",
                StringComparison.Ordinal),
            "opaque-callback" => source.Replace(
                "        if (_logger.IsTrace) _logger.Trace(\"Applying miner rewards:\");",
                "        TransactionsExecuted?.Invoke();\n        if (_logger.IsTrace) _logger.Trace(\"Applying miner rewards:\");",
                StringComparison.Ordinal),
            "receipts-ignored-result" => source.Replace(
                "TxReceipt[] receipts = _blockTransactionsExecutor.ProcessTransactions(block, options, ReceiptsTracer, token);",
                "TxReceipt[] ignoredReceipts = _blockTransactionsExecutor.ProcessTransactions(block, options, ReceiptsTracer, token);\n        TxReceipt[] receipts = Array.Empty<TxReceipt>();",
                StringComparison.Ordinal),
            "receipts-reassigned-result" => source.Replace(
                "TxReceipt[] receipts = _blockTransactionsExecutor.ProcessTransactions(block, options, ReceiptsTracer, token);",
                "TxReceipt[] receipts = _blockTransactionsExecutor.ProcessTransactions(block, options, ReceiptsTracer, token);\n\n        receipts = receipts;",
                StringComparison.Ordinal),
            "receipts-capture" => source.Replace(
                "bloomsAndReceiptsRootTask = Task.Run(() =>\n            {",
                "bloomsAndReceiptsRootTask = Task.Run(() =>\n            {\n                _ = receipts;",
                StringComparison.Ordinal),
            "receipts-local-reassignment" => source.Replace(
                "_systemContractHandler.ProcessExecutionRequests(block, _stateProvider, receipts, spec);",
                "TxReceipt[] localReceipts = receipts;\n            receipts = localReceipts;\n\n            _systemContractHandler.ProcessExecutionRequests(block, _stateProvider, receipts, spec);",
                StringComparison.Ordinal),
            "competing-blob-write" => source.Replace(
                "header.BlobGasUsed = BlobGasCalculator.CalculateBlobGas(block.Transactions);",
                "header.BlobGasUsed = 0UL;\n            header.BlobGasUsed = BlobGasCalculator.CalculateBlobGas(block.Transactions);",
                StringComparison.Ordinal),
            "competing-root-write" => source.Replace(
                "header.ReceiptsRoot = CalculateReceiptsRoot(receipts, spec, block);",
                "header.ReceiptsRoot = header.ReceiptsRoot;\n            header.ReceiptsRoot = CalculateReceiptsRoot(receipts, spec, block);",
                StringComparison.Ordinal),
            "competing-background-root-write" => source.Replace(
                "(header.Bloom, header.ReceiptsRoot) = bloomsAndReceiptsRootTask.GetAwaiter().GetResult();",
                "(header.Bloom, header.ReceiptsRoot) = bloomsAndReceiptsRootTask.GetAwaiter().GetResult();\n                (header.Bloom, header.ReceiptsRoot) = bloomsAndReceiptsRootTask.GetAwaiter().GetResult();",
                StringComparison.Ordinal),
            "duplicate-roots-post-finally" => source.Replace(
                "        header.Hash = header.CalculateHash();",
                "        CommitStateAndStorageRoots(spec);\n\n        header.Hash = header.CalculateHash();",
                StringComparison.Ordinal),
            "direct-commit" => source.Replace(
                "        CommitState(spec);\n\n        if (spec.IsEip4844Enabled)",
                "        CommitState(spec);\n        _stateProvider.Commit(spec, commitRoots: false);\n\n        if (spec.IsEip4844Enabled)",
                StringComparison.Ordinal),
             "helper-equivalent-commit" => AddHelperEquivalentCommit(source),
             "flush-state" => AddFlushState(source),
             "rewrite-header" => AddRewriteHeader(source),
             "nested-flush-state" => AddNestedFlushState(source),
             "nested-rewrite-header" => AddNestedRewriteHeader(source),
             "delegate-header-write" => AddDelegateHeaderWrite(source),
             "property-accessor-header-write" => AddPropertyAccessorHeaderWrite(source),
             "constructor-header-write" => AddConstructorHeaderWrite(source),
             "interface-dispatch-header-write" => AddInterfaceDispatchHeaderWrite(source),
             "operator-header-write" => AddOperatorHeaderWrite(source),
             "conversion-header-write" => AddConversionHeaderWrite(source),
             "dynamic-member-sentinel" => AddDynamicMemberSentinel(source),
             "lazy-initializer-header-write" => AddLazyInitializerHeaderWrite(source),
             "external-interface-getter-header-write" => AddExternalInterfaceGetterHeaderWrite(source),
             "event-accessor-header-write" => AddEventAccessorHeaderWrite(source),
             "idisposable-disposal-header-write" => AddDisposableHeaderWrite(source),
             "metrics-timer-sink-header-write" => AddMetricsTimerSinkHeaderWrite(source),
             "reflection-reentry-header-write" => AddReflectionReentryHeaderWrite(source),
             "direct-commit-post-finally" => source.Replace(
                 "        header.Hash = header.CalculateHash();",
                 "        header.Hash = header.CalculateHash();\n        _stateProvider.Commit(spec, commitRoots: false);",
                 StringComparison.Ordinal),
             "duplicate-account-false-arm" => source.Replace(
                "            if (ShouldComputeStateRoot(header))",
                "            if (!BlockchainProcessor.IsMainProcessingThread)\n            {\n                SetAccountChanges(block);\n            }\n\n            if (ShouldComputeStateRoot(header))",
                StringComparison.Ordinal),
            "duplicate-state-root-false-arm" => source.Replace(
                "            _balManager.SetBlockAccessList(block);",
                "            if (!ShouldComputeStateRoot(header))\n            {\n                ComputeStateRoot(header);\n            }\n\n            _balManager.SetBlockAccessList(block);",
                StringComparison.Ordinal),
            "direct-state-root-write" => source.Replace(
                "            _balManager.SetBlockAccessList(block);",
                "            header.StateRoot = _stateProvider.StateRoot;\n\n            _balManager.SetBlockAccessList(block);",
                StringComparison.Ordinal),
            "direct-account-changes-write" => source.Replace(
                "            _balManager.SetBlockAccessList(block);",
                "            block.AccountChanges = _stateProvider.GetAccountChanges();\n\n            _balManager.SetBlockAccessList(block);",
                StringComparison.Ordinal),
            "deconstruction-state-root-write" => source.Replace(
                "            _balManager.SetBlockAccessList(block);",
                "            (header.StateRoot, header.ReceiptsRoot) = (header.StateRoot, header.ReceiptsRoot);\n\n            _balManager.SetBlockAccessList(block);",
                StringComparison.Ordinal),
            "deconstruction-account-changes-write" => source.Replace(
                "            _balManager.SetBlockAccessList(block);",
                "            (block.AccountChanges, block.AccountChanges) = (block.AccountChanges, block.AccountChanges);\n\n            _balManager.SetBlockAccessList(block);",
                StringComparison.Ordinal),
            "compound-state-root-write" => source.Replace(
                "            _balManager.SetBlockAccessList(block);",
                "            header.StateRoot ??= _stateProvider.StateRoot;\n\n            _balManager.SetBlockAccessList(block);",
                StringComparison.Ordinal),
            "compound-account-changes-write" => source.Replace(
                "            _balManager.SetBlockAccessList(block);",
                "            block.AccountChanges ??= _stateProvider.GetAccountChanges();\n\n            _balManager.SetBlockAccessList(block);",
                StringComparison.Ordinal),
            "local-state-root-write" => source.Replace(
                "            _balManager.SetBlockAccessList(block);",
                "            BlockHeader localHeader = header;\n            localHeader.StateRoot = _stateProvider.StateRoot;\n\n            _balManager.SetBlockAccessList(block);",
                StringComparison.Ordinal),
            "local-account-changes-write" => source.Replace(
                "            _balManager.SetBlockAccessList(block);",
                "            Block localBlock = block;\n            localBlock.AccountChanges = _stateProvider.GetAccountChanges();\n\n            _balManager.SetBlockAccessList(block);",
                StringComparison.Ordinal),
            "duplicate-state-root-helper-write" => source.Replace(
                "        header.StateRoot = _stateProvider.StateRoot;",
                "        header.StateRoot = _stateProvider.StateRoot;\n        header.StateRoot = _stateProvider.StateRoot;",
                StringComparison.Ordinal),
            "duplicate-account-helper-write" => source.Replace(
                "    private void SetAccountChanges(Block block)\n        => block.AccountChanges = _stateProvider.GetAccountChanges();",
                "    private void SetAccountChanges(Block block)\n    {\n        block.AccountChanges = _stateProvider.GetAccountChanges();\n        block.AccountChanges = _stateProvider.GetAccountChanges();\n    }",
                StringComparison.Ordinal),
            "redirect-state-root-helper-value" => source.Replace(
                "        header.StateRoot = _stateProvider.StateRoot;",
                "        header.StateRoot = header.StateRoot;",
                StringComparison.Ordinal),
            "redirect-account-helper-value" => source.Replace(
                "    private void SetAccountChanges(Block block)\n        => block.AccountChanges = _stateProvider.GetAccountChanges();",
                "    private void SetAccountChanges(Block block)\n        => block.AccountChanges = null;",
                StringComparison.Ordinal),
            "compound-state-root-helper-write" => source.Replace(
                "        header.StateRoot = _stateProvider.StateRoot;",
                "        header.StateRoot ??= _stateProvider.StateRoot;",
                StringComparison.Ordinal),
            "compound-account-helper-write" => source.Replace(
                "    private void SetAccountChanges(Block block)\n        => block.AccountChanges = _stateProvider.GetAccountChanges();",
                "    private void SetAccountChanges(Block block)\n        => block.AccountChanges ??= _stateProvider.GetAccountChanges();",
                StringComparison.Ordinal),
            "later-header-overwrite" => source.Replace(
                "            _balManager.SetBlockAccessList(block);",
                "            _balManager.SetBlockAccessList(block);\n\n            header.ReceiptsRoot = CalculateReceiptsRoot(receipts, spec, block);",
                StringComparison.Ordinal),
            "later-hash-overwrite" => source.Replace(
                "        header.Hash = header.CalculateHash();",
                "        header.Hash = header.CalculateHash();\n        header.Hash = header.CalculateHash();",
                StringComparison.Ordinal),
            "background-result-predicate" => source.Replace(
                "if (bloomsAndReceiptsRootTask is not null)", "if (bloomsAndReceiptsRootTask != null)",
                StringComparison.Ordinal),
            "finally-predicate" => source.Replace(
                "bloomsAndReceiptsRootTask is { IsCompletedSuccessfully: false }",
                "bloomsAndReceiptsRootTask is { IsCompletedSuccessfully: true }", StringComparison.Ordinal),
            "wrap-post-transaction-commit" => WrapStatement(source,
                "        CommitState(spec);", 2),
            "wrap-task-initializer" => WrapTaskInitializer(source),
            "wrap-rewards" => WrapStatement(source,
                "            ApplyMinerRewards(block, blockTracer, spec);"),
            "wrap-withdrawals" => WrapStatement(source,
                "            _systemContractHandler.ProcessWithdrawals(block, spec);"),
            "wrap-finalization-commit" => WrapStatement(source,
                "            CommitState(spec);"),
            "wrap-execution-requests" => WrapStatement(source,
                "            _systemContractHandler.ProcessExecutionRequests(block, _stateProvider, receipts, spec);"),
            "wrap-end-trace" => WrapStatement(source,
                "            ReceiptsTracer.EndBlockTrace(accumulateBlockBloom: bloomsAndReceiptsRootTask is null);"),
            "wrap-storage-roots" => WrapStatement(source,
                "            CommitStateAndStorageRoots(spec);"),
            "wrap-bal" => WrapStatement(source,
                "            _balManager.SetBlockAccessList(block);"),
            "wrap-hash" => WrapStatement(source,
                "        header.Hash = header.CalculateHash();"),
            "wrap-return" => WrapStatement(source,
                "        return receipts;"),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        Assert.That(mutated, Is.Not.EqualTo(source));

        Dictionary<string, byte[]> overrides = new(StringComparer.Ordinal)
        {
            [Extractor.BlockProcessorPath] = Encoding.UTF8.GetBytes(mutated),
        };
        Extractor.RequireCompilationForTest(root, overrides);
        using TemporaryDirectory output = new();
        Assert.That(() => Extractor.ExtractForTest(root, output.Path, overrides),
            Throws.TypeOf<ExtractionException>().With.Message.Contains(ExpectedSourceDiagnostic(mutation)));
        Assert.That(Directory.EnumerateFileSystemEntries(output.Path), Is.Empty);
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(originalBytes));
    }

    private static string AddHelperEquivalentCommit(string source)
    {
        string withHelper = source.Replace(
            "    private void CommitState(IReleaseSpec spec)",
            "    private void CommitStateEquivalent(IReleaseSpec spec)\n        => _stateProvider.Commit(spec, commitRoots: false);\n\n    private void CommitState(IReleaseSpec spec)",
            StringComparison.Ordinal);
        return withHelper.Replace(
            "        CommitState(spec);\n\n        if (spec.IsEip4844Enabled)",
            "        CommitState(spec);\n        CommitStateEquivalent(spec);\n\n        if (spec.IsEip4844Enabled)",
            StringComparison.Ordinal);
    }

    private static string AddFlushState(string source)
    {
        string withHelper = source.Replace(
            "    private void CommitState(IReleaseSpec spec)",
            "    private void FlushState(IReleaseSpec spec)\n        => _stateProvider.Commit(spec, commitRoots: false);\n\n    private void CommitState(IReleaseSpec spec)",
            StringComparison.Ordinal);
        return withHelper.Replace(
            "        CommitState(spec);\n\n        if (spec.IsEip4844Enabled)",
            "        CommitState(spec);\n        FlushState(spec);\n\n        if (spec.IsEip4844Enabled)",
            StringComparison.Ordinal);
    }

    private static string AddRewriteHeader(string source)
    {
        string withCall = source.Replace(
            "        header.Hash = header.CalculateHash();",
            "        RewriteHeader(header);\n\n        header.Hash = header.CalculateHash();",
            StringComparison.Ordinal);
        return withCall.Replace(
            "    private void CommitState(IReleaseSpec spec)",
            "    private static void RewriteHeader(BlockHeader header)\n        => header.Hash = header.CalculateHash();\n\n    private void CommitState(IReleaseSpec spec)",
            StringComparison.Ordinal);
    }

    private static string AddNestedFlushState(string source)
    {
        string withHelper = source.Replace(
            "    private void CommitState(IReleaseSpec spec)",
            "    private static class NestedCommitHelper\n    {\n        internal static void FlushState(IWorldState stateProvider, IReleaseSpec spec)\n            => stateProvider.Commit(spec, commitRoots: false);\n    }\n\n    private void CommitState(IReleaseSpec spec)",
            StringComparison.Ordinal);
        return withHelper.Replace(
            "        CommitState(spec);\n\n        if (spec.IsEip4844Enabled)",
            "        CommitState(spec);\n        NestedCommitHelper.FlushState(_stateProvider, spec);\n\n        if (spec.IsEip4844Enabled)",
            StringComparison.Ordinal);
    }

    private static string AddNestedRewriteHeader(string source)
    {
        string withCall = source.Replace(
            "        header.Hash = header.CalculateHash();",
            "        NestedHeaderHelper.RewriteHeader(header);\n\n        header.Hash = header.CalculateHash();",
            StringComparison.Ordinal);
        return withCall.Replace(
            "    private void CommitState(IReleaseSpec spec)",
            "    private static class NestedHeaderHelper\n    {\n        internal static void RewriteHeader(BlockHeader header)\n            => header.Hash = header.CalculateHash();\n    }\n\n    private void CommitState(IReleaseSpec spec)",
            StringComparison.Ordinal);
    }

    private static string AddDelegateHeaderWrite(string source)
    {
        string withCall = source.Replace(
            "        header.Hash = header.CalculateHash();\n\n        return receipts;",
            "        header.Hash = header.CalculateHash();\n        RewriteAfterHash(header);\n\n        return receipts;",
            StringComparison.Ordinal);
        return withCall.Replace(
            "    private void CommitState(IReleaseSpec spec)",
            "    private static readonly Action<BlockHeader> RewriteAfterHash =\n        header => header.Hash = header.CalculateHash();\n\n    private void CommitState(IReleaseSpec spec)",
            StringComparison.Ordinal);
    }

    private static string AddPropertyAccessorHeaderWrite(string source)
    {
        string withCall = source.Replace(
            "        header.Hash = header.CalculateHash();\n\n        return receipts;",
            "        _headerForAccessorMutation = header;\n        header.Hash = header.CalculateHash();\n        _ = RewriteAfterHash;\n\n        return receipts;",
            StringComparison.Ordinal);
        return withCall.Replace(
            "    private void CommitState(IReleaseSpec spec)",
            "    private BlockHeader _headerForAccessorMutation = null!;\n\n    private BlockHeader RewriteAfterHash\n    {\n        get\n        {\n            _headerForAccessorMutation.Hash = _headerForAccessorMutation.CalculateHash();\n            return _headerForAccessorMutation;\n        }\n    }\n\n    private void CommitState(IReleaseSpec spec)",
            StringComparison.Ordinal);
    }

    private static string AddConstructorHeaderWrite(string source)
    {
        string withCall = source.Replace(
            "        header.Hash = header.CalculateHash();\n\n        return receipts;",
            "        header.Hash = header.CalculateHash();\n        new HeaderConstructor(header);\n\n        return receipts;",
            StringComparison.Ordinal);
        return withCall.Replace(
            "    private void CommitState(IReleaseSpec spec)",
            "    private sealed class HeaderConstructor\n    {\n        public HeaderConstructor(BlockHeader header)\n        {\n            header.Hash = header.CalculateHash();\n        }\n    }\n\n    private void CommitState(IReleaseSpec spec)",
            StringComparison.Ordinal);
    }

    private static string AddInterfaceDispatchHeaderWrite(string source)
    {
        string withCall = source.Replace(
            "        header.Hash = header.CalculateHash();\n\n        return receipts;",
            "        header.Hash = header.CalculateHash();\n        _headerMutator.Rewrite(header);\n\n        return receipts;",
            StringComparison.Ordinal);
        return withCall.Replace(
            "    private void CommitState(IReleaseSpec spec)",
            "    private interface IHeaderMutator\n    {\n        void Rewrite(BlockHeader header);\n    }\n\n    private sealed class HeaderMutator : IHeaderMutator\n    {\n        public void Rewrite(BlockHeader header)\n            => header.Hash = header.CalculateHash();\n    }\n\n    private readonly IHeaderMutator _headerMutator = null!;\n\n    private void CommitState(IReleaseSpec spec)",
            StringComparison.Ordinal);
    }

    private static string AddOperatorHeaderWrite(string source)
    {
        string withCall = source.Replace(
            "        header.Hash = header.CalculateHash();\n\n        return receipts;",
            "        header.Hash = header.CalculateHash();\n        HeaderOperator.Header = header;\n        _ = HeaderOperatorValue + HeaderOperatorValue;\n\n        return receipts;",
            StringComparison.Ordinal);
        return withCall.Replace(
            "    private void CommitState(IReleaseSpec spec)",
            "    private sealed class HeaderOperator\n    {\n        internal static BlockHeader Header = null!;\n\n        public static HeaderOperator operator +(HeaderOperator left, HeaderOperator right)\n        {\n            Header.Hash = Header.CalculateHash();\n            return left;\n        }\n    }\n\n    private static readonly HeaderOperator HeaderOperatorValue = null!;\n\n    private void CommitState(IReleaseSpec spec)",
            StringComparison.Ordinal);
    }

    private static string AddConversionHeaderWrite(string source)
    {
        string withCall = source.Replace(
            "        header.Hash = header.CalculateHash();\n\n        return receipts;",
            "        header.Hash = header.CalculateHash();\n        HeaderConversion.Header = header;\n        _ = (BlockHeader)HeaderConversionValue;\n\n        return receipts;",
            StringComparison.Ordinal);
        return withCall.Replace(
            "    private void CommitState(IReleaseSpec spec)",
            "    private sealed class HeaderConversion\n    {\n        internal static BlockHeader Header = null!;\n\n        public static explicit operator BlockHeader(HeaderConversion value)\n        {\n            Header.Hash = Header.CalculateHash();\n            return Header;\n        }\n    }\n\n    private static readonly HeaderConversion HeaderConversionValue = null!;\n\n    private void CommitState(IReleaseSpec spec)",
            StringComparison.Ordinal);
    }

    private static string AddDynamicMemberSentinel(string source) =>
        source.Replace(
            "        header.Hash = header.CalculateHash();\n\n        return receipts;",
            "        header.Hash = header.CalculateHash();\n        dynamic headerMutation = null!;\n        _ = headerMutation.Rewrite(header);\n\n        return receipts;",
            StringComparison.Ordinal);

    private static string AddLazyInitializerHeaderWrite(string source)
    {
        string withCall = source.Replace(
            "        header.Hash = header.CalculateHash();\n\n        return receipts;",
            "        _headerForLazyInitializerMutation = header;\n        header.Hash = header.CalculateHash();\n        _ = _lazyHeaderMutation.Value;\n\n        return receipts;",
            StringComparison.Ordinal);
        return withCall.Replace(
            "    private void CommitState(IReleaseSpec spec)",
            "    private static BlockHeader _headerForLazyInitializerMutation = null!;\n\n    private readonly Lazy<int> _lazyHeaderMutation = new(() =>\n    {\n        _headerForLazyInitializerMutation.Hash = _headerForLazyInitializerMutation.CalculateHash();\n        return 0;\n    });\n\n    private void CommitState(IReleaseSpec spec)",
            StringComparison.Ordinal);
    }

    private static string AddExternalInterfaceGetterHeaderWrite(string source)
    {
        string withCall = source.Replace(
            "        header.Hash = header.CalculateHash();\n\n        return receipts;",
            "        HeaderForExternalGetterMutation = header;\n        header.Hash = header.CalculateHash();\n        _ = _externalGetterMutation.Count;\n\n        return receipts;",
            StringComparison.Ordinal);
        return withCall.Replace(
            "    private void CommitState(IReleaseSpec spec)",
            "    private static BlockHeader HeaderForExternalGetterMutation = null!;\n\n    private sealed class HeaderGetterMutation : System.Collections.Generic.IReadOnlyCollection<int>\n    {\n        public int Count\n        {\n            get\n            {\n                HeaderForExternalGetterMutation.Hash = HeaderForExternalGetterMutation.CalculateHash();\n                return 0;\n            }\n        }\n\n        public System.Collections.Generic.IEnumerator<int> GetEnumerator() =>\n            ((System.Collections.Generic.IEnumerable<int>)Array.Empty<int>()).GetEnumerator();\n\n        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();\n    }\n\n    private readonly System.Collections.Generic.IReadOnlyCollection<int> _externalGetterMutation = null!;\n\n    private void CommitState(IReleaseSpec spec)",
            StringComparison.Ordinal);
    }

    private static string AddEventAccessorHeaderWrite(string source)
    {
        string withCall = source.Replace(
            "        header.Hash = header.CalculateHash();\n\n        return receipts;",
            "        HeaderForEventAccessorMutation = header;\n        header.Hash = header.CalculateHash();\n        _eventAccessorMutation.PropertyChanged += null;\n\n        return receipts;",
            StringComparison.Ordinal);
        return withCall.Replace(
            "    private void CommitState(IReleaseSpec spec)",
            "    private static BlockHeader HeaderForEventAccessorMutation = null!;\n\n    private sealed class HeaderEventMutation : System.ComponentModel.INotifyPropertyChanged\n    {\n        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged\n        {\n            add\n            {\n                HeaderForEventAccessorMutation.Hash = HeaderForEventAccessorMutation.CalculateHash();\n            }\n            remove\n            {\n            }\n        }\n    }\n\n    private readonly System.ComponentModel.INotifyPropertyChanged _eventAccessorMutation = null!;\n\n    private void CommitState(IReleaseSpec spec)",
            StringComparison.Ordinal);
    }

    private static string AddDisposableHeaderWrite(string source) =>
        source.Replace(
            "    private void CommitState(IReleaseSpec spec)",
            "    private static BlockHeader HeaderForDisposableMutation = null!;\n\n    private sealed class HeaderDisposableMutation : IDisposable\n    {\n        public void Dispose()\n        {\n            HeaderForDisposableMutation.Hash = HeaderForDisposableMutation.CalculateHash();\n        }\n    }\n\n    private static void DisposeHeaderMutation()\n    {\n        using IDisposable disposable = new HeaderDisposableMutation();\n    }\n\n    private void CommitState(IReleaseSpec spec)",
            StringComparison.Ordinal);

    private static string AddMetricsTimerSinkHeaderWrite(string source)
    {
        string withHeader = source.Replace(
            "            CommitStateAndStorageRoots(spec);",
            "            HeaderForMetricsTimerSinkMutation = header;\n            CommitStateAndStorageRoots(spec);",
            StringComparison.Ordinal);
        string withField = withHeader.Replace(
            "    private void CommitState(IReleaseSpec spec)",
            "    private static BlockHeader HeaderForMetricsTimerSinkMutation = null!;\n\n    private void CommitState(IReleaseSpec spec)",
            StringComparison.Ordinal);
        return withField.Replace(
            "        public static void AddTicks(long ticks)\n        {\n            Evm.Metrics.IncrementStateHashTime(ticks);",
            "        public static void AddTicks(long ticks)\n        {\n            HeaderForMetricsTimerSinkMutation.BlobGasUsed = 0UL;\n            Evm.Metrics.IncrementStateHashTime(ticks);",
            StringComparison.Ordinal);
    }

    private static string AddReflectionReentryHeaderWrite(string source) =>
        source.Replace(
            "        header.Hash = header.CalculateHash();\n\n        return receipts;",
            "        header.Hash = header.CalculateHash();\n        typeof(BlockProcessor)\n            .GetMethod(nameof(SetAccountChanges), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!\n            .Invoke(this, new object?[] { block });\n\n        return receipts;",
            StringComparison.Ordinal);

    [TestCase("duplicate-roots-post-finally", "extra roots commit after finally is rejected")]
    [TestCase("direct-commit", "direct typed _stateProvider.Commit invocation")]
    [TestCase("direct-commit-post-finally", "direct typed _stateProvider.Commit invocation")]
    [TestCase("flush-state", "unwhitelisted source-local helper 'FlushState'")]
    [TestCase("rewrite-header", "unwhitelisted source-local helper 'RewriteHeader'")]
    [TestCase("nested-flush-state", "NestedCommitHelper")]
    [TestCase("nested-rewrite-header", "NestedHeaderHelper")]
    [TestCase("delegate-header-write", "un-audited delegate invocation 'RewriteAfterHash(header)'")]
    [TestCase("property-accessor-header-write", "source-local property 'RewriteAfterHash' with an executable accessor")]
    [TestCase("constructor-header-write", "source-owned constructor/object creation")]
    [TestCase("interface-dispatch-header-write", "unbound source interface/virtual dispatch '_headerMutator.Rewrite(header)'")]
    [TestCase("operator-header-write", "source-owned binary operator")]
    [TestCase("conversion-header-write", "source-owned user-defined conversion")]
    [TestCase("dynamic-member-sentinel", "un-audited dynamic member invocation")]
    [TestCase("lazy-initializer-header-write", "initializer ledger rejects an un-audited pinned initializer lambda")]
    [TestCase("external-interface-getter-header-write", "property/indexer/event activation ledger drift")]
    [TestCase("event-accessor-header-write", "property/indexer/event activation ledger drift")]
    [TestCase("idisposable-disposal-header-write", "typed effect ledger drift")]
    [TestCase("metrics-timer-sink-header-write", "typed effect ledger drift")]
    [TestCase("reflection-reentry-header-write", "reflection/reentry API")]
    [TestCase("callback-conditional", "TransactionsExecuted signal has an unexpected conditional guard")]
    [TestCase("callback-lambda", "TransactionsExecuted signal expected one")]
    [TestCase("callback-duplicate", "TransactionsExecuted signal expected one")]
    [TestCase("callback-try-finally", "TransactionsExecuted signal must be the exact outer ProcessBlock expression statement")]
    [TestCase("opaque-bound-helper", "bound helper 'SetAccountChanges' through unwhitelisted")]
    [TestCase("opaque-helper-write", "Pinned five-tree typed effect ledger drift")]
    [TestCase("opaque-local-function", "nested local function")]
    [TestCase("opaque-task-run", "un-audited delegate creation")]
    [TestCase("opaque-callback", "un-audited delegate invocation")]
    public void Source_local_closure_mutations_report_specific_diagnostics(string mutation, string expectedDiagnostic)
    {
        string root = FindRepoRoot();
        string path = Path.Combine(root, Extractor.BlockProcessorPath);
        string source = Encoding.UTF8.GetString(File.ReadAllBytes(path));
        string mutated = mutation switch
        {
            "duplicate-roots-post-finally" => source.Replace(
                "        header.Hash = header.CalculateHash();",
                "        CommitStateAndStorageRoots(spec);\n\n        header.Hash = header.CalculateHash();",
                StringComparison.Ordinal),
            "direct-commit" => source.Replace(
                "        CommitState(spec);\n\n        if (spec.IsEip4844Enabled)",
                "        CommitState(spec);\n        _stateProvider.Commit(spec, commitRoots: false);\n\n        if (spec.IsEip4844Enabled)",
                StringComparison.Ordinal),
             "direct-commit-post-finally" => source.Replace(
                 "        header.Hash = header.CalculateHash();",
                 "        header.Hash = header.CalculateHash();\n        _stateProvider.Commit(spec, commitRoots: false);",
                 StringComparison.Ordinal),
             "flush-state" => AddFlushState(source),
             "rewrite-header" => AddRewriteHeader(source),
             "nested-flush-state" => AddNestedFlushState(source),
             "nested-rewrite-header" => AddNestedRewriteHeader(source),
             "delegate-header-write" => AddDelegateHeaderWrite(source),
             "property-accessor-header-write" => AddPropertyAccessorHeaderWrite(source),
             "constructor-header-write" => AddConstructorHeaderWrite(source),
             "interface-dispatch-header-write" => AddInterfaceDispatchHeaderWrite(source),
             "operator-header-write" => AddOperatorHeaderWrite(source),
             "conversion-header-write" => AddConversionHeaderWrite(source),
             "dynamic-member-sentinel" => AddDynamicMemberSentinel(source),
             "lazy-initializer-header-write" => AddLazyInitializerHeaderWrite(source),
             "external-interface-getter-header-write" => AddExternalInterfaceGetterHeaderWrite(source),
             "event-accessor-header-write" => AddEventAccessorHeaderWrite(source),
             "idisposable-disposal-header-write" => AddDisposableHeaderWrite(source),
             "metrics-timer-sink-header-write" => AddMetricsTimerSinkHeaderWrite(source),
             "reflection-reentry-header-write" => AddReflectionReentryHeaderWrite(source),
             "callback-conditional" => source.Replace(
                 "        TransactionsExecuted?.Invoke();",
                 "        if (token.CanBeCanceled)\n        {\n            TransactionsExecuted?.Invoke();\n        }",
                 StringComparison.Ordinal),
             "callback-lambda" => source.Replace(
                 "        TransactionsExecuted?.Invoke();",
                 "        Task.Run(() => TransactionsExecuted?.Invoke());",
                 StringComparison.Ordinal),
             "callback-duplicate" => source.Replace(
                 "        TransactionsExecuted?.Invoke();",
                 "        TransactionsExecuted?.Invoke();\n        TransactionsExecuted?.Invoke();",
                 StringComparison.Ordinal),
             "callback-try-finally" => source.Replace(
                 "        TransactionsExecuted?.Invoke();",
                 "        try\n        {\n            TransactionsExecuted?.Invoke();\n        }\n        finally\n        {\n        }",
                 StringComparison.Ordinal),
             "opaque-bound-helper" => source.Replace(
                 "        if (_logger.IsTrace) _logger.Trace(\"Applying miner rewards:\");",
                 "        SetAccountChanges(block);\n        if (_logger.IsTrace) _logger.Trace(\"Applying miner rewards:\");",
                 StringComparison.Ordinal),
             "opaque-helper-write" => source.Replace(
                 "        if (_logger.IsTrace) _logger.Trace(\"Applying miner rewards:\");",
                 "        block.Header.BlobGasUsed = 0UL;\n        if (_logger.IsTrace) _logger.Trace(\"Applying miner rewards:\");",
                 StringComparison.Ordinal),
             "opaque-local-function" => source.Replace(
                 "        if (_logger.IsTrace) _logger.Trace(\"Applying miner rewards:\");",
                 "        void ObserveOpaque(Block ignored) { }\n        if (_logger.IsTrace) _logger.Trace(\"Applying miner rewards:\");",
                 StringComparison.Ordinal),
               "opaque-task-run" => source.Replace(
                  "        if (_logger.IsTrace) _logger.Trace(\"Applying miner rewards:\");",
                  "        _ = Task.Run(() => { });\n        if (_logger.IsTrace) _logger.Trace(\"Applying miner rewards:\");",
                  StringComparison.Ordinal),
             "opaque-callback" => source.Replace(
                 "        if (_logger.IsTrace) _logger.Trace(\"Applying miner rewards:\");",
                 "        TransactionsExecuted?.Invoke();\n        if (_logger.IsTrace) _logger.Trace(\"Applying miner rewards:\");",
                 StringComparison.Ordinal),
             _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        Assert.That(mutated, Is.Not.EqualTo(source));
        Extractor.RequireCompilationForTest(root, new Dictionary<string, byte[]>
        {
            [Extractor.BlockProcessorPath] = Encoding.UTF8.GetBytes(mutated),
        });
        using TemporaryDirectory output = new();
        ExtractionException? error = null;
        try
        {
            Extractor.ExtractForTest(root, output.Path,
                new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    [Extractor.BlockProcessorPath] = Encoding.UTF8.GetBytes(mutated),
                });
        }
        catch (ExtractionException caught)
        {
            error = caught;
        }

        Assert.That(error, Is.Not.Null);
        Assert.That(error!.Message, Does.Contain(expectedDiagnostic));
        Assert.That(Directory.EnumerateFileSystemEntries(output.Path), Is.Empty);
    }

    [Test]
    public void Generated_kernel_is_executable_and_placeholder_free()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), Extractor.DefaultLeanPath));
        Assert.That(source, Does.Contain("def run"));
        Assert.That(source, Does.Contain("endBlockTrace true"));
        Assert.That(source, Does.Contain("commitRoots"));
        Assert.That(source, Does.Contain("header-bloom mutation is intentionally omitted"));
        Assert.That(source, Does.Contain("postTransactionCommitWitness"));
        Assert.That(source, Does.Not.Contain("headerAfterTrace"));
        Assert.That(ArtifactSafety.ContainsProofToken(source, generated: true), Is.False);
    }

    [Test]
    public void Handwritten_spec_is_relational_and_refinement_is_a_theorem()
    {
        string root = FindRepoRoot();
        string specification = File.ReadAllText(Path.Combine(root,
            "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor/Specification/SequentialBlockPostTransactionFinalization.lean"));
        string refinement = File.ReadAllText(Path.Combine(root,
            "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor/Refinement/SequentialBlockPostTransactionFinalization.lean"));
        Assert.That(specification, Does.Contain("inductive HeaderEffect"));
        Assert.That(specification, Does.Contain("def normalTail"));
        Assert.That(specification, Does.Contain("postTransactionCommitWitness"));
        Assert.That(specification, Does.Not.Contain("def run"));
        Assert.That(refinement, Does.Contain("theorem generated_refines_spec"));
        Assert.That(refinement, Does.Contain("(bridge : CompletedFoldBridge fold input)"));
        Assert.That(refinement, Does.Contain("structure SourceAttachedRefinement"));
        Assert.That(refinement, Does.Contain("(_bridge : CompletedFoldBridge fold input) : Prop where"));
        Assert.That(refinement, Does.Contain("sourceAttachedReferenceResult"));
        Assert.That(refinement, Does.Contain("Fold.projectReceipts fold.state.receipts"));
        Assert.That(refinement, Does.Contain("actualFoldProjection"));
        Assert.That(refinement, Does.Contain("noAdditionalModeledEffects"));
        Assert.That(refinement, Does.Contain("completedFold : CompletedFoldBridge fold input"));
        Assert.That(refinement, Does.Contain("hActualFold := actualFoldProjection_matches_input"));
        Assert.That(refinement, Does.Contain("adapter.noAdditionalModeledEffects"));
        Assert.That(refinement, Does.Contain("bridge.postTransactionCommit"));
        Assert.That(refinement, Does.Not.Contain("generatedSpecRelation"));
        Assert.That(refinement, Does.Not.Contain("hObservation :"));
        Assert.That(refinement, Does.Not.Contain("generated_refines_spec input adapter fold bridge hNormal).1"));
        Assert.That(refinement, Does.Contain("upstreamCompletedTerminalResultsOk"));
        Assert.That(refinement, Does.Contain("bridge_respects_upstream_ok_restriction"));
        Assert.That(refinement, Does.Contain(".evmException"));
        Assert.That(refinement, Does.Not.Contain("def generatedRefinesReference"));
    }

    [Test]
    public void Handwritten_vectors_cover_bloom_guard_and_extensional_bridge_mutation()
    {
        string path = Path.Combine(FindRepoRoot(),
            "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor/Vectors/SequentialBlockPostTransactionFinalizationVectors.lean");
        string source = File.ReadAllText(path);
        Assert.That(source, Does.Contain("synchronous_bloom_header_guard"));
        Assert.That(source, Does.Contain("same_count_different_receipts_is_not_extensional"));
        Assert.That(source, Does.Contain("hook_normal_return_observations"));
        Assert.That(source, Does.Contain("hookThrowScope"));
        Assert.That(source, Does.Contain("normal_event_tail"));
        Assert.That(source, Does.Contain("eip4844_guard_vectors"));
        Assert.That(source, Does.Contain("thread_and_state_root_guard_vectors"));
        Assert.That(source, Does.Contain("unsupported_guard_vectors"));
        Assert.That(source, Does.Contain("source_adapter_effects_are_closed"));
        Assert.That(source, Does.Contain("wrong_source_effects_are_rejected"));
    }

    [Test]
    public void Source_attached_relation_keeps_adapter_and_fold_fields_causal()
    {
        string root = FindRepoRoot();
        string refinement = File.ReadAllText(Path.Combine(root,
            "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor/Refinement/SequentialBlockPostTransactionFinalization.lean"));
        string vectors = File.ReadAllText(Path.Combine(root,
            "tools/Evm/Lean/SequentialBlockPostTransactionFinalizationExtractor/Vectors/SequentialBlockPostTransactionFinalizationVectors.lean"));

        Assert.That(refinement, Does.Contain("adapter.noAdditionalModeledEffects"));
        Assert.That(refinement, Does.Contain("adapter.identity.blockIdentity"));
        Assert.That(refinement, Does.Contain("receiptsIdentity : spec.receipts = mapReceipts generated.receipts"));
        Assert.That(refinement, Does.Contain("bridge.receiptProjection"));
        Assert.That(refinement, Does.Contain("bridge.receiptLogProjection"));
        Assert.That(refinement, Does.Contain("bridge.terminalResultProjection"));
        Assert.That(refinement, Does.Contain("bridge.completedIndexProjection"));
        Assert.That(refinement, Does.Contain("bridge.postTransactionCommit"));
        Assert.That(vectors, Does.Contain("source_adapter_effects_are_closed"));
        Assert.That(vectors, Does.Contain("wrong_source_effects_are_rejected"));
    }

    [Test]
    public void Emitter_reproduces_checked_in_kernel()
    {
        IrDocument document = ReadCheckedIr();
        byte[] expected = File.ReadAllBytes(Path.Combine(FindRepoRoot(), Extractor.DefaultLeanPath));
        Assert.That(Extractor.EmitLeanForTest(document), Is.EqualTo(expected));
    }

    [Test]
    public void ProcessOne_suffix_binds_exact_publication_order_and_dataflow()
    {
        ProcessOnePublicationIrDocument document = ProcessOneValidatedPublicationExtractor.AuditForTest(FindRepoRoot());

        Assert.That(document.AcceptanceState, Is.EqualTo("source-admitted"));
        Assert.That(document.Anchors.Select(static anchor => anchor.Id), Is.EqualTo(new[]
        {
            "processOne.processBlock-return", "processOne.processed-commit",
            "processOne.retry-access-list-catch", "processOne.retry-parallel-catch",
            "processOne.disposal-finally", "processOne.validate-call", "processOne.validation-guard",
            "processOne.validator-call", "processOne.validation-dispose", "processOne.invalid-block-throw", "processOne.post-validation-call",
            "processOne.store-guard", "processOne.insert-deferred", "processOne.return-tuple",
            "postValidation.accountChanges", "postValidation.executionRequests",
            "postValidation.generatedBlockAccessList", "postValidation.encodedBlockAccessList-fallback",
            "validator.generatedBlockAccessList-observation", "options.NoValidation", "options.StoreReceipts",
        }));
        Assert.That(document.OrderedEvents, Is.EqualTo(new[]
        {
            "processBlockReturned", "validatedOrNoValidation", "postValidation.accountChanges",
            "postValidation.executionRequests", "postValidation.generatedBlockAccessList",
            "postValidation.encodedBlockAccessListCoalesce", "publishedExecutionArtifacts",
            "receiptStorage.InsertDeferred", "returnedProcessedBlockAndReceipts",
        }));
        Assert.That(document.DataFlows.Single(static flow => flow.Id == "processOne.receipts").DataFlowSucceeded, Is.True);
        Assert.That(document.DataFlows.Single(static flow => flow.Id == "processOne.receipts").Reads,
            Does.Contain("return(block,receipts);"));
        Assert.That(document.DataFlows.Single(static flow => flow.Id == "processOne.suggestedGeneratedBal").ReachingDefinitions,
            Does.Contain("rejection retains validator observation"));
        Assert.That(document.AdapterPremises.Select(static premise => premise.Id), Is.EqualTo(new[]
        {
            "exactBase", "standardSequential", "balDisabled", "nonparallel", "processBlockNormalReturn",
            "validatorObservation", "rejectionCleanupNormalReturn", "postValidationNormalReturn", "storeNormalReturn", "noAdditionalEffects",
        }));
        Assert.That(document.ForbiddenEffects, Does.Contain("WorldState.CommitTree"));
        Assert.That(document.ForbiddenEffects, Does.Contain("BlockchainProcessor head publication"));
        Assert.That(document.Sources, Has.Length.EqualTo(8));
        Assert.That(document.Anchors.Single(anchor => anchor.Id == "processOne.validator-call").OperationKind,
            Is.EqualTo("Invocation"));
        Assert.That(document.ControlFlows.SelectMany(flow => flow.Edges), Has.Some.Contains("conditional"));
        Assert.That(document.ControlFlows.SelectMany(flow => flow.BlockMemberships),
            Has.Some.Matches<ControlFlowBlockMembership>(block => block.SyntaxStarts.Length > 0));
        PublicationAnchor processBlockReturn = document.Anchors.Single(anchor => anchor.Id == "processOne.processBlock-return");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(processBlockReturn.OwnerFqn, Does.Contain("Nethermind.Evm.Tracing.IBlockTracer"));
            Assert.That(processBlockReturn.SymbolFqn, Does.Contain("Nethermind.Evm.Tracing.IBlockTracer"));
        }
        foreach (string disposalId in new[] { "processOne.disposal-finally", "processOne.validation-dispose" })
        {
            string target = document.Anchors.Single(anchor => anchor.Id == disposalId).SymbolFqn;
            Assert.That(target, Does.Contain("global::Nethermind.Core.BlockExtensions.DisposeAccountChanges("));
            Assert.That(target, Does.Contain("global::Nethermind.Core.Block"));
        }
    }

    [Test]
    public void ProcessOne_suffix_compile_valid_mutations_fail_closed(
        [Values("return-suggested", "return-different-receipts", "encoded-bal-overwrite", "encoded-bal-reverse",
            "duplicate-store", "validator-bal-side-effect", "validator-bal-hidden", "no-validation-inverted",
            "store-guard-inverted", "validation-conditional", "processed-flag", "acceptance-dispose",
            "rejection-dispose-missing", "validator-arguments", "copy-account", "copy-requests", "copy-bal",
            "post-validation-extra-statement", "commit-tree", "reset", "retry-filter", "disposal-wrong-target")]
        string mutation)
    {
        string root = FindRepoRoot();
        string blockProcessor = File.ReadAllText(Path.Combine(root, Extractor.BlockProcessorPath));
        string validator = File.ReadAllText(Path.Combine(root,
            "src/Nethermind/Nethermind.Consensus/Validators/BlockValidator.cs"));
        Dictionary<string, byte[]> overrides = new(StringComparer.Ordinal);
        switch (mutation)
        {
            case "disposal-wrong-target":
                blockProcessor += "\ninternal static class PublicationDisposalSentinel\n{\n" +
                    "    public static void DisposeAccountChanges(this Nethermind.Core.Block block) { }\n}\n";
                break;
            case "return-different-receipts":
                blockProcessor = blockProcessor.Replace("return (block, receipts);", "return (block, Array.Empty<TxReceipt>());", StringComparison.Ordinal);
                break;
            case "encoded-bal-reverse":
                blockProcessor = blockProcessor.Replace("processedBlock.EncodedBlockAccessList ?? suggestedBlock.EncodedBlockAccessList",
                    "suggestedBlock.EncodedBlockAccessList ?? processedBlock.EncodedBlockAccessList", StringComparison.Ordinal);
                break;
            case "no-validation-inverted":
                blockProcessor = blockProcessor.Replace("!options.ContainsFlag(ProcessingOptions.NoValidation)",
                    "options.ContainsFlag(ProcessingOptions.NoValidation)", StringComparison.Ordinal);
                break;
            case "store-guard-inverted":
                blockProcessor = blockProcessor.Replace("if (options.ContainsFlag(ProcessingOptions.StoreReceipts))",
                    "if (!options.ContainsFlag(ProcessingOptions.StoreReceipts))", StringComparison.Ordinal);
                break;
            case "validation-conditional":
                blockProcessor = blockProcessor.Replace("        ValidateProcessedBlock(suggestedBlock, options, block, receipts);",
                    "        if (options.ContainsFlag(ProcessingOptions.StoreReceipts)) ValidateProcessedBlock(suggestedBlock, options, block, receipts);", StringComparison.Ordinal);
                break;
            case "processed-flag":
                blockProcessor = blockProcessor.Replace("processed = true;", "processed = false;", StringComparison.Ordinal);
                break;
            case "acceptance-dispose":
                blockProcessor = blockProcessor.Replace("        PostValidation(suggestedBlock, block, receipts, options);",
                    "        block.DisposeAccountChanges();\n        PostValidation(suggestedBlock, block, receipts, options);", StringComparison.Ordinal);
                break;
            case "rejection-dispose-missing":
                blockProcessor = blockProcessor.Replace("            block.DisposeAccountChanges();", string.Empty, StringComparison.Ordinal);
                break;
            case "validator-arguments":
                blockProcessor = blockProcessor.Replace("blockValidator.ValidateProcessedBlock(block, receipts, suggestedBlock, out string? error)",
                    "blockValidator.ValidateProcessedBlock(suggestedBlock, receipts, block, out string? error)", StringComparison.Ordinal);
                break;
            case "copy-account":
                blockProcessor = blockProcessor.Replace("suggestedBlock.AccountChanges = processedBlock.AccountChanges;",
                    "suggestedBlock.AccountChanges = null;", StringComparison.Ordinal);
                break;
            case "copy-requests":
                blockProcessor = blockProcessor.Replace("suggestedBlock.ExecutionRequests = processedBlock.ExecutionRequests;",
                    "suggestedBlock.ExecutionRequests = null;", StringComparison.Ordinal);
                break;
            case "copy-bal":
                blockProcessor = blockProcessor.Replace("suggestedBlock.GeneratedBlockAccessList = processedBlock.GeneratedBlockAccessList;",
                    "suggestedBlock.GeneratedBlockAccessList = null;", StringComparison.Ordinal);
                break;
            case "post-validation-extra-statement":
                blockProcessor = blockProcessor.Replace("suggestedBlock.ExecutionRequests = processedBlock.ExecutionRequests;",
                    "suggestedBlock.ExecutionRequests = processedBlock.ExecutionRequests; if (receipts.Length == 0) return;", StringComparison.Ordinal);
                break;
            case "commit-tree":
                blockProcessor = blockProcessor.Replace("return (block, receipts);", "_stateProvider.CommitTree(0); return (block, receipts);", StringComparison.Ordinal);
                break;
            case "reset":
                blockProcessor = blockProcessor.Replace("return (block, receipts);", "_stateProvider.Reset(); return (block, receipts);", StringComparison.Ordinal);
                break;
            case "retry-filter":
                blockProcessor = blockProcessor.Replace("when (_balManager.ParallelExecutionEnabled)",
                    "when (!_balManager.ParallelExecutionEnabled)", StringComparison.Ordinal);
                break;
            case "validator-bal-hidden":
                validator = validator.Replace("suggestedBlock.GeneratedBlockAccessList = processedBlock.GeneratedBlockAccessList;",
                    "if (receipts.Length == 0) suggestedBlock.GeneratedBlockAccessList = processedBlock.GeneratedBlockAccessList;", StringComparison.Ordinal);
                overrides["src/Nethermind/Nethermind.Consensus/Validators/BlockValidator.cs"] = Encoding.UTF8.GetBytes(validator);
                break;
            case "return-suggested":
                blockProcessor = blockProcessor.Replace("return (block, receipts);", "return (suggestedBlock, receipts);",
                    StringComparison.Ordinal);
                overrides[Extractor.BlockProcessorPath] = Encoding.UTF8.GetBytes(blockProcessor);
                break;
            case "encoded-bal-overwrite":
                blockProcessor = blockProcessor.Replace(
                    "suggestedBlock.EncodedBlockAccessList = processedBlock.EncodedBlockAccessList ?? suggestedBlock.EncodedBlockAccessList;",
                    "suggestedBlock.EncodedBlockAccessList = processedBlock.EncodedBlockAccessList;",
                    StringComparison.Ordinal);
                overrides[Extractor.BlockProcessorPath] = Encoding.UTF8.GetBytes(blockProcessor);
                break;
            case "duplicate-store":
                const string storeMarker = "    private void StoreTxReceipts(Block block, TxReceipt[] txReceipts, IReleaseSpec spec) =>";
                blockProcessor = blockProcessor.Replace(
                    storeMarker,
                    "    private void DuplicateInsertDeferred(Block block, TxReceipt[] txReceipts, IReleaseSpec spec)\n" +
                    "    {\n" +
                    "        receiptStorage.InsertDeferred(block, txReceipts, spec);\n" +
                    "        receiptStorage.InsertDeferred(block, txReceipts, spec);\n" +
                    "    }\n\n" + storeMarker,
                    StringComparison.Ordinal);
                blockProcessor = ReplaceOnce(blockProcessor,
                    "receiptStorage.InsertDeferred(block, txReceipts, spec);",
                    "DuplicateInsertDeferred(block, txReceipts, spec);",
                    occurrence: 3);
                overrides[Extractor.BlockProcessorPath] = Encoding.UTF8.GetBytes(blockProcessor);
                break;
            case "validator-bal-side-effect":
                validator = validator.Replace(
                    "suggestedBlock.GeneratedBlockAccessList = processedBlock.GeneratedBlockAccessList;",
                    "suggestedBlock.GeneratedBlockAccessList = null;",
                    StringComparison.Ordinal);
                overrides["src/Nethermind/Nethermind.Consensus/Validators/BlockValidator.cs"] = Encoding.UTF8.GetBytes(validator);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        overrides.TryAdd(Extractor.BlockProcessorPath, Encoding.UTF8.GetBytes(blockProcessor));
        Assert.That(overrides.Any(item => !item.Value.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(root, item.Key)))),
            Is.True, "Mutation must change source bytes before admission is tested.");
        ProcessOneValidatedPublicationExtractor.RequireCompilationForTest(root, overrides);
        Assert.That(() => ProcessOneValidatedPublicationExtractor.AuditForTest(root, overrides),
            Throws.TypeOf<ExtractionException>().With.Message.Contains(ExpectedPublicationDiagnostic(mutation)));
    }

    [Test]
    public void ProcessOne_suffix_ir_mutations_fail_closed(
        [Values("symbol", "owner", "tracer-owner-namespace", "tracer-target-namespace", "cfg", "dataflow", "events", "obligations", "dependencies")] string mutation)
    {
        string root = FindRepoRoot();
        ProcessOnePublicationIrDocument document = ProcessOneValidatedPublicationExtractor.AuditForTest(root);
        ProcessOnePublicationIrDocument changed = mutation switch
        {
            "symbol" => document with { Anchors = [document.Anchors[0] with { SymbolFqn = "wrong target" }, .. document.Anchors.Skip(1)] },
            "owner" => document with { Anchors = [document.Anchors[0] with { OwnerFqn = "wrong owner" }, .. document.Anchors.Skip(1)] },
            "tracer-owner-namespace" => document with
            {
                Anchors = [document.Anchors[0] with
                {
                    OwnerFqn = document.Anchors[0].OwnerFqn.Replace("Nethermind.Evm.Tracing.IBlockTracer",
                        "Nethermind.Blockchain.Tracing.IBlockTracer", StringComparison.Ordinal),
                }, .. document.Anchors.Skip(1)],
            },
            "tracer-target-namespace" => document with
            {
                Anchors = [document.Anchors[0] with
                {
                    SymbolFqn = document.Anchors[0].SymbolFqn.Replace("Nethermind.Evm.Tracing.IBlockTracer",
                        "Nethermind.Blockchain.Tracing.IBlockTracer", StringComparison.Ordinal),
                }, .. document.Anchors.Skip(1)],
            },
            "cfg" => document with { Anchors = [document.Anchors[0] with { ControlFlowBlock = 999 }, .. document.Anchors.Skip(1)] },
            "dataflow" => document with { DataFlows = [document.DataFlows[0] with { Reads = ["wrong receipts"] }, .. document.DataFlows.Skip(1)] },
            "events" => document with { OrderedEvents = document.OrderedEvents.Reverse().ToArray() },
            "obligations" => document with { OpenObligations = ["weakened premise", .. document.OpenObligations.Skip(1)] },
            "dependencies" => document with { Dependencies = document.Dependencies.Reverse().ToArray() },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        Assert.That(() => ProcessOneValidatedPublicationExtractor.ValidateAuditForTest(root, changed),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void ProcessOne_suffix_emission_is_deterministic_and_attaches_actual_source_sites()
    {
        string root = FindRepoRoot();
        ProcessOnePublicationIrDocument document = ProcessOneValidatedPublicationExtractor.AuditForTest(root);
        byte[] emitted = ProcessOneValidatedPublicationExtractor.EmitLeanForTest(root, document);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ProcessOneValidatedPublicationExtractor.EmitLeanForTest(root, document), Is.EqualTo(emitted));
            Assert.That(Encoding.UTF8.GetString(emitted), Does.Contain(document.SourceClosureSha256));
            Assert.That(Encoding.UTF8.GetString(emitted), Does.Contain("processOne.return-tuple"));
            Assert.That(Encoding.UTF8.GetString(emitted), Does.Not.Contain("{{SOURCE_SITES}}"));
        }
    }

    [Test]
    public void ProcessOne_suffix_static_draft_is_not_an_acceptance_witness()
    {
        string root = FindRepoRoot();
        using TemporaryDirectory output = new();
        File.WriteAllText(Path.Combine(output.Path, ProcessOneValidatedPublicationExtractor.IrFileName),
            "{\"acceptanceState\":\"static-draft\"}");
        File.WriteAllText(Path.Combine(output.Path, ProcessOneValidatedPublicationExtractor.ManifestFileName),
            "{\"acceptanceState\":\"static-draft\"}");
        File.WriteAllText(Path.Combine(output.Path, ProcessOneValidatedPublicationExtractor.LeanFileName),
            "-- static-draft\n");
        Assert.That(() => ProcessOneValidatedPublicationExtractor.ValidateCheckedIn(root, output.Path),
            Throws.TypeOf<ExtractionException>().With.Message.Contains("static draft"));
    }

    [Test]
    public void ProcessOne_receipt_dependency_accepts_complete_supported_fixture()
    {
        (JsonObject pins, JsonObject manifest, Dictionary<string, string> hashes) = ReceiptDependencyFixture();
        Assert.That(() => ValidateReceiptFixture(pins, manifest, hashes), Throws.Nothing);
    }

    [Test]
    public void ProcessOne_receipt_dependency_rejects_malformed_document(
        [Values("pins", "manifest")] string location,
        [Values("wrong-schema", "empty", "missing-list", "wrong-kind", "missing-source", "unexpected-source",
            "duplicate-source", "null-source", "missing-hash", "invalid-hash", "mismatched-hash", "extra-field", "extra-source-field",
            "duplicate-property", "malformed-json")] string mutation)
    {
        (JsonObject pins, JsonObject manifest, Dictionary<string, string> hashes) = ReceiptDependencyFixture();
        JsonObject document = location == "pins" ? pins : manifest;
        JsonArray sources = document["sources"]!.AsArray();
        switch (mutation)
        {
            case "wrong-schema": document["schemaVersion"] = location == "pins" ? 1 : 7; break;
            case "empty": sources.Clear(); break;
            case "missing-list": document.Remove("sources"); break;
            case "wrong-kind": document["sources"] = new JsonObject(); break;
            case "missing-source": sources.RemoveAt(0); break;
            case "unexpected-source": sources[0]!["path"] = "src/unadmitted.cs"; break;
            case "duplicate-source": sources[^1] = sources[0]!.DeepClone(); break;
            case "null-source": sources[0] = null; break;
            case "missing-hash": sources[0]!.AsObject().Remove("sha256"); break;
            case "invalid-hash": sources[0]!["sha256"] = "malformed"; break;
            case "mismatched-hash": sources[0]!["sha256"] = new string('0', 64); break;
            case "extra-field": document["unadmitted"] = true; break;
            case "extra-source-field": sources[0]!["unadmitted"] = true; break;
            case "duplicate-property":
            case "malformed-json":
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        string json = document.ToJsonString();
        if (mutation == "duplicate-property") json = "{\"schemaVersion\":" + document["schemaVersion"]!.ToJsonString() + "," + json[1..];
        if (mutation == "malformed-json") json = "{";
        byte[] pinsBytes = Encoding.UTF8.GetBytes(location == "pins" ? json : pins.ToJsonString());
        byte[] manifestBytes = Encoding.UTF8.GetBytes(location == "manifest" ? json : manifest.ToJsonString());
        Assert.That(() => ProcessOneValidatedPublicationExtractor.ValidateReceiptDependencyForTest(pinsBytes, manifestBytes, hashes),
            Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void ProcessOne_receipt_dependency_rejects_jointly_weakened_inventory(
        [Values("empty", "omit-caller", "stale-source")] string mutation)
    {
        (JsonObject pins, JsonObject manifest, Dictionary<string, string> hashes) = ReceiptDependencyFixture();
        foreach (JsonObject document in new[] { pins, manifest })
        {
            JsonArray sources = document["sources"]!.AsArray();
            switch (mutation)
            {
                case "empty": sources.Clear(); break;
                case "omit-caller":
                    JsonNode caller = sources.Single(source => source!["path"]!.GetValue<string>().EndsWith(
                        "/TransactionProcessing/TransactionProcessor.cs", StringComparison.Ordinal))!;
                    sources.Remove(caller);
                    break;
                case "stale-source": sources[0]!["sha256"] = new string('0', 64); break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
        }
        Assert.That(() => ValidateReceiptFixture(pins, manifest, hashes), Throws.TypeOf<ExtractionException>());
    }

    [Test]
    public void ProcessOne_receipt_dependency_rejects_missing_or_changed_file(
        [Values("source", "ir", "lean")] string kind, [Values("missing", "stale", "manifest-hash", "wrong-path")] string mutation)
    {
        (JsonObject pins, JsonObject manifest, Dictionary<string, string> hashes) = ReceiptDependencyFixture();
        JsonObject identity = kind == "source" ? manifest["sources"]![0]!.AsObject() : manifest[kind]!.AsObject();
        string path = kind == "ir"
            ? "tools/Evm/Lean/ReceiptTerminalFoldExtractor/Generated/ReceiptTerminalFoldKernel.ir.json"
            : identity["path"]!.GetValue<string>();
        if (mutation == "missing") hashes.Remove(path);
        else if (mutation == "stale") hashes[path] = new string('0', 64);
        else if (mutation == "manifest-hash") identity["sha256"] = new string('0', 64);
        else
        {
            identity["path"] = "unexpected-artifact";
            if (kind == "source") pins["sources"]![0]!["path"] = "unexpected-artifact";
        }
        Assert.That(() => ValidateReceiptFixture(pins, manifest, hashes), Throws.TypeOf<ExtractionException>());
    }

    private static void ValidateReceiptFixture(JsonObject pins, JsonObject manifest, Dictionary<string, string> hashes) =>
        ProcessOneValidatedPublicationExtractor.ValidateReceiptDependencyForTest(
            Encoding.UTF8.GetBytes(pins.ToJsonString()), Encoding.UTF8.GetBytes(manifest.ToJsonString()), hashes);

    private static (JsonObject Pins, JsonObject Manifest, Dictionary<string, string> Hashes) ReceiptDependencyFixture()
    {
        string root = FindRepoRoot();
        const string package = "tools/Evm/Lean/ReceiptTerminalFoldExtractor/";
        JsonObject pins = JsonNode.Parse(File.ReadAllText(Path.Combine(root, package + "SOURCE_PINS.json")))!.AsObject();
        JsonObject manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(root,
            package + "Generated/ReceiptTerminalFoldKernel.source-manifest.json")))!.AsObject();
        manifest["schemaVersion"] = 9;
        manifest["extractorVersion"] = "1.9.2";
        Dictionary<string, string> hashes = new(StringComparer.Ordinal);
        foreach (JsonNode? entry in pins["sources"]!.AsArray())
        {
            JsonObject source = entry!.AsObject();
            string path = source["path"]!.GetValue<string>();
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, path)))).ToLowerInvariant();
            source["sha256"] = hash;
            manifest["sources"]!.AsArray().Single(item => item!["path"]!.GetValue<string>() == path)!["sha256"] = hash;
            hashes.Add(path, hash);
        }
        foreach (string kind in new[] { "ir", "lean" })
        {
            string path = package + "Generated/ReceiptTerminalFoldKernel." + (kind == "ir" ? "ir.json" : "lean");
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, path)))).ToLowerInvariant();
            manifest[kind]!["sha256"] = hash;
            hashes.Add(path, hash);
        }
        return (pins, manifest, hashes);
    }

    private static IrDocument ReadCheckedIr() => Extractor.LoadIrForTest(
        Path.Combine(FindRepoRoot(), Extractor.DefaultOutputPath, Extractor.ArtifactName + ".ir.json"));

    private static string ReplaceOnce(string source, string oldValue, string newValue, int occurrence)
    {
        int start = -1;
        for (int index = 0; index < occurrence; index++)
        {
            start = source.IndexOf(oldValue, start + 1, StringComparison.Ordinal);
            if (start < 0)
            {
                return source;
            }
        }

        return source[..start] + newValue + source[(start + oldValue.Length)..];
    }

    private static string MoveBalAfterHash(string source)
    {
        const string bal = "            _balManager.SetBlockAccessList(block);";
        const string hash = "        header.Hash = header.CalculateHash();";
        int balPosition = source.IndexOf(bal, StringComparison.Ordinal);
        int hashPosition = source.IndexOf(hash, StringComparison.Ordinal);
        if (balPosition < 0 || hashPosition < 0 || balPosition > hashPosition)
        {
            return source;
        }

        string withoutBal = source.Remove(balPosition, bal.Length);
        int adjustedHash = hashPosition - bal.Length;
        return withoutBal.Insert(adjustedHash + hash.Length, "\n\n" + bal);
    }

    private static string AddTaskReassignment(string source)
    {
        const string marker = "            CalculateBlooms(receipts);";
        string replacement = "            bloomsAndReceiptsRootTask = null;" + Environment.NewLine + marker;
        return source.Replace(marker, replacement, StringComparison.Ordinal);
    }

    private static string AddAfterTaskInitializer(string source, string addition)
    {
        const string marker = "        Task<(Bloom BlockBloom, Hash256 ReceiptsRoot)>? bloomsAndReceiptsRootTask = null;";
        return source.Replace(marker, marker + Environment.NewLine + addition, StringComparison.Ordinal);
    }

    private static string WrapStatement(string source, string statement, int occurrence = 1)
    {
        string replacement = "        if (true)" + Environment.NewLine +
            "        {" + Environment.NewLine + statement + Environment.NewLine +
            "        }";
        return ReplaceOnce(source, statement, replacement, occurrence);
    }

    private static string WrapTaskInitializer(string source)
    {
        const string declaration = "        Task<(Bloom BlockBloom, Hash256 ReceiptsRoot)>? bloomsAndReceiptsRootTask = null;";
        const string replacement = "        Task<(Bloom BlockBloom, Hash256 ReceiptsRoot)>? bloomsAndReceiptsRootTask;\n" +
            "        if (true)\n" +
            "        {\n" +
            "            bloomsAndReceiptsRootTask = null;\n" +
            "        }";
        return source.Replace(declaration, replacement, StringComparison.Ordinal);
    }

    private static IrDocument DuplicateMembership(IrDocument document)
    {
        ControlFlowIdentity flow = document.ControlFlows.Single(item => item.Id == "block.processBlock");
        ControlFlowBlockMembership target = flow.BlockMemberships
            .First(membership => membership.SyntaxStarts.Length != 0);
        ControlFlowIdentity alteredFlow = flow with
        {
            BlockMemberships = flow.BlockMemberships.Select(membership => membership.Ordinal == target.Ordinal
                ? membership with { SyntaxStarts = target.SyntaxStarts.Concat(target.SyntaxStarts).ToArray() }
                : membership).ToArray(),
        };
        return document with
        {
            ControlFlows = document.ControlFlows.Select(item => item.Id == flow.Id ? alteredFlow : item).ToArray(),
        };
    }

    private static IrDocument AddDominanceBypass(IrDocument document)
    {
        ControlFlowIdentity flow = document.ControlFlows.Single(item => item.Id == "block.processBlock");
        int entry = flow.ReachableBlocks.Min();
        int returnBlock = document.Anchors.Single(anchor => anchor.Id == "block.return-receipts")
            .Binding.ControlFlowBlock;
        string bypass = $"{entry}->{returnBlock}";
        if (flow.Edges.Contains(bypass, StringComparer.Ordinal))
        {
            int nonDominatedTarget = document.Anchors.Single(anchor => anchor.Id == "block.rewards")
                .Binding.ControlFlowBlock;
            bypass = $"{entry}->{nonDominatedTarget}";
        }

        ControlFlowIdentity alteredFlow = flow with
        {
            Edges = flow.Edges.Concat([bypass]).ToArray(),
        };
        return document with
        {
            ControlFlows = document.ControlFlows.Select(item => item.Id == flow.Id ? alteredFlow : item).ToArray(),
        };
    }

    private static string FindRepoRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, Extractor.BlockProcessorPath)))
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
                "sequential-post-finalization-" + Guid.NewGuid().ToString("N"));
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
