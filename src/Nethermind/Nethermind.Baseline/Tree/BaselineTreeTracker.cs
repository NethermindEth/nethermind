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

using System;
using System.Linq;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;

namespace Nethermind.Baseline.Tree
{
    public sealed class BaselineTreeTracker : IDisposable
    {
        private readonly Address _address;
        private readonly IBaselineTreeHelper _baselineTreeHelper;
        private readonly IBlockProcessor _blockProcessor;
        private readonly IBlockFinder _blockFinder;
        private readonly ILogger _logger;
        private BaselineTree _baselineTree;
        private BlockHeader? _currentBlockHeader;

        public BaselineTreeTracker(
            Address address,
            BaselineTree baselineTree,
            IBlockProcessor blockProcessor,
            IBaselineTreeHelper baselineTreeHelper,
            IBlockFinder blockFinder,
            ILogger logger)
        {
            _address = address ?? throw new ArgumentNullException(nameof(address));
            _baselineTree = baselineTree ?? throw new ArgumentNullException(nameof(baselineTree));
            _blockProcessor = blockProcessor ?? throw new ArgumentNullException(nameof(blockProcessor));
            _baselineTreeHelper = baselineTreeHelper ?? throw new ArgumentNullException(nameof(baselineTreeHelper));
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            StartTracking();
        }

        public void StartTracking()
        {
            BlockParameter fromBlockParameter = new BlockParameter(0L);
            if (_baselineTree.LastBlockDbHash != Keccak.Zero)
            {
                var lastBaselineTreeParameter = new BlockParameter(_baselineTree.LastBlockDbHash);
                var searchResult = _blockFinder.SearchForHeader(lastBaselineTreeParameter);
                if (!searchResult.IsError)
                {
                    fromBlockParameter = lastBaselineTreeParameter;
                }
            }

            Keccak hashBeforeBuildingTree = BlockParameter.Latest.BlockHash;
            _baselineTreeHelper.BuildTree(_baselineTree, _address, fromBlockParameter, BlockParameter.Latest);
            Keccak latestHash = BlockParameter.Latest.BlockHash;
            if (latestHash != null && hashBeforeBuildingTree != latestHash)
            {
                // TODO: this is not covered by any test
                var startParameter = hashBeforeBuildingTree == null ? new BlockParameter(0L) : new BlockParameter(hashBeforeBuildingTree);
                _baselineTreeHelper.BuildTree(_baselineTree, _address, startParameter, new BlockParameter(latestHash));
            }

            var headerSearch = latestHash == null ? null : new BlockParameter(latestHash);
            _currentBlockHeader = _blockFinder.SearchForHeader(headerSearch).Object;

            if (_baselineTree.LastBlockWithLeaves != _currentBlockHeader.Number)
            {
                _baselineTree.MemorizeCurrentCount(_currentBlockHeader.Hash, _currentBlockHeader.Number, _baselineTree.Count);
            }

            _blockProcessor.BlockProcessed += OnBlockProcessed;
        }

        private void OnBlockProcessed(object? sender, BlockProcessedEventArgs e)
        {
            if(_logger.IsWarn) _logger.Warn($"Tree tracker for {_baselineTree} processing block {e.Block.ToString(Block.Format.Short)}");
            
            if (_currentBlockHeader != null && _currentBlockHeader.Hash != e.Block.ParentHash && _currentBlockHeader.Number < e.Block.Number)
            {
                // what is this - not covered by any test?
                // why do we build tree here?
                _baselineTreeHelper.BuildTree(_baselineTree, _address, new BlockParameter(_currentBlockHeader.Hash), new BlockParameter(e.Block.Hash));
                _currentBlockHeader = e.Block.Header;
                
                // TODO: why this is here
                _baselineTree.MemorizeCurrentCount(_baselineTree.LastBlockDbHash, _baselineTree.LastBlockWithLeaves, _baselineTree.Count);
                return;
            }

            uint removedItemsCount = 0;
            bool reorganized = _currentBlockHeader != null && _currentBlockHeader.Hash != e.Block.ParentHash; 
            if (reorganized)
            {
                if(_logger.IsWarn) _logger.Warn(
                    $"Tree tracker for {_baselineTree} reorganizes from branching point at {e.Block.ToString(Block.Format.Short)}");
                removedItemsCount = Revert(e.Block.Number);
            }

            _currentBlockHeader = e.Block.Header;
            uint treeStartingCount = _baselineTree.Count;
            uint newLeavesCount = AddFromCurrentBlock(e.TxReceipts);
            _baselineTree.CalculateHashes(treeStartingCount - removedItemsCount);
            if (newLeavesCount != 0 || removedItemsCount != 0 || reorganized)
            {
                uint currentTreeCount = treeStartingCount + newLeavesCount - removedItemsCount;
                _baselineTree.MemorizeCurrentCount(e.Block.Hash, e.Block.Number, currentTreeCount);
            }
        }

        private uint AddFromCurrentBlock(TxReceipt[] txReceipts)
        {
            uint newLeavesCount = 0;
            LogFilter insertLeavesFilter = new LogFilter(
                    0,
                    new BlockParameter(0L),
                    new BlockParameter(0L),
                    new AddressFilter(_address),
                    new AnyTopicsFilter(new SpecificTopic(BaselineModule.LeavesTopic), new SpecificTopic(BaselineModule.LeafTopic)));
            var logs = _currentBlockHeader.FindLogs(txReceipts, insertLeavesFilter, FindOrder.Ascending, FindOrder.Ascending);
            foreach (var filterLog in logs)
            {
                if (filterLog.Data.Length == 96)
                {
                    Keccak leafHash = new Keccak(filterLog.Data.Slice(32, 32).ToArray());
                    _baselineTree.Insert(leafHash, false);
                    ++newLeavesCount;
                }
                else
                {
                    for (int i = 0; i < (filterLog.Data.Length - 128) / 32; i++)
                    {
                        Keccak leafHash = new Keccak(filterLog.Data.Slice(128 + 32 * i, 32).ToArray());
                        _baselineTree.Insert(leafHash, false);
                        ++newLeavesCount;
                    }
                }
            }

            return newLeavesCount;
        }

        private uint Revert(long number)
        {
            // we go to the position 1 before the earliest of the blocks that we want to revert
            var deletedLeavesCount = _baselineTree.GoBackTo(Math.Max(0, number - 1));
            return deletedLeavesCount;
        }

        public void StopTracking()
        {
            _blockProcessor.BlockProcessed -= OnBlockProcessed;
        }

        public void Dispose()
        {
            StopTracking();
        }
    }
}
