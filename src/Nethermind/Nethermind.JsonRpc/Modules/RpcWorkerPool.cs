// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc.Modules;

/// <summary>
/// A fixed set of dedicated, below-normal-priority threads that run JSON-RPC invocations off the request threads.
/// </summary>
/// <remarks>
/// Running EVM work on its own threads rather than on Kestrel's thread pool keeps the number of concurrent
/// executions equal to the number of workers, lets block processing win CPU under RPC overload through thread
/// priority, and confines the EVM's thread-static pools to a small, long-lived set of threads. The pool itself is
/// unbounded and relies on <see cref="RpcAdmissionController"/> handing out exactly <see cref="WorkerCount"/>
/// permits, so at most one item per worker is ever queued. Threads are started on demand so an idle node pays
/// nothing for classes it never serves. Continuations of the returned task run on the thread pool, never on a
/// worker, so response serialization does not occupy a worker slot.
/// </remarks>
public sealed class RpcWorkerPool : IDisposable
{
    private static readonly TimeSpan ShutdownJoinBudget = TimeSpan.FromSeconds(2);

    private readonly string _threadNamePrefix;
    private readonly Thread?[] _threads;
    private readonly BlockingCollection<WorkItem> _queue = [];
    private readonly CancellationTokenSource _shutdown = new();
    private int _startedThreads;
    private volatile bool _disposed;

    public RpcWorkerPool(string threadNamePrefix, int workerCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        _threadNamePrefix = threadNamePrefix;
        _threads = new Thread?[workerCount];
    }

    public int WorkerCount => _threads.Length;

    /// <summary>Queues <paramref name="work"/> for a worker thread and returns a task that completes with its result.</summary>
    /// <exception cref="ObjectDisposedException">The pool was disposed before the work could be queued.</exception>
    public Task<object?> RunAsync(Func<object?> work)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        WorkItem item = new(work, ExecutionContext.Capture());
        EnsureWorkerStarted();
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
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        _queue.CompleteAdding();
        while (_queue.TryTake(out WorkItem? pending))
        {
            pending.Fail(new ObjectDisposedException(nameof(RpcWorkerPool)));
        }

        // A worker stuck in a long invocation would otherwise throw on the disposed queue and take the process
        // down; the primitives are left alive for it and the background thread dies with the process instead.
        if (JoinWorkers())
        {
            _queue.Dispose();
            _shutdown.Dispose();
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
    // number of items ever queued while it is still growing; afterwards the permit count keeps the queue at <= cap.
    private void EnsureWorkerStarted()
    {
        int started = Volatile.Read(ref _startedThreads);
        while (started < _threads.Length)
        {
            int witnessed = Interlocked.CompareExchange(ref _startedThreads, started + 1, started);
            if (witnessed == started)
            {
                StartWorker(started);
                return;
            }

            started = witnessed;
        }
    }

    private void StartWorker(int index)
    {
        Thread thread = new(WorkerLoop)
        {
            Name = $"{_threadNamePrefix}-{index:00}",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        _threads[index] = thread;
        thread.Start();
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
