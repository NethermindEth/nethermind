// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        RecDecoded = 21, RecStarted = 22, TxsDecoded = 23, TxRootJoined = 24,
        // Block-end state work on the processing thread: commit with roots, then the state root.
        MerkleStart = 25, JournalDone = 26, StorageRootsDone = 27, AccountsFlushed = 28, AccountsInserted = 29, StateRootDone = 30,
        // Request-thread checkpoints between the decoded block and its suggestion.
        HashChecked = 31, ParentFound = 32, ParentReady = 33, ShouldProcess = 34, Validated = 35;

    private const int Count = 36;
    private static readonly string[] Names =
    [
        "http", "body", "entry", "locked", "gcregion", "handle", "decoded", "presuggest", "suggested", "enqueue", "dequeued",
        "branch", "p1start", "txsdone", "p1end", "verdict", "resumed", "handleend", "response", "commit", "branchend",
        "recdecoded", "recstarted", "txsdecoded", "txrootjoined",
        "mstart", "mjournal", "mstorage", "mflush", "minsert", "mroot",
        "hashok", "parent", "parentok", "shouldok", "validated"
    ];

    public const int StorageTries = 0, StorageSlots = 1, AccountsWritten = 2;
    private const int CounterCount = 3;
    private static readonly string[] CounterNames = ["ntries", "nslots", "naccts"];

    private sealed class TxMisses(int index, long startUs)
    {
        public readonly int Index = index;
        public readonly long StartUs = startUs;
        public int Accounts, Slots;
    }

    private sealed class Record
    {
        // Per-transaction pre-block cache misses on the processing thread, with each transaction's start, and notes.
        public readonly List<TxMisses> Txs = [];
        public TxMisses? CurrentTx;
        public readonly StringBuilder Notes = new();
        public long Start => Stamps[HttpStart] != 0 ? Stamps[HttpStart] : Stamps[MethodEntry];
        public readonly long[] Stamps = new long[Count];
        public readonly long[] Counters = [-1, -1, -1];
        public long Block = -1;
        // The processing thread's /proc schedstat across ProcessOne: nanoseconds run and waited on a runqueue.
        public long RunStart, WaitStart, RunNs = -1, WaitNs = -1, Slices = -1, SlicesStart;
        // Whole-process CPU time (all threads) from the HTTP request and from ProcessOne's start, to ProcessOne's end.
        public long ProcCpuRequestStart, ProcCpuP1Start, ProcCpuRequestNs = -1, ProcCpuP1Ns = -1;
        // Collections and GC pause time between the HTTP request's start and its response being written.
        public int Gc0Start, Gc1Start, Gc2Start, Gc0 = -1, Gc1 = -1, Gc2 = -1;
        public long PauseStartTicks, PauseUs = -1;

        public void GcStart()
        {
            Gc0Start = GC.CollectionCount(0); Gc1Start = GC.CollectionCount(1); Gc2Start = GC.CollectionCount(2);
            PauseStartTicks = GC.GetTotalPauseDuration().Ticks;
        }

        public void GcEnd()
        {
            Gc0 = GC.CollectionCount(0) - Gc0Start; Gc1 = GC.CollectionCount(1) - Gc1Start; Gc2 = GC.CollectionCount(2) - Gc2Start;
            PauseUs = (GC.GetTotalPauseDuration().Ticks - PauseStartTicks) / 10;
        }
    }

    /// <summary>Reads the calling thread's schedstat at the start of block execution.</summary>
    public static void SchedStart()
    {
        if (Enabled && Volatile.Read(ref s_active) is { } record && ReadSchedStat(out long run, out long wait, out long slices))
        {
            record.RunStart = run; record.WaitStart = wait; record.SlicesStart = slices;
            record.ProcCpuP1Start = ProcessCpuNs();
        }
    }

    /// <summary>Reads it again at the end, on the same thread, and keeps the difference.</summary>
    public static void SchedEnd()
    {
        if (Enabled && Volatile.Read(ref s_active) is { } record && record.RunStart != 0 && ReadSchedStat(out long run, out long wait, out long slices))
        {
            record.RunNs = run - record.RunStart; record.WaitNs = wait - record.WaitStart; record.Slices = slices - record.SlicesStart;
            long cpu = ProcessCpuNs();
            if (cpu > 0 && record.ProcCpuP1Start > 0) record.ProcCpuP1Ns = cpu - record.ProcCpuP1Start;
            if (cpu > 0 && record.ProcCpuRequestStart > 0) record.ProcCpuRequestNs = cpu - record.ProcCpuRequestStart;
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Timespec { public long Seconds; public long Nanoseconds; }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "clock_gettime")]
    private static extern int ClockGetTime(int clockId, out Timespec time);

    /// <summary>CPU time of every thread of the process (CLOCK_PROCESS_CPUTIME_ID), or 0 where unavailable.</summary>
    private static long ProcessCpuNs()
    {
        try
        {
            return ClockGetTime(2, out Timespec t) == 0 ? t.Seconds * 1_000_000_000 + t.Nanoseconds : 0;
        }
        catch (Exception)
        {
            return 0;
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

    private static readonly AsyncLocal<Record?> s_request = new();
    private static Record? s_active;

    public static void BeginRequest()
    {
        if (!Enabled) return;
        Record record = new();
        record.GcStart();
        record.ProcCpuRequestStart = ProcessCpuNs();
        record.Stamps[HttpStart] = Stopwatch.GetTimestamp();
        s_request.Value = record;
    }

    /// <summary>Stamps the request this async flow belongs to, whatever its method.</summary>
    public static void StampRequest(int point)
    {
        if (Enabled && s_request.Value is { } record && record.Stamps[point] == 0)
        {
            record.Stamps[point] = Stopwatch.GetTimestamp();
            if (point == ResponseDone) record.GcEnd();
        }
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

    /// <summary>Stamps the active newPayload only when called on the block-processing thread.</summary>
    public static void StampProcessing(int point)
    {
        if (Enabled && Threading.ProcessingThread.IsBlockProcessingThread) Stamp(point);
    }

    /// <summary>Sets a counter of the active newPayload, on the block-processing thread; the first value counts.</summary>
    public static void SetCounter(int counter, long value)
    {
        if (Enabled && Threading.ProcessingThread.IsBlockProcessingThread && Volatile.Read(ref s_active) is { } record && record.Counters[counter] < 0)
            record.Counters[counter] = value;
    }

    /// <summary>Marks a transaction's start on the processing thread; the cache misses that follow count against it.</summary>
    public static void TxStart(int index)
    {
        if (Enabled && Threading.ProcessingThread.IsBlockProcessingThread && Volatile.Read(ref s_active) is { } record)
        {
            TxMisses tx = new(index, ElapsedUs(record));
            record.Txs.Add(tx);
            record.CurrentTx = tx;
        }
    }

    /// <summary>Counts a pre-block cache miss on the processing thread against its current transaction.</summary>
    public static void Miss(bool storage)
    {
        if (Enabled && Threading.ProcessingThread.IsBlockProcessingThread && Volatile.Read(ref s_active) is { CurrentTx: { } tx })
        {
            if (storage) tx.Slots++;
            else tx.Accounts++;
        }
    }

    /// <summary>Microseconds since the active newPayload started, or -1 without one.</summary>
    public static long NowUs() => Enabled && Volatile.Read(ref s_active) is { } record ? ElapsedUs(record) : -1;

    private static long ElapsedUs(Record record) => (Stopwatch.GetTimestamp() - record.Start) * 1_000_000 / Stopwatch.Frequency;

    /// <summary>Appends a note (no spaces or '=') to the active newPayload's line, from any thread.</summary>
    public static void Note(string text)
    {
        if (Enabled && Volatile.Read(ref s_active) is { } record)
        {
            lock (record.Notes)
            {
                if (record.Notes.Length > 0) record.Notes.Append(';');
                record.Notes.Append(text);
            }
        }
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
            .Append(" gc0=").Append(record.Gc0 < 0 ? "na" : record.Gc0.ToString())
            .Append(" gc1=").Append(record.Gc1 < 0 ? "na" : record.Gc1.ToString())
            .Append(" gc2=").Append(record.Gc2 < 0 ? "na" : record.Gc2.ToString())
            .Append(" gcpause=").Append(record.PauseUs < 0 ? "na" : record.PauseUs.ToString())
            .Append(" pcpu=").Append(record.ProcCpuP1Ns < 0 ? "na" : (record.ProcCpuP1Ns / 1000).ToString())
            .Append(" pcpureq=").Append(record.ProcCpuRequestNs < 0 ? "na" : (record.ProcCpuRequestNs / 1000).ToString());
        for (int i = 0; i < CounterCount; i++)
            line.Append(' ').Append(CounterNames[i]).Append('=').Append(record.Counters[i] < 0 ? "na" : record.Counters[i].ToString());

        // The transactions that missed the pre-block cache most, as index:accounts/slots@start-us.
        line.Append(" ntx=").Append(record.Txs.Count).Append(" txmiss=");
        int printed = 0;
        foreach (TxMisses tx in record.Txs.OrderByDescending(static t => t.Accounts + t.Slots))
        {
            if (printed == 12 || tx.Accounts + tx.Slots < 3) break;
            if (printed++ > 0) line.Append(',');
            line.Append(tx.Index).Append(':').Append(tx.Accounts).Append('/').Append(tx.Slots).Append('@').Append(tx.StartUs);
        }

        if (printed == 0) line.Append("na");
        lock (record.Notes) line.Append(" notes=").Append(record.Notes.Length == 0 ? "na" : record.Notes.ToString());
        Console.Out.WriteLine(line.ToString());
    }
}
