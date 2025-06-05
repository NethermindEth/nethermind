// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.HistoryPruning;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.HistoryPruning;

public class HistoryPrunerTests
{
    private const long SecondsPerSlot = 12;
    private const long BeaconGenesisBlockNumber = 50;
    private static readonly ulong BeaconGenesisTimestamp = (ulong)new DateTimeOffset(TestBlockchain.InitialTimestamp).ToUnixTimeSeconds() + (BeaconGenesisBlockNumber * SecondsPerSlot);

    [Test]
    public async Task Can_prune_blocks_older_than_specified_epochs()
    {
        using BasicTestBlockchain testBlockchain = await BasicTestBlockchain.Create();

        List<Hash256> blockHashes = [];
        blockHashes.Add(testBlockchain.BlockTree.Head!.Hash!);
        for (int i = 0; i < 100; i++)
        {
            await testBlockchain.AddBlock();
            blockHashes.Add(testBlockchain.BlockTree.Head!.Hash!);
        }

        var head = testBlockchain.BlockTree.Head;
        Assert.That(head, Is.Not.Null);

        // n.b. technically invalid, should be at least 82125 epochs
        // however not feasible to test this
        IHistoryConfig historyConfig = new HistoryConfig
        {
            HistoryRetentionEpochs = 2,
            DropPreMerge = false
        };
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();

        HistoryPruner historyPruner = new(
            testBlockchain.BlockTree,
            testBlockchain.ReceiptStorage,
            specProvider,
            historyConfig,
            SecondsPerSlot,
            LimboLogs.Instance);

        await historyPruner.TryPruneHistory(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(testBlockchain.BlockTree.FindBlock(0, BlockTreeLookupOptions.None), Is.Not.Null, "Genesis block should still exist");
            Assert.That(testBlockchain.BlockTree.FindHeader(0, BlockTreeLookupOptions.None), Is.Not.Null, "Genesis block header should still exist");
            Assert.That(testBlockchain.BlockTree.FindCanonicalBlockInfo(0), Is.Not.Null, "Genesis block info should still exist");
            Assert.That(testBlockchain.ReceiptStorage.HasBlock(0, blockHashes[0]), Is.True, "Genesis block receipt should still exist");
        }

        for (int i = 1; i <= 100; i++)
        {
            var block = testBlockchain.BlockTree.FindBlock(i, BlockTreeLookupOptions.None);
            var header = testBlockchain.BlockTree.FindHeader(i, BlockTreeLookupOptions.None);
            var blockInfo = testBlockchain.BlockTree.FindCanonicalBlockInfo(i);
            var hasReceipt = testBlockchain.ReceiptStorage.HasBlock(i, blockHashes[i]);

            if (i < 100 - 64)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(block, Is.Null, $"Block {i} should be pruned");
                    Assert.That(header, Is.Null, $"Header {i} should be pruned");
                    Assert.That(blockInfo, Is.Null, $"Block info {i} should be pruned");
                    Assert.That(hasReceipt, Is.False, $"Receipt for block {i} should be pruned");
                }
            }
            else
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(block, Is.Not.Null, $"Block {i} should not be pruned (part of the last 64 blocks)");
                    Assert.That(header, Is.Not.Null, $"Header {i} should not be pruned (part of the last 64 blocks)");
                    Assert.That(blockInfo, Is.Not.Null, $"Block info {i} should not be pruned (part of the last 64 blocks)");
                    Assert.That(hasReceipt, Is.True, $"Receipt for block {i} should not be pruned (part of the last 64 blocks)");
                }
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(testBlockchain.BlockTree.BestKnownNumber, Is.EqualTo(100L), "BestKnownNumber should be maintained");
            Assert.That(testBlockchain.BlockTree.Head?.Number, Is.EqualTo(100L), "Head block number should be maintained");
        }
    }

    [Test]
    public async Task Can_prune_pre_merge_blocks()
    {
        using BasicTestBlockchain testBlockchain = await BasicTestBlockchain.Create();

        List<Hash256> blockHashes = [];
        blockHashes.Add(testBlockchain.BlockTree.Head!.Hash!);
        for (int i = 0; i < 100; i++)
        {
            await testBlockchain.AddBlock();
            blockHashes.Add(testBlockchain.BlockTree.Head!.Hash!);
        }

        var head = testBlockchain.BlockTree.Head;
        Assert.That(head, Is.Not.Null);

        IHistoryConfig historyConfig = new HistoryConfig
        {
            HistoryRetentionEpochs = null,
            DropPreMerge = true
        };
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();
        specProvider.BeaconChainGenesisTimestamp.Returns(BeaconGenesisTimestamp);

        HistoryPruner historyPruner = new(
            testBlockchain.BlockTree,
            testBlockchain.ReceiptStorage,
            specProvider,
            historyConfig,
            SecondsPerSlot,
            LimboLogs.Instance);

        await historyPruner.TryPruneHistory(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(historyPruner.CheckConfig());
            Assert.That(testBlockchain.BlockTree.FindBlock(0, BlockTreeLookupOptions.None), Is.Not.Null, "Genesis block should still exist");
            Assert.That(testBlockchain.BlockTree.FindHeader(0, BlockTreeLookupOptions.None), Is.Not.Null, "Genesis block header should still exist");
            Assert.That(testBlockchain.BlockTree.FindCanonicalBlockInfo(0), Is.Not.Null, "Genesis block info should still exist");
            Assert.That(testBlockchain.ReceiptStorage.HasBlock(0, blockHashes[0]), Is.True, "Genesis block receipt should still exist");
        }

        for (int i = 1; i <= 100; i++)
        {
            var block = testBlockchain.BlockTree.FindBlock(i, BlockTreeLookupOptions.None);
            var header = testBlockchain.BlockTree.FindHeader(i, BlockTreeLookupOptions.None);
            var blockInfo = testBlockchain.BlockTree.FindCanonicalBlockInfo(i);
            var hasReceipt = testBlockchain.ReceiptStorage.HasBlock(i, blockHashes[i]);
            if (i < BeaconGenesisBlockNumber)
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(block, Is.Null, $"Block {i} should be pruned");
                    Assert.That(header, Is.Null, $"Header {i} should be pruned");
                    Assert.That(blockInfo, Is.Null, $"Block info {i} should be pruned");
                    Assert.That(hasReceipt, Is.False, $"Receipt for block {i} should be pruned");
                }
            }
            else
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(block, Is.Not.Null, $"Block {i} should not be pruned (part of the last 64 blocks)");
                    Assert.That(header, Is.Not.Null, $"Header {i} should not be pruned (part of the last 64 blocks)");
                    Assert.That(blockInfo, Is.Not.Null, $"Block info {i} should not be pruned (part of the last 64 blocks)");
                    Assert.That(hasReceipt, Is.True, $"Receipt for block {i} should not be pruned (part of the last 64 blocks)");
                }
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(testBlockchain.BlockTree.BestKnownNumber, Is.EqualTo(100L), "BestKnownNumber should be maintained");
            Assert.That(testBlockchain.BlockTree.Head?.Number, Is.EqualTo(100L), "Head block number should be maintained");
        }
    }

    [Test]
    public async Task Does_not_prune_when_disabled()
    {
        using BasicTestBlockchain testBlockchain = await BasicTestBlockchain.Create();

        List<Hash256> blockHashes = [];
        blockHashes.Add(testBlockchain.BlockTree.Head!.Hash!);
        for (int i = 0; i < 10; i++)
        {
            await testBlockchain.AddBlock();
            blockHashes.Add(testBlockchain.BlockTree.Head!.Hash!);
        }

        IHistoryConfig historyConfig = new HistoryConfig
        {
            HistoryRetentionEpochs = null,
            DropPreMerge = false
        };

        ISpecProvider specProvider = Substitute.For<ISpecProvider>();

        HistoryPruner historyPruner = new(
            testBlockchain.BlockTree,
            testBlockchain.ReceiptStorage,
            specProvider,
            historyConfig,
            SecondsPerSlot,
            LimboLogs.Instance);

        await historyPruner.TryPruneHistory(CancellationToken.None);

        for (int i = 0; i <= 10; i++)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(historyPruner.CheckConfig());
                Assert.That(testBlockchain.BlockTree.FindBlock(i, BlockTreeLookupOptions.None), Is.Not.Null, $"Block {i} should still exist");
                Assert.That(testBlockchain.BlockTree.FindHeader(i, BlockTreeLookupOptions.None), Is.Not.Null, $"Header {i} should still exist");
                Assert.That(testBlockchain.BlockTree.FindCanonicalBlockInfo(i), Is.Not.Null, $"Block info {i} should still exist");
                Assert.That(testBlockchain.ReceiptStorage.HasBlock(i, blockHashes[i]), Is.True, $"Receipt for block {i} should still exist");
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(testBlockchain.BlockTree.BestKnownNumber, Is.EqualTo(10L), "BestKnownNumber should be maintained");
            Assert.That(testBlockchain.BlockTree.Head?.Number, Is.EqualTo(10L), "Head should be maintained");
        }
    }

    [Test]
    public void Can_accept_valid_config()
    {
        IHistoryConfig validHistoryConfig = new HistoryConfig
        {
            HistoryRetentionEpochs = 100000,
            DropPreMerge = false
        };

        HistoryPruner historyPruner = new(
            Substitute.For<IBlockTree>(),
            Substitute.For<IReceiptStorage>(),
            Substitute.For<ISpecProvider>(),
            validHistoryConfig,
            SecondsPerSlot,
            LimboLogs.Instance);

        Assert.That(historyPruner.CheckConfig());
    }

    [Test]
    public void Can_reject_invalid_config()
    {
        IHistoryConfig invalidHistoryConfig = new HistoryConfig
        {
            HistoryRetentionEpochs = 10,
            DropPreMerge = false
        };

        HistoryPruner historyPruner = new(
            Substitute.For<IBlockTree>(),
            Substitute.For<IReceiptStorage>(),
            Substitute.For<ISpecProvider>(),
            invalidHistoryConfig,
            SecondsPerSlot,
            LimboLogs.Instance);

        Assert.That(!historyPruner.CheckConfig());
    }
}
