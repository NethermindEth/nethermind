//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Collections;
using System.Collections.Generic;

namespace Nethermind.Core.Collections
{
    /// <summary>
    /// <see cref="ISet{T}"/> of items <see cref="T"/> with ability to store and restore state snapshots.
    /// </summary>
    /// <typeparam name="T">Item type.</typeparam>
    /// <remarks>Due to snapshots <see cref="Remove"/> is not supported.</remarks>
    public class JournalSet<T> : IReadOnlySet<T>, ICollection<T>, IJournal<int>
    {
        private readonly List<T> _items = new();
        private readonly HashSet<T> _set = new();
        public int TakeSnapshot() => Position;

        private int Position => Count - 1;

        public void Restore(int snapshot)
        {
            if (snapshot >= Count)
            {
                throw new InvalidOperationException($"{nameof(JournalCollection<T>)} tried to restore snapshot {snapshot} beyond current position {Count}");
            }

            int current = Position;

            // we use dictionary to remove items added after snapshot
            for (int i = snapshot + 1; i <= current; i++)
            {
                T item = _items[i];
                _set.Remove(item);
            }

            _items.RemoveRange(snapshot + 1, current - snapshot);
        }

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
        public bool IsProperSubsetOf(IEnumerable<T> other) => _set.IsProperSubsetOf(other);
        public bool IsProperSupersetOf(IEnumerable<T> other) => _set.IsProperSupersetOf(other);
        public bool IsSubsetOf(IEnumerable<T> other) => _set.IsSubsetOf(other);
        public bool IsSupersetOf(IEnumerable<T> other) => _set.IsSupersetOf(other);
        public bool Overlaps(IEnumerable<T> other) => _set.Overlaps(other);
        public bool SetEquals(IEnumerable<T> other) => _set.SetEquals(other);
    }
}
