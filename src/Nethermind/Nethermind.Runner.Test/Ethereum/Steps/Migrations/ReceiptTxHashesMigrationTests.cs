// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.History;
using Nethermind.Init.Steps.Migrations;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Ethereum.Steps.Migrations;

[Parallelizable(ParallelScope.All)]
public class ReceiptTxHashesMigrationTests
{
    private static readonly ReceiptArrayStorageDecoder CompactDecoder = new(compactEncoding: true);

    [Test]
    public async Task Adds_transaction_hashes_to_compact_receipts_and_does_not_repeat_after_a_restart()
    {
        using Chain chain = new(3);
        for (ulong number = 1; number <= 3; number++)
        {
            chain.Store(number, CompactDecoder.EncodeAsBytes(chain.ReceiptsWithoutTxHashes(number), RlpBehaviors.Storage));
        }

        await chain.RunMigration();

        using (Assert.EnterMultipleScope())
        {
            for (ulong number = 1; number <= 3; number++)
            {
                Assert.That(chain.Read(number), Is.EqualTo(CompactDecoder.EncodeAsBytes(chain.ReceiptsWithTxHashes(number), RlpBehaviors.Storage)));
            }
        }

        byte[] writtenAfterCompletion = CompactDecoder.EncodeAsBytes(chain.ReceiptsWithoutTxHashes(3), RlpBehaviors.Storage);
        chain.Store(3, writtenAfterCompletion);
        await chain.RunMigration();
        Assert.That(chain.Read(3), Is.EqualTo(writtenAfterCompletion));
    }

    [Test]
    public async Task Leaves_values_that_cannot_take_transaction_hashes_unchanged()
    {
        using Chain chain = new(4);
        byte[] withHashes = CompactDecoder.EncodeAsBytes(chain.ReceiptsWithTxHashes(1), RlpBehaviors.Storage);
        byte[] full = new ReceiptArrayStorageDecoder(compactEncoding: false).EncodeAsBytes(chain.ReceiptsWithTxHashes(2), RlpBehaviors.Storage);
        byte[] withoutBody = CompactDecoder.EncodeAsBytes(chain.ReceiptsWithoutTxHashes(4), RlpBehaviors.Storage);
        chain.Store(1, withHashes);
        chain.Store(2, full);
        chain.Store(4, withoutBody);
        chain.DeleteBody(4);

        await chain.RunMigration();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.Read(1), Is.EqualTo(withHashes));
            Assert.That(chain.Read(2), Is.EqualTo(full));
            Assert.That(chain.Read(3), Is.Null);
            Assert.That(chain.Read(4), Is.EqualTo(withoutBody));
        }
    }

    [Test]
    public async Task Waits_until_block_processing_stops_before_a_block()
    {
        using Chain chain = new(1);
        byte[] withoutHashes = CompactDecoder.EncodeAsBytes(chain.ReceiptsWithoutTxHashes(1), RlpBehaviors.Storage);
        chain.Store(1, withoutHashes);
        chain.ProcessingQueue.IsProcessingBlocks(Arg.Any<ulong?>()).Returns(true, true, false);

        await chain.RunMigration();

        chain.ProcessingQueue.Received(3).IsProcessingBlocks(Arg.Any<ulong?>());
        Assert.That(chain.Read(1), Is.Not.EqualTo(withoutHashes));
    }

    [Test]
    public async Task Waits_while_blocks_are_queued_for_processing()
    {
        using Chain chain = new(1);
        byte[] withoutHashes = CompactDecoder.EncodeAsBytes(chain.ReceiptsWithoutTxHashes(1), RlpBehaviors.Storage);
        chain.Store(1, withoutHashes);
        chain.ProcessingQueue.IsEmpty.Returns(false, false, true);

        await chain.RunMigration();

        _ = chain.ProcessingQueue.Received(3).IsEmpty;
        Assert.That(chain.Read(1), Is.Not.EqualTo(withoutHashes));
    }

    [Test]
    public async Task Stops_at_the_oldest_unreclaimed_block_when_it_rises_during_the_pass()
    {
        using Chain chain = new(3);
        byte[][] withoutHashes = new byte[4][];
        for (ulong number = 1; number <= 3; number++)
        {
            withoutHashes[number] = CompactDecoder.EncodeAsBytes(chain.ReceiptsWithoutTxHashes(number), RlpBehaviors.Storage);
            chain.Store(number, withoutHashes[number]);
        }

        chain.HistoryPruner.OldestUnreclaimedBlockNumber.Returns(0UL, 0UL, 3UL);
        TestLogger logger = new();

        await chain.RunMigration(new ReceiptConfig(), new OneLoggerLogManager(new(logger)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.Read(3), Is.EqualTo(CompactDecoder.EncodeAsBytes(chain.ReceiptsWithTxHashes(3), RlpBehaviors.Storage)));
            Assert.That(chain.Read(2), Is.EqualTo(withoutHashes[2]));
            Assert.That(chain.Read(1), Is.EqualTo(withoutHashes[1]));
            Assert.That(logger.LogList.Last(static line => line.StartsWith("Receipt hashes")), Does.Match(@"^Receipt hashes\s+1 /\s+1 "));
        }
    }

    [Test]
    public async Task Keeps_a_margin_above_the_history_pruning_cutoff()
    {
        using Chain chain = new(2);
        byte[] withoutHashes = CompactDecoder.EncodeAsBytes(chain.ReceiptsWithoutTxHashes(2), RlpBehaviors.Storage);
        chain.Store(2, withoutHashes);
        chain.HistoryPruner.CutoffBlockNumber.Returns(0UL);

        await chain.RunMigration();

        Assert.That(chain.Read(2), Is.EqualTo(withoutHashes));
    }

    [Test]
    public async Task Adds_transaction_hashes_to_receipts_written_while_disabled_after_it_is_enabled_again()
    {
        using Chain chain = new(2);
        for (ulong number = 1; number <= 2; number++)
        {
            chain.Store(number, CompactDecoder.EncodeAsBytes(chain.ReceiptsWithoutTxHashes(number), RlpBehaviors.Storage));
        }

        await chain.RunMigration();
        byte[] writtenWhileDisabled = CompactDecoder.EncodeAsBytes(chain.ReceiptsWithoutTxHashes(2), RlpBehaviors.Storage);
        chain.Store(2, writtenWhileDisabled);
        await chain.RunMigration(new ReceiptConfig { StoreTxHashes = false });

        await chain.RunMigration();

        Assert.That(chain.Read(2), Is.EqualTo(CompactDecoder.EncodeAsBytes(chain.ReceiptsWithTxHashes(2), RlpBehaviors.Storage)));
    }

    [TestCase(0UL, 0UL, null, 1UL)]
    [TestCase(8192UL, 0UL, null, 1UL)]
    [TestCase(8193UL, 0UL, null, 1UL)]
    [TestCase(8194UL, 0UL, null, 2UL)]
    [TestCase(20000UL, 0UL, null, 11808UL)]
    [TestCase(20000UL, 15000UL, null, 15000UL)]
    [TestCase(20000UL, 0UL, 15000UL, 16024UL)]
    public void Benchmark_bound_preserves_history_pruning_floors(ulong head, ulong oldest, ulong? cutoff, ulong expected)
    {
        using Chain chain = new(1);
        chain.HistoryPruner.OldestUnreclaimedBlockNumber.Returns(oldest);
        chain.HistoryPruner.CutoffBlockNumber.Returns(cutoff);
        ReceiptTxHashesMigration migration = chain.RegisteredMigrations().OfType<ReceiptTxHashesMigration>().Single();
        System.Reflection.MethodInfo method = typeof(ReceiptTxHashesMigration).GetMethod("GetLowestBlock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        Assert.That(method.Invoke(migration, [head]), Is.EqualTo(expected));
    }

    [Test]
    public void Is_registered_as_a_database_migration()
    {
        using Chain chain = new(1);

        Assert.That(chain.RegisteredMigrations().OfType<ReceiptTxHashesMigration>().Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task Logs_its_progress_while_it_runs()
    {
        using Chain chain = new(3);
        for (ulong number = 1; number <= 3; number++)
        {
            chain.Store(number, CompactDecoder.EncodeAsBytes(chain.ReceiptsWithoutTxHashes(number), RlpBehaviors.Storage));
        }

        TestLogger logger = new();
        await chain.RunMigration(new ReceiptConfig(), new OneLoggerLogManager(new(logger)));

        Assert.That(logger.LogList, Has.Some.Matches(@"^Receipt hashes\s+1 /\s+3 "));
    }

    [Test]
    public async Task Counts_the_blocks_done_before_a_restart_in_its_progress()
    {
        using Chain chain = new(3);
        for (ulong number = 1; number <= 3; number++)
        {
            chain.Store(number, CompactDecoder.EncodeAsBytes(chain.ReceiptsWithoutTxHashes(number), RlpBehaviors.Storage));
        }

        chain.SavePosition(2);
        TestLogger logger = new();
        await chain.RunMigration(new ReceiptConfig(), new OneLoggerLogManager(new(logger)));

        Assert.That(logger.LogList, Has.Some.Matches(@"^Receipt hashes\s+2 /\s+3 "));
    }

    [Test]
    public async Task Logs_a_final_progress_line_when_it_stops()
    {
        using Chain chain = new(3);
        for (ulong number = 1; number <= 3; number++)
        {
            chain.Store(number, CompactDecoder.EncodeAsBytes(chain.ReceiptsWithoutTxHashes(number), RlpBehaviors.Storage));
        }

        TestLogger logger = new();
        await chain.RunMigration(new ReceiptConfig(), new OneLoggerLogManager(new(logger)), new ManualTimestamper());

        Assert.That(logger.LogList.Where(static line => line.StartsWith("Receipt hashes")), Has.One.Matches(@"^Receipt hashes\s+3 /\s+3 "));
    }

    [Test]
    public async Task Does_nothing_when_disabled()
    {
        using Chain chain = new(1);
        byte[] withoutHashes = CompactDecoder.EncodeAsBytes(chain.ReceiptsWithoutTxHashes(1), RlpBehaviors.Storage);
        chain.Store(1, withoutHashes);

        await chain.RunMigration(new ReceiptConfig { StoreTxHashes = false });

        Assert.That(chain.Read(1), Is.EqualTo(withoutHashes));
    }

    private sealed class Chain : System.IDisposable
    {
        public readonly Block[] Blocks;
        public readonly IBlockProcessingQueue ProcessingQueue = Substitute.For<IBlockProcessingQueue>();
        public readonly IHistoryPruner HistoryPruner = Substitute.For<IHistoryPruner>();
        private readonly ISyncModeSelector _syncModeSelector = Substitute.For<ISyncModeSelector>();
        private readonly IContainer _container;
        private readonly IDb _receipts;

        public Chain(int length)
        {
            _syncModeSelector.Current.Returns(SyncMode.WaitingForBlock);
            ProcessingQueue.IsEmpty.Returns(true);
            _container = new ContainerBuilder()
                .AddModule(new TestNethermindModule(new ReceiptConfig()))
                .AddSingleton(_syncModeSelector)
                .AddSingleton(ProcessingQueue)
                .AddSingleton(HistoryPruner)
                .Build();
            _receipts = _container.Resolve<IColumnsDb<ReceiptsColumns>>().GetColumnDb(ReceiptsColumns.Blocks);

            IBlockTree blockTree = _container.Resolve<IBlockTree>();
            Blocks = new Block[length + 1];
            Blocks[0] = Core.Test.Builders.Build.A.Block.WithNumber(0).TestObject;
            AddToMainChain(blockTree, Blocks[0]);
            for (int number = 1; number <= length; number++)
            {
                Blocks[number] = Core.Test.Builders.Build.A.Block
                    .WithParent(Blocks[number - 1])
                    .WithTransactions(
                        Core.Test.Builders.Build.A.Transaction.WithNonce((ulong)number).SignedAndResolved().TestObject,
                        Core.Test.Builders.Build.A.Transaction.WithNonce((ulong)number + 100).SignedAndResolved().TestObject)
                    .TestObject;
                AddToMainChain(blockTree, Blocks[number]);
            }
        }

        public TxReceipt[] ReceiptsWithTxHashes(ulong number) =>
            Blocks[number].Transactions
                .Select(static tx => Core.Test.Builders.Build.A.Receipt.WithAllFieldsFilled.WithTransactionHash(tx.Hash).WithCalculatedBloom().TestObject)
                .ToArray();

        public TxReceipt[] ReceiptsWithoutTxHashes(ulong number) =>
            ReceiptsWithTxHashes(number).Select(static r => new TxReceipt(r) { TxHash = null }).ToArray();

        public void Store(ulong number, byte[] value) => _receipts.Set(Key(number), value);

        public byte[] Read(ulong number) => _receipts.Get(Key(number));

        public void SavePosition(ulong next) =>
            _container.Resolve<IColumnsDb<ReceiptsColumns>>().GetColumnDb(ReceiptsColumns.Default).PutSpan(ReceiptTxHashesMigration.NextBlockKey.Bytes, next.ToBigEndianSpanWithoutLeadingZeros(out _));

        public void DeleteBody(ulong number) => _container.Resolve<IBlockStore>().Delete(number, Blocks[number].Hash!);

        public IEnumerable<IDatabaseMigration> RegisteredMigrations() => _container.Resolve<IEnumerable<IDatabaseMigration>>();

        public Task RunMigration() => RunMigration(new ReceiptConfig());

        public Task RunMigration(ReceiptConfig config) => RunMigration(config, LimboLogs.Instance);

        /// <summary>
        /// Runs a new instance over the same databases, as a node restart does. Unless a clock is given, each clock read
        /// is one progress interval later than the previous one, so every block is a point at which progress is due.
        /// </summary>
        public async Task RunMigration(ReceiptConfig config, ILogManager logManager, ITimestamper timestamper = null)
        {
            await using ILifetimeScope scope = _container.BeginLifetimeScope(builder => builder
                .AddSingleton<IReceiptConfig>(config)
                .AddSingleton<ILogManager>(logManager)
                .AddSingleton<ITimestamper>(timestamper ?? new SteppingTimestamper())
                .AddSingleton<ReceiptTxHashesMigration>());
            ReceiptTxHashesMigration migration = scope.Resolve<ReceiptTxHashesMigration>();

            await migration.Run(CancellationToken.None);
            await migration.Completion;
        }

        private sealed class SteppingTimestamper : ITimestamper
        {
            private DateTime _now = DateTime.UnixEpoch;

            public DateTime UtcNow => _now += ReceiptTxHashesMigration.ProgressInterval;
        }

        private static void AddToMainChain(IBlockTree blockTree, Block block)
        {
            blockTree.SuggestBlock(block);
            blockTree.TryUpdateMainChain(block.Header, wereProcessed: true, forceUpdateHeadBlock: true, block);
        }

        private byte[] Key(ulong number)
        {
            byte[] key = new byte[40];
            KeyValueStoreExtensions.GetBlockNumPrefixedKey(number, Blocks[number].Hash!, key);
            return key;
        }

        public void Dispose() => _container.Dispose();
    }
}
