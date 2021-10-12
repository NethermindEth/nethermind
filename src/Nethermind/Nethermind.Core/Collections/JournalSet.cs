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
using System.Linq;
using System.Threading;

namespace Nethermind.Core.Collections
{
    public class JournalSet<T> : IReadOnlySet<T>, ICollection<T>, IJournal<int>
    {
        private readonly Dictionary<int, T> _dictionary = new();
        private readonly HashSet<T> _set = new();
        private SortedRealList<int, SnapshotCollection>? _snapshotCollections;
        private SortedRealList<int, SnapshotCollection> SnapshotCollections => 
            LazyInitializer.EnsureInitialized(ref _snapshotCollections, () => new SortedRealList<int, SnapshotCollection>());
        
        public int TakeSnapshot() => Position;
        public (int, ICollection<T>) TakeSnapshotWithCollection()
        {
            int snapshot = Position;
            KeyValuePair<int, SnapshotCollection>? lastCollection = SnapshotCollections.Count > 0 ? SnapshotCollections[^1] : null;
            
            if (lastCollection?.Key != snapshot)
            {
                SnapshotCollection collection = new(this);
                SnapshotCollections.Add(snapshot, collection);
                return (snapshot, collection);
            }

            return (snapshot, lastCollection.Value.Value);
        }
        
        public void DropSnapshot(int snapshot) => SnapshotCollections.Remove(snapshot);

        private int Position => Count - 1;

        public void Restore(int snapshot)
        {
            if (snapshot >= Count)
            {
                throw new InvalidOperationException($"{nameof(JournalCollection<T>)} tried to restore snapshot {snapshot} beyond current position {Count}");
            }

            int index = snapshot + 1;
            for (int i = Position; i >= index; i--)
            {
                T item = _dictionary[i];
                _dictionary.Remove(i);
                _set.Remove(item);
            }

            if (_snapshotCollections is not null)
            {
                foreach (int snapshotTaken in _snapshotCollections.Keys.ToArray())
                {
                    if (snapshotTaken >= snapshot)
                    {
                        _snapshotCollections.Remove(snapshotTaken);
                    }
                }
            }
        }

        public bool Add(T item)
        {
            if (_set.Add(item))
            {
                _dictionary.Add(Position, item);
                return true;
            }
            
            if (_snapshotCollections is not null)
            {
                SnapshotCollection snapshotCollection = _snapshotCollections[^1].Value;
                snapshotCollection.AddInSnapshot(item);
            }

            return false;
        }

        public void Clear()
        {
            _dictionary.Clear();
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

        private class SnapshotCollection : ICollection<T>, IReadOnlyCollection<T>
        {
            private readonly JournalSet<T> _journalSet;
            private readonly HashSet<T> _snapshotCollection = new();

            public SnapshotCollection(JournalSet<T> journalSet)
            {
                _journalSet = journalSet;
            }

            public IEnumerator<T> GetEnumerator() => _snapshotCollection.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            public void Add(T item) => _journalSet.Add(item);
            public void AddInSnapshot(T item) => _snapshotCollection.Add(item);
            public void Clear() { }
            public bool Contains(T item) => _snapshotCollection.Contains(item);
            public void CopyTo(T[] array, int arrayIndex) => _snapshotCollection.CopyTo(array, arrayIndex);
            public bool Remove(T item) => throw new NotSupportedException("Cannot remove from Journal, use Restore(int snapshot) instead.");
            public int Count => _snapshotCollection.Count;
            public bool IsReadOnly => false;
        }
    }
}
