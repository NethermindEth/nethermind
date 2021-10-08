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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.Baseline.Tree
{
    public class BaselineTreeHelper : IBaselineTreeHelper
    {
        private readonly ILogFinder _logFinder;
        private readonly IDb _mainDb;
        private readonly IDb _metadataBaselineDb;
        private readonly ILogger _logger;

        public BaselineTreeHelper(ILogFinder logFinder, IDb mainDb, IDb metadataBaselineDb, ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _logFinder = logFinder ?? throw new ArgumentNullException(nameof(logFinder));
            _mainDb = mainDb ?? throw new ArgumentNullException(nameof(mainDb));
            _metadataBaselineDb = metadataBaselineDb ?? throw new ArgumentNullException(nameof(metadataBaselineDb));
        }

        public BaselineTreeNode[] GetHistoricalLeaves(BaselineTree tree, uint[] leafIndexes, long blockNumber)
        {
            if(_logger.IsWarn) _logger.Warn(
                $"Retrieving historical leaves of {tree} with index {string.Join(", ", leafIndexes)} for block {blockNumber}");
            
            var historicalCount = tree.GetBlockCount(blockNumber);
            BaselineTreeNode[] leaves = new BaselineTreeNode[leafIndexes.Length];

            for (int i = 0; i < leafIndexes.Length; i++)
            {
                var leafIndex = leafIndexes[i];
                if (historicalCount <= leafIndex)
                {
                    leaves[i] = new BaselineTreeNode(Keccak.Zero, leafIndex);
                }
                else
                {
                    leaves[i] = tree.GetLeaf(leafIndex);
                }
            }

            return leaves;
        }

        public BaselineTreeNode GetHistoricalLeaf(BaselineTree tree, uint leafIndex, long blockNumber)
        {
            if(_logger.IsWarn) _logger.Warn($"Retrieving historical leaf of {tree} with index {leafIndex} for block {blockNumber}");
            
            var historicalCount = tree.GetBlockCount(blockNumber);
            if (historicalCount <= leafIndex)
            {
                return new BaselineTreeNode(Keccak.Zero, leafIndex);
            }

            return tree.GetLeaf(leafIndex);
        }

        public BaselineTree CreateHistoricalTree(Address address, long blockNumber)
        {
            if(_logger.IsWarn) _logger.Warn($"Building historical tree at {address} for block {blockNumber}");
            var readOnlyMain = new ReadOnlyDb(_mainDb, true);
            var readOnlyMetadata = new ReadOnlyDb(_metadataBaselineDb, true);
            var historicalTree = new ShaBaselineTree(readOnlyMain, readOnlyMetadata, address.Bytes, BaselineModule.TruncationLength, _logger);
            var endIndex = historicalTree.Count;
            var historicalCount = historicalTree.GetBlockCount(blockNumber);
            if(_logger.IsWarn) _logger.Warn($"Historical count of {historicalTree} for block {blockNumber} is {historicalCount}");

            if (endIndex - historicalCount > 0)
            {
                if(_logger.IsWarn) _logger.Warn($"Deleting {endIndex - historicalCount} from {historicalTree}");
                historicalTree.Delete(endIndex - historicalCount, false);
                historicalTree.CalculateHashes(historicalCount, endIndex);
                if(_logger.IsWarn) _logger.Warn($"After deleting from {historicalTree} root is {historicalTree.Root}");
            }

            return historicalTree;
        }

        public BaselineTree RebuildEntireTree(Address treeAddress, Keccak blockHash)
        {
            if(_logger.IsWarn) _logger.Warn($"Rebuilding entire tree from {treeAddress} at {blockHash}");
            
            BaselineTree baselineTree = new ShaBaselineTree(_mainDb, _metadataBaselineDb, treeAddress.Bytes, BaselineModule.TruncationLength, _logger);
            var trie = BuildTree(baselineTree, treeAddress, new BlockParameter(0L), new BlockParameter(blockHash));
            return trie;
        }

        public BaselineTree BuildTree(BaselineTree baselineTree, Address treeAddress, BlockParameter blockFrom, BlockParameter blockTo)
        {
            if(_logger.IsWarn) _logger.Warn($"Build {baselineTree} from {blockFrom} to {blockTo}");
            
            var initCount = baselineTree.Count;
            LogFilter insertLeavesFilter = new LogFilter(
                0,
                blockFrom,
                blockTo,
                new AddressFilter(treeAddress),
                new SequenceTopicsFilter(new SpecificTopic(BaselineModule.LeavesTopic)));

            LogFilter insertLeafFilter = new LogFilter(
                0,
                blockFrom,
                blockTo,
                new AddressFilter(treeAddress),
                new SequenceTopicsFilter(new SpecificTopic(BaselineModule.LeafTopic)));

            var insertLeavesLogs = _logFinder.FindLogs(insertLeavesFilter);
            var insertLeafLogs = _logFinder.FindLogs(insertLeafFilter);

            long? currentBlockNumber = null;
            uint count = 0;
            using var batch = baselineTree.StartBatch();
            foreach (FilterLog filterLog in insertLeavesLogs
                .Union(insertLeafLogs)
                .OrderBy(fl => fl.BlockNumber).ThenBy(fl => fl.LogIndex))
            {
                if (filterLog.BlockHash == baselineTree.LastBlockDbHash)
                    continue;
                if (currentBlockNumber == null)
                {
                    currentBlockNumber = filterLog.BlockNumber;
                }

                if (currentBlockNumber != filterLog.BlockNumber)
                {
                    baselineTree.MemorizePastCount(currentBlockNumber.Value, count);
                    currentBlockNumber = filterLog.BlockNumber;
                }

                if (filterLog.Data.Length == 96)
                {
                    Keccak leafHash = new Keccak(filterLog.Data.Slice(32, 32).ToArray());
                    
                    if(_logger.IsWarn) _logger.Warn($"Inserting leaf into {baselineTree} in block {currentBlockNumber}");
                    baselineTree.Insert(leafHash, false);
                    ++count;
                }
                else
                {
                    for (int i = 0; i < (filterLog.Data.Length - 128) / 32; i++)
                    {
                        Keccak leafHash = new Keccak(filterLog.Data.Slice(128 + 32 * i, 32).ToArray());
                        if(_logger.IsWarn) _logger.Warn($"Inserting leaf {i} into {baselineTree} in block {currentBlockNumber}");
                        baselineTree.Insert(leafHash, false);
                        ++count;
                    }
                }
            }

            if (currentBlockNumber != null && count != 0)
            {
                baselineTree.MemorizePastCount(currentBlockNumber.Value, baselineTree.Count);
            }

            baselineTree.CalculateHashes(initCount);
            return baselineTree;
        }
    }
}
