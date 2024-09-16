// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Taiko;

public class L1OriginStore(IDb db, ILogManager? logManager = null) : IL1OriginStore
{
    private readonly IDb _db = db;
    private readonly ILogger? _logger = logManager?.GetClassLogger<L1OriginStore>();

    private static readonly int L1OriginHeadKeyLength = 32;
    private static readonly byte[] L1OriginHeadKey = UInt256.MaxValue.ToBigEndian();

    public L1Origin? ReadL1Origin(UInt256 blockId)
    {
        Span<byte> key = stackalloc byte[L1OriginHeadKeyLength];
        blockId.ToBigEndian(key);

        return _db.Get(key) switch
        {
            null => null,
            byte[] bytes => Rlp.Decode<L1Origin>(bytes)
        };
    }

    public void WriteL1Origin(UInt256 blockId, L1Origin l1Origin)
    {
        Span<byte> key = stackalloc byte[L1OriginHeadKeyLength];
        blockId.ToBigEndian(key);

        IRlpStreamDecoder<L1Origin>? decoder = Rlp.GetStreamDecoder<L1Origin>();

        if (decoder is null)
        {
            _logger?.Warn($"Unable to save L1 origin decoder for {nameof(L1Origin)} not found");
            return;
        }

        IByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(decoder.GetLength(l1Origin, RlpBehaviors.None));

        try
        {
            using NettyRlpStream stream = new(buffer);
            decoder.Encode(stream, l1Origin);
            _db.Set(key, buffer.Array);
        }
        finally
        {
            buffer.Release();
        }
    }

    public UInt256? ReadHeadL1Origin()
    {
        return _db.Get(L1OriginHeadKey) switch
        {
            null => null,
            byte[] bytes => new UInt256(bytes, isBigEndian: true)
        };
    }

    public void WriteHeadL1Origin(UInt256 blockId)
    {
        _db.Set(L1OriginHeadKey, blockId.ToBigEndian());
    }
}
