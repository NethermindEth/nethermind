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
using System.Threading;

namespace Nethermind.Core.Collections
{
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
