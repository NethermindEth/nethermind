// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test
{
    public class PoSSwitcherTests
    {
        [Test]
        public void Initial_TTD_should_be_null()
        {
            UInt256? expectedTtd = null;
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            PoSSwitcher poSSwitcher = new(new MergeConfig(), new SyncConfig(), new MemDb(), blockTree, TestSpecProvider.Instance, new ChainSpec(), LimboLogs.Instance);

            Assert.That(poSSwitcher.TerminalTotalDifficulty, Is.EqualTo(expectedTtd));
        }

        [Test]
        public void Read_TTD_from_chainspec_if_not_specified_in_merge_config()
        {
            UInt256 expectedTtd = 10;
            IBlockTree blockTree = Substitute.For<IBlockTree>();

            ChainSpecFileLoader loader = new(new EthereumJsonSerializer(), LimboLogs.Instance);
            string path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "Specs/test_spec.json");
            ChainSpec chainSpec = loader.LoadEmbeddedOrFromFile(path);

            ChainSpecBasedSpecProvider specProvider = new(chainSpec);
            PoSSwitcher poSSwitcher = new(new MergeConfig(), new SyncConfig(), new MemDb(), blockTree, specProvider, new ChainSpec(), LimboLogs.Instance);

            Assert.That(poSSwitcher.TerminalTotalDifficulty, Is.EqualTo(expectedTtd));
            Assert.That(specProvider.MergeBlockNumber?.BlockNumber, Is.EqualTo(101));
        }

        [Test]
        public void GetBlockSwitchInfo_returns_post_merge_for_ttd_zero_genesis_without_total_difficulty()
        {
            ChainSpecFileLoader loader = new(new EthereumJsonSerializer(), LimboLogs.Instance);
            ChainSpec chainSpec = loader.LoadEmbeddedOrFromFile(FindHoodiChainSpecPath());
            ChainSpecBasedSpecProvider specProvider = new(chainSpec);
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            PoSSwitcher poSSwitcher = new(new MergeConfig(), new SyncConfig(), new MemDb(), blockTree, specProvider, chainSpec, LimboLogs.Instance);

            Block genesis = Build.A.Block.Genesis
                .WithTimestamp(1_742_212_800)
                .WithDifficulty(BlockHeaderBuilder.DefaultDifficulty)
                .WithTotalDifficulty((UInt256?)null)
                .TestObject;

            (bool isTerminal, bool isPostMerge) = poSSwitcher.GetBlockConsensusInfo(genesis.Header);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(chainSpec.Parameters.TerminalTotalDifficulty?.IsZero, Is.True);
                Assert.That(specProvider.TerminalTotalDifficulty?.IsZero, Is.True);
                Assert.That(specProvider.MergeBlockNumber?.BlockNumber, Is.EqualTo(0));
                Assert.That(isTerminal, Is.False);
                Assert.That(isPostMerge, Is.True);
                Assert.That(genesis.Header.IsPostMerge, Is.True);
            }
        }

        [Test]
        public void GetBlockSwitchInfo_does_not_mark_genesis_post_merge_for_config_only_ttd_zero()
        {
            TestSpecProvider specProvider = new(London.Instance);
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            ChainSpec chainSpec = new();
            PoSSwitcher poSSwitcher = new(new MergeConfig() { TerminalTotalDifficulty = "0" }, new SyncConfig(), new MemDb(), blockTree, specProvider, chainSpec, LimboLogs.Instance);

            Block genesis = Build.A.Block.Genesis
                .WithDifficulty(BlockHeaderBuilder.DefaultDifficulty)
                .WithTotalDifficulty((UInt256?)null)
                .TestObject;

            (bool isTerminal, bool isPostMerge) = poSSwitcher.GetBlockConsensusInfo(genesis.Header);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(specProvider.TerminalTotalDifficulty?.IsZero, Is.True);
                Assert.That(chainSpec.Parameters?.TerminalTotalDifficulty?.IsZero, Is.Not.True);
                Assert.That(isTerminal, Is.False);
                Assert.That(isPostMerge, Is.False);
                Assert.That(genesis.Header.IsPostMerge, Is.False);
            }
        }

        [TestCase(5000000)]
        [TestCase(4900000)]
        public void IsTerminalBlock_returning_expected_results(long terminalTotalDifficulty)
        {
            Block genesisBlock = Build.A.Block.WithNumber(0).TestObject;
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.TerminalTotalDifficulty = (UInt256)terminalTotalDifficulty;
            BlockTree blockTree = Build.A.BlockTree(genesisBlock, specProvider).OfChainLength(6).TestObject;
            _ = CreatePosSwitcher(blockTree, new MemDb(), specProvider);

            BlockHeader? block3 = blockTree.FindHeader(3, BlockTreeLookupOptions.All);
            BlockHeader? block4 = blockTree.FindHeader(4, BlockTreeLookupOptions.All);
            BlockHeader? block5 = blockTree.FindHeader(5, BlockTreeLookupOptions.All);
            Block blockWithPostMergeFlag = Build.A.Block.WithNumber(4).WithDifficulty(0).WithPostMergeFlag(true)
                                            .WithParent(block3!).TestObject;
            Assert.That(block3!.IsTerminalBlock(specProvider), Is.EqualTo(false)); // PoWBlock
            Assert.That(block4!.IsTerminalBlock(specProvider), Is.EqualTo(true)); // terminal block
            Assert.That(block5!.IsTerminalBlock(specProvider), Is.EqualTo(false)); // incorrect PoW not terminal block
            Assert.That(blockWithPostMergeFlag.IsTerminalBlock(specProvider), Is.EqualTo(false)); // block with post merge flag
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
            _ = CreatePosSwitcher(blockTree, new MemDb(), specProvider);

            Assert.That(genesisBlock.IsTerminalBlock(specProvider), Is.EqualTo(expectedResult));
        }

        [Test]
        public void Override_TTD_and_number_from_merge_config()
        {
            UInt256 expectedTtd = 340;
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.UpdateMergeTransitionInfo(100, 20);
            PoSSwitcher poSSwitcher = new(new MergeConfig() { TerminalTotalDifficulty = "340", TerminalBlockNumber = 2000 }, new SyncConfig(), new MemDb(), blockTree, specProvider, new ChainSpec(), LimboLogs.Instance);

            Assert.That(poSSwitcher.TerminalTotalDifficulty, Is.EqualTo(expectedTtd));
            Assert.That(specProvider.MergeBlockNumber?.BlockNumber, Is.EqualTo(2001));
        }

        [Test]
        public void Can_update_merge_transition_info()
        {
            UInt256 expectedTtd = 340;
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.UpdateMergeTransitionInfo(2001, expectedTtd);
            PoSSwitcher poSSwitcher = new(new MergeConfig() { }, new SyncConfig(), new MemDb(), blockTree, specProvider, new ChainSpec(), LimboLogs.Instance);

            Assert.That(poSSwitcher.TerminalTotalDifficulty, Is.EqualTo(expectedTtd));
            Assert.That(specProvider.MergeBlockNumber?.BlockNumber, Is.EqualTo(2001));
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
            Assert.That(poSSwitcher.GetBlockConsensusInfo(block3!), Is.EqualTo((false, false))); // PoWBlock
            Assert.That(poSSwitcher.GetBlockConsensusInfo(block4!), Is.EqualTo((true, false))); // terminal block
            Assert.That(poSSwitcher.GetBlockConsensusInfo(block5!), Is.EqualTo((false, true))); // incorrect PoW, TTD > TD and it is not terminal, so we should process it in the same way like post merge blocks
            Assert.That(poSSwitcher.GetBlockConsensusInfo(blockWithPostMergeFlag.Header), Is.EqualTo((false, true))); // block with post merge flag
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
            Assert.That(poSSwitcher.GetBlockConsensusInfo(block3!), Is.EqualTo((false, false))); // PoWBlock
            Assert.That(poSSwitcher.GetBlockConsensusInfo(block4!), Is.EqualTo((false, false))); // terminal block
            Assert.That(poSSwitcher.GetBlockConsensusInfo(block5!), Is.EqualTo((false, false)));
            Assert.That(poSSwitcher.GetBlockConsensusInfo(blockWithPostMergeFlag.Header), Is.EqualTo((false, true))); // block with post merge flag
        }

        [Test]
        public void New_terminal_block_when_ttd_reached()
        {
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.TerminalTotalDifficulty = 5000000;
            Block genesisBlock = Build.A.Block.WithNumber(0).TestObject;
            BlockTree blockTree = Build.A.BlockTree(genesisBlock, specProvider).OfChainLength(4).TestObject;

            PoSSwitcher poSSwitcher = CreatePosSwitcher(blockTree, new MemDb(), specProvider);

            Assert.That(poSSwitcher.HasEverReachedTerminalBlock(), Is.EqualTo(false));
            Block block = Build.A.Block.WithTotalDifficulty(5000000L).WithNumber(4).WithParent(blockTree.Head!).WithDifficulty(1000000L).TestObject;
            Block alternativeTerminalBlock = Build.A.Block.WithTotalDifficulty(5000000L).WithNumber(4).WithParent(blockTree.Head!).WithGasLimit(20000000).WithDifficulty(1000000L).TestObject;
            blockTree.SuggestBlock(block);
            blockTree.TryUpdateMainChain(block.Header, true, preloadedBlocks: new[] { block });
            Assert.That(poSSwitcher.HasEverReachedTerminalBlock(), Is.EqualTo(true));
            Assert.That(poSSwitcher.GetBlockConsensusInfo(alternativeTerminalBlock.Header), Is.EqualTo((true, false)));

            // A competing lower-difficulty branch crosses TTD one height later, so its own parent is still pre-TTD.
            Block laterBranchParent = CreatePreTerminalBlock(4);
            Block laterTerminalBlock = Build.A.Block
                .WithTotalDifficulty(5000000L)
                .WithParent(laterBranchParent.Header)
                .WithDifficulty(1000000L)
                .TestObject;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(laterTerminalBlock.Number, Is.EqualTo(blockTree.Head!.Number + 1));
                Assert.That(laterTerminalBlock.TotalDifficulty, Is.EqualTo(laterBranchParent.TotalDifficulty + laterTerminalBlock.Difficulty));
                Assert.That(poSSwitcher.GetBlockConsensusInfo(laterTerminalBlock.Header), Is.EqualTo((true, false)));
            }
        }

        [Test]
        public void Switch_when_TTD_is_reached()
        {
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.TerminalTotalDifficulty = 5000000;
            Block genesisBlock = Build.A.Block.WithNumber(0).TestObject;
            BlockTree blockTree = Build.A.BlockTree(genesisBlock, specProvider).OfChainLength(4).TestObject;

            PoSSwitcher poSSwitcher = CreatePosSwitcher(blockTree, new MemDb(), specProvider);

            Assert.That(poSSwitcher.HasEverReachedTerminalBlock(), Is.EqualTo(false));
            Block block = Build.A.Block.WithTotalDifficulty(5000000L).WithNumber(4).WithParent(blockTree.Head!).WithDifficulty(1000000L).TestObject;
            blockTree.SuggestBlock(block);
            blockTree.TryUpdateMainChain(block.Header, true, preloadedBlocks: new[] { block });

            Assert.That(poSSwitcher.HasEverReachedTerminalBlock(), Is.EqualTo(true));
        }

        [Test]
        public void Final_total_difficulty_from_config_does_not_mark_terminal_block_as_reached()
        {
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.TerminalTotalDifficulty = 5000000;
            Block genesisBlock = Build.A.Block.WithNumber(0).TestObject;
            BlockTree blockTree = Build.A.BlockTree(genesisBlock, specProvider).OfChainLength(4).TestObject;
            MergeConfig mergeConfig = new() { FinalTotalDifficulty = "5000000" };
            PoSSwitcher poSSwitcher = new(mergeConfig, new SyncConfig(), new MemDb(), blockTree, specProvider, new ChainSpec { Genesis = genesisBlock }, LimboLogs.Instance);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(poSSwitcher.HasEverReachedTerminalBlock(), Is.False);
                Assert.That(poSSwitcher.FinalTotalDifficulty, Is.EqualTo((UInt256?)5000000));
                Assert.That(poSSwitcher.TransitionFinished, Is.True);
            }
        }

        [Test]
        public void Reaches_and_persists_terminal_block_with_final_total_difficulty_in_config()
        {
            using MemDb metadataDb = new();
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.TerminalTotalDifficulty = 5000000;
            Block genesisBlock = Build.A.Block.WithNumber(0).TestObject;
            BlockTree blockTree = Build.A.BlockTree(genesisBlock, specProvider).OfChainLength(4).TestObject;
            MergeConfig mergeConfig = new() { FinalTotalDifficulty = "5000000" };
            PoSSwitcher poSSwitcher = new(mergeConfig, new SyncConfig(), metadataDb, blockTree, specProvider, new ChainSpec { Genesis = genesisBlock }, LimboLogs.Instance);
            int terminalBlockReachedCount = 0;
            poSSwitcher.TerminalBlockReached += (_, _) => terminalBlockReachedCount++;

            Assert.That(poSSwitcher.HasEverReachedTerminalBlock(), Is.False);
            Block terminalBlock = Build.A.Block.WithTotalDifficulty(5000000L).WithNumber(4).WithParent(blockTree.Head!).WithDifficulty(1000000L).TestObject;
            blockTree.SuggestBlock(terminalBlock);
            blockTree.TryUpdateMainChain(terminalBlock.Header, true, preloadedBlocks: new[] { terminalBlock });

            using (Assert.EnterMultipleScope())
            {
                Assert.That(poSSwitcher.HasEverReachedTerminalBlock(), Is.True);
                Assert.That(terminalBlockReachedCount, Is.EqualTo(1));
                Assert.That(specProvider.MergeBlockNumber?.BlockNumber, Is.EqualTo(5));
            }

            IBlockTree restartedBlockTree = Substitute.For<IBlockTree>();
            restartedBlockTree.BestSuggestedHeader.Returns(CreatePreTerminalBlock(0).Header);
            TestSpecProvider restartedSpecProvider = new(London.Instance) { TerminalTotalDifficulty = 5000000L };
            PoSSwitcher restarted = new(mergeConfig, new SyncConfig(), metadataDb, restartedBlockTree, restartedSpecProvider, new ChainSpec(), LimboLogs.Instance);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(MatchesTerminalBlock(ReadTerminalMetadata(metadataDb), terminalBlock), Is.True);
                Assert.That(restartedSpecProvider.MergeBlockNumber?.BlockNumber, Is.EqualTo(5));
                Assert.That(restarted.HasEverReachedTerminalBlock(), Is.True);
            }
        }

        [Test]
        public void Local_chain_above_ttd_marks_terminal_block_as_reached_without_terminal_block_metadata()
        {
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.TerminalTotalDifficulty = 3000000;
            Block genesisBlock = Build.A.Block.WithNumber(0).TestObject;
            BlockTree blockTree = Build.A.BlockTree(genesisBlock, specProvider).OfChainLength(4).TestObject;
            MergeConfig mergeConfig = new() { FinalTotalDifficulty = "3000000" };
            PoSSwitcher poSSwitcher = new(mergeConfig, new SyncConfig(), new MemDb(), blockTree, specProvider, new ChainSpec { Genesis = genesisBlock }, LimboLogs.Instance);

            Assert.That(poSSwitcher.HasEverReachedTerminalBlock(), Is.True);
        }

        [Test]
        public void Local_ttd_evidence_at_initialization_does_not_suppress_terminal_block_notification()
        {
            TestSpecProvider specProvider = new(London.Instance) { TerminalTotalDifficulty = 5000000 };
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            blockTree.Head.Returns(CreatePostTerminalBlock(5));
            PoSSwitcher poSSwitcher = new(new MergeConfig(), new SyncConfig(), new MemDb(), blockTree, specProvider, new ChainSpec(), LimboLogs.Instance);
            int terminalBlockReachedCount = 0;
            poSSwitcher.TerminalBlockReached += (_, _) => terminalBlockReachedCount++;

            bool updated = poSSwitcher.TryUpdateTerminalBlock(CreateTerminalBlock(5).Header);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(poSSwitcher.HasEverReachedTerminalBlock(), Is.True);
                Assert.That(updated, Is.True);
                Assert.That(terminalBlockReachedCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Best_suggested_header_is_not_durable_ttd_evidence()
        {
            TestSpecProvider specProvider = new(London.Instance) { TerminalTotalDifficulty = 5000000 };
            Block genesisBlock = Build.A.Block.WithNumber(0).TestObject;
            BlockTree blockTree = Build.A.BlockTree(genesisBlock, specProvider).OfChainLength(4).TestObject;
            PoSSwitcher poSSwitcher = CreatePosSwitcher(blockTree, new MemDb(), specProvider);
            Block terminalBlock = Build.A.Block.WithTotalDifficulty(5000000L).WithParent(blockTree.Head!).WithDifficulty(1000000L).TestObject;

            // Suggested but not processed, so the candidate can still be invalidated or reorged away.
            blockTree.SuggestBlock(terminalBlock);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(blockTree.BestSuggestedHeader?.Hash, Is.EqualTo(terminalBlock.Hash));
                Assert.That(poSSwitcher.HasEverReachedTerminalBlock(), Is.False);
            }

            blockTree.TryUpdateMainChain(terminalBlock.Header, true, preloadedBlocks: new[] { terminalBlock });

            // Processing it flips the answer through the NewHeadBlock subscription recording the metadata, not
            // through the head's total difficulty; that branch is covered by
            // Local_chain_above_ttd_marks_terminal_block_as_reached_without_terminal_block_metadata.
            Assert.That(poSSwitcher.HasEverReachedTerminalBlock(), Is.True);
        }

        [Test]
        public void Committed_terminal_head_is_recorded_when_terminal_metadata_is_missing()
        {
            using MemDb metadataDb = new();
            TestSpecProvider specProvider = new(London.Instance) { TerminalTotalDifficulty = 5000000 };
            Block genesisBlock = Build.A.Block.WithNumber(0).TestObject;
            BlockTree blockTree = Build.A.BlockTree(genesisBlock, specProvider).OfChainLength(4).TestObject;
            Block terminalBlock = Build.A.Block.WithTotalDifficulty(5000000L).WithParent(blockTree.Head!).WithDifficulty(1000000L).TestObject;

            // The head hash is committed before NewHeadBlock is raised, so a crash in that window leaves a
            // terminal head that no later event announces again.
            blockTree.SuggestBlock(terminalBlock);
            blockTree.TryUpdateMainChain(terminalBlock.Header, true, preloadedBlocks: new[] { terminalBlock });
            Assert.That(metadataDb.KeyExists(MetadataDbKeys.TerminalPoWNumber), Is.False);

            PoSSwitcher poSSwitcher = CreatePosSwitcher(blockTree, metadataDb, specProvider);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(MatchesTerminalBlock(ReadTerminalMetadata(metadataDb), terminalBlock), Is.True);
                Assert.That(specProvider.MergeBlockNumber?.BlockNumber, Is.EqualTo(terminalBlock.Number + 1));
                Assert.That(poSSwitcher.HasEverReachedTerminalBlock(), Is.True);
            }
        }

        [Test]
        public void Terminal_block_behind_a_committed_head_is_recovered_by_walking_back()
        {
            using MemDb metadataDb = new();
            TestSpecProvider specProvider = new(London.Instance) { TerminalTotalDifficulty = 5000000 };
            Block genesisBlock = Build.A.Block.WithNumber(0).TestObject;
            BlockTree blockTree = Build.A.BlockTree(genesisBlock, specProvider).OfChainLength(4).TestObject;
            Block terminalBlock = Build.A.Block.WithTotalDifficulty(5000000L).WithParent(blockTree.Head!).WithDifficulty(1000000L).TestObject;
            Block firstPoS = Build.A.Block.WithTotalDifficulty(5000000L).WithParent(terminalBlock).WithDifficulty(0L).TestObject;
            Block secondPoS = Build.A.Block.WithTotalDifficulty(5000000L).WithParent(firstPoS).WithDifficulty(0L).TestObject;

            // One UpdateMainChain commits a whole branch and defers its events, so the durable head can be
            // several blocks past the terminal block when the process dies before those events are raised.
            foreach (Block block in new[] { terminalBlock, firstPoS, secondPoS })
                blockTree.SuggestBlock(block);
            blockTree.TryUpdateMainChain(secondPoS.Header, true, preloadedBlocks: new[] { terminalBlock, firstPoS, secondPoS });

            using (Assert.EnterMultipleScope())
            {
                Assert.That(metadataDb.KeyExists(MetadataDbKeys.TerminalPoWNumber), Is.False);
                Assert.That(blockTree.Head!.Number, Is.EqualTo(secondPoS.Number), "the head must be past the terminal block");
            }

            PoSSwitcher poSSwitcher = CreatePosSwitcher(blockTree, metadataDb, specProvider);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(MatchesTerminalBlock(ReadTerminalMetadata(metadataDb), terminalBlock), Is.True);
                Assert.That(specProvider.MergeBlockNumber?.BlockNumber, Is.EqualTo(terminalBlock.Number + 1));
                Assert.That(poSSwitcher.HasEverReachedTerminalBlock(), Is.True);
            }
        }

        [Test]
        public void Terminal_metadata_keys_are_persisted_in_a_single_write_batch()
        {
            using BatchTrackingTerminalMetadataDb metadataDb = new();
            TestSpecProvider specProvider = new(London.Instance) { TerminalTotalDifficulty = 5000000 };
            PoSSwitcher poSSwitcher = new(new MergeConfig(), new SyncConfig(), metadataDb, Substitute.For<IBlockTree>(), specProvider, new ChainSpec(), LimboLogs.Instance);

            bool updated = poSSwitcher.TryUpdateTerminalBlock(CreateTerminalBlock(4).Header);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(updated, Is.True);
                Assert.That(metadataDb.WriteBatchCount, Is.EqualTo(1));
                Assert.That(metadataDb.TerminalMetadataWritesInsideBatch, Is.EqualTo(2));
                Assert.That(metadataDb.TerminalMetadataWritesOutsideBatch, Is.Zero);
            }
        }

        [Test]
        public void Config_only_ttd_zero_genesis_is_not_persisted_as_terminal_pow()
        {
            using MemDb metadataDb = new();
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.UpdateMergeTransitionInfo(0, 0);
            // No ChainSpec.Parameters at all - the shape a command-line TTD or a devnet harness leaves behind,
            // and what MergeTestBlockchain builds. Classification still follows the chain spec, but nothing
            // may be persisted as a terminal PoW block.
            PoSSwitcher poSSwitcher = new(new MergeConfig { TerminalTotalDifficulty = "0" }, new SyncConfig(), metadataDb,
                Substitute.For<IBlockTree>(), specProvider, new ChainSpec(), LimboLogs.Instance);
            int terminalBlockReachedCount = 0;
            poSSwitcher.TerminalBlockReached += (_, _) => terminalBlockReachedCount++;
            Block genesis = Build.A.Block.Genesis.WithDifficulty(0).WithTotalDifficulty(0L).TestObject;

            bool updated = poSSwitcher.TryUpdateTerminalBlock(genesis.Header);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(updated, Is.False);
                Assert.That(terminalBlockReachedCount, Is.Zero);
                Assert.That(metadataDb.KeyExists(MetadataDbKeys.TerminalPoWNumber), Is.False);
                Assert.That(metadataDb.KeyExists(MetadataDbKeys.TerminalPoWHash), Is.False);
                Assert.That(specProvider.MergeBlockNumber?.BlockNumber, Is.EqualTo(0));
            }
        }

        [Test]
        public void Post_merge_genesis_is_not_persisted_as_terminal_pow()
        {
            using MemDb metadataDb = new();
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.UpdateMergeTransitionInfo(0, 0);
            ChainSpec chainSpec = new() { Parameters = new ChainParameters { TerminalTotalDifficulty = 0 } };
            PoSSwitcher poSSwitcher = new(new MergeConfig(), new SyncConfig(), metadataDb, Substitute.For<IBlockTree>(), specProvider, chainSpec, LimboLogs.Instance);
            int terminalBlockReachedCount = 0;
            poSSwitcher.TerminalBlockReached += (_, _) => terminalBlockReachedCount++;
            Block genesis = Build.A.Block.Genesis.WithDifficulty(0).WithTotalDifficulty(0L).TestObject;

            bool updated = poSSwitcher.TryUpdateTerminalBlock(genesis.Header);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(updated, Is.False);
                Assert.That(terminalBlockReachedCount, Is.Zero);
                Assert.That(metadataDb.KeyExists(MetadataDbKeys.TerminalPoWNumber), Is.False);
                Assert.That(metadataDb.KeyExists(MetadataDbKeys.TerminalPoWHash), Is.False);
                Assert.That(specProvider.MergeBlockNumber?.BlockNumber, Is.Zero);
            }
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Concurrent_terminal_candidates_serialize_metadata_writes_and_raise_one_notification(CancellationToken cancellationToken)
        {
            using BlockingTerminalMetadataDb metadataDb = new();
            TestSpecProvider specProvider = new(London.Instance) { TerminalTotalDifficulty = 5000000 };
            PoSSwitcher poSSwitcher = new(new MergeConfig(), new SyncConfig(), metadataDb, Substitute.For<IBlockTree>(), specProvider, new ChainSpec(), LimboLogs.Instance);
            Block firstTerminalBlock = CreateTerminalBlock(4);
            Block secondTerminalBlock = CreateTerminalBlock(5);
            int terminalBlockReachedCount = 0;
            poSSwitcher.TerminalBlockReached += (_, _) => Interlocked.Increment(ref terminalBlockReachedCount);

            Task<bool> firstUpdate = Task.Run(() => poSSwitcher.TryUpdateTerminalBlock(firstTerminalBlock.Header));
            TaskCompletionSource competingCandidateStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> secondUpdateCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread secondUpdateThread;
            try
            {
                await metadataDb.FirstTerminalNumberWrite.WaitAsync(cancellationToken);

                secondUpdateThread = StartOperation(
                    () => poSSwitcher.TryUpdateTerminalBlock(secondTerminalBlock.Header),
                    competingCandidateStarted,
                    secondUpdateCompletion);
                await competingCandidateStarted.Task.WaitAsync(cancellationToken);
                await AssertOperationBlocksWhileTerminalMetadataWriteIsPaused(secondUpdateThread, secondUpdateCompletion.Task, cancellationToken);
            }
            finally
            {
                metadataDb.ReleaseFirstTerminalNumberWrite();
            }

            bool firstUpdateResult = await firstUpdate.WaitAsync(cancellationToken);
            bool secondUpdateResult = await secondUpdateCompletion.Task.WaitAsync(cancellationToken);

            (ulong Number, Hash256? Hash) persistedTerminalMetadata = ReadTerminalMetadata(metadataDb);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(firstUpdateResult, Is.True);
                Assert.That(secondUpdateResult, Is.True);
                Assert.That(terminalBlockReachedCount, Is.EqualTo(1));
                Assert.That(MatchesTerminalBlock(persistedTerminalMetadata, secondTerminalBlock), Is.True);
                Assert.That(specProvider.MergeBlockNumber?.BlockNumber, Is.EqualTo(persistedTerminalMetadata.Number + 1));
            }
        }

        [Test]
        [CancelAfter(10000)]
        public async Task Terminal_update_and_finalization_are_serialized(CancellationToken cancellationToken)
        {
            using BlockingTerminalMetadataDb metadataDb = new();
            TestSpecProvider specProvider = new(London.Instance) { TerminalTotalDifficulty = 5000000 };
            PoSSwitcher poSSwitcher = new(new MergeConfig(), new SyncConfig(), metadataDb, Substitute.For<IBlockTree>(), specProvider, new ChainSpec(), LimboLogs.Instance);
            Block terminalBlock = CreateTerminalBlock(4);
            Block replacementTerminalBlock = CreateTerminalBlock(5);

            Task<bool> terminalUpdate = Task.Run(() => poSSwitcher.TryUpdateTerminalBlock(terminalBlock.Header));
            TaskCompletionSource finalizationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> finalizationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread finalizationThread;
            try
            {
                await metadataDb.FirstTerminalNumberWrite.WaitAsync(cancellationToken);

                finalizationThread = StartOperation(
                    () =>
                    {
                        poSSwitcher.ForkchoiceUpdated(terminalBlock.Header, TestItem.KeccakA);
                        return true;
                    },
                    finalizationStarted,
                    finalizationCompletion);
                await finalizationStarted.Task.WaitAsync(cancellationToken);
                await AssertOperationBlocksWhileTerminalMetadataWriteIsPaused(finalizationThread, finalizationCompletion.Task, cancellationToken);

                Assert.That(finalizationCompletion.Task.IsCompleted, Is.False);
            }
            finally
            {
                metadataDb.ReleaseFirstTerminalNumberWrite();
            }

            bool terminalUpdated = await terminalUpdate.WaitAsync(cancellationToken);
            await finalizationCompletion.Task.WaitAsync(cancellationToken);
            int terminalMetadataWriteCountBeforeReplacement = metadataDb.TerminalMetadataWriteCount;
            bool replacementUpdated = poSSwitcher.TryUpdateTerminalBlock(replacementTerminalBlock.Header);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(terminalUpdated, Is.True);
                Assert.That(poSSwitcher.TransitionFinished, Is.True);
                Assert.That(replacementUpdated, Is.False);
                Assert.That(metadataDb.TerminalMetadataWriteCount, Is.EqualTo(terminalMetadataWriteCountBeforeReplacement));
                Assert.That(MatchesTerminalBlock(ReadTerminalMetadata(metadataDb), terminalBlock), Is.True);
            }
        }

        [TestCase(0, 1, true)]
        [TestCase(5000, 6000, true)]
        [TestCase(5000, 4000, false)]
        public void Genesis_difficulty_reaching_ttd_marks_terminal_block_as_reached(long ttd, long genesisDifficulty, bool expected)
        {
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.TerminalTotalDifficulty = (UInt256)ttd;
            Block genesisBlock = Build.A.Block.WithNumber(0).WithDifficulty((UInt256)genesisDifficulty).TestObject;
            PoSSwitcher poSSwitcher = new(new MergeConfig(), new SyncConfig(), new MemDb(), Substitute.For<IBlockTree>(), specProvider, new ChainSpec { Genesis = genesisBlock }, LimboLogs.Instance);

            Assert.That(poSSwitcher.HasEverReachedTerminalBlock(), Is.EqualTo(expected));
        }

        [TestCase(5000, 6000, true)]
        [TestCase(5000, 4000, false)]
        public void Sync_pivot_above_ttd_marks_terminal_block_as_reached(long ttd, long pivotTotalDifficulty, bool expected)
        {
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.TerminalTotalDifficulty = (UInt256)ttd;
            SyncConfig syncConfig = new() { PivotTotalDifficulty = $"{(UInt256)pivotTotalDifficulty}" };
            PoSSwitcher poSSwitcher = new(new MergeConfig(), syncConfig, new MemDb(), Substitute.For<IBlockTree>(), specProvider, new ChainSpec(), LimboLogs.Instance);

            Assert.That(poSSwitcher.HasEverReachedTerminalBlock(), Is.EqualTo(expected));
        }

        [Test]
        public void No_final_difficulty_if_conditions_are_not_met() =>
            AssertFinalTotalDifficulty(10005, 10000, 10000, null);

        [TestCase(0, 1)]
        [TestCase(0, 0)]
        [TestCase(5000, 6000)]
        public void Can_set_final_total_difficulty_for_post_merge_networks(long ttd, long genesisDifficulty) =>
            AssertFinalTotalDifficulty(ttd, genesisDifficulty, null, genesisDifficulty);

        [TestCase(0, 1)]
        [TestCase(0, 0)]
        [TestCase(5000, 6000)]
        public void Can_set_final_total_difficulty_based_on_sync_pivot(long ttd, long pivotTotalDifficulty) =>
            AssertFinalTotalDifficulty(ttd, 0, pivotTotalDifficulty, pivotTotalDifficulty);

        private void AssertFinalTotalDifficulty(long ttd, long genesisDifficulty, long? pivotTotalDifficulty, long? expectedFinalTotalDifficulty)
        {
            TestSpecProvider specProvider = new(London.Instance);
            specProvider.TerminalTotalDifficulty = (UInt256)ttd;
            Block genesisBlock = Build.A.Block.WithNumber(0).WithDifficulty((UInt256)genesisDifficulty).TestObject;
            BlockTree blockTree = Build.A.BlockTree(genesisBlock, specProvider).OfChainLength(4).TestObject;
            SyncConfig syncConfig = new();
            if (pivotTotalDifficulty is not null)
                syncConfig = new SyncConfig() { PivotTotalDifficulty = $"{(UInt256)pivotTotalDifficulty}" };
            PoSSwitcher poSSwitcher = new(new MergeConfig(), syncConfig, new MemDb(), blockTree, specProvider, new ChainSpec() { Genesis = genesisBlock }, LimboLogs.Instance);
            if (expectedFinalTotalDifficulty is not null)
                Assert.That(poSSwitcher.FinalTotalDifficulty, Is.EqualTo((UInt256?)(UInt256)expectedFinalTotalDifficulty));
            else
                Assert.That(poSSwitcher.FinalTotalDifficulty, Is.Null);
        }

        private static Block CreatePreTerminalBlock(ulong number) =>
            Build.A.Block.WithNumber(number).WithTotalDifficulty(4000000L).WithDifficulty(1000000L).TestObject;

        private static Block CreateTerminalBlock(ulong number) =>
            Build.A.Block.WithNumber(number).WithTotalDifficulty(5000000L).WithDifficulty(1000000L).TestObject;

        // Above TTD with a parent already above TTD, so it is durable TTD evidence without being terminal itself.
        private static Block CreatePostTerminalBlock(ulong number) =>
            Build.A.Block.WithNumber(number).WithTotalDifficulty(6000000L).WithDifficulty(1000000L).TestObject;

        private static bool IsTerminalMetadataKey(ReadOnlySpan<byte> key) =>
            IsTerminalNumberKey(key) || key.Length == 1 && key[0] == MetadataDbKeys.TerminalPoWHash;

        private static bool IsTerminalNumberKey(ReadOnlySpan<byte> key) =>
            key.Length == 1 && key[0] == MetadataDbKeys.TerminalPoWNumber;

        private static (ulong Number, Hash256? Hash) ReadTerminalMetadata(IDb metadataDb)
        {
            RlpReader persistedNumberReader = new(metadataDb.Get(MetadataDbKeys.TerminalPoWNumber));
            RlpReader persistedHashReader = new(metadataDb.Get(MetadataDbKeys.TerminalPoWHash));
            return (persistedNumberReader.DecodeULong(), persistedHashReader.DecodeKeccak());
        }

        private static bool MatchesTerminalBlock((ulong Number, Hash256? Hash) terminalMetadata, Block block) =>
            terminalMetadata.Number == block.Header.Number && terminalMetadata.Hash == block.Header.Hash;

        private static Thread StartOperation(
            Func<bool> operation,
            TaskCompletionSource operationStarted,
            TaskCompletionSource<bool> operationCompletion)
        {
            Thread operationThread = new(() =>
            {
                operationStarted.TrySetResult();
                try
                {
                    operationCompletion.TrySetResult(operation());
                }
                catch (Exception exception)
                {
                    operationCompletion.TrySetException(exception);
                }
            })
            { IsBackground = true };
            operationThread.Start();
            return operationThread;
        }

        private static async Task AssertOperationBlocksWhileTerminalMetadataWriteIsPaused(
            Thread operationThread,
            Task operationCompletion,
            CancellationToken cancellationToken)
        {
            while ((operationThread.ThreadState & ThreadState.WaitSleepJoin) == 0)
            {
                if (operationCompletion.IsCompleted)
                {
                    await operationCompletion;
                    Assert.Fail("Operation completed while the terminal metadata write was paused.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }

        private sealed class BlockingTerminalMetadataDb : MemDb
        {
            private readonly TaskCompletionSource _firstTerminalNumberWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource _releaseFirstTerminalNumberWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _terminalMetadataWriteCount;
            private int _terminalNumberWriteCount;

            public Task FirstTerminalNumberWrite => _firstTerminalNumberWrite.Task;

            public int TerminalMetadataWriteCount => Volatile.Read(ref _terminalMetadataWriteCount);

            public void ReleaseFirstTerminalNumberWrite() => _releaseFirstTerminalNumberWrite.TrySetResult();

            public override void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
            {
                if (IsTerminalMetadataKey(key))
                {
                    Interlocked.Increment(ref _terminalMetadataWriteCount);
                    if (IsTerminalNumberKey(key) && Interlocked.CompareExchange(ref _terminalNumberWriteCount, 1, 0) == 0)
                    {
                        _firstTerminalNumberWrite.TrySetResult();
                        _releaseFirstTerminalNumberWrite.Task.GetAwaiter().GetResult();
                    }
                }

                base.Set(key, value, flags);
            }
        }

        private sealed class BatchTrackingTerminalMetadataDb : MemDb
        {
            public int WriteBatchCount { get; private set; }

            public int TerminalMetadataWritesInsideBatch { get; private set; }

            public int TerminalMetadataWritesOutsideBatch { get; private set; }

            public override IWriteBatch StartWriteBatch()
            {
                WriteBatchCount++;
                return new TrackingWriteBatch(this);
            }

            // Attribution is by the object the write arrives through, not by "a batch is open": a direct
            // put made while a batch happens to be open is not atomic with it and must not count as inside.
            public override void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
            {
                if (IsTerminalMetadataKey(key))
                    TerminalMetadataWritesOutsideBatch++;

                base.Set(key, value, flags);
            }

            private void SetThroughBatch(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags)
            {
                if (IsTerminalMetadataKey(key))
                    TerminalMetadataWritesInsideBatch++;

                base.Set(key, value, flags);
            }

            private sealed class TrackingWriteBatch(BatchTrackingTerminalMetadataDb owner) : IWriteBatch
            {
                public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) =>
                    owner.SetThroughBatch(key, value, flags);

                public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) =>
                    throw new NotSupportedException("Merging is not supported by this implementation.");

                public void Clear() { }

                public void Dispose() { }
            }
        }

        private static PoSSwitcher CreatePosSwitcher(IBlockTree blockTree, IDb? db = null, ISpecProvider? specProvider = null)
        {
            db ??= new MemDb();
            MergeConfig? mergeConfig = new() { };
            return new PoSSwitcher(mergeConfig, new SyncConfig(), db, blockTree, specProvider ?? MainnetSpecProvider.Instance, new ChainSpec(), LimboLogs.Instance);
        }

        private static string FindHoodiChainSpecPath()
        {
            string? directory = TestContext.CurrentContext.WorkDirectory;
            while (directory is not null)
            {
                string path = Path.Combine(directory, "Chains", "hoodi.json");
                if (File.Exists(path))
                    return path;

                directory = Directory.GetParent(directory)?.FullName;
            }

            throw new FileNotFoundException($"Could not find Chains/hoodi.json from {TestContext.CurrentContext.WorkDirectory}.");
        }
    }
}
