// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Headers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Visitors;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Repositories;
using Nethermind.Db.Blooms;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class BlockTreeTests
    {
        private TestMemDb _blocksInfosDb = null!;
        private TestMemDb _headersDb = null!;
        private TestMemDb _blocksDb = null!;

        private BlockTree BuildBlockTree()
        {
            _blocksDb = new TestMemDb();
            _headersDb = new TestMemDb();
            _blocksInfosDb = new TestMemDb();
            BlockTreeBuilder builder = Build.A.BlockTree()
                .WithBlocksDb(_blocksDb)
                .WithHeadersDb(_headersDb)
                .WithBlockInfoDb(_blocksInfosDb)
                .WithoutSettingHead;
            return builder.TestObject;
        }

        private static void AddToMain(BlockTree blockTree, Block block0)
        {
            blockTree.SuggestBlock(block0);
            blockTree.UpdateMainChain(new[] { block0 }, true);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Add_genesis_shall_notify()
        {
            bool hasNotified = false;
            BlockTree blockTree = BuildBlockTree();
            blockTree.NewHeadBlock += (_, _) => { hasNotified = true; };

            bool hasNotifiedNewSuggested = false;
            blockTree.NewSuggestedBlock += (_, _) => { hasNotifiedNewSuggested = true; };

            Block block = Build.A.Block.WithNumber(0).TestObject;
            AddBlockResult result = blockTree.SuggestBlock(block);
            blockTree.UpdateMainChain(block);

            Assert.True(hasNotified, "notification");
            Assert.That(result, Is.EqualTo(AddBlockResult.Added), "result");
            Assert.True(hasNotifiedNewSuggested, "NewSuggestedBlock");
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Add_genesis_shall_work_even_with_0_difficulty()
        {
            bool hasNotified = false;
            BlockTree blockTree = BuildBlockTree();
            blockTree.NewBestSuggestedBlock += (_, _) => { hasNotified = true; };

            bool hasNotifiedNewSuggested = false;
            blockTree.NewSuggestedBlock += (_, _) => { hasNotifiedNewSuggested = true; };

            Block block = Build.A.Block.WithNumber(0).WithDifficulty(0).TestObject;
            AddBlockResult result = blockTree.SuggestBlock(block);

            Assert.True(hasNotified, "notification");
            Assert.That(result, Is.EqualTo(AddBlockResult.Added), "result");
            Assert.True(hasNotifiedNewSuggested, "NewSuggestedBlock");
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Suggesting_genesis_many_times_does_not_cause_any_trouble()
        {
            BlockTree blockTree = BuildBlockTree();
            Block blockA = Build.A.Block.WithNumber(0).TestObject;
            Block blockB = Build.A.Block.WithNumber(0).TestObject;
            blockTree.SuggestBlock(blockA).Should().Be(AddBlockResult.Added);
            blockTree.SuggestBlock(blockB).Should().Be(AddBlockResult.AlreadyKnown);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Shall_notify_on_new_head_block_after_genesis()
        {
            bool hasNotified = false;
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            blockTree.SuggestBlock(block0);
            blockTree.NewHeadBlock += (_, _) => { hasNotified = true; };

            bool hasNotifiedNewSuggested = false;
            blockTree.NewSuggestedBlock += (_, _) => { hasNotifiedNewSuggested = true; };

            AddBlockResult result = blockTree.SuggestBlock(block1);
            blockTree.UpdateMainChain(block1);

            Assert.True(hasNotified, "notification");
            Assert.That(result, Is.EqualTo(AddBlockResult.Added), "result");
            Assert.True(hasNotifiedNewSuggested, "NewSuggestedBlock");
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Shall_notify_new_head_block_once_and_block_added_to_main_multiple_times_when_adding_multiple_blocks_at_once()
        {
            int newHeadBlockNotifications = 0;
            int blockAddedToMainNotifications = 0;

            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(0).WithParent(block1).TestObject;
            Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(0).WithParent(block2).TestObject;

            blockTree.SuggestBlock(block0);
            blockTree.NewHeadBlock += (_, _) => { newHeadBlockNotifications++; };
            blockTree.BlockAddedToMain += (_, _) => { blockAddedToMainNotifications++; };

            blockTree.SuggestBlock(block1);
            blockTree.SuggestBlock(block2);
            blockTree.SuggestBlock(block3);
            blockTree.UpdateMainChain(new[] { block1, block2, block3 }, true);

            newHeadBlockNotifications.Should().Be(1, "new head block");
            blockAddedToMainNotifications.Should().Be(3, "block added to main");
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Shall_notify_on_new_suggested_block_after_genesis()
        {
            bool hasNotified = false;
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            blockTree.SuggestBlock(block0);
            blockTree.NewBestSuggestedBlock += (_, _) => { hasNotified = true; };

            bool hasNotifiedNewSuggested = false;
            blockTree.NewSuggestedBlock += (_, _) => { hasNotifiedNewSuggested = true; };

            AddBlockResult result = blockTree.SuggestBlock(block1);

            Assert.True(hasNotified, "notification");
            Assert.That(result, Is.EqualTo(AddBlockResult.Added), "result");
            Assert.True(hasNotifiedNewSuggested, "NewSuggestedBlock");
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Shall_not_notify_but_add_on_lower_difficulty()
        {
            bool hasNotifiedBest = false;
            bool hasNotifiedHead = false;
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(3).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            blockTree.SuggestBlock(block0);
            blockTree.SuggestBlock(block1);
            blockTree.NewHeadBlock += (_, _) => { hasNotifiedHead = true; };
            blockTree.NewBestSuggestedBlock += (_, _) => { hasNotifiedBest = true; };

            bool hasNotifiedNewSuggested = false;
            blockTree.NewSuggestedBlock += (_, _) => { hasNotifiedNewSuggested = true; };

            AddBlockResult result = blockTree.SuggestBlock(block2);

            Assert.False(hasNotifiedBest, "notification best");
            Assert.False(hasNotifiedHead, "notification head");
            Assert.That(result, Is.EqualTo(AddBlockResult.Added), "result");
            Assert.True(hasNotifiedNewSuggested, "NewSuggestedBlock");
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Shall_ignore_orphans()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).TestObject;
            blockTree.SuggestBlock(block0);
            AddBlockResult result = blockTree.SuggestBlock(block2);
            Assert.That(result, Is.EqualTo(AddBlockResult.UnknownParent));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Shall_ignore_known()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            blockTree.SuggestBlock(block0);
            blockTree.SuggestBlock(block1);
            AddBlockResult result = blockTree.SuggestBlock(block1);
            Assert.That(result, Is.EqualTo(AddBlockResult.AlreadyKnown));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Cleans_invalid_blocks_before_starting()
        {
            MemDb blockInfosDb = new MemDb();
            BlockTreeBuilder builder = Build.A.BlockTree()
                .WithBlockInfoDb(blockInfosDb)
                .WithoutSettingHead;
            IBlockStore blockStore = builder.BlockStore;
            BlockTree tree = builder.TestObject;

            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
            Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;

            tree.SuggestBlock(block0);
            tree.SuggestBlock(block1);
            tree.SuggestBlock(block2);
            tree.SuggestBlock(block3);

            blockInfosDb.Set(BlockTree.DeletePointerAddressInDb, block1.Hash!.Bytes);
            BlockTree tree2 = Build.A.BlockTree()
                .WithoutSettingHead
                .WithDatabaseFrom(builder)
                .TestObject;

            Assert.That(tree2.BestKnownNumber, Is.EqualTo(0L), "best known");
            Assert.That(tree2.Head, Is.EqualTo(null), "head");
            Assert.That(tree2.BestSuggestedHeader!.Number, Is.EqualTo(0L), "suggested");

            Assert.IsNull(blockStore.Get(block2.Number, block2.Hash!), "block 1");
            Assert.IsNull(blockStore.Get(block2.Number, block2.Hash!), "block 2");
            Assert.IsNull(blockStore.Get(block3.Number, block3.Hash!), "block 3");

            Assert.IsNull(blockInfosDb.Get(2), "level 1");
            Assert.IsNull(blockInfosDb.Get(2), "level 2");
            Assert.IsNull(blockInfosDb.Get(3), "level 3");
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void When_cleaning_descendants_of_invalid_does_not_touch_other_branches()
        {
            MemDb blockInfosDb = new();
            BlockTreeBuilder builder = Build.A.BlockTree()
                .WithBlockInfoDb(blockInfosDb)
                .WithoutSettingHead;
            IBlockStore blockStore = builder.BlockStore;
            BlockTree tree = builder.TestObject;

            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
            Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;

            Block block1B = Build.A.Block.WithNumber(1).WithDifficulty(1).WithParent(block0).TestObject;
            Block block2B = Build.A.Block.WithNumber(2).WithDifficulty(1).WithParent(block1B).TestObject;
            Block block3B = Build.A.Block.WithNumber(3).WithDifficulty(1).WithParent(block2B).TestObject;

            tree.SuggestBlock(block0);
            tree.SuggestBlock(block1);
            tree.SuggestBlock(block2);
            tree.SuggestBlock(block3);

            tree.SuggestBlock(block1B);
            tree.SuggestBlock(block2B);
            tree.SuggestBlock(block3B);

            blockInfosDb.Set(BlockTree.DeletePointerAddressInDb, block1.Hash!.Bytes);
            BlockTree tree2 = Build.A.BlockTree()
                .WithoutSettingHead
                .WithDatabaseFrom(builder)
                .TestObject;

            Assert.That(tree2.BestKnownNumber, Is.EqualTo(3L), "best known");
            Assert.That(tree2.Head, Is.EqualTo(null), "head");
            Assert.That(tree2.BestSuggestedHeader!.Hash, Is.EqualTo(block3B.Hash), "suggested");

            blockStore.Get(block1.Number, block1.Hash!).Should().BeNull("block 1");
            blockStore.Get(block2.Number, block2.Hash!).Should().BeNull("block 2");
            blockStore.Get(block3.Number, block3.Hash!).Should().BeNull("block 3");

            Assert.NotNull(blockInfosDb.Get(1), "level 1");
            Assert.NotNull(blockInfosDb.Get(2), "level 2");
            Assert.NotNull(blockInfosDb.Get(3), "level 3");
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_load_best_known_up_to_256million()
        {
            _blocksDb = new TestMemDb();
            _headersDb = new TestMemDb();
            TestMemDb blocksInfosDb = new TestMemDb();

            Rlp chainLevel = Rlp.Encode(new ChainLevelInfo(true, new BlockInfo(TestItem.KeccakA, 1)));
            blocksInfosDb.ReadFunc = (key) =>
            {
                if (!Bytes.AreEqual(key, BlockTree.DeletePointerAddressInDb.Bytes))
                {
                    return chainLevel.Bytes;
                }

                return null!;
            };

            BlockTree blockTree = Build.A.BlockTree()
                .WithoutSettingHead
                .WithBlocksDb(_blocksDb)
                .WithHeadersDb(_headersDb)
                .WithBlockInfoDb(blocksInfosDb)
                .TestObject;

            Assert.That(blockTree.BestKnownNumber, Is.EqualTo(256000000));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Add_and_find_branch()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block = Build.A.Block.TestObject;
            blockTree.SuggestBlock(block);
            Block? found = blockTree.FindBlock(block.Hash, BlockTreeLookupOptions.None);
            Assert.That(found?.Header.CalculateHash(), Is.EqualTo(block.Hash));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Add_on_branch_move_find()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block = Build.A.Block.TestObject;
            AddToMain(blockTree, block);
            Block? found = blockTree.FindBlock(block.Hash, BlockTreeLookupOptions.RequireCanonical);
            Assert.That(found?.Header.CalculateHash(), Is.EqualTo(block.Hash));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Add_on_branch_move_find_via_block_finder_interface()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block = Build.A.Block.TestObject;
            AddToMain(blockTree, block);
            Block? found = ((IBlockFinder)blockTree).FindBlock(new BlockParameter(block.Hash!, true));
            Assert.That(found?.Header.CalculateHash(), Is.EqualTo(block.Hash));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Add_on_branch_and_not_find_on_main()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block = Build.A.Block.TestObject;
            blockTree.SuggestBlock(block);
            Block? found = blockTree.FindBlock(block.Hash, BlockTreeLookupOptions.RequireCanonical);
            Assert.IsNull(found);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Add_on_branch_and_not_find_on_main_via_block_finder_interface()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block = Build.A.Block.TestObject;
            blockTree.SuggestBlock(block);
            Block? found = ((IBlockFinder)blockTree).FindBlock(new BlockParameter(block.Hash!, true));
            Assert.IsNull(found);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Find_by_number_basic()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);
            AddToMain(blockTree, block2);

            Block? found = blockTree.FindBlock(2, BlockTreeLookupOptions.None);
            Assert.That(found?.Header.CalculateHash(), Is.EqualTo(block2.Hash));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Find_by_number_beyond_what_is_known_returns_null()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);
            AddToMain(blockTree, block2);

            Block? found = blockTree.FindBlock(1920000, BlockTreeLookupOptions.None);
            Assert.Null(found);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Find_by_number_returns_null_when_block_is_missing()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);

            Block? found = blockTree.FindBlock(5, BlockTreeLookupOptions.None);
            Assert.IsNull(found);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Find_headers_basic()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);
            AddToMain(blockTree, block2);

            BlockHeader[] headers = blockTree.FindHeaders(block0.Hash, 2, 0, false);
            Assert.That(headers.Length, Is.EqualTo(2));
            Assert.That(headers[0].Hash, Is.EqualTo(block0.Hash));
            Assert.That(headers[1].Hash, Is.EqualTo(block1.Hash));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Find_headers_skip()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);
            AddToMain(blockTree, block2);

            BlockHeader[] headers = blockTree.FindHeaders(block0.Hash, 2, 1, false);
            Assert.That(headers.Length, Is.EqualTo(2));
            Assert.That(headers[0].Hash, Is.EqualTo(block0.Hash));
            Assert.That(headers[1].Hash, Is.EqualTo(block2.Hash));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Find_headers_reverse()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            Block block3 = Build.A.Block.WithNumber(3).WithParent(block2).TestObject;
            Block block4 = Build.A.Block.WithNumber(4).WithParent(block3).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);
            AddToMain(blockTree, block2);
            AddToMain(blockTree, block3);
            AddToMain(blockTree, block4);

            BlockHeader[] headers = blockTree.FindHeaders(block2.Hash, 2, 0, true);
            Assert.That(headers.Length, Is.EqualTo(2));
            Assert.That(headers[0].Hash, Is.EqualTo(block2.Hash));
            Assert.That(headers[1].Hash, Is.EqualTo(block1.Hash));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Find_headers_reverse_skip()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);
            AddToMain(blockTree, block2);

            BlockHeader[] headers = blockTree.FindHeaders(block2.Hash, 2, 1, true);
            Assert.That(headers.Length, Is.EqualTo(2));
            Assert.That(headers[0].Hash, Is.EqualTo(block2.Hash));
            Assert.That(headers[1].Hash, Is.EqualTo(block0.Hash));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Find_headers_reverse_below_zero()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);
            AddToMain(blockTree, block2);

            BlockHeader[] headers = blockTree.FindHeaders(block0.Hash, 2, 1, true);
            Assert.That(headers.Length, Is.EqualTo(2));
            Assert.That(headers[0].Hash, Is.EqualTo(block0.Hash));
            Assert.Null(headers[1]);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void When_finding_headers_does_not_find_a_header_it_breaks_the_loop()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);
            AddToMain(blockTree, block2);

            BlockHeader[] headers = blockTree.FindHeaders(block0.Hash, 100, 0, false);
            Assert.That(headers.Length, Is.EqualTo(100));
            Assert.That(headers[0].Hash, Is.EqualTo(block0.Hash));
            Assert.Null(headers[3]);

            Assert.That(_headersDb.ReadsCount, Is.EqualTo(0));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void When_finding_blocks_does_not_find_a_block_it_breaks_the_loop()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);
            AddToMain(blockTree, block2);

            BlockHeader[] headers = blockTree.FindHeaders(block0.Hash, 100, 0, false);
            Assert.That(headers.Length, Is.EqualTo(100));
            Assert.That(headers[0].Hash, Is.EqualTo(block0.Hash));
            Assert.Null(headers[3]);

            Assert.That(_headersDb.ReadsCount, Is.EqualTo(0));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Find_sequence_basic_longer()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);
            AddToMain(blockTree, block2);

            int length = 256;
            BlockHeader[] blocks = blockTree.FindHeaders(block0.Hash, length, 0, false);
            Assert.That(blocks.Length, Is.EqualTo(length));
            Assert.That(blocks[0].CalculateHash(), Is.EqualTo(block0.Hash));
            Assert.That(blocks[1].CalculateHash(), Is.EqualTo(block1.Hash));
            Assert.That(blocks[2].CalculateHash(), Is.EqualTo(block2.Hash));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Find_sequence_basic_shorter()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);
            AddToMain(blockTree, block2);

            int length = 2;
            BlockHeader[] blocks = blockTree.FindHeaders(block1.Hash, length, 0, false);
            Assert.That(blocks.Length, Is.EqualTo(length));
            Assert.That(blocks[0].CalculateHash(), Is.EqualTo(block1.Hash));
            Assert.That(blocks[1].CalculateHash(), Is.EqualTo(block2.Hash));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Find_sequence_basic()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);
            AddToMain(blockTree, block2);

            int length = 3;
            BlockHeader[] blocks = blockTree.FindHeaders(block0.Hash, length, 0, false);
            Assert.That(blocks.Length, Is.EqualTo(length));
            Assert.That(blocks[0].CalculateHash(), Is.EqualTo(block0.Hash));
            Assert.That(blocks[1].CalculateHash(), Is.EqualTo(block1.Hash));
            Assert.That(blocks[2].CalculateHash(), Is.EqualTo(block2.Hash));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Find_sequence_reverse()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);
            AddToMain(blockTree, block2);

            BlockHeader[] blocks = blockTree.FindHeaders(block2.Hash, 3, 0, true);
            Assert.That(blocks.Length, Is.EqualTo(3));

            Assert.That(blocks[0].CalculateHash(), Is.EqualTo(block2.Hash));
            Assert.That(blocks[2].CalculateHash(), Is.EqualTo(block0.Hash));
        }


        [Test, Timeout(Timeout.MaxTestTime)]
        public void Find_sequence_zero_blocks()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);
            AddToMain(blockTree, block2);

            BlockHeader[] blocks = blockTree.FindHeaders(block0.Hash, 0, 0, false);
            Assert.That(blocks.Length, Is.EqualTo(0));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Find_sequence_one_block()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);
            AddToMain(blockTree, block2);

            BlockHeader[] blocks = blockTree.FindHeaders(block2.Hash, 1, 0, false);
            Assert.That(blocks.Length, Is.EqualTo(1));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Find_sequence_basic_skip()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);
            AddToMain(blockTree, block2);

            BlockHeader[] blocks = blockTree.FindHeaders(block0.Hash, 2, 1, false);
            Assert.That(blocks.Length, Is.EqualTo(2), "length");
            Assert.That(blocks[0].CalculateHash(), Is.EqualTo(block0.Hash));
            Assert.That(blocks[1].CalculateHash(), Is.EqualTo(block2.Hash));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Find_sequence_some_empty()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);
            AddToMain(blockTree, block2);

            BlockHeader[] blocks = blockTree.FindHeaders(block0.Hash, 4, 0, false);
            Assert.That(blocks.Length, Is.EqualTo(4));
            Assert.IsNull(blocks[3]);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Total_difficulty_is_calculated_when_exists_parent_with_total_difficulty()
        {
            BlockTree blockTree = BuildBlockTree();

            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            blockTree.SuggestBlock(block0);
            Block block1 = Build.A.Block.WithNumber(1).WithParentHash(block0.Hash!).WithDifficulty(2).TestObject;
            blockTree.SuggestBlock(block1);
            block1.TotalDifficulty.Should().NotBeNull();
            Assert.That((int)block1.TotalDifficulty!, Is.EqualTo(3));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Total_difficulty_is_null_when_no_parent()
        {
            BlockTree blockTree = BuildBlockTree();

            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            blockTree.SuggestBlock(block0);

            Block block2 = Build.A.Block.WithNumber(1).WithDifficulty(3).WithParentHash(Keccak.Zero).TestObject;
            blockTree.SuggestBlock(block2);
            Assert.That(block2.TotalDifficulty, Is.EqualTo(null));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Head_block_gets_updated()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);

            Assert.That(blockTree.Head!.CalculateHash(), Is.EqualTo(block1.Hash));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Best_suggested_block_gets_updated()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            AddToMain(blockTree, block0);
            blockTree.SuggestBlock(block1);

            Assert.That(blockTree.Head!.CalculateHash(), Is.EqualTo(block0.Hash), "head block");
            Assert.That(blockTree.BestSuggestedHeader!.CalculateHash(), Is.EqualTo(block1.Hash), "best suggested");
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Sets_genesis_block()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            AddToMain(blockTree, block0);

            Assert.That(blockTree.Genesis!.CalculateHash(), Is.EqualTo(block0.Hash));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void ForkChoiceUpdated_update_hashes()
        {
            BlockTree blockTree = BuildBlockTree();
            Hash256 finalizedBlockHash = TestItem.KeccakB;
            Hash256 safeBlockHash = TestItem.KeccakC;
            blockTree.ForkChoiceUpdated(finalizedBlockHash, safeBlockHash);
            Assert.That(blockTree.FinalizedHash, Is.EqualTo(finalizedBlockHash));
            Assert.That(blockTree.SafeHash, Is.EqualTo(safeBlockHash));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Stores_multiple_blocks_per_level()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            Block block1B = Build.A.Block.WithNumber(1).WithDifficulty(3).WithParent(block0).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);
            blockTree.SuggestBlock(block1B);

            Block? found = blockTree.FindBlock(block1B.Hash, BlockTreeLookupOptions.None);

            Assert.That(found?.Header.CalculateHash(), Is.EqualTo(block1B.Hash));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_init_head_block_from_db_by_hash()
        {
            Block genesisBlock = Build.A.Block.Genesis.TestObject;
            Block headBlock = genesisBlock;

            BlockStore blockStore = new BlockStore(new MemDb());
            blockStore.Insert(genesisBlock);

            TestMemDb headersDb = new();
            headersDb.Set(genesisBlock.Hash!, Rlp.Encode(genesisBlock.Header).Bytes);

            TestMemDb blockInfosDb = new();
            blockInfosDb.Set(Keccak.Zero, genesisBlock.Hash!.Bytes);
            ChainLevelInfo level = new(true, new BlockInfo(headBlock.Hash!, headBlock.Difficulty));
            level.BlockInfos[0].WasProcessed = true;

            blockInfosDb.Set(0, Rlp.Encode(level).Bytes);

            BlockTree blockTree = Build.A.BlockTree()
                .WithoutSettingHead
                .WithBlockStore(blockStore)
                .WithHeadersDb(headersDb)
                .WithBlockInfoDb(blockInfosDb)
                .WithSpecProvider(OlympicSpecProvider.Instance)
                .TestObject;
            Assert.That(blockTree.Head?.Hash, Is.EqualTo(headBlock.Hash), "head");
            Assert.That(blockTree.Genesis?.Hash, Is.EqualTo(headBlock.Hash), "genesis");
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Sets_head_block_hash_in_db_on_new_head_block()
        {
            TestMemDb blockInfosDb = new();

            BlockTree blockTree = Build.A.BlockTree()
                .WithoutSettingHead
                .WithBlockInfoDb(blockInfosDb)
                .TestObject;

            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;

            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);

            Hash256 dec = new Hash256(blockInfosDb.Get(Keccak.Zero)!);
            Assert.That(dec, Is.EqualTo(block1.Hash));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_check_if_block_was_processed()
        {
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;

            BlockTree blockTree = BuildBlockTree();
            blockTree.SuggestBlock(block0);
            blockTree.SuggestBlock(block1);
            Assert.False(blockTree.WasProcessed(block1.Number, block1.Hash!), "before");
            blockTree.UpdateMainChain(new[] { block0, block1 }, true);
            Assert.True(blockTree.WasProcessed(block1.Number, block1.Hash!), "after");
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Best_known_number_is_set()
        {
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;

            BlockTree blockTree = BuildBlockTree();
            blockTree.SuggestBlock(block0);
            blockTree.SuggestBlock(block1);
            Assert.That(blockTree.BestKnownNumber, Is.EqualTo(1L));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Is_main_chain_returns_false_when_on_branch()
        {
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;

            BlockTree blockTree = BuildBlockTree();
            blockTree.SuggestBlock(block0);
            blockTree.SuggestBlock(block1);
            Assert.False(blockTree.IsMainChain(block1.Hash!));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Is_main_chain_returns_true_when_on_main()
        {
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;

            BlockTree blockTree = BuildBlockTree();
            blockTree.SuggestBlock(block0);
            blockTree.SuggestBlock(block1);
            blockTree.UpdateMainChain(block1);
            Assert.True(blockTree.IsMainChain(block1.Hash!));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Pending_returns_head()
        {
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;

            BlockTree blockTree = BuildBlockTree();
            blockTree.SuggestBlock(block0);
            blockTree.SuggestBlock(block1);
            blockTree.UpdateMainChain(block0);
            blockTree.BestSuggestedHeader.Should().Be(block1.Header);
            blockTree.PendingHash.Should().Be(block0.Hash!);
            ((IBlockFinder)blockTree).FindPendingHeader().Should().BeSameAs(block0.Header);
            ((IBlockFinder)blockTree).FindPendingBlock().Should().BeSameAs(block0);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Is_main_chain_returns_true_on_fast_sync_block()
        {
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            BlockTree blockTree = BuildBlockTree();
            blockTree.SuggestBlock(block0, BlockTreeSuggestOptions.None);
            blockTree.IsMainChain(block0.Hash!).Should().BeTrue();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Was_processed_returns_true_on_fast_sync_block()
        {
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            BlockTree blockTree = BuildBlockTree();
            blockTree.SuggestBlock(block0, BlockTreeSuggestOptions.None);
        }

        [Test(Description = "There was a bug where we switched positions and used the index from before the positions were switched"), Timeout(Timeout.MaxTestTime)]
        public void When_moving_to_main_one_of_the_two_blocks_at_given_level_the_was_processed_check_is_executed_on_the_correct_block_index_regression()
        {
            TestMemDb blockInfosDb = new();

            BlockTree blockTree = Build.A.BlockTree()
                .WithoutSettingHead
                .WithBlockInfoDb(blockInfosDb)
                .TestObject;
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(1).WithDifficulty(3).WithParent(block0).TestObject;

            AddToMain(blockTree, block0);

            blockTree.SuggestBlock(block2);
            blockTree.SuggestBlock(block1);
            blockTree.UpdateMainChain(block1);

            Hash256 storedInDb = new(blockInfosDb.Get(Keccak.Zero)!);
            Assert.That(storedInDb, Is.EqualTo(block1.Hash));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void When_deleting_invalid_block_sets_head_bestKnown_and_suggested_right()
        {
            BlockTree tree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
            Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;

            tree.SuggestBlock(block0);
            tree.SuggestBlock(block1);
            tree.SuggestBlock(block2);
            tree.SuggestBlock(block3);

            tree.UpdateMainChain(block0);
            tree.UpdateMainChain(block1);
            tree.DeleteInvalidBlock(block2);

            Assert.That(tree.BestKnownNumber, Is.EqualTo(block1.Number));
            Assert.That(tree.Head?.Header, Is.EqualTo(block1.Header));
            Assert.That(tree.BestSuggestedHeader, Is.EqualTo(block1.Header));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void When_deleting_invalid_block_deletes_its_descendants()
        {
            BlockStore blockStore = new(new MemDb());
            MemDb blockInfosDb = new();
            BlockTree tree = Build.A.BlockTree()
                .WithoutSettingHead
                .WithBlockInfoDb(blockInfosDb)
                .WithBlockStore(blockStore)
                .TestObject;
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
            Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;

            tree.SuggestBlock(block0);
            tree.SuggestBlock(block1);
            tree.SuggestBlock(block2);
            tree.SuggestBlock(block3);

            tree.UpdateMainChain(block0);
            tree.UpdateMainChain(block1);
            tree.DeleteInvalidBlock(block2);

            Assert.That(tree.BestKnownNumber, Is.EqualTo(1L), "best known");
            Assert.That(tree.Head!.Number, Is.EqualTo(1L), "head");
            Assert.That(tree.BestSuggestedHeader!.Number, Is.EqualTo(1L), "suggested");

            Assert.NotNull(blockStore.Get(block1.Number, block1.Hash!), "block 1");
            Assert.IsNull(blockStore.Get(block2.Number, block2.Hash!), "block 2");
            Assert.IsNull(blockStore.Get(block3.Number, block3.Hash!), "block 3");

            Assert.NotNull(blockInfosDb.Get(1), "level 1");
            Assert.IsNull(blockInfosDb.Get(2), "level 2");
            Assert.IsNull(blockInfosDb.Get(3), "level 3");
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void When_deleting_invalid_block_deletes_its_descendants_even_if_not_first()
        {
            BlockStore blockStore = new(new MemDb());
            MemDb blockInfosDb = new();
            ChainLevelInfoRepository repository = new(blockInfosDb);

            BlockTree tree = Build.A.BlockTree()
                .WithoutSettingHead
                .WithBlockInfoDb(blockInfosDb)
                .WithBlockStore(blockStore)
                .TestObject;

            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
            Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;

            Block block1b = Build.A.Block.WithNumber(1).WithDifficulty(2).WithExtraData(new byte[] { 1 }).WithParent(block0).TestObject;
            Block block2b = Build.A.Block.WithNumber(2).WithDifficulty(3).WithExtraData(new byte[] { 1 }).WithParent(block1b).TestObject;
            Block block3b = Build.A.Block.WithNumber(3).WithDifficulty(4).WithExtraData(new byte[] { 1 }).WithParent(block2b).TestObject;

            tree.SuggestBlock(block0);
            tree.SuggestBlock(block1);
            tree.SuggestBlock(block2);
            tree.SuggestBlock(block3);

            tree.SuggestBlock(block1b);
            tree.SuggestBlock(block2b);
            tree.SuggestBlock(block3b);

            tree.UpdateMainChain(block0);
            tree.UpdateMainChain(block1);
            tree.DeleteInvalidBlock(block1b);

            Assert.That(tree.BestKnownNumber, Is.EqualTo(3L), "best known");
            Assert.That(tree.Head!.Number, Is.EqualTo(1L), "head");
            Assert.That(tree.BestSuggestedHeader!.Number, Is.EqualTo(1L), "suggested");

            Assert.NotNull(blockStore.Get(block1.Number, block1.Hash!), "block 1");
            Assert.NotNull(blockStore.Get(block2.Number, block2.Hash!), "block 2");
            Assert.NotNull(blockStore.Get(block3.Number, block3.Hash!), "block 3");
            Assert.Null(blockStore.Get(block1b.Number, block1b.Hash!), "block 1b");
            Assert.Null(blockStore.Get(block2b.Number, block2b.Hash!), "block 2b");
            Assert.Null(blockStore.Get(block3b.Number, block3b.Hash!), "block 3b");

            Assert.NotNull(blockInfosDb.Get(1), "level 1");
            Assert.NotNull(blockInfosDb.Get(2), "level 2");
            Assert.NotNull(blockInfosDb.Get(3), "level 3");

            Assert.NotNull(blockInfosDb.Get(1), "level 1b");
            Assert.NotNull(blockInfosDb.Get(2), "level 2b");
            Assert.NotNull(blockInfosDb.Get(3), "level 3b");

            repository.LoadLevel(1)!.BlockInfos.Length.Should().Be(1);
            repository.LoadLevel(2)!.BlockInfos.Length.Should().Be(1);
            repository.LoadLevel(3)!.BlockInfos.Length.Should().Be(1);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void After_removing_invalid_block_will_not_accept_it_again()
        {
            BlockTree tree = Build.A.BlockTree()
                .WithoutSettingHead
                .TestObject;

            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
            Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;

            tree.SuggestBlock(block0);
            tree.SuggestBlock(block1);
            tree.SuggestBlock(block2);
            tree.SuggestBlock(block3);

            tree.DeleteInvalidBlock(block1);
            AddBlockResult result = tree.SuggestBlock(block1);
            Assert.That(result, Is.EqualTo(AddBlockResult.InvalidBlock));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void After_deleting_invalid_block_will_accept_other_blocks()
        {
            BlockTree tree = Build.A.BlockTree()
                .WithoutSettingHead
                .TestObject;

            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
            Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;

            Block block1B = Build.A.Block.WithNumber(1).WithDifficulty(1).WithParent(block0).TestObject;

            tree.SuggestBlock(block0);
            tree.SuggestBlock(block1);
            tree.SuggestBlock(block2);
            tree.SuggestBlock(block3);

            tree.DeleteInvalidBlock(block1);
            AddBlockResult result = tree.SuggestBlock(block1B);
            Assert.That(result, Is.EqualTo(AddBlockResult.Added));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void When_deleting_invalid_block_does_not_delete_blocks_that_are_not_its_descendants()
        {
            BlockTree tree = Build.A.BlockTree()
                .WithoutSettingHead
                .TestObject;

            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
            Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;
            Block block4 = Build.A.Block.WithNumber(4).WithDifficulty(5).WithParent(block3).TestObject;
            Block block5 = Build.A.Block.WithNumber(5).WithDifficulty(6).WithParent(block4).TestObject;

            Block block3bad = Build.A.Block.WithNumber(3).WithDifficulty(1).WithParent(block2).TestObject;

            tree.SuggestBlock(block0);
            tree.SuggestBlock(block1);
            tree.SuggestBlock(block2);
            tree.SuggestBlock(block3);
            tree.SuggestBlock(block4);
            tree.SuggestBlock(block5);

            tree.SuggestBlock(block3bad);

            tree.UpdateMainChain(block5);
            tree.DeleteInvalidBlock(block3bad);

            Assert.That(tree.BestKnownNumber, Is.EqualTo(5L), "best known");
            Assert.That(tree.Head?.Header, Is.EqualTo(block5.Header), "head");
            Assert.That(tree.BestSuggestedHeader!.Hash, Is.EqualTo(block5.Hash), "suggested");
        }

        [Test, Timeout(Timeout.MaxTestTime), TestCaseSource(nameof(SourceOfBSearchTestCases))]
        public void Loads_lowest_inserted_header_correctly(long beginIndex, long insertedBlocks)
        {
            long? expectedResult = insertedBlocks == 0L ? null : beginIndex - insertedBlocks + 1L;

            SyncConfig syncConfig = new()
            {
                FastSync = true,
                PivotNumber = beginIndex.ToString(),
            };

            BlockTreeBuilder builder = Build.A
                .BlockTree()
                .WithSyncConfig(syncConfig)
                .WithoutSettingHead;
            BlockTree tree = builder.TestObject;
            tree.SuggestBlock(Build.A.Block.Genesis.TestObject);

            for (long i = beginIndex; i > beginIndex - insertedBlocks; i--)
            {
                tree.Insert(Build.A.BlockHeader.WithNumber(i).WithTotalDifficulty(i).TestObject);
            }

            BlockTree loadedTree = Build.A.BlockTree()
                .WithDatabaseFrom(builder)
                .WithSyncConfig(syncConfig)
                .TestObject;

            Assert.That(tree.LowestInsertedHeader?.Number, Is.EqualTo(expectedResult), "tree");
            Assert.That(loadedTree.LowestInsertedHeader?.Number, Is.EqualTo(expectedResult), "loaded tree");
        }

        [Test, Timeout(Timeout.MaxTestTime), TestCaseSource(nameof(SourceOfBSearchTestCases))]
        public void Loads_lowest_inserted_body_correctly(long beginIndex, long insertedBlocks)
        {
            // left old code to prove that it does not matter for the result nowadays
            // we store and no longer binary search lowest body number

            MemDb blocksDb = new();
            blocksDb.Set(BlockTree.LowestInsertedBodyNumberDbEntryAddress, Rlp.Encode(1L).Bytes);

            SyncConfig syncConfig = new()
            {
                PivotNumber = beginIndex.ToString(),
            };

            BlockTreeBuilder builder = Build.A.BlockTree()
                .WithBlocksDb(blocksDb)
                .WithSyncConfig(syncConfig)
                .WithoutSettingHead;

            BlockTree tree = builder.TestObject;

            tree.SuggestBlock(Build.A.Block.Genesis.TestObject);

            for (long i = beginIndex; i > beginIndex - insertedBlocks; i--)
            {
                Block block = Build.A.Block.WithNumber(i).WithTotalDifficulty(i).TestObject;
                tree.Insert(block.Header);
                tree.Insert(block);
            }

            BlockTree loadedTree = Build.A.BlockTree()
                .WithoutSettingHead
                .WithDatabaseFrom(builder)
                .WithSyncConfig(syncConfig)
                .TestObject;

            Assert.That(tree.LowestInsertedBodyNumber, Is.EqualTo(null), "tree");
            Assert.That(loadedTree.LowestInsertedBodyNumber, Is.EqualTo(1), "loaded tree");
        }


        private static object[] SourceOfBSearchTestCases =
        {
            new object[] {1L, 0L},
            new object[] {1L, 1L},
            new object[] {2L, 0L},
            new object[] {2L, 1L},
            new object[] {2L, 2L},
            new object[] {3L, 0L},
            new object[] {3L, 1L},
            new object[] {3L, 2L},
            new object[] {3L, 3L},
            new object[] {4L, 0L},
            new object[] {4L, 1L},
            new object[] {4L, 2L},
            new object[] {4L, 3L},
            new object[] {4L, 4L},
            new object[] {5L, 0L},
            new object[] {5L, 1L},
            new object[] {5L, 2L},
            new object[] {5L, 3L},
            new object[] {5L, 4L},
            new object[] {5L, 5L},
            new object[] {728000, 0L},
            new object[] {7280000L, 1L}
        };

        [Test, Timeout(Timeout.MaxTestTime), TestCaseSource(nameof(SourceOfBSearchTestCases))]
        public void Loads_best_known_correctly_on_inserts(long beginIndex, long insertedBlocks)
        {
            long expectedResult = insertedBlocks == 0L ? 0L : beginIndex;

            SyncConfig syncConfig = new()
            {
                PivotNumber = beginIndex.ToString(),
                FastSync = true,
            };

            BlockTreeBuilder builder = Build.A.BlockTree()
                .WithoutSettingHead
                .WithSyncConfig(syncConfig);

            BlockTree tree = builder.TestObject;

            tree.SuggestBlock(Build.A.Block.Genesis.TestObject);

            for (long i = beginIndex; i > beginIndex - insertedBlocks; i--)
            {
                Block block = Build.A.Block.WithNumber(i).WithTotalDifficulty(i).TestObject;
                tree.Insert(block.Header);
                tree.Insert(block);
            }

            BlockTree loadedTree = Build.A.BlockTree()
                .WithoutSettingHead
                .WithDatabaseFrom(builder)
                .WithSyncConfig(syncConfig)
                .TestObject;

            Assert.That(tree.BestKnownNumber, Is.EqualTo(expectedResult), "tree");
            Assert.That(loadedTree.BestKnownNumber, Is.EqualTo(expectedResult), "loaded tree");
        }

        [Test]
        public void Loads_best_head_up_to_best_persisted_state()
        {
            MemDb metadataDb = new();
            metadataDb.Set(MetadataDbKeys.BeaconSyncPivotNumber, Rlp.Encode(51).Bytes);

            SyncConfig syncConfig = new()
            {
                PivotNumber = "0",
                FastSync = true,
            };

            BlockTreeBuilder builder = Build.A.BlockTree()
                .WithoutSettingHead
                .WithMetadataDb(metadataDb)
                .WithSyncConfig(syncConfig);

            BlockTree tree = builder.TestObject;
            Block genesis = Build.A.Block.Genesis.TestObject;
            tree.SuggestBlock(genesis);
            Block parent = genesis;

            List<Block> blocks = new() { genesis };

            for (long i = 1; i < 100; i++)
            {
                Block block = Build.A.Block
                    .WithNumber(i)
                    .WithParent(parent)
                    .WithTotalDifficulty(i).TestObject;
                blocks.Add(block);
                parent = block;
                if (i <= 50)
                {
                    // tree.Insert(block.Header);
                    tree.SuggestBlock(block);
                }
                else
                {
                    tree.Insert(block, BlockTreeInsertBlockOptions.SaveHeader, BlockTreeInsertHeaderOptions.BeaconBodyMetadata);
                }
            }
            tree.UpdateMainChain(blocks.ToArray(), true);
            tree.BestPersistedState = 50;

            BlockTree loadedTree = Build.A.BlockTree()
                .WithoutSettingHead
                .WithDatabaseFrom(builder)
                .WithSyncConfig(syncConfig)
                .TestObject;

            Assert.That(loadedTree.Head?.Number, Is.EqualTo(50));
        }

        [Timeout(Timeout.MaxTestTime)]
        [TestCase(1L)]
        [TestCase(2L)]
        [TestCase(3L)]
        public void Loads_best_known_correctly_on_inserts_followed_by_suggests(long pivotNumber)
        {
            SyncConfig syncConfig = new()
            {
                PivotNumber = pivotNumber.ToString(),
            };
            BlockTreeBuilder builder = Build.A.BlockTree()
                .WithoutSettingHead
                .WithSyncConfig(syncConfig);

            BlockTree tree = builder.TestObject;
            tree.SuggestBlock(Build.A.Block.Genesis.TestObject);

            Block? pivotBlock = null;
            for (long i = pivotNumber; i > 0; i--)
            {
                Block block = Build.A.Block.WithNumber(i).WithTotalDifficulty(i).TestObject;
                pivotBlock ??= block;
                tree.Insert(block.Header);
            }

            tree.SuggestHeader(Build.A.BlockHeader.WithNumber(pivotNumber + 1).WithParent(pivotBlock!.Header).TestObject);

            BlockTree loadedTree = Build.A.BlockTree()
                .WithoutSettingHead
                .WithDatabaseFrom(builder)
                .WithSyncConfig(syncConfig)
                .TestObject;

            Assert.That(tree.BestKnownNumber, Is.EqualTo(pivotNumber + 1), "tree");
            Assert.That(tree.LowestInsertedHeader?.Number, Is.EqualTo(1), "loaded tree - lowest header");
            Assert.That(tree.LowestInsertedBodyNumber, Is.EqualTo(null), "loaded tree - lowest body");
            Assert.That(loadedTree.BestKnownNumber, Is.EqualTo(pivotNumber + 1), "loaded tree");
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Loads_best_known_correctly_when_head_before_pivot()
        {
            int pivotNumber = 1000;
            int head = 10;
            SyncConfig syncConfig = new()
            {
                PivotNumber = pivotNumber.ToString()
            };

            BlockTreeBuilder treeBuilder = Build.A.BlockTree().OfChainLength(head + 1);

            BlockTree loadedTree = Build.A.BlockTree()
                .WithoutSettingHead
                .WithDatabaseFrom(treeBuilder)
                .WithSyncConfig(syncConfig)
                .TestObject;

            Assert.That(loadedTree.BestKnownNumber, Is.EqualTo(head), "loaded tree");
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Cannot_insert_genesis()
        {
            long pivotNumber = 0L;

            SyncConfig syncConfig = new()
            {
                PivotNumber = pivotNumber.ToString(),
            };

            BlockTree tree = Build.A.BlockTree()
                .WithoutSettingHead
                .WithSyncConfig(syncConfig)
                .TestObject;

            Block genesis = Build.A.Block.Genesis.TestObject;
            tree.SuggestBlock(genesis);
            Assert.Throws<InvalidOperationException>(() => tree.Insert(genesis));
            Assert.Throws<InvalidOperationException>(() => tree.Insert(genesis.Header));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Should_set_zero_total_difficulty()
        {
            long pivotNumber = 0L;

            SyncConfig syncConfig = new()
            {
                PivotNumber = pivotNumber.ToString(),
            };

            CustomSpecProvider specProvider = new(((ForkActivation)0, London.Instance));
            specProvider.UpdateMergeTransitionInfo(null, 0);

            BlockTree tree = Build.A.BlockTree()
                .WithoutSettingHead
                .WithSyncConfig(syncConfig)
                .WithSpecProvider(specProvider)
                .TestObject;

            Block genesis = Build.A.Block.WithDifficulty(0).TestObject;
            tree.SuggestBlock(genesis).Should().Be(AddBlockResult.Added);
            tree.FindBlock(genesis.Hash, BlockTreeLookupOptions.None)!.TotalDifficulty.Should().Be(UInt256.Zero);

            Block A = Build.A.Block.WithParent(genesis).WithDifficulty(0).TestObject;
            tree.SuggestBlock(A).Should().Be(AddBlockResult.Added);
            tree.FindBlock(A.Hash, BlockTreeLookupOptions.None)!.TotalDifficulty.Should().Be(UInt256.Zero);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Inserts_blooms()
        {
            long pivotNumber = 5L;

            SyncConfig syncConfig = new()
            {
                PivotNumber = pivotNumber.ToString(),
            };

            IBloomStorage bloomStorage = Substitute.For<IBloomStorage>();
            IChainLevelInfoRepository chainLevelInfoRepository = Substitute.For<IChainLevelInfoRepository>();

            BlockTree tree = Build.A.BlockTree()
                .WithChainLevelInfoRepository(chainLevelInfoRepository)
                .WithBloomStorage(bloomStorage)
                .WithSyncConfig(syncConfig)
                .TestObject;

            tree.SuggestBlock(Build.A.Block.Genesis.TestObject);

            for (long i = 5; i > 0; i--)
            {
                Block block = Build.A.Block.WithNumber(i).WithTotalDifficulty(1L).TestObject;
                tree.Insert(block.Header);
                Received.InOrder(() =>
                {
                    bloomStorage.Store(block.Header.Number, block.Bloom!);
                    chainLevelInfoRepository.PersistLevel(block.Header.Number, Arg.Any<ChainLevelInfo>(), Arg.Any<BatchWrite>());
                });
            }
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Block_loading_is_lazy()
        {
            SyncConfig syncConfig = new()
            {
                PivotNumber = 0L.ToString(),
            };

            BlockTreeBuilder builder = Build.A.BlockTree()
                .WithSyncConfig(syncConfig);
            BlockTree tree = builder
                .TestObject;

            Block genesis = Build.A.Block.Genesis.TestObject;
            tree.SuggestBlock(genesis);

            Block previousBlock = genesis;
            for (int i = 1; i < 10; i++)
            {
                Block block = Build.A.Block.WithNumber(i).WithParent(previousBlock).TestObject;
                tree.SuggestBlock(block);
                previousBlock = block;
            }

            Block lastBlock = previousBlock;

            BlockTree loadedTree = Build.A.BlockTree()
                .WithoutSettingHead
                .WithSyncConfig(syncConfig)
                .WithDatabaseFrom(builder)
                .TestObject;
            loadedTree.FindHeader(lastBlock.Hash, BlockTreeLookupOptions.None);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void When_block_is_moved_to_main_blooms_are_stored()
        {
            Transaction t1 = Build.A.Transaction.TestObject;
            Transaction t2 = Build.A.Transaction.TestObject;

            IBloomStorage bloomStorage = Substitute.For<IBloomStorage>();
            BlockTree blockTree = Build.A.BlockTree()
                .WithoutSettingHead
                .WithBloomStorage(bloomStorage)
                .TestObject;
            // new(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), OlympicSpecProvider.Instance, bloomStorage, LimboLogs.Instance);
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1A = Build.A.Block.WithNumber(1).WithDifficulty(2).WithTransactions(t1).WithParent(block0).TestObject;
            Block block1B = Build.A.Block.WithNumber(1).WithDifficulty(3).WithTransactions(t2).WithParent(block0).TestObject;

            AddToMain(blockTree, block0);

            blockTree.SuggestBlock(block1B);
            blockTree.SuggestBlock(block1A);
            blockTree.UpdateMainChain(block1A);

            bloomStorage.Received().Store(block1A.Number, block1A.Bloom!);
        }


        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_find_genesis_level()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            ChainLevelInfo info = blockTree.FindLevel(0)!;
            Assert.True(info.HasBlockOnMainChain);
            Assert.That(info.BlockInfos.Length, Is.EqualTo(1));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_find_some_level()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            ChainLevelInfo info = blockTree.FindLevel(1)!;
            Assert.True(info.HasBlockOnMainChain);
            Assert.That(info.BlockInfos.Length, Is.EqualTo(1));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Cannot_find_future_level()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            ChainLevelInfo info = blockTree.FindLevel(1000)!;
            Assert.IsNull(info);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_delete_a_future_slice()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            blockTree.DeleteChainSlice(1000, 2000);
            Assert.That(blockTree.Head!.Number, Is.EqualTo(2));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_delete_slice()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            blockTree.DeleteChainSlice(2, 2);
            Assert.Null(blockTree.FindBlock(2, BlockTreeLookupOptions.None));
            Assert.Null(blockTree.FindHeader(2, BlockTreeLookupOptions.None));
            Assert.Null(blockTree.FindLevel(2));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Does_not_delete_outside_of_the_slice()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            blockTree.DeleteChainSlice(2, 2);
            Assert.NotNull(blockTree.FindBlock(1, BlockTreeLookupOptions.None));
            Assert.NotNull(blockTree.FindHeader(1, BlockTreeLookupOptions.None));
            Assert.NotNull(blockTree.FindLevel(1));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_delete_one_block()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            blockTree.DeleteChainSlice(2, 2);
            Assert.That(blockTree.Head!.Number, Is.EqualTo(1));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_delete_two_blocks()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            blockTree.DeleteChainSlice(1, 2);
            Assert.Null(blockTree.FindLevel(1));
            Assert.Null(blockTree.FindLevel(2));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_delete_in_the_middle()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            blockTree.DeleteChainSlice(1, 1);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Throws_when_start_after_end()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            Assert.Throws<ArgumentException>(() => blockTree.DeleteChainSlice(2, 1));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Throws_when_start_at_zero()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            Assert.Throws<ArgumentException>(() => blockTree.DeleteChainSlice(0, 1));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Throws_when_start_below_zero()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            Assert.Throws<ArgumentException>(() => blockTree.DeleteChainSlice(-1, 1));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Cannot_delete_too_many()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            Assert.Throws<ArgumentException>(() => blockTree.DeleteChainSlice(1000, 52001));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Cannot_add_blocks_when_blocked()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            blockTree.BlockAcceptingNewBlocks();
            blockTree.SuggestBlock(Build.A.Block.WithNumber(3).TestObject).Should().Be(AddBlockResult.CannotAccept);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void When_block_cannot_insert_blocks()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            blockTree.CanAcceptNewBlocks.Should().BeTrue();
            blockTree.BlockAcceptingNewBlocks();
            blockTree.CanAcceptNewBlocks.Should().BeFalse();
            Block newBlock = Build.A.Block.WithNumber(3).TestObject;
            AddBlockResult result = blockTree.Insert(newBlock);
            result.Should().Be(AddBlockResult.CannotAccept);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_skip_blocked_tree()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            blockTree.CanAcceptNewBlocks.Should().BeTrue();
            blockTree.BlockAcceptingNewBlocks();
            blockTree.CanAcceptNewBlocks.Should().BeFalse();
            Block newBlock = Build.A.Block.WithNumber(3).TestObject;
            AddBlockResult result = blockTree.Insert(newBlock, BlockTreeInsertBlockOptions.SkipCanAcceptNewBlocks);
            result.Should().Be(AddBlockResult.Added);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_block_and_unblock_adding_blocks()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            blockTree.CanAcceptNewBlocks.Should().BeTrue();
            blockTree.BlockAcceptingNewBlocks();
            blockTree.CanAcceptNewBlocks.Should().BeFalse();
            blockTree.BlockAcceptingNewBlocks();
            blockTree.ReleaseAcceptingNewBlocks();
            blockTree.CanAcceptNewBlocks.Should().BeFalse();
            blockTree.ReleaseAcceptingNewBlocks();
            blockTree.CanAcceptNewBlocks.Should().BeTrue();
        }

        [Timeout(Timeout.MaxTestTime)]
        [TestCase(10, false, 10000000ul)]
        [TestCase(4, false, 4000000ul)]
        [TestCase(10, true, 10000000ul)]
        public void Recovers_total_difficulty(int chainLength, bool deleteAllLevels, ulong expectedTotalDifficulty)
        {
            BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree().OfChainLength(chainLength);
            BlockTree blockTree = blockTreeBuilder.TestObject;
            int chainLeft = deleteAllLevels ? 0 : 1;
            for (int i = chainLength - 1; i >= chainLeft; i--)
            {
                ChainLevelInfo? level = blockTreeBuilder.ChainLevelInfoRepository.LoadLevel(i);
                if (level is not null)
                {
                    for (int j = 0; j < level.BlockInfos.Length; j++)
                    {
                        Hash256 blockHash = level.BlockInfos[j].BlockHash;
                        BlockHeader? header = blockTree.FindHeader(blockHash, BlockTreeLookupOptions.None);
                        if (header is not null)
                        {
                            header.TotalDifficulty = null;
                        }
                    }

                    blockTreeBuilder.ChainLevelInfoRepository.Delete(i);
                }
            }

            blockTree.FindBlock(blockTree.Head!.Hash, BlockTreeLookupOptions.None)!.TotalDifficulty.Should()
                .Be(new UInt256(expectedTotalDifficulty));

            for (int i = chainLength - 1; i >= 0; i--)
            {
                ChainLevelInfo? level = blockTreeBuilder.ChainLevelInfoRepository.LoadLevel(i);

                level.Should().NotBeNull();
                level!.BlockInfos.Should().HaveCount(1);
            }
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task Visitor_can_block_adding_blocks()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            ManualResetEvent manualResetEvent = new ManualResetEvent(false);
            Task acceptTask = blockTree.Accept(new TestBlockTreeVisitor(manualResetEvent), CancellationToken.None);
            blockTree.CanAcceptNewBlocks.Should().BeFalse();
            manualResetEvent.Set();
            await acceptTask;
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task SuggestBlockAsync_should_wait_for_blockTree_unlock()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            blockTree.BlockAcceptingNewBlocks();
            ValueTask<AddBlockResult> suggest = blockTree.SuggestBlockAsync(Build.A.Block.WithNumber(3).TestObject);
            suggest.IsCompleted.Should().Be(false);
            blockTree.ReleaseAcceptingNewBlocks();
            await suggest;
            suggest.IsCompleted.Should().Be(true);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task SuggestBlockAsync_works_well_with_multiple_locks_and_unlocks()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            blockTree.BlockAcceptingNewBlocks();        // 1st blockade
            blockTree.ReleaseAcceptingNewBlocks();      // release - access unlocked
            blockTree.BlockAcceptingNewBlocks();        // 1st blockade
            blockTree.BlockAcceptingNewBlocks();        // 2nd blockade
            blockTree.BlockAcceptingNewBlocks();        // 3rd blockade
            ValueTask<AddBlockResult> suggest = blockTree.SuggestBlockAsync(Build.A.Block.WithNumber(3).TestObject);
            suggest.IsCompleted.Should().Be(false);
            blockTree.ReleaseAcceptingNewBlocks();      // 1st release - 2 blockades left
            suggest.IsCompleted.Should().Be(false);
            blockTree.ReleaseAcceptingNewBlocks();      // 2nd release - 1 blockade left
            suggest.IsCompleted.Should().Be(false);
            blockTree.BlockAcceptingNewBlocks();        // 1 more blockade - 2 blockades left
            suggest.IsCompleted.Should().Be(false);
            blockTree.ReleaseAcceptingNewBlocks();      // release - 1 blockade left
            suggest.IsCompleted.Should().Be(false);
            blockTree.ReleaseAcceptingNewBlocks();      // 3rd release - access unlocked
            await suggest;
            suggest.IsCompleted.Should().Be(true);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task SuggestBlockAsync_works_well_when_there_are_no_blockades()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            ValueTask<AddBlockResult> suggest = blockTree.SuggestBlockAsync(Build.A.Block.WithNumber(3).TestObject);
            await suggest;
            suggest.IsCompleted.Should().Be(true);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void SuggestBlock_should_work_with_zero_difficulty()
        {
            Block genesisWithZeroDifficulty = Build.A.Block.WithDifficulty(0).WithNumber(0).TestObject;
            CustomSpecProvider specProvider = new(((ForkActivation)0, GrayGlacier.Instance));
            specProvider.UpdateMergeTransitionInfo(null, 0);
            BlockTree blockTree = Build.A.BlockTree(genesisWithZeroDifficulty, specProvider).OfChainLength(1).TestObject;

            Block block = Build.A.Block.WithDifficulty(0).WithParent(genesisWithZeroDifficulty).TestObject;
            blockTree.SuggestBlock(block).Should().Be(AddBlockResult.Added);
            blockTree.SuggestBlock(Build.A.Block.WithParent(block).WithDifficulty(0).TestObject).Should().Be(AddBlockResult.Added);
        }

        public static IEnumerable<TestCaseData> InvalidBlockTestCases
        {
            get
            {
                BlockHeader? FindHeader(BlockTree b, Hash256? h, BlockTreeLookupOptions o) => b.FindHeader(h, o);
                BlockHeader? FindBlock(BlockTree b, Hash256? h, BlockTreeLookupOptions o) => b.FindBlock(h, o)?.Header;

                IReadOnlyList<BlockTreeLookupOptions> valueCombinations = EnumExtensions.AllValuesCombinations<BlockTreeLookupOptions>();
                foreach (BlockTreeLookupOptions blockTreeLookupOptions in valueCombinations)
                {
                    bool allowInvalid = (blockTreeLookupOptions & BlockTreeLookupOptions.AllowInvalid) == BlockTreeLookupOptions.AllowInvalid;
                    yield return new TestCaseData((Func<BlockTree, Hash256?, BlockTreeLookupOptions, BlockHeader?>)FindHeader, blockTreeLookupOptions, allowInvalid)
                    {
                        TestName = $"InvalidBlock_{nameof(FindHeader)}_({blockTreeLookupOptions})_{(allowInvalid ? "found" : "not_found")}"
                    };
                    yield return new TestCaseData((Func<BlockTree, Hash256?, BlockTreeLookupOptions, BlockHeader?>)FindBlock, blockTreeLookupOptions, allowInvalid)
                    {
                        TestName = $"InvalidBlock_{nameof(FindBlock)}_({blockTreeLookupOptions})_{(allowInvalid ? "found" : "not_found")}"
                    };
                }
            }
        }

        [TestCaseSource(nameof(InvalidBlockTestCases))]
        public void Find_handles_invalid_blocks(Func<BlockTree, Hash256?, BlockTreeLookupOptions, BlockHeader?> findFunction, BlockTreeLookupOptions lookupOptions, bool foundInvalid)
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            Block invalidBlock = Build.A.Block.WithNumber(4).WithParent(blockTree.Head!).TestObject;
            blockTree.SuggestBlock(invalidBlock);
            blockTree.DeleteInvalidBlock(invalidBlock);
            findFunction(blockTree, invalidBlock.Hash, lookupOptions).Should().Be(foundInvalid ? invalidBlock.Header : null);
        }

        [Test]
        public void On_restart_loads_already_processed_genesis_block()
        {
            TestMemDb blocksDb = new();
            TestMemDb headersDb = new();
            TestMemDb blockNumberDb = new();
            TestMemDb blocksInfosDb = new();
            ChainLevelInfoRepository chainLevelInfoRepository = new(blocksInfosDb);

            // First run
            {
                Hash256 uncleHash = new("0x1dcc4de8dec75d7aab85b567b6ccd41ad312451b948a7413f0a142fd40d49347");
                BlockTree tree = Build.A.BlockTree(HoleskySpecProvider.Instance)
                    .WithBlockStore(new BlockStore(blocksDb))
                    .WithBlocksNumberDb(blockNumberDb)
                    .WithHeadersDb(headersDb)
                    .WithChainLevelInfoRepository(chainLevelInfoRepository)
                    .WithoutSettingHead
                    .TestObject;

                // Holesky genesis
                Block genesis = new(new(
                    parentHash: Keccak.Zero,
                    unclesHash: uncleHash,
                    beneficiary: new Address(Keccak.Zero),
                    difficulty: 1,
                    number: 0,
                    gasLimit: 25000000,
                    timestamp: 1695902100,
                    extraData: Array.Empty<byte>())
                {
                    Hash = new Hash256("0xb5f7f912443c940f21fd611f12828d75b534364ed9e95ca4e307729a4661bde4"),
                    Bloom = Core.Bloom.Empty
                });

                // Second block
                Block second = new(new(
                    parentHash: genesis.Header.Hash!,
                    unclesHash: uncleHash,
                    beneficiary: new Address(Keccak.Zero),
                    difficulty: 0,
                    number: genesis.Header.Number + 1,
                    gasLimit: 25000000,
                    timestamp: genesis.Header.Timestamp + 100,
                    extraData: Array.Empty<byte>())
                {
                    Hash = new Hash256("0x1111111111111111111111111111111111111111111111111111111111111111"),
                    Bloom = Core.Bloom.Empty,
                    StateRoot = genesis.Header.Hash,
                });

                // Third block
                Block third = new(new(
                    parentHash: second.Header.Hash!,
                    unclesHash: uncleHash,
                    beneficiary: new Address(Keccak.Zero),
                    difficulty: 0,
                    number: second.Header.Number + 1,
                    gasLimit: 25000000,
                    timestamp: second.Header.Timestamp + 100,
                    extraData: Array.Empty<byte>())
                {
                    Hash = new Hash256("0x2222222222222222222222222222222222222222222222222222222222222222"),
                    Bloom = Core.Bloom.Empty,
                    StateRoot = genesis.Header.Hash,
                });

                tree.SuggestBlock(genesis);
                tree.Genesis.Should().NotBeNull();

                tree.UpdateMainChain(ImmutableList.Create(genesis), true);

                tree.SuggestBlock(second);
                tree.SuggestBlock(third);
            }

            // Assume Nethermind got restarted
            {
                BlockTree tree = Build.A.BlockTree(HoleskySpecProvider.Instance)
                    .WithBlockStore(new BlockStore(blocksDb))
                    .WithBlocksNumberDb(blockNumberDb)
                    .WithHeadersDb(headersDb)
                    .WithChainLevelInfoRepository(chainLevelInfoRepository)
                    .WithoutSettingHead
                    .TestObject;

                tree.Genesis.Should().NotBeNull();
            }
        }

        private class TestBlockTreeVisitor : IBlockTreeVisitor
        {
            private readonly ManualResetEvent _manualResetEvent;
            private bool _wait = true;

            public TestBlockTreeVisitor(ManualResetEvent manualResetEvent)
            {
                _manualResetEvent = manualResetEvent;
            }

            public bool PreventsAcceptingNewBlocks => true;
            public long StartLevelInclusive => 0;
            public long EndLevelExclusive => 3;
            public async Task<LevelVisitOutcome> VisitLevelStart(ChainLevelInfo chainLevelInfo, long levelNumber, CancellationToken cancellationToken)
            {
                if (_wait)
                {
                    await _manualResetEvent.WaitOneAsync(cancellationToken);
                    _wait = false;
                }

                return LevelVisitOutcome.None;
            }

            public Task<bool> VisitMissing(Hash256 hash, CancellationToken cancellationToken)
            {
                return Task.FromResult(true);
            }

            public Task<HeaderVisitOutcome> VisitHeader(BlockHeader header, CancellationToken cancellationToken)
            {
                return Task.FromResult(HeaderVisitOutcome.None);
            }

            public Task<BlockVisitOutcome> VisitBlock(Block block, CancellationToken cancellationToken)
            {
                return Task.FromResult(BlockVisitOutcome.None);
            }

            public Task<LevelVisitOutcome> VisitLevelEnd(ChainLevelInfo chainLevelInfo, long levelNumber, CancellationToken cancellationToken)
            {
                return Task.FromResult(LevelVisitOutcome.None);
            }
        }
    }
}
