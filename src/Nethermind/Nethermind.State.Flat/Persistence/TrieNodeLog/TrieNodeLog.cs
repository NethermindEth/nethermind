// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Text;
using System.Threading.Channels;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Flat.Persistence.TrieNodeLog;

/// <summary>
/// <see cref="ITrieNodeLog"/> made of independent <see cref="TrieNodeLogShard"/>s: a state partition
/// (<c>StateTopNodes</c> and <c>StateNodes</c>) and a storage partition (<c>StorageNodes</c>), each with its own
/// byte budget and shard count, sharded by the first byte of the column key.
/// <c>FallbackNodes</c> (paths of 16+ nibbles, practically empty) goes straight to RocksDB. A batch's records are
/// staged per shard and appended by one worker per shard, and the shards are made durable and merged in parallel.
/// Unless <see cref="IFlatDbConfig.TrieNodeLogSecondLevelMergeLag"/> is -1, every shard merges into a second-level shard
/// of the same size instead of RocksDB, and only that one merges into RocksDB.
/// </summary>
public sealed class TrieNodeLog : ITrieNodeLog, IAsyncDisposable
{
    private const int StagingChunkSize = 1024 * 1024;
    private const int StagingQueueDepth = 4;
    private const int StagedRecordHeaderLength = 1 + 1 + 4; // delete flag, key length, value length
    private const string SecondLevelSuffix = "-l2";
    private const int SecondLevelIndexRatio = 16; // both partitions

    private readonly int[] _partitionOffset = new int[PartitionCount]; // index of a partition's first shard
    private readonly int[] _partitionShift = new int[PartitionCount]; // right shift of the shard byte selecting the shard
    private readonly TrieNodeLogShard[] _shards; // partition-major: state, storage
    private readonly TrieNodeLogShard[] _secondLevelShards; // same order as _shards, empty when disabled
    private static readonly FlatDbColumns[] StateColumns = [FlatDbColumns.StateTopNodes, FlatDbColumns.StateNodes];
    private static readonly FlatDbColumns[] StorageColumns = [FlatDbColumns.StorageNodes];
    private readonly SemaphoreSlim _mergeLimiter;
    // A first-level merge holds its limiter while it may wait for second-level merges, so they cannot share one.
    private readonly SemaphoreSlim _secondLevelMergeLimiter;
    private readonly Lock _drainLock = new(); // drains and clears queue behind each other; sync runs bypass batches from several threads
    private readonly bool _drainOnShutdown;
    private readonly ILogger _logger;
    private int _disposed;

    public TrieNodeLog(string basePath, IColumnsDb<FlatDbColumns> db, IFlatDbConfig config, ILogManager logManager)
    {
        if (config.TrieNodeLogMaxConcurrentMerges < 1)
            throw new InvalidConfigurationException($"{nameof(IFlatDbConfig.TrieNodeLogMaxConcurrentMerges)} must be at least 1, got {config.TrieNodeLogMaxConcurrentMerges}", -1);
        if (config.TrieNodeLogMergeBacklogMargin < 1)
            throw new InvalidConfigurationException($"{nameof(IFlatDbConfig.TrieNodeLogMergeBacklogMargin)} must be at least 1, got {config.TrieNodeLogMergeBacklogMargin}", -1);
        if (config.TrieNodeLogSecondLevelMergeLag < -1)
            throw new InvalidConfigurationException($"{nameof(IFlatDbConfig.TrieNodeLogSecondLevelMergeLag)} must be -1 (disabled) or at least 0, got {config.TrieNodeLogSecondLevelMergeLag}", -1);
        _mergeLimiter = new SemaphoreSlim(config.TrieNodeLogMaxConcurrentMerges, config.TrieNodeLogMaxConcurrentMerges);
        _secondLevelMergeLimiter = new SemaphoreSlim(config.TrieNodeLogMaxConcurrentMerges, config.TrieNodeLogMaxConcurrentMerges);
        _drainOnShutdown = config.TrieNodeLogDrainOnShutdown;
        _logger = logManager.GetClassLogger<TrieNodeLog>();

        // A generation's index is 1/IndexRatio of its bytes and rolls it at three-quarters occupancy, so the ratio
        // follows the partition's typical record size: state nodes are mostly branches, storage has smaller leaves.
        ReadOnlySpan<(string Name, FlatDbColumns[] Columns, long Budget, int ShardCount, string ShardCountSetting, int IndexRatio)> partitions =
        [
            (StatePartitionName, StateColumns, config.TrieNodeLogStateBytes, config.TrieNodeLogStateShardCount, nameof(IFlatDbConfig.TrieNodeLogStateShardCount), 64),
            (StoragePartitionName, StorageColumns, config.TrieNodeLogStorageBytes, config.TrieNodeLogStorageShardCount, nameof(IFlatDbConfig.TrieNodeLogStorageShardCount), 32),
        ];
        using ArrayPoolListRef<TrieNodeLogShard> shards = new(PartitionCount);
        using ArrayPoolListRef<TrieNodeLogShard> secondLevelShards = new(PartitionCount);
        try
        {
            for (int partition = 0; partition < PartitionCount; partition++)
            {
                (string partitionName, FlatDbColumns[] columns, long budget, int shardCount, string shardCountSetting, int indexRatio) = partitions[partition];
                if (shardCount < 1 || !BitOperations.IsPow2(shardCount))
                    throw new InvalidConfigurationException($"{shardCountSetting} must be a power of two, got {shardCount}", -1);

                _partitionOffset[partition] = shards.Count;
                _partitionShift[partition] = 8 - BitOperations.Log2((uint)shardCount);
                for (int shard = 0; shard < shardCount; shard++)
                {
                    string name = $"{partitionName}-{shard}";
                    TrieNodeLogShard? secondLevel = null;
                    if (SecondLevelEnabled(config))
                    {
                        string secondLevelName = name + SecondLevelSuffix;
                        secondLevel = new TrieNodeLogShard(secondLevelName, name, columns, Path.Combine(basePath, secondLevelName), db, budget / shardCount, SecondLevelIndexRatio, config.TrieNodeLogSecondLevelMergeLag, config.TrieNodeLogMergeBacklogMargin, _secondLevelMergeLimiter, secondLevel: null, logManager);
                        secondLevelShards.Add(secondLevel);
                    }
                    shards.Add(new TrieNodeLogShard(name, name, columns, Path.Combine(basePath, name), db, budget / shardCount, indexRatio, config.TrieNodeLogMergeLag, config.TrieNodeLogMergeBacklogMargin, _mergeLimiter, secondLevel, logManager));
                }
                foreach (FlatDbColumns column in columns) db.GetColumnDb(column).SetWriteBuffer(WriteBufferAdjuster.MaxWriteBufferSize(column));
            }
        }
        catch
        {
            // A shard that failed to recover must not leave the ones already started running.
            foreach (TrieNodeLogShard shard in shards) shard.DisposeAsync().AsTask().GetAwaiter().GetResult();
            foreach (TrieNodeLogShard shard in secondLevelShards) shard.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
        _shards = shards.ToArray();
        _secondLevelShards = secondLevelShards.ToArray();
        WriteShardManifest(db, _shards.Concat(_secondLevelShards).Select(static shard => shard.Name));
    }

    internal IReadOnlyList<TrieNodeLogShard> Shards => _shards;

    private static bool SecondLevelEnabled(IFlatDbConfig config) => config.TrieNodeLogSecondLevelMergeLag >= 0;

    /// <summary>
    /// Whether the shard directories under <paramref name="basePath"/> are exactly those <paramref name="config"/>
    /// would create, so a log constructed over them recovers their generations instead of needing
    /// <see cref="MergeAllOnDisk"/> first. A partition with no directories at all is fine; one with another shard
    /// count is not, since the shard a key maps to depends on that count. Second-level directories are held to the
    /// same rule when the second level is enabled, and do not match when it is disabled.
    /// </summary>
    public static bool MatchesOnDiskLayout(string basePath, IFlatDbConfig config)
    {
        if (!Directory.Exists(basePath)) return true;
        HashSet<string> onDisk = Directory.GetDirectories(basePath).Select(Path.GetFileName).ToHashSet()!;
        string[] suffixes = SecondLevelEnabled(config) ? ["", SecondLevelSuffix] : [""];
        foreach ((string name, int shardCount) in new[] { (StatePartitionName, config.TrieNodeLogStateShardCount), (StoragePartitionName, config.TrieNodeLogStorageShardCount) })
        {
            foreach (string suffix in suffixes)
            {
                int present = 0;
                for (int shard = 0; shard < shardCount; shard++)
                {
                    if (onDisk.Remove($"{name}-{shard}{suffix}")) present++;
                }
                if (present != 0 && present != shardCount) return false;
            }
        }
        return onDisk.Count == 0;
    }

    // Metadata key of the shard names the database may hold confirmed-but-unmerged generations for, so a database
    // restored without the log directory is refused even when no directory is left to enumerate.
    private static readonly byte[] ShardManifestKey = Keccak.Compute("TrieNodeLogShards").BytesToArray();

    /// <summary>Durably records the shard names before any of them commits.</summary>
    private static void WriteShardManifest(IColumnsDb<FlatDbColumns> db, IEnumerable<string> names)
    {
        IDb metadata = db.GetColumnDb(FlatDbColumns.Metadata);
        metadata.PutSpan(ShardManifestKey, Encoding.UTF8.GetBytes(string.Join('\n', names)));
        metadata.FlushOrThrow();
    }

    /// <summary>
    /// Merges every shard directory found under <paramref name="basePath"/> into RocksDB and removes it, whatever
    /// shard layout wrote it, so the log can be reconfigured or disabled between runs without losing nodes.
    /// Run before the log is constructed; the configured shards then start empty.
    /// </summary>
    /// <remarks>
    /// Refuses a database that confirms generations of a recorded shard whose files are gone. Second-level shards
    /// go first: they hold what their first-level shards merged before.
    /// </remarks>
    public static void MergeAllOnDisk(string basePath, IColumnsDb<FlatDbColumns> db, ILogManager logManager)
    {
        IDb metadata = db.GetColumnDb(FlatDbColumns.Metadata);
        byte[]? manifest = metadata.Get(ShardManifestKey);
        if (manifest is not null)
        {
            foreach (string name in Encoding.UTF8.GetString(manifest).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                TrieNodeLogShard.ThrowIfConfirmedGenerationsMissing(metadata, name, Path.Combine(basePath, name));
        }

        if (!Directory.Exists(basePath)) return;
        ILogger logger = logManager.GetClassLogger<TrieNodeLog>();
        using SemaphoreSlim mergeLimiter = new(1, 1);
        foreach (string directory in Directory.GetDirectories(basePath).OrderBy(static directory => directory.EndsWith(SecondLevelSuffix) ? 0 : 1))
        {
            string name = Path.GetFileName(directory);
            FlatDbColumns[]? columns = name.Split('-')[0] switch
            {
                "state_top" or StatePartitionName => StateColumns,
                StoragePartitionName => StorageColumns,
                _ => null,
            };
            if (columns is null)
            {
                if (logger.IsWarn) logger.Warn($"Ignoring unrecognized trie node log directory {directory}");
                continue;
            }

            if (logger.IsInfo) logger.Info($"Merging trie node log shard {name} left by the previous run");
            string versionName = name.EndsWith(SecondLevelSuffix) ? name[..^SecondLevelSuffix.Length] : name;
            TrieNodeLogShard shard = new(name, versionName, columns, directory, db, generationBytes: 0, indexRatio: 1, mergeLag: 0, backlogMargin: 1, mergeLimiter, secondLevel: null, logManager);
            try
            {
                shard.Drain();
            }
            finally
            {
                shard.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            Directory.Delete(directory, recursive: true);
        }
        metadata.Remove(ShardManifestKey);
    }

    internal static bool Covers(FlatDbColumns column) => column is FlatDbColumns.StateTopNodes or FlatDbColumns.StateNodes or FlatDbColumns.StorageNodes;

    private const string StatePartitionName = "state";
    private const string StoragePartitionName = "storage";
    private const int StatePartition = 0;
    private const int StoragePartition = 1;
    private const int PartitionCount = 2;

    /// <summary>Shard of a column key: its column's partition, then the top bits of the key's first byte.</summary>
    internal int ShardIndex(byte column, ReadOnlySpan<byte> key)
    {
        int partition = (FlatDbColumns)column == FlatDbColumns.StorageNodes ? StoragePartition : StatePartition;
        int shardByte = key[0];

        return _partitionOffset[partition] + (shardByte >> _partitionShift[partition]);
    }

    public ITrieNodeLog.IView OpenView(IColumnsDb<FlatDbColumns> db, ReaderFlags flags)
    {
        // Pinned before the snapshot so a generation merged and deleted in between stays readable, bound after it
        // so the view serves exactly the log version the snapshot's metadata confirms. The second level is pinned after
        // the first, so a generation the first level dropped before its pin is already published in the second level.
        ArrayPoolList<TrieNodeLogView> views = new(_shards.Length + _secondLevelShards.Length);
        IColumnDbSnapshot<FlatDbColumns>? snapshot = null;
        try
        {
            foreach (TrieNodeLogShard shard in _shards) views.Add(shard.PinLiveGenerations());
            foreach (TrieNodeLogShard shard in _secondLevelShards) views.Add(shard.PinLiveGenerations());
            snapshot = db.CreateSnapshot(flags);
            IReadOnlyKeyValueStore metadata = snapshot.GetColumn(FlatDbColumns.Metadata);
            foreach (TrieNodeLogView view in views) view.Bind(metadata);
            return new View(this, snapshot, views);
        }
        catch
        {
            views.DisposeRecursive();
            snapshot?.Dispose();
            throw;
        }
    }

    public ITrieNodeLog.IWriteBatch StartWriteBatch(IColumnsWriteBatch<FlatDbColumns> batch, bool bypass)
    {
        if (bypass) return NullTrieNodeLog.Instance;

        ArrayPoolList<TrieNodeLogWriteBatch> batches = new(_shards.Length);
        try
        {
            foreach (TrieNodeLogShard shard in _shards) batches.Add(shard.StartWriteBatch());
        }
        catch
        {
            batches.DisposeRecursive();
            throw;
        }

        return new WriteBatch(this, batch, batches);
    }

    public void Drain()
    {
        using Lock.Scope _ = _drainLock.EnterScope();
        int gated = EnterExclusive();
        try
        {
            if (Array.Exists(_shards, static shard => shard.HasGenerations))
                Parallel.ForEach(_shards, static shard => shard.DrainExclusive());
        }
        finally
        {
            for (int i = 0; i < gated; i++) _shards[i].ExitExclusive();
        }
    }

    public void Clear()
    {
        using Lock.Scope _ = _drainLock.EnterScope();
        int gated = EnterExclusive();
        try
        {
            foreach (TrieNodeLogShard shard in _shards) shard.ClearExclusive();
        }
        finally
        {
            for (int i = 0; i < gated; i++) _shards[i].ExitExclusive();
        }
    }

    /// <summary>Takes every shard's batch gate, so no log-backed batch is open or can open; returns how many were taken.</summary>
    private int EnterExclusive()
    {
        int gated = 0;
        try
        {
            for (; gated < _shards.Length; gated++) _shards[gated].EnterExclusive();
        }
        catch
        {
            for (int i = 0; i < gated; i++) _shards[i].ExitExclusive();
            throw;
        }
        return gated;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_drainOnShutdown)
        {
            try
            {
                Drain();
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error("Trie node log merge at shutdown failed; the generations are kept for the next start", e);
            }
        }
        // First-level merges may still be copying into the second level.
        foreach (TrieNodeLogShard shard in _shards) await shard.DisposeAsync();
        foreach (TrieNodeLogShard shard in _secondLevelShards) await shard.DisposeAsync();
        _mergeLimiter.Dispose();
        _secondLevelMergeLimiter.Dispose();
    }

    private sealed class View(TrieNodeLog log, IColumnDbSnapshot<FlatDbColumns> snapshot, ArrayPoolList<TrieNodeLogView> views) : ITrieNodeLog.IView
    {
        public IColumnDbSnapshot<FlatDbColumns> Snapshot => snapshot;

        public IReadOnlyKeyValueStore GetColumn(FlatDbColumns column) =>
            Covers(column) ? new Column(log, views, (byte)column, snapshot.GetColumn(column)) : snapshot.GetColumn(column);

        public void Dispose()
        {
            snapshot.Dispose();
            views.DisposeRecursive();
        }

        private sealed class Column(TrieNodeLog log, ArrayPoolList<TrieNodeLogView> views, byte column, IReadOnlyKeyValueStore inner) : IReadOnlyKeyValueStore
        {
            public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) =>
                TryGet(key, out byte[]? value) ? value : inner.Get(key, flags);

            public bool KeyExists(ReadOnlySpan<byte> key) =>
                TryGet(key, out byte[]? value) ? value is not null : inner.KeyExists(key);

            /// <summary>The first level, then its second-level shard, which follows the first-level views.</summary>
            private bool TryGet(ReadOnlySpan<byte> key, out byte[]? value)
            {
                int shard = log.ShardIndex(column, key);
                return views[shard].TryGet(column, key, out value)
                    || (log._secondLevelShards.Length != 0 && views[log._shards.Length + shard].TryGet(column, key, out value));
            }
        }
    }

    /// <summary>
    /// Stages each record into a per-shard chunk and hands full chunks to that shard's append worker, so the
    /// hashing, index probing and file writes of the shards proceed in parallel with the caller.
    /// </summary>
    private sealed class WriteBatch : ITrieNodeLog.IWriteBatch
    {
        private readonly TrieNodeLog _log;
        private readonly IColumnsWriteBatch<FlatDbColumns> _rocksDbBatch;
        private readonly ArrayPoolList<TrieNodeLogWriteBatch> _batches;
        private readonly ArrayPoolList<ShardWriter> _writers;
        private bool _durable;
        private bool _committed;
        private bool _confirmed;

        public WriteBatch(TrieNodeLog log, IColumnsWriteBatch<FlatDbColumns> rocksDbBatch, ArrayPoolList<TrieNodeLogWriteBatch> batches)
        {
            _log = log;
            _rocksDbBatch = rocksDbBatch;
            _batches = batches;
            _writers = new ArrayPoolList<ShardWriter>(batches.Count);
            foreach (TrieNodeLogWriteBatch batch in batches) _writers.Add(new ShardWriter(batch, log._logger));
        }

        public IWriteBatch Wrap(FlatDbColumns column, IWriteBatch inner) => Covers(column) ? new Column(this, (byte)column) : inner;

        private void Stage(byte column, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, bool delete) =>
            _writers[_log.ShardIndex(column, key)].Stage(key, value, delete);

        public void Commit()
        {
            long sw = Stopwatch.GetTimestamp();
            try
            {
                foreach (ShardWriter writer in _writers) writer.Complete();
                Parallel.ForEach(_batches, static batch => batch.MakeDurable());
            }
            catch
            {
                // Every append worker has stopped before its generation is truncated.
                foreach (ShardWriter writer in _writers) writer.Dispose();
                foreach (TrieNodeLogWriteBatch batch in _batches) batch.Abort();
                throw;
            }
            _durable = true;

            Parallel.ForEach(_batches, static batch => batch.Publish());
            // The RocksDB batch is not thread-safe.
            IWriteBatch metadata = _rocksDbBatch.GetColumnBatch(FlatDbColumns.Metadata);
            foreach (TrieNodeLogWriteBatch batch in _batches) batch.WriteVersion(metadata);
            _committed = true;
            Metrics.TrieNodeLogCommitTime.Observe(Stopwatch.GetTimestamp() - sw);
        }

        public void Confirm() => _confirmed = _committed;

        public void Dispose()
        {
            _writers.DisposeRecursive();
            if (!_durable)
            {
                foreach (TrieNodeLogWriteBatch batch in _batches) batch.Abort();
            }
            else if (!_confirmed)
            {
                // Fsynced records RocksDB never confirmed, possibly published: they cannot be unpublished under readers,
                // and a later confirmed version would vouch for them, so the shards stop taking batches until a restart.
                foreach (TrieNodeLogWriteBatch batch in _batches) batch.Poison();
            }
            _batches.DisposeRecursive();
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
    private sealed class ShardWriter(TrieNodeLogWriteBatch batch, ILogger logger) : IDisposable
    {
        private readonly Channel<(byte[] Buffer, int Length)> _chunks = Channel.CreateBounded<(byte[], int)>(new BoundedChannelOptions(StagingQueueDepth) { SingleReader = true, SingleWriter = true });
        private Task? _worker;
        private byte[]? _chunk;
        private int _chunkLength;

        public void Stage(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, bool delete)
        {
            int length = StagedRecordHeaderLength + key.Length + value.Length;
            if (_chunk is not null && _chunkLength + length > _chunk.Length) Dispatch();
            _chunk ??= ArrayPool<byte>.Shared.Rent(Math.Max(StagingChunkSize, length));

            Span<byte> destination = _chunk.AsSpan(_chunkLength, length);
            destination[0] = delete ? (byte)1 : (byte)0;
            destination[1] = (byte)key.Length;
            BinaryPrimitives.WriteInt32LittleEndian(destination[2..], value.Length);
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
                        int keyLength = record[1];
                        int valueLength = BinaryPrimitives.ReadInt32LittleEndian(record[2..]);
                        batch.Append(record.Slice(StagedRecordHeaderLength, keyLength), record.Slice(StagedRecordHeaderLength + keyLength, valueLength), delete: record[0] == 1);
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

        public void Dispose()
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
