// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.Init.Steps.Migrations;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Repositories;
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

        for (ulong number = 1; number <= 3; number++)
        {
            Assert.That(chain.Read(number), Is.EqualTo(CompactDecoder.EncodeAsBytes(chain.ReceiptsWithTxHashes(number), RlpBehaviors.Storage)));
        }

        chain.BlockStore.ClearReceivedCalls();
        await chain.RunMigration();
        chain.BlockStore.DidNotReceiveWithAnyArgs().GetReceiptRecoveryBlock(default, default!);
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
        chain.BlockStore.GetReceiptRecoveryBlock(4, chain.Blocks[4].Hash!).Returns((ReceiptRecoveryBlock?)null);

        await chain.RunMigration();

        Assert.That(chain.Read(1), Is.EqualTo(withHashes));
        Assert.That(chain.Read(2), Is.EqualTo(full));
        Assert.That(chain.Read(3), Is.Null);
        Assert.That(chain.Read(4), Is.EqualTo(withoutBody));
    }

    [Test]
    public async Task Waits_until_block_processing_stops_before_a_chunk()
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
    public async Task Does_nothing_when_disabled()
    {
        using Chain chain = new(1);
        byte[] withoutHashes = CompactDecoder.EncodeAsBytes(chain.ReceiptsWithoutTxHashes(1), RlpBehaviors.Storage);
        chain.Store(1, withoutHashes);

        await chain.RunMigration(new ReceiptConfig { ReceiptsTxHashesMigration = false });

        Assert.That(chain.Read(1), Is.EqualTo(withoutHashes));
    }

    private sealed class Chain : System.IDisposable
    {
        public readonly Block[] Blocks;
        public readonly IBlockStore BlockStore = Substitute.For<IBlockStore>();
        public readonly IBlockProcessingQueue ProcessingQueue = Substitute.For<IBlockProcessingQueue>();
        private readonly TestMemColumnsDb<ReceiptsColumns> _receiptsDb = new();
        private readonly IBlockTree _blockTree = Substitute.For<IBlockTree>();
        private readonly IChainLevelInfoRepository _chainLevels = Substitute.For<IChainLevelInfoRepository>();
        private readonly ISyncModeSelector _syncModeSelector = Substitute.For<ISyncModeSelector>();

        public Chain(int length)
        {
            Blocks = new Block[length + 1];
            for (int number = 1; number <= length; number++)
            {
                Block block = Core.Test.Builders.Build.A.Block
                    .WithNumber((ulong)number)
                    .WithTransactions(
                        Core.Test.Builders.Build.A.Transaction.WithNonce((ulong)number).SignedAndResolved().TestObject,
                        Core.Test.Builders.Build.A.Transaction.WithNonce((ulong)number + 100).SignedAndResolved().TestObject)
                    .TestObject;
                Blocks[number] = block;
                BlockStore.GetReceiptRecoveryBlock(block.Number, block.Hash!).Returns(new ReceiptRecoveryBlock(block));
                _chainLevels.LoadLevel(block.Number).Returns(new ChainLevelInfo(true, new BlockInfo(block.Hash!, 0)));
            }

            _blockTree.Head.Returns(Blocks[length]);
            _blockTree.GetLowestBlock().Returns(1UL);
            _syncModeSelector.Current.Returns(SyncMode.WaitingForBlock);
        }

        public TxReceipt[] ReceiptsWithTxHashes(ulong number) =>
            Blocks[number].Transactions
                .Select(static tx => Core.Test.Builders.Build.A.Receipt.WithAllFieldsFilled.WithTransactionHash(tx.Hash).WithCalculatedBloom().TestObject)
                .ToArray();

        public TxReceipt[] ReceiptsWithoutTxHashes(ulong number) =>
            ReceiptsWithTxHashes(number).Select(static r => new TxReceipt(r) { TxHash = null }).ToArray();

        public void Store(ulong number, byte[] value) => _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks).Set(Key(number), value);

        public byte[] Read(ulong number) => _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks).Get(Key(number));

        public Task RunMigration() => RunMigration(new ReceiptConfig());

        public async Task RunMigration(ReceiptConfig config)
        {
            await using ReceiptTxHashesMigration migration = new(
                config,
                _receiptsDb,
                BlockStore,
                _blockTree,
                _chainLevels,
                _syncModeSelector,
                ProcessingQueue,
                LimboLogs.Instance);

            await migration.Run(CancellationToken.None);
            await migration.Completion;
        }

        private byte[] Key(ulong number)
        {
            byte[] key = new byte[40];
            KeyValueStoreExtensions.GetBlockNumPrefixedKey(number, Blocks[number].Hash!, key);
            return key;
        }

        public void Dispose() => _receiptsDb.Dispose();
    }
}
