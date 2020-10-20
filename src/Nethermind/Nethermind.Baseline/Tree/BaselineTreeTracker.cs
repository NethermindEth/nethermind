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
using System.Linq;
using System.Timers;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Baseline.Tree
{
    public class BaselineTreeTracker
    {
        private readonly Address _address;
        private readonly BaselineTree _baselineTree;
        private readonly ILogFinder _logFinder;
        private readonly IBlockFinder _blockFinder;
        private readonly IBlockProcessor _blockProcessor;
        private Timer _timer;

        /// <summary>
        /// This class should smoothly react to new blocks and logs
        /// For now it will be very non-optimized just to deliver the basic functionality
        /// </summary>
        /// <param name="address"></param>
        /// <param name="baselineTree"></param>
        /// <param name="logFinder"></param>
        /// <param name="blockFinder"></param>
        /// <param name="blockProcessor"></param>
        public BaselineTreeTracker(Address address, BaselineTree baselineTree, ILogFinder logFinder, IBlockFinder blockFinder, IBlockProcessor blockProcessor)
        {
            _address = address ?? throw new ArgumentNullException(nameof(address));
            _baselineTree = baselineTree ?? throw new ArgumentNullException(nameof(baselineTree));
            _logFinder = logFinder ?? throw new ArgumentNullException(nameof(logFinder));
            _blockFinder = blockFinder ?? throw new ArgumentNullException(nameof(blockFinder));
            _blockProcessor = blockProcessor ?? throw new ArgumentNullException(nameof(blockProcessor));
            _blockProcessor.BlockProcessed += OnBlockProcessed;

            _timer = InitTimer();
        }

        private void OnBlockProcessed(object? sender, BlockProcessedEventArgs e)
        {
            var block = e.Block;
            // check if current parent block = e.Block
            Keccak[] leavesAndLeafTopics = new Keccak[]
            {
                new Keccak("0x8ec50f97970775682a68d3c6f9caedf60fd82448ea40706b8b65d6c03648b922"),
                new Keccak("0x6a82ba2aa1d2c039c41e6e2b5a5a1090d09906f060d32af9c1ac0beff7af75c0")
            };
            var logs = e.Block.Header.FindLogs(e.TxReceipts, new LogEntry(_address, Array.Empty<byte>(), leavesAndLeafTopics), FindOrder.Ascending, FindOrder.Ascending, BaselineLogEntryEqualityComparer.Instance);
            foreach (var filterLog in logs)
            {
                // ToDo write a comment here?
                if (filterLog.Data.Length == 96)
                {
                    Keccak leafHash = new Keccak(filterLog.Data.Slice(32, 32).ToArray());
                    _baselineTree.Insert(leafHash);
                }
                else
                {
                    for (int i = 0; i < (filterLog.Data.Length - 128) / 32; i++)
                    {
                        Keccak leafHash = new Keccak(filterLog.Data.Slice(128 + 32 * i, 32).ToArray());
                        _baselineTree.Insert(leafHash);
                    }
                }
            }

        }

        private Timer InitTimer()
        {
            Timer timer = new Timer();
            timer.Interval = 1000;
            timer.Elapsed += TimerOnElapsed;
            timer.AutoReset = false;
            return timer;
        }

        private void TimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            _timer.Enabled = true;
        }
    }
}
