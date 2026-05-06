// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using Nethermind.Core.Collections;
using Nethermind.Core.Resettables;
using Nethermind.Int256;

[assembly: InternalsVisibleTo("Nethermind.Core.Test")]
[assembly: InternalsVisibleTo("Nethermind.State.Test")]

namespace Nethermind.Core.BlockAccessLists;

public class BlockAccessList : IEquatable<BlockAccessList>, IJournal<int>, IResettable
{
    [JsonIgnore]
    public uint Index = 0;

    /// storage keys across all accounts + addresses
    [JsonIgnore]
    public long ItemCount
    {
        get => _itemCount ??= CountItems();
        init => _itemCount = value;
    }

    public EnumerableWithCount<AccountChanges> AccountChanges => new(GetSortedAccountChanges(), _accountChanges.Count);

    [JsonIgnore]
    public ReadOnlySpan<AccountChanges> AccountChangesByAddress => GetSortedAccountChanges();

    [JsonIgnore]
    public Dictionary<Address, AccountChanges>.ValueCollection UnorderedAccountChanges => _accountChanges.Values;

    public bool HasAccount(Address address) => _accountChanges.ContainsKey(address);

    private readonly Dictionary<Address, AccountChanges> _accountChanges;
    private readonly List<ChangeType> _changeStream = new();
    private readonly List<BalanceChangeDetails> _balanceDetails = new();
    private readonly List<NonceChangeDetails> _nonceDetails = new();
    private readonly List<CodeChangeDetails> _codeDetails = new();
    private readonly List<StorageChangeDetails> _storageDetails = new();
    private AccountChanges[]? _sortedAccountChanges;
    private long? _itemCount = null;

    /// <summary>
    /// Sentinel meaning "no in-block predecessor at this address/slot". Distinct from
    /// <see cref="Eip7928Constants.PrestateIndex"/>, which is a real prior index after prestate load.
    /// </summary>
    private const long NoPreviousBlockAccessIndex = -1;

    public BlockAccessList() : this(0)
    {
    }

    private BlockAccessList(int capacity)
        => _accountChanges = new(capacity, GenericEqualityComparer.GetOptimized<Address>());

    public BlockAccessList(SortedDictionary<Address, AccountChanges> accountChanges) : this(accountChanges.Count)
    {
        foreach (KeyValuePair<Address, AccountChanges> pair in accountChanges)
        {
            _accountChanges.Add(pair.Key, pair.Value);
        }
    }

    public static BlockAccessList FromSortedAccountChanges(AccountChanges[] sortedAccountChanges, long itemCount)
    {
        BlockAccessList blockAccessList = new(sortedAccountChanges.Length)
        {
            _sortedAccountChanges = sortedAccountChanges,
            _itemCount = itemCount
        };

        foreach (AccountChanges accountChanges in sortedAccountChanges)
        {
            blockAccessList._accountChanges.Add(accountChanges.Address, accountChanges);
        }

        return blockAccessList;
    }

    public bool Equals(BlockAccessList? other)
    {
        if (other is null)
            return false;

        if (_accountChanges.Count != other._accountChanges.Count)
            return false;

        foreach (KeyValuePair<Address, AccountChanges> pair in _accountChanges)
        {
            if (!other._accountChanges.TryGetValue(pair.Key, out AccountChanges? otherValue))
                return false;

            if (pair.Value != otherValue)
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

    // todo: optimize to use hashmaps where appropriate, separate data structures for tracing and state reading
    public void Merge(BlockAccessList other)
    {
        _itemCount = null;
        _accountChanges.EnsureCapacity(_accountChanges.Count + other._accountChanges.Count);
        bool addedAccount = false;
        foreach (AccountChanges otherAccountChange in other.UnorderedAccountChanges)
        {
            if (_accountChanges.TryGetValue(otherAccountChange.Address, out AccountChanges? accountChange))
            {
                accountChange.Merge(otherAccountChange);
            }
            else
            {
                _accountChanges.Add(otherAccountChange.Address, otherAccountChange);
                addedAccount = true;
            }
        }

        if (addedAccount)
        {
            _sortedAccountChanges = null;
        }
    }

    public AccountChanges? GetAccountChanges(Address address) => _accountChanges.TryGetValue(address, out AccountChanges? value) ? value : null;

    public void Clear()
    {
        _itemCount = null;
        _accountChanges.Clear();
        _sortedAccountChanges = null;
        _changeStream.Clear();
        _balanceDetails.Clear();
        _nonceDetails.Clear();
        _codeDetails.Clear();
        _storageDetails.Clear();
    }

    public void Reset()
    {
        Clear();
        Index = 0;
    }

    public void AddBalanceChange(Address address, UInt256 before, UInt256 after)
    {
        _itemCount = null;
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

        bool changedDuringTx = HasBalanceChangedDuringTx(accountChanges, address, before, after);
        bool hasPrev = accountChanges.TryPopBalanceChange(Index, out BalanceChange oldBalanceChange);

        PushBalanceChangeDetails(address, hasPrev, in oldBalanceChange, in before);

        if (changedDuringTx)
        {
            accountChanges.AddBalanceChange(new(Index, after));
        }
    }

    public void AddCodeChange(Address address, byte[] before, ReadOnlyMemory<byte> after)
    {
        _itemCount = null;
        AccountChanges accountChanges = GetOrAddAccountChanges(address);

        if (before.AsSpan().SequenceEqual(after.Span))
        {
            return;
        }

        bool changedDuringTx = HasCodeChangedDuringTx(accountChanges, before, after.Span);
        bool hasPrev = accountChanges.TryPopCodeChange(Index, out CodeChange oldCodeChange);
        PushCodeChangeDetails(address, hasPrev, in oldCodeChange, before);

        if (changedDuringTx)
        {
            accountChanges.AddCodeChange(new(Index, after.ToArray()));
        }
    }

    public void AddNonceChange(Address address, ulong newNonce)
    {
        _itemCount = null;
        if (newNonce == 0)
        {
            return;
        }

        AccountChanges accountChanges = GetOrAddAccountChanges(address);

        bool hasPrev = accountChanges.TryPopNonceChange(Index, out NonceChange oldNonceChange);
        PushNonceChangeDetails(address, hasPrev, in oldNonceChange);

        accountChanges.AddNonceChange(new(Index, newNonce));
    }

    public void AddAccountRead(Address address)
    {
        if (!_accountChanges.ContainsKey(address))
        {
            _itemCount = null;
            _accountChanges.Add(address, new(address));
            _sortedAccountChanges = null;
        }
    }

    public void AddStorageChange(Address address, UInt256 key, UInt256 before, UInt256 after)
    {
        _itemCount = null;
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
        _itemCount = null;
        AccountChanges accountChanges = GetOrAddAccountChanges(address);

        if (!accountChanges.HasStorageChange(key))
        {
            accountChanges.AddStorageRead(key);
        }
    }

    public void DeleteAccount(Address address, UInt256 oldBalance)
    {
        _itemCount = null;
        AccountChanges accountChanges = GetOrAddAccountChanges(address);

        // todo: this will be optimized when bal structure changes, no need to iterate list

        // Push revertible changes for each storage change that will be cleared.
        // Push ALL changes per slot in reverse order so they restore in correct order (LIFO).
        foreach (SlotChanges slotChanges in accountChanges.StorageChanges)
        {
            // Push changes in reverse order so they restore in original order
            foreach (KeyValuePair<uint, StorageChange> change in slotChanges.Changes)
            {
                PushStorageChangeDetails(address, slotChanges.Key, hasPrevious: true, change.Value, default);
            }
        }

        // Push revertible changes for nonce changes (reverse order for correct restore)
        IList<NonceChange> nonceChanges = accountChanges.NonceChanges;
        int nonceCount = nonceChanges.Count;
        for (int i = nonceCount - 1; i >= 0; i--)
        {
            NonceChange prev = nonceChanges[i];
            PushNonceChangeDetails(address, hasPrevious: true, in prev);
        }

        // Push revertible changes for code changes (reverse order for correct restore)
        IList<CodeChange> codeChanges = accountChanges.CodeChanges;
        int codeCount = codeChanges.Count;
        for (int i = codeCount - 1; i >= 0; i--)
        {
            CodeChange prev = codeChanges[i];
            PushCodeChangeDetails(address, hasPrevious: true, in prev, priorCode: null);
        }

        accountChanges.SelfDestruct();
        AddBalanceChange(address, oldBalance, 0);
    }

    private void StorageChange(AccountChanges accountChanges, in UInt256 key, in UInt256 before, in UInt256 after)
    {
        SlotChanges slotChanges = accountChanges.GetOrAddSlotChanges(key);

        bool changedDuringTx = HasStorageChangedDuringTx(accountChanges, slotChanges, key, before, after);
        bool hasPrev = slotChanges.TryPopStorageChangeDirect(Index, out StorageChange oldStorageChange);

        PushStorageChangeDetails(accountChanges.Address, key, hasPrev, in oldStorageChange, in before);

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
        => _changeStream.Count;

    // todo: this will be simplified when BlockAccessList structure is refactored
    public void Restore(int snapshot)
    {
        snapshot = int.Max(0, snapshot);
        if (_changeStream.Count <= snapshot)
            return;

        _itemCount = null;

        Span<ChangeType> changeStream = CollectionsMarshal.AsSpan(_changeStream);
        Span<BalanceChangeDetails> balanceDetails = CollectionsMarshal.AsSpan(_balanceDetails);
        Span<NonceChangeDetails> nonceDetails = CollectionsMarshal.AsSpan(_nonceDetails);
        Span<CodeChangeDetails> codeDetails = CollectionsMarshal.AsSpan(_codeDetails);
        Span<StorageChangeDetails> storageDetails = CollectionsMarshal.AsSpan(_storageDetails);
        int balanceDetailsCount = balanceDetails.Length;
        int nonceDetailsCount = nonceDetails.Length;
        int codeDetailsCount = codeDetails.Length;
        int storageDetailsCount = storageDetails.Length;

        for (int i = changeStream.Length - 1; i >= snapshot; i--)
        {
            switch (changeStream[i])
            {
                case ChangeType.BalanceChange:
                    ApplyBalanceUndo(in balanceDetails[--balanceDetailsCount]);
                    break;
                case ChangeType.CodeChange:
                    ApplyCodeUndo(in codeDetails[--codeDetailsCount]);
                    break;
                case ChangeType.NonceChange:
                    ApplyNonceUndo(in nonceDetails[--nonceDetailsCount]);
                    break;
                case ChangeType.StorageChange:
                    ApplyStorageUndo(in storageDetails[--storageDetailsCount]);
                    break;
            }
        }

        Debug.Assert(
            (balanceDetails.Length - balanceDetailsCount)
          + (nonceDetails.Length - nonceDetailsCount)
          + (codeDetails.Length - codeDetailsCount)
          + (storageDetails.Length - storageDetailsCount)
          == changeStream.Length - snapshot,
          "_changeStream and typed details lists drifted during Restore");

        ClearTail(_balanceDetails, balanceDetailsCount);
        ClearTail(_nonceDetails, nonceDetailsCount);
        ClearTail(_codeDetails, codeDetailsCount);
        ClearTail(_storageDetails, storageDetailsCount);
        CollectionsMarshal.SetCount(_changeStream, snapshot);
        CollectionsMarshal.SetCount(_balanceDetails, balanceDetailsCount);
        CollectionsMarshal.SetCount(_nonceDetails, nonceDetailsCount);
        CollectionsMarshal.SetCount(_codeDetails, codeDetailsCount);
        CollectionsMarshal.SetCount(_storageDetails, storageDetailsCount);
    }

    // todo: optimize early validation
    public IEnumerable<ChangeAtIndex> GetChangesAtIndex(uint index)
    {
        foreach (AccountChanges accountChanges in GetSortedAccountChanges())
        {
            yield return CreateChangeAtIndex(accountChanges, index);
        }
    }

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine($"BlockAccessList (Index={Index}, Accounts={_accountChanges.Count})");
        foreach (AccountChanges ac in GetSortedAccountChanges())
        {
            sb.AppendLine($"  {ac}");
        }
        return sb.ToString();
    }

    // for testing
    internal void AddAccountChanges(params AccountChanges[] accountChanges)
    {
        _itemCount = null;
        foreach (AccountChanges change in accountChanges)
        {
            _accountChanges.Add(change.Address, change);
        }
        _sortedAccountChanges = null;
    }

    internal void RemoveAccountChanges(params Address[] addresses)
    {
        _itemCount = null;
        bool removedAccount = false;
        foreach (Address address in addresses)
        {
            removedAccount |= _accountChanges.Remove(address);
        }

        if (removedAccount)
        {
            _sortedAccountChanges = null;
        }
    }

    private bool HasBalanceChangedDuringTx(AccountChanges accountChanges, Address address, UInt256 beforeInstr, UInt256 afterInstr)
    {
        IList<BalanceChange> balanceChanges = accountChanges.BalanceChanges;
        int count = balanceChanges.Count;

        // todo: these methods will also change with new generatingBAL structure
        if (count == 0)
        {
            // first balance change of block
            // return balance prior to this instruction
            return beforeInstr != afterInstr;
        }

        uint currentIndex = Index;
        for (int i = count - 1; i >= 0; i--)
        {
            BalanceChange balanceChange = balanceChanges[i];
            if (balanceChange.Index != currentIndex)
            {
                // balance changed in previous tx in block
                return balanceChange.Value != afterInstr;
            }
        }

        // balance only changed within this transaction
        Span<BalanceChangeDetails> balanceDetails = CollectionsMarshal.AsSpan(_balanceDetails);
        for (int i = balanceDetails.Length - 1; i >= 0; i--)
        {
            ref readonly BalanceChangeDetails change = ref balanceDetails[i];
            if (change.Address == address && change.PreviousBlockAccessIndex == NoPreviousBlockAccessIndex)
            {
                // first change of this transaction & block
                return change.PriorValue != afterInstr;
            }
        }

        // should never happen
        throw new InvalidOperationException("Error calculating pre tx balance");
    }

    private bool HasStorageChangedDuringTx(AccountChanges accountChanges, SlotChanges slotChanges, UInt256 key, in UInt256 beforeInstr, in UInt256 afterInstr)
    {
        IList<StorageChange> values = slotChanges.Changes.Values;
        int count = values.Count;
        if (count == 0)
        {
            // first storage change of block
            // return storage prior to this instruction
            return beforeInstr != afterInstr;
        }

        uint currentIndex = Index;
        for (int i = count - 1; i >= 0; i--)
        {
            StorageChange storageChange = values[i];
            if (storageChange.Index != currentIndex)
            {
                // storage changed in previous tx in block
                return storageChange.Value != afterInstr;
            }
        }

        // storage only changed within this transaction
        Address address = accountChanges.Address;
        Span<StorageChangeDetails> storageDetails = CollectionsMarshal.AsSpan(_storageDetails);
        for (int i = storageDetails.Length - 1; i >= 0; i--)
        {
            ref readonly StorageChangeDetails change = ref storageDetails[i];
            if (
                change.Address == address &&
                change.Slot == key &&
                change.PreviousBlockAccessIndex == NoPreviousBlockAccessIndex)
            {
                // first change of this transaction & block
                return change.PriorValue != afterInstr;
            }
        }

        // should never happen
        throw new InvalidOperationException("Error calculating pre tx storage");
    }

    private bool HasCodeChangedDuringTx(AccountChanges accountChanges, in ReadOnlySpan<byte> beforeInstr, in ReadOnlySpan<byte> afterInstr)
    {
        IList<CodeChange> codeChanges = accountChanges.CodeChanges;
        int count = codeChanges.Count;

        if (count == 0)
        {
            // first code change of block
            // return code prior to this instruction
            return !beforeInstr.SequenceEqual(afterInstr);
        }

        uint currentIndex = Index;
        for (int i = count - 1; i >= 0; i--)
        {
            CodeChange codeChange = codeChanges[i];
            if (codeChange.Index != currentIndex)
            {
                // code changed in previous tx in block
                return !codeChange.Code.AsSpan().SequenceEqual(afterInstr);
            }
        }

        // code only changed within this transaction
        Address address = accountChanges.Address;
        Span<CodeChangeDetails> codeDetails = CollectionsMarshal.AsSpan(_codeDetails);
        for (int i = codeDetails.Length - 1; i >= 0; i--)
        {
            ref readonly CodeChangeDetails change = ref codeDetails[i];
            if (change.Address == address && change.PreviousBlockAccessIndex == NoPreviousBlockAccessIndex)
            {
                // first change of this transaction & block
                return change.PriorCode is null || !change.PriorCode.AsSpan().SequenceEqual(afterInstr);
            }
        }

        // should never happen
        throw new InvalidOperationException("Error calculating pre tx code");
    }

    private AccountChanges GetOrAddAccountChanges(Address address)
    {
        if (!_accountChanges.TryGetValue(address, out AccountChanges? existing))
        {
            AccountChanges accountChanges = new(address);
            _accountChanges.Add(address, accountChanges);
            _sortedAccountChanges = null;
            return accountChanges;
        }
        return existing;
    }

    private AccountChanges[] GetSortedAccountChanges()
    {
        AccountChanges[]? sortedAccountChanges = _sortedAccountChanges;
        if (sortedAccountChanges is not null)
        {
            return sortedAccountChanges;
        }

        if (_accountChanges.Count == 0)
        {
            _sortedAccountChanges = [];
            return _sortedAccountChanges;
        }

        sortedAccountChanges = new AccountChanges[_accountChanges.Count];
        _accountChanges.Values.CopyTo(sortedAccountChanges, 0);
        Array.Sort(sortedAccountChanges, static (left, right) => left.Address.CompareTo(right.Address));
        _sortedAccountChanges = sortedAccountChanges;
        return sortedAccountChanges;
    }

    public static ChangeAtIndex CreateChangeAtIndex(AccountChanges accountChanges, uint index)
    {
        bool isSystemContract =
            accountChanges.Address == Eip7002Constants.WithdrawalRequestPredeployAddress ||
            accountChanges.Address == Eip7251Constants.ConsolidationRequestPredeployAddress;

        return new(
            accountChanges.Address,
            accountChanges.BalanceChangeAtIndex(index),
            accountChanges.NonceChangeAtIndex(index),
            accountChanges.CodeChangeAtIndex(index),
            accountChanges,
            index,
            accountChanges.HasSlotChangesAtIndex(index),
            isSystemContract ? 0 : accountChanges.StorageReads.Count);
    }

    private long CountItems()
    {
        long count = _accountChanges.Count;
        foreach (AccountChanges accountChanges in _accountChanges.Values)
        {
            count += accountChanges.StorageChanges.Count + accountChanges.StorageReads.Count;
        }
        return count;
    }

    private void PushBalanceChangeDetails(Address address, bool hasPrevious, in BalanceChange previousBalance, in UInt256 priorValue)
    {
        long previousIndex;
        UInt256 value;
        if (hasPrevious)
        {
            previousIndex = previousBalance.Index;
            value = previousBalance.Value;
        }
        else
        {
            previousIndex = NoPreviousBlockAccessIndex;
            value = priorValue;
        }
        Debug.Assert(IsRestorablePreviousIndex(previousIndex));
        _balanceDetails.Add(new(address, Index, previousIndex, value));
        _changeStream.Add(ChangeType.BalanceChange);
    }

    private void PushNonceChangeDetails(Address address, bool hasPrevious, in NonceChange previousNonce)
    {
        long previousIndex;
        ulong value;
        if (hasPrevious)
        {
            previousIndex = previousNonce.Index;
            value = previousNonce.Value;
        }
        else
        {
            previousIndex = NoPreviousBlockAccessIndex;
            value = 0;
        }
        Debug.Assert(IsRestorablePreviousIndex(previousIndex));
        _nonceDetails.Add(new(address, Index, previousIndex, value));
        _changeStream.Add(ChangeType.NonceChange);
    }

    private void PushCodeChangeDetails(Address address, bool hasPrevious, in CodeChange previousCode, byte[]? priorCode)
    {
        long previousIndex;
        byte[]? code;
        if (hasPrevious)
        {
            previousIndex = previousCode.Index;
            code = previousCode.Code;
        }
        else
        {
            previousIndex = NoPreviousBlockAccessIndex;
            code = priorCode;
        }
        Debug.Assert(IsRestorablePreviousIndex(previousIndex));
        _codeDetails.Add(new(address, Index, previousIndex, code));
        _changeStream.Add(ChangeType.CodeChange);
    }

    private void PushStorageChangeDetails(Address address, UInt256 slot, bool hasPrevious, in StorageChange previousStorage, in UInt256 priorValue)
    {
        long previousIndex;
        UInt256 value;
        if (hasPrevious)
        {
            previousIndex = previousStorage.Index;
            value = previousStorage.Value;
        }
        else
        {
            previousIndex = NoPreviousBlockAccessIndex;
            value = priorValue;
        }
        Debug.Assert(IsRestorablePreviousIndex(previousIndex));
        _storageDetails.Add(new(address, slot, Index, previousIndex, value));
        _changeStream.Add(ChangeType.StorageChange);
    }

    private bool IsRestorablePreviousIndex(long previousIndex) =>
        previousIndex == NoPreviousBlockAccessIndex ||
        previousIndex == Eip7928Constants.PrestateIndex ||
        previousIndex <= Index;

    private void ApplyBalanceUndo(in BalanceChangeDetails change)
    {
        AccountChanges accountChanges = _accountChanges[change.Address];

        // balance could have gone back to pre-tx value so would already be empty
        accountChanges.TryPopBalanceChange(change.BlockAccessIndex, out _);
        if (change.PreviousBlockAccessIndex != NoPreviousBlockAccessIndex)
        {
            accountChanges.AddBalanceChange(new((uint)change.PreviousBlockAccessIndex, change.PriorValue));
        }
    }

    private void ApplyCodeUndo(in CodeChangeDetails change)
    {
        AccountChanges accountChanges = _accountChanges[change.Address];
        accountChanges.TryPopCodeChange(change.BlockAccessIndex, out _);
        if (change.PreviousBlockAccessIndex != NoPreviousBlockAccessIndex)
        {
            accountChanges.AddCodeChange(new((uint)change.PreviousBlockAccessIndex, change.PriorCode ?? []));
        }
    }

    private void ApplyNonceUndo(in NonceChangeDetails change)
    {
        AccountChanges accountChanges = _accountChanges[change.Address];
        accountChanges.TryPopNonceChange(change.BlockAccessIndex, out _);
        if (change.PreviousBlockAccessIndex != NoPreviousBlockAccessIndex)
        {
            accountChanges.AddNonceChange(new((uint)change.PreviousBlockAccessIndex, change.PriorValue));
        }
    }

    private void ApplyStorageUndo(in StorageChangeDetails change)
    {
        AccountChanges accountChanges = _accountChanges[change.Address];
        SlotChanges slotChanges = accountChanges.GetOrAddSlotChanges(change.Slot);

        slotChanges.TryPopStorageChangeDirect(change.BlockAccessIndex, out _);
        if (change.PreviousBlockAccessIndex != NoPreviousBlockAccessIndex)
        {
            slotChanges.AddStorageChange(new((uint)change.PreviousBlockAccessIndex, change.PriorValue));
            accountChanges.RemoveStorageRead(change.Slot);
        }

        accountChanges.ClearEmptySlotChangesAndAddRead(change.Slot);
    }

    private static void ClearTail<T>(List<T> list, int count) =>
        CollectionsMarshal.AsSpan(list)[count..].Clear();

    private enum ChangeType : byte
    {
        BalanceChange = 0,
        CodeChange = 1,
        NonceChange = 2,
        StorageChange = 3
    }

    private readonly struct BalanceChangeDetails(Address address, uint blockAccessIndex, long previousBlockAccessIndex, UInt256 priorValue)
    {
        public readonly Address Address = address;
        public readonly uint BlockAccessIndex = blockAccessIndex;
        public readonly long PreviousBlockAccessIndex = previousBlockAccessIndex;
        public readonly UInt256 PriorValue = priorValue;
    }

    private readonly struct NonceChangeDetails(Address address, uint blockAccessIndex, long previousBlockAccessIndex, ulong priorValue)
    {
        public readonly Address Address = address;
        public readonly uint BlockAccessIndex = blockAccessIndex;
        public readonly long PreviousBlockAccessIndex = previousBlockAccessIndex;
        public readonly ulong PriorValue = priorValue;
    }

    private readonly struct CodeChangeDetails(Address address, uint blockAccessIndex, long previousBlockAccessIndex, byte[]? priorCode)
    {
        public readonly Address Address = address;
        public readonly uint BlockAccessIndex = blockAccessIndex;
        public readonly long PreviousBlockAccessIndex = previousBlockAccessIndex;
        public readonly byte[]? PriorCode = priorCode;
    }

    private readonly struct StorageChangeDetails(Address address, UInt256 slot, uint blockAccessIndex, long previousBlockAccessIndex, UInt256 priorValue)
    {
        public readonly Address Address = address;
        public readonly UInt256 Slot = slot;
        public readonly uint BlockAccessIndex = blockAccessIndex;
        public readonly long PreviousBlockAccessIndex = previousBlockAccessIndex;
        public readonly UInt256 PriorValue = priorValue;
    }
}

public record struct ChangeAtIndex(Address Address, BalanceChange? BalanceChange, NonceChange? NonceChange, CodeChange? CodeChange, AccountChanges AccountChanges, uint Index, bool HasSlotChanges, int Reads)
{
    public override string ToString()
    {
        int slotChangeCount = 0;
        foreach (SlotChanges slotChanges in AccountChanges.StorageChanges)
        {
            if (slotChanges.Changes.ContainsKey(Index))
            {
                slotChangeCount++;
            }
        }

        return $"{nameof(ChangeAtIndex)}({Address}, Balance={BalanceChange?.Value}, Nonce={NonceChange?.Value}, Code={CodeChange is not null}, Slots={slotChangeCount}, Reads={Reads})";
    }
}
