// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// Dedicated threads that drain <see cref="StateRootStreamer"/>s.
/// </summary>
/// <remarks>
/// Not the shared thread pool: block execution keeps it saturated with prewarming for the whole block, which is
/// exactly when a streamer has to keep up. The threads run above normal priority because a streamer's work is short
/// and whatever it has not done when the block ends is done on the block processing thread instead.
/// </remarks>
public sealed class StateRootStreamThreads : IDisposable
{
    private readonly Thread[] _threads;
    private readonly ConcurrentQueue<StateRootStreamer> _ready = new();
    private readonly SemaphoreSlim _readyCount = new(0);
    // Scheduling and disposal exclude each other, so nothing is queued once the workers are told to stop, and the
    // semaphore outlives every release.
    private readonly Lock _lifecycleLock = new();
    private bool _disposed;

    public StateRootStreamThreads(int threadCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(threadCount, 1);

        _threads = new Thread[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            Thread thread = new(WorkerLoop) { IsBackground = true, Name = $"StateRoot-{i}", Priority = ThreadPriority.AboveNormal };
            _threads[i] = thread;
            thread.Start();
        }
    }

    internal bool TrySchedule(StateRootStreamer streamer)
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return false;

            _ready.Enqueue(streamer);
            _readyCount.Release();
            return true;
        }
    }

    private void WorkerLoop()
    {
        while (true)
        {
            _readyCount.Wait();
            if (_ready.TryDequeue(out StateRootStreamer? streamer))
            {
                streamer.Drain();
                continue;
            }

            // Every queued streamer holds a permit, so an empty queue on a permit is the stop signal.
            return;
        }
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
            _readyCount.Release(_threads.Length);
        }

        foreach (Thread thread in _threads) thread.Join();
        _readyCount.Dispose();
    }
}
