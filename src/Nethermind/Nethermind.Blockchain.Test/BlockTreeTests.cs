/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.TxPools;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Store;
using Nethermind.Store.Repositories;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test
{
    [TestFixture]
    public class BlockTreeTests
    {
        private BlockTree BuildBlockTree()
        {
            _blocksDb = new MemDb();
            _headersDb = new MemDb();
            _blocksInfosDb = new MemDb();
            return new BlockTree(_blocksDb, _headersDb, _blocksInfosDb, new ChainLevelInfoRepository(_blocksInfosDb),  MainNetSpecProvider.Instance, NullTxPool.Instance, LimboLogs.Instance);
        }

        private static void AddToMain(BlockTree blockTree, Block block0)
        {
            blockTree.SuggestBlock(block0);
            blockTree.UpdateMainChain(new[] {block0});
        }

        [Test]
        public void Add_genesis_shall_notify()
        {
            bool hasNotified = false;
            BlockTree blockTree = BuildBlockTree();
            blockTree.NewHeadBlock += (sender, args) => { hasNotified = true; };

            Block block = Build.A.Block.WithNumber(0).TestObject;
            var result = blockTree.SuggestBlock(block);
            blockTree.UpdateMainChain(block);

            Assert.True(hasNotified, "notification");
            Assert.AreEqual(AddBlockResult.Added, result, "result");
        }

        [Test]
        public void Add_genesis_shall_work_even_with_0_difficulty()
        {
            bool hasNotified = false;
            BlockTree blockTree = BuildBlockTree();
            blockTree.NewBestSuggestedBlock += (sender, args) => { hasNotified = true; };

            Block block = Build.A.Block.WithNumber(0).WithDifficulty(0).TestObject;
            var result = blockTree.SuggestBlock(block);

            Assert.True(hasNotified, "notification");
            Assert.AreEqual(AddBlockResult.Added, result, "result");
        }

        [Test]
        public void Can_only_add_genesis_once()
        {
            BlockTree blockTree = BuildBlockTree();
            Block blockA = Build.A.Block.WithNumber(0).TestObject;
            Block blockB = Build.A.Block.WithNumber(0).TestObject;
            blockTree.SuggestBlock(blockA);
            Assert.Throws<InvalidOperationException>(() => blockTree.SuggestBlock(blockB));
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
            var result = blockTree.SuggestBlock(block1);
            blockTree.UpdateMainChain(block1);

            Assert.True(hasNotified, "notification");
            Assert.AreEqual(AddBlockResult.Added, result, "result");
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
            var result = blockTree.SuggestBlock(block1);

            Assert.True(hasNotified, "notification");
            Assert.AreEqual(AddBlockResult.Added, result, "result");
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
            var result = blockTree.SuggestBlock(block2);

            Assert.False(hasNotifiedBest, "notification best");
            Assert.False(hasNotifiedHead, "notification head");
            Assert.AreEqual(AddBlockResult.Added, result, "result");
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
        public void Can_load_best_known_up_to_256million()
        {
            _blocksDb = new MemDb();
            _headersDb = new MemDb();
            IDb blocksInfosDb = Substitute.For<IDb>();

            Rlp chainLevel = Rlp.Encode(new ChainLevelInfo(true, new BlockInfo[] {new BlockInfo(TestItem.KeccakA, 1)}));
            blocksInfosDb[Arg.Any<byte[]>()].Returns(chainLevel.Bytes);

            BlockTree blockTree = new BlockTree(_blocksDb, _headersDb, blocksInfosDb, new ChainLevelInfoRepository(blocksInfosDb), MainNetSpecProvider.Instance, NullTxPool.Instance, LimboLogs.Instance);


            Assert.AreEqual(255_999_998, blockTree.BestKnownNumber);
        }

        [Test]
        public void Add_and_find_branch()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block = Build.A.Block.TestObject;
            blockTree.SuggestBlock(block);
            Block found = blockTree.FindBlock(block.Hash, BlockTreeLookupOptions.None);
            Assert.AreEqual(block.Hash, BlockHeader.CalculateHash(found.Header));
        }

        [Test]
        public void Add_on_branch_move_find()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block = Build.A.Block.TestObject;
            AddToMain(blockTree, block);
            Block found = blockTree.FindBlock(block.Hash, BlockTreeLookupOptions.RequireCanonical);
            Assert.AreEqual(block.Hash, BlockHeader.CalculateHash(found.Header));
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
            Assert.AreEqual(block2.Hash, BlockHeader.CalculateHash(found.Header));
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
            Assert.AreEqual(block0.Hash, BlockHeader.CalculateHash(blocks[0]));
            Assert.AreEqual(block1.Hash, BlockHeader.CalculateHash(blocks[1]));
            Assert.AreEqual(block2.Hash, BlockHeader.CalculateHash(blocks[2]));
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
            Assert.AreEqual(block1.Hash, BlockHeader.CalculateHash(blocks[0]));
            Assert.AreEqual(block2.Hash, BlockHeader.CalculateHash(blocks[1]));
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
            Assert.AreEqual(block0.Hash, BlockHeader.CalculateHash(blocks[0]));
            Assert.AreEqual(block1.Hash, BlockHeader.CalculateHash(blocks[1]));
            Assert.AreEqual(block2.Hash, BlockHeader.CalculateHash(blocks[2]));
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

            Assert.AreEqual(block2.Hash, BlockHeader.CalculateHash(blocks[0]));
            Assert.AreEqual(block0.Hash, BlockHeader.CalculateHash(blocks[2]));
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
            Assert.AreEqual(block0.Hash, BlockHeader.CalculateHash(blocks[0]));
            Assert.AreEqual(block2.Hash, BlockHeader.CalculateHash(blocks[1]));
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
            Assert.AreEqual(3, (int) block1.TotalDifficulty);
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

            Assert.AreEqual(block1.Hash, BlockHeader.CalculateHash(blockTree.Head));
        }

        [Test]
        public void Best_suggested_block_gets_updated()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            AddToMain(blockTree, block0);
            blockTree.SuggestBlock(block1);

            Assert.AreEqual(block0.Hash, BlockHeader.CalculateHash(blockTree.Head), "head block");
            Assert.AreEqual(block1.Hash, BlockHeader.CalculateHash(blockTree.BestSuggestedHeader), "best suggested");
        }

        [Test]
        public void Sets_genesis_block()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            AddToMain(blockTree, block0);

            Assert.AreEqual(block0.Hash, BlockHeader.CalculateHash(blockTree.Genesis));
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

            Assert.AreEqual(block1B.Hash, BlockHeader.CalculateHash(found.Header));
        }

        [Test]
        public void Can_init_head_block_from_db_by_header()
        {
            Block genesisBlock = Build.A.Block.Genesis.TestObject;
            Block headBlock = genesisBlock;

            MemDb blocksDb = new MemDb();
            MemDb headersDb = new MemDb();
            blocksDb.Set(genesisBlock.Hash, Rlp.Encode(genesisBlock).Bytes);
            headersDb.Set(genesisBlock.Hash, Rlp.Encode(genesisBlock.Header).Bytes);

            MemDb blockInfosDb = new MemDb();
            blockInfosDb.Set(Keccak.Zero, Rlp.Encode(genesisBlock.Header).Bytes);

            ChainLevelInfo level = new ChainLevelInfo(true, new BlockInfo[1] {new BlockInfo(headBlock.Hash, headBlock.Difficulty)});
            level.BlockInfos[0].WasProcessed = true;

            blockInfosDb.Set(0, Rlp.Encode(level).Bytes);

            BlockTree blockTree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), OlympicSpecProvider.Instance, Substitute.For<ITxPool>(), LimboLogs.Instance);
            Assert.AreEqual(headBlock.Hash, blockTree.Head?.Hash, "head");
            Assert.AreEqual(headBlock.Hash, blockTree.Genesis?.Hash, "genesis");
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
            ChainLevelInfo level = new ChainLevelInfo(true, new BlockInfo[1] {new BlockInfo(headBlock.Hash, headBlock.Difficulty)});
            level.BlockInfos[0].WasProcessed = true;

            blockInfosDb.Set(0, Rlp.Encode(level).Bytes);

            BlockTree blockTree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), OlympicSpecProvider.Instance, Substitute.For<ITxPool>(), LimboLogs.Instance);
            Assert.AreEqual(headBlock.Hash, blockTree.Head?.Hash, "head");
            Assert.AreEqual(headBlock.Hash, blockTree.Genesis?.Hash, "genesis");
        }

        [Test]
        public async Task Can_load_blocks_from_db()
        {
            for (int chainLength = 1; chainLength <= 32; chainLength++)
            {
                Block genesisBlock = Build.A.Block.Genesis.TestObject;

                MemDb blocksDb = new MemDb();
                MemDb blockInfosDb = new MemDb();
                MemDb headersDb = new MemDb();

                BlockTree testTree = Build.A.BlockTree(genesisBlock).OfChainLength(chainLength).TestObject;
                for (int i = 0; i < testTree.Head.Number + 1; i++)
                {
                    Block ithBlock = testTree.FindBlock(i, BlockTreeLookupOptions.None);
                    blocksDb.Set(ithBlock.Hash, Rlp.Encode(ithBlock).Bytes);

                    ChainLevelInfo ithLevel = new ChainLevelInfo(true, new BlockInfo[1] {new BlockInfo(ithBlock.Hash, ithBlock.TotalDifficulty.Value)});
                    blockInfosDb.Set(i, Rlp.Encode(ithLevel).Bytes);
                }

                blocksDb.Set(Keccak.Zero, Rlp.Encode(genesisBlock).Bytes);

                BlockTree blockTree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), OlympicSpecProvider.Instance, Substitute.For<ITxPool>(), LimboLogs.Instance);
                await blockTree.LoadBlocksFromDb(CancellationToken.None);

                Assert.AreEqual(blockTree.BestSuggestedHeader.Hash, testTree.Head.Hash, $"head {chainLength}");
            }
        }

        [Test]
        public async Task Can_load_blocks_from_db_odd()
        {
            for (int chainLength = 2; chainLength <= 32; chainLength++)
            {
                Block genesisBlock = Build.A.Block.Genesis.TestObject;

                MemDb blocksDb = new MemDb();
                MemDb blockInfosDb = new MemDb();
                MemDb headersDb = new MemDb();

                BlockTree testTree = Build.A.BlockTree(genesisBlock).OfChainLength(chainLength).TestObject;
                for (int i = 0; i < testTree.Head.Number + 1; i++)
                {
                    Block ithBlock = testTree.FindBlock(i, BlockTreeLookupOptions.None);
                    blocksDb.Set(ithBlock.Hash, Rlp.Encode(ithBlock).Bytes);

                    ChainLevelInfo ithLevel = new ChainLevelInfo(true, new BlockInfo[1] {new BlockInfo(ithBlock.Hash, ithBlock.TotalDifficulty.Value)});
                    blockInfosDb.Set(i, Rlp.Encode(ithLevel).Bytes);
                }

                blocksDb.Set(Keccak.Zero, Rlp.Encode(testTree.FindBlock(1, BlockTreeLookupOptions.None)).Bytes);

                BlockTree blockTree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), OlympicSpecProvider.Instance, Substitute.For<ITxPool>(), LimboLogs.Instance);
                await blockTree.LoadBlocksFromDb(CancellationToken.None);

                Assert.AreEqual(blockTree.BestSuggestedHeader.Hash, testTree.Head.Hash, $"head {chainLength}");
            }
        }

        [Test]
        public void Sets_head_block_hash_in_db_on_new_head_block()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();

            BlockTree blockTree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), OlympicSpecProvider.Instance, Substitute.For<ITxPool>(), LimboLogs.Instance);
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
            blockTree.UpdateMainChain(new[] {block0, block1});
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

        [Test(Description = "There was a bug where we switched positions and used the index from before the positions were switched")]
        public void When_moving_to_main_one_of_the_two_blocks_at_given_level_the_was_processed_check_is_executed_on_the_correct_block_index_regression()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();

            BlockTree blockTree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), OlympicSpecProvider.Instance, Substitute.For<ITxPool>(), LimboLogs.Instance);
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
            Assert.AreEqual(block1.Header, tree.Head);
            Assert.AreEqual(block1.Header, tree.BestSuggestedHeader);
        }

        private int _dbLoadTimeout = 5000;

        [Test]
        public void When_deleting_invalid_block_deletes_its_descendants()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();
            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainNetSpecProvider.Instance, NullTxPool.Instance, LimboLogs.Instance);
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
        public async Task Cleans_invalid_blocks_before_starting_DB_load()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();
            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainNetSpecProvider.Instance, NullTxPool.Instance, LimboLogs.Instance);
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
            Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;

            tree.SuggestBlock(block0);
            tree.SuggestBlock(block1);
            tree.SuggestBlock(block2);
            tree.SuggestBlock(block3);

            blockInfosDb.Set(BlockTree.DeletePointerAddressInDb, block1.Hash.Bytes);

            CancellationTokenSource tokenSource = new CancellationTokenSource();
#pragma warning disable 4014
            Task.Delay(_dbLoadTimeout).ContinueWith(t => tokenSource.Cancel());
#pragma warning restore 4014
            await tree.LoadBlocksFromDb(tokenSource.Token);

            Assert.AreEqual(0L, tree.BestKnownNumber, "best known");
            Assert.AreEqual(null, tree.Head, "head");
            Assert.AreEqual(0L, tree.BestSuggestedHeader.Number, "suggested");

            Assert.IsNull(blocksDb.Get(block2.Hash), "block 1");
            Assert.IsNull(blocksDb.Get(block2.Hash), "block 2");
            Assert.IsNull(blocksDb.Get(block3.Hash), "block 3");

            Assert.IsNull(blockInfosDb.Get(2), "level 1");
            Assert.IsNull(blockInfosDb.Get(2), "level 2");
            Assert.IsNull(blockInfosDb.Get(3), "level 3");
        }

        [Test]
        public void After_removing_invalid_block_will_not_accept_it_again()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();
            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainNetSpecProvider.Instance, NullTxPool.Instance, LimboLogs.Instance);
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
            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainNetSpecProvider.Instance, NullTxPool.Instance, LimboLogs.Instance);
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
        public void When_head_block_is_followed_by_a_block_bodies_gap_it_should_delete_all_levels_after_the_gap_start()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();
            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainNetSpecProvider.Instance, NullTxPool.Instance, LimboLogs.Instance);
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
            Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;
            Block block4 = Build.A.Block.WithNumber(4).WithDifficulty(5).WithParent(block3).TestObject;
            Block block5 = Build.A.Block.WithNumber(5).WithDifficulty(6).WithParent(block4).TestObject;

            tree.SuggestBlock(block0);
            tree.SuggestBlock(block1);
            tree.SuggestBlock(block2);
            tree.SuggestHeader(block3.Header);
            tree.SuggestHeader(block4.Header);
            tree.SuggestBlock(block5);

            tree.UpdateMainChain(block2);

            tree.FixFastSyncGaps(CancellationToken.None);

            Assert.Null(blockInfosDb.Get(3), "level 3");
            Assert.Null(blockInfosDb.Get(4), "level 4");
            Assert.Null(blockInfosDb.Get(5), "level 5");

            Assert.AreEqual(2L, tree.BestKnownNumber, "best known");
            Assert.AreEqual(block2.Header, tree.Head, "head");
            Assert.AreEqual(block2.Hash, tree.BestSuggestedHeader.Hash, "suggested");
        }

        [Test]
        public void When_deleting_invalid_block_does_not_delete_blocks_that_are_not_its_descendants()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();
            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainNetSpecProvider.Instance, NullTxPool.Instance, LimboLogs.Instance);
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
            Assert.AreEqual(block5.Header, tree.Head, "head");
            Assert.AreEqual(block5.Hash, tree.BestSuggestedHeader.Hash, "suggested");
        }

        [Test]
        public async Task When_cleaning_descendants_of_invalid_does_not_touch_other_branches()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();
            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainNetSpecProvider.Instance, NullTxPool.Instance, LimboLogs.Instance);
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

            CancellationTokenSource tokenSource = new CancellationTokenSource();
#pragma warning disable 4014
            Task.Delay(_dbLoadTimeout).ContinueWith(t => tokenSource.Cancel());
#pragma warning restore 4014
            await tree.LoadBlocksFromDb(tokenSource.Token);

            Assert.AreEqual(3L, tree.BestKnownNumber, "best known");
            Assert.AreEqual(null, tree.Head, "head");
            Assert.AreEqual(block3B.Hash, tree.BestSuggestedHeader.Hash, "suggested");

            Assert.IsNull(blocksDb.Get(block1.Hash), "block 1");
            Assert.IsNull(blocksDb.Get(block2.Hash), "block 2");
            Assert.IsNull(blocksDb.Get(block3.Hash), "block 3");

            Assert.NotNull(blockInfosDb.Get(1), "level 1");
            Assert.NotNull(blockInfosDb.Get(2), "level 2");
            Assert.NotNull(blockInfosDb.Get(3), "level 3");
        }

        [Test]
        public async Task Can_load_from_DB_when_there_is_an_invalid_block_in_DB_and_a_valid_branch()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();
            BlockTree tree1 = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainNetSpecProvider.Instance, NullTxPool.Instance, LimboLogs.Instance);

            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
            Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;

            Block block1B = Build.A.Block.WithNumber(1).WithDifficulty(1).WithParent(block0).TestObject;
            Block block2B = Build.A.Block.WithNumber(2).WithDifficulty(1).WithParent(block1B).TestObject;
            Block block3B = Build.A.Block.WithNumber(3).WithDifficulty(1).WithParent(block2B).TestObject;

            tree1.SuggestBlock(block0);
            tree1.SuggestBlock(block1);
            tree1.SuggestBlock(block2);
            tree1.SuggestBlock(block3);

            tree1.SuggestBlock(block1B);
            tree1.SuggestBlock(block2B);
            tree1.SuggestBlock(block3B);

            tree1.UpdateMainChain(block0);

            BlockTree tree2 = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainNetSpecProvider.Instance, NullTxPool.Instance, LimboLogs.Instance);

            CancellationTokenSource tokenSource = new CancellationTokenSource();
#pragma warning disable 4014
            Task.Delay(_dbLoadTimeout).ContinueWith(t => tokenSource.Cancel());
#pragma warning restore 4014

            tree2.NewBestSuggestedBlock += (sender, args) =>
            {
                if (args.Block.Hash == block1.Hash)
                {
                    tree2.DeleteInvalidBlock(args.Block);
                }
                else
                {
                    tree2.UpdateMainChain(args.Block);
                }
            };

            await tree2.LoadBlocksFromDb(tokenSource.Token, startBlockNumber: null, batchSize: 1);

            /* note the block tree historically loads one less block than it could */

            Assert.AreEqual(3L, tree2.BestKnownNumber, "best known");
            Assert.AreEqual(block2B.Hash, tree2.Head.Hash, "head");
            Assert.AreEqual(block2B.Hash, tree2.BestSuggestedHeader.Hash, "suggested");

            Assert.IsNull(blocksDb.Get(block1.Hash), "block 1");
            Assert.IsNull(blocksDb.Get(block2.Hash), "block 2");
            Assert.IsNull(blocksDb.Get(block3.Hash), "block 3");

            Assert.NotNull(blockInfosDb.Get(1), "level 1");
            Assert.NotNull(blockInfosDb.Get(2), "level 2");
            Assert.NotNull(blockInfosDb.Get(3), "level 3");
        }

        [Test]
        public async Task Can_load_from_DB_when_there_is_only_an_invalid_chain_in_DB()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();
            BlockTree tree1 = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainNetSpecProvider.Instance, NullTxPool.Instance, LimboLogs.Instance);

            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
            Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;

            tree1.SuggestBlock(block0);
            tree1.SuggestBlock(block1);
            tree1.SuggestBlock(block2);
            tree1.SuggestBlock(block3);

            tree1.UpdateMainChain(block0);

            BlockTree tree2 = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainNetSpecProvider.Instance, NullTxPool.Instance, LimboLogs.Instance);

            CancellationTokenSource tokenSource = new CancellationTokenSource();
#pragma warning disable 4014
            Task.Delay(_dbLoadTimeout).ContinueWith(t => tokenSource.Cancel());
#pragma warning restore 4014

            tree2.NewBestSuggestedBlock += (sender, args) =>
            {
                if (args.Block.Hash == block1.Hash)
                {
                    tree2.DeleteInvalidBlock(args.Block);
                }
                else
                {
                    tree2.UpdateMainChain(args.Block);
                }
            };

            await tree2.LoadBlocksFromDb(tokenSource.Token, startBlockNumber: null, batchSize: 1);

            /* note the block tree historically loads one less block than it could */

            Assert.AreEqual(0L, tree2.BestKnownNumber, "best known");
            Assert.AreEqual(block0.Hash, tree2.Head.Hash, "head");
            Assert.AreEqual(block0.Hash, tree2.BestSuggestedHeader.Hash, "suggested");

            Assert.IsNull(blocksDb.Get(block1.Hash), "block 1");
            Assert.IsNull(blocksDb.Get(block2.Hash), "block 2");
            Assert.IsNull(blocksDb.Get(block3.Hash), "block 3");

            Assert.IsNull(blockInfosDb.Get(1), "level 1");
            Assert.IsNull(blockInfosDb.Get(2), "level 2");
            Assert.IsNull(blockInfosDb.Get(3), "level 3");
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

            BlockTree tree = new BlockTree(blocksDb, headersDb,blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainNetSpecProvider.Instance, NullTxPool.Instance, syncConfig, LimboLogs.Instance);
            tree.SuggestBlock(Build.A.Block.Genesis.TestObject);

            for (long i = beginIndex; i > beginIndex - insertedBlocks; i--)
            {
                tree.Insert(Build.A.BlockHeader.WithNumber(i).TestObject);
            }

            BlockTree loadedTree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainNetSpecProvider.Instance, NullTxPool.Instance, syncConfig, LimboLogs.Instance);

            Assert.AreEqual(expectedResult, tree.LowestInsertedHeader?.Number, "tree");
            Assert.AreEqual(expectedResult, loadedTree.LowestInsertedHeader?.Number, "loaded tree");
        }

        [Test, TestCaseSource("SourceOfBSearchTestCases")]
        public void Loads_lowest_inserted_body_correctly(long beginIndex, long insertedBlocks)
        {
            long? expectedResult = insertedBlocks == 0L ? (long?) null : beginIndex - insertedBlocks + 1L;

            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();

            SyncConfig syncConfig = new SyncConfig();
            syncConfig.PivotNumber = beginIndex.ToString();

            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainNetSpecProvider.Instance, NullTxPool.Instance, syncConfig, LimboLogs.Instance);
            tree.SuggestBlock(Build.A.Block.Genesis.TestObject);

            for (long i = beginIndex; i > beginIndex - insertedBlocks; i--)
            {
                Block block = Build.A.Block.WithNumber(i).TestObject;
                tree.Insert(block.Header);
                tree.Insert(block);
            }

            BlockTree loadedTree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainNetSpecProvider.Instance, NullTxPool.Instance, syncConfig, LimboLogs.Instance);

            Assert.AreEqual(expectedResult, tree.LowestInsertedBody?.Number, "tree");
            Assert.AreEqual(expectedResult, loadedTree.LowestInsertedBody?.Number, "loaded tree");
        }

        [Test, TestCaseSource("SourceOfBSearchTestCases")]
        public void Loads_best_known_correctly_on_inserts(long beginIndex, long insertedBlocks)
        {
            long expectedResult = insertedBlocks == 0L ? 0L : beginIndex;

            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();

            SyncConfig syncConfig = new SyncConfig();
            syncConfig.PivotNumber = beginIndex.ToString();

            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainNetSpecProvider.Instance, NullTxPool.Instance, syncConfig, LimboLogs.Instance);
            tree.SuggestBlock(Build.A.Block.Genesis.TestObject);

            for (long i = beginIndex; i > beginIndex - insertedBlocks; i--)
            {
                Block block = Build.A.Block.WithNumber(i).TestObject;
                tree.Insert(block.Header);
                tree.Insert(block);
            }

            BlockTree loadedTree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainNetSpecProvider.Instance, NullTxPool.Instance, syncConfig, LimboLogs.Instance);

            Assert.AreEqual(expectedResult, tree.BestKnownNumber, "tree");
            Assert.AreEqual(expectedResult, loadedTree.BestKnownNumber, "loaded tree");
        }

        [TestCase(1L)]
        [TestCase(2L)]
        [TestCase(3L)]
        public void Loads_best_known_correctly_on_inserts_followed_by_suggests(long pivotNumber)
        {
            long expectedResult = pivotNumber + 1;

            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();

            SyncConfig syncConfig = new SyncConfig();
            syncConfig.PivotNumber = pivotNumber.ToString();

            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainNetSpecProvider.Instance, NullTxPool.Instance, syncConfig, LimboLogs.Instance);
            tree.SuggestBlock(Build.A.Block.Genesis.TestObject);

            Block pivotBlock = null;
            for (long i = pivotNumber; i > 0; i--)
            {
                Block block = Build.A.Block.WithNumber(i).TestObject;
                if (pivotBlock == null) pivotBlock = block;
                tree.Insert(block.Header);
            }

            tree.SuggestHeader(Build.A.BlockHeader.WithNumber(pivotNumber + 1).WithParent(pivotBlock.Header).TestObject);

            BlockTree loadedTree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainNetSpecProvider.Instance, NullTxPool.Instance, syncConfig, LimboLogs.Instance);

            Assert.AreEqual(pivotNumber + 1, tree.BestKnownNumber, "tree");
            Assert.AreEqual(1, tree.LowestInsertedHeader?.Number, "loaded tree - lowest header");
            Assert.AreEqual(null, tree.LowestInsertedBody?.Number, "loaded tree - lowest body");
            Assert.AreEqual(pivotNumber + 1, loadedTree.BestKnownNumber, "loaded tree");
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

            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainNetSpecProvider.Instance, NullTxPool.Instance, syncConfig, LimboLogs.Instance);
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

            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainNetSpecProvider.Instance, NullTxPool.Instance, syncConfig, LimboLogs.Instance);
            tree.SuggestBlock(Build.A.Block.Genesis.TestObject);

            List<Block> blocks = new List<Block>();
            for (long i = 5; i > 0; i--)
            {
                Block block = Build.A.Block.WithNumber(i).TestObject;
                tree.Insert(block.Header);
                blocks.Add(block);
            }

            tree.Insert(blocks);
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
            BlockTree tree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainNetSpecProvider.Instance, NullTxPool.Instance, syncConfig, LimboLogs.Instance);
            tree.SuggestBlock(genesis);

            Block previousBlock = genesis;
            for (int i = 1; i < 10; i++)
            {
                Block block = Build.A.Block.WithNumber(i).WithParent(previousBlock).TestObject;
                tree.SuggestBlock(block);
                previousBlock = block;
            }

            Block lastBlock = previousBlock;

            BlockTree loadedTree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), MainNetSpecProvider.Instance, NullTxPool.Instance, syncConfig, LimboLogs.Instance);
            loadedTree.FindHeader(lastBlock.Hash, BlockTreeLookupOptions.None);
        }

        [Test]
        public void When_block_is_moved_to_main_transactions_are_removed_from_tx_pool()
        {
            MemDb blocksDb = new MemDb();
            MemDb headersDb = new MemDb();
            MemDb blockInfosDb = new MemDb();

            Transaction t1 = Build.A.Transaction.TestObject;
            Transaction t2 = Build.A.Transaction.TestObject;

            ITxPool txPoolMock = Substitute.For<ITxPool>();
            BlockTree blockTree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), OlympicSpecProvider.Instance, txPoolMock, LimboLogs.Instance);
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1A = Build.A.Block.WithNumber(1).WithDifficulty(2).WithTransactions(t1).WithParent(block0).TestObject;
            Block block1B = Build.A.Block.WithNumber(1).WithDifficulty(3).WithTransactions(t2).WithParent(block0).TestObject;

            AddToMain(blockTree, block0);

            blockTree.SuggestBlock(block1B);
            blockTree.SuggestBlock(block1A);
            blockTree.UpdateMainChain(block1A);

            txPoolMock.Received().RemoveTransaction(t1.Hash, 1);
        }

        [Test]
        public void When_block_is_moved_out_of_main_transactions_are_removed_from_tx_pool()
        {
            MemDb blocksDb = new MemDb();
            MemDb headersDb = new MemDb();
            MemDb blockInfosDb = new MemDb();

            Transaction t1 = Build.A.Transaction.TestObject;
            Transaction t2 = Build.A.Transaction.TestObject;

            ITxPool txPoolMock = Substitute.For<ITxPool>();
            BlockTree blockTree = new BlockTree(blocksDb, headersDb, blockInfosDb, new ChainLevelInfoRepository(blockInfosDb), OlympicSpecProvider.Instance, txPoolMock, LimboLogs.Instance);
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1A = Build.A.Block.WithNumber(1).WithDifficulty(2).WithTransactions(t1).WithParent(block0).TestObject;
            Block block1B = Build.A.Block.WithNumber(1).WithDifficulty(3).WithTransactions(t2).WithParent(block0).TestObject;

            AddToMain(blockTree, block0);

            blockTree.SuggestBlock(block1B);
            blockTree.SuggestBlock(block1A);
            blockTree.UpdateMainChain(block1A);
            blockTree.UpdateMainChain(block1B);

            txPoolMock.Received().AddTransaction(t1, 1);
        }

        static object[] SourceOfBSearchTestCases =
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

        private MemDb _blocksInfosDb;
        private MemDb _headersDb;
        private MemDb _blocksDb;
    }
}