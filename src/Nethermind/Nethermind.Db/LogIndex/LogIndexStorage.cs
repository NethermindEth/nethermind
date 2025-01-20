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
        private static readonly byte[] LastBlockNumKey = "LastBlockNum"u8.ToArray();

        public string TempFilePath { get; }
        public string FinalFilePath { get; }

        private readonly SafeFileHandle _finalFileHandle;
        private readonly FileStream _finalFileStream;
        private readonly IDb _defaultDb;
        private readonly IDb _addressDb;
        private readonly IDb _topicsDb;
        private readonly ILogger _logger;
        private readonly int _ioParallelism;
        private const int BlockNumSize = sizeof(int);
        private const int PageSize = 4096;
        private const int IndexDataMaxCount = 8 - 1;
        private const int PageDataMaxCount = PageSize / BlockNumSize;

        private readonly IFilePagesPool _tempPagesPool;

        // used for state/data validation, TODO: remove, replace with tests
        private static readonly bool EnableStateChecks = false;

        // TODO: ensure class is singleton
        public LogIndexStorage(IColumnsDb<LogIndexColumns> columnsDb, ILogger logger, string baseDbPath, int ioParallelism)
        {
            if (ioParallelism < 1) throw new ArgumentException("IO parallelism degree must be a positive value.", nameof(ioParallelism));
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

            _defaultDb = columnsDb.GetColumnDb(LogIndexColumns.Default);
            _tempPagesPool = new AsyncFilePagesPool(TempFilePath, _defaultDb, PageSize)
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

        public async Task StopAsync()
        {
            await _setReceiptsSemaphore.WaitAsync();
            await _tempPagesPool.StopAsync();
        }

        async ValueTask IAsyncDisposable.DisposeAsync()
        {
            await _tempPagesPool.DisposeAsync();
            await _finalFileStream.DisposeAsync();
            _finalFileHandle.Dispose();
            _setReceiptsSemaphore.Dispose();
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

                    int currentLowestBlockNumber = GetBlockNumber(firstKey);

                    iterator.Next();
                    int? nextLowestBlockNumber;
                    if (iterator.Valid() && iterator.Key().AsSpan()[..keyPrefix.Length].SequenceEqual(keyPrefix))
                    {
                        ReadOnlySpan<byte> nextKey = iterator.Key().AsSpan();
                        nextLowestBlockNumber = GetBlockNumber(nextKey);
                    }
                    else
                    {
                        nextLowestBlockNumber = int.MaxValue;
                    }

                    if (nextLowestBlockNumber > from && currentLowestBlockNumber <= to)
                    {
                        var indexInfo = IndexInfo.Deserialize(firstKey, db.Get(firstKey));
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
            (Address, IndexInfo)[] addressData = Enumerate(addressIterator).Select(x => (new Address(SplitDbKey(x.key).key), IndexInfo.Deserialize(x.key, x.value))).ToArray();

            // Total: 5_654_366
            // From first 200_000: 1 - 134_083 (0.670415), 2 - 10_486, 3 - 33_551, 4 - 4872, 5 - 4227, 6 - 4764, 7 - 6792, 8 - 609, 9 - 67, 10 - 55
            // From first 300_000: 1 - 228_553 (0.761843333)
            // From first 1_000_000: 1 - 875_216 (0.875216)
            //var topicData = Enumerate(topicIterator).Select(x => (new Hash256(SplitDbKey(x.key).key), DeserializeIndexInfo(x.key, x.value))).ToArray();
            var topicData = Enumerate(topicIterator).Take(200_000).Select(x => (topic: new Hash256(SplitDbKey(x.key).key), Index: IndexInfo.Deserialize(x.key, x.value))).GroupBy(x => x.Index.TotalValuesCount).ToDictionary(g => g.Key, g => g.Count());

            GC.KeepAlive(addressData);
            GC.KeepAlive(topicData);

            return Task.CompletedTask;
        }

        private readonly SemaphoreSlim _setReceiptsSemaphore = new (1, 1);

        public Task<SetReceiptsStats> SetReceiptsAsync(int blockNumber, TxReceipt[] receipts, bool isBackwardSync,
            CancellationToken cancellationToken)
        {
            return SetReceiptsAsync([new(blockNumber, receipts)], isBackwardSync, cancellationToken);
        }

        // batch is expected to be sorted
        public async Task<SetReceiptsStats> SetReceiptsAsync(
            BlockReceipts[] batch, bool isBackwardSync, CancellationToken cancellationToken
        )
        {
            if (!await _setReceiptsSemaphore.WaitAsync(TimeSpan.Zero, CancellationToken.None))
                throw new InvalidOperationException($"Concurrent invocations of {nameof(SetReceiptsAsync)} is not supported.");

            await _tempPagesPool.StartAsync();

            var stats = new SetReceiptsStats();

            if (BuildProcessingDictionary(batch, stats, cancellationToken) is not { } dictionary)
                return stats;

            var finalizeQueue = Channel.CreateUnbounded<IndexInfo>();
            Task finalizingTask = KeepFinalizingIndexes(finalizeQueue.Reader, stats, cancellationToken);

            try
            {
                var watch = Stopwatch.StartNew();
                Parallel.ForEach(
                    dictionary, new() { MaxDegreeOfParallelism = _ioParallelism },
                    pair => SaveBlockNumbersByKey(pair.Key, pair.Value, stats, finalizeQueue.Writer)
                );
                stats.ProcessingData.Include(watch.Elapsed);

                WriteLastKnownBlockNumber(_lastKnownBlock = batch[^1].BlockNumber);
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

                _setReceiptsSemaphore.Release();
            }

            return stats;
        }

        private void SaveBlockNumbersByKey(byte[] key, IReadOnlyList<int> blockNums, SetReceiptsStats stats, ChannelWriter<IndexInfo> finalizeQueue)
        {
            byte[]? writeBuffer = null;

            try
            {
                IDb db = key.Length switch
                {
                    Address.Size => _addressDb,
                    Hash256.Size => _topicsDb,
                    var size => throw ValidationException($"Unexpected key of {size} bytes.")
                };

                IndexInfo? indexInfo = GetIndex(db, key, stats);

                if (indexInfo != null && blockNums[^1] <= indexInfo.LastBlockNumber)
                    return;

                if (indexInfo?.Type != IndexType.Temp)
                    indexInfo = null;

                int writeCount = 0;
                writeBuffer = ArrayPool<byte>.Shared.Rent(IndexDataMaxCount * BlockNumSize);

                foreach (var blockNum in blockNums)
                {
                    indexInfo ??= CreateTempIndex(key, stats);

                    if (indexInfo.LastBlockNumber is {} lastIndexNum && blockNum <= lastIndexNum)
                        continue;

                    BinaryPrimitives.WriteInt32LittleEndian(writeBuffer.AsSpan(writeCount * BlockNumSize), blockNum);
                    writeCount += 1;

                    if (indexInfo.TotalValuesCount + writeCount == PageDataMaxCount)
                    {
                        indexInfo.AddData(writeBuffer.AsSpan(..(writeCount * BlockNumSize)));
                        writeCount = 0;

                        finalizeQueue.TryWrite(indexInfo);
                        indexInfo = null;
                    }

                    else if (indexInfo.DataValuesCount + writeCount > IndexDataMaxCount)
                    {
                        indexInfo.AddData(writeBuffer.AsSpan(..(writeCount * BlockNumSize)));
                        writeCount = 0;

                        StoreIndexData(indexInfo, stats);
                    }
                }

                if (writeBuffer is { Length: > 0 } && indexInfo != null)
                    indexInfo.AddData(writeBuffer.AsSpan(..(writeCount * BlockNumSize)));

                if (indexInfo != null)
                    SaveIndex(db, indexInfo);
            }
            finally
            {
                if (writeBuffer != null) ArrayPool<byte>.Shared.Return(writeBuffer);
            }
        }

        public PagesStats PagesStats => _tempPagesPool.Stats;

        private SafeFileHandle TempFileHandle => _tempPagesPool.FileHandle;

        private async Task KeepFinalizingIndexes(ChannelReader<IndexInfo> reader, SetReceiptsStats stats, CancellationToken cancellationToken)
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

                    var blockNumber = indexInfo.GetLowestBlockNumber(TempFileHandle, blockNumberBytes)
                        ?? throw ValidationException("Attempt to finalize index without starting block.");

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

                    CreateDbKey(indexInfo.Key, blockNumber, dbKey);
                    var finalIndexData = IndexInfo.Serialize(IndexType.Final, new FileRef(offset, compressed.Length), indexInfo.LastBlockNumber, []);
                    db.PutSpan(dbKey, finalIndexData, WriteFlags.DisableWAL);

                    Interlocked.Increment(ref stats.NewFinalIndexes);

                    if (indexInfo.File is { } fileRef)
                    {
                        // TODO: improve awaiting
                        _tempPagesPool.ReturnPageAsync(fileRef.Offset).Wait();
                    }
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

        private void StoreIndexData(IndexInfo indexInfo, SetReceiptsStats stats)
        {
            if (indexInfo.Type == IndexType.Final)
                throw ValidationException("Attempt to add data to finalized index.");

            if (indexInfo.TotalValuesCount > PageDataMaxCount)
                throw ValidationException($"Attempt to write more blocks that page can fit ({indexInfo.TotalValuesCount}).");

            if (indexInfo.DataValuesCount == 0)
                throw ValidationException("Attempt to write index without data.");

            // Allocate file page if needed
            if (indexInfo.File is not {} oldFileRef)
            {
                // TODO: improve awaiting
                long freePage = _tempPagesPool.TakePageAsync().WaitResult();
                indexInfo.File = oldFileRef = new(freePage);
            }

            var bytes = indexInfo.Data;
            RandomAccess.Write(TempFileHandle, bytes, oldFileRef.Position);
            indexInfo.File = new FileRef(oldFileRef, bytes.Length);
            indexInfo.ClearData();

            stats.BytesWritten.Include(bytes.Length);
        }

        private void SaveIndex(IDb db, IndexInfo indexInfo)
        {
            if (indexInfo.LastBlockNumber < indexInfo.TotalValuesCount - 1)
                throw ValidationException("Index last block number is too small.");

            Span<byte> blockNumberBytes = stackalloc byte[sizeof(int)];
            Span<byte> dbKey = stackalloc byte[indexInfo.Key.Length + sizeof(int)];

            var blockNumber = indexInfo.GetLowestBlockNumber(TempFileHandle, blockNumberBytes)
                ?? throw ValidationException("Attempt to save index without starting block.");

            CreateDbKey(indexInfo.Key, blockNumber, dbKey);
            db.PutSpan(dbKey, indexInfo.Serialize(), WriteFlags.DisableWAL);
        }

        private static IndexInfo CreateTempIndex(byte[] keyPrefix, SetReceiptsStats stats)
        {
            // TODO: Save index offset to DB immediately after obtaining the page
            Interlocked.Increment(ref stats.NewTempIndexes);
            return IndexInfo.Temp(keyPrefix);
        }

        private static IndexInfo? GetIndex(IDb db, byte[] keyPrefix, SetReceiptsStats stats)
        {
            var dbKey = new byte[keyPrefix.Length + BlockNumSize];

            // TODO: check if Seek and a few Next (or using reversed data order) will make use of prefix seek
            byte[] dbPrefix = new byte[dbKey.Length]; // TODO: check if ArrayPool will work (as size is not guaranteed)
            Array.Copy(keyPrefix, dbPrefix, keyPrefix.Length);

            CreateDbKey(keyPrefix, int.MaxValue, dbKey);

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
                return IndexInfo.Deserialize(iterator.Key(), iterator.Value());
            }
            else
            {
                stats.SeekForPrevMiss.Include(watch.Elapsed);
            }

            return null;
        }

        /// <summary>
        /// Saves a key consisting of the <c>key || block-number</c> byte array to <paramref name="dbKey"/>
        /// </summary>
        private static void CreateDbKey(ReadOnlySpan<byte> key, int blockNumber, Span<byte> dbKey)
        {
            key.CopyTo(dbKey);
            SetBlockNumber(dbKey, blockNumber);
        }

        private static (byte[] key, int blockNumber) SplitDbKey(ReadOnlySpan<byte> dbKey) =>
        (
            dbKey[..^BlockNumSize].ToArray(),
            GetBlockNumber(dbKey)
        );

        // RocksDB uses big-endian (lexicographic) ordering
        private static int GetBlockNumber(ReadOnlySpan<byte> dbKey) => BinaryPrimitives.ReadInt32BigEndian(dbKey[^BlockNumSize..]);
        private static void SetBlockNumber(Span<byte> dbKey, int blockNumber) => BinaryPrimitives.WriteInt32BigEndian(dbKey[^BlockNumSize..], blockNumber);

        private static byte[] Combine(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
        {
            var result = new byte[first.Length + second.Length];
            first.CopyTo(result.AsSpan());
            second.CopyTo(result.AsSpan(first.Length..));
            return result;
        }

        private static ReadOnlySpan<byte> LoadData(SafeFileHandle fileHandle, IndexInfo indexInfo, Span<byte> buffer)
        {
            var length = 0;

            if (indexInfo.File is { } fileRef)
            {
                var fileLength = indexInfo.FileByteLength;
                RandomAccess.Read(fileHandle, buffer[..fileLength], fileRef.Offset);
                length += fileLength;
            }

            if (indexInfo.Data is { Length: > 0 } data)
            {
                data.AsSpan().CopyTo(buffer[length..]);
                length += data.Length;
            }

            if (length > PageSize)
                throw ValidationException($"Invalid size of loaded data ({length}).");

            return buffer[..length];
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

        private static long Append(FileStream fileStream, ReadOnlySpan<byte> data)
        {
            long offset = fileStream.Length;
            fileStream.Seek(offset, SeekOrigin.Begin);
            fileStream.Write(data);
            return offset;
        }

        private int ReadLastKnownBlockNumber()
        {
            using IIterator<byte[], byte[]> iterator = _defaultDb.GetIterator();

            iterator.Seek(LastBlockNumKey);;
            return iterator.Valid() ? BinaryPrimitives.ReadInt32BigEndian(iterator.Value()) : -1;
        }

        private void WriteLastKnownBlockNumber(int value)
        {
            Span<byte> buffer = ArrayPool<byte>.Shared.RentSpan(sizeof(int));
            BinaryPrimitives.WriteInt32BigEndian(buffer, value);
            _defaultDb.PutSpan(LastBlockNumKey, buffer);
        }

        // used for data validation, TODO: remove, replace with tests
        private static Exception ValidationException(string message) => new InvalidOperationException(message);

        internal enum IndexType : byte
        {
            Temp = 1,
            Final = 2
        }

        internal readonly struct FileRef(long offset, int length = 0)
        {
            public static int Size => sizeof(long) + sizeof(int);

            public long Offset { get; } = offset;
            public int Length { get; } = length; // in bytes
            public long Position => Offset + Length;

            public FileRef(FileRef prev, int length) : this(prev.Offset, prev.Length + length) { }

            public void Serialize(Span<byte> buffer)
            {
                BinaryPrimitives.WriteInt64LittleEndian(buffer, Offset);
                BinaryPrimitives.WriteInt32LittleEndian(buffer[sizeof(long)..], Length);
            }

            public static FileRef Deserialize(ReadOnlySpan<byte> data) => new(
                BinaryPrimitives.ReadInt64LittleEndian(data),
                BinaryPrimitives.ReadInt32LittleEndian(data[sizeof(long)..])
            );
        }

        // TODO: make ref struct or make different implementations depending on Type
        internal class IndexInfo
        {
            public byte[] Key { get; }
            public IndexType Type { get; }
            public FileRef? File { get; set; }
            public int? LastBlockNumber { get; private set; }
            public byte[] Data { get; private set; }
            public bool IsTemp => Type == IndexType.Temp;

            public int FileByteLength => File?.Length ?? 0;

            public int FileValuesCount
            {
                get
                {
                    if (Type == IndexType.Final) return PageSize / BlockNumSize;
                    return FileByteLength / BlockNumSize;
                }
            }

            public int DataValuesCount => Data.Length / BlockNumSize;

            public int TotalValuesCount => FileValuesCount + DataValuesCount;

            private IndexInfo(byte[] key, IndexType type, FileRef? fileRef, int? lastBlockNumber, byte[] data)
            {
                Key = key;
                Type = type;
                File = fileRef;
                LastBlockNumber = lastBlockNumber;
                Data = data;
            }

            /// <summary>
            /// Index with a single value stored to DB and nothing saved to a file.
            /// </summary>
            public static IndexInfo Temp(byte[] key, int lastBlockNumber) =>
                new(key, IndexType.Temp, null, lastBlockNumber, lastBlockNumber.ToLittleEndianByteArray());

            /// <summary>
            /// New index with no blocks added yet.
            /// </summary>
            public static IndexInfo Temp(byte[] key) =>
                new(key, IndexType.Temp, null, null, []);

            private int? _lowestBlockNumber;

            public int? GetLowestBlockNumber(SafeFileHandle fileHandle, Span<byte> buffer)
            {
                if (TotalValuesCount <= 1)
                    return LastBlockNumber;

                if (File is not {} fileRef)
                    return Data.Length > 0 ? BinaryPrimitives.ReadInt32LittleEndian(Data) : null;

                if (_lowestBlockNumber == null)
                {
                    RandomAccess.Read(fileHandle, buffer[..4], fileRef.Offset);
                    _lowestBlockNumber = BinaryPrimitives.ReadInt32LittleEndian(buffer);
                }

                return _lowestBlockNumber;
            }

            public void AddData(Span<byte> data)
            {
                if (data.Length == 0)
                    return;

                Data = Data.Length == 0 ? data.ToArray() : Combine(Data, data);
                LastBlockNumber = BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(^BlockNumSize..));
            }

            public void ClearData()
            {
                Data = [];
            }

            // TODO: use protobuf?
            public static byte[] Serialize(IndexType type, FileRef? fileRef, int? lastBlockNumber, byte[] data)
            {
                if (type == IndexType.Temp && !fileRef.HasValue && data.Length == BlockNumSize)
                    return []; // Minimize size for indexes pointing to a single value

                if (data.Length % BlockNumSize != 0 || data.Length / BlockNumSize > IndexDataMaxCount)
                    throw ValidationException($"Invalid {type} index length ({data.Length}).");

                byte[] result = new byte[
                    1 + // Type
                    1 + // FileRef nullability
                    (fileRef != null ? FileRef.Size : 0) + // FileRef
                    sizeof(int) + // LastBlockNumber
                    data.Length // Data
                ]; // TODO: use Array pool

                var valIndex = 0;
                var span = result.AsSpan();

                // Type
                result[valIndex++] = (byte)type;

                // FileRef
                span[valIndex++] = (byte)(fileRef != null ? 1 : 0);
                if (fileRef.HasValue)
                {
                    fileRef.Value.Serialize(span[valIndex..]);
                    valIndex += FileRef.Size;
                }

                // LastBlockNumber
                BinaryPrimitives.WriteInt32LittleEndian(
                    span[valIndex..],
                    lastBlockNumber ?? -1
                );
                valIndex += sizeof(int);

                // Data
                data.CopyTo(result.AsSpan(valIndex..));

                if(EnableStateChecks)
                {
                    IndexInfo deserialized = Deserialize(new byte[Address.Size + sizeof(int)], result);
                    if (deserialized.Type != type || !Equals(deserialized.File, fileRef) ||
                        deserialized.LastBlockNumber != lastBlockNumber || !deserialized.Data.SequenceEqual(data))
                        throw ValidationException("Invalid index serialization/deserialization.");
                }

                return result;
            }

            public byte[] Serialize() => Serialize(Type, File, LastBlockNumber, Data);

            public static IndexInfo Deserialize(ReadOnlySpan<byte> dbKey, ReadOnlySpan<byte> bytes)
            {
                var (key, blockNumber) = SplitDbKey(dbKey);

                if (bytes.Length == 0)
                    return Temp(key, blockNumber);

                var valIndex = 0;

                // Type
                var type = (IndexType)bytes[valIndex++];

                // FileRef
                FileRef? fileRef = null;
                var hasFileRef = bytes[valIndex++] == 1;
                if (hasFileRef)
                {
                    fileRef = FileRef.Deserialize(bytes[valIndex..]);
                    valIndex += FileRef.Size;
                }

                // LastBlockNumber
                int? lastBlockNumber = BinaryPrimitives.ReadInt32LittleEndian(bytes[valIndex..]) is var num && num != -1 ? num : null;
                valIndex += sizeof(int);

                // Data
                var data = bytes[valIndex..].ToArray();

                IndexInfo result = new(key, type, fileRef, lastBlockNumber, data);

                if (!Enum.IsDefined(result.Type) ||
                    result.FileByteLength > PageSize ||
                    result.LastBlockNumber < 0 ||
                    (result.IsTemp && result.FileByteLength > PageSize))
                {
                    throw ValidationException("Invalid deserialized index.");
                }

                return result;
            }
        }
    }
}
