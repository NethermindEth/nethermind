// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
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

/// <summary>A non-accepting verdict is charged to the sending peer's flood counter, so the simulator must
/// only reject for reasons the transaction is actually answerable for.</summary>
[TestFixture]
public class FrameTxPrefixSimulatorTests
{
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
        FrameTxPrefixSimulator simulator = CreateSimulator(out IReadOnlyTxProcessorSource source, out _);
        source.Build(Arg.Any<BlockHeader?>()).Throws(fault);

        FrameTxSimulationResult result = simulator.Simulate(Tx());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(FrameTxSimulationOutcome.Undecided));
            Assert.That(result.Payer, Is.Null);
        }
    }

    [TestCaseSource(nameof(NodeFaults))]
    public void Simulate_ProcessorHitsNodeFault_LeavesTheTransactionUndecided(Exception fault)
    {
        FrameTxPrefixSimulator simulator = CreateSimulator(out _, out ITransactionProcessor processor);
        processor.Process(Arg.Any<Transaction>(), Arg.Any<ITxTracer>(), Arg.Any<ExecutionOptions>()).Throws(fault);

        FrameTxSimulationResult result = simulator.Simulate(Tx());

        Assert.That(result.Outcome, Is.EqualTo(FrameTxSimulationOutcome.Undecided));
    }

    [Test]
    public void Simulate_FaultEpisodeEnds_WarnsAgainOnTheNext()
    {
        // Latching the warning for the whole process life would hide a later, genuinely systemic outage.
        InterfaceLogger sink = Substitute.For<InterfaceLogger>();
        sink.IsWarn.Returns(true);
        FrameTxPrefixSimulator simulator = CreateSimulator(out _, out ITransactionProcessor processor, logSink: sink);

        Fault(processor, new IOException("disk failure"));
        simulator.Simulate(Tx());
        processor.Process(Arg.Any<Transaction>(), Arg.Any<ITxTracer>(), Arg.Any<ExecutionOptions>()).Returns(TransactionResult.Ok);
        simulator.Simulate(Tx());
        Fault(processor, new IOException("disk failure"));
        simulator.Simulate(Tx());

        sink.Received(2).Warn(Arg.Any<string>());

        static void Fault(ITransactionProcessor processor, Exception fault) =>
            processor.Process(Arg.Any<Transaction>(), Arg.Any<ITxTracer>(), Arg.Any<ExecutionOptions>()).Throws(fault);
    }

    [Test]
    public void Simulate_ProcessorThrowsOverAttackerBytecode_Rejects()
    {
        // Whatever the transaction's own bytecode can provoke stays a rejection.
        FrameTxPrefixSimulator simulator = CreateSimulator(out _, out ITransactionProcessor processor);
        processor.Process(Arg.Any<Transaction>(), Arg.Any<ITxTracer>(), Arg.Any<ExecutionOptions>())
            .Throws(new InvalidOperationException("bad bytecode"));

        FrameTxSimulationResult result = simulator.Simulate(Tx());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(FrameTxSimulationOutcome.Rejected));
            Assert.That(result.Reason, Is.Not.Null);
        }
    }

    [Test]
    public void Simulate_NoChainHead_LeavesTheTransactionUndecided()
    {
        // Having no head is the node's condition, not a defect in the transaction.
        FrameTxPrefixSimulator simulator = CreateSimulator(out _, out _, hasHead: false);

        FrameTxSimulationResult result = simulator.Simulate(Tx());

        Assert.That(result.Outcome, Is.EqualTo(FrameTxSimulationOutcome.Undecided));
    }

    [Test]
    public void Simulate_AfterDispose_LeavesTheTransactionUndecided()
    {
        FrameTxPrefixSimulator simulator = CreateSimulator(out _, out _);
        simulator.Dispose();

        FrameTxSimulationResult result = simulator.Simulate(Tx());

        Assert.That(result.Outcome, Is.EqualTo(FrameTxSimulationOutcome.Undecided));
    }

    [Test]
    public void Simulate_SenderNotRecovered_Rejects()
    {
        FrameTxPrefixSimulator simulator = CreateSimulator(out _, out _);

        FrameTxSimulationResult result = simulator.Simulate(new Transaction { Type = TxType.FrameTx });

        Assert.That(result.Outcome, Is.EqualTo(FrameTxSimulationOutcome.Rejected));
    }

    [Test]
    public void Simulate_Cancelled_ThrowsRatherThanJudgingTheTransaction()
    {
        FrameTxPrefixSimulator simulator = CreateSimulator(out _, out _);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => simulator.Simulate(Tx(), token: cts.Token));
    }

    [TestCase(false, ExecutionOptions.FrameValidationPrefixOnly)]
    [TestCase(true, ExecutionOptions.FrameValidationPrefixOnly | ExecutionOptions.FrameSignaturesPreValidated)]
    public void Simulate_ForwardsTheCallersSignaturePrecondition(bool preValidated, ExecutionOptions expected)
    {
        FrameTxPrefixSimulator simulator = CreateSimulator(out _, out ITransactionProcessor processor);

        simulator.Simulate(Tx(), signaturesPreValidated: preValidated);

        processor.Received(1).Process(Arg.Any<Transaction>(), Arg.Any<ITxTracer>(), expected);
    }

    [Test]
    public void Simulate_ExceedsWallClockBudget_LeavesUndecidedInsteadOfBlocking()
    {
        FrameTxPrefixSimulator simulator = CreateSimulator(out _, out ITransactionProcessor processor, wallClockBudget: TimeSpan.FromMilliseconds(20));
        bool tracerCancelled = false;
        processor.Process(Arg.Any<Transaction>(), Arg.Any<ITxTracer>(), Arg.Any<ExecutionOptions>())
            .Returns(callInfo =>
            {
                ITxTracer tracer = callInfo.ArgAt<ITxTracer>(1);
                tracerCancelled = SpinWait.SpinUntil(() => tracer.IsCancelled, TimeSpan.FromSeconds(5));
                throw new OperationCanceledException();
            });

        FrameTxSimulationResult result = simulator.Simulate(Tx());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracerCancelled, Is.True, "the budget did not cancel the tracer");
            Assert.That(result.Outcome, Is.EqualTo(FrameTxSimulationOutcome.Undecided));
            Assert.That(result.Reason, Does.Contain("budget"));
        }
    }

    [Test]
    public void Simulate_BlockedBehindAnotherSimulation_TimesOutWaitingInsteadOfBlocking()
    {
        FrameTxPrefixSimulator simulator = CreateSimulator(out _, out ITransactionProcessor processor, wallClockBudget: TimeSpan.FromMilliseconds(100));
        using ManualResetEventSlim holding = new();
        using ManualResetEventSlim release = new();
        processor.Process(Arg.Any<Transaction>(), Arg.Any<ITxTracer>(), Arg.Any<ExecutionOptions>())
            .Returns(_ =>
            {
                holding.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                return TransactionResult.Ok;
            });

        Task<FrameTxSimulationResult> first = Task.Run(() => simulator.Simulate(Tx()));
        Assert.That(holding.Wait(TimeSpan.FromSeconds(5)), Is.True, "the first simulation never entered");

        FrameTxSimulationResult second = simulator.Simulate(Tx());
        release.Set();
        Assert.That(first.Wait(TimeSpan.FromSeconds(5)), Is.True, "the first simulation never completed");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.Outcome, Is.EqualTo(FrameTxSimulationOutcome.Undecided));
            Assert.That(second.Reason, Does.Contain("waiting"));
            processor.Received(1).Process(Arg.Any<Transaction>(), Arg.Any<ITxTracer>(), Arg.Any<ExecutionOptions>());
        }
    }

    private static FrameTxPrefixSimulator CreateSimulator(
        out IReadOnlyTxProcessorSource source,
        out ITransactionProcessor processor,
        bool hasHead = true,
        InterfaceLogger? logSink = null,
        TimeSpan? wallClockBudget = null)
    {
        processor = Substitute.For<ITransactionProcessor>();
        IReadOnlyTxProcessingScope scope = Substitute.For<IReadOnlyTxProcessingScope>();
        scope.TransactionProcessor.Returns(processor);
        scope.WorldState.Returns(Substitute.For<IWorldState>());

        source = Substitute.For<IReadOnlyTxProcessorSource>();
        source.Build(Arg.Any<BlockHeader?>()).Returns(scope);

        IReadOnlyTxProcessingEnvFactory envFactory = Substitute.For<IReadOnlyTxProcessingEnvFactory>();
        envFactory.Create().Returns(source);

        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.Head.Returns(hasHead ? Build.A.Block.WithNumber(1).TestObject : null);

        ILogManager logManager = logSink is null ? LimboLogs.Instance : new OneLoggerLogManager(new ILogger(logSink));
        TestSpecProvider specProvider = new(Eip8141Prototype.Instance);
        return wallClockBudget is null
            ? new FrameTxPrefixSimulator(envFactory, blockFinder, specProvider, logManager)
            : new FrameTxPrefixSimulator(envFactory, blockFinder, specProvider, logManager, wallClockBudget.Value);
    }

    private static Transaction Tx() => Build.A.Transaction.WithSenderAddress(TestItem.AddressA).TestObject;
}
