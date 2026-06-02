// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Init.Steps.Migrations;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Repositories;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Ethereum.Steps.Migrations
{
    public class ReceiptMigrationTests
    {
        [TestCase(null, 0, false, false, false, false)] // No change to migrate
        [TestCase(5, 5, false, false, false, true)] // Explicit command and partially migrated
        [TestCase(null, 5, true, false, false, true)] // Partially migrated
        [TestCase(5, 0, false, false, false, true)] // Explicit command
        [TestCase(null, 0, true, false, false, true)] // Force reset
        [TestCase(null, 0, false, false, true, true)] // Encoding mismatch
        [TestCase(null, 0, false, true, false, true)] // Encoding mismatch
        [TestCase(null, 0, false, true, true, false)] // Encoding match
        public async Task RunMigration(int? commandStartBlockNumber, long currentMigratedBlockNumber, bool forceReset, bool receiptIsCompact, bool useCompactEncoding, bool wasMigrated)
        {
            int chainLength = 10;
            IReceiptConfig receiptConfig = new ReceiptConfig()
            {
                ForceReceiptsMigration = forceReset,
                StoreReceipts = true,
                ReceiptsMigration = true,
                CompactReceiptStore = useCompactEncoding
            };

            BlockTreeBuilder blockTreeBuilder = Core.Test.Builders.Build.A.BlockTree().OfChainLength(chainLength);
            IBlockTree blockTree = blockTreeBuilder.TestObject;
            IChainLevelInfoRepository chainLevelInfoRepository = blockTreeBuilder.ChainLevelInfoRepository;

            InMemoryReceiptStorage inMemoryReceiptStorage = new(true) { MigratedBlockNumber = currentMigratedBlockNumber };
            InMemoryReceiptStorage outMemoryReceiptStorage = new(true) { MigratedBlockNumber = currentMigratedBlockNumber };
            TestReceiptStorage receiptStorage = new(inMemoryReceiptStorage, outMemoryReceiptStorage);
            ReceiptArrayStorageDecoder receiptArrayStorageDecoder = new(receiptIsCompact);

            ISyncModeSelector syncModeSelector = Substitute.For<ISyncModeSelector>();
            syncModeSelector.Current.Returns(SyncMode.WaitingForBlock);

            // Insert the blocks
            int txIndex = 0;
            for (int i = 1; i < chainLength; i++)
            {
                Block block = blockTree.FindBlock(i);
                inMemoryReceiptStorage.Insert(block, new[] {
                    Core.Test.Builders.Build.A.Receipt.WithTransactionHash(TestItem.Keccaks[txIndex++]).TestObject,
                    Core.Test.Builders.Build.A.Receipt.WithTransactionHash(TestItem.Keccaks[txIndex++]).TestObject
                });
            }

            TestMemColumnsDb<ReceiptsColumns> receiptColumnDb = new();
            TestMemDb blocksDb = (TestMemDb)receiptColumnDb.GetColumnDb(ReceiptsColumns.Blocks);
            TestMemDb txDb = (TestMemDb)receiptColumnDb.GetColumnDb(ReceiptsColumns.Transactions);
            TestMemDb defaultDb = (TestMemDb)receiptColumnDb.GetColumnDb(ReceiptsColumns.Default);

            // Put the last block receipt encoding
            Block lastBlock = blockTree.FindBlock(chainLength - 1);
            TxReceipt[] receipts = inMemoryReceiptStorage.Get(lastBlock);
            using (NettyRlpStream nettyStream = receiptArrayStorageDecoder.EncodeToNewNettyStream(receipts, RlpBehaviors.Storage))
            {
                ((IKeyValueStoreWithBatching)blocksDb).PutSpan(Bytes.Concat(lastBlock.Number.ToBigEndianByteArray(), lastBlock.Hash.BytesToArray()).AsSpan(), nettyStream.AsSpan());
            }

            ReceiptMigration migration = new(
                receiptStorage,
                blockTree,
                syncModeSelector,
                chainLevelInfoRepository,
                receiptConfig,
                receiptColumnDb,
                Substitute.For<IReceiptsRecovery>(),
                LimboLogs.Instance
            );

            if (commandStartBlockNumber.HasValue)
            {
                _ = migration.Run(0, commandStartBlockNumber.Value);
                await migration._migrationTask!;
            }
            else
            {
                await migration.Run(CancellationToken.None);
                Assert.That(() => outMemoryReceiptStorage.MigratedBlockNumber, Is.InRange(0, 1).After(1000, 10));
            }

            if (wasMigrated)
            {
                int blockNum = commandStartBlockNumber ?? (chainLength - 1);
                int txCount = blockNum * 2;
                defaultDb.KeyWasWritten((item => item.Item2 is null), txCount);
                ((TestMemDb)receiptColumnDb.GetColumnDb(ReceiptsColumns.Blocks)).KeyWasRemoved((_ => true), blockNum);
                Assert.That(outMemoryReceiptStorage.Count, Is.EqualTo(txCount));
            }
            else
            {
                txDb.KeyWasWritten((item => item.Item2 is null), 0);
            }
        }

        [TestCaseSource(nameof(PointerTrackerScenarios))]
        public void MigrationPointerTracker_advances_pointer_only_across_contiguously_completed_blocks(
            long to, long[] completionOrder, long[] expectedPointerAfterEachCompletion)
        {
            InMemoryReceiptStorage receiptStorage = new() { MigratedBlockNumber = to + 1 };
            ReceiptMigration.MigrationPointerTracker tracker = new(receiptStorage, to);

            for (int i = 0; i < completionOrder.Length; i++)
            {
                tracker.ReportCompleted(completionOrder[i]);
                Assert.That(receiptStorage.MigratedBlockNumber, Is.EqualTo(expectedPointerAfterEachCompletion[i]),
                    $"pointer after completing block {completionOrder[i]}");
            }
        }

        private static IEnumerable<TestCaseData> PointerTrackerScenarios()
        {
            yield return new TestCaseData(3L, new[] { 3L, 2L, 1L, 0L }, new[] { 3L, 2L, 1L, 0L })
                .SetName("DescendingCompletionAdvancesOneByOne");
            yield return new TestCaseData(10L, new[] { 10L, 9L, 7L, 8L }, new[] { 10L, 9L, 9L, 7L })
                .SetName("GapHoldsPointerUntilFilledThenJumps");
            yield return new TestCaseData(3L, new[] { 0L, 1L, 2L, 3L }, new[] { 4L, 4L, 4L, 0L })
                .SetName("UnfinishedHighestBlockHoldsPointerUntilItCompletes");
        }

        private class TestReceiptStorage(IReceiptStorage inStorage, IReceiptStorage outStorage) : IReceiptMigrationStore
        {
            public Hash256 FindBlockHash(Hash256 txHash) => inStorage.FindBlockHash(txHash);

            public void InsertForMigration(Block block, TxReceipt[] receipts) => outStorage.Insert(block, receipts);

            public TxReceipt[] Get(Block block, bool recover = true, bool recoverSender = true) => inStorage.Get(block, recover, recoverSender);

            public TxReceipt[] Get(Hash256 blockHash, bool recover = true) => inStorage.Get(blockHash, recover);

            public bool CanGetReceiptsByHash(long blockNumber) => inStorage.CanGetReceiptsByHash(blockNumber);
            public bool TryGetReceiptsIterator(long blockNumber, Hash256 blockHash, out ReceiptsIterator iterator) => inStorage.TryGetReceiptsIterator(blockNumber, blockHash, out iterator);

            public void Insert(Block block, TxReceipt[] txReceipts, IReleaseSpec spec, bool ensureCanonical, WriteFlags writeFlags, long? lastBlockNumber) => outStorage.Insert(block, txReceipts, spec, ensureCanonical, writeFlags, lastBlockNumber);
            public void Insert(Block block, TxReceipt[] txReceipts, bool ensureCanonical, WriteFlags writeFlags, long? lastBlockNumber) => outStorage.Insert(block, txReceipts, ensureCanonical, writeFlags, lastBlockNumber);

            public long MigratedBlockNumber
            {
                get => outStorage.MigratedBlockNumber;
                set => outStorage.MigratedBlockNumber = value;
            }

            public bool HasBlock(long blockNumber, Hash256 hash) => outStorage.HasBlock(blockNumber, hash);

            public void EnsureCanonical(Block block)
            {
            }

            public void RemoveReceipts(Block block)
            {
            }

#pragma warning disable CS0067
            public event EventHandler<BlockReplacementEventArgs> NewCanonicalReceipts;
            public event EventHandler<ReceiptsEventArgs> ReceiptsInserted;
#pragma warning restore CS0067
        }
    }
}
