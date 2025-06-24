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
        // Use value that we won't encounter during iterator Seek or SeekForPrev
        private static readonly byte[] LastBlockNumKey = Enumerable.Repeat(byte.MaxValue, Math.Max(Address.Size, Hash256.Size) + 1).ToArray();

        private readonly IColumnsDb<LogIndexColumns> _columnsDb;
        private readonly IDb _addressDb;
        private readonly IDb _topicsDb;
        private readonly ILogger _logger;

        private const int BlockNumSize = sizeof(int);

        // Special RocksDB key postfix - any ordered prefix seeking will start on it.
        private static readonly byte[] BackwardMergeKey = Enumerable.Repeat((byte)0, BackwardMergeKeyLength).ToArray();
        private const int BackwardMergeKeyLength = BlockNumSize - 1;

        // Special RocksDB key postfix - any ordered prefix seeking will end on it.
        private static readonly byte[] ForwardMergeKey = Enumerable.Repeat(byte.MaxValue, ForwardMergeKeyLength).ToArray();
        private const int ForwardMergeKeyLength = BlockNumSize + 1;

        private readonly int _compactionDistance;
        private readonly int _maxReorgDepth;
        private readonly MergeOperator _mergeOperator;
        private readonly Compressor _compressor;

        // TODO: ensure class is singleton
        // TODO: take parameters from log-index/chain config
        public LogIndexStorage(IDbFactory dbFactory, ILogger logger,
            int ioParallelism, int compactionDistance, int maxReorgDepth = 64)
        {

            if (compactionDistance < 1) throw new ArgumentException("Compaction distance must be a positive value.", nameof(compactionDistance));
            _compactionDistance = compactionDistance;

            if (maxReorgDepth < 0) throw new ArgumentException("Compaction distance must be a positive value.", nameof(compactionDistance));
            _maxReorgDepth = maxReorgDepth;

            _logger = logger;
            _compressor = new(this, logger, ioParallelism);
            _columnsDb = dbFactory.CreateColumnsDb<LogIndexColumns>(new("logIndexStorage", DbNames.LogIndex)
            {
                MergeOperator = _mergeOperator = new(_compressor)
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
                // TODO: consider not waiting for compression queue to finish
                await _compressor.StopAsync();

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

                    foreach (var block in EnumerateBlockNumbers(value, from, to))
                        yield return block;

                    if (value.Length > 0 && ReadValLastBlockNum(value) >= to)
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

        private static IEnumerable<int> EnumerateBlockNumbers(byte[]? data, int from, int to)
        {
            if (data == null)
                yield break;

            var blockNums = data.Length == 0 || ReadCompressionMarker(data) <= 0
                ? ReadBlockNums(data)
                : DecompressDbValue(data);

            ReverseBlocksIfNeeded(blockNums);

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

        private IDb GetDbByKeyLength(int length, out int prefixLength)
        {
            if (IncludeTopicIndex) length -= 1;

            if (length - Hash256.Size is BlockNumSize or ForwardMergeKeyLength or BackwardMergeKeyLength)
            {
                prefixLength = Hash256.Size;
                return _topicsDb;
            }

            if (length - Address.Size is BlockNumSize or ForwardMergeKeyLength or BackwardMergeKeyLength)
            {
                prefixLength = Address.Size;
                return _addressDb;
            }

            throw ValidationException($"Unexpected key of {length} bytes.");
        }

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

        public async Task ReorgFrom(BlockReceipts block)
        {
            if (!await _setReceiptsSemaphore.WaitAsync(TimeSpan.Zero, CancellationToken.None))
                throw new InvalidOperationException($"{nameof(LogIndexStorage)} does not support concurrent invocations.");

            var keyArray = ArrayPool<byte>.Shared.Rent(Hash256.Size + BackwardMergeKey.Length);
            var valueArray = ArrayPool<byte>.Shared.Rent(BlockNumSize + 1);

            IWriteBatch addressBatch = _addressDb.StartWriteBatch();
            IWriteBatch topicBatch = _topicsDb.StartWriteBatch();

            Span<byte> dbValue = MergeOps.Create(MergeOp.ReorgOp, block.BlockNumber, valueArray);

            try
            {
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

                var blockNum = block.BlockNumber;
                if (GetLastKnownBlockNumber() >= blockNum)
                {
                    WriteLastKnownBlockNumber(addressBatch, _addressLastKnownBlock = blockNum - 1);
                    WriteLastKnownBlockNumber(topicBatch, _topicLastKnownBlock = blockNum - 1);
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

        public SetReceiptsStats Compact(bool waitForCompression)
        {
            var stats = new SetReceiptsStats();
            Compact(stats, waitForCompression: waitForCompression);
            return stats;
        }

        public SetReceiptsStats Recompact(int minLengthToCompress = -1)
        {
            if (minLengthToCompress < 0)
                minLengthToCompress = Compressor.MinLengthToCompress;

            var stats = new SetReceiptsStats();

            var timestamp = Stopwatch.GetTimestamp();
            var addressCount = QueueLargeKeysCompression(_addressDb, minLengthToCompress);
            stats.QueueingAddressCompression.Include(Stopwatch.GetElapsedTime(timestamp));

            timestamp = Stopwatch.GetTimestamp();
            var topicCount = QueueLargeKeysCompression(_topicsDb, minLengthToCompress);
            stats.QueueingTopicCompression.Include(Stopwatch.GetElapsedTime(timestamp));

            _logger.Info($"Queued keys for compaction: {addressCount:N0} address, {topicCount:N0} topic");

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

        private int QueueLargeKeysCompression(IDb db, int minLengthToCompress)
        {
            var counter = 0;

            using var addressIterator = db.GetIterator();
            foreach (var (key, value) in Enumerate(addressIterator))
            {
                if (IsMergeKey(key) && value.Length >= minLengthToCompress)
                {
                    _compressor.Enqueue(key);
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
            stats.PostMergeProcessing.Combine(_compressor.GetAndResetStats());
            return stats;
        }

        // TODO: log as Debug or not log at all!
        private void Compact(SetReceiptsStats stats, bool flush = false, bool waitForCompression = false)
        {
            if (flush)
            {
                _logger.Warn("Log index flushing starting");
                var timestamp = Stopwatch.GetTimestamp();
                _addressDb.Flush();
                _topicsDb.Flush();
                stats.FlushingDbs.Include(Stopwatch.GetElapsedTime(timestamp));
                _logger.Warn("Log index flushing completed");
            }

            {
                // TODO: try keep writing during compaction
                _logger.Warn("Log index compaction starting");
                var timestamp = Stopwatch.GetTimestamp();
                _addressDb.Compact();
                _topicsDb.Compact();
                stats.CompactingDbs.Include(Stopwatch.GetElapsedTime(timestamp));
                _logger.Warn("Log index compaction completed");
            }

            if (waitForCompression)
            {
                _compressor.WaitUntilEmpty();
            }
        }

        // TODO: optimize allocations
        private static void SaveBlockNumbersByKey(IWriteBatch dbBatch, byte[] key, IReadOnlyList<int> blockNums, bool isBackwardSync, SetReceiptsStats stats)
        {
            var dbKeyArray = ArrayPool<byte>.Shared.Rent(key.Length + BackwardMergeKey.Length);

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
        private static void CreateDbKey(ReadOnlySpan<byte> key, int blockNumber, Span<byte> dbKey)
        {
            key.CopyTo(dbKey);
            SetKeyBlockNum(dbKey, blockNumber);
        }

        private static ReadOnlySpan<byte> CreateMergeDbKey(ReadOnlySpan<byte> key, Span<byte> dbKey, bool isBackwardSync)
        {
            var postfix = isBackwardSync ? BackwardMergeKey : ForwardMergeKey;

            key.CopyTo(dbKey);
            postfix.CopyTo(dbKey[key.Length..]);

            return dbKey[..(key.Length + postfix.Length)];
        }

        // RocksDB uses big-endian (lexicographic) ordering
        private static int GetKeyBlockNum(ReadOnlySpan<byte> dbKey) => BinaryPrimitives.ReadInt32BigEndian(dbKey[^BlockNumSize..]);
        private static void SetKeyBlockNum(Span<byte> dbKey, int blockNumber) => BinaryPrimitives.WriteInt32BigEndian(dbKey[^BlockNumSize..], blockNumber);

        private static bool IsMergeKey(ReadOnlySpan<byte> dbKey) => dbKey.Length is
            Hash256.Size + ForwardMergeKeyLength or
            Hash256.Size + BackwardMergeKeyLength or
            Address.Size + ForwardMergeKeyLength or
            Address.Size + BackwardMergeKeyLength;

        private static bool UseBackwardSyncFor(ReadOnlySpan<byte> dbKey) => dbKey.Length is
            Hash256.Size + BackwardMergeKeyLength or
            Address.Size + BackwardMergeKeyLength;

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

        // TODO: test on big-endian system?
        private static unsafe ReadOnlySpan<byte> Compress(Span<byte> data, Span<byte> buffer)
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

        // used for data validation, TODO: introduce custom exception type
        // TODO: include key value when available
        private static Exception ValidationException(string message) => new InvalidOperationException(message);

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

        private static void ReverseBlocksIfNeeded(Span<byte> data)
        {
            if (data.Length != 0 && ReadValBlockNum(data) > ReadValLastBlockNum(data))
                MemoryMarshal.Cast<byte, int>(data).Reverse();
        }

        private static void ReverseBlocksIfNeeded(Span<int> blocks)
        {
            if (blocks.Length != 0 && blocks[0] > blocks[^1])
                blocks.Reverse();
        }

        private Span<byte> RemoveReorgableBlocks(Span<byte> data)
        {
            var lastCompressBlock = GetLastKnownBlockNumber() - _maxReorgDepth;
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
                var currentBlock = ReadValBlockNum(operand[i..]);
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
            if (ReadValLastBlockNum(data) == target)
                return right * BlockNumSize;
            if (ReadValBlockNum(data) == target)
                return left * BlockNumSize;

            while (left <= right)
            {
                int mid = left + (right - left) / 2;
                int offset = mid * 4;

                int value = ReadValBlockNum(data[offset..]);

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
