//  Copyright (c) 2021 Demerzel Solutions Limited
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

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.FastSync
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class StateSyncFeedTempDbTests
    {
        [Test(Description = "We are ensuring that other parts of the system (beam sync in particular)" +
                            "can benefit from the state nodes awaiting resolution for their children." +
                            "Imagine a case where you have an extension node that is already known" +
                            "(both hash and value) to the node data feed but cannot yet been stored" +
                            "in the StateDb because we only store the nodes whose children are fully resolved." +
                            "Such a node should already be available for other state-related parts of the system.")]
        [Ignore("Temporarily disabled temp DB as it was causing trouble")]
        public async Task Can_read_dependent_items_from_state_db_while_waiting_for_dependencies()
        {
            MemDb codeDb = new MemDb();
            MemDb stateDB = new MemDb();
            MemDb tempDb = new MemDb();
            
            SyncConfig syncConfig = new SyncConfig();
            syncConfig.FastSync = true;

            IBlockTree blockTree = Substitute.For<IBlockTree>(); 
            ISyncPeerPool pool = Substitute.For<ISyncPeerPool>(); 
            
            SyncProgressResolver syncProgressResolver = new SyncProgressResolver(
                blockTree,
                NullReceiptStorage.Instance,
                stateDB,
                new MemDb(),
                new TrieStore(stateDB.Innermost, LimboLogs.Instance),
                syncConfig,
                LimboLogs.Instance);
            ISyncModeSelector syncModeSelector = new MultiSyncModeSelector(syncProgressResolver, pool, syncConfig, LimboLogs.Instance);
            StateSyncFeed stateSyncFeed = new StateSyncFeed(codeDb, stateDB, tempDb, syncModeSelector, blockTree, LimboLogs.Instance);

            // so we want to setup a trie in a structure of -> branch into two leaves
            // so we can respond with the branch node and with leaves missing
            // and we can prove that we can read the branch from the temp DB while it is still missing from the State DB

            AccountDecoder accountDecoder = new AccountDecoder();
            
            TrieNode leaf = TrieNodeFactory.CreateLeaf(new HexPrefix(true, new byte[] {1, 2, 3}), accountDecoder.Encode(Account.TotallyEmpty).Bytes);
            TrieNode branch = TrieNodeFactory.CreateBranch();
            branch.SetChild(1, leaf);
            branch.ResolveKey(NullTrieNodeResolver.Instance, true);

            // PatriciaTree tree = new PatriciaTree();
            // tree = new PatriciaTree();
            // tree.Set(branch.Keccak.Bytes, branch.Value);

            stateSyncFeed.ResetStateRoot(0, branch.Keccak);
            
            var request = await stateSyncFeed.PrepareRequest();
            BuildRequestAndHandleResponse(branch, request, stateSyncFeed);

            byte[] value = tempDb.Get(branch.Keccak);
            value.Should().BeEquivalentTo(branch.FullRlp);
            
            byte[] valueFromState = stateDB.Get(branch.Keccak);
            valueFromState.Should().BeNull();

            request = await stateSyncFeed.PrepareRequest();
            
            BuildRequestAndHandleResponse(leaf, request, stateSyncFeed);
            
            value = tempDb.Get(branch.Keccak);
            value.Should().BeNull();
            
            valueFromState = stateDB.Get(branch.Keccak);
            valueFromState.Should().BeEquivalentTo(branch.FullRlp);
        }

        private static void BuildRequestAndHandleResponse(TrieNode node, StateSyncBatch stateSyncBatch, StateSyncFeed stateSyncFeed)
        {
            stateSyncBatch.Responses = new[] {node.FullRlp};
            stateSyncFeed.HandleResponse(stateSyncBatch);
        }
    }
}
