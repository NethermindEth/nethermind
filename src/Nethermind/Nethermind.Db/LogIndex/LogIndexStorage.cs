using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Db.LogIndex
{
    // TODO: test on big-endian system or optimize for little-endian
    // TODO: use uint for block number?
    public partial class LogIndexStorage : ILogIndexStorage
    {
        private static class SpecialKey
        {
            public static readonly byte[] Version = "ver"u8.ToArray();
            public static readonly byte[] MinBlockNum = "min"u8.ToArray();
            public static readonly byte[] MaxBlockNum = "max"u8.ToArray();
            public static readonly byte[] CompressionAlgo = "alg"u8.ToArray();
        }

        public static class SpecialPostfix
        {
            // Any ordered prefix seeking will start on it
            public static readonly byte[] BackwardMerge = Enumerable.Repeat((byte)0, BlockNumSize).ToArray();

            // Any ordered prefix seeking will end on it.
            public static readonly byte[] ForwardMerge = Enumerable.Repeat(byte.MaxValue, BlockNumSize).ToArray();

            // Exclusive upper bound for iterator seek, so that ForwardMerge will be the last key
            public static readonly byte[] UpperBound = Enumerable.Repeat(byte.MaxValue, BlockNumSize).Concat([byte.MinValue]).ToArray();
        }

        private struct DbBatches : IDisposable
        {
            private bool _completed;

            private readonly IColumnsWriteBatch<LogIndexColumns> _batch;
            public IWriteBatch Meta { get; }
            public IWriteBatch Address { get; }
            public IWriteBatch[] Topics { get; }

            public DbBatches(IColumnsDb<LogIndexColumns> rootDb)
            {
                _batch = rootDb.StartWriteBatch();

                Meta = _batch.GetColumnBatch(LogIndexColumns.Meta);
                Address = _batch.GetColumnBatch(LogIndexColumns.Addresses);
                Topics = ArrayPool<IWriteBatch>.Shared.Rent(MaxTopics);
                Topics[0] = _batch.GetColumnBatch(LogIndexColumns.Topics0);
                Topics[1] = _batch.GetColumnBatch(LogIndexColumns.Topics1);
                Topics[2] = _batch.GetColumnBatch(LogIndexColumns.Topics2);
                Topics[3] = _batch.GetColumnBatch(LogIndexColumns.Topics3);
            }

            // Require explicit Commit call instead of committing on Dispose
            public void Commit()
            {
                if (_completed) return;
                _completed = true;

                _batch.Dispose();
            }

            public void Dispose()
            {
                ArrayPool<IWriteBatch>.Shared.Return(Topics);

                if (_completed) return;
                _completed = true;

                _batch.Clear();
                _batch.Dispose();
            }
        }

        private static readonly byte[] VersionBytes = [1];

        public const int MaxTopics = 4;

        public bool Enabled { get; }

        public const int BlockNumSize = sizeof(int);
        private const int MaxKeyLength = Hash256.Size + 1; // Math.Max(Address.Size, Hash256.Size)
        private const int MaxDbKeyLength = MaxKeyLength + BlockNumSize;

        private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Shared;

        private readonly IColumnsDb<LogIndexColumns> _rootDb;
        private readonly IDb _metaDb;
        private readonly IDb _addressDb;
        private readonly IDb[] _topicDbs;

        private IEnumerable<IDb> DBColumns
        {
            get
            {
                yield return _addressDb;

                foreach (IDb topicDb in _topicDbs)
                    yield return topicDb;
            }
        }

        private readonly ILogger _logger;

        private readonly int _maxReorgDepth;

        private readonly Dictionary<LogIndexColumns, MergeOperator> _mergeOperators;
        private readonly ICompressor _compressor;
        private readonly ICompactor _compactor;
        private readonly CompressionAlgorithm _compressionAlgorithm;

        private readonly Lock _rangeInitLock = new(); // May not be needed, but added for safety

        private int? _maxBlock;
        private int? _minBlock;

        public int? MaxBlockNumber => _maxBlock;
        public int? MinBlockNumber => _minBlock;

        private Exception? _lastBackgroundError;
        public bool HasBackgroundError => _lastBackgroundError is not null;

        /// <summary>
        /// Whether a first batch was already added.
        /// </summary>
        private bool WasInitialized => _minBlock is not null || _maxBlock is not null;

        /// <summary>
        /// Guarantees initialization won't be run concurrently
        /// </summary>
        private readonly SemaphoreSlim _initSemaphore = new(1, 1);

        /// <summary>
        /// Maps a syncing direction to semaphore.
        /// Used for blocking concurrent executions and
        /// ensuring the current iteration is completed before stopping/disposing.
        /// </summary>
        private readonly Dictionary<bool, SemaphoreSlim> _setReceiptsSemaphores = new()
        {
            { false, new(1, 1) },
            { true, new(1, 1) }
        };

        private bool _stopped;
        private bool _disposed;

        public LogIndexStorage(IDbFactory dbFactory, ILogManager logManager, ILogIndexConfig config)
        {
            try
            {
                Enabled = config.Enabled;

                _maxReorgDepth = config.MaxReorgDepth;

                _logger = logManager.GetClassLogger<LogIndexStorage>();

                _compressor = config.CompressionDistance > 0
                    ? new Compressor(this, config.CompressionDistance, config.MaxCompressionParallelism)
                    : new NoOpCompressor();

                _compactor = config.CompactionDistance > 0
                    ? new Compactor(this, _logger, config.CompactionDistance)
                    : new NoOpCompactor();

                _mergeOperators = new()
                {
                    { LogIndexColumns.Addresses, new(this, _compressor, topicIndex: null) },
                    { LogIndexColumns.Topics0, new(this, _compressor, topicIndex: 0) },
                    { LogIndexColumns.Topics1, new(this, _compressor, topicIndex: 1) },
                    { LogIndexColumns.Topics2, new(this, _compressor, topicIndex: 2) },
                    { LogIndexColumns.Topics3, new(this, _compressor, topicIndex: 3) }
                };

                _rootDb = CreateRootDb(dbFactory, config.Reset);
                _metaDb = GetMetaDb(_rootDb);
                _addressDb = _rootDb.GetColumnDb(LogIndexColumns.Addresses);
                _topicDbs = _mergeOperators.Keys.Where(cl => $"{cl}".Contains("Topic")).Select(cl => _rootDb.GetColumnDb(cl)).ToArray();
                _compressionAlgorithm = SelectCompressionAlgo(config.CompressionAlgorithm);

                (_minBlock, _maxBlock) = (LoadRangeBound(SpecialKey.MinBlockNum), LoadRangeBound(SpecialKey.MaxBlockNum));

                if (Enabled)
                    _compressor.Start();
            }
            catch // TODO: do not throw errors from constructor?
            {
                DisposeCore();
                throw;
            }
        }

        private IColumnsDb<LogIndexColumns> CreateRootDb(IDbFactory dbFactory, bool reset)
        {
            (IColumnsDb<LogIndexColumns> root, IDb meta) = CreateDb();

            if (reset)
                return ResetAndCreateNew(root, "Log index storage: resetting data per configuration...");

            var versionBytes = meta.Get(SpecialKey.Version);
            if (versionBytes is null) // DB is empty
            {
                meta.Set(SpecialKey.Version, VersionBytes);
                return root;
            }

            return versionBytes.SequenceEqual(VersionBytes)
                ? root
                : ResetAndCreateNew(root, $"Log index storage: version is incorrect: {versionBytes[0]} < {VersionBytes}, resetting data...");

            IColumnsDb<LogIndexColumns> ResetAndCreateNew(IColumnsDb<LogIndexColumns> db, string message)
            {
                if (_logger.IsWarn)
                    _logger.Warn(message);

                db.Clear();

                // `Clear` removes the DB folder, need to create a new instance
                db.Dispose();
                (db, meta) = CreateDb();

                meta.Set(SpecialKey.Version, VersionBytes);
                return db;
            }

            (IColumnsDb<LogIndexColumns> root, IDb meta) CreateDb()
            {
                IColumnsDb<LogIndexColumns> db = dbFactory.CreateColumnsDb<LogIndexColumns>(new("logIndexStorage", DbNames.LogIndex)
                {
                    ColumnsMergeOperators = _mergeOperators.ToDictionary(x => $"{x.Key}", x => (IMergeOperator)x.Value)
                });

                return (db, GetMetaDb(db));
            }
        }

        private CompressionAlgorithm SelectCompressionAlgo(string? configAlgoName)
        {
            IDb meta = GetMetaDb(_rootDb);

            CompressionAlgorithm? configAlgo = null;
            if (configAlgoName is not null && !CompressionAlgorithm.Supported.TryGetValue(configAlgoName, out configAlgo))
            {
                throw new NotSupportedException(
                    $"Configured compression algorithm ({configAlgoName}) is not supported on this platform."
                );
            }

            var algoBytes = meta.Get(SpecialKey.CompressionAlgo);
            if (algoBytes is null) // DB is empty
            {
                KeyValuePair<string, CompressionAlgorithm> selected = configAlgo is not null
                    ? KeyValuePair.Create(configAlgoName, configAlgo)
                    : CompressionAlgorithm.Best;

                meta.Set(SpecialKey.CompressionAlgo, Encoding.ASCII.GetBytes(selected.Key));
                return selected.Value;
            }

            var usedAlgoName = Encoding.ASCII.GetString(algoBytes);
            if (!CompressionAlgorithm.Supported.TryGetValue(usedAlgoName, out CompressionAlgorithm usedAlgo))
            {
                throw new NotSupportedException(
                    $"Used compression algorithm ({usedAlgoName}) is not supported on this platform. " +
                    "Log index must be reset to use a different compression algorithm."
                );
            }

            configAlgoName ??= usedAlgoName;
            if (usedAlgoName != configAlgoName)
            {
                throw new NotSupportedException(
                    $"Used compression algorithm ({usedAlgoName}) is different from the one configured ({configAlgoName}). " +
                    "Log index must be reset to use a different compression algorithm."
                );
            }

            return usedAlgo;
        }

        private static void ForceMerge(IDb db)
        {
            // Fetching RocksDB key values forces it to merge corresponding parts
            db.GetAllValues().ForEach(static _ => { });
        }

        public async Task StopAsync()
        {
            if (_stopped)
                return;

            await _setReceiptsSemaphores[false].WaitAsync();
            await _setReceiptsSemaphores[true].WaitAsync();

            try
            {
                if (_stopped)
                    return;

                _stopped = true;

                // Disposing RocksDB during any write operation will cause 0xC0000005
                await Task.WhenAll(
                    _compactor.StopAsync(),
                    _compressor.StopAsync()
                );

                if (_logger.IsInfo) _logger.Info("Log index storage stopped");
            }
            finally
            {
                _setReceiptsSemaphores[false].Release();
                _setReceiptsSemaphores[true].Release();
            }
        }

        private void ThrowIfStopped()
        {
            if (_stopped)
                throw new InvalidOperationException("Log index storage is stopped.");
        }

        private void OnBackgroundError<TCaller>(Exception error)
        {
            _lastBackgroundError = error;

            if (_logger.IsError)
                _logger.Error($"Error in {typeof(TCaller).Name}", error);
        }

        private void ThrowIfHasError()
        {
            if (_lastBackgroundError is { } error)
                ExceptionDispatchInfo.Throw(error);
        }

        async ValueTask IAsyncDisposable.DisposeAsync()
        {
            if (_disposed)
                return;

            await StopAsync();

            await _setReceiptsSemaphores[false].WaitAsync();
            await _setReceiptsSemaphores[true].WaitAsync();

            if (_disposed)
                return;

            _disposed = true;

            DisposeCore();
        }

        private void DisposeCore()
        {
            _setReceiptsSemaphores[false].Dispose();
            _setReceiptsSemaphores[true].Dispose();
            _compressor?.Dispose();
            DBColumns?.DisposeItems();
            _rootDb?.Dispose();
        }

        private int? LoadRangeBound(ReadOnlySpan<byte> key)
        {
            var value = _metaDb.Get(key);
            return value is { Length: > 0 } ? GetValBlockNum(value) : null;
        }

        private void UpdateRange(int minBlock, int maxBlock, bool isBackwardSync)
        {
            if (!WasInitialized)
            {
                using Lock.Scope _ = _rangeInitLock.EnterScope();
                (_minBlock, _maxBlock) = (minBlock, maxBlock);
                return;
            }

            // Update fields separately for each direction
            // so that concurrent different direction sync won't overwrite each other
            if (isBackwardSync) _minBlock = minBlock;
            else _maxBlock = maxBlock;
        }

        private static int SaveRangeBound(IWriteOnlyKeyValueStore dbBatch, byte[] key, int value)
        {
            var bufferArr = Pool.Rent(BlockNumSize);
            Span<byte> buffer = bufferArr.AsSpan(..BlockNumSize);

            try
            {
                SetValBlockNum(buffer, value);
                dbBatch.PutSpan(key, buffer);
                return value;
            }
            finally
            {
                Pool.Return(bufferArr);
            }
        }

        private (int min, int max) SaveRange(DbBatches batches, int firstBlock, int lastBlock, bool isBackwardSync, bool isReorg = false)
        {
            var batchMin = Math.Min(firstBlock, lastBlock);
            var batchMax = Math.Max(firstBlock, lastBlock);

            var min = _minBlock ?? SaveRangeBound(batches.Meta, SpecialKey.MinBlockNum, batchMin);
            var max = _maxBlock ?? SaveRangeBound(batches.Meta, SpecialKey.MaxBlockNum, batchMax);

            if (isBackwardSync)
            {
                if (isReorg)
                    throw new ArgumentException("Backwards sync does not support reorgs.");
                if (batchMin < _minBlock)
                    min = SaveRangeBound(batches.Meta, SpecialKey.MinBlockNum, batchMin);
            }
            else
            {
                if ((isReorg && batchMax < _maxBlock) || (!isReorg && batchMax > _maxBlock))
                    max = SaveRangeBound(batches.Meta, SpecialKey.MaxBlockNum, batchMax);
            }

            return (min, max);
        }

        private int? GetLastReorgableBlockNumber() => _maxBlock - _maxReorgDepth;

        private static bool IsBlockNewer(int next, int? lastMin, int? lastMax, bool isBackwardSync) => isBackwardSync
            ? lastMin is null || next < lastMin
            : lastMax is null || next > lastMax;

        private bool IsBlockNewer(int next, bool isBackwardSync) =>
            IsBlockNewer(next, _minBlock, _maxBlock, isBackwardSync);

        public string GetDbSize() => _rootDb.GatherMetric().Size.SizeToString(useSi: true, addSpace: true);

        public IEnumerator<int> GetEnumerator(Address address, int from, int to) =>
            GetEnumerator(null, address.Bytes, from, to);

        public IEnumerator<int> GetEnumerator(int index, Hash256 topic, int from, int to) =>
            GetEnumerator(index, topic.BytesToArray(), from, to);

        public IEnumerator<int> GetEnumerator(int? index, byte[] key, int from, int to)
        {
            IDb db = GetDb(index);
            ISortedKeyValueStore? sortedDb = db as ISortedKeyValueStore
                ?? throw new NotSupportedException($"{db.GetType().Name} DB does not support sorted lookups.");

            return new LogIndexEnumerator(this, sortedDb, key, from, to);
        }

        // TODO: discuss potential optimizations
        public LogIndexAggregate Aggregate(IReadOnlyList<BlockReceipts> batch, bool isBackwardSync, LogIndexUpdateStats? stats)
        {
            ThrowIfStopped();
            ThrowIfHasError();

            if ((!isBackwardSync && !IsSeqAsc(batch)) || (isBackwardSync && !IsSeqDesc(batch)))
                throw new ArgumentException($"Unexpected blocks batch order: ({batch[0]} to {batch[^1]}).");

            if (!IsBlockNewer(batch[^1].BlockNumber, isBackwardSync))
                return new(batch);

            var timestamp = Stopwatch.GetTimestamp();

            var aggregate = new LogIndexAggregate(batch);
            foreach ((var blockNumber, TxReceipt[] receipts) in batch)
            {
                if (!IsBlockNewer(blockNumber, isBackwardSync))
                    continue;

                stats?.IncrementBlocks();
                stats?.IncrementTx(receipts.Length);

                foreach (TxReceipt receipt in receipts)
                {
                    if (receipt.Logs == null)
                        continue;

                    foreach (LogEntry log in receipt.Logs)
                    {
                        stats?.IncrementLogs();

                        List<int> addressNums = aggregate.Address.GetOrAdd(log.Address, static _ => new(1));

                        if (addressNums.Count == 0 || addressNums[^1] != blockNumber)
                            addressNums.Add(blockNumber);

                        var topicsLength = Math.Min(log.Topics.Length, MaxTopics);
                        for (byte topicIndex = 0; topicIndex < topicsLength; topicIndex++)
                        {
                            stats?.IncrementTopics();

                            List<int> topicNums = aggregate.Topic[topicIndex].GetOrAdd(log.Topics[topicIndex], static _ => new(1));

                            if (topicNums.Count == 0 || topicNums[^1] != blockNumber)
                                topicNums.Add(blockNumber);
                        }
                    }
                }
            }

            stats?.KeysCount.Include(aggregate.Address.Count + aggregate.TopicCount);
            stats?.Aggregating.Include(Stopwatch.GetElapsedTime(timestamp));

            return aggregate;
        }

        private async ValueTask LockRunAsync(SemaphoreSlim semaphore)
        {
            if (!await semaphore.WaitAsync(TimeSpan.Zero, CancellationToken.None))
            {
                ThrowIfStopped();
                throw new InvalidOperationException($"{nameof(LogIndexStorage)} does not support concurrent invocations in the same direction.");
            }
        }

        public async Task ReorgFrom(BlockReceipts block)
        {
            ThrowIfStopped();
            ThrowIfHasError();

            if (!WasInitialized)
                return;

            const bool isBackwardSync = false;

            SemaphoreSlim semaphore = _setReceiptsSemaphores[isBackwardSync];
            await LockRunAsync(semaphore);

            byte[]? keyArray = null, valueArray = null;

            try
            {
                keyArray = Pool.Rent(MaxDbKeyLength);
                valueArray = Pool.Rent(BlockNumSize + 1);

                using IColumnsWriteBatch<LogIndexColumns>? batch = _rootDb.StartWriteBatch();

                using var batches = new DbBatches(_rootDb);

                Span<byte> dbValue = MergeOps.Create(MergeOp.Reorg, block.BlockNumber, valueArray);

                foreach (TxReceipt receipt in block.Receipts)
                {
                    foreach (LogEntry log in receipt.Logs ?? [])
                    {
                        ReadOnlySpan<byte> addressKey = CreateMergeDbKey(log.Address.Bytes, keyArray, isBackwardSync: false);
                        batches.Address.Merge(addressKey, dbValue);

                        var topicsLength = Math.Min(log.Topics.Length, MaxTopics);
                        for (var topicIndex = 0; topicIndex < topicsLength; topicIndex++)
                        {
                            Hash256 topic = log.Topics[topicIndex];
                            ReadOnlySpan<byte> topicKey = CreateMergeDbKey(topic.Bytes, keyArray, isBackwardSync: false);
                            batches.Topics[topicIndex].Merge(topicKey, dbValue);
                        }
                    }
                }

                // Need to update the last block number so that new-receipts comparison won't fail when rewriting it
                var blockNum = block.BlockNumber - 1;

                var (minBlock, maxBlock) = SaveRange(batches, blockNum, blockNum, isBackwardSync, isReorg: true);

                batches.Commit();

                // Postpone values update until batch is commited
                UpdateRange(minBlock, maxBlock, isBackwardSync);
            }
            finally
            {
                semaphore.Release();

                if (keyArray is not null) Pool.Return(keyArray);
                if (valueArray is not null) Pool.Return(valueArray);
            }
        }

        // TODO: refactor compaction to explicitly compress full range for each involved key
        public async Task CompactAsync(bool flush = false, int mergeIterations = 0, LogIndexUpdateStats? stats = null)
        {
            ThrowIfStopped();
            ThrowIfHasError();

            if (_logger.IsInfo)
                _logger.Info($"Log index forced compaction started, DB size: {GetDbSize()}");

            var timestamp = Stopwatch.GetTimestamp();

            if (flush)
                DBColumns.ForEach(static db => db.Flush());

            for (var i = 0; i < mergeIterations; i++)
            {
                Task[] tasks = DBColumns
                    .Select(static db => Task.Run(() => ForceMerge(db)))
                    .ToArray();

                await Task.WhenAll(tasks);
                await _compressor.WaitUntilEmptyAsync(TimeSpan.FromSeconds(30));
            }

            CompactingStats compactStats = await _compactor.ForceAsync();
            stats?.Compacting.Combine(compactStats);

            foreach (MergeOperator mergeOperator in _mergeOperators.Values)
                stats?.Combine(mergeOperator.Stats);

            if (_logger.IsInfo)
                _logger.Info($"Log index forced compaction finished in {Stopwatch.GetElapsedTime(timestamp)}, DB size: {GetDbSize()} {stats:d}");
        }

        public async Task SetReceiptsAsync(LogIndexAggregate aggregate, LogIndexUpdateStats? stats = null)
        {
            ThrowIfStopped();
            ThrowIfHasError();

            long totalTimestamp = Stopwatch.GetTimestamp();

            var isBackwardSync = aggregate.LastBlockNum < aggregate.FirstBlockNum;
            SemaphoreSlim semaphore = _setReceiptsSemaphores[isBackwardSync];
            await LockRunAsync(semaphore);

            var wasInitialized = WasInitialized;
            if (!wasInitialized)
                await _initSemaphore.WaitAsync();

            try
            {
                using var batches = new DbBatches(_rootDb);

                // Add values to batches
                long timestamp;
                if (!aggregate.IsEmpty)
                {
                    timestamp = Stopwatch.GetTimestamp();

                    // Add addresses
                    foreach ((Address address, List<int> blockNums) in aggregate.Address)
                    {
                        SaveBlockNumbersByKey(batches.Address, address.Bytes, blockNums, isBackwardSync, stats);
                    }

                    // Add topics
                    for (var topicIndex = 0; topicIndex < aggregate.Topic.Length; topicIndex++)
                    {
                        Dictionary<Hash256, List<int>> topics = aggregate.Topic[topicIndex];

                        foreach ((Hash256 topic, List<int> blockNums) in topics)
                            SaveBlockNumbersByKey(batches.Topics[topicIndex], topic.Bytes, blockNums, isBackwardSync, stats);
                    }

                    stats?.Processing.Include(Stopwatch.GetElapsedTime(timestamp));
                }

                timestamp = Stopwatch.GetTimestamp();
                var (addressRange, topicRanges) = SaveRange(batches, aggregate.FirstBlockNum, aggregate.LastBlockNum, isBackwardSync);
                stats?.UpdatingMeta.Include(Stopwatch.GetElapsedTime(timestamp));

                // Submit batches
                timestamp = Stopwatch.GetTimestamp();
                batches.Commit();
                stats?.CommitingBatch.Include(Stopwatch.GetElapsedTime(timestamp));

                UpdateRange(addressRange, topicRanges, isBackwardSync);

                // Enqueue compaction if needed
                _compactor.TryEnqueue();
            }
            finally
            {
                if (!wasInitialized)
                    _initSemaphore.Release();

                semaphore.Release();
            }

            foreach (MergeOperator mergeOperator in _mergeOperators.Values)
                stats?.Combine(mergeOperator.GetAndResetStats());
            stats?.PostMergeProcessing.Combine(_compressor.GetAndResetStats());
            stats?.Compacting.Combine(_compactor.GetAndResetStats());
            stats?.SetReceipts.Include(Stopwatch.GetElapsedTime(totalTimestamp));
        }

        public Task SetReceiptsAsync(IReadOnlyList<BlockReceipts> batch, bool isBackwardSync, LogIndexUpdateStats? stats = null)
        {
            LogIndexAggregate aggregate = Aggregate(batch, isBackwardSync, stats);
            return SetReceiptsAsync(aggregate, stats);
        }

        protected virtual void SaveBlockNumbersByKey(
            IWriteBatch dbBatch, ReadOnlySpan<byte> key, IReadOnlyList<int> blockNums,
            bool isBackwardSync, LogIndexUpdateStats? stats
        )
        {
            var dbKeyArray = Pool.Rent(MaxDbKeyLength);

            try
            {
                ReadOnlySpan<byte> dbKey = CreateMergeDbKey(key, dbKeyArray, isBackwardSync);

                var newValue = CreateDbValue(blockNums);

                var timestamp = Stopwatch.GetTimestamp();

                if (newValue is null or [])
                    throw new LogIndexStateException("No block numbers to save.", key);

                // TODO: consider disabling WAL, but check:
                // - FlushOnTooManyWrites
                // - atomic flushing
                dbBatch.Merge(dbKey, newValue);
                stats?.CallingMerge.Include(Stopwatch.GetElapsedTime(timestamp));
            }
            finally
            {
                Pool.Return(dbKeyArray);
            }
        }

        private static ReadOnlySpan<byte> WriteKey(ReadOnlySpan<byte> key, Span<byte> buffer)
        {
            key.CopyTo(buffer);
            return buffer[..key.Length];
        }

        private static ReadOnlySpan<byte> ExtractKey(ReadOnlySpan<byte> dbKey) => dbKey[..^BlockNumSize];

        /// <summary>
        /// Generates a key consisting of the <c>key || block-number</c> byte array.
        /// </summary>/
        private static ReadOnlySpan<byte> CreateDbKey(ReadOnlySpan<byte> key, int blockNumber, Span<byte> buffer)
        {
            key = WriteKey(key, buffer);
            SetKeyBlockNum(buffer[key.Length..], blockNumber);

            var length = key.Length + BlockNumSize;
            return buffer[..length];
        }

        /// <summary>
        /// Generates a key consisting of the <c>key || block-number</c> byte array.
        /// </summary>/
        private static ReadOnlySpan<byte> CreateDbKey(ReadOnlySpan<byte> key, ReadOnlySpan<byte> blockNumber, Span<byte> buffer)
        {
            key = WriteKey(key, buffer);
            blockNumber.CopyTo(buffer[key.Length..]);

            var length = key.Length + blockNumber.Length;
            return buffer[..length];
        }

        private static ReadOnlySpan<byte> CreateMergeDbKey(ReadOnlySpan<byte> key, Span<byte> buffer, bool isBackwardSync) =>
            CreateDbKey(key, isBackwardSync ? SpecialPostfix.BackwardMerge : SpecialPostfix.ForwardMerge, buffer);

        // RocksDB uses big-endian (lexicographic) ordering
        // +1 is needed as 0 is used for the backward-merge key
        private static void SetKeyBlockNum(Span<byte> dbKeyEnd, int blockNumber) => BinaryPrimitives.WriteInt32BigEndian(dbKeyEnd, blockNumber + 1);

        private static bool UseBackwardSyncFor(ReadOnlySpan<byte> dbKey) => dbKey.EndsWith(SpecialPostfix.BackwardMerge);

        private static int BinarySearch(ReadOnlySpan<int> blocks, int from)
        {
            int index = blocks.BinarySearch(from);
            return index < 0 ? ~index : index;
        }

        private ReadOnlySpan<byte> Compress(Span<byte> data, Span<byte> buffer)
        {
            ReadOnlySpan<int> blockNumbers = MemoryMarshal.Cast<byte, int>(data);
            var length = (int)_compressionAlgorithm.Compress(blockNumbers, (nuint)blockNumbers.Length, buffer);
            return buffer[..length];
        }

        private static int ReadCompressionMarker(ReadOnlySpan<byte> source) => -BinaryPrimitives.ReadInt32LittleEndian(source);
        private static void WriteCompressionMarker(Span<byte> source, int len) => BinaryPrimitives.WriteInt32LittleEndian(source, -len);

        private static bool IsCompressed(ReadOnlySpan<byte> source, out int len)
        {
            if (source.Length == 0)
            {
                len = 0;
                return false;
            }

            len = ReadCompressionMarker(source);
            return len > 0;
        }

        private static void SetValBlockNum(Span<byte> destination, int blockNum) => BinaryPrimitives.WriteInt32LittleEndian(destination, blockNum);
        private static int GetValBlockNum(ReadOnlySpan<byte> source) => BinaryPrimitives.ReadInt32LittleEndian(source);
        private static int GetValLastBlockNum(ReadOnlySpan<byte> source) => GetValBlockNum(source[^BlockNumSize..]);

        private static void SetValBlockNums(Span<byte> destination, IEnumerable<int> blockNums)
        {
            var shift = 0;
            foreach (var blockNum in blockNums)
            {
                SetValBlockNum(destination[shift..], blockNum);
                shift += BlockNumSize;
            }
        }

        private static void ReadBlockNums(ReadOnlySpan<byte> source, Span<int> buffer)
        {
            if (source.Length % BlockNumSize != 0)
                throw new LogIndexStateException("Invalid length for array of block numbers.");

            if (buffer.Length < source.Length / BlockNumSize)
                throw new ArgumentException($"Buffer is too small to hold {source.Length / BlockNumSize} block numbers.", nameof(buffer));

            if (BitConverter.IsLittleEndian)
            {
                ReadOnlySpan<int> sourceInt = MemoryMarshal.Cast<byte, int>(source);
                sourceInt.CopyTo(buffer);
            }
            else
            {
                for (var i = 0; i < source.Length; i += BlockNumSize)
                    buffer[i / BlockNumSize] = GetValBlockNum(source[i..]);
            }
        }

        private static byte[] CreateDbValue(IReadOnlyList<int> blockNums)
        {
            var value = new byte[blockNums.Count * BlockNumSize];
            SetValBlockNums(value, blockNums);
            return value;
        }

        private IDb GetDb(int? topicIndex) => topicIndex.HasValue ? _topicDbs[topicIndex.Value] : _addressDb;

        private static IDb GetMetaDb(IColumnsDb<LogIndexColumns> rootDb) => rootDb.GetColumnDb(LogIndexColumns.Meta);

        private byte[] CompressDbValue(ReadOnlySpan<byte> key, Span<byte> data)
        {
            if (IsCompressed(data, out _))
                throw new LogIndexStateException("Attempt to compress already compressed data.", key);
            if (data.Length % BlockNumSize != 0)
                throw new LogIndexStateException($"Invalid length of data to compress: {data.Length}.", key);

            var buffer = Pool.Rent(data.Length + BlockNumSize);

            try
            {
                WriteCompressionMarker(buffer, data.Length / BlockNumSize);
                var compressedLen = Compress(data, buffer.AsSpan(BlockNumSize..)).Length;
                return buffer[..(BlockNumSize + compressedLen)];
            }
            finally
            {
                Pool.Return(buffer);
            }
        }

        private void DecompressDbValue(ReadOnlySpan<byte> data, Span<int> buffer)
        {
            if (!IsCompressed(data, out int len))
                throw new ValidationException("Data is not compressed");

            if (buffer.Length < len)
                throw new ArgumentException($"Buffer is too small to decompress {len} block numbers.", nameof(buffer));

            _ = _compressionAlgorithm.Decompress(data[BlockNumSize..], (nuint)len, buffer);
        }

        private Span<byte> RemoveReorgableBlocks(Span<byte> data)
        {
            if (GetLastReorgableBlockNumber() is not { } lastCompressBlock)
                return Span<byte>.Empty;

            var lastCompressIndex = LastBlockSearch(data, lastCompressBlock, false);

            if (lastCompressIndex < 0) lastCompressIndex = 0;
            if (lastCompressIndex > data.Length) lastCompressIndex = data.Length;

            return data[..lastCompressIndex];
        }

        private static void ReverseBlocksIfNeeded(Span<byte> data)
        {
            if (data.Length != 0 && GetValBlockNum(data) > GetValLastBlockNum(data))
                MemoryMarshal.Cast<byte, int>(data).Reverse();
        }

        private static void ReverseBlocksIfNeeded(Span<int> blocks)
        {
            if (blocks.Length != 0 && blocks[0] > blocks[^1])
                blocks.Reverse();
        }

        private static int LastBlockSearch(ReadOnlySpan<byte> operand, int block, bool isBackward)
        {
            if (operand.IsEmpty)
                return 0;

            var i = operand.Length - BlockNumSize;
            for (; i >= 0; i -= BlockNumSize)
            {
                var currentBlock = GetValBlockNum(operand[i..]);
                if (currentBlock == block)
                    return i;

                if (isBackward)
                {
                    if (currentBlock > block)
                        return i + BlockNumSize;
                }
                else
                {
                    if (currentBlock < block)
                        return i + BlockNumSize;
                }
            }

            return i;
        }

        private static bool IsSeqAsc(IReadOnlyList<BlockReceipts> blocks)
        {
            int j = blocks.Count - 1;
            int i = 1, d = blocks[0].BlockNumber;
            while (i <= j && blocks[i].BlockNumber - i == d) i++;
            return i > j;
        }

        private static bool IsSeqDesc(IReadOnlyList<BlockReceipts> blocks)
        {
            int j = blocks.Count - 1;
            int i = 1, d = blocks[0].BlockNumber;
            while (i <= j && blocks[i].BlockNumber + i == d) i++;
            return i > j;
        }
    }
}
