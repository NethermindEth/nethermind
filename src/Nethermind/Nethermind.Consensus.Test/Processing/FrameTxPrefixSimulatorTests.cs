// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
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

/// <summary>The admission bounds <see cref="FrameTxPrefixSimulator"/> applies before any EVM work, and how
/// it attributes failures: a non-accepting verdict is charged to the sending peer's flood counter.</summary>
[TestFixture]
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
    public void Simulate_CancelledByTheNodesOwnEnv_LeavesTheTransactionUndecided()
    {
        // Not the caller's token and not the tracer's abort, so it is this node stopping: a malfunction
        // rather than a bound it chose to spend, and the peer must not be charged for it.
        using FrameTxPrefixSimulator simulator = CreateOverBuiltEnv(out IReadOnlyTxProcessorSource source, out _);
        source.Build(Arg.Any<BlockHeader?>()).Throws(new OperationCanceledException());

        FrameTxSimulationResult result = simulator.Simulate(FrameTx());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(FrameTxSimulationOutcome.Undecided));
            Assert.That(result.NodeBound, Is.True, "a shutdown is the node's condition, not the prefix's");
        }
    }

    [Test]
    public void Simulate_SenderNotRecovered_Rejects()
    {
        // The guard standing between an unrecovered sender and SimulateLocked's tx.SenderAddress!, so it is
        // the frame-tx type check that must not swallow it: TxType.FrameTx passes SupportsFrames.
        IReadOnlyTxProcessingEnvFactory envFactory = Substitute.For<IReadOnlyTxProcessingEnvFactory>();
        using FrameTxPrefixSimulator simulator = Create(envFactory, out _);

        FrameTxSimulationResult result = simulator.Simulate(new Transaction { Type = TxType.FrameTx });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(FrameTxSimulationOutcome.Rejected));
            Assert.That(result.Indeterminate, Is.False, "an unrecovered sender is a definite rejection");
            envFactory.DidNotReceive().Create();
        }
    }

    [Test]
    public void Simulate_CancelledBeforeEntry_Throws()
    {
        using FrameTxPrefixSimulator simulator = Create(Substitute.For<IReadOnlyTxProcessingEnvFactory>(), out _);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => simulator.Simulate(FrameTx(), token: cts.Token));
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
    public void Simulate_FaultEpisodeEnds_WarnsAgainOnTheNext()
    {
        // Latching the warning for the whole process life would hide a later, genuinely systemic outage.
        InterfaceLogger sink = Substitute.For<InterfaceLogger>();
        sink.IsWarn.Returns(true);
        FrameTxPrefixSimulator simulator = CreateOverBuiltEnv(out _, out ITransactionProcessor processor, sink);

        Fault(processor, new IOException("disk failure"));
        simulator.Simulate(FrameTx());
        processor.Process(Arg.Any<Transaction>(), Arg.Any<ITxTracer>(), Arg.Any<ExecutionOptions>()).Returns(TransactionResult.Ok);
        simulator.Simulate(FrameTx());
        Fault(processor, new IOException("disk failure"));
        simulator.Simulate(FrameTx());

        sink.Received(2).Warn(Arg.Any<string>());

        static void Fault(ITransactionProcessor processor, Exception fault) =>
            processor.Process(Arg.Any<Transaction>(), Arg.Any<ITxTracer>(), Arg.Any<ExecutionOptions>()).Throws(fault);
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
    public void Simulate_TracerAbortsOnAViolation_RejectsWithTheReasonRatherThanDeferring()
    {
        // The tracer cancels the interpreter at the first banned opcode, so the violation reaches the
        // simulator as a cancellation. Read as an ordinary one it would defer, and admission would pool
        // the transaction the trace rules exist to refuse.
        using FrameTxPrefixSimulator simulator = CreateOverBuiltEnv(out _, out ITransactionProcessor processor);
        Violate(processor);
        processor.Process(Arg.Any<Transaction>(), Arg.Any<ITxTracer>(), Arg.Any<ExecutionOptions>())
            .Throws(new OperationCanceledException());

        FrameTxSimulationResult result = simulator.Simulate(FrameTx());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(FrameTxSimulationOutcome.Rejected));
            Assert.That(result.Indeterminate, Is.False, "a trace violation is a definite verdict on the prefix");
            Assert.That(result.Reason, Does.Contain("SLOAD outside tx.sender storage"));
        }
    }

    [Test]
    public void Simulate_TracerAbortsOnItsWallClockBound_IsChargedToTheSender()
    {
        // The prefix's own wall clock trips the timeout, so revalidation retains it, but it still counts
        // against the sender rather than reading as this node shedding load.
        using FrameTxPrefixSimulator simulator = CreateOverBuiltEnv(out _, out ITransactionProcessor processor, timeoutMs: 1);
        processor.Process(Arg.Any<Transaction>(), Arg.Any<ITxTracer>(), Arg.Any<ExecutionOptions>())
            .Returns<TransactionResult>(static _ =>
            {
                Thread.Sleep(50);
                throw new OperationCanceledException();
            });

        FrameTxSimulationResult result = simulator.Simulate(FrameTx());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(FrameTxSimulationOutcome.Rejected));
            Assert.That(result.Indeterminate, Is.True, "a timeout says nothing about the prefix's validity");
            Assert.That(result.NodeBound, Is.False, "the sender chose the prefix that spent the clock");
        }
    }

    /// <summary>Records a trace violation on whichever tracer the simulator hands the processor.</summary>
    private static void Violate(ITransactionProcessor processor) =>
        processor.When(static p => p.Process(Arg.Any<Transaction>(), Arg.Any<ITxTracer>(), Arg.Any<ExecutionOptions>()))
            .Do(static call => call.ArgAt<ITxTracer>(1).LoadOperationStorage(TestItem.AddressB, UInt256.Zero, ReadOnlySpan<byte>.Empty));

    [Test]
    public void Simulate_AfterDispose_LeavesTheTransactionUndecided()
    {
        FrameTxPrefixSimulator simulator = CreateOverBuiltEnv(out _, out _);
        simulator.Dispose();

        FrameTxSimulationResult result = simulator.Simulate(FrameTx());

        Assert.That(result.Outcome, Is.EqualTo(FrameTxSimulationOutcome.Undecided));
    }

    // The lock is held for the whole of a simulation, so a second arrival must shed rather than park: this
    // runs on a small pool of background threads that also serve sync, and holds the pool's head read lock.
    [Test]
    public void Simulate_WhileAnotherSimulationHoldsTheEnv_ShedsInsteadOfWaiting()
    {
        using ManualResetEventSlim inside = new(false);
        using ManualResetEventSlim release = new(false);
        IReadOnlyTxProcessingEnvFactory envFactory = Substitute.For<IReadOnlyTxProcessingEnvFactory>();
        envFactory.Create().Returns(_ =>
        {
            inside.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            throw new TestEnvUnavailableException();
        });
        // Far longer than this test may take, so a simulator that waits for the lock fails on the clock.
        using FrameTxPrefixSimulator simulator = CreateSimulator(
            envFactory, BlockFinderAtHead(), budgetPerHeadMs: 0, timeoutMs: 30_000);

        Task<FrameTxSimulationResult> holder = Task.Run(() => simulator.Simulate(FrameTx()));
        Assert.That(inside.Wait(TimeSpan.FromSeconds(10)), Is.True, "the first simulation never took the env");

        Stopwatch elapsed = Stopwatch.StartNew();
        FrameTxSimulationResult result = simulator.Simulate(FrameTx());
        elapsed.Stop();
        release.Set();
        holder.Wait(TimeSpan.FromSeconds(10));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Reason, Does.Contain("busy"));
            Assert.That(result.NodeBound, Is.True, "the peer did not choose when this node is busy");
            Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)), "shedding must not wait for the timeout");
        }
    }

    // Shedding protects the background pool that gossip admission runs on; a local submission is on the RPC
    // thread, so shedding it would just hand a peer the exemption from the budget that local calls are given.
    [Test]
    public void Simulate_LocalSubmission_WaitsForABusySimulatorRatherThanShedding()
    {
        using ManualResetEventSlim inside = new(false);
        using ManualResetEventSlim release = new(false);
        IReadOnlyTxProcessingEnvFactory envFactory = Substitute.For<IReadOnlyTxProcessingEnvFactory>();
        envFactory.Create().Returns(_ =>
        {
            inside.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            throw new TestEnvUnavailableException();
        });
        using FrameTxPrefixSimulator simulator = CreateSimulator(
            envFactory, BlockFinderAtHead(), budgetPerHeadMs: 0, timeoutMs: 30_000);

        Task<FrameTxSimulationResult> holder = Task.Run(() => simulator.Simulate(FrameTx()));
        Assert.That(inside.Wait(TimeSpan.FromSeconds(10)), Is.True, "the first simulation never took the env");

        Task<FrameTxSimulationResult> local = Task.Run(() => simulator.Simulate(FrameTx(), local: true));
        // Long enough that a zero wait would already have shed it, so what follows measures the wait.
        Thread.Sleep(200);
        Assert.That(local.IsCompleted, Is.False, "a local submission must not be shed while the simulator is busy");

        release.Set();
        Assert.That(local.Wait(TimeSpan.FromSeconds(10)), Is.True);
        holder.Wait(TimeSpan.FromSeconds(10));

        Assert.That(local.Result.Reason, Does.Not.Contain("busy"));
    }

    // The budget rejects nearly everything under spam, so it is read before the lock; reading it after would
    // make every arrival pay a contended acquisition to learn it had none.
    [Test]
    public void Simulate_WithNoBudgetLeft_ShedsWithoutContendingForTheEnv()
    {
        using ManualResetEventSlim inside = new(false);
        using ManualResetEventSlim release = new(false);
        int calls = 0;
        IReadOnlyTxProcessingEnvFactory envFactory = Substitute.For<IReadOnlyTxProcessingEnvFactory>();
        envFactory.Create().Returns(_ =>
        {
            // The first call spends the budget and returns; the second holds the lock for the rest of the test.
            if (Interlocked.Increment(ref calls) > 1)
            {
                inside.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
            else
            {
                Thread.Sleep(5);
            }

            throw new TestEnvUnavailableException();
        });
        using FrameTxPrefixSimulator simulator = CreateSimulator(
            envFactory, BlockFinderAtHead(), budgetPerHeadMs: 1, timeoutMs: 30_000);

        simulator.Simulate(FrameTx());
        Task<FrameTxSimulationResult> holder = Task.Run(() => simulator.Simulate(FrameTx(), local: true));
        Assert.That(inside.Wait(TimeSpan.FromSeconds(10)), Is.True, "the second simulation never took the env");

        // Both verdicts are available now, so reporting the budget is what proves the lock was never reached.
        FrameTxSimulationResult result = simulator.Simulate(FrameTx());
        release.Set();
        holder.Wait(TimeSpan.FromSeconds(10));

        Assert.That(result.Reason, Does.Contain("budget"));
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

    [TestCase(false, ExecutionOptions.FrameValidationPrefixOnly)]
    [TestCase(true, ExecutionOptions.FrameValidationPrefixOnly | ExecutionOptions.FrameSignaturesPreValidated)]
    public void Simulate_ForwardsTheCallersSignaturePrecondition(bool preValidated, ExecutionOptions expected)
    {
        using FrameTxPrefixSimulator simulator = CreateOverBuiltEnv(out _, out ITransactionProcessor processor);

        simulator.Simulate(FrameTx(), signaturesPreValidated: preValidated);

        processor.Received(1).Process(Arg.Any<Transaction>(), Arg.Any<ITxTracer>(), expected);
    }

    /// <summary>A simulator over an env that builds, so a test can choose where inside it the failure lands.</summary>
    private static FrameTxPrefixSimulator CreateOverBuiltEnv(
        out IReadOnlyTxProcessorSource source,
        out ITransactionProcessor processor,
        InterfaceLogger? logSink = null,
        int timeoutMs = 250)
    {
        processor = Substitute.For<ITransactionProcessor>();
        IReadOnlyTxProcessingScope scope = Substitute.For<IReadOnlyTxProcessingScope>();
        scope.TransactionProcessor.Returns(processor);
        scope.WorldState.Returns(Substitute.For<IWorldState>());

        source = Substitute.For<IReadOnlyTxProcessorSource>();
        source.Build(Arg.Any<BlockHeader?>()).Returns(scope);

        IReadOnlyTxProcessingEnvFactory envFactory = Substitute.For<IReadOnlyTxProcessingEnvFactory>();
        envFactory.Create().Returns(source);

        return CreateSimulator(envFactory, BlockFinderAtHead(), budgetPerHeadMs: 1000, logSink, timeoutMs);
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
        int budgetPerHeadMs,
        InterfaceLogger? logSink = null,
        int timeoutMs = 250) =>
        new(envFactory,
            blockFinder,
            new TestSpecProvider(Eip8141Prototype.Instance),
            new TxPoolConfig { FrameTxSimulationBudgetPerHeadMs = budgetPerHeadMs, FrameTxSimulationTimeoutMs = timeoutMs },
            logSink is null ? LimboLogs.Instance : new OneLoggerLogManager(new ILogger(logSink)));

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
