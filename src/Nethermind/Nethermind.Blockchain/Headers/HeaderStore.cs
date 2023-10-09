// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
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
        // validate hash here
        // using previously received header RLPs would allows us to save 2GB allocations on a sample
        // 3M Goerli blocks fast sync
        using NettyRlpStream newRlp = _headerDecoder.EncodeToNewNettyStream(header);
        Span<byte> blockNumberSpan = stackalloc byte[8];
        header.Number.WriteBigEndian(blockNumberSpan);
        _blockNumberDb.Set(header.Hash, blockNumberSpan);
        _headerDb.Set(header.Number, header.Hash, newRlp.AsSpan());
    }

    public BlockHeader? Get(Keccak blockHash, bool shouldCache = false, long? blockNumber = null)
    {
        blockNumber ??= GetBlockNumberFromBlockNumberDb(blockHash);

        if (blockNumber == null)
        {
            return _headerDb.Get(blockHash, _headerDecoder, _headerCache, shouldCache: shouldCache);
        }

        BlockHeader? withNumber = _headerDb.Get(blockNumber.Value, blockHash, _headerDecoder, _headerCache, shouldCache: shouldCache);
        if (withNumber != null)
        {
            return withNumber;
        }

        return _headerDb.Get(blockHash, _headerDecoder, _headerCache, shouldCache: shouldCache);
    }

    public void Cache(BlockHeader header)
    {
        _headerCache.Set(header.Hash, header);
    }

    public void Delete(Keccak blockHash)
    {
        long? blockNumber = GetBlockNumberFromBlockNumberDb(blockHash);
        if (blockNumber != null) _headerDb.Delete(blockNumber.Value, blockHash);
        _headerDb.Delete(blockHash);
        _blockNumberDb.Delete(blockHash);
        _headerCache.Delete(blockHash);
    }

    private long? GetBlockNumberFromBlockNumberDb(Keccak blockHash)
    {
        byte[] numberBytes = _blockNumberDb.Get(blockHash);
        if (numberBytes == null) return null;
        if (numberBytes.Length != 8)
        {
            throw new Exception($"Unexpected number span length: {numberBytes.Length}");
        }

        return BinaryPrimitives.ReadInt64BigEndian(numberBytes);
    }
}
