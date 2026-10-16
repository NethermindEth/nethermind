// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using EvmMetrics = Nethermind.Evm.Metrics;

namespace Nethermind.Consensus.Processing;

/// <remarks>
/// BENCH ONLY (bench/handoff-diag2, not for merge). Iteration 2: the block's startup timeline and each transaction's warm
/// run, in the same slow-block JSON. Times are printed in ms relative to <see cref="Prepare"/>, negative before it.
/// <list type="bullet">
/// <item>Block (<c>handoff</c> object): <c>t_payload</c> (newPayload started the sender recovery), <c>t_recov0</c>,
/// <c>t_recov_done</c>, <c>t_prewarm</c> (PreWarmCaches entry), <c>t_session</c> (coordinator entry), <c>t_seed0</c> and
/// <c>t_seed_end</c> (when Seed starts and returns, wherever it runs), <c>seed_ms_hash</c> and <c>seed_hashed</c> (hashes
/// Seed computed itself), <c>seed_n</c> (footprints stored), <c>t_load0</c>/<c>t_load1</c> around the grouping, <c>jobs</c>,
/// <c>hoisted</c>, <c>split</c>, <c>unclaimed_at_load</c>, <c>recov_at_load</c>, <c>workers</c> (fan-out size),
/// <c>workers_run</c>, <c>t_worker</c> (each slot's start), <c>helpers</c>, <c>t_helper0</c>, <c>t_addr0</c>,
/// <c>t_addr_end</c>, <c>t_fanout</c> (the sender and recipient fan-out starts, after its hold under
/// Blocks.PreWarmTransactionWorkersFirst), <c>t_calldata0</c>/<c>t_calldata1</c>, <c>t_disc0</c>/<c>t_disc1</c>,
/// <c>t_jobs_out</c> (the first worker to find the jobs handed out), <c>t_lookup</c> (the txpool look-ups done),
/// <c>prep_us</c>, <c>diag_us_blk</c>, and <c>b1</c>: the flags Blocks.PreWarmEarlyStart and
/// Blocks.PreWarmTransactionWorkersFirst the block was warmed under. With the early start, Seed runs beside the workers.</item>
/// <item>Transaction: <c>gl</c>; <c>job</c> (g grouped, s split, h hoisted, l late claim), <c>rank</c>, <c>slot</c> (worker
/// slot, helpers from <see cref="HelperSlotBase"/>), <c>snd_ms</c> (late claim), <c>w0</c>/<c>w1</c> and <c>w_state</c> for
/// every run, <c>w_cold</c>/<c>w_cold_s</c> (backing-store reads, all and storage), <c>w_ops</c>/<c>w_sload</c> (a cut run
/// has <c>w_part</c>: its opcodes miss the frame segment in progress); <c>m_ops</c>, <c>m_sload</c>, <c>m_cold</c>,
/// <c>m_cold_s</c> for a transaction the main thread executed; <c>pool</c>, <c>mp_run</c>; <c>act</c> on a missing one;
/// <c>seed_skip</c> when a worker passed it over because a mempool run was seeded for it by then; <c>mp_seed</c>, what Seed
/// did with a run recorded for it (the <c>mp</c> of a missing one, on every row).</item>
/// </list>
/// Warm threads write plain array stores; the coordinator-side summaries and the look-ups are guarded and counted in
/// <c>warm_faults</c>. With BENCH_HANDOFF_DIAG_FAIL=1 the new main-thread hooks (prepare, ran), the timeline report, the
/// load summary and the look-ups throw as well.
/// </remarks>
internal static partial class HandoffDiag
{
    /// <summary>The fan-out slots whose start is kept.</summary>
    internal const int MaxSlots = 64;

    /// <summary>Helpers recruited for late senders are numbered from here in <c>slot</c>.</summary>
    internal const int HelperSlotBase = 100;

    internal const byte JobGrouped = (byte)'g';
    internal const byte JobSplit = (byte)'s';
    internal const byte JobHoisted = (byte)'h';
    internal const byte JobLate = (byte)'l';

    private static BlockFootprints? _published;
    private static long _positionBlocks;
    // The opcodes and SLOADs of the transaction the block-processing thread executes; that thread only.
    private static readonly StrongBox<ExecutionCounts> MainCounts = new();
    private static readonly PayloadTimes?[] Payloads = new PayloadTimes?[16];
    private static int _nextPayload;
    private static readonly Lock PayloadLock = new();

    // The b1 flags (Blocks.PreWarmEarlyStart, Blocks.PreWarmTransactionWorkersFirst) of the last prewarmer built.
    private static bool _earlyStart;
    private static bool _workersFirst;

    /// <summary>The prewarmer's b1 flags, printed for blocks without a session of their own.</summary>
    public static void Configure(bool earlyStart, bool workersFirst)
    {
        _earlyStart = earlyStart;
        _workersFirst = workersFirst;
    }

    /// <summary>Hands the session's footprints of the block about to be processed to <see cref="Prepare"/>.</summary>
    public static void Publish(BlockFootprints footprints, Hash256? blockHash, long prewarmTicks, bool earlyStart, bool workersFirst)
    {
        Positions positions = footprints.Diag;
        positions.BlockHash = blockHash;
        positions.Prewarm = prewarmTicks;
        positions.EarlyStart = earlyStart;
        positions.WorkersFirst = workersFirst;
        if (Enabled) Volatile.Write(ref _published, footprints);
    }

    private static void Link(BlockRecords records, Block block)
    {
        try
        {
            if (Interlocked.Exchange(ref _published, null) is { } footprints && ReferenceEquals(footprints.Diag.Transactions, block.Transactions))
            {
                records.Footprints = footprints;
            }

            if (records.FailTest) throw new InvalidOperationException("BENCH_HANDOFF_DIAG_FAIL prepare");
        }
        catch (Exception ex)
        {
            Fault(records, ex, "prepare");
        }
    }

    private static bool NextFailTest() => FailTest && Interlocked.Increment(ref _positionBlocks) % 50 == 1;

    // Inside NotTaken's guarded region: the main thread is about to execute the transaction.
    private static void StartCounting(ref TxRecord record)
    {
        record.ColdStart = ColdReadWatch.DiagReads;
        record.ColdStorageStart = ColdReadWatch.DiagStorageReads;
        MainCounts.Value = default;
        EvmMetrics.Capture(MainCounts);
        record.Counting = true;
    }

    /// <summary>The main thread executed the transaction it started; <paramref name="result"/> is passed through.</summary>
    public static TransactionResult Ran(TransactionResult result)
    {
        if (_current is not { } snapshot || snapshot.Current < 0) return result;
        long start = Stopwatch.GetTimestamp();
        ref TxRecord record = ref snapshot.Records[snapshot.Current];
        try
        {
            if (record.Counting)
            {
                EvmMetrics.Capture(null);
                record.Counting = false;
                record.MainOps = MainCounts.Value.OpCodes;
                record.MainSLoad = MainCounts.Value.Counters.SLoad;
                record.MainCold = (int)(ColdReadWatch.DiagReads - record.ColdStart);
                record.MainColdStorage = (int)(ColdReadWatch.DiagStorageReads - record.ColdStorageStart);
                record.Counted = true;
                if (snapshot.FailTest && !snapshot.MainEndFaulted)
                {
                    snapshot.MainEndFaulted = true;
                    throw new InvalidOperationException("BENCH_HANDOFF_DIAG_FAIL ran");
                }
            }
        }
        catch (Exception ex)
        {
            Fault(snapshot, ex, "ran");
        }
        finally
        {
            record.DiagTicks += Stopwatch.GetTimestamp() - start;
        }

        return result;
    }

    /// <summary>newPayload starts the early sender recovery of <paramref name="blockHash"/>.</summary>
    public static PayloadTimes? Payload(Hash256 blockHash)
    {
        if (!Enabled) return null;
        PayloadTimes times = new(blockHash);
        using (PayloadLock.EnterScope())
        {
            Payloads[_nextPayload] = times;
            _nextPayload = (_nextPayload + 1) % Payloads.Length;
        }

        return times;
    }

    // The first newPayload of the hash still kept.
    private static PayloadTimes? FindPayload(Hash256? blockHash)
    {
        if (blockHash is null) return null;
        PayloadTimes? found = null;
        using (PayloadLock.EnterScope())
        {
            foreach (PayloadTimes? times in Payloads)
            {
                if (times is not null && times.BlockHash == blockHash && (found is null || times.Payload < found.Payload)) found = times;
            }
        }

        return found;
    }

    private static double Ms(long ticks, long start) => Math.Round((ticks - start) * MsPerTick, 3);

    private static void WriteTime(Utf8JsonWriter writer, string name, long ticks, long start)
    {
        if (ticks != 0) writer.WriteNumber(name, Ms(ticks, start));
    }

    // The diag2 fields of one transaction; inside WriteTransaction's guard.
    private static void WriteRuns(Utf8JsonWriter writer, BlockRecords snapshot, int index)
    {
        ref TxRecord r = ref snapshot.Records[index];
        writer.WriteNumber("gl", snapshot.Transactions[index].GasLimit);
        if (r.Counted)
        {
            writer.WriteNumber("m_ops", r.MainOps);
            writer.WriteNumber("m_sload", r.MainSLoad);
            writer.WriteNumber("m_cold", r.MainCold);
            writer.WriteNumber("m_cold_s", r.MainColdStorage);
        }

        if (r.Outcome == Outcome.Missing && r.Active > 0) writer.WriteNumber("act", r.Active - 1);
        if (snapshot.Footprints?.Diag is not { } p || (uint)index >= (uint)p.Kind.Length) return;
        long start = snapshot.Start;
        if (p.Kind[index] != 0) writer.WriteString("job", KindName(p.Kind[index]));
        if (p.Rank[index] > 0) writer.WriteNumber("rank", p.Rank[index] - 1);
        if (p.Slot[index] > 0) writer.WriteNumber("slot", p.Slot[index] - 1);
        WriteTime(writer, "snd_ms", p.ClaimedAt[index], start);
        WriteTime(writer, "w0", p.RunStart[index], start);
        if (p.RunEnd[index] != 0)
        {
            writer.WriteNumber("w1", Ms(p.RunEnd[index], start));
            writer.WriteString("w_state", WarmName((WarmState)p.Warm[index], p.WarmReason[index]));
            writer.WriteNumber("w_cold", p.RunCold[index]);
            writer.WriteNumber("w_cold_s", p.RunColdStorage[index]);
        }

        if (p.RunOps[index] > 0)
        {
            writer.WriteNumber("w_ops", p.RunOps[index] - 1);
            writer.WriteNumber("w_sload", p.RunSLoad[index]);
            if (p.RunCountsPartial[index]) writer.WriteBoolean("w_part", true);
        }

        if (p.SeedSkip[index]) writer.WriteBoolean("seed_skip", true);
        if (p.Mempool[index] != 0) writer.WriteString("mp_seed", MempoolName(p.Mempool[index]));
        if (p.Pool[index] != 0) writer.WriteBoolean("pool", p.Pool[index] == 1);
        if (p.MempoolRun[index] != 0) writer.WriteString("mp_run", p.MempoolRun[index] == 1 ? "notrun" : WarmName((WarmState)(p.MempoolRun[index] - 2), p.MempoolRunReason[index]));
    }

    private static string KindName(byte kind) => kind switch
    {
        JobGrouped => "g",
        JobSplit => "s",
        JobHoisted => "h",
        JobLate => "l",
        _ => "unknown"
    };

    // The block's diag2 fields; inside the handoff object, after its counters.
    private static void WriteTimeline(Utf8JsonWriter writer, BlockRecords snapshot)
    {
        long start = snapshot.Start;
        long diagTicks = snapshot.PrepareTicks;
        foreach (ref readonly TxRecord record in snapshot.Records.AsSpan()) diagTicks += record.DiagTicks;
        writer.WriteNumber("prep_us", Math.Round(snapshot.PrepareTicks * MsPerTick * 1000, 1));
        writer.WriteNumber("diag_us_blk", Math.Round(diagTicks * MsPerTick * 1000, 1));
        if (snapshot.FaultHooks is not null) writer.WriteString("diag_hooks", snapshot.FaultHooks);
        Positions? p = snapshot.Footprints?.Diag;
        writer.WriteStartObject("b1");
        writer.WriteBoolean("early_start", p?.EarlyStart ?? _earlyStart);
        writer.WriteBoolean("workers_first", p?.WorkersFirst ?? _workersFirst);
        writer.WriteEndObject();
        if (p is null) return;
        try
        {
            if (FindPayload(p.BlockHash) is { } payload)
            {
                WriteTime(writer, "t_payload", payload.Payload, start);
                WriteTime(writer, "t_recov0", Volatile.Read(ref payload.RecoveryStart), start);
                WriteTime(writer, "t_recov_done", Volatile.Read(ref payload.RecoveryDone), start);
            }

            WriteTime(writer, "t_prewarm", p.Prewarm, start);
            WriteTime(writer, "t_session", p.Session, start);
            WriteTime(writer, "t_seed0", p.SeedStart, start);
            WriteTime(writer, "t_seed_end", p.SeedEnd, start);
            if (p.SeedEnd != 0)
            {
                writer.WriteNumber("seed_ms_hash", Math.Round(p.SeedHashTicks * MsPerTick, 3));
                writer.WriteNumber("seed_hashed", p.SeedHashed);
                writer.WriteNumber("seed_n", p.SeedStored);
            }

            WriteTime(writer, "t_load0", p.LoadStart, start);
            WriteTime(writer, "t_load1", p.LoadEnd, start);
            if (p.Jobs >= 0)
            {
                writer.WriteNumber("jobs", p.Jobs);
                writer.WriteNumber("hoisted", p.Hoisted);
                writer.WriteNumber("split", p.Split);
                writer.WriteNumber("unclaimed_at_load", p.UnclaimedAtLoad);
                writer.WriteNumber("recov_at_load", p.RecoveredAtLoad);
            }

            writer.WriteNumber("workers", p.Workers);
            writer.WriteNumber("workers_run", Volatile.Read(ref p.WorkersRun));
            if (p.Workers > 0)
            {
                writer.WriteStartArray("t_worker");
                for (int slot = 0; slot < Math.Min(p.Workers, MaxSlots); slot++)
                {
                    long ticks = p.WorkerStart[slot];
                    if (ticks == 0) writer.WriteNullValue();
                    else writer.WriteNumberValue(Ms(ticks, start));
                }

                writer.WriteEndArray();
            }

            writer.WriteNumber("helpers", Volatile.Read(ref p.Helpers));
            WriteTime(writer, "t_helper0", p.HelperStart, start);
            WriteTime(writer, "t_addr0", p.AddressStart, start);
            WriteTime(writer, "t_fanout", p.FanOutStart, start);
            WriteTime(writer, "t_addr_end", p.AddressEnd, start);
            WriteTime(writer, "t_calldata0", p.CalldataStart, start);
            WriteTime(writer, "t_calldata1", p.CalldataEnd, start);
            WriteTime(writer, "t_disc0", p.DiscoveryStart, start);
            WriteTime(writer, "t_disc1", p.DiscoveryEnd, start);
            WriteTime(writer, "t_jobs_out", p.JobsOut, start);
            WriteTime(writer, "t_lookup", p.LookupEnd, start);
            writer.WriteNumber("warm_faults", Volatile.Read(ref p.Faults));
            if (p.LastFault is { } fault) writer.WriteString("warm_fault", fault);
            // At the end, where no container is open: the fields above stay checkable in a faulted block.
            if (snapshot.FailTest) throw new InvalidOperationException("BENCH_HANDOFF_DIAG_FAIL timeline");
        }
        catch (Exception ex)
        {
            snapshot.ReportErrors++;
            snapshot.LastReportError ??= $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>When a payload's newPayload started its early sender recovery, and when that recovery ran.</summary>
    internal sealed class PayloadTimes(Hash256 blockHash)
    {
        public readonly Hash256 BlockHash = blockHash;
        public readonly long Payload = Stopwatch.GetTimestamp();
        public long RecoveryStart;
        public long RecoveryDone;

        public void RecoveryStarted() => Volatile.Write(ref RecoveryStart, Stopwatch.GetTimestamp());

        public void RecoveryEnded() => Volatile.Write(ref RecoveryDone, Stopwatch.GetTimestamp());
    }

    /// <remarks>The block's timeline: Stopwatch ticks, 0 when not reached.</remarks>
    internal sealed partial class Positions
    {
        public Hash256? BlockHash;
        public long Prewarm;
        public long Session;
        public long SeedStart;
        public long SeedEnd;
        public long SeedHashTicks;
        public int SeedHashed;
        public int SeedStored;
        public long LoadStart;
        public long LoadEnd;
        public int Jobs = -1;
        public int Hoisted;
        public int Split;
        public int UnclaimedAtLoad;
        public int RecoveredAtLoad;
        public int Workers;
        public int WorkersRun;
        public int Helpers;
        public long HelperStart;
        public long AddressStart;
        public long AddressEnd;
        public long FanOutStart;
        public bool EarlyStart;
        public bool WorkersFirst;
        public long CalldataStart;
        public long CalldataEnd;
        public long DiscoveryStart;
        public long DiscoveryEnd;
        public long JobsOut;
        public long LookupEnd;
        public Func<int>? ActiveWorkers;
        public int Faults;
        public string? LastFault;
        private MempoolFootprints? _recorded;

        private static long Now() => Stopwatch.GetTimestamp();

        public void SessionStarted() => Session = Now();

        public void SeedStarted() => SeedStart = Now();

        /// <summary>Seed computes the hash of <paramref name="tx"/>, which nothing computed before.</summary>
        public void SeedHash(Transaction tx)
        {
            long start = Now();
            _ = tx.Hash;
            SeedHashTicks += Now() - start;
            SeedHashed++;
        }

        public void SeedEnded(int stored)
        {
            SeedStored = stored;
            SeedEnd = Now();
        }

        public void LoadStarted() => LoadStart = Now();

        /// <summary>The queue grouped the block into <paramref name="jobs"/>, in the order its workers take them.</summary>
        public void Loaded(ArrayPoolList<BlockCachePreWarmer.WarmupJob> jobs, int txCount, int unclaimed, Func<int> active)
        {
            LoadEnd = Now();
            try
            {
                if (FailTest) throw new InvalidOperationException("BENCH_HANDOFF_DIAG_FAIL load");
                ActiveWorkers = active;
                UnclaimedAtLoad = unclaimed;
                RecoveredAtLoad = txCount - unclaimed;
                int hoisted = 0, split = 0;
                for (int rank = 0; rank < jobs.Count; rank++)
                {
                    BlockCachePreWarmer.WarmupJob job = jobs[rank];
                    byte kind = job.IsHoisted ? JobHoisted : job.Split ? JobSplit : JobGrouped;
                    if (job.IsHoisted) hoisted++;
                    if (job.Split) split++;
                    foreach ((int index, Transaction _) in job.Transactions.AsSpan())
                    {
                        if ((uint)index >= (uint)Kind.Length) continue;
                        Kind[index] = kind;
                        Rank[index] = rank + 1;
                    }
                }

                Hoisted = hoisted;
                Split = split;
                Jobs = jobs.Count;
            }
            catch (Exception ex)
            {
                Fault(ex);
            }
        }

        /// <summary>A late claim found the sender of <paramref name="position"/>; <paramref name="now"/> is taken once per scan.</summary>
        public void Claimed(int position, ref long now)
        {
            if ((uint)position >= (uint)Kind.Length) return;
            if (now == 0) now = Now();
            Kind[position] = JobLate;
            ClaimedAt[position] = now;
        }

        public void WorkersQueued(int count) => Workers = count;

        public void WorkerStarted(int slot)
        {
            if ((uint)slot < MaxSlots) WorkerStart[slot] = Now();
            Interlocked.Increment(ref WorkersRun);
        }

        /// <returns>The helper's slot.</returns>
        public int HelperStarted()
        {
            int helper = Interlocked.Increment(ref Helpers) - 1;
            if (helper == 0) HelperStart = Now();
            return HelperSlotBase + helper;
        }

        public void JobsRanOut()
        {
            if (JobsOut == 0) Interlocked.CompareExchange(ref JobsOut, Now(), 0);
        }

        public void AddressesStarted() => AddressStart = Now();

        public void AddressesEnded() => AddressEnd = Now();

        /// <summary>The sender and recipient fan-out starts, after its hold under Blocks.PreWarmTransactionWorkersFirst.</summary>
        public void FanOutStarted() => FanOutStart = Now();

        /// <summary>A late claim passed over <paramref name="position"/>: a mempool run was seeded for it by then.</summary>
        public void SeedSkipped(int position)
        {
            if ((uint)position < (uint)SeedSkip.Length) SeedSkip[position] = true;
        }

        /// <summary>A worker passed over a job whose every transaction a mempool run was seeded for by then.</summary>
        public void SeedSkipped(ReadOnlySpan<(int Index, Transaction Tx)> transactions)
        {
            foreach ((int index, Transaction _) in transactions) SeedSkipped(index);
        }

        public void CalldataStarted() => CalldataStart = Now();

        public void CalldataEnded() => CalldataEnd = Now();

        public void DiscoveryStarted() => DiscoveryStart = Now();

        public void DiscoveryEnded() => DiscoveryEnd = Now();

        /// <summary>A worker of <paramref name="slot"/> reached the warm of <paramref name="position"/>.</summary>
        public void Ran(int position, int slot)
        {
            if ((uint)position < (uint)Slot.Length) Slot[position] = (short)(slot + 1);
        }

        /// <summary>The warm of <paramref name="position"/> starts on this thread.</summary>
        public void RunStarted(int position)
        {
            if ((uint)position >= (uint)RunStart.Length) return;
            ColdStart[position] = ColdReadWatch.DiagReads;
            ColdStorageStart[position] = ColdReadWatch.DiagStorageReads;
            RunStart[position] = Now();
        }

        // From WarmEnded, on the thread RunStarted ran on.
        private void RunEnded(int position)
        {
            if (RunStart[position] == 0) return;
            RunEnd[position] = Now();
            RunCold[position] = (int)(ColdReadWatch.DiagReads - ColdStart[position]);
            RunColdStorage[position] = (int)(ColdReadWatch.DiagStorageReads - ColdStorageStart[position]);
        }

        /// <param name="partial">The run was cut: the counts are the VM's, without the frame segment in progress.</param>
        public void RunCounts(int position, long ops, long sload, bool partial)
        {
            if ((uint)position >= (uint)RunOps.Length) return;
            RunSLoad[position] = sload;
            RunCountsPartial[position] = partial;
            RunOps[position] = ops + 1;
        }

        /// <summary>
        /// Looks up, off the main thread, whether the txpool holds each transaction and what the mempool pass did with it.
        /// Only hashes already computed are looked up: computing one here would take that work off the threads measured.
        /// </summary>
        public void QueueLookups(MempoolFootprints? recorded)
        {
            if (!Enabled) return;
            _recorded = recorded;
            ThreadPool.UnsafeQueueUserWorkItem(static positions => positions.Lookup(), this, preferLocal: false);
        }

        private void Lookup()
        {
            try
            {
                if (FailTest) throw new InvalidOperationException("BENCH_HANDOFF_DIAG_FAIL lookup");
                Func<Hash256, bool>? pending = Nethermind.TxPool.TxPool.DiagIsPending;
                MempoolFootprints? recorded = _recorded;
                Transaction[] txs = Transactions;
                for (int i = 0; i < txs.Length; i++)
                {
                    Transaction tx = txs[i];
                    if (!tx.IsHashCalculated || tx.Hash is not { } hash) continue;
                    if (pending is not null && !tx.SupportsBlobs) Pool[i] = pending(hash) ? (byte)1 : (byte)2;
                    if (recorded is not null) MempoolRun[i] = recorded.DiagRun(hash, out MempoolRunReason[i]);
                }
            }
            catch (Exception ex)
            {
                Fault(ex);
            }
            finally
            {
                LookupEnd = Now();
            }
        }

        public void Fault(Exception ex)
        {
            Interlocked.Increment(ref Faults);
            LastFault ??= $"{ex.GetType().Name}: {ex.Message}";
        }
    }
}
