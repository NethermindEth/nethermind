// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Threading;

namespace Nethermind.Db.Rocks;

/// <summary>
/// Decides whether asynchronous prefetches of one column family are worth sending, from how long recent
/// prefetch batches took. A batch the block cache serves completes in well under <see cref="ColdBatchMicroseconds"/>;
/// one that reads from disk takes longer. While recent batches were fast, prefetches are skipped except every
/// <see cref="ProbeInterval"/>th one, which keeps measuring so the gate reopens when reads turn cold.
/// </summary>
/// <remarks>Updates are approximate under concurrency; the gate is a heuristic, never a correctness decision.</remarks>
internal sealed class PrefetchGate
{
    internal const double ColdBatchMicroseconds = 400;
    internal const int ProbeInterval = 16;
    private const int ScoreScale = 1024;
    private const int OpenScore = ScoreScale / 4;

    // Exponential average of the cold share of recent batches, starting as cold so a new database prefetches.
    private int _coldScore = ScoreScale;
    private int _skipped;

    public bool ShouldPrefetch() =>
        Volatile.Read(ref _coldScore) >= OpenScore || Interlocked.Increment(ref _skipped) % ProbeInterval == 0;

    public void Record(long startTimestamp) =>
        Record(Stopwatch.GetElapsedTime(startTimestamp).TotalMicroseconds > ColdBatchMicroseconds);

    internal void Record(bool cold)
    {
        int score = Volatile.Read(ref _coldScore);
        Volatile.Write(ref _coldScore, score + ((cold ? ScoreScale : 0) - score) / 8);
        if (cold) Metrics.IncrementPrefetchColdBatches();
        else Metrics.IncrementPrefetchWarmBatches();
    }
}
