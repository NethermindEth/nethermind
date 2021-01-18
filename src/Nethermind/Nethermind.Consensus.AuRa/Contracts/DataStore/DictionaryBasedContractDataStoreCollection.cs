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
    public abstract class DictionaryBasedContractDataStoreCollection<T> : IDictionaryContractDataStoreCollection<T>
    {
        private IDictionary<T, T> _items;

        protected IDictionary<T, T> Items => _items ??= CreateDictionary();

        protected abstract IDictionary<T, T> CreateDictionary();

        public void Clear()
        {
            Items.Clear();
        }

        public virtual IEnumerable<T> GetSnapshot() => Items.Values.ToArray();

        public void Insert(IEnumerable<T> items, bool inFront = false)
        {
            IDictionary<T,T> dictionary = Items;

            foreach (T item in items)
            {
                bool keyAlreadyPresent = dictionary.TryGetValue(item, out T value);
                if (!keyAlreadyPresent || CanReplace(item, value))
                {
                    dictionary[item] = item;
                }
            }
        }

        protected abstract bool CanReplace(T replaced, T replacing);

        public void Remove(IEnumerable<T> items)
        {
            IDictionary<T,T> dictionary = Items;
            
            foreach (T item in items)
            {
                dictionary.Remove(item);
            }
        }

        public bool TryGetValue(T key, out T value) => Items.TryGetValue(key, out value);
    }
}
