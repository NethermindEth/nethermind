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
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Baseline.Tree
{
    public sealed class BaselineTreeTracker : IDisposable
    {
        public static Keccak LeafTopic = new Keccak("0x6a82ba2aa1d2c039c41e6e2b5a5a1090d09906f060d32af9c1ac0beff7af75c0");
        public static Keccak LeavesTopic = new Keccak("0x8ec50f97970775682a68d3c6f9caedf60fd82448ea40706b8b65d6c03648b922");
        private readonly Address _address;
        private readonly IBaselineTreeHelper _baselineTreeHelper;
        private readonly IBlockProcessor _blockProcessor;
        private readonly ILogFinder _logFinder;
        private const int MaxLeavesInStack = 1000;
        private BaselineTree _baselineTree;
        private Block? _currentBlock = null; // BlockHeader lub number/hash
        private Stack<Keccak> _leavesStack = new Stack<Keccak>();

        /// <summary>
        /// This class should smoothly react to new blocks and logs
        /// For now it will be very non-optimized just to deliver the basic functionality
        /// </summary>
        /// <param name="address"></param>
        /// <param name="baselineTree"></param>
        /// <param name="blockProcessor"></param>
        public BaselineTreeTracker(Address address, BaselineTree baselineTree, IBlockProcessor blockProcessor, IBaselineTreeHelper baselineTreeHelper)
        {
            _address = address ?? throw new ArgumentNullException(nameof(address));
            _baselineTree = baselineTree ?? throw new ArgumentNullException(nameof(baselineTree));
            _blockProcessor = blockProcessor ?? throw new ArgumentNullException(nameof(blockProcessor));
            _baselineTreeHelper = baselineTreeHelper ?? throw new ArgumentNullException(nameof(baselineTreeHelper));
            _blockProcessor.BlockProcessed += OnBlockProcessed;
        }

        private void OnBlockProcessed(object? sender, BlockProcessedEventArgs e)
        {
            if (_currentBlock != null && _currentBlock.Hash != e.Block.ParentHash)
            {
                if (_currentBlock.Number < e.Block.Number) // ToDo MM nie ma inicjowania bloku current
                {
                    AddNewLeavesFromDb(_currentBlock.Hash, e.Block.Hash);
                }
                else
                {
                    var leavesToReorganize = _baselineTree.GetLeavesCountFromNextBlocks(e.Block.Number);
                    if (leavesToReorganize < MaxLeavesInStack) // toDo MM think about
                    {
                        Reorganize(leavesToReorganize);
                    }
                    else
                    {
                        _baselineTree = _baselineTreeHelper.RebuildEntireTree(_address, e.Block.Hash);
                        return;
                    }
                }

                return;
            }



            _currentBlock = e.Block;
            LogFilter insertLeavesFilter = new LogFilter(
    0,
    new BlockParameter(0L),
    new BlockParameter(0L),
    new AddressFilter(_address),
    new AnyTopicsFilter(new SpecificTopic(LeavesTopic), new SpecificTopic(LeafTopic)));
            var logs = _currentBlock.Header.FindLogs(e.TxReceipts, insertLeavesFilter, FindOrder.Ascending, FindOrder.Ascending);
            foreach (var filterLog in logs)
            {
                if (filterLog.Data.Length == 96)
                {
                    Keccak leafHash = new Keccak(filterLog.Data.Slice(32, 32).ToArray());
                    _leavesStack.Push(leafHash);
                    _baselineTree.Insert(leafHash);
                }
                else
                {
                    for (int i = 0; i < (filterLog.Data.Length - 128) / 32; i++)
                    {
                        Keccak leafHash = new Keccak(filterLog.Data.Slice(128 + 32 * i, 32).ToArray());
                        _leavesStack.Push(leafHash);
                        _baselineTree.Insert(leafHash);
                    }
                }
            }
        }

        private void Reorganize(uint numberOfElements)
        {
            var calculatingHashesStart = _baselineTree.Count - numberOfElements;
            var itemsToMove = new Keccak[numberOfElements];
            for (uint i = 0; i < numberOfElements; ++i)
            {
                itemsToMove[i] = _baselineTree.Pop(false);
            }

            // ToDo MM add receipts from block "copy after refactoring

            for (uint j = 0; j < numberOfElements; ++j)
            {
                _baselineTree.Insert(itemsToMove[j], false);
            }

            _baselineTree.CalculateHashes(calculatingHashesStart);
        }

        private void AddNewLeavesFromDb(Keccak from, Keccak to)
        {
            var initCount = _baselineTree.Count;
            LogFilter insertLeavesFilter = new LogFilter(
                0,
                new BlockParameter(from),
                new BlockParameter(to),
                new AddressFilter(_address),
                new SequenceTopicsFilter(new SpecificTopic(LeafTopic)));

            LogFilter insertLeafFilter = new LogFilter(
                0,
                new BlockParameter(from),
                new BlockParameter(to),
                new AddressFilter(_address),
                new SequenceTopicsFilter(new SpecificTopic(LeavesTopic)));

            var insertLeavesLogs = _logFinder.FindLogs(insertLeavesFilter);
            var insertLeafLogs = _logFinder.FindLogs(insertLeafFilter);

            foreach (FilterLog filterLog in insertLeavesLogs
                .Union(insertLeafLogs)
                .OrderBy(fl => fl.BlockNumber).ThenBy(fl => fl.LogIndex))
            {
                if (filterLog.Data.Length == 96)
                {
                    Keccak leafHash = new Keccak(filterLog.Data.Slice(32, 32).ToArray());
                    _baselineTree.Insert(leafHash, false);
                }
                else
                {
                    for (int i = 0; i < (filterLog.Data.Length - 128) / 32; i++)
                    {
                        Keccak leafHash = new Keccak(filterLog.Data.Slice(128 + 32 * i, 32).ToArray());
                        _baselineTree.Insert(leafHash, false);
                    }
                }
            }

            _baselineTree.CalculateHashes(initCount);
        }

        public void Dispose()
        {
            _blockProcessor.BlockProcessed -= OnBlockProcessed;
        }
    }
}
