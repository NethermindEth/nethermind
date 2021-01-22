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
    public class ListIContractDataStoreCollection<T> : IContractDataStoreCollection<T>
    {
        private List<T> _items;

        private List<T> Items => _items ??= new List<T>();
        
        public void Clear()
        {
            Items.Clear();
        }

        public IEnumerable<T> GetSnapshot() => Items.ToList();

        public void Insert(IEnumerable<T> items, bool inFront = false)
        {
            if (inFront)
            {
                Items.InsertRange(0, items);
            }
            else
            {
                Items.AddRange(items);
            }
        }

        public void Remove(IEnumerable<T> items)
        {
            ISet<T> set = items.ToHashSet();
            Items.RemoveAll(i => set.Contains(i));
        }
    }
}
