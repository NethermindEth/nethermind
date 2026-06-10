// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// Persistent pool of dedicated reader threads that drain a per-call job set via a shared cursor.
/// Intended for the BAL read-warming pump in <see cref="FlatWorldStateScope"/>.
/// </summary>
/// <remarks>
/// Threads are spawned once at construction and parked on a semaphore between batches. Each
/// <see cref="Drain"/> call selects up to <see cref="MaxConcurrency"/> of those threads, hands
/// them a shared <see cref="Interlocked.Increment(ref int)"/> cursor over the batch, and waits
/// for them all to finish before returning.
///
/// Why dedicated OS threads rather than the shared thread pool: block execution keeps the pool
/// saturated for the whole block, so pooled warm reads are starved exactly when they matter,
/// and the pool's slow thread injection never reaches the oversubscribed concurrency a
/// disk-latency-bound drain needs.
///
/// Why one persistent pool rather than spawning <see cref="TaskCreationOptions.LongRunning"/>
/// tasks per block: each block's warmup would otherwise create-and-join up to ~64 OS threads,
/// which is significant overhead at mainnet block cadence. Persistent threads amortize that.
///
/// One <see cref="Drain"/> call at a time. Concurrent callers will see the second drain block
/// until the first completes (guarded by an interlocked sentinel).
/// </remarks>
public sealed class BalReaderPool : IDisposable
{
    public int MaxConcurrency { get; }

    private readonly Thread[] _threads;
    private readonly SemaphoreSlim _workAvailable;
    private Batch? _current;
    private int _drainInFlight; // 0 = idle, 1 = a Drain owns _current
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

    public BalReaderPool(int maxConcurrency)
    {
        if (maxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));

        MaxConcurrency = maxConcurrency;
        _threads = new Thread[maxConcurrency];
        _workAvailable = new SemaphoreSlim(0);
        for (int i = 0; i < maxConcurrency; i++)
        {
            Thread t = new(WorkerLoop) { IsBackground = true, Name = $"BalReader-{i}" };
            _threads[i] = t;
            t.Start();
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> for each <c>j</c> in <c>[0, jobCount)</c> across up to
    /// <paramref name="workers"/> dedicated reader threads. Blocks until every job has been
    /// claimed (executed, or skipped on cancellation). The first exception observed in any
    /// worker is rethrown after the batch completes.
    /// </summary>
    /// <param name="jobCount">Number of jobs.</param>
    /// <param name="workers">Number of reader threads to wake for this batch. Clamped to
    /// <see cref="MaxConcurrency"/>.</param>
    /// <param name="work">Per-job action; receives the job index.</param>
    /// <param name="token">Cancels the cursor: workers stop pulling new jobs once observed.
    /// Jobs already claimed run to completion.</param>
    public void Drain(int jobCount, int workers, Action<int> work, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (jobCount <= 0) return;

        int activeWorkers = Math.Clamp(workers, 1, MaxConcurrency);

        // Single-batch invariant: only one Drain may own _current at a time.
        while (Interlocked.CompareExchange(ref _drainInFlight, 1, 0) != 0)
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
            Volatile.Write(ref _drainInFlight, 0);
        }
    }

    private void WorkerLoop()
    {
        while (true)
        {
            _workAvailable.Wait();
            if (_disposed) return;

            Batch? batch = _current;
            if (batch is null) continue; // spurious wake

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
