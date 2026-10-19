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
        using ParkedCleaner parked = new(_logManager);

        try
        {
            parked.FinalizeBlock(AlreadyFinalized);
            Assert.That(parked.CleaningStarted.Wait(TimeSpan.FromSeconds(10)), Is.True, "the first clean never started");

            parked.FinalizeBlock(FinalizedWhileCleaning);
        }
        finally
        {
            parked.ReleaseCleaning.Set();
        }

        await parked.Cleaner.CleaningTask;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parked.ProcessedTxsDb.GetAllKeys(), Is.Empty);
            Assert.That(parked.BlobTxStorage.TryGetBlobTransactionsFromBlock(FinalizedWhileCleaning, out _), Is.False);
        }
    }

    [Test]
    public void dispose_should_wait_for_the_running_clean_and_not_start_another()
    {
        using ParkedCleaner parked = new(_logManager);
        // Release the parked clean only once Dispose has marked the cleaner disposed, then let Dispose wait for it.
        parked.BlockTree.When(b => b.BlocksFinalized -= Arg.Any<EventHandler<FinalizeEventArgs>>())
            .Do(_ => parked.ReleaseCleaning.Set());

        try
        {
            parked.FinalizeBlock(AlreadyFinalized);
            Assert.That(parked.CleaningStarted.Wait(TimeSpan.FromSeconds(10)), Is.True, "the first clean never started");

            parked.FinalizeBlock(FinalizedWhileCleaning);
            parked.Cleaner.Dispose();
        }
        finally
        {
            parked.ReleaseCleaning.Set();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(parked.Cleaner.CleaningTask.IsCompleted, Is.True);
            Assert.That(parked.BatchesStarted, Is.EqualTo(1));
            Assert.That(parked.BlobTxStorage.TryGetBlobTransactionsFromBlock(AlreadyFinalized, out _), Is.False);
            Assert.That(parked.BlobTxStorage.TryGetBlobTransactionsFromBlock(FinalizedWhileCleaning, out _), Is.True);
        }
    }

    private const ulong AlreadyFinalized = 101;
    private const ulong FinalizedWhileCleaning = 110;

    /// <summary>A cleaner whose first clean parks inside the database until <see cref="ReleaseCleaning"/> is set.</summary>
    private sealed class ParkedCleaner : IDisposable
    {
        private int _batchesStarted;

        public IDb ProcessedTxsDb { get; }
        public BlobTxStorage BlobTxStorage { get; }
        public IBlockTree BlockTree { get; } = Substitute.For<IBlockTree>();
        public ProcessedTransactionsDbCleaner Cleaner { get; }
        public ManualResetEventSlim CleaningStarted { get; } = new();
        public ManualResetEventSlim ReleaseCleaning { get; } = new();
        public int BatchesStarted => Volatile.Read(ref _batchesStarted);

        public ParkedCleaner(ILogManager logManager)
        {
            ulong[] blocks = [1, AlreadyFinalized, 102, 105, FinalizedWhileCleaning];

            IColumnsDb<BlobTxsColumns> columnsDb = new MemColumnsDb<BlobTxsColumns>(BlobTxsColumns.ProcessedTxs);
            BlobTxStorage = new(columnsDb);
            foreach (ulong block in blocks)
            {
                using ArrayPoolListRef<Transaction> txs = new(2, GetTx(TestItem.PrivateKeyA), GetTx(TestItem.PrivateKeyB));
                BlobTxStorage.AddBlobTransactionsFromBlock(block, txs);
            }

            ProcessedTxsDb = columnsDb.GetColumnDb(BlobTxsColumns.ProcessedTxs);
            Assert.That(ProcessedTxsDb.GetAllKeys().Count(), Is.EqualTo(3 * blocks.Length),
                "expected the block-number key and the two transaction keys of every block");

            // Park the first clean inside the database, so the second finalization provably arrives while it runs.
            IDb gatedDb = Substitute.For<IDb>();
            gatedDb.GetAllKeys().Returns(_ => ProcessedTxsDb.GetAllKeys());
            gatedDb.StartWriteBatch().Returns(_ =>
            {
                if (Interlocked.Increment(ref _batchesStarted) == 1)
                {
                    CleaningStarted.Set();
                    ReleaseCleaning.Wait();
                }

                return ProcessedTxsDb.StartWriteBatch();
            });

            IColumnsDb<BlobTxsColumns> gatedColumnsDb = Substitute.For<IColumnsDb<BlobTxsColumns>>();
            gatedColumnsDb.GetColumnDb(BlobTxsColumns.ProcessedTxs).Returns(gatedDb);
            IDbProvider dbProvider = Substitute.For<IDbProvider>();
            dbProvider.BlobTransactionsDb.Returns(gatedColumnsDb);

            Cleaner = new(BlockTree, dbProvider, logManager, new TxPoolConfig());
        }

        public void FinalizeBlock(ulong blockNumber) =>
            BlockTree.BlocksFinalized += Raise.EventWith(new FinalizeEventArgs(Build.A.BlockHeader.WithNumber(blockNumber).TestObject));

        public void Dispose()
        {
            CleaningStarted.Dispose();
            ReleaseCleaning.Dispose();
        }
    }
}
