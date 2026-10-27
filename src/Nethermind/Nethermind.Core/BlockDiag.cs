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
        RecStart, RecEnd,
        Count
    }

    /// <summary>Timed sections, accumulated as wall ticks and thread CPU nanoseconds.</summary>
    public enum F
    {
        TxsExecutedEvent, FinCommit1, FinRewardsWithdrawals, FinCommit2, FinExecRequests, FinCommitRoots,
        WsJournal, WsStartBatch, WsStorageFlush, WsStateFlush, WsBatchDispose, WsAccountTrie,
        FinSetAccountChanges, FinStateRoot, FinReceiptsWait, FinBalHash,
        PwDrain, PreCommit, PcStorageTrees, PcStateTree, PcSnapshot,
        ShFull, ShP1, ShP2, WsfPrologue, WsfLoop,
        // When the first helper of the block's first loop of a kind started its first item, after the loop started; no CPU.
        PlStorageFirst, PlAccountFirst, PlHashFirst,
        // The storage-root step's nested loops on the block-processing thread: the outermost BulkSet and hash loops it
        // ran inside storage-root items, the join waits after its own last item of each of its nested loops at any
        // depth (no CPU), and the first helper of its first loop of each kind in the block (no CPU).
        SrNestedBs, SrNestedHash, SrNestedWait, SrNestedBsFirst, SrNestedHashFirst,
        // The block's largest storage-root item (index 0 of its first parallel loop) on the thread that ran it, and the
        // trie-node loads from persistence inside it, on that thread and on the helpers of its nested loops (no CPU).
        SrItem0, SrItem0ReadsOwn, SrItem0ReadsNested,
        // The storage-root prefetch (FlatDb.StorageRootPrefetchMinChanges): the block-processing thread's time in the
        // prefetches it ran inside storage-root items, item 0's prefetch on the thread that ran it, and item 0 from the
        // end of its prefetch to its own end (no CPU).
        SrPrefetchMain, SrItem0Prefetch, SrItem0AfterPrefetch,
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
        FusedLoops, FusedMain, FusedHelper, FusedFallback,
        ShRan, ShSettled, ShRest, ShOk, ShXFirst, ShErr,
        // Nested loops of the storage-root step: those the block-processing thread started, with their items by runner,
        // and those another thread started, with all their items.
        SrNestedBsLoops, SrNestedBsMain, SrNestedBsHelper, SrNestedHashLoops, SrNestedHashMain, SrNestedHashHelper,
        SrNestedOffLoops, SrNestedOffItems,
        // The items the block-processing thread ran in the block's first parallel storage-root loop: how many, and their
        // first, last and summed indices in the sorted list.
        SrMainItems, SrMainIdxFirst, SrMainIdxLast, SrMainIdxSum,
        // The largest item: its estimated changes, what it applied, whether the block-processing thread ran it, the
        // context switches, major faults and blocks read of its thread, and its trie-node loads from persistence.
        SrItem0Est, SrItem0Writes, SrItem0Deletes, SrItem0Skipped, SrItem0Main,
        SrItem0Vcsw, SrItem0Ivcsw, SrItem0Majflt, SrItem0Inblock, SrItem0ReadsOwn, SrItem0ReadsNested,
        // Storage-root prefetches inside storage-root items: contracts prefetched, trie nodes they read from the store and
        // levels that read any, summed; and item 0's own.
        SrPrefetchContracts, SrPrefetchLoads, SrPrefetchRounds, SrItem0PrefetchLoads, SrItem0PrefetchRounds,
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
        "rec_start", "rec_end",
    ];

    private static readonly string[] FNames =
    [
        "txs_executed_event", "commit1", "rewards_withdrawals", "commit2", "exec_requests", "commit_roots",
        "ws_journal", "ws_start_batch", "ws_storage_flush", "ws_state_flush", "ws_batch_dispose", "ws_account_trie",
        "set_account_changes", "state_root", "receipts_wait", "bal_hash",
        "pw_drain", "precommit", "pc_storage_trees", "pc_state_tree", "pc_snapshot",
        "sh_full", "sh_p1", "sh_p2", "wsf_prologue", "wsf_loop",
        "pl_storage_first", "pl_account_first", "pl_hash_first",
        "sr_nested_bs", "sr_nested_hash", "sr_nested_wait", "sr_nested_bs_first", "sr_nested_hash_first",
        "sr_item0", "sr_item0_reads_own", "sr_item0_reads_nested",
        "sr_prefetch_main", "sr_item0_prefetch", "sr_item0_after_prefetch",
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
        "fused_loops", "fused_main", "fused_helper", "fused_fallback",
        "sh_ran", "sh_settled", "sh_rest", "sh_ok", "sh_x_first", "sh_err",
        "sr_nested_bs_loops", "sr_nested_bs_main", "sr_nested_bs_helper", "sr_nested_hash_loops", "sr_nested_hash_main", "sr_nested_hash_helper",
        "sr_nested_off_loops", "sr_nested_off_items",
        "sr_main_items", "sr_main_idx_first", "sr_main_idx_last", "sr_main_idx_sum",
        "sr_item0_est", "sr_item0_writes", "sr_item0_deletes", "sr_item0_skipped", "sr_item0_main",
        "sr_item0_vcsw", "sr_item0_ivcsw", "sr_item0_majflt", "sr_item0_inblock", "sr_item0_reads_own", "sr_item0_reads_nested",
        "sr_prefetch_contracts", "sr_prefetch_loads", "sr_prefetch_rounds", "sr_item0_prefetch_loads", "sr_item0_prefetch_rounds",
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

    /// <summary>
    /// Counts one work item as <see cref="CountWorkItem(C, C)"/> does; the first item a helper starts in the loop that
    /// <paramref name="loopStart"/> timed also records into <paramref name="firstHelper"/> how long after the loop's start
    /// that was, which tells the helpers' wake-up from the imbalance of the work.
    /// </summary>
    /// <param name="loopStart">What <see cref="LoopStarted"/> returned for the loop; 0 times nothing.</param>
    public void CountWorkItem(C main, C helper, F firstHelper, long loopStart)
    {
        if (ReferenceEquals(Current, this))
        {
            Interlocked.Increment(ref _c[(int)main]);
            return;
        }

        Interlocked.Increment(ref _c[(int)helper]);
        if (loopStart != 0 && Volatile.Read(ref _wall[(int)firstHelper]) == 0)
            Interlocked.CompareExchange(ref _wall[(int)firstHelper], Math.Max(1, Stopwatch.GetTimestamp() - loopStart), 0);
    }

    private readonly long[] _loopStart = new long[(int)F.Count];

    /// <summary>Starts timing the block's first parallel loop that reports into <paramref name="firstHelper"/>; called by
    /// the block-processing thread right before the loop.</summary>
    /// <returns>The loop's start in <see cref="Stopwatch"/> ticks, or 0 when the block already timed such a loop.</returns>
    public long LoopStarted(F firstHelper)
    {
        if (_loopStart[(int)firstHelper] != 0) return 0;
        return _loopStart[(int)firstHelper] = Stopwatch.GetTimestamp();
    }

    /// <summary>The parallel loops that can run inside a storage-root item, below its storage-trie write batch.</summary>
    public enum NestedKind { BulkSet, Hash }

    // What the current thread does for the storage-root step: set for the length of a storage-root item, and of each
    // nested item another thread runs for one, so nested loops and trie-node loads count for the item they work for.
    private struct ItemContext
    {
        public BlockDiag? Diag;
        // The work is for the block's largest item, whose trie-node loads are timed.
        public bool Largest;
        // Nested loops the thread has begun and not yet ended inside the context.
        public int Depth;
        // The block-processing thread: when it last ended an item of one of its nested loops; reset as it begins one.
        public long LastItemEnd;
        // Loads accumulated on the thread until the context ends.
        public long Reads;
        public long ReadTicks;
        // The largest item: when its storage-root prefetch ended; 0 without one.
        public long PrefetchEnd;
    }

    [ThreadStatic] private static ItemContext _item;

    private bool _storageLoopSeen;

    /// <summary>Marks the block's first parallel storage-root loop; called by the block-processing thread before it.</summary>
    /// <returns>Whether this is that loop, whose item 0 is the block's largest storage-root item.</returns>
    public bool StorageLoopStarted()
    {
        if (_storageLoopSeen) return false;
        _storageLoopSeen = true;
        return true;
    }

    /// <summary>Starts a storage-root item on the current thread; dispose it when the item ends.</summary>
    /// <param name="index">The item's index in the loop's list, sorted largest first.</param>
    /// <param name="firstLoop">What <see cref="StorageLoopStarted"/> returned for the loop.</param>
    public static StorageItem EnterStorageItem(BlockDiag? diag, int index, bool firstLoop) =>
        diag is null ? default : new StorageItem(diag, index, firstLoop);

    /// <summary>Records what the block's largest storage-root item applied; called by the thread that ran it.</summary>
    /// <param name="writes">The slots it wrote, deletes included.</param>
    public void SetLargestItem(int estimated, int writes, int deletes, int skipped)
    {
        _c[(int)C.SrItem0Est] = estimated;
        _c[(int)C.SrItem0Writes] = writes - deletes;
        _c[(int)C.SrItem0Deletes] = deletes;
        _c[(int)C.SrItem0Skipped] = skipped;
    }

    // The block-processing thread only, in the block's first parallel storage-root loop.
    private void CountMainItem(int index)
    {
        if (_c[(int)C.SrMainItems]++ == 0) _c[(int)C.SrMainIdxFirst] = index;
        _c[(int)C.SrMainIdxLast] = index;
        _c[(int)C.SrMainIdxSum] += index;
    }

    private void AddReads(C count, F section, long reads, long ticks)
    {
        if (reads == 0) return;
        Interlocked.Add(ref _c[(int)count], reads);
        Interlocked.Add(ref _wall[(int)section], ticks);
    }

    /// <summary>A storage-root item on the thread that runs it.</summary>
    public readonly ref struct StorageItem
    {
        private readonly BlockDiag? _diag;
        private readonly ItemContext _previous;
        private readonly bool _largest;
        private readonly bool _main;
        private readonly long _wall0;
        private readonly long _cpu0;
        private readonly ThreadUsage _usage0;

        internal StorageItem(BlockDiag diag, int index, bool firstLoop)
        {
            _diag = diag;
            _previous = _item;
            _largest = firstLoop && index == 0;
            _main = ReferenceEquals(Current, diag);
            if (firstLoop && _main) diag.CountMainItem(index);
            _item = new ItemContext { Diag = diag, Largest = _largest };
            if (!_largest) return;
            _usage0 = ThreadUsage.Read();
            _cpu0 = ThreadCpuNs();
            _wall0 = Stopwatch.GetTimestamp();
        }

        /// <summary>Whether this is the block's largest item, index 0 of its first parallel storage-root loop.</summary>
        public bool IsLargest => _largest;

        public void Dispose()
        {
            if (_diag is not { } diag) return;
            if (_largest)
            {
                long now = Stopwatch.GetTimestamp();
                diag.AddSection(F.SrItem0, now - _wall0, ThreadCpuNs() - _cpu0);
                if (_item.PrefetchEnd != 0) diag.AddSection(F.SrItem0AfterPrefetch, now - _item.PrefetchEnd, 0);
                ThreadUsage usage = ThreadUsage.Read();
                diag._c[(int)C.SrItem0Main] = _main ? 1 : 0;
                diag._c[(int)C.SrItem0Vcsw] = usage.Vcsw - _usage0.Vcsw;
                diag._c[(int)C.SrItem0Ivcsw] = usage.Ivcsw - _usage0.Ivcsw;
                diag._c[(int)C.SrItem0Majflt] = usage.Majflt - _usage0.Majflt;
                diag._c[(int)C.SrItem0Inblock] = usage.Inblock - _usage0.Inblock;
                diag.AddReads(C.SrItem0ReadsOwn, F.SrItem0ReadsOwn, _item.Reads, _item.ReadTicks);
            }

            _item = _previous;
        }
    }

    /// <summary>
    /// Begins a parallel loop on the current thread. Inside a storage-root item it is counted, by who started it; on the
    /// block-processing thread its join wait is added up and, for the outermost loop, its time. Call
    /// <see cref="NestedLoop.End"/> on the same thread once the loop has returned.
    /// </summary>
    public static NestedLoop BeginNestedLoop(NestedKind kind)
    {
        ref ItemContext item = ref _item;
        if (item.Diag is not { } diag) return default;
        item.Depth++;
        int owner = Environment.CurrentManagedThreadId;
        if (!ReferenceEquals(Current, diag))
        {
            Interlocked.Increment(ref diag._c[(int)C.SrNestedOffLoops]);
            return new NestedLoop(diag, kind, item.Largest, byMain: false, outermost: false, owner, 0, 0, 0);
        }

        bool bulkSet = kind == NestedKind.BulkSet;
        diag._c[(int)(bulkSet ? C.SrNestedBsLoops : C.SrNestedHashLoops)]++;
        long firstStart = diag.LoopStarted(bulkSet ? F.SrNestedBsFirst : F.SrNestedHashFirst);
        // An inner loop runs inside an item of the outer one, which records its own end after it.
        item.LastItemEnd = 0;
        bool outermost = item.Depth == 1;
        long cpu0 = outermost ? ThreadCpuNs() : 0;
        return new NestedLoop(diag, kind, item.Largest, byMain: true, outermost, owner, firstStart, Stopwatch.GetTimestamp(), cpu0);
    }

    /// <summary>A parallel loop begun inside a storage-root item; <see langword="default"/> outside one.</summary>
    public readonly struct NestedLoop
    {
        private readonly BlockDiag? _diag;
        private readonly NestedKind _kind;
        private readonly bool _largest;
        private readonly bool _byMain;
        private readonly bool _outermost;
        private readonly int _owner;
        private readonly long _firstStart;
        private readonly long _start;
        private readonly long _cpu0;

        internal NestedLoop(BlockDiag diag, NestedKind kind, bool largest, bool byMain, bool outermost, int owner, long firstStart, long start, long cpu0)
        {
            _diag = diag;
            _kind = kind;
            _largest = largest;
            _byMain = byMain;
            _outermost = outermost;
            _owner = owner;
            _firstStart = firstStart;
            _start = start;
            _cpu0 = cpu0;
        }

        /// <summary>Starts one of the loop's items on the current thread; dispose it when the item ends.</summary>
        public NestedItem RunItem()
        {
            if (_diag is not { } diag) return default;
            if (_byMain)
            {
                bool bulkSet = _kind == NestedKind.BulkSet;
                diag.CountWorkItem(bulkSet ? C.SrNestedBsMain : C.SrNestedHashMain, bulkSet ? C.SrNestedBsHelper : C.SrNestedHashHelper,
                    bulkSet ? F.SrNestedBsFirst : F.SrNestedHashFirst, _firstStart);
            }
            else
            {
                Interlocked.Increment(ref diag._c[(int)C.SrNestedOffItems]);
            }

            // The thread that began the loop already works in the item's context.
            if (Environment.CurrentManagedThreadId == _owner) return _byMain ? new NestedItem(diag) : default;
            return new NestedItem(diag, _largest);
        }

        /// <summary>Ends the loop on the thread that began it, right after the loop has returned.</summary>
        public void End()
        {
            if (_diag is not { } diag) return;
            ref ItemContext item = ref _item;
            item.Depth--;
            if (!_byMain) return;

            long now = Stopwatch.GetTimestamp();
            // The waits of loops at every depth add up: an inner loop's wait falls inside an item of the outer loop,
            // before that loop's own last item ends. Without an item of its own the thread waited for the whole loop.
            diag.AddSection(F.SrNestedWait, now - (item.LastItemEnd != 0 ? item.LastItemEnd : _start), 0);
            // An inner loop's time is inside an item of the outer one, so only the outermost is timed.
            if (_outermost) diag.AddSection(_kind == NestedKind.BulkSet ? F.SrNestedBs : F.SrNestedHash, now - _start, ThreadCpuNs() - _cpu0);
        }
    }

    /// <summary>One item of a nested loop on the thread that runs it.</summary>
    public readonly ref struct NestedItem
    {
        private readonly BlockDiag? _diag;
        private readonly bool _helper;
        private readonly ItemContext _previous;

        // An item of a nested loop of the block-processing thread, run by that thread.
        internal NestedItem(BlockDiag diag) => _diag = diag;

        // An item another thread runs for the storage-root item: it takes the item's context meanwhile.
        internal NestedItem(BlockDiag diag, bool largest)
        {
            _diag = diag;
            _helper = true;
            _previous = _item;
            _item = new ItemContext { Diag = diag, Largest = largest };
        }

        public void Dispose()
        {
            if (_diag is not { } diag) return;
            if (!_helper)
            {
                _item.LastItemEnd = Stopwatch.GetTimestamp();
                return;
            }

            if (_item.Largest) diag.AddReads(C.SrItem0ReadsNested, F.SrItem0ReadsNested, _item.Reads, _item.ReadTicks);
            _item = _previous;
        }
    }

    /// <summary>
    /// Begins a storage-root prefetch on the current thread; inside a storage-root item it is stamped. Until
    /// <see cref="Prefetch.End"/>, which the same thread calls, the loads on this thread are the prefetch's, not the item's.
    /// </summary>
    public static Prefetch BeginPrefetch()
    {
        ref ItemContext item = ref _item;
        if (item.Diag is not { } diag) return default;
        bool largest = item.Largest;
        item.Largest = false;
        bool main = ReferenceEquals(Current, diag);
        return new Prefetch(diag, largest, main, Stopwatch.GetTimestamp(), main || largest ? ThreadCpuNs() : 0);
    }

    /// <summary>A storage-root prefetch begun inside a storage-root item; <see langword="default"/> outside one.</summary>
    public readonly ref struct Prefetch
    {
        private readonly BlockDiag? _diag;
        private readonly bool _largest;
        private readonly bool _main;
        private readonly long _start;
        private readonly long _cpu0;

        internal Prefetch(BlockDiag diag, bool largest, bool main, long start, long cpu0)
        {
            _diag = diag;
            _largest = largest;
            _main = main;
            _start = start;
            _cpu0 = cpu0;
        }

        /// <param name="loads">The trie nodes the prefetch read from the store.</param>
        /// <param name="rounds">Its levels that read any.</param>
        public void End(int loads, int rounds)
        {
            if (_diag is not { } diag) return;
            long now = Stopwatch.GetTimestamp();
            long cpu = _main || _largest ? ThreadCpuNs() - _cpu0 : 0;
            Interlocked.Increment(ref diag._c[(int)C.SrPrefetchContracts]);
            Interlocked.Add(ref diag._c[(int)C.SrPrefetchLoads], loads);
            Interlocked.Add(ref diag._c[(int)C.SrPrefetchRounds], rounds);
            // Each section has one writer: the block-processing thread, the thread of item 0.
            if (_main) diag.AddSection(F.SrPrefetchMain, now - _start, cpu);
            if (!_largest) return;

            ref ItemContext item = ref _item;
            item.Largest = true;
            item.PrefetchEnd = now;
            diag.AddSection(F.SrItem0Prefetch, now - _start, cpu);
            diag._c[(int)C.SrItem0PrefetchLoads] = loads;
            diag._c[(int)C.SrItem0PrefetchRounds] = rounds;
        }
    }

    /// <summary>Starts timing a trie-node load from persistence: 0 unless the current thread works for the block's
    /// largest storage-root item.</summary>
    public static long TrieReadStart() => _item.Largest ? Stopwatch.GetTimestamp() : 0;

    /// <summary>Accumulates on the current thread a load that <see cref="TrieReadStart"/> started.</summary>
    public static void TrieReadEnd(long start)
    {
        ref ItemContext item = ref _item;
        item.Reads++;
        item.ReadTicks += Stopwatch.GetTimestamp() - start;
    }

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

    /// <summary>The accumulated wall ticks and thread CPU nanoseconds of <paramref name="section"/>.</summary>
    public (long WallTicks, long CpuNs) Get(F section) => (_wall[(int)section], _cpu[(int)section]);

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

    // Linux struct rusage on 64-bit: two timevals, then fourteen longs.
    [StructLayout(LayoutKind.Sequential)]
    private struct RUsage
    {
        public long UtimeSec, UtimeUsec, StimeSec, StimeUsec;
        public long MaxRss, IxRss, IdRss, IsRss, MinFlt, MajFlt, NSwap, InBlock, OuBlock, MsgSnd, MsgRcv, NSignals, NVcsw, NIvcsw;
    }

    private const int RUsageThread = 1;

    [DllImport("libc", EntryPoint = "getrusage")]
    private static extern int GetRUsage(int who, out RUsage usage);

    /// <summary>The calling thread's voluntary and involuntary context switches, major faults and 512-byte blocks read
    /// from storage; zeros where they cannot be read.</summary>
    private readonly struct ThreadUsage(long vcsw, long ivcsw, long majflt, long inblock)
    {
        public readonly long Vcsw = vcsw;
        public readonly long Ivcsw = ivcsw;
        public readonly long Majflt = majflt;
        public readonly long Inblock = inblock;

        public static ThreadUsage Read() =>
            HasThreadCpuClock && GetRUsage(RUsageThread, out RUsage usage) == 0
                ? new ThreadUsage(usage.NVcsw, usage.NIvcsw, usage.MajFlt, usage.InBlock)
                : default;
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
