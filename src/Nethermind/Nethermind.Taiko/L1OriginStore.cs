// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Taiko;

public class L1OriginStore(IDb db, IRlpStreamDecoder<L1Origin> decoder, ILogManager? logManager = null) : IL1OriginStore
{
    private readonly ILogger? _logger = logManager?.GetClassLogger<L1OriginStore>();

    private static readonly int L1OriginHeadKeyLength = 32;
    private static readonly byte[] L1OriginHeadKey = UInt256.MaxValue.ToBigEndian();

    public L1Origin? ReadL1Origin(UInt256 blockId)
    {
        Span<byte> keyBytes = stackalloc byte[L1OriginHeadKeyLength];
        blockId.ToBigEndian(keyBytes);
        ValueHash256 key = new(keyBytes);

        return db.Get(key, decoder);
    }

    public void WriteL1Origin(UInt256 blockId, L1Origin l1Origin)
    {
        Span<byte> key = stackalloc byte[L1OriginHeadKeyLength];
        blockId.ToBigEndian(key);

        if (decoder is null)
        {
            _logger?.Warn($"Unable to save L1 origin decoder for {nameof(L1Origin)} not found");
            return;
        }

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
        db.Set(L1OriginHeadKey, blockId.ToBigEndian());
    }
}
