// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
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
public sealed class ReplayBlockProcessor(
    IBlockProcessor inner,
    StateDiffStore store,
    ReplayScopeTracker tracker,
    ILogManager logManager) : IBlockProcessor
{
    private static readonly TxReceipt[] EmptyReceipts = [];
    private readonly ILogger _logger = logManager.GetClassLogger<ReplayBlockProcessor>();

    public event Action? TransactionsExecuted
    {
        add => inner.TransactionsExecuted += value;
        remove => inner.TransactionsExecuted -= value;
    }

    public (Block Block, TxReceipt[] Receipts) ProcessOne(Block suggestedBlock, ProcessingOptions options, IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token = default)
    {
        if (suggestedBlock.IsGenesis || !store.TryRead(suggestedBlock.Number, out StateDiffRecord? record))
            return inner.ProcessOne(suggestedBlock, options, blockTracer, spec, token);

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

    private static void ApplyRecord(IWorldStateScopeProvider.IScope scope, StateDiffRecord record)
    {
        if (record.Codes.Count > 0)
        {
            using IWorldStateScopeProvider.ICodeSetter codeSetter = scope.CodeDb.BeginCodeWrite();
            foreach (CodeDiff code in record.Codes) codeSetter.Set(code.CodeHash, code.Code);
        }

        using IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(record.Accounts.Count);

        // Storage first: disposing each storage batch commits its tree and marks the dirty storage root,
        // which the account flush (writeBatch.Dispose) folds back into the account's storage root.
        foreach (AccountDiff account in record.Accounts)
        {
            if (!account.StorageCleared && account.Slots.Count == 0) continue;

            using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = writeBatch.CreateStorageWriteBatch(account.Address, account.Slots.Count);
            if (account.StorageCleared) storageBatch.Clear();
            foreach (SlotDiff slot in account.Slots) storageBatch.Set(slot.Index, slot.Value);
        }

        foreach (AccountDiff account in record.Accounts)
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
}
