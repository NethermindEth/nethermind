using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Abi;
using Nethermind.Baseline.Tree;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Filters.Topics;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.TxPool;

namespace Nethermind.Baseline.Tree
{
    public class BaselineTreeHelper
    {
        public BaselineTree RebuildEntireTree(Address treeAddress, Keccak blockHash, ILogFinder logFinder)
        {
            // bad

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

            var insertLeavesLogs = logFinder.FindLogs(insertLeavesFilter);
            var insertLeafLogs = logFinder.FindLogs(insertLeafFilter);
            BaselineTree baselineTree = new ShaBaselineTree(new MemDb(), Array.Empty<byte>(), 5);

            // Keccak leafTopic = new Keccak("0x8ec50f97970775682a68d3c6f9caedf60fd82448ea40706b8b65d6c03648b922");
            foreach (FilterLog filterLog in insertLeavesLogs
                .Union(insertLeafLogs)
                .OrderBy(fl => fl.BlockNumber).ThenBy(fl => fl.LogIndex))
            {
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
