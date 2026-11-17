// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Blockchain.Headers;

public interface IHeaderStore : IHeaderFinder
{
    void Insert(BlockHeader header);

    /// <summary>
    /// Stores <paramref name="header"/>, visible to readers at once, with its database write queued off the caller's
    /// path where deferred block-data persistence is on; otherwise the same as <see cref="Insert"/>.
    /// </summary>
    void InsertDeferred(BlockHeader header) => Insert(header);
    void BulkInsert(IReadOnlyList<BlockHeader> headers);
    BlockHeader? Get(Hash256 blockHash, bool shouldCache, ulong? blockNumber = null);

    /// <summary>Appends the header of <paramref name="blockHash"/>, encoded by <paramref name="decoder"/>, to <paramref name="destination"/>.</summary>
    /// <returns><see langword="false"/> when the header is not stored.</returns>
    /// <remarks>An implementation can copy the stored encoding without a decode when the store encodes as <paramref name="decoder"/> does.</remarks>
    bool TryAppendRlp(in ValueHash256 blockHash, ulong blockNumber, IHeaderDecoder decoder, ArrayPoolList<byte> destination)
    {
        if (Get(new Hash256(in blockHash), shouldCache: false, blockNumber) is not { } header) return false;

        using ArrayPoolSpan<byte> rlp = decoder.EncodeToArrayPoolSpan(header);
        destination.AddRange(rlp);
        return true;
    }

    /// <summary>
    /// Gets the header of <paramref name="blockHash"/> if <paramref name="bloomMatches"/> accepts its logs bloom, which
    /// is empty for a zero-length (EIP-7668) bloom.
    /// </summary>
    /// <param name="blockHash">The hash of the block.</param>
    /// <param name="blockNumber">The number of the block.</param>
    /// <param name="bloomMatches">Tells whether the bloom is a match.</param>
    /// <param name="found">Whether the header is stored, whether or not its bloom is a match.</param>
    /// <remarks>The bloom may be tested on the stored encoding, so a header it rejects need not be decoded.</remarks>
    BlockHeader? GetIfBloomMatches(Hash256 blockHash, ulong blockNumber, Func<ReadOnlySpan<byte>, bool> bloomMatches, out bool found)
    {
        BlockHeader? header = Get(blockHash, shouldCache: false, blockNumber);
        found = header is not null;
        return found && bloomMatches(header!.Bloom!.Bytes) ? header : null;
    }

    /// <inheritdoc cref="GetIfBloomMatches(Hash256, ulong, Func{ReadOnlySpan{byte}, bool}, out bool)"/>
    BlockHeader? GetIfBloomMatches(in ValueHash256 blockHash, ulong blockNumber, Func<ReadOnlySpan<byte>, bool> bloomMatches, out bool found) =>
        GetIfBloomMatches(new Hash256(in blockHash), blockNumber, bloomMatches, out found);
    void Cache(BlockHeader header);
    void Delete(Hash256 blockHash);
    void InsertBlockNumber(Hash256 blockHash, ulong blockNumber);
    ulong? GetBlockNumber(Hash256 blockHash);

    /// <summary>Looks up the numbers of several blocks, as <see cref="GetBlockNumber"/> does for one.</summary>
    /// <param name="blockHashes">The hashes of the blocks.</param>
    /// <param name="numbers">Receives the number of each block in order, or null for an unknown block.</param>
    void GetBlockNumbers(ReadOnlySpan<ValueHash256> blockHashes, Span<ulong?> numbers)
    {
        for (int i = 0; i < blockHashes.Length; i++)
        {
            numbers[i] = GetBlockNumber(new Hash256(in blockHashes[i]));
        }
    }

    /// <summary>Starts reading stored headers into memory before the reads that need them.</summary>
    /// <remarks>Returns before the reads complete. A store that cannot read ahead does nothing.</remarks>
    void Prefetch(ReadOnlySpan<(ulong Number, ValueHash256 Hash)> blocks) { }

    /// <summary>
    /// Returns up to <paramref name="count"/> consecutive headers ending at <paramref name="endBlockHash"/>,
    /// walking backward through parent hashes. Uses a DB iterator for bulk read when the underlying
    /// store supports <see cref="ISortedKeyValueStore"/>; otherwise falls back to per-hash lookups.
    /// The returned list is ordered oldest-first. If the chain breaks, the returned list is shorter.
    /// Returns an empty list when <paramref name="endBlockHash"/> is not found.
    /// </summary>
    IOwnedReadOnlyList<BlockHeader> FindReversedHeaders(ulong endBlockNumber, Hash256 endBlockHash, int count);

    Dictionary<ValueHash256, BlockHeader> PrefetchByNumberRange(ulong fromInclusive, ulong toExclusive) => [];
}
