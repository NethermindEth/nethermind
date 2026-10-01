// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Channels;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Flat.Persistence.TrieNodeLog;

/// <summary>
/// <see cref="ITrieNodeLog"/> made of independent <see cref="TrieNodeLogShard"/>s: three partitions
/// — state-top (<c>StateTopNodes</c>), state (<c>StateNodes</c>, state entries of <c>FallbackNodes</c>) and
/// storage (<c>StorageNodes</c>, storage entries of <c>FallbackNodes</c>) — each with its own byte budget and
/// shard count, sharded by the first byte of the column key. A batch's records are staged per shard and appended by one worker per shard, and the
/// shards are made durable and merged in parallel.
/// </summary>
public sealed class TrieNodeLog : ITrieNodeLog, IAsyncDisposable
{
    private const int StagingChunkSize = 1024 * 1024;
    private const int StagingQueueDepth = 4;
    private const int StagedRecordHeaderLength = 1 + 1 + 1 + 4; // column, delete flag, key length, value length

    private readonly int[] _partitionOffset = new int[PartitionCount]; // index of a partition's first shard
    private readonly int[] _partitionShift = new int[PartitionCount]; // right shift of the shard byte selecting the shard
    private readonly TrieNodeLogShard[] _shards; // partition-major: state_top, state, storage
    private readonly SemaphoreSlim _mergeLimiter;
    private readonly ILogger _logger;

    public TrieNodeLog(string basePath, IColumnsDb<FlatDbColumns> db, IFlatDbConfig config, ILogManager logManager)
    {
        if (config.TrieNodeLogMaxConcurrentMerges < 1)
            throw new InvalidConfigurationException($"{nameof(IFlatDbConfig.TrieNodeLogMaxConcurrentMerges)} must be at least 1, got {config.TrieNodeLogMaxConcurrentMerges}", -1);
        if (config.TrieNodeLogMergeBacklogMargin < 1)
            throw new InvalidConfigurationException($"{nameof(IFlatDbConfig.TrieNodeLogMergeBacklogMargin)} must be at least 1, got {config.TrieNodeLogMergeBacklogMargin}", -1);
        _mergeLimiter = new SemaphoreSlim(config.TrieNodeLogMaxConcurrentMerges, config.TrieNodeLogMaxConcurrentMerges);
        _logger = logManager.GetClassLogger<TrieNodeLog>();

        ReadOnlySpan<(string Name, long Budget, int ShardCount, string ShardCountSetting)> partitions =
        [
            ("state_top", config.TrieNodeLogStateTopBytes, config.TrieNodeLogStateTopShardCount, nameof(IFlatDbConfig.TrieNodeLogStateTopShardCount)),
            ("state", config.TrieNodeLogStateBytes, config.TrieNodeLogStateShardCount, nameof(IFlatDbConfig.TrieNodeLogStateShardCount)),
            ("storage", config.TrieNodeLogStorageBytes, config.TrieNodeLogStorageShardCount, nameof(IFlatDbConfig.TrieNodeLogStorageShardCount)),
        ];
        List<TrieNodeLogShard> shards = [];
        for (int partition = 0; partition < PartitionCount; partition++)
        {
            (string partitionName, long budget, int shardCount, string shardCountSetting) = partitions[partition];
            if (shardCount < 1 || !BitOperations.IsPow2(shardCount))
                throw new InvalidConfigurationException($"{shardCountSetting} must be a power of two, got {shardCount}", -1);

            _partitionOffset[partition] = shards.Count;
            _partitionShift[partition] = 8 - BitOperations.Log2((uint)shardCount);
            for (int shard = 0; shard < shardCount; shard++)
            {
                string name = $"{partitionName}-{shard}";
                shards.Add(new TrieNodeLogShard(name, Path.Combine(basePath, name), db, budget / shardCount, config.TrieNodeLogMergeLag, config.TrieNodeLogMergeBacklogMargin, _mergeLimiter, config.TrieNodeLogCompression, logManager));
            }
        }
        _shards = shards.ToArray();

        foreach (FlatDbColumns column in TrieColumns) db.GetColumnDb(column).SetWriteBuffer(WriteBufferAdjuster.MaxWriteBufferSize(column));
    }

    private static readonly FlatDbColumns[] TrieColumns = [FlatDbColumns.StateTopNodes, FlatDbColumns.StateNodes, FlatDbColumns.StorageNodes, FlatDbColumns.FallbackNodes];

    internal IReadOnlyList<TrieNodeLogShard> Shards => _shards;

    internal static bool Covers(FlatDbColumns column) => column is FlatDbColumns.StateTopNodes or FlatDbColumns.StateNodes or FlatDbColumns.StorageNodes or FlatDbColumns.FallbackNodes;

    private const int StateTopPartition = 0;
    private const int StatePartition = 1;
    private const int StoragePartition = 2;
    private const int PartitionCount = 3;

    /// <summary>Shard of a column key: its partition, then the top bits of its first byte (after the fallback column's partition prefix).</summary>
    internal int ShardIndex(byte column, ReadOnlySpan<byte> key)
    {
        int partition;
        int shardByte;
        switch ((FlatDbColumns)column)
        {
            case FlatDbColumns.FallbackNodes:
                // BaseTriePersistence prefixes fallback keys with 0 for state and 1 for storage nodes.
                partition = key[0] == 1 ? StoragePartition : StatePartition;
                shardByte = key[1];
                break;
            case FlatDbColumns.StorageNodes:
                partition = StoragePartition;
                shardByte = key[0];
                break;
            case FlatDbColumns.StateNodes:
                partition = StatePartition;
                shardByte = key[0];
                break;
            default:
                partition = StateTopPartition;
                shardByte = key[0];
                break;
        }

        return _partitionOffset[partition] + (shardByte >> _partitionShift[partition]);
    }

    public ITrieNodeLog.IView PinLiveGenerations()
    {
        TrieNodeLogView[] views = new TrieNodeLogView[_shards.Length];
        for (int i = 0; i < views.Length; i++) views[i] = _shards[i].PinLiveGenerations();
        return new View(this, views);
    }

    public ITrieNodeLog.IWriteBatch StartWriteBatch(bool bypass)
    {
        if (bypass)
        {
            Drain();
            return NullTrieNodeLog.Instance;
        }

        TrieNodeLogWriteBatch[] batches = new TrieNodeLogWriteBatch[_shards.Length];
        try
        {
            for (int i = 0; i < batches.Length; i++) batches[i] = _shards[i].StartWriteBatch();
        }
        catch
        {
            foreach (TrieNodeLogWriteBatch? batch in batches) batch?.Dispose();
            throw;
        }

        return new WriteBatch(this, batches);
    }

    public void Drain()
    {
        foreach (TrieNodeLogShard shard in _shards) shard.ThrowIfBatchOpen();
        Parallel.ForEach(_shards, static shard => shard.Drain());
    }

    public void Clear()
    {
        foreach (TrieNodeLogShard shard in _shards) shard.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (TrieNodeLogShard shard in _shards) await shard.DisposeAsync();
        _mergeLimiter.Dispose();
    }

    private sealed class View(TrieNodeLog log, TrieNodeLogView[] views) : ITrieNodeLog.IView
    {
        public void Bind(IReadOnlyKeyValueStore metadata)
        {
            foreach (TrieNodeLogView view in views) view.Bind(metadata);
        }

        public IReadOnlyKeyValueStore Wrap(FlatDbColumns column, IReadOnlyKeyValueStore inner) =>
            Covers(column) ? new Column(log, views, (byte)column, inner) : inner;

        public void Dispose()
        {
            foreach (TrieNodeLogView view in views) view.Dispose();
        }

        private sealed class Column(TrieNodeLog log, TrieNodeLogView[] views, byte column, IReadOnlyKeyValueStore inner) : IReadOnlyKeyValueStore
        {
            public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) =>
                views[log.ShardIndex(column, key)].TryGet(column, key, out byte[]? value) ? value : inner.Get(key, flags);

            public bool KeyExists(ReadOnlySpan<byte> key) =>
                views[log.ShardIndex(column, key)].TryGet(column, key, out byte[]? value) ? value is not null : inner.KeyExists(key);
        }
    }

    /// <summary>
    /// Stages each record into a per-shard chunk and hands full chunks to that shard's append worker, so the
    /// hashing, index probing and file writes of the shards proceed in parallel with the caller.
    /// </summary>
    private sealed class WriteBatch : ITrieNodeLog.IWriteBatch
    {
        private readonly TrieNodeLog _log;
        private readonly TrieNodeLogWriteBatch[] _batches;
        private readonly ShardWriter[] _writers;
        private bool _committed;

        public WriteBatch(TrieNodeLog log, TrieNodeLogWriteBatch[] batches)
        {
            _log = log;
            _batches = batches;
            _writers = new ShardWriter[batches.Length];
            for (int i = 0; i < batches.Length; i++) _writers[i] = new ShardWriter(batches[i]);
        }

        public IWriteBatch Wrap(FlatDbColumns column, IWriteBatch inner) => Covers(column) ? new Column(this, (byte)column) : inner;

        private void Stage(byte column, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, bool delete) =>
            _writers[_log.ShardIndex(column, key)].Stage(column, key, value, delete);

        public void Commit(IWriteOnlyKeyValueStore metadataBatch)
        {
            long sw = Stopwatch.GetTimestamp();
            try
            {
                foreach (ShardWriter writer in _writers) writer.Complete();
                Parallel.ForEach(_batches, static batch => batch.MakeDurable());
            }
            catch
            {
                foreach (TrieNodeLogWriteBatch batch in _batches) batch.Abort();
                throw;
            }

            Parallel.ForEach(_batches, static batch => batch.Publish());
            // The RocksDB batch is not thread-safe.
            foreach (TrieNodeLogWriteBatch batch in _batches) batch.WriteVersion(metadataBatch);
            _committed = true;
            Metrics.TrieNodeLogCommitTime.Observe(Stopwatch.GetTimestamp() - sw);
        }

        public void Dispose()
        {
            foreach (ShardWriter writer in _writers) writer.Dispose(_log._logger);
            if (!_committed)
            {
                foreach (TrieNodeLogWriteBatch batch in _batches) batch.Abort();
            }
            foreach (TrieNodeLogWriteBatch batch in _batches) batch.Dispose();
        }

        private sealed class Column(WriteBatch batch, byte column) : IWriteBatch
        {
            public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
            {
                if (value is null) Remove(key);
                else batch.Stage(column, key, value, delete: false);
            }

            public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) => batch.Stage(column, key, value, delete: false);

            public void Remove(ReadOnlySpan<byte> key) => batch.Stage(column, key, default, delete: true);

            public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) => throw new NotSupportedException();

            public void Clear() => throw new NotSupportedException();

            // The owning persistence batch drives commit and disposal.
            public void Dispose() { }
        }
    }

    /// <summary>Staging chunks of one shard and the worker that appends them to the shard's batch, in order.</summary>
    private sealed class ShardWriter(TrieNodeLogWriteBatch batch)
    {
        private readonly Channel<(byte[] Buffer, int Length)> _chunks = Channel.CreateBounded<(byte[], int)>(new BoundedChannelOptions(StagingQueueDepth) { SingleReader = true, SingleWriter = true });
        private Task? _worker;
        private byte[]? _chunk;
        private int _chunkLength;

        public void Stage(byte column, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, bool delete)
        {
            int length = StagedRecordHeaderLength + key.Length + value.Length;
            if (_chunk is not null && _chunkLength + length > _chunk.Length) Dispatch();
            _chunk ??= ArrayPool<byte>.Shared.Rent(Math.Max(StagingChunkSize, length));

            Span<byte> destination = _chunk.AsSpan(_chunkLength, length);
            destination[0] = column;
            destination[1] = delete ? (byte)1 : (byte)0;
            destination[2] = (byte)key.Length;
            BinaryPrimitives.WriteInt32LittleEndian(destination[3..], value.Length);
            key.CopyTo(destination[StagedRecordHeaderLength..]);
            value.CopyTo(destination[(StagedRecordHeaderLength + key.Length)..]);
            _chunkLength += length;
        }

        private void Dispatch()
        {
            _worker ??= Task.Run(Append);
            (byte[] Buffer, int Length) chunk = (_chunk!, _chunkLength);
            _chunk = null;
            _chunkLength = 0;
            if (!_chunks.Writer.TryWrite(chunk)) _chunks.Writer.WriteAsync(chunk).AsTask().GetAwaiter().GetResult();
        }

        /// <summary>Hands over the last chunk and waits for the worker; a worker failure is thrown here.</summary>
        public void Complete()
        {
            if (_chunkLength > 0) Dispatch();
            _chunks.Writer.TryComplete();
            _worker?.GetAwaiter().GetResult();
        }

        private async Task Append()
        {
            try
            {
                await foreach ((byte[] buffer, int length) in _chunks.Reader.ReadAllAsync())
                {
                    int position = 0;
                    while (position < length)
                    {
                        ReadOnlySpan<byte> record = buffer.AsSpan(position);
                        int keyLength = record[2];
                        int valueLength = BinaryPrimitives.ReadInt32LittleEndian(record[3..]);
                        batch.Append(record[0], record.Slice(StagedRecordHeaderLength, keyLength), record.Slice(StagedRecordHeaderLength + keyLength, valueLength), delete: record[1] == 1);
                        position += StagedRecordHeaderLength + keyLength + valueLength;
                    }
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (Exception e)
            {
                // Unblocks a producer waiting for queue space; the failure surfaces again in Complete.
                _chunks.Writer.TryComplete(e);
                throw;
            }
        }

        public void Dispose(ILogger logger)
        {
            _chunks.Writer.TryComplete();
            try
            {
                _worker?.GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                // Already thrown from Complete when the batch was committed; a batch abandoned before that only logs.
                if (logger.IsDebug) logger.Debug($"Trie node log append worker failed: {e}");
            }

            while (_chunks.Reader.TryRead(out (byte[] Buffer, int Length) chunk)) ArrayPool<byte>.Shared.Return(chunk.Buffer);
            if (_chunk is not null) ArrayPool<byte>.Shared.Return(_chunk);
            _chunk = null;
        }
    }
}
