// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.Core;

/// <summary>
/// Bench-only diagnostics of one engine newPayload request: timestamps along its path from the HTTP request to the
/// response, the times of the parts of FinalizeBlock and the commit, and the block's state-change counts.
/// </summary>
/// <remarks>
/// One instance per engine request, created where the request enters (or at the engine module when it did not come
/// over JSON-RPC HTTP), carried on the payload, then on the block, and on the block-processing thread through
/// <see cref="Current"/>. Nothing is allocated per transaction. Written into the per-block "Slow block" line.
/// </remarks>
public sealed class BlockDiag
{
    /// <summary>Points on the request's path, as <see cref="Stopwatch.GetTimestamp"/> ticks; 0 = not reached.</summary>
    public enum P
    {
        HttpEntry, HttpAuth, HttpBody, RpcDispatch, RpcRented, RpcParams,
        ModEntry, ModLock, GcRegion,
        HEntry, HRlp, HRecovery, TxRootBgStart, TxRootBgEnd, HTxRoot, HDecoded, HLogged, HHash, HParent, HPreValidate,
        HValidated, HSuggested, HEnqueueCall, HEnqueueRun,
        QDequeued, PEntry, PStats, PBranch, SwStart,
        BEntry, PwCall, PwReturned, PwRun, PwWorkers, BProcessOne, TxsStart, Tx0End, TxsEnd, FinStart, FinEnd,
        Verdict, PwDrained, PreCommitEnd, SwStop,
        HResume, HReturn, RpcDone, HttpWritten, HttpComplete,
        Count
    }

    /// <summary>Timed sections, accumulated as wall ticks and thread CPU nanoseconds.</summary>
    public enum F
    {
        TxsExecutedEvent, FinCommit1, FinRewardsWithdrawals, FinCommit2, FinExecRequests, FinCommitRoots,
        WsJournal, WsStartBatch, WsStorageFlush, WsStateFlush, WsBatchDispose, WsAccountTrie,
        FinSetAccountChanges, FinStateRoot, FinReceiptsWait, FinBalHash,
        PwDrain, PreCommit, PcStorageTrees, PcStateTree, PcSnapshot,
        Count
    }

    /// <summary>Per-block counts.</summary>
    public enum C
    {
        TxCount, RootCommits, ChangedAccounts,
        StateWrites, StateSkipped, StateCreated, StateDeleted,
        StorageAccounts, SlotWrites, SlotSkipped,
        TrieAccounts, TrieStorageRoots, TrieStorageCleared,
        EarlyActive, EarlyAppliedAtBatch, EarlyApplied, EarlyReused, EarlyRestored, EarlyAbandoned,
        EarlyAdoptedSlots, EarlyAbandonedSlots, LateAccounts, PostTxAccounts,
        ParStorageLoops, ParStorageMain, ParStorageHelper, ParBulkSetLoops, ParBulkSetMain, ParBulkSetHelper,
        ParHashCalls, ParHashLoops, ParHashMain, ParHashHelper,
        Count
    }

    /// <summary>Which FinalizeBlock step the block-processing thread is in, so shared parallel loops count only there.</summary>
    public enum Phase { None, StorageRoots, AccountTrie, StateRoot }

    private static readonly string[] PNames =
    [
        "http_entry", "http_auth", "http_body", "rpc_dispatch", "rpc_rented", "rpc_params",
        "mod_entry", "mod_lock", "gc_region",
        "h_entry", "h_rlp", "h_recovery", "txroot_bg_start", "txroot_bg_end", "h_txroot", "h_decoded", "h_logged", "h_hash", "h_parent", "h_prevalidate",
        "h_validated", "h_suggested", "h_enqueue_call", "h_enqueue_run",
        "q_dequeued", "p_entry", "p_stats", "p_branch", "sw_start",
        "b_entry", "pw_call", "pw_returned", "pw_run", "pw_workers", "b_process_one", "txs_start", "tx0_end", "txs_end", "fin_start", "fin_end",
        "verdict", "pw_drained", "precommit_end", "sw_stop",
        "h_resume", "h_return", "rpc_done", "http_written", "http_complete",
    ];

    private static readonly string[] FNames =
    [
        "txs_executed_event", "commit1", "rewards_withdrawals", "commit2", "exec_requests", "commit_roots",
        "ws_journal", "ws_start_batch", "ws_storage_flush", "ws_state_flush", "ws_batch_dispose", "ws_account_trie",
        "set_account_changes", "state_root", "receipts_wait", "bal_hash",
        "pw_drain", "precommit", "pc_storage_trees", "pc_state_tree", "pc_snapshot",
    ];

    private static readonly string[] CNames =
    [
        "tx_count", "root_commits", "changed_accounts",
        "state_writes", "state_skipped", "state_created", "state_deleted",
        "storage_accounts", "slot_writes", "slot_skipped",
        "trie_accounts", "trie_storage_roots", "trie_storage_cleared",
        "early_active", "early_applied_at_batch", "early_applied", "early_reused", "early_restored", "early_abandoned",
        "early_adopted_slots", "early_abandoned_slots", "late_accounts", "post_tx_accounts",
        "par_storage_loops", "par_storage_main", "par_storage_helper", "par_bulkset_loops", "par_bulkset_main", "par_bulkset_helper",
        "par_hash_calls", "par_hash_loops", "par_hash_main", "par_hash_helper",
    ];

    private readonly long[] _t = new long[(int)P.Count];
    private readonly long[] _wall = new long[(int)F.Count];
    private readonly long[] _cpu = new long[(int)F.Count];
    private readonly long[] _c = new long[(int)C.Count];

    public Hash256? BlockHash { get; set; }

    /// <summary>The index of the transaction the block-processing thread is executing; the count after the last one.</summary>
    public int TxIndex;

    /// <summary>The FinalizeBlock step in progress on the block-processing thread.</summary>
    public Phase CurrentPhase;

    /// <summary>
    /// The diag of the block-processing thread when it is in <paramref name="phase"/>, else <see langword="null"/>: taken by
    /// the caller of a parallel loop, so the loop counts its work items only in that step.
    /// </summary>
    public static BlockDiag? InPhase(Phase phase) => Current is { } diag && diag.CurrentPhase == phase ? diag : null;

    /// <summary>Counts one work item of a parallel loop, as run by the block-processing thread or by a helper.</summary>
    public void CountWorkItem(C main, C helper) => Interlocked.Increment(ref _c[ReferenceEquals(Current, this) ? (int)main : (int)helper]);
    public ulong BlockNumber { get; set; }

    /// <summary>The request of the block the current thread is processing, if any; set by the block-processing thread.</summary>
    [field: ThreadStatic]
    public static BlockDiag? Current { get; set; }

    private static BlockDiag? _lastCompleted;

    /// <summary>The newPayload request whose response was completed last.</summary>
    public static BlockDiag? LastCompleted
    {
        get => Volatile.Read(ref _lastCompleted);
        set => Volatile.Write(ref _lastCompleted, value);
    }

    public void Stamp(P point) => _t[(int)point] = Stopwatch.GetTimestamp();

    public long Get(P point) => _t[(int)point];

    public void Add(C count, long value) => _c[(int)count] += value;

    public void Set(C count, long value) => _c[(int)count] = value;

    public long Get(C count) => _c[(int)count];

    /// <summary>Adds one timed run of <paramref name="section"/>.</summary>
    public void AddSection(F section, long wallTicks, long cpuNs)
    {
        _wall[(int)section] += wallTicks;
        _cpu[(int)section] += cpuNs;
    }

    /// <summary>Times a section while disposed; does nothing for a <see langword="null"/> diag.</summary>
    public static Section Measure(BlockDiag? diag, F section) => diag is null ? default : new Section(diag, section);

    public readonly ref struct Section
    {
        private readonly BlockDiag? _diag;
        private readonly F _section;
        private readonly long _wall0;
        private readonly long _cpu0;

        internal Section(BlockDiag diag, F section)
        {
            _diag = diag;
            _section = section;
            _cpu0 = ThreadCpuNs();
            _wall0 = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_diag is null) return;
            long wall = Stopwatch.GetTimestamp() - _wall0;
            _diag.AddSection(_section, wall, ThreadCpuNs() - _cpu0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Sec;
        public long Nsec;
    }

    private const int ClockThreadCpuTimeId = 3;

    [DllImport("libc", EntryPoint = "clock_gettime")]
    private static extern int ClockGetTime(int clockId, out Timespec time);

    private static readonly bool HasThreadCpuClock = OperatingSystem.IsLinux() && Environment.Is64BitProcess;

    /// <summary>The calling thread's CPU time in nanoseconds, or 0 where it cannot be read.</summary>
    public static long ThreadCpuNs()
    {
        if (!HasThreadCpuClock) return 0;
        return ClockGetTime(ClockThreadCpuTimeId, out Timespec ts) == 0 ? ts.Sec * 1_000_000_000L + ts.Nsec : 0;
    }

    private long Anchor => _t[(int)P.HttpEntry] != 0 ? _t[(int)P.HttpEntry] : _t[(int)P.ModEntry];

    private static double Us(long ticks) => Math.Round(ticks * 1_000_000.0 / Stopwatch.Frequency, 1);

    /// <summary>The timestamps as microseconds after the request's first point, -1 where not reached.</summary>
    public void WriteTimes(Utf8JsonWriter writer)
    {
        long anchor = Anchor;
        writer.WriteString("src", _t[(int)P.HttpEntry] != 0 ? "rpc" : "module");
        writer.WriteStartObject("t");
        for (int i = 0; i < _t.Length; i++)
        {
            long v = _t[i];
            writer.WriteNumber(PNames[i], v == 0 || anchor == 0 ? -1 : Us(v - anchor));
        }
        writer.WriteEndObject();
    }

    /// <summary>Writes the block's own "diag" object: timestamps, sections and counts.</summary>
    public void WriteJson(Utf8JsonWriter writer, string name)
    {
        writer.WriteStartObject(name);
        writer.WriteString("hash", BlockHash?.ToString() ?? "0x");
        writer.WriteNumber("number", BlockNumber);
        WriteTimes(writer);

        writer.WriteStartObject("fin");
        for (int i = 0; i < _wall.Length; i++)
        {
            writer.WriteStartArray(FNames[i]);
            writer.WriteNumberValue(Us(_wall[i]));
            writer.WriteNumberValue(Math.Round(_cpu[i] / 1000.0, 1));
            writer.WriteEndArray();
        }
        writer.WriteEndObject();

        writer.WriteStartObject("cnt");
        for (int i = 0; i < _c.Length; i++) writer.WriteNumber(CNames[i], _c[i]);
        writer.WriteEndObject();

        writer.WriteBoolean("cpu_clock", HasThreadCpuClock);
        writer.WriteEndObject();
    }

    /// <summary>Writes the timestamps of a completed request, for the line of a later block.</summary>
    public void WritePost(Utf8JsonWriter writer, string name)
    {
        writer.WriteStartObject(name);
        writer.WriteString("hash", BlockHash?.ToString() ?? "0x");
        writer.WriteNumber("number", BlockNumber);
        WriteTimes(writer);
        writer.WriteEndObject();
    }
}
