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

namespace Nethermind.Baseline.Tree
{
    public interface IBaselineTreeHelper
    {
        BaselineTree RebuildEntireTree(Address treeAddress, Keccak blockHash);
    }

    public class BaselineTreeHelper : IBaselineTreeHelper
    {
        private readonly ILogFinder _logFinder;
        
        public BaselineTreeHelper(ILogFinder logFinder)
        {
            _logFinder = logFinder;
        }

        public BaselineTree RebuildEntireTree(Address treeAddress, Keccak blockHash)
        {
            Keccak leavesTopic = new Keccak("0x8ec50f97970775682a68d3c6f9caedf60fd82448ea40706b8b65d6c03648b922");
            LogFilter insertLeavesFilter = new LogFilter(
                0,
                new BlockParameter(0L),
                new BlockParameter(blockHash),
                new AddressFilter(treeAddress),
                new TopicsFilter(new SpecificTopic(leavesTopic)));

            Keccak leafTopic = new Keccak("0x6a82ba2aa1d2c039c41e6e2b5a5a1090d09906f060d32af9c1ac0beff7af75c0");
            LogFilter insertLeafFilter = new LogFilter(
                0,
                new BlockParameter(0L),
                new BlockParameter(blockHash),
                new AddressFilter(treeAddress),
                new TopicsFilter(new SpecificTopic(leafTopic))); // find tree topics

            var insertLeavesLogs = _logFinder.FindLogs(insertLeavesFilter);
            var insertLeafLogs = _logFinder.FindLogs(insertLeafFilter);
            BaselineTree baselineTree = new ShaBaselineTree(new MemDb(), Array.Empty<byte>(), 5);

            // Keccak leafTopic = new Keccak("0x8ec50f97970775682a68d3c6f9caedf60fd82448ea40706b8b65d6c03648b922");
            foreach (FilterLog filterLog in insertLeavesLogs
                .Union(insertLeafLogs)
                .OrderBy(fl => fl.BlockNumber).ThenBy(fl => fl.LogIndex))
            {
                // okomentować
                if (filterLog.Data.Length == 96)
                {
                    Keccak leafHash = new Keccak(filterLog.Data.Slice(32, 32).ToArray());
                    baselineTree.Insert(leafHash);
                }
                else
                {
                    for (int i = 0; i < (filterLog.Data.Length - 128) / 32; i++)
                    {
                        Keccak leafHash = new Keccak(filterLog.Data.Slice(128 + 32 * i, 32).ToArray());
                        baselineTree.Insert(leafHash);
                    }
                }
            }

            return baselineTree;
        }
    }
}
