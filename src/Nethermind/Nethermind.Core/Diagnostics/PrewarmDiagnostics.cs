// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.Core.Diagnostics;

/// <summary>
/// DIAGNOSTIC-ONLY counters for the main-execution read path's hit/miss against the
/// prewarm-populated <c>PreBlockCaches</c> (the "warm world state" scope). The hit ratio
/// equals the prewarmer's coverage: a hit means the prewarmer warmed the slot/account the
/// main thread actually read; a miss means it did not (speculation drift, or not-yet-warmed).
/// </summary>
/// <remarks>
/// Incremented only on the <c>populatePreBlockCache == false</c> scope (main execution),
/// so under pre-BAL sequential execution there is a single writer; <see cref="Interlocked"/>
/// is used for correctness regardless. Counters are cumulative; callers snapshot deltas per block.
/// </remarks>
public static class PrewarmCoverage
{
    public static bool Enabled;

    public static long SlotHit;
    public static long SlotMiss;
    public static long AddrHit;
    public static long AddrMiss;

    public static void RecordSlot(bool hit) => Interlocked.Increment(ref hit ? ref SlotHit : ref SlotMiss);
    public static void RecordAddr(bool hit) => Interlocked.Increment(ref hit ? ref AddrHit : ref AddrMiss);
}

/// <summary>
/// DIAGNOSTIC-ONLY lead-cap throttle for the speculative prewarmer. When enabled, prewarm
/// workers pause once they get more than <see cref="HighWatermarkPct"/>% of the block ahead of
/// execution and resume only after the lead falls back below <see cref="LowWatermarkPct"/>%.
/// </summary>
/// <remarks>
/// This prototypes the lead-bounding half of the "throttle when far ahead" proposal. The
/// state-refresh half is intentionally omitted: mid-block re-basing would require reading the
/// main thread's uncommitted, non-thread-safe journal, which is infeasible without stalling it.
/// Coordination is a pair of counters: execution increments <see cref="Executed"/>, prewarm
/// workers increment <see cref="Warmed"/> and spin-wait on the lead. Per-block scoped; the
/// prewarmer calls <see cref="StartBlock"/> before execution begins (PreWarmCaches precedes
/// block processing), so the reset is observed before the first <see cref="OnExecuted"/>.
/// </remarks>
public static class PrewarmThrottle
{
    public static bool Enabled;
    public static int HighWatermarkPct = 25;
    public static int LowWatermarkPct = 10;

    private static long _executed;
    private static long _warmed;
    private static int _highTxs;
    private static int _lowTxs;

    public static long Executed => Volatile.Read(ref _executed);
    public static long Warmed => Volatile.Read(ref _warmed);

    public static void Configure(int highPct, int lowPct)
    {
        HighWatermarkPct = highPct;
        LowWatermarkPct = lowPct;
        Enabled = highPct > 0;
    }

    /// <summary>Resets the per-block counters and computes the watermark thresholds in tx units.</summary>
    public static void StartBlock(int totalTxs)
    {
        Volatile.Write(ref _executed, 0);
        Volatile.Write(ref _warmed, 0);
        _highTxs = totalTxs * HighWatermarkPct / 100;
        _lowTxs = totalTxs * LowWatermarkPct / 100;
    }

    public static void OnExecuted() => Interlocked.Increment(ref _executed);

    public static void OnWarmed() => Interlocked.Increment(ref _warmed);

    /// <summary>
    /// Blocks the calling prewarm worker while its lead over execution exceeds the high
    /// watermark, returning once the lead has drained below the low watermark (or the token
    /// is cancelled). No-op when the cap is wide enough that the lead can't reach it.
    /// </summary>
    public static void WaitIfTooFarAhead(CancellationToken token)
    {
        if (_highTxs <= 0) return;
        if (Volatile.Read(ref _warmed) - Volatile.Read(ref _executed) < _highTxs) return;

        SpinWait spin = default;
        while (Volatile.Read(ref _warmed) - Volatile.Read(ref _executed) > _lowTxs)
        {
            if (token.IsCancellationRequested) return;
            spin.SpinOnce();
        }
    }
}
