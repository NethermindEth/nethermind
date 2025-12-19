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
    public sealed class JournalSet<T> : IHashSetEnumerableCollection<T>, ICollection<T>, IJournal<int>
    {
        private readonly List<T> _items = [];
        public int TakeSnapshot() => Position;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ContainsItem(List<T> items, T item)
        {
            // Compatibility: List<T>.Contains can route through EqualityComparer<T>.Default which may trigger
            // generic type construction in restricted runtimes. We special-case reference types to use
            // ReferenceEquals scanning instead.
            if (!typeof(T).IsValueType)
            {
                object? target = item;
                for (int i = 0; i < items.Count; i++)
                {
                    if (ReferenceEquals(items[i], target))
                    {
                        return true;
                    }
                }

                return false;
            }

            // Value types: fall back to List<T>.Contains (may still use EqualityComparer<T>.Default, but we
            // cannot safely avoid equality for value types without changing semantics).
            return items.Contains(item);
        }

        private int Position => Count - 1;

        [SkipLocalsInit]
        public void Restore(int snapshot)
        {
            if (snapshot >= Count)
            {
                ThrowInvalidRestore(snapshot);
            }

            // Just truncate; uniqueness is enforced by Add().
            CollectionsMarshal.SetCount(_items, snapshot + 1);
        }

        [DoesNotReturn, StackTraceHidden]
        private void ThrowInvalidRestore(int snapshot)
            => throw new InvalidOperationException($"{nameof(JournalSet<T>)} tried to restore snapshot {snapshot} beyond current position {Count}");

        public bool Add(T item)
        {
            // Compatibility: avoid HashSet/Dictionary which can trigger EqualityComparer<T>.Default generic instantiation
            // in restricted runtimes. Use linear scan instead.
            if (!ContainsItem(_items, item))
            {
                _items.Add(item);
                return true;
            }

            return false;
        }

        public void Clear()
        {
            _items.Clear();
        }

        public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public bool Remove(T item) => throw new NotSupportedException("Cannot remove from Journal, use Restore(int snapshot) instead.");
        public int Count => _items.Count;
        public bool IsReadOnly => false;
        void ICollection<T>.Add(T item) => Add(item);
        public bool Contains(T item) => ContainsItem(_items, item);
        public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
    }
}
