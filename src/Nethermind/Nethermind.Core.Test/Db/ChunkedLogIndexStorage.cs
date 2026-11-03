// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Db.LogIndex;
using Nethermind.Db.Rocks;

namespace Nethermind.Core.Test.Db;

/// <summary>
/// A RocksDB-backed <see cref="ILogIndexStorage"/> from the production modules in a temporary directory,
/// filled so that the postings of each key span many compressed chunks.
/// </summary>
public sealed class ChunkedLogIndexStorage : IAsyncDisposable
{
    private readonly TempPath _dbPath;
    private readonly IContainer _container;
    private readonly LogIndexStorage _storage;

    private ChunkedLogIndexStorage(TempPath dbPath, IContainer container)
    {
        _dbPath = dbPath;
        _container = container;
        _storage = (LogIndexStorage)container.Resolve<ILogIndexStorage>();
    }

    public ILogIndexStorage Storage => _storage;

    /// <summary>
    /// Indexes blocks from 0 to <paramref name="blockCount"/> - 1, forward from the middle and then backward to the start,
    /// so postings use backward, finalized and forward keys. Each batch of <paramref name="batchSize"/> blocks is merged into its own chunk.
    /// </summary>
    /// <param name="logsAt">Returns the logs of one block.</param>
    public static async Task<ChunkedLogIndexStorage> CreateAsync(int blockCount, int batchSize, Func<int, LogEntry[]> logsAt)
    {
        TempPath dbPath = TempPath.GetTempDirectory();
        IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(
                new InitConfig { BaseDbPath = dbPath.Path },
                new LogIndexConfig { Enabled = true, CompressionDistance = 16, MaxReorgDepth = 200 }))
            .AddSingleton<IDbFactory, RocksDbFactory>()
            .Build();

        ChunkedLogIndexStorage index = new(dbPath, container);
        try
        {
            for (int start = blockCount / 2; start < blockCount; start += batchSize)
                await index.AddBatchAsync(start, Math.Min(batchSize, blockCount - start), logsAt, isBackwardSync: false);

            for (int end = blockCount / 2; end > 0; end -= batchSize)
            {
                int start = Math.Max(0, end - batchSize);
                await index.AddBatchAsync(start, end - start, logsAt, isBackwardSync: true);
            }
        }
        catch
        {
            await index.DisposeAsync();
            throw;
        }

        return index;
    }

    private async Task AddBatchAsync(int start, int count, Func<int, LogEntry[]> logsAt, bool isBackwardSync)
    {
        BlockReceipts[] batch = new BlockReceipts[count];
        for (int i = 0; i < count; i++)
        {
            int block = isBackwardSync ? start + count - 1 - i : start + i;
            batch[i] = new(block, [Build.A.Receipt.WithLogs(logsAt(block)).TestObject]);
        }

        await _storage.AddReceiptsAsync(_storage.Aggregate(batch, isBackwardSync, null), null);
        await _storage.CompactAsync(mergeIterations: 1);
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
        _dbPath.Dispose();
    }
}
