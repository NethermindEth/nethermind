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
using System.IO;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test
{
    public class PoSSwitcherTests
    {
        private static readonly IBlockCacheService _blockCacheService = new BlockCacheService();

        [Test]
        public void Initial_TTD_should_be_null()
        {
            UInt256? expectedTtd = null;
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            PoSSwitcher poSSwitcher = new(new MergeConfig(), new SyncConfig(), new MemDb(), blockTree, MainnetSpecProvider.Instance, _blockCacheService, LimboLogs.Instance);

            Assert.AreEqual(expectedTtd, poSSwitcher.TerminalTotalDifficulty);
        }
        
        [Test]
        public void Read_TTD_from_chainspec_if_not_specified_in_merge_config()
        {
            UInt256 expectedTtd = 10;
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            ChainSpecLoader loader = new(new EthereumJsonSerializer());
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Specs/test_spec.json");
            ChainSpec chainSpec = loader.Load(File.ReadAllText(path));

            ChainSpecBasedSpecProvider specProvider = new(chainSpec);
            PoSSwitcher poSSwitcher = new(new MergeConfig(), new SyncConfig(), new MemDb(), blockTree, specProvider, _blockCacheService, LimboLogs.Instance);

            Assert.AreEqual(expectedTtd, poSSwitcher.TerminalTotalDifficulty);
            Assert.AreEqual(101, specProvider.MergeBlockNumber);
        }
        
        [TestCase(5000000)]
        [TestCase(4900000)]
        public void IsTerminalBlock_returning_expected_results(long terminalTotalDifficulty)
        {
            Block genesisBlock = Build.A.Block.WithNumber(0).TestObject; 
            BlockTree blockTree = Build.A.BlockTree(genesisBlock).OfChainLength(6).TestObject;
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.TerminalTotalDifficulty = (UInt256)terminalTotalDifficulty;
            PoSSwitcher poSSwitcher = CreatePosSwitcher(blockTree, new MemDb(), specProvider);

            BlockHeader? block3 = blockTree.FindHeader(3, BlockTreeLookupOptions.All);
            BlockHeader? block4 = blockTree.FindHeader(4, BlockTreeLookupOptions.All);
            BlockHeader? block5 = blockTree.FindHeader(5, BlockTreeLookupOptions.All);
            Block blockWithPostMergeFlag = Build.A.Block.WithNumber(4).WithDifficulty(0).WithPostMergeFlag(true)
                                            .WithParent(block3!).TestObject; 
            Assert.AreEqual(false, poSSwitcher.IsTerminalBlock(block3!)); // PoWBlock
            Assert.AreEqual(true, poSSwitcher.IsTerminalBlock(block4!)); // terminal block
            Assert.AreEqual(false, poSSwitcher.IsTerminalBlock(block5!)); // incorrect PoW not terminal block
            Assert.AreEqual(false, poSSwitcher.IsTerminalBlock(blockWithPostMergeFlag.Header)); // block with post merge flag
        }
        
        [TestCase(5000000, true)]
        [TestCase(4900000, false)]
        public void IsTerminalBlock_returning_expected_result_for_genesis_block(long genesisDifficulty, bool expectedResult)
        {
            Block genesisBlock = Build.A.Block.WithNumber(0).WithDifficulty((UInt256)genesisDifficulty)
                .WithTotalDifficulty(genesisDifficulty).TestObject; 
            BlockTree blockTree = Build.A.BlockTree(genesisBlock).OfChainLength(6).TestObject;
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.TerminalTotalDifficulty = (UInt256)5000000;
            PoSSwitcher poSSwitcher = CreatePosSwitcher(blockTree, new MemDb(), specProvider);

            Assert.AreEqual(expectedResult, poSSwitcher.IsTerminalBlock(genesisBlock!.Header));
        }
        
        [Test]
        public void Override_TTD_and_number_from_merge_config()
        {
            UInt256 expectedTtd = 340;
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.UpdateMergeTransitionInfo(100, 20);
            PoSSwitcher poSSwitcher = new(new MergeConfig() {TerminalTotalDifficulty = "340", TerminalBlockNumber = 2000}, new SyncConfig(), new MemDb(), blockTree, specProvider, _blockCacheService, LimboLogs.Instance);

            Assert.AreEqual(expectedTtd, poSSwitcher.TerminalTotalDifficulty);
            Assert.AreEqual(2001, specProvider.MergeBlockNumber);
        }
        
        [Test]
        public void Can_update_merge_transition_info()
        {
            UInt256 expectedTtd = 340;
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.UpdateMergeTransitionInfo(2001, expectedTtd);
            PoSSwitcher poSSwitcher = new(new MergeConfig() {},  new SyncConfig(), new MemDb(), blockTree, specProvider, _blockCacheService, LimboLogs.Instance);

            Assert.AreEqual(expectedTtd, poSSwitcher.TerminalTotalDifficulty);
            Assert.AreEqual(2001, specProvider.MergeBlockNumber);
        }
        
        [TestCase(5000000)]
        [TestCase(4900000)]
        public void GetBlockSwitchInfo_returning_expected_results(long terminalTotalDifficulty)
        {
            Block genesisBlock = Build.A.Block.WithNumber(0).TestObject; 
            BlockTree blockTree = Build.A.BlockTree(genesisBlock).OfChainLength(6).TestObject;
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.TerminalTotalDifficulty = (UInt256)terminalTotalDifficulty;
            PoSSwitcher poSSwitcher = CreatePosSwitcher(blockTree, new MemDb(), specProvider);

            BlockHeader? block3 = blockTree.FindHeader(3, BlockTreeLookupOptions.All);
            BlockHeader? block4 = blockTree.FindHeader(4, BlockTreeLookupOptions.All);
            BlockHeader? block5 = blockTree.FindHeader(5, BlockTreeLookupOptions.All);
            Block blockWithPostMergeFlag = Build.A.Block.WithNumber(4).WithDifficulty(0).WithPostMergeFlag(true)
                .WithParent(block3!).TestObject; 
            Assert.AreEqual((false, false), poSSwitcher.GetBlockConsensusInfo(block3!)); // PoWBlock
            Assert.AreEqual((true, false), poSSwitcher.GetBlockConsensusInfo(block4!)); // terminal block
            Assert.AreEqual((false, true), poSSwitcher.GetBlockConsensusInfo(block5!)); // incorrect PoW, TTD > TD and it is not terminal, so we should process it in the same way like post merge blocks
            Assert.AreEqual((false, true), poSSwitcher.GetBlockConsensusInfo(blockWithPostMergeFlag.Header)); // block with post merge flag
        }

        [Test]
        public void Switch_when_TTD_is_reached()
        {
            Block genesisBlock = Build.A.Block.WithNumber(0).TestObject; 
            BlockTree blockTree = Build.A.BlockTree(genesisBlock).OfChainLength(4).TestObject;
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.TerminalTotalDifficulty = 5000000;
            PoSSwitcher poSSwitcher = CreatePosSwitcher(blockTree, new MemDb(), specProvider);

            Assert.AreEqual(false, poSSwitcher.HasEverReachedTerminalBlock());
            Block block = Build.A.Block.WithTotalDifficulty(5000000L).WithNumber(4).WithParent(blockTree.Head!).WithDifficulty(1000000L).TestObject;
            blockTree.SuggestBlock(block);
            blockTree.UpdateMainChain(block);

            Assert.AreEqual(true, poSSwitcher.HasEverReachedTerminalBlock());
        }
        
        [Test]
        public void Can_load_parameters_after_the_restart()
        {
            using MemDb metadataDb = new();
            var terminalBlock = 4;
            Block genesisBlock = Build.A.Block.WithNumber(0).TestObject; 
            BlockTree blockTree = Build.A.BlockTree(genesisBlock).OfChainLength(4).TestObject;
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.TerminalTotalDifficulty = 5000000;
            PoSSwitcher poSSwitcher = CreatePosSwitcher(blockTree, metadataDb, specProvider);

            Assert.AreEqual(false, poSSwitcher.HasEverReachedTerminalBlock());
            Block block = Build.A.Block.WithTotalDifficulty(5000000L).WithNumber(terminalBlock).WithParent(blockTree.Head!).WithDifficulty(1000000L).TestObject;
            blockTree.SuggestBlock(block);
            blockTree.UpdateMainChain(block);
            Assert.AreEqual(terminalBlock + 1, specProvider.MergeBlockNumber);
            Assert.AreEqual(true, poSSwitcher.HasEverReachedTerminalBlock());

            TestSpecProvider newSpecProvider = new(London.Instance);
            newSpecProvider.TerminalTotalDifficulty = 5000000L;
            // we're using the same MemDb for a new switcher
            PoSSwitcher newPoSSwitcher = CreatePosSwitcher(blockTree, metadataDb, newSpecProvider);
            
            Assert.AreEqual(terminalBlock + 1, newSpecProvider.MergeBlockNumber);
            Assert.AreEqual(true, newPoSSwitcher.HasEverReachedTerminalBlock());
        }

        private static PoSSwitcher CreatePosSwitcher(IBlockTree blockTree, IDb? db = null, ISpecProvider? specProvider = null)
        {
            db ??= new MemDb();
            MergeConfig? mergeConfig = new() {Enabled = true};
            return new PoSSwitcher(mergeConfig, new SyncConfig(), db, blockTree, specProvider ?? MainnetSpecProvider.Instance, _blockCacheService, LimboLogs.Instance);
        }
    }
}
