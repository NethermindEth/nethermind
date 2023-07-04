// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
