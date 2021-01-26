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
