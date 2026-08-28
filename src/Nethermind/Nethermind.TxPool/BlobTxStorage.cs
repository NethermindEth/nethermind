// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.TxPool;

public class BlobTxStorage(IColumnsDb<BlobTxsColumns> database) : IBlobTxStorage, IBlobTxMetadataStorage
{
    private const int MaxPooledKeys = 128;
    private const int TransactionLockCount = 64;

    // Sidecar-free records live in the full-txs column under a key shape (prefix + hash) that
    // cannot collide with the 64-byte timestamp-prefixed full-tx keys.
    private const int ElidedTxKeyLength = 33;
    private const byte ElidedTxKeyPrefix = 0x01;
    private static readonly TxDecoder _txDecoder = TxDecoder.Instance;
    private static readonly Lock[] _transactionLocks = CreateTransactionLocks();
    private readonly ConcurrentQueue<byte[]> _keyPool = new();
    private int _pooledKeyCount;
    private readonly IColumnsDb<BlobTxsColumns> _database = database;
    private readonly IDb _fullBlobTxsDb = database.GetColumnDb(BlobTxsColumns.FullBlobTxs);
    private readonly IDb _lightBlobTxsDb = database.GetColumnDb(BlobTxsColumns.LightBlobTxs);
    private readonly IDb _processedBlobTxsDb = database.GetColumnDb(BlobTxsColumns.ProcessedTxs);

    public BlobTxStorage() : this(new MemColumnsDb<BlobTxsColumns>()) { }

    public bool TryGet(in ValueHash256 hash, Address sender, in UInt256 timestamp, [NotNullWhen(true)] out Transaction? transaction)
    {
        Span<byte> txHashPrefixed = stackalloc byte[64];
        GetHashPrefixedByTimestamp(timestamp, hash, txHashPrefixed);

        byte[]? txBytes = _fullBlobTxsDb.Get(txHashPrefixed);
        return TryDecodeFullTx(txBytes, sender, timestamp, out transaction);
    }

    /// <inheritdoc/>
    public bool TryGetWithoutBlobs(in ValueHash256 hash, Address sender, [NotNullWhen(true)] out Transaction? transaction)
    {
        Span<byte> elidedKey = stackalloc byte[ElidedTxKeyLength];
        GetElidedTxKey(hash, elidedKey);

        byte[]? elidedBytes = _fullBlobTxsDb.Get(elidedKey);
        if (elidedBytes is not null)
        {
            transaction = Rlp.Decode<Transaction>(elidedBytes, RlpBehaviors.InMempoolForm);
            transaction.SenderAddress = sender;
            return true;
        }

        transaction = default;
        return false;
    }

    public int TryGetMany(TxLookupKey[] keys, int count, Transaction?[] results)
    {
        if (count == 0) return 0;

        // Outer array must be exact-size for the IDb indexer (uses keys.Length).
        // Inner byte[64] keys are pooled via ConcurrentQueue to avoid per-call allocations.
        byte[][] dbKeys = new byte[count][];
        int rentedKeyCount = 0;
        try
        {
            for (int i = 0; i < dbKeys.Length; i++)
            {
                byte[] key = RentKey();
                dbKeys[i] = key;
                rentedKeyCount++;
                GetHashPrefixedByTimestamp(keys[i].Timestamp, keys[i].Hash, key);
            }

            KeyValuePair<byte[], byte[]?>[] dbResults = _fullBlobTxsDb[dbKeys];

            int found = 0;
            for (int i = 0; i < count; i++)
            {
                if (TryDecodeFullTx(dbResults[i].Value, keys[i].Sender, keys[i].Timestamp, out results[i]))
                    found++;
            }

            return found;
        }
        finally
        {
            for (int i = 0; i < rentedKeyCount; i++)
                ReturnKey(dbKeys[i]);
        }
    }

    public IEnumerable<LightTransaction> GetAll()
    {
        foreach (byte[] txBytes in _lightBlobTxsDb.GetAllValues())
        {
            if (TryDecodeLightTx(txBytes, out LightTransaction? transaction))
            {
                yield return transaction!;
            }
        }
    }

    public void Add(Transaction transaction)
    {
        if (transaction?.Hash is null)
        {
            throw new ArgumentNullException(nameof(transaction));
        }

        ValueHash256 hash = transaction.Hash.ValueHash256;
        lock (GetTransactionLock(hash))
        {
            Span<byte> txHashPrefixed = stackalloc byte[64];
            GetHashPrefixedByTimestamp(transaction.Timestamp, hash, txHashPrefixed);

            EncodeAndSaveTx(transaction, _fullBlobTxsDb, txHashPrefixed);
            SaveWithoutBlobsIfMissing(transaction);
            _lightBlobTxsDb.Set(transaction.Hash, LightTxDecoder.Encode(transaction));
        }
    }

    /// <inheritdoc/>
    public void AddWithoutBlobs(Transaction transaction)
    {
        if (transaction?.Hash is null)
        {
            throw new ArgumentNullException(nameof(transaction));
        }

        ValueHash256 hash = transaction.Hash.ValueHash256;
        lock (GetTransactionLock(hash))
        {
            Span<byte> txHashPrefixed = stackalloc byte[64];
            GetHashPrefixedByTimestamp(transaction.Timestamp, hash, txHashPrefixed);
            if (!_fullBlobTxsDb.KeyExists(txHashPrefixed))
            {
                return;
            }

            SaveWithoutBlobsIfMissing(transaction);
        }
    }

    private void SaveWithoutBlobsIfMissing(Transaction transaction)
    {
        if (transaction.NetworkWrapper is not ShardBlobNetworkWrapper)
        {
            return;
        }

        Span<byte> elidedKey = stackalloc byte[ElidedTxKeyLength];
        GetElidedTxKey(transaction.Hash, elidedKey);
        if (_fullBlobTxsDb.KeyExists(elidedKey))
        {
            return;
        }

        using ArrayPoolSpan<byte> rlp = _txDecoder.EncodeToArrayPoolSpan(BlobTransactionPayload.Elide(transaction), RlpBehaviors.InMempoolForm);
        _fullBlobTxsDb.PutSpan(elidedKey, rlp);
    }

    public void Delete(in ValueHash256 hash, in UInt256 timestamp)
    {
        lock (GetTransactionLock(hash))
        {
            Span<byte> txHashPrefixed = stackalloc byte[64];
            GetHashPrefixedByTimestamp(timestamp, hash, txHashPrefixed);

            Span<byte> elidedKey = stackalloc byte[ElidedTxKeyLength];
            GetElidedTxKey(hash, elidedKey);

            using IColumnsWriteBatch<BlobTxsColumns> batch = _database.StartWriteBatch();
            IWriteBatch fullBlobTxsBatch = batch.GetColumnBatch(BlobTxsColumns.FullBlobTxs);
            fullBlobTxsBatch.Remove(txHashPrefixed);
            fullBlobTxsBatch.Remove(elidedKey);
            batch.GetColumnBatch(BlobTxsColumns.LightBlobTxs).Remove(hash.BytesAsSpan);
        }
    }

    public void AddBlobTransactionsFromBlock(ulong blockNumber, in ArrayPoolListRef<Transaction> blockBlobTransactions)
    {
        if (blockBlobTransactions.Count == 0)
        {
            return;
        }

        EncodeAndSaveTxs(blockBlobTransactions, _processedBlobTxsDb, blockNumber);
    }

    public bool TryGetBlobTransactionsFromBlock(ulong blockNumber, out Transaction[]? blockBlobTransactions)
    {
        byte[]? bytes = _processedBlobTxsDb.Get(blockNumber);

        if (bytes is not null)
        {
            RlpReader ctx = new(bytes);
            blockBlobTransactions = _txDecoder.DecodeArray(ref ctx, RlpBehaviors.InMempoolForm | RlpBehaviors.Storage);
            return true;
        }

        blockBlobTransactions = default;
        return false;
    }

    public void DeleteBlobTransactionsFromBlock(ulong blockNumber)
        => _processedBlobTxsDb.Delete(blockNumber);

    private static bool TryDecodeFullTx(
        byte[]? txBytes,
        Address sender,
        in UInt256 timestamp,
        out Transaction? transaction)
    {
        if (txBytes is not null)
        {
            transaction = Rlp.Decode<Transaction>(txBytes, RlpBehaviors.InMempoolForm | RlpBehaviors.Storage);
            transaction.SenderAddress = sender;
            transaction.Timestamp = timestamp;
            return true;
        }

        transaction = default;
        return false;
    }

    private static bool TryDecodeLightTx(byte[]? txBytes, out LightTransaction? lightTx)
    {
        if (txBytes is not null)
        {
            lightTx = LightTxDecoder.Decode(txBytes);
            return true;
        }

        lightTx = default;
        return false;
    }

    private byte[] RentKey()
    {
        if (_keyPool.TryDequeue(out byte[]? key))
        {
            Interlocked.Decrement(ref _pooledKeyCount);
            return key;
        }

        return new byte[64];
    }

    private void ReturnKey(byte[] key)
    {
        if (Interlocked.Increment(ref _pooledKeyCount) <= MaxPooledKeys)
        {
            _keyPool.Enqueue(key);
        }
        else
        {
            Interlocked.Decrement(ref _pooledKeyCount);
        }
    }

    private static void GetElidedTxKey(in ValueHash256 hash, scoped Span<byte> elidedKey)
    {
        elidedKey[0] = ElidedTxKeyPrefix;
        hash.Bytes.CopyTo(elidedKey[1..]);
    }

    private static void GetHashPrefixedByTimestamp(in UInt256 timestamp, in ValueHash256 hash, scoped Span<byte> txHashPrefixed)
    {
        timestamp.WriteBigEndian(txHashPrefixed);
        hash.Bytes.CopyTo(txHashPrefixed[32..]);
    }

    private static Lock GetTransactionLock(in ValueHash256 hash) =>
        _transactionLocks[(uint)hash.GetHashCode() % TransactionLockCount];

    private static Lock[] CreateTransactionLocks()
    {
        Lock[] locks = new Lock[TransactionLockCount];
        for (int i = 0; i < locks.Length; i++)
        {
            locks[i] = new Lock();
        }

        return locks;
    }

    private void EncodeAndSaveTx(Transaction transaction, IDb db, scoped Span<byte> txHashPrefixed)
    {
        using ArrayPoolSpan<byte> rlp = _txDecoder.EncodeToArrayPoolSpan(transaction, RlpBehaviors.InMempoolForm | RlpBehaviors.Storage);
        db.PutSpan(txHashPrefixed, rlp);
    }

    private void EncodeAndSaveTxs(in ArrayPoolListRef<Transaction> blockBlobTransactions, IDb db, ulong blockNumber)
    {
        using ArrayPoolSpan<byte> rlp = _txDecoder.EncodeToArrayPoolSpan(blockBlobTransactions.AsSpan(), RlpBehaviors.InMempoolForm | RlpBehaviors.Storage);
        db.PutSpan(blockNumber.ToBigEndianSpanWithoutLeadingZeros(out _), rlp);
    }
}

internal static class UInt256Extensions
{
    public static void WriteBigEndian(in this UInt256 value, scoped Span<byte> output)
    {
        BinaryPrimitives.WriteUInt64BigEndian(output[..8], value.u3);
        BinaryPrimitives.WriteUInt64BigEndian(output.Slice(8, 8), value.u2);
        BinaryPrimitives.WriteUInt64BigEndian(output.Slice(16, 8), value.u1);
        BinaryPrimitives.WriteUInt64BigEndian(output.Slice(24, 8), value.u0);
    }
}
