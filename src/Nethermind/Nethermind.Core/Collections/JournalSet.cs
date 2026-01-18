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
#if ZKVM
        // NativeAOT/ZKVM: avoid HashSet construction (can trigger EqualityComparer<T>.Default / generic type construction).
        // This is used for tracking/journaling; O(n) behavior is acceptable for ZKVM builds.
        private readonly List<T> _set = [];
#else
        private readonly HashSet<T> _set = [];
#endif
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
#if ZKVM
                _set.Remove(item);
#else
                _set.Remove(item);
#endif
            }

            CollectionsMarshal.SetCount(_items, snapshot + 1);
        }

        [DoesNotReturn, StackTraceHidden]
        private void ThrowInvalidRestore(int snapshot)
            => throw new InvalidOperationException($"{nameof(JournalSet<T>)} tried to restore snapshot {snapshot} beyond current position {Count}");

        public bool Add(T item)
        {
#if ZKVM
            if (ContainsZkvm(item))
            {
                return false;
            }

            _set.Add(item);
            _items.Add(item);
            return true;
#else
            if (_set.Add(item))
            {
                // we use dictionary in order to track item positions
                _items.Add(item);
                return true;
            }

            return false;
#endif
        }

        public void Clear()
        {
            _items.Clear();
#if ZKVM
            _set.Clear();
#else
            _set.Clear();
#endif
        }

#if ZKVM
        public List<T>.Enumerator GetEnumerator() => _set.GetEnumerator();
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => _set.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => _set.GetEnumerator();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool ContainsZkvm(T item)
        {
            // Avoid List<T>.Contains which pulls in EqualityComparer<T>.Default and may trigger generic construction in NativeAOT.
            // Use a simple linear scan and reference equality when possible.
            ReadOnlySpan<T> span = CollectionsMarshal.AsSpan(_set);
            if (!typeof(T).IsValueType)
            {
                object? target = item;
                for (int i = 0; i < span.Length; i++)
                {
                    if (ReferenceEquals(span[i], target))
                    {
                        return true;
                    }
                }

                return false;
            }

            // Value types: fall back to EqualityComparer only when needed.
            EqualityComparer<T> comparer = EqualityComparer<T>.Default;
            for (int i = 0; i < span.Length; i++)
            {
                if (comparer.Equals(span[i], item))
                {
                    return true;
                }
            }

            return false;
        }
#else
        public HashSet<T>.Enumerator GetEnumerator() => _set.GetEnumerator();
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
#endif
        public bool Remove(T item) => throw new NotSupportedException("Cannot remove from Journal, use Restore(int snapshot) instead.");
#if ZKVM
        public int Count => _set.Count;
#else
        public int Count => _set.Count;
#endif
        public bool IsReadOnly => false;
        void ICollection<T>.Add(T item) => Add(item);
#if ZKVM
        public bool Contains(T item) => ContainsZkvm(item);
        public void CopyTo(T[] array, int arrayIndex) => _set.CopyTo(array, arrayIndex);
#else
        public bool Contains(T item) => _set.Contains(item);
        public void CopyTo(T[] array, int arrayIndex) => _set.CopyTo(array, arrayIndex);
#endif
    }
}
