// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// Persistent pool of dedicated reader threads for the BAL read-warming pump in
/// <see cref="FlatWorldStateScope"/>.
/// </summary>
/// <remarks>
/// Dedicated OS threads rather than the shared thread pool because block execution keeps
/// the pool saturated for the whole block, so pooled warm reads are starved exactly when
/// they matter, and the pool's slow thread injection never reaches the oversubscribed
/// concurrency a disk-latency-bound drain needs. One persistent pool rather than per-block
/// <see cref="TaskCreationOptions.LongRunning"/> tasks so the ~64 OS thread create/joins
/// each block don't recur at mainnet cadence.
///
/// One <see cref="Run"/> call at a time -- concurrent callers serialize via an interlocked
/// sentinel.
/// </remarks>
public sealed class WarmReadPool : IDisposable
{
    public int MaxConcurrency { get; }

    private readonly Thread[] _threads;
    private readonly SemaphoreSlim _workAvailable;
    private Batch? _current;
    private int _runInFlight;
    private volatile bool _disposed;

    private sealed class Batch
    {
        public Action<int> Work = null!;
        public int JobCount;
        public int NextIndex = -1;
        public CountdownEvent Done = null!;
        public CancellationToken Token;
        public Exception? FirstException;
    }

    public WarmReadPool(int maxConcurrency)
    {
        if (maxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));

        MaxConcurrency = maxConcurrency;
        _threads = new Thread[maxConcurrency];
        _workAvailable = new SemaphoreSlim(0);
        for (int i = 0; i < maxConcurrency; i++)
        {
            Thread t = new(WorkerLoop) { IsBackground = true, Name = $"WarmRead-{i}" };
            _threads[i] = t;
            t.Start();
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> for each <c>j</c> in <c>[0, jobCount)</c> across up to
    /// <paramref name="workers"/> dedicated reader threads (clamped to <see cref="MaxConcurrency"/>).
    /// Blocks until every job has been claimed. Cancellation stops new claims but in-flight
    /// jobs run to completion. The first worker exception is rethrown after the batch joins.
    /// </summary>
    public void Run(int jobCount, int workers, Action<int> work, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (jobCount <= 0) return;

        int activeWorkers = Math.Clamp(workers, 1, MaxConcurrency);

        while (Interlocked.CompareExchange(ref _runInFlight, 1, 0) != 0)
        {
            Thread.Yield();
        }

        Batch batch = new()
        {
            Work = work,
            JobCount = jobCount,
            Token = token,
            Done = new CountdownEvent(activeWorkers),
        };

        try
        {
            _current = batch;
            _workAvailable.Release(activeWorkers);
            batch.Done.Wait();
            if (batch.FirstException is not null) throw batch.FirstException;
        }
        finally
        {
            _current = null;
            batch.Done.Dispose();
            Volatile.Write(ref _runInFlight, 0);
        }
    }

    private void WorkerLoop()
    {
        while (true)
        {
            _workAvailable.Wait();
            if (_disposed) return;

            Batch? batch = _current;
            if (batch is null) continue;

            try
            {
                while (!batch.Token.IsCancellationRequested)
                {
                    int j = Interlocked.Increment(ref batch.NextIndex);
                    if (j >= batch.JobCount) break;
                    batch.Work(j);
                }
            }
            catch (Exception ex)
            {
                // Latch the first exception; subsequent workers carry on so the cursor still
                // drains and Done can be signaled.
                Interlocked.CompareExchange(ref batch.FirstException, ex, null);
            }
            finally
            {
                batch.Done.Signal();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workAvailable.Release(_threads.Length);
        foreach (Thread t in _threads) t.Join();
        _workAvailable.Dispose();
    }
}
