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
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.TransactionPools;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class BlockTreeTests
    {
        private BlockTree BuildBlockTree()
        {
            return new BlockTree(new MemDb(), new MemDb(), MainNetSpecProvider.Instance, NullTransactionPool.Instance, LimboLogs.Instance);
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
        public void Add_and_find_branch()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block = Build.A.Block.TestObject;
            blockTree.SuggestBlock(block);
            Block found = blockTree.FindBlock(block.Hash, false);
            Assert.AreEqual(block.Hash, BlockHeader.CalculateHash(found.Header));
        }

        [Test]
        public void Add_on_branch_move_find()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block = Build.A.Block.TestObject;
            AddToMain(blockTree, block);
            Block found = blockTree.FindBlock(block.Hash, true);
            Assert.AreEqual(block.Hash, BlockHeader.CalculateHash(found.Header));
        }

        [Test]
        public void Add_on_branch_and_not_find_on_main()
        {
            BlockTree blockTree = BuildBlockTree();
            Block block = Build.A.Block.TestObject;
            blockTree.SuggestBlock(block);
            Block found = blockTree.FindBlock(block.Hash, true);
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

            Block found = blockTree.FindBlock(2);
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

            Block found = blockTree.FindBlock(1920000);
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

            Block found = blockTree.FindBlock(5);
            Assert.IsNull(found);
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

            Block[] blocks = blockTree.FindBlocks(block0.Hash, 3, 0, false);
            Assert.AreEqual(3, blocks.Length);
            Assert.AreEqual(block0.Hash, BlockHeader.CalculateHash(blocks[0].Header));
            Assert.AreEqual(block2.Hash, BlockHeader.CalculateHash(blocks[2].Header));
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

            Block[] blocks = blockTree.FindBlocks(block2.Hash, 3, 0, true);
            Assert.AreEqual(3, blocks.Length);

            Assert.AreEqual(block2.Hash, BlockHeader.CalculateHash(blocks[0].Header));
            Assert.AreEqual(block0.Hash, BlockHeader.CalculateHash(blocks[2].Header));
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

            Block[] blocks = blockTree.FindBlocks(block0.Hash, 0, 0, false);
            Assert.AreEqual(0, blocks.Length);
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

            Block[] blocks = blockTree.FindBlocks(block0.Hash, 2, 1, false);
            Assert.AreEqual(2, blocks.Length, "length");
            Assert.AreEqual(block0.Hash, BlockHeader.CalculateHash(blocks[0].Header));
            Assert.AreEqual(block2.Hash, BlockHeader.CalculateHash(blocks[1].Header));
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

            Block[] blocks = blockTree.FindBlocks(block0.Hash, 4, 0, false);
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
            Assert.AreEqual(block1.Hash, BlockHeader.CalculateHash(blockTree.BestSuggested), "best suggested");
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

            Block found = blockTree.FindBlock(block1B.Hash, false);

            Assert.AreEqual(block1B.Hash, BlockHeader.CalculateHash(found.Header));
        }

        [Test]
        public void Can_init_head_block_from_db()
        {
            Block genesisBlock = Build.A.Block.Genesis.TestObject;
            Block headBlock = genesisBlock;

            MemDb blocksDb = new MemDb();
            blocksDb.Set(Keccak.Zero, Rlp.Encode(genesisBlock).Bytes);
            blocksDb.Set(genesisBlock.Hash, Rlp.Encode(genesisBlock).Bytes);

            MemDb blockInfosDb = new MemDb();
            ChainLevelInfo level = new ChainLevelInfo(true, new BlockInfo[1] {new BlockInfo(headBlock.Hash, headBlock.Difficulty, (ulong) headBlock.Transactions.Length)});
            level.BlockInfos[0].WasProcessed = true;

            blockInfosDb.Set(0, Rlp.Encode(level).Bytes);

            BlockTree blockTree = new BlockTree(blocksDb, blockInfosDb, OlympicSpecProvider.Instance, Substitute.For<ITransactionPool>(), LimboLogs.Instance);
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

                BlockTree testTree = Build.A.BlockTree(genesisBlock).OfChainLength(chainLength).TestObject;
                for (int i = 0; i < testTree.Head.Number + 1; i++)
                {
                    Block ithBlock = testTree.FindBlock((ulong) i);
                    blocksDb.Set(ithBlock.Hash, Rlp.Encode(ithBlock).Bytes);

                    ChainLevelInfo ithLevel = new ChainLevelInfo(true, new BlockInfo[1] {new BlockInfo(ithBlock.Hash, ithBlock.TotalDifficulty.Value, (ulong) ithBlock.Transactions.Length)});
                    blockInfosDb.Set(i, Rlp.Encode(ithLevel).Bytes);
                }

                blocksDb.Set(Keccak.Zero, Rlp.Encode(genesisBlock).Bytes);

                BlockTree blockTree = new BlockTree(blocksDb, blockInfosDb, OlympicSpecProvider.Instance, Substitute.For<ITransactionPool>(), LimboLogs.Instance);
                await blockTree.LoadBlocksFromDb(CancellationToken.None);

                Assert.AreEqual(blockTree.BestSuggested.Hash, testTree.Head.Hash, $"head {chainLength}");
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

                BlockTree testTree = Build.A.BlockTree(genesisBlock).OfChainLength(chainLength).TestObject;
                for (int i = 0; i < testTree.Head.Number + 1; i++)
                {
                    Block ithBlock = testTree.FindBlock((ulong) i);
                    blocksDb.Set(ithBlock.Hash, Rlp.Encode(ithBlock).Bytes);

                    ChainLevelInfo ithLevel = new ChainLevelInfo(true, new BlockInfo[1] {new BlockInfo(ithBlock.Hash, ithBlock.TotalDifficulty.Value, (ulong) ithBlock.Transactions.Length)});
                    blockInfosDb.Set(i, Rlp.Encode(ithLevel).Bytes);
                }

                blocksDb.Set(Keccak.Zero, Rlp.Encode(testTree.FindBlock(1)).Bytes);

                BlockTree blockTree = new BlockTree(blocksDb, blockInfosDb, OlympicSpecProvider.Instance, Substitute.For<ITransactionPool>(), LimboLogs.Instance);
                await blockTree.LoadBlocksFromDb(CancellationToken.None);

                Assert.AreEqual(blockTree.BestSuggested.Hash, testTree.Head.Hash, $"head {chainLength}");
            }
        }

        [Test]
        public void Sets_head_block_hash_in_db_on_new_head_block()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();

            BlockTree blockTree = new BlockTree(blocksDb, blockInfosDb, OlympicSpecProvider.Instance, Substitute.For<ITransactionPool>(), LimboLogs.Instance);
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
            Assert.AreEqual(UInt256.One, blockTree.BestKnownNumber);
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

            BlockTree blockTree = new BlockTree(blocksDb, blockInfosDb, OlympicSpecProvider.Instance, Substitute.For<ITransactionPool>(), LimboLogs.Instance);
            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(1).WithDifficulty(3).WithParent(block0).TestObject;

            AddToMain(blockTree, block0);

            blockTree.SuggestBlock(block2);
            blockTree.SuggestBlock(block1);
            blockTree.UpdateMainChain(block1);

            BlockHeader storedInDb = Rlp.Decode<BlockHeader>(new Rlp(blockInfosDb.Get(Keccak.Zero)));
            Assert.AreEqual(block1.Hash, storedInDb.Hash);
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
            Assert.AreEqual(block1.Header, tree.BestSuggested);
        }

        private int _dbLoadTimeout = 5000;

        [Test]
        public void When_deleting_invalid_block_deletes_its_descendants()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            BlockTree tree = new BlockTree(blocksDb, blockInfosDb, MainNetSpecProvider.Instance, NullTransactionPool.Instance, LimboLogs.Instance);
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

            Assert.AreEqual(UInt256.One, tree.BestKnownNumber, "best known");
            Assert.AreEqual(UInt256.One, tree.Head.Number, "head");
            Assert.AreEqual(UInt256.One, tree.BestSuggested.Number, "suggested");

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
            BlockTree tree = new BlockTree(blocksDb, blockInfosDb, MainNetSpecProvider.Instance, NullTransactionPool.Instance, LimboLogs.Instance);
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

            Assert.AreEqual(UInt256.Zero, tree.BestKnownNumber, "best known");
            Assert.AreEqual(null, tree.Head, "head");
            Assert.AreEqual(UInt256.Zero, tree.BestSuggested.Number, "suggested");

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
            BlockTree tree = new BlockTree(blocksDb, blockInfosDb, MainNetSpecProvider.Instance, NullTransactionPool.Instance, LimboLogs.Instance);
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
            BlockTree tree = new BlockTree(blocksDb, blockInfosDb, MainNetSpecProvider.Instance, NullTransactionPool.Instance, LimboLogs.Instance);
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
        public async Task When_cleaning_descendants_of_invalid_does_not_touch_other_branches()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            BlockTree tree = new BlockTree(blocksDb, blockInfosDb, MainNetSpecProvider.Instance, NullTransactionPool.Instance, LimboLogs.Instance);
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

            Assert.AreEqual((UInt256) 3, tree.BestKnownNumber, "best known");
            Assert.AreEqual(null, tree.Head, "head");
            Assert.AreEqual(block3B.Hash, tree.BestSuggested.Hash, "suggested");

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
            BlockTree tree1 = new BlockTree(blocksDb, blockInfosDb, MainNetSpecProvider.Instance, NullTransactionPool.Instance, LimboLogs.Instance);

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

            BlockTree tree2 = new BlockTree(blocksDb, blockInfosDb, MainNetSpecProvider.Instance, NullTransactionPool.Instance, LimboLogs.Instance);

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

            Assert.AreEqual((UInt256) 3, tree2.BestKnownNumber, "best known");
            Assert.AreEqual(block2B.Hash, tree2.Head.Hash, "head");
            Assert.AreEqual(block2B.Hash, tree2.BestSuggested.Hash, "suggested");

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
            BlockTree tree1 = new BlockTree(blocksDb, blockInfosDb, MainNetSpecProvider.Instance, NullTransactionPool.Instance, LimboLogs.Instance);

            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
            Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;

            tree1.SuggestBlock(block0);
            tree1.SuggestBlock(block1);
            tree1.SuggestBlock(block2);
            tree1.SuggestBlock(block3);

            tree1.UpdateMainChain(block0);

            BlockTree tree2 = new BlockTree(blocksDb, blockInfosDb, MainNetSpecProvider.Instance, NullTransactionPool.Instance, LimboLogs.Instance);

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

            Assert.AreEqual((UInt256) 0, tree2.BestKnownNumber, "best known");
            Assert.AreEqual(block0.Hash, tree2.Head.Hash, "head");
            Assert.AreEqual(block0.Hash, tree2.BestSuggested.Hash, "suggested");

            Assert.IsNull(blocksDb.Get(block1.Hash), "block 1");
            Assert.IsNull(blocksDb.Get(block2.Hash), "block 2");
            Assert.IsNull(blocksDb.Get(block3.Hash), "block 3");

            Assert.IsNull(blockInfosDb.Get(1), "level 1");
            Assert.IsNull(blockInfosDb.Get(2), "level 2");
            Assert.IsNull(blockInfosDb.Get(3), "level 3");
        }
    }
}