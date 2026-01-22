// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Collections;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

public class BlockAccessList : IEquatable<BlockAccessList>, IJournal<int>
{
    [JsonIgnore]
    public ushort Index = 0;
    public IEnumerable<AccountChanges> AccountChanges => _accountChanges.Values;

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

    public bool Equals(BlockAccessList? other) =>
        other is not null && _accountChanges.SequenceEqual(other._accountChanges);

    public override bool Equals(object? obj) =>
        obj is BlockAccessList other && Equals(other);

    public override int GetHashCode() =>
        _accountChanges.Count.GetHashCode();

    public static bool operator ==(BlockAccessList left, BlockAccessList right) =>
        left.Equals(right);

    public static bool operator !=(BlockAccessList left, BlockAccessList right) =>
        !(left == right);

    public AccountChanges? GetAccountChanges(Address address) => _accountChanges.TryGetValue(address, out AccountChanges? value) ? value : null;

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
        if (address == Address.SystemUser && before == after)
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

        if (Enumerable.SequenceEqual(before, after))
        {
            return;
        }

        bool changedDuringTx = HasCodeChangedDuringTx(accountChanges.Address, before, after);
        accountChanges.PopCodeChange(Index, out CodeChange? oldCodeChange);
        _changes.Push(new()
        {
            Address = address,
            Type = ChangeType.CodeChange,
            PreviousValue = oldCodeChange,
            PreTxCode = before
        });

        if (changedDuringTx)
        {
            accountChanges.AddCodeChange(codeChange);
        }
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

    public void AddAccountRead(Address address)
    {
        if (!_accountChanges.ContainsKey(address))
        {
            _accountChanges.Add(address, new(address));
        }
    }

    public void AddStorageChange(Address address, UInt256 key, UInt256 before, UInt256 after)
    {
        AccountChanges accountChanges = GetOrAddAccountChanges(address);

        if (before != after)
        {
            StorageChange(accountChanges, key, before, after);
        }
    }

    public void AddStorageChange(in StorageCell storageCell, UInt256 before, UInt256 after)
        => AddStorageChange(storageCell.Address, storageCell.Index, before, after);

    public void AddStorageRead(in StorageCell storageCell) =>
        AddStorageRead(storageCell.Address, storageCell.Index);

    public void AddStorageRead(Address address, UInt256 key)
    {
        AccountChanges accountChanges = GetOrAddAccountChanges(address);

        if (!accountChanges.HasStorageChange(key))
        {
            accountChanges.AddStorageRead(key);
        }
    }

    public void DeleteAccount(Address address, UInt256 oldBalance)
    {
        AccountChanges accountChanges = GetOrAddAccountChanges(address);

        // Push revertible changes for each storage change that will be cleared.
        // Push ALL changes per slot in reverse order so they restore in correct order (LIFO).
        foreach (SlotChanges slotChanges in accountChanges.StorageChanges)
        {
            ReadOnlySpan<StorageChange> changes = CollectionsMarshal.AsSpan(slotChanges.Changes);
            // Push changes in reverse order so they restore in original order
            for (int i = changes.Length - 1; i >= 0; i--)
            {
                _changes.Push(new()
                {
                    Address = address,
                    Type = ChangeType.StorageChange,
                    Slot = slotChanges.Slot,
                    PreviousValue = changes[i],
                    BlockAccessIndex = Index
                });
            }
        }

        // Push revertible changes for nonce changes (reverse order for correct restore)
        IList<NonceChange> nonceChanges = accountChanges.NonceChanges;
        int nonceCount = nonceChanges.Count;
        for (int i = nonceCount - 1; i >= 0; i--)
        {
            _changes.Push(new()
            {
                Address = address,
                Type = ChangeType.NonceChange,
                PreviousValue = nonceChanges[i],
                BlockAccessIndex = Index
            });
        }

        // Push revertible changes for code changes (reverse order for correct restore)
        IList<CodeChange> codeChanges = accountChanges.CodeChanges;
        int codeCount = codeChanges.Count;
        for (int i = codeCount - 1; i >= 0; i--)
        {
            _changes.Push(new()
            {
                Address = address,
                Type = ChangeType.CodeChange,
                PreviousValue = codeChanges[i],
                BlockAccessIndex = Index
            });
        }

        accountChanges.SelfDestruct();
        AddBalanceChange(address, oldBalance, 0);
    }

    private void StorageChange(AccountChanges accountChanges, in UInt256 key, in UInt256 before, in UInt256 after)
    {
        SlotChanges slotChanges = accountChanges.GetOrAddSlotChanges(key);

        bool changedDuringTx = HasStorageChangedDuringTx(accountChanges.Address, key, before, after);
        slotChanges.PopStorageChange(Index, out StorageChange? oldStorageChange);

        _changes.Push(new()
        {
            Address = accountChanges.Address,
            BlockAccessIndex = Index,
            Slot = key,
            Type = ChangeType.StorageChange,
            PreviousValue = oldStorageChange,
            PreTxStorage = before
        });

        if (changedDuringTx)
        {
            StorageChange storageChange = new()
            {
                BlockAccessIndex = Index,
                NewValue = after
            };

            slotChanges.Changes.Add(Index, storageChange);
            accountChanges.RemoveStorageRead(key);
        }
        else
        {
            accountChanges.ClearEmptySlotChangesAndAddRead(key);
        }
    }

    public int TakeSnapshot()
        => _changes.Count;

    public void Restore(int snapshot)
    {
        snapshot = int.Max(0, snapshot);
        while (_changes.Count > snapshot)
        {
            Change change = _changes.Pop();
            AccountChanges accountChanges = _accountChanges[change.Address];
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
                    SlotChanges slotChanges = accountChanges.GetOrAddSlotChanges(change.Slot!.Value);

                    slotChanges.PopStorageChange(Index, out _);
                    if (previousStorage is not null)
                    {
                        slotChanges.Changes.Add(previousStorage.Value.BlockAccessIndex, previousStorage.Value);
                        accountChanges.RemoveStorageRead(change.Slot.Value);
                    }

                    accountChanges.ClearEmptySlotChangesAndAddRead(change.Slot!.Value);
                    break;
            }
        }
    }

    public override string? ToString()
        => JsonSerializer.Serialize(this);

    // for testing
    internal void AddAccountChanges(params AccountChanges[] accountChanges)
        => _accountChanges.AddRange(accountChanges.ToDictionary(x => x.Address, x => x));

    private bool HasBalanceChangedDuringTx(Address address, UInt256 beforeInstr, UInt256 afterInstr)
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

    private bool HasStorageChangedDuringTx(Address address, UInt256 key, in UInt256 beforeInstr, in UInt256 afterInstr)
    {
        AccountChanges accountChanges = _accountChanges[address];

        if (!accountChanges.TryGetSlotChanges(key, out SlotChanges? slotChanges) || slotChanges.Changes.Count == 0)
        {
            // first storage change of block
            // return storage prior to this instruction
            return beforeInstr != afterInstr;
        }

        foreach (StorageChange storageChange in slotChanges.Changes.Values.AsEnumerable().Reverse())
        {
            if (storageChange.BlockAccessIndex != Index)
            {
                // storage changed in previous tx in block
                return storageChange.NewValue != afterInstr;
            }
        }

        // storage only changed within this transaction
        foreach (Change change in _changes)
        {
            if (
                change.Type == ChangeType.StorageChange &&
                change.Address == address &&
                change.Slot == key &&
                change.PreviousValue is null)
            {
                // first change of this transaction & block
                return change.PreTxStorage is null || change.PreTxStorage != afterInstr;
            }
        }

        // should never happen
        Debug.Fail("Error calculating pre tx storage");
        return true;
    }

    private bool HasCodeChangedDuringTx(Address address, in ReadOnlySpan<byte> beforeInstr, in ReadOnlySpan<byte> afterInstr)
    {
        AccountChanges accountChanges = _accountChanges[address];
        int count = accountChanges.CodeChanges.Count();

        if (count == 0)
        {
            // first code change of block
            // return code prior to this instruction
            return !Enumerable.SequenceEqual(beforeInstr.ToArray(), afterInstr.ToArray());
        }

        foreach (CodeChange codeChange in accountChanges.CodeChanges.Reverse())
        {
            if (codeChange.BlockAccessIndex != Index)
            {
                // code changed in previous tx in block
                return !Enumerable.SequenceEqual(codeChange.NewCode, afterInstr.ToArray());
            }
        }

        // storage only changed within this transaction
        foreach (Change change in _changes)
        {
            if (change.Type == ChangeType.CodeChange && change.Address == address && change.PreviousValue is null)
            {
                // first change of this transaction & block
                return change.PreTxCode is null || !Enumerable.SequenceEqual(change.PreTxCode, afterInstr.ToArray());
            }
        }

        // should never happen
        Debug.Fail("Error calculating pre tx code");
        return true;
    }

    private AccountChanges GetOrAddAccountChanges(Address address)
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
        public UInt256? Slot { get; init; }
        public ChangeType Type { get; init; }
        public IIndexedChange? PreviousValue { get; init; }
        public UInt256? PreTxBalance { get; init; }
        public UInt256? PreTxStorage { get; init; }
        public byte[]? PreTxCode { get; init; }
        public ushort BlockAccessIndex { get; init; }
    }
}
