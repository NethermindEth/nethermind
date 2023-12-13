// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
            if (_inner is not IDictionaryContractDataStoreCollection<T> dictionaryContractDataStoreCollection)
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
