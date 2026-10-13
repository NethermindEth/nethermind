// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace Nethermind.Evm.State;

/// <summary>
/// BENCH ONLY (bench/prewarm-trace, never for master): a per-transaction timeline of the block prewarmer against block
/// processing, switched on with the environment variable <c>BENCH_PREWARM_TRACE=1</c>. It only counts and timestamps:
/// nothing it records is read by prewarming or block processing. Off, every hook is one static readonly check.
/// </summary>
public static class PrewarmTrace
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("BENCH_PREWARM_TRACE") == "1";

    /// <summary>What the prewarm work on this thread is doing, for attributing its backing-store reads.</summary>
    public enum Kind : byte
    {
        None = 0,
        Warm,
        Capture,
        DiscoveryRead,
        Refresh
    }

    [ThreadStatic] private static Kind t_kind;
    [ThreadStatic] private static int t_tx;

    private static PrewarmTraceBlock? s_current;

    /// <summary>The block being traced; null between blocks, so the idle-gap passes are not counted.</summary>
    public static PrewarmTraceBlock? Current => Volatile.Read(ref s_current);

    /// <summary>For tests: the last block whose trace ended.</summary>
    public static PrewarmTraceBlock? LastEnded { get; private set; }

    public static void Begin(PrewarmTraceBlock block) => Volatile.Write(ref s_current, block);

    /// <summary>Ends the trace of <paramref name="block"/> if it is still the current one.</summary>
    public static void End(PrewarmTraceBlock block)
    {
        Interlocked.CompareExchange(ref s_current, null, block);
        block.EndTicks = Stopwatch.GetTimestamp();
        LastEnded = block;
    }

    public readonly struct Scope(Kind previousKind, int previousTx) : IDisposable
    {
        public void Dispose()
        {
            t_kind = previousKind;
            t_tx = previousTx;
        }
    }

    public static Scope Enter(Kind kind, int tx)
    {
        Scope scope = new(t_kind, t_tx);
        t_kind = kind;
        t_tx = tx;
        return scope;
    }

    /// <summary>A read the pre-block caches could not answer, by block processing or by a prewarm scope.</summary>
    public static void OnBackendRead(bool prewarmer, bool account)
    {
        PrewarmTraceBlock? block = Current;
        if (block is null) return;
        if (prewarmer) block.OnPrewarmerRead(t_kind, t_tx);
        else block.OnMainRead(account);
    }

    public static void OnMainTxStart(int index) => Current?.OnMainTxStart(index);
}

/// <summary>BENCH ONLY: the counters of one block; see <see cref="PrewarmTrace"/>.</summary>
public sealed class PrewarmTraceBlock(ulong number, int txCount)
{
    public const int Speculative = 1, WarmStarted = 2, Aborted = 4, SkippedAtStart = 8, JobSkipped = 16, HandOff = 32,
        UpFront = 64, DiscoverySkipped = 128, Refreshed = 256, Completed = 512;

    public readonly ulong Number = number;
    public readonly int TxCount = txCount;
    public readonly long StartTicks = Stopwatch.GetTimestamp();
    public long EndTicks;
    public bool Carried;
    public readonly long[] WarmStart = new long[txCount], WarmEnd = new long[txCount], MainStart = new long[txCount],
        DiscoveryStart = new long[txCount], DiscoveryEnd = new long[txCount];
    public readonly int[] WarmReads = new int[txCount], CaptureReads = new int[txCount], RefreshReads = new int[txCount],
        MainReads = new int[txCount], MainAccountReads = new int[txCount], DiscoveryRuns = new int[txCount],
        DiscoveryCells = new int[txCount], Flags = new int[txCount];
    public int MainPreReads, DiscoveryReadReads, OtherReads;
    private int _mainIndex = -1;

    private bool In(int i) => (uint)i < (uint)TxCount;

    public void SetFlag(int i, int flag)
    {
        if (In(i)) Interlocked.Or(ref Flags[i], flag);
    }

    public void OnWarmStart(int i)
    {
        if (!In(i)) return;
        Interlocked.Or(ref Flags[i], WarmStarted);
        Interlocked.CompareExchange(ref WarmStart[i], Stopwatch.GetTimestamp(), 0);
    }

    public void OnWarmEnd(int i)
    {
        if (In(i)) Volatile.Write(ref WarmEnd[i], Stopwatch.GetTimestamp());
    }

    public void OnDiscoveryStart(int i)
    {
        if (!In(i)) return;
        Interlocked.Increment(ref DiscoveryRuns[i]);
        Interlocked.CompareExchange(ref DiscoveryStart[i], Stopwatch.GetTimestamp(), 0);
    }

    public void OnDiscoveryEnd(int i, int cells)
    {
        if (!In(i)) return;
        Interlocked.Add(ref DiscoveryCells[i], cells);
        Volatile.Write(ref DiscoveryEnd[i], Stopwatch.GetTimestamp());
    }

    internal void OnMainTxStart(int i)
    {
        _mainIndex = i;
        if (In(i)) MainStart[i] = Stopwatch.GetTimestamp();
    }

    // Block processing only, on its one thread.
    internal void OnMainRead(bool account)
    {
        int i = _mainIndex;
        if (i < 0)
        {
            MainPreReads++;
        }
        else if (In(i))
        {
            MainReads[i]++;
            if (account) MainAccountReads[i]++;
        }
    }

    internal void OnPrewarmerRead(PrewarmTrace.Kind kind, int tx)
    {
        switch (kind)
        {
            case PrewarmTrace.Kind.Warm when In(tx):
                Interlocked.Increment(ref WarmReads[tx]);
                break;
            case PrewarmTrace.Kind.Capture when In(tx):
                Interlocked.Increment(ref CaptureReads[tx]);
                break;
            case PrewarmTrace.Kind.Refresh when In(tx):
                Interlocked.Increment(ref RefreshReads[tx]);
                break;
            case PrewarmTrace.Kind.DiscoveryRead:
                Interlocked.Increment(ref DiscoveryReadReads);
                break;
            default:
                Interlocked.Increment(ref OtherReads);
                break;
        }
    }

    public double Ms(long ticks) => ticks == 0 ? -1 : (ticks - StartTicks) * 1000.0 / Stopwatch.Frequency;

    private static string F(double ms) => ms < 0 ? "-" : ms.ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>One line for the block, then one per transaction that block processing read at least
    /// <paramref name="minMainReads"/> cold keys for, or whose warm read <paramref name="minWarmReads"/>, or that discovery ran
    /// (at most <paramref name="maxTxLines"/>, the most main-thread reads first).</summary>
    public IEnumerable<string> Format(int minMainReads = 32, int minWarmReads = 64, int maxTxLines = 20)
    {
        int mainReads = 0, warmReads = 0, captureReads = 0, refreshReads = 0, spec = 0, aborted = 0, skipped = 0, jobSkipped = 0,
            never = 0, handoffs = 0, upFront = 0, discSkipped = 0, completed = 0;
        List<int> heavy = [];
        for (int i = 0; i < TxCount; i++)
        {
            int f = Flags[i];
            mainReads += MainReads[i];
            warmReads += WarmReads[i];
            captureReads += CaptureReads[i];
            refreshReads += RefreshReads[i];
            if ((f & Speculative) != 0) spec++;
            if ((f & Aborted) != 0) aborted++;
            if ((f & SkippedAtStart) != 0) skipped++;
            if ((f & JobSkipped) != 0) jobSkipped++;
            if ((f & HandOff) != 0) handoffs++;
            if ((f & UpFront) != 0) upFront++;
            if ((f & DiscoverySkipped) != 0) discSkipped++;
            if ((f & Completed) != 0) completed++;
            if ((f & (WarmStarted | Speculative)) == 0) never++;
            if (MainReads[i] >= minMainReads || WarmReads[i] >= minWarmReads || DiscoveryRuns[i] > 0 || CaptureReads[i] > 0) heavy.Add(i);
        }

        yield return string.Create(CultureInfo.InvariantCulture,
            $"PrewarmTrace block={Number} txs={TxCount} carried={(Carried ? 1 : 0)} end={F(Ms(EndTicks))} main_pre={MainPreReads} main_reads={mainReads} warm_reads={warmReads} capture_reads={captureReads} refresh_reads={refreshReads} disc_read_reads={DiscoveryReadReads} other_reads={OtherReads} spec={spec} completed={completed} aborted={aborted} skipped={skipped} job_skipped={jobSkipped} never={never} upfront={upFront} handoffs={handoffs} disc_skipped={discSkipped}");

        heavy.Sort((a, b) => MainReads[b].CompareTo(MainReads[a]));
        if (heavy.Count > maxTxLines) heavy.RemoveRange(maxTxLines, heavy.Count - maxTxLines);
        foreach (int i in heavy)
        {
            long mainEnd = 0;
            for (int j = i + 1; j < TxCount && mainEnd == 0; j++) mainEnd = MainStart[j];
            if (mainEnd == 0) mainEnd = EndTicks;
            StringBuilder flags = new();
            int f = Flags[i];
            if ((f & Speculative) != 0) flags.Append("spec,");
            if ((f & WarmStarted) != 0) flags.Append("warm,");
            if ((f & Completed) != 0) flags.Append("completed,");
            if ((f & Aborted) != 0) flags.Append("aborted,");
            if ((f & SkippedAtStart) != 0) flags.Append("skipped,");
            if ((f & JobSkipped) != 0) flags.Append("job_skipped,");
            if ((f & UpFront) != 0) flags.Append("upfront,");
            if ((f & HandOff) != 0) flags.Append("handoff,");
            if ((f & DiscoverySkipped) != 0) flags.Append("disc_skipped,");
            if ((f & Refreshed) != 0) flags.Append("refreshed,");
            if (flags.Length > 0) flags.Length--;
            yield return string.Create(CultureInfo.InvariantCulture,
                $"PrewarmTrace tx block={Number} i={i} flags={(flags.Length == 0 ? "-" : flags.ToString())} warm_start={F(Ms(WarmStart[i]))} warm_end={F(Ms(WarmEnd[i]))} warm_reads={WarmReads[i]} main_start={F(Ms(MainStart[i]))} main_end={F(Ms(mainEnd))} main_reads={MainReads[i]} main_acc_reads={MainAccountReads[i]} disc_runs={DiscoveryRuns[i]} disc_start={F(Ms(DiscoveryStart[i]))} disc_end={F(Ms(DiscoveryEnd[i]))} disc_cells={DiscoveryCells[i]} capture_reads={CaptureReads[i]} refresh_reads={RefreshReads[i]}");
        }
    }
}
