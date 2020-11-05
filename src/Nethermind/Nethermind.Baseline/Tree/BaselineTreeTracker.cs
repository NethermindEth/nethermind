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

namespace Nethermind.Baseline.Tree
{
    public sealed class BaselineTreeTracker : IDisposable
    {
        private readonly Address _address;
        private readonly IBaselineTreeHelper _baselineTreeHelper;
        private readonly IBlockProcessor _blockProcessor;
        private readonly IBlockFinder _blockFinder;
        private BaselineTree _baselineTree;
        private BlockHeader? _currentBlockHeader;

        public BaselineTreeTracker(
            Address address,
            BaselineTree baselineTree, 
            IBlockProcessor blockProcessor,
            IBaselineTreeHelper baselineTreeHelper,
            IBlockFinder blockFinder)
        {
            _address = address ?? throw new ArgumentNullException(nameof(address));
            _baselineTree = baselineTree ?? throw new ArgumentNullException(nameof(baselineTree));
            _blockProcessor = blockProcessor ?? throw new ArgumentNullException(nameof(blockProcessor));
            _baselineTreeHelper = baselineTreeHelper ?? throw new ArgumentNullException(nameof(baselineTreeHelper));
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));

            StartTracking();
        }

        private void StartTracking()
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

            var toBlockParameter = BlockParameter.Latest;
            _currentBlockHeader = _blockFinder.SearchForHeader(null).Object;
            _baselineTreeHelper.BuildTree(_baselineTree, _address, fromBlockParameter, toBlockParameter);
            _blockProcessor.BlockProcessed += OnBlockProcessed;
        }

        private void OnBlockProcessed(object? sender, BlockProcessedEventArgs e)
        {
            if (_currentBlockHeader != null && _currentBlockHeader.Hash != e.Block.ParentHash)
            {
                if (_currentBlockHeader.Number < e.Block.Number)
                {
                    _baselineTreeHelper.BuildTree(_baselineTree, _address, new BlockParameter(_currentBlockHeader.Hash), new BlockParameter(e.Block.Hash));
                }
                else
                {
                    Reorganize(e.TxReceipts, e.Block.Number);
                }

                _currentBlockHeader = e.Block.Header;
                return;
            }

            _currentBlockHeader = e.Block.Header;
            AddFromCurrentBlock(e.TxReceipts, e.Block.Number);
        }

        private void AddFromCurrentBlock(TxReceipt[] txReceipts, long newBlockNumber)
        {
            uint count = 0;
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
                    _baselineTree.Insert(leafHash);
                    ++count;
                }
                else
                {
                    for (int i = 0; i < (filterLog.Data.Length - 128) / 32; i++)
                    {
                        Keccak leafHash = new Keccak(filterLog.Data.Slice(128 + 32 * i, 32).ToArray());
                        _baselineTree.Insert(leafHash);
                        ++count;
                    }
                }
            }

            if (count != 0)
            {
                var previousBlockWithLeaves = _baselineTree.LastBlockWithLeaves;
                _baselineTree.SaveBlockNumberCount(newBlockNumber, count, previousBlockWithLeaves);
                _baselineTree.LastBlockWithLeaves = newBlockNumber;
            }
        }

        private void Reorganize(TxReceipt[] txReceipts, long newBlockNumber)
        {
            var leavesToReorganize = _baselineTree.GetLeavesCountFromNextBlocks(newBlockNumber, true);
            var calculatingHashesStart = _baselineTree.Count - leavesToReorganize;
            _baselineTree.Delete(leavesToReorganize, false);

            AddFromCurrentBlock(txReceipts, newBlockNumber);
            _baselineTree.CalculateHashes(calculatingHashesStart);
        }

        public void Dispose()
        {
            _baselineTree.LastBlockDbHash = _currentBlockHeader!.Hash;
            _baselineTree.SaveCurrentBlockInDb();
            _blockProcessor.BlockProcessed -= OnBlockProcessed;
        }
    }
}
