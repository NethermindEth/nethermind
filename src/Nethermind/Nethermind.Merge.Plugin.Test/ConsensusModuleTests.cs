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
// 

using System;
using Nethermind.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Specs;
using Nethermind.State.Repositories;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test
{
    public class ConsensusModuleTests
    {
        private BlockTree BuildBlockTree()
        {
            // IBlockTree blockTree = Build.A.BlockTree().TestObject;
            MemDb blocksDb = new();
            MemDb headersDb = new();
            MemDb blocksInfosDb = new();
            ChainLevelInfoRepository chainLevelInfoRepository = new(blocksInfosDb);
            return new BlockTree(blocksDb, headersDb, blocksInfosDb, chainLevelInfoRepository, MainnetSpecProvider.Instance, NullBloomStorage.Instance, LimboLogs.Instance);
        }
        
        [Test]
        public void consensus_assembleBlock_should_return_expected_results()
        {
            IConsensusRpcModule consensusRpcModule = CreateConsensusModule();
            Assert.Throws<NotImplementedException> (() => consensusRpcModule.consensus_assembleBlock(new AssembleBlockRequest()));
        }
        
        [Test]
        public void consensus_newBlock_should_return_expected_results()
        {
            IConsensusRpcModule consensusRpcModule = CreateConsensusModule();
            Assert.Throws<NotImplementedException> (() => consensusRpcModule.consensus_newBlock(new BlockRequestResult()));
        }
        
        [Test]
        public void consensus_finaliseBlock_should_return_true()
        {
            IConsensusRpcModule consensusRpcModule = CreateConsensusModule();
            var result = consensusRpcModule.consensus_finaliseBlock(TestItem.KeccakA);
            Assert.AreEqual(true, result.Data.Value);
        }
        
        private IConsensusRpcModule CreateConsensusModule()
        {
            IBlockTree blockTree = BuildBlockTree();

            // ToDo temp
            var eth2BlockProducer = new Eth2BlockProducer(null, null, blockTree, null, null, null, null, LimboLogs.Instance);
            return new ConsensusRpcModule(
                new AssembleBlockHandler(blockTree, eth2BlockProducer, LimboLogs.Instance),
                new NewBlockHandler(blockTree, null, null, LimboLogs.Instance),
                new FinaliseBlockHandler(),
                new SetHeadBlockHandler(blockTree, LimboLogs.Instance));
        }
    }
}
