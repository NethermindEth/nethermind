// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Collections;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Per-account changes recorded at a single BAL index (i.e. one transaction). Each scalar field
/// (balance / nonce / code) collapses to a single optional value because at one index there can
/// only be one final value. Storage changes carry one <see cref="StorageChange"/> per slot.
/// </summary>
public class AccountChangesAtIndex(Address address)
{
    public Address Address { get; private set; } = address;

    public BalanceChange? BalanceChange { get; internal set; }
    public NonceChange? NonceChange { get; internal set; }
    public CodeChange? CodeChange { get; internal set; }

    /// <summary>Physical existence after a mutation at this index, independently of EIP-161 emptiness.</summary>
    public bool? AccountExists { get; internal set; }

    public UInt256? PreTxBalance { get; internal set; }
    /// <summary>The nonce before the transaction, or <see langword="null"/> until a nonce change records it.</summary>
    public ulong? PreTxNonce { get; internal set; }
    /// <summary>The code before the transaction, or <see langword="default"/> until a code change records it.</summary>
    public ReadOnlyMemory<byte> PreTxCode { get; internal set; }

    // One record per slot this index wrote or seeded: its change, if any, and its transaction prestate, so a write
    // resolves both with a single lookup.
    private readonly Dictionary<UInt256, StorageSlot> _storageSlots = new(UInt256Comparer.GetOptimized());
    private int _storageChangeCount;
    private readonly HashSet<UInt256> _storageReads = new(UInt256Comparer.GetOptimized());
    private bool _storageReadsMayHoldChangedSlots;

    /// <summary>The slots changed at this index, each with its change.</summary>
    public StorageChangeView StorageChanges => new(this);
    public int StorageChangeCount => _storageChangeCount;

    /// <summary>Slots read but not changed at this index.</summary>
    /// <remarks>The first access after <see cref="AddStorageReadUnchecked"/> compacts the set, so it is not safe for concurrent readers.</remarks>
    public HashSet<UInt256> StorageReads
    {
        get
        {
            if (_storageReadsMayHoldChangedSlots) RemoveChangedSlotsFromReads();
            return _storageReads;
        }
    }

    private void RemoveChangedSlotsFromReads()
    {
        _storageReadsMayHoldChangedSlots = false;
        foreach (KeyValuePair<UInt256, StorageChange> change in StorageChanges) _storageReads.Remove(change.Key);
    }

    public bool HasStorageChange(in UInt256 key)
    {
        ref StorageSlot slot = ref CollectionsMarshal.GetValueRefOrNullRef(_storageSlots, key);
        return !Unsafe.IsNullRef(ref slot) && slot.HasChange;
    }

    public void SetStorageChange(in UInt256 key, StorageChange storageChange)
    {
        ref StorageSlot slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_storageSlots, key, out _);
        if (!slot.HasChange)
        {
            slot.HasChange = true;
            _storageChangeCount++;
        }
        slot.Change = storageChange;
    }

    public bool RemoveStorageChange(in UInt256 key)
    {
        ref StorageSlot slot = ref CollectionsMarshal.GetValueRefOrNullRef(_storageSlots, key);
        if (Unsafe.IsNullRef(ref slot) || !slot.HasChange) return false;

        if (slot.HasPreTx)
        {
            slot.HasChange = false;
            slot.Change = default;
        }
        else
        {
            _storageSlots.Remove(key);
        }
        _storageChangeCount--;
        return true;
    }

    /// <summary>Drops every storage change, keeping the slots' transaction prestate.</summary>
    public void ClearStorageChanges()
    {
        if (_storageChangeCount == 0) return;

        foreach (UInt256 key in _storageSlots.Keys) RemoveStorageChange(key);
    }

    public void AddStorageRead(in UInt256 key) => _storageReads.Add(key);

    /// <summary>Records a read of <paramref name="key"/>, which may also have a change at this index.</summary>
    /// <remarks>
    /// Saves the change lookup on every read: a changed slot recorded here leaves the reads when
    /// <see cref="StorageReads"/> is next accessed, so EIP-7928 still lists it only as a change.
    /// </remarks>
    public void AddStorageReadUnchecked(in UInt256 key)
    {
        if (_storageReads.Add(key) && _storageChangeCount != 0) _storageReadsMayHoldChangedSlots = true;
    }

    public bool RemoveStorageRead(in UInt256 key) => _storageReads.Remove(key);

    /// <summary>
    /// Records a write of <paramref name="after"/> over <paramref name="before"/> to <paramref name="key"/> at
    /// <paramref name="index"/>, with one lookup of the slot.
    /// </summary>
    /// <param name="key">The slot written.</param>
    /// <param name="before">The value the write replaces, which becomes the transaction prestate on the slot's first write.</param>
    /// <param name="after">The value written.</param>
    /// <param name="index">The block access list index of the write.</param>
    /// <param name="journalEpoch">The current snapshot epoch.</param>
    /// <param name="previous">The slot's change before this write, or <see langword="default"/> without one.</param>
    /// <param name="hadChange">Whether the slot had a change before this write.</param>
    /// <returns>Whether this snapshot epoch needs an undo record of <paramref name="previous"/>.</returns>
    /// <remarks>
    /// A write back to the transaction prestate drops the slot's change and lists it as a read, as EIP-7928 does.
    /// One undo per slot between snapshot boundaries also covers interleaved writes to other slots.
    /// </remarks>
    internal bool RecordStorageWrite(in UInt256 key, in UInt256 before, in UInt256 after, uint index, ulong journalEpoch,
        out StorageChange previous, out bool hadChange)
    {
        ref StorageSlot slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_storageSlots, key, out _);
        hadChange = slot.HasChange;
        previous = slot.Change;
        bool needsUndo = !slot.HasPreTx || slot.JournalEpoch != journalEpoch;
        if (!slot.HasPreTx)
        {
            slot.PreTx = before;
            slot.HasPreTx = true;
        }
        slot.JournalEpoch = journalEpoch;

        if (slot.PreTx != after)
        {
            slot.Change = new StorageChange(index, after);
            if (!hadChange)
            {
                slot.HasChange = true;
                _storageChangeCount++;
                // An existing change already excludes this slot from the read set, or the next StorageReads access will.
                _storageReads.Remove(key);
            }
        }
        else
        {
            if (hadChange)
            {
                slot.HasChange = false;
                slot.Change = default;
                _storageChangeCount--;
            }
            _storageReads.Add(key);
        }

        return needsUndo;
    }

    /// <summary>Records a slot's transaction prestate known before its first write, such as one hidden by a storage clear.</summary>
    public void SeedPreTxStorage(in UInt256 key, in UInt256 value)
    {
        ref StorageSlot slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_storageSlots, key, out _);
        if (slot.HasPreTx) return;

        slot.PreTx = value;
        slot.HasPreTx = true;
        slot.JournalEpoch = ulong.MaxValue;
    }

    /// <summary>Reads the captured transaction-prestate value for a slot, if one was recorded this transaction.</summary>
    public bool TryGetPreTxStorage(in UInt256 key, out UInt256 value)
    {
        ref StorageSlot slot = ref CollectionsMarshal.GetValueRefOrNullRef(_storageSlots, key);
        if (!Unsafe.IsNullRef(ref slot) && slot.HasPreTx)
        {
            value = slot.PreTx;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>What this index holds for one slot: its change and its transaction prestate, each when present.</summary>
    internal struct StorageSlot
    {
        public StorageChange Change;
        public UInt256 PreTx;
        public ulong JournalEpoch;
        public bool HasChange;
        public bool HasPreTx;
    }

    /// <summary>The slots an <see cref="AccountChangesAtIndex"/> changed, each with its change.</summary>
    /// <remarks>A view over the live records: it reflects every later change and enumerates without allocating.</remarks>
    public readonly struct StorageChangeView : IEnumerable<KeyValuePair<UInt256, StorageChange>>
    {
        private readonly AccountChangesAtIndex _owner;

        internal StorageChangeView(AccountChangesAtIndex owner) => _owner = owner;

        public int Count => _owner._storageChangeCount;

        /// <summary>The capacity of the records behind the view, which also hold slots with only a prestate.</summary>
        public int Capacity => _owner._storageSlots.Capacity;

        public StorageChange this[in UInt256 key] =>
            TryGetValue(in key, out StorageChange change) ? change : throw new KeyNotFoundException();

        public bool TryGetValue(in UInt256 key, out StorageChange change)
        {
            ref StorageSlot slot = ref CollectionsMarshal.GetValueRefOrNullRef(_owner._storageSlots, key);
            if (!Unsafe.IsNullRef(ref slot) && slot.HasChange)
            {
                change = slot.Change;
                return true;
            }

            change = default;
            return false;
        }

        public Enumerator GetEnumerator() => new(_owner._storageSlots.GetEnumerator());

        IEnumerator<KeyValuePair<UInt256, StorageChange>> IEnumerable<KeyValuePair<UInt256, StorageChange>>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<KeyValuePair<UInt256, StorageChange>>
        {
            private Dictionary<UInt256, StorageSlot>.Enumerator _slots;

            internal Enumerator(Dictionary<UInt256, StorageSlot>.Enumerator slots) => _slots = slots;

            public readonly KeyValuePair<UInt256, StorageChange> Current => new(_slots.Current.Key, _slots.Current.Value.Change);

            readonly object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                while (_slots.MoveNext())
                {
                    if (_slots.Current.Value.HasChange) return true;
                }

                return false;
            }

            void IEnumerator.Reset() => throw new NotSupportedException();

            public void Dispose() => _slots.Dispose();
        }
    }

    public void Reset(Address address)
    {
        Address = address;
        BalanceChange = null;
        NonceChange = null;
        CodeChange = null;
        AccountExists = null;
        PreTxBalance = null;
        PreTxNonce = null;
        PreTxCode = default;
        _storageSlots.ClearAndTrim();
        _storageChangeCount = 0;
        _storageReads.ClearAndTrim();
        _storageReadsMayHoldChangedSlots = false;
    }
}
