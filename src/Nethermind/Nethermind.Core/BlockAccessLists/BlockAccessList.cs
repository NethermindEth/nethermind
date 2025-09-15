// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

public struct BlockAccessList : IEquatable<BlockAccessList>, IJournal<int>
{
    public ushort Index = 0;
    private readonly SortedDictionary<Address, AccountChanges> _accountChanges;
    private readonly Stack<Change> _changes;

    public BlockAccessList()
    {
        _accountChanges = [];
        _changes = new();
    }

    public BlockAccessList(SortedDictionary<Address, AccountChanges> accountChanges)
    {
        _accountChanges = accountChanges;
        _changes = new();
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
    {
        _changes.Clear();
        Index++;
    }

    public void AddBalanceChange(Address address, UInt256? before, UInt256? after)
    {
        if (address == Address.SystemUser)
        {
            return;
        }

        BalanceChange balanceChange = new()
        {
            BlockAccessIndex = Index,
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
        if (balanceChanges.Count != 0 && balanceChanges.Last().Key == Index)
        {
            _changes.Push(new()
            {
                Address = address,
                Type = ChangeType.BalanceChange,
                PreviousValue = balanceChanges.Last().Value
            });
            balanceChanges.RemoveAt(balanceChanges.Count - 1);
        }
        else
        {
            _changes.Push(new()
            {
                Address = address,
                Type = ChangeType.BalanceChange,
                PreviousValue = null
            });
        }
        balanceChanges.Add(balanceChange.BlockAccessIndex, balanceChange);
    }

    public void AddCodeChange(Address address, byte[] after)
    {
        CodeChange codeChange = new()
        {
            BlockAccessIndex = Index,
            NewCode = after
        };

        if (!_accountChanges.TryGetValue(address, out AccountChanges accountChanges))
        {
            accountChanges = new(address);
            _accountChanges.Add(address, accountChanges);
        }

        SortedList<ushort, CodeChange> codeChanges = accountChanges.CodeChanges;
        if (codeChanges.Count != 0 && codeChanges.Last().Key == Index)
        {
            _changes.Push(new()
            {
                Address = address,
                Type = ChangeType.CodeChange,
                PreviousValue = codeChanges.Last().Value
            });
            codeChanges.RemoveAt(codeChanges.Count - 1);
        }
        else
        {
            _changes.Push(new()
            {
                Address = address,
                Type = ChangeType.CodeChange,
                PreviousValue = null
            });
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
            BlockAccessIndex = Index,
            NewNonce = newNonce
        };

        if (!_accountChanges.TryGetValue(address, out AccountChanges accountChanges))
        {
            accountChanges = new(address);
            _accountChanges.Add(address, accountChanges);
        }

        SortedList<ushort, NonceChange> nonceChanges = accountChanges.NonceChanges;
        if (nonceChanges.Count != 0 && nonceChanges.Last().Key == Index)
        {
            _changes.Push(new()
            {
                Address = address,
                Type = ChangeType.NonceChange,
                PreviousValue = nonceChanges.Last().Value
            });
            nonceChanges.RemoveAt(nonceChanges.Count - 1);
        }
        else
        {
            _changes.Push(new()
            {
                Address = address,
                Type = ChangeType.NonceChange,
                PreviousValue = null
            });
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

    public void AddStorageChange(Address address, UInt256 storageIndex, ReadOnlySpan<byte> before, ReadOnlySpan<byte> after)
    {
        if (!_accountChanges.TryGetValue(address, out AccountChanges accountChanges))
        {
            accountChanges = new(address);
            _accountChanges.Add(address, accountChanges);
        }

        if (before != after)
        {
            StorageChange(accountChanges, new StorageCell(address, storageIndex).Hash.BytesAsSpan, after);
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
            BlockAccessIndex = Index,
            NewValue = new(newValue.ToArray())
        };

        byte[] storageKey = [.. key];
        StorageChange? previousStorage = null;

        if (!accountChanges.StorageChanges.TryGetValue(storageKey, out SlotChanges storageChanges))
        {
            storageChanges = new(storageKey);
        }
        else if (storageChanges.Changes is not [] && storageChanges.Changes[^1].BlockAccessIndex == Index)
        {
            previousStorage = storageChanges.Changes[^1];
            storageChanges.Changes.RemoveAt(storageChanges.Changes.Count - 1);
        }
        storageChanges.Changes.Add(storageChange);
        accountChanges.StorageChanges[storageKey] = storageChanges;
        _changes.Push(new()
        {
            Address = accountChanges.Address,
            Slot = storageKey,
            Type = ChangeType.StorageChange,
            PreviousValue = previousStorage
        });
    }

    public readonly int TakeSnapshot()
        => _changes.Count;

    public readonly void Restore(int snapshot)
    {
        snapshot = int.Max(0, snapshot);
        while (_changes.Count > snapshot)
        {
            Change change = _changes.Pop();
            switch (change.Type)
            {
                case ChangeType.BalanceChange:
                    BalanceChange? previousBalance = change.PreviousValue is null ? null : (BalanceChange)change.PreviousValue;
                    SortedList<ushort, BalanceChange> balanceChanges = _accountChanges[change.Address].BalanceChanges;

                    balanceChanges.RemoveAt(balanceChanges.Count - 1);
                    if (previousBalance is not null)
                    {
                        balanceChanges.Add(Index, previousBalance.Value);
                    }
                    break;
                case ChangeType.CodeChange:
                    CodeChange? previousCode = change.PreviousValue is null ? null : (CodeChange)change.PreviousValue;
                    SortedList<ushort, CodeChange> codeChanges = _accountChanges[change.Address].CodeChanges;

                    codeChanges.RemoveAt(codeChanges.Count - 1);
                    if (previousCode is not null)
                    {
                        codeChanges.Add(Index, previousCode.Value);
                    }
                    break;
                case ChangeType.NonceChange:
                    NonceChange? previousNode = change.PreviousValue is null ? null : (NonceChange)change.PreviousValue;
                    SortedList<ushort, NonceChange> nonceChanges = _accountChanges[change.Address].NonceChanges;

                    nonceChanges.RemoveAt(nonceChanges.Count - 1);
                    if (previousNode is not null)
                    {
                        nonceChanges.Add(Index, previousNode.Value);
                    }
                    break;
                case ChangeType.StorageChange:
                    StorageChange? previousStorage = change.PreviousValue is null ? null : (StorageChange)change.PreviousValue;
                    SlotChanges storageChanges = _accountChanges[change.Address].StorageChanges[change.Slot!];

                    storageChanges.Changes.RemoveAt(storageChanges.Changes.Count - 1);
                    if (previousStorage is not null)
                    {
                        storageChanges.Changes.Add(previousStorage.Value);
                    }

                    if (storageChanges.Changes.Count == 0)
                    {
                        _accountChanges[change.Address].StorageChanges.Remove(change.Slot!);
                    }
                    break;
            }
        }
    }

    private enum ChangeType
    {
        BalanceChange = 0,
        CodeChange = 1,
        NonceChange = 2,
        StorageChange = 3
    }
    private readonly struct Change
    {
        public Address Address { get; init; }
        public byte[]? Slot { get; init; }
        public ChangeType Type { get; init; }
        public IIndexedChange? PreviousValue { get; init; }
    }
}
