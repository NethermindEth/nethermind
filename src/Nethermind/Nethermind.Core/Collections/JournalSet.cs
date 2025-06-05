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
    public sealed class JournalSet<T> : IReadOnlyCollection<T>, ICollection<T>, IJournal<int>
    {
        private readonly List<T> _items = [];
        private readonly HashSet<T> _set = [];
        public int TakeSnapshot() => Position;

        private int Position => Count - 1;

        [SkipLocalsInit]
        public void Restore(int snapshot)
        {
            if (snapshot >= _set.Count)
            {
                ThrowInvalidRestore(snapshot);
            }

            // we use dictionary to remove items added after snapshot
            foreach (T item in CollectionsMarshal.AsSpan(_items)[(snapshot + 1)..])
            {
                _set.Remove(item);
            }

            CollectionsMarshal.SetCount(_items, snapshot + 1);
        }

        [DoesNotReturn]
        [StackTraceHidden]
        private void ThrowInvalidRestore(int snapshot)
            => throw new InvalidOperationException($"{nameof(JournalSet<T>)} tried to restore snapshot {snapshot} beyond current position {Count}");

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
            _items.Clear();
            _set.Clear();
        }

        public IEnumerator<T> GetEnumerator() => _set.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public bool Remove(T item) => throw new NotSupportedException("Cannot remove from Journal, use Restore(int snapshot) instead.");
        public int Count => _set.Count;
        public bool IsReadOnly => false;
        void ICollection<T>.Add(T item) => Add(item);
        public bool Contains(T item) => _set.Contains(item);
        public void CopyTo(T[] array, int arrayIndex) => _set.CopyTo(array, arrayIndex);
    }
}
