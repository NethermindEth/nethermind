// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Security.Cryptography;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Era1;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework.Constraints;
using Snappier;

namespace Nethermind.Era1.Test;
public class Era1IntegrationTests
{
    [Test]
    public async Task ExportAndImportTwoBlocksAndReceipts()
    {
        using MemoryStream stream = new();
        EraBuilder builder = EraBuilder.Create(stream);
        Block block0 = Build.A.Block
            .WithNumber(0)
            .WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty)
            .WithTransactions(Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA)
                                                 .To(TestItem.GetRandomAddress()).TestObject)
            .TestObject;
        Block block1 = Build.A.Block
            .WithNumber(1)
            .WithTotalDifficulty(BlockHeaderBuilder.DefaultDifficulty)
            .WithTransactions(Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyB)
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

        EraReader reader = await EraReader.Create(stream);

        IAsyncEnumerator<(Block, TxReceipt[], UInt256)> enumerator = reader.GetAsyncEnumerator();
        await enumerator.MoveNextAsync();
        (Block importedBlock0, TxReceipt[] ImportedReceipts0, UInt256 td0) = enumerator.Current;
        importedBlock0.Header.TotalDifficulty = td0;
        await enumerator.MoveNextAsync();
        (Block importedBlock1, TxReceipt[] ImportedReceipts1, UInt256 td1) = enumerator.Current;
        importedBlock1.Header.TotalDifficulty = td1;
        await enumerator.DisposeAsync();

        importedBlock0.Should().BeEquivalentTo(block0);
        importedBlock1.Should().BeEquivalentTo(block1);

        ImportedReceipts0.Should().BeEquivalentTo(ImportedReceipts0);
        ImportedReceipts1.Should().BeEquivalentTo(ImportedReceipts1);

        Assert.That(td0, Is.EqualTo(BlockHeaderBuilder.DefaultDifficulty));
        Assert.That(td1, Is.EqualTo(BlockHeaderBuilder.DefaultDifficulty));
    }

    [Test]
    public async Task ImportFiles()
    {
        var eraFiles = EraReader.GetAllEraFiles("data", "mainnet");

        Directory.CreateDirectory("temp");
        try
        {
            foreach (var era in eraFiles)
            {
                using var eraEnumerator = await EraReader.Create(era);

                string tempEra = Path.Combine("temp", Path.GetFileName(era));
                var builder = EraBuilder.Create(tempEra);

                await foreach ((Block b, TxReceipt[] r, UInt256 td) in eraEnumerator)
                {
                    await builder.Add(b, r, td);
                }
                await builder.Finalize();
                builder.Dispose();

                using var eraEnumeratorTemp = await EraReader.Create(tempEra);
                await foreach ((Block b, TxReceipt[] r, UInt256 td) in eraEnumeratorTemp)
                {
                }
            }
        }
        finally
        {
            Directory.Delete("temp", true);
        }
    }

    [Test]
    public async Task ImportGethFiles()
    {
        var eraFiles = EraReader.GetAllEraFiles("geth", "mainnet");
        Directory.CreateDirectory("temp");
        try
        {
            var count = 0;

            foreach (var era in eraFiles)
            {
                using var eraEnumerator = await EraReader.Create(era);

                string tempEra = Path.Combine("temp", Path.GetFileName(era));
                using var builder = EraBuilder.Create(tempEra);
                await foreach ((Block b, TxReceipt[] r, UInt256 td) in eraEnumerator)
                {
                    count++;
                    await builder.Add(b, r, td);
                }
                await builder.Finalize();
            }
        }
        finally
        {
            Directory.Delete("temp", true);
        }
    }


    [Test]
    public async Task TestEraBuilderCreatesCorrectIndex()
    {
        BasicTestBlockchain testBlockchain = await BasicTestBlockchain.Create();
        using MemoryStream stream = new();
        EraBuilder builder = EraBuilder.Create(stream);

        Block genesis = testBlockchain.BlockFinder.FindBlock(0)!;
        TxReceipt[] genesisReceipts = testBlockchain.ReceiptStorage.Get(genesis);

        await builder.Add(genesis, genesisReceipts);

        testBlockchain.BlockProcessor.BlockProcessed += (sender, blockArgs) =>
        {
            builder.Add(blockArgs.Block, blockArgs.TxReceipts).Wait();
        };

        int numOfBlocks = 12;
        await testBlockchain.BuildSomeBlocks(numOfBlocks);

        await builder.Finalize();

        //Block index is layed out as 64 bit segments
        //start-number | index | index | index ... | count
        byte[] buffer = new byte[1024];
        stream.Seek(-8, SeekOrigin.End);
        stream.Read(buffer, 0, buffer.Length);
        long count = BitConverter.ToInt64(buffer, 0);
        //Plus genesis block
        Assert.That(count, Is.EqualTo(numOfBlocks + 1));
        //Seek to start of block index
        stream.Seek(-8 - 8 - count * 8, SeekOrigin.End);
        stream.Read(buffer, 0, 8);

        long startNumber = BitConverter.ToInt64(buffer, 0);

        Assert.That(startNumber, Is.EqualTo(0));

        for (int i = 0; i < count; i++)
        {
            //Seek to next block index 
            stream.Seek(-8 - count * 8 + i * 8, SeekOrigin.End);
            stream.Read(buffer, 0, 8);

            long blockOffset = BitConverter.ToInt64(buffer, 0);
            //Block offsets should be relative to index position
            stream.Seek(blockOffset, SeekOrigin.Current);

            stream.Read(buffer, 0, 2);

            ushort entryType = BitConverter.ToUInt16(buffer);

            //We expect to find a compressed header in this position 
            Assert.That(entryType, Is.EqualTo(EntryTypes.CompressedHeader));
        }
    }

    [Test]
    public async Task TestExportImportHistory()
    {
        TestBlockchain testBlockchain = await BasicTestBlockchain.Create();
        using MemoryStream stream = new();
        EraBuilder builder = EraBuilder.Create(stream);

        Block genesis = testBlockchain.BlockFinder.FindBlock(0)!;

        int numOfBlocks = 32;
        int numOfTx = 300;
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
                                                     .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
                nonce++;
            }
            blocks.Add(Build.A.Block.WithTotalDifficulty(1000000L + blocks[i].TotalDifficulty)
                                    .WithTransactions(transactions)
                                    .WithParent(blocks[i])
                                    .WithGasLimit(30_000_000).TestObject);
        }

        testBlockchain.BlockProcessor.Process(genesis.StateRoot!, blocks, ProcessingOptions.NoValidation, new BlockReceiptsTracer());

        foreach (var block in blocks)
        {
            await builder.Add(block, testBlockchain.ReceiptStorage.Get(block));
        }

        await builder.Finalize();

        EraReader iterator = await EraReader.Create(stream);

        await using var enu = iterator.GetAsyncEnumerator();
        for (int i = 0; i < numOfBlocks; i++)
        {
            Assert.That(await enu.MoveNextAsync(), Is.True, $"Expected block {i} from the iterator, but it returned false.");
            (Block b, TxReceipt[] r, UInt256 td) = enu.Current;
            b.Header.TotalDifficulty = td;

            Block expectedBlock = blocks[i] ?? throw new ArgumentException("Could not find required block?");

            //ignore these for comparison
            expectedBlock.Header.MaybeParent = null;
            expectedBlock.Transactions.All(t => { t.SenderAddress = null; return true; });

            TxReceipt[] expectedReceipts = testBlockchain.ReceiptStorage.Get(expectedBlock);

            b.Should().BeEquivalentTo(expectedBlock);

            Assert.That(r.Length, Is.EqualTo(expectedReceipts.Length), "Incorrect amount of receipts.");

            for (int y = 0; y < expectedReceipts.Length; y++)
            {
                Assert.That(r[y].TxType, Is.EqualTo(expectedReceipts[y].TxType));
                Assert.That(r[y].PostTransactionState, Is.EqualTo(expectedReceipts[y].PostTransactionState));
                Assert.That(r[y].GasUsedTotal, Is.EqualTo(expectedReceipts[y].GasUsedTotal));
                Assert.That(r[y].Bloom, Is.EqualTo(expectedReceipts[y].Bloom));
                Assert.That(r[y].Logs, Is.EquivalentTo(expectedReceipts[y].Logs));
                if (expectedReceipts[y].Error == null)
                    Assert.That(r[y].Error, new OrConstraint(Is.Null, Is.Empty));
                else
                    Assert.That(r[y].Error, Is.EqualTo(expectedReceipts[y].Error));
            }
        }

    }

}
