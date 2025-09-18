using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;
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
    public sealed partial class LogIndexStorage : ILogIndexStorage
    {
        private static class SpecialKey
        {
            // Use values that we won't encounter during iterator Seek or SeekForPrev
            public static readonly byte[] MinBlockNum = Enumerable.Repeat(byte.MaxValue, MaxDbKeyLength)
                .Concat(new byte[] { 1 }).ToArray();

            // Use values that we won't encounter during iterator Seek or SeekForPrev
            public static readonly byte[] MaxBlockNum = Enumerable.Repeat(byte.MaxValue, MaxDbKeyLength)
                .Concat(new byte[] { 2 }).ToArray();
        }

        private static class SpecialPostfix
        {
            // Any ordered prefix seeking will start on it
            public static readonly byte[] BackwardMerge = Enumerable.Repeat((byte)0, BlockNumSize).ToArray();

            // Any ordered prefix seeking will end on it.
            public static readonly byte[] ForwardMerge = Enumerable.Repeat(byte.MaxValue, BlockNumSize).ToArray();
        }

        private static class Defaults
        {
            public const int IOParallelism = 1;
            public const int MaxReorgDepth = 64;
        }

        public const int MaxTopics = 4;

        private const int MaxKeyLength = Hash256.Size + 1; // Math.Max(Address.Size, Hash256.Size)
        private const int MaxDbKeyLength = MaxKeyLength + BlockNumSize;

        // TODO: consider using ArrayPoolList just for `using` syntax
        private static readonly ArrayPool<byte> _arrayPool = ArrayPool<byte>.Shared;

        private readonly IColumnsDb<LogIndexColumns> _columnsDb;
        private readonly IDb _addressDb;
        private readonly IDb[] _topicsDbs;
        private readonly ILogger _logger;

        private const int BlockNumSize = sizeof(int);

        private readonly int _maxReorgDepth;

        private readonly Dictionary<LogIndexColumns, MergeOperator> _mergeOperators;
        private readonly ICompressor _compressor;
        private readonly ICompactor _compactor;

        private int? _addressMaxBlock;
        private int? _addressMinBlock;
        private int?[] _topicMinBlocks;
        private int?[] _topicMaxBlocks;

        private readonly Lock _firstRunLock = new();
        private bool HasRunBefore => _addressMinBlock is not null; // TODO: check other metadata values?

        private readonly TaskCompletionSource _firstBlockAddedSource = new();
        public Task FirstBlockAdded => _firstBlockAddedSource.Task;

        // Not thread safe
        private bool _stopped;
        private bool _disposed;

        // TODO: ensure class is singleton
        public LogIndexStorage(IDbFactory dbFactory, ILogManager logManager,
            int? ioParallelism = null, int? compactionDistance = null, int? maxReorgDepth = null)
        {
            if (maxReorgDepth < 0) throw new ArgumentException("Compaction distance must be a positive value.", nameof(compactionDistance));
            _maxReorgDepth = maxReorgDepth ?? Defaults.MaxReorgDepth;

            _logger = logManager.GetClassLogger<LogIndexStorage>();
            _compressor = new Compressor(this, _logger, ioParallelism ?? Defaults.IOParallelism);
            _compactor = compactionDistance.HasValue ? new Compactor(this, _logger, compactionDistance.Value) : new NoOpCompactor();

            _mergeOperators = new()
            {
                { LogIndexColumns.Addresses, new(this, _compressor, topicIndex: null) },
                { LogIndexColumns.Topics0, new(this, _compressor, topicIndex: 0) },
                { LogIndexColumns.Topics1, new(this, _compressor, topicIndex: 1) },
                { LogIndexColumns.Topics2, new(this, _compressor, topicIndex: 2) },
                { LogIndexColumns.Topics3, new(this, _compressor, topicIndex: 3) }
            };

            _columnsDb = dbFactory.CreateColumnsDb<LogIndexColumns>(new("logIndexStorage", DbNames.LogIndex)
            {
                MergeOperatorByColumn = _mergeOperators.ToDictionary(x => $"{x.Key}", x => (IMergeOperator)x.Value)
            });
            _addressDb = _columnsDb.GetColumnDb(LogIndexColumns.Addresses);
            _topicsDbs = _mergeOperators.Keys.Where(cl => $"{cl}".Contains("Topic")).Select(cl => _columnsDb.GetColumnDb(cl)).ToArray();

            _addressMaxBlock = LoadBlockNumber(_addressDb, SpecialKey.MaxBlockNum);
            _addressMinBlock = LoadBlockNumber(_addressDb, SpecialKey.MinBlockNum);
            _topicMaxBlocks = _topicsDbs.Select(static db => LoadBlockNumber(db, SpecialKey.MaxBlockNum)).ToArray();
            _topicMinBlocks = _topicsDbs.Select(static db => LoadBlockNumber(db, SpecialKey.MinBlockNum)).ToArray();

            if (HasRunBefore)
                _firstBlockAddedSource.SetResult();
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

        // Used for:
        // - blocking concurrent executions
        // - ensuring the current migration task is completed before stopping
        private readonly Dictionary<bool, SemaphoreSlim> _setReceiptsSemaphores = new()
        {
            { false, new(1, 1) },
            { true, new(1, 1) }
        };

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

                await _compactor.StopAsync(); // Need to wait, as releasing RocksDB during compaction will cause 0xC0000005
                await _compressor.StopAsync(); // TODO: consider not waiting for compression queue to finish

                // TODO: check if needed
                _addressDb.Flush();
                _topicsDbs.ForEach(static db => db.Flush());

                if (_logger.IsInfo) _logger.Info("Log index storage stopped");
            }
            finally
            {
                _stopped = true;

                _setReceiptsSemaphores[false].Release();
                _setReceiptsSemaphores[true].Release();
            }
        }

        async ValueTask IAsyncDisposable.DisposeAsync()
        {
            if (_disposed)
                return;

            await StopAsync();

            _setReceiptsSemaphores[false].Dispose();
            _setReceiptsSemaphores[true].Dispose();
            _columnsDb.Dispose();
            _addressDb.Dispose();
            _topicsDbs.ForEach(static db => db.Dispose());

            _disposed = true;
        }

        private static int? LoadBlockNumber(IDb db, byte[] key)
        {
            var value = db.Get(key);
            return value is { Length: > 1 } ? GetValBlockNum(value) : null;
        }

        private static int SaveBlockNumber(IWriteOnlyKeyValueStore dbBatch, byte[] key, int value)
        {
            var bufferArr = _arrayPool.Rent(BlockNumSize);
            Span<byte> buffer = bufferArr.AsSpan(BlockNumSize);

            try
            {
                SetValBlockNum(buffer, value);
                dbBatch.PutSpan(key, buffer);
                return value;
            }
            finally
            {
                _arrayPool.Return(bufferArr);
            }
        }

        private static (int min, int max) SaveBlockNumbers(IWriteOnlyKeyValueStore dbBatch, int batchFirst, int batchLast,
            int? lastMin, int? lastMax, bool isBackwardSync, bool isReorg)
        {
            var batchMin = Math.Min(batchFirst, batchLast);
            var batchMax = Math.Max(batchFirst, batchLast);

            var min = lastMin ?? SaveBlockNumber(dbBatch, SpecialKey.MinBlockNum, batchMin);
            var max = lastMax ??= SaveBlockNumber(dbBatch, SpecialKey.MaxBlockNum, batchMax);

            if (!isBackwardSync)
            {
                if ((isReorg && batchMax < lastMax) || (!isReorg && batchMax > lastMax))
                    max = SaveBlockNumber(dbBatch, SpecialKey.MaxBlockNum, batchMax);
            }
            else
            {
                if (isReorg)
                    throw ValidationException("Backwards sync does not support reorgs.");
                if (batchMin < lastMin)
                    min = SaveBlockNumber(dbBatch, SpecialKey.MinBlockNum, batchMin);
            }

            return (min, max);
        }

        private (int min, int max) SaveAddressBlockNumbers(IWriteBatch dbBatch, LogIndexAggregate aggregate, bool isBackwardSync, bool isReorg = false) =>
            SaveBlockNumbers(dbBatch, aggregate.FirstBlockNum, aggregate.LastBlockNum, _addressMinBlock, _addressMaxBlock, isBackwardSync, isReorg);

        private (int min, int max) SaveAddressBlockNumbers(IWriteBatch dbBatch, int block, bool isBackwardSync, bool isReorg = false) =>
            SaveBlockNumbers(dbBatch, block, block, _addressMinBlock, _addressMaxBlock, isBackwardSync, isReorg);

        private (int min, int max) SaveTopicBlockNumbers(int topicIndex, IWriteBatch dbBatch, LogIndexAggregate aggregate, bool isBackwardSync, bool isReorg = false) =>
            SaveBlockNumbers(dbBatch, aggregate.FirstBlockNum, aggregate.LastBlockNum, _topicMinBlocks[topicIndex], _topicMaxBlocks[topicIndex], isBackwardSync, isReorg);

        private (int min, int max) SaveTopicBlockNumbers(int topicIndex, IWriteBatch dbBatch, int block, bool isBackwardSync, bool isReorg = false) =>
            SaveBlockNumbers(dbBatch, block, block, _topicMinBlocks[topicIndex], _topicMaxBlocks[topicIndex], isBackwardSync, isReorg);

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
            return FormatSize(_columnsDb.GatherMetric().Size);
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

                dbKeyBuffer = _arrayPool.Rent(MaxDbKeyLength);
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
                if (dbKeyBuffer != null) _arrayPool.Return(dbKeyBuffer);

                if (_logger.IsTrace) _logger.Trace($"{nameof(IterateBlockNumbersFor)}({Convert.ToHexString(key)}, {from}, {to}) in {Stopwatch.GetElapsedTime(timestamp)}");
            }
        }

        private static bool IsInKeyBounds(IIterator iterator, ReadOnlySpan<byte> key)
        {
            return iterator.Valid() && ExtractKey(iterator.Key()).SequenceEqual(key);
        }

        private static IEnumerable<int> EnumerateBlockNumbers(byte[] data, int from)
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

                        for (byte topicIndex = 0; topicIndex < log.Topics.Length; topicIndex++)
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
                throw new InvalidOperationException($"{nameof(LogIndexStorage)} does not support concurrent invocations in the same direction.");
        }

        public async Task ReorgFrom(BlockReceipts block)
        {
            if (!HasRunBefore)
                throw new InvalidOperationException("Reorg before first block is added.");

            const bool isBackwardSync = false;

            SemaphoreSlim semaphore = _setReceiptsSemaphores[isBackwardSync];
            await LockRunAsync(semaphore);

            byte[]? keyArray = null, valueArray = null;

            try
            {
                keyArray = _arrayPool.Rent(MaxDbKeyLength);
                valueArray = _arrayPool.Rent(BlockNumSize + 1);

                IWriteBatch addressBatch = _addressDb.StartWriteBatch();
                IWriteBatch[] topicBatches = _topicsDbs.Select(static db => db.StartWriteBatch()).ToArray();

                Span<byte> dbValue = MergeOps.Create(MergeOp.ReorgOp, block.BlockNumber, valueArray);

                foreach (TxReceipt receipt in block.Receipts)
                {
                    foreach (LogEntry log in receipt.Logs ?? [])
                    {
                        ReadOnlySpan<byte> addressKey = CreateMergeDbKey(log.Address.Bytes, keyArray, isBackwardSync: false);
                        addressBatch.Merge(addressKey, dbValue);

                        for (var topicIndex = 0; topicIndex < log.Topics.Length; topicIndex++)
                        {
                            Hash256 topic = log.Topics[topicIndex];
                            ReadOnlySpan<byte> topicKey = CreateMergeDbKey(topic.Bytes, keyArray, isBackwardSync: false);
                            topicBatches[topicIndex].Merge(topicKey, dbValue);
                        }
                    }
                }

                // Need to update last block number, so that new-receipts comparison won't fail when rewriting it
                // TODO: figure out if this can be improved, maybe don't use comparison checks at all
                var blockNum = block.BlockNumber - 1;

                (int min, int max) addressRange =
                    SaveAddressBlockNumbers(addressBatch, blockNum, isBackwardSync, isReorg: true);

                (int?[] min, int?[] max) topicRanges = (min: _topicMinBlocks.ToArray(), max: _topicMaxBlocks.ToArray());
                for (var topicIndex = 0; topicIndex < topicBatches.Length; topicIndex++)
                {
                    IWriteBatch topicBatch = topicBatches[topicIndex];

                    (topicRanges.min[topicIndex], topicRanges.max[topicIndex]) =
                        SaveTopicBlockNumbers(topicIndex, topicBatch, blockNum, isBackwardSync: false, isReorg: true);
                }

                addressBatch.Dispose();
                topicBatches.ForEach(static b => b.Dispose());

                (_addressMaxBlock, _topicMaxBlocks) = (addressRange.max, topicRanges.max);
            }
            finally
            {
                semaphore.Release();

                if (keyArray is not null) _arrayPool.Return(keyArray);
                if (valueArray is not null) _arrayPool.Return(valueArray);
            }
        }

        public async Task CompactAsync(bool flush, LogIndexUpdateStats? stats = null)
        {
            // TODO: include time to stats
            if (flush)
            {
                _addressDb.Flush();
                _topicsDbs.ForEach(static db => db.Flush());
            }

            CompactingStats compactStats = await _compactor.ForceAsync();
            stats?.Compacting.Combine(compactStats);
        }

        public async Task RecompactAsync(int minLengthToCompress = -1, LogIndexUpdateStats? stats = null)
        {
            if (minLengthToCompress < 0)
                minLengthToCompress = Compressor.MinLengthToCompress;

            await CompactAsync(flush: true, stats);

            var timestamp = Stopwatch.GetTimestamp();
            var addressCount = await QueueLargeKeysCompression(topicIndex: null, minLengthToCompress);
            stats?.QueueingAddressCompression.Include(Stopwatch.GetElapsedTime(timestamp));

            timestamp = Stopwatch.GetTimestamp();
            var topicCount = 0;
            for (var topicIndex = 0; topicIndex < _topicsDbs.Length; topicIndex++)
                topicCount += await QueueLargeKeysCompression(topicIndex, minLengthToCompress);
            stats?.QueueingTopicCompression.Include(Stopwatch.GetElapsedTime(timestamp));

            _logger.Info($"Queued keys for compaction: {addressCount:N0} address, {topicCount:N0} topic");

            _compressor.WaitUntilEmpty();
            await CompactAsync(flush: true, stats);
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
            long totalTimestamp = Stopwatch.GetTimestamp();

            SemaphoreSlim semaphore = _setReceiptsSemaphores[isBackwardSync];
            await LockRunAsync(semaphore);

            try
            {
                IWriteBatch addressBatch = _addressDb.StartWriteBatch();
                IWriteBatch[] topicBatches = _topicsDbs.Select(static db => db.StartWriteBatch()).ToArray();

                // Add values to batches
                long timestamp;
                if (!aggregate.IsEmpty)
                {
                    timestamp = Stopwatch.GetTimestamp();

                    // Add addresses
                    foreach (var (address, blockNums) in aggregate.Address)
                    {
                        SaveBlockNumbersByKey(addressBatch, address.Bytes, blockNums, isBackwardSync, stats);
                    }

                    // Add topics
                    for (var topicIndex = 0; topicIndex < aggregate.Topic.Length; topicIndex++)
                    {
                        var topics = aggregate.Topic[topicIndex];

                        foreach (var (topic, blockNums) in topics)
                            SaveBlockNumbersByKey(topicBatches[topicIndex], topic.Bytes, blockNums, isBackwardSync, stats);
                    }

                    stats?.Processing.Include(Stopwatch.GetElapsedTime(timestamp));
                }

                timestamp = Stopwatch.GetTimestamp();

                // Update ranges in DB
                (int min, int max) addressRange;
                if (HasRunBefore)
                {
                    addressRange = SaveAddressBlockNumbers(addressBatch, aggregate, isBackwardSync);
                }
                else
                {
                    lock (_firstRunLock)
                        addressRange = SaveAddressBlockNumbers(addressBatch, aggregate, isBackwardSync);
                }

                (int?[] min, int?[] max) topicRanges = (min: _topicMinBlocks.ToArray(), max: _topicMaxBlocks.ToArray());
                for (var topicIndex = 0; topicIndex < topicBatches.Length; topicIndex++)
                {
                    IWriteBatch topicBatch = topicBatches[topicIndex];

                    (topicRanges.min[topicIndex], topicRanges.max[topicIndex]) =
                        SaveTopicBlockNumbers(topicIndex, topicBatch, aggregate, isBackwardSync);
                }

                stats?.UpdatingMeta.Include(Stopwatch.GetElapsedTime(timestamp));

                // Notify we have the first block
                _firstBlockAddedSource.TrySetResult();

                // Submit batches
                // TODO: return batches in case of an error without writing anything
                timestamp = Stopwatch.GetTimestamp();
                addressBatch.Dispose();
                topicBatches.ForEach(static b => b.Dispose());
                stats?.WaitingBatch.Include(Stopwatch.GetElapsedTime(timestamp));

                // Update ranges in memory
                if (HasRunBefore)
                {
                    if (isBackwardSync)
                        (_addressMinBlock, _topicMinBlocks) = (addressRange.min, topicRanges.min);
                    else
                        (_addressMaxBlock, _topicMaxBlocks) = (addressRange.max, topicRanges.max);
                }
                else
                {
                    lock (_firstRunLock)
                    {
                        (_addressMinBlock, _addressMaxBlock) = addressRange;
                        (_topicMinBlocks, _topicMaxBlocks) = topicRanges;
                    }
                }

                // Enqueue compaction if needed
                _compactor.TryEnqueue();
            }
            finally
            {
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
        private static void SaveBlockNumbersByKey(
            IWriteBatch dbBatch, ReadOnlySpan<byte> key, IReadOnlyList<int> blockNums,
            bool isBackwardSync, LogIndexUpdateStats? stats
        )
        {
            var dbKeyArray = _arrayPool.Rent(MaxDbKeyLength);

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

                dbBatch.Merge(dbKey, newValue);
                stats?.CallingMerge.Include(Stopwatch.GetElapsedTime(timestamp));
            }
            finally
            {
                _arrayPool.Return(dbKeyArray);
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

        private static unsafe ReadOnlySpan<int> Decompress(ReadOnlySpan<byte> data, ReadOnlySpan<int> decompressedBlockNumbers)
        {
            fixed (byte* dataPtr = data)
            fixed (int* decompressedPtr = decompressedBlockNumbers)
                _ = TurboPFor.p4nd1dec256v32(dataPtr, (nuint)decompressedBlockNumbers.Length, decompressedPtr);

            return decompressedBlockNumbers;
        }

        // TODO: test on big-endian system?
        private static unsafe ReadOnlySpan<byte> Compress(Span<byte> data, Span<byte> buffer)
        {
            int length;
            ReadOnlySpan<int> blockNumbers = MemoryMarshal.Cast<byte, int>(data);

            fixed (int* blockNumbersPtr = blockNumbers)
            fixed (byte* compressedPtr = buffer)
            {
                // TODO: test different deltas and block sizes
                length = (int)TurboPFor.p4nd1enc256v32(blockNumbersPtr, (nuint)blockNumbers.Length, compressedPtr);
            }

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

        private IDb GetDb(int? topicIndex) => topicIndex.HasValue ? _topicsDbs[topicIndex.Value] : _addressDb;

        private static byte[] CompressDbValue(Span<byte> data)
        {
            if (IsCompressed(data, out _))
                throw ValidationException("Data is already compressed.");
            if (data.Length % BlockNumSize != 0)
                throw ValidationException("Invalid data length.");

            // TODO: use same array as destination if possible
            Span<byte> buffer = new byte[data.Length + BlockNumSize];
            WriteCompressionMarker(buffer, data.Length / BlockNumSize);
            ReadOnlySpan<byte> compressed = Compress(data, buffer[BlockNumSize..]);

            compressed = buffer[..(BlockNumSize + compressed.Length)];
            return compressed.ToArray();
        }

        private static int[] DecompressDbValue(ReadOnlySpan<byte> data)
        {
            if (!IsCompressed(data, out int len))
                throw new ValidationException("Data is not compressed");

            // TODO: reuse buffer
            ReadOnlySpan<int> buffer = new int[len + 1]; // +1 fixes TurboPFor reading outside of array bounds
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

        private static readonly string[] SizeSuffixes = ["B", "KB", "MB", "GB", "TB", "PB"];

        private static string FormatSize(double size)
        {
            int index = 0;
            while (size >= 1024 && index < SizeSuffixes.Length - 1)
            {
                size /= 1024;
                index++;
            }

            return $"{size:0.##} {SizeSuffixes[index]}";
        }
    }
}
