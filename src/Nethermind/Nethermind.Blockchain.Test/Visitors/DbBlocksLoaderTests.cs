// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain.Blocks;
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
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Visitors
{
    [TestFixture]
    public class DbBlocksLoaderTests
    {
        private readonly int _dbLoadTimeout = 5000;

        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task Can_load_blocks_from_db()
        {
            for (int chainLength = 2; chainLength <= 32; chainLength++)
            {
                Block genesisBlock = Build.A.Block.Genesis.TestObject;

                BlockStore blockStore = new(new MemDb());
                MemDb blockInfosDb = new();
                MemDb headersDb = new();

                BlockTree testTree = Build.A.BlockTree(genesisBlock).OfChainLength(chainLength).TestObject;
                for (int i = 0; i < testTree.Head!.Number + 1; i++)
                {
                    Block ithBlock = testTree.FindBlock(i, BlockTreeLookupOptions.None)!;
                    blockStore.Insert(ithBlock);

                    headersDb.Set(ithBlock.Hash!, Rlp.Encode(ithBlock.Header).Bytes);

                    ChainLevelInfo ithLevel = new(
                        true,
                        blockInfos: new[]
                        {
                            new BlockInfo(ithBlock.Hash!, ithBlock.TotalDifficulty!.Value) {WasProcessed = true}
                        });
                    blockInfosDb.Set(i, Rlp.Encode(ithLevel).Bytes);
                }

                blockInfosDb.Set(Keccak.Zero, genesisBlock.Header.Hash!.Bytes);
                headersDb.Set(genesisBlock.Header.Hash, Rlp.Encode(genesisBlock.Header).Bytes);

                BlockTree blockTree = Build.A.BlockTree()
                    .WithoutSettingHead
                    .WithBlockStore(blockStore)
                    .WithHeadersDb(headersDb)
                    .WithBlockInfoDb(blockInfosDb)
                    .WithSpecProvider(OlympicSpecProvider.Instance)
                    .TestObject;

                DbBlocksLoader loader = new(blockTree, LimboNoErrorLogger.Instance);
                await blockTree.Accept(loader, CancellationToken.None);

                Assert.That(blockTree.BestSuggestedHeader!.Hash, Is.EqualTo(testTree.Head.Hash), $"head {chainLength}");
            }
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task Can_load_blocks_from_db_odd()
        {
            for (int chainLength = 2; chainLength <= 32; chainLength++)
            {
                Block genesisBlock = Build.A.Block.Genesis.TestObject;

                BlockStore blockStore = new(new MemDb());
                MemDb blockInfosDb = new();
                MemDb headersDb = new();

                BlockTree testTree = Build.A.BlockTree(genesisBlock).OfChainLength(chainLength).TestObject;
                for (int i = 0; i < testTree.Head!.Number + 1; i++)
                {
                    Block ithBlock = testTree.FindBlock(i, BlockTreeLookupOptions.None)!;
                    blockStore.Insert(ithBlock);

                    headersDb.Set(ithBlock.Hash!, Rlp.Encode(ithBlock.Header).Bytes);

                    ChainLevelInfo ithLevel = new(true, blockInfos: new[]
                    {
                        new BlockInfo(ithBlock.Hash!, ithBlock.TotalDifficulty!.Value)
                    });

                    blockInfosDb.Set(i, Rlp.Encode(ithLevel).Bytes);
                }

                blockInfosDb.Set(Keccak.Zero, genesisBlock.Header.Hash!.Bytes);
                headersDb.Set(genesisBlock.Header.Hash, Rlp.Encode(genesisBlock.Header).Bytes);

                BlockTree blockTree = Build.A.BlockTree()
                    .WithoutSettingHead
                    .WithBlockStore(blockStore)
                    .WithHeadersDb(headersDb)
                    .WithBlockInfoDb(blockInfosDb)
                    .WithSpecProvider(OlympicSpecProvider.Instance)
                    .TestObject;

                DbBlocksLoader loader = new(blockTree, LimboNoErrorLogger.Instance);
                await blockTree.Accept(loader, CancellationToken.None);

                Assert.That(blockTree.BestSuggestedHeader!.Hash, Is.EqualTo(testTree.Head.Hash), $"head {chainLength}");
            }
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task Can_load_from_DB_when_there_is_an_invalid_block_in_DB_and_a_valid_branch()
        {
            BlockStore blockStore = new(new MemDb());
            MemDb blockInfosDb = new();

            BlockTreeBuilder builder = Build.A.BlockTree()
                .WithoutSettingHead
                .WithBlockInfoDb(blockInfosDb)
                .WithBlockStore(blockStore);

            BlockTree tree1 = builder
                .TestObject;

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

            BlockTree tree2 = Build.A.BlockTree()
                .WithDatabaseFrom(builder)
                .WithoutSettingHead
                .TestObject;

            CancellationTokenSource tokenSource = new();
            tokenSource.CancelAfter(_dbLoadTimeout);

            tree2.NewBestSuggestedBlock += (_, args) =>
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

            DbBlocksLoader loader = new(tree2, LimboNoErrorLogger.Instance, null, 1);
            await tree2.Accept(loader, tokenSource.Token);

            Assert.That(tree2.BestKnownNumber, Is.EqualTo(3L), "best known");
            tree2.Head!.Header.Should().BeEquivalentTo(block3B.Header, options => { return options.Excluding(t => t.MaybeParent); });
            tree2.BestSuggestedHeader.Should().BeEquivalentTo(block3B.Header, options => { return options.Excluding(t => t.MaybeParent); });

            Assert.IsNull(blockStore.Get(block1.Hash!), "block 1");
            Assert.IsNull(blockStore.Get(block2.Hash!), "block 2");
            Assert.IsNull(blockStore.Get(block3.Hash!), "block 3");

            Assert.NotNull(blockInfosDb.Get(1), "level 1");
            Assert.NotNull(blockInfosDb.Get(2), "level 2");
            Assert.NotNull(blockInfosDb.Get(3), "level 3");
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task Can_load_from_DB_when_there_is_only_an_invalid_chain_in_DB()
        {
            BlockStore blockStore = new(new MemDb());
            MemDb blockInfosDb = new();

            BlockTreeBuilder builder = Build.A.BlockTree()
                .WithoutSettingHead
                .WithBlockInfoDb(blockInfosDb)
                .WithBlockStore(blockStore);
            BlockTree tree1 = builder.TestObject;

            Block block0 = Build.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
            Block block1 = Build.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
            Block block2 = Build.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
            Block block3 = Build.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;

            tree1.SuggestBlock(block0);
            tree1.SuggestBlock(block1);
            tree1.SuggestBlock(block2);
            tree1.SuggestBlock(block3);

            tree1.UpdateMainChain(block0);

            BlockTree tree2 = Build.A.BlockTree()
                .WithoutSettingHead
                .WithDatabaseFrom(builder)
                .TestObject;

            CancellationTokenSource tokenSource = new();
            tokenSource.CancelAfter(_dbLoadTimeout);

            tree2.NewBestSuggestedBlock += (_, args) =>
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

            DbBlocksLoader loader = new(tree2, LimboNoErrorLogger.Instance, null, 1);
            await tree2.Accept(loader, tokenSource.Token);

            /* note the block tree historically loads one less block than it could */

            Assert.That(tree2.BestKnownNumber, Is.EqualTo(0L), "best known");
            Assert.That(tree2.Head!.Hash, Is.EqualTo(block0.Hash), "head");
            Assert.That(tree2.BestSuggestedHeader!.Hash, Is.EqualTo(block0.Hash), "suggested");

            Assert.IsNull(blockStore.Get(block1.Hash!), "block 1");
            Assert.IsNull(blockStore.Get(block2.Hash!), "block 2");
            Assert.IsNull(blockStore.Get(block3.Hash!), "block 3");

            Assert.IsNull(blockInfosDb.Get(1), "level 1");
            Assert.IsNull(blockInfosDb.Get(2), "level 2");
            Assert.IsNull(blockInfosDb.Get(3), "level 3");
        }
    }
}
