// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Threading;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Collects per-transaction timing for each block-processing loop.
/// </summary>
/// <remarks>
/// The block-processing thread prepares and takes its own buffer. Parallel workers record into the
/// buffer supplied by their executor. The executor joins the workers before the owner takes the buffer.
/// </remarks>
public static class PerTxTimingCollector
{
    private static volatile bool _enabled;
    // Backing storage is pooled via ArrayPoolList<long>; the underlying array is rented
    // from the shared pool and returned on Dispose. The 256-slot capacity floor keeps
    // small blocks in a single pool bucket to avoid churn.
    [ThreadStatic]
    private static ArrayPoolList<long>? _ticksPerTx;

    /// <summary>Whether timing capture is enabled on the calling block-processing thread.</summary>
    public static bool IsEnabled => _enabled && ProcessingThread.IsBlockProcessingThread;

    /// <summary>Called by ProcessingStats to enable/disable capture.</summary>
    public static void SetEnabled(bool enabled) => _enabled = enabled;

    /// <summary>Called by the tx executor before processing transactions.</summary>
    public static ArrayPoolList<long>? Prepare(int txCount)
    {
        if (!IsEnabled) return null;

        DiscardPending();
        return _ticksPerTx = new ArrayPoolList<long>(capacity: Math.Max(txCount, 256), count: txCount);
    }

    /// <summary>Called by the tx executor after each transaction.</summary>
    public static void Record(ArrayPoolList<long> timings, int index, long ticks)
    {
        Span<long> span = timings.AsSpan();
        if ((uint)index < (uint)span.Length) span[index] = ticks;
    }

    /// <summary>
    /// Hands off the per-tx timing list to the caller, or returns null if no data was captured.
    /// The caller takes ownership and MUST dispose the list (returns its backing array to the
    /// shared pool). The internal reference is cleared so the next <see cref="Prepare"/> rents
    /// a fresh backing array.
    /// </summary>
    public static ArrayPoolList<long>? Snapshot()
    {
        ArrayPoolList<long>? handoff = _ticksPerTx;
        if (!IsEnabled || handoff is null || handoff.Count == 0) return null;
        _ticksPerTx = null;
        return handoff;
    }

    internal static void DiscardPending()
    {
        _ticksPerTx?.Dispose();
        _ticksPerTx = null;
    }
}
