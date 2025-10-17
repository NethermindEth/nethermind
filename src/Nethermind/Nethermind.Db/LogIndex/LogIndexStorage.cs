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
    // TODO: test on big-endian system
    public partial class LogIndexStorage : ILogIndexStorage
    {
        // Use values that we won't encounter in the middle of a regular iteration
        private static class SpecialKey
        {
            public static readonly byte[] Version = Enumerable.Repeat(byte.MaxValue, MaxDbKeyLength)
                .Concat(new byte[] { 0 }).ToArray();

            public static readonly byte[] MinBlockNum = Enumerable.Repeat(byte.MaxValue, MaxDbKeyLength)
                .Concat(new byte[] { 1 }).ToArray();

            public static readonly byte[] MaxBlockNum = Enumerable.Repeat(byte.MaxValue, MaxDbKeyLength)
                .Concat(new byte[] { 2 }).ToArray();

            public static readonly byte[] CompressionAlgo = Enumerable.Repeat(byte.MaxValue, MaxDbKeyLength)
                .Concat(new byte[] { 3 }).ToArray();
        }

        private static class SpecialPostfix
        {
            // Any ordered prefix seeking will start on it
            public static readonly byte[] BackwardMerge = Enumerable.Repeat((byte)0, BlockNumSize).ToArray();

            // Any ordered prefix seeking will end on it.
            public static readonly byte[] ForwardMerge = Enumerable.Repeat(byte.MaxValue, BlockNumSize).ToArray();
        }

        private struct DbBatches : IDisposable
        {
            private bool _completed;

            public IWriteBatch Address { get; }
            public IWriteBatch[] Topics { get; }

            public DbBatches(IDb addressDb, IDb[] topicsDbs)
            {
                Address = addressDb.StartWriteBatch();

                Topics = ArrayPool<IWriteBatch>.Shared.Rent(MaxTopics);
                for (var i = 0; i < topicsDbs.Length; i++)
                    Topics[i] = topicsDbs[i].StartWriteBatch();
            }

            public void Commit()
            {
                if (_completed) return;
                _completed = true;

                Address.Dispose();

                for (var i = 0; i < MaxTopics; i++)
                    Topics[i].Dispose();
            }

            public void Dispose()
            {
                if (_completed) return;
                _completed = true;

                Address.Clear();
                Address.Dispose();

                for (var i = 0; i < MaxTopics; i++)
                {
                    Topics[i].Clear();
                    Topics[i].Dispose();
                }

                ArrayPool<IWriteBatch>.Shared.Return(Topics);
            }
        }

        private static readonly byte[] VersionBytes = [1];

        public const int MaxTopics = 4;

        public bool Enabled { get; }

        private const int BlockNumSize = sizeof(int);
        private const int MaxKeyLength = Hash256.Size + 1; // Math.Max(Address.Size, Hash256.Size)
        private const int MaxDbKeyLength = MaxKeyLength + BlockNumSize;

        private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Shared;

        private readonly IColumnsDb<LogIndexColumns> _rootDb;
        private readonly IDb _addressDb;
        private readonly IDb[] _topicDbs;
        private IEnumerable<IDb> DBColumns => new[] { _addressDb }.Concat(_topicDbs);

        private readonly ILogger _logger;

        private readonly int _maxReorgDepth;

        private readonly Dictionary<LogIndexColumns, MergeOperator> _mergeOperators;
        private readonly ICompressor _compressor;
        private readonly ICompactor _compactor;
        private readonly CompressionAlgorithm _compressionAlgorithm;

        private readonly Lock _rangeInitLock = new(); // May not be needed, but added for safety
        private int? _addressMaxBlock;
        private int? _addressMinBlock;
        private int?[] _topicMinBlocks;
        private int?[] _topicMaxBlocks;

        private Exception? _lastBackgroundError;
        public bool HasBackgroundError => _lastBackgroundError is not null;

        /// <summary>
        /// Whether a first batch was already added.
        /// </summary>
        private bool WasInitialized => _addressMinBlock is not null || _topicMinBlocks.Min() is not null;

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
                _addressDb = _rootDb.GetColumnDb(LogIndexColumns.Addresses);
                _topicDbs = _mergeOperators.Keys.Where(cl => $"{cl}".Contains("Topic")).Select(cl => _rootDb.GetColumnDb(cl)).ToArray();
                _compressionAlgorithm = SelectCompressionAlgo(config.CompressionAlgorithm);

                _addressMaxBlock = LoadRangeBound(_addressDb, SpecialKey.MaxBlockNum);
                _addressMinBlock = LoadRangeBound(_addressDb, SpecialKey.MinBlockNum);
                _topicMaxBlocks = _topicDbs.Select(static db => LoadRangeBound(db, SpecialKey.MaxBlockNum)).ToArray();
                _topicMinBlocks = _topicDbs.Select(static db => LoadRangeBound(db, SpecialKey.MinBlockNum)).ToArray();

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
            // Using tailing iterator may cause invalid merge order, TODO: raise an issue
            using IIterator iterator = db.GetIterator(ordered: true);

            // Iterator seeking forces RocksDB to merge corresponding key values
            iterator.SeekToFirst();
            while (iterator.Valid())
            {
                _ = iterator.Value();
                iterator.Next();
            }
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

        // TODO: stop the storage?
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

        private static int? LoadRangeBound(IDb db, byte[] key)
        {
            var value = db.Get(key);
            return value is { Length: > 1 } ? GetValBlockNum(value) : null;
        }

        private void UpdateRanges((int min, int max) addressRange, (int?[] min, int?[] max) topicRanges, bool isBackwardSync)
        {
            if (!WasInitialized)
            {
                using Lock.Scope _ = _rangeInitLock.EnterScope();
                (_addressMinBlock, _addressMaxBlock) = addressRange;
                (_topicMinBlocks, _topicMaxBlocks) = topicRanges;
                return;
            }

            if (isBackwardSync)
                (_addressMinBlock, _topicMinBlocks) = (addressRange.min, topicRanges.min);
            else
                (_addressMaxBlock, _topicMaxBlocks) = (addressRange.max, topicRanges.max);
        }

        private static int SaveRangeBound(IWriteOnlyKeyValueStore dbBatch, byte[] key, int value)
        {
            var bufferArr = Pool.Rent(BlockNumSize);
            Span<byte> buffer = bufferArr.AsSpan(BlockNumSize);

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

        private static (int min, int max) SaveRange(IWriteOnlyKeyValueStore dbBatch, int batchFirst, int batchLast,
            int? lastMin, int? lastMax, bool isBackwardSync, bool isReorg)
        {
            var batchMin = Math.Min(batchFirst, batchLast);
            var batchMax = Math.Max(batchFirst, batchLast);

            var min = lastMin ?? SaveRangeBound(dbBatch, SpecialKey.MinBlockNum, batchMin);
            var max = lastMax ?? SaveRangeBound(dbBatch, SpecialKey.MaxBlockNum, batchMax);

            if (!isBackwardSync)
            {
                if ((isReorg && batchMax < lastMax) || (!isReorg && batchMax > lastMax))
                    max = SaveRangeBound(dbBatch, SpecialKey.MaxBlockNum, batchMax);
            }
            else
            {
                if (isReorg)
                    throw new ArgumentException("Backwards sync does not support reorgs.");
                if (batchMin < lastMin)
                    min = SaveRangeBound(dbBatch, SpecialKey.MinBlockNum, batchMin);
            }

            return (min, max);
        }

        private ((int min, int max) address, (int?[] min, int?[] max) topics) SaveRanges(
            DbBatches batches, int firstBlock, int lastBlock, bool isBackwardSync, bool isReorg = false
        )
        {
            (int min, int max) addressRange =
                SaveRange(batches.Address, firstBlock, lastBlock, _addressMinBlock, _addressMaxBlock, isBackwardSync, isReorg);

            (int?[] min, int?[] max) topicRanges = (min: _topicMinBlocks.ToArray(), max: _topicMaxBlocks.ToArray());
            for (var i = 0; i < MaxTopics; i++)
            {
                (topicRanges.min[i], topicRanges.max[i]) =
                    SaveRange(batches.Topics[i], firstBlock, lastBlock, _topicMinBlocks[i], _topicMaxBlocks[i], isBackwardSync, isReorg);
            }

            return (addressRange, topicRanges);
        }

        private int GetLastReorgableBlockNumber() => Math.Min(_addressMaxBlock ?? 0, _topicMaxBlocks.Min() ?? 0) - _maxReorgDepth;

        private static bool IsBlockNewer(int next, int? lastMin, int? lastMax, bool isBackwardSync) => isBackwardSync
            ? lastMin is null || next < lastMin
            : lastMax is null || next > lastMax;

        private bool IsAddressBlockNewer(int next, bool isBackwardSync) => IsBlockNewer(next, _addressMinBlock, _addressMaxBlock, isBackwardSync);
        private bool IsTopicBlockNewer(int topicIndex, int next, bool isBackwardSync) => IsBlockNewer(next, _topicMinBlocks[topicIndex], _topicMaxBlocks[topicIndex], isBackwardSync);

        private bool IsBlockNewer(int next, bool isBackwardSync) =>
            IsAddressBlockNewer(next, isBackwardSync) ||
            IsTopicBlockNewer(0, next, isBackwardSync) ||
            IsTopicBlockNewer(1, next, isBackwardSync) ||
            IsTopicBlockNewer(2, next, isBackwardSync) ||
            IsTopicBlockNewer(3, next, isBackwardSync);

        public int? GetMaxBlockNumber() => _addressMaxBlock is { } addressMaxBlock && _topicMaxBlocks.Min() is { } topicMaxBlock
            ? Math.Min(addressMaxBlock, topicMaxBlock)
            : null;

        public int? GetMinBlockNumber() => _addressMinBlock is { } addressMinBlock && _topicMinBlocks.Max() is { } topicMinBlock
            ? Math.Max(addressMinBlock, topicMinBlock)
            : null;

        public string GetDbSize()
        {
            return _rootDb.GatherMetric().Size.SizeToString(useSi: true, addSpace: true);
        }

        public List<int> GetBlockNumbersFor(Address address, int from, int to)
        {
            return GetBlockNumbersFor(null, address.Bytes, from, to);
        }

        public List<int> GetBlockNumbersFor(int index, Hash256 topic, int from, int to)
        {
            return GetBlockNumbersFor(index, topic.Bytes.ToArray(), from, to);
        }

        private List<int> GetBlockNumbersFor(int? topicIndex, byte[] key, int from, int to)
        {
            // TODO: use ArrayPoolList?
            var result = new List<int>(128);

            IterateBlockNumbersFor(topicIndex, key, from, to, iterator =>
            {
                var value = iterator.Value().ToArray();
                foreach (var block in EnumerateBlockNumbers(value, from))
                {
                    if (block > to)
                        return false;

                    result.Add(block);
                }

                return true;
            });

            return result;
        }

        private void IterateBlockNumbersFor(
            int? topicIndex, byte[] key, int from, int to,
            Func<IIterator, bool> callback
        )
        {
            var timestamp = Stopwatch.GetTimestamp();
            byte[] dbKeyBuffer = null;

            try
            {
                // Adjust parameters to avoid composing invalid lookup keys
                if (from < 0) from = 0;
                if (to < from) return;

                dbKeyBuffer = Pool.Rent(MaxDbKeyLength);
                ReadOnlySpan<byte> dbKey = CreateDbKey(key, from, dbKeyBuffer);
                ReadOnlySpan<byte> normalizedKey = ExtractKey(dbKey);

                IDb? db = GetDb(topicIndex);
                using IIterator iterator = db.GetIterator(ordered: true); // TODO: specify lower/upper bounds?

                // Find the last index for the given key, starting at or before `from`
                iterator.SeekForPrev(dbKey);

                // Otherwise, find the first index for the given key
                if (!IsInKeyBounds(iterator, normalizedKey))
                {
                    iterator.SeekToFirst();
                    iterator.Seek(key);
                }

                while (IsInKeyBounds(iterator, normalizedKey))
                {
                    if (!callback(iterator))
                        return;

                    iterator.Next();
                }
            }
            finally
            {
                if (dbKeyBuffer != null) Pool.Return(dbKeyBuffer);

                if (_logger.IsTrace) _logger.Trace($"{nameof(IterateBlockNumbersFor)}({Convert.ToHexString(key)}, {from}, {to}) in {Stopwatch.GetElapsedTime(timestamp)}");
            }
        }

        private static bool IsInKeyBounds(IIterator iterator, ReadOnlySpan<byte> key)
        {
            return iterator.Valid() && ExtractKey(iterator.Key()).SequenceEqual(key);
        }

        private IEnumerable<int> EnumerateBlockNumbers(byte[] data, int from)
        {
            if (data.Length == 0)
                yield break;

            var blockNums = data.Length == 0 || !IsCompressed(data, out _)
                ? ReadBlockNums(data)
                : DecompressDbValue(data);

            ReverseBlocksIfNeeded(blockNums);

            int startIndex = BinarySearch(blockNums, from);
            if (startIndex < 0)
            {
                startIndex = ~startIndex;
            }

            for (int i = startIndex; i < blockNums.Length; i++)
                yield return blockNums[i];
        }

        // TODO: optimize
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

                foreach (TxReceipt receipt in receipts)
                {
                    stats?.IncrementTx();

                    if (receipt.Logs == null)
                        continue;

                    foreach (LogEntry log in receipt.Logs)
                    {
                        stats?.IncrementLogs();

                        if (IsAddressBlockNewer(blockNumber, isBackwardSync))
                        {
                            List<int> addressNums = aggregate.Address.GetOrAdd(log.Address, static _ => new(1));

                            if (addressNums.Count == 0 || addressNums[^1] != blockNumber)
                                addressNums.Add(blockNumber);
                        }

                        var topicsLength = Math.Min(log.Topics.Length, MaxTopics);
                        for (byte topicIndex = 0; topicIndex < topicsLength; topicIndex++)
                        {
                            if (IsTopicBlockNewer(topicIndex, blockNumber, isBackwardSync))
                            {

                                stats?.IncrementTopics();

                                var topicNums = aggregate.Topic[topicIndex].GetOrAdd(log.Topics[topicIndex], static _ => new(1));

                                if (topicNums.Count == 0 || topicNums[^1] != blockNumber)
                                    topicNums.Add(blockNumber);
                            }
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

                using var batches = new DbBatches(_addressDb, _topicDbs);

                Span<byte> dbValue = MergeOps.Create(MergeOp.ReorgOp, block.BlockNumber, valueArray);

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

                // Need to update last block number, so that new-receipts comparison won't fail when rewriting it
                var blockNum = block.BlockNumber - 1;

                var (addressRange, topicRanges) = SaveRanges(batches, blockNum, blockNum, isBackwardSync, isReorg: true);

                batches.Commit();

                UpdateRanges(addressRange, topicRanges, isBackwardSync);
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
                using var batches = new DbBatches(_addressDb, _topicDbs);

                // Add values to batches
                long timestamp;
                if (!aggregate.IsEmpty)
                {
                    timestamp = Stopwatch.GetTimestamp();

                    // Add addresses
                    foreach (var (address, blockNums) in aggregate.Address)
                    {
                        SaveBlockNumbersByKey(batches.Address, address.Bytes, blockNums, isBackwardSync, stats);
                    }

                    // Add topics
                    for (var topicIndex = 0; topicIndex < aggregate.Topic.Length; topicIndex++)
                    {
                        var topics = aggregate.Topic[topicIndex];

                        foreach (var (topic, blockNums) in topics)
                            SaveBlockNumbersByKey(batches.Topics[topicIndex], topic.Bytes, blockNums, isBackwardSync, stats);
                    }

                    stats?.Processing.Include(Stopwatch.GetElapsedTime(timestamp));
                }

                timestamp = Stopwatch.GetTimestamp();
                var (addressRange, topicRanges) = SaveRanges(batches, aggregate.FirstBlockNum, aggregate.LastBlockNum, isBackwardSync);
                stats?.UpdatingMeta.Include(Stopwatch.GetElapsedTime(timestamp));

                // Submit batches
                timestamp = Stopwatch.GetTimestamp();
                batches.Commit();
                stats?.CommitingBatch.Include(Stopwatch.GetElapsedTime(timestamp));

                UpdateRanges(addressRange, topicRanges, isBackwardSync);

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

        private static ReadOnlySpan<byte> CreateMergeDbKey(ReadOnlySpan<byte> key, Span<byte> buffer, bool isBackwardSync)
        {
            key = WriteKey(key, buffer);
            var postfix = isBackwardSync ? SpecialPostfix.BackwardMerge : SpecialPostfix.ForwardMerge;
            postfix.CopyTo(buffer[key.Length..]);

            var length = key.Length + postfix.Length;
            return buffer[..length];
        }

        // RocksDB uses big-endian (lexicographic) ordering
        // +1 is needed as 0 is used for the backward-merge key
        private static void SetKeyBlockNum(Span<byte> dbKeyEnd, int blockNumber) => BinaryPrimitives.WriteInt32BigEndian(dbKeyEnd, blockNumber + 1);

        private static bool UseBackwardSyncFor(ReadOnlySpan<byte> dbKey) => dbKey.EndsWith(SpecialPostfix.BackwardMerge);

        private static int BinarySearch(ReadOnlySpan<int> blocks, int from)
        {
            int index = blocks.BinarySearch(from);
            return index < 0 ? ~index : index;
        }

        private ReadOnlySpan<int> Decompress(ReadOnlySpan<byte> data, Span<int> decompressedBlockNumbers)
        {
            _ = _compressionAlgorithm.Decompress(data, (nuint)decompressedBlockNumbers.Length, decompressedBlockNumbers);
            return decompressedBlockNumbers;
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

        private static int[] ReadBlockNums(ReadOnlySpan<byte> source)
        {
            if (source.Length % 4 != 0)
                throw new LogIndexStateException("Invalid length for array of block numbers.");

            var result = new int[source.Length / BlockNumSize];
            for (var i = 0; i < source.Length; i += BlockNumSize)
                result[i / BlockNumSize] = GetValBlockNum(source[i..]);

            return result;
        }

        private static byte[] CreateDbValue(IReadOnlyList<int> blockNums)
        {
            var value = new byte[blockNums.Count * BlockNumSize];
            SetValBlockNums(value, blockNums);
            return value;
        }

        private IDb GetDb(int? topicIndex) => topicIndex.HasValue ? _topicDbs[topicIndex.Value] : _addressDb;

        private static IDb GetMetaDb(IColumnsDb<LogIndexColumns> rootDb) => rootDb.GetColumnDb(LogIndexColumns.Addresses);

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

        private int[] DecompressDbValue(ReadOnlySpan<byte> data)
        {
            if (!IsCompressed(data, out int len))
                throw new ValidationException("Data is not compressed");

            // TODO: reuse buffer
            Span<int> buffer = new int[len + 1]; // +1 fixes TurboPFor reading outside of array bounds
            buffer = buffer[..^1];

            var result = Decompress(data[BlockNumSize..], buffer);
            return result.ToArray();
        }

        private Span<byte> RemoveReorgableBlocks(Span<byte> data)
        {
            var lastCompressBlock = GetLastReorgableBlockNumber();
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
