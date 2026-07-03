// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Cpu;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Logging;
using Nethermind.StateDiffArchive.Data;
using Nethermind.StateDiffArchive.Storage;

namespace Nethermind.StateDiffArchive.Replay;

/// <summary>
/// Decorates <see cref="IBlockProcessor"/> so that, when a recorded state diff exists for a block, it is
/// applied to the open world-state scope through the scope-provider write interface instead of executing
/// transactions through the EVM. Blocks without a record (genesis, or beyond the archive) fall through to
/// the inner processor for normal execution.
/// </summary>
/// <param name="parallelAccountRead">
/// When set, the touched accounts are read in parallel to warm the backend cache around the storage apply.
/// Only safe on a backend whose <c>scope.Get</c> is concurrency-safe (the flat DB); the legacy trie scope
/// resolves trie nodes in place on shared state, so it must stay <c>false</c> there.
/// </param>
public sealed class ReplayBlockProcessor(
    IBlockProcessor inner,
    StateDiffStore store,
    ReplayScopeTracker tracker,
    ILogManager logManager,
    bool parallelAccountRead) : IBlockProcessor
{
    private static readonly TxReceipt[] EmptyReceipts = [];
    private readonly ILogger _logger = logManager.GetClassLogger<ReplayBlockProcessor>();
    private int _exhaustedLogged;

    // One-block-ahead prefetch: read+decompress the next diff while the current block is being applied.
    // Touched only on the (single) main processing thread; the background read itself touches only the store.
    private Task<StateDiffRecord?>? _prefetch;
    private ulong _prefetchBlock;

    public event Action? TransactionsExecuted
    {
        add => inner.TransactionsExecuted += value;
        remove => inner.TransactionsExecuted -= value;
    }

    public (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options, IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token = default)
    {
        if (suggestedBlock.IsGenesis)
            return inner.ProcessOne(suggestedBlock, options, blockTracer, spec, token);

        StateDiffRecord? record = TakePrefetched(suggestedBlock.Number) ?? ReadRecord(suggestedBlock.Number);
        if (record is null)
        {
            LogReplayExhausted(suggestedBlock.Number);
            return inner.ProcessOne(suggestedBlock, options, blockTracer, spec, token);
        }

        // Start loading the next block's diff while we apply this one.
        StartPrefetch(suggestedBlock.Number + 1);

        using (record)
        {
            if (record.StateRoot != suggestedBlock.Header.StateRoot)
                throw new StateDiffReplayException(suggestedBlock.Number, suggestedBlock.Header.StateRoot!, record.StateRoot);

            IWorldStateScopeProvider.IScope scope = tracker.Current
                ?? throw new InvalidOperationException(
                    $"No active world-state scope to replay block {suggestedBlock.Number}; the replay scope provider is not registered.");

            ApplyRecord(scope, record, parallelAccountRead);

            // The scope provider verifies the recomputed root against this on commit.
            tracker.ExpectedRoot = suggestedBlock.Header.StateRoot;

            Metrics.BlocksReplayed++;
            Metrics.LastReplayedBlock = (long)suggestedBlock.Number;
            if (_logger.IsTrace) _logger.Trace($"Replayed state diff for block {suggestedBlock.Number}");

            return (suggestedBlock, EmptyReceipts);
        }
    }

    private StateDiffRecord? ReadRecord(ulong blockNumber)
        => store.TryRead(blockNumber, out StateDiffRecord? record) ? record : null;

    private void StartPrefetch(ulong blockNumber)
    {
        _prefetchBlock = blockNumber;
        _prefetch = Task.Run(() => ReadRecord(blockNumber));
    }

    // Wait for the in-flight prefetch (so the store is never read from two threads at once) and return its
    // record if it is for the requested block; otherwise discard it (e.g. after a reorg changed the next block).
    private StateDiffRecord? TakePrefetched(ulong blockNumber)
    {
        Task<StateDiffRecord?>? prefetch = _prefetch;
        _prefetch = null;
        if (prefetch is null) return null;

        StateDiffRecord? record = prefetch.GetAwaiter().GetResult();
        if (_prefetchBlock == blockNumber) return record;

        record?.Dispose();
        return null;
    }

    // Logs once when replay first runs out of recorded diffs and the node continues with normal processing.
    private void LogReplayExhausted(ulong blockNumber)
    {
        if (_logger.IsInfo && Interlocked.Exchange(ref _exhaustedLogged, 1) == 0)
            _logger.Info($"StateDiffArchive: no more diffs to replay at block {blockNumber}; continuing with normal block processing.");
    }

    // Matches PersistentStorageProvider.UpdateRootHashes: below this many contracts the parallel overhead isn't worth it.
    private const int MultiThreadThreshold = 3;

    // Initial pooled-list capacity for the per-block storage work; grows if a block touches more contracts.
    private const int InitialStorageCapacity = 64;

    private static void ApplyRecord(IWorldStateScopeProvider.IScope scope, StateDiffRecord record, bool parallelAccountRead)
    {
        if (record.HasCodes)
        {
            using IWorldStateScopeProvider.ICodeSetter codeSetter = scope.CodeDb.BeginCodeWrite();
            foreach (StateDiffRecord.CodeView code in record.Codes) codeSetter.Set(code.CodeHash, code.Code);
        }

        // Reproduce the recorded write batches in order: pre-Byzantium blocks commit per transaction, so one
        // slot may be written across several batches. A v1 record surfaces as a single batch. The trie is
        // committed once, at block end (ReplayScope.Commit), so intermediate roots are not recomputed.
        foreach (StateDiffRecord.WriteBatchView batch in record.Batches)
            ApplyBatch(scope, batch, parallelAccountRead);
    }

    private static void ApplyBatch(IWorldStateScopeProvider.IScope scope, StateDiffRecord.WriteBatchView batch, bool parallelAccountRead)
    {
        using IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(batch.CountAccounts());

        // Storage first: each contract's storage is an independent trie, so it commits before the account
        // flush (writeBatch.Dispose) folds the recomputed storage root back into the account.
        ApplyStorage(scope, batch, writeBatch, parallelAccountRead);

        foreach (StateDiffRecord.AccountView account in batch.Accounts)
        {
            switch (account.Change)
            {
                case AccountChangeKind.Set:
                    writeBatch.Set(account.Address, account.Account);
                    break;
                case AccountChangeKind.Deleted:
                    writeBatch.Set(account.Address, null);
                    break;
            }
        }
    }

    // Warms the backend cache for the storage-touched accounts by reading them in parallel, so the serial
    // storage-batch open (which reads each account for its storage root) hits warm entries. Only called when
    // the backend's scope.Get is concurrency-safe — the legacy trie scope resolves nodes in place on shared state.
    private static void ReadStorageAccounts(IWorldStateScopeProvider.IScope scope, StateDiffRecord.WriteBatchView batch)
    {
        using ArrayPoolList<Address> addresses = new(InitialStorageCapacity);
        foreach (StateDiffRecord.AccountView account in batch.Accounts)
            if (account.StorageCleared || account.HasSlots) addresses.Add(account.Address);

        if (addresses.Count >= MultiThreadThreshold)
            ParallelUnbalancedWork.For(0, addresses.Count, RuntimeInformation.ParallelOptionsPhysicalCoresUpTo16, i => scope.Get(addresses[i]));
        else
            foreach (Address address in addresses) scope.Get(address);
    }

    // Reads the accounts with no storage work (the ones ReadStorageAccounts skips) to trigger the trie warmer for
    // their state-trie paths, overlapping the storage compile/apply. Best-effort: honors cancellation, so it stops
    // as soon as the storage apply completes and the account writes take over. Flat backend only.
    private static void WarmNonStorageAccounts(IWorldStateScopeProvider.IScope scope, StateDiffRecord.WriteBatchView batch, CancellationToken token)
    {
        using ArrayPoolList<Address> addresses = new(InitialStorageCapacity);
        foreach (StateDiffRecord.AccountView account in batch.Accounts)
            if (!account.StorageCleared && !account.HasSlots) addresses.Add(account.Address);

        if (addresses.Count == 0) return;

        ParallelOptions options = new()
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = RuntimeInformation.ParallelOptionsPhysicalCoresUpTo16.MaxDegreeOfParallelism
        };

        try
        {
            ParallelUnbalancedWork.For(0, addresses.Count, options, i => scope.Get(addresses[i]));
        }
        catch (OperationCanceledException)
        {
            // Stopped once the storage apply finished; the remaining accounts are read by the state-apply flush.
        }
    }

    private static void ApplyStorage(IWorldStateScopeProvider.IScope scope, StateDiffRecord.WriteBatchView batch, IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch, bool parallelAccountRead)
    {
        // Opening a storage write batch below reads the account for its storage root; that read can be warmed
        // ahead of time by reading the touched accounts in parallel (flat backend only — see ReadStorageAccounts).
        if (parallelAccountRead) ReadStorageAccounts(scope, batch);

        // While the (serial) compile and (parallel) apply run, warm the non-storage accounts in the background so
        // the state-tree flush that follows hits warm trie paths. Cancelled and joined before returning, since the
        // account writes then take over the same accounts. Flat backend only (concurrency-safe scope.Get).
        CancellationTokenSource? warmupCts = parallelAccountRead ? new CancellationTokenSource() : null;
        Task? warmup = warmupCts is null ? null : Task.Run(() => WarmNonStorageAccounts(scope, batch, warmupCts.Token));

        try
        {
            // Opening a storage write batch mutates the scope's per-address tree map and must stay single-threaded,
            // so open them serially. Each work item keeps only the record-backed slot region; the slots are parsed
            // and written by the worker, so nothing is materialized here.
            using ArrayPoolList<StorageWork> work = new(InitialStorageCapacity);
            foreach (StateDiffRecord.AccountView account in batch.Accounts)
            {
                if (!account.StorageCleared && !account.HasSlots) continue;

                // Count the slots once (a light length-prefix walk) to size the storage batch and to key the sort;
                // the slots themselves are parsed and written by the worker.
                StateDiffRecord.SlotSet slots = account.Slots;
                int slotCount = slots.Count();
                work.Add(new StorageWork(writeBatch.CreateStorageWriteBatch(account.Address, slotCount), account.StorageCleared, slots, slotCount));
            }

            if (work.Count == 0) return;

            // Independent tries, so process the opened batches concurrently. Schedule the largest first to help balance.
            if (work.Count >= MultiThreadThreshold)
            {
                work.AsSpan().Sort(static (a, b) => b.SlotCount.CompareTo(a.SlotCount));
                ParallelUnbalancedWork.For(0, work.Count, RuntimeInformation.ParallelOptionsPhysicalCoresUpTo16, i => ApplyOneStorage(work[i]));
            }
            else
            {
                foreach (StorageWork w in work) ApplyOneStorage(w);
            }
        }
        finally
        {
            if (warmup is not null)
            {
                warmupCts!.Cancel();
                warmup.GetAwaiter().GetResult();
                warmupCts.Dispose();
            }
        }
    }

    private static void ApplyOneStorage(StorageWork work)
    {
        using IWorldStateScopeProvider.IStorageWriteBatch batch = work.Batch;
        if (work.Cleared) batch.Clear();
        foreach (StateDiffRecord.SlotView slot in work.Slots) batch.Set(slot.Index, slot.Value.ToArray());
    }

    private readonly record struct StorageWork(
        IWorldStateScopeProvider.IStorageWriteBatch Batch,
        bool Cleared,
        StateDiffRecord.SlotSet Slots,
        int SlotCount);
}
