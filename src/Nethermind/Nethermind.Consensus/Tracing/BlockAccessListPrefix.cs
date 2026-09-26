// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Tracing;

/// <summary>A block's validated access list, indexed for reading the state before any of its transactions. The list
/// records at every block access index the post-values that index left: index 0 for the system calls ahead of the
/// transactions, i + 1 for transaction i, and one past the last transaction for what follows them. The state before
/// transaction i is therefore the parent state read through, per key, the latest change at an index in 1..i.</summary>
/// <remarks>Only the transactions' own indices are read. The system calls around them run again on every trace and
/// write into the world state above the overlay, so a key they wrote is answered there; a key a transaction wrote
/// on top of a system call has its account's storage cached by then and the overlay is refused before it is used.
/// Immutable once built, and shared by every trace of the block.</remarks>
internal sealed class BlockAccessListPrefix
{
    private const uint FirstTransactionIndex = 1;
    private const uint NeverWritten = uint.MaxValue;

    private readonly Dictionary<AddressAsKey, WrittenAccount> _accounts;

    public BlockAccessListPrefix(Hash256 blockHash, ReadOnlyBlockAccessList accessList, int transactionCount)
    {
        BlockHash = blockHash;
        TransactionCount = transactionCount;
        ReadOnlySpan<ReadOnlyAccountChanges> accounts = accessList.AccountChanges.AsSpan();
        _accounts = new Dictionary<AddressAsKey, WrittenAccount>(accounts.Length);
        foreach (ReadOnlyAccountChanges changes in accounts)
        {
            uint firstStorageWrite = FirstStorageWrite(changes.StorageChanges);
            if (firstStorageWrite == NeverWritten && !HasTransactionChange(changes)) continue;

            _accounts.Add(changes.Address, new WrittenAccount(changes, CodeHashes(changes.CodeChanges), firstStorageWrite));
        }
    }

    public Hash256 BlockHash { get; }

    public int TransactionCount { get; }

    /// <summary>The account as the transactions before <paramref name="transactionIndex"/> left it, over
    /// <paramref name="underlying"/>; null when they left it empty, which EIP-161 deletes. False when none of them
    /// changed it.</summary>
    public bool TryGetAccount(Address address, uint transactionIndex, Account? underlying, out Account? overlaid)
    {
        if (_accounts.TryGetValue(address, out WrittenAccount written)) return written.TryOverlay(transactionIndex, underlying, out overlaid);

        overlaid = null;
        return false;
    }

    /// <summary>The value the transactions before <paramref name="transactionIndex"/> left in the slot; false when none
    /// of them wrote it.</summary>
    public bool TryGetStorage(Address address, in UInt256 index, uint transactionIndex, out UInt256 value)
    {
        if (_accounts.TryGetValue(address, out WrittenAccount written) && written.FirstStorageWrite < transactionIndex + FirstTransactionIndex
            && written.Changes.TryGetDeclaredSlotChanges(index, out ReadOnlySlotChanges? slot) && slot is not null
            && slot.TryGetLastBefore(transactionIndex + FirstTransactionIndex, out StorageChange change) && change.Index >= FirstTransactionIndex)
        {
            value = change.Value;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Whether a transaction before <paramref name="transactionIndex"/> wrote any slot of the account.</summary>
    public bool HasStorageWrites(Address address, uint transactionIndex) =>
        _accounts.TryGetValue(address, out WrittenAccount written) && written.FirstStorageWrite < transactionIndex + FirstTransactionIndex;

    /// <summary>A slot's changes are sorted by index and the system calls own index 0, so the first change a
    /// transaction made is the first or the second entry.</summary>
    private static uint FirstStorageWrite(ReadOnlySlotChanges[] slots)
    {
        uint first = NeverWritten;
        foreach (ReadOnlySlotChanges slot in slots)
        {
            StorageChange[] changes = slot.Changes;
            if (changes.Length == 0) continue;

            int at = changes[0].Index >= FirstTransactionIndex ? 0 : 1;
            if (at < changes.Length && changes[at].Index < first) first = changes[at].Index;
        }

        return first;
    }

    private static bool HasTransactionChange(ReadOnlyAccountChanges changes) =>
        LastIndex(changes.BalanceChanges) >= FirstTransactionIndex
        || LastIndex(changes.NonceChanges) >= FirstTransactionIndex
        || LastIndex(changes.CodeChanges) >= FirstTransactionIndex;

    private static uint LastIndex<T>(T[] changes) where T : struct, IIndexedChange => changes.Length == 0 ? 0 : changes[^1].Index;

    /// <summary>Hashed once per block so that an account whose code a transaction changed is answered without
    /// allocating its hash on every trace.</summary>
    private static Hash256[]? CodeHashes(CodeChange[] changes)
    {
        if (changes.Length == 0) return null;

        Hash256[] hashes = new Hash256[changes.Length];
        for (int i = 0; i < changes.Length; i++)
        {
            ValueHash256 hash = changes[i].CodeHash;
            hashes[i] = hash == Keccak.OfAnEmptyString.ValueHash256 ? Keccak.OfAnEmptyString : new Hash256(in hash);
        }

        return hashes;
    }

    private readonly struct WrittenAccount(ReadOnlyAccountChanges changes, Hash256[]? codeHashes, uint firstStorageWrite)
    {
        public ReadOnlyAccountChanges Changes { get; } = changes;

        public uint FirstStorageWrite { get; } = firstStorageWrite;

        public bool TryOverlay(uint transactionIndex, Account? underlying, out Account? overlaid)
        {
            uint end = transactionIndex + FirstTransactionIndex;
            bool balanceChanged = Changes.TryGetLastBalanceChangeBefore(end, out BalanceChange balance) && balance.Index >= FirstTransactionIndex;
            bool nonceChanged = Changes.TryGetLastNonceChangeBefore(end, out NonceChange nonce) && nonce.Index >= FirstTransactionIndex;
            Hash256? codeHash = LastCodeHash(transactionIndex);
            bool storageWritten = FirstStorageWrite < end;

            if (!balanceChanged && !nonceChanged && codeHash is null)
            {
                // Storage alone changed: the fields stand, and only an empty root would hide the new slots.
                if (!storageWritten || underlying is null || underlying.HasStorage)
                {
                    overlaid = null;
                    return false;
                }

                overlaid = underlying.WithChangedStorageRoot(IStateReadOverlay.NonEmptyStorageRoot);
                return true;
            }

            Account basis = underlying ?? Account.TotallyEmpty;
            ulong nonceValue = nonceChanged ? nonce.Value : basis.Nonce;
            UInt256 balanceValue = balanceChanged ? balance.Value : basis.Balance;
            Hash256 code = codeHash ?? basis.CodeHash;
            if (nonceValue == 0 && balanceValue.IsZero && code == Keccak.OfAnEmptyString)
            {
                overlaid = null;
                return true;
            }

            Hash256 storageRoot = storageWritten && !basis.HasStorage ? IStateReadOverlay.NonEmptyStorageRoot : basis.StorageRoot;
            overlaid = new Account(nonceValue, balanceValue, storageRoot, code);
            return true;
        }

        /// <summary>Code changes only on creation and delegation, so an account carries very few of them.</summary>
        private Hash256? LastCodeHash(uint transactionIndex)
        {
            CodeChange[] changes = Changes.CodeChanges;
            for (int i = changes.Length - 1; i >= 0; i--)
            {
                uint index = changes[i].Index;
                if (index > transactionIndex) continue;

                return index >= FirstTransactionIndex ? codeHashes![i] : null;
            }

            return null;
        }
    }
}
