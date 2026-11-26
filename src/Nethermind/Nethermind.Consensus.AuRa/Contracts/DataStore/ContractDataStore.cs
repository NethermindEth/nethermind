// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Contracts;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State;

namespace Nethermind.Consensus.AuRa.Contracts.DataStore
{
    public class ContractDataStore<T> : IDisposable, IContractDataStore<T>
    {
        internal IContractDataStoreCollection<T> Collection { get; }
        private readonly IDataContract<T> _dataContract;
        private readonly IReceiptFinder _receiptFinder;
        private readonly IBlockTree _blockTree;
        private Hash256 _lastHash;
        private Task _latestRefresh = Task.CompletedTask;
        private Block _pendingHead;
        private bool _refreshQueued;
        private readonly Lock _lock = new();
        private readonly Lock _refreshLock = new();
        private readonly ILogger _logger;

        public Task LatestRefreshTask => Volatile.Read(ref _latestRefresh);

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
            // we don't want this to be on main processing thread, and refreshes must run one at a time in head order
            // so that a refresh of an older head never overwrites the data of a newer one.
            // Only the latest head waiting for a refresh is kept: the refreshes of the heads it supersedes would be skipped anyway.
            lock (_refreshLock)
            {
                _pendingHead = e.Block;
                if (_refreshQueued) return;

                _refreshQueued = true;
                Volatile.Write(ref _latestRefresh, _latestRefresh.ContinueWith(_ => RefreshPendingHead(), TaskScheduler.Default));
            }
        }

        private void RefreshPendingHead()
        {
            Block block;
            lock (_refreshLock)
            {
                block = _pendingHead;
                _pendingHead = null;
                _refreshQueued = false;
            }

            try
            {
                Refresh(block);
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Couldn't load contract data from block {block.ToString(Block.Format.FullHashAndNumber)}.", e);
            }
        }

        private void Refresh(Block block)
        {
            // Heads can move faster than the refreshes run, e.g. when blocks are processed in batches while syncing.
            // A superseded head's state may already be pruned, and the newer head's refresh reloads the data anyway.
            if (IsSupersededHead(block)) return;

            try
            {
                GetItemsFromContractAtBlock(block.Header, block.Header.ParentHash == _lastHash, _receiptFinder.Get(block));
            }
            catch (StateUnavailableException e) when (IsSupersededHead(block))
            {
                if (_logger.IsDebug) _logger.Debug($"Skipped loading contract data from superseded head {block.ToString(Block.Format.FullHashAndNumber)}: {e.Message}");
            }
        }

        private bool IsSupersededHead(Block block) => _blockTree.Head is { } head && head.Hash != block.Hash;

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
                catch (ContractException e)
                {
                    if (_logger.IsError) _logger.Error($"Failed to update data from contract on block {blockHeader.ToString(BlockHeader.Format.FullHashAndNumber)} {new StackTrace()}.", e);
                }
            }
        }

        protected void TraceDataChanged(string source)
        {
            if (_logger.IsTrace) _logger.Trace($"{GetType()} changed to {string.Join(", ", Collection.GetSnapshot())} from {source}.");
        }

        protected virtual void RemoveOldContractItemsFromCollection() => Collection.Clear();

        public virtual void Dispose() => _blockTree.NewHeadBlock -= OnNewHead;
    }
}
