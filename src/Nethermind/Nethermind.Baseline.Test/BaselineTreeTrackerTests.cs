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

using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Baseline.Tree;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.JsonRpc.Test.Modules;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.Baseline.Test
{
    public class BaselineTreeTrackerTests
    {
        [Test]
        public async Task Tree_tracker_should_track_blocks()
        {
            var spec = new SingleReleaseSpecProvider(ConstantinopleFix.Instance, 1);
            TestRpcBlockchain testRpc = await TestRpcBlockchain.ForTest(SealEngineType.NethDev).Build(spec);
            testRpc.TestWallet.UnlockAccount(TestItem.Addresses[0], new SecureString());
            BaselineTree baselineTree = BuildATree();
            new BaselineTreeTracker(TestItem.Addresses[0], baselineTree, testRpc.LogFinder, testRpc.BlockFinder, testRpc.BlockProcessor);
            await testRpc.AddBlock(TestRpcBlockchain.BuildSimpleTransaction.WithNonce(0).TestObject);
            await testRpc.AddBlock(TestRpcBlockchain.BuildSimpleTransaction.WithNonce(1).TestObject, TestRpcBlockchain.BuildSimpleTransaction.WithNonce(2).TestObject);
        }

        private BaselineTree BuildATree(IKeyValueStore keyValueStore = null)
        {
            return new ShaBaselineTree(keyValueStore ?? new MemDb(), new byte[] { }, 0);
        }
    }
}
