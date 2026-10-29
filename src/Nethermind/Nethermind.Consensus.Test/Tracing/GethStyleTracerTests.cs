// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Consensus.Tracing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State.OverridableEnv;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Tracing;

public class GethStyleTracerTests
{
    [Test]
    public void Buffered_deadline_resets_per_transaction_and_transfers_results()
    {
        DeadlineClock clock = new();
        IBlockTracer<GethLikeTxTrace> inner = Substitute.For<IBlockTracer<GethLikeTxTrace>>();
        ITxTracer transactionTracer = Substitute.For<ITxTracer>();
        inner.StartNewTxTrace(Arg.Any<Transaction>()).Returns(transactionTracer);
        IDisposable resource = Substitute.For<IDisposable>();
        using GethLikeTxTrace trace = new(resource);
        inner.BuildResult().Returns(new[] { trace });
        GethTraceOptions options = new() { Timeout = TimeSpan.FromSeconds(1), DeadlineClock = clock };
        using (GethLikeBlockDeadlineTracer tracer = new(inner, options, CancellationToken.None, new()))
        {
            for (int i = 0; i < 2; i++)
            {
                using ITxTracer txTracer = tracer.StartNewTxTrace(Build.A.Transaction.TestObject);
                clock.Advance(TimeSpan.FromMilliseconds(600));
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(txTracer.IsCancelled, Is.False);
                    Assert.That(txTracer.IsTracingInstructions, Is.False);
                }
                tracer.EndTxTrace();
            }
            Assert.That(tracer.BuildResult(), Is.EquivalentTo(new[] { trace }));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(clock.Timers, Has.Count.EqualTo(2));
            Assert.That(clock.Timers.TrueForAll(timer => timer.Disposed), Is.True);
            resource.DidNotReceive().Dispose();
            transactionTracer.Received(2).Dispose();
        }
    }

    [Test]
    public void Buffered_deadline_replacement_releases_abandoned_deadline(
        [Values] bool expireFirst, [Values] bool cancelExternal)
    {
        DeadlineClock clock = new();
        using CancellationTokenSource external = new();
        DisposalCountingTracer firstInner = new();
        DisposalCountingTracer secondInner = new();
        IBlockTracer<GethLikeTxTrace> inner = Substitute.For<IBlockTracer<GethLikeTxTrace>>();
        inner.StartNewTxTrace(Arg.Any<Transaction>()).Returns(firstInner, secondInner);
        inner.BuildResult().Returns(Array.Empty<GethLikeTxTrace>());
        using (GethLikeBlockDeadlineTracer tracer = new(inner,
            new() { Timeout = TimeSpan.FromSeconds(1), DeadlineClock = clock }, external.Token, new()))
        {
            ITxTracer first = tracer.StartNewTxTrace(Build.A.Transaction.TestObject);
            using (first)
            {
                clock.Advance(expireFirst ? TimeSpan.FromSeconds(1) : TimeSpan.FromMilliseconds(600));
            }
            using ITxTracer second = tracer.StartNewTxTrace(Build.A.Transaction.TestObject);
            Assert.That(clock.Timers, Has.Count.EqualTo(2));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(clock.Timers[0].DisposeCount, Is.EqualTo(1));
                Assert.That(clock.Timers[1].DisposeCount, Is.Zero);
            }
            Assert.That(clock.Timers[0].FireQueued, Throws.Nothing);
            clock.Advance(TimeSpan.FromMilliseconds(400));
            Assert.That(second.IsCancelled, Is.False);
            if (cancelExternal) external.Cancel();
            clock.Advance(TimeSpan.FromMilliseconds(600));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(first.IsCancelled, Is.EqualTo(expireFirst));
                Assert.That(second.IsCancelled, Is.True);
                Assert.That(tracer.Expired, Is.EqualTo(!cancelExternal));
            }
            if (cancelExternal) Assert.Throws<OperationCanceledException>(tracer.EndTxTrace);
            else Assert.Throws<GethTraceDeadlineException>(tracer.EndTxTrace);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(clock.Timers.TrueForAll(timer => timer.DisposeCount == 1), Is.True);
            Assert.That(firstInner.DisposeCount, Is.EqualTo(1));
            Assert.That(secondInner.DisposeCount, Is.EqualTo(1));
            inner.DidNotReceive().EndTxTrace();
        }
    }

    [Test]
    public void Buffered_deadline_excludes_prefix_and_distinguishes_external_cancellation([Values] bool externalCancellation)
    {
        DeadlineClock clock = new();
        using CancellationTokenSource external = new();
        IBlockTracer<GethLikeTxTrace> inner = Substitute.For<IBlockTracer<GethLikeTxTrace>>();
        inner.StartNewTxTrace(Arg.Any<Transaction>()).Returns(_ => Substitute.For<ITxTracer>());
        inner.BuildResult().Returns(Array.Empty<GethLikeTxTrace>());
        GethTraceOptions options = new() { TxHash = TestItem.KeccakB, Timeout = TimeSpan.FromSeconds(1), DeadlineClock = clock };
        using GethLikeBlockDeadlineTracer tracer = new(inner, options, external.Token, new());
        using (ITxTracer prefix = tracer.StartNewTxTrace(Build.A.Transaction.WithHash(TestItem.KeccakA).TestObject))
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            tracer.EndTxTrace();
        }
        Assert.That(clock.Timers, Is.Empty);

        using ITxTracer target = tracer.StartNewTxTrace(Build.A.Transaction.WithHash(TestItem.KeccakB).TestObject);
        if (externalCancellation) external.Cancel();
        clock.Advance(TimeSpan.FromSeconds(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(target.IsCancelled, Is.True);
            Assert.That(tracer.Expired, Is.EqualTo(!externalCancellation));
        }
        if (externalCancellation) Assert.Throws<OperationCanceledException>(tracer.EndTxTrace);
        else Assert.Throws<GethTraceDeadlineException>(tracer.EndTxTrace);
        Assert.That(clock.Timers.TrueForAll(timer => timer.Disposed), Is.True);
    }

    [Test]
    public void Buffered_deadline_disposes_failed_results_and_live_transaction([Values] bool failConstruction)
    {
        DeadlineClock clock = new();
        IBlockTracer<GethLikeTxTrace> inner = Substitute.For<IBlockTracer<GethLikeTxTrace>>();
        ITxTracer transactionTracer = Substitute.For<ITxTracer>();
        IDisposable resource = Substitute.For<IDisposable>();
        inner.BuildResult().Returns(new[] { new GethLikeTxTrace(resource) });
        inner.StartNewTxTrace(Arg.Any<Transaction>()).Returns(_ => failConstruction ? throw new InvalidOperationException("config") : transactionTracer);
        inner.When(tracer => tracer.EndTxTrace()).Do(_ => throw new InvalidOperationException("result"));
        using (GethLikeBlockDeadlineTracer tracer = new(inner, new() { DeadlineClock = clock }, CancellationToken.None, new()))
        {
            if (failConstruction) Assert.Throws<InvalidOperationException>(() => tracer.StartNewTxTrace(Build.A.Transaction.TestObject));
            else
            {
                using ITxTracer owned = tracer.StartNewTxTrace(Build.A.Transaction.TestObject);
                Assert.Throws<InvalidOperationException>(tracer.EndTxTrace);
            }
        }
        resource.Received(1).Dispose();
        transactionTracer.Received(failConstruction ? 0 : 1).Dispose();
        Assert.That(clock.Timers.TrueForAll(timer => timer.Disposed), Is.True);
    }

    [Test]
    public void Buffered_deadlines_in_parallel_have_independent_timers_and_worker_cancellation()
    {
        DeadlineClock clock = new();
        using CancellationTokenSource worker = new();
        IBlockTracer<GethLikeTxTrace> inner = Substitute.For<IBlockTracer<GethLikeTxTrace>>();
        inner.StartNewTxTrace(Arg.Any<Transaction>()).Returns(_ => Substitute.For<ITxTracer>());
        inner.BuildResult().Returns(Array.Empty<GethLikeTxTrace>());
        GethTraceOptions options = new() { Timeout = TimeSpan.FromSeconds(1), DeadlineClock = clock };
        using GethLikeBlockDeadlineTracer first = new(inner, options, CancellationToken.None, new());
        using GethLikeBlockDeadlineTracer second = new(inner, options, CancellationToken.None, new());
        IBlockTracer firstWorker = GethLikeBlockDeadlineTracer.WithCancellation(first, worker.Token);
        using ITxTracer firstTx = firstWorker.StartNewTxTrace(Build.A.Transaction.TestObject);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        using ITxTracer secondTx = second.StartNewTxTrace(Build.A.Transaction.TestObject);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstTx.IsCancelled, Is.True);
            Assert.That(first.Expired, Is.True);
            Assert.That(secondTx.IsCancelled, Is.False);
        }
        worker.Cancel();
        Assert.That(first.Expired, Is.False, "worker cancellation is external to the transaction deadline");
    }

    [TestCase("success")]
    [TestCase("execution")]
    [TestCase("result")]
    [TestCase("start")]
    public void Buffered_deadline_transfers_transaction_ownership_to_processor_using(string failure)
    {
        DeadlineClock clock = new() { DeliverWhenArmed = failure == "start" };
        DisposalCountingTracer transactionTracer = new();
        IBlockTracer<GethLikeTxTrace> inner = Substitute.For<IBlockTracer<GethLikeTxTrace>>();
        inner.StartNewTxTrace(Arg.Any<Transaction>()).Returns(transactionTracer);
        inner.BuildResult().Returns(Array.Empty<GethLikeTxTrace>());
        if (failure == "result") inner.When(tracer => tracer.EndTxTrace()).Do(_ => throw new InvalidOperationException("result"));
        GethTraceOptions options = new() { Timeout = failure == "start" ? TimeSpan.Zero : TimeSpan.FromSeconds(1), DeadlineClock = clock };
        using (GethLikeBlockDeadlineTracer tracer = new(inner, options, CancellationToken.None, new()))
        {
            void ProcessTransaction()
            {
                using ITxTracer owned = tracer.StartNewTxTrace(Build.A.Transaction.TestObject);
                if (failure == "execution") throw new InvalidOperationException("execution");
                tracer.EndTxTrace();
            }

            if (failure == "success") ProcessTransaction();
            else
            {
                Exception exception = Assert.Catch(ProcessTransaction);
                Assert.That(exception, failure == "start" ? Is.TypeOf<GethTraceDeadlineException>() : Is.TypeOf<InvalidOperationException>());
                tracer.HandleFailure(exception!);
            }
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(transactionTracer.DisposeCount, Is.EqualTo(1));
            Assert.That(clock.Timers.TrueForAll(timer => timer.Disposed), Is.True);
        }
    }

    [Test]
    public void Buffered_deadline_includes_result_construction()
    {
        DeadlineClock clock = new();
        IBlockTracer<GethLikeTxTrace> inner = Substitute.For<IBlockTracer<GethLikeTxTrace>>();
        inner.StartNewTxTrace(Arg.Any<Transaction>()).Returns(new DisposalCountingTracer());
        inner.BuildResult().Returns(Array.Empty<GethLikeTxTrace>());
        inner.When(tracer => tracer.EndTxTrace()).Do(_ => clock.Advance(TimeSpan.FromSeconds(1)));
        using GethLikeBlockDeadlineTracer tracer = new(inner,
            new() { Timeout = TimeSpan.FromSeconds(1), DeadlineClock = clock }, CancellationToken.None, new());
        using ITxTracer owned = tracer.StartNewTxTrace(Build.A.Transaction.TestObject);
        Assert.Throws<GethTraceDeadlineException>(tracer.EndTxTrace);
        Assert.That(clock.Timers.TrueForAll(timer => timer.Disposed), Is.True);
    }

    [Test]
    public void Buffered_deadline_cleanup_preserves_primary_exception()
    {
        IBlockTracer<GethLikeTxTrace> inner = Substitute.For<IBlockTracer<GethLikeTxTrace>>();
        InvalidOperationException cleanup = new("cleanup");
        inner.BuildResult().Returns(_ => throw cleanup);
        InvalidOperationException primary = new("execution");
        using GethLikeBlockDeadlineTracer tracer = new(inner, new(), CancellationToken.None, new());
        Assert.That(() => tracer.HandleFailure(primary), Throws.Nothing);
        Assert.That(primary.Data[nameof(GethLikeBlockDeadlineTracer)], Is.SameAs(cleanup));
    }

    [Test]
    public void Buffered_deadline_failure_normalization_preserves_external_priority_and_cleanup(
        [Values] bool expire, [Values] bool cancelExternal)
    {
        DeadlineClock clock = new();
        using CancellationTokenSource external = new();
        IBlockTracer<GethLikeTxTrace> inner = Substitute.For<IBlockTracer<GethLikeTxTrace>>();
        inner.StartNewTxTrace(Arg.Any<Transaction>()).Returns(new DisposalCountingTracer());
        InvalidOperationException cleanup = new("cleanup");
        inner.BuildResult().Returns(_ => throw cleanup);
        using GethLikeBlockDeadlineTracer tracer = new(inner, new() { DeadlineClock = clock }, external.Token, new());
        using ITxTracer owned = tracer.StartNewTxTrace(Build.A.Transaction.TestObject);
        Assert.That(clock.Timers[0].Due, Is.EqualTo(GethTraceDeadline.DefaultTimeout));
        if (expire) clock.Advance(GethTraceDeadline.DefaultTimeout);
        if (cancelExternal) external.Cancel();
        OperationCanceledException original = new(external.Token);

        Exception failure = Assert.Catch(() =>
        {
            try { throw original; }
            catch (Exception exception)
            {
                tracer.HandleFailure(exception);
                throw;
            }
        });
        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure, expire && !cancelExternal ? Is.TypeOf<GethTraceDeadlineException>() : Is.SameAs(original));
            Assert.That(failure.Data[nameof(GethLikeBlockDeadlineTracer)], Is.SameAs(cleanup));
            Assert.That(clock.Timers.TrueForAll(timer => timer.Disposed), Is.True);
        }
    }

    private sealed class DisposalCountingTracer : TxTracer
    {
        internal int DisposeCount { get; private set; }
        public override void Dispose() => DisposeCount++;
    }

    [Test]
    public void Buffered_deadline_chunks_long_durations_without_sleep()
    {
        DeadlineClock clock = new();
        using GethTraceDeadline deadline = new(clock: clock);
        deadline.Start(TimeSpan.FromDays(100));
        Assert.That(clock.Timers[0].Due, Is.EqualTo(TimeSpan.FromMilliseconds(uint.MaxValue - 1)));
        clock.Advance(TimeSpan.FromDays(50));
        Assert.That(deadline.Expired, Is.False);
        clock.Advance(TimeSpan.FromDays(50));
        Assert.That(deadline.Expired, Is.True);
    }

    [Test]
    public void Buffered_nonpositive_deadline_depends_on_watcher_delivery(
        [Values(0, -1)] int seconds, [Values] bool deliver)
    {
        DeadlineClock clock = new();
        IBlockTracer<GethLikeTxTrace> inner = Substitute.For<IBlockTracer<GethLikeTxTrace>>();
        DisposalCountingTracer transactionTracer = new();
        inner.StartNewTxTrace(Arg.Any<Transaction>()).Returns(transactionTracer);
        inner.BuildResult().Returns(Array.Empty<GethLikeTxTrace>());
        using GethLikeBlockDeadlineTracer tracer = new(inner,
            new() { Timeout = TimeSpan.FromSeconds(seconds), DeadlineClock = clock }, CancellationToken.None, new());
        using (ITxTracer owned = tracer.StartNewTxTrace(Build.A.Transaction.TestObject))
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(clock.Timers[0].Due, Is.EqualTo(TimeSpan.Zero));
                Assert.That(owned.IsCancelled, Is.False, "arming an expired deadline does not deliver its watcher");
            }
            if (deliver) clock.Advance(TimeSpan.Zero);
            Assert.That(owned.IsCancelled, Is.EqualTo(deliver));
            if (deliver) Assert.Throws<GethTraceDeadlineException>(tracer.EndTxTrace);
            else
            {
                Assert.That(tracer.EndTxTrace, Throws.Nothing);
                Assert.That(tracer.BuildResult(), Is.Empty);
            }
        }
        using (Assert.EnterMultipleScope())
        {
            inner.Received(deliver ? 0 : 1).EndTxTrace();
            Assert.That(clock.Timers[0].DisposeCount, Is.EqualTo(1));
            Assert.That(transactionTracer.DisposeCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void Execution_deadline_excludes_paused_time_without_resetting_remaining_budget([Values(1, 2)] int pauses)
    {
        DeadlineClock clock = new();
        using GethTraceDeadline deadline = new(clock: clock);
        deadline.Start(TimeSpan.FromSeconds(1));
        for (int i = 0; i < pauses; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(200));
            Assert.That(deadline.Expired, Is.False);
            deadline.Pause();
            clock.Advance(TimeSpan.FromSeconds(10));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(deadline.Expired, Is.False);
                Assert.That(clock.Timers[0].Due, Is.EqualTo(Timeout.InfiniteTimeSpan));
            }
            deadline.Resume();
        }
        clock.Advance(TimeSpan.FromMilliseconds(1_000 - 200 * pauses - 1));
        Assert.That(deadline.Expired, Is.False);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.That(deadline.Expired, Is.True);
        deadline.Dispose();
        Assert.That(clock.Timers[0].DisposeCount, Is.EqualTo(1));
    }

    [Test]
    public void Paused_deadline_keeps_external_cancellation_provenance()
    {
        DeadlineClock clock = new();
        using CancellationTokenSource external = new();
        using GethTraceDeadline deadline = new(external.Token, clock);
        deadline.Start(TimeSpan.FromSeconds(1));
        deadline.Pause();
        external.Cancel();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deadline.Token.IsCancellationRequested, Is.True);
            Assert.That(deadline.Expired, Is.False);
            Assert.That(clock.Timers[0].Due, Is.EqualTo(Timeout.InfiniteTimeSpan));
        }
        deadline.Resume();
        Assert.That(deadline.Token.IsCancellationRequested, Is.True);
    }

    [Test]
    public void Buffered_deadline_queued_watcher_cannot_cancel_after_disposal(
        [Values(0, -1, 1)] int seconds, [Values] bool paused)
    {
        DeadlineClock clock = new();
        GethTraceDeadline deadline = new(clock: clock);
        deadline.Start(TimeSpan.FromSeconds(seconds));
        CancellationToken token = deadline.Token;
        if (paused) deadline.Pause();
        deadline.Dispose();
        deadline.Dispose();
        Assert.That(deadline.Resume, Throws.Nothing);
        Assert.That(clock.Timers[0].FireQueued, Throws.Nothing);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(token.IsCancellationRequested, Is.False);
            Assert.That(clock.Timers[0].DisposeCount, Is.EqualTo(1));
        }
    }

    private sealed class DeadlineClock : TimeProvider
    {
        private long _timestamp;
        internal bool DeliverWhenArmed { get; init; }
        internal List<DeadlineTimer> Timers { get; } = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period)
        {
            DeadlineTimer timer = new(callback, state, DeliverWhenArmed) { Due = dueTime };
            Timers.Add(timer);
            return timer;
        }
        internal void Advance(TimeSpan elapsed)
        {
            _timestamp += elapsed.Ticks;
            foreach (DeadlineTimer timer in Timers) timer.Fire();
        }
    }

    private sealed class DeadlineTimer(TimerCallback callback, object state, bool deliverWhenArmed) : ITimer
    {
        internal TimeSpan Due { get; set; }
        internal int DisposeCount { get; private set; }
        internal bool Disposed => DisposeCount != 0;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime;
            if (deliverWhenArmed && dueTime == TimeSpan.Zero) Fire();
            return !Disposed;
        }
        internal void Fire() { if (!Disposed) callback(state); }
        internal void FireQueued() => callback(state);
        public void Dispose() => DisposeCount++;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    /// <remarks>
    /// The retained obsolete overload has no other in-tree caller, so its bounds check would regress
    /// unnoticed. The block has to carry a transaction: on an empty block every index is out of range,
    /// so the assertion would hold even if negative indices were not rejected.
    /// </remarks>
    [Test]
    public void Trace_by_block_number_rejects_a_negative_index()
    {
        Block block = Build.A.Block.WithTransactions(Build.A.Transaction.TestObject).TestObject;
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindBlock(block.Number, BlockTreeLookupOptions.RequireCanonical).Returns(block);

        GethStyleTracer tracer = new(
            Substitute.For<IReceiptStorage>(),
            blockTree,
            Substitute.For<IBadBlockStore>(),
            Substitute.For<ISpecProvider>(),
            new ChangeableTransactionProcessorAdapter(Substitute.For<ITransactionProcessor>()),
            Substitute.For<IFileSystem>(),
            Substitute.For<IOverridableEnv<GethStyleTracer.BlockProcessingComponents>>(),
            NullPrefixStateSeedSource.Instance,
            Substitute.For<IOverridableCodeInfoRepository>());

#pragma warning disable CS0618
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            tracer.Trace(block.Number, -1, GethTraceOptions.Default, CancellationToken.None));
#pragma warning restore CS0618

        Assert.That(exception.Message, Does.Contain("the requested tx index was -1"));
    }

    /// <remarks>
    /// A tracer that is neither native nor loadable as JavaScript, an unknown name or inline code that does not
    /// compile, used to reach the JavaScript block tracer, which built a V8 engine per traced transaction only to
    /// fail loading the script.
    /// </remarks>
    [TestCase("flatCallTracer", "not found")]
    [TestCase("_bigInteger", "not found")]
    [TestCase("{ ) }", "could not be compiled")]
    public void Create_options_tracer_rejects_an_unusable_tracer(string tracer, string expectedMessage)
    {
        BlockHeader header = Build.A.BlockHeader.TestObject;
        GethTraceOptions options = GethTraceOptions.Default with { Tracer = tracer };

        Assert.That(
            () => GethStyleTracer.CreateOptionsTracer(header, options, Substitute.For<IWorldState>(), Substitute.For<ISpecProvider>()),
            Throws.ArgumentException.With.Message.Contains(expectedMessage));
    }
}
