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
    private volatile bool _disposed;

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
        if (_disposed) return false;

        _ready.Enqueue(streamer);
        _readyCount.Release();
        return true;
    }

    private void WorkerLoop()
    {
        while (true)
        {
            _readyCount.Wait();
            if (_ready.TryDequeue(out StateRootStreamer? streamer)) streamer.Drain();

            // A queued streamer counts as running until drained, and its scope waits for that.
            if (_disposed && _ready.IsEmpty) return;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _readyCount.Release(_threads.Length);
        foreach (Thread thread in _threads) thread.Join();
        _readyCount.Dispose();
    }
}
