// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Threading;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing;

/// <summary>
/// Exercises <see cref="PerTxTimingCollector"/>'s buffer handoff under the same kind of
/// parallel write pattern <see cref="BlockProcessor.ParallelBlockValidationTransactionsExecutor"/>
/// produces: many worker threads call <see cref="PerTxTimingCollector.Record"/> after
/// <see cref="PerTxTimingCollector.Prepare"/>, and the block-processing thread that called
/// <see cref="PerTxTimingCollector.Snapshot"/> after the join sees every recorded value.
/// </summary>
[TestFixture]
[NonParallelizable]
public class PerTxTimingCollectorTests
{
    private bool _previousProcessingThread;

    [SetUp]
    public void Setup()
    {
        _previousProcessingThread = ProcessingThread.IsBlockProcessingThread;
        ProcessingThread.IsBlockProcessingThread = true;
        PerTxTimingCollector.SetEnabled(true);
    }

    [TearDown]
    public void TearDown()
    {
        PerTxTimingCollector.DiscardPending();
        PerTxTimingCollector.SetEnabled(false);
        ProcessingThread.IsBlockProcessingThread = _previousProcessingThread;
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Transaction_executor_keeps_timings_with_its_processing_loop(bool secondProcessingLoop)
    {
        ExecuteTransactions(2);
        long[] other = Task.Factory.StartNew(() =>
        {
            ProcessingThread.IsBlockProcessingThread = secondProcessingLoop;
            try
            {
                ExecuteTransactions(1);
                using ArrayPoolList<long> timings = PerTxTimingCollector.Snapshot();
                return timings?.AsSpan().ToArray();
            }
            finally
            {
                PerTxTimingCollector.DiscardPending();
                ProcessingThread.IsBlockProcessingThread = false;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).GetAwaiter().GetResult();

        using ArrayPoolList<long> snapshot = PerTxTimingCollector.Snapshot();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot, Has.Count.EqualTo(2).And.All.GreaterThan(0L));
            if (secondProcessingLoop)
                Assert.That(other, Has.Length.EqualTo(1).And.All.GreaterThan(0L));
            else
                Assert.That(other, Is.Null);
        }
    }

    [Test]
    public void Executor_releases_timings_after_processing_even_on_failure([Values] bool fail)
    {
        Transaction transaction = Build.A.Transaction.TestObject;
        Block block = Build.A.Block.TestObject.WithReplacedBody(new BlockBody([transaction], []));
        ITransactionProcessorAdapter adapter = Substitute.For<ITransactionProcessorAdapter>();
        adapter.Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>()).Returns(_ =>
            fail ? throw new InvalidOperationException("Test failure") : TransactionResult.Ok);
        TimingExecutor executor = new(adapter, Substitute.For<IWorldState>());
        using BlockReceiptsTracer receipts = new();
        receipts.SetOtherTracer(NullBlockTracer.Instance);
        receipts.StartNewBlockTrace(block);

        if (fail)
            Assert.Throws<InvalidOperationException>(() => executor.ProcessTransactions(block, ProcessingOptions.None, receipts, CancellationToken.None));
        else
            executor.ProcessTransactions(block, ProcessingOptions.None, receipts, CancellationToken.None);

        using ArrayPoolList<long> snapshot = PerTxTimingCollector.Snapshot();
        Assert.That(snapshot, Has.Count.EqualTo(1));
        snapshot[0] = -1;
        fail = false;
        receipts.StartNewBlockTrace(block);
        executor.ProcessSingle(block, receipts);
        Assert.That(snapshot[0], Is.EqualTo(-1));
    }

    private sealed class TimingExecutor(ITransactionProcessorAdapter adapter, IWorldState state)
        : BlockProcessor.BlockValidationTransactionsExecutor(adapter, state)
    {
        public void ProcessSingle(Block block, BlockReceiptsTracer receipts) =>
            ProcessTransaction(block, block.Transactions[0], 0, receipts, ProcessingOptions.None);
    }

    private static void ExecuteTransactions(int count)
    {
        Transaction[] transactions = new Transaction[count];
        for (int i = 0; i < count; i++)
            transactions[i] = Build.A.Transaction.TestObject;
        Block block = Build.A.Block.TestObject.WithReplacedBody(new BlockBody(transactions, []));
        ITransactionProcessorAdapter adapter = Substitute.For<ITransactionProcessorAdapter>();
        adapter.Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>())
            .Returns(_ =>
            {
                Thread.SpinWait(10_000);
                return TransactionResult.Ok;
            });
        BlockProcessor.BlockValidationTransactionsExecutor executor = new(adapter, Substitute.For<IWorldState>());
        using BlockReceiptsTracer receipts = new();
        receipts.SetOtherTracer(NullBlockTracer.Instance);
        receipts.StartNewBlockTrace(block);
        executor.ProcessTransactions(block, ProcessingOptions.None, receipts, CancellationToken.None);
    }

    [Test]
    public void Parallel_workers_recording_concurrently_are_all_visible_in_snapshot()
    {
        // Mirrors the parallel executor's setup: Prepare on the calling thread, workers fan out
        // through ParallelUnbalancedWork.For (which is the join barrier the collector's threading
        // contract relies on), then Snapshot on the calling thread reads the result.
        const int txCount = 512;

        ArrayPoolList<long> timings = PerTxTimingCollector.Prepare(txCount);

        // Each worker writes a unique non-zero value into its slot. Using `i + 1` so we can later
        // tell a stale-zero slot apart from one that was correctly written by the worker.
        ParallelUnbalancedWork.For(0, txCount, i => PerTxTimingCollector.Record(timings, i, i + 1L));

        using ArrayPoolList<long> snapshot = PerTxTimingCollector.Snapshot();

        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot!.Count, Is.EqualTo(txCount));
        for (int i = 0; i < txCount; i++)
        {
            Assert.That(snapshot[i], Is.EqualTo(i + 1L), $"slot {i}");
        }
    }

    [Test]
    public void Snapshot_after_prepare_with_no_records_returns_zero_filled_handoff()
    {
        // Prepare allocates a length-N list pre-cleared to zero; if no worker records anything
        // (e.g. zero-tx block), Snapshot still hands off a Count==N list of zeros, not null.
        // null is reserved for the disabled-collector path tested below.
        PerTxTimingCollector.Prepare(8);

        using ArrayPoolList<long> snapshot = PerTxTimingCollector.Snapshot();

        Assert.That(snapshot, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshot!.Count, Is.EqualTo(8));
            Assert.That(snapshot.AsSpan().ToArray(), Is.All.EqualTo(0L));
        }
    }

    [Test]
    public void Snapshot_returns_null_when_collector_is_disabled()
    {
        PerTxTimingCollector.SetEnabled(false);
        PerTxTimingCollector.Prepare(4);

        ArrayPoolList<long> snapshot = PerTxTimingCollector.Snapshot();

        Assert.That(snapshot, Is.Null);
    }

    [Test]
    public void Snapshot_hands_off_ownership_so_next_prepare_starts_clean()
    {
        // First block: Prepare → Record → Snapshot hands off. Next Prepare must give us a fresh
        // list; the previous handoff must not be mutated by subsequent Record calls.
        ArrayPoolList<long> firstTimings = PerTxTimingCollector.Prepare(2);
        PerTxTimingCollector.Record(firstTimings, 0, 100L);
        PerTxTimingCollector.Record(firstTimings, 1, 200L);
        using ArrayPoolList<long> first = PerTxTimingCollector.Snapshot();

        ArrayPoolList<long> secondTimings = PerTxTimingCollector.Prepare(2);
        PerTxTimingCollector.Record(secondTimings, 0, 999L);
        using ArrayPoolList<long> second = PerTxTimingCollector.Snapshot();

        Assert.That(first, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first![0], Is.EqualTo(100L));
            Assert.That(first[1], Is.EqualTo(200L));

            Assert.That(second, Is.Not.Null);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(second![0], Is.EqualTo(999L));
            Assert.That(second[1], Is.EqualTo(0L), "previous block's write must not leak into the new buffer");
        }
    }

    [Test]
    public void Record_for_out_of_range_index_is_silently_dropped()
    {
        // Defensive guard in the parallel executor: if for any reason a worker computes an index
        // beyond the prepared range, the collector no-ops rather than throwing — the test contract
        // here keeps that property stable so the executor can rely on it.
        ArrayPoolList<long> timings = PerTxTimingCollector.Prepare(4);
        PerTxTimingCollector.Record(timings, 99, 42L);
        PerTxTimingCollector.Record(timings, -1, 42L);

        using ArrayPoolList<long> snapshot = PerTxTimingCollector.Snapshot();
        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot!.AsSpan().ToArray(), Is.All.EqualTo(0L));
    }
}
