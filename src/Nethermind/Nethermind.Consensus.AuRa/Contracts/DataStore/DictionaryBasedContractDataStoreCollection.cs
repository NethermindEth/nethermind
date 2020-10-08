//  Copyright (c) 2018 Demerzel Solutions Limited
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
    public abstract class DictionaryBasedContractDataStoreCollection<T> : IContractDataStoreCollection<T>
    {
        private IDictionary<T, T> _items;

        private IDictionary<T, T> Items => _items ??= CreateDictionary();

        protected abstract IDictionary<T, T> CreateDictionary();

        public void ClearItems()
        {
            Items.Clear();
        }

        public IEnumerable<T> GetSnapshot() => Items.Values.ToArray();

        public void InsertItems(IEnumerable<T> items)
        {
            IDictionary<T,T> dictionary = Items;
            
            foreach (T item in items)
            {
                dictionary[item] = item;
            }
        }

        public bool TryGetValue(T key, out T value) => Items.TryGetValue(key, out value);
    }
}
