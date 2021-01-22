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

using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.Visitors;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.State.Repositories;
using Nethermind.Db.Blooms;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Visitors
{
    [TestFixture]
    public class DbBlocksLoaderTests
    {
        private int _dbLoadTimeout = 5000;

        [Test]
        public async Task Can_load_blocks_from_db()
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

                    headersDb.Set(ithBlock.Hash, Rlp.Encode(ithBlock.Header).Bytes);

                    ChainLevelInfo ithLevel = new ChainLevelInfo(
                        true,
                        new BlockInfo[1]
                        {
                            new BlockInfo(
                                ithBlock.Hash,
                                ithBlock.TotalDifficulty.Value) {WasProcessed = true}
                        });
                    blockInfosDb.Set(i, Rlp.Encode(ithLevel).Bytes);
                }

                blockInfosDb.Set(Keccak.Zero, genesisBlock.Header.Hash.Bytes);
                headersDb.Set(genesisBlock.Header.Hash, Rlp.Encode(genesisBlock.Header).Bytes);
                
                BlockTree blockTree = new BlockTree(
                    blocksDb,
                    headersDb,
                    blockInfosDb,
                    new ChainLevelInfoRepository(blockInfosDb),
                    OlympicSpecProvider.Instance,
                    NullBloomStorage.Instance,
                    LimboLogs.Instance);

                DbBlocksLoader loader = new DbBlocksLoader(blockTree, LimboNoErrorLogger.Instance);
                await blockTree.Accept(loader, CancellationToken.None);

                Assert.AreEqual(testTree.Head.Hash, blockTree.BestSuggestedHeader.Hash, $"head {chainLength}");
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

                    headersDb.Set(ithBlock.Hash, Rlp.Encode(ithBlock.Header).Bytes);

                    ChainLevelInfo ithLevel = new ChainLevelInfo(true, new BlockInfo[1]
                    {
                        new BlockInfo(ithBlock.Hash, ithBlock.TotalDifficulty.Value)
                    });
                    
                    blockInfosDb.Set(i, Rlp.Encode(ithLevel).Bytes);
                }

                blockInfosDb.Set(Keccak.Zero, genesisBlock.Header.Hash.Bytes);
                headersDb.Set(genesisBlock.Header.Hash, Rlp.Encode(genesisBlock.Header).Bytes);

                BlockTree blockTree = new BlockTree(
                    blocksDb,
                    headersDb,
                    blockInfosDb,
                    new ChainLevelInfoRepository(blockInfosDb),
                    OlympicSpecProvider.Instance,
                    NullBloomStorage.Instance,
                    LimboLogs.Instance);
                
                DbBlocksLoader loader = new DbBlocksLoader(blockTree, LimboNoErrorLogger.Instance);
                await blockTree.Accept(loader, CancellationToken.None);

                Assert.AreEqual(testTree.Head.Hash, blockTree.BestSuggestedHeader.Hash, $"head {chainLength}");
            }
        }

        [Test]
        public async Task Can_load_from_DB_when_there_is_an_invalid_block_in_DB_and_a_valid_branch()
        {
            MemDb blocksDb = new MemDb();
            MemDb blockInfosDb = new MemDb();
            MemDb headersDb = new MemDb();

            BlockTree tree1 = new BlockTree(
                blocksDb,
                headersDb,
                blockInfosDb,
                new ChainLevelInfoRepository(blockInfosDb),
                MainnetSpecProvider.Instance,
                NullBloomStorage.Instance,
                LimboLogs.Instance);

            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
            Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;

            Block block1B = Build.A.Block.WithNumber(1).WithDifficulty(1).WithParent(block0).TestObject;
            Block block2B = Build.A.Block.WithNumber(2).WithDifficulty(1).WithParent(block1B).TestObject;
            Block block3B = Build.A.Block.WithNumber(3).WithDifficulty(1).WithParent(block2B).TestObject;

            tree1.SuggestBlock(block0);
            tree1.SuggestBlock(block1); // invalid block
            tree1.SuggestBlock(block2); // invalid branch
            tree1.SuggestBlock(block3); // invalid branch

            tree1.SuggestBlock(block1B);
            tree1.SuggestBlock(block2B);
            tree1.SuggestBlock(block3B); // expected to be head

            tree1.UpdateMainChain(block0);
            
            BlockTree tree2 = new BlockTree(
                blocksDb,
                headersDb,
                blockInfosDb,
                new ChainLevelInfoRepository(blockInfosDb),
                MainnetSpecProvider.Instance,
                NullBloomStorage.Instance,
                LimboLogs.Instance);

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

            DbBlocksLoader loader = new DbBlocksLoader(tree2, LimboNoErrorLogger.Instance, null, 1);
            await tree2.Accept(loader, tokenSource.Token);

            Assert.AreEqual(3L, tree2.BestKnownNumber, "best known");
            tree2.Head.Header.Should().BeEquivalentTo(block3B.Header, options => { return options.Excluding(t => t.MaybeParent); });
            tree2.BestSuggestedHeader.Should().BeEquivalentTo(block3B.Header, options => { return options.Excluding(t => t.MaybeParent); });

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

            BlockTree tree1 = new BlockTree(
                blocksDb,
                headersDb,
                blockInfosDb,
                new ChainLevelInfoRepository(blockInfosDb),
                MainnetSpecProvider.Instance,
                NullBloomStorage.Instance,
                LimboLogs.Instance);

            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
            Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;

            tree1.SuggestBlock(block0);
            tree1.SuggestBlock(block1);
            tree1.SuggestBlock(block2);
            tree1.SuggestBlock(block3);

            tree1.UpdateMainChain(block0);

            BlockTree tree2 = new BlockTree(
                blocksDb,
                headersDb,
                blockInfosDb,
                new ChainLevelInfoRepository(blockInfosDb),
                MainnetSpecProvider.Instance,
                NullBloomStorage.Instance,
                LimboLogs.Instance);

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

            DbBlocksLoader loader = new DbBlocksLoader(tree2, LimboNoErrorLogger.Instance, null, 1);
            await tree2.Accept(loader, tokenSource.Token);

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
    }
}
