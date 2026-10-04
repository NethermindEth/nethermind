// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;

namespace Nethermind.Core.Threading;

/// <summary>
/// Experiment (NETHERMIND_EXP_DIRECT_RUNNERS=N): parked threads that a fan-out wakes directly, one signal per runner, instead
/// of queueing runners to the thread pool, which wakes its workers one after another. Falls back to the pool when none
/// is idle.
/// </summary>
internal static class DirectRunnerPool
{
    private sealed class Runner
    {
        public readonly SemaphoreSlim Signal = new(0);
        public IThreadPoolWorkItem? Work;
    }

    private static readonly Lock s_lock = new();
    private static readonly Stack<Runner> s_idle = new();
    private static int s_started;

    public static readonly int Count = Diagnostics.ExperimentKnobs.DirectRunners;

    public static void Queue(IThreadPoolWorkItem work)
    {
        EnsureStarted();
        Runner? runner;
        lock (s_lock)
        {
            s_idle.TryPop(out runner);
        }

        if (runner is null)
        {
            ThreadPool.UnsafeQueueUserWorkItem(work, preferLocal: false);
            return;
        }

        runner.Work = work;
        runner.Signal.Release();
    }

    private static void EnsureStarted()
    {
        if (Volatile.Read(ref s_started) != 0 || Interlocked.Exchange(ref s_started, 1) != 0) return;
        for (int i = 0; i < Count; i++)
        {
            Runner runner = new();
            Thread thread = new(() => Loop(runner)) { IsBackground = true, Name = "Direct runner" };
            thread.Start();
        }
    }

    private static void Loop(Runner runner)
    {
        while (true)
        {
            lock (s_lock)
            {
                s_idle.Push(runner);
            }

            runner.Signal.Wait();
            IThreadPoolWorkItem? work = runner.Work;
            runner.Work = null;
            if (work is null) continue;
            try
            {
                work.Execute();
            }
            catch (Exception)
            {
                // A runner's work reports its own faults; this thread must survive to be parked again.
            }
        }
    }
}
