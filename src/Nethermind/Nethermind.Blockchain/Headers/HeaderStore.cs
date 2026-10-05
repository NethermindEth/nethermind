// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Headers;

public class HeaderStore(
    [KeyFilter(DbNames.Headers)] IDb headerDb,
    [KeyFilter(DbNames.BlockNumbers)] IDb blockNumberDb,
    IHeaderDecoder? decoder = null)
    : IHeaderStore, IClearableCache
{
    // SyncProgressResolver MaxLookupBack is 256, add 16 wiggle room
    public const int CacheSize = 256 + 16;
    internal const int MaxCachedProofBytes = 64 * 1024;
    internal const int MaxLargeProofCacheBytes = 32 * 1024 * 1024;
    internal const int MaxLargeProofHeaders = 128;

    private const int NumberPrefixedKeyLength = sizeof(ulong) + Hash256.Size;

    private readonly IHeaderDecoder _headerDecoder = decoder ?? new HeaderDecoder();
    private readonly AssociativeCache<ValueHash256, BlockHeader> _headerCache = new(CacheSize);
    private ProofHeaderCache? _proofCache;

    private sealed class ProofHeaderCache
    {
        private readonly Lock _lock = new();
        private readonly LinkedList<BlockHeader> _lru = new();
        private readonly Dictionary<ValueHash256, System.Collections.Generic.LinkedListNode<BlockHeader>> _headers = [];
        private int _proofBytes;

        public BlockHeader? Get(ValueHash256 hash)
        {
            BlockHeader snapshot;
            lock (_lock)
            {
                if (!_headers.TryGetValue(hash, out System.Collections.Generic.LinkedListNode<BlockHeader>? node)) return null;
                _lru.Remove(node);
                _lru.AddLast(node);
                snapshot = node.Value;
            }
            return Snapshot(snapshot);
        }

        public void Set(BlockHeader header)
        {
            BlockHeader snapshot = Snapshot(header);
            lock (_lock)
            {
                DeleteCore(header.Hash!.ValueHash256);
                int bytes = snapshot.RecursiveStark!.StarkProof.Length;
                while (_headers.Count >= MaxLargeProofHeaders || bytes > MaxLargeProofCacheBytes - _proofBytes)
                    DeleteCore(_lru.First!.Value.Hash!.ValueHash256);
                _headers.Add(snapshot.Hash!.ValueHash256, _lru.AddLast(snapshot));
                _proofBytes += bytes;
            }
        }

        public void Delete(ValueHash256 hash) { lock (_lock) DeleteCore(hash); }

        private void DeleteCore(ValueHash256 hash)
        {
            if (!_headers.Remove(hash, out System.Collections.Generic.LinkedListNode<BlockHeader>? node)) return;
            _lru.Remove(node);
            _proofBytes -= node.Value.RecursiveStark!.StarkProof.Length;
        }

        public void Clear()
        {
            lock (_lock) { _headers.Clear(); _lru.Clear(); _proofBytes = 0; }
        }

        private static BlockHeader Snapshot(BlockHeader header)
        {
            BlockHeader snapshot = header.Clone();
            RecursiveStark proof = header.RecursiveStark!;
            // Neither callers nor cached headers may share mutable proof bytes.
            snapshot.RecursiveStark = new RecursiveStark((byte[])proof.StarkProof.Clone(), proof.BlockDepsHash);
            return snapshot;
        }
    }

    public void Insert(BlockHeader header)
    {
        using ArrayPoolSpan<byte> rlp = _headerDecoder.EncodeToArrayPoolSpan(header);
        headerDb.Set(header.Number, header.Hash!, rlp);
        InsertBlockNumber(header.Hash, header.Number);
    }

    public void BulkInsert(IReadOnlyList<BlockHeader> headers)
    {
        using IWriteBatch headerWriteBatch = headerDb.StartWriteBatch();
        using IWriteBatch blockNumberWriteBatch = blockNumberDb.StartWriteBatch();

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
        if (Volatile.Read(ref _proofCache)?.Get(blockHash.ValueHash256) is { } proofHeader) return proofHeader;

        blockNumber ??= GetBlockNumberFromBlockNumberDb(blockHash);

        BlockHeader? header = null;
        if (blockNumber is not null)
        {
            header = headerDb.Get(blockNumber.Value, blockHash, _headerDecoder, _headerCache, shouldCache: false);
        }
        header ??= headerDb.Get(blockHash, _headerDecoder, _headerCache, shouldCache: false);
        if (shouldCache && header is not null) Cache(header);
        return header;
    }

    public void Cache(BlockHeader header)
    {
        if (header.RecursiveStark?.StarkProof.Length is > MaxCachedProofBytes)
        {
            if (header.RecursiveStark.StarkProof.Length > Eip8288Constants.MaxProofBytes) return;
            _headerCache.Delete(in header.Hash.ValueHash256);
            ProofHeaderCache cache = Volatile.Read(ref _proofCache) ??
                Interlocked.CompareExchange(ref _proofCache, new ProofHeaderCache(), null) ?? _proofCache!;
            cache.Set(header);
            return;
        }
        Volatile.Read(ref _proofCache)?.Delete(header.Hash.ValueHash256);
        _headerCache.Set(in header.Hash.ValueHash256, header);
    }

    public void Delete(Hash256 blockHash)
    {
        ulong? blockNumber = GetBlockNumberFromBlockNumberDb(blockHash);
        if (blockNumber is not null) headerDb.Delete(blockNumber.Value, blockHash);
        blockNumberDb.Delete(blockHash);
        headerDb.Delete(blockHash);
        _headerCache.Delete(in blockHash.ValueHash256);
        Volatile.Read(ref _proofCache)?.Delete(blockHash.ValueHash256);
    }

    public void InsertBlockNumber(Hash256 blockHash, ulong blockNumber)
    {
        Span<byte> blockNumberSpan = stackalloc byte[8];
        blockNumber.WriteBigEndian(blockNumberSpan);
        blockNumberDb.Set(blockHash, blockNumberSpan);
    }

    public ulong? GetBlockNumber(Hash256 blockHash)
    {
        ulong? blockNumber = GetBlockNumberFromBlockNumberDb(blockHash);
        if (blockNumber is not null) return blockNumber.Value;

        // Probably still hash based
        return Get(blockHash)?.Number;
    }

    private ulong? GetBlockNumberFromBlockNumberDb(Hash256 blockHash)
    {
        Span<byte> numberSpan = blockNumberDb.GetSpan(blockHash);
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
            blockNumberDb.DangerousReleaseMemory(numberSpan);
        }
    }

    public Dictionary<ValueHash256, BlockHeader> PrefetchByNumberRange(ulong fromInclusive, ulong toExclusive) =>
        PrefetchByNumberRange(fromInclusive, toExclusive, capacity: 0);

    private Dictionary<ValueHash256, BlockHeader> PrefetchByNumberRange(ulong fromInclusive, ulong toExclusive, int capacity)
    {
        Dictionary<ValueHash256, BlockHeader> prefetched = new(capacity);
        if (toExclusive <= fromInclusive || headerDb is not ISortedKeyValueStore sorted) return prefetched;

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

    void IClearableCache.ClearCache()
    {
        _headerCache.Clear();
        Volatile.Read(ref _proofCache)?.Clear();
    }
}
