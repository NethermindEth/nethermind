// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Consensus.AuRa.Contracts.DataStore
{
    public class DictionaryContractDataStoreCollection<T> : DictionaryBasedContractDataStoreCollection<T>
    {
        private readonly IEqualityComparer<T> _comparer;

        public DictionaryContractDataStoreCollection(IEqualityComparer<T> comparer = null)
        {
            _comparer = comparer;
        }

        protected override IDictionary<T, T> CreateDictionary() => new Dictionary<T, T>(_comparer);
        protected override bool CanReplace(T replaced, T replacing) => true;

    }
}
