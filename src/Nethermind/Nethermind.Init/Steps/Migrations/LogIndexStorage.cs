using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;

namespace Nethermind.Init.Steps.Migrations
{
    public class LogIndexStorage
    {
        private readonly SafeFileHandle _tempFileHandle;
        private readonly SafeFileHandle _finalFileHandle;
        private readonly FileStream _tempFileStream;
        private readonly FileStream _finalFileStream;
        private readonly IDbWithIterator _addressDb;
        private readonly IDbWithIterator _topicsDb;
        private readonly IDb _mainDb;
        private readonly ILogger _logger;
        private const int FixedLength = 4096;
        private readonly ConcurrentDictionary<byte[], IndexInfo> _indexLocks = new();

        public LogIndexStorage(SafeFileHandle tempFileHandle, SafeFileHandle finalFileHandle, IDbWithIterator addressDb, IDbWithIterator topicsDb, IDb mainDb, ILogger logger)
        {
            _tempFileHandle = tempFileHandle;
            _finalFileHandle = finalFileHandle;
            _tempFileStream = new FileStream(tempFileHandle, FileAccess.ReadWrite);
            _finalFileStream = new FileStream(finalFileHandle, FileAccess.ReadWrite);
            _addressDb = addressDb;
            _topicsDb = topicsDb;
            _mainDb = mainDb;
            _logger = logger;
        }

        public IEnumerable<int> GetBlockNumbersFor(Address address, int from, int to)
        {
            byte[] keyPrefix = address.Bytes;
            using var iterator = _addressDb.CreateIterator(true);
            iterator.Seek(keyPrefix);

            int? nextLowestBlockNumber = null;

            while (iterator.Valid() && iterator.Key().Take(keyPrefix.Length).SequenceEqual(keyPrefix))
            {
                byte[] key = iterator.Key();
                int currentLowestBlockNumber = BitConverter.ToInt32(key, keyPrefix.Length);

                if (nextLowestBlockNumber.HasValue && nextLowestBlockNumber.Value > to)
                {
                    break;
                }

                if (nextLowestBlockNumber.HasValue && currentLowestBlockNumber > to)
                {
                    break;
                }

                // Save the next lowest block number for the next iteration
                iterator.Next();
                if (iterator.Valid() && iterator.Key().Take(keyPrefix.Length).SequenceEqual(keyPrefix))
                {
                    byte[] nextKey = iterator.Key();
                    nextLowestBlockNumber = BitConverter.ToInt32(nextKey, keyPrefix.Length);
                }
                else
                {
                    nextLowestBlockNumber = null;
                }
                iterator.Prev();

                if ((currentLowestBlockNumber >= from && currentLowestBlockNumber <= to) || from >= currentLowestBlockNumber && from <= nextLowestBlockNumber)
                {
                    // Process the current index
                    var indexInfo = new IndexInfo(keyPrefix, iterator.Value());
                    SafeFileHandle fileHandle = indexInfo.IsTemp ? _tempFileHandle : _finalFileHandle;
                    var data = LoadData(fileHandle, indexInfo);
                    int[] blocks = indexInfo.IsTemp ? MemoryMarshal.Cast<byte, int>(data).ToArray() : Decompress(data);

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

                iterator.Next();
            }
            yield break;
        }


        public void SetReceipts(int blockNumber, TxReceipt[] receipts, bool isBackwardSync)
        {
            var indexes = new ConcurrentDictionary<byte[], IndexInfo>();

            try
            {
                foreach (var receipt in receipts)
                {
                    if (receipt is { Logs: not null })
                    {
                        foreach (var log in receipt.Logs)
                        {
                            byte[] key = log.LoggersAddress.Bytes;
                            if (!indexes.TryGetValue(key, out var indexInfo))
                            {
                                indexInfo = GetOrCreateTempIndex(key);
                                indexInfo.Lock();
                                indexes[key] = indexInfo;
                            }

                            // Check if the current block number is less than or equal to the last block number for the given key
                            if (blockNumber <= indexInfo.LastBlockNumber)
                            {
                                continue;
                            }

                            long position = indexInfo.Offset + indexInfo.Length * 4; // Calculate position for the next integer
                            RandomAccess.Write(_tempFileHandle, BitConverter.GetBytes(blockNumber), position);
                            indexInfo.Length++; // Increase the length
                            indexInfo.LastBlockNumber = blockNumber; // Update the last block number

                            if (indexInfo.IsReadyToFinalize())
                            {
                                var data = LoadData(_tempFileHandle, indexInfo);
                                var compressed = Compress(data);
                                long offset = Append(_finalFileStream, compressed);

                                // Use [key]+[lowestblocknuminindex] as the DB key
                                byte[] dbKey = CreateDbKey(indexInfo.Key, indexInfo.LowestBlockNumber(_tempFileHandle));
                                _addressDb.PutSpan(dbKey, CreateIndexValue(offset, compressed.Length, false, indexInfo.LastBlockNumber)); // store the count of integers

                                AddFreePage(indexInfo);
                                indexes.TryRemove(key, out _); // Remove from the concurrent dictionary
                            }
                        }
                    }
                }
            }
            finally
            {
                foreach (var indexInfo in indexes.Values)
                {
                    byte[] dbKey = CreateDbKey(indexInfo.Key, indexInfo.LowestBlockNumber(_tempFileHandle));
                    _addressDb.PutSpan(dbKey, CreateIndexValue(indexInfo.Offset, indexInfo.Length, indexInfo.IsTemp, indexInfo.LastBlockNumber));
                    indexInfo.Unlock();
                }
            }
        }

        private IndexInfo GetOrCreateTempIndex(byte[] key)
        {
            using var iterator = _addressDb.CreateIterator(true);
            iterator.Seek(key);

            IndexInfo? latestTempIndex = null;

            while (iterator.Valid() && iterator.Key().Take(key.Length).SequenceEqual(key))
            {
                var existingIndex = iterator.Value();
                if (IsTempIndex(existingIndex))
                {
                    latestTempIndex = new IndexInfo(key, existingIndex);
                }
                iterator.Next();
            }

            if (latestTempIndex != null)
            {
                return latestTempIndex;
            }

            int freePage = GetFreePage() ?? GrowFile(_tempFileStream, FixedLength);
            return new IndexInfo(key, freePage, 0, true, 0);
        }

        private void AddFreePage(IndexInfo indexInfo)
        {
            lock (_mainDb)
            {
                var freePages = GetFreePages();
                freePages.Add((int)indexInfo.Offset);
                _mainDb.PutSpan("freePages".ToBytes(), SerializeFreePages(freePages));
            }
        }

        private int? GetFreePage()
        {
            lock (_mainDb)
            {
                var freePages = GetFreePages();
                if (freePages.Count > 0)
                {
                    var page = freePages.Last();
                    freePages.RemoveAt(freePages.Count - 1);
                    _mainDb.PutSpan("freePages".ToBytes(), SerializeFreePages(freePages));
                    return page;
                }
                return null;
            }
        }

        private List<int> GetFreePages()
        {
            var data = _mainDb.Get("freePages".ToBytes());
            return data != null ? DeserializeFreePages(data) : new List<int>();
        }

        private List<int> DeserializeFreePages(byte[] data)
        {
            var result = new List<int>();
            for (int i = 0; i < data.Length; i += sizeof(int))
            {
                result.Add(BitConverter.ToInt32(data, i));
            }
            return result;
        }

        private byte[] SerializeFreePages(List<int> freePages)
        {
            var result = new ArrayPoolList<byte>(freePages.Count * sizeof(int));
            foreach (var page in freePages)
            {
                result.AddRange(BitConverter.GetBytes(page));
            }
            return result.ToArray();
        }

        private byte[] CreateDbKey(byte[] key, int blockNumber)
        {
            Span<byte> dbKey = stackalloc byte[key.Length + sizeof(int)];
            key.CopyTo(dbKey);
            BitConverter.TryWriteBytes(dbKey.Slice(key.Length), blockNumber);
            return dbKey.ToArray();
        }

        private bool CheckBlockNumber(byte[] key, int from, int to)
        {
            int blockNumber = BitConverter.ToInt32(key, 20);
            return blockNumber >= from && blockNumber <= to;
        }

        private bool IsTempIndex(byte[] indexInfo)
        {
            return indexInfo[0] == (byte)FileType.TEMP;
        }

        private byte[] LoadData(SafeFileHandle fileHandle, IndexInfo indexInfo)
        {
            int count = indexInfo.IsTemp ? indexInfo.Length * 4 : indexInfo.Length;
            var buffer = new byte[count];
            RandomAccess.Read(fileHandle, buffer, indexInfo.Offset);
            return buffer;
        }

        private int[] Decompress(byte[] data)
        {
            using var decompressedStream = new MemoryStream(data);
            using var gzipStream = new GZipStream(decompressedStream, CompressionMode.Decompress);
            using var resultStream = new MemoryStream();
            gzipStream.CopyTo(resultStream);
            resultStream.Position = 0;

            var decompressedData = new List<int>();
            var buffer = new byte[4];
            while (resultStream.Read(buffer, 0, 4) > 0)
            {
                decompressedData.Add(BitConverter.ToInt32(buffer, 0));
            }

            return decompressedData.ToArray();
        }

        private int BinarySearch(int[] blocks, int from)
        {
            int index = Array.BinarySearch(blocks, from);
            return index < 0 ? ~index : index;
        }

        private byte[] Compress(byte[] data)
        {
            using var compressedStream = new MemoryStream();
            using (var gzipStream = new GZipStream(compressedStream, CompressionMode.Compress))
            {
                gzipStream.Write(data, 0, data.Length);
            }
            return compressedStream.ToArray();
        }

        private long Append(FileStream fileStream, byte[] data)
        {
            long offset = fileStream.Length;
            fileStream.Seek(offset, SeekOrigin.Begin);
            fileStream.Write(data, 0, data.Length);
            return offset;
        }

        private byte[] CreateIndexValue(long offset, int count, bool isTemp, int lastBlockNumber)
        {
            Span<byte> value = stackalloc byte[1 + sizeof(long) + 2 * sizeof(int)];
            value[0] = (byte)(isTemp ? FileType.TEMP : FileType.FINAL);
            BitConverter.TryWriteBytes(value.Slice(1), offset);
            BitConverter.TryWriteBytes(value.Slice(1 + sizeof(long)), count);
            BitConverter.TryWriteBytes(value.Slice(1 + sizeof(long) + sizeof(int)), lastBlockNumber);
            return value.ToArray();
        }

        private int GrowFile(FileStream fileStream, int length)
        {
            fileStream.SetLength(fileStream.Length + length);
            return (int)(fileStream.Length - length);
        }

        private class IndexInfo
        {
            public byte[] Key { get; }
            public long Offset { get; set; }
            public bool IsTemp { get; set; }
            public int Length { get; set; } // Represents the count of integers
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

            public IndexInfo(byte[] key, byte[] value)
            {
                Key = key;
                IsTemp = value[0] == (byte)FileType.TEMP;
                Offset = BitConverter.ToInt64(value, 1);
                Length = BitConverter.ToInt32(value, 1 + sizeof(long));
                LastBlockNumber = BitConverter.ToInt32(value, 1 + sizeof(long) + sizeof(int));
            }

            public bool IsReadyToFinalize()
            {
                return Length >= FixedLength / 4; // Check if the index has reached the maximum size (number of integers)
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
                BitConverter.TryWriteBytes(value.Slice(1), Offset);
                BitConverter.TryWriteBytes(value.Slice(1 + sizeof(long)), Length);
                BitConverter.TryWriteBytes(value.Slice(1 + sizeof(long) + sizeof(int)), LastBlockNumber);
                return value.ToArray();
            }

            public int LowestBlockNumber(SafeFileHandle fileHandle)
            {
                Span<byte> buffer = stackalloc byte[4];
                RandomAccess.Read(fileHandle, buffer, Offset);
                return BitConverter.ToInt32(buffer);
            }
        }

        private enum FileType : byte
        {
            TEMP = 0x01,
            FINAL = 0x02
        }
    }
}
