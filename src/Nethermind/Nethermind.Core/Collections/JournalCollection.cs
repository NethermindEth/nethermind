// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace Nethermind.Core.Collections
{
    /// <summary>
    /// <see cref="ICollection{T}"/> of items <see cref="T"/> with ability to store and restore state snapshots.
    /// </summary>
    /// <typeparam name="T">Item type.</typeparam>
    /// <remarks>Due to snapshots <see cref="Remove"/> is not supported.</remarks>
    public class JournalCollection<T> : ICollection<T>, IReadOnlyCollection<T>, IJournal<int>
    {
        private readonly List<T> _list = new();
        public int TakeSnapshot() => Count - 1;

        public void Restore(int snapshot)
        {
            if (snapshot >= Count)
            {
                throw new InvalidOperationException($"{nameof(JournalCollection<T>)} tried to restore snapshot {snapshot} beyond current position {Count}");
            }

            // Just remove excessive items after snapshot
            int index = snapshot + 1;
            _list.RemoveRange(index, Count - index);
        }


        public IEnumerator<T> GetEnumerator() => _list.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)_list).GetEnumerator();
        public void Add(T item) => _list.Add(item);
        public void Clear() => _list.Clear();
        public bool Contains(T item) => _list.Contains(item);
        public void CopyTo(T[] array, int arrayIndex) => _list.CopyTo(array, arrayIndex);
        public bool Remove(T item) => throw new NotSupportedException("Cannot remove from Journal, use Restore(int snapshot) instead.");
        public int Count => _list.Count;
        public bool IsReadOnly => false;
    }
}
