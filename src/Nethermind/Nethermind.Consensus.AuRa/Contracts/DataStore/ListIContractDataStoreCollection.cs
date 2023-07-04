// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
