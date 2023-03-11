// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
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
            PoSSwitcher poSSwitcher = new(new MergeConfig(), new SyncConfig(), new MemDb(), blockTree, TestSpecProvider.Instance, LimboLogs.Instance);

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
            PoSSwitcher poSSwitcher = new(new MergeConfig(), new SyncConfig(), new MemDb(), blockTree, specProvider, LimboLogs.Instance);

            Assert.AreEqual(expectedTtd, poSSwitcher.TerminalTotalDifficulty);
            Assert.AreEqual(101, specProvider.MergeBlockNumber?.BlockNumber);
        }

        [TestCase(5000000)]
        [TestCase(4900000)]
        public void IsTerminalBlock_returning_expected_results(long terminalTotalDifficulty)
        {
            Block genesisBlock = Build.A.Block.WithNumber(0).TestObject;
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.TerminalTotalDifficulty = (UInt256)terminalTotalDifficulty;
            BlockTree blockTree = Build.A.BlockTree(genesisBlock, specProvider).OfChainLength(6).TestObject;
            PoSSwitcher poSSwitcher = CreatePosSwitcher(blockTree, new MemDb(), specProvider);

            BlockHeader? block3 = blockTree.FindHeader(3, BlockTreeLookupOptions.All);
            BlockHeader? block4 = blockTree.FindHeader(4, BlockTreeLookupOptions.All);
            BlockHeader? block5 = blockTree.FindHeader(5, BlockTreeLookupOptions.All);
            Block blockWithPostMergeFlag = Build.A.Block.WithNumber(4).WithDifficulty(0).WithPostMergeFlag(true)
                                            .WithParent(block3!).TestObject;
            Assert.AreEqual(false, block3!.IsTerminalBlock(specProvider)); // PoWBlock
            Assert.AreEqual(true, block4!.IsTerminalBlock(specProvider)); // terminal block
            Assert.AreEqual(false, block5!.IsTerminalBlock(specProvider)); // incorrect PoW not terminal block
            Assert.AreEqual(false, blockWithPostMergeFlag.IsTerminalBlock(specProvider)); // block with post merge flag
        }

        [TestCase(5000000, true)]
        [TestCase(4900000, false)]
        public void IsTerminalBlock_returning_expected_result_for_genesis_block(long genesisDifficulty, bool expectedResult)
        {
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.TerminalTotalDifficulty = (UInt256)5000000;
            Block genesisBlock = Build.A.Block.WithNumber(0).WithDifficulty((UInt256)genesisDifficulty)
                .WithTotalDifficulty(genesisDifficulty).TestObject;
            BlockTree blockTree = Build.A.BlockTree(genesisBlock, specProvider).OfChainLength(6).TestObject;
            PoSSwitcher poSSwitcher = CreatePosSwitcher(blockTree, new MemDb(), specProvider);

            Assert.AreEqual(expectedResult, genesisBlock.IsTerminalBlock(specProvider));
        }

        [Test]
        public void Override_TTD_and_number_from_merge_config()
        {
            UInt256 expectedTtd = 340;
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.UpdateMergeTransitionInfo(100, 20);
            PoSSwitcher poSSwitcher = new(new MergeConfig() { TerminalTotalDifficulty = "340", TerminalBlockNumber = 2000 }, new SyncConfig(), new MemDb(), blockTree, specProvider, LimboLogs.Instance);

            Assert.AreEqual(expectedTtd, poSSwitcher.TerminalTotalDifficulty);
            Assert.AreEqual(2001, specProvider.MergeBlockNumber?.BlockNumber);
        }

        [Test]
        public void Can_update_merge_transition_info()
        {
            UInt256 expectedTtd = 340;
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.UpdateMergeTransitionInfo(2001, expectedTtd);
            PoSSwitcher poSSwitcher = new(new MergeConfig() { }, new SyncConfig(), new MemDb(), blockTree, specProvider, LimboLogs.Instance);

            Assert.AreEqual(expectedTtd, poSSwitcher.TerminalTotalDifficulty);
            Assert.AreEqual(2001, specProvider.MergeBlockNumber?.BlockNumber);
        }

        [TestCase(5000000)]
        [TestCase(4900000)]
        public void GetBlockSwitchInfo_returning_expected_results(long terminalTotalDifficulty)
        {
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.TerminalTotalDifficulty = (UInt256)terminalTotalDifficulty;
            Block genesisBlock = Build.A.Block.WithNumber(0).TestObject;
            BlockTree blockTree = Build.A.BlockTree(genesisBlock, specProvider).OfChainLength(6).TestObject;
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

        [TestCase(5000000, false)]
        [TestCase(4900000, false)]
        [TestCase(5000000, true)]
        [TestCase(4900000, true)]
        public void GetBlockSwitchInfo_returning_expected_results_when_td_null_or_zero(long terminalTotalDifficulty, bool nullTdValue)
        {
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.TerminalTotalDifficulty = (UInt256)terminalTotalDifficulty;
            Block genesisBlock = Build.A.Block.WithNumber(0).TestObject;
            BlockTree blockTree = Build.A.BlockTree(genesisBlock, specProvider).OfChainLength(6).TestObject;
            PoSSwitcher poSSwitcher = CreatePosSwitcher(blockTree, new MemDb(), specProvider);

            BlockHeader? block3 = blockTree.FindHeader(3, BlockTreeLookupOptions.All);
            BlockHeader? block4 = blockTree.FindHeader(4, BlockTreeLookupOptions.All);
            BlockHeader? block5 = blockTree.FindHeader(5, BlockTreeLookupOptions.All);
            Block blockWithPostMergeFlag = Build.A.Block.WithNumber(4).WithDifficulty(0).WithPostMergeFlag(true)
                .WithParent(block3!).TestObject;
            block3!.TotalDifficulty = nullTdValue ? null : UInt256.Zero;
            block4!.TotalDifficulty = nullTdValue ? null : UInt256.Zero;
            block5!.TotalDifficulty = nullTdValue ? null : UInt256.Zero;
            Assert.AreEqual((false, false), poSSwitcher.GetBlockConsensusInfo(block3!)); // PoWBlock
            Assert.AreEqual((false, false), poSSwitcher.GetBlockConsensusInfo(block4!)); // terminal block
            Assert.AreEqual((false, false), poSSwitcher.GetBlockConsensusInfo(block5!));
            Assert.AreEqual((false, true), poSSwitcher.GetBlockConsensusInfo(blockWithPostMergeFlag.Header)); // block with post merge flag
        }

        [Test]
        public void New_terminal_block_when_ttd_reached()
        {
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.TerminalTotalDifficulty = 5000000;
            Block genesisBlock = Build.A.Block.WithNumber(0).TestObject;
            BlockTree blockTree = Build.A.BlockTree(genesisBlock, specProvider).OfChainLength(4).TestObject;

            PoSSwitcher poSSwitcher = CreatePosSwitcher(blockTree, new MemDb(), specProvider);

            Assert.AreEqual(false, poSSwitcher.HasEverReachedTerminalBlock());
            Block block = Build.A.Block.WithTotalDifficulty(5000000L).WithNumber(4).WithParent(blockTree.Head!).WithDifficulty(1000000L).TestObject;
            Block alternativeTerminalBlock = Build.A.Block.WithTotalDifficulty(5000000L).WithNumber(4).WithParent(blockTree.Head!).WithGasLimit(20000000).WithDifficulty(1000000L).TestObject;
            blockTree.SuggestBlock(block);
            blockTree.UpdateMainChain(block);
            Assert.AreEqual(true, poSSwitcher.HasEverReachedTerminalBlock());
            Assert.AreEqual((true, false), poSSwitcher.GetBlockConsensusInfo(alternativeTerminalBlock.Header));
        }

        [Test]
        public void Switch_when_TTD_is_reached()
        {
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.TerminalTotalDifficulty = 5000000;
            Block genesisBlock = Build.A.Block.WithNumber(0).TestObject;
            BlockTree blockTree = Build.A.BlockTree(genesisBlock, specProvider).OfChainLength(4).TestObject;

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
            int terminalBlock = 4;
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.TerminalTotalDifficulty = 5000000;
            Block genesisBlock = Build.A.Block.WithNumber(0).TestObject;
            BlockTree blockTree = Build.A.BlockTree(genesisBlock, specProvider).OfChainLength(4).TestObject;
            PoSSwitcher poSSwitcher = CreatePosSwitcher(blockTree, metadataDb, specProvider);

            Assert.AreEqual(false, poSSwitcher.HasEverReachedTerminalBlock());
            Block block = Build.A.Block.WithTotalDifficulty(5000000L).WithNumber(terminalBlock).WithParent(blockTree.Head!).WithDifficulty(1000000L).TestObject;
            blockTree.SuggestBlock(block);
            blockTree.UpdateMainChain(block);
            Assert.AreEqual(terminalBlock + 1, specProvider.MergeBlockNumber?.BlockNumber);
            Assert.AreEqual(true, poSSwitcher.HasEverReachedTerminalBlock());

            TestSpecProvider newSpecProvider = new(London.Instance);
            newSpecProvider.TerminalTotalDifficulty = 5000000L;
            // we're using the same MemDb for a new switcher
            PoSSwitcher newPoSSwitcher = CreatePosSwitcher(blockTree, metadataDb, newSpecProvider);

            Assert.AreEqual(terminalBlock + 1, newSpecProvider.MergeBlockNumber?.BlockNumber);
            Assert.AreEqual(true, newPoSSwitcher.HasEverReachedTerminalBlock());
        }

        private static PoSSwitcher CreatePosSwitcher(IBlockTree blockTree, IDb? db = null, ISpecProvider? specProvider = null)
        {
            db ??= new MemDb();
            MergeConfig? mergeConfig = new() { };
            return new PoSSwitcher(mergeConfig, new SyncConfig(), db, blockTree, specProvider ?? MainnetSpecProvider.Instance, LimboLogs.Instance);
        }
    }
}
