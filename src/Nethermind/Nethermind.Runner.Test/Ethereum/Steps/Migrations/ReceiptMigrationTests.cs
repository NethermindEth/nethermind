// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Collections;
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
        [TestCase(0)]
        [TestCase(1)]
        public async Task Truncated_legacy_receipts_leave_migration_pointer_gap(int receiptCount)
        {
            InMemoryReceiptStorage source = new();
            BlockTreeBuilder blockTreeBuilder = Core.Test.Builders.Build.A.BlockTree()
                .WithTransactions(source)
                .OfChainLength(2);
            IBlockTree blockTree = blockTreeBuilder.TestObject;
            Block block = blockTree.FindBlock(1);
            TxReceipt[] receipts = source.Get(block)[..receiptCount];
            source.Insert(block, receipts, ensureCanonical: false);

            InMemoryReceiptStorage destination = new() { MigratedBlockNumber = ulong.MaxValue };
            TestReceiptStorage receiptStorage = new(source, destination);
            TestMemColumnsDb<ReceiptsColumns> receiptColumnDb = new();
            ISyncModeSelector syncModeSelector = Substitute.For<ISyncModeSelector>();
            syncModeSelector.Current.Returns(SyncMode.WaitingForBlock);

            ReceiptMigration migration = new(
                receiptStorage,
                blockTree,
                syncModeSelector,
                blockTreeBuilder.ChainLevelInfoRepository,
                new ReceiptConfig { StoreReceipts = true, ReceiptsMigration = true },
                receiptColumnDb,
                Substitute.For<IReceiptsRecovery>(),
                LimboLogs.Instance);

            await migration.Run(CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(destination.Count, Is.Zero);
                Assert.That(destination.MigratedBlockNumber, Is.EqualTo(ulong.MaxValue));
                Assert.That(source.Get(block), Has.Length.EqualTo(receiptCount));
            }
        }

        [Test]
        public async Task Incomplete_legacy_receipts_leave_migration_pointer_gap()
        {
            InMemoryReceiptStorage source = new();
            BlockTreeBuilder blockTreeBuilder = Core.Test.Builders.Build.A.BlockTree()
                .WithTransactions(source)
                .OfChainLength(3);
            IBlockTree blockTree = blockTreeBuilder.TestObject;
            Block incompleteBlock = blockTree.FindBlock(1);
            Block completeBlock = blockTree.FindBlock(2);

            TxReceipt receipt = Core.Test.Builders.Build.A.Receipt.WithTransactionHash(TestItem.KeccakA).TestObject;
            source.Insert(incompleteBlock, new TxReceipt[] { receipt, null }, ensureCanonical: false);

            InMemoryReceiptStorage destination = new() { MigratedBlockNumber = ulong.MaxValue };
            TestReceiptStorage receiptStorage = new(source, destination);
            TestMemColumnsDb<ReceiptsColumns> receiptColumnDb = new();
            ISyncModeSelector syncModeSelector = Substitute.For<ISyncModeSelector>();
            syncModeSelector.Current.Returns(SyncMode.WaitingForBlock);

            ReceiptMigration migration = new(
                receiptStorage,
                blockTree,
                syncModeSelector,
                blockTreeBuilder.ChainLevelInfoRepository,
                new ReceiptConfig { StoreReceipts = true, ReceiptsMigration = true },
                receiptColumnDb,
                Substitute.For<IReceiptsRecovery>(),
                LimboLogs.Instance);

            await migration.Run(CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(destination.Count, Is.EqualTo(completeBlock.Transactions.Length));
                Assert.That(destination.MigratedBlockNumber, Is.EqualTo(2));
                Assert.That(source.Get(incompleteBlock)[1], Is.Null);
            }
        }

        [TestCase(true, false, 0UL, TestName = "Missing_block_body_is_skipped_without_holding_migration_pointer")]
        [TestCase(false, true, ulong.MaxValue, TestName = "Failed_receipt_recovery_leaves_migration_pointer_gap")]
        public async Task Unmigrated_complete_receipts_preserve_legacy_data(
            bool missingBlockBody,
            bool recoveryFails,
            ulong expectedMigratedBlockNumber)
        {
            InMemoryReceiptStorage source = new();
            BlockTreeBuilder blockTreeBuilder = Core.Test.Builders.Build.A.BlockTree()
                .WithTransactions(source)
                .OfChainLength(2);
            IBlockTree populatedBlockTree = blockTreeBuilder.TestObject;
            Block block = populatedBlockTree.FindBlock(1);
            Assert.That(source.Get(block), Is.Not.Empty);

            IBlockTree migrationBlockTree = populatedBlockTree;
            if (missingBlockBody)
            {
                migrationBlockTree = Substitute.For<IBlockTree>();
                migrationBlockTree.Head.Returns(populatedBlockTree.Head);
                migrationBlockTree.FindBlock(Arg.Any<Hash256>(), BlockTreeLookupOptions.None).Returns((Block)null);
            }

            IReceiptsRecovery recovery = Substitute.For<IReceiptsRecovery>();
            recovery.TryRecover(Arg.Any<Block>(), Arg.Any<TxReceipt[]>(), false)
                .Returns(recoveryFails ? ReceiptsRecoveryResult.Fail : ReceiptsRecoveryResult.Success);
            InMemoryReceiptStorage destination = new() { MigratedBlockNumber = ulong.MaxValue };
            TestReceiptStorage receiptStorage = new(source, destination);
            ISyncModeSelector syncModeSelector = Substitute.For<ISyncModeSelector>();
            syncModeSelector.Current.Returns(SyncMode.WaitingForBlock);

            ReceiptMigration migration = new(
                receiptStorage,
                migrationBlockTree,
                syncModeSelector,
                blockTreeBuilder.ChainLevelInfoRepository,
                new ReceiptConfig { StoreReceipts = true, ReceiptsMigration = true },
                new TestMemColumnsDb<ReceiptsColumns>(),
                recovery,
                LimboLogs.Instance);

            await migration.Run(CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(destination.Count, Is.Zero);
                Assert.That(destination.MigratedBlockNumber, Is.EqualTo(expectedMigratedBlockNumber));
                Assert.That(source.Get(block), Is.Not.Empty);
            }
        }

        [TestCase(null, 0UL, false, false, false, false)] // No change to migrate
        [TestCase(5UL, 5UL, false, false, false, true)] // Explicit command and partially migrated
        [TestCase(null, 5UL, true, false, false, true)] // Partially migrated
        [TestCase(5UL, 0UL, false, false, false, true)] // Explicit command
        [TestCase(null, 0UL, true, false, false, true)] // Force reset
        [TestCase(null, 0UL, false, false, true, true)] // Encoding mismatch
        [TestCase(null, 0UL, false, true, false, true)] // Encoding mismatch
        [TestCase(null, 0UL, false, true, true, false)] // Encoding match
        public async Task RunMigration(ulong? commandStartBlockNumber, ulong currentMigratedBlockNumber, bool forceReset, bool receiptIsCompact, bool useCompactEncoding, bool wasMigrated)
        {
            int chainLength = 10;
            IReceiptConfig receiptConfig = new ReceiptConfig()
            {
                ForceReceiptsMigration = forceReset,
                StoreReceipts = true,
                ReceiptsMigration = true,
                CompactReceiptStore = useCompactEncoding
            };

            InMemoryReceiptStorage inMemoryReceiptStorage = new(true) { MigratedBlockNumber = currentMigratedBlockNumber };
            BlockTreeBuilder blockTreeBuilder = Core.Test.Builders.Build.A.BlockTree()
                .WithTransactions(inMemoryReceiptStorage)
                .OfChainLength(chainLength);
            IBlockTree blockTree = blockTreeBuilder.TestObject;
            IChainLevelInfoRepository chainLevelInfoRepository = blockTreeBuilder.ChainLevelInfoRepository;

            InMemoryReceiptStorage outMemoryReceiptStorage = new(true) { MigratedBlockNumber = currentMigratedBlockNumber };
            TestReceiptStorage receiptStorage = new(inMemoryReceiptStorage, outMemoryReceiptStorage);
            ReceiptArrayStorageDecoder receiptArrayStorageDecoder = new(receiptIsCompact);

            ISyncModeSelector syncModeSelector = Substitute.For<ISyncModeSelector>();
            syncModeSelector.Current.Returns(SyncMode.WaitingForBlock);

            TestMemColumnsDb<ReceiptsColumns> receiptColumnDb = new();
            TestMemDb blocksDb = (TestMemDb)receiptColumnDb.GetColumnDb(ReceiptsColumns.Blocks);
            TestMemDb txDb = (TestMemDb)receiptColumnDb.GetColumnDb(ReceiptsColumns.Transactions);
            TestMemDb defaultDb = (TestMemDb)receiptColumnDb.GetColumnDb(ReceiptsColumns.Default);

            // Put the last block receipt encoding
            Block lastBlock = blockTree.FindBlock((ulong)(chainLength - 1));
            TxReceipt[] receipts = inMemoryReceiptStorage.Get(lastBlock);
            using (ArrayPoolSpan<byte> encodedReceipts = receiptArrayStorageDecoder.EncodeToArrayPoolSpan(receipts, RlpBehaviors.Storage))
            {
                ((IKeyValueStoreWithBatching)blocksDb).PutSpan(Bytes.Concat(lastBlock.Number.ToBigEndianByteArray(), lastBlock.Hash.BytesToArray()).AsSpan(), encodedReceipts);
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
                int blockNum = commandStartBlockNumber.HasValue ? (int)commandStartBlockNumber.Value : (chainLength - 1);
                Block[] migratedBlocks = Enumerable.Range(1, blockNum)
                    .Select(blockNumber => blockTree.FindBlock((ulong)blockNumber))
                    .ToArray();
                int txCount = migratedBlocks.Sum(block => block.Transactions.Length);
                int receiptBlockCount = migratedBlocks.Count(block => block.Transactions.Length > 0);
                defaultDb.KeyWasWritten((item => item.Item2 is null), txCount);
                ((TestMemDb)receiptColumnDb.GetColumnDb(ReceiptsColumns.Blocks)).KeyWasRemoved((_ => true), receiptBlockCount);
                Assert.That(outMemoryReceiptStorage.Count, Is.EqualTo(txCount));
            }
            else
            {
                txDb.KeyWasWritten((item => item.Item2 is null), 0);
            }
        }

        [TestCaseSource(nameof(PointerTrackerScenarios))]
        public void MigrationPointerTracker_advances_pointer_only_across_contiguously_completed_blocks(
            ulong to, ulong[] completionOrder, ulong[] expectedPointerAfterEachCompletion)
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
            yield return new TestCaseData(3UL, new ulong[] { 3UL, 2UL, 1UL, 0UL }, new ulong[] { 3UL, 2UL, 1UL, 0UL })
                .SetName("DescendingCompletionAdvancesOneByOne");
            yield return new TestCaseData(10UL, new ulong[] { 10UL, 9UL, 7UL, 8UL }, new ulong[] { 10UL, 9UL, 9UL, 7UL })
                .SetName("GapHoldsPointerUntilFilledThenJumps");
            yield return new TestCaseData(3UL, new ulong[] { 0UL, 1UL, 2UL, 3UL }, new ulong[] { 4UL, 4UL, 4UL, 0UL })
                .SetName("UnfinishedHighestBlockHoldsPointerUntilItCompletes");
        }

        [TestCaseSource(nameof(IncompletePointerTrackerScenarios))]
        public void MigrationPointerTracker_does_not_advance_across_incomplete_blocks(
            ulong to, MigrationReport[] reports, ulong expectedPointer)
        {
            InMemoryReceiptStorage receiptStorage = new() { MigratedBlockNumber = to + 1 };
            ReceiptMigration.MigrationPointerTracker tracker = new(receiptStorage, to);

            foreach (MigrationReport report in reports)
            {
                if (report.IsComplete)
                {
                    tracker.ReportCompleted(report.BlockNumber);
                }
                else
                {
                    tracker.ReportIncomplete(report.BlockNumber);
                }
            }

            Assert.That(receiptStorage.MigratedBlockNumber, Is.EqualTo(expectedPointer));
        }

        private static IEnumerable<TestCaseData> IncompletePointerTrackerScenarios()
        {
            yield return new TestCaseData(5UL,
                new[] { Complete(4), Complete(3), Incomplete(5), Complete(2) },
                6UL).SetName("HigherIncompleteDiscardsEarlierLowerCompletions");
            yield return new TestCaseData(5UL,
                new[] { Complete(5), Incomplete(4), Complete(3), Complete(2) },
                5UL).SetName("LowerCompletionsAfterIncompleteDoNotAdvancePointer");
            yield return new TestCaseData(5UL,
                new[] { Complete(5), Complete(4), Incomplete(2), Incomplete(3), Complete(1), Complete(0) },
                4UL).SetName("HighestOfMultipleIncompleteBlocksHoldsPointer");
        }

        private static MigrationReport Complete(ulong blockNumber) => new(blockNumber, true);

        private static MigrationReport Incomplete(ulong blockNumber) => new(blockNumber, false);

        public readonly record struct MigrationReport(ulong BlockNumber, bool IsComplete);

        private class TestReceiptStorage(IReceiptStorage inStorage, IReceiptStorage outStorage) : IReceiptMigrationStore
        {
            public Hash256 FindBlockHash(Hash256 txHash) => inStorage.FindBlockHash(txHash);

            public void InsertForMigration(Block block, TxReceipt[] receipts) => outStorage.Insert(block, receipts);

            public TxReceipt[] GetForMigration(ulong blockNumber, Hash256 blockHash) => inStorage.Get(blockHash, recover: false);

            public TxReceipt[] Get(Block block, bool recover = true, bool recoverSender = true) => inStorage.Get(block, recover, recoverSender);

            public TxReceipt[] Get(Hash256 blockHash, bool recover = true) => inStorage.Get(blockHash, recover);

            public bool CanGetReceiptsByHash(ulong blockNumber) => inStorage.CanGetReceiptsByHash(blockNumber);
            public bool TryGetReceiptsIterator(ulong blockNumber, Hash256 blockHash, out ReceiptsIterator iterator) => inStorage.TryGetReceiptsIterator(blockNumber, blockHash, out iterator);

            public void Insert(Block block, TxReceipt[] txReceipts, IReleaseSpec spec, bool ensureCanonical, WriteFlags writeFlags, ulong? lastBlockNumber) => outStorage.Insert(block, txReceipts, spec, ensureCanonical, writeFlags, lastBlockNumber);
            public void Insert(Block block, TxReceipt[] txReceipts, bool ensureCanonical, WriteFlags writeFlags, ulong? lastBlockNumber) => outStorage.Insert(block, txReceipts, ensureCanonical, writeFlags, lastBlockNumber);

            public ulong MigratedBlockNumber
            {
                get => outStorage.MigratedBlockNumber;
                set => outStorage.MigratedBlockNumber = value;
            }

            public bool HasBlock(ulong blockNumber, Hash256 hash) => outStorage.HasBlock(blockNumber, hash);

            public void EnsureCanonical(Block block)
            {
            }

            public void RemoveReceipts(Block block)
            {
            }

            public void RemoveReceipts(ulong blockNumber, Hash256 blockHash)
            {
            }

#pragma warning disable CS0067
            public event EventHandler<BlockReplacementEventArgs> NewCanonicalReceipts;
            public event EventHandler<ReceiptsEventArgs> ReceiptsInserted;
#pragma warning restore CS0067
        }
    }
}
