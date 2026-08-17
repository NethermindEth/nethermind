// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing;

/// <summary>
/// The admission bounds <see cref="FrameTxPrefixSimulator"/> applies before any EVM work — the frame-tx
/// boundary guard and the per-head simulation time budget — and how it attributes failures.
/// </summary>
/// <remarks>Attribution matters because a non-accepting verdict is charged to the sending peer's flood
/// counter, so the simulator must only reject for reasons the transaction is answerable for.</remarks>
public class FrameTxPrefixSimulatorTests
{
    [Test]
    public void Simulate_NonFrameTransaction_RejectedWithoutBuildingAnEnv()
    {
        IReadOnlyTxProcessingEnvFactory envFactory = Substitute.For<IReadOnlyTxProcessingEnvFactory>();
        using FrameTxPrefixSimulator simulator = Create(envFactory, out _);

        FrameTxSimulationResult result = simulator.Simulate(Build.A.Transaction.TestObject);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(FrameTxSimulationOutcome.Rejected));
            Assert.That(result.Indeterminate, Is.False, "a wrong transaction type is a definite rejection");
            envFactory.DidNotReceive().Create();
        }
    }

    [Test]
    public void Simulate_WithoutAChainHead_LeavesTheTransactionUndecided()
    {
        IReadOnlyTxProcessingEnvFactory envFactory = Substitute.For<IReadOnlyTxProcessingEnvFactory>();
        using FrameTxPrefixSimulator simulator = Create(envFactory, out IBlockFinder blockFinder);
        blockFinder.Head.Returns((Block?)null);

        FrameTxSimulationResult result = simulator.Simulate(FrameTx());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(FrameTxSimulationOutcome.Undecided));
            Assert.That(result.Indeterminate, Is.True, "nothing was learned about the prefix");
        }
    }

    [Test]
    public void Simulate_ExhaustedHeadBudget_IsIndeterminateAndSpendsNoFurtherWork()
    {
        IReadOnlyTxProcessingEnvFactory envFactory = Substitute.For<IReadOnlyTxProcessingEnvFactory>();
        using FrameTxPrefixSimulator simulator = Create(envFactory, out _, budgetPerHeadMs: 1);
        // The first simulation overruns the 1 ms budget, so the second finds none left for this head.
        simulator.Simulate(FrameTx());
        envFactory.ClearReceivedCalls();

        FrameTxSimulationResult result = simulator.Simulate(FrameTx());

        using (Assert.EnterMultipleScope())
        {
            // Shed load stays a rejection: the throttle exists precisely to slow the sending peer down.
            Assert.That(result.Outcome, Is.EqualTo(FrameTxSimulationOutcome.Rejected));
            Assert.That(result.Indeterminate, Is.True);
            Assert.That(result.Reason, Does.Contain("budget"));
            envFactory.DidNotReceive().Create();
        }
    }

    [Test]
    public void Simulate_LocalSubmission_IsExemptFromTheExhaustedHeadBudget()
    {
        // The budget rations simulation between gossiping peers, so a peer that spends it must not also
        // shut the operator out of their own node until the next head.
        IReadOnlyTxProcessingEnvFactory envFactory = Substitute.For<IReadOnlyTxProcessingEnvFactory>();
        using FrameTxPrefixSimulator simulator = Create(envFactory, out _, budgetPerHeadMs: 1);
        simulator.Simulate(FrameTx());
        envFactory.ClearReceivedCalls();

        FrameTxSimulationResult result = simulator.Simulate(FrameTx(), local: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Reason, Does.Not.Contain("budget"));
            envFactory.Received().Create();
        }
    }

    [Test]
    public void Simulate_CancelledBeforeEntry_Throws()
    {
        using FrameTxPrefixSimulator simulator = Create(Substitute.For<IReadOnlyTxProcessingEnvFactory>(), out _);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => simulator.Simulate(FrameTx(), cts.Token));
    }

    private static IEnumerable<TestCaseData> NodeFaults()
    {
        yield return new TestCaseData(new MissingTrieNodeException("missing", null, default, TestItem.KeccakA)).SetName("missing trie node");
        yield return new TestCaseData(new TrieNodeException("bad node", default, TestItem.KeccakA)).SetName("trie node error");
        yield return new TestCaseData(new TrieStoreException("store failure")).SetName("trie store error");
        yield return new TestCaseData(new TrieException("trie failure")).SetName("trie error");
        yield return new TestCaseData(new ObjectDisposedException("db")).SetName("disposed db");
        yield return new TestCaseData(new IOException("disk failure")).SetName("disk error");
    }

    [TestCaseSource(nameof(NodeFaults))]
    public void Simulate_ScopeBuildHitsNodeFault_LeavesTheTransactionUndecided(Exception fault)
    {
        using FrameTxPrefixSimulator simulator = CreateOverBuiltEnv(out IReadOnlyTxProcessorSource source, out _);
        source.Build(Arg.Any<BlockHeader?>()).Throws(fault);

        FrameTxSimulationResult result = simulator.Simulate(FrameTx());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(FrameTxSimulationOutcome.Undecided));
            Assert.That(result.Payer, Is.Null);
        }
    }

    [TestCaseSource(nameof(NodeFaults))]
    public void Simulate_ProcessorHitsNodeFault_LeavesTheTransactionUndecided(Exception fault)
    {
        // The fault surfaces once the tracer exists, where attributing by position would read it as the
        // transaction's fault.
        using FrameTxPrefixSimulator simulator = CreateOverBuiltEnv(out _, out ITransactionProcessor processor);
        processor.Process(Arg.Any<Transaction>(), Arg.Any<ITxTracer>(), Arg.Any<ExecutionOptions>()).Throws(fault);

        FrameTxSimulationResult result = simulator.Simulate(FrameTx());

        Assert.That(result.Outcome, Is.EqualTo(FrameTxSimulationOutcome.Undecided));
    }

    [Test]
    public void Simulate_ProcessorThrowsOverAttackerBytecode_Rejects()
    {
        // Whatever the transaction's own bytecode can provoke stays a rejection.
        using FrameTxPrefixSimulator simulator = CreateOverBuiltEnv(out _, out ITransactionProcessor processor);
        processor.Process(Arg.Any<Transaction>(), Arg.Any<ITxTracer>(), Arg.Any<ExecutionOptions>())
            .Throws(new InvalidOperationException("bad bytecode"));

        FrameTxSimulationResult result = simulator.Simulate(FrameTx());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(FrameTxSimulationOutcome.Rejected));
            Assert.That(result.Reason, Is.Not.Null);
        }
    }

    [Test]
    public void Simulate_AfterDispose_LeavesTheTransactionUndecided()
    {
        FrameTxPrefixSimulator simulator = CreateOverBuiltEnv(out _, out _);
        simulator.Dispose();

        FrameTxSimulationResult result = simulator.Simulate(FrameTx());

        Assert.That(result.Outcome, Is.EqualTo(FrameTxSimulationOutcome.Undecided));
    }

    private static FrameTxPrefixSimulator Create(
        IReadOnlyTxProcessingEnvFactory envFactory,
        out IBlockFinder blockFinder,
        int budgetPerHeadMs = 1000)
    {
        blockFinder = BlockFinderAtHead();
        // Stands in for a simulation past the bounds: observable, and slow enough to drive the budget.
        envFactory.Create().Returns(_ =>
        {
            Thread.Sleep(5);
            throw new TestEnvUnavailableException();
        });
        return CreateSimulator(envFactory, blockFinder, budgetPerHeadMs);
    }

    /// <summary>A simulator over an env that builds, so a test can choose where inside it the failure lands.</summary>
    private static FrameTxPrefixSimulator CreateOverBuiltEnv(
        out IReadOnlyTxProcessorSource source,
        out ITransactionProcessor processor)
    {
        processor = Substitute.For<ITransactionProcessor>();
        IReadOnlyTxProcessingScope scope = Substitute.For<IReadOnlyTxProcessingScope>();
        scope.TransactionProcessor.Returns(processor);
        scope.WorldState.Returns(Substitute.For<IWorldState>());

        source = Substitute.For<IReadOnlyTxProcessorSource>();
        source.Build(Arg.Any<BlockHeader?>()).Returns(scope);

        IReadOnlyTxProcessingEnvFactory envFactory = Substitute.For<IReadOnlyTxProcessingEnvFactory>();
        envFactory.Create().Returns(source);

        return CreateSimulator(envFactory, BlockFinderAtHead(), budgetPerHeadMs: 1000);
    }

    private static IBlockFinder BlockFinderAtHead()
    {
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.Head.Returns(Build.A.Block.WithNumber(1).TestObject);
        return blockFinder;
    }

    private static FrameTxPrefixSimulator CreateSimulator(
        IReadOnlyTxProcessingEnvFactory envFactory,
        IBlockFinder blockFinder,
        int budgetPerHeadMs) =>
        new(envFactory,
            blockFinder,
            new TestSpecProvider(Bogota.Instance),
            new TxPoolConfig { FrameTxSimulationBudgetPerHeadMs = budgetPerHeadMs },
            LimboLogs.Instance);

    private static Transaction FrameTx() => new()
    {
        Type = TxType.FrameTx,
        SenderAddress = TestItem.AddressA,
        Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 50_000, UInt256.Zero, default)],
        FrameSignatures = [],
        DecodedMaxFeePerGas = UInt256.One,
    };

    private sealed class TestEnvUnavailableException : Exception;
}
