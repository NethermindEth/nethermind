// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace Nethermind.Core.Diagnostics;

/// <summary>
/// Diagnostic timeline of one engine_newPayload request, from the HTTP request to the block's commit, enabled by
/// NETHERMIND_NP_TRACE=1. Requests are assumed one at a time (a benchmark driver); each is printed as one line
/// when the next newPayload starts, so the commit that follows its answer is in it too.
/// </summary>
public static class NewPayloadTrace
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("NETHERMIND_NP_TRACE") == "1";

    public const int HttpStart = 0, BodyRead = 1, MethodEntry = 2, Locked = 3, GcRegion = 4, HandleStart = 5, Decoded = 6,
        PreSuggest = 7, Suggested = 8, EnqueueStart = 9, Dequeued = 10, BranchStart = 11, ProcessOneStart = 12, TxsDone = 13,
        ProcessOneEnd = 14, Verdict = 15, HandlerResumed = 16, HandleEnd = 17, ResponseDone = 18, CommitDone = 19, BranchEnd = 20,
        RecDecoded = 21, RecStarted = 22, TxsDecoded = 23, TxRootJoined = 24;

    private const int Count = 25;
    private static readonly string[] Names =
    [
        "http", "body", "entry", "locked", "gcregion", "handle", "decoded", "presuggest", "suggested", "enqueue", "dequeued",
        "branch", "p1start", "txsdone", "p1end", "verdict", "resumed", "handleend", "response", "commit", "branchend",
        "recdecoded", "recstarted", "txsdecoded", "txrootjoined"
    ];

    private sealed class Record
    {
        public readonly long[] Stamps = new long[Count];
        public long Block = -1;
        // The processing thread's /proc schedstat across ProcessOne: nanoseconds run and waited on a runqueue.
        public long RunStart, WaitStart, RunNs = -1, WaitNs = -1, Slices = -1, SlicesStart;
        // The same split at the end of the transactions, so execution and finalization are told apart.
        public long TxRunNs = -1, TxWaitNs = -1;
        // Major and minor faults and voluntary context switches of the processing thread across the transactions.
        public long MajStart, MinStart, VolStart, Majflt = -1, Minflt = -1, Vol = -1;
        // Read syscalls and bytes fetched from storage (not the page cache) by the processing thread across the transactions.
        public long SyscrStart, RdStart, Syscr = -1, RdBytes = -1;
        public readonly long[] Reads = new long[ReadKinds], ReadUs = new long[ReadKinds], Slow = new long[ReadKinds], SlowUs = new long[ReadKinds];
    }

    private static bool ReadFaultsAndSwitches(out long majflt, out long minflt, out long voluntary)
    {
        majflt = minflt = voluntary = 0;
        try
        {
            // /proc/thread-self/stat: fields after the ')' of comm; minflt is field 10 and majflt field 12 overall.
            string stat = System.IO.File.ReadAllText("/proc/thread-self/stat");
            string[] f = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            minflt = long.Parse(f[7]); majflt = long.Parse(f[9]);
            foreach (string line in System.IO.File.ReadLines("/proc/thread-self/status"))
            {
                if (line.StartsWith("voluntary_ctxt_switches:", StringComparison.Ordinal)) { voluntary = long.Parse(line.AsSpan(24).Trim()); break; }
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ReadIo(out long syscr, out long readBytes)
    {
        syscr = readBytes = -1;
        try
        {
            foreach (string line in System.IO.File.ReadLines("/proc/thread-self/io"))
            {
                if (line.StartsWith("syscr:", StringComparison.Ordinal)) syscr = long.Parse(line.AsSpan(6).Trim());
                else if (line.StartsWith("read_bytes:", StringComparison.Ordinal)) readBytes = long.Parse(line.AsSpan(11).Trim());
            }
            return syscr >= 0 && readBytes >= 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Reads the schedstat when the block's transactions are done, on the processing thread.</summary>
    public static void SchedTxsDone()
    {
        if (Enabled && Volatile.Read(ref s_active) is { } record && record.RunStart != 0 && record.TxRunNs < 0
            && ReadSchedStat(out long run, out long wait, out _))
        {
            record.TxRunNs = run - record.RunStart; record.TxWaitNs = wait - record.WaitStart;
            if (ReadFaultsAndSwitches(out long maj, out long min, out long vol)) { record.Majflt = maj - record.MajStart; record.Minflt = min - record.MinStart; record.Vol = vol - record.VolStart; }
            if (record.SyscrStart >= 0 && ReadIo(out long syscr, out long rd)) { record.Syscr = syscr - record.SyscrStart; record.RdBytes = rd - record.RdStart; }
        }
    }

    /// <summary>Reads the calling thread's schedstat at the start of block execution.</summary>
    public static void SchedStart()
    {
        if (Enabled && Volatile.Read(ref s_active) is { } record && ReadSchedStat(out long run, out long wait, out long slices))
        {
            record.RunStart = run; record.WaitStart = wait; record.SlicesStart = slices;
            if (ReadFaultsAndSwitches(out long maj, out long min, out long vol)) { record.MajStart = maj; record.MinStart = min; record.VolStart = vol; }
            if (ReadIo(out long syscr, out long rd)) { record.SyscrStart = syscr; record.RdStart = rd; } else record.SyscrStart = -1;
        }
    }

    /// <summary>Reads it again at the end, on the same thread, and keeps the difference.</summary>
    public static void SchedEnd()
    {
        if (Enabled && Volatile.Read(ref s_active) is { } record && record.RunStart != 0 && ReadSchedStat(out long run, out long wait, out long slices))
        {
            record.RunNs = run - record.RunStart; record.WaitNs = wait - record.WaitStart; record.Slices = slices - record.SlicesStart;
        }
    }

    private static bool ReadSchedStat(out long run, out long wait, out long slices)
    {
        run = wait = slices = 0;
        try
        {
            string[] parts = System.IO.File.ReadAllText("/proc/thread-self/schedstat").Split(' ');
            return parts.Length >= 3 && long.TryParse(parts[0], out run) && long.TryParse(parts[1], out wait) && long.TryParse(parts[2], out slices);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // State reads on the processing thread while it executes the block's transactions: count, time, and the slow ones.
    public const int AccountRead = 0, SlotRead = 1, PersistedAccountRead = 2, PersistenceAccountRead = 3, PersistedSlotRead = 4, PersistenceSlotRead = 5;
    private const int ReadKinds = 6;
    private static readonly string[] ReadNames = ["acct", "slot", "pacct", "dbacct", "pslot", "dbslot"];
    [ThreadStatic] private static bool t_inTxs;
    private static readonly long SlowTicks = Stopwatch.Frequency / 50_000; // 20 µs
    private static readonly long[] s_reads = new long[ReadKinds], s_readTicks = new long[ReadKinds], s_slow = new long[ReadKinds], s_slowTicks = new long[ReadKinds];

    /// <summary>Whether this thread is the processing thread inside the block's transactions; cheap enough per read.</summary>
    public static bool InTxs => t_inTxs;

    public static void BeginTxs()
    {
        if (!Enabled) return;
        Array.Clear(s_reads); Array.Clear(s_readTicks); Array.Clear(s_slow); Array.Clear(s_slowTicks);
        t_inTxs = true;
    }

    public static void EndTxs()
    {
        if (!t_inTxs) return;
        t_inTxs = false;
        if (Volatile.Read(ref s_active) is { } record)
        {
            for (int i = 0; i < ReadKinds; i++)
            {
                record.Reads[i] = s_reads[i]; record.ReadUs[i] = s_readTicks[i] * 1_000_000 / Stopwatch.Frequency;
                record.Slow[i] = s_slow[i]; record.SlowUs[i] = s_slowTicks[i] * 1_000_000 / Stopwatch.Frequency;
            }
        }
    }

    public static void AddRead(int kind, long ticks)
    {
        s_reads[kind]++; s_readTicks[kind] += ticks;
        if (ticks >= SlowTicks) { s_slow[kind]++; s_slowTicks[kind] += ticks; }
    }

    private static readonly AsyncLocal<Record?> s_request = new();
    private static Record? s_active;

    public static void BeginRequest()
    {
        if (!Enabled) return;
        Record record = new();
        record.Stamps[HttpStart] = Stopwatch.GetTimestamp();
        s_request.Value = record;
    }

    /// <summary>Stamps the request this async flow belongs to, whatever its method.</summary>
    public static void StampRequest(int point)
    {
        if (Enabled && s_request.Value is { } record && record.Stamps[point] == 0) record.Stamps[point] = Stopwatch.GetTimestamp();
    }

    /// <summary>Makes this flow's request the active newPayload and prints the previous one.</summary>
    public static void BeginNewPayload()
    {
        if (!Enabled) return;
        Record record = s_request.Value ?? new Record();
        record.Stamps[MethodEntry] = Stopwatch.GetTimestamp();
        Record? previous = Interlocked.Exchange(ref s_active, record);
        // Off the request path: the next payload's latency must not include the previous one's line.
        if (previous is not null) ThreadPool.UnsafeQueueUserWorkItem(static r => Print(r), previous, preferLocal: false);
    }

    public static void SetBlock(long number)
    {
        if (Enabled && Volatile.Read(ref s_active) is { } record) record.Block = number;
    }

    /// <summary>Stamps the active newPayload; only the first stamp of a point counts.</summary>
    public static void Stamp(int point)
    {
        if (Enabled && Volatile.Read(ref s_active) is { } record && record.Stamps[point] == 0) record.Stamps[point] = Stopwatch.GetTimestamp();
    }

    private static void Print(Record record)
    {
        long start = record.Stamps[HttpStart] != 0 ? record.Stamps[HttpStart] : record.Stamps[MethodEntry];
        StringBuilder line = new("NP-TRACE block=");
        line.Append(record.Block);
        for (int i = 1; i < Count; i++)
        {
            long stamp = record.Stamps[i];
            line.Append(' ').Append(Names[i]).Append('=');
            if (stamp == 0) line.Append("na");
            else line.Append(((stamp - start) * 1_000_000 / Stopwatch.Frequency).ToString());
        }

        line.Append(" schedrun=").Append(record.RunNs < 0 ? "na" : (record.RunNs / 1000).ToString())
            .Append(" schedwait=").Append(record.WaitNs < 0 ? "na" : (record.WaitNs / 1000).ToString())
            .Append(" schedslices=").Append(record.Slices < 0 ? "na" : record.Slices.ToString())
            .Append(" txrun=").Append(record.TxRunNs < 0 ? "na" : (record.TxRunNs / 1000).ToString())
            .Append(" txwait=").Append(record.TxWaitNs < 0 ? "na" : (record.TxWaitNs / 1000).ToString())
            .Append(" txmajflt=").Append(record.Majflt < 0 ? "na" : record.Majflt.ToString())
            .Append(" txminflt=").Append(record.Minflt < 0 ? "na" : record.Minflt.ToString())
            .Append(" txvolsw=").Append(record.Vol < 0 ? "na" : record.Vol.ToString())
            .Append(" txsyscr=").Append(record.Syscr < 0 ? "na" : record.Syscr.ToString())
            .Append(" txrdbytes=").Append(record.RdBytes < 0 ? "na" : record.RdBytes.ToString());
        for (int i = 0; i < ReadKinds; i++)
        {
            string kind = ReadNames[i];
            line.Append(' ').Append(kind).Append("reads=").Append(record.Reads[i]).Append(' ').Append(kind).Append("us=").Append(record.ReadUs[i])
                .Append(' ').Append(kind).Append("slow=").Append(record.Slow[i]).Append(' ').Append(kind).Append("slowus=").Append(record.SlowUs[i]);
        }
        Console.Out.WriteLine(line.ToString());
    }
}
