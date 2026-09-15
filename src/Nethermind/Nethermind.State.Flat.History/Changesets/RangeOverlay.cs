// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>What a run of consecutive covered blocks wrote, newest block first, so that a trace of the block after
/// them reads a key those blocks touched from memory and only a key they never touched from the parent state. Each
/// node holds one block flattened to its final values and points at the node before it; a node never changes once
/// built, so any number of traces may read a chain while a newer node is added ahead of it.</summary>
internal sealed class RangeOverlay : IStateReadOverlay
{
    private readonly RangeOverlay? _older;
    private readonly Dictionary<AddressAsKey, AccountEnd> _accounts = [];
    private readonly Dictionary<StorageCell, UInt256> _slots = [];
    private readonly HashSet<AddressAsKey> _storageAccounts = [];

    private RangeOverlay(RangeOverlay? older, ulong lastBlock, Hash256 lastHash)
    {
        _older = older;
        LastBlock = lastBlock;
        LastHash = lastHash;
        Length = (older?.Length ?? 0) + 1;
        Entries = older?.Entries ?? 0;
    }

    public ulong LastBlock { get; }

    public Hash256 LastHash { get; }

    /// <summary>Blocks in the chain.</summary>
    public int Length { get; }

    /// <summary>Account and slot entries held by the whole chain, what it costs to keep.</summary>
    public long Entries { get; private set; }

    /// <summary>A new chain head: <paramref name="block"/>, folded through its last transaction, in front of
    /// <paramref name="older"/>.</summary>
    public static RangeOverlay Extend(RangeOverlay? older, MidBlockOverlay block, Hash256 blockHash)
    {
        RangeOverlay node = new(older, block.Block, blockHash);
        Dictionary<AddressAsKey, MidBlockOverlay.AccountOverlay>.Enumerator accounts = block.Accounts;
        while (accounts.MoveNext())
        {
            MidBlockOverlay.AccountOverlay account = accounts.Current.Value;
            node._accounts[accounts.Current.Key] = new AccountEnd(
                account.Nonce, account.Balance, account.CodeHash,
                Gone: account.Emptied && !account.Exists,
                Emptied: account.Emptied,
                Wiped: account.StorageClearedAt != MidBlockOverlay.NeverCleared);
        }

        Dictionary<StorageCell, MidBlockOverlay.StorageWrite>.Enumerator writes = block.Writes;
        while (writes.MoveNext())
        {
            StorageCell cell = writes.Current.Key;
            if (!block.TryGetStorage(cell, out UInt256 value)) continue;

            node._slots[cell] = value;
            node._storageAccounts.Add(cell.Address);
        }

        node.Entries += node._accounts.Count + node._slots.Count;
        return node;
    }

    /// <remarks>The storage root is the one field this chain does not maintain exactly: an account wiped in any
    /// block of the chain reports the empty root even when a later block wrote slots again, the same approximation
    /// <see cref="MidBlockReadOverlay"/> makes within one block. It is safe where this overlay is read, because
    /// storage is served slot by slot through the overlay and the scope's storage tree only ever turns an empty
    /// root into a non-empty one, never the reverse; nothing on the trace path reads the root for anything else.</remarks>
    public bool TryGetAccount(Address address, Account? underlying, out Account? overlaid)
    {
        UInt256? nonce = null;
        UInt256? balance = null;
        ValueHash256? codeHash = null;
        bool wiped = false;
        bool hit = false;
        Account? basis = underlying;
        for (RangeOverlay? node = this; node is not null; node = node._older)
        {
            if (!node._accounts.TryGetValue(address, out AccountEnd end)) continue;

            hit = true;
            if (end.Gone && nonce is null && balance is null && codeHash is null)
            {
                overlaid = null;
                return true;
            }

            nonce ??= end.Nonce;
            balance ??= end.Balance;
            codeHash ??= end.CodeHash;
            wiped |= end.Wiped;
            if (end.Emptied)
            {
                basis = Account.TotallyEmpty;
                break;
            }
        }

        if (!hit)
        {
            overlaid = null;
            return false;
        }

        basis ??= Account.TotallyEmpty;
        overlaid = new Account(
            nonce is { } n ? (ulong)n : basis.Nonce,
            balance ?? basis.Balance,
            wiped ? Keccak.EmptyTreeHash : basis.StorageRoot,
            codeHash is { } c ? (Hash256)c : basis.CodeHash);
        return true;
    }

    public bool TryGetStorage(Address address, in UInt256 index, out UInt256 value)
    {
        StorageCell cell = new(address, index);
        for (RangeOverlay? node = this; node is not null; node = node._older)
        {
            if (node._slots.TryGetValue(cell, out value)) return true;
            if (node._accounts.TryGetValue(address, out AccountEnd end) && (end.Wiped || end.Gone))
            {
                value = UInt256.Zero;
                return true;
            }
        }

        value = UInt256.Zero;
        return false;
    }

    public bool HasStorage(Address address)
    {
        for (RangeOverlay? node = this; node is not null; node = node._older)
        {
            if (node._storageAccounts.Contains(address)) return true;
            if (node._accounts.TryGetValue(address, out AccountEnd end) && (end.Wiped || end.Gone)) return true;
        }

        return false;
    }

    private readonly record struct AccountEnd(UInt256? Nonce, UInt256? Balance, ValueHash256? CodeHash, bool Gone, bool Emptied, bool Wiped);
}
