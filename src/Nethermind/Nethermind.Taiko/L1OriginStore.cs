// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Taiko;

public class L1OriginStore([KeyFilter(L1OriginStore.L1OriginDbName)] IDb db, IRlpStreamDecoder<L1Origin> decoder) : IL1OriginStore
{
    public const string L1OriginDbName = "L1Origin";
    private const int UInt256BytesLength = 32;
    private const int KeyBytesLength = UInt256BytesLength + 1;
    private const byte L1OriginPrefix = 0x00;
    private const byte BatchToBlockPrefix = 0x01;
    private const byte L1OriginHeadPrefix = 0xFF;
    private static readonly byte[] L1OriginHeadKey = [L1OriginHeadPrefix, .. new byte[32]];

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
        return data is null ? null : decoder.Decode(new RlpStream(data));
    }

    public void WriteL1Origin(UInt256 blockId, L1Origin l1Origin)
    {
        Span<byte> key = stackalloc byte[KeyBytesLength];
        CreateL1OriginKey(blockId, key);

        int encodedL1OriginLength = decoder.GetLength(l1Origin, RlpBehaviors.None);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(encodedL1OriginLength);

        try
        {
            RlpStream stream = new(buffer);
            decoder.Encode(stream, l1Origin);
            db.PutSpan(key, buffer.AsSpan(0, encodedL1OriginLength));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public UInt256? ReadHeadL1Origin()
    {
        return db.Get(L1OriginHeadKey) switch
        {
            null => null,
            byte[] bytes => new UInt256(bytes, isBigEndian: true)
        };
    }

    public void WriteHeadL1Origin(UInt256 blockId)
    {
        Span<byte> blockIdBytes = stackalloc byte[UInt256BytesLength];
        blockId.ToBigEndian(blockIdBytes);

        db.PutSpan(L1OriginHeadKey, blockIdBytes);
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

        db.PutSpan(key, blockIdBytes);
    }
}
