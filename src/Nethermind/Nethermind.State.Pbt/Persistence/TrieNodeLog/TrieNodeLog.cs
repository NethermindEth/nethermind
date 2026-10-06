// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Channels;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.State.Pbt.Persistence.TrieNodeLog;

/// <summary>
/// <see cref="ITrieNodeLog"/> made of independent <see cref="TrieNodeLogShard"/>s: an account partition
/// (<c>TopNodeGroups</c>, <c>AccountNodeGroups</c> and <c>CodeNodeGroups</c>) and a storage partition
/// (<c>StorageNodeGroups</c>), each with its own byte budget and shard count, sharded by the key hash. The root group
/// (in <c>Metadata</c>) goes straight to RocksDB. A batch's records are staged per shard and appended by one worker
/// per shard, and the shards are made durable and merged in parallel. Unless
/// <see cref="IPbtConfig.TrieNodeLogSecondLevelMergeLag"/> is -1, every shard merges into a second-level shard of the
/// same size instead of RocksDB, and only that one merges into RocksDB.
/// </summary>
public sealed class TrieNodeLog : ITrieNodeLog, IAsyncDisposable
{
    private const string DirectoryName = "pbtTrieNodeLog";
    private const int StagingChunkSize = 1024 * 1024;
    private const int StagingQueueDepth = 4;
    private const int StagedRecordHeaderLength = 1 + 1 + 4; // delete flag, key length, value length
    private const string SecondLevelSuffix = "-l2";
    private const int SecondLevelIndexRatio = 16; // both partitions
    // Bits of the key hash selecting the shard: below the index slot tag (the top 24 bits) and above the bits that
    // pick an index slot in any generation that fits in memory.
    private const int ShardHashShift = 32;

    internal const string HitLabel = "hit";
    internal const string ChainLabel = "chain";
    internal const string MissLabel = "miss";
    internal const string SecondLevelHitLabel = "second_level_hit";
    internal const string SecondLevelChainLabel = "second_level_chain";
    internal const string SecondLevelMissLabel = "second_level_miss";
    internal const string ActiveLabel = "active";
    internal const string SealedLabel = "sealed";
    internal const string MergedPinnedLabel = "merged_pinned";

    private readonly int[] _partitionOffset = new int[PartitionCount]; // index of a partition's first shard
    private readonly int[] _partitionMask = new int[PartitionCount]; // shard count - 1
    private readonly TrieNodeLogShard[] _shards; // partition-major: account, storage
    private readonly TrieNodeLogShard[] _secondLevelShards; // same order as _shards, empty when disabled
    private static readonly PbtColumns[] AccountColumns = [PbtColumns.TopNodeGroups, PbtColumns.AccountNodeGroups, PbtColumns.CodeNodeGroups];
    private static readonly PbtColumns[] StorageColumns = [PbtColumns.StorageNodeGroups];
    private readonly SemaphoreSlim _mergeLimiter;
    // A first-level merge holds its limiter while it may wait for second-level merges, so they cannot share one.
    private readonly SemaphoreSlim _secondLevelMergeLimiter;
    private readonly Lock _drainLock = new(); // drains queue behind each other; staging runs bypass batches from several threads
    private readonly bool _drainOnShutdown;
    private readonly ILogger _logger;
    private int _disposed;

    public TrieNodeLog(string basePath, IColumnsDb<PbtColumns> db, IPbtConfig config, ILogManager logManager)
    {
        if (config.TrieNodeLogMaxConcurrentMerges < 1)
            throw new InvalidConfigurationException($"{nameof(IPbtConfig.TrieNodeLogMaxConcurrentMerges)} must be at least 1, got {config.TrieNodeLogMaxConcurrentMerges}", -1);
        if (config.TrieNodeLogMergeBacklogMargin < 1)
            throw new InvalidConfigurationException($"{nameof(IPbtConfig.TrieNodeLogMergeBacklogMargin)} must be at least 1, got {config.TrieNodeLogMergeBacklogMargin}", -1);
        if (config.TrieNodeLogSecondLevelMergeLag < -1)
            throw new InvalidConfigurationException($"{nameof(IPbtConfig.TrieNodeLogSecondLevelMergeLag)} must be -1 (disabled) or at least 0, got {config.TrieNodeLogSecondLevelMergeLag}", -1);
        _mergeLimiter = new SemaphoreSlim(config.TrieNodeLogMaxConcurrentMerges, config.TrieNodeLogMaxConcurrentMerges);
        _secondLevelMergeLimiter = new SemaphoreSlim(config.TrieNodeLogMaxConcurrentMerges, config.TrieNodeLogMaxConcurrentMerges);
        _drainOnShutdown = config.TrieNodeLogDrainOnShutdown;
        _logger = logManager.GetClassLogger<TrieNodeLog>();

        // A generation's index is 1/IndexRatio of its bytes and rolls it at three-quarters occupancy, so the ratio
        // follows the partition's typical record size: storage tries are sparse, so their groups are smaller.
        ReadOnlySpan<(string Name, PbtColumns[] Columns, long Budget, int ShardCount, string ShardCountSetting, int IndexRatio)> partitions =
        [
            (AccountPartitionName, AccountColumns, config.TrieNodeLogAccountBytes, config.TrieNodeLogAccountShardCount, nameof(IPbtConfig.TrieNodeLogAccountShardCount), 64),
            (StoragePartitionName, StorageColumns, config.TrieNodeLogStorageBytes, config.TrieNodeLogStorageShardCount, nameof(IPbtConfig.TrieNodeLogStorageShardCount), 32),
        ];
        using ArrayPoolListRef<TrieNodeLogShard> shards = new(PartitionCount);
        using ArrayPoolListRef<TrieNodeLogShard> secondLevelShards = new(PartitionCount);
        try
        {
            for (int partition = 0; partition < PartitionCount; partition++)
            {
                (string partitionName, PbtColumns[] columns, long budget, int shardCount, string shardCountSetting, int indexRatio) = partitions[partition];
                if (shardCount is < 1 or > 256 || !BitOperations.IsPow2(shardCount))
                    throw new InvalidConfigurationException($"{shardCountSetting} must be a power of two up to 256, got {shardCount}", -1);

                _partitionOffset[partition] = shards.Count;
                _partitionMask[partition] = shardCount - 1;
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
    }

    /// <summary>
    /// The log configured by <paramref name="config"/> in <see cref="IInitConfig.BaseDbPath"/>, or
    /// <see cref="NullTrieNodeLog"/> when it is disabled.
    /// </summary>
    /// <remarks>
    /// The log is recovered across restarts, keeping its deduplication window, unless it is now disabled or its shard
    /// layout changed; then whatever the previous run left is merged into RocksDB first.
    /// </remarks>
    public static ITrieNodeLog Create(IPbtConfig config, IInitConfig initConfig, IColumnsDb<PbtColumns> db, ILogManager logManager)
    {
        string basePath = Path.Combine(initConfig.BaseDbPath, DirectoryName);
        if (!config.TrieNodeLogEnabled || !MatchesOnDiskLayout(basePath, config))
            MergeAllOnDisk(basePath, db, logManager);
        return config.TrieNodeLogEnabled ? new TrieNodeLog(basePath, db, config, logManager) : NullTrieNodeLog.Instance;
    }

    internal IReadOnlyList<TrieNodeLogShard> Shards => _shards;

    private static bool SecondLevelEnabled(IPbtConfig config) => config.TrieNodeLogSecondLevelMergeLag >= 0;

    /// <summary>
    /// Whether the shard directories under <paramref name="basePath"/> are exactly those <paramref name="config"/>
    /// would create, so a log constructed over them recovers their generations instead of needing
    /// <see cref="MergeAllOnDisk"/> first. A partition with no directories at all is fine; one with another shard
    /// count is not, since the shard a key maps to depends on that count. Second-level directories are held to the
    /// same rule when the second level is enabled, and do not match when it is disabled.
    /// </summary>
    public static bool MatchesOnDiskLayout(string basePath, IPbtConfig config)
    {
        if (!Directory.Exists(basePath)) return true;
        HashSet<string> onDisk = Directory.GetDirectories(basePath).Select(Path.GetFileName).ToHashSet()!;
        string[] suffixes = SecondLevelEnabled(config) ? ["", SecondLevelSuffix] : [""];
        foreach ((string name, int shardCount) in new[] { (AccountPartitionName, config.TrieNodeLogAccountShardCount), (StoragePartitionName, config.TrieNodeLogStorageShardCount) })
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

    /// <summary>
    /// Merges every shard directory found under <paramref name="basePath"/> into RocksDB and removes it, whatever
    /// shard layout wrote it, so the log can be reconfigured or disabled between runs without losing groups.
    /// Run before the log is constructed; the configured shards then start empty.
    /// </summary>
    /// <remarks>Second-level shards go first: they hold what their first-level shards merged before.</remarks>
    public static void MergeAllOnDisk(string basePath, IColumnsDb<PbtColumns> db, ILogManager logManager)
    {
        if (!Directory.Exists(basePath)) return;
        ILogger logger = logManager.GetClassLogger<TrieNodeLog>();
        using SemaphoreSlim mergeLimiter = new(1, 1);
        foreach (string directory in Directory.GetDirectories(basePath).OrderBy(static directory => directory.EndsWith(SecondLevelSuffix) ? 0 : 1))
        {
            string name = Path.GetFileName(directory);
            PbtColumns[]? columns = name.Split('-')[0] switch
            {
                AccountPartitionName => AccountColumns,
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
    }

    internal static bool Covers(PbtColumns column) => column is PbtColumns.TopNodeGroups or PbtColumns.AccountNodeGroups or PbtColumns.CodeNodeGroups or PbtColumns.StorageNodeGroups;

    /// <summary>The column label of a logged column's byte metrics: its partition.</summary>
    internal static string ColumnLabel(PbtColumns column) => column == PbtColumns.StorageNodeGroups ? StoragePartitionName : AccountPartitionName;

    private const string AccountPartitionName = "account";
    private const string StoragePartitionName = "storage";
    private const int AccountPartition = 0;
    private const int StoragePartition = 1;
    private const int PartitionCount = 2;

    /// <summary>Shard of a column key: its column's partition, then bits of the key's hash.</summary>
    /// <remarks>Group keys start with the zone byte, so their leading bytes would put most groups in one shard.</remarks>
    internal int ShardIndex(PbtColumns column, ReadOnlySpan<byte> key)
    {
        int partition = column == PbtColumns.StorageNodeGroups ? StoragePartition : AccountPartition;
        return _partitionOffset[partition] + ((int)(TrieNodeLogRecord.Hash(key) >> ShardHashShift) & _partitionMask[partition]);
    }

    public ITrieNodeLog.IView OpenView(IColumnsDb<PbtColumns> db)
    {
        // Pinned before the snapshot so a generation merged and deleted in between stays readable, bound after it
        // so the view serves exactly the log version the snapshot's metadata confirms. The second level is pinned after
        // the first, so a generation the first level dropped before its pin is already published in the second level.
        ArrayPoolList<TrieNodeLogView> views = new(_shards.Length + _secondLevelShards.Length);
        IColumnDbSnapshot<PbtColumns>? snapshot = null;
        try
        {
            foreach (TrieNodeLogShard shard in _shards) views.Add(shard.PinLiveGenerations());
            foreach (TrieNodeLogShard shard in _secondLevelShards) views.Add(shard.PinLiveGenerations());
            snapshot = db.CreateSnapshot();
            IReadOnlyKeyValueStore metadata = snapshot.GetColumn(PbtColumns.Metadata);
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

    public ITrieNodeLog.IWriteBatch StartWriteBatch(IColumnsWriteBatch<PbtColumns> batch, bool bypass)
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
        int gated = 0;
        try
        {
            for (; gated < _shards.Length; gated++) _shards[gated].EnterExclusive();
            if (Array.Exists(_shards, static shard => shard.HasGenerations))
                Parallel.ForEach(_shards, static shard => shard.DrainExclusive());
        }
        finally
        {
            for (int i = 0; i < gated; i++) _shards[i].ExitExclusive();
        }
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

    private sealed class View(TrieNodeLog log, IColumnDbSnapshot<PbtColumns> snapshot, ArrayPoolList<TrieNodeLogView> views) : ITrieNodeLog.IView
    {
        public IColumnDbSnapshot<PbtColumns> Snapshot => snapshot;

        public IReadOnlyKeyValueStore GetColumn(PbtColumns column) =>
            Covers(column) ? new Column(log, views, column, snapshot.GetColumn(column)) : snapshot.GetColumn(column);

        public void Dispose()
        {
            snapshot.Dispose();
            views.DisposeRecursive();
        }

        private sealed class Column(TrieNodeLog log, ArrayPoolList<TrieNodeLogView> views, PbtColumns column, IReadOnlyKeyValueStore inner) : IReadOnlyKeyValueStore
        {
            public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) =>
                TryGet(key, out byte[]? value) ? value : inner.Get(key, flags);

            public bool KeyExists(ReadOnlySpan<byte> key) =>
                TryGet(key, out byte[]? value) ? value is not null : inner.KeyExists(key);

            // A miss keeps RocksDB's zero-copy memory.
            public MemoryManager<byte>? GetOwnedMemory(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) =>
                TryGet(key, out byte[]? value) ? value is null or { Length: 0 } ? null : ArrayMemoryManager.From(value) : inner.GetOwnedMemory(key, flags);

            /// <summary>The first level, then its second-level shard, which follows the first-level views.</summary>
            private bool TryGet(ReadOnlySpan<byte> key, out byte[]? value)
            {
                int shard = log.ShardIndex(column, key);
                return views[shard].TryGet(key, out value)
                    || (log._secondLevelShards.Length != 0 && views[log._shards.Length + shard].TryGet(key, out value));
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
        private readonly IColumnsWriteBatch<PbtColumns> _rocksDbBatch;
        private readonly ArrayPoolList<TrieNodeLogWriteBatch> _batches;
        private readonly ArrayPoolList<ShardWriter> _writers;
        private bool _durable;
        private bool _committed;
        private bool _confirmed;

        public WriteBatch(TrieNodeLog log, IColumnsWriteBatch<PbtColumns> rocksDbBatch, ArrayPoolList<TrieNodeLogWriteBatch> batches)
        {
            _log = log;
            _rocksDbBatch = rocksDbBatch;
            _batches = batches;
            _writers = new ArrayPoolList<ShardWriter>(batches.Count);
            foreach (TrieNodeLogWriteBatch batch in batches) _writers.Add(new ShardWriter(batch, log._logger));
        }

        public IWriteBatch Wrap(PbtColumns column, IWriteBatch inner) => Covers(column) ? new Column(this, column) : inner;

        private void Stage(PbtColumns column, ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, bool delete) =>
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
            IWriteBatch metadata = _rocksDbBatch.GetColumnBatch(PbtColumns.Metadata);
            foreach (TrieNodeLogWriteBatch batch in _batches) batch.WriteVersion(metadata);
            _committed = true;
            Metrics.PbtTrieNodeLogCommitTime.Observe(Stopwatch.GetTimestamp() - sw);
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

        private sealed class Column(WriteBatch batch, PbtColumns column) : IWriteBatch
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
