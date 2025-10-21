// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Data;
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

    public void ResetBlockAccessIndex()
    {
        _changes.Clear();
        Index = 0;
    }

    public void AddBalanceChange(Address address, UInt256 before, UInt256 after)
    {
        if (address == Address.SystemUser)
        {
            return;
        }

        BalanceChange balanceChange = new()
        {
            BlockAccessIndex = Index,
            PostBalance = after
        };

        if (!_accountChanges.TryGetValue(address, out AccountChanges accountChanges))
        {
            accountChanges = new(address);
            _accountChanges.Add(address, accountChanges);
        }

        // don't add zero balance transfers, but add empty account changes
        if (before == after)
        {
            return;
        }

        SortedList<ushort, BalanceChange> balanceChanges = accountChanges.BalanceChanges;

        // balance change edge case
        if (!HasBalanceChangedDuringTx(address, before, after))
        {
            if (balanceChanges.Count != 0 && balanceChanges.Last().Key == Index)
            {
                balanceChanges.RemoveAt(balanceChanges.Count - 1);
            }
            return;
        }

        if (balanceChanges.Count != 0 && balanceChanges.Last().Key == Index)
        {
            _changes.Push(new()
            {
                Address = address,
                Type = ChangeType.BalanceChange,
                PreviousValue = balanceChanges.Last().Value,
                BlockAccessIndex = Index
            });

            balanceChanges.RemoveAt(balanceChanges.Count - 1);
        }
        else
        {
            _changes.Push(new()
            {
                Address = address,
                Type = ChangeType.BalanceChange,
                PreviousValue = null,
                PreTxBalance = before,
                BlockAccessIndex = Index
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
            Span<byte> key = new byte[32];
            storageIndex.ToBigEndian(key);
            StorageChange(accountChanges, key, before, after);
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
            Span<byte> key = new byte[32];
            storageCell.Index.ToBigEndian(key);
            StorageChange(accountChanges, key, before.AsSpan(), after.AsSpan());
        }
    }

    public void AddStorageRead(in StorageCell storageCell)
    {
        byte[] key = new byte[32];
        storageCell.Index.ToBigEndian(key);
        AddStorageRead(storageCell.Address, key);
    }

    public void AddStorageRead(Address address, byte[] key)
    {
        StorageRead storageRead = new()
        {
            Key = new(key)
        };

        if (!_accountChanges.TryGetValue(address, out AccountChanges accountChanges))
        {
            accountChanges = new(address);
            _accountChanges.Add(address, accountChanges);
        }

        if (!accountChanges.StorageChanges.ContainsKey(key))
        {
            accountChanges.StorageReads.Add(storageRead);
        }
    }

    public readonly void DeleteAccount(Address address)
    {
        if (!_accountChanges.TryGetValue(address, out AccountChanges accountChanges))
        {
            accountChanges = new(address);
            _accountChanges.Add(address, accountChanges);
            return;
        }

        foreach (byte[] key in accountChanges.StorageChanges.Keys)
        {
            accountChanges.StorageReads.Add(new(Bytes32.Wrap(key)));
        }

        accountChanges.StorageChanges.Clear();
        accountChanges.NonceChanges.Clear();
        accountChanges.CodeChanges.Clear();
    }

    private void StorageChange(AccountChanges accountChanges, in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> before, in ReadOnlySpan<byte> after)
    {
        Span<byte> newValue = stackalloc byte[32];
        newValue.Clear();
        after.CopyTo(newValue[(32 - after.Length)..]);
        StorageChange storageChange = new()
        {
            BlockAccessIndex = Index,
            NewValue = new(newValue.ToArray())
        };

        byte[] storageKey = [.. key];
        StorageChange? previousStorage = null;

        if (!accountChanges.StorageChanges.TryGetValue(storageKey, out SlotChanges storageChanges))
        {
            accountChanges.StorageChanges.Add(storageKey, new(storageKey));
            storageChanges = accountChanges.StorageChanges[storageKey];
        }

        // storage change edge case
        if (!HasStorageChangedDuringTx(accountChanges.Address, storageKey, before, after))
        {
            if (storageChanges.Changes is not [] && storageChanges.Changes[^1].BlockAccessIndex == Index)
            {
                storageChanges.Changes.RemoveAt(storageChanges.Changes.Count - 1);
            }

            if (storageChanges.Changes.Count == 0)
            {
                accountChanges.StorageChanges.Remove(storageKey);
            }

            if (!accountChanges.StorageChanges.ContainsKey(storageKey))
            {
                accountChanges.StorageReads.Add(new(Bytes32.Wrap(storageKey)));
            }
            return;
        }

        if (storageChanges.Changes is not [] && storageChanges.Changes[^1].BlockAccessIndex == Index)
        {
            previousStorage = storageChanges.Changes[^1];
            storageChanges.Changes.RemoveAt(storageChanges.Changes.Count - 1);
        }

        storageChanges.Changes.Add(storageChange);
        // accountChanges.StorageChanges[storageKey] = storageChanges;
        _changes.Push(new()
        {
            Address = accountChanges.Address,
            Slot = storageKey,
            Type = ChangeType.StorageChange,
            PreviousValue = previousStorage,
            PreTxStorage = [.. before]
        });

        accountChanges.StorageReads.Remove(new(Bytes32.Wrap(storageKey)));
    }

    public readonly int TakeSnapshot()
        => _changes.Count;

    // todo: can simplify to wipe all balance, code, nonce changes?
    public void Restore(int snapshot)
    {
        snapshot = int.Max(0, snapshot);
        while (_changes.Count > snapshot)
        {
            Change change = _changes.Pop();
            int count;
            switch (change.Type)
            {
                case ChangeType.BalanceChange:
                    BalanceChange? previousBalance = change.PreviousValue is null ? null : (BalanceChange)change.PreviousValue;
                    SortedList<ushort, BalanceChange> balanceChanges = _accountChanges[change.Address].BalanceChanges;

                    // balance could have gone back to pre-tx value
                    // so would already be removed
                    count = balanceChanges.Count;
                    if (count > 0 && balanceChanges.Last().Key == change.BlockAccessIndex)
                    {
                        balanceChanges.RemoveAt(balanceChanges.Count - 1);
                    }

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

                    // replace change with read
                    _accountChanges[change.Address].StorageReads.Add(new(Bytes32.Wrap(change.Slot!)));

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

    public override readonly string? ToString()
        => "[\n" + string.Join(",\n", [.. _accountChanges.Values.Select(account => account.ToString())]) + "\n]";

    private readonly bool HasBalanceChangedDuringTx(Address address, UInt256 beforeInstr, UInt256 afterInstr)
    {
        AccountChanges accountChanges = _accountChanges[address];
        int count = accountChanges.BalanceChanges.Count;

        if (count == 0)
        {
            // first balance change of block
            // return balance prior to this instruction
            return beforeInstr != afterInstr;
        }

        foreach (BalanceChange balanceChange in accountChanges.BalanceChanges.Values.Reverse())
        {
            if (balanceChange.BlockAccessIndex != Index)
            {
                // balance changed in previous tx in block
                return balanceChange.PostBalance != afterInstr;
            }
        }

        // balance only changed within this transaction
        foreach (Change change in _changes)
        {
            if (change.Type == ChangeType.BalanceChange && change.Address == address && change.PreviousValue is null)
            {
                // first change of this transaction & block
                return change.PreTxBalance!.Value != afterInstr;
            }
        }

        throw new Exception("Error calculating pre tx balance");
    }

    private readonly bool HasStorageChangedDuringTx(Address address, byte[] key, in ReadOnlySpan<byte> beforeInstr, in ReadOnlySpan<byte> afterInstr)
    {
        AccountChanges accountChanges = _accountChanges[address];
        int count = accountChanges.StorageChanges[key].Changes.Count;

        if (count == 0)
        {
            // first storage change of block
            // return storage prior to this instruction
            return beforeInstr != afterInstr;
        }

        foreach (StorageChange storageChange in accountChanges.StorageChanges[key].Changes.AsEnumerable().Reverse())
        {
            if (storageChange.BlockAccessIndex != Index)
            {
                // storage changed in previous tx in block
                return storageChange.NewValue.Unwrap().AsSpan() != afterInstr;
            }
        }

        // balance only changed within this transaction
        foreach (Change change in _changes)
        {
            if (change.Type == ChangeType.StorageChange && change.Address == address && change.Slot == key.AsSpan() && change.PreviousValue is null)
            {
                // first change of this transaction & block
                return change.PreTxStorage is null || change.PreTxStorage.AsSpan() != afterInstr;
            }
        }

        throw new Exception("Error calculating pre tx balance");
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
        public UInt256? PreTxBalance { get; init; }
        public byte[]? PreTxStorage { get; init; }
        public ushort BlockAccessIndex { get; init; }
    }
}
