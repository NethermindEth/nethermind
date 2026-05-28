// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Nethermind.Core.Metric;

/// <summary>
/// Sink for tick-based timing measurements collected by <see cref="MetricsTimer{TMetrics}"/>.
/// Implementations forward the elapsed ticks to one or more metric counters.
/// </summary>
/// <remarks>
/// Sinks may override <see cref="IsEnabled"/> to return a JIT-foldable constant (e.g. an
/// <see cref="IFlag"/>'s <c>IsActive</c>); when it folds to <c>false</c> the wrapping
/// <see cref="MetricsTimer{TMetrics}"/> skips the <see cref="Stopwatch"/> calls entirely.
/// </remarks>
public interface IMetricSink
{
    static abstract void AddTicks(long ticks);

    /// <summary>
    /// When <c>false</c> the surrounding <see cref="MetricsTimer{TMetrics}"/> elides its
    /// <see cref="Stopwatch"/> calls and the <see cref="AddTicks"/> dispatch.
    /// Default is <c>true</c>; sinks override to gate on a global feature flag.
    /// </summary>
    static virtual bool IsEnabled => true;
}

/// <summary>
/// Times a scope and forwards the elapsed ticks to <typeparamref name="TMetrics"/> on dispose.
/// When <c>TMetrics.IsEnabled</c> folds to <c>false</c>, the JIT elides the Stopwatch reads
/// and the <see cref="Dispose"/> body. Use with a <c>using</c> declaration:
/// <code>
/// using MetricsTimer&lt;StateRootTimeSink&gt; _ = new();
/// _stateProvider.RecalculateStateRoot();
/// </code>
/// </summary>
public readonly ref struct MetricsTimer<TMetrics>() where TMetrics : IMetricSink
{
    private readonly long _start = TMetrics.IsEnabled ? Stopwatch.GetTimestamp() : 0L;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        if (!TMetrics.IsEnabled) return;
        TMetrics.AddTicks(Stopwatch.GetElapsedTime(_start).Ticks);
    }
}
