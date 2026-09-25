// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime;
using System.Threading;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Core.Memory;

/// <summary>
/// Induces paced gen0, gen1 and optionally gen2 collections, skipping ticks when the runtime
/// already collected within the interval, so promotion happens in many small pauses instead of
/// rare multi-second ones.
/// </summary>
/// <remarks>
/// Gen1 and gen2 collections go through <see cref="GCScheduler"/> without native memory trimming, so they
/// honour its exclusion gate and a paced gen2 restarts its sustained-sweep allocation budget. Gen0 collections
/// bypass that gate because it is held for the whole <c>engine_newPayload</c> call, and splitting a payload's
/// promotion is what they are for; they still skip while a no-GC region is active.
/// </remarks>
public sealed class GcPacer : IDisposable
{
    private const long MinWarmupGen1IntervalMs = 1000;
    private const long MinWarmupGen2IntervalMs = 5000;
    private const long PendingBackgroundGcTimeoutMs = 180_000;
    private static readonly long Gen0MinAllocatedBytes = 16.MiB;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly long _gen0IntervalMs;
    private readonly long _gen1IntervalMs;
    private readonly long _gen2IntervalMs;
    private readonly long _warmupMs;
    private readonly GCScheduler _scheduler;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cancellation = new();

    private Thread? _gen1Thread;
    private Thread? _gen0Thread;
    private int _started;
    private int _disposed;

    /// <param name="gen0IntervalMs">Gen0 cadence in milliseconds; non-positive disables it.</param>
    /// <param name="gen1IntervalMs">Gen1 cadence in milliseconds; non-positive disables gen1 and gen2 pacing.</param>
    /// <param name="gen2IntervalMs">Gen2 cadence in milliseconds; non-positive disables it.</param>
    /// <param name="warmupSeconds">Seconds from start during which the gen1 and gen2 cadences are shortened.</param>
    /// <param name="logManager">The log manager.</param>
    public GcPacer(long gen0IntervalMs, long gen1IntervalMs, long gen2IntervalMs, long warmupSeconds, ILogManager logManager)
        : this(gen0IntervalMs, gen1IntervalMs, gen2IntervalMs, warmupSeconds, logManager, GCScheduler.Instance)
    {
    }

    internal GcPacer(long gen0IntervalMs, long gen1IntervalMs, long gen2IntervalMs, long warmupSeconds, ILogManager logManager, GCScheduler scheduler)
    {
        _gen0IntervalMs = gen0IntervalMs;
        _gen1IntervalMs = gen1IntervalMs;
        _gen2IntervalMs = gen2IntervalMs;
        _warmupMs = warmupSeconds * 1000;
        _scheduler = scheduler;
        _logger = logManager.GetClassLogger<GcPacer>();
    }

    internal bool IsRunning => (_gen1Thread?.IsAlive ?? false) || (_gen0Thread?.IsAlive ?? false);

    /// <summary>Starts the pacer threads for the configured cadences; only the first call wins.</summary>
    /// <returns><c>true</c> when this call started the pacer, <c>false</c> when pacing is disabled by
    /// configuration or was already started.</returns>
    public bool TryStart()
    {
        if (_gen1IntervalMs <= 0 && _gen0IntervalMs <= 0) return false;
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return false;

        CancellationToken token = _cancellation.Token;

        if (_gen1IntervalMs > 0)
        {
            _gen1Thread = new(() => RunGen1(ClampIntervalMs(_gen1IntervalMs), token))
            {
                IsBackground = true,
                Name = "GC Pacer gen1",
            };
            _gen1Thread.Start();
        }

        if (_gen0IntervalMs > 0)
        {
            _gen0Thread = new(() => RunGen0(ClampIntervalMs(_gen0IntervalMs), token))
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
        bool gen1Stopped = _gen1Thread?.Join(StopTimeout) ?? true;
        bool gen0Stopped = _gen0Thread?.Join(StopTimeout) ?? true;
        if (gen1Stopped && gen0Stopped) _cancellation.Dispose();
    }

    /// <summary>Bounds a configured interval to what <see cref="WaitHandle.WaitOne(TimeSpan)"/> accepts.</summary>
    internal static long ClampIntervalMs(long intervalMs) => Math.Clamp(intervalMs, 1, int.MaxValue);

    /// <summary>Returns the gen1 wait, halved during warm-up but never below one second.</summary>
    internal static long Gen1IntervalMs(long gen1IntervalMs, bool warmup) =>
        warmup ? Math.Max(MinWarmupGen1IntervalMs, gen1IntervalMs / 2) : gen1IntervalMs;

    /// <summary>Returns the gen2 cadence, quartered during warm-up but never below five seconds.</summary>
    internal static long Gen2IntervalMs(long gen2IntervalMs, bool warmup) =>
        warmup ? Math.Max(MinWarmupGen2IntervalMs, gen2IntervalMs / 4) : gen2IntervalMs;

    /// <summary>Whether a gen0 tick should collect: only when the runtime did not and enough was allocated to be worth a pause.</summary>
    internal static bool IsGen0Due(bool collectedSinceLastTick, long allocatedSinceLastTick) =>
        !collectedSinceLastTick && allocatedSinceLastTick >= Gen0MinAllocatedBytes;

    /// <summary>Whether a paced gen2 should start, given the time since the last observed gen2.</summary>
    internal static bool IsGen2Due(long sinceLastGen2Ms, long gen2IntervalMs, bool warmup, bool backgroundGcPending) =>
        !backgroundGcPending && sinceLastGen2Ms >= Gen2IntervalMs(gen2IntervalMs, warmup);

    /// <summary>Whether a requested background gen2 has run, or has been pending long enough to stop waiting for it.</summary>
    /// <remarks>A blocking gen2 fallback never advances the background index, hence the timeout.</remarks>
    internal static bool IsBackgroundGcSettled(long pendingSinceIndex, long backgroundIndex, long pendingForMs) =>
        backgroundIndex > pendingSinceIndex || pendingForMs >= PendingBackgroundGcTimeoutMs;

    internal bool CollectThroughScheduler(int generation) =>
        !IsNoGCRegionActive() &&
        _scheduler.GCCollect(generation, GCCollectionMode.Forced, blocking: false, compacting: false, trimNativeMemory: false);

    private static bool IsNoGCRegionActive() => GCSettings.LatencyMode == GCLatencyMode.NoGCRegion;

    private void RunGen0(long intervalMs, CancellationToken token)
    {
        int lastGen0Count = GC.CollectionCount(0);
        long lastAllocated = GC.GetTotalAllocatedBytes(precise: false);
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(intervalMs))) return;

                long allocated = GC.GetTotalAllocatedBytes(precise: false);
                if (IsGen0Due(GC.CollectionCount(0) != lastGen0Count, allocated - lastAllocated) && !IsNoGCRegionActive())
                {
                    GC.Collect(0, GCCollectionMode.Forced, blocking: false, compacting: false);
                }

                lastGen0Count = GC.CollectionCount(0);
                lastAllocated = allocated;
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error("GC pacer gen0 loop threw; continuing.", e);
            }
        }
    }

    private void RunGen1(long intervalMs, CancellationToken token)
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
                bool warmup = uptime.ElapsedMilliseconds < _warmupMs;
                if (token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(Gen1IntervalMs(intervalMs, warmup)))) return;

                if (GC.CollectionCount(1) == lastGen1Count)
                {
                    CollectThroughScheduler(1);
                }

                lastGen1Count = GC.CollectionCount(1);

                if (_gen2IntervalMs <= 0) continue;

                long nowMs = uptime.ElapsedMilliseconds;
                GCMemoryInfo background = GC.GetGCMemoryInfo(GCKind.Background);
                if (pendingBgcSinceIndex >= 0 && IsBackgroundGcSettled(pendingBgcSinceIndex, background.Index, nowMs - pendingBgcAtMs))
                {
                    pendingBgcSinceIndex = -1;
                }

                int gen2Count = GC.CollectionCount(2);
                if (gen2Count != lastGen2Count)
                {
                    lastGen2Count = gen2Count;
                    lastGen2AtMs = nowMs;
                }
                else if (IsGen2Due(nowMs - lastGen2AtMs, _gen2IntervalMs, warmup, pendingBgcSinceIndex >= 0) &&
                         CollectThroughScheduler(GC.MaxGeneration))
                {
                    lastGen2Count = GC.CollectionCount(2);
                    lastGen2AtMs = uptime.ElapsedMilliseconds;

                    if (lastGen2Count == gen2Count)
                    {
                        pendingBgcSinceIndex = background.Index;
                        pendingBgcAtMs = lastGen2AtMs;
                    }
                }
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error("GC pacer gen1 loop threw; continuing.", e);
            }
        }
    }
}
