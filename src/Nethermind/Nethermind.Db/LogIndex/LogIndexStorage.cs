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
#pragma warning disable CS0162 // Unreachable code detected

namespace Nethermind.Db
{
    // TODO: get rid of InvalidOperationExceptions - these are for state validation
    // TODO: verify all MemoryMarshal usages - needs to be CPU-cross-compatible
    [SuppressMessage("ReSharper", "PrivateFieldCanBeConvertedToLocalVariable")] // TODO: get rid of unused fields
    public sealed partial class LogIndexStorage : ILogIndexStorage
    {
        private static class SpecialKey
        {
            private const int MaxCommonLength = Hash256.Size; // Math.Max(Address.Size, Hash256.Size)

            // Use values that we won't encounter during iterator Seek or SeekForPrev
            public static readonly byte[] MinBlockNum = Enumerable.Repeat(byte.MaxValue, MaxCommonLength)
                .Concat(new byte[] { 1 }).ToArray();

            // Use values that we won't encounter during iterator Seek or SeekForPrev
            public static readonly byte[] MaxBlockNum = Enumerable.Repeat(byte.MaxValue, MaxCommonLength)
                .Concat(new byte[] { 2 }).ToArray();
        }

        private static class SpecialPostfix
        {
            // Any ordered prefix seeking will start on it
            public static readonly byte[] BackwardMerge = Enumerable.Repeat((byte)0, BackwardMergeLength).ToArray();
            public const int BackwardMergeLength = BlockNumSize - 1;

            // Any ordered prefix seeking will end on it.
            public static readonly byte[] ForwardMerge = Enumerable.Repeat(byte.MaxValue, ForwardMergeLength).ToArray();
            public const int ForwardMergeLength = BlockNumSize + 1;

            public static Span<byte> CutFrom(Span<byte> dbKey)
            {
                if (dbKey.EndsWith(BackwardMerge))
                    return dbKey[..^BackwardMergeLength];
                else if (dbKey.EndsWith(ForwardMerge))
                    return dbKey[..^ForwardMergeLength];
                else
                    throw new ArgumentException($"Key {Convert.ToHexString(dbKey)} does not have a special postfix.", nameof(dbKey));
            }
        }

        private static class Defaults
        {
            public const int IOParallelism = 1;
            public const int MaxReorgDepth = 64;
        }

        private const int MaxKeyLength = Hash256.Size + SpecialPostfix.ForwardMergeLength;

        // TODO: consider using ArrayPoolList just for `using` syntax
        private static readonly ArrayPool<byte> _arrayPool = ArrayPool<byte>.Shared;

        private readonly IColumnsDb<LogIndexColumns> _columnsDb;
        private readonly IDb _addressDb;
        private readonly IDb _topicsDb;
        private readonly ILogger _logger;

        private const int BlockNumSize = sizeof(int);

        private readonly int _maxReorgDepth;

        private readonly Dictionary<LogIndexColumns, MergeOperator> _mergeOperators;
        private readonly ICompressor _compressor;
        private readonly ICompactor _compactor;

        private int? _addressMaxBlock;
        private int? _topicMaxBlock;
        private int? _addressMinBlock;
        private int? _topicMinBlock;

        private readonly TaskCompletionSource _firstBlockAddedSource = new();
        public Task FirstBlockAdded => _firstBlockAddedSource.Task;

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
                { LogIndexColumns.Addresses, new AddressMergeOperator(this, _compressor) },
                { LogIndexColumns.Topics, new TopicMergeOperator(this, _compressor) }
            };
            _columnsDb = dbFactory.CreateColumnsDb<LogIndexColumns>(new("logIndexStorage", DbNames.LogIndex)
            {
                MergeOperatorByColumn = _mergeOperators.ToDictionary(x => $"{x.Key}", x => (IMergeOperator)x.Value)
            });
            _addressDb = _columnsDb.GetColumnDb(LogIndexColumns.Addresses);
            _topicsDb = _columnsDb.GetColumnDb(LogIndexColumns.Topics);

            _addressMaxBlock = LoadBlockNumber(_addressDb, SpecialKey.MaxBlockNum);
            _topicMaxBlock = LoadBlockNumber(_topicsDb, SpecialKey.MaxBlockNum);
            _addressMinBlock = LoadBlockNumber(_addressDb, SpecialKey.MinBlockNum);
            _topicMinBlock = LoadBlockNumber(_topicsDb, SpecialKey.MinBlockNum);

            if ((_addressMinBlock ?? _addressMaxBlock ?? _topicMinBlock ?? _topicMaxBlock) is not null)
                _firstBlockAddedSource.SetResult();
        }

        // TODO: remove if unused
        static IEnumerable<(TKey key, TValue value)> Enumerate<TKey, TValue>(IIterator<TKey, TValue> iterator)
        {
            iterator.SeekToFirst();
            while (iterator.Valid())
            {
                yield return (iterator.Key(), iterator.Value());
                iterator.Next();
            }
        }

        // Not thread safe
        private bool _stopped;
        private bool _disposed;

        // Used for:
        // - blocking concurrent executions
        // - ensuring the current migration task is completed before stopping
        private readonly SemaphoreSlim _setReceiptsSemaphore = new(1, 1);

        public async Task StopAsync()
        {
            if (_stopped)
                return;

            await _setReceiptsSemaphore.WaitAsync();

            try
            {
                if (_stopped)
                    return;

                await _compactor.StopAsync(); // Need to wait, as releasing RocksDB during compaction will cause 0xC0000005
                await _compressor.StopAsync(); // TODO: consider not waiting for compression queue to finish

                // TODO: check if needed
                _addressDb.Flush();
                _topicsDb.Flush();

                if (_logger.IsInfo) _logger.Info("Log index storage stopped");
            }
            finally
            {
                _stopped = true;
                _setReceiptsSemaphore.Release();
            }
        }

        async ValueTask IAsyncDisposable.DisposeAsync()
        {
            if (_disposed)
                return;

            await StopAsync();

            _setReceiptsSemaphore.Dispose();
            _columnsDb.Dispose();
            _addressDb.Dispose();
            _topicsDb.Dispose();

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

        private (int min, int max) SaveTopicBlockNumbers(IWriteBatch dbBatch, LogIndexAggregate aggregate, bool isBackwardSync, bool isReorg = false) =>
            SaveBlockNumbers(dbBatch, aggregate.FirstBlockNum, aggregate.LastBlockNum, _topicMinBlock, _topicMaxBlock, isBackwardSync, isReorg);

        private (int min, int max) SaveTopicBlockNumbers(IWriteBatch dbBatch, int block, bool isBackwardSync, bool isReorg = false) =>
            SaveBlockNumbers(dbBatch, block, block, _topicMinBlock, _topicMaxBlock, isBackwardSync, isReorg);

        private int GetLastReorgableBlockNumber() => Math.Min(_addressMaxBlock ?? 0, _topicMaxBlock ?? 0) - _maxReorgDepth;

        private static bool IsBlockNewer(int next, int? lastMin, int? lastMax, bool isBackwardSync) => isBackwardSync
            ? lastMin is null || next < lastMin
            : lastMax is null || next > lastMax;

        private bool IsAddressBlockNewer(int next, bool isBackwardSync) => IsBlockNewer(next, _addressMinBlock, _addressMaxBlock, isBackwardSync);
        private bool IsTopicBlockNewer(int next, bool isBackwardSync) => IsBlockNewer(next, _topicMinBlock, _topicMaxBlock, isBackwardSync);
        private bool IsBlockNewer(int next, bool isBackwardSync) => IsAddressBlockNewer(next, isBackwardSync) || IsTopicBlockNewer(next, isBackwardSync);

        public int? GetMaxBlockNumber() => _addressMaxBlock is { } addressMaxBlock && _topicMaxBlock is { } topicMaxBlock
            ? Math.Min(addressMaxBlock, topicMaxBlock)
            : null;

        public int? GetMinBlockNumber() => _addressMinBlock is { } addressMinBlock && _topicMinBlock is { } topicMinBlock
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

        private Dictionary<byte[], int[]> GetKeysFor(int? index, byte[] key, int from, int to, bool includeValues = false)
        {
            static bool IsInKeyBounds(IIterator<byte[], byte[]> iterator, byte[] key)
            {
                return iterator.Valid() && iterator.Key().AsSpan().StartsWith(key);
            }

            byte[] dbKeyBuffer = null;
            var result = new Dictionary<byte[], int[]>(Bytes.EqualityComparer);

            try
            {
                // Adjust parameters to avoid composing invalid lookup keys
                if (from < 0) from = 0;
                if (to < from) return result;

                var dbKeyLength = key.Length + BlockNumSize;
                dbKeyBuffer = _arrayPool.Rent(dbKeyLength);
                Span<byte> dbKey = dbKeyBuffer.AsSpan(..dbKeyLength);

                IDb db = index is null ? _addressDb : _topicsDb;
                using IIterator<byte[], byte[]> iterator = db.GetIterator(true);

                // Find the last index for the given key, starting at or before `from`
                CreateDbKey(index, key, from, dbKey);
                iterator.SeekForPrev(dbKey);

                // Otherwise, find the first index for the given key
                // TODO: achieve in a single seek?
                if (!IsInKeyBounds(iterator, key))
                {
                    iterator.SeekToFirst();
                    iterator.Seek(key);
                }

                using var buffer = new ArrayPoolList<int>(includeValues ? 128 : 0);
                while (IsInKeyBounds(iterator, key))
                {
                    var value = iterator.Value();
                    foreach (var block in EnumerateBlockNumbers(value, from))
                    {
                        if (block > to)
                        {
                            result.Add(iterator.Key(), buffer.AsSpan().ToArray());
                            return result;
                        }

                        if (includeValues)
                            buffer.Add(block);
                    }

                    result.Add(iterator.Key(), buffer.AsSpan().ToArray());

                    buffer.Clear();
                    iterator.Next();
                }

                return result;
            }
            finally
            {
                if (dbKeyBuffer != null) _arrayPool.Return(dbKeyBuffer);
            }
        }

        // TODO: use ArrayPoolList?
        public List<int> GetBlockNumbersFor(Address address, int from, int to)
        {
            return GetBlockNumbersFor(null, address.Bytes, from, to);
        }

        public List<int> GetBlockNumbersFor(int index, Hash256 topic, int from, int to)
        {
            return GetBlockNumbersFor(index, topic.Bytes.ToArray(), from, to);
        }

        private List<int> GetBlockNumbersFor(int? index, byte[] keyPrefix, int from, int to)
        {
            static bool IsInKeyBounds(IIterator<byte[], byte[]> iterator, byte[] key)
            {
                return iterator.Valid() && iterator.Key().AsSpan().StartsWith(key);
            }

            var timestamp = Stopwatch.GetTimestamp();
            byte[] dbKeyBuffer = null;

            var result = new List<int>();

            try
            {
                // Adjust parameters to avoid composing invalid lookup keys
                if (from < 0) from = 0;
                if (to < from) return result;

                var dbKeyLength = keyPrefix.Length + BlockNumSize;
                dbKeyBuffer = _arrayPool.Rent(dbKeyLength);
                Span<byte> dbKey = dbKeyBuffer.AsSpan(..dbKeyLength);

                IDb? db = index is null ? _addressDb : _topicsDb;
                using IIterator<byte[], byte[]> iterator = db.GetIterator(true);

                // Find the last index for the given key, starting at or before `from`
                CreateDbKey(index, keyPrefix, from, dbKey);
                iterator.SeekForPrev(dbKey);

                // Otherwise, find the first index for the given key
                // TODO: achieve in a single seek?
                if (!IsInKeyBounds(iterator, keyPrefix))
                {
                    iterator.SeekToFirst();
                    iterator.Seek(keyPrefix);
                }

                while (IsInKeyBounds(iterator, keyPrefix))
                {
                    var value = iterator.Value();

                    foreach (var block in EnumerateBlockNumbers(value, from))
                    {
                        if (block > to)
                            return result;

                        result.Add(block);
                    }

                    iterator.Next();
                }

                return result;
            }
            finally
            {
                if (dbKeyBuffer != null) _arrayPool.Return(dbKeyBuffer);

                if (_logger.IsTrace) _logger.Trace($"GetBlockNumbersFor({Convert.ToHexString(keyPrefix)}, {from}, {to}) in {Stopwatch.GetElapsedTime(timestamp)}");
            }
        }

        private static IEnumerable<int> EnumerateBlockNumbers(byte[]? data, int from)
        {
            if (data == null)
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

        private static Span<byte> BuildTopicKey(int topicIndex, ReadOnlySpan<byte> topic, Span<byte> buffer)
        {
            var index = topic.LeadingZerosCount();
            ReadOnlySpan<byte> bytes = topic[index..];

            bytes.CopyTo(buffer);

            // Reduces space requirement while introducing very low risk of collisions (and false positives)
            buffer[0] ^= (byte)topicIndex;

            return buffer[..bytes.Length];
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

                        if (IsTopicBlockNewer(blockNumber, isBackwardSync))
                        {
                            for (byte i = 0; i < log.Topics.Length; i++)
                            {
                                stats?.IncrementTopics();

                                var topicNums = aggregate.Topic.GetOrAdd(log.Topics[i], static _ => new(1));
                                if (topicNums.Count == 0 || topicNums[^1] != blockNumber)
                                    topicNums.Add(blockNumber);
                            }
                        }
                    }
                }
            }

            stats?.KeysCount.Include(aggregate.Address.Count + aggregate.Topic.Count);
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

        public async Task ReorgFrom(BlockReceipts block)
        {
            if (!await _setReceiptsSemaphore.WaitAsync(TimeSpan.Zero, CancellationToken.None))
                throw new InvalidOperationException($"{nameof(LogIndexStorage)} does not support concurrent invocations.");

            byte[]? keyArray = null, valueArray = null;

            try
            {
                keyArray = _arrayPool.Rent(MaxKeyLength);
                valueArray = _arrayPool.Rent(BlockNumSize + 1);

                IWriteBatch addressBatch = _addressDb.StartWriteBatch();
                IWriteBatch topicBatch = _topicsDb.StartWriteBatch();

                Span<byte> dbValue = MergeOps.Create(MergeOp.ReorgOp, block.BlockNumber, valueArray);

                foreach (TxReceipt receipt in block.Receipts)
                {
                    foreach (LogEntry log in receipt.Logs ?? [])
                    {
                        ReadOnlySpan<byte> addressKey = CreateMergeDbKey(log.Address.Bytes, keyArray, isBackwardSync: false);
                        addressBatch.Merge(addressKey, dbValue);

                        foreach (Hash256 topic in log.Topics)
                        {
                            ReadOnlySpan<byte> topicKey = CreateMergeDbKey(topic.Bytes, keyArray, isBackwardSync: false);
                            topicBatch.Merge(topicKey, dbValue);
                        }
                    }
                }

                // Need to update last block number, so that new-receipts comparison won't fail when rewriting it
                // TODO: figure out if this can be improved, maybe don't use comparison checks at all
                var blockNum = block.BlockNumber - 1;
                (int min, int max) addressRange = SaveAddressBlockNumbers(addressBatch, blockNum, isBackwardSync: false, isReorg: true);
                (int min, int max) topicRange = SaveTopicBlockNumbers(topicBatch, blockNum, isBackwardSync: false, isReorg: true);

                addressBatch.Dispose();
                topicBatch.Dispose();

                (_addressMinBlock, _addressMaxBlock) = addressRange;
                (_topicMinBlock, _topicMaxBlock) = topicRange;
            }
            finally
            {
                _setReceiptsSemaphore.Release();

                if (keyArray is not null) _arrayPool.Return(keyArray);
                if (valueArray is not null) _arrayPool.Return(valueArray);
            }
        }

        public async Task CompactAsync(bool flush, LogIndexUpdateStats? stats = null)
        {
            if (!await _setReceiptsSemaphore.WaitAsync(TimeSpan.Zero, CancellationToken.None))
                throw new InvalidOperationException($"{nameof(LogIndexStorage)} does not support concurrent invocations.");

            try
            {
                // TODO: include time to stats
                if (flush)
                {
                    _addressDb.Flush();
                    _topicsDb.Flush();
                }

                CompactingStats compactStats = await _compactor.ForceAsync();
                stats?.Compacting.Combine(compactStats);
            }
            finally
            {
                _setReceiptsSemaphore.Release();
            }
        }

        public async Task RecompactAsync(int minLengthToCompress = -1, LogIndexUpdateStats? stats = null)
        {
            if (minLengthToCompress < 0)
                minLengthToCompress = Compressor.MinLengthToCompress;

            await CompactAsync(flush: true, stats);

            var timestamp = Stopwatch.GetTimestamp();
            var addressCount = await QueueLargeKeysCompression(_addressDb, minLengthToCompress);
            stats?.QueueingAddressCompression.Include(Stopwatch.GetElapsedTime(timestamp));

            timestamp = Stopwatch.GetTimestamp();
            var topicCount = await QueueLargeKeysCompression(_topicsDb, minLengthToCompress);
            stats?.QueueingTopicCompression.Include(Stopwatch.GetElapsedTime(timestamp));

            _logger.Info($"Queued keys for compaction: {addressCount:N0} address, {topicCount:N0} topic");

            _compressor.WaitUntilEmpty();
            await CompactAsync(flush: true, stats);
        }

        private async Task<int> QueueLargeKeysCompression(IDb db, int minLengthToCompress)
        {
            var counter = 0;

            using var addressIterator = db.GetIterator();
            foreach (var (key, value) in Enumerate(addressIterator))
            {
                if (IsCompressed(value) || value.Length < minLengthToCompress)
                    continue;

                if (db == _addressDb)
                    await _compressor.EnqueueAddressAsync(key);
                else if (db == _topicsDb)
                    await _compressor.EnqueueTopicAsync(key);

                counter++;
            }

            return counter;
        }

        public async Task SetReceiptsAsync(LogIndexAggregate aggregate, bool isBackwardSync, LogIndexUpdateStats? stats = null)
        {
            long totalTimestamp = Stopwatch.GetTimestamp();

            if (!await _setReceiptsSemaphore.WaitAsync(TimeSpan.Zero, CancellationToken.None))
                throw new InvalidOperationException($"{nameof(LogIndexStorage)} does not support concurrent invocations.");

            try
            {
                var dbBatch = (address: _addressDb.StartWriteBatch(), topic: _topicsDb.StartWriteBatch());

                // Add values to batches
                long timestamp;
                if (!aggregate.IsEmpty)
                {
                    timestamp = Stopwatch.GetTimestamp();

                    foreach (var (address, blockNums) in aggregate.Address)
                        SaveBlockNumbersByKey(dbBatch.address, address.Bytes, blockNums, isBackwardSync, stats);

                    foreach (var (topic, blockNums) in aggregate.Topic)
                        SaveBlockNumbersByKey(dbBatch.topic, topic.Bytes, blockNums, isBackwardSync, stats);

                    stats?.Processing.Include(Stopwatch.GetElapsedTime(timestamp));
                }

                // Update block numbers
                timestamp = Stopwatch.GetTimestamp();
                (int min, int max) addressRange = SaveAddressBlockNumbers(dbBatch.address, aggregate, isBackwardSync);
                (int min, int max) topicRange = SaveTopicBlockNumbers(dbBatch.topic, aggregate, isBackwardSync);
                stats?.UpdatingMeta.Include(Stopwatch.GetElapsedTime(timestamp));

                // Notify we have the first block
                _firstBlockAddedSource.TrySetResult();

                // Submit batches
                // TODO: return batches in case of an error without writing anything
                timestamp = Stopwatch.GetTimestamp();
                dbBatch.address.Dispose();
                dbBatch.topic.Dispose();
                stats?.WaitingBatch.Include(Stopwatch.GetElapsedTime(timestamp));

                // Update availability ranges
                (_addressMinBlock, _addressMaxBlock) = addressRange;
                (_topicMinBlock, _topicMaxBlock) = topicRange;

                // Enqueue compaction if needed
                _compactor.TryEnqueue();
            }
            finally
            {
                _setReceiptsSemaphore.Release();
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
            var dbKeyArray = _arrayPool.Rent(key.Length + SpecialPostfix.ForwardMergeLength);

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

        /// <summary>
        /// Saves a key consisting of the <c>key || block-number</c> byte array to <paramref name="dbKey"/>
        /// </summary>
        private static void CreateDbKey(int? topicIndex, ReadOnlySpan<byte> key, int blockNumber, Span<byte> dbKey)
        {
            if (topicIndex.HasValue)
                key = BuildTopicKey(topicIndex.Value, key, dbKey);

            key.CopyTo(dbKey);
            SetKeyBlockNum(dbKey, blockNumber);
        }

        private static ReadOnlySpan<byte> CreateMergeDbKey(ReadOnlySpan<byte> key, Span<byte> dbKey, bool isBackwardSync)
        {
            var postfix = isBackwardSync ? SpecialPostfix.BackwardMerge : SpecialPostfix.ForwardMerge;

            key.CopyTo(dbKey);
            postfix.CopyTo(dbKey[key.Length..]);

            return dbKey[..(key.Length + postfix.Length)];
        }

        // RocksDB uses big-endian (lexicographic) ordering
        private static int GetKeyBlockNum(ReadOnlySpan<byte> dbKey) => BinaryPrimitives.ReadInt32BigEndian(dbKey[^BlockNumSize..]);
        private static void SetKeyBlockNum(Span<byte> dbKey, int blockNumber) => BinaryPrimitives.WriteInt32BigEndian(dbKey[^BlockNumSize..], blockNumber);

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

        public static int[] ReadBlockNums(Span<byte> source)
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
