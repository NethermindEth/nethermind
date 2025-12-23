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
    // TODO: test on big-endian system?
    public abstract partial class LogIndexStorage<TPosition> : ILogIndexStorage<TPosition>
        where TPosition: struct, ILogPosition<TPosition>
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

        public static class SpecialPostfix
        {
            // Any ordered prefix seeking will start on it.
            public static readonly byte[] BackwardMerge = Enumerable.Repeat(byte.MinValue, TPosition.Size).ToArray();

            // Any ordered prefix seeking will end on it.
            public static readonly byte[] ForwardMerge = Enumerable.Repeat(byte.MaxValue, TPosition.Size).ToArray();

            // Exclusive upper bound for iterator seek, immediately following ForwardMerge, as iterator bounds are exclusive.
            public static readonly byte[] UpperBound = Enumerable.Repeat(byte.MaxValue, TPosition.Size).Concat([byte.MinValue]).ToArray();
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

        public const int BlockNumberSize = sizeof(int);
        public const int CompressionMarkerSize = sizeof(int);
        public static int ValueSize => TPosition.Size;
        private const int MaxKeyLength = Hash256.Size + 1; // Math.Max(Address.Size, Hash256.Size) + 1
        private static int MaxDbKeyLength => MaxKeyLength + ValueSize;

        private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Shared;

        private readonly IColumnsDb<LogIndexColumns> _rootDb;
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

        private readonly Dictionary<LogIndexColumns, IMergeOperator> _mergeOperators;
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

                _logger = logManager.GetClassLogger<LogIndexStorage<TPosition>>();

                _compressor = config.CompressionDistance > 0
                    ? new Compressor(this, config.CompressionDistance, config.MaxCompressionParallelism)
                    : new NoOpCompressor();

                _compactor = config.CompactionDistance > 0
                    ? new Compactor(this, _logger, config.CompactionDistance)
                    : new NoOpCompactor();

                _mergeOperators = new()
                {
                    { LogIndexColumns.Addresses, new MergeOperator(this, _compressor, topicIndex: null) },
                    { LogIndexColumns.Topics0, new MergeOperator(this, _compressor, topicIndex: 0) },
                    { LogIndexColumns.Topics1, new MergeOperator(this, _compressor, topicIndex: 1) },
                    { LogIndexColumns.Topics2, new MergeOperator(this, _compressor, topicIndex: 2) },
                    { LogIndexColumns.Topics3, new MergeOperator(this, _compressor, topicIndex: 3) }
                };

                _rootDb = CreateRootDb(dbFactory, config.Reset);
                _addressDb = _rootDb.GetColumnDb(LogIndexColumns.Addresses);
                _topicDbs = _mergeOperators.Keys.Except([LogIndexColumns.Addresses]).Select(cl => _rootDb.GetColumnDb(cl)).ToArray();
                _compressionAlgorithm = SelectCompressionAlgo(config.CompressionAlgorithm);
                _compressionAlgorithm = CompressionAlgorithm.Best.Value; // TODO: proper select

                _addressMaxBlock = LoadRangeBound(_addressDb, SpecialKey.MaxBlockNum);
                _addressMinBlock = LoadRangeBound(_addressDb, SpecialKey.MinBlockNum);
                _topicMaxBlocks = _topicDbs.Select(static db => LoadRangeBound(db, SpecialKey.MaxBlockNum)).ToArray();
                _topicMinBlocks = _topicDbs.Select(static db => LoadRangeBound(db, SpecialKey.MinBlockNum)).ToArray();

                if (Enabled)
                {
                    _compressor.Start();
                    _compressor.Start();
                }
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
                    _compressor.StopAsync(),
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
            _compressor?.Dispose();
            DBColumns?.DisposeItems();
            _rootDb?.Dispose();
        }

        private static int? LoadRangeBound(IDb db, byte[] key)
        {
            var value = db.Get(key);
            return value is { Length: > 1 } ? GetRangeBlockNumber(value) : null;
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
            var bufferArr = Pool.Rent(BlockNumberSize);
            Span<byte> buffer = bufferArr.AsSpan(BlockNumberSize);

            try
            {
                SetFirstBlock(buffer, value);
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

        private static bool IsPositionNewer(TPosition next, TPosition? lastMin, TPosition? lastMax, bool isBackwardSync) => isBackwardSync
            ? lastMin is null || next < lastMin
            : lastMax is null || next > lastMax;

        private bool IsAddressBlockNewer(int next, bool isBackwardSync) => IsBlockNewer(next, _addressMinBlock, _addressMaxBlock, isBackwardSync);
        private bool IsTopicBlockNewer(int topicIndex, int next, bool isBackwardSync) => IsBlockNewer(next, _topicMinBlocks[topicIndex], _topicMaxBlocks[topicIndex], isBackwardSync);

        private bool IsPositionNewer(int next, bool isBackwardSync) =>
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

        public IList<int> GetBlockNumbersFor(Address address, int from, int to)
        {
            return GetBlockNumbersFor(null, address.Bytes, from, to);
        }

        public IList<int> GetBlockNumbersFor(int topicIndex, Hash256 topic, int from, int to)
        {
            return GetBlockNumbersFor(topicIndex, topic.Bytes.ToArray(), from, to);
        }

        public IList<TPosition> GetLogPositions(Address address, int from, int to)
        {
            return GetLogPositionsFor(null, address.Bytes, from, to);
        }

        public IList<TPosition> GetLogPositions(int index, Hash256 topic, int from, int to)
        {
            return GetLogPositionsFor(index, topic.Bytes.ToArray(), from, to);
        }

        // TODO: limit to avoid potential OOM?
        private List<TPosition> GetLogPositionsFor(int? topicIndex, byte[] key, int from, int to)
        {
            // TODO: use ArrayPoolList?
            var result = new List<TPosition>();

            IterateBlockNumbersFor(topicIndex, key, from, to, view =>
            {
                var value = view.CurrentValue.ToArray(); // TODO: remove ToArray
                foreach (TPosition position in EnumerateLogPositions(value, from))
                {
                    if (position.BlockNumber > to)
                        return false;

                    result.Add(position);
                }

                return true;
            });

            return result;
        }

        private IList<int> GetBlockNumbersFor(int? topicIndex, byte[] key, int from, int to)
        {
            // TODO: use ArrayPoolList?
            var result = new List<int>();

            var lastAddedNumber = -1;
            IterateBlockNumbersFor(topicIndex, key, from, to, view =>
            {
                var value = view.CurrentValue.ToArray(); // TODO: remove ToArray
                foreach (TPosition position in EnumerateLogPositions(value, from))
                {
                    if (position.BlockNumber > to)
                        return false;

                    if (position.BlockNumber > lastAddedNumber)
                        result.Add(lastAddedNumber = position.BlockNumber);
                }

                return true;
            });

            return result;
        }

        private void IterateBlockNumbersFor(
            int? topicIndex, byte[] key, int from, int to,
            Func<ISortedView, bool> callback
        )
        {
            var timestamp = Stopwatch.GetTimestamp();

            try
            {
                // Adjust parameters to avoid composing invalid lookup keys
                if (from < 0) from = 0;
                if (to < from) return;

                IDb db = GetDb(topicIndex);
                ISortedKeyValueStore? sortedDb = db as ISortedKeyValueStore
                    ?? throw new NotSupportedException($"{db.GetType().Name} DB does not support sorted lookups.");

                ReadOnlySpan<byte> startKey = CreateDbKey(key, CreateLogPosition(from), stackalloc byte[MaxDbKeyLength]);
                ReadOnlySpan<byte> fromKey = CreateDbKey(key, SpecialPostfix.BackwardMerge, stackalloc byte[MaxDbKeyLength]);
                ReadOnlySpan<byte> toKey = CreateDbKey(key, SpecialPostfix.UpperBound, stackalloc byte[MaxDbKeyLength]);

                using ISortedView view = sortedDb.GetViewBetween(fromKey, toKey);

                var isValid = view.StartBefore(startKey) || view.MoveNext();

                while (isValid)
                {
                    if (!callback(view))
                        return;

                    isValid = view.MoveNext() && view.CurrentKey.StartsWith(key);
                }
            }
            finally
            {
                if (_logger.IsTrace)
                    _logger.Trace($"{nameof(IterateBlockNumbersFor)}({Convert.ToHexString(key)}, {from}, {to}) in {Stopwatch.GetElapsedTime(timestamp)}");
            }
        }

        private IEnumerable<TPosition> EnumerateLogPositions(byte[] data, int from)
        {
            if (data.Length == 0)
                yield break;

            // TODO: optimize
            var positions = data.Length == 0 || !IsCompressed(data, out _)
                ? MemoryMarshal.Cast<byte, TPosition>(data).ToArray()
                : DecompressDbValue(data);

            ReverseBlocksIfNeeded(positions);

            // TODO: binary search for start?
            foreach (TPosition val in positions)
            {
                if (val.BlockNumber < from) continue;
                yield return val;
            }
        }

        // TODO: use some custom fast-but-unreliable hash function
        public LogIndexAggregate<TPosition> Aggregate(IReadOnlyList<BlockReceipts> batch, bool isBackwardSync, LogIndexUpdateStats? stats)
        {
            ThrowIfStopped();
            ThrowIfHasError();

            if ((!isBackwardSync && !IsSeqAsc(batch)) || (isBackwardSync && !IsSeqDesc(batch)))
                throw new ArgumentException($"Unexpected blocks batch order: ({batch[0]} to {batch[^1]}).");

            if (!IsPositionNewer(batch[^1].BlockNumber, isBackwardSync))
                return new(batch);

            var timestamp = Stopwatch.GetTimestamp();

            var aggregate = new LogIndexAggregate<TPosition>(batch);
            foreach ((var blockNumber, TxReceipt[] receipts) in batch)
            {
                if (!IsPositionNewer(blockNumber, isBackwardSync))
                    continue;

                stats?.IncrementBlocks();
                stats?.IncrementTx(receipts.Length);

                IEnumerable<(int logIndex, LogEntry log)> logsEnumerator = isBackwardSync
                    ? new ReverseLogsEnumerator(receipts)
                    : new LogsEnumerator(receipts);

                foreach ((var logIndex, LogEntry log) in logsEnumerator)
                {
                    stats?.IncrementLogs();
                    TPosition position = TPosition.Create(blockNumber, logIndex);

                    if (IsAddressBlockNewer(blockNumber, isBackwardSync))
                    {
                        IList<TPosition> addressPositions = aggregate.Address
                            .GetOrAdd(log.Address, static _ => new List<TPosition>(1));

                        if (addressPositions.Count == 0 || addressPositions[^1] != position)
                            addressPositions.Add(position);
                    }

                    var topicsLength = Math.Min(log.Topics.Length, MaxTopics);
                    for (byte topicIndex = 0; topicIndex < topicsLength; topicIndex++)
                    {
                        if (IsTopicBlockNewer(topicIndex, blockNumber, isBackwardSync))
                        {
                            stats?.IncrementTopics();

                            IList<TPosition> topicPositions = aggregate.Topic[topicIndex]
                                .GetOrAdd(log.Topics[topicIndex], static _ => new List<TPosition>(1));

                            if (topicPositions.Count == 0 || topicPositions[^1] != position)
                                topicPositions.Add(position);
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
                throw new InvalidOperationException($"{nameof(LogIndexStorage<>)} does not support concurrent invocations in the same direction.");
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
                valueArray = Pool.Rent(MergeOps.Size);

                using var batches = new DbBatches(_addressDb, _topicDbs);

                Span<byte> dbValue = MergeOps.Create(MergeOp.Reorg, CreateLogPosition(block.BlockNumber), valueArray);

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

            foreach (IMergeOperator mergeOperator in _mergeOperators.Values)
                stats?.Combine((mergeOperator as MergeOperator)?.Stats ?? new LogIndexUpdateStats(this));

            if (_logger.IsInfo)
                _logger.Info($"Log index forced compaction finished in {Stopwatch.GetElapsedTime(timestamp)}, DB size: {GetDbSize()} {stats:d}");
        }

        public async Task SetReceiptsAsync(LogIndexAggregate<TPosition> aggregate, LogIndexUpdateStats? stats = null)
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
                    foreach ((Address address, IList<TPosition> positions) in aggregate.Address)
                    {
                        SavePositions(batches.Address, address.Bytes, positions, isBackwardSync, stats);
                    }

                    // Add topics
                    for (var topicIndex = 0; topicIndex < aggregate.Topic.Length; topicIndex++)
                    {
                        Dictionary<Hash256, IList<TPosition>> topics = aggregate.Topic[topicIndex];

                        foreach ((Hash256 topic, IList<TPosition> positions) in topics)
                            SavePositions(batches.Topics[topicIndex], topic.Bytes, positions, isBackwardSync, stats);
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

            foreach (MergeOperator mergeOperator in _mergeOperators.Values.OfType<MergeOperator>())
                stats?.Combine(mergeOperator.GetAndResetStats());
            stats?.PostMergeProcessing.Combine(_compressor.GetAndResetStats());
            stats?.Compacting.Combine(_compactor.GetAndResetStats());
            stats?.SetReceipts.Include(Stopwatch.GetElapsedTime(totalTimestamp));
        }

        public Task SetReceiptsAsync(IReadOnlyList<BlockReceipts> batch, bool isBackwardSync, LogIndexUpdateStats? stats = null)
        {
            LogIndexAggregate<TPosition> aggregate = Aggregate(batch, isBackwardSync, stats);
            return SetReceiptsAsync(aggregate, stats);
        }

        protected virtual void SavePositions(
            IWriteBatch dbBatch, ReadOnlySpan<byte> key, IList<TPosition> positions,
            bool isBackwardSync, LogIndexUpdateStats? stats
        )
        {
            var dbKeyArray = Pool.Rent(MaxDbKeyLength);

            try
            {
                ReadOnlySpan<byte> dbKey = CreateMergeDbKey(key, dbKeyArray, isBackwardSync);

                var newValue = CreateDbValue(positions);

                var timestamp = Stopwatch.GetTimestamp();

                if (newValue is null or [])
                    throw new LogIndexStateException("No block numbers to save.", key);

                TPosition[] pos = MemoryMarshal.Cast<byte, TPosition>(newValue).ToArray();
                if (isBackwardSync) Array.Reverse(pos);

                // TODO: consider disabling WAL, but check:
                // - FlushOnTooManyWrites method
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

        private static ReadOnlySpan<byte> ExtractKey(ReadOnlySpan<byte> dbKey) => dbKey[..^TPosition.Size];

        /// <summary>
        /// Generates a key consisting of the <c>key || log-position</c> byte array.
        /// </summary>/
        private static ReadOnlySpan<byte> CreateDbKey(ReadOnlySpan<byte> key, TPosition position, Span<byte> buffer)
        {
            key = WriteKey(key, buffer);
            position.WriteFirstTo(buffer[key.Length..]);

            var length = key.Length + TPosition.Size;
            return buffer[..length];
        }

        /// <summary>
        /// Generates a key consisting of the <c>key || postfix</c> byte array.
        /// </summary>/
        private static ReadOnlySpan<byte> CreateDbKey(ReadOnlySpan<byte> key, ReadOnlySpan<byte> postfix, Span<byte> buffer)
        {
            key = WriteKey(key, buffer);
            postfix.CopyTo(buffer[key.Length..]);

            var length = key.Length + postfix.Length;
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

        private static bool UseBackwardSyncFor(ReadOnlySpan<byte> dbKey) => !dbKey.EndsWith(SpecialPostfix.ForwardMerge);

        private ReadOnlySpan<TPosition> Decompress(ReadOnlySpan<byte> data, Span<TPosition> decompressedBlockNumbers)
        {
            _ = _compressionAlgorithm.Decompress(data, (nuint)decompressedBlockNumbers.Length, decompressedBlockNumbers);
            return decompressedBlockNumbers;
        }

        private ReadOnlySpan<byte> Compress(Span<byte> data, Span<byte> buffer)
        {
            ReadOnlySpan<TPosition> position = MemoryMarshal.Cast<byte, TPosition>(data);
            var length = (int)_compressionAlgorithm.Compress(position, (nuint)position.Length, buffer);
            return buffer[..length];
        }

        private static int ReadCompressionMarker(ReadOnlySpan<byte> source) => -BinaryPrimitives.ReadInt32LittleEndian(source);
        private static void WriteCompressionMarker(Span<byte> source, int len) => BinaryPrimitives.WriteInt32LittleEndian(source, -len);

        private static bool IsCompressed(ReadOnlySpan<byte> source, out int len)
        {
            len = ReadCompressionMarker(source);
            return len > 0;
        }

        private static int GetRangeBlockNumber(ReadOnlySpan<byte> dbValue) => BinaryPrimitives.ReadInt32LittleEndian(dbValue);
        private static void SetFirstBlock(Span<byte> dbValue, int block) => BinaryPrimitives.WriteInt32LittleEndian(dbValue, block);

        private static void SetPositions(Span<byte> destination, IEnumerable<TPosition> positions)
        {
            var shift = 0;
            foreach (TPosition position in positions)
            {
                position.WriteFirstTo(destination[shift..]);
                shift += ValueSize;
            }
        }

        private static byte[] CreateDbValue(IList<TPosition> positions)
        {
            var value = new byte[positions.Count * ValueSize];
            SetPositions(value, positions);
            return value;
        }

        private IDb GetDb(int? topicIndex) => topicIndex.HasValue ? _topicDbs[topicIndex.Value] : _addressDb;

        private static IDb GetMetaDb(IColumnsDb<LogIndexColumns> rootDb) => rootDb.GetColumnDb(LogIndexColumns.Addresses);

        private byte[] CompressDbValue(ReadOnlySpan<byte> key, Span<byte> data)
        {
            if (data.Length % ValueSize != 0)
                throw new LogIndexStateException($"Invalid length of data to compress: {data.Length}.", key);

            var buffer = Pool.Rent(data.Length + ValueSize);

            try
            {
                WriteCompressionMarker(buffer, data.Length / ValueSize);
                var compressedLen = Compress(data, buffer.AsSpan(CompressionMarkerSize..)).Length;

                return buffer[..(CompressionMarkerSize + compressedLen)];
            }
            finally
            {
                Pool.Return(buffer);
            }
        }

        private TPosition[] DecompressDbValue(ReadOnlySpan<byte> data)
        {
            if (!IsCompressed(data, out int len))
                throw new ValidationException("Data is not compressed.");

            // TODO: reuse buffer
            Span<TPosition> buffer = new TPosition[len + 1]; // +1 fixes TurboPFor reading outside of array bounds
            buffer = buffer[..^1];

            ReadOnlySpan<TPosition> result = Decompress(data[CompressionMarkerSize..], buffer);
            return result.ToArray();
        }

        private Span<byte> RemoveReorgableBlocks(Span<byte> data)
        {
            var lastCompressBlock = GetLastReorgableBlockNumber();
            var lastCompressIndex = LastValueSearch(data, CreateLogPosition(lastCompressBlock), false);

            if (lastCompressIndex < 0) lastCompressIndex = 0;
            if (lastCompressIndex > data.Length) lastCompressIndex = data.Length;

            return data[..lastCompressIndex];
        }

        private static void ReverseBlocksIfNeeded(Span<byte> data)
        {
            if (data.Length != 0 && TPosition.ReadFirstFrom(data) > TPosition.ReadLastFrom(data))
                MemoryMarshal.Cast<byte, TPosition>(data).Reverse();
        }

        private static void ReverseBlocksIfNeeded(Span<TPosition> values)
        {
            if (values.Length != 0 && values[0] > values[^1])
                values.Reverse();
        }

        private static int LastValueSearch(ReadOnlySpan<byte> dbValue, TPosition position, bool isBackward)
        {
            if (dbValue.IsEmpty)
                return 0;

            var i = dbValue.Length - ValueSize;
            for (; i >= 0; i -= ValueSize)
            {
                TPosition currentPosition = TPosition.ReadFirstFrom(dbValue[i..]);
                if (currentPosition == position)
                    return i;

                if (isBackward)
                {
                    if (currentPosition > position)
                        return i + ValueSize;
                }
                else
                {
                    if (currentPosition < position)
                        return i + ValueSize;
                }
            }

            return i;
        }

        private static int BinarySearch(ReadOnlySpan<byte> dbValue, TPosition position)
        {
            ReadOnlySpan<TPosition> positions = MemoryMarshal.Cast<byte, TPosition>(dbValue);
            int index = positions.BinarySearch(position);
            return index < 0 ? ~index : index;
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
