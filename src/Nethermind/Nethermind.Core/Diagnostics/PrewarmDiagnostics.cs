// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
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

    // Miss taxonomy: of the slots the main thread missed, how many did the prewarmer warm
    // this block (so the SeqlockCache evicted them = fixable by capacity) vs never warm
    // (drift / cold-start / unreachable = the speculation floor)?
    public static long SlotMissEvicted;
    public static long SlotMissNeverWarmed;

    // Set of storage cells the prewarmer warmed this block (cleared per block). Independent of the
    // fixed-size SeqlockCache, so it records intent even when the cache has since evicted the entry.
    public static readonly ConcurrentDictionary<StorageCell, byte> WarmedSlots = new();

    // Cumulative count of NEVER-WARMED slot misses by contract address, to identify the constant
    // per-block set the prewarmer never touches.
    public static readonly ConcurrentDictionary<Address, int> NeverWarmedByAddress = new();

    public static void RecordSlot(bool hit) => Interlocked.Increment(ref hit ? ref SlotHit : ref SlotMiss);
    public static void RecordAddr(bool hit) => Interlocked.Increment(ref hit ? ref AddrHit : ref AddrMiss);

    /// <summary>Called by the prewarmer when it warms (Sets) a storage cell into the cache.</summary>
    public static void MarkWarmed(in StorageCell cell) => WarmedSlots[cell] = 0;

    /// <summary>Called on a main-thread slot miss; classifies it as evicted vs never-warmed.</summary>
    public static void RecordSlotMiss(in StorageCell cell)
    {
        Interlocked.Increment(ref SlotMiss);
        if (WarmedSlots.ContainsKey(cell))
        {
            Interlocked.Increment(ref SlotMissEvicted);
        }
        else
        {
            Interlocked.Increment(ref SlotMissNeverWarmed);
            NeverWarmedByAddress.AddOrUpdate(cell.Address, 1, static (_, c) => c + 1);
        }
    }

    /// <summary>Top-N most-frequently never-warmed contract addresses (for identifying the constant set).</summary>
    public static string TopNeverWarmed(int n)
    {
        List<KeyValuePair<Address, int>> items = [.. NeverWarmedByAddress];
        items.Sort(static (a, b) => b.Value.CompareTo(a.Value));
        StringBuilder sb = new();
        for (int i = 0; i < n && i < items.Count; i++) sb.Append(items[i].Key).Append('=').Append(items[i].Value).Append(' ');
        return sb.ToString();
    }

    public static void ResetBlock()
    {
        if (Enabled) WarmedSlots.Clear();
    }
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
