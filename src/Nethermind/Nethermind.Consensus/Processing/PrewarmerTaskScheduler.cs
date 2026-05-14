// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Threading;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Dedicated thread pool for prewarming work. Keeps prewarmer threads off
/// the shared .NET ThreadPool so they don't compete with block processing,
/// RPC, networking, and post-tx parallel work (blooms, receipts root, state root).
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
            _threads[i] = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"Prewarmer-{i}",
                Priority = ThreadPriority.BelowNormal
            };
            _threads[i].Start();
        }
    }

    private void WorkerLoop()
    {
        ProcessingThread.IsBlockProcessingThread = false;
        foreach (Task task in _tasks.GetConsumingEnumerable())
        {
            TryExecuteTask(task);
        }
    }

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
