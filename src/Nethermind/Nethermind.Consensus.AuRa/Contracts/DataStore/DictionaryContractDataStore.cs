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
using Nethermind.Blockchain.Processing;
using Nethermind.Core;

namespace Nethermind.Consensus.AuRa.Contracts.DataStore
{
    public class DictionaryContractDataStore<T> : IDictionaryContractDataStore<T>, IDisposable
    {
        private readonly ContractDataStore<T, DictionaryBasedContractDataStoreCollection<T>> _contractDataStore;

        public DictionaryContractDataStore(
            DictionaryBasedContractDataStoreCollection<T> collection,
            IDataContract<T> dataContract,
            IBlockProcessor blockProcessor)
            : this(new ContractDataStore<T, DictionaryBasedContractDataStoreCollection<T>>(collection, dataContract, blockProcessor))
        {
        }

        private DictionaryContractDataStore(ContractDataStore<T, DictionaryBasedContractDataStoreCollection<T>> contractDataStore)
        {
            _contractDataStore = contractDataStore;
        }

        public bool TryGetValue(BlockHeader header, T key, out T value)
        {
            GetItemsFromContractAtBlock(header);
            return _contractDataStore.Collection.TryGetValue(key, out value);
        }

        public IEnumerable<T> GetItemsFromContractAtBlock(BlockHeader blockHeader) => _contractDataStore.GetItemsFromContractAtBlock(blockHeader);

        public void Dispose()
        {
            _contractDataStore?.Dispose();
        }
    }
}
