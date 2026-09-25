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
        private readonly bool _useSparseClear;

        /// <summary>
        /// Initializes a journal set, optionally retaining sparse clear storage for reuse.
        /// </summary>
        /// <param name="equalityComparer">Comparer used to determine whether items are already present.</param>
        /// <param name="useSparseClear">Whether to remove individual items on small clears and enumerate from the insertion journal.</param>
        public JournalSet(EqualityComparer<T> equalityComparer, bool useSparseClear) : this(equalityComparer)
            => _useSparseClear = useSparseClear;

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
            if (_useSparseClear && _items.Count <= _set.Capacity / 8)
            {
                foreach (T item in _items)
                {
                    _set.Remove(item);
                }
            }
            else
            {
                _set.Clear();
            }

            _items.Clear();
        }

        /// <summary>Enumerates the set using its configured ordering.</summary>
        /// <remarks>Sparse journals enumerate their insertion journal directly; dense journals retain the backing <see cref="HashSet{T}"/> ordering.</remarks>
        public Enumerator GetEnumerator() => _useSparseClear
            ? new(_items.GetEnumerator())
            : new(_set.GetEnumerator());
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
            if (_useSparseClear)
            {
                _items.CopyTo(array, arrayIndex);
            }
            else
            {
                _set.CopyTo(array, arrayIndex);
            }
        }

        /// <summary>Enumerates the items in the journal set.</summary>
        public struct Enumerator : IEnumerator<T>
        {
            private readonly bool _useItems;
            private HashSet<T>.Enumerator _setEnumerator;
            private List<T>.Enumerator _itemsEnumerator;

            internal Enumerator(HashSet<T>.Enumerator setEnumerator)
            {
                _useItems = false;
                _setEnumerator = setEnumerator;
                _itemsEnumerator = default;
            }

            internal Enumerator(List<T>.Enumerator itemsEnumerator)
            {
                _useItems = true;
                _setEnumerator = default;
                _itemsEnumerator = itemsEnumerator;
            }

            /// <inheritdoc/>
            public T Current => _useItems ? _itemsEnumerator.Current : _setEnumerator.Current;
            object IEnumerator.Current => Current!;
            /// <inheritdoc/>
            public bool MoveNext() => _useItems ? _itemsEnumerator.MoveNext() : _setEnumerator.MoveNext();
            /// <inheritdoc/>
            public void Reset() => throw new NotSupportedException();
            /// <inheritdoc/>
            public void Dispose()
            {
                if (_useItems)
                {
                    _itemsEnumerator.Dispose();
                }
                else
                {
                    _setEnumerator.Dispose();
                }
            }
        }
    }
}
