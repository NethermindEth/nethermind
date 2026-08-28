// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Db.LogIndex;
using Nethermind.Evm;
using Nethermind.Facade.Filters;
using Nethermind.Facade.Filters.Topics;
using Nethermind.Facade.Find;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.History.Test;

public class SlicedReceiptRetentionTests
{
    // Both retention paths, because the pruner reclaims by range: with the index it answers for a whole span at
    // once, and without it falls back to asking block by block. A real sliced node runs the first.
    [TestCase(false, TestName = "Retains_receipts_for_a_sliced_address_asking_block_by_block")]
    [TestCase(true, TestName = "Retains_receipts_for_a_sliced_address_asking_the_log_index_for_the_span")]
    public async Task Retains_receipts_for_a_sliced_address_and_serves_its_logs_through_LogFinder(bool logIndexEnabled)
    {
        Address slicedAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, 0);

        IHistoryConfig historyConfig = new HistoryConfig
        {
            Pruning = PruningModes.Rolling,
            RetentionEpochs = 1,
            PruningInterval = 0
        };
        IFlatDbConfig flatDbConfig = new FlatDbConfig { HistorySliceAddresses = slicedAddress.ToString() };

        // Filled in after the chain is built, since the sliced height is not known before then.
        List<int> indexedHits = [];
        ILogIndexStorage logIndexStorage = null;
        if (logIndexEnabled)
        {
            logIndexStorage = Substitute.For<ILogIndexStorage>();
            logIndexStorage.Enabled.Returns(true);
            logIndexStorage.MinBlockNumber.Returns(0);
            logIndexStorage.MaxBlockNumber.Returns(int.MaxValue - 1);
            logIndexStorage.GetEnumerator(slicedAddress, Arg.Any<int>(), Arg.Any<int>()).Returns(call =>
            {
                int from = call.ArgAt<int>(1);
                int to = call.ArgAt<int>(2);
                return indexedHits.Where(h => h >= from && h <= to).ToList().GetEnumerator();
            });
        }

        using BasicTestBlockchain testBlockchain = await BuildBlockchain(historyConfig, flatDbConfig, logIndexStorage);

        byte[] logCode = Prepare.EvmCode.PushData(32).PushData(0).Op(Instruction.LOG0).Done;

        Block slicedBlock = await testBlockchain.AddBlock(Build.A.Transaction
            .WithCode(logCode).WithNonce(0).WithGasLimit(210200)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject);
        Block otherBlock = await testBlockchain.AddBlock(Build.A.Transaction
            .WithCode(logCode).WithNonce(1).WithGasLimit(210200)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject);

        ulong slicedBlockNumber = slicedBlock.Number;
        Hash256 slicedBlockHash = slicedBlock.Hash!;
        ulong otherBlockNumber = otherBlock.Number;
        Hash256 otherBlockHash = otherBlock.Hash!;

        indexedHits.Add((int)slicedBlockNumber);

        for (int i = 0; i < 100; i++)
        {
            await testBlockchain.AddBlock();
        }
        testBlockchain.BlockTree.SyncPivot = (testBlockchain.BlockTree.Head!.Number, Hash256.Zero);

        HistoryPruner historyPruner = (HistoryPruner)testBlockchain.Container.Resolve<IHistoryPruner>();
        historyPruner.TryPruneHistory(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(testBlockchain.ReceiptStorage.HasBlock(slicedBlockNumber, slicedBlockHash), Is.True, "Sliced block's receipts should be retained");
            Assert.That(testBlockchain.ReceiptStorage.HasBlock(otherBlockNumber, otherBlockHash), Is.False, "Non-sliced block's receipts should be pruned");
            Assert.That(testBlockchain.BlockTree.FindBlock(slicedBlockNumber, BlockTreeLookupOptions.None), Is.Not.Null,
                "the retained height keeps its body, which is what makes its transactions answerable and spares the walk a signature recovery per transaction");
            Assert.That(testBlockchain.BlockTree.FindBlock(otherBlockNumber, BlockTreeLookupOptions.None), Is.Null,
                "a height nothing retains still loses its body");
        }

        LogFinder logFinder = new(
            testBlockchain.BlockTree,
            testBlockchain.ReceiptStorage,
            testBlockchain.ReceiptStorage,
            LimboLogs.Instance,
            Substitute.For<IReceiptsRecovery>());

        LogFilter filter = new(0, new BlockParameter(0UL), BlockParameter.Latest, new AddressFilter(slicedAddress), new SequenceTopicsFilter());

        FilterLog[] logs = logFinder.FindLogs(filter).ToArray();

        Assert.That(logs.Length, Is.EqualTo(1));
        Assert.That(logs[0].Address, Is.EqualTo(slicedAddress));
        Assert.That(logs[0].BlockNumber, Is.EqualTo(slicedBlockNumber));

        TxReceipt[] retained = testBlockchain.ReceiptStorage.Get(slicedBlockHash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(retained, Has.Length.EqualTo(1), "retained receipts must resolve by block hash");
            Assert.That(retained[0].TxHash, Is.Not.Null, "the tx hash comes from the body the height kept");
            Assert.That(retained[0].Sender, Is.Not.Null, "so does the sender, without the walk having had to recover it");
        }
    }

    [Test]
    public async Task Keeps_the_bodies_of_a_densely_retained_span_and_drops_the_gaps_between_them()
    {
        Address slicedAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, 0);

        IHistoryConfig historyConfig = new HistoryConfig
        {
            Pruning = PruningModes.Rolling,
            RetentionEpochs = 1,
            PruningInterval = 0
        };
        IFlatDbConfig flatDbConfig = new FlatDbConfig { HistorySliceAddresses = slicedAddress.ToString() };

        List<int> indexedHits = [];
        ILogIndexStorage logIndexStorage = Substitute.For<ILogIndexStorage>();
        logIndexStorage.Enabled.Returns(true);
        logIndexStorage.MinBlockNumber.Returns(0);
        logIndexStorage.MaxBlockNumber.Returns(int.MaxValue - 1);
        logIndexStorage.GetEnumerator(slicedAddress, Arg.Any<int>(), Arg.Any<int>()).Returns(call =>
        {
            int from = call.ArgAt<int>(1);
            int to = call.ArgAt<int>(2);
            return indexedHits.Where(h => h >= from && h <= to).ToList().GetEnumerator();
        });

        using BasicTestBlockchain testBlockchain = await BuildBlockchain(historyConfig, flatDbConfig, logIndexStorage);

        for (int i = 0; i < 100; i++)
        {
            await testBlockchain.AddBlock();
        }

        // Every other height, so the retention is dense enough that the walk goes one height at a time and the
        // gaps between kept heights are a single block - the shape a slice over a busy address produces.
        for (int height = 2; height <= 60; height += 2)
        {
            indexedHits.Add(height);
        }

        testBlockchain.BlockTree.SyncPivot = (testBlockchain.BlockTree.Head!.Number, Hash256.Zero);

        HistoryPruner historyPruner = (HistoryPruner)testBlockchain.Container.Resolve<IHistoryPruner>();
        historyPruner.TryPruneHistory(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(testBlockchain.BlockTree.FindBlock(10, BlockTreeLookupOptions.None), Is.Not.Null,
                "a retained height keeps its body");
            Assert.That(testBlockchain.BlockTree.FindBlock(12, BlockTreeLookupOptions.None), Is.Not.Null,
                "so does the next one, so the walk is not off by a height");
            Assert.That(testBlockchain.BlockTree.FindBlock(11, BlockTreeLookupOptions.None), Is.Null,
                "the single-block gap between them still loses its body");
            Assert.That(testBlockchain.BlockTree.FindBlock(13, BlockTreeLookupOptions.None), Is.Null,
                "and so does the next gap");
        }
    }

    [Test]
    public async Task Reports_unavailable_for_a_block_pruned_before_its_address_was_sliced()
    {
        Address address = ContractAddress.From(TestItem.PrivateKeyA.Address, 0);

        IHistoryConfig historyConfig = new HistoryConfig
        {
            Pruning = PruningModes.Rolling,
            RetentionEpochs = 1,
            PruningInterval = 0
        };
        IFlatDbConfig flatDbConfig = new FlatDbConfig();

        using BasicTestBlockchain testBlockchain = await BuildBlockchain(historyConfig, flatDbConfig);

        byte[] logCode = Prepare.EvmCode.PushData(32).PushData(0).Op(Instruction.LOG0).Done;
        Block block = await testBlockchain.AddBlock(Build.A.Transaction
            .WithCode(logCode).WithNonce(0).WithGasLimit(210200)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject);
        ulong blockNumber = block.Number;
        Hash256 blockHash = block.Hash!;

        for (int i = 0; i < 100; i++)
        {
            await testBlockchain.AddBlock();
        }
        testBlockchain.BlockTree.SyncPivot = (testBlockchain.BlockTree.Head!.Number, Hash256.Zero);

        HistoryPruner historyPruner = (HistoryPruner)testBlockchain.Container.Resolve<IHistoryPruner>();
        historyPruner.TryPruneHistory(CancellationToken.None);

        Assert.That(testBlockchain.ReceiptStorage.HasBlock(blockNumber, blockHash), Is.False);

        LogFinder logFinder = new(
            testBlockchain.BlockTree,
            testBlockchain.ReceiptStorage,
            testBlockchain.ReceiptStorage,
            LimboLogs.Instance,
            Substitute.For<IReceiptsRecovery>());

        LogFilter filter = new(0, new BlockParameter(blockNumber), new BlockParameter(blockNumber), new AddressFilter(address), new SequenceTopicsFilter());

        Assert.That(() => logFinder.FindLogs(filter).ToArray(), Throws.TypeOf<ResourceNotFoundException>());
    }

    [Test]
    public async Task Serves_a_sliced_address_logs_over_a_range_whose_from_endpoint_is_pruned()
    {
        Address nonSlicedAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, 0);
        Address slicedAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, 1);

        IHistoryConfig historyConfig = new HistoryConfig
        {
            Pruning = PruningModes.Rolling,
            RetentionEpochs = 1,
            PruningInterval = 0
        };
        IFlatDbConfig flatDbConfig = new FlatDbConfig { HistorySliceAddresses = slicedAddress.ToString() };

        using BasicTestBlockchain testBlockchain = await BuildBlockchain(historyConfig, flatDbConfig);

        byte[] logCode = Prepare.EvmCode.PushData(32).PushData(0).Op(Instruction.LOG0).Done;

        Block prunedBlock = await testBlockchain.AddBlock(Build.A.Transaction
            .WithCode(logCode).WithNonce(0).WithGasLimit(210200)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject);
        Block slicedBlock = await testBlockchain.AddBlock(Build.A.Transaction
            .WithCode(logCode).WithNonce(1).WithGasLimit(210200)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject);

        ulong prunedBlockNumber = prunedBlock.Number;
        Hash256 prunedBlockHash = prunedBlock.Hash!;
        ulong slicedBlockNumber = slicedBlock.Number;

        for (int i = 0; i < 100; i++)
        {
            await testBlockchain.AddBlock();
        }
        testBlockchain.BlockTree.SyncPivot = (testBlockchain.BlockTree.Head!.Number, Hash256.Zero);

        HistoryPruner historyPruner = (HistoryPruner)testBlockchain.Container.Resolve<IHistoryPruner>();
        historyPruner.TryPruneHistory(CancellationToken.None);

        Assert.That(testBlockchain.ReceiptStorage.HasBlock(prunedBlockNumber, prunedBlockHash), Is.False,
            "the from endpoint's receipts must be gone for this test to exercise the availability probe");

        IPrunedLogsRetention logsRetention = (IPrunedLogsRetention)testBlockchain.Container.Resolve<IPrunedReceiptRetention>();
        LogFinder logFinder = new(
            testBlockchain.BlockTree,
            testBlockchain.ReceiptStorage,
            testBlockchain.ReceiptStorage,
            LimboLogs.Instance,
            Substitute.For<IReceiptsRecovery>(),
            prunedLogsRetention: logsRetention);

        LogFilter slicedFilter = new(0, new BlockParameter(prunedBlockNumber), BlockParameter.Latest, new AddressFilter(slicedAddress), new SequenceTopicsFilter());
        FilterLog[] logs = logFinder.FindLogs(slicedFilter).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(logs, Has.Length.EqualTo(1),
                "every block the scan reads receipts for is a bloom match on the sliced address, and retention keeps exactly those - the pruned endpoint cannot stand in for them");
            Assert.That(logs[0].Address, Is.EqualTo(slicedAddress));
            Assert.That(logs[0].BlockNumber, Is.EqualTo(slicedBlockNumber));
        }

        LogFilter nonSlicedFilter = new(0, new BlockParameter(prunedBlockNumber), BlockParameter.Latest, new AddressFilter(nonSlicedAddress), new SequenceTopicsFilter());
        Assert.That(() => logFinder.FindLogs(nonSlicedFilter).ToArray(), Throws.TypeOf<ResourceNotFoundException>(),
            "an address nothing retains must keep failing closed over the same range");
    }

    // Slicing a busy contract retains most heights, where a range removal per gap is all cost and no reclaim. Both
    // densities have to end with the same receipts present, since only the mechanism differs.
    [TestCase(1, TestName = "Retains_the_right_receipts_when_almost_every_height_is_sliced")]
    [TestCase(40, TestName = "Retains_the_right_receipts_when_few_heights_are_sliced")]
    public async Task Retains_the_right_receipts_at_either_retention_density(int sliceEvery)
    {
        Address slicedAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, 0);

        IHistoryConfig historyConfig = new HistoryConfig
        {
            Pruning = PruningModes.Rolling,
            RetentionEpochs = 1,
            PruningInterval = 0
        };
        IFlatDbConfig flatDbConfig = new FlatDbConfig { HistorySliceAddresses = slicedAddress.ToString() };

        List<int> indexedHits = [];
        ILogIndexStorage logIndexStorage = Substitute.For<ILogIndexStorage>();
        logIndexStorage.Enabled.Returns(true);
        logIndexStorage.MinBlockNumber.Returns(0);
        logIndexStorage.MaxBlockNumber.Returns(int.MaxValue - 1);
        logIndexStorage.GetEnumerator(slicedAddress, Arg.Any<int>(), Arg.Any<int>()).Returns(call =>
        {
            int rangeFrom = call.ArgAt<int>(1);
            int rangeTo = call.ArgAt<int>(2);
            return indexedHits.Where(h => h >= rangeFrom && h <= rangeTo).ToList().GetEnumerator();
        });

        using BasicTestBlockchain testBlockchain = await BuildBlockchain(historyConfig, flatDbConfig, logIndexStorage);

        byte[] logCode = Prepare.EvmCode.PushData(32).PushData(0).Op(Instruction.LOG0).Done;

        Block slicedBlock = await testBlockchain.AddBlock(Build.A.Transaction
            .WithCode(logCode).WithNonce(0).WithGasLimit(210200)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject);
        Block otherBlock = await testBlockchain.AddBlock(Build.A.Transaction
            .WithCode(logCode).WithNonce(1).WithGasLimit(210200)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject);

        ulong slicedBlockNumber = slicedBlock.Number;
        Hash256 slicedBlockHash = slicedBlock.Hash!;
        ulong otherBlockNumber = otherBlock.Number;
        Hash256 otherBlockHash = otherBlock.Hash!;

        for (int i = 0; i < 100; i++)
        {
            await testBlockchain.AddBlock();
        }

        // The sliced block is always named; the filler heights around it set the density the pruner has to cope with.
        indexedHits.Add((int)slicedBlockNumber);
        for (ulong height = 1; height < testBlockchain.BlockTree.Head!.Number; height++)
        {
            if (height != otherBlockNumber && height % (ulong)sliceEvery == 0) indexedHits.Add((int)height);
        }

        testBlockchain.BlockTree.SyncPivot = (testBlockchain.BlockTree.Head!.Number, Hash256.Zero);

        HistoryPruner historyPruner = (HistoryPruner)testBlockchain.Container.Resolve<IHistoryPruner>();
        historyPruner.TryPruneHistory(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(testBlockchain.ReceiptStorage.HasBlock(slicedBlockNumber, slicedBlockHash), Is.True,
                "the sliced block's receipts are retained whichever mechanism the density selects");
            Assert.That(testBlockchain.ReceiptStorage.HasBlock(otherBlockNumber, otherBlockHash), Is.False,
                "and a height the index never named is still reclaimed");
        }
    }

    // The log index backfills from the tip downwards while the pruner climbs from genesis, so for hours it can answer
    // for none of the span being pruned. Each header's bloom has to decide there, and the heights it clears still have
    // to go back by range - a body read per height is what made this unusable on a real node.
    [Test]
    public async Task Retains_the_right_receipts_when_the_log_index_covers_none_of_the_span()
    {
        Address slicedAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, 0);

        IHistoryConfig historyConfig = new HistoryConfig
        {
            Pruning = PruningModes.Rolling,
            RetentionEpochs = 1,
            PruningInterval = 0
        };
        IFlatDbConfig flatDbConfig = new FlatDbConfig { HistorySliceAddresses = slicedAddress.ToString() };

        ILogIndexStorage logIndexStorage = Substitute.For<ILogIndexStorage>();
        logIndexStorage.Enabled.Returns(true);
        logIndexStorage.MinBlockNumber.Returns(1_000_000);
        logIndexStorage.MaxBlockNumber.Returns(2_000_000);

        using BasicTestBlockchain testBlockchain = await BuildBlockchain(historyConfig, flatDbConfig, logIndexStorage);

        byte[] logCode = Prepare.EvmCode.PushData(32).PushData(0).Op(Instruction.LOG0).Done;

        Block slicedBlock = await testBlockchain.AddBlock(Build.A.Transaction
            .WithCode(logCode).WithNonce(0).WithGasLimit(210200)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject);
        Block otherBlock = await testBlockchain.AddBlock(Build.A.Transaction
            .WithCode(logCode).WithNonce(1).WithGasLimit(210200)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject);

        ulong slicedNumber = slicedBlock.Number;
        Hash256 slicedHash = slicedBlock.Hash!;
        ulong otherNumber = otherBlock.Number;
        Hash256 otherHash = otherBlock.Hash!;

        for (int i = 0; i < 100; i++)
        {
            await testBlockchain.AddBlock();
        }

        testBlockchain.BlockTree.SyncPivot = (testBlockchain.BlockTree.Head!.Number, Hash256.Zero);

        HistoryPruner historyPruner = (HistoryPruner)testBlockchain.Container.Resolve<IHistoryPruner>();
        historyPruner.TryPruneHistory(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(testBlockchain.ReceiptStorage.HasBlock(slicedNumber, slicedHash), Is.True,
                "with the index unable to answer, the header's bloom is the only thing that can keep this height");
            Assert.That(testBlockchain.ReceiptStorage.HasBlock(otherNumber, otherHash), Is.False,
                "and a height whose bloom clears it goes back with the range around it");
            Assert.That(logIndexStorage.ReceivedCalls().Any(call => call.GetMethodInfo().Name == nameof(ILogIndexStorage.GetEnumerator)), Is.False,
                "the index must not be asked per height about a span it already reported it cannot cover");
        }
    }

    [Test]
    public void RetainsLogsFor_Cases()
    {
        Address unbounded = TestItem.AddressA;
        Address bounded = TestItem.AddressB;
        IFlatDbConfig flatDbConfig = new FlatDbConfig { HistorySliceAddresses = $"{unbounded},{bounded}:1000" };
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(5000).TestObject);
        SlicedReceiptRetention retention = new(flatDbConfig, Substitute.For<ILogIndexStorage>(), blockTree);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retention.RetainsLogsFor([unbounded], 0, 5000), Is.True,
                "an unbounded slice answers any range");
            Assert.That(retention.RetainsLogsFor([bounded], 4200, 5000), Is.True,
                "a bounded slice answers a range inside its window");
            Assert.That(retention.RetainsLogsFor([bounded], 3000, 5000), Is.False,
                "a bounded slice refuses a range reaching below its window");
            Assert.That(retention.RetainsLogsFor([unbounded, bounded], 3000, 5000), Is.False,
                "one uncovered address refuses the whole filter");
            Assert.That(retention.RetainsLogsFor([TestItem.AddressC], 4900, 5000), Is.False,
                "an address no slice names is refused");
            Assert.That(retention.RetainsLogsFor([], 4900, 5000), Is.False,
                "a filter with no addresses can match any log, which no slice set covers");
        }
    }

    [Test]
    public void RetainsLogsFor_RefusesBelowTheHeightRetentionWasConfiguredAt_AndStampsItOnce()
    {
        IFlatDbConfig flatDbConfig = new FlatDbConfig { HistorySliceAddresses = TestItem.AddressA.ToString() };
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(9000).TestObject);
        blockTree.GetLowestBlock().Returns(0UL);
        IDb metadata = new MemDb();
        metadata.Set(MetadataDbKeys.HistoryPruningDeletePointer, Rlp.Encode(5000UL).Bytes);
        IDbProvider dbProvider = Substitute.For<IDbProvider>();
        dbProvider.MetadataDb.Returns(metadata);

        SlicedReceiptRetention retention = new(flatDbConfig, Substitute.For<ILogIndexStorage>(), blockTree, dbProvider);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retention.RetainsLogsFor([TestItem.AddressA], 4000, 9000), Is.False,
                "everything below the delete pointer at configuration time was reclaimed - or never stored - without this retention in force");
            Assert.That(retention.RetainsLogsFor([TestItem.AddressA], 5000, 9000), Is.True,
                "from the stamp up, every reclaim has been retention-aware");
        }

        metadata.Set(MetadataDbKeys.HistoryPruningDeletePointer, Rlp.Encode(8000UL).Bytes);
        SlicedReceiptRetention restarted = new(flatDbConfig, Substitute.For<ILogIndexStorage>(), blockTree, dbProvider);
        Assert.That(restarted.RetainsLogsFor([TestItem.AddressA], 5000, 9000), Is.True,
            "the stamp is stored: a later restart under the same slice set must not lose the depth already earned");
    }

    [Test]
    public void RetainsLogsFor_ReStampsWhenTheSliceSetChanges()
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(9000).TestObject);
        blockTree.GetLowestBlock().Returns(0UL);
        IDb metadata = new MemDb();
        IDbProvider dbProvider = Substitute.For<IDbProvider>();
        dbProvider.MetadataDb.Returns(metadata);

        SlicedReceiptRetention first = new(new FlatDbConfig { HistorySliceAddresses = TestItem.AddressA.ToString() },
            Substitute.For<ILogIndexStorage>(), blockTree, dbProvider);
        Assert.That(first.RetainsLogsFor([TestItem.AddressA], 0, 9000), Is.True);

        metadata.Set(MetadataDbKeys.HistoryPruningDeletePointer, Rlp.Encode(6000UL).Bytes);
        SlicedReceiptRetention widened = new(new FlatDbConfig { HistorySliceAddresses = $"{TestItem.AddressA},{TestItem.AddressB}" },
            Substitute.For<ILogIndexStorage>(), blockTree, dbProvider);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(widened.RetainsLogsFor([TestItem.AddressB], 4000, 9000), Is.False,
                "the new set's coverage starts where the availability floor stood when it appeared");
            Assert.That(widened.RetainsLogsFor([TestItem.AddressA], 6000, 9000), Is.True);
        }
    }

    [Test]
    public void RetainsLogsFor_RefusesWithoutAHead()
    {
        IFlatDbConfig flatDbConfig = new FlatDbConfig { HistorySliceAddresses = TestItem.AddressA.ToString() };
        SlicedReceiptRetention retention = new(flatDbConfig, Substitute.For<ILogIndexStorage>(), Substitute.For<IBlockTree>());

        Assert.That(retention.RetainsLogsFor([TestItem.AddressA], 0, 100), Is.False,
            "a bounded window cannot be evaluated before the head exists, and failing open is the one wrong direction");
    }

    [Test]
    public void RetainsLogsFor_returns_false_when_no_addresses_are_configured()
    {
        SlicedReceiptRetention retention = new(new FlatDbConfig(), Substitute.For<ILogIndexStorage>(), Substitute.For<IBlockTree>());

        Assert.That(retention.RetainsLogsFor([TestItem.AddressA], 0, 100), Is.False);
    }

    [Test]
    public void ShouldRetainReceipts_returns_false_when_no_addresses_are_configured()
    {
        SlicedReceiptRetention retention = new(new FlatDbConfig(), Substitute.For<ILogIndexStorage>(), Substitute.For<IBlockTree>());

        Assert.That(retention.ShouldRetainReceipts(BlockWithBloom(TestItem.AddressA).Header), Is.False);
    }

    [TestCase(false, false, 0, false, false, TestName = "bloom does not match")]
    [TestCase(true, false, 0, false, true, TestName = "matching bloom trusted when the log index cannot confirm")]
    [TestCase(true, true, 0, true, true, TestName = "matching bloom confirmed through the log index")]
    [TestCase(true, true, 0, false, false, TestName = "bloom false positive refuted by the log index")]
    [TestCase(true, true, 100, false, true, TestName = "bloom trusted when the log index does not cover the block")]
    public void ShouldRetainReceipts_Cases(bool bloomMatches, bool indexEnabled, int indexMinBlock, bool indexHits, bool expected)
    {
        IFlatDbConfig flatDbConfig = new FlatDbConfig { HistorySliceAddresses = TestItem.AddressA.ToString() };
        ILogIndexStorage logIndexStorage = Substitute.For<ILogIndexStorage>();
        logIndexStorage.Enabled.Returns(indexEnabled);
        logIndexStorage.MinBlockNumber.Returns(indexMinBlock);
        logIndexStorage.MaxBlockNumber.Returns(1000);
        logIndexStorage.GetEnumerator(TestItem.AddressA, 5, 5).Returns(_ =>
            (indexHits ? new[] { 5 }.AsEnumerable() : Enumerable.Empty<int>()).GetEnumerator());
        SlicedReceiptRetention retention = new(flatDbConfig, logIndexStorage, Substitute.For<IBlockTree>());

        Block block = BlockWithBloom(bloomMatches ? TestItem.AddressA : TestItem.AddressB, 5);
        Assert.That(retention.ShouldRetainReceipts(block.Header), Is.EqualTo(expected));
    }

    [Test]
    public void ShouldRetainReceipts_AboveIntMaxValue_LetsTheBloomDecideAlone()
    {
        IFlatDbConfig flatDbConfig = new FlatDbConfig { HistorySliceAddresses = TestItem.AddressA.ToString() };
        ILogIndexStorage logIndexStorage = Substitute.For<ILogIndexStorage>();
        logIndexStorage.Enabled.Returns(true);
        logIndexStorage.MinBlockNumber.Returns(0);
        logIndexStorage.MaxBlockNumber.Returns(int.MaxValue);
        SlicedReceiptRetention retention = new(flatDbConfig, logIndexStorage, Substitute.For<IBlockTree>());

        Block block = BlockWithBloom(TestItem.AddressA, (ulong)int.MaxValue + 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retention.ShouldRetainReceipts(block.Header), Is.True,
                "the index is int-keyed so it cannot be asked about this height; the bloom match must decide rather than a wrapped range reporting no hit");
            Assert.That(logIndexStorage.ReceivedCalls().Any(call => call.GetMethodInfo().Name == nameof(ILogIndexStorage.GetEnumerator)), Is.False,
                "the index must not be queried at all for a height it cannot represent");
        }
    }

    [Test]
    public void RetainedHeights_asks_the_index_once_for_the_span_and_reports_what_it_covers()
    {
        IFlatDbConfig flatDbConfig = new FlatDbConfig { HistorySliceAddresses = TestItem.AddressA.ToString() };
        ILogIndexStorage logIndexStorage = Substitute.For<ILogIndexStorage>();
        logIndexStorage.Enabled.Returns(true);
        logIndexStorage.MinBlockNumber.Returns(0);
        logIndexStorage.MaxBlockNumber.Returns(1000);
        logIndexStorage.GetEnumerator(TestItem.AddressA, 100, 199).Returns(_ => new[] { 120, 150 }.AsEnumerable().GetEnumerator());
        SlicedReceiptRetention retention = new(flatDbConfig, logIndexStorage, Substitute.For<IBlockTree>());

        IReadOnlySet<ulong> retained = retention.RetainedHeights(100, 200, out ulong answeredFrom, out ulong answeredTo);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retained, Is.EquivalentTo(new ulong[] { 120, 150 }));
            Assert.That(answeredFrom, Is.EqualTo(100UL));
            Assert.That(answeredTo, Is.EqualTo(200UL), "the whole span is inside the index, so none of it is left for the block-by-block fallback");
            Assert.That(logIndexStorage.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(ILogIndexStorage.GetEnumerator)), Is.EqualTo(1),
                "one query for the span, not one per height - the per-height cost is what the range reclaim exists to remove");
        }
    }

    [Test]
    public void RetainedHeights_narrows_to_the_part_the_index_holds()
    {
        IFlatDbConfig flatDbConfig = new FlatDbConfig { HistorySliceAddresses = TestItem.AddressA.ToString() };
        ILogIndexStorage logIndexStorage = Substitute.For<ILogIndexStorage>();
        logIndexStorage.Enabled.Returns(true);
        logIndexStorage.MinBlockNumber.Returns(150);
        logIndexStorage.MaxBlockNumber.Returns(179);
        logIndexStorage.GetEnumerator(TestItem.AddressA, 150, 179).Returns(_ => Enumerable.Empty<int>().GetEnumerator());
        SlicedReceiptRetention retention = new(flatDbConfig, logIndexStorage, Substitute.For<IBlockTree>());

        retention.RetainedHeights(100, 200, out ulong answeredFrom, out ulong answeredTo);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(answeredFrom, Is.EqualTo(150UL));
            Assert.That(answeredTo, Is.EqualTo(180UL), "outside what the index holds a bloom match is the deciding test, and that needs the header");
        }
    }

    [TestCase(false, TestName = "index disabled")]
    [TestCase(true, TestName = "index enabled but holding nothing")]
    public void RetainedHeights_answers_for_nothing_when_the_index_cannot_help(bool enabled)
    {
        IFlatDbConfig flatDbConfig = new FlatDbConfig { HistorySliceAddresses = TestItem.AddressA.ToString() };
        ILogIndexStorage logIndexStorage = Substitute.For<ILogIndexStorage>();
        logIndexStorage.Enabled.Returns(enabled);
        logIndexStorage.MinBlockNumber.Returns((int?)null);
        logIndexStorage.MaxBlockNumber.Returns((int?)null);
        SlicedReceiptRetention retention = new(flatDbConfig, logIndexStorage, Substitute.For<IBlockTree>());

        retention.RetainedHeights(100, 200, out ulong answeredFrom, out ulong answeredTo);

        Assert.That(answeredFrom, Is.EqualTo(answeredTo),
            "an empty answered span is what sends the caller back to asking block by block, rather than reclaiming a slice it cannot see");
    }

    [Test]
    public void ShouldRetainReceipts_ForABoundedSlice_StopsRetainingBelowItsOwnWindow()
    {
        IFlatDbConfig flatDbConfig = new FlatDbConfig { HistorySliceAddresses = $"{TestItem.AddressA}:100" };
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(1000).TestObject);
        SlicedReceiptRetention retention = new(flatDbConfig, Substitute.For<ILogIndexStorage>(), blockTree);

        Bloom bloom = new();
        bloom.Add([Build.A.LogEntry.WithAddress(TestItem.AddressA).TestObject]);
        BlockHeader inside = Build.A.BlockHeader.WithNumber(950).WithBloom(bloom).TestObject;
        BlockHeader below = Build.A.BlockHeader.WithNumber(850).WithBloom(bloom).TestObject;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retention.ShouldRetainReceipts(inside), Is.True);
            Assert.That(retention.ShouldRetainReceipts(below), Is.False,
                "a bounded slice must not retain bodies and receipts below its own window");
        }
    }

    [Test]
    public void RetainedHeights_ForABoundedSlice_AnswersHeightsBelowItsWindowAsNotRetained()
    {
        IFlatDbConfig flatDbConfig = new FlatDbConfig { HistorySliceAddresses = $"{TestItem.AddressA}:100" };
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(1000).TestObject);
        ILogIndexStorage logIndexStorage = Substitute.For<ILogIndexStorage>();
        logIndexStorage.Enabled.Returns(true);
        logIndexStorage.MinBlockNumber.Returns(0);
        logIndexStorage.MaxBlockNumber.Returns(1000);
        logIndexStorage.GetEnumerator(TestItem.AddressA, Arg.Any<int>(), Arg.Any<int>())
            .Returns(call => ((IEnumerable<int>)[System.Math.Max((int)call.ArgAt<int>(1), 850), 950]).GetEnumerator());

        SlicedReceiptRetention retention = new(flatDbConfig, logIndexStorage, blockTree);

        IReadOnlySet<ulong> retained = retention.RetainedHeights(800, 1000, out ulong answeredFrom, out ulong answeredTo);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retained, Does.Contain(950UL));
            Assert.That(retained, Does.Not.Contain(850UL), "a hit below the slice's own window is not retained");
            Assert.That(answeredFrom, Is.EqualTo(800UL));
            Assert.That(answeredTo, Is.EqualTo(1000UL), "heights below the window are answered, answered as not retained");
        }
    }

    [TestCase("", 0ul, TestName = "ExpiredRetentionUpperBound_with_no_slices_is_zero")]
    [TestCase("unbounded", 0ul, TestName = "ExpiredRetentionUpperBound_with_only_unbounded_slices_is_zero")]
    [TestCase("bounded:100", 900ul, TestName = "ExpiredRetentionUpperBound_for_a_bounded_slice_is_head_minus_its_bound")]
    [TestCase("bounded:100,deeper:300", 700ul, TestName = "ExpiredRetentionUpperBound_across_bounds_is_the_deepest_window_floor_so_the_monotonic_cursor_strands_nothing")]
    public void ExpiredRetentionUpperBound_FollowsTheDeepestBoundedWindow(string shape, ulong expected)
    {
        string addresses = shape switch
        {
            "" => "",
            "unbounded" => TestItem.AddressA.ToString(),
            "bounded:100" => $"{TestItem.AddressA}:100",
            _ => $"{TestItem.AddressA}:100,{TestItem.AddressB}:300"
        };
        IFlatDbConfig flatDbConfig = new FlatDbConfig { HistorySliceAddresses = addresses };
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.Head.Returns(Build.A.Block.WithNumber(1000).TestObject);

        SlicedReceiptRetention retention = new(flatDbConfig, Substitute.For<ILogIndexStorage>(), blockTree);

        Assert.That(retention.ExpiredRetentionUpperBound(), Is.EqualTo(expected));
    }

    [Test]
    public void RetainedHeights_with_no_addresses_answers_for_the_whole_span()
    {
        SlicedReceiptRetention retention = new(new FlatDbConfig(), Substitute.For<ILogIndexStorage>(), Substitute.For<IBlockTree>());

        IReadOnlySet<ulong> retained = retention.RetainedHeights(100, 200, out ulong answeredFrom, out ulong answeredTo);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(retained, Is.Empty);
            Assert.That(answeredFrom, Is.EqualTo(100UL));
            Assert.That(answeredTo, Is.EqualTo(200UL), "nothing is retained at any height, so the span is answered rather than left to a fallback");
        }
    }

    private static Block BlockWithBloom(Address address, ulong number = 5)
    {
        Bloom bloom = new();
        bloom.Set(address.Bytes);
        BlockHeader header = Build.A.BlockHeader.WithNumber(number).WithBloom(bloom).TestObject;
        return Build.A.Block.WithHeader(header).TestObject;
    }

    private static async Task<BasicTestBlockchain> BuildBlockchain(
        IHistoryConfig historyConfig, IFlatDbConfig flatDbConfig, ILogIndexStorage logIndexStorage = null)
    {
        ISpecProvider specProvider = new TestSpecProvider(new ReleaseSpec { MinHistoryRetentionEpochs = 0 });
        IBlockProcessingQueue blockProcessingQueue = Substitute.For<IBlockProcessingQueue>();

        return await BasicTestBlockchain.Create(containerBuilder =>
        {
            containerBuilder
                .AddSingleton(specProvider)
                .AddSingleton(blockProcessingQueue)
                .AddSingleton(historyConfig)
                .AddSingleton(flatDbConfig)
                .AddSingleton<IPrunedReceiptRetention, SlicedReceiptRetention>();

            if (logIndexStorage is not null)
            {
                containerBuilder.AddSingleton(logIndexStorage);
            }
        });
    }
}
