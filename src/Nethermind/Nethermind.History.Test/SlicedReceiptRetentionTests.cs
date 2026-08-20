// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Db.LogIndex;
using Nethermind.Evm;
using Nethermind.Facade.Filters;
using Nethermind.Facade.Filters.Topics;
using Nethermind.Facade.Find;
using Nethermind.Logging;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.History.Test;

public class SlicedReceiptRetentionTests
{
    [Test]
    public async Task Retains_receipts_for_a_sliced_address_and_serves_its_logs_through_LogFinder()
    {
        Address slicedAddress = ContractAddress.From(TestItem.PrivateKeyA.Address, 0);

        IHistoryConfig historyConfig = new HistoryConfig
        {
            Pruning = PruningModes.Rolling,
            RetentionEpochs = 1,
            PruningInterval = 0
        };
        IFlatDbConfig flatDbConfig = new FlatDbConfig { HistorySliceAddresses = slicedAddress.ToString() };

        using BasicTestBlockchain testBlockchain = await BuildBlockchain(historyConfig, flatDbConfig);

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
        testBlockchain.BlockTree.SyncPivot = (testBlockchain.BlockTree.Head!.Number, Hash256.Zero);

        HistoryPruner historyPruner = (HistoryPruner)testBlockchain.Container.Resolve<IHistoryPruner>();
        historyPruner.TryPruneHistory(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(testBlockchain.ReceiptStorage.HasBlock(slicedBlockNumber, slicedBlockHash), Is.True, "Sliced block's receipts should be retained");
            Assert.That(testBlockchain.ReceiptStorage.HasBlock(otherBlockNumber, otherBlockHash), Is.False, "Non-sliced block's receipts should be pruned");
            Assert.That(testBlockchain.BlockTree.FindBlock(slicedBlockNumber, BlockTreeLookupOptions.None), Is.Null, "Sliced block's body should still be pruned - only receipts are retained");
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
    public void ShouldRetainReceipts_returns_false_when_no_addresses_are_configured()
    {
        SlicedReceiptRetention retention = new(new FlatDbConfig(), Substitute.For<ILogIndexStorage>());

        Assert.That(retention.ShouldRetainReceipts(BlockWithBloom(TestItem.AddressA)), Is.False);
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
        SlicedReceiptRetention retention = new(flatDbConfig, logIndexStorage);

        Block block = BlockWithBloom(bloomMatches ? TestItem.AddressA : TestItem.AddressB, 5);
        Assert.That(retention.ShouldRetainReceipts(block), Is.EqualTo(expected));
    }

    private static Block BlockWithBloom(Address address, ulong number = 5)
    {
        Bloom bloom = new();
        bloom.Set(address.Bytes);
        BlockHeader header = Build.A.BlockHeader.WithNumber(number).WithBloom(bloom).TestObject;
        return Build.A.Block.WithHeader(header).TestObject;
    }

    private static async Task<BasicTestBlockchain> BuildBlockchain(IHistoryConfig historyConfig, IFlatDbConfig flatDbConfig)
    {
        ISpecProvider specProvider = new TestSpecProvider(new ReleaseSpec { MinHistoryRetentionEpochs = 0 });
        IBlockProcessingQueue blockProcessingQueue = Substitute.For<IBlockProcessingQueue>();

        return await BasicTestBlockchain.Create(containerBuilder =>
        {
            containerBuilder
                .AddSingleton(specProvider)
                .AddSingleton(blockProcessingQueue)
                .AddSingleton(historyConfig)
                .AddSingleton(flatDbConfig);
        });
    }
}
