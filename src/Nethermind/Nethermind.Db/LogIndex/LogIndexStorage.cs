using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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

namespace Nethermind.Db
{
    // TODO: get rid of InvalidOperationExceptions - these are for state validation
    // TODO: verify all MemoryMarshal usages - needs to be CPU-cross-compatible
    [SuppressMessage("ReSharper", "PrivateFieldCanBeConvertedToLocalVariable")] // TODO: get rid of unused fields
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

        private struct Batches : IDisposable
        {
            private bool _completed;

            public IWriteBatch Address { get; }
            public IWriteBatch[] Topics { get; }

            public Batches(IDb addressDb, IDb[] topicsDbs)
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

        // TODO: switch to ArrayPoolList just for `using` syntax?
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

        private Exception _lastError;

        /// <summary>
        /// Whether a first batch was already added.
        /// </summary>
        private bool WasInitialized => _addressMinBlock is not null; // TODO: check other metadata values?

        private readonly TaskCompletionSource _firstBlockAddedSource = new();
        public Task FirstBlockAdded => _firstBlockAddedSource.Task;

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

        // Not thread safe
        private bool _stopped;
        private bool _disposed;

        public LogIndexStorage(IDbFactory dbFactory, ILogManager logManager, ILogIndexConfig config)
        {
            Enabled = config.Enabled;

            _maxReorgDepth = config.MaxReorgDepth;

            _logger = logManager.GetClassLogger<LogIndexStorage>();
            _compressor = new Compressor(this, _logger, config.CompressionDistance, config.CompressionParallelism);
            _compactor = config.CompactionDistance < 0 ? new Compactor(this, _logger, config.CompactionDistance) : new NoOpCompactor();

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

            if (WasInitialized)
                _firstBlockAddedSource.SetResult();
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
                    MergeOperatorByColumnFamily = _mergeOperators.ToDictionary(x => $"{x.Key}", x => (IMergeOperator)x.Value)
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
                // TODO: discuss how to handle here and below
                throw new NotSupportedException(
                    $"Used compression algorithm ({usedAlgoName}) is not supported on this platform. " +
                    "Log index need to be reset to use a different compression algorithm."
                );
            }

            configAlgoName ??= usedAlgoName;
            if (usedAlgoName != configAlgoName)
            {
                throw new NotSupportedException(
                    $"Used compression algorithm ({usedAlgoName}) is different from the one configured ({configAlgoName}). " +
                    "Log index need to be reset to use a different compression algorithm."
                );
            }

            return usedAlgo;
        }

        // TODO: remove if unused
        private static IEnumerable<(byte[] key, byte[] value)> Enumerate(IIterator iterator)
        {
            iterator.SeekToFirst();
            while (iterator.Valid())
            {
                yield return (iterator.Key().ToArray(), iterator.Value().ToArray());
                iterator.Next();
            }
        }

        private static void ForceMerge(IDb db)
        {
            using IIterator iterator = db.GetIterator();

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

                await _compactor.StopAsync(); // Need to wait, as releasing RocksDB during compaction will cause 0xC0000005
                await _compressor.StopAsync(); // TODO: consider not waiting for compression queue to finish

                // TODO: check if needed
                DBColumns.ForEach(static db => db.Flush());

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
            _lastError = error;

            if (_logger.IsError)
                _logger.Error($"Error in {typeof(TCaller).Name}", error);
        }

        private void ThrowIfHasError()
        {
            if (_lastError is {} error)
                ExceptionDispatchInfo.Throw(error);
        }

        async ValueTask IAsyncDisposable.DisposeAsync()
        {
            if (_disposed)
                return;

            await StopAsync();

            _disposed = true;

            _setReceiptsSemaphores[false].Dispose();
            _setReceiptsSemaphores[true].Dispose();
            _compressor.Dispose();
            DBColumns.DisposeItems();
            _rootDb.Dispose();
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
                    throw ValidationException("Backwards sync does not support reorgs.");
                if (batchMin < lastMin)
                    min = SaveRangeBound(dbBatch, SpecialKey.MinBlockNum, batchMin);
            }

            return (min, max);
        }

        private ((int min, int max) address, (int?[] min, int?[] max) topics) SaveRanges(
            Batches batches, int firstBlock, int lastBlock, bool isBackwardSync, bool isReorg = false
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

        public Dictionary<byte[], int[]> GetKeysFor(Address address, int from, int to, bool includeValues = false) =>
            GetKeysFor(null, address.Bytes, from, to, includeValues);

        public Dictionary<byte[], int[]> GetKeysFor(int index, Hash256 topic, int from, int to, bool includeValues = false) =>
            GetKeysFor(index, topic.Bytes.ToArray(), from, to, includeValues);

        [SuppressMessage("ReSharper", "AccessToDisposedClosure")]
        private Dictionary<byte[], int[]> GetKeysFor(int? topicIndex, byte[] key, int from, int to, bool includeValues = false)
        {
            var result = new Dictionary<byte[], int[]>(Bytes.EqualityComparer);
            using var buffer = new ArrayPoolList<int>(includeValues ? 128 : 0);

            IterateBlockNumbersFor(topicIndex, key, from, to, iterator =>
            {
                var iteratorKey = iterator.Key().ToArray();
                var value = iterator.Value().ToArray();
                foreach (var block in EnumerateBlockNumbers(value, from))
                {
                    if (block > to)
                    {
                        result.Add(iteratorKey, buffer.AsSpan().ToArray());
                        return false;
                    }

                    if (includeValues)
                        buffer.Add(block);
                }

                result.Add(iteratorKey, buffer.AsSpan().ToArray());
                buffer.Clear();

                return true;
            });

            return result;
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
                using IIterator iterator = db.GetIterator(true); // TODO: specify lower/upper bounds?

                // Find the last index for the given key, starting at or before `from`
                iterator.SeekForPrev(dbKey);

                // Otherwise, find the first index for the given key
                // TODO: achieve in a single seek?
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

        // TODO: optimize allocations
        public LogIndexAggregate Aggregate(IReadOnlyList<BlockReceipts> batch, bool isBackwardSync, LogIndexUpdateStats? stats)
        {
            ThrowIfStopped();
            ThrowIfHasError();

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

        public Task CheckMigratedData()
        {
            // using IIterator<byte[], byte[]> addressIterator = _addressDb.GetIterator();
            // using IIterator<byte[], byte[]> topicIterator = _topicsDb.GetIterator();
            //
            // // Total: 9244, finalized - 31
            // (Address, IndexInfo)[] addressData = Enumerate(addressIterator).Select(x => (new Address(SplitDbKey(x.key).key), IndexInfo.Deserialize(x.key, x.value))).ToArray();
            //
            // // Total: 5_654_366
            // // From first 200_000: 1 - 134_083 (0.670415), 2 - 10_486, 3 - 33_551, 4 - 4872, 5 - 4227, 6 - 4764, 7 - 6792, 8 - 609, 9 - 67, 10 - 55
            // // From first 300_000: 1 - 228_553 (0.761843333)
            // // From first 1_000_000: 1 - 875_216 (0.875216)
            // //var topicData = Enumerate(topicIterator).Select(x => (new Hash256(SplitDbKey(x.key).key), DeserializeIndexInfo(x.key, x.value))).ToArray();
            // var topicData = Enumerate(topicIterator).Take(200_000).Select(x => (topic: new Hash256(SplitDbKey(x.key).key), Index: IndexInfo.Deserialize(x.key, x.value))).GroupBy(x => x.Index.TotalValuesCount).ToDictionary(g => g.Key, g => g.Count());
            //
            // GC.KeepAlive(addressData);
            // GC.KeepAlive(topicData);

            return Task.CompletedTask;
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

                using var batches = new Batches(_addressDb, _topicDbs);

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
                // TODO: figure out if this can be improved, maybe don't use comparison checks at all
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

        public async Task CompactAsync(bool flush = false, int mergeIterations = 0, LogIndexUpdateStats? stats = null)
        {
            ThrowIfStopped();
            ThrowIfHasError();

            if (_logger.IsInfo)
                _logger.Info($"Log index forced compaction started, DB size: {GetDbSize()}");

            var timestamp = Stopwatch.GetTimestamp();

            if (flush)
            {
                DBColumns.ForEach(static db => db.Flush());
            }

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

        public async Task RecompactAsync(int minLengthToCompress = -1, LogIndexUpdateStats? stats = null)
        {
            ThrowIfStopped();
            ThrowIfHasError();

            if (minLengthToCompress < 0)
                minLengthToCompress = _compressor.MinLengthToCompress;

            await CompactAsync(flush: true, mergeIterations: 2, stats: stats);

            var timestamp = Stopwatch.GetTimestamp();
            var addressCount = await QueueLargeKeysCompression(topicIndex: null, minLengthToCompress);
            stats?.QueueingAddressCompression.Include(Stopwatch.GetElapsedTime(timestamp));

            timestamp = Stopwatch.GetTimestamp();
            var topicCount = 0;
            for (var topicIndex = 0; topicIndex < _topicDbs.Length; topicIndex++)
                topicCount += await QueueLargeKeysCompression(topicIndex, minLengthToCompress);
            stats?.QueueingTopicCompression.Include(Stopwatch.GetElapsedTime(timestamp));

            _logger.Info($"Queued keys for compaction: {addressCount:N0} address, {topicCount:N0} topic");

            await _compressor.WaitUntilEmptyAsync(TimeSpan.FromSeconds(30));
            await CompactAsync(flush: true, mergeIterations: 2, stats: stats);
        }

        private async Task<int> QueueLargeKeysCompression(int? topicIndex, int minLengthToCompress)
        {
            var counter = 0;

            IDb db = GetDb(topicIndex);
            using var addressIterator = db.GetIterator();
            foreach (var (key, value) in Enumerate(addressIterator))
            {
                if (IsCompressed(value) || value.Length < minLengthToCompress)
                    continue;

                await _compressor.EnqueueAsync(topicIndex, key);

                counter++;
            }

            return counter;
        }

        public async Task SetReceiptsAsync(LogIndexAggregate aggregate, bool isBackwardSync, LogIndexUpdateStats? stats = null)
        {
            ThrowIfStopped();
            ThrowIfHasError();

            long totalTimestamp = Stopwatch.GetTimestamp();

            SemaphoreSlim semaphore = _setReceiptsSemaphores[isBackwardSync];
            await LockRunAsync(semaphore);

            var wasInitialized = WasInitialized;
            if (!wasInitialized)
                await _initSemaphore.WaitAsync();

            try
            {
                using var batches = new Batches(_addressDb, _topicDbs);

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

                // Notify we have the first block
                _firstBlockAddedSource.TrySetResult();

                // Enqueue compaction if needed
                _compactor.TryEnqueue();
            }
            // TODO: stop or block index on error?
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

        // batch is expected to be sorted, TODO: validate this is the case
        public Task SetReceiptsAsync(IReadOnlyList<BlockReceipts> batch, bool isBackwardSync, LogIndexUpdateStats? stats = null)
        {
            LogIndexAggregate aggregate = Aggregate(batch, isBackwardSync, stats);
            return SetReceiptsAsync(aggregate, isBackwardSync, stats);
        }

        // TODO: optimize allocations
        protected virtual void SaveBlockNumbersByKey(
            IWriteBatch dbBatch, ReadOnlySpan<byte> key, IReadOnlyList<int> blockNums,
            bool isBackwardSync, LogIndexUpdateStats? stats
        )
        {
            var dbKeyArray = Pool.Rent(MaxDbKeyLength);

            try
            {
                ReadOnlySpan<byte> dbKey = CreateMergeDbKey(key, dbKeyArray, isBackwardSync);

                // TODO: handle writing already processed blocks
                // if (blockNums[^1] <= lastSavedNum)
                //     return;

                var newValue = CreateDbValue(blockNums);

                var timestamp = Stopwatch.GetTimestamp();

                if (newValue is null or [])
                    throw ValidationException($"No block numbers to save for {Convert.ToHexString(key)}.");

                dbBatch.Merge(dbKey, newValue); // TODO: consider using DisableWAL, but check FlushOnTooManyWrites
                stats?.CallingMerge.Include(Stopwatch.GetElapsedTime(timestamp));
            }
            finally
            {
                Pool.Return(dbKeyArray);
            }
        }

        private static ReadOnlySpan<byte> WriteKey(ReadOnlySpan<byte> key, Span<byte> buffer)
        {
            //ReadOnlySpan<byte> normalized = key.WithoutLeadingZeros();
            //normalized = normalized.Length > 0 ? normalized : ZeroArray;

            key.CopyTo(buffer);
            return buffer[..key.Length];
        }

        private static ReadOnlySpan<byte> ExtractKey(ReadOnlySpan<byte> dbKey) => dbKey[..^BlockNumSize];

        /// <summary>
        /// Generates a key consisting of the <c>key || block-number</c> byte array.
        /// </summary>
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
        private static int GetKeyBlockNum(ReadOnlySpan<byte> dbKey) => BinaryPrimitives.ReadInt32BigEndian(dbKey[^BlockNumSize..]) - 1;
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

        // TODO: test on big-endian system
        private ReadOnlySpan<byte> Compress(Span<byte> data, Span<byte> buffer)
        {
            ReadOnlySpan<int> blockNumbers = MemoryMarshal.Cast<byte, int>(data);
            var length = (int)_compressionAlgorithm.Compress(blockNumbers, (nuint)blockNumbers.Length, buffer);
            return buffer[..length];
        }

        // used for data validation, TODO: introduce custom exception type
        // TODO: include key value when available
        private static Exception ValidationException(string message) => new InvalidOperationException(message);

        public static int ReadCompressionMarker(ReadOnlySpan<byte> source) => -BinaryPrimitives.ReadInt32LittleEndian(source);
        public static void WriteCompressionMarker(Span<byte> source, int len) => BinaryPrimitives.WriteInt32LittleEndian(source, -len);

        public static bool IsCompressed(ReadOnlySpan<byte> source) => IsCompressed(source, out _);
        public static bool IsCompressed(ReadOnlySpan<byte> source, out int len)
        {
            len = ReadCompressionMarker(source);
            return len > 0;
        }

        public static void SetValBlockNum(Span<byte> destination, int blockNum) => BinaryPrimitives.WriteInt32LittleEndian(destination, blockNum);
        public static int GetValBlockNum(ReadOnlySpan<byte> source) => BinaryPrimitives.ReadInt32LittleEndian(source);
        public static int GetValLastBlockNum(ReadOnlySpan<byte> source) => GetValBlockNum(source[^BlockNumSize..]);

        public static void SetValBlockNums(Span<byte> destination, IEnumerable<int> blockNums)
        {
            var shift = 0;
            foreach (var blockNum in blockNums)
            {
                SetValBlockNum(destination[shift..], blockNum);
                shift += BlockNumSize;
            }
        }

        public static int[] ReadBlockNums(ReadOnlySpan<byte> source)
        {
            if (source.Length % 4 != 0)
                throw ValidationException("Invalid length for array of block numbers.");

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

        private byte[] CompressDbValue(Span<byte> data)
        {
            if (IsCompressed(data, out _))
                throw ValidationException("Data is already compressed.");
            if (data.Length % BlockNumSize != 0)
                throw ValidationException("Invalid data length.");

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

        private Span<byte> RemoveReorgableBlocks(Span<byte> data)
        {
            var lastCompressBlock = GetLastReorgableBlockNumber();
            var lastCompressIndex = LastBlockSearch(data, lastCompressBlock, false);

            if (lastCompressIndex < 0) lastCompressIndex = 0;
            if (lastCompressIndex > data.Length) lastCompressIndex = data.Length;

            return data[..lastCompressIndex];
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

        // TODO: check if MemoryExtensions.BinarySearch<int> can be used and will be faster
        private static int BinaryBlockSearch(ReadOnlySpan<byte> data, int target)
        {
            if (data.Length == 0)
                return 0;

            int count = data.Length / sizeof(int);
            int left = 0, right = count - 1;

            // Short circuits in some cases
            if (GetValLastBlockNum(data) == target)
                return right * BlockNumSize;
            if (GetValBlockNum(data) == target)
                return 0;

            while (left <= right)
            {
                int mid = left + (right - left) / 2;
                int offset = mid * 4;

                int value = GetValBlockNum(data[offset..]);

                if (value == target)
                    return offset;
                if (value < target)
                    left = mid + 1;
                else
                    right = mid - 1;
            }

            return ~(left * BlockNumSize);
        }
    }
}
