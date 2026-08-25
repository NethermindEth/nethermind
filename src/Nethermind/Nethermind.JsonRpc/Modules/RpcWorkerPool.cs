// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc.Modules;

/// <summary>
/// A fixed set of dedicated threads that run JSON-RPC invocations off the request threads.
/// </summary>
/// <remarks>
/// Running EVM work on its own threads rather than on Kestrel's thread pool keeps the number of concurrent
/// executions equal to the number of workers and confines the EVM's thread-static pools to a small, long-lived set
/// of threads. Workers run at <see cref="ThreadPriority.BelowNormal"/>, which only yields CPU to block processing on
/// Windows — on Linux the runtime does not map managed priorities onto nice values — so the permit count, not the
/// priority, is what leaves headroom. The pool itself is unbounded and relies on
/// <see cref="RpcAdmissionController"/> handing out exactly <see cref="WorkerCount"/> permits, so at most one item
/// per worker is ever queued. Threads are started on demand so an idle node pays nothing for classes it never
/// serves. Continuations of the returned task run on the thread pool, never on a worker, so response serialization
/// does not occupy a worker slot.
/// </remarks>
internal sealed class RpcWorkerPool : IDisposable
{
    private static readonly TimeSpan ShutdownJoinBudget = TimeSpan.FromSeconds(2);
    private static readonly Action<Thread> DefaultStartThread = static thread => thread.Start();

    private readonly string _threadNamePrefix;
    private readonly Action<Thread> _startThread;
    private readonly Thread?[] _threads;
    private readonly BlockingCollection<WorkItem> _queue = [];
    private readonly CancellationTokenSource _shutdown = new();
    // Serializes worker start-up against Dispose so no thread is ever started on disposed primitives.
    private readonly Lock _lifecycleLock = new();
    private int _startedThreads;
    private volatile bool _disposed;

    public RpcWorkerPool(string threadNamePrefix, int workerCount) : this(threadNamePrefix, workerCount, DefaultStartThread)
    {
    }

    internal RpcWorkerPool(string threadNamePrefix, int workerCount, Action<Thread> startThread)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        _threadNamePrefix = threadNamePrefix;
        _startThread = startThread;
        _threads = new Thread?[workerCount];
    }

    public int WorkerCount => _threads.Length;

    /// <summary>Queues <paramref name="work"/> for a worker thread and returns a task that completes with its result.</summary>
    /// <remarks>
    /// A failure to start a worker thread propagates to the caller and nothing is queued, so an item never waits
    /// for a consumer that does not exist.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The pool was disposed before the work could be queued.</exception>
    public Task<object?> RunAsync(Func<object?> work)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        EnsureWorkerStarted();
        WorkItem item = new(work, ExecutionContext.Capture());
        try
        {
            _queue.Add(item);
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding raced with this call; surface it the same way as the eager check above.
            throw new ObjectDisposedException(nameof(RpcWorkerPool));
        }

        return item.Task;
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _shutdown.Cancel();
        _queue.CompleteAdding();
        FailPending();
        bool allExited = JoinWorkers();
        // A worker cancelled between reserving an item and taking it puts the item back as it exits, so anything
        // that slipped past the first drain is failed here rather than left pending forever.
        FailPending();

        // A worker stuck in a long invocation would otherwise throw on the disposed queue and take the process
        // down; the primitives are left alive for it and the background thread dies with the process instead.
        if (allExited)
        {
            _queue.Dispose();
            _shutdown.Dispose();
        }
    }

    private void FailPending()
    {
        while (_queue.TryTake(out WorkItem? pending))
        {
            pending.Fail(new ObjectDisposedException(nameof(RpcWorkerPool)));
        }
    }

    private bool JoinWorkers()
    {
        long deadline = Environment.TickCount64 + (long)ShutdownJoinBudget.TotalMilliseconds;
        bool allExited = true;
        foreach (Thread? thread in _threads)
        {
            if (thread is null)
            {
                continue;
            }

            int remaining = (int)Math.Max(0, deadline - Environment.TickCount64);
            allExited &= thread.Join(remaining);
        }

        return allExited;
    }

    // Each call starts one more worker until the cap is reached, so the number of live workers is never below the
    // number of items ever queued while it is still growing; afterwards the permit count keeps the queue at <= cap
    // and the lock is never taken again.
    private void EnsureWorkerStarted()
    {
        if (Volatile.Read(ref _startedThreads) >= _threads.Length)
        {
            return;
        }

        lock (_lifecycleLock)
        {
            int index = _startedThreads;
            if (_disposed || index >= _threads.Length)
            {
                return;
            }

            Thread thread = new(WorkerLoop)
            {
                Name = $"{_threadNamePrefix}-{index:00}",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
            };
            // Only a thread that actually started occupies its slot; a failed start leaves it for the next caller.
            _startThread(thread);
            _threads[index] = thread;
            Volatile.Write(ref _startedThreads, index + 1);
        }
    }

    private void WorkerLoop()
    {
        try
        {
            foreach (WorkItem item in _queue.GetConsumingEnumerable(_shutdown.Token))
            {
                item.Run();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Shutdown signal; remaining items are failed by Dispose.
        }
        catch (ObjectDisposedException) when (_disposed)
        {
            // A worker still starting up when Dispose ran must exit quietly rather than crash the process.
        }
    }

    private sealed class WorkItem(Func<object?> work, ExecutionContext? executionContext)
    {
        private static readonly ContextCallback RunInContext = static state =>
        {
            WorkItem item = (WorkItem)state!;
            item._result = item._work();
        };

        private readonly Func<object?> _work = work;
        private readonly TaskCompletionSource<object?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private object? _result;

        public Task<object?> Task => _completion.Task;

        public void Run()
        {
            try
            {
                if (executionContext is null)
                {
                    _result = _work();
                }
                else
                {
                    ExecutionContext.Run(executionContext, RunInContext, this);
                }

                _completion.TrySetResult(_result);
            }
            catch (Exception e)
            {
                _completion.TrySetException(e);
            }
        }

        public void Fail(Exception e) => _completion.TrySetException(e);
    }
}
