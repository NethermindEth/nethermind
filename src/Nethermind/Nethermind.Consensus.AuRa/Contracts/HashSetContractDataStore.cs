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
using Nethermind.Blockchain.Processing;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public class HashSetContractDataStore<T> : ContractDataStore<T, HashSet<T>>
    {
        public HashSetContractDataStore(IDataContract<T> dataContract, IBlockProcessor blockProcessor)
            : base(dataContract, blockProcessor)
        {
        }

        protected override HashSet<T> CreateItems() => new HashSet<T>();

        protected override void ClearItems(HashSet<T> collection)
        {
            collection.Clear();
        }

        protected override IEnumerable<T> GetItems(HashSet<T> collection) => collection;

        protected override void InsertItems(HashSet<T> collection, IEnumerable<T> items)
        {
            foreach (T item in items)
            {
                collection.Add(item);
            }
        }
    }
}
