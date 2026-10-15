// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.ExceptionServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;

namespace Nethermind.Evm.State;

/// <summary>
/// Generic <see cref="IWorldStateScopeProvider.IScope.ApplyBal"/> built on the scope's own reads and write batches.
/// </summary>
public static class ScopeBalApplier
{
    /// <inheritdoc cref="IWorldStateScopeProvider.IScope.ApplyBal"/>
    /// <param name="scope">The scope to write into.</param>
    public static void Apply(IWorldStateScopeProvider.IScope scope, ReadOnlyBlockAccessList bal)
    {
        IWorldStateScopeProvider.ICodeSetter? codeSetter = null;
        // Clones deploying the same code in one block would otherwise all write it: ContainsCode only sees persisted code.
        ArrayPoolList<ValueHash256>? writtenCodeHashes = null;
        try
        {
            try
            {
                using IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(bal.AccountChanges.Count);
                StorageBatches storageBatches = new();
                try
                {
                    foreach (ReadOnlyAccountChanges accountChanges in bal.AccountChanges)
                    {
                        if (!accountChanges.HasStateChanges) continue;

                        Address address = accountChanges.Address;
                        Account? existing = scope.Get(address);
                        if (accountChanges.BalanceChanges.Length == 0 && accountChanges.NonceChanges.Length == 0 && accountChanges.CodeChanges.Length == 0)
                        {
                            // Slot writes alone are not an account change: they neither create a missing account nor touch an
                            // existing one, so EIP-158 leaves an empty account holding storage in place.
                            if (existing is not null) storageBatches.Add(writeBatch, accountChanges);
                            continue;
                        }

                        Account account = existing ?? Account.TotallyEmpty;

                        if (accountChanges.BalanceChanges.Length > 0) account = account.WithChangedBalance(accountChanges.BalanceChanges[^1].Value);
                        if (accountChanges.NonceChanges.Length > 0) account = account.WithChangedNonce(accountChanges.NonceChanges[^1].Value);
                        if (accountChanges.CodeChanges.Length > 0)
                        {
                            CodeChange codeChange = accountChanges.CodeChanges[^1];
                            if (writtenCodeHashes?.Contains(codeChange.CodeHash) != true && !scope.CodeDb.ContainsCode(codeChange.CodeHash))
                            {
                                codeSetter ??= scope.CodeDb.BeginCodeWrite();
                                codeSetter.Set(codeChange.CodeHash, codeChange.Code);
                                (writtenCodeHashes ??= new ArrayPoolList<ValueHash256>(1)).Add(codeChange.CodeHash);
                            }
                            account = account.WithChangedCodeHash(codeChange.CodeHash.ToCommitment());
                        }

                        // EIP-158 is always active with BALs (EIP-7928 postdates Spurious Dragon), so an empty account is removed.
                        if (account.IsEmpty)
                        {
                            writeBatch.Set(address, null);
                            continue;
                        }

                        // Storage creation reads the account; keep flat state and the trie aligned until that read completes.
                        storageBatches.Add(writeBatch, accountChanges);
                        writeBatch.Set(address, account);
                    }
                    storageBatches.Apply();
                }
                catch (Exception exception)
                {
                    storageBatches.Dispose(exception);
                    throw;
                }
                storageBatches.Dispose();
            }
            finally
            {
                codeSetter?.Dispose();
            }

            if (writtenCodeHashes is null) return;

            foreach (ValueHash256 codeHash in writtenCodeHashes.AsSpan())
            {
                scope.CodeDb.MarkCodePersisted(in codeHash);
            }
        }
        finally
        {
            writtenCodeHashes?.Dispose();
        }
    }

    private sealed class StorageBatches : IDisposable
    {
        private const int MinParallelBatches = 3;
        private readonly ArrayPoolList<(IWorldStateScopeProvider.IStorageWriteBatch? Batch, ReadOnlySlotChanges[] Changes)> _batches = new(0);

        public void Add(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch, ReadOnlyAccountChanges changes)
        {
            if (changes.StorageChanges.Length == 0) return;
            // Scope-owned storage-tree lookup stays serial; only independent batches are handed to workers.
            _batches.Add((writeBatch.CreateStorageWriteBatch(changes.Address, changes.StorageChanges.Length), changes.StorageChanges));
        }

        public void Apply()
        {
            if (_batches.Count < MinParallelBatches)
            {
                for (int i = 0; i < _batches.Count; i++) Apply(i);
                return;
            }

            _batches.AsSpan().Sort(static (a, b) => b.Changes.Length.CompareTo(a.Changes.Length));
            ParallelUnbalancedWork.For(0, _batches.Count, this, static (i, state) =>
            {
                state.Apply(i);
                return state;
            });
        }

        private void Apply(int index)
        {
            ref (IWorldStateScopeProvider.IStorageWriteBatch? Batch, ReadOnlySlotChanges[] Changes) work = ref _batches.GetRef(index);
            using IWorldStateScopeProvider.IStorageWriteBatch batch = work.Batch!;
            work.Batch = null;
            foreach (ReadOnlySlotChanges changes in work.Changes)
            {
                if (changes.Changes.Length > 0) batch.Set(changes.Key, changes.Changes[^1].Value);
            }
        }

        public void Dispose() => Dispose(null);

        public void Dispose(Exception? applyFailure)
        {
            ExceptionDispatchInfo? failure = null;
            // The parallel loop joins before returning or throwing; only unclaimed batches remain owned here.
            foreach ((IWorldStateScopeProvider.IStorageWriteBatch? batch, _) in _batches.AsSpan())
            {
                try { batch?.Dispose(); }
                catch (Exception exception) { failure ??= ExceptionDispatchInfo.Capture(exception); }
            }
            _batches.Dispose();
            if (failure is not null && applyFailure is not null) throw new AggregateException(applyFailure, failure.SourceException);
            failure?.Throw();
        }
    }
}
