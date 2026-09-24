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
    /// <remarks>Due to snapshots <see cref="Remove"/> is not supported.</remarks>
    public sealed class JournalSet<T>(EqualityComparer<T> equalityComparer) : ICollection<T>, IJournal<int>
    {
        private readonly List<T> _items = [];
        private readonly HashSet<T> _set = new(GenericEqualityComparer.GetOptimized(equalityComparer));
        private bool _enumerationNeedsNormalization;

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

        public bool Add(T item)
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
            if (_items.Count <= _set.Capacity / 8)
            {
                foreach (T item in _items)
                {
                    _set.Remove(item);
                }

                _enumerationNeedsNormalization = true;
            }
            else
            {
                _set.Clear();
                _enumerationNeedsNormalization = false;
            }

            _items.Clear();
        }

        public HashSet<T>.Enumerator GetEnumerator()
        {
            NormalizeForEnumeration();
            return _set.GetEnumerator();
        }
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public bool Remove(T item) => throw new NotSupportedException("Cannot remove from Journal, use Restore(int snapshot) instead.");
        public int Count => _set.Count;
        public bool IsReadOnly => false;
        void ICollection<T>.Add(T item) => Add(item);
        public bool Contains(T item) => _set.Contains(item);
        /// <summary>
        /// Gets the first item added to the set.
        /// </summary>
        /// <remarks>The caller must ensure the set is not empty.</remarks>
        public T First => _items[0];
        public void CopyTo(T[] array, int arrayIndex)
        {
            NormalizeForEnumeration();
            _set.CopyTo(array, arrayIndex);
        }

        private void NormalizeForEnumeration()
        {
            if (!_enumerationNeedsNormalization)
            {
                return;
            }

            // Sparse clears leave reusable HashSet slots whose order can reverse later additions; rebuild only when enumeration is requested.
            _set.Clear();
            foreach (T item in _items)
            {
                _set.Add(item);
            }

            _enumerationNeedsNormalization = false;
        }
    }
}
