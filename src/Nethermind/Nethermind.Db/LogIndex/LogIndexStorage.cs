using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.Db
{
    // TODO: try to increase page size gradually (use different files for different page sizes?)
    // TODO: get rid of InvalidOperationExceptions - these are for state validation
    [SuppressMessage("ReSharper", "PrivateFieldCanBeConvertedToLocalVariable")] // TODO: get rid of unused fields
    public sealed class LogIndexStorage : ILogIndexStorage
    {
        private static readonly byte[] LastBlockNumKey = "LastBlockNum"u8.ToArray();

        private readonly IDb _defaultDb;
        private readonly IDb _addressDb;
        private readonly IDb _topicsDb;
        private readonly ILogger _logger;
        private readonly int _ioParallelism;
        public const int BlockNumSize = sizeof(int);
        public const int BlockMaxVal = int.MaxValue;
        public const int MaxUncompressedLength = 128 * BlockNumSize;
        private const int CompactionDistance = 262_144; // 2^18

        // TODO: get rid of static fields
        private static readonly Channel<byte[]> CompressKeysChannel = Channel.CreateUnbounded<byte[]>();
        public static readonly ChannelWriter<byte[]> CompressKeys = CompressKeysChannel.Writer;

        // TODO: ensure class is singleton
        public LogIndexStorage(IColumnsDb<LogIndexColumns> columnsDb, ILogger logger, string baseDbPath, int ioParallelism)
        {
            if (ioParallelism < 1) throw new ArgumentException("IO parallelism degree must be a positive value.", nameof(ioParallelism));
            _ioParallelism = ioParallelism;

            _logger = logger;

            _addressDb = columnsDb.GetColumnDb(LogIndexColumns.Addresses);
            _topicsDb = columnsDb.GetColumnDb(LogIndexColumns.Topics);
            _defaultDb = columnsDb.GetColumnDb(LogIndexColumns.Default);
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
            //Console.WriteLine("!!!!!!!!!! StopAsync !!!!!!!!!!");
            await _setReceiptsSemaphore.WaitAsync();

            // TODO: check if needed
            _addressDb.Flush();
            _topicsDb.Flush();
            _defaultDb.Flush();

            if (_logger.IsInfo) _logger.Info("Log index storage stopped");
        }

        ValueTask IAsyncDisposable.DisposeAsync()
        {
            _setReceiptsSemaphore.Dispose();
            return ValueTask.CompletedTask;
        }

        private int _lastKnownBlock = -1;

        public int GetLastKnownBlockNumber()
        {
            return _lastKnownBlock < 0
                ? ReadLastKnownBlockNumber() // Should only happen until first update
                : _lastKnownBlock;
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

                    int currentLowestBlockNumber = GetBlockNumber(firstKey);

                    iterator.Next();
                    int? nextLowestBlockNumber;
                    if (IsInKeyBounds(iterator, keyPrefix))
                    {
                        ReadOnlySpan<byte> nextKey = iterator.Key().AsSpan();
                        nextLowestBlockNumber = GetBlockNumber(nextKey);
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
            }
        }

        // TODO: optimize allocations
        private Dictionary<byte[], List<int>>? BuildProcessingDictionary(
            BlockReceipts[] batch, SetReceiptsStats stats
        )
        {
            if (batch[^1].BlockNumber <= _lastKnownBlock)
                return null;

            var watch = Stopwatch.StartNew();

            var blockNumsByKey = new Dictionary<byte[], List<int>>(Bytes.EqualityComparer);
            foreach ((var blockNumber, TxReceipt[] receipts) in batch)
            {
                if (blockNumber <= _lastKnownBlock)
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

                        if (addressNums.Count == 0 || addressNums[^1] != blockNumber)
                            addressNums.Add(blockNumber);

                        foreach (Hash256 topic in log.Topics)
                        {
                            stats.TopicsAdded++;

                            List<int> topicNums = blockNumsByKey.GetOrAdd(topic.Bytes.ToArray(), _ => new(1));

                            if (topicNums.Count == 0 || topicNums[^1] != blockNumber)
                                topicNums.Add(blockNumber);
                        }
                    }
                }
            }

            stats.KeysCount.Include(blockNumsByKey.Count);
            stats.BuildingDictionary.Include(watch.Elapsed);

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
        private readonly SemaphoreSlim _setReceiptsSemaphore = new (1, 1);

        public Task<SetReceiptsStats> SetReceiptsAsync(int blockNumber, TxReceipt[] receipts, bool isBackwardSync)
        {
            return SetReceiptsAsync([new(blockNumber, receipts)], isBackwardSync);
        }

        public SetReceiptsStats Compact()
        {
            var stats = new SetReceiptsStats();
            Compact(stats);
            return stats;
        }

        private int? _lastCompactionAt;

        // batch is expected to be sorted
        public async Task<SetReceiptsStats> SetReceiptsAsync(
            BlockReceipts[] batch, bool isBackwardSync
        )
        {
            //Console.WriteLine("!!!!!!!!!! SetReceiptsAsync !!!!!!!!!!");
            if (!await _setReceiptsSemaphore.WaitAsync(TimeSpan.Zero, CancellationToken.None))
                throw new InvalidOperationException($"Concurrent invocations of {nameof(SetReceiptsAsync)} is not supported.");

            var stats = new SetReceiptsStats();

            try
            {
                var dictionary = BuildProcessingDictionary(batch, stats);

                if (dictionary is { Count: > 0 })
                {
                    var watch = Stopwatch.StartNew();
                    Parallel.ForEach(
                        dictionary, new() { MaxDegreeOfParallelism = _ioParallelism },
                        pair => SaveBlockNumbersByKey(pair.Key, pair.Value, stats)
                    );
                    stats.Processing.Include(watch.Elapsed);
                }
            }
            finally
            {
                _lastCompactionAt ??= batch[0].BlockNumber - 1;
                if (batch[^1].BlockNumber - _lastCompactionAt >= CompactionDistance)
                {
                    Compact(stats);
                    _lastCompactionAt = batch[^1].BlockNumber;
                }

                if (_lastKnownBlock < batch[^1].BlockNumber)
                    WriteLastKnownBlockNumber(_lastKnownBlock = batch[^1].BlockNumber);

                stats.LastBlockNumber = _lastKnownBlock;

                _setReceiptsSemaphore.Release();
                //Console.WriteLine("!!!!!!!!!! _setReceiptsSemaphore.Release() !!!!!!!!!!");
            }

            return stats;
        }

        private void Compact(SetReceiptsStats stats)
        {
            // TODO: log as Debug
            _logger.Info("Log index compaction started");
            var watch = new Stopwatch();

            watch.Restart();
            _addressDb.Flush();
            _topicsDb.Flush();
            stats.FlushingDbs.Include(watch.Elapsed);

            // TODO: try keep writing during compaction
            watch.Restart();
            _addressDb.Compact();
            _topicsDb.Compact();
            stats.CompactingDbs.Include(watch.Elapsed);

            watch.Restart();
            CompressPostMerge(CompressKeysChannel.Reader, stats);
            stats.PostMergeProcessing.Include(watch.Elapsed);

            _logger.Info("Log index compaction completed");
        }

        // TODO: optimize allocations
        private void SaveBlockNumbersByKey(byte[] key, IReadOnlyList<int> blockNums, SetReceiptsStats stats)
        {
            var db = key.Length switch
            {
                Address.Size => _addressDb,
                Hash256.Size => _topicsDb,
                var size => throw ValidationException($"Unexpected key of {size} bytes.")
            };

            var dbKeyArray = ArrayPool<byte>.Shared.Rent(key.Length + BlockNumSize);
            var dbKey = dbKeyArray.AsSpan(0, key.Length + BlockNumSize);

            try
            {
                CreateDbKey(key, BlockMaxVal, dbKey);

                // TODO: handle writing already processed blocks
                // if (blockNums[^1] <= lastSavedNum)
                //     return;

                var newValue = CreateDbValue(blockNums);

                var watch = Stopwatch.StartNew();
                db.Merge(dbKey, newValue);
                stats.CallingMerge.Include(watch.Elapsed);
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
            SetBlockNumber(dbKey, blockNumber);
        }

        // RocksDB uses big-endian (lexicographic) ordering
        private static int GetBlockNumber(ReadOnlySpan<byte> dbKey) => BinaryPrimitives.ReadInt32BigEndian(dbKey[^BlockNumSize..]);
        private static void SetBlockNumber(Span<byte> dbKey, int blockNumber) => BinaryPrimitives.WriteInt32BigEndian(dbKey[^BlockNumSize..], blockNumber);

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

        private int ReadLastKnownBlockNumber()
        {
            var value = _defaultDb.Get(LastBlockNumKey);
            return value is { Length: > 1 } ? BinaryPrimitives.ReadInt32BigEndian(value) : -1;
        }

        private void WriteLastKnownBlockNumber(int value)
        {
            Span<byte> buffer = ArrayPool<byte>.Shared.RentSpan(sizeof(int));
            BinaryPrimitives.WriteInt32BigEndian(buffer, value);
            _defaultDb.PutSpan(LastBlockNumKey, buffer);
        }

        // used for data validation, TODO: remove, replace with tests
        private static Exception ValidationException(string message) => new InvalidOperationException(message);

        // TODO: optimize allocations
        private void CompressPostMerge(ChannelReader<byte[]> newKeysReader, SetReceiptsStats stats)
        {
            using var addressWriteBatch = _addressDb.StartWriteBatch();
            using var topicsWriteBatch = _topicsDb.StartWriteBatch();

            while (newKeysReader.TryRead(out var dbKey))
            {
                var (db, batch) = (dbKey.Length - BlockNumSize) switch
                {
                    Address.Size => (_addressDb, addressWriteBatch),
                    Hash256.Size => (_topicsDb, topicsWriteBatch),
                    var size => throw ValidationException($"Unexpected index size of {size} bytes.")
                };

                var dbValue = db.Get(dbKey) ?? throw new ValidationException("Empty value in the post-merge compression queue.");
                var blockNum = ReadBlockNum(dbValue);

                var dbKeyComp = (byte[])dbKey.Clone();
                SetBlockNumber(dbKeyComp, blockNum);

                // Put compressed value at a new key and clear uncompressed one
                dbValue = CompressDbValue(dbValue);
                batch.PutSpan(dbKeyComp, dbValue);
                batch.PutSpan(dbKey, Array.Empty<byte>());

                if (db == _addressDb) stats.CompressedAddressKeys++;
                else if (db == _topicsDb) stats.CompressedTopicKeys++;
            }
        }

        public static int ReadCompressionMarker(ReadOnlySpan<byte> source) => -BinaryPrimitives.ReadInt32LittleEndian(source);
        public static void WriteCompressionMarker(Span<byte> source, int len) => BinaryPrimitives.WriteInt32LittleEndian(source, -len);

        public static void WriteBlockNum(Span<byte> destination, int blockNum) => BinaryPrimitives.WriteInt32LittleEndian(destination, blockNum);
        public static int ReadBlockNum(ReadOnlySpan<byte> source) => BinaryPrimitives.ReadInt32LittleEndian(source);
        public static int ReadLastBlockNum(ReadOnlySpan<byte> source) => ReadBlockNum(source[^BlockNumSize..]);

        public static void WriteBlockNums(Span<byte> destination, IEnumerable<int> blockNums)
        {
            var shift = 0;
            foreach (var blockNum in blockNums)
            {
                WriteBlockNum(destination[shift..], blockNum);
                shift += BlockNumSize;
            }
        }

        public static int[] ReadBlockNums(ReadOnlySpan<byte> source)
        {
            if (source.Length % 4 != 0)
                throw ValidationException("Invalid length for array of block numbers.");

            var result = new int[source.Length / BlockNumSize];
            for (var i = 0; i < source.Length; i += BlockNumSize)
                result[i / BlockNumSize] = ReadBlockNum(source[i..]);

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
            if (ReadCompressionMarker(data) >= 0)
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
