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
    private static readonly byte[] L1OriginHeadKey = UInt256.MaxValue.ToBigEndian();

    public L1Origin? ReadL1Origin(UInt256 blockId)
    {
        Span<byte> keyBytes = stackalloc byte[UInt256BytesLength];
        blockId.ToBigEndian(keyBytes);

        return db.Get(new ValueHash256(keyBytes), decoder);
    }

    public void WriteL1Origin(UInt256 blockId, L1Origin l1Origin)
    {
        Span<byte> key = stackalloc byte[UInt256BytesLength];
        blockId.ToBigEndian(key);

        int encodedL1OriginLength = decoder.GetLength(l1Origin, RlpBehaviors.None);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(encodedL1OriginLength);

        try
        {
            RlpStream stream = new(buffer);
            decoder.Encode(stream, l1Origin);
            db.Set(new ValueHash256(key), buffer.AsSpan(0, encodedL1OriginLength));
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

        db.Set(new(L1OriginHeadKey.AsSpan()), blockIdBytes);
    }
}
