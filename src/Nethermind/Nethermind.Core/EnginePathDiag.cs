// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Core;

/// <summary>
/// BENCH ONLY (branch bench/evm-per-contract-14393, not for merge). Timestamps of one engine_newPayload along its whole
/// path - HTTP request, JSON-RPC, engine module lock, no-GC region entry, handler steps, processing queue, stopwatch,
/// verdict, answer - with the managed thread and that thread's CPU time at each point, GC counts and pause time at four
/// of them, and the wall clock at the HTTP start and end (to line up with a pcap of the same host). One INFO line per
/// block once both the HTTP answer and the processing stopwatch are done. <c>BENCH_ENGINE_DIAG=0</c> turns it off.
/// </summary>
public static class EnginePathDiag
{
    public enum P : byte
    {
        HttpStart, Authenticated, BodyRead, ModuleEntry, LockWait, Locked, RegionQueued, HandlerEntry, Decoded,
        RecoveryQueued, Logged, Checked, BlockValidated, Suggested, EnqueueQueued, EnqueueStart, WorkersEntered,
        LoopTake, StopwatchStart, Verdict, Resumed, ModuleReturn, ProcessReturned, HttpEnd, StopwatchStop,
        RecoveryStart, RecoveryEnd, RegionEntryStart, RegionEntryEnd
    }

    private const int Count = (int)P.RegionEntryEnd + 1;
    private const int HttpDone = 1, StopwatchDone = 2;
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("BENCH_ENGINE_DIAG") != "0";
    private static readonly bool IsLinux = OperatingSystem.IsLinux();
    private static readonly AsyncLocal<Record?> _current = new();
    private static readonly ConcurrentDictionary<ValueHash256, Record> _byHash = new();
    private static ILogger _logger;
    private static bool _hasLogger;

    public sealed class Record
    {
        internal readonly long[] T = new long[Count];
        internal readonly int[] Tid = new int[Count];
        internal readonly long[] CpuNs = new long[Count];
        // GC counts gen0..2 and total pause ticks at: 0 start, 1 stopwatch start, 2 verdict, 3 HTTP end
        internal readonly int[] Gc = new int[12];
        internal readonly long[] Pause = new long[4];
        internal readonly bool[] Snapped = new bool[4];
        internal long UnixUsStart, UnixUsEnd, Bytes, Number = -1;
        internal int Txs, Done, RegionStarted = -1, RegionGc0, RegionGc1, RegionGc2;
        internal long RegionPauseTicks;
        internal ValueHash256 Hash;
        internal bool Attached;
        internal long Created = Stopwatch.GetTimestamp();
    }

    public static Record? Current => Enabled ? _current.Value : null;

    public static void SetLogger(ILogger logger)
    {
        if (_hasLogger) return;
        _logger = logger;
        _hasLogger = true;
    }

    /// <summary>Opens a record for this HTTP request; the async flow carries it to the engine module and the handler.</summary>
    public static void BeginHttp()
    {
        if (!Enabled) return;
        Record r = new();
        _current.Value = r;
        r.UnixUsStart = UnixUs();
        Snap(r, 0);
        Set(r, P.HttpStart);
    }

    public static void Mark(P p)
    {
        if (Enabled && _current.Value is { } r) Set(r, p);
    }

    public static void Mark(Record? r, P p)
    {
        if (r is not null) Set(r, p);
    }

    public static void Mark(Hash256? hash, P p)
    {
        if (Enabled && hash is not null && _byHash.TryGetValue(hash.ValueHash256, out Record? r)) Set(r, p);
    }

    public static void Bytes(long bytes)
    {
        if (Enabled && _current.Value is { } r) r.Bytes = bytes;
    }

    /// <summary>Binds the open record (or a new one when the call did not come over HTTP) to the payload's block hash.</summary>
    public static void Attach(Hash256? hash, long number, int txs)
    {
        if (!Enabled || hash is null) return;
        Record? r = _current.Value;
        if (r is null)
        {
            r = new Record();
            _current.Value = r;
            Snap(r, 0);
        }

        r.Hash = hash.ValueHash256;
        r.Number = number;
        r.Txs = txs;
        r.Attached = true;
        _byHash[r.Hash] = r;
        Set(r, P.HandlerEntry);
        // drop records whose block never reached the stopwatch (invalid, syncing, duplicates)
        long stale = Stopwatch.GetTimestamp() - 120 * Stopwatch.Frequency;
        foreach (KeyValuePair<ValueHash256, Record> kv in _byHash)
        {
            if (kv.Value.Created < stale) _byHash.TryRemove(kv.Key, out _);
        }
    }

    public static void StopwatchStart(Hash256? hash)
    {
        if (!Enabled || hash is null || !_byHash.TryGetValue(hash.ValueHash256, out Record? r)) return;
        Snap(r, 1);
        Set(r, P.StopwatchStart);
    }

    public static void Verdict(Hash256? hash)
    {
        if (!Enabled || hash is null || !_byHash.TryGetValue(hash.ValueHash256, out Record? r)) return;
        Set(r, P.Verdict);
        Snap(r, 2);
    }

    public static void StopwatchStop(Hash256? hash)
    {
        if (!Enabled || hash is null || !_byHash.TryGetValue(hash.ValueHash256, out Record? r)) return;
        Set(r, P.StopwatchStop);
        Complete(r, StopwatchDone);
    }

    /// <summary>The HTTP answer is complete: the response was written and the body reader released.</summary>
    public static void EndHttp()
    {
        if (!Enabled || _current.Value is not { } r) return;
        Set(r, P.HttpEnd);
        Snap(r, 3);
        r.UnixUsEnd = UnixUs();
        if (r.Attached) Complete(r, HttpDone);
    }

    /// <summary>The no-GC region entry, on its own pool thread: GCs and pause time inside <c>GC.TryStartNoGCRegion</c>.</summary>
    public static void RegionEntry(Record? r, bool start, bool started = false)
    {
        if (r is null) return;
        if (start)
        {
            r.RegionGc0 = GC.CollectionCount(0);
            r.RegionGc1 = GC.CollectionCount(1);
            r.RegionGc2 = GC.CollectionCount(2);
            r.RegionPauseTicks = GC.GetTotalPauseDuration().Ticks;
            Set(r, P.RegionEntryStart);
        }
        else
        {
            Set(r, P.RegionEntryEnd);
            r.RegionGc0 = GC.CollectionCount(0) - r.RegionGc0;
            r.RegionGc1 = GC.CollectionCount(1) - r.RegionGc1;
            r.RegionGc2 = GC.CollectionCount(2) - r.RegionGc2;
            r.RegionPauseTicks = GC.GetTotalPauseDuration().Ticks - r.RegionPauseTicks;
            Volatile.Write(ref r.RegionStarted, started ? 1 : 0);
        }
    }

    private static void Set(Record r, P p)
    {
        int i = (int)p;
        r.Tid[i] = Environment.CurrentManagedThreadId;
        r.CpuNs[i] = ThreadCpuNs();
        Volatile.Write(ref r.T[i], Stopwatch.GetTimestamp());
    }

    private static void Snap(Record r, int k)
    {
        r.Gc[3 * k] = GC.CollectionCount(0);
        r.Gc[3 * k + 1] = GC.CollectionCount(1);
        r.Gc[3 * k + 2] = GC.CollectionCount(2);
        r.Pause[k] = GC.GetTotalPauseDuration().Ticks;
        r.Snapped[k] = true;
    }

    private static void Complete(Record r, int bit)
    {
        if ((Interlocked.Or(ref r.Done, bit) | bit) != (HttpDone | StopwatchDone)) return;
        _byHash.TryRemove(r.Hash, out _);
        if (!_hasLogger || !_logger.IsInfo) return;
        try
        {
            _logger.Info(Format(r));
        }
        catch (Exception e)
        {
            if (_logger.IsDebug) _logger.Debug($"EngineDiag format failed: {e}");
        }
    }

    private static string Format(Record r)
    {
        long b = r.T[(int)P.HttpStart] != 0 ? r.T[(int)P.HttpStart] : r.T[(int)P.HandlerEntry];
        double us = 1e6 / Stopwatch.Frequency;
        StringBuilder sb = new();
        sb.Append($"EngineDiag n={r.Number} txs={r.Txs} bytes={r.Bytes} unix_us={r.UnixUsStart}..{r.UnixUsEnd}");
        string[] seg = ["pre", "proc", "post"];
        for (int k = 0; k < 3; k++)
        {
            if (!r.Snapped[k] || !r.Snapped[k + 1]) continue;
            sb.Append($" | {seg[k]} gc={r.Gc[3 * k + 3] - r.Gc[3 * k]}/{r.Gc[3 * k + 4] - r.Gc[3 * k + 1]}/{r.Gc[3 * k + 5] - r.Gc[3 * k + 2]}");
            sb.Append($" pause_us={(r.Pause[k + 1] - r.Pause[k]) / 10}");
        }

        sb.Append($" | region started={Volatile.Read(ref r.RegionStarted)} gc={r.RegionGc0}/{r.RegionGc1}/{r.RegionGc2} pause_us={r.RegionPauseTicks / 10}");
        sb.Append(" |");
        for (int i = 0; i < Count; i++)
        {
            long t = Volatile.Read(ref r.T[i]);
            if (t == 0) continue;
            sb.Append($" {(P)i}={(t - b) * us:F0}@{r.Tid[i]}:{r.CpuNs[i] / 1000}");
        }

        return sb.ToString();
    }

    private static long UnixUs() => (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10;

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Sec;
        public long Nsec;
    }

    [DllImport("libc", EntryPoint = "clock_gettime")]
    private static extern int ClockGetTime(int clockId, out Timespec ts);

    private const int ClockThreadCpuTimeId = 3;

    private static long ThreadCpuNs()
    {
        if (!IsLinux) return 0;
        try
        {
            return ClockGetTime(ClockThreadCpuTimeId, out Timespec ts) == 0 ? ts.Sec * 1_000_000_000 + ts.Nsec : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
