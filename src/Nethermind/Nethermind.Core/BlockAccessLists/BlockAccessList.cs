// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

public class BlockAccessList : IEquatable<BlockAccessList>, IJournal<int>
{
    [JsonIgnore]
    public ushort Index = 0;
    public IReadOnlyList<AccountChanges> AccountChanges => _accountChanges;

    private readonly List<AccountChanges> _accountChanges;
    private readonly Dictionary<Address, AccountChanges> _byAddress;
    private readonly Stack<Change> _changes;
    private bool _sealed;

    public BlockAccessList()
    {
        _accountChanges = [];
        _byAddress = [];
        _changes = new();
    }

    public BlockAccessList(List<AccountChanges> accountChanges)
    {
        _accountChanges = accountChanges;
        _byAddress = new(accountChanges.Count);
        foreach (AccountChanges ac in accountChanges)
        {
            _byAddress[ac.Address] = ac;
        }
        _changes = new();
        // Decoder built this from already-sorted wire input; treat as sealed.
        _sealed = true;
    }

    public bool Equals(BlockAccessList? other)
    {
        if (other is null)
            return false;

        if (_accountChanges.Count != other._accountChanges.Count)
            return false;

        foreach (AccountChanges ac in _accountChanges)
        {
            if (!other._byAddress.TryGetValue(ac.Address, out AccountChanges? otherValue))
                return false;

            if (ac != otherValue)
                return false;
        }

        return true;
    }

    public override bool Equals(object? obj) =>
        obj is BlockAccessList other && Equals(other);

    public override int GetHashCode() =>
        _accountChanges.Count.GetHashCode();

    public static bool operator ==(BlockAccessList left, BlockAccessList right) =>
        left.Equals(right);

    public static bool operator !=(BlockAccessList left, BlockAccessList right) =>
        !(left == right);

    public AccountChanges? GetAccountChanges(Address address) => _byAddress.TryGetValue(address, out AccountChanges? value) ? value : null;

    public int ItemCount()
    {
        int count = _accountChanges.Count;
        foreach (AccountChanges accountChanges in _accountChanges)
            count += accountChanges.StorageChanges.Count + accountChanges.StorageReads.Count;
        return count;
    }

    public void IncrementBlockAccessIndex()
    {
        _changes.Clear();
        Index++;
    }

    public void RollbackCurrentIndex()
    {
        Restore(0);
        _changes.Clear();
        Index--;
    }

    public void Clear()
    {
        _accountChanges.Clear();
        _byAddress.Clear();
        _changes.Clear();
        Index = 0;
        _sealed = false;
    }

    public void AddBalanceChange(Address address, UInt256 before, UInt256 after)
    {
        bool isZeroBalanceChange = before == after;
        if (address == Address.SystemUser && isZeroBalanceChange)
        {
            return;
        }

        AccountChanges accountChanges = GetOrAddAccountChanges(address);

        // don't add zero balance transfers, but add empty account changes
        if (isZeroBalanceChange)
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
            accountChanges.AddBalanceChange(new(Index, after));
        }
    }

    public void AddCodeChange(Address address, byte[] before, ReadOnlyMemory<byte> after)
    {
        AccountChanges accountChanges = GetOrAddAccountChanges(address);

        if (before.AsSpan().SequenceEqual(after.Span))
        {
            return;
        }

        bool changedDuringTx = HasCodeChangedDuringTx(accountChanges.Address, before, after.Span);
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
            accountChanges.AddCodeChange(new(Index, after.ToArray()));
        }
    }

    public void AddNonceChange(Address address, ulong newNonce)
    {
        if (newNonce == 0)
        {
            return;
        }

        AccountChanges accountChanges = GetOrAddAccountChanges(address);

        accountChanges.PopNonceChange(Index, out NonceChange? oldNonceChange);
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
        if (!_byAddress.ContainsKey(address))
        {
            AccountChanges ac = new(address);
            _byAddress[address] = ac;
            _accountChanges.Add(ac);
            _sealed = false;
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
        // Push ALL changes per slot in reverse order so they restore in original ascending order (LIFO).
        foreach (SlotChanges slotChanges in accountChanges.StorageChanges)
        {
            IReadOnlyList<StorageChange> changes = slotChanges.Changes;
            for (int i = changes.Count - 1; i >= 0; i--)
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
        IReadOnlyList<NonceChange> nonceChanges = accountChanges.NonceChanges;
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
        IReadOnlyList<CodeChange> codeChanges = accountChanges.CodeChanges;
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
        slotChanges.TryPopStorageChange(Index, out StorageChange? oldStorageChange);

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
            slotChanges.Changes.Add(new(Index, after));
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
            AccountChanges accountChanges = _byAddress[change.Address];
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

                    slotChanges.TryPopStorageChange(Index, out _);
                    if (previousStorage is not null)
                    {
                        slotChanges.Changes.Add(previousStorage.Value);
                        accountChanges.RemoveStorageRead(change.Slot.Value);
                    }

                    accountChanges.ClearEmptySlotChangesAndAddRead(change.Slot!.Value);
                    break;
            }
        }
    }

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine($"BlockAccessList (Index={Index}, Accounts={_accountChanges.Count})");
        foreach (AccountChanges ac in _accountChanges)
        {
            sb.AppendLine($"  {ac}");
        }
        return sb.ToString();
    }

    // for testing
    internal void AddAccountChanges(params AccountChanges[] accountChanges)
    {
        foreach (AccountChanges change in accountChanges)
        {
            _byAddress[change.Address] = change;
            _accountChanges.Add(change);
        }
        if (accountChanges.Length > 0) _sealed = false;
    }

    private bool HasBalanceChangedDuringTx(Address address, UInt256 beforeInstr, UInt256 afterInstr)
    {
        AccountChanges accountChanges = _byAddress[address];
        IReadOnlyList<BalanceChange> balanceChanges = accountChanges.BalanceChanges;
        int count = balanceChanges.Count;

        if (count == 0)
        {
            // first balance change of block
            // return balance prior to this instruction
            return beforeInstr != afterInstr;
        }

        for (int i = count - 1; i >= 0; i--)
        {
            BalanceChange balanceChange = balanceChanges[i];
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
        throw new InvalidOperationException("Error calculating pre tx balance");
    }

    private bool HasStorageChangedDuringTx(Address address, UInt256 key, in UInt256 beforeInstr, in UInt256 afterInstr)
    {
        AccountChanges accountChanges = _byAddress[address];

        if (!accountChanges.TryGetSlotChanges(key, out SlotChanges? slotChanges) || slotChanges.Changes.Count == 0)
        {
            // first storage change of block
            // return storage prior to this instruction
            return beforeInstr != afterInstr;
        }

        IReadOnlyList<StorageChange> values = slotChanges.Changes;
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
        AccountChanges accountChanges = _byAddress[address];
        IReadOnlyList<CodeChange> codeChanges = accountChanges.CodeChanges;
        int count = codeChanges.Count;

        if (count == 0)
        {
            // first code change of block
            // return code prior to this instruction
            return !beforeInstr.SequenceEqual(afterInstr);
        }

        for (int i = count - 1; i >= 0; i--)
        {
            CodeChange codeChange = codeChanges[i];
            if (codeChange.BlockAccessIndex != Index)
            {
                // code changed in previous tx in block
                return !codeChange.NewCode.AsSpan().SequenceEqual(afterInstr);
            }
        }

        // storage only changed within this transaction
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

    private AccountChanges GetOrAddAccountChanges(Address address)
    {
        if (!_byAddress.TryGetValue(address, out AccountChanges? existing))
        {
            AccountChanges accountChanges = new(address);
            _byAddress[address] = accountChanges;
            _accountChanges.Add(accountChanges);
            _sealed = false;
            return accountChanges;
        }
        return existing;
    }

    // Sort the unsorted build-time collections into the canonical order required
    // by RLP encoding and BAL validation. Idempotent via _sealed; mutations that
    // can grow _accountChanges clear the flag. Returns true when this call did
    // work (was unsealed), false when it was already sealed.
    //
    // Invariant: outer sealed ⇒ every child sealed. All BAL-level entry points
    // that mutate a child also unseal the outer, so this holds automatically.
    // Callers that reach into a child AccountChanges directly (test helpers,
    // decoder composition) must seal the child themselves.
    public bool Seal()
    {
        if (_sealed) return false;
        _accountChanges.Sort(static (a, b) => a.Address.CompareTo(b.Address));
        List<AccountChanges> accountChanges = _accountChanges;
        Parallel.For(0, accountChanges.Count, i => accountChanges[i].Seal());
        _sealed = true;
        return true;
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

public record struct ChangeAtIndex(Address Address, BalanceChange? BalanceChange, NonceChange? NonceChange, CodeChange? CodeChange, IEnumerable<SlotChanges> SlotChanges, int Reads);
