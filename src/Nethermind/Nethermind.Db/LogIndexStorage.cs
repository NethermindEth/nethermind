using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.Db
{
    public class LogIndexStorage : IDisposable
    {
        private readonly SafeFileHandle _tempFileHandle;
        private readonly SafeFileHandle _finalFileHandle;
        private readonly FileStream _tempFileStream;
        private readonly FileStream _finalFileStream;
        private readonly IDb _addressDb;
        private readonly IDb _topicsDb;
        private readonly IDb _mainDb;
        private readonly ILogger _logger;
        private const int FixedLength = 4096;
        private readonly ConcurrentDictionary<byte[], IndexInfo> _indexLocks = new();

        public LogIndexStorage(IColumnsDb<LogIndexColumns> columnsDb, ILogger logger, string tempFilePath = null, string finalFilePath = null)
        {
            tempFilePath = tempFilePath ?? Path.Combine(Directory.GetCurrentDirectory(), "temp_index.bin");
            finalFilePath = finalFilePath ?? Path.Combine(Directory.GetCurrentDirectory(), "finalized_index.bin");

            _logger = logger;

            _logger.Info($"Temp file path: {tempFilePath}");
            _logger.Info($"Final file path: {finalFilePath}");

            _tempFileHandle = File.OpenHandle(tempFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite);
            _finalFileHandle = File.OpenHandle(finalFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite);

            _tempFileStream = new FileStream(_tempFileHandle, FileAccess.ReadWrite);
            _finalFileStream = new FileStream(_finalFileHandle, FileAccess.ReadWrite);

            _addressDb = columnsDb.GetColumnDb(LogIndexColumns.Addresses);
            _topicsDb = columnsDb.GetColumnDb(LogIndexColumns.Topics);
            _mainDb = columnsDb.GetColumnDb(LogIndexColumns.Default);
        }

        public void Dispose()
        {
            _tempFileStream?.Dispose();
            _finalFileStream?.Dispose();
            _tempFileHandle?.Dispose();
            _finalFileHandle?.Dispose();
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
            using var iterator = db.GetIterator(true);
            iterator.Seek(keyPrefix);

            int? nextLowestBlockNumber = null;

            Span<byte> blockNumberBytes = stackalloc byte[4];
            byte[] indexBuffer = ArrayPool<byte>.Shared.Rent(FixedLength);
            int[] decompressedBlockNumbers = ArrayPool<int>.Shared.Rent(FixedLength / 4);

            try
            {
                while (iterator.Valid() && iterator.Key().AsSpan().Slice(0, keyPrefix.Length).SequenceEqual(keyPrefix))
                {
                    ReadOnlySpan<byte> firstKey = iterator.Key().AsSpan();
                    int keyLength = firstKey.Length;

                    if (keyLength == keyPrefix.Length + 4)
                    {
                        int currentLowestBlockNumber = BinaryPrimitives.ReadInt32LittleEndian(firstKey.Slice(keyPrefix.Length));

                        iterator.Next();
                        if (iterator.Valid() && iterator.Key().AsSpan().Slice(0, keyPrefix.Length).SequenceEqual(keyPrefix))
                        {
                            ReadOnlySpan<byte> nextKey = iterator.Key().AsSpan();
                            nextLowestBlockNumber = BinaryPrimitives.ReadInt32LittleEndian(nextKey.Slice(keyPrefix.Length));
                        }
                        else
                        {
                            nextLowestBlockNumber = int.MaxValue;
                        }

                        if (nextLowestBlockNumber > from && currentLowestBlockNumber <= to)
                        {
                            var indexInfo = new IndexInfo(keyPrefix.ToArray(), db.Get(firstKey));
                            SafeFileHandle fileHandle = indexInfo.IsTemp ? _tempFileHandle : _finalFileHandle;
                            var data = LoadData(fileHandle, indexInfo, indexBuffer.AsSpan());
                            int[] blocks = indexInfo.IsTemp ? MemoryMarshal.Cast<byte, int>(data).ToArray() : Decompress(data, decompressedBlockNumbers.AsSpan());

                            int startIndex = BinarySearch(blocks, from);
                            if (startIndex < 0)
                            {
                                startIndex = ~startIndex;
                            }

                            for (int i = startIndex; i < blocks.Length; i++)
                            {
                                int block = blocks[i];
                                if (block > to)
                                {
                                    yield break;
                                }
                                yield return block;
                            }
                        }
                    }
                    else
                    {
                        iterator.Next();
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(indexBuffer);
                ArrayPool<int>.Shared.Return(decompressedBlockNumbers);
            }
        }

        public void SetReceipts(int blockNumber, TxReceipt[] receipts, bool isBackwardSync)
        {
            var addressIndexes = new Dictionary<byte[], IndexInfo>();
            var topicIndexes = new Dictionary<byte[], IndexInfo>();

            Span<byte> blockNumberBytes = stackalloc byte[4];
            byte[] indexBuffer = ArrayPool<byte>.Shared.Rent(FixedLength);

            try
            {
                foreach (var receipt in receipts)
                {
                    if (receipt?.Logs != null)
                    {
                        foreach (var log in receipt.Logs)
                        {
                            // Handle address logs
                            byte[] addressKey = log.LoggersAddress.Bytes;
                            if (!addressIndexes.TryGetValue(addressKey, out var addressIndexInfo))
                            {
                                addressIndexInfo = GetOrCreateTempIndex(_addressDb, addressKey);
                                addressIndexInfo.Lock();
                                addressIndexes[addressKey] = addressIndexInfo;
                            }

                            ProcessLog(blockNumber, addressIndexInfo, blockNumberBytes, indexBuffer.AsSpan(), addressKey, _addressDb, addressIndexes);

                            // Handle topic logs
                            foreach (var topic in log.Topics)
                            {
                                var topicKey = topic.Bytes;
                                var topicKeyArray = topicKey.ToArray();
                                if (!topicIndexes.TryGetValue(topicKeyArray, out var topicIndexInfo))
                                {
                                    topicIndexInfo = GetOrCreateTempIndex(_topicsDb, topicKey);
                                    topicIndexInfo.Lock();
                                    topicIndexes[topicKeyArray] = topicIndexInfo;
                                }

                                ProcessLog(blockNumber, topicIndexInfo, blockNumberBytes, indexBuffer.AsSpan(), topicKeyArray, _topicsDb, topicIndexes);
                            }
                        }
                    }
                }
            }
            finally
            {
                FinalizeIndexes(_addressDb, addressIndexes, blockNumberBytes);
                FinalizeIndexes(_topicsDb, topicIndexes, blockNumberBytes);
                ArrayPool<byte>.Shared.Return(indexBuffer);
            }
        }

        private void ProcessLog(int blockNumber, IndexInfo indexInfo, Span<byte> blockNumberBytes, Span<byte> indexBuffer, byte[] key, IDb db, Dictionary<byte[], IndexInfo> indexDictionary)
        {
            if (blockNumber <= indexInfo.LastBlockNumber)
            {
                return;
            }

            long position = indexInfo.Offset + indexInfo.Length * 4;
            BinaryPrimitives.WriteInt32LittleEndian(blockNumberBytes, blockNumber);
            RandomAccess.Write(_tempFileHandle, blockNumberBytes, position);
            indexInfo.Length++;
            indexInfo.LastBlockNumber = blockNumber;

            if (indexInfo.IsReadyToFinalize())
            {
                var data = LoadData(_tempFileHandle, indexInfo, indexBuffer);
                var compressed = Compress(data, indexBuffer);
                long offset = Append(_finalFileStream, compressed);

                byte[] dbKey = ArrayPool<byte>.Shared.Rent(key.Length + sizeof(int));
                try
                {
                    CreateDbKey(indexInfo.Key, indexInfo.LowestBlockNumber(_tempFileHandle, blockNumberBytes), dbKey.AsSpan());
                    db.PutSpan(dbKey.AsSpan(0, key.Length + sizeof(int)), CreateIndexValue(offset, compressed.Length, false, indexInfo.LastBlockNumber));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(dbKey);
                }

                indexDictionary.Remove(key);

                AddFreePage(indexInfo);
            }
        }

        private void FinalizeIndexes(IDb db, Dictionary<byte[], IndexInfo> indexes, Span<byte> blockNumberBytes)
        {
            foreach (var indexInfo in indexes.Values)
            {
                byte[] dbKey = ArrayPool<byte>.Shared.Rent(indexInfo.Key.Length + sizeof(int));
                try
                {
                    CreateDbKey(indexInfo.Key, indexInfo.LowestBlockNumber(_tempFileHandle, blockNumberBytes), dbKey.AsSpan());
                    db.PutSpan(dbKey.AsSpan(0, indexInfo.Key.Length + sizeof(int)), CreateIndexValue(indexInfo.Offset, indexInfo.Length, indexInfo.IsTemp, indexInfo.LastBlockNumber));
                }
                catch
                {
                    _logger.Info(JsonSerializer.Serialize(indexInfo));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(dbKey);
                }
                indexInfo.Unlock();
            }
        }

        private IndexInfo GetOrCreateTempIndex(IDb db, ReadOnlySpan<byte> key)
        {
            using var iterator = db.GetIterator(true);
            iterator.Seek(key.ToArray());

            Span<byte> keyBytes = stackalloc byte[key.Length + sizeof(int)];
            bool setKeyBytes = false;

            while (iterator.Valid() && iterator.Key().AsSpan().Slice(0, key.Length).SequenceEqual(key))
            {
                keyBytes = iterator.Key();
                setKeyBytes = true;
                iterator.Next();
            }

            if (setKeyBytes)
            {
                IndexInfo latestIndex = new IndexInfo(key.ToArray(), db.Get(keyBytes));

                if (latestIndex.IsTemp)
                {
                    return latestIndex;
                }
            }


            long freePage = GetFreePage() ?? GrowFile(_tempFileStream, FixedLength);
            return new IndexInfo(key.ToArray(), freePage, 0, true, 0);
        }

        private void AddFreePage(IndexInfo indexInfo)
        {
            lock (_mainDb)
            {
                byte[] freePages = _mainDb.Get(Encoding.UTF8.GetBytes("freePages"));
                long newFreePage = indexInfo.Offset;

                // Prepare new array with the old free pages plus the new one
                byte[] newFreePages = ArrayPool<byte>.Shared.Rent((freePages?.Length ?? 0) + sizeof(long));

                try
                {
                    if (freePages != null)
                    {
                        freePages.CopyTo(newFreePages, 0);
                    }

                    // Append the new free page
                    BinaryPrimitives.WriteInt64LittleEndian(newFreePages.AsSpan(freePages?.Length ?? 0), newFreePage);

                    // Save the updated free pages back to the database
                    _mainDb.PutSpan(Encoding.UTF8.GetBytes("freePages"), newFreePages.AsSpan(0, (freePages?.Length ?? 0) + sizeof(long)));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(newFreePages);
                }
            }
        }


        private long? GetFreePage()
        {
            byte[] freePages = _mainDb.Get(Encoding.UTF8.GetBytes("freePages"));

            if (freePages != null && freePages.Length >= sizeof(long))
            {
                // Extract the last 8 bytes as the free page
                long freePage = BinaryPrimitives.ReadInt64LittleEndian(freePages.AsSpan(freePages.Length - sizeof(long)));

                // Update the freePages array by removing the last 8 bytes
                _mainDb.PutSpan(Encoding.UTF8.GetBytes("freePages"), freePages.AsSpan(0, freePages.Length - sizeof(long)));

                return freePage;
            }

            return null;
        }


        public void CreateDbKey(ReadOnlySpan<byte> key, int blockNumber, Span<byte> buffer)
        {
            key.CopyTo(buffer);
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(key.Length), blockNumber);
        }



        private ReadOnlySpan<byte> LoadData(SafeFileHandle fileHandle, IndexInfo indexInfo, Span<byte> buffer)
        {
            int count = indexInfo.IsTemp ? indexInfo.Length * 4 : indexInfo.Length;
            RandomAccess.Read(fileHandle, buffer.Slice(0, count), indexInfo.Offset);
            return buffer.Slice(0, count);
        }

        private unsafe int[] Decompress(ReadOnlySpan<byte> data, Span<int> decompressedBlockNumbers)
        {
            fixed (byte* dataPtr = data)
            fixed (int* decompressedPtr = decompressedBlockNumbers)
            {
                TurboPFor.p4nddec128v32(dataPtr, decompressedBlockNumbers.Length, decompressedPtr);
            }
            return decompressedBlockNumbers.Slice(0, data.Length / sizeof(int)).ToArray();
        }

        private int BinarySearch(int[] blocks, int from)
        {
            int index = Array.BinarySearch(blocks, from);
            return index < 0 ? ~index : index;
        }

        private unsafe ReadOnlySpan<byte> Compress(ReadOnlySpan<byte> data, Span<byte> buffer)
        {
            ReadOnlySpan<int> blockNumbers = MemoryMarshal.Cast<byte, int>(data);
            fixed (int* blockNumbersPtr = blockNumbers)
            fixed (byte* compressedPtr = buffer)
            {
                TurboPFor.p4ndenc128v32(blockNumbersPtr, blockNumbers.Length, compressedPtr);
            }
            return buffer.Slice(0, blockNumbers.Length * sizeof(int)); // Adjust length if necessary
        }

        private long Append(FileStream fileStream, ReadOnlySpan<byte> data)
        {
            long offset = fileStream.Length;
            fileStream.Seek(offset, SeekOrigin.Begin);
            fileStream.Write(data);
            return offset;
        }

        private byte[] CreateIndexValue(long offset, int count, bool isTemp, int lastBlockNumber)
        {
            Span<byte> value = stackalloc byte[1 + sizeof(long) + 2 * sizeof(int)];
            value[0] = (byte)(isTemp ? FileType.TEMP : FileType.FINAL);
            BinaryPrimitives.WriteInt64LittleEndian(value.Slice(1), offset);
            BinaryPrimitives.WriteInt32LittleEndian(value.Slice(1 + sizeof(long)), count);
            BinaryPrimitives.WriteInt32LittleEndian(value.Slice(1 + sizeof(long) + sizeof(int)), lastBlockNumber);
            return value.ToArray();
        }

        private long GrowFile(FileStream fileStream, int length)
        {
            long originalLength = fileStream.Length;
            fileStream.SetLength(originalLength + length);
            return originalLength;
        }

        private class IndexInfo
        {
            public byte[] Key { get; }
            public long Offset { get; set; }
            public bool IsTemp { get; set; }
            public int Length { get; set; }
            public int LastBlockNumber { get; set; }
            private readonly object _lock = new();

            public IndexInfo(byte[] key, long offset, int length, bool isTemp, int lastBlockNumber)
            {
                Key = key;
                Offset = offset;
                Length = length;
                IsTemp = isTemp;
                LastBlockNumber = lastBlockNumber;
            }

            public IndexInfo(byte[] key, Span<byte> value)
            {
                Key = key;
                IsTemp = value[0] == (byte)FileType.TEMP;
                Offset = BinaryPrimitives.ReadInt64LittleEndian(value.Slice(1));
                Length = BinaryPrimitives.ReadInt32LittleEndian(value.Slice(1 + sizeof(long)));
                LastBlockNumber = BinaryPrimitives.ReadInt32LittleEndian(value.Slice(1 + sizeof(long) + sizeof(int)));
            }

            public bool IsReadyToFinalize()
            {
                return Length >= FixedLength / 4;
            }

            public void Lock()
            {
                Monitor.Enter(_lock);
            }

            public void Unlock()
            {
                Monitor.Exit(_lock);
            }

            public byte[] ToBytes()
            {
                Span<byte> value = stackalloc byte[1 + sizeof(long) + 2 * sizeof(int)];
                value[0] = (byte)(IsTemp ? FileType.TEMP : FileType.FINAL);
                BinaryPrimitives.WriteInt64LittleEndian(value.Slice(1), Offset);
                BinaryPrimitives.WriteInt32LittleEndian(value.Slice(1 + sizeof(long)), Length);
                BinaryPrimitives.WriteInt32LittleEndian(value.Slice(1 + sizeof(long) + sizeof(int)), LastBlockNumber);
                return value.ToArray();
            }

            public int LowestBlockNumber(SafeFileHandle fileHandle, Span<byte> buffer)
            {
                RandomAccess.Read(fileHandle, buffer.Slice(0, 4), Offset);
                return BinaryPrimitives.ReadInt32LittleEndian(buffer);
            }
        }

        private enum FileType : byte
        {
            TEMP = 0x01,
            FINAL = 0x02
        }
    }
}
