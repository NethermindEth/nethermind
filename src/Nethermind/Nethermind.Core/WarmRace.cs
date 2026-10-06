// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace Nethermind.Core;

public static class WarmRace
{
    public static readonly bool On = Environment.GetEnvironmentVariable("WARMRACE") == "1";

    private const byte Undecided = 0;
    private const byte Lost = 1;
    private const byte InFlight = 2;

    private static readonly ConcurrentDictionary<StorageCell, long> WarmStart = new();
    private static readonly ConcurrentDictionary<StorageCell, long> WarmDone = new();
    private static readonly ConcurrentDictionary<StorageCell, long> MainMissed = new();
    private static readonly ConcurrentDictionary<StorageCell, long> WarmAfter = new();
    private static int _mainMissCount;

    [ThreadStatic] private static bool _inBlock;
    [ThreadStatic] private static List<(StorageCell Cell, long At, long Ticks, byte Category, int Ordinal, int TxWarm)>? _misses;
    [ThreadStatic] private static int _txWarm;
    [ThreadStatic] private static long[]? _txWarmCount;
    [ThreadStatic] private static long[]? _accountTicksByWarm;
    private static volatile int[] _txState = [];
    [ThreadStatic] private static int _txOrdinal;
    [ThreadStatic] private static long _blockStart;
    [ThreadStatic] private static int _indexBucket, _timeBucket;
    [ThreadStatic] private static long[]? _noneCount;
    [ThreadStatic] private static long[]? _noneTicks;
    private static long _warmBlockAt, _firstWarmTxAt;
    [ThreadStatic] private static long _accountMisses, _accountTicks;

    private static List<(StorageCell Cell, long At, long Ticks, byte Category, int Ordinal, int TxWarm)> Misses => _misses ??= [];

    public static void WarmBlock(int txCount)
    {
        _txState = new int[txCount];
        Volatile.Write(ref _firstWarmTxAt, 0);
        Volatile.Write(ref _warmBlockAt, Stopwatch.GetTimestamp());
    }

    public static void WarmTxStart(int index)
    {
        int[] state = _txState;
        if ((uint)index < (uint)state.Length) Volatile.Write(ref state[index], 1);
        if (Volatile.Read(ref _firstWarmTxAt) == 0) Interlocked.CompareExchange(ref _firstWarmTxAt, Stopwatch.GetTimestamp(), 0);
    }

    public static void WarmTxDone(int index)
    {
        int[] state = _txState;
        if ((uint)index < (uint)state.Length) Volatile.Write(ref state[index], 2);
    }

    public static void MainTx(int index)
    {
        if (!_inBlock) return;
        int[] state = _txState;
        _txWarm = (uint)index < (uint)state.Length ? Volatile.Read(ref state[index]) : 0;
        (_txWarmCount ??= new long[3])[_txWarm]++;
        if (_txWarm != 0) return;
        double sinceStart = (Stopwatch.GetTimestamp() - _blockStart) * 1000.0 / Stopwatch.Frequency;
        _indexBucket = index == 0 ? 0 : index < 8 ? 1 : index < 32 ? 2 : 3;
        _timeBucket = sinceStart < 1 ? 0 : sinceStart < 10 ? 1 : sinceStart < 100 ? 2 : 3;
        (_noneCount ??= new long[8])[_indexBucket]++;
        _noneCount[4 + _timeBucket]++;
    }

    public static void BeginBlock()
    {
        _inBlock = true;
        _txOrdinal = 0;
        _accountMisses = _accountTicks = 0;
        _txWarm = 0;
        _blockStart = Stopwatch.GetTimestamp();
        Array.Clear(_noneCount ??= new long[8]);
        Array.Clear(_noneTicks ??= new long[8]);
        Array.Clear(_txWarmCount ??= new long[3]);
        Array.Clear(_accountTicksByWarm ??= new long[3]);
        Misses.Clear();
    }

    public static void BeginTx() => _txOrdinal = 0;

    public static void WarmTouch(in StorageCell cell)
    {
        if (Volatile.Read(ref _mainMissCount) == 0) return;
        if (MainMissed.ContainsKey(cell)) WarmAfter.TryAdd(cell, Stopwatch.GetTimestamp());
    }

    public static void WarmLoadStart(in StorageCell cell)
    {
        long now = Stopwatch.GetTimestamp();
        WarmStart.TryAdd(cell, now);
        if (Volatile.Read(ref _mainMissCount) != 0 && MainMissed.ContainsKey(cell)) WarmAfter.TryAdd(cell, now);
    }

    public static void WarmLoadDone(in StorageCell cell) => WarmDone.TryAdd(cell, Stopwatch.GetTimestamp());

    public static void MainAccountMiss(long ticks)
    {
        if (!_inBlock) return;
        _accountMisses++;
        _accountTicks += ticks;
        (_accountTicksByWarm ??= new long[3])[_txWarm] += ticks;
        AddNone(ticks);
    }

    public static void MainMiss(in StorageCell cell, long start, long ticks)
    {
        if (!_inBlock) return;
        byte category = Undecided;
        if (WarmDone.TryGetValue(cell, out long done) && done <= start) category = Lost;
        else if (WarmStart.TryGetValue(cell, out long warmStart) && warmStart <= start) category = InFlight;
        Misses.Add((cell, start, ticks, category, ++_txOrdinal, _txWarm));
        AddNone(ticks);
    }

    private static void AddNone(long ticks)
    {
        if (_txWarm != 0) return;
        long[] none = _noneTicks ??= new long[8];
        none[_indexBucket] += ticks;
        none[4 + _timeBucket] += ticks;
    }

    public static void MainMissBegin(in StorageCell cell, long start)
    {
        if (!_inBlock) return;
        MainMissed.TryAdd(cell, start);
        Interlocked.Increment(ref _mainMissCount);
    }

    public static void EndBlock(ulong number)
    {
        if (!_inBlock) return;
        _inBlock = false;
        double f = 1000.0 / Stopwatch.Frequency;
        long lost = 0, inFlight = 0, late = 0, never = 0;
        long lostTicks = 0, inFlightTicks = 0, lateTicks = 0, neverTicks = 0;
        long late1 = 0, late10 = 0, lateMore = 0;
        long[] ordinalCount = new long[4];
        long[] ordinalTicks = new long[4];
        long total = 0, totalTicks = 0;
        long[] byWarm = new long[12];
        foreach ((StorageCell cell, long at, long ticks, byte category, int ordinal, int txWarm) in Misses)
        {
            int kind = category == Lost ? 0 : category == InFlight ? 1 : WarmAfter.ContainsKey(cell) ? 2 : 3;
            byWarm[kind * 3 + txWarm] += ticks;
            total++;
            totalTicks += ticks;
            int bucket = ordinal == 1 ? 0 : ordinal <= 4 ? 1 : ordinal <= 24 ? 2 : 3;
            ordinalCount[bucket]++;
            ordinalTicks[bucket] += ticks;
            if (category == Lost)
            {
                lost++;
                lostTicks += ticks;
            }
            else if (category == InFlight)
            {
                inFlight++;
                inFlightTicks += ticks;
            }
            else if (WarmAfter.TryGetValue(cell, out long after))
            {
                late++;
                lateTicks += ticks;
                double lagMs = (after - at) * f;
                if (lagMs <= 1) late1++;
                else if (lagMs <= 10) late10++;
                else lateMore++;
            }
            else
            {
                never++;
                neverTicks += ticks;
            }
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"WARMRACE block={number} miss={total} miss_ms={totalTicks * f:F3} lost={lost} lost_ms={lostTicks * f:F3} inflight={inFlight} inflight_ms={inFlightTicks * f:F3} late={late} late_ms={lateTicks * f:F3} late_le1={late1} late_le10={late10} late_gt10={lateMore} never={never} never_ms={neverTicks * f:F3} ord1={ordinalCount[0]} ord1_ms={ordinalTicks[0] * f:F3} ord2_4={ordinalCount[1]} ord2_4_ms={ordinalTicks[1] * f:F3} ord5_24={ordinalCount[2]} ord5_24_ms={ordinalTicks[2] * f:F3} ord25={ordinalCount[3]} ord25_ms={ordinalTicks[3] * f:F3} acct_miss={_accountMisses} acct_ms={_accountTicks * f:F3} warm_loads={WarmStart.Count} tx_none={_txWarmCount![0]} tx_run={_txWarmCount[1]} tx_done={_txWarmCount[2]} acct_none_ms={_accountTicksByWarm![0] * f:F3} acct_run_ms={_accountTicksByWarm[1] * f:F3} acct_done_ms={_accountTicksByWarm[2] * f:F3} inflight_none_ms={byWarm[3] * f:F3} inflight_run_ms={byWarm[4] * f:F3} inflight_done_ms={byWarm[5] * f:F3} late_none_ms={byWarm[6] * f:F3} late_run_ms={byWarm[7] * f:F3} late_done_ms={byWarm[8] * f:F3} never_none_ms={byWarm[9] * f:F3} never_run_ms={byWarm[10] * f:F3} never_done_ms={byWarm[11] * f:F3} warm_block_ms={(Volatile.Read(ref _warmBlockAt) - _blockStart) * f:F3} first_warm_tx_ms={(Volatile.Read(ref _firstWarmTxAt) == 0 ? -1 : (Volatile.Read(ref _firstWarmTxAt) - _blockStart) * f):F3} none_i0={_noneCount![0]} none_i1_7={_noneCount[1]} none_i8_31={_noneCount[2]} none_i32={_noneCount[3]} none_t1={_noneCount[4]} none_t10={_noneCount[5]} none_t100={_noneCount[6]} none_tmore={_noneCount[7]} none_i0_ms={_noneTicks![0] * f:F3} none_i1_7_ms={_noneTicks[1] * f:F3} none_i8_31_ms={_noneTicks[2] * f:F3} none_i32_ms={_noneTicks[3] * f:F3} none_t1_ms={_noneTicks[4] * f:F3} none_t10_ms={_noneTicks[5] * f:F3} none_t100_ms={_noneTicks[6] * f:F3} none_tmore_ms={_noneTicks[7] * f:F3}"));

        Volatile.Write(ref _mainMissCount, 0);
        MainMissed.Clear();
        WarmAfter.Clear();
        WarmStart.Clear();
        WarmDone.Clear();
        Misses.Clear();
    }
}
