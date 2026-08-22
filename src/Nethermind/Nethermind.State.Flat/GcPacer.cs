// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Flat;

/// <summary>
/// Induces paced gen1 (and optionally gen2/gen0) collections, skipping ticks when the runtime
/// already collected within the interval, so promotion happens in many small pauses instead of
/// rare multi-second ones.
/// </summary>
public sealed class GcPacer(IFlatDbConfig flatConfig, ILogManager logManager) : IDisposable
{
    private readonly ILogger _logger = logManager.GetClassLogger<GcPacer>();
    private readonly CancellationTokenSource _cancellation = new();

    private Thread? _gen1Thread;
    private Thread? _gen0Thread;
    private int _started;
    private int _disposed;

    /// <summary>Starts the pacer threads for the configured cadences; only the first call wins.</summary>
    /// <returns><c>true</c> when this call started the pacer, <c>false</c> when pacing is disabled by
    /// configuration or was already started.</returns>
    public bool TryStart()
    {
        long intervalMs = flatConfig.GcPaceIntervalMs;
        long gen0IntervalMs = flatConfig.GcPaceGen0IntervalMs;

        // gen1 and gen0 pacing start independently: a gen0-only config (gen1 interval left at 0) must
        // still start the gen0 fission thread.
        if (intervalMs <= 0 && gen0IntervalMs <= 0) return false;
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return false;

        CancellationToken token = _cancellation.Token;

        // The timed wait rejects millisecond values above int.MaxValue, so clamp each sleep-driving
        // interval so an out-of-range setting can't turn a paced loop into a busy exception-retry loop.
        if (intervalMs > 0)
        {
            long gen1Interval = Math.Clamp(intervalMs, 1, int.MaxValue);
            long warmupMs = flatConfig.GcPaceWarmupSeconds * 1000;
            long gen2IntervalMs = flatConfig.GcPaceGen2IntervalMs;
            _gen1Thread = new(() => Run(gen1Interval, warmupMs, gen2IntervalMs, token))
            {
                // Must stay at normal priority: below-normal starves under saturated block processing.
                IsBackground = true,
                Name = "GC Pacer",
            };
            _gen1Thread.Start();
        }

        if (gen0IntervalMs > 0)
        {
            long gen0Interval = Math.Clamp(gen0IntervalMs, 1, int.MaxValue);
            _gen0Thread = new(() => RunGen0(gen0Interval, token))
            {
                IsBackground = true,
                Name = "GC Pacer gen0",
            };
            _gen0Thread.Start();
        }

        return true;
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;

        _cancellation.Cancel();
        _gen1Thread?.Join();
        _gen0Thread?.Join();
        _cancellation.Dispose();
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

    private void RunGen0(long gen0IntervalMs, CancellationToken token)
    {
        int lastGen0Count = GC.CollectionCount(0);
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(gen0IntervalMs))) return;

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
                if (_logger.IsError) _logger.Error("GC pacer gen0 loop threw; continuing.", e);
            }
        }
    }

    private void Run(long intervalMs, long warmupMs, long gen2IntervalMs, CancellationToken token)
    {
        Stopwatch uptime = Stopwatch.StartNew();
        int lastGen1Count = GC.CollectionCount(1);
        int lastGen2Count = GC.CollectionCount(2);
        long lastGen2AtMs = 0;
        long pendingBgcSinceIndex = -1;
        long pendingBgcAtMs = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                bool warmup = uptime.ElapsedMilliseconds < warmupMs;
                if (token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(warmup ? Math.Max(1000, intervalMs / 2) : intervalMs))) return;

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
                if (_logger.IsError) _logger.Error("GC pacer loop threw; continuing.", e);
            }
        }
    }
}
