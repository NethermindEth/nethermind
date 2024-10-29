// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO.MemoryMappedFiles;
using Autofac;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Specs.ChainSpecStyle;
using NSubstitute;
using NUnit.Framework.Constraints;

namespace Nethermind.Era1.Test;
public class Era1ModuleTests
{
    [Test]
    public async Task ExportAndImportTwoBlocksAndReceipts()
    {
        using var tmpFile = new TmpFile();
        using EraWriter builder = new EraWriter(tmpFile.FilePath, Substitute.For<ISpecProvider>());
        Block block0 = Build.A.Block
            .WithNumber(0)
            .WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty)
            .WithTransactions(Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA)
                                                 .WithSenderAddress(null)
                                                 .To(TestItem.GetRandomAddress()).TestObject)
            .TestObject;
        Block block1 = Build.A.Block
            .WithNumber(1)
            .WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty)
            .WithTransactions(Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB)
                                                 .WithSenderAddress(null)
                                                 .To(TestItem.GetRandomAddress()).TestObject).TestObject;
        TxReceipt receipt0 = Build.A.Receipt
            .WithAllFieldsFilled
            .TestObject;
        TxReceipt receipt1 = Build.A.Receipt
            .WithAllFieldsFilled
            .TestObject;

        await builder.Add(block0, new[] { receipt0 });
        await builder.Add(block1, new[] { receipt1 });
        await builder.Finalize();

        using EraReader reader = new EraReader(tmpFile.FilePath);

        IAsyncEnumerator<(Block, TxReceipt[])> enumerator = reader.GetAsyncEnumerator();
        await enumerator.MoveNextAsync();
        (Block importedBlock0, TxReceipt[] ImportedReceipts0) = enumerator.Current;
        await enumerator.MoveNextAsync();
        (Block importedBlock1, TxReceipt[] ImportedReceipts1) = enumerator.Current;
        await enumerator.DisposeAsync();

        importedBlock0.Should().BeEquivalentTo(block0);
        importedBlock1.Should().BeEquivalentTo(block1);

        ImportedReceipts0.Should().BeEquivalentTo(ImportedReceipts0);
        ImportedReceipts1.Should().BeEquivalentTo(ImportedReceipts1);

        Assert.That(importedBlock0.TotalDifficulty, Is.EqualTo(BlockHeaderBuilder.DefaultDifficulty));
        Assert.That(importedBlock1.TotalDifficulty, Is.EqualTo(BlockHeaderBuilder.DefaultDifficulty));
    }

    [TestCase("holesky")]
    [TestCase("mainnet")]
    public async Task ImportAndExportGethFiles(string network)
    {
        var eraFiles = EraPathUtils.GetAllEraFiles($"testdata/{network}", network);

        Assert.That(eraFiles.Count(), Is.GreaterThan(0));

        var specProvider = new ChainSpecBasedSpecProvider(new ChainSpec
        {
            SealEngineType = SealEngineType.BeaconChain,
            Parameters = new ChainParameters()
        });

        foreach (var era in eraFiles)
        {
            var readFromFile = new List<(Block b, TxReceipt[] r)>();

            using var tmpFile = new TmpFile();
            using var builder = new EraWriter(tmpFile.FilePath, specProvider);

            using var eraEnumerator = new EraReader(era);
            await foreach ((Block b, TxReceipt[] r) in eraEnumerator)
            {
                await builder.Add(b, r);
                readFromFile.Add((b, r));
            }
            await builder.Finalize();

            using EraReader exportedToImported = new EraReader(tmpFile.FilePath);
            int i = 0;
            await foreach ((Block b, TxReceipt[] r) in exportedToImported)
            {
                Assert.That(i, Is.LessThan(readFromFile.Count()), "Exceeded the block count read from the file.");
                b.ToString(Block.Format.Full).Should().BeEquivalentTo(readFromFile[i].b.ToString(Block.Format.Full));
                r.Should().BeEquivalentTo(readFromFile[i].r);
                i++;
            }
        }
    }

    [Test]
    public async Task CreateEraAndVerifyAccumulators()
    {
        TestBlockchain testBlockchain = await BasicTestBlockchain.Create();
        testBlockchain.State.AddToBalance(TestItem.AddressA, 10.Ether(), testBlockchain.SpecProvider.GenesisSpec);
        testBlockchain.State.RecalculateStateRoot();

        using TmpFile tmpFile = new TmpFile();
        Block genesis = testBlockchain.BlockFinder.FindBlock(0)!;

        int numOfBlocks = 12;
        int numOfTx = 2;
        UInt256 nonce = 0;

        List<Block> blocks = [genesis];

        for (int i = 0; i < numOfBlocks; i++)
        {
            Transaction[] transactions = new Transaction[numOfTx];
            for (int y = 0; y < numOfTx; y++)
            {
                transactions[y] = Build.A.Transaction.WithTo(TestItem.GetRandomAddress())
                                                     .WithNonce(nonce)
                                                     .WithValue(TestContext.CurrentContext.Random.NextUInt(10))
                                                     .SignedAndResolved(TestItem.PrivateKeyA)
                                                     .TestObject;
                nonce++;
            }
            blocks.Add(Build.A.Block.WithUncles(Build.A.Block.TestObject)
                                    .WithBaseFeePerGas(1)
                                    .WithTotalDifficulty(blocks[i].TotalDifficulty + blocks[i].Difficulty)
                                    .WithTransactions(transactions)
                                    .WithParent(blocks[i]).TestObject);
        }

        blocks = testBlockchain.BlockProcessor.Process(genesis.StateRoot!, blocks, ProcessingOptions.NoValidation | ProcessingOptions.StoreReceipts, new BlockReceiptsTracer()).ToList();
        using EraWriter builder = new EraWriter(tmpFile.FilePath, testBlockchain.SpecProvider);

        foreach (var block in blocks)
        {
            await builder.Add(block, testBlockchain.ReceiptStorage.Get(block));
        }

        await builder.Finalize();

        using EraReader eraReader = new EraReader(tmpFile.FilePath);

        Func<Task> verifyTask = () => eraReader.VerifyContent(testBlockchain.SpecProvider, Always.Valid, default);
        await verifyTask.Should().NotThrowAsync();
    }

    [Test]
    public async Task TestEraBuilderCreatesCorrectIndex()
    {
        BasicTestBlockchain testBlockchain = await BasicTestBlockchain.Create();
        using var tmpFile = new TmpFile();
        List<(Block, TxReceipt[])> toAddBlocks = new List<(Block, TxReceipt[])>();
        testBlockchain.BlockProcessor.BlockProcessed += (sender, blockArgs) =>
        {
            toAddBlocks.Add((blockArgs.Block, blockArgs.TxReceipts));
        };

        int numOfBlocks = 12;
        await testBlockchain.BuildSomeBlocks(numOfBlocks);

        using EraWriter builder = new EraWriter(tmpFile.FilePath, Substitute.For<ISpecProvider>());
        foreach ((Block, TxReceipt[]) blockAndReceipt in toAddBlocks)
        {
            await builder.Add(blockAndReceipt.Item1, blockAndReceipt.Item2);
        }
        await builder.Finalize();

        using MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(tmpFile.FilePath, FileMode.Open);
        using E2StoreReader fileReader = new E2StoreReader(mmf);
        Assert.That(fileReader.BlockCount, Is.EqualTo(numOfBlocks));

        for (int i = 0; i < fileReader.BlockCount; i++)
        {
            long blockOffset = fileReader.BlockOffset(fileReader.StartBlock + i);

            using (var accessor = mmf.CreateViewAccessor(blockOffset, 2))
            {
                ushort entryType = accessor.ReadUInt16(0);

                //We expect to find a compressed header in this position
                Assert.That(entryType, Is.EqualTo(EntryTypes.CompressedHeader));
            }
        }
    }

    [Test]
    public async Task TestBigBlocksExportImportHistory()
    {
        TestBlockchain testBlockchain = await BasicTestBlockchain.Create();
        testBlockchain.State.AddToBalance(TestItem.AddressA, 10.Ether(), testBlockchain.SpecProvider.GenesisSpec);
        testBlockchain.State.RecalculateStateRoot();

        using var tmpFile = new TmpFile();
        using EraWriter builder = new EraWriter(tmpFile.FilePath, Substitute.For<ISpecProvider>());

        Block genesis = testBlockchain.BlockFinder.FindBlock(0)!;

        int numOfBlocks = 16;
        int numOfTx = 1000;
        UInt256 nonce = 0;
        var blocks = new List<Block>
        {
            genesis
        };
        for (int i = 0; i < numOfBlocks; i++)
        {
            Transaction[] transactions = new Transaction[numOfTx];
            for (int y = 0; y < numOfTx; y++)
            {
                transactions[y] = Build.A.Transaction.WithTo(TestItem.GetRandomAddress())
                                                     .WithNonce(nonce)
                                                     .WithValue(1)
                                                     .SignedAndResolved(TestItem.PrivateKeyA)
                                                     .TestObject;
                nonce++;
            }
            blocks.Add(Build.A.Block.WithUncles(Build.A.Block.TestObject)
                                    .WithBaseFeePerGas(1)
                                    .WithWithdrawals(100)
                                    .WithTotalDifficulty(1000000L + blocks[i].Difficulty)
                                    .WithTransactions(transactions)
                                    .WithParent(blocks[i])
                                    .WithGasLimit(30_000_000).TestObject);
        }

        testBlockchain.BlockProcessor.Process(genesis.StateRoot!, blocks, ProcessingOptions.NoValidation, new BlockReceiptsTracer());

        foreach (var block in blocks)
        {
            foreach (var item in block.Transactions)
                item.SenderAddress = null;
            await builder.Add(block, testBlockchain.ReceiptStorage.Get(block));
        }

        await builder.Finalize();

        using EraReader iterator = new EraReader(tmpFile.FilePath);

        await using var enu = iterator.GetAsyncEnumerator();
        for (int i = 0; i < numOfBlocks; i++)
        {
            Assert.That(await enu.MoveNextAsync(), Is.True, $"Expected block {i} from the iterator, but it returned false.");
            (Block b, TxReceipt[] r) = enu.Current;

            Block expectedBlock = blocks[i] ?? throw new ArgumentException("Could not find required block?");

            //ignore this for comparison
            expectedBlock.Header.MaybeParent = null;

            TxReceipt[] expectedReceipts = testBlockchain.ReceiptStorage.Get(expectedBlock);

            b.Should().BeEquivalentTo(expectedBlock);

            Assert.That(r.Length, Is.EqualTo(expectedReceipts.Length), "Incorrect amount of receipts.");

            for (int y = 0; y < expectedReceipts.Length; y++)
            {
                Assert.That(r[y].TxType, Is.EqualTo(expectedReceipts[y].TxType));
                Assert.That(r[y].PostTransactionState, Is.EqualTo(expectedReceipts[y].PostTransactionState));
                Assert.That(r[y].GasUsedTotal, Is.EqualTo(expectedReceipts[y].GasUsedTotal));
                Assert.That(r[y].Bloom, Is.EqualTo(expectedReceipts[y].Bloom));
                Assert.That(r[y].Logs, Is.EquivalentTo(expectedReceipts[y].Logs!));
                if (expectedReceipts[y].Error == null)
                    Assert.That(r[y].Error, new OrConstraint(Is.Null, Is.Empty));
                else
                    Assert.That(r[y].Error, Is.EqualTo(expectedReceipts[y].Error));
            }
        }
    }

    [TestCase(true, 0, 0, 1000, 1001, 9999)]
    [TestCase(true, 0, 2000, 1000, 1001, 2000)]
    [TestCase(true, 0, 0, 0, null, 0)]
    [TestCase(false, 0, 0, 0, 1, 9999)]
    [TestCase(false, 0, 0, 2000, 2001, 9999)]
    public async Task EraExportAndImport(bool fastSync, long start, long end, long headBlockNumber, long? expectedMinSuggestedBlock, long expectedMaxSuggestedBlock)
    {
        const int ChainLength = 10000;
        await using IContainer outCtx = await EraTestModule.CreateExportedEraEnv(ChainLength);
        TmpDirectory tmpDir = outCtx.Resolve<TmpDirectory>();
        IBlockTree outTree = outCtx.Resolve<IBlockTree>();

        BlockTree inTree = Build.A.BlockTree()
            .WithBlocks(outTree.FindBlock(0, BlockTreeLookupOptions.None)!)
            .TestObject;

        Block headBlock = outTree.FindBlock(headBlockNumber)!;
        if (headBlockNumber != 0)
        {
            inTree.Insert(headBlock, BlockTreeInsertBlockOptions.SaveHeader);
            inTree.UpdateMainChain(new[] { headBlock }, true);
        }

        await using IContainer inCtx = EraTestModule.BuildContainerBuilder()
            .AddSingleton<IBlockTree>(inTree)
            .AddSingleton<ISyncConfig>(new SyncConfig()
            {
                FastSync = fastSync
            })
            .AddSingleton<IEraConfig>(new EraConfig()
            {
                Start = start,
                End = end,
                ImportDirectory = tmpDir.DirectoryPath,
                TrustedAccumulatorFile = Path.Join(tmpDir.DirectoryPath, EraExporter.AccumulatorFileName),
                MaxEra1Size = 16,
                NetworkName = EraTestModule.TestNetwork
            })
            .Build();

        long? minSuggestedNumber = null;
        long maxSuggestedBlock = 0;
        inTree.NewBestSuggestedBlock += (sender, args) =>
        {
            minSuggestedNumber ??= args.Block.Number;
            maxSuggestedBlock = args.Block.Number;
            inTree.UpdateMainChain([args.Block], true);
        };

        EraCliRunner cliRunner = inCtx.Resolve<EraCliRunner>();
        await cliRunner.Run(default);

        Assert.That(minSuggestedNumber, Is.EqualTo(expectedMinSuggestedBlock));
        Assert.That(maxSuggestedBlock, Is.EqualTo(expectedMaxSuggestedBlock));
    }
}
