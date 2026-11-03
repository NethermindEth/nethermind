// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Test.Builders;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Db.LogIndex;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Facade.Filters;
using Nethermind.Logging;
using NUnit.Framework;
using Testably.Abstractions;

namespace Nethermind.Blockchain.Test.Filters;

/// <summary>
/// Intersections over a RocksDB-backed <see cref="LogIndexStorage"/> with postings split into many compressed chunks.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class LogIndexFilterVisitorStorageTests
{
    private const int BlockCount = 6_000;
    private const int BatchSize = 50;

    private static readonly Address SparseAddress = TestItem.AddressA;
    private static readonly Address DenseAddress = TestItem.AddressB;
    private static readonly Address MissingAddress = TestItem.AddressC;
    private static readonly Hash256 DenseTopic = TestItem.KeccakA;
    private static readonly Hash256 SparseTopic = TestItem.KeccakB;

    private TempPath _dbPath = null!;
    private LogIndexStorage _storage = null!;
    private HashSet<int> _sparseAddressBlocks = null!;
    private HashSet<int> _denseAddressBlocks = null!;
    private HashSet<int> _denseTopicBlocks = null!;
    private HashSet<int> _sparseTopicBlocks = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        Random random = new(42);
        _sparseAddressBlocks = RandomBlocks(random, 100);
        _denseAddressBlocks = RandomBlocks(random, 2);
        _denseTopicBlocks = RandomBlocks(random, 1);
        _sparseTopicBlocks = RandomBlocks(random, 30);

        _dbPath = TempPath.GetTempDirectory();
        DbConfig dbConfig = new();
        RocksDbConfigFactory configFactory = new(dbConfig, new PruningConfig(), new TestHardwareInfo(0), LimboLogs.Instance);
        RocksDbFactory dbFactory = new(configFactory, dbConfig, new HyperClockCacheWrapper(), new RealFileSystem(), LimboLogs.Instance, _dbPath.Path);

        _storage = new LogIndexStorage(dbFactory, LimboLogs.Instance, new LogIndexConfig
        {
            Enabled = true,
            CompressionDistance = 16,
            MaxReorgDepth = 200
        });

        // Merging after each batch compresses it into its own chunk, so postings span many chunks
        for (int start = BlockCount / 2; start < BlockCount; start += BatchSize)
        {
            await AddAsync(start, descending: false);
            await _storage.CompactAsync(mergeIterations: 1);
        }

        for (int end = BlockCount / 2; end > 0; end -= BatchSize)
        {
            await AddAsync(end - BatchSize, descending: true);
            await _storage.CompactAsync(mergeIterations: 1);
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        try
        {
            await _storage.StopAsync();
        }
        finally
        {
            await ((IAsyncDisposable)_storage).DisposeAsync();
        }

        _dbPath.Dispose();
    }

    [Test]
    public void Intersection_matches_reference(
        [Values("sparse-dense", "dense-dense", "sparse-sparse", "missing-dense", "dense-dense-sparse", "two-addresses-dense", "sparse-or-topics-sparse")] string query,
        [Values(0, 777, BlockCount / 2 - 5)] int from,
        [Values(BlockCount / 2 + 5, 4_321, BlockCount - 1)] int to)
    {
        (LogFilter Filter, IEnumerable<int> Blocks) testCase = query switch
        {
            "sparse-dense" => (Filter(SparseAddress, DenseTopic), _sparseAddressBlocks.Intersect(_denseTopicBlocks)),
            "dense-dense" => (Filter(DenseAddress, DenseTopic), _denseAddressBlocks.Intersect(_denseTopicBlocks)),
            "sparse-sparse" => (Filter(SparseAddress, null, SparseTopic), _sparseAddressBlocks.Intersect(_sparseTopicBlocks)),
            "missing-dense" => (Filter(MissingAddress, DenseTopic), []),
            "dense-dense-sparse" => (Filter(DenseAddress, DenseTopic, SparseTopic),
                _denseAddressBlocks.Intersect(_denseTopicBlocks).Intersect(_sparseTopicBlocks)),
            "two-addresses-dense" => (FilterBuilder.New()
                    .WithAddresses(SparseAddress, MissingAddress)
                    .WithTopicExpressions(TestTopicExpressions.Specific(DenseTopic))
                    .Build(),
                _sparseAddressBlocks.Intersect(_denseTopicBlocks)),
            "sparse-or-topics-sparse" => (FilterBuilder.New()
                    .WithAddress(SparseAddress)
                    .WithTopicExpressions(TestTopicExpressions.Or(DenseTopic, TestItem.KeccakE), TestTopicExpressions.Specific(SparseTopic))
                    .Build(),
                _sparseAddressBlocks.Intersect(_denseTopicBlocks).Intersect(_sparseTopicBlocks)),
            _ => throw new ArgumentOutOfRangeException(nameof(query))
        };

        long[] expected = testCase.Blocks.Where(b => b >= from && b <= to).Order().Select(static b => (long)b).ToArray();

        Assert.That(_storage.EnumerateBlockNumbersFor(testCase.Filter, (ulong)from, (ulong)to).ToArray(), Is.EqualTo(expected));
    }

    private static LogFilter Filter(Address address, Hash256? topic0, Hash256? topic1 = null) => FilterBuilder.New()
        .WithAddress(address)
        .WithTopicExpressions(
            topic0 is null ? TestTopicExpressions.Any : TestTopicExpressions.Specific(topic0),
            topic1 is null ? TestTopicExpressions.Any : TestTopicExpressions.Specific(topic1))
        .Build();

    private static HashSet<int> RandomBlocks(Random random, int oneIn) =>
        [.. Enumerable.Range(0, BlockCount).Where(_ => random.Next(oneIn) == 0)];

    private Task AddAsync(int start, bool descending)
    {
        BlockReceipts[] batch = new BlockReceipts[BatchSize];
        for (int i = 0; i < BatchSize; i++)
        {
            int block = descending ? start + BatchSize - 1 - i : start + i;
            List<LogEntry> logs = [Build.A.LogEntry.WithAddress(TestItem.AddressD).TestObject];
            if (_sparseAddressBlocks.Contains(block)) logs.Add(Build.A.LogEntry.WithAddress(SparseAddress).TestObject);
            if (_denseAddressBlocks.Contains(block)) logs.Add(Build.A.LogEntry.WithAddress(DenseAddress).TestObject);
            if (_denseTopicBlocks.Contains(block)) logs.Add(Build.A.LogEntry.WithAddress(TestItem.AddressD).WithTopics(DenseTopic).TestObject);
            if (_sparseTopicBlocks.Contains(block)) logs.Add(Build.A.LogEntry.WithAddress(TestItem.AddressD).WithTopics(TestItem.KeccakC, SparseTopic).TestObject);

            batch[i] = new(block, [Build.A.Receipt.WithLogs([.. logs]).TestObject]);
        }

        return _storage.AddReceiptsAsync(_storage.Aggregate(batch, descending, null), null);
    }
}
