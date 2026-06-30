// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Cpu;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
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
public sealed class ReplayBlockProcessor(
    IBlockProcessor inner,
    StateDiffStore store,
    ReplayScopeTracker tracker,
    ILogManager logManager) : IBlockProcessor
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

            ApplyRecord(scope, record);

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

    private static void ApplyRecord(IWorldStateScopeProvider.IScope scope, StateDiffRecord record)
    {
        if (record.HasCodes)
        {
            using IWorldStateScopeProvider.ICodeSetter codeSetter = scope.CodeDb.BeginCodeWrite();
            foreach (StateDiffRecord.CodeView code in record.Codes) codeSetter.Set(code.CodeHash, code.Code);
        }

        using IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(0);

        // Storage first: each contract's storage is an independent trie, so it commits before the account
        // flush (writeBatch.Dispose) folds the recomputed storage root back into the account.
        ApplyStorage(record, writeBatch);

        foreach (StateDiffRecord.AccountView account in record.Accounts)
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

    private static void ApplyStorage(StateDiffRecord record, IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch)
    {
        // Opening a storage write batch mutates the scope's per-address tree map and must stay single-threaded,
        // so collect the per-contract work serially first.
        List<StorageWork> work = [];
        foreach (StateDiffRecord.AccountView account in record.Accounts)
        {
            if (!account.StorageCleared && !account.HasSlots) continue;

            List<(UInt256 Index, byte[] Value)> slots = [];
            foreach (StateDiffRecord.SlotView slot in account.Slots) slots.Add((slot.Index, slot.Value.ToArray()));
            work.Add(new StorageWork(writeBatch.CreateStorageWriteBatch(account.Address, slots.Count), account.StorageCleared, slots));
        }

        if (work.Count == 0) return;

        // The opened batches operate on independent tries, so process them concurrently (largest first to balance).
        if (work.Count >= MultiThreadThreshold)
        {
            work.Sort(static (a, b) => b.Slots.Count.CompareTo(a.Slots.Count));
            ParallelUnbalancedWork.For(0, work.Count, RuntimeInformation.ParallelOptionsPhysicalCoresUpTo16, i => ApplyOneStorage(work[i]));
        }
        else
        {
            foreach (StorageWork w in work) ApplyOneStorage(w);
        }
    }

    private static void ApplyOneStorage(StorageWork work)
    {
        using IWorldStateScopeProvider.IStorageWriteBatch batch = work.Batch;
        if (work.Cleared) batch.Clear();
        foreach ((UInt256 index, byte[] value) in work.Slots) batch.Set(index, value);
    }

    private readonly record struct StorageWork(
        IWorldStateScopeProvider.IStorageWriteBatch Batch,
        bool Cleared,
        List<(UInt256 Index, byte[] Value)> Slots);
}
