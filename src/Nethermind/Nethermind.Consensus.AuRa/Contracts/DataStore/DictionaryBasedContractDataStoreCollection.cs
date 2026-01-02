// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            IDictionary<T, T> dictionary = Items;

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
            IDictionary<T, T> dictionary = Items;

            foreach (T item in items)
            {
                dictionary.Remove(item);
            }
        }

        public bool TryGetValue(T key, out T value) => Items.TryGetValue(key, out value);
    }
}
