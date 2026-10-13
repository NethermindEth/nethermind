// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading.Tasks.Sources;
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
        using (GethLikeBlockDeadlineTracer tracer = new(inner, options, CancellationToken.None))
        {
            for (int i = 0; i < 2; i++)
            {
                using ITxTracer txTracer = tracer.StartNewTxTrace(Build.A.Transaction.TestObject);
                clock.Advance(TimeSpan.FromMilliseconds(600));
                Assert.That(txTracer.IsCancelled, Is.False);
                Assert.That(txTracer.IsTracingInstructions, Is.False);
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
    public void Buffered_deadline_excludes_prefix_and_distinguishes_external_cancellation([Values] bool externalCancellation)
    {
        DeadlineClock clock = new();
        using CancellationTokenSource external = new();
        IBlockTracer<GethLikeTxTrace> inner = Substitute.For<IBlockTracer<GethLikeTxTrace>>();
        inner.StartNewTxTrace(Arg.Any<Transaction>()).Returns(_ => Substitute.For<ITxTracer>());
        inner.BuildResult().Returns(Array.Empty<GethLikeTxTrace>());
        GethTraceOptions options = new() { TxHash = TestItem.KeccakB, Timeout = TimeSpan.FromSeconds(1), DeadlineClock = clock };
        using GethLikeBlockDeadlineTracer tracer = new(inner, options, external.Token);
        using (ITxTracer prefix = tracer.StartNewTxTrace(Build.A.Transaction.WithHash(TestItem.KeccakA).TestObject))
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            tracer.EndTxTrace();
        }
        Assert.That(clock.Timers, Is.Empty);

        using ITxTracer target = tracer.StartNewTxTrace(Build.A.Transaction.WithHash(TestItem.KeccakB).TestObject);
        if (externalCancellation) external.Cancel();
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.That(target.IsCancelled, Is.True);
        Assert.That(tracer.Expired, Is.EqualTo(!externalCancellation));
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
        using (GethLikeBlockDeadlineTracer tracer = new(inner, new() { DeadlineClock = clock }, CancellationToken.None))
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
        using GethLikeBlockDeadlineTracer first = new(inner, options, CancellationToken.None);
        using GethLikeBlockDeadlineTracer second = new(inner, options, CancellationToken.None);
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
        DeadlineClock clock = new();
        DisposalCountingTracer transactionTracer = new();
        IBlockTracer<GethLikeTxTrace> inner = Substitute.For<IBlockTracer<GethLikeTxTrace>>();
        inner.StartNewTxTrace(Arg.Any<Transaction>()).Returns(transactionTracer);
        inner.BuildResult().Returns(Array.Empty<GethLikeTxTrace>());
        if (failure == "result") inner.When(tracer => tracer.EndTxTrace()).Do(_ => throw new InvalidOperationException("result"));
        GethTraceOptions options = new() { Timeout = failure == "start" ? TimeSpan.Zero : TimeSpan.FromSeconds(1), DeadlineClock = clock };
        using (GethLikeBlockDeadlineTracer tracer = new(inner, options, CancellationToken.None))
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
            new() { Timeout = TimeSpan.FromSeconds(1), DeadlineClock = clock }, CancellationToken.None);
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
        using GethLikeBlockDeadlineTracer tracer = new(inner, new(), CancellationToken.None);
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
        using GethLikeBlockDeadlineTracer tracer = new(inner, new() { DeadlineClock = clock }, external.Token);
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

    [TestCase(false, false, false)]
    [TestCase(false, true, false)]
    [TestCase(true, false, false)]
    [TestCase(false, true, true)]
    public async Task Streaming_deadline_excludes_blocked_reader_but_retains_execution_budget(bool cancelExternal, bool exhaustExecution, bool synchronousFlush)
    {
        DeadlineClock clock = new();
        using CancellationTokenSource external = new();
        Pipe pipe = new(new PipeOptions(pauseWriterThreshold: synchronousFlush ? 0 : 1, resumeWriterThreshold: synchronousFlush ? 0 : 1));
        FlushObserver observed = new(pipe.Writer, () =>
        {
            if (synchronousFlush) clock.Advance(TimeSpan.FromMilliseconds(700));
        });
        using Utf8JsonWriter writer = new(observed);
        GethTraceOptions options = new()
        {
            EnforceExecutionDeadline = true, DeadlineClock = clock, Timeout = TimeSpan.FromSeconds(1), EnableReturnData = true
        };
        using GethLikeBlockEnvelopeStreamingTracer inner = new(options, writer, observed, external.Token);
        using GethLikeBlockDeadlineTracer tracer = new(inner, options, external.Token);
        writer.WriteStartArray();
        Task execution = null;
        try
        {
            for (int transaction = 0; transaction < (cancelExternal || exhaustExecution ? 1 : 2); transaction++)
            {
                observed.Reset();
                ITxTracer current = tracer.StartNewTxTrace(Build.A.Transaction.WithHash(TestItem.KeccakA).TestObject);
                execution = Task.Run(() =>
                {
                    try
                    {
                        using (current)
                        using (ExecutionEnvironment environment = ExecutionEnvironment.Rent(
                            null!, Address.Zero, Address.Zero, null, callDepth: 0, value: default, inputData: default))
                        {
                            current.StartOperation(0, Instruction.RETURNDATACOPY, 100, in environment);
                            current.SetOperationReturnData(new byte[512 * 1024]);
                            clock.Advance(TimeSpan.FromMilliseconds(400));
                            current.StartOperation(1, Instruction.STOP, 100, in environment);
                            clock.Advance(TimeSpan.FromMilliseconds(synchronousFlush ? 0 : exhaustExecution ? 700 : 500));
                            tracer.EndTxTrace();
                        }
                    }
                    catch (Exception exception)
                    {
                        tracer.HandleFailure(exception);
                        throw;
                    }
                });

                await observed.Blocked.WaitAsync(TimeSpan.FromSeconds(10));
                if (!synchronousFlush) clock.Advance(TimeSpan.FromSeconds(20));
                bool expiredDuringTransport = current.IsCancelled;
                if (cancelExternal) external.Cancel();
                ReadResult read = await pipe.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                pipe.Reader.AdvanceTo(read.Buffer.End);
                Exception failure = null;
                try { await execution.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception exception) { failure = exception; }
                using (Assert.EnterMultipleScope())
                {
                    if (!synchronousFlush)
                        Assert.That(expiredDuringTransport, Is.False, "queued timer ticks during backpressure must not spend execution time");
                    Assert.That(failure, cancelExternal ? Is.InstanceOf<OperationCanceledException>()
                        : exhaustExecution ? Is.TypeOf<GethTraceDeadlineException>() : Is.Null);
                    Assert.That(clock.Timers.Count, Is.EqualTo(transaction + 1));
                    Assert.That(clock.Timers.TrueForAll(timer => timer.Disposed), Is.True);
                }
            }
        }
        finally
        {
            external.Cancel();
            observed.CancelPendingFlush();
            if (execution is { IsCompleted: false })
            {
                try { await execution.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (OperationCanceledException) { /* The external token releases a blocked flush during cleanup. */ }
            }
            tracer.Dispose();
            writer.WriteEndArray();
            writer.Flush();
            await pipe.Writer.CompleteAsync();
            await pipe.Reader.CompleteAsync();
        }
    }

    private sealed class FlushObserver(PipeWriter inner, Action beforeFlush) : PipeWriter, IValueTaskSource<FlushResult>
    {
        private TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ValueTask<FlushResult> _pending;
        internal Task Blocked => _blocked.Task;
        internal void Reset() => _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void Advance(int bytes) => inner.Advance(bytes);
        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception exception = null) => inner.Complete(exception);
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            beforeFlush();
            _pending = inner.FlushAsync(cancellationToken);
            if (_pending.IsCompleted) _blocked.TrySetResult();
            return _pending.IsCompleted ? _pending : new(this, 0);
        }
        public FlushResult GetResult(short token) => _pending.GetAwaiter().GetResult();
        public ValueTaskSourceStatus GetStatus(short token) => !_pending.IsCompleted ? ValueTaskSourceStatus.Pending
            : _pending.IsCanceled ? ValueTaskSourceStatus.Canceled
            : _pending.IsFaulted ? ValueTaskSourceStatus.Faulted : ValueTaskSourceStatus.Succeeded;
        public void OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            _pending.ConfigureAwait(false).GetAwaiter().OnCompleted(() => continuation(state));
            _blocked.TrySetResult();
        }
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
    public void Deadline_pause_retains_accumulated_time_and_ignores_queued_ticks([Values] bool longDuration)
    {
        DeadlineClock clock = new();
        using GethTraceDeadline deadline = new(clock: clock);
        TimeSpan duration = longDuration ? TimeSpan.FromDays(100) : TimeSpan.FromSeconds(1);
        deadline.Start(duration);
        clock.Advance(duration * 0.4);
        deadline.Pause();
        deadline.Pause();
        clock.Advance(TimeSpan.FromDays(200));
        Assert.That(deadline.Expired, Is.False);
        Assert.That(clock.Timers[0].Due, Is.EqualTo(Timeout.InfiniteTimeSpan));
        deadline.Resume();
        Assert.That(clock.Timers[0].Due, Is.EqualTo(longDuration ? TimeSpan.FromMilliseconds(uint.MaxValue - 1) : duration * 0.6));
        clock.Advance(duration * 0.5);
        deadline.Resume();
        deadline.Pause();
        clock.Advance(TimeSpan.FromDays(200));
        Assert.That(deadline.Expired, Is.False);
        deadline.Resume();
        Assert.That(clock.Timers[0].Due, Is.EqualTo(duration * 0.1));
        clock.Advance(duration * 0.1);
        Assert.That(deadline.Expired, Is.True);
    }

    [Test]
    public void Deadline_pause_does_not_revive_expired_execution([Values] bool fireTimer)
    {
        DeadlineClock clock = new();
        using GethTraceDeadline deadline = new(clock: clock);
        deadline.Start(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(1), fireTimer);
        deadline.Pause();
        deadline.Resume();
        Assert.That(deadline.Expired, Is.True);
        Assert.That(deadline.Token.IsCancellationRequested, Is.True);
    }

    [Test]
    public void Deadline_pause_cleanup_keeps_external_cancellation_and_disposal([Values] bool disposeWhilePaused)
    {
        DeadlineClock clock = new();
        using CancellationTokenSource external = new();
        using GethTraceDeadline deadline = new(external.Token, clock);
        CancellationToken token = deadline.Token;
        deadline.Start(TimeSpan.FromSeconds(1));
        deadline.Pause();
        if (disposeWhilePaused) deadline.Dispose();
        else external.Cancel();
        deadline.Resume();
        clock.Advance(TimeSpan.FromSeconds(10));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(token.IsCancellationRequested, Is.EqualTo(!disposeWhilePaused));
            Assert.That(deadline.Expired, Is.False);
            Assert.That(clock.Timers[0].Due, Is.EqualTo(Timeout.InfiniteTimeSpan));
        }
        deadline.Dispose();
        Assert.That(clock.Timers.TrueForAll(timer => timer.Disposed), Is.True);
    }

    private sealed class DeadlineClock : TimeProvider
    {
        private long _timestamp;
        internal List<DeadlineTimer> Timers { get; } = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period)
        {
            DeadlineTimer timer = new(callback, state) { Due = dueTime };
            Timers.Add(timer);
            return timer;
        }
        internal void Advance(TimeSpan elapsed, bool fireTimer = true)
        {
            _timestamp += elapsed.Ticks;
            if (fireTimer)
                foreach (DeadlineTimer timer in Timers) timer.Fire();
        }
    }

    private sealed class DeadlineTimer(TimerCallback callback, object state) : ITimer
    {
        internal TimeSpan Due { get; set; }
        internal bool Disposed { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period) { Due = dueTime; return !Disposed; }
        internal void Fire() { if (!Disposed) callback(state); }
        public void Dispose() => Disposed = true;
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
