// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
    /// Items are kept in an <see cref="OptimizedHashSet{T}"/>, so the comparer must be the item's own equality.
    /// </remarks>
    public sealed class JournalSet<T>(EqualityComparer<T> equalityComparer) : ICollection<T>, IJournal<int> where T : notnull, IEquatable<T>
    {
#if !ZK_EVM
        // Removing entries one by one beats zeroing every bucket only while few remain: add, restore, clear and reuse
        // cycles on Address and StorageCell journals put the crossover between about Capacity/100 and Capacity/1000.
        private const int SparseClearCapacityDivisor = 256;
#endif

        private readonly List<T> _items = [];
        private readonly OptimizedHashSet<T> _set = new(GenericEqualityComparer.GetOptimized(equalityComparer));

        public int TakeSnapshot() => Position;

        private int Position => Count - 1;

        [SkipLocalsInit]
        public void Restore(int snapshot)
        {
            if (snapshot >= Count)
            {
                ThrowInvalidRestore(snapshot);
            }

            // Remove items added after snapshot.
            foreach (T item in CollectionsMarshal.AsSpan(_items)[(snapshot + 1)..])
            {
                _set.Remove(item);
            }

            CollectionsMarshal.SetCount(_items, snapshot + 1);
        }

        [DoesNotReturn, StackTraceHidden]
        private void ThrowInvalidRestore(int snapshot)
            => throw new InvalidOperationException($"{nameof(JournalSet<>)} tried to restore snapshot {snapshot} beyond current position {Count}");

        public bool Add(in T item)
        {
            if (_set.Add(item))
            {
                // we use dictionary in order to track item positions
                _items.Add(item);
                return true;
            }

            return false;
        }

        public void Clear()
        {
            // The guest set's own Clear already zeroes only the buckets in use while it is sparse.
#if !ZK_EVM
            if (Count <= _set.Capacity / SparseClearCapacityDivisor)
            {
                foreach (T item in CollectionsMarshal.AsSpan(_items))
                {
                    _set.Remove(item);
                }
            }
            else
#endif
            {
                _set.Clear();
            }

            _items.Clear();
        }

        /// <summary>Enumerates the items in the order they were first added, excluding those dropped by <see cref="Restore"/>.</summary>
        public List<T>.Enumerator GetEnumerator() => _items.GetEnumerator();
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public bool Remove(T item) => throw new NotSupportedException("Cannot remove from Journal, use Restore(int snapshot) instead.");
        public int Count => _set.Count;
        public bool IsReadOnly => false;
        void ICollection<T>.Add(T item) => Add(in item);
        public bool Contains(in T item) => _set.Contains(item);
        bool ICollection<T>.Contains(T item) => Contains(in item);
        public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
    }
}
