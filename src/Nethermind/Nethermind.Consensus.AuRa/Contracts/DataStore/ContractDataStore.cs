// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.Consensus.AuRa.Contracts.DataStore
{
    public class ContractDataStore<T> : IDisposable, IContractDataStore<T>
    {
        internal IContractDataStoreCollection<T> Collection { get; }
        private readonly IDataContract<T> _dataContract;
        private readonly IReceiptFinder _receiptFinder;
        private readonly IBlockTree _blockTree;
        private Keccak _lastHash;
        private readonly object _lock = new object();
        private readonly ILogger _logger;

        protected internal ContractDataStore(IContractDataStoreCollection<T> collection, IDataContract<T> dataContract, IBlockTree blockTree, IReceiptFinder receiptFinder, ILogManager logManager)
        {
            Collection = collection is null || collection is ThreadSafeContractDataStoreCollectionDecorator<T> ? collection : new ThreadSafeContractDataStoreCollectionDecorator<T>(collection);
            _dataContract = dataContract ?? throw new ArgumentNullException(nameof(dataContract));
            _receiptFinder = receiptFinder ?? throw new ArgumentNullException(nameof(receiptFinder));
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _logger = logManager?.GetClassLogger<ContractDataStore<T>>() ?? throw new ArgumentNullException(nameof(logManager));
            blockTree.NewHeadBlock += OnNewHead;
        }

        public IEnumerable<T> GetItemsFromContractAtBlock(BlockHeader blockHeader)
        {
            GetItemsFromContractAtBlock(blockHeader, blockHeader.Hash == _lastHash);
            return Collection.GetSnapshot();
        }

        private void OnNewHead(object sender, BlockEventArgs e)
        {
            // we don't want this to be on main processing thread
            Task.Run(() => Refresh(e.Block))
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        if (_logger.IsError) _logger.Error($"Couldn't load contract data from block {e.Block.ToString(Block.Format.FullHashAndNumber)}.", t.Exception);
                    }
                });
        }

        private void Refresh(Block block)
        {
            GetItemsFromContractAtBlock(block.Header, block.Header.ParentHash == _lastHash, _receiptFinder.Get(block));
        }

        private void GetItemsFromContractAtBlock(BlockHeader blockHeader, bool isConsecutiveBlock, TxReceipt[] receipts = null)
        {
            bool fromReceipts = receipts is not null;
            if (fromReceipts || !isConsecutiveBlock)
            {
                bool incrementalChanges = _dataContract.IncrementalChanges;
                bool canGetFullStateFromReceipts = fromReceipts && (isConsecutiveBlock || !incrementalChanges);

                try
                {
                    bool dataChanged = true;
                    IEnumerable<T> items;

                    lock (_lock)
                    {
                        if (canGetFullStateFromReceipts)
                        {
                            dataChanged = _dataContract.TryGetItemsChangedFromBlock(blockHeader, receipts, out items);

                            if (!dataChanged && !isConsecutiveBlock)
                            {
                                items = _dataContract.GetAllItemsFromBlock(blockHeader);
                                dataChanged = true;
                            }
                        }
                        else
                        {
                            items = _dataContract.GetAllItemsFromBlock(blockHeader);
                        }
                    }

                    if (dataChanged)
                    {
                        if (!fromReceipts || !isConsecutiveBlock || !incrementalChanges)
                        {
                            RemoveOldContractItemsFromCollection();
                        }

                        Collection.Insert(items);
                        TraceDataChanged("contract");
                    }

                    _lastHash = blockHeader.Hash;

                    if (_logger.IsTrace) _logger.Trace($"{GetType()} trying to {nameof(GetItemsFromContractAtBlock)} with params " +
                                                       $"{nameof(canGetFullStateFromReceipts)}:{canGetFullStateFromReceipts}, " +
                                                       $"{nameof(fromReceipts)}:{fromReceipts}, " +
                                                       $"{nameof(isConsecutiveBlock)}:{isConsecutiveBlock}, " +
                                                       $"{nameof(incrementalChanges)}:{incrementalChanges}, " +
                                                       $"{nameof(dataChanged)}:{dataChanged}. " +
                                                       $"Results in {string.Join(", ", Collection.GetSnapshot())}. " +
                                                       $"On {blockHeader.ToString(BlockHeader.Format.FullHashAndNumber)}. " +
                                                       $"From {new StackTrace()}");
                }
                catch (AbiException e)
                {
                    if (_logger.IsError) _logger.Error($"Failed to update data from contract on block {blockHeader.ToString(BlockHeader.Format.FullHashAndNumber)} {new StackTrace()}.", e);
                }
            }
        }

        protected void TraceDataChanged(string source)
        {
            if (_logger.IsTrace) _logger.Trace($"{GetType()} changed to {string.Join(", ", Collection.GetSnapshot())} from {source}.");
        }

        protected virtual void RemoveOldContractItemsFromCollection()
        {
            Collection.Clear();
        }

        public virtual void Dispose()
        {
            _blockTree.NewHeadBlock -= OnNewHead;
        }
    }
}
