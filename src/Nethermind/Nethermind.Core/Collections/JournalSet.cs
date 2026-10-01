// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Core.Collections
{
    /// <summary>
    /// <see cref="ICollection{T}"/> of items <see cref="T"/> with ability to store and restore state snapshots.
    /// </summary>
    /// <typeparam name="T">Item type.</typeparam>
    /// <remarks>
    /// Due to snapshots <see cref="Remove"/> is not supported.
    /// <para>
    /// Each item is stored once, in insertion order, and indexed by an open-addressing table with linear probing.
    /// The table spans only what recent items needed: <see cref="Clear"/> shrinks it when the cleared items used
    /// little of it, and it doubles inside the array it keeps. A pooled hash set instead keeps its buckets at the
    /// largest size it ever reached, so after one large transaction each probe of a small one lands on a line of its
    /// own in a large bucket array; in an eth_call profile that bucket load was 43% of the access-list probe.
    /// </para>
    /// <para>
    /// <see cref="Restore"/> frees the newest entries first. With linear probing that is exact and needs no
    /// tombstones: an entry went into the first free slot on its probe path, so freeing the newest entry leaves the
    /// table as it was before that entry was added. Growth re-adds the entries in insertion order, which keeps the
    /// table equal to one built by adding the entries one by one, so the argument holds across growth too.
    /// </para>
    /// </remarks>
    public sealed class JournalSet<T>(EqualityComparer<T> equalityComparer) : ICollection<T>, IJournal<int>
    {
        /// <summary>Smallest table, in slots; a power of two.</summary>
        private const int MinTableSize = 16;

        /// <summary>
        /// With at most one item per this many slots at a <see cref="Clear"/>, their slots are freed one by one and the
        /// table shrinks; with more, the table is zeroed and keeps its size.
        /// </summary>
        private const int SparseClearTableDivisor = 16;

        /// <summary>2^32 divided by the golden ratio: spreads weak hashes, such as small integers, over the table.</summary>
        private const uint FibonacciMultiplier = 0x9E3779B9;

        private readonly List<T> _items = [];

        // Null for a value type with the default comparison, which the JIT then devirtualizes and inlines. A reference
        // type keeps a comparer instance, as HashSet<T> does: shared generic code would otherwise look up
        // EqualityComparer<T>.Default on every probe.
        private readonly IEqualityComparer<T>? _comparer =
            GenericEqualityComparer.GetOptimized(equalityComparer) ?? (typeof(T).IsValueType ? null : EqualityComparer<T>.Default);

        /// <summary>Hash of each item, parallel to <see cref="_items"/>, so growth and restore never hash again.</summary>
        private int[] _hashes = [];

        /// <summary>Slots past <see cref="_tableSize"/> are always free.</summary>
        private Slot[] _table = new Slot[MinTableSize];

        private int _tableSize = MinTableSize;
        private int _homeShift = 32 - BitOperations.Log2(MinTableSize);

        private struct Slot
        {
            public int Hash;

            /// <summary>Index of the item in <see cref="_items"/> plus one; zero marks a free slot.</summary>
            public int Entry;
        }

        public int TakeSnapshot() => Position;

        private int Position => Count - 1;

        public void Restore(int snapshot)
        {
            int count = _items.Count;
            if (snapshot >= count)
            {
                ThrowInvalidRestore(snapshot);
            }

            ArgumentOutOfRangeException.ThrowIfLessThan(snapshot, -1);

            // Newest first: see the remarks on the class.
            for (int i = count - 1; i > snapshot; i--)
            {
                FreeSlotOf(i);
            }

            CollectionsMarshal.SetCount(_items, snapshot + 1);
        }

        [DoesNotReturn, StackTraceHidden]
        private void ThrowInvalidRestore(int snapshot)
            => throw new InvalidOperationException($"{nameof(JournalSet<>)} tried to restore snapshot {snapshot} beyond current position {Count}");

        public bool Add(T item)
        {
            int hash = GetHash(item);
            ref Slot slot = ref FindSlot(item, hash);
            if (slot.Entry != 0)
            {
                return false;
            }

            int count = _items.Count;
            if (count >= _tableSize >> 1)
            {
                // Half full: double the table, then find the new free slot.
                Grow();
                slot = ref FindFreeSlot(hash);
            }

            if (count == _hashes.Length)
            {
                GrowHashes();
            }

            _items.Add(item);
            _hashes[count] = hash;
            slot.Hash = hash;
            slot.Entry = count + 1;
            return true;
        }

        public void Clear()
        {
            int count = _items.Count;
            if (count <= _tableSize / SparseClearTableDivisor)
            {
                // Few items in a large table, typically the transaction after a large one: free their slots and
                // shrink the table to what they needed.
                for (int i = count - 1; i >= 0; i--)
                {
                    FreeSlotOf(i);
                }

                SetTableSize(Math.Max(MinTableSize, (int)BitOperations.RoundUpToPowerOf2((uint)count * 2)));
            }
            else
            {
                // The table was in use: keep its size, so a run of similar transactions grows it only once.
                Array.Clear(_table, 0, _tableSize);
            }

            _items.Clear();
        }

        /// <summary>Enumerates the items in the order they were first added, excluding those dropped by <see cref="Restore"/>.</summary>
        public List<T>.Enumerator GetEnumerator() => _items.GetEnumerator();
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public bool Remove(T item) => throw new NotSupportedException("Cannot remove from Journal, use Restore(int snapshot) instead.");
        public int Count => _items.Count;
        public bool IsReadOnly => false;
        void ICollection<T>.Add(T item) => Add(item);
        public bool Contains(T item) => FindSlot(item, GetHash(item)).Entry != 0;
        public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetHash(T item)
        {
            if (typeof(T).IsValueType && _comparer is null)
            {
                return item!.GetHashCode();
            }

            return item is null ? 0 : _comparer!.GetHashCode(item);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool AreEqual(T x, T y)
        {
            if (typeof(T).IsValueType && _comparer is null)
            {
                return EqualityComparer<T>.Default.Equals(x, y);
            }

            return _comparer!.Equals(x, y);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private uint Home(int hash) => ((uint)hash * FibonacciMultiplier) >> _homeShift;

        /// <summary>The slot holding <paramref name="item"/>, or the free slot that ends its probe sequence.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ref Slot FindSlot(T item, int hash)
        {
            // The table is at most half full, so a probe always reaches a free slot, and _tableSize <= _table.Length.
            ref Slot table = ref MemoryMarshal.GetArrayDataReference(_table);
            ReadOnlySpan<T> items = CollectionsMarshal.AsSpan(_items);
            uint mask = (uint)_tableSize - 1;
            uint index = Home(hash);
            while (true)
            {
                ref Slot slot = ref Unsafe.Add(ref table, index);
                int entry = slot.Entry;
                if (entry == 0 || (slot.Hash == hash && AreEqual(items[entry - 1], item)))
                {
                    return ref slot;
                }

                index = (index + 1) & mask;
            }
        }

        private ref Slot FindFreeSlot(int hash)
        {
            ref Slot table = ref MemoryMarshal.GetArrayDataReference(_table);
            uint mask = (uint)_tableSize - 1;
            uint index = Home(hash);
            while (Unsafe.Add(ref table, index).Entry != 0)
            {
                index = (index + 1) & mask;
            }

            return ref Unsafe.Add(ref table, index);
        }

        /// <summary>Frees the slot of the item at <paramref name="index"/>, found from its stored hash.</summary>
        private void FreeSlotOf(int index)
        {
            ref Slot table = ref MemoryMarshal.GetArrayDataReference(_table);
            uint mask = (uint)_tableSize - 1;
            uint slot = Home(_hashes[index]);
            int entry = index + 1;
            while (Unsafe.Add(ref table, slot).Entry != entry)
            {
                slot = (slot + 1) & mask;
            }

            Unsafe.Add(ref table, slot) = default;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Grow()
        {
            int size = checked(_tableSize * 2);
            if (_table.Length < size)
            {
                _table = new Slot[size];
            }
            else
            {
                // Reusing the kept array: only the active part can hold entries.
                Array.Clear(_table, 0, _tableSize);
            }

            SetTableSize(size);

            // Insertion order, so the table stays one that adding the items one by one would build.
            ReadOnlySpan<int> hashes = _hashes.AsSpan(0, _items.Count);
            for (int i = 0; i < hashes.Length; i++)
            {
                ref Slot slot = ref FindFreeSlot(hashes[i]);
                slot.Hash = hashes[i];
                slot.Entry = i + 1;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void GrowHashes() => Array.Resize(ref _hashes, Math.Max(MinTableSize / 2, _hashes.Length * 2));

        private void SetTableSize(int size)
        {
            Debug.Assert(BitOperations.IsPow2(size) && size <= _table.Length);
            _tableSize = size;
            _homeShift = 32 - BitOperations.Log2((uint)size);
        }
    }
}
