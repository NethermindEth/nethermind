// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// A block access list merged from the sealed <see cref="BlockAccessListLog"/> runs of a block, held in EIP-7928
/// encoding order.
/// </summary>
/// <remarks>
/// The entries sit in flat lists that each account addresses by range, so the list can be compared, counted and
/// encoded without per-account objects, and a reused instance stops allocating once warm.
/// </remarks>
public sealed class MergedBlockAccessList
{
    private readonly List<AccountRange> _accounts = [];
    private readonly List<SlotRange> _storageChanges = [];
    private readonly List<StorageChange> _storageChangeEntries = [];
    private readonly List<UInt256> _storageReads = [];
    private readonly List<BalanceChange> _balanceChanges = [];
    private readonly List<NonceChange> _nonceChanges = [];
    private readonly List<CodeChange> _codeChanges = [];

    public int AccountCount => _accounts.Count;

    /// <summary>The EIP-7928 item count: accounts plus distinct storage keys, changed or read.</summary>
    public int ItemCount => _accounts.Count + _storageChanges.Count + _storageReads.Count;

    public AccountChanges GetAccount(int index) => new(this, _accounts[index]);

    /// <summary>Replaces the list with the merge of <paramref name="logs"/>.</summary>
    /// <param name="logs">Sealed logs, together holding each block access index at most once.</param>
    /// <remarks>
    /// The runs merge in encoding order with the index last, so each account's and slot's changes come out ordered by
    /// index. A slot changed at any index is listed only as a change.
    /// </remarks>
    public void Merge(ReadOnlySpan<BlockAccessListLog> logs)
    {
        Clear();

        int[] scratch = ArrayPool<int>.Shared.Rent(logs.Length * 2);
        Span<int> positions = scratch.AsSpan(0, logs.Length);
        positions.Clear();
        LoserTree<Runs> tree = new(new Runs(logs, positions), scratch.AsSpan(logs.Length, logs.Length));

        Address? address = null;
        AccountRange account = default;
        bool hasSlot = false;
        UInt256 slot = default;
        int slotChangesStart = 0;
        while (tree.TryPeek(out int run))
        {
            BlockAccessListLog log = logs[run];
            ref readonly BlockAccessListLog.Item item = ref log.Items[positions[run]];

            if (!item.Address.Equals(address))
            {
                if (address is not null) FinishAccount(ref account, ref hasSlot, in slot, slotChangesStart);
                address = item.Address;
                // Every run leads an account with its account item, so the first one popped has the lowest index.
                account = StartAccount(address, item.Index);
            }

            switch (item.Section)
            {
                case BlockAccessListLog.Section.Storage:
                    if (!hasSlot || item.Slot != slot)
                    {
                        FinishSlot(ref hasSlot, in slot, slotChangesStart);
                        hasSlot = true;
                        slot = item.Slot;
                        slotChangesStart = _storageChangeEntries.Count;
                    }
                    if (item.IsChange) _storageChangeEntries.Add(new StorageChange(item.Index, item.Value));
                    break;
                case BlockAccessListLog.Section.Balance:
                    _balanceChanges.Add(new BalanceChange(item.Index, item.Value));
                    break;
                case BlockAccessListLog.Section.Nonce:
                    _nonceChanges.Add(new NonceChange(item.Index, item.Value.u0));
                    break;
                case BlockAccessListLog.Section.Code:
                    _codeChanges.Add(log.GetCodeChange(in item));
                    break;
            }

            tree.Pop();
        }

        if (address is not null) FinishAccount(ref account, ref hasSlot, in slot, slotChangesStart);
        ArrayPool<int>.Shared.Return(scratch);
    }

    /// <summary>Whether this list equals <paramref name="other"/>, which the decoder only builds from canonical encodings.</summary>
    /// <param name="other">The list to compare with.</param>
    /// <param name="mismatchedAddress">The account at the first differing position, or null when equal or when this list ends first.</param>
    /// <remarks>Equal lists encode to the same bytes, so this stands in for encoding and hashing.</remarks>
    public bool Matches(ReadOnlyBlockAccessList other, out Address? mismatchedAddress)
    {
        ReadOnlySpan<ReadOnlyAccountChanges> otherAccounts = other.AccountChanges.AsSpan();
        for (int i = 0; i < _accounts.Count; i++)
        {
            AccountChanges account = GetAccount(i);
            if (i >= otherAccounts.Length || !account.Matches(otherAccounts[i]))
            {
                mismatchedAddress = account.Address;
                return false;
            }
        }

        mismatchedAddress = null;
        return _accounts.Count == otherAccounts.Length;
    }

    /// <summary>Copies the list into a <see cref="GeneratedBlockAccessList"/>.</summary>
    public GeneratedBlockAccessList ToGeneratedBlockAccessList()
    {
        GeneratedAccountChanges[] accounts = new GeneratedAccountChanges[_accounts.Count];
        for (int i = 0; i < accounts.Length; i++)
        {
            AccountChanges source = GetAccount(i);
            GeneratedAccountChanges target = new(source.Address);
            for (int slot = 0; slot < source.SlotCount; slot++)
            {
                target.GetOrAddSlotChanges(source.GetSlotKey(slot)).Changes.AddRange(source.GetSlotChanges(slot));
            }
            foreach (UInt256 read in source.StorageReads) target.AddStorageRead(read);
            target.BalanceChanges.AddRange(source.BalanceChanges);
            target.NonceChanges.AddRange(source.NonceChanges);
            target.CodeChanges.AddRange(source.CodeChanges);
            accounts[i] = target;
        }
        return GeneratedBlockAccessList.FromAccounts(accounts);
    }

    public void Clear()
    {
        _accounts.Clear();
        _storageChanges.Clear();
        _storageChangeEntries.Clear();
        _storageReads.Clear();
        _balanceChanges.Clear();
        _nonceChanges.Clear();
        _codeChanges.Clear();
    }

    private AccountRange StartAccount(Address address, uint firstIndex) => new()
    {
        Address = address,
        FirstIndex = firstIndex,
        SlotsStart = _storageChanges.Count,
        StorageReadsStart = _storageReads.Count,
        BalanceChangesStart = _balanceChanges.Count,
        NonceChangesStart = _nonceChanges.Count,
        CodeChangesStart = _codeChanges.Count,
    };

    private void FinishAccount(ref AccountRange account, ref bool hasSlot, in UInt256 slot, int slotChangesStart)
    {
        FinishSlot(ref hasSlot, in slot, slotChangesStart);
        account.SlotsEnd = _storageChanges.Count;
        account.StorageReadsEnd = _storageReads.Count;
        account.BalanceChangesEnd = _balanceChanges.Count;
        account.NonceChangesEnd = _nonceChanges.Count;
        account.CodeChangesEnd = _codeChanges.Count;
        _accounts.Add(account);
    }

    private void FinishSlot(ref bool hasSlot, in UInt256 slot, int slotChangesStart)
    {
        if (!hasSlot) return;
        hasSlot = false;

        int changes = _storageChangeEntries.Count - slotChangesStart;
        if (changes == 0) _storageReads.Add(slot);
        else _storageChanges.Add(new SlotRange(slot, slotChangesStart, changes));
    }

    /// <summary>One account of a <see cref="MergedBlockAccessList"/>, valid until the list changes.</summary>
    public readonly ref struct AccountChanges
    {
        private readonly MergedBlockAccessList _list;
        private readonly AccountRange _range;

        internal AccountChanges(MergedBlockAccessList list, AccountRange range)
        {
            _list = list;
            _range = range;
        }

        public Address Address => _range.Address;

        /// <summary>The lowest block access index that accessed the account.</summary>
        public uint FirstIndex => _range.FirstIndex;

        public int SlotCount => _range.SlotsEnd - _range.SlotsStart;

        public UInt256 GetSlotKey(int slot) => _list._storageChanges[_range.SlotsStart + slot].Key;

        /// <summary>The changes of a changed slot, ordered by index.</summary>
        public ReadOnlySpan<StorageChange> GetSlotChanges(int slot)
        {
            SlotRange range = _list._storageChanges[_range.SlotsStart + slot];
            return CollectionsMarshal.AsSpan(_list._storageChangeEntries).Slice(range.ChangesStart, range.ChangesCount);
        }

        public ReadOnlySpan<UInt256> StorageReads => Slice(_list._storageReads, _range.StorageReadsStart, _range.StorageReadsEnd);
        public ReadOnlySpan<BalanceChange> BalanceChanges => Slice(_list._balanceChanges, _range.BalanceChangesStart, _range.BalanceChangesEnd);
        public ReadOnlySpan<NonceChange> NonceChanges => Slice(_list._nonceChanges, _range.NonceChangesStart, _range.NonceChangesEnd);
        public ReadOnlySpan<CodeChange> CodeChanges => Slice(_list._codeChanges, _range.CodeChangesStart, _range.CodeChangesEnd);

        internal bool Matches(ReadOnlyAccountChanges other)
        {
            if (!Address.Equals(other.Address)
                || !StorageReads.SequenceEqual(other.StorageReads)
                || !BalanceChanges.SequenceEqual(other.BalanceChanges)
                || !NonceChanges.SequenceEqual(other.NonceChanges)
                || !CodeChanges.SequenceEqual(other.CodeChanges))
            {
                return false;
            }

            ReadOnlySpan<ReadOnlySlotChanges> otherSlots = other.StorageChanges;
            if (otherSlots.Length != SlotCount) return false;
            for (int slot = 0; slot < otherSlots.Length; slot++)
            {
                if (GetSlotKey(slot) != otherSlots[slot].Key || !GetSlotChanges(slot).SequenceEqual(otherSlots[slot].Changes)) return false;
            }

            return true;
        }

        private static ReadOnlySpan<T> Slice<T>(List<T> list, int start, int end) => CollectionsMarshal.AsSpan(list)[start..end];
    }

    internal struct AccountRange
    {
        public Address Address;
        public uint FirstIndex;
        public int SlotsStart, SlotsEnd;
        public int StorageReadsStart, StorageReadsEnd;
        public int BalanceChangesStart, BalanceChangesEnd;
        public int NonceChangesStart, NonceChangesEnd;
        public int CodeChangesStart, CodeChangesEnd;
    }

    private readonly record struct SlotRange(UInt256 Key, int ChangesStart, int ChangesCount);

    private readonly ref struct Runs(ReadOnlySpan<BlockAccessListLog> logs, Span<int> positions) : ILoserTreeRuns
    {
        private readonly ReadOnlySpan<BlockAccessListLog> _logs = logs;
        private readonly Span<int> _positions = positions;

        public int Count => _logs.Length;

        public bool IsExhausted(int run) => _positions[run] >= _logs[run].Items.Length;

        public int CompareHeads(int left, int right)
            => BlockAccessListLog.CompareItems(in _logs[left].Items[_positions[left]], in _logs[right].Items[_positions[right]]);

        public void Advance(int run) => _positions[run]++;
    }
}
