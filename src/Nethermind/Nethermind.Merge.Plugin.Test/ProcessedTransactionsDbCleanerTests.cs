// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

[TestFixture]
[Parallelizable(ParallelScope.Self)]
public class ProcessedTransactionsDbCleanerTests
{
    private readonly ILogManager _logManager = LimboLogs.Instance;
    private readonly ISpecProvider _specProvider = MainnetSpecProvider.Instance;

    private static Transaction GetTx(PrivateKey sender) =>
        Build.A.Transaction
            .WithShardBlobTxTypeAndFields()
            .WithMaxFeePerGas(UInt256.One)
            .WithMaxPriorityFeePerGas(UInt256.One)
            .WithNonce(0UL)
            .SignedAndResolved(new EthereumEcdsa(MainnetSpecProvider.Instance.ChainId), sender).TestObject;

    [Test]
    public async Task should_remove_processed_txs_from_db_after_finalization([Values(0UL, 1UL, 42UL, 358UL)] ulong blockOfTxs, [Values(1UL, 42UL, 358UL)] ulong finalizedBlock, [Values] bool legacy)
    {
        IColumnsDb<BlobTxsColumns> columnsDb = new MemColumnsDb<BlobTxsColumns>(BlobTxsColumns.ProcessedTxs);
        BlobTxStorage blobTxStorage = new(columnsDb);
        using (ArrayPoolListRef<Transaction> txs = new(2, GetTx(TestItem.PrivateKeyA), GetTx(TestItem.PrivateKeyB)))
        {
            if (legacy)
            {
                using ArrayPoolSpan<byte> encoded = TxDecoder.Instance.EncodeToArrayPoolSpan(txs.AsSpan(), RlpBehaviors.InMempoolForm | RlpBehaviors.Storage);
                columnsDb.GetColumnDb(BlobTxsColumns.ProcessedTxs).PutSpan(blockOfTxs.ToBigEndianSpanWithoutLeadingZeros(out _), encoded);
            }
            else
            {
                blobTxStorage.AddBlobTransactionsFromBlock(blockOfTxs, txs);
            }
        }

        Assert.That(blobTxStorage.TryGetBlobTransactionsFromBlock(blockOfTxs, out Transaction[]? returnedTxs), Is.True);
        Assert.That(returnedTxs!.Length, Is.EqualTo(2));

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IDbProvider dbProvider = Substitute.For<IDbProvider>();
        dbProvider.BlobTransactionsDb.Returns(columnsDb);
        ProcessedTransactionsDbCleaner dbCleaner = new(blockTree, dbProvider, _logManager, new TxPoolConfig());

        blockTree.BlocksFinalized += Raise.EventWith(
            new FinalizeEventArgs(Build.A.BlockHeader.WithNumber(finalizedBlock).TestObject));

        await dbCleaner.CleaningTask;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blobTxStorage.TryGetBlobTransactionsFromBlock(blockOfTxs, out _), Is.EqualTo(blockOfTxs > finalizedBlock));
            Assert.That(columnsDb.GetColumnDb(BlobTxsColumns.ProcessedTxs).GetAllKeys().Count(),
                Is.EqualTo(blockOfTxs <= finalizedBlock ? 0 : legacy ? 1 : 3));
        }
    }

    [Test]
    public async Task should_clean_to_the_highest_finalized_block_when_a_finalization_arrives_while_cleaning()
    {
        const ulong AlreadyFinalized = 101;
        const ulong FinalizedWhileCleaning = 110;
        ulong[] blocks = [1, AlreadyFinalized, 102, 105, FinalizedWhileCleaning];

        IColumnsDb<BlobTxsColumns> columnsDb = new MemColumnsDb<BlobTxsColumns>(BlobTxsColumns.ProcessedTxs);
        BlobTxStorage blobTxStorage = new(columnsDb);
        foreach (ulong block in blocks)
        {
            using ArrayPoolListRef<Transaction> txs = new(2, GetTx(TestItem.PrivateKeyA), GetTx(TestItem.PrivateKeyB));
            blobTxStorage.AddBlobTransactionsFromBlock(block, txs);
        }

        IDb processedTxsDb = columnsDb.GetColumnDb(BlobTxsColumns.ProcessedTxs);
        Assert.That(processedTxsDb.GetAllKeys().Count(), Is.EqualTo(3 * blocks.Length),
            "expected the block-number key and the two transaction keys of every block");

        using ManualResetEventSlim cleaningStarted = new();
        using ManualResetEventSlim releaseCleaning = new();
        int batchesStarted = 0;

        // Park the first clean inside the database, so the second finalization provably arrives while it runs.
        IDb gatedDb = Substitute.For<IDb>();
        gatedDb.GetAllKeys().Returns(_ => processedTxsDb.GetAllKeys());
        gatedDb.StartWriteBatch().Returns(_ =>
        {
            if (Interlocked.Increment(ref batchesStarted) == 1)
            {
                cleaningStarted.Set();
                releaseCleaning.Wait();
            }

            return processedTxsDb.StartWriteBatch();
        });

        IColumnsDb<BlobTxsColumns> gatedColumnsDb = Substitute.For<IColumnsDb<BlobTxsColumns>>();
        gatedColumnsDb.GetColumnDb(BlobTxsColumns.ProcessedTxs).Returns(gatedDb);
        IDbProvider dbProvider = Substitute.For<IDbProvider>();
        dbProvider.BlobTransactionsDb.Returns(gatedColumnsDb);

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        ProcessedTransactionsDbCleaner dbCleaner = new(blockTree, dbProvider, _logManager, new TxPoolConfig());

        try
        {
            blockTree.BlocksFinalized += Raise.EventWith(
                new FinalizeEventArgs(Build.A.BlockHeader.WithNumber(AlreadyFinalized).TestObject));
            Assert.That(cleaningStarted.Wait(TimeSpan.FromSeconds(10)), Is.True, "the first clean never started");

            blockTree.BlocksFinalized += Raise.EventWith(
                new FinalizeEventArgs(Build.A.BlockHeader.WithNumber(FinalizedWhileCleaning).TestObject));
        }
        finally
        {
            releaseCleaning.Set();
        }

        await dbCleaner.CleaningTask;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(processedTxsDb.GetAllKeys(), Is.Empty);
            Assert.That(blobTxStorage.TryGetBlobTransactionsFromBlock(FinalizedWhileCleaning, out _), Is.False);
        }
    }
}
