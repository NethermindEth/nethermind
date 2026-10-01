// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.BeaconChain.Threading;

/// <summary>Runs the plugin's data-parallel loops on dedicated threads instead of the thread pool.</summary>
/// <remarks>
/// <see cref="Parallel.For(int, int, Action{int})"/> on the default scheduler queues its helper tasks to the thread pool and waits for
/// each one to be dequeued. On a thread that is not a pool thread, such as the block import thread, it cannot run a queued helper itself,
/// so while the pool is starved every loop waits as long as the whole pool queue. Loops here never wait on the pool, and never add to it.
/// </remarks>
internal static class BeaconParallel
{
    private static readonly ParallelOptions DefaultOptions = new() { TaskScheduler = ComputeScheduler.Instance };

    /// <summary>The loops started on the current thread; lets tests see that a code path uses these threads.</summary>
    [ThreadStatic]
    internal static int LoopsStartedOnThisThread;

    public static void For(int fromInclusive, int toExclusive, Action<int> body)
    {
        LoopsStartedOnThisThread++;
        Parallel.For(fromInclusive, toExclusive, DefaultOptions, body);
    }

    public static void For(int fromInclusive, int toExclusive, Action<int, ParallelLoopState> body)
    {
        LoopsStartedOnThisThread++;
        Parallel.For(fromInclusive, toExclusive, DefaultOptions, body);
    }

    /// <param name="maxDegreeOfParallelism">The most threads the loop uses, the calling one included.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired.</exception>
    public static void For(int fromInclusive, int toExclusive, int maxDegreeOfParallelism, CancellationToken cancellationToken, Action<int> body)
    {
        LoopsStartedOnThisThread++;
        ParallelOptions options = new() { TaskScheduler = ComputeScheduler.Instance, MaxDegreeOfParallelism = maxDegreeOfParallelism, CancellationToken = cancellationToken };
        Parallel.For(fromInclusive, toExclusive, options, body);
    }

    public static void ForEach<T>(IEnumerable<T> source, Action<T> body)
    {
        LoopsStartedOnThisThread++;
        Parallel.ForEach(source, DefaultOptions, body);
    }

    /// <summary>One background thread per processor, started on first use and kept for the life of the process.</summary>
    private sealed class ComputeScheduler : TaskScheduler
    {
        public static readonly ComputeScheduler Instance = new(Environment.ProcessorCount);

        private readonly BlockingCollection<Task> _tasks = [];
        private readonly int _threads;

        private ComputeScheduler(int threads)
        {
            _threads = threads;
            for (int i = 0; i < threads; i++)
            {
                new Thread(Run) { IsBackground = true, Name = $"Beacon compute {i + 1}" }.Start();
            }
        }

        public override int MaximumConcurrencyLevel => _threads;

        private void Run()
        {
            foreach (Task task in _tasks.GetConsumingEnumerable())
            {
                // TryExecuteTask stores a fault in the task, and the waiting loop rethrows it.
                TryExecuteTask(task);
            }
        }

        protected override void QueueTask(Task task) => _tasks.Add(task);

        // A queued helper a waiting loop runs itself is claimed once; the thread that dequeues it later finds it done. This keeps a
        // nested loop on these threads from waiting for a thread that is itself waiting.
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => TryExecuteTask(task);

        protected override IEnumerable<Task> GetScheduledTasks() => _tasks.ToArray();
    }
}
