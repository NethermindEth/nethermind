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
using Nethermind.Blockchain;
using Nethermind.Blockchain.Data;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Consensus.AuRa.Contracts.DataStore
{
    public class DictionaryContractDataStore<T, TCollection> : IDictionaryContractDataStore<T>, IDisposable where TCollection : DictionaryBasedContractDataStoreCollection<T>
    {
        public ContractDataStore<T> ContractDataStore { get; private set; }

        public DictionaryContractDataStore(
            TCollection collection,
            IDataContract<T> dataContract,
            IBlockTree blockTree, 
            IReceiptFinder receiptFinder,
            ILogManager logManager)
            : this(CreateContractDataStore(collection, dataContract, blockTree, receiptFinder, logManager))
        {
        }

        public DictionaryContractDataStore(
            TCollection collection,
            IDataContract<T> dataContract,
            IBlockTree blockTree, 
            IReceiptFinder receiptFinder,
            ILogManager logManager,
            ILocalDataSource<IEnumerable<T>> localDataSource)
            : this(localDataSource == null 
                ? CreateContractDataStore(collection, dataContract, blockTree, receiptFinder, logManager) 
                : CreateContractDataStoreWithLocalData(collection, dataContract, blockTree, receiptFinder, logManager, localDataSource))
        {
        }
        
        private static ContractDataStore<T> CreateContractDataStore(
            TCollection collection, 
            IDataContract<T> dataContract, 
            IBlockTree blockTree, 
            IReceiptFinder receiptFinder,
            ILogManager logManager) => 
            new ContractDataStore<T>(collection, dataContract, blockTree, receiptFinder, logManager);

        private static ContractDataStoreWithLocalData<T> CreateContractDataStoreWithLocalData(
            TCollection collection, 
            IDataContract<T> dataContract, 
            IBlockTree blockTree, 
            IReceiptFinder receiptFinder,
            ILogManager logManager,
            ILocalDataSource<IEnumerable<T>> localDataSource) => 
            new ContractDataStoreWithLocalData<T>(
                collection, 
                dataContract ?? new EmptyDataContract<T>(), 
                blockTree,
                receiptFinder, 
                logManager, 
                localDataSource);

        private DictionaryContractDataStore(ContractDataStore<T> contractDataStore)
        {
            ContractDataStore = contractDataStore;
        }

        public bool TryGetValue(BlockHeader header, T key, out T value)
        {
            GetItemsFromContractAtBlock(header);
            TCollection collection = ((TCollection)(ContractDataStore.Collection));
            return collection.TryGetValue(key, out value);
        }

        public IEnumerable<T> GetItemsFromContractAtBlock(BlockHeader blockHeader) => ContractDataStore.GetItemsFromContractAtBlock(blockHeader);

        public void Dispose()
        {
            ContractDataStore?.Dispose();
        }
    }
}
