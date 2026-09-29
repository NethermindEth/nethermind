// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Db;

namespace Nethermind.Consensus.Tracing;

/// <summary>Bounds active indexed block-tracing workers across both RPC namespaces.</summary>
public sealed class ParallelTraceBudget : IDisposable
{
    private readonly SemaphoreSlim _slots;

    /// <summary>Creates the node-owned budget from configuration; dispose after all dependent tracers.</summary>
    public ParallelTraceBudget(IFlatDbConfig config) : this(config.HistoryTransactionIndexTraceParallelism)
    {
    }

    /// <summary>Creates a budget capped at sixteen workers; zero selects the processor count.</summary>
    public ParallelTraceBudget(int degree)
    {
        Degree = Math.Clamp(degree == 0 ? Environment.ProcessorCount : degree, 1, 16);
        _slots = new SemaphoreSlim(Degree, Degree);
    }

    /// <summary>Maximum concurrent permit holders across every tracer sharing this budget.</summary>
    public int Degree { get; }
    /// <summary>Acquires one permit or throws on cancellation. Pair a successful acquisition with Release.</summary>
    public void Wait(CancellationToken token) => _slots.Wait(token);
    /// <summary>Returns one previously acquired permit.</summary>
    public void Release() => _slots.Release();

    /// <summary>Releases the semaphore after dependent tracers have stopped using it.</summary>
    public void Dispose() => _slots.Dispose();
}
