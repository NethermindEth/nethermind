// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime;
using System.Threading;
using Nethermind.Logging;

namespace Nethermind.Core.Memory;

/// <summary>
/// Diagnostic-only measurement of garbage collections around block processing and the no-GC region's lifecycle.
/// Not for merging: it logs a line per block and per region at info level.
/// </summary>
public static class GcBlockDiagnostics
{
    private static long _regionRequestedAt;
    private static long _regionStartedAt;
    private static int _regionStartResult;

    public readonly record struct Snapshot(long Timestamp, int Gen0, int Gen1, int Gen2, TimeSpan Pause, bool RegionActive)
    {
        public static Snapshot Take() => new(
            Stopwatch.GetTimestamp(),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            GC.GetTotalPauseDuration(),
            GCSettings.LatencyMode == GCLatencyMode.NoGCRegion);
    }

    public static void RegionRequested()
    {
        Volatile.Write(ref _regionStartedAt, 0);
        Volatile.Write(ref _regionStartResult, 0);
        Volatile.Write(ref _regionRequestedAt, Stopwatch.GetTimestamp());
    }

    /// <summary>Records a region entry and logs what the entry itself cost.</summary>
    public static void RegionEntered(Snapshot before, bool started, ILogger logger)
    {
        Snapshot after = Snapshot.Take();
        Volatile.Write(ref _regionStartedAt, after.Timestamp);
        Volatile.Write(ref _regionStartResult, started ? 1 : 2);
        long requestedAt = Volatile.Read(ref _regionRequestedAt);
        if (logger.IsInfo)
        {
            logger.Info($"GCDIAG region-entry started={started} queued-to-entry {Ms(requestedAt, before.Timestamp):F2} ms entry {Ms(before.Timestamp, after.Timestamp):F2} ms " +
                $"gc0 +{after.Gen0 - before.Gen0} gc1 +{after.Gen1 - before.Gen1} gc2 +{after.Gen2 - before.Gen2} pause +{(after.Pause - before.Pause).TotalMilliseconds:F2} ms");
        }
    }

    /// <summary>Logs whether the runtime still held the region when the payload released it.</summary>
    public static void RegionReleased(bool startedByUs, bool runtimeStillActive, ILogger logger)
    {
        if (logger.IsInfo && startedByUs)
        {
            logger.Info($"GCDIAG region-release runtime-still-active={runtimeStillActive}");
        }
    }

    public static void RegionSkipped(string reason, ILogger logger)
    {
        Volatile.Write(ref _regionStartResult, 3);
        if (logger.IsInfo) logger.Info($"GCDIAG region-skipped {reason}");
    }

    /// <summary>Logs the collections and pause time that fell inside one block's processing.</summary>
    public static void BlockProcessed(ulong blockNumber, Snapshot before, ILogger logger)
    {
        if (!logger.IsInfo) return;

        Snapshot after = Snapshot.Take();
        long startedAt = Volatile.Read(ref _regionStartedAt);
        int result = Volatile.Read(ref _regionStartResult);
        string entry = startedAt == 0 ? "none" : $"{Ms(before.Timestamp, startedAt):F2}";
        logger.Info($"GCDIAG block {blockNumber} proc {Ms(before.Timestamp, after.Timestamp):F2} ms " +
            $"gc0 +{after.Gen0 - before.Gen0} gc1 +{after.Gen1 - before.Gen1} gc2 +{after.Gen2 - before.Gen2} " +
            $"pause {(after.Pause - before.Pause).TotalMilliseconds:F2} ms region-at-start {before.RegionActive} region-at-end {after.RegionActive} " +
            $"region-entry-vs-start {entry} ms result {result}");
    }

    private static double Ms(long from, long to) => Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;
}
