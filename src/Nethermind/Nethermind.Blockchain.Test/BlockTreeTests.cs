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

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Visitors;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class BlockTreeTests
    {
        private MemDb _blocksInfosDb;
        private MemDb _headersDb;
        private MemDb _blocksDb;
        
        private BlockTree BuildBlockTree()
        {
            _blocksDb = new MemDb();
            _headersDb = new MemDb();
            _blocksInfosDb = new MemDb();
            _chainLevelInfoRepository = new ChainLevelInfoRepository(_blocksInfosDb);
            return new BlockTree(_blocksDb, _headersDb, _blocksInfosDb, _chainLevelInfoRepository, MainnetSpecProvider.Instance, NullBloomStorage.Instance, LimboLogs.Instance);
        }

        private static void AddToMain(BlockTree blockTree, Block block0)
        {
            blockTree.SuggestBlock(block0);
            blockTree.UpdateMainChain(new[] {block0}, true);
        }

        [Test]
        public void Add_genesis_shall_notify()
        {
            bool hasNotified = false;
            BlockTree blockTree = BuildBlockTree();
            blockTree.NewHeadBlock += (sender, args) => { hasNotified = true; };
            
            bool hasNotifiedNewSuggested = false;
            blockTree.NewSuggestedBlock += (sender, args) => { hasNotifiedNewSuggested = true; };

            Block block = Build.A.Block.WithNumber(0).TestObject;
            var result = blockTree.SuggestBlock(block);
            blockTree.UpdateMainChain(block);

            Assert.True(hasNotified, "notification");
            Assert.AreEqual(AddBlockResult.Added, result, "result");
            Assert.True(hasNotifiedNewSuggested, "NewSuggestedBlock");
        }

        [Test]
        public void Add_genesis_shall_work_even_with_0_difficulty()
        {
            bool hasNotified = false;
            BlockTree blockTree = BuildBlockTree();
            blockTree.NewBestSuggestedBlock += (sender, args) => { hasNotified = true; };
            
            bool hasNotifiedNewSuggested = false;
            blockTree.NewSuggestedBlock += (sender, args) => { hasNotifiedNewSuggested = true; };

            Block block = Build.A.Block.WithNumber(0).WithDifficulty(0).TestObject;
            var result = blockTree.SuggestBlock(block);

            Assert.True(hasNotified, "notification");
            Assert.AreEqual(AddBlockResult.Added, result, "result");
            Assert.True(hasNotifiedNewSuggested, "NewSuggestedBlock");
        }

        [Test]
        public void Suggesting_genesis_many_times_does_not_cause_any_trouble()
        {
            BlockTree blockTree = BuildBlockTree();
            Block blockA = Build.A.Block.WithNumber(0).TestObject;
            Block blockB = Build.A.Block.WithNumber(0).TestObject;
            blockTree.SuggestBlock(blockA).Should().Be(AddBlockResult.Added);
            blockTree.SuggestBlock(blockB).Should().Be(AddBlockResult.AlreadyKnown);
        }

        [Test]
        public void Shall_notify_on_new_head_block_after_genesis()
        {
            bool hasNotified = false;
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            blockTree.SuggestBlock(block0);
            blockTree.NewHeadBlock += (sender, args) => { hasNotified = true; };
            
            bool hasNotifiedNewSuggested = false;
            blockTree.NewSuggestedBlock += (sender, args) => { hasNotifiedNewSuggested = true; };
            
            var result = blockTree.SuggestBlock(block1);
            blockTree.UpdateMainChain(block1);

            Assert.True(hasNotified, "notification");
            Assert.AreEqual(AddBlockResult.Added, result, "result");
            Assert.True(hasNotifiedNewSuggested, "NewSuggestedBlock");
        }

        [Test]
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
            blockTree.NewHeadBlock += (sender, args) => { newHeadBlockNotifications++; };
            blockTree.BlockAddedToMain += (sender, args) => { blockAddedToMainNotifications++; };

            blockTree.SuggestBlock(block1);
            blockTree.SuggestBlock(block2);
            blockTree.SuggestBlock(block3);
            blockTree.UpdateMainChain(new Block[] {block1, block2, block3}, true);

            newHeadBlockNotifications.Should().Be(1, "new head block");
            blockAddedToMainNotifications.Should().Be(3, "block added to main");
        }

        [Test]
        public void Shall_notify_on_new_suggested_block_after_genesis()
        {
            bool hasNotified = false;
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            blockTree.SuggestBlock(block0);
            blockTree.NewBestSuggestedBlock += (sender, args) => { hasNotified = true; };
            
            bool hasNotifiedNewSuggested = false;
            blockTree.NewSuggestedBlock += (sender, args) => { hasNotifiedNewSuggested = true; };
            
            var result = blockTree.SuggestBlock(block1);

            Assert.True(hasNotified, "notification");
            Assert.AreEqual(AddBlockResult.Added, result, "result");
            Assert.True(hasNotifiedNewSuggested, "NewSuggestedBlock");
        }

        [Test]
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
            blockTree.NewHeadBlock += (sender, args) => { hasNotifiedHead = true; };
            blockTree.NewBestSuggestedBlock += (sender, args) => { hasNotifiedBest = true; };
            
            bool hasNotifiedNewSuggested = false;
            blockTree.NewSuggestedBlock += (sender, args) => { hasNotifiedNewSuggested = true; };
            
            var result = blockTree.SuggestBlock(block2);

            Assert.False(hasNotifiedBest, "notification best");
            Assert.False(hasNotifiedHead, "notification head");
            Assert.AreEqual(AddBlockResult.Added, result, "result");
            Assert.True(hasNotifiedNewSuggested, "NewSuggestedBlock");
        }

        [Test]
        public void Shall_ignore_orphans()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).TestObject;
            blockTree.SuggestBlock(block0);
            var result = blockTree.SuggestBlock(block2);
            Assert.AreEqual(AddBlockResult.UnknownParent, result);
        }

        [Test]
        public void Shall_ignore_known()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            blockTree.SuggestBlock(block0);
            blockTree.SuggestBlock(block1);
            var result = blockTree.SuggestBlock(block1);
            Assert.AreEqual(AddBlockResult.AlreadyKnown, result);
        }

        [Test]
        public void Cleans_invalid_blocks_before_starting()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();
            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, LimboLogs.Instance);
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
            Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;

            tree.SuggestBlock(block0);
            tree.SuggestBlock(block1);
            tree.SuggestBlock(block2);
            tree.SuggestBlock(block3);

            blockInfosDb.Set(BlockTree.DeletePointerAddressInDb, block1.Hash.Bytes);
            BlockTree tree2 = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, LimboLogs.Instance);

            Assert.AreEqual(0L, tree2.BestKnownNumber, "best known");
            Assert.AreEqual(null, tree2.Head, "head");
            Assert.AreEqual(0L, tree2.BestSuggestedHeader.Number, "suggested");

            Assert.IsNull(blocksDb.Get(block2.Hash), "block 1");
            Assert.IsNull(blocksDb.Get(block2.Hash), "block 2");
            Assert.IsNull(blocksDb.Get(block3.Hash), "block 3");

            Assert.IsNull(blockInfosDb.Get(2), "level 1");
            Assert.IsNull(blockInfosDb.Get(2), "level 2");
            Assert.IsNull(blockInfosDb.Get(3), "level 3");
        }

        [Test]
        public void When_cleaning_descendants_of_invalid_does_not_touch_other_branches()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();
            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, LimboLogs.Instance);
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

            blockInfosDb.Set(BlockTree.DeletePointerAddressInDb, block1.Hash.Bytes);
            BlockTree tree2 = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, LimboLogs.Instance);

            Assert.AreEqual(3L, tree2.BestKnownNumber, "best known");
            Assert.AreEqual(null, tree2.Head, "head");
            Assert.AreEqual(block3B.Hash, tree2.BestSuggestedHeader.Hash, "suggested");

            blocksDb.Get(block1.Hash).Should().BeNull("block 1");
            blocksDb.Get(block2.Hash).Should().BeNull("block 2");
            blocksDb.Get(block3.Hash).Should().BeNull("block 3");

            Assert.NotNull(blockInfosDb.Get(1), "level 1");
            Assert.NotNull(blockInfosDb.Get(2), "level 2");
            Assert.NotNull(blockInfosDb.Get(3), "level 3");
        }

        [Test]
        public void Can_load_best_known_up_to_256million()
        {
            _blocksDb = new MemDb();
            _headersDb = new MemDb();
            IDb blocksInfosDb = Substitute.For<IDb>();

            Rlp chainLevel = Rlp.Encode(new ChainLevelInfo(true, new BlockInfo(TestItem.KeccakA, 1)));
            blocksInfosDb[BlockTree.DeletePointerAddressInDb.Bytes].Returns((byte[]) null);
            blocksInfosDb[Arg.Is<byte[]>(b => !Bytes.AreEqual(b, BlockTree.DeletePointerAddressInDb.Bytes))].Returns(chainLevel.Bytes);

            BlockTree blockTree = new BlockTree(_blocksDb, _headersDb, blocksInfosDb, new ChainLevelInfoRepository(blocksInfosDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, LimboLogs.Instance);

            Assert.AreEqual(256000000, blockTree.BestKnownNumber);
        }

        [Test]
        public void Add_and_find_branch()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block = Build.A.Block.TestObject;
            blockTree.SuggestBlock(block);
            Block found = blockTree.FindBlock(block.Hash, BlockTreeLookupOptions.None);
            Assert.AreEqual(block.Hash, found.Header.CalculateHash());
        }

        [Test]
        public void Add_on_branch_move_find()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block = Build.A.Block.TestObject;
            AddToMain(blockTree, block);
            Block found = blockTree.FindBlock(block.Hash, BlockTreeLookupOptions.RequireCanonical);
            Assert.AreEqual(block.Hash, found.Header.CalculateHash());
        }

        [Test]
        public void Add_on_branch_move_find_via_block_finder_interface()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block = Build.A.Block.TestObject;
            AddToMain(blockTree, block);
            Block found = ((IBlockFinder) blockTree).FindBlock(new BlockParameter(block.Hash, true));
            Assert.AreEqual(block.Hash, found.Header.CalculateHash());
        }

        [Test]
        public void Add_on_branch_and_not_find_on_main()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block = Build.A.Block.TestObject;
            blockTree.SuggestBlock(block);
            Block found = blockTree.FindBlock(block.Hash, BlockTreeLookupOptions.RequireCanonical);
            Assert.IsNull(found);
        }

        [Test]
        public void Add_on_branch_and_not_find_on_main_via_block_finder_interface()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block = Build.A.Block.TestObject;
            blockTree.SuggestBlock(block);
            Block found = ((IBlockFinder) blockTree).FindBlock(new BlockParameter(block.Hash, true));
            Assert.IsNull(found);
        }

        [Test]
        public void Find_by_number_basic()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);
            AddToMain(blockTree, block2);

            Block found = blockTree.FindBlock(2, BlockTreeLookupOptions.None);
            Assert.AreEqual(block2.Hash, found.Header.CalculateHash());
        }

        [Test]
        public void Find_by_number_beyond_what_is_known_returns_null()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);
            AddToMain(blockTree, block2);

            Block found = blockTree.FindBlock(1920000, BlockTreeLookupOptions.None);
            Assert.Null(found);
        }

        [Test]
        public void Find_by_number_returns_null_when_block_is_missing()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);

            Block found = blockTree.FindBlock(5, BlockTreeLookupOptions.None);
            Assert.IsNull(found);
        }

        [Test]
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
            Assert.AreEqual(2, headers.Length);
            Assert.AreEqual(block0.Hash, headers[0].Hash);
            Assert.AreEqual(block1.Hash, headers[1].Hash);
        }

        [Test]
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
            Assert.AreEqual(2, headers.Length);
            Assert.AreEqual(block0.Hash, headers[0].Hash);
            Assert.AreEqual(block2.Hash, headers[1].Hash);
        }

        [Test]
        public void Find_headers_reverse()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithParent(block1).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);
            AddToMain(blockTree, block2);

            BlockHeader[] headers = blockTree.FindHeaders(block2.Hash, 2, 0, true);
            Assert.AreEqual(2, headers.Length);
            Assert.AreEqual(block2.Hash, headers[0].Hash);
            Assert.AreEqual(block1.Hash, headers[1].Hash);
        }

        [Test]
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
            Assert.AreEqual(2, headers.Length);
            Assert.AreEqual(block2.Hash, headers[0].Hash);
            Assert.AreEqual(block0.Hash, headers[1].Hash);
        }

        [Test]
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
            Assert.AreEqual(2, headers.Length);
            Assert.AreEqual(block0.Hash, headers[0].Hash);
            Assert.Null(headers[1]);
        }

        [Test]
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
            Assert.AreEqual(100, headers.Length);
            Assert.AreEqual(block0.Hash, headers[0].Hash);
            Assert.Null(headers[3]);

            Assert.AreEqual(0, _headersDb.ReadsCount);
        }

        [Test]
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
            Assert.AreEqual(100, headers.Length);
            Assert.AreEqual(block0.Hash, headers[0].Hash);
            Assert.Null(headers[3]);

            Assert.AreEqual(0, _headersDb.ReadsCount);
        }

        [Test]
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
            Assert.AreEqual(length, blocks.Length);
            Assert.AreEqual(block0.Hash, blocks[0].CalculateHash());
            Assert.AreEqual(block1.Hash, blocks[1].CalculateHash());
            Assert.AreEqual(block2.Hash, blocks[2].CalculateHash());
        }

        [Test]
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
            Assert.AreEqual(length, blocks.Length);
            Assert.AreEqual(block1.Hash, blocks[0].CalculateHash());
            Assert.AreEqual(block2.Hash, blocks[1].CalculateHash());
        }

        [Test]
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
            Assert.AreEqual(length, blocks.Length);
            Assert.AreEqual(block0.Hash, blocks[0].CalculateHash());
            Assert.AreEqual(block1.Hash, blocks[1].CalculateHash());
            Assert.AreEqual(block2.Hash, blocks[2].CalculateHash());
        }

        [Test]
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
            Assert.AreEqual(3, blocks.Length);

            Assert.AreEqual(block2.Hash, blocks[0].CalculateHash());
            Assert.AreEqual(block0.Hash, blocks[2].CalculateHash());
        }


        [Test]
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
            Assert.AreEqual(0, blocks.Length);
        }

        [Test]
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
            Assert.AreEqual(1, blocks.Length);
        }

        [Test]
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
            Assert.AreEqual(2, blocks.Length, "length");
            Assert.AreEqual(block0.Hash, blocks[0].CalculateHash());
            Assert.AreEqual(block2.Hash, blocks[1].CalculateHash());
        }

        [Test]
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
            Assert.AreEqual(4, blocks.Length);
            Assert.IsNull(blocks[3]);
        }

        [Test]
        public void Total_difficulty_is_calculated_when_exists_parent_with_total_difficulty()
        {
            BlockTree blockTree = BuildBlockTree();

            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            blockTree.SuggestBlock(block0);
            Block block1 = Build.A.Block.WithNumber(1).WithParentHash(block0.Hash).WithDifficulty(2).TestObject;
            blockTree.SuggestBlock(block1);
            block1.TotalDifficulty.Should().NotBeNull();
            Assert.AreEqual(3, (int) block1.TotalDifficulty!);
        }

        [Test]
        public void Total_difficulty_is_null_when_no_parent()
        {
            BlockTree blockTree = BuildBlockTree();

            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            blockTree.SuggestBlock(block0);

            Block block2 = Build.A.Block.WithNumber(1).WithDifficulty(3).WithParentHash(Keccak.Zero).TestObject;
            blockTree.SuggestBlock(block2);
            Assert.AreEqual(null, block2.TotalDifficulty);
        }

        [Test]
        public void Head_block_gets_updated()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);

            Assert.AreEqual(block1.Hash, blockTree.Head.CalculateHash());
        }

        [Test]
        public void Best_suggested_block_gets_updated()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            AddToMain(blockTree, block0);
            blockTree.SuggestBlock(block1);

            Assert.AreEqual(block0.Hash, blockTree.Head.CalculateHash(), "head block");
            Assert.AreEqual(block1.Hash, blockTree.BestSuggestedHeader.CalculateHash(), "best suggested");
        }

        [Test]
        public void Sets_genesis_block()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            AddToMain(blockTree, block0);

            Assert.AreEqual(block0.Hash, blockTree.Genesis.CalculateHash());
        }

        [Test]
        public void Stores_multiple_blocks_per_level()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            Block block1B = Build.A.Block.WithNumber(1).WithDifficulty(3).WithParent(block0).TestObject;
            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);
            blockTree.SuggestBlock(block1B);

            Block found = blockTree.FindBlock(block1B.Hash, BlockTreeLookupOptions.None);

            Assert.AreEqual(block1B.Hash, found.Header.CalculateHash());
        }

        [Test]
        public void Can_init_head_block_from_db_by_hash()
        {
            Block genesisBlock = Build.A.Block.Genesis.TestObject;
            Block headBlock = genesisBlock;

            MemDb blocksDb = new MemDb();
            MemDb headersDb = new MemDb();
            blocksDb.Set(genesisBlock.Hash, Rlp.Encode(genesisBlock).Bytes);
            headersDb.Set(genesisBlock.Hash, Rlp.Encode(genesisBlock.Header).Bytes);

            MemDb blockInfosDb = new MemDb();
            blockInfosDb.Set(Keccak.Zero, genesisBlock.Hash.Bytes);
            ChainLevelInfo level = new ChainLevelInfo(true, new BlockInfo(headBlock.Hash, headBlock.Difficulty));
            level.BlockInfos[0].WasProcessed = true;

            blockInfosDb.Set(0, Rlp.Encode(level).Bytes);

            BlockTree blockTree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), OlympicSpecProvider.Instance, NullBloomStorage.Instance, LimboLogs.Instance);
            Assert.AreEqual(headBlock.Hash, blockTree.Head?.Hash, "head");
            Assert.AreEqual(headBlock.Hash, blockTree.Genesis?.Hash, "genesis");
        }

        [Test]
        public void Sets_head_block_hash_in_db_on_new_head_block()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();

            BlockTree blockTree = new BlockTree(
                blocksDb,
                headersDb,
                blockInfosDb,
                new ChainLevelInfoRepository(blockInfosDb),
                OlympicSpecProvider.Instance,
                NullBloomStorage.Instance,
                LimboLogs.Instance);
            
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;

            AddToMain(blockTree, block0);
            AddToMain(blockTree, block1);

            var dec = new Keccak(blockInfosDb.Get(Keccak.Zero));
            Assert.AreEqual(block1.Hash, dec);
        }

        [Test]
        public void Can_check_if_block_was_processed()
        {
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;

            BlockTree blockTree = BuildBlockTree();
            blockTree.SuggestBlock(block0);
            blockTree.SuggestBlock(block1);
            Assert.False(blockTree.WasProcessed(block1.Number, block1.Hash), "before");
            blockTree.UpdateMainChain(new[] {block0, block1}, true);
            Assert.True(blockTree.WasProcessed(block1.Number, block1.Hash), "after");
        }

        [Test]
        public void Best_known_number_is_set()
        {
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;

            BlockTree blockTree = BuildBlockTree();
            blockTree.SuggestBlock(block0);
            blockTree.SuggestBlock(block1);
            Assert.AreEqual(1L, blockTree.BestKnownNumber);
        }

        [Test]
        public void Is_main_chain_returns_false_when_on_branch()
        {
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;

            BlockTree blockTree = BuildBlockTree();
            blockTree.SuggestBlock(block0);
            blockTree.SuggestBlock(block1);
            Assert.False(blockTree.IsMainChain(block1.Hash));
        }

        [Test]
        public void Is_main_chain_returns_true_when_on_main()
        {
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;

            BlockTree blockTree = BuildBlockTree();
            blockTree.SuggestBlock(block0);
            blockTree.SuggestBlock(block1);
            blockTree.UpdateMainChain(block1);
            Assert.True(blockTree.IsMainChain(block1.Hash));
        }

        [Test]
        public void Pending_returns_head()
        {
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;

            BlockTree blockTree = BuildBlockTree();
            blockTree.SuggestBlock(block0);
            blockTree.SuggestBlock(block1);
            blockTree.UpdateMainChain(block0);
            blockTree.BestSuggestedHeader.Should().Be(block1.Header);
            blockTree.PendingHash.Should().Be(block0.Hash);
            ((IBlockFinder) blockTree).FindPendingHeader().Should().BeSameAs(block0.Header);
            ((IBlockFinder) blockTree).FindPendingBlock().Should().BeSameAs(block0);
        }

        [Test]
        public void Is_main_chain_returns_true_on_fast_sync_block()
        {
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            BlockTree blockTree = BuildBlockTree();
            blockTree.SuggestBlock(block0, false);
            blockTree.IsMainChain(block0.Hash).Should().BeTrue();
        }

        [Test]
        public void Was_processed_returns_true_on_fast_sync_block()
        {
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            BlockTree blockTree = BuildBlockTree();
            blockTree.SuggestBlock(block0, false);
        }

        [Test(Description = "There was a bug where we switched positions and used the index from before the positions were switched")]
        public void When_moving_to_main_one_of_the_two_blocks_at_given_level_the_was_processed_check_is_executed_on_the_correct_block_index_regression()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();

            BlockTree blockTree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), OlympicSpecProvider.Instance, NullBloomStorage.Instance, LimboLogs.Instance);
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(1).WithDifficulty(3).WithParent(block0).TestObject;

            AddToMain(blockTree, block0);

            blockTree.SuggestBlock(block2);
            blockTree.SuggestBlock(block1);
            blockTree.UpdateMainChain(block1);

            Keccak storedInDb = new Keccak(blockInfosDb.Get(Keccak.Zero));
            Assert.AreEqual(block1.Hash, storedInDb);
        }

        [Test]
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

            Assert.AreEqual(block1.Number, tree.BestKnownNumber);
            Assert.AreEqual(block1.Header, tree.Head?.Header);
            Assert.AreEqual(block1.Header, tree.BestSuggestedHeader);
        }

        [Test]
        public void When_deleting_invalid_block_deletes_its_descendants()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();
            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, LimboLogs.Instance);
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

            Assert.AreEqual(1L, tree.BestKnownNumber, "best known");
            Assert.AreEqual(1L, tree.Head.Number, "head");
            Assert.AreEqual(1L, tree.BestSuggestedHeader.Number, "suggested");

            Assert.NotNull(blocksDb.Get(block1.Hash), "block 1");
            Assert.IsNull(blocksDb.Get(block2.Hash), "block 2");
            Assert.IsNull(blocksDb.Get(block3.Hash), "block 3");

            Assert.NotNull(blockInfosDb.Get(1), "level 1");
            Assert.IsNull(blockInfosDb.Get(2), "level 2");
            Assert.IsNull(blockInfosDb.Get(3), "level 3");
        }
        
        [Test]
        public void When_deleting_invalid_block_deletes_its_descendants_even_if_not_first()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();
            ChainLevelInfoRepository repository = new ChainLevelInfoRepository(blockInfosDb);
            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, repository, MainnetSpecProvider.Instance, NullBloomStorage.Instance, LimboLogs.Instance);
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
            Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;
            
            Block block1b = Build.A.Block.WithNumber(1).WithDifficulty(2).WithExtraData(new byte[] {1}).WithParent(block0).TestObject;
            Block block2b = Build.A.Block.WithNumber(2).WithDifficulty(3).WithExtraData(new byte[] {1}).WithParent(block1b).TestObject;
            Block block3b = Build.A.Block.WithNumber(3).WithDifficulty(4).WithExtraData(new byte[] {1}).WithParent(block2b).TestObject;

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

            Assert.AreEqual(3L, tree.BestKnownNumber, "best known");
            Assert.AreEqual(1L, tree.Head.Number, "head");
            Assert.AreEqual(1L, tree.BestSuggestedHeader.Number, "suggested");

            Assert.NotNull(blocksDb.Get(block1.Hash), "block 1");
            Assert.NotNull(blocksDb.Get(block2.Hash), "block 2");
            Assert.NotNull(blocksDb.Get(block3.Hash), "block 3");
            Assert.Null(blocksDb.Get(block1b.Hash), "block 1b");
            Assert.Null(blocksDb.Get(block2b.Hash), "block 2b");
            Assert.Null(blocksDb.Get(block3b.Hash), "block 3b");

            Assert.NotNull(blockInfosDb.Get(1), "level 1");
            Assert.NotNull(blockInfosDb.Get(2), "level 2");
            Assert.NotNull(blockInfosDb.Get(3), "level 3");
            
            Assert.NotNull(blockInfosDb.Get(1), "level 1b");
            Assert.NotNull(blockInfosDb.Get(2), "level 2b");
            Assert.NotNull(blockInfosDb.Get(3), "level 3b");

            repository.LoadLevel(1).BlockInfos.Length.Should().Be(1);
            repository.LoadLevel(2).BlockInfos.Length.Should().Be(1);
            repository.LoadLevel(3).BlockInfos.Length.Should().Be(1);
        }

        [Test]
        public void After_removing_invalid_block_will_not_accept_it_again()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();
            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, LimboLogs.Instance);
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
            Assert.AreEqual(AddBlockResult.InvalidBlock, result);
        }

        [Test]
        public void After_deleting_invalid_block_will_accept_other_blocks()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();
            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, LimboLogs.Instance);
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
            Assert.AreEqual(AddBlockResult.Added, result);
        }

        [Test]
        public void When_deleting_invalid_block_does_not_delete_blocks_that_are_not_its_descendants()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();
            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, LimboLogs.Instance);
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

            Assert.AreEqual(5L, tree.BestKnownNumber, "best known");
            Assert.AreEqual(block5.Header, tree.Head?.Header, "head");
            Assert.AreEqual(block5.Hash, tree.BestSuggestedHeader.Hash, "suggested");
        }

        [Test, TestCaseSource("SourceOfBSearchTestCases")]
        public void Loads_lowest_inserted_header_correctly(long beginIndex, long insertedBlocks)
        {
            long? expectedResult = insertedBlocks == 0L ? (long?) null : beginIndex - insertedBlocks + 1L;

            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();

            SyncConfig syncConfig = new SyncConfig();
            syncConfig.PivotNumber = beginIndex.ToString();

            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, syncConfig, LimboLogs.Instance);
            tree.SuggestBlock(Build.A.Block.Genesis.TestObject);

            for (long i = beginIndex; i > beginIndex - insertedBlocks; i--)
            {
                tree.Insert(Build.A.BlockHeader.WithNumber(i).WithTotalDifficulty(i).TestObject);
            }

            BlockTree loadedTree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, syncConfig, LimboLogs.Instance);

            Assert.AreEqual(expectedResult, tree.LowestInsertedHeader?.Number, "tree");
            Assert.AreEqual(expectedResult, loadedTree.LowestInsertedHeader?.Number, "loaded tree");
        }

        [Test, TestCaseSource("SourceOfBSearchTestCases")]
        public void Loads_lowest_inserted_body_correctly(long beginIndex, long insertedBlocks)
        {
            // left old code to prove that it does not matter for the result nowadays
            // we store and no longer binary search lowest body number
            
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();
            
            blocksDb.Set(0, Rlp.Encode(1L).Bytes);

            SyncConfig syncConfig = new SyncConfig();
            syncConfig.PivotNumber = beginIndex.ToString();

            var repo = new ChainLevelInfoRepository(blockInfosDb);
            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, repo, MainnetSpecProvider.Instance, NullBloomStorage.Instance, syncConfig, LimboLogs.Instance);
            tree.SuggestBlock(Build.A.Block.Genesis.TestObject);

            for (long i = beginIndex; i > beginIndex - insertedBlocks; i--)
            {
                Block block = Build.A.Block.WithNumber(i).WithTotalDifficulty(i).TestObject;
                tree.Insert(block.Header);
                tree.Insert(block);
            }

            var loadedRepo = new ChainLevelInfoRepository(blockInfosDb);
            BlockTree loadedTree = new BlockTree(blocksDb, headersDb, blockInfosDb, loadedRepo, MainnetSpecProvider.Instance, NullBloomStorage.Instance, syncConfig, LimboLogs.Instance);

            Assert.AreEqual(null, tree.LowestInsertedBodyNumber, "tree");
            Assert.AreEqual(1, loadedTree.LowestInsertedBodyNumber, "loaded tree");
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

        private ChainLevelInfoRepository _chainLevelInfoRepository;

        [Test, TestCaseSource(nameof(SourceOfBSearchTestCases))]
        public void Loads_best_known_correctly_on_inserts(long beginIndex, long insertedBlocks)
        {
            long expectedResult = insertedBlocks == 0L ? 0L : beginIndex;

            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();

            SyncConfig syncConfig = new SyncConfig();
            syncConfig.PivotNumber = beginIndex.ToString();

            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, syncConfig, LimboLogs.Instance);
            tree.SuggestBlock(Build.A.Block.Genesis.TestObject);

            for (long i = beginIndex; i > beginIndex - insertedBlocks; i--)
            {
                Block block = Build.A.Block.WithNumber(i).WithTotalDifficulty(i).TestObject;
                tree.Insert(block.Header);
                tree.Insert(block);
            }

            BlockTree loadedTree = new BlockTree(
                blocksDb,
                headersDb,
                blockInfosDb,
                new ChainLevelInfoRepository(blockInfosDb),
                MainnetSpecProvider.Instance,
                NullBloomStorage.Instance,
                syncConfig,
                LimboLogs.Instance);

            Assert.AreEqual(expectedResult, tree.BestKnownNumber, "tree");
            Assert.AreEqual(expectedResult, loadedTree.BestKnownNumber, "loaded tree");
        }

        [TestCase(1L)]
        [TestCase(2L)]
        [TestCase(3L)]
        public void Loads_best_known_correctly_on_inserts_followed_by_suggests(long pivotNumber)
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();

            SyncConfig syncConfig = new SyncConfig();
            syncConfig.PivotNumber = pivotNumber.ToString();

            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, syncConfig, LimboLogs.Instance);
            tree.SuggestBlock(Build.A.Block.Genesis.TestObject);

            Block pivotBlock = null;
            for (long i = pivotNumber; i > 0; i--)
            {
                Block block = Build.A.Block.WithNumber(i).WithTotalDifficulty(i).TestObject;
                pivotBlock ??= block;
                tree.Insert(block.Header);
            }

            tree.SuggestHeader(Build.A.BlockHeader.WithNumber(pivotNumber + 1).WithParent(pivotBlock!.Header).TestObject);

            BlockTree loadedTree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, syncConfig, LimboLogs.Instance);

            Assert.AreEqual(pivotNumber + 1, tree.BestKnownNumber, "tree");
            Assert.AreEqual(1, tree.LowestInsertedHeader?.Number, "loaded tree - lowest header");
            Assert.AreEqual(null, tree.LowestInsertedBodyNumber, "loaded tree - lowest body");
            Assert.AreEqual(pivotNumber + 1, loadedTree.BestKnownNumber, "loaded tree");
        }
        
        [Test]
        public void Loads_best_known_correctly_when_head_before_pivot()
        {
            var pivotNumber = 1000;
            var head = 10;
            SyncConfig syncConfig = new SyncConfig {PivotNumber = pivotNumber.ToString()};

            var treeBuilder = Build.A.BlockTree().OfChainLength(head + 1);
            
            BlockTree loadedTree = new BlockTree(
                treeBuilder.BlocksDb,
                treeBuilder.HeadersDb,
                treeBuilder.BlockInfoDb,
                treeBuilder.ChainLevelInfoRepository,
                MainnetSpecProvider.Instance,
                NullBloomStorage.Instance,
                syncConfig,
                LimboLogs.Instance);
            
            Assert.AreEqual(head, loadedTree.BestKnownNumber, "loaded tree");
        }

        [Test]
        public void Cannot_insert_genesis()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();

            long pivotNumber = 0L;

            SyncConfig syncConfig = new SyncConfig();
            syncConfig.PivotNumber = pivotNumber.ToString();

            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, syncConfig, LimboLogs.Instance);
            Block genesis = Build.A.Block.Genesis.TestObject;
            tree.SuggestBlock(genesis);
            Assert.Throws<InvalidOperationException>(() => tree.Insert(genesis));
            Assert.Throws<InvalidOperationException>(() => tree.Insert(genesis.Header));
            Assert.Throws<InvalidOperationException>(() => tree.Insert(new[] {genesis}));
        }

        [Test]
        public void Can_batch_insert_blocks()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();

            long pivotNumber = 5L;

            SyncConfig syncConfig = new SyncConfig();
            syncConfig.PivotNumber = pivotNumber.ToString();

            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, syncConfig, LimboLogs.Instance);
            tree.SuggestBlock(Build.A.Block.Genesis.TestObject);

            List<Block> blocks = new List<Block>();
            for (long i = 5; i > 0; i--)
            {
                Block block = Build.A.Block.WithNumber(i).WithTotalDifficulty(1L).TestObject;
                tree.Insert(block.Header);
                blocks.Add(block);
            }

            tree.Insert(blocks);
        }

        [Test]
        public void Inserts_blooms()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();

            long pivotNumber = 5L;

            SyncConfig syncConfig = new SyncConfig();
            syncConfig.PivotNumber = pivotNumber.ToString();

            var bloomStorage = Substitute.For<IBloomStorage>();
            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainnetSpecProvider.Instance, bloomStorage, syncConfig, LimboLogs.Instance);
            tree.SuggestBlock(Build.A.Block.Genesis.TestObject);

            for (long i = 5; i > 0; i--)
            {
                Block block = Build.A.Block.WithNumber(i).WithTotalDifficulty(1L).TestObject;
                tree.Insert(block.Header);
                bloomStorage.Received().Store(block.Header.Number, block.Bloom);
            }
        }

        [Test]
        public void Block_loading_is_lazy()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();

            SyncConfig syncConfig = new SyncConfig();
            syncConfig.PivotNumber = 0L.ToString();

            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, syncConfig, LimboLogs.Instance);
            tree.SuggestBlock(genesis);

            Block previousBlock = genesis;
            for (int i = 1; i < 10; i++)
            {
                Block block = Build.A.Block.WithNumber(i).WithParent(previousBlock).TestObject;
                tree.SuggestBlock(block);
                previousBlock = block;
            }

            Block lastBlock = previousBlock;

            BlockTree loadedTree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainnetSpecProvider.Instance, NullBloomStorage.Instance, syncConfig, LimboLogs.Instance);
            loadedTree.FindHeader(lastBlock.Hash, BlockTreeLookupOptions.None);
        }

        [Test]
        public void When_block_is_moved_to_main_blooms_are_stored()
        {
            MemDb blocksDb = new MemDb();
            MemDb headersDb = new MemDb();
            MemDb blockInfosDb = new MemDb();

            Transaction t1 = Build.A.Transaction.TestObject;
            Transaction t2 = Build.A.Transaction.TestObject;

            var bloomStorage = Substitute.For<IBloomStorage>();
            BlockTree blockTree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), OlympicSpecProvider.Instance, bloomStorage, LimboLogs.Instance);
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1A = Build.A.Block.WithNumber(1).WithDifficulty(2).WithTransactions(t1).WithParent(block0).TestObject;
            Block block1B = Build.A.Block.WithNumber(1).WithDifficulty(3).WithTransactions(t2).WithParent(block0).TestObject;

            AddToMain(blockTree, block0);

            blockTree.SuggestBlock(block1B);
            blockTree.SuggestBlock(block1A);
            blockTree.UpdateMainChain(block1A);

            bloomStorage.Received().Store(block1A.Number, block1A.Bloom);
        }


        [Test]
        public void Can_find_genesis_level()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            ChainLevelInfo info = blockTree.FindLevel(0);
            Assert.True(info.HasBlockOnMainChain);
            Assert.AreEqual(1, info.BlockInfos.Length);
        }

        [Test]
        public void Can_find_some_level()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            ChainLevelInfo info = blockTree.FindLevel(1);
            Assert.True(info.HasBlockOnMainChain);
            Assert.AreEqual(1, info.BlockInfos.Length);
        }

        [Test]
        public void Cannot_find_future_level()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            ChainLevelInfo info = blockTree.FindLevel(1000);
            Assert.IsNull(info);
        }

        [Test]
        public void Can_delete_a_future_slice()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            blockTree.DeleteChainSlice(1000, 2000);
            Assert.AreEqual(2, blockTree.Head.Number);
        }

        [Test]
        public void Can_delete_slice()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            blockTree.DeleteChainSlice(2, 2);
            Assert.Null(blockTree.FindBlock(2, BlockTreeLookupOptions.None));
            Assert.Null(blockTree.FindHeader(2, BlockTreeLookupOptions.None));
            Assert.Null(blockTree.FindLevel(2));
        }

        [Test]
        public void Does_not_delete_outside_of_the_slice()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            blockTree.DeleteChainSlice(2, 2);
            Assert.NotNull(blockTree.FindBlock(1, BlockTreeLookupOptions.None));
            Assert.NotNull(blockTree.FindHeader(1, BlockTreeLookupOptions.None));
            Assert.NotNull(blockTree.FindLevel(1));
        }

        [Test]
        public void Can_delete_one_block()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            blockTree.DeleteChainSlice(2, 2);
            Assert.AreEqual(1, blockTree.Head.Number);
        }

        [Test]
        public void Can_delete_two_blocks()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            blockTree.DeleteChainSlice(1, 2);
            Assert.Null(blockTree.FindLevel(1));
            Assert.Null(blockTree.FindLevel(2));
        }

        [Test]
        public void Can_delete_in_the_middle()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            blockTree.DeleteChainSlice(1, 1);
        }

        [Test]
        public void Throws_when_start_after_end()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            Assert.Throws<ArgumentException>(() => blockTree.DeleteChainSlice(2, 1));
        }

        [Test]
        public void Throws_when_start_at_zero()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            Assert.Throws<ArgumentException>(() => blockTree.DeleteChainSlice(0, 1));
        }

        [Test]
        public void Throws_when_start_below_zero()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            Assert.Throws<ArgumentException>(() => blockTree.DeleteChainSlice(-1, 1));
        }

        [Test]
        public void Cannot_delete_too_many()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            Assert.Throws<ArgumentException>(() => blockTree.DeleteChainSlice(1000, 52001));
        }
        
        [Test]
        public void Cannot_add_blocks_when_blocked()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            blockTree.BlockAcceptingNewBlocks();
            blockTree.SuggestBlock(Build.A.Block.WithNumber(3).TestObject).Should().Be(AddBlockResult.CannotAccept);
        }
        
        [Test]
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

        [TestCase(10, 10000000ul)]
        [TestCase(4, 4000000ul)]
        [TestCase(10, null)]
        public void Recovers_total_difficulty(int chainLength, ulong? expectedTotalDifficulty)
        {
            BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree().OfChainLength(chainLength);
            BlockTree blockTree = blockTreeBuilder.TestObject;
            int chainLeft = expectedTotalDifficulty.HasValue ? 1 : 0;
            for (int i = chainLength - 1; i >= chainLeft; i--)
            {
                var level = blockTreeBuilder.ChainLevelInfoRepository.LoadLevel(i);
                for (int j = 0; j < level.BlockInfos.Length; j++)
                {
                    Keccak blockHash = level.BlockInfos[j].BlockHash;
                    var header = blockTree.FindHeader(blockHash, BlockTreeLookupOptions.None);
                    header.TotalDifficulty = null;
                }
                blockTreeBuilder.ChainLevelInfoRepository.Delete(i);
            }

            if (expectedTotalDifficulty.HasValue)
            {
                blockTree.FindBlock(blockTree.Head.Hash, BlockTreeLookupOptions.None).TotalDifficulty.Should().Be(new UInt256(expectedTotalDifficulty.Value));
                for (int i = chainLength - 1; i >= chainLeft; i--)
                {
                    var level = blockTreeBuilder.ChainLevelInfoRepository.LoadLevel(i);
                    level.Should().NotBeNull();
                    level.BlockInfos.Should().HaveCount(1);
                }
            }
            else
            {
                Action action = () => blockTree.FindBlock(blockTree.Head.Hash, BlockTreeLookupOptions.None);
                action.Should().Throw<InvalidOperationException>();
            }
        }
        
        [Test]
        public async Task Visitor_can_block_adding_blocks()
        {
            BlockTree blockTree = Build.A.BlockTree().OfChainLength(3).TestObject;
            var manualResetEvent = new ManualResetEvent(false);
            var acceptTask = blockTree.Accept(new TestBlockTreeVisitor(manualResetEvent), CancellationToken.None);
            blockTree.CanAcceptNewBlocks.Should().BeFalse();
            manualResetEvent.Set();
            await acceptTask;
        }

        private class TestBlockTreeVisitor : IBlockTreeVisitor
        {
            private readonly ManualResetEvent _manualResetEvent;
            private bool _wait = true;

            public TestBlockTreeVisitor(ManualResetEvent manualResetEvent)
            {
                _manualResetEvent = manualResetEvent;
            }

            public bool PreventsAcceptingNewBlocks { get; } = true;
            public long StartLevelInclusive { get; } = 0;
            public long EndLevelExclusive { get; } = 3;
            public async Task<LevelVisitOutcome> VisitLevelStart(ChainLevelInfo chainLevelInfo, long levelNumber, CancellationToken cancellationToken)
            {
                if (_wait)
                {
                    await _manualResetEvent.WaitOneAsync(cancellationToken);
                    _wait = false;
                }

                return LevelVisitOutcome.None;
            }

            public Task<bool> VisitMissing(Keccak hash, CancellationToken cancellationToken)
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
