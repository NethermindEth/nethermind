// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.Blockchain.Tracing.ParityStyle;

/// <summary>
/// Counts the bytes of trace data that tracers keep in memory for a response, and fails the trace when they pass
/// <see cref="Limit"/>. One budget can be shared by the tracers of one request, also by tracers that run in parallel:
/// its members are thread-safe.
/// </summary>
/// <param name="limit">The most bytes that may be kept.</param>
/// <param name="gate">A limit that this budget shares with others: it is passed every byte charged and given back,
/// and may hold a charge back until there is room for it.</param>
public sealed class TraceDataBudget(long limit, ITraceDataGate? gate = null)
{
    private long _used;

    /// <summary>The most bytes that may be kept.</summary>
    public long Limit => limit;

    /// <summary>The bytes charged now.</summary>
    public long Used => Interlocked.Read(ref _used);

    /// <summary>Charges <paramref name="bytes"/> kept bytes; with a gate, first waits until they fit within its limit.</summary>
    /// <exception cref="TraceDataLimitExceededException">The bytes charged so far are more than <see cref="Limit"/>.</exception>
    public void Add(int bytes)
    {
        bool fits = Interlocked.Add(ref _used, bytes) <= limit;
        // The gate counts the bytes also when the charge fails, so that giving them back balances it.
        gate?.Charge(bytes, mayWait: fits);
        if (!fits) throw new TraceDataLimitExceededException(limit);
    }

    /// <summary>
    /// Gives back <paramref name="bytes"/> that a tracer charged and no longer keeps, such as the trace of a transaction
    /// that a streamed response has written.
    /// </summary>
    public void Release(long bytes)
    {
        Interlocked.Add(ref _used, -bytes);
        gate?.Release(bytes);
    }

    /// <summary>Charges <paramref name="bytes"/> only when they fit within <see cref="Limit"/>, and within the gate's limit without waiting.</summary>
    /// <returns>Whether the bytes were charged.</returns>
    public bool TryAdd(long bytes)
    {
        long used = Volatile.Read(ref _used);
        while (true)
        {
            if (used + bytes > limit) return false;
            long seen = Interlocked.CompareExchange(ref _used, used + bytes, used);
            if (seen == used) break;
            used = seen;
        }

        if (gate is null || gate.TryCharge(bytes)) return true;
        Interlocked.Add(ref _used, -bytes);
        return false;
    }
}
