// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

public struct BlockAccessList : IEquatable<BlockAccessList>
{
    private SortedDictionary<Address, AccountChanges> _accountChanges { get; init; }
    private ushort _blockAccessIndex = 0;

    public BlockAccessList()
    {
        _accountChanges = [];
    }

    public BlockAccessList(SortedDictionary<Address, AccountChanges> accountChanges)
    {
        _accountChanges = accountChanges;
    }

    public readonly bool Equals(BlockAccessList other) =>
        _accountChanges.SequenceEqual(other._accountChanges);

    public override readonly bool Equals(object? obj) =>
        obj is BlockAccessList other && Equals(other);

    public override readonly int GetHashCode() =>
        _accountChanges.Count.GetHashCode();

    public static bool operator ==(BlockAccessList left, BlockAccessList right) =>
        left.Equals(right);

    public static bool operator !=(BlockAccessList left, BlockAccessList right) =>
        !(left == right);

    public readonly IEnumerable<AccountChanges> GetAccountChanges() => _accountChanges.Values;
    public readonly AccountChanges? GetAccountChanges(Address address) => _accountChanges.TryGetValue(address, out AccountChanges value) ? value : null;

    public void IncrementBlockAccessIndex()
        => _blockAccessIndex++;

    public void AddBalanceChange(Address address, UInt256? before, UInt256? after)
    {
        BalanceChange balanceChange = new()
        {
            BlockAccessIndex = _blockAccessIndex,
            PostBalance = after!.Value
        };

        if (!_accountChanges.TryGetValue(address, out AccountChanges accountChanges))
        {
            accountChanges = new(address);
            _accountChanges.Add(address, accountChanges);
        }

        // don't add zero balance transfers, but add empty account changes
        if ((before ?? 0) == after)
        {
            return;
        }

        SortedList<ushort, BalanceChange> balanceChanges = accountChanges.BalanceChanges;
        if (balanceChanges.Count != 0 && balanceChanges.Last().Key == _blockAccessIndex)
        {
            balanceChanges.RemoveAt(balanceChanges.Count - 1);
        }
        balanceChanges.Add(balanceChange.BlockAccessIndex, balanceChange);
    }

    public void AddCodeChange(Address address, byte[] before, byte[] after)
    {
        CodeChange codeChange = new()
        {
            BlockAccessIndex = _blockAccessIndex,
            NewCode = after
        };

        if (!_accountChanges.TryGetValue(address, out AccountChanges accountChanges))
        {
            accountChanges = new(address);
            _accountChanges.Add(address, accountChanges);
        }

        SortedList<ushort, CodeChange> codeChanges = accountChanges.CodeChanges;
        if (codeChanges.Count != 0 && codeChanges.Last().Key == _blockAccessIndex)
        {
            codeChanges.RemoveAt(codeChanges.Count - 1);
        }
        codeChanges.Add(codeChange.BlockAccessIndex, codeChange);
    }

    public void AddNonceChange(Address address, ulong newNonce)
    {
        if (newNonce == 0)
        {
            return;
        }

        NonceChange nonceChange = new()
        {
            BlockAccessIndex = _blockAccessIndex,
            NewNonce = newNonce
        };

        if (!_accountChanges.TryGetValue(address, out AccountChanges accountChanges))
        {
            accountChanges = new(address);
            _accountChanges.Add(address, accountChanges);
        }

        SortedList<ushort, NonceChange> nonceChanges = accountChanges.NonceChanges;
        if (nonceChanges.Count != 0 && nonceChanges.Last().Key == _blockAccessIndex)
        {
            nonceChanges.RemoveAt(nonceChanges.Count - 1);
        }
        nonceChanges.Add(nonceChange.BlockAccessIndex, nonceChange);
    }

    public readonly void AddAccountRead(Address address)
    {
        if (!_accountChanges.ContainsKey(address))
        {
            _accountChanges.Add(address, new(address));
        }
    }

    public void AddStorageChange(Address address, UInt256 storageIndex, ReadOnlySpan<byte> newValue, ReadOnlySpan<byte> currentValue)
    {
        if (!_accountChanges.TryGetValue(address, out AccountChanges accountChanges))
        {
            accountChanges = new(address);
            _accountChanges.Add(address, accountChanges);
        }

        if (currentValue != newValue)
        {
            StorageChange(accountChanges, new StorageCell(address, storageIndex).Hash.BytesAsSpan, newValue);
        }
    }

    public void AddStorageChange(in StorageCell storageCell, byte[] before, byte[] after)
    {
        Address address = storageCell.Address;

        if (!_accountChanges.TryGetValue(address, out AccountChanges accountChanges))
        {
            accountChanges = new(address);
            _accountChanges.Add(address, accountChanges);
        }

        if (before is null || !Enumerable.SequenceEqual(before, after))
        {
            StorageChange(accountChanges, storageCell.Hash.BytesAsSpan, after.AsSpan());
        }
    }

    public void AddStorageRead(in StorageCell storageCell)
    {
        StorageRead storageRead = new()
        {
            Key = new(storageCell.Hash.ToByteArray())
        };
        Address address = storageCell.Address;

        if (!_accountChanges.TryGetValue(address, out AccountChanges accountChanges))
        {
            accountChanges = new(address);
            _accountChanges.Add(address, accountChanges);
        }

        accountChanges.StorageReads.Add(storageRead);
    }

    private void StorageChange(AccountChanges accountChanges, in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> value)
    {
        Span<byte> newValue = stackalloc byte[32];
        newValue.Clear();
        value.CopyTo(newValue[(32 - value.Length)..]);
        StorageChange storageChange = new()
        {
            BlockAccessIndex = _blockAccessIndex,
            NewValue = new(newValue.ToArray())
        };

        byte[] storageKey = [.. key];

        if (!accountChanges.StorageChanges.TryGetValue(storageKey, out SlotChanges storageChanges))
        {
            storageChanges = new(storageKey);
        }
        else if (storageChanges.Changes is not [] && storageChanges.Changes[^1].BlockAccessIndex == _blockAccessIndex)
        {
            storageChanges.Changes.RemoveAt(storageChanges.Changes.Count - 1);
        }
        storageChanges.Changes.Add(storageChange);
        accountChanges.StorageChanges[storageKey] = storageChanges;
    }
}
