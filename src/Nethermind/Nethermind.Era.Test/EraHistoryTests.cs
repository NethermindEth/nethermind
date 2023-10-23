// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Era1;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using NUnit.Framework.Constraints;
using Snappier;

namespace Nethermind.Era1.Test;
public class Era1Tests
{
    [Test]
    public async Task Test1()
    {
        var ms = new MemoryStream();    
        var sut = new E2Store(ms);

        await sut.WriteEntry(EntryTypes.Version, Array.Empty<byte>());
    }

    [Test]
    public async Task TestHistoryImport()
    {
        var eraFiles = E2Store.GetAllEraFiles("data", "mainnet");

        Directory.CreateDirectory("temp");

        foreach (var era in eraFiles)
        {
            using var eraEnumerator = await EraIterator.Create(era);

            string tempEra = Path.Combine("temp", Path.GetFileName(era));
            var builder = await EraBuilder.Create(tempEra);

            await foreach ((Block b, TxReceipt[] r, UInt256 td) in eraEnumerator)
            {
                Debug.WriteLine($"Reencoding block");

                Rlp encodedHeader = new HeaderDecoder().Encode(b.Header);
                Rlp encodedBody = new BlockBodyDecoder().Encode(b.Body);

                await builder.Add(b, r, td);
            }
            await builder.Finalize();
            builder.Dispose();

            using var eraEnumeratorTemp = await EraIterator.Create(tempEra);
            await foreach ((Block b, TxReceipt[] r, UInt256 td) in eraEnumeratorTemp)
            {
                Rlp encodedHeader = new HeaderDecoder().Encode(b.Header);
                Rlp encodedBody = new BlockBodyDecoder().Encode(b.Body);
            }
        }

        Directory.Delete("temp", true);
    }

    [Test]
    public async Task TestEraBuilderCreatesCorrectIndex()
    {
        BasicTestBlockchain testBlockchain = await BasicTestBlockchain.Create();
        using MemoryStream stream = new();
        EraBuilder builder = await EraBuilder.Create(stream);

        Block genesis = testBlockchain.BlockFinder.FindBlock(0)!;
        TxReceipt[] genesisReceipts = testBlockchain.ReceiptStorage.Get(genesis);

        await builder.Add(genesis, genesisReceipts, genesis.TotalDifficulty ?? 0);

        testBlockchain.BlockProcessor.BlockProcessed += (sender, blockArgs) => {
            builder.Add(blockArgs.Block, blockArgs.TxReceipts, blockArgs.Block.TotalDifficulty ?? 0).Wait();
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
        stream.Seek(-8 -8 - count * 8, SeekOrigin.End);
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

            stream.Read(buffer, 0, buffer.Length);

            ushort entryType = BitConverter.ToUInt16(buffer);

            //We expect to find a compressed header in this position 
            Assert.That(entryType, Is.EqualTo(EntryTypes.CompressedHeader));
        }
    }

    [Test]  
    public async Task TestExportImportHistory()
    {
        BasicTestBlockchain testBlockchain = await BasicTestBlockchain.Create();
        using MemoryStream stream = new();
        EraBuilder builder = await EraBuilder.Create(stream);

        Block genesis = testBlockchain.BlockFinder.FindBlock(0)!;
        TxReceipt[] genesisReceipts = testBlockchain.ReceiptStorage.Get(genesis);

        await builder.Add(genesis, genesisReceipts, genesis.TotalDifficulty ?? 0);

        testBlockchain.BlockProcessor.BlockProcessed += (sender, blockArgs) => {
            builder.Add(blockArgs.Block, blockArgs.TxReceipts, blockArgs.Block.TotalDifficulty??0).Wait();
        };

        int numOfBlocks = 32 * 3 * 10;
        await testBlockchain.BuildSomeBlocks(numOfBlocks);

        await builder.Finalize();

        EraIterator iterator = await EraIterator.Create(stream);

        await using var enu = iterator.GetAsyncEnumerator();
        for (int i = 0; i < numOfBlocks;i++)
        {
            Assert.That(await enu.MoveNextAsync(), Is.True, $"Expected block {i} from the iterator, but it returned false.");
            (Block b, TxReceipt[] r, UInt256 td) = enu.Current;
            b.Header.TotalDifficulty = td;
                
            Block wantedBlock = testBlockchain.BlockFinder.FindBlock(i) ?? throw new ArgumentException("Could not find required block?");
            TxReceipt[] wantedReceipts = testBlockchain.ReceiptStorage.Get(wantedBlock);

            Assert.That(b.ToString(Block.Format.Full), Is.EqualTo(wantedBlock.ToString(Block.Format.Full)));

            for (int y = 0; y < wantedBlock.Transactions.Length; y++)
            {
                Assert.That(b.Transactions[y].CalculateHash(), Is.EqualTo(wantedBlock.Transactions[y].CalculateHash()));
            }

            Assert.That(b.Uncles, Is.EquivalentTo(wantedBlock.Uncles));

            if (wantedBlock.Withdrawals == null)
                Assert.That(b.Withdrawals, Is.EqualTo(wantedBlock.Withdrawals));
            else
                Assert.That(b.Withdrawals, Is.EquivalentTo(wantedBlock.Withdrawals));


            Assert.That(r.Length, Is.EqualTo(wantedReceipts.Length), "Incorrect amount of receipts.");

            for (int y = 0; y < wantedReceipts.Length; y++)
            {
                Assert.That(r[y].TxType, Is.EqualTo(wantedReceipts[y].TxType));
                Assert.That(r[y].PostTransactionState, Is.EqualTo(wantedReceipts[y].PostTransactionState));
                Assert.That(r[y].GasUsedTotal, Is.EqualTo(wantedReceipts[y].GasUsedTotal));
                Assert.That(r[y].Bloom, Is.EqualTo(wantedReceipts[y].Bloom));
                Assert.That(r[y].Logs, Is.EquivalentTo(wantedReceipts[y].Logs));
                if (wantedReceipts[y].Error == null)
                    Assert.That(r[y].Error, new OrConstraint( Is.Null, Is.Empty));
                else
                    Assert.That(r[y].Error, Is.EqualTo(wantedReceipts[y].Error));
            }
        }
        
    }

}
