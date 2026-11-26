// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.Core.Metric;

/// <summary>A counter cell that a hot path resolves once and then updates atomically, exported by its <see cref="Value"/>.</summary>
public sealed class MetricCounter
{
    private long _value;

    public long Value => Volatile.Read(ref _value);

    public void Increment() => Interlocked.Increment(ref _value);

    public void Add(long value) => Interlocked.Add(ref _value, value);
}
