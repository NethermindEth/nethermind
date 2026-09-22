// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Headers;

public class HeaderStore : IHeaderStore, IClearableCache
{
    // SyncProgressResolver MaxLookupBack is 256, add 16 wiggle room
    public const int CacheSize = 256 + 16;

    private const int NumberPrefixedKeyLength = sizeof(ulong) + Hash256.Size;

    private readonly IDb _headerDb;
    private readonly IDb _blockNumberDb;
    private readonly IHeaderDecoder _headerDecoder;
    private readonly AssociativeCache<ValueHash256, BlockHeader> _headerCache = new(CacheSize);
    private readonly DeferredWriteOverlay<BlockHeader>? _pending;

    public HeaderStore(
        [KeyFilter(DbNames.Headers)] IDb headerDb,
        [KeyFilter(DbNames.BlockNumbers)] IDb blockNumberDb,
        IHeaderDecoder? decoder = null,
        IDeferredBlockDataWriter? deferredWriter = null,
        IStatePersistenceBarrier? persistenceBarrier = null)
    {
        _headerDb = headerDb;
        _blockNumberDb = blockNumberDb;
        _headerDecoder = decoder ?? new HeaderDecoder();
        if (deferredWriter is { Enabled: true })
        {
            _pending = new DeferredWriteOverlay<BlockHeader>(deferredWriter, (_, _, header) => Insert(header));
            IStatePersistenceBarrier barrier = persistenceBarrier ?? NullStatePersistenceBarrier.Instance;
            barrier.RegisterFlush(() => _headerDb.Flush(onlyWal: true));
            barrier.RegisterFlush(() => _blockNumberDb.Flush(onlyWal: true));
        }
    }

    /// <inheritdoc/>
    public void InsertDeferred(BlockHeader header)
    {
        if (_pending is null)
        {
            Insert(header);
            return;
        }

        _pending.Publish(header.Number, header.Hash!, header.Clone());
    }

    public void Insert(BlockHeader header)
    {
        using ArrayPoolSpan<byte> rlp = _headerDecoder.EncodeToArrayPoolSpan(header);
        _headerDb.Set(header.Number, header.Hash!, rlp);
        InsertBlockNumber(header.Hash, header.Number);
    }

    public void BulkInsert(IReadOnlyList<BlockHeader> headers)
    {
        using IWriteBatch headerWriteBatch = _headerDb.StartWriteBatch();
        using IWriteBatch blockNumberWriteBatch = _blockNumberDb.StartWriteBatch();

        Span<byte> blockNumberSpan = stackalloc byte[8];
        foreach (BlockHeader header in headers)
        {
            using ArrayPoolSpan<byte> rlp = _headerDecoder.EncodeToArrayPoolSpan(header);
            headerWriteBatch.Set(header.Number, header.Hash!, rlp);

            header.Number.WriteBigEndian(blockNumberSpan);
            blockNumberWriteBatch.Set(header.Hash, blockNumberSpan);
        }
    }

    public BlockHeader? Get(Hash256 blockHash, bool shouldCache = false, ulong? blockNumber = null)
    {
        if (_pending is not null && _pending.TryGet(blockHash, out BlockHeader pending)) return pending;

        blockNumber ??= GetBlockNumberFromBlockNumberDb(blockHash);

        BlockHeader? header = null;
        if (blockNumber is not null)
        {
            header = _headerDb.Get(blockNumber.Value, blockHash, _headerDecoder, _headerCache, shouldCache: shouldCache);
        }
        return header ?? _headerDb.Get(blockHash, _headerDecoder, _headerCache, shouldCache: shouldCache);
    }

    public void Cache(BlockHeader header) => _headerCache.Set(in header.Hash.ValueHash256, header);

    public void Delete(Hash256 blockHash)
    {
        ulong? blockNumber = GetBlockNumber(blockHash);
        if (_pending is null) DeleteFromDb(blockHash, blockNumber);
        else _pending.Remove(blockHash, () => DeleteFromDb(blockHash, blockNumber));
    }

    private void DeleteFromDb(Hash256 blockHash, ulong? blockNumber)
    {
        if (blockNumber is not null) _headerDb.Delete(blockNumber.Value, blockHash);
        _blockNumberDb.Delete(blockHash);
        _headerDb.Delete(blockHash);
        _headerCache.Delete(in blockHash.ValueHash256);
    }

    public void InsertBlockNumber(Hash256 blockHash, ulong blockNumber)
    {
        Span<byte> blockNumberSpan = stackalloc byte[8];
        blockNumber.WriteBigEndian(blockNumberSpan);
        _blockNumberDb.Set(blockHash, blockNumberSpan);
    }

    public ulong? GetBlockNumber(Hash256 blockHash)
    {
        if (_pending is not null && _pending.TryGet(blockHash, out BlockHeader pending)) return pending.Number;

        ulong? blockNumber = GetBlockNumberFromBlockNumberDb(blockHash);
        if (blockNumber is not null) return blockNumber.Value;

        // Probably still hash based
        return Get(blockHash)?.Number;
    }

    private ulong? GetBlockNumberFromBlockNumberDb(Hash256 blockHash)
    {
        Span<byte> numberSpan = _blockNumberDb.GetSpan(blockHash);
        if (numberSpan.IsNullOrEmpty()) return null;
        try
        {
            if (numberSpan.Length != 8)
            {
                throw new InvalidDataException($"Unexpected number span length: {numberSpan.Length}");
            }

            return BinaryPrimitives.ReadUInt64BigEndian(numberSpan);
        }
        finally
        {
            _blockNumberDb.DangerousReleaseMemory(numberSpan);
        }
    }

    public Dictionary<ValueHash256, BlockHeader> PrefetchByNumberRange(ulong fromInclusive, ulong toExclusive) =>
        PrefetchByNumberRange(fromInclusive, toExclusive, capacity: 0);

    private Dictionary<ValueHash256, BlockHeader> PrefetchByNumberRange(ulong fromInclusive, ulong toExclusive, int capacity)
    {
        Dictionary<ValueHash256, BlockHeader> prefetched = new(capacity);
        if (toExclusive <= fromInclusive || _headerDb is not ISortedKeyValueStore sorted) return prefetched;

        Span<byte> startKey = stackalloc byte[NumberPrefixedKeyLength];
        Span<byte> endKey = stackalloc byte[NumberPrefixedKeyLength];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(fromInclusive, default, startKey);
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(toExclusive, default, endKey);

        using ISortedView view = sorted.GetViewBetween(startKey, endKey);
        while (view.MoveNext())
        {
            if (view.CurrentKey.Length != NumberPrefixedKeyLength) continue; // skip old hash-only keys

            BlockHeader header = _headerDecoder.Decode(view.CurrentValue);
            header.Hash ??= new Hash256(view.CurrentKey[sizeof(ulong)..]);
            prefetched[header.Hash.ValueHash256] = header;
        }

        return prefetched;
    }

    public IOwnedReadOnlyList<BlockHeader> FindReversedHeaders(ulong endBlockNumber, Hash256 endBlockHash, int count)
    {
        ulong startBlockNumber = (endBlockNumber + 1).SaturatingSub((ulong)count);
        Dictionary<ValueHash256, BlockHeader> prefetched = PrefetchByNumberRange(startBlockNumber, endBlockNumber + 1, count);

        BlockHeader? cursor = prefetched.TryGetValue(endBlockHash.ValueHash256, out BlockHeader? found)
            ? found
            : Get(endBlockHash, shouldCache: false, blockNumber: endBlockNumber);

        if (cursor is null) return ArrayPoolList<BlockHeader>.Empty();

        ArrayPoolList<BlockHeader> result = new(count) { cursor };
        while (result.Count < count && cursor.ParentHash is not null)
        {
            ulong parentNumber = cursor.Number - 1;
            cursor = prefetched.TryGetValue(cursor.ParentHash.ValueHash256, out BlockHeader? dictHeader)
                ? dictHeader
                : Get(cursor.ParentHash, shouldCache: false, blockNumber: parentNumber);
            if (cursor is null) break;
            result.Add(cursor);
        }

        result.AsSpan().Reverse();
        return result;
    }

    BlockHeader? IHeaderFinder.Get(Hash256 blockHash, ulong? blockNumber) => Get(blockHash, true, blockNumber);

    void IClearableCache.ClearCache() => _headerCache.Clear();
}
