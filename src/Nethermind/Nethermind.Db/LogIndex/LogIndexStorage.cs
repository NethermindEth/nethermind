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
    // TODO: try to increase page size gradually (use different files for different page sizes?)
    // TODO: get rid of InvalidOperationExceptions - these are for state validation
    [SuppressMessage("ReSharper", "PrivateFieldCanBeConvertedToLocalVariable")] // TODO: get rid of unused fields
    public sealed class LogIndexStorage : ILogIndexStorage
    {
        // Use value that we won't encounter during iterator Seek or SeekForPrev
        private static readonly byte[] LastBlockNumKey = Enumerable.Repeat(byte.MaxValue, Math.Max(Address.Size, Hash256.Size) + 1).ToArray();

        private readonly IDb _addressDb;
        private readonly IDb _topicsDb;
        private readonly ILogger _logger;
        private readonly int _ioParallelism;
        public const int BlockNumSize = sizeof(int);
        public const int BlockMaxVal = int.MaxValue;
        public const int MaxUncompressedLength = 128 * BlockNumSize;
        public const byte RevertOperator = (byte)'-';

        private readonly int _compactionDistance;

        // TODO: get rid of static fields
        // A lot of duplicates in case of regular Channel, TODO: find a better way to guarantee uniqueness
        private static readonly ConcurrentDictionary<byte[], bool> CompressQueue = new(Bytes.EqualityComparer);
        public static void EnqueueCompress(byte[] dbKey) => CompressQueue.TryAdd(dbKey, true);

        // TODO: ensure class is singleton
        public LogIndexStorage(IColumnsDb<LogIndexColumns> columnsDb, ILogger logger,
            int ioParallelism, int compactionDistance)
        {
            if (ioParallelism < 1) throw new ArgumentException("IO parallelism degree must be a positive value.", nameof(ioParallelism));
            _ioParallelism = ioParallelism;

            if (compactionDistance < 1) throw new ArgumentException("Compaction distance must be a positive value.", nameof(compactionDistance));
            _compactionDistance = compactionDistance;

            _logger = logger;

            _addressDb = columnsDb.GetColumnDb(LogIndexColumns.Addresses);
            _topicsDb = columnsDb.GetColumnDb(LogIndexColumns.Topics);
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
            CompressQueue.Clear();

            _setReceiptsSemaphore.Dispose();
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

            bool IsInKeyBounds(IIterator<byte[], byte[]> iterator, byte[] key)
            {
                return iterator.Valid() && iterator.Key().AsSpan()[..key.Length].SequenceEqual(key);
            }

            using IIterator<byte[], byte[]> iterator = db.GetIterator(true);

            var dbKeyBuffer = ArrayPool<byte>.Shared.Rent(keyPrefix.Length + BlockNumSize);
            var dbKey = dbKeyBuffer.AsSpan(0, keyPrefix.Length + BlockNumSize);

            // Find last index for the given key, starting at or before `from`
            CreateDbKey(keyPrefix, from, dbKey);
            iterator.SeekForPrev(dbKey);

            // Otherwise, find first index for the given key
            // TODO: optimize seeking!
            if (!IsInKeyBounds(iterator, keyPrefix))
            {
                iterator.SeekToFirst();
                iterator.Seek(keyPrefix);
            }

            try
            {
                // TODO: optimize allocations
                while (IsInKeyBounds(iterator, keyPrefix))
                {
                    ReadOnlySpan<byte> firstKey = iterator.Key().AsSpan();

                    int currentLowestBlockNumber = GetKeyBlockNum(firstKey);

                    iterator.Next();
                    int? nextLowestBlockNumber;
                    if (IsInKeyBounds(iterator, keyPrefix))
                    {
                        ReadOnlySpan<byte> nextKey = iterator.Key().AsSpan();
                        nextLowestBlockNumber = GetKeyBlockNum(nextKey);
                    }
                    else
                    {
                        nextLowestBlockNumber = BlockMaxVal;
                    }

                    if (nextLowestBlockNumber > from &&
                        (currentLowestBlockNumber <= to || currentLowestBlockNumber == BlockMaxVal))
                    {
                        ReadOnlySpan<byte> data = db.Get(firstKey);

                        var decompressedBlockNumbers = data.Length == 0 || ReadCompressionMarker(data) <= 0
                            ? ReadBlockNums(data)
                            : DecompressDbValue(data);

                        int startIndex = BinarySearch(decompressedBlockNumbers, from);
                        if (startIndex < 0)
                        {
                            startIndex = ~startIndex;
                        }

                        for (int i = startIndex; i < decompressedBlockNumbers.Length; i++)
                        {
                            int block = decompressedBlockNumbers[i];
                            if (block > to)
                                yield break;

                            yield return block;
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(dbKeyBuffer);

                // TODO: log in Debug
                _logger.Info($"GetBlockNumbersFor({Convert.ToHexString(keyPrefix)}, {from}, {to}) in {Stopwatch.GetElapsedTime(timestamp)}");
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
            BlockReceipts[] batch, SetReceiptsStats stats
        )
        {
            if (batch[^1].BlockNumber <= GetLastKnownBlockNumber())
                return null;

            var timestamp = Stopwatch.GetTimestamp();

            var blockNumsByKey = new Dictionary<byte[], List<int>>(Bytes.EqualityComparer);
            foreach ((var blockNumber, TxReceipt[] receipts) in batch)
            {
                if (blockNumber <= GetLastKnownBlockNumber())
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

                        if (blockNumber > _addressLastKnownBlock && (addressNums.Count == 0 || addressNums[^1] != blockNumber))
                        {
                            addressNums.Add(blockNumber);
                        }

                        if (blockNumber > _topicLastKnownBlock)
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

        public Task RevertFrom(BlockReceipts block)
        {
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
                    CreateDbKey(log.Address.Bytes, BlockMaxVal, addressKey);
                    addressBatch.Merge(addressKey, dbValue);

                    foreach (Hash256 topic in log.Topics)
                    {
                        CreateDbKey(topic.Bytes, BlockMaxVal, topicKey);
                        topicBatch.Merge(topicKey, dbValue);
                    }
                }

                addressBatch.Dispose();
                topicBatch.Dispose();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(keyArray);
                ArrayPool<byte>.Shared.Return(valueArray);
            }

            return Task.CompletedTask;
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
                if (GetKeyBlockNum(key) == BlockMaxVal && value.Length > maxUncompressedLength)
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
                throw new InvalidOperationException($"Concurrent invocations of {nameof(SetReceiptsAsync)} is not supported.");

            long timestamp;
            var stats = new SetReceiptsStats();

            try
            {
                Dictionary<int, IWriteBatch> dbBatches = new(2)
                {
                    [Address.Size] = _addressDb.StartWriteBatch(),
                    [Hash256.Size] = _topicsDb.StartWriteBatch()
                };

                if (BuildProcessingDictionary(batch, stats) is { Count: > 0 } dictionary)
                {
                    // Add values to batches
                    timestamp = Stopwatch.GetTimestamp();
                    foreach (var (key, blockNums) in dictionary)
                    {
                        var dbBatch = dbBatches[key.Length];
                        SaveBlockNumbersByKey(dbBatch, key, blockNums, stats);
                    }
                    stats.Processing.Include(Stopwatch.GetElapsedTime(timestamp));

                    // Compact if needed
                    _lastCompactionAt ??= batch[0].BlockNumber;
                    if (batch[^1].BlockNumber - _lastCompactionAt >= _compactionDistance)
                    {
                        Compact(stats);
                        _lastCompactionAt = batch[^1].BlockNumber;
                    }
                }

                // Update last processed block number
                var lastAddedBlockNum = batch[^1].BlockNumber;
                if (GetLastKnownBlockNumber() < lastAddedBlockNum)
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
        private void SaveBlockNumbersByKey(IWriteBatch dbBatch, byte[] key, IReadOnlyList<int> blockNums, SetReceiptsStats stats)
        {
            var dbKeyArray = ArrayPool<byte>.Shared.Rent(key.Length + BlockNumSize);
            var dbKey = dbKeyArray.AsSpan(0, key.Length + BlockNumSize);

            try
            {
                CreateDbKey(key, BlockMaxVal, dbKey);

                // TODO: handle writing already processed blocks
                // if (blockNums[^1] <= lastSavedNum)
                //     return;

                var newValue = CreateDbValue(blockNums);

                var timestamp = Stopwatch.GetTimestamp();
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
        public static void CreateDbKey(ReadOnlySpan<byte> key, int blockNumber, Span<byte> dbKey)
        {
            key.CopyTo(dbKey);
            SetKeyBlockNum(dbKey, blockNumber);
        }

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

                var dbKeyComp = (byte[])dbKey.Clone();
                SetKeyBlockNum(dbKeyComp, blockNum);

                // Put compressed value at a new key and clear uncompressed one
                // TODO: reading and clearing the value is not atomic, find a fix
                timestamp = Stopwatch.GetTimestamp();
                dbValue = CompressDbValue(dbValue);
                stats.CompressingValue.Include(Stopwatch.GetElapsedTime(timestamp));

                timestamp = Stopwatch.GetTimestamp();
                db.PutSpan(dbKeyComp, dbValue);
                db.PutSpan(dbKey, Array.Empty<byte>());
                stats.PuttingValues.Include(Stopwatch.GetElapsedTime(timestamp));

                if (db == _addressDb) Interlocked.Increment(ref stats.CompressedAddressKeys);
                else if (db == _topicsDb) Interlocked.Increment(ref stats.CompressedTopicKeys);
            }, new() { MaxDegreeOfParallelism = _ioParallelism });

            foreach (var dbKey in CompressQueue.Keys)
            {
                CompressQueue.TryRemove(dbKey, out _);
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

        public static int[] ReadBlockNums(ReadOnlySpan<byte> source)
        {
            if (source.Length % 4 != 0)
                throw ValidationException("Invalid length for array of block numbers.");

            var result = new int[source.Length / BlockNumSize];
            for (var i = 0; i < source.Length; i += BlockNumSize)
                result[i / BlockNumSize] = ReadValBlockNum(source[i..]);

            return result;
        }

        public static byte[] CreateDbValue(IReadOnlyList<int> blockNums)
        {
            var value = new byte[blockNums.Count * BlockNumSize];
            WriteBlockNums(value, blockNums);
            return value;
        }

        public static byte[] CompressDbValue(ReadOnlySpan<byte> data)
        {
            if (ReadCompressionMarker(data) > 0)
                throw ValidationException("Data is already compressed.");
            if (data.Length % BlockNumSize != 0)
                throw ValidationException("Invalid data length.");

            // TODO: use same array as destination if possible
            Span<byte> buffer = new byte[data.Length + BlockNumSize];
            WriteCompressionMarker(buffer, data.Length / BlockNumSize);
            var compressed = Compress(data, buffer[BlockNumSize..]);

            compressed = buffer[..(BlockNumSize + compressed.Length)];
            return compressed.ToArray();
        }

        public static int[] DecompressDbValue(ReadOnlySpan<byte> data)
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
