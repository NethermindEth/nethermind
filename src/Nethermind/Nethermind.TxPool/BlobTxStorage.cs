// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.TxPool;

public class BlobTxStorage(IColumnsDb<BlobTxsColumns> database, ILogManager? logManager = null) : IBlobTxStorage, IBlobTxMetadataStorage, ISpecChangeValidationStorage, IAtomicBlobTxStorage
{
    private const int MaxPooledKeys = 128;
    private const int TransactionLockCount = 64;
    private const int ProcessedTransactionKeyLength = sizeof(ulong) + sizeof(int);
    private const int ProcessedBlockIndexLength = 1 + sizeof(int);

    // Sidecar-free records live in the full-txs column under a key shape (prefix + hash) that
    // cannot collide with the 64-byte timestamp-prefixed full-tx keys.
    private const int FullTxKeyLength = 64;
    private const int ElidedTxKeyLength = 33;
    private const byte ElidedTxKeyPrefix = 0x01;
    private static readonly TxDecoder _txDecoder = TxDecoder.Instance;
    private static ReadOnlySpan<byte> SpecChangeValidationMarkerKey => "spec-change-validation"u8;
    private static readonly Lock[] _transactionLocks = CreateTransactionLocks();
    private readonly ConcurrentQueue<byte[]> _keyPool = new();
    private readonly Lock _processedTransactionsLock = new();
    private int _pooledKeyCount;
    private readonly IColumnsDb<BlobTxsColumns> _database = database;
    private readonly IDb _fullBlobTxsDb = database.GetColumnDb(BlobTxsColumns.FullBlobTxs);
    private readonly IDb _lightBlobTxsDb = database.GetColumnDb(BlobTxsColumns.LightBlobTxs);
    private readonly IDb _processedBlobTxsDb = database.GetColumnDb(BlobTxsColumns.ProcessedTxs);
    private readonly ILogger _logger = (logManager ?? LimboLogs.Instance).GetClassLogger<BlobTxStorage>();

    public BlobTxStorage() : this(new MemColumnsDb<BlobTxsColumns>()) { }

    public bool TryGet(in ValueHash256 hash, Address sender, in UInt256 timestamp, [NotNullWhen(true)] out Transaction? transaction)
    {
        Span<byte> txHashPrefixed = stackalloc byte[FullTxKeyLength];
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
            if (transaction is null)
            {
                return false;
            }

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
        // Inner full-transaction keys are pooled via ConcurrentQueue to avoid per-call allocations.
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

    /// <summary>Enumerates all stored light blob transactions.</summary>
    /// <remarks>
    /// Undecodable records are skipped so that a single corrupt entry cannot abort restoration of the whole pool at
    /// startup. Both counts and the first failure are reported once when enumeration ends, so that losing one record
    /// and losing every record — which a layout change does — stay distinguishable without logging per record.
    /// </remarks>
    public IEnumerable<LightTransaction> GetAll()
    {
        int skipped = 0;
        int restored = 0;
        string? firstFailure = null;

        try
        {
            foreach (byte[] txBytes in _lightBlobTxsDb.GetAllValues())
            {
                LightTransaction transaction;
                try
                {
                    transaction = LightTxDecoder.Decode(txBytes);
                }
                // A truncated record surfaces from RlpReader's unchecked Span.Slice, not as an RlpException, so the
                // filter spans every root a corrupt record is known to decode into.
                catch (Exception e) when (e is RlpException or ArgumentOutOfRangeException or IndexOutOfRangeException)
                {
                    skipped++;
                    firstFailure ??= $"{e.GetType().Name}: {e.Message}";
                    continue;
                }

                restored++;
                yield return transaction;
            }
        }
        finally
        {
            if (skipped > 0 && _logger.IsWarn)
            {
                _logger.Warn($"Skipped {skipped} of {skipped + restored} blob transaction record(s) as unreadable while restoring the blob transaction pool. First failure: {firstFailure}");
            }
        }
    }

    public void Add(Transaction transaction)
        => Replace(transaction, []);

    void IAtomicBlobTxStorage.Replace(Transaction transaction, scoped ReadOnlySpan<UInt256> obsoleteTimestamps)
        => Replace(transaction, obsoleteTimestamps);

    private void Replace(Transaction transaction, scoped ReadOnlySpan<UInt256> obsoleteTimestamps)
    {
        if (transaction?.Hash is null)
        {
            throw new ArgumentNullException(nameof(transaction));
        }

        ValueHash256 hash = transaction.Hash.ValueHash256;
        lock (GetTransactionLock(hash))
        {
            using ArrayPoolSpan<byte> fullRlp = _txDecoder.EncodeToArrayPoolSpan(
                transaction,
                RlpBehaviors.InMempoolForm | RlpBehaviors.Storage);
            byte[] lightRlp = LightTxDecoder.Encode(transaction);

            Span<byte> elidedKey = stackalloc byte[ElidedTxKeyLength];
            if (ShouldWriteElided(transaction, elidedKey))
            {
                using ArrayPoolSpan<byte> elidedRlp = _txDecoder.EncodeToArrayPoolSpan(
                    BlobTransactionPayload.Elide(transaction),
                    RlpBehaviors.InMempoolForm);
                WriteTransaction(transaction, obsoleteTimestamps, fullRlp, lightRlp, elidedKey, elidedRlp);
            }
            else
            {
                WriteTransaction(transaction, obsoleteTimestamps, fullRlp, lightRlp, elidedKey, []);
            }
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
            Span<byte> txHashPrefixed = stackalloc byte[FullTxKeyLength];
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
        Span<byte> elidedKey = stackalloc byte[ElidedTxKeyLength];
        if (!ShouldWriteElided(transaction, elidedKey))
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
            Span<byte> txHashPrefixed = stackalloc byte[FullTxKeyLength];
            GetHashPrefixedByTimestamp(timestamp, hash, txHashPrefixed);

            Span<byte> elidedKey = stackalloc byte[ElidedTxKeyLength];
            GetElidedTxKey(hash, elidedKey);

            using IColumnsWriteBatch<BlobTxsColumns> batch = _database.StartWriteBatch();
            try
            {
                IWriteBatch fullBlobTxsBatch = batch.GetColumnBatch(BlobTxsColumns.FullBlobTxs);
                fullBlobTxsBatch.Remove(txHashPrefixed);
                fullBlobTxsBatch.Remove(elidedKey);
                batch.GetColumnBatch(BlobTxsColumns.LightBlobTxs).Remove(hash.BytesAsSpan);
            }
            catch
            {
                batch.Clear();
                throw;
            }
        }
    }

    void IAtomicBlobTxStorage.DeleteMany(scoped ReadOnlySpan<BlobTxDeleteKey> keys) =>
        DeleteMany(keys);

    private void WriteTransaction(
        Transaction transaction,
        scoped ReadOnlySpan<UInt256> obsoleteTimestamps,
        scoped ReadOnlySpan<byte> fullRlp,
        byte[] lightRlp,
        scoped ReadOnlySpan<byte> elidedKey,
        scoped ReadOnlySpan<byte> elidedRlp)
    {
        ValueHash256 hash = transaction.Hash!.ValueHash256;
        using IColumnsWriteBatch<BlobTxsColumns> batch = _database.StartWriteBatch();
        try
        {
            IWriteBatch fullBlobTxsBatch = batch.GetColumnBatch(BlobTxsColumns.FullBlobTxs);
            Span<byte> txHashPrefixed = stackalloc byte[FullTxKeyLength];
            // Obsolete timestamps can include the current one; remove first so the ordered batch's final put preserves it.
            for (int i = 0; i < obsoleteTimestamps.Length; i++)
            {
                GetHashPrefixedByTimestamp(obsoleteTimestamps[i], hash, txHashPrefixed);
                fullBlobTxsBatch.Remove(txHashPrefixed);
            }

            GetHashPrefixedByTimestamp(transaction.Timestamp, hash, txHashPrefixed);
            fullBlobTxsBatch.PutSpan(txHashPrefixed, fullRlp);
            if (!elidedRlp.IsEmpty)
            {
                fullBlobTxsBatch.PutSpan(elidedKey, elidedRlp);
            }

            batch.GetColumnBatch(BlobTxsColumns.LightBlobTxs).Set(hash.BytesAsSpan, lightRlp);
        }
        catch
        {
            batch.Clear();
            throw;
        }
    }

    private void DeleteMany(scoped ReadOnlySpan<BlobTxDeleteKey> keys)
    {
        if (keys.IsEmpty)
        {
            return;
        }

        Span<bool> requiredLocks = stackalloc bool[TransactionLockCount];
        for (int i = 0; i < keys.Length; i++)
        {
            requiredLocks[GetTransactionLockIndex(keys[i].Hash)] = true;
        }

        Span<int> acquiredLocks = stackalloc int[TransactionLockCount];
        int acquiredLockCount = 0;
        try
        {
            for (int i = 0; i < requiredLocks.Length; i++)
            {
                if (requiredLocks[i])
                {
                    _transactionLocks[i].Enter();
                    acquiredLocks[acquiredLockCount++] = i;
                }
            }

            DeleteManyFromStorage(keys);
        }
        finally
        {
            for (int i = acquiredLockCount - 1; i >= 0; i--)
            {
                _transactionLocks[acquiredLocks[i]].Exit();
            }
        }
    }

    private void DeleteManyFromStorage(scoped ReadOnlySpan<BlobTxDeleteKey> keys)
    {
        if (keys.IsEmpty)
        {
            return;
        }

        using IColumnsWriteBatch<BlobTxsColumns> batch = _database.StartWriteBatch();
        try
        {
            IWriteBatch fullBlobTxsBatch = batch.GetColumnBatch(BlobTxsColumns.FullBlobTxs);
            IWriteBatch lightBlobTxsBatch = batch.GetColumnBatch(BlobTxsColumns.LightBlobTxs);
            Span<byte> txHashPrefixed = stackalloc byte[FullTxKeyLength];
            Span<byte> elidedKey = stackalloc byte[ElidedTxKeyLength];
            for (int i = 0; i < keys.Length; i++)
            {
                ref readonly BlobTxDeleteKey key = ref keys[i];
                GetHashPrefixedByTimestamp(key.Timestamp, key.Hash, txHashPrefixed);
                fullBlobTxsBatch.Remove(txHashPrefixed);
                GetElidedTxKey(key.Hash, elidedKey);
                fullBlobTxsBatch.Remove(elidedKey);
                lightBlobTxsBatch.Remove(key.Hash.BytesAsSpan);
            }
        }
        catch
        {
            batch.Clear();
            throw;
        }
    }

    string? ISpecChangeValidationStorage.GetSpecChangeValidationMarker()
    {
        byte[]? marker = _fullBlobTxsDb.Get(SpecChangeValidationMarkerKey);
        return marker is null ? null : Encoding.UTF8.GetString(marker);
    }

    void ISpecChangeValidationStorage.SetSpecChangeValidationMarker(string? marker)
    {
        if (marker is null)
        {
            _fullBlobTxsDb.Remove(SpecChangeValidationMarkerKey);
        }
        else
        {
            _fullBlobTxsDb.Set(SpecChangeValidationMarkerKey, Encoding.UTF8.GetBytes(marker));
        }
    }

    [SkipLocalsInit]
    public void AddBlobTransactionsFromBlock(ulong blockNumber, in ArrayPoolListRef<Transaction> blockBlobTransactions)
    {
        if (blockBlobTransactions.Count == 0)
        {
            return;
        }

        using Lock.Scope locked = _processedTransactionsLock.EnterScope();
        using IColumnsWriteBatch<BlobTxsColumns> batch = _database.StartWriteBatch();
        try
        {
            IWriteBatch processed = batch.GetColumnBatch(BlobTxsColumns.ProcessedTxs);
            int previousCount = GetProcessedTransactionCount(blockNumber);
            Unsafe.SkipInit(out Vector128<byte> keyStorage);
            Span<byte> key = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref keyStorage, 1))[..ProcessedTransactionKeyLength];
            BinaryPrimitives.WriteUInt64BigEndian(key, blockNumber);
            SaveProcessedTransactions(processed, key, blockBlobTransactions.AsSpan());

            RemoveProcessedTransactionRecords(processed, key, blockBlobTransactions.Count, previousCount);
            // Zero cannot start the legacy RLP list. Payload keys are longer than any legacy block-number key.
            Unsafe.SkipInit(out ulong indexStorage);
            Span<byte> index = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref indexStorage, 1))[..ProcessedBlockIndexLength];
            index[0] = 0;
            BinaryPrimitives.WriteInt32BigEndian(index[1..], blockBlobTransactions.Count);
            processed.PutSpan(blockNumber.ToBigEndianSpanWithoutLeadingZeros(out _), index);
        }
        catch
        {
            batch.Clear();
            throw;
        }
    }

    [SkipLocalsInit]
    public bool TryGetBlobTransactionsFromBlock(ulong blockNumber, [NotNullWhen(true)] out Transaction[]? blockBlobTransactions)
    {
        using Lock.Scope locked = _processedTransactionsLock.EnterScope();
        using MemoryManager<byte>? bytes = _processedBlobTxsDb.GetOwnedMemory(blockNumber.ToBigEndianSpanWithoutLeadingZeros(out _));

        if (bytes is not null)
        {
            ReadOnlySpan<byte> encoded = bytes.GetSpan();
            if (encoded.IsEmpty)
            {
                blockBlobTransactions = null;
                return false;
            }
            if (encoded[0] != 0)
            {
                RlpReader ctx = new(encoded);
                blockBlobTransactions = _txDecoder.DecodeNonNullArray(ref ctx, RlpBehaviors.InMempoolForm | RlpBehaviors.Storage);
                return true;
            }

            int count = DecodeProcessedTransactionCount(encoded);
            Transaction[] transactions = new Transaction[count];
            Unsafe.SkipInit(out Vector128<byte> keyStorage);
            Span<byte> key = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref keyStorage, 1))[..ProcessedTransactionKeyLength];
            BinaryPrimitives.WriteUInt64BigEndian(key, blockNumber);
            for (int i = 0; i < count; i++)
            {
                BinaryPrimitives.WriteInt32BigEndian(key[sizeof(ulong)..], i);
                using MemoryManager<byte> payload = _processedBlobTxsDb.GetOwnedMemory(key)
                    ?? throw new RlpException($"Missing processed blob transaction {i} for block {blockNumber}.");
                RlpReader reader = new(payload.GetSpan());
                transactions[i] = _txDecoder.Decode(ref reader, RlpBehaviors.InMempoolForm | RlpBehaviors.Storage)
                    ?? throw new RlpException($"Null processed blob transaction {i} for block {blockNumber}.");
            }
            blockBlobTransactions = transactions;
            return true;
        }

        blockBlobTransactions = default;
        return false;
    }

    [SkipLocalsInit]
    public void DeleteBlobTransactionsFromBlock(ulong blockNumber)
    {
        using Lock.Scope locked = _processedTransactionsLock.EnterScope();
        using IColumnsWriteBatch<BlobTxsColumns> batch = _database.StartWriteBatch();
        try
        {
            IWriteBatch processed = batch.GetColumnBatch(BlobTxsColumns.ProcessedTxs);
            Unsafe.SkipInit(out Vector128<byte> keyStorage);
            Span<byte> key = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref keyStorage, 1))[..ProcessedTransactionKeyLength];
            BinaryPrimitives.WriteUInt64BigEndian(key, blockNumber);
            RemoveProcessedTransactionRecords(processed, key, 0, GetProcessedTransactionCount(blockNumber));
            processed.Delete(blockNumber);
        }
        catch
        {
            batch.Clear();
            throw;
        }
    }

    private int GetProcessedTransactionCount(ulong blockNumber)
    {
        ReadOnlySpan<byte> index = _processedBlobTxsDb.GetSpan(blockNumber.ToBigEndianSpanWithoutLeadingZeros(out _));
        try
        {
            return !index.IsEmpty && index[0] == 0 ? DecodeProcessedTransactionCount(index) : 0;
        }
        finally
        {
            _processedBlobTxsDb.DangerousReleaseMemory(index);
        }
    }

    private static int DecodeProcessedTransactionCount(ReadOnlySpan<byte> index)
    {
        if (index.Length != ProcessedBlockIndexLength || BinaryPrimitives.ReadInt32BigEndian(index[1..]) <= 0)
            throw new RlpException("Invalid processed blob transaction index.");
        return BinaryPrimitives.ReadInt32BigEndian(index[1..]);
    }

    private static void RemoveProcessedTransactionRecords(IWriteBatch batch, Span<byte> key, int start, int count)
    {
        for (int i = start; i < count; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(key[sizeof(ulong)..], i);
            batch.Remove(key);
        }
    }

    private static void SaveProcessedTransactions(IWriteBatch batch, Span<byte> key, ReadOnlySpan<Transaction> transactions)
    {
        const RlpBehaviors behaviors = RlpBehaviors.InMempoolForm | RlpBehaviors.Storage;
        int maxLength = 0;
        foreach (Transaction transaction in transactions)
        {
            maxLength = Math.Max(maxLength, _txDecoder.GetLength(transaction, behaviors));
        }

        using ArrayPoolSpan<byte> rented = new(maxLength);
        Span<byte> buffer = rented;
        Span<byte> transactionIndex = key.Slice(sizeof(ulong), sizeof(int));
        int index = 0;
        foreach (Transaction transaction in transactions)
        {
            BinaryPrimitives.WriteInt32BigEndian(transactionIndex, index++);
            RlpWriter writer = new(buffer);
            _txDecoder.EncodeTx(ref writer, transaction, behaviors, forSigning: false, isEip155Enabled: false, chainId: 0);
            batch.PutSpan(key, buffer[..writer.Position]);
        }
    }

    private static bool TryDecodeFullTx(
        byte[]? txBytes,
        Address sender,
        in UInt256 timestamp,
        [NotNullWhen(true)] out Transaction? transaction)
    {
        if (txBytes is not null)
        {
            transaction = Rlp.Decode<Transaction>(txBytes, RlpBehaviors.InMempoolForm | RlpBehaviors.Storage);
            if (transaction is null)
            {
                return false;
            }

            transaction.SenderAddress = sender;
            transaction.Timestamp = timestamp;
            return true;
        }

        transaction = default;
        return false;
    }

    private byte[] RentKey()
    {
        if (_keyPool.TryDequeue(out byte[]? key))
        {
            Interlocked.Decrement(ref _pooledKeyCount);
            return key;
        }

        return new byte[FullTxKeyLength];
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

    private bool ShouldWriteElided(Transaction transaction, scoped Span<byte> elidedKey)
    {
        if (transaction.NetworkWrapper is not ShardBlobNetworkWrapper)
        {
            return false;
        }

        GetElidedTxKey(transaction.Hash, elidedKey);
        return !_fullBlobTxsDb.KeyExists(elidedKey);
    }

    private static void GetHashPrefixedByTimestamp(in UInt256 timestamp, in ValueHash256 hash, scoped Span<byte> txHashPrefixed)
    {
        timestamp.WriteBigEndian(txHashPrefixed);
        hash.Bytes.CopyTo(txHashPrefixed[32..]);
    }

    private static Lock GetTransactionLock(in ValueHash256 hash) => _transactionLocks[GetTransactionLockIndex(hash)];

    private static int GetTransactionLockIndex(in ValueHash256 hash) =>
        (int)((uint)hash.GetHashCode() % TransactionLockCount);

    private static Lock[] CreateTransactionLocks()
    {
        Lock[] locks = new Lock[TransactionLockCount];
        for (int i = 0; i < locks.Length; i++)
        {
            locks[i] = new Lock();
        }

        return locks;
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
