// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Tracing;

/// <summary>A block's validated access list, read as the state before any of its transactions. The list records at
/// every block access index the post-values that index left: index 0 for the system calls ahead of the transactions,
/// i + 1 for transaction i, and one past the last transaction for what follows them. The state before transaction i
/// is therefore the parent state read through, per key, the latest change at an index in 1..i.</summary>
/// <remarks>Only the transactions' own indices are read. The system calls around them run again on every trace and
/// write into the world state above the overlay, so a key they wrote is answered there; a key a transaction wrote on
/// top of a system call has its account's storage cached by then and the overlay is refused before it is used.
/// Accounts are found through the list's own map; alongside it this keeps only the first transaction that wrote each
/// account's storage and the code the transactions deployed, keyed by hash. Immutable once built, and shared by every
/// trace of the block.</remarks>
internal sealed class BlockAccessListPrefix
{
    private const uint FirstTransactionIndex = 1;

    private readonly ReadOnlyBlockAccessList _accessList;
    private readonly Dictionary<AddressAsKey, uint> _firstStorageWrites;
    private readonly Dictionary<ValueHash256, DeployedCode> _deployedCode;

    public BlockAccessListPrefix(Hash256 blockHash, ReadOnlyBlockAccessList accessList, int transactionCount)
    {
        BlockHash = blockHash;
        TransactionCount = transactionCount;
        _accessList = accessList;
        ReadOnlySpan<ReadOnlyAccountChanges> accounts = accessList.AccountChanges.AsSpan();
        _firstStorageWrites = new Dictionary<AddressAsKey, uint>(accounts.Length);
        _deployedCode = [];
        foreach (ReadOnlyAccountChanges changes in accounts)
        {
            if (TryGetFirstStorageWrite(changes.StorageChanges, out uint first)) _firstStorageWrites.Add(changes.Address, first);
            AddDeployedCode(changes.CodeChanges);
        }
    }

    public Hash256 BlockHash { get; }

    public int TransactionCount { get; }

    /// <summary>The account as the transactions before <paramref name="transactionIndex"/> left it, over
    /// <paramref name="underlying"/>; null when they left it empty, which EIP-161 deletes. False when none of them
    /// changed it. A changed account is a new <see cref="Account"/>, the one allocation a read here can make.</summary>
    public bool TryGetAccount(Address address, uint transactionIndex, Account? underlying, out Account? overlaid)
    {
        overlaid = null;
        if (_accessList.GetAccountChanges(address) is not { } changes) return false;

        uint end = transactionIndex + FirstTransactionIndex;
        bool balanceChanged = changes.TryGetLastBalanceChangeBefore(end, out BalanceChange balance) && balance.Index >= FirstTransactionIndex;
        bool nonceChanged = changes.TryGetLastNonceChangeBefore(end, out NonceChange nonce) && nonce.Index >= FirstTransactionIndex;
        Hash256? codeHash = LastCodeHash(changes.CodeChanges, transactionIndex);
        bool storageWritten = HasStorageWrites(address, transactionIndex);

        if (!balanceChanged && !nonceChanged && codeHash is null)
        {
            // Storage alone changed: the fields stand, and only an empty root would hide the new slots.
            if (!storageWritten || underlying is null || underlying.HasStorage) return false;

            overlaid = underlying.WithChangedStorageRoot(IStateReadOverlay.NonEmptyStorageRoot);
            return true;
        }

        Account basis = underlying ?? Account.TotallyEmpty;
        ulong nonceValue = nonceChanged ? nonce.Value : basis.Nonce;
        UInt256 balanceValue = balanceChanged ? balance.Value : basis.Balance;
        Hash256 code = codeHash ?? basis.CodeHash;
        if (nonceValue == 0 && balanceValue.IsZero && code == Keccak.OfAnEmptyString) return true;

        Hash256 storageRoot = storageWritten && !basis.HasStorage ? IStateReadOverlay.NonEmptyStorageRoot : basis.StorageRoot;
        overlaid = new Account(nonceValue, balanceValue, storageRoot, code);
        return true;
    }

    /// <summary>The value the transactions before <paramref name="transactionIndex"/> left in the slot; false when none
    /// of them wrote it.</summary>
    public bool TryGetStorage(Address address, in UInt256 index, uint transactionIndex, out UInt256 value)
    {
        value = default;
        if (_accessList.GetAccountChanges(address) is not { } changes) return false;
        if (!changes.TryGetSlotChanges(in index, out ReadOnlySlotChanges? slot)) return false;
        if (!slot.TryGetLastBefore(transactionIndex + FirstTransactionIndex, out StorageChange change)) return false;
        if (change.Index < FirstTransactionIndex) return false;

        value = change.Value;
        return true;
    }

    /// <summary>Whether a transaction before <paramref name="transactionIndex"/> wrote any slot of the account.</summary>
    public bool HasStorageWrites(Address address, uint transactionIndex) =>
        _firstStorageWrites.TryGetValue(address, out uint first) && first <= transactionIndex;

    /// <summary>Code a transaction deployed, whether or not it outlived the block; the code database keeps only what
    /// the block ended with when the block was validated from its access list.</summary>
    public bool TryGetCode(in ValueHash256 codeHash, [NotNullWhen(true)] out byte[]? code)
    {
        bool found = _deployedCode.TryGetValue(codeHash, out DeployedCode deployed);
        code = deployed.Code;
        return found;
    }

    /// <summary>A slot's changes are sorted by index and the system calls own index 0, so the first change a
    /// transaction made is the first or the second entry.</summary>
    private static bool TryGetFirstStorageWrite(ReadOnlySlotChanges[] slots, out uint first)
    {
        first = uint.MaxValue;
        foreach (ReadOnlySlotChanges slot in slots)
        {
            StorageChange[] changes = slot.Changes;
            if (changes.Length == 0) continue;

            int at = changes[0].Index >= FirstTransactionIndex ? 0 : 1;
            if (at < changes.Length && changes[at].Index < first) first = changes[at].Index;
        }

        return first != uint.MaxValue;
    }

    /// <summary>Hashed once per block so that an account whose code a transaction changed is answered without
    /// allocating its hash on every trace.</summary>
    private void AddDeployedCode(CodeChange[] changes)
    {
        foreach (CodeChange change in changes)
        {
            if (change.Index < FirstTransactionIndex) continue;

            ValueHash256 hash = change.CodeHash;
            if (_deployedCode.ContainsKey(hash)) continue;

            Hash256 boxed = hash == Keccak.OfAnEmptyString.ValueHash256 ? Keccak.OfAnEmptyString : new Hash256(in hash);
            _deployedCode.Add(hash, new DeployedCode(boxed, change.Code));
        }
    }

    /// <summary>Code changes only on creation and delegation, so an account carries very few of them.</summary>
    private Hash256? LastCodeHash(CodeChange[] changes, uint transactionIndex)
    {
        for (int i = changes.Length - 1; i >= 0; i--)
        {
            uint index = changes[i].Index;
            if (index > transactionIndex) continue;
            if (index < FirstTransactionIndex) return null;

            return _deployedCode[changes[i].CodeHash].Hash;
        }

        return null;
    }

    private readonly record struct DeployedCode(Hash256 Hash, byte[] Code);
}
