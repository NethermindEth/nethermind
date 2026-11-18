// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.LogIndex;
using Nethermind.Facade.Find;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Benchmarks.Store;

/// <summary>
/// Builds the log index backward over stored blocks through <see cref="LogIndexBuilder"/>: reading the receipts of each block
/// and aggregating its log addresses and topics. Writing the aggregates is left out.
/// </summary>
/// <remarks>The chain is longer than the receipts cache, so no block is served from it.</remarks>
[MemoryDiagnoser]
public class LogIndexBuildBenchmark
{
    private const int Blocks = 160;
    private const int TransactionsPerBlock = 200;
    private const int LogsPerTransaction = 2;
    private const int TopicsPerLog = 3;

    private ISpecProvider _specProvider;
    private IBlockTree _blockTree;
    private IBlockStore _blockStore;
    private TestMemColumnsDb<ReceiptsColumns> _receiptsDb;
    private LogIndexStorage _aggregator;
    private PersistentReceiptStorage _receiptStorage;

    [GlobalSetup]
    public void Setup()
    {
        _specProvider = new TestSpecProvider(Prague.Instance);
        BlockTreeBuilder builder = Build.A.BlockTree().WithSpecProvider(_specProvider);
        _blockTree = builder.TestObject;
        _blockStore = builder.BlockStore;
        _receiptsDb = new TestMemColumnsDb<ReceiptsColumns>();
        PersistentReceiptStorage writer = CreateReceiptStorage();

        Random random = new(42);
        Address[] contracts = CreatePool(64, static bytes => new Address(bytes[..Address.Size]), random);
        Hash256[] events = CreatePool(16, static bytes => new Hash256(bytes), random);

        Block previous = Build.A.Block.Genesis.TestObject;
        BlockTreeBuilder.AddBlock(_blockTree, previous);
        ulong nonce = 0;
        for (int b = 0; b < Blocks; b++)
        {
            Transaction[] transactions = new Transaction[TransactionsPerBlock];
            TxReceipt[] receipts = new TxReceipt[TransactionsPerBlock];
            for (int i = 0; i < transactions.Length; i++)
            {
                transactions[i] = Build.A.Transaction.WithType(TxType.EIP1559).WithChainId(_specProvider.ChainId).WithNonce(nonce++)
                    .WithGasLimit(100_000).WithMaxFeePerGas(30 * Unit.GWei).WithMaxPriorityFeePerGas(Unit.GWei)
                    .WithTo(contracts[i % contracts.Length]).WithData(new byte[68])
                    .SignedAndResolved(TestItem.PrivateKeyA).TestObject;

                LogEntry[] logs = new LogEntry[LogsPerTransaction];
                for (int l = 0; l < logs.Length; l++)
                {
                    Hash256[] topics = new Hash256[TopicsPerLog];
                    topics[0] = events[random.Next(events.Length)];
                    for (int t = 1; t < topics.Length; t++)
                        topics[t] = RandomTopic(random);

                    logs[l] = new LogEntry(contracts[random.Next(contracts.Length)], new byte[64], topics);
                }

                receipts[i] = Build.A.Receipt.WithTransactionHash(transactions[i].Hash).WithSender(TestItem.AddressA)
                    .WithLogs(logs).WithGasUsedTotal(50_000UL * (ulong)(i + 1)).TestObject;
            }

            previous = Build.A.Block.WithParent(previous).WithBaseFeePerGas(Unit.GWei).WithReceiptsRoot(TestItem.KeccakA)
                .WithTransactions(transactions).TestObject;
            BlockTreeBuilder.AddBlock(_blockTree, previous);
            writer.Insert(previous, receipts);
        }

        _blockTree.SyncPivot = (previous.Number, previous.Hash!);
        _aggregator = new LogIndexStorage(new MemDbFactory(), LimboLogs.Instance, CreateConfig());
    }

    [IterationSetup]
    public void IterationSetup()
    {
        ((IClearableCache)_blockStore).ClearCache();
        _receiptStorage = CreateReceiptStorage();
    }

    [GlobalCleanup]
    public void Cleanup() => ((ILogIndexStorage)_aggregator).DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Benchmark(OperationsPerInvoke = Blocks)]
    public async Task Backward()
    {
        IndexedRange storage = new(_aggregator);
        LogIndexBuilder builder = new(storage, CreateConfig(), _blockTree, new SyncConfig(), _receiptStorage, LimboLogs.Instance, new MemDb());
        await builder.StartAsync();
        await builder.BackwardSyncCompletion;
        await builder.StopAsync();
        await builder.DisposeAsync();
    }

    private static LogIndexConfig CreateConfig() => new()
    {
        Enabled = true,
        MaxReorgDepth = 0,
        MaxReceiptsParallelism = 1,
        MaxAggregationParallelism = 1,
        CompressionDistance = 0,
        CompactionDistance = 0
    };

    private PersistentReceiptStorage CreateReceiptStorage() =>
        new(_receiptsDb, _specProvider, new ReceiptsRecovery(new EthereumEcdsa(_specProvider.ChainId), _specProvider), _blockTree, _blockStore, new ReceiptConfig());

    private static T[] CreatePool<T>(int count, Func<byte[], T> create, Random random)
    {
        T[] pool = new T[count];
        byte[] bytes = new byte[Hash256.Size];
        for (int i = 0; i < pool.Length; i++)
        {
            random.NextBytes(bytes);
            pool[i] = create(bytes);
        }

        return pool;
    }

    private static Hash256 RandomTopic(Random random)
    {
        byte[] bytes = new byte[Hash256.Size];
        random.NextBytes(bytes.AsSpan(12));
        return new Hash256(bytes);
    }

    /// <summary>Aggregates through the real storage and only records the indexed range.</summary>
    private sealed class IndexedRange(LogIndexStorage aggregator) : ILogIndexStorage
    {
        public bool Enabled => true;
        public int? MaxBlockNumber { get; private set; }
        public int? MinBlockNumber { get; private set; }

        public IEnumerator<int> GetEnumerator(Address address, int from, int to) => throw new NotSupportedException();
        public IEnumerator<int> GetEnumerator(int topicIndex, Hash256 topic, int from, int to) => throw new NotSupportedException();

        public LogIndexAggregate Aggregate(IReadOnlyList<BlockReceipts> batch, bool isBackwardSync, LogIndexUpdateStats stats = null) =>
            aggregator.Aggregate(batch, isBackwardSync, stats);

        public Task AddReceiptsAsync(LogIndexAggregate aggregate, LogIndexUpdateStats stats = null)
        {
            int min = Math.Min(aggregate.FirstBlockNum, aggregate.LastBlockNum);
            int max = Math.Max(aggregate.FirstBlockNum, aggregate.LastBlockNum);
            MinBlockNumber = MinBlockNumber is { } currentMin ? Math.Min(currentMin, min) : min;
            MaxBlockNumber = MaxBlockNumber is { } currentMax ? Math.Max(currentMax, max) : max;
            return Task.CompletedTask;
        }

        public Task RemoveReorgedAsync(BlockReceipts block) => throw new NotSupportedException();
        public string GetDbSize() => string.Empty;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
