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

using System;
using System.Collections.Generic;

namespace Nethermind.Consensus.AuRa.Contracts.DataStore
{
    public class ThreadSafeContractDataStoreCollectionDecorator<T> : IDictionaryContractDataStoreCollection<T>
    {
        private readonly IContractDataStoreCollection<T> _inner;
        private readonly object _lock = new object();

        public ThreadSafeContractDataStoreCollectionDecorator(IContractDataStoreCollection<T> inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public void Clear()
        {
            lock (_lock)
            {
                _inner.Clear();
            }
        }

        public IEnumerable<T> GetSnapshot()
        {
            lock (_lock)
            {
                return _inner.GetSnapshot();
            }
        }

        public void Insert(IEnumerable<T> items, bool inFront = false)
        {
            lock (_lock)
            {
                _inner.Insert(items, inFront);
            }
        }

        public void Remove(IEnumerable<T> items)
        {
            lock (_lock)
            {
                _inner.Remove(items);
            }
        }

        public bool TryGetValue(T key, out T value)
        {
            // ReSharper disable once InconsistentlySynchronizedField
            if (!(_inner is IDictionaryContractDataStoreCollection<T> dictionaryContractDataStoreCollection))
            {
                throw new InvalidOperationException("Inner collection is not dictionary based.");
            }

            lock (_lock)
            {
                return dictionaryContractDataStoreCollection.TryGetValue(key, out value);
            }
        }
    }
}
