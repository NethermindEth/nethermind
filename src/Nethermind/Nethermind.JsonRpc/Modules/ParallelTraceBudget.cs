// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Db;

namespace Nethermind.JsonRpc.Modules;

/// <summary>Bounds active indexed block-tracing workers across both RPC namespaces.</summary>
public sealed class ParallelTraceBudget : IDisposable
{
    public ParallelTraceBudget(IFlatDbConfig config)
    {
        Degree = Math.Clamp(config.HistoryTransactionIndexTraceParallelism == 0
            ? Environment.ProcessorCount : config.HistoryTransactionIndexTraceParallelism, 1, 16);
        Slots = new SemaphoreSlim(Degree, Degree);
    }

    public int Degree { get; }
    public SemaphoreSlim Slots { get; }

    public void Dispose() => Slots.Dispose();
}
