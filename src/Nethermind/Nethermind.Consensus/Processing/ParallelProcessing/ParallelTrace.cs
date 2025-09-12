// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Nethermind.Core.Extensions;

namespace Nethermind.Consensus.Processing.ParallelProcessing;

public class ParallelTrace<TLogger> where TLogger : struct, IIsTracing
{
    private long _counter = 0;
    private ConcurrentQueue<(long, DateTime, string)>? _traces;

    private ConcurrentQueue<(long, DateTime, string)> Traces => typeof(TLogger) == typeof(IsTracing)
        ? LazyInitializer.EnsureInitialized(ref _traces, static () => new ConcurrentQueue<(long, DateTime, string)>())
        : throw new InvalidOperationException();

    public long ReserveId() => typeof(TLogger) == typeof(IsTracing) ? Interlocked.Increment(ref _counter) : 0;

    public void Add(string eventString)
    {
        if (typeof(TLogger) == typeof(IsTracing)) Add(ReserveId(), eventString);
    }

    public void Add(long id, string eventString)
    {
        if (typeof(TLogger) == typeof(IsTracing)) Traces.Enqueue((id, DateTime.Now, eventString));
    }

    public (long, DateTime, string)[]? GetTraces() => typeof(TLogger) == typeof(IsTracing) ? Traces.OrderBy(t => t.Item1).ToArray() : null;

    public string Format<TData>(TData data) => data is byte[]  bytes ? bytes.ToHexString() : data?.ToString() ?? "";
}

public abstract class ParallelTrace : ParallelTrace<NotTracing>
{
    public static ParallelTrace<NotTracing> Empty = new();
}


