// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Core.Collections;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Taiko;

/// <summary>
/// RocksDB-backed store for L1Origin records, the head pointer, and the
/// batch→last-block mapping consumed by the Taiko engine RPC and header validator.
/// </summary>
/// <remarks>
/// Individual <see cref="IDb"/> Get/Put operations are atomic, but multi-step
/// read–modify–write sequences are not. Writes therefore acquire <see cref="_writeLock"/>
/// so that <see cref="SetL1OriginSignature"/> and other writers cannot interleave
/// and clobber each other when invoked concurrently from the auth-RPC thread pool.
/// Reads are not synchronised; callers tolerate eventual visibility of the latest write.
/// </remarks>
public class L1OriginStore([KeyFilter(L1OriginStore.L1OriginDbName)] IDb db, RlpDecoder<L1Origin> decoder) : IL1OriginStore
{
    public const string L1OriginDbName = "L1Origin";
    private const int UInt256BytesLength = 32;
    private const int KeyBytesLength = UInt256BytesLength + 1;
    private const byte L1OriginPrefix = 0x00;
    private const byte BatchToBlockPrefix = 0x01;
    private const byte L1OriginHeadPrefix = 0xFF;
    private static readonly byte[] L1OriginHeadKey = [L1OriginHeadPrefix];

    /// <summary>
    /// Serialises all write paths against each other. Held briefly during a single
    /// Put or a Read+Put pair; reads do not take the lock.
    /// </summary>
    private readonly Lock _writeLock = new();

    private static void CreateL1OriginKey(UInt256 blockId, Span<byte> keyBytes)
    {
        keyBytes[0] = L1OriginPrefix;
        blockId.ToBigEndian(keyBytes[1..]);
    }

    private static void CreateBatchToBlockKey(UInt256 batchId, Span<byte> keyBytes)
    {
        keyBytes[0] = BatchToBlockPrefix;
        batchId.ToBigEndian(keyBytes[1..]);
    }

    public L1Origin? ReadL1Origin(UInt256 blockId)
    {
        Span<byte> keyBytes = stackalloc byte[KeyBytesLength];
        CreateL1OriginKey(blockId, keyBytes);

        byte[]? data = db.Get(keyBytes);
        if (data is null) return null;
        RlpReader ctx = new(data);
        return decoder.Decode(ref ctx);
    }

    public void WriteL1Origin(UInt256 blockId, L1Origin l1Origin)
    {
        lock (_writeLock)
        {
            WriteL1OriginNoLock(blockId, l1Origin);
        }
    }

    /// <summary>
    /// Encode-and-put helper. Caller must hold <see cref="_writeLock"/>.
    /// </summary>
    private void WriteL1OriginNoLock(UInt256 blockId, L1Origin l1Origin)
    {
        Span<byte> key = stackalloc byte[KeyBytesLength];
        CreateL1OriginKey(blockId, key);

        int encodedL1OriginLength = decoder.GetLength(l1Origin, RlpBehaviors.None);
        using ArrayPoolSpan<byte> buffer = new(encodedL1OriginLength);
        RlpWriter writer = new(buffer);
        decoder.Encode(ref writer, l1Origin);
        db.PutSpan(key, buffer);
    }

    public UInt256? ReadHeadL1Origin() => db.Get(L1OriginHeadKey) switch
    {
        null => null,
        byte[] bytes => new UInt256(bytes, isBigEndian: true)
    };

    public void WriteHeadL1Origin(UInt256 blockId)
    {
        Span<byte> blockIdBytes = stackalloc byte[UInt256BytesLength];
        blockId.ToBigEndian(blockIdBytes);

        lock (_writeLock)
        {
            db.PutSpan(L1OriginHeadKey, blockIdBytes);
        }
    }

    public UInt256? ReadBatchToLastBlockID(UInt256 batchId)
    {
        Span<byte> keyBytes = stackalloc byte[KeyBytesLength];
        CreateBatchToBlockKey(batchId, keyBytes);

        return db.Get(keyBytes) switch
        {
            null => null,
            byte[] bytes => new UInt256(bytes, isBigEndian: true)
        };
    }

    public void WriteBatchToLastBlockID(UInt256 batchId, UInt256 blockId)
    {
        Span<byte> key = stackalloc byte[KeyBytesLength];
        CreateBatchToBlockKey(batchId, key);

        Span<byte> blockIdBytes = stackalloc byte[UInt256BytesLength];
        blockId.ToBigEndian(blockIdBytes);

        lock (_writeLock)
        {
            db.PutSpan(key, blockIdBytes);
        }
    }

    public L1Origin? SetL1OriginSignature(UInt256 blockId, byte[] signature)
    {
        lock (_writeLock)
        {
            L1Origin? origin = ReadL1Origin(blockId);
            if (origin is null) return null;

            origin.Signature = signature;
            WriteL1OriginNoLock(blockId, origin);
            return origin;
        }
    }
}
