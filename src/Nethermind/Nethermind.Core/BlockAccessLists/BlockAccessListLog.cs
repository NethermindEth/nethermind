// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Collections;
using Nethermind.Core.Resettables;
using Nethermind.Core.Threading;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// The state accesses of one or more block access indexes, appended by a <see cref="BlockAccessIndexRecorder"/> and
/// then sealed into a sorted run that <see cref="GeneratedBlockAccessList"/> merges with the runs of other logs.
/// </summary>
/// <remarks>
/// A log is shared by the indexes that run one after another on the same thread, so a block needs one log per
/// worker rather than one per transaction, and the merge works over that many runs. Each entry carries its index, and
/// <see cref="Seal"/> collapses each key's entries per index. <see cref="Clear"/> keeps the collections, so a reused
/// instance stops allocating once warm.
/// </remarks>
public sealed class BlockAccessListLog : IResettable
{
    private readonly List<Entry> _entries = [];
    private readonly List<CodeWrite> _codeWrites = [];
    private readonly List<int> _sortedEntries = [];
    private readonly List<Item> _items = [];
    private readonly Comparison<int> _compareEntries;

    public BlockAccessListLog() => _compareEntries = CompareEntries;

    /// <summary>The number of entries, which is also the position the next entry takes.</summary>
    public int Count => _entries.Count;

    /// <summary>The sorted run <see cref="Seal"/> built.</summary>
    internal ReadOnlySpan<Item> Items => CollectionsMarshal.AsSpan(_items);

    internal Span<Entry> Entries => CollectionsMarshal.AsSpan(_entries);

    internal int AddEntry(in Entry entry)
    {
        _entries.Add(entry);
        return _entries.Count - 1;
    }

    internal int AddCodeWrite(in CodeWrite codeWrite)
    {
        _codeWrites.Add(codeWrite);
        return _codeWrites.Count - 1;
    }

    internal ref readonly CodeWrite GetCodeWrite(int position) => ref CollectionsMarshal.AsSpan(_codeWrites)[position];

    /// <summary>Drops the entries from <paramref name="count"/> on, such as those of a transaction left out of a block.</summary>
    public void Truncate(int count) => CollectionsMarshal.SetCount(_entries, count);

    /// <summary>Calls <paramref name="visit"/> with each key the entries from <paramref name="start"/> on access.</summary>
    /// <remarks>A storage key comes with its slot; any other key with <see langword="null"/>.</remarks>
    internal void VisitAccesses<TVisitor>(int start, ref TVisitor visit) where TVisitor : struct, IAccessVisitor
    {
        foreach (ref readonly Entry entry in Entries[start..])
        {
            if (entry.Key.Kind is EntryKind.Storage or EntryKind.StorageRead) visit.Storage(in entry.Key.Cell);
            else visit.Account(entry.Key.Cell.Address);
        }
    }

    internal interface IAccessVisitor
    {
        void Account(Address address);
        void Storage(in StorageCell cell);
    }

    /// <summary>Builds the sorted run of the logged changes and accesses; call once every index has executed.</summary>
    /// <remarks>
    /// The log is sorted by key, index and position, so each key's entries at one index are adjacent and the last
    /// write that was not reverted holds the final value. A write counts as a change only when that value differs
    /// from the value before the index, so a slot set back to its original value is a read, as is a slot whose writes
    /// all reverted. A slot changed at no index becomes one read item.
    /// </remarks>
    public void Seal()
    {
        _items.Clear();
        _sortedEntries.Clear();
        for (int position = 0; position < _entries.Count; position++) _sortedEntries.Add(position);
        Span<int> sorted = CollectionsMarshal.AsSpan(_sortedEntries);
        sorted.Sort(_compareEntries);

        ReadOnlySpan<Entry> entries = CollectionsMarshal.AsSpan(_entries);
        Address? address = null;
        int accountItem = 0;
        uint firstIndex = 0;
        for (int keyStart = 0, keyEnd; keyStart < sorted.Length; keyStart = keyEnd)
        {
            ref readonly EntryKey key = ref entries[sorted[keyStart]].Key;
            for (keyEnd = keyStart + 1; keyEnd < sorted.Length && CompareKeys(in key, in entries[sorted[keyEnd]].Key) == 0; keyEnd++) { }

            if (!key.Cell.Address.Equals(address))
            {
                if (address is not null) SetAccountItem(accountItem, address, firstIndex);
                address = key.Cell.Address;
                accountItem = _items.Count;
                firstIndex = uint.MaxValue;
                _items.Add(default);
            }

            // Each key's entries are ordered by index, so its first entry has its lowest.
            firstIndex = uint.Min(firstIndex, entries[sorted[keyStart]].Index);

            Section section = SectionOf(key.Kind);
            if (section == Section.Account) continue;

            bool changed = false;
            for (int indexStart = keyStart, indexEnd; indexStart < keyEnd; indexStart = indexEnd)
            {
                uint index = entries[sorted[indexStart]].Index;
                int latest = -1;
                for (indexEnd = indexStart; indexEnd < keyEnd && entries[sorted[indexEnd]].Index == index; indexEnd++)
                {
                    ref readonly Entry entry = ref entries[sorted[indexEnd]];
                    if (!entry.Reverted && IsWrite(entry.Key.Kind)) latest = sorted[indexEnd];
                }

                if (latest < 0) continue;
                ref readonly Entry write = ref entries[latest];
                bool isChange = section switch
                {
                    Section.Balance => write.Value != write.PreTxValue,
                    Section.Code => write.HasValue && IsCodeChanged(in GetCodeWrite((int)write.Value.u0)),
                    Section.Nonce => write.HasValue,
                    _ => write.HasValue && write.Value != write.PreTxValue,
                };

                if (!isChange) continue;
                changed = true;
                _items.Add(new Item(address, section, in key.Cell.Index, in write.Value, index, isChange: true));
            }

            if (section == Section.Storage && !changed)
            {
                _items.Add(new Item(address, section, in key.Cell.Index, default, entries[sorted[keyStart]].Index, isChange: false));
            }
        }

        if (address is not null) SetAccountItem(accountItem, address, firstIndex);

        static bool IsCodeChanged(in CodeWrite codeWrite) => !codeWrite.Change.Code.AsSpan().SequenceEqual(codeWrite.PreTxCode.Span);
    }

    // The account item precedes its sections but takes the lowest index of all of them, known only once they are done.
    private void SetAccountItem(int position, Address address, uint firstIndex)
        => CollectionsMarshal.AsSpan(_items)[position] = new Item(address, Section.Account, default, default, firstIndex, isChange: false);

    /// <summary>The code change a sealed <see cref="Section.Code"/> item refers to.</summary>
    internal CodeChange GetCodeChange(in Item item) => GetCodeWrite((int)item.Value.u0).Change;

    /// <summary>Whether the sealed runs of <paramref name="logs"/> together hold exactly the accounts and changes of
    /// <paramref name="suggested"/>, and only storage reads it declares.</summary>
    /// <param name="logs">Sealed logs, together holding each block access index at most once.</param>
    /// <param name="suggested">The block's access list.</param>
    /// <remarks>
    /// Checks each log on its own, in parallel, instead of merging them. As no index is in two logs, logs that hold only
    /// what <paramref name="suggested"/> declares, together hold as many changes as it and touch all its accounts hold
    /// exactly its accounts and changes. Whether the declared reads all happened is for <see cref="BalReadStoragePlan"/>.
    /// </remarks>
    public static bool Matches(ReadOnlySpan<BlockAccessListLog> logs, ReadOnlyBlockAccessList suggested)
    {
        ReadOnlySpan<ReadOnlyAccountChanges> accounts = suggested.AccountChanges.AsSpan();
        int suggestedChanges = suggested.TotalStorageChangeEvents;
        foreach (ReadOnlyAccountChanges account in accounts)
        {
            suggestedChanges += account.BalanceChanges.Length + account.NonceChanges.Length + account.CodeChanges.Length;
        }

        int words = (accounts.Length + 63) / 64;
        ulong[] touchedAccounts = ArrayPool<ulong>.Shared.Rent(words);
        int[] changeCounts = ArrayPool<int>.Shared.Rent(logs.Length);
        BlockAccessListLog[] runs = ArrayPool<BlockAccessListLog>.Shared.Rent(logs.Length);
        try
        {
            touchedAccounts.AsSpan(0, words).Clear();
            logs.CopyTo(runs);
            ParallelUnbalancedWork.For(0, logs.Length, (runs, suggested, touchedAccounts, changeCounts), static (i, state) =>
            {
                state.changeCounts[i] = state.runs[i].IsDeclaredBy(state.suggested, state.touchedAccounts, out int changeCount) ? changeCount : -1;
                return state;
            });

            int changes = 0;
            foreach (int changeCount in changeCounts.AsSpan(0, logs.Length))
            {
                if (changeCount < 0) return false;
                changes += changeCount;
            }

            return changes == suggestedChanges && AllTouched(touchedAccounts.AsSpan(0, words), accounts.Length);
        }
        finally
        {
            ArrayPool<BlockAccessListLog>.Shared.Return(runs, clearArray: true);
            ArrayPool<int>.Shared.Return(changeCounts);
            ArrayPool<ulong>.Shared.Return(touchedAccounts);
        }

        static bool AllTouched(ReadOnlySpan<ulong> touched, int count)
        {
            for (int word = 0; word < touched.Length; word++)
            {
                int bits = int.Min(64, count - word * 64);
                ulong expected = bits == 64 ? ulong.MaxValue : (1UL << bits) - 1;
                if (touched[word] != expected) return false;
            }

            return true;
        }
    }

    /// <summary>Whether <paramref name="suggested"/> declares every account, change and storage read of the sealed run.</summary>
    /// <param name="suggested">The block's access list.</param>
    /// <param name="touchedAccounts">A bit per account of <paramref name="suggested"/>, set for each account the run holds; shared by the logs of a block.</param>
    /// <param name="changeCount">The number of changes the run holds.</param>
    /// <remarks>
    /// A change must equal the one <paramref name="suggested"/> lists for its key and index, and a read must name a slot it
    /// lists as read or changed.
    /// </remarks>
    private bool IsDeclaredBy(ReadOnlyBlockAccessList suggested, ulong[] touchedAccounts, out int changeCount)
    {
        changeCount = 0;
        ReadOnlySpan<ReadOnlyAccountChanges> accounts = suggested.AccountChanges.AsSpan();
        ReadOnlyAccountChanges? account = null;
        int ordinal = 0;
        foreach (ref readonly Item item in Items)
        {
            if (item.Section == Section.Account)
            {
                // The run is sorted by address, so its next account is never before the previous one.
                ordinal = FindAccount(accounts, ordinal, item.Address);
                if (ordinal < 0) return false;
                account = accounts[ordinal];
                Interlocked.Or(ref touchedAccounts[ordinal >> 6], 1UL << (ordinal & 63));
                continue;
            }

            bool declared = item.Section switch
            {
                Section.Storage when !item.IsChange => account!.TryGetDeclaredSlotChanges(in item.Slot, out _),
                Section.Storage => account!.TryGetSlotChanges(in item.Slot, out ReadOnlySlotChanges? slot)
                    && Contains<StorageChange>(slot.Changes, new StorageChange(item.Index, in item.Value)),
                Section.Balance => Contains<BalanceChange>(account!.BalanceChanges, new BalanceChange(item.Index, item.Value)),
                Section.Nonce => Contains<NonceChange>(account!.NonceChanges, new NonceChange(item.Index, item.Value.u0)),
                _ => Contains<CodeChange>(account!.CodeChanges, GetCodeChange(in item)),
            };
            if (!declared) return false;
            if (item.IsChange) changeCount++;
        }

        return true;

        static bool Contains<T>(ReadOnlySpan<T> changes, in T change) where T : struct, IIndexedChange, IEquatable<T>
        {
            int position = changes.BinarySearch(new IndexKey<T>(change.Index));
            return position >= 0 && changes[position].Equals(change);
        }
    }

    private static int FindAccount(ReadOnlySpan<ReadOnlyAccountChanges> accounts, int from, Address address)
    {
        for (int left = from, right = accounts.Length - 1; left <= right;)
        {
            int middle = left + ((right - left) >> 1);
            int cmp = accounts[middle].Address.Bytes.SequenceCompareTo(address.Bytes);
            if (cmp == 0) return middle;
            if (cmp < 0) left = middle + 1;
            else right = middle - 1;
        }

        return -1;
    }

    public void Clear()
    {
        _entries.ClearAndTrim();
        _codeWrites.ClearAndTrim();
        _sortedEntries.ClearAndTrim();
        _items.ClearAndTrim();
    }

    public void Reset() => Clear();

    /// <summary>Orders items by address, then section, slot and index: the EIP-7928 encoding order.</summary>
    internal static int CompareItems(in Item x, in Item y)
    {
        int cmp = CompareKeys(x.Address, x.Section, in x.Slot, y.Address, y.Section, in y.Slot);
        return cmp != 0 ? cmp : x.Index.CompareTo(y.Index);
    }

    private static int CompareKeys(in EntryKey x, in EntryKey y)
        => CompareKeys(x.Cell.Address, SectionOf(x.Kind), in x.Cell.Index, y.Cell.Address, SectionOf(y.Kind), in y.Cell.Index);

    private static int CompareKeys(Address xAddress, Section xSection, in UInt256 xSlot, Address yAddress, Section ySection, in UInt256 ySlot)
    {
        int cmp = ReferenceEquals(xAddress, yAddress) ? 0 : xAddress.Bytes.SequenceCompareTo(yAddress.Bytes);
        if (cmp != 0) return cmp;
        cmp = (byte)xSection - (byte)ySection;
        return cmp != 0 ? cmp : xSlot.CompareTo(in ySlot);
    }

    private int CompareEntries(int left, int right)
    {
        ReadOnlySpan<Entry> entries = CollectionsMarshal.AsSpan(_entries);
        ref readonly Entry x = ref entries[left];
        ref readonly Entry y = ref entries[right];
        int cmp = CompareKeys(in x.Key, in y.Key);
        if (cmp != 0) return cmp;
        cmp = x.Index.CompareTo(y.Index);
        return cmp != 0 ? cmp : left - right;
    }

    internal static bool IsWrite(EntryKind kind) => kind is not (EntryKind.AccountRead or EntryKind.StorageRead);

    private static Section SectionOf(EntryKind kind) => kind switch
    {
        EntryKind.AccountRead or EntryKind.Existence => Section.Account,
        EntryKind.StorageRead or EntryKind.Storage => Section.Storage,
        EntryKind.Balance => Section.Balance,
        EntryKind.Nonce => Section.Nonce,
        _ => Section.Code,
    };

    /// <summary>A section of an account's EIP-7928 entry, in encoding order.</summary>
    internal enum Section : byte
    {
        /// <summary>The account itself, present for every accessed account.</summary>
        Account,
        Storage,
        Balance,
        Nonce,
        Code,
    }

    /// <summary>One change or access in a sealed run.</summary>
    /// <remarks>A <see cref="Section.Code"/> item's <see cref="Value"/> is a position for <see cref="GetCodeChange"/>.</remarks>
    internal readonly struct Item(Address address, Section section, in UInt256 slot, in UInt256 value, uint index, bool isChange)
    {
        public readonly Address Address = address;
        public readonly UInt256 Slot = slot;
        public readonly UInt256 Value = value;
        public readonly uint Index = index;
        public readonly Section Section = section;
        public readonly bool IsChange = isChange;
    }

    internal enum EntryKind : byte
    {
        AccountRead,
        StorageRead,
        Storage,
        Balance,
        Nonce,
        Code,
        Existence,
    }

    internal readonly struct EntryKey(in StorageCell cell, EntryKind kind) : IEquatable<EntryKey>
    {
        public readonly StorageCell Cell = cell;
        public readonly EntryKind Kind = kind;

        public EntryKey(Address address, EntryKind kind) : this(new StorageCell(address, UInt256.Zero), kind) { }

        public bool Equals(EntryKey other) => Kind == other.Kind && Cell.Equals(in other.Cell);
        public override bool Equals(object? obj) => obj is EntryKey other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Cell.GetHashCode(), Kind);
    }

    internal struct Entry
    {
        public EntryKey Key;
        public UInt256 PreTxValue;
        /// <summary>The written value; for code, the position of its <see cref="CodeWrite"/>.</summary>
        public UInt256 Value;
        public ulong Epoch;
        /// <summary>The key's previous write at the same index, restored by a revert, or -1.</summary>
        public int Previous;
        public uint Index;
        /// <summary>False once a SELFDESTRUCT dropped the change.</summary>
        public bool HasValue;
        /// <summary>Whether a revert undid the write, which leaves only the access.</summary>
        public bool Reverted;
    }

    internal readonly record struct CodeWrite(ReadOnlyMemory<byte> PreTxCode, CodeChange Change);
}
