// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Headers;

public class HeaderStore : IHeaderStore
{
    // SyncProgressResolver MaxLookupBack is 256, add 16 wiggle room
    public const int CacheSize = 256 + 16;
    // Go a bit further back for numbers as smaller
    public const int NumberCacheSize = CacheSize * 4;

    private readonly IDb _headerDb;
    private readonly IDb _blockNumberDb;
    private readonly IHeaderDecoder _headerDecoder;
    private readonly ClockCache<Hash256AsKey, BlockHeader> _headerCache = new(CacheSize);
    private readonly ClockCache<Hash256AsKey, long> _numberCache = new(NumberCacheSize);
    private readonly ClockCache<long, Hash256> _hashCache = new(NumberCacheSize);

    public HeaderStore([KeyFilter(DbNames.Headers)] IDb headerDb, [KeyFilter(DbNames.BlockNumbers)] IDb blockNumberDb, IHeaderDecoder? decoder = null)
    {
        _headerDb = headerDb;
        _blockNumberDb = blockNumberDb;
        _headerDecoder = decoder ?? new HeaderDecoder();
    }

    public void Insert(BlockHeader header)
    {
        using NettyRlpStream newRlp = _headerDecoder.EncodeToNewNettyStream(header);
        _headerDb.Set(header.Number, header.Hash, newRlp.AsSpan());
        InsertBlockNumber(header.Hash, header.Number);
    }

    public void BulkInsert(IReadOnlyList<BlockHeader> headers)
    {
        using IWriteBatch headerWriteBatch = _headerDb.StartWriteBatch();
        using IWriteBatch blockNumberWriteBatch = _blockNumberDb.StartWriteBatch();

        Span<byte> blockNumberSpan = stackalloc byte[8];
        foreach (BlockHeader header in headers)
        {
            using NettyRlpStream newRlp = _headerDecoder.EncodeToNewNettyStream(header);
            headerWriteBatch.Set(header.Number, header.Hash, newRlp.AsSpan());

            header.Number.WriteBigEndian(blockNumberSpan);
            blockNumberWriteBatch.Set(header.Hash, blockNumberSpan);
            CacheNumber(header.Hash, header.Number, isMainChain: false);
        }
    }

    public BlockHeader? Get(Hash256 blockHash, out bool fromCache, bool shouldCache = false, long? blockNumber = null)
    {
        blockNumber ??= GetBlockNumberFromBlockNumberDb(blockHash);

        BlockHeader? header;
        if (blockNumber is not null)
        {
            header = _headerDb.Get(blockNumber.Value, blockHash, _headerDecoder, out fromCache, _headerCache, shouldCache: shouldCache);
            if (header is not null)
            {
                return header;
            }
        }

        return _headerDb.Get(blockHash, _headerDecoder, out fromCache, _headerCache, shouldCache: shouldCache);
    }

    public void CacheBlockHash(long blockNumber, Hash256 blockHash)
        => CacheNumber(blockHash, blockNumber, isMainChain: true);

    public void Cache(BlockHeader header, bool isMainChain)
    {
        CacheNumber(header.Hash, header.Number, isMainChain);
        _headerCache.Set(header.Hash, header);
    }

    public BlockHeader? GetFromCache(Hash256 blockHash)
        => _headerCache.Get(blockHash);

    public void Delete(Hash256 blockHash)
    {
        long? blockNumber = GetBlockNumber(blockHash);
        if (blockNumber is not null)
        {
            _headerDb.Delete(blockNumber.Value, blockHash);
            _hashCache.Delete(blockNumber.Value);
        }
        _blockNumberDb.Delete(blockHash);
        _headerDb.Delete(blockHash);
        _headerCache.Delete(blockHash);
        _numberCache.Delete(blockHash);
    }

    public void InsertBlockNumber(Hash256 blockHash, long blockNumber)
    {
        Span<byte> blockNumberSpan = stackalloc byte[8];
        blockNumber.WriteBigEndian(blockNumberSpan);
        _blockNumberDb.Set(blockHash, blockNumberSpan);
        CacheNumber(blockHash, blockNumber, isMainChain: false);
    }

    private void CacheNumber(Hash256 blockHash, long blockNumber, bool isMainChain)
    {
        _numberCache.Set(blockHash, blockNumber);
        if (isMainChain)
        {
            _hashCache.Set(blockNumber, blockHash);
        }
    }

    public Hash256? GetBlockHash(long blockNumber)
    {
        if (_hashCache.TryGet(blockNumber, out Hash256? hash))
        {
            return hash;
        }

        return null;
    }

    public long? GetBlockNumber(Hash256 blockHash)
    {
        if (_numberCache.TryGet(blockHash, out long number))
        {
            return number;
        }

        return GetBlockNumberThroughHeaderCache(blockHash);
    }

    private long? GetBlockNumberThroughHeaderCache(Hash256 blockHash)
    {
        if (_headerCache.TryGet(blockHash, out BlockHeader? header))
        {
            CacheNumber(blockHash, header.Number, isMainChain: false);
            return header.Number;
        }

        long? blockNumber = GetBlockNumberFromBlockNumberDb(blockHash);
        if (blockNumber is not null) return blockNumber.Value;

        // Probably still hash based
        blockNumber = Get(blockHash, out _)?.Number;
        if (blockNumber.HasValue)
        {
            CacheNumber(blockHash, blockNumber.Value, isMainChain: false);
        }
        return blockNumber;
    }

    private long? GetBlockNumberFromBlockNumberDb(Hash256 blockHash)
    {
        // Double check cache as we have done a fair amount of checks
        // since the cache check and something else might have populated it.
        if (_numberCache.TryGet(blockHash, out long number))
        {
            return number;
        }
        Span<byte> numberSpan = _blockNumberDb.GetSpan(blockHash);
        if (numberSpan.IsNullOrEmpty()) return null;
        try
        {
            if (numberSpan.Length != 8)
            {
                throw new InvalidDataException($"Unexpected number span length: {numberSpan.Length}");
            }

            number = BinaryPrimitives.ReadInt64BigEndian(numberSpan);
            CacheNumber(blockHash, number, isMainChain: false);
            return number;
        }
        finally
        {
            _blockNumberDb.DangerousReleaseMemory(numberSpan);
        }
    }
}
