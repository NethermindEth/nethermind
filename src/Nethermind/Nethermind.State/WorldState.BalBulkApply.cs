// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;

namespace Nethermind.State;

public sealed partial class WorldState : IBalBulkWorldState
{
    /// <inheritdoc/>
    /// <remarks>
    /// Per account (sequentially): the parent account is read through the scope (warm from the BAL
    /// read warmup), <see cref="BalPostState.Compute"/> derives the post-block account, and the
    /// result goes straight into the scope's <see cref="IWorldStateScopeProvider.IWorldStateWriteBatch"/>
    /// — no journal entries, no per-operation balance/nonce bookkeeping. Storage batches are then
    /// filled and disposed in parallel (each owns an independent per-account tree whose root is
    /// computed at its dispose); the outer batch's dispose reconciles those roots into the account
    /// leaves. Block-level change tracking is fed via <see cref="StateProvider.TrackBulkAppliedState"/>
    /// so <see cref="GetAccountChanges"/> (TxPool cache invalidation) matches the journaled path.
    /// </remarks>
    public void BulkApplyBal(ReadOnlyBlockAccessList bal, IReleaseSpec spec)
    {
        GuardInScope();
        // Read-through traces (Before == After) are expected — pre-block validation reads populate
        // them; only unflushed WRITES violate the precondition, as a later FlushToTree would clobber
        // the bulk-applied values with the stale journal entries.
        Debug.Assert(!_stateProvider.HasPendingBlockChanges(),
            "BulkApplyBal must not run with unflushed journal writes — a later FlushToTree would overwrite the bulk-applied values.");

        IWorldStateScopeProvider.IScope scope = _currentScope!;
        using ArrayPoolList<(IWorldStateScopeProvider.IStorageWriteBatch Batch, ReadOnlyAccountChanges Changes)> storageBatches = new(bal.ItemCount);
        using IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(bal.ItemCount);
        using IWorldStateScopeProvider.ICodeSetter codeSetter = scope.CodeDb.BeginCodeWrite();

        // Storage-root fixups fired at the batch's dispose must land in the change tracking
        // too, so any later read through the journal sees the reconciled account.
        batch.OnAccountUpdated += (_, updated) => _stateProvider.TrackBulkAppliedState(updated.Address, updated.Account);

        foreach (ReadOnlyAccountChanges accountChanges in bal.AccountChanges)
        {
            Address address = accountChanges.Address;
            Account? parent = scope.Get(address);
            Account? post = BalPostState.Compute(parent, accountChanges, spec);

            if (!ReferenceEquals(post, parent))
            {
                batch.Set(address, post);
                _stateProvider.TrackBulkAppliedState(address, post);
            }

            if (post is null)
            {
                // Absent post-block (EIP-158 empty or never materialized): Set(null) drops the
                // account (and clears its storage where the backend keeps one); the row's slot
                // writes go with it, mirroring the journaled commit's sweep.
                continue;
            }

            if (accountChanges.CodeChanges.Length > 0)
            {
                CodeChange codeChange = accountChanges.CodeChanges[^1];
                if (codeChange.Code is { Length: > 0 })
                {
                    codeSetter.Set(codeChange.CodeHash, codeChange.Code);
                }
            }

            if (HasStorageWrites(accountChanges))
            {
                storageBatches.Add((batch.CreateStorageWriteBatch(address, accountChanges.ChangedSlots.Length), accountChanges));
            }
        }

        // Each storage batch owns an independent per-account tree — fill and dispose in
        // parallel; the root computation happens inside each dispose. The outer batch's dispose
        // (end of method) then reconciles the computed roots into the account leaves.
        if (storageBatches.Count == 1)
        {
            WriteAndDisposeStorageBatch(storageBatches[0]);
        }
        else if (storageBatches.Count > 1)
        {
            ArrayPoolList<(IWorldStateScopeProvider.IStorageWriteBatch, ReadOnlyAccountChanges)> batches = storageBatches;
            Parallel.For(0, storageBatches.Count, i => WriteAndDisposeStorageBatch(batches[i]));
        }
    }

    private static bool HasStorageWrites(ReadOnlyAccountChanges accountChanges)
    {
        foreach (ReadOnlySlotChanges slotChanges in accountChanges.StorageChanges)
        {
            if (slotChanges.Changes.Length > 0) return true;
        }

        return false;
    }

    private static void WriteAndDisposeStorageBatch((IWorldStateScopeProvider.IStorageWriteBatch Batch, ReadOnlyAccountChanges Changes) entry)
    {
        using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = entry.Batch;
        foreach (ReadOnlySlotChanges slotChanges in entry.Changes.StorageChanges)
        {
            if (slotChanges.Changes.Length == 0) continue;

            // StorageChange.Value is EvmWord (Vector256<byte>) in big-endian wire form; the batch
            // expects the stripped bytes, and a zero value (empty bytes) is a slot delete.
            EvmWord value = slotChanges.Changes[^1].Value;
            ReadOnlySpan<byte> valueBytes = MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.As<EvmWord, byte>(ref value), 32);
            storageBatch.Set(slotChanges.Key, [.. valueBytes.WithoutLeadingZeros()]);
        }
    }
}
