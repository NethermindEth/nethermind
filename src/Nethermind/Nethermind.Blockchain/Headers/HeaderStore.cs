// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
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
    // Headers written off the engine API path, as the bodies they belong to are; null when deferral is off. The block
    // data the persistence barrier makes durable before a block's state includes them, so on a restart from persisted
    // state no header it needs is missing.
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
            _pending = new DeferredWriteOverlay<BlockHeader>(deferredWriter, WriteHeader);
            IStatePersistenceBarrier barrier = persistenceBarrier ?? NullStatePersistenceBarrier.Instance;
            barrier.RegisterFlush(() => headerDb.Flush(onlyWal: true));
            barrier.RegisterFlush(() => blockNumberDb.Flush(onlyWal: true));
        }
    }

    public void Insert(BlockHeader header) => WriteHeader(header.Number, header.Hash!, header);

    /// <inheritdoc/>
    /// <remarks>
    /// Readers find the header at once through the pending overlay; the header is encoded and written by the deferred
    /// writer. Nothing that changes its encoding is mutated after a block is suggested.
    /// </remarks>
    public void InsertDeferred(BlockHeader header)
    {
        if (_pending is null)
        {
            Insert(header);
            return;
        }

        _pending.Publish(header.Number, header.Hash!, header);
    }

    private void WriteHeader(ulong blockNumber, Hash256 blockHash, BlockHeader header)
    {
        using ArrayPoolSpan<byte> rlp = _headerDecoder.EncodeToArrayPoolSpan(header);
        _headerDb.Set(blockNumber, blockHash, rlp);
        InsertBlockNumber(blockHash, blockNumber);
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
        if (_headerCache.Get(in blockHash.ValueHash256) is { } cached) return cached;
        if (_pending is not null && _pending.TryGet(blockHash, out BlockHeader pending)) return pending;

        blockNumber ??= GetBlockNumberFromBlockNumberDb(blockHash);

        BlockHeader? header = null;
        if (blockNumber is not null)
        {
            header = _headerDb.Get(blockNumber.Value, blockHash, _headerDecoder, _headerCache, shouldCache: shouldCache);
        }
        return header ?? _headerDb.Get(blockHash, _headerDecoder, _headerCache, shouldCache: shouldCache);
    }

    /// <inheritdoc/>
    [SkipLocalsInit]
    public BlockHeader? GetIfBloomMatches(Hash256 blockHash, ulong blockNumber, Func<ReadOnlySpan<byte>, bool> bloomMatches, out bool found)
    {
        BlockHeader? header = _headerCache.Get(in blockHash.ValueHash256);
        if (header is null && _pending is not null) _pending.TryGet(blockHash, out header);
        if (header is not null)
        {
            found = true;
            return bloomMatches(header.Bloom!.Bytes) ? header : null;
        }

        Span<byte> key = stackalloc byte[NumberPrefixedKeyLength];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, key);
        header = ReadIfBloomMatches(key, bloomMatches, out found);
        return found ? header : ReadIfBloomMatches(blockHash.Bytes, bloomMatches, out found);
    }

    private BlockHeader? ReadIfBloomMatches(ReadOnlySpan<byte> key, Func<ReadOnlySpan<byte>, bool> bloomMatches, out bool found)
    {
        if (_headerDb is IReadOnlyNativeKeyValueStore native)
        {
            ReadOnlySpan<byte> slice = native.GetNativeSlice(key, out IntPtr handle);
            if (slice.IsNull())
            {
                found = false;
                return null;
            }

            try
            {
                return DecodeIfBloomMatches(slice, bloomMatches, out found);
            }
            finally
            {
                native.DangerousReleaseHandle(handle);
            }
        }

        Span<byte> data = _headerDb.GetSpan(key);
        if (data.IsNull())
        {
            found = false;
            return null;
        }

        try
        {
            return DecodeIfBloomMatches(data, bloomMatches, out found);
        }
        finally
        {
            _headerDb.DangerousReleaseMemory(data);
        }
    }

    private BlockHeader? DecodeIfBloomMatches(ReadOnlySpan<byte> rlp, Func<ReadOnlySpan<byte>, bool> bloomMatches, out bool found)
    {
        found = rlp.Length != 0;
        if (!found) return null;

        // A zero-length bloom is left to the decoder, which checks it against the spec.
        bool bloomTested = TryReadBloom(rlp, out ReadOnlySpan<byte> bloom);
        if (bloomTested && !bloomMatches(bloom)) return null;

        RlpReader reader = new(rlp);
        BlockHeader? header = _headerDecoder.Decode(ref reader, RlpBehaviors.AllowExtraBytes);
        found = header is not null;
        return found && (bloomTested || bloomMatches(header!.Bloom!.Bytes)) ? header : null;
    }

    private static bool TryReadBloom(ReadOnlySpan<byte> headerRlp, out ReadOnlySpan<byte> bloom)
    {
        RlpReader reader = new(headerRlp);
        reader.ReadSequenceLength();
        // Parent hash, uncles hash, beneficiary, state root, transactions root and receipts root precede the bloom.
        reader.SkipItems(6);
        bloom = reader.DecodeByteArraySpan();
        return bloom.Length == Bloom.ByteLength;
    }

    public void Cache(BlockHeader header) => _headerCache.Set(in header.Hash.ValueHash256, header);

    public void Delete(Hash256 blockHash)
    {
        if (_pending is not null)
        {
            // Removed with the database delete under the overlay's lock, so a queued write cannot bring it back.
            _pending.Remove(blockHash, () => DeleteFromDb(blockHash));
        }
        else
        {
            DeleteFromDb(blockHash);
        }
    }

    private void DeleteFromDb(Hash256 blockHash)
    {
        ulong? blockNumber = GetBlockNumberFromBlockNumberDb(blockHash);
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
        Dictionary<ValueHash256, BlockHeader> prefetched = [with(capacity)];
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

        ArrayPoolList<BlockHeader> result = [with(count), cursor];
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
