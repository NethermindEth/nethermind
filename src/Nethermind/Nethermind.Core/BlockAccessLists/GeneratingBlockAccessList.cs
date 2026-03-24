// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using Nethermind.Int256;

[assembly: InternalsVisibleTo("Nethermind.Core.Test")]
[assembly: InternalsVisibleTo("Nethermind.State.Test")]

namespace Nethermind.Core.BlockAccessLists;

public class GeneratingBlockAccessList : IJournal<int>
{
    [JsonIgnore]
    public int Index = 0;

    /// storage keys across all accounts + addresses
    [JsonIgnore]
    public int ItemCount { get; set; }

    public bool HasAccount(Address address) => _accountChanges.ContainsKey(address);

    private readonly SortedDictionary<Address, GeneratingAccountChanges> _accountChanges = [];
    private readonly Stack<Change> _changes = new();

    public GeneratingBlockAccessList()
    {
    }

    // public GeneratingBlockAccessList(SortedDictionary<Address, AccountChanges> accountChanges)
    // {
    //     _accountChanges = accountChanges;
    // }

    public GeneratingAccountChanges? GetAccountChanges(Address address) => _accountChanges.TryGetValue(address, out GeneratingAccountChanges? value) ? value : null;

    public void AddBalanceChange(Address address, UInt256 before, UInt256 after)
    {
        bool isZeroBalanceChange = before == after;
        if (address == Address.SystemUser && isZeroBalanceChange)
        {
            return;
        }

        GeneratingAccountChanges accountChanges = GetOrAddAccountChanges(address);

        // don't add zero balance transfers, but add empty account changes
        if (isZeroBalanceChange)
        {
            return;
        }

        bool changedDuringTx = HasBalanceChangedDuringTx(address, before, after);
        accountChanges.PopBalanceChange(out BalanceChange? oldBalanceChange);

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
            accountChanges.AddBalanceChange(new(Index, after));
        }
    }

    public void AddCodeChange(Address address, byte[] before, ReadOnlyMemory<byte> after)
    {
        GeneratingAccountChanges accountChanges = GetOrAddAccountChanges(address);

        if (before.AsSpan().SequenceEqual(after.Span))
        {
            return;
        }

        bool changedDuringTx = HasCodeChangedDuringTx(address, before, after.Span);
        accountChanges.PopCodeChange(out CodeChange? oldCodeChange);
        _changes.Push(new()
        {
            Address = address,
            Type = ChangeType.CodeChange,
            PreviousValue = oldCodeChange,
            PreTxCode = before
            // N.B. don't need PreTxCode as SELFDESTRUCT cannot be first code change of tx
        });

        if (changedDuringTx)
        {
            accountChanges.AddCodeChange(new(Index, after.ToArray()));
        }
    }

    public void AddNonceChange(Address address, ulong newNonce)
    {
        if (newNonce == 0)
        {
            return;
        }

        GeneratingAccountChanges accountChanges = GetOrAddAccountChanges(address);

        accountChanges.PopNonceChange(out NonceChange? oldNonceChange);
        _changes.Push(new()
        {
            Address = address,
            Type = ChangeType.NonceChange,
            PreviousValue = oldNonceChange
        });

        accountChanges.AddNonceChange(new(Index, newNonce));
    }

    public void AddAccountRead(Address address)
    {
        if (!_accountChanges.ContainsKey(address))
        {
            _accountChanges.Add(address, new());
        }
    }

    public void AddStorageChange(Address address, UInt256 key, UInt256 before, UInt256 after)
    {
        GeneratingAccountChanges accountChanges = GetOrAddAccountChanges(address);

        if (before != after)
        {
            StorageChange(address, accountChanges, key, before, after);
        }
    }

    public void AddStorageChange(in StorageCell storageCell, UInt256 before, UInt256 after)
        => AddStorageChange(storageCell.Address, storageCell.Index, before, after);

    public void AddStorageRead(in StorageCell storageCell) =>
        AddStorageRead(storageCell.Address, storageCell.Index);

    public void AddStorageRead(Address address, UInt256 key)
    {
        GeneratingAccountChanges accountChanges = GetOrAddAccountChanges(address);

        if (!accountChanges.HasStorageChange(key))
        {
            accountChanges.AddStorageRead(key);
        }
    }

    public void DeleteAccount(Address address, UInt256 oldBalance)
    {
        GeneratingAccountChanges accountChanges = GetOrAddAccountChanges(address);

        // Push revertible changes for each storage change that will be cleared.
        // Push ALL changes per slot in reverse order so they restore in correct order (LIFO).
        foreach (SlotChanges slotChanges in accountChanges.StorageChanges)
        {
            // Push changes in reverse order so they restore in original order
            foreach (KeyValuePair<int, StorageChange> change in slotChanges.Changes)
            {
                _changes.Push(new()
                {
                    Address = address,
                    Type = ChangeType.StorageChange,
                    Slot = slotChanges.Slot,
                    PreviousValue = change.Value,
                    BlockAccessIndex = Index
                });
            }
        }

        if (accountChanges.NonceChange is not null)
        {
            _changes.Push(new()
            {
                Address = address,
                Type = ChangeType.NonceChange,
                PreviousValue = accountChanges.NonceChange,
                BlockAccessIndex = Index
            });
        }

        if (accountChanges.CodeChange is not null)
        {
            _changes.Push(new()
            {
                Address = address,
                Type = ChangeType.CodeChange,
                PreviousValue = accountChanges.CodeChange,
                BlockAccessIndex = Index
            });
        }

        accountChanges.SelfDestruct();
        AddBalanceChange(address, oldBalance, 0);
    }

    private void StorageChange(Address address, GeneratingAccountChanges accountChanges, in UInt256 key, in UInt256 before, in UInt256 after)
    {
        SlotChanges slotChanges = accountChanges.GetOrAddSlotChanges(key);

        bool changedDuringTx = HasStorageChangedDuringTx(address, key, before, after);
        slotChanges.TryPopStorageChange(Index, out StorageChange? oldStorageChange);

        _changes.Push(new()
        {
            Address = address,
            BlockAccessIndex = Index,
            Slot = key,
            Type = ChangeType.StorageChange,
            PreviousValue = oldStorageChange,
            PreTxStorage = before
        });

        if (changedDuringTx)
        {
            slotChanges.AddStorageChange(new(Index, after));
            accountChanges.RemoveStorageRead(key);
        }
        else
        {
            accountChanges.ClearEmptySlotChangesAndAddRead(key);
        }
    }

    public int TakeSnapshot()
        => _changes.Count;

    public void Restore(int snapshot, int? blockAccessIndex = null)
    {
        snapshot = int.Max(0, snapshot);
        while (_changes.Count > snapshot)
        {
            Change change = _changes.Pop();
            GeneratingAccountChanges accountChanges = _accountChanges[change.Address];
            switch (change.Type)
            {
                case ChangeType.BalanceChange:
                    BalanceChange? previousBalance = change.PreviousValue is null ? null : (BalanceChange)change.PreviousValue;

                    // balance could have gone back to pre-tx value
                    // so would already be empty
                    accountChanges.PopBalanceChange(out _); // todo: this index must be the same?
                    if (previousBalance is not null)
                    {
                        accountChanges.AddBalanceChange(previousBalance.Value);
                    }
                    break;
                case ChangeType.CodeChange:
                    CodeChange? previousCode = change.PreviousValue is null ? null : (CodeChange)change.PreviousValue;

                    accountChanges.PopCodeChange(out _);
                    if (previousCode is not null)
                    {
                        accountChanges.AddCodeChange(previousCode.Value);
                    }
                    break;
                case ChangeType.NonceChange:
                    NonceChange? previousNonce = change.PreviousValue is null ? null : (NonceChange)change.PreviousValue;

                    accountChanges.PopNonceChange(out _);
                    if (previousNonce is not null)
                    {
                        accountChanges.AddNonceChange(previousNonce.Value);
                    }
                    break;
                case ChangeType.StorageChange:
                    StorageChange? previousStorage = change.PreviousValue is null ? null : (StorageChange)change.PreviousValue;
                    SlotChanges slotChanges = accountChanges.GetOrAddSlotChanges(change.Slot!.Value);

                    slotChanges.TryPopStorageChange(Index, out _);
                    if (previousStorage is not null)
                    {
                        slotChanges.AddStorageChange(previousStorage.Value);
                        accountChanges.RemoveStorageRead(change.Slot.Value);
                    }

                    accountChanges.ClearEmptySlotChangesAndAddRead(change.Slot!.Value);
                    break;
            }
        }
    }

    // public IEnumerable<ChangeAtIndex> GetChangesAtIndex(ushort index)
    // {
    //     foreach (AccountChanges accountChanges in AccountChanges)
    //     {
    //         bool isSystemContract =
    //             accountChanges.Address == Eip7002Constants.WithdrawalRequestPredeployAddress ||
    //             accountChanges.Address == Eip7251Constants.ConsolidationRequestPredeployAddress;

    //         yield return
    //             new(
    //                 accountChanges.Address,
    //                 accountChanges.BalanceChangeAtIndex(index),
    //                 accountChanges.NonceChangeAtIndex(index),
    //                 accountChanges.CodeChangeAtIndex(index),
    //                 accountChanges.SlotChangesAtIndex(index),
    //                 isSystemContract ? 0 : accountChanges.StorageReads.Count
    //             );
    //     }
    // }

    // public override string ToString()
    // {
    //     StringBuilder sb = new();
    //     sb.AppendLine($"BlockAccessList (Index={Index}, Accounts={_accountChanges.Count})");
    //     foreach (AccountChanges ac in _accountChanges.Values)
    //     {
    //         sb.AppendLine($"  {ac}");
    //     }
    //     return sb.ToString();
    // }

    // for testing
    // internal void AddAccountChanges(params AccountChanges[] accountChanges)
    // {
    //     foreach (AccountChanges change in accountChanges)
    //     {
    //         _accountChanges.Add(change.Address, change);
    //     }
    // }

    // internal void RemoveAccountChanges(params Address[] addresses)
    // {
    //     foreach (Address address in addresses)
    //     {
    //         _accountChanges.Remove(address);
    //     }
    // }

    private bool HasBalanceChangedDuringTx(Address address, UInt256 beforeInstr, UInt256 afterInstr)
    {
        GeneratingAccountChanges accountChanges = _accountChanges[address];
        BalanceChange? balanceChange = accountChanges.BalanceChange;

        if (balanceChange is null)
        {
            // first balance change of block
            // return balance prior to this instruction
            return beforeInstr != afterInstr;
        }

        if (balanceChange.Value.BlockAccessIndex != Index)
        {
            // balance changed in previous tx in block
            return balanceChange.Value.PostBalance != afterInstr;
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
        throw new InvalidOperationException("Error calculating pre tx balance");
    }

    private bool HasStorageChangedDuringTx(Address address, UInt256 key, in UInt256 beforeInstr, in UInt256 afterInstr)
    {
        GeneratingAccountChanges accountChanges = _accountChanges[address];

        if (!accountChanges.TryGetSlotChanges(key, out SlotChanges? slotChanges) || slotChanges.Changes.Count == 0)
        {
            // first storage change of block
            // return storage prior to this instruction
            return beforeInstr != afterInstr;
        }

        IList<StorageChange> values = slotChanges.Changes.Values;
        for (int i = values.Count - 1; i >= 0; i--)
        {
            StorageChange storageChange = values[i];
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
        throw new InvalidOperationException("Error calculating pre tx storage");
    }

    private bool HasCodeChangedDuringTx(Address address, in ReadOnlySpan<byte> beforeInstr, in ReadOnlySpan<byte> afterInstr)
    {
        GeneratingAccountChanges accountChanges = _accountChanges[address];
        CodeChange? codeChange = accountChanges.CodeChange;

        if (codeChange is null)
        {
            // first code change of block
            // return code prior to this instruction
            return !beforeInstr.SequenceEqual(afterInstr);
        }

        if (codeChange.Value.BlockAccessIndex != Index)
        {
            // code changed in previous tx in block
            return !codeChange.Value.NewCode.AsSpan().SequenceEqual(afterInstr);
        }

        // code only changed within this transaction
        foreach (Change change in _changes)
        {
            if (change.Type == ChangeType.CodeChange && change.Address == address && change.PreviousValue is null)
            {
                // first change of this transaction & block
                return change.PreTxCode is null || !change.PreTxCode.AsSpan().SequenceEqual(afterInstr);
            }
        }

        // should never happen
        throw new InvalidOperationException("Error calculating pre tx code");
    }

    private GeneratingAccountChanges GetOrAddAccountChanges(Address address)
    {
        if (!_accountChanges.TryGetValue(address, out GeneratingAccountChanges? existing))
        {
            GeneratingAccountChanges accountChanges = new();
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
        public int BlockAccessIndex { get; init; }
    }
}
