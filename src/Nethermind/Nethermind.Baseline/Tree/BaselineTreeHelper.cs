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
using Nethermind.Trie;

namespace Nethermind.Baseline.Tree
{
    public class BaselineTreeHelper : IBaselineTreeHelper
    {
        private readonly ILogFinder _logFinder;
        private readonly IDb _mainDb;
        private readonly IKeyValueStore _metadataBaselineDb;

        public BaselineTreeHelper(ILogFinder logFinder, IDb mainDb, IKeyValueStore metadataBaselineDb)
        {
            _logFinder = logFinder ?? throw new ArgumentNullException(nameof(logFinder));
            _mainDb = mainDb ?? throw new ArgumentNullException(nameof(mainDb));
            _metadataBaselineDb = metadataBaselineDb ?? throw new ArgumentNullException(nameof(metadataBaselineDb));
        }

        public BaselineTreeNode[] GetHistoricalLeaves(BaselineTree tree, uint[] leafIndexes, long blockNumber)
        {
            var historicalCount = tree.GetCountDiff(blockNumber);
            BaselineTreeNode[] leaves = new BaselineTreeNode[leafIndexes.Length];

            for (int i = 0; i < leafIndexes.Length; i++)
            {
                var leafIndex = leafIndexes[i];
                if (historicalCount < leafIndex)
                {
                    leaves[i] = tree.GetLeaf(leafIndex);
                }
                else
                {
                    leaves[i] = new BaselineTreeNode(Keccak.Zero, leafIndex);
                }
            }

            return leaves;
        }

        public BaselineTreeNode GetHistoricalLeaf(BaselineTree tree, uint leafIndex, long blockNumber)
        {
            var historicalCount = tree.GetCountDiff(blockNumber);
            if (historicalCount < leafIndex)
            {
                return new BaselineTreeNode(Keccak.Zero, leafIndex);
            }

            return tree.GetLeaf(leafIndex);
        }

        public BaselineTree CreateHistoricalTree(Address address, long blockNumber)
        {
            // ToDo MM locking
            var historicalTree = new ShaBaselineTree(new ReadOnlyDb(_mainDb, true), _metadataBaselineDb, address.Bytes, BaselineModule.TruncationLength);
            var endIndex = historicalTree.Count;
            var historicalCount = historicalTree.GetCountDiff(blockNumber);
            historicalTree.Delete(endIndex - historicalCount, false);
            historicalTree.CalculateHashes(historicalCount, endIndex);

            return historicalTree;
        }

        public BaselineTree RebuildEntireTree(Address treeAddress, Keccak blockHash)
        {
            BaselineTree baselineTree = new ShaBaselineTree(_mainDb, _metadataBaselineDb, treeAddress.Bytes, BaselineModule.TruncationLength);
            return BuildTree(baselineTree, treeAddress, new BlockParameter(0L), new BlockParameter(blockHash));
        }

        public BaselineTree BuildTree(BaselineTree baselineTree, Address treeAddress, BlockParameter blockFrom, BlockParameter blockTo)
        {
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
                if (currentBlockNumber == null)
                {
                    currentBlockNumber = filterLog.BlockNumber;
                }

                if (currentBlockNumber != filterLog.BlockNumber)
                {
                    var previousBlockWithLeaves = baselineTree.LastBlockWithLeaves;
                    baselineTree.Metadata.SaveBlockNumberCount(currentBlockNumber.Value, count, previousBlockWithLeaves);
                    baselineTree.LastBlockWithLeaves = currentBlockNumber.Value;
                    currentBlockNumber = filterLog.BlockNumber;
                    count = 1; // ToDo wrong -> we do not know how many leaves we will have
                }
                else
                {
                    ++count;
                }

                if (filterLog.Data.Length == 96)
                {
                    Keccak leafHash = new Keccak(filterLog.Data.Slice(32, 32).ToArray());
                    baselineTree.Insert(leafHash, false);
                }
                else
                {
                    for (int i = 0; i < (filterLog.Data.Length - 128) / 32; i++)
                    {
                        Keccak leafHash = new Keccak(filterLog.Data.Slice(128 + 32 * i, 32).ToArray());
                        baselineTree.Insert(leafHash, false);
                    }
                }
            }

            if (currentBlockNumber != null && count != 0)
            {
                var previousBlockWithLeaves = baselineTree.LastBlockWithLeaves;
                baselineTree.Metadata.SaveBlockNumberCount(currentBlockNumber.Value, count, previousBlockWithLeaves);
                baselineTree.LastBlockWithLeaves = currentBlockNumber.Value;
            }

            baselineTree.CalculateHashes(initCount);
            return baselineTree;
        }
    }
}
