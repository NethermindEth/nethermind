// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.TxPool;

public class BlobTxStorage : IBlobTxStorage
{
    private static readonly TxDecoder _txDecoder = new();
    private readonly IDb _fullBlobTxsDb;
    private readonly IDb _lightBlobTxsDb;
    private readonly IDb _processedBlobTxsDb;

    public BlobTxStorage()
    {
        _fullBlobTxsDb = new MemDb();
        _lightBlobTxsDb = new MemDb();
        _processedBlobTxsDb = new MemDb();
    }

    public BlobTxStorage(IColumnsDb<BlobTxsColumns> database)
    {
        _fullBlobTxsDb = database.GetColumnDb(BlobTxsColumns.FullBlobTxs);
        _lightBlobTxsDb = database.GetColumnDb(BlobTxsColumns.LightBlobTxs);
        _processedBlobTxsDb = database.GetColumnDb(BlobTxsColumns.ProcessedTxs);
    }

    public bool TryGet(in ValueHash256 hash, Address sender, in UInt256 timestamp, [NotNullWhen(true)] out Transaction? transaction)
    {
        Span<byte> txHashPrefixed = stackalloc byte[64];
        GetHashPrefixedByTimestamp(timestamp, hash, txHashPrefixed);

        byte[]? txBytes = _fullBlobTxsDb.Get(txHashPrefixed);
        return TryDecodeFullTx(txBytes, sender, out transaction);
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

        Span<byte> txHashPrefixed = stackalloc byte[64];
        GetHashPrefixedByTimestamp(transaction.Timestamp, transaction.Hash, txHashPrefixed);

        EncodeAndSaveTx(transaction, _fullBlobTxsDb, txHashPrefixed);
        _lightBlobTxsDb.Set(transaction.Hash, LightTxDecoder.Encode(transaction));
    }

    public void Delete(in ValueHash256 hash, in UInt256 timestamp)
    {
        Span<byte> txHashPrefixed = stackalloc byte[64];
        GetHashPrefixedByTimestamp(timestamp, hash, txHashPrefixed);

        _fullBlobTxsDb.Remove(txHashPrefixed);
        _lightBlobTxsDb.Remove(hash.BytesAsSpan);
    }

    public void AddBlobTransactionsFromBlock(long blockNumber, IList<Transaction> blockBlobTransactions)
    {
        if (blockBlobTransactions.Count == 0)
        {
            return;
        }

        EncodeAndSaveTxs(blockBlobTransactions, _processedBlobTxsDb, blockNumber);
    }

    public bool TryGetBlobTransactionsFromBlock(long blockNumber, out Transaction[]? blockBlobTransactions)
    {
        byte[]? bytes = _processedBlobTxsDb.Get(blockNumber);

        if (bytes is not null)
        {
            RlpStream rlpStream = new(bytes);
            blockBlobTransactions = _txDecoder.DecodeArray(rlpStream, RlpBehaviors.InMempoolForm);
            return true;
        }

        blockBlobTransactions = default;
        return false;
    }

    public void DeleteBlobTransactionsFromBlock(long blockNumber)
        => _processedBlobTxsDb.Delete(blockNumber);

    private static bool TryDecodeFullTx(byte[]? txBytes, Address sender, out Transaction? transaction)
    {
        if (txBytes is not null)
        {
            RlpStream rlpStream = new(txBytes);
            transaction = Rlp.Decode<Transaction>(rlpStream, RlpBehaviors.InMempoolForm);
            transaction.SenderAddress = sender;
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

    private static void GetHashPrefixedByTimestamp(UInt256 timestamp, ValueHash256 hash, Span<byte> txHashPrefixed)
    {
        timestamp.WriteBigEndian(txHashPrefixed);
        hash.Bytes.CopyTo(txHashPrefixed[32..]);
    }

    private void EncodeAndSaveTx(Transaction transaction, IDb db, Span<byte> txHashPrefixed)
    {
        int length = _txDecoder.GetLength(transaction, RlpBehaviors.InMempoolForm);
        IByteBuffer byteBuffer = PooledByteBufferAllocator.Default.Buffer(length);
        using NettyRlpStream rlpStream = new(byteBuffer);
        rlpStream.Encode(transaction, RlpBehaviors.InMempoolForm);

        db.PutSpan(txHashPrefixed, byteBuffer.AsSpan());
    }

    private void EncodeAndSaveTxs(IList<Transaction> blockBlobTransactions, IDb db, long blockNumber)
    {
        int contentLength = GetLength(blockBlobTransactions);

        IByteBuffer byteBuffer = PooledByteBufferAllocator.Default.Buffer(Rlp.LengthOfSequence(contentLength));
        using NettyRlpStream rlpStream = new(byteBuffer);
        rlpStream.StartSequence(contentLength);
        foreach (Transaction transaction in blockBlobTransactions)
        {
            _txDecoder.Encode(rlpStream, transaction, RlpBehaviors.InMempoolForm);
        }

        db.Set(blockNumber, byteBuffer.Array);
    }

    private int GetLength(IList<Transaction> blockBlobTransactions)
    {
        int contentLength = 0;
        foreach (Transaction transaction in blockBlobTransactions)
        {
            contentLength += _txDecoder.GetLength(transaction, RlpBehaviors.InMempoolForm);
        }

        return contentLength;
    }
}

internal static class UInt256Extensions
{
    public static void WriteBigEndian(in this UInt256 value, Span<byte> output)
    {
        BinaryPrimitives.WriteUInt64BigEndian(output.Slice(0, 8), value.u3);
        BinaryPrimitives.WriteUInt64BigEndian(output.Slice(8, 8), value.u2);
        BinaryPrimitives.WriteUInt64BigEndian(output.Slice(16, 8), value.u1);
        BinaryPrimitives.WriteUInt64BigEndian(output.Slice(24, 8), value.u0);
    }
}
