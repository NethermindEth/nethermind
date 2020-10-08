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
using System.Runtime.CompilerServices;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.AuRa.Contracts.DataStore
{
    public class ContractDataStore<T, TCollection> : IDisposable, IContractDataStore<T> where TCollection : IContractDataStoreCollection<T>
    {
        internal TCollection Collection { get; }
        private readonly IDataContract<T> _dataContract;
        private readonly IBlockProcessor _blockProcessor;
        private Keccak _lastHash;

        protected internal ContractDataStore(
            TCollection collection,
            IDataContract<T> dataContract,
            IBlockProcessor blockProcessor)
        {
            Collection = collection;
            _dataContract = dataContract ?? throw new ArgumentNullException(nameof(dataContract));
            _blockProcessor = blockProcessor ?? throw new ArgumentNullException(nameof(blockProcessor));;
            _blockProcessor.BlockProcessed += OnBlockProcessed;
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public IEnumerable<T> GetItemsFromContractAtBlock(BlockHeader blockHeader)
        {
            GetItemsFromContractAtBlock(blockHeader, blockHeader.Hash == _lastHash);
            return Collection.GetSnapshot();
        }
        
        
        [MethodImpl(MethodImplOptions.Synchronized)]
        private void OnBlockProcessed(object sender, BlockProcessedEventArgs e)
        {
            BlockHeader header = e.Block.Header;
            GetItemsFromContractAtBlock(header, header.ParentHash == _lastHash, e.TxReceipts);
        }
        
        private void GetItemsFromContractAtBlock(BlockHeader blockHeader, bool isConsecutiveBlock, TxReceipt[] receipts = null)
        {
            bool fromReceipts = receipts != null;

            if (fromReceipts || !isConsecutiveBlock)
            {
                bool incrementalChanges = _dataContract.IncrementalChanges;
                bool canGetFullStateFromReceipts = fromReceipts && (isConsecutiveBlock || !incrementalChanges) && !blockHeader.IsGenesis;

                IEnumerable<T> items = canGetFullStateFromReceipts
                    ? _dataContract.GetItemsChangedFromBlock(blockHeader, receipts)
                    : _dataContract.GetAllItemsFromBlock(blockHeader);

                if (!fromReceipts || !isConsecutiveBlock || !incrementalChanges)
                {
                    Collection.ClearItems();
                }

                Collection.InsertItems(items);

                _lastHash = blockHeader.Hash;
            }
        }

        public void Dispose()
        {
            _blockProcessor.BlockProcessed -= OnBlockProcessed;
        }
    }
    
    public class ContractDataStore<T> : ContractDataStore<T, IContractDataStoreCollection<T>>
    {
        public ContractDataStore(
            IContractDataStoreCollection<T> collection, 
            IDataContract<T> dataContract, 
            IBlockProcessor blockProcessor) 
            : base(collection, dataContract, blockProcessor)
        {
        }
    }
}
