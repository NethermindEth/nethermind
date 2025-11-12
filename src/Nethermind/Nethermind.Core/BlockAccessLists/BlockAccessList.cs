// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Serialization;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

public struct BlockAccessList : IEquatable<BlockAccessList>, IJournal<int>
{
    [JsonIgnore]
    public ushort Index = 0;
    public readonly IEnumerable<AccountChanges> AccountChanges => _accountChanges.Values;

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

    public readonly AccountChanges? GetAccountChanges(Address address) => _accountChanges.TryGetValue(address, out AccountChanges? value) ? value : null;

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

        AccountChanges accountChanges = GetOrAddAccountChanges(address);

        // don't add zero balance transfers, but add empty account changes
        if (before == after)
        {
            return;
        }

        bool changedDuringTx = HasBalanceChangedDuringTx(address, before, after);
        accountChanges.PopBalanceChange(Index, out BalanceChange? oldBalanceChange);

        _changes.Push(new()
        {
            Address = address,
            Type = ChangeType.BalanceChange,
            PreviousValue = oldBalanceChange,
            PreTxBalance = before,
            BlockAccessIndex = Index
        });

        if (changedDuringTx)
        {
            accountChanges.AddBalanceChange(balanceChange);
        }
    }

    public void AddCodeChange(Address address, byte[] before, byte[] after)
    {
        CodeChange codeChange = new()
        {
            BlockAccessIndex = Index,
            NewCode = after
        };

        AccountChanges accountChanges = GetOrAddAccountChanges(address);

        // is needed?
        // need more complex logic like for storage & balance changes?
        if (Enumerable.SequenceEqual(before, after))
        {
            return;
        }

        accountChanges.PopCodeChange(Index, out CodeChange? oldCodeChange);
        _changes.Push(new()
        {
            Address = address,
            Type = ChangeType.CodeChange,
            PreviousValue = oldCodeChange
        });

        accountChanges.AddCodeChange(codeChange);
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

        AccountChanges accountChanges = GetOrAddAccountChanges(address);

        accountChanges.PopNonceChange(Index, out NonceChange? oldNonceChange);
        _changes.Push(new()
        {
            Address = address,
            Type = ChangeType.NonceChange,
            PreviousValue = oldNonceChange
        });

        accountChanges.AddNonceChange(nonceChange);
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
        AccountChanges accountChanges = GetOrAddAccountChanges(address);

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
        AccountChanges accountChanges = GetOrAddAccountChanges(address);

        if (before is null || !Enumerable.SequenceEqual(before, after))
        {
            Span<byte> key = new byte[32];
            storageCell.Index.ToBigEndian(key);
            StorageChange(accountChanges, key, before.AsSpan(), after.AsSpan());
        }
    }

    public readonly void AddStorageRead(in StorageCell storageCell)
    {
        byte[] key = new byte[32];
        storageCell.Index.ToBigEndian(key);
        AddStorageRead(storageCell.Address, key);
    }

    public readonly void AddStorageRead(Address address, byte[] key)
    {
        AccountChanges accountChanges = GetOrAddAccountChanges(address);

        if (!accountChanges.HasStorageChange(key))
        {
            accountChanges.AddStorageRead(key);
        }
    }

    public readonly void DeleteAccount(Address address)
    {
        AccountChanges accountChanges = GetOrAddAccountChanges(address);
        accountChanges.SelfDestruct();
    }

    private void StorageChange(AccountChanges accountChanges, in ReadOnlySpan<byte> key, in ReadOnlySpan<byte> before, in ReadOnlySpan<byte> after)
    {
        byte[] storageKey = [.. key];
        SlotChanges slotChanges = accountChanges.GetOrAddSlotChanges(storageKey);

        bool changedDuringTx = HasStorageChangedDuringTx(accountChanges.Address, storageKey, before, after);
        slotChanges.PopStorageChange(Index, out StorageChange? oldStorageChange);

        _changes.Push(new()
        {
            Address = accountChanges.Address,
            BlockAccessIndex = Index,
            Slot = storageKey,
            Type = ChangeType.StorageChange,
            PreviousValue = oldStorageChange,
            PreTxStorage = [.. before]
        });

        if (changedDuringTx)
        {
            byte[] newValue = new byte[32];
            after.CopyTo(newValue.AsSpan()[(32 - after.Length)..]);
            StorageChange storageChange = new()
            {
                BlockAccessIndex = Index,
                NewValue = newValue
            };

            slotChanges.Changes.Add(storageChange);
            accountChanges.RemoveStorageRead(storageKey);
        }
        else
        {
            accountChanges.ClearSlotChangesIfEmpty(storageKey);
            if (!accountChanges.HasStorageChange(storageKey))
            {
                accountChanges.AddStorageRead(storageKey);
            }
        }
    }

    public readonly int TakeSnapshot()
        => _changes.Count;

    public readonly void Restore(int snapshot)
    {
        snapshot = int.Max(0, snapshot);
        while (_changes.Count > snapshot)
        {
            Change change = _changes.Pop();
            AccountChanges accountChanges = _accountChanges[change.Address];
            // int count;
            switch (change.Type)
            {
                case ChangeType.BalanceChange:
                    BalanceChange? previousBalance = change.PreviousValue is null ? null : (BalanceChange)change.PreviousValue;

                    // balance could have gone back to pre-tx value
                    // so would already be empty
                    accountChanges.PopBalanceChange(change.BlockAccessIndex, out _); // todo: this index must be the same?
                    if (previousBalance is not null)
                    {
                        accountChanges.AddBalanceChange(previousBalance.Value);
                    }
                    break;
                case ChangeType.CodeChange:
                    CodeChange? previousCode = change.PreviousValue is null ? null : (CodeChange)change.PreviousValue;

                    accountChanges.PopCodeChange(Index, out _);
                    if (previousCode is not null)
                    {
                        accountChanges.AddCodeChange(previousCode.Value);
                    }
                    break;
                case ChangeType.NonceChange:
                    NonceChange? previousNonce = change.PreviousValue is null ? null : (NonceChange)change.PreviousValue;

                    accountChanges.PopNonceChange(Index, out _);
                    if (previousNonce is not null)
                    {
                        accountChanges.AddNonceChange(previousNonce.Value);
                    }
                    break;
                case ChangeType.StorageChange:
                    StorageChange? previousStorage = change.PreviousValue is null ? null : (StorageChange)change.PreviousValue;
                    accountChanges.TryGetSlotChanges(change.Slot!, out SlotChanges? slotChanges);

                    // replace change with read
                    accountChanges.AddStorageRead(change.Slot!);

                    slotChanges!.PopStorageChange(Index, out _);
                    if (previousStorage is not null)
                    {
                        slotChanges.Changes.Add(previousStorage.Value);
                    }

                    accountChanges.ClearSlotChangesIfEmpty(change.Slot!);
                    break;
            }
        }
    }

    public override readonly string? ToString()
        => ""; // json serialise
        // => "[\n" + string.Join(",\n", [.. _accountChanges.Values.Select(account => account.ToString())]) + "\n]";

    private readonly bool HasBalanceChangedDuringTx(Address address, UInt256 beforeInstr, UInt256 afterInstr)
    {
        AccountChanges accountChanges = _accountChanges[address];
        int count = accountChanges.BalanceChanges.Count();

        if (count == 0)
        {
            // first balance change of block
            // return balance prior to this instruction
            return beforeInstr != afterInstr;
        }

        foreach (BalanceChange balanceChange in accountChanges.BalanceChanges.Reverse())
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

        // should never happen
        Debug.Fail("Error calculating pre tx balance");
        return true;
    }

    private readonly bool HasStorageChangedDuringTx(Address address, byte[] key, in ReadOnlySpan<byte> beforeInstr, in ReadOnlySpan<byte> afterInstr)
    {
        AccountChanges accountChanges = _accountChanges[address];

        if (!accountChanges.TryGetSlotChanges(key, out SlotChanges? slotChanges))
        {
            // first storage change of block
            // return storage prior to this instruction
            return !Enumerable.SequenceEqual(beforeInstr.ToArray(), afterInstr.ToArray());
        }

        foreach (StorageChange storageChange in slotChanges.Changes.AsEnumerable().Reverse())
        {
            if (storageChange.BlockAccessIndex != Index)
            {
                // storage changed in previous tx in block
                return !Enumerable.SequenceEqual(storageChange.NewValue, afterInstr.ToArray());
            }
        }

        // storage only changed within this transaction
        foreach (Change change in _changes)
        {
            if (change.Type == ChangeType.StorageChange && change.Address == address && Enumerable.SequenceEqual(change.Slot!, key) && change.PreviousValue is null)
            {
                // first change of this transaction & block
                return change.PreTxStorage is null || !Enumerable.SequenceEqual(change.PreTxStorage, afterInstr.ToArray());
            }
        }

        // should never happen
        Debug.Fail("Error calculating pre tx storage");
        return true;
    }

    private readonly AccountChanges GetOrAddAccountChanges(Address address)
    {
        if (!_accountChanges.TryGetValue(address, out AccountChanges? existing))
        {
            AccountChanges accountChanges = new(address);
            _accountChanges.Add(address, accountChanges);
            return accountChanges;
        }
        return existing;
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
