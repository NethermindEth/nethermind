// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Test.Builders;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.LogIndex;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Facade.Filters;
using Nethermind.Facade.Find;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.State.Proofs;
using NSubstitute;
using NUnit.Framework;
using Testably.Abstractions;

namespace Nethermind.Blockchain.Test.Find;

public class LogIndexReorgTests
{
    private const int MaxReorgDepth = 4;
    private const int ChainALength = 30;
    private const int ChainBLength = 40;
    private const int CatchUpTimeoutMs = 60_000;

    public enum Finality
    {
        None,
        Canonical,
        NotCanonicalDuringReorg
    }

    [TestCase(10, Finality.Canonical, ChainALength, false, TestName = "Reorg deeper than the max reorg depth on a finalizing chain")]
    [TestCase(ChainALength - 2, Finality.None, ChainALength, false, TestName = "Side branch known before the head moves to it")]
    [TestCase(ChainALength - 7, Finality.None, 10, false, TestName = "Reorg within the revertable depth of the index on a chain without finality")]
    [TestCase(ChainALength - 7, Finality.NotCanonicalDuringReorg, 10, false, TestName = "Reorg of blocks indexed while the finalized block was not canonical")]
    [TestCase(10, Finality.None, 10, true, TestName = "Reorg below the revertable depth of the index on a chain without finality")]
    public async Task Indexed_logs_match_a_full_scan_after_a_reorg(int forkPoint, Finality finality, int startHead, bool beyondRevertableDepth)
    {
        InMemoryReceiptStorage receipts = new();
        BlockTree tree = Build.A.BlockTree().OfChainLength(1).TestObject;

        List<Block> chainA = Extend(tree, tree.FindBlock(0, BlockTreeLookupOptions.None)!, ChainALength, TestItem.AddressA, 0, receipts);
        MoveHead(tree, chainA[..startHead], receipts);
        tree.SyncPivot = (tree.Head!.Number, tree.Head.Hash!);
        if (finality is not Finality.None)
            Finalize(tree, chainA[forkPoint - 1]);

        using TempPath dbPath = TempPath.GetTempDirectory();
        using HyperClockCacheWrapper cache = new();
        RocksDbFactory dbFactory = CreateDbFactory(dbPath, cache);

        LogIndexConfig config = new() { Enabled = true, MaxReorgDepth = MaxReorgDepth, MaxBatchSize = 4 };
        LogIndexStorage storage = new(dbFactory, LimboLogs.Instance, config);
        TestErrorLogManager logManager = new();
        LogIndexBuilder builder = new(storage, config, tree, new SyncConfig { FastSync = false }, receipts, logManager, new MemDb());
        try
        {
            await builder.StartAsync();
            await builder.BackwardSyncCompletion.WaitAsync(TimeSpan.FromMilliseconds(CatchUpTimeoutMs));
            WaitForIndexToCatchUp(builder, storage);

            if (startHead < ChainALength)
            {
                MoveHead(tree, chainA, receipts);
                WaitForIndexToCatchUp(builder, storage);
            }

            List<Block> chainB = Extend(tree, tree.FindBlock((ulong)forkPoint, BlockTreeLookupOptions.None)!, ChainBLength, TestItem.AddressB, 7, receipts);
            if (finality is Finality.NotCanonicalDuringReorg)
                Finalize(tree, chainB[^MaxReorgDepth]);
            WaitForIndexToCatchUp(builder, storage);

            MoveHead(tree, chainB, receipts);
            if (finality is not Finality.None)
                Finalize(tree, chainB[^MaxReorgDepth]);

            if (beyondRevertableDepth)
            {
                Assert.That(() => builder.LastError, Is.TypeOf<LogIndexStateException>().After(CatchUpTimeoutMs, 20));
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(storage.MaxBlockNumber, Is.Null, "the index must not be used once a reorg could not be reverted");
                    Assert.That(logManager.Errors.Select(static e => e.Exception), Has.Some.TypeOf<LogIndexStateException>());
                }
            }
            else
            {
                WaitForIndexToCatchUp(builder, storage);
            }

            AssertIndexedLogsMatchScan(tree, receipts, storage);
        }
        finally
        {
            await builder.StopAsync();
            await builder.DisposeAsync();
        }
    }

    [Test]
    public async Task Indexed_logs_match_a_full_scan_after_a_deeper_reorg_follows_an_unrevertable_one()
    {
        const int chainLength = 60;
        const int firstFork = 45;
        const int secondFork = 40;
        InMemoryReceiptStorage receipts = new();
        BlockTree tree = Build.A.BlockTree().OfChainLength(1).TestObject;

        List<Block> chainA = Extend(tree, tree.FindBlock(0, BlockTreeLookupOptions.None)!, chainLength, TestItem.AddressA, 0, receipts);
        MoveHead(tree, chainA[..10], receipts);
        tree.SyncPivot = (tree.Head!.Number, tree.Head.Hash!);

        using TempPath dbPath = TempPath.GetTempDirectory();
        using HyperClockCacheWrapper cache = new();
        LogIndexConfig config = new() { Enabled = true, MaxReorgDepth = MaxReorgDepth, MaxBatchSize = 4 };
        LogIndexStorage storage = new(CreateDbFactory(dbPath, cache), LimboLogs.Instance, config);
        LogIndexBuilder builder = new(storage, config, tree, new SyncConfig { FastSync = false }, receipts, LimboLogs.Instance, new MemDb());
        try
        {
            await builder.StartAsync();
            await builder.BackwardSyncCompletion.WaitAsync(TimeSpan.FromMilliseconds(CatchUpTimeoutMs));
            MoveHead(tree, chainA, receipts);
            WaitForIndexToCatchUp(builder, storage);

            MoveHead(tree, Extend(tree, chainA[firstFork - 1], chainLength + 10, TestItem.AddressB, 7, receipts), receipts);
            Assert.That(() => builder.LastError, Is.TypeOf<LogIndexStateException>().After(CatchUpTimeoutMs, 20));

            // The stopped builder does not revert this reorg, so the index keeps chain A below the first fork
            MoveHead(tree, Extend(tree, chainA[secondFork - 1], chainLength + 20, TestItem.AddressC, 9, receipts, TestItem.KeccakB), receipts);
            AssertIndexedLogsMatchScan(tree, receipts, storage);
        }
        finally
        {
            await builder.StopAsync();
            await builder.DisposeAsync();
        }

        await using LogIndexStorage reopened = new(CreateDbFactory(dbPath, cache), LimboLogs.Instance, config);
        AssertIndexedLogsMatchScan(tree, receipts, reopened);
    }

    [Test]
    public async Task Reorg_of_a_queued_batch_after_a_revert_is_reverted_once_the_batch_is_written()
    {
        InMemoryReceiptStorage receipts = new();
        BlockTree tree = Build.A.BlockTree().OfChainLength(1).TestObject;

        List<Block> chainA = Extend(tree, tree.FindBlock(0, BlockTreeLookupOptions.None)!, ChainALength, TestItem.AddressA, 0, receipts);
        MoveHead(tree, chainA, receipts);
        tree.SyncPivot = (tree.Head!.Number, tree.Head.Hash!);

        // The pivot is 26 and the forward walk starts at 23, as deep below it as a revert reaches. Reverting chain A from block 24 brings it back to the pivot after queueing blocks 24-25 of chain B.
        const int pivot = ChainALength - MaxReorgDepth;
        List<Block> chainB = Extend(tree, chainA[pivot - 4], pivot + MaxReorgDepth - 1, TestItem.AddressB, 7, receipts);
        List<Block> chainC = Extend(tree, chainB[0], ChainBLength, TestItem.AddressB, 9, receipts);
        HashSet<Hash256> chainCHashes = chainC.Select(static b => b.Hash!).ToHashSet();

        using ManualResetEventSlim writeGate = new(true);
        using ManualResetEventSlim chainCApplied = new(false);
        using ManualResetEventSlim chainCRead = new(false);
        int chainBBlock25Reads = 0;
        ObservedReceiptStorage observedReceipts = new(receipts, block =>
        {
            // The first read is from reverting the blocks chain B replaced, the second from queueing chain B
            if (block.Hash == chainB[1].Hash && Interlocked.Increment(ref chainBBlock25Reads) == 2)
            {
                writeGate.Reset();
                MoveHead(tree, chainC, receipts);
                chainCApplied.Set();
            }
            else if (chainCApplied.IsSet && chainCHashes.Contains(block.Hash!))
            {
                chainCRead.Set();
            }

            return receipts.Get(block);
        });

        using TempPath dbPath = TempPath.GetTempDirectory();
        using HyperClockCacheWrapper cache = new();
        LogIndexConfig config = new() { Enabled = true, MaxReorgDepth = MaxReorgDepth, MaxBatchSize = 4 };
        GatedLogIndexStorage storage = new(CreateDbFactory(dbPath, cache), config, writeGate);
        LogIndexBuilder builder = new(storage, config, tree, new SyncConfig { FastSync = false }, observedReceipts, LimboLogs.Instance, new MemDb());
        try
        {
            await builder.StartAsync();
            await builder.BackwardSyncCompletion.WaitAsync(TimeSpan.FromMilliseconds(CatchUpTimeoutMs));
            WaitForIndexToCatchUp(builder, storage);

            MoveHead(tree, chainB, receipts);
            Assert.That(chainCApplied.Wait(CatchUpTimeoutMs), Is.True);

            // Chain C replaces block 25 of the batch held by the gate. Without draining, the walk moves on to chain C
            // and reads it; with draining, it waits for the batch, so the gate opens after a grace period.
            chainCRead.Wait(TimeSpan.FromSeconds(1));
            writeGate.Set();

            WaitForIndexToCatchUp(builder, storage);
            Assert.That(builder.LastError, Is.Null);
            AssertIndexedLogsMatchScan(tree, receipts, storage);
        }
        finally
        {
            writeGate.Set();
            await builder.StopAsync();
            await builder.DisposeAsync();
        }
    }

    [Test]
    public async Task Reorg_seen_before_a_restart_is_reverted_after_it()
    {
        InMemoryReceiptStorage receipts = new();
        BlockTree tree = Build.A.BlockTree().OfChainLength(1).TestObject;

        List<Block> chainA = Extend(tree, tree.FindBlock(0, BlockTreeLookupOptions.None)!, ChainALength, TestItem.AddressA, 0, receipts);
        MoveHead(tree, chainA[..^1], receipts);
        tree.SyncPivot = (tree.Head!.Number, tree.Head.Hash!);

        // The index reaches block 25, the head minus the max reorg depth, and chain B replaces blocks 23-25, within the revertable depth
        const int indexedMax = ChainALength - 1 - MaxReorgDepth;
        List<Block> chainB = Extend(tree, chainA[indexedMax - 4], ChainBLength, TestItem.AddressB, 7, receipts);
        Block nextToIndex = chainA[indexedMax];

        using TempPath dbPath = TempPath.GetTempDirectory();
        using HyperClockCacheWrapper cache = new();
        LogIndexConfig config = new() { Enabled = true, MaxReorgDepth = MaxReorgDepth, MaxBatchSize = 4 };
        MemDb metadataDb = new();

        LogIndexBuilder? builder = null;
        Task? stopping = null;
        ObservedReceiptStorage observedReceipts = new(receipts, block =>
        {
            // Chain B becomes canonical while the walk reads the next block, and the builder stops before it reverts the replaced blocks
            if (block.Hash == nextToIndex.Hash && stopping is null)
            {
                MoveHead(tree, chainB, receipts);
                stopping = builder!.StopAsync();
                SpinWait.SpinUntil(() => !builder.IsRunning, CatchUpTimeoutMs);
            }

            return receipts.Get(block);
        });

        LogIndexStorage storage = new(CreateDbFactory(dbPath, cache), LimboLogs.Instance, config);
        builder = new(storage, config, tree, new SyncConfig { FastSync = false }, observedReceipts, LimboLogs.Instance, metadataDb);
        try
        {
            await builder.StartAsync();
            await builder.BackwardSyncCompletion.WaitAsync(TimeSpan.FromMilliseconds(CatchUpTimeoutMs));
            WaitForIndexToCatchUp(builder, storage);

            MoveHead(tree, chainA, receipts);
            Assert.That(() => stopping, Is.Not.Null.After(CatchUpTimeoutMs, 20));
            await stopping!;
        }
        finally
        {
            if (stopping is null)
                await builder.StopAsync();
            await builder.DisposeAsync();
        }

        LogIndexStorage reopened = new(CreateDbFactory(dbPath, cache), LimboLogs.Instance, config);
        LogIndexBuilder restarted = new(reopened, config, tree, new SyncConfig { FastSync = false }, receipts, LimboLogs.Instance, metadataDb);
        try
        {
            Assert.That(reopened.MaxBlockNumber, Is.EqualTo(indexedMax), "the first run must stop before reverting");

            await restarted.StartAsync();
            WaitForIndexToCatchUp(restarted, reopened);

            Assert.That(restarted.LastError, Is.Null);
            AssertIndexedLogsMatchScan(tree, receipts, reopened);
            Assert.That(metadataDb.KeyExists(MetadataDbKeys.LogIndexLowestReplacedBlock), Is.False, "a reverted block must not be reverted again after the next restart");
        }
        finally
        {
            await restarted.StopAsync();
            await restarted.DisposeAsync();
        }
    }

    private static RocksDbFactory CreateDbFactory(TempPath dbPath, HyperClockCacheWrapper cache)
    {
        Directory.CreateDirectory(dbPath.Path);
        DbConfig dbConfig = new();
        RocksDbConfigFactory configFactory = new(dbConfig, new PruningConfig(), new TestHardwareInfo(0), LimboLogs.Instance);
        return new(configFactory, dbConfig, cache, new RealFileSystem(), LimboLogs.Instance, dbPath.Path);
    }

    private static void MoveHead(BlockTree tree, IReadOnlyList<Block> chain, InMemoryReceiptStorage receipts)
    {
        tree.TryUpdateMainChain(chain[^1].Header, wereProcessed: true, forceUpdateHeadBlock: true, preloadedBlocks: chain.ToArray());
        foreach (Block block in chain)
            receipts.EnsureCanonical(block);
        Assert.That(tree.Head!.Hash, Is.EqualTo(chain[^1].Hash));
    }

    private static void Finalize(BlockTree tree, Block block) => tree.ForkChoiceUpdated(block.Hash, block.Hash);

    private static void WaitForIndexToCatchUp(LogIndexBuilder builder, LogIndexStorage storage) =>
        Assert.That(() => storage.MaxBlockNumber >= (int)builder.MaxTargetBlockNumber, Is.True.After(CatchUpTimeoutMs, 20));

    private static void AssertIndexedLogsMatchScan(BlockTree tree, InMemoryReceiptStorage receipts, LogIndexStorage storage)
    {
        IReceiptsRecovery recovery = Substitute.For<IReceiptsRecovery>();
        IndexedLogFinder indexed = new(tree, receipts, receipts, LimboLogs.Instance, recovery, MainnetSpecProvider.Instance, storage);
        LogFinder scan = new(tree, receipts, receipts, LimboLogs.Instance, recovery, MainnetSpecProvider.Instance);
        BlockHeader from = tree.FindHeader(0, BlockTreeLookupOptions.None)!;
        BlockHeader to = tree.Head!.Header;

        using (Assert.EnterMultipleScope())
        {
            foreach (Address address in new[] { TestItem.AddressA, TestItem.AddressB, TestItem.AddressC })
                AssertMatch(FilterBuilder.New().FromBlock(0UL).ToBlock(to.Number).WithAddress(address).Build(), $"logs of {address}");

            foreach (Hash256 topic in new[] { TestItem.KeccakA, TestItem.KeccakB })
                AssertMatch(FilterBuilder.New().FromBlock(0UL).ToBlock(to.Number).WithTopicExpressions(TestTopicExpressions.Specific(topic)).Build(), $"logs with {topic}");
        }

        void AssertMatch(LogFilter filter, string message) =>
            Assert.That(indexed.FindLogs(filter, from, to).Select(static l => l.BlockNumber).ToArray(),
                Is.EqualTo(scan.FindLogs(filter, from, to).Select(static l => l.BlockNumber).ToArray()), message);
    }

    private static List<Block> Extend(BlockTree tree, Block from, int toNumber, Address emitter, ulong variant, InMemoryReceiptStorage receipts, Hash256? topic = null)
    {
        List<Block> blocks = [];
        Block parent = from;
        while ((int)parent.Number < toNumber)
        {
            parent = CreateBlock(parent, emitter, variant, receipts, topic ?? TestItem.KeccakA);
            Assert.That(tree.SuggestBlock(parent), Is.EqualTo(AddBlockResult.Added));
            blocks.Add(parent);
        }

        return blocks;
    }

    private static Block CreateBlock(Block parent, Address emitter, ulong variant, InMemoryReceiptStorage receipts, Hash256 topic)
    {
        Transaction tx = Build.A.Transaction
            .WithValue(1)
            .WithNonce(parent.Number)
            .WithData(Rlp.Encode((long)(parent.Number * 1000 + variant)))
            .Signed(new EthereumEcdsa(MainnetSpecProvider.Instance.ChainId), TestItem.PrivateKeyA)
            .TestObject;

        Block block = Build.A.Block
            .WithParent(parent)
            .WithDifficulty(1_000_000 + variant)
            .WithTransactions(tx)
            .WithBloom(new Bloom())
            .TestObject;

        LogEntry[] logs = [Build.A.LogEntry.WithAddress(emitter).WithTopics(topic).TestObject];
        TxReceipt receipt = new() { Logs = logs, TxHash = tx.Hash, Bloom = new Bloom(logs), BlockNumber = block.Number };
        block.Bloom!.Add(receipt.Logs);
        block.Header.TxRoot = TxTrie.CalculateRoot(block.Transactions);
        block.Header.ReceiptsRoot = ReceiptTrie.CalculateRoot(((ISpecProvider)MainnetSpecProvider.Instance).GetSpec(block.Header), [receipt], new ReceiptMessageDecoder());
        block.Header.Hash = block.CalculateHash();
        receipt.BlockHash = block.Hash;
        receipts.Insert(block, [receipt], ensureCanonical: false);
        return block;
    }

    private sealed class GatedLogIndexStorage(IDbFactory dbFactory, ILogIndexConfig config, ManualResetEventSlim writeGate)
        : LogIndexStorage(dbFactory, LimboLogs.Instance, config)
    {
        protected override void MergeBlockNumbers(IWriteBatch dbBatch, ReadOnlySpan<byte> key, List<int> numbers, bool isBackwardSync, LogIndexUpdateStats? stats)
        {
            writeGate.Wait(CatchUpTimeoutMs);
            base.MergeBlockNumbers(dbBatch, key, numbers, isBackwardSync, stats);
        }
    }

    /// <summary>Reads receipts from <paramref name="inner"/> through <paramref name="onRead"/>, so a test can act when a block is read.</summary>
    /// <remarks>It offers no receipts iterator, so the builder reads every block with <see cref="Get(Block, bool, bool)"/>. NSubstitute cannot proxy the iterator's ref struct out parameter.</remarks>
    private sealed class ObservedReceiptStorage(IReceiptStorage inner, Func<Block, TxReceipt[]> onRead) : IReceiptStorage
    {
        public ulong MigratedBlockNumber { get => inner.MigratedBlockNumber; set => inner.MigratedBlockNumber = value; }

        public event EventHandler<BlockReplacementEventArgs>? NewCanonicalReceipts { add { } remove { } }
        public event EventHandler<ReceiptsEventArgs>? ReceiptsInserted { add { } remove { } }

        public TxReceipt[] Get(Block block, bool recover = true, bool recoverSender = true) => onRead(block);
        public TxReceipt[] Get(Hash256 blockHash, bool recover = true) => inner.Get(blockHash, recover);
        public Hash256? FindBlockHash(Hash256 txHash) => inner.FindBlockHash(txHash);
        public bool CanGetReceiptsByHash(ulong blockNumber) => inner.CanGetReceiptsByHash(blockNumber);

        public bool TryGetReceiptsIterator(ulong blockNumber, Hash256 blockHash, out ReceiptsIterator iterator)
        {
            iterator = new ReceiptsIterator();
            return false;
        }

        public void Insert(Block block, TxReceipt[]? txReceipts, bool ensureCanonical, WriteFlags writeFlags = WriteFlags.None, ulong? lastBlockNumber = null) =>
            inner.Insert(block, txReceipts, ensureCanonical, writeFlags, lastBlockNumber);
        public void Insert(Block block, TxReceipt[]? txReceipts, IReleaseSpec spec, bool ensureCanonical, WriteFlags writeFlags = WriteFlags.None, ulong? lastBlockNumber = null) =>
            inner.Insert(block, txReceipts, spec, ensureCanonical, writeFlags, lastBlockNumber);
        public bool HasBlock(ulong blockNumber, Hash256 hash) => inner.HasBlock(blockNumber, hash);
        public void EnsureCanonical(Block block) => inner.EnsureCanonical(block);
        public void RemoveReceipts(Block block) => inner.RemoveReceipts(block);
        public void RemoveReceipts(ulong blockNumber, Hash256 blockHash) => inner.RemoveReceipts(blockNumber, blockHash);
    }
}
