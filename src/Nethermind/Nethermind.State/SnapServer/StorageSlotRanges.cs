// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;

namespace Nethermind.State.SnapServer;

/// <summary>Storage slots of a served storage range reply, one group per account, with all values in one pooled buffer that the reply owns.</summary>
public sealed class StorageSlotRanges : IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>>
{
    private readonly ArrayPoolList<byte> _values;
    private readonly ArrayPoolList<Slot> _slots = new(0);
    private readonly ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>> _accounts;
    private int _firstOpenSlot;
    private int _openSlotsLength;

    public StorageSlotRanges(int accountCapacity) : this(accountCapacity, SafeArrayPool<byte>.Shared) { }

    internal StorageSlotRanges(int accountCapacity, ArrayPool<byte> pool)
    {
        _values = new ArrayPoolList<byte>(pool, 0);
        _accounts = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(accountCapacity);
    }

    public int Count => _accounts.Count;

    /// <summary>The RLP length of all the account groups, each with its sequence prefix.</summary>
    public int AccountsLength { get; private set; }

    public IOwnedReadOnlyList<PathWithStorageSlot> this[int index] => _accounts[index];

    public ReadOnlySpan<IOwnedReadOnlyList<PathWithStorageSlot>> AsSpan() => _accounts.AsSpan();

    internal void AddSlot(in ValueHash256 path, ReadOnlySpan<byte> value)
    {
        _slots.Add(new Slot(path, _values.Count, value.Length));
        _values.AddRange(value);
        _openSlotsLength += Rlp.LengthOfSequence(Rlp.LengthOfKeccakRlp + Rlp.LengthOf(value));
    }

    internal bool TryCompleteAccount()
    {
        int count = _slots.Count - _firstOpenSlot;
        if (count == 0) return false;

        _accounts.Add(new AccountSlots(this, _firstOpenSlot, count, _openSlotsLength));
        AccountsLength += Rlp.LengthOfSequence(_openSlotsLength);
        _firstOpenSlot = _slots.Count;
        _openSlotsLength = 0;
        return true;
    }

    private ref readonly ValueHash256 GetPath(int slot) => ref _slots.AsSpan()[slot].Path;

    private ReadOnlySpan<byte> GetValue(int slot)
    {
        Slot entry = _slots[slot];
        return _values.AsSpan().Slice(entry.Start, entry.Length);
    }

    public IEnumerator<IOwnedReadOnlyList<PathWithStorageSlot>> GetEnumerator() => ((IEnumerable<IOwnedReadOnlyList<PathWithStorageSlot>>)_accounts).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        _values.Dispose();
        _slots.Dispose();
        _accounts.Dispose();
    }

    private readonly struct Slot(in ValueHash256 path, int start, int length)
    {
        public readonly ValueHash256 Path = path;
        public readonly int Start = start;
        public readonly int Length = length;
    }

    /// <summary>The slots of one account in a <see cref="StorageSlotRanges"/> reply.</summary>
    public sealed class AccountSlots : IOwnedReadOnlyList<PathWithStorageSlot>
    {
        private readonly StorageSlotRanges _owner;
        private readonly int _firstSlot;
        private PathWithStorageSlot[]? _slots;

        internal AccountSlots(StorageSlotRanges owner, int firstSlot, int count, int slotsLength)
        {
            _owner = owner;
            _firstSlot = firstSlot;
            Count = count;
            SlotsLength = slotsLength;
        }

        public int Count { get; }

        /// <summary>The RLP length of all the path and value pairs, each with its sequence prefix.</summary>
        public int SlotsLength { get; }

        public PathWithStorageSlot this[int index] => Materialize()[index];

        /// <summary>The path of the slot at <paramref name="index"/>.</summary>
        public ref readonly ValueHash256 GetPath(int index) => ref _owner.GetPath(_firstSlot + index);

        /// <summary>The RLP value of the slot at <paramref name="index"/>, as stored in the trie leaf.</summary>
        public ReadOnlySpan<byte> GetValue(int index) => _owner.GetValue(_firstSlot + index);

        public ReadOnlySpan<PathWithStorageSlot> AsSpan() => Materialize();

        private PathWithStorageSlot[] Materialize()
        {
            if (_slots is not null) return _slots;

            PathWithStorageSlot[] slots = new PathWithStorageSlot[Count];
            for (int i = 0; i < slots.Length; i++)
            {
                slots[i] = new PathWithStorageSlot(in GetPath(i), GetValue(i).ToArray());
            }

            return _slots = slots;
        }

        public IEnumerator<PathWithStorageSlot> GetEnumerator() => ((IEnumerable<PathWithStorageSlot>)Materialize()).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public void Dispose() { }
    }
}
