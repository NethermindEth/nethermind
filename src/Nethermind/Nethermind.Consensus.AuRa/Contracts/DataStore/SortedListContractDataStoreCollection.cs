// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Consensus.AuRa.Contracts.DataStore
{
    public class SortedListContractDataStoreCollection<T> : DictionaryBasedContractDataStoreCollection<T>
    {
        private readonly IComparer<T> _valueComparer;
        private readonly IComparer<T> _keyComparer;

        public SortedListContractDataStoreCollection(IComparer<T> keyComparer = null, IComparer<T> valueComparer = null)
        {
            _valueComparer = valueComparer;
            _keyComparer = keyComparer;
        }

        protected override IDictionary<T, T> CreateDictionary() => new SortedList<T, T>(_keyComparer);

        public override IEnumerable<T> GetSnapshot() => Items.Values.OrderBy(x => x, _valueComparer).ToList();

        protected override bool CanReplace(T replaced, T replacing) =>
            (_valueComparer?.Compare(replacing, replaced) ?? 1) >= 0; // allow replacing only if new value is >= old value
    }
}
