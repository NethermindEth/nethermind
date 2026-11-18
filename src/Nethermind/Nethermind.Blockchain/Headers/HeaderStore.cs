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
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Headers;

public class HeaderStore : IHeaderStore, IClearableCache
{
    // SyncProgressResolver MaxLookupBack is 256, add 16 wiggle room
    public const int CacheSize = 256 + 16;

    private const int NumberPrefixedKeyLength = sizeof(ulong) + Hash256.Size;
    private const int BlockNumberLength = sizeof(ulong);

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
            header = _headerDb.Get(blockNumber.Value, blockHash, _headerDecoder, (AssociativeCache<ValueHash256, BlockHeader>?)null, RlpBehaviors.ExcludeHashes, shouldCache: false);
        }
        header ??= _headerDb.Get(blockHash, _headerDecoder, (AssociativeCache<ValueHash256, BlockHeader>?)null, RlpBehaviors.ExcludeHashes, shouldCache: false);
        if (header is null) return null;

        header.Hash = blockHash;
        if (shouldCache) _headerCache.Set(in blockHash.ValueHash256, header);
        return header;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A header read from the database is copied as it is stored when <paramref name="decoder"/> is the decoder of
    /// this store, or both are a plain <see cref="HeaderDecoder"/>. A cached or pending header is encoded.
    /// </remarks>
    [SkipLocalsInit]
    public bool TryAppendRlp(in ValueHash256 blockHash, ulong blockNumber, IHeaderDecoder decoder, ArrayPoolList<byte> destination)
    {
        BlockHeader? header = _headerCache.Get(in blockHash);
        if (header is null && _pending is not null) _pending.TryGet(in blockHash, out header);
        if (header is null && EncodesAsStored(decoder))
        {
            Span<byte> key = stackalloc byte[NumberPrefixedKeyLength];
            KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, key);
            return TryAppendStored(key, destination) || TryAppendStored(blockHash.Bytes, destination);
        }

        header ??= Get(new Hash256(in blockHash), shouldCache: false, blockNumber);
        if (header is null) return false;

        int length = decoder.GetLength(header);
        RlpWriter writer = RlpWriter.ContentWriter(destination.AddUninitialized(length));
        decoder.Encode(ref writer, header);
        Rlp.CheckLength(in writer, length);
        return true;
    }

    private bool EncodesAsStored(IHeaderDecoder decoder) =>
        ReferenceEquals(decoder, _headerDecoder)
        || (decoder.GetType() == typeof(HeaderDecoder) && _headerDecoder.GetType() == typeof(HeaderDecoder));

    private bool TryAppendStored(ReadOnlySpan<byte> key, ArrayPoolList<byte> destination)
    {
        if (_headerDb is IReadOnlyNativeKeyValueStore native)
        {
            ReadOnlySpan<byte> slice = native.GetNativeSlice(key, out IntPtr handle);
            if (slice.IsNull()) return false;

            try
            {
                return TryAppendFirstItem(slice, destination);
            }
            finally
            {
                native.DangerousReleaseHandle(handle);
            }
        }

        Span<byte> data = _headerDb.GetSpan(key);
        if (data.IsNull()) return false;

        try
        {
            return TryAppendFirstItem(data, destination);
        }
        finally
        {
            _headerDb.DangerousReleaseMemory(data);
        }
    }

    private static bool TryAppendFirstItem(ReadOnlySpan<byte> stored, ArrayPoolList<byte> destination)
    {
        if (stored.Length == 0) return false;

        RlpReader reader = new(stored);
        destination.AddRange(reader.ReadEncodedItem<OffFlag>());
        return true;
    }

    /// <inheritdoc/>
    public BlockHeader? GetIfBloomMatches(Hash256 blockHash, ulong blockNumber, Func<ReadOnlySpan<byte>, bool> bloomMatches, out bool found) =>
        GetIfBloomMatches(in blockHash.ValueHash256, blockHash, blockNumber, bloomMatches, out found);

    /// <inheritdoc/>
    public BlockHeader? GetIfBloomMatches(in ValueHash256 blockHash, ulong blockNumber, Func<ReadOnlySpan<byte>, bool> bloomMatches, out bool found) =>
        GetIfBloomMatches(in blockHash, null, blockNumber, bloomMatches, out found);

    [SkipLocalsInit]
    private BlockHeader? GetIfBloomMatches(in ValueHash256 blockHash, Hash256? hash, ulong blockNumber, Func<ReadOnlySpan<byte>, bool> bloomMatches, out bool found)
    {
        BlockHeader? header = _headerCache.Get(in blockHash);
        if (header is null && _pending is not null) _pending.TryGet(in blockHash, out header);
        if (header is not null)
        {
            found = true;
            return bloomMatches(header.Bloom!.Bytes) ? header : null;
        }

        Span<byte> key = stackalloc byte[NumberPrefixedKeyLength];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumber, blockHash, key);
        header = ReadIfBloomMatches(key, in blockHash, hash, bloomMatches, out found);
        return found ? header : ReadIfBloomMatches(blockHash.Bytes, in blockHash, hash, bloomMatches, out found);
    }

    private BlockHeader? ReadIfBloomMatches(ReadOnlySpan<byte> key, in ValueHash256 blockHash, Hash256? hash, Func<ReadOnlySpan<byte>, bool> bloomMatches, out bool found)
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
                return DecodeIfBloomMatches(slice, in blockHash, hash, bloomMatches, out found);
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
            return DecodeIfBloomMatches(data, in blockHash, hash, bloomMatches, out found);
        }
        finally
        {
            _headerDb.DangerousReleaseMemory(data);
        }
    }

    private BlockHeader? DecodeIfBloomMatches(ReadOnlySpan<byte> rlp, in ValueHash256 blockHash, Hash256? hash, Func<ReadOnlySpan<byte>, bool> bloomMatches, out bool found)
    {
        BlockHeader? header = DecodeIfBloomMatches(_headerDecoder, rlp, bloomMatches, out found);
        if (header is not null) header.Hash = hash ?? new Hash256(in blockHash);
        return header;
    }

    private static BlockHeader? DecodeIfBloomMatches(IHeaderDecoder decoder, ReadOnlySpan<byte> rlp, Func<ReadOnlySpan<byte>, bool> bloomMatches, out bool found)
    {
        found = rlp.Length != 0;
        if (!found) return null;

        // A zero-length bloom is left to the decoder, which checks it against the spec.
        bool bloomTested = TryReadBloom(rlp, out ReadOnlySpan<byte> bloom);
        if (bloomTested && !bloomMatches(bloom)) return null;

        RlpReader reader = new(rlp);
        BlockHeader? header = decoder.Decode(ref reader, RlpBehaviors.AllowExtraBytes | RlpBehaviors.TrustedInput | RlpBehaviors.ExcludeHashes);
        found = header is not null;
        return found && (bloomTested || bloomMatches(header!.Bloom!.Bytes)) ? header : null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The headers neither cached nor pending are read in one batch by their number-prefixed keys; one not found by
    /// that key is then read by its hash.
    /// </remarks>
    public void GetIfBloomMatches(ReadOnlyMemory<ValueHash256> blockHashes, ReadOnlyMemory<ulong> blockNumbers, Func<ReadOnlySpan<byte>, bool> bloomMatches, Memory<BlockHeader?> headers, Memory<bool> found) =>
        GetIfBloomMatches(blockHashes.Span, blockNumbers.Span, bloomMatches, headers.Span, found.Span);

    private void GetIfBloomMatches(ReadOnlySpan<ValueHash256> blockHashes, ReadOnlySpan<ulong> blockNumbers, Func<ReadOnlySpan<byte>, bool> bloomMatches, Span<BlockHeader?> headers, Span<bool> found)
    {
        using ArrayPoolListRef<int> reads = new(blockHashes.Length);
        for (int i = 0; i < blockHashes.Length; i++)
        {
            BlockHeader? header = _headerCache.Get(in blockHashes[i]);
            if (header is null && _pending is not null) _pending.TryGet(in blockHashes[i], out header);
            if (header is null)
            {
                reads.Add(i);
                continue;
            }

            found[i] = true;
            headers[i] = bloomMatches(header.Bloom!.Bytes) ? header : null;
        }

        if (reads.Count == 0) return;

        using ArrayPoolSpan<byte> keys = new(reads.Count * NumberPrefixedKeyLength);
        for (int j = 0; j < reads.Count; j++)
        {
            int i = reads[j];
            KeyValueStoreExtensions.GetBlockNumPrefixedKey(blockNumbers[i], blockHashes[i], keys.Slice(j * NumberPrefixedKeyLength, NumberPrefixedKeyLength));
        }

        using ArrayPoolSpan<BloomMatch> matches = new(reads.Count);
        _headerDb.GetMany<BloomMatch, BloomMatchDecoder>(keys, NumberPrefixedKeyLength, matches, new BloomMatchDecoder(_headerDecoder, bloomMatches));
        for (int j = 0; j < reads.Count; j++)
        {
            int i = reads[j];
            (BlockHeader? header, bool isFound) = matches[j];
            if (!isFound)
            {
                header = ReadIfBloomMatches(blockHashes[i].Bytes, in blockHashes[i], null, bloomMatches, out isFound);
            }
            else if (header is not null)
            {
                header.Hash = new Hash256(in blockHashes[i]);
            }

            headers[i] = header;
            found[i] = isFound;
        }

        // The rented array goes back to a shared pool, which must not keep the headers alive.
        ((Span<BloomMatch>)matches).Clear();
    }

    private readonly record struct BloomMatch(BlockHeader? Header, bool Found);

    private readonly struct BloomMatchDecoder(IHeaderDecoder decoder, Func<ReadOnlySpan<byte>, bool> bloomMatches) : IValueDecoder<BloomMatch>
    {
        public BloomMatch Decode(ReadOnlySpan<byte> buffer) => new(DecodeIfBloomMatches(decoder, buffer, bloomMatches, out bool found), found);
    }

    private static bool TryReadBloom(ReadOnlySpan<byte> headerRlp, out ReadOnlySpan<byte> bloom)
    {
        RlpReader reader = new RlpReader(headerRlp).EnterSequence<OffFlag>(out _);
        // Parent hash, uncles hash, beneficiary, state root, transactions root and receipts root precede the bloom.
        for (int i = 0; i < 6; i++)
        {
            reader.SkipItem<OffFlag>();
        }

        bloom = reader.DecodeByteArraySpan<OffFlag>();
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
        return GetHashKeyedBlockNumber(blockHash);
    }

    /// <inheritdoc/>
    /// <remarks>Reads the numbers of blocks that are not pending in one batch.</remarks>
    public void GetBlockNumbers(ReadOnlySpan<ValueHash256> blockHashes, Span<ulong?> numbers)
    {
        using ArrayPoolSpan<byte> keys = new(blockHashes.Length * ValueHash256.MemorySize);
        using ArrayPoolSpan<int> stored = new(blockHashes.Length);
        int count = 0;
        for (int i = 0; i < blockHashes.Length; i++)
        {
            if (_pending is not null && _pending.TryGet(in blockHashes[i], out BlockHeader pending))
            {
                numbers[i] = pending.Number;
                continue;
            }

            blockHashes[i].Bytes.CopyTo(keys.Slice(count * ValueHash256.MemorySize, ValueHash256.MemorySize));
            stored[count++] = i;
        }

        using ArrayPoolSpan<ulong?> storedNumbers = new(count);
        _blockNumberDb.GetMany<ulong?, BlockNumberDecoder>(keys.Slice(0, count * ValueHash256.MemorySize), ValueHash256.MemorySize, storedNumbers, default);
        for (int j = 0; j < count; j++)
        {
            int i = stored[j];
            // Probably still hash based
            numbers[i] = storedNumbers[j] ?? GetHashKeyedBlockNumber(new Hash256(in blockHashes[i]));
        }
    }

    private readonly struct BlockNumberDecoder : IValueDecoder<ulong?>
    {
        public ulong? Decode(ReadOnlySpan<byte> buffer) => buffer.Length switch
        {
            0 => null,
            BlockNumberLength => BinaryPrimitives.ReadUInt64BigEndian(buffer),
            _ => throw new InvalidDataException($"Unexpected number span length: {buffer.Length}")
        };
    }

    /// <summary>Gets the number of a block whose header is cached or stored under its hash alone.</summary>
    /// <remarks>For callers that already checked the deferred-write overlay and the block number database.</remarks>
    private ulong? GetHashKeyedBlockNumber(Hash256 blockHash) =>
        _headerCache.Get(in blockHash.ValueHash256)?.Number
        ?? _headerDb.Get(blockHash, _headerDecoder, (AssociativeCache<ValueHash256, BlockHeader>?)null, RlpBehaviors.ExcludeHashes, shouldCache: false)?.Number;

    [SkipLocalsInit]
    private ulong? GetBlockNumberFromBlockNumberDb(Hash256 blockHash)
    {
        Span<byte> numberSpan = stackalloc byte[BlockNumberLength];
        int length;
        try
        {
            length = _blockNumberDb.Get(blockHash.Bytes, numberSpan);
        }
        catch (ArgumentException e)
        {
            throw new InvalidDataException($"Block number entry is longer than {BlockNumberLength} bytes", e);
        }

        if (length == 0) return null;
        if (length != BlockNumberLength)
        {
            throw new InvalidDataException($"Unexpected number span length: {length}");
        }

        return BinaryPrimitives.ReadUInt64BigEndian(numberSpan);
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

            BlockHeader header = _headerDecoder.Decode(view.CurrentValue, RlpBehaviors.TrustedInput | RlpBehaviors.ExcludeHashes);
            header.Hash = new Hash256(view.CurrentKey[sizeof(ulong)..]);
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
