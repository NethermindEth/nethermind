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
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public abstract class ContractDataStore<T, TCollection> : IDisposable, IContractDataStore<T>
    {
        private readonly IDataContract<T> _dataContract;
        private readonly IBlockProcessor _blockProcessor;
        private Keccak _lastHash;
        
        protected TCollection Items { get; private set; }
        
        protected ContractDataStore(IDataContract<T> dataContract, IBlockProcessor blockProcessor)
        {
            _dataContract = dataContract ?? throw new ArgumentNullException(nameof(dataContract));
            _blockProcessor = blockProcessor ?? throw new ArgumentNullException(nameof(blockProcessor));;
            _blockProcessor.BlockProcessed += OnBlockProcessed;
        }

        public IEnumerable<T> GetItemsFromContractAtBlock(BlockHeader parent)
        {
            lock (Items)
            {
                GetItemsFromContractAtBlock(parent, parent.Hash == _lastHash);
                return GetItemsFromContractAtBlock(Items);
            }
        }

        private void OnBlockProcessed(object sender, BlockProcessedEventArgs e)
        {
            lock (Items)
            {
                BlockHeader header = e.Block.Header;
                GetItemsFromContractAtBlock(header, header.ParentHash == _lastHash, e.TxReceipts);
            }
        }
        
        private void GetItemsFromContractAtBlock(BlockHeader blockHeader, bool isConsecutiveBlock, TxReceipt[] receipts = null)
        {
            Items ??= CreateItems();
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
                    ClearItems(Items);
                }

                InsertItems(Items, items);

                _lastHash = blockHeader.Hash;
            }
        }

        public void Dispose()
        {
            _blockProcessor.BlockProcessed -= OnBlockProcessed;
        }
        
        protected abstract IEnumerable<T> GetItemsFromContractAtBlock(TCollection collection);
        
        protected abstract TCollection CreateItems();
        
        protected abstract void ClearItems(TCollection collection);
        
        protected abstract void InsertItems(TCollection collection, IEnumerable<T> items);
    }
}
