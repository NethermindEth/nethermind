// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Tracing;

public sealed partial class ParallelBlockTracer
{
    /// <summary>Runs <paramref name="action"/> on one of the worker threads.</summary>
    internal Task QueueWorker(Action action)
    {
        ActionJob job = new(action);
        _workers.Post(job, 0);
        return job.Completion;
    }

    /// <summary>A share of work posted to the worker threads, with the ticket it was posted under: a job that is
    /// reused may be posted again before an earlier post of it is taken, and that earlier post must do nothing.</summary>
    private interface IParallelTraceJob
    {
        void Execute(long ticket);
    }

    /// <summary>Threads started when the first block asks and kept for the next one. A job is one worker's share of
    /// one block; with more threads than the degree, the jobs of two blocks run side by side and the semaphore
    /// interleaves their transactions. Every job belongs to a run the tracer has counted in, and disposal waits for
    /// those runs before it stops taking jobs, so no job is ever refused. The queue holds the job and its ticket by
    /// value, so posting allocates nothing once the queue has grown to the most jobs ever waiting.</summary>
    private sealed class Workers(int count) : IDisposable
    {
        /// <summary>Jobs per thread the queue keeps room for once it has drained; a burst beyond that, as when
        /// abandoned helpers pile up behind a response that is not read, is trimmed away again.</summary>
        private const int KeptPerThread = 4;

        private readonly object _gate = new();
        private readonly Queue<(IParallelTraceJob Job, long Ticket)> _jobs = new(count * KeptPerThread);
        private readonly Thread?[] _threads = new Thread?[count];
        private int _started;
        private bool _grown;
        private bool _completed;

        public void Post(IParallelTraceJob job, long ticket)
        {
            lock (_gate)
            {
                if (_started < count) StartMissing();
                _jobs.Enqueue((job, ticket));
                _grown |= _jobs.Count > count * KeptPerThread;
                Monitor.Pulse(_gate);
            }
        }

        /// <summary>Starts the threads not started yet. A thread is recorded only once it has started, so one that
        /// fails to start is tried again by the next post, and <see cref="Dispose"/> joins exactly the started ones.</summary>
        private void StartMissing()
        {
            for (; _started < count; _started++)
            {
                Thread thread = new(Work) { IsBackground = true, Name = $"{nameof(ParallelBlockTracer)} {_started}" };
                thread.Start();
                _threads[_started] = thread;
            }
        }

        private void Work()
        {
            while (true)
            {
                (IParallelTraceJob Job, long Ticket) next;
                lock (_gate)
                {
                    while (_jobs.Count == 0)
                    {
                        if (_completed) return;
                        Monitor.Wait(_gate);
                    }

                    next = _jobs.Dequeue();
                    if (_grown && _jobs.Count == 0)
                    {
                        _jobs.TrimExcess(count * KeptPerThread);
                        _grown = false;
                    }
                }

                next.Job.Execute(next.Ticket);
            }
        }

        /// <summary>Lets the threads finish the jobs already posted, then waits for them.</summary>
        public void Dispose()
        {
            int started;
            lock (_gate)
            {
                _completed = true;
                Monitor.PulseAll(_gate);
                started = _started;
            }

            for (int i = 0; i < started; i++) _threads[i]!.Join();
        }
    }

    /// <summary>A one-off action, for tests that occupy the worker threads.</summary>
    private sealed class ActionJob(Action action) : IParallelTraceJob
    {
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completion => _done.Task;

        public void Execute(long ticket)
        {
            try
            {
                action();
                _done.SetResult();
            }
            catch (Exception e)
            {
                _done.SetException(e);
            }
        }
    }

    /// <summary>The state of one block traced in parallel: its cursor, its results, its helpers and the token that
    /// stops them. Reused from block to block, so a block allocates none of it once the pool is warm.</summary>
    /// <remarks>
    /// <para>The caller traces alongside its helpers. Once it has traced all it can, a helper still queued behind other
    /// blocks' jobs would find the cursor spent, so the caller abandons it: a helper's ticket moves from posted to
    /// started or to abandoned exactly once, by compare and exchange, and the caller then waits only for the helpers
    /// that started. Nothing is cancelled and nothing is thrown on the way.</para>
    /// <para>The stop token is the request's token joined with the failure of any worker. It is linked by one
    /// registration on the request's token rather than by a linked source, and reset rather than replaced.</para>
    /// </remarks>
    private sealed class BlockRun<TTrace> : IDisposable
    {
        private const int IdleRuns = 16;
        private static readonly Lock IdleLock = new();
        private static readonly BlockRun<TTrace>?[] Idle = new BlockRun<TTrace>?[IdleRuns];
        private static int _idleCount;
        private static readonly Action<object?> CancelStop = static state => ((CancellationTokenSource)state!).Cancel();

        private readonly object _gate = new();
        private CancellationTokenSource _stop = new();
        private CancellationTokenRegistration _link;
        private Helper?[] _helpers = [];
        private CollectedTraceResults<TTrace>? _collected;
        private SerializingTraceEmitter<TTrace>? _serializing;
        private InTurnTraceEmitter<TTrace>? _inTurn;

        private ParallelBlockTracer? _tracer;
        private Block? _block;
        private BlockHeader? _parent;
        private ParallelTraceBudget? _slots;
        private Func<IWorldState, Hash256, IBlockTracer<TTrace>>? _forTransaction;
        private ParallelTraceResults<TTrace>? _results;
        private bool _streamed;
        private int _workers;
        private int _queued;
        private int _cursor;
        private int _running;
        private long _request;
        private ExceptionDispatchInfo? _helperFailure;

        public static BlockRun<TTrace> Rent()
        {
            lock (IdleLock)
            {
                if (_idleCount != 0)
                {
                    BlockRun<TTrace> run = Idle[--_idleCount]!;
                    Idle[_idleCount] = null;
                    return run;
                }
            }

            return new BlockRun<TTrace>();
        }

        /// <summary>Starts a block; a streamed one serializes its traces on the workers when the stream allows it, and
        /// in block order otherwise, and either way only a window of transactions per worker is traced ahead of the
        /// response.</summary>
        public ParallelTraceResults<TTrace> Start(ParallelBlockTracer tracer, Block block, BlockHeader parent, ParallelTraceBudget slots,
            Func<IWorldState, Hash256, IBlockTracer<TTrace>> forTransaction, IParallelTraceStream<TTrace>? stream, int workers, CancellationToken token)
        {
            _tracer = tracer;
            _block = block;
            _parent = parent;
            _slots = slots;
            _forTransaction = forTransaction;
            _workers = workers;
            _streamed = stream is not null;
            _cursor = -1;
            _queued = 0;
            _running = 0;
            _request = ExternalRequest.Current;
            _helperFailure = null;
            if (!_stop.TryReset())
            {
                _stop.Dispose();
                _stop = new CancellationTokenSource();
            }

            if (token.CanBeCanceled) _link = token.UnsafeRegister(CancelStop, _stop);

            int transactions = block.Transactions.Length;
            int window = workers * tracer.StreamedPerWorker;
            if (stream is null)
            {
                _collected ??= new CollectedTraceResults<TTrace>();
                _collected.Start(transactions + 1);
                return _results = _collected;
            }

            if (stream.SerializeAheadOptions is { } options)
            {
                _serializing ??= new SerializingTraceEmitter<TTrace>();
                long budget = Math.Max(StreamedBudgetBytes, workers * StreamedBudgetBytesPerWorker);
                _serializing.Start(stream, options, tracer._serialized, transactions, window, budget);
                return _results = _serializing;
            }

            _inTurn ??= new InTurnTraceEmitter<TTrace>();
            _inTurn.Start(stream, transactions, window);
            return _results = _inTurn;
        }

        /// <summary>Traces every transaction on the caller and its helpers. False when the first transaction shows the
        /// block cannot be traced this way, before any helper exists.</summary>
        public bool Trace(ICoveredBlock covered)
        {
            ParallelTraceResults<TTrace> results = _results!;
            CancellationToken stop = _stop.Token;
            IPrefixStateSeedSource callerSeeds = covered.CreateWorkerSeeds();
            results.Reserve(stop);
            if (!TraceOne(Interlocked.Increment(ref _cursor), callerSeeds, stop)) return false;

            ExceptionDispatchInfo? failure = null;
            try
            {
                for (int i = 1; i < _workers; i++) Queue(covered.CreateWorkerSeeds());
                TraceMany(callerSeeds);
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
                _stop.Cancel();
            }
            finally
            {
                // Whatever the caller ran into, nothing the helpers use may be released while one still runs.
                AbandonUnstarted();
                WaitForHelpers();
            }

            ThrowFailure(failure, _helperFailure);
            return true;
        }

        /// <summary>The traces of a block the caller takes whole; nothing for a streamed block.</summary>
        public IReadOnlyList<TTrace> TakeTraces() => _streamed ? [] : _collected!.Flatten();

        private bool TraceOne(int index, IPrefixStateSeedSource seeds, CancellationToken stop) =>
            _tracer!.TraceOne(_block!, _parent!, _block!.Transactions, index, _slots!, seeds, _forTransaction!, _results!, stop);

        private void TraceMany(IPrefixStateSeedSource seeds)
        {
            ParallelTraceResults<TTrace> results = _results!;
            CancellationToken stop = _stop.Token;
            int transactions = _block!.Transactions.Length;
            try
            {
                while (true)
                {
                    // With the last transaction taken, a place would only be waited for and given back, holding a
                    // worker thread that other blocks' helpers queue for.
                    if (Volatile.Read(ref _cursor) >= transactions - 1) return;

                    results.Reserve(stop);
                    int index = Interlocked.Increment(ref _cursor);
                    if (index >= transactions)
                    {
                        results.Unreserve();
                        return;
                    }

                    // The workers already waiting for a place would otherwise hold their threads until the next write.
                    if (index == transactions - 1) results.LastTaken(_workers - 1);

                    if (!TraceOne(index, seeds, stop))
                        throw new InvalidOperationException("The tracer bounded the first transaction and refused a later one.");
                }
            }
            catch
            {
                _stop.Cancel();
                throw;
            }
        }

        private void Queue(IPrefixStateSeedSource seeds)
        {
            if (_queued == _helpers.Length) Array.Resize(ref _helpers, Math.Max(4, 2 * _helpers.Length));
            Helper helper = _helpers[_queued] ??= new Helper(this);
            long ticket = helper.Arm(seeds);
            _queued++;
            Interlocked.Increment(ref _running);
            _tracer!._workers.Post(helper, ticket);
        }

        private void Help(Helper helper)
        {
            try
            {
                using ExternalRequest.Scope request = ExternalRequest.Enter(_request);
                TraceMany(helper.Seeds!);
            }
            catch (Exception exception)
            {
                lock (_gate)
                {
                    if (_helperFailure is null || _helperFailure.SourceException is OperationCanceledException && exception is not OperationCanceledException)
                        _helperFailure = ExceptionDispatchInfo.Capture(exception);
                }
            }
            finally
            {
                Done();
            }
        }

        private void AbandonUnstarted()
        {
            for (int i = 0; i < _queued; i++)
            {
                if (_helpers[i]!.Abandon()) Done();
            }
        }

        private void Done()
        {
            if (Interlocked.Decrement(ref _running) != 0) return;

            lock (_gate) Monitor.PulseAll(_gate);
        }

        private void WaitForHelpers()
        {
            lock (_gate)
            {
                while (Volatile.Read(ref _running) != 0) Monitor.Wait(_gate);
            }
        }

        /// <summary>Lets go of the block and keeps the run for the next one.</summary>
        public void Return()
        {
            _link.Dispose();
            _link = default;
            _results?.Clear();
            for (int i = 0; i < _queued; i++) _helpers[i]!.Seeds = null;
            _tracer = null;
            _block = null;
            _parent = null;
            _slots = null;
            _forTransaction = null;
            _results = null;
            _helperFailure = null;

            lock (IdleLock)
            {
                if (_idleCount < Idle.Length)
                {
                    Idle[_idleCount++] = this;
                    return;
                }
            }

            Dispose();
        }

        public void Dispose()
        {
            _stop.Dispose();
            _collected?.Dispose();
            _serializing?.Dispose();
            _inTurn?.Dispose();
        }

        /// <summary>One helper of the run, reused from block to block. Its ticket holds a generation and a state, so a
        /// post of an earlier block that is taken late finds a different ticket and does nothing.</summary>
        private sealed class Helper(BlockRun<TTrace> run) : IParallelTraceJob
        {
            private const long Posted = 0;
            private const long Started = 1;
            private const long Abandoned = 2;
            private const int StateBits = 2;

            private long _ticket;
            private long _armed;

            public IPrefixStateSeedSource? Seeds { get; set; }

            /// <summary>Moves to the next generation and returns its ticket.</summary>
            public long Arm(IPrefixStateSeedSource seeds)
            {
                Seeds = seeds;
                _armed = ((Volatile.Read(ref _ticket) >> StateBits) + 1) << StateBits | Posted;
                Volatile.Write(ref _ticket, _armed);
                return _armed;
            }

            public void Execute(long ticket)
            {
                if (Interlocked.CompareExchange(ref _ticket, ticket | Started, ticket) != ticket) return;
                run.Help(this);
            }

            /// <summary>True when the helper had not started and now never will.</summary>
            public bool Abandon() => Interlocked.CompareExchange(ref _ticket, _armed | Abandoned, _armed) == _armed;
        }
    }
}
