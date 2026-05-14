// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Threading;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Dedicated thread pool for prewarming work. Keeps prewarmer threads off
/// the shared .NET ThreadPool so they don't compete with block processing.
///
/// On Linux, pins prewarmer threads to the upper half of available CPUs
/// (leaving the lower half for block processing). This prevents L1/L2
/// cache thrashing between prewarmer EVM and real block processing EVM.
/// </summary>
public sealed class PrewarmerTaskScheduler : TaskScheduler, IDisposable
{
    private readonly BlockingCollection<Task> _tasks = new();
    private readonly Thread[] _threads;
    private bool _disposed;

    public PrewarmerTaskScheduler(int threadCount)
    {
        _threads = new Thread[threadCount];
        for (int i = 0; i < threadCount; i++)
        {
            int threadIndex = i;
            _threads[i] = new Thread(() => WorkerLoop(threadIndex))
            {
                IsBackground = true,
                Name = $"Prewarmer-{i}",
                Priority = ThreadPriority.BelowNormal
            };
            _threads[i].Start();
        }
    }

    private void WorkerLoop(int threadIndex)
    {
        ProcessingThread.IsBlockProcessingThread = false;
        TrySetCpuAffinity(threadIndex);
        foreach (Task task in _tasks.GetConsumingEnumerable())
        {
            TryExecuteTask(task);
        }
    }

    private static void TrySetCpuAffinity(int threadIndex)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;

        try
        {
            // Pin prewarmer threads to the upper half of available CPUs.
            // Block processing (main thread) naturally runs on lower CPUs,
            // so this creates physical separation and prevents cache thrashing.
            int cpuCount = Environment.ProcessorCount;
            int targetCpu = (cpuCount / 2) + (threadIndex % (cpuCount / 2));

            ulong mask = 1UL << targetCpu;
            int tid = Gettid(); // current thread's kernel TID
            SchedSetaffinity(tid, (nuint)sizeof(ulong), ref mask);
        }
        catch
        {
            // Affinity is best-effort — don't fail if not supported
        }
    }

    [DllImport("libc", EntryPoint = "sched_setaffinity", SetLastError = true)]
    private static extern int SchedSetaffinity(int pid, nuint cpusetsize, ref ulong mask);

    [DllImport("libc", EntryPoint = "gettid")]
    private static extern int Gettid();

    protected override void QueueTask(Task task) => _tasks.Add(task);

    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

    protected override IEnumerable<Task> GetScheduledTasks() => _tasks;

    public override int MaximumConcurrencyLevel => _threads.Length;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tasks.CompleteAdding();
    }
}
