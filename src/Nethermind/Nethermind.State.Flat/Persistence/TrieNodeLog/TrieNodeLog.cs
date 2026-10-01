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
/// <see cref="ITrieNodeLog"/> made of independent <see cref="TrieNodeLogShard"/>s: a state partition
/// (<c>StateTopNodes</c>, <c>StateNodes</c>, state entries of <c>FallbackNodes</c>) and, with
/// <see cref="TrieNodeLogScope.All"/>, a storage partition (<c>StorageNodes</c>, storage entries of
/// <c>FallbackNodes</c>), each split into <see cref="IFlatDbConfig.TrieNodeLogShardCount"/> shards by the first
/// byte of the column key. A batch's records are staged per shard and appended by one worker per shard, and the
/// shards are made durable and merged in parallel.
/// </summary>
public sealed class TrieNodeLog : ITrieNodeLog, IAsyncDisposable
{
    private const int StagingChunkSize = 1024 * 1024;
    private const int StagingQueueDepth = 4;
    private const int StagedRecordHeaderLength = 1 + 1 + 1 + 4; // column, delete flag, key length, value length

    private readonly TrieNodeLogScope _scope;
    private readonly int _shardCount;
    private readonly int _shardBits;
    private readonly TrieNodeLogShard[] _shards; // state shards first, then storage shards
    private readonly ILogger _logger;

    public TrieNodeLog(string basePath, IColumnsDb<FlatDbColumns> db, IFlatDbConfig config, ILogManager logManager)
    {
        _scope = config.TrieNodeLogScope;
        _shardCount = config.TrieNodeLogShardCount;
        if (_shardCount < 1 || !BitOperations.IsPow2(_shardCount))
            throw new InvalidConfigurationException($"{nameof(IFlatDbConfig.TrieNodeLogShardCount)} must be a power of two, got {_shardCount}", -1);
        _shardBits = BitOperations.Log2((uint)_shardCount);
        _logger = logManager.GetClassLogger<TrieNodeLog>();

        int partitions = _scope == TrieNodeLogScope.All ? 2 : 1;
        _shards = new TrieNodeLogShard[partitions * _shardCount];
        for (int partition = 0; partition < partitions; partition++)
        {
            string partitionName = partition == 0 ? "state" : "storage";
            long budget = partition == 0 ? config.TrieNodeLogStateBytes : config.TrieNodeLogStorageBytes;
            for (int shard = 0; shard < _shardCount; shard++)
            {
                string name = $"{partitionName}-{shard}";
                _shards[partition * _shardCount + shard] = new TrieNodeLogShard(name, Path.Combine(basePath, name), db, budget / _shardCount, config.TrieNodeLogMergeLag, logManager);
            }
        }

        foreach (FlatDbColumns column in Enum.GetValues<FlatDbColumns>())
        {
            if (Covers(column)) db.GetColumnDb(column).SetWriteBuffer(WriteBufferAdjuster.MaxWriteBufferSize(column));
        }
    }

    internal IReadOnlyList<TrieNodeLogShard> Shards => _shards;

    internal bool Covers(FlatDbColumns column) => column switch
    {
        FlatDbColumns.StateTopNodes => _scope >= TrieNodeLogScope.StateTop,
        FlatDbColumns.StateNodes => _scope >= TrieNodeLogScope.State,
        FlatDbColumns.StorageNodes or FlatDbColumns.FallbackNodes => _scope >= TrieNodeLogScope.All,
        _ => false,
    };

    /// <summary>Shard of a column key: its partition, then the top bits of its first byte (after the fallback column's partition prefix).</summary>
    internal int ShardIndex(byte column, ReadOnlySpan<byte> key)
    {
        bool storage;
        int shardByte;
        switch ((FlatDbColumns)column)
        {
            case FlatDbColumns.FallbackNodes:
                // BaseTriePersistence prefixes fallback keys with 0 for state and 1 for storage nodes.
                storage = key[0] == 1;
                shardByte = key[1];
                break;
            case FlatDbColumns.StorageNodes:
                storage = true;
                shardByte = key[0];
                break;
            default:
                storage = false;
                shardByte = key[0];
                break;
        }

        return (storage ? _shardCount : 0) + (shardByte >> (8 - _shardBits));
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
    }

    private sealed class View(TrieNodeLog log, TrieNodeLogView[] views) : ITrieNodeLog.IView
    {
        public void Bind(IReadOnlyKeyValueStore metadata)
        {
            foreach (TrieNodeLogView view in views) view.Bind(metadata);
        }

        public IReadOnlyKeyValueStore Wrap(FlatDbColumns column, IReadOnlyKeyValueStore inner) =>
            log.Covers(column) ? new Column(log, views, (byte)column, inner) : inner;

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

        public IWriteBatch Wrap(FlatDbColumns column, IWriteBatch inner) => _log.Covers(column) ? new Column(this, (byte)column) : inner;

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
