// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Headers;

public class HeaderStore : IHeaderStore
{
    // SyncProgressResolver MaxLookupBack is 128, add 16 wiggle room
    private const int CacheSize = 128 + 16;

    private readonly IDb _headerDb;
    private readonly IDb _blockNumberDb;
    private readonly HeaderDecoder _headerDecoder = new();
    private readonly LruCache<ValueKeccak, BlockHeader> _headerCache =
        new(CacheSize, CacheSize, "headers");

    public HeaderStore(IDb headerDb, IDb blockNumberDb)
    {
        _headerDb = headerDb;
        _blockNumberDb = blockNumberDb;
    }

    public void Insert(BlockHeader header)
    {
        using NettyRlpStream newRlp = _headerDecoder.EncodeToNewNettyStream(header);
        _headerDb.Set(header.Number, header.Hash, newRlp.AsSpan());
        InsertBlockNumber(header.Hash, header.Number);
    }

    public BlockHeader? Get(Keccak blockHash, bool shouldCache = false, long? blockNumber = null)
    {
        blockNumber ??= GetBlockNumberFromBlockNumberDb(blockHash);

        BlockHeader? header = null;
        if (blockNumber is not null)
        {
            header = _headerDb.Get(blockNumber.Value, blockHash, _headerDecoder, _headerCache, shouldCache: shouldCache);
        }
        return header ?? _headerDb.Get(blockHash, _headerDecoder, _headerCache, shouldCache: shouldCache);
    }

    public void Cache(BlockHeader header)
    {
        _headerCache.Set(header.Hash, header);
    }

    public void Delete(Keccak blockHash)
    {
        long? blockNumber = GetBlockNumberFromBlockNumberDb(blockHash);
        if (blockNumber != null) _headerDb.Delete(blockNumber.Value, blockHash);
        _blockNumberDb.Delete(blockHash);
        _headerDb.Delete(blockHash);
        _headerCache.Delete(blockHash);
    }

    public void InsertBlockNumber(Keccak blockHash, long blockNumber)
    {
        Span<byte> blockNumberSpan = stackalloc byte[8];
        blockNumber.WriteBigEndian(blockNumberSpan);
        _blockNumberDb.Set(blockHash, blockNumberSpan);
    }

    public long? GetBlockNumber(Keccak blockHash)
    {
        long? blockNumber = GetBlockNumberFromBlockNumberDb(blockHash);
        if (blockNumber != null) return blockNumber.Value;

        // Probably still hash based
        return Get(blockHash)?.Number;
    }

    private long? GetBlockNumberFromBlockNumberDb(Keccak blockHash)
    {
        if (_blockNumberDb is IDbWithSpan spanDb)
        {
            Span<byte> numberSpan = spanDb.GetSpan(blockHash);
            if (numberSpan.IsNullOrEmpty()) return null;
            try
            {
                if (numberSpan.Length != 8)
                {
                    throw new InvalidDataException($"Unexpected number span length: {numberSpan.Length}");
                }

                long num = BinaryPrimitives.ReadInt64BigEndian(numberSpan);
                return num;
            }
            finally
            {
                spanDb.DangerousReleaseMemory(numberSpan);
            }
        }

        byte[] numberBytes = _blockNumberDb.Get(blockHash);
        if (numberBytes == null) return null;
        if (numberBytes.Length != 8)
        {
            throw new InvalidDataException($"Unexpected number span length: {numberBytes.Length}");
        }

        return BinaryPrimitives.ReadInt64BigEndian(numberBytes);
    }
}
