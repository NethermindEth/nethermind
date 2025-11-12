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

public class HeaderStore(
    [KeyFilter(DbNames.Headers)] IDb headerDb,
    [KeyFilter(DbNames.BlockNumbers)] IDb blockNumberDb,
    IHeaderDecoder? decoder = null)
    : IHeaderStore
{
    // SyncProgressResolver MaxLookupBack is 256, add 16 wiggle room
    public const int CacheSize = 256 + 16;

    private readonly IHeaderDecoder _headerDecoder = decoder ?? new HeaderDecoder();
    private readonly ClockCache<ValueHash256, BlockHeader> _headerCache = new(CacheSize);

    public void Insert(BlockHeader header)
    {
        using NettyRlpStream newRlp = _headerDecoder.EncodeToNewNettyStream(header);
        headerDb.Set(header.Number, header.Hash!, newRlp.AsSpan());
        InsertBlockNumber(header.Hash, header.Number);
    }

    public void BulkInsert(IReadOnlyList<BlockHeader> headers)
    {
        using IWriteBatch headerWriteBatch = headerDb.StartWriteBatch();
        using IWriteBatch blockNumberWriteBatch = blockNumberDb.StartWriteBatch();

        Span<byte> blockNumberSpan = stackalloc byte[8];
        foreach (BlockHeader header in headers)
        {
            using NettyRlpStream newRlp = _headerDecoder.EncodeToNewNettyStream(header);
            headerWriteBatch.Set(header.Number, header.Hash!, newRlp.AsSpan());

            header.Number.WriteBigEndian(blockNumberSpan);
            blockNumberWriteBatch.Set(header.Hash, blockNumberSpan);
        }
    }

    public BlockHeader? Get(Hash256 blockHash, bool shouldCache = false, long? blockNumber = null)
    {
        blockNumber ??= GetBlockNumberFromBlockNumberDb(blockHash);

        BlockHeader? header = null;
        if (blockNumber is not null)
        {
            header = headerDb.Get(blockNumber.Value, blockHash, _headerDecoder, _headerCache, shouldCache: shouldCache);
        }
        return header ?? headerDb.Get(blockHash, _headerDecoder, _headerCache, shouldCache: shouldCache);
    }

    public void Cache(BlockHeader header)
    {
        _headerCache.Set(header.Hash, header);
    }

    public void Delete(Hash256 blockHash)
    {
        long? blockNumber = GetBlockNumberFromBlockNumberDb(blockHash);
        if (blockNumber is not null) headerDb.Delete(blockNumber.Value, blockHash);
        blockNumberDb.Delete(blockHash);
        headerDb.Delete(blockHash);
        _headerCache.Delete(blockHash);
    }

    public void InsertBlockNumber(Hash256 blockHash, long blockNumber)
    {
        Span<byte> blockNumberSpan = stackalloc byte[8];
        blockNumber.WriteBigEndian(blockNumberSpan);
        blockNumberDb.Set(blockHash, blockNumberSpan);
    }

    public long? GetBlockNumber(Hash256 blockHash)
    {
        long? blockNumber = GetBlockNumberFromBlockNumberDb(blockHash);
        if (blockNumber is not null) return blockNumber.Value;

        // Probably still hash based
        return Get(blockHash)?.Number;
    }

    private long? GetBlockNumberFromBlockNumberDb(Hash256 blockHash)
    {
        Span<byte> numberSpan = blockNumberDb.GetSpan(blockHash);
        if (numberSpan.IsNullOrEmpty()) return null;
        try
        {
            if (numberSpan.Length != 8)
            {
                throw new InvalidDataException($"Unexpected number span length: {numberSpan.Length}");
            }

            return BinaryPrimitives.ReadInt64BigEndian(numberSpan);
        }
        finally
        {
            blockNumberDb.DangerousReleaseMemory(numberSpan);
        }
    }
}
