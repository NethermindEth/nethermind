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

using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Consensus.AuRa.Contracts.DataStore
{
    public class HashSetContractDataStoreCollection<T> : IContractDataStoreCollection<T>
    {
        private HashSet<T> _items;

        private ISet<T> Items => _items ??= new HashSet<T>();

        public void Clear()
        {
            Items.Clear();
        }

        public IEnumerable<T> GetSnapshot() => Items.ToHashSet();

        public void Insert(IEnumerable<T> items, bool inFront = false)
        {
            ISet<T> set = Items;
            
            foreach (T item in items)
            {
                set.Add(item);
            }
        }

        public void Remove(IEnumerable<T> items)
        {
            ISet<T> set = Items;
            
            foreach (T item in items)
            {
                set.Remove(item);
            }            
        }
    }
}
