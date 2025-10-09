// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
    private static readonly HeaderDecoder _headerDecoder = new();

    private readonly IDb _headerDb;
    private readonly IDb _blockNumberDb;
    private readonly HeaderCache _headerCache = new();
    private readonly ClockCache<Hash256AsKey, long> _numberCache = new(NumberCacheSize);

    public HeaderStore([KeyFilter(DbNames.Headers)] IDb headerDb, [KeyFilter(DbNames.BlockNumbers)] IDb blockNumberDb)
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

    public void BulkInsert(IReadOnlyList<BlockHeader> headers)
    {
        using IWriteBatch headerWriteBatch = _headerDb.StartWriteBatch();
        using IWriteBatch blockNumberWriteBatch = _blockNumberDb.StartWriteBatch();

        Span<byte> blockNumberSpan = stackalloc byte[8];
        foreach (var header in headers)
        {
            using NettyRlpStream newRlp = _headerDecoder.EncodeToNewNettyStream(header);
            headerWriteBatch.Set(header.Number, header.Hash, newRlp.AsSpan());

            header.Number.WriteBigEndian(blockNumberSpan);
            blockNumberWriteBatch.Set(header.Hash, blockNumberSpan);
            CacheNumber(header.Hash, header.Number);
        }
    }

    public BlockHeader? Get(Hash256 blockHash, long? blockNumber = null)
    {
        blockNumber ??= GetBlockNumberFromBlockNumberDb(blockHash);

        BlockHeader? header = null;
        if (blockNumber is not null)
        {
            header = _headerDb.Get(blockNumber.Value, blockHash, _headerDecoder, _headerCache, shouldCache: false);
        }
        return header ?? _headerDb.Get(blockHash, _headerDecoder, _headerCache, shouldCache: false);
    }

    public bool TryGetCache(Hash256 blockHash, bool needsDifficulty, bool requiresCanonical, [NotNullWhen(true)] out BlockHeader? header)
        => _headerCache.TryGet(blockHash, needsDifficulty, requiresCanonical, out header);

    public bool Cache(BlockHeader header, bool hasDifficulty, bool isCanonical = false)
    {
        CacheNumber(header.Hash, header.Number);
        return _headerCache.Set(header.Hash, header, hasDifficulty, isCanonical);
    }

    public void DeleteCanonicalCache(Hash256 blockHash) => _headerCache.DeleteCanonicalCache(blockHash);

    public void Delete(Hash256 blockHash)
    {
        long? blockNumber = GetBlockNumberFromBlockNumberDb(blockHash);
        if (blockNumber is not null) _headerDb.Delete(blockNumber.Value, blockHash);
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
        CacheNumber(blockHash, blockNumber);
    }

    private void CacheNumber(Hash256 blockHash, long blockNumber)
        => _numberCache.Set(blockHash, blockNumber);

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
        if (TryGetCache(blockHash, needsDifficulty: false, requiresCanonical: false, out BlockHeader header))
        {
            CacheNumber(blockHash, header.Number);
            return header.Number;
        }

        long? blockNumber = GetBlockNumberFromBlockNumberDb(blockHash);
        if (blockNumber is not null) return blockNumber.Value;

        // Probably still hash based
        blockNumber = Get(blockHash)?.Number;
        if (blockNumber.HasValue)
        {
            CacheNumber(blockHash, blockNumber.Value);
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
            CacheNumber(blockHash, number);
            return number;
        }
        finally
        {
            _blockNumberDb.DangerousReleaseMemory(numberSpan);
        }
    }

    private class HeaderCache : IClockCache<Hash256AsKey, BlockHeader>
    {
        private readonly ClockCache<Hash256AsKey, BlockHeader> _headerCache = new(CacheSize);
        private readonly ClockCache<Hash256AsKey, BlockHeader> _headerCanonicalCache = new(CacheSize);
        private readonly ClockCache<Hash256AsKey, BlockHeader> _headerWithoutDifficultyCache = new(CacheSize);

        public BlockHeader? Get(Hash256AsKey blockHash)
        {
            TryGet(blockHash, out BlockHeader? header);
            return header;
        }

        public bool Set(Hash256AsKey key, BlockHeader header)
            => Set(key, header, hasDifficulty: true);

        public bool Set(Hash256AsKey key, BlockHeader header, bool hasDifficulty, bool isCanonical = false)
        {
            if (hasDifficulty)
            {
                if (isCanonical)
                {
                    bool wasSet = _headerCanonicalCache.Set(header.Hash, header);
                    // clear other caches as canonical covers all types
                    _headerCache.Delete(header.Hash);
                    _headerWithoutDifficultyCache.Delete(header.Hash);
                    return wasSet;
                }
                else if (!_headerCanonicalCache.Contains(header.Hash))
                {
                    bool wasSet = _headerCache.Set(header.Hash, header);
                    // clear without difficulty regular covers it
                    _headerWithoutDifficultyCache.Delete(header.Hash);
                    return wasSet;
                }
                return false;
            }
            else if (!_headerCanonicalCache.Contains(header.Hash) && !_headerCache.Contains(header.Hash))
            {
                return _headerWithoutDifficultyCache.Set(header.Hash, header);
            }
            return false;
        }

        public bool TryGet(Hash256AsKey blockHash, [NotNullWhen(true)] out BlockHeader? header)
            => TryGet(blockHash, needsDifficulty: true, requiresCanonical: false, out header);

        public bool TryGet(Hash256AsKey blockHash, bool needsDifficulty, bool requiresCanonical, [NotNullWhen(true)] out BlockHeader? header)
        {
            if (_headerCanonicalCache.TryGet(blockHash, out header))
            {
                return true;
            }

            if (requiresCanonical)
            {
                return false;
            }

            if (_headerCache.TryGet(blockHash, out header))
            {
                return true;
            }

            if (needsDifficulty)
            {
                return false;
            }

            return _headerWithoutDifficultyCache.TryGet(blockHash, out header);
        }

        public void Delete(Hash256AsKey blockHash)
        {
            _headerCache.Delete(blockHash);
            _headerCanonicalCache.Delete(blockHash);
            _headerWithoutDifficultyCache.Delete(blockHash);
        }
        public void DeleteCanonicalCache(Hash256 blockHash) => _headerCanonicalCache.Delete(blockHash);
    }
}
