// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Threading.Tasks;
using Microsoft.IO;
using Microsoft.Win32.SafeHandles;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Resettables;
using Nethermind.Core.Specs;
using Nethermind.Core.Threading;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

using Snappier;
#pragma warning disable 618

namespace Nethermind.Blockchain.Receipts
{
    public class PersistentReceiptStorage : IReceiptStorage
    {
        private string? _basePath;
        private readonly IColumnsDb<ReceiptsColumns> _database;
        private readonly ISpecProvider _specProvider;
        private readonly IReceiptsRecovery _receiptsRecovery;
        private long? _lowestInsertedReceiptBlock;
        private readonly IDb _blocksDb;
        private readonly IDb _defaultColumn;
        private readonly IDb _transactionDb;
        private static readonly Hash256 MigrationBlockNumberKey = Keccak.Compute(nameof(MigratedBlockNumber));
        private long _migratedBlockNumber;
        private readonly ReceiptArrayStorageDecoder _storageDecoder = ReceiptArrayStorageDecoder.Instance;
        private readonly IBlockTree _blockTree;
        private readonly IBlockStore _blockStore;
        private readonly IReceiptConfig _receiptConfig;
        private readonly bool _legacyHashKey;

        private const int CacheSize = 64;
        private const int BlocksPerEra = 8192;
        private const int FileSplit = 8;
        private const int BlocksPerFile = BlocksPerEra / FileSplit;
        private readonly LruCache<ValueHash256, TxReceipt[]> _receiptsCache = new(CacheSize, CacheSize, "receipts");

        public event EventHandler<BlockReplacementEventArgs> ReceiptsInserted;

        public PersistentReceiptStorage(
            IColumnsDb<ReceiptsColumns> receiptsDb,
            ISpecProvider specProvider,
            IReceiptsRecovery receiptsRecovery,
            IBlockTree blockTree,
            IBlockStore blockStore,
            IReceiptConfig receiptConfig,
            ReceiptArrayStorageDecoder? storageDecoder = null
        )
        {
            _database = receiptsDb ?? throw new ArgumentNullException(nameof(receiptsDb));
            _defaultColumn = _database.GetColumnDb(ReceiptsColumns.Default);
            long Get(Hash256 key, long defaultValue) => _defaultColumn.Get(key)?.ToLongFromBigEndianByteArrayWithoutLeadingZeros() ?? defaultValue;

            _specProvider = specProvider ?? throw new ArgumentNullException(nameof(specProvider));
            _receiptsRecovery = receiptsRecovery ?? throw new ArgumentNullException(nameof(receiptsRecovery));
            _blocksDb = _database.GetColumnDb(ReceiptsColumns.Blocks);
            _transactionDb = _database.GetColumnDb(ReceiptsColumns.Transactions);
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _blockStore = blockStore ?? throw new ArgumentNullException(nameof(blockStore));
            _storageDecoder = storageDecoder ?? ReceiptArrayStorageDecoder.Instance;
            _receiptConfig = receiptConfig ?? throw new ArgumentNullException(nameof(receiptConfig));

            byte[] lowestBytes = _defaultColumn.Get(Keccak.Zero);
            _lowestInsertedReceiptBlock = lowestBytes is null ? (long?)null : new RlpStream(lowestBytes).DecodeLong();
            _migratedBlockNumber = Get(MigrationBlockNumberKey, long.MaxValue);

            KeyValuePair<byte[], byte[]>? firstValue = _blocksDb.GetAll().FirstOrDefault();
            _legacyHashKey = firstValue.HasValue && firstValue.Value.Key is not null && firstValue.Value.Key.Length == Hash256.Size;

            _blockTree.BlockAddedToMain += BlockTreeOnBlockAddedToMain;

            var basePath = _defaultColumn.DbPath;
            if (basePath is not null)
            {
                _basePath = Path.Combine(basePath, "receiptfiles");
            }
        }

        private void BlockTreeOnBlockAddedToMain(object? sender, BlockReplacementEventArgs e)
        {
            if (e.PreviousBlock is not null)
            {
                RemoveBlockTx(e.PreviousBlock, e.Block);
            }
            EnsureCanonical(e.Block);
            ReceiptsInserted?.Invoke(this, e);

            // Dont block main loop
            Task.Run(() =>
            {

                Block newMain = e.Block;

                // Delete old tx index
                if (_receiptConfig.TxLookupLimit > 0 && newMain.Number > _receiptConfig.TxLookupLimit.Value)
                {
                    Block newOldTx = _blockTree.FindBlock(newMain.Number - _receiptConfig.TxLookupLimit.Value);
                    if (newOldTx is not null)
                    {
                        RemoveBlockTx(newOldTx);
                    }
                }
            });
        }

        public Hash256 FindBlockHash(Hash256 txHash)
        {
            var blockHashData = _transactionDb.Get(txHash);
            if (blockHashData is null) return FindReceiptObsolete(txHash)?.BlockHash;

            if (blockHashData.Length == Hash256.Size) return new Hash256(blockHashData);

            long blockNum = new RlpStream(blockHashData).DecodeLong();
            return _blockTree.FindBlockHash(blockNum);
        }

        // Find receipt stored with old - obsolete format.
        private TxReceipt FindReceiptObsolete(Hash256 hash)
        {
            var receiptData = _defaultColumn.GetSpan(hash);
            try
            {
                return DeserializeReceiptObsolete(hash, receiptData);
            }
            finally
            {
                _defaultColumn.DangerousReleaseMemory(receiptData);
            }
        }

        private TxReceipt DeserializeReceiptObsolete(Hash256 hash, Span<byte> receiptData)
        {
            if (!receiptData.IsNullOrEmpty())
            {
                return _storageDecoder.DeserializeReceiptObsolete(hash, receiptData);
            }

            return null;
        }

        public TxReceipt[] Get(Block block, bool recover = true)
        {
            if (block.ReceiptsRoot == Keccak.EmptyTreeHash)
            {
                return Array.Empty<TxReceipt>();
            }

            Hash256 blockHash = block.Hash;
            if (_receiptsCache.TryGet(blockHash, out TxReceipt[]? receipts))
            {
                return receipts ?? Array.Empty<TxReceipt>();
            }

            Span<byte> receiptsData = GetReceiptData(block.Number, blockHash);
            if (receiptsData.IsNullOrEmpty())
            {
                receipts = _storageDecoder.Decode(in receiptsData);

                if (recover)
                {
                    _receiptsRecovery.TryRecover(block, receipts);
                    _receiptsCache.Set(blockHash, receipts);
                }

                return receipts;
            }

            receiptsData = GetReceiptDataDb(block.Number, blockHash);

            try
            {
                if (receiptsData.IsNullOrEmpty())
                {
                    return Array.Empty<TxReceipt>();
                }
                else
                {
                    receipts = _storageDecoder.Decode(in receiptsData);

                    if (recover)
                    {
                        _receiptsRecovery.TryRecover(block, receipts);
                        _receiptsCache.Set(blockHash, receipts);
                    }

                    return receipts;
                }
            }
            finally
            {
                _blocksDb.DangerousReleaseMemory(receiptsData);
            }
        }

        [SkipLocalsInit]
        private unsafe Span<byte> GetReceiptData(long blockNumber, Hash256 blockHash)
        {
            string? path = null;
            string? filename = null;
            if (_basePath is not null)
            {
                (string directory, filename, _) = GetReceiptsDirectoryAndPath(blockNumber, _basePath);
                path = Path.Combine(directory, filename);
            }
            if (path is not null && File.Exists(path))
            {
                using FileStream file = new(path, mode: FileMode.Open, access: FileAccess.Read, share: FileShare.ReadWrite);
                var fileHandle = file.SafeFileHandle;
                long headerOffset = (blockNumber / FileSplit) % BlocksPerFile;

                Vector128<long> headerEntry = default;
                Span<byte> headerData = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref headerEntry, 1));
                long read = RandomAccess.Read(fileHandle, headerData, Vector128<byte>.Count * headerOffset);
                if (read == Vector128<byte>.Count && headerEntry != default)
                {
                    long offset = headerEntry.GetElement(0);
                    int length = (int)headerEntry.GetElement(1);

                    var array = ArrayPool<byte>.Shared.Rent(length);
                    try
                    {
                        var input = array.AsSpan(0, length);
                        read = RandomAccess.Read(fileHandle, input, offset);
                        if (read == length)
                        {
                            using RecyclableMemoryStream output = RecyclableStream.GetStream(filename);
                            using (SnappyStream decompressor = new(new MemoryStream(array, 0, length), CompressionMode.Decompress))
                            {
                                decompressor.CopyTo(output);
                            }

                            ArrayPool<byte>.Shared.Return(array);
                            array = null;

                            return output.ToArray();
                        }
                    }
                    finally
                    {
                        if (array is not null) ArrayPool<byte>.Shared.Return(array);
                    }
                }
            }

            return default;
        }

        [SkipLocalsInit]
        private unsafe Span<byte> GetReceiptDataDb(long blockNumber, Hash256 blockHash)
        {
            Span<byte> blockNumPrefixed = stackalloc byte[40];
            if (_legacyHashKey)
            {
                Span<byte> receiptsData = _blocksDb.GetSpan(blockHash);
                if (!receiptsData.IsNull())
                {
                    return receiptsData;
                }

                GetBlockNumPrefixedKey(blockNumber, blockHash, blockNumPrefixed);

#pragma warning disable CS9080
                receiptsData = _blocksDb.GetSpan(blockNumPrefixed);
#pragma warning restore CS9080

                return receiptsData;
            }
            else
            {
                GetBlockNumPrefixedKey(blockNumber, blockHash, blockNumPrefixed);

                Span<byte> receiptsData = _blocksDb.GetSpan(blockNumPrefixed);
                if (receiptsData.IsNull())
                {
                    receiptsData = _blocksDb.GetSpan(blockHash);
                }

#pragma warning disable CS9080
                return receiptsData;
#pragma warning restore CS9080
            }
        }

        private static void GetBlockNumPrefixedKey(long blockNumber, Hash256 blockHash, Span<byte> output)
        {
            blockNumber.WriteBigEndian(output);
            blockHash!.Bytes.CopyTo(output[8..]);
        }

        public TxReceipt[] Get(Hash256 blockHash, bool recover = true)
        {
            Block? block = _blockTree.FindBlock(blockHash);
            if (block is null) return Array.Empty<TxReceipt>();
            return Get(block, recover);
        }

        public bool CanGetReceiptsByHash(long blockNumber) => blockNumber >= MigratedBlockNumber;

        public bool TryGetReceiptsIterator(long blockNumber, Hash256 blockHash, out ReceiptsIterator iterator)
        {
            if (_receiptsCache.TryGet(blockHash, out var receipts))
            {
                iterator = new ReceiptsIterator(receipts);
                return true;
            }

            var result = CanGetReceiptsByHash(blockNumber);
            Span<byte> receiptsData = GetReceiptData(blockNumber, blockHash);

            Func<IReceiptsRecovery.IRecoveryContext?> recoveryContextFactory = () => null;

            if (ReceiptArrayStorageDecoder.IsCompactEncoding(receiptsData))
            {
                recoveryContextFactory = () =>
                {
                    ReceiptRecoveryBlock? block = _blockStore.GetReceiptRecoveryBlock(blockNumber, blockHash);

                    if (!block.HasValue)
                    {
                        throw new InvalidOperationException($"Unable to recover receipts for block {blockHash} because of missing block data.");
                    }

                    return _receiptsRecovery.CreateRecoveryContext(block.Value);
                };
            }

            IReceiptRefDecoder refDecoder = ReceiptArrayStorageDecoder.GetRefDecoder(receiptsData);

            iterator = result ? new ReceiptsIterator(receiptsData, _blocksDb, recoveryContextFactory, refDecoder) : new ReceiptsIterator();
            return result;
        }

        private readonly ConcurrentDictionary<int, McsLock> _locks = new();

        private static (string directory, string filename, int lockId) GetReceiptsDirectoryAndPath(long blockNumber, string basePath)
        {
            var era = (int)(blockNumber / BlocksPerEra);
            long eraGroup = era / 100;
            int blockDigit = (int)(blockNumber % FileSplit);

            string directory = Path.Combine(basePath, eraGroup.ToString("D3"));
            string path = $"{era:D5}-{blockDigit:D1}.sz";

            return (directory, path, era * FileSplit + blockDigit);
        }

        [SkipLocalsInit]
        public void Insert(Block block, TxReceipt[]? txReceipts, bool ensureCanonical = true)
        {
            txReceipts ??= Array.Empty<TxReceipt>();
            int txReceiptsLength = txReceipts.Length;

            if (block.Transactions.Length != txReceiptsLength)
            {
                throw new InvalidDataException(
                    $"Block {block.ToString(Block.Format.FullHashAndNumber)} has different numbers " +
                    $"of transactions {block.Transactions.Length} and receipts {txReceipts.Length}.");
            }

            _receiptsRecovery.TryRecover(block, txReceipts, false);
            _receiptsCache.Set(block.Hash, txReceipts);

            IReleaseSpec spec = _specProvider.GetSpec(block.Header);
            RlpBehaviors behaviors = spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts | RlpBehaviors.Storage : RlpBehaviors.Storage;

            if (_basePath is not null)
            {
                SaveToDisk(block, txReceipts, behaviors);
            }
            else
            {
                SaveToDb(block, txReceipts, behaviors);
            }

            long blockNumber = block.Number;
            if (blockNumber < MigratedBlockNumber)
            {
                MigratedBlockNumber = blockNumber;
            }

            if (ensureCanonical)
            {
                EnsureCanonical(block);
            }
        }
        
        private void SaveToDb(Block block, TxReceipt[]? txReceipts, RlpBehaviors behaviors)
        {
            using NettyRlpStream stream = _storageDecoder.EncodeToNewNettyStream(txReceipts, behaviors);

            Span<byte> blockNumPrefixed = stackalloc byte[40];
            GetBlockNumPrefixedKey(block.Number, block.Hash!, blockNumPrefixed);

            _blocksDb.PutSpan(blockNumPrefixed, stream.AsSpan());
        }

        private void SaveToDisk(Block block, TxReceipt[]? txReceipts, RlpBehaviors behaviors)
        {
            (string directory, string filename, int lockId) = GetReceiptsDirectoryAndPath(block.Number, _basePath);
            using RecyclableMemoryStream output = RecyclableStream.GetStream(filename);
            using (RecyclableRlpStream newRlp = _storageDecoder.EncodeToNewRecyclableRlpStream(txReceipts, filename, behaviors))
            {
                using SnappyStream compressor = new(output, CompressionMode.Compress, leaveOpen: true);
                newRlp.CopyTo(compressor);
            }

            output.Position = 0;

            string path = Path.Combine(directory, filename);

            bool newLock = false;
            using var handle = _locks.GetOrAdd(lockId, _ =>
            {
                newLock = true;
                return new McsLock();
            }).Acquire();

            if (newLock)
            {
                Directory.CreateDirectory(directory);
            }

            using FileStream file = new(path, mode: FileMode.OpenOrCreate, access: FileAccess.Write, share: FileShare.Read);
            SafeFileHandle fileHandle = file.SafeFileHandle;
            long fileOffset = Math.Max(RandomAccess.GetLength(fileHandle), Vector128<byte>.Count * BlocksPerFile);

            long outputLength = output.Length;
            if (output.TryGetBuffer(out ArraySegment<byte> buffer))
            {
                ReadOnlySpan<byte> outputData = buffer.Array.AsSpan(buffer.Offset, (int)outputLength);
                RandomAccess.Write(fileHandle, outputData, fileOffset);
            }
            else
            {
                WriteReadOnlySequence(fileHandle, output, fileOffset);
            }

            long headerOffset = (block.Number / FileSplit) % BlocksPerFile;
            Vector128<long> headerEntry = Vector128.Create(fileOffset, outputLength);
            ReadOnlySpan<byte> headerData = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref headerEntry, 1));
            RandomAccess.Write(fileHandle, headerData, Vector128<byte>.Count * headerOffset);

            static void WriteReadOnlySequence(SafeFileHandle fileHandle, RecyclableMemoryStream output, long fileOffset)
            {
                ReadOnlySequence<byte> sequence = output.GetReadOnlySequence();
                foreach (ReadOnlyMemory<byte> memory in sequence)
                {
                    ReadOnlySpan<byte> outputData = memory.Span;
                    RandomAccess.Write(fileHandle, outputData, fileOffset);
                    fileOffset += outputData.Length;
                }
            }
        }

        public long? LowestInsertedReceiptBlockNumber
        {
            get => _lowestInsertedReceiptBlock;
            set
            {
                _lowestInsertedReceiptBlock = value;
                if (value.HasValue)
                {
                    _defaultColumn.Set(Keccak.Zero, Rlp.Encode(value.Value).Bytes);
                }
            }
        }

        public long MigratedBlockNumber
        {
            get => _migratedBlockNumber;
            set
            {
                _migratedBlockNumber = value;
                _defaultColumn.Set(MigrationBlockNumberKey, MigratedBlockNumber.ToBigEndianByteArrayWithoutLeadingZeros());
            }
        }

        internal void ClearCache()
        {
            _receiptsCache.Clear();
        }

        [SkipLocalsInit]
        public bool HasBlock(long blockNumber, Hash256 blockHash)
        {
            if (_receiptsCache.Contains(blockHash)) return true;

            bool hasFilesystem = HasBlockOnFilesystem(blockNumber);
            if (hasFilesystem) return true;

            Span<byte> blockNumPrefixed = stackalloc byte[40];
            if (_legacyHashKey)
            {
                if (_blocksDb.KeyExists(blockHash)) return true;

                GetBlockNumPrefixedKey(blockNumber, blockHash, blockNumPrefixed);
                return _blocksDb.KeyExists(blockNumPrefixed);
            }
            else
            {
                GetBlockNumPrefixedKey(blockNumber, blockHash, blockNumPrefixed);
                return _blocksDb.KeyExists(blockNumPrefixed) || _blocksDb.KeyExists(blockHash);
            }
        }

        private bool HasBlockOnFilesystem(long blockNumber)
        {
            (string directory, string filename, _) = GetReceiptsDirectoryAndPath(blockNumber, _basePath);
            var path = Path.Combine(directory, filename);
            if (File.Exists(path))
            {
                using FileStream file = new(path, mode: FileMode.Open, access: FileAccess.Read, share: FileShare.ReadWrite);
                var fileHandle = file.SafeFileHandle;
                long headerOffset = (blockNumber / FileSplit) % BlocksPerFile;

                Vector128<long> headerEntry = default;
                var headerData = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref headerEntry, 1));
                long read = RandomAccess.Read(fileHandle, headerData, Vector128<long>.Count * headerOffset);
                if (read == Vector128<byte>.Count && headerEntry != default)
                {
                    return true;
                }
            }

            return false;
        }

        public void EnsureCanonical(Block block)
        {
            using IWriteBatch writeBatch = _transactionDb.StartWriteBatch();

            long headNumber = _blockTree.FindBestSuggestedHeader()?.Number ?? 0;

            if (_receiptConfig.TxLookupLimit == -1) return;
            if (_receiptConfig.TxLookupLimit != 0 && block.Number <= headNumber - _receiptConfig.TxLookupLimit) return;
            if (_receiptConfig.CompactTxIndex)
            {
                byte[] blockNumber = Rlp.Encode(block.Number).Bytes;
                foreach (Transaction tx in block.Transactions)
                {
                    Hash256 hash = (tx.Hash ??= tx.CalculateHash());
                    writeBatch[hash.Bytes] = blockNumber;
                }
            }
            else
            {
                byte[] blockHash = block.Hash.BytesToArray();
                foreach (Transaction tx in block.Transactions)
                {
                    Hash256 hash = (tx.Hash ??= tx.CalculateHash());
                    writeBatch[hash.Bytes] = blockHash;
                }
            }
        }

        private void RemoveBlockTx(Block block, Block? exceptBlock = null)
        {
            HashSet<Hash256AsKey> newTxs = null;
            if (exceptBlock is not null)
            {
                newTxs = new HashSet<Hash256AsKey>(exceptBlock.Transactions.Select((tx) => new Hash256AsKey(tx.Hash)));
            }

            using IWriteBatch writeBatch = _transactionDb.StartWriteBatch();
            foreach (Transaction tx in block.Transactions)
            {
                // If the tx is contained in another block, don't remove it. Used for reorg where the same tx
                // is contained in the new block
                if (newTxs?.Contains(tx.Hash) == true) continue;
                writeBatch[tx.Hash.Bytes] = null;
            }
        }
    }
}
