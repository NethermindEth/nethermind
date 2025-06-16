using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
#pragma warning disable CS0162 // Unreachable code detected

namespace Nethermind.Db
{
    // TODO: get rid of InvalidOperationExceptions - these are for state validation
    [SuppressMessage("ReSharper", "PrivateFieldCanBeConvertedToLocalVariable")] // TODO: get rid of unused fields
    public sealed class LogIndexStorage : ILogIndexStorage
    {
        private class MergeOperator(LogIndexStorage storage): IMergeOperator
        {
            private SetReceiptsStats _stats = new();

            public string Name => $"{nameof(LogIndexStorage)}.{nameof(MergeOperator)}";

            public byte[] FullMerge(ReadOnlySpan<byte> key, RocksDbMergeEnumerator enumerator, out bool success) =>
                Merge(key, enumerator, out success);

            public byte[] PartialMerge(ReadOnlySpan<byte> key, RocksDbMergeEnumerator enumerator, out bool success) =>
                Merge(key, enumerator, out success);

            public SetReceiptsStats GetAndResetStats()
            {
                return Interlocked.Exchange(ref _stats, new());
            }

            private static int BinarySearchInt32LE(ReadOnlySpan<byte> data, int target)
            {
                int count = data.Length / sizeof(int);
                int left = 0, right = count - 1;

                while (left <= right)
                {
                    int mid = left + (right - left) / 2;
                    int offset = mid * 4;

                    int value = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, sizeof(int)));

                    if (value == target)
                        return offset;
                    if (value < target)
                        left = mid + 1;
                    else
                        right = mid - 1;
                }

                return ~(left * sizeof(int));
            }

            private static void ReverseInt32(Span<byte> data) => MemoryMarshal.Cast<byte, int>(data).Reverse();

            private static bool IsRevertBlockOp(ReadOnlySpan<byte> operand, out int blockNumber)
            {
                if (operand.Length == BlockNumSize + 1 && operand[0] == RevertOperator)
                {
                    blockNumber = ReadValLastBlockNum(operand);
                    return true;
                }

                blockNumber = 0;
                return false;
            }

            // TODO: avoid array copying in case of a single value?
            private byte[] Merge(ReadOnlySpan<byte> key, RocksDbMergeEnumerator enumerator, out bool success)
            {
                var lastBlockNum = -1;
                var timestamp = Stopwatch.GetTimestamp();

                var isBackwardSync = IsBackwardSync(key);

                try
                {
                    success = true;

                    // TODO: get rid of foreach if they cause allocations
                    IEnumerable<int> indexes = Enumerable.Range(0, enumerator.Count);
                    if (isBackwardSync) indexes = indexes.Reverse();

                    // Calculate total length
                    var resultLength = 0;
                    foreach (var i in indexes)
                    {
                        ReadOnlySpan<byte> value = enumerator.Get(i);

                        if (!IsRevertBlockOp(value, out _))
                            resultLength += value.Length;
                    }

                    // Concat all values
                    var shift = 0;
                    var result = new byte[resultLength]; // TODO: try to use ArrayPool for result
                    foreach (var i in indexes)
                    {
                        Span<byte> value = enumerator.Get(i);

                        // Do revert if requested
                        if (IsRevertBlockOp(value, out int revertBlock))
                        {
                            if (isBackwardSync)
                                throw ValidationException("Reversion is not supported for backward sync.");

                            // TODO: detect if revert block is already compressed
                            var revertIndex = BinarySearchInt32LE(result.AsSpan(..shift), revertBlock);
                            shift = revertIndex >= 0 ? revertIndex : ~revertIndex;
                            continue;
                        }

                        // Reverse if coming from backward sync
                        if (isBackwardSync)
                        {
                            ReverseInt32(value);
                        }

                        var firstBlockNum = ReadValBlockNum(value);

                        // Validate we are merging non-intersecting segments - to prevent data corruption
                        if (!IsNextBlockNewer(next: firstBlockNum, last: lastBlockNum, false))
                        {
                            // setting success=false instead of throwing during background merge may simply hide the error
                            // TODO: check if this can be handled better, for example via paranoid_checks=true
                            throw ValidationException($"Invalid order during merge: {lastBlockNum} -> {firstBlockNum} (backwards: {isBackwardSync})");
                        }

                        lastBlockNum = firstBlockNum;
                        value.CopyTo(result.AsSpan(shift..));
                        shift += value.Length;
                    }

                    if (result.Length > MaxUncompressedLength)
                    {
                        storage.EnqueueCompress(key.ToArray());
                    }

                    return result.Length == shift ? result : result[..shift];
                }
                finally
                {
                    _stats.InMemoryMerging.Include(Stopwatch.GetElapsedTime(timestamp));
                }
            }
        }

        // Use value that we won't encounter during iterator Seek or SeekForPrev
        private static readonly byte[] LastBlockNumKey = Enumerable.Repeat(byte.MaxValue, Math.Max(Address.Size, Hash256.Size) + 1).ToArray();

        private readonly IColumnsDb<LogIndexColumns> _columnsDb;
        private readonly IDb _addressDb;
        private readonly IDb _topicsDb;
        private readonly ILogger _logger;
        private readonly int _ioParallelism;
        private const int BlockNumSize = sizeof(int);
        private const int BlockMaxVal = int.MaxValue;
        private const int BlockMinVal = int.MinValue;
        public const int MaxUncompressedLength = 128 * BlockNumSize;
        private const byte RevertOperator = (byte)'-';

        private readonly int _compactionDistance;
        private readonly MergeOperator _mergeOperator;

        // TODO: get rid of static fields
        // A lot of duplicates in case of regular Channel, TODO: find a better way to guarantee uniqueness
        private readonly ConcurrentDictionary<byte[], bool> _compressQueue = new(Bytes.EqualityComparer);
        private void EnqueueCompress(byte[] dbKey) => _compressQueue.TryAdd(dbKey, true);

        // TODO: ensure class is singleton
        public LogIndexStorage(IDbFactory dbFactory, ILogger logger,
            int ioParallelism, int compactionDistance)
        {
            if (ioParallelism < 1) throw new ArgumentException("IO parallelism degree must be a positive value.", nameof(ioParallelism));
            _ioParallelism = ioParallelism;

            if (compactionDistance < 1) throw new ArgumentException("Compaction distance must be a positive value.", nameof(compactionDistance));
            _compactionDistance = compactionDistance;

            _logger = logger;

            _columnsDb = dbFactory.CreateColumnsDb<LogIndexColumns>(new("logIndexStorage", DbNames.LogIndex)
            {
                MergeOperator = _mergeOperator = new(this)
            });

            _addressDb = _columnsDb.GetColumnDb(LogIndexColumns.Addresses);
            _topicsDb = _columnsDb.GetColumnDb(LogIndexColumns.Topics);
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

        public async Task StopAsync()
        {
            await _setReceiptsSemaphore.WaitAsync();

            try
            {
                // TODO: check if needed
                _addressDb.Flush();
                _topicsDb.Flush();

                if (_logger.IsInfo) _logger.Info("Log index storage stopped");
            }
            finally
            {
                _setReceiptsSemaphore.Release();
            }
        }

        ValueTask IAsyncDisposable.DisposeAsync()
        {
            _compressQueue.Clear();

            // TODO: dispose ColumnsDB?
            _setReceiptsSemaphore.Dispose();
            _columnsDb.Dispose();
            _addressDb.Dispose();
            _topicsDb.Dispose();

            return ValueTask.CompletedTask;
        }

        private int _addressLastKnownBlock = -1;
        private int _topicLastKnownBlock = -1;

        public int GetLastKnownBlockNumber()
        {
            if (_addressLastKnownBlock < 0)
                _addressLastKnownBlock = ReadLastKnownBlockNumber(_addressDb);

            if (_topicLastKnownBlock < 0)
                _topicLastKnownBlock = ReadLastKnownBlockNumber(_topicsDb);

            return Math.Min(_addressLastKnownBlock, _topicLastKnownBlock);
        }

        private static bool IsNextBlockNewer(int next, int last, bool isBackwardSync)
        {
            if (last < 0) return true;
            return isBackwardSync ? next < last : next > last;
        }

        private bool IsNextBlockNewer(int next, bool isBackwardSync) => IsNextBlockNewer(next, GetLastKnownBlockNumber(), isBackwardSync);

        public IEnumerable<int> GetBlockNumbersFor(Address address, int from, int to)
        {
            return GetBlockNumbersFor(_addressDb, address.Bytes, from, to);
        }

        public IEnumerable<int> GetBlockNumbersFor(Hash256 topic, int from, int to)
        {
            return GetBlockNumbersFor(_topicsDb, topic.Bytes.ToArray(), from, to);
        }

        private IEnumerable<int> GetBlockNumbersFor(IDb db, byte[] keyPrefix, int from, int to)
        {
            var timestamp = Stopwatch.GetTimestamp();

            static bool IsInKeyBounds(IIterator<byte[], byte[]> iterator, byte[] key)
            {
                return iterator.Valid() && iterator.Key().AsSpan()[..key.Length].SequenceEqual(key);
            }

            using IIterator<byte[], byte[]> iterator = db.GetIterator(true);

            var dbKeyLength = keyPrefix.Length + BlockNumSize;
            var dbKeyBuffer = ArrayPool<byte>.Shared.Rent(dbKeyLength);
            Span<byte> dbKey = dbKeyBuffer.AsSpan(..dbKeyLength);

            // Find the last index for the given key, starting at or before `from`
            CreateDbKey(keyPrefix, from, dbKey);
            iterator.SeekForPrev(dbKey);

            // Otherwise, find the first index for the given key
            // TODO: achieve in a single seek?
            if (!IsInKeyBounds(iterator, keyPrefix))
            {
                iterator.SeekToFirst();
                iterator.Seek(keyPrefix);
            }

            try
            {
                while (IsInKeyBounds(iterator, keyPrefix))
                {
                    var value = iterator.Value();
                    foreach (var block in IterateBlockNumbers(iterator.Value(), from, to))
                        yield return block;

                    if (ReadValLastBlockNum(value) >= to)
                        break;

                    iterator.Next();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(dbKeyBuffer);

                // TODO: log in Debug
                _logger.Info($"GetBlockNumbersFor({Convert.ToHexString(keyPrefix)}, {from}, {to}) in {Stopwatch.GetElapsedTime(timestamp)}");
            }
        }

        private static IEnumerable<int> IterateBlockNumbers(byte[]? data, int from, int to)
        {
            if (data == null)
                yield break;

            var blockNums = data.Length == 0 || ReadCompressionMarker(data) <= 0
                ? ReadBlockNums(data)
                : DecompressDbValue(data);

            int startIndex = BinarySearch(blockNums, from);
            if (startIndex < 0)
            {
                startIndex = ~startIndex;
            }

            for (int i = startIndex; i < blockNums.Length; i++)
            {
                int block = blockNums[i];
                if (block > to)
                    yield break;

                yield return block;
            }
        }

        private const bool IncludeTopicIndex = false;

        private static byte[] BuildTopicKey(Hash256 topic, byte topicIndex)
        {
            var key = new byte[Hash256.Size + (IncludeTopicIndex ? 1 : 0)];
            BuildTopicKey(topic, topicIndex, key);
            return key;
        }

        private static void BuildTopicKey(Hash256 topic, byte topicIndex, byte[] buffer)
        {
            topic.Bytes.CopyTo(buffer);
            if (IncludeTopicIndex) buffer[^1] = topicIndex;
        }

        private IDb GetDbByKeyLength(int length) => length switch
        {
            Address.Size => _addressDb,
            Hash256.Size when !IncludeTopicIndex => _topicsDb,
            Hash256.Size + 1 when IncludeTopicIndex => _topicsDb,
            var size => throw ValidationException($"Unexpected key of {size} bytes.")
        };

        // TODO: optimize allocations
        private Dictionary<byte[], List<int>>? BuildProcessingDictionary(
            BlockReceipts[] batch, SetReceiptsStats stats, bool isBackwardSync
        )
        {
            if (!IsNextBlockNewer(batch[^1].BlockNumber, isBackwardSync))
                return null;

            var timestamp = Stopwatch.GetTimestamp();

            var blockNumsByKey = new Dictionary<byte[], List<int>>(Bytes.EqualityComparer);
            foreach ((var blockNumber, TxReceipt[] receipts) in batch)
            {
                if (!IsNextBlockNewer(blockNumber, isBackwardSync))
                    continue;

                stats.BlocksAdded++;

                foreach (TxReceipt receipt in receipts)
                {
                    stats.TxAdded++;

                    if (receipt.Logs == null)
                        continue;

                    foreach (LogEntry log in receipt.Logs)
                    {
                        stats.LogsAdded++;

                        List<int> addressNums = blockNumsByKey.GetOrAdd(log.Address.Bytes, _ => new(1));

                        if (IsNextBlockNewer(next: blockNumber, last: _addressLastKnownBlock, isBackwardSync) &&
                            (addressNums.Count == 0 || addressNums[^1] != blockNumber))
                        {
                            addressNums.Add(blockNumber);
                        }

                        if (IsNextBlockNewer(next: blockNumber, last: _topicLastKnownBlock, isBackwardSync))
                        {
                            for (byte i = 0; i < log.Topics.Length; i++)
                            {
                                stats.TopicsAdded++;

                                var topic = log.Topics[i];
                                var topicKey = BuildTopicKey(topic, i);

                                var topicNums = blockNumsByKey.GetOrAdd(topicKey, _ => new(1));
                                if (topicNums.Count == 0 || topicNums[^1] != blockNumber)
                                    topicNums.Add(blockNumber);
                            }
                        }
                    }
                }
            }

            stats.KeysCount.Include(blockNumsByKey.Count);
            stats.BuildingDictionary.Include(Stopwatch.GetElapsedTime(timestamp));

            return blockNumsByKey;
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

        // Used for:
        // - blocking concurrent executions
        // - ensuring current migration task is completed before stopping
        private readonly SemaphoreSlim _setReceiptsSemaphore = new(1, 1);

        public Task<SetReceiptsStats> SetReceiptsAsync(int blockNumber, TxReceipt[] receipts, bool isBackwardSync)
        {
            return SetReceiptsAsync([new(blockNumber, receipts)], isBackwardSync);
        }

        public async Task RevertFrom(BlockReceipts block)
        {
            if (!await _setReceiptsSemaphore.WaitAsync(TimeSpan.Zero, CancellationToken.None))
                throw new InvalidOperationException($"{nameof(LogIndexStorage)} does not support concurrent invocations.");

            var keyArray = ArrayPool<byte>.Shared.Rent(Hash256.Size + BlockNumSize);
            var valueArray = ArrayPool<byte>.Shared.Rent(BlockNumSize + 1);

            Span<byte> addressKey = keyArray.AsSpan(0, Address.Size + BlockNumSize);
            Span<byte> topicKey = keyArray.AsSpan(0, Hash256.Size + BlockNumSize);

            IWriteBatch addressBatch = _addressDb.StartWriteBatch();
            IWriteBatch topicBatch = _topicsDb.StartWriteBatch();

            Span<byte> dbValue = valueArray.AsSpan(0, BlockNumSize + 1);
            dbValue[0] = RevertOperator;
            WriteValBlockNum(dbValue[1..], block.BlockNumber);

            try
            {
                foreach (TxReceipt receipt in block.Receipts)
                foreach (LogEntry log in receipt.Logs ?? [])
                {
                    CreateMergeDbKey(log.Address.Bytes, addressKey, isBackwardSync: false);
                    addressBatch.Merge(addressKey, dbValue);

                    foreach (Hash256 topic in log.Topics)
                    {
                        CreateMergeDbKey(topic.Bytes, topicKey, isBackwardSync: false);
                        topicBatch.Merge(topicKey, dbValue);
                    }
                }

                var blockNum = block.BlockNumber;
                if (GetLastKnownBlockNumber() < blockNum)
                {
                    WriteLastKnownBlockNumber(addressBatch, _addressLastKnownBlock = blockNum);
                    WriteLastKnownBlockNumber(topicBatch, _topicLastKnownBlock = blockNum);
                }

                addressBatch.Dispose();
                topicBatch.Dispose();
            }
            finally
            {
                _setReceiptsSemaphore.Release();

                ArrayPool<byte>.Shared.Return(keyArray);
                ArrayPool<byte>.Shared.Return(valueArray);
            }
        }

        public SetReceiptsStats Compact()
        {
            var stats = new SetReceiptsStats();
            Compact(stats);
            return stats;
        }

        public SetReceiptsStats Recompact(int maxUncompressedLength = MaxUncompressedLength)
        {
            var stats = new SetReceiptsStats();

            var timestamp = Stopwatch.GetTimestamp();
            var addressCount = QueueLargeKeysCompression(_addressDb, maxUncompressedLength);
            stats.QueueingAddressCompression.Include(Stopwatch.GetElapsedTime(timestamp));

            timestamp = Stopwatch.GetTimestamp();
            var topicCount = QueueLargeKeysCompression(_topicsDb, maxUncompressedLength);
            stats.QueueingTopicCompression.Include(Stopwatch.GetElapsedTime(timestamp));

            _logger.Info($"Queued keys for compaction: {addressCount:N0} address, {topicCount:N0} topic");

            CompressPostMerge(stats.PostMergeProcessing);

            timestamp = Stopwatch.GetTimestamp();
            _addressDb.Flush();
            _topicsDb.Flush();
            stats.FlushingDbs.Include(Stopwatch.GetElapsedTime(timestamp));

            timestamp = Stopwatch.GetTimestamp();
            _addressDb.Compact();
            _topicsDb.Compact();
            stats.CompactingDbs.Include(Stopwatch.GetElapsedTime(timestamp));

            return stats;
        }

        private int QueueLargeKeysCompression(IDb db, int maxUncompressedLength)
        {
            var counter = 0;

            using var addressIterator = db.GetIterator();
            foreach (var (key, value) in Enumerate(addressIterator))
            {
                if (GetKeyBlockNum(key) is BlockMinVal or BlockMaxVal && value.Length > maxUncompressedLength)
                {
                    EnqueueCompress(key);
                    counter++;
                }
            }

            return counter;
        }

        private int? _lastCompactionAt;

        // batch is expected to be sorted
        public async Task<SetReceiptsStats> SetReceiptsAsync(
            BlockReceipts[] batch, bool isBackwardSync
        )
        {
            if (!await _setReceiptsSemaphore.WaitAsync(TimeSpan.Zero, CancellationToken.None))
                throw new InvalidOperationException($"{nameof(LogIndexStorage)} does not support concurrent invocations.");

            long timestamp;
            var stats = new SetReceiptsStats();

            try
            {
                Dictionary<int, IWriteBatch> dbBatches = new(2)
                {
                    [Address.Size] = _addressDb.StartWriteBatch(),
                    [Hash256.Size] = _topicsDb.StartWriteBatch()
                };

                if (BuildProcessingDictionary(batch, stats, isBackwardSync) is { Count: > 0 } dictionary)
                {
                    // Add values to batches
                    timestamp = Stopwatch.GetTimestamp();
                    foreach (var (key, blockNums) in dictionary)
                    {
                        var dbBatch = dbBatches[key.Length];
                        SaveBlockNumbersByKey(dbBatch, key, blockNums, isBackwardSync, stats);
                    }
                    stats.Processing.Include(Stopwatch.GetElapsedTime(timestamp));

                    // Compact if needed
                    _lastCompactionAt ??= batch[0].BlockNumber;
                    if (Math.Abs(batch[^1].BlockNumber - _lastCompactionAt.Value) >= _compactionDistance)
                    {
                        Compact(stats);
                        _lastCompactionAt = batch[^1].BlockNumber;
                    }
                }

                // Update last processed block number
                var lastAddedBlockNum = batch[^1].BlockNumber;
                if (IsNextBlockNewer(lastAddedBlockNum, isBackwardSync))
                {
                    WriteLastKnownBlockNumber(dbBatches[Address.Size], _addressLastKnownBlock = lastAddedBlockNum);
                    WriteLastKnownBlockNumber(dbBatches[Hash256.Size], _topicLastKnownBlock = lastAddedBlockNum);
                }

                // Submit batches
                // TODO: return batches in case of an error without writing anything
                timestamp = Stopwatch.GetTimestamp();
                foreach (var dbBatch in dbBatches.Values)
                {
                    dbBatch.Dispose();
                }
                stats.WritingBatch.Include(Stopwatch.GetElapsedTime(timestamp));

                stats.LastBlockNumber = GetLastKnownBlockNumber();
            }
            finally
            {
                _setReceiptsSemaphore.Release();
            }

            stats.Combine(_mergeOperator.GetAndResetStats());
            return stats;
        }

        private void Compact(SetReceiptsStats stats)
        {
            // TODO: log as Debug
            _logger.Warn("Log index flushing starting");
            var timestamp = Stopwatch.GetTimestamp();
            _addressDb.Flush();
            _topicsDb.Flush();
            stats.FlushingDbs.Include(Stopwatch.GetElapsedTime(timestamp));
            _logger.Warn("Log index flushing completed");

            // TODO: try keep writing during compaction
            _logger.Warn("Log index compaction starting");
            timestamp = Stopwatch.GetTimestamp();
            _addressDb.Compact();
            _topicsDb.Compact();
            stats.CompactingDbs.Include(Stopwatch.GetElapsedTime(timestamp));
            _logger.Warn("Log index compaction completed");

            _logger.Warn("Log index post-merge processing starting");
            CompressPostMerge(stats.PostMergeProcessing);
            _logger.Warn("Log index post-merge processing completed");
        }

        // TODO: optimize allocations
        private static void SaveBlockNumbersByKey(IWriteBatch dbBatch, byte[] key, IReadOnlyList<int> blockNums, bool isBackwardSync, SetReceiptsStats stats)
        {
            var dbKeyArray = ArrayPool<byte>.Shared.Rent(key.Length + BlockNumSize);
            var dbKey = dbKeyArray.AsSpan(0, key.Length + BlockNumSize);

            try
            {
                CreateMergeDbKey(key, dbKey, isBackwardSync);

                // TODO: handle writing already processed blocks
                // if (blockNums[^1] <= lastSavedNum)
                //     return;

                var newValue = CreateDbValue(blockNums);

                var timestamp = Stopwatch.GetTimestamp();

                if (newValue is null or [])
                    throw ValidationException($"No block numbers to save for {Convert.ToHexString(key)}.");

                dbBatch.Merge(dbKey, newValue);
                stats.CallingMerge.Include(Stopwatch.GetElapsedTime(timestamp));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(dbKeyArray);
            }
        }

        /// <summary>
        /// Saves a key consisting of the <c>key || block-number</c> byte array to <paramref name="dbKey"/>
        /// </summary>
        private static Span<byte> CreateDbKey(ReadOnlySpan<byte> key, int blockNumber, Span<byte> dbKey)
        {
            key.CopyTo(dbKey);
            SetKeyBlockNum(dbKey, blockNumber);

            return dbKey[..(key.Length + BlockNumSize)];
        }

        private static void CreateMergeDbKey(ReadOnlySpan<byte> key, Span<byte> dbKey, bool isBackwardSync) =>
            CreateDbKey(key, isBackwardSync ? BlockMinVal : BlockMaxVal, dbKey);

        // RocksDB uses big-endian (lexicographic) ordering
        private static int GetKeyBlockNum(ReadOnlySpan<byte> dbKey) => BinaryPrimitives.ReadInt32BigEndian(dbKey[^BlockNumSize..]);
        private static void SetKeyBlockNum(Span<byte> dbKey, int blockNumber) => BinaryPrimitives.WriteInt32BigEndian(dbKey[^BlockNumSize..], blockNumber);

        private static int BinarySearch(ReadOnlySpan<int> blocks, int from)
        {
            int index = blocks.BinarySearch(from);
            return index < 0 ? ~index : index;
        }

        private static unsafe ReadOnlySpan<int> Decompress(ReadOnlySpan<byte> data, ReadOnlySpan<int> decompressedBlockNumbers)
        {
            fixed (byte* dataPtr = data)
            fixed (int* decompressedPtr = decompressedBlockNumbers)
            {
                _ = TurboPFor.p4nd1dec256v32(dataPtr, decompressedBlockNumbers.Length, decompressedPtr);
            }

            return decompressedBlockNumbers;
        }

        private static unsafe ReadOnlySpan<byte> Compress(ReadOnlySpan<byte> data, Span<byte> buffer)
        {
            int length;
            ReadOnlySpan<int> blockNumbers = MemoryMarshal.Cast<byte, int>(data);

            fixed (int* blockNumbersPtr = blockNumbers)
            fixed (byte* compressedPtr = buffer)
            {
                // TODO: test different deltas and block sizes
                length = TurboPFor.p4nd1enc256v32(blockNumbersPtr, blockNumbers.Length, compressedPtr);
            }

            return buffer[..length];
        }

        private int ReadLastKnownBlockNumber(IDb db)
        {
            var value = db.Get(LastBlockNumKey);
            return value is { Length: > 1 } ? BinaryPrimitives.ReadInt32BigEndian(value) : -1;
        }

        private void WriteLastKnownBlockNumber(IWriteBatch dbBatch, int value)
        {
            Span<byte> buffer = ArrayPool<byte>.Shared.RentSpan(sizeof(int));
            BinaryPrimitives.WriteInt32BigEndian(buffer, value);
            dbBatch.PutSpan(LastBlockNumKey, buffer);
        }

        // used for data validation, TODO: remove, replace with tests
        private static Exception ValidationException(string message) => new InvalidOperationException(message);

        private static bool IsBackwardSync(ReadOnlySpan<byte> dbKey) => GetKeyBlockNum(dbKey) is BlockMinVal;

        // TODO: optimize allocations
        // TODO: set max block value to compress at, as revert only works for uncompressed numbers
        private void CompressPostMerge(PostMergeProcessingStats stats)
        {
            var execTimestamp = Stopwatch.GetTimestamp();

            var block = new ActionBlock<byte[]>(dbKey =>
            {
                var db = GetDbByKeyLength(dbKey.Length - BlockNumSize);

                var timestamp = Stopwatch.GetTimestamp();
                var dbValue = db.Get(dbKey) ?? throw ValidationException("Empty value in the post-merge compression queue.");
                stats.GettingValue.Include(Stopwatch.GetElapsedTime(timestamp));

                var blockNum = ReadValBlockNum(dbValue);
                var isBackwardSync = IsBackwardSync(dbKey);

                var dbKeyComp = (byte[])dbKey.Clone();
                SetKeyBlockNum(dbKeyComp, blockNum);

                // Put compressed value at a new key and clear uncompressed one
                // TODO: reading and clearing the value is not atomic, find a fix
                timestamp = Stopwatch.GetTimestamp();
                dbValue = CompressDbValue(dbValue);
                stats.CompressingValue.Include(Stopwatch.GetElapsedTime(timestamp));

                timestamp = Stopwatch.GetTimestamp();
                db.PutSpan(dbKeyComp, dbValue);
                db.PutSpan(dbKey, []);
                stats.PuttingValues.Include(Stopwatch.GetElapsedTime(timestamp));

                if (db == _addressDb) Interlocked.Increment(ref stats.CompressedAddressKeys);
                else if (db == _topicsDb) Interlocked.Increment(ref stats.CompressedTopicKeys);
            }, new() { MaxDegreeOfParallelism = _ioParallelism });

            foreach (var dbKey in _compressQueue.Keys)
            {
                _compressQueue.TryRemove(dbKey, out _);
                block.Post(dbKey);
            }

            block.Complete();
            block.Completion.Wait(); // TODO: await?

            stats.Execution.Include(Stopwatch.GetElapsedTime(execTimestamp));
        }

        public static int ReadCompressionMarker(ReadOnlySpan<byte> source) => -BinaryPrimitives.ReadInt32LittleEndian(source);
        public static void WriteCompressionMarker(Span<byte> source, int len) => BinaryPrimitives.WriteInt32LittleEndian(source, -len);

        public static void WriteValBlockNum(Span<byte> destination, int blockNum) => BinaryPrimitives.WriteInt32LittleEndian(destination, blockNum);
        public static int ReadValBlockNum(ReadOnlySpan<byte> source) => BinaryPrimitives.ReadInt32LittleEndian(source);
        public static int ReadValLastBlockNum(ReadOnlySpan<byte> source) => ReadValBlockNum(source[^BlockNumSize..]);

        public static void WriteBlockNums(Span<byte> destination, IEnumerable<int> blockNums)
        {
            var shift = 0;
            foreach (var blockNum in blockNums)
            {
                WriteValBlockNum(destination[shift..], blockNum);
                shift += BlockNumSize;
            }
        }

        public static int[] ReadBlockNums(Span<byte> source)
        {
            if (source.Length % 4 != 0)
                throw ValidationException("Invalid length for array of block numbers.");

            var result = new int[source.Length / BlockNumSize];
            for (var i = 0; i < source.Length; i += BlockNumSize)
                result[i / BlockNumSize] = ReadValBlockNum(source[i..]);

            return result;
        }

        private static byte[] CreateDbValue(IReadOnlyList<int> blockNums)
        {
            var value = new byte[blockNums.Count * BlockNumSize];
            WriteBlockNums(value, blockNums);
            return value;
        }

        private static byte[] CompressDbValue(Span<byte> data)
        {
            if (ReadCompressionMarker(data) > 0)
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
            var len = ReadCompressionMarker(data);
            if (len < 0)
                throw new ValidationException("Data is not compressed");

            var buffer = new int[len];
            var result = Decompress(data[BlockNumSize..], buffer);
            return result.ToArray();
        }
    }
}
