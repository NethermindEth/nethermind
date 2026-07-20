// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime;
using Nethermind.Logging;

namespace Nethermind.State.Flat;

/// <summary>
/// Induces paced gen1 (and optionally gen2/gen0) collections, skipping ticks when the runtime
/// already collected within the interval, so promotion happens in many small pauses instead of
/// rare multi-second ones.
/// </summary>
public static class GcPacer
{
    // Bootstrap-time console logger: the pacer is a process-wide static utility started before/independent
    // of the DI log pipeline, so it uses the same fallback sink as Runner startup.
    private static readonly ILogger Logger = new(SimpleConsoleLogger.Instance);

    private static int s_started;

    /// <summary>Starts the process-wide pacer threads; only the first call wins.</summary>
    /// <returns><c>true</c> when this call started the pacer, <c>false</c> when it was already running.</returns>
    public static bool Start(long intervalMs, long warmupSeconds, long gen2IntervalMs, long gen0IntervalMs)
    {
        if (Interlocked.CompareExchange(ref s_started, 1, 0) != 0) return false;

        // gen1 and gen0 pacing start independently: a gen0-only config (gen1 interval left at 0) must
        // still start the gen0 fission thread. Thread.Sleep(TimeSpan) rejects millisecond values above
        // int.MaxValue, so clamp each sleep-driving interval so an out-of-range setting can't turn a
        // paced loop into a busy exception-retry loop.
        if (intervalMs > 0)
        {
            long gen1Interval = Math.Clamp(intervalMs, 1, int.MaxValue);
            Thread thread = new(() => Run(gen1Interval, warmupSeconds * 1000, gen2IntervalMs))
            {
                // Must stay at normal priority: below-normal starves under saturated block processing.
                IsBackground = true,
                Name = "GC Pacer",
            };
            thread.Start();
        }

        if (gen0IntervalMs > 0)
        {
            long gen0Interval = Math.Clamp(gen0IntervalMs, 1, int.MaxValue);
            Thread gen0Thread = new(() => RunGen0(gen0Interval))
            {
                IsBackground = true,
                Name = "GC Pacer gen0",
            };
            gen0Thread.Start();
        }

        return true;
    }

    // Induces a paced collection unless a real no-GC region is active. Guards on GCSettings.LatencyMode
    // (the runtime's authoritative no-GC-region flag) rather than the GCScheduler gate: GCKeeper holds
    // that gate for the whole engine_newPayload even when no real region starts, so gating on it would
    // suppress gen0 fission exactly when a gigagas payload needs it; and GCScheduler.GCCollect also runs
    // a native MallocTrim that at a subsecond gen0 cadence stalls RocksDB. A real no-GC region is still
    // preserved - the tick skips so a coincident induced collection can't end it. Returns whether it ran.
    private static bool PacedCollect(int generation)
    {
        if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion) return false;
        GC.Collect(generation, GCCollectionMode.Forced, blocking: false, compacting: false);
        return true;
    }

    private static void RunGen0(long gen0IntervalMs)
    {
        int lastGen0Count = GC.CollectionCount(0);
        while (true)
        {
            try
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(gen0IntervalMs));

                if (GC.CollectionCount(0) == lastGen0Count)
                {
                    // gen0 fission must run during payload processing (its whole point is to split a
                    // gigagas payload's survivors), so it is guarded only against a real no-GC region.
                    PacedCollect(0);
                }

                lastGen0Count = GC.CollectionCount(0);
            }
            catch (Exception e)
            {
                // Never let an unhandled throw silently kill this daemon thread; keep pacing.
                if (Logger.IsError) Logger.Error("GC pacer gen0 loop threw; continuing.", e);
            }
        }
    }

    private static void Run(long intervalMs, long warmupMs, long gen2IntervalMs)
    {
        Stopwatch uptime = Stopwatch.StartNew();
        int lastGen1Count = GC.CollectionCount(1);
        int lastGen2Count = GC.CollectionCount(2);
        long lastGen2AtMs = 0;
        long pendingBgcSinceIndex = -1;
        long pendingBgcAtMs = 0;

        while (true)
        {
            try
            {
                bool warmup = uptime.ElapsedMilliseconds < warmupMs;
                Thread.Sleep(TimeSpan.FromMilliseconds(warmup ? Math.Max(1000, intervalMs / 2) : intervalMs));

                if (GC.CollectionCount(1) == lastGen1Count)
                {
                    // Must stay blocking:false: a blocking induced collection waits behind an
                    // in-flight background gen2 and wedges this thread.
                    PacedCollect(1);
                }

                lastGen1Count = GC.CollectionCount(1);

                if (gen2IntervalMs > 0)
                {
                    // GC.Collect(2) waits behind an in-flight background collection even with
                    // blocking:false; GCKind.Background's Index counts COMPLETED collections, so fire
                    // only once the previously fired one has completed (or the request went stale).
                    GCMemoryInfo background = GC.GetGCMemoryInfo(GCKind.Background);
                    if (pendingBgcSinceIndex >= 0 &&
                        (background.Index > pendingBgcSinceIndex || uptime.ElapsedMilliseconds - pendingBgcAtMs >= 180_000))
                    {
                        pendingBgcSinceIndex = -1;
                    }

                    int gen2Count = GC.CollectionCount(2);
                    if (gen2Count != lastGen2Count)
                    {
                        lastGen2Count = gen2Count;
                        lastGen2AtMs = uptime.ElapsedMilliseconds;
                    }
                    else if (pendingBgcSinceIndex < 0 &&
                             uptime.ElapsedMilliseconds - lastGen2AtMs >= (warmup ? Math.Max(gen2IntervalMs / 4, 5000) : gen2IntervalMs))
                    {
                        long bgIndexBefore = background.Index;
                        int gen2Before = GC.CollectionCount(2);
                        if (PacedCollect(2))
                        {
                            int gen2After = GC.CollectionCount(2);
                            lastGen2Count = gen2After;
                            lastGen2AtMs = uptime.ElapsedMilliseconds;

                            // A blocking:false request can still run as a full blocking gen2 (e.g. concurrent
                            // GC disabled): it completes inline and advances CollectionCount(2) synchronously
                            // while the background index never moves for it. Only latch on the background index
                            // when a real background collection was actually scheduled, otherwise pacing stays
                            // suppressed until the 180s stale timeout.
                            if (gen2After == gen2Before)
                            {
                                pendingBgcSinceIndex = bgIndexBefore;
                                pendingBgcAtMs = uptime.ElapsedMilliseconds;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                // Never let an unhandled throw silently kill this daemon thread; keep pacing.
                if (Logger.IsError) Logger.Error("GC pacer loop threw; continuing.", e);
            }
        }
    }
}
