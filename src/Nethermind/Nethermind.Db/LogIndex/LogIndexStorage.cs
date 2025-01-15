using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
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
        private const string FolderName = "log-index";
        private const string TempFileName = "temp_index.bin";
        private const string FinalFileName = "finalized_index.bin";

        public string TempFilePath { get; }
        public string FinalFilePath { get; }

        private readonly SafeFileHandle _finalFileHandle;
        private readonly FileStream _finalFileStream;
        private readonly IDb _addressDb;
        private readonly IDb _topicsDb;
        private readonly ILogger _logger;
        private readonly int _ioParallelism;
        private const int PageSize = 4096;

        private readonly IFilePagesPool _tempPagesPool;

        // used for state/data validation, TODO: remove, replace with tests
        private static readonly bool EnableStateChecks = false;

        // TODO: ensure class is singleton
        public LogIndexStorage(IColumnsDb<LogIndexColumns> columnsDb, ILogger logger, string baseDbPath, int ioParallelism)
        {
            if (ioParallelism < 1) throw new ArgumentException("IO parallelism degree must be greater than 1.", nameof(ioParallelism));
            _ioParallelism = ioParallelism;

            TempFilePath = Path.Combine(baseDbPath, FolderName, TempFileName);
            FinalFilePath = Path.Combine(baseDbPath, FolderName, FinalFileName);

            _logger = logger;

            _logger.Info($"Temp file path: {TempFilePath}");
            _logger.Info($"Final file path: {FinalFilePath}");

            // TODO: use IFileSystem
            Directory.CreateDirectory(Path.Combine(baseDbPath, FolderName));

            _finalFileHandle = File.OpenHandle(FinalFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite);
            _finalFileStream = new(_finalFileHandle, FileAccess.ReadWrite);

            _addressDb = columnsDb.GetColumnDb(LogIndexColumns.Addresses);
            _topicsDb = columnsDb.GetColumnDb(LogIndexColumns.Topics);

            IDb tempPagesDb = columnsDb.GetColumnDb(LogIndexColumns.Default);
            _tempPagesPool = new AsyncFilePagesPool(TempFilePath, tempPagesDb, PageSize)
            {
                AllocatedPagesPoolSize = 2048,
                ReturnedPagesPoolSize = -1
            };
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

        async ValueTask IAsyncDisposable.DisposeAsync()
        {
            await _finalFileStream.DisposeAsync();
            _finalFileHandle.Dispose();

            await _tempPagesPool.StopAsync();
            await _tempPagesPool.DisposeAsync();
        }

        private int _lastKnownBlock = -1;

        public int GetLastKnownBlockNumber() => _lastKnownBlock;

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
            using IIterator<byte[], byte[]> iterator = db.GetIterator(true);

            //TODO: rework to use SeekForPrev
            iterator.Seek(keyPrefix);

            byte[] indexBuffer = ArrayPool<byte>.Shared.Rent(PageSize);

            try
            {
                // TODO: optimize allocations
                while (iterator.Valid() && iterator.Key().AsSpan()[..keyPrefix.Length].SequenceEqual(keyPrefix))
                {
                    ReadOnlySpan<byte> firstKey = iterator.Key().AsSpan();

                    int currentLowestBlockNumber = BinaryPrimitives.ReadInt32BigEndian(firstKey.Slice(keyPrefix.Length));

                    iterator.Next();
                    int? nextLowestBlockNumber;
                    if (iterator.Valid() && iterator.Key().AsSpan()[..keyPrefix.Length].SequenceEqual(keyPrefix))
                    {
                        ReadOnlySpan<byte> nextKey = iterator.Key().AsSpan();
                        nextLowestBlockNumber = BinaryPrimitives.ReadInt32BigEndian(nextKey.Slice(keyPrefix.Length));
                    }
                    else
                    {
                        nextLowestBlockNumber = int.MaxValue;
                    }

                    if (nextLowestBlockNumber > from && currentLowestBlockNumber <= to)
                    {
                        IndexInfo? indexInfo = DeserializeIndexInfo(firstKey, db.Get(firstKey));
                        SafeFileHandle fileHandle = indexInfo.IsTemp ? _tempPagesPool.FileHandle : _finalFileHandle;
                        ReadOnlySpan<byte> data = LoadData(fileHandle, indexInfo, indexBuffer);

                        int[] decompressedBlockNumbers;
                        if (indexInfo.IsTemp)
                        {
                            decompressedBlockNumbers = MemoryMarshal.Cast<byte, int>(data).ToArray();
                        }
                        else
                        {
                            decompressedBlockNumbers = new int[PageSize / sizeof(int)];
                            decompressedBlockNumbers = Decompress(data, decompressedBlockNumbers).ToArray();
                        }

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
                ArrayPool<byte>.Shared.Return(indexBuffer);
            }
        }

        // TODO: try to minimize number of allocations
        private Dictionary<byte[], List<int>>? BuildProcessingDictionary(
            BlockReceipts[] batch, SetReceiptsStats stats, CancellationToken cancellationToken
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

                        cancellationToken.ThrowIfCancellationRequested();

                        List<int> addressNums = blockNumsByKey.GetOrAdd(log.Address.Bytes, _ => new(1));

                        if (addressNums.Count == 0 || addressNums[^1] != blockNumber)
                            addressNums.Add(blockNumber);

                        foreach (Hash256 topic in log.Topics)
                        {
                            stats.TopicsAdded++;

                            cancellationToken.ThrowIfCancellationRequested();

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
            using IIterator<byte[], byte[]> addressIterator = _addressDb.GetIterator();
            using IIterator<byte[], byte[]> topicIterator = _topicsDb.GetIterator();

            // Total: 9244, finalized - 31
            (Address, IndexInfo)[] addressData = Enumerate(addressIterator).Select(x => (new Address(SplitDbKey(x.key).key), DeserializeIndexInfo(x.key, x.value))).ToArray();

            // Total: 5_654_366
            // From first 200_000: 1 - 134_083 (0.670415), 2 - 10_486, 3 - 33_551, 4 - 4872, 5 - 4227, 6 - 4764, 7 - 6792, 8 - 609, 9 - 67, 10 - 55
            // From first 300_000: 1 - 228_553 (0.761843333)
            // From first 1_000_000: 1 - 875_216 (0.875216)
            //var topicData = Enumerate(topicIterator).Select(x => (new Hash256(SplitDbKey(x.key).key), DeserializeIndexInfo(x.key, x.value))).ToArray();
            var topicData = Enumerate(topicIterator).Take(200_000).Select(x => (topic: new Hash256(SplitDbKey(x.key).key), Index: DeserializeIndexInfo(x.key, x.value))).GroupBy(x => x.Index.Length).ToDictionary(g => g.Key, g => g.Count());

            GC.KeepAlive(addressData);
            GC.KeepAlive(topicData);

            return Task.CompletedTask;
        }

        public Task<SetReceiptsStats> SetReceiptsAsync(int blockNumber, TxReceipt[] receipts, bool isBackwardSync,
            CancellationToken cancellationToken)
        {
            return SetReceiptsAsync([new(blockNumber, receipts)], isBackwardSync, cancellationToken);
        }

        public async Task<SetReceiptsStats> SetReceiptsAsync(
            BlockReceipts[] batch, bool isBackwardSync, CancellationToken cancellationToken
        )
        {
            await _tempPagesPool.StartAsync();

            var stats = new SetReceiptsStats();

            if (BuildProcessingDictionary(batch, stats, cancellationToken) is not { } dictionary)
                return stats;

            var finalizeQueue = Channel.CreateUnbounded<IndexInfo>();
            Task finalizingTask = KeepFinalizingIndexes(finalizeQueue.Reader, cancellationToken);

            try
            {
                var watch = Stopwatch.StartNew();
                Parallel.ForEach(
                    dictionary, new() { MaxDegreeOfParallelism = _ioParallelism },
                    pair => SaveBlockNumbersByKey(pair.Key, pair.Value, stats, finalizeQueue.Writer)
                );
                stats.ProcessingData.Include(watch.Elapsed);

                _lastKnownBlock = Math.Max(_lastKnownBlock, batch.Max(b => b.BlockNumber));
            }
            finally
            {
                finalizeQueue.Writer.Complete();

                var watch = Stopwatch.StartNew();
                await finalizingTask;
                stats.WaitingForFinalization.Include(watch.Elapsed);

                watch = Stopwatch.StartNew();
                _addressDb.Flush();
                _topicsDb.Flush();
                stats.FlushingDbs.Include(watch.Elapsed);
            }

            return stats;
        }

        private void SaveBlockNumbersByKey(byte[] key, IReadOnlyList<int> blockNums, SetReceiptsStats stats, ChannelWriter<IndexInfo> finalizeQueue)
        {
            byte[]? bytesBuffer = null;

            try
            {
                IDb db = key.Length switch
                {
                    Address.Size => _addressDb,
                    Hash256.Size => _topicsDb,
                    var size => throw ValidationException($"Unexpected key of {size} bytes.")
                };

                IndexInfo? indexInfo = GetIndex(db, key, blockNums, stats);

                if (indexInfo != null && blockNums[^1] <= indexInfo.LastBlockNumber)
                    return;

                indexInfo = indexInfo?.Type switch
                {
                    IndexType.DB => CreateTempIndex(indexInfo, stats),
                    IndexType.Temp => indexInfo,
                    _ => CreateTempIndex(key, blockNums, stats)
                };

                bytesBuffer = ArrayPool<byte>.Shared.Rent(PageSize);
                Span<byte> bytes = Span<byte>.Empty;

                if (indexInfo.Type == IndexType.DB)
                {
                    indexInfo.LastBlockNumber = blockNums[^1];
                    SaveIndex(db, indexInfo);
                    return;
                }

                foreach (var blockNum in blockNums)
                {
                    indexInfo ??= CreateTempIndex(key, stats);

                    if (blockNum <= indexInfo.LastBlockNumber)
                        continue;

                    bytes = bytesBuffer.AsSpan(0, bytes.Length + sizeof(int));
                    BinaryPrimitives.WriteInt32LittleEndian(bytes[^sizeof(int)..], blockNum);

                    if (indexInfo.ByteLengthRemaining == bytes.Length)
                    {
                        WriteBytes(indexInfo, bytes, stats);
                        bytes = Span<byte>.Empty;

                        finalizeQueue.TryWrite(indexInfo);
                        indexInfo = null;
                    }
                }

                if (indexInfo != null && bytes.Length != 0)
                {
                    WriteBytes(indexInfo, bytes, stats);
                    SaveIndex(db, indexInfo);
                }
            }
            finally
            {
                if (bytesBuffer != null) ArrayPool<byte>.Shared.Return(bytesBuffer);
            }
        }

        public PagesStats PagesStats => _tempPagesPool.Stats;

        private SafeFileHandle TempFileHandle => _tempPagesPool.FileHandle;

        private async Task KeepFinalizingIndexes(ChannelReader<IndexInfo> reader, CancellationToken cancellationToken)
        {
            byte[] blockNumberBytes = ArrayPool<byte>.Shared.Rent(sizeof(int));
            byte[] dataBuffer = ArrayPool<byte>.Shared.Rent(PageSize);
            byte[] compressBuffer = ArrayPool<byte>.Shared.Rent(PageSize);

            try
            {
                var addressKey = new byte[Address.Size + sizeof(int)];
                var topicKey = new byte[Hash256.Size + sizeof(int)];

                await foreach (IndexInfo indexInfo in reader.ReadAllAsync(cancellationToken))
                {
                    if (indexInfo.Type != IndexType.Temp)
                        throw ValidationException("Non-temp index should not be finalized.");

                    ReadOnlySpan<byte> data = LoadData(TempFileHandle, indexInfo, dataBuffer);
                    ReadOnlySpan<byte> compressed = Compress(data, compressBuffer);
                    long offset = Append(_finalFileStream, compressed);

                    if (EnableStateChecks)
                    {
                        ReadOnlySpan<int> test = Decompress(compressed, new int[PageSize / sizeof(int)]);
                        if (!test.SequenceEqual(MemoryMarshal.Cast<byte, int>(data)))
                            throw ValidationException("Invalid data compression/decompression.");
                    }

                    (byte[] dbKey, IDb db) = indexInfo.Key.Length switch
                    {
                        Address.Size => (addressKey, _addressDb),
                        Hash256.Size => (topicKey, _topicsDb),
                        var size => throw ValidationException($"Unexpected index size of {size} bytes.")
                    };

                    CreateDbKey(indexInfo.Key, indexInfo.LowestBlockNumber(TempFileHandle, blockNumberBytes), dbKey);
                    db.PutSpan(dbKey, SerializeIndexInfo(IndexType.Final, offset, compressed.Length, indexInfo.LastBlockNumber), WriteFlags.DisableWAL);

                    // TODO: improve awaiting
                    _tempPagesPool.ReturnPageAsync(indexInfo.Offset).Wait();
                }

                await _finalFileStream.FlushAsync(cancellationToken);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(blockNumberBytes);
                ArrayPool<byte>.Shared.Return(dataBuffer);
                ArrayPool<byte>.Shared.Return(compressBuffer);
            }
        }

        private void WriteBytes(IndexInfo indexInfo, Span<byte> bytes, SetReceiptsStats stats)
        {
            if (indexInfo.Type != IndexType.Temp)
                throw ValidationException("Non-temp index should not be written to.");

            if (indexInfo.ByteLengthRemaining < bytes.Length)
                throw ValidationException($"Index has less bytes remaining ({indexInfo.ByteLengthRemaining}) than attempted to store ({bytes.Length}).");

            if (bytes.Length == 0 || bytes.Length % sizeof(int) != 0)
                throw ValidationException($"Invalid bytes length ({bytes.Length}).");

            long position = indexInfo.Position!.Value;
            RandomAccess.Write(TempFileHandle, bytes, position);
            indexInfo.Length += bytes.Length / sizeof(int);
            indexInfo.LastBlockNumber = BinaryPrimitives.ReadInt32LittleEndian(bytes[^sizeof(int)..]);

            stats.BytesWritten.Include(bytes.Length);
        }

        private void SaveIndex(IDb db, IndexInfo indexInfo)
        {
            Span<byte> blockNumberBytes = stackalloc byte[sizeof(int)];
            Span<byte> dbKey = stackalloc byte[indexInfo.Key.Length + sizeof(int)];
            CreateDbKey(indexInfo.Key, indexInfo.LowestBlockNumber(TempFileHandle, blockNumberBytes), dbKey);
            db.PutSpan(dbKey, SerializeIndexInfo(indexInfo), WriteFlags.DisableWAL);
        }

        private static IndexInfo CreateDbIndex(byte[] keyPrefix, SetReceiptsStats stats)
        {
            Interlocked.Increment(ref stats.NewDbIndexes);
            return IndexInfo.NewDb(keyPrefix, -1);
        }

        private IndexInfo CreateTempIndex(byte[] keyPrefix, SetReceiptsStats stats)
        {
            // TODO: improve awaiting
            long freePage = _tempPagesPool.TakePageAsync().WaitResult();

            // TODO: Save index offset to DB immediately after obtaining the page
            Interlocked.Increment(ref stats.NewTempIndexes);
            return IndexInfo.NewTemp(keyPrefix, freePage, 0, -1);
        }

        private IndexInfo CreateTempIndex(IndexInfo oldIndex, SetReceiptsStats stats)
        {
            if (oldIndex.Type != IndexType.DB || oldIndex.LastBlockNumber < 0)
                throw ValidationException($"Attempt to create a temp index from an unsuitable existing index.");

            // TODO: improve awaiting
            long freePage = _tempPagesPool.TakePageAsync().WaitResult();

            Span<byte> oldIndexData = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(oldIndexData, oldIndex.LastBlockNumber);
            RandomAccess.Write(_tempPagesPool.FileHandle, oldIndexData, freePage);

            // TODO: Save index offset to DB immediately after obtaining the page
            Interlocked.Increment(ref stats.NewTempFromDbIndexes);
            return IndexInfo.NewTemp(oldIndex, freePage);
        }

        private IndexInfo CreateTempIndex(byte[] keyPrefix, IReadOnlyList<int> forBlockNums, SetReceiptsStats stats)
        {
            return forBlockNums.Count == 1
                ? CreateDbIndex(keyPrefix, stats)
                : CreateTempIndex(keyPrefix, stats);
        }

        private IndexInfo? GetIndex(IDb db, byte[] keyPrefix, IReadOnlyList<int> forBlockNums, SetReceiptsStats stats)
        {
            var firstBlockNum = forBlockNums[0];
            var dbKey = new byte[keyPrefix.Length + sizeof(int)];

            // TODO: check if Seek and a few Next (or using reversed data order) will make use of prefix seek
            byte[] dbPrefix = new byte[dbKey.Length]; // TODO: check if ArrayPool will work (as size is not guaranteed)
            Array.Copy(keyPrefix, dbPrefix, keyPrefix.Length);

            CreateDbKey(keyPrefix, firstBlockNum, dbKey);

            var options = new IteratorOptions
            {
                IsTailing = true,
                LowerBound = dbPrefix
            };
            using IIterator<byte[], byte[]> iterator = db.GetIterator(ref options);

            var watch = Stopwatch.StartNew();
            iterator.SeekForPrev(dbKey);

            if (EnableStateChecks && !iterator.Valid())
            {
                iterator.Seek(dbKey.ToArray());
                if (iterator.Valid() && iterator.Key().AsSpan()[..keyPrefix.Length].SequenceEqual(keyPrefix))
                {
                    //var data = Enumerate(db.GetIterator(ref options)).Select(x => x.key.ToHexString()).ToArray();
                    throw ValidationException("Invalid iterator behaviour.");
                }
            }

            if (iterator.Valid() && // Found key is less than or equal to the requested one
                iterator.Key().AsSpan()[..keyPrefix.Length].SequenceEqual(keyPrefix))
            {
                stats.SeekForPrevHit.Include(watch.Elapsed);
                return DeserializeIndexInfo(iterator.Key(), iterator.Value());
            }
            else
            {
                stats.SeekForPrevMiss.Include(watch.Elapsed);
            }

            return null;
        }

        /// <summary>
        /// Saves a key consisting of the <c>key || block-number</c> byte array to <paramref name="buffer"/>
        /// </summary>
        private static void CreateDbKey(ReadOnlySpan<byte> key, int blockNumber, Span<byte> buffer)
        {
            key.CopyTo(buffer);
            BinaryPrimitives.WriteInt32BigEndian(buffer[key.Length..], blockNumber);
        }

        private static (byte[] key, int blockNumber) SplitDbKey(ReadOnlySpan<byte> dbKey) =>
        (
            dbKey[..^sizeof(int)].ToArray(),
            BinaryPrimitives.ReadInt32BigEndian(dbKey[^sizeof(int)..])
        );

        private static ReadOnlySpan<byte> LoadData(SafeFileHandle fileHandle, IndexInfo indexInfo, Span<byte> buffer)
        {
            if (indexInfo.Type == IndexType.DB)
            {
                BinaryPrimitives.WriteInt32LittleEndian(buffer, indexInfo.LastBlockNumber);
                return buffer[..sizeof(int)];
            }

            int count = indexInfo.ByteLength;
            RandomAccess.Read(fileHandle, buffer[..count], indexInfo.Offset);
            return buffer[..count];
        }

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

            if (length > PageSize)
                throw ValidationException($"Compressed data is too large ({length} bytes).");

            return buffer[..length];
        }

        private long Append(FileStream fileStream, ReadOnlySpan<byte> data)
        {
            long offset = fileStream.Length;
            fileStream.Seek(offset, SeekOrigin.Begin);
            fileStream.Write(data);
            return offset;
        }

        private byte[] SerializeIndexInfo(IndexInfo index)
        {
            if (index.Type == IndexType.DB)
                return [];

            return SerializeIndexInfo(index.Type, index.Offset, index.Length, index.LastBlockNumber);
        }

        private byte[] SerializeIndexInfo(IndexType type, long offset, int length, int lastBlockNumber)
        {
            if (length > PageSize / (type == IndexType.Final ? 1 : sizeof(int)))
                throw ValidationException($"Invalid {type} index length ({length}).");

            byte[] data = new byte[
                1 +
                sizeof(long) +
                sizeof(int) +
                sizeof(int)
            ]; // TODO: use Array pool

            var valIndex = 0;
            Span<byte> span = data.AsSpan();

            data[0] = (byte)type;
            BinaryPrimitives.WriteInt64BigEndian(span[(valIndex += 1)..], offset);
            BinaryPrimitives.WriteInt32BigEndian(span[(valIndex += sizeof(long))..], length);
            BinaryPrimitives.WriteInt32BigEndian(span[(valIndex += sizeof(int))..], lastBlockNumber);

            if(EnableStateChecks)
            {
                IndexInfo deserialized = DeserializeIndexInfo(new byte[Address.Size + sizeof(int)], data);
                if (deserialized.Type != type || deserialized.Offset != offset ||
                    deserialized.Length != length || deserialized.LastBlockNumber != lastBlockNumber)
                {
                    throw ValidationException("Invalid index serialization/deserialization.");
                }
            }

            return data;
        }

        private IndexInfo DeserializeIndexInfo(ReadOnlySpan<byte> dbKey, ReadOnlySpan<byte> data)
        {
            var (key, blockNumber) = SplitDbKey(dbKey);

            if (data.Length == 0)
                return IndexInfo.NewDb(key, blockNumber);

            var valIndex = 0;

            IndexInfo result = new(
                key,
                type: (IndexType)data[0],
                offset: BinaryPrimitives.ReadInt64BigEndian(data[(valIndex += 1)..]),
                length: BinaryPrimitives.ReadInt32BigEndian(data[(valIndex += sizeof(long))..]),
                lastBlockNumber: BinaryPrimitives.ReadInt32BigEndian(data[(valIndex += sizeof(int))..])
            );

            if (!Enum.IsDefined(result.Type) ||
                result.ByteLength > PageSize ||
                result.LastBlockNumber < 0 ||
                result.Offset < 0 ||
                (result.Type != IndexType.Final && result.LengthRemaining <= 0))
            {
                throw ValidationException("Invalid deserialized index.");
            }

            return result;
        }

        // used for data validation, TODO: remove, replace with tests
        private static Exception ValidationException(string message) => new InvalidOperationException(message);

        private enum IndexType : byte
        {
            DB = 0,
            Temp = 1,
            Final = 2
        }

        // TODO: make ref struct?
        private class IndexInfo
        {
            public byte[] Key { get; private init; }
            public IndexType Type { get; private init; }
            public bool IsTemp => Type != IndexType.Final;
            public long Offset { get; private init; }

            public int Length { get; set; }
            public int LastBlockNumber { get; set; }

            public int ByteLength => Type != IndexType.Final ? Length * sizeof(int) : Length;
            public long? Position => Type != IndexType.DB ? Offset + ByteLength : -1;

            public int LengthRemaining => Type switch
            {
                IndexType.DB => LastBlockNumber < 0 ? 1 : 0,
                IndexType.Temp => PageSize / sizeof(int) - Length,
                _ => 0
            };

            public int ByteLengthRemaining => IsTemp ? LengthRemaining * sizeof(int) : 0;

            public IndexInfo(byte[] key, IndexType type, long offset, int length, int lastBlockNumber)
            {
                Key = key;
                Type = type;
                Offset = offset;
                Length = length;
                LastBlockNumber = lastBlockNumber;
            }

            public static IndexInfo NewDb(byte[] key, int lastBlockNumber) =>
                new(key, IndexType.DB, -1, 0, lastBlockNumber);

            public static IndexInfo NewTemp(byte[] key, long offset, int length, int lastBlockNumber) =>
                new(key, IndexType.Temp, offset, length, lastBlockNumber);

            public static IndexInfo NewTemp(IndexInfo oldIndex, long offset) =>
                new(oldIndex.Key, IndexType.Temp, offset, 1, oldIndex.LastBlockNumber);

            private int _lowestBlockNumber = -1;

            public int LowestBlockNumber(SafeFileHandle fileHandle, Span<byte> buffer)
            {
                if (Type == IndexType.DB)
                    return LastBlockNumber;

                if (_lowestBlockNumber < 0)
                {
                    RandomAccess.Read(fileHandle, buffer[..4], Offset);
                    _lowestBlockNumber = BinaryPrimitives.ReadInt32LittleEndian(buffer);
                }

                return _lowestBlockNumber;
            }
        }
    }
}
